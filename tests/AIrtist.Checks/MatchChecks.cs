using AIrtist.Core;
using System.IO;

internal static class MatchChecks
{
    private static readonly Word[] Words = [new("apple", "사과", "과일", "apple", ["사과", "apple"]), new("rabbit", "토끼", "동물", "rabbit", ["토끼", "rabbit"]), new("cup", "컵", "용기", "cup", ["컵", "cup"])];
    private static void Require(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid command was accepted");
    }
    private static Task Command(Action action) { action(); return Task.CompletedTask; }

    public static async Task Run()
    {
        var clock = new ManualClock();
        var ai = new ControlledAi();
        using var match = new MatchController(ai, Words, clock);
        await Reject(() => Command(() => match.Start(new(4))));
        match.Start(new(3, 60, 180), 42);
        long expired = match.Snapshot.Version;
        clock.Advance(60); // No UI timer tick. Exactly at the deadline must be rejected.
        await Reject(() => match.SubmitAsync(expired, "a painted shape", 42));
        Require(match.Snapshot.Phase == MatchPhase.RoundResult && match.Snapshot.Records.Single().State == RoundState.TimedOut && ai.Generated == 0, "Writing deadline admitted generation");
        match.Advance(expired);
        var second = match.Snapshot;
        await Reject(() => match.SubmitAsync(expired, "a painted shape", 42));
        await Reject(() => match.SubmitAsync(second.Version, second.Word!.Korean, 42));
        Require(match.Snapshot.Phase == MatchPhase.Writing && match.Snapshot.Records.Length == 1, "Invalid input consumed the round");
        ai.Block = true;
        Task running = match.SubmitAsync(second.Version, "a painted shape", 42);
        await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Reject(() => match.SubmitAsync(second.Version, "a different shape", 43));
        await Reject(() => Command(() => match.Advance(second.Version)));
        await Reject(() => Command(() => match.Skip(second.Version)));
        await Reject(() => Command(() => match.Start(new(3))));
        match.Cancel(expired); // An old command cannot cancel the active round.
        Require(!running.IsCompleted, "Old cancellation affected current round");
        match.Cancel(second.Version);
        await running;
        Require(match.Snapshot.Records.Length == 2 && match.Snapshot.Records[1].State == RoundState.Cancelled && !match.Snapshot.Busy, "Cancel cleanup did not finish");
        match.Advance(second.Version);
        ai.Block = false;
        clock.Advance(59.999);
        await match.SubmitAsync(match.Snapshot.Version, "a painted shape", 42);
        clock.Advance(1);
        var last = match.Snapshot;
        Require(last.Records.Length == 3 && last.Records[2].State == RoundState.Completed && last.TotalScore == 33.3, "Valid pre-deadline submission or accumulation failed");
        Require(last.Records.Select(r => r.WordId).Distinct().Count() == 3, "A word repeated in a match");
        match.Advance(last.Version);
        var final = match.Snapshot;
        Require(final.Phase == MatchPhase.Finished && final.Result is null && Math.Abs(final.AverageScore - 11.1) < 1e-9, "Final summary retained image or incorrect average");
        await Reject(() => Command(() => match.Advance(last.Version)));
        match.Start(new(3, 5, 5), 5);
        Require(match.Snapshot.Records.Length == 0 && match.Snapshot.TotalScore == 0, "New match retained scores");
        match.Skip(match.Snapshot.Version);
        await Reject(() => Command(() => match.Skip(match.Snapshot.Version)));
        Require(match.Snapshot.Records.Single().State == RoundState.Skipped, "Skip recorded more than once");
        match.Advance(match.Snapshot.Version);
        ai.Fail = true;
        await match.SubmitAsync(match.Snapshot.Version, "a painted shape", 42);
        Require(match.Snapshot.Records[1].State == RoundState.Failed && match.Snapshot.Records[1].Score == 0, "Failure not scored as zero");
        match.Advance(match.Snapshot.Version);
        ai.Fail = false;
        await match.SubmitAsync(match.Snapshot.Version, "a painted shape", 42);
        Require(match.Snapshot.Records[2].State == RoundState.Completed, "Failure prevented next round");
        Console.WriteLine("Match: admission deadline, invalid/duplicate/stale commands, cancel cleanup, no repeat, skip/failure, final scores and restart passed");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(double seconds) => timestamp += (long)(seconds * TimeSpan.TicksPerSecond);
    }
    private sealed class ControlledAi : ILocalAi
    {
        public int Generated;
        public bool Block, Fail;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Translate(string description, CancellationToken token) => "a painted shape";
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        {
            Generated++;
            if (Fail) throw new InvalidDataException("Injected inference failure");
            if (Block) { Started.TrySetResult(); Task.Delay(Timeout.Infinite, token).GetAwaiter().GetResult(); }
            return [1, 2, 3];
        }
        public (double Cosine, double Score) Score(byte[] png, string wordId, CancellationToken token) => (.2, 33.3);
    }
}
