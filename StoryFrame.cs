namespace KNE;

/// <summary>The visible checkpoint of a suspended scene.</summary>
public sealed record StoryFrame(string Kind, string Speaker, string Text, IReadOnlyList<string> Options)
{
    public FrameIdentity? Identity { get; init; }
    public bool HasBackgroundTasks { get; init; }
    public bool NeedsTick => Kind == "wait" || HasBackgroundTasks;
    public string? EndAction { get; init; }
    public string? EndScreen { get; init; }
    public static StoryFrame Empty { get; } = new("idle", "", "", Array.Empty<string>());
    public bool Completed => Kind == "completed";
}
