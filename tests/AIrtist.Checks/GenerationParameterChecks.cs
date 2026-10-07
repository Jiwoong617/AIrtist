using System.IO;
using System.Text.Json;
using AIrtist.Core;

internal static class GenerationParameterChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid generation parameters accepted");
    }
    public static async Task Run(string root)
    {
        foreach (var value in new[] { new GenerationParameters(float.NaN), new(float.PositiveInfinity), new(0), new(21),
            new(7, 0), new(7, 51), new(Seed: -1) }) Reject(value.Validate);
        new GenerationParameters(1, 1, 0).Validate(); new GenerationParameters(20, 50, int.MaxValue).Validate();
        var random = new GenerationParameters(5.5f, 12);
        Require(random.Accepts(random.Resolve()) && !random.Accepts(new(5.6f, 12, 42)), "Random resolution changed settings");
        var baseline = JsonSerializer.Deserialize<SchedulerData>(File.ReadAllText(Path.Combine(root, "models/app-stage3/generation/scheduler.json")), RuntimeAssets.Json)!;
        var generated = DpmScheduler.CreateSchedule(25);
        for (int i = 0; i < 25; i++)
        {
            var a = baseline.Steps[i]; var b = generated.Steps[i];
            Require(a.Timestep == b.Timestep, "DPM timesteps differ from pinned reference");
            var av = new[] { a.Alpha, a.Sigma, a.Sample, a.Current, a.Previous };
            var bv = new[] { b.Alpha, b.Sigma, b.Sample, b.Current, b.Previous };
            Require(av.Zip(bv).All(v => Math.Abs(v.First - v.Second) < 0.00003), "DPM coefficients differ from pinned reference");
        }
        foreach (int count in Enumerable.Range(1, 50))
        {
            var scheduler = new DpmScheduler(DpmScheduler.CreateSchedule(count));
            float[] sample = [0.2f, -0.4f];
            for (int i = 0; i < count; i++)
            {
                Require(i == 0 || scheduler.Steps[i].Timestep < scheduler.Steps[i - 1].Timestep, "Steps must descend");
                sample = scheduler.Step(i, sample, [0.01f, -0.02f]);
                Require(sample.All(float.IsFinite), "Non-finite DPM output");
            }
            Require(scheduler.Steps[^1].Sample == 0 && scheduler.Steps[^1].Current == 1, "Terminal sigma is not zero");
        }
        var ai = new RecordingAi();
        using (var drawing = new DrawingController(new SerializedDrawingAi(ai)))
        {
            await drawing.PrepareTranslationAsync(drawing.Snapshot.Version, "a red fruit");
            var draft = drawing.Snapshot.Draft!;
            await drawing.GenerateAsync(drawing.Snapshot.Version, draft.Id, draft.Translation, random);
            Require(drawing.Snapshot.Result?.Parameters == ai.Received.Single() && ai.Received[0].Steps == 12,
                "Drawing/serialized AI dropped selected parameters");
        }
        ApprovalPolicies();
        await Runtime(false); await Runtime(true); await Runtime(false, true); await Runtime(true, true);
        Console.WriteLine("Parameters: bounds, 1..50 schedules, pinned 25-step reference, drawing propagation, host locks/random/replay, tampered approvals and two-peer runtime passed");
    }
    private static readonly MultiplayerCompatibility Compatibility = new(1, MultiplayerCompatibility.CurrentRules, new('A', 64), new('B', 64));
    private static readonly Word Word = new("apple", "사과", "과일", "apple", ["사과", "apple"]);
    private static readonly VotingPlayer[] Players = [new("p1", "방장"), new("p2", "참가자")];
    private static readonly MultiplayerParticipant[] Members = Players.Select(p => new MultiplayerParticipant(p, Compatibility)).ToArray();
    private static void ApprovalPolicies()
    {
        var fixedValues = new GenerationParameters(4.5f, 9, 1234);
        foreach (bool locked in new[] { false, true })
        {
            Guid match = Guid.NewGuid(), round = Guid.NewGuid();
            var policy = MultiplayerRoundPolicy.Default with { FixedParameters = locked ? fixedValues : null };
            using var host = new MultiplayerRoundHost(match, round, 1, "p1", Word, Compatibility, Members, policy);
            using var client = new MultiplayerSubmissionClient(match, round, 1, "p1", "p2", Players, Compatibility, [Word]);
            client.Dispatch("p1", RoundControlMessages.Encode(host.StartMessage("p2")));
            if (locked) Reject(() => client.CreateSubmission("빨간 과일", "a red fruit", new()));
            Require(client.Snapshot().Phase == SubmissionClientPhase.Writing, "Invalid parameter consumed submission");
            var request = client.CreateSubmission("빨간 과일", "a red fruit", locked ? fixedValues : new(6.25f, 8));
            Require(host.Submit("p2", request with { Parameters = new(Steps: 51) }).Status == SubmissionStatus.InvalidRequest, "Host allowed invalid range");
            if (locked)
                Require(host.Submit("p2", request with { Parameters = fixedValues with { Seed = 99 } }).Status == SubmissionStatus.InvalidRequest, "Host allowed fixed-setting override");
            var receipt = host.Submit("p2", request);
            Require(receipt.Status == SubmissionStatus.Accepted && request.Parameters.Accepts(receipt.Approval!.Parameters), "Host dropped parameters");
            Require(host.Submit("p2", request).Approval == receipt.Approval, "Retry rerolled seed");
            Require(host.Submit("p2", request with { Parameters = request.Parameters with { Guidance = 8 } }).Status == SubmissionStatus.RequestConflict,
                "Retry changed parameters");
            var reply = new SubmissionReplyMessage(match, round, 1, "p2", request.RequestId, receipt, TimeSpan.FromSeconds(170));
            var forged = reply with { Receipt = receipt with { Approval = receipt.Approval! with { Parameters = new(20, 50, 5) } } };
            Require(client.Dispatch("p1", RoundControlMessages.Encode(forged)).Status == RoundSyncStatus.InvalidMessage, "Client accepted changed approval parameters");
            Require(client.Dispatch("p1", RoundControlMessages.Encode(reply)).Status == RoundSyncStatus.Accepted, "Valid approval rejected");
            Require(client.TakeGenerationApproval()?.Parameters == receipt.Approval!.Parameters && client.TakeGenerationApproval() is null,
                "Approval settings missing or AI starts twice");
        }
        Require(!(MultiplayerMatchSettings.Default with { RoundPolicy = MultiplayerRoundPolicy.Default with { FixedParameters = new() } }).IsValid(),
            "Fixed room allowed random seed");
    }
    private sealed class RecordingAi : IDrawingAi
    {
        public readonly List<GenerationParameters> Received = new();
        public string Translate(string text, CancellationToken token) => "a red fruit";
        public byte[] Generate(string text, int seed, CancellationToken token, Action<string> progress)
            => throw new InvalidOperationException("Legacy path silently dropped parameters");
        public byte[] Generate(string text, GenerationParameters parameters, CancellationToken token, Action<string> progress)
        { lock (Received) Received.Add(parameters); return [1]; } // This test stops at AI handoff, before PNG validation.
    }
    private static async Task Runtime(bool locked, bool oneClick = false)
    {
        var fixedValues = new GenerationParameters(8.5f, 17, 4321);
        var selected = new GenerationParameters(3.5f, 6, 15);
        var network = new LocalRoundNetwork(); var ai = new RecordingAi();
        Guid match = Guid.NewGuid(), round = Guid.NewGuid();
        var runners = Players.Select(p => new MultiplayerRoundRuntime(new(match, round, 1, "p1", p.Id, Compatibility, Members),
            [Word], ai, network.Connect(p.Id), "apple", MultiplayerRoundPolicy.Default with { FixedParameters = locked ? fixedValues : null })).ToArray();
        try
        {
            async Task Until(Func<bool> done)
            {
                for (int i = 0; i < 500; i++)
                {
                    foreach (var r in runners) r.Tick();
                    if (done()) return;
                    await Task.Delay(5);
                }
                throw new InvalidOperationException("Parameter runtime timeout");
            }
            var guest = runners[1];
            await Until(() => guest.Snapshot().Submission.Phase == SubmissionClientPhase.Writing);
            if (oneClick) guest.Draw("빨간 과일", locked ? fixedValues : selected);
            else
            {
                guest.PrepareTranslation("빨간 과일");
                await Until(() => guest.Snapshot().AiPhase == RoundAiPhase.Reviewing);
                guest.Confirm(guest.Snapshot().Draft!.Id, "a red fruit", locked ? fixedValues : selected);
            }
            await Until(() => { lock (ai.Received) return ai.Received.Count == 1; });
            Require(ai.Received.Single() == (locked ? fixedValues : selected), "Network/runtime did not use approved parameters");
        }
        finally { foreach (var runner in runners) runner.Dispose(); }
    }
}
