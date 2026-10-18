using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    private void SceneEditor_BlockLibraryRequested(object? sender, EventArgs e)
    {
        if (busy || closed || ActiveEditor.Document?.Graph == null) return;
        BlockLibraryOverlay.Visibility = Visibility.Visible; ProjectPanel.IsEnabled = false;
        LibraryView.Open(ActiveEditor.CanAddBlock);
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
            ActiveEditor.AddBlock(entry.Kind, entry.Group); CloseBlockLibrary();
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
