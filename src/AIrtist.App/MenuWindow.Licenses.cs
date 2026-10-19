using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIrtist.Core;

namespace AIrtist.App;

internal sealed record LicenseDocument(string Title, string FileName)
{
    internal static readonly IReadOnlyList<LicenseDocument> All = Array.AsReadOnly<LicenseDocument>([
        new("모델 이용 조건", "MODEL_TERMS.ko.txt"),
        new("모델 출처", "MODEL_SOURCES.txt"),
        new("파일별 변경 내역", "CHANGES.txt"),
        new("Tiny-SD · Open RAIL-M", "Tiny-SD_LICENSE.txt"),
        new("TAESD · MIT", "TAESD_LICENSE.txt"),
        new("번역 모델 · 라이선스 안내", "OPUS-MT_NOTICE.txt"),
        new("번역 모델 · 원본 모델 카드", "OPUS-MT_MODEL_CARD.md"),
        new("Apache 2.0", "Apache-2.0.txt"),
        new("CC BY 4.0", "CC-BY-4.0.txt"),
        new("CLIP · MIT", "CLIP_LICENSE.txt")
    ]);
    public override string ToString() => Title;

    internal string Read(RuntimeAssets assets)
    {
        // Only the fixed shipped documents can be selected, including before model initialization.
        if (!All.Contains(this)) throw new InvalidDataException("지원하지 않는 라이선스 문서입니다.");
        string path = assets.File("licenses/" + FileName);
        long length = new FileInfo(path).Length;
        if (length is < 1 or > 1_000_000) throw new InvalidDataException("라이선스 문서 크기가 올바르지 않습니다.");
        return File.ReadAllText(path);
    }
}

public partial class MenuWindow
{
    private void LicensesClicked(object sender, RoutedEventArgs e)
    {
        if (LicenseOverlay.Visibility == Visibility.Visible) { CloseLicenseClicked(sender, e); return; }
        if (SettingsOverlay.Visibility == Visibility.Visible) BackClicked(sender, e);
        LicenseOverlay.Visibility = Visibility.Visible;
        ScreenHost.IsEnabled = MenuPage.IsEnabled = false;
        if (LicenseChoice.SelectedIndex == 0) LoadLicense(); else LicenseChoice.SelectedIndex = 0;
        LicenseChoice.Focus();
    }

    private void LicenseSelectionChanged(object sender, SelectionChangedEventArgs e) => LoadLicense();
    private void LoadLicense()
    {
        if (LicenseChoice.SelectedItem is not LicenseDocument document) return;
        try { LicenseText.Text = document.Read(assets); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LicenseText.Text = "라이선스 문서를 읽을 수 없습니다. 앱 폴더 전체가 있는지 확인해 주세요.";
            audio.Cue(SoundCue.ErrorNotice);
        }
        LicenseText.CaretIndex = 0; LicenseText.ScrollToHome();
    }
    private void CloseLicenseClicked(object sender, RoutedEventArgs e)
    {
        LicenseOverlay.Visibility = Visibility.Collapsed;
        ScreenHost.IsEnabled = MenuPage.IsEnabled = true;
        Licenses.Focus();
    }
    private void LicenseKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || LicenseChoice.IsDropDownOpen) return;
        CloseLicenseClicked(sender, e); e.Handled = true;
    }
}
