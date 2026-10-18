using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private readonly DispatcherTimer storageTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool discoveringProjects;
    internal Action<string>? MissingProjectWarning { get; set; }

    private async Task RefreshStoredProjectsAsync()
    {
        if (closed || discoveringProjects) return;
        discoveringProjects = true;
        try
        {
            var projects = await ProjectStorage.DiscoverAsync(settings, lifetime.Token);
            if (!closed) RecentProjectsList.ItemsSource = projects;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) OperationStatus.Text = "Хранилище проектов: " + error.Message; }
        finally { discoveringProjects = false; }
    }

    private void BtnSelectStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите хранилище проектов" };
        if (dialog.ShowDialog(this) == true) TxtProjectsStorage.Text = dialog.FolderName;
    }

    private async Task<bool> EnsureProjectStorageAsync()
    {
        if (!string.IsNullOrWhiteSpace(settings.ProjectsStoragePath))
        {
            if (!Path.IsPathFullyQualified(settings.ProjectsStoragePath)) throw new ArgumentException("Укажите полный путь хранилища в настройках.");
            PathSafety.RejectLinks(settings.ProjectsStoragePath);
            Directory.CreateDirectory(settings.ProjectsStoragePath);
            return true;
        }
        var parent = new OpenFolderDialog { Title = "Выберите место для хранилища проектов KNE" };
        if (parent.ShowDialog(this) != true) return false;
        string name = Interaction.InputBox("Название папки хранилища проектов:", "Хранилище проектов", "projects");
        if (string.IsNullOrWhiteSpace(name)) return false;
        string storage = ProjectStorage.Create(parent.FolderName, name.Trim());
        var next = new UserSettings { EditorPath = settings.EditorPath, ProjectsStoragePath = storage, RecentProjects = settings.RecentProjects, HiddenProjects = settings.HiddenProjects, ProjectLastOpened = settings.ProjectLastOpened, Theme = settings.Theme };
        await settingsStore.SaveAsync(next); settings = next; TxtProjectsStorage.Text = storage;
        return true;
    }

    private async void ForgetRecentProject_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button { Tag: string path }) return;
        await RunOperationAsync(async () =>
        {
            settings.ForgetProject(path);
            await settingsStore.SaveAsync(settings);
            await RefreshStoredProjectsAsync();
        }, "Удаление записи из списка…");
    }
}
