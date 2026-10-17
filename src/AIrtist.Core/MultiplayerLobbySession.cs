using System.Security.Cryptography;
using System.Text.Json;

namespace AIrtist.Core;

public sealed record LobbySessionContext(Guid LobbyId, string HostId, string LocalId,
    MultiplayerCompatibility Compatibility, IReadOnlyList<LobbyMember> Members);
public enum LobbySessionPhase { Waiting, Preparing, Running, Stopped }
public sealed record LobbyStateMessage(Guid LobbyId, string RecipientId, long StateVersion, LobbySnapshot State);
public sealed record LobbyReadyReply(Guid LobbyId, string RecipientId, long Sequence, bool Accepted);
public sealed record LobbyStartOffer(Guid LobbyId, string RecipientId, LobbyLaunchPlan Plan);
public sealed record LobbyStartReceipt(Guid LobbyId, Guid MatchId, string PlayerId, string PlanHash, bool Accepted);
public sealed record LobbyHeartbeat(Guid LobbyId, string PlayerId);
public sealed record LobbyStopNotice(Guid LobbyId, string PlayerId);
public sealed record LobbySessionSnapshot(LobbySessionPhase Phase, LobbySnapshot? Lobby, bool PendingReady, bool CanStart, string Message);

// One local participant, with a pinned service-authenticated roster. This session
// owns the connection through lobby AND match, so late lobby ACKs remain serviced.
// A roster change ends this synchronization epoch; an adapter must obtain a fresh
// shared LobbyId/roster before recreating it. It must not reset channels in place.
public sealed class MultiplayerLobbySession : IDisposable
{
    // Only call with the service adapter's once-only handoff, after the connection barrier.
    public static MultiplayerLobbySession FromPreparedConnection(PreparedLobbyConnection connection, MultiplayerCompatibility compatibility,
        IEnumerable<Word> words, IDrawingAi ai, MultiplayerMatchSettings? hostSettings = null, string? firstWordId = null, TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? displayNames = null)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(compatibility);
        if (connection.CompatibilityHash != compatibility.ConnectionHash() || connection.Members is null || connection.Members.Count is < 2 or > 16 ||
            connection.Transport is null || connection.Transport.LocalId != connection.LocalId || !connection.Transport.IsConnected(connection.HostId) ||
            (connection.LocalId == connection.HostId && connection.Members.Any(m => !connection.Transport.IsConnected(m.PlayerId))))
            throw new ArgumentException("전원 연결 준비와 현재 호환성을 확인한 뒤 대기방을 시작하세요.");
        var members = connection.Members.OrderBy(m => m.PlayerId, StringComparer.Ordinal)
            .Select((m, i) => new LobbyMember(new(m.PlayerId, displayNames?.GetValueOrDefault(m.PlayerId) ?? $"참가자 {i + 1}"), m.SessionId)).ToArray();
        return new(new(connection.ScopeId, connection.HostId, connection.LocalId, compatibility, members), words, ai,
            connection.Transport, hostSettings, firstWordId, clock);
    }

    private readonly object sync = new();
    private readonly LobbySessionContext context;
    private readonly MultiplayerLobbyHost? host;
    private readonly IRoundTransport transport;
    private readonly TimeProvider clock;
    private readonly IDrawingAi ai;
    private readonly Word[] words;
    private readonly string? firstWord;
    private readonly ReliableControlEndpoint client;
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private readonly Queue<RoundInboundPacket> loopback = new();
    private readonly HashSet<string> receipts = new(StringComparer.Ordinal);
    private readonly long created;
    private long lastHeartbeat, lastHostMessage, preparingStarted, readySequence, pendingReady, stateVersion, receivedVersion;
    private LobbySnapshot? view;
    private byte[]? stateBytes;
    private LobbyLaunchPlan? plan;
    private string? planHash, failure, commandError;
    private LobbySessionPhase phase;
    private MultiplayerMatchRuntime? match;
    private MatchLease? lease;
    private bool disposed, stopQueued, desiredReady;
    private sealed class Peer(ReliableControlEndpoint endpoint)
    {
        public readonly ReliableControlEndpoint Endpoint = endpoint;
        public long SentState;
        public bool Offered, Stopped;
    }
    private sealed class MatchLease(IRoundTransport parent) : IRoundTransport
    {
        private readonly Queue<RoundInboundPacket> incoming = new();
        private bool closed;
        public string LocalId => parent.LocalId;
        public bool IsConnected(string id) => !closed && parent.IsConnected(id);
        public bool TrySend(string id, RoundPacketKind kind, byte[] bytes) => !closed && parent.TrySend(id, kind, bytes);
        public void Enqueue(RoundInboundPacket packet) { if (!closed && incoming.Count < 256) incoming.Enqueue(packet); }
        public IReadOnlyList<RoundInboundPacket> Receive(int count)
        {
            var result = new List<RoundInboundPacket>();
            while (!closed && result.Count < count && incoming.Count > 0) result.Add(incoming.Dequeue());
            return result;
        }
        public void Dispose() { closed = true; incoming.Clear(); }
    }

    public MultiplayerLobbySession(LobbySessionContext context, IEnumerable<Word> words, IDrawingAi ai, IRoundTransport transport,
        MultiplayerMatchSettings? hostSettings = null, string? firstWordId = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(context); context.Compatibility.Validate();
        var members = context.Members.Take(17).OrderBy(m => m?.Player?.Id, StringComparer.Ordinal).ToArray();
        // Reuse the host's full roster validation without accepting a wire roster.
        var validated = new MultiplayerLobbyHost(context.LobbyId, context.HostId, context.Compatibility, hostSettings ?? MultiplayerMatchSettings.Default);
        validated.UpdateMembership(1, context.HostId, members);
        if (validated.Snapshot().Phase != LobbyPhase.Waiting || !members.Any(m => m.Player.Id == context.LocalId)
            || transport.LocalId != context.LocalId || (context.LocalId != context.HostId && (hostSettings is not null || firstWordId is not null)))
            throw new ArgumentException("대기방의 인증된 참가자·연결 정보를 확인해 주세요.");
        this.words = words.Select(w => w with { Aliases = (string[])w.Aliases.Clone() }).ToArray();
        if (this.words.Length == 0 || this.words.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != this.words.Length
            || (firstWordId is not null && !this.words.Any(w => w.Id == firstWordId)))
            throw new ArgumentException("단어 목록을 확인해 주세요.");
        this.context = context with { Members = Array.AsReadOnly(members) };
        this.ai = ai; this.transport = transport; this.clock = clock ?? TimeProvider.System; firstWord = firstWordId;
        created = lastHostMessage = lastHeartbeat = this.clock.GetTimestamp();
        client = new(context.LobbyId, context.LobbyId, context.HostId, DispatchClient, this.clock);
        if (context.LocalId == context.HostId)
        {
            host = validated;
            foreach (var m in members) peers.Add(m.Player.Id, new(new(context.LobbyId, context.LobbyId, m.Player.Id, DispatchHost, this.clock)));
            CaptureHostState();
        }
    }

    public MultiplayerMatchRuntime? Match { get { lock (sync) { Alive(); return match; } } }
    public void SetReady(long expectedRevision, bool ready)
    {
        lock (sync)
        {
            Alive(); RequireWaiting(expectedRevision);
            if (pendingReady != 0) throw new InvalidOperationException("준비 응답을 기다려 주세요.");
            long next = readySequence + 1;
            var self = context.Members.Single(m => m.Player.Id == context.LocalId);
            var request = new LobbyReadyRequest(context.LobbyId, expectedRevision, context.LocalId, self.SessionId, next, ready, context.Compatibility);
            if (client.Queue(request) != ControlQueueStatus.Queued) throw new InvalidOperationException("대기방 전송이 밀려 있습니다. 잠시 후 다시 시도해 주세요.");
            readySequence = pendingReady = next; desiredReady = ready; commandError = null;
        }
    }
    public void UpdateSettings(long expectedRevision, MultiplayerMatchSettings settings)
    {
        lock (sync)
        {
            Alive(); RequireWaiting(expectedRevision);
            if (host is null) throw new UnauthorizedAccessException("방장만 설정을 변경합니다.");
            host.UpdateSettings(context.LocalId, expectedRevision, settings); CaptureHostState();
        }
    }
    public void Start(long expectedRevision)
    {
        lock (sync)
        {
            Alive(); RequireWaiting(expectedRevision);
            if (host is null) throw new UnauthorizedAccessException("방장만 경기를 시작합니다.");
            if (pendingReady != 0) throw new InvalidOperationException("준비 응답을 기다려 주세요.");
            plan = host.Start(context.LocalId, expectedRevision, Guid.NewGuid()); planHash = Hash(plan);
            phase = LobbySessionPhase.Preparing; preparingStarted = clock.GetTimestamp(); receipts.Add(context.LocalId); CaptureHostState();
        }
    }
    // Called only with a trusted service roster, never a peer-provided list.
    public void ObserveMembership(string ownerId, IReadOnlyList<LobbyMember> members)
    {
        lock (sync)
        {
            Alive();
            if (ownerId != context.HostId || members is null || !context.Members.SequenceEqual(members.OrderBy(m => m?.Player?.Id, StringComparer.Ordinal)))
                Stop("참가자 또는 방장이 변경되었습니다. 새 대기방에서 다시 준비해 주세요.");
        }
    }
    public void Tick()
    {
        lock (sync)
        {
            Alive();
            if (phase != LobbySessionPhase.Stopped && (!transport.IsConnected(context.HostId)
                || (host is not null && context.Members.Any(m => !transport.IsConnected(m.Player.Id))))) Stop("참가자 연결이 끊겨 대기방·경기를 중단했습니다.");
            foreach (var packet in transport.Receive(256)) Receive(packet);
            int localCount = loopback.Count; for (int i = 0; i < localCount; i++) Receive(loopback.Dequeue());
            if (phase != LobbySessionPhase.Stopped)
            {
                CaptureHostState(); ResolveReady();
                if (host is not null && phase == LobbySessionPhase.Preparing && receipts.Count == context.Members.Count) OpenMatch();
                if (phase == LobbySessionPhase.Preparing && clock.GetElapsedTime(preparingStarted) >= TimeSpan.FromSeconds(30)) Stop("경기 시작 정보 확인 시간이 지났습니다.");
                if (view is null && clock.GetElapsedTime(created) >= TimeSpan.FromSeconds(15)) Stop("대기방 설정을 받지 못했습니다.");
                if (phase != LobbySessionPhase.Running && clock.GetElapsedTime(lastHeartbeat) >= TimeSpan.FromSeconds(2)
                    && client.Queue(new LobbyHeartbeat(context.LobbyId, context.LocalId)) == ControlQueueStatus.Queued) lastHeartbeat = clock.GetTimestamp();
                if (host is null && phase != LobbySessionPhase.Running && clock.GetElapsedTime(lastHostMessage) >= TimeSpan.FromSeconds(15)) Stop("방장 응답이 없어 대기방을 중단했습니다.");
            }
            Publish();
            foreach (var p in peers)
            {
                foreach (var bytes in p.Value.Endpoint.PollPackets()) Send(p.Key, RoundPacketKind.LobbyControlToClient, bytes);
                if (p.Value.Endpoint.Snapshot().Failure != ControlChannelFailure.None) Stop("대기방 전송이 중단되었습니다.");
            }
            foreach (var bytes in client.PollPackets()) Send(context.HostId, RoundPacketKind.LobbyControlToHost, bytes);
            if (client.Snapshot().Failure != ControlChannelFailure.None) Stop("대기방 전송이 중단되었습니다.");
            if (phase != LobbySessionPhase.Stopped && match is not null)
            {
                match.Tick();
                if (match.Snapshot().Round is not null) phase = LobbySessionPhase.Running;
            }
        }
    }
    private void CaptureHostState()
    {
        if (host is null) return;
        var current = host.Snapshot(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(current);
        if (stateBytes is not null && bytes.AsSpan().SequenceEqual(stateBytes)) return;
        stateBytes = bytes; view = current; stateVersion++;
    }
    private void ResolveReady()
    {
        if (pendingReady != 0 && view?.Members.Single(m => m.Member.Player.Id == context.LocalId).ReadySequence >= pendingReady) pendingReady = 0;
    }
    private void Publish()
    {
        if (host is null)
        {
            if (phase == LobbySessionPhase.Stopped && !stopQueued && client.Queue(new LobbyStopNotice(context.LobbyId, context.LocalId)) == ControlQueueStatus.Queued) stopQueued = true;
            return;
        }
        foreach (var p in peers)
        {
            var peer = p.Value;
            if (phase == LobbySessionPhase.Stopped)
            {
                if (!peer.Stopped && peer.Endpoint.Queue(new LobbyStopNotice(context.LobbyId, context.HostId)) == ControlQueueStatus.Queued) peer.Stopped = true;
                continue;
            }
            if (peer.SentState < stateVersion && peer.Endpoint.Queue(new LobbyStateMessage(context.LobbyId, p.Key, stateVersion, view!)) == ControlQueueStatus.Queued) peer.SentState = stateVersion;
            if (plan is not null && p.Key != context.HostId && peer.SentState == stateVersion && !peer.Offered
                && peer.Endpoint.Queue(new LobbyStartOffer(context.LobbyId, p.Key, plan)) == ControlQueueStatus.Queued) peer.Offered = true;
        }
    }
    private ControlDispatchResult DispatchHost(string sender, byte[] bytes)
    {
        object value; try { value = RoundControlMessages.Decode(bytes); } catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
        if (value is LobbyHeartbeat ping && ping.LobbyId == context.LobbyId && ping.PlayerId == sender)
            return new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(new LobbyHeartbeat(context.LobbyId, context.HostId)));
        if (value is LobbyStopNotice stop && stop.LobbyId == context.LobbyId && stop.PlayerId == sender)
        { Stop("참가자의 연결 처리가 중단되었습니다."); return new(RoundSyncStatus.Accepted); }
        if (value is LobbyReadyRequest ready)
        {
            bool accepted = false;
            try { if (phase == LobbySessionPhase.Waiting) { host!.SetReady(sender, ready); accepted = true; } }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { }
            CaptureHostState();
            return new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(new LobbyReadyReply(context.LobbyId, sender, ready.Sequence, accepted)));
        }
        if (value is LobbyStartReceipt receipt && plan is not null && receipt.LobbyId == context.LobbyId && receipt.MatchId == plan.MatchId && receipt.PlayerId == sender)
        {
            if (phase == LobbySessionPhase.Stopped) return new(RoundSyncStatus.Closed);
            if (!receipt.Accepted || receipt.PlanHash != planHash) { Stop("참가자의 경기 시작 정보가 일치하지 않습니다."); return new(RoundSyncStatus.Conflict); }
            receipts.Add(sender); return new(RoundSyncStatus.Accepted);
        }
        return new(RoundSyncStatus.InvalidMessage);
    }
    private ControlDispatchResult DispatchClient(string sender, byte[] bytes)
    {
        if (sender != context.HostId) return new(RoundSyncStatus.WrongSender);
        object value; try { value = RoundControlMessages.Decode(bytes); } catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
        if (value is LobbyHeartbeat pong && pong.LobbyId == context.LobbyId && pong.PlayerId == sender)
        { lastHostMessage = clock.GetTimestamp(); return new(RoundSyncStatus.Accepted); }
        if (value is LobbyStopNotice stop && stop.LobbyId == context.LobbyId && stop.PlayerId == sender)
        { Stop("방장이 대기방·경기를 중단했습니다."); return new(RoundSyncStatus.Accepted); }
        if (phase == LobbySessionPhase.Stopped) return new(RoundSyncStatus.Closed);
        if (value is LobbyReadyReply reply && reply.LobbyId == context.LobbyId && reply.RecipientId == context.LocalId && reply.Sequence == pendingReady)
        {
            if (!reply.Accepted) { pendingReady = 0; commandError = "준비 요청이 거절되었습니다. 최신 설정을 확인하고 다시 준비해 주세요."; }
            return new(RoundSyncStatus.Accepted);
        }
        if (value is LobbyStateMessage state) return new(AcceptState(state));
        if (value is LobbyStartOffer offer && offer.LobbyId == context.LobbyId && offer.RecipientId == context.LocalId && offer.Plan is not null)
        {
            bool accepted = AcceptOffer(offer.Plan);
            return new(accepted ? RoundSyncStatus.Accepted : RoundSyncStatus.Conflict,
                RoundControlMessages.Encode(new LobbyStartReceipt(context.LobbyId, offer.Plan.MatchId, context.LocalId, Hash(offer.Plan), accepted)));
        }
        return new(RoundSyncStatus.InvalidMessage);
    }
    private RoundSyncStatus AcceptState(LobbyStateMessage message)
    {
        if (message.LobbyId != context.LobbyId || message.RecipientId != context.LocalId) return RoundSyncStatus.WrongRecipient;
        var s = message.State;
        if (s is null || s.LobbyId != context.LobbyId || s.HostId != context.HostId || s.Revision < 1 || message.StateVersion < 1
            || s.Settings is null || !s.Settings.IsValid() || s.Message is null || s.Message.Length > 256
            || !Enum.IsDefined(s.Phase) || s.Members is null || s.Members.Count != context.Members.Count
            || s.Members.Any(m => m?.Member is null || !Enum.IsDefined(m.Issue) || m.ReadySequence < 0 || (m.Ready && (m.Issue != LobbyCompatibilityIssue.None || m.ReadySequence == 0)))
            || !s.Members.Select(m => m.Member).SequenceEqual(context.Members)
            || s.CanStart != (s.Phase == LobbyPhase.Waiting && s.Members.Count >= 2 && s.Members.All(m => m.Ready))) return RoundSyncStatus.InvalidMessage;
        if (host is not null) return RoundSyncStatus.Duplicate;
        if (plan is not null && s.Phase != LobbyPhase.Closed && (s.Phase != LobbyPhase.Started || s.Revision != plan.Revision || s.Settings != plan.Settings))
            return RoundSyncStatus.Conflict;
        if (message.StateVersion < receivedVersion || (view is not null && s.Revision < view.Revision)) return RoundSyncStatus.Stale;
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(s);
        if (message.StateVersion == receivedVersion) return new ReadOnlySpan<byte>(stateBytes).SequenceEqual(encoded) ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict;
        if (view is not null && (s.Phase < view.Phase || (s.Revision == view.Revision && s.Settings != view.Settings)
            || s.Members.Zip(view.Members).Any(p => p.First.ReadySequence < p.Second.ReadySequence))) return RoundSyncStatus.Conflict;
        view = s with { Members = Array.AsReadOnly(s.Members.ToArray()) }; receivedVersion = message.StateVersion; stateBytes = encoded;
        if (s.Phase == LobbyPhase.Closed) Stop("대기방이 닫혔습니다.");
        ResolveReady(); return RoundSyncStatus.Accepted;
    }
    private bool AcceptOffer(LobbyLaunchPlan offered)
    {
        if (host is not null) return false;
        if (plan is not null) return Hash(offered) == planHash;
        if (phase != LobbySessionPhase.Waiting || pendingReady != 0 || !desiredReady || readySequence == 0 || view is null || view.Phase != LobbyPhase.Started
            || view.Members.Single(m => m.Member.Player.Id == context.LocalId).ReadySequence != readySequence
            || !view.Members.All(m => m.Ready && m.Issue == LobbyCompatibilityIssue.None)
            || offered.LobbyId != context.LobbyId || offered.HostId != context.HostId || offered.Revision != view.Revision
            || offered.MatchId == Guid.Empty || offered.MatchId == context.LobbyId || offered.Settings != view.Settings
            || !context.Compatibility.Matches(offered.Compatibility) || offered.Participants is null
            || offered.Participants.Count != context.Members.Count || offered.Participants.Any(p => p?.Player is null || !context.Compatibility.Matches(p.Compatibility))
            || !offered.Participants.Select(p => p.Player).SequenceEqual(context.Members.Select(m => m.Player)))
        { Stop("경기 시작 정보나 준비 상태가 일치하지 않습니다."); return false; }
        plan = offered with { Participants = Array.AsReadOnly(offered.Participants.ToArray()) }; planHash = Hash(plan);
        preparingStarted = clock.GetTimestamp(); OpenMatch(); return true;
    }
    private void OpenMatch()
    {
        if (match is not null) return;
        lease = new(transport);
        match = new(plan!.ContextFor(context.LocalId), words, ai, lease, host is null ? null : plan.Settings, firstWord, clock);
        phase = LobbySessionPhase.Preparing;
    }
    private void Receive(RoundInboundPacket packet)
    {
        if (packet.Bytes is null || packet.Bytes.Length is 0 or > 1024 || !context.Members.Any(m => m.Player.Id == packet.AuthenticatedSender)) return;
        if (packet.Kind == RoundPacketKind.LobbyControlToHost && peers.TryGetValue(packet.AuthenticatedSender, out var peer)) peer.Endpoint.Receive(packet.AuthenticatedSender, packet.Bytes);
        else if (packet.Kind == RoundPacketKind.LobbyControlToClient && packet.AuthenticatedSender == context.HostId) client.Receive(packet.AuthenticatedSender, packet.Bytes);
        else if (packet.Kind is RoundPacketKind.ControlToHost or RoundPacketKind.ControlToClient or RoundPacketKind.ImageToHost or RoundPacketKind.ImageToClient
            or RoundPacketKind.MatchControlToHost or RoundPacketKind.MatchControlToClient) lease?.Enqueue(packet);
    }
    private void Send(string id, RoundPacketKind kind, byte[] bytes)
    {
        if (id == context.LocalId) { if (loopback.Count < 256) loopback.Enqueue(new(id, kind, bytes)); }
        else transport.TrySend(id, kind, bytes);
    }
    public void Abort() { lock (sync) { Alive(); Stop("대기방·경기를 중단했습니다."); } }
    private void Stop(string reason)
    {
        if (phase == LobbySessionPhase.Stopped) return;
        phase = LobbySessionPhase.Stopped; failure = reason; pendingReady = 0; match?.Abort(); lease?.Dispose();
    }
    private void RequireWaiting(long expected)
    {
        if (phase != LobbySessionPhase.Waiting || view is null || view.Phase != LobbyPhase.Waiting || view.Revision != expected)
            throw new InvalidOperationException("현재 대기방 설정을 확인해 주세요.");
    }
    public LobbySessionSnapshot Snapshot()
    {
        lock (sync)
        {
            Alive();
            return new(phase, view, pendingReady != 0, host is not null && phase == LobbySessionPhase.Waiting && pendingReady == 0 && view?.CanStart == true,
                failure ?? commandError ?? (pendingReady != 0 ? "방장의 준비 확인을 기다립니다." : phase == LobbySessionPhase.Preparing ? "모두 같은 경기 시작 정보를 확인할 때까지 기다립니다." : view?.Message ?? "대기방 설정을 받고 있습니다."));
        }
    }
    public static string Hash(LobbyLaunchPlan value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private void Alive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true; match?.Dispose(); match = null; lease?.Dispose();
            client.Dispose(); foreach (var p in peers.Values) p.Endpoint.Dispose(); transport.Dispose(); loopback.Clear();
        }
    }
}
