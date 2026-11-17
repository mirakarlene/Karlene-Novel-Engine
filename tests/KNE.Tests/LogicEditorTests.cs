using System.IO;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class LogicEditorTests
{
    [Fact]
    public Task TypedLogicOperandsAndDestinationApplyUndoRedoAndSaveWithoutChangingConnections() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var graph = Graph(ScriptNodeKind.StringReplace, new() { A = new("string", "before before"), B = new("string", "before"), C = new("string", "after") });
        string path = fixture.Write("editing.knegraph", graph.Serialize()); var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.SelectGraphBlocks(["logic"]);
        Find<TextBox>(editor, "LogicAValueInput").Text = "Привет before"; Find<TextBox>(editor, "LogicCValueInput").Text = "мир"; Find<TextBox>(editor, "LogicResultInput").Text = "translated";
        WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var changed = Node(editor); Assert.Equal(new LogicOperand("string", "Привет before"), changed.Logic!.A); Assert.Equal("мир", changed.Logic.C.Value); Assert.Equal("translated", changed.Variable); Assert.Equal("dialogue", changed.Next);
        WorkspaceTabsTests.Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("before before", Node(editor).Logic!.A.Value); Assert.Equal("result", Node(editor).Variable);
        WorkspaceTabsTests.Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await editor.SaveAsync();
        var saved = VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == "logic"); Assert.Equal("Привет before", saved.Logic!.A.Value); Assert.Equal("мир", saved.Logic.C.Value); Assert.Equal("translated", saved.Variable); Assert.Equal("dialogue", saved.Next);
        var before = File.ReadAllBytes(path); Find<ComboBox>(editor, "LogicAKindInput").SelectedItem = "number"; Find<TextBox>(editor, "LogicAValueInput").Text = "not a number";
        await Assert.ThrowsAnyAsync<Exception>(editor.SaveAsync); Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal("Привет before", Node(editor).Logic!.A.Value); Assert.Equal("not a number", Find<TextBox>(editor, "LogicAValueInput").Text);
    });

    [Fact]
    public Task CompoundConditionEditorAddsMoreThanTwentyRowsAndNestedNotGroupsWithUndoAndBothPortsPreserved() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var graph = Graph(ScriptNodeKind.CompoundCondition, new()); var block = graph.Nodes.Single(n => n.Id == "logic"); block.Next = null; block.True = "dialogue"; block.False = "failed";
        graph.Nodes.Add(new() { Id = "failed", Kind = ScriptNodeKind.Dialogue, Text = "other", Next = "end" });
        string path = fixture.Write("conditions.knegraph", graph.Serialize()); var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.SelectGraphBlocks(["logic"]);
        for (int index = 2; index < 25; index++) Fields(editor).OfType<Button>().Single(button => Equals(button.Content, "+ Условие / группа")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var operators = Fields(editor).OfType<ComboBox>().Where(input => input.Items.Contains("compare")).ToArray(); Assert.Equal(26, operators.Length); Assert.Equal("and", operators[0].SelectedItem);
        operators[1].SelectedItem = "not";
        WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(25, Node(editor).Logic!.Condition.Children.Count); Assert.Equal("not", Node(editor).Logic!.Condition.Children[0].Operator); Assert.Single(Node(editor).Logic!.Condition.Children[0].Children);
        Assert.Equal(new (string, string?)[] { ("Да", "dialogue"), ("Нет", "failed") }, Node(editor).Outputs().Select(p => (p.Label, p.Target)));
        WorkspaceTabsTests.Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, Node(editor).Logic!.Condition.Children.Count);
        WorkspaceTabsTests.Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await editor.SaveAsync();
        var saved = VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == "logic"); Assert.Equal(25, saved.Logic!.Condition.Children.Count); Assert.Equal("not", saved.Logic.Condition.Children[0].Operator); Assert.Equal("dialogue", saved.True); Assert.Equal("failed", saved.False);
    });

    [Fact]
    public void EveryNewLogicOperationIsAvailableInTheLogicLibraryWithIndependentTypedDefaults()
    {
        var kinds = Enum.GetValues<ScriptNodeKind>().Where(LogicCompiler.IsLogic).ToArray(); Assert.Equal(29, kinds.Length);
        foreach (var kind in kinds)
        {
            var definition = Assert.Single(BlockLibrary.Definitions, entry => entry.Kind == kind); Assert.Equal("logic", definition.Group); Assert.Equal(LogicCompiler.Label(kind), definition.Title);
            var first = BlockLibrary.Create(kind); var second = BlockLibrary.Create(kind); Assert.NotNull(first.Logic); Assert.NotNull(second.Logic); first.Logic!.Condition.Children.Clear();
            Assert.Equal(2, second.Logic!.Condition.Children.Count); Assert.Equal("inventory", second.Logic.List); Assert.Equal(kind, second.Kind);
        }
        Assert.Null(BlockLibrary.Create(ScriptNodeKind.Random).Logic);
    }

    static ScriptNode Node(ScriptEditor editor) => editor.Document!.Graph!.Nodes.Single(n => n.Id == "logic");
    static IEnumerable<FrameworkElement> Fields(ScriptEditor editor) => Descendants(WorkspaceTabsTests.Find<StackPanel>(editor, "ExtendedProperties"));
    static IEnumerable<FrameworkElement> Descendants(DependencyObject owner)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject nested) { if (nested is FrameworkElement element) yield return element; foreach (var descendant in Descendants(nested)) yield return descendant; }
    }
    static T Find<T>(ScriptEditor editor, string name) where T : FrameworkElement => Fields(editor).OfType<T>().Single(control => control.Name == name);
    static VisualScript Graph(ScriptNodeKind kind, LogicSettings settings) => new() { FunctionName = "editing", Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "logic" }, new() { Id = "logic", Kind = kind, Logic = settings, Variable = "result", Next = "dialogue" }, new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "done", Next = "end" }, new() { Id = "end", Kind = ScriptNodeKind.End }] };
}
