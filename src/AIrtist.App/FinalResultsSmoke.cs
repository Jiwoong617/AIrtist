using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIrtist.Core;

namespace AIrtist.App;

public partial class FinalResultsView
{
    internal void FocusEntry() { if (WinnersScroll.IsVisible) WinnersScroll.Focus(); else Leave.Focus(); }
    internal static async Task<object> Smoke(string output)
    {
        var view = new FinalResultsView { Margin = new Thickness(24) };
        var window = new Window { Title = "AIrtist · 결과 화면 검사", Width = 1120, Height = 820, Content = view };
        window.SetResourceReference(Window.BackgroundProperty, "PageBackground");
        window.Show(); await Task.Delay(50);
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        MatchFinalResults Fixture(params int[] totals)
        {
            var players = totals.Select((_, i) => new VotingPlayer("p" + i, i == 15 ? "가나다라마바사아자차카타파하가나" : "플레이어 " + (i + 1))).ToArray();
            var result = new VotingResult(Guid.NewGuid(), Guid.NewGuid(), 1, 1, VotingEnd.AllVoted, DateTimeOffset.UnixEpoch,
                players.Select((p, i) => new ArtworkTally("a" + i, p.Id, totals[i])).ToArray(), [], totals.Sum(), players.Length, 0);
            return MatchFinalResults.Create(players, [new(1, "apple", result)]);
        }
        async Task Display(MatchFinalResults result, string image, bool dark = false, bool small = false)
        {
            ThemeManager.Apply(dark); window.Width = small ? 940 : 1120; window.Height = small ? 680 : 820;
            view.ShowResults(result, false); await Task.Delay(30); view.UpdateLayout();
            MenuWindow.Capture(view, Path.Combine(output, image));
        }
        try
        {
            await Display(Fixture(1, 2, 0), "single-light.png");
            Check(view.WinnerCards.Items.Count == 1 && view.WinnersScroll.ScrollableWidth == 0, "Single winner layout");
            var middle = view.WinnerCards.TranslatePoint(new Point(view.WinnerCards.ActualWidth / 2, 0), view);
            Check(Math.Abs(middle.X - view.ActualWidth / 2) < 2, "Single winner is not centered");
            await Display(Fixture(1, 1), "tie-dark.png", true);
            Check(view.WinnerCards.Items.Count == 2 && view.WinnerCards.Items.Cast<WinnerCard>().All(c => c.Place == "공동 1위"), "Tie label");
            var many = Fixture(Enumerable.Repeat(1, 16).ToArray());
            await Display(many, "sixteen-first.png", small: true);
            Check(view.WinnerCards.Items.Count == 16 && view.WinnersScroll.ScrollableWidth > 0, "Tied winners cannot scroll");
            view.WinnersWheel(view, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            view.UpdateLayout(); Check(view.WinnersScroll.HorizontalOffset > 0, "Mouse wheel did not scroll horizontally");
            view.WinnersScroll.ScrollToRightEnd(); view.UpdateLayout();
            double offset = view.WinnersScroll.HorizontalOffset;
            view.ShowResults(many, false); view.UpdateLayout();
            Check(view.WinnersScroll.HorizontalOffset == offset && offset > 0, "Refresh reset horizontal scroll");
            MenuWindow.Capture(view, Path.Combine(output, "sixteen-last.png"));
            await Display(Fixture(0, 0, 0), "no-votes-dark.png", true, true);
            Check(view.NoVotes.Visibility == Visibility.Visible && view.WinnerCards.Items.Count == 0 && view.WinnersScroll.Visibility == Visibility.Collapsed, "Zero votes displayed a winner");
            int leaves = 0; view.LeaveRequested += (_, _) => leaves++;
            view.ShowResults(many, true); view.SmokeLeave(); Check(leaves == 0, "Host could leave while result delivery pending");
            view.ShowResults(many, false); view.SmokeLeave(); Check(leaves == 1, "Final leave button did not execute");
            return new { status = "passed", singleCentered = true, sharedFirstPlace = true, sixteenHorizontalScroll = true,
                scrollPreserved = true, zeroVotesWithoutWinner = true, themes = true, longName = true, deliveryProtected = true };
        }
        finally { window.Close(); ThemeManager.Apply(false); }
    }
}
