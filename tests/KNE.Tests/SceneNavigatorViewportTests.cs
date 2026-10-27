using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class SceneNavigatorViewportTests
{
    [Theory]
    [InlineData(1500, 950)]
    [InlineData(1100, 700)]
    public Task PanLargeGraph(int width, int height) => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(width, height);
        host.View.ZoomWithWheel(120, ModifierKeys.Control, new(200, 150));
        host.Scroll(2000, 1200);
        var before = host.Graph.TranslatePoint(new Point(), host.Viewport);
        host.View.BeginPan(new(250, 180)); Assert.Equal(1, host.CaptureRequests);
        host.View.PanTo(new(210, 150)); host.Layout();
        Assert.Equal(2040, host.Viewport.HorizontalOffset, 5); Assert.Equal(1230, host.Viewport.VerticalOffset, 5);
        var after = host.Graph.TranslatePoint(new Point(), host.Viewport);
        Assert.Equal(before.X - 40, after.X, 5); Assert.Equal(before.Y - 30, after.Y, 5);
        host.View.PanTo(new(170, 100)); host.Layout();
        Assert.Equal(2080, host.Viewport.HorizontalOffset, 5); Assert.Equal(1280, host.Viewport.VerticalOffset, 5);
        host.Viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
        Assert.Null(host.Viewport.Cursor);
        host.View.PanTo(new(100, 50)); host.Layout();
        Assert.Equal(2080, host.Viewport.HorizontalOffset, 5); Assert.Equal(1280, host.Viewport.VerticalOffset, 5);
        host.View.BeginPan(new(200, 150)); host.Viewport.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        host.View.PanTo(new(100, 50)); host.Layout();
        Assert.Null(host.Viewport.Cursor); Assert.Equal(2080, host.Viewport.HorizontalOffset, 5); Assert.Equal(1280, host.Viewport.VerticalOffset, 5);
        host.View.BeginPan(new()); host.View.PanTo(new(100000, 100000)); host.Layout();
        Assert.Equal(0, host.Viewport.HorizontalOffset); Assert.Equal(0, host.Viewport.VerticalOffset);
        host.View.PanTo(new(-100000, -100000)); host.Layout();
        Assert.Equal(host.Viewport.ScrollableWidth, host.Viewport.HorizontalOffset); Assert.Equal(host.Viewport.ScrollableHeight, host.Viewport.VerticalOffset);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FailedCaptureDoesNotStartDrag() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700, capture: false); host.Scroll(2000, 1200);
        host.View.BeginPan(new(200, 150)); host.View.PanTo(new(100, 50)); host.Layout();
        Assert.Equal(1, host.CaptureRequests); Assert.Null(host.Viewport.Cursor);
        Assert.Equal(2000, host.Viewport.HorizontalOffset); Assert.Equal(1200, host.Viewport.VerticalOffset);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RightDragLifecycle() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700);
        var card = host.Graph.Children.OfType<Border>().First(); int opened = 0;
        host.View.SceneRequested += (_, _) => opened++;
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.Equal(1, host.CaptureRequests);
        Assert.Same(Cursors.SizeAll, host.Viewport.Cursor); Assert.Equal(0, opened);
        host.View.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Null(host.Viewport.Cursor);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ZoomAroundPointer() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700); host.Scroll(2500, 1600);
        var anchor = new Point(230, 170); var logical = host.Viewport.TranslatePoint(anchor, host.Graph);
        double extent = host.Viewport.ExtentWidth;
        Assert.True(host.View.ZoomWithWheel(120, ModifierKeys.Control, anchor));
        Assert.Equal(1.1, host.Scale.ScaleX, 5); Assert.Equal(host.Scale.ScaleX, host.Scale.ScaleY);
        Assert.True(host.Viewport.ExtentWidth > extent);
        var moved = host.Graph.TranslatePoint(logical, host.Viewport);
        Assert.Equal(anchor.X, moved.X, 5); Assert.Equal(anchor.Y, moved.Y, 5);
        for (int i = 0; i < 20; i++) host.View.ZoomWithWheel(120, ModifierKeys.Control, anchor);
        Assert.Equal(1.5, host.Scale.ScaleX);
        for (int i = 0; i < 20; i++) host.View.ZoomWithWheel(-120, ModifierKeys.Control, anchor);
        Assert.Equal(.4, host.Scale.ScaleX);
        var offset = new Point(host.Viewport.HorizontalOffset, host.Viewport.VerticalOffset);
        host.View.ZoomWithWheel(-120, ModifierKeys.Control, anchor); host.Layout();
        Assert.Equal(.4, host.Scale.ScaleY); Assert.Equal(offset, new(host.Viewport.HorizontalOffset, host.Viewport.VerticalOffset));
        return Task.CompletedTask;
    });

    [Fact]
    public Task WheelWithoutCtrlKeepsScrolling() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700); host.Scroll(2000, 1200);
        Assert.False(host.View.ZoomWithWheel(-120, ModifierKeys.None, new(200, 100)));
        Assert.False(host.View.ZoomWithWheel(120, ModifierKeys.Shift, new(200, 100)));
        Assert.Equal(1, host.Scale.ScaleX); Assert.Equal(2000, host.Viewport.HorizontalOffset); Assert.Equal(1200, host.Viewport.VerticalOffset);
        Assert.True(host.View.ZoomWithWheel(0, ModifierKeys.Control, new(200, 100))); Assert.Equal(1, host.Scale.ScaleX);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ZoomDuringDrag() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700); host.Scroll(2000, 1200);
        host.View.BeginPan(new(300, 200)); host.View.PanTo(new(260, 180)); host.Layout();
        var anchor = new Point(260, 180); host.View.ZoomWithWheel(120, ModifierKeys.Control, anchor);
        double x = host.Viewport.HorizontalOffset, y = host.Viewport.VerticalOffset;
        host.View.PanTo(new(230, 160)); host.Layout();
        Assert.Equal(x + 30, host.Viewport.HorizontalOffset, 5); Assert.Equal(y + 20, host.Viewport.VerticalOffset, 5);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RefreshPreservesView() => WorkspaceTabsTests.Sta(() =>
    {
        using var host = new NavigatorHost(1100, 700);
        host.View.ZoomWithWheel(120, ModifierKeys.Control, new(200, 100)); host.Scroll(2000, 1200);
        var index = host.View.Index!;
        host.View.Present(index with { DynamicReferences = 3 }); host.Layout();
        Assert.Equal(1.1, host.Scale.ScaleX, 5); Assert.Equal(2000, host.Viewport.HorizontalOffset); Assert.Equal(1200, host.Viewport.VerticalOffset);
        Assert.Same(index.Nodes, host.View.Index!.Nodes); Assert.Same(index.Links, host.View.Index.Links);
        return Task.CompletedTask;
    });

    private sealed class NavigatorHost : IDisposable
    {
        public SceneNavigatorView View { get; }
        public int CaptureRequests { get; private set; }
        private readonly Size size;
        public ScrollViewer Viewport => ((DockPanel)View.Content).Children.OfType<ScrollViewer>().Single();
        public Canvas Graph => (Canvas)Viewport.Content;
        public ScaleTransform Scale => (ScaleTransform)Graph.LayoutTransform;
        public NavigatorHost(int width, int height, bool capture = true)
        {
            size = new(width, height);
            View = new(() => { CaptureRequests++; return capture; });
            var nodes = Enumerable.Range(0, 100).Select(i => new SceneNavigationNode("node" + i, i == 0 ? "main" : "scene" + i,
                $"game/scenes/{i:D3}.krl", 1, i < 25, [])).ToArray();
            var links = Enumerable.Range(0, 24).Select(i => new SceneNavigationLink("node" + i, "node" + (i + 1), "call")).ToArray();
            View.Present(new(nodes, links, 0));
            Layout();
        }
        public void Layout() { View.Measure(size); View.Arrange(new Rect(size)); View.UpdateLayout(); }
        public void Scroll(double x, double y) { Viewport.ScrollToHorizontalOffset(x); Viewport.ScrollToVerticalOffset(y); Layout(); }
        public void Dispose() => View.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
}
