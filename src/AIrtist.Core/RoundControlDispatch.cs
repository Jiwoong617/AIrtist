namespace AIrtist.Core;

public sealed record RoundStartMessage(Guid MatchId, Guid RoundId, long SubmissionVersion, string HostId,
    string RecipientId, MultiplayerCompatibility Compatibility, IReadOnlyList<VotingPlayer> Players,
    RoundPrompt Prompt, MultiplayerRoundPolicy Policy, DateTimeOffset WritingDeadline, TimeSpan WritingRemaining);
public sealed record SubmissionReplyMessage(Guid MatchId, Guid RoundId, long SubmissionVersion, string RecipientId,
    string RequestId, SubmissionReceipt Receipt, TimeSpan GenerationRemaining);
public sealed record ImageBeginReplyMessage(Guid MatchId, Guid RoundId, long SubmissionVersion, string RecipientId,
    Guid ApprovalId, Guid TransferId, ImageTransferStatus Status);
public sealed record GenerationFailedMessage(Guid MatchId, Guid RoundId, long SubmissionVersion, string PlayerId, Guid ApprovalId);
public sealed record GenerationFailureReply(Guid MatchId, Guid RoundId, long SubmissionVersion, string RecipientId,
    Guid ApprovalId, ImageTransferStatus Status);
public sealed record RoundProgressMessage(Guid MatchId, Guid RoundId, long SubmissionVersion, string RecipientId,
    long Revision, MultiplayerRoundPhase Phase, IReadOnlyList<PlayerRoundProgress> Players, TimeSpan WritingRemaining);
// Status describes dispatch/validation; business acceptance is carried in Reply.
public sealed record ControlDispatchResult(RoundSyncStatus Status, byte[]? Reply = null);

public sealed class HostControlDispatcher : IDisposable
{
    private readonly MultiplayerRoundHost host;
    private readonly RoundStartMessage context;
    public ReliableImageSendQueue Images { get; }
    public HostControlDispatcher(MultiplayerRoundHost host, string hostId, TimeProvider? clock = null)
    {
        this.host = host; context = host.StartMessage(hostId);
        if (context.HostId != hostId) throw new ArgumentException("방장 컨텍스트가 필요합니다.");
        Images = new(context.MatchId, context.RoundId, context.SubmissionVersion, hostId, clock: clock);
    }

    public ImageQueueStatus QueueGalleryImage(string recipientId, Guid transferId)
    {
        var gallery = host.Snapshot(recipientId).Gallery ?? throw new InvalidOperationException("갤러리가 없습니다.");
        if (gallery.TimedOut || gallery.Remaining <= TimeSpan.Zero) throw new InvalidOperationException("갤러리 전송이 마감되었습니다.");
        var descriptor = gallery.Manifest.Artworks.Single(a => a.Transfer.TransferId == transferId).Transfer;
        return Images.TryQueue(recipientId, descriptor, () => host.CopyGalleryImage(recipientId, transferId), gallery.Remaining);
    }

    public ControlDispatchResult Dispatch(string authenticatedSender, ReadOnlySpan<byte> bytes)
    {
        if (!context.Players.Any(p => p.Id == authenticatedSender)) return new(RoundSyncStatus.WrongSender);
        object message;
        try { message = RoundControlMessages.Decode(bytes); }
        catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
        bool Round(Guid match, Guid round, long version) => match == context.MatchId && round == context.RoundId && version == context.SubmissionVersion;
        switch (message)
        {
            case SubmissionRequest request:
                if (!Round(request.MatchId, request.RoundId, request.SubmissionVersion)) return new(RoundSyncStatus.WrongRound);
                if (request.PlayerId != authenticatedSender) return new(RoundSyncStatus.WrongSender);
                if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80) return new(RoundSyncStatus.InvalidMessage);
                return new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(host.SubmitControl(authenticatedSender, request)));
            case ImageTransferDescriptor image:
                if (!Round(image.MatchId, image.RoundId, image.SubmissionVersion)) return new(RoundSyncStatus.WrongRound);
                if (image.PlayerId != authenticatedSender) return new(RoundSyncStatus.WrongSender);
                var status = host.BeginImage(authenticatedSender, image);
                return new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(new ImageBeginReplyMessage(
                    context.MatchId, context.RoundId, context.SubmissionVersion, authenticatedSender, image.ApprovalId, image.TransferId, status)));
            case ImageProgressRequest probe:
                var progress = host.ImageProgress(authenticatedSender, probe);
                return progress is null ? new(RoundSyncStatus.InvalidMessage) : new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(progress));
            case ImageProgressReply progressReply:
                return new(Images.ReceiveProgress(authenticatedSender, progressReply));
            case GenerationFailedMessage failed:
                if (!Round(failed.MatchId, failed.RoundId, failed.SubmissionVersion)) return new(RoundSyncStatus.WrongRound);
                if (failed.PlayerId != authenticatedSender) return new(RoundSyncStatus.WrongSender);
                if (failed.ApprovalId == Guid.Empty) return new(RoundSyncStatus.InvalidMessage);
                return new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(new GenerationFailureReply(context.MatchId,
                    context.RoundId, context.SubmissionVersion, authenticatedSender, failed.ApprovalId,
                    host.ReportGenerationFailure(authenticatedSender, failed.ApprovalId))));
            case GalleryReady ready:
                if (!Round(ready.MatchId, ready.RoundId, ready.GalleryVersion - 1)) return new(RoundSyncStatus.WrongRound);
                if (ready.PlayerId != authenticatedSender) return new(RoundSyncStatus.WrongSender);
                return new(host.AcknowledgeGallery(authenticatedSender, ready) switch
                { GalleryReadyStatus.Accepted => RoundSyncStatus.Accepted, GalleryReadyStatus.Duplicate => RoundSyncStatus.Duplicate,
                    GalleryReadyStatus.Closed => RoundSyncStatus.Closed, _ => RoundSyncStatus.InvalidMessage });
            case VoteRequest vote:
                if (!Round(vote.MatchId, vote.RoundId, vote.VotingVersion - 2)) return new(RoundSyncStatus.WrongRound);
                if (vote.VoterId != authenticatedSender) return new(RoundSyncStatus.WrongSender);
                host.Vote(authenticatedSender, vote);
                var state = host.VotingState(authenticatedSender);
                return new(RoundSyncStatus.Accepted, state is null ? null : RoundControlMessages.Encode(state));
            default: return new(RoundSyncStatus.InvalidMessage); // Host-only messages cannot be sent by clients.
        }
    }
    public void Dispose() => Images.Dispose();
}

// Binds the reliable lane to one synchronous game dispatcher. It keeps at most
// one reply while the outgoing queue is full, and delays ACK until that reply is
// queued. The handler is never rerun when Poll/Receive retries this pending work.
// Caller serializes access on its network loop; disposing also owns the channel.
public sealed class ReliableControlEndpoint : IDisposable
{
    private readonly ReliableControlChannel channel;
    private readonly string peerId;
    private readonly Func<string, byte[], ControlDispatchResult> dispatch;
    private ulong? pendingSequence;
    private byte[]? pendingReply;
    public RoundSyncStatus? LastDispatchStatus { get; private set; }
    public int PendingReplyBytes => pendingReply?.Length ?? 0;

    public ReliableControlEndpoint(Guid matchId, Guid roundId, string authenticatedPeerId,
        Func<string, byte[], ControlDispatchResult> dispatch, TimeProvider? clock = null)
    {
        channel = new(matchId, roundId, authenticatedPeerId, clock: clock);
        peerId = authenticatedPeerId; this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }
    public ControlQueueStatus Queue<T>(T message) where T : class => Queue(message, out _);
    public ControlQueueStatus Queue<T>(T message, out ulong sequence) where T : class => channel.TryQueue(RoundControlMessages.Encode(message), out sequence);
    public ControlQueueStatus QueueEncoded(ReadOnlySpan<byte> bytes) => channel.TryQueue(bytes, out _);
    public ControlReceiveStatus Receive(string authenticatedSender, ReadOnlySpan<byte> packet)
    {
        var status = channel.Receive(authenticatedSender, packet); Pump(); return status;
    }
    public IReadOnlyList<byte[]> PollPackets() { Pump(); return channel.PollPackets(); }
    public ControlChannelSnapshot Snapshot() { Pump(); return channel.Snapshot(); }
    private void Pump()
    {
        if (channel.Snapshot().Failure != ControlChannelFailure.None) { pendingReply = null; pendingSequence = null; return; }
        if (pendingSequence is null && channel.TakeReceived() is { } message)
        {
            try
            {
                var result = dispatch(peerId, message.CopyPayload());
                if (result.Reply is { Length: 0 or > RoundControlMessages.MaxBytes }) throw new InvalidDataException("잘못된 제어 응답 크기입니다.");
                LastDispatchStatus = result.Status;
                pendingReply = result.Reply; pendingSequence = message.Sequence;
            }
            catch { channel.Disconnect(); pendingReply = null; pendingSequence = null; throw; }
        }
        if (pendingSequence is null) return;
        if (pendingReply is not null)
        {
            var queued = channel.TryQueue(pendingReply, out _);
            if (queued == ControlQueueStatus.Backpressure) return;
            pendingReply = null;
            if (queued == ControlQueueStatus.Closed) { pendingSequence = null; return; }
        }
        channel.ConfirmReceived(pendingSequence.Value); pendingSequence = null;
    }
    public void Disconnect() { channel.Disconnect(); pendingReply = null; pendingSequence = null; }
    public void Dispose() { channel.Dispose(); pendingReply = null; pendingSequence = null; }
}
