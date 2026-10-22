using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    private void SceneEditor_BlockLibraryRequested(object? sender, EventArgs e)
    {
        if (busy || closed || ActiveEditor.Document?.Graph == null) return;
        BlockLibraryOverlay.Visibility = Visibility.Visible; ProjectPanel.IsEnabled = false;
        LibraryView.Open(kind => ShaderLibrary.IsShader(kind) && ActiveEditor.Document?.Graph?.IsShader == false || ActiveEditor.CanAddBlock(kind));
    }
    private void CloseBlockLibrary()
    {
        BlockLibraryOverlay.Visibility = Visibility.Collapsed;
        if (!closed) { ProjectPanel.IsEnabled = !busy; ActiveEditor.FocusAddBlock(); }
    }
    private async void Library_BlockSelected(object? sender, BlockDefinition entry)
    {
        try
        {
            if (ShaderLibrary.IsShader(entry.Kind) && entry.Kind != ScriptNodeKind.ShaderContainer && ActiveEditor.Document?.Graph?.IsShader != true)
            {
                var warning = new Window { Owner = this, Title = "Шейдерная блок-схема", Width = 480, Height = 220, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var body = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
                body.Children.Add(new System.Windows.Controls.TextBlock { Text = "Блоки раздела «Шейдеры» нужно размещать в отдельных специальных файлах .kneshader. Создайте такой файл через «Новая блок-схема».", TextWrapping = TextWrapping.Wrap, FontSize = 16 });
                var close = new System.Windows.Controls.Button { Content = "Понятно", Margin = new Thickness(0, 20, 0, 0), IsDefault = true, IsCancel = true }; close.Click += (_, _) => warning.Close(); body.Children.Add(close); warning.Content = body; EditorTheme.Apply(warning, settings.Theme); warning.ShowDialog(); return;
            }
            ActiveEditor.AddBlock(entry); CloseBlockLibrary();
            Task recordUse = LibraryView.RecordUseAsync(entry);
            if (entry.Kind == ScriptNodeKind.Script) SceneEditor_ProjectScriptRequested(ActiveEditor, EventArgs.Empty);
            await recordUse;
        }
        catch (Exception error) { OperationStatus.Text = "Блок не добавлен: " + error.Message; }
    }
    private void Library_CloseRequested(object? sender, EventArgs e) => CloseBlockLibrary();
    private void BlockLibraryBackdrop_Click(object sender, MouseButtonEventArgs e) { CloseBlockLibrary(); e.Handled = true; }
    private void BlockLibraryPanel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && BlockLibraryOverlay.Visibility == Visibility.Visible) { CloseBlockLibrary(); e.Handled = true; }
    }
}
