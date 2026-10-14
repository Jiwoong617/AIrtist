using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

internal sealed class OnlineLiveCheckException(string step) : Exception(step);

// Explicit diagnostic only. Real EOS, one participant, no AI or peer connection.
internal static class OnlineLiveSmoke
{
    internal static void Require(bool value, string step)
    { if (!value) throw new OnlineLiveCheckException(step + " 검사 실패"); }

    private sealed class NoAi : IDrawingAi
    {
        public string Translate(string input, CancellationToken token) => throw new OnlineLiveCheckException("예상하지 않은 번역 호출");
        public byte[] Generate(string input, int seed, CancellationToken token, Action<string> progress) => throw new OnlineLiveCheckException("예상하지 않은 생성 호출");
    }

    internal static async Task<object> Run(RuntimeAssets assets, string output, string configPath)
    {
        // Verify credentials before loading native SDK. Never copy them to the report directory.
        if (string.IsNullOrEmpty(configPath)) EmbeddedOnlineSettings.Load(); else EosSettings.Load(configPath);
        string wordsPath = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
        var compatibility = MultiplayerCompatibility.FromVerifiedAssets(assets, wordsPath);
        // Separate this disposable room from game discovery without changing product matching rules.
        compatibility = compatibility with { AssetsSha256 = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        var ai = new NoAi();
        var worker = new OnlineSessionWorker(compatibility, WordCatalog.Load(wordsPath), ai, new EosOnlineConnectionFactory());
        string? copiedCode = null;
        var window = new OnlineRoomWindow(assets, ai, worker, configPath, value => copiedCode = value);
        var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => windowClosed.TrySetResult();
        var total = Stopwatch.StartNew();
        int dispatcherTicks = 0;
        var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        pulse.Tick += (_, _) => dispatcherTicks++;
        var steps = new List<object>();
        void Record(string step)
        {
            steps.Add(new { step, elapsedSeconds = total.Elapsed.TotalSeconds, dispatcherTicks });
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new { realEos = true, participants = 1, steps }, RuntimeAssets.Json));
        }
        async Task Until(Func<OnlineSnapshot, bool> predicate, string step)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var state = worker.Snapshot;
                if (state.Phase is OnlinePhase.Failed or OnlinePhase.Closed || state.Error is not null)
                    throw new OnlineLiveCheckException(step + ": " + (state.Error ?? state.Message));
                if (predicate(state)) return;
                if (elapsed.Elapsed > TimeSpan.FromSeconds(40)) throw new OnlineLiveCheckException(step + " 시간 초과");
                await Task.Delay(30);
            }
        }
        pulse.Start();
        try
        {
            window.SmokeLiveCreate();
            await Until(s => s.Phase == OnlinePhase.Connected && s.Room?.State == EosLobbyState.Joined, "자동 인증·방 생성");
            string? localId = worker.Snapshot.LocalId;
            long firstGeneration = worker.Snapshot.Generation;
            Require(localId is not null && dispatcherTicks > 0, "인증 및 UI Dispatcher 진행");
            Record("authenticated");
            window.SmokeRender(Path.Combine(output, "online-authenticated.png"));

            var membership = worker.Snapshot.Room!.Membership;
            Require(membership is not null && membership.Lobby.OwnerId == localId && membership.Lobby.MaxMembers == 2 &&
                membership.Lobby.AvailableSlots == 1 && membership.Members.Count == 1 && membership.Members[0] == localId,
                "서비스 방장·정원·명단");
            string createdId = membership!.Lobby.LobbyId;
            Require(RoomCode.FromLobbyId(createdId) is not null, "실제 서비스 방 코드");
            window.SmokeLiveVerifyJoined();
            window.SmokeCopyCode();
            Require(copiedCode == RoomCode.Display(RoomCode.FromLobbyId(createdId)!), "실제 방 코드 복사");
            Record("created-and-membership-verified");
            window.SmokeRender(Path.Combine(output, "online-room-created.png"));

            window.SmokeLiveLeave();
            await Until(s => s.Phase == OnlinePhase.Offline && !s.HasSession, "방 나가기");
            Require(worker.Snapshot.Message == "접속을 종료했습니다.", "서비스 나가기 응답");
            Record("leave-confirmed");

            // Reuse the same worker/factory and native SDK lifetime with a fresh auth session.
            window.SmokeConnect();
            await Until(s => s.Phase == OnlinePhase.Connected && s.Room?.State == EosLobbyState.Idle, "재접속");
            Require(worker.Snapshot.LocalId == localId && worker.Snapshot.Generation > firstGeneration, "기기 계정 재사용·접속 세대");
            Record("reconnected-same-device");
            window.SmokeLiveSearch();
            await Until(s => s.Room?.State == EosLobbyState.Idle && s.Room.Message == "방 검색 완료", "재접속 후 검색");
            Require(worker.Snapshot.Room!.SearchResults.All(r => r.LobbyId != createdId), "나간 방 검색 제외");
            Record("left-room-absent");
            window.SmokeJoinCode(RoomCode.FromLobbyId(createdId)!);
            await Until(s => s.Room?.State == EosLobbyState.Idle && s.Room.Message.Contains("참가 가능한 방이 없습니다"), "닫힌 방 코드 거절");
            window.SmokeVerifyJoinRetry();
            Record("closed-code-exact-lookup-rejected");
            window.SmokeLiveLeave();
            await Until(s => s.Phase == OnlinePhase.Offline && !s.HasSession, "최종 접속 종료");
            Require(worker.Snapshot.Message == "접속을 종료했습니다.", "최종 종료 확인");
            window.SmokeRender(Path.Combine(output, "online-disconnected.png"));
            Record("disconnected");
        }
        finally
        {
            pulse.Stop();
            await worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
            await windowClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Require(worker.Snapshot.Phase == OnlinePhase.Closed, "네트워크 작업 스레드 종료");
        return new { status = "passed", realEos = true, realAi = false, participants = 1,
            isolation = "unique diagnostic compatibility bucket", reconnectedSameDevice = true,
            serviceLeaveConfirmed = true, workerClosed = true, windowClosed = true, dispatcherTicks, elapsedSeconds = total.Elapsed.TotalSeconds,
            multiPc = false, p2p = false, imageTransfer = false, voting = false, steps };
    }
}
