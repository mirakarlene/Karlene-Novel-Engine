using System.Windows;
using System.Windows.Controls;

namespace KNE;

internal sealed class VersionHistoryWindow : Window
{
    internal ListBox Versions { get; } = new() { DisplayMemberPath = "Label", MinWidth = 230, Margin = new(0, 0, 14, 0) };
    internal ListBox Files { get; } = new();
    internal Button Restore { get; } = new() { Content = "Откатить к выбранной версии", Padding = new(12, 7, 12, 7), IsEnabled = false };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Func<ProjectSnapshot, Task<bool>> restore;
    private bool restoring;
    internal VersionHistoryWindow(string title, IReadOnlyList<ProjectSnapshot> versions, Func<ProjectSnapshot, Task<bool>> restore)
    {
        this.restore = restore;
        Title = "История автосохранения · " + title; Width = 850; Height = 550; MinWidth = 620; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closing += (_, args) => { if (restoring) args.Cancel = true; };
        var root = new DockPanel { Margin = new(18) }; var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); footer.Children.Add(status);
        var buttons = new WrapPanel(); buttons.Children.Add(Restore); var close = new Button { Content = "Закрыть", Padding = new(12, 7, 12, 7), Margin = new(8, 0, 0, 0), IsCancel = true }; close.Click += (_, _) => Close(); buttons.Children.Add(close); footer.Children.Add(buttons); root.Children.Add(footer);
        var hint = new TextBlock { Text = "Версия содержит файлы, открытые во вкладках при автосохранении. Откат заменяет только перечисленные файлы.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) }; DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(1.2, GridUnitType.Star) }); columns.Children.Add(Versions); Grid.SetColumn(Files, 1); columns.Children.Add(Files); root.Children.Add(columns); Content = root;
        Versions.ItemsSource = versions; status.Text = versions.Count == 0 ? "История пока пуста. Версии появятся после автосохранения открытых файлов." : "Выберите версию для просмотра файлов.";
        Versions.SelectionChanged += (_, _) => { Restore.IsEnabled = Versions.SelectedItem is ProjectSnapshot; Files.ItemsSource = (Versions.SelectedItem as ProjectSnapshot)?.Manifest.Files.Select(file => file.Path).ToArray(); };
        Restore.Click += async (_, _) =>
        {
            try { if (await RestoreSelectedAsync()) { status.Text = "Версия восстановлена."; DialogResult = true; } }
            catch (Exception error) { status.Text = "Откат не выполнен: " + error.Message; }
        };
    }
    internal async Task<bool> RestoreSelectedAsync()
    {
        if (restoring || Versions.SelectedItem is not ProjectSnapshot selected) return false;
        restoring = true; Restore.IsEnabled = false;
        try { return await restore(selected); }
        finally { restoring = false; Restore.IsEnabled = Versions.SelectedItem != null; }
    }
}
