namespace KNE;

/// <summary>The visible checkpoint of a suspended scene.</summary>
public sealed record StoryFrame(string Kind, string Speaker, string Text, IReadOnlyList<string> Options)
{
    public static StoryFrame Empty { get; } = new("idle", "", "", Array.Empty<string>());
    public bool Completed => Kind == "completed";
}
