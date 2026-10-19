using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectRuntimeOptionsTests
{
    [Theory]
    [InlineData("original", 16, 12)] [InlineData("half", 8, 6)] [InlineData("quarter", 4, 3)]
    public void TextureQuality_DecodesActualSmallerImagesAndAccountsForTheirDecodedSize(string quality, int width, int height)
    {
        using var fixture = new TempProject(); string path = Image(fixture, "texture.png", 16, 12);
        var cache = new SceneAssetCache(); cache.Configure(new() { TextureQuality = quality });
        var image = cache.Load(path);
        Assert.Equal(width, image.PixelWidth); Assert.Equal(height, image.PixelHeight); Assert.True(image.IsFrozen);
        Assert.Equal(width * height * 4, cache.CachedBytes); Assert.Same(image, cache.Load(path));
        var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 255);
    }

    [Fact]
    public void TextureQuality_ReconfigurationDropsOldDecodedResolutionAndKeepsOnePixelMinimum()
    {
        using var fixture = new TempProject(); string path = Image(fixture, "one.png", 1, 1);
        var cache = new SceneAssetCache(); var original = cache.Load(path);
        cache.Configure(new() { TextureQuality = "quarter" }); Assert.Equal(0, cache.CachedBytes); Assert.Equal(0, cache.CachedCount);
        var quarter = cache.Load(path); Assert.NotSame(original, quarter); Assert.Equal(1, quarter.PixelWidth); Assert.Equal(1, quarter.PixelHeight);
        cache.Configure(new() { TextureQuality = "original" }); Assert.Equal(0, cache.CachedCount); Assert.NotSame(quarter, cache.Load(path));
    }

    [Theory]
    [InlineData(32)] [InlineData(64)] [InlineData(128)]
    public async Task AsyncLimit_AllowsExactlyTheConfiguredActiveTasksAndRejectsTheNextBranch(int limit)
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { MaxAsyncTasks = limit, Preloading = false });
        string source = "function main() local remaining=" + (limit - 1) + " while remaining>0 do local branches={} local count=math.min(64,remaining) for i=1,count do branches[i]=function() kne_pause(10) end end kne_async(branches); remaining=remaining-count end d(nil,'at capacity'); kne_async({function() kne_pause(10) end}); d(nil,'unreachable') end";
        fixture.Write("game/scenes/main.krl", source);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync(); Assert.Equal("at capacity", runtime.Story.Text);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        Assert.Contains("не более " + limit + " веток", failure.Message); Assert.NotEqual("unreachable", runtime.Story.Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SceneTransition_UnloadOptionDropsOnlyOptionalCacheAndPreservesTheDisplayedStory(bool unload)
    {
        using var fixture = new TempProject(); Image(fixture, "first.png", 2, 3); Image(fixture, "second.png", 3, 4);
        fixture.Write("game/scenes/main.krl", "function main() background('first.png'); d(nil,'First scene'); call(child); d(nil,'Returned') end");
        fixture.Write("game/scenes/child.krl", "function child() background('second.png'); d(nil,'Second scene') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { UnloadPreviousScene = unload, Preloading = false });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync"); var cache = Field<SceneAssetCache>(window, "assets");
                Assert.Equal(1, cache.CachedCount);
                var runtime = Field<ProcessGameSession>(window, "runtime"); await runtime.AdvanceAsync(); Invoke(window, "RenderOutput");
                Assert.Equal("Second scene", Find<TextBlock>(window, "DialogueText").Text);
                var image = Assert.IsType<BitmapImage>(Find<Image>(window, "BackgroundImage").Source); Assert.Equal(3, image.PixelWidth); Assert.Equal(4, image.PixelHeight);
                Assert.Equal(unload ? 1 : 2, cache.CachedCount); Assert.Equal(unload ? 48 : 72, cache.CachedBytes);
                await runtime.AdvanceAsync(); Invoke(window, "RenderOutput"); Assert.Equal("Returned", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Same(image, Find<Image>(window, "BackgroundImage").Source); Assert.Equal(unload ? 1 : 2, cache.CachedCount);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SceneUnloadPreservesPreloadedCurrentTexturesAndDropsUnneededPreviousResources()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string old = Image(fixture, "old.png", 2, 3), current = Image(fixture, "current.png", 3, 4), ui = Image(fixture, "ui.png", 4, 5);
        var cache = new SceneAssetCache(); _ = cache.Load(old); var active = cache.Load(current); var texture = cache.Load(ui);
        var state = SceneVisuals.Empty with { Background = current, UiConfigurations = [new("dialogue", GameplayUiStyle.ForTarget("dialogue") with { Texture = "ui.png" })] };
        cache.RetainForScene(project, state);
        Assert.Equal(2, cache.CachedCount); Assert.Equal((3 * 4 + 4 * 5) * 4, cache.CachedBytes);
        Assert.Same(active, cache.Load(current)); Assert.Same(texture, cache.Load(ui));
    }

    [Fact]
    public async Task WorkspaceDeletionRemovesOnlyTheConfirmedProjectClearsDraftTabsAndForgetsItsHistory()
    {
        using var fixture = new TempProject(); string script = fixture.Write("draft.krl", "function draft() end");
        string settingsPath = Path.Combine(fixture.Root, "editor-settings.json");
        var store = new UserSettingsStore(settingsPath, Path.Combine(fixture.Root, "missing.config"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow(); typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store);
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", script);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var editor = Assert.IsType<ScriptEditor>(((TabItem)tabs.Items[0]).Tag);
                var code = Find<KrlCodeEditor>(editor, "CodeEditor"); code.Text += " -- discarded only after explicit deletion"; Assert.True(editor.HasUnsavedChanges);
                var project = Field<ProjectInfo>(window, "currentProject");
                await WorkspaceTabsTests.Invoke(window, "DeleteCurrentProjectAsync", project);
                Assert.False(Directory.Exists(fixture.ProjectPath)); Assert.Empty(tabs.Items); Assert.False(editor.HasUnsavedChanges);
                Assert.Null(Find<System.Windows.Controls.DataGrid>(window, "FilesDataGrid").ItemsSource);
                Assert.Equal(Visibility.Visible, Find<StackPanel>(window, "WelcomePanel").Visibility);
                var settings = await store.LoadAsync(); Assert.DoesNotContain(fixture.ProjectPath, settings.RecentProjects); Assert.False(settings.ProjectLastOpened.ContainsKey(fixture.ProjectPath));
                Assert.True(File.Exists(settingsPath));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task LiveTextureQualityReloadReplacesBackgroundAndUiTexturesWithoutRestartingTheStory()
    {
        using var fixture = new TempProject(); Image(fixture, "texture.png", 16, 12);
        fixture.Write("game/scenes/main.krl", "function main() background('texture.png'); d(nil,'Same story') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync"); Assert.Equal(16, Assert.IsType<BitmapImage>(Find<Image>(window, "BackgroundImage").Source).PixelWidth);
                var config = GameplayUiStyle.ForTarget("dialogue") with { Texture = "texture.png" };
                var visuals = SceneVisuals.Empty with { UiConfigurations = [new("dialogue", config)] };
                Invoke(window, "ApplyGameplayUi", visuals);
                var originalUi = Assert.IsType<ImageBrush>(Find<Border>(window, "DialoguePanel").Background).ImageSource;
                await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { TextureQuality = "quarter" }, Preloading = false });
                window.ReloadPerformanceSettings(); Invoke(window, "RenderOutput"); Invoke(window, "ApplyGameplayUi", visuals);
                Assert.Equal("Same story", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(4, Assert.IsType<BitmapImage>(Find<Image>(window, "BackgroundImage").Source).PixelWidth);
                var currentUi = Assert.IsType<ImageBrush>(Find<Border>(window, "DialoguePanel").Background).ImageSource;
                Assert.NotSame(originalUi, currentUi); Assert.Equal(4, Assert.IsType<BitmapImage>(currentUi).PixelWidth);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public Task ScreenImagesUseTheProjectTextureResolutionAndSharedCache() => WorkspaceTabsTests.Sta(() =>
    {
        using var fixture = new TempProject(); string path = Image(fixture, "screen.png", 16, 12);
        var cache = new SceneAssetCache(); cache.Configure(new() { TextureQuality = "quarter" });
        var element = new ScreenElement { Type = "image", Image = "screen.png" };
        var first = Assert.IsType<Image>(ScreenRenderer.Create(element, new GameTheme(), fixture.ProjectPath, text => text, cachePath => cache.Load(cachePath)));
        var second = Assert.IsType<Image>(ScreenRenderer.Create(element, new GameTheme(), fixture.ProjectPath, text => text, cachePath => cache.Load(cachePath)));
        Assert.Same(first.Source, second.Source); Assert.Same(cache.Load(path), first.Source);
        var image = Assert.IsType<BitmapImage>(first.Source); Assert.Equal(4, image.PixelWidth); Assert.Equal(3, image.PixelHeight);
        Assert.Equal(48, cache.CachedBytes); Assert.Equal(1, cache.CachedCount);
        return Task.CompletedTask;
    });

    private static string Image(TempProject fixture, string name, int width, int height)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, name); var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 160; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(path); encoder.Save(output); return path;
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static void Invoke(object owner, string method, params object[] args) => owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static Task InvokeTask(object owner, string method) => (Task)owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;
}
