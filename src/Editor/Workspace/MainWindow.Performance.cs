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
            if (window.ShowDialog() == true) { RefreshOpenGamePerformance(project.Path); await OpenProjectAsync(project.Path); }
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
}
