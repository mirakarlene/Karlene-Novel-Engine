using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

/// <summary>Grouped block picker hosted by the main window's modal overlay.</summary>
public partial class BlockLibraryView : UserControl
{
    private Func<ScriptNodeKind, bool> canAdd = _ => true;
    /// <summary>Raised when a selectable command is chosen.</summary>
    public event EventHandler<BlockDefinition>? BlockSelected;
    /// <summary>Raised when the picker is dismissed without a selection.</summary>
    public event EventHandler? CloseRequested;
    /// <summary>Builds the library controls.</summary>
    public BlockLibraryView() { InitializeComponent(); BlockGroups.ItemsSource = BlockLibrary.Groups; }
    /// <summary>Resets the picker and disables commands that cannot currently be added.</summary>
    public void Open(Func<ScriptNodeKind, bool> availability)
    {
        canAdd = availability; BlockSearch.Clear(); BlockGroups.SelectedIndex = 1; Render(); BlockSearch.Focus();
    }
    private void Group_Changed(object sender, SelectionChangedEventArgs e) => Render();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Render();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Render()
    {
        if (BlockCards == null || BlockGroups.SelectedItem is not BlockGroup group) return;
        BlockCards.Children.Clear();
        string search = BlockSearch.Text.Trim();
        foreach (var entry in BlockLibrary.Definitions.Where(entry => entry.Group == group.Id &&
            (entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            var color = (Color)ColorConverter.ConvertFromString(entry.Color);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            text.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
            var button = new Button { Tag = entry.Kind, Content = text, Width = 236, MinHeight = 130, Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(14), Background = Brushes.White, BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(0, 5, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, IsEnabled = canAdd(entry.Kind) };
            if (!button.IsEnabled) button.ToolTip = entry.Kind == ScriptNodeKind.Start ? "В схеме уже есть начало." : "Достигнут предел в 500 блоков.";
            button.Click += (_, _) => BlockSelected?.Invoke(this, entry);
            BlockCards.Children.Add(button);
        }
        EmptyLibraryText.Visibility = BlockCards.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
