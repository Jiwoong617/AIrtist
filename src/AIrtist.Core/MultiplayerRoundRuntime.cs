namespace AIrtist.Core;

public sealed record RoundRuntimeContext(Guid MatchId, Guid RoundId, long SubmissionVersion, string HostId, string LocalId,
    MultiplayerCompatibility Compatibility, IReadOnlyList<MultiplayerParticipant> Participants);
public enum RoundAiPhase { Editing, Translating, Reviewing, WaitingApproval, Generating, Submitted }
public sealed record RoundDraft(long Id, string Description, string Translation);
public sealed record RoundRuntimeSnapshot(string LocalId, bool IsHost, SubmissionClientSnapshot Submission,
    RoundClientSnapshot Gallery, RoundAiPhase AiPhase, bool Busy, RoundDraft? Draft, string Message, byte[]? OwnPng);

// One local participant in one round. Tick is the single packet/timer/state loop;
// public commands/snapshots are serialized against it. AI runs off that loop and
// only its still-current, uncancelled completion can submit bytes. This class
// owns its transport and all round objects, but not the shared local AI service.
public sealed class MultiplayerRoundRuntime : IDisposable
{
    private readonly object sync = new();
    private readonly RoundRuntimeContext context;
    private readonly IDrawingAi ai;
    private readonly IRoundTransport transport;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Word> words;
    private readonly MultiplayerSubmissionClient client;
    private readonly ReliableControlEndpoint clientEndpoint;
    private readonly MultiplayerRoundHost? host;
    private readonly HostControlDispatcher? hostDispatch;
    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private readonly Queue<RoundInboundPacket> loopback = new();
    private byte[]? pendingCommand, ownPng;
    private bool imageQueued, readyQueued, disconnected, disposed;
    private RoundDraft? draft;
    private long draftId, progressRevision;
    private PlayerRoundProgress[] lastProgress = [];
    private MultiplayerRoundPhase? lastHostPhase;
    private RoundAiPhase aiPhase;
    private string message = "경기 시작을 기다리고 있어요";
    private int seed;
    private GenerationApproval? generation;
    private Job? job;
    private sealed class Peer(ReliableControlEndpoint endpoint)
    {
        public readonly ReliableControlEndpoint Endpoint = endpoint;
        public bool StartSent, StoppedSent, Disconnected;
        public ulong GallerySequence;
        public long ProgressRevision, VotingRevision;
        public readonly HashSet<Guid> ImagesQueued = new();
    }
    private sealed class Job(bool generating, long started, TimeSpan budget)
    {
        public readonly bool Generating = generating;
        public readonly long Started = started;
        public readonly TimeSpan Budget = budget;
        public readonly CancellationTokenSource Cancel = new();
        public Task<Outcome> Work = null!;
    }
    private sealed record Outcome(string? Translation, byte[]? Png, Exception? Error);

    public MultiplayerRoundRuntime(RoundRuntimeContext context, IEnumerable<Word> words, IDrawingAi ai, IRoundTransport transport,
        string? hostWordId = null, MultiplayerRoundPolicy? policy = null, TimeProvider? clock = null)
    {
        if (transport.LocalId != context.LocalId) throw new ArgumentException("로컬 연결 ID가 참가자와 다릅니다.");
        context.Compatibility.Validate();
        var participants = context.Participants.Take(17).ToArray();
        if (participants.Any(p => p?.Player is null || !context.Compatibility.Matches(p.Compatibility))) throw new ArgumentException("참가자 호환성이 다릅니다.");
        this.context = context with { Participants = Array.AsReadOnly(participants) };
        this.words = words.ToDictionary(w => w.Id, w => w with { Aliases = (string[])w.Aliases.Clone() });
        this.ai = ai; this.transport = transport; this.clock = clock ?? TimeProvider.System;
        client = new(context.MatchId, context.RoundId, context.SubmissionVersion, context.HostId, context.LocalId,
            participants.Select(p => p.Player), context.Compatibility, this.words.Values, this.clock);
        clientEndpoint = new(context.MatchId, context.RoundId, context.HostId, (s, b) => client.Dispatch(s, b), this.clock);
        if (context.LocalId == context.HostId)
        {
            if (hostWordId is null || !this.words.TryGetValue(hostWordId, out var word)) throw new ArgumentException("방장이 제시어를 선택해야 합니다.");
            host = new(context.MatchId, context.RoundId, context.SubmissionVersion, context.HostId, word, context.Compatibility, participants, policy, this.clock);
            hostDispatch = new(host, context.HostId, this.clock);
            foreach (var participant in participants)
                peers.Add(participant.Player.Id, new(new(context.MatchId, context.RoundId, participant.Player.Id,
                    (s, b) => hostDispatch.Dispatch(s, b), this.clock)));
        }
    }

    public void PrepareTranslation(string description)
    {
        lock (sync)
        {
            CheckAlive(); var state = client.Snapshot();
            if (job is not null || state.Phase != SubmissionClientPhase.Writing) throw new InvalidOperationException("작성 시간 안에 이전 작업이 끝난 뒤 번역해 주세요.");
            string input = InputRules.Validate(description, words[state.Prompt!.Id]); draft = new(++draftId, input, "");
            aiPhase = RoundAiPhase.Translating; message = "영어 묘사를 준비하고 있어요";
            StartJob(false, Min(state.WritingRemaining, TimeSpan.FromMinutes(3)), token => new(ai.Translate(input, token), null, null));
        }
    }
    public void Revise(long expectedDraftId)
    {
        lock (sync)
        {
            CheckDraft(expectedDraftId); draft = null; aiPhase = RoundAiPhase.Editing; message = "원문을 고쳐 주세요. 작성 시간은 계속 흐릅니다.";
        }
    }
    public void Confirm(long expectedDraftId, string translation, int seed)
    {
        lock (sync)
        {
            CheckDraft(expectedDraftId);
            var request = client.CreateSubmission(draft!.Description, translation);
            draft = draft with { Translation = request.Translation }; this.seed = seed;
            pendingCommand = RoundControlMessages.Encode(request); aiPhase = RoundAiPhase.WaitingApproval; message = "방장의 제출 승인을 기다리고 있어요";
        }
    }
    public void Vote(string artworkId)
    {
        lock (sync)
        {
            CheckAlive();
            if (pendingCommand is not null) throw new InvalidOperationException("이전 요청을 보내고 있어요.");
            pendingCommand = RoundControlMessages.Encode(client.Gallery.CreateVote(artworkId, Guid.NewGuid().ToString("N")));
        }
    }
    public void CloseVoting()
    {
        lock (sync) { CheckAlive(); if (host is null) throw new UnauthorizedAccessException("방장만 마감할 수 있습니다."); host.CloseVoting(context.LocalId); }
    }
    public void CancelAi()
    {
        lock (sync)
        {
            CheckAlive(); if (job is null) return; job.Cancel.Cancel(); message = "작업을 취소하고 정리하고 있어요";
            if (job.Generating) ReportFailure();
        }
    }

    public void Tick()
    {
        lock (sync)
        {
            CheckAlive();
            for (int n = 0; n < 128 && loopback.Count > 0; n++) Receive(loopback.Dequeue());
            foreach (var packet in transport.Receive(128)) Receive(packet);
            if (!disconnected && context.LocalId != context.HostId && !transport.IsConnected(context.HostId)) DisconnectClient();
            if (!disconnected && clientEndpoint.Snapshot().Failure != ControlChannelFailure.None) DisconnectClient();
            ScheduleHost();
            var state = client.Snapshot();
            if (job is not null)
            {
                bool valid = job.Generating ? state.Phase == SubmissionClientPhase.Generating : state.Phase == SubmissionClientPhase.Writing;
                if (!valid || clock.GetElapsedTime(job.Started) >= job.Budget) job.Cancel.Cancel();
                FinishJob();
            }
            if (job is null && !disconnected && client.TakeGenerationApproval() is { } approval)
            {
                generation = approval; aiPhase = RoundAiPhase.Generating; message = "승인된 묘사로 그림을 그리고 있어요";
                StartJob(true, client.Snapshot().GenerationRemaining, token => new(null,
                    ai.Generate(approval.Translation, seed, token, text => { lock (sync) { if (!disposed && job is not null && !job.Cancel.IsCancellationRequested) message = text; } }), null));
            }
            if (!disconnected)
            {
                if (pendingCommand is not null && clientEndpoint.QueueEncoded(pendingCommand) == ControlQueueStatus.Queued) pendingCommand = null;
                if (!imageQueued && ownPng is not null && client.Snapshot().CanSendImage)
                {
                    var status = client.QueueImage(ownPng);
                    imageQueued = status is ImageQueueStatus.Queued or ImageQueueStatus.Duplicate;
                }
                if (!readyQueued && client.Gallery.ReadyMessage() is { } ready)
                    readyQueued = clientEndpoint.Queue(ready) == ControlQueueStatus.Queued;
                client.PumpImageProgress((_, request) => clientEndpoint.Queue(request));
                foreach (var packet in clientEndpoint.PollPackets()) Send(context.HostId, RoundPacketKind.ControlToHost, packet);
                foreach (var packet in client.PollImagePackets()) Send(packet.PeerId, RoundPacketKind.ImageToHost, packet.Bytes);
            }
            if (hostDispatch is not null)
            {
                hostDispatch.Images.PumpProgressRequests((peer, request) => peers[peer].Disconnected ? ControlQueueStatus.Closed : peers[peer].Endpoint.Queue(request));
                foreach (var pair in peers.Where(p => !p.Value.Disconnected))
                    foreach (var packet in pair.Value.Endpoint.PollPackets()) Send(pair.Key, RoundPacketKind.ControlToClient, packet);
                foreach (var packet in hostDispatch.Images.PollPackets()) Send(packet.PeerId, RoundPacketKind.ImageToClient, packet.Bytes);
                hostDispatch.Images.TakeResults(); // Core gallery deadline decides whether all receivers made it.
            }
        }
    }

    private void ScheduleHost()
    {
        if (host is null) return;
        var state = host.Snapshot(context.LocalId);
        if (lastHostPhase != state.Phase || !lastProgress.SequenceEqual(state.Players))
        { progressRevision++; lastHostPhase = state.Phase; lastProgress = state.Players.ToArray(); }
        foreach (var pair in peers)
        {
            string id = pair.Key; var peer = pair.Value;
            if (peer.Disconnected) continue;
            if ((id != context.LocalId && !transport.IsConnected(id)) || peer.Endpoint.Snapshot().Failure != ControlChannelFailure.None)
            { peer.Disconnected = true; peer.Endpoint.Disconnect(); hostDispatch!.Images.DisconnectPeer(id); continue; }
            if (!peer.StartSent) peer.StartSent = peer.Endpoint.Queue(host.StartMessage(id)) == ControlQueueStatus.Queued;
            if (!peer.StartSent) continue;
            if (peer.ProgressRevision < progressRevision && peer.Endpoint.Queue(new RoundProgressMessage(context.MatchId, context.RoundId,
                context.SubmissionVersion, id, progressRevision, state.Phase, state.Players, state.WritingRemaining)) == ControlQueueStatus.Queued)
                peer.ProgressRevision = progressRevision;
            if (state.Gallery is { } gallery)
            {
                if (peer.GallerySequence == 0 && peer.Endpoint.Queue(gallery.Manifest, out ulong sequence) == ControlQueueStatus.Queued) peer.GallerySequence = sequence;
                if (peer.GallerySequence != 0 && peer.Endpoint.Snapshot().LastAcknowledgedSequence >= peer.GallerySequence && !gallery.TimedOut && gallery.Remaining > TimeSpan.Zero)
                    foreach (var artwork in gallery.Manifest.Artworks)
                    {
                        if (peer.ImagesQueued.Contains(artwork.Transfer.TransferId)) continue;
                        ImageQueueStatus queued;
                        try { queued = hostDispatch!.QueueGalleryImage(id, artwork.Transfer.TransferId); }
                        catch (InvalidOperationException) { break; } // Gallery can reach its deadline during this tick.
                        if (queued == ImageQueueStatus.Backpressure) break;
                        peer.ImagesQueued.Add(artwork.Transfer.TransferId);
                    }
                if (!peer.StoppedSent && host.GalleryFailureState(id) is { } stop) peer.StoppedSent = peer.Endpoint.Queue(stop) == ControlQueueStatus.Queued;
            }
            bool readyForVoting = state.Gallery?.ReadyPlayerIds.Contains(id) == true
                && (id != context.LocalId || client.Gallery.ReadyMessage() is not null);
            if (readyForVoting && host.VotingState(id) is { } voting && voting.Revision > peer.VotingRevision
                && peer.Endpoint.Queue(voting) == ControlQueueStatus.Queued) peer.VotingRevision = voting.Revision;
        }
    }

    private void Receive(RoundInboundPacket packet)
    {
        if (packet.Bytes is null || packet.Bytes.Length is 0 or > 1024) return;
        switch (packet.Kind)
        {
            case RoundPacketKind.ControlToHost when host is not null && peers.TryGetValue(packet.AuthenticatedSender, out var peer) && !peer.Disconnected:
                peer.Endpoint.Receive(packet.AuthenticatedSender, packet.Bytes); break;
            case RoundPacketKind.ImageToHost when host is not null && peers.TryGetValue(packet.AuthenticatedSender, out var imagePeer) && !imagePeer.Disconnected:
                host.ReceiveImagePacket(packet.AuthenticatedSender, packet.Bytes); break;
            case RoundPacketKind.ControlToClient when !disconnected && packet.AuthenticatedSender == context.HostId:
                clientEndpoint.Receive(packet.AuthenticatedSender, packet.Bytes); break;
            case RoundPacketKind.ImageToClient when !disconnected && packet.AuthenticatedSender == context.HostId:
                client.Gallery.ReceiveGalleryPacket(packet.AuthenticatedSender, packet.Bytes); break;
        }
    }
    private void Send(string peer, RoundPacketKind kind, byte[] bytes)
    {
        if (peer == context.LocalId)
        {
            if (loopback.Count < LocalRoundNetwork.MaxQueuedPackets) loopback.Enqueue(new(context.LocalId, kind, bytes));
        }
        else transport.TrySend(peer, kind, bytes);
    }
    private void StartJob(bool generating, TimeSpan budget, Func<CancellationToken, Outcome> work)
    {
        var next = new Job(generating, clock.GetTimestamp(), budget); job = next;
        next.Work = Task.Run(() => { try { return work(next.Cancel.Token); } catch (Exception ex) { return new Outcome(null, null, ex); } });
    }
    private void FinishJob()
    {
        if (job is null || !job.Work.IsCompleted) return;
        var finished = job; var outcome = finished.Work.GetAwaiter().GetResult();
        bool cancelled = finished.Cancel.IsCancellationRequested; job = null; finished.Cancel.Dispose();
        if (cancelled || outcome.Error is not null || disconnected)
        {
            message = cancelled ? "작업이 취소되었거나 제한 시간이 지났습니다." : "AI 작업에 실패했습니다. GPU 상태와 입력을 확인해 주세요.";
            if (finished.Generating) { ReportFailure(); aiPhase = RoundAiPhase.Submitted; }
            else { draft = null; aiPhase = RoundAiPhase.Editing; }
            return;
        }
        if (!finished.Generating)
        {
            draft = draft! with { Translation = outcome.Translation! }; aiPhase = RoundAiPhase.Reviewing;
            message = "번역을 확인·수정한 뒤 최종 제출해 주세요";
        }
        else
        {
            try
            {
                // Validate before retaining/uploading an AI output, including decoder work.
                var artwork = new VotingArtwork(generation!.ApprovalId.ToString("N"), context.LocalId, outcome.Png!);
                var descriptor = client.PrepareImage(artwork.CopyPng());
                ownPng = artwork.CopyPng(); pendingCommand = RoundControlMessages.Encode(descriptor);
                aiPhase = RoundAiPhase.Submitted; message = "그림을 방장에게 전달하고 있어요";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { ReportFailure(); message = "그림을 제출하지 못했습니다. 다른 참가자를 기다립니다."; aiPhase = RoundAiPhase.Submitted; }
        }
    }
    private void ReportFailure()
    {
        if (generation is null || disconnected) return;
        try { pendingCommand = RoundControlMessages.Encode(client.CreateGenerationFailure(generation.ApprovalId)); }
        catch (InvalidOperationException) { } // Host/local deadline already closed this generation.
    }
    private void DisconnectClient()
    {
        disconnected = true; pendingCommand = null; clientEndpoint.Disconnect(); client.MarkHostUnavailable();
        job?.Cancel.Cancel(); message = "방장 연결이 끊어졌습니다. 확정된 결과는 유지됩니다.";
    }
    public RoundRuntimeSnapshot Snapshot()
    {
        lock (sync) { CheckAlive(); return new(context.LocalId, host is not null, client.Snapshot(), client.Gallery.Snapshot(), aiPhase, job is not null, draft, message, ownPng); }
    }
    private void CheckDraft(long id)
    {
        CheckAlive(); if (job is not null || aiPhase != RoundAiPhase.Reviewing || draft?.Id != id || client.Snapshot().Phase != SubmissionClientPhase.Writing)
            throw new InvalidOperationException("작성 시간 안에 현재 번역을 확인해 주세요.");
    }
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private void CheckAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            if (job is { } active) { active.Cancel.Cancel(); _ = active.Work.ContinueWith(_ => active.Cancel.Dispose(), TaskScheduler.Default); job = null; }
            foreach (var peer in peers.Values) peer.Endpoint.Dispose(); clientEndpoint.Dispose(); hostDispatch?.Dispose(); host?.Dispose(); client.Dispose(); transport.Dispose();
            loopback.Clear(); pendingCommand = ownPng = null; draft = null;
        }
    }
}
