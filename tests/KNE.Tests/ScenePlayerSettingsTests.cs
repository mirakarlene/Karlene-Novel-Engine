using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class ScenePlayerSettingsTests
{
    [Fact]
    public Task PlayerBlocksManualAutoAndEntryWritesInForbiddenScenesButAllowsLoadingAndAnIndependentEntryCheckpointInTheNextScene() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = true, AutoSaveInterval = "dialogue", RollbackDepth = 3 }, EngineSettings.JsonOptions));
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'locked:1'); d(nil,'locked:2'); goto_scene(allowed) end\nfunction allowed() d(nil,'allowed:1'); d(nil,'allowed:2') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new()
        {
            ["main"] = new() { AllowSaves = false, AllowAutosave = true, CheckpointOnEntry = true },
            ["allowed"] = new() { AllowAutosave = false, CheckpointOnEntry = true }
        } } });
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var window = new GameWindow(project, slots);
        try
        {
            await InvokeTask(window, "StartGameAsync"); var runtime = Field<IGameSession>(window, "runtime"); Assert.Equal("locked:1", runtime.Story.Text); Assert.False(Find<Button>(window, "SaveButton").IsEnabled);
            Assert.Empty(await slots.ListAsync()); Find<Button>(window, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => true); Assert.Empty(await slots.ListAsync());
            var oldCheckpoint = await runtime.CaptureCheckpointAsync(); await slots.SaveAsync("existing", oldCheckpoint); byte[] existing = File.ReadAllBytes(slots.SlotPath("existing"));
            await InvokeTask(window, "LoadGameAsync", "existing"); runtime = Field<IGameSession>(window, "runtime"); Assert.Equal("locked:1", runtime.Story.Text); Assert.False(runtime.Visuals.Scene!.Settings.AllowSaves);
            Assert.Single(await slots.ListAsync()); Assert.Equal(existing, File.ReadAllBytes(slots.SlotPath("existing")));
            await Next(window, "locked:2"); Assert.Single(await slots.ListAsync()); Assert.False(slots.Exists("auto")); Assert.False(slots.Exists("quick")); Assert.Equal(1, Field<GameRollbackHistory>(window, "rollback").Count);
            await Next(window, "allowed:1"); Assert.True(Find<Button>(window, "SaveButton").IsEnabled); Assert.False(slots.Exists("auto"));
            string entry = SceneSavePolicy.SlotName(runtime.Visuals.Scene!); Assert.Equal("allowed:1", (await slots.LoadAsync(entry)).Frame.Text); byte[] entered = File.ReadAllBytes(slots.SlotPath(entry));
            await Next(window, "allowed:2"); Assert.Equal(entered, File.ReadAllBytes(slots.SlotPath(entry))); Assert.False(slots.Exists("auto"));
            Find<Button>(window, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => slots.Exists("quick")); Assert.Equal("allowed:2", (await slots.LoadAsync("quick")).Frame.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LoadingInsideACallDoesNotRewriteCallerEntryAndRollbackAllowsTheSameFreshVisitToCreateItsEntryAgain() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("engine/state.json", """{"hits":0}""");
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = false, RollbackDepth = 5 }, EngineSettings.JsonOptions));
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'caller:before'); call_scene(child); d(nil,'caller:after'); call_scene(child); d(nil,'caller:finish') end\nfunction child() state.hits=state.hits+1; d(nil,'child:'..state.hits) end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new() { ["main"] = new() { CheckpointOnEntry = true }, ["child"] = new() { CheckpointOnEntry = true } } } });
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var original = new GameWindow(project, slots);
        string callerEntry, childEntry; byte[] callerBytes, firstChildBytes;
        try
        {
            await InvokeTask(original, "StartGameAsync"); callerEntry = SceneSavePolicy.SlotName(Field<IGameSession>(original, "runtime").Visuals.Scene!); callerBytes = File.ReadAllBytes(slots.SlotPath(callerEntry));
            await Next(original, "child:1"); var runtime = Field<IGameSession>(original, "runtime"); childEntry = SceneSavePolicy.SlotName(runtime.Visuals.Scene!); firstChildBytes = File.ReadAllBytes(slots.SlotPath(childEntry)); await slots.SaveAsync("resume", await runtime.GetCheckpointAsync());
        }
        finally { original.Close(); }
        var restored = new GameWindow(project, slots);
        try
        {
            await InvokeTask(restored, "LoadGameAsync", "resume"); Assert.Equal("child:1", Field<IGameSession>(restored, "runtime").Story.Text); Assert.Equal(firstChildBytes, File.ReadAllBytes(slots.SlotPath(childEntry)));
            await Next(restored, "caller:after"); Assert.Equal(callerBytes, File.ReadAllBytes(slots.SlotPath(callerEntry)));
            await Next(restored, "child:2"); Assert.Equal(2, (await slots.LoadAsync(childEntry)).State.GetProperty("hits").GetInt32()); Assert.Equal(callerBytes, File.ReadAllBytes(slots.SlotPath(callerEntry)));
            await restored.RollbackAsync(); Assert.Equal("caller:after", Field<IGameSession>(restored, "runtime").Story.Text); Assert.Equal(callerBytes, File.ReadAllBytes(slots.SlotPath(callerEntry)));
            File.WriteAllBytes(slots.SlotPath(childEntry), firstChildBytes);
            await Next(restored, "child:2"); Assert.Equal(2, (await slots.LoadAsync(childEntry)).State.GetProperty("hits").GetInt32()); Assert.NotEqual(firstChildBytes, File.ReadAllBytes(slots.SlotPath(childEntry))); Assert.False(slots.Exists("auto"));
        }
        finally { restored.Close(); }
    });

    [Fact]
    public Task SceneMusicVolumeMultipliesThePlayersPreferenceAndInheritedMusicRemainsOpenAcrossMutedCallsAndReturns() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "theme.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[800], 0, 800);
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = false }, EngineSettings.JsonOptions));
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'main'); call_scene(muted); d(nil,'back'); goto_scene(inherited) end\nfunction muted() d(nil,'muted') end\nfunction inherited() d(nil,'inherited') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new() { ["main"] = new() { Music = "theme.wav", MusicVolume = 25, LoopMusic = true }, ["muted"] = new() { MusicVolume = 0, StopMusicOnExit = true } } } });
        var window = new GameWindow(await fixture.LoadAsync()); var output = new AudioOutput(); var music = new GameAudioPlayer(_ => output, source => new WaveFileReader(source));
        Field<GameAudioPlayer>(window, "music").Dispose(); typeof(GameWindow).GetField("music", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, music); Field<Dictionary<string, GameAudioPlayer>>(window, "audioPlayers")["music"] = music;
        var preferences = Field<object>(window, "preferences"); preferences.GetType().GetProperty("MusicVolume")!.SetValue(preferences, .4);
        try
        {
            await InvokeTask(window, "StartGameAsync"); Assert.Equal(new Uri(path), music.Source); Assert.Equal(.1, music.Volume, 6); Assert.Equal(PlaybackState.Playing, output.PlaybackState);
            await Next(window, "muted"); Assert.Equal(new Uri(path), music.Source); Assert.Equal(0, music.Volume); Assert.Equal(PlaybackState.Playing, output.PlaybackState);
            await Next(window, "back"); Assert.Equal(new Uri(path), music.Source); Assert.Equal(.1, music.Volume, 6);
            await Next(window, "inherited"); Assert.Equal(new Uri(path), music.Source); Assert.Equal(.4, music.Volume, 6);
        }
        finally { window.Close(); }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 44);
    });

    [Fact]
    public Task PlayerKeepsOrdinaryTicksUncoveredAndPlaysTheExitTransitionWhenTheSceneNaturallyEnds() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = false }, EngineSettings.JsonOptions));
        fixture.Write("game/scenes/main.krl", "function main() kne_pause(10); d(nil,'after wait') end"); await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new() { ["main"] = new() { ExitTransition = new() { Kind = "fade", Duration = .3 } } } } });
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var window = new GameWindow(project, slots) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        var preferences = Field<object>(window, "preferences"); preferences.GetType().GetProperty("Animate")!.SetValue(preferences, true);
        try
        {
            window.Show(); Field<DispatcherTimer>(window, "renderTimer").Stop(); await InvokeTask(window, "StartGameAsync"); Field<DispatcherTimer>(window, "renderTimer").Stop(); window.UpdateLayout();
            var runtime = Field<IGameSession>(window, "runtime"); Assert.Equal("wait", runtime.Story.Kind); long visit = runtime.Visuals.Scene!.Visit; var sceneArea = Find<Grid>(window, "SceneArea"); int originalChildren = sceneArea.Children.Count;
            bool extraLayer = false; EventHandler observer = (_, _) => extraLayer |= sceneArea.Children.Count != originalChildren; sceneArea.LayoutUpdated += observer;
            try
            {
                await InvokeTask(window, "PumpSceneTimeAsync", .1); Assert.Equal("wait", runtime.Story.Kind); Assert.Equal(visit, runtime.Visuals.Scene!.Visit); Assert.False(extraLayer); Assert.Equal(originalChildren, sceneArea.Children.Count);
                await InvokeTask(window, "PumpSceneTimeAsync", 10d); Assert.Equal("after wait", runtime.Story.Text); Assert.Equal(visit, runtime.Visuals.Scene!.Visit); Assert.False(extraLayer); Assert.Equal(originalChildren, sceneArea.Children.Count);
            }
            finally { sceneArea.LayoutUpdated -= observer; }
            var finishing = InvokeTask(window, "ExecuteAsync", (Func<Task>)(() => runtime.AdvanceAsync()), false, "Natural scene end", true);
            var elapsed = Stopwatch.StartNew(); while (sceneArea.Children.Count == originalChildren && !finishing.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
            Assert.True(sceneArea.Children.Count > originalChildren, "Natural scene completion did not show its exit transition."); Assert.True(runtime.Visuals.Scene!.Exited); Assert.True(runtime.Story.Completed);
            Assert.Same((Grid)window.Content, System.Windows.Media.VisualTreeHelper.GetParent(Find<StackPanel>(window, "GameControls")));
            await finishing; Assert.Equal(originalChildren, sceneArea.Children.Count); Assert.False(Field<bool>(window, "startupFailed"));
        }
        finally { window.Close(); }
    });

    static async Task Next(GameWindow window, string expected)
    {
        Find<Button>(window, "NextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => Field<IGameSession>(window, "runtime").Story.Text == expected);
    }
    static async Task WaitUntil(GameWindow window, Func<bool> completed)
    {
        var elapsed = Stopwatch.StartNew(); while ((!completed() || Field<object?>(window, "execution") != null) && elapsed.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10);
        Assert.True(completed() && Field<object?>(window, "execution") == null, $"Player operation did not finish; frame: {Field<IGameSession>(window, "runtime").Story.Text}; status: {Find<TextBlock>(window, "StatusText").Text}");
    }
    static Task InvokeTask(object owner, string method, params object[] args) => (Task)owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args)!;
    static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    sealed class AudioOutput : IWavePlayer
    {
        IWaveProvider? provider;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => provider!.WaveFormat;
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => provider = waveProvider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Stop();
    }
}
