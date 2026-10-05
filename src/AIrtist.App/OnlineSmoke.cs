using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// Explicit diagnostic mode only: no EOS service calls or real AI generation.
internal static class OnlineSmoke
{
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Online smoke timeout"); await Task.Delay(30); }
    }
    private sealed class Ai : IDrawingAi
    {
        public int Generated;
        public string Translate(string input, CancellationToken token) { token.ThrowIfCancellationRequested(); return "a round red object"; }
        public byte[] Generate(string input, int seed, CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Generated);
            var pixels = new byte[32 * 32 * 3]; new Random(seed).NextBytes(pixels);
            var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 96);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
    }
    internal static async Task<object> Run(RuntimeAssets assets, string output)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
        var words = WordCatalog.Load(path); var compatibility = MultiplayerCompatibility.FromVerifiedAssets(assets, path);
        var ai = new Ai();
        // Invalid local config is checked before native initialization. Never contact a service.
        var missing = new OnlineSessionWorker(compatibility, words, ai);
        var room = new OnlineRoomWindow(assets, ai, missing, Path.Combine(output, "missing.json"));
        try
        {
            room.SmokeConnect(); await Until(() => missing.Snapshot.Phase == OnlinePhase.Failed);
            room.SmokeRender(Path.Combine(output, "online-settings-missing.png"));
            Require(!missing.Snapshot.HasSession && missing.Snapshot.Message.Contains("설정"), "Missing configuration did not fail safely");
            await missing.DisconnectAsync();
            string malformed = Path.Combine(output, "invalid-config.json");
            File.WriteAllText(malformed, "{\"PRIVATE_SENTINEL\": \"DO_NOT_ECHO\"}");
            missing.Post(new(OnlineAction.Connect, missing.Snapshot.Generation, malformed, "검사"));
            await Until(() => missing.Snapshot.Phase == OnlinePhase.Failed);
            Require(!missing.Snapshot.Message.Contains("PRIVATE_SENTINEL") && !missing.Snapshot.Message.Contains("DO_NOT_ECHO"), "Settings content leaked");
        }
        finally { room.Close(); await missing.StopAsync(); }

        // Regression: already-completed shutdown must not synchronously reenter WPF Closing.
        var stoppedRoom = new OnlineRoomWindow(assets, ai, missing, "unused");
        var stoppedMain = new MainWindow(assets);
        stoppedMain.SmokeAttachStoppedOnlineWorker(missing);
        bool roomClosed = false, mainClosed = false;
        stoppedRoom.Closed += (_, _) => roomClosed = true;
        stoppedMain.Closed += (_, _) => mainClosed = true;
        stoppedRoom.Close(); stoppedMain.Close();
        await Until(() => roomClosed && mainClosed);

        using var world = new World(compatibility.ConnectionHash());
        var workers = world.Factories.Select(f => new OnlineSessionWorker(compatibility, words, ai, f)).ToArray();
        var windows = new List<RoundPreviewWindow>();
        try
        {
            for (int i = 0; i < 3; i++) workers[i].Post(new(OnlineAction.Connect, 0, "mock", "검사"));
            await Until(() => workers.All(w => w.Snapshot.Phase == OnlinePhase.Connected));
            // Stop one backend Tick to prove the UI queue is bounded, without blocking Dispatcher.
            world.Factories[0].Block = true;
            await Until(() => world.Factories[0].Entered.IsSet);
            for (int i = 0; i < 32; i++) Require(workers[0].Post(new(OnlineAction.Search, workers[0].Snapshot.Generation)), "Queue rejected below limit");
            Require(!workers[0].Post(new(OnlineAction.Search, workers[0].Snapshot.Generation)), "Queue overflow accepted");
            world.Factories[0].Block = false; world.Factories[0].Release.Set();
            await Until(() => world.Factories[0].Searches == 32);
            Require(!workers[0].Post(new(OnlineAction.Translate, workers[0].Snapshot.Generation, new('x', 4097))), "Oversize command accepted");
            // Search/create/join pass through the worker boundary.
            workers[0].Post(new(OnlineAction.Create, workers[0].Snapshot.Generation, Number: 3));
            for (int i = 1; i < 3; i++) workers[i].Post(new(OnlineAction.Join, workers[i].Snapshot.Generation, "mock-room"));
            await Until(() => workers.All(w => w.Snapshot.Room?.State == EosLobbyState.Joined));
            for (int i = 0; i < 2; i++) workers[i].Post(new(OnlineAction.Prepare, workers[i].Snapshot.Generation));
            await Until(() => workers.Take(2).All(w => w.Snapshot.Connection is not null));
            await Task.Delay(200); Require(workers.All(w => w.Snapshot.Lobby is null), "Partial connection opened Core lobby");
            workers[2].Post(new(OnlineAction.Prepare, workers[2].Snapshot.Generation));
            await Until(() => workers.All(w => w.Snapshot.Lobby?.Lobby is not null));
            windows.AddRange(workers.Select(w => new RoundPreviewWindow(assets, ai, w)));
            windows[0].OnlineSmokeSettings();
            await Until(() => workers.All(w => w.Snapshot.Lobby?.Lobby?.Settings.RoundCount == 2));
            for (int i = 0; i < 3; i++) windows[i].OnlineSmokeReady(i == 0);
            await Until(() => workers[0].Snapshot.Lobby?.CanStart == true);
            windows[0].OnlineSmokeRender(Path.Combine(output, "online-lobby-ready.png"));
            windows[1].OnlineSmokeRender(Path.Combine(output, "online-guest-ready.png"));
            // Core still enforces host authority for queued commands, independently of disabled buttons.
            workers[1].Post(new(OnlineAction.Start, workers[1].Snapshot.Generation, Revision: workers[1].Snapshot.Lobby!.Lobby!.Revision));
            await Until(() => workers[1].Snapshot.Error is not null);
            Require(workers.All(w => w.Snapshot.Match is null), "Guest started match");
            windows[0].OnlineSmokeStart();
            for (int round = 1; round <= 2; round++)
            {
                await Until(() => workers.All(w => w.Snapshot.Match?.RoundNumber == round && w.Snapshot.Match.Round?.Submission.Phase == SubmissionClientPhase.Writing));
                foreach (var window in windows) window.OnlineSmokeTranslate();
                await Until(() => workers.All(w => w.Snapshot.Match?.Round?.AiPhase == RoundAiPhase.Reviewing));
                windows[0].OnlineSmokeRender(Path.Combine(output, $"online-{round}-review.png"));
                foreach (var window in windows) window.OnlineSmokeConfirm();
                await Until(() => workers.All(w => w.Snapshot.Match?.Round?.Gallery.CanVote == true));
                windows[0].OnlineSmokeRender(Path.Combine(output, $"online-{round}-gallery.png"));
                int[] targets = [1, 0, 1];
                for (int i = 0; i < 3; i++) windows[i].OnlineSmokeVote(world.Members[targets[i]].PlayerId);
                await Until(() => workers.All(w => w.Snapshot.Match?.Results.Count == round));
                Require(workers.All(w => w.Snapshot.Match!.Results[round - 1].Result.Tallies.Select(t => t.Votes).Order().SequenceEqual(new[] { 0, 1, 2 })), "Vote totals differ");
                windows[0].OnlineSmokeRender(Path.Combine(output, $"online-{round}-result.png"));
                if (round == 1)
                {
                    await Until(() => workers[0].Snapshot.Match?.CanAdvance == true);
                    windows[0].OnlineSmokeNext();
                }
            }
            await Until(() => workers.All(w => w.Snapshot.Match?.Phase == MultiplayerMatchPhase.Completed));
            Require(ai.Generated == 6, "Generation count wrong");
            // Failure keeps confirmed results visible and closes command access.
            world.Factories[0].ThrowTick = true;
            await Until(() => workers[0].Snapshot.Phase == OnlinePhase.Failed);
            Require(workers[0].Snapshot.Match?.Results.Count == 2, "Connection failure lost result history");
            Require(!workers[0].Snapshot.Message.Contains("PRIVATE_SENTINEL"), "Backend exception leaked");
            windows[0].OnlineSmokeRender(Path.Combine(output, "online-disconnected.png"), small: true);
            long old = workers[0].Snapshot.Generation;
            await Task.WhenAll(workers.Select(w => w.DisconnectAsync()));
            // Login again on the same worker/factory and reject commands from the old epoch.
            workers[0].Post(new(OnlineAction.Connect, workers[0].Snapshot.Generation, "mock", "검사"));
            await Until(() => workers[0].Snapshot.Phase == OnlinePhase.Connected);
            int before = world.Factories[0].Creates;
            workers[0].Post(new(OnlineAction.Create, old, Number: 3));
            workers[0].Post(new(OnlineAction.Search, workers[0].Snapshot.Generation));
            await Until(() => world.Factories[0].Searches == 33);
            Require(world.Factories[0].Creates == before && workers[0].Snapshot.Lobby is null && workers[0].Snapshot.Match is null, "Stale session command/state reused");
            // Leave bypasses a full command queue and discards pending mutations.
            world.Factories[0].Entered.Reset(); world.Factories[0].Release.Reset(); world.Factories[0].Block = true;
            await Until(() => world.Factories[0].Entered.IsSet);
            for (int i = 0; i < 32; i++) workers[0].Post(new(OnlineAction.Create, workers[0].Snapshot.Generation, Number: 3));
            Task disconnect = workers[0].DisconnectAsync();
            Require(!workers[0].Post(new(OnlineAction.Search, workers[0].Snapshot.Generation)), "Command accepted during disconnect");
            world.Factories[0].Block = false; world.Factories[0].Release.Set(); await disconnect;
            Require(world.Factories[0].Creates == before, "Queued mutation survived disconnect");
            // A service that never acknowledges leave cannot keep the window alive forever.
            workers[0].Post(new(OnlineAction.Connect, workers[0].Snapshot.Generation, "mock", "검사"));
            await Until(() => workers[0].Snapshot.Phase == OnlinePhase.Connected);
            workers[0].Post(new(OnlineAction.Create, workers[0].Snapshot.Generation, Number: 3));
            await Until(() => workers[0].Snapshot.Room?.State == EosLobbyState.Joined);
            world.Factories[0].IgnoreLeave = true;
            await workers[0].DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Require(workers[0].Snapshot.Phase == OnlinePhase.Offline && workers[0].Snapshot.Message.Contains("확인하지 못"), "Unconfirmed leave reported success");
        }
        finally
        {
            foreach (var factory in world.Factories) { factory.Block = false; factory.Release.Set(); }
            foreach (var window in windows) window.Close();
            await Task.WhenAll(workers.Select(w => w.StopAsync()));
        }
        Require(world.Factories.All(f => f.ThreadId != Environment.CurrentManagedThreadId && f.ThreadId != 0 && f.Disposed && f.Created == f.Closed), "Thread/session cleanup failed");
        Require(world.Factories.Select(f => f.ThreadId).Distinct().Count() == 3, "Workers did not use independent threads");
        Require(workers.All(w => w.Snapshot.Phase == OnlinePhase.Closed && !w.Post(new(OnlineAction.Search, w.Snapshot.Generation))), "Closed worker accepted work");
        return new { status = "passed", players = 3, rounds = 2, fakePngGenerations = ai.Generated, queueLimit = 32,
            separateNetworkThreads = 3, missingSettings = "safe failure", staleCommand = "rejected", closedWorkerWindows = "passed", realEos = false, realAi = false };
    }

    private sealed class World : IDisposable
    {
        public readonly Guid Scope = Guid.NewGuid();
        public readonly ConnectionMember[] Members = Enumerable.Range(1, 3).Select(i => new ConnectionMember(new((char)('0' + i), 32), Guid.NewGuid())).ToArray();
        public readonly IRoundTransport[] Transports;
        public readonly Factory[] Factories;
        public readonly string Hash;
        public readonly int[] Prepared = new int[3];
        public World(string hash)
        {
            Hash = hash; var network = new LocalRoundNetwork();
            Transports = Members.Select(m => network.Connect(m.PlayerId)).ToArray();
            Factories = Enumerable.Range(0, 3).Select(i => new Factory(this, i)).ToArray();
        }
        public void Dispose() { foreach (var t in Transports) t.Dispose(); foreach (var f in Factories) { f.Entered.Dispose(); f.Release.Dispose(); } }
    }
    private sealed class Factory(World world, int index) : IOnlineConnectionFactory
    {
        public int ThreadId, Created, Closed, Searches, Creates;
        public volatile bool Block, ThrowTick, IgnoreLeave;
        public bool Disposed;
        public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public void Check()
        {
            if (ThreadId == 0) ThreadId = Environment.CurrentManagedThreadId;
            Require(ThreadId == Environment.CurrentManagedThreadId && !Disposed, "Backend called from wrong thread/after disposal");
        }
        public IOnlineConnection Connect(string path, string name, string hash)
        { Check(); Created++; Require(hash == world.Hash, "Compatibility hash lost"); return new Connection(this, world, index); }
        public void Dispose() { Check(); Require(Created == Closed, "Factory shut down with live session"); Disposed = true; }
    }
    private sealed class Connection(Factory factory, World world, int index) : IOnlineConnection
    {
        private EosLobbyState state = EosLobbyState.Idle;
        private bool preparing, handed, disposed;
        private string Id => world.Members[index].PlayerId;
        private EosLobbyListing Listing => new("mock-room", world.Members[0].PlayerId, "mock", 3, 0);
        public EosConnectionSnapshot Authentication { get { factory.Check(); return new(EosConnectionState.Connected, Id, "모의 인증"); } }
        public EosLobbySnapshot Lobby { get { factory.Check(); return new(state, [Listing], state is EosLobbyState.Joined or EosLobbyState.Leaving ? new(Listing, world.Members.Select(m => m.PlayerId).ToArray()) : null, 1, "모의 방"); } }
        public LobbyBootstrapSnapshot? Bootstrap
        {
            get
            {
                factory.Check(); int count = world.Prepared.Count(p => p != 0);
                return !preparing ? null : new(handed ? LobbyBootstrapState.HandedOff : count == 3 ? LobbyBootstrapState.Ready : LobbyBootstrapState.Preparing, world.Scope, count, 3, "모의 연결 준비");
            }
        }
        public void Tick()
        {
            factory.Check(); if (factory.Block) { factory.Entered.Set(); factory.Release.Wait(); }
            if (factory.ThrowTick) { factory.ThrowTick = false; throw new InvalidOperationException("PRIVATE_SENTINEL"); }
            if (state == EosLobbyState.Leaving && !factory.IgnoreLeave) state = EosLobbyState.Idle;
        }
        public void Search() { factory.Check(); Interlocked.Increment(ref factory.Searches); }
        public void Create(int count) { factory.Check(); Interlocked.Increment(ref factory.Creates); state = EosLobbyState.Joined; }
        public void Join(string id) { factory.Check(); Require(id == "mock-room", "Join target lost"); state = EosLobbyState.Joined; }
        public void Prepare() { factory.Check(); preparing = true; Interlocked.Exchange(ref world.Prepared[index], 1); }
        public PreparedLobbyConnection TakeConnection()
        { factory.Check(); Require(Bootstrap?.State == LobbyBootstrapState.Ready, "Early/repeated handoff"); handed = true; return new(world.Scope, world.Members[0].PlayerId, Id, world.Hash, world.Members, new ThreadTransport(factory, world.Transports[index])); }
        public void Leave() { factory.Check(); state = EosLobbyState.Leaving; }
        public void Dispose() { factory.Check(); Require(!disposed, "Double session disposal"); disposed = true; factory.Closed++; }
    }
    private sealed class ThreadTransport(Factory factory, IRoundTransport inner) : IRoundTransport
    {
        public string LocalId { get { factory.Check(); return inner.LocalId; } }
        public bool IsConnected(string id) { factory.Check(); return inner.IsConnected(id); }
        public bool TrySend(string id, RoundPacketKind kind, byte[] bytes) { factory.Check(); return inner.TrySend(id, kind, bytes); }
        public IReadOnlyList<RoundInboundPacket> Receive(int count) { factory.Check(); return inner.Receive(count); }
        public void Dispose() { factory.Check(); inner.Dispose(); }
    }
}
