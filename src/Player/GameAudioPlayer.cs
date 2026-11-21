using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KNE;

internal sealed class AudioPlaybackErrorEventArgs(Exception error) : EventArgs
{
    internal Exception ErrorException { get; } = error;
}

/// <summary>Owns one logical game channel independently of the selected playback backend.</summary>
internal sealed class GameAudioPlayer : IDisposable
{
    private readonly Func<int, IWavePlayer> createOutput;
    private readonly Func<string, WaveStream> openReader;
    private readonly Dispatcher dispatcher;
    private IWavePlayer? output;
    private WaveStream? reader;
    private SampleChannel? gain;
    private IGameAudioTrack? track;
    private double volume = 1;
    private int generation;
    internal string BufferProfile { get; set; } = "balanced";
    internal string Backend { get; set; } = "naudio";
    internal Uri? Source { get; private set; }
    internal static int BufferMilliseconds(string profile) => profile switch
    {
        "low_latency" => 25, "balanced" => 75, "stable" => 150,
        _ => throw new ArgumentException("Неизвестный профиль аудио буфера.", nameof(profile))
    };
    internal GameAudioPlayer(Func<int, IWavePlayer>? createOutput = null, Func<string, WaveStream>? openReader = null)
    {
        this.createOutput = createOutput ?? new Func<int, IWavePlayer>(milliseconds => new NAudio.Wave.WaveOut { BufferMilliseconds = milliseconds, NumberOfBuffers = 2 });
        this.openReader = openReader ?? (path => new MediaFoundationReader(path));
        dispatcher = Dispatcher.CurrentDispatcher;
    }
    internal event EventHandler? MediaEnded;
    internal event EventHandler<AudioPlaybackErrorEventArgs>? MediaFailed;
    internal double Volume
    {
        get => volume;
        set { if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value)); volume = value; if (gain != null) gain.Volume = (float)value; if (track != null) track.Volume = value; }
    }
    internal TimeSpan Position
    {
        get => track?.Position ?? reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (track != null) track.Position = value;
            else if (reader != null) reader.CurrentTime = value < TimeSpan.Zero ? TimeSpan.Zero : value > reader.TotalTime ? reader.TotalTime : value;
        }
    }
    internal void Open(Uri source)
    {
        Close(); int version = generation;
        try
        {
            if (!source.IsAbsoluteUri || !source.IsFile) throw new ArgumentException("Аудио должно находиться в локальном файле.", nameof(source));
            switch (Backend)
            {
                case "sdl_mixer":
                    track = new SdlMixerTrack(source.LocalPath, Complete);
                    break;
                case "openal":
                    reader = openReader(source.LocalPath);
                    track = new OpenAlTrack(reader, BufferMilliseconds(BufferProfile), Complete);
                    break;
                default:
                    reader = openReader(source.LocalPath);
                    gain = new SampleChannel(reader, forceStereo: false) { Volume = (float)volume };
                    output = createOutput(BufferMilliseconds(BufferProfile));
                    output.PlaybackStopped += (_, args) => Complete(args.Exception);
                    output.Init(gain.ToWaveProvider());
                    break;
            }
            if (track != null) track.Volume = volume;
            Source = source;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Close(); MediaFailed?.Invoke(this, new(error)); }

        void Complete(Exception? error)
        {
            if (dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (version != generation || Source == null) return;
                if (error != null) MediaFailed?.Invoke(this, new(error));
                else MediaEnded?.Invoke(this, EventArgs.Empty);
            }));
        }
    }
    internal void Play() { if (track != null) track.Play(); else output?.Play(); }
    internal void Pause() { if (track != null) track.Pause(); else output?.Pause(); }
    internal void Close()
    {
        generation++;
        var previousOutput = output; var previousReader = reader; var previousTrack = track;
        output = null; reader = null; track = null; gain = null; Source = null;
        try { previousTrack?.Dispose(); previousOutput?.Dispose(); }
        finally { previousReader?.Dispose(); }
    }
    public void Dispose() => Close();
}
