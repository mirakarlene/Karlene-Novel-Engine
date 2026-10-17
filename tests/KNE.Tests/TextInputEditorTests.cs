using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class TextInputEditorTests
{
    [Fact]
    public Task InputEditor_AllFieldsRoundTripAndUndo_AndInvalidDraftCannotDamageNode() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/input.knegraph", VisualScript.Create().Serialize());
        var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.TextInput);
        string id = editor.Document!.Graph!.Nodes.Last().Id;
        Assert.Equal("narrative", BlockLibrary.Definitions.Single(block => block.Kind == ScriptNodeKind.TextInput).Group);
        Text("InputVariable", "playername"); Text("InputPrompt", "Введите пароль"); Text("InputMaximumLength", "7");
        Extended<ComboBox>("InputValidationMode").SelectedIndex = 2;
        Text("InputExpectedValue", "123-456"); Check("InputIgnoreCase", true); Text("InputPattern", "[0-9]{3}-[0-9]{3}");
        Text("InputFailureMessage", "Неверный код"); Text("InputPlaceholder", "000-000"); Check("InputAutoFocus", false);
        Text("InputMask", "000-000"); Check("InputIsPassword", true); Check("InputBranchOnResult", true); Check("InputHasErrorOutput", true); Check("InputHasCancelOutput", true);
        Click("ApplyPropertiesButton");
        var expected = new TextInputSettings { Variable = "playername", Prompt = "Введите пароль", MaximumLength = 7, ValidationMode = TextInputValidationMode.Regex, ExpectedValue = "123-456", IgnoreCase = true, Pattern = "[0-9]{3}-[0-9]{3}", FailureMessage = "Неверный код", Placeholder = "000-000", AutoFocus = false, InputMask = "000-000", IsPassword = true, BranchOnResult = true, HasErrorOutput = true, HasCancelOutput = true };
        Assert.Equal(expected, Node().Input); Assert.Equal(new[] { "Успех", "Ошибка", "Отмена" }, Node().Outputs().Select(output => output.Label));
        Click("UndoButton"); Assert.Equal("player_answer", Node().Input!.Variable);
        Click("RedoButton"); Assert.Equal(expected, Node().Input);
        Text("InputVariable", "bad variable"); Click("ApplyPropertiesButton");
        Assert.Equal(expected, Node().Input); Assert.Contains("Переменная", ((TextBlock)editor.FindName("EditorStatus")).Text);
        Text("InputVariable", "playername"); Text("InputMaximumLength", "257"); Click("ApplyPropertiesButton"); Assert.Equal(expected, Node().Input);
        Text("InputMaximumLength", "7"); await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
        Assert.Equal(expected, Node().Input); editor.Clear();

        ScriptNode Node() => editor.Document!.Graph!.Nodes.Single(node => node.Id == id);
        T Extended<T>(string name) where T : FrameworkElement => ((StackPanel)editor.FindName("ExtendedProperties")).Children.OfType<T>().Single(control => control.Name == name);
        void Text(string name, string value) => Extended<TextBox>(name).Text = value;
        void Check(string name, bool value) => Extended<CheckBox>(name).IsChecked = value;
        void Click(string name) => ((Button)editor.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    });

    [Fact]
    public async Task SceneTest_InputFeedbackRetryAndBackReplay_RestartDiscardsAnswer()
    {
        using var fixture = new TempProject();
        const string source = "function main() text_input([[{\"variable\":\"answer\",\"prompt\":\"Code\",\"validation_mode\":\"Exact\",\"expected_value\":\"ok\",\"failure_message\":\"Retry\"}]]) d(nil,state.answer) d(nil,'last') end";
        fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        await using var scene = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await scene.RestartAsync(); Assert.Equal("input", scene.Story.Kind);
        await scene.SubmitInputAsync("bad"); Assert.Equal("input", scene.Story.Kind); Assert.Equal("Retry", scene.Story.InputFeedback); Assert.False(scene.CanGoBack);
        await scene.SubmitInputAsync("ok"); Assert.Equal("ok", scene.Story.Text); Assert.True(scene.CanGoBack);
        await scene.AdvanceAsync(); Assert.Equal("last", scene.Story.Text);
        await scene.BackAsync(); Assert.Equal("ok", scene.Story.Text);
        await scene.BackAsync(); Assert.Equal("input", scene.Story.Kind); Assert.Equal("", scene.Story.InputFeedback);
        await scene.SubmitInputAsync("ok"); Assert.Equal("ok", scene.Story.Text);
        await scene.RestartAsync(); Assert.Equal("input", scene.Story.Kind); Assert.False(scene.CanGoBack);
    }

    [Fact]
    public async Task EditorPreview_StopsForInputBeforeSelection_AndReplaysAnswerOnReseek()
    {
        using var fixture = new TempProject();
        const string source = "function main()\n text_input([[{\"variable\":\"answer\",\"prompt\":\"Name\"}]])\n d(nil,state.answer)\nend";
        fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        var selection = new EditorSelection(project.MainScenePath, source, 3, null, false, "main", false);
        await using var preview = new EditorPreview(project, _ => { }); await preview.SeekAsync(selection);
        Assert.True(preview.NeedsInput); Assert.False(preview.Reached);
        await preview.SubmitInputAsync("Игрок"); Assert.True(preview.Reached);
        if (preview.Story.Kind == "preview") await preview.StepAsync(); Assert.Equal("Игрок", preview.Story.Text);
        await preview.SeekAsync(selection); Assert.True(preview.Reached); Assert.False(preview.NeedsInput);
        if (preview.Story.Kind == "preview") await preview.StepAsync(); Assert.Equal("Игрок", preview.Story.Text);
        preview.ResetChoices(); await preview.SeekAsync(selection); Assert.True(preview.NeedsInput);
    }
}
