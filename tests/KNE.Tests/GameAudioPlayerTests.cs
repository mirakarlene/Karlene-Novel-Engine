using System.IO;
using System.Windows.Threading;
using NAudio.Wave;
using Xunit;

namespace KNE.Tests;

public sealed class GameAudioPlayerTests
{
    [Theory]
    [InlineData("low_latency", 25)] [InlineData("balanced", 75)] [InlineData("stable", 150)]
    public Task BufferProfile_ConfiguresTheActualOutputBeforeInitializationAndPreservesTransportAndVolume(string profile, int milliseconds) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = WaveFile(fixture); int? requested = null;
        var device = new RecordingOutput();
        using var player = new GameAudioPlayer(value => { requested = value; return device; }, path => new WaveFileReader(path)) { BufferProfile = profile, Volume = .35 };
        player.Open(new Uri(path));
        Assert.Equal(milliseconds, requested); Assert.NotNull(device.Provider); Assert.Equal(new Uri(path), player.Source); Assert.Equal(.35, player.Volume);
        var gain = (NAudio.Wave.SampleProviders.SampleChannel)typeof(GameAudioPlayer).GetField("gain", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(player)!;
        Assert.Equal(.35f, gain.Volume);
        player.Play(); Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        player.Pause(); Assert.Equal(PlaybackState.Paused, device.PlaybackState);
        player.Position = TimeSpan.FromMilliseconds(100); Assert.InRange(player.Position.TotalMilliseconds, 99, 101);
        player.Volume = .6; Assert.Equal(.6f, gain.Volume);
        int ended = 0, failed = 0; player.MediaEnded += (_, _) => ended++; player.MediaFailed += (_, _) => failed++;
        device.Finish(new IOException("device error")); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }); Assert.Equal(1, failed); Assert.Equal(0, ended);
        player.Close(); Assert.True(device.Disposed); Assert.Null(player.Source); Assert.Equal(TimeSpan.Zero, player.Position);
        device.Finish(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }); Assert.Equal(1, failed); Assert.Equal(0, ended);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 44);
    });

    [Fact]
    public Task OpenFailureAndCloseReleaseTheDecodedFileAndIgnoreOldCompletionCallbacks() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = WaveFile(fixture); int errors = 0;
        using var player = new GameAudioPlayer(_ => throw new InvalidOperationException("No output device"));
        player.MediaFailed += (_, args) => { Assert.Contains("No output device", args.ErrorException.Message); errors++; };
        player.Open(new Uri(path)); Assert.Equal(1, errors); Assert.Null(player.Source);
        player.Play(); player.Pause(); player.Close();
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 44);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("")] [InlineData("fast")] [InlineData("Balanced")]
    public void BufferProfile_RejectsUnknownPolicies(string profile) => Assert.Throws<ArgumentException>(() => GameAudioPlayer.BufferMilliseconds(profile));

    [AudioTheory]
    [InlineData("low_latency", 25)] [InlineData("balanced", 75)] [InlineData("stable", 150)]
    public Task NativeAudio_AllProfilesInitializeRealBuffersFinishSilentPlaybackAndReleaseTheFile(string profile, int milliseconds) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = WaveFile(fixture);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); WaveOut? actual = null;
        using var player = new GameAudioPlayer(value => actual = new WaveOut { BufferMilliseconds = value, NumberOfBuffers = 2 }) { BufferProfile = profile, Volume = 0 };
        player.MediaEnded += (_, _) => done.TrySetResult(); player.MediaFailed += (_, args) => done.TrySetException(args.ErrorException);
        player.Open(new Uri(path)); Assert.NotNull(player.Source); Assert.NotNull(actual);
        Assert.Equal(milliseconds, actual.BufferMilliseconds); Assert.Equal(2, actual.NumberOfBuffers);
        player.Play(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); player.Close();
        Assert.Null(player.Source); Assert.Equal(PlaybackState.Stopped, actual.PlaybackState);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 44);
    });

    [AudioTheory]
    [InlineData("stable")]
    public Task NativeAudio_Mp3UsesInstalledWindowsCodecsAndCompletesWithAConfigurableBuffer(string profile) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string wav = WaveFile(fixture, 44100), mp3 = PathSafety.ResolveRelative(fixture.ProjectPath, "silent.mp3");
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        using (var source = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToMp3(source, mp3, 96000);
        Assert.True(new FileInfo(mp3).Length > 100);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var player = new GameAudioPlayer { BufferProfile = profile, Volume = 0 };
        player.MediaFailed += (_, args) => done.TrySetException(args.ErrorException); player.MediaEnded += (_, _) => done.TrySetResult();
        player.Open(new Uri(mp3)); Assert.Equal(new Uri(mp3), player.Source); player.Play();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); player.Close(); Assert.Null(player.Source);
        using var exclusive = File.Open(mp3, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.True(exclusive.Length > 100);
    });

    [Fact]
    public async Task LegacyMusicAndSoundResumeAfterPauseAndLiveTextureChangesDoNotRestartAudio()
    {
        using var fixture = new TempProject(); WaveFile(fixture);
        fixture.Write("game/scenes/main.krl", "function main() play_music('silent.wav',true); play_sound('silent.wav'); d(nil,'Audio frame') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { AudioBuffer = "stable", Preloading = false });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var musicDevice = new RecordingOutput(); var soundDevice = new RecordingOutput();
            var music = new GameAudioPlayer(_ => musicDevice, path => new WaveFileReader(path)); var sound = new GameAudioPlayer(_ => soundDevice, path => new WaveFileReader(path));
            var window = new GameWindow(project);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(GameWindow).GetField("music", flags)!.SetValue(window, music); typeof(GameWindow).GetField("sound", flags)!.SetValue(window, sound);
            var players = (Dictionary<string, GameAudioPlayer>)typeof(GameWindow).GetField("audioPlayers", flags)!.GetValue(window)!; players["music"] = music; players["sfx"] = sound;
            void Invoke(string method) => typeof(GameWindow).GetMethod(method, flags)!.Invoke(window, null);
            try
            {
                await (Task)typeof(GameWindow).GetMethod("StartGameAsync", flags)!.Invoke(window, null)!;
                Assert.Equal("stable", music.BufferProfile); Assert.Equal("stable", sound.BufferProfile);
                Assert.Equal(PlaybackState.Playing, musicDevice.PlaybackState); Assert.Equal(PlaybackState.Playing, soundDevice.PlaybackState);
                Invoke("ShowMenu"); Assert.Equal(PlaybackState.Paused, musicDevice.PlaybackState); Assert.Equal(PlaybackState.Paused, soundDevice.PlaybackState);
                Invoke("ShowGameplay"); Assert.Equal(PlaybackState.Playing, musicDevice.PlaybackState); Assert.Equal(PlaybackState.Playing, soundDevice.PlaybackState);
                int musicStarts = musicDevice.Starts, soundStarts = soundDevice.Starts;
                await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { AudioBuffer = "low_latency", Graphics = new() { TextureQuality = "quarter" }, Preloading = false });
                window.ReloadPerformanceSettings(); Invoke("RenderOutput");
                Assert.Equal(musicStarts, musicDevice.Starts); Assert.Equal(soundStarts, soundDevice.Starts);
                Assert.False(musicDevice.Disposed); Assert.False(soundDevice.Disposed);
            }
            finally { window.Close(); }
            Assert.True(musicDevice.Disposed); Assert.True(soundDevice.Disposed);
        });
    }

    private static string WaveFile(TempProject fixture)
        => WaveFile(fixture, 8000);
    private static string WaveFile(TempProject fixture, int sampleRate)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, "silent.wav");
        using var writer = new BinaryWriter(File.Create(path));
        int bytes = sampleRate; // Half a second of mono PCM silence.
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]); return path;
    }
    private sealed class RecordingOutput : IWavePlayer
    {
        internal IWaveProvider? Provider { get; private set; }
        internal bool Disposed { get; private set; }
        internal int Starts { get; private set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public float Volume { get; set; } = 1;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => Provider = waveProvider;
        public void Play() { Starts++; PlaybackState = PlaybackState.Playing; }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() { Stop(); Disposed = true; }
        internal void Finish(Exception? error = null) => PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }
}

internal sealed class AudioTheoryAttribute : TheoryAttribute
{
    public AudioTheoryAttribute() { if (Environment.GetEnvironmentVariable("KNE_AUDIO_TEST") != "1") Skip = "Set KNE_AUDIO_TEST=1 on Windows with an audio output device to exercise real silent audio playback."; }
}
