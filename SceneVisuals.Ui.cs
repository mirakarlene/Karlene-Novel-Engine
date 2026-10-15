namespace KNE;

public sealed partial record SceneVisuals
{
    public GameplayUiConfiguration[] UiConfigurations { get; init; } = [];
    public long UiVersion { get; init; }
}
