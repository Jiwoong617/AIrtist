using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIrtist.Core;

// Separate from the natural soak: no inference/controller/PNG, explicit GC at each boundary.
internal static class DrawingResourceProbe
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
            return new { handles = process.HandleCount, privateBytes = process.PrivateMemorySize64,
                workingSet = process.WorkingSet64, threads = process.Threads.Count };
        }
        void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        string status = "running";
        object initial = Read();
        var modules = new List<object>();
        void Save() => File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        { status, scope = "Session construction/disposal only; no inference or PNG; forced GC after every disposal", initial, rows, modules }, RuntimeAssets.Json));
        Save();
        try
        {
            foreach (var model in new[]
            {
                (Name: "translation-cpu", File: "translation/step-fp16-storage.onnx", Gpu: false),
                (Name: "text-gpu", File: "generation/text_encoder-compact.onnx", Gpu: true),
                (Name: "unet-gpu", File: "generation/unet-w8.onnx", Gpu: true),
                (Name: "decoder-gpu", File: "generation/taesd_decoder-compact.onnx", Gpu: true)
            })
            {
                for (int iteration = 1; iteration <= 4; iteration++)
                {
                    Collect();
                    object before = Read();
                    var watch = Stopwatch.StartNew();
                    object opened;
                    using (var session = OrtSessions.Open(assets.File(model.File), model.Gpu)) opened = Read();
                    object disposed = Read();
                    Collect();
                    object collected = Read();
                    rows.Add(new { model = model.Name, iteration, seconds = watch.Elapsed.TotalSeconds, before, opened, disposed, collected });
                    Save();
                    Console.WriteLine($"{model.Name} {iteration}/4 recorded ({watch.Elapsed.TotalSeconds:F2}s)");
                }
            }
            foreach (ProcessModule module in process.Modules)
            {
                if (!new[] { "onnxruntime.dll", "DirectML.dll" }.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase)) continue;
                if (!module.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Use the checks EXE apphost with local AI runtime DLLs.");
                modules.Add(new { name = module.ModuleName, path = module.FileName,
                    version = module.FileVersionInfo.FileVersion,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName))) });
            }
            status = "recorded";
        }
        catch { status = "failed"; throw; }
        finally { Save(); }
    }
}
