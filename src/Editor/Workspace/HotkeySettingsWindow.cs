using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using Microsoft.Win32;

namespace KNE;

internal sealed class HotkeySettingsWindow : Window
{
    internal Dictionary<string, string> Profile { get; private set; }
    internal ListBox Categories { get; } = new() { ItemsSource = EditorHotkeys.Categories, SelectedIndex = 0, MinWidth = 155 };
    internal TextBox Search { get; } = new() { ToolTip = "Поиск по действиям во всех категориях", Margin = new(0, 0, 0, 10) };
    internal ListBox Actions { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private sealed record Row(EditorAction Action, string Keys);
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button confirm = new() { Content = "Подтвердить сочетание", IsEnabled = false };
    private EditorAction? recording;
    private string? pending;
    internal HotkeySettingsWindow(Dictionary<string, string> profile)
    {
        Profile = EditorHotkeys.Validate(profile); Title = "Горячие клавиши"; Width = 850; Height = 600; MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rowTemplate = new FrameworkElementFactory(typeof(DockPanel));
        var keysText = new FrameworkElementFactory(typeof(TextBlock)); keysText.SetValue(DockPanel.DockProperty, Dock.Right); keysText.SetValue(MarginProperty, new Thickness(14, 3, 4, 3)); keysText.SetBinding(TextBlock.TextProperty, new Binding("Keys")); rowTemplate.AppendChild(keysText);
        var titleText = new FrameworkElementFactory(typeof(TextBlock)); titleText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); titleText.SetValue(MarginProperty, new Thickness(4, 3, 0, 3)); titleText.SetBinding(TextBlock.TextProperty, new Binding("Action.Title")); rowTemplate.AppendChild(titleText);
        Actions.ItemTemplate = new DataTemplate { VisualTree = rowTemplate }; ScrollViewer.SetHorizontalScrollBarVisibility(Actions, ScrollBarVisibility.Disabled);
        var root = new DockPanel { Margin = new(18) };
        var footer = new WrapPanel { Margin = new(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        Add(footer, "Экспорт JSON…", async () => { var dialog = new SaveFileDialog { Filter = "Профиль клавиш (*.json)|*.json", FileName = "kne-hotkeys.json" }; if (dialog.ShowDialog(this) == true) await File.WriteAllTextAsync(dialog.FileName, EditorHotkeys.Export(Profile)); });
        Add(footer, "Импорт JSON…", async () => { var dialog = new OpenFileDialog { Filter = "Профиль клавиш (*.json)|*.json" }; if (dialog.ShowDialog(this) == true) { if (new FileInfo(dialog.FileName).Length > 32768) throw new InvalidDataException("Профиль больше 32 КБ."); Import(await File.ReadAllTextAsync(dialog.FileName)); } });
        Add(footer, "Сохранить", () => { DialogResult = true; return Task.CompletedTask; }); Add(footer, "Отмена", () => { Close(); return Task.CompletedTask; }); root.Children.Add(footer);
        Categories.Margin = new(0, 0, 16, 0); DockPanel.SetDock(Categories, Dock.Left); root.Children.Add(Categories);
        var right = new DockPanel(); var searchRow = new DockPanel(); DockPanel.SetDock(searchRow, Dock.Top); searchRow.Children.Add(new TextBlock { Text = "⌕", Margin = new(0, 4, 8, 0), FontSize = 18 }); searchRow.Children.Add(Search); right.Children.Add(searchRow);
        var capture = new StackPanel(); DockPanel.SetDock(capture, Dock.Bottom); capture.Children.Add(status); var buttons = new WrapPanel(); capture.Children.Add(buttons);
        confirm.Margin = new(0, 0, 8, 0); confirm.Click += (_, _) => Confirm(); buttons.Children.Add(confirm);
        Add(buttons, "Отменить запись", () => { CancelRecording(); return Task.CompletedTask; });
        Add(buttons, "Убрать сочетание", () => { if (recording != null) Assign(recording.Id, ""); return Task.CompletedTask; }); right.Children.Add(capture); right.Children.Add(Actions); root.Children.Add(right); Content = root;
        Categories.SelectionChanged += (_, _) => Refresh(); Search.TextChanged += (_, _) => Refresh();
        Actions.SelectionChanged += (_, _) => { if (Actions.SelectedItem is Row row) { recording = row.Action; pending = null; confirm.IsEnabled = false; status.Text = "Нажмите новое сочетание для «" + recording.Title + "», затем подтвердите."; } };
        PreviewKeyDown += (_, e) =>
        {
            if (recording == null || Search.IsKeyboardFocusWithin) return;
            e.Handled = true;
            if (e.Key == Key.Escape) { CancelRecording(); return; }
            try { Capture(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers); }
            catch (InvalidDataException error) { status.Text = error.Message; }
        };
        Refresh();
    }
    internal void Capture(Key key, ModifierKeys modifiers) { pending = EditorHotkeys.Format(key, modifiers); status.Text = recording?.Title + ": " + pending; confirm.IsEnabled = recording != null; }
    internal void Confirm() { if (recording != null && pending != null) { try { Assign(recording.Id, pending); } catch (InvalidDataException error) { status.Text = error.Message; } } }
    internal void Assign(string id, string keys) { var next = new Dictionary<string, string>(Profile) { [id] = keys }; Profile = EditorHotkeys.Validate(next); CancelRecording(); Refresh(); }
    internal void Import(string json) { var next = EditorHotkeys.Import(json); Profile = next; CancelRecording(); Refresh(); status.Text = "Профиль импортирован. Нажмите «Сохранить», чтобы применить."; }
    private void CancelRecording() { recording = null; pending = null; confirm.IsEnabled = false; Actions.SelectedItem = null; status.Text = "Выберите действие для записи сочетания."; }
    private void Refresh()
    {
        CancelRecording(); string query = Search.Text.Trim();
        Actions.ItemsSource = EditorHotkeys.Actions.Where(action => query.Length == 0 ? action.Category == (string?)Categories.SelectedItem : action.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || action.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(action => new Row(action, EditorHotkeys.Keys(action, Profile) is { Length: > 0 } keys ? keys.Replace(';', ',') : "не назначено")).ToArray();
    }
    private void Add(Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, Padding = new(10, 6, 10, 6), Margin = new(0, 0, 8, 6) }; button.Click += async (_, _) => { try { await action(); } catch (Exception error) { status.Text = error.Message; } }; panel.Children.Add(button);
    }
}
