using System.IO;
using System.Text.Json;

namespace KNE;

public sealed partial class LuaRuntime
{
    private sealed class SceneFrame(SceneRuntimeState scene, int parent, int depth)
    {
        internal readonly SceneRuntimeState Scene = scene;
        internal readonly int Parent = parent, Depth = depth;
        internal int References = 1;
        internal RuntimeWindowSettings? Window;
        internal SceneVisuals? Audio;
    }
    private readonly Dictionary<int, SceneFrame> sceneFrames = [];
    private int executingScene, musicScene, lastSceneVisit;
    private void InitializeSceneSettings()
    {
        sceneFrames.Clear(); executingScene = musicScene = lastSceneVisit = 0;
        Register(nameof(EnterSceneForLua), "kne_scene_enter"); Register(nameof(LeaveSceneForLua), "kne_scene_leave");
        Register(nameof(ExitSceneForLua), "kne_scene_exit"); Register(nameof(ResumeSceneForLua), "kne_scene_restore");
        Register(nameof(PresentSceneForLua), "kne_scene_present"); Register(nameof(SelectSceneForLua), "kne_scene_select");
        Register(nameof(CheckSceneCallForLua), "kne_scene_check_call"); Register(nameof(SceneTriggerForLua), "kne_scene_trigger");
        Register(nameof(RetainSceneForLua), "kne_scene_retain"); Register(nameof(ReleaseSceneForLua), "kne_scene_release");
    }
    /// <summary>Initializes a fresh scene frame once; callers are restored separately.</summary>
    public string EnterSceneForLua(string file, string function, int token, int parent, int depth)
    {
        try
        {
            string full = Path.IsPathFullyQualified(file) ? PathSafety.EnsureInside(project.Path, file) : PathSafety.ResolveRelative(project.Path, file);
            string relative = Path.GetRelativePath(project.Path, full).Replace('\\', '/');
            var settings = project.Engine.ScenePerformance.For(relative, function);
            settings.Validate(project.Path);
            if (token <= 0 || sceneFrames.ContainsKey(token) || sceneFrames.Count >= 8192) throw new InvalidDataException("Некорректный кадр сцены.");
            lastSceneVisit = Math.Max(lastSceneVisit, token);
            var scene = new SceneRuntimeState(relative, function, token, settings);
            var frame = new SceneFrame(scene, parent, depth); sceneFrames.Add(token, frame); executingScene = token;
            RetainSceneForLua(parent);
            lua!.DoString("do " + string.Join(";", settings.InitialState.EnumerateObject().Select(p => "state[" + LuaQuote(p.Name) + "]=" + StateLiteral(p.Value))) + " end", "KNE scene initial state");
            lock (stateLock) windowSettings = windowSettings with { Width = settings.WindowWidth ?? project.Engine.Window.Width, Height = settings.WindowHeight ?? project.Engine.Window.Height };
            frame.Window = WindowSettings;
            if (!string.IsNullOrWhiteSpace(settings.Music)) { PlayMusic(settings.Music, settings.LoopMusic); musicScene = token; }
            return "";
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException or JsonException) { return error.Message; }
    }
    /// <summary>Remembers caller presentation before a call and applies the outgoing music policy.</summary>
    public void LeaveSceneForLua(int token)
    {
        if (!sceneFrames.TryGetValue(token, out var frame)) return;
        frame.Window = WindowSettings; frame.Audio = Visuals;
        if (frame.Scene.Settings.StopMusicOnExit && musicScene == token) { StopMusic(); musicScene = 0; }
    }
    /// <summary>Releases a returned or replaced scene frame.</summary>
    public void ExitSceneForLua(int token)
    {
        var scene = sceneFrames.GetValueOrDefault(token)?.Scene;
        LeaveSceneForLua(token); ReleaseSceneForLua(token);
        lock (stateLock) if (scene != null && (executingScene == token || visuals.Scene?.Visit == token))
            visuals = visuals with { Scene = scene with { Exited = true, LastVisit = lastSceneVisit } };
    }
    /// <summary>Keeps inherited scene policy alive for its parallel/detached descendants.</summary>
    public void RetainSceneForLua(int token) { if (sceneFrames.TryGetValue(token, out var frame)) frame.References++; }
    /// <summary>Releases an execution context and its ancestor lease after the final descendant exits.</summary>
    public void ReleaseSceneForLua(int token)
    {
        if (!sceneFrames.TryGetValue(token, out var frame) || --frame.References > 0) return;
        sceneFrames.Remove(token); ReleaseSceneForLua(frame.Parent);
    }
    /// <summary>Restores caller resolution and music without reapplying initial variables.</summary>
    public void ResumeSceneForLua(int token)
    {
        if (!sceneFrames.TryGetValue(token, out var frame)) return;
        executingScene = token;
        lock (stateLock)
        {
            if (frame.Window != null) windowSettings = frame.Window;
            if (frame.Audio is { } audio)
            {
                var previous = audio.AudioChannels.FirstOrDefault(c => c.Channel == "music"); var current = visuals.AudioChannels.FirstOrDefault(c => c.Channel == "music");
                bool changed = visuals.Music != audio.Music || visuals.LoopMusic != audio.LoopMusic || previous?.Path != current?.Path || previous?.Loop != current?.Loop || previous?.Volume != current?.Volume;
                if (changed) visuals = visuals with
                {
                    Music = audio.Music, LoopMusic = audio.LoopMusic, MusicVersion = visuals.MusicVersion + 1,
                    AudioChannels = visuals.AudioChannels.Where(c => c.Channel != "music").Concat(previous == null ? [] : new[] { previous with { Version = Math.Max(previous.Version, current?.Version ?? 0) + 1 } }).ToArray()
                };
                musicScene = token;
            }
        }
    }
    /// <summary>Selects a coroutine's inherited policy before it executes.</summary>
    public void SelectSceneForLua(int token)
    {
        if (token == executingScene) return;
        if (sceneFrames.TryGetValue(executingScene, out var previous)) previous.Window = WindowSettings;
        executingScene = token;
        if (sceneFrames.TryGetValue(token, out var frame) && frame.Window != null) lock (stateLock) windowSettings = frame.Window;
    }
    /// <summary>Publishes the policy of the scene currently presented to the player.</summary>
    public void PresentSceneForLua(int token, int lastVisit)
    {
        SelectSceneForLua(token);
        if (sceneFrames.TryGetValue(token, out var frame)) lock (stateLock) visuals = visuals with { Scene = frame.Scene with { LastVisit = lastVisit } };
    }
    /// <summary>Checks every enclosing scene's relative call budget, in addition to the scheduler hard limit.</summary>
    public string CheckSceneCallForLua(int token, int depth)
    {
        int remaining = 64;
        while (token > 0 && sceneFrames.TryGetValue(token, out var frame) && remaining-- > 0)
        {
            if (frame.Scene.Settings.MaxCallDepth is { } maximum && depth + 1 - frame.Depth > maximum)
                return "Превышена глубина call для сцены «" + (frame.Scene.Settings.Name == "" ? frame.Scene.Function : frame.Scene.Settings.Name) + "»: " + maximum;
            token = frame.Parent;
        }
        return "";
    }
    /// <summary>Filters keyboard/mouse subscriptions while leaving narrative text-close events available.</summary>
    public bool SceneTriggerForLua(int token, string specification)
    {
        if (!sceneFrames.TryGetValue(token, out var frame) || frame.Scene.Settings.AllowTriggers) return true;
        return ReadTrigger(specification).Kind == "text_closed";
    }
}
