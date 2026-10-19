using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class ErrorActionsUiTests
{
    [Fact]
    public async Task TransitionFailure_ReturnRestoresLastSafeSegment_WithoutOverwritingAutosave_AndCanFailAgain()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.score=7; d(nil, 'Safe segment'); call(child) end");
        fixture.Write("game/scenes/child.lua", "function child() state.score=99; error('BROKEN_TRANSITION') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            object content = window.Content;
            try
            {
                await Start(window);
                byte[] saved = await File.ReadAllBytesAsync(store.SlotPath("auto"));
                await FailAdvance(window);
                Assert.True(Find<Button>(window, "ErrorReturnButton").IsEnabled);
                await ClickReady(window, "ErrorReturnButton");
                Assert.Same(content, window.Content);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Grid>(window, "SceneArea").Visibility);
                Assert.Equal("Safe segment", Find<TextBlock>(window, "DialogueText").Text);
                Assert.True(Find<Button>(window, "NextButton").IsEnabled);
                var runtime = (ProcessGameSession)typeof(GameWindow).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Equal(7, runtime.Variables.GetProperty("Globals").GetProperty("state").GetProperty("score").GetInt32());
                Assert.Equal(saved, await File.ReadAllBytesAsync(store.SlotPath("auto")));
                await FailAdvance(window);
                Assert.True(Find<Button>(window, "ErrorReturnButton").IsEnabled);
                Assert.Contains("BROKEN_TRANSITION", Find<TextBox>(window, "StartupErrorMessage").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RestartAfterTransitionError_StartsMainFromBeginning_WithConsoleAndChangingVariables()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() chapter='fresh'; state.score=1; d(nil, 'Opening'); chapter='later'; state.score=2; d(nil, 'Second'); call(child) end");
        fixture.Write("game/scenes/child.lua", "function child() error('RESTART_FAILURE') end");
        var project = await fixture.LoadAsync();
        project.Engine.Game.DebugEnabled = false;
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            object content = window.Content;
            try
            {
                await Start(window);
                await Advance(window);
                await FailAdvance(window);
                Assert.True(Find<Button>(window, "ErrorRestartButton").IsEnabled);
                await ClickReady(window, "ErrorRestartButton");
                Assert.Same(content, window.Content);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DebugOverlay").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Equal("Opening", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal("fresh", Globals(window).GetProperty("chapter").GetString());
                Assert.DoesNotContain("later", Find<TextBox>(window, "VariablesOutput").Text);
                Assert.True(Find<TextBox>(window, "CommandInput").IsEnabled);
                await Advance(window);
                Assert.Equal("Second", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal("later", Globals(window).GetProperty("chapter").GetString());
                Assert.Equal(2, Globals(window).GetProperty("state").GetProperty("score").GetInt32());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RestartConsole_ReceivesVariableSnapshotsWhileStartupIsStillRunning()
    {
        using var fixture = new TempProject();
        string flag = Path.Combine(fixture.Root, "restart.flag").Replace('\\', '/');
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                local flag = io.open('{{flag}}', 'r')
                if not flag then d(nil, 'Before restart'); error('NEEDS_RESTART') end
                flag:close()
                phase='LIVE_STARTUP'
                tick=0
                local began=os.clock()
                while os.clock()-began<1.2 do tick=tick+1 end
                phase='READY'
                d(nil, 'After busy restart')
            end
            """);
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await Start(window);
                await FailAdvance(window);
                await File.WriteAllTextAsync(flag, "restart");
                bool observedDuringStartup = false;
                var variables = Find<TextBox>(window, "VariablesOutput");
                TextChangedEventHandler changed = (_, _) =>
                {
                    if (variables.Text.Contains("LIVE_STARTUP", StringComparison.Ordinal)
                        && !Find<TextBox>(window, "CommandInput").IsEnabled)
                        observedDuringStartup = true;
                };
                variables.TextChanged += changed;
                try { await InvokeTask(window, "RestartFromErrorAsync"); }
                finally { variables.TextChanged -= changed; }
                Assert.True(observedDuringStartup);
                Assert.Equal("READY", Globals(window).GetProperty("phase").GetString());
                Assert.True(Globals(window).GetProperty("tick").GetInt64() > 0);
                Assert.Equal("After busy restart", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DebugOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task FailedRestart_KeepsDetailedErrorAndConsole_AndAllowsAnotherRestartAfterCorrection()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() startup_marker='captured again'; error('REPEATED_STARTUP_ERROR') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await InvokeTask(window, "StartGameAsync");
                await InvokeTask(window, "RestartFromErrorAsync");
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DebugOverlay").Visibility);
                Assert.Contains("REPEATED_STARTUP_ERROR", Find<TextBox>(window, "StartupErrorMessage").Text);
                Assert.Equal("captured again", Globals(window).GetProperty("startup_marker").GetString());
                Assert.True(Find<Button>(window, "ErrorRestartButton").IsEnabled);
                Assert.False(Find<Button>(window, "ErrorReturnButton").IsEnabled);
                Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
                fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Finally corrected') end");
                await InvokeTask(window, "RestartFromErrorAsync");
                Assert.Equal("Finally corrected", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.True(Find<Button>(window, "NextButton").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task OrdinaryScriptFailure_ReturnIsDisabled_ConsoleShowsLogsAndFailedVariables_WithoutResumingGame()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Safe'); error_flag='captured'; print('LOG_BEFORE_FAILURE'); error('DIRECT_FAILURE') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await Start(window);
                await FailAdvance(window);
                Assert.False(Find<Button>(window, "ErrorReturnButton").IsEnabled);
                var enqueue = typeof(GameWindow).GetMethod("QueueOutput", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int index = 0; index < 350; index++) enqueue.Invoke(window, [$"TERMINAL_LOG_{index}"]);
                ExportErrorView(window, "error-actions.png", 1280, 720);
                Click(window, "ErrorConsoleButton");
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DebugOverlay").Visibility);
                Assert.Contains("LOG_BEFORE_FAILURE", Find<TextBlock>(window, "ConsoleOutput").Text);
                Assert.Contains("TERMINAL_LOG_349", Find<TextBlock>(window, "ConsoleOutput").Text);
                Assert.Equal("captured", Globals(window).GetProperty("error_flag").GetString());
                Assert.Contains("Игрок", Find<TextBox>(window, "VariablesOutput").Text);
                Assert.Contains("<function>", Find<TextBox>(window, "VariablesOutput").Text);
                Assert.DoesNotContain("\\u0418", Find<TextBox>(window, "VariablesOutput").Text);
                Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
                Assert.Equal(Visibility.Collapsed, Find<TextBox>(window, "CommandInput").Visibility);
                Assert.False(Find<Button>(window, "NextButton").IsEnabled);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
                ExportErrorView(window, "error-console.png", 1280, 720);
                ExportErrorView(window, "error-console-minimum.png", 640, 480);
                await InvokeTask(window, "ReturnFromErrorAsync");
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Click(window, "ErrorConsoleButton");
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task StartupSyntaxFailure_HasNoReturn_RestartUsesCorrectedFile_AndStartsConsole()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() local broken = ) end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                if (Find<Border>(window, "StartupErrorOverlay").Visibility != Visibility.Visible)
                    await InvokeTask(window, "StartGameAsync");
                Assert.False(Find<Button>(window, "ErrorReturnButton").IsEnabled);
                fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Corrected main') end");
                await InvokeTask(window, "RestartFromErrorAsync");
                Assert.Equal("Corrected main", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DebugOverlay").Visibility);
                Assert.True(Find<Button>(window, "NextButton").IsEnabled);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task MarkdownButton_CopiesOnlyDetailsToClipboard_InSafeMarkdownFence()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Safe'); error('MARKDOWN_FAILURE') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var original = Clipboard.GetDataObject();
            var window = new GameWindow(project);
            try
            {
                await Start(window);
                await FailAdvance(window);
                string report = Find<TextBox>(window, "StartupErrorMessage").Text;
                string details = report[(report.IndexOf("Подробности:", StringComparison.Ordinal) + "Подробности:".Length)..].TrimStart('\r', '\n');
                Click(window, "ErrorMarkdownButton");
                Assert.Equal(ErrorReportMarkdown.FormatDetails(details), Clipboard.GetText());
                Assert.Contains("MARKDOWN_FAILURE", Clipboard.GetText());
                Assert.DoesNotContain("Название проекта:", Clipboard.GetText());
                Assert.Contains("скопированы", Find<TextBlock>(window, "ErrorActionStatus").Text);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
            }
            finally
            {
                window.Close();
                if (original != null) Clipboard.SetDataObject(original, true); else Clipboard.Clear();
            }
        });
    }

    [Theory]
    [InlineData("", "```")]
    [InlineData("plain error", "```")]
    [InlineData("line one\n```text\n```\nline last", "````")]
    [InlineData("error with ````` and `", "``````")]
    public void MarkdownDetails_PreservesExactContent_AndUsesFenceLongerThanEmbeddedBackticks(string details, string fence)
    {
        Assert.Equal("## Подробности ошибки\n\n" + fence + "text\n" + details + "\n" + fence + "\n", ErrorReportMarkdown.FormatDetails(details));
    }

    private static async Task Start(GameWindow window)
    {
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await InvokeTask(window, "StartGameAsync");
        Assert.True(Find<TextBox>(window, "CommandInput").IsEnabled);
    }

    private static Task Advance(GameWindow window) => ClickReady(window, "NextButton");

    private static async Task ClickReady(GameWindow window, string button)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = Find<TextBox>(window, "CommandInput");
        DependencyPropertyChangedEventHandler handler = (_, e) => { if ((bool)e.NewValue) completion.TrySetResult(); };
        input.IsEnabledChanged += handler;
        try { Click(window, button); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { input.IsEnabledChanged -= handler; }
    }

    private static async Task FailAdvance(GameWindow window)
    {
        var overlay = Find<Border>(window, "StartupErrorOverlay");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border));
        EventHandler handler = (_, _) => { if (overlay.Visibility == Visibility.Visible) completion.TrySetResult(); };
        descriptor.AddValueChanged(overlay, handler);
        try { Click(window, "NextButton"); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { descriptor.RemoveValueChanged(overlay, handler); }
    }

    private static T Find<T>(GameWindow window, string name) => (T)window.FindName(name);
    private static JsonElement Globals(GameWindow window) => JsonSerializer.Deserialize<JsonElement>(Find<TextBox>(window, "VariablesOutput").Text).GetProperty("Globals");
    private static void Click(GameWindow window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task InvokeTask(GameWindow window, string method) =>
        (Task)typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;

    private static void ExportErrorView(GameWindow window, string filename, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, filename));
        encoder.Save(stream);
    }

    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
