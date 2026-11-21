using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Xunit;

namespace KNE.Tests;

public sealed class StartupMediaTests
{
    [Theory]
    [InlineData(ProjectAuthoringMode.Code)]
    [InlineData(ProjectAuthoringMode.Visual)]
    public async Task StockDiagnosticsReportsBeforeAnyProjectScriptsRun(ProjectAuthoringMode mode)
    {
        using var fixture = new TempProject();
        string path = ProjectCreator.CreateProject("Отчёт при открытии", fixture.Root, mode,
            new ProjectCreationOptions { Width = 1280, Height = 720 });
        File.WriteAllText(Path.Combine(path, "custom.lua"), "init(-1) error('must not run during diagnostics')");
        var project = await ProjectLoader.LoadProjectAsync(path);
        var lines = new List<string>();
        LuaRuntime.ReportStockProjectOpening(project, GameScripts.Load(project), lines.Add, CancellationToken.None);
        Assert.Equal("Диагностика KNE — Отчёт при открытии", lines.FirstOrDefault());
        Assert.Contains(lines, line => line.StartsWith("Сценариев проверено:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Ошибка целостности", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthoringCommandsReachWorkerHostInOrderAndContinueAfterPlayback()
    {
        using var fixture = new TempProject();
        var video = new ScriptNode
        {
            Kind = ScriptNodeKind.ShowVideo,
            SystemArguments = new() { ["video_path"] = new("string", "game/intro.mp4"), ["video_mode"] = new("string", "area") }
        };
        Assert.Equal("visual", BlockLibrary.Definitions.Single(item => item.Kind == video.Kind).Group);
        string code = SystemBlocks.Compile(video);
        Assert.Equal("show_video(\"game/intro.mp4\", 0, 0, 640, 360)", code);
        fixture.Write("game/scenes/main.krl",
            "function main() enable_console(); " + code + "; global_pause(0.5); disable_console(); d(nil, 'finished') end");
        var requested = new List<string>();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { })
        {
            SystemHost = async (call, token) =>
            {
                requested.Add(call.Operation);
                if (call.Operation == "show_video")
                {
                    playing.TrySetResult();
                    await ended.Task.WaitAsync(token);
                }
                return SystemReply.From(true);
            }
        };
        var start = session.StartAsync(timeout.Token);
        await playing.Task.WaitAsync(timeout.Token);
        Assert.Equal(new[] { "enable_console", "show_video" }, requested);
        Assert.False(start.IsCompleted);
        ended.SetResult();
        await start;
        Assert.Equal(new[] { "enable_console", "show_video", "global_pause", "disable_console" }, requested);
        Assert.Equal("finished", session.Story.Text);
    }

    [Fact]
    public void VideoRegionUsesProjectCoordinatesAndRejectsExternalAndInvalidPaths()
    {
        using var fixture = new TempProject();
        string video = fixture.Write("game/intro.mp4", "placeholder");
        SystemCall Call(params object[] args) => new("show_video", JsonSerializer.SerializeToElement(args));
        Assert.Equal((video, (Rect?)null, ((Key, ModifierKeys)?)(Key.Escape, ModifierKeys.None)), GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("game/intro.mp4")));
        Assert.Equal((video, (Rect?)new Rect(30, 40, 640, 360), ((Key, ModifierKeys)?)(Key.Space, ModifierKeys.None)), GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("game/intro.mp4", 30, 40, 640, 360, "Space")));
        Assert.Null(GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("game/intro.mp4", false)).Skip);
        Assert.Throws<ArgumentException>(() => GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("game/intro.mp4", 641, 0, 640, 360)));
        Assert.Throws<ArgumentException>(() => GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("../outside.mp4")));
        Assert.Throws<FileNotFoundException>(() => GameWindow.ReadVideoRequest(fixture.ProjectPath, Call("game/missing.mp4")));
        Assert.Equal(-1, GameWindow.ReadPauseDuration(new("global_pause", JsonSerializer.SerializeToElement(new[] { -1 }))));
        Assert.Throws<ArgumentException>(() => GameWindow.ReadPauseDuration(new("global_pause", JsonSerializer.SerializeToElement(new[] { -2 }))));
    }

    [Fact]
    public Task TestPauseShortcutCanAttachAndReleaseOnTheGameDispatcher() => WorkspaceTabsTests.Sta(() =>
    {
        var window = new Window();
        try
        {
            nint handle = new WindowInteropHelper(window).EnsureHandle();
            var shortcut = new TestPauseShortcut(handle, () => { });
            Assert.False(shortcut.IsInvalid);
            shortcut.Dispose();
            Assert.True(shortcut.IsClosed);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [NativeVideoFact]
    public Task NativeMp4PlaysToCompletionInTheGameWindow() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        File.Copy(Environment.GetEnvironmentVariable("KNE_VIDEO_SMOKE")!, fixture.Write("game/intro.mp4", ""), overwrite: true);
        fixture.Write("game/scenes/main.krl", "function main() show_video('game/intro.mp4', 30, 40, 640, 360, false); d(nil, 'after video') end");
        fixture.Write("engine/game.json", """{"main_menu":false}""");
        var window = new GameWindow(await fixture.LoadAsync())
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            var dialogue = (TextBlock)window.FindName("DialogueText");
            var error = (TextBox)window.FindName("StartupErrorMessage");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (dialogue.Text != "after video")
            {
                Assert.True(((Border)window.FindName("StartupErrorOverlay")).Visibility != Visibility.Visible, error.Text);
                await Task.Delay(40, timeout.Token);
            }
            Assert.DoesNotContain(((Grid)window.Content).Children.OfType<Border>(), border => border.Child is Viewbox box &&
                box.Child is Canvas canvas && canvas.Children.OfType<Border>().Any(item => item.Child is MediaElement));
        }
        finally { window.Close(); }
    });
}

internal sealed class NativeVideoFactAttribute : FactAttribute
{
    public NativeVideoFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("KNE_VIDEO_SMOKE"))) Skip = "Opt-in: set KNE_VIDEO_SMOKE to a short local MP4 file.";
    }
}
