using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ScriptDocumentTests
{
    [Fact]
    public async Task NewGraphs_UseUniqueNamedFunctions_AndLeaveMainUnique()
    {
        using var fixture = new TempProject();
        Directory.CreateDirectory(Path.Combine(fixture.ProjectPath, "chapters"));
        var first = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "chapter.knegraph"));
        var second = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "chapters", "chapter.knegraph"));
        Assert.Equal("scene_chapter", first.Graph!.FunctionName);
        Assert.Equal("scene_chapter_2", second.Graph!.FunctionName);
        Assert.Equal("main", GameScripts.Load(await fixture.LoadAsync()).Main.Name);
    }
    [Fact]
    public async Task CreateSaveReopen_PreservesDraftConnectionsAndLayout()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "scene.knegraph");
        var document = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        Assert.False(document.IsDirty);
        var dialogue = document.Graph!.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue);
        dialogue.Next = null;
        dialogue.X = 495;
        dialogue.Y = 310;
        dialogue.Text = "Незавершённая сцена";
        Assert.True(document.IsDirty);
        await document.SaveAsync();
        Assert.False(document.IsDirty);
        var reopened = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
        var draft = reopened.Graph!.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue);
        Assert.Equal(495, draft.X);
        Assert.Equal(310, draft.Y);
        Assert.Null(draft.Next);
        Assert.Equal("Незавершённая сцена", draft.Text);
        Assert.Throws<InvalidDataException>(() => reopened.Graph.Compile());
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_ExternalChangeOrDeletion_DoesNotOverwrite(bool deleted)
    {
        using var fixture = new TempProject();
        string path = fixture.Write("manual.krl", "function main() d(nil, 'Before') end");
        var document = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
        document.Code = "function main() d(nil, 'Editor') end";
        if (deleted) File.Delete(path);
        else File.WriteAllText(path, "external edit");
        await Assert.ThrowsAsync<IOException>(() => document.SaveAsync());
        Assert.True(document.IsDirty);
        if (deleted) Assert.False(File.Exists(path));
        else Assert.Equal("external edit", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"));
    }

    [Fact]
    public async Task Export_ProducesRunnableKrlWithoutReplacingGraphOrExistingFile()
    {
        using var fixture = new TempProject();
        string graphPath = Path.Combine(fixture.ProjectPath, "scene.knegraph");
        var document = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, graphPath);
        document.Graph!.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue).Text = "Экспорт 🌸";
        string codePath = Path.Combine(fixture.ProjectPath, "export.krl");
        await document.ExportAsync(codePath);
        var code = await ScriptDocument.OpenAsync(fixture.ProjectPath, codePath);
        Assert.False(code.IsVisual);
        var project = await fixture.LoadAsync();
        project.Configuration.EntryPoints!.MainScene = "export.krl";
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true, preview: true);
        await runtime.StartAsync();
        Assert.Equal("Экспорт 🌸", runtime.Story.Text);
        Assert.Equal("Добро пожаловать!",
            VisualScript.Parse(File.ReadAllText(graphPath)).Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue).Text);
        await Assert.ThrowsAsync<IOException>(() => document.ExportAsync(codePath));
        await Assert.ThrowsAsync<IOException>(() => ScriptDocument.CreateVisualAsync(fixture.ProjectPath, graphPath));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"));
    }

    [Fact]
    public async Task Document_ProjectBoundaryAndUnsupportedExtensions_AreRejected()
    {
        using var fixture = new TempProject();
        string outside = Path.Combine(fixture.Root, "outside.knegraph");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScriptDocument.CreateVisualAsync(fixture.ProjectPath, outside));
        Assert.False(File.Exists(outside));
        string text = fixture.Write("notes.txt", "keep");
        await Assert.ThrowsAsync<ArgumentException>(() => ScriptDocument.OpenAsync(fixture.ProjectPath, text));
        var graph = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath,
            Path.Combine(fixture.ProjectPath, "scene.knegraph"));
        await Assert.ThrowsAsync<ArgumentException>(() => graph.ExportAsync(Path.Combine(fixture.Root, "outside.krl")));
        await Assert.ThrowsAsync<ArgumentException>(() => graph.ExportAsync(text));
        Assert.Equal("keep", File.ReadAllText(text));
    }
}
