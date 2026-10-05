using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class RoundRuntimeChecks
{
    private static readonly Word Word = new("apple", "사과", "과일", "apple", ["사과", "apple"]);
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; } throw new InvalidOperationException("Invalid runtime action accepted"); }
    private static byte[] Png()
    {
        var pixels = new byte[32 * 32 * 3]; new Random(123).NextBytes(pixels);
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 96);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class Ai(byte[] png) : IDrawingAi
    {
        public readonly ConcurrentBag<string> Sources = new(), Prompts = new();
        public bool FailGeneration, BlockGeneration;
        public readonly ManualResetEventSlim Release = new(false);
        public int Active, Maximum;
        private void Enter() { int count = Interlocked.Increment(ref Active); Maximum = Math.Max(Maximum, count); }
        public string Translate(string description, CancellationToken token)
        { Enter(); try { Sources.Add(description); token.ThrowIfCancellationRequested(); return "a red fruit"; } finally { Interlocked.Decrement(ref Active); } }
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        {
            Enter(); try
            {
                Prompts.Add(translation); if (BlockGeneration) Release.Wait(); // Deliberately ignores cancellation.
                if (FailGeneration) throw new InvalidOperationException("fake generation failed");
                return (byte[])png.Clone();
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly LocalRoundNetwork Network = new();
        public readonly MultiplayerRoundRuntime[] Runtimes;
        public readonly Ai RawAi = new(Png());
        public readonly HashSet<string> Dropped = new();
        public World(bool loss = false, MultiplayerRoundPolicy? policy = null)
        {
            var players = Enumerable.Range(1, 3).Select(i => new MultiplayerParticipant(new("p" + i, "참가자 " + i), Compatibility)).ToArray();
            var transports = players.Select(p => Network.Connect(p.Player.Id)).ToArray();
            var sharedAi = new SerializedDrawingAi(RawAi);
            Runtimes = players.Select((p, i) => new MultiplayerRoundRuntime(new(Match, Round, 5, "p1", p.Player.Id, Compatibility, players),
                [Word], sharedAi, transports[i], i == 0 ? Word.Id : null, policy, Clock)).ToArray();
            if (loss) Network.DropPacket = (from, to, kind, bytes) =>
            {
                if (kind is RoundPacketKind.ControlToHost or RoundPacketKind.ControlToClient)
                    return Dropped.Add(from + to + kind + bytes[6] + ":" + BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8)));
                int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
                return index % 7 == 0 && Dropped.Add(from + to + kind + new Guid(bytes.AsSpan(8, 16)) + ":" + index);
            };
        }
        public async Task Until(Func<bool> condition, int steps = 2500)
        {
            for (int i = 0; i < steps; i++)
            {
                foreach (var runtime in Runtimes) runtime.Tick();
                if (condition()) return;
                Clock.Advance(TimeSpan.FromMilliseconds(20)); await Task.Delay(1);
            }
            throw new InvalidOperationException("Runtime wait failed: " + string.Join(" | ", Runtimes.Select(r => JsonSerializer.Serialize(r.Snapshot()))));
        }
        public void Dispose() { RawAi.Release.Set(); foreach (var runtime in Runtimes) runtime.Dispose(); }
    }
    public static async Task Run()
    {
        await Play(); await FailureAndLateResults(); await QueuedAiCancellation(); await GenerationDeadline();
        await EmptyRound(); await OneImageRound(); ProgressValidation(); TransportBounds();
        Console.WriteLine("Round runtime: autonomous timers/broadcasts/image scheduling/3-player voting, shared AI serialization, review correction, cancel/failure/stale-result rejection and empty-round final sync passed (local transport, fake AI)");
    }
    private static async Task Play()
    {
        using var world = new World(loss: true);
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Submission.Phase == SubmissionClientPhase.Writing));
        foreach (var runtime in world.Runtimes) { Reject(() => runtime.PrepareTranslation("사 과")); runtime.PrepareTranslation("아삭한 빨간 과일"); }
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().AiPhase == RoundAiPhase.Reviewing));
        var first = world.Runtimes[0]; long old = first.Snapshot().Draft!.Id; first.Revise(old);
        first.PrepareTranslation("둥글고 붉은 과일"); await world.Until(() => first.Snapshot().AiPhase == RoundAiPhase.Reviewing);
        Reject(() => first.Confirm(old, "a fruit", 1)); Reject(() => first.Confirm(first.Snapshot().Draft!.Id, "an apple", 1));
        foreach (var runtime in world.Runtimes) runtime.Confirm(runtime.Snapshot().Draft!.Id, "a round crimson fruit", 42);
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Gallery.CanVote));
        Require(world.RawAi.Prompts.Count == 3 && world.RawAi.Prompts.All(p => p == "a round crimson fruit")
            && world.RawAi.Sources.All(s => !s.Contains("사과")) && world.RawAi.Maximum == 1, "AI answer leak/correction/serialization failed");
        Require(world.Runtimes.All(r => r.Snapshot().Submission.Players.All(p => p.State == PlayerRoundState.Completed)), "Participant states not broadcast");
        int[] targets = [1, 0, 1];
        for (int i = 0; i < 3; i++)
        {
            var state = world.Runtimes[i].Snapshot();
            Reject(() => world.Runtimes[i].Vote(state.Gallery.Artworks.Single(a => a.OwnerId == state.LocalId).Id));
            world.Runtimes[i].Vote(state.Gallery.Artworks[targets[i]].Id);
        }
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Gallery.Result is not null));
        string expected = JsonSerializer.Serialize(first.Snapshot().Gallery.Result);
        Require(world.Runtimes.All(r => JsonSerializer.Serialize(r.Snapshot().Gallery.Result) == expected)
            && first.Snapshot().Gallery.Result!.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0]) && world.Dropped.Count > 0, "Runtime results diverged");
        first.Dispose();
        world.Runtimes[1].Tick(); world.Runtimes[2].Tick();
        Require(world.Runtimes[1].Snapshot().Gallery.Phase == RoundClientPhase.Disconnected
            && JsonSerializer.Serialize(world.Runtimes[1].Snapshot().Gallery.Result) == expected, "Host loss erased final result");
    }
    private static async Task FailureAndLateResults()
    {
        using var world = new World(); await world.Until(() => world.Runtimes[1].Snapshot().Submission.Phase == SubmissionClientPhase.Writing);
        var runtime = world.Runtimes[1]; runtime.PrepareTranslation("빨간 과일");
        await world.Until(() => runtime.Snapshot().AiPhase == RoundAiPhase.Reviewing);
        world.RawAi.BlockGeneration = true; runtime.Confirm(runtime.Snapshot().Draft!.Id, "a red fruit", 1);
        await world.Until(() => world.RawAi.Prompts.Count == 1); runtime.CancelAi();
        Reject(() => runtime.PrepareTranslation("다른 과일"));
        world.RawAi.Release.Set(); await world.Until(() => !runtime.Snapshot().Busy);
        await world.Until(() => world.Runtimes[0].Snapshot().Submission.Players.Any(p => p.PlayerId == "p2" && p.State == PlayerRoundState.GenerationFailed));
        Require(runtime.Snapshot().OwnPng is null && runtime.Snapshot().Submission.Phase == SubmissionClientPhase.AwaitingGallery, "Cancelled late PNG was uploaded");
        Reject(() => runtime.Confirm(runtime.Snapshot().Draft!.Id, "a fruit", 2));

        world.RawAi.BlockGeneration = false; world.RawAi.FailGeneration = true;
        var failure = world.Runtimes[2]; failure.PrepareTranslation("붉은 과일");
        await world.Until(() => failure.Snapshot().AiPhase == RoundAiPhase.Reviewing);
        failure.Confirm(failure.Snapshot().Draft!.Id, "a red fruit", 2);
        await world.Until(() => failure.Snapshot().Submission.Players.Any(p => p.PlayerId == "p3" && p.State == PlayerRoundState.GenerationFailed));
        Require(failure.Snapshot().OwnPng is null, "Failed generator submitted data");
    }
    private static async Task EmptyRound()
    {
        using var world = new World(policy: MultiplayerRoundPolicy.Default with { WritingDuration = TimeSpan.FromSeconds(1) });
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Gallery.Result is not null));
        Require(world.Runtimes.All(r => r.Snapshot().Gallery.Result is { Reason: VotingEnd.InsufficientImages, AcceptedVotes: 0 })
            && world.RawAi.Prompts.Count == 0, "Empty round did not autonomously finish/sync");
    }
    private static async Task QueuedAiCancellation()
    {
        using var world = new World();
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Submission.Phase == SubmissionClientPhase.Writing));
        foreach (var runtime in world.Runtimes) runtime.PrepareTranslation("붉은 과일");
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().AiPhase == RoundAiPhase.Reviewing));
        world.RawAi.BlockGeneration = true;
        foreach (var runtime in world.Runtimes) runtime.Confirm(runtime.Snapshot().Draft!.Id, "a red fruit", 42);
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Busy) && world.RawAi.Prompts.Count == 1);
        // One native call deliberately stays blocked while other approved jobs
        // wait on the shared semaphore. Cancelling the wait must not enter AI.
        await Task.Delay(30);
        Require(world.RawAi.Prompts.Count == 1 && world.RawAi.Maximum == 1, "Concurrent local AI entered the model");
        foreach (var runtime in world.Runtimes) runtime.CancelAi();
        world.RawAi.Release.Set();
        await world.Until(() => world.Runtimes.All(r => !r.Snapshot().Busy));
        Require(world.RawAi.Prompts.Count == 1 && world.Runtimes.All(r => r.Snapshot().OwnPng is null), "Cancelled queued AI ran/uploaded");
    }
    private static async Task GenerationDeadline()
    {
        using var world = new World(policy: MultiplayerRoundPolicy.Default with { GenerationDuration = TimeSpan.FromSeconds(1) });
        var runtime = world.Runtimes[1];
        await world.Until(() => runtime.Snapshot().Submission.Phase == SubmissionClientPhase.Writing);
        runtime.PrepareTranslation("붉은 과일");
        await world.Until(() => runtime.Snapshot().AiPhase == RoundAiPhase.Reviewing);
        world.RawAi.BlockGeneration = true; runtime.Confirm(runtime.Snapshot().Draft!.Id, "a red fruit", 42);
        await world.Until(() => world.RawAi.Prompts.Count == 1);
        world.Clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var r in world.Runtimes) r.Tick();
        world.RawAi.Release.Set();
        await world.Until(() => !runtime.Snapshot().Busy && world.Runtimes[0].Snapshot().Submission.Players[1].State == PlayerRoundState.GenerationTimedOut);
        Require(runtime.Snapshot().OwnPng is null, "Generation deadline accepted late image");
    }
    private static async Task OneImageRound()
    {
        using var world = new World(policy: MultiplayerRoundPolicy.Default with { WritingDuration = TimeSpan.FromSeconds(5) });
        var runtime = world.Runtimes[0];
        await world.Until(() => runtime.Snapshot().Submission.Phase == SubmissionClientPhase.Writing);
        runtime.PrepareTranslation("붉은 과일");
        await world.Until(() => runtime.Snapshot().AiPhase == RoundAiPhase.Reviewing);
        runtime.Confirm(runtime.Snapshot().Draft!.Id, "a red fruit", 42);
        await world.Until(() => runtime.Snapshot().Submission.UploadEnd == ImageSendEnd.Complete);
        world.Clock.Advance(TimeSpan.FromSeconds(6));
        await world.Until(() => world.Runtimes.All(r => r.Snapshot().Gallery.Result is not null));
        Require(world.Runtimes.All(r => r.Snapshot().Gallery is { Artworks.Count: 1, Result.Reason: VotingEnd.InsufficientImages, Result.AcceptedVotes: 0 }),
            "Single-image final state arrived before gallery and was lost");
    }
    private static void ProgressValidation()
    {
        Guid m = Guid.NewGuid(), r = Guid.NewGuid(); var clock = new ControlTransportChecks.Clock();
        VotingPlayer[] players = [new("p1", "방장"), new("p2", "참가자")];
        using var host = new MultiplayerRoundHost(m, r, 5, "p1", Word, Compatibility, players.Select(p => new MultiplayerParticipant(p, Compatibility)), clock: clock);
        using var client = new MultiplayerSubmissionClient(m, r, 5, "p1", "p2", players, Compatibility, [Word], clock);
        client.Dispatch("p1", RoundControlMessages.Encode(host.StartMessage("p2")));
        var progress = new RoundProgressMessage(m, r, 5, "p2", 1, MultiplayerRoundPhase.Collecting,
            players.Select(p => new PlayerRoundProgress(p.Id, PlayerRoundState.Writing)).ToArray(), TimeSpan.FromSeconds(120));
        Require(client.Dispatch("p2", RoundControlMessages.Encode(progress)).Status == RoundSyncStatus.WrongSender, "Peer forged progress");
        Require(client.Dispatch("p1", RoundControlMessages.Encode(progress)).Status == RoundSyncStatus.Accepted, "Progress rejected");
        clock.Advance(TimeSpan.FromSeconds(5)); client.Dispatch("p1", RoundControlMessages.Encode(progress));
        Require(client.Snapshot().WritingRemaining == TimeSpan.FromSeconds(115), "Progress replay extended timer");
        var next = progress with { Revision = 2, Players = [new("p1", PlayerRoundState.Generating), new("p2", PlayerRoundState.Writing)] };
        client.Dispatch("p1", RoundControlMessages.Encode(next));
        Require(client.Dispatch("p1", RoundControlMessages.Encode(progress)).Status == RoundSyncStatus.Stale
            && client.Dispatch("p1", RoundControlMessages.Encode(progress with { Revision = 3 })).Status == RoundSyncStatus.Conflict, "Progress rollback accepted");
    }
    private static void TransportBounds()
    {
        var network = new LocalRoundNetwork(); using var a = network.Connect("a"); using var b = network.Connect("b");
        var payload = new byte[1024]; for (int i = 0; i < 256; i++) Require(a.TrySend("b", RoundPacketKind.ImageToHost, payload), "Bounded queue filled early");
        Require(!a.TrySend("b", RoundPacketKind.ImageToHost, payload) && !a.TrySend("b", RoundPacketKind.ImageToHost, new byte[1025]), "Local transport queue unbounded");
        payload[0] = 1; var packets = b.Receive(128);
        Require(packets.Count == 128 && packets.All(p => p.AuthenticatedSender == "a" && p.Bytes[0] == 0), "Transport identity/byte ownership changed");
        b.Dispose(); Require(!a.IsConnected("b") && !a.TrySend("b", RoundPacketKind.ControlToClient, [1]), "Closed transport still connected");
    }
}
