using AIrtist.Core;

internal static class ConnectionBootstrapChecks
{
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static readonly Word[] Words = [new("apple", "사과", "둥근 과일", "apple", ["사과", "apple"])];
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; } throw new InvalidOperationException("잘못된 연결 합의 요청 허용"); }
    private sealed class NoAi : IDrawingAi
    {
        public string Translate(string text, CancellationToken token) => throw new InvalidOperationException("연결/대기방에서 AI 실행 금지");
        public byte[] Generate(string text, int seed, CancellationToken token, Action<string> progress) => throw new InvalidOperationException("연결/대기방에서 AI 실행 금지");
    }
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    private sealed class World : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly LocalRoundNetwork Network = new();
        public readonly string[] Ids;
        public readonly Dictionary<string, string?> Attributes = new();
        public readonly List<(long At, Action Action)> Pending = [];
        public readonly List<(string Player, bool Owner, string Value, long At)> Writes = [];
        public readonly Service[] Services;
        public readonly LobbyConnectionBootstrap[] Bootstraps;
        public string? Proposal;
        public string Host;
        public string[] Roster;
        public bool FailWrites, NeverComplete;
        public int PublishDelay = 200, CallbackDelay = 300;
        public World(int count = 3)
        {
            Ids = Enumerable.Range(1, count).Select(i => i.ToString("x32")).ToArray(); Host = Ids[0]; Roster = Ids.ToArray();
            Services = Ids.Select((id, i) => new Service(this, id) { Revision = 10 + i * 17 }).ToArray();
            Bootstraps = Services.Select(service => new LobbyConnectionBootstrap(service, Compatibility.ConnectionHash(), Clock)).ToArray();
        }
        public void Step(int steps = 1)
        {
            for (int i = 0; i < steps; i++)
            {
                Clock.Milliseconds += 100;
                var due = Pending.Where(p => p.At <= Clock.Milliseconds).ToArray();
                foreach (var action in due) { Pending.Remove(action); action.Action(); }
                foreach (var bootstrap in Bootstraps) bootstrap.Tick();
            }
        }
        public void Until(Func<bool> condition, int max = 150)
        { for (int i = 0; i < max && !condition(); i++) Step(); Check(condition(), "연결 준비 조건 대기 실패"); }
        public void Ready() => Until(() => Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Ready));
        public void Dispose() { foreach (var b in Bootstraps) b.Dispose(); }
    }
    private sealed class Service(World world, string id) : ILobbyBootstrapService
    {
        public long Revision;
        public bool AllowConnection = true, ReadFails, HoldRelease;
        public Connection? Connection;
        public LobbyBootstrapView? Read()
        {
            if (ReadFails) return null;
            string? proposal = world.Proposal;
            if (HoldRelease && proposal?.EndsWith("|run", StringComparison.Ordinal) == true) proposal = proposal[..^3] + "prepare";
            return new("service-room", world.Host, id, Revision, world.Roster, proposal, world.Attributes);
        }
        public void WriteMember(string value, Action<bool> done) => Write(false, value, done);
        public void WriteProposal(string value, Action<bool> done)
        {
            if (id != world.Host) throw new InvalidOperationException("서비스 방장 권한 위반");
            Write(true, value, done);
        }
        private void Write(bool owner, string value, Action<bool> done)
        {
            world.Writes.Add((id, owner, value, world.Clock.Milliseconds));
            bool success = !world.FailWrites;
            if (success) world.Pending.Add((world.Clock.Milliseconds + world.PublishDelay, () =>
            { if (owner) world.Proposal = value; else world.Attributes[id] = value; }));
            if (!world.NeverComplete) world.Pending.Add((world.Clock.Milliseconds + world.CallbackDelay, () => done(success)));
        }
        public IBootstrapTransport Connect(Guid scope, long revision)
        {
            Check(revision == Revision, "로컬 명단 변경 번호를 다른 PC 번호로 대체함");
            Check(Connection == null, "전송기를 두 번 만듦");
            Connection = new(world, this, world.Network.Connect(id), scope); return Connection;
        }
    }
    private sealed class Connection(World world, Service service, IRoundTransport inner, Guid scope) : IBootstrapTransport
    {
        public Guid Scope => scope;
        private bool disposed;
        public ConnectionReadiness Readiness
        {
            get
            {
                if (disposed) return ConnectionReadiness.Failed;
                bool peers = LocalId == world.Host
                    ? world.Services.All(p => p.AllowConnection && p.Connection?.Scope == scope)
                    : world.Services[0].AllowConnection && world.Services[0].Connection?.Scope == scope;
                return service.AllowConnection && peers ? ConnectionReadiness.Ready : ConnectionReadiness.Connecting;
            }
        }
        public string LocalId => inner.LocalId;
        public bool IsConnected(string id) => Readiness == ConnectionReadiness.Ready && inner.IsConnected(id);
        public bool TrySend(string id, RoundPacketKind kind, byte[] bytes) => IsConnected(id) && inner.TrySend(id, kind, bytes);
        public IReadOnlyList<RoundInboundPacket> Receive(int count) => disposed ? [] : inner.Receive(count);
        public void Dispose() { if (!disposed) { disposed = true; inner.Dispose(); } }
    }
    public static void Run()
    {
        CoreHandoff(); PartialConnections(); DelayedPublication(); MembershipAndReplay(); FailuresAndBounds();
        Console.WriteLine("Connection bootstrap: independent local revisions, shared scope/session IDs, all-peer barrier, Core lobby handoff/start, delay/membership/replay/failure checks passed (mock service/local transport/no AI)");
    }
    private static void CoreHandoff()
    {
        using var w = new World(); Reject(() => w.Bootstraps[0].TakeConnection()); w.Ready();
        var prepared = w.Bootstraps.Select(b => b.TakeConnection()).ToArray();
        Check(prepared.Select(p => p.ScopeId).Distinct().Count() == 1, "공통 연결 ID 불일치");
        Check(prepared.All(p => p.Members.SequenceEqual(prepared[0].Members)), "참가 세션 ID 불일치");
        Reject(() => w.Bootstraps[0].TakeConnection());
        var lower = Compatibility with { AssetsSha256 = Compatibility.AssetsSha256.ToLowerInvariant(), WordsSha256 = Compatibility.WordsSha256.ToLowerInvariant() };
        Check(lower.ConnectionHash() == Compatibility.ConnectionHash(), "해시 대소문자로 호환성이 달라짐");
        var changed = Compatibility with { WordsSha256 = new('C', 64) };
        Check(changed.ConnectionHash() != Compatibility.ConnectionHash(), "단어 목록이 검색 해시에 반영되지 않음");
        Reject(() => MultiplayerLobbySession.FromPreparedConnection(prepared[0], changed, Words, new NoAi()));
        var sessions = prepared.Select(p => MultiplayerLobbySession.FromPreparedConnection(p, Compatibility, Words, new NoAi(),
            p.LocalId == p.HostId ? MultiplayerMatchSettings.Default with { RoundCount = 1 } : null, clock: w.Clock)).ToArray();
        try
        {
            void Tick(int count)
            { for (int i = 0; i < count; i++) { w.Step(); foreach (var s in sessions) s.Tick(); } }
            Tick(20);
            Check(sessions.All(s => s.Snapshot().Lobby is not null && s.Match is null), "준비 연결 이후 Core 대기방 생성 실패/자동 경기 시작");
            foreach (var s in sessions) s.SetReady(s.Snapshot().Lobby!.Revision, true);
            Tick(20); Check(sessions[0].Snapshot().CanStart, "Core 전원 준비 실패");
            sessions[0].Start(sessions[0].Snapshot().Lobby!.Revision); Tick(20);
            Check(sessions.All(s => s.Match is not null), "Core 경기 시작 확인 실패");
            w.Services[1].Revision++; Tick(1);
            Check(w.Bootstraps[1].Snapshot.State == LobbyBootstrapState.Failed && sessions[1].Snapshot().Phase == LobbySessionPhase.Stopped,
                "서비스 명단 변경 후 전달된 Core 연결이 살아 있음");
        }
        finally { foreach (var s in sessions) s.Dispose(); }
        Check(w.Writes.All(p => p.Value.Length <= LobbyConnectionBootstrap.MaxAttributeChars), "속성 상한 초과");
        foreach (var group in w.Writes.GroupBy(p => p.Player))
            Check(group.Zip(group.Skip(1)).All(p => p.Second.At - p.First.At >= 500), "서비스 쓰기 간격 초과");
    }
    private static void PartialConnections()
    {
        using var w = new World(); w.Services[2].AllowConnection = false;
        w.Step(80);
        Check(w.Bootstraps.All(b => b.Snapshot.State != LobbyBootstrapState.Ready), "일부 P2P 준비만으로 대기방을 시작함");
        Check(w.Services[1].Connection?.Readiness == ConnectionReadiness.Ready, "부분 연결 시나리오가 실행되지 않음");
        foreach (var b in w.Bootstraps) Reject(() => b.TakeConnection());
        w.Services[2].AllowConnection = true; w.Ready();
        using var two = new World(2); two.Ready(); Check(two.Bootstraps.All(b => b.Snapshot.ReadyMembers == 2), "2명 준비를 금지함");
        using var sixteen = new World(16); sixteen.Ready(); Check(sixteen.Bootstraps.All(b => b.Snapshot.ReadyMembers == 16), "16명 데이터 상한 준비 실패");
    }
    private static void DelayedPublication()
    {
        using var w = new World { PublishDelay = 1200, CallbackDelay = 100 };
        w.Services[2].HoldRelease = true;
        w.Until(() => w.Bootstraps[0].Snapshot.State == LobbyBootstrapState.Ready);
        Check(w.Bootstraps[2].Snapshot.State != LobbyBootstrapState.Ready, "방장 해제 통지를 보지 않고 준비 완료");
        Reject(() => w.Bootstraps[2].TakeConnection());
        w.Services[2].HoldRelease = false; w.Ready();
        Check(w.Writes.Count == 8, "저장 완료/캐시 지연 때문에 같은 속성을 무한 재작성함");
    }
    private static void MembershipAndReplay()
    {
        using (var w = new World())
        {
            w.Ready(); var old = w.Proposal!; w.Services[0].Revision++; w.Step();
            Check(w.Bootstraps[0].Snapshot.State == LobbyBootstrapState.Failed, "로컬 재입장 이벤트를 무시함");
            w.Proposal = old; w.Step(); Reject(() => w.Bootstraps[0].TakeConnection());
        }
        using (var w = new World())
        {
            w.Ready(); w.Attributes[w.Ids[1]] = w.Attributes[w.Ids[1]]!.Replace(w.Bootstraps[1].Snapshot.ScopeId!.Value.ToString("N"), Guid.Empty.ToString("N"));
            w.Step(); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "준비 철회 후 이전 Ready를 유지함");
        }
        using (var w = new World())
        {
            w.Ready(); string data = w.Attributes[w.Ids[1]]!; var fields = data.Split('|'); fields[1] = Guid.NewGuid().ToString("N");
            w.Attributes[w.Ids[1]] = string.Join('|', fields); w.Step();
            Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "같은 Product ID의 새 세션을 허용함");
        }
        using (var w = new World()) { w.Step(3); w.Host = w.Ids[1]; w.Step(); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "방장 변경 허용"); }
        using (var w = new World()) { w.Ready(); w.Proposal = w.Proposal!.Replace("|run", "|prepare"); w.Step(); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "옛 prepare 재적용"); }
        using (var w = new World()) { w.Ready(); w.Proposal = null; w.Step(); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "방장 합의 삭제 후 이전 Ready 유지"); }
    }
    private static void FailuresAndBounds()
    {
        using (var w = new World { FailWrites = true }) { w.Step(5); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "쓰기 실패 허용"); }
        using (var w = new World { NeverComplete = true })
        {
            w.Step(302); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "저장 응답 마감 실패");
        }
        using (var w = new World { CallbackDelay = 31000 })
        {
            w.Step(313); Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "늦은 저장 성공으로 부활");
        }
        using (var w = new World())
        {
            w.Services[2].AllowConnection = false; w.Step(601);
            Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Failed), "전체 60초 마감 실패");
        }
        using (var w = new World())
        {
            w.Ready(); w.Attributes[w.Ids[2]] = new string('x', 257); w.Step();
            Check(w.Bootstraps[0].Snapshot.State == LobbyBootstrapState.Failed, "속성 크기 초과 허용");
        }
        using (var w = new World())
        {
            w.Ready(); w.Services[0].ReadFails = true; w.Step();
            Check(w.Bootstraps[0].Snapshot.State == LobbyBootstrapState.Failed, "서비스 연결 상실 허용");
        }
        using (var w = new World())
        {
            // Task.Run may inline while a thread-pool caller synchronously waits.
            // A dedicated thread guarantees this exercises the wrong-thread boundary.
            bool rejected = false;
            Exception? unexpected = null;
            var otherThread = new Thread(() =>
            {
                try { w.Bootstraps[0].Tick(); }
                catch (InvalidOperationException) { rejected = true; }
                catch (Exception ex) { unexpected = ex; }
            });
            otherThread.Start(); otherThread.Join();
            Check(rejected && unexpected is null, "스레드 제한 실패");
            w.Step(); foreach (var b in w.Bootstraps) { b.Dispose(); b.Dispose(); } w.Step(30);
            Check(w.Bootstraps.All(b => b.Snapshot.State == LobbyBootstrapState.Disposed), "Dispose 뒤 늦은 저장 응답 반영");
        }
    }
}
