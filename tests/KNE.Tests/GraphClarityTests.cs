using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Xunit;

namespace KNE.Tests;

public sealed class GraphClarityTests
{
    [Theory]
    [InlineData(.4)] [InlineData(.7)] [InlineData(1)] [InlineData(1.5)]
    public Task Zoom_KeepsTextAndPortsOutsideRasterEffects_WhileShadowTracksResizedMovedCards(double zoom) => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var editor = await Open(fixture);
        ((Slider)editor.FindName("ZoomSlider")).Value = zoom;
        editor.ResizeGraphNode("dialogue", new Rect(350, 130, 320, 215));
        editor.SelectGraphBlocks(["dialogue"]); editor.MoveSelectedBlocks(new Vector(45, 20));
        editor.Measure(new Size(1280, 800)); editor.Arrange(new Rect(0, 0, 1280, 800)); editor.UpdateLayout();
        var canvas = (Canvas)editor.FindName("GraphCanvas");
        var card = canvas.Children.OfType<Border>().Single(border => Equals(border.Tag, "dialogue"));
        Assert.Null(card.Effect); Assert.Null(card.CacheMode); Assert.Null(canvas.Effect); Assert.Null(canvas.CacheMode);
        Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(card));
        foreach (var element in Descendants(card).OfType<UIElement>())
        { Assert.Null(element.Effect); Assert.Null(element.CacheMode); }
        var shadows = (Dictionary<string, Rectangle>)typeof(ScriptEditor).GetField("cardShadows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        Assert.IsType<DropShadowEffect>(shadows["dialogue"].Effect); Assert.False(shadows["dialogue"].IsHitTestVisible);
        Assert.Equal(320, shadows["dialogue"].Width); Assert.Equal(215, shadows["dialogue"].Height);
        Assert.Equal(395, Canvas.GetLeft(shadows["dialogue"])); Assert.Equal(150, Canvas.GetTop(shadows["dialogue"]));
        string? directory = Environment.GetEnvironmentVariable("KNE_GRAPH_CLARITY_RENDER");
        if (directory != null)
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(1280 * dpi / 96, 800 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(editor);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(System.IO.Path.Combine(directory, $"canvas-{zoom:0.0}-{dpi}.png")); png.Save(file);
            }
        }
    });

    [Fact]
    public Task DeleteShortcut_RemovesSelectedGroupAndLinksWithSingleUndo_AndDoesNotDeleteWhileEditingText() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var editor = await Open(fixture);
        editor.SelectGraphBlocks(["dialogue", "end"]);
        foreach (DependencyObject text in new DependencyObject[] { new TextBox(), new PasswordBox(), new KrlCodeEditor() })
            Assert.False(editor.HandleGraphShortcut(Key.Delete, ModifierKeys.None, text));
        Assert.False(editor.HandleGraphShortcut(Key.Delete, ModifierKeys.Shift, editor));
        Assert.Equal(3, editor.Document!.Graph!.Nodes.Count);
        Assert.True(editor.HandleGraphShortcut(Key.Delete, ModifierKeys.None, editor));
        Assert.Single(editor.Document.Graph.Nodes); Assert.Null(editor.Document.Graph.Nodes[0].Next);
        editor.HandleGraphShortcut(Key.Z, ModifierKeys.Control, editor);
        Assert.Equal(3, editor.Document.Graph.Nodes.Count); Assert.Equal("dialogue", editor.Document.Graph.Nodes[0].Next);
        editor.HandleGraphShortcut(Key.X, ModifierKeys.Control, editor); Assert.Single(editor.Document.Graph.Nodes);
    });

    [Fact]
    public Task DuplicateRegisteredCharacterAndUpdateAndSpeaker_RemapAllProfileReferencesTogether() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var editor = await Open(fixture);
        var graph = editor.Document!.Graph!;
        graph.Nodes.Add(new() { Id = "profile", Kind = ScriptNodeKind.RegisterCharacter, Character = new() { Id = "alice", Name = "Алиса" } });
        graph.Nodes.Add(new() { Id = "update", Kind = ScriptNodeKind.UpdateCharacter, Character = new() { Id = "alice", Name = "Новое имя" } });
        graph.Nodes.Add(new() { Id = "speaker", Kind = ScriptNodeKind.SetSpeaker, SpeakerId = "alice" });
        editor.SelectGraphBlocks(["profile", "update", "speaker"]); editor.DuplicateGraphSelection();
        var pasted = editor.Document.Graph!.Nodes.Where(node => editor.SelectedBlockIds.Contains(node.Id)).ToArray();
        string id = pasted.Single(node => node.Kind == ScriptNodeKind.RegisterCharacter).Character!.Id;
        Assert.NotEqual("alice", id);
        Assert.Equal(id, pasted.Single(node => node.Kind == ScriptNodeKind.UpdateCharacter).Character!.Id);
        Assert.Equal("Новое имя", pasted.Single(node => node.Kind == ScriptNodeKind.UpdateCharacter).Character!.Name);
        Assert.Equal(id, pasted.Single(node => node.Kind == ScriptNodeKind.SetSpeaker).SpeakerId);
        Assert.Equal("alice", editor.Document.Graph.Nodes.Single(node => node.Id == "update").Character!.Id);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }

    private static async Task<ScriptEditor> Open(TempProject fixture)
    {
        var editor = new ScriptEditor();
        await editor.OpenAsync(fixture.ProjectPath, fixture.Write("canvas.knegraph", VisualScript.Create().Serialize()));
        return editor;
    }
}
