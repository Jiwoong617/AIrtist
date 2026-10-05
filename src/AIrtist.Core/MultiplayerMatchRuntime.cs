using System.Security.Cryptography;
using System.Text.Json;

namespace AIrtist.Core;

public sealed record MatchRuntimeContext(Guid MatchId, string HostId, string LocalId,
    MultiplayerCompatibility Compatibility, IReadOnlyList<MultiplayerParticipant> Participants, MultiplayerMatchSettings? AgreedSettings = null);
public sealed record MultiplayerMatchSettings(int RoundCount, MultiplayerRoundPolicy RoundPolicy, TimeSpan ResultConfirmationTimeout)
{
    public static MultiplayerMatchSettings Default => new(3, MultiplayerRoundPolicy.Default, TimeSpan.FromSeconds(30));
    public bool IsValid()
    {
        static bool Time(TimeSpan value, int seconds) => value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromSeconds(seconds);
        var p = RoundPolicy;
        return RoundCount is >= 1 and <= 10 && Time(ResultConfirmationTimeout, 120) && p is not null
            && Time(p.WritingDuration, 600) && Time(p.GenerationDuration, 600) && Time(p.TransferDuration, 120)
            && Time(p.GalleryDuration, 300) && p.Voting is not null && Time(p.Voting.Duration, 600);
    }
}
public enum MultiplayerMatchPhase { AwaitingStart, Playing, BetweenRounds, Completed, Stopped }
public enum MatchStopReason { None, ParticipantDisconnected, ResultMismatch, RoundFailed, ConfirmationTimedOut, ControlFailed }
public sealed record MatchBeginRound(Guid MatchId, string RecipientId, Guid RoundId, int RoundNumber,
    MultiplayerMatchSettings Settings, string WordId, string? PreviousResultHash,
    MultiplayerCompatibility Compatibility, IReadOnlyList<VotingPlayer> Players);
public sealed record MatchRoundFinished(Guid MatchId, Guid RoundId, int RoundNumber, string PlayerId, string ResultHash);
public sealed record MatchHeartbeat(Guid MatchId, string PlayerId);
public sealed record MatchProgress(Guid MatchId, Guid RoundId, int RoundNumber, string RecipientId, long Revision,
    MultiplayerMatchPhase Phase, IReadOnlyList<string> ReadyPlayerIds, MatchStopReason StopReason);
public sealed record MatchRoundRecord(int RoundNumber, string WordId, VotingResult Result);
public sealed record MatchRuntimeSnapshot(Guid MatchId, Guid RoundId, int RoundNumber, int RoundCount, bool IsHost,
    MultiplayerMatchPhase Phase, bool CanAdvance, IReadOnlyList<string> ReadyPlayerIds,
    IReadOnlyList<MatchRoundRecord> Results, RoundRuntimeSnapshot? Round, string Message);

// One authenticated participant for an entire match. Match control lanes persist
// across round replacement; per-round control/image queues and AI are disposed.
// The reserved match-control scope uses MatchId as its channel ID. Actual round
// IDs are fresh and never equal it. IRoundTransport is owned by this object.
public sealed class MultiplayerMatchRuntime : IDisposable
{
    private readonly object sync = new();
    private readonly MatchRuntimeContext context;
    private readonly Word[] words;
    private readonly IDrawingAi ai;
    private readonly IRoundTransport transport;
    private readonly TimeProvider clock;
    private readonly ReliableControlEndpoint client;
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private readonly Queue<RoundInboundPacket> loopback = new();
    private readonly Dictionary<string, string> confirmations = new(StringComparer.Ordinal);
    private readonly List<MatchRoundRecord> results = new();
    private readonly HashSet<Guid> seenRounds = new();
    private string[] wordOrder = [], ready = [];
    private MatchBeginRound? start;
    private MultiplayerRoundRuntime? round;
    private RoundLease? lease;
    private RoundRuntimeSnapshot? stoppedRound;
    private MultiplayerMatchPhase phase;
    private MatchStopReason stopReason;
    private long revision, receivedRevision, confirmationStarted, lastHeartbeat, lastHostMessage;
    private bool confirmationTimer, finishQueued, disposed;
    private readonly bool isHost;
    private sealed class Peer(ReliableControlEndpoint endpoint)
    {
        public readonly ReliableControlEndpoint Endpoint = endpoint;
        public int SentRound;
        public long SentRevision;
    }
    // A round may clear its receive queue without closing the persistent peer.
    private sealed class RoundLease(IRoundTransport parent) : IRoundTransport
    {
        private readonly Queue<RoundInboundPacket> incoming = new();
        private bool closed;
        public string LocalId => parent.LocalId;
        public bool IsConnected(string id) => !closed && parent.IsConnected(id);
        public bool TrySend(string id, RoundPacketKind kind, byte[] bytes) => !closed && parent.TrySend(id, kind, bytes);
        public void Enqueue(RoundInboundPacket packet) { if (!closed && incoming.Count < LocalRoundNetwork.MaxQueuedPackets) incoming.Enqueue(packet); }
        public IReadOnlyList<RoundInboundPacket> Receive(int count)
        {
            var packets = new List<RoundInboundPacket>();
            while (!closed && incoming.Count > 0 && packets.Count < count) packets.Add(incoming.Dequeue());
            return packets;
        }
        public void Dispose() { closed = true; incoming.Clear(); }
    }
    public MultiplayerMatchRuntime(MatchRuntimeContext context, IEnumerable<Word> words, IDrawingAi ai, IRoundTransport transport,
        MultiplayerMatchSettings? hostSettings = null, string? firstWordId = null, TimeProvider? clock = null)
    {
        context.Compatibility.Validate();
        var members = context.Participants.Take(17).ToArray();
        if (context.MatchId == Guid.Empty || transport.LocalId != context.LocalId || members.Length is < 2 or > 16
            || members.Any(p => p?.Player is null || string.IsNullOrWhiteSpace(p.Player.Name) || p.Player.Name.Length > 40 || !context.Compatibility.Matches(p.Compatibility))
            || members.Select(p => p.Player.Id).Distinct(StringComparer.Ordinal).Count() != members.Length
            || !members.Any(p => p.Player.Id == context.HostId) || !members.Any(p => p.Player.Id == context.LocalId))
            throw new ArgumentException("경기 참가자·연결 정보를 확인해 주세요.");
        foreach (var p in members) VotingRoundHost.ValidateId(p.Player.Id);
        this.words = words.Select(w => w with { Aliases = (string[])w.Aliases.Clone() }).ToArray();
        if (this.words.Length == 0 || this.words.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != this.words.Length)
            throw new ArgumentException("단어 목록을 확인해 주세요.");
        this.context = context with { Participants = Array.AsReadOnly(members) };
        this.ai = ai; this.transport = transport; this.clock = clock ?? TimeProvider.System; isHost = context.HostId == context.LocalId;
        if (context.AgreedSettings is { } agreed && (!agreed.IsValid() || (isHost && agreed != (hostSettings ?? MultiplayerMatchSettings.Default))))
            throw new ArgumentException("대기방에서 확인한 경기 설정이 다릅니다.");
        lastHeartbeat = lastHostMessage = this.clock.GetTimestamp();
        if (!isHost && (hostSettings is not null || firstWordId is not null)) throw new ArgumentException("방장만 경기 설정을 선택합니다.");
        if (isHost && (!(hostSettings ?? MultiplayerMatchSettings.Default).IsValid() || (firstWordId is not null && !this.words.Any(w => w.Id == firstWordId))))
            throw new ArgumentException("라운드 수·제시어·제한 시간을 확인해 주세요.");
        client = new(context.MatchId, context.MatchId, context.HostId, DispatchClient, this.clock);
        if (isHost)
        {
            foreach (var p in members) peers.Add(p.Player.Id, new(new(context.MatchId, context.MatchId, p.Player.Id, DispatchHost, this.clock)));
            var settings = hostSettings ?? MultiplayerMatchSettings.Default;
            wordOrder = DrawWords(this.words, settings.RoundCount, firstWordId);
            BeginHostRound(settings);
        }
    }
    private static string[] DrawWords(Word[] words, int count, string? first)
    {
        var schedule = new List<string>();
        while (schedule.Count < count)
        {
            var bag = words.Select(w => w.Id).ToArray(); Random.Shared.Shuffle(bag);
            if (schedule.Count == 0 && first is not null)
            { int index = Array.IndexOf(bag, first); (bag[0], bag[index]) = (bag[index], bag[0]); }
            else if (schedule.Count > 0 && bag.Length > 1 && bag[0] == schedule[^1]) (bag[0], bag[1]) = (bag[1], bag[0]);
            schedule.AddRange(bag.Take(count - schedule.Count));
        }
        return schedule.ToArray();
    }
    public void AdvanceRound(Guid expectedRoundId)
    {
        lock (sync)
        {
            CheckAlive();
            if (!isHost) throw new UnauthorizedAccessException("방장만 다음 라운드를 시작할 수 있습니다.");
            if (start?.RoundId != expectedRoundId || phase != MultiplayerMatchPhase.BetweenRounds)
                throw new InvalidOperationException("모두 결과를 확인한 뒤 현재 라운드에서 진행해 주세요.");
            BeginHostRound(start.Settings);
        }
    }
    private void BeginHostRound(MultiplayerMatchSettings settings)
    {
        Guid id; do { id = Guid.NewGuid(); } while (id == context.MatchId || seenRounds.Contains(id));
        int number = results.Count + 1;
        Activate(new(context.MatchId, context.LocalId, id, number, settings, wordOrder[number - 1],
            results.Count == 0 ? null : ResultHash(results[^1].Result), context.Compatibility, context.Participants.Select(p => p.Player).ToArray()));
        revision++;
    }
    private void Activate(MatchBeginRound message)
    {
        round?.Dispose(); lease = new(transport); stoppedRound = null;
        start = message with { Players = Array.AsReadOnly(message.Players.ToArray()) };
        seenRounds.Add(start.RoundId); confirmations.Clear(); ready = []; confirmationTimer = finishQueued = false;
        phase = MultiplayerMatchPhase.Playing; stopReason = MatchStopReason.None;
        round = new(new(context.MatchId, start.RoundId, 1, context.HostId, context.LocalId, context.Compatibility, context.Participants),
            words, ai, lease, isHost ? start.WordId : null, start.Settings.RoundPolicy, clock);
    }
    public void PrepareTranslation(Guid expected, string description) { lock (sync) Active(expected).PrepareTranslation(description); }
    public void Revise(Guid expected, long draftId) { lock (sync) Active(expected).Revise(draftId); }
    public void Confirm(Guid expected, long draftId, string translation, int seed) { lock (sync) Active(expected).Confirm(draftId, translation, seed); }
    public void Vote(Guid expected, string artwork) { lock (sync) Active(expected).Vote(artwork); }
    public void CloseVoting(Guid expected) { lock (sync) Active(expected).CloseVoting(); }
    public void CancelAi(Guid expected) { lock (sync) Active(expected).CancelAi(); }
    public void Abort() { lock (sync) { CheckAlive(); Stop(MatchStopReason.RoundFailed); } }
    private MultiplayerRoundRuntime Active(Guid expected)
    {
        CheckAlive();
        if (phase != MultiplayerMatchPhase.Playing || start?.RoundId != expected || round is null)
            throw new InvalidOperationException("현재 진행 중인 라운드에서 요청해 주세요.");
        return round;
    }
    public void Tick()
    {
        lock (sync)
        {
            CheckAlive();
            for (int i = 0; i < 128 && loopback.Count > 0; i++) Receive(loopback.Dequeue());
            foreach (var packet in transport.Receive(128)) Receive(packet);
            if (phase is not (MultiplayerMatchPhase.Stopped or MultiplayerMatchPhase.Completed))
            {
                if (!transport.IsConnected(context.HostId) || (isHost && peers.Keys.Any(p => !transport.IsConnected(p)))) Stop(MatchStopReason.ParticipantDisconnected);
                else if (client.Snapshot().Failure != ControlChannelFailure.None || (isHost && peers.Values.Any(p => p.Endpoint.Snapshot().Failure != ControlChannelFailure.None))) Stop(MatchStopReason.ControlFailed);
                else if (!isHost && clock.GetElapsedTime(lastHostMessage) >= TimeSpan.FromSeconds(15)) Stop(MatchStopReason.ControlFailed);
            }
            if (round is not null && phase != MultiplayerMatchPhase.Stopped)
            {
                round.Tick(); var snapshot = round.Snapshot();
                if (snapshot.Gallery.Phase is RoundClientPhase.Failed or RoundClientPhase.Disconnected) Stop(MatchStopReason.RoundFailed);
                else if (snapshot.Gallery.Result is { } result)
                {
                    if (results.Count < start!.RoundNumber)
                        results.Add(new(start.RoundNumber, start.WordId, Freeze(result)));
                    if (!confirmationTimer) { confirmationTimer = true; confirmationStarted = clock.GetTimestamp(); }
                    if (!finishQueued && !snapshot.Busy)
                        finishQueued = client.Queue(new MatchRoundFinished(context.MatchId, start.RoundId, start.RoundNumber, context.LocalId, ResultHash(result))) == ControlQueueStatus.Queued;
                    if (isHost) CheckConfirmations(result);
                    else if (phase == MultiplayerMatchPhase.Playing && clock.GetElapsedTime(confirmationStarted) >= start.Settings.ResultConfirmationTimeout)
                        Stop(MatchStopReason.ConfirmationTimedOut);
                }
            }
            if (isHost) Publish();
            if (phase is not (MultiplayerMatchPhase.Completed or MultiplayerMatchPhase.Stopped) && clock.GetElapsedTime(lastHeartbeat) >= TimeSpan.FromSeconds(2)
                && client.Queue(new MatchHeartbeat(context.MatchId, context.LocalId)) == ControlQueueStatus.Queued) lastHeartbeat = clock.GetTimestamp();
            foreach (var packet in client.PollPackets()) Send(context.HostId, RoundPacketKind.MatchControlToHost, packet);
            foreach (var p in peers) foreach (var packet in p.Value.Endpoint.PollPackets()) Send(p.Key, RoundPacketKind.MatchControlToClient, packet);
        }
    }
    private void CheckConfirmations(VotingResult result)
    {
        if (phase is MultiplayerMatchPhase.Stopped or MultiplayerMatchPhase.Completed) return;
        if (!confirmationTimer) { confirmationTimer = true; confirmationStarted = clock.GetTimestamp(); }
        string hash = ResultHash(result);
        if (confirmations.Values.Any(v => v != hash)) { Stop(MatchStopReason.ResultMismatch); return; }
        string[] next = context.Participants.Select(p => p.Player.Id).Where(confirmations.ContainsKey).ToArray();
        if (!ready.SequenceEqual(next)) { ready = next; revision++; }
        if (ready.Length == context.Participants.Count && phase == MultiplayerMatchPhase.Playing)
        { phase = start!.RoundNumber == start.Settings.RoundCount ? MultiplayerMatchPhase.Completed : MultiplayerMatchPhase.BetweenRounds; revision++; }
        else if (phase == MultiplayerMatchPhase.Playing && clock.GetElapsedTime(confirmationStarted) >= start!.Settings.ResultConfirmationTimeout)
            Stop(MatchStopReason.ConfirmationTimedOut);
    }
    private void Publish()
    {
        if (start is null) return;
        foreach (var pair in peers)
        {
            var p = pair.Value;
            if (p.SentRound < start.RoundNumber && p.Endpoint.Queue(start with { RecipientId = pair.Key }) == ControlQueueStatus.Queued) p.SentRound = start.RoundNumber;
            if (p.SentRound == start.RoundNumber && p.SentRevision < revision
                && p.Endpoint.Queue(new MatchProgress(context.MatchId, start.RoundId, start.RoundNumber, pair.Key, revision, phase, ready, stopReason)) == ControlQueueStatus.Queued)
                p.SentRevision = revision;
        }
    }
    private ControlDispatchResult DispatchHost(string sender, byte[] bytes)
    {
        object value; try { value = RoundControlMessages.Decode(bytes); } catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
        if (value is MatchHeartbeat ping)
            return ping.MatchId == context.MatchId && ping.PlayerId == sender && peers.ContainsKey(sender)
                ? new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(new MatchHeartbeat(context.MatchId, context.HostId))) : new(RoundSyncStatus.WrongSender);
        if (value is not MatchRoundFinished message || start is null) return new(RoundSyncStatus.InvalidMessage);
        if (message.MatchId != context.MatchId || message.RoundId != start.RoundId || message.RoundNumber != start.RoundNumber) return new(RoundSyncStatus.WrongRound);
        if (message.PlayerId != sender || !peers.ContainsKey(sender)) return new(RoundSyncStatus.WrongSender);
        if (!IsHash(message.ResultHash)) return new(RoundSyncStatus.InvalidMessage);
        if (phase == MultiplayerMatchPhase.Stopped) return new(RoundSyncStatus.Closed);
        if (confirmations.TryGetValue(sender, out var old)) return new(old == message.ResultHash ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict);
        confirmations.Add(sender, message.ResultHash); return new(RoundSyncStatus.Accepted);
    }
    private ControlDispatchResult DispatchClient(string sender, byte[] bytes)
    {
        if (sender != context.HostId) return new(RoundSyncStatus.WrongSender);
        object value; try { value = RoundControlMessages.Decode(bytes); } catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
        if (value is MatchHeartbeat pong)
        {
            if (pong.MatchId != context.MatchId || pong.PlayerId != sender) return new(RoundSyncStatus.WrongSender);
            lastHostMessage = clock.GetTimestamp(); return new(RoundSyncStatus.Accepted);
        }
        if (value is MatchBeginRound begin) return new(AcceptBegin(begin));
        if (value is not MatchProgress progress || start is null) return new(RoundSyncStatus.InvalidMessage);
        if (progress.MatchId != context.MatchId || progress.RoundId != start.RoundId || progress.RoundNumber != start.RoundNumber) return new(RoundSyncStatus.WrongRound);
        if (progress.RecipientId != context.LocalId) return new(RoundSyncStatus.WrongRecipient);
        if (progress.Revision < 1 || !Enum.IsDefined(progress.Phase) || progress.Phase == MultiplayerMatchPhase.AwaitingStart || !Enum.IsDefined(progress.StopReason)
            || (progress.Phase == MultiplayerMatchPhase.Stopped) != (progress.StopReason != MatchStopReason.None)
            || progress.ReadyPlayerIds is null || progress.ReadyPlayerIds.Count > context.Participants.Count
            || !context.Participants.Select(p => p.Player.Id).Where(progress.ReadyPlayerIds.Contains).SequenceEqual(progress.ReadyPlayerIds)) return new(RoundSyncStatus.InvalidMessage);
        if (progress.Revision < receivedRevision) return new(RoundSyncStatus.Stale);
        if (isHost) return new(RoundSyncStatus.Duplicate); // Own authoritative state is newer than loopback messages.
        if (phase == MultiplayerMatchPhase.Stopped || (phase == MultiplayerMatchPhase.Completed && progress.Phase != phase)) return new(RoundSyncStatus.Closed);
        if (progress.Phase is MultiplayerMatchPhase.BetweenRounds or MultiplayerMatchPhase.Completed
            && (results.Count != start.RoundNumber || progress.ReadyPlayerIds.Count != context.Participants.Count
                || (progress.Phase == MultiplayerMatchPhase.Completed) != (start.RoundNumber == start.Settings.RoundCount))) return new(RoundSyncStatus.InvalidMessage);
        if (progress.Revision == receivedRevision)
            return new(progress.Phase == phase && progress.ReadyPlayerIds.SequenceEqual(ready) && progress.StopReason == stopReason ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict);
        if (progress.Phase < phase || ready.Any(id => !progress.ReadyPlayerIds.Contains(id))) return new(RoundSyncStatus.Conflict);
        receivedRevision = progress.Revision; ready = progress.ReadyPlayerIds.ToArray();
        if (progress.Phase == MultiplayerMatchPhase.Stopped) Stop(progress.StopReason); else phase = progress.Phase;
        return new(RoundSyncStatus.Accepted);
    }
    private RoundSyncStatus AcceptBegin(MatchBeginRound begin)
    {
        if (begin.MatchId != context.MatchId) return RoundSyncStatus.WrongRound;
        if (begin.RecipientId != context.LocalId) return RoundSyncStatus.WrongRecipient;
        if (begin.Settings is null || !begin.Settings.IsValid() || (context.AgreedSettings is not null && begin.Settings != context.AgreedSettings)
            || begin.Compatibility is null || !context.Compatibility.Matches(begin.Compatibility)
            || begin.Players is null || !context.Participants.Select(p => p.Player).SequenceEqual(begin.Players)
            || begin.RoundId == Guid.Empty || begin.RoundId == context.MatchId || begin.RoundNumber < 1 || begin.RoundNumber > begin.Settings.RoundCount
            || !words.Any(w => w.Id == begin.WordId)) return RoundSyncStatus.InvalidMessage;
        if (start is not null && begin.RoundNumber == start.RoundNumber)
            return RoundControlMessages.Encode(begin).AsSpan().SequenceEqual(RoundControlMessages.Encode(start)) ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict;
        if (isHost || phase is MultiplayerMatchPhase.Completed or MultiplayerMatchPhase.Stopped) return RoundSyncStatus.Closed;
        if (begin.RoundNumber != (start?.RoundNumber ?? 0) + 1 || seenRounds.Contains(begin.RoundId)) return RoundSyncStatus.Stale;
        if (start is null ? begin.PreviousResultHash is not null : begin.Settings != start.Settings || results.Count != start.RoundNumber
            || round!.Snapshot().Busy || begin.PreviousResultHash != ResultHash(results[^1].Result)) return RoundSyncStatus.Conflict;
        Activate(begin); return RoundSyncStatus.Accepted;
    }
    private void Receive(RoundInboundPacket packet)
    {
        if (packet.Bytes is null || packet.Bytes.Length is 0 or > 1024) return;
        if (packet.Kind == RoundPacketKind.MatchControlToHost && peers.TryGetValue(packet.AuthenticatedSender, out var peer)) peer.Endpoint.Receive(packet.AuthenticatedSender, packet.Bytes);
        else if (packet.Kind == RoundPacketKind.MatchControlToClient && packet.AuthenticatedSender == context.HostId) client.Receive(packet.AuthenticatedSender, packet.Bytes);
        else if (packet.Kind is RoundPacketKind.ControlToHost or RoundPacketKind.ControlToClient or RoundPacketKind.ImageToHost or RoundPacketKind.ImageToClient) lease?.Enqueue(packet);
    }
    private void Send(string peer, RoundPacketKind kind, byte[] bytes)
    {
        if (peer == context.LocalId) { if (loopback.Count < LocalRoundNetwork.MaxQueuedPackets) loopback.Enqueue(new(context.LocalId, kind, bytes)); }
        else transport.TrySend(peer, kind, bytes);
    }
    private void Stop(MatchStopReason reason)
    {
        if (phase is MultiplayerMatchPhase.Stopped or MultiplayerMatchPhase.Completed) return;
        stopReason = reason; phase = MultiplayerMatchPhase.Stopped; revision++;
        if (round is not null) { stoppedRound = round.Snapshot(); round.Dispose(); round = null; }
    }
    public MatchRuntimeSnapshot Snapshot()
    {
        lock (sync)
        {
            CheckAlive();
            string message = phase switch
            {
                MultiplayerMatchPhase.Completed => "모든 라운드가 끝났습니다.",
                MultiplayerMatchPhase.BetweenRounds => "모두 결과를 확인했습니다. 방장이 다음 라운드를 시작할 수 있습니다.",
                MultiplayerMatchPhase.Stopped => stopReason switch
                {
                    MatchStopReason.ParticipantDisconnected => "참가자 연결이 끊겨 경기를 중단했습니다.",
                    MatchStopReason.ResultMismatch => "참가자들의 결과가 달라 경기를 중단했습니다.",
                    MatchStopReason.ConfirmationTimedOut => "결과 수신 확인 시간이 지나 경기를 중단했습니다.",
                    _ => "경기를 계속할 수 없어 중단했습니다. 확정된 라운드 기록은 유지합니다."
                },
                _ => results.Count == (start?.RoundNumber ?? -1) ? "모두 확정 결과를 받을 때까지 기다립니다." : "라운드를 진행하고 있습니다."
            };
            return new(context.MatchId, start?.RoundId ?? Guid.Empty, start?.RoundNumber ?? 0, start?.Settings.RoundCount ?? 0,
                isHost, phase, isHost && phase == MultiplayerMatchPhase.BetweenRounds, Array.AsReadOnly((string[])ready.Clone()),
                Array.AsReadOnly(results.ToArray()), round?.Snapshot() ?? stoppedRound, message);
        }
    }
    private static VotingResult Freeze(VotingResult result) => result with
    { Tallies = Array.AsReadOnly(result.Tallies.ToArray()), LeadingArtworkIds = Array.AsReadOnly(result.LeadingArtworkIds.ToArray()) };
    public static string ResultHash(VotingResult result) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result, RuntimeAssets.Json)));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private void CheckAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true; round?.Dispose(); round = null; stoppedRound = null;
            client.Dispose(); foreach (var p in peers.Values) p.Endpoint.Dispose(); transport.Dispose(); loopback.Clear(); results.Clear();
        }
    }
}
