namespace KNE;

public partial class ScenePreview
{
    private void RenderNarrative(StoryFrame story, SceneVisuals visuals, bool animate)
    {
        if (project is null) return;
        DialogueText.Visibility = System.Windows.Visibility.Visible;
        NarrativePresentation.Apply(SpeakerText, DialogueText, DialogueEffects, story, project.Engine.Theme, visuals, animate && project.Engine.Theme.AnimationEnabled);
        PlayerInput.ApplyAppearance(DialogueText);
        if (story.Kind == "input") { DialogueText.Visibility = System.Windows.Visibility.Collapsed; DialogueEffects.Stop(); }
    }
}
