using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;
using Microsoft.ML.OnnxRuntime;

// Compare provider construction separately from model sessions. This diagnostic
// deliberately collects after each iteration; it is not a natural memory soak.
internal static class DrawingProviderProbe
{
    public static void Run(RuntimeAssets assets, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new diagnostic output directory.");
        assets.Verify(CancellationToken.None);
        Directory.CreateDirectory(output);
        using var process = Process.GetCurrentProcess();
        var rows = new List<object>();
        object Read()
        {
            process.Refresh();
            return new { handles = process.HandleCount, privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64 };
        }
        void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        void Save(string status) => File.WriteAllText(Path.Combine(output, "results.json"),
            JsonSerializer.Serialize(new { status, scope = "Provider only / fresh session / shared provider; no inference; GC between operations", rows }, RuntimeAssets.Json));
        void Repeat(string name, Action action)
        {
            for (int i = 1; i <= 12; i++)
            {
                Collect();
                object before = Read();
                action();
                Collect();
                rows.Add(new { name, iteration = i, before, after = Read() });
                Save("running");
            }
            Console.WriteLine($"{name}: 12 iterations recorded");
        }
        try
        {
            Repeat("options-only", () => { using var options = Options(); });
            Repeat("fresh-provider-session", () =>
            {
                using var options = Options();
                using var session = new InferenceSession(assets.File("generation/taesd_decoder-compact.onnx"), options);
            });
            using (var shared = Options())
            {
                Repeat("shared-provider-session", () =>
                {
                    using var session = new InferenceSession(assets.File("generation/taesd_decoder-compact.onnx"), shared);
                });
            }
            Collect();
            rows.Add(new { name = "after-shared-provider-dispose", after = Read() });
            Save("recorded");
        }
        catch { Save("failed"); throw; }
    }

    private static SessionOptions Options()
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 6, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        try
        {
            options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
            options.AppendExecutionProvider("DML", new() { ["device_id"] = "0", ["disable_metacommands"] = "True" });
            return options;
        }
        catch { options.Dispose(); throw; }
    }
}
