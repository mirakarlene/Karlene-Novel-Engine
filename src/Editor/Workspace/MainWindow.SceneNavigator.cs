using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KNE;

public partial class MainWindow
{
    private sealed class SceneNavigatorTab;

    private void AddSceneNavigatorButton()
    {
        var button = new Button { Name = "SceneNavigatorButton", Content = "Навигатор сцен", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
        button.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = AppearanceButton });
        button.Click += async (_, _) => await RunOperationAsync(OpenSceneNavigatorAsync, "Навигатор сцен");
        PluginToolbar.Children.Add(button);
    }

    private async Task OpenSceneNavigatorAsync()
    {
        if (currentProject == null) return;
        var existing = DocumentTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Tag is SceneNavigatorTab);
        if (existing != null) { DocumentTabs.SelectedItem = existing; ShowActiveEditor(); return; }
        var definition = new SceneNavigatorTab(); var view = new SceneNavigatorView { Tag = definition };
        var tab = new TabItem { Tag = definition, ToolTip = "Связи сценариев .krl и .knegraph · только просмотр" };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "Навигатор сцен", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0), ToolTip = "Закрыть навигатор сцен" };
        close.Click += (_, args) =>
        {
            args.Handled = true; DocumentTabs.Items.Remove(tab); EditorWorkspace.Children.Remove(view); ShowActiveEditor();
        };
        header.Children.Add(close); tab.Header = header;
        view.SceneRequested += async (_, node) => await RunOperationAsync(() => OpenNavigatedSceneAsync(node), "Открытие сцены");
        view.RefreshRequested += async (_, _) => await RunOperationAsync(() => RefreshSceneNavigatorAsync(view), "Обновление навигатора сцен");
        EditorWorkspace.Children.Add(view); DocumentTabs.Items.Add(tab); DocumentTabs.SelectedItem = tab; ShowActiveEditor();
        try { await RefreshSceneNavigatorAsync(view); }
        catch { DocumentTabs.Items.Remove(tab); EditorWorkspace.Children.Remove(view); ShowActiveEditor(); throw; }
    }

    private async Task OpenNavigatedSceneAsync(SceneNavigationNode node)
    {
        if (currentProject == null) return;
        await OpenScriptAsync(PathSafety.ResolveRelative(currentProject.Path, node.File));
        ActiveEditor.NavigateToSceneLine(node.Line);
    }

    private async Task RefreshSceneNavigatorAsync(SceneNavigatorView view)
    {
        if (currentProject is not { } project) return;
        var sources = OpenEditors.Where(editor => editor.Document != null).ToDictionary(editor => editor.Document!.FilePath,
            editor => editor.CaptureNavigationSource(), StringComparer.OrdinalIgnoreCase);
        var index = await SceneNavigationIndex.BuildAsync(project, sources, lifetime.Token);
        if (currentProject?.Path == project.Path && EditorWorkspace.Children.Contains(view)) view.Present(index);
    }
}
