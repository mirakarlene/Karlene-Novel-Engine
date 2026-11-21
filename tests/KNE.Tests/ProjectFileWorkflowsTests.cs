using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectFileWorkflowsTests
{
    [Theory]
    [InlineData(".lua", "scripts")]
    [InlineData(".vlua", "scripts")]
    [InlineData(".krl", "scenes")]
    [InlineData(".knegraph", "scenes")]
    [InlineData(".kneshader", "shaders")]
    [InlineData(".cs", "scripts")]
    public void DestinationDialogUsesNativeFolderPathsForScriptFormats(string extension, string subfolder)
    {
        using var fixture = new TempProject();
        var format = Assert.Single(ProjectFileFormats.CreateGroups.SelectMany(group => group), format => format.Extension == extension);
        string initial = Path.Combine(fixture.ProjectPath, format.Folder!);

        var dialog = MainWindow.CreateDestinationFolderDialog(initial: initial, root: fixture.ProjectPath);

        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", subfolder), dialog.InitialDirectory);
    }

    [Fact]
    public void MediaAndMarkdownImportTilesRestrictExtensionsAndHaveDistinctColors()
    {
        var allFormats = ProjectFileFormats.ImportGroups.SelectMany(group => group).ToArray();
        foreach (var (title, accepted, rejected) in new[]
        {
            ("Изображения", ".PNG", ".wav"),
            ("Аудио", ".MP3", ".mp4"),
            ("Видео", ".MP4", ".md"),
            ("Markdown", ".MD", ".txt")
        })
        {
            var tile = Assert.Single(allFormats, format => format.Title == title);
            Assert.True(tile.Accepts(accepted));
            Assert.False(tile.Accepts(rejected));
            Assert.Contains("*" + accepted, tile.Filter, StringComparison.OrdinalIgnoreCase);
            Assert.Single(allFormats, format => format.Color == tile.Color);
        }
    }

    [Fact]
    public void DestinationDialogFallsBackToProjectRootWhenSuggestedFolderIsAbsent()
    {
        using var fixture = new TempProject();
        string initial = Path.Combine(fixture.ProjectPath, "game/missing");
        var dialog = MainWindow.CreateDestinationFolderDialog(initial: initial, root: fixture.ProjectPath);
        Assert.Equal(fixture.ProjectPath, dialog.InitialDirectory);
    }

    [Fact]
    public Task GraphFormatsRenderAndCreateDocumentsAtTheSelectedDestination() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var dialog = MainWindow.BuildFormatPicker(import: false);
        try
        {
            var panel = Assert.IsType<StackPanel>(dialog.Content);
            panel.Measure(new Size(540, 510)); panel.Arrange(new Rect(0, 0, 540, 510));
            var formats = panel.Children.OfType<UniformGrid>().SelectMany(row => row.Children.OfType<Button>())
                .Select(button => Assert.IsType<ProjectFileFormat>(button.Tag)).ToArray();
            Assert.Contains(formats, format => format.IsDirectory);
            Assert.Contains(formats, format => format.Title == "Другое");
            Assert.Contains(ProjectFileFormats.ImportGroups.SelectMany(row => row), format => format.IsDirectory);
            var csharp = Assert.Single(formats, format => format.Extension == ".cs");
            Assert.Contains(formats, format => format != csharp && format.Color == csharp.Color);
            string script = Path.Combine(fixture.ProjectPath, "game", MainWindow.CompleteFileName("Rules", csharp.Extension));
            await MainWindow.CreateProjectEntryAsync(project, csharp, script, CancellationToken.None);
            Assert.True(File.Exists(script));
            foreach (var format in formats.Where(format => ScriptDocument.IsGraphPath("scene" + format.Extension)))
            {
                string target = Path.Combine(fixture.ProjectPath, "game", MainWindow.CompleteFileName("Глава 1", format.Extension));
                await MainWindow.CreateProjectEntryAsync(project, format, target, CancellationToken.None);
                var document = await ScriptDocument.OpenAsync(fixture.ProjectPath, target);
                Assert.True(document.IsVisual);
                Assert.Equal(format.Extension == ".vlua", document.Graph!.IsVisualLua);
                Assert.Equal(format.Extension == ".kneshader", document.Graph.IsShader);
                await Assert.ThrowsAsync<IOException>(() => MainWindow.CreateProjectEntryAsync(project, format, target, CancellationToken.None));
            }
            Assert.Equal("scene.knegraph", MainWindow.CompleteFileName("scene.knegraph", ".knegraph"));
            Assert.Equal("scene.txt.knegraph", MainWindow.CompleteFileName("scene.txt", ".knegraph"));
            Assert.Throws<ArgumentException>(() => MainWindow.CompleteFileName("../scene", ".knegraph"));
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task MinimapSurfacesRenderAtBothEditorSizesAndLuaOmitsSceneActions() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var editor = new ScriptEditor();
        string source = fixture.Write("game/scripts/list.lua", string.Join("\n", Enumerable.Repeat("print('line')", 150)));
        await editor.OpenAsync(fixture.ProjectPath, source);
        foreach (string button in new[] { "RunButton", "TestSceneButton", "MainSceneButton", "ExportButton" })
            Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName(button)).Visibility);
        Render("CodeMinimap");
        string graph = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await editor.OpenAsync(fixture.ProjectPath, graph);
        Assert.NotNull(((RadioButton)editor.FindName("GraphMode")).Style);
        Render("GraphMinimap");
        editor.Clear();

        void Render(string name)
        {
            foreach (var size in new[] { new Size(640, 410), new Size(940, 680) })
            {
                editor.Measure(size); editor.Arrange(new Rect(size)); editor.UpdateLayout();
                var minimap = (EditorMinimap)editor.FindName(name);
                Assert.True(minimap.ActualWidth >= 80 && minimap.ActualHeight >= 80);
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(editor);
            }
        }
    });

    [Fact]
    public Task GraphMinimapUsesInheritedAccessibilityColorsAndAppliesWindowPlacement() => WorkspaceTabsTests.Sta(() =>
    {
        var map = new EditorMinimap();
        map.Bind(() => (new[] { new MinimapNode(new Rect(0, 0, 150, 150), "narrative") }, new Rect(240, 240, 20, 20)), _ => { });
        var grid = new Grid { Children = { map } };
        map.Apply(new GraphMinimapSettings(ColorCategories: true, Width: 168, Corner: "top_left"));
        grid.Measure(new Size(320, 240));
        grid.Arrange(new Rect(0, 0, 320, 240));
        grid.UpdateLayout();
        Assert.Equal(new Size(168, 117), map.RenderSize);
        Assert.Equal(new Point(0, 0), map.TranslatePoint(new Point(0, 0), grid));
        int normal = Pixel();
        BlockAccessibility.SetMode(grid, "achromatopsia");
        grid.UpdateLayout();
        int accessible = Pixel();
        Assert.NotEqual(normal, accessible);
        Assert.Equal("achromatopsia", BlockAccessibility.GetMode(map));
        map.Apply(new GraphMinimapSettings());
        grid.UpdateLayout();
        Assert.Equal(new Size(126, 88), map.RenderSize);
        Assert.Equal(new Point(194, 152), map.TranslatePoint(new Point(0, 0), grid));
        return Task.CompletedTask;

        int Pixel()
        {
            var bitmap = new RenderTargetBitmap(168, 117, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(map);
            byte[] bytes = new byte[4];
            bitmap.CopyPixels(new Int32Rect(65, 37, 1, 1), bytes, bytes.Length, 0);
            return BitConverter.ToInt32(bytes);
        }
    });

    [Fact]
    public async Task FolderOperationsRetainContentsAndProtectProjectEntryPoints()
    {
        using var fixture = new TempProject();
        string source = Path.Combine(fixture.ProjectPath, "game", "inventory");
        ProjectDirectories.Create(fixture.ProjectPath, source);
        fixture.Write("game/inventory/slot.txt", "saved");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        string duplicate = Path.Combine(fixture.ProjectPath, "game", "inventory_copy");
        await ProjectDirectories.CopyAsync(fixture.ProjectPath, source, duplicate, CancellationToken.None);
        Assert.Equal("saved", File.ReadAllText(Path.Combine(duplicate, "slot.txt")));
        Assert.True(Directory.Exists(Path.Combine(duplicate, "empty")));
        await Assert.ThrowsAsync<IOException>(() => ProjectDirectories.CopyAsync(fixture.ProjectPath, source, Path.Combine(source, "loop"), CancellationToken.None));
        string renamed = Path.Combine(fixture.ProjectPath, "duplicate");
        ProjectDirectories.Move(fixture.ProjectPath, duplicate, renamed);
        ProjectDirectories.Delete(fixture.ProjectPath, renamed);
        Assert.False(Directory.Exists(renamed));
        Assert.True(Directory.Exists(source));
        var project = await fixture.LoadAsync();
        Assert.Throws<InvalidOperationException>(() => ProjectDirectories.CheckEditable(project, Path.Combine(project.Path, "game")));
    }

    [Fact]
    public void CacheCleanupKeepsProjectAndEditorData()
    {
        using var fixture = new TempProject();
        fixture.Write("profile/cache/temp.bin", "temporary");
        fixture.Write("profile/help/blocks.html", "generated");
        string settings = fixture.Write("profile/settings.json", "keep");
        string backup = fixture.Write("profile/autosave/draft", "keep");
        string layout = fixture.Write(VisualLua.LayoutDirectory + "/graph.json", "keep");
        MainWindow.ClearEditorCacheFiles(Path.GetDirectoryName(settings)!, Path.Combine(Path.GetDirectoryName(settings)!, "help"));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(settings)!, "cache")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(settings)!, "help", "blocks.html")));
        Assert.All(new[] { settings, backup, layout }, path => Assert.Equal("keep", File.ReadAllText(path)));
    }
}
