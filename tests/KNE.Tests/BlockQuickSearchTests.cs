using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;
using static KNE.Tests.WorkspaceTabsTests;

namespace KNE.Tests;

public sealed class BlockQuickSearchTests
{
    [Fact]
    public async Task QuickSearch_FiltersExistingBlocksByNameTextAndId_AndCentersSelectedResult()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create();
        graph.Nodes[1].X = -12450; graph.Nodes[1].Y = 9876; graph.Nodes[1].Width = 320; graph.Nodes[1].Height = 220;
        graph.Nodes[1].Text = "Тайна леса";
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, fixture.Write("map.knegraph", graph.Serialize()));
            editor.Measure(new Size(1200, 760)); editor.Arrange(new Rect(0, 0, 1200, 760)); editor.UpdateLayout();
            editor.PanGraph(100, -800); editor.ChangeZoom(-3);
            Assert.Equal("dialogue", Assert.Single(editor.SearchGraphBlocks("РЕПЛИКА")).Id);
            Assert.Equal("dialogue", Assert.Single(editor.SearchGraphBlocks("ЛЕСА")).Id);
            Assert.Equal("start", Assert.Single(editor.SearchGraphBlocks("start")).Id);
            Assert.Equal(3, editor.SearchGraphBlocks("").Count);
            Assert.Empty(editor.SearchGraphBlocks("does not exist"));
            string original = editor.Document!.Graph!.Serialize();
            editor.OpenQuickSearch(); Assert.Equal(Visibility.Visible, Find<Border>(editor, "QuickSearchOverlay").Visibility);
            Find<TextBox>(editor, "QuickSearchInput").Text = "no match";
            Assert.Equal(Visibility.Visible, Find<TextBlock>(editor, "QuickSearchEmpty").Visibility);
            Assert.False(editor.NavigateToBlock("absent"));
            Assert.True(editor.NavigateToBlock("dialogue"));
            Assert.Equal("dialogue", Assert.Single(editor.SelectedBlockIds));
            Assert.Equal(Visibility.Collapsed, Find<Border>(editor, "QuickSearchOverlay").Visibility);
            var viewport = Find<Border>(editor, "GraphScroll"); var canvas = Find<Canvas>(editor, "GraphCanvas");
            var card = canvas.Children.OfType<Border>().Single(item => Equals(item.Tag, "dialogue"));
            var center = canvas.TranslatePoint(new Point(Canvas.GetLeft(card) + 160, Canvas.GetTop(card) + 110), viewport);
            Assert.Equal(viewport.ActualWidth / 2, center.X, 3); Assert.Equal(viewport.ActualHeight / 2, center.Y, 3);
            Assert.Equal(.7, Find<Slider>(editor, "ZoomSlider").Value, 3);
            Assert.Equal(original, editor.Document.Graph.Serialize()); Assert.False(editor.Document.IsDirty);
        });
    }

    [Fact]
    public async Task QuickSearch_TabOnlyInCanvas_KeyboardNavigatesAcceptsAndCancels_AndCodeTabStillIndents()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, fixture.Write("map.knegraph", VisualScript.Create().Serialize()));
            var viewport = Find<Border>(editor, "GraphScroll"); var overlay = Find<Border>(editor, "QuickSearchOverlay");
            void Key(UIElement source, Key key) => source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Key(Find<TextBox>(editor, "FunctionNameInput"), System.Windows.Input.Key.Tab);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Key(viewport, System.Windows.Input.Key.Tab); Assert.Equal(Visibility.Visible, overlay.Visibility);
            var input = Find<TextBox>(editor, "QuickSearchInput");
            Key(input, System.Windows.Input.Key.Down);
            Assert.Equal(1, Find<ListBox>(editor, "QuickSearchResults").SelectedIndex);
            Key(input, System.Windows.Input.Key.Enter); Assert.Equal("dialogue", Assert.Single(editor.SelectedBlockIds));
            Key(viewport, System.Windows.Input.Key.Tab); Key(input, System.Windows.Input.Key.Escape);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            await editor.OpenAsync(fixture.ProjectPath, fixture.Write("manual.krl", "function manual() end"));
            var code = Find<KrlCodeEditor>(editor, "CodeEditor");
            code.Select(0, 0); code.IndentSelection();
            Assert.StartsWith("    function", editor.Document!.Code); Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        });
    }
    private sealed class FakeSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
