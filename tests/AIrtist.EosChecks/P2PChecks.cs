using AIrtist.Core;
using AIrtist.Eos;
using Epic.OnlineServices.P2P;

internal static class P2PChecks
{
    private static readonly string H = new('1', 32), A = new('2', 32), B = new('3', 32), Stranger = new('4', 32);
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("잘못된 전송 조작이 허용되었습니다.");
    }
    private sealed class Clock : TimeProvider
    {
        private long value;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => value;
        public void Advance(TimeSpan time) => value += time.Ticks;
    }
    private sealed class Api(string localId, string socketName) : IEosP2PApi
    {
        public readonly Queue<(EosReceivedPacket Info, byte[] Bytes)> Incoming = new();
        public readonly List<(string Peer, byte Channel, byte[] Bytes, bool Delayed)> Sent = new();
        public readonly HashSet<string> Accepted = new(), Rejected = new();
        public readonly string LocalId = localId, Socket = socketName;
        public Action<EosPeerSignal>? Notify;
        public Action<string, byte, byte[]>? Deliver;
        public EosQueueInfo Queue;
        public ulong QueueLimit;
        public bool StartSucceeds = true, AcceptSucceeds = true, QueueSucceeds = true, ReceiveFails;
        public EosSendResult SendResult = EosSendResult.Sent;
        public int ReceiveCalls, DisposeCalls;
        public bool Start(Action<EosPeerSignal> notify, ulong queueBytes) { Notify = notify; QueueLimit = queueBytes; return StartSucceeds; }
        public bool Accept(string id) { Accepted.Add(id); return AcceptSucceeds; }
        public void Reject(string id) => Rejected.Add(id);
        public bool GetQueueInfo(out EosQueueInfo info) { info = Queue; return QueueSucceeds; }
        public EosSendResult Send(string id, byte channel, byte[] bytes, bool delayed)
        {
            Sent.Add((id, channel, (byte[])bytes.Clone(), delayed));
            if (SendResult == EosSendResult.Sent) Deliver?.Invoke(id, channel, bytes);
            return SendResult;
        }
        public EosReceiveResult Receive(byte[] buffer, out EosReceivedPacket packet)
        {
            ReceiveCalls++; packet = default;
            if (ReceiveFails) return EosReceiveResult.Failed;
            if (!Incoming.TryDequeue(out var next)) return EosReceiveResult.Empty;
            packet = next.Info; next.Bytes.CopyTo(buffer, 0); return EosReceiveResult.Packet;
        }
        public void Inject(string from, byte channel, byte[] bytes, string? socket = null) =>
            Incoming.Enqueue((new(from, socket ?? Socket, channel, bytes.Length), (byte[])bytes.Clone()));
        public void Signal(string peer, EosPeerSignalKind kind, string? socket = null, string? local = null) =>
            Notify?.Invoke(new(local ?? LocalId, peer, socket ?? Socket, kind));
        // Leave the handler accessible to tests to simulate a stale callback after unsubscribe.
        public void Dispose() => DisposeCalls++;
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Scope = Guid.NewGuid();
        public readonly Clock Clock = new();
        public readonly Dictionary<string, Api> Apis = new();
        public readonly Dictionary<string, EosRoundTransport> Transports = new();
        public readonly string[] Members = [H, A, B];
        public World()
        {
            foreach (var id in Members)
            {
                var api = new Api(id, Scope.ToString("N")); Apis.Add(id, api);
                Transports.Add(id, new(new(Scope, H, id, Members), api, Clock));
                api.Deliver = (to, channel, data) => Apis[to].Inject(id, channel, data);
            }
        }
        public void Ready()
        {
            Apis[H].Signal(A, EosPeerSignalKind.Established);
            Apis[H].Signal(B, EosPeerSignalKind.Established);
            Apis[A].Signal(H, EosPeerSignalKind.Established);
            Apis[B].Signal(H, EosPeerSignalKind.Established);
        }
        public void Dispose() { foreach (var transport in Transports.Values) transport.Dispose(); }
    }

    public static void Run()
    {
        Require(P2PInterface.MAX_PACKET_SIZE == 1170 && P2PInterface.SOCKETID_SOCKETNAME_SIZE == 33, "고정 SDK의 패킷/소켓 크기가 바뀌었습니다.");
        WithWorld(w =>
        {
            var host = w.Transports[H]; var api = w.Apis[H];
            Require(api.QueueLimit == EosRoundTransport.QueueBytes && api.Accepted.SetEquals([A, B]), "큐 제한/허용 명단 불일치");
            Require(w.Apis[A].Accepted.SetEquals([H]), "클라이언트가 방장 외 연결 허용");
            Require(!host.IsConnected(H) && !host.TrySend(A, RoundPacketKind.ControlToClient, [1]), "연결 전 게임 메시지 허용");
            host.Pump(); Require(api.Sent.Count == 2 && api.Sent.All(p => p.Delayed && p.Channel == EosRoundTransport.HelloChannel), "연결 hello 불일치");
            host.Pump(); Require(api.Sent.Count == 2, "hello 무제한 반복");
            api.Signal(Stranger, EosPeerSignalKind.Requested); Require(api.Rejected.Contains(Stranger) && !api.Accepted.Contains(Stranger), "낯선 상대 수락");
            api.Signal(A, EosPeerSignalKind.Established, Guid.NewGuid().ToString("N"));
            api.Signal(A, EosPeerSignalKind.Established, local: B);
            Require(host.Snapshot.ConnectedPeers == 0, "다른 소켓/로컬 ID 알림 수락");
            api.Signal(A, EosPeerSignalKind.Established);
            Require(!host.IsConnected(A), "일부 연결만으로 대기방 시작 가능");
            w.Clock.Advance(TimeSpan.FromMilliseconds(500)); host.Pump();
            Require(api.Sent.Count == 3 && api.Sent[^1].Peer == B, "연결된 상대에게 hello를 계속 보내거나 재시도 주기 오류");
            w.Ready(); Require(w.Transports.Values.All(t => t.Snapshot.State == EosTransportState.Ready), "연결 알림 반영 실패");
            Require(!w.Transports[A].IsConnected(B), "클라이언트 간 직접 연결 허용");
        });
        WithWorld(w =>
        {
            w.Ready();
            foreach (var kind in Enum.GetValues<RoundPacketKind>())
            {
                var toHost = (byte)kind % 2 == 0;
                var from = toHost ? A : H; var to = toHost ? H : A;
                byte[] bytes = [4, 2, (byte)kind];
                Require(w.Transports[from].TrySend(to, kind, bytes), "8종 채널 송신 실패");
                bytes[0] = 99;
                var received = w.Transports[to].Receive(1).Single();
                Require(received.AuthenticatedSender == from && received.Kind == kind && received.Bytes[0] == 4, "송신자/채널/버퍼 복사 불일치");
                Require(!w.Apis[from].Sent.Last().Delayed, "게임 패킷에 지연 배달 허용");
                Require(!w.Transports[to].TrySend(from, kind, [1]), "반대 권한 방향 송신 허용");
            }
            Require(!w.Transports[A].TrySend(B, RoundPacketKind.ControlToHost, [1]), "클라이언트간 송신 허용");
            var host = w.Transports[H]; var api = w.Apis[H];
            foreach (var data in new[] { Array.Empty<byte>(), new byte[1025] }) Require(!host.TrySend(A, RoundPacketKind.ImageToClient, data), "잘못된 크기 송신 허용");
            Require(!host.TrySend(A, (RoundPacketKind)99, [1]), "알 수 없는 종류 송신 허용");
            api.Inject(Stranger, 0, [1]); api.Inject(A, 0, [1], Guid.NewGuid().ToString("N"));
            api.Inject(A, 1, [1]); api.Inject(A, 77, [1]); api.Inject(A, 0, []); api.Inject(A, 0, new byte[1170]);
            api.Inject(A, EosRoundTransport.HelloChannel, [1]);
            api.Inject(B, 0, System.Text.Encoding.UTF8.GetBytes(H));
            var accepted = host.Receive(256);
            Require(accepted.Count == 1 && accepted[0].AuthenticatedSender == B, "위조 대상/소켓/크기/방향 필터 또는 SDK 송신자 검증 실패");
            for (int i = 0; i < 300; i++) api.Inject(Stranger, 0, [1]);
            var before = api.ReceiveCalls; host.Receive(256);
            Require(api.ReceiveCalls - before == EosRoundTransport.MaxScanPackets && api.Incoming.Count == 44, "거절 패킷 처리 예산 초과");
        });
        WithWorld(w =>
        {
            w.Ready(); var host = w.Transports[H]; var api = w.Apis[H];
            api.Queue = new(EosRoundTransport.QueueBytes - 1024, 255);
            Require(host.TrySend(A, RoundPacketKind.ImageToClient, new byte[1024]), "상한 경계의 패킷 거절");
            api.Queue = new(EosRoundTransport.QueueBytes - 1023, 255);
            Require(!host.TrySend(A, RoundPacketKind.ImageToClient, new byte[1024]), "송신 바이트 상한 초과");
            api.Queue = new(0, 256); Require(!host.TrySend(A, RoundPacketKind.ImageToClient, [1]), "송신 개수 상한 초과");
            api.Queue = default; api.SendResult = EosSendResult.Busy;
            Require(!host.TrySend(A, RoundPacketKind.ControlToClient, [1]) && host.Snapshot.State == EosTransportState.Ready, "큐 혼잡을 연결 실패로 오인");
            for (int i = 0; i < 256; i++) Require(host.TrySend(H, RoundPacketKind.ControlToHost, [1]), "루프백 큐 조기 거절");
            Require(!host.TrySend(H, RoundPacketKind.ControlToHost, [1]), "루프백 개수 초과");
            Require(host.Receive(17).Count == 17 && host.TrySend(H, RoundPacketKind.ControlToHost, [1]), "루프백 소비 후 복구 실패");
            Reject(() => host.Receive(257)); Reject(() => host.Receive(0));
            api.QueueSucceeds = false;
            Require(!host.TrySend(A, RoundPacketKind.ControlToClient, [1]) && host.Snapshot.State == EosTransportState.Failed, "큐 조회 오류 후 송신 허용");
        });
        WithWorld(w =>
        {
            w.Clock.Advance(TimeSpan.FromSeconds(30));
            w.Apis[H].Signal(A, EosPeerSignalKind.Established); w.Apis[H].Signal(B, EosPeerSignalKind.Established);
            Require(w.Transports[H].Snapshot.State == EosTransportState.Failed, "마감 뒤 연결 알림으로 부활");
            w.Transports[H].Pump(); Require(w.Apis[H].DisposeCalls == 1, "실패 후 SDK 정리 누락");
        });
        foreach (var signal in new[] { EosPeerSignalKind.Closed, EosPeerSignalKind.Interrupted })
            WithWorld(w =>
            {
                w.Ready(); var host = w.Transports[H]; host.TrySend(H, RoundPacketKind.ControlToHost, [1]);
                w.Apis[H].Signal(A, signal); w.Apis[H].Signal(A, EosPeerSignalKind.Established);
                Require(host.Snapshot.State == EosTransportState.Failed && !host.IsConnected(A) && host.Receive(256).Count == 0, "끊긴 연결 재사용/잔여 패킷 반환");
                host.Pump(); host.Dispose(); host.Dispose();
                Require(w.Apis[H].DisposeCalls == 1, "SDK 중복 해제");
            });
        foreach (var change in new[] { 0, 1, 2 })
            WithWorld(w =>
            {
                w.Ready(); var host = w.Transports[H];
                host.ObserveMembership(w.Scope, H, [B, A, H]); Require(host.Snapshot.State == EosTransportState.Ready, "명단 순서 변경으로 중단");
                host.ObserveMembership(change == 0 ? Guid.NewGuid() : w.Scope, change == 1 ? A : H, change == 2 ? [H, A] : w.Members);
                Require(host.Snapshot.State == EosTransportState.Failed, "멤버/방장/범위 변경 무시");
            });
        WithWorld(w =>
        {
            w.Ready(); var host = w.Transports[H];
            Task.Run(() => Reject(() => host.TrySend(A, RoundPacketKind.ControlToClient, [1]))).GetAwaiter().GetResult();
            host.Dispose(); w.Apis[H].Signal(A, EosPeerSignalKind.Established);
            Require(host.Snapshot.State == EosTransportState.Disposed && !host.TrySend(A, RoundPacketKind.ControlToClient, [1]), "종료 후 콜백/송신 허용");
            w.Apis[A].ReceiveFails = true; Require(w.Transports[A].Receive(1).Count == 0 && w.Transports[A].Snapshot.State == EosTransportState.Failed, "수신 실패 처리 누락");
            w.Apis[B].SendResult = EosSendResult.Failed;
            Require(!w.Transports[B].TrySend(H, RoundPacketKind.ControlToHost, [1]) && w.Transports[B].Snapshot.State == EosTransportState.Failed, "송신 실패 처리 누락");
        });
        var context = new EosTransportContext(Guid.NewGuid(), H, H, [H, A]);
        foreach (var bad in new[] { context with { ScopeId = Guid.Empty }, context with { LocalId = Stranger }, context with { HostId = Stranger },
            context with { Members = [H, H] }, context with { Members = [H, "bad-id"] }, context with { Members = [] },
            context with { Members = Enumerable.Range(1, 17).Select(i => i.ToString("x32")).ToArray() } })
            Reject(() => EosRoundTransport.Validate(bad));
        var failing = new Api(H, context.ScopeId.ToString("N")) { StartSucceeds = false };
        Reject(() => new EosRoundTransport(context, failing)); Require(failing.DisposeCalls == 1, "초기화 실패 정리 누락");
        var acceptFailure = new Api(H, context.ScopeId.ToString("N")) { AcceptSucceeds = false };
        Reject(() => new EosRoundTransport(context, acceptFailure));
        acceptFailure.Accepted.Clear(); acceptFailure.Signal(A, EosPeerSignalKind.Requested);
        Require(acceptFailure.DisposeCalls == 1 && acceptFailure.Accepted.Count == 0, "초기화 실패 후 늦은 연결 요청 수락");
        var timeoutApi = new Api(H, context.ScopeId.ToString("N")); var timeoutClock = new Clock();
        using (var timeout = new EosRoundTransport(context, timeoutApi, timeoutClock))
        {
            timeoutClock.Advance(TimeSpan.FromSeconds(30)); timeout.Pump();
            Require(timeout.Snapshot.State == EosTransportState.Failed && timeoutApi.DisposeCalls == 1, "알림 없는 연결 시간 초과 미처리");
        }
        var editableMembers = new[] { H, A };
        var immutableApi = new Api(H, context.ScopeId.ToString("N"));
        using (var immutable = new EosRoundTransport(context with { Members = editableMembers }, immutableApi))
        {
            editableMembers[1] = Stranger;
            immutableApi.Signal(Stranger, EosPeerSignalKind.Requested);
            Require(immutableApi.Rejected.Contains(Stranger), "호출자의 배열 수정으로 허용 명단 확대");
        }
        Console.WriteLine("PASS: EOS P2P SDK 경계 모의 검사 — 8종 전송, SDK 송신자·명단·소켓·방향, 큐/크기/처리 예산, 연결 마감·이탈·정리. 실제 EOS 연결은 미검증.");
    }
    private static void WithWorld(Action<World> action) { using var world = new World(); action(world); }
}
