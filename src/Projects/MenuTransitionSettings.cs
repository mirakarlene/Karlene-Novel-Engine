using System.IO;

namespace KNE;

/// <summary>Transition between the game's menu and its story.</summary>
public sealed record MenuTransitionSettings
{
    public string Kind { get; init; } = "none";
    public string Direction { get; init; } = "left";
    public double Duration { get; init; } = 1;

    internal bool IsEnabled => Kind != "none";

    internal void Validate()
    {
        if (Kind is not ("none" or "fade" or "dissolve" or "pixellate" or "wipe" or "slide" or "slideaway" or "iris") ||
            Direction is not ("left" or "right" or "up" or "down" or "in" or "out") ||
            (Kind is "wipe" or "slide" or "slideaway" && Direction is "in" or "out") ||
            (Kind == "iris" && Direction is not ("in" or "out")) ||
            !double.IsFinite(Duration) || Duration is <= 0 or > 60)
            throw new InvalidDataException("Выберите допустимый переход, направление и длительность от 0 до 60 секунд.");
    }
}
