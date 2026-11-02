using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    internal async Task<bool> ExecuteWorkspaceActionAsync(string action)
    {
        if (busy || closed || SessionClock.IsOnBreak) return false;
        if (currentProject == null && action is not ("file.open" or "general.settings" or "general.help")) return false;
        if (action == "general.settings") { BtnSettings_Click(this, new RoutedEventArgs()); return true; }
        if (action == "general.help") { BtnHelp_Click(this, new RoutedEventArgs()); return true; }
        if (action == "file.open") { BtnSelectProject_Click(this, new RoutedEventArgs()); return true; }
        if (ProjectPanel.Visibility != Visibility.Visible) return false;
        switch (action)
        {
            case "file.save_all": await RunOperationAsync(SaveAllEditorsAsync, "Сохранение всех файлов…"); break;
            case "file.new": BtnCreateFile_Click(this, new RoutedEventArgs()); break;
            case "file.close":
                ((DocumentTabs.SelectedItem as TabItem)?.Header as StackPanel)?.Children.OfType<Button>().LastOrDefault()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case "navigation.files": BtnFileList_Click(this, new RoutedEventArgs()); break;
            case "navigation.scenes": await RunOperationAsync(OpenSceneNavigatorAsync, "Навигатор сцен"); break;
            case "navigation.refresh": BtnRefresh_Click(this, new RoutedEventArgs()); break;
            case "blocks.library": SceneEditor_BlockLibraryRequested(this, EventArgs.Empty); break;
            case "debug.run": BtnRunProject_Click(this, new RoutedEventArgs()); break;
            case "debug.test": OpenTestingWindow(sceneTest: true); break;
            case "debug.analyze": BtnAnalyzeProject_Click(this, new RoutedEventArgs()); break;
            case "debug.build": BtnBuildRelease_Click(this, new RoutedEventArgs()); break;
            default: return false;
        }
        return true;
    }
}
