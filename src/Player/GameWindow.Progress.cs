using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private GameRollbackHistory rollback = null!;
    private readonly GameAutosavePolicy autosave = new();
    internal async Task RollbackAsync()
    {
        if (execution != null || !ready || rollback.Count == 0) return;
        GameCheckpoint previous = rollback.Peek();
        await ExecuteAsync(async () =>
        {
            await runtime.RestoreAsync(previous, execution!.Token); rollback.Pop();
            renderedStory = null; renderedSettings = null; renderedVisuals = null;
            triggerInput.Reset(); pendingTriggerSignals.Clear(); gameplayUi?.Reset(); DialogueEffects.Reset(); choicePresentation?.Reset();
            CloseAudio(); sceneEffects?.Stop(); ShowGameplay(); autosave.Reset();
        }, operation: "Откат игры");
    }
    private async Task<GameCheckpoint> CheckpointAsync(CancellationToken token) => rollback.Attach(await runtime.GetCheckpointAsync(token));
    private bool menuMusicPlaying;
    private void StartMenuMusic()
    {
        if (ready || string.IsNullOrWhiteSpace(project.Engine.Game.MenuMusic)) return;
        music.Close(); music.BufferProfile = project.Engine.Performance.AudioBuffer; music.Open(new Uri(ResourcePaths.Resolve(project.Path, project.Engine.Game.MenuMusic, audio: true))); music.Volume = preferences.MusicVolume; menuMusicPlaying = true; music.Play();
    }
    private void StopMenuMusic()
    {
        if (!menuMusicPlaying) return; menuMusicPlaying = false; music.Close();
    }
}
