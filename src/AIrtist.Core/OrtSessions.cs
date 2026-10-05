using Microsoft.ML.OnnxRuntime;

namespace AIrtist.Core;

public static class OrtSessions
{
    private static readonly object gpuSync = new();
    private static SessionOptions? gpuOptions;
    private static bool gpuShutdown;

    static OrtSessions() => AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownGpu();

    public static InferenceSession Open(string path, bool gpu, string? profile = null)
    {
        if (!gpu)
        {
            using var cpuOptions = CreateOptions(gpu: false);
            ConfigureProfiling(cpuOptions, profile);
            return new InferenceSession(path, cpuOptions);
        }

        lock (gpuSync)
        {
            if (gpuShutdown) throw new ObjectDisposedException(nameof(OrtSessions), "GPU 실행 환경이 종료되었습니다.");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "DirectML.dll")))
                throw new FileNotFoundException("패키지의 DirectML.dll 파일을 찾을 수 없습니다.");
            // Keep only the provider factory (DML device/queue) for the process lifetime.
            // Model sessions, execution contexts and weights are still disposed by callers.
            // Reconstructing the factory retains handles on the tested Windows/driver stack.
            gpuOptions ??= CreateOptions(gpu: true);
            try
            {
                ConfigureProfiling(gpuOptions, profile);
                return new InferenceSession(path, gpuOptions);
            }
            finally { gpuOptions.EnableProfiling = false; }
        }
    }

    // Call when the app/check runner exits. Active sessions retain their own native
    // provider reference; future GPU construction is refused, including late workers.
    public static void ShutdownGpu()
    {
        lock (gpuSync)
        {
            gpuShutdown = true;
            gpuOptions?.Dispose();
            gpuOptions = null;
        }
    }

    // After a native GPU failure, release the device/queue once all model sessions
    // of that generation have unwound. The next operation can prepare a fresh one.
    internal static void InvalidateGpu()
    {
        lock (gpuSync)
        {
            gpuOptions?.Dispose();
            gpuOptions = null;
        }
    }

    private static SessionOptions CreateOptions(bool gpu)
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = !gpu,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 6,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        try
        {
            if (gpu)
            {
                options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                options.AppendExecutionProvider("DML", new() { ["device_id"] = "0", ["disable_metacommands"] = "True" });
            }
            return options;
        }
        catch { options.Dispose(); throw; }
    }

    private static void ConfigureProfiling(SessionOptions options, string? profile)
    {
        if (profile is not null)
        {
            options.ProfileOutputPathPrefix = profile;
        }
        // ORT reads the prefix when EnableProfiling is set, not when a session opens.
        options.EnableProfiling = profile is not null;
    }

    public static float[] Run(InferenceSession session, NamedOnnxValue[] inputs, CancellationToken token,
        bool terminateOnCancellation = true)
    {
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        // GPU calls finish the current native operation before observing cancellation.
        // Terminating an in-flight DirectML call can leave the retained queue unusable.
        using var cancellation = terminateOnCancellation ? token.Register(() => options.Terminate = true) : default;
        try
        {
            using var output = session.Run(inputs, session.OutputMetadata.Keys.ToArray(), options);
            token.ThrowIfCancellationRequested();
            var values = output.First().AsTensor<float>().ToArray();
            if (values.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("AI 모델이 비정상적인 수치를 반환했습니다.");
            return values;
        }
        catch (OnnxRuntimeException) when (terminateOnCancellation && token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
