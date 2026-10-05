using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;

internal static class TranslationBaselineChecks
{
    public static void Run(RuntimeAssets assets, string root, string split)
    {
        if (split is not ("development" or "holdout"))
            throw new ArgumentException("Unknown translation comparison");
        assets.Verify(CancellationToken.None);
        string output = Path.Combine(root, "outputs/translation-quality");
        Directory.CreateDirectory(output);
        var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(root, $"experiments/translation_quality/{split}.json")))!;
        var translator = new LocalTranslator(assets);
        var rows = new List<object>();
        foreach (var item in cases)
        {
            string source = item.GetProperty("source").GetString()!;
            long[] ids = translator.Tokenize(source);
            var watch = Stopwatch.StartNew();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string actual = translator.Translate(source, deadline.Token);
            double seconds = watch.Elapsed.TotalSeconds;
            rows.Add(new { source, actual, seconds, input_ids = ids });
            File.WriteAllText(Path.Combine(output, $"csharp-baseline-{split}.json"), JsonSerializer.Serialize(rows, RuntimeAssets.Json));
            Console.WriteLine($"{source} -> {actual} ({seconds:F2}s)");
        }
        Console.WriteLine($"Recorded {rows.Count} translations; semantic review is separate from runtime parity.");
    }
}
