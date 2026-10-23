using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectFolderBrowserUiTests : IDisposable
{
    private readonly PluginApi? previous = PluginCatalog.Current;
    public ProjectFolderBrowserUiTests() => PluginCatalog.Current = null;
    public void Dispose() => PluginCatalog.Current = previous;

    [Theory]
    [InlineData(1100)]
    [InlineData(1500)]
    public Task OpenProject_ShowsRootTilesExactInstructionsAndWindowsIcons_WithinExistingWorkspace(int width) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string nested = fixture.Write("chapters/act1/scene.krl", "function scene() end");
        var window = Create(fixture);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); Layout(window, width);
            var tiles = Find<ListBox>(window, "FileTiles"); var root = (FrameworkElement)window.Content;
            Assert.Equal(Visibility.Visible, tiles.Visibility); Assert.Equal(Visibility.Collapsed, Find<DataGrid>(window, "FilesDataGrid").Visibility);
            Assert.Equal(fixture.ProjectPath, Find<TextBox>(window, "FileBrowserPath").Text);
            Assert.Equal(Visibility.Collapsed, Find<Button>(window, "FileBrowserBackButton").Visibility);
            Assert.Equal("В этом окне можно выбрать папку и файл, который вы хотите отредактировать. Чтоб перемещаться по папкам вы можете использовать либо файловый инспектор в панели слева, либо проводник в этом окне", Find<TextBlock>(window, "FileBrowserIntroduction").Text);
            var entries = Entries(window); Assert.Contains(entries, e => e.Name == "chapters" && e.IsDirectory); Assert.Contains(entries, e => e.Name == "project.json" && !e.IsDirectory);
            Assert.DoesNotContain(entries, e => e.File?.FilePath == nested);
            Assert.Equal(entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase), entries);
            foreach (var entry in entries)
            { Assert.True(Assert.IsAssignableFrom<ImageSource>(entry.Icon).IsFrozen); Assert.Same(entry.Icon, entry.Icon); }
            foreach (string name in new[] { "ProjectHeader", "FileBrowserIntroduction", "FileBrowserPath", "FileTiles" })
            {
                var element = Find<FrameworkElement>(window, name); var bounds = element.TransformToVisual(root).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Width > 0 && bounds.Height > 0); Assert.InRange(bounds.Left, 0, width); Assert.InRange(bounds.Right, 0, width); Assert.InRange(bounds.Bottom, 0, 700);
            }
            var folder = entries.Single(e => e.Name == "chapters"); var container = Assert.IsType<ListBoxItem>(tiles.ItemContainerGenerator.ContainerFromItem(folder));
            var label = FileBrowserDesignTests.Descendants(container).OfType<TextBlock>().Single(t => t.Text == "chapters");
            Assert.Same(folder, MainWindow.BrowserItemAtDoubleClick(tiles, label)); Assert.Null(MainWindow.FileAtDoubleClick(tiles, label));
            Assert.Null(MainWindow.BrowserItemAtDoubleClick(tiles, tiles));
            FileBrowserDesignTests.Capture(root, "project-tiles-" + width + ".png", new(width, 700));
        }
        finally { await Close(window); }
    });

    [Theory]
    [InlineData("tiles")]
    [InlineData("table")]
    public Task FolderNavigation_InspectorAndBackShareDirectory_AndNeverLeaveProject(string mode) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapters/act1/scene.krl", "function scene() end");
        Directory.CreateDirectory(Path.Combine(fixture.ProjectPath, "chapters", "empty"));
        var window = Create(fixture, mode);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            var bytes = window.Project!.Files.ToDictionary(f => f.FilePath, f => File.ReadAllBytes(f.FilePath));
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", Entries(window).Single(e => e.Name == "chapters"));
            Assert.Equal(Path.Combine(fixture.ProjectPath, "chapters"), Find<TextBox>(window, "FileBrowserPath").Text);
            Assert.Equal(new[] { "act1", "empty" }, Entries(window).Select(e => e.Name)); Assert.Equal(Visibility.Visible, Find<Button>(window, "FileBrowserBackButton").Visibility);
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", Entries(window).Single(e => e.Name == "empty")); Assert.Empty(Entries(window));
            Find<Button>(window, "FileBrowserBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("empty", ((ProjectBrowserEntry)Find<ListBox>(window, "FileTiles").SelectedItem).Name);
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", Entries(window).Single(e => e.Name == "act1"));
            Assert.Equal("scene.krl", Assert.Single(Entries(window)).Name);
            Layout(window, 1100);
            Assert.Equal(Path.Combine("chapters", "act1"), ((ProjectTreeNode)Find<TreeView>(window, "ProjectFilesTree").SelectedItem).RelativePath);
            Find<Button>(window, "FileBrowserBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>(window, "FileBrowserBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fixture.ProjectPath, Find<TextBox>(window, "FileBrowserPath").Text);
            Invoke(window, "FileBrowserBack_Click", window, new RoutedEventArgs()); Assert.Equal(fixture.ProjectPath, Find<TextBox>(window, "FileBrowserPath").Text);
            var tree = Find<TreeView>(window, "ProjectFilesTree"); Layout(window, 1100);
            var gameNode = tree.Items.Cast<ProjectTreeNode>().Single(n => n.Name == "game");
            Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(gameNode)).IsSelected = true;
            Assert.Equal(Path.Combine(fixture.ProjectPath, "game"), Find<TextBox>(window, "FileBrowserPath").Text);
            Assert.Contains(Entries(window), e => e.Name == "scenes" && e.IsDirectory);
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(window, "NavigateBrowserDirectory", "..", null!, true)); Assert.IsType<ArgumentException>(error.InnerException);
            Assert.Equal(Path.Combine(fixture.ProjectPath, "game"), Find<TextBox>(window, "FileBrowserPath").Text);
            foreach (var (path, expected) in bytes) Assert.Equal(expected, File.ReadAllBytes(path));
        }
        finally { await Close(window); }
    });

    [Fact]
    public Task ViewPreference_SaveReloadAndSwitchPreserveFolderSelection_AndOpenNestedFile() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string script = fixture.Write("chapters/scene.krl", "function scene() end"); var window = Create(fixture);
        try
        {
            await LoadPreferences(window); await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", Entries(window).Single(e => e.Name == "chapters"));
            var file = Assert.Single(Entries(window)); Find<ListBox>(window, "FileTiles").SelectedItem = file;
            Assert.Same(file, Find<DataGrid>(window, "FilesDataGrid").SelectedItem);
            Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs()); Find<ComboBox>(window, "ProjectBrowserViewInput").SelectedValue = "table";
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync"); Invoke(window, "ShowPanel", Find<Grid>(window, "ProjectPanel"));
            Assert.Equal("table", (await Store(fixture).LoadAsync()).ProjectBrowserView);
            var reopened = Create(fixture);
            try { await LoadPreferences(reopened); Assert.Equal("table", Find<ComboBox>(reopened, "ProjectBrowserViewInput").SelectedValue); Assert.Equal(Visibility.Visible, Find<DataGrid>(reopened, "FilesDataGrid").Visibility); }
            finally { await Close(reopened); }
            Assert.Equal(Visibility.Visible, Find<DataGrid>(window, "FilesDataGrid").Visibility); Assert.Equal(Visibility.Collapsed, Find<ListBox>(window, "FileTiles").Visibility);
            Assert.Equal(Path.Combine(fixture.ProjectPath, "chapters"), Find<TextBox>(window, "FileBrowserPath").Text); Assert.Same(file, Find<DataGrid>(window, "FilesDataGrid").SelectedItem);
            Layout(window, 1100); FileBrowserDesignTests.Capture((FrameworkElement)window.Content, "project-table-1100.png", new(1100, 700));
            Invoke(window, "BtnSettings_Click", window, new RoutedEventArgs()); Layout(window, 1100);
            FileBrowserDesignTests.Capture((FrameworkElement)window.Content, "project-browser-settings-1100.png", new(1100, 700));
            Find<ComboBox>(window, "ProjectBrowserViewInput").SelectedValue = "tiles"; await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            Assert.Equal("tiles", (await Store(fixture).LoadAsync()).ProjectBrowserView);
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", file);
            Assert.Equal(script, Find<ScriptEditor>(window, "SceneEditor").Document!.FilePath);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "FileBrowserPanel").Visibility);
            await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", Find<ScriptEditor>(window, "SceneEditor"));
            Assert.Equal(Visibility.Visible, Find<ListBox>(window, "FileTiles").Visibility);
            Assert.Equal(Path.Combine(fixture.ProjectPath, "chapters"), Find<TextBox>(window, "FileBrowserPath").Text);
        }
        finally { await Close(window); }
        var restored = Create(fixture);
        try { await LoadPreferences(restored); Assert.Equal("tiles", Find<ComboBox>(restored, "ProjectBrowserViewInput").SelectedValue); }
        finally { await Close(restored); }
    });

    [Fact]
    public Task RefreshKeepsFolderAndSearch_DeletedFolderFallsBackToParent_AndNewProjectStartsAtRoot() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); using var other = new TempProject(); fixture.Write("chapters/act1/scene.krl", "function scene() end");
        var window = Create(fixture);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); Invoke(window, "NavigateBrowserDirectory", Path.Combine("chapters", "act1"), null!, true);
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            Assert.Equal(Path.Combine(fixture.ProjectPath, "chapters", "act1"), Find<TextBox>(window, "FileBrowserPath").Text);
            Find<TextBox>(window, "FileSearch").Text = "ACT1/SCENE"; await Idle(window);
            Assert.Equal("scene.krl", Assert.Single(Entries(window)).Name);
            Find<TextBox>(window, "FileSearch").Clear(); await Idle(window);
            File.Delete(Path.Combine(fixture.ProjectPath, "chapters", "act1", "scene.krl")); Directory.Delete(Path.Combine(fixture.ProjectPath, "chapters", "act1"));
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            Assert.Equal(Path.Combine(fixture.ProjectPath, "chapters"), Find<TextBox>(window, "FileBrowserPath").Text); Assert.Empty(Entries(window));
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", other.ProjectPath, false);
            Assert.Equal(other.ProjectPath, Find<TextBox>(window, "FileBrowserPath").Text); Assert.Equal(Visibility.Collapsed, Find<Button>(window, "FileBrowserBackButton").Visibility);
        }
        finally { await Close(window); }
    });

    [Theory]
    [InlineData("{}", "tiles")]
    [InlineData("{\"ProjectBrowserView\":null}", "tiles")]
    [InlineData("{\"ProjectBrowserView\":\"unknown\"}", "tiles")]
    [InlineData("{\"ProjectBrowserView\":\"table\"}", "table")]
    public async Task ProfileDefaultsToTilesAndPreservesTableChoice(string json, string expected)
    {
        using var fixture = new TempProject(); var store = Store(fixture); Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!); File.WriteAllText(store.SettingsPath, json);
        var settings = await store.LoadAsync(); Assert.Equal(expected, settings.ProjectBrowserView); await store.SaveAsync(settings); Assert.Equal(expected, (await store.LoadAsync()).ProjectBrowserView);
    }

    [Fact]
    public Task TilesRetainFileActionsAndUseClickedFile_AndMissingFolderShowsRecoverableMessage() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("extra.krl", "function extra() end");
        string empty = Path.Combine(fixture.ProjectPath, "empty"); Directory.CreateDirectory(empty); var window = Create(fixture);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); Layout(window, 1100);
            var list = Find<ListBox>(window, "FileTiles"); var file = Entries(window).Single(e => e.Name == "extra.krl");
            var tile = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(file));
            var label = FileBrowserDesignTests.Descendants(tile).OfType<TextBlock>().Single(t => t.Text == file.Name);
            Assert.Same(file.File, MainWindow.FileAtDoubleClick(list, label));
            list.SelectedItem = file; var menu = list.ContextMenu; menu.PlacementTarget = list;
            Assert.Equal(new[] { "Редактировать", "Переименовать", "Дублировать", "Переместить", "Удалить" }, menu.Items.Cast<MenuItem>().Select(item => item.Header));
            menu.Items.Cast<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (int i = 0; i < 200 && Find<ScriptEditor>(window, "SceneEditor").Document == null; i++) { await Idle(window); await Task.Delay(5); }
            Assert.Equal(file.File!.FilePath, Find<ScriptEditor>(window, "SceneEditor").Document!.FilePath);
            await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", Find<ScriptEditor>(window, "SceneEditor"));
            var folder = Entries(window).Single(e => e.Name == "empty"); Directory.Delete(empty);
            await WorkspaceTabsTests.Invoke(window, "OpenBrowserEntryAsync", folder);
            Assert.Contains("Не удалось открыть папку", Find<TextBlock>(window, "OperationStatus").Text);
            Assert.Equal(fixture.ProjectPath, Find<TextBox>(window, "FileBrowserPath").Text); Assert.Equal(Visibility.Visible, Find<Grid>(window, "FileBrowserPanel").Visibility);
        }
        finally { await Close(window); }
    });

    [Theory]
    [InlineData("tiles", false)]
    [InlineData("tiles", true)]
    [InlineData("table", false)]
    [InlineData("table", true)]
    public Task SaveAndDeferredTreeRefreshKeepActiveEditorAndNeighbourDraft(string mode, bool graph) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        string activePath = fixture.Write("chapters/act1/active." + (graph ? "knegraph" : "krl"), graph ? VisualScript.Create().Serialize() : "function active() end");
        string neighbourPath = fixture.Write("neighbour.krl", "function neighbour() end"); var window = Create(fixture, mode);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            Invoke(window, "NavigateBrowserDirectory", Path.Combine("chapters", "act1"), null!, true); Layout(window, 1100);
            await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", neighbourPath);
            var neighbour = Find<ScriptEditor>(window, "SceneEditor"); WorkspaceTabsTests.Find<KrlCodeEditor>(neighbour, "CodeEditor").Text += " -- neighbour draft";
            await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", activePath);
            var tabs = Find<TabControl>(window, "DocumentTabs"); var selected = Assert.IsType<TabItem>(tabs.SelectedItem); var active = Assert.IsType<ScriptEditor>(selected.Tag);
            if (graph) active.AddBlock(ScriptNodeKind.Dialogue); else WorkspaceTabsTests.Find<KrlCodeEditor>(active, "CodeEditor").Text += " -- saved edit";
            var document = active.Document; var before = window.Project; var browser = Find<Grid>(window, "FileBrowserPanel"); int browserTransitions = 0;
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Grid));
            EventHandler shown = (_, _) => { if (browser.Visibility == Visibility.Visible) browserTransitions++; };
            descriptor.AddValueChanged(browser, shown);
            try
            {
                WorkspaceTabsTests.Find<Button>(active, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpUntil(window, () => active.IsEnabled && !active.HasUnsavedChanges && !ReferenceEquals(before, window.Project));
                Assert.Same(selected, tabs.SelectedItem); Assert.Same(document, active.Document); Assert.Equal(2, tabs.Items.Count);
                Assert.Equal(Visibility.Visible, active.Visibility); Assert.Equal(Visibility.Visible, Find<Grid>(window, "EditorWorkspace").Visibility);
                Assert.Equal(Visibility.Collapsed, browser.Visibility); Assert.Equal(0, browserTransitions);
                FileBrowserDesignTests.Capture((FrameworkElement)window.Content, "saved-editor-" + mode + "-" + (graph ? "graph" : "code") + ".png", new(1100, 700));
                if (graph) Assert.Equal(document!.Graph!.Nodes.Count, VisualScript.Parse(File.ReadAllText(activePath)).Nodes.Count);
                else Assert.EndsWith("-- saved edit", File.ReadAllText(activePath));
                Assert.True(neighbour.HasUnsavedChanges); Assert.EndsWith("-- neighbour draft", neighbour.Document!.Code); Assert.Equal("function neighbour() end", File.ReadAllText(neighbourPath));
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); Layout(window, 1100); await Idle(window); Layout(window, 1100);
                Assert.Same(selected, tabs.SelectedItem); Assert.Equal(Visibility.Visible, active.Visibility); Assert.Equal(Visibility.Collapsed, browser.Visibility); Assert.Equal(0, browserTransitions);
            }
            finally { descriptor.RemoveValueChanged(browser, shown); }
            var tree = Find<TreeView>(window, "ProjectFilesTree"); var game = tree.Items.Cast<ProjectTreeNode>().Single(n => n.Name == "game");
            Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(game)).IsSelected = true;
            Assert.Equal(Visibility.Visible, browser.Visibility); Assert.Equal(Path.Combine(fixture.ProjectPath, "game"), Find<TextBox>(window, "FileBrowserPath").Text);
        }
        finally { await Close(window); }
    });

    [Theory]
    [InlineData("kne")]
    [InlineData("dark")]
    [InlineData("rose")]
    public Task TileSurfaceKeepsThemeDuringBusyRefreshAndFinalTabClose(string theme) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = Create(fixture);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); EditorTheme.Apply(window, theme); Layout(window, 1100);
            var tiles = Find<ListBox>(window, "FileTiles");
            void CheckSurface()
            {
                Layout(window, 1100);
                var image = new RenderTargetBitmap((int)Math.Ceiling(tiles.ActualWidth), (int)Math.Ceiling(tiles.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(tiles);
                byte[] pixel = new byte[4]; image.CopyPixels(new Int32Rect(10, image.PixelHeight - 10, 1, 1), pixel, 4, 0);
                Assert.Equal(WelcomeOptionsTests.BrushColor(window.FindResource("Editor.Surface")), Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]));
            }
            CheckSurface(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = WorkspaceTabsTests.Invoke(window, "RunOperationAsync", (Func<Task>)(() => { entered.SetResult(); return release.Task; }), "Проверка обновления…");
            await entered.Task;
            try { Assert.False(tiles.IsEnabled); CheckSurface(); FileBrowserDesignTests.Capture((FrameworkElement)window.Content, "tiles-disabled-" + theme + ".png", new(1100, 700)); }
            finally { release.TrySetResult(); await operation; }
            Assert.True(tiles.IsEnabled); CheckSurface();
            await WorkspaceTabsTests.Invoke(window, "RefreshChangedProjectAsync"); EditorTheme.Apply(window, theme); CheckSurface();
            await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", window.Project!.MainScenePath); var editor = Find<ScriptEditor>(window, "SceneEditor");
            await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", editor); CheckSurface();
            Assert.Equal(Visibility.Visible, tiles.Visibility);
        }
        finally { await Close(window); }
    });

    private static async Task PumpUntil(MainWindow window, Func<bool> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            Layout(window, 1100); await Idle(window);
            if (condition()) { Layout(window, 1100); await Idle(window); return; }
            await Task.Delay(10);
        }
        throw new TimeoutException("Workspace did not finish the save and watcher refresh.");
    }

    private static MainWindow Create(TempProject fixture, string mode = "tiles")
    {
        var window = new MainWindow(); typeof(MainWindow).GetField("settingsStore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Store(fixture));
        typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new UserSettings { ProjectBrowserView = mode }); return window;
    }
    private static UserSettingsStore Store(TempProject fixture) => new(Path.Combine(fixture.Root, "profile", "settings.json"));
    private static T Find<T>(MainWindow window, string name) where T : class => WorkspaceTabsTests.Find<T>(window, name);
    private static ProjectBrowserEntry[] Entries(MainWindow window) => Find<ListBox>(window, "FileTiles").Items.Cast<ProjectBrowserEntry>().ToArray();
    private static object? Invoke(object owner, string method, params object[] args) => owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args);
    private static void Layout(MainWindow window, int width) => FileBrowserDesignTests.Layout((FrameworkElement)window.Content, new(width, 700));
    private static async Task Idle(MainWindow window) { for (int i = 0; i < 10; i++) await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static async Task LoadPreferences(MainWindow window)
    {
        Invoke(window, "Window_Loaded", window, new RoutedEventArgs());
        for (int i = 0; i < 200; i++) { await Idle(window); if (!(bool)typeof(MainWindow).GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!) return; await Task.Delay(10); }
        throw new TimeoutException("Preferences did not load.");
    }
    private static async Task Close(MainWindow window)
    {
        foreach (var editor in Find<TabControl>(window, "DocumentTabs").Items.OfType<TabItem>().Select(t => t.Tag).OfType<ScriptEditor>().ToArray())
        { editor.ConfirmSave = _ => MessageBoxResult.No; await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", editor); }
        window.Close();
    }
}
