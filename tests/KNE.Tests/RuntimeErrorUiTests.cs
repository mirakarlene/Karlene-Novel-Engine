using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class RuntimeErrorUiTests
{
    [Fact]
    public async Task ChildFunctionFailure_ReplacesSameWindowWithDetailedError_AndPreservesAutosave()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Before child'); call(child) end");
        fixture.Write("game/scenes/child.lua", "function child()\n local value = 1\n error('CHILD_FAILURE_MARKER')\nend");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            object content = window.Content;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                await Start(window);
                Assert.Equal("Before child", Find<TextBlock>(window, "DialogueText").Text);
                byte[] original = await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, "auto.json"));
                await ErrorAction(window, () => Click(window, "NextButton"));
                Assert.Same(content, window.Content);
                Assert.False(closed);
                AssertDetailedError(window, project, "game/scenes/child.lua", "CHILD_FAILURE_MARKER", 3);
                Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, "auto.json")));
                string report = Find<TextBox>(window, "StartupErrorMessage").Text;
                Invoke(window, "RenderOutput"); Invoke(window, "ShowMenu"); Invoke(window, "ShowGameplay");
                await InvokeTask(window, "StartGameAsync");
                Assert.Equal(report, Find<TextBox>(window, "StartupErrorMessage").Text);
                AssertTerminal(window);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ChoiceBranchFailure_ReplacesGameplayWithDetailedError_WithoutSavingBrokenBranch()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n local answer = choice('Path', {'Safe', 'Broken'})\n if answer == 2 then error('CHOICE_FAILURE_MARKER') end\nend");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            try
            {
                await Start(window);
                var panel = Find<StackPanel>(window, "ChoicePanel");
                Assert.Equal(2, panel.Children.Count);
                byte[] original = await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, "auto.json"));
                await ErrorAction(window, () => ((Button)panel.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                AssertDetailedError(window, project, "game/scenes/main.krl", "CHOICE_FAILURE_MARKER", 3);
                Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(store.DirectoryPath, "auto.json")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task DebugCommandFailure_ReplacesGameplayWithDetailedError_AndCannotResumeFromHotkeys()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Waiting') end");
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(cmd)\n error('DEBUG_FAILURE_MARKER')\nend");
        var project = await fixture.LoadAsync(); project.Engine.Game.DebugEnabled = true;
        await Sta(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("KNE runtime error keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
            var window = new GameWindow(project);
            try
            {
                await Start(window);
                Find<Border>(window, "DebugOverlay").Visibility = Visibility.Visible;
                Find<TextBox>(window, "CommandInput").Text = "break";
                await ErrorAction(window, () => Find<TextBox>(window, "CommandInput").RaiseEvent(
                    new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent }));
                AssertDetailedError(window, project, "game/scripts/gamelogic.lua", "DEBUG_FAILURE_MARKER", 2);
                foreach (Key key in new[] { Key.F3, Key.Escape, Key.Space, Key.F11 })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    window.RaiseEvent(args); Assert.True(args.Handled); AssertTerminal(window);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task StartupSyntaxError_ReplacesMenuWithDetailedError_BeforeAnyAutosave()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n local broken = )\nend");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                if (Find<Border>(window, "StartupErrorOverlay").Visibility != Visibility.Visible)
                    await InvokeTask(window, "StartGameAsync");
                AssertDetailedError(window, project, "game/scenes/main.krl", "unexpected symbol", 2);
                Assert.False(store.Exists("auto"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ScriptFailureDuringRestore_ReplacesGameplayWithError_AndPreservesSavedCheckpoint()
    {
        using var fixture = new TempProject();
        string flag = Path.Combine(fixture.Root, "restore.flag").Replace('\\', '/');
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                local flag = io.open('{{flag}}', 'r')
                if flag then flag:close(); error('RESTORE_FAILURE_MARKER') end
                d(nil, 'Saved dialogue')
            end
            """);
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            try
            {
                await Start(window);
                byte[] original = await File.ReadAllBytesAsync(store.SlotPath("auto"));
                await File.WriteAllTextAsync(flag, "fail restore");
                await InvokeTask(window, "LoadGameAsync", "auto");
                AssertDetailedError(window, project, "game/scenes/main.krl", "RESTORE_FAILURE_MARKER", 3);
                Assert.Equal(original, await File.ReadAllBytesAsync(store.SlotPath("auto")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task MalformedSave_StaysRecoverableNotice_AndKeepsPlayingInSameWindow()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'First'); d(nil, 'Second') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            try
            {
                await Start(window);
                await File.WriteAllTextAsync(Path.Combine(store.DirectoryPath, "quick.json"), "broken json");
                await InvokeTask(window, "LoadGameAsync", "quick");
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "NoticePanel").Visibility);
                Assert.True(Find<Button>(window, "NextButton").IsEnabled);
                Assert.Equal("First", Find<TextBlock>(window, "DialogueText").Text);
                await ReadyAction(window, () => Click(window, "NextButton"));
                Assert.Equal("Second", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    private static void AssertDetailedError(GameWindow window, ProjectInfo project, string file, string marker, int line)
    {
        AssertTerminal(window);
        string report = Find<TextBox>(window, "StartupErrorMessage").Text.Replace('\\', '/');
        Assert.Contains(project.Title, report);
        Assert.Contains(project.Path.Replace('\\', '/'), report);
        Assert.Contains(file, report);
        Assert.Contains(marker, report);
        Assert.Contains(RuntimeInformation.OSDescription, report);
        Assert.Contains(Environment.MachineName, report);
        Assert.Contains("Строка: " + line, report);
        Assert.Contains("Название проекта: " + project.Name, report);
        Assert.Contains("Тип ошибки:", report);
        Assert.Contains("Подробности:", report);
        Assert.Matches(@"Когда: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}", report);
    }

    private static void AssertTerminal(GameWindow window)
    {
        Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "SceneArea").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<StackPanel>(window, "GameControls").Visibility);
        Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
        Assert.False(Find<StackPanel>(window, "ChoicePanel").IsEnabled);
        foreach (string button in new[] { "NextButton", "StartButton", "ContinueButton", "ResumeButton", "PauseButton", "SaveButton", "LoadButton", "LoadAutoButton", "SaveSelectedButton", "LoadSelectedButton", "DeleteSelectedButton" })
            Assert.False(Find<Button>(window, button).IsEnabled, button);
        Assert.True(Find<Button>(window, "StartupErrorExitButton").IsEnabled);
        Assert.Empty(Find<Canvas>(window, "ScreenCanvas").Children);
    }

    private static async Task Start(GameWindow window)
    {
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await InvokeTask(window, "StartGameAsync");
        Assert.True(Find<TextBox>(window, "CommandInput").IsEnabled);
    }

    private static async Task ErrorAction(GameWindow window, Action action)
    {
        var overlay = Find<Border>(window, "StartupErrorOverlay");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border));
        EventHandler handler = (_, _) => { if (overlay.Visibility == Visibility.Visible) completion.TrySetResult(); };
        descriptor.AddValueChanged(overlay, handler);
        try { action(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { descriptor.RemoveValueChanged(overlay, handler); }
    }

    private static async Task ReadyAction(GameWindow window, Action action)
    {
        var input = Find<TextBox>(window, "CommandInput");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DependencyPropertyChangedEventHandler handler = (_, e) => { if ((bool)e.NewValue) completion.TrySetResult(); };
        input.IsEnabledChanged += handler;
        try { action(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { input.IsEnabledChanged -= handler; }
    }

    private static T Find<T>(GameWindow window, string name) => (T)window.FindName(name);
    private static void Click(GameWindow window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Invoke(GameWindow window, string method) => typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static Task InvokeTask(GameWindow window, string method, params object[] arguments) =>
        (Task)typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments)!;

    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
