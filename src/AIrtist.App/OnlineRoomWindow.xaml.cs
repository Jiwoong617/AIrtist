using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

public partial class OnlineRoomWindow : Window
{
    private readonly OnlineSessionWorker worker;
    private readonly RuntimeAssets assets;
    private readonly IDrawingAi ai;
    private readonly string settingsPath;
    private readonly Action<string> copy;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private OnlineSnapshot view;
    private string localError = "", memberKey = "", screen = "choice";
    private string? roomCode;
    private bool closing, mayClose, busy, queuedAction, operationPending, openingGame;
    private OnlineCommand? pendingAction;
    private long submittedAfter;

    internal OnlineRoomWindow(RuntimeAssets assets, IDrawingAi ai, OnlineSessionWorker worker, string settingsPath = "", Action<string>? copy = null)
    {
        InitializeComponent(); this.assets = assets; this.ai = ai; this.worker = worker; this.settingsPath = settingsPath;
        this.copy = copy ?? (text => Clipboard.SetText(text)); view = worker.Snapshot;
        RoomParameters.RequireFixedSeed();
        Capacity.ItemsSource = Enumerable.Range(2, 15).ToArray(); Capacity.SelectedItem = 4;
        RoomRounds.ItemsSource = Enumerable.Range(1, 10).ToArray(); RoomRounds.SelectedItem = 3;
        RoomWriting.ItemsSource = new[] { 60, 90, 120, 180, 300 }; RoomWriting.SelectedItem = 120;
        RoomGeneration.ItemsSource = new[] { 60, 120, 180, 240, 300 }; RoomGeneration.SelectedItem = 180;
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh();
        Closing += OnClosing; Closed += (_, _) => timer.Stop();
    }
    private void ChooseCreateClicked(object sender, RoutedEventArgs e) { screen = "create"; localError = ""; Refresh(); }
    private void ChooseJoinClicked(object sender, RoutedEventArgs e) { screen = "join"; localError = ""; Refresh(); CodeInput.Focus(); }
    private async void BackClicked(object sender, RoutedEventArgs e)
    {
        if (screen == "choice" || view.Room?.Membership is not null) { Close(); return; }
        await LeaveAsync(); screen = "choice"; localError = ""; Refresh();
    }
    private void Send(OnlineAction action, string text = "", string extra = "", int number = 0)
    {
        localError = worker.Post(new(action, view.Generation, text, extra, number)) ? "" : "요청이 많습니다. 잠시 후 다시 시도해 주세요.";
    }
    // Diagnostic override only. Production callers provide no file path.
    private void ConnectClicked(object sender, RoutedEventArgs e) { Send(OnlineAction.Connect, settingsPath, "플레이어"); Refresh(); }
    private void StartAction(OnlineCommand action)
    {
        if (queuedAction || operationPending || busy || closing) return;
        localError = "";
        if (view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle)
        {
            if (!worker.Post(action with { Generation = view.Generation })) { localError = "잠시 후 다시 시도해 주세요."; return; }
            operationPending = true;
            submittedAfter = view.ProcessedCommands;
        }
        else if (view.Phase == OnlinePhase.Offline || view.Phase == OnlinePhase.Failed && !view.HasSession)
        {
            pendingAction = action; queuedAction = true;
            if (!worker.Post(new(OnlineAction.Connect, view.Generation, settingsPath, "플레이어")))
            { queuedAction = false; pendingAction = null; localError = "접속을 시작하지 못했습니다. 다시 시도해 주세요."; }
        }
        else { localError = "연결을 정리한 뒤 다시 시도해 주세요."; }
        Refresh();
    }
    private void RoomParametersChanged(object sender, RoutedEventArgs e)
    {
        if (RoomParameters is null || ParameterSurface is null) return;
        ParameterSurface.Visibility = RoomParameters.Visibility = LockRoomParameters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CreateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = MultiplayerMatchSettings.Default with { RoundCount = (int)RoomRounds.SelectedItem, RoundPolicy = MultiplayerRoundPolicy.Default with
            {
                WritingDuration = TimeSpan.FromSeconds((int)RoomWriting.SelectedItem), GenerationDuration = TimeSpan.FromSeconds((int)RoomGeneration.SelectedItem),
                FixedParameters = LockRoomParameters.IsChecked == true ? RoomParameters.Read() : null
            }};
            StartAction(new(OnlineAction.Create, view.Generation, Number: (int)Capacity.SelectedItem, Settings: settings));
        }
        catch (ArgumentException ex) { localError = ex.Message; Refresh(); }
    }
    private void SearchClicked(object sender, RoutedEventArgs e) => Send(OnlineAction.Search);
    private void JoinClicked(object sender, RoutedEventArgs e)
    {
        try { StartAction(new(OnlineAction.JoinCode, view.Generation, RoomCode.Normalize(CodeInput.Text))); }
        catch (ArgumentException ex) { localError = ex.Message; Refresh(); }
    }
    private void CopyCodeClicked(object sender, RoutedEventArgs e)
    {
        if (roomCode is null) return;
        try { copy(RoomCode.Display(roomCode)); localError = "방 코드 복사 완료"; }
        catch (System.Runtime.InteropServices.ExternalException) { localError = "방 코드 복사에 실패했습니다."; }
        Refresh();
    }
    private void PrepareClicked(object sender, RoutedEventArgs e) { Send(OnlineAction.Prepare); Refresh(); }
    private async void LeaveClicked(object sender, RoutedEventArgs e) { await LeaveAsync(); screen = "choice"; Refresh(); }
    private async Task LeaveAsync()
    {
        busy = true; pendingAction = null; queuedAction = operationPending = false; localError = ""; Refresh();
        await worker.DisconnectAsync(); busy = false; Refresh();
    }
    private async void RetryClicked(object sender, RoutedEventArgs e) { await LeaveAsync(); localError = "다시 방 만들기 또는 방 참가를 눌러 주세요."; Refresh(); }
    private async void OpenGameClicked(object sender, RoutedEventArgs e)
    {
        if (view.Lobby?.Lobby is null || busy || closing) { openingGame = false; return; }
        busy = true; timer.Stop();
        try
        {
            var game = new RoundPreviewWindow(assets, ai, worker) { Owner = this, Width = Width, Height = Height };
            Hide(); game.ShowDialog();
        }
        catch (Exception) { localError = "게임 화면을 열지 못했습니다. 다시 방에 모여 주세요."; }
        finally
        {
            await worker.DisconnectAsync(); busy = openingGame = false; screen = "choice";
            if (!closing) { Show(); timer.Start(); Refresh(); }
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true; if (closing) return;
        closing = busy = true; pendingAction = null; Refresh();
        await worker.DisconnectAsync(); mayClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void Refresh()
    {
        view = worker.Snapshot;
        if (queuedAction && view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle && pendingAction is { } action)
        {
            queuedAction = false; pendingAction = null;
            operationPending = worker.Post(action with { Generation = view.Generation });
            submittedAfter = view.ProcessedCommands;
            if (!operationPending) localError = "요청을 처리하지 못했습니다. 다시 눌러 주세요.";
        }
        if (view.Phase is OnlinePhase.Failed or OnlinePhase.Closed || view.Error is not null)
        { queuedAction = operationPending = false; pendingAction = null; }
        if (view.Room?.State is EosLobbyState.Creating or EosLobbyState.Searching or EosLobbyState.Joining or EosLobbyState.Joined)
            operationPending = false;
        if (view.ProcessedCommands > submittedAfter) operationPending = false;
        bool waiting = view.Room?.Membership is not null;
        bool working = busy || queuedAction || operationPending || view.Phase is OnlinePhase.Connecting or OnlinePhase.Leaving ||
            view.Room?.State is EosLobbyState.Searching or EosLobbyState.Creating or EosLobbyState.Joining;
        bool available = !working && !waiting && (view.Phase == OnlinePhase.Offline || view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle);
        ChoicePanel.Visibility = !waiting && screen == "choice" ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = !waiting && screen == "create" ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = !waiting && screen == "join" ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        Create.IsEnabled = Join.IsEnabled = available;
        Capacity.IsEnabled = RoomRounds.IsEnabled = RoomWriting.IsEnabled = RoomGeneration.IsEnabled = LockRoomParameters.IsEnabled = RoomParameters.IsEnabled = !working && !waiting;
        CodeInput.IsEnabled = !working;
        ChooseCreate.IsEnabled = ChooseJoin.IsEnabled = !working;
        Prepare.IsEnabled = !busy && view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Joined && view.Room.Membership?.Members.Count >= 2 && view.Connection is null;
        Prepare.Content = view.Connection is null ? "게임 준비" : "준비 대기 중…";
        OpenGame.IsEnabled = !busy && view.Phase == OnlinePhase.Connected && view.Lobby?.Phase == LobbySessionPhase.Waiting && view.Lobby.Lobby is not null;
        OpenGame.Visibility = OpenGame.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        Leave.IsEnabled = !busy && view.HasSession;
        Retry.Visibility = view.Phase == OnlinePhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        Retry.IsEnabled = !busy;
        roomCode = RoomCode.FromLobbyId(view.Room?.Membership?.Lobby.LobbyId);
        CodeBadge.Visibility = roomCode is null ? Visibility.Collapsed : Visibility.Visible;
        CodeBadge.Content = roomCode is null ? "" : "방 코드   " + RoomCode.Display(roomCode) + "   ⧉";
        if (view.Room?.Membership is { } room)
        {
            Members.Text = $"참가자  {room.Members.Count} / {room.Lobby.MaxMembers}";
            string key = string.Join("/", room.Members) + room.Lobby.OwnerId;
            if (memberKey != key)
            {
                memberKey = key;
                MemberCards.ItemsSource = room.Members.Select((id, i) => (id == view.LocalId ? "나" : $"참가자 {i + 1}") + (id == room.Lobby.OwnerId ? "   ·   방장" : "   ·   참가자")).ToArray();
            }
        }
        else { memberKey = ""; MemberCards.ItemsSource = null; }
        Connecting.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = !string.IsNullOrEmpty(localError) ? localError : view.Error ?? (working ? "연결 중…" :
            view.Phase == OnlinePhase.Offline || screen == "choice" && view.Phase is not (OnlinePhase.Failed or OnlinePhase.Closed) ? "" : view.Message);
        Status.Visibility = string.IsNullOrEmpty(Status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (IsVisible && OpenGame.IsEnabled && !openingGame)
        {
            openingGame = true;
            _ = Dispatcher.BeginInvoke(new Action(() => { Refresh(); if (!closing) OpenGameClicked(this, new()); }));
        }
    }
}
