using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace KNE;

/// <summary>Lua/KRL source editor with syntax colors, bracket pairing and space-based indentation.</summary>
public sealed class KrlCodeEditor : TextEditor
{
    public static readonly DependencyProperty EditorThemeNameProperty = DependencyProperty.Register("EditorThemeName", typeof(string), typeof(KrlCodeEditor), new PropertyMetadata("dark", (view, _) => ((KrlCodeEditor)view).ApplyEditorPalette()));
    public string EditorThemeName { get => (string)GetValue(EditorThemeNameProperty); set => SetValue(EditorThemeNameProperty, value); }
    private string? analyzedText;
    private IReadOnlyList<LuaToken> tokens = [];
    private static readonly Brush PairFill = Frozen("#264F78");
    private static readonly Pen PairOutline = FrozenPen("#858585");
    public event EventHandler? SelectionChanged;
    public int CaretIndex { get => CaretOffset; set => CaretOffset = value; }
    public IReadOnlyList<LuaToken> Tokens
    {
        get
        {
            if (analyzedText == null) { analyzedText = Text; tokens = LuaSyntax.Parse(analyzedText); }
            return tokens;
        }
    }

    public KrlCodeEditor()
    {
        Background = Frozen("#1E1E1E"); Foreground = Frozen("#D4D4D4"); LineNumbersForeground = Frozen("#858585");
        ShowLineNumbers = true;
        Options.IndentationSize = LuaIndentation.Size; Options.ConvertTabsToSpaces = true;
        Options.HighlightCurrentLine = true;
        TextArea.TextView.CurrentLineBackground = Frozen("#202020");
        TextArea.TextView.CurrentLineBorder = new Pen(Frozen("#333333"), 1);
        TextArea.SelectionBrush = Frozen("#264F78"); TextArea.SelectionForeground = null;
        TextArea.TextView.LineTransformers.Add(new SyntaxColors(this));
        TextArea.TextView.BackgroundRenderers.Add(new BracketRenderer(this));
        TextChanged += (_, _) => { analyzedText = null; TextArea.TextView.Redraw(); };
        TextArea.Caret.PositionChanged += (_, _) =>
        {
            TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        TextArea.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        TextArea.TextEntering += (_, args) =>
        {
            if (args.Text.Length == 1 && InsertPairedCharacter(args.Text[0])) args.Handled = true;
        };
        SetResourceReference(EditorThemeNameProperty, "Editor.ThemeName");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (IsReadOnly || e.Handled) return;
        if (e.Key == Key.Tab && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            IndentSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            InsertLineBreak(); e.Handled = true;
        }
        else if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && RemoveEmptyPair()) e.Handled = true;
    }

    public void IndentSelection(bool reverse = false)
    {
        if (IsReadOnly) return;
        if (SelectionLength == 0 && !reverse)
        {
            ReplaceSelection("    ", 4); return;
        }
        int start = SelectionStart, end = start + SelectionLength;
        var first = Document.GetLineByOffset(start);
        var last = Document.GetLineByOffset(end);
        if (end > start && end == last.Offset) last = last.PreviousLine;
        using (Document.RunUpdate())
        {
            for (var line = last; line != null && line.LineNumber >= first.LineNumber; line = line.PreviousLine)
            {
                string content = Document.GetText(line);
                int removed = reverse ? content.StartsWith('\t') ? 1 : content.TakeWhile(c => c == ' ').Take(4).Count() : 0;
                string inserted = reverse ? "" : "    ";
                Document.Replace(line.Offset, removed, inserted);
                start = MappedOffset(start, line.Offset, removed, inserted.Length);
                end = MappedOffset(end, line.Offset, removed, inserted.Length);
            }
        }
        Select(start, end - start); CaretOffset = end;
    }

    public void InsertLineBreak()
    {
        if (IsReadOnly) return;
        using (Document.RunUpdate())
        {
            if (SelectionLength > 0) ReplaceSelection("", 0);
            var line = Document.GetLineByOffset(CaretOffset);
            int caret = CaretOffset;
            int? closingIndent = LuaIndentation.ClosingColumns(Text, line.Offset);
            if (closingIndent != null)
            {
                int leading = Document.GetText(line).TakeWhile(c => c is ' ' or '\t').Count();
                Document.Replace(line.Offset, leading, new string(' ', closingIndent.Value));
                caret = MappedOffset(caret, line.Offset, leading, closingIndent.Value);
                CaretOffset = caret;
            }
            string text = Text, indent = LuaIndentation.ForNewLine(text, caret);
            string newline = Document.Lines.FirstOrDefault(l => l.DelimiterLength > 0) is { } withDelimiter
                ? Document.GetText(withDelimiter.EndOffset, withDelimiter.DelimiterLength) : Environment.NewLine;
            bool splitPair = caret > 0 && caret < text.Length && text[caret - 1] is '(' or '[' or '{' && Matches(text[caret - 1], text[caret]) &&
                !Tokens.Any(t => t.Start < caret && t.End > caret && t.Kind is LuaTokenKind.String or LuaTokenKind.Comment);
            string insertion = newline + indent;
            if (splitPair) insertion += newline + new string(' ', LuaIndentation.Columns(Document.GetText(line)));
            Document.Insert(caret, insertion);
            CaretOffset = caret + newline.Length + indent.Length;
        }
        ScrollToLine(TextArea.Caret.Line);
    }

    /// <summary>Handles only bracket/quote completion; ordinary text keeps AvalonEdit's editing pipeline.</summary>
    public bool InsertPairedCharacter(char character)
    {
        if (IsReadOnly) return false;
        string text = Text; int caret = CaretOffset;
        var context = Tokens.FirstOrDefault(t => t.Start < caret && t.End >= caret && t.Kind is LuaTokenKind.String or LuaTokenKind.Comment);
        bool escaped = false;
        for (int i = caret - 1; i >= 0 && text[i] == '\\'; i--) escaped = !escaped;
        if (SelectionLength == 0 && caret < text.Length && text[caret] == character && !escaped &&
            (character is ')' or ']' or '}' && context == null || character is '\'' or '"' && context?.Kind == LuaTokenKind.String && context.End == caret + 1))
        {
            CaretOffset++; return true;
        }
        if (context != null) return false;
        char closing = character switch { '(' => ')', '[' => ']', '{' => '}', '\'' => '\'', '"' => '"', _ => '\0' };
        if (closing == '\0') return false;
        string selection = SelectedText;
        int start = SelectionLength == 0 ? caret : SelectionStart;
        using (Document.RunUpdate())
        {
            Document.Replace(start, SelectionLength, character + selection + closing);
            Select(start + 1, selection.Length); CaretOffset = start + 1 + selection.Length;
        }
        return true;
    }

    private bool RemoveEmptyPair()
    {
        int caret = CaretOffset; string text = Text;
        if (SelectionLength != 0 || caret == 0 || caret >= text.Length || !Matches(text[caret - 1], text[caret])) return false;
        var context = Tokens.FirstOrDefault(t => t.Start < caret && t.End > caret);
        if (context?.Kind == LuaTokenKind.Comment || context?.Kind == LuaTokenKind.String && context.Length != 2) return false;
        using (Document.RunUpdate()) { Document.Remove(caret - 1, 2); CaretOffset = caret - 1; }
        return true;
    }

    private void ReplaceSelection(string insertion, int caretDelta)
    {
        int start = SelectionLength > 0 ? SelectionStart : CaretOffset;
        using (Document.RunUpdate()) { Document.Replace(start, SelectionLength, insertion); Select(start + caretDelta, 0); }
    }
    private static int MappedOffset(int value, int offset, int removed, int added) =>
        value >= offset + removed ? value + added - removed : value > offset ? offset + added : value;
    private static bool Matches(char left, char right) => left switch
    { '(' => right == ')', '[' => right == ']', '{' => right == '}', '\'' => right == '\'', '"' => right == '"', _ => false };

    public static Brush TokenBrush(LuaToken token) => Palette[token.Kind switch
    {
        LuaTokenKind.Keyword => 0, LuaTokenKind.Control => 1, LuaTokenKind.Function => 2,
        LuaTokenKind.Identifier => 3, LuaTokenKind.Number => 4, LuaTokenKind.String => 5,
        LuaTokenKind.Comment => 6, LuaTokenKind.Bracket => 7 + token.Depth % 3,
        LuaTokenKind.InvalidBracket => 10, _ => 11
    }];
    private static readonly Brush[] Palette = [Frozen("#569CD6"), Frozen("#C586C0"), Frozen("#DCDCAA"), Frozen("#9CDCFE"),
        Frozen("#B5CEA8"), Frozen("#CE9178"), Frozen("#6A9955"), Frozen("#FFD700"), Frozen("#DA70D6"), Frozen("#179FFF"), Frozen("#F44747"), Frozen("#D4D4D4")];
    private static readonly Brush[] LightPalette = [Frozen("#0000A0"), Frozen("#8F008F"), Frozen("#795E26"), Frozen("#005A83"), Frozen("#096B52"), Frozen("#A31515"), Frozen("#008000"), Frozen("#886000"), Frozen("#8F008F"), Frozen("#005A83"), Frozen("#CC0000"), Frozen("#202733")];
    private Brush BrushFor(LuaToken token) => EditorThemeName == "dark" ? TokenBrush(token) : LightPalette[Array.IndexOf(Palette, TokenBrush(token))];
    private void ApplyEditorPalette()
    {
        bool dark = EditorThemeName == "dark";
        Background = Frozen(dark ? "#1E1E1E" : "#FFFFFF"); Foreground = Frozen(dark ? "#D4D4D4" : "#202733");
        LineNumbersForeground = Frozen(dark ? "#858585" : "#637083");
        TextArea.TextView.CurrentLineBackground = Frozen(dark ? "#202020" : "#F0F3F7");
        TextArea.TextView.CurrentLineBorder = new Pen(Frozen(dark ? "#333333" : "#D9E0EB"), 1);
        TextArea.SelectionBrush = Frozen(dark ? "#264F78" : "#CCDEF9"); TextArea.TextView.Redraw();
    }
    private static Brush Frozen(string color)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Pen FrozenPen(string color)
    { var pen = new Pen(Frozen(color), 1); pen.Freeze(); return pen; }

    private sealed class SyntaxColors(KrlCodeEditor editor) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var tokens = editor.Tokens;
            int low = 0, high = tokens.Count;
            while (low < high) { int middle = (low + high) / 2; if (tokens[middle].End <= line.Offset) low = middle + 1; else high = middle; }
            for (int i = low; i < tokens.Count && tokens[i].Start < line.EndOffset; i++)
            {
                var token = tokens[i];
                ChangeLinePart(Math.Max(token.Start, line.Offset), Math.Min(token.End, line.EndOffset),
                    element => element.TextRunProperties.SetForegroundBrush(editor.BrushFor(token)));
            }
        }
    }

    private sealed class BracketRenderer(KrlCodeEditor editor) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext context)
        {
            var token = editor.Tokens.FirstOrDefault(t => t.Length == 1 && t.Kind is LuaTokenKind.Bracket or LuaTokenKind.InvalidBracket && t.Start == editor.CaretOffset)
                ?? editor.Tokens.FirstOrDefault(t => t.Length == 1 && t.Kind is LuaTokenKind.Bracket or LuaTokenKind.InvalidBracket && t.End == editor.CaretOffset);
            if (token == null) return;
            foreach (int offset in token.Partner == null ? new[] { token.Start } : new[] { token.Start, token.Partner.Value })
                foreach (Rect rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, new SimpleSegment(offset, 1)))
                    context.DrawRoundedRectangle(PairFill, PairOutline, rectangle, 2, 2);
        }
    }
    private sealed record SimpleSegment(int Offset, int Length) : ISegment { public int EndOffset => Offset + Length; }
}
