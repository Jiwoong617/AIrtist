using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIrtist.Core;

namespace AIrtist.App;

public partial class RoundPreviewWindow : Window
{
    private readonly IDrawingAi ai;
    private readonly MultiplayerCompatibility compatibility;
    private readonly Word[] words;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<int, (string Source, string English, long DraftId)> edits = new();
    private readonly Dictionary<int, string[]> parameterEdits = new();
    private MultiplayerMatchRuntime[] rounds = [];
    private VotingPlayer[] players = [];
    private CancellationTokenSource? loopCancel;
    private Task? loop;
    private volatile string? loopFailure;
    private int shownRole = -1, dispatcherTicks;
    private long shownDraft = -1;
    private Guid shownRound;
    private string galleryKey = "";
    private byte[]? shownPng;
    private bool closed;
    private string? commandError;
    private record Card(string Id, BitmapSource Image, string Label, string Votes, string Action, bool CanVote, string Outline);

    // The caller verifies assets and shares one serialized AI service with drawing.
    public RoundPreviewWindow(RuntimeAssets assets, IDrawingAi sharedAi)
    {
        InitializeComponent(); ai = sharedAi;
        RoomParameters.RequireFixedSeed();
        string path = Path.Combine(AppContext.BaseDirectory, "data", "words.json");
        words = WordCatalog.Load(path); compatibility = MultiplayerCompatibility.FromVerifiedAssets(assets, path);
        Count.ItemsSource = new[] { 2, 3, 4 }; Count.SelectedIndex = 1;
        RoundCount.ItemsSource = Enumerable.Range(1, 10).ToArray(); RoundCount.SelectedItem = 3;
        WordChoice.ItemsSource = words; WordChoice.SelectedIndex = 0;
        timer.Tick += (_, _) => { dispatcherTicks++; Refresh(); }; timer.Start();
        Closed += async (_, _) => { closed = true; timer.Stop(); await StopAsync(); };
    }
    private MultiplayerMatchRuntime? Current => Viewer.SelectedIndex >= 0 && Viewer.SelectedIndex < rounds.Length ? rounds[Viewer.SelectedIndex] : null;
    private RoundRuntimeSnapshot? CurrentRound => (onlineView?.Match ?? Current?.Snapshot())?.Round;
    private void Act(Action command)
    {
        try { command(); commandError = null; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { commandError = ex.Message; }
        Refresh();
    }
    private void StartClicked(object sender, RoutedEventArgs e) => Act(StartRound);
    private void StartRound()
    {
        if (online is not null) { OnlineLobbyAction(OnlineAction.Start); return; }
        if (rounds.Length != 0) return;
        var session = CurrentLobby ?? throw new InvalidOperationException("대기방을 먼저 만들어 주세요.");
        var state = session.Snapshot().Lobby ?? throw new InvalidOperationException("대기방 설정을 받고 있습니다.");
        if (ReadLobbySettings() != state.Settings) throw new InvalidOperationException("변경한 설정을 먼저 적용해 주세요.");
        session.Start(state.Revision);
    }
    private void StartLobbyLoop()
    {
        edits.Clear(); shownRole = -1; shownDraft = -1; shownRound = Guid.Empty; galleryKey = ""; loopFailure = null;
        loopCancel = new(); var token = loopCancel.Token; var activeSessions = sessions;
        loop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var session in activeSessions) session.Tick();
                    await Task.Delay(20, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                loopFailure = $"경기 처리가 중단되었습니다 ({ex.GetType().Name}). 다시 설정해 주세요.";
                foreach (var session in activeSessions) session.Abort();
            }
        });
    }
    private async Task StopAsync()
    {
        if (online is not null) return; // Room owner asks the worker to leave; never dispose EOS on UI.
        loopCancel?.Cancel(); if (loop is not null) await loop;
        foreach (var session in sessions) session.Dispose(); sessions = [];
        rounds = []; loopCancel?.Dispose(); loopCancel = null; loop = null;
    }
    private async void ResetClicked(object sender, RoutedEventArgs e)
    { if (online is not null) Close(); else await ResetPreviewAsync(); }
    private async Task ResetPreviewAsync()
    {
        Reset.IsEnabled = false; await StopAsync(); if (closed) return;
        ClearLobby(); players = []; Viewer.ItemsSource = null; edits.Clear(); parameterEdits.Clear(); Parameters.Set(new()); shownRole = -1; shownDraft = -1;
        galleryKey = ""; shownPng = null; Cards.ItemsSource = null; OwnImage.Source = null; OwnImage.Visibility = Visibility.Collapsed;
        Description.Text = English.Text = ProgressLabel.Text = ResultLabel.Text = Meaning.Text = ClockLabel.Text = "";
        Prompt.Text = "설정을 정하고 대기방을 만드세요"; GalleryTitle.Text = "생성한 그림";
        HistoryPanel.Visibility = Visibility.Collapsed; HistoryLabel.Text = MatchLabel.Text = ""; shownRound = Guid.Empty;
        GalleryHint.Text = "다른 역할로 바꾸어 모든 참가자의 그림을 만들어 주세요.";
        commandError = loopFailure = null; Setup.IsEnabled = Start.IsEnabled = true; Refresh();
    }
    private void ViewerChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) Refresh(); }
    private void TranslateClicked(object sender, RoutedEventArgs e) => Act(() => { if (online is not null) SendOnline(OnlineAction.Translate, Description.Text); else Current?.PrepareTranslation(shownRound, Description.Text); });
    private void DrawClicked(object sender, RoutedEventArgs e) => Act(() =>
    {
        var parameters = CurrentRound?.Submission.FixedParameters ?? Parameters.Read();
        if (online is not null) SendOnline(OnlineAction.Draw, Description.Text, parameters: parameters);
        else Current?.Draw(shownRound, Description.Text, parameters);
    });
    private void ConfirmClicked(object sender, RoutedEventArgs e) => Act(() =>
    {
        var parameters = CurrentRound?.Submission.FixedParameters ?? Parameters.Read();
        if (online is not null) SendOnline(OnlineAction.Confirm, English.Text, parameters: parameters);
        else Current?.Confirm(shownRound, shownDraft, English.Text, parameters);
    });
    private void ReviseClicked(object sender, RoutedEventArgs e) => Act(() => { if (online is not null) SendOnline(OnlineAction.Revise); else Current?.Revise(shownRound, shownDraft); });
    private void CancelClicked(object sender, RoutedEventArgs e) => Act(() => { if (online is not null) SendOnline(OnlineAction.Cancel); else Current?.CancelAi(shownRound); });
    private void VoteClicked(object sender, RoutedEventArgs e) => Act(() => { string id = (string)((Button)sender).Tag; if (online is not null) SendOnline(OnlineAction.Vote, id); else Current?.Vote(shownRound, id); });
    private void CloseVoteClicked(object sender, RoutedEventArgs e) => Act(() => { if (online is not null) SendOnline(OnlineAction.CloseVoting); else Current?.CloseVoting(shownRound); });
    private void NextRoundClicked(object sender, RoutedEventArgs e) => Act(() => { if (online is not null) SendOnline(OnlineAction.NextRound); else Current?.AdvanceRound(shownRound); });
    private void Refresh()
    {
        if (closed) return;
        if (online is not null) { onlineView = online.Snapshot; RefreshOnlineLobby(); } else RefreshLobby();
        var match = onlineView?.Match ?? Current?.Snapshot(); var state = match?.Round;
        // Role switching is local-preview-only. Keep it on the same round while
        // the begin message reaches the other simulated participants.
        Viewer.IsEnabled = rounds.Length == 0 || rounds.Select(r => r.Snapshot().RoundId).Distinct().Count() == 1;
        if (state is null)
        {
            Translate.IsEnabled = Confirm.IsEnabled = Revise.IsEnabled = Cancel.IsEnabled = English.IsEnabled = Description.IsEnabled = false;
            CloseVote.Visibility = NextRound.Visibility = Visibility.Collapsed;
            StatusLabel.Text = commandError ?? onlineView?.Error ?? match?.Message ?? onlineView?.Message ?? LobbyMessage(); return;
        }
        if (shownRound != match!.RoundId)
        {
            if (shownRole >= 0) parameterEdits[shownRole] = Parameters.Capture();
            edits.Clear(); shownRole = -1; shownDraft = -1; shownRound = match.RoundId; shownPng = null; galleryKey = "";
            Description.Text = English.Text = ""; OwnImage.Source = null; Cards.ItemsSource = null; commandError = null;
        }
        MatchLabel.Text = $"{match.RoundNumber} / {match.RoundCount} 라운드" + (match.Phase == MultiplayerMatchPhase.Completed ? " · 경기 종료" : "");
        HistoryPanel.Visibility = match.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryLabel.Text = string.Join("\n", match.Results.Select(r => $"{r.RoundNumber}R · {words.Single(w => w.Id == r.WordId).Korean}  |  "
            + string.Join(" / ", players.Select(p => $"{p.Name}: {r.Result.Tallies.SingleOrDefault(t => t.OwnerId == p.Id)?.Votes ?? 0}표"))
            + (r.Result.Reason == VotingEnd.InsufficientImages ? " (투표 없음)" : "")));
        NextRound.Visibility = match.CanAdvance ? Visibility.Visible : Visibility.Collapsed;
        int role = Viewer.SelectedIndex;
        if (shownRole != role)
        {
            if (shownRole >= 0)
            {
                edits[shownRole] = (Description.Text, English.Text, shownDraft);
                parameterEdits[shownRole] = Parameters.Capture();
            }
            var saved = edits.GetValueOrDefault(role); Description.Text = saved.Source ?? ""; English.Text = saved.English ?? "";
            shownRole = role; shownDraft = saved.Source is null ? -1 : saved.DraftId;
            Parameters.Restore(parameterEdits.GetValueOrDefault(role));
            galleryKey = ""; commandError = null;
        }
        if (state.Draft is { } current && state.AiPhase == RoundAiPhase.Reviewing && shownDraft != current.Id)
        { English.Text = current.Translation; shownDraft = current.Id; }
        // A translating draft already exists before its English result arrives.
        if (state.AiPhase == RoundAiPhase.Translating) shownDraft = -1;
        bool writing = match.Phase == MultiplayerMatchPhase.Playing && state.Submission.Phase == SubmissionClientPhase.Writing && loopFailure is null
            && (onlineView is null || onlineView.Phase == OnlinePhase.Connected);
        bool reviewing = writing && !state.Busy && state.AiPhase == RoundAiPhase.Reviewing;
        bool canDraw = writing && !state.Busy && state.AiPhase is RoundAiPhase.Editing or RoundAiPhase.Reviewing;
        Parameters.IsEnabled = canDraw && state.Submission.FixedParameters is null;
        if (state.Submission.FixedParameters is { } fixedValues) Parameters.Set(fixedValues);
        ParameterPolicyLabel.Text = state.Submission.FixedParameters is null ? "자유 설정 · 제출하면 고정됩니다" : "방장 설정으로 고정된 방입니다";
        if (state.Submission.ApprovedParameters is { } approved)
            ParameterPolicyLabel.Text += $"\n사용값: CFG {approved.Guidance} / {approved.Steps} Steps\nSeed {approved.Seed}";
        Description.IsEnabled = Translate.IsEnabled = canDraw;
        English.IsEnabled = Confirm.IsEnabled = Revise.IsEnabled = reviewing;
        Cancel.IsEnabled = state.Busy && match.Phase == MultiplayerMatchPhase.Playing;
        Reset.IsEnabled = online is not null || loopFailure is not null || match.Phase == MultiplayerMatchPhase.Stopped || rounds.All(r => r.Snapshot().Round?.Busy != true);
        if (state.Submission.Prompt is { } prompt) { Prompt.Text = $"제시어 · {prompt.Korean}"; Meaning.Text = prompt.Meaning; }
        ClockLabel.Text = state.Gallery.Phase switch
        {
            RoundClientPhase.Completed => "라운드 종료",
            RoundClientPhase.Voting => $"투표 남은 시간 · {Seconds(state.Gallery.VotingRemaining)}초",
            RoundClientPhase.ReceivingGallery or RoundClientPhase.Ready => "모두 그림을 받을 때까지 기다립니다",
            RoundClientPhase.Failed or RoundClientPhase.Disconnected => "라운드 중단",
            _ => $"작성 · {Seconds(state.Submission.WritingRemaining)}초" + (state.Submission.Phase is SubmissionClientPhase.Generating or SubmissionClientPhase.Approved
                or SubmissionClientPhase.AwaitingImagePermission or SubmissionClientPhase.SendingImage ? $"   |   내 생성 · {Seconds(state.Submission.GenerationRemaining)}초" : "")
        };
        if (match.Phase == MultiplayerMatchPhase.Stopped) ClockLabel.Text = "경기 중단 · 확정 기록 보존";
        ProgressLabel.Text = string.Join("\n", state.Submission.Players.Select(p => $"{players.Single(v => v.Id == p.PlayerId).Name}  ·  {Progress(p.State)}"));
        bool gallery = state.Gallery.Phase is RoundClientPhase.Voting or RoundClientPhase.Completed;
        ParameterPanel.Visibility = PromptComposer.Visibility = gallery ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(GallerySurface, gallery ? 0 : 2);
        Grid.SetColumnSpan(GallerySurface, gallery ? 3 : 1);
        GalleryTitle.Text = gallery ? "모두의 그림 · 다른 사람에게 한 표" : "생성한 그림";
        GalleryHint.Text = gallery ? "자기 그림에는 투표할 수 없습니다. 선택은 한 번만 가능합니다." : online is not null
            ? "각자 그림을 제출하면 모두의 그림을 함께 볼 수 있습니다." : "다른 역할로 바꾸어 모든 참가자의 그림을 만들어 주세요.";
        if (!ReferenceEquals(shownPng, state.OwnPng)) { shownPng = state.OwnPng; OwnImage.Source = shownPng is null ? null : ImageCodec.Load(shownPng); }
        OwnImage.Visibility = !gallery && shownPng is not null ? Visibility.Visible : Visibility.Collapsed;
        bool canVote = state.Gallery.CanVote && match.Phase == MultiplayerMatchPhase.Playing;
        string key = $"{role}/{gallery}/{canVote}/{state.Gallery.SelectedArtworkId}/{state.Gallery.Result?.Revision}";
        if (key != galleryKey)
        {
            galleryKey = key;
            Cards.ItemsSource = !gallery ? null : state.Gallery.Artworks.Select(a =>
            {
                bool own = a.OwnerId == state.LocalId, selected = a.Id == state.Gallery.SelectedArtworkId;
                var tally = state.Gallery.Result?.Tallies.Single(t => t.ArtworkId == a.Id);
                return new Card(a.Id, a.Preview, players.Single(p => p.Id == a.OwnerId).Name + (own ? " · 내 그림" : ""),
                    tally is null ? "" : $"{tally.Votes}표", own ? "내 그림" : selected ? "투표한 그림" : "이 그림에 투표",
                    canVote && !own, FindResource(selected ? "Accent" : "CardBorder").ToString()!);
            }).ToArray();
        }
        ResultLabel.Text = state.Gallery.Result is { } result ? ResultText(result) : "";
        CloseVote.Visibility = state.IsHost && state.Gallery.Phase == RoundClientPhase.Voting && match.Phase == MultiplayerMatchPhase.Playing ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = loopFailure ?? commandError ?? onlineView?.Error ?? (onlineView?.Phase == OnlinePhase.Failed ? onlineView.Message : null)
            ?? (match.Phase == MultiplayerMatchPhase.Stopped ? match.Message : null) ?? state.Gallery.Failure ?? state.Submission.Failure
            ?? (match.Phase != MultiplayerMatchPhase.Playing || state.Gallery.Result is not null ? match.Message
                : state.Gallery.CanVote ? "그림을 보고 다른 사람의 그림 하나에 투표해 주세요."
                : state.Submission.UploadEnd == ImageSendEnd.Complete ? "그림 제출이 완료되었습니다. 다른 참가자를 기다립니다." : state.Message);
    }
    private string ResultText(VotingResult result)
    {
        if (result.Reason == VotingEnd.InsufficientImages) return "그림이 부족해 투표 없이 종료했습니다.";
        if (result.AcceptedVotes == 0) return "유효한 표 없이 종료했습니다.";
        var owners = result.Tallies.Where(t => result.LeadingArtworkIds.Contains(t.ArtworkId)).Select(t => players.Single(p => p.Id == t.OwnerId).Name);
        return $"{(result.LeadingArtworkIds.Count > 1 ? "공동 " : "")}최다 득표 · {string.Join(", ", owners)}\n유효 {result.AcceptedVotes}표 · 미투표 {result.MissingVotes}명";
    }
    private static int Seconds(TimeSpan time) => (int)Math.Ceiling(time.TotalSeconds);
    private static string Progress(PlayerRoundState state) => state switch
    {
        PlayerRoundState.Writing => "작성 중", PlayerRoundState.Generating => "생성 중", PlayerRoundState.Receiving => "전송 중",
        PlayerRoundState.Completed => "제출 완료", PlayerRoundState.MissedWritingDeadline => "작성 마감",
        PlayerRoundState.GenerationFailed => "생성 실패·취소", PlayerRoundState.GenerationTimedOut => "생성 시간 초과",
        PlayerRoundState.TransferTimedOut => "전송 시간 초과", _ => "그림 오류"
    };

    public async Task<object> SmokeAsync(string output, int roundCount = 1)
    {
        WritingSeconds.Text = "600"; GenerationSeconds.Text = "180"; Count.SelectedItem = 3;
        RoundCount.SelectedItem = roundCount;
        WordChoice.SelectedItem = words.Single(w => w.Id == "apple"); CreateLobbyClicked(this, new());
        await ReadyAllPreviewPlayersAsync(); StartClicked(this, new());
        var measurements = new List<object>();
        var roundIds = new List<Guid>();
        for (int number = 1; number <= roundCount; number++)
        {
            await Until(() => rounds.Length == 3 && rounds.All(r => r.Snapshot().RoundNumber == number && r.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
            Viewer.SelectedIndex = 0; Refresh();
            Require(Description.Text.Length == 0 && English.Text.Length == 0 && rounds.All(r => r.Snapshot().Round is { Draft: null, OwnPng: null }), "New round retained draft/image");
            Guid id = Current!.Snapshot().RoundId; Require(!roundIds.Contains(id), "Round ID reused"); roundIds.Add(id);
            string word = CurrentRound!.Submission.Prompt!.Id;
            var example = word switch
            {
                "banana" => ("길고 휘어진 노란 과일", "a long curved yellow fruit"),
                "rabbit" => ("긴 귀를 가진 작고 하얀 동물", "a small white animal with long ears"),
                "umbrella" => ("비를 막아주는 펼쳐진 둥근 물건", "an open round object that blocks rain"),
                "cup" => ("손잡이가 달린 작은 음료 용기", "a small drink container with a handle"),
                "tiger" => ("검은 줄무늬와 주황색 털을 가진 큰 동물", "a large animal with orange fur and black stripes"),
                "dog" => ("털이 많고 꼬리를 흔드는 동물", "a furry animal wagging its tail"),
                "car" => ("네 바퀴가 달린 작은 빨간 탈것", "a small red vehicle with four wheels"),
                _ => ("빨갛고 둥근 과일", "a round red fruit")
            };
            for (int i = 0; i < 3; i++)
            {
                Viewer.SelectedIndex = i; Refresh(); Description.Text = example.Item1;
                var watch = Stopwatch.StartNew(); TranslateClicked(this, new());
                await Until(() => CurrentRound?.AiPhase == RoundAiPhase.Reviewing);
                double translationSeconds = watch.Elapsed.TotalSeconds; string automatic = English.Text;
                English.Text = example.Item2;
                if (i == 0)
                {
                    Viewer.SelectedIndex = 1; Refresh(); Viewer.SelectedIndex = 0; Refresh();
                    Require(English.Text == example.Item2 && Description.Text == example.Item1, "Role switch lost draft edits");
                    Require(!Confirm.IsDefault && !Translate.IsDefault && Description.AcceptsReturn && English.AcceptsReturn, "Enter can submit unexpectedly");
                    Render(Path.Combine(output, $"round-{number}-review.png"));
                }
                watch.Restart(); Act(() => Current!.Confirm(shownRound, shownDraft, English.Text, 42 + i + (number - 1) * 3));
                await Until(() => CurrentRound?.Submission.UploadEnd == ImageSendEnd.Complete);
                var state = CurrentRound!;
                File.WriteAllBytes(Path.Combine(output, $"round-{number}-player-{i + 1}.png"), state.OwnPng!);
                measurements.Add(new { round = number, word, source = example.Item1, automaticTranslation = automatic, corrected = example.Item2,
                    seed = 42 + i + (number - 1) * 3, translationSeconds, generationAndUploadSeconds = watch.Elapsed.TotalSeconds, bytes = state.OwnPng!.Length });
                File.WriteAllText(Path.Combine(output, "generation-progress.json"), JsonSerializer.Serialize(measurements, RuntimeAssets.Json));
            }
            await Until(() => rounds.All(r => r.Snapshot().Round!.Gallery.CanVote));
            Viewer.SelectedIndex = 0; Refresh(); Render(Path.Combine(output, $"round-{number}-gallery.png"));
            var artworks = CurrentRound!.Gallery.Artworks;
            bool selfRejected = false;
            try { Current!.Vote(shownRound, artworks.Single(a => a.OwnerId == "p1").Id); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { selfRejected = true; }
            Require(selfRejected, "Self vote was not rejected");
            int[] targets = [1, 0, 1];
            for (int i = 0; i < 3; i++)
            {
                Viewer.SelectedIndex = i; Refresh();
                string artworkId = artworks.Single(a => a.OwnerId == players[targets[i]].Id).Id;
                VoteClicked(new Button { Tag = artworkId }, new());
            }
            var end = number == roundCount ? MultiplayerMatchPhase.Completed : MultiplayerMatchPhase.BetweenRounds;
            await Until(() => rounds.All(r => r.Snapshot().Phase == end));
            var result = rounds[0].Snapshot().Results[number - 1].Result;
            Require(result.Tallies.Select(t => t.Votes).SequenceEqual(new[] { 1, 2, 0 }), "Unexpected tally");
            string json = JsonSerializer.Serialize(rounds[0].Snapshot().Results, RuntimeAssets.Json);
            Require(rounds.All(r => JsonSerializer.Serialize(r.Snapshot().Results, RuntimeAssets.Json) == json), "Histories differ");
            Viewer.SelectedIndex = 0; Refresh(); Render(Path.Combine(output, $"round-{number}-result.png"));
            Render(Path.Combine(output, $"round-{number}-result-small.png"), 892, 602, 144);
            if (number < roundCount) { Require(NextRound.Visibility == Visibility.Visible, "Host next-round action missing"); NextRoundClicked(this, new()); }
        }
        var results = rounds[0].Snapshot().Results;
        Require(NextRound.Visibility == Visibility.Collapsed && results.Count == roundCount, "Match did not stop after configured rounds");
        await StopAsync();
        return new { status = "passed", transport = "in-memory local preview", realAi = true, participants = 3, dispatcherTicks,
            roundCount, roundIds, measurements, results, identicalResults = true, selfVoteBlocked = true, draftsReset = true };
    }
    private async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (loopFailure is not null || commandError is not null || sessions.Any(s => s.Snapshot().Phase == LobbySessionPhase.Stopped) || watch.Elapsed > TimeSpan.FromMinutes(4))
                throw new InvalidOperationException(loopFailure ?? commandError ?? $"Round smoke timeout: {Current?.Snapshot().Message}");
            await Task.Delay(100); Refresh();
        }
        Refresh();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Render(string destination, double width = 1116, double height = 764, double dpi = 96)
    {
        var margin = Page.Margin; Page.Margin = new(0); Page.Width = width; Page.Height = height;
        Page.Measure(new Size(width, height)); Page.Arrange(new Rect(0, 0, width, height)); Page.UpdateLayout();
        var image = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32); image.Render(Page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(destination); encoder.Save(stream);
        Page.Margin = margin; Page.Width = Page.Height = double.NaN;
    }
}
