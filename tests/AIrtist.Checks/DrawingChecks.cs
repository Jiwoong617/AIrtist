using AIrtist.Core;

internal static class DrawingChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> command)
    {
        try { await command(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid drawing command was accepted");
    }
    private static Task Prepare(DrawingController drawing, string text = "빨간 사과")
        => drawing.PrepareTranslationAsync(drawing.Snapshot.Version, text);
    private static Task Generate(DrawingController drawing, string text = "a red apple", int seed = 1)
        => drawing.GenerateAsync(drawing.Snapshot.Version, drawing.Snapshot.Draft!.Id, text, seed);

    public static async Task Run()
    {
        var ai = new FakeAi();
        using var drawing = new DrawingController(ai);
        foreach (string invalid in new[] { "", "a", "<|special|>", new string('가', 161) })
            await Reject(() => Prepare(drawing, invalid));
        Require(ai.Translated == 0, "Invalid descriptions reached translation");
        long editingVersion = drawing.Snapshot.Version;
        await Prepare(drawing, "  빨간 사과  ");
        var preview = drawing.Snapshot;
        Require(preview.Phase == DrawingPhase.Reviewing && preview.Draft!.Description == "빨간 사과"
            && preview.Result is null && ai.Generated == 0 && ai.Source == "빨간 사과", "Preview did not preserve free input");
        foreach (string invalid in new[] { "", "초록 사과", "<|test|>", new string('a', 321) })
            await Reject(() => Generate(drawing, invalid));
        await Reject(() => drawing.GenerateAsync(editingVersion, preview.Draft!.Id, "apple", 1));
        await Reject(() => drawing.GenerateAsync(preview.Version, preview.Draft!.Id + 1, "apple", 1));
        Require(ai.Generated == 0 && !drawing.Snapshot.Busy, "Invalid review consumed the operation");
        await Generate(drawing, "  a green apple  ", 2);
        var first = drawing.Snapshot.Result!;
        Require(drawing.Snapshot.Phase == DrawingPhase.Completed && first.Translation == "a green apple"
            && first.Seed == 2 && ai.Prompt == first.Translation && ai.Translated == 1, "Correction was retranslated or lost");
        await Reject(() => drawing.GenerateAsync(preview.Version, preview.Draft!.Id, "apple", 3));
        await Generate(drawing, "a green apple", 3);
        Require(ai.Generated == 2 && ai.Translated == 1 && !ReferenceEquals(first, drawing.Snapshot.Result), "Repeat was limited by a round or retranslated");
        drawing.Review(drawing.Snapshot.Version);
        Require(drawing.Snapshot.Draft!.Translation == "a green apple", "Review lost confirmed text");
        long staleDraft = drawing.Snapshot.Draft.Id;
        drawing.Revise(drawing.Snapshot.Version, staleDraft);
        await Prepare(drawing, "흰 토끼");
        Require(drawing.Snapshot.Draft!.Id != staleDraft, "Revision reused a stale draft");
        await Reject(() => drawing.GenerateAsync(drawing.Snapshot.Version, staleDraft, "rabbit", 4));
        await Generate(drawing, "a white rabbit", 4);
        Require(drawing.Snapshot.Result!.Description == "흰 토끼", "Revised source was not preserved");

        await Failures();
        await Cancellation();
        await TimeoutAndIdle();
        Console.WriteLine("Drawing: direct names, review/revision, repeat, stale/duplicate commands, failure, cancellation, timeout and disposal passed");
    }

    private static async Task Failures()
    {
        var ai = new FakeAi { FailTranslation = true };
        using var drawing = new DrawingController(ai);
        await Prepare(drawing);
        Require(drawing.Snapshot.Phase == DrawingPhase.Editing && !drawing.Snapshot.Busy && drawing.Snapshot.Draft is null, "Translation failure did not recover");
        ai.FailTranslation = false;
        await Prepare(drawing);
        ai.FailGeneration = true;
        await Generate(drawing, "a yellow banana");
        Require(drawing.Snapshot.Phase == DrawingPhase.Reviewing && drawing.Snapshot.Draft!.Translation == "a yellow banana"
            && drawing.Snapshot.Result is null && !drawing.Snapshot.Busy, "Generation failure lost the correction or retry");
        ai.FailGeneration = false;
        await Generate(drawing, "a yellow banana");
        Require(drawing.Snapshot.Phase == DrawingPhase.Completed, "Retry after failure failed");
    }

    private static async Task Cancellation()
    {
        var gate = new Gate();
        var ai = new FakeAi { TranslationGate = gate };
        using var drawing = new DrawingController(ai);
        Task pending = Prepare(drawing);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Reject(() => Prepare(drawing, "다른 그림"));
            long operation = drawing.Snapshot.Version;
            drawing.Cancel(operation - 1);
            Require(!pending.IsCompleted && drawing.Snapshot.Busy, "Old cancellation affected the current operation");
            drawing.Cancel(operation);
            await Reject(() => Prepare(drawing));
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
        Require(drawing.Snapshot.Phase == DrawingPhase.Editing && drawing.Snapshot.Draft is null && !drawing.Snapshot.Busy,
            "Late cancelled translation was accepted");
        ai.TranslationGate = null;
        await Prepare(drawing);
        await Generate(drawing);
        var previous = drawing.Snapshot.Result;
        gate = new Gate();
        ai.GenerationGate = gate;
        pending = Generate(drawing, "a blue car");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Action<string> lateProgress = ai.Progress!;
        try
        {
            await Reject(() => Generate(drawing));
            drawing.Cancel(drawing.Snapshot.Version);
            await Reject(() => Generate(drawing));
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
        Require(drawing.Snapshot.Phase == DrawingPhase.Reviewing && ReferenceEquals(previous, drawing.Snapshot.Result)
            && drawing.Snapshot.Draft!.Translation == "a blue car", "Cancelled generation replaced the previous image or lost the correction");
        ai.GenerationGate = null;
        await Generate(drawing, "a blue car", 5);
        var completed = drawing.Snapshot;
        lateProgress("stale progress");
        Require(drawing.Snapshot.Message == completed.Message && drawing.Snapshot.Result!.Seed == 5, "Late progress affected the new image");

        gate = new Gate();
        ai.GenerationGate = gate;
        pending = Generate(drawing);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        drawing.Dispose();
        gate.Release.TrySetResult();
        await pending;
        try { _ = drawing.Snapshot; throw new InvalidOperationException("Disposed drawing retained access"); }
        catch (ObjectDisposedException) { }
    }

    private static async Task TimeoutAndIdle()
    {
        var ai = new FakeAi();
        using var drawing = new DrawingController(ai, TimeSpan.FromMilliseconds(200));
        await Prepare(drawing);
        var review = drawing.Snapshot;
        await Task.Delay(250); // No writing/review clock: an idle review outlives the AI timeout.
        Require(drawing.Snapshot.Version == review.Version && drawing.Snapshot.Phase == DrawingPhase.Reviewing, "Idle review expired");
        ai.ReturnAfterTimeout = true;
        await Generate(drawing);
        Require(drawing.Snapshot.Phase == DrawingPhase.Reviewing && drawing.Snapshot.Result is null && !drawing.Snapshot.Busy
            && drawing.Snapshot.Message.Contains("제한 시간"), "Late timed-out generation was accepted");
        ai.ReturnAfterTimeout = false;
        await Generate(drawing);
        Require(drawing.Snapshot.Phase == DrawingPhase.Completed, "Retry after timeout failed");
    }

    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Wait() { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); }
    }

    private sealed class FakeAi : IDrawingAi
    {
        public int Translated, Generated;
        public bool FailTranslation, FailGeneration, ReturnAfterTimeout;
        public string? Source, Prompt;
        public Gate? TranslationGate, GenerationGate;
        public Action<string>? Progress;
        public string Translate(string description, CancellationToken token)
        {
            Interlocked.Increment(ref Translated);
            Source = description;
            TranslationGate?.Wait(); // Deliberately ignores cancellation to check late-result rejection.
            if (FailTranslation) throw new InvalidOperationException("fake translation failure");
            return "a red apple";
        }
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        {
            Interlocked.Increment(ref Generated);
            Prompt = translation;
            Progress = progress;
            GenerationGate?.Wait();
            if (ReturnAfterTimeout && !token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Missing timeout");
            if (FailGeneration) throw new InvalidOperationException("fake generation failure");
            progress("fake image ready");
            return [(byte)seed];
        }
    }
}
