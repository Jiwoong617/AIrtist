using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

// Launched as two or three independent EXEs by check-local.ps1. EOS is opt-in.
internal static class LocalProcessSmoke
{
    internal sealed class OfflineFactory : IOnlineConnectionFactory
    {
        public IOnlineConnection Connect(string path, string name, string hash) => throw new IOException("Diagnostic offline backend");
        public void Dispose() { }
    }
    internal static async Task<object> Run(RuntimeAssets assets, string root, int index, bool realAi, bool onlineHost, int participants = 3)
    {
        if (participants is not (2 or 3) || index < 0 || index >= participants) throw new ArgumentOutOfRangeException(nameof(participants));
        string[] names = ["붓쟁이", "그림친구", "별빛"];
        int[] expectedVotes = participants == 2 ? [1, 1] : [0, 1, 2];
        string output = Path.Combine(root, "player-" + index); Directory.CreateDirectory(output);
        string wordsPath = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
        var compatibility = MultiplayerCompatibility.FromVerifiedAssets(assets, wordsPath);
        IDrawingAi ai = realAi ? new SerializedDrawingAi(new LocalDrawingAi(assets)) : new OnlineSmoke.Ai();
        if (index == 0) await LocalConnectionChecks.Run(assets);
        var worker = new OnlineSessionWorker(compatibility, WordCatalog.Load(wordsPath), ai,
            new AutomaticRoomFactory(index == 0 && onlineHost ? new EosOnlineConnectionFactory() : new OfflineFactory()));
        var shell = new MenuWindow(assets, Path.Combine(output, "settings.json"), ai, worker);
        shell.Show();
        var room = await shell.SmokeMultiplayerAsync();
        room.SmokeNickname(names[index]);
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
                room.SmokeLocalCreate(rounds, participants);
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
            await Until(() => worker.Snapshot.Room?.Membership?.Members.Count == participants, "all-process-members");
            var roster = worker.Snapshot.Room!.Membership!.Members.Order(StringComparer.Ordinal).ToArray();
            string id = worker.Snapshot.LocalId!;
            OnlineSmoke.Require(roster.Distinct().Count() == participants && roster.Contains(id), "Process identities not independent");
            await Until(() => worker.Snapshot.Waiting?.Players.All(p => p.Name != "접속 중") == true, "nicknames");
            OnlineSmoke.Require(worker.Snapshot.Waiting!.Players.Select(p => p.Name).Order().SequenceEqual(names.Take(participants).Order()), "Nicknames not synchronized");
            shell.SmokeSettings(index != 1, Path.Combine(output, "waiting-settings.png"));
            await shell.SmokeLicensesAsync(Path.Combine(output, "licenses-waiting.png"));
            MenuWindow.Capture(shell, Path.Combine(output, "room.png"));
            string beforeSettings = worker.Snapshot.Waiting!.Revision;
            if (index != 0) room.SmokeWaitingReady();
            else
            {
                await Until(() => worker.Snapshot.Waiting?.CanStart == true, "ready-all");
                room.SmokeWaitingSettings(new(6.5f, 8, 42));
            }
            await Until(() => worker.Snapshot.Waiting is { Pending: false } updated && updated.Revision != beforeSettings, "settings-published");
            OnlineSmoke.Require(worker.Snapshot.Waiting!.Settings.RoundPolicy.FixedParameters == new GenerationParameters(6.5f, 8, 42), "Room parameters not synchronized");
            OnlineSmoke.Require(worker.Snapshot.Waiting!.Players.Where(p => !p.Host).All(p => !p.Ready), "Settings retained stale readiness");
            File.WriteAllText(Path.Combine(output, "readiness-reset.txt"), "checked");
            await Until(() => Enumerable.Range(0, participants).All(i => File.Exists(Path.Combine(root, "player-" + i, "readiness-reset.txt"))), "readiness-reset-barrier");
            MenuWindow.Capture(shell, Path.Combine(output, "room-fixed.png"));
            if (index != 0) room.SmokeWaitingReady();
            else
            {
                await Until(() => worker.Snapshot.Waiting?.CanStart == true, "ready-again");
                room.SmokeWaitingReady();
            }
            await Until(() => room.Game is not null, "single-lobby-start");
            game = room.Game!;
            OnlineSmoke.Require(System.Windows.Application.Current.Windows.Count == 1, "Multiplayer opened another window");
            for (int round = 1; round <= rounds; round++)
            {
                await Until(() => worker.Snapshot.Match?.RoundNumber == round && worker.Snapshot.Match.Round?.Submission.Phase == SubmissionClientPhase.Writing, "writing-" + round);
                OnlineSmoke.Require(worker.Snapshot.Match!.Round!.Submission.FixedParameters == new GenerationParameters(6.5f, 8, 42), "Match lost fixed room parameters");
                shell.SmokeSettings(round % 2 == 0, Path.Combine(output, $"round-{round}-settings.png"));
                var remaining = worker.Snapshot.Match!.Round!.Submission.WritingRemaining;
                await shell.SmokeLicensesAsync(Path.Combine(output, $"licenses-writing-{round}.png"), holdMs: 1100);
                OnlineSmoke.Require(worker.Snapshot.Match!.Round!.Submission.WritingRemaining < remaining, "License paused the match timer");
                game.OnlineSmokeDraw(index, index == 1 ? "길고 파란 물체" : "둥글고 붉은 물체");
                await Until(() => worker.Snapshot.Match?.Round?.Gallery.CanVote == true, "gallery-" + round, realAi ? 300 : 45);
                var artworks = worker.Snapshot.Match!.Round!.Gallery.Artworks;
                OnlineSmoke.Require(artworks.Count == participants, "Gallery missing peer PNG");
                if (realAi) OnlineSmoke.Require(artworks.Select(a => a.Sha256).Distinct().Count() >= 2, "Different descriptions produced identical diagnostic images");
                galleryHashes.Add(artworks.OrderBy(a => a.OwnerId, StringComparer.Ordinal).Select(a => a.Sha256).ToArray());
                foreach (var art in artworks) File.WriteAllBytes(Path.Combine(output, $"round-{round}-{art.OwnerId}.png"), art.CopyPng());
                game.OnlineSmokeRender(Path.Combine(output, $"gallery-{round}.png"));
                await shell.SmokeLicensesAsync(Path.Combine(output, $"licenses-gallery-{round}.png"));
                await Task.Delay(150); // Let the audio thread consume the newly displayed gallery transition.
                int self = Array.IndexOf(roster, id); int[] targets = [1, 0, 1];
                game.OnlineSmokeVote(roster[targets[self]]);
                await Until(() => worker.Snapshot.Match!.Results.Count == round, "votes-" + round);
                OnlineSmoke.Require(worker.Snapshot.Match!.Results[round - 1].Result.Tallies.Select(t => t.Votes).Order().SequenceEqual(expectedVotes), "Vote tallies differ");
                game.OnlineSmokeRender(Path.Combine(output, $"result-{round}.png"));
                await Task.Delay(200);
                if (index == 0 && round < rounds)
                { await Until(() => worker.Snapshot.Match!.CanAdvance, "advance"); game.OnlineSmokeNext(); }
            }
            await Until(() => worker.Snapshot.Match?.Phase == MultiplayerMatchPhase.Completed, "complete");
            foreach (string cue in new[] { "round_start", "gallery_open", "round_result" })
                OnlineSmoke.Require(shell.AudioCounts.GetValueOrDefault(cue) == rounds, $"Audio {cue} expected {rounds}: {shell.AudioCounts.GetValueOrDefault(cue)}");
            OnlineSmoke.Require(shell.AudioMusicChanges == 2, "Settings/rounds restarted the music");
            game.OnlineSmokeRender(Path.Combine(output, "result.png"));
            game.OnlineSmokeFinalButton(index == 0);
            File.WriteAllText(Path.Combine(output, "complete.txt"), "complete");
            await Until(() => Enumerable.Range(0, participants).All(i => File.Exists(Path.Combine(root, "player-" + i, "complete.txt"))), "all-complete");
            if (index == 0) game.OnlineSmokeShowFinal();
            await Until(() => game.OnlineSmokeFinalVisible, "final-screen");
            await Until(() => game.OnlineSmokeFinalCanLeave, "final-delivered");
            var final = worker.Snapshot.Match!.FinalResults!;
            OnlineSmoke.Require(final.Winners.Count == (participants == 2 ? 2 : 1) &&
                final.Winners.All(p => p.Votes == expectedVotes.Max() * rounds) && final.TotalVotes == participants * rounds,
                "Final cumulative winner/tie differs");
            shell.SmokeSettings(index != 1, Path.Combine(output, "final-settings.png"));
            await shell.SmokeLicensesAsync(Path.Combine(output, "licenses-final.png"));
            MenuWindow.Capture(shell, Path.Combine(output, "final-results.png"));
            File.WriteAllText(Path.Combine(output, "final.json"), JsonSerializer.Serialize(final, RuntimeAssets.Json));
            File.WriteAllText(Path.Combine(output, "final-shown.txt"), "shown");
            await Until(() => Enumerable.Range(0, participants).All(i => File.Exists(Path.Combine(root, "player-" + i, "final-shown.txt"))), "all-final-shown");
            if (index == 0)
            {
                await Until(() => game.OnlineSmokeFinalCanLeave, "final-delivered");
                game.OnlineSmokeFinalLeave();
                await Until(() => shell.SmokeAtMainMenu, "host-main-menu");
            }
            else
            {
                await Until(() => worker.Snapshot.Phase == OnlinePhase.Failed, "host-left", allowFailure: true);
                await Task.Delay(550);
                OnlineSmoke.Require(game.OnlineSmokeFinalVisible && game.OnlineSmokeFinalCanLeave &&
                    worker.Snapshot.Match?.Results.Count == rounds && shell.AudioCounts.GetValueOrDefault("error_notice") == 0,
                    "Host departure dismissed or disrupted final screen");
                MenuWindow.Capture(shell, Path.Combine(output, "final-after-host-left.png"));
                game.OnlineSmokeFinalLeave();
                await Until(() => shell.SmokeAtMainMenu, "guest-main-menu", allowFailure: true);
            }
            MenuWindow.Capture(shell, Path.Combine(output, "back-at-main.png"));
            OnlineSmoke.Require(System.Windows.Application.Current.Windows.Count == 1, "Final navigation created another window");
            if (index == 0 && onlineHost)
            {
                // Without a same-PC registration, the unchanged code-entry UI must fall back
                // to real EOS and allow correction of a closed/nonexistent code.
                room = await shell.SmokeMultiplayerAsync();
                room.SmokeJoinCode(File.ReadAllText(codeFile));
                await Until(() => worker.Snapshot.Room?.State == EosLobbyState.Idle &&
                    worker.Snapshot.Room.Message.Contains("참가 가능한 방이 없습니다"), "automatic-eos-code-fallback", 40);
                room.SmokeVerifyJoinRetry(); await worker.DisconnectAsync();
            }
            return new { status = "passed", index, processId = Environment.ProcessId, id, roster, rounds, realAi, eos = onlineHost && index == 0,
                singleWindow = true, nicknames = true, settingsResetReadiness = true, fixedParameters = true,
                licensesEverywhere = true, licenseKeepsTimerRunning = true,
                tcpLoopback = true, boundaryChecks = index == 0, automaticEosFallback = onlineHost && index == 0,
                galleryHashes, votes = expectedVotes, galleryAccessibility = true, hostLeave = true,
                finalResults = final, finalScreenSynchronized = true, finalSurvivedHostLeave = index != 0, returnedToMain = true,
                audio = shell.AudioCounts, musicChanges = shell.AudioMusicChanges, elapsedSeconds = total.Elapsed.TotalSeconds };
        }
        finally { await room.CloseAsync(); shell.Close(); await worker.StopAsync(); }
    }
}
