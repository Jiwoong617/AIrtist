using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MenuWindow
{
    internal async Task<object> SmokeAsync(string output)
    {
        Show();
        Require(MainMenu.Visibility == Visibility.Visible && SettingsPanel.Visibility == Visibility.Collapsed, "Startup is not the main menu");
        Capture(MenuPage, Path.Combine(output, "main-menu.png"));
        SettingsClicked(this, new());
        Resolution.SelectedIndex = 1; Bgm.Value = 34; Sfx.Value = 81; ApplyClicked(this, new());
        Require(GameSettings.Load(settingsPath) == new GameSettings(1280, 900, 34, 81), "Settings did not persist");
        Require(Width == Math.Min(1280, SystemParameters.WorkArea.Width), "Resolution not applied");
        Capture(MenuPage, Path.Combine(output, "settings.png"));
        BackClicked(this, new());
        _ = Dispatcher.BeginInvoke(new Action(() => SingleClicked(this, new())));
        await OnlineSmoke.Until(() => child is MainWindow);
        var drawingWindow = (MainWindow)child!;
        object drawingReport;
        try
        {
            Require(drawingWindow.Width == Width && drawingWindow.Height == Height && !IsVisible, "Drawing screen did not inherit settings");
            drawingReport = await drawingWindow.OneClickSmokeAsync(output);
        }
        finally { drawingWindow.Close(); }
        await OnlineSmoke.Until(() => child is null && IsVisible);
        _ = Dispatcher.BeginInvoke(new Action(() => MultiClicked(this, new())));
        await OnlineSmoke.Until(() => child is OnlineRoomWindow);
        var room = (OnlineRoomWindow)child!;
        Require(room.Width == Width && !IsVisible, "Multiplayer screen navigation failed");
        room.SmokeRender(Path.Combine(output, "multiplayer-room.png")); room.Close();
        await OnlineSmoke.Until(() => child is null && IsVisible);
        SettingsClicked(this, new());
        Require(Bgm.Value == 34 && Sfx.Value == 81 && Resolution.SelectedIndex == 1, "Settings were lost after navigation");
        BackClicked(this, new());
        bool exited = false; Closed += (_, _) => exited = true;
        ExitClicked(this, new()); await OnlineSmoke.Until(() => exited);
        return new { status = "passed", mainMenu = true, navigation = true, resolution = true, savedVolumes = true, soundPlayback = false, exit = true, drawingReport };
    }
    internal static void Capture(FrameworkElement view, string destination)
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
