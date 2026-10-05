namespace AIrtist.Core;

// Shared by game rules and the EOS adapter without pulling AI/WPF dependencies into the latter.
public enum RoundPacketKind
{
    ControlToHost = 0, ControlToClient = 1, ImageToHost = 2, ImageToClient = 3,
    MatchControlToHost = 4, MatchControlToClient = 5, LobbyControlToHost = 6, LobbyControlToClient = 7
}
public sealed record RoundInboundPacket(string AuthenticatedSender, RoundPacketKind Kind, byte[] Bytes);

// Sender identities come from the service/connection, never from the packet body.
// All operations are bounded/nonblocking; false means send queue full/packet lost.
public interface IRoundTransport : IDisposable
{
    string LocalId { get; }
    bool IsConnected(string peerId);
    bool TrySend(string peerId, RoundPacketKind kind, byte[] bytes);
    IReadOnlyList<RoundInboundPacket> Receive(int maxPackets);
}
