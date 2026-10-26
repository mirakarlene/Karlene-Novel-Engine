using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KNE;

/// <summary>A read-only scene graph with viewport panning and zoom; nodes only open their source.</summary>
public sealed class SceneNavigatorView : UserControl
{
    public event EventHandler<SceneNavigationNode>? SceneRequested;
    public event EventHandler? RefreshRequested;
    private readonly Canvas graph = new() { Name = "SceneNavigationCanvas", Margin = new Thickness(20) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer viewport;
    private readonly ScaleTransform zoom = new(1, 1);
    private readonly Func<bool> captureMouse;
    private bool panning;
    private Point panStart, panOrigin;
    public SceneNavigationIndex? Index { get; private set; }

    public SceneNavigatorView() : this(null) { }

    internal SceneNavigatorView(Func<bool>? captureMouse)
    {
        Name = "SceneNavigator";
        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(12, 8, 12, 12) }; DockPanel.SetDock(header, Dock.Top);
        var refresh = new Button { Content = "Обновить", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right); refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        header.Children.Add(refresh); header.Children.Add(status); panel.Children.Add(header);
        graph.SetResourceReference(Panel.BackgroundProperty, "Editor.Background");
        graph.LayoutTransform = zoom;
        viewport = new ScrollViewer { Content = graph, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, Focusable = true, ToolTip = "ПКМ + перетаскивание — обзор · Ctrl + колесо — масштаб" };
        this.captureMouse = captureMouse ?? viewport.CaptureMouse;
        viewport.PreviewMouseRightButtonDown += (_, args) => { BeginPan(args.GetPosition(viewport)); args.Handled = panning; };
        viewport.PreviewMouseMove += (_, args) => { if (panning) { PanTo(args.GetPosition(viewport)); args.Handled = true; } };
        viewport.PreviewMouseRightButtonUp += (_, args) => { if (panning) { EndPan(); args.Handled = true; } };
        viewport.LostMouseCapture += (_, _) => EndPan();
        viewport.PreviewMouseWheel += (_, args) =>
        {
            if (ZoomWithWheel(args.Delta, Keyboard.Modifiers, args.GetPosition(viewport))) args.Handled = true;
        };
        Unloaded += (_, _) => EndPan();
        panel.Children.Add(viewport);
        Content = panel;
    }

    internal void BeginPan(Point position)
    {
        viewport.Focus();
        if (!captureMouse()) return;
        panning = true; panStart = position; panOrigin = new(viewport.HorizontalOffset, viewport.VerticalOffset);
        viewport.Cursor = Cursors.SizeAll;
    }

    internal void PanTo(Point position)
    {
        if (!panning) return;
        viewport.ScrollToHorizontalOffset(panOrigin.X + panStart.X - position.X);
        viewport.ScrollToVerticalOffset(panOrigin.Y + panStart.Y - position.Y);
    }

    private void EndPan()
    {
        panning = false;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        viewport.Cursor = null;
    }

    internal bool ZoomWithWheel(int delta, ModifierKeys modifiers, Point anchor)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control)) return false;
        if (delta != 0) ChangeZoom(delta > 0 ? 1 : -1, anchor);
        return true;
    }

    private void ChangeZoom(int steps, Point anchor)
    {
        double next = Math.Clamp(Math.Round(zoom.ScaleX + steps * .1, 2), .4, 1.5);
        if (next == zoom.ScaleX) return;
        Point logical = viewport.TranslatePoint(anchor, graph);
        zoom.ScaleX = zoom.ScaleY = next;
        viewport.UpdateLayout();
        Point moved = graph.TranslatePoint(logical, viewport);
        viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset + moved.X - anchor.X);
        viewport.ScrollToVerticalOffset(viewport.VerticalOffset + moved.Y - anchor.Y);
        viewport.UpdateLayout();
        if (panning) { panStart = anchor; panOrigin = new(viewport.HorizontalOffset, viewport.VerticalOffset); }
    }

    public void Present(SceneNavigationIndex index)
    {
        Index = index; graph.Children.Clear();
        status.Text = "Только просмотр · двойной клик открывает сцену · серый: не используется · красный: ошибка" +
            (index.DynamicReferences > 0 ? "\nВычисляемых переходов без статической цели: " + index.DynamicReferences : "");
        var positions = Layout(index);
        foreach (var link in index.Links)
        {
            Point from = positions[link.Source], to = positions[link.Target];
            var start = new Point(from.X + 320, from.Y + 48); var end = new Point(to.X, to.Y + 48);
            double bend = Math.Max(50, Math.Abs(end.X - start.X) / 2);
            var controls = link.Source == link.Target
                ? new BezierSegment(new(start.X + 50, start.Y + 90), new(end.X - 50, end.Y + 90), end, true)
                : new BezierSegment(new(start.X + bend, start.Y), new(end.X - bend, end.Y), end, true);
            var geometry = new PathGeometry([new PathFigure(start, [controls], false)]);
            var path = new System.Windows.Shapes.Path { Data = geometry, StrokeThickness = 1.5, ToolTip = link.Kind };
            path.SetResourceReference(Shape.StrokeProperty, "KNE.AccentBrush"); graph.Children.Add(path);
            var arrow = new Polygon { Points = [new(end.X, end.Y), new(end.X - 9, end.Y - 5), new(end.X - 9, end.Y + 5)], IsHitTestVisible = false };
            arrow.SetResourceReference(Shape.FillProperty, "KNE.AccentBrush"); graph.Children.Add(arrow);
        }
        foreach (var node in index.Nodes)
        {
            var title = new TextBlock { Text = node.Function, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var file = new TextBlock { Text = node.File, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 42 };
            var labels = new StackPanel(); labels.Children.Add(title); labels.Children.Add(file);
            var card = new Border { Tag = node, Width = 320, Height = 100, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), Child = labels,
                Cursor = Cursors.Hand, ToolTip = node.Function + " · " + node.File + ":" + node.Line + (node.Errors.Count > 0 ? "\n" + string.Join("\n", node.Errors) : "") };
            card.SetResourceReference(Border.BackgroundProperty, "Editor.Surface");
            if (node.Errors.Count > 0) { card.Background = new SolidColorBrush(Color.FromRgb(133, 46, 46)); card.BorderBrush = Brushes.Red; labels.SetValue(TextBlock.ForegroundProperty, Brushes.White); }
            else if (!node.Reachable) { card.Background = Brushes.DimGray; card.BorderBrush = Brushes.Gray; labels.SetValue(TextBlock.ForegroundProperty, Brushes.White); }
            else card.SetResourceReference(Border.BorderBrushProperty, "KNE.AccentBrush");
            card.MouseLeftButtonDown += (_, args) => { if (args.ClickCount == 2) { args.Handled = true; SceneRequested?.Invoke(this, node); } };
            Canvas.SetLeft(card, positions[node.Id].X); Canvas.SetTop(card, positions[node.Id].Y); graph.Children.Add(card);
        }
        graph.Width = positions.Count == 0 ? 1 : positions.Values.Max(point => point.X) + 340;
        graph.Height = positions.Count == 0 ? 1 : positions.Values.Max(point => point.Y) + 120;
        if (index.Nodes.Count == 0) status.Text += "\nВ проекте пока нет сценариев .krl или .knegraph.";
    }

    private static Dictionary<string, Point> Layout(SceneNavigationIndex index)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var outgoing = index.Links.GroupBy(link => link.Source).ToDictionary(group => group.Key, group => group.Select(link => link.Target).ToArray());
        var pending = new Queue<string>();
        foreach (var entry in index.Nodes.Where(node => node.Function == "main")) { ranks[entry.Id] = 0; pending.Enqueue(entry.Id); }
        while (pending.TryDequeue(out string? id))
            foreach (string target in outgoing.GetValueOrDefault(id) ?? [])
                if (ranks.TryAdd(target, ranks[id] + 1)) pending.Enqueue(target);
        int unused = ranks.Count == 0 ? 0 : ranks.Values.Max() + 1;
        var rows = new Dictionary<int, int>(); var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var node in index.Nodes.OrderBy(node => node.File, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Line))
        {
            int rank = ranks.GetValueOrDefault(node.Id, unused), row = rows.GetValueOrDefault(rank);
            positions[node.Id] = new Point(rank * 400, row * 140); rows[rank] = row + 1;
        }
        return positions;
    }
}
