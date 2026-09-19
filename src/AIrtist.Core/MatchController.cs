using System.Collections.Concurrent;

namespace AIrtist.Core;

// Provisional offline rules. No remote-host authority or multiplayer is implied.
public sealed record MatchSettings(int Rounds = 3, int WritingSeconds = 60, int AiSeconds = 180)
{
    public void Validate(int wordCount)
    {
        if (Rounds < 1 || Rounds > Math.Min(8, wordCount) || WritingSeconds is < 5 or > 300 || AiSeconds is < 5 or > 600)
            throw new ArgumentException("라운드·작성·AI 제한 시간 설정이 올바르지 않습니다.");
    }
}

public enum MatchPhase { Setup, Writing, Processing, RoundResult, Finished }
public sealed record RoundRecord(int Number, string WordId, string Word, RoundState State, double Score, double? Cosine, string Message);
public sealed record MatchSnapshot(long Version, MatchPhase Phase, MatchSettings Settings, int Number, Word? Word,
    double RemainingSeconds, bool Busy, string Message, WorkflowResult? Result, RoundRecord[] Records)
{
    public double TotalScore => Records.Sum(r => r.Score);
    public double AverageScore => Records.Length == 0 ? 0 : TotalScore / Records.Length;
}

public sealed class MatchController : IDisposable
{
    private readonly object sync = new();
    private readonly RoundController round;
    private readonly Word[] words;
    private readonly TimeProvider clock;
    private readonly ConcurrentQueue<RoundUpdate> updates = new();
    private readonly List<RoundRecord> records = [];
    private MatchSettings settings = new();
    private Word[] sequence = [];
    private MatchPhase phase;
    private long version, roundId, phaseStarted;
    private int index;
    private bool busy, disposed;
    private string message = "경기 설정을 고르고 시작해 주세요";
    private WorkflowResult? result;

    public MatchController(ILocalAi ai, IEnumerable<Word> words, TimeProvider? clock = null)
    {
        this.words = words.ToArray();
        if (this.words.Length == 0 || this.words.Select(w => w.Id).Distinct().Count() != this.words.Length)
            throw new ArgumentException("제시어 목록이 비어 있거나 ID가 중복되었습니다.");
        this.clock = clock ?? TimeProvider.System;
        round = new RoundController(ai);
        // Do not acquire the match lock from the round's callback: commands call the round under that lock.
        round.Updated += updates.Enqueue;
    }

    public MatchSnapshot Snapshot
    {
        get { lock (sync) { CheckAlive(); TickCore(); return Capture(); } }
    }

    public void Start(MatchSettings selected, int? shuffleSeed = null)
    {
        lock (sync)
        {
            CheckAlive();
            if (busy || phase is not (MatchPhase.Setup or MatchPhase.Finished)) throw new InvalidOperationException("현재 경기를 먼저 마쳐 주세요.");
            selected.Validate(words.Length);
            settings = selected;
            sequence = words.ToArray();
            var random = shuffleSeed.HasValue ? new Random(shuffleSeed.Value) : Random.Shared;
            random.Shuffle(sequence);
            sequence = sequence.Take(settings.Rounds).ToArray();
            index = 0;
            records.Clear();
            BeginRound();
        }
    }

    public async Task SubmitAsync(long expectedVersion, string description, int seed)
    {
        Task work;
        long requestVersion;
        lock (sync)
        {
            CheckAlive();
            TickCore(); // Admission uses the monotonic deadline even when UI ticks are delayed.
            if (expectedVersion != version || phase != MatchPhase.Writing || busy)
                throw new InvalidOperationException("이미 제출했거나 작성 시간이 끝난 라운드입니다.");
            string input = InputRules.Validate(description, sequence[index]);
            // Validation does not reserve a generation. Check the deadline again after validation.
            TickCore();
            if (phase != MatchPhase.Writing) throw new InvalidOperationException("작성 제한 시간이 지났습니다.");
            work = round.SubmitAsync(roundId, sequence[index], input, seed, TimeSpan.FromSeconds(settings.AiSeconds));
            busy = true;
            phase = MatchPhase.Processing;
            phaseStarted = clock.GetTimestamp();
            message = "묘사를 번역하고 있어요";
            requestVersion = version;
        }
        try { await work.ConfigureAwait(false); }
        finally
        {
            lock (sync)
            {
                if (!disposed && requestVersion == version)
                {
                    busy = false;
                    TickCore();
                    if (phase == MatchPhase.Processing) FinishRound(RoundState.Failed, "AI 작업을 완료하지 못했습니다.");
                }
            }
        }
    }

    public void Skip(long expectedVersion)
    {
        lock (sync)
        {
            CheckAlive(); TickCore();
            if (expectedVersion != version || phase != MatchPhase.Writing || busy) throw new InvalidOperationException("작성 중인 라운드만 건너뛸 수 있습니다.");
            FinishRound(RoundState.Skipped, "라운드를 건너뛰었습니다.");
        }
    }

    public void Cancel(long expectedVersion)
    {
        lock (sync)
        {
            CheckAlive();
            if (expectedVersion != version || phase != MatchPhase.Processing || !busy) return;
            message = "취소하고 작업을 정리하고 있어요";
            round.Cancel();
        }
    }

    public void Advance(long expectedVersion)
    {
        lock (sync)
        {
            CheckAlive(); TickCore();
            if (expectedVersion != version || phase != MatchPhase.RoundResult || busy) throw new InvalidOperationException("라운드 처리가 끝난 뒤 진행해 주세요.");
            result = null; // Only scores/word IDs persist between rounds, not image buffers.
            if (++index == settings.Rounds)
            {
                version++;
                phase = MatchPhase.Finished;
                message = "경기를 마쳤어요";
            }
            else BeginRound();
        }
    }

    private void BeginRound()
    {
        version++;
        roundId = round.NewRound();
        updates.Clear();
        result = null;
        phase = MatchPhase.Writing;
        phaseStarted = clock.GetTimestamp();
        message = "제시어를 쓰지 않고 묘사해 주세요";
    }

    private void TickCore()
    {
        while (updates.TryDequeue(out var update))
        {
            if (update.Id != roundId || phase != MatchPhase.Processing) continue;
            message = update.Message;
            if (update.State is RoundState.Completed or RoundState.Cancelled or RoundState.TimedOut or RoundState.Failed)
                FinishRound(update.State, update.Message, update.Result);
        }
        if (phase == MatchPhase.Writing && Remaining() <= 0)
            FinishRound(RoundState.TimedOut, "작성 제한 시간이 지났습니다.");
    }

    private void FinishRound(RoundState state, string text, WorkflowResult? completed = null)
    {
        if (phase is not (MatchPhase.Writing or MatchPhase.Processing)) return;
        result = completed;
        message = text;
        phase = MatchPhase.RoundResult;
        var word = sequence[index];
        records.Add(new(index + 1, word.Id, word.Korean, state, completed?.Score ?? 0, completed?.Cosine, text));
    }

    private double Remaining() => phase is MatchPhase.Writing or MatchPhase.Processing
        ? Math.Max(0, (phase == MatchPhase.Writing ? settings.WritingSeconds : settings.AiSeconds) - clock.GetElapsedTime(phaseStarted).TotalSeconds) : 0;
    private MatchSnapshot Capture() => new(version, phase, settings, Math.Min(index + 1, settings.Rounds),
        phase is MatchPhase.Setup or MatchPhase.Finished ? null : sequence[index], Remaining(), busy, message, result, records.ToArray());
    private void CheckAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; version++; result = null; round.Dispose(); updates.Clear(); } }
}
