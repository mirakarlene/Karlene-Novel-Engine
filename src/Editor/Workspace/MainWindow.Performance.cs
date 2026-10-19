using System.Windows;

namespace KNE;

public partial class MainWindow
{
    private async void ProjectParameters_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        await RunOperationAsync(async () =>
        {
            var window = new ProjectParametersWindow(project) { Owner = this }; EditorTheme.Apply(window, settings.Theme);
            bool? saved = window.ShowDialog();
            if (window.DeletionRequested) await DeleteCurrentProjectAsync(project);
            else if (saved == true) { RefreshOpenGamePerformance(project.Path); await OpenProjectAsync(project.Path); }
        }, "Параметры проекта…");
    }
    private async void SceneParameters_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        await RunOperationAsync(async () =>
        {
            var window = new SceneParametersWindow(project) { Owner = this }; EditorTheme.Apply(window, settings.Theme);
            if (window.ShowDialog() == true) { RefreshOpenGamePerformance(project.Path); await OpenProjectAsync(project.Path); }
        }, "Параметры сцены…");
    }
    private static void RefreshOpenGamePerformance(string path)
    {
        if (Application.Current == null) return;
        foreach (var game in Application.Current.Windows.OfType<GameWindow>().Where(game => game.ProjectPath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray()) game.ReloadPerformanceSettings();
    }
    private async Task DeleteCurrentProjectAsync(ProjectInfo project)
    {
        // The modal dialog already required explicit confirmation and 72 further clicks.
        if (Application.Current != null)
        {
            foreach (var game in Application.Current.Windows.OfType<GameWindow>().Where(game => game.ProjectPath.Equals(project.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) await game.CloseForProjectDeletionAsync();
            foreach (var testing in Application.Current.Windows.OfType<SceneTestingWindow>().Where(window => window.ProjectPath.Equals(project.Path, StringComparison.OrdinalIgnoreCase)).ToArray()) await testing.CloseForProjectDeletionAsync();
        }
        monitor?.Dispose(); monitor = null; monitoredPath = null;
        try { await ProjectMaintenance.DeleteAsync(project, lifetime.Token); }
        catch { await OpenProjectAsync(project.Path); throw; }
        ClearEditorTabs(); currentProject = null;
        FilesDataGrid.ItemsSource = null; ProjectFilesTree.ItemsSource = null;
        pendingRefresh = false; settings.ForgetProject(project.Path);
        await settingsStore.SaveAsync(settings); await RefreshStoredProjectsAsync();
        ShowPanel(WelcomePanel); OperationStatus.Text = "Проект удалён.";
    }
}
