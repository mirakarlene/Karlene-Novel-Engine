using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class MainWindow
{
    private ScriptEditor ActiveEditor => (DocumentTabs?.SelectedItem as TabItem)?.Tag as ScriptEditor ?? SceneEditor;
    private IEnumerable<ScriptEditor> OpenEditors => DocumentTabs.Items.Cast<TabItem>().Select(tab => (ScriptEditor)tab.Tag);
    private readonly HashSet<string> expandedTreePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ScriptEditor, EventHandler> tabStateHandlers = [];
    private string? selectedTreePath;

    private bool IsDocumentOpen(string path) => OpenEditors.Any(editor =>
        editor.Document?.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true);

    private async Task OpenScriptAsync(string path)
    {
        if (currentProject == null) return;
        path = PathSafety.EnsureInside(currentProject.Path, path);
        var existing = DocumentTabs.Items.Cast<TabItem>().FirstOrDefault(tab =>
            ((ScriptEditor)tab.Tag).Document?.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
        if (existing != null) { DocumentTabs.SelectedItem = existing; ShowActiveEditor(); return; }

        var editor = OpenEditors.Any() ? CreateTabEditor() : SceneEditor;
        try { await editor.OpenAsync(currentProject.Path, path); }
        catch { if (editor != SceneEditor) EditorWorkspace.Children.Remove(editor); throw; }
        var tab = new TabItem { Tag = editor, ToolTip = Path.GetRelativePath(currentProject.Path, path) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        void UpdateHeader() => title.Text = Path.GetFileName(path) + (editor.HasUnsavedChanges ? " *" : "");
        EventHandler changed = (_, _) => UpdateHeader();
        editor.DocumentStateChanged += changed;
        tabStateHandlers.Add(editor, changed);
        var close = new Button { Content = "×", ToolTip = "Закрыть «" + Path.GetFileName(path) + "»", Padding = new Thickness(5, 0, 5, 0) };
        close.Click += async (_, args) =>
        {
            args.Handled = true;
            await RunOperationAsync(() => CloseEditorTabAsync(editor), "Закрытие вкладки…");
        };
        header.Children.Add(title); header.Children.Add(close); tab.Header = header; UpdateHeader();
        DocumentTabs.Items.Add(tab); DocumentTabs.SelectedItem = tab;
        ShowActiveEditor();
    }

    private ScriptEditor CreateTabEditor()
    {
        var editor = new ScriptEditor();
        editor.BlockLibraryRequested += SceneEditor_BlockLibraryRequested;
        editor.TestSceneRequested += SceneEditor_TestSceneRequested;
        editor.ExternalEditorRequested += SceneEditor_ExternalEditorRequested;
        editor.RunRequested += SceneEditor_RunRequested;
        editor.MainSceneRequested += SceneEditor_MainSceneRequested;
        editor.CloseRequested += SceneEditor_CloseRequested;
        editor.ProjectScriptRequested += SceneEditor_ProjectScriptRequested;
        EditorWorkspace.Children.Add(editor);
        return editor;
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == DocumentTabs) ShowActiveEditor();
    }

    private void ShowActiveEditor()
    {
        if (EditorWorkspace == null) return;
        bool any = DocumentTabs.Items.Count > 0;
        DocumentTabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        foreach (var editor in EditorWorkspace.Children.OfType<ScriptEditor>())
            editor.Visibility = any && editor == ActiveEditor ? Visibility.Visible : Visibility.Collapsed;
        EditorWorkspace.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        FilesDataGrid.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task CloseEditorTabAsync(ScriptEditor editor)
    {
        var tab = DocumentTabs.Items.Cast<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, editor));
        if (tab == null || !await editor.CanLeaveAsync()) return;
        DocumentTabs.Items.Remove(tab);
        DetachTabState(editor);
        editor.Clear();
        if (editor != SceneEditor) EditorWorkspace.Children.Remove(editor);
        ShowActiveEditor();
    }

    private async Task<bool> CanLeaveAllAsync()
    {
        foreach (var editor in OpenEditors.ToArray()) if (!await editor.CanLeaveAsync()) return false;
        return true;
    }

    private async Task SaveAllEditorsAsync()
    {
        foreach (var editor in OpenEditors.ToArray()) await editor.SaveAsync();
    }

    private void ClearEditorTabs()
    {
        var editors = OpenEditors.ToArray();
        DocumentTabs.Items.Clear();
        foreach (var editor in editors)
        {
            DetachTabState(editor);
            editor.Clear();
            if (editor != SceneEditor) EditorWorkspace.Children.Remove(editor);
        }
        SceneEditor.Clear();
        ShowActiveEditor();
    }

    private void DetachTabState(ScriptEditor editor)
    {
        if (tabStateHandlers.Remove(editor, out var handler)) editor.DocumentStateChanged -= handler;
    }

    private static IEnumerable<ProjectTreeNode> FlattenTree(IEnumerable<ProjectTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenTree(node.Children)) yield return child;
        }
    }

    private void RememberTreeState()
    {
        if (ProjectFilesTree.ItemsSource is not IEnumerable<ProjectTreeNode> roots) return;
        foreach (var node in FlattenTree(roots))
        {
            if (node.IsExpanded) expandedTreePaths.Add(node.RelativePath);
            else expandedTreePaths.Remove(node.RelativePath);
            if (node.IsSelected) selectedTreePath = node.RelativePath;
        }
    }
}
