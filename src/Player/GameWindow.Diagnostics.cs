using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private static readonly JsonSerializerOptions VariableFormatting = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private GameErrorReport? currentErrorReport;
    private bool diagnosticConsole;
    private string? renderedVariables;
    private JsonElement formattedSnapshot;
    private string? formattedVariableBody;

    private void RefreshErrorActions()
    {
        ErrorRestartButton.IsEnabled = execution == null && !closed;
        ErrorReturnButton.IsEnabled = execution == null && !closed && runtime is ProcessGameSession { CanReturnAfterError: true };
    }

    private void RenderVariables()
    {
        if (DebugOverlay.Visibility != Visibility.Visible) return;
        var variables = runtime is ProcessGameSession session ? session.Variables : default;
        if (formattedVariableBody == null || !variables.Equals(formattedSnapshot))
        {
            formattedSnapshot = variables;
            formattedVariableBody = variables.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(variables, VariableFormatting)
                : "Переменные появятся после начала выполнения сценария.";
        }
        string text = formattedVariableBody;
        if (project.Engine.Performance.DebugMode) text = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n.NET {Environment.Version} · {Environment.MachineName}\nAPI: {GraphicsEnvironment.For(this).Backend} · логика {runtime.LogicMilliseconds:F2} мс\n" + text;
        if (text == renderedVariables) return;
        VariablesOutput.Text = text;
        renderedVariables = text;
    }

    private void CloseConsole_Click(object sender, RoutedEventArgs e) => DebugOverlay.Visibility = Visibility.Collapsed;

    private void ErrorConsole_Click(object sender, RoutedEventArgs e)
    {
        DebugOverlay.Visibility = DebugOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RenderConsole();
    }

    private async void ErrorRestart_Click(object sender, RoutedEventArgs e) => await RestartFromErrorAsync();

    private async Task RestartFromErrorAsync()
    {
        if (!startupFailed || execution != null || closed) return;
        RestoreErrorPresentation();
        diagnosticConsole = true;
        DebugOverlay.Visibility = Visibility.Visible;
        await ExecuteAsync(async () =>
        {
            var candidate = new ProcessGameSession(project, QueueOutput);
            var previous = runtime;
            runtime = candidate;
            ready = false;
            renderedStory = null; renderedSettings = null; renderedVisuals = null;
            renderedVariables = null;
            await previous.DisposeAsync();
            if (closed) { await candidate.DisposeAsync(); return; }
            await candidate.StartAsync(execution!.Token);
            ready = true;
            ShowGameplay();
        }, starting: true, operation: "Перезапуск игры с консолью");
    }

    private async void ErrorReturn_Click(object sender, RoutedEventArgs e) => await ReturnFromErrorAsync();

    private async Task ReturnFromErrorAsync()
    {
        if (!startupFailed || execution != null || closed || runtime is not ProcessGameSession { CanReturnAfterError: true } session) return;
        RestoreErrorPresentation();
        await ExecuteAsync(async () =>
        {
            await session.ReturnAsync(execution!.Token);
            ready = true;
            renderedStory = null; renderedSettings = null; renderedVisuals = null;
            ShowGameplay();
        }, starting: true, operation: "Возврат к последнему корректному сегменту");
    }

    private void RestoreErrorPresentation()
    {
        startupFailed = false;
        StartupErrorOverlay.Visibility = Visibility.Collapsed;
        currentErrorReport = null;
        SceneArea.Visibility = Visibility.Visible;
        MenuOverlay.IsEnabled = true;
        GameControls.IsEnabled = true;
        CommandInput.Visibility = Visibility.Visible;
        StartButton.IsEnabled = true;
        SaveSelectedButton.IsEnabled = true;
        LoadSelectedButton.IsEnabled = SaveSlotsList.SelectedItem is GameSaveEntry { Damaged: false };
        DeleteSelectedButton.IsEnabled = SaveSlotsList.SelectedItem is GameSaveEntry;
        AnimatedBackdrop.Configure(project.Engine.Theme, preferences.Animate);
        renderTimer.Start();
    }

    private void ErrorMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (currentErrorReport == null) return;
        try
        {
            Clipboard.SetText(ErrorReportMarkdown.FormatDetails(currentErrorReport.Details));
            ErrorActionStatus.Text = "Подробности ошибки скопированы в буфер обмена в формате Markdown.";
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            ErrorActionStatus.Text = "Не удалось скопировать отчёт: " + error.Message;
        }
    }
}

/// <summary>Copies diagnostic text verbatim inside a Markdown fence that cannot be closed by the report.</summary>
public static class ErrorReportMarkdown
{
    public static string FormatDetails(string details)
    {
        ArgumentNullException.ThrowIfNull(details);
        int longest = 0, current = 0;
        foreach (char value in details)
        {
            current = value == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return "## Подробности ошибки\n\n" + fence + "text\n" + details + "\n" + fence + "\n";
    }
}
