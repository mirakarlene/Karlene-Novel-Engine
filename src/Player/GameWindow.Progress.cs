using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private GameRollbackHistory rollback = null!;
    private readonly GameAutosavePolicy autosave = new();
    private bool historyOpen;

    private static bool SameNarrativeStop(StoryFrame previous, StoryFrame current) =>
        previous.Kind == current.Kind && previous.PresentationId == current.PresentationId &&
        (previous.PresentationId != 0 || previous.Text == current.Text);

    private Task<GameCheckpoint> CaptureRollbackPositionAsync(CancellationToken token) =>
        runtime is ProcessGameSession worker && worker.CachedRollbackPosition(ReadPlayTime()) is { } saved
            ? Task.FromResult(saved)
            : runtime.CaptureCheckpointAsync(token);

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryButtonTriggerAsync(sender)) await RollbackAsync();
    }

    private async void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TryButtonTriggerAsync(sender) || historyOpen || execution != null || pumpingSceneTime ||
            runtime.DialogueJournal is not { Limit: > 0 } journal) return;
        var window = new GameHistoryWindow(this, journal);
        historyOpen = true;
        PauseDialogueReveal();
        DialogueEffects.Stop();
        choicePresentation?.Stop();
        try { window.ShowDialog(); }
        finally
        {
            historyOpen = false;
            lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!closed) RenderOutput();
        }
    }

    internal async Task RollbackAsync()
    {
        if (execution != null || pumpingSceneTime || !ready || historyOpen || rollback.Count == 0) return;
        GameCheckpoint previous = rollback.Peek();
        if (previous.DialoguePosition is { } position && runtime.DialogueJournal is { } journal)
            previous = previous with { DialogueJournal = journal.Rewind(position) };
        await ExecuteAsync(async () =>
        {
            await runtime.RestoreAsync(previous, execution!.Token); rollback.Pop();
            ResetPlayTime(previous.PlayedSeconds);
            sceneSaves.Reset(); sceneSaves.MarkRestored(runtime.Visuals.Scene);
            renderedStory = null; renderedSettings = null; renderedVisuals = null;
            ResetTriggerInput(); gameplayUi?.Reset(); StopDialogueReveal(); DialogueEffects.Reset(); choicePresentation?.Reset();
            CloseAudio(); sceneEffects?.Stop(); ShowGameplay(); autosave.Reset();
        }, operation: "Откат игры");
    }
    private async Task<GameCheckpoint> CheckpointAsync(CancellationToken token)
    {
        if (!CanSaveInScene) throw new InvalidOperationException("Сохранения в этой сцене запрещены.");
        return rollback.Attach(await runtime.GetCheckpointAsync(token));
    }
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
