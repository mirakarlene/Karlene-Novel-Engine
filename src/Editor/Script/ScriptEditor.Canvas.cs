using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace KNE;

/// <summary>An icon-only connection handle, deliberately independent of the desktop button theme.</summary>
internal sealed class GraphPort : Button
{
    internal required string NodeId { get; init; }
    internal int? OutputIndex { get; init; }
    internal bool IsOutput => OutputIndex is >= 0;

    internal GraphPort()
    {
        Width = Height = 22;
        Cursor = Cursors.Cross;
        Focusable = false;
        var circle = new FrameworkElementFactory(typeof(Border));
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        circle.SetValue(Border.BackgroundProperty, Brushes.White);
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        circle.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Foreground))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(TextBlock.TextProperty, "›");
        glyph.SetValue(TextBlock.FontSizeProperty, 21d);
        glyph.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, -5, 0, 0));
        glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        circle.AppendChild(glyph);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = circle };
    }
}

internal sealed class GraphResizeThumb : Thumb
{
    internal Func<Point, bool>? AllowHit { get; init; }
    internal GraphResizeThumb()
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        surface.SetValue(IsHitTestVisibleProperty, false);
        Template = new ControlTemplate(typeof(Thumb)) { VisualTree = surface };
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        AllowHit?.Invoke(hitTestParameters.HitPoint) == false ? null : new PointHitTestResult(this, hitTestParameters.HitPoint);
}

public partial class ScriptEditor
{
    private readonly Dictionary<(string NodeId, int? Output), GraphPort> graphPorts = [];
    private readonly Dictionary<string, Rectangle> cardShadows = [];
    private readonly List<(Thumb Handle, string NodeId, int Horizontal, int Vertical)> resizeHandles = [];
    private GraphPort? draggingPort;
    private Point portDragOrigin;
    private bool portDragMoved;
    private System.Windows.Shapes.Path? connectionPreview;
    private Thumb? activeResizeThumb;

    private static double NodeWidth(ScriptNode node) => node.Width;
    private static double NodeHeight(ScriptNode node) => node.Height ?? Math.Max(136, 88 + Math.Max(node.Outputs().Count(), ShaderLibrary.IsShader(node.Kind) ? ShaderLibrary.Get(node.Kind).Inputs.Length : node.Plugin?.Inputs.Length ?? 0) * 28);

    private void RenderCanvasGraph()
    {
        ApplyPluginTexture();
        GraphCanvas.Children.Clear(); cards.Clear(); links.Clear(); graphPorts.Clear(); resizeHandles.Clear(); cardShadows.Clear();
        TextOptions.SetTextFormattingMode(GraphCanvas, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(GraphCanvas, TextRenderingMode.ClearType);
        if (Document?.Graph is not { } graph) return;
        UpdateGraphExtent();
        foreach (var node in graph.Nodes)
        {
            var color = (Color)ColorConverter.ConvertFromString(BlockLibrary.ColorFor(node));
            var brush = new SolidColorBrush(color);
            Brush portBrush = node.Kind == ScriptNodeKind.Start ? Brushes.SlateGray : brush;
            var contents = new StackPanel();
            var header = new Border
            {
                Background = brush, CornerRadius = new CornerRadius(9, 9, 0, 0), Height = 32,
                Cursor = Cursors.SizeAll, Child = new TextBlock
                {
                    Text = (node.Plugin == null ? "" : node.Plugin.Icon + " ") + node.Title, Foreground = node.Kind == ScriptNodeKind.Start || CalculationCompiler.IsCalculation(node.Kind) || graph.IsShader || node.Category == "shaders" ? Brushes.Black : Brushes.White,
                    FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(12, 6, 12, 0), ToolTip = DescribeNodeIdentity(node)
                }
            };
            contents.Children.Add(header);
            var summary = new TextBlock
            {
                Text = Summary(node), Height = 40, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 5, 14, 0),
                Foreground = Brushes.DarkSlateGray
            };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "Editor.Text"); contents.Children.Add(summary);
            var ports = new Grid { Height = Math.Max(20, NodeHeight(node) - 81), ClipToBounds = false };
            var outputs = node.Outputs().ToArray();
            string[] shaderInputs = ShaderLibrary.IsData(node) ? ShaderLibrary.Get(node.Kind).Inputs : node.Plugin?.Inputs ?? [];
            int rows = Math.Max(1, Math.Max(outputs.Length, shaderInputs.Length));
            for (int i = 0; i < rows; i++) ports.RowDefinitions.Add(new RowDefinition());
            if (!ShaderLibrary.IsData(node) && node.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.Comment or ScriptNodeKind.Plugin))
            {
                var input = CreateGraphPort(node, null, portBrush, "Вход");
                input.HorizontalAlignment = HorizontalAlignment.Left; input.Margin = new Thickness(-12, 0, 0, 0);
                Grid.SetRowSpan(input, rows); ports.Children.Add(input);
            }
            for (int index = 0; index < shaderInputs.Length; index++)
            {
                var input = CreateGraphPort(node, -index - 1, portBrush, shaderInputs[index]);
                input.HorizontalAlignment = HorizontalAlignment.Left; input.Margin = new Thickness(-12, 0, 0, 0); Grid.SetRow(input, index); ports.Children.Add(input);
                var label = new TextBlock { Text = shaderInputs[index], FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 50, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Editor.Muted"); Grid.SetRow(label, index); ports.Children.Add(label);
            }
            for (int index = 0; index < outputs.Length; index++)
            {
                var output = CreateGraphPort(node, index, portBrush, outputs[index].Label);
                output.HorizontalAlignment = HorizontalAlignment.Right; output.Margin = new Thickness(0, 0, -12, 0);
                Grid.SetRow(output, index); ports.Children.Add(output);
                var label = new TextBlock
                {
                    Text = outputs[index].Label, Foreground = Brushes.SlateGray, FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(20, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetRow(label, index); ports.Children.Add(label);
                label.SetResourceReference(TextBlock.ForegroundProperty, "Editor.Muted");
            }
            contents.Children.Add(ports);
            var card = new Border
            {
                Width = NodeWidth(node), Height = NodeHeight(node), BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(11), BorderBrush = brush, Background = Brushes.White,
                UseLayoutRounding = true, SnapsToDevicePixels = true,
                Child = contents, Tag = node.Id
            };
            card.SetResourceReference(Border.BackgroundProperty, "Editor.Surface");
            string category = node.Category.Length > 0 ? node.Category : BlockLibrary.Definitions.First(entry => entry.Kind == (node.Kind == ScriptNodeKind.Condition ? ScriptNodeKind.Branching : node.Kind)).Group;
            var title = (TextBlock)header.Child;
            header.Child = null;
            var heading = new DockPanel(); var badge = new CategoryIcon { Group = category, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(badge); heading.Children.Add(title); header.Child = heading;
            BlockAccessibility.Decorate(card, category, presentation =>
            {
                bool adapted = BlockAccessibility.GetMode(card) != "normal";
                var tint = adapted && node.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.End) ? new SolidColorBrush(presentation.Color) : brush;
                header.Background = tint; UpdateCardOutline(node, card);
                if (category == "shaders" && !adapted) header.Background = ShaderRainbow.Create(header);
                title.Foreground = node.Kind == ScriptNodeKind.Start ? Brushes.Black : node.Kind == ScriptNodeKind.End ? Brushes.White : adapted ? new SolidColorBrush(presentation.Foreground) : CalculationCompiler.IsCalculation(node.Kind) || graph.IsShader || category == "shaders" ? Brushes.Black : Brushes.White;
                badge.Foreground = title.Foreground;
                foreach (var port in ports.Children.OfType<GraphPort>()) port.Foreground = tint;
                if (cardShadows.TryGetValue(node.Id, out var softBorder) && softBorder.Effect is DropShadowEffect effect) effect.Color = tint.Color;
            });
            // Shader effects rasterize the entire subtree. Keep glyphs and ports outside that surface.
            var shadow = new Rectangle
            {
                Width = NodeWidth(node), Height = NodeHeight(node), RadiusX = 11, RadiusY = 11,
                Fill = Brushes.White, IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = color, BlurRadius = 14, ShadowDepth = 2, Opacity = .23 }
            };
            Canvas.SetLeft(shadow, node.X); Canvas.SetTop(shadow, node.Y); Panel.SetZIndex(shadow, 0);
            GraphCanvas.Children.Add(shadow); cardShadows.Add(node.Id, shadow);
            card.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (node.Kind is not (ScriptNodeKind.Subsystem or ScriptNodeKind.ShaderContainer) || args.ClickCount != 2) return;
                for (DependencyObject? target = args.OriginalSource as DependencyObject; target != null && target != card; target = VisualTreeHelper.GetParent(target))
                    if (target is ButtonBase or Thumb) return;
                args.Handled = true; OpenSubsystem(node.Id);
            };
            HookCardSelectionAndMovement(card, header, node);
            Canvas.SetLeft(card, node.X); Canvas.SetTop(card, node.Y); Panel.SetZIndex(card, 1);
            GraphCanvas.Children.Add(card); cards.Add(node.Id, card);
            AddResizeHandles(node);
            card.SizeChanged += (_, _) => RenderLinks();
        }
        RefreshCardSelection(); RenderLinks();
    }

    private GraphPort CreateGraphPort(ScriptNode node, int? output, Brush color, string label)
    {
        var port = new GraphPort
        {
            NodeId = node.Id, OutputIndex = output, Foreground = color, Content = label,
            ToolTip = output is >= 0 ? label + " · потяните к входу. Правая кнопка — отсоединить."
                : "Вход · потяните к выходу другого блока"
        };
        graphPorts.Add((node.Id, output), port);
        port.Click += (_, _) => Guard(() => ClickGraphPort(port));
        port.PreviewMouseLeftButtonDown += (_, e) => Guard(() =>
        {
            ApplyProperties(repaint: false);
            GraphScroll.Focus();
            draggingPort = port; portDragOrigin = e.GetPosition(GraphScroll); portDragMoved = false;
            port.CaptureMouse(); e.Handled = true;
        });
        port.PreviewMouseMove += (_, e) =>
        {
            if (draggingPort != port || e.LeftButton != MouseButtonState.Pressed) return;
            var current = e.GetPosition(GraphScroll);
            portDragMoved |= Math.Abs(current.X - portDragOrigin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                             Math.Abs(current.Y - portDragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance;
            if (portDragMoved) DrawConnectionPreview(port, e.GetPosition(GraphCanvas));
            e.Handled = true;
        };
        port.PreviewMouseLeftButtonUp += (_, e) => Guard(() =>
        {
            if (draggingPort != port) return;
            Point viewport = e.GetPosition(GraphScroll);
            var destination = new Rect(0, 0, GraphScroll.ActualWidth, GraphScroll.ActualHeight).Contains(viewport)
                ? FindGraphPort(GraphCanvas.InputHitTest(e.GetPosition(GraphCanvas)) as DependencyObject) : null;
            bool dragged = portDragMoved;
            CancelPortDrag(clearPending: false);
            if (dragged)
            {
                pendingConnection = null;
                if (destination == null || !ConnectGraphPorts(port.NodeId, port.OutputIndex, destination.NodeId, destination.OutputIndex))
                    EditorStatus.Text = "Связь отменена: отпустите порт над совместимым портом.";
            }
            else ClickGraphPort(port);
            e.Handled = true;
        });
        port.LostMouseCapture += (_, _) => { if (draggingPort == port) CancelPortDrag(clearPending: false); };
        port.MouseRightButtonUp += (_, e) => Guard(() =>
        {
            if (ShaderLibrary.IsData(node)) { DisconnectShaderPort(port); e.Handled = true; return; }
            if (output is not { } index || Document?.Graph == null) return;
            ApplyProperties(repaint: false);
            var graph = Document.Graph;
            var current = graph.Nodes.Single(n => n.Id == node.Id);
            if (current.Outputs().ElementAt(index).Target != null) { Remember(); current.SetOutput(index, null); }
            pendingConnection = null; RenderGraph(); UpdateTitle(); e.Handled = true;
        });
        return port;
    }

    private static GraphPort? FindGraphPort(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is GraphPort port) return port;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void ClickGraphPort(GraphPort port)
    {
        GraphScroll.Focus();
        if (Document?.Graph?.Nodes.FirstOrDefault(node => node.Id == port.NodeId) is { } current && ShaderLibrary.IsData(current))
        {
            if (port.IsOutput) { ApplyProperties(repaint: false); pendingConnection = (port.NodeId, port.OutputIndex!.Value); EditorStatus.Text = "Выберите вход значения другого блока."; }
            else if (pendingConnection is { } source) ConnectShaderPorts(source.Id, source.Port, port.NodeId, port.OutputIndex ?? -1);
            return;
        }
        if (port.OutputIndex is >= 0 and var index)
        {
            ApplyProperties(repaint: false); pendingConnection = (port.NodeId, index);
            EditorStatus.Text = "Выберите вход другого блока. Esc — отменить.";
        }
        else if (port.OutputIndex is < 0 && pendingConnection is { } connection) ConnectGraphPorts(connection.Id, connection.Port, port.NodeId, port.OutputIndex);
        else ConnectTo(port.NodeId);
    }

    internal bool ConnectGraphPorts(string firstNode, int? firstOutput, string secondNode, int? secondOutput)
    {
        if (Document?.Graph is { } model && model.Nodes.Any(node => (node.Id == firstNode || node.Id == secondNode) && ShaderLibrary.IsData(node))) return ConnectShaderPorts(firstNode, firstOutput ?? -1, secondNode, secondOutput ?? -1);
        bool firstIsOutput = firstOutput is >= 0, secondIsOutput = secondOutput is >= 0;
        if (firstIsOutput == secondIsOutput || firstNode == secondNode || Document?.Graph is not { } graph) return false;
        string source = firstIsOutput ? firstNode : secondNode;
        string target = firstIsOutput ? secondNode : firstNode;
        int output = (firstIsOutput ? firstOutput : secondOutput)!.Value;
        int? encodedInput = firstIsOutput ? secondOutput : firstOutput;
        int input = encodedInput is < 0 ? -encodedInput.Value - 1 : 0;
        var from = graph.Nodes.FirstOrDefault(n => n.Id == source);
        var to = graph.Nodes.FirstOrDefault(n => n.Id == target);
        if (from == null || to == null || to.Kind is ScriptNodeKind.Start or ScriptNodeKind.Comment ||
            output < 0 || output >= from.Outputs().Count() || input >= (to.Plugin?.Inputs.Length ?? 1)) return false;
        ApplyProperties(repaint: false);
        from = Document!.Graph!.Nodes.Single(n => n.Id == source);
        if (output >= from.Outputs().Count()) return false;
        if (from.Outputs().ElementAt(output).Target != target || from.TargetInput(output) != input) { Remember(); from.SetOutput(output, target, input); }
        pendingConnection = null; RenderGraph(); UpdateTitle(); EditorStatus.Text = "Блоки соединены.";
        return true;
    }

    private void CancelPortDrag(bool clearPending = true)
    {
        if (clearPending) activeResizeThumb?.CancelDrag();
        var previous = draggingPort; draggingPort = null; portDragMoved = false;
        if (previous?.IsMouseCaptured == true) previous.ReleaseMouseCapture();
        if (connectionPreview != null) GraphCanvas.Children.Remove(connectionPreview);
        connectionPreview = null;
        if (clearPending) pendingConnection = null;
    }

    private void ClearCanvasInteraction()
    {
        CancelPortDrag();
        graphPorts.Clear(); resizeHandles.Clear();
    }

    private Point PortAnchor(string nodeId, int? output)
    {
        var node = Document!.Graph!.Nodes.Single(n => n.Id == nodeId);
        if (graphPorts.TryGetValue((nodeId, output), out var port) && port.IsArrangeValid && port.ActualWidth > 0 &&
            cards.TryGetValue(nodeId, out var card) && card.IsArrangeValid)
        {
            // Canvas.Left/Top update the model before WPF's next arrange pass. Use the card-local
            // handle position so moving a selected group never leaves its arrows one frame behind.
            Point local = port.TranslatePoint(new Point(port.ActualWidth / 2, port.ActualHeight / 2), card);
            return new Point(node.X + local.X, node.Y + local.Y);
        }
        double body = Math.Max(20, NodeHeight(node) - 81);
        bool isOutput = output is >= 0;
        return new Point(node.X + (isOutput ? NodeWidth(node) : 0), node.Y + 78.5 +
            (isOutput ? body * (output!.Value + .5) / Math.Max(1, node.Outputs().Count()) : output is < 0 ? body * (-output.Value - .5) / Math.Max(1, node.Plugin?.Inputs.Length ?? ShaderLibrary.Get(node.Kind).Inputs.Length) : body / 2));
    }

    internal static PathGeometry ConnectionGeometry(Point start, Point end)
    {
        double bend = Math.Max(65, Math.Abs(end.X - start.X) * .48);
        return new PathGeometry([new PathFigure(start,
            [new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true)], false)]);
    }

    private void DrawConnectionPreview(GraphPort port, Point pointer)
    {
        if (connectionPreview == null)
        {
            connectionPreview = new System.Windows.Shapes.Path
            {
                IsHitTestVisible = false, Stroke = port.Foreground, StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection([3, 2]), Opacity = .75
            };
            GraphCanvas.Children.Add(connectionPreview);
        }
        Point anchor = PortAnchor(port.NodeId, port.OutputIndex);
        connectionPreview.Data = port.IsOutput ? ConnectionGeometry(anchor, pointer) : ConnectionGeometry(pointer, anchor);
    }

    private void RenderCanvasLinks()
    {
        foreach (var link in links) GraphCanvas.Children.Remove(link);
        links.Clear();
        if (Document?.Graph is not { } graph) return;
        var map = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var node in graph.Nodes)
        {
            if (ShaderLibrary.IsData(node))
            {
                foreach (var (input, connection) in node.Shader!.Inputs)
                {
                    if (!map.ContainsKey(connection.Node)) continue;
                    int slot = Array.IndexOf(ShaderLibrary.Get(node.Kind).Inputs, input); if (slot < 0) continue;
                    var path = new System.Windows.Shapes.Path { Data = ConnectionGeometry(PortAnchor(connection.Node, connection.Output), PortAnchor(node.Id, -slot - 1)), Stroke = Brushes.MediumPurple, StrokeThickness = 2.5, IsHitTestVisible = false };
                    GraphCanvas.Children.Add(path); links.Add(path);
                }
                continue;
            }
            int index = 0;
            foreach (var (_, target) in node.Outputs())
            {
                int output = index++;
                if (target == null || !map.ContainsKey(target)) continue;
                Point start = PortAnchor(node.Id, output), end = PortAnchor(target, map[target].Plugin == null ? null : -node.TargetInput(output) - 1);
                Brush color = node.Kind == ScriptNodeKind.Start ? Brushes.SlateGray : new SolidColorBrush((Color)ColorConverter.ConvertFromString(BlockLibrary.ColorFor(node)));
                var path = new System.Windows.Shapes.Path
                {
                    Data = ConnectionGeometry(start, end), Stroke = color, StrokeThickness = 2.2,
                    Opacity = .8, IsHitTestVisible = false
                };
                var arrow = new Polygon
                {
                    Fill = color, Points = new PointCollection { end, new(end.X - 10, end.Y - 5), new(end.X - 10, end.Y + 5) },
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(path, 0); Panel.SetZIndex(arrow, 0);
                GraphCanvas.Children.Add(path); GraphCanvas.Children.Add(arrow); links.Add(path); links.Add(arrow);
            }
        }
        PositionResizeHandles();
        foreach (var node in graph.Nodes)
            if (cardShadows.TryGetValue(node.Id, out var shadow))
            {
                shadow.Width = NodeWidth(node); shadow.Height = NodeHeight(node);
                Canvas.SetLeft(shadow, node.X); Canvas.SetTop(shadow, node.Y);
            }
    }

    private void RefreshCardSelection()
    {
        foreach (var (id, card) in cards)
        {
            UpdateCardOutline(Document!.Graph!.Nodes.Single(node => node.Id == id), card);
        }
    }

    private void UpdateCardOutline(ScriptNode node, Border card)
    {
        bool selected = selectedIds.Contains(node.Id) || selectedId == node.Id;
        string mode = BlockAccessibility.GetMode(card);
        string category = node.Category.Length > 0 ? node.Category : BlockLibrary.Definitions.First(entry => entry.Kind == (node.Kind == ScriptNodeKind.Condition ? ScriptNodeKind.Branching : node.Kind)).Group;
        var presentation = BlockAccessibility.Presentation(category, mode);
        Color color = mode != "normal" && node.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.End) ? presentation.Color : (Color)ColorConverter.ConvertFromString(BlockLibrary.ColorFor(node));
        card.BorderBrush = selected ? Brushes.DarkOrange : new SolidColorBrush(color);
        card.BorderThickness = new Thickness(presentation.BorderWidth + (selected ? 1 : 0));
    }

    private void CommitGraphGesture(string before)
    {
        if (Document?.Graph is not { } graph || graph.Serialize() == before) return;
        undo.Push(before); TrimHistory(undo); redo.Clear(); UpdateTitle();
    }

    internal void ResizeGraphNode(string id, Rect bounds, bool remember = true)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if (Document?.Graph is not { } graph) return;
        var node = graph.Nodes.Single(n => n.Id == id);
        string? before = remember ? graph.Serialize() : null;
        node.X = Math.Clamp(bounds.X, -1_000_000_000, 1_000_000_000);
        node.Y = Math.Clamp(bounds.Y, -1_000_000_000, 1_000_000_000);
        node.Width = Math.Clamp(bounds.Width, 160, 4096); node.Height = Math.Clamp(bounds.Height, 100, 4096);
        if (cards.TryGetValue(id, out var card))
        {
            card.Width = node.Width; card.Height = node.Height.Value;
            Canvas.SetLeft(card, node.X); Canvas.SetTop(card, node.Y);
            if (card.Child is StackPanel content && content.Children[content.Children.Count - 1] is Grid ports) ports.Height = Math.Max(20, node.Height.Value - 81);
        }
        UpdateGraphExtent(); RenderLinks();
        if (before != null) CommitGraphGesture(before);
    }

    private void AddResizeHandles(ScriptNode node)
    {
        foreach (var (horizontal, vertical) in new[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) })
        {
            GraphResizeThumb? thumb = null;
            thumb = new GraphResizeThumb
            {
                Width = horizontal == 0 ? Math.Max(10, node.Width - 20) : 10,
                Height = vertical == 0 ? Math.Max(10, NodeHeight(node) - 20) : 10,
                Cursor = horizontal == 0 ? Cursors.SizeNS : vertical == 0 ? Cursors.SizeWE : horizontal == vertical ? Cursors.SizeNWSE : Cursors.SizeNESW,
                Background = Brushes.Transparent, Opacity = 0, Focusable = false,
                AllowHit = point => !graphPorts.Where(pair => pair.Key.NodeId == node.Id).Any(pair =>
                {
                    Point local = thumb!.TranslatePoint(point, pair.Value);
                    return local.X is >= -2 and <= 24 && local.Y is >= -2 and <= 24;
                })
            };
            Panel.SetZIndex(thumb, 3);
            string? before = null;
            Rect origin = default;
            Point pointer = default;
            thumb.DragStarted += (_, _) => Guard(() =>
            {
                ApplyProperties(repaint: false);
                GraphScroll.Focus();
                var current = Document!.Graph!.Nodes.Single(n => n.Id == node.Id);
                before = Document.Graph.Serialize(); origin = new Rect(current.X, current.Y, NodeWidth(current), NodeHeight(current));
                pointer = Mouse.GetPosition(GraphScroll);
                activeResizeThumb = thumb;
            });
            thumb.DragDelta += (_, _) => Guard(() =>
            {
                if (before == null) return;
                var position = Mouse.GetPosition(GraphScroll);
                double dx = (position.X - pointer.X) / ZoomSlider.Value, dy = (position.Y - pointer.Y) / ZoomSlider.Value;
                ResizeGraphNode(node.Id, ResizeBounds(origin, horizontal, vertical, new Vector(dx, dy)), remember: false);
            });
            thumb.DragCompleted += (_, e) => Guard(() =>
            {
                activeResizeThumb = null;
                if (before == null) return;
                if (e.Canceled) { Document!.Graph = VisualScript.Parse(before); RenderGraph(); }
                else CommitGraphGesture(before);
                before = null;
            });
            GraphCanvas.Children.Add(thumb); resizeHandles.Add((thumb, node.Id, horizontal, vertical));
        }
    }

    private static Rect ResizeBounds(Rect origin, int horizontal, int vertical, Vector delta)
    {
        double width = Math.Clamp(origin.Width + delta.X * horizontal, 160, 4096);
        double height = Math.Clamp(origin.Height + delta.Y * vertical, 100, 4096);
        return new Rect(horizontal < 0 ? origin.Right - width : origin.X,
            vertical < 0 ? origin.Bottom - height : origin.Y, width, height);
    }

    private void PositionResizeHandles()
    {
        if (Document?.Graph is not { } graph) return;
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var (handle, id, horizontal, vertical) in resizeHandles)
        {
            if (!nodes.TryGetValue(id, out var node)) continue;
            double width = NodeWidth(node), height = NodeHeight(node);
            handle.Width = horizontal == 0 ? Math.Max(10, width - 24) : 10;
            handle.Height = vertical == 0 ? Math.Max(10, height - 24) : 10;
            Canvas.SetLeft(handle, node.X + (horizontal < 0 ? -5 : horizontal > 0 ? width - 5 : 12));
            Canvas.SetTop(handle, node.Y + (vertical < 0 ? -5 : vertical > 0 ? height - 5 : 12));
        }
    }
}
