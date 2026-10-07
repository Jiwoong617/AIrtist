using System.IO;
using System.Windows;
using AIrtist.Core;

namespace AIrtist.App;

public partial class RoundPreviewWindow
{
    private void RoomParametersChanged(object sender, RoutedEventArgs e)
    { if (RoomParameters is not null) RoomParameters.Visibility = LockRoomParameters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
    private MultiplayerLobbySession[] sessions = [];
    private MultiplayerLobbySession? CurrentLobby => Viewer.SelectedIndex >= 0 && Viewer.SelectedIndex < sessions.Length ? sessions[Viewer.SelectedIndex] : null;

    private MultiplayerMatchSettings ReadLobbySettings()
    {
        if (!int.TryParse(WritingSeconds.Text, out int writing) || writing is < 1 or > 600
            || !int.TryParse(GenerationSeconds.Text, out int generating) || generating is < 1 or > 600
            || RoundCount.SelectedItem is not int count)
            throw new ArgumentException("작성·생성 시간을 1~600초로 입력해 주세요.");
        return MultiplayerMatchSettings.Default with { RoundCount = count, RoundPolicy = MultiplayerRoundPolicy.Default with
            { WritingDuration = TimeSpan.FromSeconds(writing), GenerationDuration = TimeSpan.FromSeconds(generating),
                FixedParameters = LockRoomParameters.IsChecked == true ? RoomParameters.Read() : null } };
    }

    private void CreateLobbyClicked(object sender, RoutedEventArgs e) => Act(() =>
    {
        if (sessions.Length > 0 || rounds.Length > 0) return;
        var settings = ReadLobbySettings();
        if (WordChoice.SelectedItem is not Word word || Count.SelectedItem is not int count) return;
        players = Enumerable.Range(1, count).Select(n => new VotingPlayer($"p{n}", n == 1 ? "참가자 1 · 방장" : $"참가자 {n}")).ToArray();
        var members = players.Select(p => new LobbyMember(p, Guid.NewGuid())).ToArray();
        var network = new LocalRoundNetwork(); var transports = players.Select(p => network.Connect(p.Id)).ToArray();
        Guid lobbyId = Guid.NewGuid(); var created = new List<MultiplayerLobbySession>();
        try
        {
            for (int i = 0; i < players.Length; i++)
                created.Add(new(new(lobbyId, players[0].Id, players[i].Id, compatibility, members), words, ai, transports[i], i == 0 ? settings : null, i == 0 ? word.Id : null));
            sessions = created.ToArray();
        }
        catch
        {
            foreach (var session in created) session.Dispose();
            foreach (var transport in transports) transport.Dispose();
            throw;
        }
        Viewer.ItemsSource = players; Viewer.SelectedIndex = 0;
        StartLobbyLoop();
    });

    private void ApplySettingsClicked(object sender, RoutedEventArgs e) => Act(() =>
    {
        if (online is not null) { OnlineLobbyAction(OnlineAction.Settings); return; }
        if (CurrentLobby?.Snapshot().Lobby is not { } state) return;
        CurrentLobby.UpdateSettings(state.Revision, ReadLobbySettings());
    });

    private void ReadyClicked(object sender, RoutedEventArgs e) => Act(() =>
    {
        if (online is not null) { OnlineLobbyAction(OnlineAction.Ready); return; }
        if (CurrentLobby?.Snapshot().Lobby is not { } state || Viewer.SelectedItem is not VotingPlayer viewer) return;
        if (ReadLobbySettings() != state.Settings) throw new InvalidOperationException("방장이 변경한 설정을 먼저 적용해야 합니다.");
        var member = state.Members.Single(m => m.Member.Player.Id == viewer.Id);
        CurrentLobby.SetReady(state.Revision, !member.Ready);
    });

    private void ClearLobby()
    {
        LobbyMembersLabel.Text = "인원과 시간을 정한 뒤 대기방을 만들어 주세요."; LobbySettingsLabel.Text = "";
    }

    private bool PendingLobbySettings()
    {
        if (CurrentLobby?.Snapshot().Lobby is not { } state) return false;
        try { return ReadLobbySettings() != state.Settings; }
        catch (ArgumentException) { return true; }
    }

    private string LobbyMessage() => CurrentLobby?.Snapshot().Phase == LobbySessionPhase.Stopped ? CurrentLobby.Snapshot().Message : PendingLobbySettings()
        ? "변경한 설정을 적용해 주세요. 적용하면 모두 다시 준비해야 합니다."
        : CurrentLobby?.Snapshot().Message ?? "대기방을 만들고 각 역할에서 준비해 주세요. 온라인 연결은 아직 지원하지 않습니다.";

    private void RefreshLobby()
    {
        if (rounds.Length == 0 && sessions.Length > 0 && sessions.All(s => s.Snapshot().Phase == LobbySessionPhase.Running))
            rounds = sessions.Select(s => s.Match!).ToArray();
        bool playing = rounds.Length > 0, hasRoom = sessions.Length > 0;
        var session = CurrentLobby?.Snapshot(); var state = session?.Lobby;
        bool waiting = state?.Phase == LobbyPhase.Waiting && session?.Phase == LobbySessionPhase.Waiting;
        bool host = state is not null && (Viewer.SelectedItem as VotingPlayer)?.Id == state.HostId;
        bool pending = PendingLobbySettings();
        LobbyPanel.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        MatchContent.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        Setup.IsEnabled = !playing;
        Count.IsEnabled = WordChoice.IsEnabled = !hasRoom;
        RoundCount.IsEnabled = WritingSeconds.IsEnabled = GenerationSeconds.IsEnabled = !hasRoom || (waiting && host);
        LockRoomParameters.IsEnabled = RoomParameters.IsEnabled = !hasRoom || (waiting && host);
        Setup.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        Start.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        CreateLobby.Visibility = !hasRoom ? Visibility.Visible : Visibility.Collapsed;
        ApplySettings.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ApplySettings.IsEnabled = waiting && host && pending;
        Start.IsEnabled = waiting && host && !pending && session!.CanStart;
        Ready.IsEnabled = waiting && !pending && session?.PendingReady == false;
        if (!playing) Reset.IsEnabled = hasRoom;
        if (state is null) { Ready.Content = "준비 완료"; return; }
        Ready.Content = state.Members.SingleOrDefault(m => m.Member.Player.Id == (Viewer.SelectedItem as VotingPlayer)?.Id)?.Ready == true ? "준비 취소" : "준비 완료";
        LobbySettingsLabel.Text = $"{state.Members.Count}명 · {state.Settings.RoundCount}라운드 · 작성 {Seconds(state.Settings.RoundPolicy.WritingDuration)}초 · 생성 {Seconds(state.Settings.RoundPolicy.GenerationDuration)}초";
        LobbyMembersLabel.Text = string.Join("\n", state.Members.Select(m => $"{m.Member.Player.Name}  —  " +
            (m.Ready ? "준비 완료" : m.Issue switch
            {
                LobbyCompatibilityIssue.None => "준비 대기", LobbyCompatibilityIssue.Unchecked => "버전 확인·준비 대기",
                LobbyCompatibilityIssue.Protocol => "통신 버전 다름", LobbyCompatibilityIssue.Rules => "게임 규칙 다름",
                LobbyCompatibilityIssue.Assets => "모델 버전 다름", _ => "단어 목록 다름"
            })));
        if (!playing)
        {
            Prompt.Text = "경기 전 대기방"; Meaning.Text = "설정을 확인하고 각 역할에서 준비를 눌러 주세요.";
            MatchLabel.Text = "준비 " + state.Members.Count(m => m.Ready) + " / " + state.Members.Count;
        }
    }

    private async Task ReadyAllPreviewPlayersAsync()
    {
        await Until(() => sessions.All(s => s.Snapshot().Lobby is not null && s.Snapshot().Lobby!.Settings == sessions[0].Snapshot().Lobby!.Settings));
        for (int i = 0; i < players.Length; i++)
        {
            Viewer.SelectedIndex = i; Refresh();
            if (!CurrentLobby!.Snapshot().Lobby!.Members.Single(m => m.Member.Player.Id == players[i].Id).Ready) ReadyClicked(this, new());
            Require(commandError is null, "Preview readiness failed: " + commandError);
            await Until(() => !CurrentLobby!.Snapshot().PendingReady);
        }
        await Until(() => sessions.All(s => s.Snapshot().Lobby!.CanStart));
        Viewer.SelectedIndex = 0; Refresh();
    }

    public async Task<object> LobbySmokeAsync(string output)
    {
        Count.SelectedItem = 3; RoundCount.SelectedItem = 2; CreateLobbyClicked(this, new());
        await Until(() => sessions.All(s => s.Snapshot().Lobby is not null));
        Require(sessions.Length == 3 && !Start.IsEnabled && LobbyPanel.Visibility == Visibility.Visible, "Lobby gate missing");
        Render(Path.Combine(output, "lobby-waiting.png"));
        await ReadyAllPreviewPlayersAsync(); Require(Start.IsEnabled, "All-ready host cannot start");
        ReadyClicked(this, new()); Require(!Start.IsEnabled && !Ready.IsEnabled, "Pending ready did not block actions");
        await Until(() => !CurrentLobby!.Snapshot().PendingReady);
        Require(!Start.IsEnabled, "Unready did not block start"); ReadyClicked(this, new());
        await Until(() => sessions.All(s => s.Snapshot().Lobby!.CanStart) && !CurrentLobby!.Snapshot().PendingReady);
        Viewer.SelectedIndex = 1; Refresh();
        Require(!Start.IsEnabled && !RoundCount.IsEnabled, "Guest can edit/start in UI");
        StartClicked(this, new()); Require(commandError is not null && rounds.Length == 0, "Guest start not rejected by Core");
        Viewer.SelectedIndex = 0; RoundCount.SelectedItem = 3; WritingSeconds.Text = "240"; Refresh();
        Require(!Start.IsEnabled && !Ready.IsEnabled && ApplySettings.IsEnabled, "Unapplied settings bypass readiness");
        ApplySettingsClicked(this, new());
        await Until(() => sessions.All(s => s.Snapshot().Lobby!.Settings.RoundCount == 3 && s.Snapshot().Lobby!.Members.All(m => !m.Ready)));
        Require(!Start.IsEnabled, "Settings did not clear readiness");
        Render(Path.Combine(output, "lobby-settings-changed.png"));
        await ReadyAllPreviewPlayersAsync(); Render(Path.Combine(output, "lobby-ready.png"));
        Render(Path.Combine(output, "lobby-ready-small.png"), 892, 602, 144);
        StartClicked(this, new());
        await Until(() => rounds.Length == 3 && rounds.All(r => r.Snapshot().Round?.Submission.Phase == SubmissionClientPhase.Writing));
        Require(rounds.All(r => r.Snapshot().RoundCount == 3) && MatchContent.Visibility == Visibility.Visible
            && LobbyPanel.Visibility == Visibility.Collapsed && Translate.IsEnabled, "Lobby-to-match transition failed");
        Render(Path.Combine(output, "lobby-match-started.png"));
        await ResetPreviewAsync();
        Require(sessions.Length == 0 && rounds.Length == 0 && CreateLobby.Visibility == Visibility.Visible && !Start.IsEnabled, "Reset did not clear lobby/match");
        return new { status = "passed", transport = "in-memory local preview", realAi = false, participants = 3,
            readinessGate = true, isolatedLobbyStates = true, acknowledgedStart = true, hostAuthority = true, settingsInvalidateReady = true, roundRuntimeStarted = true, reset = true };
    }
}
