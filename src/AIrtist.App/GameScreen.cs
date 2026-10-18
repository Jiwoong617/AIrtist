using System.Windows.Controls;

namespace AIrtist.App;

// Navigation content has no HWND. MenuWindow owns the only application window.
public class GameScreen : UserControl
{
    private Task? closeTask;
    internal GameAudio? Audio { get; set; }
    public event EventHandler? Closed;
    public string Title { get; protected set; } = "AIrtist";
    public async void Close() => await CloseAsync();
    public Task CloseAsync() => closeTask ??= FinishAsync();
    private async Task FinishAsync()
    {
        IsEnabled = false;
        await OnCloseAsync();
        Closed?.Invoke(this, EventArgs.Empty);
    }
    protected virtual Task OnCloseAsync() => Task.CompletedTask;
}
