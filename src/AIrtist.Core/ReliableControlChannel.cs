using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace AIrtist.Core;

public sealed record ControlChannelPolicy(TimeSpan DeliveryTimeout, TimeSpan RetryInterval, int PacketsPerSecond, int BurstPackets)
{
    public static ControlChannelPolicy Default => new(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), 64, 8);
}
public enum ControlQueueStatus { Queued, Backpressure, Closed }
public enum ControlReceiveStatus { Accepted, Duplicate, MessageReady, Acknowledged, WrongPeer, WrongRound, OutOfOrder, InvalidPacket, Conflict, Closed }
public enum ControlChannelFailure { None, SendTimedOut, ReceiveTimedOut, CorruptMessage, Disconnected }
public sealed record ControlChannelSnapshot(int QueuedMessages, int QueuedBytes, int ReceiveBytes,
    ulong LastAcknowledgedSequence, ulong LastReceivedSequence, ControlChannelFailure Failure);
public sealed class ReceivedControlMessage
{
    private readonly byte[] payload;
    public ulong Sequence { get; }
    internal ReceivedControlMessage(ulong sequence, byte[] payload) { Sequence = sequence; this.payload = payload; }
    public byte[] CopyPayload() => (byte[])payload.Clone();
}

// A single round-scoped ordered lane to ONE authenticated peer. The SDK adapter
// supplies the real peer identity, delivers packets and polls this channel; this
// class does not authenticate users, open sockets or implement EOS.
public sealed class ReliableControlChannel : IDisposable
{
    public const int MaxPacketBytes = 1024;
    public const int HeaderBytes = 88;
    public const int PayloadBytes = MaxPacketBytes - HeaderBytes;
    public const int MaxFragments = (RoundControlMessages.MaxBytes + PayloadBytes - 1) / PayloadBytes;
    public const int MaxQueuedMessages = 8;
    public const int MaxQueuedBytes = 128 * 1024;
    private const uint Magic = 0x314C5443; // CTL1
    private const byte Data = 1, Ack = 2;
    private readonly object sync = new();
    private readonly Guid matchId, roundId;
    private readonly string peerId;
    private readonly ControlChannelPolicy policy;
    private readonly TimeProvider clock;
    private readonly Queue<Outgoing> outgoing = new();
    private Incoming? incoming;
    private Stamp? lastReceived;
    private Stamp? pendingAck;
    private ulong nextSequence = 1, lastAcknowledged;
    private int queuedBytes;
    private double tokens;
    private long refilledAt;
    private bool disposed;
    private ControlChannelFailure failure;

    private sealed record Stamp(ulong Sequence, int TotalBytes, byte[] Hash);
    private sealed class Outgoing(Stamp stamp, byte[] payload, long enqueuedAt)
    {
        public readonly Stamp Stamp = stamp;
        public readonly byte[] Payload = payload;
        public readonly long EnqueuedAt = enqueuedAt;
        public int NextFragment;
        public long LastCycleAt;
        public bool Waiting;
        public bool HasCompletedCycle;
    }
    private sealed class Incoming(Stamp stamp, long started)
    {
        public readonly Stamp Stamp = stamp;
        public readonly long Started = started;
        public readonly byte[] Payload = new byte[stamp.TotalBytes];
        public readonly bool[] Received = new bool[FragmentCount(stamp.TotalBytes)];
        public int Count;
        public bool Complete, Taken;
    }

    public ReliableControlChannel(Guid matchId, Guid roundId, string authenticatedPeerId,
        ControlChannelPolicy? policy = null, TimeProvider? clock = null)
    {
        if (matchId == Guid.Empty || roundId == Guid.Empty) throw new ArgumentException("경기·라운드 ID가 필요합니다.");
        VotingRoundHost.ValidateId(authenticatedPeerId);
        this.policy = policy ?? ControlChannelPolicy.Default;
        if (this.policy.DeliveryTimeout < TimeSpan.FromSeconds(1) || this.policy.DeliveryTimeout > TimeSpan.FromSeconds(30)
            || this.policy.RetryInterval < TimeSpan.FromMilliseconds(100) || this.policy.RetryInterval >= this.policy.DeliveryTimeout
            || this.policy.PacketsPerSecond is < 1 or > 256 || this.policy.BurstPackets is < 1 or > 32)
            throw new ArgumentException("제어 메시지 전송 정책을 확인해 주세요.");
        this.matchId = matchId; this.roundId = roundId; peerId = authenticatedPeerId;
        this.clock = clock ?? TimeProvider.System; refilledAt = this.clock.GetTimestamp(); tokens = this.policy.BurstPackets;
    }

    public ControlQueueStatus TryQueue(ReadOnlySpan<byte> payload, out ulong sequence)
    {
        lock (sync)
        {
            Advance(); sequence = 0;
            if (failure != ControlChannelFailure.None) return ControlQueueStatus.Closed;
            if (payload.Length is 0 or > RoundControlMessages.MaxBytes) throw new ArgumentException("제어 메시지는 1~32768바이트여야 합니다.");
            if (outgoing.Count == MaxQueuedMessages || queuedBytes + payload.Length > MaxQueuedBytes) return ControlQueueStatus.Backpressure;
            if (nextSequence == ulong.MaxValue) throw new InvalidOperationException("새 라운드 연결이 필요합니다.");
            sequence = nextSequence++;
            byte[] copy = payload.ToArray();
            outgoing.Enqueue(new(new(sequence, copy.Length, SHA256.HashData(copy)), copy, clock.GetTimestamp()));
            queuedBytes += copy.Length;
            return ControlQueueStatus.Queued;
        }
    }

    // The adapter must hand these bounded packets to the SDK promptly. A failed
    // SDK send is treated as packet loss; the same sequence will retry, not requeue.
    public ReadOnlyCollection<byte[]> PollPackets(int maxPackets = 8)
    {
        lock (sync)
        {
            Advance();
            if (maxPackets is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxPackets));
            var result = new List<byte[]>();
            if (failure != ControlChannelFailure.None) return result.AsReadOnly();
            int available = Math.Min(maxPackets, (int)tokens);
            if (available > 0 && pendingAck is not null)
            {
                result.Add(Packet(Ack, pendingAck, 0, [])); pendingAck = null; available--; tokens--;
            }
            if (available == 0 || outgoing.Count == 0) return result.AsReadOnly();
            var item = outgoing.Peek();
            if (item.Waiting)
            {
                if (clock.GetElapsedTime(item.LastCycleAt) < policy.RetryInterval) return result.AsReadOnly();
                item.Waiting = false; item.NextFragment = 0;
            }
            int count = FragmentCount(item.Payload.Length);
            while (available > 0 && item.NextFragment < count)
            {
                int index = item.NextFragment++;
                int offset = index * PayloadBytes;
                result.Add(Packet(Data, item.Stamp, index, item.Payload.AsSpan(offset, Math.Min(PayloadBytes, item.Payload.Length - offset))));
                tokens--; available--;
            }
            if (item.NextFragment == count) { item.Waiting = true; item.HasCompletedCycle = true; item.LastCycleAt = clock.GetTimestamp(); }
            return result.AsReadOnly();
        }
    }

    public ControlReceiveStatus Receive(string authenticatedSender, ReadOnlySpan<byte> packet)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != peerId) return ControlReceiveStatus.WrongPeer;
            if (failure != ControlChannelFailure.None) return ControlReceiveStatus.Closed;
            if (!ReadHeader(packet, out byte kind, out var stamp, out int index)) return ControlReceiveStatus.InvalidPacket;
            if (new Guid(packet.Slice(8, 16)) != matchId || new Guid(packet.Slice(24, 16)) != roundId) return ControlReceiveStatus.WrongRound;
            if (kind == Ack)
            {
                if (outgoing.Count == 0 || outgoing.Peek().Stamp.Sequence != stamp.Sequence)
                    return stamp.Sequence <= lastAcknowledged ? ControlReceiveStatus.Duplicate : ControlReceiveStatus.OutOfOrder;
                if (!Same(outgoing.Peek().Stamp, stamp)) return ControlReceiveStatus.Conflict;
                // Ignore an ACK until at least one complete send cycle was offered.
                if (!outgoing.Peek().HasCompletedCycle) return ControlReceiveStatus.InvalidPacket;
                queuedBytes -= outgoing.Dequeue().Payload.Length; lastAcknowledged = stamp.Sequence;
                return ControlReceiveStatus.Acknowledged;
            }
            if (lastReceived is not null && stamp.Sequence == lastReceived.Sequence)
            {
                if (!Same(lastReceived, stamp)) return ControlReceiveStatus.Conflict;
                pendingAck = lastReceived; return ControlReceiveStatus.Duplicate;
            }
            ulong expected = (lastReceived?.Sequence ?? 0) + 1;
            if (stamp.Sequence != expected) return ControlReceiveStatus.OutOfOrder;
            incoming ??= new(stamp, clock.GetTimestamp());
            if (!Same(incoming.Stamp, stamp)) return ControlReceiveStatus.Conflict;
            int offset = index * PayloadBytes;
            var payload = packet[HeaderBytes..]; var target = incoming.Payload.AsSpan(offset, payload.Length);
            if (incoming.Received[index])
            {
                if (!target.SequenceEqual(payload)) { Fail(ControlChannelFailure.CorruptMessage); return ControlReceiveStatus.Conflict; }
                return ControlReceiveStatus.Duplicate;
            }
            payload.CopyTo(target); incoming.Received[index] = true; incoming.Count++;
            if (incoming.Count != incoming.Received.Length) return ControlReceiveStatus.Accepted;
            if (!SHA256.HashData(incoming.Payload).AsSpan().SequenceEqual(stamp.Hash))
            { Fail(ControlChannelFailure.CorruptMessage); return ControlReceiveStatus.Conflict; }
            incoming.Complete = true; return ControlReceiveStatus.MessageReady;
        }
    }

    // Exactly one dequeue per sequence. Confirm only after the application handler
    // has applied OR intentionally rejected the message. Transport ACK means handled,
    // not that a game action was authorized (the game response conveys that).
    public ReceivedControlMessage? TakeReceived()
    {
        lock (sync)
        {
            Advance();
            if (failure != ControlChannelFailure.None || incoming is not { Complete: true, Taken: false }) return null;
            incoming.Taken = true;
            return new(incoming.Stamp.Sequence, incoming.Payload);
        }
    }

    public bool ConfirmReceived(ulong sequence)
    {
        lock (sync)
        {
            Advance();
            if (failure != ControlChannelFailure.None) return false;
            if (lastReceived?.Sequence == sequence) { pendingAck = lastReceived; return true; }
            if (incoming is not { Complete: true, Taken: true } || incoming.Stamp.Sequence != sequence) return false;
            lastReceived = incoming.Stamp; pendingAck = lastReceived; incoming = null; return true;
        }
    }

    public ControlChannelSnapshot Snapshot()
    {
        lock (sync)
        {
            Advance(); return new(outgoing.Count, queuedBytes, incoming?.Payload.Length ?? 0,
                lastAcknowledged, lastReceived?.Sequence ?? 0, failure);
        }
    }
    public void Disconnect() { lock (sync) { Advance(); Fail(ControlChannelFailure.Disconnected); } }
    private void Advance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure != ControlChannelFailure.None) return;
        // Includes queue and application-handler time; no unbounded pending work.
        if (outgoing.Count > 0 && clock.GetElapsedTime(outgoing.Peek().EnqueuedAt) >= policy.DeliveryTimeout) { Fail(ControlChannelFailure.SendTimedOut); return; }
        if (incoming is not null && clock.GetElapsedTime(incoming.Started) >= policy.DeliveryTimeout) { Fail(ControlChannelFailure.ReceiveTimedOut); return; }
        tokens = Math.Min(policy.BurstPackets, tokens + clock.GetElapsedTime(refilledAt).TotalSeconds * policy.PacketsPerSecond);
        refilledAt = clock.GetTimestamp();
    }
    private void Fail(ControlChannelFailure reason)
    { if (failure == ControlChannelFailure.None) failure = reason; outgoing.Clear(); queuedBytes = 0; incoming = null; pendingAck = null; }
    public void Dispose() { lock (sync) { if (disposed) return; Fail(ControlChannelFailure.Disconnected); disposed = true; } }

    private static int FragmentCount(int length) => (length + PayloadBytes - 1) / PayloadBytes;
    private static bool Same(Stamp a, Stamp b) => a.Sequence == b.Sequence && a.TotalBytes == b.TotalBytes && a.Hash.AsSpan().SequenceEqual(b.Hash);
    private byte[] Packet(byte kind, Stamp stamp, int index, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), 1); packet[6] = kind;
        matchId.TryWriteBytes(packet.AsSpan(8, 16)); roundId.TryWriteBytes(packet.AsSpan(24, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(40, 8), stamp.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(48, 4), stamp.TotalBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(52, 2), (ushort)index);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(54, 2), (ushort)FragmentCount(stamp.TotalBytes));
        stamp.Hash.CopyTo(packet, 56); payload.CopyTo(packet.AsSpan(HeaderBytes)); return packet;
    }
    private static bool ReadHeader(ReadOnlySpan<byte> packet, out byte kind, out Stamp stamp, out int index)
    {
        kind = 0; stamp = null!; index = 0;
        if (packet.Length is < HeaderBytes or > MaxPacketBytes || BinaryPrimitives.ReadUInt32LittleEndian(packet[..4]) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4, 2)) != 1 || packet[7] != 0) return false;
        kind = packet[6];
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(40, 8));
        int total = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(48, 4));
        index = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(52, 2));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(54, 2));
        if (sequence == 0 || total is < 1 or > RoundControlMessages.MaxBytes || count != FragmentCount(total)) return false;
        if (kind == Ack) { if (index != 0 || packet.Length != HeaderBytes) return false; }
        else if (kind == Data)
        {
            if (index >= count || packet.Length != HeaderBytes + Math.Min(PayloadBytes, total - index * PayloadBytes)) return false;
        }
        else return false;
        stamp = new(sequence, total, packet.Slice(56, 32).ToArray()); return true;
    }
}
