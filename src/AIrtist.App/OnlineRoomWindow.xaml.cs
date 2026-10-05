using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIrtist.Core;
using AIrtist.Eos;
using Microsoft.Win32;

namespace AIrtist.App;

public partial class OnlineRoomWindow : Window
{
    private readonly OnlineSessionWorker worker;
    private readonly RuntimeAssets assets;
    private readonly IDrawingAi ai;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private OnlineSnapshot view;
    private string roomKey = "", localError = "";
    private bool closing, mayClose, busy;
    private record RoomItem(string Id, string Label);

    internal OnlineRoomWindow(RuntimeAssets assets, IDrawingAi ai, OnlineSessionWorker worker, string settingsPath)
    {
        InitializeComponent(); this.assets = assets; this.ai = ai; this.worker = worker; view = worker.Snapshot;
        SettingsPath.Text = settingsPath;
        Capacity.ItemsSource = Enumerable.Range(2, 15).ToArray(); Capacity.SelectedItem = 4;
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh();
        Closing += OnClosing; Closed += (_, _) => timer.Stop();
    }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "EOS 설정 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SettingsPath.Text = dialog.FileName;
    }
    private void Send(OnlineAction action, string text = "", string extra = "", int number = 0)
    {
        localError = worker.Post(new(action, view.Generation, text, extra, number)) ? "" : "이전 요청을 처리 중입니다. 잠시 후 다시 시도해 주세요.";
        Refresh();
    }
    private void ConnectClicked(object sender, RoutedEventArgs e)
    {
        string name = PlayerName.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name) > 32)
        { localError = "표시 이름을 짧게 입력해 주세요. 한글 10자 또는 영문 32자까지 가능합니다."; Refresh(); return; }
        Send(OnlineAction.Connect, SettingsPath.Text, name);
    }
    private void CreateClicked(object sender, RoutedEventArgs e) => Send(OnlineAction.Create, number: (int)Capacity.SelectedItem);
    private void SearchClicked(object sender, RoutedEventArgs e) => Send(OnlineAction.Search);
    private void JoinClicked(object sender, RoutedEventArgs e) { if (Rooms.SelectedItem is RoomItem item) Send(OnlineAction.Join, item.Id); }
    private void PrepareClicked(object sender, RoutedEventArgs e) => Send(OnlineAction.Prepare);
    private void RoomChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized && worker is not null) Refresh(); }
    private async void LeaveClicked(object sender, RoutedEventArgs e)
    {
        busy = true; localError = ""; Refresh();
        await worker.DisconnectAsync(); busy = false; Refresh();
    }
    private async void OpenGameClicked(object sender, RoutedEventArgs e)
    {
        if (view.Lobby?.Lobby is null || busy) return;
        busy = true; timer.Stop();
        try { new RoundPreviewWindow(assets, ai, worker) { Owner = this }.ShowDialog(); }
        catch (Exception) { localError = "경기 화면을 열지 못했습니다. 앱 파일과 참가자 연결을 확인해 주세요."; }
        finally { await worker.DisconnectAsync(); busy = false; if (!closing) { timer.Start(); Refresh(); } }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true; if (closing) return;
        closing = busy = true; Refresh();
        await worker.DisconnectAsync(); mayClose = true;
        // A completed disconnect can resume inside Closing; queue the second Close after it returns.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void Refresh()
    {
        view = worker.Snapshot;
        bool offline = view.Phase == OnlinePhase.Offline || (view.Phase == OnlinePhase.Failed && !view.HasSession);
        bool idle = !busy && view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle;
        Connect.IsEnabled = Browse.IsEnabled = PlayerName.IsEnabled = offline && !busy;
        Leave.IsEnabled = !busy && view.Phase is OnlinePhase.Connected or OnlinePhase.Connecting or OnlinePhase.Failed;
        Create.IsEnabled = Search.IsEnabled = Capacity.IsEnabled = idle;
        Join.IsEnabled = idle && Rooms.SelectedItem is RoomItem;
        Prepare.IsEnabled = !busy && view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Joined
            && view.Room.Membership?.Members.Count >= 2 && view.Connection is null;
        OpenGame.IsEnabled = !busy && view.Phase == OnlinePhase.Connected && view.Lobby?.Phase == LobbySessionPhase.Waiting && view.Lobby.Lobby is not null;
        var list = view.Room?.SearchResults ?? [];
        string key = string.Join("|", list.Select(r => $"{r.LobbyId}/{r.AvailableSlots}/{r.MaxMembers}"));
        if (key != roomKey)
        {
            roomKey = key; string? selected = (Rooms.SelectedItem as RoomItem)?.Id;
            var items = list.Select((r, i) => new RoomItem(r.LobbyId, $"방 {i + 1} · {r.MaxMembers - r.AvailableSlots}/{r.MaxMembers}명 · {r.LobbyId[..Math.Min(8, r.LobbyId.Length)]}")).ToArray();
            Rooms.ItemsSource = items; Rooms.SelectedItem = items.SingleOrDefault(r => r.Id == selected);
        }
        Members.Text = view.Room?.Membership is { } room ? $"참가자 {room.Members.Count}/{room.Lobby.MaxMembers}명" +
            (room.Lobby.OwnerId == view.LocalId ? " · 내가 방장" : " · 참가 중") +
            (view.Connection is { } ready ? $"  |  연결 준비 {ready.ReadyMembers}/{ready.TotalMembers}명" : "") : "아직 참가한 방이 없습니다.";
        Status.Text = !string.IsNullOrEmpty(localError) ? localError : view.Error ?? (busy ? "접속을 정리하는 중입니다…" : view.Message);
    }
}
