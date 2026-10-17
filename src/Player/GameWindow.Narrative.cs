using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private void RenderNarrative()
    {
        if (clockSuspended || closed || startupFailed || MenuOverlay.Visibility == Visibility.Visible) { DialogueEffects.Stop(); return; }
        bool animate = preferences.Animate && project.Engine.Theme.AnimationEnabled && ready;
        NarrativePresentation.Apply(SpeakerText, DialogueText, DialogueEffects, runtime.Story, project.Engine.Theme, runtime.Visuals, animate);
    }
}
