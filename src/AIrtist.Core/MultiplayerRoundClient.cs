using System.Collections.ObjectModel;

namespace AIrtist.Core;

public enum RoundClientPhase { AwaitingGallery, ReceivingGallery, Ready, Voting, Completed, Failed, Disconnected }
public enum RoundSyncStatus { Accepted, Duplicate, Complete, WrongSender, WrongRound, WrongRecipient, NotReady,
    InvalidMessage, Conflict, Stale, Closed, CorruptImage }
public sealed record RoundClientSnapshot(RoundClientPhase Phase, ReadOnlyCollection<VotingArtwork> Artworks,
    bool CanVote, string? SelectedArtworkId, TimeSpan VotingRemaining, VotingResult? Result, string? Failure);

// One instance per authenticated round. Roster, host and local ID are established
// by the lobby adapter, never learned from the gallery message being validated.
public sealed class MultiplayerRoundClient : IDisposable
{
    private readonly object sync = new();
    private readonly Guid matchId, roundId;
    private readonly long galleryVersion;
    private readonly string hostId, localId;
    private readonly VotingPlayer[] players;
    private readonly TimeProvider clock;
    private readonly Dictionary<Guid, IncomingImage> incoming = new();
    private readonly Dictionary<Guid, VotingArtwork> pictures = new();
    private GalleryManifest? manifest;
    private byte[]? manifestBytes;
    private VotingStateMessage? voting;
    private long galleryStarted, votingReceived;
    private VoteRequest? pendingVote;
    private RoundClientPhase phase = RoundClientPhase.AwaitingGallery;
    private string? failure;
    private bool disposed;

    public MultiplayerRoundClient(Guid matchId, Guid roundId, long submissionVersion, string hostId, string localId,
        IEnumerable<VotingPlayer> players, TimeProvider? clock = null)
    {
        if (matchId == Guid.Empty || roundId == Guid.Empty || submissionVersion < 1 || submissionVersion > long.MaxValue - 2)
            throw new ArgumentException("잘못된 라운드 정보입니다.");
        this.players = players.Take(17).ToArray();
        if (this.players.Length is < 2 or > 16 || this.players.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40)
            || this.players.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != this.players.Length)
            throw new ArgumentException("잘못된 참가자 목록입니다.");
        foreach (var player in this.players) VotingRoundHost.ValidateId(player.Id);
        if (!this.players.Any(p => p.Id == hostId) || !this.players.Any(p => p.Id == localId)) throw new ArgumentException("방장·참가자 ID를 확인해 주세요.");
        this.matchId = matchId; this.roundId = roundId; galleryVersion = submissionVersion + 1;
        this.hostId = hostId; this.localId = localId; this.clock = clock ?? TimeProvider.System;
    }

    public RoundSyncStatus AcceptGallery(string authenticatedSender, GalleryManifest message)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId) return RoundSyncStatus.WrongSender;
            if (phase is RoundClientPhase.Failed or RoundClientPhase.Disconnected) return RoundSyncStatus.Closed;
            if (message is null) return RoundSyncStatus.InvalidMessage;
            if (message.MatchId != matchId || message.RoundId != roundId || message.GalleryVersion != galleryVersion) return RoundSyncStatus.WrongRound;
            try { ValidateManifest(message); } catch (ArgumentException) { return RoundSyncStatus.InvalidMessage; }
            byte[] bytes;
            try { bytes = RoundControlMessages.Encode(message); } catch (InvalidDataException) { return RoundSyncStatus.InvalidMessage; }
            if (manifest is not null) return manifestBytes!.AsSpan().SequenceEqual(bytes) ? RoundSyncStatus.Duplicate : RoundSyncStatus.Conflict;
            manifest = message with { Players = Array.AsReadOnly(message.Players.ToArray()), Artworks = Array.AsReadOnly(message.Artworks.ToArray()) };
            manifestBytes = bytes; galleryStarted = clock.GetTimestamp();
            foreach (var entry in manifest.Artworks) incoming.Add(entry.Transfer.TransferId, new(entry.Transfer, galleryStarted));
            phase = incoming.Count == 0 ? RoundClientPhase.Ready : RoundClientPhase.ReceivingGallery;
            return RoundSyncStatus.Accepted;
        }
    }

    private void ValidateManifest(GalleryManifest message)
    {
        if (message.HostId != hostId || message.GalleryId == Guid.Empty || message.Players is null || !message.Players.SequenceEqual(players)
            || message.Artworks is null || message.Artworks.Count > players.Length
            || message.DeliveryTimeout < TimeSpan.FromSeconds(1) || message.DeliveryTimeout > TimeSpan.FromMinutes(5)
            || message.VotingPolicy is null || message.VotingPolicy.Duration < TimeSpan.FromSeconds(1) || message.VotingPolicy.Duration > TimeSpan.FromMinutes(10))
            throw new ArgumentException("잘못된 갤러리 정보입니다.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var owners = new HashSet<string>(StringComparer.Ordinal); var transfers = new HashSet<Guid>();
        int bytes = 0;
        foreach (var entry in message.Artworks)
        {
            if (entry is null) throw new ArgumentException("그림 정보가 없습니다.");
            ImagePackets.Validate(entry.Transfer);
            var transfer = entry.Transfer;
            if (transfer.MatchId != matchId || transfer.RoundId != roundId || transfer.SubmissionVersion != galleryVersion
                || entry.ArtworkId != transfer.ApprovalId.ToString("N") || !players.Any(p => p.Id == transfer.PlayerId)
                || !ids.Add(entry.ArtworkId) || !owners.Add(transfer.PlayerId) || !transfers.Add(transfer.TransferId))
                throw new ArgumentException("그림 소유자·전송 정보가 잘못되었습니다.");
            bytes += transfer.TotalBytes;
        }
        if (bytes > MultiplayerRoundHost.MaxRoundImageBytes) throw new ArgumentException("갤러리 크기 상한을 초과했습니다.");
    }

    public RoundSyncStatus ReceiveGalleryPacket(string authenticatedSender, ReadOnlySpan<byte> packet)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId) return RoundSyncStatus.WrongSender;
            if (phase is RoundClientPhase.Failed or RoundClientPhase.Disconnected) return RoundSyncStatus.Closed;
            if (manifest is null) return RoundSyncStatus.NotReady;
            if (!ImagePackets.ReadHeader(packet, out var transferId, out int index, out int count)) return RoundSyncStatus.InvalidMessage;
            var entry = manifest.Artworks.FirstOrDefault(a => a.Transfer.TransferId == transferId);
            if (entry is null) return RoundSyncStatus.WrongRound;
            if (pictures.ContainsKey(transferId))
            {
                int expected = Math.Min(ImagePackets.PayloadBytes, entry.Transfer.TotalBytes - index * ImagePackets.PayloadBytes);
                return count == ImagePackets.Count(entry.Transfer.TotalBytes) && packet.Length == ImagePackets.HeaderBytes + expected
                    ? RoundSyncStatus.Duplicate : RoundSyncStatus.InvalidMessage;
            }
            var receiver = incoming[transferId];
            var status = receiver.Add(packet);
            if (status == ImagePacketStatus.InvalidPacket) return RoundSyncStatus.InvalidMessage;
            if (status == ImagePacketStatus.Accepted) return RoundSyncStatus.Accepted;
            if (status == ImagePacketStatus.Duplicate) return RoundSyncStatus.Duplicate;
            if (status is ImagePacketStatus.HashMismatch or ImagePacketStatus.ConflictingPacket)
            { Fail("그림 전송 데이터가 손상되었습니다."); return RoundSyncStatus.CorruptImage; }
            VotingArtwork artwork;
            try { artwork = new(entry.ArtworkId, entry.Transfer.PlayerId, receiver.TakeCompleted()); }
            catch (ArgumentException) { Fail("지원하지 않거나 손상된 PNG입니다."); return RoundSyncStatus.CorruptImage; }
            Advance();
            if (phase == RoundClientPhase.Failed) return RoundSyncStatus.Closed;
            incoming.Remove(transferId); pictures.Add(transferId, artwork);
            if (incoming.Count == 0) phase = RoundClientPhase.Ready;
            return RoundSyncStatus.Complete;
        }
    }

    public GalleryReady? ReadyMessage()
    {
        lock (sync)
        {
            Advance();
            return phase is RoundClientPhase.Ready or RoundClientPhase.Voting or RoundClientPhase.Completed
                ? new(matchId, roundId, galleryVersion, manifest!.GalleryId, localId) : null;
        }
    }

    public ImageProgressReply? ImageProgress(string authenticatedSender, ImageProgressRequest request)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId || request?.Transfer is null || request.ProbeId < 1 || request.RecipientId != localId) return null;
            var entry = manifest?.Artworks.FirstOrDefault(a => a.Transfer == request.Transfer);
            if (entry is null) return null;
            ImageProgressState state = phase is RoundClientPhase.Failed or RoundClientPhase.Disconnected ? ImageProgressState.Rejected
                : pictures.ContainsKey(entry.Transfer.TransferId) ? ImageProgressState.Complete : ImageProgressState.Receiving;
            return new(entry.Transfer, hostId, request.ProbeId, state,
                state == ImageProgressState.Receiving ? incoming[entry.Transfer.TransferId].ReceivedChunks() : null);
        }
    }

    public RoundSyncStatus AcceptVotingState(string authenticatedSender, VotingStateMessage message)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId) return RoundSyncStatus.WrongSender;
            if (phase is RoundClientPhase.Failed or RoundClientPhase.Disconnected) return RoundSyncStatus.Closed;
            if (message is null) return RoundSyncStatus.InvalidMessage;
            if (message.MatchId != matchId || message.RoundId != roundId || message.VotingVersion != galleryVersion + 1) return RoundSyncStatus.WrongRound;
            if (message.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
            if (manifest is null || incoming.Count != 0) return RoundSyncStatus.NotReady;
            if (message.GalleryId != manifest.GalleryId) return RoundSyncStatus.WrongRound;
            if (!ValidVotingState(message)) return RoundSyncStatus.InvalidMessage;
            if (voting is not null)
            {
                if (message.Revision < voting.Revision) return RoundSyncStatus.Stale;
                // Same-revision retries may only refresh a shorter remaining time.
                if (message.Revision == voting.Revision)
                {
                    if (!SameState(voting, message)) return RoundSyncStatus.Conflict;
                    RefreshRemaining(message); return RoundSyncStatus.Duplicate;
                }
                if (voting.Result is not null || message.Deadline != voting.Deadline || message.AcceptedVotes < voting.AcceptedVotes
                    || (voting.SelectedArtworkId is not null && message.SelectedArtworkId != voting.SelectedArtworkId)) return RoundSyncStatus.Conflict;
            }
            if (pendingVote is not null && message.SelectedArtworkId is not null && message.SelectedArtworkId != pendingVote.ArtworkId)
                return RoundSyncStatus.Conflict;
            TimeSpan remaining = voting is null ? message.Remaining : Min(VotingRemaining(), message.Remaining);
            voting = message with { Remaining = remaining, Result = Freeze(message.Result) }; votingReceived = clock.GetTimestamp();
            if (message.SelectedArtworkId is not null || message.Result is not null) pendingVote = null;
            phase = message.Result is null ? RoundClientPhase.Voting : RoundClientPhase.Completed;
            return RoundSyncStatus.Accepted;
        }
    }

    public RoundSyncStatus AcceptGalleryStopped(string authenticatedSender, GalleryStoppedMessage message)
    {
        lock (sync)
        {
            Advance();
            if (authenticatedSender != hostId) return RoundSyncStatus.WrongSender;
            if (message is null || !Enum.IsDefined(message.Reason)) return RoundSyncStatus.InvalidMessage;
            if (message.MatchId != matchId || message.RoundId != roundId || message.GalleryVersion != galleryVersion) return RoundSyncStatus.WrongRound;
            if (message.RecipientId != localId) return RoundSyncStatus.WrongRecipient;
            if (manifest is null) return RoundSyncStatus.NotReady;
            if (manifest.GalleryId != message.GalleryId) return RoundSyncStatus.WrongRound;
            if (voting is not null || phase == RoundClientPhase.Disconnected) return RoundSyncStatus.Closed;
            if (phase == RoundClientPhase.Failed) return RoundSyncStatus.Duplicate;
            Fail("참가자의 그림 수신이 끝나지 않아 라운드가 중단되었습니다."); return RoundSyncStatus.Accepted;
        }
    }

    private bool Eligible(string id) => (manifest!.VotingPolicy.AllowVotersWithoutImage || manifest.Artworks.Any(a => a.Transfer.PlayerId == id))
        && manifest.Artworks.Any(a => a.Transfer.PlayerId != id);

    private bool ValidVotingState(VotingStateMessage state)
    {
        int eligible = players.Count(p => Eligible(p.Id));
        if (state.Revision < 1 || state.EligibleVoters != eligible || state.AcceptedVotes < 0 || state.AcceptedVotes > eligible
            || state.Remaining < TimeSpan.Zero || state.Remaining > manifest!.VotingPolicy.Duration) return false;
        if (state.SelectedArtworkId is not null && (!Eligible(localId) || state.AcceptedVotes == 0
            || !manifest.Artworks.Any(a => a.ArtworkId == state.SelectedArtworkId && a.Transfer.PlayerId != localId))) return false;
        if (state.CanVote != (state.Result is null && state.SelectedArtworkId is null && Eligible(localId))) return false;
        var result = state.Result;
        if (result is null) return manifest.Artworks.Count >= 2;
        if (state.Remaining != TimeSpan.Zero || result.MatchId != matchId || result.RoundId != roundId
            || result.VotingVersion != state.VotingVersion || result.Revision != state.Revision
            || result.AcceptedVotes != state.AcceptedVotes || result.EligibleVoters != eligible || result.MissingVotes != eligible - state.AcceptedVotes
            || !Enum.IsDefined(result.Reason) || result.Tallies is null || result.Tallies.Count != manifest.Artworks.Count
            || result.LeadingArtworkIds is null || result.LeadingArtworkIds.Count > manifest.Artworks.Count) return false;
        if ((manifest.Artworks.Count < 2) != (result.Reason == VotingEnd.InsufficientImages)) return false;
        if (result.Reason == VotingEnd.InsufficientImages && state.AcceptedVotes != 0) return false;
        if (result.Reason == VotingEnd.AllVoted && state.AcceptedVotes != eligible) return false;
        int sum = 0;
        for (int i = 0; i < result.Tallies.Count; i++)
        {
            var tally = result.Tallies[i]; var art = manifest.Artworks[i];
            if (tally is null || tally.ArtworkId != art.ArtworkId || tally.OwnerId != art.Transfer.PlayerId || tally.Votes < 0
                || tally.Votes > eligible - (Eligible(tally.OwnerId) ? 1 : 0)) return false;
            sum += tally.Votes;
        }
        if (sum != state.AcceptedVotes || (state.SelectedArtworkId is not null && !result.Tallies.Any(t => t.ArtworkId == state.SelectedArtworkId && t.Votes > 0))) return false;
        int most = result.Tallies.Count == 0 ? 0 : result.Tallies.Max(t => t.Votes);
        var leaders = most == 0 ? [] : result.Tallies.Where(t => t.Votes == most).Select(t => t.ArtworkId).ToArray();
        return leaders.SequenceEqual(result.LeadingArtworkIds);
    }

    private static VotingResult? Freeze(VotingResult? result) => result is null ? null : result with
    { Tallies = Array.AsReadOnly(result.Tallies.ToArray()), LeadingArtworkIds = Array.AsReadOnly(result.LeadingArtworkIds.ToArray()) };
    private static bool SameState(VotingStateMessage a, VotingStateMessage b) => RoundControlMessages.Encode(a with { Remaining = TimeSpan.Zero })
        .AsSpan().SequenceEqual(RoundControlMessages.Encode(b with { Remaining = TimeSpan.Zero }));
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
    private void RefreshRemaining(VotingStateMessage message)
    { voting = voting! with { Remaining = Min(VotingRemaining(), message.Remaining) }; votingReceived = clock.GetTimestamp(); }
    private TimeSpan VotingRemaining() => voting is null ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, (voting.Remaining - clock.GetElapsedTime(votingReceived)).Ticks));

    public VoteRequest CreateVote(string artworkId, string requestId)
    {
        lock (sync)
        {
            Advance(); VotingRoundHost.ValidateId(requestId);
            if (pendingVote is not null && pendingVote.ArtworkId == artworkId && pendingVote.RequestId == requestId
                && phase == RoundClientPhase.Voting && VotingRemaining() > TimeSpan.Zero) return pendingVote;
            if (phase != RoundClientPhase.Voting || voting is null || !voting.CanVote || VotingRemaining() == TimeSpan.Zero || pendingVote is not null)
                throw new InvalidOperationException("지금은 새 투표를 할 수 없습니다.");
            if (!pictures.Values.Any(a => a.Id == artworkId && a.OwnerId != localId)) throw new ArgumentException("다른 참가자의 그림 하나를 선택해 주세요.");
            pendingVote = new(matchId, roundId, galleryVersion + 1, requestId, localId, artworkId);
            return pendingVote;
        }
    }

    public RoundClientSnapshot Snapshot()
    {
        lock (sync)
        {
            Advance();
            var artworks = manifest is null ? [] : manifest.Artworks.Where(a => pictures.ContainsKey(a.Transfer.TransferId)).Select(a => pictures[a.Transfer.TransferId]).ToArray();
            return new(phase, Array.AsReadOnly(artworks), phase == RoundClientPhase.Voting && voting is { CanVote: true }
                && pendingVote is null && VotingRemaining() > TimeSpan.Zero, voting?.SelectedArtworkId, VotingRemaining(), voting?.Result, failure);
        }
    }

    public int BufferedImageBytes { get { lock (sync) { Advance(); return incoming.Values.Sum(i => i.BufferedBytes); } } }
    public void MarkHostUnavailable()
    {
        lock (sync)
        {
            Advance(); ClearIncoming(); pendingVote = null;
            phase = RoundClientPhase.Disconnected; failure = "방장 연결이 끊어졌습니다.";
            // Preserve confirmed results and already decoded images locally.
        }
    }
    private void Advance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (phase == RoundClientPhase.ReceivingGallery && clock.GetElapsedTime(galleryStarted) >= manifest!.DeliveryTimeout)
            Fail("그림 수신 제한 시간을 초과했습니다.");
    }
    private void ClearIncoming() { foreach (var receiver in incoming.Values) receiver.Discard(); incoming.Clear(); }
    private void Fail(string message) { ClearIncoming(); pictures.Clear(); phase = RoundClientPhase.Failed; failure = message; }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            ClearIncoming(); pictures.Clear(); voting = null; manifest = null; manifestBytes = null; pendingVote = null; disposed = true;
        }
    }
}
