using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIrtist.Core;

// Diagnostic only: real inference, bounded retained PNGs, no forced GC during operations.
internal static class DrawingSoak
{
    private sealed record Prompt(string Id, string Source, string Reviewed);
    private static readonly Prompt[] Prompts =
    [
        new("rabbit", "긴 귀를 가진 흰 토끼", "a white rabbit with long ears"),
        new("apple", "파란 배경 위의 빨간 사과", "a red apple on a blue background"),
        new("car", "네 개의 바퀴가 달린 작은 빨간 자동차", "a small red car with four wheels"),
        new("house", "초록 언덕 위에 파란 지붕이 있는 노란 집", "a yellow house with a blue roof on a green hill"),
        new("shapes", "빨간 공 아래에 파란 상자", "a blue box below a red ball"),
        new("boat", "a small white boat on blue water", "a small white boat on blue water")
    ];

    public static async Task Run(RuntimeAssets assets, string output, int count)
    {
        if (count is < 10 or > 300 || count % 10 != 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Use a multiple of 10 from 10 to 300.");
        output = Path.GetFullPath(output);
        string report = Path.Combine(output, "results.json");
        if (File.Exists(report)) throw new IOException("Use a new diagnostic output directory.");
        assets.Verify(CancellationToken.None);
        Directory.CreateDirectory(output);
        var observed = new ObservedAi(assets);
        var shared = new SerializedDrawingAi(observed);
        using var drawing = new DrawingController(shared);
        using var timed = new DrawingController(shared, TimeSpan.FromSeconds(10));
        using var process = Process.GetCurrentProcess();
        var elapsed = Stopwatch.StartNew();
        var rows = new List<object>();
        var initial = Idle(process);
        string status = "running";
        int completed = 0, cancelled = 0, timedOut = 0;
        object? cleanup = null;

        void Save() => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            status, requestedImages = count, completed, cancelled, timedOut,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds, modelRoot = assets.Root,
            modelManifestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assets.File("manifest.json")))),
            settings = assets.Read<GenerationSettings>("generation/settings.json"),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            forcedGcDuringOperations = false, initial, rows, cleanup
        }, RuntimeAssets.Json));

        async Task Translate(DrawingController controller, Prompt prompt, string name)
        {
            var before = controller.Snapshot;
            if (before.Draft is not null) controller.Revise(before.Version, before.Draft.Id);
            var metrics = await Measure(process, () => controller.PrepareTranslationAsync(controller.Snapshot.Version, prompt.Source));
            var state = controller.Snapshot;
            bool passed = state.Phase == DrawingPhase.Reviewing && !state.Busy && state.Draft is not null;
            rows.Add(new { name, kind = "translation", prompt.Id, prompt.Source, automatic = state.Draft?.Translation,
                passed, state.Message, metrics, idle = Idle(process) });
            Save();
            if (!passed) throw new InvalidOperationException($"{name}: {state.Message}");
        }

        async Task Generate(DrawingController controller, Prompt prompt, string name, int seed, string kind, bool keepPng)
        {
            var before = controller.Snapshot;
            observed.Reset(kind == "cancel" ? () => controller.Cancel(controller.Snapshot.Version) : null);
            var metrics = await Measure(process, () => controller.GenerateAsync(before.Version, before.Draft!.Id, prompt.Reviewed, seed));
            var state = controller.Snapshot;
            bool passed = !state.Busy && (kind == "complete"
                ? state.Phase == DrawingPhase.Completed && state.Result is not null && !ReferenceEquals(before.Result, state.Result)
                    && state.Result.Translation == prompt.Reviewed && state.Result.Seed == seed
                : state.Phase == DrawingPhase.Reviewing && ReferenceEquals(before.Result, state.Result)
                    && state.Message.Contains(kind == "cancel" ? "취소" : "제한 시간", StringComparison.Ordinal)
                    && (kind != "cancel" || observed.CancelRequestedSeconds is not null));
            string? pngSha256 = null, pngFile = null;
            int pngBytes = 0;
            if (passed && kind == "complete")
            {
                var png = state.Result!.Png;
                var bitmap = ImageCodec.Load(png);
                passed = bitmap.PixelWidth == 512 && bitmap.PixelHeight == 512;
                pngSha256 = Convert.ToHexString(SHA256.HashData(png));
                pngBytes = png.Length;
                if (keepPng)
                {
                    pngFile = name + ".png";
                    File.WriteAllBytes(Path.Combine(output, pngFile), png);
                }
            }
            if (passed)
            {
                if (kind == "complete") completed++;
                else if (kind == "cancel") cancelled++;
                else timedOut++;
            }
            rows.Add(new
            {
                name, kind, prompt.Id, prompt.Source, prompt.Reviewed, seed, passed,
                phase = state.Phase.ToString(), state.Message, metrics, idle = Idle(process),
                observed.FirstStepSeconds, observed.DecodeStartSeconds, observed.CancelRequestedSeconds,
                cancelCleanupSeconds = observed.CancelRequestedSeconds is double at ? metrics.Seconds - at : (double?)null,
                generationSeconds = kind == "complete" && passed ? state.Result!.GenerationSeconds : (double?)null,
                pngBytes, pngSha256, pngFile
            });
            Save();
            Console.WriteLine($"{name}: {(passed ? "passed" : "FAILED")} ({metrics.Seconds:F2}s), images {completed}/{count}");
            if (!passed) throw new InvalidOperationException($"{name}: {state.Message}");
        }

        Save();
        Console.WriteLine($"SOAK: {count} images, {count / 10} cancellations and timeouts each; {output}");
        try
        {
            for (int i = 1; i <= count; i++)
            {
                var prompt = Prompts[((i - 1) / 2) % Prompts.Length];
                if (i % 2 == 1) await Translate(drawing, prompt, $"translate-{i:D3}");
                if (i % 10 == 9)
                    await Generate(drawing, prompt, $"cancel-{i:D3}", 42 + i, "cancel", false);
                if (i % 10 == 0)
                {
                    await Translate(timed, prompt, $"timeout-translate-{i:D3}");
                    await Generate(timed, prompt, $"timeout-{i:D3}", 42 + i, "timeout", false);
                }
                // Each pair reuses the same reviewed draft with another seed. Native timeout
                // uses its own 10s controller; the shared AI/runtime and main controller recover.
                await Generate(drawing, prompt, $"complete-{i:D3}", 42 + i, "complete",
                    (i <= 11 && i % 2 == 1) || i > count - 2);
            }
            var beforeDispose = Idle(process);
            drawing.Dispose();
            timed.Dispose();
            await Task.Delay(2000);
            var afterDispose = Idle(process);
            // Diagnostic tail only: distinguish finalizable allocations from retained
            // resources. Never collect during the measured operations or in the app.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(1000);
            cleanup = new { beforeDispose, afterDispose, afterDiagnosticGc = Idle(process) };
            status = "passed";
        }
        catch { status = "failed"; throw; }
        finally { Save(); }
    }

    private sealed record Memory(long WorkingSet, long PrivateBytes, int Handles, int Threads);
    private sealed record Metrics(double Seconds, long PeakWorkingSet, long PeakPrivateBytes);
    private static Memory Idle(Process process)
    {
        process.Refresh();
        return new(process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, process.Threads.Count);
    }

    private static async Task<Metrics> Measure(Process process, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        Task task = action();
        long peakWorkingSet = 0, peakPrivateBytes = 0;
        do
        {
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
            if (!task.IsCompleted) await Task.WhenAny(task, Task.Delay(100));
        } while (!task.IsCompleted);
        await task;
        return new(watch.Elapsed.TotalSeconds, peakWorkingSet, peakPrivateBytes);
    }

    private sealed class ObservedAi(RuntimeAssets assets) : IDrawingAi
    {
        private readonly LocalDrawingAi inner = new(assets);
        private Action? cancel;
        public double? FirstStepSeconds, DecodeStartSeconds, CancelRequestedSeconds;
        public void Reset(Action? onCancel)
        { cancel = onCancel; FirstStepSeconds = DecodeStartSeconds = CancelRequestedSeconds = null; }
        public string Translate(string description, CancellationToken token) => inner.Translate(description, token);
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        {
            var watch = Stopwatch.StartNew();
            return inner.Generate(translation, seed, token, text =>
            {
                progress(text);
                if (text.EndsWith(" · 1/25", StringComparison.Ordinal)) FirstStepSeconds = watch.Elapsed.TotalSeconds;
                if (text == "그림을 완성하고 있어요") DecodeStartSeconds = watch.Elapsed.TotalSeconds;
                if (cancel is { } action && text.EndsWith(" · 3/25", StringComparison.Ordinal))
                {
                    cancel = null;
                    CancelRequestedSeconds = watch.Elapsed.TotalSeconds;
                    action();
                }
            });
        }
    }
}
