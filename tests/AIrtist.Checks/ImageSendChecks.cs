using System.Buffers.Binary;
using System.Security.Cryptography;
using AIrtist.Core;

internal static class ImageSendChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid image sender operation accepted");
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly ReliableImageSendQueue Sender;
        public World() => Sender = new(Match, Round, 5, "local", clock: Clock);
        public ImageTransferDescriptor Describe(byte[] png) => new(Match, Round, 5, Guid.NewGuid(), Guid.NewGuid(), "local", png.Length, Convert.ToHexString(SHA256.HashData(png)));
        public void Dispose() => Sender.Dispose();
    }
    private static ImageProgressRequest Probe(World world)
    {
        ImageProgressRequest? request = null;
        Require(world.Sender.PumpProgressRequests((_, r) => { request = r; return ControlQueueStatus.Queued; }) == 1, "Progress probe not queued");
        return request!;
    }
    private static ImageProgressReply Reply(ImageProgressRequest request, ImageProgressState state, byte[]? bits = null)
        => new(request.Transfer, "local", request.ProbeId, state, bits);
    private static byte[] Bitmap(int count, params int[] missing)
    {
        var bits = new byte[(count + 7) / 8];
        for (int i = 0; i < count; i++) if (!missing.Contains(i)) bits[i / 8] |= (byte)(1 << (i % 8));
        return bits;
    }
    private static List<OutgoingImagePacket> Drain(World world)
    {
        var packets = new List<OutgoingImagePacket>();
        for (int i = 0; i < 200; i++)
        {
            world.Clock.Advance(TimeSpan.FromMilliseconds(50)); var batch = world.Sender.PollPackets();
            if (batch.Count == 0) break; packets.AddRange(batch);
        }
        return packets;
    }
    public static void Run()
    {
        RateAndSelectiveRetry(); CapacityAndTimeout(); ReceiverValidation();
        Console.WriteLine("Image send: shared rate/fairness, selective missing chunks, bitmap/probe identity, 16 MiB/16 queue cap, bounded results/history, timeout/cancel and validated-completion checks passed");
    }
    private static void RateAndSelectiveRetry()
    {
        using (var world = new World())
        {
            byte[] png = new byte[100000]; var a = world.Describe(png); var b = world.Describe(png);
            world.Sender.TryQueue("peer1", a, png, TimeSpan.FromSeconds(10)); world.Sender.TryQueue("peer2", b, png, TimeSpan.FromSeconds(10));
            var first = world.Sender.PollPackets(128);
            Require(first.Count == 32 && first.Count(p => p.PeerId == "peer1") == 16 && first.Count(p => p.PeerId == "peer2") == 16, "Budget duplicated per recipient/unfair sender");
            Require(world.Sender.PollPackets().Count == 0, "Burst bypass"); world.Clock.Advance(TimeSpan.FromMilliseconds(10));
            Require(world.Sender.PollPackets(128).Count == 10, "Rate refill bypass");
            world.Sender.DisconnectPeer("peer1");
            Require(world.Sender.Snapshot() is { ActiveTransfers: 1, BufferedBytes: 100000, PendingResults: 1 }, "Disconnect affected another peer/leaked buffer");
        }
        using var w = new World(); byte[] source = new byte[100000]; var descriptor = w.Describe(source);
        Require(w.Sender.TryQueue("peer", descriptor, source, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Queued, "Queue failed");
        source[0] = 99; // Caller cannot mutate the owned sender payload.
        var sent = Drain(w); int count = (source.Length + ImagePackets.PayloadBytes - 1) / ImagePackets.PayloadBytes;
        Require(sent.Count == count && sent.All(p => p.Bytes.Length <= 1024) && sent[0].Bytes[32] == 0, "Initial packet count/ownership incorrect");
        Require(w.Sender.PumpProgressRequests((_, _) => ControlQueueStatus.Backpressure) == 0, "Probe ignored control backpressure");
        var probe = Probe(w);
        Require(w.Sender.PumpProgressRequests((_, _) => throw new InvalidOperationException("Flooded pending probe")) == 0, "Unbounded probes");
        var reply = Reply(probe, ImageProgressState.Receiving, Bitmap(count, 1, 7));
        Require(w.Sender.ReceiveProgress("stranger", reply) == RoundSyncStatus.Stale, "Forged receiver");
        Require(w.Sender.ReceiveProgress("peer", reply with { RecipientId = "other" }) == RoundSyncStatus.WrongRecipient, "Wrong reply recipient");
        Require(w.Sender.ReceiveProgress("peer", reply with { Transfer = descriptor with { RoundId = Guid.NewGuid() } }) == RoundSyncStatus.WrongRound, "Old round reply");
        Require(w.Sender.ReceiveProgress("peer", reply with { Transfer = descriptor with { ApprovalId = Guid.NewGuid() } }) == RoundSyncStatus.Conflict, "Rebound approval");
        Require(w.Sender.ReceiveProgress("peer", reply with { ProbeId = 999 }) == RoundSyncStatus.Stale, "Wrong probe accepted");
        foreach (var bad in new[] { reply with { ReceivedChunks = [] }, reply with { ReceivedChunks = new byte[3000] },
            reply with { ReceivedChunks = Bitmap(count) }, reply with { State = (ImageProgressState)99 },
            Reply(probe, ImageProgressState.Complete, [1]) })
            Require(w.Sender.ReceiveProgress("peer", bad) == RoundSyncStatus.InvalidMessage, "Invalid progress completed transfer");
        var padding = Bitmap(count, 1); padding[^1] |= 128;
        Require(w.Sender.ReceiveProgress("peer", reply with { ReceivedChunks = padding }) == RoundSyncStatus.InvalidMessage, "Bitmap padding accepted");
        Require(w.Sender.ReceiveProgress("peer", reply) == RoundSyncStatus.Accepted, "Missing bitmap rejected");
        Require(w.Sender.ReceiveProgress("peer", reply) == RoundSyncStatus.Stale, "Duplicate bitmap replayed work");
        var retried = Drain(w); Require(retried.Select(p => BinaryPrimitives.ReadInt32LittleEndian(p.Bytes.AsSpan(24, 4))).SequenceEqual([1, 7]), "Resent already received chunks");
        w.Clock.Advance(TimeSpan.FromMilliseconds(500)); var second = Probe(w);
        Require(w.Sender.ReceiveProgress("peer", Reply(second, ImageProgressState.Receiving, Bitmap(count, 0, 7))) == RoundSyncStatus.Conflict, "Regressing bitmap accepted");
        Require(w.Sender.ReceiveProgress("peer", Reply(second, ImageProgressState.Receiving, Bitmap(count, 7))) == RoundSyncStatus.Accepted, "Second missing report");
        Require(Drain(w).Count == 1, "Selective retry was not narrowed");
        w.Clock.Advance(TimeSpan.FromMilliseconds(500)); var final = Probe(w);
        Require(w.Sender.ReceiveProgress("peer", Reply(final, ImageProgressState.Complete)) == RoundSyncStatus.Accepted
            && w.Sender.Snapshot() is { ActiveTransfers: 0, BufferedBytes: 0, PendingResults: 1 }, "Completion leaked payload");
        Require(w.Sender.TakeResults().Single().End == ImageSendEnd.Complete && w.Sender.TakeResults().Count == 0, "Completion delivered twice");
        Require(w.Sender.ReceiveProgress("peer", Reply(final, ImageProgressState.Complete)) == RoundSyncStatus.Stale, "Late completion reopened transfer");
    }
    private static void CapacityAndTimeout()
    {
        using (var bytes = new World())
        {
            var png = new byte[VotingArtwork.MaxBytes];
            for (int i = 0; i < 4; i++) Require(bytes.Sender.TryQueue("peer", bytes.Describe(png), png, TimeSpan.FromSeconds(30)) == ImageQueueStatus.Queued, "Byte cap filled early");
            Require(bytes.Sender.TryQueue("peer", bytes.Describe(png), png, TimeSpan.FromSeconds(30)) == ImageQueueStatus.Backpressure
                && bytes.Sender.Snapshot().BufferedBytes == 16 * 1024 * 1024, "Unbounded PNG reservations");
        }
        using var w = new World(); var small = new byte[33]; var first = w.Describe(small);
        for (int i = 0; i < 16; i++) Require(w.Sender.TryQueue("peer", i == 0 ? first : w.Describe(small), small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Queued, "Count cap filled early");
        Require(w.Sender.TryQueue("peer", first, small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Duplicate, "Same transfer queued twice");
        Require(w.Sender.TryQueue("peer", first with { ApprovalId = Guid.NewGuid() }, small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Conflict, "Replaced transfer");
        Require(w.Sender.TryQueue("peer", w.Describe(small), small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Backpressure, "Count cap bypassed");
        w.Sender.Cancel("peer", first.TransferId);
        Require(w.Sender.TryQueue("peer", w.Describe(small), small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Backpressure, "Pending results unbounded");
        w.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(w.Sender.Snapshot() is { BufferedBytes: 0, ActiveTransfers: 0, PendingResults: 16 }, "Exact send timeout leaked buffer");
        var results = w.Sender.TakeResults(); Require(results.Count(r => r.End == ImageSendEnd.TimedOut) == 15 && results.Count(r => r.End == ImageSendEnd.Cancelled) == 1, "Wrong cancellation results");
        for (int i = 16; i < 256; i++)
        {
            var d = w.Describe(small); Require(w.Sender.TryQueue("peer", d, small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Queued, "History cap early");
            w.Sender.Cancel("peer", d.TransferId); w.Sender.TakeResults();
        }
        Require(w.Sender.TryQueue("peer", w.Describe(small), small, TimeSpan.FromSeconds(10)) == ImageQueueStatus.Backpressure, "Transfer history unbounded");
        Reject(() => w.Sender.TryQueue("peer", first with { RoundId = Guid.NewGuid() }, small, TimeSpan.FromSeconds(10)));
        using var late = new World(); var lateDesc = late.Describe(new byte[2000]); late.Sender.TryQueue("peer", lateDesc, new byte[2000], TimeSpan.FromSeconds(10));
        late.Sender.PollPackets(); var query = Probe(late); late.Clock.Advance(TimeSpan.FromMilliseconds(9999));
        Require(late.Sender.ReceiveProgress("peer", Reply(query, ImageProgressState.Receiving, Bitmap(3, 1))) == RoundSyncStatus.Accepted, "Late progress fixture");
        late.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(late.Sender.TakeResults().Single().End == ImageSendEnd.TimedOut, "Progress extended absolute deadline");
        late.Sender.Dispose(); Reject(() => late.Sender.Snapshot());
    }
    private static void ReceiverValidation()
    {
        Guid match = Guid.NewGuid(), round = Guid.NewGuid();
        var compatibility = new MultiplayerCompatibility(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
        var players = new[] { new VotingPlayer("host", "방장"), new VotingPlayer("peer", "참가자") };
        using var host = new MultiplayerRoundHost(match, round, 5, "host", new("apple", "사과", "과일", "apple", ["사과", "apple"]), compatibility, players.Select(p => new MultiplayerParticipant(p, compatibility)));
        var approval = host.Submit("peer", new(match, round, 5, "submit", "peer", "빨간 과일", "a red fruit")).Approval!;
        byte[] corruptPng = new byte[2000]; var descriptor = ImagePackets.Describe(approval, Guid.NewGuid(), corruptPng);
        var probe = new ImageProgressRequest(descriptor, "host", 1);
        Require(host.ImageProgress("peer", probe)!.State == ImageProgressState.Rejected && host.BufferedImageBytes == 0, "Probe allocated unapproved image");
        host.BeginImage("peer", descriptor);
        Require(host.ImageProgress("host", probe) is null && host.ImageProgress("peer", probe with { RecipientId = "peer" }) is null
            && host.ImageProgress("peer", probe with { Transfer = descriptor with { RoundId = Guid.NewGuid() } }) is null, "Wrong progress authority/context accepted");
        Require(host.ImageProgress("peer", probe) is { State: ImageProgressState.Receiving, ReceivedChunks: not null }, "Missing image reported complete");
        foreach (var packet in ImagePackets.Split(descriptor, corruptPng)) host.ReceiveImagePacket("peer", packet);
        Require(host.ImageProgress("peer", probe) is { State: ImageProgressState.Rejected, ReceivedChunks: null } && host.BufferedImageBytes == 0,
            "Hash-valid invalid PNG reported complete");
    }
}
