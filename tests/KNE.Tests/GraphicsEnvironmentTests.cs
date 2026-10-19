using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class GraphicsEnvironmentTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void FramePacer_EnforcesBeforeAtAfterBoundariesWithoutAccumulatingSkippedFrames(int rate)
    {
        var gate = new FramePacer(); double period = 1d / rate;
        Assert.True(gate.ShouldRender(0, rate));
        Assert.False(gate.ShouldRender(period - .000001, rate));
        Assert.True(gate.ShouldRender(period, rate));
        Assert.False(gate.ShouldRender(period + .000001, rate));
        Assert.False(gate.ShouldRender(2 * period - .000001, rate));
        Assert.True(gate.ShouldRender(2 * period + .000001, rate));
        Assert.False(gate.ShouldRender(2 * period + .000002, rate));
    }

    [Fact]
    public void FramePacer_UnlimitedRateChangesClockRollbackAndResetRenderImmediately()
    {
        var gate = new FramePacer();
        Assert.True(gate.ShouldRender(10, 30)); Assert.False(gate.ShouldRender(10.01, 30));
        Assert.True(gate.ShouldRender(10.01, 144)); // A higher cap must use its new period immediately.
        Assert.True(gate.ShouldRender(10.01, 0)); Assert.True(gate.ShouldRender(10.01, 0));
        Assert.True(gate.ShouldRender(2, 30)); Assert.False(gate.ShouldRender(2.001, 30));
        gate.Reset(); Assert.True(gate.ShouldRender(2.001, 30));
    }

    [Fact]
    public void FramePacer_InvalidClockOrCapThrowsWithoutMovingTheLastAcceptedFrame()
    {
        var gate = new FramePacer(); Assert.True(gate.ShouldRender(1, 30));
        foreach (double clock in new[] { -1, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => gate.ShouldRender(clock, 30));
        foreach (int cap in new[] { -1, 1, 29, 31, 59, 61, 143, 145 })
            Assert.Throws<ArgumentOutOfRangeException>(() => gate.ShouldRender(2, cap));
        Assert.False(gate.ShouldRender(1.001, 30)); Assert.True(gate.ShouldRender(1.04, 30));
    }

    [Fact]
    public Task GraphicsPreferences_InheritPerWindowNormalizeInvalidFieldsAndAllowIndependentLayers() => WorkspaceTabsTests.Sta(() =>
    {
        var firstChild = new Border(); var secondChild = new Border();
        var first = new Window { Content = firstChild }; var second = new Window { Content = secondChild };
        try
        {
            GraphicsEnvironment.SetSettings(first, new GraphicsSettings { Api = "vulkan", VSync = false, MaxFps = 144, MsaaSamples = 4 });
            GraphicsEnvironment.SetSettings(second, new GraphicsSettings { Api = "opengl", MaxFps = 30, MsaaSamples = 2 });
            Assert.Equal("vulkan", GraphicsEnvironment.GetSettings(firstChild).Api);
            Assert.False(GraphicsEnvironment.GetSettings(firstChild).VSync);
            Assert.Equal(144, GraphicsEnvironment.GetSettings(firstChild).MaxFps);
            Assert.Equal(4, GraphicsEnvironment.GetSettings(firstChild).MsaaSamples);
            Assert.Equal("opengl", GraphicsEnvironment.GetSettings(secondChild).Api);
            GraphicsEnvironment.SetSettings(firstChild, new GraphicsSettings { Api = "broken", MaxFps = 31, MsaaSamples = 3, VSync = false });
            var normalized = GraphicsEnvironment.GetSettings(firstChild);
            Assert.Equal("auto", normalized.Api); Assert.Equal(60, normalized.MaxFps); Assert.Equal(0, normalized.MsaaSamples); Assert.False(normalized.VSync);
            Assert.Equal("vulkan", GraphicsEnvironment.GetSettings(first).Api);
            Assert.Equal(30, GraphicsEnvironment.GetSettings(secondChild).MaxFps);
        }
        finally { first.Close(); second.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task GraphicsPreferences_WindowConfigurationOverridesStaleChildDefaultsForNativePreparation() => WorkspaceTabsTests.Sta(() =>
    {
        var child = new Border(); var window = new Window { Content = child };
        try
        {
            GraphicsEnvironment.SetSettings(child, new GraphicsSettings { Api = "auto", MaxFps = 30, VSync = true });
            Assert.Equal("auto", GraphicsEnvironment.ResolveSettings(child).Api);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { Api = "opengl", MaxFps = 144, VSync = false, MsaaSamples = 4 });
            var effective = GraphicsEnvironment.ResolveSettings(child);
            Assert.Equal("opengl", effective.Api); Assert.Equal(144, effective.MaxFps); Assert.False(effective.VSync); Assert.Equal(4, effective.MsaaSamples);
            Assert.Equal("auto", GraphicsEnvironment.GetSettings(child).Api);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { Api = "vulkan", MaxFps = 60, MsaaSamples = 2 });
            Assert.Equal("vulkan", GraphicsEnvironment.ResolveSettings(child).Api);
            Assert.Equal(2, GraphicsEnvironment.ResolveSettings(child).MsaaSamples);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void GraphicsTelemetry_ReportsNativeDrawsBytesMaximumDurationAndFallbackErrorsWithoutInventingWpfDraws()
    {
        var stats = new GraphicsTelemetry();
        Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes); Assert.Equal(0, stats.RenderMilliseconds); Assert.Null(stats.Error);
        stats.Record(1, "vulkan", 3, 1024, 2.5, true);
        stats.Record(2, "vulkan", 5, 2048, 1.25, false);
        Assert.Equal("vulkan", stats.Backend); Assert.Equal(8, stats.DrawCalls); Assert.Equal(3072, stats.AllocatedBytes); Assert.Equal(2.5, stats.RenderMilliseconds);
        stats.Record(2, "WPF", 0, 0, 0, false, "Driver unavailable");
        Assert.Equal(3, stats.DrawCalls); Assert.Equal(1024, stats.AllocatedBytes); Assert.Equal("Driver unavailable", stats.Error);
        Assert.Contains("vulkan", stats.Backend); Assert.Contains("WPF", stats.Backend);
        stats.Remove(1); Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes); Assert.Equal(0, stats.RenderMilliseconds);
        stats.Remove(2); Assert.Null(stats.Error); Assert.Equal("", stats.Backend); Assert.Null(stats.DrawCalls);
    }

    [Fact]
    public Task GraphicsClock_UsesCompositionForVsyncAndTimerOtherwise_AndStopsWhenHiddenMinimizedUnloadedOrDisposed() => WorkspaceTabsTests.Sta(async () =>
    {
        var view = new Border();
        var window = new Window { Content = view, Width = 200, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        using var clock = new GraphicsFrameClock(view, _ => { });
        bool Subscribed() => (bool)typeof(GraphicsFrameClock).GetField("subscribed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        var timer = (DispatcherTimer)typeof(GraphicsFrameClock).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        bool animations = SystemParameters.ClientAreaAnimation;
        try
        {
            Assert.False(clock.Running);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { VSync = true, MaxFps = 30 });
            window.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); clock.Refresh();
            Assert.Equal(animations, clock.Running); Assert.Equal(animations, Subscribed()); Assert.False(timer.IsEnabled);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { VSync = false, MaxFps = 144 }); clock.Refresh();
            Assert.Equal(animations, clock.Running); Assert.False(Subscribed()); Assert.Equal(animations, timer.IsEnabled);
            if (animations) Assert.Equal(TimeSpan.FromMilliseconds(1000d / 144), timer.Interval);
            // Inherited preference changes must reconfigure an already running clock, without a manual Refresh.
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { VSync = false, MaxFps = 30 });
            if (animations) Assert.Equal(TimeSpan.FromMilliseconds(1000d / 30), timer.Interval);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { VSync = false, MaxFps = 0 });
            if (animations) Assert.Equal(TimeSpan.FromMilliseconds(1), timer.Interval);
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { VSync = true, MaxFps = 60 });
            Assert.Equal(animations, Subscribed()); Assert.False(timer.IsEnabled);
            view.Visibility = Visibility.Collapsed; Assert.False(clock.Running);
            view.Visibility = Visibility.Visible; clock.Refresh(); Assert.Equal(animations, clock.Running);
            window.WindowState = WindowState.Minimized; clock.Refresh(); Assert.False(clock.Running);
            window.WindowState = WindowState.Normal; clock.Refresh(); Assert.Equal(animations, clock.Running);
            window.Content = null; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); Assert.False(clock.Running);
            window.Content = view; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); clock.Refresh(); Assert.Equal(animations, clock.Running);
            clock.Dispose(); clock.Dispose(); Assert.False(clock.Running);
            clock.Refresh(); Assert.False(clock.Running);
            view.Visibility = Visibility.Collapsed; view.Visibility = Visibility.Visible; Assert.False(clock.Running);
        }
        finally { window.Close(); }
    });
}
