using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KNE;

/// <summary>Isolated shader preview with externally adjustable parameters and an optional project texture.</summary>
internal sealed class ShaderPreviewWindow : Window
{
    private readonly ProjectInfo project;
    private readonly VisualScript graph;
    private readonly string path;
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly StackPanel inputs = new();
    private readonly Dictionary<string, double[]> overrides = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private ShaderGpuSession? gpu;
    private BitmapSource source = Sample();
    private CompiledShaderGraph? program;
    private bool busy, closed;
    private int revision;
    private int programRevision;
    private int renderedRevision = -1;
    private BitmapSource? renderedSource;
    private Task? closing;
    internal string ProjectPath => project.Path;
    internal async Task CloseForProjectDeletionAsync() { Close(); if (closing != null) await closing; }
    private async Task ReleaseAsync() { if (gpu != null) await gpu.DisposeAsync(); lifetime.Dispose(); }
    internal ShaderPreviewWindow(ProjectInfo project, VisualScript graph, string path)
    {
        this.project = project; this.graph = VisualScript.Parse(graph.Serialize()); this.path = path;
        Title = "Предпросмотр шейдера · " + Path.GetFileName(path); Width = 980; Height = 680; MinWidth = 700; MinHeight = 500;
        var root = new Grid { Margin = new(20) }; root.ColumnDefinitions.Add(new()); root.ColumnDefinitions.Add(new() { Width = new(260) });
        var canvas = new Grid { Margin = new(0, 0, 16, 0), Background = Brushes.Black }; canvas.Children.Add(image); root.Children.Add(canvas);
        var sidebar = new DockPanel(); Grid.SetColumn(sidebar, 1); root.Children.Add(sidebar);
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Top); sidebar.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = "Внешние параметры", FontSize = 20, FontWeight = FontWeights.Bold, Margin = new(0, 0, 0, 8) });
        controls.Children.Add(new TextBlock { Text = "Изменения здесь влияют только на предпросмотр. Параметры файла задаются в свойствах блоков.", TextWrapping = TextWrapping.Wrap, Opacity = .7, Margin = new(0, 0, 0, 12) });
        var choose = new Button { Content = "Выбрать текстуру…", Margin = new(0, 0, 0, 8) }; controls.Children.Add(choose);
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { InitialDirectory = project.Path, Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (dialog.ShowDialog(this) != true) return;
            try { var cache = new SceneAssetCache(); cache.Configure(project.Engine.Performance.Graphics); source = cache.Load(dialog.FileName); }
            catch (Exception error) { status.Text = error.Message; }
        };
        DockPanel.SetDock(status, Dock.Bottom); sidebar.Children.Add(status);
        sidebar.Children.Add(new ScrollViewer { Content = inputs, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
        Content = root;
        Loaded += async (_, _) => { await CompileAsync(true); if (!closed && program != null) timer.Start(); };
        timer.Tick += Render; debounce.Tick += async (_, _) => { debounce.Stop(); await CompileAsync(false); };
        Closed += async (_, _) => { closed = true; revision++; lifetime.Cancel(); timer.Stop(); debounce.Stop(); image.Source = null; closing = ReleaseAsync(); await closing; };
    }
    private async Task CompileAsync(bool buildInputs)
    {
        int version = ++revision;
        try
        {
            var snapshot = overrides.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            var compiled = await Task.Run(() => ShaderGraphCompiler.Compile(graph, project.Path, path, snapshot), lifetime.Token);
            if (closed || version != revision) return;
            program = compiled; programRevision = version; status.Text = Path.GetFileName(path) + "\nAPI: " + project.Engine.Performance.Graphics.Api;
            if (buildInputs)
            {
                inputs.Children.Clear();
                foreach (var parameter in compiled.Parameters)
                {
                    inputs.Children.Add(new TextBlock { Text = parameter.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 4) });
                    overrides[parameter.Id] = parameter.Values.ToArray();
                    Border? swatch = null;
                    if (parameter.Kind == ScriptNodeKind.ShaderColor) { swatch = new Border { Height = 28, CornerRadius = new(4), Margin = new(0, 0, 0, 4) }; inputs.Children.Add(swatch); }
                    void Color() { if (swatch != null) { var v = overrides[parameter.Id]; swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(v[3] * 255), (byte)(v[0] * 255), (byte)(v[1] * 255), (byte)(v[2] * 255))); } }
                    Color();
                    for (int index = 0; index < parameter.Values.Length; index++)
                    {
                        int component = index;
                        string componentName = parameter.Values.Length == 1 ? "Значение" : parameter.Kind == ScriptNodeKind.ShaderColor ? "RGBA"[index].ToString() : "XYZW"[index].ToString();
                        var label = new TextBlock { Text = componentName + " · " + parameter.Values[index].ToString("0.###"), FontSize = 11 };
                        inputs.Children.Add(label);
                        var slider = new Slider { Minimum = parameter.Minimum, Maximum = parameter.Maximum, Value = parameter.Values[index], Margin = new(0, 0, 0, 8) };
                        slider.ValueChanged += (_, _) => { overrides[parameter.Id][component] = slider.Value; label.Text = componentName + " · " + slider.Value.ToString("0.###"); Color(); debounce.Stop(); debounce.Start(); };
                        inputs.Children.Add(slider);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { status.Text = path + "\n" + error.GetBaseException().Message; }
    }
    private async void Render(object? sender, EventArgs args)
    {
        if (closed || busy || program == null || programRevision != revision || !IsVisible || WindowState == WindowState.Minimized) return;
        if (!program.Animated && renderedRevision == revision && ReferenceEquals(renderedSource, source)) return;
        busy = true; int version = programRevision;
        try
        {
            gpu ??= new();
            var output = await gpu.RenderAsync(source, [program], project.Engine.Performance.Graphics, (float)clock.Elapsed.TotalSeconds, token: lifetime.Token);
            if (!closed && version == revision) { image.Source = output; renderedRevision = version; renderedSource = source; }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { timer.Stop(); status.Text = path + "\n" + error.GetBaseException().Message; }
        finally { busy = false; }
    }
    private static BitmapSource Sample()
    {
        const int width = 512, height = 320;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = (y * width + x) * 4; bool tile = (x / 32 + y / 32) % 2 == 0; pixels[i] = (byte)(x * 255 / width); pixels[i + 1] = (byte)(y * 255 / height); pixels[i + 2] = tile ? (byte)220 : (byte)60; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4); image.Freeze(); return image;
    }
}
