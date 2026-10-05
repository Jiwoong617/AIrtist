using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;

internal static class DrawingStability
{
    public static async Task Run(RuntimeAssets assets, string root)
    {
        assets.Verify(CancellationToken.None);
        string output = Path.Combine(root, "outputs/drawing-stability", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var observed = new ObservedAi(assets);
        using var drawing = new DrawingController(observed);
        await drawing.PrepareTranslationAsync(drawing.Snapshot.Version, "a white rabbit");
        if (drawing.Snapshot.Phase != DrawingPhase.Reviewing) throw new InvalidOperationException(drawing.Snapshot.Message);
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (string name in new[] { "complete-1", "complete-2", "cancel-during-unet", "recover-after-cancel", "timeout", "recover-after-timeout", "complete-5", "complete-6" })
        {
            bool cancel = name == "cancel-during-unet", timeout = name == "timeout";
            // The short timeout case gets its own controller; the same AI/native runtime is reused.
            using var timed = timeout ? new DrawingController(observed, TimeSpan.FromSeconds(10)) : null;
            var controller = timed ?? drawing;
            if (timed is not null) await controller.PrepareTranslationAsync(controller.Snapshot.Version, "a white rabbit");
            observed.Cancel = cancel ? () => controller.Cancel(controller.Snapshot.Version) : null;
            var prior = controller.Snapshot.Result;
            var watch = Stopwatch.StartNew();
            var state = controller.Snapshot;
            Task work = controller.GenerateAsync(state.Version, state.Draft!.Id, "a white rabbit with long ears", 42);
            long peakWorking = 0, peakPrivate = 0;
            while (!work.IsCompleted)
            {
                process.Refresh();
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                await Task.WhenAny(work, Task.Delay(100));
            }
            await work;
            state = controller.Snapshot;
            bool passed = cancel || timeout
                ? state.Phase == DrawingPhase.Reviewing && ReferenceEquals(prior, state.Result) && state.Message.Contains(cancel ? "취소" : "제한 시간")
                : state.Phase == DrawingPhase.Completed && !ReferenceEquals(prior, state.Result);
            if (!passed || state.Busy) throw new InvalidOperationException($"{name}: {state.Phase}: {state.Message}");
            if (!cancel && !timeout) File.WriteAllBytes(Path.Combine(output, name + ".png"), state.Result!.Png);
            process.Refresh();
            rows.Add(new { name, state = state.Phase.ToString(), state.Message, seconds = watch.Elapsed.TotalSeconds,
                peakWorking, peakPrivate, naturalIdlePrivate = process.PrivateMemorySize64 });
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, RuntimeAssets.Json));
            Console.WriteLine($"{name}: passed ({watch.Elapsed.TotalSeconds:F2}s)");
        }
    }

    private sealed class ObservedAi(RuntimeAssets assets) : IDrawingAi
    {
        private readonly LocalDrawingAi inner = new(assets);
        public Action? Cancel;
        public string Translate(string description, CancellationToken token) => inner.Translate(description, token);
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
            => inner.Generate(translation, seed, token, text =>
            {
                progress(text);
                if (Cancel is { } cancel && text.EndsWith("3/25", StringComparison.Ordinal)) { Cancel = null; cancel(); }
            });
    }
}
