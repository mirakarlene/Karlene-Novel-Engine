using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using static KNE.Tests.WorkspaceTabsTests;

namespace KNE.Tests;

public sealed class ProjectCompatibilityTests
{
    [Fact]
    public void LegacyCatalog_IsUnavailableForCreation_AndFavoritesAndRecentMoveToBranching()
    {
        Assert.DoesNotContain(BlockLibrary.Definitions, entry => entry.Kind == ScriptNodeKind.Condition);
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockLibrary.Create(ScriptNodeKind.Condition));
        var preferences = new BlockLibraryPreferences
        {
            Favorites = ["logic/Condition", "narrative/Condition", "audio/Music"],
            Recent = ["logic/Condition", "narrative/Condition", "logic/CompareVariables"]
        };
        preferences.Normalize();
        Assert.Equal(new[] { "narrative/Branching", "audio/Music" }, preferences.Favorites);
        Assert.Equal(new[] { "narrative/Branching", "logic/CompareVariables" }, preferences.Recent);
    }

    [Theory]
    [InlineData("logic")]
    [InlineData("narrative")]
    [InlineData("")]
    public async Task Migration_ReplacesLegacyCondition_PreservesBehaviorIdsAndLayout_AndBacksUpExactBytes(string category)
    {
        using var fixture = new TempProject();
        string source = Legacy(category);
        fixture.Write("game/scenes/main.krl", "function main() call(legacy) end");
        string path = fixture.Write("game/scenes/legacy.knegraph", source);
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        await File.WriteAllBytesAsync(path, original);
        var project = await fixture.LoadAsync();
        var issue = Assert.Single(await ProjectCompatibility.InspectAsync(project));
        Assert.Equal(Path.Combine("game", "scenes", "legacy.knegraph"), issue.File); Assert.True(issue.CanMigrate);
        string backup = await ProjectCompatibility.MigrateAsync(project);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(backup, "game", "scenes", "legacy.knegraph.original")));
        var graph = VisualScript.Parse(await File.ReadAllTextAsync(path));
        var branch = graph.Nodes.Single(node => node.Id == "check");
        Assert.Equal(ScriptNodeKind.Branching, branch.Kind); Assert.Equal("narrative", branch.Category);
        Assert.Equal("yes", branch.True); Assert.Equal("no", branch.False);
        Assert.Equal(315, branch.Width); Assert.Equal(177, branch.Height); Assert.Equal(-340, branch.X); Assert.Equal(290, branch.Y);
        Assert.Equal("score", branch.Variable); Assert.Equal("17", branch.Value); Assert.Equal(ScriptComparison.GreaterOrEqual, branch.Comparison);
        Assert.Contains("state[\"score\"] >= 17", graph.Compile());
        Assert.DoesNotContain("\"Kind\": \"Condition\"", File.ReadAllText(path));
        Assert.Empty(await ProjectCompatibility.InspectAsync(await fixture.LoadAsync()));
        Assert.DoesNotContain((await fixture.LoadAsync()).Files, file => file.FilePath.Contains(".kne-backups", StringComparison.Ordinal));
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("yes", runtime.Story.Text);
    }

    [Fact]
    public async Task Migration_NormalizesKnownPropertyCasingWithoutDroppingResizedLayout()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("layout.knegraph", """{"version":1,"functionName":"layout","nodes":[{"id":"note","kind":"Comment","width":444,"height":333,"text":"kept"}]}""");
        var project = await fixture.LoadAsync();
        Assert.True(Assert.Single(await ProjectCompatibility.InspectAsync(project)).CanMigrate);
        await ProjectCompatibility.MigrateAsync(project);
        var node = Assert.Single(VisualScript.Parse(await File.ReadAllTextAsync(path)).Nodes);
        Assert.Equal(444, node.Width); Assert.Equal(333, node.Height); Assert.Equal("kept", node.Text);
        Assert.Empty(await ProjectCompatibility.InspectAsync(await fixture.LoadAsync()));
    }

    [Theory]
    [InlineData("""{"Version":2,"Nodes":[]}""")]
    [InlineData("""{"Version":1,"Nodes":[{"Id":"one","Kind":"BrandNewCommand"}]}""")]
    [InlineData("""{"Version":1,"Nodes":[{"Id":"one","Kind":99999}]}""")]
    [InlineData("""{"Version":1,"Nodes":[{"Id":"one","Kind":"Comment","FuturePayload":{"hello":"world"}}]}""")]
    [InlineData("""{"Version":1,"Nodes":[{"Id":"one","Kind":"Comment","Width":300,"width":500}]}""")]
    public async Task UnknownSchema_DisablesMigrationAndLeavesEveryFileUnchanged(string source)
    {
        using var fixture = new TempProject();
        string legacy = fixture.Write("a-legacy.knegraph", Legacy("logic"));
        string unknown = fixture.Write("z-unknown.knegraph", source);
        byte[] before = await File.ReadAllBytesAsync(legacy);
        var project = await fixture.LoadAsync();
        Assert.Contains(await ProjectCompatibility.InspectAsync(project), issue => !issue.CanMigrate);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectCompatibility.MigrateAsync(project));
        Assert.Equal(source, await File.ReadAllTextAsync(unknown)); Assert.Equal(before, await File.ReadAllBytesAsync(legacy));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne-backups")));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"Nodes":[{"Id":"bad","Kind":"Comment","Width":"wide"}]}""")]
    [InlineData("""{"Nodes":[{"Id":"bad","Kind":"Comment","Width":100}]}""")]
    public async Task CorruptData_IsNotMisreportedAsAnEngineUpgrade(string source)
    {
        using var fixture = new TempProject(); fixture.Write("corrupt.knegraph", source);
        Assert.Empty(await ProjectCompatibility.InspectAsync(await fixture.LoadAsync()));
        Assert.Throws<InvalidDataException>(() => VisualScript.Parse(source));
    }

    [Fact]
    public async Task Migration_FileLockedDuringCommit_RollsBackEarlierFilesAndKeepsBackups()
    {
        using var fixture = new TempProject();
        string first = fixture.Write("a-first.knegraph", Legacy("logic"));
        string second = fixture.Write("z-second.knegraph", Legacy("narrative").Replace("\"legacy\"", "\"legacy_second\""));
        byte[] originalFirst = File.ReadAllBytes(first), originalSecond = File.ReadAllBytes(second);
        var project = await fixture.LoadAsync();
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ProjectCompatibility.MigrateAsync(project));
        Assert.Equal(originalFirst, File.ReadAllBytes(first)); Assert.Equal(originalSecond, File.ReadAllBytes(second));
        string backup = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.ProjectPath, ".kne-backups")));
        Assert.Equal(originalFirst, File.ReadAllBytes(Path.Combine(backup, "a-first.knegraph.original")));
        Assert.Equal(originalSecond, File.ReadAllBytes(Path.Combine(backup, "z-second.knegraph.original")));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CurrentWidthAndHeight_AreSupportedWithoutMigration_AndCancellationNeverWrites()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create(); graph.FunctionName = "resized";
        graph.Nodes[1].Width = 560; graph.Nodes[1].Height = 400;
        string path = fixture.Write("current.knegraph", graph.Serialize());
        Assert.Empty(await ProjectCompatibility.InspectAsync(await fixture.LoadAsync()));
        Assert.Equal(560, VisualScript.Parse(await File.ReadAllTextAsync(path)).Nodes[1].Width);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ProjectCompatibility.MigrateAsync(await fixture.LoadAsync()));
        string legacy = fixture.Write("legacy.knegraph", Legacy("logic")); string original = File.ReadAllText(legacy);
        var project = await fixture.LoadAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectCompatibility.MigrateAsync(project, cancellation.Token));
        Assert.Equal(original, File.ReadAllText(legacy)); Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne-backups")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningIncompatibleProject_OffersRecoveryAndPreservesOriginalOnCancelOrNew(bool createNew)
    {
        var action = createNew ? ProjectCompatibilityAction.NewProject : ProjectCompatibilityAction.Cancel;
        using var fixture = new TempProject(); string path = fixture.Write("old.knegraph", Legacy("logic"));
        string original = File.ReadAllText(path);
        await Sta(async () =>
        {
            var window = new MainWindow(); int calls = 0; var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ChooseCompatibilityAction = issues => { calls++; Assert.True(Assert.Single(issues).CanMigrate); return action; };
            window.CreateReplacementProject = () => created.TrySetResult();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                if (action == ProjectCompatibilityAction.NewProject) await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, calls); Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "ProjectPanel").Visibility);
                Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne-backups")));
                Assert.Equal(action == ProjectCompatibilityAction.NewProject, created.Task.IsCompleted);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task OpeningIncompatibleProject_MigratesBeforeFrameCatalogAndShowsProject_AndRecoveryDialogExplainsUnsupportedFormats()
    {
        using var fixture = new TempProject(); fixture.Write("old.knegraph", Legacy("logic"));
        await Sta(async () =>
        {
            var window = new MainWindow(); window.ChooseCompatibilityAction = _ => ProjectCompatibilityAction.Migrate;
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                Assert.Equal(Visibility.Visible, Find<Grid>(window, "ProjectPanel").Visibility);
                Assert.True(File.Exists(Path.Combine(fixture.ProjectPath, "engine", "frame-ids.json")));
                Assert.Empty(await ProjectCompatibility.InspectAsync(await fixture.LoadAsync()));
                var dialog = new ProjectCompatibilityWindow([new("bad.knegraph", "Unsupported version", false)]);
                Assert.Contains("старой версии KNE", dialog.Title);
                var buttons = ((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
                Assert.False(buttons.Single(button => Equals(button.Content, "Миграция данных")).IsEnabled);
                Assert.True(buttons.Single(button => Equals(button.Content, "Создать новый проект")).IsEnabled);
                dialog.Close();
            }
            finally { window.Close(); }
        });
    }

    private static string Legacy(string category)
    {
        var graph = new VisualScript { FunctionName = "legacy", Nodes = [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "value" },
            new() { Id = "value", Kind = ScriptNodeKind.SetVariable, Variable = "score", Value = "18", Next = "check" },
            new() { Id = "check", Kind = ScriptNodeKind.Condition, Category = category, Variable = "score", Value = "17", Comparison = ScriptComparison.GreaterOrEqual, True = "yes", False = "no", Width = 315, Height = 177, X = -340, Y = 290 },
            new() { Id = "yes", Kind = ScriptNodeKind.Dialogue, Text = "yes", Next = "end" },
            new() { Id = "no", Kind = ScriptNodeKind.Dialogue, Text = "no", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }] };
        return graph.Serialize();
    }
}
