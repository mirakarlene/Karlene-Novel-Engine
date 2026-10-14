using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ScriptEditorTests
{
    [Fact]
    public async Task ColoredCodeEditor_PreservesExistingTabsAndCrLf_SaveUndoAndPreviewUseActualSource()
    {
        using var fixture = new TempProject();
        const string original = "function chapter()\r\n\td(nil, 'Привет')\r\nend\r\n";
        string path = fixture.Write("chapter.krl", original);
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            var code = (KrlCodeEditor)editor.FindName("CodeEditor");
            Assert.Equal(original, code.Text); Assert.False(editor.Document!.IsDirty);
            Assert.Contains(code.Tokens, token => token.Kind == LuaTokenKind.Function);
            await editor.SaveAsync(); Assert.Equal(original, File.ReadAllText(path));
            code.Select(original.IndexOf("d(", StringComparison.Ordinal), 0); code.IndentSelection();
            Assert.True(editor.Document.IsDirty); Assert.Equal(code.Text, editor.Document.Code);
            Assert.True(((Button)editor.FindName("UndoButton")).IsEnabled);
            var preview = editor.CapturePreview()!;
            Assert.Equal(code.Text, preview.Source); Assert.Equal(2, preview.Line); Assert.Equal("chapter", preview.Function);
            Click(editor, "UndoButton"); Assert.Equal(original, code.Text); Assert.False(editor.Document.IsDirty);
            Click(editor, "RedoButton"); Assert.Equal(original.Replace("\td(", "\t    d("), code.Text);
            await editor.SaveAsync(); Assert.Equal(code.Text, File.ReadAllText(path));
            await editor.OpenAsync(fixture.ProjectPath, fixture.Write("other.lua", "function other() end"));
            Assert.False(code.CanUndo); Assert.False(code.CanRedo);
        });
    }

    [Fact]
    public async Task Preview_CodeCaretCapturesUnsavedSourceAndFunction_ExternalEditorButtonRequestsConfiguredLaunch()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("manual.krl", "function chapter() d(nil, 'Сохранённое') end");
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            int previews = 0, launches = 0;
            editor.PreviewRequested += (_, _) => previews++;
            editor.ExternalEditorRequested += (_, _) => launches++;
            var text = (KrlCodeEditor)editor.FindName("CodeEditor");
            text.Text = "function chapter()\n  d(nil, 'Новое')\nend\nfunction other()\n d(nil, 'Другое')\nend";
            text.CaretIndex = text.Text.IndexOf("Другое", StringComparison.Ordinal);
            var capture = editor.CapturePreview()!;
            Assert.Equal(5, capture.Line); Assert.Equal("other", capture.Function); Assert.Null(capture.NodeId);
            Assert.Equal(text.Text, capture.Source); Assert.True(editor.Document!.IsDirty);
            Assert.Contains("Сохранённое", File.ReadAllText(path)); Assert.True(previews >= 2);
            Assert.Equal(Visibility.Visible, ((Button)editor.FindName("ExternalEditorButton")).Visibility);
            Click(editor, "ExternalEditorButton"); Assert.Equal(1, launches);
            editor.Clear(); Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName("ExternalEditorButton")).Visibility);
        });
    }

    [Fact]
    public async Task Preview_SelectedBlockIncludesAppliedDraftAndPersistedAnimation_AndExplainsCallVersusGoto()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "chapter.knegraph");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            var canvas = (Canvas)editor.FindName("GraphCanvas");
            Select(canvas, "dialogue");
            ((TextBox)editor.FindName("SceneTextInput")).Text = "Несохранённая реплика";
            var animate = (CheckBox)editor.FindName("PreviewAnimationInput");
            Assert.False(animate.IsChecked); animate.IsChecked = true;
            var selection = editor.CapturePreview()!;
            Assert.Equal("dialogue", selection.NodeId); Assert.True(selection.Animate);
            Assert.Contains("Несохранённая реплика", selection.Source);
            Assert.Contains("kne_editor_node(\"dialogue\")", selection.Source);
            Assert.DoesNotContain("Несохранённая реплика", File.ReadAllText(path));
            Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName("ExternalEditorButton")).Visibility);
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.True(editor.Document!.Graph!.Nodes.Single(n => n.Id == "dialogue").PreviewAnimation);
            var types = (ComboBox)editor.FindName("BlockType");
            types.SelectedItem = types.Items.Cast<ScriptNode>().Single(n => n.Kind == ScriptNodeKind.CallScene);
            Click(editor, "AddBlockButton");
            Assert.Contains("продолжить со следующего блока", ((TextBlock)editor.FindName("TransitionHelp")).Text);
            types.SelectedItem = types.Items.Cast<ScriptNode>().Single(n => n.Kind == ScriptNodeKind.GotoScene);
            Click(editor, "AddBlockButton");
            Assert.Contains("Продолжение этой ветки не выполняется", ((TextBlock)editor.FindName("TransitionHelp")).Text);
        });
    }

    [Fact]
    public async Task Editor_GraphFunctionRename_SavesAndSupportsUndo_AndRejectsLuaKeywords()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "chapter.knegraph");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            var name = (TextBox)editor.FindName("FunctionNameInput");
            Assert.Equal("scene_chapter", name.Text);
            name.Text = "lmao"; await editor.SaveAsync();
            Assert.Equal("lmao", editor.Document!.Graph!.FunctionName);
            Assert.Equal("lmao", VisualScript.Parse(File.ReadAllText(path)).FunctionName);
            Click(editor, "UndoButton"); Assert.Equal("scene_chapter", name.Text);
            Click(editor, "RedoButton"); Assert.Equal("lmao", name.Text);
            name.Text = "goto";
            await Assert.ThrowsAsync<InvalidDataException>(() => editor.SaveAsync());
            Assert.Equal("lmao", editor.Document.Graph.FunctionName);
            Assert.Equal("lmao", VisualScript.Parse(File.ReadAllText(path)).FunctionName);
        });
    }

    [Theory]
    [InlineData(ScriptNodeKind.CallScene, "Вызвать сцену", 1)]
    [InlineData(ScriptNodeKind.GotoScene, "Перейти на сцену", 0)]
    public async Task Editor_SceneTransitionPalette_EditsTargetAndKeepsMatchingOutputPorts(ScriptNodeKind kind, string title, int outputCount)
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "scene.knegraph");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            var types = (ComboBox)editor.FindName("BlockType");
            var item = types.Items.Cast<ScriptNode>().Single(n => n.Kind == kind);
            Assert.Equal(title, item.Title); types.SelectedItem = item;
            Click(editor, "AddBlockButton");
            var added = editor.Document!.Graph!.Nodes.Single(n => n.Kind == kind);
            Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("PathField")).Visibility);
            Assert.Equal("Имя функции", ((TextBlock)editor.FindName("PathLabel")).Text);
            Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName("BrowsePathButton")).Visibility);
            ((TextBox)editor.FindName("PathInput")).Text = "chapter_N";
            Click(editor, "ApplyPropertiesButton");
            added = editor.Document.Graph!.Nodes.Single(n => n.Kind == kind);
            Assert.Equal("chapter_N", added.Path);
            var canvas = (Canvas)editor.FindName("GraphCanvas");
            Assert.Equal(outputCount, ((StackPanel)Card(canvas, added.Id).Child).Children.OfType<Button>().Count(b => !Equals(b.Content, "● Вход")));
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal(added.Path, editor.Document.Graph!.Nodes.Single(n => n.Kind == kind).Path);
        });
    }

    [Fact]
    public async Task Editor_CodeMode_SavesManualCodeAndResetsUndoOnOpen()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("manual.krl", "function main() end");
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor();
            await editor.OpenAsync(fixture.ProjectPath, path);
            var input = (KrlCodeEditor)editor.FindName("CodeEditor");
            Assert.False(input.IsReadOnly);
            Assert.False(((RadioButton)editor.FindName("GraphMode")).IsEnabled);
            input.Text = "function main() d(nil, 'Ручной код') end";
            Assert.True(editor.Document!.IsDirty);
            Assert.Contains("*", ((TextBlock)editor.FindName("DocumentTitle")).Text);
            await editor.SaveAsync();
            Assert.False(editor.Document.IsDirty);
            Assert.Equal("function main() d(nil, 'Ручной код') end", File.ReadAllText(path));
            await editor.OpenAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
            Assert.False(input.CanUndo);
            Assert.Contains("Добро пожаловать", input.Text);
            editor.Clear();
            Assert.Null(editor.Document);
            Assert.False(((Button)editor.FindName("RunButton")).IsEnabled);
        });
    }

    [Fact]
    public async Task Editor_GraphMode_AddConnectEditUndoAndReopen()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "scene.knegraph");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await OnStaAsync(async () =>
        {
            var editor = new ScriptEditor();
            await editor.OpenAsync(fixture.ProjectPath, path);
            var canvas = (Canvas)editor.FindName("GraphCanvas");
            Assert.Equal(3, canvas.Children.OfType<Border>().Count());
            Assert.Equal(2, canvas.Children.OfType<System.Windows.Shapes.Path>().Count());
            var types = (ComboBox)editor.FindName("BlockType");
            types.SelectedItem = types.Items.Cast<ScriptNode>().Single(n => n.Kind == ScriptNodeKind.Choice);
            Click(editor, "AddBlockButton");
            var choice = editor.Document!.Graph!.Nodes.Single(n => n.Kind == ScriptNodeKind.Choice);
            ((TextBox)editor.FindName("SceneTextInput")).Text = "Куда?";
            ((TextBox)editor.FindName("OptionsInput")).Text = "Домой\nКонец\n";
            Assert.Contains("*", ((TextBlock)editor.FindName("DocumentTitle")).Text);
            Click(editor, "ApplyPropertiesButton");
            Connect(canvas, "start", 0, choice.Id);
            Connect(canvas, choice.Id, 0, "dialogue");
            Connect(canvas, choice.Id, 1, "end");
            Assert.Equal("end", editor.Document.Graph.Nodes.Single(n => n.Id == choice.Id).Options[1].Target);
            Click(editor, "UndoButton");
            Assert.Null(editor.Document.Graph.Nodes.Single(n => n.Id == choice.Id).Options[1].Target);
            Click(editor, "RedoButton");
            Assert.Equal("end", editor.Document.Graph.Nodes.Single(n => n.Id == choice.Id).Options[1].Target);

            Select(canvas, "dialogue");
            ((TextBox)editor.FindName("SpeakerInput")).Text = "Алиса";
            ((TextBox)editor.FindName("SceneTextInput")).Text = "Возвращаемся домой.";
            Click(editor, "ApplyPropertiesButton");
            ((RadioButton)editor.FindName("CodeMode")).IsChecked = true;
            var code = (KrlCodeEditor)editor.FindName("CodeEditor");
            Assert.True(code.IsReadOnly);
            Assert.Contains("choice(\"Куда?\", {\"Домой\", \"Конец\"})", code.Text);
            Assert.Contains("d(\"Алиса\", \"Возвращаемся домой.\")", code.Text);
            Assert.Equal(Visibility.Collapsed, ((Grid)editor.FindName("GraphPanel")).Visibility);
            ((RadioButton)editor.FindName("GraphMode")).IsChecked = true;
            Assert.Equal(Visibility.Visible, ((Grid)editor.FindName("GraphPanel")).Visibility);
            await editor.SaveAsync();
            Assert.False(editor.Document.IsDirty);
            await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal("Алиса", editor.Document.Graph!.Nodes.Single(n => n.Id == "dialogue").Speaker);
            Assert.Equal(choice.Id, editor.Document.Graph.Nodes.Single(n => n.Id == "start").Next);
            Assert.Equal(new[] { "Домой", "Конец" },
                editor.Document.Graph.Nodes.Single(n => n.Id == choice.Id).Options.Select(o => o.Text));
            Select(canvas, choice.Id);
            Click(editor, "DeleteBlockButton");
            Assert.DoesNotContain(editor.Document.Graph.Nodes, n => n.Id == choice.Id);
            Assert.Null(editor.Document.Graph.Nodes.Single(n => n.Id == "start").Next);
            Click(editor, "UndoButton");
            Assert.Equal(choice.Id, editor.Document.Graph.Nodes.Single(n => n.Id == "start").Next);
            Assert.Equal(4, editor.Document.Graph.Nodes.Count);
            ((Slider)editor.FindName("ZoomSlider")).Value = 0.8;
            Assert.Equal(0.8, ((ScaleTransform)canvas.LayoutTransform).ScaleX);
            Select(canvas, choice.Id);

            // Exercise the actual WPF layout and rendering without showing a desktop window.
            editor.Measure(new Size(900, 520));
            editor.Arrange(new Rect(0, 0, 900, 520));
            editor.UpdateLayout();
            Assert.True(canvas.ActualWidth >= 1600);
            string? preview = Environment.GetEnvironmentVariable("KNE_EDITOR_PREVIEW");
            if (preview != null)
            {
                var bitmap = new RenderTargetBitmap(900, 520, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(editor);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(preview);
                encoder.Save(output);
            }
        });
    }

    [Fact]
    public async Task MainWindow_GraphBecomesEntryPointAndPreviewUsesWorker()
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "scene.knegraph");
        var mainGraph = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        mainGraph.Graph!.FunctionName = "main"; await mainGraph.SaveAsync();
        fixture.Write("game/scenes/main.krl", "function prologue() end");
        fixture.Write("project.json",
            File.ReadAllText(Path.Combine(fixture.ProjectPath, "project.json"))
                .Replace("\"metadata\":", "\"custom\": {\"keep\": true}, \"metadata\":"));
        await OnStaAsync(async () =>
        {
            var window = new MainWindow();
            try
            {
                var open = typeof(MainWindow).GetMethod("OpenProjectAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await (Task)open.Invoke(window, [fixture.ProjectPath, false])!;
                var editor = (ScriptEditor)window.FindName("SceneEditor");
                await editor.OpenAsync(fixture.ProjectPath, path);
                var status = (TextBlock)window.FindName("OperationStatus");
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var descriptor =
                    System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,
                        typeof(TextBlock));
                EventHandler changed = (_, _) =>
                {
                    if (status.Text.StartsWith("Главная сцена:")) done.TrySetResult();
                    else if (status.Text.StartsWith("Операция не завершена."))
                        done.TrySetException(new InvalidOperationException(status.Text));
                };
                descriptor.AddValueChanged(status, changed);
                try
                {
                    Click(editor, "MainSceneButton");
                    await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    descriptor.RemoveValueChanged(status, changed);
                }

                var project = await fixture.LoadAsync();
                Assert.Equal("scene.knegraph", project.Configuration.EntryPoints!.MainScene);
                Assert.True(project.Configuration.AdditionalData!["custom"].GetProperty("keep").GetBoolean());
                await using var worker = new ProcessGameSession(project, _ => { });
                await worker.StartAsync();
                Assert.Equal("Добро пожаловать!", worker.Story.Text);
                Assert.NotEqual(Environment.ProcessId, worker.WorkerProcessId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Click(ScriptEditor editor, string name) =>
        ((Button)editor.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Border Card(Canvas canvas, string id) =>
        canvas.Children.OfType<Border>().Single(c => Equals(c.Tag, id));

    private static void Select(Canvas canvas, string id) => Card(canvas, id)
        .RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

    private static void Connect(Canvas canvas, string source, int port, string target)
    {
        Select(canvas, source);
        ((StackPanel)Card(canvas, source).Child).Children.OfType<Button>().Where(b => !Equals(b.Content, "● Вход"))
            .ElementAt(port).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((StackPanel)Card(canvas, target).Child).Children.OfType<Button>().Single(b => Equals(b.Content, "● Вход"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
