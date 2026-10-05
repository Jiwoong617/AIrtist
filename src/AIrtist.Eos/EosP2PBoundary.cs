using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AIrtist.EosChecks")]

namespace AIrtist.Eos;

// This roster MUST come from the EOS lobby service, not a peer message or UI-supplied player IDs.
// A new shared scope is required whenever the roster, host, or membership sessions change.
public sealed record EosTransportContext(Guid ScopeId, string HostId, string LocalId, IReadOnlyList<string> Members);
public enum EosTransportState { Connecting, Ready, Failed, Disposed }
public sealed record EosTransportSnapshot(EosTransportState State, int ConnectedPeers, int RequiredPeers, string Message);

internal enum EosPeerSignalKind { Requested, Established, Interrupted, Closed }
internal sealed record EosPeerSignal(string LocalId, string PeerId, string SocketName, EosPeerSignalKind Kind);
internal readonly record struct EosReceivedPacket(string SenderId, string SocketName, byte Channel, int Length);
internal readonly record struct EosQueueInfo(ulong OutgoingBytes, ulong OutgoingPackets);
internal enum EosSendResult { Sent, Busy, Failed }
internal enum EosReceiveResult { Packet, Empty, Failed }

// The only fakeable seam is the SDK call surface. Production always uses EosSdkP2PApi.
internal interface IEosP2PApi : IDisposable
{
    bool Start(Action<EosPeerSignal> notify, ulong queueBytes);
    bool Accept(string peerId);
    void Reject(string peerId);
    bool GetQueueInfo(out EosQueueInfo info);
    EosSendResult Send(string peerId, byte channel, byte[] data, bool allowDelayedDelivery);
    EosReceiveResult Receive(byte[] buffer, out EosReceivedPacket packet);
}
