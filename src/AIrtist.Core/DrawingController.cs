using System.Diagnostics;

namespace AIrtist.Core;

public interface IDrawingAi
{
    string Translate(string description, CancellationToken token);
    byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress);
    byte[] Generate(string translation, GenerationParameters parameters, CancellationToken token, Action<string> progress)
    {
        parameters.Validate();
        if (parameters.Guidance != 7 || parameters.Steps != 25)
            throw new NotSupportedException("이 실행기는 사용자 생성 설정을 지원하지 않습니다.");
        return Generate(translation, parameters.Resolve().Seed!.Value, token, progress);
    }
}

public sealed class LocalDrawingAi(RuntimeAssets assets) : IDrawingAi
{
    public string Translate(string description, CancellationToken token) => new LocalTranslator(assets).Translate(description, token);
    public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        => Generate(translation, new GenerationParameters(Seed: seed), token, progress);
    public byte[] Generate(string translation, GenerationParameters parameters, CancellationToken token, Action<string> progress)
    {
        var resolved = parameters.Resolve();
        // A Windows-session mutex prevents multiple game processes loading GPU models at once.
        // The wait is cancellable and remains part of the normal generation deadline.
        using var device = new Mutex(false, @"Local\AIrtist.Generation.v1");
        bool acquired = false;
        try
        {
            try
            {
                acquired = device.WaitOne(0);
                if (!acquired)
                {
                    progress("다른 창의 이미지 생성 대기 중");
                    acquired = WaitHandle.WaitAny([device, token.WaitHandle]) == 0;
                }
            }
            catch (AbandonedMutexException) { acquired = true; }
            token.ThrowIfCancellationRequested();
            return new LocalGenerator(assets).Generate(translation, resolved.Seed!.Value, token, progress, parameters: resolved);
        }
        finally { if (acquired) device.ReleaseMutex(); }
    }
}

// Share one instance across windows/local preview roles on the same PC. Waiting
// for another operation remains cancellable; models never run concurrently.
public sealed class SerializedDrawingAi(IDrawingAi inner) : IDrawingAi
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Translate(string description, CancellationToken token)
    {
        gate.Wait(token); try { token.ThrowIfCancellationRequested(); return inner.Translate(description, token); }
        finally { gate.Release(); }
    }
    public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
    {
        gate.Wait(token); try { token.ThrowIfCancellationRequested(); return inner.Generate(translation, seed, token, progress); }
        finally { gate.Release(); }
    }
    public byte[] Generate(string translation, GenerationParameters parameters, CancellationToken token, Action<string> progress)
    {
        parameters.Validate();
        gate.Wait(token); try { token.ThrowIfCancellationRequested(); return inner.Generate(translation, parameters, token, progress); }
        finally { gate.Release(); }
    }
}

public enum DrawingPhase { Editing, Translating, Reviewing, Generating, Completed }
public sealed record DrawingDraft(long Id, string Description, string Translation, double Seconds);
public sealed record DrawingResult(string Description, string Translation, byte[] Png, int Seed, double TranslationSeconds, double GenerationSeconds)
{
    public GenerationParameters Parameters { get; init; } = new(Seed: Seed);
}
public sealed record DrawingSnapshot(long Version, DrawingPhase Phase, bool Busy, string Message, DrawingDraft? Draft, DrawingResult? Result)
{
    public long FailureRevision { get; init; }
}

// Free drawing owns one AI operation at a time, without words, rounds or submission rights.
public sealed class DrawingController : IDisposable
{
    private readonly object sync = new();
    private readonly IDrawingAi ai;
    private readonly TimeSpan operationTimeout;
    private CancellationTokenSource? active;
    private long version, draftId, failureRevision;
    private bool disposed;
    private DrawingPhase phase = DrawingPhase.Editing;
    private string message = "원하는 그림을 자유롭게 묘사해 주세요";
    private DrawingDraft? draft;
    private DrawingResult? result;

    public DrawingController(IDrawingAi ai, TimeSpan? operationTimeout = null)
    {
        this.ai = ai;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromMinutes(3);
        if (this.operationTimeout <= TimeSpan.Zero || this.operationTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public DrawingSnapshot Snapshot
    {
        get { lock (sync) { CheckAlive(); return new(version, phase, active is not null, message, draft, result) { FailureRevision = failureRevision }; } }
    }

    public Task DrawAsync(long expectedVersion, string description, GenerationParameters parameters)
    {
        lock (sync)
        {
            CheckCommand(expectedVersion);
            string input = InputRules.ValidateDescription(description);
            var selected = parameters.Resolve();
            draft = null;
            var request = Begin(DrawingPhase.Translating, "그림을 준비하고 있어요");
            long operation = version, currentDraft = ++draftId;
            return RunAsync(operation, request, true, token =>
            {
                var watch = Stopwatch.StartNew();
                string translation = InputRules.ValidateTranslation(ai.Translate(input, token));
                token.ThrowIfCancellationRequested();
                var prepared = new DrawingDraft(currentDraft, input, translation, watch.Elapsed.TotalSeconds);
                lock (sync)
                {
                    if (disposed || version != operation) throw new OperationCanceledException();
                    phase = DrawingPhase.Generating; message = "그림을 그리고 있어요";
                }
                watch.Restart();
                byte[] png = ai.Generate(translation, selected, token, text =>
                {
                    lock (sync) if (!disposed && version == operation && !token.IsCancellationRequested) message = text;
                });
                return (prepared, new DrawingResult(input, translation, png, selected.Seed!.Value, prepared.Seconds, watch.Elapsed.TotalSeconds) { Parameters = selected });
            });
        }
    }

    public Task PrepareTranslationAsync(long expectedVersion, string description)
    {
        lock (sync)
        {
            CheckCommand(expectedVersion);
            if (phase != DrawingPhase.Editing) throw new InvalidOperationException("원문 작성 화면에서 번역을 요청해 주세요.");
            string input = InputRules.ValidateDescription(description);
            var request = Begin(DrawingPhase.Translating, "영어 묘사를 준비하고 있어요");
            long currentDraft = ++draftId;
            return RunAsync(version, request, false, token =>
            {
                var watch = Stopwatch.StartNew();
                string translation = ai.Translate(input, token);
                return (new DrawingDraft(currentDraft, input, translation, watch.Elapsed.TotalSeconds), null);
            });
        }
    }

    public Task GenerateAsync(long expectedVersion, long expectedDraftId, string translation, int seed)
        => GenerateAsync(expectedVersion, expectedDraftId, translation, new GenerationParameters(Seed: seed));

    public Task GenerateAsync(long expectedVersion, long expectedDraftId, string translation, GenerationParameters parameters)
    {
        lock (sync)
        {
            CheckDraft(expectedVersion, expectedDraftId);
            var selected = parameters.Resolve();
            string input = InputRules.ValidateTranslation(translation);
            var confirmed = draft! with { Translation = input };
            draft = confirmed;
            var request = Begin(DrawingPhase.Generating, "확인한 묘사로 그림을 그리고 있어요");
            long operation = version;
            return RunAsync(operation, request, true, token =>
            {
                var watch = Stopwatch.StartNew();
                byte[] png = ai.Generate(input, selected, token, text =>
                {
                    lock (sync)
                        if (!disposed && version == operation && !token.IsCancellationRequested) message = text;
                });
                return (confirmed, new DrawingResult(confirmed.Description, input, png, selected.Seed!.Value, confirmed.Seconds, watch.Elapsed.TotalSeconds) { Parameters = selected });
            });
        }
    }

    public void Review(long expectedVersion)
    {
        lock (sync)
        {
            CheckCommand(expectedVersion);
            if (phase != DrawingPhase.Completed || draft is null) throw new InvalidOperationException("완성된 그림의 묘사를 수정할 수 있습니다.");
            phase = DrawingPhase.Reviewing;
            message = "영어 묘사를 고치거나 원문으로 돌아갈 수 있어요";
            version++;
        }
    }

    public void Revise(long expectedVersion, long expectedDraftId)
    {
        lock (sync)
        {
            CheckDraft(expectedVersion, expectedDraftId);
            draft = null;
            phase = DrawingPhase.Editing;
            message = "원문을 고친 뒤 번역을 다시 확인해 주세요";
            version++;
        }
    }

    private CancellationTokenSource Begin(DrawingPhase next, string status)
    {
        active = new CancellationTokenSource();
        phase = next;
        message = status;
        version++;
        return active;
    }

    private async Task RunAsync(long operation, CancellationTokenSource request, bool generating,
        Func<CancellationToken, (DrawingDraft Draft, DrawingResult? Result)> work)
    {
        using var deadline = new CancellationTokenSource(operationTimeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(request.Token, deadline.Token);
        try
        {
            var completed = await Task.Run(() => work(combined.Token), combined.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || version != operation) return;
                // Also reject workers that return successfully after cancellation or timeout.
                combined.Token.ThrowIfCancellationRequested();
                draft = completed.Draft;
                if (completed.Result is not null) result = completed.Result;
                phase = generating ? DrawingPhase.Completed : DrawingPhase.Reviewing;
                message = generating ? "그림이 완성됐어요. 다시 그리거나 묘사를 바꿔 보세요" : "영어 묘사를 확인하고 원하는 대로 고쳐 주세요";
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (disposed || version != operation) return;
                bool generationFailed = phase == DrawingPhase.Generating;
                if (!request.IsCancellationRequested) failureRevision++;
                phase = generating && draft is not null ? DrawingPhase.Reviewing : DrawingPhase.Editing;
                message = combined.IsCancellationRequested
                    ? request.IsCancellationRequested ? "작업을 취소했습니다. 다시 시도할 수 있어요" : "AI 처리 제한 시간이 지났습니다. 다시 시도할 수 있어요"
                    : ex is ArgumentException or InvalidDataException ? ex.Message
                    : generationFailed ? "그림을 만들지 못했습니다. GPU 사용 상태를 확인하고 다시 시도해 주세요" : "번역에 실패했습니다. 묘사를 고치고 다시 시도해 주세요";
            }
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(active, request)) active = null;
                if (!disposed && version == operation) version++;
                request.Dispose();
            }
        }
    }

    public void Cancel(long expectedVersion)
    {
        lock (sync)
        {
            CheckAlive();
            if (expectedVersion != version || active is null) return;
            message = "취소하고 작업을 정리하고 있어요";
            active.Cancel();
        }
    }

    private void CheckCommand(long expectedVersion)
    {
        CheckAlive();
        if (expectedVersion != version || active is not null)
            throw new InvalidOperationException("이전 작업이 끝난 뒤 현재 화면에서 다시 시도해 주세요.");
    }

    private void CheckDraft(long expectedVersion, long expectedDraftId)
    {
        CheckCommand(expectedVersion);
        if (phase is not (DrawingPhase.Reviewing or DrawingPhase.Completed) || draft?.Id != expectedDraftId)
            throw new InvalidOperationException("현재 영어 묘사를 확인한 뒤 다시 시도해 주세요.");
    }

    private void CheckAlive() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            version++;
            draft = null;
            result = null;
            active?.Cancel();
        }
    }
}
