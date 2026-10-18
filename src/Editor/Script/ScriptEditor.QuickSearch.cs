using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    internal sealed record BlockSearchResult(string Id, string Label);

    internal IReadOnlyList<BlockSearchResult> SearchGraphBlocks(string query)
    {
        query = query.Trim();
        return Document?.Graph?.Nodes.Where(node => (node.Title + " " + node.Id + " " + Summary(node))
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(node => new BlockSearchResult(node.Id, node.Title + " · " + node.Id + " · " + Summary(node).ReplaceLineEndings(" ")))
            .ToArray() ?? [];
    }

    internal void OpenQuickSearch()
    {
        if (Document?.Graph == null || GraphMode.IsChecked != true) return;
        ApplyProperties();
        CancelPortDrag(); FinishMarquee(); pendingConnection = null;
        QuickSearchOverlay.Visibility = Visibility.Visible;
        QuickSearchInput.Text = "";
        RefreshQuickSearch();
        QuickSearchInput.Focus(); Keyboard.Focus(QuickSearchInput);
    }

    private void CloseQuickSearch()
    {
        if (QuickSearchOverlay == null) return;
        QuickSearchOverlay.Visibility = Visibility.Collapsed;
    }

    private bool IsGraphSource(DependencyObject? source)
    {
        for (var target = source; target != null; target = VisualTreeHelper.GetParent(target))
            if (target == GraphScroll) return true;
        return false;
    }

    internal bool NavigateToBlock(string id)
    {
        ApplyProperties();
        var node = Document?.Graph?.Nodes.FirstOrDefault(node => node.Id == id);
        if (node == null) return false;
        SelectNode(id);
        CanvasPan.X = GraphScroll.ActualWidth / 2 - (node.X + NodeWidth(node) / 2) * CanvasScale.ScaleX;
        CanvasPan.Y = GraphScroll.ActualHeight / 2 - (node.Y + NodeHeight(node) / 2) * CanvasScale.ScaleY;
        CloseQuickSearch();
        GraphScroll.Focus(); Keyboard.Focus(GraphScroll);
        EditorStatus.Text = "Найден: " + node.Title + " · " + node.Id;
        return true;
    }

    private void RefreshQuickSearch()
    {
        if (QuickSearchResults == null) return;
        var results = SearchGraphBlocks(QuickSearchInput.Text);
        QuickSearchResults.ItemsSource = results;
        QuickSearchResults.SelectedIndex = results.Count > 0 ? 0 : -1;
        QuickSearchEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QuickSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshQuickSearch();
    private void ChooseSearchResult()
    {
        if (QuickSearchResults.SelectedItem is BlockSearchResult result) Guard(() => NavigateToBlock(result.Id));
    }
    private void QuickSearchResults_DoubleClick(object sender, MouseButtonEventArgs e) { ChooseSearchResult(); e.Handled = true; }
    private void QuickSearchBackdrop_Click(object sender, MouseButtonEventArgs e) { CloseQuickSearch(); GraphScroll.Focus(); e.Handled = true; }
    private void QuickSearchPanel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void HandleQuickSearchKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseQuickSearch(); GraphScroll.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { ChooseSearchResult(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down)
        {
            if (QuickSearchResults.Items.Count > 0)
            {
                QuickSearchResults.SelectedIndex = Math.Clamp(QuickSearchResults.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, QuickSearchResults.Items.Count - 1);
                QuickSearchResults.ScrollIntoView(QuickSearchResults.SelectedItem);
            }
            e.Handled = true;
        }
    }
}
