using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using Xunit;

namespace KNE.Tests;

public sealed class PlayerProgressOptionsTests
{
    [Theory]
    [InlineData(0)] [InlineData(2)]
    public Task PlayerButtonsRecordPastPositionsAndLoadedSaveRestoresTheConfiguredRollbackDepth(int depth) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = false, RollbackDepth = depth }, EngineSettings.JsonOptions));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        fixture.Write("game/scenes/main.krl", "function main() state.step=1; d(nil,'one'); state.step=2; d(nil,'two'); state.step=3; d(nil,'three'); state.step=4; d(nil,'four') end");
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var window = new GameWindow(project, slots);
        try
        {
            await InvokeTask(window, "StartGameAsync");
            await Next(window, "two"); await Next(window, "three");
            Assert.Equal(depth, Field<GameRollbackHistory>(window, "rollback").Count);
            Invoke(window, "ShowMenu"); Invoke(window, "ShowGameplay");
            await QuickSave(window, slots);
            var checkpoint = await slots.LoadAsync("quick"); Assert.Equal(2, checkpoint.History.Count); Assert.Equal(depth, checkpoint.Rollback!.Count);
            Assert.False(slots.Exists("auto")); Assert.Equal(depth, Field<GameRollbackHistory>(window, "rollback").Count);
            await window.RollbackAsync();
            var runtime = Field<IGameSession>(window, "runtime");
            Assert.Equal(depth == 0 ? "three" : "two", runtime.Story.Text);
            Assert.Equal(depth == 0 ? 3 : 2, (await runtime.GetCheckpointAsync()).State.GetProperty("step").GetInt32());
            if (depth > 0)
            {
                await window.RollbackAsync(); Assert.Equal("one", runtime.Story.Text);
                Assert.Equal(1, (await runtime.GetCheckpointAsync()).State.GetProperty("step").GetInt32());
                await window.RollbackAsync(); Assert.Equal("one", runtime.Story.Text); Assert.Equal(0, Field<GameRollbackHistory>(window, "rollback").Count);
                await InvokeTask(window, "LoadGameAsync", "quick"); runtime = Field<IGameSession>(window, "runtime");
                Assert.Equal("three", runtime.Story.Text); Assert.Equal(2, Field<GameRollbackHistory>(window, "rollback").Count);
                await window.RollbackAsync(); Assert.Equal("two", runtime.Story.Text);
                await Next(window, "three"); Assert.Equal(2, Field<GameRollbackHistory>(window, "rollback").Count);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("dialogue")] [InlineData("choice")] [InlineData("actions")]
    public Task PlayerAutosavesOnlyForSelectedActionsAndManualSaveOrMenuDoesNotAdvanceTheCounter(string interval) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSaveInterval = interval, AutoSaveActions = 2 }, EngineSettings.JsonOptions));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'one'); local chosen=choice('Choose',{'Left','Right'}); state.answer=chosen; d(nil,'two'); d(nil,'three'); d(nil,'four') end");
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var window = new GameWindow(project, slots);
        try
        {
            await InvokeTask(window, "StartGameAsync"); Assert.Equal("one", (await slots.LoadAsync("auto")).Frame.Text);
            byte[] start = File.ReadAllBytes(slots.SlotPath("auto"));
            Find<Button>(window, "NextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(window, () => Field<IGameSession>(window, "runtime").Story.Kind == "choice");
            byte[] afterFirst = File.ReadAllBytes(slots.SlotPath("auto"));
            if (interval == "dialogue") Assert.Equal("choice", (await slots.LoadAsync("auto")).Frame.Kind); else Assert.Equal(start, afterFirst);
            Invoke(window, "ShowMenu"); Invoke(window, "ShowGameplay"); await QuickSave(window, slots);
            Assert.Equal(afterFirst, File.ReadAllBytes(slots.SlotPath("auto")));
            var choice = Find<StackPanel>(window, "ChoicePanel").Children.OfType<Button>().First(button => button.IsEnabled);
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => Field<IGameSession>(window, "runtime").Story.Text == "two");
            byte[] afterChoice = File.ReadAllBytes(slots.SlotPath("auto"));
            if (interval == "dialogue") Assert.Equal(afterFirst, afterChoice); else Assert.Equal("two", (await slots.LoadAsync("auto")).Frame.Text);
            await Next(window, "three");
            if (interval == "dialogue") Assert.Equal("three", (await slots.LoadAsync("auto")).Frame.Text); else Assert.Equal(afterChoice, File.ReadAllBytes(slots.SlotPath("auto")));
            if (interval == "actions") { await Next(window, "four"); Assert.Equal("four", (await slots.LoadAsync("auto")).Frame.Text); }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task PlayerPreloadingSwitchActuallyControlsSpeculativeSceneResourceLoading(bool enabled) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); WritePng(fixture, "current.png"); WritePng(fixture, "next.png");
        fixture.Write("game/scenes/main.krl", "function main() background('current.png'); d(nil,'waiting'); call(next_scene) end");
        fixture.Write("game/scenes/next.krl", "function next_scene() background('next.png'); d(nil,'next') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = enabled, PreloadScenes = 1 });
        var window = new GameWindow(await fixture.LoadAsync());
        try
        {
            await InvokeTask(window, "StartGameAsync"); var cache = Field<SceneAssetCache>(window, "assets");
            if (enabled) await WaitUntil(window, () => cache.CachedCount == 2); else Assert.Null(Field<object?>(window, "preloading"));
            Assert.Equal(enabled ? 2 : 1, cache.CachedCount); Assert.Equal(enabled ? 8 : 4, cache.CachedBytes);
            Assert.Equal("waiting", Field<IGameSession>(window, "runtime").Story.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task OptionalMenuMusicPlaysOnlyBeforeGameplayAndReleasesTheFileWhenTheGameStarts(bool configured) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "menu.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1))) writer.WriteSamples(new short[800], 0, 800);
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { MenuMusic = configured ? "menu.wav" : null }, EngineSettings.JsonOptions));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        var window = new GameWindow(await fixture.LoadAsync()); var device = new MenuOutput();
        var music = new GameAudioPlayer(_ => device, source => new WaveFileReader(source));
        Field<GameAudioPlayer>(window, "music").Dispose();
        typeof(GameWindow).GetField("music", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, music);
        Field<Dictionary<string, GameAudioPlayer>>(window, "audioPlayers")["music"] = music;
        try
        {
            Invoke(window, "ShowMenu");
            Assert.Equal(configured, Field<bool>(window, "menuMusicPlaying"));
            if (configured) { Assert.Equal(new Uri(path), music.Source); Assert.Equal(PlaybackState.Playing, device.PlaybackState); Assert.Equal(1, device.Starts); }
            else { Assert.Null(music.Source); Assert.Equal(0, device.Starts); }
            await InvokeTask(window, "StartGameAsync"); Assert.False(Field<bool>(window, "menuMusicPlaying")); Assert.Null(music.Source);
            Invoke(window, "ShowMenu"); Assert.Null(music.Source); Assert.Equal(configured ? 1 : 0, device.Starts);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 44);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RollbackCapturesDoNotEmitSaveEventsButAnActualManualSaveEmitsExactlyOne() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new GameDefaults { AutoSave = false, RollbackDepth = 2 }, EngineSettings.JsonOptions));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        fixture.Write("game/plugins/save-hook.lua", "function save_hook(data) state.save_events=(state.save_events or 0)+1 end; kne.on('on_save','save_hook')");
        fixture.Write("game/scenes/main.krl", "function main() include_lua('game/plugins/save-hook.lua'); state.save_events=0; d(nil,'one'); d(nil,'two'); d(nil,'three'); d(nil,'four') end");
        var project = await fixture.LoadAsync(); var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); var window = new GameWindow(project, slots);
        try
        {
            await InvokeTask(window, "StartGameAsync"); await Next(window, "two");
            var runtime = Field<IGameSession>(window, "runtime");
            Assert.Equal(0, (await runtime.CaptureCheckpointAsync()).State.GetProperty("save_events").GetInt32());
            await QuickSave(window, slots);
            Assert.Equal(1, (await slots.LoadAsync("quick")).State.GetProperty("save_events").GetInt32());
            await Next(window, "three");
            Assert.Equal(1, (await runtime.CaptureCheckpointAsync()).State.GetProperty("save_events").GetInt32());
            Assert.Equal(2, Field<GameRollbackHistory>(window, "rollback").Count); Assert.False(slots.Exists("auto"));
        }
        finally { window.Close(); }
    });

    private static async Task Next(GameWindow window, string expected)
    { Find<Button>(window, "NextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => Field<IGameSession>(window, "runtime").Story.Text == expected); }
    private static async Task QuickSave(GameWindow window, GameSaveStore slots)
    { Find<Button>(window, "SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitUntil(window, () => slots.Exists("quick")); }
    private static async Task WaitUntil(GameWindow window, Func<bool> completed)
    {
        var elapsed = Stopwatch.StartNew();
        while ((!completed() || Field<object?>(window, "execution") != null) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(completed() && Field<object?>(window, "execution") == null, $"Player operation did not finish; frame: {Field<IGameSession>(window, "runtime").Story.Text}; status: {Find<TextBlock>(window, "StatusText").Text}");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static void Invoke(object owner, string method) => owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
    private static Task InvokeTask(object owner, string method, params object[] args) => (Task)owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args)!;
    private static void WritePng(TempProject fixture, string relative)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, relative); var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class MenuOutput : IWavePlayer
    {
        private IWaveProvider? provider;
        internal int Starts { get; private set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => provider!.WaveFormat;
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => provider = waveProvider;
        public void Play() { Starts++; PlaybackState = PlaybackState.Playing; }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Stop();
    }
}
