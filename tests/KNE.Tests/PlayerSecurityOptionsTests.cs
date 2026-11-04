using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class PlayerSecurityOptionsTests
{
    [Theory]
    [InlineData(false, false, true, false)] [InlineData(true, false, true, true)]
    [InlineData(true, false, false, true)] [InlineData(false, true, true, true)]
    [InlineData(false, null, true, true)]
    public Task ReleaseConsoleFlagControlsF3WhileEditorAndTestBuildKeepTheirDebugAccess(bool releaseConsole, bool? testBuild, bool debugAtStartup, bool accessible) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { DebugMode = debugAtStartup, Preloading = false });
        fixture.Write("engine/security.json", JsonSerializer.Serialize(new ProjectSecuritySettings { ReleaseDebugConsole = releaseConsole }, EngineSettings.JsonOptions));
        var currentProperty = typeof(StandalonePlayer).GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = currentProperty.GetValue(null);
        GameWindow? window = null;
        try
        {
            currentProperty.SetValue(null, testBuild.HasValue ? new PlayerManifest(Guid.NewGuid().ToString("N"), "Game", "local", "portable", "", "", "1.0", TestBuild: testBuild.Value) : null);
            window = new GameWindow(await fixture.LoadAsync()); var console = WorkspaceTabsTests.Find<Border>(window, "DebugOverlay");
            var initial = console.Visibility;
            if (!accessible) Assert.Equal(Visibility.Collapsed, initial);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, new InputSource(), 0, Key.F3) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(GameWindow).GetMethod("Window_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, key]);
            Assert.Equal(accessible, key.Handled);
            Assert.Equal(accessible ? initial == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible : initial, console.Visibility);
        }
        finally { window?.Close(); currentProperty.SetValue(null, previous); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task CrashReportFlagControlsActualDiagnosticJsonCreationWithoutTerminatingThePlayer(bool enabled) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("engine/security.json", JsonSerializer.Serialize(new ProjectSecuritySettings { CrashReports = enabled }, EngineSettings.JsonOptions));
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots"));
        var window = new GameWindow(project, slots); string directory = Path.Combine(slots.DirectoryPath, "diagnostics");
        try
        {
            var error = new InvalidDataException("synthetic failure for report policy");
            typeof(GameWindow).GetMethod("ShowStartupError", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [error, "Policy test"]);
            Assert.Equal(Visibility.Visible, WorkspaceTabsTests.Find<Border>(window, "StartupErrorOverlay").Visibility);
            Assert.Contains(error.Message, WorkspaceTabsTests.Find<TextBox>(window, "StartupErrorMessage").Text);
            if (!enabled) { Assert.False(Directory.Exists(directory)); return; }
            var elapsed = Stopwatch.StartNew(); GameErrorReport? report = null;
            while (report == null && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                string? path = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "crash-*.json").SingleOrDefault() : null;
                if (path != null)
                    try { report = JsonSerializer.Deserialize<GameErrorReport>(await File.ReadAllTextAsync(path)); }
                    catch (Exception problem) when (problem is IOException or JsonException) { }
                if (report == null) await Task.Delay(10);
            }
            Assert.NotNull(report); Assert.Equal(error.Message, report.Message); Assert.Equal("Policy test", report.Operation);
            Assert.Equal(project.Path, report.ProjectPath); Assert.Equal(project.Title, report.ProjectTitle); Assert.Equal(nameof(InvalidDataException), report.ErrorType);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(@"\\no-network-probe\share\file.txt")]
    [InlineData(@"\\127.0.0.1\share with spaces\file.txt")]
    [InlineData(@"\\?\UNC\no-network-probe\share\file.txt")]
    public void NetworkDenyRejectsUncPathsBeforeAnyFilesystemProbe(string path)
    {
        using var fixture = new TempProject(); var policy = new ProjectSecuritySettings { AllowNetwork = false, FileSystem = "full" };
        var error = Assert.Throws<InvalidOperationException>(() => policy.ResolveFile(fixture.ProjectPath, path));
        Assert.Contains("Сетевые файловые пути запрещены", error.Message);
    }

    private sealed class InputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
