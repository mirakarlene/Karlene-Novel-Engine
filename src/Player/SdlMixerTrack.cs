using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace KNE;

internal interface IGameAudioTrack : IDisposable
{
    TimeSpan Position { get; set; }
    double Volume { set; }
    void Play();
    void Pause();
}

/// <summary>One shared audio device with independently controllable, SDL-decoded tracks.</summary>
internal sealed partial class SdlMixerTrack : IGameAudioTrack
{
    private static readonly object deviceLock = new();
    private static nint device;
    private static int users;
    private nint track, audio;
    private bool deviceHeld;
    private long startFrame;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };

    internal SdlMixerTrack(string path, Action<Exception?> finished)
    {
        timer.Tick += (_, _) =>
        {
            if (IsPlaying(track) || IsPaused(track)) return;
            timer.Stop();
            finished(null);
        };
        try
        {
            lock (deviceLock)
            {
                if (users == 0)
                {
                    Check(Init());
                    try
                    {
                        device = CreateMixer(0xFFFFFFFF, 0); // SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK
                        Check(device != 0);
                    }
                    catch { Quit(); throw; }
                }
                users++;
                deviceHeld = true;
            }
            audio = LoadAudio(device, path, false);
            Check(audio != 0);
            track = CreateTrack(device);
            Check(track != 0);
            Check(SetAudio(track, audio));
        }
        catch { Dispose(); throw; }
    }

    public TimeSpan Position
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, ToMilliseconds(track, Math.Max(0, IsPlaying(track) || IsPaused(track) ? GetPosition(track) : startFrame))));
        set
        {
            long frame = Math.Max(0, ToFrames(track, (long)value.TotalMilliseconds));
            long duration = GetDuration(audio);
            if (duration > 0) frame = Math.Min(duration - 1, frame);
            if (IsPlaying(track) || IsPaused(track)) Check(SetPosition(track, frame));
            else startFrame = frame;
        }
    }

    public double Volume { set => Check(SetGain(track, (float)value)); }

    public void Play()
    {
        if (IsPlaying(track) && !IsPaused(track)) return;
        if (IsPaused(track)) Check(ResumeTrack(track));
        else
        {
            Check(PlayTrack(track, 0));
            if (startFrame != 0) Check(SetPosition(track, startFrame));
            startFrame = 0;
        }
        timer.Start();
    }

    public void Pause()
    {
        if (IsPlaying(track)) Check(PauseTrack(track));
        timer.Stop();
    }

    public void Dispose()
    {
        timer.Stop();
        if (track != 0) { DestroyTrack(track); track = 0; }
        if (audio != 0) { DestroyAudio(audio); audio = 0; }
        if (!deviceHeld) return;
        lock (deviceLock)
        {
            deviceHeld = false;
            if (--users == 0)
            {
                DestroyMixer(device);
                device = 0;
                Quit();
            }
        }
    }

    private static void Check(bool success)
    {
        if (!success) throw new InvalidOperationException("SDL3_mixer: " + (Marshal.PtrToStringUTF8(GetError()) ?? "аудиоустройство или формат недоступны."));
    }

    private const string Mixer = "SDL3_mixer";

    [LibraryImport("SDL3", EntryPoint = "SDL_GetError")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint GetError();

    [LibraryImport(Mixer, EntryPoint = "MIX_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool Init();

    [LibraryImport(Mixer, EntryPoint = "MIX_Quit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Quit();

    [LibraryImport(Mixer, EntryPoint = "MIX_CreateMixerDevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateMixer(uint device, nint spec);

    [LibraryImport(Mixer, EntryPoint = "MIX_DestroyMixer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyMixer(nint mixer);

    [LibraryImport(Mixer, EntryPoint = "MIX_LoadAudio", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint LoadAudio(nint mixer, string path, [MarshalAs(UnmanagedType.U1)] bool predecode);

    [LibraryImport(Mixer, EntryPoint = "MIX_DestroyAudio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyAudio(nint audio);

    [LibraryImport(Mixer, EntryPoint = "MIX_GetAudioDuration")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial long GetDuration(nint audio);

    [LibraryImport(Mixer, EntryPoint = "MIX_CreateTrack")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateTrack(nint mixer);

    [LibraryImport(Mixer, EntryPoint = "MIX_DestroyTrack")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyTrack(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_SetTrackAudio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetAudio(nint track, nint audio);

    [LibraryImport(Mixer, EntryPoint = "MIX_PlayTrack")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool PlayTrack(nint track, uint options);

    [LibraryImport(Mixer, EntryPoint = "MIX_PauseTrack")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool PauseTrack(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_ResumeTrack")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool ResumeTrack(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_TrackPlaying")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPlaying(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_TrackPaused")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IsPaused(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_SetTrackGain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetGain(nint track, float gain);

    [LibraryImport(Mixer, EntryPoint = "MIX_GetTrackPlaybackPosition")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial long GetPosition(nint track);

    [LibraryImport(Mixer, EntryPoint = "MIX_SetTrackPlaybackPosition")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool SetPosition(nint track, long frames);

    [LibraryImport(Mixer, EntryPoint = "MIX_TrackMSToFrames")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial long ToFrames(nint track, long milliseconds);

    [LibraryImport(Mixer, EntryPoint = "MIX_TrackFramesToMS")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial long ToMilliseconds(nint track, long frames);
}
