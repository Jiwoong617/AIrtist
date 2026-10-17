using System.IO;
using System.Windows;
using System.Windows.Threading;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

public partial class OnlineRoomWindow : GameScreen
{
    private readonly OnlineSessionWorker worker;
    private readonly RuntimeAssets assets;
    private readonly IDrawingAi ai;
    private readonly string settingsPath;
    private readonly Action<string> copy;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private OnlineSnapshot view;
    private string localError = "", screen = "choice", connectedName = "";
    private string? roomCode, shownWaitingRevision;
    private bool closing, busy, queuedAction, operationPending;
    private OnlineCommand? pendingAction;
    private long submittedAfter;
    internal RoundPreviewWindow? Game { get; private set; }

    internal OnlineRoomWindow(RuntimeAssets assets, IDrawingAi ai, OnlineSessionWorker worker, string settingsPath = "", Action<string>? copy = null)
    {
        InitializeComponent(); this.assets = assets; this.ai = ai; this.worker = worker; this.settingsPath = settingsPath;
        this.copy = copy ?? (text => Clipboard.SetText(text)); view = worker.Snapshot;
        RoomParameters.RequireFixedSeed(); WaitingParameters.RequireFixedSeed();
        Capacity.ItemsSource = Enumerable.Range(2, 15).ToArray(); Capacity.SelectedItem = 4;
        RoomRounds.ItemsSource = WaitingRounds.ItemsSource = Enumerable.Range(1, 10).ToArray(); RoomRounds.SelectedItem = 3;
        RoomWriting.ItemsSource = WaitingWriting.ItemsSource = new[] { 60, 90, 120, 180, 300 }; RoomWriting.SelectedItem = 120;
        RoomGeneration.ItemsSource = WaitingGeneration.ItemsSource = new[] { 60, 120, 180, 240, 300 }; RoomGeneration.SelectedItem = 180;
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh();
    }
    protected override async Task OnCloseAsync()
    {
        closing = true; timer.Stop();
        if (Game is not null) await Game.CloseAsync();
        await worker.DisconnectAsync();
    }
    private void ChooseCreateClicked(object sender, RoutedEventArgs e) { screen = "create"; CreateNickname.Text = JoinNickname.Text; localError = ""; Refresh(); }
    private void ChooseJoinClicked(object sender, RoutedEventArgs e) { screen = "join"; JoinNickname.Text = CreateNickname.Text; localError = ""; Refresh(); CodeInput.Focus(); }
    private async void BackClicked(object sender, RoutedEventArgs e)
    {
        if (screen == "choice") { Close(); return; }
        await LeaveAsync(); screen = "choice"; localError = ""; Refresh();
    }
    private void Send(OnlineAction action, string text = "", string extra = "", int number = 0)
    { localError = worker.Post(new(action, view.Generation, text, extra, number)) ? "" : "요청이 많습니다. 잠시 후 다시 시도해 주세요."; }
    private void ConnectClicked(object sender, RoutedEventArgs e) { Send(OnlineAction.Connect, settingsPath, "플레이어"); connectedName = "플레이어"; Refresh(); }
    private async void StartAction(OnlineCommand action)
    {
        if (queuedAction || operationPending || busy || closing) return;
        try
        {
            string name = PlayerNickname.Normalize(screen == "create" ? CreateNickname.Text : JoinNickname.Text);
            localError = "";
            // Device authentication can be reused, but the selected display name must not be stale.
            if (view.HasSession && view.Room?.Membership is null && name != connectedName) await LeaveAsync();
            if (closing) return;
            view = worker.Snapshot;
            if (view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle)
            {
                operationPending = worker.Post(action with { Generation = view.Generation }); submittedAfter = view.ProcessedCommands;
            }
            else if (view.Phase == OnlinePhase.Offline || view.Phase == OnlinePhase.Failed && !view.HasSession)
            {
                connectedName = name; pendingAction = action; queuedAction = true;
                if (!worker.Post(new(OnlineAction.Connect, view.Generation, settingsPath, name))) { queuedAction = false; pendingAction = null; localError = "접속을 시작하지 못했습니다."; }
            }
            else localError = "연결을 정리한 뒤 다시 시도해 주세요.";
        }
        catch (ArgumentException ex) { localError = ex.Message; }
        Refresh();
    }
    private void RoomParametersChanged(object sender, RoutedEventArgs e)
    {
        if (RoomParameters is null || ParameterSurface is null) return;
        ParameterSurface.Visibility = RoomParameters.Visibility = LockRoomParameters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void WaitingParametersChanged(object sender, RoutedEventArgs e)
    {
        if (WaitingParameters is not null) WaitingParameters.Visibility = WaitingLock.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private MultiplayerMatchSettings ReadSettings(bool waiting) => MultiplayerMatchSettings.Default with
    {
        RoundCount = (int)(waiting ? WaitingRounds : RoomRounds).SelectedItem, RoundPolicy = MultiplayerRoundPolicy.Default with
        {
            WritingDuration = TimeSpan.FromSeconds((int)(waiting ? WaitingWriting : RoomWriting).SelectedItem),
            GenerationDuration = TimeSpan.FromSeconds((int)(waiting ? WaitingGeneration : RoomGeneration).SelectedItem),
            FixedParameters = (waiting ? WaitingLock : LockRoomParameters).IsChecked == true ? (waiting ? WaitingParameters : RoomParameters).Read() : null
        }
    };
    private void CreateClicked(object sender, RoutedEventArgs e)
    {
        try { StartAction(new(OnlineAction.Create, view.Generation, Number: (int)Capacity.SelectedItem, Settings: ReadSettings(false))); }
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
    private void PrepareClicked(object sender, RoutedEventArgs e)
    {
        if (view.Waiting is { } state)
        {
            bool host = view.LocalId == view.Room!.Membership!.Lobby.OwnerId;
            bool ready = state.Players.Single(p => p.Id == view.LocalId).Ready;
            worker.Post(new(host ? OnlineAction.Start : OnlineAction.Ready, view.Generation, Ready: !ready));
        }
        else Send(OnlineAction.Prepare); // Legacy diagnostic transport.
        Refresh();
    }
    private void ApplyRoomClicked(object sender, RoutedEventArgs e)
    {
        try { worker.Post(new(OnlineAction.Settings, view.Generation, Settings: ReadSettings(true))); localError = ""; }
        catch (ArgumentException ex) { localError = ex.Message; }
        Refresh();
    }
    private async void LeaveClicked(object sender, RoutedEventArgs e) { await LeaveAsync(); screen = "choice"; Refresh(); }
    private async Task LeaveAsync()
    {
        busy = true; pendingAction = null; queuedAction = operationPending = false; localError = ""; Refresh();
        await worker.DisconnectAsync(); shownWaitingRevision = null; busy = false; Refresh();
    }
    private async void RetryClicked(object sender, RoutedEventArgs e) { await LeaveAsync(); Refresh(); }
    private void OpenGameClicked(object sender, RoutedEventArgs e) => OpenMatch();
    private void OpenMatch()
    {
        if (Game is not null || closing || view.Match is null) return;
        Game = new(assets, ai, worker);
        Game.Closed += async (_, _) =>
        {
            if (closing) return;
            GameHost.Content = null; Game = null; await LeaveAsync(); screen = "choice"; Refresh();
        };
        GameHost.Content = Game; GameHost.Visibility = Visibility.Visible; Entrance.Visibility = Visibility.Collapsed;
    }
    private void Refresh()
    {
        if (closing) return;
        view = worker.Snapshot;
        if (queuedAction && view.Phase == OnlinePhase.Connected && view.Room?.State == EosLobbyState.Idle && pendingAction is { } action)
        {
            queuedAction = false; pendingAction = null; operationPending = worker.Post(action with { Generation = view.Generation });
            submittedAfter = view.ProcessedCommands;
            if (!operationPending) localError = "요청을 처리하지 못했습니다.";
        }
        if (view.Phase is OnlinePhase.Failed or OnlinePhase.Closed || view.Error is not null) { queuedAction = operationPending = false; pendingAction = null; }
        if (view.Room?.State is EosLobbyState.Creating or EosLobbyState.Searching or EosLobbyState.Joining or EosLobbyState.Joined || view.ProcessedCommands > submittedAfter) operationPending = false;
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
        CodeInput.IsEnabled = CreateNickname.IsEnabled = JoinNickname.IsEnabled = !working;
        ChooseCreate.IsEnabled = ChooseJoin.IsEnabled = !working;
        Prepare.IsEnabled = false; Prepare.Content = "준비 완료";
        OpenGame.IsEnabled = !busy && view.Lobby?.Lobby is not null; // Diagnostics only; no second lobby in the UI.
        Leave.IsEnabled = !busy && view.HasSession;
        Retry.Visibility = view.Phase == OnlinePhase.Failed ? Visibility.Visible : Visibility.Collapsed; Retry.IsEnabled = !busy;
        roomCode = RoomCode.FromLobbyId(view.Room?.Membership?.Lobby.LobbyId);
        CodeBadge.Visibility = roomCode is null ? Visibility.Collapsed : Visibility.Visible;
        CodeBadge.Content = roomCode is null ? "" : "방 코드   " + RoomCode.Display(roomCode) + "   ⧉";
        if (view.Room?.Membership is { } room)
        {
            bool host = room.Lobby.OwnerId == view.LocalId;
            Members.Text = $"참가자  {room.Members.Count} / {room.Lobby.MaxMembers}";
            MemberCards.ItemsSource = view.Waiting is { } state
                ? state.Players.Select(p => p.Name + (p.Id == view.LocalId ? " · 나" : "") + (p.Host ? " · 방장" : p.Ready ? " · 준비 완료" : " · 준비 대기")).ToArray()
                : room.Members.Select(id => id == view.LocalId ? connectedName + (host ? " · 방장" : " · 나") : "접속 중").ToArray();
            if (view.Waiting is { } current)
            {
                if (shownWaitingRevision != current.Revision)
                {
                    shownWaitingRevision = current.Revision; WaitingRounds.SelectedItem = current.Settings.RoundCount;
                    WaitingWriting.SelectedItem = (int)current.Settings.RoundPolicy.WritingDuration.TotalSeconds;
                    WaitingGeneration.SelectedItem = (int)current.Settings.RoundPolicy.GenerationDuration.TotalSeconds;
                    WaitingLock.IsChecked = current.Settings.RoundPolicy.FixedParameters is not null;
                    if (current.Settings.RoundPolicy.FixedParameters is { } fixedValues) WaitingParameters.Set(fixedValues);
                }
                bool editing = false;
                try { editing = host && ReadSettings(true) != current.Settings; } catch (ArgumentException) { editing = host; }
                bool active = view.Phase == OnlinePhase.Connected && view.Connection is null;
                WaitingFields.IsEnabled = active && host && !current.Starting && !current.Pending;
                ApplyRoom.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
                ApplyRoom.IsEnabled = active && !current.Starting && !current.Pending && editing;
                Prepare.Content = host ? "게임 시작" : current.Players.Single(p => p.Id == view.LocalId).Ready ? "준비 취소" : "준비 완료";
                Prepare.IsEnabled = active && !busy && !current.Starting && !current.Pending && !editing && (host ? current.CanStart : true);
                WaitingPolicy.Text = current.Settings.RoundPolicy.FixedParameters is null ? "참가자별 자유 설정" : "방장 설정으로 고정";
            }
            else { WaitingFields.IsEnabled = false; ApplyRoom.IsEnabled = false; Prepare.Content = host ? "게임 시작" : "준비 완료"; }
        }
        else { MemberCards.ItemsSource = null; shownWaitingRevision = null; }
        Connecting.Visibility = working || view.Waiting?.Starting == true && view.Match is null ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = !string.IsNullOrEmpty(localError) ? localError : view.Error ?? (view.Waiting?.Starting == true ? "경기 연결 준비 중…" : working ? "연결 중…" :
            view.Phase == OnlinePhase.Offline || screen == "choice" && view.Phase is not (OnlinePhase.Failed or OnlinePhase.Closed) ? "" : view.Message);
        Status.Visibility = string.IsNullOrEmpty(Status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (Game is null) { Entrance.Visibility = Visibility.Visible; GameHost.Visibility = Visibility.Collapsed; }
        if (view.Match is not null && IsVisible) OpenMatch();
    }
}
