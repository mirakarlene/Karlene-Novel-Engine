using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Compact project-wide graphics, runtime, diagnostics and storage preferences.</summary>
public sealed partial class ProjectParametersWindow : Window
{
    private readonly ProjectInfo project;
    internal GraphicsSettingsEditor GraphicsInput { get; } = new();
    internal TextBox TitleInput { get; } = new() { MaxLength = 128 };
    internal TextBox SavesInput { get; } = new();
    internal ComboBox CornerInput { get; } = GraphicsSettingsEditor.Choice(ProjectPerformanceSettings.Corners);
    internal ComboBox TextureInput => GraphicsInput.TextureInput;
    internal ComboBox AsyncTasksInput { get; } = GraphicsSettingsEditor.Choice(ProjectPerformanceSettings.AsyncTaskLimits);
    internal ComboBox AudioBufferInput { get; } = GraphicsSettingsEditor.Choice(ProjectPerformanceSettings.AudioBuffers);
    internal Expander DangerZone { get; } = new() { Header = "Danger Zone", IsExpanded = false, Foreground = Brushes.IndianRed, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 0) };
    internal Button DeleteProjectButton { get; } = DangerButton("Удалить проект");
    internal TextBlock DeleteCounter { get; } = new() { Visibility = Visibility.Collapsed, Foreground = Brushes.IndianRed, Margin = new(0, 5, 0, 0) };
    internal ProjectDeletionConfirmation DeletionConfirmation { get; } = new();
    internal bool DeletionRequested { get; private set; }
    internal Func<bool> ConfirmProjectDeletion { get; set; }
    internal Func<bool> ConfirmResetProgress { get; set; }
    private readonly Dictionary<string, CheckBox> switches = [];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0), FontSize = 11 };
    internal ProjectBuildSettingsEditor BuildInput { get; }
    internal int Page { get; private set; }
    internal Button PreviousPage { get; } = new() { Content = "‹", Padding = new(12, 3, 12, 3), IsEnabled = false };
    internal Button NextPage { get; } = new() { Content = "›", Padding = new(12, 3, 12, 3) };
    public ProjectParametersWindow(ProjectInfo project, string? projectsStoragePath = null)
    {
        this.project = project; Title = "Параметры проекта · " + project.Title;
        BuildInput = new(project, projectsStoragePath) { Visibility = Visibility.Collapsed };
        Width = 780; Height = 840; MinWidth = 540; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConfirmProjectDeletion = () => MessageBox.Show(this, $"Удалить проект «{project.Title}» и все файлы в папке?\n{project.Path}\n\nНесохранённые изменения будут потеряны. После подтверждения нажмите кнопку ещё 72 раза. Внешняя папка сохранений не удаляется.", "Удаление проекта", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        ConfirmResetProgress = () => MessageBox.Show(this, "Удалить сохранения этого проекта из сохранённой папки? Настройки и посторонние файлы останутся. Действие нельзя отменить.", "Сброс прогресса", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        var value = PerformanceSettingsStore.LoadProject(project.Path);
        GraphicsInput.Children.Clear(); // Reuse the value/selection contract while placing its inputs in two columns.
        GraphicsInput.Value = value.Graphics;
        GraphicsInput.ApiInput.ItemsSource = GraphicsSettings.Apis.Select(option => option.Value == "auto" ? option with { Label = "Автоматически" } : option).ToArray();
        GraphicsInput.CacheLimitInput.ItemsSource = GraphicsSettings.CacheLimits.Select(option => option with { Label = option.Value + " МБ" }).ToArray();
        GraphicsInput.ApiInput.SelectedValue = value.Graphics.Api; GraphicsInput.CacheLimitInput.SelectedValue = value.Graphics.ResourceCacheLimitMb;
        CornerInput.ItemsSource = ProjectPerformanceSettings.Corners.Where(option => option.Value != "bottom_left" || value.FpsCorner == "bottom_left").OrderBy(option => option.Value == "top_right" ? 0 : option.Value == "top_left" ? 1 : 2).ToArray();
        CornerInput.SelectedValue = value.FpsCorner; AsyncTasksInput.SelectedValue = value.MaxAsyncTasks; AudioBufferInput.SelectedValue = value.AudioBuffer;
        AudioBufferInput.ToolTip = "Два аудиобуфера по 25 / 75 / 150 мс. Меньший буфер снижает задержку, больший лучше переносит задержки обработки. Применяется при следующем открытии аудиофайла.";
        AsyncTasksInput.ToolTip = "Максимум одновременно активных веток, включая основную. Применяется при следующем запуске сценария.";
        var body = new StackPanel();
        TitleInput.Text = project.Title;
        MetadataInput = new(project, BuildInput, TitleInput);
        InitializeOptions(project);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        var graphics = new StackPanel(); graphics.Children.Add(Heading("Графика"));
        AddField(graphics, "Графический API", GraphicsInput.ApiInput); graphics.Children.Add(GraphicsInput.VSyncInput);
        AddField(graphics, "Ограничение FPS", GraphicsInput.FpsInput); AddField(graphics, "Сглаживание", GraphicsInput.MsaaInput); AddField(graphics, "Качество текстур", TextureInput);
        AddField(graphics, "Разрешение окна", ResolutionFields()); AddField(graphics, "Палитра игры", PaletteInput);
        var performance = new StackPanel(); performance.Children.Add(Heading("Производительность"));
        var cache = new DockPanel { LastChildFill = true }; GraphicsInput.CacheLimitInput.MinWidth = 80; GraphicsInput.CacheLimitInput.Width = 90; DockPanel.SetDock(GraphicsInput.CacheLimitInput, Dock.Right);
        cache.Children.Add(GraphicsInput.CacheLimitInput); cache.Children.Add(GraphicsInput.LimitCacheInput); performance.Children.Add(cache);
        performance.Children.Add(Hint("Кэш хранит подготовленные ресурсы. Лимит не включает активные изображения и память драйвера. API готовит графику один раз; движение выполняет композитор WPF."));
        AddSwitch(performance, "unload", "Выгружать ресурсы прошлой сцены", value.UnloadPreviousScene);
        AddField(performance, "Лимит асинхронных задач", AsyncTasksInput); AddField(performance, "Аудио буфер", AudioBufferInput);
        AddSwitch(performance, "preload", "Предзагрузка сцен", value.Preloading); AddField(performance, "Количество сцен", PreloadInput);
        var left = Card(graphics); var right = Card(performance); columns.Children.Add(left); columns.Children.Add(right); Grid.SetColumn(right, 2); body.Children.Add(columns);
        var diagnostics = new StackPanel(); diagnostics.Children.Add(Heading("Отладка и Диагностика"));
        var diagnosticGrid = new Grid(); diagnosticGrid.ColumnDefinitions.Add(new()); diagnosticGrid.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) diagnosticGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fpsRow = new DockPanel(); CornerInput.MinWidth = 130; CornerInput.Width = 155; DockPanel.SetDock(CornerInput, Dock.Right); fpsRow.Children.Add(CornerInput); AddSwitch(fpsRow, "fps", "Счётчик FPS", value.FpsCounter); Place(diagnosticGrid, fpsRow, 0, 0);
        Place(diagnosticGrid, Switch("stats", "Оверлей статистики (RAM, GPU, Draw Calls)", value.StatisticsOverlay), 0, 1);
        Place(diagnosticGrid, Switch("loading", "Уведомление загрузки в углу", value.LoadingIndicator), 1, 0);
        Place(diagnosticGrid, Switch("debug", "Режим отладки (консоль при запуске)", value.DebugMode), 1, 1);

        diagnostics.Children.Add(diagnosticGrid); var diagnosticCard = Card(diagnostics); diagnosticCard.Margin = new(0, 10, 0, 0); body.Children.Add(diagnosticCard);
        var storage = new StackPanel(); storage.Children.Add(Heading("Сохранения"));
        var pathRow = new DockPanel(); var browse = new Button { Content = "Выбрать...", Margin = new(8, 0, 0, 0), Padding = new(10, 5, 10, 5) }; DockPanel.SetDock(browse, Dock.Right); pathRow.Children.Add(browse);
        SavesInput.Text = value.SavesPath; SavesInput.Padding = new(5); pathRow.Children.Add(SavesInput); storage.Children.Add(pathRow);
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) SavesInput.Text = dialog.FolderName; };
        storage.Children.Add(Hint("Относительный путь считается от проекта. При смене папки существующие сохранения не переносятся."));
        AddField(storage, "Максимальная история действий (глубина отката)", RollbackInput);
        AddSwitch(storage, "autosave", "Автосохранение", project.Engine.Game.AutoSave);
        AddField(storage, "Интервал", AutosaveInput); AddField(storage, "Каждые N действий", AutosaveActionsInput);
        AddField(storage, "Фоновая музыка по умолчанию (главное меню, необязательно)", MusicRow());
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
        var save = new Button { Content = "Сохранить", Foreground = Brushes.White, Padding = new(16, 7, 16, 7) }; save.SetResourceReference(BackgroundProperty, "KNE.AccentBrush");
        var cancel = new Button { Content = "Отмена", Background = new SolidColorBrush(Color.FromRgb(65, 62, 74)), Foreground = Brushes.WhiteSmoke, Margin = new(8, 0, 0, 0), Padding = new(16, 7, 16, 7), IsCancel = true };
        save.Click += async (_, _) => { try { await SaveAsync(); DialogResult = true; } catch (Exception error) { status.Text = error.Message; } }; cancel.Click += (_, _) => Close();
        footer.Children.Add(save); footer.Children.Add(cancel);
        var storageCard = Card(storage); storageCard.Margin = new(0, 10, 0, 0); body.Children.Add(storageCard);
        var danger = new StackPanel { Margin = new(0, 7, 0, 0) };
        danger.Children.Add(Hint("Удаление проекта удаляет все файлы его папки и несохранённые изменения. Отмена или сворачивание этого раздела сбрасывает счётчик."));
        var dangerActions = new WrapPanel(); var reset = DangerButton("Удалить все сохранения"); reset.Margin = new(0, 0, 8, 0); dangerActions.Children.Add(reset); dangerActions.Children.Add(DeleteProjectButton); danger.Children.Add(dangerActions); danger.Children.Add(DeleteCounter);
        reset.Click += async (_, _) => { if (!ConfirmResetProgress()) return; try { status.Text = "Удалено сохранений: " + await new GameSaveStore(project.Path).DeleteAllProgressAsync(); } catch (Exception error) { status.Text = error.Message; } };
        DeleteProjectButton.Click += (_, _) =>
        {
            if (!DeletionConfirmation.IsArmed) { if (!ConfirmProjectDeletion()) return; DeletionConfirmation.Begin(); }
            else if (DeletionConfirmation.RecordClick()) { DeletionRequested = true; Close(); return; }
            DeleteCounter.Text = "Осталось нажатий: " + DeletionConfirmation.Remaining; DeleteCounter.Visibility = Visibility.Visible;
        };
        DangerZone.Content = danger; DangerZone.Collapsed += (_, _) => ResetDeletion(); body.Children.Add(DangerZone); body.Children.Add(status);
        BuildInput.Children.Add(SecurityCard(project.Engine.Security));
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var pageLabel = new TextBlock { Text = "1 / 3 · Проект", VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 14, 0) };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 10, 0, 0) };
        pager.Children.Add(PreviousPage); pager.Children.Add(pageLabel); pager.Children.Add(NextPage);
        void ShowPage(int page)
        {
            Page = Math.Clamp(page, 0, 2); ResetDeletion(); DangerZone.IsExpanded = false;
            scroll.Content = Page switch { 0 => body, 1 => BuildInput, _ => MetadataInput };
            BuildInput.Visibility = Visibility.Visible;
            PreviousPage.IsEnabled = Page > 0; NextPage.IsEnabled = Page < 2;
            pageLabel.Text = (Page + 1) + " / 3 · " + (Page switch { 0 => "Проект", 1 => "Сборки и безопасность", _ => "Метаданные проекта" });
            scroll.ScrollToTop();
        }
        PreviousPage.Click += (_, _) => ShowPage(Page - 1); NextPage.Click += (_, _) => ShowPage(Page + 1);
        Closed += (_, _) => { if (!DeletionRequested) ResetDeletion(); };
        body.Children.Remove(status);
        var bottom = new StackPanel(); bottom.Children.Add(status); bottom.Children.Add(footer); bottom.Children.Add(pager);
        var root = new DockPanel { Margin = new(20) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(scroll); Content = root;
        foreach (var text in Descendants(columns).OfType<TextBlock>()) text.TextWrapping = TextWrapping.Wrap;
        foreach (var check in switches.Values) if (check.Content is string label) check.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        foreach (var input in new[] { GraphicsInput.ApiInput, GraphicsInput.FpsInput, GraphicsInput.MsaaInput, TextureInput, GraphicsInput.CacheLimitInput, AsyncTasksInput, AudioBufferInput, CornerInput }) input.Margin = new(0, 3, 0, 5);
    }
    private void ResetDeletion() { DeletionConfirmation.Cancel(); DeleteCounter.Visibility = Visibility.Collapsed; DeleteCounter.Text = ""; }
    private CheckBox Switch(string key, string label, bool value) { var input = new CheckBox { Content = label, IsChecked = value, FontWeight = FontWeights.Normal, Margin = new(0, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center }; switches[key] = input; return input; }
    private void AddSwitch(Panel body, string key, string label, bool value) => body.Children.Add(Switch(key, label, value));
    private static void Place(Grid grid, UIElement child, int row, int column)
    {
        if (column == 1 && child is FrameworkElement element) { var margin = element.Margin; element.Margin = new(margin.Left + 8, margin.Top, margin.Right, margin.Bottom); }
        Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child);
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new(0, 0, 0, 8) };
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .65, FontWeight = FontWeights.Normal, FontSize = 10, Margin = new(0, 3, 0, 6) };
    private static Border Card(UIElement child) { var result = new Border { Child = child, Padding = new(12), CornerRadius = new(8), BorderThickness = new(1) }; result.SetResourceReference(Border.BackgroundProperty, "Editor.Surface"); result.SetResourceReference(Border.BorderBrushProperty, "Editor.Border"); return result; }
    private static Button DangerButton(string content) => new() { Content = content, Background = Brushes.DarkRed, Foreground = Brushes.White, BorderBrush = Brushes.IndianRed, Padding = new(12, 6, 12, 6) };
    private static void AddField(Panel body, string label, UIElement input) { body.Children.Add(new TextBlock { Text = label, FontSize = 12 }); body.Children.Add(input); }
    internal Task SaveAsync() => SaveAllOptionsAsync();
}
