using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

namespace AIrtist.Eos;

internal sealed class EosSdkP2PApi : IEosP2PApi
{
    private readonly P2PInterface p2p;
    private readonly ProductUserId local;
    private readonly Dictionary<string, ProductUserId> peers;
    private readonly SocketId socket;
    private ProductUserId receivedPeer = null!;
    private SocketId receivedSocket = SocketId.Empty;
    private ulong requestId, establishedId, interruptedId, closedId;
    private bool disposed, started;

    internal EosSdkP2PApi(PlatformInterface platform, EosTransportContext context)
    {
        EosRoundTransport.Validate(context);
        p2p = platform.GetP2PInterface();
        local = User(context.LocalId);
        peers = (context.LocalId == context.HostId ? context.Members.Where(id => id != context.LocalId) : [context.HostId])
            .ToDictionary(id => id, User, StringComparer.Ordinal);
        socket = new SocketId { SocketName = context.ScopeId.ToString("N") };
    }

    public bool Start(Action<EosPeerSignal> notify, ulong queueBytes)
    {
        if (started || disposed) return false;
        started = true;
        var queue = new SetPacketQueueSizeOptions { IncomingPacketQueueMaxSizeBytes = queueBytes, OutgoingPacketQueueMaxSizeBytes = queueBytes };
        if (p2p.SetPacketQueueSize(ref queue) != Result.Success) return false;
        var request = new AddNotifyPeerConnectionRequestOptions { LocalUserId = local, SocketId = socket };
        requestId = p2p.AddNotifyPeerConnectionRequest(ref request, this, (ref OnIncomingConnectionRequestInfo info) =>
            notify(Signal(info.LocalUserId, info.RemoteUserId, info.SocketId, EosPeerSignalKind.Requested)));
        var established = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = local, SocketId = socket };
        establishedId = p2p.AddNotifyPeerConnectionEstablished(ref established, this, (ref OnPeerConnectionEstablishedInfo info) =>
            notify(Signal(info.LocalUserId, info.RemoteUserId, info.SocketId, EosPeerSignalKind.Established)));
        var interrupted = new AddNotifyPeerConnectionInterruptedOptions { LocalUserId = local, SocketId = socket };
        interruptedId = p2p.AddNotifyPeerConnectionInterrupted(ref interrupted, this, (ref OnPeerConnectionInterruptedInfo info) =>
            notify(Signal(info.LocalUserId, info.RemoteUserId, info.SocketId, EosPeerSignalKind.Interrupted)));
        var closed = new AddNotifyPeerConnectionClosedOptions { LocalUserId = local, SocketId = socket };
        closedId = p2p.AddNotifyPeerConnectionClosed(ref closed, this, (ref OnRemoteConnectionClosedInfo info) =>
            notify(Signal(info.LocalUserId, info.RemoteUserId, info.SocketId, EosPeerSignalKind.Closed)));
        return requestId != 0 && establishedId != 0 && interruptedId != 0 && closedId != 0;
    }

    public bool Accept(string peerId)
    {
        if (disposed || !peers.TryGetValue(peerId, out var remote)) return false;
        var options = new AcceptConnectionOptions { LocalUserId = local, RemoteUserId = remote, SocketId = socket };
        return p2p.AcceptConnection(ref options) == Result.Success;
    }

    public void Reject(string peerId)
    {
        if (disposed) return;
        var remote = ProductUserId.FromString(peerId);
        if (remote == null || !remote.IsValid()) return;
        var options = new CloseConnectionOptions { LocalUserId = local, RemoteUserId = remote, SocketId = socket };
        p2p.CloseConnection(ref options);
    }

    public bool GetQueueInfo(out EosQueueInfo info)
    {
        info = default;
        if (disposed) return false;
        var options = new GetPacketQueueInfoOptions();
        if (p2p.GetPacketQueueInfo(ref options, out var status) != Result.Success) return false;
        info = new(status.OutgoingPacketQueueCurrentSizeBytes, status.OutgoingPacketQueueCurrentPacketCount);
        return true;
    }

    public EosSendResult Send(string peerId, byte channel, byte[] data, bool allowDelayedDelivery)
    {
        if (disposed || !peers.TryGetValue(peerId, out var remote) || data.Length is < 1 or > EosRoundTransport.MaxPacketBytes)
            return EosSendResult.Failed;
        var options = new SendPacketOptions
        {
            LocalUserId = local, RemoteUserId = remote, SocketId = socket, Channel = channel, Data = new(data),
            AllowDelayedDelivery = allowDelayedDelivery, DisableAutoAcceptConnection = true,
            // Core already provides bounded ACK/retry/order; do not add another reliable retransmission queue here.
            Reliability = PacketReliability.UnreliableUnordered
        };
        return p2p.SendPacket(ref options) switch
        {
            Result.Success => EosSendResult.Sent,
            Result.LimitExceeded => EosSendResult.Busy,
            _ => EosSendResult.Failed
        };
    }

    public EosReceiveResult Receive(byte[] buffer, out EosReceivedPacket packet)
    {
        packet = default;
        if (disposed || buffer.Length < P2PInterface.MAX_PACKET_SIZE) return EosReceiveResult.Failed;
        var options = new ReceivePacketOptions { LocalUserId = local, MaxDataSizeBytes = P2PInterface.MAX_PACKET_SIZE, RequestedChannel = null };
        var result = p2p.ReceivePacket(ref options, ref receivedPeer, ref receivedSocket, out var channel, new(buffer), out var written);
        if (result == Result.NotFound) return EosReceiveResult.Empty;
        if (result != Result.Success || written > buffer.Length) return EosReceiveResult.Failed;
        // Identity and socket are authenticated SDK outputs. Packet contents do not participate in identity selection.
        packet = new(receivedPeer?.ToString() ?? "", receivedSocket.SocketName ?? "", channel, (int)written);
        return EosReceiveResult.Packet;
    }

    private static EosPeerSignal Signal(ProductUserId local, ProductUserId remote, SocketId? socket, EosPeerSignalKind kind) =>
        new(local?.ToString() ?? "", remote?.ToString() ?? "", socket?.SocketName ?? "", kind);

    private static ProductUserId User(string id)
    {
        var user = ProductUserId.FromString(id);
        if (user == null || !user.IsValid()) throw new ArgumentException("EOS Product User ID 형식을 확인하세요.");
        return user;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (requestId != 0) p2p.RemoveNotifyPeerConnectionRequest(requestId);
        if (establishedId != 0) p2p.RemoveNotifyPeerConnectionEstablished(establishedId);
        if (interruptedId != 0) p2p.RemoveNotifyPeerConnectionInterrupted(interruptedId);
        if (closedId != 0) p2p.RemoveNotifyPeerConnectionClosed(closedId);
        foreach (var peer in peers.Values)
        {
            var clear = new ClearPacketQueueOptions { LocalUserId = local, RemoteUserId = peer, SocketId = socket };
            p2p.ClearPacketQueue(ref clear);
        }
        var close = new CloseConnectionsOptions { LocalUserId = local, SocketId = socket };
        p2p.CloseConnections(ref close);
    }
}
