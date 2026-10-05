using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class MultiplayerChecks
{
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static readonly Word Word = new("apple", "사과", "과일", "apple", ["사과", "apple"]);
    private static readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
    private static readonly MultiplayerRoundPolicy Policy = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), VotingPolicy.PreviewDefault);
    private static MultiplayerParticipant[] Players(int count = 3) => Enumerable.Range(1, count)
        .Select(i => new MultiplayerParticipant(new("p" + i, "참가자 " + i), Compatibility)).ToArray();
    private static MultiplayerRoundHost Host(ManualClock? clock = null, int count = 3, Guid? round = null)
        => new(Match, round ?? Round, 4, "p1", Word, Compatibility, Players(count), Policy, clock);
    private static SubmissionRequest Request(string player, string id = "submit") => new(Match, Round, 4, id, player, "아삭아삭한 빨간 과일", "a crunchy red fruit");
    private static GenerationApproval Approve(MultiplayerRoundHost host, string player)
    {
        var reply = host.Submit(player, Request(player));
        Require(reply.Status == SubmissionStatus.Accepted && reply.Approval is not null, "Approval failed");
        return reply.Approval!;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void AcknowledgeAll(MultiplayerRoundHost host)
    {
        var gallery = host.Snapshot("p1").Gallery!.Manifest;
        foreach (var p in gallery.Players)
            host.AcknowledgeGallery(p.Id, new(gallery.MatchId, gallery.RoundId, gallery.GalleryVersion, gallery.GalleryId, p.Id));
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException) { return; }
        throw new InvalidOperationException("Invalid multiplayer operation accepted");
    }
    private static T Wire<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, RuntimeAssets.Json), RuntimeAssets.Json)!;
    private static byte[] Png(int seed)
    {
        var pixels = new byte[64 * 64 * 3]; new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    public static void Run()
    {
        SubmissionChecks(); TransferChecks(); DeadlineChecks(); CapacityChecks(); FailureVotingChecks();
        var report = Play([Png(1), Png(2), Png(3)]);
        Require(report.ResultsAgree && report.PacketCount > 3, "Round scenario failed");
        Console.WriteLine("Multiplayer: authenticated approval, alias rejection, version compatibility, bounded/reordered/duplicate/corrupt PNG packets, deadline cleanup, 3-player round through voting passed (in-memory transport, not EOS)");
    }

    private static void SubmissionChecks()
    {
        using var host = Host();
        Require(host.Submit("p1", Request("p2")).Status == SubmissionStatus.IdentityMismatch, "Spoofed submission");
        Require(host.Submit("stranger", Request("stranger")).Status == SubmissionStatus.UnknownPlayer, "Unknown submitter");
        Require(host.Submit("p1", Request("p1") with { MatchId = Guid.NewGuid() }).Status == SubmissionStatus.WrongRound, "Old match");
        Require(host.Submit("p1", Request("p1") with { RoundId = Guid.NewGuid() }).Status == SubmissionStatus.WrongRound, "Old round");
        Require(host.Submit("p1", Request("p1") with { SubmissionVersion = 3 }).Status == SubmissionStatus.StaleVersion, "Old phase");
        foreach (var bad in new[] { Request("p1") with { Description = "사 과를 그려줘" },
            Request("p1") with { Translation = "a red ＡＰＰＬＥ" }, Request("p1") with { Description = "" },
            Request("p1") with { Translation = "한글 번역" }, Request("p1") with { Translation = new('x', 321) },
            Request("p1") with { RequestId = "" }, Request("p1") with { Description = null! } })
            Require(host.Submit("p1", bad).Status == SubmissionStatus.InvalidRequest, "Bad description/translation consumed approval");
        Require(host.Snapshot("p1").OwnApproval is null, "Invalid input consumed generation");
        var approval = Approve(host, "p1");
        Require(host.Submit("p1", Wire(Request("p1"))) is { Status: SubmissionStatus.Duplicate } repeat && repeat.Approval == approval, "Approval replay changed token");
        Require(host.Submit("p1", Request("p1") with { Translation = "a green fruit" }).Status == SubmissionStatus.RequestConflict, "Changed confirmed description");
        Require(host.Submit("p1", Request("p1", "second")).Status == SubmissionStatus.AlreadyApproved, "Second generation approved");
        Require(host.Snapshot("p2").OwnApproval is null, "Approval belongs to wrong viewer");
        Require(!JsonSerializer.Serialize(approval).Contains("사과", StringComparison.Ordinal), "Answer leaked into approval");
        var replies = new System.Collections.Concurrent.ConcurrentBag<SubmissionReceipt>();
        Parallel.For(0, 20, i => replies.Add(host.Submit("p2", Request("p2", "parallel-" + i))));
        Require(replies.Count(r => r.Status == SubmissionStatus.Accepted) == 1, "Concurrent generations approved");
        Reject(() => new MultiplayerRoundHost(Match, Round, 4, "p1", Word, Compatibility,
            [Players()[0], Players()[1] with { Compatibility = Compatibility with { WordsSha256 = new('C', 64) } }]));
        Reject(() => new MultiplayerRoundHost(Match, Round, 4, "p1", Word, Compatibility,
            [Players()[0], Players()[1] with { Compatibility = Compatibility with { AssetsSha256 = new('C', 64) } }]));
        Reject(() => new MultiplayerRoundHost(Match, Round, 4, "p1", Word, Compatibility,
            [Players()[0], Players()[1] with { Compatibility = Compatibility with { ProtocolVersion = 2 } }]));
        Reject(() => new MultiplayerRoundHost(Match, Round, 4, "p1", Word, Compatibility,
            [Players()[0], Players()[1] with { Compatibility = Compatibility with { RulesVersion = "other" } }]));
        Reject(() => Host(count: 17)); Reject(() => Host(count: 1));
        Reject(() => new MultiplayerRoundHost(Match, Round, 4, "p1", Word, Compatibility, [Players()[0], Players()[0]]));
        Require(host.Vote("p1", new(Match, Round, 5, "early", "p1", "missing")).Status == VoteStatus.Closed, "Vote before gallery");
        Reject(() => host.CloseVoting("p1"));
    }

    private static void TransferChecks()
    {
        var png = Png(4);
        using var host = Host();
        var approval = Approve(host, "p1");
        var descriptor = ImagePackets.Describe(approval, Guid.NewGuid(), png);
        var packets = ImagePackets.Split(descriptor, png).ToArray();
        Require(packets.All(p => p.Length <= ImagePackets.MaxPacketBytes), "Packet exceeds limit");
        Require(host.BeginImage("p2", descriptor).Equals(ImageTransferStatus.IdentityMismatch), "Spoofed image owner");
        Require(host.BeginImage("stranger", descriptor).Equals(ImageTransferStatus.UnknownPlayer), "Unknown image sender");
        Require(host.BeginImage("p2", descriptor with { PlayerId = "p2" }) == ImageTransferStatus.NotApproved, "Unapproved image");
        Require(host.BeginImage("p1", descriptor with { RoundId = Guid.NewGuid() }) == ImageTransferStatus.WrongRound, "Old image");
        Require(host.BeginImage("p1", descriptor with { SubmissionVersion = 2 }) == ImageTransferStatus.StaleVersion, "Stale image");
        Require(host.BeginImage("p1", descriptor with { ApprovalId = Guid.NewGuid() }) == ImageTransferStatus.NotApproved, "Wrong approval");
        foreach (var bad in new[] { descriptor with { TotalBytes = VotingArtwork.MaxBytes + 1 }, descriptor with { TotalBytes = -1 },
            descriptor with { Sha256 = "bad" }, descriptor with { TransferId = Guid.Empty } })
            Require(host.BeginImage("p1", bad) == ImageTransferStatus.InvalidRequest && host.BufferedImageBytes == 0, "Invalid descriptor allocated memory");
        Require(host.BeginImage("p1", Wire(descriptor)) == ImageTransferStatus.Accepted, "Transfer start failed");
        Require(host.BeginImage("p1", Wire(descriptor)) == ImageTransferStatus.Duplicate, "Transfer replay allocated");
        Require(host.BeginImage("p1", descriptor with { TransferId = Guid.NewGuid() }) == ImageTransferStatus.TransferConflict, "Replaced transfer");
        Require(host.ReceiveImagePacket("p2", packets[0]) == ImageTransferStatus.NotApproved, "Another peer supplied approved bytes");
        Require(host.ReceiveImagePacket("p1", [1, 2, 3]) == ImageTransferStatus.InvalidRequest, "Short packet");
        var oversized = new byte[1025]; Require(host.ReceiveImagePacket("p1", oversized) == ImageTransferStatus.InvalidRequest, "Large packet");
        var badCount = (byte[])packets[0].Clone(); BinaryPrimitives.WriteInt32LittleEndian(badCount.AsSpan(28, 4), int.MaxValue);
        Require(host.ReceiveImagePacket("p1", badCount) == ImageTransferStatus.InvalidRequest, "Unbounded chunk count");
        var badIndex = (byte[])packets[0].Clone(); BinaryPrimitives.WriteInt32LittleEndian(badIndex.AsSpan(24, 4), -1);
        Require(host.ReceiveImagePacket("p1", badIndex) == ImageTransferStatus.InvalidRequest, "Negative chunk index");
        var badVersion = (byte[])packets[0].Clone(); badVersion[4] = 2;
        Require(host.ReceiveImagePacket("p1", badVersion) == ImageTransferStatus.InvalidRequest, "Wrong packet version");
        Require(host.ReceiveImagePacket("p1", packets[0][..^1]) == ImageTransferStatus.InvalidRequest, "Truncated chunk accepted");
        Require(host.ReceiveImagePacket("p1", packets[0]) == ImageTransferStatus.Accepted, "First packet failed");
        Require(host.ReceiveImagePacket("p1", packets[0]) == ImageTransferStatus.Duplicate && host.BufferedImageBytes == png.Length, "Duplicate chunk grows memory");
        var conflict = (byte[])packets[0].Clone(); conflict[^1] ^= 1;
        Require(host.ReceiveImagePacket("p1", conflict) == ImageTransferStatus.CorruptImage, "Conflicting duplicate accepted");
        Require(host.BufferedImageBytes == 0 && host.ReservedImageBytes == 0, "Corrupt transfer retained buffer");
        Require(host.BeginImage("p1", descriptor) == ImageTransferStatus.Closed, "Failed transfer restarted");

        using var corrupt = Host();
        var corruptDesc = ImagePackets.Describe(Approve(corrupt, "p1"), Guid.NewGuid(), png);
        corrupt.BeginImage("p1", corruptDesc);
        var corruptPackets = ImagePackets.Split(corruptDesc, png).ToArray(); corruptPackets[^1][^1] ^= 1;
        ImageTransferStatus last = default;
        foreach (var p in corruptPackets) last = corrupt.ReceiveImagePacket("p1", p);
        Require(last == ImageTransferStatus.CorruptImage && corrupt.ReservedImageBytes == 0, "SHA mismatch accepted");

        using var invalidPng = Host();
        var junk = new byte[2048]; var junkDesc = ImagePackets.Describe(Approve(invalidPng, "p1"), Guid.NewGuid(), junk);
        invalidPng.BeginImage("p1", junkDesc);
        foreach (var p in ImagePackets.Split(junkDesc, junk)) last = invalidPng.ReceiveImagePacket("p1", p);
        Require(last == ImageTransferStatus.CorruptImage && invalidPng.ReservedImageBytes == 0, "Hash-valid non-PNG accepted");
        using var nextRound = Host(round: Guid.NewGuid());
        Require(nextRound.ReceiveImagePacket("p1", packets[0]) == ImageTransferStatus.NotApproved, "Previous round packet reused");
    }

    private static void DeadlineChecks()
    {
        var clock = new ManualClock();
        using var host = Host(clock);
        clock.Advance(TimeSpan.FromMilliseconds(9999)); var approval = Approve(host, "p1");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(host.Submit("p2", Request("p2")).Status == SubmissionStatus.Closed, "Writing deadline boundary");
        Require(host.Snapshot("p1").Phase == MultiplayerRoundPhase.Collecting, "Approved generator lost remaining budget");
        clock.Advance(TimeSpan.FromMilliseconds(19998));
        var png = Png(6); var descriptor = ImagePackets.Describe(approval, Guid.NewGuid(), png);
        Require(host.BeginImage("p1", descriptor) == ImageTransferStatus.Accepted, "Image before generation deadline rejected");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Require(host.ReceiveImagePacket("p1", ImagePackets.Split(descriptor, png).First()) == ImageTransferStatus.Closed, "Image at generation deadline accepted");
        Require(host.Snapshot("p1").Voting?.Result is { Reason: VotingEnd.InsufficientImages }
            && host.BufferedImageBytes == 0 && host.ReservedImageBytes == 0, "Deadline did not close/clean round");
        Require(host.Submit("p1", Request("p1")).Status == SubmissionStatus.Duplicate, "Timeout lost approved receipt");
        Require(host.Submit("p1", Request("p1", "retry")).Status == SubmissionStatus.AlreadyApproved, "Timeout restored generation right");

        var receiveClock = new ManualClock();
        using var timeout = Host(receiveClock);
        var transfer = ImagePackets.Describe(Approve(timeout, "p1"), Guid.NewGuid(), png);
        timeout.BeginImage("p1", transfer); var packets = ImagePackets.Split(transfer, png).ToArray();
        timeout.ReceiveImagePacket("p1", packets[0]);
        receiveClock.Advance(TimeSpan.FromMilliseconds(4999));
        Require(timeout.ReceiveImagePacket("p1", packets[0]) == ImageTransferStatus.Duplicate, "Valid retry rejected");
        receiveClock.Advance(TimeSpan.FromMilliseconds(1));
        Require(timeout.ReceiveImagePacket("p1", packets[1]) == ImageTransferStatus.Closed
            && timeout.Snapshot("p1").Players[0].State == PlayerRoundState.TransferTimedOut
            && timeout.BufferedImageBytes == 0, "Duplicate reset absolute receive timeout");
        using var failed = Host(); var a = Approve(failed, "p1");
        Require(failed.ReportGenerationFailure("p2", a.ApprovalId) == ImageTransferStatus.NotApproved, "Spoofed failure");
        Require(failed.ReportGenerationFailure("p1", a.ApprovalId) == ImageTransferStatus.Accepted
            && failed.ReportGenerationFailure("p1", a.ApprovalId) == ImageTransferStatus.Duplicate, "Failure not idempotent");
        failed.Dispose(); Reject(() => failed.Snapshot("p1"));
        Reject(() => failed.BeginImage("p1", transfer));
    }

    private static void CapacityChecks()
    {
        using var host = Host(count: 5);
        var approvals = Enumerable.Range(1, 5).Select(i => Approve(host, "p" + i)).ToArray();
        var descriptors = approvals.Select(a => new ImageTransferDescriptor(Match, Round, 4, a.ApprovalId,
            Guid.NewGuid(), a.PlayerId, VotingArtwork.MaxBytes, new('A', 64))).ToArray();
        foreach (var d in descriptors.Take(4)) Require(host.BeginImage(d.PlayerId, d) == ImageTransferStatus.Accepted, "Within-bound allocation failed");
        Require(host.BufferedImageBytes == MultiplayerRoundHost.MaxRoundImageBytes
            && host.BeginImage("p5", descriptors[4]) == ImageTransferStatus.CapacityExceeded, "Total memory bound ignored");
        host.ReportGenerationFailure("p1", approvals[0].ApprovalId);
        Require(host.BeginImage("p5", descriptors[4] with { TransferId = descriptors[0].TransferId }) == ImageTransferStatus.TransferConflict, "Reused transfer identity");
        Require(host.BeginImage("p5", descriptors[4]) == ImageTransferStatus.Accepted
            && host.ReservedImageBytes == MultiplayerRoundHost.MaxRoundImageBytes, "Failed allocation not released");
    }

    private static void FailureVotingChecks()
    {
        static void FinishImage(MultiplayerRoundHost host, GenerationApproval approval, int seed)
        {
            var png = Png(seed); var descriptor = ImagePackets.Describe(approval, Guid.NewGuid(), png);
            Require(host.BeginImage(approval.PlayerId, descriptor) == ImageTransferStatus.Accepted, "Image start failed");
            foreach (var packet in ImagePackets.Split(descriptor, png)) host.ReceiveImagePacket(approval.PlayerId, packet);
        }
        using var host = Host();
        FinishImage(host, Approve(host, "p1"), 10); FinishImage(host, Approve(host, "p2"), 11);
        Require(host.Snapshot("p1").Phase == MultiplayerRoundPhase.Collecting, "Unfinished player skipped");
        var failed = Approve(host, "p3"); host.ReportGenerationFailure("p3", failed.ApprovalId);
        AcknowledgeAll(host);
        var ballot = host.Snapshot("p3").Voting!;
        Require(ballot.Artworks.Count == 2 && ballot.CanVote, "Failed generator lost provisional voting eligibility");
        Require(host.Vote("p3", new(Match, Round, ballot.VotingVersion, "vote", "p3", ballot.Artworks[0].Id)).Status == VoteStatus.Accepted, "Failed generator cannot vote");
        using var two = Host(count: 2);
        FinishImage(two, Approve(two, "p1"), 12); FinishImage(two, Approve(two, "p2"), 13);
        AcknowledgeAll(two);
        var pair = two.Snapshot("p1").Voting!;
        two.Vote("p1", new(Match, Round, pair.VotingVersion, "vote", "p1", pair.Artworks[1].Id));
        two.Vote("p2", new(Match, Round, pair.VotingVersion, "vote", "p2", pair.Artworks[0].Id));
        Require(two.Snapshot("p1").Voting?.Result is { LeadingArtworkIds.Count: 2 }, "Two-player round tie changed");
    }

    private sealed record RoundReport(bool ResultsAgree, int PacketCount, int ImageBytes, string[] Hashes, VotingResult Result);
    private static RoundReport Play(byte[][] pngs)
    {
        using var host = Host();
        var approvals = Enumerable.Range(1, 3).Select(i => Wire(Approve(host, "p" + i))).ToArray();
        var hashes = new List<string>(); int packetCount = 0;
        for (int i = 0; i < 3; i++)
        {
            string peer = "p" + (i + 1);
            var descriptor = Wire(ImagePackets.Describe(approvals[i], Guid.NewGuid(), pngs[i]));
            hashes.Add(descriptor.Sha256);
            Require(host.BeginImage(peer, descriptor) == ImageTransferStatus.Accepted, "Image begin failed");
            var packets = ImagePackets.Split(descriptor, pngs[i]).Reverse().ToArray(); packetCount += packets.Length;
            for (int n = 0; n < packets.Length; n++)
            {
                var status = host.ReceiveImagePacket(peer, (byte[])packets[n].Clone());
                Require(status == (n == packets.Length - 1 ? ImageTransferStatus.Completed : ImageTransferStatus.Accepted), "Reordered image failed");
                Require(host.ReceiveImagePacket(peer, packets[n]) == ImageTransferStatus.Duplicate, "Chunk replay failed");
            }
            Require(host.BeginImage(peer, descriptor) == ImageTransferStatus.Duplicate, "Completed descriptor replay failed");
        }
        Require(host.BufferedImageBytes == 0 && host.ReservedImageBytes == pngs.Sum(p => p.Length), "Completed image memory accounting");
        AcknowledgeAll(host);
        var ballot = host.Snapshot("p1").Voting!;
        Require(ballot.Artworks.Count == 3 && hashes.SequenceEqual(ballot.Artworks.Select(a => a.Sha256)), "Actual image bytes changed");
        for (int i = 0; i < 3; i++) Require(ballot.Artworks[i].CopyPng().SequenceEqual(pngs[i]), "PNG reassembled incorrectly");
        VoteRequest Vote(string voter, int target, string id = "vote") => new(Match, Round, ballot.VotingVersion, id, voter, ballot.Artworks[target].Id);
        Require(host.Vote("p1", Vote("p1", 0)).Status == VoteStatus.SelfVote, "Self vote after transfer");
        Require(host.Vote("p1", Vote("p2", 0)).Status == VoteStatus.IdentityMismatch, "Vote identity lost at round boundary");
        Require(host.Vote("p1", Wire(Vote("p1", 1))).Status == VoteStatus.Accepted, "P1 vote failed");
        Require(host.Vote("p2", Wire(Vote("p2", 0))).Status == VoteStatus.Accepted, "P2 vote failed");
        Require(host.Vote("p3", Wire(Vote("p3", 1))).Status == VoteStatus.Accepted, "P3 vote failed");
        var result = host.Snapshot("p1").Voting!.Result!;
        Require(result.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0]) && host.Snapshot("p1").Phase == MultiplayerRoundPhase.Completed, "Transferred gallery tally incorrect");
        Reject(() => host.CloseVoting("p2")); Require(ReferenceEquals(host.CloseVoting("p1"), result), "Final result changed");
        var received = Players().Select(p => Wire(host.Snapshot(p.Player.Id).Voting!.Result!)).ToArray();
        string json = JsonSerializer.Serialize(result, RuntimeAssets.Json);
        bool agrees = received.All(r => JsonSerializer.Serialize(r, RuntimeAssets.Json) == json);
        Require(host.Vote("p3", Vote("p3", 1)).Status == VoteStatus.Duplicate, "Final vote replay changed");
        return new(agrees, packetCount, pngs.Sum(p => p.Length), hashes.ToArray(), result);
    }

    public static void CheckGeneratedImages(string root)
    {
        string folder = Path.Combine(root, "outputs/scoring-removal/packaged-drawing");
        byte[][] pngs = new[] { "generated.png", "generated-repeat.png", "generated-revised.png" }.Select(n => File.ReadAllBytes(Path.Combine(folder, n))).ToArray();
        var report = Play(pngs);
        Require(report.Hashes.Distinct().Count() == 3 && report.ResultsAgree, "Need three different generated images");
        string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "round-transfer-check.json"), JsonSerializer.Serialize(new
        { transport = "in-memory byte packets; no EOS or sockets", source = folder, report }, RuntimeAssets.Json));
        Console.WriteLine($"Multiplayer existing-PNG check: {report.ImageBytes} bytes / {report.PacketCount} packets; 3 matching results (no new GPU generation)");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
