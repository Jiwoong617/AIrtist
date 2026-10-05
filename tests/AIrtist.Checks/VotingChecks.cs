using System.Buffers.Binary;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using AIrtist.Core;

internal static class VotingChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid voting input was accepted");
    }
    private static byte[] Png()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null,
            new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 }, 6);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    public static void Run()
    {
        byte[] png = Png();
        var players = new[] { new VotingPlayer("a", "A"), new VotingPlayer("b", "B"), new VotingPlayer("c", "C") };
        var pictures = players.Select(p => new VotingArtwork("img-" + p.Id, p.Id, png)).ToArray();
        Guid match = Guid.NewGuid(), round = Guid.NewGuid();
        VoteRequest Request(string voter, string target, string id = "request") => new(match, round, 7, id, voter, target);
        VotingRoundHost Host(VotingPolicy? policy = null, TimeProvider? clock = null) => new(match, round, 7, "a", players, pictures, policy, clock);
        var host = Host();
        Require(host.Vote("a", Request("a", "img-a")).Status == VoteStatus.SelfVote, "Self vote accepted");
        Require(host.Vote("a", Request("b", "img-a")).Status == VoteStatus.IdentityMismatch, "Spoofed voter accepted");
        Require(host.Vote("stranger", Request("stranger", "img-a")).Status == VoteStatus.UnknownVoter, "Unknown connection accepted");
        Require(host.Vote("a", Request("a", "missing")).Status == VoteStatus.UnknownArtwork, "Wrong target accepted");
        Require(host.Vote("a", Request("a", "img-b") with { MatchId = Guid.NewGuid() }).Status == VoteStatus.WrongRound, "Old match accepted");
        Require(host.Vote("a", Request("a", "img-b") with { RoundId = Guid.NewGuid() }).Status == VoteStatus.WrongRound, "Old round accepted");
        Require(host.Vote("a", Request("a", "img-b") with { VotingVersion = 6 }).Status == VoteStatus.StaleVersion, "Stale phase accepted");
        Require(host.Vote("a", Request("a", "img-b", "")).Status == VoteStatus.InvalidRequest, "Empty request ID accepted");
        Require(host.Snapshot("a").AcceptedVotes == 0, "Rejections changed tally");
        Reject(() => host.Close("b"));
        Require(host.Vote("a", Request("a", "img-b")).Status == VoteStatus.Accepted, "First vote failed");
        long revision = host.Snapshot("a").Revision;
        Require(host.Vote("a", Request("a", "img-b")).Status == VoteStatus.Duplicate && host.Snapshot("a").Revision == revision, "Replay counted twice");
        Require(host.Vote("a", Request("a", "img-c")).Status == VoteStatus.RequestConflict, "Reused ID changed target");
        Require(host.Vote("a", Request("a", "img-c", "new")).Status == VoteStatus.AlreadyVoted, "Second vote accepted");
        // Other players still use the opening phase version; accepted ballots only advance Revision.
        Require(host.Vote("b", Request("b", "img-a")).Status == VoteStatus.Accepted, "Concurrent client's phase invalidated");
        Require(host.Vote("c", Request("c", "img-b")).Status == VoteStatus.Accepted, "Third vote failed");
        var result = host.Snapshot("a").Result!;
        Require(result.Reason == VotingEnd.AllVoted && result.AcceptedVotes == 3 && result.MissingVotes == 0
            && result.Tallies.Single(t => t.ArtworkId == "img-b").Votes == 2 && result.LeadingArtworkIds.Single() == "img-b", "Wrong tally");
        Require(host.Vote("c", Request("c", "img-b")).Status == VoteStatus.Duplicate, "Closed accepted retry lost receipt");
        Require(host.Vote("c", Request("c", "img-a", "after")).Status == VoteStatus.Closed, "Post-close vote accepted");
        Require(ReferenceEquals(host.Close("a"), result), "Result finalized twice");
        string json = JsonSerializer.Serialize(result, RuntimeAssets.Json);
        foreach (var player in players) Require(JsonSerializer.Serialize(host.Snapshot(player.Id).Result, RuntimeAssets.Json) == json, "Result differs by recipient");
        var restored = JsonSerializer.Deserialize<VotingResult>(json, RuntimeAssets.Json)!;
        Require(restored.RoundId == round && restored.Tallies.Sum(t => t.Votes) == 3 && restored.Revision == result.Revision, "Result transfer serialization failed");
        Reject(() => ((IList<ArtworkTally>)result.Tallies).Add(new("bad", "a", 99)));

        var manual = new ManualClock();
        var timed = Host(new(TimeSpan.FromSeconds(60), false, true), manual);
        manual.Advance(TimeSpan.FromMilliseconds(59999));
        Require(timed.Vote("a", Request("a", "img-b")).Status == VoteStatus.Accepted, "Vote before deadline rejected");
        manual.Advance(TimeSpan.FromMilliseconds(1));
        Require(timed.Vote("b", Request("b", "img-a")).Status == VoteStatus.Closed, "Vote at deadline accepted");
        Require(timed.Snapshot("b").Result is { Reason: VotingEnd.Deadline, AcceptedVotes: 1, MissingVotes: 2 }, "Deadline result wrong");
        var early = Host(); var empty = early.Close("a");
        Require(empty.AcceptedVotes == 0 && empty.LeadingArtworkIds.Count == 0 && early.Vote("a", Request("a", "img-b")).Status == VoteStatus.Closed, "Manual closure wrong");
        var two = new VotingRoundHost(match, round, 7, "a", players.Take(2), pictures.Take(2));
        two.Vote("a", Request("a", "img-b")); two.Vote("b", Request("b", "img-a"));
        Require(two.Snapshot("a").Result is { LeadingArtworkIds.Count: 2 } tie && tie.Tallies.All(t => t.Votes == 1), "Two-player tie changed");
        foreach (int count in new[] { 0, 1 })
            Require(new VotingRoundHost(match, round, 7, "a", players, pictures.Take(count)).Snapshot("a").Result?.Reason == VotingEnd.InsufficientImages, "Insufficient pictures not closed");
        var noPicture = new VotingRoundHost(match, round, 7, "a", players, pictures.Take(2));
        Require(noPicture.Snapshot("c").CanVote && noPicture.Vote("c", Request("c", "img-a")).Status == VoteStatus.Accepted, "Default missing-image voter ineligible");
        var ownersOnly = new VotingRoundHost(match, round, 7, "a", players, pictures.Take(2), new(TimeSpan.FromSeconds(60), true, false));
        Require(!ownersOnly.Snapshot("c").CanVote && ownersOnly.Vote("c", Request("c", "img-a")).Status == VoteStatus.Ineligible, "Eligibility configuration ignored");
        var racing = Host(new(TimeSpan.FromSeconds(60), false, true));
        Parallel.For(0, 20, i => racing.Vote("a", Request("a", i % 2 == 0 ? "img-b" : "img-c", "race-" + i)));
        Require(racing.Snapshot("a").AcceptedVotes == 1, "Parallel duplicates counted twice");

        Reject(() => new VotingRoundHost(match, round, 7, "a", [players[0], players[0]], pictures));
        Reject(() => new VotingRoundHost(match, round, 7, "a", players, [pictures[0], pictures[0]]));
        Reject(() => new VotingRoundHost(match, round, 7, "a", players, [new("other", "stranger", png)]));
        Reject(() => new VotingRoundHost(match, round, 7, "a", players, [pictures[0], new("other", "a", png)]));
        Reject(() => Host(new(TimeSpan.Zero, true, true)));
        Reject(() => new VotingArtwork("bad", "a", [1, 2, 3]));
        Reject(() => new VotingArtwork("bad", "a", png[..33]));
        Reject(() => new VotingArtwork("bad", "a", new byte[VotingArtwork.MaxBytes + 1]));
        var large = (byte[])png.Clone(); BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(16, 4), 8192);
        Reject(() => new VotingArtwork("bad", "a", large));
        var input = (byte[])png.Clone(); var isolated = new VotingArtwork("isolated", "a", input);
        input[0] = 0; var copy = isolated.CopyPng(); copy[0] = 0;
        Require(isolated.CopyPng()[0] == 137 && isolated.Preview.IsFrozen, "Image memory was externally mutable");
        Console.WriteLine("Voting: 3-player tally/result transfer, 2-player tie, self/spoof/duplicate/stale/late rejection, deadline, authority, eligibility, concurrency, bounded immutable PNG passed");
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan time) => ticks += time.Ticks;
    }
}
