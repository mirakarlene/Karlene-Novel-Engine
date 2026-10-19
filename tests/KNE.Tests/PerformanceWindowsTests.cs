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
                window.GraphicsInput.Value = new() { Api = "vulkan", MaxFps = 144, MsaaSamples = 4, TextureQuality = "quarter", VSync = false, LimitResourceCache = false, ResourceCacheLimitMb = 128 };
                Assert.Equal(project.Title, window.TitleInput.Text); Assert.True(window.TitleInput.IsReadOnly);
                Assert.Single(Descendants(window.Content).OfType<Button>(), input => Equals(input.Content, "Переименовать")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(window.TitleInput.IsReadOnly); window.TitleInput.Text = "  Новое название  ";
                window.AsyncTasksInput.SelectedValue = 32; window.AudioBufferInput.SelectedValue = "low_latency";
                window.CornerInput.SelectedValue = "top_left"; window.SavesInput.Text = "  progress/slots  ";
                var toggles = Descendants(window.Content).OfType<CheckBox>().Where(input => !ReferenceEquals(input, window.GraphicsInput.VSyncInput) && !ReferenceEquals(input, window.GraphicsInput.LimitCacheInput)).ToDictionary(input => (string)input.Content);
                Assert.Equal(6, toggles.Count);
                toggles["Счётчик FPS"].IsChecked = true; toggles["Оверлей статистики (RAM, GPU, Draw Calls)"].IsChecked = true;
                toggles["Уведомление загрузки в углу"].IsChecked = false; toggles["Режим отладки (консоль при запуске)"].IsChecked = true;
                toggles["Предзагрузка ресурсов следующей сцены"].IsChecked = false; toggles["Выгружать ресурсы прошлой сцены"].IsChecked = true;
                Capture(window, "project-parameters.png");
                await window.SaveAsync();
                var saved = PerformanceSettingsStore.LoadProject(project.Path);
                Assert.Equal(new GraphicsSettings { Api = "vulkan", MaxFps = 144, MsaaSamples = 4, TextureQuality = "quarter", VSync = false, LimitResourceCache = false, ResourceCacheLimitMb = 128 }, saved.Graphics);
                Assert.Equal("Новое название", project.Title); Assert.Equal("Новое название", (await fixture.LoadAsync()).Title);
                Assert.Equal(fixture.ProjectPath, project.Path); Assert.True(saved.UnloadPreviousScene); Assert.Equal(32, saved.MaxAsyncTasks); Assert.Equal("low_latency", saved.AudioBuffer);
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
        string manifest = Path.Combine(project.Path, "project.json"), originalManifest = File.ReadAllText(manifest), originalTitle = project.Title;
        await WorkspaceTabsTests.Sta(async () =>
        {
            var canceled = new ProjectParametersWindow(project);
            canceled.GraphicsInput.Value = new() { Api = "vulkan", MaxFps = 0, TextureQuality = "half" }; canceled.SavesInput.Text = "draft";
            var rename = Assert.Single(Descendants(canceled.Content).OfType<Button>(), input => Equals(input.Content, "Переименовать"));
            rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); canceled.TitleInput.Text = "  Cancelled title  ";
            rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(canceled.TitleInput.IsReadOnly); Assert.Equal("Cancelled title", canceled.TitleInput.Text);
            Assert.Equal(originalManifest, File.ReadAllText(manifest));
            canceled.AsyncTasksInput.SelectedValue = 64; canceled.AudioBufferInput.SelectedValue = "stable";
            var cancel = Assert.Single(Descendants(canceled.Content).OfType<Button>(), input => Equals(input.Content, "Отмена"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(originalManifest, File.ReadAllText(manifest)); Assert.Equal(originalTitle, project.Title);
            var invalid = new ProjectParametersWindow(project);
            try
            {
                invalid.SavesInput.Text = "../outside"; invalid.TitleInput.Text = "Invalid draft title";
                await Assert.ThrowsAsync<ArgumentException>(async () => await invalid.SaveAsync());
                Assert.Equal(original, File.ReadAllText(path)); Assert.Equal("../outside", invalid.SavesInput.Text);
                Assert.Equal(originalManifest, File.ReadAllText(manifest)); Assert.Equal(originalTitle, project.Title);
            }
            finally { invalid.Close(); }
        });
    }

    [Fact]
    public async Task SceneParameters_RequireCodeOrGraphSelectionPreserveDraftsWhenSwitchingAndSaveOnlySceneOverrides()
    {
        using var fixture = new TempProject();
        fixture.Write("one.knegraph", VisualScript.Create().Serialize()); fixture.Write("nested/two.knegraph", VisualScript.Create().Serialize()); fixture.Write("notes.txt", "not a scene");
        fixture.Write("nested/chapter.krl", "function chapter() d(nil, 'Code scene') end"); fixture.Write("helper.lua", "function helper() end");
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["unopened.knegraph"] = new() { EffectsQuality = "medium", PostProcessing = false } } });
        var project = await fixture.LoadAsync(); string global = File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new SceneParametersWindow(project);
            try
            {
                Assert.Equal(-1, window.SceneInput.SelectedIndex); Assert.False(window.QualityInput.IsEnabled); Assert.False(window.PostProcessingInput.IsEnabled);
                string[] choices = window.SceneInput.Items.Cast<GraphicsOption<string>>().Select(item => item.Value).Order().ToArray();
                Assert.Contains("nested/chapter.krl", choices); Assert.Contains("nested/two.knegraph", choices); Assert.Contains("one.knegraph", choices);
                Assert.DoesNotContain("helper.lua", choices); Assert.DoesNotContain("notes.txt", choices);
                window.SceneInput.SelectedValue = "nested/chapter.krl";
                Assert.True(window.QualityInput.IsEnabled); window.QualityInput.SelectedValue = "low"; window.PostProcessingInput.IsChecked = false;
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
                Assert.Equal(4, saved.Scenes.Count); Assert.Equal("low", saved.For("one.knegraph").EffectsQuality); Assert.False(saved.For("one.knegraph").PostProcessing);
                Assert.Equal("low", saved.For("NESTED/CHAPTER.KRL").EffectsQuality); Assert.False(saved.For("nested/chapter.krl").PostProcessing);
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
        File.Delete(Path.Combine(project.Path, "one.knegraph")); File.Delete(Path.Combine(project.Path, "two.knegraph"));
        foreach (var file in project.Files.Where(file => Path.GetExtension(file.FilePath).Equals(".krl", StringComparison.OrdinalIgnoreCase))) File.Delete(file.FilePath);
        project = await fixture.LoadAsync();
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
