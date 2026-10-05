using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AIrtist.Core;

internal static class ControlTransportChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static byte[] Bytes(int size) { var data = new byte[size]; new Random(71).NextBytes(data); return data; }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid control operation accepted");
    }
    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan time) => ticks += time.Ticks;
    }
    private sealed class Pair : IDisposable
    {
        public readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
        public readonly Clock Clock = new();
        public readonly ReliableControlChannel A, B;
        public Pair() { A = new(Match, Round, "b", clock: Clock); B = new(Match, Round, "a", clock: Clock); }
        public void Dispose() { A.Dispose(); B.Dispose(); }
    }

    public static void Run()
    {
        Recovery(); Validation(); LimitsAndTimeouts(); DelayedAck();
        Console.WriteLine("Control transport: 32 KiB fragmentation, ordered single dispatch, packet/ACK loss recovery, bounded queue/rate/memory, spoof/stale/corrupt rejection, deadline/disconnect cleanup passed");
    }

    private static void Recovery()
    {
        using var pair = new Pair();
        var original = Bytes(RoundControlMessages.MaxBytes);
        Require(pair.A.TryQueue(original, out ulong sequence) == ControlQueueStatus.Queued, "Queue failed");
        original[0] ^= 1; // Queue must own a copy.
        var next = Bytes(55); pair.A.TryQueue(next, out ulong second);
        var delivered = new List<byte[]>(); bool droppedData = false, droppedAck = false;
        for (int tick = 0; tick < 90 && pair.A.Snapshot().QueuedMessages != 0; tick++)
        {
            foreach (var packet in pair.A.PollPackets(32).Reverse())
            {
                Require(packet.Length <= 1024, "Control packet exceeds cap");
                if (!droppedData) { droppedData = true; continue; }
                pair.B.Receive("a", packet); pair.B.Receive("a", packet);
            }
            if (pair.B.TakeReceived() is { } message)
            {
                Require(pair.B.TakeReceived() is null, "Same message dequeued twice");
                delivered.Add(message.CopyPayload());
                Require(pair.B.ConfirmReceived(message.Sequence), "Application confirmation failed");
            }
            foreach (var ack in pair.B.PollPackets(32))
            {
                if (!droppedAck) { droppedAck = true; continue; }
                Require(pair.A.Receive("b", ack) is ControlReceiveStatus.Acknowledged or ControlReceiveStatus.Duplicate, "ACK rejected");
                pair.A.Receive("b", ack);
            }
            pair.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        Require(droppedData && droppedAck && delivered.Count == 2, "Loss recovery delivered wrong count");
        Require(delivered[0].SequenceEqual(Bytes(RoundControlMessages.MaxBytes)) && delivered[1].SequenceEqual(next), "Ordered bytes differ");
        Require(sequence == 1 && second == 2 && pair.A.Snapshot() is { QueuedBytes: 0, LastAcknowledgedSequence: 2, Failure: ControlChannelFailure.None }
            && pair.B.Snapshot() is { ReceiveBytes: 0, LastReceivedSequence: 2 }, "Final buffers/receipts incorrect");
    }

    private static void Validation()
    {
        using var pair = new Pair(); pair.A.TryQueue(Bytes(1500), out _);
        var packets = pair.A.PollPackets(); var first = packets[0];
        Require(pair.B.Receive("stranger", first) == ControlReceiveStatus.WrongPeer && pair.B.Snapshot().ReceiveBytes == 0, "Wrong peer allocated memory");
        var wrongRound = (byte[])first.Clone(); Guid.NewGuid().TryWriteBytes(wrongRound.AsSpan(24, 16));
        Require(pair.B.Receive("a", wrongRound) == ControlReceiveStatus.WrongRound, "Old round accepted");
        var future = (byte[])first.Clone(); BinaryPrimitives.WriteUInt64LittleEndian(future.AsSpan(40, 8), 100);
        Require(pair.B.Receive("a", future) == ControlReceiveStatus.OutOfOrder, "Future sequence allocated memory");
        var huge = (byte[])first.Clone(); BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(48, 4), int.MaxValue);
        var wrongCount = (byte[])first.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(wrongCount.AsSpan(54, 2), ushort.MaxValue);
        var badIndex = (byte[])first.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(badIndex.AsSpan(52, 2), ushort.MaxValue);
        var reserved = (byte[])first.Clone(); reserved[7] = 1;
        var badVersion = (byte[])first.Clone(); badVersion[4] = 99;
        foreach (var bad in new[] { new byte[1025], first[..^1], huge, wrongCount, badIndex, reserved, badVersion })
            Require(pair.B.Receive("a", bad) == ControlReceiveStatus.InvalidPacket && pair.B.Snapshot().ReceiveBytes == 0, "Invalid frame accepted");
        Require(pair.B.Receive("a", first) == ControlReceiveStatus.Accepted, "First fragment failed");
        var changed = (byte[])first.Clone(); changed[^1] ^= 1;
        Require(pair.B.Receive("a", changed) == ControlReceiveStatus.Conflict && pair.B.Snapshot() is { ReceiveBytes: 0, Failure: ControlChannelFailure.CorruptMessage }, "Conflicting duplicate kept buffer");

        using var corrupt = new Pair(); corrupt.A.TryQueue(Bytes(1500), out _);
        var broken = corrupt.A.PollPackets().ToArray(); broken[^1][^1] ^= 1;
        foreach (var p in broken) corrupt.B.Receive("a", p);
        Require(corrupt.B.TakeReceived() is null && corrupt.B.Snapshot().Failure == ControlChannelFailure.CorruptMessage, "Bad SHA delivered");

        using var ackPair = new Pair(); ackPair.A.TryQueue(Bytes(33), out _);
        var data = ackPair.A.PollPackets().Single(); ackPair.B.Receive("a", data);
        Require(!ackPair.B.ConfirmReceived(1) && ackPair.B.PollPackets().Count == 0, "ACK before application dequeued");
        var item = ackPair.B.TakeReceived()!; ackPair.B.ConfirmReceived(item.Sequence);
        var ack = ackPair.B.PollPackets().Single();
        Require(ackPair.A.Receive("stranger", ack) == ControlReceiveStatus.WrongPeer, "Spoofed ACK removed queued message");
        var fake = (byte[])ack.Clone(); fake[56] ^= 1;
        Require(ackPair.A.Receive("b", fake) == ControlReceiveStatus.Conflict && ackPair.A.Snapshot().QueuedMessages == 1, "Wrong hash ACK accepted");
        Require(ackPair.A.Receive("b", ack) == ControlReceiveStatus.Acknowledged, "Genuine ACK failed");
        Require(ackPair.B.Receive("a", data) == ControlReceiveStatus.Duplicate && ackPair.B.TakeReceived() is null, "Replay redelivered application action");
    }

    private static void LimitsAndTimeouts()
    {
        using var pair = new Pair();
        for (int i = 0; i < 4; i++) Require(pair.A.TryQueue(Bytes(32768), out _) == ControlQueueStatus.Queued, "Within-budget queue failed");
        Require(pair.A.TryQueue(Bytes(1), out _) == ControlQueueStatus.Backpressure && pair.A.Snapshot().QueuedBytes == 128 * 1024, "Queue bytes unbounded");
        Require(pair.A.PollPackets(32).Count == 8 && pair.A.PollPackets(32).Count == 0, "Burst/rate limit ignored");
        pair.Clock.Advance(TimeSpan.FromMilliseconds(100)); Require(pair.A.PollPackets(32).Count == 6, "Rate refill wrong");
        pair.Clock.Advance(TimeSpan.FromMilliseconds(9900));
        Require(pair.A.Snapshot() is { Failure: ControlChannelFailure.SendTimedOut, QueuedBytes: 0 }
            && pair.A.TryQueue(Bytes(1), out _) == ControlQueueStatus.Closed, "Send timeout retained queue or reopened");
        using var count = new Pair();
        for (int i = 0; i < 8; i++) count.A.TryQueue(Bytes(1), out _);
        Require(count.A.TryQueue(Bytes(1), out _) == ControlQueueStatus.Backpressure, "Message count cap ignored");
        using var receive = new Pair(); receive.A.TryQueue(Bytes(1500), out _);
        var first = receive.A.PollPackets()[0]; receive.B.Receive("a", first);
        receive.Clock.Advance(TimeSpan.FromMilliseconds(9999)); receive.B.Receive("a", first);
        receive.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(receive.B.Snapshot() is { ReceiveBytes: 0, Failure: ControlChannelFailure.ReceiveTimedOut }, "Retry reset receive deadline");
        using var handler = new Pair(); handler.A.TryQueue(Bytes(22), out _);
        handler.B.Receive("a", handler.A.PollPackets()[0]); var message = handler.B.TakeReceived()!;
        Require(handler.B.PollPackets().Count == 0, "Transport ACK before application handled");
        handler.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(!handler.B.ConfirmReceived(message.Sequence) && handler.B.Snapshot().ReceiveBytes == 0, "Stalled handler held buffer forever");
        count.A.Disconnect(); Require(count.A.Snapshot() is { QueuedBytes: 0, Failure: ControlChannelFailure.Disconnected }, "Disconnect leak");
        count.A.Dispose(); Reject(() => count.A.Snapshot());
        Reject(() => count.B.TryQueue(new byte[32769], out _)); Reject(() => count.B.TryQueue([], out _));
    }

    private static void DelayedAck()
    {
        using var pair = new Pair(); pair.A.TryQueue(Bytes(3000), out _);
        foreach (var packet in pair.A.PollPackets()) pair.B.Receive("a", packet);
        var message = pair.B.TakeReceived()!; pair.B.ConfirmReceived(message.Sequence);
        var delayed = pair.B.PollPackets().Single(); pair.Clock.Advance(TimeSpan.FromMilliseconds(500));
        pair.A.PollPackets(1); // A retry cycle has started but has not finished.
        Require(pair.A.Receive("b", delayed) == ControlReceiveStatus.Acknowledged, "Delayed ACK was invalidated by partial retry");
    }

    // Genuine OS UDP sockets restricted to loopback. Endpoint checks in this test
    // are NOT EOS authentication and are not shipped as an internet transport.
    public static async Task Loopback(string root)
    {
        Guid match = Guid.NewGuid(), round = Guid.NewGuid();
        using var sender = new ReliableControlChannel(match, round, "receiver");
        using var receiver = new ReliableControlChannel(match, round, "sender");
        using var socketA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var socketB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var addressA = (IPEndPoint)socketA.Client.LocalEndPoint!; var addressB = (IPEndPoint)socketB.Client.LocalEndPoint!;
        byte[] payload = Bytes(32768); sender.TryQueue(payload, out _);
        bool dropData = true, dropAck = true; int receivedMessages = 0, sentPackets = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(12) && sender.Snapshot().QueuedMessages != 0)
        {
            foreach (var packet in sender.PollPackets())
            {
                if (dropData) { dropData = false; continue; }
                await socketA.SendAsync(packet, addressB); sentPackets++;
            }
            while (socketB.Available > 0)
            {
                var packet = await socketB.ReceiveAsync(); Require(packet.RemoteEndPoint.Equals(addressA), "Unexpected loopback endpoint");
                receiver.Receive("sender", packet.Buffer);
            }
            if (receiver.TakeReceived() is { } message)
            {
                Require(message.CopyPayload().SequenceEqual(payload), "Loopback payload differs"); receivedMessages++;
                receiver.ConfirmReceived(message.Sequence);
            }
            foreach (var packet in receiver.PollPackets())
            {
                if (dropAck) { dropAck = false; continue; }
                await socketB.SendAsync(packet, addressA); sentPackets++;
            }
            while (socketA.Available > 0)
            {
                var packet = await socketA.ReceiveAsync(); Require(packet.RemoteEndPoint.Equals(addressB), "Unexpected loopback ACK endpoint");
                sender.Receive("receiver", packet.Buffer);
            }
            await Task.Delay(10);
        }
        Require(!dropData && !dropAck && receivedMessages == 1 && sender.Snapshot() is { LastAcknowledgedSequence: 1, Failure: ControlChannelFailure.None }, "Loopback recovery failed");
        string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "control-loopback-check.json"), JsonSerializer.Serialize(new
        { transport = "two UDP sockets on 127.0.0.1; no EOS, internet, or other PC", payloadBytes = payload.Length,
            receivedMessages, sentPackets, droppedDataPackets = 1, droppedAckPackets = 1, seconds = watch.Elapsed.TotalSeconds }, RuntimeAssets.Json));
        Console.WriteLine($"UDP loopback control: {payload.Length} bytes, {sentPackets} sent packets, loss/ACK recovery, exactly one delivery passed");
    }
}
