namespace KNE;

/// <summary>Size, position and color of the minimap in graph editors.</summary>
public sealed record GraphMinimapSettings(bool ColorCategories = false, double Width = 126, string Corner = "bottom_right")
{
    internal static IReadOnlyList<GraphicsOption<double>> Sizes { get; } =
        [new(96, "Маленькая"), new(126, "Средняя"), new(168, "Большая")];

    internal static IReadOnlyList<GraphicsOption<string>> Corners { get; } =
        [new("bottom_right", "Справа снизу"), new("bottom_left", "Слева снизу"), new("top_right", "Справа сверху"), new("top_left", "Слева сверху")];

    internal static GraphMinimapSettings Normalize(GraphMinimapSettings? value)
    {
        if (value == null) return new();
        double width = Sizes.Any(size => size.Value == value.Width) ? value.Width : 126;
        string corner = value.Corner is "top_left" or "top_right" or "bottom_left" or "bottom_right" ? value.Corner : "bottom_right";
        return value with { Width = width, Corner = corner };
    }
}
