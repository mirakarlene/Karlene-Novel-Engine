using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    private string browserDirectory = "";
    private IReadOnlyList<ProjectTreeNode> browserTree = [];
    private bool updatingBrowserTree;

    private void ApplyBrowserView()
    {
        bool table = settings.ProjectBrowserView == "table", visible = FileBrowserPanel.Visibility == Visibility.Visible;
        FilesDataGrid.Visibility = visible && table ? Visibility.Visible : Visibility.Collapsed;
        FileTiles.Visibility = visible && !table ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowserSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not Selector selector || e.Source != sender) return;
        var other = ReferenceEquals(selector, FileTiles) ? (Selector)FilesDataGrid : FileTiles;
        if (!ReferenceEquals(other.SelectedItem, selector.SelectedItem)) other.SelectedItem = selector.SelectedItem;
    }

    private void ShowFileBrowser()
    {
        FileBrowserPanel.Visibility = Visibility.Visible;
        EditorWorkspace.Visibility = Visibility.Collapsed;
        foreach (var editor in OpenEditors) editor.Visibility = Visibility.Collapsed;
        ApplyBrowserView();
    }

    private void RefreshBrowserEntries(string? selectedPath = null)
    {
        if (currentProject is not { } project) return;
        while (browserDirectory.Length > 0 && (!project.Directories.Contains(Path.Combine(project.Path, browserDirectory), StringComparer.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(project.Path, browserDirectory))))
            browserDirectory = Path.GetDirectoryName(browserDirectory) ?? "";
        IEnumerable<ProjectTreeNode> children = browserTree;
        if (browserDirectory.Length > 0)
            children = FlattenTree(browserTree).FirstOrDefault(node => node.File == null && node.RelativePath.Equals(browserDirectory, StringComparison.OrdinalIgnoreCase))?.Children ?? [];
        selectedPath ??= (FileTiles.SelectedItem as ProjectBrowserEntry)?.RelativePath ?? (FilesDataGrid.SelectedItem as ProjectBrowserEntry)?.RelativePath;
        var entries = children.Select(node => new ProjectBrowserEntry(node.Name, node.RelativePath, node.File)).ToArray();
        FilesDataGrid.ItemsSource = FileTiles.ItemsSource = entries;
        var selected = entries.FirstOrDefault(entry => entry.RelativePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        FilesDataGrid.SelectedItem = FileTiles.SelectedItem = selected;
        FileBrowserPath.Text = Path.Combine(project.Path, browserDirectory);
        FileBrowserBackButton.Visibility = browserDirectory.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyBrowserView();
    }

    private void NavigateBrowserDirectory(string relative, string? selectedPath = null, bool showBrowser = true)
    {
        if (currentProject is not { } project) return;
        if (relative.Length > 0)
        {
            string full = PathSafety.ResolveRelative(project.Path, relative);
            if (!project.Directories.Contains(full, StringComparer.OrdinalIgnoreCase) || !Directory.Exists(full))
                throw new DirectoryNotFoundException("Папка отсутствует в текущем проекте: " + relative);
            relative = Path.GetRelativePath(project.Path, full);
        }
        browserDirectory = relative;
        RefreshBrowserEntries(selectedPath);
        updatingBrowserTree = true;
        try
        {
            string selection = selectedPath ?? relative;
            foreach (var node in FlattenTree(browserTree))
            {
                node.IsSelected = node.RelativePath.Equals(selection, StringComparison.OrdinalIgnoreCase);
                if (selection.StartsWith(node.RelativePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) node.IsExpanded = true;
            }
            selectedTreePath = selection;
        }
        finally { updatingBrowserTree = false; }
        if (showBrowser) ShowFileBrowser();
    }

    private void FileBrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (browserDirectory.Length == 0) return;
        string previous = browserDirectory;
        TryNavigateBrowserDirectory(Path.GetDirectoryName(browserDirectory) ?? "", previous);
    }

    private void TryNavigateBrowserDirectory(string relative, string? selectedPath = null, bool showBrowser = true)
    {
        try { NavigateBrowserDirectory(relative, selectedPath, showBrowser); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { OperationStatus.Text = "Не удалось открыть папку: " + error.Message; }
    }

    private async void FileBrowser_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.System && e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt)
        { FileBrowserBack_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && (sender as Selector)?.SelectedItem is ProjectBrowserEntry entry)
        { e.Handled = true; await OpenBrowserEntryAsync(entry); }
    }

    private async Task OpenBrowserEntryAsync(ProjectBrowserEntry entry)
    {
        if (entry.IsDirectory) { TryNavigateBrowserDirectory(entry.RelativePath); return; }
        if (entry.File is { } file) await PerformFileActionAsync(file, "Редактировать");
    }

    private void FileTiles_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        FileTiles.SelectedItem = BrowserItemAtDoubleClick(FileTiles, e.OriginalSource as DependencyObject) as ProjectBrowserEntry;
    private void FileTiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    { if (FileTiles.SelectedItem is not ProjectBrowserEntry { File: not null }) e.Handled = true; }
    private async void TileFileAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Header: string action, Parent: ContextMenu { PlacementTarget: ListBox { SelectedItem: ProjectBrowserEntry { File: { } file } } } })
            await PerformFileActionAsync(file, action);
    }

    // Resolve the hit row rather than the last selection. A blank/header click must never reopen it.
    internal static ProjectFile? FileAtDoubleClick(object sender, DependencyObject? originalSource)
        => BrowserItemAtDoubleClick(sender, originalSource) switch
        { ProjectFile file => file, ProjectBrowserEntry entry => entry.File, ProjectTreeNode node => node.File, _ => null };

    internal static object? BrowserItemAtDoubleClick(object sender, DependencyObject? originalSource)
    {
        for (DependencyObject? current = originalSource; current != null; current = InputParent(current))
        {
            if (current is ComboBox or ButtonBase or Thumb or TextBoxBase) return null;
            if (sender is DataGrid grid && current is DataGridRow row)
                return ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid) ? row.Item : null;
            if (sender is ListBox list && current is ListBoxItem tile)
                return ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(tile), list) ? tile.Content : null;
            if (sender is TreeView tree && current is TreeViewItem item)
            {
                ItemsControl? owner = ItemsControl.ItemsControlFromItemContainer(item);
                while (owner is TreeViewItem parent) owner = ItemsControl.ItemsControlFromItemContainer(parent);
                return ReferenceEquals(owner, tree) ? item.Header : null;
            }
            if (ReferenceEquals(current, sender)) break;
        }
        return null;
    }

    private static DependencyObject? InputParent(DependencyObject current) => current is Visual or Visual3D
        ? VisualTreeHelper.GetParent(current) : current is FrameworkContentElement text ? text.Parent : LogicalTreeHelper.GetParent(current);
}
