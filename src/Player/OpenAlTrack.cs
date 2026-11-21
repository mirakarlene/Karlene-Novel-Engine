using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KNE;

/// <summary>Streams bounded PCM buffers decoded by the same reader used for NAudio.</summary>
internal sealed partial class OpenAlTrack : IGameAudioTrack
{
    private static readonly object contextLock = new();
    private static nint device, context;
    private static int users;
    private readonly WaveStream reader;
    private readonly IWaveProvider pcm;
    private readonly IDisposable? converter;
    private readonly Action<Exception?> finished;
    private readonly Timer timer;
    private readonly byte[] data;
    private readonly uint[] buffers = new uint[2];
    private readonly Dictionary<uint, int> queued = new(2);
    private readonly int blockAlign;
    private readonly int sampleRate;
    private readonly int format;
    private readonly int period;
    private uint source;
    private long playedFrames;
    private bool deviceHeld, playing, disposed;

    internal OpenAlTrack(WaveStream reader, int milliseconds, Action<Exception?> finished)
    {
        this.reader = reader;
        this.finished = finished;
        IWaveProvider decoded = reader;
        if (reader.WaveFormat.Channels > 2)
        {
            var resampler = new MediaFoundationResampler(reader, new WaveFormat(reader.WaveFormat.SampleRate, 16, 2));
            converter = resampler;
            decoded = resampler;
        }
        pcm = new SampleToWaveProvider16(new SampleChannel(decoded, forceStereo: false));
        blockAlign = pcm.WaveFormat.BlockAlign;
        sampleRate = pcm.WaveFormat.SampleRate;
        format = pcm.WaveFormat.Channels == 1 ? 0x1101 : 0x1103; // AL_FORMAT_MONO16 / AL_FORMAT_STEREO16
        data = new byte[checked(sampleRate * milliseconds / 1000 * blockAlign)];
        period = Math.Max(5, milliseconds / 3);
        timer = new Timer(Pump);
        try
        {
            lock (contextLock)
            {
                if (users == 0) Open();
                users++;
                deviceHeld = true;
                using var current = new CurrentDevice();
                GenSource(1, out source);
                Check();
                SourceInt(source, 0x202, 1); // AL_SOURCE_RELATIVE
                SourceFloat(source, 0x1021, 0); // AL_ROLLOFF_FACTOR
                for (int i = 0; i < buffers.Length; i++)
                {
                    GenBuffer(1, out buffers[i]);
                    Check();
                }
            }
        }
        catch { Dispose(); throw; }
    }

    public double Volume
    {
        set
        {
            lock (contextLock)
            {
                using var current = new CurrentDevice();
                SourceFloat(source, 0x100A, (float)value); // AL_GAIN
                Check();
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (contextLock)
            {
                using var current = new CurrentDevice();
                int offset = queued.Count == 0 ? 0 : Get(0x1025); // AL_SAMPLE_OFFSET
                return TimeSpan.FromSeconds((playedFrames + offset) / (double)sampleRate);
            }
        }
        set
        {
            lock (contextLock)
            {
                using var current = new CurrentDevice();
                SourceStop(source);
                SourceInt(source, 0x1009, 0); // AL_BUFFER
                Check();
                queued.Clear();
                reader.CurrentTime = value < TimeSpan.Zero ? TimeSpan.Zero : value > reader.TotalTime ? reader.TotalTime : value;
                if (converter is MediaFoundationResampler resampler) resampler.Reposition();
                playedFrames = (long)(reader.CurrentTime.TotalSeconds * sampleRate);
                if (playing) FillAndPlay();
            }
        }
    }

    public void Play()
    {
        lock (contextLock)
        {
            if (playing || disposed) return;
            using var current = new CurrentDevice();
            FillAndPlay();
            playing = true;
            timer.Change(period, period);
        }
    }

    public void Pause()
    {
        lock (contextLock)
        {
            if (!playing || disposed) return;
            using var current = new CurrentDevice();
            SourcePause(source);
            Check();
            playing = false;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Pump(object? state)
    {
        Exception? failure = null;
        lock (contextLock)
        {
            if (!playing || disposed) return;
            try
            {
                using var current = new CurrentDevice();
                int processed = Get(0x1016); // AL_BUFFERS_PROCESSED
                while (processed-- > 0)
                {
                    Unqueue(source, 1, out uint buffer);
                    Check();
                    playedFrames += queued[buffer];
                    queued.Remove(buffer);
                }
                FillAndPlay();
                if (queued.Count != 0) return;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
            playing = false;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        finished(failure);
    }

    private unsafe void FillAndPlay()
    {
        foreach (uint buffer in buffers)
        {
            if (queued.ContainsKey(buffer)) continue;
            int bytes = pcm.Read(data.AsSpan());
            if (bytes == 0) break;
            fixed (byte* samples = data)
                BufferData(buffer, format, (nint)samples, bytes, sampleRate);
            Check();
            Queue(source, 1, in buffer);
            Check();
            queued.Add(buffer, bytes / blockAlign);
        }
        if (queued.Count == 0 || Get(0x1010) == 0x1012) return; // AL_SOURCE_STATE / AL_PLAYING
        SourcePlay(source);
        Check();
    }

    private int Get(int property)
    {
        GetSourceInt(source, property, out int result);
        Check();
        return result;
    }

    private static void Open()
    {
        device = OpenDevice(0);
        if (device == 0) throw new InvalidOperationException("OpenAL Soft не смог открыть аудиоустройство.");
        context = CreateContext(device, 0);
        if (context != 0) return;
        CloseDevice(device);
        device = 0;
        throw new InvalidOperationException("OpenAL Soft не смог создать аудиоконтекст.");
    }

    private static void Check()
    {
        int error = GetError();
        if (error != 0) throw new InvalidOperationException($"Ошибка OpenAL Soft (0x{error:X}).");
    }

    private readonly struct CurrentDevice : IDisposable
    {
        private readonly nint previous;
        public CurrentDevice()
        {
            previous = CurrentContext();
            if (MakeCurrent(context) == 0) throw new InvalidOperationException("Аудиоконтекст OpenAL Soft недоступен.");
        }
        public void Dispose() => MakeCurrent(previous);
    }

    public void Dispose()
    {
        lock (contextLock)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            converter?.Dispose();
            if (!deviceHeld) return;
            try
            {
                using var current = new CurrentDevice();
                if (source != 0) DeleteSource(1, in source);
                foreach (uint buffer in buffers)
                    if (buffer != 0) DeleteBuffer(1, in buffer);
            }
            finally
            {
                if (--users == 0)
                {
                    DestroyContext(context);
                    CloseDevice(device);
                    context = device = 0;
                }
            }
        }
    }

    private const string Library = "soft_oal";

    [LibraryImport(Library, EntryPoint = "alcOpenDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OpenDevice(nint deviceName);

    [LibraryImport(Library, EntryPoint = "alcCloseDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte CloseDevice(nint device);

    [LibraryImport(Library, EntryPoint = "alcCreateContext")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateContext(nint device, nint attributes);

    [LibraryImport(Library, EntryPoint = "alcDestroyContext")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyContext(nint context);

    [LibraryImport(Library, EntryPoint = "alcGetCurrentContext")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CurrentContext();

    [LibraryImport(Library, EntryPoint = "alcMakeContextCurrent")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte MakeCurrent(nint context);

    [LibraryImport(Library, EntryPoint = "alGetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetError();

    [LibraryImport(Library, EntryPoint = "alGenSources")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void GenSource(int count, out uint source);

    [LibraryImport(Library, EntryPoint = "alDeleteSources")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DeleteSource(int count, in uint source);

    [LibraryImport(Library, EntryPoint = "alGenBuffers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void GenBuffer(int count, out uint buffer);

    [LibraryImport(Library, EntryPoint = "alDeleteBuffers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DeleteBuffer(int count, in uint buffer);

    [LibraryImport(Library, EntryPoint = "alSourcePlay")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void SourcePlay(uint source);

    [LibraryImport(Library, EntryPoint = "alSourcePause")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void SourcePause(uint source);

    [LibraryImport(Library, EntryPoint = "alSourceStop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void SourceStop(uint source);

    [LibraryImport(Library, EntryPoint = "alSourcei")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void SourceInt(uint source, int property, int value);

    [LibraryImport(Library, EntryPoint = "alSourcef")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void SourceFloat(uint source, int property, float value);

    [LibraryImport(Library, EntryPoint = "alGetSourcei")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void GetSourceInt(uint source, int property, out int value);

    [LibraryImport(Library, EntryPoint = "alBufferData")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void BufferData(uint buffer, int format, nint data, int size, int frequency);

    [LibraryImport(Library, EntryPoint = "alSourceQueueBuffers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Queue(uint source, int count, in uint buffer);

    [LibraryImport(Library, EntryPoint = "alSourceUnqueueBuffers")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Unqueue(uint source, int count, out uint buffer);
}
