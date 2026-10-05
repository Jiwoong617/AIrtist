namespace AIrtist.Core;

// Membership comes from the connection adapter, never a client-supplied roster.
// SessionId changes on every leave/rejoin. No EOS types or credentials enter Core.
public sealed record LobbyMember(VotingPlayer Player, Guid SessionId);
public enum LobbyPhase { Waiting, Started, Closed }
public enum LobbyCompatibilityIssue { Unchecked, None, Protocol, Rules, Assets, Words }
public sealed record LobbyReadyRequest(Guid LobbyId, long Revision, string PlayerId, Guid SessionId,
    long Sequence, bool Ready, MultiplayerCompatibility Compatibility);
public sealed record LobbyMemberState(LobbyMember Member, bool Ready, LobbyCompatibilityIssue Issue, long ReadySequence = 0);
public sealed record LobbySnapshot(Guid LobbyId, string HostId, long Revision, LobbyPhase Phase,
    MultiplayerMatchSettings Settings, IReadOnlyList<LobbyMemberState> Members, bool CanStart, string Message);
public sealed record LobbyLaunchPlan(Guid LobbyId, long Revision, Guid MatchId, string HostId,
    MultiplayerCompatibility Compatibility, MultiplayerMatchSettings Settings, IReadOnlyList<MultiplayerParticipant> Participants)
{
    public MatchRuntimeContext ContextFor(string localId)
    {
        if (!Participants.Any(p => p.Player.Id == localId)) throw new UnauthorizedAccessException("경기 참가자가 아닙니다.");
        return new(MatchId, HostId, localId, Compatibility, Participants, Settings);
    }
}

// All methods are serialized, including the atomic readiness check + roster freeze.
// This is a host authority, not lobby discovery, authentication or state replication.
public sealed class MultiplayerLobbyHost
{
    private readonly object sync = new();
    private readonly Guid lobbyId;
    private readonly string hostId;
    private readonly MultiplayerCompatibility compatibility;
    private Dictionary<string, Member> members = new(StringComparer.Ordinal);
    private LobbyMember[] lastMembership = [];
    private string? lastOwner;
    private long membershipVersion, revision = 1;
    private MultiplayerMatchSettings settings;
    private LobbyPhase phase;
    private LobbyLaunchPlan? launch;
    private Guid startRequest;
    private string closedMessage = "";
    private sealed class Member(LobbyMember identity)
    {
        public LobbyMember Identity = identity;
        public bool Ready;
        public LobbyCompatibilityIssue Issue = LobbyCompatibilityIssue.Unchecked;
        public LobbyReadyRequest? LastRequest;
    }

    public MultiplayerLobbyHost(Guid lobbyId, string hostId, MultiplayerCompatibility compatibility, MultiplayerMatchSettings settings)
    {
        if (lobbyId == Guid.Empty) throw new ArgumentException("방 ID를 확인해 주세요.");
        VotingRoundHost.ValidateId(hostId); ArgumentNullException.ThrowIfNull(compatibility); compatibility.Validate();
        ValidateSettings(settings);
        this.lobbyId = lobbyId; this.hostId = hostId; this.compatibility = compatibility; this.settings = settings;
    }

    // The adapter supplies a monotonically increasing version for its reconciled
    // authoritative membership snapshot. Old/duplicate notifications cannot undo it.
    public void UpdateMembership(long version, string ownerId, IEnumerable<LobbyMember> roster)
    {
        lock (sync)
        {
            if (version < 1) throw new ArgumentException("참가자 목록 버전을 확인해 주세요.");
            if (version < membershipVersion || phase == LobbyPhase.Closed) return;
            ArgumentNullException.ThrowIfNull(roster); VotingRoundHost.ValidateId(ownerId);
            var next = roster.Take(17).ToArray();
            if (next.Length > 16 || next.Any(p => p?.Player is null || p.SessionId == Guid.Empty
                || string.IsNullOrWhiteSpace(p.Player.Name) || p.Player.Name.Length > 40 || p.Player.Name.Any(char.IsControl)))
                throw new ArgumentException("참가자는 최대 16명이며 올바른 이름·접속 ID가 필요합니다.");
            foreach (var p in next) VotingRoundHost.ValidateId(p.Player.Id);
            if (next.Select(p => p.Player.Id).Distinct(StringComparer.Ordinal).Count() != next.Length
                || next.Select(p => p.SessionId).Distinct().Count() != next.Length)
                throw new ArgumentException("중복된 참가자·접속 ID입니다.");
            next = next.OrderBy(p => p.Player.Id, StringComparer.Ordinal).ToArray();
            bool same = ownerId == lastOwner && next.SequenceEqual(lastMembership);
            if (version == membershipVersion)
            {
                if (!same) throw new InvalidOperationException("같은 버전의 참가자 목록이 다릅니다.");
                return;
            }
            membershipVersion = version; lastOwner = ownerId; lastMembership = next;
            if (same) return;
            if (ownerId != hostId || !next.Any(p => p.Player.Id == hostId))
            { Close("방장이 나갔거나 변경되어 방을 닫았습니다. 새 방에서 시작해 주세요."); return; }
            // Mid-match joins, departures or owner changes must not silently alter
            // the fixed match. The adapter must abort a running match on Closed.
            if (phase == LobbyPhase.Started)
            { Close("경기 시작 후 참가자가 변경되었습니다. 경기를 중단해 주세요."); return; }
            members = next.ToDictionary(p => p.Player.Id, p => members.TryGetValue(p.Player.Id, out var old)
                && old.Identity.SessionId == p.SessionId ? old : new Member(p), StringComparer.Ordinal);
            foreach (var p in next) members[p.Player.Id].Identity = p;
            InvalidateReady();
        }
    }

    public void UpdateSettings(string authenticatedSender, long expectedRevision, MultiplayerMatchSettings value)
    {
        lock (sync)
        {
            RequireHost(authenticatedSender); RequireWaiting(expectedRevision); ValidateSettings(value);
            if (value == settings) return;
            settings = value; InvalidateReady();
        }
    }

    public void SetReady(string authenticatedSender, LobbyReadyRequest request)
    {
        lock (sync)
        {
            ArgumentNullException.ThrowIfNull(request); RequireWaiting(request.Revision);
            if (request.LobbyId != lobbyId || authenticatedSender != request.PlayerId
                || !members.TryGetValue(authenticatedSender, out var member) || member.Identity.SessionId != request.SessionId)
                throw new UnauthorizedAccessException("현재 방의 인증된 참가자만 준비 상태를 바꿀 수 있습니다.");
            var c = request.Compatibility;
            if (request.Sequence < 1 || c is null || string.IsNullOrWhiteSpace(c.RulesVersion) || c.RulesVersion.Length > 80
                || !MultiplayerCompatibility.IsHash(c.AssetsSha256) || !MultiplayerCompatibility.IsHash(c.WordsSha256))
                throw new ArgumentException("준비 요청·버전 정보를 확인해 주세요.");
            if (member.LastRequest is { } last)
            {
                if (request.Sequence < last.Sequence) throw new InvalidOperationException("이전 준비 요청입니다.");
                if (request.Sequence == last.Sequence)
                {
                    if (request != last) throw new InvalidOperationException("같은 준비 요청의 내용이 다릅니다.");
                    return;
                }
            }
            member.Issue = Compare(c); member.Ready = request.Ready && member.Issue == LobbyCompatibilityIssue.None;
            member.LastRequest = request;
        }
    }

    public LobbyLaunchPlan Start(string authenticatedSender, long expectedRevision, Guid requestId)
    {
        lock (sync)
        {
            RequireHost(authenticatedSender);
            if (requestId == Guid.Empty) throw new ArgumentException("시작 요청 ID를 확인해 주세요.");
            if (phase == LobbyPhase.Started && launch is not null && startRequest == requestId && launch.Revision == expectedRevision) return launch;
            RequireWaiting(expectedRevision);
            if (!CanStart()) throw new InvalidOperationException("같은 버전의 참가자 2명 이상이 모두 준비해야 시작할 수 있습니다.");
            launch = new(lobbyId, revision, Guid.NewGuid(), hostId, compatibility, settings,
                Array.AsReadOnly(members.Values.Select(m => new MultiplayerParticipant(m.Identity.Player, m.LastRequest!.Compatibility)).ToArray()));
            startRequest = requestId; phase = LobbyPhase.Started; return launch;
        }
    }

    public LobbySnapshot Snapshot()
    {
        lock (sync)
        {
            string message = phase == LobbyPhase.Closed ? closedMessage : phase == LobbyPhase.Started ? "경기를 시작했습니다."
                : members.Count < 2 ? "참가자 2명 이상을 기다립니다."
                : members.Values.Any(m => m.Issue is not (LobbyCompatibilityIssue.None or LobbyCompatibilityIssue.Unchecked)) ? "버전이 다른 참가자가 있습니다. 같은 게임 파일을 사용해 주세요."
                : CanStart() ? "모두 준비했습니다. 방장이 경기를 시작할 수 있습니다." : "각 참가자가 경기 설정을 확인하고 준비해 주세요.";
            return new(lobbyId, hostId, revision, phase, settings,
                Array.AsReadOnly(members.Values.Select(m => new LobbyMemberState(m.Identity, m.Ready, m.Issue, m.LastRequest?.Sequence ?? 0)).ToArray()), CanStart(), message);
        }
    }

    private bool CanStart() => phase == LobbyPhase.Waiting && members.Count >= 2 && members.Values.All(m => m.Ready && m.Issue == LobbyCompatibilityIssue.None);
    private void InvalidateReady() { revision++; foreach (var m in members.Values) m.Ready = false; }
    private void Close(string message) { InvalidateReady(); phase = LobbyPhase.Closed; closedMessage = message; }
    private void RequireHost(string sender)
    {
        if (sender != hostId || !members.ContainsKey(hostId)) throw new UnauthorizedAccessException("방장만 경기 설정·시작을 결정할 수 있습니다.");
    }
    private void RequireWaiting(long expectedRevision)
    {
        if (phase != LobbyPhase.Waiting || expectedRevision != revision) throw new InvalidOperationException("현재 대기방 설정을 다시 확인해 주세요.");
    }
    private static void ValidateSettings(MultiplayerMatchSettings value)
    {
        if (value is null || !value.IsValid()) throw new ArgumentException("라운드 수·제한 시간을 확인해 주세요.");
    }
    private LobbyCompatibilityIssue Compare(MultiplayerCompatibility c) => c.ProtocolVersion != compatibility.ProtocolVersion ? LobbyCompatibilityIssue.Protocol
        : c.RulesVersion != compatibility.RulesVersion ? LobbyCompatibilityIssue.Rules
        : !string.Equals(c.AssetsSha256, compatibility.AssetsSha256, StringComparison.OrdinalIgnoreCase) ? LobbyCompatibilityIssue.Assets
        : !string.Equals(c.WordsSha256, compatibility.WordsSha256, StringComparison.OrdinalIgnoreCase) ? LobbyCompatibilityIssue.Words : LobbyCompatibilityIssue.None;
}
