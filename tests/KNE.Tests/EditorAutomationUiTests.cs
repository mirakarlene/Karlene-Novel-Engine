using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class EditorAutomationUiTests
{
    [Fact]
    public Task HistoryDialogListsSelectedFilesAndExecutesActualRestore_AndCancelKeepsCurrentContent() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string file = fixture.Write("one.krl", "old"); var store = new ProjectSnapshots(Path.Combine(fixture.Root, "history"));
        var snapshot = (await store.CreateAsync(fixture.ProjectPath, "history", 5, [file]))!; File.WriteAllText(file, "current");
        bool confirm = false;
        var window = new VersionHistoryWindow("Project", [snapshot], async selected => { if (!confirm) return false; await store.RestoreAsync(fixture.ProjectPath, selected); return true; });
        try
        {
            Assert.False(await window.RestoreSelectedAsync()); window.Versions.SelectedIndex = 0;
            Assert.True(window.Restore.IsEnabled); Assert.Equal(new[] { "one.krl" }, window.Files.Items.Cast<string>());
            Assert.False(await window.RestoreSelectedAsync()); Assert.Equal("current", File.ReadAllText(file));
            confirm = true; Assert.True(await window.RestoreSelectedAsync()); Assert.Equal("old", File.ReadAllText(file));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SavedFilesTriggerOneFullBackupPerSaveBatch_ManualBackupWorksWhenAutomaticBackupsAreDisabled() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string file = fixture.Write("one.krl", "function one() end"); fixture.Write("game/resource.txt", "resource"); string storage = Path.Combine(fixture.Root, "backups");
        var window = Create(fixture, new() { BackupPath = storage, BackupMinutes = -1, BackupCopies = 3, AutosaveMinutes = 0, Toasts = false });
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", file);
            for (int i = 0; i < 4; i++)
            {
                var editor = (ScriptEditor)((TabItem)Find<TabControl>(window, "DocumentTabs").SelectedItem).Tag; Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function one() -- " + i + "\nend";
                await WorkspaceTabsTests.Invoke(window, "SaveAllEditorsAsync"); await WorkspaceTabsTests.Invoke(window, "AutomationTickAsync");
                Assert.Equal(Math.Min(i + 1, 3), new ProjectSnapshots(storage).List(fixture.ProjectPath, "backup").Count);
            }
            var latest = new ProjectSnapshots(storage).List(fixture.ProjectPath, "backup")[0]; Assert.Contains(latest.Manifest.Files, entry => entry.Path == "game/resource.txt"); Assert.Contains(latest.Manifest.Files, entry => entry.Path == "project.json");
            Settings(window).Editor.BackupMinutes = 0; Call(window, "LoadGeneralSettings");
            await WorkspaceTabsTests.Invoke(window, "ManualBackupAsync"); Assert.Equal(3, new ProjectSnapshots(storage).List(fixture.ProjectPath, "backup").Count);
            Assert.NotEqual(latest.Archive, new ProjectSnapshots(storage).List(fixture.ProjectPath, "backup")[0].Archive);
            Assert.Contains("Резервная копия:", Find<GeneralSettingsEditor>(window, "GeneralSettingsPage").Status.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task AutosaveWritesEveryOpenTab_PreservesSelectionAndUnopenedFiles_AndHistoryCanBeDisabled(bool history)
    {
        using var fixture = new TempProject(); string codePath = fixture.Write("first.krl", "function first() end"), untouched = fixture.Write("closed.krl", "function closed() end");
        var graph = VisualScript.Create(); graph.FunctionName = "other"; string graphPath = fixture.Write("other.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Create(fixture, new() { KeepHistory = history, BackupMinutes = 0 });
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", codePath); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", graphPath);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var first = (ScriptEditor)((TabItem)tabs.Items[0]).Tag; var second = (ScriptEditor)((TabItem)tabs.Items[1]).Tag;
                Find<KrlCodeEditor>(first, "CodeEditor").Text = "function first() -- changed\nend"; second.SelectGraphBlocks(["dialogue"]); Find<TextBox>(second, "SceneTextInput").Text = "Changed visual dialogue"; var selected = tabs.SelectedItem;
                await window.AutosaveOpenFilesAsync();
                Assert.Contains("changed", File.ReadAllText(codePath)); Assert.Equal("Changed visual dialogue", VisualScript.Parse(File.ReadAllText(graphPath)).Nodes.Single(node => node.Id == "dialogue").Text);
                Assert.Equal("function closed() end", File.ReadAllText(untouched)); Assert.Same(selected, tabs.SelectedItem); Assert.Equal(2, tabs.Items.Count); Assert.False(first.HasUnsavedChanges); Assert.False(second.HasUnsavedChanges); Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "FileBrowserPanel").Visibility);
                var store = new ProjectSnapshots(Path.Combine(fixture.Root, "autosave")); var versions = store.List(fixture.ProjectPath, "history");
                Assert.Equal(history ? 2 : 0, versions.Count);
                if (history) Assert.Equal(new[] { "first.krl", "other.knegraph" }, versions[0].Manifest.Files.Select(file => file.Path));
                await window.AutosaveOpenFilesAsync(); Assert.Equal(history ? 2 : 0, store.List(fixture.ProjectPath, "history").Count);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task AutosaveConflictKeepsExternalFileAndDraft_AndEarlierSuccessfulTabsRemainSaved()
    {
        using var fixture = new TempProject(); string firstPath = fixture.Write("a.krl", "function a() end"), secondPath = fixture.Write("b.krl", "function b() end");
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Create(fixture, new() { BackupMinutes = 0 });
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", firstPath); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", secondPath);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var first = (ScriptEditor)((TabItem)tabs.Items[0]).Tag; var second = (ScriptEditor)((TabItem)tabs.Items[1]).Tag;
                Find<KrlCodeEditor>(first, "CodeEditor").Text = "function a() -- saved\nend"; Find<KrlCodeEditor>(second, "CodeEditor").Text = "function b() -- draft\nend"; File.WriteAllText(secondPath, "function b() -- external\nend");
                await Assert.ThrowsAsync<IOException>(window.AutosaveOpenFilesAsync);
                Assert.Contains("saved", File.ReadAllText(firstPath)); Assert.Contains("external", File.ReadAllText(secondPath)); Assert.Contains("draft", second.Document!.Code); Assert.True(second.HasUnsavedChanges); Assert.Equal(2, tabs.Items.Count);
                Assert.Equal(2, new ProjectSnapshots(Path.Combine(fixture.Root, "autosave")).List(fixture.ProjectPath, "history").Count);
                second.ConfirmSave = _ => MessageBoxResult.No; second.Clear();
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("home", false)] [InlineData("settings", false)] [InlineData("last", false)] [InlineData("project", false)] [InlineData("new_scene", false)] [InlineData("new_scene", true)]
    public async Task StartupModesOpenRequestedSurface_AndNewSceneUsesProjectFormatWithoutReplacingMain(string action, bool visual)
    {
        using var fixture = new TempProject(); string main = File.ReadAllText(Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
        var project = await fixture.LoadAsync(); project.Configuration.AuthoringMode = visual ? ProjectAuthoringMode.Visual : ProjectAuthoringMode.Code; fixture.Write("project.json", JsonSerializer.Serialize(project.Configuration, ProjectConfiguration.JsonOptions));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Create(fixture, new() { Startup = action, StartupProject = fixture.ProjectPath, BackupMinutes = 0 }); Settings(window).RecentProjects = [fixture.ProjectPath];
            try
            {
                await window.ApplyStartupBehaviorAsync();
                if (action is "home" or "settings") { Assert.Null(window.Project); Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, action == "home" ? "WelcomePanel" : "SettingsPanel").Visibility); }
                else Assert.Equal(fixture.ProjectPath, window.Project!.Path);
                var tabs = Find<TabControl>(window, "DocumentTabs"); Assert.Equal(action == "new_scene" ? 1 : 0, tabs.Items.Count);
                if (action == "new_scene")
                {
                    var document = ((ScriptEditor)((TabItem)tabs.Items[0]).Tag).Document!; Assert.Equal(visual, document.IsVisual); Assert.EndsWith(visual ? ".knegraph" : ".krl", document.FilePath);
                    if (visual) Assert.Equal(2, document.Graph!.Nodes.Count);
                    Assert.Single(GameScripts.Load(window.Project!).Functions, function => function.Name == "main");
                }
                Assert.Equal(main, File.ReadAllText(Path.Combine(fixture.ProjectPath, "game/scenes/main.krl")));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("last")] [InlineData("project")] [InlineData("new_scene")]
    public async Task MissingStartupProjectFallsBackToHomeWithWarning(string action)
    {
        using var fixture = new TempProject(); string missing = Path.Combine(fixture.Root, "missing");
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Create(fixture, new() { Startup = action, StartupProject = missing }); Settings(window).RecentProjects = [missing]; string? warning = null; window.MissingProjectWarning = text => warning = text;
            try { await window.ApplyStartupBehaviorAsync(); Assert.Null(window.Project); Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "WelcomePanel").Visibility); Assert.Contains("перемещён или удалён", warning); }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1500, 950)] [InlineData(1100, 700)]
    public Task GeneralPageAndDialogsKeepFullSizeControls_PagingKeepsDrafts_AndStrictBreakDisablesWorkspace(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = Create(fixture, new() { BackupMinutes = 0, ShowWorkTime = true, BreakReminders = true, StrictBreak = true, BreakDuration = 10 });
        try
        {
            Call(window, "BtnSettings_Click", window, new RoutedEventArgs()); var root = (FrameworkElement)window.Content; Layout(root, width - 20, height - 40);
            var general = Find<GeneralSettingsEditor>(window, "GeneralSettingsPage"); general.AutosaveInterval.SelectedValue = 10; Assert.Equal(5, general.BackupCopies.SelectedValue); Assert.Equal(25, general.HistoryLimit.SelectedValue);
            Assert.Equal(0, general.ScrollableHeight); Assert.Equal(1, general.LayoutTransform.Value.M11); Assert.True(general.BackupPath.ActualWidth > 150); Capture(root, "general-top-" + width);
            general.ScrollToEnd(); Layout(root, width - 20, height - 40); Capture(root, "general-bottom-" + width);
            Click(Find<Button>(window, "NextSettingsPageButton")); Layout(root, width - 20, height - 40); Assert.Equal("2 / 4 · Поведение и уведомления", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "BehaviorSettingsPage").Visibility); Capture(root, "behavior-" + width);
            Click(Find<Button>(window, "NextSettingsPageButton")); Layout(root, width - 20, height - 40); Assert.Equal("3 / 4 · Проекты и Рендер", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "ProjectsRenderSettingsPage").Visibility); Capture(root, "projects-render-" + width);
            Click(Find<Button>(window, "NextSettingsPageButton")); Layout(root, width - 20, height - 40); Assert.Equal("4 / 4 · Плагины", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.False(Find<Button>(window, "NextSettingsPageButton").IsEnabled); Capture(root, "plugins-" + width);
            Click(Find<Button>(window, "PreviousSettingsPageButton")); Click(Find<Button>(window, "PreviousSettingsPageButton")); Click(Find<Button>(window, "PreviousSettingsPageButton")); Assert.Equal(10, general.AutosaveInterval.SelectedValue);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync"); Assert.Equal(10, Settings(window).Editor.AutosaveMinutes);
            var keyWindow = new HotkeySettingsWindow([]); EditorTheme.Apply(keyWindow, "kne"); Layout((FrameworkElement)keyWindow.Content, width == 1100 ? 600 : 830, width == 1100 ? 380 : 560);
            keyWindow.Actions.SelectedIndex = 0; keyWindow.Capture(Key.S, ModifierKeys.Control | ModifierKeys.Alt); keyWindow.Confirm(); Assert.Equal("Ctrl+Alt+S", keyWindow.Profile["file.save"]);
            keyWindow.Search.Text = "Запустить"; Assert.Single(keyWindow.Actions.Items.Cast<object>()); keyWindow.Actions.SelectedIndex = 0; keyWindow.Capture(Key.F5, ModifierKeys.None); keyWindow.Confirm(); Assert.Equal("F5", keyWindow.Profile["debug.run"]);
            Assert.Throws<InvalidDataException>(() => keyWindow.Assign("debug.run", "Ctrl+C")); Assert.Equal("F5", keyWindow.Profile["debug.run"]); Assert.Throws<InvalidDataException>(() => keyWindow.Import("broken")); Assert.Equal("Ctrl+Alt+S", keyWindow.Profile["file.save"]);
            Layout((FrameworkElement)keyWindow.Content, width == 1100 ? 600 : 830, width == 1100 ? 380 : 560); Capture((FrameworkElement)keyWindow.Content, "hotkeys-" + width); keyWindow.Close();
            var history = new VersionHistoryWindow("Project", [], _ => Task.FromResult(false)); EditorTheme.Apply(history, "kne"); Layout((FrameworkElement)history.Content, width == 1100 ? 600 : 830, width == 1100 ? 360 : 510); Assert.False(history.Restore.IsEnabled); Capture((FrameworkElement)history.Content, "history-" + width); history.Close();
            Assert.True(window.SessionClock.Advance(TimeSpan.FromHours(1), Settings(window).Editor)); Call(window, "UpdateBreakUi"); Assert.False(Find<DockPanel>(window, "EditorMainSurface").IsEnabled); Assert.Equal(Visibility.Visible, Find<Border>(window, "BreakOverlay").Visibility); Assert.Contains("10:00", Find<TextBlock>(window, "BreakCountdown").Text); Assert.False(await window.ExecuteWorkspaceActionAsync("general.settings"));
            window.SessionClock.Advance(TimeSpan.FromMinutes(10), Settings(window).Editor); Call(window, "UpdateBreakUi"); Assert.True(Find<DockPanel>(window, "EditorMainSurface").IsEnabled); general.ResetWorkRequested!(); Assert.Equal(TimeSpan.Zero, window.SessionClock.WorkTime);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task ReboundEditorAndWorkspaceActionsExecuteSaveAndGraphCommands()
    {
        using var fixture = new TempProject(); string code = fixture.Write("changed.krl", "function changed() end"); var graph = VisualScript.Create(); graph.FunctionName = "graph"; string path = fixture.Write("graph.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = Create(fixture, new() { BackupMinutes = 0 });
            try
            {
                Settings(window).Hotkeys = new() { ["file.save"] = "F6", ["blocks.duplicate"] = "F7" }; Call(window, "ApplyEditorPreferences");
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", code);
                var editor = (ScriptEditor)((TabItem)Find<TabControl>(window, "DocumentTabs").SelectedItem).Tag; Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function changed() -- saved\nend";
                Assert.Equal("F6", EditorHotkeys.GetProfile(editor)!["file.save"]);
                var saveKey = new KeyEventArgs(Keyboard.PrimaryDevice, new InputSource(), 0, Key.F6) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; Find<KrlCodeEditor>(editor, "CodeEditor").RaiseEvent(saveKey); Assert.True(saveKey.Handled);
                for (int i = 0; i < 500 && editor.IsSaving; i++) await Task.Delay(10);
                Assert.False(editor.IsSaving); Assert.Contains("saved", File.ReadAllText(code)); Assert.True(await window.ExecuteWorkspaceActionAsync("file.save_all"));
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", path); editor = (ScriptEditor)((TabItem)Find<TabControl>(window, "DocumentTabs").SelectedItem).Tag; editor.SelectGraphBlocks(["dialogue"]); int before = editor.Document!.Graph!.Nodes.Count;
                var duplicateKey = new KeyEventArgs(Keyboard.PrimaryDevice, new InputSource(), 0, Key.F7) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; Find<Canvas>(editor, "GraphCanvas").RaiseEvent(duplicateKey); Assert.True(duplicateKey.Handled); Assert.Equal(before + 1, editor.Document.Graph.Nodes.Count);
                Assert.True(await editor.ExecuteEditorActionAsync("edit.undo", Find<Canvas>(editor, "GraphCanvas"))); Assert.Equal(before, editor.Document.Graph.Nodes.Count);
                Assert.True(await editor.ExecuteEditorActionAsync("file.save", editor)); Assert.False(editor.HasUnsavedChanges);
            }
            finally { foreach (var item in Find<TabControl>(window, "DocumentTabs").Items.OfType<TabItem>()) if (item.Tag is ScriptEditor editor) { editor.ConfirmSave = _ => MessageBoxResult.No; editor.Clear(); } window.Close(); }
        });
    }

    private static MainWindow Create(TempProject fixture, EditorPreferences preferences)
    {
        var window = new MainWindow(); typeof(MainWindow).GetField("settingsStore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new UserSettingsStore(Path.Combine(fixture.Root, "profile.json")));
        typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new UserSettings { Editor = preferences }); Call(window, "LoadGeneralSettings"); Call(window, "ApplyEditorPreferences"); return window;
    }
    private static UserSettings Settings(MainWindow window) => (UserSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void Call(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static T Find<T>(FrameworkElement root, string name) where T : class => WorkspaceTabsTests.Find<T>(root, name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private sealed class InputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static void Layout(FrameworkElement root, double width, double height) { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Capture(FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("KNE_GENERAL_RENDER") is not { } directory) return; Directory.CreateDirectory(directory);
        int width = (int)Math.Ceiling(root.RenderSize.Width + root.Margin.Left + root.Margin.Right), height = (int)Math.Ceiling(root.RenderSize.Height + root.Margin.Top + root.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var context = background.RenderOpen()) context.DrawRectangle(root.TryFindResource("Editor.Background") as Brush, null, new Rect(0, 0, width, height)); bitmap.Render(background);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
