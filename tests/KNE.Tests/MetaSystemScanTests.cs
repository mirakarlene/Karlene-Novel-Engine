using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class MetaSystemScanTests
{
    [Fact]
    public async Task ProjectScanReportsInvalidSourceMissingSceneAndStaticAssetsWithoutExecutingTheProjectAndIncludesStatistics()
    {
        using var fixture = new TempProject(); string marker = Path.Combine(fixture.ProjectPath, "must-not-execute.txt");
        fixture.Write("game/scenes/main.krl", "local file=io.open('" + marker.Replace('\\', '/') + "','w'); file:write('executed'); file:close(); function main() background('missing-art.png'); goto_scene('missing_scene') end");
        fixture.Write("broken.krl", "function broken() local value=) end"); fixture.Write("broken.lua", "local value=)"); fixture.Write("broken.knegraph", "{broken graph");
        var project = await fixture.LoadAsync(); const string statistics = "FPS:42.5 · RAM:64 МиБ · логика:1.25мс · API:test";
        int count = ProjectLoader.EnumerateFiles(project.Path, default).Files.Count;
        string report = await ProjectSystemScan.RunAsync(project, statistics, default);
        Assert.Contains($"Файлов: {count}", report); Assert.Contains(statistics, report); Assert.Contains("Замечания:", report);
        foreach (string problem in new[] { "broken.krl", "broken.lua", "broken.knegraph", "missing_scene", "missing-art.png" }) Assert.Contains(problem, report);
        Assert.False(File.Exists(marker)); Assert.False(File.Exists(Path.Combine(project.Path, "engine", "frame-ids.json")));
    }

    [Fact]
    public async Task CleanProjectScanCountsScenesLinksAndStaticResourcesAndHonorsCancellation()
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main() background('art.png'); call_scene('chapter') end");
        fixture.Write("chapter.krl", "function chapter() d(nil,'chapter') end");
        File.WriteAllBytes(fixture.Write("art.png", ""), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aDQkAAAAASUVORK5CYII="));
        var project = await fixture.LoadAsync(); string report = await ProjectSystemScan.RunAsync(project, "statistics", default);
        Assert.Contains("Функций сцен: 2 · Связей: 1", report); Assert.Contains("Статических ссылок на ресурсы: 1 · Динамических переходов: 0", report);
        Assert.EndsWith("Замечаний по проверенным данным нет.", report);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectSystemScan.RunAsync(project, "statistics", canceled.Token));
    }

    [Theory]
    [InlineData("left")] [InlineData("right")] [InlineData("key")] [InlineData("timer")] [InlineData("cancel")]
    public Task FreezeOverlayBlocksPresentationUntilTheSelectedInputTimerOrCancellationAndAlwaysReleasesItsState(string mode) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); NamedListRuntimeTests.Code(fixture, "d(nil,'before'); d(nil,'after')");
        var project = await fixture.LoadAsync(); project.Engine.Theme.AnimationEnabled = false; project.Engine.Theme.Pattern = false;
        var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(project.Path, "saves")));
        using var cancellation = new CancellationTokenSource(); Task<string>? scan = null;
        try
        {
            await (Task)Method("StartGameAsync").Invoke(window, null)!;
            var root = (Grid)window.Content; int children = root.Children.Count;
            var dialogue = WorkspaceTabsTests.Find<TextBlock>(window, "DialogueText"); Assert.Equal("before", dialogue.Text);
            string resume = mode == "cancel" ? "left" : mode;
            var call = new SystemCall("freeze_and_scan", System.Text.Json.JsonSerializer.SerializeToElement(new object[] { resume, .1 }));
            scan = (Task<string>)Method("ScanProjectAsync").Invoke(window, [call, cancellation.Token])!;
            Assert.True(Field<bool>(window, "systemFrozen")); Assert.Equal(children + 1, root.Children.Count);
            var overlay = Assert.IsType<Border>(root.Children[root.Children.Count - 1]); Assert.Equal(int.MaxValue, Panel.GetZIndex(overlay));
            var report = Assert.IsType<TextBlock>(Assert.IsType<ScrollViewer>(overlay.Child).Content);
            Method("RenderOutput").Invoke(window, null); Assert.Equal("before", dialogue.Text);
            await Until(() => Field<Action<string>?>(window, "scanInput") != null || scan.IsCompleted);
            if (mode == "cancel") { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan); }
            else
            {
                if (mode != "timer")
                {
                    var wrong = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent };
                    overlay.RaiseEvent(wrong); Assert.True(wrong.Handled); Assert.False(scan.IsCompleted); Assert.True(Field<bool>(window, "systemFrozen"));
                    if (mode == "key")
                    {
                        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        window.RaiseEvent(key); Assert.True(key.Handled);
                    }
                    else overlay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, mode == "left" ? MouseButton.Left : MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                }
                string result = await scan.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Contains("Проверка проекта", result); Assert.Contains(result, report.Text);
            }
            Assert.False(Field<bool>(window, "systemFrozen")); Assert.Null(Field<Action<string>?>(window, "scanInput")); Assert.Equal(children, root.Children.Count); Assert.DoesNotContain(overlay, root.Children.Cast<UIElement>());
            Assert.Equal("before", dialogue.Text);
        }
        finally
        {
            cancellation.Cancel();
            if (scan != null) { try { await scan; } catch (OperationCanceledException) { } }
            window.Close();
        }
    });

    [Fact]
    public Task WindowSystemCommandsApplyRequestedModesIdempotentlyWithoutShowingTheWindow() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Theme.AnimationEnabled = false; project.Engine.Theme.Pattern = false;
        var store = new GameSaveStore(project.Path, Path.Combine(project.Path, "saves")); var window = new GameWindow(project, store);
        typeof(GameWindow).GetField("systemServices", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new GameSystemServices(project, store));
        async Task Invoke(string operation)
        {
            var call = new SystemCall(operation, System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<object>()));
            var reply = await (Task<SystemReply>)Method("InvokeSystemAsync").Invoke(window, [call, CancellationToken.None])!;
            Assert.Null(reply.Error); Assert.True(reply.Value.GetBoolean()); Assert.False(window.IsVisible);
        }
        try
        {
            Assert.Equal(WindowState.Normal, window.WindowState);
            await Invoke("minimize_game"); Assert.Equal(WindowState.Minimized, window.WindowState);
            window.WindowState = WindowState.Normal;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await Invoke("fullscreen_game"); Assert.True(Field<bool>(window, "fullscreen")); Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(WindowStyle.None, window.WindowStyle); Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
            }
            for (int repeat = 0; repeat < 2; repeat++)
            {
                await Invoke("windowed_game"); Assert.False(Field<bool>(window, "fullscreen")); Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle); Assert.Equal(project.Engine.Window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize, window.ResizeMode);
            }
        }
        finally { window.Close(); }
    });

    static MethodInfo Method(string name) => typeof(GameWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    static async Task Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The project scan did not reach its input stage."); await Task.Delay(5); }
    }
    sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
