using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace AIrtist.Core;

public sealed record VotingPlayer(string Id, string Name);

// Construct only from the host's approved, complete submissions. Connection authentication
// and submission approval belong to the transport/match adapter, not to client payloads.
public sealed class VotingArtwork
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private readonly byte[] png;
    public string Id { get; }
    public string OwnerId { get; }
    public string Sha256 { get; }
    public BitmapSource Preview { get; }
    public int Bytes => png.Length;
    public byte[] CopyPng() => (byte[])png.Clone();

    public VotingArtwork(string id, string ownerId, byte[] bytes)
    {
        VotingRoundHost.ValidateId(id); VotingRoundHost.ValidateId(ownerId);
        if (bytes is null || bytes.Length < 33 || bytes.Length > MaxBytes)
            throw new ArgumentException("PNG 파일은 4MiB 이하여야 합니다.");
        png = (byte[])bytes.Clone();
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!png.AsSpan(0, 8).SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new ArgumentException("완성된 PNG 그림을 선택해 주세요.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width is 0 or > 1024 || height is 0 or > 1024)
            throw new ArgumentException("투표 그림은 가로·세로 1024픽셀 이하여야 합니다.");
        try
        {
            Preview = ImageCodec.Load(png);
            if (Preview.PixelWidth != width || Preview.PixelHeight != height) throw new InvalidDataException("PNG 크기가 일치하지 않습니다.");
            // Force pixel decoding before accepting the submission; headers alone are insufficient.
            int stride = checked((Preview.PixelWidth * Preview.Format.BitsPerPixel + 7) / 8);
            Preview.CopyPixels(new byte[checked(stride * Preview.PixelHeight)], stride, 0);
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or NotSupportedException or ArgumentException or InvalidOperationException)
        { throw new ArgumentException("손상되었거나 지원하지 않는 PNG 그림입니다.", ex); }
        Id = id; OwnerId = ownerId;
        Sha256 = Convert.ToHexString(SHA256.HashData(png));
    }
}

// Provisional rules, not final product decisions. No vote changes or revotes in this version.
public sealed record VotingPolicy(TimeSpan Duration, bool CloseWhenAllVoted, bool AllowVotersWithoutImage)
{
    public static VotingPolicy PreviewDefault => new(TimeSpan.FromSeconds(60), true, true);
}
public enum VotingEnd { AllVoted, Deadline, HostClosed, InsufficientImages }
public enum VoteStatus { Accepted, Duplicate, InvalidRequest, WrongRound, StaleVersion, UnknownVoter, IdentityMismatch, UnknownArtwork, SelfVote, Ineligible, AlreadyVoted, RequestConflict, Closed }
public sealed record VoteRequest(Guid MatchId, Guid RoundId, long VotingVersion, string RequestId, string VoterId, string ArtworkId);
public sealed record VoteReceipt(VoteStatus Status, long Revision);
public sealed record ArtworkTally(string ArtworkId, string OwnerId, int Votes);
public sealed record VotingResult(Guid MatchId, Guid RoundId, long VotingVersion, long Revision, VotingEnd Reason,
    DateTimeOffset ClosedAt, IReadOnlyList<ArtworkTally> Tallies, IReadOnlyList<string> LeadingArtworkIds,
    int AcceptedVotes, int EligibleVoters, int MissingVotes);
public sealed record VotingSnapshot(Guid MatchId, Guid RoundId, long VotingVersion, long Revision,
    ReadOnlyCollection<VotingPlayer> Players, ReadOnlyCollection<VotingArtwork> Artworks, DateTimeOffset Deadline,
    TimeSpan Remaining, string? SelectedArtworkId, bool CanVote, int AcceptedVotes, int EligibleVoters, VotingResult? Result);

public sealed class VotingRoundHost
{
    private readonly object sync = new();
    private readonly Guid matchId, roundId;
    private readonly long votingVersion, started;
    private readonly string hostId;
    private readonly TimeProvider clock;
    private readonly VotingPolicy policy;
    private readonly ReadOnlyCollection<VotingPlayer> players;
    private readonly ReadOnlyCollection<VotingArtwork> artworks;
    private readonly Dictionary<string, VotingPlayer> playerById;
    private readonly Dictionary<string, VotingArtwork> artworkById;
    private readonly HashSet<string> eligible;
    private readonly Dictionary<string, VoteRequest> votes = new(StringComparer.Ordinal);
    private readonly DateTimeOffset deadline;
    private long revision = 1;
    private VotingResult? result;

    public VotingRoundHost(Guid matchId, Guid roundId, long votingVersion, string hostId,
        IEnumerable<VotingPlayer> players, IEnumerable<VotingArtwork> artworks, VotingPolicy? policy = null, TimeProvider? clock = null)
    {
        if (matchId == Guid.Empty || roundId == Guid.Empty || votingVersion < 1) throw new ArgumentException("경기·라운드·상태 버전을 확인해 주세요.");
        this.matchId = matchId; this.roundId = roundId; this.votingVersion = votingVersion; this.hostId = hostId;
        this.policy = policy ?? VotingPolicy.PreviewDefault;
        if (this.policy.Duration < TimeSpan.FromSeconds(1) || this.policy.Duration > TimeSpan.FromMinutes(10))
            throw new ArgumentException("투표 시간은 1초~10분이어야 합니다.");
        this.clock = clock ?? TimeProvider.System;
        var roster = players.Take(17).ToArray();
        if (roster.Length is < 1 or > 16) throw new ArgumentException("현재 투표 처리 상한은 16명입니다.");
        playerById = new(StringComparer.Ordinal);
        foreach (var player in roster)
        {
            if (player is null) throw new ArgumentException("참가자가 없습니다.");
            ValidateId(player.Id);
            if (string.IsNullOrWhiteSpace(player.Name) || player.Name.Length > 40 || !playerById.TryAdd(player.Id, player))
                throw new ArgumentException("참가자 ID·이름을 확인해 주세요.");
        }
        if (!playerById.ContainsKey(hostId)) throw new ArgumentException("방장은 참가자여야 합니다.");
        var pictures = artworks.Take(17).ToArray();
        artworkById = new(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var picture in pictures)
        {
            if (picture is null || !playerById.ContainsKey(picture.OwnerId) || !owners.Add(picture.OwnerId) || !artworkById.TryAdd(picture.Id, picture))
                throw new ArgumentException("그림 ID·소유자·중복 제출을 확인해 주세요.");
            bytes += picture.Bytes;
        }
        if (pictures.Length > 16 || bytes > 16 * 1024 * 1024) throw new ArgumentException("한 라운드의 그림 저장 상한을 초과했습니다.");
        this.players = Array.AsReadOnly(roster); this.artworks = Array.AsReadOnly(pictures);
        eligible = roster.Where(p => (this.policy.AllowVotersWithoutImage || owners.Contains(p.Id)) && pictures.Any(a => a.OwnerId != p.Id))
            .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        started = this.clock.GetTimestamp(); deadline = this.clock.GetUtcNow() + this.policy.Duration;
        if (pictures.Length < 2) Finish(VotingEnd.InsufficientImages);
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || id != id.Trim()) throw new ArgumentException("잘못된 ID입니다.");
    }

    // authenticatedPlayerId must come from a trusted connection, never from request.VoterId.
    public VoteReceipt Vote(string authenticatedPlayerId, VoteRequest request)
    {
        lock (sync)
        {
            Expire();
            VoteReceipt Reply(VoteStatus status) => new(status, revision);
            if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80) return Reply(VoteStatus.InvalidRequest);
            if (request.MatchId != matchId || request.RoundId != roundId) return Reply(VoteStatus.WrongRound);
            if (request.VotingVersion != votingVersion) return Reply(VoteStatus.StaleVersion);
            if (authenticatedPlayerId is null || !playerById.ContainsKey(authenticatedPlayerId)) return Reply(VoteStatus.UnknownVoter);
            if (authenticatedPlayerId != request.VoterId) return Reply(VoteStatus.IdentityMismatch);
            if (votes.TryGetValue(authenticatedPlayerId, out var previous) && previous.RequestId == request.RequestId)
                return Reply(previous == request ? VoteStatus.Duplicate : VoteStatus.RequestConflict);
            // A previously accepted request may be acknowledged again after closure; no new vote is counted.
            if (result is not null) return Reply(VoteStatus.Closed);
            if (request.ArtworkId is null || !artworkById.TryGetValue(request.ArtworkId, out var picture)) return Reply(VoteStatus.UnknownArtwork);
            if (picture.OwnerId == authenticatedPlayerId) return Reply(VoteStatus.SelfVote);
            if (!eligible.Contains(authenticatedPlayerId)) return Reply(VoteStatus.Ineligible);
            if (votes.ContainsKey(authenticatedPlayerId)) return Reply(VoteStatus.AlreadyVoted);
            votes.Add(authenticatedPlayerId, request); revision++;
            if (policy.CloseWhenAllVoted && votes.Count == eligible.Count) Finish(VotingEnd.AllVoted);
            return Reply(VoteStatus.Accepted);
        }
    }

    public VotingSnapshot Snapshot(string viewerId)
    {
        lock (sync)
        {
            if (!playerById.ContainsKey(viewerId)) throw new ArgumentException("알 수 없는 참가자입니다.");
            Expire();
            votes.TryGetValue(viewerId, out var own);
            return new(matchId, roundId, votingVersion, revision, players, artworks, deadline,
                result is null ? Remaining() : TimeSpan.Zero, own?.ArtworkId,
                result is null && eligible.Contains(viewerId) && own is null, votes.Count, eligible.Count, result);
        }
    }

    public VotingResult Close(string authenticatedHostId)
    {
        lock (sync)
        {
            if (authenticatedHostId != hostId) throw new UnauthorizedAccessException("방장만 투표를 마감할 수 있습니다.");
            Expire(); Finish(VotingEnd.HostClosed); return result!;
        }
    }
    private TimeSpan Remaining() => TimeSpan.FromTicks(Math.Max(0, (policy.Duration - clock.GetElapsedTime(started)).Ticks));
    private void Expire() { if (result is null && Remaining() == TimeSpan.Zero) Finish(VotingEnd.Deadline); }
    private void Finish(VotingEnd reason)
    {
        if (result is not null) return;
        revision++;
        var counts = artworks.Select(a => new ArtworkTally(a.Id, a.OwnerId, votes.Values.Count(v => v.ArtworkId == a.Id))).ToArray();
        int most = counts.Length == 0 ? 0 : counts.Max(t => t.Votes);
        var leaders = most == 0 ? [] : counts.Where(t => t.Votes == most).Select(t => t.ArtworkId).ToArray();
        result = new(matchId, roundId, votingVersion, revision, reason, clock.GetUtcNow(), Array.AsReadOnly(counts),
            Array.AsReadOnly(leaders), votes.Count, eligible.Count, eligible.Count - votes.Count);
    }
}
