using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class WindowTriggerInputTests
{
    [Fact]
    public Task SequenceRequiresSeparatePressesAndDoesNotContinueInAnotherScene() => WorkspaceTabsTests.Sta(() =>
    {
        var surface = new Grid();
        var window = HiddenWindow(surface);
        var signals = new List<(long Visit, TriggerSignal Signal)>();
        var trigger = new TriggerSettings { Kind = "sequence", Keys = "↑, ↑, B, A" };
        long visit = 1;
        var input = new WindowTriggerInput(window, surface, () => new(visit, [trigger]), (scope, signal) => signals.Add((scope, signal)));
        try
        {
            window.Show();
            var source = PresentationSource.FromVisual(window)!;
            Assert.False(Down(Key.Up));
            Assert.False(Down(Key.Up));
            Up(Key.Up);
            Press(Key.B);
            Press(Key.A);
            Assert.Empty(signals);

            Press(Key.Up);
            Press(Key.Up);
            Press(Key.B);
            visit++;
            Press(Key.A);
            Assert.Empty(signals);

            Press(Key.Up);
            Press(Key.Up);
            Press(Key.B);
            Assert.True(Down(Key.A));
            Up(Key.A);
            var completed = Assert.Single(signals);
            Assert.Equal(2, completed.Visit);
            Assert.True(trigger.Matches(completed.Signal));
            return Task.CompletedTask;

            bool Down(Key key) => input.KeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key));
            void Up(Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            void Press(Key key) { Down(key); Up(key); }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ResizingDeliversChangingEventsAndOneFinalEventAfterTheLastResize() => WorkspaceTabsTests.Sta(async () =>
    {
        var surface = new Grid();
        var window = HiddenWindow(surface);
        WindowTriggerScope? scope = null;
        var events = new List<TriggerSignal>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = new WindowTriggerInput(window, surface, () => scope, (_, signal) =>
        {
            events.Add(signal);
            if (signal.Button == "finished") finished.TrySetResult();
        });
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scope = new(1, [new() { Kind = "resize", Mode = "changing" }, new() { Kind = "resize", Mode = "finished" }]);
            window.Width = 530;
            window.UpdateLayout();
            window.Width = 560;
            window.UpdateLayout();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Contains(events, signal => signal.Button == "changing" && Math.Abs(signal.X - 530) <= 1);
            Assert.Contains(events, signal => signal.Button == "changing" && Math.Abs(signal.X - 560) <= 1);
            var settled = Assert.Single(events, signal => signal.Button == "finished");
            Assert.Equal(560, settled.X, 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task DoubleClickCancelsTheDeferredSingleAndASeparateClickStillRunsOnce() => WorkspaceTabsTests.Sta(async () =>
    {
        var surface = new Grid();
        var window = HiddenWindow(surface);
        var events = new List<TriggerSignal>();
        var singleDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new WindowTriggerScope(1, [new() { Kind = "hotspot" }, new() { Kind = "double_click" }]);
        var input = new WindowTriggerInput(window, surface, () => scope, (_, signal) =>
        {
            events.Add(signal);
            if (signal.Kind == "hotspot") singleDelivered.TrySetResult();
        });
        try
        {
            window.Show();
            var single = new TriggerSignal("hotspot", X: 20, Y: 20);
            Raise(Mouse.PreviewMouseDownEvent);
            Assert.True(input.DeferSingleClick(Raise(Mouse.PreviewMouseUpEvent), single));
            Assert.Empty(events);
            Assert.True(Raise(Mouse.PreviewMouseDownEvent).Handled);
            Assert.True(Raise(Mouse.PreviewMouseUpEvent).Handled);
            Assert.Equal("double_click", Assert.Single(events).Kind);

            Raise(Mouse.PreviewMouseDownEvent);
            Assert.True(input.DeferSingleClick(Raise(Mouse.PreviewMouseUpEvent), single));
            await singleDelivered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(new[] { "double_click", "hotspot" }, events.Select(signal => signal.Kind));

            MouseButtonEventArgs Raise(RoutedEvent route)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = route };
                window.RaiseEvent(args);
                return args;
            }
        }
        finally { window.Close(); }
    });

    private static Window HiddenWindow(Grid surface) => new()
    {
        Content = surface, Width = 510, Height = 440, ShowInTaskbar = false, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000
    };
}
