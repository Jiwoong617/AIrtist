using System.Security.Cryptography;
using System.Text;

namespace AIrtist.Core;

public sealed record ConnectionMember(string PlayerId, Guid SessionId);
public sealed record PreparedLobbyConnection(Guid ScopeId, string HostId, string LocalId, string CompatibilityHash,
    IReadOnlyList<ConnectionMember> Members, IRoundTransport Transport);
public sealed record LobbyBootstrapView(string LobbyId, string HostId, string LocalId, long MembershipRevision,
    IReadOnlyList<string> Members, string? Proposal, IReadOnlyDictionary<string, string?> MemberData);
public enum ConnectionReadiness { Connecting, Ready, Failed }
public interface IBootstrapTransport : IRoundTransport { ConnectionReadiness Readiness { get; } }
// All views and writes are authenticated service operations, never peer-packet or UI claims.
public interface ILobbyBootstrapService
{
    LobbyBootstrapView? Read();
    void WriteMember(string value, Action<bool> completed);
    void WriteProposal(string value, Action<bool> completed);
    IBootstrapTransport Connect(Guid scopeId, long expectedMembershipRevision);
}
public enum LobbyBootstrapState { Preparing, Connecting, Ready, HandedOff, Failed, Disposed }
public sealed record LobbyBootstrapSnapshot(LobbyBootstrapState State, Guid? ScopeId, int ReadyMembers, int TotalMembers, string Message);

// One immutable service membership epoch. Membership changes terminate it, including after handoff.
// This is a connection barrier; it does not start a game, spend generation chances or run game timers.
public sealed class LobbyConnectionBootstrap : IDisposable
{
    public const string MemberKey = "ART_MEMBER_V1", ProposalKey = "ART_CONNECT_V1";
    public const int MaxAttributeChars = 256;
    private readonly ILobbyBootstrapService service;
    private readonly TimeProvider clock;
    private readonly LobbyBootstrapView initial;
    private readonly string compatibility;
    private readonly Guid session = Guid.NewGuid();
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly long created;
    private long writeStarted, lastWrite;
    private bool pending, wrote, disposed, taken;
    private int operation;
    private Guid? scope;
    private string? digest;
    private string? lastMemberValue, expectedProposalValue;
    private ConnectionMember[]? agreedMembers;
    private IBootstrapTransport? connection;
    public LobbyBootstrapSnapshot Snapshot { get; private set; }

    public LobbyConnectionBootstrap(ILobbyBootstrapService service, string compatibilityHash, TimeProvider? clock = null)
    {
        this.service = service; this.clock = clock ?? TimeProvider.System;
        if (!Hash(compatibilityHash)) throw new ArgumentException("호환성 해시가 올바르지 않습니다.");
        compatibility = compatibilityHash;
        var view = service.Read();
        if (view is null || view.MembershipRevision < 1 || view.Members is null || view.Members.Count is < 2 or > 16 ||
            view.Members.Any(id => !Id(id)) || view.Members.Distinct(StringComparer.Ordinal).Count() != view.Members.Count ||
            !view.Members.Contains(view.HostId) || !view.Members.Contains(view.LocalId) || string.IsNullOrEmpty(view.LobbyId) ||
            view.LobbyId.Length > 128 || view.LobbyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("현재 서비스 로비의 2~16명 명단을 확인하세요.");
        initial = view with { Members = Array.AsReadOnly(view.Members.Order(StringComparer.Ordinal).ToArray()) };
        created = this.clock.GetTimestamp();
        Snapshot = new(LobbyBootstrapState.Preparing, null, 0, initial.Members.Count, "참가자 연결 정보 확인 중");
    }

    public void Tick()
    {
        VerifyThread(); if (Terminal) return;
        if (pending && clock.GetElapsedTime(writeStarted) >= TimeSpan.FromSeconds(30)) { Fail("연결 정보 저장 시간 초과"); return; }
        if (!taken && clock.GetElapsedTime(created) >= TimeSpan.FromSeconds(60)) { Fail("전원 연결 준비 시간 초과"); return; }
        var view = service.Read();
        if (view is null || view.LobbyId != initial.LobbyId || view.HostId != initial.HostId || view.LocalId != initial.LocalId ||
            view.MembershipRevision != initial.MembershipRevision || view.Members is null || view.MemberData is null ||
            !initial.Members.SequenceEqual(view.Members.Order(StringComparer.Ordinal)))
        { Fail("참가자 또는 방장이 변경되어 연결 준비를 중단했습니다."); return; }
        if (connection?.Readiness == ConnectionReadiness.Failed) { Fail("참가자 연결이 끊겼습니다."); return; }
        // A callback may still be pending after service state becomes visible. Require both confirmations.
        if (pending) return;
        if (expectedProposalValue is not null)
        {
            if (view.Proposal != expectedProposalValue) return; // SDK completion may precede cache/notification delivery.
            expectedProposalValue = null;
        }
        if (!view.MemberData.TryGetValue(initial.LocalId, out var own) || !TryMember(own, out var ownSession, out _, out _) || ownSession != session)
        {
            if (scope is not null) { Fail("내 접속 정보가 변경되었습니다."); return; }
            Write(false, MemberValue(Guid.Empty)); return;
        }
        var identities = new List<ConnectionMember>();
        var readyScopes = new List<Guid>();
        foreach (var id in initial.Members)
        {
            if (!view.MemberData.TryGetValue(id, out var text) || string.IsNullOrEmpty(text))
            { if (agreedMembers is not null) Fail("합의한 참가자의 접속 정보가 사라졌습니다."); return; }
            if (!TryMember(text, out var nonce, out var hash, out var ready) || hash != compatibility)
            { Fail("참가자의 접속 정보 또는 호환성이 일치하지 않습니다."); return; }
            identities.Add(new(id, nonce)); readyScopes.Add(ready);
        }
        if (identities.Select(m => m.SessionId).Distinct().Count() != identities.Count) { Fail("접속 ID가 중복되었습니다."); return; }
        string currentDigest = RosterHash(initial.LobbyId, initial.HostId, compatibility, identities);
        if (digest is not null && digest != currentDigest) { Fail("참가자의 접속 세션이 변경되었습니다."); return; }
        if (scope is null && initial.LocalId == initial.HostId)
        {
            // Old ready attributes must be cleared by each newly created participant session first.
            if (readyScopes.Any(value => value != Guid.Empty)) return;
            if (!CanWrite) return;
            scope = Guid.NewGuid(); digest = currentDigest; agreedMembers = identities.ToArray();
            Write(true, ProposalValue(false));
            return;
        }
        if (!TryProposal(view.Proposal, out var offered, out var offeredDigest, out var offeredHash, out var released))
        {
            if (scope is not null || !string.IsNullOrEmpty(view.Proposal)) Fail("방장의 연결 정보가 사라졌거나 형식이 올바르지 않습니다.");
            return;
        }
        if (offeredHash != compatibility) { Fail("방장의 호환성이 일치하지 않습니다."); return; }
        if (scope is null)
        {
            if (offeredDigest != currentDigest) return; // An old owner proposal can remain until fresh identities arrive.
            if (released) { Fail("이미 시작된 연결 범위에는 참가할 수 없습니다."); return; }
            scope = offered; digest = currentDigest; agreedMembers = identities.ToArray();
        }
        if (offered != scope || offeredDigest != digest) { Fail("합의한 연결 범위가 변경되었습니다."); return; }
        if (connection is null)
        {
            try { connection = service.Connect(scope.Value, initial.MembershipRevision); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { Fail("현재 로비의 P2P 연결을 준비할 수 없습니다."); return; }
            if (connection.LocalId != initial.LocalId) { Fail("P2P 참가자 정보가 일치하지 않습니다."); return; }
        }
        if (connection.Readiness == ConnectionReadiness.Failed) { Fail("P2P 연결 준비 실패"); return; }
        if (connection.Readiness != ConnectionReadiness.Ready)
        {
            if (Snapshot.State is LobbyBootstrapState.Ready or LobbyBootstrapState.HandedOff) { Fail("완료된 연결 준비가 취소됐습니다."); return; }
            Snapshot = new(LobbyBootstrapState.Connecting, scope, 0, initial.Members.Count, "P2P 연결 준비 중"); return;
        }
        view.MemberData.TryGetValue(initial.LocalId, out own);
        TryMember(own, out _, out _, out var ownReady);
        int readyCount = readyScopes.Count(value => value == scope);
        if ((Snapshot.State is LobbyBootstrapState.Ready or LobbyBootstrapState.HandedOff) && (!released || readyCount != initial.Members.Count))
        { Fail("완료된 연결 합의가 변경됐습니다."); return; }
        if (ownReady != scope) { Write(false, MemberValue(scope.Value)); return; }
        Snapshot = Snapshot with { ScopeId = scope, ReadyMembers = readyCount, Message = "다른 참가자 연결 준비 확인 중" };
        if (readyCount != initial.Members.Count) return;
        if (!released)
        {
            if (initial.LocalId == initial.HostId) Write(true, ProposalValue(true));
            return;
        }
        Snapshot = new(taken ? LobbyBootstrapState.HandedOff : LobbyBootstrapState.Ready, scope, readyCount, initial.Members.Count, "전원 연결 준비 완료");
    }

    public PreparedLobbyConnection TakeConnection()
    {
        VerifyThread(); Tick();
        if (taken || Snapshot.State != LobbyBootstrapState.Ready || connection is null || agreedMembers is null)
            throw new InvalidOperationException("전원 연결 준비가 확인된 연결만 한 번 전달할 수 있습니다.");
        taken = true; Snapshot = Snapshot with { State = LobbyBootstrapState.HandedOff };
        return new(scope!.Value, initial.HostId, initial.LocalId, compatibility, Array.AsReadOnly(agreedMembers), connection);
    }

    private string MemberValue(Guid ready) => $"1|{session:N}|{compatibility}|{ready:N}";
    private string ProposalValue(bool released) => $"1|{scope:N}|{digest}|{compatibility}|{(released ? "run" : "prepare")}";
    private bool CanWrite => !wrote || clock.GetElapsedTime(lastWrite) >= TimeSpan.FromMilliseconds(500);
    private void Write(bool owner, string value)
    {
        if (!CanWrite || (!owner && lastMemberValue == value)) return;
        if (owner) expectedProposalValue = value; else lastMemberValue = value;
        pending = wrote = true; writeStarted = lastWrite = clock.GetTimestamp(); int token = ++operation;
        void Complete(bool success)
        {
            VerifyThread(); if (Terminal || token != operation) return;
            if (clock.GetElapsedTime(writeStarted) >= TimeSpan.FromSeconds(30) || clock.GetElapsedTime(created) >= TimeSpan.FromSeconds(60))
            { Fail("연결 정보 저장 시간 초과"); return; }
            pending = false;
            if (!success) Fail("연결 정보 저장 실패");
        }
        if (owner) service.WriteProposal(value, Complete); else service.WriteMember(value, Complete);
    }
    private static bool TryMember(string? value, out Guid session, out string hash, out Guid ready)
    {
        session = ready = Guid.Empty; hash = "";
        if (value is null || value.Length > MaxAttributeChars) return false;
        var p = value.Split('|');
        if (p.Length != 4 || p[0] != "1" || !Guid.TryParseExact(p[1], "N", out session) || session == Guid.Empty ||
            !Hash(p[2]) || !Guid.TryParseExact(p[3], "N", out ready)) return false;
        hash = p[2]; return true;
    }
    private static bool TryProposal(string? value, out Guid scope, out string digest, out string hash, out bool released)
    {
        scope = Guid.Empty; digest = hash = ""; released = false;
        if (value is null || value.Length > MaxAttributeChars) return false;
        var p = value.Split('|');
        if (p.Length != 5 || p[0] != "1" || !Guid.TryParseExact(p[1], "N", out scope) || scope == Guid.Empty ||
            !Hash(p[2]) || !Hash(p[3]) || p[4] is not ("prepare" or "run")) return false;
        digest = p[2]; hash = p[3]; released = p[4] == "run"; return true;
    }
    private static string RosterHash(string room, string host, string hash, IEnumerable<ConnectionMember> members) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("AIrtist-connect-v1\n" + room + "\n" + host + "\n" + hash + "\n" +
            string.Join('\n', members.Select(m => $"{m.PlayerId}:{m.SessionId:N}")))));
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Id(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private bool Terminal => disposed || Snapshot.State == LobbyBootstrapState.Failed;
    private void Fail(string message)
    {
        operation++; pending = false; connection?.Dispose();
        Snapshot = Snapshot with { State = LobbyBootstrapState.Failed, Message = message };
    }
    private void VerifyThread() { if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("연결 준비는 네트워크 스레드에서 실행하세요."); }
    public void Dispose()
    {
        VerifyThread(); if (disposed) return; disposed = true; operation++; connection?.Dispose();
        Snapshot = Snapshot with { State = LobbyBootstrapState.Disposed, Message = "연결 준비 종료" };
    }
}
