using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MainWindow : Window
{
    private readonly RuntimeAssets assets;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource lifetime = new();
    private DrawingController? drawing;
    private IDrawingAi? sharedAi;
    private long shownVersion = -1, shownDraftId = -1;
    private DrawingPhase? shownPhase;
    private DrawingResult? shownResult;
    private bool closed;
    private OnlineSessionWorker? onlineWorker;
    private bool closingOnline, onlineStopped;
    public int SmokeDispatcherTicks { get; private set; }

    public MainWindow(RuntimeAssets assets)
    {
        InitializeComponent();
        this.assets = assets;
        timer.Tick += (_, _) => { SmokeDispatcherTicks++; Refresh(); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); drawing?.Dispose(); };
        Closing += async (_, e) =>
        {
            if (onlineWorker is null || onlineStopped) return;
            e.Cancel = true; if (closingOnline) return;
            closingOnline = true; IsEnabled = false;
            await onlineWorker.StopAsync(); onlineStopped = true;
            // StopAsync may already be complete; never reenter Close from the Closing event.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() => assets.Verify(lifetime.Token));
            if (closed) return;
            sharedAi = new SerializedDrawingAi(new LocalDrawingAi(assets));
            drawing = new(sharedAi);
            timer.Start();
            Refresh();
            Description.Focus();
        }
        catch (Exception)
        {
            StatusLabel.Text = "필수 모델 파일이 없거나 손상되었습니다. 전체 앱 패키지를 다시 풀어 주세요.";
            Activity.Visibility = Visibility.Hidden;
        }
    }

    private async void SubmitClicked(object sender, RoutedEventArgs e)
    {
        if (drawing?.Snapshot.Phase == DrawingPhase.Editing) await PrepareDescriptionAsync();
        else await GenerateAsync(Random.Shared.Next());
    }

    private async Task PrepareDescriptionAsync()
    {
        if (drawing is null) return;
        try
        {
            Task work = drawing.PrepareTranslationAsync(shownVersion, Description.Text);
            Refresh();
            await work;
            if (!closed)
            {
                Refresh();
                if (drawing.Snapshot.Phase == DrawingPhase.Reviewing) ReviewedTranslation.Focus();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (!closed) { Refresh(); StatusLabel.Text = ex.Message; }
        }
    }

    private async Task GenerateAsync(int seed)
    {
        if (drawing is null) return;
        try
        {
            string translation = drawing.Snapshot.Phase == DrawingPhase.Completed
                ? drawing.Snapshot.Draft!.Translation : ReviewedTranslation.Text;
            Task work = drawing.GenerateAsync(shownVersion, shownDraftId, translation, seed);
            Refresh();
            await work;
            if (!closed) Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (!closed) { Refresh(); StatusLabel.Text = ex.Message; }
        }
    }

    private void Refresh()
    {
        if (drawing is null || closed) return;
        var state = drawing.Snapshot;
        bool changed = shownVersion != state.Version;
        bool enteringReview = shownPhase != DrawingPhase.Reviewing && state.Phase == DrawingPhase.Reviewing;
        shownVersion = state.Version;
        shownPhase = state.Phase;
        bool editing = state.Phase == DrawingPhase.Editing;
        bool reviewing = state.Phase == DrawingPhase.Reviewing;
        bool completed = state.Phase == DrawingPhase.Completed;
        Description.IsEnabled = !state.Busy;
        Description.IsReadOnly = !editing;
        Submit.IsEnabled = (editing || reviewing || completed) && !state.Busy;
        Submit.Content = editing ? "번역 확인" : completed ? "다시 그리기" : "확정하고 그리기";
        EditDescription.IsEnabled = completed && !state.Busy;
        ReviewPanel.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;
        GeneratedImage.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Visibility = reviewing || GeneratedImage.Source is not null ? Visibility.Collapsed : Visibility.Visible;
        ReviewedTranslation.IsEnabled = ReviseDescription.IsEnabled = reviewing && !state.Busy;
        if (reviewing && state.Draft is { } draft && (shownDraftId != draft.Id || enteringReview))
        {
            shownDraftId = draft.Id;
            ReviewedTranslation.Text = draft.Translation;
        }
        Cancel.IsEnabled = state.Busy;
        VotingPreview.IsEnabled = !state.Busy;
        Online.IsEnabled = !state.Busy;
        Cancel.Content = state.Phase == DrawingPhase.Translating ? "번역 취소" : "취소";
        Activity.Visibility = state.Busy ? Visibility.Visible : Visibility.Hidden;
        if (changed || state.Busy) StatusLabel.Text = state.Message;
        if (state.Result is { } result && !ReferenceEquals(shownResult, result))
        {
            shownResult = result;
            GeneratedImage.Source = ImageCodec.Load(result.Png);
            Placeholder.Visibility = Visibility.Collapsed;
            TranslationLabel.Text = "그림에 사용한 묘사: " + result.Translation;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) { drawing?.Cancel(shownVersion); Refresh(); }
    private void VotingClicked(object sender, RoutedEventArgs e)
    {
        if (drawing?.Snapshot.Busy != false) return;
        try { new RoundPreviewWindow(assets, sharedAi!) { Owner = this }.ShowDialog(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { StatusLabel.Text = "경기 미리보기를 열지 못했습니다. 단어 데이터와 앱 파일을 확인해 주세요."; }
    }
    private void OnlineClicked(object sender, RoutedEventArgs e)
    {
        if (drawing?.Snapshot.Busy != false) return;
        try
        {
            string words = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
            onlineWorker ??= new(MultiplayerCompatibility.FromVerifiedAssets(assets, words), WordCatalog.Load(words), sharedAi!);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "--eos-config");
            string path = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.GetFullPath("config/eos.local.json");
            new OnlineRoomWindow(assets, sharedAi!, onlineWorker, path) { Owner = this }.ShowDialog();
        }
        catch (Exception) { StatusLabel.Text = "온라인 방 화면을 열지 못했습니다. 앱·단어 파일과 설정 경로를 확인해 주세요."; }
    }
    private void EditClicked(object sender, RoutedEventArgs e)
    {
        if (drawing is null) return;
        try { drawing.Review(shownVersion); Refresh(); ReviewedTranslation.Focus(); }
        catch (InvalidOperationException ex) { Refresh(); StatusLabel.Text = ex.Message; }
    }
    private void DescriptionChanged(object sender, TextChangedEventArgs e) { if (LengthLabel is not null) LengthLabel.Text = $"{Description.Text.Length} / 160 · Enter는 줄바꿈"; }
    private void TranslationChanged(object sender, TextChangedEventArgs e) { if (TranslationLengthLabel is not null) TranslationLengthLabel.Text = $"{ReviewedTranslation.Text.Length} / 320 · Enter는 줄바꿈"; }
    private void ReviseClicked(object sender, RoutedEventArgs e)
    {
        if (drawing is null) return;
        try { drawing.Revise(shownVersion, shownDraftId); Refresh(); Description.Focus(); }
        catch (InvalidOperationException ex) { Refresh(); StatusLabel.Text = ex.Message; }
    }

    public async Task<DrawingResult> SmokeAsync(string? output = null)
    {
        if (drawing is null) throw new InvalidOperationException("Drawing initialization failed: " + StatusLabel.Text);
        Description.Text = "빨간 사과 한 개";
        SmokeDispatcherTicks = 0;
        await PrepareDescriptionAsync();
        if (drawing.Snapshot.Phase != DrawingPhase.Reviewing || !ReviewedTranslation.IsEnabled || !Submit.IsEnabled || GeneratedImage.Source is not null)
            throw new InvalidOperationException("Free description did not reach review: " + StatusLabel.Text);
        if (output is not null) RenderCheck(Path.Combine(output, "translation-review-auto.png"));
        ReviewedTranslation.Text = "초록 사과";
        await GenerateAsync(42);
        if (drawing.Snapshot.Phase != DrawingPhase.Reviewing || shownResult is not null || !StatusLabel.Text.Contains("영어"))
            throw new InvalidOperationException("Non-English confirmation was not rejected");
        ReviseClicked(this, new());
        if (drawing.Snapshot.Phase != DrawingPhase.Editing || Description.Text != "빨간 사과 한 개" || !Description.IsEnabled)
            throw new InvalidOperationException("Revision did not preserve the original description");
        Description.Text = "흰 토끼 한 마리";
        await PrepareDescriptionAsync();
        ReviewedTranslation.Text = "a white rabbit with long ears";
        if (output is not null)
        {
            RenderCheck(Path.Combine(output, "translation-review-corrected.png"));
            RenderCheck(Path.Combine(output, "translation-review-150.png"), dpi: 144);
            RenderCheck(Path.Combine(output, "translation-review-minimum.png"), width: 876, height: 636);
            ReviewPanel.ScrollToEnd();
            RenderCheck(Path.Combine(output, "translation-review-minimum-bottom.png"), width: 876, height: 636);
            ReviewPanel.ScrollToHome();
        }
        await GenerateAsync(42);
        if (drawing.Snapshot.Phase != DrawingPhase.Completed || shownResult is null || GeneratedImage.Source is null || !Submit.IsEnabled || !EditDescription.IsEnabled || Cancel.IsEnabled)
            throw new InvalidOperationException("Drawing did not complete with repeat enabled: " + StatusLabel.Text);
        if (SmokeDispatcherTicks < 10) throw new InvalidOperationException("UI dispatcher did not remain responsive during inference");
        if (shownResult.Translation != "a white rabbit with long ears")
            throw new InvalidOperationException("The corrected English description was not used for generation");
        return shownResult;
    }

    public async Task<object> DrawingSmokeAsync(string output)
    {
        var first = await SmokeAsync(output);
        File.WriteAllBytes(Path.Combine(output, "generated.png"), first.Png);
        RenderCheck(Path.Combine(output, "drawing-result.png"));
        await GenerateAsync(43);
        var repeated = drawing!.Snapshot.Result;
        if (drawing.Snapshot.Phase != DrawingPhase.Completed || repeated is null || ReferenceEquals(first, repeated)
            || repeated.Translation != first.Translation || repeated.Seed != 43 || !Submit.IsEnabled)
            throw new InvalidOperationException("Repeated generation did not produce a new result");
        File.WriteAllBytes(Path.Combine(output, "generated-repeat.png"), repeated.Png);
        EditClicked(this, new());
        if (ReviewedTranslation.Text != repeated.Translation) throw new InvalidOperationException("Editing lost the confirmed English description");
        ReviseClicked(this, new());
        Description.Text = "a red apple on a blue background";
        await PrepareDescriptionAsync();
        await GenerateAsync(44);
        var final = drawing.Snapshot.Result;
        if (drawing.Snapshot.Phase != DrawingPhase.Completed || final is null || ReferenceEquals(final, repeated)
            || final.Description != Description.Text || !Submit.IsEnabled || !EditDescription.IsEnabled)
            throw new InvalidOperationException("Revised drawing did not complete: " + StatusLabel.Text);
        File.WriteAllBytes(Path.Combine(output, "generated-revised.png"), final.Png);
        RenderCheck(Path.Combine(output, "drawing-revised.png"));
        RenderCheck(Path.Combine(output, "drawing-revised-150.png"), dpi: 144);
        RenderCheck(Path.Combine(output, "drawing-revised-minimum.png"), width: 876, height: 636);
        return new { status = "passed", uiDispatcherTicks = SmokeDispatcherTicks, generatedImages = 3,
            directObjectNamesAllowed = true, translationReviewed = true, revisionPreservedSource = true,
            repeatEnabled = Submit.IsEnabled, correctedTranslation = first.Translation,
            results = new[] { first, repeated, final }.Select(r => new { r.Description, r.Translation, r.Seed, r.TranslationSeconds, r.GenerationSeconds }) };
    }

    public void RenderCheck(string destination, byte[]? image = null, double dpi = 96, double width = 1056, double height = 696)
    {
        if (drawing is null) throw new InvalidOperationException("Drawing initialization failed: " + StatusLabel.Text);
        if (Submit.IsDefault || ReviseDescription.IsDefault || !Description.AcceptsReturn || !ReviewedTranslation.AcceptsReturn)
            throw new InvalidOperationException("Enter must never confirm either description field");
        if (image is not null) { GeneratedImage.Source = ImageCodec.Load(image); Placeholder.Visibility = Visibility.Collapsed; }
        var margin = Page.Margin;
        Page.Margin = new Thickness(0); Page.Width = width; Page.Height = height;
        Page.Measure(new Size(width, height)); Page.Arrange(new Rect(0, 0, width, height)); Page.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(Page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
        Page.Margin = margin; Page.Width = Page.Height = double.NaN;
    }
}
