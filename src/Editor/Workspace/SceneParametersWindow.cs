using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

/// <summary>Per-function scene parameters with stable file paths, draft switching and transactional source updates.</summary>
public sealed class SceneParametersWindow : Window
{
    private readonly ProjectInfo project;
    private ScenePerformanceDocument document;
    private IReadOnlyList<SceneDefinition> definitions;
    private readonly Dictionary<SceneDefinition, ScenePerformanceSettings> drafts = [];
    private readonly Dictionary<SceneDefinition, string> names = [];
    private readonly Dictionary<string, byte[]?> originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel options = new() { IsEnabled = false };
    private readonly TextBlock metrics = new() { TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new(0, 12, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button save = new() { Content = "Сохранить", Padding = new(16, 8, 16, 8), IsEnabled = false };
    private SceneDefinition? selected;
    private bool loading;
    internal Action<IReadOnlyList<string>>? ValidateWrites { get; set; }
    internal ComboBox SceneInput { get; } = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new(0, 8, 0, 12) };
    internal TextBox NameInput { get; } = new() { MaxLength = 128 };
    internal TextBox FunctionInput { get; } = new() { MaxLength = 128 };
    internal CheckBox InheritResolutionInput { get; } = Check("Как в проекте");
    internal TextBox WidthInput { get; } = new();
    internal TextBox HeightInput { get; } = new();
    internal ComboBox QualityInput { get; } = GraphicsSettingsEditor.Choice(ScenePerformanceSettings.Qualities);
    internal CheckBox PostProcessingInput { get; } = Check("Отключить постобработку (вспышки, тряска)");
    internal CheckBox HideShadersInput { get; } = Check("Не отображать шейдеры");
    internal TextBox MusicInput { get; } = new();
    internal CheckBox LoopMusicInput { get; } = Check("Зациклить музыку");
    internal CheckBox InheritVolumeInput { get; } = Check("Громкость как в проекте / настройках игрока");
    internal Slider VolumeInput { get; } = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    internal CheckBox StopMusicInput { get; } = Check("Остановить музыку при выходе из сцены");
    internal SceneTransitionEditor EntryTransitionInput { get; }
    internal SceneTransitionEditor ExitTransitionInput { get; }
    internal CheckBox AllowSavesInput { get; } = Check("Разрешить сохранения в этой сцене");
    internal CheckBox AutosaveInput { get; } = Check("Разрешить автосохранение");
    internal CheckBox CheckpointInput { get; } = Check("Создать точку сохранения при входе");
    internal TextBox StateInput { get; } = new() { AcceptsReturn = true, AcceptsTab = true, MinHeight = 130, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal CheckBox LimitDepthInput { get; } = Check("Ограничить глубину вызовов");
    internal TextBox DepthInput { get; } = new();
    internal CheckBox TriggersInput { get; } = Check("Разрешить клавиатурные и мышиные триггеры");
    public SceneParametersWindow(ProjectInfo project)
    {
        this.project = project; document = PerformanceSettingsStore.LoadScenes(project.Path); definitions = SceneDefinition.Read(project); CaptureOriginals();
        Title = "Параметры сцены · " + project.Title; Width = 700; Height = 530; MinWidth = 500; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        EntryTransitionInput = new(project); ExitTransitionInput = new(project);
        SceneInput.ItemsSource = Choices();
        var root = new DockPanel { Margin = new(24) };
        var header = new StackPanel(); header.Children.Add(new TextBlock { Text = "Выберите сцену: файл · функция (.krl / .knegraph)" }); header.Children.Add(SceneInput); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel(); footer.Children.Add(status); var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new(16, 8, 16, 8), Margin = new(8, 0, 0, 0) }; cancel.Click += (_, _) => Close();
        actions.Children.Add(save); actions.Children.Add(cancel); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var basic = Card("Основное"); Field(basic, "Название сцены", NameInput); Field(basic, "Имя функции", FunctionInput); basic.Children.Add(Hint("main() остаётся точкой запуска. Статические ссылки обновляются при сохранении; пути файлов остаются прежними. Вычисляемые имена требуют проверки в коде."));
        basic.Children.Add(InheritResolutionInput); Field(basic, "Ширина окна сцены", WidthInput); Field(basic, "Высота окна сцены", HeightInput);
        var graphics = Card("Графика"); Field(graphics, "Качество эффектов", QualityInput); graphics.Children.Add(PostProcessingInput); graphics.Children.Add(HideShadersInput); graphics.Children.Add(metrics);
        var audio = Card("Музыка"); var row = new DockPanel(); var browse = new Button { Content = "Выбрать…", Padding = new(10, 6, 10, 6), Margin = new(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(MusicInput); Field(audio, "Фоновая музыка сцены (необязательно)", row);
        browse.Click += (_, _) => { var dialog = new OpenFileDialog { InitialDirectory = project.Path, Filter = "Музыка (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a" }; if (dialog.ShowDialog(this) == true) MusicInput.Text = Path.GetRelativePath(project.Path, dialog.FileName).Replace('\\', '/'); };
        audio.Children.Add(LoopMusicInput); audio.Children.Add(InheritVolumeInput);
        var volumeRow = new DockPanel(); var volumeLabel = new TextBlock { MinWidth = 42, TextAlignment = TextAlignment.Right, Margin = new(8, 0, 0, 0) }; volumeLabel.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Value") { Source = VolumeInput, StringFormat = "{0:0}%" }); DockPanel.SetDock(volumeLabel, Dock.Right); volumeRow.Children.Add(volumeLabel); volumeRow.Children.Add(VolumeInput);
        Field(audio, "Громкость (0–100%)", volumeRow); audio.Children.Add(StopMusicInput); audio.Children.Add(Hint("Пустой файл сохраняет текущий трек. 100% сохраняет громкость игрока; 0% отключает звук музыки этой сцены."));
        var transitions = Card("Переходы"); Field(transitions, "При входе", EntryTransitionInput); Field(transitions, "При выходе", ExitTransitionInput); transitions.Children.Add(Hint("Fade: затемнение при выходе, проявление из чёрного при входе. Dissolve смешивает кадры. Свой эффект получает снимок и время от 0 до 1. Переходы не зависят от выключения вспышек и тряски."));
        var storage = Card("Сохранения"); storage.Children.Add(AllowSavesInput); storage.Children.Add(AutosaveInput); storage.Children.Add(CheckpointInput); storage.Children.Add(Hint("Запрет сохранений запрещает любую запись, но не загрузку. Отдельная точка входа создаётся на первой реплике, выборе или ожидании даже при отключённом автосохранении."));
        var execution = Card("Состояние и выполнение"); Field(execution, "Начальное состояние (JSON)", StateInput); execution.Children.Add(Hint("Указанные ключи state заменяются при новом call/goto; остальные сохраняются. Возврат из call не применяет JSON повторно.")); execution.Children.Add(LimitDepthInput); Field(execution, "Максимум дополнительных вложенных call (1–63)", DepthInput); execution.Children.Add(TriggersInput);
        root.Children.Add(new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        SceneInput.SelectionChanged += (_, _) => SelectScene();
        foreach (var checkbox in new[] { InheritResolutionInput, InheritVolumeInput, LimitDepthInput, AllowSavesInput }) { checkbox.Checked += (_, _) => Refresh(); checkbox.Unchecked += (_, _) => Refresh(); }
        save.Click += async (_, _) => { try { await SaveAsync(); DialogResult = true; } catch (Exception error) { status.Text = error.Message; } };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => { if (selected != null) metrics.Text = SceneMetrics.Describe(project.Path, selected.Path); }; Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop();
    }
    private GraphicsOption<string>[] Choices() => definitions.Select(scene => new GraphicsOption<string>(scene.Key, scene.Label)).ToArray();
    private void SelectScene()
    {
        if (loading) return;
        try { StoreSelection(); }
        catch (Exception error) { status.Text = error.Message; loading = true; SceneInput.SelectedValue = selected?.Key; loading = false; return; }
        selected = definitions.FirstOrDefault(scene => scene.Key == (string?)SceneInput.SelectedValue); save.IsEnabled = options.IsEnabled = selected != null;
        if (selected == null) return;
        LoadSelection(drafts.GetValueOrDefault(selected) ?? document.For(selected.Path, selected.Function)); status.Text = "";
    }
    private void LoadSelection(ScenePerformanceSettings value)
    {
        loading = true;
        NameInput.Text = value.Name; FunctionInput.Text = names.GetValueOrDefault(selected!, selected!.Function); FunctionInput.IsReadOnly = selected.Function == "main";
        InheritResolutionInput.IsChecked = value.WindowWidth == null; WidthInput.Text = (value.WindowWidth ?? project.Engine.Window.Width).ToString(); HeightInput.Text = (value.WindowHeight ?? project.Engine.Window.Height).ToString();
        QualityInput.SelectedValue = value.EffectsQuality; PostProcessingInput.IsChecked = !value.PostProcessing; HideShadersInput.IsChecked = value.HideShaders;
        MusicInput.Text = value.Music ?? ""; LoopMusicInput.IsChecked = value.LoopMusic; InheritVolumeInput.IsChecked = value.MusicVolume == null; VolumeInput.Value = value.MusicVolume ?? 100; StopMusicInput.IsChecked = value.StopMusicOnExit;
        EntryTransitionInput.Value = value.EntryTransition; ExitTransitionInput.Value = value.ExitTransition;
        AllowSavesInput.IsChecked = value.AllowSaves; AutosaveInput.IsChecked = value.AllowAutosave; CheckpointInput.IsChecked = value.CheckpointOnEntry;
        StateInput.Text = JsonSerializer.Serialize(value.InitialState, ProjectConfiguration.JsonOptions); LimitDepthInput.IsChecked = value.MaxCallDepth.HasValue; DepthInput.Text = (value.MaxCallDepth ?? 8).ToString(); TriggersInput.IsChecked = value.AllowTriggers;
        loading = false; Refresh(); metrics.Text = SceneMetrics.Describe(project.Path, selected.Path);
    }
    private void Refresh()
    {
        WidthInput.IsEnabled = HeightInput.IsEnabled = InheritResolutionInput.IsChecked != true; VolumeInput.IsEnabled = InheritVolumeInput.IsChecked != true; DepthInput.IsEnabled = LimitDepthInput.IsChecked == true;
        AutosaveInput.IsEnabled = CheckpointInput.IsEnabled = AllowSavesInput.IsChecked == true;
    }
    private static int Number(TextBox input, string name) => int.TryParse(input.Text, out int value) ? value : throw new InvalidDataException(name + ": укажите целое число.");
    private void StoreSelection()
    {
        if (selected == null) return;
        JsonElement initial;
        try { initial = JsonSerializer.Deserialize<JsonElement>(StateInput.Text); } catch (JsonException error) { throw new InvalidDataException("Начальное состояние: " + error.Message, error); }
        drafts[selected] = new()
        {
            Name = NameInput.Text.Trim(), WindowWidth = InheritResolutionInput.IsChecked == true ? null : Number(WidthInput, "Ширина"), WindowHeight = InheritResolutionInput.IsChecked == true ? null : Number(HeightInput, "Высота"),
            EffectsQuality = (string?)QualityInput.SelectedValue ?? "high", PostProcessing = PostProcessingInput.IsChecked != true, HideShaders = HideShadersInput.IsChecked == true,
            Music = MusicInput.Text.Trim(), LoopMusic = LoopMusicInput.IsChecked == true, MusicVolume = InheritVolumeInput.IsChecked == true ? null : (int)VolumeInput.Value, StopMusicOnExit = StopMusicInput.IsChecked == true,
            EntryTransition = EntryTransitionInput.Value, ExitTransition = ExitTransitionInput.Value, AllowSaves = AllowSavesInput.IsChecked == true, AllowAutosave = AutosaveInput.IsChecked == true, CheckpointOnEntry = CheckpointInput.IsChecked == true,
            InitialState = initial, MaxCallDepth = LimitDepthInput.IsChecked == true ? Number(DepthInput, "Глубина call") : null, AllowTriggers = TriggersInput.IsChecked == true
        };
        names[selected] = FunctionInput.Text.Trim();
    }
    internal async Task SaveAsync()
    {
        StoreSelection(); if (selected == null) throw new InvalidOperationException("Сначала выберите сцену.");
        foreach (var draft in drafts.Values) draft.Validate(project.Path);
        foreach (string shader in drafts.Values.SelectMany(d => new[] { d.EntryTransition, d.ExitTransition }).Where(t => t.Kind == "custom").Select(t => t.Shader!).Distinct()) _ = SceneShaderPresenter.LoadPrograms(project, [shader]);
        var renames = names.Where(pair => pair.Value != pair.Key.Function).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (renames.Count > 0)
        {
            var current = ProjectLoader.EnumerateFiles(project.Path, CancellationToken.None).Files.Where(Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!current.SetEquals(originals.Keys.Where(Source))) throw new IOException("Список сценариев изменён вне окна. Откройте параметры сцены заново.");
        }
        var updates = SceneRenaming.Plan(project, renames); ValidateWrites?.Invoke(updates.Keys.ToArray());
        var next = JsonSerializer.Deserialize<ScenePerformanceDocument>(JsonSerializer.Serialize(document, EngineSettings.JsonOptions), EngineSettings.JsonOptions)!;
        var moved = renames.Where(pair => next.Functions.TryGetValue(pair.Key.Path, out var file) && file.ContainsKey(pair.Key.Function)).ToDictionary(pair => pair.Key, pair => next.Functions[pair.Key.Path][pair.Key.Function]);
        foreach (var pair in renames) if (next.Functions.TryGetValue(pair.Key.Path, out var file)) file.Remove(pair.Key.Function);
        foreach (var pair in moved) next.Functions[pair.Key.Path][renames[pair.Key]] = pair.Value;
        foreach (var pair in drafts)
        {
            if (!next.Functions.TryGetValue(pair.Key.Path, out var functions)) next.Functions[pair.Key.Path] = functions = new(StringComparer.Ordinal);
            functions[names.GetValueOrDefault(pair.Key, pair.Key.Function)] = pair.Value;
        }
        next.Validate(project.Path); string filePath = PathSafety.ResolveRelative(project.Path, "engine/scene-performance.json"); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, EngineSettings.JsonOptions); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Параметры сцен больше 1 МБ."); updates[filePath] = bytes;
        await ProjectSettingsTransaction.CommitAsync(updates, originals);
        string key = selected.Path + "::" + names.GetValueOrDefault(selected, selected.Function); document = next; drafts.Clear(); names.Clear(); definitions = SceneDefinition.Read(project); CaptureOriginals();
        project.Engine.ScenePerformance.Scenes = next.Scenes; project.Engine.ScenePerformance.Functions = next.Functions;
        loading = true; SceneInput.ItemsSource = Choices(); SceneInput.SelectedValue = key; loading = false; selected = null; SelectScene();
    }
    private static bool Source(string path) => ScriptDocument.IsCodePath(path) || ScriptDocument.IsVisualPath(path);
    private void CaptureOriginals()
    {
        originals.Clear(); foreach (var file in project.Files.Where(f => Source(f.FilePath))) originals[file.FilePath] = ProjectSettingsTransaction.Read(file.FilePath);
        string sceneFile = PathSafety.ResolveRelative(project.Path, "engine/scene-performance.json"); originals[sceneFile] = ProjectSettingsTransaction.Read(sceneFile);
    }
    private StackPanel Card(string title)
    {
        var body = new StackPanel(); body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        var border = new Border { Child = body, Padding = new(14), CornerRadius = new(8), BorderThickness = new(1), Margin = new(0, 0, 0, 12) }; border.SetResourceReference(Border.BackgroundProperty, "Editor.Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Editor.Border"); options.Children.Add(border); return body;
    }
    private static CheckBox Check(string text) => new() { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new(0, 7, 0, 7) };
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 11, Margin = new(0, 5, 0, 10) };
    private static void Field(Panel panel, string label, FrameworkElement input) { panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); input.Margin = new(0, 5, 0, 10); panel.Children.Add(input); }
}

internal sealed class SceneTransitionEditor : StackPanel
{
    internal ComboBox KindInput { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("none", "Нет"), new("fade", "Fade out"), new("dissolve", "Dissolve"), new("custom", "Свой эффект")]);
    internal ComboBox ShaderInput { get; } = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new(0, 5, 0, 5) };
    internal ComboBox DurationInput { get; } = new() { IsEditable = true, ItemsSource = new[] { "0.5", "1.0", "1.5", "2.0" }, Text = "1.0", Margin = new(0, 5, 0, 5) };
    internal SceneTransitionEditor(ProjectInfo project)
    {
        ShaderInput.ItemsSource = project.Files.Where(f => ScriptDocument.IsShaderPath(f.FilePath)).Select(f => { string path = Path.GetRelativePath(project.Path, f.FilePath).Replace('\\', '/'); return new GraphicsOption<string>(path, path); }).ToArray();
        Children.Add(KindInput); Children.Add(ShaderInput); Children.Add(new TextBlock { Text = "Длительность (секунды)" }); Children.Add(DurationInput); KindInput.SelectionChanged += (_, _) => Refresh(); KindInput.SelectedValue = "none";
    }
    private void Refresh() { ShaderInput.Visibility = (string?)KindInput.SelectedValue == "custom" ? Visibility.Visible : Visibility.Collapsed; DurationInput.IsEnabled = (string?)KindInput.SelectedValue != "none"; }
    internal SceneTransitionSettings Value
    {
        get
        {
            if (!double.TryParse(DurationInput.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double duration)) throw new InvalidDataException("Длительность перехода должна быть числом.");
            return new() { Kind = (string?)KindInput.SelectedValue ?? "none", Duration = duration, Shader = (string?)ShaderInput.SelectedValue };
        }
        set { KindInput.SelectedValue = value.Kind; DurationInput.Text = value.Duration.ToString(System.Globalization.CultureInfo.InvariantCulture); ShaderInput.SelectedValue = value.Shader; Refresh(); }
    }
}
