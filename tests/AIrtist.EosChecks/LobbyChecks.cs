using AIrtist.Eos;

internal static class LobbyChecks
{
    private static readonly string H = new('1', 32), A = new('2', 32), B = new('3', 32), Hash = new('a', 64);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("잘못된 로비 작업이 허용됐습니다.");
    }
    private sealed class Clock : TimeProvider
    {
        private long value;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => value;
        public void Advance(int seconds) => value += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Api : IEosLobbyApi
    {
        public Action<EosLobbySignal>? Notify;
        public Action<EosLobbySearchCompletion>? SearchReply;
        public Action<EosLobbyCompletion>? EntryReply, LeaveReply;
        public Action<bool>? AttributeReply;
        public (string Id, bool Owner, string Value)? AttributeWrite;
        public EosLobbyMembership? Membership;
        public string Bucket = "", JoinId = "";
        public readonly List<string> Leaves = [];
        public int MaxMembers, ClearCalls, DisposeCalls, ReadCalls;
        public bool StartResult = true;
        public bool Start(Action<EosLobbySignal> notify) { Notify = notify; return StartResult; }
        public void Search(string bucket, Action<EosLobbySearchCompletion> done) { Bucket = bucket; SearchReply = done; }
        public void Create(string bucket, int max, Action<EosLobbyCompletion> done) { Bucket = bucket; MaxMembers = max; EntryReply = done; }
        public void Join(string id, Action<EosLobbyCompletion> done) { JoinId = id; EntryReply = done; }
        public void Leave(string id, Action<EosLobbyCompletion> done) { Leaves.Add(id); LeaveReply = done; }
        public EosLobbyMembership? ReadMembership(string id) { ReadCalls++; return Membership; }
        public void WriteConnectionAttribute(string id, bool owner, string value, Action<bool> done)
        { AttributeWrite = (id, owner, value); AttributeReply = done; }
        public void ClearSearch() => ClearCalls++;
        public void Dispose() => DisposeCalls++;
        public EosLobbyListing Room(string id = "room-1", string? owner = null, int max = 4, int slots = 2) => new(id, owner ?? H, Bucket, max, slots);
        public void Signal(EosLobbySignalKind kind, string? player = null, string room = "room-1") => Notify!(new(room, player, kind));
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Api Api = new();
        public readonly Clock Clock = new();
        public readonly EosLobbyClient Client;
        public int Invalidations;
        public Fixture(string? local = null) { Client = new(local ?? H, Hash, Api, () => Invalidations++, Clock); }
        public void Host(params string[] members)
        {
            Client.Create(4);
            Api.Membership = new(Api.Room(), members.Length == 0 ? [H] : members);
            Api.EntryReply!(new(true, "room-1"));
            Check(Client.Snapshot.State == EosLobbyState.Joined, "방 생성 후 명단 적용 실패");
        }
        public void Dispose() => Client.Dispose();
    }

    public static void Run()
    {
        SearchJoinLeave(); MembershipChanges(); DeadlineAndCancel(); InvalidServiceData(); ConnectionAttributes(); Lifecycle();
        Console.WriteLine("PASS: EOS 로비 SDK 경계 대역 — 검색/생성/참가/나가기, 명단 검증·변경 중단, 시간 초과·늦은 완료 정리·스레드/수명. 실제 온라인 로비는 검사하지 않았습니다.");
    }
    private static void SearchJoinLeave()
    {
        using var f = new Fixture(A);
        Reject(() => f.Client.Join("invented-room")); Reject(() => f.Client.Create(1)); Reject(() => f.Client.Create(17));
        f.Client.Search(); Reject(f.Client.Search); Reject(() => f.Client.Create(3));
        Check(f.Api.Bucket.Length <= 64, "검색 버킷이 불필요하게 큽니다.");
        var room = f.Api.Room();
        EosLobbyListing[] source = [room, room, room with { LobbyId = "other-version", BucketId = "other" },
            room with { LobbyId = "full", AvailableSlots = 0 }, room with { LobbyId = "too-many", MaxMembers = 17 }];
        f.Api.SearchReply!(new(true, source)); source[0] = room with { LobbyId = "mutated" };
        Check(f.Client.Snapshot.SearchResults.Count == 1 && f.Client.Snapshot.SearchResults[0] == room, "잘못된 방 필터·복사 실패");
        Reject(() => f.Client.Join("other-version"));
        f.Client.Join(room.LobbyId); Reject(f.Client.Search);
        Check(f.Api.JoinId == "room-1", "검색에서 보관한 방만 참가해야 합니다.");
        string[] members = [H, A]; f.Api.Membership = new(room, members);
        f.Api.EntryReply!(new(true, "room-1")); members[0] = B;
        Check(f.Client.Snapshot.Membership!.Members.SequenceEqual(new[] { H, A }), "서비스 명단 복사 실패");
        Check(f.Client.Snapshot.SearchResults.Count == 0, "참가 뒤 이전 검색 결과가 남음");
        Reject(() => f.Client.Join(room.LobbyId));
        f.Client.Leave(); Check(f.Client.Snapshot.State == EosLobbyState.Leaving && f.Api.Leaves.Count == 1, "나가기 확인 대기 실패");
        f.Api.Signal(EosLobbySignalKind.Left, A); // Expected notification can precede leave completion.
        f.Api.LeaveReply!(new(true, "room-1"));
        Check(f.Client.Snapshot.State == EosLobbyState.Idle && f.Client.Snapshot.Membership == null, "나가기 완료 실패");
        f.Client.Search(); f.Api.SearchReply!(new(true, [])); Check(f.Client.Snapshot.SearchResults.Count == 0, "빈 검색 실패");
    }
    private static void MembershipChanges()
    {
        using var f = new Fixture(); f.Host(H, A);
        int before = f.Invalidations, reads = f.Api.ReadCalls;
        f.Api.Signal(EosLobbySignalKind.Left, A, "unrelated"); f.Client.Pump();
        Check(f.Invalidations == before && f.Api.ReadCalls == reads, "다른 방 알림이 영향을 줌");
        f.Api.Signal(EosLobbySignalKind.Updated); f.Api.Signal(EosLobbySignalKind.MemberUpdated, A);
        f.Client.Pump(); Check(f.Api.ReadCalls == reads + 1 && f.Invalidations == before, "변경 없는 갱신·합치기 실패");
        long revision = f.Client.Snapshot.MembershipRevision;
        f.Api.Signal(EosLobbySignalKind.Left, A); f.Api.Signal(EosLobbySignalKind.Joined, A); f.Client.Pump();
        Check(f.Client.Snapshot.State == EosLobbyState.Joined && f.Invalidations == before + 2 && f.Client.Snapshot.MembershipRevision > revision,
            "같은 ID의 재입장이 기존 P2P를 유지함");
        f.Api.Membership = new(f.Api.Room(), [H, A, B]); f.Api.Signal(EosLobbySignalKind.Updated); f.Client.Pump();
        Check(f.Client.Snapshot.Membership!.Members.Count == 3 && f.Invalidations == before + 3, "명단 추가 감지 실패");
        f.Api.Membership = new(f.Api.Room(owner: A), [H, A, B]); f.Api.Signal(EosLobbySignalKind.Updated); f.Client.Pump();
        Check(f.Client.Snapshot.State == EosLobbyState.Failed && f.Client.Snapshot.Membership == null, "캐시 방장 변경을 허용함");
        foreach (var kind in new[] { EosLobbySignalKind.Kicked, EosLobbySignalKind.Disconnected, EosLobbySignalKind.Closed, EosLobbySignalKind.Promoted })
        {
            using var other = new Fixture(); other.Host(H, A); other.Api.Signal(kind, H);
            Check(other.Client.Snapshot.State == EosLobbyState.Failed, "이탈/방장 변경 후 활성 상태");
            other.Api.Signal(EosLobbySignalKind.Joined, H); other.Client.Pump();
            Check(other.Client.Snapshot.State == EosLobbyState.Failed, "늦은 알림으로 부활함");
        }
    }
    private static void DeadlineAndCancel()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var f = new Fixture(); f.Client.Create(2);
            if (cancel) f.Client.Cancel(); else f.Clock.Advance(30);
            f.Api.Membership = new(f.Api.Room(max: 2, slots: 1), [H]);
            f.Api.EntryReply!(new(true, "room-1")); // Timeout is checked inside callback, even before Pump.
            Check(f.Client.Snapshot.State == EosLobbyState.Failed && f.Api.Leaves.SequenceEqual(new[] { "room-1" }), "늦은 생성 성공이 방을 남김");
            Reject(f.Client.Search);
        }
        using (var f = new Fixture(A))
        {
            f.Client.Search(); f.Api.SearchReply!(new(true, [f.Api.Room()])); f.Client.Join("room-1");
            f.Clock.Advance(30); f.Client.Pump(); f.Api.EntryReply!(new(true, "room-1"));
            Check(f.Api.Leaves.Count == 1 && f.Client.Snapshot.State == EosLobbyState.Failed, "늦은 참가 정리 실패");
        }
        using (var f = new Fixture())
        {
            f.Client.Search(); f.Client.Cancel(); int clears = f.Api.ClearCalls;
            f.Api.SearchReply!(new(true, [f.Api.Room()]));
            Check(f.Api.ClearCalls > clears && f.Client.Snapshot.SearchResults.Count == 0, "취소된 검색 핸들 정리 실패");
        }
        using (var f = new Fixture())
        {
            f.Host(); f.Client.Leave(); f.Clock.Advance(30); f.Client.Pump(); f.Api.LeaveReply!(new(true, "room-1"));
            Check(f.Client.Snapshot.State == EosLobbyState.Failed, "늦은 나가기 응답이 실패를 덮어씀");
        }
    }
    private static void InvalidServiceData()
    {
        foreach (var invalid in new[] { "wrong-room", "wrong-owner", "no-local", "duplicate", "bad-id", "wrong-bucket", "oversize", "missing" })
        {
            using var f = new Fixture(); f.Client.Create(4);
            var room = f.Api.Room(); IReadOnlyList<string> members = [H, A];
            switch (invalid)
            {
                case "wrong-room": room = room with { LobbyId = "wrong" }; break;
                case "wrong-owner": room = room with { OwnerId = A }; break;
                case "no-local": members = [A]; break;
                case "duplicate": members = [H, H]; break;
                case "bad-id": members = [H, "claimed-id"]; break;
                case "wrong-bucket": room = room with { BucketId = "wrong" }; break;
                case "oversize": room = room with { MaxMembers = 17 }; break;
            }
            f.Api.Membership = invalid == "missing" ? null : new(room, members);
            f.Api.EntryReply!(new(true, "room-1"));
            Check(f.Client.Snapshot.State == EosLobbyState.Failed && f.Api.Leaves.Count == 1, "잘못된 서비스 명단 허용: " + invalid);
        }
        using (var f = new Fixture(A))
        {
            f.Client.Search(); f.Api.SearchReply!(new(true, [f.Api.Room()])); f.Client.Join("room-1");
            f.Api.EntryReply!(new(true, "different-room"));
            Check(f.Client.Snapshot.State == EosLobbyState.Failed && f.Api.Leaves.Single() == "different-room", "다른 방 참가 완료를 허용함");
        }
        using (var f = new Fixture())
        {
            f.Client.Search(); f.Api.SearchReply!(new(true, Enumerable.Repeat(f.Api.Room(), 21).ToArray()));
            Check(f.Client.Snapshot.State == EosLobbyState.Failed, "검색 개수 상한 초과 허용");
        }
        using (var f = new Fixture()) { f.Client.Create(4); f.Api.EntryReply!(new(false, null)); Check(f.Api.ReadCalls == 0, "실패를 성공으로 처리함"); }
    }
    private static void Lifecycle()
    {
        var api = new Api { StartResult = false };
        Reject(() => new EosLobbyClient(H, Hash, api, () => { })); Check(api.DisposeCalls == 1, "초기화 실패 정리 실패");
        Reject(() => new EosLobbyClient(H, "bad-hash", new Api(), () => { }));
        using var f = new Fixture();
        Check(Task.Run(() => { try { f.Client.Search(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(), "다른 스레드 허용");
        f.Client.Create(4); f.Client.Dispose(); f.Client.Dispose();
        f.Api.EntryReply!(new(true, "room-1")); f.Api.Signal(EosLobbySignalKind.Joined, H); f.Client.Pump();
        Check(f.Api.DisposeCalls == 1 && f.Api.ReadCalls == 0 && f.Client.Snapshot.State == EosLobbyState.Disposed, "Dispose 후 콜백 상태 오염");
        // A disposed client's native late-success cleanup is owned by EosSdkLobbyApi, not this fake.
    }

    private static void ConnectionAttributes()
    {
        using var f = new Fixture(); f.Host(H, A);
        bool? completed = null;
        f.Client.WriteConnectionAttribute(true, "proposal", ok => completed = ok);
        Check(f.Api.AttributeWrite == ("room-1", true, "proposal"), "방장 속성 저장 경계 오류");
        Reject(() => f.Client.WriteConnectionAttribute(false, "member", _ => { }));
        f.Api.AttributeReply!(true); Check(completed == true, "저장 확인을 전달하지 않음");
        var source = new Dictionary<string, string?> { [H] = "host-data", [A] = "client-data" };
        f.Api.Membership = new(f.Api.Room(), [H, A]) { ConnectionProposal = "proposal", MemberConnectionData = source };
        f.Client.Pump(); source[A] = "modified";
        var view = f.Client.ReadConnectionView()!;
        Check(view.Proposal == "proposal" && view.MemberData[A] == "client-data", "연결 속성 복사/조회 오류");
        completed = null;
        f.Client.WriteConnectionAttribute(false, "member", ok => completed = ok);
        f.Api.Signal(EosLobbySignalKind.Left, A); f.Api.Signal(EosLobbySignalKind.Joined, A);
        f.Api.AttributeReply!(true); Check(completed == false, "예전 명단의 속성 저장 성공을 허용함");
        f.Api.Membership = f.Api.Membership with { ConnectionProposal = new string('x', 257) };
        f.Client.Pump(); Check(f.Client.Snapshot.State == EosLobbyState.Failed, "초과 크기의 로비 속성을 허용함");

        using var client = new Fixture(A);
        client.Client.Search(); client.Api.SearchReply!(new(true, [client.Api.Room()])); client.Client.Join("room-1");
        client.Api.Membership = new(client.Api.Room(), [H, A]); client.Api.EntryReply!(new(true, "room-1"));
        Reject(() => client.Client.WriteConnectionAttribute(true, "forged-owner", _ => { }));
        Reject(() => client.Client.WriteConnectionAttribute(false, new string('x', 257), _ => { }));
        client.Client.WriteConnectionAttribute(false, "self", _ => throw new InvalidOperationException("Dispose 뒤 저장 완료 전달"));
        client.Client.Dispose(); client.Api.AttributeReply!(true);
    }
}
