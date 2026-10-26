namespace KNE;

public sealed partial record SceneVisuals
{
    /// <summary>When true, default and custom backgrounds are hidden until a background command restores them.</summary>
    public bool BackgroundsCleared { get; init; }
    /// <summary>Opacity of the explicitly shown screen.</summary>
    public double ScreenOpacity { get; init; } = 1;
    /// <summary>Main places the screen with scene visuals; overlay places it above gameplay UI.</summary>
    public string ScreenLayer { get; init; } = "overlay";
    /// <summary>Text resolved from state when a show-screen block executes; shared by game and preview.</summary>
    public IReadOnlyDictionary<string, string> ScreenTexts { get; init; } = new Dictionary<string, string>();
}
