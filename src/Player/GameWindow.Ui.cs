namespace KNE;

public partial class GameWindow
{
    private GameplayUiPresenter? gameplayUi;

    private void ApplyGameplayUi(SceneVisuals visuals)
    {
        gameplayUi ??= new(SceneArea, DialoguePanel, SpeakerText, DialogueText, ChoicePanel,
            new Dictionary<string, System.Windows.Controls.Button>
            {
                ["save"] = SaveButton, ["load"] = LoadButton,
                ["auto_save"] = LoadAutoButton, ["pause"] = PauseButton,
                ["rollback"] = RollbackButton, ["history"] = HistoryButton
            }, project.Path, assets);
        gameplayUi.Apply(visuals);
    }
}
