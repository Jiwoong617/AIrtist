using System.IO;
using System.Text.Json;
using System.Windows;
using AIrtist.Core;

namespace AIrtist.App;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        OrtSessions.ShutdownGpu();
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--voting-smoke")
        {
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try
            {
                var preview = new VotingWindow(); MainWindow = preview;
                var report = preview.Smoke(output, e.Args.Skip(2).ToArray());
                File.WriteAllText(Path.Combine(output, "voting-smoke.json"), JsonSerializer.Serialize(report, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"passed\"}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }, RuntimeAssets.Json));
                Shutdown(1);
            }
            return;
        }
        string root = Path.Combine(AppContext.BaseDirectory, "models");
        int overrideIndex = Array.IndexOf(e.Args, "--models");
        if (overrideIndex >= 0 && overrideIndex + 1 < e.Args.Length) root = e.Args[overrideIndex + 1];
        var assets = new RuntimeAssets(root);
        if (e.Args.FirstOrDefault() == "--menu-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            var menu = new MenuWindow(assets, Path.Combine(output, "settings.json")); MainWindow = menu;
            try
            {
                var report = await menu.SmokeAsync(output);
                File.WriteAllText(Path.Combine(output, "menu-smoke.json"), JsonSerializer.Serialize(report, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"passed\"}"); Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }, RuntimeAssets.Json)); Shutdown(1);
            }
            return;
        }
        if (e.Args.FirstOrDefault() == "--online-live-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int configIndex = Array.IndexOf(e.Args, "--eos-config");
            if (e.Args.Length < 2 || configIndex < 0 || configIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try
            {
                await Task.Run(() => assets.Verify(CancellationToken.None));
                var report = await OnlineLiveSmoke.Run(assets, output, Path.GetFullPath(e.Args[configIndex + 1]));
                File.WriteAllText(Path.Combine(output, "online-live-smoke.json"), JsonSerializer.Serialize(report, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"passed\"}"); Shutdown(0);
            }
            catch (Exception ex)
            {
                // This path uses real credentials: never serialize arbitrary exceptions or snapshots.
                string error = ex is OnlineLiveCheckException ? ex.Message : $"온라인 실제 검사 실패: {ex.GetType().Name}";
                File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "failed", error }, RuntimeAssets.Json)); Shutdown(1);
            }
            return;
        }
        if (e.Args.FirstOrDefault() == "--online-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try
            {
                await Task.Run(() => assets.Verify(CancellationToken.None));
                var report = await OnlineSmoke.Run(assets, output);
                var fixedReport = await OnlineSmoke.Run(assets, Path.Combine(output, "fixed"), locked: true);
                File.WriteAllText(Path.Combine(output, "fixed-online-smoke.json"), JsonSerializer.Serialize(fixedReport, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "online-smoke.json"), JsonSerializer.Serialize(report, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"passed\"}"); Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }, RuntimeAssets.Json)); Shutdown(1);
            }
            return;
        }
        if (e.Args.FirstOrDefault() is "--round-smoke" or "--match-smoke" or "--lobby-smoke")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            RoundPreviewWindow? preview = null;
            try
            {
                await Task.Run(() => assets.Verify(CancellationToken.None));
                preview = new RoundPreviewWindow(assets, new SerializedDrawingAi(new LocalDrawingAi(assets))); MainWindow = preview;
                var report = e.Args[0] == "--lobby-smoke" ? await preview.LobbySmokeAsync(output)
                    : await preview.SmokeAsync(output, e.Args[0] == "--match-smoke" ? 2 : 1);
                File.WriteAllText(Path.Combine(output, e.Args[0][2..] + ".json"), JsonSerializer.Serialize(report, RuntimeAssets.Json));
                File.WriteAllText(Path.Combine(output, "status.json"), "{\"status\":\"passed\"}");
                preview.Close(); Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "status.json"), JsonSerializer.Serialize(new { status = "failed", error = ex.ToString() }, RuntimeAssets.Json));
                preview?.Close(); Shutdown(1);
            }
            return;
        }
        if (e.Args.FirstOrDefault() is not ("--self-check" or "--smoke" or "--drawing-smoke" or "--parameter-smoke"))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var menu = new MenuWindow(assets); MainWindow = menu; menu.Show(); return;
        }
        var window = new MainWindow(assets);
        MainWindow = window;
        if (e.Args.FirstOrDefault() is "--self-check" or "--smoke" or "--drawing-smoke" or "--parameter-smoke")
        {
            string destination = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(destination);
            try
            {
                await window.InitializeAsync();
                assets.Verify(CancellationToken.None);
                window.RenderCheck(Path.Combine(destination, "app-ready.png"));
                if (e.Args[0] == "--parameter-smoke")
                    File.WriteAllText(Path.Combine(destination, "parameter-smoke.json"), JsonSerializer.Serialize(await window.ParameterSmokeAsync(destination), RuntimeAssets.Json));
                if (e.Args[0] == "--drawing-smoke")
                    File.WriteAllText(Path.Combine(destination, "drawing-smoke.json"), JsonSerializer.Serialize(await window.DrawingSmokeAsync(destination), RuntimeAssets.Json));
                if (e.Args[0] == "--smoke")
                {
                    var result = await window.SmokeAsync(destination);
                    File.WriteAllBytes(Path.Combine(destination, "generated.png"), result.Png);
                    File.WriteAllText(Path.Combine(destination, "smoke.json"), JsonSerializer.Serialize(new { status = "passed", uiDispatcherTicks = window.SmokeDispatcherTicks, result.Description, result.Translation, result.Seed, result.TranslationSeconds, result.GenerationSeconds }, RuntimeAssets.Json));
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
