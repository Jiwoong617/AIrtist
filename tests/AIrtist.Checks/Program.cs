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
            if (mode == "unit") { await Rules(); await MatchChecks.Run(); }
            else if (mode == "reference") References(assets, root, results);
            else if (mode == "generate") Generate(assets, root, results, args.ElementAtOrDefault(2) ?? "csharp-generation");
            else if (mode == "smoke") await Smoke(assets, results);
            else if (mode == "stability") await Stage4Checks.Stability(assets, root);
            else if (mode == "quality") Stage4Checks.Quality(assets, root);
            else throw new ArgumentException("Unknown check mode");
            Console.WriteLine(mode == "quality" ? "RECORDED: quality (semantic quality is not a pass/fail assertion)" : $"PASS: {mode}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
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
        var scorer = new ImageScorer(assets);
        var scores = new List<object>();
        foreach (var row in Read<JsonElement[]>(Path.Combine(results, "scoring-reference.json")))
        {
            string relative = row.GetProperty("image").GetString()!;
            byte[] png = File.ReadAllBytes(Path.Combine(root, relative));
            float[] expectedInput = Floats(Path.Combine(results, Path.GetFileNameWithoutExtension(relative) + "-scoring-input.f32"));
            float[] input = ImageCodec.Preprocess(png);
            double inputError = input.Zip(expectedInput, (a, b) => Math.Abs(a - b)).Max();
            Assert(inputError <= 1f / 255f / .26130258f + 1e-6, $"Image preprocess mismatch: {inputError}");
            double[] actual = scorer.Similarities(png, CancellationToken.None);
            double[] expected = row.GetProperty("cosines").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            double error = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Max();
            Assert(error < .002, $"Image score mismatch: {error}");
            scores.Add(new { image = relative, inputError, cosineError = error, actual });
        }
        Save(Path.Combine(results, "csharp-reference-checks.json"), new { schedulerError, scores, translationCount = translations.Count });
        Console.WriteLine("TinyCLIP preprocess/inference: 3 images passed");
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
        var controller = new RoundController(new LocalAi(assets));
        var word = assets.Read<ScoringData>("scoring/vectors.json").Words.Single(w => w.Id == "rabbit");
        RoundUpdate? final = null;
        controller.Updated += update => { Console.WriteLine($"{update.State}: {update.Message}"); final = update; };
        await controller.SubmitAsync(controller.NewRound(), word, "귀가 길고 하얀 털을 가진 작은 동물", 42, TimeSpan.FromMinutes(3));
        Assert(final?.State == RoundState.Completed && final.Result is not null, $"Smoke failed: {final}");
        var result = final!.Result!;
        File.WriteAllBytes(Path.Combine(results, "csharp-smoke.png"), result.Png);
        Save(Path.Combine(results, "csharp-smoke.json"), new { result.Translation, result.Cosine, result.Score, result.TranslationSeconds, result.GenerationSeconds, result.ScoringSeconds });
        controller.Dispose();
    }

    private static async Task Rules()
    {
        var word = new Word("apple", "사과", "과일", "apple", ["사과", "apple"]);
        foreach (string invalid in new[] { "사 과", "ＡＰＰＬＥ", "사\u200b과", "apple!", "", "<|endoftext|>" })
        {
            bool rejected = false;
            try { InputRules.Validate(invalid, word); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Alias/length bypass accepted");
        }
        var fake = new FakeAi();
        using var controller = new RoundController(fake);
        var updates = new List<RoundUpdate>();
        controller.Updated += update => updates.Add(update);
        long round = controller.NewRound();
        try { await controller.SubmitAsync(round, word, "사과", 1, TimeSpan.FromSeconds(1)); } catch (ArgumentException) { }
        await controller.SubmitAsync(round, word, "빨간 과일", 1, TimeSpan.FromSeconds(1));
        Assert(fake.Generated == 1 && updates.Last().State == RoundState.Completed, "Invalid submission consumed right");
        bool duplicate = false;
        try { await controller.SubmitAsync(round, word, "빨간 과일", 1, TimeSpan.FromSeconds(1)); } catch (InvalidOperationException) { duplicate = true; }
        Assert(duplicate && fake.Generated == 1, "Duplicate generated twice");
        fake.Translation = "an apple";
        await controller.SubmitAsync(controller.NewRound(), word, "빨간 과일", 1, TimeSpan.FromSeconds(1));
        Assert(updates.Last().State == RoundState.Failed && fake.Generated == 1, "Answer leak reached generator");
        fake.Translation = "a red fruit";
        fake.Block = true;
        round = controller.NewRound();
        Task pending = controller.SubmitAsync(round, word, "빨간 과일", 1, TimeSpan.FromSeconds(5));
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int generatedBeforeDuplicate = fake.Generated;
        duplicate = false;
        try { await controller.SubmitAsync(round, word, "빨간 과일", 1, TimeSpan.FromSeconds(1)); } catch (InvalidOperationException) { duplicate = true; }
        Assert(duplicate && fake.Generated == generatedBeforeDuplicate, "Concurrent duplicate reached generator");
        controller.NewRound();
        int before = updates.Count;
        fake.Finish.SetResult();
        await pending;
        Assert(updates.Count == before, "Stale result reached new round");
        fake.Block = false;
        fake.Delay = true;
        await controller.SubmitAsync(controller.NewRound(), word, "빨간 과일", 1, TimeSpan.FromMilliseconds(20));
        Assert(updates.Last().State == RoundState.TimedOut, "Timeout incorrectly completed");
        Task cancel = controller.SubmitAsync(controller.NewRound(), word, "빨간 과일", 1, TimeSpan.FromSeconds(2));
        controller.Cancel();
        await cancel;
        Assert(updates.Last().State == RoundState.Cancelled, "Cancellation incorrectly completed");
        Console.WriteLine("Rules: alias normalization, invalid/duplicate submissions, translated answer leak, stale result, timeout and cancellation passed");
    }

    private sealed class FakeAi : ILocalAi
    {
        public int Generated;
        public string Translation = "a red fruit";
        public bool Block, Delay;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Translate(string description, CancellationToken token) => Translation;
        public byte[] Generate(string translation, int seed, CancellationToken token, Action<string> progress)
        {
            Generated++;
            if (Block) { Started.SetResult(); Finish.Task.GetAwaiter().GetResult(); }
            if (Delay) Task.Delay(5000, token).GetAwaiter().GetResult();
            return [1, 2, 3];
        }
        public (double Cosine, double Score) Score(byte[] png, string wordId, CancellationToken token) => (.2, 33.3);
    }
}
