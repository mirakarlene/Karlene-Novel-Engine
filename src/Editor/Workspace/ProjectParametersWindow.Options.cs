using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

public sealed partial class ProjectParametersWindow
{
    internal ProjectMetadataEditor MetadataInput { get; }
    internal ComboBox ResolutionInput { get; } = GraphicsSettingsEditor.Choice(ProjectCreationOptions.Resolutions.Select(r => new GraphicsOption<string>(r.Id, r.Title)).Append(new("custom", "Своё разрешение…")).ToArray());
    internal TextBox CustomWidthInput { get; } = new() { Width = 72, MaxLength = 5 };
    internal TextBox CustomHeightInput { get; } = new() { Width = 72, MaxLength = 5 };
    internal ComboBox PaletteInput { get; } = GraphicsSettingsEditor.Choice(EditorTheme.BuiltInThemes.Select(r => new GraphicsOption<string>(r.Id, r.Label)).Concat([new("violet", "Фиолетовая (прежняя)"), new("custom", "Своя (сохранить цвета)")]).ToArray());
    internal ComboBox PreloadInput { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<int>(1, "1"), new(2, "2"), new(3, "3")]);
    internal TextBox RollbackInput { get; } = new();
    internal ComboBox AutosaveInput { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("dialogue", "После каждой реплики"), new("choice", "После каждого выбора"), new("actions", "Каждые N действий")]);
    internal TextBox AutosaveActionsInput { get; } = new();
    internal TextBox MenuMusicInput { get; } = new();
    internal ComboBox FileSystemInput { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("project", "Только папка проекта"), new("full", "Полный доступ")]);
    private readonly Dictionary<string, byte[]?> originalEngine = [];
    private static readonly string[] EditableFiles = ["performance.json", "window.json", "game.json", "theme.json", "screens.json", "security.json"];
    private void InitializeOptions(ProjectInfo project)
    {
        foreach (string name in EditableFiles) { string path = PathSafety.ResolveRelative(project.Path, "engine/" + name); originalEngine[path] = ProjectSettingsTransaction.Read(path); }
        var resolution = ProjectCreationOptions.Resolutions.FirstOrDefault(r => r.Width == project.Engine.Window.Width && r.Height == project.Engine.Window.Height);
        ResolutionInput.SelectedValue = resolution?.Id ?? "custom"; CustomWidthInput.Text = project.Engine.Window.Width.ToString(); CustomHeightInput.Text = project.Engine.Window.Height.ToString(); PaletteInput.SelectedValue = project.Engine.Theme.Palette;
        PreloadInput.SelectedValue = project.Engine.Performance.PreloadScenes; RollbackInput.Text = project.Engine.Game.RollbackDepth.ToString();
        AutosaveInput.SelectedValue = project.Engine.Game.AutoSaveInterval; AutosaveActionsInput.Text = project.Engine.Game.AutoSaveActions.ToString(); MenuMusicInput.Text = project.Engine.Game.MenuMusic ?? "";
        AutosaveInput.SelectionChanged += (_, _) => AutosaveActionsInput.IsEnabled = (string?)AutosaveInput.SelectedValue == "actions";
        AutosaveActionsInput.IsEnabled = project.Engine.Game.AutoSaveInterval == "actions";
    }
    private StackPanel ResolutionFields()
    {
        var panel = new StackPanel(); panel.Children.Add(ResolutionInput);
        var custom = new WrapPanel { Margin = new(0, 5, 0, 5) };
        custom.Children.Add(new TextBlock { Text = "Ширина", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 5, 0) }); custom.Children.Add(CustomWidthInput);
        custom.Children.Add(new TextBlock { Text = "Высота", VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 5, 0) }); custom.Children.Add(CustomHeightInput); panel.Children.Add(custom);
        void Refresh() => custom.Visibility = (string?)ResolutionInput.SelectedValue == "custom" ? Visibility.Visible : Visibility.Collapsed;
        ResolutionInput.SelectionChanged += (_, _) => Refresh(); Refresh(); return panel;
    }
    private DockPanel MusicRow()
    {
        var row = new DockPanel(); var browse = new Button { Content = "Выбрать…", Margin = new(8, 0, 0, 0), Padding = new(10, 5, 10, 5) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(MenuMusicInput);
        browse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Музыка (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a", InitialDirectory = project.Path }; if (dialog.ShowDialog(this) == true) MenuMusicInput.Text = Path.GetRelativePath(project.Path, dialog.FileName).Replace('\\', '/'); };
        return row;
    }
    private Border SecurityCard(ProjectSecuritySettings value)
    {
        var panel = new StackPanel(); panel.Children.Add(Heading("Безопасность"));
        AddSwitch(panel, "encrypt", "Шифровать сценарии в релизе", value.EncryptScripts);
        AddSwitch(panel, "release_debug", "Включить консоль отладки в релизе (F3)", value.ReleaseDebugConsole);
        AddSwitch(panel, "reports", "Генерировать отчёты при краше", value.CrashReports);
        AddSwitch(panel, "plugins", "Разрешить плагины (.lua / .cs)", value.AllowPlugins);
        AddSwitch(panel, "network", "Разрешить внешние запросы", value.AllowNetwork);
        FileSystemInput.SelectedValue = value.FileSystem; AddField(panel, "Доступ к файловой системе", FileSystemInput);
        panel.Children.Add(Hint("При ограничении сети или файловой системы C#-плагины запрещены. Lua использует ограниченный API KNE. Шифрование затрудняет извлечение; пароль игроку не требуется."));
        return Card(panel);
    }
    private bool Flag(string key) => switches[key].IsChecked == true;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, EngineSettings.JsonOptions), EngineSettings.JsonOptions)!;
    private async Task SaveAllOptionsAsync()
    {
        JsonObject draft = MetadataInput.Draft();
        var value = new ProjectPerformanceSettings { Graphics = GraphicsInput.Value, FpsCounter = Flag("fps"), FpsCorner = (string?)CornerInput.SelectedValue ?? "top_right", StatisticsOverlay = Flag("stats"), LoadingIndicator = Flag("loading"), DebugMode = Flag("debug"), Preloading = Flag("preload"), PreloadScenes = (int?)PreloadInput.SelectedValue ?? 3, UnloadPreviousScene = Flag("unload"), MaxAsyncTasks = (int?)AsyncTasksInput.SelectedValue ?? 128, AudioBuffer = (string?)AudioBufferInput.SelectedValue ?? "balanced", SavesPath = SavesInput.Text.Trim() };
        value.Validate(project.Path);
        var game = Copy(project.Engine.Game); game.AutoSave = Flag("autosave"); game.AutoSaveInterval = (string?)AutosaveInput.SelectedValue ?? "dialogue";
        if (!int.TryParse(RollbackInput.Text, out int rollback) || !int.TryParse(AutosaveActionsInput.Text, out int actions)) throw new InvalidDataException("Глубина отката и число действий должны быть целыми числами.");
        game.RollbackDepth = rollback; game.AutoSaveActions = actions; game.MenuMusic = MenuMusicInput.Text.Trim(); game.Validate(project.Path);
        if (game.MenuMusic != "" && (!File.Exists(ResourcePaths.Resolve(project.Path, game.MenuMusic, audio: true)) || Path.GetExtension(game.MenuMusic).ToLowerInvariant() is not (".wav" or ".mp3" or ".wma" or ".m4a"))) throw new InvalidDataException("Музыка — существующий WAV, MP3, WMA или M4A внутри проекта.");
        var window = Copy(project.Engine.Window); var size = ProjectCreationOptions.Resolutions.FirstOrDefault(r => r.Id == (string?)ResolutionInput.SelectedValue); if (size != null) { window.Width = size.Width; window.Height = size.Height; }
        if ((string?)ResolutionInput.SelectedValue == "custom")
        {
            if (!int.TryParse(CustomWidthInput.Text, out int width) || !int.TryParse(CustomHeightInput.Text, out int height) || width is < 640 or > 7680 || height is < 480 or > 4320) throw new InvalidDataException("Разрешение: ширина 640…7680, высота 480…4320.");
            window.Width = width; window.Height = height;
        }
        var theme = Copy(project.Engine.Theme); var screens = project.Engine.Screens.Clone();
        string palette = (string?)PaletteInput.SelectedValue ?? theme.Palette;
        if (palette != theme.Palette && palette != "custom") GamePalette.Apply(theme, screens, palette);
        EngineSettings.ValidateAppearance(project.Path, theme); screens.Validate();
        var security = new ProjectSecuritySettings { EncryptScripts = Flag("encrypt"), ReleaseDebugConsole = Flag("release_debug"), CrashReports = Flag("reports"), AllowPlugins = Flag("plugins"), AllowNetwork = Flag("network"), FileSystem = (string?)FileSystemInput.SelectedValue ?? "full" }; security.Validate();
        var build = BuildInput.Value; build.Validate();
        var updates = new Dictionary<string, byte[]>();
        foreach (var (name, data) in new (string, object)[] { ("performance.json", value), ("window.json", window), ("game.json", game), ("theme.json", theme), ("screens.json", screens), ("security.json", security) }) updates[PathSafety.ResolveRelative(project.Path, "engine/" + name)] = JsonSerializer.SerializeToUtf8Bytes(data, EngineSettings.JsonOptions);
        foreach (var file in originalEngine) ProjectBuildMetadata.CheckCurrent(file.Key, file.Value);
        string configPath = PathSafety.ResolveRelative(project.Path, "project.json"), buildPath = PathSafety.ResolveRelative(project.Path, "engine/build.json");
        ProjectBuildWrite? written = null;
        try
        {
            written = await ProjectBuildSettingsStore.SaveCheckedAsync(project.Path, build, BuildInput.InitialMetadata, update: current => MetadataInput.Merge(current, draft));
            await ProjectSettingsTransaction.CommitAsync(updates, originalEngine);
        }
        catch (Exception error)
        {
            if (written != null)
            {
                var failures = new List<Exception>();
                try { ProjectBuildMetadata.CheckCurrent(configPath, written.ProjectJson); await ProjectBuildMetadata.ReplaceAsync(configPath, written.OriginalProject); } catch (Exception rollbackError) { failures.Add(rollbackError); }
                try { ProjectBuildMetadata.CheckCurrent(buildPath, written.BuildJson); await ProjectBuildMetadata.ReplaceAsync(buildPath, written.OriginalBuild); } catch (Exception rollbackError) { failures.Add(rollbackError); }
                if (failures.Count > 0) throw new AggregateException("Не удалось отменить сохранение параметров.", new[] { error }.Concat(failures));
            }
            throw;
        }
        var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(written.ProjectJson, ProjectConfiguration.JsonOptions)!;
        project.Configuration.Metadata = configuration.Metadata; project.Configuration.GameInfo = configuration.GameInfo; project.Configuration.EntryPoints = configuration.EntryPoints; project.Configuration.AuthoringMode = configuration.AuthoringMode; project.Configuration.LegacyName = configuration.LegacyName; project.Configuration.AdditionalData = configuration.AdditionalData;
        BuildInput.Saved(written.Settings); MetadataInput.Saved(JsonNode.Parse(written.ProjectJson)!.AsObject());
        foreach (var item in updates) originalEngine[item.Key] = item.Value;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject node) { yield return node; foreach (var nested in Descendants(node)) yield return nested; }
    }
}
