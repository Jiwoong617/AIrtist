using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MenuWindow
{
    internal async Task SmokeLicensesAsync(string capture, bool allDocuments = false, int holdMs = 150)
    {
        var screen = child; var game = (child as OnlineRoomWindow)?.Game;
        int musicChanges = audio.Mixer.MusicChanges;
        ShellPage.UpdateLayout();
        var anchor = Settings.TranslatePoint(new Point(Settings.ActualWidth, Settings.ActualHeight / 2), ShellPage);
        Licenses.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(LicenseOverlay.Visibility == Visibility.Visible && SettingsOverlay.Visibility == Visibility.Collapsed &&
            !ScreenHost.IsEnabled && !MenuPage.IsEnabled, "License overlay did not isolate input");
        Require(LicenseText.IsReadOnly && LicenseText.Text.Contains("Attachment A") &&
            LicenseText.Text.Contains("CC BY 4.0"), "Model terms missing from default document");
        Capture(ShellPage, capture);
        if (allDocuments)
        {
            for (int i = 0; i < LicenseDocument.All.Count; i++)
            {
                LicenseChoice.SelectedIndex = i;
                string expected = LicenseDocument.All[i].Read(assets);
                Require(LicenseText.Text == expected && expected.Length > 100, "License text incomplete");
                LicenseText.SelectAll();
                Require(LicenseText.SelectedText == expected, "Document text is not selectable");
            }
            LicenseChoice.SelectedIndex = 8;
            LicenseText.UpdateLayout(); LicenseText.ScrollToEnd();
            await Task.Delay(100);
            Require(LicenseText.VerticalOffset > 0 && LicenseText.Text.Contains("Section 8"), "Long license not scrollable to end");
            LicenseChoice.SelectedIndex = 0;
            try
            {
                LicenseDocument.All[0].Read(new RuntimeAssets(Path.Combine(Path.GetDirectoryName(capture)!, "absent-models")));
                throw new InvalidOperationException("Missing license was not detected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        await Task.Delay(holdMs);
        Require(child == screen && (child as OnlineRoomWindow)?.Game == game && audio.Mixer.MusicChanges == musicChanges,
            "License overlay replaced the game or restarted music");
        Require(Application.Current.Windows.Count == 1, "License overlay opened another window");
        LicenseClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(LicenseOverlay.Visibility == Visibility.Collapsed && ScreenHost.IsEnabled && MenuPage.IsEnabled,
            "Closing license did not restore the screen");
        if (allDocuments)
        {
            Settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Bgm.Value = 0;
            Licenses.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(SettingsOverlay.Visibility == Visibility.Collapsed && LicenseOverlay.Visibility == Visibility.Visible,
                "Settings to license switch failed");
            Settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(LicenseOverlay.Visibility == Visibility.Collapsed && SettingsOverlay.Visibility == Visibility.Visible &&
                Bgm.Value == settings.Bgm, "License to settings switch retained an unsaved volume");
            BackClicked(this, new());
            Licenses.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            LicenseText.RaiseEvent(escape);
            Require(escape.Handled && LicenseOverlay.Visibility == Visibility.Collapsed, "Escape did not close license");
        }
        ShellPage.UpdateLayout();
        var after = Settings.TranslatePoint(new Point(Settings.ActualWidth, Settings.ActualHeight / 2), ShellPage);
        Require(Math.Abs(after.X - anchor.X) < 1 && Math.Abs(after.Y - anchor.Y) < 1,
            $"Settings anchor moved after license: {anchor} -> {after}");
    }
}
