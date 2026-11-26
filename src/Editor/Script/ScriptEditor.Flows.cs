using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class ScriptEditor
{
    /// <summary>Requests opening or creating the referenced nested graph in a workspace tab.</summary>
    public event EventHandler<string>? SubsystemRequested;
    private TextBox? capturingKeys;
    private bool captureCombination;
    private readonly HashSet<Key> captureHeld = [];
    private readonly HashSet<string> capturedKeys = [];

    internal void SetSubsystemPath(string nodeId, string relative)
    {
        if (Document?.Graph?.Nodes.SingleOrDefault(node => node.Id == nodeId && node.Kind is ScriptNodeKind.Subsystem or ScriptNodeKind.ShaderContainer) is not { } node) return;
        Remember(); node.Path = relative; Repaint();
    }
    internal void OpenSubsystem(string nodeId) => Guard(() => { ApplyProperties(); SubsystemRequested?.Invoke(this, nodeId); });

    private void AddFlowFields(ScriptNode node)
    {
        if (node.Kind == ScriptNodeKind.Anchor)
            Text("AnchorNameInput", "Имя якоря", node.Text, (value, text) => value.Text = text.Trim());
        if (node.Kind == ScriptNodeKind.SendFlow)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = "Якорь в этой сцене" });
            var anchors = Document!.Graph!.Nodes.Where(candidate => candidate.Kind == ScriptNodeKind.Anchor)
                .Select(candidate => new { Id = candidate.Id, Name = candidate.Text }).ToArray();
            var target = new ComboBox { Name = "FlowAnchorInput", ItemsSource = anchors, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = node.Target, Margin = new(0, 3, 0, 8) };
            target.SelectionChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(target);
            extendedEdits.Add(value => value.Target = target.SelectedValue as string ?? "");
            Hint("Переход разрешён только к якорю своего потока. Якоря других параллельных веток и сцен недоступны.");
        }
        if (node.Kind == ScriptNodeKind.Subsystem)
        {
            Text("SubsystemPathInput", "Файл .knegraph от корня проекта", node.Path, (value, text) => value.Path = text.Trim());
            Check("WaitForSubsystemInput", "Ждать полного завершения подсистемы", node.WaitForSubsystem, (value, flag) => value.WaitForSubsystem = flag);
            var open = new Button { Name = "OpenSubsystemButton", Content = "Открыть / создать подсистему…", Margin = new Thickness(0, 0, 0, 10) };
            open.Click += (_, _) => OpenSubsystem(node.Id); ExtendedProperties.Children.Add(open);
            Hint("Двойной клик по карточке открывает файл во вкладке. Конец подсистемы возвращает в родительскую схему. В фоне реплики пишутся в консоль, как у блока «Асинхронность».");
        }
        if (!node.IsTrigger) return;
        var settings = node.Trigger ?? new TriggerSettings();
        if (node.Kind is ScriptNodeKind.TriggerKey or ScriptNodeKind.TriggerCombination)
        {
            var input = Text("TriggerKeysInput", "Клавиша / сочетание", settings.Keys, (value, text) => value.Trigger = (value.Trigger ?? settings) with { Keys = TriggerSettings.NormalizeKeys(text) });
            var choose = new Button { Name = "CaptureTriggerKeysButton", Content = "Нажать нужную клавишу…", Margin = new Thickness(0, 0, 0, 10) };
            choose.Click += (_, _) =>
            {
                capturingKeys = input; captureCombination = node.Kind == ScriptNodeKind.TriggerCombination;
                captureHeld.Clear(); capturedKeys.Clear();
                KeyCaptureText.Text = captureCombination ? "Нажмите сочетание клавиш, затем отпустите все клавиши." : "Нажмите нужную клавишу.";
                KeyCaptureOverlay.Visibility = Visibility.Visible; KeyCaptureOverlay.Focus();
            };
            ExtendedProperties.Children.Add(choose);
        }
        if (node.Kind == ScriptNodeKind.TriggerButton)
        {
            Text("TriggerButtonInput", "ID кнопки: save, load, auto_save, pause, rollback, history или screen:Имя/ID", settings.Button, (value, text) => value.Trigger = (value.Trigger ?? settings) with { Button = text.Trim() });
            var buttons = new List<string> { "save", "load", "auto_save", "pause", "rollback", "history", "choice:1", "choice:2", "input_submit", "input_cancel" };
            try
            {
                buttons.AddRange(EngineSettings.Load(projectRoot).Screens.Screens.SelectMany(screen => screen.Elements.Where(element => element.Type == "button").Select(element => "screen:" + screen.Name + "/" + element.Id)));
            }
            catch (Exception error) when (error is IOException or ArgumentException) { }
            var picker = new ComboBox { Name = "TriggerButtonPicker", ItemsSource = buttons.Distinct().ToArray(), Margin = new Thickness(0, 0, 0, 10) };
            picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is string id) ExtendedProperties.Children.OfType<TextBox>().Single(input => input.Name == "TriggerButtonInput").Text = id; };
            ExtendedProperties.Children.Add(picker);
        }
        if (node.Kind is ScriptNodeKind.TriggerHotspot or ScriptNodeKind.TriggerPointerEnterZone)
        {
            Number("TriggerXInput", "X зоны", settings.X, (value, number) => value.Trigger = (value.Trigger ?? settings) with { X = number });
            Number("TriggerYInput", "Y зоны", settings.Y, (value, number) => value.Trigger = (value.Trigger ?? settings) with { Y = number });
            Number("TriggerWidthInput", "Ширина зоны", settings.Width, (value, number) => value.Trigger = (value.Trigger ?? settings) with { Width = number });
            Number("TriggerHeightInput", "Высота зоны", settings.Height, (value, number) => value.Trigger = (value.Trigger ?? settings) with { Height = number });
            Hint("Координаты задаются в игровой области, а не во внешнем окне. Правая и нижняя границы не входят в зону.");
        }
        if (node.Kind == ScriptNodeKind.TriggerSequence)
        {
            Text("TriggerSequenceInput", "Клавиши по порядку, через запятую", settings.Keys,
                (value, text) => value.Trigger = (value.Trigger ?? settings) with { Keys = TriggerSettings.NormalizeSequence(text) });
            Hint("Например ↑, ↑, ↓, ↓, ←, →, ←, →, B, A. Удержание клавиши не добавляет повторных нажатий.");
        }
        if (node.Kind is ScriptNodeKind.TriggerDoubleClick or ScriptNodeKind.TriggerGesture)
            Choice("TriggerMouseButton", "Кнопка мыши", settings.EffectiveMouseButton,
                [new("left", "Левая"), new("right", "Правая"), new("middle", "Средняя")],
                (value, selected) => value.Trigger = (value.Trigger ?? settings) with { MouseButton = selected });
        if (node.Kind == ScriptNodeKind.TriggerDoubleClick)
            Number("TriggerClickInterval", "Интервал между кликами, мс", settings.EffectiveDoubleClickMilliseconds,
                (value, number) => value.Trigger = (value.Trigger ?? settings) with { DoubleClickMilliseconds = number });
        if (node.Kind == ScriptNodeKind.TriggerLongPress)
        {
            Choice("TriggerHoldInput", "Удерживать", settings.MouseButton ?? "key",
                [new("key", "Клавишу"), new("left", "Левую кнопку мыши"), new("right", "Правую кнопку мыши")],
                (value, selected) => value.Trigger = (value.Trigger ?? settings) with { MouseButton = selected == "key" ? null : selected });
            Text("TriggerHoldKey", "Клавиша, если выбрана клавиатура", settings.Keys,
                (value, text) => value.Trigger = (value.Trigger ?? settings) with
                { Keys = (value.Trigger ?? settings).MouseButton == null ? TriggerSettings.NormalizeSingleKey(text) : text.Trim() });
            Number("TriggerHoldDuration", "Время удержания, с", settings.EffectiveHoldSeconds,
                (value, number) => value.Trigger = (value.Trigger ?? settings) with { HoldSeconds = number });
        }
        GraphicsOption<string>[] modes = node.Kind switch
        {
            ScriptNodeKind.TriggerWheel => [new("up", "Вверх"), new("down", "Вниз"), new("both", "В обоих направлениях")],
            ScriptNodeKind.TriggerFocus => [new("gained", "Получение фокуса"), new("lost", "Потеря фокуса"), new("both", "Оба события")],
            ScriptNodeKind.TriggerResize => [new("changing", "Каждый кадр изменения"), new("finished", "Только после завершения изменения")],
            ScriptNodeKind.TriggerClipboard => [new("copy", "Копирование (Ctrl+C)"), new("paste", "Вставка (Ctrl+V)"), new("cut", "Вырезание (Ctrl+X)")],
            ScriptNodeKind.TriggerGesture => [new("circle", "Круг"), new("up", "Линия вверх"), new("down", "Линия вниз"), new("left", "Линия влево"), new("right", "Линия вправо")],
            _ => []
        };
        if (modes.Length > 0)
            Choice("TriggerEventMode", "Когда срабатывает", settings.EffectiveMode, modes,
                (value, selected) => value.Trigger = (value.Trigger ?? settings) with { Mode = selected });
        Check("TriggerOnceInput", "Сработать только один раз", settings.Once, (value, flag) => value.Trigger = (value.Trigger ?? settings) with { Once = flag });
        Hint("Вход подключён: ветка ждёт событие, затем выполняет блоки после выхода. Без входящей связи: независимый обработчик регистрируется при входе в сцену и действует до выхода из неё. Занятый обработчик повторно не запускается.");

        TextBox Text(string name, string label, string current, Action<ScriptNode, string> assign)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Name = name, Text = current, Margin = new Thickness(0, 3, 0, 8) }; ExtendedProperties.Children.Add(input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, input.Text)); return input;
        }
        void Number(string name, string label, double current, Action<ScriptNode, double> assign) => Text(name, label, current.ToString("R", CultureInfo.InvariantCulture), (value, text) =>
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) throw new InvalidDataException(label + ": укажите конечное число.");
            assign(value, number);
        });
        void Check(string name, string label, bool current, Action<ScriptNode, bool> assign)
        {
            var input = new CheckBox { Name = name, Content = label, IsChecked = current, Margin = new Thickness(0, 4, 0, 8) };
            input.Checked += (_, _) => TrackProperties(); input.Unchecked += (_, _) => TrackProperties();
            ExtendedProperties.Children.Add(input); extendedEdits.Add(value => assign(value, input.IsChecked == true));
        }
        void Choice(string name, string label, string current, GraphicsOption<string>[] options, Action<ScriptNode, string> assign)
        {
            var input = GraphicsSettingsEditor.Choice(options);
            input.Name = name;
            input.SelectedValue = current;
            input.Margin = new(0, 3, 0, 8);
            input.SelectionChanged += (_, _) => TrackProperties();
            ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            ExtendedProperties.Children.Add(input);
            extendedEdits.Add(value => assign(value, (string?)input.SelectedValue ?? current));
        }
        void Hint(string text) => ExtendedProperties.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
    }

    private void CloseKeyCapture()
    {
        capturingKeys = null; captureHeld.Clear(); capturedKeys.Clear();
        if (KeyCaptureOverlay != null) KeyCaptureOverlay.Visibility = Visibility.Collapsed;
    }
    private void CancelKeyCapture_Click(object sender, RoutedEventArgs e) => CloseKeyCapture();
    private void CaptureKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        captureHeld.Add(key); capturedKeys.Add(key.ToString());
        if (captureCombination)
            foreach (var modifier in new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") })
                if (Keyboard.Modifiers.HasFlag(modifier.Item1)) capturedKeys.Add(modifier.Item2);
        KeyCaptureText.Text = string.Join(" + ", capturedKeys);
        e.Handled = true;
        if (!captureCombination) FinishKeyCapture();
    }
    private void Editor_KeyUp(object sender, KeyEventArgs e)
    {
        if (capturingKeys == null) return;
        captureHeld.Remove(e.Key == Key.System ? e.SystemKey : e.Key); e.Handled = true;
        if (captureHeld.Count == 0) FinishKeyCapture();
    }
    private void FinishKeyCapture()
    {
        try { if (capturingKeys != null) capturingKeys.Text = TriggerSettings.NormalizeKeys(string.Join('+', capturedKeys)); }
        catch (ArgumentException error) { EditorStatus.Text = error.Message; }
        CloseKeyCapture();
    }
}
