using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class GraphSurfaceTests
{
    [Fact]
    public void AuthoredSizes_RoundTrip_AndLegacyGraphsRemainAutoSized()
    {
        var graph = VisualScript.Create();
        graph.Nodes[1].Width = 333; graph.Nodes[1].Height = 241;
        var loaded = VisualScript.Parse(graph.Serialize());
        Assert.Equal(333, loaded.Nodes[1].Width); Assert.Equal(241, loaded.Nodes[1].Height);
        Assert.Equal(210, loaded.Nodes[0].Width); Assert.Null(loaded.Nodes[0].Height);
        var legacy = VisualScript.Parse("{\"Nodes\":[{\"Id\":\"old\",\"Kind\":\"Comment\"}]}");
        Assert.Equal(210, legacy.Nodes[0].Width); Assert.Null(legacy.Nodes[0].Height);
    }

    [Theory]
    [InlineData(159, 100)] [InlineData(4097, 100)] [InlineData(210, 99)] [InlineData(210, 4097)]
    public void InvalidAuthoredSizes_AreRejected(double width, double height)
    {
        var graph = VisualScript.Create(); graph.Nodes[1].Width = width; graph.Nodes[1].Height = height;
        var error = Assert.Throws<InvalidDataException>(() => graph.Serialize());
        Assert.Contains("dialogue", error.Message); Assert.Contains("размер", error.Message);
    }

    [Theory]
    [InlineData(160, 100)] [InlineData(4096, 4096)]
    public void SizeLimits_AreInclusive(double width, double height)
    {
        var graph = VisualScript.Create(); graph.Nodes[1].Width = width; graph.Nodes[1].Height = height;
        var loaded = VisualScript.Parse(graph.Serialize());
        Assert.Equal(width, loaded.Nodes[1].Width); Assert.Equal(height, loaded.Nodes[1].Height);
    }

    [Fact]
    public Task Resize_ClampsAndUpdatesLayout_AndUndoRestoresPositionAndAutoHeight() => WithEditor(async (editor, _) =>
    {
        Invoke(editor, "ResizeGraphNode", "dialogue", new Rect(325, 55, 1, 5000), true);
        var node = editor.Document!.Graph!.Nodes[1];
        Assert.Equal((325d, 55d, 160d, (double?)4096), (node.X, node.Y, node.Width, node.Height));
        var card = Cards(editor).Single(card => Equals(card.Tag, "dialogue"));
        Assert.Equal(160, card.Width); Assert.Equal(4096, card.Height);
        Assert.True(((Canvas)editor.FindName("GraphCanvas")).Height >= node.Y + node.Height + 200);
        Click(editor, "UndoButton"); node = editor.Document.Graph.Nodes[1];
        Assert.Equal((330d, 60d, 210d, (double?)null), (node.X, node.Y, node.Width, node.Height));
        Click(editor, "RedoButton"); Assert.Equal(4096, editor.Document.Graph.Nodes[1].Height);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Cards_HaveGroupShadow_OppositeIconPorts_AndEightResizeCursors() => WithEditor(async (editor, _) =>
    {
        Layout(editor);
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        var card = Cards(editor).Single(card => Equals(card.Tag, "dialogue"));
        var shadow = Assert.IsType<DropShadowEffect>(card.Effect);
        Assert.Equal(((SolidColorBrush)card.BorderBrush).Color, shadow.Color);
        Assert.True(card.CornerRadius.TopLeft > 0); Assert.InRange(shadow.Opacity, .01, .35);
        var ports = Ports(card).ToArray(); Assert.Equal(2, ports.Length);
        var input = ports.Single(p => Equals(p.Content, "Вход"));
        var output = ports.Single(p => !Equals(p.Content, "Вход"));
        Assert.NotNull(input.Template); Assert.NotNull(output.Template);
        Assert.True(input.TranslatePoint(new Point(11, 11), canvas).X < output.TranslatePoint(new Point(11, 11), canvas).X);
        var handles = canvas.Children.OfType<Thumb>().ToArray(); Assert.Equal(24, handles.Length);
        Assert.Equal(new[] { Cursors.SizeWE, Cursors.SizeNS, Cursors.SizeNWSE, Cursors.SizeNESW }.OrderBy(c => c.ToString()),
            handles.Select(h => h.Cursor).Distinct().OrderBy(c => c.ToString()));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Connections_NormalizeEitherDragDirection_RejectInvalidEndpoints_AndRetainClickLinking() => WithEditor(async (editor, _) =>
    {
        Assert.True((bool)Invoke(editor, "ConnectGraphPorts", "start", 0, "end", null)!);
        Assert.Equal("end", editor.Document!.Graph!.Nodes[0].Next);
        Assert.True((bool)Invoke(editor, "ConnectGraphPorts", "dialogue", null, "start", 0)!);
        Assert.Equal("dialogue", editor.Document.Graph.Nodes[0].Next);
        string before = editor.Document.Graph.Serialize();
        foreach (var args in new object?[][]
        {
            ["start", 0, "dialogue", 0], ["dialogue", null, "end", null], ["dialogue", 0, "dialogue", null],
            ["dialogue", 0, "start", null], ["start", 99, "end", null], ["missing", 0, "end", null]
        }) Assert.False((bool)Invoke(editor, "ConnectGraphPorts", args)!);
        Assert.Equal(before, editor.Document.Graph.Serialize());
        var output = Ports(Cards(editor).Single(c => Equals(c.Tag, "start"))).Single();
        output.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ports(Cards(editor).Single(c => Equals(c.Tag, "end"))).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("end", editor.Document.Graph.Nodes[0].Next);
        Click(editor, "UndoButton"); Assert.Equal("dialogue", editor.Document.Graph.Nodes[0].Next);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Curves_AnchorToActualPorts_AfterResizeAndMove() => WithEditor(async (editor, _) =>
    {
        Invoke(editor, "ResizeGraphNode", "dialogue", new Rect(380, 180, 420, 260), true);
        Layout(editor); Invoke(editor, "RenderLinks");
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        var link = canvas.Children.OfType<System.Windows.Shapes.Path>().Select(p => (PathGeometry)p.Data)
            .Select(geometry => geometry.Figures.Single()).Single(figure => figure.StartPoint.X > 700);
        var output = Ports(Cards(editor).Single(c => Equals(c.Tag, "dialogue"))).Single(p => !Equals(p.Content, "Вход"));
        var input = Ports(Cards(editor).Single(c => Equals(c.Tag, "end"))).Single();
        Assert.Equal(output.TranslatePoint(new Point(11, 11), canvas), link.StartPoint);
        var segment = Assert.IsType<BezierSegment>(link.Segments.Single());
        Assert.Equal(input.TranslatePoint(new Point(11, 11), canvas), segment.Point3);
        Assert.True(segment.Point1.X > link.StartPoint.X); Assert.True(segment.Point2.X < segment.Point3.X);
        await Task.CompletedTask;
    });

    [Fact]
    public Task MovingCard_UpdatesCurveBeforeDeferredWpfArrange() => WithEditor(async (editor, _) =>
    {
        Layout(editor); Invoke(editor, "RenderLinks");
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        Point Before() => canvas.Children.OfType<System.Windows.Shapes.Path>().Select(path => (PathGeometry)path.Data)
            .Select(geometry => geometry.Figures.Single().StartPoint).OrderByDescending(point => point.X).First();
        Point first = Before();
        var node = editor.Document!.Graph!.Nodes[1]; node.X += 70; node.Y += 35;
        var card = Cards(editor).Single(c => Equals(c.Tag, "dialogue"));
        Canvas.SetLeft(card, node.X); Canvas.SetTop(card, node.Y); Invoke(editor, "RenderLinks");
        Assert.Equal(first + new Vector(70, 35), Before());
        await Task.CompletedTask;
    });

    [Fact]
    public Task Comments_HaveNoPorts_ButRemainResizable() => WithEditor(async (editor, _) =>
    {
        editor.Document!.Graph!.Nodes.Add(new ScriptNode { Id = "note", Kind = ScriptNodeKind.Comment, Text = "Note" });
        Invoke(editor, "RenderGraph");
        Assert.Empty(Ports(Cards(editor).Single(c => Equals(c.Tag, "note"))));
        Invoke(editor, "ResizeGraphNode", "note", new Rect(10, 20, 550, 300), true);
        Assert.Equal(550, editor.Document.Graph.Nodes.Single(n => n.Id == "note").Width);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(-1, -1, 290, 270, 250, 150)]
    [InlineData(1, 1, 330, 300, 170, 100)]
    [InlineData(-1, 0, 290, 300, 250, 120)]
    [InlineData(0, -1, 330, 270, 210, 150)]
    public void ResizeEdges_PreserveOppositeEdgeAndUnaffectedAxis(int horizontal, int vertical,
        double x, double y, double width, double height)
    {
        var actual = (Rect)typeof(ScriptEditor).GetMethod("ResizeBounds", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Rect(330, 300, 210, 120), horizontal, vertical, new Vector(-40, -30)])!;
        Assert.Equal(new Rect(x, y, width, height), actual);
    }

    [Fact]
    public Task ResizeGesture_CommitsOnce_AndCancellationRestoresDraft() => WithEditor(async (editor, _) =>
    {
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        var handle = canvas.Children.OfType<Thumb>().Skip(8).First();
        handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Invoke(editor, "ResizeGraphNode", "dialogue", new Rect(330, 60, 300, 200), false);
        Invoke(editor, "ResizeGraphNode", "dialogue", new Rect(330, 60, 500, 250), false);
        handle.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Click(editor, "UndoButton"); Assert.Equal(210, editor.Document!.Graph!.Nodes[1].Width);
        Click(editor, "RedoButton"); Assert.Equal(500, editor.Document.Graph.Nodes[1].Width);
        handle = canvas.Children.OfType<Thumb>().Skip(8).First();
        handle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Invoke(editor, "ResizeGraphNode", "dialogue", new Rect(300, 50, 700, 500), false);
        handle.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal((330d, 60d, 500d, (double?)250),
            (editor.Document.Graph.Nodes[1].X, editor.Document.Graph.Nodes[1].Y, editor.Document.Graph.Nodes[1].Width, editor.Document.Graph.Nodes[1].Height));
        await Task.CompletedTask;
    });

    [Fact]
    public Task Canvas_RendersMixedGroupsAndResizedCards_WithPortsStillInteractive() => WithEditor(async (editor, _) =>
    {
        var host = new Window { Content = editor, Width = 1280, Height = 800, WindowStyle = WindowStyle.None,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
        host.Show();
        var graph = editor.Document!.Graph!;
        graph.Nodes[0].X = 35; graph.Nodes[0].Y = 45;
        var dialogue = graph.Nodes[1]; dialogue.X = 340; dialogue.Y = 45; dialogue.Width = 360; dialogue.Height = 220;
        graph.Nodes[2].X = 730; graph.Nodes[2].Y = 320;
        dialogue.Next = "choice";
        graph.Nodes.Add(new ScriptNode
        {
            Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Куда отправимся?", X = 45, Y = 320, Width = 260, Height = 200,
            Options = [new ScriptOption { Text = "К морю", Target = "end" }, new ScriptOption { Text = "В горы", Target = "end" }]
        });
        graph.Nodes.Add(new ScriptNode { Id = "note", Kind = ScriptNodeKind.Comment, Text = "Комментарий без портов. Потяните край, чтобы изменить размер.", X = 350, Y = 360, Width = 325, Height = 160 });
        Invoke(editor, "RenderGraph"); Layout(editor); Invoke(editor, "RenderLinks"); editor.UpdateLayout();
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        var renderedLinks = canvas.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        Assert.Equal(4, renderedLinks.Length);
        Assert.All(renderedLinks, link => Assert.True(link.ActualWidth > 0 && link.ActualHeight > 0));
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
        ((PathGeometry)renderedLinks[0].Data).GetPointAtFractionLength(.5, out Point midpoint, out Point tangent);
        Point renderedMidpoint = canvas.TranslatePoint(midpoint, editor);
        var pixels = new byte[7 * 7 * 4];
        bitmap.CopyPixels(new Int32Rect((int)renderedMidpoint.X - 3, (int)renderedMidpoint.Y - 3, 7, 7), pixels, 7 * 4, 0);
        Assert.Contains(Enumerable.Range(0, 49), pixel => pixels[pixel * 4 + 2] < 190 && pixels[pixel * 4 + 1] < 190);
        string? output = Environment.GetEnvironmentVariable("KNE_CANVAS_RENDER");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(output); encoder.Save(file);
        }
        foreach (var port in Cards(editor).SelectMany(Ports))
        {
            Point center = port.TranslatePoint(new Point(port.ActualWidth / 2, port.ActualHeight / 2), canvas);
            var hit = canvas.InputHitTest(center) as DependencyObject;
            string originalHit = hit?.GetType().Name ?? "null";
            while (hit != null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
            Assert.True(ReferenceEquals(port, hit), $"Port {port.Content} at {center} (card {((Border)((StackPanel)((Grid)port.Parent).Parent).Parent).Tag}) hit {originalHit}; visible={port.IsVisible}");
        }
        await Task.CompletedTask;
        }
        finally { host.Content = null; host.Close(); }
    });

    private static IEnumerable<Border> Cards(ScriptEditor editor) => ((Canvas)editor.FindName("GraphCanvas")).Children.OfType<Border>();
    private static IEnumerable<Button> Ports(Border card) => ((StackPanel)card.Child).Children.OfType<Grid>()
        .SelectMany(grid => grid.Children.OfType<Button>()).Where(button => button.GetType().Name == "GraphPort");
    private static object? Invoke(object owner, string method, params object?[] args) => owner.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Click(ScriptEditor editor, string name) => ((Button)editor.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(ScriptEditor editor)
    {
        editor.Measure(new Size(1280, 800)); editor.Arrange(new Rect(0, 0, 1280, 800)); editor.UpdateLayout();
    }
    private static Task WithEditor(Func<ScriptEditor, TempProject, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                using var project = new TempProject(); var editor = new ScriptEditor();
                try
                {
                    string path = project.Write("game/scenes/canvas.knegraph", VisualScript.Create().Serialize());
                    await editor.OpenAsync(project.ProjectPath, path); await action(editor, project); completion.SetResult();
                }
                catch (Exception error) { completion.SetException(error); }
                finally { editor.Clear(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
