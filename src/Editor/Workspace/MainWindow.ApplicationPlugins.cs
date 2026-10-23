using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private readonly List<string> applicationPluginDraft = [];
    private string? applicationPluginLoadError;
    private sealed record ApplicationPluginEntry(string Name, string Path, string Status);
    private void SetSettingsPage(int page)
    {
        bool plugins = page == 1;
        GeneralSettingsPage.Visibility = plugins ? Visibility.Collapsed : Visibility.Visible;
        PluginsSettingsPage.Visibility = plugins ? Visibility.Visible : Visibility.Collapsed;
        PreviousSettingsPageButton.IsEnabled = plugins;
        NextSettingsPageButton.IsEnabled = !plugins;
        SettingsPageLabel.Text = plugins ? "2 / 2 · Плагины" : "1 / 2 · Основные";
    }
    private void PreviousSettingsPage_Click(object sender, RoutedEventArgs e) => SetSettingsPage(0);
    private void NextSettingsPage_Click(object sender, RoutedEventArgs e) => SetSettingsPage(1);
    private void ApplicationPluginsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PluginNameText == null) return; // Selection may change while XAML is being initialized.
        PluginNameText.Text = "Выберите плагин";
        PluginAuthorText.Text = PluginCreatedAtText.Text = PluginLanguageText.Text = PluginReadmeText.Text = PluginLicenseText.Text = PluginMetadataStatus.Text = "";
        if (ApplicationPluginsList.SelectedItem is not ApplicationPluginEntry entry) return;
        PluginNameText.Text = entry.Name;
        PluginLanguageText.Text = Path.GetExtension(entry.Path).Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "Lua" : "C#";
        try
        {
            var metadata = PluginMetadata.Read(entry.Path);
            PluginNameText.Text = metadata.Name; PluginAuthorText.Text = metadata.Author;
            PluginCreatedAtText.Text = metadata.CreatedAt; PluginLanguageText.Text = metadata.Language;
            PluginReadmeText.Text = metadata.Readme; PluginLicenseText.Text = metadata.License;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { PluginMetadataStatus.Text = "Не удалось прочитать сведения о плагине: " + error.Message; }
    }
    private void ResetApplicationPluginDraft()
    {
        applicationPluginDraft.Clear(); applicationPluginDraft.AddRange(settings.ApplicationPlugins);
        RefreshApplicationPluginList();
    }
    private void RefreshApplicationPluginList()
    {
        string? selected = (ApplicationPluginsList.SelectedItem as ApplicationPluginEntry)?.Path;
        bool pending = !applicationPluginDraft.SequenceEqual(settings.ApplicationPlugins, StringComparer.OrdinalIgnoreCase);
        var entries = applicationPluginDraft.Select(path => new ApplicationPluginEntry(Path.GetFileName(path), path,
            !File.Exists(path) ? "Файл не найден" : !EngineLaunch.PluginsEnabled ? "Безопасный режим" : pending ? "Ожидает сохранения" : applicationPluginLoadError != null ? "Ошибка загрузки" : "Подключён")).ToArray();
        ApplicationPluginsList.ItemsSource = entries;
        ApplicationPluginsList.SelectedItem = entries.FirstOrDefault(entry => entry.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
        ApplicationPluginsStatus.Text = applicationPluginLoadError != null && !pending ? "Не удалось загрузить плагины: " + applicationPluginLoadError
            : entries.Length == 0 ? "Плагины не подключены. Добавьте файл .lua или .cs."
            : $"Подключено файлов: {entries.Length}." + (pending ? " Есть несохранённые изменения." : "");
    }
    private void AddApplicationPluginFiles(IEnumerable<string> paths)
    {
        // Validate the entire selection before changing the draft. Files are never copied or executed here.
        var additions = paths.Select(ApplicationPluginFiles.Validate).ToArray();
        var next = applicationPluginDraft.Concat(additions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (next.Length > ApplicationPluginFiles.MaximumCount) throw new ArgumentException("Можно подключить не более 64 плагинов KNE.");
        applicationPluginDraft.Clear(); applicationPluginDraft.AddRange(next);
        RefreshApplicationPluginList();
    }
    private async void AddApplicationPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Подключить плагины KNE", Filter = "Плагины KNE (*.lua;*.cs)|*.lua;*.cs|Lua (*.lua)|*.lua|C# (*.cs)|*.cs", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await RunOperationAsync(() => { AddApplicationPluginFiles(dialog.FileNames); return Task.CompletedTask; }, "Добавление плагинов…");
    }
    private void RemoveApplicationPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationPluginsList.SelectedItem is not ApplicationPluginEntry entry) return;
        int index = ApplicationPluginsList.SelectedIndex;
        applicationPluginDraft.RemoveAll(path => path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        RefreshApplicationPluginList();
        ApplicationPluginsList.SelectedIndex = Math.Min(index, ApplicationPluginsList.Items.Count - 1);
    }
    private void ReloadApplicationPlugins()
    {
        applicationPluginLoadError = null;
        if (currentProject != null) currentProject.ApplicationPlugins = settings.ApplicationPlugins.ToArray();
        UnloadEditorPlugins();
        try { LoadEditorPlugins(currentProject?.Path); }
        catch (Exception error) { applicationPluginLoadError = error.Message; OperationStatus.Text = "Не удалось загрузить плагины: " + error.Message; }
        ThemeInput.ItemsSource = EditorTheme.Themes; ThemeInput.SelectedValue = EditorTheme.Normalize(settings.Theme);
        EditorTheme.Apply(this, settings.Theme);
        RefreshApplicationPluginList();
    }
}
