using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIrtist.Core;

public sealed record GalleryArtwork(string ArtworkId, ImageTransferDescriptor Transfer);
public sealed record GalleryManifest(Guid MatchId, Guid RoundId, long GalleryVersion, Guid GalleryId, string HostId,
    IReadOnlyList<VotingPlayer> Players, IReadOnlyList<GalleryArtwork> Artworks,
    DateTimeOffset DeliveryDeadline, TimeSpan DeliveryTimeout, VotingPolicy VotingPolicy);
public sealed record GalleryReady(Guid MatchId, Guid RoundId, long GalleryVersion, Guid GalleryId, string PlayerId);
public enum GalleryStopReason { DeliveryTimedOut }
public sealed record GalleryStoppedMessage(Guid MatchId, Guid RoundId, long GalleryVersion, Guid GalleryId, string RecipientId, GalleryStopReason Reason);
public sealed record GalleryDeliverySnapshot(GalleryManifest Manifest, ReadOnlyCollection<string> ReadyPlayerIds,
    TimeSpan Remaining, bool TimedOut);
public enum GalleryReadyStatus { Accepted, Duplicate, UnknownPlayer, IdentityMismatch, WrongGallery, NotOpen, Closed }
public sealed record VotingStateMessage(Guid MatchId, Guid RoundId, Guid GalleryId, long VotingVersion, long Revision,
    string RecipientId, DateTimeOffset Deadline, TimeSpan Remaining, string? SelectedArtworkId, bool CanVote,
    int AcceptedVotes, int EligibleVoters, VotingResult? Result);

// Complete control messages, not EOS packets. A transport must impose this size
// limit before allocating/reassembling them, and fragment them separately if needed.
public static class RoundControlMessages
{
    public const int MaxBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private sealed record Envelope(int Protocol, string Kind, JsonElement Body);
    private static string Kind<T>() => typeof(T) == typeof(GalleryManifest) ? "gallery"
        : typeof(T) == typeof(GalleryReady) ? "gallery-ready"
        : typeof(T) == typeof(GalleryStoppedMessage) ? "gallery-stopped"
        : typeof(T) == typeof(VotingStateMessage) ? "voting-state"
        : typeof(T) == typeof(VoteRequest) ? "vote"
        : typeof(T) == typeof(RoundStartMessage) ? "round-start"
        : typeof(T) == typeof(SubmissionRequest) ? "submission"
        : typeof(T) == typeof(SubmissionReplyMessage) ? "submission-reply"
        : typeof(T) == typeof(ImageTransferDescriptor) ? "image-begin"
        : typeof(T) == typeof(ImageBeginReplyMessage) ? "image-begin-reply"
        : typeof(T) == typeof(ImageProgressRequest) ? "image-progress"
        : typeof(T) == typeof(ImageProgressReply) ? "image-progress-reply"
        : typeof(T) == typeof(GenerationFailedMessage) ? "generation-failed"
        : typeof(T) == typeof(GenerationFailureReply) ? "generation-failed-reply"
        : typeof(T) == typeof(RoundProgressMessage) ? "round-progress"
        : typeof(T) == typeof(MatchBeginRound) ? "match-begin-round"
        : typeof(T) == typeof(MatchRoundFinished) ? "match-round-finished"
        : typeof(T) == typeof(MatchHeartbeat) ? "match-heartbeat"
        : typeof(T) == typeof(MatchProgress) ? "match-progress"
        : typeof(T) == typeof(LobbyReadyRequest) ? "lobby-ready"
        : typeof(T) == typeof(LobbyReadyReply) ? "lobby-ready-reply"
        : typeof(T) == typeof(LobbyStateMessage) ? "lobby-state"
        : typeof(T) == typeof(LobbyStartOffer) ? "lobby-start"
        : typeof(T) == typeof(LobbyStartReceipt) ? "lobby-start-receipt"
        : typeof(T) == typeof(LobbyHeartbeat) ? "lobby-heartbeat"
        : typeof(T) == typeof(LobbyStopNotice) ? "lobby-stop"
        : throw new ArgumentException("지원하지 않는 제어 메시지입니다.");

    public static byte[] Encode<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(MultiplayerCompatibility.CurrentProtocol,
            Kind<T>(), JsonSerializer.SerializeToElement(value, Json)), Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("제어 메시지 크기 상한을 초과했습니다.");
        return bytes;
    }

    public static T Decode<T>(ReadOnlySpan<byte> bytes) where T : class
        => Decode(bytes) is T message ? message : throw new InvalidDataException("제어 메시지 종류가 다릅니다.");

    // Only this allowlist can create network message objects; no CLR type names
    // are accepted from the wire. Dispatchers still validate authority and context.
    public static object Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new InvalidDataException("잘못된 제어 메시지 크기입니다.");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json);
            if (envelope is null || envelope.Protocol != MultiplayerCompatibility.CurrentProtocol
                || envelope.Body.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("제어 메시지 종류·버전이 다릅니다.");
            Type type = envelope.Kind switch
            {
                "gallery" => typeof(GalleryManifest), "gallery-ready" => typeof(GalleryReady),
                "gallery-stopped" => typeof(GalleryStoppedMessage), "voting-state" => typeof(VotingStateMessage),
                "vote" => typeof(VoteRequest), "round-start" => typeof(RoundStartMessage),
                "submission" => typeof(SubmissionRequest), "submission-reply" => typeof(SubmissionReplyMessage),
                "image-begin" => typeof(ImageTransferDescriptor), "image-begin-reply" => typeof(ImageBeginReplyMessage),
                "image-progress" => typeof(ImageProgressRequest), "image-progress-reply" => typeof(ImageProgressReply),
                "generation-failed" => typeof(GenerationFailedMessage), "generation-failed-reply" => typeof(GenerationFailureReply),
                "round-progress" => typeof(RoundProgressMessage),
                "match-begin-round" => typeof(MatchBeginRound), "match-round-finished" => typeof(MatchRoundFinished),
                "match-progress" => typeof(MatchProgress),
                "match-heartbeat" => typeof(MatchHeartbeat),
                "lobby-ready" => typeof(LobbyReadyRequest), "lobby-ready-reply" => typeof(LobbyReadyReply),
                "lobby-state" => typeof(LobbyStateMessage), "lobby-start" => typeof(LobbyStartOffer),
                "lobby-start-receipt" => typeof(LobbyStartReceipt), "lobby-heartbeat" => typeof(LobbyHeartbeat),
                "lobby-stop" => typeof(LobbyStopNotice),
                _ => throw new InvalidDataException("지원하지 않는 제어 메시지입니다.")
            };
            return envelope.Body.Deserialize(type, Json) ?? throw new InvalidDataException("제어 메시지가 비어 있습니다.");
        }
        catch (JsonException ex) { throw new InvalidDataException("손상된 제어 메시지입니다.", ex); }
    }
}

// Owned by the round host under its lock; retains the existing immutable artworks,
// not another complete copy of every player's PNG.
internal sealed class GalleryDelivery
{
    private readonly Dictionary<Guid, VotingArtwork> pictures;
    private readonly HashSet<string> ready = new(StringComparer.Ordinal);
    private readonly long started;
    private readonly TimeProvider clock;
    public GalleryManifest Manifest { get; }
    public bool AllReady => ready.Count == Manifest.Players.Count;
    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (Manifest.DeliveryTimeout - clock.GetElapsedTime(started)).Ticks));

    public GalleryDelivery(Guid matchId, Guid roundId, long version, string hostId, VotingPlayer[] players,
        VotingArtwork[] artworks, TimeSpan timeout, VotingPolicy policy, TimeProvider clock)
    {
        this.clock = clock; started = clock.GetTimestamp(); pictures = new();
        var entries = artworks.Select(a =>
        {
            Guid transfer = Guid.NewGuid(); pictures.Add(transfer, a);
            return new GalleryArtwork(a.Id, new(matchId, roundId, version, Guid.ParseExact(a.Id, "N"),
                transfer, a.OwnerId, a.Bytes, a.Sha256));
        }).ToArray();
        Manifest = new(matchId, roundId, version, Guid.NewGuid(), hostId, Array.AsReadOnly((VotingPlayer[])players.Clone()),
            Array.AsReadOnly(entries), clock.GetUtcNow() + timeout, timeout, policy);
        ready.Add(hostId); // Host already validated and holds every image locally.
    }

    public GalleryReadyStatus Acknowledge(string sender, GalleryReady message)
    {
        if (!Manifest.Players.Any(p => p.Id == sender)) return GalleryReadyStatus.UnknownPlayer;
        if (message is null || message.MatchId != Manifest.MatchId || message.RoundId != Manifest.RoundId
            || message.GalleryVersion != Manifest.GalleryVersion || message.GalleryId != Manifest.GalleryId) return GalleryReadyStatus.WrongGallery;
        if (sender != message.PlayerId) return GalleryReadyStatus.IdentityMismatch;
        return ready.Add(sender) ? GalleryReadyStatus.Accepted : GalleryReadyStatus.Duplicate;
    }

    public byte[] CopyImage(Guid transferId) => pictures.TryGetValue(transferId, out var picture)
        ? picture.CopyPng() : throw new ArgumentException("갤러리 그림이 없습니다.");
    public GalleryDeliverySnapshot Snapshot(bool timedOut) => new(Manifest,
        Array.AsReadOnly(Manifest.Players.Where(p => ready.Contains(p.Id)).Select(p => p.Id).ToArray()), Remaining, timedOut);
}
