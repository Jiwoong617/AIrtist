using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AIrtist.Core;

public sealed record GenerationSettings(int Width, int Height, int Steps, float Guidance, string Style);

public sealed class LocalGenerator(RuntimeAssets assets)
{
    public byte[] Generate(string description, int seed, CancellationToken token, Action<string>? progress = null,
        string? profileDirectory = null, float[]? initialLatents = null, GenerationParameters? parameters = null)
    {
        var selected = parameters ?? new GenerationParameters(Seed: seed);
        selected.Validate();
        try { return GenerateCore(description, selected.Seed ?? seed, token, progress, profileDirectory, initialLatents, selected); }
        catch (OnnxRuntimeException)
        {
            // All using scopes in GenerateCore have released their native sessions.
            OrtSessions.InvalidateGpu();
            throw;
        }
    }

    private byte[] GenerateCore(string description, int seed, CancellationToken token, Action<string>? progress,
        string? profileDirectory, float[]? initialLatents, GenerationParameters parameters)
    {
        var settings = assets.Read<GenerationSettings>("generation/settings.json");
        var baseline = assets.Read<SchedulerData>("generation/scheduler.json");
        var scheduler = new DpmScheduler(parameters.Steps == 25 ? baseline : DpmScheduler.CreateSchedule(parameters.Steps));
        if (settings.Width != 512 || settings.Height != 512 || settings.Steps != 25 || baseline.Steps.Length != 25)
            throw new InvalidDataException("검증되지 않은 생성 설정입니다.");
        var tokenizer = new ClipTokenizer(assets);
        long[] ids = tokenizer.Encode("").Concat(tokenizer.Encode(description + ", " + settings.Style)).ToArray();
        string? Profile(string name) => profileDirectory is null ? null : Path.Combine(profileDirectory, name);
        void End(InferenceSession session) { if (profileDirectory is not null) session.EndProfiling(); }
        float[] embeddings;
        progress?.Invoke("묘사를 그림으로 준비하고 있어요");
        using (var text = OrtSessions.Open(assets.File("generation/text_encoder-compact.onnx"), true, Profile("text")))
        {
            embeddings = OrtSessions.Run(text, [NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [2, 77]))], token, terminateOnCancellation: false);
            End(text);
        }
        float[] latents = initialLatents?.ToArray() ?? Gaussian(seed, 4 * 64 * 64);
        if (latents.Length != 4 * 64 * 64) throw new ArgumentException("잘못된 초기 잠재값 크기입니다.");
        using (var unet = OrtSessions.Open(assets.File("generation/unet-w8.onnx"), true, Profile("unet")))
        {
            for (int step = 0; step < scheduler.Steps.Length; step++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke($"그림을 그리고 있어요 · {step + 1}/{parameters.Steps}");
                float[] batch = latents.Concat(latents).ToArray();
                float[] noise = OrtSessions.Run(unet, [
                    NamedOnnxValue.CreateFromTensor("sample", new DenseTensor<float>(batch, [2, 4, 64, 64])),
                    NamedOnnxValue.CreateFromTensor("timestep", new DenseTensor<float>(new[] { (float)scheduler.Steps[step].Timestep }, [1])),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", new DenseTensor<float>(embeddings, [2, 77, 768]))
                ], token, terminateOnCancellation: false);
                var guided = new float[latents.Length];
                for (int i = 0; i < guided.Length; i++) guided[i] = noise[i] + parameters.Guidance * (noise[i + guided.Length] - noise[i]);
                latents = scheduler.Step(step, latents, guided);
            }
            End(unet);
        }
        progress?.Invoke("그림을 완성하고 있어요");
        using var decoder = OrtSessions.Open(assets.File("generation/taesd_decoder-compact.onnx"), true, Profile("decoder"));
        float[] decoded = OrtSessions.Run(decoder, [NamedOnnxValue.CreateFromTensor("latents", new DenseTensor<float>(latents, [1, 4, 64, 64]))], token, terminateOnCancellation: false);
        End(decoder);
        return ImageCodec.Png(decoded);
    }

    private static float[] Gaussian(int seed, int count)
    {
        var random = new Random(seed);
        var values = new float[count];
        for (int i = 0; i < count; i += 2)
        {
            double radius = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
            double angle = 2 * Math.PI * random.NextDouble();
            values[i] = (float)(radius * Math.Cos(angle));
            values[i + 1] = (float)(radius * Math.Sin(angle));
        }
        return values;
    }
}
