using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private Window? detachedConsole;
    private const double MinimumEmbeddedConsoleHeight = 200;
    private double embeddedConsoleHeight = 260;
    private GameTerminal? terminal;
    private bool? scriptConsoleEnabled;
    private bool IsConsoleOpen => DebugOverlay.Visibility == Visibility.Visible || terminal != null;
    private bool ConsoleShortcutEnabled => ConsoleAllowed &&
        (scriptConsoleEnabled == true || StandalonePlayer.Current is { TestBuild: false } ||
            project.Engine.Game.DebugEnabled || diagnosticConsole || IsConsoleOpen);

    private void SetConsoleVisible(bool visible, bool focus = true)
    {
        if (!visible)
        {
            if (terminal is { } active) { terminal = null; active.Dispose(); }
            detachedConsole?.Close();
            DebugOverlay.Visibility = Visibility.Collapsed;
            if (focus && ready && !closed)
            {
                if (runtime.Story.Kind == "input") PlayerInput.FocusAnswer();
                else DialoguePanel.Focus();
            }
            return;
        }
        if (!ConsoleAllowed || IsConsoleOpen || closed) return;
        var settings = project.Engine.Performance;
        CommandInput.ToolTip = $"Команда отладки: Enter. {settings.ConsoleHotkey} — закрыть консоль.";
        try
        {
            switch (settings.ConsoleMode)
            {
                case "terminal":
                    var opened = new GameTerminal(settings.ConsoleHotkey,
                        command => Dispatcher.InvokeAsync(() => { _ = TerminalCommandAsync(command); }).Task);
                    terminal = opened;
                    opened.Write(ConsoleOutput.Text);
                    if (startupFailed) opened.Write(StartupErrorMessage.Text);
                    _ = ObserveTerminalAsync(opened);
                    break;
                case "window":
                    var root = (Grid)Content;
                    root.Children.Remove(DebugOverlay);
                    DebugOverlay.ClearValue(HeightProperty);
                    DebugOverlay.VerticalAlignment = VerticalAlignment.Stretch;
                    DebugOverlay.Margin = new Thickness();
                    ConsoleResizeGrip.Visibility = Visibility.Collapsed;
                    var consoleWindow = new Window
                    {
                        Title = Title + " · Консоль", Owner = this, Width = 780, Height = 320, MinWidth = 460, MinHeight = 240,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Background = DebugOverlay.Background, Resources = Resources, Content = DebugOverlay
                    };
                    detachedConsole = consoleWindow;
                    consoleWindow.PreviewKeyDown += (_, e) => TryToggleConsole(e);
                    consoleWindow.Closed += (_, _) =>
                    {
                        consoleWindow.Content = null;
                        detachedConsole = null;
                        DebugOverlay.Visibility = Visibility.Collapsed;
                        DebugOverlay.VerticalAlignment = VerticalAlignment.Top;
                        DebugOverlay.Margin = new Thickness(16);
                        ConsoleResizeGrip.Visibility = Visibility.Visible;
                        root.Children.Add(DebugOverlay);
                        UpdateEmbeddedConsoleHeight();
                    };
                    DebugOverlay.Visibility = Visibility.Visible;
                    consoleWindow.Show();
                    break;
                default:
                    UpdateEmbeddedConsoleHeight();
                    DebugOverlay.Visibility = Visibility.Visible;
                    break;
            }
            RenderConsole();
            if (focus && terminal == null) CommandInput.Focus();
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            SetConsoleVisible(false, focus: false);
            QueueOutput("Не удалось открыть консоль: " + error.Message);
            if (startupFailed) ErrorActionStatus.Text = error.Message;
            else Notify("Не удалось открыть консоль: " + error.Message);
        }
    }

    private void ConsoleHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateEmbeddedConsoleHeight();

    private void ConsoleResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (detachedConsole != null) return;
        embeddedConsoleHeight = Math.Clamp(DebugOverlay.ActualHeight + e.VerticalChange,
            MinimumEmbeddedConsoleHeight, MaximumEmbeddedConsoleHeight());
        UpdateEmbeddedConsoleHeight();
    }

    private void UpdateEmbeddedConsoleHeight()
    {
        if (DebugOverlay == null || detachedConsole != null) return;
        DebugOverlay.Height = Math.Min(embeddedConsoleHeight, MaximumEmbeddedConsoleHeight());
    }

    private double MaximumEmbeddedConsoleHeight()
    {
        double available = ((FrameworkElement)Content).ActualHeight;
        return available > 0 ? Math.Max(MinimumEmbeddedConsoleHeight, available - DebugOverlay.Margin.Top - DebugOverlay.Margin.Bottom)
            : double.PositiveInfinity;
    }

    private bool TryToggleConsole(KeyEventArgs e)
    {
        if (!ConsoleShortcutEnabled || !GameConsoleShortcut.Matches(e.Key == Key.System ? e.SystemKey : e.Key,
                Keyboard.Modifiers, project.Engine.Performance.ConsoleHotkey)) return false;
        e.Handled = true;
        if (!e.IsRepeat) SetConsoleVisible(!IsConsoleOpen);
        return true;
    }

    private async Task ObserveTerminalAsync(GameTerminal opened)
    {
        string? failure = null;
        try { await opened.Completion; }
        catch (OperationCanceledException) { }
        catch (Exception error) { failure = error.Message; }
        if (terminal != opened) return;
        SetConsoleVisible(false, focus: false);
        if (failure != null)
        {
            QueueOutput("Системная консоль: " + failure);
            if (startupFailed) ErrorActionStatus.Text = failure;
            else Notify(failure);
        }
    }

    private async Task TerminalCommandAsync(string command)
    {
        if (!ConsoleAllowed || terminal == null || closed) return;
        if (command == ":vars") { RenderVariables(); terminal.Write(VariablesOutput.Text); return; }
        if (command == ":stop") { if (StopButton.IsEnabled) StopExecution(); return; }
        if (!await RunConsoleCommandAsync(command))
            terminal?.Write("Команда недоступна, пока игра не запущена или выполняется другая операция.");
    }

    private async Task<bool> RunConsoleCommandAsync(string command)
    {
        if (!ConsoleAllowed || execution != null || !ready || MenuOverlay.Visibility == Visibility.Visible || string.IsNullOrWhiteSpace(command))
            return false;
        await ExecuteAsync(() => runtime.ProcessCommandAsync(command, execution!.Token), operation: "Команда отладки");
        return true;
    }
}
