using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class FileBrowserOperationsTests
{
    [Theory]
    [InlineData("chapter.knegraph", "Блок-схема")]
    [InlineData("chapter.KNEGRAPH", "Блок-схема")]
    [InlineData("chapter.krl", "Скрипт")]
    [InlineData("chapter.lua", "Скрипт")]
    [InlineData("chapter.unknown", "Другое")]
    public void FileType_RecognizesVisualScriptsWithoutChangingCodeOrUnknownFiles(string name, string expected) =>
        Assert.Equal(expected, new ProjectFile { FilePath = name }.FileType);

    [Fact]
    public async Task Move_ChangesDirectoryAndNamePreservesContentsAndReloadsAtDestination_AndSamePathIsNoOp()
    {
        using var fixture = new TempProject();
        string source = fixture.Write("notes.txt", "Содержимое\nстрока 2");
        string directory = Path.Combine(fixture.ProjectPath, "assets"); Directory.CreateDirectory(directory);
        var project = await fixture.LoadAsync(); var operations = new ProjectFiles(project);
        var file = project.Files.Single(entry => entry.FilePath == source);
        Assert.Equal(source, operations.Move(file, source)); Assert.Equal("Содержимое\nстрока 2", File.ReadAllText(source));
        string destination = Path.Combine(directory, "renamed.txt");
        Assert.Equal(destination, operations.Move(file, destination));
        Assert.False(File.Exists(source)); Assert.Equal("Содержимое\nстрока 2", File.ReadAllText(destination));
        var reloaded = await fixture.LoadAsync();
        Assert.DoesNotContain(reloaded.Files, entry => entry.FilePath == source);
        Assert.Single(reloaded.Files, entry => entry.FilePath == destination && entry.FileName == "renamed.txt");
    }

    [Fact]
    public async Task Move_ExistingDestinationOutsideProjectAndMissingParentPreserveSourceAndOtherFiles()
    {
        using var fixture = new TempProject();
        string source = fixture.Write("notes.txt", "source"); string target = fixture.Write("occupied.txt", "keep");
        var project = await fixture.LoadAsync(); var operations = new ProjectFiles(project);
        var file = project.Files.Single(entry => entry.FilePath == source);
        Assert.Throws<IOException>(() => operations.Move(file, target));
        string outsideDirectory = fixture.ProjectPath + "_backup"; Directory.CreateDirectory(outsideDirectory);
        string outside = Path.Combine(outsideDirectory, "notes.txt");
        Assert.Throws<ArgumentException>(() => operations.Move(file, outside));
        string missingParent = Path.Combine(fixture.ProjectPath, "missing", "notes.txt");
        Assert.Throws<DirectoryNotFoundException>(() => operations.Move(file, missingParent));
        Assert.Equal("source", File.ReadAllText(source)); Assert.Equal("keep", File.ReadAllText(target));
        Assert.False(File.Exists(outside)); Assert.False(File.Exists(missingParent));
    }

    [Fact]
    public async Task Move_RejectsStaleMissingAndForgedOutsideSourcesWithoutWritingDestination()
    {
        using var fixture = new TempProject(); string source = fixture.Write("notes.txt", "source");
        var project = await fixture.LoadAsync(); var operations = new ProjectFiles(project);
        string stale = fixture.Write("created-after-snapshot.txt", "new");
        string outside = Path.Combine(fixture.Root, "outside.txt"); File.WriteAllText(outside, "outside");
        string target = Path.Combine(fixture.ProjectPath, "moved.txt");
        Assert.Throws<InvalidOperationException>(() => operations.Move(new ProjectFile { FilePath = stale }, target));
        Assert.Throws<ArgumentException>(() => operations.Move(new ProjectFile { FilePath = outside }, target));
        File.Delete(source);
        Assert.Throws<FileNotFoundException>(() => operations.Move(project.Files.Single(entry => entry.FilePath == source), target));
        Assert.Equal("new", File.ReadAllText(stale)); Assert.Equal("outside", File.ReadAllText(outside)); Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("project.json")]
    [InlineData("main.krl")]
    [InlineData("gamelogic.lua")]
    public async Task Move_ProtectedConfigurationAndEntryPointsCannotBeMoved(string name)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var file = project.Files.Single(entry => entry.FileName == name); string original = File.ReadAllText(file.FilePath);
        string directory = Path.Combine(fixture.ProjectPath, "archive"); Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, name);
        Assert.Throws<InvalidOperationException>(() => new ProjectFiles(project).Move(file, target));
        Assert.Equal(original, File.ReadAllText(file.FilePath)); Assert.False(File.Exists(target));
    }
}
