namespace KNE;

/// <summary>The visible checkpoint of a suspended scene.</summary>
public sealed record StoryFrame(string Kind, string Speaker, string Text, IReadOnlyList<string> Options)
{
    public FrameIdentity? Identity { get; init; }
    /// <summary>Currently armed, scene-scoped input subscriptions.</summary>
    public IReadOnlyList<TriggerSettings> Triggers { get; init; } = [];
    /// <summary>Deterministic occurrence of the displayed dialogue, independent of its source block identity.</summary>
    public long PresentationId { get; init; }
    public bool HasBackgroundTasks { get; init; }
    public bool NeedsTick => Kind == "wait" || HasBackgroundTasks || RemainingSeconds.HasValue || ChoiceTimer != null;
    /// <summary>Original-index resolved option states; null keeps the legacy enabled-choice behavior.</summary>
    public IReadOnlyList<ChoiceOptionState>? ChoiceOptions { get; init; }
    /// <summary>Authoritative countdown of the visible timed choice.</summary>
    public ChoiceTimerState? ChoiceTimer { get; init; }
    public CharacterProfile? Character { get; init; }
    public TextEffectSettings? TextEffect { get; init; }
    /// <summary>Configuration of the currently pending player-input request.</summary>
    public TextInputSettings? Input { get; init; }
    /// <summary>Feedback for an invalid answer without an error continuation.</summary>
    public string InputFeedback { get; init; } = "";
    public double? RemainingSeconds { get; init; }
    public bool IsSkippable { get; init; } = true;
    public double EraseProgress { get; init; }
    public bool CanAdvance => Kind == "preview" || (Kind is "dialogue" or "text_pause" && IsSkippable);
    public string? EndAction { get; init; }
    public string? EndScreen { get; init; }
    public static StoryFrame Empty { get; } = new("idle", "", "", Array.Empty<string>());
    public bool Completed => Kind == "completed";
}
