using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class CompactProjectParametersTests
{
    [Fact]
    public async Task Defaults_LegacyProjectFilesKeepOriginalTexturesAndBalancedPerformanceWithoutRewriting()
    {
        using var fixture = new TempProject();
        string file = fixture.Write("engine/performance.json", "{\"graphics\":{\"api\":\"opengl\"},\"preloading\":false}");
        string before = File.ReadAllText(file);
        var settings = PerformanceSettingsStore.LoadProject(fixture.ProjectPath);
        Assert.Equal("original", settings.Graphics.TextureQuality);
        Assert.False(settings.UnloadPreviousScene); Assert.Equal(128, settings.MaxAsyncTasks); Assert.Equal("balanced", settings.AudioBuffer);
        Assert.Equal("opengl", settings.Graphics.Api); Assert.False(settings.Preloading);
        var loaded = (await fixture.LoadAsync()).Engine.Performance;
        Assert.Equal("original", loaded.Graphics.TextureQuality); Assert.False(loaded.UnloadPreviousScene);
        Assert.Equal(128, loaded.MaxAsyncTasks); Assert.Equal("balanced", loaded.AudioBuffer);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public void Options_AdvertiseEveryTextureTaskAndAudioChoiceInTheRequestedOrder()
    {
        Assert.Equal(new[] { "original", "half", "quarter" }, GraphicsSettings.TextureQualities.Select(item => item.Value));
        Assert.Equal(new[] { 32, 64, 128 }, ProjectPerformanceSettings.AsyncTaskLimits.Select(item => item.Value));
        Assert.Equal(new[] { "low_latency", "balanced", "stable" }, ProjectPerformanceSettings.AudioBuffers.Select(item => item.Value));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Original")] [InlineData("eighth")]
    public void TextureQuality_InvalidDiscriminatorsAreRejectedAndNormalizeWithoutChangingOtherPreferences(string? quality)
    {
        var source = new GraphicsSettings { Api = "opengl", VSync = false, MaxFps = 144, MsaaSamples = 4,
            TextureQuality = quality!, LimitResourceCache = false, ResourceCacheLimitMb = 256 };
        Assert.Throws<InvalidDataException>(source.Validate);
        var normalized = GraphicsSettings.Normalize(source);
        Assert.Equal(source with { TextureQuality = "original" }, normalized);
        Assert.Equal(quality, source.TextureQuality);
    }

    [Theory]
    [InlineData(0)] [InlineData(31)] [InlineData(33)] [InlineData(63)] [InlineData(65)] [InlineData(127)] [InlineData(129)]
    public async Task AsyncTasks_InvalidAndAdjacentValuesCannotOverwriteTheSavedProject(int limit)
    {
        using var fixture = new TempProject();
        string file = Path.Combine(fixture.ProjectPath, "engine", "performance.json"); byte[] before = File.ReadAllBytes(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { MaxAsyncTasks = limit }));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Balanced")] [InlineData("fast")]
    public async Task AudioBuffer_InvalidDiscriminatorsCannotOverwriteTheSavedProject(string? profile)
    {
        using var fixture = new TempProject();
        string file = Path.Combine(fixture.ProjectPath, "engine", "performance.json"); byte[] before = File.ReadAllBytes(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { AudioBuffer = profile! }));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("original", 32, "low_latency", false)]
    [InlineData("half", 64, "balanced", true)]
    [InlineData("quarter", 128, "stable", true)]
    public async Task Store_RoundTripsTextureTaskAudioAndUnloadTogetherIntoTheLoadedEngine(string quality, int tasks, string audio, bool unload)
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new()
        {
            Graphics = new() { TextureQuality = quality, Api = "vulkan", ResourceCacheLimitMb = 512 },
            MaxAsyncTasks = tasks, AudioBuffer = audio, UnloadPreviousScene = unload, Preloading = false
        });
        var saved = PerformanceSettingsStore.LoadProject(fixture.ProjectPath);
        var loaded = (await fixture.LoadAsync()).Engine.Performance;
        foreach (var settings in new[] { saved, loaded })
        {
            Assert.Equal(quality, settings.Graphics.TextureQuality); Assert.Equal("vulkan", settings.Graphics.Api);
            Assert.Equal(512, settings.Graphics.ResourceCacheLimitMb); Assert.Equal(tasks, settings.MaxAsyncTasks);
            Assert.Equal(audio, settings.AudioBuffer); Assert.Equal(unload, settings.UnloadPreviousScene); Assert.False(settings.Preloading);
        }
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.ProjectPath, "engine", "performance.json")));
        Assert.Equal(quality, json.RootElement.GetProperty("graphics").GetProperty("texture_quality").GetString());
        Assert.Equal(tasks, json.RootElement.GetProperty("max_async_tasks").GetInt32());
        Assert.Equal(audio, json.RootElement.GetProperty("audio_buffer").GetString());
        Assert.Equal(unload, json.RootElement.GetProperty("unload_previous_scene").GetBoolean());
    }

    [Fact]
    public void DeletionConfirmation_OnlyTheSeventySecondArmedClickCompletesAndFurtherClicksAreIgnored()
    {
        var confirmation = new ProjectDeletionConfirmation();
        Assert.False(confirmation.IsArmed); Assert.Equal(0, confirmation.Remaining);
        Assert.False(confirmation.RecordClick()); Assert.Equal(0, confirmation.Remaining);
        confirmation.Begin(); Assert.True(confirmation.IsArmed); Assert.Equal(72, confirmation.Remaining);
        for (int click = 1; click <= 71; click++)
        {
            Assert.False(confirmation.RecordClick()); Assert.True(confirmation.IsArmed); Assert.Equal(72 - click, confirmation.Remaining);
        }
        Assert.True(confirmation.RecordClick()); Assert.False(confirmation.IsArmed); Assert.Equal(0, confirmation.Remaining);
        Assert.False(confirmation.RecordClick()); Assert.Equal(0, confirmation.Remaining);
    }

    [Fact]
    public void DeletionConfirmation_BeginResetsPartialProgressAndCancelRequiresACompleteNewSequence()
    {
        var confirmation = new ProjectDeletionConfirmation(); confirmation.Begin();
        for (int i = 0; i < 19; i++) Assert.False(confirmation.RecordClick());
        Assert.Equal(53, confirmation.Remaining); confirmation.Begin(); Assert.Equal(72, confirmation.Remaining);
        Assert.False(confirmation.RecordClick()); confirmation.Cancel();
        Assert.False(confirmation.IsArmed); Assert.Equal(0, confirmation.Remaining); Assert.False(confirmation.RecordClick());
        confirmation.Begin();
        for (int i = 0; i < 71; i++) Assert.False(confirmation.RecordClick());
        Assert.True(confirmation.RecordClick()); Assert.Equal(0, confirmation.Remaining);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Window_CompactColumnsDiagnosticsAndActionsFitTheSame780By840SizeInBothDangerStates(bool expanded)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(() =>
        {
            var window = new ProjectParametersWindow(project);
            try
            {
                Assert.False(window.DangerZone.IsExpanded); window.DangerZone.IsExpanded = expanded;
                if (Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER") != null) EditorTheme.Apply(window, "kne");
                Assert.Equal(780, window.Width); Assert.Equal(840, window.Height); Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                var content = Assert.IsType<Viewbox>(window.Content);
                content.Measure(new Size(780, 800)); content.Arrange(new Rect(0, 0, 780, 800)); content.UpdateLayout();
                Assert.Empty(Descendants(content).OfType<ScrollViewer>());
                var graphics = Parent<Border>(window.GraphicsInput.ApiInput); var performance = Parent<Border>(window.AsyncTasksInput);
                Assert.Same(graphics.Parent, performance.Parent); Assert.NotSame(graphics, performance);
                Assert.True(Grid.GetColumn(graphics) < Grid.GetColumn(performance));
                Assert.Equal(graphics.TransformToAncestor(content).Transform(new Point()).Y, performance.TransformToAncestor(content).Transform(new Point()).Y, 3);
                Assert.Equal(HorizontalAlignment.Center, Parent<StackPanel>(Parent<StackPanel>(window.TitleInput)).HorizontalAlignment);
                var diagnostic = Parent<Grid>(window.CornerInput);
                Assert.Equal(2, diagnostic.ColumnDefinitions.Count); Assert.Equal(3, diagnostic.RowDefinitions.Count);
                Assert.Contains(Descendants(diagnostic).OfType<CheckBox>(), input => Equals(input.Content, "Счётчик FPS"));
                Assert.IsType<DockPanel>(window.SavesInput.Parent);
                var save = Assert.Single(Descendants(content).OfType<Button>(), button => Equals(button.Content, "Сохранить"));
                var cancel = Assert.Single(Descendants(content).OfType<Button>(), button => Equals(button.Content, "Отмена"));
                Assert.Same(save.Parent, cancel.Parent); Assert.True(cancel.IsCancel);
                var body = Assert.IsType<StackPanel>(content.Child);
                Assert.True(body.Children.IndexOf(window.DangerZone) > body.Children.IndexOf(Parent<Border>(window.SavesInput)));
                Assert.Equal(System.Windows.Media.Colors.IndianRed, Assert.IsType<System.Windows.Media.SolidColorBrush>(window.DangerZone.Foreground).Color);
                foreach (FrameworkElement element in new FrameworkElement[] { window.TitleInput, window.TextureInput, window.AsyncTasksInput, window.AudioBufferInput, window.CornerInput, window.SavesInput, save, cancel, window.DangerZone })
                    AssertFits(element, content);
                if (expanded) AssertFits(window.DeleteProjectButton, content);
                Assert.Equal(780, window.Width); Assert.Equal(840, window.Height);
                if (Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(780, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, expanded ? "project-parameters-expanded.png" : "project-parameters-collapsed.png")); encoder.Save(output);
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(16)] [InlineData(32)] [InlineData(64)]
    public async Task Window_CacheOptionsOffer64Through512AndRetainOnlyTheLoadedLegacyChoice(int loaded)
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { ResourceCacheLimitMb = loaded } });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(() =>
        {
            var window = new ProjectParametersWindow(project);
            try
            {
                var input = window.GraphicsInput.CacheLimitInput;
                Assert.Equal(loaded, input.SelectedValue);
                Assert.Equal(loaded < 64 ? new[] { loaded, 64, 128, 256, 512 } : new[] { 64, 128, 256, 512 }, input.Items.Cast<GraphicsOption<int>>().Select(item => item.Value));
                Assert.Same(input.Parent, window.GraphicsInput.LimitCacheInput.Parent); Assert.IsType<DockPanel>(input.Parent);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeleteButton_ConfirmationThenExactly72ActualClickEventsRequestsDeletionWithoutTouchingProjectFiles()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string sentinel = fixture.Write("keep.txt", "still present");
        await WorkspaceTabsTests.Sta(() =>
        {
            var window = new ProjectParametersWindow(project); int prompts = 0;
            window.ConfirmProjectDeletion = () => { prompts++; return true; }; window.DangerZone.IsExpanded = true;
            try
            {
                Click(window.DeleteProjectButton);
                Assert.Equal(1, prompts); Assert.Equal(72, window.DeletionConfirmation.Remaining); Assert.False(window.DeletionRequested);
                Assert.Equal("Осталось нажатий: 72", window.DeleteCounter.Text); Assert.Equal(Visibility.Visible, window.DeleteCounter.Visibility);
                var content = Assert.IsType<Viewbox>(window.Content);
                content.Measure(new Size(780, 800)); content.Arrange(new Rect(0, 0, 780, 800)); content.UpdateLayout();
                AssertFits(window.DeleteCounter, content); AssertFits(window.DeleteProjectButton, content);
                for (int click = 1; click <= 71; click++)
                {
                    Click(window.DeleteProjectButton);
                    Assert.Equal(72 - click, window.DeletionConfirmation.Remaining); Assert.False(window.DeletionRequested);
                    Assert.Equal("Осталось нажатий: " + (72 - click), window.DeleteCounter.Text);
                }
                Assert.Equal("still present", File.ReadAllText(sentinel)); Click(window.DeleteProjectButton);
                Assert.True(window.DeletionRequested); Assert.Equal(0, window.DeletionConfirmation.Remaining);
                Assert.False(window.DeletionConfirmation.IsArmed); Assert.Equal(1, prompts);
                Assert.Equal("still present", File.ReadAllText(sentinel)); Assert.True(Directory.Exists(project.Path));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DeleteButton_RejectedConfirmationCollapseAndCancelDiscardPartialProgress()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(() =>
        {
            var window = new ProjectParametersWindow(project); int prompts = 0; bool confirm = false;
            window.ConfirmProjectDeletion = () => { prompts++; return confirm; }; window.DangerZone.IsExpanded = true;
            try
            {
                Click(window.DeleteProjectButton); Assert.False(window.DeletionConfirmation.IsArmed); Assert.Equal(0, window.DeletionConfirmation.Remaining);
                Assert.Equal(Visibility.Collapsed, window.DeleteCounter.Visibility); Assert.False(window.DeletionRequested);
                confirm = true; Click(window.DeleteProjectButton);
                for (int i = 0; i < 7; i++) Click(window.DeleteProjectButton);
                Assert.Equal(65, window.DeletionConfirmation.Remaining);
                window.DangerZone.IsExpanded = false;
                Assert.False(window.DeletionConfirmation.IsArmed); Assert.Equal(0, window.DeletionConfirmation.Remaining); Assert.Equal(Visibility.Collapsed, window.DeleteCounter.Visibility);
                window.DangerZone.IsExpanded = true; Click(window.DeleteProjectButton);
                Assert.Equal(72, window.DeletionConfirmation.Remaining); Assert.Equal(3, prompts);
                Click(window.DeleteProjectButton); Assert.Equal(71, window.DeletionConfirmation.Remaining);
                Click(Assert.Single(Descendants(window.Content).OfType<Button>(), button => Equals(button.Content, "Отмена")));
                Assert.False(window.DeletionConfirmation.IsArmed); Assert.Equal(0, window.DeletionConfirmation.Remaining); Assert.False(window.DeletionRequested);
                Assert.True(Directory.Exists(project.Path));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Parent<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? parent = LogicalTreeHelper.GetParent(element); parent != null; parent = LogicalTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException("Missing parent " + typeof(T).Name);
    }
    private static IEnumerable<DependencyObject> Descendants(object root)
    {
        if (root is not DependencyObject owner) yield break;
        foreach (object child in LogicalTreeHelper.GetChildren(owner))
            if (child is DependencyObject item) { yield return item; foreach (var nested in Descendants(item)) yield return nested; }
    }
    private static void AssertFits(FrameworkElement element, FrameworkElement content)
    {
        Assert.True(element.ActualWidth > 0); Assert.True(element.ActualHeight > 0);
        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
        Assert.InRange(bounds.Left, -.01, content.ActualWidth); Assert.InRange(bounds.Top, -.01, content.ActualHeight);
        Assert.InRange(bounds.Right, 0, content.ActualWidth + .01); Assert.InRange(bounds.Bottom, 0, content.ActualHeight + .01);
    }
}
