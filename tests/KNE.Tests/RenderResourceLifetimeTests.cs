using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class RenderResourceLifetimeTests
{
    [Fact]
    public async Task GameWindow_CloseClearsDecodedAssetsDisplayedImagesAndFormattedVariableSnapshot()
    {
        using var fixture = new TempProject(); Image(fixture, "background.png", 2, 3); Image(fixture, "character.png", 3, 4);
        fixture.Write("game/scenes/main.krl", "function main() background('background.png'); show_character('character.png', 'center'); state.loaded=true; d(nil, 'Loaded') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { DebugMode = true, Preloading = false });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync");
                var assets = Field<SceneAssetCache>(window, "assets");
                Assert.Equal(2, assets.CachedCount); Assert.Equal((2 * 3 + 3 * 4) * 4, assets.CachedBytes);
                Assert.NotNull(Find<Image>(window, "BackgroundImage").Source); Assert.NotNull(Find<Image>(window, "CharacterImage").Source);
                Assert.NotNull(Field<string?>(window, "formattedVariableBody"));
                window.Close();
                Assert.Equal(0, assets.CachedCount); Assert.Equal(0, assets.CachedBytes);
                Assert.Null(Find<Image>(window, "BackgroundImage").Source); Assert.Null(Find<Image>(window, "CharacterImage").Source); Assert.Null(Find<Image>(window, "DefaultBackgroundImage").Source);
                Assert.Null(Field<string?>(window, "formattedVariableBody")); Assert.Null(Field<string?>(window, "renderedVariables"));
                Assert.Equal(JsonValueKind.Undefined, Field<JsonElement>(window, "formattedSnapshot").ValueKind);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task HiddenConsole_DoesNotFormatNewVariableSnapshotsUntilReopenedAndReusesAnUnchangedSnapshot()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.marker='before-hidden'; d(nil, 'First'); state.marker='after-hidden'; d(nil, 'Second') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { DebugMode = true, Preloading = false });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync");
                var console = Find<Border>(window, "DebugOverlay"); var output = Find<TextBox>(window, "VariablesOutput");
                string body = Field<string>(window, "formattedVariableBody"), text = output.Text;
                var snapshot = Field<JsonElement>(window, "formattedSnapshot"); Assert.Contains("before-hidden", body);
                Invoke(window, "CloseConsole_Click", new Button(), new RoutedEventArgs()); Assert.Equal(Visibility.Collapsed, console.Visibility);
                var runtime = Field<ProcessGameSession>(window, "runtime"); await runtime.AdvanceAsync();
                Assert.Contains("after-hidden", runtime.Variables.GetRawText()); Assert.False(snapshot.Equals(runtime.Variables));
                for (int i = 0; i < 3; i++) Invoke(window, "RenderConsole");
                Assert.Same(body, Field<string>(window, "formattedVariableBody")); Assert.True(snapshot.Equals(Field<JsonElement>(window, "formattedSnapshot"))); Assert.Equal(text, output.Text);
                Find<Button>(window, "ErrorConsoleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, console.Visibility); Assert.Contains("after-hidden", output.Text); Assert.DoesNotContain("before-hidden", output.Text);
                Assert.True(runtime.Variables.Equals(Field<JsonElement>(window, "formattedSnapshot")));
                string currentBody = Field<string>(window, "formattedVariableBody"); Invoke(window, "RenderConsole");
                Assert.Same(currentBody, Field<string>(window, "formattedVariableBody"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ScenePreview_AppliesConfiguredResourceBudgetAndStopClearsItsCacheAndEveryDisplayedImage()
    {
        using var fixture = new TempProject(); string[] paths = Enumerable.Range(0, 5).Select(index => Image(fixture, $"preview-{index}.png", 1024, 1024)).ToArray();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { LimitResourceCache = true, ResourceCacheLimitMb = 16 } });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(() =>
        {
            var view = new ScenePreview();
            try
            {
                var window = new RuntimeWindowSettings("Preview", 800, 600, "#000000"); var story = new StoryFrame("dialogue", "", "Preview resource lifecycle", []);
                foreach (string path in paths) view.DisplayScene(project, window, SceneVisuals.Empty with { Background = path, Character = paths[0] }, story, false, false);
                var assets = Field<SceneAssetCache>(view, "assets");
                Assert.Equal(4, assets.CachedCount); Assert.Equal(16L * 1024 * 1024, assets.CachedBytes);
                Assert.NotNull(Find<Image>(view, "BackgroundImage").Source); Assert.NotNull(Find<Image>(view, "CharacterImage").Source);
                Find<Image>(view, "DefaultBackgroundImage").Source = Find<Image>(view, "BackgroundImage").Source;
                view.Stop();
                Assert.Equal(0, assets.CachedCount); Assert.Equal(0, assets.CachedBytes);
                Assert.Null(Find<Image>(view, "BackgroundImage").Source); Assert.Null(Find<Image>(view, "CharacterImage").Source); Assert.Null(Find<Image>(view, "DefaultBackgroundImage").Source);
            }
            finally { view.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GameplayTextures_ShareTheSceneImageBudgetAndEvictOldTexturesInGameAndPreview(bool previewMode)
    {
        using var fixture = new TempProject(); string background = Image(fixture, "shared-background.png", 1024, 1024);
        string[] textures = Enumerable.Range(0, 5).Select(index => Image(fixture, $"shared-ui-{index}.png", 1024, 1024)).ToArray();
        fixture.Write("game/scenes/main.krl", "function main() background('shared-background.png'); d(nil, 'Shared resource budget') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false, Graphics = new() { LimitResourceCache = true, ResourceCacheLimitMb = 16 } });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var game = previewMode ? null : new GameWindow(project); var preview = previewMode ? new ScenePreview() : null;
            FrameworkElement owner = game is null ? preview! : game;
            try
            {
                if (game != null) await InvokeTask(game, "StartGameAsync");
                else preview!.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty with { Background = background }, new("dialogue", "", "Shared resource budget", []), false, false);
                var assets = Field<SceneAssetCache>(owner, "assets");
                var displayedBackground = Find<Image>(owner, "BackgroundImage").Source; Assert.NotNull(displayedBackground); Assert.Equal(4L * 1024 * 1024, assets.CachedBytes);
                ImageSource? firstTexture = null;
                for (int index = 0; index < textures.Length; index++)
                {
                    var style = GameplayUiStyle.ForTarget("dialogue") with { Texture = Path.GetFileName(textures[index]) };
                    var visuals = SceneVisuals.Empty with { Background = background, UiConfigurations = [new("dialogue", style)] };
                    if (game != null) Invoke(game, "ApplyGameplayUi", visuals);
                    else preview!.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, new("dialogue", "", "Shared resource budget", []), false, false);
                    var image = Assert.IsType<ImageBrush>(Find<Border>(owner, "DialoguePanel").Background).ImageSource;
                    Assert.NotNull(image); if (index == 0) firstTexture = image;
                    Assert.Same(image, assets.Load(textures[index]));
                    Assert.InRange(assets.CachedBytes, 4L * 1024 * 1024, 16L * 1024 * 1024);
                }
                Assert.Equal(4, assets.CachedCount); Assert.Equal(16L * 1024 * 1024, assets.CachedBytes);
                Assert.Same(displayedBackground, Find<Image>(owner, "BackgroundImage").Source);
                Assert.NotSame(firstTexture, assets.Load(textures[0]));
                if (game != null) Invoke(game, "ApplyGameplayUi", SceneVisuals.Empty);
                else preview!.ResetPresentation();
                Assert.True(assets.CachedCount > 0, "Resetting UI presentation must not discard the shared scene cache.");
                if (game != null) game.Close(); else preview!.Stop();
                Assert.Equal(0, assets.CachedCount); Assert.Equal(0, assets.CachedBytes);
            }
            finally { game?.Close(); preview?.Stop(); }
        });
    }

    [Fact]
    public void SceneMetrics_RetainsAtMostFiveHundredTwelveScenesAndUpdatesAnExistingRecordWithoutEvictingAnother()
    {
        using var fixture = new TempProject();
        for (int i = 0; i <= 512; i++) SceneMetrics.Record(fixture.ProjectPath, $"scene-{i}.krl", i, i, i);
        var values = (IDictionary)typeof(SceneMetrics).GetField("values", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Equal(512, values.Count);
        Assert.Contains(Enumerable.Range(0, 513), index => SceneMetrics.Describe(fixture.ProjectPath, $"scene-{index}.krl").Contains("Замеры появятся", StringComparison.Ordinal));
        Assert.Contains("Draw Calls графического слоя: 512", SceneMetrics.Describe(fixture.ProjectPath, "scene-512.krl"));
        string[] keys = values.Keys.Cast<string>().Order().ToArray();
        SceneMetrics.Record(fixture.ProjectPath, "scene-512.krl", 9.5, 7, 8);
        Assert.Equal(512, values.Count); Assert.Contains("Draw Calls графического слоя: 7", SceneMetrics.Describe(fixture.ProjectPath, "scene-512.krl"));
        Assert.Equal(keys, values.Keys.Cast<string>().Order());
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name, params object[] arguments) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static Task InvokeTask(object owner, string name) => (Task)owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;
    private static string Image(TempProject fixture, string relative, int width, int height)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, relative); var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4) { pixels[offset] = 120; pixels[offset + 3] = 255; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var output = File.Create(path); encoder.Save(output); return path;
    }
}
