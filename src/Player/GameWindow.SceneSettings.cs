namespace KNE;

public partial class GameWindow
{
    private readonly SceneSavePolicy sceneSaves = new();
    private bool CanSaveInScene => !preMenuActive && SaveStorageWritable && SceneSavePolicy.AllowManual(runtime.Visuals.Scene);
    private async Task SaveSceneEntryAsync(CancellationToken token)
    {
        if (preMenuActive || !SaveStorageWritable || !sceneSaves.ShouldCreateEntry(runtime.Visuals.Scene, runtime.Story)) return;
        await StoreSaveAsync(SceneSavePolicy.SlotName(runtime.Visuals.Scene!), token);
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
