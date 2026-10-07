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
    private Window? child;

    internal MenuWindow(RuntimeAssets assets, string? settingsPath = null)
    {
        InitializeComponent(); this.assets = assets; this.settingsPath = settingsPath;
        ai = new SerializedDrawingAi(new LocalDrawingAi(assets));
        settings = GameSettings.Load(settingsPath);
        Resolution.ItemsSource = GameSettings.Sizes.Select(s => $"{s.Width} × {s.Height}").ToArray();
        settings.Apply(this);
        Closing += async (_, e) =>
        {
            if (stopped) return;
            if (closing) { e.Cancel = true; return; }
            if (busy || child is not null) { e.Cancel = true; return; }
            closing = true;
            if (onlineWorker is null) return;
            e.Cancel = true; IsEnabled = false;
            await onlineWorker.StopAsync(); stopped = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private async Task<bool> PrepareAsync()
    {
        if (busy || closing) return false;
        busy = true; Single.IsEnabled = Multi.IsEnabled = Settings.IsEnabled = Exit.IsEnabled = false;
        MenuStatus.Text = "그림 도구를 준비하고 있어요…";
        try
        {
            if (!prepared) await Task.Run(() => assets.Verify(CancellationToken.None));
            prepared = true; MenuStatus.Text = ""; return true;
        }
        catch (Exception) { MenuStatus.Text = "모델 파일을 확인할 수 없습니다. 앱 폴더 전체가 있는지 확인해 주세요."; return false; }
        finally { busy = false; Single.IsEnabled = Multi.IsEnabled = Settings.IsEnabled = Exit.IsEnabled = true; }
    }
    private void ShowScreen(Window screen)
    {
        child = screen; screen.Owner = this; settings.Apply(screen);
        try { Hide(); screen.ShowDialog(); }
        finally { child = null; if (!closing) { Show(); Activate(); } }
    }
    private async void SingleClicked(object sender, RoutedEventArgs e)
    {
        if (!await PrepareAsync()) return;
        var screen = new MainWindow(assets, ai);
        busy = true; IsEnabled = false;
        try { await screen.InitializeAsync(); }
        finally { busy = false; IsEnabled = true; }
        ShowScreen(screen);
    }
    private async void MultiClicked(object sender, RoutedEventArgs e)
    {
        if (!await PrepareAsync()) return;
        try
        {
            string words = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
            onlineWorker ??= new(MultiplayerCompatibility.FromVerifiedAssets(assets, words), WordCatalog.Load(words), ai);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--eos-config");
            string path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.GetFullPath("config/eos.local.json");
            ShowScreen(new OnlineRoomWindow(assets, ai, onlineWorker, path));
        }
        catch (Exception) { MenuStatus.Text = "멀티플레이 화면을 열지 못했습니다. 앱 파일과 설정 경로를 확인해 주세요."; }
    }
    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        Resolution.SelectedIndex = Array.IndexOf(GameSettings.Sizes, (settings.Width, settings.Height));
        Bgm.Value = settings.Bgm; Sfx.Value = settings.Sfx;
        MainMenu.Visibility = Visibility.Collapsed; SettingsPanel.Visibility = Visibility.Visible; MenuStatus.Text = "";
    }
    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (Resolution.SelectedIndex < 0) return;
        var size = GameSettings.Sizes[Resolution.SelectedIndex];
        var next = new GameSettings(size.Width, size.Height, (int)Bgm.Value, (int)Sfx.Value);
        try { next.Save(settingsPath); settings = next; settings.Apply(this); MenuStatus.Text = "설정을 저장했습니다."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MenuStatus.Text = "설정을 저장하지 못했습니다. 저장 폴더 권한을 확인해 주세요."; }
    }
    private void BackClicked(object sender, RoutedEventArgs e)
    { SettingsPanel.Visibility = Visibility.Collapsed; MainMenu.Visibility = Visibility.Visible; MenuStatus.Text = ""; Settings.Focus(); }
    private void ExitClicked(object sender, RoutedEventArgs e) => Close();
}
