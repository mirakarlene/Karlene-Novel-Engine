using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow : Window
{
    private readonly UserSettingsStore settingsStore = new();
    private readonly CancellationTokenSource lifetime = new();
    private UserSettings settings = new();
    private ProjectInfo? currentProject;
    private bool busy;
    private bool closed;
    private ProjectChangeMonitor? monitor;
    private string? monitoredPath;
    private bool pendingRefresh;
    private int viewRevision;
    private bool closeApproved;
    private bool checkingClose;

    public MainWindow() : this(null, null) { }

    internal MainWindow(Action<Window>? showTestingWindow, Action<string>? openAnalysisReport) : this(showTestingWindow, openAnalysisReport, null, null) { }

    internal MainWindow(Action<Window>? showTestingWindow, Action<string>? openAnalysisReport,
        Func<Window, string, string?>? selectProjectScript, Action<ProcessStartInfo>? openProjectScript)
    {
        InitializeComponent();
        EditorTheme.Apply(this, settings.Theme);
        ThemeInput.ItemsSource = EditorTheme.Themes;
        WelcomeObjectInput.ItemsSource = WelcomeBubbles.Objects;
        AccessibilityCards.ItemsSource = BlockAccessibility.Modes;
        UpdateWelcomeGreeting();
        UpdateDesignPreferences();
        SceneEditor.ProjectScriptRequested += SceneEditor_ProjectScriptRequested;
        SceneEditor.SubsystemRequested += SceneEditor_SubsystemRequested;
        presentTestingWindow = showTestingWindow ?? (window => { window.Owner = this; window.Show(); });
        presentAnalysisReport = openAnalysisReport ?? (path => { using (Process.Start(EditorLauncher.CreateStartInfo(settings.EditorPath, path))) { } });
        chooseProjectScript = selectProjectScript ?? ShowProjectScriptDialog;
        launchProjectScript = openProjectScript ?? (info => { using (Process.Start(info)) { } });
        storageTimer.Tick += async (_, _) => { if (WelcomePanel.Visibility == Visibility.Visible) await RefreshStoredProjectsAsync(); };
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (EngineDiagnostics.Current?.Snapshot.Failure != null) { closeApproved = true; return; }
        if (closeApproved || (ActiveEditor.Document == null && !busy && !LibraryView.HasPendingPreferenceSave)) return;
        e.Cancel = true;
        if (checkingClose || busy) return;
        checkingClose = true;
        try
        {
            if (await CanLeaveAllAsync())
            {
                await LibraryView.FlushPendingPreferencesAsync();
                closeApproved = true;
                // Closing may complete synchronously; re-enter Close only after this event has returned.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        catch (Exception ex)
        {
            OperationStatus.Text = ex.Message;
            if (IsVisible) MessageBox.Show(this, ex.Message, "Ошибка сохранения");
        }
        finally { checkingClose = false; }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        settings = await settingsStore.LoadAsync();
        AppGraphicsInput.Value = settings.Graphics; GraphicsEnvironment.SetSettings(this, settings.Graphics);
        TxtEditorPath.Text = settings.EditorPath;
        TxtProjectsStorage.Text = settings.ProjectsStoragePath;
        TxtUserName.Text = settings.UserName;
        ThemeInput.SelectedValue = settings.Theme;
        EditorTheme.Apply(this, settings.Theme);
        UpdateWelcomeGreeting();
        UpdateDesignPreferences();
        await RefreshStoredProjectsAsync();
        storageTimer.Start();
    }, "Загрузка настроек…");

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        storageTimer.Stop();
        lifetime.Cancel();
        monitor?.Dispose();
    }

    private async Task RunOperationAsync(Func<Task> operation, string status)
    {
        if (busy || closed) return;
        busy = true;
        WelcomePanel.IsEnabled = ProjectPanel.IsEnabled = SettingsPanel.IsEnabled = false;
        OperationStatus.Text = status;
        EngineDiagnostics.Trace("Операция редактора", status);
        try
        {
            await operation();
            if (!closed && OperationStatus.Text == status) OperationStatus.Text = "";
        }
        catch (OperationCanceledException) { if (!closed) OperationStatus.Text = "Операция отменена."; }
        catch (Exception ex)
        {
            if (!closed)
            {
                OperationStatus.Text = "Операция не завершена. " + ex.Message;
                if (IsVisible) MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            busy = false;
            if (!closed) WelcomePanel.IsEnabled = ProjectPanel.IsEnabled = SettingsPanel.IsEnabled = true;
            if (pendingRefresh && !closed) { pendingRefresh = false; _ = RefreshChangedProjectAsync(); }
        }
    }

    private void ShowPanel(UIElement panel)
    {
        WelcomePanel.Visibility = panel == WelcomePanel ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanel.Visibility = panel == ProjectPanel ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == SettingsPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> OpenProjectAsync(string path, bool remember = false)
    {
        var loaded = await ProjectLoader.LoadProjectAsync(path, lifetime.Token);
        if (closed) return false;
        if (!await CheckCompatibilityAsync(loaded)) return false;
        string? identityWarning = null;
        try { await FrameCatalog.SynchronizeAsync(loaded, lifetime.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { identityWarning = " Не удалось обновить ID фреймов: " + error.Message; }
        if (currentProject != null && !currentProject.Path.Equals(loaded.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!await CanLeaveAllAsync()) return false;
            ClearEditorTabs();
            expandedTreePaths.Clear(); selectedTreePath = null;
            ProjectFilesTree.ItemsSource = null;
            EditorWorkspace.Visibility = ActiveEditor.Visibility = Visibility.Collapsed;
            FilesDataGrid.Visibility = Visibility.Visible;
        }
        currentProject = loaded;
        EngineDiagnostics.Current?.SetProject(loaded.Title, loaded.Path);
        ProjectTitleTextBlock.Text = loaded.Name;
        await UpdateFileViewAsync();
        if (closed) return false;
        WatchProject(loaded.Path);
        ShowPanel(ProjectPanel);
        FileCountStatus.Text = "Файлов: " + loaded.Files.Count;
        OperationStatus.Text = string.Join(Environment.NewLine, loaded.Warnings.Take(5));
        OperationStatus.Text += identityWarning;
        if (remember)
        {
            settings.RememberProject(loaded.Path);
            await RefreshStoredProjectsAsync();
            try { await settingsStore.SaveAsync(settings); }
            catch (Exception ex) { OperationStatus.Text += " Не удалось сохранить список проектов: " + ex.Message; }
        }
        return true;
    }

    private async void BtnSelectProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с project.json" };
        if (dialog.ShowDialog(this) == true)
            await RunOperationAsync(() => OpenProjectAsync(dialog.FolderName, remember: true), "Открытие проекта…");
    }

    private async void BtnCreateProject_Click(object sender, RoutedEventArgs e)
    {
        string name = Interaction.InputBox("Введите имя нового проекта:", "Создание проекта", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var method = new ProjectCreationDialog { Owner = this };
        EditorTheme.Apply(method, settings.Theme);
        if (method.ShowDialog() != true) return;
        await RunOperationAsync(async () =>
        {
            if (!await EnsureProjectStorageAsync()) return;
            string path = await ProjectCreator.CreateProjectAsync(name, settings.ProjectsStoragePath, method.AuthoringMode, lifetime.Token);
            if (!await OpenProjectAsync(path, remember: true)) return;
            if (currentProject != null) await OpenScriptAsync(currentProject.MainScenePath);
        }, "Создание проекта…");
    }

    private async void BtnBack_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        if (!await CanLeaveAllAsync()) return;
        await LibraryView.FlushPendingPreferencesAsync();
        ClearEditorTabs();
        expandedTreePaths.Clear(); selectedTreePath = null;
        EditorWorkspace.Visibility = ActiveEditor.Visibility = Visibility.Collapsed;
        FilesDataGrid.Visibility = Visibility.Visible;
        currentProject = null;
        FilesDataGrid.ItemsSource = null;
        ProjectFilesTree.ItemsSource = null;
        monitor?.Dispose(); monitor = null; monitoredPath = null;
        pendingRefresh = false;
        OperationStatus.Text = "";
        await RefreshStoredProjectsAsync();
        ShowPanel(WelcomePanel);
    }, "Закрытие проекта…");

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        AppGraphicsInput.Value = settings.Graphics; GraphicsEnvironment.SetSettings(this, settings.Graphics);
        TxtEditorPath.Text = settings.EditorPath;
        TxtProjectsStorage.Text = settings.ProjectsStoragePath;
        TxtUserName.Text = settings.UserName;
        ThemeInput.SelectedValue = settings.Theme;
        UpdateDesignPreferences();
        ShowPanel(SettingsPanel);
    }

    private async Task SaveSettingsAsync()
    {
        string editorPath = TxtEditorPath.Text.Trim().Trim('"');
        if (editorPath.Length > 0 && (!Path.IsPathFullyQualified(editorPath) ||
            !File.Exists(editorPath) || !Path.GetExtension(editorPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Укажите полный путь к существующему .exe редактора или оставьте поле пустым.");
        string storage = TxtProjectsStorage.Text.Trim().Trim('"');
        if (storage.Length > 0)
        {
            if (!Path.IsPathFullyQualified(storage)) throw new ArgumentException("Укажите полный путь хранилища проектов.");
            storage = PathSafety.NormalizeDirectory(storage); PathSafety.RejectLinks(storage);
            Directory.CreateDirectory(storage);
        }
        string userName = TxtUserName.Text.Trim();
        if (userName.Length > 80 || userName.Any(char.IsControl)) throw new ArgumentException("Имя пользователя: до 80 символов, без переносов строк и управляющих символов.");
        var next = new UserSettings { Graphics = AppGraphicsInput.Value, EditorPath = editorPath, ProjectsStoragePath = storage, RecentProjects = settings.RecentProjects, HiddenProjects = settings.HiddenProjects, ProjectLastOpened = settings.ProjectLastOpened, Theme = EditorTheme.Normalize((string?)ThemeInput.SelectedValue), UserName = userName, AccessibilityMode = BlockAccessibility.GetMode(this), WelcomeObjects = WelcomeBubbles.Normalize((string?)WelcomeObjectInput.SelectedValue) };
        await settingsStore.SaveAsync(next);
        settings = next; GraphicsEnvironment.SetSettings(this, settings.Graphics);
        UpdateWelcomeGreeting();
        await RefreshStoredProjectsAsync();
        OperationStatus.Text = "Настройки сохранены.";
    }

    private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(SaveSettingsAsync, "Сохранение настроек…");

    private async void BtnSettingsBack_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        await SaveSettingsAsync();
        ShowPanel(WelcomePanel);
    }, "Сохранение настроек…");

    private void BtnSelectEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите текстовый редактор" };
        if (dialog.ShowDialog(this) == true) TxtEditorPath.Text = dialog.FileName;
    }

    private async void OpenRecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        if (!ProjectStorage.IsAvailable(settings, path))
        {
            await RefreshStoredProjectsAsync();
            const string message = "Проект не был обнаружен. Возможно, он перемещён или удалён. Проверьте путь «Хранилище проектов» в настройках.";
            if (MissingProjectWarning != null) MissingProjectWarning(message);
            else MessageBox.Show(this, message, "Проект не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RunOperationAsync(() => OpenProjectAsync(path, remember: true), "Открытие проекта…");
    }

    private async void FileSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        try { await UpdateFileViewAsync(); }
        catch (Exception ex) { if (!closed) OperationStatus.Text = "Ошибка поиска: " + ex.Message; }
    }

    private async Task UpdateFileViewAsync()
    {
        if (currentProject is not { } project || closed) return;
        int revision = ++viewRevision;
        string query = FileSearch.Text.Trim().Replace('/', Path.DirectorySeparatorChar);
        var files = project.Files.ToArray();
        var tree = await Task.Run(() => ProjectTree.Build(project.Path, files, query), lifetime.Token);
        if (revision != viewRevision || closed || currentProject != project) return;
        RememberTreeState();
        foreach (var node in FlattenTree(tree))
        {
            node.IsExpanded = expandedTreePaths.Contains(node.RelativePath);
            node.IsSelected = node.RelativePath.Equals(selectedTreePath, StringComparison.OrdinalIgnoreCase);
        }
        ProjectFilesTree.ItemsSource = tree;
        FilesDataGrid.ItemsSource = files.Where(f => Path.GetRelativePath(project.Path, f.FilePath).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void ProjectFilesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ProjectTreeNode { File: { } file })
        {
            FilesDataGrid.SelectedItem = file;
            FilesDataGrid.ScrollIntoView(file);
        }
    }

    private void WatchProject(string path)
    {
        if (monitoredPath == path) { monitor?.SetSaveDirectory(PerformanceSettingsStore.LoadProject(path).ResolveSavesPath(path)); return; }
        pendingRefresh = false;
        monitor?.Dispose();
        monitor = new ProjectChangeMonitor(path);
        monitoredPath = path;
        monitor.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!closed && currentProject?.Path == path)
            {
                if (busy) pendingRefresh = true;
                else _ = RefreshChangedProjectAsync();
            }
        }));
    }

    private async Task RefreshChangedProjectAsync()
    {
        if (busy || closed || currentProject is not { } project) return;
        busy = true;
        WelcomePanel.IsEnabled = ProjectPanel.IsEnabled = SettingsPanel.IsEnabled = false;
        try { await OpenProjectAsync(project.Path); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) OperationStatus.Text = "Не удалось обновить проект: " + ex.Message; }
        finally
        {
            busy = false;
            if (!closed) WelcomePanel.IsEnabled = ProjectPanel.IsEnabled = SettingsPanel.IsEnabled = true;
            if (pendingRefresh && !closed) { pendingRefresh = false; _ = RefreshChangedProjectAsync(); }
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is { } project)
            await RunOperationAsync(() => OpenProjectAsync(project.Path), "Обновление списка…");
    }

    private async void FileActionsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item || combo.Tag is not ProjectFile file) return;
        string action = item.Content?.ToString() ?? "";
        // Reset before awaiting or changing ItemsSource: virtualized rows may be recycled.
        combo.SelectedItem = null;
        if (busy || currentProject is not { } project) return;
        var operations = new ProjectFiles(project);
        await RunOperationAsync(async () =>
        {
            string source = operations.ValidateSource(file);
            switch (action)
            {
                case "Редактировать":
                    if (ScriptDocument.IsCodePath(source) || ScriptDocument.IsGraphPath(source))
                    {
                        await OpenScriptAsync(source);
                        break;
                    }
                    launchProjectScript(EditorLauncher.CreateStartInfo(settings.EditorPath, source));
                    break;
                case "Переименовать":
                    if (IsDocumentOpen(source))
                        throw new InvalidOperationException("Открытый сценарий нельзя переименовать. Сначала закройте его в редакторе.");
                    string name = Interaction.InputBox("Введите новое имя с расширением:", "Переименование", file.FileName);
                    if (name.Length == 0) return;
                    await Task.Run(() => operations.Rename(file, name), lifetime.Token);
                    await OpenProjectAsync(project.Path);
                    break;
                case "Дублировать":
                    var copyDialog = CreateFileDialog(project.Path, Path.GetFileNameWithoutExtension(file.FileName) + "_copy" + Path.GetExtension(file.FileName));
                    copyDialog.Title = "Дублировать файл";
                    if (copyDialog.ShowDialog(this) != true) return;
                    await Task.Run(() => operations.Duplicate(file, copyDialog.FileName), lifetime.Token);
                    await OpenProjectAsync(project.Path);
                    break;
                case "Переместить":
                    if (IsDocumentOpen(source))
                        throw new InvalidOperationException("Открытый сценарий нельзя переместить. Сначала закройте его в редакторе.");
                    var moveDialog = CreateFileDialog(project.Path, file.FileName);
                    moveDialog.Title = "Переместить файл · выберите новый путь внутри проекта";
                    if (moveDialog.ShowDialog(this) != true) return;
                    await Task.Run(() => operations.Move(file, moveDialog.FileName), lifetime.Token);
                    await OpenProjectAsync(project.Path);
                    break;
                case "Удалить":
                    if (IsDocumentOpen(source))
                        throw new InvalidOperationException("Открытый сценарий нельзя удалить. Сначала закройте его в редакторе.");
                    if (MessageBox.Show(this, $"Удалить {file.FileName}? Действие необратимо.", "Удаление",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    await Task.Run(() => operations.Delete(file), lifetime.Token);
                    await OpenProjectAsync(project.Path);
                    break;
            }
        }, "Работа с файлом…");
    }

    private static SaveFileDialog CreateFileDialog(string root, string suggestedName) => new()
    {
        Title = "Создать новый файл",
        InitialDirectory = root,
        FileName = suggestedName,
        Filter = "Все файлы (*.*)|*.*|KRL (*.krl)|*.krl|Lua (*.lua)|*.lua|Текст (*.txt)|*.txt|JSON (*.json)|*.json",
        AddExtension = true,
        DefaultExt = Path.GetExtension(suggestedName),
        OverwritePrompt = false
    };

    private async void BtnCreateFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        var dialog = CreateFileDialog(project.Path, "new_file.txt");
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            await Task.Run(() => new ProjectFiles(project).Create(dialog.FileName), lifetime.Token);
            await OpenProjectAsync(project.Path);
        }, "Создание файла…");
    }

    private async void BtnRunProject_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        await RunOperationAsync(async () =>
        {
            await SaveAllEditorsAsync();
            // Re-read configuration and files changed by the external editor.
            if (!await OpenProjectAsync(project.Path)) return;
            if (closed || currentProject == null) return;
            _ = currentProject.MainScenePath;
            _ = currentProject.InitScriptPath;
            var game = new GameWindow(currentProject);
            game.Show();
        }, "Запуск проекта…");
    }

    private async void BtnOpenScript_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project || FilesDataGrid.SelectedItem is not ProjectFile file) return;
        await RunOperationAsync(() => OpenScriptAsync(new ProjectFiles(project).ValidateSource(file)), "Открытие сценария…");
    }

    private async void BtnAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject == null) return;
        await RunOperationAsync(async () =>
        {
            var editor = new AppearanceEditor();
            await editor.OpenAsync(currentProject);
            var window = new Window { Title = "Оформление · " + currentProject.Title, Content = editor, Owner = this,
                Width = 1280, Height = 850, MinWidth = 1000, MinHeight = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            EditorTheme.Apply(window, settings.Theme);
            bool approved = false; bool checking = false;
            window.Closing += async (_, args) =>
            {
                if (approved || !editor.IsDirty) return;
                args.Cancel = true;
                if (checking) return;
                checking = true;
                try { if (await editor.CanLeaveAsync()) { approved = true; _ = window.Dispatcher.BeginInvoke(new Action(window.Close)); } }
                catch (Exception ex) { MessageBox.Show(window, ex.Message, "Оформление"); }
                finally { checking = false; }
            };
            window.ShowDialog();
            await OpenProjectAsync(currentProject.Path);
        }, "Оформление…");
    }

    private async void OpenScript_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (currentProject is not { } project || FileAtDoubleClick(sender, e.OriginalSource as DependencyObject) is not { } file ||
            !(ScriptDocument.IsCodePath(file.FilePath) || ScriptDocument.IsGraphPath(file.FilePath))) return;
        e.Handled = true;
        await RunOperationAsync(() => OpenScriptAsync(new ProjectFiles(project).ValidateSource(file)), "Открытие сценария…");
    }

    private void BtnFileList_Click(object sender, RoutedEventArgs e)
    {
        FilesDataGrid.Visibility = Visibility.Visible;
        EditorWorkspace.Visibility = ActiveEditor.Visibility = Visibility.Collapsed;
    }

    private async void BtnNewVisualScene_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        var dialog = new SaveFileDialog
        {
            Title = "Создать визуальную сцену", InitialDirectory = project.Path,
            FileName = "scene", Filter = "Игровая блок-схема (*.knegraph)|*.knegraph|Шейдерная блок-схема (*.kneshader)|*.kneshader",
            DefaultExt = ".knegraph", AddExtension = true, OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            string path = dialog.FileName;
            if (dialog.FilterIndex == 2 && !ScriptDocument.IsShaderPath(path)) path = Path.ChangeExtension(path, ".kneshader");
            await ScriptDocument.CreateVisualAsync(project.Path, path, lifetime.Token);
            await OpenProjectAsync(project.Path);
            await OpenScriptAsync(path);
        }, "Создание блок-схемы…");
    }

    private void SceneEditor_RunRequested(object? sender, EventArgs e) => OpenTestingWindow(sceneTest: false);
    private void SceneEditor_TestSceneRequested(object? sender, EventArgs e) => OpenTestingWindow(sceneTest: true);

    private async void SceneEditor_ExternalEditorRequested(object? sender, EventArgs e)
    {
        if (ActiveEditor.Document is not { IsVisual: false } document) return;
        await RunOperationAsync(async () =>
        {
            await ActiveEditor.SaveAsync();
            using (Process.Start(EditorLauncher.CreateStartInfo(settings.EditorPath, document.FilePath))) { }
        }, "Открытие внешнего редактора…");
    }

    private async void SceneEditor_MainSceneRequested(object? sender, EventArgs e)
    {
        if (currentProject is not { } project || ActiveEditor.Document is not { } document) return;
        await RunOperationAsync(async () =>
        {
            await ActiveEditor.SaveAsync();
            var program = GameScripts.Load(project);
            string relative = Path.GetRelativePath(project.Path, document.FilePath).Replace('\\', '/');
            if (!program.Main.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Игра всегда начинается с единственной main(). Назовите функцию выбранного сценария main, а прежнюю main переименуйте, затем повторите действие.");
            string path = PathSafety.ResolveRelative(project.Path, "project.json");
            string original = await File.ReadAllTextAsync(path, lifetime.Token);
            var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(original, ProjectConfiguration.JsonOptions)
                ?? throw new InvalidDataException("Пустая конфигурация проекта.");
            configuration.EntryPoints ??= new EntryPoints();
            configuration.EntryPoints.MainScene = Path.GetRelativePath(project.Path, PathSafety.EnsureInside(project.Path, document.FilePath)).Replace('\\', '/');
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(configuration, ProjectConfiguration.JsonOptions), lifetime.Token);
                PathSafety.EnsureInside(project.Path, path);
                if (await File.ReadAllTextAsync(path, lifetime.Token) != original) throw new IOException("project.json изменён другим приложением. Повторите действие.");
                File.Move(staging, path, overwrite: true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            await OpenProjectAsync(project.Path);
            OperationStatus.Text = "Главная сцена: " + configuration.EntryPoints.MainScene;
        }, "Смена главной сцены…");
    }

    private async void SceneEditor_CloseRequested(object? sender, EventArgs e) => await RunOperationAsync(
        () => CloseEditorTabAsync(sender as ScriptEditor ?? ActiveEditor), "Закрытие сценария…");
}
