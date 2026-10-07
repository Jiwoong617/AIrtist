namespace AIrtist.Core;

public enum SubmissionClientPhase { AwaitingStart, Writing, AwaitingApproval, Approved, Generating,
    AwaitingImagePermission, SendingImage, AwaitingGallery, Disconnected, AwaitingFailureConfirmation }
public sealed record SubmissionClientSnapshot(SubmissionClientPhase Phase, RoundPrompt? Prompt, TimeSpan WritingRemaining,
    TimeSpan GenerationRemaining, SubmissionStatus? LastSubmissionStatus, bool CanSendImage, string? Failure, ImageSendEnd? UploadEnd)
{
    public IReadOnlyList<PlayerRoundProgress> Players { get; init; } = [];
    public GenerationParameters? FixedParameters { get; init; }
    public GenerationParameters? ApprovedParameters { get; init; }
}

// Pre-gallery client state. The lobby supplies trusted identities/compatibility
// and a verified local word catalog. Only TakeGenerationApproval starts AI work;
// the prompt is kept here for display/validation and is absent from that approval.
public sealed class MultiplayerSubmissionClient : IDisposable
{
    public static TimeSpan ResponseTimeout => TimeSpan.FromSeconds(10); // Provisional, bounded business-response wait.
    private readonly object sync = new();
    private readonly Guid matchId, roundId;
    private readonly long version;
    private readonly string hostId, localId;
    private readonly VotingPlayer[] players;
    private readonly MultiplayerCompatibility compatibility;
    private readonly Dictionary<string, Word> words;
    private readonly TimeProvider clock;
    private RoundStartMessage? start;
    private byte[]? startIdentity;
    private long writingReceived, approvalReceived, requestStarted;
    private TimeSpan writingBudget, generationBudget;
    private SubmissionRequest? pending;
    private GenerationApproval? approval;
    private ImageTransferDescriptor? transfer;
    private SubmissionStatus? lastStatus;
    private SubmissionClientPhase phase;
    private string? failure;
    private bool disposed;
    private readonly ReliableImageSendQueue images;
    private ImageSendEnd? uploadEnd;
    private GenerationFailedMessage? generationFailure;
    private GenerationFailureReply? failureReply;
    private RoundProgressMessage? roundProgress;
    public MultiplayerRoundClient Gallery { get; }

    public MultiplayerSubmissionClient(Guid matchId, Guid roundId, long submissionVersion, string hostId, string localId,
        IEnumerable<VotingPlayer> players, MultiplayerCompatibility compatibility, IEnumerable<Word> words, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(compatibility); compatibility.Validate();
        this.players = players.Take(17).ToArray(); this.clock = clock ?? TimeProvider.System;
        this.words = words.ToDictionary(w => w.Id, w => w with { Aliases = (string[])w.Aliases.Clone() }, StringComparer.Ordinal);
        if (this.words.Count == 0 || this.words.Values.Any(w => string.IsNullOrWhiteSpace(w.Korean) || string.IsNullOrWhiteSpace(w.Meaning)
            || w.Aliases.Length == 0 || w.Aliases.Any(a => string.IsNullOrWhiteSpace(a) || InputRules.Canonical(a).Length == 0)))
            throw new ArgumentException("검증된 단어 목록이 필요합니다.");
        Gallery = new(matchId, roundId, submissionVersion, hostId, localId, this.players, this.clock);
        this.matchId = matchId; this.roundId = roundId; version = submissionVersion;
        this.hostId = hostId; this.localId = localId; this.compatibility = compatibility;
        images = new(matchId, roundId, submissionVersion, localId, clock: this.clock);
    }

    public ControlDispatchResult Dispatch(string authenticatedSender, ReadOnlySpan<byte> bytes)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId) return new(RoundSyncStatus.WrongSender);
            if (phase == SubmissionClientPhase.Disconnected) return new(RoundSyncStatus.Closed);
            object message;
            try { message = RoundControlMessages.Decode(bytes); }
            catch (InvalidDataException) { return new(RoundSyncStatus.InvalidMessage); }
            RoundSyncStatus status;
            switch (message)
            {
                case RoundStartMessage value: status = AcceptStart(value); break;
                case SubmissionReplyMessage value: status = AcceptApproval(value); break;
                case ImageBeginReplyMessage value: status = AcceptImageBegin(value); break;
                case GenerationFailureReply value: status = AcceptGenerationFailure(value); break;
                case RoundProgressMessage value: status = AcceptProgress(value); break;
                case ImageProgressReply value:
                    status = images.ReceiveProgress(authenticatedSender, value); ApplyImageResults(); break;
                case ImageProgressRequest value when start is not null:
                    var progress = Gallery.ImageProgress(authenticatedSender, value);
                    return progress is null ? new(RoundSyncStatus.InvalidMessage) : new(RoundSyncStatus.Accepted, RoundControlMessages.Encode(progress));
                case GalleryManifest value when start is not null:
                    if (value.VotingPolicy != start.Policy.Voting || value.DeliveryTimeout != start.Policy.GalleryDuration)
                    { status = RoundSyncStatus.InvalidMessage; break; }
                    status = Gallery.AcceptGallery(authenticatedSender, value);
                    if (status == RoundSyncStatus.Accepted)
                    {
                        phase = SubmissionClientPhase.AwaitingGallery;
                        if (transfer is not null)
                        {
                            bool collected = value.Artworks.Any(a => a.ArtworkId == transfer.ApprovalId.ToString("N")
                                && a.Transfer.PlayerId == localId && a.Transfer.TotalBytes == transfer.TotalBytes
                                && string.Equals(a.Transfer.Sha256, transfer.Sha256, StringComparison.OrdinalIgnoreCase));
                            if (collected) { images.ConfirmFromGallery(hostId, transfer); uploadEnd = ImageSendEnd.Complete; failure = null; }
                            else images.Cancel(hostId, transfer.TransferId);
                        }
                        ApplyImageResults();
                    }
                    break;
                case GalleryStoppedMessage value when start is not null: status = Gallery.AcceptGalleryStopped(authenticatedSender, value); break;
                case VotingStateMessage value when start is not null: status = Gallery.AcceptVotingState(authenticatedSender, value); break;
                default: status = RoundSyncStatus.InvalidMessage; break;
            }
            return new(status);
        }
    }

    private bool SameRound(Guid match, Guid round, long stateVersion) => match == matchId && round == roundId && stateVersion == version;
    private RoundSyncStatus AcceptProgress(RoundProgressMessage value)
    {
        if (!SameRound(value.MatchId, value.RoundId, value.SubmissionVersion)) return RoundSyncStatus.WrongRound;
        if (value.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        if (start is null) return RoundSyncStatus.NotReady;
        if (value.Revision < 1 || !Enum.IsDefined(value.Phase) || value.Players is null || value.Players.Count != players.Length
            || value.Players.Where((p, i) => p is null || p.PlayerId != players[i].Id || !Enum.IsDefined(p.State)).Any()
            || value.WritingRemaining < TimeSpan.Zero || value.WritingRemaining > start.Policy.WritingDuration) return RoundSyncStatus.InvalidMessage;
        static int Rank(PlayerRoundState s) => s == PlayerRoundState.Writing ? 0 : s == PlayerRoundState.Generating ? 1 : s == PlayerRoundState.Receiving ? 2 : 3;
        if (roundProgress is not null)
        {
            if (value.Revision < roundProgress.Revision) return RoundSyncStatus.Stale;
            if (value.Revision == roundProgress.Revision && (value.Phase != roundProgress.Phase || !value.Players.SequenceEqual(roundProgress.Players))) return RoundSyncStatus.Conflict;
            if (value.Phase < roundProgress.Phase || (roundProgress.Phase is MultiplayerRoundPhase.Completed or MultiplayerRoundPhase.GalleryTimedOut && value.Phase != roundProgress.Phase)
                || value.Players.Where((p, i) => Rank(p.State) < Rank(roundProgress.Players[i].State)
                    || (Rank(roundProgress.Players[i].State) == 3 && p.State != roundProgress.Players[i].State)).Any()) return RoundSyncStatus.Conflict;
        }
        bool duplicate = roundProgress?.Revision == value.Revision;
        writingBudget = Min(WritingRemaining(), value.WritingRemaining); writingReceived = clock.GetTimestamp();
        roundProgress = value with { Players = Array.AsReadOnly(value.Players.ToArray()) };
        var own = value.Players.Single(p => p.PlayerId == localId).State;
        if (own is PlayerRoundState.MissedWritingDeadline or PlayerRoundState.GenerationFailed or PlayerRoundState.GenerationTimedOut or PlayerRoundState.TransferTimedOut or PlayerRoundState.InvalidImage)
        {
            phase = SubmissionClientPhase.AwaitingGallery;
            failure = own switch
            {
                PlayerRoundState.MissedWritingDeadline => "작성 시간이 끝나 그림을 제출하지 못했습니다.",
                PlayerRoundState.GenerationFailed => "그림 생성이 실패하거나 취소되었습니다.",
                PlayerRoundState.GenerationTimedOut => "그림 생성 제한 시간을 초과했습니다.",
                PlayerRoundState.TransferTimedOut => "그림 전송 제한 시간을 초과했습니다.",
                _ => "그림 파일을 확인하지 못해 제출이 종료되었습니다."
            };
        }
        return duplicate ? RoundSyncStatus.Duplicate : RoundSyncStatus.Accepted;
    }
    private RoundSyncStatus AcceptStart(RoundStartMessage value)
    {
        if (!SameRound(value.MatchId, value.RoundId, value.SubmissionVersion)) return RoundSyncStatus.WrongRound;
        if (value.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        static bool Time(TimeSpan time, int minutes) => time >= TimeSpan.FromSeconds(1) && time <= TimeSpan.FromMinutes(minutes);
        var p = value.Policy;
        if (value.HostId != hostId || !compatibility.Matches(value.Compatibility) || value.Players is null || !players.SequenceEqual(value.Players)
            || value.Prompt?.Id is null || !words.TryGetValue(value.Prompt.Id, out var word)
            || value.Prompt.Korean != word.Korean || value.Prompt.Meaning != word.Meaning
            || p is null || !Time(p.WritingDuration, 10) || !Time(p.GenerationDuration, 10) || !Time(p.TransferDuration, 2)
            || !Time(p.GalleryDuration, 5) || p.Voting is null || !Time(p.Voting.Duration, 10) || !p.HasValidParameters
            || value.WritingRemaining < TimeSpan.Zero || value.WritingRemaining > p.WritingDuration) return RoundSyncStatus.InvalidMessage;
        byte[] identity = RoundControlMessages.Encode(value with { WritingRemaining = TimeSpan.Zero });
        if (start is not null)
        {
            if (!identity.AsSpan().SequenceEqual(startIdentity)) return RoundSyncStatus.Conflict;
            writingBudget = Min(WritingRemaining(), value.WritingRemaining); writingReceived = clock.GetTimestamp();
            return RoundSyncStatus.Duplicate;
        }
        start = value with { Players = Array.AsReadOnly(value.Players.ToArray()) }; startIdentity = identity;
        writingBudget = value.WritingRemaining; writingReceived = clock.GetTimestamp();
        phase = writingBudget > TimeSpan.Zero ? SubmissionClientPhase.Writing : SubmissionClientPhase.AwaitingGallery;
        return RoundSyncStatus.Accepted;
    }

    // Called only on the player's final confirmation, never during translation.
    public SubmissionRequest CreateSubmission(string description, string translation, GenerationParameters? parameters = null)
    {
        lock (sync)
        {
            Advance();
            if (phase != SubmissionClientPhase.Writing) throw new InvalidOperationException("현재 묘사를 확정할 수 없습니다.");
            var word = words[start!.Prompt.Id];
            var selected = parameters ?? start.Policy.FixedParameters ?? new GenerationParameters();
            selected.Validate();
            if (start.Policy.FixedParameters is { } fixedValues && selected != fixedValues)
                throw new ArgumentException("이 방은 방장이 정한 파라미터로 고정되어 있습니다.");
            pending = new(matchId, roundId, version, Guid.NewGuid().ToString("N"), localId,
                InputRules.Validate(description, word), InputRules.ValidateTranslation(translation, word)) { Parameters = selected };
            phase = SubmissionClientPhase.AwaitingApproval; requestStarted = clock.GetTimestamp(); lastStatus = null; return pending;
        }
    }
    public SubmissionRequest RetrySubmission()
    {
        lock (sync) { Advance(); return phase == SubmissionClientPhase.AwaitingApproval ? pending! : throw new InvalidOperationException("대기 중인 제출이 없습니다."); }
    }

    private RoundSyncStatus AcceptApproval(SubmissionReplyMessage reply)
    {
        if (!SameRound(reply.MatchId, reply.RoundId, reply.SubmissionVersion)) return RoundSyncStatus.WrongRound;
        if (reply.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        if (pending is null || pending.RequestId != reply.RequestId || start is null) return RoundSyncStatus.Stale;
        if (reply.Receipt is null || !Enum.IsDefined(reply.Receipt.Status) || reply.GenerationRemaining < TimeSpan.Zero
            || reply.GenerationRemaining > start.Policy.GenerationDuration) return RoundSyncStatus.InvalidMessage;
        var result = reply.Receipt;
        if (result.Status is not (SubmissionStatus.Accepted or SubmissionStatus.Duplicate))
        {
            if (result.Approval is not null || reply.GenerationRemaining != TimeSpan.Zero) return RoundSyncStatus.InvalidMessage;
            if (approval is not null || phase != SubmissionClientPhase.AwaitingApproval) return RoundSyncStatus.Stale;
            lastStatus = result.Status;
            phase = result.Status == SubmissionStatus.InvalidRequest && WritingRemaining() > TimeSpan.Zero
                ? SubmissionClientPhase.Writing : SubmissionClientPhase.AwaitingGallery;
            return RoundSyncStatus.Accepted;
        }
        var a = result.Approval;
        if (a is null || !SameRound(a.MatchId, a.RoundId, a.SubmissionVersion) || a.PlayerId != localId || a.ApprovalId == Guid.Empty
            || a.Description != pending.Description || a.Translation != pending.Translation
            || !pending.Parameters.Accepts(a.Parameters)) return RoundSyncStatus.InvalidMessage;
        if (approval is not null)
        {
            if (approval != a) return RoundSyncStatus.Conflict;
            generationBudget = Min(GenerationRemaining(), reply.GenerationRemaining); approvalReceived = clock.GetTimestamp();
            return RoundSyncStatus.Duplicate;
        }
        if (phase != SubmissionClientPhase.AwaitingApproval) return RoundSyncStatus.Closed;
        // Conservatively subtract this request's entire local wait. This avoids
        // launching on an approval whose host budget expired in transit without
        // depending on synchronized wall clocks. Retries never restart this wait.
        approval = a; generationBudget = TimeSpan.FromTicks(Math.Max(0,
            (reply.GenerationRemaining - clock.GetElapsedTime(requestStarted)).Ticks));
        approvalReceived = clock.GetTimestamp(); lastStatus = result.Status;
        phase = generationBudget > TimeSpan.Zero ? SubmissionClientPhase.Approved : SubmissionClientPhase.AwaitingGallery;
        return RoundSyncStatus.Accepted;
    }

    public GenerationApproval? TakeGenerationApproval()
    {
        lock (sync) { Advance(); if (phase != SubmissionClientPhase.Approved) return null; phase = SubmissionClientPhase.Generating; return approval; }
    }

    public ImageTransferDescriptor PrepareImage(byte[] png)
    {
        lock (sync)
        {
            Advance();
            if (phase != SubmissionClientPhase.Generating) throw new InvalidOperationException("현재 그림을 제출할 수 없습니다.");
            transfer = ImagePackets.Describe(approval!, Guid.NewGuid(), png); phase = SubmissionClientPhase.AwaitingImagePermission;
            requestStarted = clock.GetTimestamp(); return transfer;
        }
    }
    public ImageTransferDescriptor RetryImageBegin()
    {
        lock (sync) { Advance(); return phase == SubmissionClientPhase.AwaitingImagePermission ? transfer! : throw new InvalidOperationException("그림 전송 승인 대기 중이 아닙니다."); }
    }

    public GenerationFailedMessage CreateGenerationFailure(Guid approvalId)
    {
        lock (sync)
        {
            Advance();
            if (phase == SubmissionClientPhase.AwaitingFailureConfirmation && generationFailure?.ApprovalId == approvalId) return generationFailure;
            if (approval?.ApprovalId != approvalId || phase is not (SubmissionClientPhase.Approved or SubmissionClientPhase.Generating
                or SubmissionClientPhase.AwaitingImagePermission or SubmissionClientPhase.SendingImage))
                throw new InvalidOperationException("현재 생성 작업의 실패가 아닙니다.");
            if (transfer is not null) images.Cancel(hostId, transfer.TransferId);
            ApplyImageResults();
            generationFailure = new(matchId, roundId, version, localId, approvalId);
            phase = SubmissionClientPhase.AwaitingFailureConfirmation; requestStarted = clock.GetTimestamp();
            failure = "그림 생성을 완료하지 못했습니다."; return generationFailure;
        }
    }
    private RoundSyncStatus AcceptGenerationFailure(GenerationFailureReply reply)
    {
        if (!SameRound(reply.MatchId, reply.RoundId, reply.SubmissionVersion)) return RoundSyncStatus.WrongRound;
        if (reply.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        if (!Enum.IsDefined(reply.Status)) return RoundSyncStatus.InvalidMessage;
        if (generationFailure?.ApprovalId != reply.ApprovalId) return RoundSyncStatus.Stale;
        if (failureReply is not null) return failureReply == reply
            || (failureReply.Status is ImageTransferStatus.Accepted or ImageTransferStatus.Duplicate
                && reply.Status is ImageTransferStatus.Accepted or ImageTransferStatus.Duplicate) ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict;
        if (phase != SubmissionClientPhase.AwaitingFailureConfirmation) return RoundSyncStatus.Closed;
        failureReply = reply; phase = SubmissionClientPhase.AwaitingGallery; return RoundSyncStatus.Accepted;
    }

    public ImageQueueStatus QueueImage(byte[] png)
    {
        lock (sync)
        {
            Advance();
            if (phase != SubmissionClientPhase.SendingImage) throw new InvalidOperationException("그림 전송이 허가되지 않았습니다.");
            return images.TryQueue(hostId, transfer!, png, Min(start!.Policy.TransferDuration, GenerationRemaining()));
        }
    }
    public IReadOnlyList<OutgoingImagePacket> PollImagePackets()
    {
        lock (sync) { Advance(); return images.PollPackets(); }
    }
    public int PumpImageProgress(Func<string, ImageProgressRequest, ControlQueueStatus> enqueue)
    {
        lock (sync) { Advance(); return images.PumpProgressRequests(enqueue); }
    }
    public ImageSendSnapshot ImageSendSnapshot() { lock (sync) { Advance(); return images.Snapshot(); } }
    private void ApplyImageResults()
    {
        foreach (var result in images.TakeResults())
        {
            if (result.Transfer != transfer) continue;
            uploadEnd = result.End;
            if (phase == SubmissionClientPhase.SendingImage) phase = SubmissionClientPhase.AwaitingGallery;
            failure = result.End switch
            {
                ImageSendEnd.Complete => null, ImageSendEnd.TimedOut => "그림 전송 제한 시간을 초과했습니다.",
                ImageSendEnd.Disconnected => "연결이 끊겨 그림을 전송하지 못했습니다.",
                ImageSendEnd.Cancelled => "그림 전송이 취소되었습니다.", _ => "방장이 그림 전송을 승인하지 않았습니다."
            };
        }
    }
    private RoundSyncStatus AcceptImageBegin(ImageBeginReplyMessage reply)
    {
        if (!SameRound(reply.MatchId, reply.RoundId, reply.SubmissionVersion)) return RoundSyncStatus.WrongRound;
        if (reply.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
        if (!Enum.IsDefined(reply.Status)) return RoundSyncStatus.InvalidMessage;
        if (transfer is null || reply.TransferId != transfer.TransferId || reply.ApprovalId != transfer.ApprovalId) return RoundSyncStatus.Stale;
        if (phase == SubmissionClientPhase.SendingImage && reply.Status is ImageTransferStatus.Accepted or ImageTransferStatus.Duplicate) return RoundSyncStatus.Duplicate;
        if (phase != SubmissionClientPhase.AwaitingImagePermission) return RoundSyncStatus.Closed;
        phase = reply.Status is ImageTransferStatus.Accepted or ImageTransferStatus.Duplicate
            ? SubmissionClientPhase.SendingImage : SubmissionClientPhase.AwaitingGallery;
        return RoundSyncStatus.Accepted;
    }
    public IEnumerable<byte[]> UploadPackets(byte[] png)
    {
        ImageTransferDescriptor descriptor;
        lock (sync) { Advance(); if (phase != SubmissionClientPhase.SendingImage) throw new InvalidOperationException("그림 전송이 허가되지 않았습니다."); descriptor = transfer!; }
        foreach (var packet in ImagePackets.Split(descriptor, png))
        {
            lock (sync) { Advance(); if (phase != SubmissionClientPhase.SendingImage) yield break; }
            yield return packet;
        }
    }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private TimeSpan WritingRemaining() => start is null ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (writingBudget - clock.GetElapsedTime(writingReceived)).Ticks));
    private TimeSpan GenerationRemaining() => approval is null ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, (generationBudget - clock.GetElapsedTime(approvalReceived)).Ticks));
    public SubmissionClientSnapshot Snapshot()
    {
        lock (sync) { Advance(); return new(phase, start?.Prompt, WritingRemaining(), GenerationRemaining(), lastStatus, phase == SubmissionClientPhase.SendingImage, failure, uploadEnd)
            { Players = roundProgress?.Players ?? Array.AsReadOnly(players.Select(p => new PlayerRoundProgress(p.Id, PlayerRoundState.Writing)).ToArray()),
                FixedParameters = start?.Policy.FixedParameters, ApprovedParameters = approval?.Parameters }; }
    }
    private void Advance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ApplyImageResults();
        if (phase is SubmissionClientPhase.AwaitingApproval or SubmissionClientPhase.AwaitingImagePermission or SubmissionClientPhase.AwaitingFailureConfirmation
            && clock.GetElapsedTime(requestStarted) >= ResponseTimeout)
        { phase = SubmissionClientPhase.AwaitingGallery; failure = "방장 응답 제한 시간을 초과했습니다."; }
        if (phase == SubmissionClientPhase.Writing && WritingRemaining() == TimeSpan.Zero) phase = SubmissionClientPhase.AwaitingGallery;
        if (phase is SubmissionClientPhase.Approved or SubmissionClientPhase.Generating or SubmissionClientPhase.AwaitingImagePermission or SubmissionClientPhase.SendingImage
            && GenerationRemaining() == TimeSpan.Zero) phase = SubmissionClientPhase.AwaitingGallery;
        if (phase != SubmissionClientPhase.SendingImage && transfer is not null) { images.Cancel(hostId, transfer.TransferId); ApplyImageResults(); }
    }
    public void MarkHostUnavailable()
    {
        lock (sync) { Advance(); phase = SubmissionClientPhase.Disconnected; images.DisconnectPeer(hostId); ApplyImageResults(); failure = "방장 연결이 끊어졌습니다."; Gallery.MarkHostUnavailable(); }
    }
    public void Dispose() { lock (sync) { if (disposed) return; images.Dispose(); Gallery.Dispose(); pending = null; approval = null; transfer = null; disposed = true; } }
}
