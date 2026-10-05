using System.Text;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Platform;
using AIrtist.Core;

namespace AIrtist.Eos;

public enum EosConnectionState { Ready, SigningIn, Connected, Renewing, Failed, Disposed }
public sealed record EosConnectionSnapshot(EosConnectionState State, string? PlayerId, string Message);

// Owns Platform and Connect notifications, borrowing the application's process-wide SDK lifetime.
public sealed class EosDeviceSession : IDisposable
{
    private readonly EosLibrary library;
    private readonly PlatformInterface platform;
    private readonly ConnectInterface connect;
    private readonly string displayName;
    private readonly TimeProvider clock;
    private ulong expirationId, statusId;
    private long operationStart;
    private int operation;
    private bool renewalRequested, disposed;
    private EosRoundTransport? transport;
    private EosLobbyClient? lobby;
    private LobbyConnectionBootstrap? bootstrap;
    private readonly HashSet<Guid> usedTransportScopes = new();
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    public EosConnectionSnapshot Snapshot { get; private set; } = new(EosConnectionState.Ready, null, "인증 대기");

    public EosDeviceSession(EosLibrary library, EosSettings settings, string displayName, TimeProvider? clock = null)
    {
        settings.Validate();
        if (string.IsNullOrWhiteSpace(displayName) || displayName != displayName.Trim() || displayName.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(displayName) > ConnectInterface.USERLOGININFO_DISPLAYNAME_MAX_LENGTH)
            throw new ArgumentException("표시 이름은 UTF-8 기준 1~32바이트여야 합니다.", nameof(displayName));
        this.displayName = displayName;
        this.clock = clock ?? TimeProvider.System;
        this.library = library;
        library.AcquireSession();
        try
        {
            var options = new Options
            {
                ProductId = settings.ProductId, SandboxId = settings.SandboxId, DeploymentId = settings.DeploymentId,
                ClientCredentials = new ClientCredentials { ClientId = settings.ClientId, ClientSecret = settings.ClientSecret },
                IsServer = false, Flags = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
                TickBudgetInMilliseconds = 5, TaskNetworkTimeoutSeconds = 10
                // No RTC, overlay, integrated platform or storage features.
            };
            platform = PlatformInterface.Create(ref options) ?? throw new InvalidOperationException("EOS 플랫폼 생성 실패: 제품 설정을 확인하세요.");
            connect = platform.GetConnectInterface();
            var expirationOptions = new AddNotifyAuthExpirationOptions();
            expirationId = connect.AddNotifyAuthExpiration(ref expirationOptions, this, OnAuthExpiration);
            var statusOptions = new AddNotifyLoginStatusChangedOptions();
            statusId = connect.AddNotifyLoginStatusChanged(ref statusOptions, this, OnLoginStatusChanged);
            if (expirationId == 0 || statusId == 0) throw new InvalidOperationException("EOS 인증 알림 등록 실패");
        }
        catch
        {
            if (expirationId != 0) connect!.RemoveNotifyAuthExpiration(expirationId);
            if (statusId != 0) connect!.RemoveNotifyLoginStatusChanged(statusId);
            platform?.Release();
            Helper.ReleaseCallbacksAfterPlatformRelease(this);
            library.ReleaseSession();
            throw;
        }
    }

    public void StartLogin()
    {
        VerifyActive();
        if (Snapshot.State != EosConnectionState.Ready) throw new InvalidOperationException("이 인증 세션에서는 로그인 요청을 한 번만 시작할 수 있습니다.");
        Snapshot = new(EosConnectionState.SigningIn, null, "기기 인증 준비 중");
        operationStart = clock.GetTimestamp();
        var attempt = ++operation;
        var options = new CreateDeviceIdOptions { DeviceModel = "Windows PC" };
        connect.CreateDeviceId(ref options, this, (ref CreateDeviceIdCallbackInfo info) =>
        {
            if (!IsCurrent(attempt)) return;
            if (info.ResultCode is Result.Success or Result.DuplicateNotAllowed) Login(attempt);
            else Fail("기기 인증 준비 실패", info.ResultCode);
        });
    }

    public void Tick()
    {
        VerifyActive();
        CheckTimeout();
        platform.Tick();
        if (renewalRequested && Snapshot.State == EosConnectionState.Connected)
        {
            renewalRequested = false;
            Snapshot = Snapshot with { State = EosConnectionState.Renewing, Message = "인증 갱신 중" };
            operationStart = clock.GetTimestamp();
            Login(++operation);
        }
        CheckTimeout();
        lobby?.Pump();
        transport?.Pump();
        bootstrap?.Tick();
    }

    public EosLobbyClient CreateLobbyClient(string compatibilityHash)
    {
        VerifyActive();
        EosLobbyClient.ValidateCompatibility(compatibilityHash);
        if (Snapshot.State != EosConnectionState.Connected || Snapshot.PlayerId is null)
            throw new InvalidOperationException("실제 EOS 기기 인증 후 로비를 시작하세요.");
        if (lobby is not null) throw new InvalidOperationException("인증 세션당 로비 연결부는 한 번만 만듭니다.");
        lobby = new(Snapshot.PlayerId, compatibilityHash, new EosSdkLobbyApi(platform, Snapshot.PlayerId, this),
            () => transport?.Fail("EOS 로비 참가 상태가 변경되어 새 연결이 필요합니다."), clock);
        return lobby;
    }

    // The coordinator must agree a fresh shared scope first. Never accept a caller/packet-supplied roster.
    internal EosRoundTransport CreateRoundTransport(Guid sharedScopeId, long expectedMembershipRevision)
    {
        VerifyActive();
        var room = lobby?.Snapshot;
        if (Snapshot.State != EosConnectionState.Connected || Snapshot.PlayerId is null ||
            room?.State != EosLobbyState.Joined || room.Membership is null || room.MembershipRevision != expectedMembershipRevision)
            throw new InvalidOperationException("인증된 현재 로비 명단과 변경 번호를 확인한 뒤 연결하세요.");
        var context = new EosTransportContext(sharedScopeId, room.Membership.Lobby.OwnerId, Snapshot.PlayerId, room.Membership.Members);
        EosRoundTransport.Validate(context);
        if (transport is { IsDisposed: false }) throw new InvalidOperationException("이전 P2P 전송을 먼저 종료하세요.");
        if (usedTransportScopes.Count >= 256 || !usedTransportScopes.Add(context.ScopeId))
            throw new InvalidOperationException("새 연결 범위가 필요합니다. 세션당 연결 범위 상한은 256개입니다.");
        // Copy before constructing either SDK or application state so caller edits cannot widen the allowlist.
        context = context with { Members = Array.AsReadOnly(context.Members.ToArray()) };
        transport = new(context, new EosSdkP2PApi(platform, context), clock);
        return transport;
    }

    public LobbyConnectionBootstrap PrepareLobbyConnection()
    {
        VerifyActive();
        if (Snapshot.State != EosConnectionState.Connected || lobby?.Snapshot.State != EosLobbyState.Joined || bootstrap is not null)
            throw new InvalidOperationException("현재 인증된 방에서 연결 준비를 한 번만 시작할 수 있습니다.");
        bootstrap = new(new BootstrapService(this, lobby), lobby.CompatibilityHash, clock);
        return bootstrap;
    }

    private sealed class BootstrapService(EosDeviceSession owner, EosLobbyClient room) : ILobbyBootstrapService
    {
        public LobbyBootstrapView? Read() => room.IsDisposed ? null : room.ReadConnectionView();
        public void WriteMember(string value, Action<bool> completed) => room.WriteConnectionAttribute(false, value, completed);
        public void WriteProposal(string value, Action<bool> completed) => room.WriteConnectionAttribute(true, value, completed);
        public IBootstrapTransport Connect(Guid scope, long revision) => owner.CreateRoundTransport(scope, revision);
    }

    private void Login(int attempt)
    {
        var options = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
            UserLoginInfo = new UserLoginInfo { DisplayName = displayName }
        };
        connect.Login(ref options, this, (ref LoginCallbackInfo info) =>
        {
            if (!IsCurrent(attempt)) return;
            if (info.ResultCode == Result.Success) Complete(info.LocalUserId);
            else if (info.ResultCode == Result.InvalidUser && info.ContinuanceToken != null && Snapshot.PlayerId == null)
            {
                var create = new CreateUserOptions { ContinuanceToken = info.ContinuanceToken };
                connect.CreateUser(ref create, this, (ref CreateUserCallbackInfo created) =>
                {
                    if (!IsCurrent(attempt)) return;
                    if (created.ResultCode == Result.Success) Complete(created.LocalUserId);
                    else Fail("게임 참가자 생성 실패", created.ResultCode);
                });
            }
            else Fail("기기 로그인 실패", info.ResultCode);
        });
    }

    private void Complete(ProductUserId user)
    {
        if (user == null || !user.IsValid()) { Fail("인증된 참가자 ID 없음"); return; }
        var id = user.ToString();
        if (Snapshot.PlayerId != null && Snapshot.PlayerId != id) { Fail("인증 갱신 중 참가자 ID 변경"); return; }
        Snapshot = new(EosConnectionState.Connected, id, "인증 완료");
    }

    private void OnAuthExpiration(ref AuthExpirationCallbackInfo info)
    {
        if (!disposed && Snapshot.State == EosConnectionState.Connected && info.LocalUserId?.ToString() == Snapshot.PlayerId)
            renewalRequested = true;
    }

    private void OnLoginStatusChanged(ref LoginStatusChangedCallbackInfo info)
    {
        if (!disposed && Snapshot.PlayerId != null && info.LocalUserId?.ToString() == Snapshot.PlayerId && info.CurrentStatus != LoginStatus.LoggedIn)
            Fail("EOS 로그인 연결 상실");
    }

    private bool IsCurrent(int attempt)
    {
        CheckTimeout();
        return !disposed && attempt == operation && (Snapshot.State is EosConnectionState.SigningIn or EosConnectionState.Renewing);
    }

    private void CheckTimeout()
    {
        if (!disposed && (Snapshot.State is EosConnectionState.SigningIn or EosConnectionState.Renewing) &&
            clock.GetElapsedTime(operationStart) >= OperationTimeout) Fail("EOS 인증 시간 초과");
    }

    private void Fail(string message, Result? result = null)
    {
        ++operation; // Ignore callbacks from a timed out or failed attempt.
        renewalRequested = false;
        Snapshot = new(EosConnectionState.Failed, null, result == null ? message : $"{message}: {result}");
        transport?.Fail("EOS 인증 연결이 끊겼습니다.");
        lobby?.Fail("EOS 인증 연결이 끊겼습니다.");
    }

    private void VerifyActive() { library.VerifyThread(); ObjectDisposedException.ThrowIf(disposed, this); }

    public void Dispose()
    {
        if (disposed) return;
        VerifyActive();
        disposed = true;
        ++operation;
        Snapshot = new(EosConnectionState.Disposed, null, "인증 세션 종료");
        bootstrap?.Dispose();
        transport?.Dispose();
        lobby?.Dispose();
        connect.RemoveNotifyAuthExpiration(expirationId);
        connect.RemoveNotifyLoginStatusChanged(statusId);
        platform.Release();
        Helper.ReleaseCallbacksAfterPlatformRelease(this);
        library.ReleaseSession();
    }
}
