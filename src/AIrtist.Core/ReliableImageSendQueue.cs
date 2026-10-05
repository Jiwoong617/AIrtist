using System.Security.Cryptography;

namespace AIrtist.Core;

public enum ImageProgressState { Receiving, Complete, Rejected }
public sealed record ImageProgressRequest(ImageTransferDescriptor Transfer, string RecipientId, long ProbeId);
public sealed record ImageProgressReply(ImageTransferDescriptor Transfer, string RecipientId, long ProbeId,
    ImageProgressState State, byte[]? ReceivedChunks);
public enum ImageQueueStatus { Queued, Duplicate, Conflict, Backpressure }
public enum ImageSendEnd { Complete, TimedOut, RemoteRejected, Cancelled, Disconnected }
public sealed record ImageSendResult(string PeerId, ImageTransferDescriptor Transfer, ImageSendEnd End);
public sealed record OutgoingImagePacket(string PeerId, Guid TransferId, byte[] Bytes);
public sealed record ImageSendSnapshot(int ActiveTransfers, int BufferedBytes, int PendingResults, int KnownTransfers);
public sealed record ImageSendPolicy(int PacketsPerSecond, int BurstPackets, TimeSpan ProbeInterval)
{
    public static ImageSendPolicy Default => new(1024, 32, TimeSpan.FromMilliseconds(500));
}

// One queue/budget for ALL image recipients on this local round, not one queue per
// peer. No packet backlog: only bounded PNG copies and a missing-chunk bitmap.
// The adapter serializes calls and sends returned packets promptly. A failed SDK
// send is loss; a later receiver bitmap schedules those chunks again.
public sealed class ReliableImageSendQueue : IDisposable
{
    public const int MaxActiveTransfers = 16, MaxBufferedBytes = 16 * 1024 * 1024, MaxKnownTransfers = 256;
    private readonly Guid matchId, roundId;
    private readonly long version;
    private readonly string localId;
    private readonly TimeProvider clock;
    private readonly ImageSendPolicy policy;
    private readonly List<Transfer> active = new();
    private readonly Queue<ImageSendResult> results = new();
    private readonly Dictionary<(string, Guid), ImageTransferDescriptor> known = new();
    private int bufferedBytes, cursor;
    private long refilledAt;
    private double tokens;
    private bool disposed;
    private sealed class Transfer(string peer, ImageTransferDescriptor descriptor, byte[] png, TimeSpan timeout, long started)
    {
        public readonly string Peer = peer;
        public readonly ImageTransferDescriptor Descriptor = descriptor;
        public readonly byte[] Png = png;
        public readonly TimeSpan Timeout = timeout;
        public readonly long Started = started;
        public readonly bool[] Needed = Enumerable.Repeat(true, ImagePackets.Count(png.Length)).ToArray();
        public readonly bool[] Acknowledged = new bool[ImagePackets.Count(png.Length)];
        public int NextIndex, Remaining = ImagePackets.Count(png.Length);
        public long ProbeId, LastProbeAt;
        public bool AwaitingReply;
    }
    public ReliableImageSendQueue(Guid matchId, Guid roundId, long submissionVersion, string localId,
        ImageSendPolicy? policy = null, TimeProvider? clock = null)
    {
        if (matchId == Guid.Empty || roundId == Guid.Empty || submissionVersion < 1 || submissionVersion > long.MaxValue - 2)
            throw new ArgumentException("잘못된 그림 송신 컨텍스트입니다.");
        VotingRoundHost.ValidateId(localId); this.policy = policy ?? ImageSendPolicy.Default;
        if (this.policy.PacketsPerSecond is < 1 or > 8192 || this.policy.BurstPackets is < 1 or > 128
            || this.policy.ProbeInterval < TimeSpan.FromMilliseconds(100) || this.policy.ProbeInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentException("잘못된 그림 전송 정책입니다.");
        this.matchId = matchId; this.roundId = roundId; version = submissionVersion; this.localId = localId;
        this.clock = clock ?? TimeProvider.System; refilledAt = this.clock.GetTimestamp(); tokens = this.policy.BurstPackets;
    }

    public ImageQueueStatus TryQueue(string authenticatedPeerId, ImageTransferDescriptor descriptor, byte[] png, TimeSpan timeout)
        => TryQueue(authenticatedPeerId, descriptor, () => png, timeout);

    // The source callback is invoked only after queue/capacity checks, so gallery
    // backpressure does not repeatedly copy an image that cannot be retained.
    internal ImageQueueStatus TryQueue(string authenticatedPeerId, ImageTransferDescriptor descriptor, Func<byte[]> source, TimeSpan timeout)
    {
        Advance(); VotingRoundHost.ValidateId(authenticatedPeerId); ImagePackets.Validate(descriptor);
        if (descriptor.MatchId != matchId || descriptor.RoundId != roundId || descriptor.SubmissionVersion < version
            || descriptor.SubmissionVersion > version + 1 || timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("현재 라운드·전송 마감에 맞는 그림이 아닙니다.");
        var key = (authenticatedPeerId, descriptor.TransferId);
        if (known.TryGetValue(key, out var previous)) return previous == descriptor ? ImageQueueStatus.Duplicate : ImageQueueStatus.Conflict;
        if (active.Count + results.Count >= MaxActiveTransfers || known.Count >= MaxKnownTransfers
            || bufferedBytes + descriptor.TotalBytes > MaxBufferedBytes) return ImageQueueStatus.Backpressure;
        long queuedAt = clock.GetTimestamp();
        byte[] png = source();
        if (png is null || png.Length != descriptor.TotalBytes
            || !Convert.ToHexString(SHA256.HashData(png)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("송신 그림의 길이·해시가 다릅니다.");
        active.Add(new(authenticatedPeerId, descriptor, (byte[])png.Clone(), timeout, queuedAt));
        known.Add(key, descriptor); bufferedBytes += png.Length; return ImageQueueStatus.Queued;
    }

    public IReadOnlyList<OutgoingImagePacket> PollPackets(int maxPackets = 32)
    {
        Advance(); if (maxPackets is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        var packets = new List<OutgoingImagePacket>(); int idle = 0;
        while (packets.Count < maxPackets && tokens >= 1 && active.Count > 0 && idle < active.Count)
        {
            if (cursor >= active.Count) cursor = 0;
            var item = active[cursor++];
            if (item.Remaining == 0) { idle++; continue; }
            idle = 0;
            while (!item.Needed[item.NextIndex]) item.NextIndex++;
            int index = item.NextIndex++; item.Needed[index] = false; item.Remaining--;
            packets.Add(new(item.Peer, item.Descriptor.TransferId, ImagePackets.Create(item.Descriptor, item.Png, index))); tokens--;
        }
        return packets.AsReadOnly();
    }

    // Uses the reliable control lane. Backpressure leaves the same probe pending;
    // after successful enqueue that lane owns retries, so no parallel probe flood.
    public int PumpProgressRequests(Func<string, ImageProgressRequest, ControlQueueStatus> enqueue)
    {
        Advance(); int queued = 0;
        foreach (var item in active.ToArray())
        {
            if (queued == 4) break;
            if (item.Remaining != 0 || item.AwaitingReply || (item.ProbeId > 0 && clock.GetElapsedTime(item.LastProbeAt) < policy.ProbeInterval)) continue;
            var request = new ImageProgressRequest(item.Descriptor, item.Peer, item.ProbeId + 1);
            var status = enqueue(item.Peer, request);
            if (status == ControlQueueStatus.Closed) { Finish(item, ImageSendEnd.Disconnected); continue; }
            if (status != ControlQueueStatus.Queued) continue;
            item.ProbeId++; item.AwaitingReply = true; item.LastProbeAt = clock.GetTimestamp(); queued++;
        }
        return queued;
    }

    public RoundSyncStatus ReceiveProgress(string authenticatedSender, ImageProgressReply reply)
    {
        Advance();
        if (reply?.Transfer is null || reply.ProbeId < 1 || !Enum.IsDefined(reply.State)) return RoundSyncStatus.InvalidMessage;
        if (reply.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        if (reply.Transfer.MatchId != matchId || reply.Transfer.RoundId != roundId) return RoundSyncStatus.WrongRound;
        var item = active.FirstOrDefault(t => t.Peer == authenticatedSender && t.Descriptor.TransferId == reply.Transfer.TransferId);
        if (item is null) return RoundSyncStatus.Stale;
        if (item.Descriptor != reply.Transfer) return RoundSyncStatus.Conflict;
        if (reply.ProbeId != item.ProbeId || !item.AwaitingReply) return RoundSyncStatus.Stale;
        if (reply.State != ImageProgressState.Receiving)
        {
            if (reply.ReceivedChunks is not null) return RoundSyncStatus.InvalidMessage;
            Finish(item, reply.State == ImageProgressState.Complete ? ImageSendEnd.Complete : ImageSendEnd.RemoteRejected);
            return RoundSyncStatus.Accepted;
        }
        int count = item.Needed.Length; var bits = reply.ReceivedChunks;
        if (bits is null || bits.Length != (count + 7) / 8 || (count % 8 != 0 && (bits[^1] >> (count % 8)) != 0)) return RoundSyncStatus.InvalidMessage;
        int missing = 0;
        for (int i = 0; i < count; i++)
        {
            bool received = (bits[i / 8] & (1 << (i % 8))) != 0;
            if (item.Acknowledged[i] && !received) return RoundSyncStatus.Conflict;
            if (!received) missing++;
        }
        if (missing == 0) return RoundSyncStatus.InvalidMessage; // Complete requires receiver PNG validation.
        for (int i = 0; i < count; i++) { item.Acknowledged[i] = (bits[i / 8] & (1 << (i % 8))) != 0; item.Needed[i] = !item.Acknowledged[i]; }
        item.Remaining = missing; item.NextIndex = 0; item.AwaitingReply = false;
        return RoundSyncStatus.Accepted;
    }

    public ImageSendSnapshot Snapshot() { Advance(); return new(active.Count, bufferedBytes, results.Count, known.Count); }
    public IReadOnlyList<ImageSendResult> TakeResults() { Advance(); var copy = results.ToArray(); results.Clear(); return Array.AsReadOnly(copy); }
    public void Cancel(string peerId, Guid transferId) { Advance(); var item = active.FirstOrDefault(t => t.Peer == peerId && t.Descriptor.TransferId == transferId); if (item is not null) Finish(item, ImageSendEnd.Cancelled); }
    internal void ConfirmFromGallery(string peerId, ImageTransferDescriptor descriptor)
    { Advance(); var item = active.FirstOrDefault(t => t.Peer == peerId && t.Descriptor == descriptor); if (item is not null) Finish(item, ImageSendEnd.Complete); }
    public void DisconnectPeer(string peerId) { Advance(); foreach (var item in active.Where(t => t.Peer == peerId).ToArray()) Finish(item, ImageSendEnd.Disconnected); }
    private void Finish(Transfer item, ImageSendEnd end)
    {
        active.Remove(item); bufferedBytes -= item.Png.Length; results.Enqueue(new(item.Peer, item.Descriptor, end));
    }
    private void Advance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var item in active.Where(t => clock.GetElapsedTime(t.Started) >= t.Timeout).ToArray()) Finish(item, ImageSendEnd.TimedOut);
        tokens = Math.Min(policy.BurstPackets, tokens + clock.GetElapsedTime(refilledAt).TotalSeconds * policy.PacketsPerSecond);
        refilledAt = clock.GetTimestamp();
    }
    public void Dispose() { if (disposed) return; active.Clear(); known.Clear(); results.Clear(); bufferedBytes = 0; disposed = true; }
}
