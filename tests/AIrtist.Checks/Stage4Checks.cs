using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

internal static class Stage4Checks
{
    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, RuntimeAssets.Json));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static async Task Stability(RuntimeAssets assets, string root)
    {
        string output = Path.Combine(root, "outputs/app-stage4/stability-natural");
        Directory.CreateDirectory(output);
        assets.Verify(CancellationToken.None);
        var words = assets.Read<ScoringData>("scoring/vectors.json").Words;
        var rows = new List<object>();
        using var controller = new RoundController(new LocalAi(assets));
        RoundUpdate? final = null;
        bool cancelAtStep = false;
        controller.Updated += update =>
        {
            final = update;
            if (cancelAtStep && update.Message.EndsWith("3/25")) { cancelAtStep = false; controller.Cancel(); }
        };
        var cases = new[] {
            ("complete-1", "rabbit", "귀가 길고 하얀 털을 가진 작은 동물", RoundState.Completed),
            ("complete-2", "apple", "아삭아삭하고 빨간 과일", RoundState.Completed),
            ("cancel-during-unet", "rabbit", "귀가 길고 하얀 털을 가진 작은 동물", RoundState.Cancelled),
            ("recover-after-cancel", "apple", "아삭아삭하고 빨간 과일", RoundState.Completed),
            ("timeout", "rabbit", "귀가 길고 하얀 털을 가진 작은 동물", RoundState.TimedOut),
            ("recover-after-timeout", "rabbit", "귀가 길고 하얀 털을 가진 작은 동물", RoundState.Completed),
            ("complete-5", "apple", "아삭아삭하고 빨간 과일", RoundState.Completed),
            ("complete-6", "rabbit", "귀가 길고 하얀 털을 가진 작은 동물", RoundState.Completed)
        };
        using var process = Process.GetCurrentProcess();
        foreach (var (name, id, description, expected) in cases)
        {
            cancelAtStep = expected == RoundState.Cancelled;
            final = null;
            long peakWorking = 0, peakPrivate = 0;
            using var monitoring = new CancellationTokenSource();
            Task sampler = Task.Run(async () => {
                try { while (true) {
                    process.Refresh();
                    peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    await Task.Delay(100, monitoring.Token);
                } } catch (OperationCanceledException) { }
            });
            var watch = Stopwatch.StartNew();
            await controller.SubmitAsync(controller.NewRound(), words.Single(w => w.Id == id), description, 42,
                expected == RoundState.TimedOut ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(180));
            monitoring.Cancel();
            await sampler;
            double seconds = watch.Elapsed.TotalSeconds;
            Require(final?.State == expected, $"{name}: expected {expected}, got {final?.State}: {final?.Message}");
            var result = final!.Result;
            if (result is not null) File.WriteAllBytes(Path.Combine(output, name + ".png"), result.Png);
            // Match the product: no forced collection between rounds.
            process.Refresh();
            long naturalIdlePrivate = process.PrivateMemorySize64;
            await Task.Delay(250);
            process.Refresh();
            rows.Add(new { name, state = final.State.ToString(), seconds, peakWorking, peakPrivate,
                naturalIdlePrivate, settledIdlePrivate = process.PrivateMemorySize64,
                result?.Translation, result?.Cosine, result?.Score, result?.TranslationSeconds, result?.GenerationSeconds, result?.ScoringSeconds });
            Save(Path.Combine(output, "results.json"), rows);
            Console.WriteLine($"{name}: {final.State}, {seconds:0.00}s, private peak {peakPrivate / 1_000_000} MB");
        }
    }

    public static void Quality(RuntimeAssets assets, string root)
    {
        string output = Path.Combine(root, "outputs/app-stage4/quality");
        Directory.CreateDirectory(output);
        var translator = new LocalTranslator(assets);
        string[] descriptions = [
            "아삭아삭하고 빨간 과일", "초록색의 둥근 과일", "길고 노란 과일", "껍질을 벗겨 먹는 과일",
            "귀가 길고 하얀 털을 가진 작은 동물", "귀가 짧고 검은 털을 가진 작은 동물",
            "비 오는 날 머리 위로 펼치는 물건", "빗물을 막는 접을 수 있는 물건",
            "손잡이가 달린 작은 음료 용기", "손잡이가 없는 파란 음료 용기",
            "검은 줄무늬가 있는 주황색 큰 고양잇과 동물", "갈기가 있는 큰 고양잇과 동물",
            "멍멍 짖는 작은 반려동물", "꼬리를 흔들며 사람을 따르는 동물",
            "도로를 달리는 네 바퀴 탈것", "바퀴가 두 개인 탈것", "노란 상자 위의 빨간 공",
            "노란 상자 아래의 빨간 공", "파란 공 두 개", "파란 공 세 개", "빨간색이 아닌 초록색 과일",
            "a small animal with long ears and white fur", "하얀 배경에 빨간 과일 하나", "빨간 과일과 파란 용기"
        ];
        var translations = new List<object>();
        foreach (string source in descriptions)
        {
            var watch = Stopwatch.StartNew();
            try { translations.Add(new { source, translation = translator.Translate(source, CancellationToken.None), seconds = watch.Elapsed.TotalSeconds }); }
            catch (Exception ex) { translations.Add(new { source, error = ex.Message }); }
            Save(Path.Combine(output, "translations.json"), translations);
        }
        var scorer = new ImageScorer(assets);
        var rows = new List<object>();
        var samples = new List<(string Name, string Category, string? Expected, byte[] Png)> {
            ("generated-red-fruit", "generated", "apple", File.ReadAllBytes(Path.Combine(root, "outputs/app-stage3/csharp-generation-bundled/fruit_red.png"))),
            ("generated-white-animal", "generated", "rabbit", File.ReadAllBytes(Path.Combine(root, "outputs/app-stage3/packaged-smoke-03/generated.png"))),
            ("geometric-scene", "unrelated", null, File.ReadAllBytes(Path.Combine(root, "outputs/app-stage3/csharp-generation-bundled/sphere_below.png")))
        };
        foreach (string label in new[] { "apple", "rabbit", "cup", "사과", "토끼", "컵", "blank", "noise", "pear", "apple-drawing", "apple-outline", "cup-drawing", "cup-outline" })
            samples.Add((label, label.Contains("drawing") || label.Contains("outline") ? "controlled-drawing" : label == "pear" ? "near-wrong" : label is "blank" or "noise" ? "unrelated" : "text-only",
                label.StartsWith("apple-") ? "apple" : label.StartsWith("cup-") ? "cup" : null, DrawFixture(label)));
        foreach (var (name, category, expected, png) in samples)
        {
            File.WriteAllBytes(Path.Combine(output, name + ".png"), png);
            var cosines = scorer.Similarities(png, CancellationToken.None);
            var ordered = scorer.Data.Words.Select((word, i) => new { word.Id, cosine = cosines[i], score = Math.Clamp((cosines[i] - scorer.Data.Score_Floor) / (scorer.Data.Score_Ceiling - scorer.Data.Score_Floor) * 100, 0, 100) }).OrderByDescending(x => x.cosine).ToArray();
            rows.Add(new { name, category, expected, ranking = ordered });
        }
        Save(Path.Combine(output, "scores.json"), rows);
        Console.WriteLine($"Quality data recorded: {translations.Count} translations, {rows.Count} images. Semantic review required; this is not a quality pass.");
    }

    // Transparent, reproducible diagnostic drawings, not replacement game generation.
    private static byte[] DrawFixture(string label)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 512, 512));
            bool outline = label.EndsWith("outline");
            var pen = new Pen(Brushes.Black, 8);
            if (label.StartsWith("apple-"))
            {
                drawing.DrawEllipse(outline ? Brushes.White : Brushes.Red, pen, new Point(219, 280), 85, 105);
                drawing.DrawEllipse(outline ? Brushes.White : Brushes.Red, pen, new Point(289, 280), 85, 105);
                drawing.DrawLine(new Pen(Brushes.SaddleBrown, 15), new Point(254, 195), new Point(268, 136));
                drawing.DrawEllipse(outline ? Brushes.White : Brushes.Green, pen, new Point(300, 155), 36, 15);
            }
            else if (label.StartsWith("cup-"))
            {
                drawing.DrawEllipse(null, new Pen(Brushes.Black, 18), new Point(350, 278), 63, 65);
                drawing.DrawRoundedRectangle(outline ? Brushes.White : Brushes.SkyBlue, pen, new Rect(145, 178, 192, 195), 18, 18);
                drawing.DrawEllipse(Brushes.White, pen, new Point(241, 178), 96, 24);
            }
            else if (label == "pear")
            {
                drawing.DrawEllipse(Brushes.YellowGreen, pen, new Point(256, 302), 100, 95);
                drawing.DrawEllipse(Brushes.YellowGreen, null, new Point(256, 230), 56, 100);
                drawing.DrawLine(new Pen(Brushes.SaddleBrown, 13), new Point(256, 145), new Point(270, 110));
            }
            else if (label == "noise")
            {
                var random = new Random(42);
                for (int y = 0; y < 512; y += 8) for (int x = 0; x < 512; x += 8)
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256))), null, new Rect(x, y, 8, 8));
            }
            else if (label != "blank")
            {
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), 85, Brushes.Black, 1);
                drawing.DrawText(text, new Point((512 - text.Width) / 2, (512 - text.Height) / 2));
            }
        }
        var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
