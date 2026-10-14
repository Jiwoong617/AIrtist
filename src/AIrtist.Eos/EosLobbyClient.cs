using AIrtist.Core;
using System.Collections.ObjectModel;

namespace AIrtist.Eos;

// Runs on the authentication session's Tick thread. This service lobby is not a Core match lobby:
// shared scope/member sessions and an all-peer P2P barrier must be agreed before starting Core timers.
public sealed class EosLobbyClient : IDisposable
{
    public const int MaxSearchResults = 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly IEosLobbyApi api;
    private readonly TimeProvider clock;
    private readonly string localId, bucket;
    private readonly Action invalidateTransport;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private long started;
    private int operation;
    private string? joinedId;
    private bool disposed, refreshPending;
    public bool IsDisposed => disposed;
    internal string CompatibilityHash { get; }
    private bool attributePending;
    public EosLobbySnapshot Snapshot { get; private set; } = new(EosLobbyState.Idle, [], null, 0, "온라인 방 대기");

    internal EosLobbyClient(string localId, string compatibilityHash, IEosLobbyApi api,
        Action invalidateTransport, TimeProvider? clock = null)
    {
        if (!ProductId(localId)) throw new ArgumentException("인증된 참가자 ID가 필요합니다.");
        ValidateCompatibility(compatibilityHash);
        CompatibilityHash = compatibilityHash;
        this.localId = localId;
        bucket = "AIrtist-v1-" + Convert.ToBase64String(Convert.FromHexString(compatibilityHash)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        this.api = api; this.invalidateTransport = invalidateTransport; this.clock = clock ?? TimeProvider.System;
        try { if (!api.Start(OnSignal)) throw new InvalidOperationException("EOS 로비 알림 등록 실패"); }
        catch { disposed = true; api.Dispose(); throw; }
    }

    internal static void ValidateCompatibility(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("모델·단어·규칙·통신 버전의 호환성 SHA256이 필요합니다.");
    }
    private static bool ProductId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool LobbyId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private bool Valid(EosLobbyListing? item) => item is not null && LobbyId(item.LobbyId) && ProductId(item.OwnerId) &&
        item.BucketId == bucket && item.MaxMembers is >= 2 and <= 16 && item.AvailableSlots >= 0 && item.AvailableSlots <= item.MaxMembers;

    public void Search()
    {
        Require(EosLobbyState.Idle);
        int token = Begin(EosLobbyState.Searching, "방 검색 중");
        api.ClearSearch();
        api.Search(bucket, result =>
        {
            if (!Current(token)) { if (!disposed) api.ClearSearch(); return; }
            if (!result.Success || result.Results is null || result.Results.Count > MaxSearchResults)
            { Fail("방 검색 실패"); api.ClearSearch(); return; }
            var rooms = result.Results.Where(item => Valid(item) && item.AvailableSlots > 0)
                .DistinctBy(item => item.LobbyId).ToArray();
            Snapshot = Snapshot with { State = EosLobbyState.Idle, SearchResults = Array.AsReadOnly(rooms), Message = "방 검색 완료" };
        });
    }

    public void Create(int maxMembers, string? lobbyId = null)
    {
        Require(EosLobbyState.Idle);
        if (maxMembers is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(maxMembers));
        if (lobbyId is not null && RoomCode.FromLobbyId(lobbyId) is null) throw new ArgumentException("방 코드를 확인하세요.");
        int token = Begin(EosLobbyState.Creating, "방 생성 중"); api.ClearSearch();
        api.Create(bucket, maxMembers, result => CompleteMembership(token, result, lobbyId, true), lobbyId);
    }

    public void Join(string lobbyId)
    {
        Require(EosLobbyState.Idle);
        if (!Snapshot.SearchResults.Any(item => item.LobbyId == lobbyId))
            throw new ArgumentException("현재 검색 결과에서 방을 선택하세요.");
        int token = Begin(EosLobbyState.Joining, "방 참가 중");
        api.Join(lobbyId, result => CompleteMembership(token, result, lobbyId, false));
    }

    public void JoinByCode(string code)
    {
        Require(EosLobbyState.Idle);
        string id = RoomCode.ToLobbyId(code);
        int token = Begin(EosLobbyState.Searching, "방 코드를 확인하고 있어요"); api.ClearSearch();
        api.Search(bucket, result =>
        {
            if (!Current(token)) { if (!disposed) api.ClearSearch(); return; }
            if (!result.Success) { Fail("방 코드 조회 실패. 잠시 후 다시 접속해 주세요."); api.ClearSearch(); return; }
            if (result.Results.Count != 1 || !Valid(result.Results[0]) || result.Results[0].LobbyId != id || result.Results[0].AvailableSlots == 0)
            {
                api.ClearSearch(); Snapshot = Snapshot with { State = EosLobbyState.Idle, Message = "참가 가능한 방이 없습니다. 코드·빈자리·게임 버전을 확인해 주세요." }; return;
            }
            int joinToken = Begin(EosLobbyState.Joining, "친구의 방에 들어가고 있어요");
            api.Join(id, entry => CompleteMembership(joinToken, entry, id, false));
        }, id);
    }

    private void CompleteMembership(int token, EosLobbyCompletion result, string? expectedId, bool created)
    {
        if (!Current(token))
        {
            // Native operations cannot be canceled. The adapter owns cleanup after Dispose;
            // while still alive we explicitly leave a late successful remote side effect.
            if (!disposed && result.Success && LobbyId(result.LobbyId)) api.Leave(result.LobbyId!, _ => { });
            return;
        }
        api.ClearSearch();
        if (!result.Success || !LobbyId(result.LobbyId)) { Fail("방 생성 또는 참가 실패"); return; }
        joinedId = result.LobbyId;
        if ((expectedId is not null && expectedId != joinedId) || !RefreshMembership() ||
            (created && Snapshot.Membership!.Lobby.OwnerId != localId))
        {
            Fail("서비스가 확인한 방 정보가 일치하지 않습니다.");
            api.Leave(joinedId!, _ => { }); joinedId = null; return;
        }
        Snapshot = Snapshot with { State = EosLobbyState.Joined, Message = "온라인 방 참가 완료" };
    }

    public void Leave()
    {
        Require(EosLobbyState.Joined);
        int token = Begin(EosLobbyState.Leaving, "방 나가는 중"); invalidateTransport();
        string id = joinedId!;
        api.Leave(id, result =>
        {
            if (!Current(token)) return;
            if (!result.Success || result.LobbyId != id) { Fail("방 나가기 확인 실패"); return; }
            joinedId = null; refreshPending = false;
            Snapshot = new(EosLobbyState.Idle, [], null, Snapshot.MembershipRevision + 1, "방에서 나왔습니다.");
        });
    }

    internal LobbyBootstrapView? ReadConnectionView()
    {
        Alive();
        var view = Snapshot.Membership;
        return Snapshot.State != EosLobbyState.Joined || view is null ? null :
            new(view.Lobby.LobbyId, view.Lobby.OwnerId, localId, Snapshot.MembershipRevision,
                view.Members, view.ConnectionProposal, view.MemberConnectionData);
    }

    internal void WriteConnectionAttribute(bool owner, string value, Action<bool> completed)
    {
        Require(EosLobbyState.Joined);
        if (attributePending || value.Length is < 1 or > LobbyConnectionBootstrap.MaxAttributeChars ||
            (owner && Snapshot.Membership!.Lobby.OwnerId != localId))
            throw new InvalidOperationException("현재 참가자의 연결 정보만 순서대로 저장하세요.");
        attributePending = true;
        string id = joinedId!; long revision = Snapshot.MembershipRevision;
        api.WriteConnectionAttribute(id, owner, value, success =>
        {
            if (disposed) return;
            attributePending = false;
            bool current = Snapshot.State == EosLobbyState.Joined && joinedId == id && Snapshot.MembershipRevision == revision;
            if (current) refreshPending = true;
            completed(success && current);
        });
    }

    // Cancellation is terminal for this client: don't start another operation while an old native
    // create/join may still succeed. Recreate the authentication session to retry after failure.
    public void Cancel()
    {
        Alive();
        if (Pending) Fail("로비 작업 취소");
    }

    public void Pump()
    {
        VerifyThread(); if (disposed) return;
        CheckTimeout();
        if (refreshPending && Snapshot.State == EosLobbyState.Joined)
        {
            refreshPending = false;
            if (!RefreshMembership()) Fail("서비스 참가자 명단을 확인할 수 없습니다.");
        }
    }

    private bool RefreshMembership()
    {
        var value = api.ReadMembership(joinedId!);
        if (value is null || !Valid(value.Lobby) || value.Lobby.LobbyId != joinedId || value.Members is null ||
            value.Members.Count is < 1 or > 16 || value.Members.Count > value.Lobby.MaxMembers ||
            value.Members.Any(id => !ProductId(id)) || value.Members.Distinct(StringComparer.Ordinal).Count() != value.Members.Count ||
            !value.Members.Contains(localId) || !value.Members.Contains(value.Lobby.OwnerId)) return false;
        if (value.ConnectionProposal?.Length > LobbyConnectionBootstrap.MaxAttributeChars || value.MemberConnectionData is null ||
            value.MemberConnectionData.Count > 16 || value.MemberConnectionData.Any(p => !value.Members.Contains(p.Key) || p.Value?.Length > LobbyConnectionBootstrap.MaxAttributeChars)) return false;
        var copy = value with
        {
            Members = Array.AsReadOnly(value.Members.Order(StringComparer.Ordinal).ToArray()),
            MemberConnectionData = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(value.MemberConnectionData, StringComparer.Ordinal))
        };
        var before = Snapshot.Membership;
        bool changed = before is null || before.Lobby.OwnerId != copy.Lobby.OwnerId || !before.Members.SequenceEqual(copy.Members);
        if (changed)
        {
            invalidateTransport();
            if (before is not null && before.Lobby.OwnerId != copy.Lobby.OwnerId) return false; // No host migration.
        }
        Snapshot = Snapshot with { Membership = copy, MembershipRevision = Snapshot.MembershipRevision + (changed ? 1 : 0) };
        return true;
    }

    private void OnSignal(EosLobbySignal signal)
    {
        VerifyThread();
        if (disposed || Snapshot.State is not (EosLobbyState.Joined or EosLobbyState.Leaving) || signal.LobbyId != joinedId) return;
        if (signal.Kind is not (EosLobbySignalKind.Updated or EosLobbySignalKind.MemberUpdated))
        {
            // Even a leave/rejoin with the same Product ID invalidates the connection epoch.
            invalidateTransport();
            Snapshot = Snapshot with { MembershipRevision = Snapshot.MembershipRevision + 1 };
            if (Snapshot.State == EosLobbyState.Joined && (signal.Kind == EosLobbySignalKind.Promoted || signal.Kind == EosLobbySignalKind.Closed ||
                (signal.PlayerId == localId && signal.Kind is EosLobbySignalKind.Left or EosLobbySignalKind.Disconnected or EosLobbySignalKind.Kicked)))
            { Fail("방이 닫혔거나 방장·내 참가 상태가 변경되었습니다."); return; }
        }
        refreshPending = true;
    }

    internal void Fail(string message)
    {
        VerifyThread(); if (disposed || Snapshot.State == EosLobbyState.Failed) return;
        operation++; refreshPending = false; invalidateTransport();
        Snapshot = Snapshot with { State = EosLobbyState.Failed, SearchResults = [], Membership = null, Message = message };
    }
    private bool Pending => Snapshot.State is EosLobbyState.Searching or EosLobbyState.Creating or EosLobbyState.Joining or EosLobbyState.Leaving;
    private void CheckTimeout() { if (Pending && clock.GetElapsedTime(started) >= Timeout) Fail("EOS 로비 응답 시간 초과"); }
    private bool Current(int token) { VerifyThread(); if (disposed) return false; CheckTimeout(); return token == operation && Pending; }
    private int Begin(EosLobbyState state, string message)
    {
        started = clock.GetTimestamp();
        Snapshot = Snapshot with { State = state, SearchResults = [], Message = message };
        return ++operation;
    }
    private void Require(EosLobbyState state) { Alive(); if (Snapshot.State != state) throw new InvalidOperationException("현재 로비 작업을 완료한 뒤 다시 요청하세요."); }
    private void Alive() { VerifyThread(); ObjectDisposedException.ThrowIf(disposed, this); }
    private void VerifyThread() { if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("로비는 EOS 네트워크 스레드에서만 사용하세요."); }
    public void Dispose()
    {
        VerifyThread(); if (disposed) return;
        disposed = true; operation++; invalidateTransport(); api.Dispose();
        Snapshot = new(EosLobbyState.Disposed, [], null, Snapshot.MembershipRevision, "온라인 로비 종료");
    }
}
