using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MenuWindow
{
    internal bool SmokeAtMainMenu => child is null && MenuPage.Visibility == Visibility.Visible && ScreenHost.Content is null;
    internal async Task<OnlineRoomWindow> SmokeMultiplayerAsync()
    {
        MultiClicked(this, new());
        await OnlineSmoke.Until(() => child is OnlineRoomWindow);
        Require(Application.Current.Windows.Cast<Window>().Count(w => w.IsVisible) == 1, "More than one window is visible");
        return (OnlineRoomWindow)child!;
    }
    internal void SmokeSettings(bool dark, string path)
    {
        var screen = child; var game = (child as OnlineRoomWindow)?.Game;
        ShellPage.UpdateLayout();
        var point = Settings.TranslatePoint(new Point(Settings.ActualWidth, Settings.ActualHeight / 2), ShellPage);
        var right = ShellPage.ActualWidth - point.X;
        SettingsClicked(this, new()); ThemeChoice.SelectedIndex = dark ? 1 : 0; ApplyClicked(this, new());
        Require(ThemeManager.Dark == dark && GameSettings.Load(settingsPath).DarkMode == dark, "Theme was not saved");
        Require(child == screen && (child as OnlineRoomWindow)?.Game == game, "Settings replaced the active screen");
        Capture(ShellPage, path);
        BackClicked(this, new());
        ShellPage.UpdateLayout();
        var after = Settings.TranslatePoint(new Point(Settings.ActualWidth, Settings.ActualHeight / 2), ShellPage);
        Require(Math.Abs(ShellPage.ActualWidth - after.X - right) < 1 && Math.Abs(after.Y - point.Y) < 1,
            $"Settings anchor moved: {right}/{point.Y} -> {ShellPage.ActualWidth - after.X}/{after.Y}");
    }
    internal async Task<object> SmokeAsync(string output)
    {
        Show();
        var labels = new ParameterEditor();
        foreach (var name in new[] { "GuidanceLabel", "StepsLabel", "SeedLabel" })
        {
            var label = (System.Windows.Controls.TextBlock)labels.FindName(name);
            Require(System.Windows.Controls.ToolTipService.GetInitialShowDelay(label) == 1000 &&
                System.Windows.Controls.ToolTipService.GetShowOnDisabled(label) && label.ToolTip is string { Length: > 0 }, "Parameter tooltip missing");
        }
        Require(MainMenu.Visibility == Visibility.Visible && SettingsOverlay.Visibility == Visibility.Collapsed, "Startup is not the main menu");
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Capture(ShellPage, Path.Combine(output, "main-menu.png"));
        SettingsClicked(this, new());
        Resolution.SelectedIndex = 1; Bgm.Value = 34; Sfx.Value = 81; ThemeChoice.SelectedIndex = 1; ApplyClicked(this, new());
        Require(GameSettings.Load(settingsPath) == new GameSettings(1280, 900, 34, 81, true), "Settings did not persist");
        Capture(ShellPage, Path.Combine(output, "settings-dark.png")); BackClicked(this, new());
        SingleClicked(this, new());
        await OnlineSmoke.Until(() => child is MainWindow drawing && drawing.SmokeInitialized);
        var drawingWindow = (MainWindow)child!;
        Require(IsVisible && ScreenHost.Content == drawingWindow, "Single screen left the shell");
        SmokeSettings(true, Path.Combine(output, "drawing-settings.png"));
        var drawingTask = drawingWindow.OneClickSmokeAsync(output);
        SmokeSettings(false, Path.Combine(output, "generating-settings.png"));
        object drawingReport = await drawingTask;
        await Task.Delay(400);
        Require(audio.Mixer.Count(SoundCue.GenerationComplete) == 3, "Single drawing completion sound count");
        Require(audio.Mixer.MusicChanges == 2, "Drawing settings restarted music");
        Require(new System.Windows.Interop.WindowInteropHelper(this).Handle == handle && Application.Current.Windows.Count == 1, "Single created another native window");
        SmokeSettings(false, Path.Combine(output, "settings-light.png")); Capture(ShellPage, Path.Combine(output, "drawing-light.png"));
        await drawingWindow.CloseAsync();
        var room = await SmokeMultiplayerAsync();
        SmokeSettings(true, Path.Combine(output, "multiplayer-settings.png"));
        Capture(ShellPage, Path.Combine(output, "multiplayer-dark.png"));
        await room.CloseAsync();
        SmokeSettings(false, Path.Combine(output, "main-settings.png"));
        Require(new System.Windows.Interop.WindowInteropHelper(this).Handle == handle && Application.Current.Windows.Count == 1, "Navigation replaced the window");
        bool exited = false; Closed += (_, _) => exited = true;
        ExitClicked(this, new()); await OnlineSmoke.Until(() => exited);
        return new { status = "passed", singleWindow = true, parameterTooltipsDelayMs = 1000, themeSaved = true, settingsEverywhere = true, navigation = true, resolution = true, savedVolumes = true, exit = true, drawingReport,
            audio = audio.Mixer.Counts(), musicChanges = audio.Mixer.MusicChanges };
    }    internal static void Capture(FrameworkElement view, string destination)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public partial class MainWindow
{
    internal bool SmokeInitialized => drawing is not null;
    internal async Task<object> OneClickSmokeAsync(string output)
    {
        if (drawing is null) throw new InvalidOperationException("Drawing was not initialized");
        Description.Text = "빨간 사과 한 개"; Parameters.Set(new(7, 8, 42));
        await ClickAndWait();
        var first = drawing.Snapshot.Result ?? throw new InvalidOperationException(StatusLabel.Text);
        if (first.Description != Description.Text || string.IsNullOrWhiteSpace(first.Translation) || ReviewPanel.Visibility != Visibility.Collapsed || !Description.IsEnabled)
            throw new InvalidOperationException("One-click translation/generation flow failed");
        File.WriteAllBytes(Path.Combine(output, "one-click.png"), first.Png);
        MenuWindow.Capture(Page, Path.Combine(output, "drawing.png"));
        await ClickAndWait();
        var second = drawing.Snapshot.Result!;
        if (ReferenceEquals(first, second) || !first.Png.SequenceEqual(second.Png)) throw new InvalidOperationException("One-click repeat failed");
        Description.Text = "a blue bird on a branch"; Parameters.Set(new(5.5f, 8, 43));
        await ClickAndWait();
        var third = drawing.Snapshot.Result!;
        if (third.Description != Description.Text || ReferenceEquals(second, third) || third.Seed != 43) throw new InvalidOperationException("Editing original input failed");
        File.WriteAllBytes(Path.Combine(output, "one-click-edited.png"), third.Png);
        MenuWindow.Capture(Page, Path.Combine(output, "drawing-edited.png"));
        return new { generated = 3, koreanTranslatedInternally = true, repeat = true, originalEditable = true, parameters = third.Parameters };
    }
    private async Task ClickAndWait()
    {
        SubmitClicked(this, new());
        if (drawingClick is null) throw new InvalidOperationException("Click did not start generation");
        await drawingClick;
        Refresh();
        if (drawing!.Snapshot.Phase != DrawingPhase.Completed) throw new InvalidOperationException(StatusLabel.Text);
    }
}
