using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private void RenderNarrative()
    {
        if (clockSuspended || closed || startupFailed || MenuOverlay.Visibility == Visibility.Visible) { DialogueEffects.Stop(); return; }
        bool animate = activeScenePerformance.EffectsQuality != "low" && preferences.Animate && project.Engine.Theme.AnimationEnabled && ready;
        DialogueText.Visibility = Visibility.Visible;
        NarrativePresentation.Apply(SpeakerText, DialogueText, DialogueEffects, runtime.Story, project.Engine.Theme, runtime.Visuals, animate);
        PlayerInput.ApplyAppearance(DialogueText);
        if (runtime.Story.Kind == "input") { DialogueText.Visibility = Visibility.Collapsed; DialogueEffects.Stop(); }
    }
}
