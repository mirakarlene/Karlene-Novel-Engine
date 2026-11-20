using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KNE;

/// <summary>Applies resolved narrative markup to WPF runs.</summary>
internal static class InlineTextPresentation
{
    private sealed class Cache
    {
        public string? Markup;
        public int Length = -1;
        public int Offset;
        public bool PreserveLayout;
        public object? Fonts;
        public InlineTextDocument? Document;
    }
    private static readonly ConditionalWeakTable<TextBlock, Cache> caches = new();
    private static readonly Dictionary<string, Brush> brushes = new(StringComparer.Ordinal);
    private sealed record FontContext(string Root, IReadOnlyDictionary<string, string> Sources)
    {
        public Dictionary<string, FontFamily> Families { get; } = new(StringComparer.Ordinal);
    }
    private static readonly DependencyProperty FontContextProperty = DependencyProperty.RegisterAttached(
        "FontContext", typeof(FontContext), typeof(InlineTextPresentation), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    internal static void SetFonts(DependencyObject target, string root, IReadOnlyDictionary<string, string> sources)
    {
        if (target.GetValue(FontContextProperty) is FontContext current && current.Root == root &&
            (ReferenceEquals(current.Sources, sources) || current.Sources.Count == sources.Count &&
                sources.All(pair => current.Sources.GetValueOrDefault(pair.Key) == pair.Value))) return;
        target.SetValue(FontContextProperty, new FontContext(root, sources));
    }

    public static void Apply(TextBlock target, string markup, int? visibleLength = null, int? visibleSuffix = null, bool preserveLayout = false)
    {
        var cache = caches.GetOrCreateValue(target);
        bool changed = cache.Markup != markup;
        if (changed || cache.Document == null) cache.Document = InlineText.Parse(markup);
        var document = cache.Document;
        int length = Math.Clamp(visibleLength ?? document.Text.Length, 0, document.Text.Length);
        int offset = 0;
        if (visibleSuffix is int suffix)
        {
            int[] elements = System.Globalization.StringInfo.ParseCombiningCharacters(document.Text);
            int index = Math.Max(0, elements.Length - suffix);
            offset = index == elements.Length ? document.Text.Length : elements[index];
        }
        var fonts = (FontContext?)target.GetValue(FontContextProperty);
        int start = preserveLayout ? 0 : offset;
        if (!changed && cache.Length == length && cache.Offset == offset && cache.PreserveLayout == preserveLayout &&
            ReferenceEquals(cache.Fonts, fonts) && target.Text == document.Text[start..length]) return;
        cache.Markup = markup; cache.Length = length;
        cache.Offset = offset; cache.Fonts = fonts; cache.PreserveLayout = preserveLayout;
        bool hasStyles = document.Runs.Count > 0 || preserveLayout && offset > 0;
        if (!hasStyles) target.Inlines.Clear();
        target.Text = document.Text[start..length];
        if (!hasStyles) return;
        target.Inlines.Clear();
        int copied = start;
        foreach (var run in document.Runs)
        {
            if (run.Start >= length) break;
            if (run.Start + run.Length <= start) continue;
            int styledStart = Math.Max(start, run.Start);
            if (copied < styledStart) Add(copied, styledStart, null);
            copied = Math.Min(length, run.Start + run.Length);
            if (copied <= styledStart) break;
            Add(styledStart, copied, run);
        }
        if (copied < length) Add(copied, length, null);

        void Add(int begin, int end, InlineTextRun? style)
        {
            if (begin < offset && end > offset) { Add(begin, offset, style); Add(offset, end, style); return; }
            var inline = new Run(document.Text[begin..end]);
            if (end <= offset) inline.Foreground = Brushes.Transparent;
            else if (style?.Color is { } color) inline.Foreground = Brush(color);
            if (style?.Bold == true) inline.FontWeight = FontWeights.Bold;
            if (style?.Italic == true) inline.FontStyle = FontStyles.Italic;
            if (style?.Size is double size) inline.FontSize = size;
            if (style?.Font is { } alias && fonts != null && ResolveFont(fonts, alias) is { } family) inline.FontFamily = family;
            target.Inlines.Add(inline);
        }
    }

    private static FontFamily? ResolveFont(FontContext context, string alias)
    {
        if (context.Families.TryGetValue(alias, out var font)) return font;
        if (!context.Sources.TryGetValue(alias, out string? relative)) return null;
        string path = PathSafety.ResolveRelative(context.Root, relative);
        if (!File.Exists(path)) return null;
        var typeface = new GlyphTypeface(new Uri(path));
        string family = typeface.FamilyNames.Values.First();
        var directory = new Uri(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar);
        return context.Families[alias] = new FontFamily(directory, "./" + Uri.EscapeDataString(Path.GetFileName(path)) + "#" + family);
    }

    public static TextBlock Label(string markup)
    {
        var label = new TextBlock(); Apply(label, markup); return label;
    }

    private static Brush Brush(string color)
    {
        lock (brushes)
        {
            if (brushes.TryGetValue(color, out var cached)) return cached;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze();
            // HEX input is unbounded across a project; keep the shared cache bounded.
            if (brushes.Count >= 256) brushes.Clear();
            brushes[color] = brush; return brush;
        }
    }
}
