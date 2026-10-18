using System.IO;
using System.Text.Json;
using System.Xml.Linq;

namespace KNE;

/// <summary>Persists per-user preferences without writing beside the executable.</summary>
public sealed class UserSettings
{
    public string EditorPath { get; set; } = "";
    /// <summary>The single directory whose immediate children are KNE projects.</summary>
    public string ProjectsStoragePath { get; set; } = "";
    public List<string> RecentProjects { get; set; } = [];

    public void RememberProject(string path)
    {
        string normalized = PathSafety.NormalizeDirectory(path);
        RecentProjects = (RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && !p.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Prepend(normalized).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }
}

public sealed class UserSettingsStore
{
    private readonly string? legacyConfigPath;
    public string SettingsPath { get; }

    public UserSettingsStore(string? settingsPath = null, string? legacyConfigPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "settings.json");
        this.legacyConfigPath = legacyConfigPath ?? (settingsPath == null ? Path.Combine(AppContext.BaseDirectory, "KNE.dll.config") : null);
    }

    public async Task<UserSettings> LoadAsync()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<UserSettings>(await File.ReadAllTextAsync(SettingsPath))
                    ?? throw new InvalidDataException("Файл настроек содержит null.");
                settings.EditorPath ??= "";
                settings.ProjectsStoragePath ??= "";
                settings.RecentProjects = (settings.RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
                return settings;
            }
            catch (JsonException ex) { throw new InvalidDataException("Файл настроек повреждён.", ex); }
        }
        // Read-only migration from the previous application's configuration.
        if (legacyConfigPath == null || !File.Exists(legacyConfigPath)) return new UserSettings();
        return new UserSettings
        {
            EditorPath = XDocument.Load(legacyConfigPath).Descendants("add")
                .FirstOrDefault(e => (string?)e.Attribute("key") == "EditorPath")?.Attribute("value")?.Value ?? ""
        };
    }

    public async Task SaveAsync(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string directory = Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(settings, ProjectConfiguration.JsonOptions));
            File.Move(staging, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
