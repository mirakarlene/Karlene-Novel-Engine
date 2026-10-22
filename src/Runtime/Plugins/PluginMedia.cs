using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Speech.Synthesis;

namespace KNE;

/// <summary>Session-owned audio playback and Windows offline text-to-speech.</summary>
public sealed class PluginMedia : IDisposable
{
    private readonly List<PluginAudio> playing = [];
    private SpeechSynthesizer? speech;
    private bool disposed;
    public PluginAudio Play(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        playing.RemoveAll(audio => { if (!audio.IsStopped) return false; audio.Dispose(); return true; });
        if (playing.Count >= 32) throw new InvalidOperationException("Не более 32 звуков плагинов одновременно.");
        var value = new PluginAudio(path); playing.Add(value); value.Play(); return value;
    }
    public void Speak(string text, string? voice = null, int rate = 0, int volume = 100)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 16000 || rate is < -10 or > 10 || volume is < 0 or > 100) throw new ArgumentException("Текст до 16000 символов, скорость −10…10, громкость 0…100.");
        speech ??= new SpeechSynthesizer(); if (voice != null) speech.SelectVoice(voice);
        speech.Rate = rate; speech.Volume = volume; speech.SpeakAsyncCancelAll(); speech.SpeakAsync(text);
    }
    public void StopSpeech() => speech?.SpeakAsyncCancelAll();
    public void Dispose() { if (disposed) return; disposed = true; foreach (var audio in playing) audio.Dispose(); playing.Clear(); speech?.Dispose(); speech = null; }
}

/// <summary>Real-time pitch, echo and reverberation on an independent plugin audio channel.</summary>
public sealed class PluginAudio : IDisposable
{
    private readonly WaveStream reader;
    private readonly WaveOut output;
    private readonly SmbPitchShiftingSampleProvider pitch;
    private readonly PluginAudioEffects effects;
    private bool disposed;
    internal bool IsStopped => disposed || output.PlaybackState == PlaybackState.Stopped;
    internal PluginAudio(string path)
    {
        reader = new MediaFoundationReader(path);
        try
        {
            pitch = new SmbPitchShiftingSampleProvider(new SampleChannel(reader)); effects = new(pitch);
            output = new WaveOut { BufferMilliseconds = 75, NumberOfBuffers = 2 };
            try { output.Init(effects.ToWaveProvider()); } catch { output.Dispose(); throw; }
        }
        catch { reader.Dispose(); throw; }
    }
    public float Pitch { get => pitch.PitchFactor; set { if (!float.IsFinite(value) || value is < 0.5f or > 2) throw new ArgumentOutOfRangeException(nameof(value)); pitch.PitchFactor = value; } }
    public float Echo { get => effects.Echo; set => effects.Echo = value; }
    public float Reverb { get => effects.Reverb; set => effects.Reverb = value; }
    public void Play() { ObjectDisposedException.ThrowIf(disposed, this); output.Play(); }
    public void Stop() { if (!disposed) output.Stop(); }
    public void Dispose() { if (disposed) return; disposed = true; try { output.Dispose(); } finally { reader.Dispose(); } }
}

internal sealed class PluginAudioEffects(ISampleProvider source) : ISampleProvider
{
    private readonly float[] delay = new float[Math.Max(1, source.WaveFormat.SampleRate * source.WaveFormat.Channels / 3)];
    private readonly float[] room = new float[Math.Max(1, source.WaveFormat.SampleRate * source.WaveFormat.Channels / 19)];
    private int delayIndex, roomIndex;
    private float echo, reverb;
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Echo { get => Volatile.Read(ref echo); set { Validate(value); Volatile.Write(ref echo, value); } }
    public float Reverb { get => Volatile.Read(ref reverb); set { Validate(value); Volatile.Write(ref reverb, value); } }
    private static void Validate(float value) { if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value)); }
    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }
    public int Read(Span<float> buffer)
    {
        int read = source.Read(buffer); float e = Echo, r = Reverb;
        for (int i = 0; i < read; i++)
        {
            float dry = buffer[i], wet = dry + delay[delayIndex] * e + room[roomIndex] * r * 0.6f;
            delay[delayIndex] = dry; room[roomIndex] = dry + room[roomIndex] * r * 0.65f;
            buffer[i] = Math.Clamp(wet, -1, 1); delayIndex = (delayIndex + 1) % delay.Length; roomIndex = (roomIndex + 1) % room.Length;
        }
        return read;
    }
}
