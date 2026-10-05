using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIrtist.Core;
using Microsoft.Win32;

namespace AIrtist.App;

public partial class VotingWindow : Window
{
    private readonly Dictionary<string, VotingArtwork> selected = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private VotingPlayer[] players = [];
    private VotingRoundHost? host;
    private long shownRevision = -1;
    private string? shownViewer;
    private Card[] cards = [];
    private bool ready;
    private sealed record Card(string Id, BitmapSource Preview, string OwnerLabel, string Note, string Action, bool CanVote, Brush Outline);

    public VotingWindow(byte[]? recentDrawing = null)
    {
        InitializeComponent(); ready = true;
        if (recentDrawing is not null) selected["p1"] = new("image-p1", "p1", recentDrawing);
        UpdateImports();
        timer.Tick += (_, _) => Refresh(); timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void CountChanged(object sender, SelectionChangedEventArgs e) { if (ready) UpdateImports(); }
    private void UpdateImports()
    {
        int count = PlayerCount.SelectedIndex + 2;
        players = Enumerable.Range(1, count).Select(i => new VotingPlayer("p" + i, "플레이어 " + i)).ToArray();
        Imports.ItemsSource = players.Select(p => new { p.Id, p.Name, Label = selected.ContainsKey(p.Id) ? "PNG 준비됨" : "아직 그림 없음" }).ToArray();
        StartVoting.IsEnabled = players.Count(p => selected.ContainsKey(p.Id)) >= 2;
    }
    private void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG 그림|*.png", Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string id = (string)((Button)sender).Tag;
            selected[id] = ReadArtwork(id, dialog.FileName);
            Notice.Text = ""; UpdateImports();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Notice.Text = ex.Message; }
    }
    private static VotingArtwork ReadArtwork(string owner, string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > VotingArtwork.MaxBytes) throw new ArgumentException("PNG 파일은 4MiB 이하여야 합니다.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        return new("image-" + owner, owner, bytes);
    }
    private void StartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            host = new(Guid.NewGuid(), Guid.NewGuid(), 1, "p1", players,
                players.Where(p => selected.ContainsKey(p.Id)).Select(p => selected[p.Id]));
            SetupPanel.Visibility = Visibility.Collapsed; VotingControls.Visibility = Visibility.Visible;
            shownRevision = -1; shownViewer = null;
            Viewer.ItemsSource = players; Viewer.SelectedIndex = 0;
            Notice.Text = ""; Refresh();
        }
        catch (ArgumentException ex) { Notice.Text = ex.Message; }
    }
    private string ViewerId => (string?)Viewer.SelectedValue ?? "p1";
    private void ViewerChanged(object sender, SelectionChangedEventArgs e) { if (ready) { Notice.Text = ""; Refresh(); } }
    private void Refresh()
    {
        if (host is null) return;
        var state = host.Snapshot(ViewerId);
        CloseVoting.IsEnabled = ViewerId == "p1" && state.Result is null;
        if (state.Result is { } result)
            Status.Text = result.Reason == VotingEnd.InsufficientImages ? "그림이 부족해 투표 없이 종료했습니다."
                : $"투표 결과 · {result.AcceptedVotes}표 / {result.EligibleVoters}명 · 미투표 {result.MissingVotes}명";
        else Status.Text = $"남은 시간 {Math.Ceiling(state.Remaining.TotalSeconds)}초 · {state.AcceptedVotes}/{state.EligibleVoters}명 투표"
                + (state.SelectedArtworkId is not null ? " · 투표를 마쳤어요" : state.CanVote ? " · 마음에 드는 그림을 선택하세요" : " · 투표 대상이 없습니다");
        if (shownRevision == state.Revision && shownViewer == ViewerId) return;
        shownRevision = state.Revision; shownViewer = ViewerId;
        cards = state.Artworks.Select(a =>
        {
            bool own = a.OwnerId == ViewerId, chosen = state.SelectedArtworkId == a.Id;
            var tally = state.Result?.Tallies.Single(t => t.ArtworkId == a.Id);
            bool leading = state.Result?.LeadingArtworkIds.Contains(a.Id) == true;
            string note = tally is null ? own ? "자기 그림에는 투표할 수 없어요" : chosen ? "이 그림에 투표했어요" : "한 그림에 한 번만 투표해요"
                : $"{tally.Votes}표" + (leading ? state.Result!.LeadingArtworkIds.Count > 1 ? " · 공동 최다 득표" : " · 최다 득표" : "");
            return new Card(a.Id, a.Preview, state.Players.Single(p => p.Id == a.OwnerId).Name + (own ? " · 내 그림" : ""), note,
                tally is not null ? "투표 종료" : own ? "내 그림" : chosen ? "투표 완료" : "이 그림에 투표", state.CanVote && !own,
                own ? new SolidColorBrush(Color.FromRgb(167, 227, 194)) : Brushes.Transparent);
        }).ToArray();
        Gallery.ItemsSource = cards;
    }
    private VoteReceipt Cast(string artworkId)
    {
        var state = host!.Snapshot(ViewerId);
        var receipt = host.Vote(ViewerId, new(state.MatchId, state.RoundId, state.VotingVersion, Guid.NewGuid().ToString("N"), ViewerId, artworkId));
        Notice.Text = receipt.Status is VoteStatus.Accepted or VoteStatus.Duplicate ? "" : "투표할 수 없습니다. 이미 투표했거나 투표가 마감됐는지 확인해 주세요.";
        Refresh(); return receipt;
    }
    private void VoteClicked(object sender, RoutedEventArgs e) => Cast((string)((Button)sender).Tag);
    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        try { host?.Close(ViewerId); Refresh(); }
        catch (UnauthorizedAccessException ex) { Notice.Text = ex.Message; }
    }
    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        host = null; Gallery.ItemsSource = null; cards = []; selected.Clear();
        SetupPanel.Visibility = Visibility.Visible; VotingControls.Visibility = Visibility.Collapsed;
        Status.Text = "두 사람 이상의 그림을 선택하면 시작할 수 있어요."; Notice.Text = ""; UpdateImports();
    }

    public object Smoke(string output, string[] paths)
    {
        if (paths.Length != 3) throw new ArgumentException("투표 진단에는 서로 다른 실제 PNG 3장이 필요합니다.");
        for (int i = 0; i < 3; i++) selected["p" + (i + 1)] = ReadArtwork("p" + (i + 1), paths[i]);
        if (selected.Values.Select(a => a.Sha256).Distinct().Count() != 3) throw new InvalidOperationException("진단 그림이 중복됐습니다.");
        UpdateImports(); StartClicked(this, new());
        Require(cards.Length == 3 && !cards[0].CanVote && cards.Skip(1).All(c => c.CanVote), "Own image UI not blocked");
        Render(Path.Combine(output, "voting-gallery.png"));
        Render(Path.Combine(output, "voting-gallery-small.png"), 760, 550, 144);
        Require(Cast("image-p1").Status == VoteStatus.SelfVote, "Host allowed self vote");
        Require(Cast("image-p2").Status == VoteStatus.Accepted && cards.All(c => !c.CanVote), "First vote failed");
        var first = host!.Snapshot("p1");
        Viewer.SelectedIndex = 1; Refresh();
        Require(Cast("image-p1").Status == VoteStatus.Accepted, "Second vote failed");
        Viewer.SelectedIndex = 2; Refresh();
        Require(Cast("image-p2").Status == VoteStatus.Accepted, "Third vote failed");
        var final = host.Snapshot("p3").Result!;
        Require(final.AcceptedVotes == 3 && final.Tallies.Single(t => t.ArtworkId == "image-p2").Votes == 2 && cards.All(c => !c.CanVote), "Tally mismatch");
        foreach (var player in players)
            Require(JsonSerializer.Serialize(host.Snapshot(player.Id).Result, RuntimeAssets.Json) == JsonSerializer.Serialize(final, RuntimeAssets.Json), "Participant results differ");
        Render(Path.Combine(output, "voting-result.png"));
        Render(Path.Combine(output, "voting-result-small.png"), 760, 550, 144);
        ResetClicked(this, new());
        Require(host is null && cards.Length == 0 && selected.Count == 0, "Reset retained prior round");
        return new { status = "passed", participants = 3, actualImages = 3, firstVoteRevision = first.Revision, result = final, identicalResults = true, selfVoteBlocked = true, resetPassed = true };
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Render(string destination, double width = 1064, double height = 708, double dpi = 96)
    {
        var margin = Page.Margin; Page.Margin = new(0); Page.Width = width; Page.Height = height;
        Page.Measure(new Size(width, height)); Page.Arrange(new Rect(0, 0, width, height)); Page.UpdateLayout();
        var image = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(Page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(destination); encoder.Save(stream);
        Page.Margin = margin; Page.Width = Page.Height = double.NaN;
    }
}
