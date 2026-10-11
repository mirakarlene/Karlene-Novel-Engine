using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectFilesTests
{
    [Fact]
    public async Task MissingEntryPoints_DoNotBlockOrdinaryFileOperations()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", """{"entry_points":{"main_scene":"missing.krl","init_script":"missing.lua"}}""");
        string path = fixture.Write("notes.txt", "keep");
        var project = await fixture.LoadAsync();
        var operations = new ProjectFiles(project);
        var file = project.Files.Single(f => f.FileName == "notes.txt");
        string renamed = operations.Rename(file, "renamed.txt");
        Assert.Equal("keep", File.ReadAllText(renamed));
        project = await fixture.LoadAsync();
        new ProjectFiles(project).Delete(project.Files.Single(f => f.FileName == "renamed.txt"));
        Assert.False(File.Exists(renamed));
        Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task CreateDuplicateRenameDelete_UpdateDiskAndReloadedSnapshot()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var operations = new ProjectFiles(project);
        string created = operations.Create(Path.Combine(fixture.ProjectPath, "notes.txt"));
        File.WriteAllText(created, "Содержимое");
        project = await fixture.LoadAsync();
        operations = new ProjectFiles(project);
        var file = project.Files.Single(f => f.FileName == "notes.txt");
        string copy = operations.Duplicate(file, Path.Combine(fixture.ProjectPath, "notes_copy.txt"));
        Assert.Equal("Содержимое", File.ReadAllText(copy));
        string renamed = operations.Rename(file, "renamed.txt");
        Assert.False(File.Exists(created));
        Assert.Equal("Содержимое", File.ReadAllText(renamed));
        project = await fixture.LoadAsync();
        new ProjectFiles(project).Delete(project.Files.Single(f => f.FileName == "renamed.txt"));
        Assert.False(File.Exists(renamed));
        Assert.Equal("Содержимое", File.ReadAllText(copy));
        var snapshot = await fixture.LoadAsync();
        Assert.DoesNotContain(snapshot.Files, f => f.FileName == "notes.txt" || f.FileName == "renamed.txt");
        Assert.Contains(snapshot.Files, f => f.FileName == "notes_copy.txt");
    }

    [Fact]
    public async Task CreateAndDuplicate_ExistingDestination_PreserveContents()
    {
        using var fixture = new TempProject();
        string target = fixture.Write("existing.txt", "keep");
        var project = await fixture.LoadAsync();
        var operations = new ProjectFiles(project);
        Assert.Throws<IOException>(() => operations.Create(target));
        Assert.Throws<IOException>(() => operations.Duplicate(project.Files.Single(f => f.FileName == "README.txt"), target));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("project.json")]
    [InlineData("main.krl")]
    [InlineData("gamelogic.lua")]
    public async Task DeleteAndRename_RequiredFile_AreRejected(string name)
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var operations = new ProjectFiles(project);
        var file = project.Files.Single(f => f.FileName == name);
        string original = File.ReadAllText(file.FilePath);
        Assert.Throws<InvalidOperationException>(() => operations.Delete(file));
        Assert.Throws<InvalidOperationException>(() => operations.Rename(file, "changed.txt"));
        Assert.Equal(original, File.ReadAllText(file.FilePath));
    }

    [Fact]
    public async Task CreateAndDuplicate_PrefixSibling_RejectBeforeWriting()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        string sibling = fixture.ProjectPath + "_backup";
        Directory.CreateDirectory(sibling);
        string target = Path.Combine(sibling, "outside.txt");
        var operations = new ProjectFiles(project);
        Assert.Throws<ArgumentException>(() => operations.Create(target));
        Assert.Throws<ArgumentException>(() => operations.Duplicate(project.Files[0], target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Delete_ForgedOutsidePath_PreservesOutsideFile()
    {
        using var fixture = new TempProject();
        string outside = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(outside, "keep");
        var project = await fixture.LoadAsync();
        Assert.Throws<ArgumentException>(() => new ProjectFiles(project).Delete(new ProjectFile { FilePath = outside }));
        Assert.Equal("keep", File.ReadAllText(outside));
    }
}
