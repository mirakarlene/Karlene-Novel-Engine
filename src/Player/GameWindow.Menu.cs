using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace KNE;

public partial class GameWindow
{
    private bool scenePresentationSuspended;
    private bool restoreSceneImages;
    private bool returningToMenu;

    private void SuspendScenePresentation()
    {
        scenePresentationSuspended = true;
        preloading?.Cancel();
        preloading?.Dispose();
        preloading = null;
        performanceScene = performanceFunction = null;
        BackgroundImage.Source = CharacterImage.Source = DefaultBackgroundImage.Source = null;
        sceneEffects?.Stop();
        SceneTitle.Visibility = Visibility.Collapsed;
        gameplayUi?.Reset();
        refreshScreenResources = true;
        ScriptScreenViewbox.Visibility = MainScriptScreenViewbox.Visibility = Visibility.Collapsed;
        ScriptScreenCanvas.Children.Clear();
        MainScriptScreenCanvas.Children.Clear();
        if (shaders != null) _ = shaders.SetActiveAsync([]);
        assets.Clear();
        DefaultBackground.Background = EnginePresentation.CreateBackground(project.Engine.Theme);
        DefaultBackground.Visibility = Visibility.Visible;
        UpdateDefaultBackdropVisibility(true);
    }

    private bool ResumeScenePresentation()
    {
        if (!scenePresentationSuspended) return false;
        scenePresentationSuspended = false;
        restoreSceneImages = true;
        return true;
    }

    private BitmapSource? CaptureMenuTransition(MenuTransitionSettings transition) =>
        transition.IsEnabled && !preview && preferences.Animate && project.Engine.Theme.AnimationEnabled &&
        IsVisible && WindowState != WindowState.Minimized ? SceneTransitionPresenter.Capture(GameStage, SceneRasterScale) : null;

    private async Task ReturnToMainMenuAsync(bool restartWithoutMenu = false)
    {
        if (closed || returningToMenu) return;
        returningToMenu = true;
        try
        {
            var transition = project.Engine.Game.MainMenuReturnTransition;
            var previous = CaptureMenuTransition(transition);
            var root = GameStage;
            using var cover = previous == null ? null : SceneTransitionPresenter.Hold(root, previous);
            ready = false;
            CloseAudio();
            SuspendScenePresentation();
            await runtime.DisposeAsync();
            if (closed) return;
            runtime = CreateGameSession();
            rollback.Clear();
            autosave.Reset();
            sceneSaves.Reset();
            ResetTriggerInput();
            DialogueEffects.Reset();
            StopDialogueReveal();
            choicePresentation?.Reset();
            renderedStory = null;
            renderedVisuals = null;
            ResetPlayTime();
            if (project.Engine.Screens.Find("main_menu") == null && restartWithoutMenu)
            {
                cover?.Dispose();
                returningToMenu = false;
                await StartGameAsync();
            }
            else
            {
                ShowMenu();
                cover?.Dispose();
                if (previous != null)
                    await SceneTransitionPresenter.PlayMenuAsync(root, previous, transition, blockFlowLifetime.Token, SceneRasterScale);
            }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { if (!closed) ShowStartupError(error, "Возврат в главное меню"); }
        finally { returningToMenu = false; RunMainMenuEvent(); }
    }
}
