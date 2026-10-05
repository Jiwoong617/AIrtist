using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

internal static class GpuLifetimeChecks
{
    public static async Task Run(RuntimeAssets assets, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new diagnostic output directory.");
        assets.Verify(CancellationToken.None);
        Directory.CreateDirectory(output);
        string model = assets.File("generation/taesd_decoder-compact.onnx");
        bool missingRejected = false;
        try { using var missing = OrtSessions.Open(Path.Combine(output, "missing.onnx"), true); }
        catch (OnnxRuntimeException) { missingRejected = true; }
        if (!missingRejected) throw new InvalidOperationException("Missing model accepted");

        var profiles = await Task.WhenAll(new[] { "profile-a", "profile-b" }.Select(name => Task.Run(() =>
        {
            using var session = OrtSessions.Open(model, true, Path.Combine(output, name));
            string profile = session.EndProfiling();
            if (!File.Exists(profile) || !Path.GetFileName(profile).StartsWith(name, StringComparison.Ordinal))
                throw new InvalidOperationException("Concurrent profile prefix was mixed");
            return profile;
        })));
        using (var plain = OrtSessions.Open(model, true))
            if (!string.IsNullOrEmpty(plain.EndProfiling())) throw new InvalidOperationException("Profiling leaked into the next session");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        int before = process.HandleCount;
        using (var active = OrtSessions.Open(model, true))
        {
            OrtSessions.ShutdownGpu();
            OrtSessions.ShutdownGpu();
            bool closedRejected = false;
            try { using var late = OrtSessions.Open(model, true); }
            catch (ObjectDisposedException) { closedRejected = true; }
            if (!closedRejected) throw new InvalidOperationException("GPU reopened after shutdown");
            // The active session's native ownership remains valid after factory release.
            var result = OrtSessions.Run(active,
                [NamedOnnxValue.CreateFromTensor("latents", new DenseTensor<float>(new float[4 * 64 * 64], [1, 4, 64, 64]))],
                CancellationToken.None);
            if (result.Length != 3 * 512 * 512) throw new InvalidOperationException("Active decoder lost ownership during shutdown");
        }
        process.Refresh();
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            status = "passed", missingModelRecovery = true, concurrentProfileIsolation = true, profiles,
            unprofiledSessionIsolated = true, idempotentShutdown = true, lateCreationRejected = true,
            existingSessionRemainsValid = true, handlesBeforeShutdown = before, handlesAfterRelease = process.HandleCount
        }, RuntimeAssets.Json));
    }
}
