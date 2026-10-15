namespace KNE;

public partial class GameWindow
{
    private GameplayUiPresenter? gameplayUi;

    private void ApplyGameplayUi(SceneVisuals visuals)
    {
        gameplayUi ??= new(SceneArea, DialoguePanel, SpeakerText, DialogueText, ChoicePanel,
            new Dictionary<string, System.Windows.Controls.Button>
            {
                ["next"] = NextButton, ["save"] = SaveButton, ["load"] = LoadButton,
                ["auto_save"] = LoadAutoButton, ["pause"] = PauseButton
            }, project.Path);
        gameplayUi.Apply(visuals);
    }
}
