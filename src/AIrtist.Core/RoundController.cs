using System.Diagnostics;

namespace AIrtist.Core;

public interface ILocalAi
{
    string Translate(string description, CancellationToken token);
    byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress);
    (double Cosine, double Score) Score(byte[] png, string wordId, CancellationToken token);
}

public sealed class LocalAi(RuntimeAssets assets) : ILocalAi
{
    public string Translate(string description, CancellationToken token) => new LocalTranslator(assets).Translate(description, token);
    public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress) => new LocalGenerator(assets).Generate(translation, seed, token, progress);
    public (double Cosine, double Score) Score(byte[] png, string wordId, CancellationToken token) => new ImageScorer(assets).Score(png, wordId, token);
}

public enum RoundState { Ready, Translating, Generating, Scoring, Completed, Cancelled, TimedOut, Failed, Skipped }
public sealed record RoundUpdate(long Id, RoundState State, string Message, WorkflowResult? Result = null);

// Owns submission rights and late-result rejection; UI only requests commands.
public sealed class RoundController(ILocalAi ai) : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim execution = new(1, 1);
    private CancellationTokenSource? active;
    private long id;
    private bool submitted;
    private bool disposed;
    public event Action<RoundUpdate>? Updated;

    public long NewRound()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            active?.Cancel();
            id++;
            submitted = false;
            return id;
        }
    }

    public async Task SubmitAsync(long roundId, Word word, string description, int seed, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        string input = InputRules.Validate(description, word);
        CancellationTokenSource request;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (roundId != id || submitted) throw new InvalidOperationException("이미 제출했거나 종료된 라운드입니다.");
            if (!execution.Wait(0)) throw new InvalidOperationException("이전 작업을 정리하고 있습니다. 잠시 후 시도해 주세요.");
            submitted = true;
            request = new CancellationTokenSource();
            active = request;
        }
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, deadline.Token);
        var token = linked.Token;
        try
        {
            await Task.Run(() =>
            {
                Publish(roundId, RoundState.Translating, "묘사를 번역하고 있어요");
                var watch = Stopwatch.StartNew();
                // Neither translator nor generator receives the answer or its aliases.
                string translation = ai.Translate(input, token);
                token.ThrowIfCancellationRequested();
                double translationSeconds = watch.Elapsed.TotalSeconds;
                if (InputRules.ContainsAlias(translation, word))
                    throw new InvalidDataException("번역 결과에 제시어가 포함되어 생성을 중단했습니다. 다음 제시어에서 다른 묘사를 시도해 주세요.");
                Publish(roundId, RoundState.Generating, "그림을 준비하고 있어요");
                watch.Restart();
                byte[] png = ai.Generate(translation, seed, token, message => Publish(roundId, RoundState.Generating, message));
                token.ThrowIfCancellationRequested();
                double generationSeconds = watch.Elapsed.TotalSeconds;
                Publish(roundId, RoundState.Scoring, "완성된 그림을 평가하고 있어요");
                watch.Restart();
                var score = ai.Score(png, word.Id, token);
                var result = new WorkflowResult(translation, png, score.Cosine, score.Score, translationSeconds, generationSeconds, watch.Elapsed.TotalSeconds);
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    Publish(roundId, RoundState.Completed, "그림이 완성됐어요", result);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            Publish(roundId, deadline.IsCancellationRequested ? RoundState.TimedOut : RoundState.Cancelled,
                deadline.IsCancellationRequested ? "생성 제한 시간이 지났습니다." : "생성을 취소했습니다.");
        }
        catch (Exception ex)
        {
            Publish(roundId, RoundState.Failed, ex is ArgumentException or InvalidDataException ? ex.Message : "AI 실행에 실패했습니다. 모델 파일과 GPU 사용 상태를 확인해 주세요.");
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(active, request)) active = null; }
            request.Dispose();
            execution.Release();
        }
    }

    private void Publish(long roundId, RoundState state, string message, WorkflowResult? result = null)
    {
        lock (sync)
        {
            if (disposed || roundId != id) return;
            Updated?.Invoke(new(roundId, state, message, result));
        }
    }

    public void Cancel() { lock (sync) active?.Cancel(); }
    public void Dispose() { lock (sync) { disposed = true; id++; active?.Cancel(); } }
}
