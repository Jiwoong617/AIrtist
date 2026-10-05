using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AIrtist.Core;

// These are application contracts, independent of EOS SDK types. Peer identities must
// come from the transport's authenticated connection, never from a received payload.
public sealed record MultiplayerCompatibility(int ProtocolVersion, string RulesVersion, string AssetsSha256, string WordsSha256)
{
    public const int CurrentProtocol = 1;
    public const string CurrentRules = "multiplayer-match-v6";

    // Call only after RuntimeAssets.Verify; a manifest fingerprint alone does not verify models.
    public static MultiplayerCompatibility FromVerifiedAssets(RuntimeAssets assets, string wordsPath)
    {
        static string Hash(string path)
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file));
        }
        return new(CurrentProtocol, CurrentRules, Hash(assets.File("manifest.json")), Hash(wordsPath));
    }

    public void Validate()
    {
        if (ProtocolVersion != CurrentProtocol || RulesVersion != CurrentRules || !IsHash(AssetsSha256) || !IsHash(WordsSha256))
            throw new ArgumentException("통신·규칙·모델·단어 목록 버전을 확인해 주세요.");
    }

    // Stable discovery/connection fingerprint. File verification must precede FromVerifiedAssets.
    public string ConnectionHash()
    {
        Validate();
        string value = "AIrtist-connect-v1\n" + ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            RulesVersion + "\n" + AssetsSha256.ToLowerInvariant() + "\n" + WordsSha256.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    public bool Matches(MultiplayerCompatibility? other) => other is not null
        && ProtocolVersion == other.ProtocolVersion && RulesVersion == other.RulesVersion
        && string.Equals(AssetsSha256, other.AssetsSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(WordsSha256, other.WordsSha256, StringComparison.OrdinalIgnoreCase);

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record MultiplayerParticipant(VotingPlayer Player, MultiplayerCompatibility Compatibility);
public sealed record SubmissionRequest(Guid MatchId, Guid RoundId, long SubmissionVersion, string RequestId,
    string PlayerId, string Description, string Translation);
// No word or answer is included in the generation input handed to a player.
public sealed record GenerationApproval(Guid MatchId, Guid RoundId, long SubmissionVersion, Guid ApprovalId,
    string PlayerId, string Description, string Translation, DateTimeOffset GenerationDeadline);
public sealed record ImageTransferDescriptor(Guid MatchId, Guid RoundId, long SubmissionVersion, Guid ApprovalId,
    Guid TransferId, string PlayerId, int TotalBytes, string Sha256);

public enum ImagePacketStatus { Accepted, Duplicate, Complete, InvalidPacket, ConflictingPacket, HashMismatch }

// 1 KiB application packets including the header. The EOS adapter must additionally
// check its selected SDK's packet limit and send-queue/backpressure behavior.
public static class ImagePackets
{
    public const int MaxPacketBytes = 1024;
    public const int HeaderBytes = 32;
    public const int PayloadBytes = MaxPacketBytes - HeaderBytes;
    public const int MaxChunks = (VotingArtwork.MaxBytes + PayloadBytes - 1) / PayloadBytes;
    private const uint Magic = 0x31474D49; // IMG1, little endian

    public static ImageTransferDescriptor Describe(GenerationApproval approval, Guid transferId, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (transferId == Guid.Empty || png is null || png.Length is < 33 or > VotingArtwork.MaxBytes)
            throw new ArgumentException("그림 전송 ID·크기를 확인해 주세요.");
        return new(approval.MatchId, approval.RoundId, approval.SubmissionVersion, approval.ApprovalId,
            transferId, approval.PlayerId, png.Length, Convert.ToHexString(SHA256.HashData(png)));
    }

    public static IEnumerable<byte[]> Split(ImageTransferDescriptor descriptor, byte[] png)
    {
        Validate(descriptor);
        if (png is null || png.Length != descriptor.TotalBytes
            || !Convert.ToHexString(SHA256.HashData(png)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("전송할 그림과 전송 정보가 다릅니다.");
        int count = Count(png.Length);
        for (int index = 0; index < count; index++)
            yield return Create(descriptor, png, index);
    }

    internal static byte[] Create(ImageTransferDescriptor descriptor, byte[] png, int index)
    {
        int offset = index * PayloadBytes, length = Math.Min(PayloadBytes, png.Length - offset);
        var packet = new byte[HeaderBytes + length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), MultiplayerCompatibility.CurrentProtocol);
        descriptor.TransferId.TryWriteBytes(packet.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), index);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28, 4), Count(png.Length));
        png.AsSpan(offset, length).CopyTo(packet.AsSpan(HeaderBytes)); return packet;
    }

    internal static int Count(int bytes) => (bytes + PayloadBytes - 1) / PayloadBytes;
    internal static void Validate(ImageTransferDescriptor descriptor)
    {
        if (descriptor is null || descriptor.MatchId == Guid.Empty || descriptor.RoundId == Guid.Empty
            || descriptor.ApprovalId == Guid.Empty || descriptor.TransferId == Guid.Empty || descriptor.SubmissionVersion < 1
            || descriptor.TotalBytes is < 33 or > VotingArtwork.MaxBytes || !MultiplayerCompatibility.IsHash(descriptor.Sha256))
            throw new ArgumentException("잘못된 그림 전송 정보입니다.");
        VotingRoundHost.ValidateId(descriptor.PlayerId);
    }

    internal static bool ReadHeader(ReadOnlySpan<byte> packet, out Guid transferId, out int index, out int count)
    {
        transferId = default; index = count = 0;
        if (packet.Length is <= HeaderBytes or > MaxPacketBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[..4]) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(4, 4)) != MultiplayerCompatibility.CurrentProtocol) return false;
        transferId = new Guid(packet.Slice(8, 16));
        index = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(24, 4));
        count = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(28, 4));
        return transferId != Guid.Empty && count is > 0 and <= MaxChunks && index >= 0 && index < count;
    }
}

// Owned and serialized by MultiplayerRoundHost. One allocation per approved player;
// repeated or reordered packets cannot extend its absolute receive deadline.
internal sealed class IncomingImage(ImageTransferDescriptor descriptor, long started)
{
    private byte[]? bytes = new byte[descriptor.TotalBytes];
    private readonly bool[] received = new bool[ImagePackets.Count(descriptor.TotalBytes)];
    private int receivedCount;
    public ImageTransferDescriptor Descriptor { get; } = descriptor;
    public long Started { get; } = started;
    public int BufferedBytes => bytes?.Length ?? 0;

    public ImagePacketStatus Add(ReadOnlySpan<byte> packet)
    {
        if (bytes is null || !ImagePackets.ReadHeader(packet, out var transferId, out int index, out int count)
            || transferId != Descriptor.TransferId || count != received.Length) return ImagePacketStatus.InvalidPacket;
        int offset = index * ImagePackets.PayloadBytes;
        int expected = Math.Min(ImagePackets.PayloadBytes, bytes.Length - offset);
        if (packet.Length != ImagePackets.HeaderBytes + expected) return ImagePacketStatus.InvalidPacket;
        var payload = packet[ImagePackets.HeaderBytes..];
        var target = bytes.AsSpan(offset, expected);
        if (received[index]) return target.SequenceEqual(payload) ? ImagePacketStatus.Duplicate : ImagePacketStatus.ConflictingPacket;
        payload.CopyTo(target); received[index] = true; receivedCount++;
        if (receivedCount != received.Length) return ImagePacketStatus.Accepted;
        return Convert.ToHexString(SHA256.HashData(bytes)).Equals(Descriptor.Sha256, StringComparison.OrdinalIgnoreCase)
            ? ImagePacketStatus.Complete : ImagePacketStatus.HashMismatch;
    }

    public byte[] TakeCompleted()
    {
        if (bytes is null || receivedCount != received.Length) throw new InvalidOperationException("그림 전송이 끝나지 않았습니다.");
        var result = bytes; bytes = null; return result;
    }
    public void Discard() => bytes = null;
    public byte[] ReceivedChunks()
    {
        var bits = new byte[(received.Length + 7) / 8];
        for (int i = 0; i < received.Length; i++) if (received[i]) bits[i / 8] |= (byte)(1 << (i % 8));
        return bits;
    }
}
