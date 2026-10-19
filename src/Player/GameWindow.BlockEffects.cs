using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private SceneEffectsPresenter? sceneEffects;
    private readonly Dictionary<string, MediaPlayer> audioPlayers = [];
    private readonly HashSet<string> activeAudioChannels = [];
    private StoryFrame? handledEnd;

    private void InitializeBlockEffects()
    {
        sceneEffects = new(VisualLayer, BackgroundImage, CharacterImage, SceneFlash, SceneTitle);
        audioPlayers["music"] = music; audioPlayers["sfx"] = sound;
        foreach (string channel in new[] { "voice", "ambient" })
        {
            var player = new MediaPlayer(); audioPlayers[channel] = player;
            player.MediaFailed += (_, error) => QueueOutput("Ошибка аудио «" + channel + "»: " + error.ErrorException.Message);
        }
        foreach (var pair in audioPlayers)
            pair.Value.MediaEnded += (_, _) =>
            {
                if (runtime.Visuals.AudioChannels.FirstOrDefault(item => item.Channel == pair.Key)?.Loop == true)
                { pair.Value.Position = TimeSpan.Zero; pair.Value.Play(); }
                else if (pair.Key != "music" || !runtime.Visuals.LoopMusic) activeAudioChannels.Remove(pair.Key);
            };
    }

    private void RenderBlockEffects(SceneVisuals scene)
    {
        sceneEffects!.Apply(scene, preferences.Animate && project.Engine.Theme.AnimationEnabled && activeScenePerformance.EffectsQuality != "low", activeScenePerformance.PostProcessing);
        if (scene.ScreenVersion != renderedVisuals?.ScreenVersion || scene.OverlayScreen != renderedVisuals?.OverlayScreen)
        {
            ScriptScreenCanvas.Children.Clear();
            MainScriptScreenCanvas.Children.Clear();
            var layout = project.Engine.Screens.Screens.FirstOrDefault(item => item.Id == scene.OverlayScreen);
            ScriptScreenViewbox.Visibility = layout != null && scene.ScreenLayer == "overlay" ? Visibility.Visible : Visibility.Collapsed;
            MainScriptScreenViewbox.Visibility = layout != null && scene.ScreenLayer == "main" ? Visibility.Visible : Visibility.Collapsed;
            ScriptScreenViewbox.Opacity = MainScriptScreenViewbox.Opacity = scene.ScreenOpacity;
            var targetCanvas = scene.ScreenLayer == "main" ? MainScriptScreenCanvas : ScriptScreenCanvas;
            if (layout != null)
                foreach (var element in layout.Elements)
                {
                    FrameworkElement view;
                    try { view = ScreenRenderer.Create(element, project.Engine.Theme, project.Path, ResolveScreenText); }
                    catch (Exception error) when (error is System.IO.IOException or NotSupportedException or ArgumentException or FormatException)
                    { QueueOutput("Ошибка элемента экрана: " + error.Message); continue; }
                    view.Width = element.Width; view.Height = element.Height; Canvas.SetLeft(view, element.X); Canvas.SetTop(view, element.Y);
                    GameTriggerInput.SetButtonId(view, "screen:" + layout.Name + "/" + element.Id);
                    if (view is Button button) button.Click += async (_, _) =>
                    {
                        if (await TryButtonTriggerAsync(view)) return;
                        if (element.Action is "back" or "resume") ScriptScreenViewbox.Visibility = MainScriptScreenViewbox.Visibility = Visibility.Collapsed;
                        else await DispatchScreenActionAsync(element);
                    };
                    targetCanvas.Children.Add(view);
                    ScreenRenderer.Animate(view, element, preferences.Animate && project.Engine.Theme.AnimationEnabled);
                }
        }
        foreach (var channel in scene.AudioChannels)
        {
            var player = audioPlayers[channel.Channel];
            var previous = renderedVisuals?.AudioChannels.FirstOrDefault(item => item.Channel == channel.Channel);
            if (channel.Version != previous?.Version || channel.Path != previous?.Path)
            {
                player.Close();
                activeAudioChannels.Remove(channel.Channel);
                if (channel.Path != null) { player.Open(new Uri(channel.Path)); player.Play(); activeAudioChannels.Add(channel.Channel); }
            }
        }
        RefreshAudioVolumes(scene);
    }

    private void RefreshAudioVolumes(SceneVisuals? state = null)
    {
        state ??= runtime?.Visuals ?? SceneVisuals.Empty;
        foreach (var pair in audioPlayers)
        {
            double preference = pair.Key is "music" or "ambient" ? preferences.MusicVolume : preferences.SoundVolume;
            double channel = state.AudioChannels.FirstOrDefault(item => item.Channel == pair.Key)?.Volume ?? 1;
            pair.Value.Volume = Math.Clamp(preference * state.MasterVolume * channel, 0, 1);
        }
    }
    private void CloseAudio() { foreach (var player in audioPlayers.Values) player.Close(); activeAudioChannels.Clear(); }
    private void PauseAudio() { foreach (var player in audioPlayers.Values) player.Pause(); }
    private void ResumeAudio()
    {
        foreach (string channel in activeAudioChannels) audioPlayers[channel].Play();
    }
    private void HandleSceneEnd(StoryFrame frame)
    {
        if (frame.EndAction == null || ReferenceEquals(handledEnd, frame)) return;
        handledEnd = frame;
        CloseAudio();
        if (preview) return;
        switch (frame.EndAction)
        {
            case "menu": ready = false; ShowMenu(); break;
            case "screen": ready = false; ShowLayout(project.Engine.Screens.ResolveReference(frame.EndScreen!).Id); break;
            case "exit": Close(); break;
        }
    }
}
