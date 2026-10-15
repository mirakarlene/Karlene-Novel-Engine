using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectImportTests
{
    [Fact]
    public async Task Import_CopiesBinaryWithoutChangingSource_AndPublishesOnlyCompleteFile()
    {
        using var fixture = new TempProject();
        byte[] bytes = [0, 255, 12, 8, 34];
        string source = Path.Combine(fixture.Root, "внешний файл.png");
        await File.WriteAllBytesAsync(source, bytes);
        string target = Path.Combine(fixture.ProjectPath, "game", "backgrounds", "импорт.png");
        await ProjectImport.CopyFileAsync(fixture.ProjectPath, source, target);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, ".kne-import-*"));
    }

    [Theory]
    [InlineData("project.json")]
    [InlineData("engine/state.json")]
    [InlineData("saves/imported.krl")]
    [InlineData("metadata/custom.json")]
    [InlineData("../outside.krl")]
    public async Task Import_RejectsProtectedOrEscapingDestination(string relative)
    {
        using var fixture = new TempProject();
        string source = Path.Combine(fixture.Root, "external.bin");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        string target = Path.GetFullPath(Path.Combine(fixture.ProjectPath, relative));
        byte[]? before = File.Exists(target) ? await File.ReadAllBytesAsync(target) : null;
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectImport.CopyFileAsync(fixture.ProjectPath, source, target));
        if (before != null) Assert.Equal(before, await File.ReadAllBytesAsync(target));
        else Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Import_NeverOverwritesExistingFile_AndLeavesNoStagingOnFailure()
    {
        using var fixture = new TempProject();
        string target = fixture.Write("game/scenes/existing.krl", "original");
        await Assert.ThrowsAsync<IOException>(() => ProjectImport.WriteScriptAsync(fixture.ProjectPath, target, "changed"));
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        string missing = Path.Combine(fixture.Root, "missing.png");
        string destination = Path.Combine(fixture.ProjectPath, "game", "backgrounds", "new.png");
        await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectImport.CopyFileAsync(fixture.ProjectPath, missing, destination));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, ".kne-import-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Import_CancelledBeforePublication_WritesNothing()
    {
        using var fixture = new TempProject();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        string target = Path.Combine(fixture.ProjectPath, "game", "scenes", "cancel.krl");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectImport.WriteScriptAsync(fixture.ProjectPath, target, "code", cancel.Token));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, ".kne-import-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Import_AdaptationUsesUtf8WithoutBom_AndRejectsWrongExtension()
    {
        using var fixture = new TempProject();
        string target = Path.Combine(fixture.ProjectPath, "game", "scenes", "адаптация.krl");
        const string code = "function imported()\n    d(nil, 'Привет')\nend";
        await ProjectImport.WriteScriptAsync(fixture.ProjectPath, target, code);
        Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(code), await File.ReadAllBytesAsync(target));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectImport.WriteScriptAsync(fixture.ProjectPath, Path.ChangeExtension(target, ".exe"), code));
    }
}
