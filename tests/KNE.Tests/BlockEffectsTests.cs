using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class BlockEffectsTests
{
    [Fact]
    public async Task Effects_CommandsPublishTypedIndependentState_AndPreserveDialogue()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
              show_title("Глава первая")
              move_visual("background", 25, -80, 0.5)
              move_visual("sprite", -50, 40, 1)
              animate_visual("sprite", "pulse", 0.8, 1.2)
              screen_shake(12, 0.4)
              screen_flash("#88CCFF", 0.2)
              camera(1.5, 100, -25, 0.3)
              show_screen("settings")
              d(nil, "Реплика")
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Реплика", runtime.Story.Text);
        var visuals = runtime.Visuals;
        Assert.Equal("Глава первая", visuals.Title);
        Assert.Equal(new VisualMotion(25, -80, .5, 1), visuals.BackgroundMotion);
        Assert.Equal(new VisualMotion(-50, 40, 1, 1), visuals.CharacterMotion);
        Assert.Equal(new VisualAnimation("pulse", .8, 1.2, 1), visuals.CharacterAnimation);
        Assert.Equal(new SceneCamera(1.5, 100, -25, .3, 1), visuals.Camera);
        Assert.Equal(new SceneShake(12, .4, 1), visuals.Shake);
        Assert.Equal(new SceneFlash("#88CCFF", .2, 1), visuals.Flash);
        Assert.Equal("settings", visuals.OverlayScreen);
        var roundTrip = JsonSerializer.Deserialize<SceneVisuals>(JsonSerializer.Serialize(visuals))!;
        Assert.Equal(visuals.Camera, roundTrip.Camera); Assert.Equal(visuals.Title, roundTrip.Title);
    }

    [Theory]
    [InlineData("camera(0,0,0,1)", "camera")]
    [InlineData("move_visual('window',0,0,1)", "move_visual")]
    [InlineData("animate_visual('sprite','unknown',1,1)", "animate_visual")]
    [InlineData("screen_flash('invalid-color',1)", "screen_flash")]
    [InlineData("set_master_volume(2)", "set_master_volume")]
    [InlineData("set_audio_channel('unknown',1,false)", "set_audio_channel")]
    [InlineData("show_screen('missing')", "show_screen")]
    [InlineData("screen_shake(0/0,1)", "screen_shake")]
    public async Task Effects_InvalidArgumentsFailAtScriptLocation_WithoutCorruptingState(string command, string name)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n " + command + "\nend");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(name, error.ToString()); Assert.Equal(1, runtime.Visuals.Camera.Zoom); Assert.Equal(1, runtime.Visuals.MasterVolume);
        var report = GameErrorReport.TryGet(error); Assert.NotNull(report);
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(2, report.Line);
    }

    [Fact]
    public async Task Effects_WorkerTransportPreservesVisualCommandsAndIndependentAudioChannelConfiguration()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
              show_title("Worker")
              camera(2, 30, -10, 0.2)
              set_audio_channel("ambient", 0.35, true)
              set_master_volume(0.7)
              d(nil,"snapshot")
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        Assert.Equal("snapshot", session.Story.Text); Assert.Equal("Worker", session.Visuals.Title);
        Assert.Equal(new SceneCamera(2, 30, -10, .2, 1), session.Visuals.Camera);
        Assert.Equal(new AudioChannelState("ambient", Loop: true, Volume: .35), Assert.Single(session.Visuals.AudioChannels));
        Assert.Equal(.7, session.Visuals.MasterVolume);
    }

    [Fact]
    public async Task Audio_FourChannelsKeepIndependentLoopVolumeAndStopState_AndLegacyCommandsShareChannels()
    {
        using var fixture = new TempProject();
        fixture.Write("game/audio/test.wav", "dummy resource");
        fixture.Write("game/scenes/main.krl", """
            function main()
              set_master_volume(0.5)
              set_audio_channel("voice", 0.3, false)
              play_audio("ambient", "game/audio/test.wav", true)
              play_audio("voice", "game/audio/test.wav", false)
              play_music("game/audio/test.wav",true)
              play_sound("game/audio/test.wav")
              stop_audio("sfx")
              d(nil,"one")
              stop_music()
              play_audio("voice", "game/audio/test.wav", false)
              d(nil,"two")
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var channels = runtime.Visuals.AudioChannels.ToDictionary(item => item.Channel);
        Assert.Equal(4, channels.Count); Assert.True(channels["ambient"].Loop); Assert.False(channels["voice"].Loop);
        Assert.Equal(.3, channels["voice"].Volume); Assert.Null(channels["sfx"].Path); Assert.NotNull(channels["music"].Path);
        Assert.Equal(.5, runtime.Visuals.MasterVolume);
        await runtime.AdvanceAsync();
        var next = runtime.Visuals.AudioChannels.ToDictionary(item => item.Channel);
        Assert.Null(next["music"].Path); Assert.Equal(channels["ambient"], next["ambient"]);
        Assert.Equal(channels["voice"].Version + 1, next["voice"].Version);
    }

    [Fact]
    public async Task FrozenPreview_RendersTitleTransformsAndOverlay_WithoutAnimatingOrPlayingAudio()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var view = new ScenePreview();
            var visuals = SceneVisuals.Empty with { Title = "Титр", CharacterMotion = new(90, -20, 1, 1), Camera = new(1.5, 12, 18, 1, 1), OverlayScreen = "settings", ScreenVersion = 1, CharacterAnimation = new("fade_out", 1, 1, 1) };
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, new("dialogue", "", "Текст", []), animate: false, allowChoices: false);
            Assert.Equal("Титр", Find<TextBlock>(view, "SceneTitle").Text); Assert.Equal("Текст", Find<TextBlock>(view, "DialogueText").Text);
            var sprite = Find<Image>(view, "CharacterImage"); Assert.Equal(0, sprite.Opacity); Assert.False(sprite.HasAnimatedProperties);
            var motion = ((TransformGroup)sprite.RenderTransform).Children.OfType<TranslateTransform>().Single(); Assert.Equal(90, motion.X); Assert.Equal(-20, motion.Y);
            var camera = ((TransformGroup)Find<Grid>(view, "VisualLayer").RenderTransform).Children.OfType<ScaleTransform>().Single(); Assert.Equal(1.5, camera.ScaleX);
            Assert.Equal(Visibility.Visible, Find<Viewbox>(view, "ScriptScreenViewbox").Visibility); Assert.NotEmpty(Find<Canvas>(view, "ScriptScreenCanvas").Children.Cast<UIElement>());
            view.Stop(); Assert.Equal(1, camera.ScaleX); Assert.Equal(0, motion.X);
        });
    }

    [Fact]
    public async Task GameAudio_PlayerVolumeMultipliesUserMasterAndChannel_AndCleanupClosesAllPlayers()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project);
            var state = SceneVisuals.Empty with { MasterVolume = .5, AudioChannels = [new("music", Volume: .25), new("voice", Volume: .4), new("ambient", Volume: .5)] };
            typeof(GameWindow).GetMethod("RefreshAudioVolumes", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [state]);
            var players = (Dictionary<string, GameAudioPlayer>)typeof(GameWindow).GetField("audioPlayers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.Equal(new[] { "ambient", "music", "sfx", "voice" }, players.Keys.Order().ToArray());
            Assert.Equal(.1, players["music"].Volume, 6); Assert.Equal(.16, players["voice"].Volume, 6); Assert.Equal(.2, players["ambient"].Volume, 6); Assert.Equal(.4, players["sfx"].Volume, 6);
            typeof(GameWindow).GetMethod("CloseAudio", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Assert.All(players.Values, player => Assert.Null(player.Source)); window.Close();
        });
    }

    [Fact]
    public async Task GameEnd_MenuUsesMainLayoutAndScreenUsesRequestedLayout_WithoutReturningToPause()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project);
            var handle = typeof(GameWindow).GetMethod("HandleSceneEnd", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var ready = typeof(GameWindow).GetField("ready", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var activeScreen = typeof(GameWindow).GetField("activeScreen", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ready.SetValue(window, true);
            handle.Invoke(window, [new StoryFrame("completed", "", "", []) { EndAction = "menu" }]);
            Assert.False((bool)ready.GetValue(window)!); Assert.Equal("main_menu", activeScreen.GetValue(window));
            handle.Invoke(window, [new StoryFrame("completed", "", "", []) { EndAction = "screen", EndScreen = "settings" }]);
            Assert.Equal("settings", activeScreen.GetValue(window)); window.Close();
        });
    }

    [Fact]
    public async Task GameEnd_ExitClosesGame_WhilePreviewExitKeepsTestingHostOpen()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var handle = typeof(GameWindow).GetMethod("HandleSceneEnd", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var game = new GameWindow(project); bool gameClosed = false; game.Closed += (_, _) => gameClosed = true;
            handle.Invoke(game, [new StoryFrame("completed", "", "", []) { EndAction = "exit" }]);
            Assert.True(gameClosed);
            var preview = new GameWindow(project, preview: true); bool previewClosed = false; preview.Closed += (_, _) => previewClosed = true;
            handle.Invoke(preview, [new StoryFrame("completed", "", "", []) { EndAction = "exit" }]);
            Assert.False(previewClosed); preview.Close();
        });
    }

    [Fact]
    public async Task Preview_RepeatedFrameRenderingDoesNotRestartVisualAnimationClocks_AndStopClearsThem()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var view = new ScenePreview();
            var state = SceneVisuals.Empty with { CharacterAnimation = new("fade_out", 1, 1, 1), Camera = new(1.4, 15, 8, 1, 1), Flash = new("#FFFFFF", .5, 1) };
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), state, new("dialogue", "", "one", []), animate: true, allowChoices: false);
            var sprite = Find<Image>(view, "CharacterImage"); Assert.True(sprite.HasAnimatedProperties);
            // Simulate an already detached/completed opacity clock. Updating dialogue must not replay its command.
            sprite.BeginAnimation(UIElement.OpacityProperty, null);
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), state, new("dialogue", "", "two", []), animate: true, allowChoices: false);
            Assert.False(sprite.HasAnimatedProperties); Assert.Equal("two", Find<TextBlock>(view, "DialogueText").Text);
            var camera = (TransformGroup)Find<Grid>(view, "VisualLayer").RenderTransform;
            Assert.True(camera.Children.OfType<ScaleTransform>().Single().HasAnimatedProperties);
            view.Stop();
            Assert.All(camera.Children, transform => Assert.False(transform.HasAnimatedProperties));
            Assert.False(Find<Border>(view, "SceneFlash").HasAnimatedProperties);
        });
    }

    [Fact]
    public async Task Preview_ResetPresentationReplaysSameVersionEffects_AndStopClearsRestartedClocks()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var view = new ScenePreview();
            var state = SceneVisuals.Empty with { CharacterAnimation = new("fade_in", 1, 1, 1) };
            var frame = new StoryFrame("dialogue", "", "first", []);
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), state, frame, animate: true, allowChoices: false);
            var sprite = Find<Image>(view, "CharacterImage"); Assert.True(sprite.HasAnimatedProperties);
            sprite.BeginAnimation(UIElement.OpacityProperty, null); Assert.False(sprite.HasAnimatedProperties);
            view.ResetPresentation();
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), state, frame, animate: true, allowChoices: false);
            Assert.True(sprite.HasAnimatedProperties); Assert.Equal("first", Find<TextBlock>(view, "DialogueText").Text);
            view.Stop(); Assert.False(sprite.HasAnimatedProperties);
        });
    }

    [Fact]
    public async Task Preview_TickUpdatesKeepExistingChoiceButtonsAndDoNotRestartBackdrop()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var view = new ScenePreview(); var story = new StoryFrame("choice", "", "Куда?", ["Лево", "Право"]);
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, story, animate: true, allowChoices: true);
            var choices = Find<StackPanel>(view, "ChoicePanel"); var firstButton = Assert.IsAssignableFrom<Button>(choices.Children[0]);
            var backdrop = Find<AnimatedBackground>(view, "AnimatedBackdrop"); backdrop.Step(.05); Assert.True(backdrop.Phase > 0);
            backdrop.Phase = 1.25;
            view.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty with { Title = "Ветка обновилась" },
                new("choice", "", "Куда?", ["Лево", "Право"]), animate: true, allowChoices: true);
            Assert.Same(firstButton, choices.Children[0]); Assert.Equal(1.25, backdrop.Phase); backdrop.Step(.05); Assert.True(backdrop.Phase > 1.25);
            Assert.Equal("Ветка обновилась", Find<TextBlock>(view, "SceneTitle").Text); view.Stop();
        });
    }

    [Fact]
    public async Task Overlay_EmptyScreenCommandClearsLayer_AndBackButtonDismissesWithoutChangingScene()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() show_screen('settings') d(nil,'one') show_screen('') d(nil,'two') end");
        var project = await fixture.LoadAsync();
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("settings", runtime.Visuals.OverlayScreen); Assert.Equal(1, runtime.Visuals.ScreenVersion);
        await runtime.AdvanceAsync(); Assert.Null(runtime.Visuals.OverlayScreen); Assert.Equal(2, runtime.Visuals.ScreenVersion); Assert.Equal("two", runtime.Story.Text);
        await Sta(() =>
        {
            var window = new GameWindow(project);
            typeof(GameWindow).GetMethod("RenderBlockEffects", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                [SceneVisuals.Empty with { OverlayScreen = "settings", ScreenVersion = 1 }]);
            var layer = Find<Viewbox>(window, "ScriptScreenViewbox"); Assert.Equal(Visibility.Visible, layer.Visibility);
            var back = Find<Canvas>(window, "ScriptScreenCanvas").Children.OfType<Button>().Single(button => ((ScreenElement)button.Tag).Action == "back");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(Visibility.Collapsed, layer.Visibility); window.Close();
        });
    }

    private static T Find<T>(FrameworkElement view, string name) => (T)view.FindName(name);
    private static Task Sta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); } catch (Exception error) { completed.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
