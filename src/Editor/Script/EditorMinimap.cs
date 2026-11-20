using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

/// <summary>Compact overview of an editor surface with a draggable viewport.</summary>
public sealed class EditorMinimap : FrameworkElement
{
    private static readonly Brush Background = Paint(229, 26, 32, 44);
    private static readonly Brush Content = Paint(177, 151, 164, 189);
    private static readonly Brush View = Paint(70, 132, 173, 238);
    private static readonly Pen Outline = ViewportOutline();
    private KrlCodeEditor? editor;
    private Func<(IEnumerable<Rect> Nodes, Rect Viewport)>? graph;
    private Action<Point>? centerGraph;
    private Rect world;
    private Rect map;
    private Rect viewport;
    private Vector grabOffset;

    internal void Bind(KrlCodeEditor value)
    {
        editor = value;
        editor.TextChanged += (_, _) => InvalidateVisual();
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
    }

    internal void Bind(Func<(IEnumerable<Rect> Nodes, Rect Viewport)> snapshot, Action<Point> center)
    { graph = snapshot; centerGraph = center; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 8 || ActualHeight < 8) return;
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Background, null, bounds);
        dc.PushClip(new RectangleGeometry(bounds));
        if (editor is { Document: { } document } source)
        {
            double total = Math.Max(1, source.ExtentHeight);
            double height = Math.Min(ActualHeight, document.LineCount * 2d);
            int samples = Math.Min((int)Math.Max(1, Math.Ceiling(height)), document.LineCount);
            double step = height / samples;
            for (int i = 0; i < samples; i++)
            {
                var line = document.GetLineByNumber(1 + (int)((long)i * document.LineCount / samples));
                if (line.Length == 0) continue;
                dc.DrawRectangle(Content, null, new Rect(5, i * step, Math.Min(ActualWidth - 10, 1 + line.Length * .62), Math.Min(1, step)));
            }
            map = new Rect(0, 0, ActualWidth, height);
            viewport = new Rect(0, Math.Min(height, height * source.VerticalOffset / total), ActualWidth,
                Math.Min(height, Math.Max(3, height * source.ViewportHeight / total)));
        }
        else if (graph != null)
        {
            var snapshot = graph();
            var nodes = snapshot.Nodes.ToArray();
            if (!IsMouseCaptured || world.IsEmpty || world.Width == 0)
            {
                world = snapshot.Viewport;
                foreach (var node in nodes) world.Union(node);
                world.Inflate(45, 45);
                double scale = Math.Min((ActualWidth - 8) / world.Width, (ActualHeight - 8) / world.Height);
                map = new Rect((ActualWidth - world.Width * scale) / 2, (ActualHeight - world.Height * scale) / 2,
                    world.Width * scale, world.Height * scale);
            }
            foreach (var node in nodes) dc.DrawRectangle(Content, null, Project(node));
            viewport = Project(snapshot.Viewport);
        }
        if (!viewport.IsEmpty) dc.DrawRectangle(View, Outline, viewport);
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (map.Width <= 0 || map.Height <= 0) return;
        Point click = e.GetPosition(this);
        grabOffset = viewport.Contains(click) ? click - Center(viewport) : default;
        if (CaptureMouse()) Navigate(click);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Navigate(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    private void Navigate(Point pointer)
    {
        Point center = pointer - grabOffset;
        double x = Math.Clamp((center.X - map.Left) / map.Width, 0, 1);
        double y = Math.Clamp((center.Y - map.Top) / map.Height, 0, 1);
        if (editor is { } source)
            source.ScrollToVerticalOffset(Math.Clamp(source.ExtentHeight * y - source.ViewportHeight / 2, 0, Math.Max(0, source.ExtentHeight - source.ViewportHeight)));
        else
            centerGraph?.Invoke(new Point(world.Left + world.Width * x, world.Top + world.Height * y));
        InvalidateVisual();
    }

    private Rect Project(Rect area)
    {
        double scale = map.Width / world.Width;
        return new Rect(map.Left + (area.Left - world.Left) * scale, map.Top + (area.Top - world.Top) * scale,
            area.Width * scale, area.Height * scale);
    }
    private static Point Center(Rect area) => new(area.Left + area.Width / 2, area.Top + area.Height / 2);
    private static Pen ViewportOutline()
    {
        var outline = new Pen(Paint(200, 132, 173, 238), 1);
        outline.Freeze();
        return outline;
    }
    private static Brush Paint(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}
