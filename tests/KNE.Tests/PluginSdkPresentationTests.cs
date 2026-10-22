using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class PluginSdkPresentationTests
{
    [Fact]
    public async Task WorkspaceTexture_IsDecodedOnceAndSharedByEditorTabs_AndReleasedOnProjectUnload()
    {
        using var fixture = new TempProject();
        await WorkspaceTabsTests.Sta(async () =>
        {
            string imagePath = Path.Combine(fixture.Root, "workspace.png"); WritePng(imagePath, Colors.Purple);
            Install(fixture, $$"""
                using KNE;
                public sealed class TextureExtension : IKnePlugin
                {
                    public void Execute(IPluginContext context)
                    {
                        if (context.Api.IsEditor) context.Api.SetWorkspaceTexture({{JsonSerializer.Serialize(imagePath)}});
                    }
                }
                """);
            var first = VisualScript.Create(); first.FunctionName = "first";
            var second = VisualScript.Create(); second.FunctionName = "second";
            string firstPath = fixture.Write("first.knegraph", first.Serialize());
            string secondPath = fixture.Write("second.knegraph", second.Serialize());
            var window = new MainWindow(); var previous = PluginCatalog.Current;
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                var api = Assert.IsType<PluginApi>(PluginCatalog.Current);
                var image = Assert.IsAssignableFrom<BitmapSource>(api.WorkspaceImage);
                Assert.True(image.IsFrozen); Assert.Equal(2, image.PixelWidth); Assert.Equal(imagePath, api.WorkspaceTexture);
                File.Delete(imagePath);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", firstPath);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", secondPath);
                var editors = Find<TabControl>(window, "DocumentTabs").Items.OfType<TabItem>().Select(tab => tab.Tag).OfType<ScriptEditor>().ToArray();
                Assert.Equal(2, editors.Length);
                foreach (var editor in editors)
                {
                    Invoke(editor, "RenderGraph"); Invoke(editor, "RenderGraph");
                    var brush = Assert.IsType<ImageBrush>(Find<Canvas>(editor, "GraphCanvas").Background);
                    Assert.Same(image, brush.ImageSource); Assert.Equal(.4, brush.Opacity); Assert.Equal(Stretch.UniformToFill, brush.Stretch);
                }
                Assert.Throws<FileNotFoundException>(() => api.SetWorkspaceTexture("missing.png"));
                Assert.Same(image, api.WorkspaceImage);
                Invoke(window, "UnloadEditorPlugins");
                Assert.Null(api.WorkspaceImage); Assert.Null(api.WorkspaceTexture); Assert.Null(PluginCatalog.Current);
                foreach (var editor in editors) Assert.IsNotType<ImageBrush>(Find<Canvas>(editor, "GraphCanvas").Background);
                Assert.Throws<ObjectDisposedException>(() => api.SetWorkspaceTexture("missing.png"));
                Invoke(window, "ClearEditorTabs");
            }
            finally { Invoke(window, "ClearEditorTabs"); window.Close(); PluginCatalog.Current = previous; }
        });
    }

    [Fact]
    public async Task GameTexture_ImportsExternalPngAsPortableInitialBackground_AndScriptCanReplaceIt()
    {
        using var fixture = new TempProject();
        string imagePath = Path.Combine(fixture.Root, "initial.png");
        string replacement = PathSafety.ResolveRelative(fixture.ProjectPath, "game/backgrounds/replacement.png");
        await WorkspaceTabsTests.Sta(() => { WritePng(imagePath, Colors.Purple); WritePng(replacement, Colors.Gold); return Task.CompletedTask; });
        byte[] original = File.ReadAllBytes(imagePath);
        Install(fixture, $$"""
            using KNE;
            public sealed class TextureExtension : IKnePlugin
            {
                public void Execute(IPluginContext context) => context.Api.SetGameTexture({{JsonSerializer.Serialize(imagePath)}});
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, 'initial background')
                background('game/backgrounds/replacement.png')
                d(nil, 'replacement background')
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, executionLimit: TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("initial background", runtime.Story.Text);
        var api = Assert.IsType<PluginHost>(GetField(runtime, "pluginHost")).Api;
        string initial = Assert.IsType<string>(api.GameTexture);
        Assert.StartsWith(".kne/plugin-assets/", initial); Assert.False(Path.IsPathRooted(initial));
        Assert.Equal(PathSafety.ResolveRelative(fixture.ProjectPath, initial), runtime.Visuals.Background);
        Assert.Equal(original, File.ReadAllBytes(PathSafety.ResolveRelative(fixture.ProjectPath, initial)));
        Assert.Equal(original, File.ReadAllBytes(imagePath));
        await runtime.AdvanceAsync();
        Assert.Equal("replacement background", runtime.Story.Text);
        Assert.Equal(replacement, runtime.Visuals.Background);
        Assert.True(File.Exists(PathSafety.ResolveRelative(fixture.ProjectPath, initial)));
    }

    [Fact]
    public async Task GamePalette_CopiesEveryPaletteColorToTheme_AndLoadsWithoutPluginAfterSaving()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var api = new PluginApi(fixture.ProjectPath);
        api.RegisterPalette(new("night-rpg", "Ночная палитра", "#102938", "#294657", "#F8F5EA", "#F070D0", "#8297A8"));
        project.Engine.Theme.FontFamily = "Consolas"; project.Engine.Theme.Dialogue.Height = 300;
        api.ApplyGamePalette("night-rpg", project.Engine.Theme);
        var theme = project.Engine.Theme;
        Assert.Equal("custom", theme.Palette);
        Assert.Equal("#102938", theme.BackgroundColor); Assert.Equal("#8297A8", theme.PatternColor);
        Assert.Equal("#F070D0", theme.AccentColor); Assert.Equal("#F8F5EA", theme.TextColor); Assert.Equal("#294657", theme.ButtonColor);
        Assert.Equal("#294657", theme.Dialogue.BackgroundColor); Assert.Equal("#F8F5EA", theme.Dialogue.TextColor);
        Assert.Equal("#F070D0", theme.Dialogue.SpeakerColor); Assert.Equal("#8297A8", theme.Dialogue.BorderColor);
        Assert.Equal("Consolas", theme.FontFamily); Assert.Equal(300, theme.Dialogue.Height);
        string expected = JsonSerializer.Serialize(theme, EngineSettings.JsonOptions);
        Assert.Throws<ArgumentException>(() => api.ApplyGamePalette("missing", theme));
        Assert.Equal(expected, JsonSerializer.Serialize(theme, EngineSettings.JsonOptions));
        project.Engine.Screens.Theme = theme;
        fixture.Write("engine/theme.json", expected); fixture.Write("engine/screens.json", project.Engine.Screens.Serialize());
        api.Dispose();
        var loaded = EngineSettings.Load(fixture.ProjectPath);
        Assert.Equal(expected, JsonSerializer.Serialize(loaded.Theme, EngineSettings.JsonOptions));
        Assert.Equal("custom", loaded.Theme.Palette);
        Assert.Throws<ObjectDisposedException>(() => api.ApplyGamePalette("night-rpg", theme));
    }

    [Fact]
    public async Task PlayerPluginScreen_UsesRealFactoryAndLiveState_ThenHidesRecreatesAndClearsOnError()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            using System;
            using System.Text.Json;
            using System.Windows.Controls;
            public sealed class ScreenExtension : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    if (!context.Api.IsPlayer) return;
                    context.Api.RegisterScreen(new PluginGameScreen("quest", snapshot => new QuestPanel(snapshot)));
                    context.Api.RegisterScreen(new PluginGameScreen("broken", _ => throw new InvalidOperationException("screen factory boom")));
                }
            }
            public sealed class QuestPanel : Border, IPluginScreenView
            {
                public QuestPanel(JsonElement snapshot) { Child = new TextBlock(); Tag = 0; ToolTip = "created with hp " + Hp(snapshot); }
                public void UpdateState(JsonElement snapshot) { ((TextBlock)Child).Text = "HP: " + Hp(snapshot); Tag = (int)Tag + 1; }
                private static int Hp(JsonElement snapshot) => snapshot.GetProperty("Globals").GetProperty("state").GetProperty("hp").GetInt32();
            }
            """);
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project); var session = new ScreenSession(project.Engine.RuntimeDefaults(project.Title));
            var original = (IGameSession)GetField(window, "runtime")!; await original.DisposeAsync(); SetField(window, "runtime", session);
            try
            {
                Invoke(window, "InitializePlayerPlugins"); session.SetState(7, ["quest"]); Invoke(window, "RenderPluginScreens");
                var overlay = (Grid)GetField(window, "pluginOverlay")!;
                var view = Assert.IsAssignableFrom<Border>(Assert.Single(overlay.Children.Cast<UIElement>()));
                Assert.Equal(500, Panel.GetZIndex(overlay)); Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.Equal("created with hp 7", view.ToolTip); Assert.Equal("HP: 7", Assert.IsType<TextBlock>(view.Child).Text); Assert.Equal(1, view.Tag);
                session.SetState(19, ["quest"]); Invoke(window, "RenderPluginScreens");
                Assert.Same(view, Assert.Single(overlay.Children.Cast<UIElement>()));
                Assert.Equal("HP: 19", Assert.IsType<TextBlock>(view.Child).Text); Assert.Equal(2, view.Tag);
                session.SetState(19, []); Invoke(window, "RenderPluginScreens");
                Assert.Empty(overlay.Children); Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                session.SetState(23, ["quest"]); Invoke(window, "RenderPluginScreens");
                var recreated = Assert.IsAssignableFrom<Border>(Assert.Single(overlay.Children.Cast<UIElement>()));
                Assert.NotSame(view, recreated); Assert.Equal("HP: 23", Assert.IsType<TextBlock>(recreated.Child).Text); Assert.Equal(1, recreated.Tag);
                session.SetState(23, ["broken"]); Invoke(window, "RenderOutput");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility);
                Assert.Contains("screen factory boom", Find<TextBox>(window, "StartupErrorMessage").Text);
                Invoke(window, "RenderPluginScreens"); Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Invoke(window, "DisposePlayerPlugins"); Assert.Empty(overlay.Children);
                Assert.Empty((Dictionary<string, FrameworkElement>)GetField(window, "pluginViews")!); Assert.Null(GetField(window, "playerPlugins"));
            }
            finally { Invoke(window, "DisposePlayerPlugins"); window.Close(); await session.DisposeAsync(); }
        });
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("tooLong")]
    [InlineData("lowRate")]
    [InlineData("highRate")]
    [InlineData("lowVolume")]
    [InlineData("highVolume")]
    public void Speech_RejectsInvalidInputBeforeCreatingSynthesizer_AndDisposedServiceCannotRestart(string variation)
    {
        using var media = new PluginMedia();
        string text = variation == "empty" ? " " : variation == "tooLong" ? new string('a', 16001) : "valid but never spoken";
        int rate = variation == "lowRate" ? -11 : variation == "highRate" ? 11 : 0;
        int volume = variation == "lowVolume" ? -1 : variation == "highVolume" ? 101 : 100;
        Assert.ThrowsAny<ArgumentException>(() => media.Speak(text, rate: rate, volume: volume));
        Assert.Null(GetField(media, "speech")); media.StopSpeech(); Assert.Null(GetField(media, "speech"));
        media.Dispose(); media.Dispose();
        Assert.Throws<ObjectDisposedException>(() => media.Speak("never spoken"));
        Assert.Throws<ObjectDisposedException>(() => media.Play("never-opened.wav"));
        Assert.Null(GetField(media, "speech"));
    }

    [Fact]
    public void AdministratorRequest_RejectsMissingProgramAndMissingReasonBeforeAnyPrompt()
    {
        using var fixture = new TempProject(); string missing = Path.Combine(fixture.ProjectPath, "not-existing.exe");
        var error = Assert.Throws<FileNotFoundException>(() => PluginPrivileges.RequestAdministrator(missing, [], "A valid reason but no executable"));
        Assert.Equal(missing, error.FileName);
        Assert.ThrowsAny<ArgumentException>(() => PluginPrivileges.RequestAdministrator("\0", [], "Invalid path"));
        // A real existing assembly is safe here because the reason guard runs before confirmation or UAC.
        Assert.ThrowsAny<ArgumentException>(() => PluginPrivileges.RequestAdministrator(typeof(App).Assembly.Location, [], " "));
    }

    private sealed class ScreenSession(RuntimeWindowSettings settings) : IGameSession
    {
        public RuntimeWindowSettings WindowSettings => settings;
        public StoryFrame Story => StoryFrame.Empty;
        public SceneVisuals Visuals => SceneVisuals.Empty;
        public IReadOnlyList<string> PluginScreens { get; private set; } = [];
        public JsonElement Variables { get; private set; } = JsonSerializer.SerializeToElement(new { Globals = new { state = new { hp = 0 } } });
        public void SetState(int hp, string[] active) { PluginScreens = active; Variables = JsonSerializer.SerializeToElement(new { Globals = new { state = new { hp } } }); }
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task AdvanceAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ChooseAsync(int option, CancellationToken token = default) => Task.CompletedTask;
        public Task ProcessCommandAsync(string command, CancellationToken token = default) => Task.CompletedTask;
        public Task<GameCheckpoint> GetCheckpointAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static object? GetField(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string method, params object[] arguments) => owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static void WritePng(string path, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] pixels = [color.B, color.G, color.R, color.A, color.B, color.G, color.R, color.A];
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Install(TempProject fixture, string source)
    {
        fixture.Write("game/plugins/presentation.cs", source);
        fixture.Write("plugins/presentation.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("presentation", "game/plugins/presentation.cs")));
    }
}
