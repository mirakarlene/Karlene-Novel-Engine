using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace KNE;

/// <summary>Code editing and visual scene authoring, with graph undo and generated-code preview.</summary>
public partial class ScriptEditor : UserControl
{
    /// <summary>The currently open source, or null when no scene is open.</summary>
    public ScriptDocument? Document { get; private set; }

    /// <summary>Requests a saved preview of the currently open scene.</summary>
    public event EventHandler? RunRequested;

    /// <summary>Requests persisting this scene as the project entry point.</summary>
    public event EventHandler? MainSceneRequested;

    /// <summary>Requests closing this document with an unsaved-change check.</summary>
    public event EventHandler? CloseRequested;

    public event EventHandler? PreviewRequested;
    public event EventHandler? ExternalEditorRequested;
    public event EventHandler? TestSceneRequested;
    /// <summary>Requests the block library overlay in the owning main window.</summary>
    public event EventHandler? BlockLibraryRequested;
    /// <summary>Requests creating or opening the selected project script in the configured external editor.</summary>
    public event EventHandler? ProjectScriptRequested;

    /// <summary>Captures unsaved edits for an isolated editor worker.</summary>
    public EditorSelection? CapturePreview() => CapturePreview(false);
    public EditorSelection? CapturePreview(bool sceneTest)
    {
        if (Document == null) return null;
        ApplyProperties();
        var graph = Document.Graph;
        if (graph != null)
            return new(Document.FilePath, graph.Compile(editorPreview: !sceneTest), 1,
                Selected?.Id ?? graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Start).Id,
                Selected?.PreviewAnimation == true, graph.FunctionName);
        int line = 1;
        for (int index = 0; index < Math.Min(CodeEditor.CaretIndex, Document.Code.Length); index++)
            if (Document.Code[index] == '\n') line++;
        var functions = LuaSource.Functions(Document.Code);
        string? function = functions.LastOrDefault(f => f.Line <= line)?.Name ?? functions.FirstOrDefault()?.Name;
        return new(Document.FilePath, Document.Code, line, null, false, function);
    }

    private void Code_SelectionChanged(object? sender, EventArgs e)
    {
        if (!updating && Document?.IsVisual == false) PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExternalEditor_Click(object sender, RoutedEventArgs e) => ExternalEditorRequested?.Invoke(this, EventArgs.Empty);

    private string projectRoot = "";
    private FrameCatalog? frameCatalog;
    private bool updating;
    private bool saving;
    private bool propertyDraft;
    private string? selectedId;
    private (string Id, int Port)? pendingConnection;
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly Dictionary<string, Border> cards = new(StringComparer.Ordinal);
    private readonly List<UIElement> links = [];

    /// <summary>Creates the editing controls and command palette.</summary>
    public ScriptEditor()
    {
        InitializeComponent();
        InitializeGraphSelection();
        ValueTypeInput.ItemsSource = new[]
        {
            new { Label = "Число", Value = ScriptValueKind.Number },
            new { Label = "Строка", Value = ScriptValueKind.String },
            new { Label = "Логическое", Value = ScriptValueKind.Boolean }
        };
        ComparisonInput.ItemsSource = Enum.GetValues<ScriptComparison>().Select((value, index) =>
            new { Label = new[] { "=", "≠", ">", "<", "≥", "≤" }[index], Value = value }).ToArray();
        foreach (var input in new[]
                     { SpeakerInput, SceneTextInput, OptionsInput, PathInput, VariableInput, ValueInput, FunctionNameInput })
            input.TextChanged += (_, _) => TrackProperties();
        foreach (var input in new[] { PositionInput, ValueTypeInput, ComparisonInput })
            input.SelectionChanged += (_, _) => TrackProperties();
        SpeakerInput.TextChanged += (_, _) =>
        {
            if (!updating)
            {
                selectedSpeakerId = "";
                if (SpeakerInput.Text.Length > 0 && ExtendedProperties.Children.OfType<CheckBox>().FirstOrDefault(control => control.Name == "ExplicitNarratorInput") is { } narrator)
                    narrator.IsChecked = false;
                UpdateSpeakerReference();
            }
        };
        LoopInput.Checked += (_, _) => TrackProperties();
        LoopInput.Unchecked += (_, _) => TrackProperties();
    }

    private void TrackProperties()
    {
        if (updating || Selected == null) return;
        propertyDraft = true;
        UpdateTitle();
    }

    /// <summary>Opens a scene after the caller has resolved unsaved changes.</summary>
    /// <param name="root">The project root directory.</param>
    /// <param name="path">Absolute path of the selected scenario.</param>
    /// <returns>A task that completes after both editing views are initialized.</returns>
    public async Task OpenAsync(string root, string path)
    {
        CloseKeyCapture();
        CloseQuickSearch();
        CloseCharacterPicker();
        FinishMarquee();
        ClearCanvasInteraction();
        var document = await ScriptDocument.OpenAsync(root, path);
        EngineDiagnostics.Trace("Открытие сценария", path);
        projectRoot = root;
        Document = document;
        frameCatalog = null;
        try
        {
            var project = await ProjectLoader.LoadProjectAsync(root);
            frameCatalog = await Task.Run(() => FrameCatalog.Load(project));
        }
        catch (Exception) { /* Invalid drafts must remain editable; runtime and analysis report details. */ }
        undo.Clear();
        redo.Clear();
        selectedId = null;
        selectedIds.Clear();
        pendingConnection = null;
        CanvasPan.X = CanvasPan.Y = 0;
        updating = true;
        CodeEditor.Text = document.IsVisual ? "" : document.Code;
        CodeEditor.IsReadOnly = document.IsVisual;
        CodeMode.IsEnabled = true;
        GraphMode.IsEnabled = document.IsVisual;
        GraphMode.ToolTip = document.IsVisual
            ? null
            : "Произвольный Lua/KRL не преобразуется в блоки. Создайте визуальную сцену.";
        ExportButton.IsEnabled = document.IsVisual;
        SaveButton.IsEnabled = RunButton.IsEnabled = TestSceneButton.IsEnabled = MainSceneButton.IsEnabled = CloseDocumentButton.IsEnabled = true;
        if (document.IsVisual)
        {
            GraphMode.IsChecked = true;
            CodeMode.IsChecked = false;
        }
        else
        {
            CodeMode.IsChecked = true;
            GraphMode.IsChecked = false;
        }

        updating = false;
        ShowMode();
        RenderGraph();
        UpdateProperties();
        UpdateTitle();
        EditorStatus.Text = document.IsVisual
            ? "Сохранение допускает незавершённую схему. Код и запуск проверяют все связи."
            : "Lua/KRL · Tab — 4 пробела · Shift+Tab — убрать отступ · Ctrl+S — сохранить.";
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Resets the editor when leaving a project, after checking unsaved changes.</summary>
    public void Clear()
    {
        CloseKeyCapture();
        CloseQuickSearch();
        CloseCharacterPicker();
        FinishMarquee();
        ClearCanvasInteraction();
        updating = true;
        Document = null;
        frameCatalog = null;
        selectedId = null;
        selectedIds.Clear();
        pendingConnection = null;
        propertyDraft = false;
        undo.Clear();
        redo.Clear();
        CodeEditor.Clear();
        GraphCanvas.Children.Clear();
        cards.Clear();
        links.Clear();
        CodeMode.IsEnabled = GraphMode.IsEnabled = SaveButton.IsEnabled = RunButton.IsEnabled =
            TestSceneButton.IsEnabled = MainSceneButton.IsEnabled = ExportButton.IsEnabled = CloseDocumentButton.IsEnabled = false;
        CodeMode.IsChecked = GraphMode.IsChecked = false;
        CodeEditor.Visibility = GraphPanel.Visibility = Visibility.Collapsed;
        ExternalEditorButton.Visibility = Visibility.Collapsed;
        PropertiesPanel.IsEnabled = false;
        EditorStatus.Text = "";
        updating = false;
        UpdateTitle();
    }

    internal Func<string, MessageBoxResult>? ConfirmSave { get; set; }
    /// <summary>Includes both applied document edits and unapplied property drafts.</summary>
    public bool HasUnsavedChanges => Document?.IsDirty == true || propertyDraft;

    /// <summary>Prompts to save or discard edits before closing the document.</summary>
    /// <returns>True when the caller may leave; false when the user cancels.</returns>
    public async Task<bool> CanLeaveAsync()
    {
        if (saving) throw new InvalidOperationException("Дождитесь завершения сохранения.");
        if (Document == null || (!Document.IsDirty && !propertyDraft)) return true;
        var answer = ConfirmSave?.Invoke(Document.FilePath) ?? MessageBox.Show(Window.GetWindow(this), "Сохранить изменения в сценарии «" + System.IO.Path.GetFileName(Document.FilePath) + "»?",
            "Несохранённые изменения",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        await SaveAsync();
        return true;
    }

    /// <summary>Applies property edits and persists the current scene.</summary>
    /// <returns>A task that completes after saving, or faults on a conflict.</returns>
    public async Task SaveAsync()
    {
        if (Document == null) return;
        if (saving) throw new InvalidOperationException("Сохранение ещё не завершено.");
        saving = true;
        IsEnabled = false;
        try
        {
            ApplyProperties();
            await Document.SaveAsync();
            EngineDiagnostics.Trace("Сценарий сохранён", Document.FilePath);
            UpdateTitle();
            EditorStatus.Text = "Сценарий сохранён.";
            try
            {
                frameCatalog = await FrameCatalog.SynchronizeAsync(await ProjectLoader.LoadProjectAsync(projectRoot));
                RenderGraph(); UpdateProperties();
            }
            catch (Exception error) { EditorStatus.Text += " ID будут обновлены после исправления проекта: " + error.Message; }
        }
        finally
        {
            saving = false;
            IsEnabled = true;
        }
    }

    /// <summary>Notifies workspace tabs when a draft or its saved baseline changes.</summary>
    public event EventHandler? DocumentStateChanged;

    private void UpdateTitle()
    {
        DocumentStateChanged?.Invoke(this, EventArgs.Empty);
        DocumentTitle.Text = Document == null
            ? "Выберите сценарий в дереве и нажмите «Открыть сценарий»."
            : System.IO.Path.GetRelativePath(projectRoot, Document.FilePath) +
              (Document.IsDirty || propertyDraft ? " *" : "");
        UndoButton.IsEnabled = Document != null && (Document.IsVisual ? undo.Count > 0 || propertyDraft : CodeEditor.CanUndo);
        RedoButton.IsEnabled = Document != null && (Document.IsVisual ? redo.Count > 0 && !propertyDraft : CodeEditor.CanRedo);
    }

    private void Code_TextChanged(object? sender, EventArgs e)
    {
        if (updating || Document == null || Document.IsVisual) return;
        Document.Code = CodeEditor.Text;
        UpdateTitle();
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (updating || Document == null) return;
        Guard(() =>
        {
            ApplyProperties();
            ShowMode();
        });
    }

    private void ShowMode()
    {
        bool graph = Document?.IsVisual == true && GraphMode.IsChecked == true;
        GraphPanel.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        CodeEditor.Visibility = Document != null && !graph ? Visibility.Visible : Visibility.Collapsed;
        ExternalEditorButton.Visibility = Document?.IsVisual == false && !graph ? Visibility.Visible : Visibility.Collapsed;
        if (!graph && Document?.Graph is { } model)
        {
            updating = true;
            try
            {
                CodeEditor.Text = model.Compile();
                EditorStatus.Text = "Предпросмотр. Для ручного редактирования создайте KRL-копию.";
            }
            catch (InvalidDataException ex)
            {
                CodeEditor.Text = "-- " + ex.Message;
                EditorStatus.Text = ex.Message;
            }
            finally
            {
                updating = false;
            }
        }
    }

    private void Remember()
    {
        if (Document?.Graph == null) return;
        undo.Push(Document.Graph.Serialize());
        TrimHistory(undo);
        redo.Clear();
    }

    private static void TrimHistory(Stack<string> history)
    {
        long bytes = 0;
        var newest = history.Take(100)
            .TakeWhile((item, index) => (bytes += item.Length * 2L) <= 16 * 1024 * 1024 || index == 0).Reverse()
            .ToArray();
        history.Clear();
        foreach (string item in newest) history.Push(item);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        if (Document?.Graph is { } model && undo.TryPop(out string? previous))
        {
            redo.Push(model.Serialize());
            Document.Graph = VisualScript.Parse(previous);
            Repaint();
        }
        else if (Document?.IsVisual == false) CodeEditor.Undo();
    });

    private void Redo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        if (Document?.Graph is { } model && redo.TryPop(out string? next))
        {
            undo.Push(model.Serialize());
            TrimHistory(undo);
            Document.Graph = VisualScript.Parse(next);
            Repaint();
        }
        else if (Document?.IsVisual == false) CodeEditor.Redo();
    });

    private void Repaint()
    {
        pendingConnection = null;
        SynchronizeGraphSelection();
        RenderGraph();
        UpdateProperties();
        UpdateTitle();
        ShowMode();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        if (Document?.Graph != null) BlockLibraryRequested?.Invoke(this, EventArgs.Empty);
    });

    private ScriptNode? Selected => Document?.Graph?.Nodes.FirstOrDefault(n => n.Id == selectedId);

    private static void Visible(UIElement element, bool value) =>
        element.Visibility = value ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateProperties()
    {
        updating = true;
        propertyDraft = false;
        try
        {
            var node = Selected;
            FunctionNameInput.Text = Document?.Graph?.FunctionName ?? "";
            PropertiesPanel.IsEnabled = true;
            SelectedTitle.Text = node?.Title ?? "Выберите блок";
            ApplyPropertiesButton.IsEnabled = DeleteBlockButton.IsEnabled = node != null;
            UpdateExtendedProperties(node);
            if (node == null)
            {
                foreach (var field in new UIElement[]
                         {
                             SpeakerField, TextField, OptionsField, PathField, PositionField, LoopInput, VariableField,
                             ComparisonField, TransitionHelp
                         }) Visible(field, false);
                return;
            }

            SpeakerInput.Text = node.Speaker;
            SceneTextInput.Text = node.Text;
            PathInput.Text = node.Path;
            OptionsInput.Text = string.Join(Environment.NewLine, node.Options.Select(o => o.Text));
            PositionInput.SelectedIndex = Array.IndexOf(new[] { "left", "center", "right" }, node.Position);
            LoopInput.IsChecked = node.Loop;
            VariableInput.Text = node.Variable;
            ValueInput.Text = node.Value;
            ValueTypeInput.SelectedValue = node.ValueKind;
            ComparisonInput.SelectedValue = node.Comparison;
            Visible(SpeakerField, IsDialogue(node.Kind));
            Visible(TextField, IsDialogue(node.Kind) || node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Comment or ScriptNodeKind.Title or ScriptNodeKind.ConsoleWrite);
            Visible(OptionsField, node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async or ScriptNodeKind.RandomBranch);
            OptionsLabel.Text = node.Kind == ScriptNodeKind.RandomBranch ? "Названия случайных веток — по одному на строку" : node.Kind == ScriptNodeKind.Switch ? "Значения case — по одному на строку" : node.Kind is ScriptNodeKind.Parallel or ScriptNodeKind.Async ? "Названия веток — по одному на строку" : "Ответы — по одному на строку";
            Visible(PathField,
                node.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.Music
                    or ScriptNodeKind.Sound or ScriptNodeKind.Voice or ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene or ScriptNodeKind.ForEach or ScriptNodeKind.ShowScreen or ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script);
            bool sceneTarget = node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene;
            PathLabel.Text = sceneTarget ? "Имя функции" : node.Kind == ScriptNodeKind.ForEach ? "Имя коллекции в state" : node.Kind == ScriptNodeKind.ShowScreen ? "Название экрана (пусто — скрыть)" : "Путь от корня проекта";
            BrowsePathButton.Content = sceneTarget ? "Выбрать сцену…" : "Выбрать ресурс…";
            Visible(BrowsePathButton, !sceneTarget && node.Kind is not (ScriptNodeKind.ForEach or ScriptNodeKind.ShowScreen));
            Visible(PositionField, node.Kind == ScriptNodeKind.Character);
            Visible(LoopInput, node.Kind is ScriptNodeKind.Music or ScriptNodeKind.AudioChannel);
            LoopInput.Content = "Повторять аудио";
            Visible(VariableField, node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.SetVariable or ScriptNodeKind.Switch or ScriptNodeKind.While or ScriptNodeKind.ForEach or ScriptNodeKind.TryCatch or ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag or ScriptNodeKind.Random or ScriptNodeKind.Async or ScriptNodeKind.Join or ScriptNodeKind.WaitCondition);
            Visible(ComparisonField, node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.While or ScriptNodeKind.WaitCondition);
            Visible(TransitionHelp, sceneTarget);
            TransitionHelp.Text = node.Kind == ScriptNodeKind.CallScene
                ? "call — вызвать функцию, затем продолжить со следующего блока этой ветки."
                : "goto — перейти в функцию навсегда. Продолжение этой ветки не выполняется.";
        }
        finally
        {
            updating = false;
        }
    }

    private void ApplyProperties(bool repaint = true)
    {
        if (Document?.Graph is { } graph && FunctionNameInput.Text.Trim() != graph.FunctionName)
        {
            var renamed = VisualScript.Parse(graph.Serialize());
            renamed.FunctionName = FunctionNameInput.Text.Trim();
            _ = renamed.Serialize();
            Remember(); Document.Graph = renamed;
        }
        var node = Selected;
        if (node == null || Document?.Graph is not { } model) { propertyDraft = false; UpdateTitle(); return; }
        // Edit a copy first so invalid draft properties cannot damage the current document.
        var copy = VisualScript.Parse(model.Serialize());
        var changed = copy.Nodes.Single(n => n.Id == node.Id);

        if (IsDialogue(node.Kind)) { changed.Speaker = SpeakerInput.Text; changed.SpeakerId = selectedSpeakerId; }
        if (IsDialogue(node.Kind) || node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Comment or ScriptNodeKind.Title or ScriptNodeKind.ConsoleWrite) changed.Text = SceneTextInput.Text;
        if (node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async or ScriptNodeKind.RandomBranch)
        {
            string[] labels = OptionsInput.Text.Replace("\r", "").TrimEnd('\n').Split('\n');
            changed.Options = labels.Select((label, i) => new ScriptOption
                { Text = label, Target = i < node.Options.Count ? node.Options[i].Target : null, Weight = i < node.Options.Count ? node.Options[i].Weight : 1, Lock = i < node.Options.Count ? node.Options[i].Lock : null }).ToList();
        }

        ApplyExtendedProperties(changed);
        if (CalculationCompiler.IsCalculation(changed.Kind)) _ = CalculationCompiler.Compile(changed.Kind, changed.Calculation ?? new CalculationSettings(), changed.Variable);
        if (node.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.Music
            or ScriptNodeKind.Sound or ScriptNodeKind.Voice or ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene or ScriptNodeKind.ForEach or ScriptNodeKind.ShowScreen or ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script) changed.Path = PathInput.Text.Trim();
        if (node.Kind == ScriptNodeKind.Character)
            changed.Position = (PositionInput.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "center";
        if (node.Kind is ScriptNodeKind.Music or ScriptNodeKind.AudioChannel) changed.Loop = LoopInput.IsChecked == true;
        if (node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.SetVariable or ScriptNodeKind.Switch or ScriptNodeKind.While or ScriptNodeKind.ForEach or ScriptNodeKind.TryCatch or ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag or ScriptNodeKind.Random or ScriptNodeKind.Async or ScriptNodeKind.Join or ScriptNodeKind.WaitCondition)
        {
            changed.Variable = VariableInput.Text.Trim();
            changed.Value = ValueInput.Text;
            changed.ValueKind = ValueTypeInput.SelectedValue is ScriptValueKind value ? value : ScriptValueKind.Number;
        }

        if (node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.While or ScriptNodeKind.WaitCondition && ComparisonInput.SelectedValue is ScriptComparison comparison)
            changed.Comparison = comparison;
        if (copy.Serialize() == model.Serialize())
        {
            propertyDraft = false;
            UpdateTitle();
            return;
        }

        Remember();
        Document.Graph = copy;
        propertyDraft = false;
        if (repaint) RenderGraph();
        UpdateTitle();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        EditorStatus.Text = "Свойства применены.";
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    });

    private void Delete_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        if (Document?.Graph is not { } model) return;
        SynchronizeGraphSelection();
        if (selectedIds.Count == 0) return;
        Remember();
        model.Nodes.RemoveAll(node => selectedIds.Contains(node.Id));
        DetachExternalConnections(model.Nodes, model.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal));

        selectedId = null;
        selectedIds.Clear();
        Repaint();
    });

    private void RenderGraph() => RenderCanvasGraph();

    private static string Summary(ScriptNode node) => node.Kind switch
    {
        _ when CalculationCompiler.IsCalculation(node.Kind) => "state." + node.Variable + " ← " + (node.Kind == ScriptNodeKind.Formula ? node.Calculation?.FormulaName : node.Calculation?.A),
        ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue => (node.Speaker.Length == 0 ? "" : node.Speaker + ": ") + node.Text,
        ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Comment or ScriptNodeKind.Title or ScriptNodeKind.ConsoleWrite => node.Text,
        ScriptNodeKind.RegisterCharacter or ScriptNodeKind.UpdateCharacter => node.Character == null ? "Профиль не настроен" : node.Character.Name + " · " + node.Character.Id,
        ScriptNodeKind.SetSpeaker => "Спикер по умолчанию: " + node.SpeakerId,
        ScriptNodeKind.ResetSpeakers => "Сброс спикера в текущей ветке",
        ScriptNodeKind.Branching => "state." + node.Variable + " / " + node.Value,
        ScriptNodeKind.CompareVariables => "state." + node.Variable + " / state." + node.CompareVariable,
        ScriptNodeKind.RandomBranch => node.Options.Count + " случайных веток",
        ScriptNodeKind.TextPause => node.Duration + " с · " + (node.Skippable ? "можно пропустить" : "без пропуска"),
        ScriptNodeKind.ClearText => node.Animated ? "Стирание · " + node.Duration + " с" : "Мгновенная очистка",
        ScriptNodeKind.Condition or ScriptNodeKind.SetVariable => "state." + node.Variable + " / " + node.Value,
        ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.Music or ScriptNodeKind.Sound =>
            node.Path.Length == 0 ? "Ресурс не выбран" : node.Path,
        ScriptNodeKind.Subsystem => "Подсистема: " + node.Path + (node.WaitForSubsystem ? " · ждать завершения" : " · в фоне"),
        ScriptNodeKind.TriggerKey or ScriptNodeKind.TriggerCombination => node.Trigger?.Keys ?? "Выберите клавиши",
        ScriptNodeKind.TriggerButton => node.Trigger?.Button ?? "Выберите кнопку",
        ScriptNodeKind.TriggerHotspot => $"Зона {node.Trigger?.X}, {node.Trigger?.Y} · {node.Trigger?.Width} × {node.Trigger?.Height}",
        ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene => node.Path.Length == 0 ? "Функция не выбрана" : node.Path,
        ScriptNodeKind.Switch => "state." + node.Variable + " · " + node.Options.Count + " вариантов",
        ScriptNodeKind.While or ScriptNodeKind.WaitCondition => "state." + node.Variable + " / " + node.Value,
        ScriptNodeKind.ForEach => "state." + node.Path + " → " + node.Variable,
        ScriptNodeKind.TryCatch => "Ошибка → state." + node.Variable,
        ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag => "state." + node.Variable + " = " + node.Value,
        ScriptNodeKind.Random => "state." + node.Variable + " ← " + node.Minimum + " … " + node.Maximum,
        ScriptNodeKind.Parallel or ScriptNodeKind.Async => node.Options.Count + " веток" + (node.Kind == ScriptNodeKind.Async ? " → " + node.Variable : ""),
        ScriptNodeKind.Join => "Ожидать state." + node.Variable,
        ScriptNodeKind.Timer or ScriptNodeKind.Pause => node.Duration + " с",
        ScriptNodeKind.MoveBackground or ScriptNodeKind.MoveCharacter => $"X {node.OffsetX} · Y {node.OffsetY} · {node.Duration} с",
        ScriptNodeKind.Animation => node.Target + " · " + node.Text,
        ScriptNodeKind.Camera => $"Zoom {node.Amount} · X {node.OffsetX} · Y {node.OffsetY}",
        ScriptNodeKind.Shake => "Сила " + node.Amount + " · " + node.Duration + " с",
        ScriptNodeKind.Flash => node.Value + " · " + node.Duration + " с",
        ScriptNodeKind.ShowScreen => node.Path.Length == 0 ? "Скрыть дополнительный экран" : node.Path,
        ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script => node.Path.Length == 0 ? "Файл не выбран" : node.Path,
        ScriptNodeKind.ConfigureDialogue => "Оформление диалога",
        ScriptNodeKind.ConfigureButton => "Оформление: " + node.UiTarget,
        ScriptNodeKind.ClearBackgrounds => "Убрать стандартный и загруженные фоны",
        ScriptNodeKind.ClearSprites => "Убрать все спрайты и их анимации",
        ScriptNodeKind.AudioChannel => node.Channel + " · громкость " + node.Amount,
        ScriptNodeKind.Voice => node.Path.Length == 0 ? "Озвучка не выбрана" : node.Path,
        ScriptNodeKind.StopSound => "Канал " + node.Channel,
        ScriptNodeKind.MasterVolume => "Громкость " + node.Amount,
        ScriptNodeKind.End => node.EndAction switch { ScriptEndAction.Menu => "Главное меню", ScriptEndAction.Screen => "Экран: " + node.Path, ScriptEndAction.Exit => "Закрыть игру", _ => "Завершить ветку / вернуться" },
        _ => node.Kind == ScriptNodeKind.End ? "Завершение сцены" : "Команда сцены"
    };

    private void SelectNode(string id, bool repaint = true)
    {
        if (selectedId == id && selectedIds.Count == 1) return;
        SelectGraphBlocks([id]);
        if (repaint) RenderGraph();
        RefreshCardSelection();
    }

    private void ConnectTo(string target)
    {
        if (pendingConnection is not { } connection || Document?.Graph is not { } graph)
        {
            SelectNode(target);
            return;
        }

        ApplyProperties();
        graph = Document.Graph!;
        var destination = graph.Nodes.Single(n => n.Id == target);
        if (destination.Kind is ScriptNodeKind.Start or ScriptNodeKind.Comment || target == connection.Id)
            throw new InvalidOperationException("Выберите другой блок, кроме «Начала».");
        Remember();
        graph.Nodes.Single(n => n.Id == connection.Id).SetOutput(connection.Port, target);
        pendingConnection = null;
        RenderGraph();
        UpdateTitle();
        EditorStatus.Text = "Блоки соединены.";
    }

    private void RenderLinks() => RenderCanvasLinks();

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CanvasScale != null) CanvasScale.ScaleX = CanvasScale.ScaleY = e.NewValue;
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomSlider.Value += e.Delta > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }

    private void BrowseResource_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        bool scene = Selected?.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene;
        var dialog = new OpenFileDialog
        {
            InitialDirectory = projectRoot, Title = scene ? "Выберите игровую сцену" : "Выберите ресурс внутри проекта",
            Filter = scene ? "Сцены KNE (*.lua;*.krl;*.knegraph)|*.lua;*.krl;*.knegraph" : "Все файлы|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            PathInput.Text = System.IO.Path
                .GetRelativePath(projectRoot, PathSafety.EnsureInside(projectRoot, dialog.FileName)).Replace('\\', '/');
    });

    private async void Save_Click(object sender, RoutedEventArgs e) => await GuardAsync(SaveAsync);

    private void Run_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        _ = Document?.Graph?.Compile();
        RunRequested?.Invoke(this, EventArgs.Empty);
    });

    private void TestScene_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties(); _ = Document?.Graph?.Compile();
        TestSceneRequested?.Invoke(this, EventArgs.Empty);
    });

    private void MainScene_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        ApplyProperties();
        _ = Document?.Graph?.Compile();
        MainSceneRequested?.Invoke(this, EventArgs.Empty);
    });

    private void CloseDocument_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void Export_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        ApplyProperties();
        if (Document?.Graph == null) return;
        var dialog = new SaveFileDialog
        {
            InitialDirectory = System.IO.Path.GetDirectoryName(Document.FilePath),
            FileName = System.IO.Path.GetFileNameWithoutExtension(Document.FilePath) + "_manual.krl",
            Filter = "KRL (*.krl)|*.krl", DefaultExt = ".krl", AddExtension = true, OverwritePrompt = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await Document.ExportAsync(dialog.FileName);
        EditorStatus.Text = "Создана KRL-копия. Откройте её для ручного редактирования.";
    });

    private async void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (capturingKeys != null) { CaptureKeyDown(e); return; }
        if (QuickSearchOverlay.Visibility == Visibility.Visible)
        {
            HandleQuickSearchKey(e);
            return;
        }
        if (CharacterPickerOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CloseCharacterPicker(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape)
        {
            CancelPortDrag();
            FinishMarquee();
            pendingConnection = null;
            EditorStatus.Text = "Соединение отменено.";
            return;
        }

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && Document?.IsVisual == true &&
            GraphMode.IsChecked == true && !IsTextEditingSource(e.OriginalSource as DependencyObject) && IsGraphSource(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            Guard(OpenQuickSearch);
            return;
        }

        if (Document?.IsVisual == true && GraphMode.IsChecked == true && !IsTextEditingSource(e.OriginalSource as DependencyObject) &&
            ((e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) ||
             (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.C or Key.V or Key.D or Key.Z or Key.X or Key.Y)))
        {
            e.Handled = true;
            Guard(() => HandleGraphShortcut(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject));
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.S)
        {
            e.Handled = true;
            await GuardAsync(SaveAsync);
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            EditorStatus.Text = ex.Message;
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            EditorStatus.Text = ex.Message;
        }
    }
}
