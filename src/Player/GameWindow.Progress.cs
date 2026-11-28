using System.Windows;
using System.Windows.Controls;

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
        if (await TryButtonTriggerAsync(sender) || historyOpen || execution != null || pumpingSceneTime || !ready ||
            MenuOverlay.Visibility == Visibility.Visible ||
            runtime.DialogueJournal is not { Limit: > 0 } journal) return;
        OpenHistory(journal);
    }

    private void OpenHistory(DialogueJournalSnapshot journal)
    {
        historyOpen = true;
        ResetTriggerInput();
        MenuScroll.Visibility = ScreenViewbox.Visibility = SaveBrowserPanel.Visibility = DialogueSpeedFallback.Visibility = Visibility.Collapsed;
        RenderHistoryLayout();
        HistoryEntries.ItemsSource = journal.Entries;
        HistoryEmpty.Visibility = journal.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryScreen.Visibility = MenuOverlay.Visibility = Visibility.Visible;
        DialoguePanel.Visibility = GameControls.Visibility = ScriptHud.Visibility = Visibility.Collapsed;
        UpdatePlayClock();
        PauseAudio();
        PauseDialogueReveal();
        DialogueEffects.Stop();
        choicePresentation?.Stop();
        if (journal.Entries.Count > 0 && HistoryEntries.Parent != null) HistoryEntries.ScrollIntoView(journal.Entries[^1]);
        if (HistoryBackButton.Parent != null) HistoryBackButton.Focus();
    }

    private void RenderHistoryLayout()
    {
        var layout = project.Engine.Screens.Find("history") ?? ScreenLayouts.CreateHistoryScreen();
        HistoryCanvas.Children.Clear();
        string ResolveText(string text) => translations.ScreenText(project, GameTextCategory.Interface, text);
        foreach (var element in layout.Elements)
        {
            FrameworkElement view;
            switch (element.Type)
            {
                case "history_list":
                    HistoryEntries.FontSize = element.FontSize;
                    HistoryEmpty.FontSize = element.FontSize;
                    HistoryEmpty.Text = ResolveText(element.Text);
                    HistoryEmpty.Width = element.Width;
                    Canvas.SetLeft(HistoryEmpty, element.X);
                    Canvas.SetTop(HistoryEmpty, element.Y + element.Height / 2 - element.FontSize);
                    view = HistoryEntries;
                    break;
                case "button" when element.Action == "back" && HistoryBackButton.Parent == null:
                    HistoryBackButton.Content = ResolveText(element.Text);
                    HistoryBackButton.FontSize = element.FontSize;
                    view = HistoryBackButton;
                    break;
                default:
                    try { view = ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveText, path => assets.Load(path)); }
                    catch (Exception error) when (error is System.IO.IOException or ArgumentException or FormatException or NotSupportedException)
                    { QueueOutput("Ошибка элемента истории: " + error.Message); continue; }
                    if (view is Button button)
                        button.Click += async (_, _) =>
                        {
                            if (element.Action == "back") CloseHistory();
                            else await DispatchScreenActionAsync(element);
                        };
                    break;
            }
            view.Width = element.Width;
            view.Height = element.Height;
            Canvas.SetLeft(view, element.X);
            Canvas.SetTop(view, element.Y);
            if (element.SupportsOpacity) view.Opacity = element.Opacity;
            HistoryCanvas.Children.Add(view);
            if (element.Type == "history_list") HistoryCanvas.Children.Add(HistoryEmpty);
            ScreenRenderer.Animate(view, element, preferences.Animate && project.Engine.Theme.AnimationEnabled);
        }
    }

    private void HistoryBack_Click(object sender, RoutedEventArgs e) => CloseHistory();

    private void CloseHistory()
    {
        if (!historyOpen) return;
        ShowGameplay();
        RenderOutput();
        HistoryButton.Focus();
    }

    private void ClearHistoryScreen()
    {
        historyOpen = false;
        HistoryScreen.Visibility = Visibility.Collapsed;
        HistoryEntries.ItemsSource = null;
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
