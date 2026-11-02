using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private readonly DispatcherTimer automationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch sessionTime = Stopwatch.StartNew();
    internal EditorSessionClock SessionClock { get; } = new();
    private readonly EditorNotificationQueue notifications = new();
    private readonly WindowsEditorNotifications notificationDelivery = new();
    private readonly HashSet<string> pendingBackups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Window, (bool Enabled, string Title)> breakWindows = [];
    private readonly EditorSaveSchedule saveSchedule = new();
    private TimeSpan lastClockTick;
    private bool automationBusy, deliveringNotification;
    private string? automationProject;
    private ProjectSnapshots HistoryStore => new(Path.Combine(Path.GetDirectoryName(settingsStore.SettingsPath)!, "autosave"));
    private string BackupStorage(EditorPreferences preferences) => preferences.BackupPath == "" ? EditorPreferences.DefaultBackupPath(settings.ProjectsStoragePath, settingsStore.SettingsPath) : preferences.BackupPath;

    private void InitializeAutomation()
    {
        GeneralSettingsPage.ThemeProvider = () => (string?)ThemeInput.SelectedValue ?? settings.Theme;
        GeneralSettingsPage.BackupRequested = ManualBackupAsync;
        GeneralSettingsPage.HistoryRequested = OpenVersionHistoryAsync;
        GeneralSettingsPage.TestNotificationRequested = async () =>
        {
            var preferences = GeneralSettingsPage.Value;
            GeneralSettingsPage.Status.Text = await notificationDelivery.ShowAsync(new("test", "Тестовое уведомление KNE"), preferences with { Toasts = true }, lifetime.Token);
        };
        GeneralSettingsPage.ResetWorkRequested = () => { SessionClock.Reset(); lastClockTick = sessionTime.Elapsed; UpdateBreakUi(); GeneralSettingsPage.Status.Text = "Статистика текущего сеанса сброшена."; };
        automationTimer.Tick += async (_, _) => await AutomationTickAsync();
    }
    private void LoadGeneralSettings() => GeneralSettingsPage.Load(settings.Editor, settings.Hotkeys, EditorPreferences.DefaultBackupPath(settings.ProjectsStoragePath, settingsStore.SettingsPath));
    private void ApplyEditorPreferences()
    {
        EditorHotkeys.SetProfile(this, settings.Hotkeys);
        saveSchedule.Reset(sessionTime.Elapsed); UpdateBreakUi();
    }
    private void EditorSaved(ScriptEditor editor, bool changed)
    {
        if (currentProject == null || closed) return;
        if (changed) notifications.Add(new("saved", "Сохранён файл: " + Path.GetFileName(editor.Document?.FilePath)));
        QueueProjectBackup();
    }
    private void QueueProjectBackup() { if (!closed && currentProject != null && settings.Editor.BackupMinutes == -1) pendingBackups.Add(currentProject.Path); }
    private void ScheduleProjectAutomation(string path)
    {
        if (string.Equals(path, automationProject, StringComparison.OrdinalIgnoreCase)) return;
        automationProject = path; saveSchedule.Reset(sessionTime.Elapsed);
    }
    private async Task AutomationTickAsync()
    {
        if (closed) return;
        TimeSpan now = sessionTime.Elapsed;
        bool reminder = SessionClock.Advance(now - lastClockTick, settings.Editor); lastClockTick = now;
        if (reminder && !settings.Editor.StrictBreak) notifications.Add(new("break", $"Вы работаете уже {settings.Editor.BreakMinutes} минут. Пора сделать перерыв?"));
        UpdateBreakUi();
        if (!deliveringNotification) _ = DeliverNotificationsAsync();
        if (automationBusy || busy || checkingClose || SessionClock.IsOnBreak || OwnedWindows.Cast<Window>().Any(window => window.IsVisible) || OpenEditors.Any(editor => editor.IsSaving)) return;
        bool saveDue = currentProject != null && saveSchedule.TakeAutosaveDue(now, settings.Editor);
        bool backupDue = currentProject != null && saveSchedule.TakeBackupDue(now, settings.Editor);
        if (!saveDue && !backupDue && pendingBackups.Count == 0) return;
        automationBusy = true;
        await RunOperationAsync(async () =>
        {
            try
            {
                if (saveDue) await AutosaveOpenFilesAsync();
                if (backupDue && currentProject is { } periodic) await CreateBackupAsync(periodic.Path, settings.Editor);
                foreach (string path in pendingBackups.ToArray())
                { pendingBackups.Remove(path); if (settings.Editor.BackupMinutes == -1) await CreateBackupAsync(path, settings.Editor); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { OperationStatus.Text = "Автоматическое сохранение / копирование: " + error.Message; notifications.Add(new("error", OperationStatus.Text, Critical: true)); }
        }, "Автоматическое сохранение / копирование…");
        automationBusy = false;
    }

    internal async Task AutosaveOpenFilesAsync()
    {
        if (currentProject is not { } project) return;
        var editors = OpenEditors.Where(editor => editor.Document != null).ToArray();
        if (!editors.Any(editor => editor.HasUnsavedChanges)) return;
        string[] files = editors.Select(editor => editor.Document!.FilePath).ToArray();
        var history = HistoryStore;
        if (settings.Editor.KeepHistory) await history.CreateAsync(project.Path, "history", settings.Editor.AutosaveVersions, files, lifetime.Token);
        try { foreach (var editor in editors) await editor.SaveAsync(); }
        finally
        {
            // Preserve successful files even if a later tab hits an external-edit conflict.
            if (settings.Editor.KeepHistory && !closed) await history.CreateAsync(project.Path, "history", settings.Editor.AutosaveVersions, files, lifetime.Token);
        }
        OperationStatus.Text = $"Автосохранение: {editors.Length} файлов.";
    }
    private void ValidateBackupStorage(EditorPreferences preferences, string? projectsStorage = null)
    {
        string backupPath = BackupStorage(preferences);
        if (!Path.IsPathFullyQualified(backupPath)) throw new IOException("Укажите полный путь к папке резервных копий.");
        string backup = PathSafety.NormalizeDirectory(backupPath); PathSafety.RejectLinks(backup);
        string storage = projectsStorage ?? settings.ProjectsStoragePath;
        if (storage != "")
        {
            storage = PathSafety.NormalizeDirectory(storage);
            if (backup.Equals(storage, StringComparison.OrdinalIgnoreCase) || backup.StartsWith(storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Папка резервных копий должна находиться вне хранилища проектов.");
        }
        foreach (string project in settings.RecentProjects.Append(currentProject?.Path ?? "").Where(path => path != "")) _ = new ProjectSnapshots(backup).DirectoryFor(project, "backup");
    }
    private async Task CreateBackupAsync(string projectPath, EditorPreferences preferences)
    {
        ValidateBackupStorage(preferences);
        var snapshot = await new ProjectSnapshots(BackupStorage(preferences)).CreateAsync(projectPath, "backup", preferences.BackupCopies, token: lifetime.Token);
        if (snapshot != null) { OperationStatus.Text = "Резервная копия: " + snapshot.Archive; notifications.Add(new("backup", "Резервная копия проекта создана.")); }
    }
    private async Task<string?> ChooseSnapshotProjectAsync()
    {
        if (currentProject != null) return currentProject.Path;
        var dialog = new OpenFolderDialog { Title = "Выберите проект для резервной копии / истории", InitialDirectory = settings.ProjectsStoragePath };
        if (dialog.ShowDialog(this) != true) return null;
        return (await ProjectLoader.LoadProjectAsync(dialog.FolderName, lifetime.Token)).Path;
    }
    private async Task ManualBackupAsync()
    {
        var preferences = GeneralSettingsPage.Value;
        if (await ChooseSnapshotProjectAsync() is not { } path) return;
        await RunOperationAsync(async () =>
        {
            if (string.Equals(currentProject?.Path, path, StringComparison.OrdinalIgnoreCase)) await SaveAllEditorsAsync();
            await CreateBackupAsync(path, preferences); pendingBackups.Remove(path); GeneralSettingsPage.Status.Text = OperationStatus.Text;
        }, "Создание полной резервной копии…");
    }
    private async Task OpenVersionHistoryAsync()
    {
        if (await ChooseSnapshotProjectAsync() is not { } path) return;
        var project = await ProjectLoader.LoadProjectAsync(path, lifetime.Token); var store = HistoryStore;
        var versions = await Task.Run(() => store.List(path, "history"), lifetime.Token);
        var window = new VersionHistoryWindow(project.Title, versions, async snapshot =>
        {
            if (MessageBox.Show(this, "Заменить файлы проекта выбранной версией? Текущие файлы останутся в истории перед откатом.", "Откат автосохранения", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            if (currentProject?.Path == path && !await CanLeaveAllAsync()) return false;
            string[] opened = currentProject?.Path == path ? OpenEditors.Select(editor => editor.Document!.FilePath).ToArray() : [];
            string? selected = currentProject?.Path == path ? ActiveEditor.Document?.FilePath : null;
            await store.RestoreAsync(path, snapshot, lifetime.Token, settings.Editor.AutosaveVersions);
            if (currentProject?.Path == path)
            {
                ClearEditorTabs(); await OpenProjectAsync(path);
                foreach (string file in opened.Where(File.Exists)) await OpenScriptAsync(file);
                if (selected != null && File.Exists(selected)) await OpenScriptAsync(selected);
            }
            return true;
        }) { Owner = this };
        EditorTheme.Apply(window, settings.Theme); window.ShowDialog();
    }

    private async Task DeliverNotificationsAsync()
    {
        deliveringNotification = true;
        try
        {
            foreach (var notification in notifications.Drain(settings.Editor, WindowState == WindowState.Minimized, closed))
                await notificationDelivery.ShowAsync(notification, settings.Editor, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) OperationStatus.Text = "Уведомления Windows: " + error.Message; }
        finally { deliveringNotification = false; }
    }
    private void UpdateBreakUi()
    {
        WorkTimeStatus.Visibility = settings.Editor.ShowWorkTime ? Visibility.Visible : Visibility.Collapsed;
        WorkTimeStatus.Text = $"Время работы: {(int)SessionClock.WorkTime.TotalHours:00}:{SessionClock.WorkTime.Minutes:00}:{SessionClock.WorkTime.Seconds:00}";
        BreakOverlay.Visibility = SessionClock.IsOnBreak ? Visibility.Visible : Visibility.Collapsed;
        EditorMainSurface.IsEnabled = !SessionClock.IsOnBreak;
        BreakCountdown.Text = $"Перерыв · {(int)SessionClock.BreakRemaining.TotalMinutes:00}:{SessionClock.BreakRemaining.Seconds:00}";
        if (SessionClock.IsOnBreak)
        {
            foreach (Window window in OwnedWindows)
            {
                if (!breakWindows.ContainsKey(window)) breakWindows.Add(window, (window.IsEnabled, window.Title));
                window.IsEnabled = false; window.Title = BreakCountdown.Text + " · " + breakWindows[window].Title;
            }
        }
        else { foreach (var pair in breakWindows) { pair.Key.IsEnabled = pair.Value.Enabled; pair.Key.Title = pair.Value.Title; } breakWindows.Clear(); }
    }
}
