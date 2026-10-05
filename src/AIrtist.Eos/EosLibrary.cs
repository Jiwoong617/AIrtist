using Epic.OnlineServices;
using Epic.OnlineServices.Platform;
using Epic.OnlineServices.Version;

namespace AIrtist.Eos;

// EOS lifetime is process-wide. All calls including disposal belong to one network thread.
public sealed class EosLibrary : IDisposable
{
    public const string SdkVersion = "1.19.2.1";
    private static int lifetime; // 0 = never initialized, 1 = owned, 2 = terminal (no more SDK calls).
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;
    private bool hasSession;
    public string RuntimeVersion { get; }

    public EosLibrary()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("EOS 연결은 Windows x64에서 실행하세요.");
        if (Interlocked.CompareExchange(ref lifetime, 1, 0) != 0)
            throw new InvalidOperationException("EOS는 프로세스에서 한 번만 초기화할 수 있습니다.");
        var initialized = false;
        try
        {
            var options = new InitializeOptions { ProductName = "AIrtist", ProductVersion = "0.1.0" };
            var result = PlatformInterface.Initialize(ref options);
            if (result != Result.Success) throw new InvalidOperationException($"EOS 초기화 실패: {result}");
            initialized = true;
            RuntimeVersion = VersionInterface.GetVersion().ToString();
            if (!RuntimeVersion.StartsWith(SdkVersion + "-", StringComparison.Ordinal) ||
                $"{VersionInterface.MAJOR}.{VersionInterface.MINOR}.{VersionInterface.PATCH}.{VersionInterface.HOTFIX}" != SdkVersion)
                throw new InvalidOperationException("EOS native/managed SDK 버전이 일치하지 않습니다.");
        }
        catch
        {
            Interlocked.Exchange(ref lifetime, 2);
            if (initialized) PlatformInterface.Shutdown();
            throw;
        }
    }

    internal void AcquireSession()
    {
        VerifyThread();
        if (hasSession) throw new InvalidOperationException("기기 인증 세션이 이미 열려 있습니다.");
        hasSession = true;
    }

    internal void ReleaseSession() { VerifyThread(); hasSession = false; }

    internal void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("EOS는 생성한 네트워크 스레드에서만 사용하세요.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyThread();
        if (hasSession) throw new InvalidOperationException("인증 세션을 먼저 종료하세요.");
        var result = PlatformInterface.Shutdown();
        disposed = true;
        Interlocked.Exchange(ref lifetime, 2);
        // EOS forbids all SDK calls after Shutdown, including reinitialization.
        if (result != Result.Success) throw new InvalidOperationException($"EOS 종료 실패: {result}");
    }
}
