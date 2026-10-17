namespace KNE;

/// <summary>The visible checkpoint of a suspended scene.</summary>
public sealed record StoryFrame(string Kind, string Speaker, string Text, IReadOnlyList<string> Options)
{
    public FrameIdentity? Identity { get; init; }
    /// <summary>Deterministic occurrence of the displayed dialogue, independent of its source block identity.</summary>
    public long PresentationId { get; init; }
    public bool HasBackgroundTasks { get; init; }
    public bool NeedsTick => Kind == "wait" || HasBackgroundTasks || RemainingSeconds.HasValue;
    public CharacterProfile? Character { get; init; }
    public TextEffectSettings? TextEffect { get; init; }
    public double? RemainingSeconds { get; init; }
    public bool IsSkippable { get; init; } = true;
    public double EraseProgress { get; init; }
    public bool CanAdvance => Kind == "preview" || (Kind is "dialogue" or "text_pause" && IsSkippable);
    public string? EndAction { get; init; }
    public string? EndScreen { get; init; }
    public static StoryFrame Empty { get; } = new("idle", "", "", Array.Empty<string>());
    public bool Completed => Kind == "completed";
}
