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
        var document = await ScriptDocument.OpenAsync(root, path);
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
        updating = true;
        Document = null;
        frameCatalog = null;
        selectedId = null;
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

    /// <summary>Prompts to save or discard edits when closing or switching documents.</summary>
    /// <returns>True when the caller may leave; false when the user cancels.</returns>
    public async Task<bool> CanLeaveAsync()
    {
        if (saving) throw new InvalidOperationException("Дождитесь завершения сохранения.");
        if (Document == null || (!Document.IsDirty && !propertyDraft)) return true;
        var answer = MessageBox.Show(Window.GetWindow(this), "Сохранить изменения в сценарии?",
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

    private void UpdateTitle()
    {
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
            Visible(SpeakerField, node.Kind == ScriptNodeKind.Dialogue);
            Visible(TextField, node.Kind is ScriptNodeKind.Dialogue or ScriptNodeKind.Choice or ScriptNodeKind.Comment or ScriptNodeKind.Title);
            Visible(OptionsField, node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async);
            OptionsLabel.Text = node.Kind == ScriptNodeKind.Switch ? "Значения case — по одному на строку" : node.Kind is ScriptNodeKind.Parallel or ScriptNodeKind.Async ? "Названия веток — по одному на строку" : "Ответы — по одному на строку";
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
        ApplyExtendedProperties(changed);
        if (node.Kind == ScriptNodeKind.Dialogue) changed.Speaker = SpeakerInput.Text;
        if (node.Kind is ScriptNodeKind.Dialogue or ScriptNodeKind.Choice or ScriptNodeKind.Comment or ScriptNodeKind.Title) changed.Text = SceneTextInput.Text;
        if (node.Kind is ScriptNodeKind.Choice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async)
        {
            string[] labels = OptionsInput.Text.Replace("\r", "").TrimEnd('\n').Split('\n');
            changed.Options = labels.Select((label, i) => new ScriptOption
                { Text = label, Target = i < node.Options.Count ? node.Options[i].Target : null }).ToList();
        }

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
        if (Selected is not { } node || Document?.Graph is not { } model) return;
        Remember();
        model.Nodes.Remove(node);
        foreach (var other in model.Nodes)
        {
            var outputs = other.Outputs().ToArray();
            for (int i = 0; i < outputs.Length; i++)
                if (outputs[i].Target == node.Id)
                    other.SetOutput(i, null);
        }

        selectedId = null;
        Repaint();
    });

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();
        cards.Clear();
        links.Clear();
        if (Document?.Graph is not { } graph) return;
        UpdateGraphExtent();
        foreach (var node in graph.Nodes)
        {
            var contents = new StackPanel();
            var header = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BlockLibrary.ColorFor(node))),
                Height = 30, Cursor = Cursors.SizeAll
            };
            header.Child = new TextBlock
            {
                Text = node.Title, Foreground = node.Kind == ScriptNodeKind.Start ? Brushes.Black : Brushes.White, FontWeight = FontWeights.Bold,
                ToolTip = DescribeNodeIdentity(node),
                Margin = new Thickness(8, 5, 8, 0)
            };
            contents.Children.Add(header);
            var summary = new TextBlock
            {
                Text = Summary(node), Height = 42, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 4, 8, 0)
            };
            contents.Children.Add(summary);
            if (node.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.Comment))
            {
                var input = new Button
                {
                    Content = "● Вход", HorizontalAlignment = HorizontalAlignment.Left, Height = 26,
                    Padding = new Thickness(4, 0, 4, 0), ToolTip = "Соединить выбранный выход с этим блоком"
                };
                input.Click += (_, _) => Guard(() => ConnectTo(node.Id));
                contents.Children.Add(input);
            }
            else if (node.Kind == ScriptNodeKind.Start) contents.Children.Add(new Border { Height = 26 });

            int port = 0;
            foreach (var output in node.Outputs())
            {
                int index = port++;
                var button = new Button
                {
                    Content = output.Label + (string.IsNullOrEmpty(output.Target) ? " ○" : " ●"), Height = 28,
                    HorizontalContentAlignment = HorizontalAlignment.Right, Padding = new Thickness(4, 0, 4, 0),
                    ToolTip = "Нажмите, затем выберите вход другого блока. Правая кнопка — отсоединить."
                };
                button.Click += (_, _) => Guard(() =>
                {
                    ApplyProperties();
                    pendingConnection = (node.Id, index);
                    EditorStatus.Text = $"Выберите вход для «{node.Title}»: {output.Label}. Esc — отменить.";
                });
                button.MouseRightButtonUp += (_, args) => Guard(() =>
                {
                    ApplyProperties();
                    Remember();
                    Document!.Graph!.Nodes.Single(n => n.Id == node.Id).SetOutput(index, null);
                    pendingConnection = null;
                    RenderGraph();
                    UpdateTitle();
                    args.Handled = true;
                });
                contents.Children.Add(button);
            }

            var card = new Border
            {
                Width = 210, BorderThickness = new Thickness(2),
                BorderBrush = node.Id == selectedId ? Brushes.DarkOrange : Brushes.LightSlateGray,
                Background = Brushes.White, Child = contents, Tag = node.Id
            };
            card.MouseLeftButtonDown += (_, _) => Guard(() => SelectNode(node.Id));
            Point start = default;
            double originX = 0, originY = 0;
            bool dragged = false;
            string? before = null;
            header.MouseLeftButtonDown += (_, args) => Guard(() =>
            {
                SelectNode(node.Id, repaint: false);
                var current = Document!.Graph!.Nodes.Single(n => n.Id == node.Id);
                before = Document.Graph.Serialize();
                start = args.GetPosition(GraphScroll);
                originX = current.X;
                originY = current.Y;
                dragged = false;
                header.CaptureMouse();
                args.Handled = true;
            });
            header.MouseMove += (_, args) =>
            {
                if (!header.IsMouseCaptured || args.LeftButton != MouseButtonState.Pressed) return;
                Point currentPoint = args.GetPosition(GraphScroll);
                var current = Document!.Graph!.Nodes.Single(n => n.Id == node.Id);
                current.X = originX + (currentPoint.X - start.X) / ZoomSlider.Value;
                current.Y = originY + (currentPoint.Y - start.Y) / ZoomSlider.Value;
                dragged |= current.X != originX || current.Y != originY;
                Canvas.SetLeft(card, current.X);
                Canvas.SetTop(card, current.Y);
                UpdateGraphExtent();
                RenderLinks();
            };
            header.MouseLeftButtonUp += (_, args) =>
            {
                if (header.IsMouseCaptured)
                {
                    header.ReleaseMouseCapture();
                    args.Handled = true;
                }
            };
            header.LostMouseCapture += (_, _) =>
            {
                if (before != null)
                {
                    if (dragged)
                    {
                        undo.Push(before);
                        TrimHistory(undo);
                        redo.Clear();
                    }

                    before = null;
                    dragged = false;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RenderGraph();
                        UpdateTitle();
                    }));
                }
            };
            Canvas.SetLeft(card, node.X);
            Canvas.SetTop(card, node.Y);
            Panel.SetZIndex(card, 1);
            GraphCanvas.Children.Add(card);
            cards.Add(node.Id, card);
        }

        RenderLinks();
    }

    private static string Summary(ScriptNode node) => node.Kind switch
    {
        ScriptNodeKind.Dialogue => (node.Speaker.Length == 0 ? "" : node.Speaker + ": ") + node.Text,
        ScriptNodeKind.Choice or ScriptNodeKind.Comment or ScriptNodeKind.Title => node.Text,
        ScriptNodeKind.Condition or ScriptNodeKind.SetVariable => "state." + node.Variable + " / " + node.Value,
        ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.Music or ScriptNodeKind.Sound =>
            node.Path.Length == 0 ? "Ресурс не выбран" : node.Path,
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
        if (selectedId == id) return;
        ApplyProperties(repaint: false);
        selectedId = id;
        if (repaint) RenderGraph();
        foreach (var (key, card) in cards) card.BorderBrush = key == id ? Brushes.DarkOrange : Brushes.LightSlateGray;
        UpdateProperties();
        PreviewRequested?.Invoke(this, EventArgs.Empty);
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

    private void RenderLinks()
    {
        foreach (var link in links) GraphCanvas.Children.Remove(link);
        links.Clear();
        if (Document?.Graph is not { } graph) return;
        var map = graph.Nodes.ToDictionary(n => n.Id);
        foreach (var node in graph.Nodes)
        {
            int index = 0;
            foreach (var (_, target) in node.Outputs())
            {
                int port = index++;
                if (target == null || !map.TryGetValue(target, out var destination)) continue;
                var start = new Point(node.X + 210, node.Y + 118 + port * 28);
                var end = new Point(destination.X, destination.Y + 89);
                double bend = Math.Max(70, Math.Abs(end.X - start.X) / 2);
                var curve = new PathFigure { StartPoint = start };
                curve.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y),
                    end, true));
                var path = new System.Windows.Shapes.Path
                {
                    Data = new PathGeometry([curve]), Stroke = Brushes.SlateGray, StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                var arrow = new Polygon
                {
                    Fill = Brushes.SlateGray,
                    Points = new PointCollection { end, new(end.X - 9, end.Y - 5), new(end.X - 9, end.Y + 5) },
                    IsHitTestVisible = false
                };
                GraphCanvas.Children.Add(path);
                GraphCanvas.Children.Add(arrow);
                links.Add(path);
                links.Add(arrow);
            }
        }
    }

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
        if (e.Key == Key.Escape)
        {
            pendingConnection = null;
            EditorStatus.Text = "Соединение отменено.";
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.S)
        {
            e.Handled = true;
            await GuardAsync(SaveAsync);
        }
        // Let text fields keep their own text undo. Graph undo is active on the canvas and toolbar.
        else if (e.OriginalSource is not TextBox && e.Key == Key.Z)
        {
            e.Handled = true;
            Undo_Click(this, e);
        }
        else if (e.OriginalSource is not TextBox && e.Key == Key.Y)
        {
            e.Handled = true;
            Redo_Click(this, e);
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
