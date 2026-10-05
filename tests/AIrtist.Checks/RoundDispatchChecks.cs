using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class RoundDispatchChecks
{
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static readonly Word Word = new("apple", "사과", "과일", "apple", ["사과", "apple"]);
    private static readonly VotingPlayer[] Players = [new("p1", "방장"), new("p2", "참가자 2"), new("p3", "참가자 3")];
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe round dispatch operation accepted");
    }
    private static byte[] Png(int seed)
    {
        var pixels = new byte[32 * 32 * 3]; new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Rgb24, null, pixels, 32 * 3);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class World : IDisposable
    {
        public readonly Guid Match = Guid.NewGuid(), Round = Guid.NewGuid();
        public readonly ControlTransportChecks.Clock Clock = new();
        public readonly MultiplayerRoundHost Host;
        public readonly HostControlDispatcher Dispatch;
        public World()
        {
            Host = new(Match, Round, 5, "p1", Word, Compatibility, Players.Select(p => new MultiplayerParticipant(p, Compatibility)), clock: Clock);
            Dispatch = new(Host, "p1", Clock);
        }
        public MultiplayerSubmissionClient Client(string id = "p2") => new(Match, Round, 5, "p1", id, Players, Compatibility, [Word], Clock);
        public SubmissionRequest Request(string id = "p2") => new(Match, Round, 5, "request", id, "빨간 과일", "a red fruit");
        public void Dispose() { Dispatch.Dispose(); Host.Dispose(); }
    }
    private static RoundSyncStatus Client<T>(MultiplayerSubmissionClient client, T message, string sender = "p1") where T : class
        => client.Dispatch(sender, RoundControlMessages.Encode(message)).Status;
    private static ControlDispatchResult Host<T>(World world, T message, string sender = "p2") where T : class
        => world.Dispatch.Dispatch(sender, RoundControlMessages.Encode(message));

    public static void Run()
    {
        Authority(); ClientLifecycle(); Deadlines(); Backpressure(); GenerationFailure(); EarlyGalleryCompletion(); FailureThroughLoss();
        var report = Play([Png(61), Png(62), Png(63)]);
        Require(report.ResultsAgree && report.GenerationStarts == 3, "Full dispatched round failed");
        var recovered = Play([Png(81), Png(82), Png(83)], reliableImages: true);
        Require(recovered.ResultsAgree && recovered.ConfirmedImageTransfers == 12 && recovered.DroppedImagePackets > 0, "Reliable image round failed");
        Console.WriteLine("Round dispatch: start/compatibility, submission/approval correlation, one generation, image permission, bounded replies/timeouts, spoof/stale rejection and full 3-player round passed (in-memory, not EOS)");
    }

    private static void Authority()
    {
        using var world = new World(); using var client = world.Client();
        var start = world.Host.StartMessage("p2");
        Require(Client(client, start, "p3") == RoundSyncStatus.WrongSender, "Non-host started round");
        Require(Client(client, start with { RoundId = Guid.NewGuid() }) == RoundSyncStatus.WrongRound, "Old start accepted");
        Require(Client(client, start with { RecipientId = "p3" }) == RoundSyncStatus.WrongRecipient, "Other recipient start");
        foreach (var bad in new[] { start with { Compatibility = Compatibility with { RulesVersion = "multiplayer-round-v2" } },
            start with { Compatibility = Compatibility with { WordsSha256 = new('C', 64) } },
            start with { Compatibility = Compatibility with { AssetsSha256 = new('C', 64) } },
            start with { Players = Players.Reverse().ToArray() }, start with { Players = null! },
            start with { Prompt = new("unknown", "사과", "과일") }, start with { Prompt = start.Prompt with { Meaning = "회사" } },
            start with { Policy = null! }, start with { Policy = start.Policy with { GenerationDuration = TimeSpan.MaxValue } },
            start with { WritingRemaining = TimeSpan.FromMinutes(99) } })
            Require(Client(client, bad) == RoundSyncStatus.InvalidMessage, "Invalid start accepted");
        Require(client.Snapshot().Phase == SubmissionClientPhase.AwaitingStart && client.TakeGenerationApproval() is null, "Invalid start changed phase");
        Reject(() => client.CreateSubmission("빨간 과일", "a red fruit"));
        Require(Client(client, start) == RoundSyncStatus.Accepted, "Valid start rejected");
        Require(Client(client, start with { WritingDeadline = start.WritingDeadline.AddSeconds(1) }) == RoundSyncStatus.Conflict, "Changed start reset round");
        Require(Host(world, start).Status == RoundSyncStatus.InvalidMessage, "Client sent host-only message");
        Require(Client(client, world.Request()) == RoundSyncStatus.InvalidMessage, "Host injected client request");
        Require(Host(world, world.Request(), "stranger").Status == RoundSyncStatus.WrongSender, "Unknown sender");
        Require(Host(world, world.Request("p3")).Status == RoundSyncStatus.WrongSender, "Payload identity impersonation");
        Require(Host(world, world.Request() with { RoundId = Guid.NewGuid() }).Status == RoundSyncStatus.WrongRound, "Old submission");
        Require(Host(world, world.Request() with { SubmissionVersion = 4 }).Status == RoundSyncStatus.WrongRound, "Stale submission");
        Require(Host(world, world.Request() with { RequestId = new('x', 81) }).Status == RoundSyncStatus.InvalidMessage, "Unbounded request correlation");
        var denied = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, world.Request() with { Translation = "a red apple" }).Reply!);
        Require(denied.Receipt.Status == SubmissionStatus.InvalidRequest && world.Host.Snapshot("p2").OwnApproval is null, "Invalid word consumed approval");
        foreach (var malformed in new[] { "{}", "{\"Protocol\":1,\"Kind\":\"round-start\",\"Body\":null}",
            "{\"Protocol\":1,\"Kind\":\"unknown\",\"Body\":{}}", "{\"Protocol\":99,\"Kind\":\"submission\",\"Body\":{}}" })
        {
            Require(world.Dispatch.Dispatch("p2", Encoding.UTF8.GetBytes(malformed)).Status == RoundSyncStatus.InvalidMessage, "Malformed host input escaped");
            Require(client.Dispatch("p1", Encoding.UTF8.GetBytes(malformed)).Status == RoundSyncStatus.InvalidMessage, "Malformed client input escaped");
        }
        Require(world.Dispatch.Dispatch("p2", new byte[32769]).Status == RoundSyncStatus.InvalidMessage, "Oversized dispatch");
        Require(world.Dispatch.Dispatch("p2", "{\"Protocol\":1,\"Kind\":\"submission\",\"Body\":{}}"u8).Status == RoundSyncStatus.InvalidMessage, "Missing body fields escaped");
    }

    private static void ClientLifecycle()
    {
        using var world = new World(); using var client = world.Client();
        Client(client, world.Host.StartMessage("p2"));
        Reject(() => client.CreateSubmission("사 과", "a red fruit"));
        Require(client.Snapshot().Phase == SubmissionClientPhase.Writing, "Invalid local input consumed chance");
        var request = client.CreateSubmission(" 빨간 과일 ", " a red fruit ");
        Require(request.Description == "빨간 과일" && client.RetrySubmission() == request, "Final text/retry changed");
        Require(client.TakeGenerationApproval() is null, "Generation before business approval");
        var response = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, request).Reply!);
        Require(Client(client, response, "p3") == RoundSyncStatus.WrongSender, "Forged approval");
        Require(Client(client, response with { RequestId = "old-request" }) == RoundSyncStatus.Stale, "Uncorrelated approval");
        Require(Client(client, response with { RoundId = Guid.NewGuid() }) == RoundSyncStatus.WrongRound, "Old response");
        Require(Client(client, response with { RecipientId = "p3" }) == RoundSyncStatus.WrongRecipient, "Wrong approval recipient");
        Require(Client(client, response with { Receipt = response.Receipt with { Approval = response.Receipt.Approval! with { Translation = "a green fruit" } } }) == RoundSyncStatus.InvalidMessage, "Changed approved text");
        Require(Client(client, response with { GenerationRemaining = TimeSpan.MaxValue }) == RoundSyncStatus.InvalidMessage, "Unbounded generation timer");
        Require(Client(client, response) == RoundSyncStatus.Accepted, "Approval rejected");
        var starts = new System.Collections.Concurrent.ConcurrentBag<GenerationApproval>();
        Parallel.For(0, 12, _ => { if (client.TakeGenerationApproval() is { } approval) starts.Add(approval); });
        Require(starts.Count == 1 && starts.Single().Translation == "a red fruit", "Duplicate generation or prompt injection");
        var repeat = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, request).Reply!);
        Require(repeat.Receipt.Status == SubmissionStatus.Duplicate && repeat.Receipt.Approval == response.Receipt.Approval, "Host generation token changed");
        Require(Client(client, repeat) == RoundSyncStatus.Duplicate && client.TakeGenerationApproval() is null, "Retransmitted approval restarted AI");
        Require(Client(client, response with { Receipt = response.Receipt with { Approval = response.Receipt.Approval! with { ApprovalId = Guid.NewGuid() } } }) == RoundSyncStatus.Conflict, "Approval replaced");
        var png = Png(66); var transfer = client.PrepareImage(png);
        Require(client.RetryImageBegin() == transfer, "Retry changed transfer identity");
        Reject(() => client.UploadPackets(png).ToArray());
        Require(world.Host.ReceiveImagePacket("p2", ImagePackets.Split(transfer, png).First()) == ImageTransferStatus.NotApproved, "PNG sent before permit");
        var permission = RoundControlMessages.Decode<ImageBeginReplyMessage>(Host(world, transfer).Reply!);
        Require(Client(client, permission with { TransferId = Guid.NewGuid() }) == RoundSyncStatus.Stale, "Unrelated image permit");
        Require(Client(client, permission with { Status = (ImageTransferStatus)900 }) == RoundSyncStatus.InvalidMessage, "Invalid image status");
        Require(Client(client, permission) == RoundSyncStatus.Accepted && client.Snapshot().CanSendImage, "Image permit rejected");
        int reserved = world.Host.ReservedImageBytes;
        Require(Client(client, RoundControlMessages.Decode<ImageBeginReplyMessage>(Host(world, transfer).Reply!)) == RoundSyncStatus.Duplicate
            && world.Host.ReservedImageBytes == reserved, "Repeated image begin allocated twice");
        foreach (var packet in client.UploadPackets(png)) world.Host.ReceiveImagePacket("p2", packet);
        Require(world.Host.Snapshot("p2").Players.Single(p => p.PlayerId == "p2").State == PlayerRoundState.Completed, "Uploaded PNG not accepted");
        client.MarkHostUnavailable();
        Require(!client.Snapshot().CanSendImage && client.TakeGenerationApproval() is null && Client(client, response) == RoundSyncStatus.Closed, "Disconnect resumed work");

        using var retry = world.Client("p3"); Client(retry, world.Host.StartMessage("p3"));
        var first = retry.CreateSubmission("빨간 과일", "a red fruit");
        var rejection = new SubmissionReplyMessage(world.Match, world.Round, 5, "p3", first.RequestId,
            new(SubmissionStatus.InvalidRequest, null), TimeSpan.Zero);
        Require(Client(retry, rejection) == RoundSyncStatus.Accepted && retry.Snapshot().Phase == SubmissionClientPhase.Writing, "Rejection did not permit correction");
        var second = retry.CreateSubmission("아삭한 과일", "a crunchy fruit");
        Require(first.RequestId != second.RequestId && Client(retry, rejection) == RoundSyncStatus.Stale, "Old rejection cleared new request");
    }

    private static void Deadlines()
    {
        using var world = new World(); using var client = world.Client();
        var start = world.Host.StartMessage("p2"); Client(client, start);
        world.Clock.Advance(TimeSpan.FromSeconds(15)); Client(client, start);
        Require(client.Snapshot().WritingRemaining == TimeSpan.FromSeconds(105), "Start retry extended writing");
        var request = client.CreateSubmission("빨간 과일", "a red fruit");
        var response = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, request).Reply!);
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(client.Snapshot() is { Phase: SubmissionClientPhase.AwaitingGallery, Failure: not null }
            && Client(client, response) == RoundSyncStatus.Closed && client.TakeGenerationApproval() is null, "Late approval started generation");

        using var generating = world.Client("p3"); Client(generating, world.Host.StartMessage("p3"));
        var r = generating.CreateSubmission("빨간 과일", "a red fruit");
        var approval = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, r, "p3").Reply!);
        Client(generating, approval); generating.TakeGenerationApproval();
        world.Clock.Advance(TimeSpan.FromSeconds(7)); Client(generating, approval);
        Require(generating.Snapshot().GenerationRemaining == TimeSpan.FromSeconds(173), "Approval replay extended generation");
        var transfer = generating.PrepareImage(Png(70));
        var permission = RoundControlMessages.Decode<ImageBeginReplyMessage>(Host(world, transfer, "p3").Reply!);
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(Client(generating, permission) == RoundSyncStatus.Closed && !generating.Snapshot().CanSendImage, "Late upload permit accepted");
        using var closed = world.Client("p1"); world.Clock.Advance(TimeSpan.FromSeconds(120));
        Client(closed, world.Host.StartMessage("p1"));
        Require(closed.Snapshot().Phase == SubmissionClientPhase.AwaitingGallery, "Expired start opened writing");

        using var transit = new World(); using var delayed = transit.Client();
        Client(delayed, transit.Host.StartMessage("p2"));
        var submitted = delayed.CreateSubmission("빨간 과일", "a red fruit");
        var delayedReply = RoundControlMessages.Decode<SubmissionReplyMessage>(Host(transit, submitted).Reply!) with { GenerationRemaining = TimeSpan.FromSeconds(1) };
        transit.Clock.Advance(TimeSpan.FromSeconds(2));
        Require(Client(delayed, delayedReply) == RoundSyncStatus.Accepted && delayed.TakeGenerationApproval() is null
            && delayed.Snapshot().Phase == SubmissionClientPhase.AwaitingGallery, "Expired-in-transit approval launched AI");
    }

    private static void Backpressure()
    {
        using var world = new World(); int handled = 0;
        using var endpoint = new ReliableControlEndpoint(world.Match, world.Round, "p2", (sender, bytes) =>
        { handled++; return world.Dispatch.Dispatch(sender, bytes); }, world.Clock);
        using var peer = new ReliableControlChannel(world.Match, world.Round, "p1", clock: world.Clock);
        for (int i = 0; i < 8; i++) Require(endpoint.Queue(world.Host.StartMessage("p2")) == ControlQueueStatus.Queued, "Queue fill failed");
        peer.TryQueue(RoundControlMessages.Encode(world.Request()), out _);
        for (int i = 0; i < 10 && endpoint.PendingReplyBytes == 0; i++)
        {
            foreach (var packet in peer.PollPackets()) endpoint.Receive("p2", packet);
            world.Clock.Advance(TimeSpan.FromMilliseconds(50));
        }
        Require(handled == 1 && endpoint.PendingReplyBytes > 0 && endpoint.Snapshot().LastReceivedSequence == 0, "Full queue lost response or ACKed early");
        int replies = 0;
        for (int i = 0; i < 120; i++)
        {
            foreach (var packet in endpoint.PollPackets()) peer.Receive("p1", packet);
            if (peer.TakeReceived() is { } m)
            { if (RoundControlMessages.Decode(m.CopyPayload()) is SubmissionReplyMessage) replies++; peer.ConfirmReceived(m.Sequence); }
            foreach (var packet in peer.PollPackets()) endpoint.Receive("p2", packet);
            if (endpoint.Snapshot().QueuedMessages == 0 && peer.Snapshot().QueuedMessages == 0) break;
            world.Clock.Advance(TimeSpan.FromMilliseconds(50));
        }
        Require(handled == 1 && replies == 1 && endpoint.PendingReplyBytes == 0 && endpoint.Snapshot() is { QueuedMessages: 0, LastReceivedSequence: 1, Failure: ControlChannelFailure.None }, "Backpressure reran/lost request");
        using var stalled = new ReliableControlEndpoint(world.Match, world.Round, "p2", (s, b) => world.Dispatch.Dispatch(s, b), world.Clock);
        using var stalledPeer = new ReliableControlChannel(world.Match, world.Round, "p1", clock: world.Clock);
        for (int i = 0; i < 8; i++) stalled.Queue(world.Host.StartMessage("p2"));
        stalledPeer.TryQueue(RoundControlMessages.Encode(world.Request()), out _);
        foreach (var packet in stalledPeer.PollPackets()) stalled.Receive("p2", packet);
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(stalled.Snapshot().Failure != ControlChannelFailure.None && stalled.PendingReplyBytes == 0, "Stalled pending reply retained memory");
    }

    private sealed class Link : IDisposable
    {
        public readonly ReliableControlEndpoint Host, Client;
        private readonly ControlTransportChecks.Clock clock;
        private readonly string player;
        private readonly HashSet<string> dropped = new();
        public int DroppedPackets => dropped.Count;
        public int Packets { get; private set; }
        public Link(World world, MultiplayerSubmissionClient client, string player)
        {
            clock = world.Clock; this.player = player;
            Host = new(world.Match, world.Round, player, (s, b) => world.Dispatch.Dispatch(s, b), clock);
            Client = new(world.Match, world.Round, "p1", (s, b) => client.Dispatch(s, b), clock);
        }
        private void Deliver(string direction, IReadOnlyList<byte[]> packets, ReliableControlEndpoint receiver, string sender)
        {
            foreach (var packet in packets.Reverse())
            {
                Packets++;
                string key = direction + packet[6] + ":" + BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(40, 8));
                if (dropped.Add(key)) continue; // First data AND first ACK per direction/sequence.
                receiver.Receive(sender, packet); receiver.Receive(sender, packet);
            }
        }
        public void Flush()
        {
            for (int tick = 0; tick < 190; tick++)
            {
                Step();
                var a = Host.Snapshot(); var b = Client.Snapshot();
                Require(a.Failure == ControlChannelFailure.None && b.Failure == ControlChannelFailure.None, "Dispatch lane timed out");
                if (a.QueuedMessages == 0 && b.QueuedMessages == 0 && Host.PendingReplyBytes == 0 && Client.PendingReplyBytes == 0) return;
                clock.Advance(TimeSpan.FromMilliseconds(50));
            }
            throw new InvalidOperationException("Dispatch lane failed to drain");
        }
        public void Step()
        {
            Deliver("h", Host.PollPackets(), Client, "p1");
            Deliver("c", Client.PollPackets(), Host, player);
            Require(Host.Snapshot().Failure == ControlChannelFailure.None && Client.Snapshot().Failure == ControlChannelFailure.None, "Image progress control lane failed");
        }
        public void Dispose() { Host.Dispose(); Client.Dispose(); }
    }
    private sealed record PlayReport(int GenerationStarts, int ControlPackets, int DroppedControlPackets, int UploadPackets,
        int GalleryPackets, int PngBytesPerClient, bool ResultsAgree, int DroppedImagePackets, int ConfirmedImageTransfers);
    private static PlayReport Play(byte[][] pngs, bool reliableImages = false)
    {
        using var world = new World();
        var clients = Players.Select(p => world.Client(p.Id)).ToArray();
        var links = clients.Select((c, i) => new Link(world, c, Players[i].Id)).ToArray();
        int generationStarts = 0, uploads = 0, galleryPackets = 0, confirmations = 0;
        var droppedImages = new HashSet<string>();
        bool Drop(string peer, byte[] packet)
        {
            int index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(24, 4));
            return index % 17 == 0 && droppedImages.Add(peer + new Guid(packet.AsSpan(8, 16)) + ":" + index);
        }
        try
        {
            for (int i = 0; i < 3; i++)
            {
                string player = Players[i].Id; var client = clients[i]; var link = links[i];
                link.Host.Queue(world.Host.StartMessage(player)); link.Flush();
                var request = client.CreateSubmission("아삭한 빨간 과일", "a crunchy red fruit");
                link.Client.Queue(request); link.Flush();
                Require(client.TakeGenerationApproval() is not null, "No AI approval after response"); generationStarts++;
                link.Client.Queue(request); link.Flush();
                Require(client.TakeGenerationApproval() is null, "Business retry regenerated");
                var descriptor = client.PrepareImage(pngs[i]);
                Reject(() => client.UploadPackets(pngs[i]).ToArray());
                link.Client.Queue(descriptor); link.Flush();
                if (reliableImages)
                {
                    Require(client.QueueImage(pngs[i]) == ImageQueueStatus.Queued, "Approved image queue failed");
                    for (int tick = 0; tick < 580 && client.Snapshot().UploadEnd is null; tick++)
                    {
                        foreach (var packet in client.PollImagePackets().Reverse())
                        {
                            uploads++; if (Drop("upload-" + player, packet.Bytes)) continue;
                            Require(world.Host.ReceiveImagePacket(player, packet.Bytes) is ImageTransferStatus.Accepted or ImageTransferStatus.Completed, "Recovered upload failed");
                            world.Host.ReceiveImagePacket(player, packet.Bytes);
                        }
                        client.PumpImageProgress((_, request) => link.Client.Queue(request)); link.Step();
                        world.Clock.Advance(TimeSpan.FromMilliseconds(50));
                    }
                    Require(client.Snapshot().UploadEnd == ImageSendEnd.Complete && client.ImageSendSnapshot().BufferedBytes == 0, "Upload completion not delivered");
                    confirmations++; link.Flush();
                }
                else foreach (var packet in client.UploadPackets(pngs[i]).Reverse())
                {
                    var status = world.Host.ReceiveImagePacket(player, packet); uploads++;
                    Require(status is ImageTransferStatus.Accepted or ImageTransferStatus.Completed, "Upload failed");
                    world.Host.ReceiveImagePacket(player, packet);
                }
            }
            var gallery = world.Host.Snapshot("p1").Gallery!.Manifest;
            for (int i = 0; i < 3; i++)
            {
                links[i].Host.Queue(gallery); links[i].Flush();
                foreach (var artwork in gallery.Artworks)
                {
                    if (reliableImages)
                    {
                        Require(world.Dispatch.QueueGalleryImage(Players[i].Id, artwork.Transfer.TransferId) == ImageQueueStatus.Queued, "Gallery image queue failed");
                        continue;
                    }
                    foreach (var packet in world.Host.GalleryPackets(Players[i].Id, artwork.Transfer.TransferId).Reverse())
                    { clients[i].Gallery.ReceiveGalleryPacket("p1", packet); galleryPackets++; clients[i].Gallery.ReceiveGalleryPacket("p1", packet); }
                }
            }
            if (reliableImages)
            {
                for (int tick = 0; tick < 1000 && confirmations < 12; tick++)
                {
                    foreach (var packet in world.Dispatch.Images.PollPackets().Reverse())
                    {
                        galleryPackets++; if (Drop("gallery-" + packet.PeerId, packet.Bytes)) continue;
                        var client = clients[Array.FindIndex(Players, p => p.Id == packet.PeerId)].Gallery;
                        Require(client.ReceiveGalleryPacket("p1", packet.Bytes) is RoundSyncStatus.Accepted or RoundSyncStatus.Complete, "Recovered gallery failed");
                        client.ReceiveGalleryPacket("p1", packet.Bytes);
                    }
                    world.Dispatch.Images.PumpProgressRequests((peer, request) => links[Array.FindIndex(Players, p => p.Id == peer)].Host.Queue(request));
                    foreach (var link in links) link.Step();
                    foreach (var delivery in world.Dispatch.Images.TakeResults()) { Require(delivery.End == ImageSendEnd.Complete, "Gallery transfer did not complete"); confirmations++; }
                    world.Clock.Advance(TimeSpan.FromMilliseconds(50));
                }
                Require(confirmations == 12 && world.Dispatch.Images.Snapshot().BufferedBytes == 0, "Gallery completion/buffer leak");
                foreach (var link in links) link.Flush();
            }
            for (int i = 0; i < 3; i++)
            {
                Require(clients[i].Gallery.ReadyMessage() is not null, "Gallery not complete");
                links[i].Client.Queue(clients[i].Gallery.ReadyMessage()!); links[i].Flush();
            }
            int[] targets = [1, 0, 1];
            for (int i = 0; i < 3; i++)
            {
                links[i].Host.Queue(world.Host.VotingState(Players[i].Id)!); links[i].Flush();
                var vote = clients[i].Gallery.CreateVote(gallery.Artworks[targets[i]].ArtworkId, "vote");
                links[i].Client.Queue(vote); links[i].Flush();
            }
            for (int i = 0; i < 3; i++) { links[i].Host.Queue(world.Host.VotingState(Players[i].Id)!); links[i].Flush(); }
            string result = JsonSerializer.Serialize(clients[0].Gallery.Snapshot().Result);
            bool agree = clients.All(c => JsonSerializer.Serialize(c.Gallery.Snapshot().Result) == result);
            Require(agree && clients[0].Gallery.Snapshot().Result!.Tallies.Select(t => t.Votes).SequenceEqual([1, 2, 0]), "Dispatched results mismatch");
            foreach (var client in clients)
            {
                Require(client.TakeGenerationApproval() is null && !client.Snapshot().CanSendImage, "Result resumed generation/upload");
                client.MarkHostUnavailable(); Require(JsonSerializer.Serialize(client.Gallery.Snapshot().Result) == result, "Disconnect lost result");
            }
            return new(generationStarts, links.Sum(l => l.Packets), links.Sum(l => l.DroppedPackets), uploads, galleryPackets, pngs.Sum(p => p.Length), agree, droppedImages.Count, confirmations);
        }
        finally { foreach (var link in links) link.Dispose(); foreach (var client in clients) client.Dispose(); }
    }

    private static void GenerationFailure()
    {
        using var world = new World(); using var client = world.Client();
        Client(client, world.Host.StartMessage("p2"));
        var submission = client.CreateSubmission("빨간 과일", "a red fruit");
        Client(client, RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, submission).Reply!));
        var approval = client.TakeGenerationApproval()!;
        Reject(() => client.CreateGenerationFailure(Guid.NewGuid()));
        var descriptor = client.PrepareImage(Png(88));
        Client(client, RoundControlMessages.Decode<ImageBeginReplyMessage>(Host(world, descriptor).Reply!));
        // Simulate failure during an allocated/incomplete image upload.
        Require(world.Host.BufferedImageBytes > 0, "Failure fixture missing receiver buffer");
        var failure = client.CreateGenerationFailure(approval.ApprovalId);
        Require(client.CreateGenerationFailure(approval.ApprovalId) == failure, "Failure retry changed context");
        Require(Host(world, failure with { PlayerId = "p3" }).Status == RoundSyncStatus.WrongSender, "Spoofed failure");
        Require(Host(world, failure with { RoundId = Guid.NewGuid() }).Status == RoundSyncStatus.WrongRound, "Stale failure");
        var reply = RoundControlMessages.Decode<GenerationFailureReply>(Host(world, failure).Reply!);
        Require(reply.Status == ImageTransferStatus.Accepted && world.Host.BufferedImageBytes == 0 && world.Host.ReservedImageBytes == 0, "Generation failure leaked image reservation");
        Require(Client(client, reply, "p3") == RoundSyncStatus.WrongSender, "Peer forged failure reply");
        Require(Client(client, reply) == RoundSyncStatus.Accepted && client.TakeGenerationApproval() is null, "Failure allowed regeneration");
        var duplicate = RoundControlMessages.Decode<GenerationFailureReply>(Host(world, failure).Reply!);
        Require(duplicate.Status == ImageTransferStatus.Duplicate && Client(client, duplicate) == RoundSyncStatus.Duplicate, "Repeated failure not idempotent");
        Require(world.Host.Submit("p2", submission with { RequestId = "retry-generation" }).Status == SubmissionStatus.AlreadyApproved, "Failure restored generation quota");

        using var timed = world.Client("p3"); Client(timed, world.Host.StartMessage("p3"));
        var next = timed.CreateSubmission("빨간 과일", "a red fruit");
        Client(timed, RoundControlMessages.Decode<SubmissionReplyMessage>(Host(world, next, "p3").Reply!));
        var a = timed.TakeGenerationApproval()!; var f = timed.CreateGenerationFailure(a.ApprovalId);
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(timed.Snapshot() is { Phase: SubmissionClientPhase.AwaitingGallery, Failure: not null }
            && Client(timed, RoundControlMessages.Decode<GenerationFailureReply>(Host(world, f, "p3").Reply!)) == RoundSyncStatus.Closed, "Late failure reply resumed work");
    }

    public static void CheckImageRecovery(string root)
    {
        string folder = Path.Combine(root, "outputs/scoring-removal/packaged-drawing");
        var pngs = new[] { "generated.png", "generated-repeat.png", "generated-revised.png" }.Select(n => File.ReadAllBytes(Path.Combine(folder, n))).ToArray();
        var report = Play(pngs, reliableImages: true); string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "image-recovery-round-check.json"), JsonSerializer.Serialize(new
        { transport = "in-memory, shared image rate budget, selective image loss plus control data/ACK loss; no EOS or AI execution", report }, RuntimeAssets.Json));
        Console.WriteLine($"Image recovery round: {report.DroppedImagePackets} image / {report.DroppedControlPackets} control drops, {report.ConfirmedImageTransfers} confirmed transfers, 3 matching vote results");
    }

    private static void EarlyGalleryCompletion()
    {
        using var world = new World(); using var client = world.Client(); using var link = new Link(world, client, "p2");
        link.Host.Queue(world.Host.StartMessage("p2")); link.Flush();
        link.Client.Queue(client.CreateSubmission("빨간 과일", "a red fruit")); link.Flush(); client.TakeGenerationApproval();
        var png = Png(91); link.Client.Queue(client.PrepareImage(png)); link.Flush(); client.QueueImage(png);
        foreach (var packet in client.PollImagePackets()) world.Host.ReceiveImagePacket("p2", packet.Bytes);
        Require(client.ImageSendSnapshot().BufferedBytes > 0 && client.Snapshot().UploadEnd is null, "Missing completion-race fixture");
        foreach (string id in new[] { "p1", "p3" })
        {
            var a = world.Host.Submit(id, world.Request(id)).Approval!; world.Host.ReportGenerationFailure(id, a.ApprovalId);
        }
        // A valid gallery can overtake our upload progress reply. Its matching
        // artwork hash/owner/approval is already proof of host collection.
        link.Host.Queue(world.Host.Snapshot("p2").Gallery!.Manifest); link.Flush();
        Require(client.Snapshot() is { UploadEnd: ImageSendEnd.Complete, Failure: null, CanSendImage: false }
            && client.ImageSendSnapshot().BufferedBytes == 0, "Early gallery falsely cancelled a successful upload");
    }

    private static void FailureThroughLoss()
    {
        using var world = new World(); using var client = world.Client(); using var link = new Link(world, client, "p2");
        link.Host.Queue(world.Host.StartMessage("p2")); link.Flush();
        link.Client.Queue(client.CreateSubmission("빨간 과일", "a red fruit")); link.Flush();
        var approval = client.TakeGenerationApproval()!; var message = client.CreateGenerationFailure(approval.ApprovalId);
        link.Client.Queue(message); link.Flush(); link.Client.Queue(message); link.Flush();
        Require(client.Snapshot() is { Phase: SubmissionClientPhase.AwaitingGallery, CanSendImage: false }
            && world.Host.Snapshot("p2").Players.Single(p => p.PlayerId == "p2").State == PlayerRoundState.GenerationFailed
            && world.Host.BufferedImageBytes == 0 && client.TakeGenerationApproval() is null, "Lost failure notification not committed/idempotent");
    }

    public static void CheckGeneratedImages(string root)
    {
        string folder = Path.Combine(root, "outputs/scoring-removal/packaged-drawing");
        var pngs = new[] { "generated.png", "generated-repeat.png", "generated-revised.png" }.Select(n => File.ReadAllBytes(Path.Combine(folder, n))).ToArray();
        var report = Play(pngs); string output = Path.Combine(root, "outputs/multiplayer"); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "round-dispatch-check.json"), JsonSerializer.Serialize(new
        { transport = "in-memory authenticated-peer simulation; packet/ACK loss on control only; no EOS or AI execution", report }, RuntimeAssets.Json));
        Console.WriteLine($"Round dispatch: {report.GenerationStarts} single generation approvals, {report.DroppedControlPackets} control drops recovered; {report.UploadPackets} upload / {report.GalleryPackets} gallery packets; three results agree");
    }
}
