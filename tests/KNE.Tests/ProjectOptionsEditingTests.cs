using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectOptionsEditingTests
{
    [Fact]
    public Task ProjectFieldsAndRuntimeOptionsRoundTripTogetherWhileUnknownFreshJsonFieldsSurvive() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("assets/icon.png", "icon fixture"); fixture.Write("assets/menu.wav", "music fixture");
        string configPath = Path.Combine(fixture.ProjectPath, "project.json");
        var config = Read(configPath); config["custom"] = new JsonObject { ["nested"] = new JsonArray("before", 2, true) };
        config["metadata"]!["unknown_author_metadata"] = "preserve"; config["remove_null"] = null; Write(configPath, config);
        var project = await fixture.LoadAsync(); var window = new ProjectParametersWindow(project);
        try
        {
            window.TitleInput.Text = "Другая история";
            window.BuildInput.Author.Text = "Автор"; window.BuildInput.Team.Text = "Команда"; window.BuildInput.Version.Text = "2.1";
            window.MetadataInput["game_info.description"].Text = "Описание для магазина\nСо второй строкой";
            window.MetadataInput["metadata.icon"].Text = "assets/icon.png"; window.MetadataInput["metadata.default_language"].Text = "en-US";
            window.MetadataInput["metadata.created_date"].Text = "2026-11-04"; window.MetadataInput["metadata.kne_version"].Text = "1.2.3";
            var additional = JsonNode.Parse(window.MetadataInput.Additional.Text)!.AsObject(); additional["custom"]!["nested"] = new JsonArray("edited", 3, false);
            additional["additional_section"] = new JsonObject { ["enabled"] = true }; additional["nullable_custom"] = null; additional.Remove("remove_null"); window.MetadataInput.Additional.Text = additional.ToJsonString();
            window.ResolutionInput.SelectedValue = "qhd"; window.PaletteInput.SelectedValue = "light";
            window.PreloadInput.SelectedValue = 2; window.GraphicsInput.CacheLimitInput.SelectedValue = 32;
            window.RollbackInput.Text = "25"; window.AutosaveInput.SelectedValue = "actions"; window.AutosaveActionsInput.Text = "7";
            window.MenuMusicInput.Text = "assets/menu.wav"; window.FileSystemInput.SelectedValue = "project";
            Flags(window)["autosave"].IsChecked = true; Flags(window)["encrypt"].IsChecked = true;
            Flags(window)["release_debug"].IsChecked = true; Flags(window)["reports"].IsChecked = false;
            Flags(window)["plugins"].IsChecked = false; Flags(window)["network"].IsChecked = false;
            config = Read(configPath); config["fresh_external"] = new JsonObject { ["answer"] = 42 }; Write(configPath, config);
            await window.SaveAsync();

            var loaded = await fixture.LoadAsync();
            Assert.Equal("Другая история", loaded.Title); Assert.Equal(fixture.ProjectPath, loaded.Path);
            Assert.Equal("Автор", loaded.Configuration.Metadata!.Author); Assert.Equal("Команда", loaded.Configuration.Metadata.Team);
            Assert.Equal("2.1", loaded.Configuration.Metadata.Version); Assert.Equal("assets/icon.png", loaded.Configuration.Metadata.Icon);
            Assert.Equal("en-US", loaded.Configuration.Metadata.DefaultLanguage); Assert.Equal("2026-11-04", loaded.Configuration.Metadata.CreatedDate);
            Assert.Equal("1.2.3", loaded.Configuration.Metadata.KneVersion); Assert.Equal("Описание для магазина\nСо второй строкой", loaded.Configuration.GameInfo!.Description);
            Assert.Equal("Другая история", loaded.Configuration.GameInfo.Title);
            config = Read(configPath);
            Assert.Equal("preserve", (string?)config["metadata"]!["unknown_author_metadata"]); Assert.Equal(42, (int)config["fresh_external"]!["answer"]!);
            Assert.True((bool)config["additional_section"]!["enabled"]!); Assert.Equal("edited", (string?)config["custom"]!["nested"]![0]);
            Assert.Equal(3, (int)config["custom"]!["nested"]![1]!); Assert.False((bool)config["custom"]!["nested"]![2]!);
            Assert.True(config.ContainsKey("nullable_custom")); Assert.Null(config["nullable_custom"]); Assert.False(config.ContainsKey("remove_null"));
            Assert.Equal((2560, 1440), (loaded.Engine.Window.Width, loaded.Engine.Window.Height)); Assert.Equal("light", loaded.Engine.Theme.Palette);
            Assert.Equal(2, loaded.Engine.Performance.PreloadScenes); Assert.Equal(32, loaded.Engine.Performance.Graphics.ResourceCacheLimitMb);
            Assert.Equal(25, loaded.Engine.Game.RollbackDepth); Assert.True(loaded.Engine.Game.AutoSave); Assert.Equal("actions", loaded.Engine.Game.AutoSaveInterval);
            Assert.Equal(7, loaded.Engine.Game.AutoSaveActions); Assert.Equal("assets/menu.wav", loaded.Engine.Game.MenuMusic);
            Assert.True(loaded.Engine.Security.EncryptScripts); Assert.True(loaded.Engine.Security.ReleaseDebugConsole); Assert.False(loaded.Engine.Security.CrashReports);
            Assert.False(loaded.Engine.Security.AllowPlugins); Assert.False(loaded.Engine.Security.AllowNetwork); Assert.Equal("project", loaded.Engine.Security.FileSystem);
            var build = ProjectBuildSettingsStore.Load(loaded.Path); Assert.Equal("Автор", build.Author); Assert.Equal("Команда", build.Team); Assert.Equal("2.1", build.Version);
            Assert.Empty(Directory.EnumerateFiles(fixture.ProjectPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("{")] [InlineData("[]")] [InlineData("null")]
    public Task InvalidAdditionalJsonPreservesEveryProjectAndEngineFileAndKeepsDraft(string invalid) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath);
            window.TitleInput.Text = "Unsaved"; window.MetadataInput.Additional.Text = invalid;
            await Assert.ThrowsAsync<InvalidDataException>(window.SaveAsync);
            AssertSnapshot(before, fixture.ProjectPath); Assert.Equal(invalid, window.MetadataInput.Additional.Text); Assert.Equal("Unsaved", window.TitleInput.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("rollback", "-1")] [InlineData("rollback", "1001")] [InlineData("rollback", "2.5")]
    [InlineData("actions", "0")] [InlineData("actions", "1001")]
    [InlineData("language", ";")]
    [InlineData("icon", "not-an-icon.txt")] [InlineData("music", "not-an-icon.txt")]
    [InlineData("json_size", "1048576")]
    public Task InvalidOptionsAreRejectedBeforeWritingAnyRelatedFile(string field, string invalid) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("not-an-icon.txt", "text"); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.TitleInput.Text = "Unsaved title";
            switch (field)
            {
                case "rollback": window.RollbackInput.Text = invalid; break;
                case "actions": window.AutosaveActionsInput.Text = invalid; break;
                case "language": window.MetadataInput["metadata.default_language"].Text = invalid; break;
                case "icon": window.MetadataInput["metadata.icon"].Text = invalid; break;
                case "music": window.MenuMusicInput.Text = invalid; break;
                case "json_size": window.MetadataInput.Additional.Text = JsonSerializer.Serialize(new { padding = new string('x', int.Parse(invalid)) }); break;
            }
            await Assert.ThrowsAsync<InvalidDataException>(window.SaveAsync); AssertSnapshot(before, fixture.ProjectPath);
            Assert.Equal("Unsaved title", window.TitleInput.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ConcurrentEngineEditRejectsTheWholeSaveAndPreservesTheExternalVersion() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            window.TitleInput.Text = "Draft title"; window.MenuMusicInput.Text = ""; window.RollbackInput.Text = "5";
            string gameFile = Path.Combine(fixture.ProjectPath, "engine/game.json"); var game = Read(gameFile); game["subtitle"] = "External edit"; Write(gameFile, game);
            var before = Snapshot(fixture.ProjectPath);
            await Assert.ThrowsAsync<IOException>(window.SaveAsync); AssertSnapshot(before, fixture.ProjectPath);
            Assert.Equal("Draft title", window.TitleInput.Text); Assert.Equal("5", window.RollbackInput.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1664, 936, true)] [InlineData(639, 936, false)] [InlineData(1664, 479, false)]
    public Task CustomWindowResolutionSavesTheExactDimensionsAndRejectsInvalidSizeBeforeAnyWrite(int width, int height, bool valid) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.ResolutionInput.SelectedValue = "custom";
            window.CustomWidthInput.Text = width.ToString(); window.CustomHeightInput.Text = height.ToString();
            Assert.True(window.CustomWidthInput.IsEnabled); Assert.True(window.CustomHeightInput.IsEnabled);
            if (valid)
            {
                await window.SaveAsync(); var saved = await fixture.LoadAsync();
                Assert.Equal(width, saved.Engine.Window.Width); Assert.Equal(height, saved.Engine.Window.Height);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(window.SaveAsync); AssertSnapshot(before, fixture.ProjectPath);
                Assert.Equal(width.ToString(), window.CustomWidthInput.Text); Assert.Equal(height.ToString(), window.CustomHeightInput.Text);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task LockedEngineFileRollsBackAllEarlierSettingsMetadataAndNewBuildProfile(bool previousBuild) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        if (previousBuild) await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "Old author" });
        var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.TitleInput.Text = "Draft title"; window.RollbackInput.Text = "3"; window.ResolutionInput.SelectedValue = "qhd";
            using (var locked = new FileStream(Path.Combine(fixture.ProjectPath, "engine/game.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = await Record.ExceptionAsync(window.SaveAsync);
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file failure.");
            }
            AssertSnapshot(before, fixture.ProjectPath); Assert.Equal("Draft title", window.TitleInput.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task SettingsTransactionRestoresExistingFilesAndDeletesNewEarlierFilesOnLaterCommitFailure()
    {
        using var fixture = new TempProject();
        string first = fixture.Write("transaction/first.json", "first before"), lockedPath = fixture.Write("transaction/locked.json", "locked before");
        string added = Path.Combine(fixture.ProjectPath, "transaction/new.json");
        var values = new Dictionary<string, byte[]> { [first] = [1, 2], [added] = [3, 4], [lockedPath] = [5, 6] };
        var originals = values.Keys.ToDictionary(path => path, ProjectSettingsTransaction.Read);
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => ProjectSettingsTransaction.CommitAsync(values, originals));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file failure.");
        }
        Assert.Equal("first before", File.ReadAllText(first)); Assert.Equal("locked before", File.ReadAllText(lockedPath)); Assert.False(File.Exists(added));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(first)!, "*.tmp"));
    }

    [Theory]
    [InlineData(780, 840)] [InlineData(540, 500)]
    public Task ThreeProjectPagesKeepFooterFullSizeAndDraftsWhileScrollingThePageBody(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new ProjectParametersWindow(await fixture.LoadAsync());
        try
        {
            if (Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER") != null) EditorTheme.Apply(window, "kne");
            var root = (FrameworkElement)window.Content;
            void Layout() { root.Measure(new Size(width - 20, height - 40)); root.Arrange(new Rect(0, 0, width - 20, height - 40)); root.UpdateLayout(); }
            Layout();
            var save = Descendants(root).OfType<Button>().Single(button => Equals(button.Content, "Сохранить"));
            Rect Bounds(FrameworkElement item) => item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
            var saveBounds = Bounds(save); var nextBounds = Bounds(window.NextPage);
            var scroll = Descendants(root).OfType<ScrollViewer>().First();
            if (height == 500) Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(1, root.LayoutTransform.Value.M11);
            Assert.True(saveBounds.Width >= 64, $"Save bounds: {saveBounds}; root: {root.ActualWidth}x{root.ActualHeight}");
            Assert.True(saveBounds.Height >= 25, $"Save bounds: {saveBounds}; root: {root.ActualWidth}x{root.ActualHeight}");
            Assert.True(nextBounds.Bottom <= root.ActualHeight + 0.01, $"Next bounds: {nextBounds}; root: {root.ActualWidth}x{root.ActualHeight}");
            Assert.Equal(12, save.FontSize);
            Assert.True(Bounds(scroll).Bottom <= saveBounds.Top + 0.01, $"Scroll bounds: {Bounds(scroll)}; save bounds: {saveBounds}");
            Assert.Equal(0, window.Page); Assert.False(window.PreviousPage.IsEnabled);
            Capture(window, "project-options-1-" + width);
            window.RollbackInput.Text = "42";
            window.NextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Assert.Equal(1, window.Page); Assert.DoesNotContain(Descendants((DependencyObject)scroll.Content), node => ReferenceEquals(node, window.BuildInput.Author));
            Assert.Contains(Descendants((DependencyObject)scroll.Content).OfType<TextBlock>(), text => text.Text == "Безопасность");
            Assert.Equal(saveBounds, Bounds(save)); Assert.Equal(nextBounds.Y, Bounds(window.NextPage).Y);
            Capture(window, "project-options-2-" + width);
            window.NextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Assert.Equal(2, window.Page); Assert.False(window.NextPage.IsEnabled); Assert.True(window.PreviousPage.IsEnabled);
            Assert.Same(window.MetadataInput, scroll.Content); Assert.Equal(saveBounds, Bounds(save)); Assert.Equal(nextBounds.Y, Bounds(window.NextPage).Y);
            Capture(window, "project-options-3-" + width);
            window.BuildInput.Author.Text = "Draft author";
            scroll.ScrollToEnd(); Layout(); Assert.Equal(saveBounds, Bounds(save)); Assert.True(scroll.VerticalOffset > 0);
            Capture(window, "project-options-3-bottom-" + width);
            window.PreviousPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.PreviousPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Assert.Equal("42", window.RollbackInput.Text); Assert.Equal("Draft author", window.BuildInput.Author.Text);
        }
        finally { window.Close(); }
    });

    private static Dictionary<string, CheckBox> Flags(ProjectParametersWindow window) => (Dictionary<string, CheckBox>)typeof(ProjectParametersWindow).GetField("switches", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static void Write(string path, JsonObject value) => File.WriteAllText(path, value.ToJsonString(ProjectConfiguration.JsonOptions));
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private static void AssertSnapshot(Dictionary<string, byte[]> before, string root)
    {
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node) { yield return node; foreach (var nested in Descendants(node)) yield return nested; }
    }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); var root = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(root.RenderSize.Width + root.Margin.Left + root.Margin.Right), height = (int)Math.Ceiling(root.RenderSize.Height + root.Margin.Top + root.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
}
