using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class GraphSelectionTests
{
    [Fact]
    public async Task CopyPaste_RemapsSelectedIdsAndInternalLinks_DropsExternalLinksAndKeepsClipboard()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            string? clipboard = null;
            editor.WriteGraphClipboard = value => clipboard = value;
            editor.ReadGraphClipboard = () => clipboard;
            editor.SelectGraphBlocks(["dialogue", "end"]);
            editor.CopyGraphSelection();
            var copied = VisualScript.Parse(clipboard!);
            Assert.Equal(2, copied.Nodes.Count);
            Assert.Equal("end", copied.Nodes.Single(node => node.Id == "dialogue").Next);
            editor.PasteGraphSelection();
            var pasted = editor.Document!.Graph!.Nodes.Where(node => editor.SelectedBlockIds.Contains(node.Id)).ToArray();
            Assert.Equal(2, pasted.Length); Assert.Equal(5, editor.Document.Graph.Nodes.Count);
            Assert.DoesNotContain(pasted, node => node.Id is "dialogue" or "end");
            Assert.Equal(pasted.Single(node => node.Kind == ScriptNodeKind.End).Id, pasted.Single(node => node.Kind == ScriptNodeKind.Dialogue).Next);
            Assert.Equal("dialogue", editor.Document.Graph.Nodes.Single(node => node.Id == "start").Next);
            Assert.Equal("end", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Next);
            Assert.Equal(copied.Serialize(), clipboard);
            editor.SelectGraphBlocks(["dialogue"]); editor.CopyGraphSelection();
            Assert.Null(VisualScript.Parse(clipboard!).Nodes.Single().Next);
        });
    }

    [Fact]
    public async Task Duplicate_IsAtomicUndoAndCtrlXRedo_PreservesClipboardAndBlockDimensions()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            var node = editor.Document!.Graph!.Nodes.Single(item => item.Id == "dialogue");
            node.Width = 350; node.Height = 240;
            editor.SelectGraphBlocks(["dialogue", "end"]);
            editor.ReadGraphClipboard = () => throw new InvalidOperationException("Duplicate must not read clipboard");
            editor.WriteGraphClipboard = _ => throw new InvalidOperationException("Duplicate must not replace clipboard");
            Assert.True(editor.HandleGraphShortcut(Key.D, ModifierKeys.Control, editor));
            var duplicate = editor.Document.Graph.Nodes.Single(item => item.Kind == ScriptNodeKind.Dialogue && item.Id != "dialogue");
            string duplicateId = duplicate.Id;
            Assert.Equal(350, duplicate.Width); Assert.Equal(240, duplicate.Height);
            Assert.Equal(node.X + 32, duplicate.X); Assert.Equal(node.Y + 32, duplicate.Y);
            Assert.True(editor.HandleGraphShortcut(Key.Z, ModifierKeys.Control, editor));
            Assert.Equal(3, editor.Document.Graph.Nodes.Count);
            Assert.True(editor.HandleGraphShortcut(Key.X, ModifierKeys.Control, editor));
            Assert.Equal(5, editor.Document.Graph.Nodes.Count);
            Assert.Equal(duplicateId, editor.Document.Graph.Nodes.Single(item => item.Kind == ScriptNodeKind.Dialogue && item.Id != "dialogue").Id);
        });
    }

    [Fact]
    public async Task Shortcuts_CopyAndPasteUseGraphClipboard_AndAllLeaveTextFieldsUntouched()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            string? clipboard = null;
            editor.ReadGraphClipboard = () => clipboard; editor.WriteGraphClipboard = value => clipboard = value;
            editor.SelectGraphBlocks(["dialogue"]);
            Assert.True(editor.HandleGraphShortcut(Key.C, ModifierKeys.Control, editor));
            Assert.NotNull(clipboard);
            Assert.True(editor.HandleGraphShortcut(Key.V, ModifierKeys.Control, editor));
            Assert.Equal(4, editor.Document!.Graph!.Nodes.Count);
            foreach (var key in new[] { Key.C, Key.V, Key.D, Key.Z, Key.X })
            {
                Assert.False(editor.HandleGraphShortcut(key, ModifierKeys.Control, new TextBox()));
                Assert.False(editor.HandleGraphShortcut(key, ModifierKeys.Control, new PasswordBox()));
                Assert.False(editor.HandleGraphShortcut(key, ModifierKeys.Control, new KrlCodeEditor()));
                Assert.False(editor.HandleGraphShortcut(key, ModifierKeys.None, editor));
            }
            Assert.Equal(4, editor.Document.Graph.Nodes.Count);
        });
    }

    [Fact]
    public async Task Marquee_IntersectsEdges_AddsOrReplacesSelection_AndGroupMoveUndoRestoresEveryPosition()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            editor.SelectGraphArea(new Rect(535, 65, 10, 20));
            Assert.Equal(new[] { "dialogue" }, editor.SelectedBlockIds);
            editor.SelectGraphArea(new Rect(625, 65, 10, 20), additive: true);
            Assert.Equal(2, editor.SelectedBlockIds.Count); Assert.Contains("end", editor.SelectedBlockIds);
            var before = editor.Document!.Graph!.Nodes.ToDictionary(node => node.Id, node => new Point(node.X, node.Y));
            editor.MoveSelectedBlocks(new Vector(-220, 170));
            foreach (var node in editor.Document.Graph.Nodes)
                Assert.Equal(before[node.Id] + (node.Id == "start" ? new Vector() : new Vector(-220, 170)), new Point(node.X, node.Y));
            editor.HandleGraphShortcut(Key.Z, ModifierKeys.Control, editor);
            foreach (var node in editor.Document.Graph.Nodes) Assert.Equal(before[node.Id], new Point(node.X, node.Y));
            editor.SelectGraphArea(new Rect(10000, 10000, 20, 20)); Assert.Empty(editor.SelectedBlockIds);
        });
    }

    [Fact]
    public async Task CopyStartAndDialogue_KeepsSingleEntry_PasteInvalidDataDoesNotMutateGraphOrHistory()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture); string? clipboard = null;
            editor.ReadGraphClipboard = () => clipboard; editor.WriteGraphClipboard = value => clipboard = value;
            editor.SelectGraphBlocks(["start", "dialogue"]); editor.CopyGraphSelection(); editor.PasteGraphSelection();
            Assert.Single(editor.Document!.Graph!.Nodes, node => node.Kind == ScriptNodeKind.Start);
            Assert.Equal(4, editor.Document.Graph.Nodes.Count);
            string before = editor.Document.Graph.Serialize();
            clipboard = "{\"Version\":1,\"Unknown\":true}";
            Assert.Throws<InvalidDataException>(editor.PasteGraphSelection);
            Assert.Equal(before, editor.Document.Graph.Serialize());
            editor.HandleGraphShortcut(Key.Z, ModifierKeys.Control, editor);
            Assert.Equal(3, editor.Document.Graph.Nodes.Count);
        });
    }

    [Fact]
    public async Task DuplicateCharacterAndDialogue_RenamesProfileAndSpeakerTogether_AndOpenClearsSelection()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            editor.AddBlock(ScriptNodeKind.RegisterCharacter);
            var character = editor.Document!.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.RegisterCharacter);
            var dialogue = editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue");
            dialogue.SpeakerId = character.Character!.Id;
            editor.SelectGraphBlocks([character.Id, "dialogue"]); editor.DuplicateGraphSelection();
            var duplicateProfile = editor.Document.Graph.Nodes.Single(node => editor.SelectedBlockIds.Contains(node.Id) && node.Kind == ScriptNodeKind.RegisterCharacter);
            var duplicateDialogue = editor.Document.Graph.Nodes.Single(node => editor.SelectedBlockIds.Contains(node.Id) && node.Kind == ScriptNodeKind.Dialogue);
            Assert.NotEqual(character.Character.Id, duplicateProfile.Character!.Id);
            Assert.Equal(duplicateProfile.Character.Id, duplicateDialogue.SpeakerId);
            await editor.OpenAsync(fixture.ProjectPath, editor.Document.FilePath);
            Assert.Empty(editor.SelectedBlockIds);
        });
    }

    [Fact]
    public async Task DuplicateInput_WithThreeBranches_PreservesConfigurationAndRemapsAllPorts()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            var settings = new TextInputSettings { Variable = "password", Prompt = "Код?", InputMask = "000-000", MaximumLength = 7,
                IsPassword = true, BranchOnResult = true, HasErrorOutput = true, HasCancelOutput = true };
            editor.Document!.Graph!.Nodes.Add(new() { Id = "input", Kind = ScriptNodeKind.TextInput, Input = settings, Next = "dialogue", True = "end", False = "cancel" });
            editor.Document.Graph.Nodes.Add(new() { Id = "cancel", Kind = ScriptNodeKind.End });
            editor.SelectGraphBlocks(["input", "dialogue", "end", "cancel"]); editor.DuplicateGraphSelection();
            var copied = editor.Document.Graph.Nodes.Where(node => editor.SelectedBlockIds.Contains(node.Id)).ToArray();
            var input = copied.Single(node => node.Kind == ScriptNodeKind.TextInput);
            Assert.Equal(settings, input.Input); Assert.Equal(4, copied.Length);
            Assert.Equal(new[] { "Успех", "Ошибка", "Отмена" }, input.Outputs().Select(port => port.Label));
            Assert.All(input.Outputs(), port => Assert.Contains(copied, node => node.Id == port.Target));
            Assert.Equal(3, input.Outputs().Select(port => port.Target).Distinct().Count());
            Assert.Equal("dialogue", editor.Document.Graph.Nodes.Single(node => node.Id == "input").Next);
        });
    }

    [Fact]
    public async Task DuplicateInput_WithDisabledBranches_DoesNotKeepHiddenConnectionsToOriginalBlocks()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture);
            editor.Document!.Graph!.Nodes.Add(new() { Id = "input", Kind = ScriptNodeKind.TextInput, Input = new(), Next = "dialogue", True = "end", False = "dialogue" });
            editor.SelectGraphBlocks(["input"]); editor.DuplicateGraphSelection();
            var copied = editor.Document.Graph.Nodes.Single(node => editor.SelectedBlockIds.Contains(node.Id));
            Assert.Null(copied.Next); Assert.Null(copied.True); Assert.Null(copied.False);
            copied.Input = copied.Input! with { BranchOnResult = true, HasErrorOutput = true, HasCancelOutput = true };
            Assert.All(copied.Outputs(), port => Assert.Null(port.Target));
        });
    }

    [Fact]
    public async Task DeleteSelection_DisconnectsIncomingLinks_AndUndoRestoresWholeGroup()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture); editor.SelectGraphBlocks(["dialogue", "end"]);
            ((Button)editor.FindName("DeleteBlockButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(editor.Document!.Graph!.Nodes); Assert.Null(editor.Document.Graph.Nodes[0].Next);
            editor.HandleGraphShortcut(Key.Z, ModifierKeys.Control, editor);
            Assert.Equal(3, editor.Document.Graph.Nodes.Count);
            Assert.Equal("dialogue", editor.Document.Graph.Nodes.Single(node => node.Id == "start").Next);
            Assert.Equal("end", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Next);
        });
    }

    [Fact]
    public async Task PasteAtBlockLimit_IsRejectedAtomically_AndCodeViewKeepsNativeShortcuts()
    {
        using var fixture = new TempProject();
        await OnSta(async () =>
        {
            var editor = await Open(fixture); editor.SelectGraphBlocks(["dialogue"]);
            string? clipboard = null; editor.WriteGraphClipboard = value => clipboard = value; editor.ReadGraphClipboard = () => clipboard;
            editor.CopyGraphSelection();
            while (editor.Document!.Graph!.Nodes.Count < 500) editor.Document.Graph.Nodes.Add(new() { Kind = ScriptNodeKind.Comment });
            string before = editor.Document.Graph.Serialize();
            Assert.Throws<InvalidOperationException>(editor.PasteGraphSelection);
            Assert.Equal(before, editor.Document.Graph.Serialize());
            ((RadioButton)editor.FindName("CodeMode")).IsChecked = true;
            Assert.False(editor.HandleGraphShortcut(Key.D, ModifierKeys.Control, editor));
            Assert.False(editor.HandleGraphShortcut(Key.X, ModifierKeys.Control, editor));
            Assert.Equal(500, editor.Document.Graph.Nodes.Count);
        });
    }

    private static async Task<ScriptEditor> Open(TempProject fixture)
    {
        string path = Path.Combine(fixture.ProjectPath, "canvas.knegraph");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
        editor.Measure(new Size(1200, 800)); editor.Arrange(new Rect(0, 0, 1200, 800)); editor.UpdateLayout();
        return editor;
    }

    private static Task OnSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
