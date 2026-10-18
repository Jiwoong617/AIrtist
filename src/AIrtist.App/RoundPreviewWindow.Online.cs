using System.Windows;
using AIrtist.Core;
using AIrtist.Eos;

namespace AIrtist.App;

public partial class RoundPreviewWindow
{
    private OnlineSessionWorker? online;
    private OnlineSnapshot? onlineView;
    private long onlineGeneration, shownLobbyRevision = -1;
    private string? onlineRoomCode;

    internal RoundPreviewWindow(RuntimeAssets assets, IDrawingAi ai, OnlineSessionWorker worker) : this(assets, ai)
    {
        online = worker; onlineView = worker.Snapshot; onlineGeneration = onlineView.Generation;
        onlineRoomCode = RoomCode.FromLobbyId(onlineView.Room?.Membership?.Lobby.LobbyId);
        if (onlineRoomCode is not null)
        { OnlineRoomCode.Content = "방 코드   " + RoomCode.Display(onlineRoomCode) + "   ⧉"; OnlineRoomCode.Visibility = Visibility.Visible; }
        Title = "AIrtist · 멀티 플레이"; WindowHeading.Text = "멀티 플레이";
        WindowHint.Visibility = Visibility.Collapsed;
        LobbyGuide.Text = "모두 준비하면 방장이 경기를 시작합니다. 내 이름 옆에 ‘나’가 표시됩니다.";
        LobbyPolicy.Text = "설정 변경 시 모두 다시 준비합니다. 참가자·방장이 바뀌면 접속을 종료하고 새로 모여 주세요.";
        Count.Visibility = CountCaption.Visibility = WordChoice.Visibility = WordCaption.Visibility = RolePanel.Visibility = Visibility.Collapsed;
        Reset.Content = "방 나가기";
        // There is exactly one controllable participant in an online window.
        var state = onlineView.Lobby?.Lobby ?? throw new InvalidOperationException("대기방을 기다려 주세요.");
        players = state.Members.Select(m => m.Member.Player).ToArray();
        Viewer.ItemsSource = players.Where(p => p.Id == onlineView.LocalId).ToArray(); Viewer.SelectedIndex = 0;
        Refresh();
    }
    private void CopyOnlineCodeClicked(object sender, RoutedEventArgs e)
    {
        if (onlineRoomCode is null) return;
        try { Clipboard.SetText(RoomCode.Display(onlineRoomCode)); commandError = "방 코드 복사 완료"; }
        catch (System.Runtime.InteropServices.ExternalException) { commandError = "방 코드 복사에 실패했습니다."; Audio?.Cue(SoundCue.ErrorNotice); }
        Refresh();
    }

    private void SendOnline(OnlineAction action, string text = "", int number = 0, GenerationParameters? parameters = null)
    {
        if (!online!.Post(new(action, onlineGeneration, text, Number: number, RoundId: shownRound, Revision: shownDraft, Parameters: parameters)))
            throw new InvalidOperationException("이전 요청을 처리하고 있습니다. 잠시 후 다시 시도해 주세요.");
    }

    private void OnlineLobbyAction(OnlineAction action)
    {
        var state = onlineView?.Lobby?.Lobby ?? throw new InvalidOperationException("대기방을 기다려 주세요.");
        var settings = ReadLobbySettings();
        if (action != OnlineAction.Settings && settings != state.Settings)
            throw new InvalidOperationException("변경한 설정을 먼저 적용해 주세요.");
        bool ready = state.Members.Single(m => m.Member.Player.Id == onlineView!.LocalId).Ready;
        if (!online!.Post(new(action, onlineGeneration, Revision: state.Revision, Ready: !ready, Settings: settings)))
            throw new InvalidOperationException("이전 요청을 처리하고 있습니다. 잠시 후 다시 시도해 주세요.");
    }

    private void RefreshOnlineLobby()
    {
        var session = onlineView!.Lobby; var state = session?.Lobby;
        bool playing = onlineView.Match is not null;
        bool waiting = session?.Phase == LobbySessionPhase.Waiting && state?.Phase == LobbyPhase.Waiting && onlineView.Phase == OnlinePhase.Connected;
        bool host = state?.HostId == onlineView.LocalId;
        if (state is not null && shownLobbyRevision != state.Revision)
        {
            // Only remote/committed settings changes replace the host's current edit.
            if (shownOnlineSettings != state.Settings)
            {
                shownOnlineSettings = state.Settings; RoundCount.SelectedItem = state.Settings.RoundCount;
                WritingSeconds.Text = Seconds(state.Settings.RoundPolicy.WritingDuration).ToString();
                GenerationSeconds.Text = Seconds(state.Settings.RoundPolicy.GenerationDuration).ToString();
                LockRoomParameters.IsChecked = state.Settings.RoundPolicy.FixedParameters is not null;
                if (state.Settings.RoundPolicy.FixedParameters is { } fixedValues) RoomParameters.Set(fixedValues);
            }
            shownLobbyRevision = state.Revision;
        }
        bool pending;
        try { pending = state is not null && ReadLobbySettings() != state.Settings; } catch (ArgumentException) { pending = true; }
        LobbyPanel.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        MatchContent.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        CreateLobby.Visibility = Visibility.Collapsed;
        ApplySettings.Visibility = waiting && host ? Visibility.Visible : Visibility.Collapsed;
        ApplySettings.IsEnabled = waiting && host && pending;
        RoundCount.IsEnabled = WritingSeconds.IsEnabled = GenerationSeconds.IsEnabled = waiting && host;
        LockRoomParameters.IsEnabled = RoomParameters.IsEnabled = waiting && host;
        Setup.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        Start.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        Start.IsEnabled = waiting && host && !pending && session!.CanStart;
        Ready.IsEnabled = waiting && !pending && session?.PendingReady == false;
        Reset.IsEnabled = true;
        if (state is null) return;
        Ready.Content = state.Members.Single(m => m.Member.Player.Id == onlineView.LocalId).Ready ? "준비 취소" : "준비 완료";
        LobbySettingsLabel.Text = $"{state.Members.Count}명 · {state.Settings.RoundCount}라운드 · 작성 {Seconds(state.Settings.RoundPolicy.WritingDuration)}초 · 생성 {Seconds(state.Settings.RoundPolicy.GenerationDuration)}초";
        LobbyMembersLabel.Text = string.Join("\n", state.Members.Select(m => $"{m.Member.Player.Name}" +
            (m.Member.Player.Id == onlineView.LocalId ? " · 나" : "") + (m.Member.Player.Id == state.HostId ? " · 방장" : "") +
            (m.Ready ? "  —  준비 완료" : "  —  준비 대기")));
        if (!playing)
        {
            Prompt.Text = "경기 전 대기방"; Meaning.Text = "설정을 확인하고 준비를 눌러 주세요.";
            MatchLabel.Text = "준비 " + state.Members.Count(m => m.Ready) + " / " + state.Members.Count;
        }
    }
    private MultiplayerMatchSettings? shownOnlineSettings;
}
