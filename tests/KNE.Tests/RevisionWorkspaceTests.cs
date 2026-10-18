using System.IO;
using System.Reflection;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class RevisionWorkspaceTests
{
    [Fact]
    public async Task Help_ExportsAnOfflineHtmlReferenceForEveryBlock_WithActualParameterAndPortSemantics()
    {
        using var fixture = new Fixture();
        string html = BlockHelpDocument.BuildHtml();
        string decoded = WebUtility.HtmlDecode(html);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Equal(BlockLibrary.Definitions.Count, Regex.Matches(html, "<article id=").Count);
        foreach (var definition in BlockLibrary.Definitions)
        {
            var article = Regex.Match(decoded, "<article id=\"" + definition.Kind + "\">(?<body>.*?)</article>", RegexOptions.Singleline);
            Assert.True(article.Success, definition.Kind.ToString());
            Assert.Contains("<h3>" + definition.Title + "</h3>", article.Value);
            Assert.Contains(definition.Description, article.Value);
            Assert.Contains("<li>", article.Value);
        }
        Assert.Contains("Портов нет. Заметка не выполняется.", decoded);
        string triggers = string.Join("\n", BlockHelpDocument.Parameters(ScriptNodeKind.TriggerKey));
        Assert.Contains("Вход подключён: ветка останавливается", triggers);
        Assert.Contains("Без входящей связи: независимый обработчик", triggers);
        Assert.Contains("Автоповтор", triggers);
        string input = string.Join("\n", BlockHelpDocument.Parameters(ScriptNodeKind.TextInput));
        Assert.Contains("1–256 (по умолчанию 32)", input);
        Assert.Contains("Успех / Ошибка / Отмена", input);
        Assert.Contains("регулярное выражение", input);
        Assert.Contains("1280×720", string.Join("\n", BlockHelpDocument.Parameters(ScriptNodeKind.TriggerHotspot)));
        string subsystem = string.Join("\n", BlockHelpDocument.Parameters(ScriptNodeKind.Subsystem));
        Assert.Contains(".knegraph", subsystem); Assert.Contains("Двойной клик", subsystem);
        Assert.Contains("выбор и ввод требуют ожидания", subsystem);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        string directory = Path.Combine(fixture.Root, "help");
        string path = await BlockHelpDocument.WriteAsync(directory);
        Assert.Equal(Path.Combine(directory, "blocks.html"), path);
        Assert.Equal(html, await File.ReadAllTextAsync(path));
        File.WriteAllText(path, "stale manual");
        Assert.Equal(path, await BlockHelpDocument.WriteAsync(directory));
        Assert.Equal(html, await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { "blocks.html" }, Directory.EnumerateFiles(directory).Select(Path.GetFileName));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlockHelpDocument.WriteAsync(directory, cancellation.Token));
        Assert.Equal(html, await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { "blocks.html" }, Directory.EnumerateFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public Task HelpButton_OpensTheFreshLocalHtmlInsteadOfAnOutdatedModal() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new Fixture();
        var window = Window(fixture, new());
        try
        {
            string directory = Path.Combine(fixture.Root, "manual");
            window.HelpDocumentDirectory = directory;
            var launched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.OpenHelpDocument = path => launched.TrySetResult(path);
            Invoke(window, "BtnHelp_Click", new Button(), new RoutedEventArgs());
            string opened = await launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Path.Combine(directory, "blocks.html"), opened);
            Assert.Contains("<article id=\"Subsystem\">", await File.ReadAllTextAsync(opened));
            Assert.Contains("<article id=\"Formula\">", await File.ReadAllTextAsync(opened));
            await Idle(window);
            Assert.Equal(Visibility.Visible, WorkspaceTabsTests.Find<StackPanel>(window, "WelcomePanel").Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task ProjectHistory_RecordsUtcLastOpen_HidesDiscoveredEntryWithoutDeleting_AndReopeningRestoresIt()
    {
        using var fixture = new Fixture();
        var settings = new UserSettings { ProjectsStoragePath = fixture.Root, Theme = "dark" };
        DateTimeOffset before = DateTimeOffset.UtcNow;
        settings.RememberProject(fixture.Project);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        DateTimeOffset opened = Assert.Single(settings.ProjectLastOpened).Value;
        Assert.InRange(opened, before, after); Assert.Equal(TimeSpan.Zero, opened.Offset);
        var project = Assert.Single(await ProjectStorage.DiscoverAsync(settings));
        Assert.Equal(fixture.Project, project.Path); Assert.Equal(opened, project.LastOpened);
        Assert.Contains(opened.ToLocalTime().ToString("g"), project.LastOpenedLabel);
        settings.ForgetProject(fixture.Project.ToUpperInvariant());
        Assert.Empty(settings.RecentProjects); Assert.Empty(settings.ProjectLastOpened);
        Assert.Empty(await ProjectStorage.DiscoverAsync(settings));
        Assert.True(File.Exists(Path.Combine(fixture.Project, "project.json")));
        await fixture.Store.SaveAsync(settings);
        var hidden = await fixture.Store.LoadAsync();
        Assert.Equal("dark", hidden.Theme); Assert.Single(hidden.HiddenProjects);
        Assert.Empty(await ProjectStorage.DiscoverAsync(hidden));
        hidden.RememberProject(fixture.Project);
        Assert.Empty(hidden.HiddenProjects); Assert.Single(hidden.RecentProjects);
        await fixture.Store.SaveAsync(hidden);
        var restored = await fixture.Store.LoadAsync();
        Assert.Equal(fixture.Project, Assert.Single(await ProjectStorage.DiscoverAsync(restored)).Path);
        Assert.Equal(hidden.ProjectLastOpened[fixture.Project], restored.ProjectLastOpened[fixture.Project]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Theme\":null,\"HiddenProjects\":null,\"ProjectLastOpened\":null}")]
    [InlineData("{\"Theme\":\"unknown\"}")]
    public async Task Settings_LegacyHistoryAndInvalidTheme_LoadSafeLightDefaults(string json)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Store.SettingsPath, json);
        var settings = await fixture.Store.LoadAsync();
        Assert.Equal("light", settings.Theme);
        Assert.Empty(settings.HiddenProjects); Assert.Empty(settings.ProjectLastOpened);
        var project = Assert.Single(await ProjectStorage.DiscoverAsync(new UserSettings { ProjectsStoragePath = fixture.Root }));
        Assert.Null(project.LastOpened); Assert.Equal("Ещё не открывался в KNE", project.LastOpenedLabel);
    }

    [Fact]
    public async Task Welcome_RendersPathAndLastOpen_AndCrossHidesOnlyTheRecordAcrossReload()
    {
        using var fixture = new Fixture();
        var opened = new DateTimeOffset(2025, 3, 4, 12, 30, 0, TimeSpan.Zero);
        var settings = new UserSettings { ProjectsStoragePath = fixture.Root, RecentProjects = [fixture.Project], ProjectLastOpened = new() { [fixture.Project] = opened } };
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Window(fixture, settings);
            try
            {
                await WorkspaceTabsTests.Invoke(window, "RefreshStoredProjectsAsync"); Layout(window);
                var recent = WorkspaceTabsTests.Find<ItemsControl>(window, "RecentProjectsList");
                Assert.Contains(Descendants(recent).OfType<TextBlock>(), text => text.Text == fixture.Project);
                Assert.Contains(Descendants(recent).OfType<TextBlock>(), text => text.Text.Contains(opened.ToLocalTime().ToString("g")));
                Render(window, "welcome-history.png");
                var cross = Assert.Single(Descendants(recent).OfType<Button>(), button => Equals(button.Content, "×"));
                Assert.Equal(fixture.Project, cross.Tag);
                cross.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle(window);
                Assert.Empty(recent.Items);
                Assert.True(File.Exists(Path.Combine(fixture.Project, "project.json")));
                var saved = await fixture.Store.LoadAsync();
                Assert.Empty(saved.RecentProjects); Assert.Empty(saved.ProjectLastOpened);
                Assert.Equal(fixture.Project, Assert.Single(saved.HiddenProjects));
                Assert.Empty(await ProjectStorage.DiscoverAsync(saved));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(MessageBoxResult.Cancel)]
    [InlineData(MessageBoxResult.No)]
    [InlineData(MessageBoxResult.Yes)]
    public async Task HomeButton_PromptsForDrafts_AndHonorsCancelDiscardSaveWithoutClosingWindow(MessageBoxResult answer)
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Project, "game", "scenes", "main.krl");
        string original = File.ReadAllText(path);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Window(fixture, new()); int closed = 0; window.Closed += (_, _) => closed++;
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.Project, false);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", path);
                var tabs = WorkspaceTabsTests.Find<TabControl>(window, "DocumentTabs");
                var editor = (ScriptEditor)((TabItem)tabs.Items[0]).Tag;
                WorkspaceTabsTests.Find<KrlCodeEditor>(editor, "CodeEditor").Text = original + "\n-- draft";
                string? prompted = null; editor.ConfirmSave = file => { prompted = file; return answer; };
                var home = Assert.Single(Descendants(WorkspaceTabsTests.Find<WrapPanel>(window, "ExitActionsGroup")).OfType<Button>(), button => Equals(button.Content, "Главный экран"));
                home.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(window);
                Assert.Equal(path, prompted); Assert.Equal(0, closed);
                Assert.Equal(answer == MessageBoxResult.Yes ? original + "\n-- draft" : original, File.ReadAllText(path));
                Assert.Equal(answer == MessageBoxResult.Cancel ? Visibility.Collapsed : Visibility.Visible, WorkspaceTabsTests.Find<StackPanel>(window, "WelcomePanel").Visibility);
                Assert.Equal(answer == MessageBoxResult.Cancel ? 1 : 0, tabs.Items.Count);
                if (answer == MessageBoxResult.Cancel) { Assert.True(editor.HasUnsavedChanges); editor.ConfirmSave = _ => MessageBoxResult.No; await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", editor); }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public Task EditorTheme_IsWindowScoped_AndRepeatedSwitchesReplacePaletteWithoutDuplicatingStyles() => WorkspaceTabsTests.Sta(() =>
    {
        var first = new Window(); var second = new Window();
        try
        {
            EditorTheme.Apply(first, "dark"); EditorTheme.Apply(second, "light");
            Color dark = ((SolidColorBrush)first.Background).Color;
            Color light = ((SolidColorBrush)second.Background).Color;
            Assert.NotEqual(light, dark);
            int styles = first.Resources.MergedDictionaries.Count;
            EditorTheme.Apply(first, "light"); EditorTheme.Apply(first, "dark");
            Assert.Equal(styles, first.Resources.MergedDictionaries.Count);
            Assert.Equal(dark, ((SolidColorBrush)first.Background).Color);
            Assert.Equal(light, ((SolidColorBrush)second.Background).Color);
            EditorTheme.Apply(first, "unsupported");
            Assert.Equal(light, ((SolidColorBrush)first.Background).Color);
        }
        finally { first.Close(); second.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ThemeSelector_UpdatesExistingEditorControls_RoundTripsAndRestoresLightWithoutTouchingGamePalette()
    {
        using var fixture = new Fixture(); string enginePath = Path.Combine(fixture.Project, "engine", "game.json"); string authored = File.ReadAllText(enginePath);
        await WorkspaceTabsTests.Sta(async () =>
        {
            string hiddenPath = Path.Combine(fixture.Root, "hidden");
            var lastOpened = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);
            var window = Window(fixture, new UserSettings { ProjectsStoragePath = fixture.Root, RecentProjects = [fixture.Project], HiddenProjects = [hiddenPath], ProjectLastOpened = new() { [fixture.Project] = lastOpened } });
            try
            {
                Invoke(window, "BtnSettings_Click", new Button(), new RoutedEventArgs());
                var theme = WorkspaceTabsTests.Find<ComboBox>(window, "ThemeInput");
                var editorPath = WorkspaceTabsTests.Find<TextBox>(window, "TxtEditorPath");
                var code = WorkspaceTabsTests.Find<KrlCodeEditor>(WorkspaceTabsTests.Find<ScriptEditor>(window, "SceneEditor"), "CodeEditor");
                theme.SelectedValue = "light"; Layout(window);
                Color light = ((SolidColorBrush)editorPath.Background).Color;
                theme.SelectedValue = "dark"; Layout(window);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#191C24"), ((SolidColorBrush)window.Background).Color);
                Assert.NotEqual(light, ((SolidColorBrush)editorPath.Background).Color);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#E5EAF3"), ((SolidColorBrush)editorPath.Foreground).Color);
                Assert.Equal("dark", code.EditorThemeName);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#1E1E1E"), ((SolidColorBrush)code.Background).Color);
                Render(window, "settings-dark.png");
                await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
                var saved = await fixture.Store.LoadAsync();
                Assert.Equal("dark", saved.Theme);
                Assert.Equal(fixture.Project, Assert.Single(saved.RecentProjects));
                Assert.Equal(hiddenPath, Assert.Single(saved.HiddenProjects));
                Assert.Equal(lastOpened, saved.ProjectLastOpened[fixture.Project]);
                var restored = Window(fixture, new());
                try
                {
                    Invoke(restored, "Window_Loaded", restored, new RoutedEventArgs()); await Idle(restored);
                    Assert.Equal("dark", WorkspaceTabsTests.Find<ComboBox>(restored, "ThemeInput").SelectedValue);
                    Assert.Equal((Color)ColorConverter.ConvertFromString("#191C24"), ((SolidColorBrush)restored.Background).Color);
                }
                finally { restored.Close(); }
                theme.SelectedValue = "light"; Layout(window);
                Assert.Equal(light, ((SolidColorBrush)editorPath.Background).Color);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#F7F6FA"), ((SolidColorBrush)window.Background).Color);
                Assert.Equal("light", code.EditorThemeName);
                Assert.Equal((Color)ColorConverter.ConvertFromString("#FFFFFF"), ((SolidColorBrush)code.Background).Color);
                Render(window, "settings-light.png");
                await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
                Assert.Equal("light", (await fixture.Store.LoadAsync()).Theme);
                Assert.Equal(authored, File.ReadAllText(enginePath));
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow Window(Fixture fixture, UserSettings settings)
    {
        var window = new MainWindow();
        typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, settings);
        typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, fixture.Store);
        return window;
    }
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task Idle(MainWindow window)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!(bool)typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Workspace operation did not finish.");
    }
    private static void Layout(MainWindow window)
    {
        var content = (FrameworkElement)window.Content; content.Measure(new Size(1460, 900)); content.Arrange(new Rect(0, 0, 1460, 900)); content.UpdateLayout();
    }
    private static void Render(MainWindow window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_REVISION_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); Layout(window);
        var image = new RenderTargetBitmap(1460, 900, 96, 96, PixelFormats.Pbgra32); image.Render((FrameworkElement)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private sealed class Fixture : IDisposable
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kne-calculations-theme-tests");
        public string Root { get; } = Path.Combine(Scratch, "revision-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public UserSettingsStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Project = ProjectCreator.CreateProject("Revision", Root); Store = new(Path.Combine(Root, "settings.json")); }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(target, true);
        }
    }
}
