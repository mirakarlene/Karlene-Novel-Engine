using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectSnapshotsTests
{
    [Fact]
    public async Task FailedRestoreAtRetentionLimitKeepsChosenVersionAndEveryPreviousArchive()
    {
        using var project = new TempProject(); var store = new ProjectSnapshots(Path.Combine(project.Root, "copies"));
        string first = project.Write("a.krl", "first"), second = project.Write("b.krl", "second");
        for (int i = 0; i < 5; i++) { File.WriteAllText(first, "version " + i); await store.CreateAsync(project.ProjectPath, "history", 5, [first, second]); }
        var before = store.List(project.ProjectPath, "history"); var oldest = before[^1]; File.WriteAllText(first, "current");
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.RestoreAsync(project.ProjectPath, oldest, keep: 5));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file error.");
        }
        Assert.Equal("current", File.ReadAllText(first)); Assert.Equal("second", File.ReadAllText(second));
        Assert.Equal(before.Select(item => item.Archive), store.List(project.ProjectPath, "history").Select(item => item.Archive)); Assert.True(File.Exists(oldest.Archive));
    }

    [Fact]
    public async Task FullBackupIncludesResourcesAndHiddenFiles_HistoryOnlyIncludesOpenFiles_AndKeepsIndependentLimits()
    {
        using var project = new TempProject(); using var other = new TempProject();
        string first = project.Write("game/scenes/a.krl", "function a() end"), second = project.Write("game/scenes/b.krl", "function b() end");
        project.Write(".private/test.txt", "hidden"); Directory.CreateDirectory(Path.Combine(project.ProjectPath, "empty")); byte[] resource = [0, 255, 12, 37]; string binary = project.Write("game/images/image.bin", ""); File.WriteAllBytes(binary, resource);
        var store = new ProjectSnapshots(Path.Combine(project.Root, "copies"));
        var full = await store.CreateAsync(project.ProjectPath, "backup", 3); Assert.NotNull(full);
        using (var archive = ZipFile.OpenRead(full.Archive))
        {
            Assert.NotNull(archive.GetEntry("project/project.json")); Assert.NotNull(archive.GetEntry("project/.private/test.txt")); Assert.NotNull(archive.GetEntry("project/empty/"));
            using var stream = archive.GetEntry("project/game/images/image.bin")!.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory); Assert.Equal(resource, memory.ToArray());
        }
        var history = await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]);
        Assert.Equal(new[] { "game/scenes/a.krl", "game/scenes/b.krl" }, history!.Manifest.Files.Select(file => file.Path));
        Assert.Equal(history.Archive, (await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]))!.Archive);
        await store.CreateAsync(other.ProjectPath, "history", 3, [Path.Combine(other.ProjectPath, "game/scenes/main.krl")]);
        var created = new List<string>();
        for (int i = 0; i < 5; i++) { File.WriteAllText(first, "version " + i); created.Add((await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]))!.Archive); }
        Assert.Equal(created.TakeLast(3).Reverse(), store.List(project.ProjectPath, "history").Select(item => item.Archive));
        Assert.Single(store.List(project.ProjectPath, "backup")); Assert.Single(store.List(other.ProjectPath, "history"));
        Assert.Equal("hidden", File.ReadAllText(Path.Combine(project.ProjectPath, ".private/test.txt")));
    }

    [Fact]
    public async Task RestoreOldestVersionStagesItBeforePruning_SavesCurrentFilesAndRestoresOnlyListedFiles()
    {
        using var project = new TempProject(); var store = new ProjectSnapshots(Path.Combine(project.Root, "copies"));
        string first = project.Write("game/scenes/a.krl", "old a"), second = project.Write("game/scenes/b.krl", "old b"), untouched = project.Write("untouched.txt", "keep");
        var oldest = (await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]))!;
        File.WriteAllText(first, "middle"); await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]);
        File.WriteAllText(first, "latest"); await store.CreateAsync(project.ProjectPath, "history", 3, [first, second]);
        File.WriteAllText(first, "before restore"); File.WriteAllText(second, "before b");
        await store.RestoreAsync(project.ProjectPath, oldest, keep: 3);
        Assert.Equal("old a", File.ReadAllText(first)); Assert.Equal("old b", File.ReadAllText(second)); Assert.Equal("keep", File.ReadAllText(untouched));
        Assert.False(File.Exists(oldest.Archive)); var current = store.List(project.ProjectPath, "history")[0]; Assert.NotEqual(oldest.Archive, current.Archive);
        await store.RestoreAsync(project.ProjectPath, current, keep: 3);
        Assert.Equal("before restore", File.ReadAllText(first)); Assert.Equal("before b", File.ReadAllText(second)); Assert.Equal(3, store.List(project.ProjectPath, "history").Count);
    }

    [Fact]
    public async Task DamagedPayloadOrTraversalCannotOverwriteProjectOrEscapeStorage()
    {
        using var project = new TempProject(); var store = new ProjectSnapshots(Path.Combine(project.Root, "copies")); string file = project.Write("one.krl", "old");
        var snapshot = (await store.CreateAsync(project.ProjectPath, "history", 5, [file]))!;
        using (var zip = ZipFile.Open(snapshot.Archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("project/one.krl")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("project/one.krl").Open()); writer.Write("BAD");
        }
        File.WriteAllText(file, "current"); await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(project.ProjectPath, snapshot)); Assert.Equal("current", File.ReadAllText(file));
        using (var zip = ZipFile.Open(snapshot.Archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("snapshot.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("snapshot.json").Open()); writer.Write(JsonSerializer.Serialize(snapshot.Manifest with { Files = [snapshot.Manifest.Files[0] with { Path = "../outside.txt" }] }));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(project.ProjectPath, snapshot)); Assert.Equal("current", File.ReadAllText(file)); Assert.False(File.Exists(Path.Combine(project.Root, "outside.txt")));
    }

    [Fact]
    public async Task FailedSecondReplacementRollsBackFirst_AndCancellationLeavesNoPartialArchives()
    {
        using var project = new TempProject(); var store = new ProjectSnapshots(Path.Combine(project.Root, "copies"));
        string first = project.Write("a.krl", "old a"), second = project.Write("b.krl", "old b"); var snapshot = (await store.CreateAsync(project.ProjectPath, "history", 5, [first, second]))!;
        File.WriteAllText(first, "current a"); File.WriteAllText(second, "current b");
        using (var locked = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.RestoreAsync(project.ProjectPath, snapshot));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file error.");
        }
        Assert.Equal("current a", File.ReadAllText(first)); Assert.Equal("current b", File.ReadAllText(second));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(project.ProjectPath, "backup", 5, token: cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Root, "copies"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ProjectInternalStorageAndExternalFilesAreRejected_AndProjectsAreNeverMoved()
    {
        using var project = new TempProject(); string file = project.Write("one.krl", "keep"), external = Path.Combine(project.Root, "external.krl"); File.WriteAllText(external, "external");
        await Assert.ThrowsAsync<IOException>(() => new ProjectSnapshots(Path.Combine(project.ProjectPath, "backups")).CreateAsync(project.ProjectPath, "backup", 5));
        var store = new ProjectSnapshots(Path.Combine(project.Root, "copies"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(project.ProjectPath, "history", 5, [external]));
        var snapshot = await store.CreateAsync(project.ProjectPath, "backup", 5);
        Assert.NotNull(snapshot); Assert.Equal("keep", File.ReadAllText(file)); Assert.Equal("external", File.ReadAllText(external)); Assert.True(File.Exists(Path.Combine(project.ProjectPath, "project.json")));
    }
}
