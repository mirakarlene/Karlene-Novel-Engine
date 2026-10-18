using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

internal enum EngineRecoveryAction { Close, Restart, RestartWithoutPlugins }

/// <summary>A separate, minimal error surface which does not reuse potentially broken editor controls.</summary>
internal sealed class EngineRecoveryWindow : Window
{
    private readonly Action<EngineRecoveryAction> respond;
    private bool responded;
    internal Button CloseEngineButton { get; } = new() { Content = "Закрыть" };
    internal Button RestartEngineButton { get; } = new() { Content = "Перезапустить движок" };
    internal Button SafeRestartButton { get; } = new() { Content = "Перезапустить без плагинов" };
    internal TextBox ReportText { get; } = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal TextBlock RecoveryStatus { get; } = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    internal EngineRecoveryWindow(EngineFailureReport report, string reportPath, Action<EngineRecoveryAction> respond)
    {
        this.respond = respond;
        Title = "Сбой Karlene Novel Engine"; Width = 960; Height = 680; MinWidth = 680; MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(27, 20, 43)); Foreground = Brushes.GhostWhite;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = new TextBlock { Text = "Движок остановлен из-за ошибки", FontSize = 25, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(title);
        ReportText.Text = report.ToDisplayText() + "\n\nОтчёт: " + reportPath;
        ReportText.Background = new SolidColorBrush(Color.FromRgb(36, 29, 51)); ReportText.Foreground = Brushes.GhostWhite;
        ReportText.FontFamily = new FontFamily("Consolas"); ReportText.Padding = new Thickness(12); Grid.SetRow(ReportText, 1); grid.Children.Add(ReportText);
        Grid.SetRow(RecoveryStatus, 2); grid.Children.Add(RecoveryStatus);
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (button, action) in new[] { (CloseEngineButton, EngineRecoveryAction.Close), (RestartEngineButton, EngineRecoveryAction.Restart), (SafeRestartButton, EngineRecoveryAction.RestartWithoutPlugins) })
        {
            button.Padding = new Thickness(16, 9, 16, 9); button.Margin = new Thickness(0, 0, 10, 6);
            button.Click += (_, _) => Respond(action); actions.Children.Add(button);
        }
        Grid.SetRow(actions, 3); grid.Children.Add(actions); Content = grid;
        Closed += (_, _) => { if (!responded) Respond(EngineRecoveryAction.Close); };
    }

    private void Respond(EngineRecoveryAction action)
    {
        if (responded) return;
        responded = true;
        try { respond(action); }
        catch (Exception error)
        {
            responded = false;
            RecoveryStatus.Text = "Не удалось выполнить действие: " + error.Message;
        }
    }
}
