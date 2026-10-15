using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    private void SceneEditor_BlockLibraryRequested(object? sender, EventArgs e)
    {
        if (busy || closed || SceneEditor.Document?.Graph == null) return;
        BlockLibraryOverlay.Visibility = Visibility.Visible; ProjectPanel.IsEnabled = false;
        LibraryView.Open(SceneEditor.CanAddBlock);
    }
    private void CloseBlockLibrary()
    {
        BlockLibraryOverlay.Visibility = Visibility.Collapsed;
        if (!closed) { ProjectPanel.IsEnabled = !busy; SceneEditor.FocusAddBlock(); }
    }
    private void Library_BlockSelected(object? sender, BlockDefinition entry)
    {
        try { SceneEditor.AddBlock(entry.Kind, entry.Group); CloseBlockLibrary(); }
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
