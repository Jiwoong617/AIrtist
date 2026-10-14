using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// Launched as three independent EXEs by check-local.ps1. No EOS service access.
internal static class LocalProcessSmoke
{
    internal sealed class OfflineFactory : IOnlineConnectionFactory
    {
        public IOnlineConnection Connect(string path, string name, string hash) => throw new IOException("Diagnostic offline backend");
        public void Dispose() { }
    }
    internal static async Task<object> Run(RuntimeAssets assets, string root, int index, bool realAi, bool onlineHost)
    {
        string output = Path.Combine(root, "player-" + index); Directory.CreateDirectory(output);
        string wordsPath = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
        var compatibility = MultiplayerCompatibility.FromVerifiedAssets(assets, wordsPath);
        IDrawingAi ai = realAi ? new SerializedDrawingAi(new LocalDrawingAi(assets)) : new OnlineSmoke.Ai();
        if (index == 0) await LocalConnectionChecks.Run(assets);
        var worker = new OnlineSessionWorker(compatibility, WordCatalog.Load(wordsPath), ai,
            new AutomaticRoomFactory(index == 0 && onlineHost ? new EosOnlineConnectionFactory() : new OfflineFactory()));
        var room = new OnlineRoomWindow(assets, ai, worker);
        RoundPreviewWindow? game = null; int rounds = realAi ? 1 : 2;
        string codeFile = Path.Combine(root, "room-code.txt"); var galleryHashes = new List<string[]>();
        var total = Stopwatch.StartNew();
        async Task Until(Func<bool> predicate, string step, int seconds = 30, bool allowFailure = false)
        {
            File.WriteAllText(Path.Combine(output, "progress.txt"), step);
            var watch = Stopwatch.StartNew();
            while (!predicate())
            {
                var state = worker.Snapshot;
                if (!allowFailure && (state.Phase == OnlinePhase.Failed || state.Error is not null || state.Match?.Phase == MultiplayerMatchPhase.Stopped))
                    throw new InvalidOperationException(step + ": " + (state.Error ?? state.Message));
                if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(step);
                await Task.Delay(30);
            }
        }
        try
        {
            if (index == 0)
            {
                room.SmokeLocalCreate(rounds);
                await Until(() => worker.Snapshot.Room?.State == EosLobbyState.Joined, "create");
                if (onlineHost) await Until(() => worker.Snapshot.Room?.Message == "방 참가 완료", "eos-mirror-created", 40);
                string code = RoomCode.FromLobbyId(worker.Snapshot.Room!.Membership!.Lobby.LobbyId)!;
                File.WriteAllText(codeFile + ".tmp", code); File.Move(codeFile + ".tmp", codeFile);
            }
            else
            {
                await Until(() => File.Exists(codeFile), "wait-code");
                room.SmokeJoinCode(File.ReadAllText(codeFile));
            }
            await Until(() => worker.Snapshot.Room?.Membership?.Members.Count == 3, "three-process-members");
            var roster = worker.Snapshot.Room!.Membership!.Members.Order(StringComparer.Ordinal).ToArray();
            string id = worker.Snapshot.LocalId!;
            OnlineSmoke.Require(roster.Distinct().Count() == 3 && roster.Contains(id), "Process identities not independent");
            room.SmokeRender(Path.Combine(output, "room.png"));
            room.SmokeLocalPrepare();
            await Until(() => worker.Snapshot.Lobby?.Lobby is not null, "connection-barrier");
            game = new RoundPreviewWindow(assets, ai, worker);
            game.OnlineSmokeReady(index == 0);
            if (index == 0)
            {
                await Until(() => worker.Snapshot.Lobby!.CanStart, "ready-all"); game.OnlineSmokeStart();
            }
            for (int round = 1; round <= rounds; round++)
            {
                await Until(() => worker.Snapshot.Match?.RoundNumber == round && worker.Snapshot.Match.Round?.Submission.Phase == SubmissionClientPhase.Writing, "writing-" + round);
                game.OnlineSmokeDraw(index);
                await Until(() => worker.Snapshot.Match?.Round?.Gallery.CanVote == true, "gallery-" + round, realAi ? 300 : 45);
                var artworks = worker.Snapshot.Match!.Round!.Gallery.Artworks;
                OnlineSmoke.Require(artworks.Count == 3, "Gallery missing peer PNG");
                galleryHashes.Add(artworks.OrderBy(a => a.OwnerId, StringComparer.Ordinal).Select(a => a.Sha256).ToArray());
                foreach (var art in artworks) File.WriteAllBytes(Path.Combine(output, $"round-{round}-{art.OwnerId}.png"), art.CopyPng());
                game.OnlineSmokeRender(Path.Combine(output, $"gallery-{round}.png"));
                int self = Array.IndexOf(roster, id); int[] targets = [1, 0, 1];
                game.OnlineSmokeVote(roster[targets[self]]);
                await Until(() => worker.Snapshot.Match!.Results.Count == round, "votes-" + round);
                OnlineSmoke.Require(worker.Snapshot.Match!.Results[round - 1].Result.Tallies.Select(t => t.Votes).Order().SequenceEqual(new[] { 0, 1, 2 }), "Vote tallies differ");
                if (index == 0 && round < rounds)
                { await Until(() => worker.Snapshot.Match!.CanAdvance, "advance"); game.OnlineSmokeNext(); }
            }
            await Until(() => worker.Snapshot.Match?.Phase == MultiplayerMatchPhase.Completed, "complete");
            game.OnlineSmokeRender(Path.Combine(output, "result.png"));
            File.WriteAllText(Path.Combine(output, "complete.txt"), "complete");
            await Until(() => Enumerable.Range(0, 3).All(i => File.Exists(Path.Combine(root, "player-" + i, "complete.txt"))), "all-complete");
            if (index == 0) await worker.DisconnectAsync();
            else
            {
                await Until(() => worker.Snapshot.Phase == OnlinePhase.Failed, "host-left", allowFailure: true);
                OnlineSmoke.Require(worker.Snapshot.Match?.Results.Count == rounds, "Disconnect lost results");
            }
            if (index == 0 && onlineHost)
            {
                // Without a same-PC registration, the unchanged code-entry UI must fall back
                // to real EOS and allow correction of a closed/nonexistent code.
                room.SmokeJoinCode(File.ReadAllText(codeFile));
                await Until(() => worker.Snapshot.Room?.State == EosLobbyState.Idle &&
                    worker.Snapshot.Room.Message.Contains("참가 가능한 방이 없습니다"), "automatic-eos-code-fallback", 40);
                room.SmokeVerifyJoinRetry(); await worker.DisconnectAsync();
            }
            return new { status = "passed", index, processId = Environment.ProcessId, id, roster, rounds, realAi, eos = onlineHost && index == 0,
                tcpLoopback = true, boundaryChecks = index == 0, automaticEosFallback = onlineHost && index == 0,
                galleryHashes, votes = new[] { 0, 1, 2 }, hostLeave = true, elapsedSeconds = total.Elapsed.TotalSeconds };
        }
        finally { await worker.StopAsync(); game?.Close(); room.Close(); }
    }
}
