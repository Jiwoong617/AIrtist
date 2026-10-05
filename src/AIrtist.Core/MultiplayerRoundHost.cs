using System.Collections.ObjectModel;

namespace AIrtist.Core;

public sealed record MultiplayerRoundPolicy(TimeSpan WritingDuration, TimeSpan GenerationDuration,
    TimeSpan TransferDuration, VotingPolicy Voting)
{
    public TimeSpan GalleryDuration { get; init; } = TimeSpan.FromSeconds(60);
    // Provisional timings. Translation review is included in writing; transfer is
    // included in the approved player's generation budget, with its own shorter cap.
    public static MultiplayerRoundPolicy Default => new(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180),
        TimeSpan.FromSeconds(30), VotingPolicy.PreviewDefault);
}
public enum MultiplayerRoundPhase { Collecting, Distributing, Voting, Completed, GalleryTimedOut }
public enum SubmissionStatus { Accepted, Duplicate, InvalidRequest, WrongRound, StaleVersion, UnknownPlayer,
    IdentityMismatch, Closed, AlreadyApproved, RequestConflict }
public enum ImageTransferStatus { Accepted, Duplicate, Completed, InvalidRequest, WrongRound, StaleVersion,
    UnknownPlayer, IdentityMismatch, NotApproved, Closed, TransferConflict, CapacityExceeded, CorruptImage }
public enum PlayerRoundState { Writing, Generating, Receiving, Completed, MissedWritingDeadline, GenerationFailed,
    GenerationTimedOut, TransferTimedOut, InvalidImage }
public sealed record SubmissionReceipt(SubmissionStatus Status, GenerationApproval? Approval);
public sealed record RoundPrompt(string Id, string Korean, string Meaning);
public sealed record PlayerRoundProgress(string PlayerId, PlayerRoundState State);
public sealed record MultiplayerRoundSnapshot(Guid MatchId, Guid RoundId, long SubmissionVersion,
    MultiplayerRoundPhase Phase, RoundPrompt Prompt, DateTimeOffset WritingDeadline, TimeSpan WritingRemaining,
    ReadOnlyCollection<PlayerRoundProgress> Players, GenerationApproval? OwnApproval, VotingSnapshot? Voting,
    GalleryDeliverySnapshot? Gallery);

// One round only. A future match/lobby adapter owns the fixed authenticated roster,
// round scheduling and host disconnect policy. This class never treats client IDs
// in payloads as authentication and never calls the translator or generator.
public sealed class MultiplayerRoundHost : IDisposable
{
    public const int MaxRoundImageBytes = 16 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Guid matchId, roundId;
    private readonly long submissionVersion, started;
    private readonly string hostId;
    private readonly Word word;
    private readonly MultiplayerCompatibility compatibility;
    private readonly VotingPlayer[] players;
    private readonly Dictionary<string, Slot> slots;
    private readonly MultiplayerRoundPolicy policy;
    private readonly TimeProvider clock;
    private readonly DateTimeOffset writingDeadline;
    private VotingRoundHost? voting;
    private GalleryDelivery? gallery;
    private bool galleryTimedOut;
    private int reservedBytes;
    private bool disposed;

    private sealed class Slot
    {
        public PlayerRoundState State = PlayerRoundState.Writing;
        public SubmissionRequest? Request;
        public GenerationApproval? Approval;
        public long ApprovedAt;
        public ImageTransferDescriptor? Transfer;
        public IncomingImage? Incoming;
        public VotingArtwork? Artwork;
    }

    public MultiplayerRoundHost(Guid matchId, Guid roundId, long submissionVersion, string hostId, Word word,
        MultiplayerCompatibility compatibility, IEnumerable<MultiplayerParticipant> participants,
        MultiplayerRoundPolicy? policy = null, TimeProvider? clock = null)
    {
        if (matchId == Guid.Empty || roundId == Guid.Empty || submissionVersion < 1 || submissionVersion > long.MaxValue - 2)
            throw new ArgumentException("경기·라운드·상태 버전을 확인해 주세요.");
        ArgumentNullException.ThrowIfNull(word);
        if (string.IsNullOrWhiteSpace(word.Id) || string.IsNullOrWhiteSpace(word.Korean) || string.IsNullOrWhiteSpace(word.Meaning)
            || word.Aliases is null || word.Aliases.Length == 0
            || word.Aliases.Any(a => string.IsNullOrWhiteSpace(a) || InputRules.Canonical(a).Length == 0))
            throw new ArgumentException("멀티플레이 제시어 정보가 잘못되었습니다.");
        this.word = word with { Aliases = (string[])word.Aliases.Clone() };
        ArgumentNullException.ThrowIfNull(compatibility); compatibility.Validate(); this.compatibility = compatibility;
        var members = participants.Take(17).ToArray();
        if (members.Length is < 2 or > 16) throw new ArgumentException("현재 라운드 처리 인원은 2~16명입니다.");
        slots = new(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member?.Player is null || !compatibility.Matches(member.Compatibility))
                throw new ArgumentException("참가자의 통신·규칙·모델·단어 목록이 일치하지 않습니다.");
            VotingRoundHost.ValidateId(member.Player.Id);
            if (string.IsNullOrWhiteSpace(member.Player.Name) || member.Player.Name.Length > 40 || !slots.TryAdd(member.Player.Id, new()))
                throw new ArgumentException("참가자 ID·이름을 확인해 주세요.");
        }
        if (hostId is null || !slots.ContainsKey(hostId)) throw new ArgumentException("방장은 참가자여야 합니다.");
        players = members.Select(m => m.Player).ToArray();
        this.policy = policy ?? MultiplayerRoundPolicy.Default;
        static bool ValidTime(TimeSpan time, int maxMinutes) => time >= TimeSpan.FromSeconds(1) && time <= TimeSpan.FromMinutes(maxMinutes);
        if (!ValidTime(this.policy.WritingDuration, 10) || !ValidTime(this.policy.GenerationDuration, 10)
            || !ValidTime(this.policy.TransferDuration, 2) || !ValidTime(this.policy.GalleryDuration, 5)
            || this.policy.Voting is null || !ValidTime(this.policy.Voting.Duration, 10))
            throw new ArgumentException("작성·생성·전송·투표 제한 시간을 확인해 주세요.");
        this.clock = clock ?? TimeProvider.System;
        this.matchId = matchId; this.roundId = roundId; this.submissionVersion = submissionVersion; this.hostId = hostId;
        started = this.clock.GetTimestamp(); writingDeadline = this.clock.GetUtcNow() + this.policy.WritingDuration;
    }

    // A final confirmation only: preparing/editing a translation never calls Submit.
    public SubmissionReceipt Submit(string authenticatedPlayerId, SubmissionRequest request)
    {
        lock (sync)
        {
            Advance();
            SubmissionReceipt Reply(SubmissionStatus status, GenerationApproval? approval = null) => new(status, approval);
            if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80
                || request.Description is null || request.Description.Length > 160 || request.Translation is null || request.Translation.Length > 320)
                return Reply(SubmissionStatus.InvalidRequest);
            if (request.MatchId != matchId || request.RoundId != roundId) return Reply(SubmissionStatus.WrongRound);
            if (request.SubmissionVersion != submissionVersion) return Reply(SubmissionStatus.StaleVersion);
            if (authenticatedPlayerId is null || !slots.TryGetValue(authenticatedPlayerId, out var slot)) return Reply(SubmissionStatus.UnknownPlayer);
            if (authenticatedPlayerId != request.PlayerId) return Reply(SubmissionStatus.IdentityMismatch);
            if (slot.Request?.RequestId == request.RequestId)
                return slot.Request == request ? Reply(SubmissionStatus.Duplicate, slot.Approval) : Reply(SubmissionStatus.RequestConflict);
            if (slot.Approval is not null) return Reply(SubmissionStatus.AlreadyApproved);
            if (slot.State != PlayerRoundState.Writing || voting is not null) return Reply(SubmissionStatus.Closed);
            string description, translation;
            try { description = InputRules.Validate(request.Description, word); translation = InputRules.ValidateTranslation(request.Translation, word); }
            catch (ArgumentException) { return Reply(SubmissionStatus.InvalidRequest); }
            slot.Request = request; slot.ApprovedAt = clock.GetTimestamp();
            slot.Approval = new(matchId, roundId, submissionVersion, Guid.NewGuid(), authenticatedPlayerId,
                description, translation, clock.GetUtcNow() + policy.GenerationDuration);
            slot.State = PlayerRoundState.Generating;
            return Reply(SubmissionStatus.Accepted, slot.Approval);
        }
    }

    public RoundStartMessage StartMessage(string recipientId)
    {
        lock (sync)
        {
            Advance();
            if (!slots.ContainsKey(recipientId)) throw new UnauthorizedAccessException("참가자가 아닙니다.");
            return new(matchId, roundId, submissionVersion, hostId, recipientId, compatibility,
                Array.AsReadOnly((VotingPlayer[])players.Clone()), new(word.Id, word.Korean, word.Meaning), policy,
                writingDeadline, Remaining(started, policy.WritingDuration));
        }
    }

    internal SubmissionReplyMessage SubmitControl(string sender, SubmissionRequest request)
    {
        lock (sync)
        {
            var receipt = Submit(sender, request);
            var slot = slots[sender];
            var remaining = receipt.Approval is not null && slot.State == PlayerRoundState.Generating
                ? Remaining(slot.ApprovedAt, policy.GenerationDuration) : TimeSpan.Zero;
            return new(matchId, roundId, submissionVersion, sender, request.RequestId, receipt, remaining);
        }
    }

    public ImageTransferStatus BeginImage(string authenticatedPlayerId, ImageTransferDescriptor descriptor)
    {
        lock (sync)
        {
            Advance();
            try { ImagePackets.Validate(descriptor); } catch (ArgumentException) { return ImageTransferStatus.InvalidRequest; }
            if (descriptor.MatchId != matchId || descriptor.RoundId != roundId) return ImageTransferStatus.WrongRound;
            if (descriptor.SubmissionVersion != submissionVersion) return ImageTransferStatus.StaleVersion;
            if (authenticatedPlayerId is null || !slots.TryGetValue(authenticatedPlayerId, out var slot)) return ImageTransferStatus.UnknownPlayer;
            if (authenticatedPlayerId != descriptor.PlayerId) return ImageTransferStatus.IdentityMismatch;
            if (slot.Approval is null || slot.Approval.ApprovalId != descriptor.ApprovalId) return ImageTransferStatus.NotApproved;
            if (slot.Transfer is not null)
            {
                if (slot.Transfer != descriptor) return ImageTransferStatus.TransferConflict;
                return slot.State is PlayerRoundState.Receiving or PlayerRoundState.Completed ? ImageTransferStatus.Duplicate : ImageTransferStatus.Closed;
            }
            if (slot.State != PlayerRoundState.Generating || voting is not null) return ImageTransferStatus.Closed;
            // Transfer IDs bind packets to exactly one authenticated owner and approval.
            if (slots.Values.Any(s => s.Transfer?.TransferId == descriptor.TransferId)) return ImageTransferStatus.TransferConflict;
            if (reservedBytes + descriptor.TotalBytes > MaxRoundImageBytes) return ImageTransferStatus.CapacityExceeded;
            slot.Transfer = descriptor; slot.Incoming = new(descriptor, clock.GetTimestamp());
            reservedBytes += descriptor.TotalBytes; slot.State = PlayerRoundState.Receiving;
            return ImageTransferStatus.Accepted;
        }
    }

    public ImageTransferStatus ReceiveImagePacket(string authenticatedPlayerId, ReadOnlySpan<byte> packet)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedPlayerId is null || !slots.TryGetValue(authenticatedPlayerId, out var slot)) return ImageTransferStatus.UnknownPlayer;
            if (!ImagePackets.ReadHeader(packet, out var transferId, out int index, out int count)) return ImageTransferStatus.InvalidRequest;
            if (slot.Transfer is null || slot.Transfer.TransferId != transferId) return ImageTransferStatus.NotApproved;
            if (slot.State == PlayerRoundState.Completed)
            {
                // An acknowledgement for an already committed image cannot replace it.
                int expected = Math.Min(ImagePackets.PayloadBytes, slot.Transfer.TotalBytes - index * ImagePackets.PayloadBytes);
                return count == ImagePackets.Count(slot.Transfer.TotalBytes) && packet.Length == ImagePackets.HeaderBytes + expected
                    ? ImageTransferStatus.Duplicate : ImageTransferStatus.InvalidRequest;
            }
            if (slot.State != PlayerRoundState.Receiving || voting is not null) return ImageTransferStatus.Closed;
            var status = slot.Incoming!.Add(packet);
            if (status == ImagePacketStatus.InvalidPacket) return ImageTransferStatus.InvalidRequest;
            if (status == ImagePacketStatus.Duplicate) return ImageTransferStatus.Duplicate;
            if (status == ImagePacketStatus.Accepted) return ImageTransferStatus.Accepted;
            if (status is ImagePacketStatus.ConflictingPacket or ImagePacketStatus.HashMismatch)
            { Fail(slot, PlayerRoundState.InvalidImage); OpenVotingIfReady(); return ImageTransferStatus.CorruptImage; }
            byte[] png = slot.Incoming.TakeCompleted();
            VotingArtwork artwork;
            try { artwork = new(slot.Approval!.ApprovalId.ToString("N"), authenticatedPlayerId, png); }
            catch (ArgumentException)
            { Fail(slot, PlayerRoundState.InvalidImage); OpenVotingIfReady(); return ImageTransferStatus.CorruptImage; }
            // Decode/hash work must not commit an image after its deadline either.
            Advance();
            if (slot.State != PlayerRoundState.Receiving) return ImageTransferStatus.Closed;
            slot.Artwork = artwork; slot.Incoming = null;
            slot.State = PlayerRoundState.Completed;
            OpenVotingIfReady();
            return ImageTransferStatus.Completed;
        }
    }

    public ImageTransferStatus ReportGenerationFailure(string authenticatedPlayerId, Guid approvalId)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedPlayerId is null || !slots.TryGetValue(authenticatedPlayerId, out var slot)) return ImageTransferStatus.UnknownPlayer;
            if (slot.Approval is null || slot.Approval.ApprovalId != approvalId) return ImageTransferStatus.NotApproved;
            if (slot.State == PlayerRoundState.GenerationFailed) return ImageTransferStatus.Duplicate;
            if (slot.State is not (PlayerRoundState.Generating or PlayerRoundState.Receiving)) return ImageTransferStatus.Closed;
            Fail(slot, PlayerRoundState.GenerationFailed); OpenVotingIfReady(); return ImageTransferStatus.Accepted;
        }
    }

    public ImageProgressReply? ImageProgress(string authenticatedSender, ImageProgressRequest request)
    {
        lock (sync)
        {
            Advance();
            var descriptor = request?.Transfer;
            try { ImagePackets.Validate(descriptor!); } catch (ArgumentException) { return null; }
            if (request!.ProbeId < 1 || request.RecipientId != hostId || descriptor!.MatchId != matchId || descriptor.RoundId != roundId
                || descriptor.SubmissionVersion != submissionVersion || descriptor.PlayerId != authenticatedSender
                || !slots.TryGetValue(authenticatedSender, out var slot)) return null;
            bool matches = slot.Transfer == descriptor;
            var state = matches && slot.State == PlayerRoundState.Completed ? ImageProgressState.Complete
                : matches && slot.State == PlayerRoundState.Receiving ? ImageProgressState.Receiving : ImageProgressState.Rejected;
            return new(descriptor, authenticatedSender, request.ProbeId, state,
                state == ImageProgressState.Receiving ? slot.Incoming!.ReceivedChunks() : null);
        }
    }

    public byte[] CopyGalleryImage(string authenticatedPlayerId, Guid transferId)
    {
        lock (sync)
        {
            Advance();
            if (!slots.ContainsKey(authenticatedPlayerId)) throw new UnauthorizedAccessException("참가자가 아닙니다.");
            if (gallery is null || galleryTimedOut) throw new InvalidOperationException("갤러리 전송 단계가 아닙니다.");
            return gallery.CopyImage(transferId);
        }
    }

    public VoteReceipt Vote(string authenticatedPlayerId, VoteRequest request)
    {
        lock (sync) { Advance(); return voting?.Vote(authenticatedPlayerId, request) ?? new(VoteStatus.Closed, 0); }
    }

    public GalleryReadyStatus AcknowledgeGallery(string authenticatedPlayerId, GalleryReady message)
    {
        lock (sync)
        {
            Advance();
            if (galleryTimedOut) return GalleryReadyStatus.Closed;
            if (gallery is null) return GalleryReadyStatus.NotOpen;
            var reply = gallery.Acknowledge(authenticatedPlayerId, message);
            OpenVotingIfReady(); return reply;
        }
    }

    // Enumerate lazily with transport backpressure; do not queue all packets for all
    // participants at once. Per call, at most one PNG copy (4 MiB) is owned by sender.
    public IEnumerable<byte[]> GalleryPackets(string authenticatedPlayerId, Guid transferId)
    {
        ImageTransferDescriptor descriptor; byte[] png;
        lock (sync)
        {
            Advance();
            if (authenticatedPlayerId is null || !slots.ContainsKey(authenticatedPlayerId)) throw new UnauthorizedAccessException("참가자가 아닙니다.");
            if (gallery is null || galleryTimedOut) throw new InvalidOperationException("갤러리 전송 단계가 아닙니다.");
            descriptor = gallery.Manifest.Artworks.FirstOrDefault(a => a.Transfer.TransferId == transferId)?.Transfer
                ?? throw new ArgumentException("잘못된 전송 ID입니다.");
            png = gallery.CopyImage(transferId);
        }
        return ImagePackets.Split(descriptor, png);
    }

    public VotingStateMessage? VotingState(string authenticatedPlayerId)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedPlayerId is null || !slots.ContainsKey(authenticatedPlayerId)) throw new UnauthorizedAccessException("참가자가 아닙니다.");
            var state = voting?.Snapshot(authenticatedPlayerId);
            return state is null ? null : new(matchId, roundId, gallery!.Manifest.GalleryId, state.VotingVersion, state.Revision,
                authenticatedPlayerId, state.Deadline, state.Remaining, state.SelectedArtworkId, state.CanVote,
                state.AcceptedVotes, state.EligibleVoters, state.Result);
        }
    }

    public GalleryStoppedMessage? GalleryFailureState(string authenticatedPlayerId)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedPlayerId is null || !slots.ContainsKey(authenticatedPlayerId)) throw new UnauthorizedAccessException("참가자가 아닙니다.");
            return galleryTimedOut ? new(matchId, roundId, gallery!.Manifest.GalleryVersion, gallery.Manifest.GalleryId,
                authenticatedPlayerId, GalleryStopReason.DeliveryTimedOut) : null;
        }
    }

    public VotingResult CloseVoting(string authenticatedHostId)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedHostId != hostId) throw new UnauthorizedAccessException("방장만 투표를 마감할 수 있습니다.");
            return voting?.Close(authenticatedHostId) ?? throw new InvalidOperationException("투표가 시작되지 않았습니다.");
        }
    }

    public MultiplayerRoundSnapshot Snapshot(string viewerId)
    {
        lock (sync)
        {
            Advance();
            if (viewerId is null || !slots.TryGetValue(viewerId, out var own)) throw new ArgumentException("알 수 없는 참가자입니다.");
            var ballot = voting?.Snapshot(viewerId);
            var phase = galleryTimedOut ? MultiplayerRoundPhase.GalleryTimedOut
                : ballot is null ? (gallery is null ? MultiplayerRoundPhase.Collecting : MultiplayerRoundPhase.Distributing)
                : ballot.Result is null ? MultiplayerRoundPhase.Voting : MultiplayerRoundPhase.Completed;
            return new(matchId, roundId, submissionVersion, phase, new(word.Id, word.Korean, word.Meaning), writingDeadline,
                ballot is null ? Remaining(started, policy.WritingDuration) : TimeSpan.Zero,
                Array.AsReadOnly(players.Select(p => new PlayerRoundProgress(p.Id, slots[p.Id].State)).ToArray()), own.Approval, ballot,
                gallery?.Snapshot(galleryTimedOut));
        }
    }

    // Raw PNG reservations only. Decoder/preview memory and transport queues have
    // additional overhead and must be measured separately during real integration.
    public int ReservedImageBytes { get { lock (sync) { Advance(); return reservedBytes; } } }
    public int BufferedImageBytes { get { lock (sync) { Advance(); return slots.Values.Sum(s => s.Incoming?.BufferedBytes ?? 0); } } }

    private TimeSpan Remaining(long since, TimeSpan duration) => TimeSpan.FromTicks(Math.Max(0, (duration - clock.GetElapsedTime(since)).Ticks));
    private void Advance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (voting is not null || galleryTimedOut) return;
        if (gallery is not null)
        {
            // Provisional failure policy: stop this round, never award unseen votes.
            if (gallery.Remaining == TimeSpan.Zero) galleryTimedOut = true;
            return;
        }
        foreach (var slot in slots.Values)
        {
            if (slot.State == PlayerRoundState.Writing && Remaining(started, policy.WritingDuration) == TimeSpan.Zero)
                slot.State = PlayerRoundState.MissedWritingDeadline;
            else if (slot.State is PlayerRoundState.Generating or PlayerRoundState.Receiving)
            {
                if (Remaining(slot.ApprovedAt, policy.GenerationDuration) == TimeSpan.Zero) Fail(slot, PlayerRoundState.GenerationTimedOut);
                else if (slot.Incoming is not null && Remaining(slot.Incoming.Started, policy.TransferDuration) == TimeSpan.Zero)
                    Fail(slot, PlayerRoundState.TransferTimedOut);
            }
        }
        OpenVotingIfReady();
    }

    private void Fail(Slot slot, PlayerRoundState state)
    {
        if (slot.Transfer is not null && slot.State == PlayerRoundState.Receiving) reservedBytes -= slot.Transfer.TotalBytes;
        slot.Incoming?.Discard(); slot.Incoming = null; slot.State = state;
    }

    private void OpenVotingIfReady()
    {
        if (voting is not null || galleryTimedOut || slots.Values.Any(s => s.State is PlayerRoundState.Writing or PlayerRoundState.Generating or PlayerRoundState.Receiving)) return;
        var artworks = players.Select(p => slots[p.Id].Artwork).OfType<VotingArtwork>().ToArray();
        gallery ??= new(matchId, roundId, submissionVersion + 1, hostId, players, artworks, policy.GalleryDuration, policy.Voting, clock);
        // With 0 or 1 candidate there is no ballot timer; results can be distributed
        // without waiting for readiness. Otherwise all fixed participants must ack.
        if (artworks.Length < 2 || gallery.AllReady)
            voting = new(matchId, roundId, submissionVersion + 2, hostId, players, artworks, policy.Voting, clock);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            foreach (var slot in slots.Values) { slot.Incoming?.Discard(); slot.Incoming = null; slot.Artwork = null; }
            voting = null; gallery = null; reservedBytes = 0; disposed = true;
        }
    }
}
