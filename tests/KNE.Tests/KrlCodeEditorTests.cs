using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class KrlCodeEditorTests
{
    [Theory]
    [InlineData("function main()", 4)]
    [InlineData("    if state.score > 0 then", 8)]
    [InlineData("    for i = 1, 3 do", 8)]
    [InlineData("    while ready do", 8)]
    [InlineData("    repeat", 8)]
    [InlineData("    else", 8)]
    [InlineData("    elseif ready then", 8)]
    [InlineData("if ready then d(nil,'a') else", 4)]
    [InlineData("    local data = {", 8)]
    [InlineData("    function f() end", 4)]
    [InlineData("    -- function end then {", 4)]
    [InlineData("    d(nil, 'then {')", 4)]
    [InlineData("\td(nil,'Слова')", 4)]
    public void NewLine_InfersFourSpaceIndentFromExecutableLuaTokens(string source, int spaces)
    {
        Assert.Equal(new string(' ', spaces), LuaIndentation.ForNewLine(source, source.Length));
    }

    [Theory]
    [InlineData("function main()\n    if ready then\n        d()\n        end", 4)]
    [InlineData("function main()\n    if ready then\n    elseif other then\n    end\n    end", 0)]
    [InlineData("function main()\n    repeat\n        d()\n        until ready", 4)]
    [InlineData("function main()\n    if ready then\n        else", 4)]
    [InlineData("local data = {\n    x = 1,\n    }", 0)]
    public void ClosingLine_AlignsWithItsLuaOpener(string source, int columns)
    {
        Assert.Equal(columns, LuaIndentation.ClosingColumns(source, source.LastIndexOf('\n') + 1));
    }

    [Theory]
    [InlineData("function main()\n    local text = [=[\n        end\n]=]")]
    [InlineData("function main()\n    --[==[\n        else\n]==]")]
    [InlineData("function main()\n    d('end')")]
    [InlineData("function main()\n    -- end")]
    public void ClosingLine_NeverFormatsStringOrCommentContents(string source)
    {
        int line = source.IndexOf("        ", StringComparison.Ordinal);
        if (line < 0) line = source.LastIndexOf('\n') + 1;
        Assert.Null(LuaIndentation.ClosingColumns(source, line));
    }

    [Fact]
    public async Task TabKey_InsertsExactlyFourSpacesAtAnyColumn_AndOneUndoRestoresText()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Text = "abc" }; editor.CaretIndex = 1;
            PressKey(editor, Key.Tab);
            Assert.Equal("a    bc", editor.Text); Assert.Equal(5, editor.CaretIndex);
            Assert.DoesNotContain('\t', editor.Text); editor.Undo(); Assert.Equal("abc", editor.Text);
            editor.Redo(); Assert.Equal("a    bc", editor.Text);
        });
    }

    [Fact]
    public async Task TabAndShiftTab_IndentSelectedLines_ExcludeFinalLineAtColumnOne_AndKeepGroupedUndo()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Text = "first\r\n  second\r\nthird" };
            editor.Select(0, "first\r\n  second\r\n".Length);
            editor.IndentSelection();
            Assert.Equal("    first\r\n      second\r\nthird", editor.Text);
            Assert.Equal("first\r\n      second\r\n", editor.SelectedText);
            editor.Undo(); Assert.Equal("first\r\n  second\r\nthird", editor.Text);
            editor.Select(0, editor.Text.Length); editor.IndentSelection(true);
            Assert.Equal("first\r\nsecond\r\nthird", editor.Text);
            editor.Undo(); Assert.Equal("first\r\n  second\r\nthird", editor.Text);
            editor.Text = "\tline"; editor.CaretIndex = 5; editor.IndentSelection(true);
            Assert.Equal("line", editor.Text); Assert.Equal(4, editor.CaretIndex);
        });
    }

    [Fact]
    public async Task EnterKey_IndentsFunctionBody_AlignsEnd_AndPreservesLineEndings()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Text = "function main()\n    d(nil,'Hi')\n    end" };
            editor.CaretIndex = "function main()".Length; PressKey(editor, Key.Enter);
            Assert.StartsWith("function main()\n    \n", editor.Text);
            Assert.Equal("function main()\n    ".Length, editor.CaretIndex);
            editor.Undo(); Assert.Equal("function main()\n    d(nil,'Hi')\n    end", editor.Text);
            editor.CaretIndex = editor.Text.Length; PressKey(editor, Key.Enter);
            Assert.Equal("function main()\n    d(nil,'Hi')\nend\n", editor.Text);
            editor.Undo(); Assert.EndsWith("    end", editor.Text);
            editor.Text = "local data = {}\r\n"; editor.CaretIndex = "local data = {".Length; PressKey(editor, Key.Enter);
            Assert.Equal("local data = {\r\n    \r\n}\r\n", editor.Text);
            Assert.Equal("local data = {\r\n    ".Length, editor.CaretIndex);
        });
    }

    [Fact]
    public async Task PairedTyping_WrapsSelection_SkipsCloser_AndBackspaceRemovesEmptyPair()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Text = "word" }; editor.Select(0, 4);
            editor.TextArea.PerformTextInput("(");
            Assert.Equal("(word)", editor.Text); Assert.Equal("word", editor.SelectedText);
            editor.Select(5, 0); editor.TextArea.PerformTextInput(")"); Assert.Equal("(word)", editor.Text); Assert.Equal(6, editor.CaretIndex);
            editor.Undo(); Assert.Equal("word", editor.Text);
            editor.Text = ""; editor.TextArea.PerformTextInput("{"); Assert.Equal("{}", editor.Text); Assert.Equal(1, editor.CaretIndex);
            PressKey(editor, Key.Back); Assert.Equal("", editor.Text); editor.Undo(); Assert.Equal("{}", editor.Text);
            editor.Text = ""; editor.TextArea.PerformTextInput("\""); Assert.Equal("\"\"", editor.Text);
            editor.TextArea.PerformTextInput("Привет"); editor.TextArea.PerformTextInput("\""); Assert.Equal("\"Привет\"", editor.Text);
        });
    }

    [Fact]
    public async Task StringsCommentsAndReadonlyCode_DoNotGainAutomaticPairsOrFormattingEdits()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Text = "-- comment" }; editor.CaretIndex = editor.Text.Length;
            editor.TextArea.PerformTextInput("("); Assert.Equal("-- comment(", editor.Text);
            editor.Text = "local text = [=[\n    end\n]=]"; editor.CaretIndex = editor.Text.IndexOf("end", StringComparison.Ordinal) + 3;
            PressKey(editor, Key.Enter); Assert.Equal("local text = [=[\n    end\n    \n]=]", editor.Text);
            editor.Text = "'text'"; editor.CaretIndex = 3; editor.TextArea.PerformTextInput("{"); Assert.Equal("'te{xt'", editor.Text);
            editor.IsReadOnly = true; string original = editor.Text;
            editor.IndentSelection(); editor.IndentSelection(true); editor.InsertLineBreak(); Assert.False(editor.InsertPairedCharacter('('));
            editor.TextArea.PerformTextInput("x"); PressKey(editor, Key.Tab); Assert.Equal(original, editor.Text);
        });
    }

    [Fact]
    public async Task Highlighting_ActuallyColorsRenderedLuaAndKrl_AndNestedBracketsSharePairColors()
    {
        await Sta(() =>
        {
            var editor = new KrlCodeEditor { Width = 1100, Height = 560, FontFamily = new FontFamily("Consolas"), FontSize = 16,
                Text = "-- KRL / Lua: комментарий\nfunction main()\n    local score = 12.5\n    local text = [=[Скобки (и слова end) в строке]=]\n    if score > 0 then\n        d(\"Алиса\", { score, (1 + 2) })\n        call(chapter)\n    else\n        goto(finale)\n    end\nend" };
            editor.CaretIndex = editor.Text.IndexOf('{');
            var owner = new Window { Content = editor };
            editor.ApplyTemplate();
            editor.Measure(new Size(1100, 560)); editor.Arrange(new Rect(0, 0, 1100, 560)); editor.UpdateLayout();
            editor.TextArea.TextView.EnsureVisualLines();
            var actual = editor.TextArea.TextView.VisualLines.SelectMany(v => v.Elements)
                .Select(e => ((SolidColorBrush)e.TextRunProperties.ForegroundBrush).Color).Distinct().ToArray();
            foreach (string color in new[] { "#569CD6", "#C586C0", "#DCDCAA", "#B5CEA8", "#CE9178", "#6A9955", "#FFD700", "#DA70D6", "#179FFF" })
                Assert.Contains((Color)ColorConverter.ConvertFromString(color), actual);
            Assert.True(editor.ShowLineNumbers); Assert.Equal(4, editor.Options.IndentationSize); Assert.True(editor.Options.ConvertTabsToSpaces);
            foreach (var token in editor.Tokens.Where(t => t.Partner != null))
                Assert.Equal(KrlCodeEditor.TokenBrush(token), KrlCodeEditor.TokenBrush(editor.Tokens.Single(other => other.Start == token.Partner)));
            Assert.Equal(Color.FromRgb(30, 30, 30), ((SolidColorBrush)editor.Background).Color);
            string? directory = Environment.GetEnvironmentVariable("KNE_SYNTAX_RENDER");
            if (directory != null)
            {
                Directory.CreateDirectory(directory); var image = new RenderTargetBitmap(1100, 560, 96, 96, PixelFormats.Pbgra32); image.Render(editor);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, "lua-krl-editor.png")); encoder.Save(output);
            }
            editor.Text = "d('Changed', false)";
            Assert.Contains(editor.Tokens, t => t.Kind == LuaTokenKind.Keyword && editor.Text.Substring(t.Start, t.Length) == "false");
            Assert.DoesNotContain(editor.Tokens, t => t.Kind == LuaTokenKind.Comment);
            editor.Text = "([)]"; editor.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines();
            Assert.Contains(editor.TextArea.TextView.VisualLines.SelectMany(v => v.Elements),
                element => ((SolidColorBrush)element.TextRunProperties.ForegroundBrush).Color == Color.FromRgb(244, 71, 71));
            owner.Close();
        });
    }

    private static void PressKey(KrlCodeEditor editor, Key key) => editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        new FakePresentationSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
