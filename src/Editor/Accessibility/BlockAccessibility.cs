using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>A persisted accessibility choice shown as a preview card.</summary>
/// <param name="Id">Stable settings discriminator.</param>
/// <param name="Title">User-facing mode name.</param>
/// <param name="Description">Short explanation of the mode.</param>
public sealed record AccessibilityOption(string Id, string Title, string Description);
/// <summary>Window-only category styling; never serialized into the project.</summary>
/// <param name="Color">Category tint.</param>
/// <param name="Foreground">Readable black or white foreground.</param>
/// <param name="Badges">Whether category symbols are visible.</param>
/// <param name="BorderWidth">Unselected graph outline width in DIP.</param>
public sealed record CategoryPresentation(Color Color, Color Foreground, bool Badges, double BorderWidth);

/// <summary>Window-scoped category adaptation, leaving project data and the game palette untouched.</summary>
public static class BlockAccessibility
{
    /// <summary>The six supported modes in preview-card order.</summary>
    public static IReadOnlyList<AccessibilityOption> Modes { get; } = [
        new("normal", "Обычное зрение", "Исходные цвета категорий"),
        new("protanopia", "Протанопия", "Слабая чувствительность к красному"),
        new("deuteranopia", "Дейтеранопия", "Слабая чувствительность к зелёному"),
        new("tritanopia", "Тританопия", "Различение синего и жёлтого"),
        new("achromatopsia", "Ахроматопсия", "Различение по яркости и значкам"),
        new("high_contrast", "Высокий контраст", "Усиленные границы и значки")];
    /// <summary>Returns a valid discriminator, falling back to the original palette.</summary>
    /// <param name="mode">Stored or user-selected mode.</param><returns>A supported mode ID.</returns>
    public static string Normalize(string? mode) => Modes.Any(option => option.Id == mode) ? mode! : "normal";
    /// <summary>Inherits the selected mode through the editor visual tree.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(BlockAccessibility), new FrameworkPropertyMetadata("normal", FrameworkPropertyMetadataOptions.Inherits, Changed));
    /// <summary>Reads the effective inherited mode.</summary>
    /// <param name="view">Editor element.</param><returns>The effective mode ID.</returns>
    public static string GetMode(DependencyObject view) => (string)view.GetValue(ModeProperty);
    /// <summary>Assigns a normalized mode to an editor subtree.</summary>
    /// <param name="view">Editor subtree root.</param><param name="mode">Requested mode.</param>
    public static void SetMode(DependencyObject view, string mode) => view.SetValue(ModeProperty, Normalize(mode));
    /// <summary>Applies accessibility without rebuilding the graph or replacing its draft.</summary>
    /// <param name="window">Editor window.</param><param name="mode">Requested mode.</param>
    public static void Apply(Window window, string mode) => SetMode(window, mode);
    /// <summary>Associates a catalog category with its live border color.</summary>
    public static readonly DependencyProperty CategoryProperty = DependencyProperty.RegisterAttached("Category", typeof(string), typeof(BlockAccessibility), new PropertyMetadata("", Changed));
    /// <summary>Reads the associated category ID.</summary>
    /// <param name="view">Category element.</param><returns>The category ID.</returns>
    public static string GetCategory(DependencyObject view) => (string)view.GetValue(CategoryProperty);
    /// <summary>Assigns a category for live palette updates.</summary>
    /// <param name="view">Category element.</param><param name="category">Catalog group ID.</param>
    public static void SetCategory(DependencyObject view, string category) => view.SetValue(CategoryProperty, category);
    private sealed record Decoration(string Group, Action<CategoryPresentation> Update);
    private static readonly DependencyProperty DecorationProperty = DependencyProperty.RegisterAttached("Decoration", typeof(Decoration), typeof(BlockAccessibility));
    internal static void Decorate(FrameworkElement view, string group, Action<CategoryPresentation> update)
    { view.SetValue(DecorationProperty, new Decoration(group, update)); update(Presentation(group, GetMode(view))); }
    private static void Changed(DependencyObject view, DependencyPropertyChangedEventArgs args)
    {
        if (view.GetValue(DecorationProperty) is Decoration decoration) decoration.Update(Presentation(decoration.Group, GetMode(view)));
        if (view is CategoryIcon icon) icon.InvalidateVisual();
        if (view is System.Windows.Controls.Border border && BlockLibrary.Groups.Any(group => group.Id == GetCategory(view)))
            border.BorderBrush = new SolidColorBrush(Presentation(GetCategory(view), GetMode(view)).Color);
    }
    /// <summary>Resolves a category tint, readable text and non-color cues.</summary>
    /// <param name="group">Catalog group ID.</param><param name="mode">Accessibility mode ID.</param>
    /// <returns>Presentation for the editor; the normal mode retains the catalog tint.</returns>
    public static CategoryPresentation Presentation(string group, string mode)
    {
        mode = Normalize(mode); int index = BlockLibrary.Groups.ToList().FindIndex(item => item.Id == group);
        if (index < 0) index = 0;
        string original = BlockLibrary.Groups[index].Color;
        string[] safe = mode == "tritanopia" ? ["#666666", "#B83B63", "#007F83", "#903F80", "#4B667D", "#C44A65", "#9CCED6", "#8D4051", "#506A79", "#506060", "#777777"] : ["#64748B", "#E69F00", "#0072B2", "#CC79A7", "#009E73", "#56B4E9", "#F0E442", "#D55E00", "#775E91", "#007B83", "#777777"];
        Color color = (Color)ColorConverter.ConvertFromString(mode == "normal" || mode == "high_contrast" ? original : safe[index % safe.Length]);
        if (mode == "achromatopsia") { byte value = (byte)(45 + index * 16); color = Color.FromRgb(value, value, value); }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return new(color, luminance > .179 ? Colors.Black : Colors.White, mode != "normal", mode == "high_contrast" ? 3 : 1.5);
    }
    internal static double Linear(byte value) { double channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
}
