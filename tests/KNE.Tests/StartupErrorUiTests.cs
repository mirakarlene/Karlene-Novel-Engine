using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class StartupErrorUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateMain_ReplacesMainMenuWithError_AndReportsSourceLocations(bool sameFile)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", sameFile
            ? "function main() end\nfunction main() end"
            : "function main() end");
        if (!sameFile) fixture.Write("game/scenes/other.lua", "function main() end");
        var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                AssertErrorScreen(window);
                string message = Find<TextBox>(window, "StartupErrorMessage").Text;
                Assert.Contains("main", message);
                Assert.Contains("game/scenes/main.krl", message.Replace('\\', '/'));
                if (!sameFile) Assert.Contains("game/scenes/other.lua", message.Replace('\\', '/'));
                else { Assert.Contains(":1", message); Assert.Contains(":2", message); }
                Assert.Empty(Find<TextBlock>(window, "ConsoleOutput").Text);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingMain_ReplacesMainMenuWithErrorBeforeExecutingProjectCode(bool mainMenu)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function chapter() end");
        fixture.Write("game/scripts/gamelogic.lua", "error('INIT_MUST_NOT_RUN')");
        fixture.Write("engine/game.json", mainMenu ? "{\"main_menu\":true}" : "{\"main_menu\":false}");
        var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                AssertErrorScreen(window);
                Assert.Contains("main", Find<TextBox>(window, "StartupErrorMessage").Text);
                Assert.DoesNotContain("INIT_MUST_NOT_RUN", Find<TextBox>(window, "StartupErrorMessage").Text);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeStartupFailure_ReplacesGameWithPersistentError_WithoutSaving(bool initializerFails)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", initializerFails
            ? "function main() d(nil, 'should not appear') end"
            : "function main() error('STARTUP_FAILURE_MARKER') end");
        fixture.Write("game/scripts/gamelogic.lua", initializerFails ? "error('STARTUP_FAILURE_MARKER')" : "player_name = 'Игрок'");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                await InvokeTask(window, "StartGameAsync");
                AssertErrorScreen(window);
                Assert.Contains("STARTUP_FAILURE_MARKER", Find<TextBox>(window, "StartupErrorMessage").Text);
                Invoke(window, "RenderOutput");
                AssertErrorScreen(window);
                Assert.False(store.Exists("auto"));
                Assert.False(store.Exists("quick"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ValidMain_MenuDoesNotExecuteIt_AndStartRunsIt()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'MAIN_EXECUTED_ON_START') end");
        fixture.Write("game/scripts/gamelogic.lua", "print('INIT_EXECUTED_ON_START')");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Empty(Find<TextBlock>(window, "DialogueText").Text);
                Assert.Empty(Find<TextBlock>(window, "ConsoleOutput").Text);
                Assert.True(Find<Button>(window, "StartButton").IsEnabled);
                await InvokeTask(window, "StartGameAsync");
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Equal("MAIN_EXECUTED_ON_START", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Contains("INIT_EXECUTED_ON_START", Find<TextBlock>(window, "ConsoleOutput").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task StartupError_BlocksHotkeysMenuAndSaveActions_AndKeepsExitAvailable()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function chapter() end");
        var project = await fixture.LoadAsync();
        project.Engine.Game.DebugEnabled = true;
        await Sta(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("KNE error keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
            var store = new GameSaveStore(project.Path);
            var window = new GameWindow(project, store);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                string message = Find<TextBox>(window, "StartupErrorMessage").Text;
                foreach (Key key in new[] { Key.F3, Key.Escape, Key.Space, Key.F11 })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    window.RaiseEvent(args);
                    Assert.True(args.Handled);
                    AssertErrorScreen(window);
                }
                foreach (string name in new[] { "StartButton", "ResumeButton", "PauseButton", "SaveButton", "SaveSelectedButton" })
                    Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Invoke(window, "ShowMenu");
                Invoke(window, "ShowGameplay");
                Invoke(window, "GoBackScreen");
                await InvokeTask(window, "ShowSaveBrowserAsync");
                await InvokeTask(window, "StartGameAsync");
                Invoke(window, "RenderOutput");
                AssertErrorScreen(window);
                Assert.Equal(message, Find<TextBox>(window, "StartupErrorMessage").Text);
                Assert.False(store.Exists("auto")); Assert.False(store.Exists("quick"));
                Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "preferences.json")));
                Find<Button>(window, "StartupErrorExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(closed);
            }
            finally { if (!closed) window.Close(); }
        });
    }

    private static void AssertErrorScreen(GameWindow window)
    {
        Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "SceneArea").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<StackPanel>(window, "GameControls").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<Border>(window, "NoticePanel").Visibility);
        Assert.Empty(Find<Canvas>(window, "ScreenCanvas").Children);
        Assert.False(Find<Button>(window, "StartButton").IsEnabled);
        Assert.False(Find<Button>(window, "NextButton").IsEnabled);
        Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
        Assert.True(Find<Button>(window, "StartupErrorExitButton").IsEnabled);
        var backdrop = Find<AnimatedBackground>(window, "AnimatedBackdrop"); double phase = backdrop.Phase; backdrop.Step(.05); Assert.Equal(phase, backdrop.Phase);
        Assert.False(string.IsNullOrWhiteSpace(Find<TextBox>(window, "StartupErrorMessage").Text));
    }

    private static T Find<T>(GameWindow window, string name) => (T)window.FindName(name);
    private static void Invoke(GameWindow window, string method) => typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static Task InvokeTask(GameWindow window, string method) => (Task)typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;

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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
