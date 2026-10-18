using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectStorageTests
{
    [Theory]
    [InlineData(false, "projects")]
    [InlineData(true, "Мои новеллы")]
    public void Create_DefaultAndCustomNames_CreateOnlyTheChosenStorage(bool custom, string expectedName)
    {
        using var fixture = new StorageFixture();
        string existing = fixture.Project("Existing");
        string storage = custom ? ProjectStorage.Create(fixture.Root, expectedName) : ProjectStorage.Create(fixture.Root);
        Assert.Equal(Path.Combine(fixture.Root, expectedName), storage);
        Assert.True(Directory.Exists(storage));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage));
        Assert.True(File.Exists(Path.Combine(existing, "project.json")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    public void Create_InvalidFolderName_RejectsItWithoutCreatingStorage(string name)
    {
        using var fixture = new StorageFixture();
        Assert.Throws<ArgumentException>(() => ProjectStorage.Create(fixture.Root, name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void Create_MissingParent_DoesNotCreateTheParent()
    {
        using var fixture = new StorageFixture();
        string parent = Path.Combine(fixture.Root, "missing");
        Assert.Throws<DirectoryNotFoundException>(() => ProjectStorage.Create(parent));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public async Task Discover_OnlyDirectVisibleChildrenWithManifest_AreIncluded()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root);
        File.WriteAllText(Path.Combine(storage, "project.json"), "{}");
        string first = fixture.Project("projects/Alpha"), second = fixture.Project("projects/Zeta");
        fixture.Project("projects/Container/Nested");
        fixture.Project("projects/.hidden");
        Directory.CreateDirectory(Path.Combine(storage, "No manifest"));
        var settings = new UserSettings { ProjectsStoragePath = storage };

        var projects = await ProjectStorage.DiscoverAsync(settings);

        Assert.Equal(new[] { first, second }, projects.Select(project => project.Path));
        Assert.All(projects, project => Assert.True(project.IsAvailable));
        Assert.Empty(settings.RecentProjects);
    }

    [Fact]
    public async Task Discover_DeletedAndMovedProjects_KeepUnavailableOriginalEntriesAndDiscoverNewLocation()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root);
        string deleted = fixture.Project("projects/Deleted"), old = fixture.Project("projects/Old");
        var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [deleted, old] };
        Assert.All(await ProjectStorage.DiscoverAsync(settings), project => Assert.True(project.IsAvailable));
        File.Delete(Path.Combine(deleted, "project.json"));
        Directory.Delete(deleted);
        string moved = Path.Combine(storage, "Moved");
        PathSafety.EnsureInside(fixture.Root, old);
        PathSafety.EnsureInside(fixture.Root, moved);
        Directory.Move(old, moved);

        var projects = await ProjectStorage.DiscoverAsync(settings);

        Assert.Equal(new[] { new StoredProject(deleted, false), new StoredProject(old, false), new StoredProject(moved, true) }, projects);
        Assert.Equal(new[] { deleted, old }, settings.RecentProjects);
    }

    [Fact]
    public async Task Discover_ConfiguredStorage_DoesNotDiscoverOrMakeAvailableOutsideProjects()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root);
        string inside = fixture.Project("projects/Inside");
        string rememberedOutside = fixture.Project("Elsewhere/Remembered"), otherOutside = fixture.Project("Elsewhere/Unremembered");
        var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [rememberedOutside] };

        var projects = await ProjectStorage.DiscoverAsync(settings);

        Assert.Equal(new[] { new StoredProject(rememberedOutside, false), new StoredProject(inside, true) }, projects);
        Assert.DoesNotContain(projects, project => project.Path == otherOutside);
        Assert.False(ProjectStorage.IsAvailable(settings, rememberedOutside));
    }

    [Fact]
    public void IsAvailable_ConfiguredStorage_RequiresADirectChildManifest()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root);
        string direct = fixture.Project("projects/Direct"), nested = fixture.Project("projects/Direct/Nested");
        File.WriteAllText(Path.Combine(storage, "project.json"), "{}");
        var settings = new UserSettings { ProjectsStoragePath = storage + Path.DirectorySeparatorChar };
        Assert.True(ProjectStorage.IsAvailable(settings, direct + Path.DirectorySeparatorChar));
        Assert.False(ProjectStorage.IsAvailable(settings, nested));
        Assert.False(ProjectStorage.IsAvailable(settings, storage));
        Assert.False(ProjectStorage.IsAvailable(settings, Path.Combine(storage, "Missing")));
        Assert.False(ProjectStorage.IsAvailable(settings, ""));
    }

    [Fact]
    public async Task Discover_MissingStorage_RetainsRecentsWithoutCreatingDirectories()
    {
        using var fixture = new StorageFixture();
        string storage = Path.Combine(fixture.Root, "Missing storage"), recent = Path.Combine(storage, "Missing project");
        var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [recent] };
        Assert.Equal(new[] { new StoredProject(recent, false) }, await ProjectStorage.DiscoverAsync(settings));
        Assert.False(Directory.Exists(storage));
        Assert.Equal(new[] { recent }, settings.RecentProjects);
    }

    [Fact]
    public async Task Discover_UnconfiguredStorage_OnlyChecksRememberedProjects()
    {
        using var fixture = new StorageFixture();
        string remembered = fixture.Project("Remembered"), missing = Path.Combine(fixture.Root, "Missing");
        fixture.Project("Unremembered");
        Assert.Empty(await ProjectStorage.DiscoverAsync(new UserSettings()));
        var settings = new UserSettings { RecentProjects = [remembered, missing] };
        Assert.Equal(new[] { new StoredProject(remembered, true), new StoredProject(missing, false) }, await ProjectStorage.DiscoverAsync(settings));
    }

    [Fact]
    public async Task Discover_DuplicateRecentsAndDiscoveredProject_AppearOnceInRecentOrder()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root);
        string first = fixture.Project("projects/Zeta"), second = fixture.Project("projects/Alpha");
        var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [first, first.ToUpperInvariant(), second] };
        var projects = await ProjectStorage.DiscoverAsync(settings);
        Assert.Equal(new[] { new StoredProject(first, true), new StoredProject(second, true) }, projects);
        Assert.Equal(3, settings.RecentProjects.Count);
    }

    [Fact]
    public async Task Discover_PreCancelledToken_CancelsWithoutChangingRecents()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root), recent = fixture.Project("projects/Recent");
        var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [recent] };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectStorage.DiscoverAsync(settings, cancellation.Token));
        Assert.Equal(new[] { recent }, settings.RecentProjects);
    }

    [Fact]
    public async Task Settings_SaveReloadAndRemember_PreserveStorageAndUnavailableRecents()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root, "Мои проекты");
        string missing = Path.Combine(storage, "Удалённый проект"), latest = fixture.Project("Мои проекты/Latest");
        var settings = new UserSettings { EditorPath = "editor.exe", ProjectsStoragePath = storage, RecentProjects = [missing] };
        var store = new UserSettingsStore(Path.Combine(fixture.Root, "profile", "settings.json"));
        await store.SaveAsync(settings);
        var loaded = await new UserSettingsStore(store.SettingsPath).LoadAsync();
        Assert.Equal(storage, loaded.ProjectsStoragePath);
        Assert.Equal("editor.exe", loaded.EditorPath);
        Assert.Equal(new[] { missing }, loaded.RecentProjects);
        loaded.RememberProject(latest);
        loaded.EditorPath = "";
        await store.SaveAsync(loaded);
        var reloaded = await store.LoadAsync();
        Assert.Equal(storage, reloaded.ProjectsStoragePath);
        Assert.Equal("", reloaded.EditorPath);
        Assert.Equal(new[] { latest, missing }, reloaded.RecentProjects);
        Assert.Equal(new[] { new StoredProject(latest, true, reloaded.ProjectLastOpened[latest]), new StoredProject(missing, false) }, await ProjectStorage.DiscoverAsync(reloaded));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(store.SettingsPath)!));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ProjectsStoragePath\":null}")]
    public async Task Settings_LegacyOrNullStorage_DefaultsToUnconfigured(string json)
    {
        using var fixture = new StorageFixture();
        string path = Path.Combine(fixture.Root, "settings.json");
        File.WriteAllText(path, json);
        var loaded = await new UserSettingsStore(path).LoadAsync();
        Assert.Equal("", loaded.ProjectsStoragePath);
        Assert.Empty(await ProjectStorage.DiscoverAsync(loaded));
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MainWindow_MissingRecentProject_ShowsWarningAndKeepsWelcomeScreen()
    {
        using var fixture = new StorageFixture();
        string storage = ProjectStorage.Create(fixture.Root), missing = Path.Combine(storage, "Missing");
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                var settings = new UserSettings { ProjectsStoragePath = storage, RecentProjects = [missing] };
                typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, settings);
                var warning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.MissingProjectWarning = message => warning.TrySetResult(message);
                typeof(MainWindow).GetMethod("BtnSettings_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [new Button(), new RoutedEventArgs()]);
                Assert.Equal(storage, WorkspaceTabsTests.Find<TextBox>(window, "TxtProjectsStorage").Text);
                // Restore the welcome view without firing Loaded (which reads the real user's settings).
                WorkspaceTabsTests.Find<StackPanel>(window, "WelcomePanel").Visibility = Visibility.Visible;
                WorkspaceTabsTests.Find<StackPanel>(window, "SettingsPanel").Visibility = Visibility.Collapsed;
                typeof(MainWindow).GetMethod("OpenRecentProject_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [new Button { Tag = missing }, new RoutedEventArgs()]);
                string message = await warning.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("Проект не был обнаружен", message);
                Assert.Contains("Хранилище проектов", message);
                Assert.Equal(new StoredProject(missing, false), Assert.Single(WorkspaceTabsTests.Find<ItemsControl>(window, "RecentProjectsList").ItemsSource.Cast<StoredProject>()));
                Assert.Null(typeof(MainWindow).GetField("currentProject", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window));
                Assert.Equal(Visibility.Visible, WorkspaceTabsTests.Find<StackPanel>(window, "WelcomePanel").Visibility);
                Assert.Equal(Visibility.Collapsed, WorkspaceTabsTests.Find<Grid>(window, "ProjectPanel").Visibility);
                Assert.Equal(new[] { missing }, settings.RecentProjects);
                Assert.False(Directory.Exists(missing));
            }
            finally { window.Close(); }
        });
    }

    private sealed class StorageFixture : IDisposable
    {
        private static readonly string Scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kne-flows-triggers-tests"));
        public string Root { get; } = Path.Combine(Scratch, "storage-" + Guid.NewGuid().ToString("N"));
        public StorageFixture() => Directory.CreateDirectory(Root);
        public string Project(string relative)
        {
            string path = PathSafety.ResolveRelative(Root, relative);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "project.json"), "{}");
            return path;
        }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("storage-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
