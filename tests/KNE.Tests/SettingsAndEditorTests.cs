using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class SettingsAndEditorTests
{
    [Fact]
    public async Task Settings_LegacyMigration_ReadsEditorAndDoesNotModifyLegacyFile()
    {
        using var fixture = new TempProject();
        string legacy = Path.Combine(fixture.Root, "legacy.config");
        const string xml = "<configuration><appSettings><add key='EditorPath' value='C:\\Editor\\editor.exe'/></appSettings></configuration>";
        File.WriteAllText(legacy, xml);
        var store = new UserSettingsStore(Path.Combine(fixture.Root, "profile", "settings.json"), legacy);
        Assert.Equal("C:\\Editor\\editor.exe", (await store.LoadAsync()).EditorPath);
        Assert.Equal(xml, File.ReadAllText(legacy));
        Assert.False(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task Settings_SaveReloadAndReplace_PersistOutsideApplicationDirectory()
    {
        using var fixture = new TempProject();
        var store = new UserSettingsStore(Path.Combine(fixture.Root, "profile", "settings.json"));
        Assert.Equal("", (await store.LoadAsync()).EditorPath);
        await store.SaveAsync(new UserSettings { EditorPath = "C:\\Редактор с пробелами\\editor.exe" });
        Assert.Equal("C:\\Редактор с пробелами\\editor.exe", (await new UserSettingsStore(store.SettingsPath).LoadAsync()).EditorPath);
        await store.SaveAsync(new UserSettings { EditorPath = "" });
        Assert.Equal("", (await store.LoadAsync()).EditorPath);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!));
    }

    [Fact]
    public async Task Settings_InvalidJson_ReportsErrorWithoutOverwritingFile()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(path, "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => new UserSettingsStore(path).LoadAsync());
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task Settings_NullValuesAndDuplicateRecentProjects_AreNormalized()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(path, """{"EditorPath":null,"RecentProjects":[null,"","E:\\game","e:\\GAME"]}""");
        var settings = await new UserSettingsStore(path).LoadAsync();
        Assert.Equal("", settings.EditorPath);
        Assert.Equal(new[] { "E:\\game" }, settings.RecentProjects);
        settings.RememberProject(fixture.ProjectPath);
        Assert.Equal(fixture.ProjectPath, settings.RecentProjects[0]);
    }

    [Fact]
    public void Editor_SpacedFilePath_IsOneArgument()
    {
        var info = EditorLauncher.CreateStartInfo("", "E:\\Проекты\\Моя игра\\main.krl");
        Assert.Equal("notepad.exe", info.FileName);
        Assert.Equal(new[] { "E:\\Проекты\\Моя игра\\main.krl" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.Equal("", info.Arguments);
    }

    [Fact]
    public void Editor_MissingExecutable_ReportsError()
    {
        using var fixture = new TempProject();
        Assert.Throws<FileNotFoundException>(() => EditorLauncher.CreateStartInfo(Path.Combine(fixture.Root, "missing.exe"), "main.krl"));
    }
}
