using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectTranslationsTests
{
    [Theory]
    [InlineData(1020, 720)]
    [InlineData(760, 510)]
    public Task TranslationWindowShowsLanguagesAndControlsAtSupportedSizes(double width, double height) =>
        WorkspaceTabsTests.Sta(async () =>
        {
            using var fixture = new TempProject();
            var project = await fixture.LoadAsync();
            var owner = new Window { ShowInTaskbar = false, ShowActivated = false, Left = -2000, WindowStartupLocation = WindowStartupLocation.Manual };
            owner.Show();
            var dialog = new ProjectTranslationsWindow(owner, "forest", project, new Dictionary<string, string>(), default)
            {
                Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000, ShowActivated = false
            };
            try
            {
                dialog.Show();
                var list = WorkspaceTabsTests.Find<ListBox>(dialog, "Entries");
                for (int i = 0; i < 200 && list.Items.Count == 0; i++) await Task.Delay(10);
                dialog.UpdateLayout();
                Assert.NotEmpty(list.Items.Cast<object>());
                var visibleFields = FileBrowserDesignTests.Descendants(list).OfType<TextBox>().ToArray();
                Assert.Contains(visibleFields, field => field.IsReadOnly && field.Text.StartsWith('«') && field.Text.EndsWith('»'));
                Assert.Contains(visibleFields, field => !field.IsReadOnly && field.Text.Length == 0);
                Assert.Equal("Русский - ru", WorkspaceTabsTests.Find<TextBlock>(dialog, "OriginalLanguageLabel").Text);
                Assert.Equal("English - en", WorkspaceTabsTests.Find<TextBlock>(dialog, "TargetLanguageLabel").Text);
                var export = WorkspaceTabsTests.Find<WrapPanel>(dialog, "OriginalFormats");
                var imports = WorkspaceTabsTests.Find<WrapPanel>(dialog, "TranslationFormats");
                Assert.Equal(5, export.Children.Count);
                Assert.Equal(6, imports.Children.Count);
                Assert.All(export.Children.Cast<FrameworkElement>().Concat(imports.Children.Cast<FrameworkElement>()), control =>
                {
                    Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0);
                    var point = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), dialog);
                    Assert.InRange(point.X, 0, dialog.ActualWidth);
                    Assert.InRange(point.Y, 0, dialog.ActualHeight);
                });
                Assert.Same(dialog.FindResource("Editor.Background"), dialog.Background);
            }
            finally { dialog.Close(); owner.Close(); }
        });

    [Fact]
    public async Task CatalogReadsWholeProjectAndKeepsRepeatedDialogueLines()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                d("Мира", "Привет")
                d(nil, "Привет")
                choice("Куда?", {"Домой", "В сад"})
            end
            """);
        var graph = VisualScript.Create();
        graph.FunctionName = "chapter";
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Dialogue).Text = "Новый день";
        fixture.Write("game/scenes/chapter.knegraph", graph.Serialize());
        var module = VisualLua.ParseSource("""d("Мира", "Реплика VLua")""");
        fixture.Write("game/scripts/helper.vlua", module.Serialize());
        var project = await fixture.LoadAsync();
        var catalog = await GameTextCatalog.ReadAsync(project, new Dictionary<string, string>(), default);
        var entries = catalog.Entries;
        var repetitions = entries.Where(entry => entry.Text == "Привет").ToArray();
        Assert.Equal(2, repetitions.Length);
        Assert.Equal(repetitions[0].Key, repetitions[1].Key);
        Assert.Contains(entries, entry => entry.Category == GameTextCategory.Dialogue && entry.Text == "Новый день");
        Assert.Contains(entries, entry => entry.Category == GameTextCategory.Dialogue && entry.Text == "Реплика VLua");
        Assert.Contains(entries, entry => entry.Category == GameTextCategory.Interface && entry.Text == "В сад");
        Assert.Contains(entries, entry => entry.Category == GameTextCategory.Notifications && entry.Text == "Игра сохранена.");
        Assert.Contains(entries, entry => entry.Category == GameTextCategory.MainMenu && entry.Text == project.Engine.Game.StartLabel);
        Assert.Equal("Русский - ru", ProjectTranslations.LanguageLabel("ru"));
    }

    [Fact]
    public Task GameSettingsAppliesAndRemembersTheSelectedTranslation() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var saves = new GameSaveStore(project.Path);
        await ProjectTranslations.SaveAsync(project, "en", new Dictionary<string, string>
        {
            [ProjectTranslations.Key(GameTextCategory.Interface, "Настройки")] = "Settings",
            [ProjectTranslations.Key(GameTextCategory.Interface, GameTextCatalog.LanguageSettingLabel)] = "Game language"
        }, default);

        var showLayout = typeof(GameWindow).GetMethod("ShowLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var layout = project.Engine.Screens.Find("settings")!;
        var window = new GameWindow(project, saves);
        try
        {
            showLayout.Invoke(window, [layout.Id]);
            ComboBox Selector(GameWindow view) => WorkspaceTabsTests.Find<Canvas>(view, "ScreenCanvas").Children
                .OfType<Grid>().SelectMany(row => row.Children.OfType<ComboBox>()).Single();
            var selector = Selector(window);
            Assert.Equal("ru", selector.SelectedValue);
            selector.SelectedValue = "en";
            Assert.Equal("Game language", System.Windows.Automation.AutomationProperties.GetName(Selector(window)));
            Assert.Contains(WorkspaceTabsTests.Find<Canvas>(window, "ScreenCanvas").Children.OfType<TextBlock>(),
                title => title.Text == "Settings");
            Assert.Equal("en", System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(saves.DirectoryPath, "preferences.json")))
                .RootElement.GetProperty("Language").GetString());

            var reopened = new GameWindow(project, saves);
            try
            {
                showLayout.Invoke(reopened, [layout.Id]);
                Assert.Equal("en", Selector(reopened).SelectedValue);
                Selector(reopened).SelectedValue = "ru";
                Assert.Contains(WorkspaceTabsTests.Find<Canvas>(reopened, "ScreenCanvas").Children.OfType<TextBlock>(),
                    title => title.Text == "Настройки");
            }
            finally { reopened.Close(); }
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task WorkerDisplaysTranslatedTextButRestoresTheSameSaveInAnotherLanguage()
    {
        using var fixture = new TempProject();
        string script = fixture.Write("game/scenes/main.krl", """
            function main()
                state.friend = "Мира"
                d(nil, "Привет, {friend}!")
                choice("Куда?", {"Домой", "В сад"})
            end
            """);
        var project = await fixture.LoadAsync();
        await ProjectTranslations.SaveAsync(project, "en", new Dictionary<string, string>
        {
            [ProjectTranslations.Key(GameTextCategory.Dialogue, "Привет, {friend}!")] = "Hello, {friend}!",
            [ProjectTranslations.Key(GameTextCategory.Interface, "Домой")] = "Home"
        }, default);
        await using var translated = new ProcessGameSession(project, _ => { }) { PresentationLanguage = "en", InitialDialogueSpeed = 0 };
        await translated.StartAsync();
        Assert.Equal("Hello, Мира!", translated.Story.Text);
        await translated.AdvanceAsync();
        Assert.Equal(new[] { "Home", "В сад" }, translated.Story.Options);
        var checkpoint = await translated.GetCheckpointAsync();
        Assert.Equal(new[] { "Домой", "В сад" }, checkpoint.Frame.Options);
        await using var original = new ProcessGameSession(project, _ => { }) { InitialDialogueSpeed = 0 };
        await original.RestoreAsync(checkpoint);
        Assert.Equal(checkpoint.Frame.Options, original.Story.Options);
        Assert.Contains("Привет, {friend}!", await File.ReadAllTextAsync(script));

        string packagedPath = Path.Combine(fixture.Root, "packaged");
        await GameBuildExporter.CopyProjectAsync(project, packagedPath, default);
        var packaged = await ProjectLoader.LoadProjectAsync(packagedPath);
        Assert.Equal(new[] { "ru", "en" }, ProjectTranslations.Languages(packaged));
        Assert.Equal("Home", ProjectTranslations.Load(packaged, "en").Translate(GameTextCategory.Interface, "Домой"));
    }
}
