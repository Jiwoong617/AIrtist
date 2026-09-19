using System.IO;
using System.Text.Json;
using System.Windows;
using AIrtist.Core;

namespace AIrtist.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string root = Path.Combine(AppContext.BaseDirectory, "models");
        int overrideIndex = Array.IndexOf(e.Args, "--models");
        if (overrideIndex >= 0 && overrideIndex + 1 < e.Args.Length) root = e.Args[overrideIndex + 1];
        var assets = new RuntimeAssets(root);
        var window = new MainWindow(assets);
        MainWindow = window;
        if (e.Args.FirstOrDefault() is "--self-check" or "--smoke" or "--match-smoke")
        {
            string destination = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(destination);
            try
            {
                await window.InitializeAsync();
                assets.Verify(CancellationToken.None);
                window.RenderCheck(Path.Combine(destination, "app-ready.png"));
                if (e.Args[0] == "--match-smoke")
                    File.WriteAllText(Path.Combine(destination, "match-smoke.json"), JsonSerializer.Serialize(await window.MatchSmokeAsync(destination), RuntimeAssets.Json));
                if (e.Args[0] == "--smoke")
                {
                    var result = await window.SmokeAsync();
                    File.WriteAllBytes(Path.Combine(destination, "generated.png"), result.Png);
                    File.WriteAllText(Path.Combine(destination, "smoke.json"), JsonSerializer.Serialize(new { status = "passed", uiDispatcherTicks = window.SmokeDispatcherTicks, result.Translation, result.Cosine, result.Score, result.TranslationSeconds, result.GenerationSeconds, result.ScoringSeconds }, RuntimeAssets.Json));
                    window.RenderCheck(Path.Combine(destination, "app-result.png"), result.Png);
                }
                File.WriteAllText(Path.Combine(destination, "status.json"), "{\"status\":\"passed\"}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }, RuntimeAssets.Json));
                Shutdown(1);
            }
        }
        else
        {
            window.Show();
            await window.InitializeAsync();
        }
    }
}
