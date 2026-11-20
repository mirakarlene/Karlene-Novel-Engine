using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class MetaPresentationSettingsTests
{
    [Fact]
    public Task ProjectConsoleAndStoragePagesFitTheirMinimumSizeWithExpandedConditionalFieldsAndPinnedActions() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            window.ResolutionInput.SelectedValue = "custom"; window.AutosaveInput.SelectedValue = "actions";
            EditorTheme.Apply(window, "kne"); window.Width = 740; window.Height = 680; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = window.Top = -10000; window.ShowInTaskbar = false; window.ShowActivated = false; window.Show(); window.UpdateLayout();
            var root = (FrameworkElement)window.Content; var viewport = Logical(root).OfType<ContentControl>().Single(control => control.GetType() == typeof(ContentControl));
            var save = Logical(root).OfType<Button>().Single(button => Equals(button.Content, "Сохранить")); var baseline = Bounds(save, root);
            for (int page = 0; page <= 1; page++)
            {
                window.ShowPage(page); if (page == 1) window.DangerZone.IsExpanded = true; window.UpdateLayout();
                Assert.Empty(Logical(root).OfType<ScrollViewer>()); Assert.Equal(baseline, Bounds(save, root));
                FrameworkElement[] controls = page == 0
                    ? [window.ConsoleModeInput, window.ConsoleHotkeyInput, window.GraphicsInput.ApiInput, window.CustomWidthInput, window.CustomHeightInput, window.PreloadInput]
                    : [window.PersistentLocationInput, window.DeleteCrashReportsButton, window.AutosaveActionsInput, window.DeleteProjectButton];
                foreach (var control in controls) { Fits(control, viewport); Assert.True(control.IsEnabled && control.IsHitTestVisible); }
                for (int first = 0; first < controls.Length; first++) for (int second = first + 1; second < controls.Length; second++)
                    Assert.False(Rect.Intersect(Bounds(controls[first], viewport), Bounds(controls[second], viewport)) is { Width: > .5, Height: > .5 }, "Overlapping project parameter fields.");
                foreach (var label in Logical(viewport).OfType<TextBlock>().Where(label => label.IsVisible))
                {
                    Fits(label, viewport); Assert.True(label.ActualHeight + .1 >= label.DesiredSize.Height - label.Margin.Top - label.Margin.Bottom, "Truncated label: " + label.Text);
                    Assert.True(label.ActualWidth + .1 >= label.DesiredSize.Width - label.Margin.Left - label.Margin.Right, "Truncated label width: " + label.Text);
                }
            }
            Assert.Equal(new[] { "embedded", "window", "terminal" }, window.ConsoleModeInput.Items.Cast<GraphicsOption<string>>().Select(option => option.Value));
            Assert.Equal(new[] { "local", "appdata" }, window.PersistentLocationInput.Items.Cast<GraphicsOption<string>>().Select(option => option.Value));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("embedded", "F3")] [InlineData("window", "Ctrl+Shift+D")] [InlineData("terminal", "Alt+F3")]
    public Task ConsoleModeAndShortcutAreSavedTogetherAndCancellationPreservesTheCommittedPair(string mode, string hotkey) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var window = new ProjectParametersWindow(project);
        try
        {
            Assert.Equal("embedded", window.ConsoleModeInput.SelectedValue); Assert.Equal("F3", window.ConsoleHotkeyInput.Text);
            window.ConsoleModeInput.SelectedValue = mode; window.ConsoleHotkeyInput.Text = hotkey; window.PersistentLocationInput.SelectedValue = "local";
            await window.SaveAsync(); var saved = PerformanceSettingsStore.LoadProject(project.Path);
            Assert.Equal(mode, saved.ConsoleMode); Assert.Equal(hotkey, saved.ConsoleHotkey); Assert.Equal("local", saved.PersistentLocation);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json")));
            Assert.Equal(mode, json.RootElement.GetProperty("console_mode").GetString()); Assert.Equal(hotkey, json.RootElement.GetProperty("console_hotkey").GetString());
        }
        finally { window.Close(); }
        var canceled = new ProjectParametersWindow(project);
        try { canceled.ConsoleModeInput.SelectedValue = mode == "embedded" ? "window" : "embedded"; canceled.ConsoleHotkeyInput.Text = "Ctrl+F9"; canceled.Close(); }
        finally { canceled.Close(); }
        Assert.Equal(mode, PerformanceSettingsStore.LoadProject(project.Path).ConsoleMode); Assert.Equal(hotkey, PerformanceSettingsStore.LoadProject(project.Path).ConsoleHotkey);
    });

    [Fact]
    public Task LegacyPersistentChoiceDisplaysAsLocalAndAnInvalidConsoleShortcutCannotOverwriteSettings() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { PersistentLocation = "saves" });
        var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            Assert.Equal("local", window.PersistentLocationInput.SelectedValue);
            string path = Path.Combine(fixture.ProjectPath, "engine", "performance.json"); byte[] before = File.ReadAllBytes(path);
            window.ConsoleModeInput.SelectedValue = "terminal"; window.ConsoleHotkeyInput.Text = "Alt+F4";
            await Assert.ThrowsAsync<InvalidDataException>(() => window.SaveAsync()); Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ConsoleShortcutsUseTheSameExactKeyAndModifierRulesForWpfAndNativeTerminalInput()
    {
        Assert.True(GameConsoleShortcut.Matches(Key.F3, ModifierKeys.None, "F3")); Assert.False(GameConsoleShortcut.Matches(Key.F3, ModifierKeys.Control, "F3"));
        Assert.True(GameConsoleShortcut.Matches(Key.D, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+D"));
        Assert.False(GameConsoleShortcut.Matches(Key.D, ModifierKeys.Control, "Ctrl+Shift+D"));
        Assert.True(GameConsoleShortcut.Matches(new ConsoleKeyInfo('D', ConsoleKey.D, true, false, true), "Ctrl+Shift+D"));
        Assert.False(GameConsoleShortcut.Matches(new ConsoleKeyInfo('D', ConsoleKey.D, false, false, true), "Ctrl+Shift+D"));
        Assert.Equal("Ctrl+Shift+D", GameConsoleShortcut.Normalize(" Ctrl + Shift + D "));
    }

    [Theory]
    [InlineData("same")] [InlineData("absent")] [InlineData("copy")] [InlineData("existing")]
    public async Task LegacyMigrationCopiesOnlyIntoAnAbsentDestinationAndPreservesSourceAndExistingData(string scenario)
    {
        using var fixture = new TempProject(); string source = Path.Combine(fixture.Root, "source"), destination = scenario == "same" ? source : Path.Combine(fixture.Root, "destination");
        if (scenario != "absent")
        {
            var store = new PersistentGameStore(source); store.Set("enabled", JsonSerializer.SerializeToElement(false)); store.Set("name", JsonSerializer.SerializeToElement("Аня")); store.RecordLaunch();
        }
        if (scenario == "existing") new PersistentGameStore(destination).Set("existing", JsonSerializer.SerializeToElement(42));
        string sourcePath = Path.Combine(source, "persistent"), targetPath = Path.Combine(destination, "persistent");
        byte[]? sourceBytes = File.Exists(sourcePath) ? File.ReadAllBytes(sourcePath) : null;
        byte[]? targetBytes = File.Exists(targetPath) ? File.ReadAllBytes(targetPath) : null;
        await PersistentGameStore.CopyIfMissingAsync(source, destination);
        if (scenario == "absent") { Assert.False(Directory.Exists(destination)); Assert.False(Directory.Exists(source)); return; }
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath)); Assert.Equal(targetBytes ?? sourceBytes, File.ReadAllBytes(targetPath));
        if (scenario == "existing") { Assert.Equal(42, new PersistentGameStore(destination).Get("existing").GetInt32()); Assert.False(new PersistentGameStore(destination).Contains("name")); }
        else { var loaded = new PersistentGameStore(destination); Assert.False(loaded.Get("enabled").GetBoolean()); Assert.Equal("Аня", loaded.Get("name").GetString()); Assert.Equal(1, loaded.LaunchCount); }
        Assert.Empty(Directory.EnumerateFiles(destination, "*.tmp"));
    }

    [Fact]
    public async Task CrashDeletionRemovesOnlyCanonicalTopLevelReportsOwnedByThisProject()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var paths = Reports(fixture, project);
        var before = paths.Kept.ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(1, await GameCrashReports.DeleteAsync(project)); Assert.False(File.Exists(paths.Owned));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(0, await GameCrashReports.DeleteAsync(project));
    }

    [Fact]
    public Task CrashReportButtonRequiresConfirmationAndDeletesReportsWithoutSavingUnrelatedDrafts() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var paths = Reports(fixture, project);
        var window = new ProjectParametersWindow(project); byte[] configuration = File.ReadAllBytes(Path.Combine(project.Path, "project.json")); int confirmations = 0;
        try
        {
            window.ShowPage(1); window.ConsoleHotkeyInput.Text = "Alt+F4";
            window.ConfirmDeleteCrashReports = () => { confirmations++; return false; };
            Click(window.DeleteCrashReportsButton); Assert.True(File.Exists(paths.Owned)); Assert.Equal(1, confirmations);
            window.ConfirmDeleteCrashReports = () => { confirmations++; return true; }; Click(window.DeleteCrashReportsButton);
            await Until(() => window.DeleteCrashReportsButton.IsEnabled && !File.Exists(paths.Owned));
            Assert.Equal(2, confirmations); Assert.Equal(configuration, File.ReadAllBytes(Path.Combine(project.Path, "project.json")));
            Assert.Equal("Alt+F4", window.ConsoleHotkeyInput.Text); Assert.All(paths.Kept, path => Assert.True(File.Exists(path)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task GridPreferenceDefaultsOffAndRoundTripsThroughAnExplicitSettingsFile()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.Root, "user-settings.json"); var store = new UserSettingsStore(path);
        Assert.False((await store.LoadAsync()).ShowGraphGrid); Assert.False(File.Exists(path));
        await store.SaveAsync(new() { ShowGraphGrid = true }); Assert.True((await new UserSettingsStore(path).LoadAsync()).ShowGraphGrid);
        await store.SaveAsync(new() { ShowGraphGrid = false }); Assert.False((await new UserSettingsStore(path).LoadAsync()).ShowGraphGrid);
    }

    [Fact]
    public Task GridTracksPanAndZoomWithoutInterceptingInputOrChangingAuthoredNodes() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = fixture.Write("grid.knegraph", VisualScript.Create().Serialize());
        var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
        var host = new Window { Content = editor, Width = 1280, Height = 800, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        EditorTheme.Apply(host, "kne");
        try
        {
            host.Show(); host.UpdateLayout(); string original = editor.Document!.Graph!.Serialize(); var grid = WorkspaceTabsTests.Find<Border>(editor, "GraphGrid");
            Assert.False(editor.ShowGraphGrid); Assert.False(grid.IsHitTestVisible); editor.ShowGraphGrid = true;
            editor.PanGraph(47, -19); editor.ChangeZoom(2); host.UpdateLayout();
            var scale = WorkspaceTabsTests.Find<ScaleTransform>(editor, "CanvasScale"); var pan = WorkspaceTabsTests.Find<TranslateTransform>(editor, "CanvasPan");
            var brush = Assert.IsType<DrawingBrush>(grid.Background); Assert.InRange(brush.Opacity, .01, .25); Assert.Equal(TileMode.Tile, brush.TileMode);
            double spacing = 32 * scale.ScaleX; Assert.Equal(spacing, brush.Viewport.Width, 6); Assert.Equal(spacing, brush.Viewport.Height, 6);
            Assert.Equal(pan.X % spacing, brush.Viewport.X, 6); Assert.Equal(pan.Y % spacing, brush.Viewport.Y, 6);
            var canvas = WorkspaceTabsTests.Find<Canvas>(editor, "GraphCanvas"); var card = canvas.Children.OfType<Border>().Single(card => Equals(card.Tag, "dialogue"));
            var point = card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight / 2), editor); var hit = editor.InputHitTest(point); Assert.NotNull(hit);
            editor.ShowGraphGrid = false; host.UpdateLayout(); Assert.Null(grid.Background); Assert.Same(hit, editor.InputHitTest(point));
            editor.PanGraph(11, 3); Assert.Null(grid.Background); editor.ShowGraphGrid = true; host.UpdateLayout(); Assert.False(grid.IsHitTestVisible);
            Assert.Equal(original, editor.Document.Graph.Serialize()); Assert.False(editor.Document.IsDirty);
        }
        finally { host.Close(); editor.Clear(); }
    });

    static (string Owned, string[] Kept) Reports(TempProject fixture, ProjectInfo project)
    {
        string directory = Path.Combine(project.Path, "saves", "diagnostics"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(project.Path, "saves", ".legacy-import-complete"), "fixture has no legacy profile");
        string Name(int id) => "crash-20261120-120000-" + id.ToString("x32") + ".json";
        var report = new GameErrorReport("project", "Project", project.Path, DateTimeOffset.UnixEpoch, "test", "test", "test", "TestException", "failure", null, null, "details", "test");
        string Write(string relative, GameErrorReport value) => fixture.Write("saves/diagnostics/" + relative, JsonSerializer.Serialize(value, ProjectConfiguration.JsonOptions));
        string owned = Write(Name(1), report), foreign = Write(Name(2), report with { ProjectPath = Path.Combine(fixture.Root, "another-project") });
        string broken = fixture.Write("saves/diagnostics/" + Name(3), "{broken"), unrelated = Write("notes.json", report), noncanonical = Write("crash-custom.json", report), nested = Write("nested/" + Name(4), report);
        string persistent = fixture.Write("saves/persistent", "preserve persistent"), slot = fixture.Write("saves/auto.json", "preserve slot");
        return (owned, [foreign, broken, unrelated, noncanonical, nested, persistent, slot]);
    }
    static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    static void Fits(FrameworkElement element, FrameworkElement viewport)
    {
        Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, "Hidden control: " + element.Name);
        void Check(FrameworkElement ancestor)
        {
            var bounds = Bounds(element, ancestor); Assert.True(bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= ancestor.ActualWidth + .1 && bounds.Bottom <= ancestor.ActualHeight + .1, $"Clipped {element.GetType().Name}/{element.Name}: {bounds}, viewport {ancestor.RenderSize}");
        }
        Check(viewport);
        for (var ancestor = VisualTreeHelper.GetParent(element); ancestor != null && !ReferenceEquals(ancestor, viewport); ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is FrameworkElement { ClipToBounds: true } clipping) Check(clipping);
    }
    static IEnumerable<DependencyObject> Logical(DependencyObject owner)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject nested) { yield return nested; foreach (var descendant in Logical(nested)) yield return descendant; }
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Until(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew(); while (!ready()) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Report deletion did not complete."); await Task.Delay(5); }
    }
}
