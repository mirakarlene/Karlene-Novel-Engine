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

    public MainWindow()
    {
        InitializeComponent();
        EmbeddedPreview.SelectionProvider = SceneEditor.CapturePreview;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved || (SceneEditor.Document == null && !busy)) return;
        e.Cancel = true;
        if (checkingClose || busy) return;
        checkingClose = true;
        try
        {
            if (await SceneEditor.CanLeaveAsync())
            {
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
        TxtEditorPath.Text = settings.EditorPath;
        RecentProjectsList.ItemsSource = settings.RecentProjects;
    }, "Загрузка настроек…");

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        monitor?.Dispose();
        EmbeddedPreview.Stop();
    }

    private async Task RunOperationAsync(Func<Task> operation, string status)
    {
        if (busy || closed) return;
        busy = true;
        WelcomePanel.IsEnabled = ProjectPanel.IsEnabled = SettingsPanel.IsEnabled = false;
        OperationStatus.Text = status;
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

    private async Task OpenProjectAsync(string path, bool remember = false)
    {
        var loaded = await ProjectLoader.LoadProjectAsync(path, lifetime.Token);
        if (closed) return;
        if (currentProject != null && !currentProject.Path.Equals(loaded.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!await SceneEditor.CanLeaveAsync()) return;
            SceneEditor.Clear();
            EditorWorkspace.Visibility = SceneEditor.Visibility = Visibility.Collapsed;
            EmbeddedPreview.Stop();
            FilesDataGrid.Visibility = Visibility.Visible;
        }
        currentProject = loaded;
        ProjectTitleTextBlock.Text = loaded.Name;
        await UpdateFileViewAsync();
        if (closed) return;
        WatchProject(loaded.Path);
        ShowPanel(ProjectPanel);
        OperationStatus.Text = loaded.Warnings.Count == 0 ? "Файлов: " + loaded.Files.Count :
            $"Файлов: {loaded.Files.Count}. " + string.Join(Environment.NewLine, loaded.Warnings.Take(5));
        if (remember)
        {
            settings.RememberProject(loaded.Path);
            RecentProjectsList.ItemsSource = settings.RecentProjects;
            try { await settingsStore.SaveAsync(settings); }
            catch (Exception ex) { OperationStatus.Text += " Не удалось сохранить список проектов: " + ex.Message; }
        }
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
        if (method.ShowDialog() != true) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для нового проекта",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            string path = await ProjectCreator.CreateProjectAsync(name, dialog.FolderName, method.AuthoringMode, lifetime.Token);
            await OpenProjectAsync(path, remember: true);
            if (currentProject != null) await OpenScriptAsync(currentProject.MainScenePath);
        }, "Создание проекта…");
    }

    private async void BtnBack_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        if (!await SceneEditor.CanLeaveAsync()) return;
        SceneEditor.Clear();
        EditorWorkspace.Visibility = SceneEditor.Visibility = Visibility.Collapsed;
        EmbeddedPreview.Stop();
        FilesDataGrid.Visibility = Visibility.Visible;
        currentProject = null;
        FilesDataGrid.ItemsSource = null;
        ProjectFilesTree.ItemsSource = null;
        monitor?.Dispose(); monitor = null; monitoredPath = null;
        pendingRefresh = false;
        OperationStatus.Text = "";
        ShowPanel(WelcomePanel);
    }, "Закрытие проекта…");

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        TxtEditorPath.Text = settings.EditorPath;
        ShowPanel(SettingsPanel);
    }

    private async Task SaveSettingsAsync()
    {
        string editorPath = TxtEditorPath.Text.Trim().Trim('"');
        if (editorPath.Length > 0 && (!Path.IsPathFullyQualified(editorPath) ||
            !File.Exists(editorPath) || !Path.GetExtension(editorPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Укажите полный путь к существующему .exe редактора или оставьте поле пустым.");
        var next = new UserSettings { EditorPath = editorPath, RecentProjects = settings.RecentProjects };
        await settingsStore.SaveAsync(next);
        settings = next;
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
        if (sender is Button { Tag: string path })
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
        if (monitoredPath == path) return;
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

    private void BtnHelp_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, """
        1. Создайте проект или откройте папку с project.json.
        2. Сценарий game/scenes/main.krl должен объявлять main().
        3. Скрипт game/scripts/gamelogic.lua выполняется перед сценой.
        4. В project.json можно изменить entry_points.main_scene и init_script.
        5. d(имя или nil, текст) показывает реплику; «Далее» и пробел продолжают сцену.
        6. choice(вопрос, {"Ответ 1", "Ответ 2"}) возвращает индекс выбора с 1.
        7. background, show_character, play_music и play_sound используют пути от корня проекта.
        8. Переменные для сохранения храните в таблице state; доступны ручной слот и автосохранение.
        9. Поиск фильтрует дерево и таблицу; внешние изменения подхватываются автоматически.
        10. Для команд внизу окна объявите process_command(command).
        11. Lua работает в отдельном процессе. «Остановить» принудительно прерывает операцию;
            лимит — 30 секунд. Далее игра восстанавливается с последнего успешного действия.
        Примеры и ограничения сохранений описаны в README.md движка.
        """, "Помощь KNE");

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
                case "Посмотреть":
                    string details = await Task.Run(() =>
                    {
                        var info = new FileInfo(source);
                        return $"Имя: {info.Name}\nПуть: {info.FullName}\nРазмер: {info.Length:N0} байт\nИзменён: {info.LastWriteTime:g}\nАтрибуты: {info.Attributes}";
                    }, lifetime.Token);
                    if (!closed) MessageBox.Show(this, details, "Информация о файле");
                    break;
                case "Редактировать":
                    if (ScriptDocument.IsCodePath(source) || ScriptDocument.IsVisualPath(source))
                    {
                        await OpenScriptAsync(source);
                        break;
                    }
                    goto case "Внешний редактор";
                case "Внешний редактор":
                    using (Process.Start(EditorLauncher.CreateStartInfo(settings.EditorPath, source))) { }
                    break;
                case "Переименовать":
                    if (SceneEditor.Document?.FilePath.Equals(source, StringComparison.OrdinalIgnoreCase) == true)
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
                case "Удалить":
                    if (SceneEditor.Document?.FilePath.Equals(source, StringComparison.OrdinalIgnoreCase) == true)
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
            await SceneEditor.SaveAsync();
            // Re-read configuration and files changed by the external editor.
            await OpenProjectAsync(project.Path);
            if (closed || currentProject == null) return;
            _ = currentProject.MainScenePath;
            _ = currentProject.InitScriptPath;
            var game = new GameWindow(currentProject);
            game.Show();
        }, "Запуск проекта…");
    }

    private async Task OpenScriptAsync(string path)
    {
        if (currentProject == null || !await SceneEditor.CanLeaveAsync()) return;
        EmbeddedPreview.Open(currentProject);
        await SceneEditor.OpenAsync(currentProject.Path, path);
        FilesDataGrid.Visibility = Visibility.Collapsed;
        EditorWorkspace.Visibility = SceneEditor.Visibility = Visibility.Visible;
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

    private void OpenScript_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FilesDataGrid.SelectedItem is ProjectFile file && (ScriptDocument.IsCodePath(file.FilePath) || ScriptDocument.IsVisualPath(file.FilePath)))
            BtnOpenScript_Click(sender, e);
    }

    private void BtnFileList_Click(object sender, RoutedEventArgs e)
    {
        FilesDataGrid.Visibility = Visibility.Visible;
        EditorWorkspace.Visibility = SceneEditor.Visibility = Visibility.Collapsed;
        EmbeddedPreview.Stop();
    }

    private async void BtnNewVisualScene_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        var dialog = new SaveFileDialog
        {
            Title = "Создать визуальную сцену", InitialDirectory = project.Path,
            FileName = "scene.knegraph", Filter = "Блок-схема KNE (*.knegraph)|*.knegraph",
            DefaultExt = ".knegraph", AddExtension = true, OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            if (!await SceneEditor.CanLeaveAsync()) return;
            await ScriptDocument.CreateVisualAsync(project.Path, dialog.FileName, lifetime.Token);
            await OpenProjectAsync(project.Path);
            await OpenScriptAsync(dialog.FileName);
        }, "Создание блок-схемы…");
    }

    private void SceneEditor_RunRequested(object? sender, EventArgs e) => SceneEditor_PreviewRequested(sender, e);

    private void SceneEditor_PreviewRequested(object? sender, EventArgs e)
    {
        if (closed || currentProject == null) return;
        try { EmbeddedPreview.RequestSelection(); }
        catch (Exception error) { OperationStatus.Text = "Предпросмотр: " + error.Message; }
    }

    private async void SceneEditor_ExternalEditorRequested(object? sender, EventArgs e)
    {
        if (SceneEditor.Document is not { IsVisual: false } document) return;
        await RunOperationAsync(async () =>
        {
            await SceneEditor.SaveAsync();
            using (Process.Start(EditorLauncher.CreateStartInfo(settings.EditorPath, document.FilePath))) { }
        }, "Открытие внешнего редактора…");
    }

    private async void SceneEditor_MainSceneRequested(object? sender, EventArgs e)
    {
        if (currentProject is not { } project || SceneEditor.Document is not { } document) return;
        await RunOperationAsync(async () =>
        {
            await SceneEditor.SaveAsync();
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

    private async void SceneEditor_CloseRequested(object? sender, EventArgs e) => await RunOperationAsync(async () =>
    {
        if (!await SceneEditor.CanLeaveAsync()) return;
        SceneEditor.Clear();
        EditorWorkspace.Visibility = SceneEditor.Visibility = Visibility.Collapsed;
        EmbeddedPreview.Stop();
        FilesDataGrid.Visibility = Visibility.Visible;
    }, "Закрытие сценария…");
}
