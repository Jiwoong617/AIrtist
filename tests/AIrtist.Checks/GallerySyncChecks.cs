using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class GallerySyncChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid gallery operation was accepted");
    }
    private static T Wire<T>(T value) where T : class => RoundControlMessages.Decode<T>(RoundControlMessages.Encode(value));
    private static byte[] Png(int seed)
    {
        var pixels = new byte[32 * 32 * 3]; new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 32 * 3);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
        public readonly Clock Clock = new();
        public readonly VotingPlayer[] Players;
        public readonly MultiplayerRoundHost Host;
        public readonly GalleryManifest Gallery;
        public World(byte[][] images, int count = 3)
        {
            Players = Enumerable.Range(1, count).Select(i => new VotingPlayer("p" + i, "참가자 " + i)).ToArray();
            var compatibility = new MultiplayerCompatibility(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
            Host = new(Match, Round, 10, "p1", new("apple", "사과", "과일", "apple", ["사과", "apple"]), compatibility,
                Players.Select(p => new MultiplayerParticipant(p, compatibility)), clock: Clock);
            for (int i = 0; i < count; i++)
            {
                var approval = Host.Submit(Players[i].Id, new(Match, Round, 10, "submit", Players[i].Id, "빨간 과일", "a red fruit")).Approval!;
                if (i >= images.Length) { Host.ReportGenerationFailure(Players[i].Id, approval.ApprovalId); continue; }
                var descriptor = ImagePackets.Describe(approval, Guid.NewGuid(), images[i]);
                Require(Host.BeginImage(Players[i].Id, descriptor) == ImageTransferStatus.Accepted, "Source image begin failed");
                foreach (var packet in ImagePackets.Split(descriptor, images[i])) Host.ReceiveImagePacket(Players[i].Id, packet);
            }
            Gallery = Host.Snapshot("p1").Gallery!.Manifest;
        }
        public MultiplayerRoundClient Client(string id) => new(Match, Round, 10, "p1", id, Players, Clock);
        public void Dispose() => Host.Dispose();
    }

    private static int Deliver(World world, MultiplayerRoundClient client, string player, bool declare = true)
    {
        var status = declare ? client.AcceptGallery("p1", Wire(world.Gallery)) : RoundSyncStatus.Accepted;
        Require(status is RoundSyncStatus.Accepted or RoundSyncStatus.Duplicate, "Gallery declaration failed");
        int packets = 0;
        foreach (var entry in world.Gallery.Artworks.Reverse())
        {
            foreach (var packet in world.Host.GalleryPackets(player, entry.Transfer.TransferId).Reverse())
            {
                status = client.ReceiveGalleryPacket("p1", (byte[])packet.Clone());
                Require(status is RoundSyncStatus.Accepted or RoundSyncStatus.Complete, "Gallery chunk failed");
                Require(client.ReceiveGalleryPacket("p1", packet) == RoundSyncStatus.Duplicate, "Gallery duplicate failed");
                packets++;
            }
        }
        Require(client.BufferedImageBytes == 0 && client.ReadyMessage() is not null, "Client announced readiness before release");
        return packets;
    }

    public static void Run()
    {
        Play([Png(1), Png(2), Png(3)]); Validation(); Failures(); InsufficientImages();
        ReliablePlay([Png(11), Png(12), Png(13)]);
        Console.WriteLine("Gallery sync: three isolated clients, full PNG fanout, readiness barrier, authority/version/revision checks, preserved results, bounded messages, timeout/corruption/disconnect passed (in-memory, not EOS)");
    }

    private sealed record SyncReport(int Clients, int Packets, int PngBytesPerClient, bool ResultsAgree, int[] Votes);
    private static SyncReport Play(byte[][] pngs)
    {
        using var world = new World(pngs);
        using var c1 = world.Client("p1"); using var c2 = world.Client("p2"); using var c3 = world.Client("p3");
        var clients = new[] { c1, c2, c3 };
        Require(world.Host.Snapshot("p1").Phase == MultiplayerRoundPhase.Distributing && world.Host.VotingState("p1") is null, "Timer started before gallery readiness");
        Require(c2.ReadyMessage() is null, "Client acknowledged missing gallery");
        Require(world.Host.Vote("p2", new(world.Match, world.Round, 12, "early", "p2", world.Gallery.Artworks[0].ArtworkId)).Status == VoteStatus.Closed, "Early vote accepted");
        int packets = 0;
        for (int i = 0; i < clients.Length; i++)
        {
            packets += Deliver(world, clients[i], "p" + (i + 1));
            var snapshot = clients[i].Snapshot();
            Require(snapshot.Phase == RoundClientPhase.Ready && !snapshot.CanVote && snapshot.Artworks.Count == 3, "Incomplete gallery enabled voting");
            for (int n = 0; n < pngs.Length; n++) Require(snapshot.Artworks[n].CopyPng().SequenceEqual(pngs[n]), "Replica differs from original PNG");
        }
        Require(world.Host.AcknowledgeGallery("p3", c2.ReadyMessage()!) == GalleryReadyStatus.IdentityMismatch, "Spoofed readiness");
        Require(world.Host.AcknowledgeGallery("stranger", c2.ReadyMessage()!) == GalleryReadyStatus.UnknownPlayer, "Unknown readiness");
        Require(world.Host.AcknowledgeGallery("p2", c2.ReadyMessage()! with { GalleryId = Guid.NewGuid() }) == GalleryReadyStatus.WrongGallery, "Old readiness");
        Require(world.Host.AcknowledgeGallery("p2", Wire(c2.ReadyMessage()!)) == GalleryReadyStatus.Accepted, "P2 readiness failed");
        world.Clock.Advance(TimeSpan.FromMilliseconds(59999));
        Require(world.Host.AcknowledgeGallery("p2", Wire(c2.ReadyMessage()!)) == GalleryReadyStatus.Duplicate
            && world.Host.VotingState("p1") is null, "Readiness replay opened/reset ballot");
        Require(world.Host.AcknowledgeGallery("p3", Wire(c3.ReadyMessage()!)) == GalleryReadyStatus.Accepted, "Last readiness before deadline rejected");
        for (int i = 0; i < clients.Length; i++)
            Require(clients[i].AcceptVotingState("p1", Wire(world.Host.VotingState("p" + (i + 1))!)) == RoundSyncStatus.Accepted, "Voting start sync failed");
        var opening = Wire(world.Host.VotingState("p2")!);
        Require(c2.Snapshot().VotingRemaining == TimeSpan.FromSeconds(60), "Transfer time reduced ballot time");
        Require(c2.AcceptVotingState("p3", opening) == RoundSyncStatus.WrongSender, "Peer forged host state");
        Require(c2.AcceptVotingState("p1", opening with { RecipientId = "p1" }) == RoundSyncStatus.WrongRecipient, "Other player's selection disclosed");
        Require(c2.AcceptVotingState("p1", opening with { RoundId = Guid.NewGuid() }) == RoundSyncStatus.WrongRound, "Old round state accepted");
        Require(c2.AcceptVotingState("p1", opening with { Revision = 1, AcceptedVotes = 1 }) == RoundSyncStatus.Conflict, "Same revision changed tally");
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        Require(c2.AcceptVotingState("p1", opening) == RoundSyncStatus.Duplicate && c2.Snapshot().VotingRemaining == TimeSpan.FromSeconds(55), "Replay extended displayed timer");
        Reject(() => c2.CreateVote(world.Gallery.Artworks[1].ArtworkId, "self"));
        string[] targets = [world.Gallery.Artworks[1].ArtworkId, world.Gallery.Artworks[0].ArtworkId, world.Gallery.Artworks[1].ArtworkId];
        for (int i = 0; i < clients.Length; i++)
        {
            var request = clients[i].CreateVote(targets[i], "vote");
            Require(ReferenceEquals(clients[i].CreateVote(targets[i], "vote"), request) && !clients[i].Snapshot().CanVote, "Pending vote replay changed");
            Require(world.Host.Vote("p" + (i + 1), Wire(request)).Status == VoteStatus.Accepted, "Vote was rejected");
        }
        var finalStates = Enumerable.Range(1, 3).Select(i => Wire(world.Host.VotingState("p" + i)!)).ToArray();
        var valid = finalStates[1];
        var badTallies = valid.Result!.Tallies.ToArray(); badTallies[0] = badTallies[0] with { Votes = 99 };
        foreach (var invalid in new[] {
            valid with { GalleryId = Guid.NewGuid() }, valid with { VotingVersion = valid.VotingVersion - 1 },
            valid with { EligibleVoters = 99 }, valid with { CanVote = true },
            valid with { SelectedArtworkId = world.Gallery.Artworks[1].ArtworkId },
            valid with { Result = valid.Result with { Tallies = badTallies } },
            valid with { Result = valid.Result with { LeadingArtworkIds = Array.Empty<string>() } },
            valid with { Result = valid.Result with { RoundId = Guid.NewGuid() } },
            valid with { Result = valid.Result with { MissingVotes = 1 } } })
            Require(c2.AcceptVotingState("p1", invalid) is RoundSyncStatus.InvalidMessage or RoundSyncStatus.WrongRound,
                "Malformed final state accepted");
        Require(c2.Snapshot().Result is null, "Rejected final state was committed");
        for (int i = 0; i < clients.Length; i++)
        {
            Require(clients[i].AcceptVotingState("p1", finalStates[i]) == RoundSyncStatus.Accepted, "Final state rejected");
            Require(clients[i].AcceptVotingState("p1", Wire(finalStates[i])) == RoundSyncStatus.Duplicate, "Final replay rejected");
        }
        Require(c2.AcceptVotingState("p1", opening) == RoundSyncStatus.Stale, "Final result regressed");
        var higher = finalStates[1] with { Revision = finalStates[1].Revision + 1, Result = finalStates[1].Result! with { Revision = finalStates[1].Revision + 1 } };
        Require(c2.AcceptVotingState("p1", higher) == RoundSyncStatus.Conflict, "Final result overwritten with later revision");
        string resultJson = JsonSerializer.Serialize(c1.Snapshot().Result);
        bool agrees = clients.All(c => JsonSerializer.Serialize(c.Snapshot().Result) == resultJson);
        Require(agrees && c2.Snapshot().Result!.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0]), "Replica results differ");
        // Deserialized result lists belong to the caller; the client must keep a frozen copy.
        ((IList<ArtworkTally>)finalStates[1].Result!.Tallies)[0] = new("bad", "bad", 99);
        Require(JsonSerializer.Serialize(c2.Snapshot().Result) == resultJson, "Caller mutated committed result");
        c2.MarkHostUnavailable();
        Require(c2.Snapshot().Phase == RoundClientPhase.Disconnected && !c2.Snapshot().CanVote
            && JsonSerializer.Serialize(c2.Snapshot().Result) == resultJson, "Host loss erased confirmed result");
        Require(c2.AcceptVotingState("p1", opening) == RoundSyncStatus.Closed, "Disconnected state reopened");
        return new(3, packets, pngs.Sum(p => p.Length), agrees, [1, 2, 0]);
    }

    private static GalleryManifest Entries(GalleryManifest manifest, params GalleryArtwork[] entries)
        => manifest with { Artworks = Array.AsReadOnly(entries) };
    private static void Validation()
    {
        using var world = new World([Png(4), Png(5), Png(6)]);
        using var client = world.Client("p2");
        var gallery = world.Gallery; var first = gallery.Artworks[0];
        Require(client.AcceptGallery("p3", gallery) == RoundSyncStatus.WrongSender && client.BufferedImageBytes == 0, "Peer forged gallery");
        Require(client.AcceptGallery("p1", gallery with { RoundId = Guid.NewGuid() }) == RoundSyncStatus.WrongRound, "Wrong gallery round");
        foreach (var invalid in new[] {
            gallery with { GalleryVersion = 10 }, gallery with { GalleryId = Guid.Empty }, gallery with { HostId = "p3" },
            gallery with { Players = Array.AsReadOnly(world.Players.Reverse().ToArray()) },
            gallery with { Artworks = null! }, gallery with { VotingPolicy = null! },
            Entries(gallery, first, first), Entries(gallery, first with { ArtworkId = "fake" }),
            Entries(gallery, first with { Transfer = first.Transfer with { PlayerId = "stranger" } }),
            Entries(gallery, first with { Transfer = first.Transfer with { TotalBytes = VotingArtwork.MaxBytes + 1 } }),
            Entries(gallery, first with { Transfer = first.Transfer with { Sha256 = "bad" } }),
            Entries(gallery, first with { Transfer = first.Transfer with { RoundId = Guid.NewGuid() } }) })
        {
            Require(client.AcceptGallery("p1", invalid) is RoundSyncStatus.InvalidMessage or RoundSyncStatus.WrongRound, "Invalid gallery accepted");
            Require(client.BufferedImageBytes == 0 && client.ReadyMessage() is null, "Invalid gallery reserved memory");
        }
        Require(client.AcceptGallery("p1", Wire(gallery)) == RoundSyncStatus.Accepted, "Valid gallery after rejection failed");
        int buffered = client.BufferedImageBytes;
        Require(client.AcceptGallery("p1", Wire(gallery)) == RoundSyncStatus.Duplicate && client.BufferedImageBytes == buffered, "Duplicate gallery allocated buffers");
        Require(client.AcceptGallery("p1", gallery with { GalleryId = Guid.NewGuid() }) == RoundSyncStatus.Conflict, "Gallery replaced after start");
        var packet = world.Host.GalleryPackets("p2", first.Transfer.TransferId).First();
        Require(client.ReceiveGalleryPacket("p3", packet) == RoundSyncStatus.WrongSender, "Non-host image accepted");
        Require(client.ReceiveGalleryPacket("p1", [1, 2, 3]) == RoundSyncStatus.InvalidMessage, "Malformed image header");
        using var later = new MultiplayerRoundClient(world.Match, Guid.NewGuid(), 10, "p1", "p2", world.Players);
        Require(later.AcceptGallery("p1", gallery) == RoundSyncStatus.WrongRound, "Prior round gallery reused");
        Reject(() => RoundControlMessages.Decode<GalleryManifest>(new byte[RoundControlMessages.MaxBytes + 1]));
        Reject(() => RoundControlMessages.Decode<GalleryManifest>("{}"u8));
        Reject(() => RoundControlMessages.Decode<GalleryManifest>("{\"Protocol\":1,\"Kind\":\"gallery\"}"u8));
        Reject(() => RoundControlMessages.Decode<GalleryReady>(RoundControlMessages.Encode(gallery)));
        var text = Encoding.UTF8.GetString(RoundControlMessages.Encode(gallery));
        Reject(() => RoundControlMessages.Decode<GalleryManifest>(Encoding.UTF8.GetBytes(text.Replace("\"Protocol\":1", "\"Protocol\":999", StringComparison.Ordinal))));
        Reject(() => RoundControlMessages.Decode<GalleryManifest>(Encoding.UTF8.GetBytes(text.Insert(1, "\"Unexpected\":1,"))));
        using var large = new World([Png(1), Png(2), Png(3), Png(4), Png(5)], 5);
        using var capped = large.Client("p2");
        var overBudget = Entries(large.Gallery, large.Gallery.Artworks.Select(a => a with { Transfer = a.Transfer with { TotalBytes = VotingArtwork.MaxBytes } }).ToArray());
        Require(capped.AcceptGallery("p1", overBudget) == RoundSyncStatus.InvalidMessage && capped.BufferedImageBytes == 0, "Gallery memory cap bypassed");
        Reject(() => world.Host.GalleryPackets("stranger", first.Transfer.TransferId));
    }

    private static void Failures()
    {
        using var world = new World([Png(7), Png(8), Png(9)]);
        using var corrupt = world.Client("p2"); corrupt.AcceptGallery("p1", Wire(world.Gallery));
        var packet = world.Host.GalleryPackets("p2", world.Gallery.Artworks[0].Transfer.TransferId).First();
        Require(corrupt.ReceiveGalleryPacket("p1", packet) == RoundSyncStatus.Accepted, "First packet failed");
        var conflicting = (byte[])packet.Clone(); conflicting[^1] ^= 1;
        Require(corrupt.ReceiveGalleryPacket("p1", conflicting) == RoundSyncStatus.CorruptImage && corrupt.BufferedImageBytes == 0
            && corrupt.ReadyMessage() is null && corrupt.Snapshot().Artworks.Count == 0, "Corruption retained gallery");
        using var p2 = world.Client("p2"); using var p3 = world.Client("p3");
        Deliver(world, p2, "p2"); p3.AcceptGallery("p1", Wire(world.Gallery));
        p3.ReceiveGalleryPacket("p1", packet);
        world.Host.AcknowledgeGallery("p2", p2.ReadyMessage()!);
        world.Clock.Advance(TimeSpan.FromSeconds(60));
        Require(world.Host.Snapshot("p1").Phase == MultiplayerRoundPhase.GalleryTimedOut && world.Host.VotingState("p2") is null, "Gallery timeout awarded votes");
        Require(world.Host.AcknowledgeGallery("p2", p2.ReadyMessage()!) == GalleryReadyStatus.Closed, "Late readiness accepted");
        Require(p3.Snapshot().Phase == RoundClientPhase.Failed && p3.BufferedImageBytes == 0, "Missing packets did not expire");
        var stopped = Wire(world.Host.GalleryFailureState("p2")!);
        Require(p2.AcceptGalleryStopped("p3", stopped) == RoundSyncStatus.WrongSender, "Peer stopped round");
        Require(p2.AcceptGalleryStopped("p1", stopped) == RoundSyncStatus.Accepted && p2.Snapshot().Phase == RoundClientPhase.Failed
            && p2.Snapshot().Artworks.Count == 0 && p2.ReadyMessage() is null, "Ready peer ignored host abort");
        Require(p2.AcceptGalleryStopped("p1", stopped) == RoundSyncStatus.Duplicate, "Repeated abort not idempotent");
        using var disconnected = world.Client("p2"); disconnected.AcceptGallery("p1", Wire(world.Gallery));
        disconnected.MarkHostUnavailable();
        Require(disconnected.BufferedImageBytes == 0 && disconnected.ReceiveGalleryPacket("p1", packet) == RoundSyncStatus.Closed, "Disconnect retained active buffers");

        using var malformed = world.Client("p2"); var junk = new byte[2048];
        var a = world.Gallery.Artworks[0];
        var entry = a with { Transfer = a.Transfer with { TotalBytes = junk.Length, Sha256 = Convert.ToHexString(SHA256.HashData(junk)) } };
        var badGallery = Entries(world.Gallery, entry);
        Require(malformed.AcceptGallery("p1", badGallery) == RoundSyncStatus.Accepted, "Valid-sized descriptor rejected prematurely");
        RoundSyncStatus last = default;
        foreach (var part in ImagePackets.Split(entry.Transfer, junk)) last = malformed.ReceiveGalleryPacket("p1", part);
        Require(last == RoundSyncStatus.CorruptImage && malformed.BufferedImageBytes == 0, "Hash-valid non-PNG gallery accepted");
    }

    private static void InsufficientImages()
    {
        foreach (int count in new[] { 0, 1 })
        {
            using var world = new World(count == 0 ? [] : [Png(10)]);
            using var client = world.Client("p2");
            var final = Wire(world.Host.VotingState("p2")!);
            Require(client.AcceptVotingState("p1", final) == RoundSyncStatus.NotReady, "Result before gallery metadata");
            Deliver(world, client, "p2");
            Require(client.AcceptVotingState("p1", final) == RoundSyncStatus.Accepted
                && client.Snapshot().Result is { Reason: VotingEnd.InsufficientImages, AcceptedVotes: 0, LeadingArtworkIds.Count: 0 }, "Insufficient-gallery result mismatch");
        }
    }

    public static void CheckGeneratedImages(string root)
    {
        string folder = Path.Combine(root, "outputs/scoring-removal/packaged-drawing");
        var pngs = new[] { "generated.png", "generated-repeat.png", "generated-revised.png" }.Select(n => File.ReadAllBytes(Path.Combine(folder, n))).ToArray();
        var report = Play(pngs);
        string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "gallery-sync-check.json"), JsonSerializer.Serialize(new
        { transport = "in-memory; three independent clients, including host local view; no EOS or sockets", report }, RuntimeAssets.Json));
        Console.WriteLine($"Gallery existing-PNG check: {report.PngBytesPerClient} bytes per client, {report.Packets} packets across {report.Clients} clients; results match");
    }

    // Persistent channels for each participant; every control action is applied in
    // the handler before ConfirmReceived. Drop one data packet AND ACK per message.
    private static int Exchange<T>(ReliableControlChannel sender, ReliableControlChannel receiver, string senderId, string receiverId,
        T value, Action<T> handle, Clock clock) where T : class
    {
        Require(sender.TryQueue(RoundControlMessages.Encode(value), out ulong sequence) == ControlQueueStatus.Queued, "Reliable control enqueue failed");
        int handled = 0, packets = 0; bool lostData = false, lostAck = false;
        for (int tick = 0; tick < 150 && sender.Snapshot().LastAcknowledgedSequence != sequence; tick++)
        {
            foreach (var packet in sender.PollPackets().Reverse())
            {
                packets++;
                if (!lostData) { lostData = true; continue; }
                receiver.Receive(senderId, packet); receiver.Receive(senderId, packet);
            }
            if (receiver.TakeReceived() is { } message)
            {
                handle(RoundControlMessages.Decode<T>(message.CopyPayload())); handled++;
                Require(receiver.ConfirmReceived(message.Sequence), "Handled message not confirmed");
            }
            foreach (var packet in receiver.PollPackets())
            {
                packets++;
                if (!lostAck) { lostAck = true; continue; }
                sender.Receive(receiverId, packet);
            }
            clock.Advance(TimeSpan.FromMilliseconds(50));
        }
        Require(handled == 1 && lostData && lostAck && sender.Snapshot().LastAcknowledgedSequence == sequence, "Lost control message executed zero/multiple times");
        return packets;
    }

    private sealed record ReliableReport(int ControlMessages, int ControlPackets, int DroppedDataPackets, int DroppedAckPackets,
        int ImagePackets, int PngBytesPerClient, bool ResultsAgree);
    private static ReliableReport ReliablePlay(byte[][] pngs)
    {
        using var world = new World(pngs);
        var clients = world.Players.Select(p => world.Client(p.Id)).ToArray();
        var hostChannels = world.Players.Select(p => new ReliableControlChannel(world.Match, world.Round, p.Id, clock: world.Clock)).ToArray();
        var clientChannels = world.Players.Select(_ => new ReliableControlChannel(world.Match, world.Round, "p1", clock: world.Clock)).ToArray();
        int controls = 0, controlPackets = 0, images = 0;
        try
        {
            void ToClient<T>(int i, T value, Action<T> handle) where T : class
            {
                controlPackets += Exchange(hostChannels[i], clientChannels[i], "p1", world.Players[i].Id, value, handle, world.Clock); controls++;
            }
            void ToHost<T>(int i, T value, Action<T> handle) where T : class
            {
                controlPackets += Exchange(clientChannels[i], hostChannels[i], world.Players[i].Id, "p1", value, handle, world.Clock); controls++;
            }
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i]; string player = world.Players[i].Id;
                ToClient(i, world.Gallery, message => Require(client.AcceptGallery("p1", message) == RoundSyncStatus.Accepted, "Reliable gallery failed"));
                images += Deliver(world, client, player, declare: false);
                ToHost(i, client.ReadyMessage()!, message => Require(world.Host.AcknowledgeGallery(player, message)
                    == (i == 0 ? GalleryReadyStatus.Duplicate : GalleryReadyStatus.Accepted), "Reliable readiness failed"));
            }
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                ToClient(i, world.Host.VotingState(world.Players[i].Id)!, message => Require(client.AcceptVotingState("p1", message) == RoundSyncStatus.Accepted, "Reliable voting start failed"));
            }
            int[] targets = [1, 0, 1];
            for (int i = 0; i < clients.Length; i++)
            {
                string player = world.Players[i].Id;
                var request = clients[i].CreateVote(world.Gallery.Artworks[targets[i]].ArtworkId, "reliable-vote");
                ToHost(i, request, message => Require(world.Host.Vote(player, message).Status == VoteStatus.Accepted, "Reliable vote failed or executed twice"));
            }
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                ToClient(i, world.Host.VotingState(world.Players[i].Id)!, message => Require(client.AcceptVotingState("p1", message) == RoundSyncStatus.Accepted, "Reliable result failed"));
            }
            string result = JsonSerializer.Serialize(clients[0].Snapshot().Result);
            bool agrees = clients.All(c => JsonSerializer.Serialize(c.Snapshot().Result) == result);
            Require(agrees && clients[0].Snapshot().Result!.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0]), "Reliable results differ");
            foreach (var channel in hostChannels.Concat(clientChannels)) Require(channel.Snapshot() is { QueuedBytes: 0, ReceiveBytes: 0, Failure: ControlChannelFailure.None }, "Reliable round retained work");
            // A later unacknowledged control action must terminate its lane; the
            // adapter's disconnect signal preserves an already confirmed result.
            hostChannels[1].TryQueue(RoundControlMessages.Encode(world.Host.VotingState("p2")!), out _);
            world.Clock.Advance(TimeSpan.FromSeconds(10));
            Require(hostChannels[1].Snapshot().Failure == ControlChannelFailure.SendTimedOut, "Unresponsive peer did not expire");
            clients[1].MarkHostUnavailable();
            Require(JsonSerializer.Serialize(clients[1].Snapshot().Result) == result && !clients[1].Snapshot().CanVote, "Timeout erased confirmed result");
            return new(controls, controlPackets, controls, controls, images, pngs.Sum(p => p.Length), agrees);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var channel in hostChannels.Concat(clientChannels)) channel.Dispose();
        }
    }

    public static void CheckReliableGeneratedImages(string root)
    {
        string folder = Path.Combine(root, "outputs/scoring-removal/packaged-drawing");
        var pngs = new[] { "generated.png", "generated-repeat.png", "generated-revised.png" }.Select(n => File.ReadAllBytes(Path.Combine(folder, n))).ToArray();
        var report = ReliablePlay(pngs);
        string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "reliable-gallery-check.json"), JsonSerializer.Serialize(new
        { transport = "persistent in-memory control channels; simulated packet/ACK loss; three client states including host view; no EOS", report }, RuntimeAssets.Json));
        Console.WriteLine($"Reliable gallery: {report.ControlMessages} controls / {report.ControlPackets} packets, {report.DroppedDataPackets + report.DroppedAckPackets} deliberate drops, 3 matching results");
    }
}
