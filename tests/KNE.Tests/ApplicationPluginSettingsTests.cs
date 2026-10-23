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

public sealed class ApplicationPluginSettingsTests : IDisposable
{
    private readonly PluginApi? previousCatalog = PluginCatalog.Current;
    public ApplicationPluginSettingsTests() => PluginCatalog.Current = null;
    public void Dispose() => PluginCatalog.Current = previousCatalog;

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
            Assert.InRange(Bounds(translations).Right, 1, width);
            Assert.Equal(Bounds(appearance).Top, Bounds(translations).Top, 1);
            Assert.Equal(Bounds(appearance).Right, Bounds(translations).Left, 1);
            Assert.InRange(Find<FrameworkElement>(window, "ProjectHeader").ActualHeight, 25, 40);
            var text = new FormattedText((string)translations.Content, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(translations.FontFamily, translations.FontStyle, translations.FontWeight, translations.FontStretch), translations.FontSize, Brushes.White, 1);
            Assert.True(translations.ActualWidth >= text.WidthIncludingTrailingWhitespace + translations.Padding.Left + translations.Padding.Right);
            Assert.InRange(translations.ActualWidth - text.WidthIncludingTrailingWhitespace, 0, 3);
            Assert.Equal(0, translations.Padding.Left); Assert.Equal(0, translations.Padding.Right); Assert.Equal(0, translations.MinWidth);
            Snapshot(root, "header-" + width + ".png", width, 700);
            Invoke(window, "BtnBack_Click", window, new RoutedEventArgs()); await Idle(window);
            Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs()); Layout(root, width, 700);
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "GeneralSettingsPage").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(window, "PluginsSettingsPage").Visibility);
            Assert.False(Find<Button>(window, "PreviousSettingsPageButton").IsEnabled);
            Snapshot(root, "settings-general-" + width + ".png", width, 700);
            Find<Button>(window, "NextSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width, 700);
            Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(window, "GeneralSettingsPage").Visibility);
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "PluginsSettingsPage").Visibility);
            Assert.False(Find<Button>(window, "NextSettingsPageButton").IsEnabled);
            Assert.True(Find<Button>(window, "PreviousSettingsPageButton").IsEnabled);
            foreach (string name in new[] { "PluginsSettingsGroup", "ApplicationPluginsList", "AddApplicationPluginButton", "RemoveApplicationPluginButton", "PluginDetailsGroup" })
            {
                var element = Find<FrameworkElement>(window, name); var bounds = Bounds(element);
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0); Assert.InRange(bounds.Left, 0, width); Assert.InRange(bounds.Right, 0, width);
                Assert.InRange(bounds.Top, 0, 700); Assert.InRange(bounds.Bottom, 0, 700);
            }
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(Find<ListBox>(window, "ApplicationPluginsList")));
            Snapshot(root, "settings-" + width + ".png", width, 700);
            Find<Button>(window, "PreviousSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(root, width, 700);
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "GeneralSettingsPage").Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Settings_PluginPagesKeepDraftAndDisplayPublisherMetadataWithoutExecutingSource() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var store = Store(fixture); var window = Window(store);
        string cs = External(fixture, "documented.cs", "invalid source must not execute on selection");
        string lua = External(fixture, "plain.lua", "error('must not run')");
        File.WriteAllText(Path.ChangeExtension(cs, ".kneplugin.json"), JsonSerializer.Serialize(new PluginManifest("documented", "documented.cs")
        { Name = "Документированный плагин", Author = "Автор KNE", CreatedAt = new(2026, 10, 20), ReadmeFile = "usage.md", LicenseFile = "LICENSE.plugin" }));
        External(fixture, "usage.md", "# Инструкция\nДобавьте блок в сцену."); External(fixture, "LICENSE.plugin", "MIT\nCopyright Автор KNE");
        try
        {
            await LoadSettings(window); Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs());
            Invoke(window, "AddApplicationPluginFiles", (object)new[] { cs, lua });
            Find<Button>(window, "NextSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = Find<ListBox>(window, "ApplicationPluginsList"); list.SelectedIndex = 0;
            Assert.Equal("Документированный плагин", Find<TextBlock>(window, "PluginNameText").Text);
            Assert.Equal("Автор KNE", Find<TextBlock>(window, "PluginAuthorText").Text);
            Assert.Equal("2026-10-20", Find<TextBlock>(window, "PluginCreatedAtText").Text);
            Assert.Equal("C#", Find<TextBlock>(window, "PluginLanguageText").Text);
            Assert.Equal("# Инструкция\nДобавьте блок в сцену.", Find<TextBox>(window, "PluginReadmeText").Text);
            Assert.Equal("MIT\nCopyright Автор KNE", Find<TextBox>(window, "PluginLicenseText").Text);
            var root = (FrameworkElement)window.Content; Layout(root, 1100, 700); Snapshot(root, "plugin-metadata-1100.png", 1100, 700);
            Find<Button>(window, "PreviousSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>(window, "NextSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, list.SelectedIndex); Assert.Equal(2, list.Items.Count); Assert.Empty((await store.LoadAsync()).ApplicationPlugins);
            list.SelectedIndex = 1;
            Assert.Equal("Lua", Find<TextBlock>(window, "PluginLanguageText").Text);
            Assert.Equal("Не указан", Find<TextBlock>(window, "PluginAuthorText").Text);
            Assert.Equal("Не указана", Find<TextBlock>(window, "PluginCreatedAtText").Text);
            Assert.Equal("Инструкция не предоставлена.", Find<TextBox>(window, "PluginReadmeText").Text);
            Assert.Equal("Лицензия не указана.", Find<TextBox>(window, "PluginLicenseText").Text);
            list.SelectedIndex = -1; Assert.Equal("Выберите плагин", Find<TextBlock>(window, "PluginNameText").Text);
            Assert.Empty(Find<TextBox>(window, "PluginReadmeText").Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"readmeFile\":\"../outside.md\"}")]
    [InlineData("{\"createdAt\":\"yesterday\"}")]
    public Task Settings_InvalidMetadataShowsLocalDiagnosticAndKeepsDraft(string json) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = Window(Store(fixture));
        string source = External(fixture, "broken.lua", ""); File.WriteAllText(Path.ChangeExtension(source, ".kneplugin.json"), json);
        try
        {
            await LoadSettings(window); Invoke(window, "AddApplicationPluginFiles", (object)new[] { source });
            Assert.Contains("Не удалось прочитать", Find<TextBlock>(window, "PluginMetadataStatus").Text);
            Assert.Single(Find<ListBox>(window, "ApplicationPluginsList").Items.Cast<object>());
            Assert.Empty(Find<TextBox>(window, "PluginReadmeText").Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("missing.cs", "", false)]
    [InlineData("broken.cs", "not C#", true)]
    [InlineData("broken.lua", "function (", true)]
    [InlineData("incompatible.lua", "", true)]
    public void PluginLoadFailuresNotifyRecoveryWithSourceAndCause(string name, string code, bool exists)
    {
        using var fixture = new TempProject(); string source = Path.Combine(fixture.Root, name);
        if (exists) File.WriteAllText(source, code);
        if (name == "incompatible.lua") File.WriteAllText(Path.ChangeExtension(source, ".kneplugin.json"), "{\"apiVersion\":2}");
        using var host = new PluginHost(fixture.Root, true, new object()); var failures = new List<PluginFailureException>();
        PluginFailure.Detected += failures.Add;
        try
        {
            var error = Assert.Throws<PluginFailureException>(() => host.Load(() => new EditorPluginContext(host.Api), applicationPlugins: [source]));
            Assert.Same(error, Assert.Single(failures)); Assert.Equal(source, error.SourcePath);
            Assert.Contains(name, error.Message); Assert.NotNull(error.InnerException); Assert.Empty(host.Api.Blocks);
            var diagnostics = new EngineDiagnostics(Path.Combine(fixture.Root, "failure.json"), 1, 1, false);
            var report = diagnostics.Report(error, "Ошибка плагина");
            Assert.Contains(name, report.Message); Assert.Contains(error.InnerException!.Message, report.Exception);
            Assert.Equal("Ошибка плагина", EngineDiagnostics.Read(diagnostics.SessionPath).Failure!.Kind);
        }
        finally { PluginFailure.Detected -= failures.Add; }
    }

    [Fact]
    public void ConflictingPluginsAndDeferredCommandsNotifyRecoveryExactlyOnce()
    {
        using var fixture = new TempProject();
        string first = External(fixture, "first.cs", CSharpSource), second = External(fixture, "second.cs", CSharpSource);
        using var host = new PluginHost(fixture.Root, true, new object()); var failures = new List<PluginFailureException>();
        PluginFailure.Detected += failures.Add;
        try
        {
            var conflict = Assert.Throws<PluginFailureException>(() => host.Load(() => new EditorPluginContext(host.Api), applicationPlugins: [first, second]));
            Assert.Equal(second, conflict.SourcePath); Assert.Same(conflict, Assert.Single(failures)); Assert.Contains("уже зарегистрирован", conflict.Message);
            failures.Clear(); host.Api.SourcePath = first;
            host.Api.RegisterCommand(new("test.failure", "Сбой", _ => throw new InvalidOperationException("command failed")));
            host.Api.SourcePath = second; // Attribution must retain the source that registered the callback.
            var commandError = Assert.Throws<PluginFailureException>(() => host.Api.commands.Single(c => c.Id == "test.failure").Execute(new(fixture.Root, () => null, _ => { })));
            Assert.Equal(first, commandError.SourcePath); Assert.Same(commandError, Assert.Single(failures)); Assert.Contains("command failed", commandError.Message);
            failures.Clear();
            host.Api.RegisterCommand(new("test.cancel", "Отмена", _ => throw new OperationCanceledException()));
            Assert.Throws<OperationCanceledException>(() => host.Api.commands.Single(c => c.Id == "test.cancel").Execute(new(fixture.Root, () => null, _ => { })));
            Assert.Empty(failures);
        }
        finally { PluginFailure.Detected -= failures.Add; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPluginFailureRetainsRecoveryFlagSourceAndOriginalDetails(bool lua)
    {
        using var fixture = new TempProject();
        string source = External(fixture, lua ? "event.lua" : "event.cs", lua
            ? "function fail_text(text, speaker) error('plugin worker failure') end\nkne.before_text('fail_text')"
            : "using KNE; public class Failure : IKnePlugin { public void Execute(IPluginContext c) { c.Api.BeforeExecute(\"Dialogue\", e => throw new System.InvalidOperationException(\"plugin worker failure\")); } }");
        var project = await fixture.LoadAsync(); project.ApplicationPlugins = [source];
        await using var session = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        var failures = new List<PluginFailureException>(); PluginFailure.Detected += failures.Add;
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
            var report = GameErrorReport.TryGet(error)!;
            Assert.True(report.PluginFailure); Assert.Contains("plugin worker failure", report.Details);
            Assert.Contains(Path.GetFileName(source), report.Details);
            var recovery = Assert.Single(failures); Assert.Same(error, recovery.InnerException);
            var diagnostics = new EngineDiagnostics(Path.Combine(fixture.Root, "worker-failure.json"), 1, 1, false);
            var engineReport = diagnostics.Report(recovery, "Ошибка плагина");
            Assert.Contains("plugin worker failure", engineReport.Exception); Assert.Contains(Path.GetFileName(source), engineReport.Exception);
        }
        finally { PluginFailure.Detected -= failures.Add; }
    }

    [Fact]
    public Task PluginFailureRecoveryShowsOriginalReportAndOffersRestartWithoutPlugins() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var diagnostics = new EngineDiagnostics(Path.Combine(fixture.Root, "recovery.json"), 1, 1, false);
        var report = diagnostics.Report(new PluginFailureException("broken.cs", "Загрузка плагина", new InvalidOperationException("Plugin registration conflict")), "Ошибка плагина");
        EngineRecoveryAction? selected = null;
        var recovery = new EngineRecoveryWindow(report, diagnostics.ReportPath, action => selected = action);
        try
        {
            Assert.Contains("broken.cs", recovery.ReportText.Text); Assert.Contains("Plugin registration conflict", recovery.ReportText.Text);
            Assert.Contains(diagnostics.ReportPath, recovery.ReportText.Text);
            var root = (FrameworkElement)recovery.Content; Layout(root, 960, 680); Snapshot(root, "plugin-recovery.png", 960, 680);
            recovery.SafeRestartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(EngineRecoveryAction.RestartWithoutPlugins, selected);
            Assert.Contains("--disable-plugins", EngineLaunch.RestartInfo(true).ArgumentList);
        }
        finally { recovery.Close(); }
        await Task.CompletedTask;
    });

    [Fact]
    public async Task WorkerScriptErrorDoesNotRequestPluginRecovery()
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main() error('ordinary script failure') end");
        var project = await fixture.LoadAsync(); await using var session = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        var failures = new List<PluginFailureException>(); PluginFailure.Detected += failures.Add;
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
            Assert.False(GameErrorReport.TryGet(error)!.PluginFailure); Assert.Empty(failures);
        }
        finally { PluginFailure.Detected -= failures.Add; }
    }

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
