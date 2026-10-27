using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

internal sealed class RemoteDebugView : UserControl, IAsyncDisposable
{
    internal RemoteDebugConnection Connection { get; } = new();
    internal TextBox Variables { get; } = new() { IsReadOnly = true, FontFamily = new("Consolas"), TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal TextBlock Status { get; } = new() { Text = "Ожидание запуска игры и Lua worker…", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    internal ListBox Breakpoints { get; } = new() { MinHeight = 70, MaxHeight = 180 };
    internal ComboBox Files { get; } = new() { MinWidth = 100, MaxWidth = 380 };
    internal TextBox Line { get; } = new() { Text = "1", Width = 72 };
    internal ComboBox Nodes { get; } = new() { MinWidth = 120, MaxWidth = 340, DisplayMemberPath = "Label", SelectedValuePath = "Value", Visibility = Visibility.Collapsed };
    private readonly List<DebugBreakpoint> points = [];
    private readonly ProjectInfo project;
    private PreviewLocation? location;
    private bool disposed;
    internal event Action<PreviewLocation>? SourceRequested;

    internal RemoteDebugView(ProjectInfo project)
    {
        this.project = project;
        var root = new DockPanel { Margin = new(12) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        var controls = new WrapPanel();
        foreach (var (label, command) in new[] { ("Пауза", "pause"), ("Продолжить", "continue"), ("Шаг", "step") })
        { var button = Action(label); button.Click += async (_, _) => await GuardAsync(() => Connection.SendAsync(command)); controls.Children.Add(button); }
        var source = Action("Открыть место остановки"); source.Click += (_, _) => { if (location != null) SourceRequested?.Invoke(location); }; controls.Children.Add(source); top.Children.Add(controls); top.Children.Add(Status);
        top.Children.Add(new TextBlock { Text = "Точки останова · строки .krl / блоки .knegraph", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        Files.ItemsSource = project.Files.Where(file => ScenePerformanceDocument.IsScenePath(file.FilePath)).Select(file => Path.GetRelativePath(project.Path, file.FilePath).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var addRow = new WrapPanel(); addRow.Children.Add(Files); Line.Margin = Nodes.Margin = new(8, 0, 8, 0); addRow.Children.Add(Line); addRow.Children.Add(Nodes);
        var add = Action("Добавить"); add.Click += async (_, _) => await GuardAsync(AddBreakpointAsync); addRow.Children.Add(add);
        var remove = Action("Удалить выбранную"); remove.Click += async (_, _) => await GuardAsync(async () => { if (Breakpoints.SelectedItem is DebugBreakpoint point) { points.Remove(point); await UpdateBreakpointsAsync(); } }); addRow.Children.Add(remove);
        top.Children.Add(addRow); top.Children.Add(Breakpoints);
        top.Children.Add(new TextBlock { Text = "Игра останавливается перед первой исполняемой строкой или блоком. «Шаг» выполняет до следующей строки / блока; ответы и переход к следующей реплике выбираются в окне игры. Список точек действует в текущем сеансе.", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new(0, 7, 0, 10) });
        root.Children.Add(top); root.Children.Add(Variables); Content = root;
        Files.SelectionChanged += (_, _) =>
        {
            try
            {
                bool visual = Files.SelectedItem is string file && ScriptDocument.IsVisualPath(file);
                Line.Visibility = visual ? Visibility.Collapsed : Visibility.Visible; Nodes.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
                if (visual) { var graph = VisualScript.Parse(File.ReadAllText(PathSafety.ResolveRelative(project.Path, (string)Files.SelectedItem))); Nodes.ItemsSource = graph.Nodes.Select(node => new GraphicsOption<string>(node.Id, node.Kind + " · " + node.Id)).ToArray(); Nodes.SelectedIndex = 0; }
            }
            catch (Exception error) { Status.Text = error.Message; }
        };
        if (Files.Items.Count > 0) Files.SelectedIndex = 0;
        Connection.Changed += Present;
    }
    private static Button Action(string label) => new() { Content = label, Padding = new(10, 5, 10, 5), Margin = new(0, 0, 8, 4) };
    internal async Task AddBreakpointAsync()
    {
        if (Files.SelectedItem is not string file) throw new InvalidDataException("Выберите сцену.");
        DebugBreakpoint point;
        if (ScriptDocument.IsVisualPath(file)) point = new(file, NodeId: (string?)Nodes.SelectedValue ?? throw new InvalidDataException("Выберите блок."));
        else
        {
            if (!int.TryParse(Line.Text, out int line) || line < 1 || line > File.ReadLines(PathSafety.ResolveRelative(project.Path, file)).Count()) throw new InvalidDataException("Укажите существующую строку .krl.");
            point = new(file, line);
        }
        point.Validate(); if (!points.Contains(point)) points.Add(point); await UpdateBreakpointsAsync();
    }
    private Task UpdateBreakpointsAsync() { Breakpoints.ItemsSource = points.ToArray(); return Connection.SetBreakpointsAsync(points.ToArray()); }
    private async Task GuardAsync(Func<Task> operation) { try { await operation(); } catch (Exception error) { Status.Text = error.Message; } }
    private void Present(DebugSnapshot snapshot)
    {
        if (disposed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            Status.Text = snapshot.Status + (snapshot.Location is { } place ? " · " + place.File + ":" + place.Line + (place.NodeId == null ? "" : " · " + place.NodeId) : "");
            if (snapshot.Location != null) location = snapshot.Location;
            if (snapshot.Variables.ValueKind == JsonValueKind.Object) Variables.Text = JsonSerializer.Serialize(snapshot.Variables, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        });
    }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; Connection.Changed -= Present; await Connection.DisposeAsync(); }
}
