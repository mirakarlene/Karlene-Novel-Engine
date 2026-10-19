using System.IO;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class PerformanceWindowsTests
{
    [Fact]
    public async Task ProjectParameters_ExposeSavedValuesAndPersistEveryEditedSwitchWithoutChangingVisualLayouts()
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { FpsCorner = "bottom_right", Graphics = new() { Api = "opengl", MaxFps = 30, MsaaSamples = 2 } });
        var project = await fixture.LoadAsync(); string screens = File.ReadAllText(Path.Combine(project.Path, "engine", "screens.json"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new ProjectParametersWindow(project);
            try
            {
                Assert.Equal(new GraphicsSettings { Api = "opengl", MaxFps = 30, MsaaSamples = 2 }, window.GraphicsInput.Value);
                Assert.Equal("bottom_right", window.CornerInput.SelectedValue); Assert.Equal("saves", window.SavesInput.Text);
                window.GraphicsInput.Value = new() { Api = "vulkan", MaxFps = 144, MsaaSamples = 4, VSync = false };
                window.CornerInput.SelectedValue = "top_left"; window.SavesInput.Text = "  progress/slots  ";
                var body = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(window.Content).Content);
                var toggles = body.Children.OfType<CheckBox>().ToDictionary(input => (string)input.Content);
                Assert.Equal(5, toggles.Count);
                toggles["Счётчик FPS"].IsChecked = true; toggles["Оверлей статистики (RAM, GPU, Draw Calls)"].IsChecked = true;
                toggles["Уведомление загрузки в углу"].IsChecked = false; toggles["Режим отладки — консоль всегда открыта"].IsChecked = true;
                toggles["Предзагрузка ресурсов следующей сцены"].IsChecked = false;
                Capture(window, "project-parameters.png");
                await window.SaveAsync();
                var saved = PerformanceSettingsStore.LoadProject(project.Path);
                Assert.Equal(new GraphicsSettings { Api = "vulkan", MaxFps = 144, MsaaSamples = 4, VSync = false }, saved.Graphics);
                Assert.True(saved.FpsCounter); Assert.Equal("top_left", saved.FpsCorner); Assert.True(saved.StatisticsOverlay); Assert.False(saved.LoadingIndicator);
                Assert.True(saved.DebugMode); Assert.False(saved.Preloading); Assert.Equal("progress/slots", saved.SavesPath);
                Assert.Equal(screens, File.ReadAllText(Path.Combine(project.Path, "engine", "screens.json")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ProjectParameters_CancelDiscardsDraftAndInvalidSaveCannotOverwritePreviousSettings()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string path = Path.Combine(project.Path, "engine", "performance.json"), original = File.ReadAllText(path);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var canceled = new ProjectParametersWindow(project);
            canceled.GraphicsInput.Value = new() { Api = "vulkan", MaxFps = 0 }; canceled.SavesInput.Text = "draft";
            var cancel = Assert.Single(Descendants(canceled.Content).OfType<Button>(), input => Equals(input.Content, "Отмена"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, File.ReadAllText(path));
            var invalid = new ProjectParametersWindow(project);
            try
            {
                invalid.SavesInput.Text = "../outside";
                await Assert.ThrowsAsync<ArgumentException>(async () => await invalid.SaveAsync());
                Assert.Equal(original, File.ReadAllText(path)); Assert.Equal("../outside", invalid.SavesInput.Text);
            }
            finally { invalid.Close(); }
        });
    }

    [Fact]
    public async Task SceneParameters_RequireGraphSelectionPreserveDraftsWhenSwitchingAndSaveOnlySceneOverrides()
    {
        using var fixture = new TempProject();
        fixture.Write("one.knegraph", VisualScript.Create().Serialize()); fixture.Write("nested/two.knegraph", VisualScript.Create().Serialize()); fixture.Write("notes.txt", "not a scene");
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["unopened.knegraph"] = new() { EffectsQuality = "medium", PostProcessing = false } } });
        var project = await fixture.LoadAsync(); string global = File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new SceneParametersWindow(project);
            try
            {
                Assert.Equal(-1, window.SceneInput.SelectedIndex); Assert.False(window.QualityInput.IsEnabled); Assert.False(window.PostProcessingInput.IsEnabled);
                Assert.Equal(new[] { "nested/two.knegraph", "one.knegraph" }, window.SceneInput.Items.Cast<GraphicsOption<string>>().Select(item => item.Value).Order());
                window.SceneInput.SelectedValue = "one.knegraph";
                Assert.True(window.QualityInput.IsEnabled); Assert.True(window.PostProcessingInput.IsEnabled); Assert.Equal("high", window.QualityInput.SelectedValue); Assert.True(window.PostProcessingInput.IsChecked);
                window.QualityInput.SelectedValue = "low"; window.PostProcessingInput.IsChecked = false;
                window.SceneInput.SelectedValue = "nested/two.knegraph";
                Assert.Equal("high", window.QualityInput.SelectedValue); Assert.True(window.PostProcessingInput.IsChecked);
                window.QualityInput.SelectedValue = "medium";
                window.SceneInput.SelectedValue = "one.knegraph";
                Assert.Equal("low", window.QualityInput.SelectedValue); Assert.False(window.PostProcessingInput.IsChecked);
                Capture(window, "scene-parameters.png");
                await window.SaveAsync();
                var saved = PerformanceSettingsStore.LoadScenes(project.Path);
                Assert.Equal(3, saved.Scenes.Count); Assert.Equal("low", saved.For("one.knegraph").EffectsQuality); Assert.False(saved.For("one.knegraph").PostProcessing);
                Assert.Equal("medium", saved.For("nested/two.knegraph").EffectsQuality); Assert.True(saved.For("nested/two.knegraph").PostProcessing);
                Assert.Equal("medium", saved.For("unopened.knegraph").EffectsQuality); Assert.False(saved.For("unopened.knegraph").PostProcessing);
                Assert.Equal(global, File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SceneParameters_CloseDiscardsAllSwitchedDraftsAndEmptyProjectKeepsControlsDisabled()
    {
        using var fixture = new TempProject(); fixture.Write("one.knegraph", VisualScript.Create().Serialize()); fixture.Write("two.knegraph", VisualScript.Create().Serialize());
        var project = await fixture.LoadAsync(); string path = Path.Combine(project.Path, "engine", "scene-performance.json"), original = File.ReadAllText(path);
        await WorkspaceTabsTests.Sta(() =>
        {
            var canceled = new SceneParametersWindow(project);
            canceled.SceneInput.SelectedValue = "one.knegraph"; canceled.QualityInput.SelectedValue = "low";
            canceled.SceneInput.SelectedValue = "two.knegraph"; canceled.PostProcessingInput.IsChecked = false; canceled.Close();
            Assert.Equal(original, File.ReadAllText(path));
            return Task.CompletedTask;
        });
        File.Delete(Path.Combine(project.Path, "one.knegraph")); File.Delete(Path.Combine(project.Path, "two.knegraph")); project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var empty = new SceneParametersWindow(project);
            try { Assert.Empty(empty.SceneInput.Items); Assert.False(empty.QualityInput.IsEnabled); Assert.False(empty.PostProcessingInput.IsEnabled); await empty.SaveAsync(); Assert.Empty(PerformanceSettingsStore.LoadScenes(project.Path).Scenes); }
            finally { empty.Close(); }
        });
    }

    [Fact]
    public void SceneMetrics_SeparateMeasuredDrawsFromObjectCountsAndScopeValuesToProjectAndScene()
    {
        using var first = new TempProject(); using var second = new TempProject();
        Assert.Contains("Замеры появятся", SceneMetrics.Describe(first.ProjectPath, "one.knegraph"));
        SceneMetrics.Record(first.ProjectPath, "one.knegraph", 12.5, null, 100);
        string measured = SceneMetrics.Describe(first.ProjectPath, "one.knegraph");
        Assert.Contains(12.5.ToString("F2") + " мс", measured); Assert.Contains("н/д (WPF)", measured); Assert.Contains("Объектов сцены: 100", measured); Assert.DoesNotContain("Больше 100", measured);
        SceneMetrics.Record(first.ProjectPath, "one.knegraph", 3.25, 7, 101);
        measured = SceneMetrics.Describe(first.ProjectPath, "one.knegraph");
        Assert.Contains("Draw Calls графического слоя: 7", measured); Assert.Contains("Объектов сцены: 101", measured); Assert.Contains("Больше 100 объектов", measured);
        Assert.Contains("Замеры появятся", SceneMetrics.Describe(first.ProjectPath, "two.knegraph")); Assert.Contains("Замеры появятся", SceneMetrics.Describe(second.ProjectPath, "one.knegraph"));
    }

    private static IEnumerable<DependencyObject> Descendants(object root)
    {
        if (root is not DependencyObject owner) yield break;
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject item) { yield return item; foreach (var nested in Descendants(item)) yield return nested; }
    }
    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER"); if (directory == null) return;
        EditorTheme.Apply(window, "kne");
        var view = (FrameworkElement)window.Content; var size = new Size(window.Width, window.Height - 40);
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        { drawing.DrawRectangle(window.Background, null, new Rect(size)); drawing.DrawRectangle(new System.Windows.Media.VisualBrush(view), null, new Rect(size)); }
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        Directory.CreateDirectory(directory); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
