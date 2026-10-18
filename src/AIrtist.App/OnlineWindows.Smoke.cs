using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIrtist.Core;

namespace AIrtist.App;

public partial class MainWindow
{
    internal void SmokeAttachStoppedOnlineWorker(OnlineSessionWorker worker)
    {
        OnlineSmoke.Require(worker.Snapshot.Phase == OnlinePhase.Closed && onlineWorker is null, "Shutdown regression setup");
        onlineWorker = worker;
    }
}

public partial class OnlineRoomWindow
{
    internal void SmokeLocalCreate(int rounds, int participants = 3)
    {
        ChooseCreateClicked(this, new()); Capacity.SelectedItem = participants; RoomRounds.SelectedItem = rounds;
        RoomWriting.SelectedItem = 300; RoomGeneration.SelectedItem = 300; CreateClicked(this, new());
    }
    internal void SmokeNickname(string name) { CreateNickname.Text = JoinNickname.Text = name; }
    internal void SmokeWaitingReady() { Refresh(); OnlineSmoke.Require(Prepare.IsEnabled, "Waiting action unavailable"); PrepareClicked(this, new()); }
    internal void SmokeWaitingSettings(GenerationParameters? fixedValues)
    {
        Refresh(); WaitingLock.IsChecked = fixedValues is not null;
        if (fixedValues is not null) WaitingParameters.Set(fixedValues);
        ApplyRoomClicked(this, new());
    }
    internal void SmokeLocalPrepare() { Refresh(); OnlineSmoke.Require(Prepare.IsEnabled, "Local prepare unavailable"); PrepareClicked(this, new()); }
    internal void SmokeCreateWithParameters(GenerationParameters? fixedValues, string output)
    {
        ChooseCreateClicked(this, new()); Refresh(); OnlineSmoke.Require(Create.IsEnabled, "Create UI unavailable");
        LockRoomParameters.IsChecked = fixedValues is not null;
        if (fixedValues is not null) RoomParameters.Set(fixedValues);
        OnlineSmoke.Require(RoomParameters.Visibility == (fixedValues is null ? Visibility.Collapsed : Visibility.Visible), "Creation parameter visibility wrong");
        SmokeRender(Path.Combine(output, "room-create-parameters.png"));
        Capacity.SelectedItem = 3; CreateClicked(this, new());
    }
    internal void SmokeConnect() { Refresh(); ConnectClicked(this, new()); }
    internal void SmokeLiveSearch() { Refresh(); OnlineLiveSmoke.Require(view.Room?.State == AIrtist.Eos.EosLobbyState.Idle, "방 검색 가능 상태"); SearchClicked(this, new()); }
    internal void SmokeLiveCreate()
    {
        ChooseCreateClicked(this, new()); Refresh(); OnlineLiveSmoke.Require(Create.IsEnabled, "방 생성 버튼 상태");
        Capacity.SelectedItem = 2; CreateClicked(this, new());
    }
    internal void SmokeLiveLeave() { Refresh(); OnlineLiveSmoke.Require(Leave.IsEnabled, "접속 종료 버튼 상태"); LeaveClicked(this, new()); }
    internal void SmokeLiveVerifyJoined()
    {
        Refresh();
        OnlineLiveSmoke.Require(!Create.IsEnabled && !Join.IsEnabled && !Prepare.IsEnabled && !OpenGame.IsEnabled && Leave.IsEnabled && CodeBadge.Visibility == Visibility.Visible,
            "단일 참가자 방의 버튼 권한");
    }
    internal void SmokeJoinCode(string code)
    { ChooseJoinClicked(this, new()); CodeInput.Text = code; JoinClicked(this, new()); }
    internal void SmokeVerifyInvalidCode()
    { Refresh(); OnlineSmoke.Require(!queuedAction && !operationPending && Join.IsEnabled && localError.Contains("방 코드"), "Invalid code started connection or blocked retry"); }
    internal void SmokeVerifyJoinRetry()
    { Refresh(); OnlineSmoke.Require(Join.IsEnabled && !operationPending && !queuedAction, "Missing room blocked code retry"); }
    internal void SmokeCopyCode() { Refresh(); OnlineSmoke.Require(CodeBadge.Visibility == Visibility.Visible, "Room code missing"); CopyCodeClicked(this, new()); }
    internal void SmokeChoices(string output)
    {
        Refresh(); OnlineSmoke.Require(ChoicePanel.Visibility == Visibility.Visible && ChooseCreate.IsEnabled && ChooseJoin.IsEnabled, "Choice screen missing");
        SmokeRender(Path.Combine(output, "multiplayer-choice.png"));
        ChooseJoinClicked(this, new()); SmokeRender(Path.Combine(output, "join-code.png"));
        ChooseCreateClicked(this, new()); SmokeRender(Path.Combine(output, "create-room.png"));
    }
    internal void SmokeRender(string path)
    {
        Refresh(); var margin = Page.Margin; Page.Margin = new(0);
        var size = new Size(824, 660); Page.Width = size.Width; Page.Height = size.Height;
        Page.Measure(size); Page.Arrange(new Rect(size)); Page.UpdateLayout();
        var bitmap = new RenderTargetBitmap(824, 660, 96, 96, PixelFormats.Pbgra32); bitmap.Render(Page);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        Page.Margin = margin; Page.Width = Page.Height = double.NaN;
    }
}

public partial class RoundPreviewWindow
{
    internal void OnlineSmokeSettings() { Refresh(); RoundCount.SelectedItem = 2; ApplySettingsClicked(this, new()); }
    internal void OnlineSmokeReady(bool host)
    {
        Refresh(); OnlineSmoke.Require(Ready.IsEnabled && RoundCount.IsEnabled == host && !Start.IsEnabled && RolePanel.Visibility == Visibility.Collapsed, "Online lobby UI permissions wrong");
        ReadyClicked(this, new());
    }
    internal void OnlineSmokeStart() { Refresh(); OnlineSmoke.Require(Start.IsEnabled, "Host start not enabled"); StartClicked(this, new()); }
    internal void OnlineSmokeTranslate() { Refresh(); OnlineSmoke.Require(Translate.IsEnabled && string.IsNullOrEmpty(English.Text), "New round UI not ready"); Description.Text = "둥글고 붉은 물체"; TranslateClicked(this, new()); }
    internal void OnlineSmokeDraw(int index, string description = "둥글고 붉은 물체")
    {
        Refresh(); OnlineSmoke.Require(Translate.IsEnabled && English.Visibility == Visibility.Collapsed && Confirm.Visibility == Visibility.Collapsed, "One-click UI not ready");
        bool locked = CurrentRound!.Submission.FixedParameters is not null;
        OnlineSmoke.Require(Parameters.IsEnabled == !locked, "Room lock missing");
        if (!locked) Parameters.Set(new(5f + index * .5f, 8 + index, 200 + index));
        Description.Text = description; DrawClicked(this, new());
    }
    internal void OnlineSmokeConfirm(int index = 0)
    {
        Refresh(); OnlineSmoke.Require(Confirm.IsEnabled, "Review UI not ready");
        bool locked = CurrentRound!.Submission.FixedParameters is not null;
        OnlineSmoke.Require(Parameters.IsEnabled == !locked, "Room lock missing from participant UI");
        if (!locked) Parameters.Set(new(5f + index * .5f, 8 + index, 200 + index));
        English.Text = "a round red object"; ConfirmClicked(this, new());
    }
    internal void OnlineSmokeVote(string owner)
    {
        Refresh(); var state = CurrentRound!;
        OnlineSmoke.Require(Cards.Items.Cast<Card>().Single(c => c.Id == state.Gallery.Artworks.Single(a => a.OwnerId == state.LocalId).Id).CanVote == false, "Self vote UI enabled");
        string id = state.Gallery.Artworks.Single(a => a.OwnerId == owner).Id;
        VoteClicked(new Button { Tag = id }, new());
    }
    internal void OnlineSmokeNext() { Refresh(); NextRoundClicked(this, new()); }
    internal void OnlineSmokeFinalButton(bool host)
    {
        Refresh();
        OnlineSmoke.Require((ShowFinal.Visibility == Visibility.Visible) == host, "Final button host visibility");
        if (host) OnlineSmoke.Require(ShowFinal.IsEnabled, "Final button unavailable after confirmations");
        OnlineSmoke.Require(FinalResults.Visibility == Visibility.Collapsed, "Final screen opened without host click");
    }
    internal void OnlineSmokeShowFinal() { Refresh(); ShowFinal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    internal bool OnlineSmokeFinalVisible { get { Refresh(); return FinalResults.Visibility == Visibility.Visible && RoundPage.Visibility == Visibility.Collapsed; } }
    internal bool OnlineSmokeFinalCanLeave => FinalResults.SmokeCanLeave;
    internal void OnlineSmokeFinalLeave() => FinalResults.SmokeLeave();
    internal void OnlineSmokeRender(string path, bool small = false)
    {
        Refresh();
        GalleryAccessibilityChecks.Verify(Cards, item => ((Card)item).Label);
        Render(path, small ? 892 : 1116, small ? 602 : 764, small ? 144 : 96);
    }
}
