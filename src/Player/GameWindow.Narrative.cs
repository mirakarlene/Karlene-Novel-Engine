using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace KNE;

public partial class GameWindow
{
    private DispatcherTimer? dialogueRevealTimer;
    private StoryFrame? dialogueRevealFrame;
    private long dialogueRevealSample;
    private double heldRevealSeconds;
    private int displayedCharacters;
    private bool animateDialogue;

    private double RevealElapsed => heldRevealSeconds + (dialogueRevealSample == 0 ? 0 : Stopwatch.GetElapsedTime(dialogueRevealSample).TotalSeconds);

    private void RenderNarrative()
    {
        if (clockSuspended || closed || startupFailed || systemFrozen || historyOpen || MenuOverlay.Visibility == Visibility.Visible)
        {
            PauseDialogueReveal();
            DialogueEffects.Stop();
            return;
        }
        animateDialogue = activeScenePerformance.EffectsQuality != "low" && preferences.Animate && project.Engine.Theme.AnimationEnabled && ready;
        InlineTextPresentation.SetFonts(this, project.Path, runtime.Visuals.Fonts);
        DialogueText.Visibility = Visibility.Visible;
        var story = runtime.Story;
        if (story.Reveal is { } reveal && (story.VisibleSuffix ?? story.VisiblePrefix) is int visible)
        {
            if (dialogueRevealFrame is not { } previous || previous.PresentationId != story.PresentationId ||
                previous.Reveal != reveal || previous.Text != story.Text || story.RemainingSeconds > previous.RemainingSeconds)
            {
                displayedCharacters = visible;
                heldRevealSeconds = 0;
                dialogueRevealSample = Stopwatch.GetTimestamp();
            }
            else if (previous.RemainingSeconds != story.RemainingSeconds)
            {
                heldRevealSeconds = 0;
                dialogueRevealSample = Stopwatch.GetTimestamp();
            }
            dialogueRevealFrame = story;
            if (dialogueRevealSample == 0) dialogueRevealSample = Stopwatch.GetTimestamp();
            displayedCharacters = Math.Max(displayedCharacters, visible);
            story = WithVisibleCharacters(story, reveal, displayedCharacters);
            dialogueRevealTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1d / 60), DispatcherPriority.Render, DialogueRevealTick, Dispatcher);
            if (displayedCharacters < reveal.Last) dialogueRevealTimer.Start();
        }
        else StopDialogueReveal();
        NarrativePresentation.Apply(SpeakerText, DialogueText, DialogueEffects, story, project.Engine.Theme, runtime.Visuals, animateDialogue);
        PlayerInput.ApplyAppearance(DialogueText);
        if (runtime.Story.Kind == "input") { DialogueText.Visibility = Visibility.Collapsed; DialogueEffects.Stop(); }
    }

    private void DialogueRevealTick(object? sender, EventArgs args)
    {
        if (clockSuspended || closed || startupFailed || systemFrozen || historyOpen || MenuOverlay.Visibility == Visibility.Visible ||
            dialogueRevealFrame is not { Reveal: { } reveal, RemainingSeconds: double remaining } story)
        {
            PauseDialogueReveal();
            return;
        }
        int visible = reveal.VisibleAt(remaining - RevealElapsed);
        if (visible <= displayedCharacters) return;
        displayedCharacters = visible;
        NarrativePresentation.ApplyDialogueText(DialogueText, DialogueEffects, WithVisibleCharacters(story, reveal, visible), animateDialogue);
        shaders?.NotifyContentChanged();
        if (visible == reveal.Last) dialogueRevealTimer?.Stop();
    }

    private static StoryFrame WithVisibleCharacters(StoryFrame story, DialogueReveal reveal, int visible) => reveal.FromEnd
        ? story with { VisibleSuffix = visible } : story with { VisiblePrefix = visible };

    private void PauseDialogueReveal()
    {
        dialogueRevealTimer?.Stop();
        heldRevealSeconds = RevealElapsed;
        dialogueRevealSample = 0;
    }

    private void StopDialogueReveal()
    {
        dialogueRevealTimer?.Stop();
        dialogueRevealFrame = null;
        dialogueRevealSample = 0;
        heldRevealSeconds = 0;
    }
}
