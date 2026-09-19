using Microsoft.ML.OnnxRuntime;

namespace AIrtist.Core;

public static class OrtSessions
{
    public static InferenceSession Open(string path, bool gpu, string? profile = null)
    {
        if (gpu && !File.Exists(Path.Combine(AppContext.BaseDirectory, "DirectML.dll")))
            throw new FileNotFoundException("패키지의 DirectML.dll 파일을 찾을 수 없습니다.");
        using var options = new SessionOptions
        {
            EnableMemoryPattern = !gpu,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 6,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        if (profile is not null)
        {
            options.ProfileOutputPathPrefix = profile;
            options.EnableProfiling = true;
        }
        if (gpu)
        {
            options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
            // ORT 1.22's generic API forwards these options to the DML factory.
            options.AppendExecutionProvider("DML", new() { ["device_id"] = "0", ["disable_metacommands"] = "True" });
        }
        return new InferenceSession(path, options);
    }

    public static float[] Run(InferenceSession session, NamedOnnxValue[] inputs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var cancellation = token.Register(() => options.Terminate = true);
        try
        {
            using var output = session.Run(inputs, session.OutputMetadata.Keys.ToArray(), options);
            token.ThrowIfCancellationRequested();
            var values = output.First().AsTensor<float>().ToArray();
            if (values.Any(v => !float.IsFinite(v)))
                throw new InvalidDataException("AI 모델이 비정상적인 수치를 반환했습니다.");
            return values;
        }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
