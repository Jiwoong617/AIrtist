namespace AIrtist.Core;

// Development-only, bounded local network. No sockets or user authentication.
// Each endpoint pins its simulated identity; payload IDs cannot change it.
public sealed class LocalRoundNetwork
{
    public const int MaxQueuedPackets = 256;
    private readonly object sync = new();
    private readonly Dictionary<string, Endpoint> endpoints = new(StringComparer.Ordinal);
    public Func<string, string, RoundPacketKind, byte[], bool>? DropPacket { get; set; }
    public IRoundTransport Connect(string id)
    {
        lock (sync)
        {
            VotingRoundHost.ValidateId(id);
            if (endpoints.Count >= 16 || endpoints.ContainsKey(id)) throw new InvalidOperationException("참가자 연결을 확인해 주세요.");
            var endpoint = new Endpoint(this, id); endpoints.Add(id, endpoint); return endpoint;
        }
    }
    private sealed class Endpoint(LocalRoundNetwork network, string id) : IRoundTransport
    {
        private readonly Queue<RoundInboundPacket> packets = new();
        private bool closed;
        public string LocalId => id;
        public bool IsConnected(string peerId) { lock (network.sync) return !closed && network.endpoints.TryGetValue(peerId, out var peer) && !peer.closed; }
        public bool TrySend(string peerId, RoundPacketKind kind, byte[] bytes)
        {
            lock (network.sync)
            {
                if (closed || bytes is null || bytes.Length is 0 or > 1024 || !Enum.IsDefined(kind)
                    || !network.endpoints.TryGetValue(peerId, out var peer) || peer.closed || peer.packets.Count >= MaxQueuedPackets) return false;
                if (network.DropPacket?.Invoke(id, peerId, kind, bytes) == true) return true;
                peer.packets.Enqueue(new(id, kind, (byte[])bytes.Clone())); return true;
            }
        }
        public IReadOnlyList<RoundInboundPacket> Receive(int maxPackets)
        {
            lock (network.sync)
            {
                if (maxPackets is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxPackets));
                var result = new List<RoundInboundPacket>();
                while (!closed && packets.Count > 0 && result.Count < maxPackets) result.Add(packets.Dequeue());
                return result;
            }
        }
        public void Dispose() { lock (network.sync) { closed = true; packets.Clear(); } }
    }
}
