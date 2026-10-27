using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace KNE;

/// <summary>Compact project-wide graphics, runtime, diagnostics and storage preferences.</summary>
public sealed class ProjectParametersWindow : Window
{
    private readonly ProjectInfo project;
    internal GraphicsSettingsEditor GraphicsInput { get; } = new();
    internal TextBox TitleInput { get; } = new() { IsReadOnly = true, MaxLength = 128, FontSize = 18, FontWeight = FontWeights.SemiBold, Width = 420 };
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
    internal Func<Task> BeforeBuildAsync { get; set; } = () => Task.CompletedTask;
    private readonly CancellationTokenSource buildLifetime = new();
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
        GraphicsInput.CacheLimitInput.ItemsSource = GraphicsSettings.CacheLimits.Where(option => option.Value >= 64 || option.Value == value.Graphics.ResourceCacheLimitMb).Select(option => option with { Label = option.Value + " МБ" }).ToArray();
        GraphicsInput.ApiInput.SelectedValue = value.Graphics.Api; GraphicsInput.CacheLimitInput.SelectedValue = value.Graphics.ResourceCacheLimitMb;
        CornerInput.ItemsSource = ProjectPerformanceSettings.Corners.Where(option => option.Value != "bottom_left" || value.FpsCorner == "bottom_left").OrderBy(option => option.Value == "top_right" ? 0 : option.Value == "top_left" ? 1 : 2).ToArray();
        CornerInput.SelectedValue = value.FpsCorner; AsyncTasksInput.SelectedValue = value.MaxAsyncTasks; AudioBufferInput.SelectedValue = value.AudioBuffer;
        AudioBufferInput.ToolTip = "Два аудиобуфера по 25 / 75 / 150 мс. Меньший буфер снижает задержку, больший лучше переносит задержки обработки. Применяется при следующем открытии аудиофайла.";
        AsyncTasksInput.ToolTip = "Максимум одновременно активных веток, включая основную. Применяется при следующем запуске сценария.";
        var body = new StackPanel { Width = 732 };
        var heading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = "Название проекта", HorizontalAlignment = HorizontalAlignment.Center, Opacity = .7, Margin = new(0, 0, 0, 6) });
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        TitleInput.Text = project.Title; TitleInput.ToolTip = "Название в project.json; папка проекта остаётся на прежнем месте.";
        var rename = new Button { Content = "Переименовать", Margin = new(8, 0, 0, 0), Padding = new(10, 5, 10, 5) };
        void Rename()
        {
            if (TitleInput.IsReadOnly) { TitleInput.IsReadOnly = false; rename.Content = "Готово"; TitleInput.Focus(); TitleInput.SelectAll(); }
            else { try { TitleInput.Text = ProjectMaintenance.ValidateName(TitleInput.Text); TitleInput.IsReadOnly = true; rename.Content = "Переименовать"; status.Text = "Новое название применится при сохранении."; } catch (ArgumentException error) { status.Text = error.Message; } }
        }
        rename.Click += (_, _) => Rename(); TitleInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Rename(); e.Handled = true; } };
        titleRow.Children.Add(TitleInput); titleRow.Children.Add(rename); heading.Children.Add(titleRow); body.Children.Add(heading);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        var graphics = new StackPanel(); graphics.Children.Add(Heading("Графика"));
        AddField(graphics, "Графический API", GraphicsInput.ApiInput); graphics.Children.Add(GraphicsInput.VSyncInput);
        AddField(graphics, "Ограничение FPS", GraphicsInput.FpsInput); AddField(graphics, "Сглаживание", GraphicsInput.MsaaInput); AddField(graphics, "Качество текстур", TextureInput);
        var performance = new StackPanel(); performance.Children.Add(Heading("Производительность"));
        var cache = new DockPanel { LastChildFill = true }; GraphicsInput.CacheLimitInput.MinWidth = 80; GraphicsInput.CacheLimitInput.Width = 90; DockPanel.SetDock(GraphicsInput.CacheLimitInput, Dock.Right);
        cache.Children.Add(GraphicsInput.CacheLimitInput); cache.Children.Add(GraphicsInput.LimitCacheInput); performance.Children.Add(cache);
        performance.Children.Add(Hint("Кэш хранит подготовленные ресурсы. Лимит не включает активные изображения и память драйвера. API готовит графику один раз; движение выполняет композитор WPF."));
        AddSwitch(performance, "unload", "Выгружать ресурсы прошлой сцены", value.UnloadPreviousScene);
        AddField(performance, "Лимит асинхронных задач", AsyncTasksInput); AddField(performance, "Аудио буфер", AudioBufferInput);
        var left = Card(graphics); var right = Card(performance); columns.Children.Add(left); columns.Children.Add(right); Grid.SetColumn(right, 2); body.Children.Add(columns);
        var diagnostics = new StackPanel(); diagnostics.Children.Add(Heading("Отладка и Диагностика"));
        var diagnosticGrid = new Grid(); diagnosticGrid.ColumnDefinitions.Add(new()); diagnosticGrid.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) diagnosticGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fpsRow = new DockPanel(); CornerInput.MinWidth = 130; CornerInput.Width = 155; DockPanel.SetDock(CornerInput, Dock.Right); fpsRow.Children.Add(CornerInput); AddSwitch(fpsRow, "fps", "Счётчик FPS", value.FpsCounter); Place(diagnosticGrid, fpsRow, 0, 0);
        Place(diagnosticGrid, Switch("stats", "Оверлей статистики (RAM, GPU, Draw Calls)", value.StatisticsOverlay), 0, 1);
        Place(diagnosticGrid, Switch("loading", "Уведомление загрузки в углу", value.LoadingIndicator), 1, 0);
        Place(diagnosticGrid, Switch("debug", "Режим отладки (консоль при запуске)", value.DebugMode), 1, 1);
        Place(diagnosticGrid, Switch("preload", "Предзагрузка ресурсов следующей сцены", value.Preloading), 2, 0);
        diagnostics.Children.Add(diagnosticGrid); var diagnosticCard = Card(diagnostics); diagnosticCard.Margin = new(0, 10, 0, 0); body.Children.Add(diagnosticCard);
        var storage = new StackPanel(); storage.Children.Add(Heading("Путь к сохранениям"));
        var pathRow = new DockPanel(); var browse = new Button { Content = "Выбрать...", Margin = new(8, 0, 0, 0), Padding = new(10, 5, 10, 5) }; DockPanel.SetDock(browse, Dock.Right); pathRow.Children.Add(browse);
        SavesInput.Text = value.SavesPath; SavesInput.Padding = new(5); pathRow.Children.Add(SavesInput); storage.Children.Add(pathRow);
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) SavesInput.Text = dialog.FolderName; };
        storage.Children.Add(Hint("Относительный путь считается от проекта. При смене папки существующие сохранения не переносятся."));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
        var save = new Button { Content = "Сохранить", Foreground = Brushes.White, Padding = new(16, 7, 16, 7) }; save.SetResourceReference(BackgroundProperty, "KNE.AccentBrush");
        var cancel = new Button { Content = "Отмена", Background = new SolidColorBrush(Color.FromRgb(65, 62, 74)), Foreground = Brushes.WhiteSmoke, Margin = new(8, 0, 0, 0), Padding = new(16, 7, 16, 7), IsCancel = true };
        save.Click += async (_, _) => { try { await SaveAsync(); DialogResult = true; } catch (Exception error) { status.Text = error.Message; } }; cancel.Click += (_, _) => Close();
        footer.Children.Add(save); footer.Children.Add(cancel); storage.Children.Add(footer);
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
        var firstPage = body.Children.OfType<UIElement>().Where(item => item != status).ToArray();
        body.Children.Insert(body.Children.Count - 1, BuildInput);
        var pageLabel = new TextBlock { Text = "1 / 2 · Проект", VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 14, 0) };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 10, 0, 0) };
        pager.Children.Add(PreviousPage); pager.Children.Add(pageLabel); pager.Children.Add(NextPage); body.Children.Add(pager);
        void ShowPage(int page)
        {
            Page = page; ResetDeletion(); DangerZone.IsExpanded = false;
            foreach (var child in firstPage) child.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildInput.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (footer.Parent is Panel parent) parent.Children.Remove(footer);
            (page == 0 ? storage.Children : BuildInput.Children).Add(footer);
            PreviousPage.IsEnabled = page == 1; NextPage.IsEnabled = page == 0; pageLabel.Text = page == 0 ? "1 / 2 · Проект" : "2 / 2 · Сборки";
        }
        PreviousPage.Click += (_, _) => ShowPage(0); NextPage.Click += (_, _) => ShowPage(1);
        BuildInput.BuildButton.Click += async (_, _) =>
        {
            BuildInput.IsEnabled = false; save.IsEnabled = false; PreviousPage.IsEnabled = false;
            try
            {
                await BeforeBuildAsync(); await SaveAsync(); var options = ProjectBuildSettingsStore.Load(project.Path); var loaded = await ProjectLoader.LoadProjectAsync(project.Path, buildLifetime.Token);
                var built = await new GameBuildExporter().BuildAsync(loaded, options, options.ReleasePath, log: line => Dispatcher.BeginInvoke(() => status.Text = line), token: buildLifetime.Token);
                status.Text = "Готово: " + (built.Package ?? built.Executable);
            }
            catch (OperationCanceledException) { status.Text = "Сборка отменена."; }
            catch (Exception error) { status.Text = error.Message; }
            finally { BuildInput.IsEnabled = true; save.IsEnabled = true; PreviousPage.IsEnabled = Page == 1; }
        };
        Closed += (_, _) => { buildLifetime.Cancel(); if (!DeletionRequested) ResetDeletion(); };
        // Keep the requested window dimensions, including a smaller manually resized window and expanded danger zone.
        Content = new Viewbox { Child = body, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, Margin = new(20) };
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
    internal async Task SaveAsync()
    {
        string title = ProjectMaintenance.ValidateName(TitleInput.Text);
        var value = new ProjectPerformanceSettings { Graphics = GraphicsInput.Value, FpsCounter = switches["fps"].IsChecked == true, FpsCorner = (string?)CornerInput.SelectedValue ?? "top_right", StatisticsOverlay = switches["stats"].IsChecked == true, LoadingIndicator = switches["loading"].IsChecked == true, DebugMode = switches["debug"].IsChecked == true, Preloading = switches["preload"].IsChecked == true, UnloadPreviousScene = switches["unload"].IsChecked == true, MaxAsyncTasks = (int?)AsyncTasksInput.SelectedValue ?? 128, AudioBuffer = (string?)AudioBufferInput.SelectedValue ?? "balanced", SavesPath = SavesInput.Text.Trim() };
        value.Validate(project.Path);
        var build = BuildInput.Value; build.Validate();
        string buildFile = System.IO.Path.Combine(project.Path, "engine", "build.json");
        byte[]? previousBuild = System.IO.File.Exists(buildFile) ? await System.IO.File.ReadAllBytesAsync(buildFile) : null;
        var previous = PerformanceSettingsStore.LoadProject(project.Path);
        try
        {
            await ProjectBuildSettingsStore.SaveAsync(project.Path, build);
            await PerformanceSettingsStore.SaveProjectAsync(project.Path, value);
            if (title != project.Title) await ProjectMaintenance.RenameAsync(project, title);
            BuildInput.Saved(ProjectBuildSettingsStore.Load(project.Path));
        }
        catch
        {
            await PerformanceSettingsStore.SaveProjectAsync(project.Path, previous);
            if (previousBuild == null) System.IO.File.Delete(buildFile); else await System.IO.File.WriteAllBytesAsync(buildFile, previousBuild);
            throw;
        }
    }
}
