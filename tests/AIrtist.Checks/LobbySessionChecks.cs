using System.Buffers.Binary;
using System.Text.Json;
using AIrtist.Core;

internal static class LobbySessionChecks
{
    private static readonly Word[] Words = [new("apple", "사과", "둥근 과일", "apple", ["사과", "apple"])];
    private static readonly MultiplayerCompatibility Compatible = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Invalid lobby sync action accepted"); }
    private sealed class NoAi : IDrawingAi
    {
        public string Translate(string input, CancellationToken token) => throw new InvalidOperationException("AI must not run during lobby");
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress) => throw new InvalidOperationException("AI must not run during lobby");
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly LocalRoundNetwork Network = new();
        public readonly LobbyMember[] Members = Enumerable.Range(1, 3).Select(n => new LobbyMember(new("p" + n, "참가자 " + n), Guid.NewGuid())).ToArray();
        public readonly IRoundTransport[] Transports;
        public readonly MultiplayerLobbySession[] Sessions;
        public readonly HashSet<string> Dropped = new();
        public readonly List<(string From, string To, RoundPacketKind Kind, byte[] Bytes)> Packets = new();
        public bool DropP3Upload, DropHostToP3, DropP3Receipt, DropP3Ready;
        public World(bool loss = false, bool mismatch = false)
        {
            Transports = Members.Select(m => Network.Connect(m.Player.Id)).ToArray();
            Network.DropPacket = (from, to, kind, bytes) =>
            {
                if (Packets.Count < 20000) Packets.Add((from, to, kind, (byte[])bytes.Clone()));
                if ((DropP3Upload && from == "p3" && kind == RoundPacketKind.LobbyControlToHost)
                    || (DropHostToP3 && from == "p1" && to == "p3" && kind == RoundPacketKind.LobbyControlToClient)) return true;
                if (from == "p3" && kind == RoundPacketKind.LobbyControlToHost && bytes.Length > ReliableControlChannel.HeaderBytes && bytes[6] == 1)
                {
                    try
                    {
                        var message = RoundControlMessages.Decode(bytes.AsSpan(ReliableControlChannel.HeaderBytes));
                        if ((DropP3Receipt && message is LobbyStartReceipt) || (DropP3Ready && message is LobbyReadyRequest)) return true;
                    }
                    catch (System.IO.InvalidDataException) { }
                }
                return loss && Dropped.Add(from + to + kind + bytes[6] + new Guid(bytes.AsSpan(24, 16)) + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8)));
            };
            var settings = MultiplayerMatchSettings.Default with { RoundCount = 2, RoundPolicy = MultiplayerRoundPolicy.Default with
                { WritingDuration = TimeSpan.FromSeconds(20), GenerationDuration = TimeSpan.FromSeconds(1) } };
            Sessions = Members.Select((m, i) => new MultiplayerLobbySession(new(Id, "p1", m.Player.Id,
                mismatch && i == 2 ? Compatible with { AssetsSha256 = new('C', 64) } : Compatible, Members), Words, new NoAi(), Transports[i], i == 0 ? settings : null, clock: Clock)).ToArray();
        }
        public void Step(int count = 1)
        {
            for (int i = 0; i < count; i++) { foreach (var s in Sessions) s.Tick(); Clock.Advance(TimeSpan.FromMilliseconds(20)); }
        }
        public void Until(Func<bool> predicate, int limit = 4000)
        {
            for (int i = 0; i < limit; i++) { if (predicate()) return; Step(); }
            throw new InvalidOperationException("Lobby wait failed: " + string.Join(" | ", Sessions.Select(s => s.Snapshot().ToString())));
        }
        public void Ready()
        {
            Until(() => Sessions.All(s => s.Snapshot().Lobby is not null));
            foreach (var s in Sessions) s.SetReady(s.Snapshot().Lobby!.Revision, true);
            Until(() => Sessions.All(s => !s.Snapshot().PendingReady && s.Snapshot().Lobby!.CanStart));
        }
        public void Start() => Sessions[0].Start(Sessions[0].Snapshot().Lobby!.Revision);
        public void Dispose() { foreach (var s in Sessions) s.Dispose(); }
    }
    public static void Run()
    {
        ReplicationAndMatch(); StartBarrier(); ReadyCancellationRace(); MismatchAndTimeout(); MembershipAndDisposal(); CodecAndAuthority(); AgreedSettings();
        Console.WriteLine("Lobby session: isolated replicas, packet/ACK loss, ready/settings sync, acknowledged start barrier, match handoff, cancellation race, mismatch/timeout/membership and hostile messages passed (local transport, no AI)");
    }
    private static void ReplicationAndMatch()
    {
        using var w = new World(loss: true); w.Ready();
        var before = w.Sessions[0].Snapshot().Lobby!;
        Reject(() => w.Sessions[1].Start(before.Revision));
        Reject(() => w.Sessions[1].UpdateSettings(before.Revision, before.Settings));
        var changed = before.Settings with { RoundCount = 1, RoundPolicy = before.Settings.RoundPolicy with { WritingDuration = TimeSpan.FromSeconds(30) } };
        w.Sessions[0].UpdateSettings(before.Revision, changed);
        w.Until(() => w.Sessions.All(s => s.Snapshot().Lobby!.Revision > before.Revision));
        Require(w.Sessions.All(s => s.Snapshot().Lobby!.Members.All(m => !m.Ready)), "Settings did not clear all replicas");
        Reject(() => w.Sessions[2].SetReady(before.Revision, true)); w.Ready(); w.Start();
        Require(w.Sessions[0].Match is null, "Host match started before receipt barrier");
        w.Until(() => w.Sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Running));
        var matchId = w.Sessions[0].Match!.Snapshot().MatchId;
        Require(w.Sessions.All(s => s.Match!.Snapshot().MatchId == matchId && s.Match.Snapshot().RoundCount == 1), "Match ID/settings differ");
        Reject(() => w.Start());
        foreach (var p in w.Packets.Where(p => p.Kind is RoundPacketKind.LobbyControlToHost or RoundPacketKind.LobbyControlToClient).Take(90).ToArray())
            w.Transports[Array.FindIndex(w.Members, m => m.Player.Id == p.From)].TrySend(p.To, p.Kind, p.Bytes);
        w.Step(30);
        Require(w.Sessions.All(s => s.Match!.Snapshot().MatchId == matchId), "Delayed lobby packets replaced match");
        w.Until(() => w.Sessions.All(s => s.Match!.Snapshot().Phase == MultiplayerMatchPhase.Completed));
        string result = JsonSerializer.Serialize(w.Sessions[0].Match!.Snapshot().Results);
        Require(w.Sessions.All(s => JsonSerializer.Serialize(s.Match!.Snapshot().Results) == result), "Empty match results differ after handoff");
        Require(w.Dropped.Count > 30, "Packet loss was not exercised");
    }
    private static void StartBarrier()
    {
        using var w = new World(); w.Ready(); w.DropP3Receipt = true; w.Start();
        w.Step(120); // p3 receives the plan, but its application receipt is lost.
        Require(w.Sessions[0].Match is null && w.Sessions[1].Match is not null && w.Sessions[2].Match is not null, "Plan receipt barrier missing");
        Require(w.Sessions.Skip(1).All(s => s.Match!.Snapshot().Round is null), "Client began writing before everyone prepared");
        w.DropP3Receipt = false;
        w.Until(() => w.Sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Running));
    }
    private static void ReadyCancellationRace()
    {
        using var w = new World(); w.Ready(); w.DropP3Ready = true;
        w.Sessions[2].SetReady(w.Sessions[2].Snapshot().Lobby!.Revision, false); w.Start(); w.Step(100);
        Require(w.Sessions[2].Snapshot().Phase == LobbySessionPhase.Stopped && w.Sessions[2].Match is null, "Start ignored local in-flight unready");
        Require(w.Sessions[0].Match is null, "Host started without cancellation-race peer");
        w.DropP3Ready = false; w.Until(() => w.Sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Stopped));
    }
    private static void MismatchAndTimeout()
    {
        using (var w = new World(mismatch: true))
        {
            w.Until(() => w.Sessions.All(s => s.Snapshot().Lobby is not null));
            foreach (var s in w.Sessions) s.SetReady(s.Snapshot().Lobby!.Revision, true);
            w.Until(() => w.Sessions.All(s => !s.Snapshot().PendingReady));
            Require(w.Sessions[0].Snapshot().Lobby!.Members[2].Issue == LobbyCompatibilityIssue.Assets, "Mismatch not replicated"); Reject(() => w.Start());
        }
        using (var w = new World())
        {
            w.Ready(); w.DropP3Upload = true; w.Start();
            w.Until(() => w.Sessions[0].Snapshot().Phase == LobbySessionPhase.Stopped);
            Require(w.Sessions[0].Match is null, "Timed-out start created host match");
        }
        using (var w = new World())
        {
            w.DropHostToP3 = true;
            w.Until(() => w.Sessions[2].Snapshot().Phase == LobbySessionPhase.Stopped);
            Require(w.Sessions[2].Snapshot().Lobby is null, "Dropped initial state unexpectedly arrived");
        }
    }
    private static void MembershipAndDisposal()
    {
        using var w = new World(); w.Ready(); w.Start(); w.Until(() => w.Sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Running));
        w.Sessions[0].ObserveMembership("p1", w.Members.Reverse().ToArray());
        Require(w.Sessions[0].Snapshot().Phase == LobbySessionPhase.Running, "Reordered roster aborted match");
        w.Sessions[0].ObserveMembership("p2", w.Members);
        w.Until(() => w.Sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Stopped));
        Require(w.Sessions.All(s => s.Match!.Snapshot().Phase == MultiplayerMatchPhase.Stopped), "Lobby close did not abort match");
        using var left = new World(); left.Ready(); left.Transports[2].Dispose();
        left.Until(() => left.Sessions[0].Snapshot().Phase == LobbySessionPhase.Stopped);
    }
    private static void CodecAndAuthority()
    {
        using var w = new World(); w.Ready();
        var state = w.Sessions[2].Snapshot().Lobby!;
        // A real non-host lane cannot forge the transport sender as host.
        using var impostor = new ReliableControlChannel(w.Id, w.Id, "p3", clock: w.Clock);
        Require(impostor.TryQueue(RoundControlMessages.Encode(new LobbyStopNotice(w.Id, "p1")), out _) == ControlQueueStatus.Queued, "Fixture queue failed");
        foreach (var bytes in impostor.PollPackets()) w.Transports[1].TrySend("p3", RoundPacketKind.LobbyControlToClient, bytes);
        w.Step(10); Require(w.Sessions[2].Snapshot().Phase == LobbySessionPhase.Waiting, "Guest forged host close");
        byte[] packet = RoundControlMessages.Encode(new LobbyStateMessage(w.Id, "p3", 1, state));
        Require(RoundControlMessages.Decode<LobbyStateMessage>(packet).State.Members.Count == 3 && packet.Length <= RoundControlMessages.MaxBytes, "Lobby codec failed");
        // Test malformed messages from an authenticated host on a fresh connection.
        using var netClient = new LocalFixture();
        netClient.Send(new LobbyStateMessage(netClient.Id, "p2", 1, state with { LobbyId = netClient.Id, Members = Array.AsReadOnly(netClient.Members.Select(m => new LobbyMemberState(m, false, LobbyCompatibilityIssue.Unchecked)).ToArray()), CanStart = false }));
        Require(netClient.Session.Snapshot().Lobby is not null, "Valid state rejected");
        var baseline = netClient.Session.Snapshot().Lobby!;
        netClient.Send(new LobbyStateMessage(netClient.Id, "p2", 2, baseline with { CanStart = true }));
        Require(!netClient.Session.Snapshot().Lobby!.CanStart, "Invalid ready summary accepted");
        netClient.Send(new LobbyStateMessage(netClient.Id, "p2", 2, baseline with { Settings = baseline.Settings with { RoundCount = 9 } }));
        Require(netClient.Session.Snapshot().Lobby!.Settings == baseline.Settings, "Same-revision settings conflict accepted");
        netClient.Send(new LobbyStateMessage(netClient.Id, "p2", 1, baseline with { Revision = baseline.Revision + 1 }));
        Require(netClient.Session.Snapshot().Lobby!.Revision == baseline.Revision, "Conflicting same-version state accepted");
        // A host cannot synthesize this local participant's consent from a state
        // message when the player has never sent a ready request.
        var forgedReady = baseline with { Phase = LobbyPhase.Started,
            Members = Array.AsReadOnly(baseline.Members.Select(m => m with { Ready = true, ReadySequence = 1, Issue = LobbyCompatibilityIssue.None }).ToArray()) };
        netClient.Send(new LobbyStateMessage(netClient.Id, "p2", 2, forgedReady));
        var plan = new LobbyLaunchPlan(netClient.Id, baseline.Revision, Guid.NewGuid(), "p1", Compatible, baseline.Settings,
            Array.AsReadOnly(netClient.Members.Select(m => new MultiplayerParticipant(m.Player, Compatible)).ToArray()));
        netClient.Send(new LobbyStartOffer(netClient.Id, "p2", plan));
        Require(netClient.Session.Snapshot().Phase == LobbySessionPhase.Stopped && netClient.Session.Match is null, "Host invented local ready consent");
        using var prepared = new LocalFixture();
        var initial = baseline with { LobbyId = prepared.Id, Members = Array.AsReadOnly(prepared.Members.Select(m => new LobbyMemberState(m, false, LobbyCompatibilityIssue.Unchecked)).ToArray()) };
        prepared.Send(new LobbyStateMessage(prepared.Id, "p2", 1, initial));
        prepared.Session.SetReady(initial.Revision, true);
        var startState = initial with { Phase = LobbyPhase.Started, Members = Array.AsReadOnly(initial.Members.Select(m => m with { Ready = true, ReadySequence = 1, Issue = LobbyCompatibilityIssue.None }).ToArray()) };
        prepared.Send(new LobbyStateMessage(prepared.Id, "p2", 2, startState));
        var acceptedPlan = plan with { LobbyId = prepared.Id, Participants = Array.AsReadOnly(prepared.Members.Select(m => new MultiplayerParticipant(m.Player, Compatible)).ToArray()) };
        prepared.Send(new LobbyStartOffer(prepared.Id, "p2", acceptedPlan));
        Require(prepared.Session.Match is not null, "Valid prepared fixture failed");
        prepared.Send(new LobbyStateMessage(prepared.Id, "p2", 3, startState with { Revision = startState.Revision + 1, Settings = startState.Settings with { RoundCount = 4 } }));
        Require(prepared.Session.Snapshot().Lobby!.Settings == startState.Settings, "Prepared lobby changed agreed settings");
    }

    private static void AgreedSettings()
    {
        var clock = new ControlTransportChecks.Clock(); var net = new LocalRoundNetwork();
        using var hostWire = net.Connect("p1"); var clientWire = net.Connect("p2");
        Guid matchId = Guid.NewGuid(), roundId = Guid.NewGuid();
        MultiplayerParticipant[] players = [new(new("p1", "방장"), Compatible), new(new("p2", "참가자"), Compatible)];
        var agreed = MultiplayerMatchSettings.Default;
        using var receiver = new MultiplayerMatchRuntime(new(matchId, "p1", "p2", Compatible, players, agreed), Words, new NoAi(), clientWire, clock: clock);
        using var lane = new ReliableControlEndpoint(matchId, matchId, "p2", (_, _) => new(RoundSyncStatus.Accepted), clock);
        var begin = new MatchBeginRound(matchId, "p2", roundId, 1, agreed, "apple", null, Compatible, players.Select(p => p.Player).ToArray());
        void Send(MatchBeginRound message)
        {
            Require(lane.Queue(message) == ControlQueueStatus.Queued, "Match fixture full");
            for (int i = 0; i < 30; i++)
            {
                foreach (var bytes in lane.PollPackets()) hostWire.TrySend("p2", RoundPacketKind.MatchControlToClient, bytes);
                receiver.Tick();
                foreach (var p in hostWire.Receive(256)) if (p.Kind == RoundPacketKind.MatchControlToHost) lane.Receive(p.AuthenticatedSender, p.Bytes);
                clock.Advance(TimeSpan.FromMilliseconds(10));
            }
        }
        Send(begin with { Settings = agreed with { RoundCount = 4 } });
        Require(receiver.Snapshot().RoundNumber == 0, "Match changed agreed lobby settings");
        Send(begin); Require(receiver.Snapshot().RoundNumber == 1, "Agreed match settings rejected");
    }
    private sealed class LocalFixture : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly LobbyMember[] Members = [new(new("p1", "방장"), Guid.NewGuid()), new(new("p2", "참가자"), Guid.NewGuid())];
        private readonly IRoundTransport host;
        private readonly ReliableControlChannel channel;
        public readonly MultiplayerLobbySession Session;
        public LocalFixture()
        {
            var net = new LocalRoundNetwork(); host = net.Connect("p1"); var client = net.Connect("p2");
            channel = new(Id, Id, "p2", clock: Clock);
            Session = new(new(Id, "p1", "p2", Compatible, Members), Words, new NoAi(), client, clock: Clock);
        }
        public void Send<T>(T message) where T : class
        {
            Require(channel.TryQueue(RoundControlMessages.Encode(message), out _) == ControlQueueStatus.Queued, "Fixture full");
            for (int i = 0; i < 30; i++)
            {
                foreach (var bytes in channel.PollPackets()) host.TrySend("p2", RoundPacketKind.LobbyControlToClient, bytes);
                Session.Tick(); foreach (var p in host.Receive(256)) channel.Receive(p.AuthenticatedSender, p.Bytes);
                if (channel.TakeReceived() is { } received) channel.ConfirmReceived(received.Sequence);
                Clock.Advance(TimeSpan.FromMilliseconds(20));
            }
        }
        public void Dispose() { Session.Dispose(); host.Dispose(); channel.Dispose(); }
    }
}
