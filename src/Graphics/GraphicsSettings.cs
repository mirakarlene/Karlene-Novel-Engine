using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>A stable value and localized label for a rendering preference.</summary>
public sealed record GraphicsOption<T>(T Value, string Label);

/// <summary>Settings for KNE's owned graphics layers; WPF continues composing controls with its system renderer.</summary>
public sealed record GraphicsSettings
{
    public string Api { get; set; } = "auto";
    public bool VSync { get; set; } = true;
    public int MaxFps { get; set; } = 60;
    public int MsaaSamples { get; set; }
    /// <summary>Decode project textures at original, half or quarter dimensions.</summary>
    public string TextureQuality { get; set; } = "original";
    public static IReadOnlyList<GraphicsOption<string>> TextureQualities { get; } = [new("original", "Оригинал"), new("half", "Половина"), new("quarter", "Четверть")];
    /// <summary>Bounds optional decoded images and native preparation caches.</summary>
    public bool LimitResourceCache { get; set; } = true;
    /// <summary>Maximum optional cache size in MiB; active displayed resources are separate.</summary>
    public int ResourceCacheLimitMb { get; set; } = 64;
    public static IReadOnlyList<GraphicsOption<int>> CacheLimits { get; } = [new(16, "16 МиБ"), new(32, "32 МиБ"), new(64, "64 МиБ"), new(128, "128 МиБ"), new(256, "256 МиБ"), new(512, "512 МиБ")];
    internal long CacheBudgetBytes => LimitResourceCache ? ResourceCacheLimitMb * 1024L * 1024 : long.MaxValue;
    public static IReadOnlyList<GraphicsOption<string>> Apis { get; } = [new("auto", "Автоматически (OpenGL / Vulkan)"), new("vulkan", "Только Vulkan"), new("opengl", "Только OpenGL")];
    public static IReadOnlyList<GraphicsOption<int>> FrameRates { get; } = [new(30, "30"), new(60, "60"), new(144, "144"), new(0, "Без ограничений")];
    public static IReadOnlyList<GraphicsOption<int>> Antialiasing { get; } = [new(0, "Нет"), new(2, "MSAA ×2"), new(4, "MSAA ×4")];
    /// <summary>Rejects unsupported persisted values before a native context is created.</summary>
    public void Validate()
    {
        if (!Apis.Any(item => item.Value == Api) || !FrameRates.Any(item => item.Value == MaxFps) || !Antialiasing.Any(item => item.Value == MsaaSamples))
            throw new InvalidDataException("Графика: неизвестный API, лимит FPS или уровень MSAA.");
        if (!CacheLimits.Any(item => item.Value == ResourceCacheLimitMb)) throw new InvalidDataException("Неизвестный лимит кэша ресурсов.");
        if (!TextureQualities.Any(item => item.Value == TextureQuality)) throw new InvalidDataException("Неизвестное качество текстур.");
    }
    /// <summary>Recovers user preferences from missing or invalid older settings.</summary>
    public static GraphicsSettings Normalize(GraphicsSettings? value)
    {
        value ??= new();
        return value with { Api = Apis.Any(item => item.Value == value.Api) ? value.Api : "auto", MaxFps = FrameRates.Any(item => item.Value == value.MaxFps) ? value.MaxFps : 60,
            MsaaSamples = Antialiasing.Any(item => item.Value == value.MsaaSamples) ? value.MsaaSamples : 0,
            ResourceCacheLimitMb = CacheLimits.Any(item => item.Value == value.ResourceCacheLimitMb) ? value.ResourceCacheLimitMb : 64,
            TextureQuality = TextureQualities.Any(item => item.Value == value.TextureQuality) ? value.TextureQuality : "original" };
    }
}

/// <summary>Project-wide technical defaults stored independently from visual screen layouts.</summary>
public sealed class ProjectPerformanceSettings
{
    public GraphicsSettings Graphics { get; set; } = new();
    public bool FpsCounter { get; set; }
    public string FpsCorner { get; set; } = "top_right";
    public bool StatisticsOverlay { get; set; }
    public bool LoadingIndicator { get; set; } = true;
    public bool DebugMode { get; set; }
    public bool Preloading { get; set; } = true;
    /// <summary>Release optional resources when execution enters a different scene file.</summary>
    public bool UnloadPreviousScene { get; set; }
    /// <summary>Maximum active coroutine tasks, including the main branch.</summary>
    public int MaxAsyncTasks { get; set; } = 128;
    /// <summary>Profile selecting two bounded audio output buffers per channel.</summary>
    public string AudioBuffer { get; set; } = "balanced";
    public string SavesPath { get; set; } = "saves";
    public static IReadOnlyList<GraphicsOption<int>> AsyncTaskLimits { get; } = [new(32, "32"), new(64, "64"), new(128, "128")];
    public static IReadOnlyList<GraphicsOption<string>> AudioBuffers { get; } = [new("low_latency", "Низкая задержка"), new("balanced", "Сбалансированный"), new("stable", "Стабильный")];
    public static IReadOnlyList<GraphicsOption<string>> Corners { get; } = [new("top_left", "Слева сверху"), new("top_right", "Справа сверху"), new("bottom_left", "Слева снизу"), new("bottom_right", "Справа снизу")];
    public void Validate(string root)
    {
        if (Graphics == null) throw new InvalidDataException("Секция graphics не должна быть null.");
        Graphics.Validate();
        if (!Corners.Any(item => item.Value == FpsCorner)) throw new InvalidDataException("Неизвестный угол счётчика FPS.");
        if (!AsyncTaskLimits.Any(item => item.Value == MaxAsyncTasks)) throw new InvalidDataException("Лимит асинхронных задач: 32, 64 или 128.");
        if (!AudioBuffers.Any(item => item.Value == AudioBuffer)) throw new InvalidDataException("Неизвестный профиль аудио буфера.");
        _ = ResolveSavesPath(root);
    }
    /// <summary>Resolves the explicit save directory. Relative paths remain confined to the project.</summary>
    public string ResolveSavesPath(string root)
    {
        if (string.IsNullOrWhiteSpace(SavesPath)) throw new InvalidDataException("Путь к сохранениям не должен быть пустым.");
        string path = Path.IsPathFullyQualified(SavesPath) ? PathSafety.NormalizeDirectory(SavesPath) : PathSafety.ResolveRelative(root, SavesPath);
        if (path.Equals(PathSafety.NormalizeDirectory(root), StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(path)?.TrimEnd('\\', '/') == path.TrimEnd('\\', '/'))
            throw new InvalidDataException("Для сохранений нужна отдельная папка, не корень проекта или диска.");
        PathSafety.RejectLinks(path); return path;
    }
}

/// <summary>Overrides for one graph; gameplay commands and dialogue remain unchanged.</summary>
public sealed record ScenePerformanceSettings
{
    public string EffectsQuality { get; set; } = "high";
    public bool PostProcessing { get; set; } = true;
    /// <summary>Bypasses all project shaders for this scene without changing its authored files.</summary>
    public bool HideShaders { get; set; }
    public static IReadOnlyList<GraphicsOption<string>> Qualities { get; } = [new("high", "Высокое"), new("medium", "Среднее"), new("low", "Низкое")];
    public void Validate()
    {
        if (!Qualities.Any(item => item.Value == EffectsQuality)) throw new InvalidDataException("Неизвестное качество эффектов сцены.");
    }
}

/// <summary>A versioned collection keyed by project-relative graph paths.</summary>
public sealed class ScenePerformanceDocument
{
    internal static bool IsScenePath(string path) => ScriptDocument.IsVisualPath(path) || Path.GetExtension(path).Equals(".krl", StringComparison.OrdinalIgnoreCase);
    public int Version { get; set; } = 1;
    public Dictionary<string, ScenePerformanceSettings> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ScenePerformanceSettings For(string? relativePath) => relativePath != null && Scenes.TryGetValue(relativePath.Replace('\\', '/'), out var value) ? value : new();
    public void Validate(string root)
    {
        if (Version != 1 || Scenes == null || Scenes.Count > 5000) throw new InvalidDataException("Некорректный формат параметров сцен.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Scenes)
        {
            string full = PathSafety.ResolveRelative(root, pair.Key);
            if (!IsScenePath(full) || !paths.Add(full) || pair.Value == null) throw new InvalidDataException("Параметры сцены требуют уникальный путь к .krl или .knegraph.");
            pair.Value.Validate();
        }
    }
}

/// <summary>Bounded, validated and atomic persistence for project and scene performance settings.</summary>
public static class PerformanceSettingsStore
{
    public static ProjectPerformanceSettings LoadProject(string root)
    {
        var value = Read<ProjectPerformanceSettings>(root, "performance.json"); value.Validate(root); return value;
    }
    public static ScenePerformanceDocument LoadScenes(string root)
    {
        var value = Read<ScenePerformanceDocument>(root, "scene-performance.json"); value.Validate(root);
        value.Scenes = value.Scenes.ToDictionary(pair => Path.GetRelativePath(root, PathSafety.ResolveRelative(root, pair.Key)).Replace('\\', '/'), pair => pair.Value, StringComparer.OrdinalIgnoreCase); return value;
    }
    public static Task SaveProjectAsync(string root, ProjectPerformanceSettings settings, CancellationToken token = default)
    { settings.Validate(root); return WriteAsync(root, "performance.json", settings, token); }
    public static Task SaveScenesAsync(string root, ScenePerformanceDocument settings, CancellationToken token = default)
    { settings.Validate(root); return WriteAsync(root, "scene-performance.json", settings, token); }
    private static T Read<T>(string root, string name) where T : new()
    {
        string path = PathSafety.ResolveRelative(root, "engine/" + name);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Параметры больше 1 МБ: " + name);
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), EngineSettings.JsonOptions) ?? throw new InvalidDataException("Параметры не должны быть null: " + name); }
        catch (JsonException error) { throw new InvalidDataException("Ошибка engine/" + name + ": " + error.Message, error); }
    }
    private static async Task WriteAsync<T>(string root, string name, T value, CancellationToken token)
    {
        string path = PathSafety.ResolveRelative(root, "engine/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(value, EngineSettings.JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException("Параметры больше 1 МБ.");
            await File.WriteAllTextAsync(temporary, json, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
