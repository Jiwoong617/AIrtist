using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIrtist.Core;

internal static class QualityChecks
{
    public static void Translation(RuntimeAssets assets, string root, string split)
    {
        if (split is not ("development" or "holdout")) throw new ArgumentException("Unknown split");
        string input = split == "development" ? "experiments/app_quality/translation-development.json" : "experiments/app_quality/translation-holdout.json";
        var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(root, input)))!;
        string output = Path.Combine(root, "outputs/app-quality");
        Directory.CreateDirectory(output);
        var translator = new LocalTranslator(assets);
        var candidate = new BeamSearchTranslator(assets);
        var rows = new List<object>();
        foreach (var item in cases)
        {
            string source = item.GetProperty("source").GetString()!;
            foreach (int beams in new[] { 1, 6 })
            {
                var watch = Stopwatch.StartNew();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                string? actual = null, error = null;
                try { actual = beams == 1 ? translator.Translate(source, deadline.Token) : candidate.Translate(source, deadline.Token, beams); }
                catch (Exception ex) { error = ex.Message; }
                rows.Add(new { source, beams, actual, error, seconds = watch.Elapsed.TotalSeconds });
                File.WriteAllText(Path.Combine(output, $"translation-{split}.json"), JsonSerializer.Serialize(rows, RuntimeAssets.Json));
                Console.WriteLine($"{beams}: {source} -> {actual ?? error} ({watch.Elapsed.TotalSeconds:F2}s)");
            }
        }
    }
}
