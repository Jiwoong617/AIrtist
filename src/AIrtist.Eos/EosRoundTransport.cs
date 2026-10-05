using AIrtist.Core;

namespace AIrtist.Eos;

// One platform, one immutable lobby epoch, host/client star topology, one SDK Tick thread.
public sealed class EosRoundTransport : IBootstrapTransport
{
    public const int MaxPacketBytes = 1024, MaxQueuedPackets = 256, MaxScanPackets = 256;
    public const ulong QueueBytes = 256 * 1024;
    internal const byte HelloChannel = 8;
    private static readonly byte[] Hello = [1];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30), HelloInterval = TimeSpan.FromMilliseconds(500);
    private readonly IEosP2PApi api;
    private readonly TimeProvider clock;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly HashSet<string> members, peers, connected = new(StringComparer.Ordinal);
    private readonly string hostId;
    private readonly Queue<RoundInboundPacket> loopback = new();
    // EOS allows 1170 bytes: receive the full packet, then reject >1024 (never silently truncate).
    private readonly byte[] receiveBuffer = new byte[1170];
    private readonly long created;
    private long lastHello;
    private bool backendClosed, disposed, helloSent;
    public string LocalId { get; }
    public string SocketName { get; }
    public EosTransportSnapshot Snapshot { get; private set; }
    public bool IsDisposed => disposed;
    public ConnectionReadiness Readiness => Snapshot.State switch
    {
        EosTransportState.Connecting => ConnectionReadiness.Connecting,
        EosTransportState.Ready => ConnectionReadiness.Ready,
        _ => ConnectionReadiness.Failed
    };

    internal EosRoundTransport(EosTransportContext context, IEosP2PApi api, TimeProvider? clock = null)
    {
        Validate(context);
        this.api = api;
        this.clock = clock ?? TimeProvider.System;
        LocalId = context.LocalId; hostId = context.HostId; SocketName = context.ScopeId.ToString("N");
        members = new(context.Members, StringComparer.Ordinal);
        peers = new(LocalId == hostId ? members.Where(id => id != LocalId) : [hostId], StringComparer.Ordinal);
        created = this.clock.GetTimestamp();
        Snapshot = new(EosTransportState.Connecting, 0, peers.Count, "P2P 연결 준비 중");
        try
        {
            if (!api.Start(OnSignal, QueueBytes)) throw new InvalidOperationException("EOS P2P 큐/알림 초기화 실패");
            foreach (var id in peers)
                if (!api.Accept(id)) throw new InvalidOperationException("EOS P2P 참가자 연결 허용 실패");
            if (peers.Count == 0) Ready();
        }
        catch
        {
            disposed = true;
            Snapshot = new(EosTransportState.Failed, 0, peers.Count, "P2P 초기화 실패");
            api.Dispose(); backendClosed = true; throw;
        }
    }

    internal static void Validate(EosTransportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ScopeId == Guid.Empty || context.Members is null || context.Members.Count is < 1 or > 16 ||
            context.Members.Any(id => !IsProductId(id)) || context.Members.Distinct(StringComparer.Ordinal).Count() != context.Members.Count ||
            !context.Members.Contains(context.LocalId, StringComparer.Ordinal) || !context.Members.Contains(context.HostId, StringComparer.Ordinal))
            throw new ArgumentException("인증된 EOS 대기방 명단과 새 연결 범위를 확인하세요.");
    }

    private static bool IsProductId(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Call after EOS Platform.Tick; do not create a Core lobby session until the service-level all-member barrier is ready.
    public void Pump()
    {
        VerifyThread();
        if (disposed) return;
        if (Snapshot.State == EosTransportState.Failed) { CloseBackend(); return; }
        if (Snapshot.State != EosTransportState.Connecting) return;
        if (clock.GetElapsedTime(created) >= ConnectTimeout) { Fail("P2P 연결 준비 시간 초과"); CloseBackend(); return; }
        if (helloSent && clock.GetElapsedTime(lastHello) < HelloInterval) return;
        helloSent = true; lastHello = clock.GetTimestamp();
        foreach (var peer in peers.Where(id => !connected.Contains(id)))
        {
            if (!CanQueue(Hello.Length)) break;
            if (api.Send(peer, HelloChannel, Hello, true) == EosSendResult.Failed) { Fail("P2P 연결 요청 실패"); break; }
        }
    }

    public bool IsConnected(string peerId)
    {
        VerifyThread();
        return !disposed && Snapshot.State == EosTransportState.Ready &&
            (peerId == LocalId || connected.Contains(peerId));
    }

    public bool TrySend(string peerId, RoundPacketKind kind, byte[] bytes)
    {
        VerifyThread();
        if (disposed || Snapshot.State != EosTransportState.Ready || bytes is null || bytes.Length is < 1 or > MaxPacketBytes ||
            !Route(LocalId, peerId, kind)) return false;
        if (peerId == LocalId)
        {
            if (loopback.Count >= MaxQueuedPackets) return false;
            loopback.Enqueue(new(LocalId, kind, (byte[])bytes.Clone())); return true;
        }
        if (!connected.Contains(peerId) || !CanQueue(bytes.Length)) return false;
        var result = api.Send(peerId, (byte)kind, bytes, false);
        if (result == EosSendResult.Failed) Fail("P2P 송신 연결 오류");
        return result == EosSendResult.Sent;
    }

    public IReadOnlyList<RoundInboundPacket> Receive(int maxPackets)
    {
        VerifyThread();
        if (maxPackets is < 1 or > MaxQueuedPackets) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        var output = new List<RoundInboundPacket>();
        if (disposed || Snapshot.State != EosTransportState.Ready) return output;
        while (loopback.Count != 0 && output.Count < maxPackets) output.Add(loopback.Dequeue());
        for (var scanned = 0; scanned < MaxScanPackets && output.Count < maxPackets; scanned++)
        {
            var result = api.Receive(receiveBuffer, out var packet);
            if (result == EosReceiveResult.Empty) break;
            if (result == EosReceiveResult.Failed) { Fail("P2P 수신 연결 오류"); output.Clear(); break; }
            if (packet.SocketName != SocketName || !connected.Contains(packet.SenderId) || packet.Length is < 1 or > MaxPacketBytes ||
                packet.Channel > (byte)RoundPacketKind.LobbyControlToClient) continue;
            var kind = (RoundPacketKind)packet.Channel;
            if (!Route(packet.SenderId, LocalId, kind)) continue;
            output.Add(new(packet.SenderId, kind, receiveBuffer.AsSpan(0, packet.Length).ToArray()));
        }
        return output;
    }

    // Call from the trusted EOS lobby membership callback, never from a packet or a UI form.
    public void ObserveMembership(Guid scopeId, string ownerId, IReadOnlyList<string> memberIds)
    {
        VerifyThread();
        if (disposed) return;
        if (scopeId.ToString("N") != SocketName || ownerId != hostId || memberIds is null || memberIds.Count != members.Count || !members.SetEquals(memberIds))
            Fail("방장 또는 참가자가 변경되어 새 연결 범위가 필요합니다.");
    }

    internal void Fail(string message)
    {
        VerifyThread();
        if (disposed || Snapshot.State == EosTransportState.Failed) return;
        connected.Clear(); loopback.Clear();
        Snapshot = new(EosTransportState.Failed, 0, peers.Count, message);
        // Cleanup is deferred until Pump/Dispose, outside an EOS callback.
    }

    private bool CanQueue(int bytes)
    {
        if (!api.GetQueueInfo(out var info)) { Fail("P2P 송신 큐 상태 확인 실패"); return false; }
        return info.OutgoingPackets < MaxQueuedPackets && info.OutgoingBytes <= QueueBytes - (ulong)bytes;
    }

    private bool Route(string from, string to, RoundPacketKind kind)
    {
        if (!Enum.IsDefined(kind) || !members.Contains(from) || !members.Contains(to)) return false;
        return kind is RoundPacketKind.ControlToHost or RoundPacketKind.ImageToHost or RoundPacketKind.MatchControlToHost or RoundPacketKind.LobbyControlToHost
            ? to == hostId : from == hostId;
    }

    private void OnSignal(EosPeerSignal signal)
    {
        VerifyThread();
        if (disposed || Snapshot.State == EosTransportState.Failed || signal.LocalId != LocalId || signal.SocketName != SocketName) return;
        if (Snapshot.State == EosTransportState.Connecting && clock.GetElapsedTime(created) >= ConnectTimeout)
        { Fail("P2P 연결 준비 시간 초과"); return; }
        if (!peers.Contains(signal.PeerId))
        {
            if (signal.Kind == EosPeerSignalKind.Requested) api.Reject(signal.PeerId);
            return;
        }
        switch (signal.Kind)
        {
            case EosPeerSignalKind.Requested:
                if (!api.Accept(signal.PeerId)) Fail("P2P 연결 수락 실패");
                break;
            case EosPeerSignalKind.Established:
                connected.Add(signal.PeerId);
                Snapshot = Snapshot with { ConnectedPeers = connected.Count };
                if (connected.Count == peers.Count) Ready();
                break;
            case EosPeerSignalKind.Interrupted:
            case EosPeerSignalKind.Closed:
                Fail("참가자 P2P 연결이 끊겼습니다.");
                break;
        }
    }

    private void Ready() => Snapshot = new(EosTransportState.Ready, connected.Count, peers.Count, "P2P 연결 준비 완료");
    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("P2P는 EOS 네트워크 스레드에서만 사용하세요.");
    }
    private void CloseBackend() { if (!backendClosed) { api.Dispose(); backendClosed = true; } }
    public void Dispose()
    {
        VerifyThread();
        if (disposed) return;
        disposed = true; connected.Clear(); loopback.Clear(); CloseBackend();
        Snapshot = new(EosTransportState.Disposed, 0, peers.Count, "P2P 연결 종료");
    }
}
