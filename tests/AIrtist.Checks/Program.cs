using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            string mode = args.FirstOrDefault() ?? "unit";
            string root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? ".");
            var assets = new RuntimeAssets(Path.Combine(root, "models/app-stage3"));
            string results = Path.Combine(root, "outputs/app-stage3");
            if (mode == "unit") { AssetAndWordChecks.Run(root); LobbyChecks.Run(); LobbySessionChecks.Run(); ConnectionBootstrapChecks.Run(); VotingChecks.Run(); MultiplayerChecks.Run(); GallerySyncChecks.Run(); ControlTransportChecks.Run(); ImageSendChecks.Run(); RoundDispatchChecks.Run(); await RoundRuntimeChecks.Run(); await MatchRuntimeChecks.Run(); await DrawingChecks.Run(); }
            else if (mode == "lobby") LobbyChecks.Run();
            else if (mode == "lobby-session") LobbySessionChecks.Run();
            else if (mode == "connection-bootstrap") ConnectionBootstrapChecks.Run();
            else if (mode == "match-runtime") await MatchRuntimeChecks.Run();
            else if (mode == "multiplayer-transfer") MultiplayerChecks.CheckGeneratedImages(root);
            else if (mode == "gallery-sync") GallerySyncChecks.CheckGeneratedImages(root);
            else if (mode == "control-loopback") await ControlTransportChecks.Loopback(root);
            else if (mode == "reliable-gallery") GallerySyncChecks.CheckReliableGeneratedImages(root);
            else if (mode == "round-dispatch") RoundDispatchChecks.CheckGeneratedImages(root);
            else if (mode == "image-recovery") RoundDispatchChecks.CheckImageRecovery(root);
            else if (mode == "reference") References(assets, root, results);
            else if (mode == "generate") Generate(assets, root, results, args.ElementAtOrDefault(2) ?? "csharp-generation");
            else if (mode == "smoke") await Smoke(assets, results);
            else if (mode == "stability") await DrawingStability.Run(assets, root);
            else if (mode == "soak") await DrawingSoak.Run(assets,
                args.ElementAtOrDefault(3) ?? Path.Combine(root, "outputs/drawing-soak", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")),
                int.Parse(args.ElementAtOrDefault(2) ?? "30", System.Globalization.CultureInfo.InvariantCulture));
            else if (mode == "resource-probe") DrawingResourceProbe.Run(assets,
                args.ElementAtOrDefault(2) ?? Path.Combine(root, "outputs/drawing-resource", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            else if (mode == "provider-probe") DrawingProviderProbe.Run(assets,
                args.ElementAtOrDefault(2) ?? Path.Combine(root, "outputs/drawing-provider", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            else if (mode == "gpu-lifetime") await GpuLifetimeChecks.Run(assets,
                args.ElementAtOrDefault(2) ?? Path.Combine(root, "outputs/gpu-lifetime", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            else if (mode == "quality-translation") QualityChecks.Translation(assets, root, args.ElementAtOrDefault(2) ?? "development");
            else if (mode == "quality-translation-baseline") TranslationBaselineChecks.Run(assets, root, args.ElementAtOrDefault(2) ?? "holdout");
            else throw new ArgumentException("Unknown check mode");
            Console.WriteLine(mode is "resource-probe" or "provider-probe" ? "RECORDED: session resources (not a leak-free assertion)"
                : mode.StartsWith("quality") ? $"RECORDED: {mode} (semantic review required)" : $"PASS: {mode}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { OrtSessions.ShutdownGpu(); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), RuntimeAssets.Json)!;
    private static void Save(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data, RuntimeAssets.Json));
    private static float[] Floats(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static void References(RuntimeAssets assets, string root, string results)
    {
        var clip = new ClipTokenizer(assets);
        foreach (var row in Read<JsonElement[]>(Path.Combine(results, "clip-tokenizer-reference.json")))
            Assert(clip.Encode(row.GetProperty("text").GetString()!).SequenceEqual(row.GetProperty("ids").EnumerateArray().Select(x => x.GetInt64())), "CLIP token mismatch");
        Console.WriteLine("CLIP tokenizer: 13 reference inputs passed");
        var translator = new LocalTranslator(assets);
        var translations = new List<object>();
        foreach (var row in Read<JsonElement[]>(Path.Combine(results, "translation-reference.json")))
        {
            string source = row.GetProperty("source").GetString()!;
            long[] actualIds = translator.Tokenize(source);
            Assert(actualIds.SequenceEqual(row.GetProperty("input_ids").EnumerateArray().Select(x => x.GetInt64())), $"Marian token mismatch: {source} ({string.Join(',', actualIds)})");
            string expected = row.GetProperty("compact").GetString()!;
            string actual = translator.Translate(source, CancellationToken.None);
            translations.Add(new { source, expected, actual });
            Save(Path.Combine(results, "csharp-translations.json"), translations);
            Assert(actual == expected, $"Translation mismatch: {actual} != {expected}");
        }
        Console.WriteLine("Marian tokenizer/inference: 12 reference inputs passed");
        var scheduler = new DpmScheduler(assets.Read<SchedulerData>("generation/scheduler.json"));
        var updates = Read<JsonElement[]>(Path.Combine(results, "scheduler-reference.json"));
        double schedulerError = 0;
        for (int i = 0; i < updates.Length; i++)
        {
            float[] Values(string key) => updates[i].GetProperty(key).EnumerateArray().Select(x => x.GetSingle()).ToArray();
            float[] actual = scheduler.Step(i, Values("before"), Values("noise"));
            float[] expected = Values("after");
            schedulerError = Math.Max(schedulerError, actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max());
        }
        Assert(schedulerError < .0001, $"Scheduler mismatch: {schedulerError}");
        Console.WriteLine($"DPM scheduler: 25 steps passed, max error {schedulerError}");
        Save(Path.Combine(results, "csharp-reference-checks.json"), new { schedulerError, translationCount = translations.Count });
    }

    private static void Generate(RuntimeAssets assets, string root, string results, string runName)
    {
        string output = Path.Combine(results, runName);
        Directory.CreateDirectory(output);
        var prompts = Read<JsonElement>(Path.Combine(root, "experiments/tiny_sd/prompts.json"));
        var generator = new LocalGenerator(assets);
        var rows = new List<object>();
        foreach (var item in prompts.GetProperty("cases").EnumerateArray().Where(x => new[] { "fruit_red", "animal_ears", "sphere_below" }.Contains(x.GetProperty("id").GetString())))
        {
            string id = item.GetProperty("id").GetString()!;
            string directory = Path.Combine(output, id);
            Directory.CreateDirectory(directory);
            var watch = Stopwatch.StartNew();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            byte[] png = generator.Generate(item.GetProperty("en").GetString()!, 20260913, deadline.Token, Console.WriteLine,
                directory, Floats(Path.Combine(results, "reference-initial-latents.f32")));
            File.WriteAllBytes(Path.Combine(output, id + ".png"), png);
            rows.Add(new { id, seconds = watch.Elapsed.TotalSeconds, bytes = png.Length });
            Save(Path.Combine(output, "results.json"), rows);
        }
    }

    private static async Task Smoke(RuntimeAssets assets, string results)
    {
        assets.Verify(CancellationToken.None);
        Directory.CreateDirectory(results);
        using var drawing = new DrawingController(new LocalDrawingAi(assets));
        await drawing.PrepareTranslationAsync(drawing.Snapshot.Version, "흰 토끼 한 마리");
        var preview = drawing.Snapshot;
        Assert(preview.Phase == DrawingPhase.Reviewing && preview.Draft is not null, "Translation preview failed");
        await drawing.GenerateAsync(preview.Version, preview.Draft!.Id, "a white rabbit with long ears", 42);
        var final = drawing.Snapshot;
        Assert(final.Phase == DrawingPhase.Completed && final.Result is not null, $"Smoke failed: {final.Message}");
        var result = final.Result!;
        File.WriteAllBytes(Path.Combine(results, "csharp-drawing-smoke.png"), result.Png);
        Save(Path.Combine(results, "csharp-drawing-smoke.json"), new { result.Description, result.Translation, result.TranslationSeconds, result.GenerationSeconds });
    }
}
