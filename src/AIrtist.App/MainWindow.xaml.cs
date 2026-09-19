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
    private MatchController? match;
    private Word[] words = [];
    private long shownVersion = -1;
    private MatchPhase? shownPhase;
    private WorkflowResult? shownResult;
    private bool closed, editingSettings;
    public int SmokeDispatcherTicks { get; private set; }

    public MainWindow(RuntimeAssets assets)
    {
        InitializeComponent();
        this.assets = assets;
        timer.Tick += (_, _) => { SmokeDispatcherTicks++; Refresh(); };
        Closed += (_, _) => { closed = true; timer.Stop(); lifetime.Cancel(); match?.Dispose(); };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() => assets.Verify(lifetime.Token));
            if (closed) return;
            words = assets.Read<ScoringData>("scoring/vectors.json").Words;
            match = new(new LocalAi(assets), words);
            StartMatch.IsEnabled = true;
            SetupStatus.Text = "모든 AI 처리는 이 PC에서 실행됩니다";
            timer.Start();
            Refresh();
        }
        catch (Exception)
        {
            SetupStatus.Text = StatusLabel.Text = "필수 모델 파일이 없거나 손상되었습니다. 전체 앱 패키지를 다시 풀어 주세요.";
            Activity.Visibility = Visibility.Hidden;
        }
    }

    private static int Selected(ComboBox box) => int.Parse(((ComboBoxItem)box.SelectedItem).Content.ToString()!);
    private void StartClicked(object sender, RoutedEventArgs e)
    {
        try { BeginMatch(new(Selected(RoundsSetting), Selected(WritingSetting), Selected(AiSetting))); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { SetupStatus.Text = ex.Message; }
    }

    private void BeginMatch(MatchSettings settings, int? seed = null)
    {
        if (match is null) return;
        match.Start(settings, seed);
        editingSettings = false;
        Refresh();
        Description.Focus();
    }

    private async void SubmitClicked(object sender, RoutedEventArgs e) => await SubmitDescriptionAsync(Random.Shared.Next());
    private async Task SubmitDescriptionAsync(int seed)
    {
        if (match is null) return;
        try
        {
            Task work = match.SubmitAsync(shownVersion, Description.Text, seed);
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
        if (match is null || closed) return;
        var state = match.Snapshot;
        bool changed = shownVersion != state.Version || shownPhase != state.Phase;
        if (shownVersion != state.Version)
        {
            Description.Text = "";
            GeneratedImage.Source = null;
            Placeholder.Visibility = Visibility.Visible;
            ScoreLabel.Text = "—";
            shownResult = null;
            TranslationLabel.Text = "묘사는 이 PC에서 번역·생성·평가됩니다";
        }
        shownVersion = state.Version;
        shownPhase = state.Phase;
        bool writing = state.Phase == MatchPhase.Writing;
        bool processing = state.Phase == MatchPhase.Processing;
        MatchPanel.Visibility = state.Phase is MatchPhase.Setup or MatchPhase.Finished ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = state.Phase == MatchPhase.Setup || editingSettings ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = state.Phase == MatchPhase.Finished && !editingSettings ? Visibility.Visible : Visibility.Collapsed;
        Description.IsEnabled = Submit.IsEnabled = writing && !state.Busy;
        Cancel.IsEnabled = processing && state.Busy;
        Next.IsEnabled = !state.Busy && (writing || state.Phase == MatchPhase.RoundResult);
        Next.Content = writing ? "건너뛰기" : state.Number == state.Settings.Rounds ? "최종 결과 →" : "다음 라운드 →";
        Activity.Visibility = processing ? Visibility.Visible : Visibility.Hidden;
        RoundLabel.Text = state.Phase is MatchPhase.Setup or MatchPhase.Finished ? "●  오프라인 경기" : $"●  라운드 {state.Number} / {state.Settings.Rounds} · 누적 {state.TotalScore:0.0}점";
        TimerLabel.Text = writing || processing ? $"{(writing ? "작성" : "AI 처리")} 남은 시간 {Math.Ceiling(state.RemainingSeconds):0}초" : "";
        if (changed)
        {
            StatusLabel.Text = state.Message;
            if (state.Word is { } word)
            {
                WordLabel.Text = word.Korean;
                MeaningLabel.Text = word.Meaning;
                AliasLabel.Text = "사용할 수 없는 말: " + string.Join(", ", word.Aliases);
            }
        }
        else if (processing) StatusLabel.Text = state.Message;
        if (state.Result is { } result && !ReferenceEquals(shownResult, result))
        {
            shownResult = result;
            GeneratedImage.Source = ImageCodec.Load(result.Png);
            Placeholder.Visibility = Visibility.Collapsed;
            ScoreLabel.Text = result.Score.ToString("0.0");
            TranslationLabel.Text = result.Translation;
            ScoreLabel.ToolTip = $"코사인 유사도 {result.Cosine:0.0000} · 검증 중인 점수 규칙";
        }
        if (changed && state.Phase == MatchPhase.RoundResult && state.Result is null)
        {
            ScoreLabel.Text = "0.0";
            TranslationLabel.Text = "그림을 완료하지 못한 라운드는 0점으로 기록합니다";
        }
        if (changed && state.Phase == MatchPhase.Finished)
        {
            TotalLabel.Text = $"총 {state.TotalScore:0.0}점 / {state.Settings.Rounds * 100}점";
            AverageLabel.Text = $"평균 {state.AverageScore:0.0}점 · 완료한 그림 {state.Records.Count(r => r.State == RoundState.Completed)} / {state.Settings.Rounds}장 · 평가점은 정답 확률이 아닙니다";
            RoundHistory.ItemsSource = state.Records.Select(r => $"{r.Number} 라운드   {r.Word}   ·   {r.Score:0.0}점   ·   {r.Message}").ToArray();
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) { match?.Cancel(shownVersion); Refresh(); }
    private void NextClicked(object sender, RoutedEventArgs e)
    {
        if (match is null) return;
        try
        {
            if (match.Snapshot.Phase == MatchPhase.Writing) match.Skip(shownVersion);
            else match.Advance(shownVersion);
            Refresh();
            if (match.Snapshot.Phase == MatchPhase.Writing) Description.Focus();
        }
        catch (InvalidOperationException ex) { Refresh(); StatusLabel.Text = ex.Message; }
    }
    private void RestartClicked(object sender, RoutedEventArgs e) { editingSettings = true; Refresh(); }
    private void DescriptionChanged(object sender, TextChangedEventArgs e) { if (LengthLabel is not null) LengthLabel.Text = $"{Description.Text.Length} / 160 · Enter는 줄바꿈"; }

    public async Task<WorkflowResult> SmokeAsync()
    {
        int seed = Enumerable.Range(0, 10000).First(value => { var shuffled = words.ToArray(); new Random(value).Shuffle(shuffled); return shuffled[0].Id == "rabbit"; });
        BeginMatch(new(3, 60, 180), seed);
        Description.Text = "귀가 길고 하얀 털을 가진 작은 동물";
        SmokeDispatcherTicks = 0;
        await SubmitDescriptionAsync(42);
        if (shownResult is null || GeneratedImage.Source is null || Submit.IsEnabled || !Next.IsEnabled || Cancel.IsEnabled)
            throw new InvalidOperationException("UI workflow did not complete: " + StatusLabel.Text);
        if (SmokeDispatcherTicks < 10) throw new InvalidOperationException("UI dispatcher did not remain responsive during inference");
        return shownResult;
    }

    public async Task<object> MatchSmokeAsync(string output)
    {
        var first = await SmokeAsync();
        File.WriteAllBytes(Path.Combine(output, "generated.png"), first.Png);
        RenderCheck(Path.Combine(output, "round-result.png"));
        NextClicked(this, new());
        if (match!.Snapshot.Phase != MatchPhase.Writing || shownResult is not null || GeneratedImage.Source is not null)
            throw new InvalidOperationException("Next round retained the previous image or result");
        NextClicked(this, new()); // Round 2 is explicitly skipped.
        NextClicked(this, new());
        Description.Text = match.Snapshot.Word!.Id switch {
            "apple" => "a round red crunchy fruit", "banana" => "a long curved yellow fruit", "rabbit" => "a small animal with long ears and white fur",
            "umbrella" => "a portable folding canopy to protect from rain", "cup" => "a small drinking container with a handle", "tiger" => "a large orange feline with black stripes",
            "dog" => "a small furry pet that barks", "car" => "a road vehicle with four wheels", _ => throw new InvalidOperationException()
        };
        await SubmitDescriptionAsync(43);
        if (match.Snapshot.Records.Length != 3 || match.Snapshot.Records[2].State != RoundState.Completed) throw new InvalidOperationException("Final round failed: " + StatusLabel.Text);
        NextClicked(this, new());
        var final = match.Snapshot;
        if (final.Phase != MatchPhase.Finished || final.Records.Length != 3 || SummaryPanel.Visibility != Visibility.Visible || RoundHistory.Items.Count != 3)
            throw new InvalidOperationException("Final result UI did not complete");
        RenderCheck(Path.Combine(output, "match-result.png"));
        RenderCheck(Path.Combine(output, "match-result-150.png"), dpi: 144);
        RestartClicked(this, new());
        BeginMatch(new(3, 60, 180), 9);
        if (match.Snapshot.Records.Length != 0 || match.Snapshot.Result is not null) throw new InvalidOperationException("New match retained prior scores");
        return new { status = "passed", uiDispatcherTicks = SmokeDispatcherTicks, final.Records, final.TotalScore, final.AverageScore, restarted = true };
    }

    public void RenderCheck(string destination, byte[]? image = null, double dpi = 96)
    {
        if (Submit.IsDefault || !Description.AcceptsReturn) throw new InvalidOperationException("Enter must never submit IME composition");
        if (image is not null) { GeneratedImage.Source = ImageCodec.Load(image); Placeholder.Visibility = Visibility.Collapsed; }
        var margin = Page.Margin;
        Page.Margin = new Thickness(0); Page.Width = 1056; Page.Height = 696;
        Page.Measure(new Size(1056, 696)); Page.Arrange(new Rect(0, 0, 1056, 696)); Page.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(1056 * dpi / 96), (int)(696 * dpi / 96), dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(Page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
        Page.Margin = margin; Page.Width = Page.Height = double.NaN;
    }
}
