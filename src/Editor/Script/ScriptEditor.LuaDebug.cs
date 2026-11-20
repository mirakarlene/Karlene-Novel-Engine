using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    private void SetVisualLuaActions(bool visualLua)
    {
        RunButton.Visibility = TestSceneButton.Visibility = MainSceneButton.Visibility = ExportButton.Visibility =
            visualLua ? Visibility.Collapsed : Visibility.Visible;
        DebugLuaButton.Visibility = visualLua && Document?.Graph?.IsVisualLua == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DebugLua_Click(object sender, RoutedEventArgs e)
    {
        if (Document is not { Graph.IsVisualLua: true } document) return;
        DebugLuaButton.IsEnabled = false;
        try
        {
            ApplyProperties();
            string source = document.Graph!.Compile();
            string relative = Path.GetRelativePath(projectRoot, document.FilePath).Replace('\\', '/');
            EditorStatus.Text = "Подготовка временной копии для отладки…";
            var report = await Task.Run(() => VisualLuaDebug.RunAsync(projectRoot, relative, source));
            if (!IsLoaded || !ReferenceEquals(Document, document)) return;
            EditorStatus.Text = report.Status;
            var log = new TextBox
            {
                Text = report.Status + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, report.Output),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12)
            };
            var window = new Window
            {
                Owner = Window.GetWindow(this), Title = "Дебаг · " + Path.GetFileName(document.FilePath),
                Width = 650, Height = 360, MinWidth = 400, MinHeight = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = log
            };
            window.ShowDialog();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(Document, document)) EditorStatus.Text = "Дебаг: " + error.Message;
        }
        finally { DebugLuaButton.IsEnabled = true; }
    }
}
