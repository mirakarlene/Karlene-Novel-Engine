using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

/// <summary>Full-size, scrollable editor preferences; no scaling of existing settings pages.</summary>
public sealed class GeneralSettingsEditor : ScrollViewer
{
    internal TextBox BackupPath { get; } = new();
    internal ComboBox BackupFrequency { get; } = Choice([(-1, "При каждом сохранении проекта"), (120, "Раз в два часа"), (60, "Раз в час"), (30, "Раз в 30 минут"), (10, "Раз в 10 минут"), (0, "Вручную (только по кнопке)")]);
    internal ComboBox BackupCopies { get; } = Choice(EditorPreferences.BackupLimits.Select(n => (n, n.ToString())));
    internal ComboBox AutosaveInterval { get; } = Choice([(1, "1 минута"), (5, "5 минут"), (10, "10 минут"), (30, "30 минут"), (0, "Отключено")]);
    internal ComboBox HistoryLimit { get; } = Choice(EditorPreferences.HistoryLimits.Select(n => (n, n.ToString())));
    internal CheckBox KeepHistory { get; } = Check("Сохранять историю изменений");
    internal ComboBox Startup { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("home", "Открыть главный экран"), new("last", "Открыть последний проект"), new("project", "Открыть назначенный проект"), new("new_scene", "Создать новую пустую сцену в назначенном проекте"), new("settings", "Открыть экран настроек")]);
    internal TextBox StartupProject { get; } = new() { IsReadOnly = true };
    internal CheckBox SoundEnabled { get; } = Check("Звуковые сигналы");
    internal ComboBox Sound { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("quiet", "Тихий"), new("standard", "Стандартный"), new("loud", "Громкий"), new("custom", "Свой файл")]);
    internal TextBox SoundFile { get; } = new() { IsReadOnly = true };
    internal CheckBox Toasts { get; } = Check("Визуальные тосты (Windows Action Center)");
    internal CheckBox Aggregate { get; } = Check("Группировать похожие уведомления");
    internal CheckBox Minimized { get; } = Check("Когда программа свёрнута");
    internal CheckBox Tips { get; } = Check("Некритичные уведомления (советы, напоминания)");
    internal CheckBox Breaks { get; } = Check("Включить напоминания о перерыве");
    internal ComboBox BreakInterval { get; } = Choice([(30, "30 минут"), (45, "45 минут"), (60, "1 час"), (90, "1.5 часа"), (120, "2 часа")]);
    internal ComboBox BreakType { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("soft", "Мягкое (уведомление)"), new("strict", "Строгое (блокировка интерфейса)")]);
    internal ComboBox BreakDuration { get; } = Choice(EditorPreferences.BreakDurations.Select(n => (n, n + " минут")));
    internal CheckBox WorkTime { get; } = Check("Показывать время работы в статус-баре");
    internal Dictionary<string, string> Hotkeys { get; private set; } = [];
    internal Func<Task>? BackupRequested { get; set; }
    internal Func<Task>? HistoryRequested { get; set; }
    internal Func<Task>? TestNotificationRequested { get; set; }
    internal Action? ResetWorkRequested { get; set; }
    internal Func<string>? ThemeProvider { get; set; }
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly FrameworkElement projectRow, soundRow;
    public GeneralSettingsEditor()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var grid = new Grid { Margin = new(0, 0, 12, 0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new());
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right); Content = grid;
        var backup = Section(left, "Резервное копирование"); Field(backup, "Путь хранения", PathRow(BackupPath, "Выбрать…", () => BrowseFolder(BackupPath)));
        backup.Children.Add(Hint("Резервные копии хранятся отдельно от проектов. При перемещении хранилища проекты не перемещаются автоматически"));
        Field(backup, "Частота создания", BackupFrequency); Field(backup, "Хранить последних копий", BackupCopies);
        backup.Children.Add(Hint("Старые копии автоматически удаляются. Это не влияет на историю версий автосохранения"));
        Button(backup, "Создать резервную копию", () => BackupRequested?.Invoke() ?? Task.CompletedTask);
        var autosave = Section(left, "Автосохранение"); Field(autosave, "Интервал автосохранения", AutosaveInterval); Field(autosave, "Максимум версий на проект", HistoryLimit); autosave.Children.Add(KeepHistory);
        Button(autosave, "Просмотреть историю", () => HistoryRequested?.Invoke() ?? Task.CompletedTask);
        var keys = Section(left, "Горячие клавиши"); Button(keys, "Настроить горячие клавиши", () =>
        {
            var window = new HotkeySettingsWindow(Hotkeys) { Owner = Window.GetWindow(this) };
            EditorTheme.Apply(window, ThemeProvider?.Invoke() ?? "kne"); if (window.ShowDialog() == true) Hotkeys = window.Profile; return Task.CompletedTask;
        });
        var startup = Section(left, "Поведение при запуске"); Field(startup, "При запуске KNE…", Startup);
        projectRow = PathRow(StartupProject, "Выбрать проект…", () => BrowseFolder(StartupProject)); startup.Children.Add(projectRow);
        startup.Children.Add(Hint("Если выбран проект, но он был удалён или перемещён, KNE откроет главный экран и покажет предупреждение"));
        var notifications = Section(right, "Уведомления"); notifications.Children.Add(SoundEnabled); Field(notifications, "Звук уведомления", Sound);
        soundRow = PathRow(SoundFile, "Выбрать WAV…", () => { var dialog = new OpenFileDialog { Filter = "Звук WAV (*.wav)|*.wav" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) SoundFile.Text = dialog.FileName; }); notifications.Children.Add(soundRow);
        notifications.Children.Add(Toasts); notifications.Children.Add(Aggregate); notifications.Children.Add(new TextBlock { Text = "Когда показывать уведомления", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 3) }); notifications.Children.Add(Minimized); notifications.Children.Add(Tips);
        notifications.Children.Add(Hint("После закрытия KNE работа завершается; новые уведомления не отправляются."));
        Button(notifications, "Проверить уведомления", () => TestNotificationRequested?.Invoke() ?? Task.CompletedTask);
        notifications.Children.Add(Hint("Уведомления используют Windows Action Center. Убедитесь, что KNE разрешён в параметрах уведомлений Windows"));
        var breaks = Section(right, "Ограничение по времени"); breaks.Children.Add(Breaks); Field(breaks, "Интервал напоминания", BreakInterval); Field(breaks, "Тип напоминания", BreakType);
        breaks.Children.Add(Hint("Мягкие напоминания используют настройки некритичных уведомлений."));
        Field(breaks, "Длительность строгого перерыва", BreakDuration); breaks.Children.Add(WorkTime);
        Button(breaks, "Сбросить статистику", () => { ResetWorkRequested?.Invoke(); return Task.CompletedTask; }); right.Children.Add(Status);
        Startup.SelectionChanged += (_, _) => Refresh(); Sound.SelectionChanged += (_, _) => Refresh(); SoundEnabled.Click += (_, _) => Refresh(); Breaks.Click += (_, _) => Refresh(); BreakType.SelectionChanged += (_, _) => Refresh();
        Load(new(), [], "");
    }

    internal void Load(EditorPreferences value, Dictionary<string, string> keys, string defaultBackup)
    {
        BackupPath.Text = value.BackupPath == "" ? defaultBackup : value.BackupPath; BackupFrequency.SelectedValue = value.BackupMinutes; BackupCopies.SelectedValue = value.BackupCopies;
        AutosaveInterval.SelectedValue = value.AutosaveMinutes; HistoryLimit.SelectedValue = value.AutosaveVersions; KeepHistory.IsChecked = value.KeepHistory;
        Startup.SelectedValue = value.Startup; StartupProject.Text = value.StartupProject; SoundEnabled.IsChecked = value.NotificationSound; Sound.SelectedValue = value.Sound; SoundFile.Text = value.SoundFile;
        Toasts.IsChecked = value.Toasts; Aggregate.IsChecked = value.AggregateNotifications; Minimized.IsChecked = value.NotifyMinimized; Tips.IsChecked = value.NotifyTips;
        Breaks.IsChecked = value.BreakReminders; BreakInterval.SelectedValue = value.BreakMinutes; BreakType.SelectedValue = value.StrictBreak ? "strict" : "soft"; BreakDuration.SelectedValue = value.BreakDuration; WorkTime.IsChecked = value.ShowWorkTime;
        Hotkeys = new(keys); Status.Text = ""; Refresh();
    }
    internal EditorPreferences Value => new()
    {
        BackupPath = BackupPath.Text.Trim(), BackupMinutes = (int?)BackupFrequency.SelectedValue ?? -1, BackupCopies = (int?)BackupCopies.SelectedValue ?? 5,
        AutosaveMinutes = (int?)AutosaveInterval.SelectedValue ?? 5, AutosaveVersions = (int?)HistoryLimit.SelectedValue ?? 25, KeepHistory = KeepHistory.IsChecked == true,
        Startup = (string?)Startup.SelectedValue ?? "home", StartupProject = StartupProject.Text.Trim(), NotificationSound = SoundEnabled.IsChecked == true, Sound = (string?)Sound.SelectedValue ?? "standard", SoundFile = SoundFile.Text,
        Toasts = Toasts.IsChecked == true, AggregateNotifications = Aggregate.IsChecked == true, NotifyMinimized = Minimized.IsChecked == true, NotifyTips = Tips.IsChecked == true,
        BreakReminders = Breaks.IsChecked == true, BreakMinutes = (int?)BreakInterval.SelectedValue ?? 60, StrictBreak = (string?)BreakType.SelectedValue == "strict", BreakDuration = (int?)BreakDuration.SelectedValue ?? 5, ShowWorkTime = WorkTime.IsChecked == true
    };
    private void Refresh()
    {
        if (projectRow == null || soundRow == null) return;
        projectRow.IsEnabled = (string?)Startup.SelectedValue is "project" or "new_scene";
        Sound.IsEnabled = SoundEnabled.IsChecked == true; soundRow.IsEnabled = SoundEnabled.IsChecked == true && (string?)Sound.SelectedValue == "custom";
        BreakInterval.IsEnabled = BreakType.IsEnabled = Breaks.IsChecked == true; BreakDuration.IsEnabled = Breaks.IsChecked == true && (string?)BreakType.SelectedValue == "strict";
    }
    private void BrowseFolder(TextBox input) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName; }
    private static ComboBox Choice(IEnumerable<(int Value, string Label)> values) => GraphicsSettingsEditor.Choice(values.Select(value => new GraphicsOption<int>(value.Value, value.Label)).ToArray());
    private static CheckBox Check(string label) => new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Margin = new(0, 5, 0, 5), VerticalContentAlignment = VerticalAlignment.Center };
    private static StackPanel Section(Panel parent, string title)
    {
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        var border = new Border { Child = panel, Padding = new(18), BorderThickness = new(1), CornerRadius = new(10), Margin = new(0, 0, 0, 12) }; border.SetResourceReference(BackgroundProperty, "Editor.Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Editor.Border"); parent.Children.Add(border); return panel;
    }
    private static void Field(Panel panel, string label, FrameworkElement input) { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 4, 0, 5) }); input.Margin = new(0, 0, 0, 5); panel.Children.Add(input); }
    private static TextBlock Hint(string text) { var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 7) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "Editor.Muted"); return hint; }
    private static DockPanel PathRow(TextBox input, string title, Action browse)
    {
        var panel = new DockPanel(); var button = new Button { Content = title, Padding = new(10, 6, 10, 6), Margin = new(8, 0, 0, 0) }; button.Click += (_, _) => browse(); DockPanel.SetDock(button, Dock.Right); panel.Children.Add(button); input.Padding = new(6); panel.Children.Add(input); return panel;
    }
    private void Button(Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, Padding = new(12, 6, 12, 6), Margin = new(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception error) { Status.Text = error.Message; } finally { button.IsEnabled = true; } }; panel.Children.Add(button);
    }
}
