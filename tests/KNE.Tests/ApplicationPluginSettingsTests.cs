using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ApplicationPluginSettingsTests
{
    private const string CSharpSource = """
        using KNE;
        public sealed class GlobalExtension : IKnePlugin
        {
            public void Execute(IPluginContext context)
            {
                context.Api.RegisterBlock(new PluginBlock("global.counter", "Глобальный блок", "logic"), (execution, values) => {
                    execution.Set("global_runs", (double?)execution.Get("global_runs") + 1 ?? 1); return 1;
                });
                context.Api.BeforeExecute("Dialogue", frame => frame.Text = "global:" + frame.Text);
                context.Api.RegisterPalette(new PluginPalette("global.neon", "Глобальный неон", "#101020", "#24243B", "#F4F0FF", "#D74DFF", "#9B93BF"));
                if (context.Api.IsEditor) context.Api.RegisterCommand(new PluginEditorCommand("global.tool", "Инструменты расширения", editor => { }));
            }
        }
        """;
    private const string LuaSource = """
        function global_text(text, speaker) return text .. ' [lua]', speaker end
        kne.before_text('global_text')
        kne.register_group([[{"id":"global-lua","title":"Глобальный Lua","color":"#2786A6"}]])
        """;

    [Fact]
    public async Task Profile_NormalizesDuplicatesAndInvalidPaths_KeepsMissingSourcesAndPluginPalette()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.Root, "missing plugin.cs");
        var store = Store(fixture);
        await store.SaveAsync(new UserSettings { ApplicationPlugins = [path, path.ToUpperInvariant(), "relative.lua", "", null!, Path.ChangeExtension(path, ".txt")], Theme = "global.neon", UserName = "Автор", EditorPath = "editor.exe" });
        var loaded = await store.LoadAsync();
        Assert.Equal(new[] { path }, loaded.ApplicationPlugins); Assert.False(File.Exists(path));
        Assert.Equal("global.neon", loaded.Theme); Assert.Equal("Автор", loaded.UserName); Assert.Equal("editor.exe", loaded.EditorPath);
        await store.SaveAsync(loaded);
        Assert.Equal(new[] { path }, (await store.LoadAsync()).ApplicationPlugins);
        Assert.Empty(ApplicationPluginFiles.Normalize(null));
        Assert.Equal(64, ApplicationPluginFiles.Normalize(Enumerable.Range(0, 65).Select(i => Path.Combine(fixture.Root, i + ".lua"))).Length);
    }

    [Fact]
    public Task Settings_AddSaveReopenAndRemove_ActivatesPluginsAndPreservesSourceFiles() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var store = Store(fixture);
        await store.SaveAsync(new UserSettings { UserName = "Автор", WelcomeObjects = "stars" });
        string cs = External(fixture, "Мой плагин.cs", CSharpSource), lua = External(fixture, "Мой плагин.lua", LuaSource);
        var previous = PluginCatalog.Current; var window = Window(store);
        try
        {
            await LoadSettings(window); Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs());
            var list = Find<ListBox>(window, "ApplicationPluginsList");
            Assert.Empty(list.Items); Assert.False(Find<Button>(window, "RemoveApplicationPluginButton").IsEnabled);
            Invoke(window, "AddApplicationPluginFiles", (object)new[] { cs, lua, cs.ToUpperInvariant() });
            Assert.Equal(2, list.Items.Count); Assert.Empty((await store.LoadAsync()).ApplicationPlugins); Assert.Null(PluginCatalog.Current);
            Assert.Contains("несохранённые", Find<TextBlock>(window, "ApplicationPluginsStatus").Text);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            Assert.Equal(new[] { cs, lua }, (await store.LoadAsync()).ApplicationPlugins);
            Assert.Equal("global.counter", Assert.Single(PluginCatalog.Current!.Blocks).Id);
            Assert.Equal("global-lua", Assert.Single(PluginCatalog.Current.Groups).Id);
            Assert.Equal("global.neon", Assert.Single(PluginCatalog.Current.Palettes).Id);
            Layout((FrameworkElement)window.Content, 1500, 950);
            Snapshot((FrameworkElement)window.Content, "settings-with-plugins.png", 1500, 950);
            Find<ComboBox>(window, "ThemeInput").SelectedValue = "global.neon";
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            Assert.Equal(new[] { cs, lua }, window.Project!.ApplicationPlugins);
            Assert.Contains(Find<Panel>(window, "PluginToolbar").Children.OfType<Button>(), b => Equals(b.Content, "Инструменты расширения"));
            Invoke(window, "BtnBack_Click", window, new RoutedEventArgs()); await Idle(window);
            Assert.Null(window.Project); Assert.Equal("global.counter", Assert.Single(PluginCatalog.Current!.Blocks).Id);
            Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs());
            list.SelectedIndex = 0; Assert.True(Find<Button>(window, "RemoveApplicationPluginButton").IsEnabled);
            Invoke(window, "RemoveApplicationPlugin_Click", window, new RoutedEventArgs());
            list.SelectedIndex = 0; Invoke(window, "RemoveApplicationPlugin_Click", window, new RoutedEventArgs());
            Assert.Empty(list.Items); Assert.Equal(2, (await store.LoadAsync()).ApplicationPlugins.Count);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            var saved = await store.LoadAsync();
            Assert.Empty(saved.ApplicationPlugins); Assert.Equal("Автор", saved.UserName); Assert.Equal("stars", saved.WelcomeObjects);
            Assert.Null(PluginCatalog.Current); Assert.Equal("kne", Find<ComboBox>(window, "ThemeInput").SelectedValue);
            Assert.Equal(CSharpSource, File.ReadAllText(cs)); Assert.Equal(LuaSource, File.ReadAllText(lua));
            Assert.DoesNotContain("Не удалось", Find<TextBlock>(window, "ApplicationPluginsStatus").Text);
        }
        finally { window.Close(); PluginCatalog.Current = previous; }
        var restored = Window(store);
        try { await LoadSettings(restored); Assert.Empty(Find<ListBox>(restored, "ApplicationPluginsList").Items); Assert.Null(PluginCatalog.Current); }
        finally { restored.Close(); PluginCatalog.Current = previous; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Settings_RestoresGlobalPaletteAndReportsBrokenSource_ThenCanRemoveIt(bool missing) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var store = Store(fixture);
        string source = External(fixture, "global.cs", CSharpSource);
        await store.SaveAsync(new UserSettings { ApplicationPlugins = [source], Theme = "global.neon" });
        var previous = PluginCatalog.Current; var window = Window(store);
        try
        {
            await LoadSettings(window);
            Assert.Equal("global.neon", Find<ComboBox>(window, "ThemeInput").SelectedValue);
            Assert.Equal(Color.FromRgb(0xD7, 0x4D, 0xFF), ((SolidColorBrush)window.FindResource("KNE.AccentBrush")).Color);
            Assert.Null(window.Project);
        }
        finally { window.Close(); PluginCatalog.Current = previous; }
        if (missing) File.Delete(source); else File.WriteAllText(source, "not C# source");
        var broken = Window(store);
        try
        {
            await LoadSettings(broken); Invoke(broken, "BtnSettings_Click", broken, new RoutedEventArgs());
            Assert.Null(PluginCatalog.Current);
            Assert.Equal("kne", Find<ComboBox>(broken, "ThemeInput").SelectedValue);
            Assert.Contains("Не удалось загрузить", Find<TextBlock>(broken, "ApplicationPluginsStatus").Text);
            Assert.Contains("global.cs", Find<TextBlock>(broken, "ApplicationPluginsStatus").Text);
            var list = Find<ListBox>(broken, "ApplicationPluginsList"); Assert.Single(list.Items.Cast<object>());
            list.SelectedIndex = 0; Invoke(broken, "RemoveApplicationPlugin_Click", broken, new RoutedEventArgs());
            await WorkspaceTabsTests.Invoke(broken, "SaveSettingsAsync");
            Assert.Empty((await store.LoadAsync()).ApplicationPlugins); Assert.Equal(!missing, File.Exists(source));
            Assert.DoesNotContain("Не удалось", Find<TextBlock>(broken, "ApplicationPluginsStatus").Text);
        }
        finally { broken.Close(); PluginCatalog.Current = previous; }
    });

    [Fact]
    public Task Settings_AdditionRejectsInvalidSelectionAtomically_AndEnforcesFileAndListLimits() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = Window(Store(fixture));
        string valid = External(fixture, "valid.lua", ""), invalid = External(fixture, "invalid.txt", "");
        try
        {
            await LoadSettings(window);
            var list = Find<ListBox>(window, "ApplicationPluginsList");
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(window, "AddApplicationPluginFiles", (object)new[] { valid, invalid }));
            Assert.IsType<ArgumentException>(error.InnerException); Assert.Empty(list.Items);
            Assert.Throws<FileNotFoundException>(() => ApplicationPluginFiles.Validate(Path.Combine(fixture.Root, "missing.cs")));
            using (var sparse = File.Create(valid)) sparse.SetLength(ApplicationPluginFiles.MaximumBytes);
            Assert.Equal(valid, ApplicationPluginFiles.Validate(valid));
            using (var sparse = File.OpenWrite(valid)) sparse.SetLength(ApplicationPluginFiles.MaximumBytes + 1);
            Assert.Throws<InvalidDataException>(() => ApplicationPluginFiles.Validate(valid)); Assert.Empty(list.Items);
            var files = Enumerable.Range(0, 65).Select(i => External(fixture, i + ".lua", "")).ToArray();
            Invoke(window, "AddApplicationPluginFiles", (object)files.Take(64).ToArray()); Assert.Equal(64, list.Items.Count);
            var limit = Assert.Throws<TargetInvocationException>(() => Invoke(window, "AddApplicationPluginFiles", (object)new[] { files[64] }));
            Assert.IsType<ArgumentException>(limit.InnerException); Assert.Equal(64, list.Items.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task GlobalSources_RunInWorkerWithProjectManifests_AndDuplicateSourceExecutesOnlyOnce()
    {
        using var fixture = new TempProject();
        string cs = External(fixture, "global.cs", CSharpSource), lua = fixture.Write("plugins/shared.lua", LuaSource);
        fixture.Write("plugins/shared.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("shared", "plugins/shared.lua")));
        fixture.Write("plugins/project.cs", "using KNE; public sealed class ProjectExtension : IKnePlugin { public void Execute(IPluginContext c) { c.Api.On(\"on_scene_start\", e => e.Context.Set(\"project_loaded\", true)); } }");
        fixture.Write("plugins/project.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("project", "plugins/project.cs")));
        var graph = VisualScript.Create(); graph.Nodes.Single(n => n.Id == "dialogue").Text = "hello";
        var block = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", "Глобальный блок", "", "#7C5CFF", new PluginBlock("global.counter", "Глобальный блок", "logic")));
        graph.Nodes.Single(n => n.Id == "start").Next = block.Id; block.Options[0].Target = "dialogue"; graph.Nodes.Add(block);
        fixture.Write("game/scenes/main.krl", graph.Compile());
        var project = await fixture.LoadAsync(); project.ApplicationPlugins = [cs, lua];
        Assert.DoesNotContain(GameScripts.Load(project).Functions, f => f.Path == "plugins/shared.lua");
        await using var session = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        await session.StartAsync(); Assert.Equal("global:hello [lua]", session.Story.Text);
        var saved = await session.GetCheckpointAsync();
        Assert.Equal(1, saved.State.GetProperty("global_runs").GetDouble()); Assert.True(saved.State.GetProperty("project_loaded").GetBoolean());
        await session.RestoreAsync(saved); Assert.Equal("global:hello [lua]", session.Story.Text);
        Assert.Empty((await fixture.LoadAsync()).ApplicationPlugins);
        // Only source bytes change: replayed dialogue stays identical, so a missing fingerprint hash cannot be hidden by a frame mismatch.
        File.WriteAllText(cs, CSharpSource + "\n// source revision");
        await using var changed = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => changed.RestoreAsync(saved));
        Assert.Contains("Сохранение относится к другому проекту или сценарии изменились.", error.Message);
    }

    [Fact]
    public async Task FrameAndSceneTesting_InheritGlobalPlugins_AndSafeModeIgnoresMissingGlobalSource()
    {
        using var fixture = new TempProject(); string cs = External(fixture, "global.cs", CSharpSource);
        string code = "function main()\n  d(nil, 'hello')\nend";
        string path = fixture.Write("game/scenes/main.krl", code);
        var project = await fixture.LoadAsync(); project.ApplicationPlugins = [cs];
        var selection = new EditorSelection(path, code, 2, null, false, "main", FromMain: false);
        await using var preview = new EditorPreview(project, _ => { }); await preview.SeekAsync(selection);
        Assert.True(preview.Reached); Assert.Equal("global:hello", preview.Story.Text);
        await using var scene = new SceneTestSession(project, selection); await scene.RestartAsync(); Assert.Equal("global:hello", scene.Story.Text);
        project.ApplicationPlugins = [Path.Combine(fixture.Root, "missing.cs")];
        await using var safe = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30), pluginsEnabled: false);
        await safe.StartAsync(); Assert.Equal("hello", safe.Story.Text);
        Assert.Equal("hello", (await safe.GetCheckpointAsync()).Frame.Text);
        string? previous = Environment.GetEnvironmentVariable(EngineLaunch.PluginsEnvironment);
        try
        {
            Environment.SetEnvironmentVariable(EngineLaunch.PluginsEnvironment, "1");
            await using var safeScene = new SceneTestSession(project, selection);
            await safeScene.RestartAsync(); Assert.Equal("hello", safeScene.Story.Text);
        }
        finally { Environment.SetEnvironmentVariable(EngineLaunch.PluginsEnvironment, previous); }
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1500)]
    public Task HeaderAndSettings_KeepFullTranslationLabelGapsAndPluginControlsVisible(int width) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = Window(Store(fixture));
        try
        {
            await LoadSettings(window); await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            var root = (FrameworkElement)window.Content; Layout(root, width, 700);
            Rect Bounds(FrameworkElement e) => e.TransformToVisual(root).TransformBounds(new Rect(e.RenderSize));
            var toolbar = Find<Panel>(window, "PluginToolbar");
            var translations = Assert.Single(toolbar.Children.OfType<Button>(), b => Equals(b.Content, "Диалоги / переводы"));
            var appearance = Find<Button>(window, "AppearanceButton");
            Assert.InRange(Bounds(translations).Right, 1, width); Assert.True(Bounds(translations).Left - Bounds(appearance).Right >= 15);
            var text = new FormattedText((string)translations.Content, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(translations.FontFamily, translations.FontStyle, translations.FontWeight, translations.FontStretch), translations.FontSize, Brushes.White, 1);
            Assert.True(translations.ActualWidth >= text.WidthIncludingTrailingWhitespace + translations.Padding.Left + translations.Padding.Right);
            Snapshot(root, "header-" + width + ".png", width, 700);
            Invoke(window, "BtnBack_Click", window, new RoutedEventArgs()); await Idle(window);
            Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs()); Layout(root, width, 700);
            foreach (string name in new[] { "PluginsSettingsGroup", "ApplicationPluginsList", "AddApplicationPluginButton", "RemoveApplicationPluginButton" })
            {
                var element = Find<FrameworkElement>(window, name); var bounds = Bounds(element);
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0); Assert.InRange(bounds.Left, 0, width); Assert.InRange(bounds.Right, 0, width);
                Assert.InRange(bounds.Top, 0, 700); Assert.InRange(bounds.Bottom, 0, 700);
            }
            Assert.Equal(ScrollBarVisibility.Hidden, ScrollViewer.GetVerticalScrollBarVisibility(Find<ListBox>(window, "ApplicationPluginsList")));
            Snapshot(root, "settings-" + width + ".png", width, 700);
        }
        finally { window.Close(); }
    });

    private static UserSettingsStore Store(TempProject fixture) => new(Path.Combine(fixture.Root, "profile", "settings.json"));
    private static string External(TempProject fixture, string name, string source) { string path = Path.Combine(fixture.Root, name); File.WriteAllText(path, source); return path; }
    private static MainWindow Window(UserSettingsStore store) { var window = new MainWindow(); typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store); return window; }
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static async Task LoadSettings(MainWindow window) { Invoke(window, "Window_Loaded", window, new RoutedEventArgs()); await Idle(window); }
    private static async Task Idle(MainWindow window)
    {
        for (int i = 0; i < 500; i++)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!(bool)typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Settings operation did not finish.");
    }
    private static void Layout(FrameworkElement root, int width, int height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Snapshot(FrameworkElement root, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_APP_PLUGIN_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
