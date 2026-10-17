using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace KNE;

internal static class NarrativePresentation
{
    private sealed class BrushesCache
    {
        public string? NameColor, TextColor;
        public Brush? NameBrush, TextBrush;
        public object? SpokenFrame;
    }
    private static readonly ConditionalWeakTable<TextBlock, BrushesCache> brushes = new();
    public static void Apply(TextBlock speaker, TextBlock text, EffectTextPresenter effects, StoryFrame story,
        GameTheme theme, SceneVisuals visuals, bool animate)
    {
        var style = visuals.UiConfigurations.LastOrDefault(item => item.Target == "dialogue")?.Style;
        var character = story.Character;
        string font = character?.FontFamily ?? style?.FontFamily ?? theme.FontFamily;
        if (speaker.FontFamily.Source != font || text.FontFamily.Source != font)
            speaker.FontFamily = text.FontFamily = new FontFamily(font);
        text.FontSize = character?.FontSize ?? style?.FontSize ?? theme.Dialogue.FontSize;
        speaker.FontSize = character?.FontSize ?? style?.FontSize ?? theme.Dialogue.SpeakerFontSize;
        var cache = brushes.GetOrCreateValue(text);
        string nameColor = character?.NameColor ?? style?.TextColor ?? theme.Dialogue.SpeakerColor;
        string textColor = style?.TextColor ?? theme.Dialogue.TextColor;
        if (cache.NameColor != nameColor) { cache.NameColor = nameColor; cache.NameBrush = Brush(nameColor); }
        if (cache.TextColor != textColor) { cache.TextColor = textColor; cache.TextBrush = Brush(textColor); }
        speaker.Foreground = cache.NameBrush; text.Foreground = cache.TextBrush;
        speaker.TextAlignment = text.TextAlignment = (character?.TextAlignment ?? style?.TextAlignment) switch
        { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
        speaker.Text = story.Speaker;
        speaker.Visibility = story.Speaker.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string visible = story.Text;
        if (story.Kind == "clear_text")
        {
            string[] elements = EffectTextPresenter.TextElements(story.Text);
            int count = Math.Clamp((int)Math.Ceiling(elements.Length * (1 - story.EraseProgress)), 0, elements.Length);
            visible = string.Concat(elements.Take(count));
        }
        text.Text = visible;
        if (story.PresentationId > 0) cache.SpokenFrame = story.PresentationId;
        else if (story.Kind == "dialogue") cache.SpokenFrame = story.Identity;
        effects.Present(text, story.TextEffect, animate, cache.SpokenFrame, story.Kind is "text_pause" or "clear_text");
    }

    private static Brush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }
}
