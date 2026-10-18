using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class MatchRuntimeChecks
{
    private static readonly Word[] Words = [new("apple", "사과", "둥근 과일", "apple", ["사과", "apple"]), new("banana", "바나나", "긴 과일", "banana", ["바나나", "banana"])];
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return; } throw new InvalidOperationException("Invalid match action accepted"); }
    private static byte[] Png()
    {
        var pixels = new byte[32 * 32 * 3]; new Random(321).NextBytes(pixels);
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 96);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class Ai : IDrawingAi
    {
        private readonly byte[] png = Png();
        public int Generations;
        public bool Block;
        public readonly ManualResetEventSlim Release = new(false);
        public string Translate(string input, CancellationToken token) => "a colorful fruit";
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Generations); if (Block) Release.Wait(); return (byte[])png.Clone(); }
    }
    private sealed class World : IDisposable
    {
        public readonly Guid MatchId = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly LocalRoundNetwork Network = new();
        public readonly MultiplayerMatchRuntime[] Matches;
        public readonly IRoundTransport[] Transports;
        public readonly Ai Ai = new();
        public readonly List<(string From, string To, RoundPacketKind Kind, byte[] Bytes)> Capture = new();
        public readonly HashSet<string> Dropped = new();
        public bool DropClientMatch, DropHostMatch, CorruptReceipt;
        public World(bool loss = false, MultiplayerMatchSettings? settings = null)
        {
            var participants = Enumerable.Range(1, 3).Select(i => new MultiplayerParticipant(new("p" + i, "참가자 " + i), Compatibility)).ToArray();
            Transports = participants.Select(p => Network.Connect(p.Player.Id)).ToArray();
            Network.DropPacket = (from, to, kind, bytes) =>
            {
                if (Capture.Count < 20000) Capture.Add((from, to, kind, (byte[])bytes.Clone()));
                if (CorruptReceipt && from == "p3" && kind == RoundPacketKind.MatchControlToHost && bytes.Length > ReliableControlChannel.HeaderBytes
                    && RoundControlMessages.Decode(bytes.AsSpan(ReliableControlChannel.HeaderBytes)) is MatchRoundFinished receipt)
                {
                    byte[] payload = RoundControlMessages.Encode(receipt with { ResultHash = new('A', 64) });
                    Require(payload.Length == bytes.Length - ReliableControlChannel.HeaderBytes, "Receipt fixture size changed");
                    payload.CopyTo(bytes, ReliableControlChannel.HeaderBytes);
                    SHA256.HashData(payload).CopyTo(bytes, 56); // Valid packet containing a dishonest result claim.
                }
                if ((DropClientMatch && from == "p3" && kind == RoundPacketKind.MatchControlToHost)
                    || (DropHostMatch && from == "p1" && to == "p3" && kind == RoundPacketKind.MatchControlToClient)) return true;
                if (!loss) return false;
                if (kind is RoundPacketKind.ImageToHost or RoundPacketKind.ImageToClient)
                {
                    int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
                    return index % 7 == 0 && Dropped.Add(from + to + kind + new Guid(bytes.AsSpan(8, 16)) + ":" + index);
                }
                return Dropped.Add(from + to + kind + new Guid(bytes.AsSpan(24, 16)) + bytes[6] + ":" + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8)));
            };
            var sharedAi = new SerializedDrawingAi(Ai);
            Matches = participants.Select((p, i) => new MultiplayerMatchRuntime(new(MatchId, "p1", p.Player.Id, Compatibility, participants),
                Words, sharedAi, Transports[i], i == 0 ? settings ?? MultiplayerMatchSettings.Default with { RoundCount = 2 } : null,
                i == 0 ? "apple" : null, Clock)).ToArray();
        }
        public async Task Until(Func<bool> condition, int steps = 3000)
        {
            for (int i = 0; i < steps; i++)
            {
                foreach (var m in Matches) m.Tick();
                if (condition()) return;
                Clock.Advance(TimeSpan.FromMilliseconds(20)); await Task.Delay(1);
            }
            throw new InvalidOperationException("Match wait failed: " + string.Join(" | ", Matches.Select(m =>
            { var s = m.Snapshot(); return $"{s.RoundNumber}/{s.Phase}/{s.Results.Count}/{string.Join(',', s.ReadyPlayerIds)} {s.Message} {s.Round?.Submission.Phase}/{s.Round?.Gallery.Phase}"; })));
        }
        public async Task PlayRound(bool blockReceipt = false)
        {
            await Until(() => Matches.All(m => m.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
            foreach (var m in Matches) m.PrepareTranslation(m.Snapshot().RoundId, "색이 선명한 과일");
            await Until(() => Matches.All(m => m.Snapshot().Round?.AiPhase == RoundAiPhase.Reviewing));
            foreach (var m in Matches) m.Confirm(m.Snapshot().RoundId, m.Snapshot().Round!.Draft!.Id, "a colorful fruit", 42);
            await Until(() => Matches.All(m => m.Snapshot().Round!.Gallery.CanVote));
            if (blockReceipt) DropClientMatch = true;
            int[] target = [1, 0, 1];
            for (int i = 0; i < 3; i++) Matches[i].Vote(Matches[i].Snapshot().RoundId, Matches[i].Snapshot().Round!.Gallery.Artworks[target[i]].Id);
            await Until(() => Matches.All(m => m.Snapshot().Results.Count == m.Snapshot().RoundNumber));
        }
        public void Dispose() { Ai.Release.Set(); foreach (var m in Matches) m.Dispose(); }
    }
    public static async Task Run()
    {
        FinalAggregation(); await TwoRounds(); await ConfirmationBarrier(); await ConfirmationFailures(); await LateAiBarrier(); await DisconnectAndLiveness(); await EmptyRounds(); await ProtocolValidation(); HostMessageValidation();
        Console.WriteLine("Match runtime: rounds/results, cumulative winners/ties/zero votes, host-only final screen, retransmission/ACK barrier, retained final results after disconnect and protocol validation passed (local transport, fake AI)");
    }
    private static async Task TwoRounds()
    {
        using var world = new World(loss: true); var host = world.Matches[0];
        Guid firstId = host.Snapshot().RoundId;
        Reject(() => host.AdvanceRound(firstId));
        Reject(() => host.ShowFinalResults(firstId));
        await world.PlayRound();
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.BetweenRounds));
        string first = JsonSerializer.Serialize(host.Snapshot().Results[0]);
        Reject(() => world.Matches[1].AdvanceRound(firstId));
        var replay = world.Capture.Where(p => p.Kind is RoundPacketKind.ControlToHost or RoundPacketKind.ControlToClient or RoundPacketKind.ImageToHost or RoundPacketKind.ImageToClient).Take(150).ToArray();
        host.AdvanceRound(firstId); Reject(() => host.AdvanceRound(firstId));
        await world.Until(() => world.Matches.All(m => m.Snapshot().RoundNumber == 2 && m.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
        Guid secondId = host.Snapshot().RoundId;
        Require(secondId != firstId && secondId != world.MatchId, "Round/channel ID reused");
        Require(world.Matches.All(m => m.Snapshot().RoundId == secondId && m.Snapshot().Round is { Draft: null, OwnPng: null, Gallery.Artworks.Count: 0 }), "Old draft/image retained");
        Require(host.Snapshot().Round!.Submission.Prompt!.Id != "apple", "Word repeated before bag exhausted");
        Reject(() => host.PrepareTranslation(firstId, "옛 라운드 입력"));
        Reject(() => host.CancelAi(firstId));
        foreach (var packet in replay)
        {
            world.Transports[int.Parse(packet.From[1..]) - 1].TrySend(packet.To, packet.Kind, packet.Bytes);
            foreach (var m in world.Matches) m.Tick();
        }
        Require(world.Matches.All(m => m.Snapshot().Round!.Submission.Phase == SubmissionClientPhase.Writing
            && m.Snapshot().Round!.Gallery.Artworks.Count == 0 && m.Snapshot().Results.Count == 1), "Old packets changed next round");
        await world.PlayRound();
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.Completed));
        Require(world.Ai.Generations == 6 && world.Dropped.Count > 0, "Two rounds did not run six AI jobs with loss");
        string final = JsonSerializer.Serialize(host.Snapshot().Results);
        Require(world.Matches.All(m => JsonSerializer.Serialize(m.Snapshot().Results) == final)
            && JsonSerializer.Serialize(host.Snapshot().Results[0]) == first
            && host.Snapshot().Results.All(r => r.Result.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0])), "History mutated or diverged");
        Reject(() => host.AdvanceRound(secondId));
        Require(world.Matches.All(m => m.Snapshot().FinalResults is null) && host.Snapshot().CanShowFinalResults &&
            !world.Matches[1].Snapshot().CanShowFinalResults, "Final page opened automatically or guest was authorized");
        Reject(() => world.Matches[1].ShowFinalResults(secondId)); Reject(() => host.ShowFinalResults(firstId));
        world.DropHostMatch = true;
        host.ShowFinalResults(secondId); host.ShowFinalResults(secondId);
        Require(host.Snapshot().FinalResultsDeliveryPending, "Host could leave before final display message delivery");
        await world.Until(() => world.Matches[1].Snapshot().FinalResults is not null);
        Require(world.Matches[2].Snapshot().FinalResults is null && host.Snapshot().FinalResultsDeliveryPending, "Lost packet bypassed final delivery barrier");
        world.DropHostMatch = false;
        await world.Until(() => world.Matches.All(m => m.Snapshot().FinalResults is not null) && !host.Snapshot().FinalResultsDeliveryPending);
        string standings = JsonSerializer.Serialize(host.Snapshot().FinalResults);
        Require(world.Matches.All(m => JsonSerializer.Serialize(m.Snapshot().FinalResults) == standings) &&
            host.Snapshot().FinalResults!.Winners.Single() is { PlayerId: "p2", Votes: 4 }, "Cumulative standings diverged");
        world.Transports[0].Dispose();
        foreach (var m in world.Matches) m.Tick();
        Require(JsonSerializer.Serialize(world.Matches[1].Snapshot().Results) == final, "Host loss erased history");
        Require(JsonSerializer.Serialize(world.Matches[1].Snapshot().FinalResults) == standings, "Host loss erased final screen");
    }
    private static async Task ConfirmationBarrier()
    {
        using var world = new World(); await world.PlayRound(blockReceipt: true);
        // A queued receipt can exist but has not yet been delivered to the host.
        world.DropClientMatch = true;
        for (int i = 0; i < 15; i++) { foreach (var m in world.Matches) m.Tick(); world.Clock.Advance(TimeSpan.FromMilliseconds(20)); }
        if (world.Matches[0].Snapshot().Phase == MultiplayerMatchPhase.BetweenRounds)
            throw new InvalidOperationException("Receipt test started after the barrier");
        Reject(() => world.Matches[0].AdvanceRound(world.Matches[0].Snapshot().RoundId));
        world.DropClientMatch = false;
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.BetweenRounds));
    }
    private static async Task DisconnectAndLiveness()
    {
        using (var world = new World())
        {
            await world.Until(() => world.Matches.All(m => m.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
            world.Transports[2].Dispose();
            await world.Until(() => world.Matches[0].Snapshot().Phase == MultiplayerMatchPhase.Stopped && world.Matches[1].Snapshot().Phase == MultiplayerMatchPhase.Stopped);
            Reject(() => world.Matches[0].AdvanceRound(world.Matches[0].Snapshot().RoundId));
        }
        using (var world = new World())
        {
            world.DropHostMatch = true;
            await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.Stopped));
        }
    }
    private static async Task LateAiBarrier()
    {
        using var world = new World(settings: MultiplayerMatchSettings.Default with { RoundCount = 2,
            RoundPolicy = MultiplayerRoundPolicy.Default with { WritingDuration = TimeSpan.FromSeconds(1), GenerationDuration = TimeSpan.FromSeconds(1) } });
        var participant = world.Matches[1];
        await world.Until(() => participant.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing);
        participant.PrepareTranslation(participant.Snapshot().RoundId, "붉은 과일");
        await world.Until(() => participant.Snapshot().Round?.AiPhase == RoundAiPhase.Reviewing);
        world.Ai.Block = true;
        participant.Confirm(participant.Snapshot().RoundId, participant.Snapshot().Round!.Draft!.Id, "a red fruit", 42);
        await world.Until(() => world.Ai.Generations == 1);
        world.Clock.Advance(TimeSpan.FromSeconds(2));
        await world.Until(() => world.Matches.All(m => m.Snapshot().Results.Count == 1));
        Require(participant.Snapshot().Round!.Busy && !world.Matches[0].Snapshot().CanAdvance, "Busy cancelled AI crossed round barrier");
        Reject(() => world.Matches[0].AdvanceRound(world.Matches[0].Snapshot().RoundId));
        world.Ai.Release.Set();
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.BetweenRounds));
        Require(participant.Snapshot().Round!.OwnPng is null, "Late cancelled PNG survived");
        world.Matches[0].AdvanceRound(world.Matches[0].Snapshot().RoundId);
        await world.Until(() => world.Matches.All(m => m.Snapshot().RoundNumber == 2));
        Require(world.Matches.All(m => m.Snapshot().Round!.OwnPng is null && m.Snapshot().Round!.Draft is null), "Late AI contaminated next round");
    }
    private static async Task ConfirmationFailures()
    {
        using (var world = new World(settings: MultiplayerMatchSettings.Default with { RoundCount = 2, ResultConfirmationTimeout = TimeSpan.FromSeconds(1) }))
        {
            await world.PlayRound(blockReceipt: true);
            await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.Stopped));
            Require(world.Matches[0].Snapshot().Message.Contains("시간") && world.Matches.All(m => m.Snapshot().Results.Count == 1), "Confirmation timeout lost records/did not stop");
        }
        using (var world = new World())
        {
            world.CorruptReceipt = true;
            await world.PlayRound();
            await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.Stopped));
            Require(world.Matches[0].Snapshot().Message.Contains("달라") && !world.Matches[0].Snapshot().CanAdvance, "Different final hash allowed next round");
        }
    }
    private static async Task EmptyRounds()
    {
        using var world = new World(settings: MultiplayerMatchSettings.Default with { RoundCount = 2, RoundPolicy = MultiplayerRoundPolicy.Default with { WritingDuration = TimeSpan.FromSeconds(1) } });
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.BetweenRounds));
        world.Matches[0].AdvanceRound(world.Matches[0].Snapshot().RoundId);
        await world.Until(() => world.Matches.All(m => m.Snapshot().Phase == MultiplayerMatchPhase.Completed));
        Require(world.Matches.All(m => m.Snapshot().Results.Count == 2 && m.Snapshot().Results.All(r => r.Result.Reason == VotingEnd.InsufficientImages)), "Empty match not finalized");
        world.Matches[0].ShowFinalResults(world.Matches[0].Snapshot().RoundId);
        await world.Until(() => world.Matches.All(m => m.Snapshot().FinalResults is { TotalVotes: 0, Winners.Count: 0 }));
    }
    private static async Task ProtocolValidation()
    {
        using var world = new World();
        await world.Until(() => world.Matches.All(m => m.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
        var state = world.Matches[1].Snapshot();
        // A new channel tries sequence 1 with forged host origin. The transport
        // pins p3, so the p2 receiver must not dispatch it as host control.
        using var forged = new ReliableControlChannel(world.MatchId, world.MatchId, "p2", clock: world.Clock);
        forged.TryQueue(RoundControlMessages.Encode(new MatchProgress(world.MatchId, state.RoundId, 1, "p2", 99,
            MultiplayerMatchPhase.Completed, ["p1", "p2", "p3"], MatchStopReason.None) { FinalResultsOpened = true }), out _);
        foreach (var packet in forged.PollPackets()) world.Transports[2].TrySend("p2", RoundPacketKind.MatchControlToClient, packet);
        world.Matches[1].Tick();
        Require(world.Matches[1].Snapshot().Phase == MultiplayerMatchPhase.Playing && world.Matches[1].Snapshot().Results.Count == 0, "Peer forged match completion");
        Require(!(MultiplayerMatchSettings.Default with { RoundCount = 11 }).IsValid()
            && !(MultiplayerMatchSettings.Default with { ResultConfirmationTimeout = TimeSpan.Zero }).IsValid(), "Unbounded match settings accepted");
    }
    private static void HostMessageValidation()
    {
        var clock = new ControlTransportChecks.Clock(); var network = new LocalRoundNetwork();
        using var hostWire = network.Connect("p1"); var clientWire = network.Connect("p2");
        Guid matchId = Guid.NewGuid(), roundId = Guid.NewGuid();
        MultiplayerParticipant[] participants = [new(new("p1", "방장"), Compatibility), new(new("p2", "참가자"), Compatibility)];
        using var receiver = new MultiplayerMatchRuntime(new(matchId, "p1", "p2", Compatibility, participants), Words, new Ai(), clientWire, clock: clock);
        using var endpoint = new ReliableControlEndpoint(matchId, matchId, "p2", (_, _) => new(RoundSyncStatus.Accepted), clock);
        var begin = new MatchBeginRound(matchId, "p2", roundId, 1, MultiplayerMatchSettings.Default, "apple", null, Compatibility, participants.Select(p => p.Player).ToArray());
        void Deliver<T>(T value) where T : class
        {
            Require(endpoint.Queue(value) == ControlQueueStatus.Queued, "Host test queue full");
            for (int i = 0; i < 20; i++)
            {
                foreach (var packet in endpoint.PollPackets()) hostWire.TrySend("p2", RoundPacketKind.MatchControlToClient, packet);
                receiver.Tick();
                foreach (var packet in hostWire.Receive(128)) if (packet.Kind == RoundPacketKind.MatchControlToHost) endpoint.Receive("p2", packet.Bytes);
                clock.Advance(TimeSpan.FromMilliseconds(10));
            }
        }
        Deliver(begin with { Settings = begin.Settings with { RoundCount = 11 } });
        Deliver(begin with { RecipientId = "p1" }); Deliver(begin with { WordId = "unknown" });
        Require(receiver.Snapshot().RoundNumber == 0, "Invalid initial settings accepted");
        Deliver(begin); Deliver(begin);
        Require(receiver.Snapshot().RoundNumber == 1 && receiver.Snapshot().RoundId == roundId, "Valid/duplicate begin failed");
        Deliver(begin with { RoundId = Guid.NewGuid(), RoundNumber = 3 });
        Deliver(begin with { RoundId = Guid.NewGuid(), RoundNumber = 2, PreviousResultHash = new('A', 64) });
        Require(receiver.Snapshot().RoundId == roundId && receiver.Snapshot().Results.Count == 0, "Skipped/premature round replaced active round");
        var progress = new MatchProgress(matchId, roundId, 1, "p2", 5, MultiplayerMatchPhase.Playing, [], MatchStopReason.None);
        Deliver(progress);
        Deliver(progress with { Revision = 6, FinalResultsOpened = true });
        Deliver(progress with { Revision = 6, Phase = MultiplayerMatchPhase.Completed, ReadyPlayerIds = ["p1", "p2"], FinalResultsOpened = true });
        Require(receiver.Snapshot().FinalResults is null, "Premature final page accepted");
        Deliver(progress with { Phase = MultiplayerMatchPhase.Completed, ReadyPlayerIds = ["p1", "p2"] });
        Deliver(progress with { Phase = MultiplayerMatchPhase.Stopped, StopReason = MatchStopReason.RoundFailed });
        Deliver(progress with { Revision = 4, Phase = MultiplayerMatchPhase.Stopped, StopReason = MatchStopReason.RoundFailed });
        Require(receiver.Snapshot().Phase == MultiplayerMatchPhase.Playing, "Conflicting/stale/premature host state applied");
        Deliver(progress with { Revision = 6, ReadyPlayerIds = ["p1", "p1"] });
        Require(receiver.Snapshot().ReadyPlayerIds.Count == 0, "Duplicate ready identity accepted");
        Deliver(progress with { Revision = 6, Phase = MultiplayerMatchPhase.Stopped, StopReason = MatchStopReason.RoundFailed });
        Deliver(begin with { RoundId = Guid.NewGuid(), RoundNumber = 2 });
        Require(receiver.Snapshot().Phase == MultiplayerMatchPhase.Stopped && receiver.Snapshot().RoundNumber == 1, "Stopped match restarted");
    }
    private static void FinalAggregation()
    {
        VotingPlayer[] players = [new("a", "같은이름"), new("b", "같은이름"), new("c", "세번째")];
        Guid match = Guid.NewGuid();
        MatchRoundRecord Record(int number, params int[] votes) => new(number, "apple", new(match, Guid.NewGuid(), 1, 1,
            VotingEnd.AllVoted, DateTimeOffset.UnixEpoch, players.Select((p, i) => new ArtworkTally("art-" + p.Id, p.Id, votes[i])).ToArray(),
            [], votes.Sum(), players.Length, players.Length - votes.Sum()));
        var tie = MatchFinalResults.Create(players, [Record(1, 2, 1, 0), Record(2, 0, 1, 1)]);
        Require(tie.Winners.Select(p => p.PlayerId).SequenceEqual(["a", "b"]) && tie.Winners.All(p => p.Votes == 2) &&
            tie.TotalVotes == 5 && tie.Players.Count == 3, "Tied totals merged names or counted round wins");
        var single = MatchFinalResults.Create(players, [Record(1, 0, 0, 2)]);
        Require(single.Winners.Single() is { PlayerId: "c", Votes: 2 }, "Single winner aggregation");
        Require(MatchFinalResults.Create(players, [Record(1, 0, 0, 0)]).Winners.Count == 0, "All-zero match has winners");
        Reject(() => MatchFinalResults.Create(players, [Record(2, 1, 1, 1)]));
        Reject(() => MatchFinalResults.Create(players, [Record(1, -1, 1, 1)]));
        Require(!Compatibility.Matches(Compatibility with { RulesVersion = "multiplayer-waiting-v8" }), "Old final-screen-incompatible build accepted");
    }
}
