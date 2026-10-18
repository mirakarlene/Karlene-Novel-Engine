using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class EngineDiagnosticsTests
{
    [Fact]
    public void Trace_IsBoundedAndDurable_AndReportIncludesCauseStateOsProjectAndRecentOperations()
    {
        using var fixture = new TempProject();
        var clock = new FixedClock();
        var trace = new EngineDiagnostics(Path.Combine(fixture.Root, "diagnostics", "engine-test.json"), 123, 456, true, clock);
        trace.SetProject("Новелла", fixture.ProjectPath);
        for (int i = 0; i < 230; i++) trace.Record("Operation " + i, new string('Ж', 2000));
        var report = trace.Report(new InvalidOperationException("Outer", new IOException("Broken plugin file")), "UI exception");
        Assert.Equal(clock.GetUtcNow(), report.Time); Assert.Equal("Broken plugin file", report.Message);
        Assert.Contains("System.IO.IOException", report.Exception); Assert.Equal("System.IO.IOException", report.Component);
        Assert.Equal(200, report.Trace.Count); Assert.Equal("Operation 30", report.Trace[0].Operation); Assert.Equal("Operation 229", report.Trace[^1].Operation);
        Assert.Equal(1025, report.Trace[^1].Detail.Length); Assert.Contains("Новелла", report.Project);
        Assert.True(report.PluginsDisabled); Assert.Equal(123, report.ProcessId); Assert.NotEmpty(report.OperatingSystem); Assert.Contains(".NET", report.Runtime);
        Assert.Contains("Последние операции", File.ReadAllText(trace.ReportPath));
        var persisted = EngineDiagnostics.Read(trace.SessionPath);
        Assert.Equal("Broken plugin file", persisted.Failure!.Message); Assert.False(persisted.CleanExit);
        trace.Complete(); Assert.True(EngineDiagnostics.Read(trace.SessionPath).CleanExit);
        Assert.Null(EngineDiagnostics.RecordUnexpectedExit(trace.SessionPath, 1));
    }

    [Fact]
    public void UnhandledBackgroundFailure_IsRecoveredByObserver_WithoutLosingOriginalException()
    {
        using var fixture = new TempProject();
        var trace = new EngineDiagnostics(Path.Combine(fixture.Root, "background.json"), 10, 20, false);
        trace.Record("Выполнение плагина", "game/plugins/broken.cs");
        trace.Report(new InvalidOperationException("Exact background failure"), "Фоновый поток");
        var report = EngineDiagnostics.RecordUnexpectedExit(trace.SessionPath, unchecked((int)0xE0434352));
        Assert.Equal("Exact background failure", report!.Message);
        Assert.Equal("Фоновый поток", report.Kind);
        Assert.Equal("game/plugins/broken.cs", report.Trace[^1].Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watchdog_DoesNotConfuseReusedProcessIdentity_AndRepeatedErrorsKeepOriginalCause(bool firstClean)
    {
        using var fixture = new TempProject(); using var process = Process.GetCurrentProcess();
        var trace = new EngineDiagnostics(Path.Combine(fixture.Root, "identity.json"), process.Id, 1, false);
        int launches = 0;
        await EngineLaunch.WatchAsync(process.Id, 1, trace.SessionPath, _ => launches++);
        Assert.Equal(0, launches); Assert.Null(EngineDiagnostics.Read(trace.SessionPath).Failure);
        var original = trace.Report(new InvalidOperationException("first root cause"), "First");
        var repeated = trace.Report(new IOException("secondary while recovering"), "Second");
        Assert.Same(original, repeated); Assert.Equal("first root cause", EngineDiagnostics.Read(trace.SessionPath).Failure!.Message);
        Assert.Equal("Повторная ошибка", trace.Snapshot.Trace[^1].Operation);
        if (firstClean) trace.Complete();
        var observed = EngineDiagnostics.RecordUnexpectedExit(trace.SessionPath, 42);
        if (firstClean) Assert.Null(observed); else Assert.Equal("first root cause", observed!.Message);
    }

    [Theory]
    [InlineData(-1073741819, "обращение к памяти")]
    [InlineData(-1073741571, "Переполнение стека")]
    [InlineData(-1073740791, "FailFast")]
    [InlineData(42, "Точную причину")]
    public void UnexpectedExit_ExplainsNativeStatus_AndPreservesLastKnownOperation(int exitCode, string reason)
    {
        using var fixture = new TempProject();
        var trace = new EngineDiagnostics(Path.Combine(fixture.Root, "native.json"), 123, 456, false);
        trace.Record("C# plugin", "fault.cs");
        var report = EngineDiagnostics.RecordUnexpectedExit(trace.SessionPath, exitCode)!;
        Assert.Contains(reason, report.Message); Assert.Contains("0x", report.Message);
        Assert.Equal("C# plugin", report.Trace[^1].Operation); Assert.Equal("fault.cs", report.Trace[^1].Detail);
        Assert.Equal(report.Message, EngineDiagnostics.Read(trace.SessionPath).Failure!.Message);
    }

    [Fact]
    public void Trace_PersistenceFailureDoesNotReplaceOriginalFault_AndCorruptJournalIsRejected()
    {
        using var fixture = new TempProject();
        string blocked = fixture.Write("occupied", "file");
        var trace = new EngineDiagnostics(Path.Combine(blocked, "session.json"), 1, 2, false);
        Assert.NotNull(trace.PersistenceError);
        var report = trace.Report(new ArgumentException("original cause"), "test");
        Assert.Equal("original cause", report.Message); Assert.NotNull(trace.PersistenceError);
        string malformed = fixture.Write("corrupt.json", "{\"Trace\":null}");
        Assert.Throws<InvalidDataException>(() => EngineDiagnostics.Read(malformed));
    }

    [Fact]
    public void Restart_IsIndependentProcess_PreservesWorkingDirectoryAndExplicitlySelectsPluginMode()
    {
        var normal = EngineLaunch.RestartInfo(false); var safe = EngineLaunch.RestartInfo(true);
        Assert.False(normal.UseShellExecute); Assert.True(normal.CreateNoWindow);
        Assert.Equal("0", normal.Environment[EngineLaunch.PluginsEnvironment]); Assert.Equal("1", safe.Environment[EngineLaunch.PluginsEnvironment]);
        Assert.Contains("--disable-plugins", safe.ArgumentList); Assert.DoesNotContain("--disable-plugins", normal.ArgumentList);
        Assert.DoesNotContain(normal.ArgumentList, value => value.StartsWith("--engine-", StringComparison.Ordinal));
        Assert.Equal(Path.GetDirectoryName(typeof(App).Assembly.Location), normal.WorkingDirectory);
        var recovery = EngineLaunch.RecoveryInfo("report with spaces.json");
        Assert.Equal(ProcessWindowStyle.Normal, recovery.WindowStyle);
        Assert.Contains("--engine-recovery", recovery.ArgumentList); Assert.Contains("report with spaces.json", recovery.ArgumentList);
        var fallback = EngineLaunch.CreateStartInfo(Path.Combine("E:\\folder with spaces", "missing.dll"), new[] { "--engine-recovery", "report with spaces.json" });
        Assert.Equal("dotnet", fallback.FileName);
        Assert.Equal(new[] { "E:\\folder with spaces\\missing.dll", "--engine-recovery", "report with spaces.json" }, fallback.ArgumentList);
    }

    [Fact]
    public async Task Watchdog_ObservesRealProcessExit_AndOnlyLaunchesRecoveryForUncleanSession()
    {
        using var fixture = new TempProject();
        foreach (bool clean in new[] { false, true })
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            string release = Path.Combine(fixture.Root, "release-" + clean + ".txt");
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("while (-not (Test-Path -LiteralPath '" + release.Replace("'", "''") + "')) { Start-Sleep -Milliseconds 10 }; exit 42");
            using var process = Process.Start(info)!;
            var trace = new EngineDiagnostics(Path.Combine(fixture.Root, clean ? "clean.json" : "unclean.json"), process.Id, process.StartTime.ToUniversalTime().Ticks, false);
            if (clean) trace.Complete();
            ProcessStartInfo? launched = null;
            var observed = EngineLaunch.WatchAsync(process.Id, process.StartTime.ToUniversalTime().Ticks, trace.SessionPath, value => launched = value);
            File.WriteAllText(release, "ready");
            try { await observed.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            if (clean) Assert.Null(launched);
            else
            {
                Assert.NotNull(launched); Assert.Contains("--engine-recovery", launched.ArgumentList);
                Assert.Contains(trace.SessionPath, launched.ArgumentList); Assert.Contains("42", EngineDiagnostics.Read(trace.SessionPath).Failure!.Message);
            }
        }
    }

    [Fact]
    public Task RecoveryWindow_ExposesDetailsAndAllThreeActions_AndRestartFailureCanBeRetried() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var trace = new EngineDiagnostics(Path.Combine(fixture.Root, "window.json"), 1, 2, false);
        var report = trace.Report(new InvalidOperationException("Ошибка рендера"), "UI");
        foreach (var action in Enum.GetValues<EngineRecoveryAction>())
        {
            var calls = new List<EngineRecoveryAction>();
            var window = new EngineRecoveryWindow(report, trace.ReportPath, calls.Add);
            Assert.Contains("Ошибка рендера", window.ReportText.Text); Assert.Contains(trace.ReportPath, window.ReportText.Text);
            Button button = action switch { EngineRecoveryAction.Close => window.CloseEngineButton, EngineRecoveryAction.Restart => window.RestartEngineButton, _ => window.SafeRestartButton };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { action }, calls); window.Close(); Assert.Single(calls);
        }
        int attempts = 0;
        var retry = new EngineRecoveryWindow(report, trace.ReportPath, _ => { if (++attempts == 1) throw new IOException("Launch denied"); });
        retry.RestartEngineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains("Launch denied", retry.RecoveryStatus.Text);
        retry.SafeRestartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, attempts);
        retry.ShowInTaskbar = false; retry.ShowActivated = false; retry.WindowStartupLocation = WindowStartupLocation.Manual;
        retry.Left = retry.Top = -10000; retry.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        retry.Measure(new Size(960, 680)); retry.Arrange(new Rect(0, 0, 960, 680)); retry.UpdateLayout();
        string? output = Environment.GetEnvironmentVariable("KNE_ENGINE_RECOVERY_RENDER");
        if (output != null)
        {
            var bitmap = new RenderTargetBitmap(960, 680, 96, 96, PixelFormats.Pbgra32); bitmap.Render(retry);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(output); png.Save(file);
        }
        retry.Close(); await Task.CompletedTask;
    });

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 30, 0, TimeSpan.Zero);
    }
}
