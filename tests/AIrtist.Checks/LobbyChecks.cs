using AIrtist.Core;

internal static class LobbyChecks
{
    private static readonly MultiplayerCompatibility Compatible = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid lobby action accepted");
    }
    private sealed class Room
    {
        public readonly MultiplayerLobbyHost Host = new(Guid.NewGuid(), "p1", Compatible, MultiplayerMatchSettings.Default);
        public readonly LobbyMember[] Members;
        private readonly Dictionary<string, long> sequences = new();
        public Room(int count = 3)
        {
            Members = Enumerable.Range(1, count).Select(i => new LobbyMember(new("p" + i, "참가자 " + i), Guid.NewGuid())).ToArray();
            Host.UpdateMembership(1, "p1", Members);
        }
        public LobbyReadyRequest Request(string id, bool ready = true, MultiplayerCompatibility? compatibility = null)
        {
            var s = Host.Snapshot(); var m = s.Members.Single(m => m.Member.Player.Id == id).Member;
            long sequence = sequences.GetValueOrDefault(id) + 1; sequences[id] = sequence;
            return new(s.LobbyId, s.Revision, id, m.SessionId, sequence, ready, compatibility ?? Compatible);
        }
        public void ReadyAll() { foreach (var m in Host.Snapshot().Members) Host.SetReady(m.Member.Player.Id, Request(m.Member.Player.Id)); }
        public LobbyLaunchPlan Start() => Host.Start("p1", Host.Snapshot().Revision, Guid.NewGuid());
    }

    public static void Run()
    {
        ReadinessAndSettings(); IdentityAndOrdering(); Compatibility(); Membership(); FrozenStart(); Limits(); ConcurrentStart();
        Console.WriteLine("Lobby: readiness/settings, identity/replay, compatibility, membership/owner change, immutable handoff and concurrent start passed (no EOS)");
    }

    private static void ReadinessAndSettings()
    {
        var r = new Room(); Require(!r.Host.Snapshot().CanStart, "Room started without ready"); Reject(() => r.Start());
        r.ReadyAll(); Require(r.Host.Snapshot().CanStart, "All-ready blocked");
        var unready = r.Request("p3", false); r.Host.SetReady("p3", unready);
        Require(!r.Host.Snapshot().CanStart, "Cancellation ignored"); Reject(() => r.Start());
        r.Host.SetReady("p3", r.Request("p3"));
        var before = r.Host.Snapshot();
        Reject(() => r.Host.UpdateSettings("p2", before.Revision, before.Settings with { RoundCount = 4 }));
        Reject(() => r.Host.Start("p2", before.Revision, Guid.NewGuid()));
        r.Host.UpdateSettings("p1", before.Revision, before.Settings);
        Require(r.Host.Snapshot().CanStart && r.Host.Snapshot().Revision == before.Revision, "Unchanged settings cleared ready");
        var lateReady = r.Request("p3");
        r.Host.UpdateSettings("p1", before.Revision, before.Settings with { RoundCount = 4 });
        var after = r.Host.Snapshot();
        Require(after.Settings.RoundCount == 4 && after.Members.All(m => !m.Ready) && after.Revision > before.Revision, "Settings did not invalidate ready");
        Reject(() => r.Host.SetReady("p3", lateReady));
        Reject(() => r.Host.UpdateSettings("p1", before.Revision, before.Settings));
        r.ReadyAll(); Require(r.Start().Settings.RoundCount == 4, "Updated settings not handed to match");
    }

    private static void IdentityAndOrdering()
    {
        var r = new Room(); var first = r.Request("p2");
        Reject(() => r.Host.SetReady("p3", first)); Reject(() => r.Host.SetReady("unknown", first));
        Reject(() => r.Host.SetReady("p2", first with { LobbyId = Guid.NewGuid() }));
        Reject(() => r.Host.SetReady("p2", first with { SessionId = Guid.NewGuid() }));
        Reject(() => r.Host.SetReady("p2", first with { Revision = first.Revision + 1 }));
        Reject(() => r.Host.SetReady("p2", first with { Sequence = 0 }));
        Require(r.Host.Snapshot().Members.All(m => !m.Ready), "Invalid ready changed state");
        r.Host.SetReady("p2", first); r.Host.SetReady("p2", first);
        Require(r.Host.Snapshot().Members.Single(m => m.Member.Player.Id == "p2").Ready, "Idempotent request lost ready");
        Reject(() => r.Host.SetReady("p2", first with { Ready = false }));
        var last = r.Request("p2", false); r.Host.SetReady("p2", last);
        Reject(() => r.Host.SetReady("p2", first));
        Require(!r.Host.Snapshot().Members.Single(m => m.Member.Player.Id == "p2").Ready, "Late ready undid cancellation");
    }

    private static void Compatibility()
    {
        var cases = new[]
        {
            (Compatible with { ProtocolVersion = 2 }, LobbyCompatibilityIssue.Protocol),
            (Compatible with { RulesVersion = "multiplayer-round-v5" }, LobbyCompatibilityIssue.Rules),
            (Compatible with { AssetsSha256 = new('C', 64) }, LobbyCompatibilityIssue.Assets),
            (Compatible with { WordsSha256 = new('C', 64) }, LobbyCompatibilityIssue.Words)
        };
        foreach (var (value, issue) in cases)
        {
            var r = new Room(); r.ReadyAll(); r.Host.SetReady("p3", r.Request("p3", compatibility: value));
            var member = r.Host.Snapshot().Members.Single(m => m.Member.Player.Id == "p3");
            Require(!member.Ready && member.Issue == issue && !r.Host.Snapshot().CanStart, "Compatibility mismatch admitted: " + issue);
            Reject(() => r.Start()); r.Host.SetReady("p3", r.Request("p3")); Require(r.Host.Snapshot().CanStart, "Compatibility repair not accepted");
        }
        var room = new Room(); var req = room.Request("p1");
        Reject(() => room.Host.SetReady("p1", req with { Compatibility = Compatible with { AssetsSha256 = "x" } }));
        Reject(() => room.Host.SetReady("p1", req with { Compatibility = Compatible with { RulesVersion = new('x', 81) } }));
        room.ReadyAll(); room.Host.SetReady("p3", room.Request("p3", compatibility: Compatible with { AssetsSha256 = new('a', 64), WordsSha256 = new('b', 64) }));
        Require(room.Host.Snapshot().CanStart, "Hash casing blocked matching build");
    }

    private static void Membership()
    {
        var r = new Room(); r.ReadyAll(); var before = r.Host.Snapshot(); var oldReady = r.Request("p3");
        r.Host.UpdateMembership(2, "p1", r.Members.Reverse());
        Require(r.Host.Snapshot().Revision == before.Revision && r.Host.Snapshot().CanStart, "Reordered notification cleared ready");
        r.Host.UpdateMembership(1, "p2", r.Members); // stale owner notification
        Require(r.Host.Snapshot().Phase == LobbyPhase.Waiting, "Old owner notification closed lobby");
        Reject(() => r.Host.UpdateMembership(2, "p2", r.Members));
        r.Host.UpdateMembership(3, "p1", r.Members.Take(2));
        Require(r.Host.Snapshot().Members.Count == 2 && r.Host.Snapshot().Members.All(m => !m.Ready), "Departure retained ready");
        Reject(() => r.Host.SetReady("p3", oldReady));
        var rejoined = r.Members.Select(p => p.Player.Id == "p3" ? p with { SessionId = Guid.NewGuid() } : p).ToArray();
        r.Host.UpdateMembership(4, "p1", rejoined);
        Reject(() => r.Host.SetReady("p3", oldReady with { Revision = r.Host.Snapshot().Revision }));
        r.ReadyAll(); Require(r.Host.Snapshot().CanStart, "Rejoin cannot prepare again");
        r.Host.UpdateMembership(5, "p2", rejoined);
        Require(r.Host.Snapshot().Phase == LobbyPhase.Closed && !r.Host.Snapshot().CanStart, "Owner migration silently accepted");
        Reject(() => r.Start()); r.Host.UpdateMembership(6, "p1", rejoined);
        Require(r.Host.Snapshot().Phase == LobbyPhase.Closed, "Closed room restarted by notification");
        var absentHost = new Room(); absentHost.Host.UpdateMembership(2, "p1", absentHost.Members.Skip(1));
        Require(absentHost.Host.Snapshot().Phase == LobbyPhase.Closed, "Missing host did not close");
        var newPeer = new Room(2); newPeer.ReadyAll();
        newPeer.Host.UpdateMembership(2, "p1", newPeer.Members.Append(new(new("p3", "참가자 3"), Guid.NewGuid())));
        Require(newPeer.Host.Snapshot().Members.All(m => !m.Ready), "Join retained ready");
    }

    private static void FrozenStart()
    {
        var r = new Room(); r.ReadyAll(); var before = r.Host.Snapshot(); Guid request = Guid.NewGuid();
        var plan = r.Host.Start("p1", before.Revision, request);
        Require(ReferenceEquals(plan, r.Host.Start("p1", before.Revision, request)), "Start retry created another match");
        Reject(() => r.Host.Start("p1", before.Revision, Guid.NewGuid()));
        Reject(() => r.Host.Start("p1", before.Revision + 1, request));
        Reject(() => r.Host.SetReady("p1", r.Request("p1", false)));
        Reject(() => r.Host.UpdateSettings("p1", before.Revision, before.Settings));
        Reject(() => plan.ContextFor("outsider"));
        foreach (var p in plan.Participants)
        {
            var context = plan.ContextFor(p.Player.Id);
            Require(context.MatchId == plan.MatchId && context.HostId == "p1" && context.LocalId == p.Player.Id
                && context.Participants.Count == 3 && context.Compatibility.Matches(Compatible), "Invalid runtime handoff");
        }
        Require(plan.Participants is not MultiplayerParticipant[] && before.Members is not LobbyMemberState[], "Mutable backing array exposed");
        r.Members[0] = new(new("replacement", "변경"), Guid.NewGuid());
        Require(plan.Participants[0].Player.Id == "p1" && r.Host.Snapshot().Members[0].Member.Player.Id == "p1", "Caller array mutated roster");
        r.Host.UpdateMembership(2, "p2", r.Members.Skip(1));
        Require(r.Host.Snapshot().Phase == LobbyPhase.Closed && plan.Participants.Count == 3, "Post-start change lost immutable match plan");
        Reject(() => r.Host.Start("p1", before.Revision, request));
        var joined = new Room(2); joined.ReadyAll(); joined.Start();
        joined.Host.UpdateMembership(2, "p1", joined.Members.Append(new(new("p3", "새 참가자"), Guid.NewGuid())));
        Require(joined.Host.Snapshot().Phase == LobbyPhase.Closed, "Mid-match join silently changed roster");
    }

    private static void Limits()
    {
        var one = new Room(1); one.ReadyAll(); Require(!one.Host.Snapshot().CanStart, "One participant admitted"); Reject(() => one.Start());
        var two = new Room(2); two.ReadyAll(); Require(two.Start().Participants.Count == 2, "Two-player match forced to 3+");
        var full = new Room(16); full.ReadyAll(); Require(full.Start().Participants.Count == 16, "Core 16-player bound broken");
        Reject(() => new Room(17));
        var r = new Room(); var s = r.Host.Snapshot();
        Reject(() => r.Host.UpdateMembership(2, "p1", [r.Members[0], r.Members[0]]));
        Reject(() => r.Host.UpdateMembership(2, "p1", [r.Members[0], r.Members[1] with { SessionId = r.Members[0].SessionId }]));
        Reject(() => r.Host.UpdateMembership(2, "p1", [r.Members[0] with { Player = new("p1", "a\nb") }]));
        Require(r.Host.Snapshot().Revision == s.Revision && r.Host.Snapshot().Members.Count == 3, "Invalid roster partially applied");
        foreach (var invalid in new[] { s.Settings with { RoundCount = 0 }, s.Settings with { RoundCount = 11 },
            s.Settings with { RoundPolicy = s.Settings.RoundPolicy with { WritingDuration = TimeSpan.Zero } },
            s.Settings with { RoundPolicy = s.Settings.RoundPolicy with { GenerationDuration = TimeSpan.FromSeconds(601) } } })
            Reject(() => r.Host.UpdateSettings("p1", s.Revision, invalid));
        Require(r.Host.Snapshot().Revision == s.Revision, "Invalid settings changed revision");
        Reject(() => r.Host.Start("p1", s.Revision, Guid.Empty));
    }

    private static void ConcurrentStart()
    {
        var r = new Room(); r.ReadyAll(); int starts = 0;
        Parallel.For(0, 16, _ =>
        {
            try { r.Start(); Interlocked.Increment(ref starts); }
            catch (InvalidOperationException) { }
        });
        Require(starts == 1, "Concurrent starts opened multiple matches");
    }
}
