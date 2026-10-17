namespace KNE;

public partial class ScenePreview
{
    private void RenderNarrative(StoryFrame story, SceneVisuals visuals, bool animate)
    {
        if (project is null) return;
        NarrativePresentation.Apply(SpeakerText, DialogueText, DialogueEffects, story, project.Engine.Theme, visuals, animate && project.Engine.Theme.AnimationEnabled);
    }
}
