using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    private readonly List<Action<ScriptNode>> extendedEdits = [];
    internal void FocusAddBlock() => AddBlockButton.Focus();

    /// <summary>Checks the block limit and the unique entry-point rule without modifying the graph.</summary>
    public bool CanAddBlock(ScriptNodeKind kind) => Document?.Graph is { } graph && BlockLibrary.Definitions.Any(entry => entry.Kind == kind) && graph.Nodes.Count < 500 &&
        ShaderLibrary.CanPlace(kind, graph.IsShader) && (kind != ScriptNodeKind.Start || graph.Nodes.All(node => node.Kind != ScriptNodeKind.Start));

    /// <summary>Creates a command at the center of the visible canvas, retaining the viewport and undo history.</summary>
    public void AddBlock(ScriptNodeKind kind, string? category = null)
    {
        ApplyProperties();
        if (!CanAddBlock(kind)) throw new InvalidOperationException(kind == ScriptNodeKind.Start ? "Блок «Начало» уже существует." : "Добавление блока недоступно: предел 500 блоков.");
        var node = BlockLibrary.Create(kind, category);
        node.IsShaderInvocation = kind == ScriptNodeKind.ShaderContainer && Document?.Graph?.IsShader == false;
        double centerX = (GraphScroll.ActualWidth / 2 - CanvasPan.X) / ZoomSlider.Value;
        double centerY = (GraphScroll.ActualHeight / 2 - CanvasPan.Y) / ZoomSlider.Value;
        node.X = Math.Clamp(centerX - 105, -1_000_000_000, 1_000_000_000);
        node.Y = Math.Clamp(centerY - 65, -1_000_000_000, 1_000_000_000);
        if (node.Kind == ScriptNodeKind.RegisterCharacter)
        {
            string id = "character"; int suffix = 2;
            while (Document!.Graph!.Nodes.Any(item => item.Character?.Id == id)) id = "character_" + suffix++;
            node.Character = node.Character! with { Id = id };
        }
        Remember(); Document!.Graph!.Nodes.Add(node); selectedIds.Clear(); selectedIds.Add(node.Id); selectedId = node.Id; Repaint();
        var card = cards[node.Id]; card.Measure(new Size(210, double.PositiveInfinity));
        node.Y = Math.Clamp(centerY - card.DesiredSize.Height / 2, -1_000_000_000, 1_000_000_000);
        Canvas.SetTop(card, node.Y); UpdateGraphExtent(); RenderLinks(); UpdateTitle();
        EditorStatus.Text = "Добавлен блок «" + node.Title + "». Соедините его выходы и настройте свойства.";
    }

    private void UpdateExtendedProperties(ScriptNode? node)
    {
        ExtendedProperties.Children.Clear(); extendedEdits.Clear();
        selectedSpeakerId = node?.SpeakerId ?? ""; UpdateSpeakerReference();
        if (node == null) return;
        if (node.Kind is ScriptNodeKind.RemoveLayerShaders or ScriptNodeKind.RemoveAllShaders) { AddShaderRemovalFields(node); return; }
        if (ShaderLibrary.IsShader(node.Kind)) { AddShaderFields(node); return; }
        AddNarrativeFields(node);
        AddFlowFields(node);
        AddCalculationFields(node);
        AddTextInputFields(node);
        AddChoiceFields(node);
        bool literal = node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.SetVariable or ScriptNodeKind.While or ScriptNodeKind.WaitCondition;
        foreach (var control in new UIElement[] { ValueTypeLabel, ValueTypeInput }) Visible(control, literal || node.Kind == ScriptNodeKind.Switch);
        foreach (var control in new UIElement[] { ValueLabel, ValueInput }) Visible(control, literal || node.Kind is ScriptNodeKind.CheckFlag or ScriptNodeKind.SetFlag);
        ValueTypeInput.IsEnabled = node.Kind is not (ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag);
        VariableLabel.Text = node.Kind switch { ScriptNodeKind.Async => "Сохранить ID веток в state", ScriptNodeKind.Join => "Переменная с ID веток", ScriptNodeKind.ForEach => "Переменная для элемента", ScriptNodeKind.TryCatch => "Переменная для текста ошибки", _ => "Переменная state" };
        if (node.Kind is ScriptNodeKind.MoveBackground or ScriptNodeKind.MoveCharacter or ScriptNodeKind.Animation or ScriptNodeKind.Shake or ScriptNodeKind.Flash or ScriptNodeKind.Camera or ScriptNodeKind.Timer or ScriptNodeKind.Pause or ScriptNodeKind.WaitCondition)
            Number("DurationInput", node.Kind == ScriptNodeKind.WaitCondition ? "Таймаут ожидания, секунды" : "Длительность, секунды", node.Duration, (value, number) => value.Duration = number);
        if (node.Kind is ScriptNodeKind.MoveBackground or ScriptNodeKind.MoveCharacter or ScriptNodeKind.Camera)
        {
            Number("OffsetXInput", "Смещение X", node.OffsetX, (value, number) => value.OffsetX = number);
            Number("OffsetYInput", "Смещение Y", node.OffsetY, (value, number) => value.OffsetY = number);
        }
        if (node.Kind is ScriptNodeKind.Animation or ScriptNodeKind.Shake or ScriptNodeKind.Camera or ScriptNodeKind.AudioChannel or ScriptNodeKind.MasterVolume)
            Number("AmountInput", node.Kind switch { ScriptNodeKind.Camera => "Масштаб камеры (1 = 100%)", ScriptNodeKind.AudioChannel or ScriptNodeKind.MasterVolume => "Громкость от 0 до 1", _ => "Сила эффекта" }, node.Amount, (value, number) => value.Amount = number);
        if (node.Kind == ScriptNodeKind.Random)
        {
            Number("MinimumInput", "От A (включительно)", node.Minimum, (value, number) => value.Minimum = number);
            Number("MaximumInput", "До B (включительно)", node.Maximum, (value, number) => value.Maximum = number);
        }
        if (node.Kind is ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.StopSound or ScriptNodeKind.AudioChannel)
            Choice("ChannelInput", "Аудиоканал", node.Channel, ["music", "sfx", "voice", "ambient"], (value, text) => value.Channel = text);
        if (node.Kind == ScriptNodeKind.Animation)
        {
            Choice("TargetInput", "Объект", node.Target, ["sprite", "background"], (value, text) => value.Target = text);
            Choice("AnimationInput", "Анимация", node.Text, ["pulse", "rotate", "fade_in", "fade_out", "none"], (value, text) => value.Text = text);
        }
        if (node.Kind == ScriptNodeKind.Flash) Text("FlashColorInput", "Цвет вспышки (#RRGGBB / #AARRGGBB)", node.Value, (value, text) => value.Value = text);
        if (node.Kind == ScriptNodeKind.ForEach) Text("IterationKeyInput", "Переменная для ключа / индекса", node.IterationKey, (value, text) => value.IterationKey = text);
        if (node.Kind == ScriptNodeKind.End)
        {
            var modes = new[] { ("Завершить ветку / вернуться", ScriptEndAction.Return), ("Главное меню", ScriptEndAction.Menu), ("Специальный экран", ScriptEndAction.Screen), ("Закрыть игру", ScriptEndAction.Exit) };
            var combo = new ComboBox { Name = "EndActionInput", Margin = new Thickness(0, 3, 0, 8), ItemsSource = modes.Select(mode => mode.Item1).ToArray(), SelectedIndex = Array.FindIndex(modes, mode => mode.Item2 == node.EndAction) };
            Add("После конца сцены", combo); combo.SelectionChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value => value.EndAction = modes[Math.Max(0, combo.SelectedIndex)].Item2);
            Text("EndScreenInput", "Название специального экрана (для выбранного действия)", node.Path, (value, text) => value.Path = text);
        }
        if (node.Kind == ScriptNodeKind.ShowScreen)
        {
            Number("ScreenOpacityInput", "Прозрачность экрана (0–1)", node.Opacity, (value, number) => value.Opacity = number);
            Choice("ScreenLayerInput", "Слой", node.ScreenLayer, ["main", "overlay"], (value, text) => value.ScreenLayer = text);
        }
        if (node.Kind is ScriptNodeKind.ConfigureDialogue or ScriptNodeKind.ConfigureButton) AddUiConfigurationFields(node);
        if (node.Kind == ScriptNodeKind.Script)
        {
            var edit = new Button { Name = "EditProjectScriptButton", Content = "Создать / редактировать скрипт…", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 10) };
            edit.Click += (_, _) => Guard(() => { ApplyProperties(); ProjectScriptRequested?.Invoke(this, EventArgs.Empty); }); ExtendedProperties.Children.Add(edit);
        }
        if (node.Kind is ScriptNodeKind.While or ScriptNodeKind.ForEach or ScriptNodeKind.Switch or ScriptNodeKind.TryCatch or ScriptNodeKind.Parallel or ScriptNodeKind.Async)
            ExtendedProperties.Children.Add(new TextBlock { Text = "Внутреннюю ветку подключите к выходу «Далее» блока или завершите через «Конец → Завершить ветку / вернуться». Циклы задаются блоком, обратную связь рисовать не нужно.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), Foreground = System.Windows.Media.Brushes.DimGray });
        if (node.Kind == ScriptNodeKind.Title)
            ExtendedProperties.Children.Add(new TextBlock { Text = "Титр остаётся до следующей команды «Титр». Пустой текст скрывает его. Добавьте паузу или реплику, чтобы игрок успел его прочитать.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        void Add(string label, Control control)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); ExtendedProperties.Children.Add(control);
        }
        void Text(string name, string label, string current, Action<ScriptNode, string> assign)
        {
            var input = new TextBox { Name = name, Text = current, Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, input.Text.Trim()));
        }
        void Number(string name, string label, double current, Action<ScriptNode, double> assign)
        {
            var input = new TextBox { Name = name, Text = current.ToString("R", CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.TextChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value => { if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new InvalidDataException(label + ": укажите конечное число с точкой."); assign(value, number); });
        }
        void Choice(string name, string label, string current, string[] options, Action<ScriptNode, string> assign)
        {
            var input = new ComboBox { Name = name, ItemsSource = options, SelectedItem = current, Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.SelectionChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, (string?)input.SelectedItem ?? current));
        }
    }

    private void ApplyExtendedProperties(ScriptNode node) { foreach (var edit in extendedEdits) edit(node); }
}
