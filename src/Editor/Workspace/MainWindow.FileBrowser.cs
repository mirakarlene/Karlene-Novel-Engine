using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace KNE;

public partial class MainWindow
{
    // Resolve the hit row rather than the last selection. A blank/header click must never reopen it.
    internal static ProjectFile? FileAtDoubleClick(object sender, DependencyObject? originalSource)
    {
        for (DependencyObject? current = originalSource; current != null; current = InputParent(current))
        {
            if (current is ComboBox or ButtonBase or Thumb or TextBoxBase) return null;
            if (sender is DataGrid grid && current is DataGridRow row)
                return ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid) ? row.Item as ProjectFile : null;
            if (sender is TreeView tree && current is TreeViewItem item)
            {
                ItemsControl? owner = ItemsControl.ItemsControlFromItemContainer(item);
                while (owner is TreeViewItem parent) owner = ItemsControl.ItemsControlFromItemContainer(parent);
                return ReferenceEquals(owner, tree) && item.Header is ProjectTreeNode node ? node.File : null;
            }
            if (ReferenceEquals(current, sender)) break;
        }
        return null;
    }

    private static DependencyObject? InputParent(DependencyObject current) => current is Visual or Visual3D
        ? VisualTreeHelper.GetParent(current) : current is FrameworkContentElement text ? text.Parent : LogicalTreeHelper.GetParent(current);
}
