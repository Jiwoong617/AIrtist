using AIrtist.Eos;
using System.Diagnostics;
using Epic.OnlineServices;

if (args.Length == 0 || args[0] is not ("native" or "settings" or "p2p" or "lobby" or "login" or "live-lobby"))
{
    Console.WriteLine("native | settings <검사 출력 폴더> | p2p | lobby | login <eos.local.json 경로> [표시 이름] | live-lobby <eos.local.json 경로>");
    return 2;
}
try
{
    if (args[0] == "p2p") { P2PChecks.Run(); return 0; }
    if (args[0] == "lobby") { LobbyChecks.Run(); return 0; }
    if (args[0] == "settings")
    {
        if (args.Length != 2) return 2;
        SettingsChecks.Run(args[1]);
        return 0;
    }
    if (args[0] == "native")
    {
        // Real native load/init/shutdown. No device creation or service login.
        using (var library = new EosLibrary())
        {
            Console.WriteLine($"EOS native 초기화: {library.RuntimeVersion}");
            // Exercise wrapper ownership without starting an online operation.
            var baseline = Helper.GetAllocationCount();
            var firstOwner = new object();
            var otherOwner = new object();
            Helper.AddCallback(out var firstKey, firstOwner, (Action)(() => { }));
            Helper.AddCallback(out var otherKey, otherOwner, (Action)(() => { }));
            var withBoth = Helper.GetAllocationCount();
            Helper.ReleaseCallbacksAfterPlatformRelease(firstOwner);
            if (Helper.GetAllocationCount() != withBoth - 2) throw new InvalidOperationException("다른 소유자 콜백이 제거되었거나 정리되지 않았습니다.");
            Helper.ReleaseCallbacksAfterPlatformRelease(firstOwner);
            Helper.RemoveCallback(otherKey);
            if (Helper.GetAllocationCount() != baseline) throw new InvalidOperationException("C# 콜백 참조가 남았습니다.");
            var refused = false;
            try { using var duplicate = new EosLibrary(); }
            catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("중복 SDK 소유권을 허용했습니다.");
            var otherThread = Task.Run(() =>
            {
                try { library.Dispose(); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            if (!otherThread) throw new InvalidOperationException("다른 스레드의 SDK 종료를 허용했습니다.");
        }
        var restartRefused = false;
        try { using var restarted = new EosLibrary(); }
        catch (InvalidOperationException) { restartRefused = true; }
        if (!restartRefused) throw new InvalidOperationException("종료 후 SDK 재초기화를 허용했습니다.");
        Console.WriteLine("PASS: native 초기화/종료, 중복 소유권·스레드·종료 후 재초기화 거절, 소유자별 콜백 참조 정리. 온라인 인증은 검사하지 않았습니다.");
        return 0;
    }
    if (args.Length < 2 || (args[0] == "live-lobby" && args.Length != 2)) return 2;
    // Validate before network work; print neither credentials nor participant IDs.
    var settings = EosSettings.Load(args[1]);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        using var library = new EosLibrary();
        using var session = new EosDeviceSession(library, settings, args.Length > 2 ? args[2] : "AIrtist Tester");
        session.StartLogin();
        EosConnectionState? previous = null;
        var elapsed = Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested && elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            session.Tick();
            var state = session.Snapshot;
            if (state.State != previous) { Console.WriteLine($"{state.State}: {state.Message}"); previous = state.State; }
            if (state.State == EosConnectionState.Connected)
            {
                Console.WriteLine("PASS: 실제 Connect 기기 ID 로그인 완료.");
                if (args[0] == "live-lobby") LiveLobbyChecks.Run(session, cancellation.Token);
                else Console.WriteLine("로비/P2P 검사는 별도입니다.");
                return 0;
            }
            if (state.State == EosConnectionState.Failed) return 1;
            Thread.Sleep(20); // Console network worker, never the WPF UI thread.
        }
        Console.WriteLine("EOS 인증 검사 취소 또는 시간 초과");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
catch (Exception error)
{
    // Known messages contain only field names / enum codes. Never print SDK logs, tokens or raw JSON errors.
    Console.WriteLine(error is InvalidDataException or InvalidOperationException or PlatformNotSupportedException
        ? error.Message : $"EOS 검사 실패: {error.GetType().Name}");
    return 1;
}
