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
    internal void SmokeCreateWithParameters(GenerationParameters? fixedValues, string output)
    {
        Refresh(); OnlineSmoke.Require(Create.IsEnabled, "Create UI unavailable");
        LockRoomParameters.IsChecked = fixedValues is not null;
        if (fixedValues is not null) RoomParameters.Set(fixedValues);
        OnlineSmoke.Require(RoomParameters.Visibility == (fixedValues is null ? Visibility.Collapsed : Visibility.Visible), "Creation parameter visibility wrong");
        SmokeRender(Path.Combine(output, "room-create-parameters.png"));
        Capacity.SelectedItem = 3; CreateClicked(this, new());
    }
    internal void SmokeConnect() { Refresh(); ConnectClicked(this, new()); }
    internal void SmokeLiveSearch() { Refresh(); OnlineLiveSmoke.Require(Search.IsEnabled, "방 검색 버튼 상태"); SearchClicked(this, new()); }
    internal void SmokeLiveCreate()
    {
        Refresh(); OnlineLiveSmoke.Require(Create.IsEnabled, "방 생성 버튼 상태");
        Capacity.SelectedItem = 2; CreateClicked(this, new());
    }
    internal void SmokeLiveLeave() { Refresh(); OnlineLiveSmoke.Require(Leave.IsEnabled, "접속 종료 버튼 상태"); LeaveClicked(this, new()); }
    internal void SmokeLiveVerifyJoined()
    {
        Refresh();
        OnlineLiveSmoke.Require(!Create.IsEnabled && !Search.IsEnabled && !Prepare.IsEnabled && !OpenGame.IsEnabled && Leave.IsEnabled,
            "단일 참가자 방의 버튼 권한");
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
    internal void OnlineSmokeDraw(int index)
    {
        Refresh(); OnlineSmoke.Require(Translate.IsEnabled && English.Visibility == Visibility.Collapsed && Confirm.Visibility == Visibility.Collapsed, "One-click UI not ready");
        bool locked = CurrentRound!.Submission.FixedParameters is not null;
        OnlineSmoke.Require(Parameters.IsEnabled == !locked, "Room lock missing");
        if (!locked) Parameters.Set(new(5f + index * .5f, 8 + index, 200 + index));
        Description.Text = "둥글고 붉은 물체"; DrawClicked(this, new());
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
    internal void OnlineSmokeRender(string path, bool small = false) { Refresh(); Render(path, small ? 892 : 1116, small ? 602 : 764, small ? 144 : 96); }
}
