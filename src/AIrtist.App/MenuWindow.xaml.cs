using System.IO;
using System.Windows;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MenuWindow : Window
{
    private readonly RuntimeAssets assets;
    private readonly IDrawingAi ai;
    private readonly string? settingsPath;
    private GameSettings settings;
    private OnlineSessionWorker? onlineWorker;
    private bool prepared, busy, closing, stopped;
    private GameScreen? child;

    internal MenuWindow(RuntimeAssets assets, string? settingsPath = null, IDrawingAi? ai = null, OnlineSessionWorker? worker = null)
    {
        InitializeComponent(); this.assets = assets; this.settingsPath = settingsPath;
        this.ai = ai ?? new SerializedDrawingAi(new LocalDrawingAi(assets)); onlineWorker = worker;
        settings = GameSettings.Load(settingsPath); ThemeManager.Apply(settings.DarkMode);
        Resolution.ItemsSource = GameSettings.Sizes.Select(s => $"{s.Width} × {s.Height}").ToArray();
        settings.Apply(this);
        Closing += async (_, e) =>
        {
            if (stopped) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; IsEnabled = false;
            // Wait for initialization to finish before disposing its screen.
            while (busy) await Task.Delay(25);
            if (child is not null) await child.CloseAsync();
            if (onlineWorker is not null) await onlineWorker.StopAsync();
            stopped = true; _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private async Task<bool> PrepareAsync()
    {
        if (busy || closing) return false;
        busy = true; Single.IsEnabled = Multi.IsEnabled = false;
        MenuStatus.Text = "그림 도구를 준비하고 있어요…";
        try
        {
            if (!prepared) await Task.Run(() => assets.Verify(CancellationToken.None));
            prepared = true; MenuStatus.Text = ""; return !closing;
        }
        catch (Exception) { MenuStatus.Text = "모델 파일을 확인할 수 없습니다. 앱 폴더 전체가 있는지 확인해 주세요."; return false; }
        finally { busy = false; Single.IsEnabled = Multi.IsEnabled = true; }
    }
    private void ShowScreen(GameScreen screen)
    {
        child = screen; ScreenHost.Content = screen; MenuPage.Visibility = Visibility.Collapsed;
        screen.Closed += (_, _) =>
        {
            if (child != screen) return;
            ScreenHost.Content = null; child = null;
            if (!closing) { MenuPage.Visibility = Visibility.Visible; Single.Focus(); }
        };
    }
    private async void SingleClicked(object sender, RoutedEventArgs e)
    {
        if (!await PrepareAsync()) return;
        var screen = new MainWindow(assets, ai); ShowScreen(screen); busy = true;
        try { await screen.InitializeAsync(); }
        finally { busy = false; }
    }
    private async void MultiClicked(object sender, RoutedEventArgs e)
    {
        if (!await PrepareAsync()) return;
        try
        {
            string words = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
            onlineWorker ??= new(MultiplayerCompatibility.FromVerifiedAssets(assets, words), WordCatalog.Load(words), ai);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--eos-config");
            string path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : "";
            ShowScreen(new OnlineRoomWindow(assets, ai, onlineWorker, path));
        }
        catch (Exception) { MenuStatus.Text = "멀티플레이 화면을 열지 못했습니다. 앱 파일과 설정 경로를 확인해 주세요."; }
    }
    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) { BackClicked(sender, e); return; }
        Resolution.SelectedIndex = Array.IndexOf(GameSettings.Sizes, (settings.Width, settings.Height));
        Bgm.Value = settings.Bgm; Sfx.Value = settings.Sfx; ThemeChoice.SelectedIndex = settings.DarkMode ? 1 : 0;
        SettingsOverlay.Visibility = Visibility.Visible; ScreenHost.IsEnabled = MenuPage.IsEnabled = false;
        MenuStatus.Text = ""; ThemeChoice.Focus();
    }
    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (Resolution.SelectedIndex < 0) return;
        var size = GameSettings.Sizes[Resolution.SelectedIndex];
        var next = new GameSettings(size.Width, size.Height, (int)Bgm.Value, (int)Sfx.Value, ThemeChoice.SelectedIndex == 1);
        try
        {
            next.Save(settingsPath);
            bool resize = next.Width != settings.Width || next.Height != settings.Height;
            settings = next;
            if (resize) settings.Apply(this);
            ThemeManager.Apply(settings.DarkMode); SettingsStatus.Text = "설정을 저장했습니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SettingsStatus.Text = "설정을 저장하지 못했습니다."; }
    }
    private void BackClicked(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed; ScreenHost.IsEnabled = MenuPage.IsEnabled = true;
        SettingsStatus.Text = ""; Settings.Focus();
    }
    private void ExitClicked(object sender, RoutedEventArgs e) => Close();
}
