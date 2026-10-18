using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIrtist.Core;

namespace AIrtist.App;

public partial class FinalResultsView : UserControl
{
    private MatchFinalResults? shown;
    public event EventHandler? LeaveRequested;
    private sealed record WinnerCard(string PlayerId, string Name, string VotesText, string Place)
    {
        public override string ToString() => $"{Name}, {Place}, {VotesText}";
    }
    public FinalResultsView() => InitializeComponent();
    internal bool SmokeCanLeave => Leave.IsEnabled;
    internal void SmokeLeave() => Leave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    internal void ShowResults(MatchFinalResults result, bool deliveryPending)
    {
        if (!ReferenceEquals(shown, result))
        {
            shown = result;
            WinnerCards.ItemsSource = result.Winners.Select(p => new WinnerCard(p.PlayerId, p.Name, $"누적 {p.Votes}표",
                result.Winners.Count == 1 ? "1위" : "공동 1위")).ToArray();
            NoVotes.Visibility = result.Winners.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            WinnersScroll.Visibility = result.Winners.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            WinnersScroll.ScrollToLeftEnd();
        }
        DeliveryStatus.Visibility = deliveryPending ? Visibility.Visible : Visibility.Collapsed;
        Leave.IsEnabled = !deliveryPending;
    }
    private void LeaveClicked(object sender, RoutedEventArgs e) { if (Leave.IsEnabled) LeaveRequested?.Invoke(this, EventArgs.Empty); }
    private void WinnersWheel(object sender, MouseWheelEventArgs e)
    {
        if (WinnersScroll.ScrollableWidth <= 0 || WinnersScroll.ScrollableHeight > 0) return;
        WinnersScroll.ScrollToHorizontalOffset(WinnersScroll.HorizontalOffset - e.Delta); e.Handled = true;
    }
    private void WinnersKeyDown(object sender, KeyEventArgs e)
    {
        if (WinnersScroll.ScrollableWidth <= 0) return;
        if (e.Key == Key.Left) WinnersScroll.LineLeft();
        else if (e.Key == Key.Right) WinnersScroll.LineRight();
        else return;
        e.Handled = true;
    }
}
