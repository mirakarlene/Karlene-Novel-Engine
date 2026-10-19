using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace KNE;

public sealed class SceneParametersWindow : Window
{
    private readonly ProjectInfo project;
    private readonly ScenePerformanceDocument document;
    internal ComboBox SceneInput { get; } = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new(0, 8, 0, 16) };
    internal ComboBox QualityInput { get; } = GraphicsSettingsEditor.Choice(ScenePerformanceSettings.Qualities);
    internal CheckBox PostProcessingInput { get; } = new() { Content = "Включить постобработку (вспышка, тряска)", Margin = new(0, 10, 0, 16) };
    private readonly StackPanel options = new() { IsEnabled = false };
    private readonly TextBlock metrics = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private string? selected;
    public SceneParametersWindow(ProjectInfo project)
    {
        this.project = project; document = PerformanceSettingsStore.LoadScenes(project.Path); Title = "Параметры сцены · " + project.Title; Width = 700; Height = 530; MinWidth = 500; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new(24) }; body.Children.Add(new TextBlock { Text = "Выберите сцену (.krl или .knegraph)" }); body.Children.Add(SceneInput);
        SceneInput.ItemsSource = project.Files.Where(file => ScenePerformanceDocument.IsScenePath(file.FilePath)).Select(file => { string path = Path.GetRelativePath(project.Path, file.FilePath).Replace('\\', '/'); return new GraphicsOption<string>(path, path); }).ToArray();
        options.Children.Add(new TextBlock { Text = "Качество эффектов" }); options.Children.Add(QualityInput); options.Children.Add(PostProcessingInput); options.Children.Add(metrics); body.Children.Add(options);
        options.Children.Add(new TextBlock { Text = "Среднее: меньше сегментов узора. Низкое: статичный узор и эффекты без анимации. Постобработка управляет вспышками и тряской, не меняя сценарий.", TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new(0, 0, 0, 12) });
        SceneInput.SelectionChanged += (_, _) => { StoreSelection(); selected = SceneInput.SelectedValue as string; options.IsEnabled = selected != null; var value = document.For(selected); QualityInput.SelectedValue = value.EffectsQuality; PostProcessingInput.IsChecked = value.PostProcessing; metrics.Text = SceneMetrics.Describe(project.Path, selected); };
        var save = new Button { Content = "Сохранить", HorizontalAlignment = HorizontalAlignment.Left }; body.Children.Add(save);
        save.Click += async (_, _) => { try { await SaveAsync(); DialogResult = true; } catch (Exception error) { metrics.Text = error.Message; } };
        Content = body;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (selected != null) metrics.Text = SceneMetrics.Describe(project.Path, selected); };
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop();
    }
    private void StoreSelection() { if (selected != null) document.Scenes[selected] = new() { EffectsQuality = (string?)QualityInput.SelectedValue ?? "high", PostProcessing = PostProcessingInput.IsChecked == true }; }
    internal Task SaveAsync() { StoreSelection(); return PerformanceSettingsStore.SaveScenesAsync(project.Path, document); }
}

internal static class SceneMetrics
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, (double Ms, int? Draws, int Objects)> values = new(StringComparer.OrdinalIgnoreCase);
    internal static void Record(string root, string scene, double milliseconds, int? draws, int objects)
    {
        lock (sync)
        {
            string key = Path.Combine(root, scene);
            if (values.Count >= 512 && !values.ContainsKey(key)) values.Remove(values.Keys.First());
            values[key] = (milliseconds, draws, objects);
        }
    }
    internal static string Describe(string root, string? scene)
    {
        lock (sync)
            return scene != null && values.TryGetValue(Path.Combine(root, scene), out var value)
                ? $"Логика: {value.Ms:F2} мс\nDraw Calls графического слоя: {(value.Draws?.ToString() ?? "н/д (WPF)")}\nОбъектов сцены: {value.Objects}" + (value.Objects > 100 ? "\nБольше 100 объектов: проверьте возможность объединения спрайтов." : "")
                : "Замеры появятся после запуска этой сцены. Draw Calls WPF и общая VRAM недоступны; они не подменяются количеством объектов.";
    }
}
