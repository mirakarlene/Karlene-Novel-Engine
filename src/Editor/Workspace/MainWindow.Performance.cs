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
            if (window.ShowDialog() == true) await OpenProjectAsync(project.Path);
        }, "Параметры проекта…");
    }
    private async void SceneParameters_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        await RunOperationAsync(async () =>
        {
            var window = new SceneParametersWindow(project) { Owner = this }; EditorTheme.Apply(window, settings.Theme);
            if (window.ShowDialog() == true) await OpenProjectAsync(project.Path);
        }, "Параметры сцены…");
    }
}
