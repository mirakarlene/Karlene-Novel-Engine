namespace KNE;

public partial class GameWindow
{
    private readonly SceneSavePolicy sceneSaves = new();
    private bool CanSaveInScene => SceneSavePolicy.AllowManual(runtime.Visuals.Scene);
    private async Task SaveSceneEntryAsync(CancellationToken token)
    {
        if (!sceneSaves.ShouldCreateEntry(runtime.Visuals.Scene, runtime.Story)) return;
        await saves.SaveAsync(SceneSavePolicy.SlotName(runtime.Visuals.Scene!), await CheckpointAsync(token), token);
    }
    private bool presentDuringOperation;
    private bool SceneChanged(SceneRuntimeState? previous, bool starting = false) => runtime.Visuals.Scene is { } current &&
        (starting || previous == null || previous.Visit != current.Visit || previous.Exited != current.Exited);
    private bool NeedsSceneTransition(SceneRuntimeState? previous, bool starting = false) => preferences.Animate && SceneChanged(previous, starting) &&
        (previous?.Settings.ExitTransition.Kind is not (null or "none") || runtime.Visuals.Scene is { Exited: false, Settings.EntryTransition.Kind: not "none" });
    private void PresentSceneOutput()
    {
        presentDuringOperation = true;
        try { RenderOutput(); } finally { presentDuringOperation = false; }
    }
}
