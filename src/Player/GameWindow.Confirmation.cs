using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private TaskCompletionSource<bool>? confirmation;

    private async Task<bool> ConfirmInGameAsync(string title, string message, CancellationToken token = default)
    {
        if (closed || confirmation != null) return false;
        var previousFocus = Keyboard.FocusedElement;
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        confirmation = response;
        ConfirmationTitle.Text = title;
        ConfirmationMessage.Text = message;
        ConfirmationOverlay.Visibility = Visibility.Visible;
        UpdatePlayClock();
        ConfirmationNo.Focus();
        try { return await response.Task.WaitAsync(token); }
        finally
        {
            confirmation = null;
            ConfirmationOverlay.Visibility = Visibility.Collapsed;
            UpdatePlayClock();
            lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!closed && previousFocus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        }
    }

    private void Confirmation_Click(object sender, RoutedEventArgs e) => CompleteConfirmation(ReferenceEquals(sender, ConfirmationYes));

    private void CompleteConfirmation(bool accepted) => confirmation?.TrySetResult(accepted);
}
