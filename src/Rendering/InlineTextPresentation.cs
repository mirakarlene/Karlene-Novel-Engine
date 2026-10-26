using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KNE;

/// <summary>Shared inline colors, preserving the existing TextBlock layout and plain accessible text.</summary>
internal static class InlineTextPresentation
{
    private sealed class Cache
    {
        public string? Markup;
        public int Length = -1;
        public InlineTextDocument? Document;
    }
    private static readonly ConditionalWeakTable<TextBlock, Cache> caches = new();
    private static readonly Dictionary<string, Brush> brushes = new(StringComparer.Ordinal);

    public static void Apply(TextBlock target, string markup, int? visibleLength = null)
    {
        var cache = caches.GetOrCreateValue(target);
        bool changed = cache.Markup != markup;
        if (changed || cache.Document == null) cache.Document = InlineText.Parse(markup);
        var document = cache.Document;
        int length = Math.Clamp(visibleLength ?? document.Text.Length, 0, document.Text.Length);
        if (!changed && cache.Length == length && target.Text == document.Text[..length]) return;
        cache.Markup = markup; cache.Length = length;
        if (document.Runs.Count == 0) { target.Text = document.Text[..length]; return; }
        target.Text = document.Text[..length];
        target.Inlines.Clear(); int copied = 0;
        foreach (var run in document.Runs)
        {
            if (run.Start >= length) break;
            if (copied < run.Start) target.Inlines.Add(new Run(document.Text[copied..run.Start]));
            copied = Math.Min(length, run.Start + run.Length);
            target.Inlines.Add(new Run(document.Text[run.Start..copied]) { Foreground = Brush(run.Color) });
        }
        if (copied < length) target.Inlines.Add(new Run(document.Text[copied..length]));
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
