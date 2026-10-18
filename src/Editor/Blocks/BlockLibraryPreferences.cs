using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>User favorites and the twelve most recently inserted catalog entries.</summary>
public sealed class BlockLibraryPreferences
{
    /// <summary>Keys of starred catalog entries, including their group.</summary>
    public List<string> Favorites { get; set; } = [];
    /// <summary>Distinct entry keys ordered from most to least recently used.</summary>
    public List<string> Recent { get; set; } = [];
    /// <summary>Identifies an entry without conflating conditions from different groups.</summary>
    public static string Key(BlockDefinition entry) => entry.Group + "/" + entry.Kind;
    /// <summary>Removes obsolete entries and enforces the recent-page capacity.</summary>
    public void Normalize()
    {
        var known = BlockLibrary.Definitions.Select(Key).ToHashSet(StringComparer.Ordinal);
        Favorites = (Favorites ?? []).Select(MigrateKey).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        Recent = (Recent ?? []).Select(MigrateKey).Where(known.Contains).Distinct(StringComparer.Ordinal).Take(BlockLibraryView.PageSize).ToList();
    }
    private static string MigrateKey(string key) => key switch
    {
        "narrative/Condition" => "narrative/Branching",
        _ => key
    };
    /// <summary>Adds a successfully inserted entry to the head of the recent list.</summary>
    public void RecordUse(BlockDefinition entry)
    {
        string key = Key(entry);
        Recent = Recent.Where(value => value != key).Prepend(key).Take(BlockLibraryView.PageSize).ToList();
    }
}

/// <summary>Stores catalog preferences separately from editor and project settings.</summary>
public sealed class BlockLibraryPreferenceStore
{
    private readonly SemaphoreSlim writes = new(1, 1);
    /// <summary>The per-user JSON path, injectable for isolated tests.</summary>
    public string SettingsPath { get; }
    /// <summary>Uses local application data unless an explicit path is supplied.</summary>
    public BlockLibraryPreferenceStore(string? settingsPath = null) => SettingsPath = settingsPath ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "block-library.json");
    /// <summary>Loads valid entries; missing, malformed, oversized or inaccessible preferences start empty.</summary>
    public async Task<BlockLibraryPreferences> LoadAsync()
    {
        try
        {
            var info = new FileInfo(SettingsPath);
            if (!info.Exists || info.Length > 1024 * 1024) return new();
            var preferences = JsonSerializer.Deserialize<BlockLibraryPreferences>(await File.ReadAllTextAsync(SettingsPath)) ?? new();
            preferences.Normalize();
            return preferences;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    /// <summary>Atomically publishes one snapshot without touching other user settings.</summary>
    public async Task SaveAsync(BlockLibraryPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Normalize();
        string json = JsonSerializer.Serialize(preferences, ProjectConfiguration.JsonOptions);
        await writes.WaitAsync();
        string? staging = null;
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!;
            Directory.CreateDirectory(directory);
            staging = Path.Combine(directory, ".block-library-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(staging, json);
            File.Move(staging, SettingsPath, overwrite: true);
        }
        finally
        {
            try { if (staging != null && File.Exists(staging)) File.Delete(staging); }
            finally { writes.Release(); }
        }
    }
}
