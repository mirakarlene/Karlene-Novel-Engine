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
    public Dictionary<string, DateTimeOffset> ProjectLastOpened { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> HiddenProjects { get; set; } = [];
    public string Theme { get; set; } = "kne";
    /// <summary>Optional name displayed on the editor welcome screen.</summary>
    public string UserName { get; set; } = "";
    /// <summary>Window-only block palette and category-symbol preference.</summary>
    public string AccessibilityMode { get; set; } = "normal";
    /// <summary>Decorative welcome-object shape; defaults to the original bubbles.</summary>
    public string WelcomeObjects { get; set; } = "bubbles";

    public void RememberProject(string path)
    {
        string normalized = PathSafety.NormalizeDirectory(path);
        HiddenProjects.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        ProjectLastOpened[normalized] = DateTimeOffset.UtcNow;
        RecentProjects = (RecentProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && !p.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Prepend(normalized).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    /// <summary>Hides a recent/discovered entry without deleting its project files.</summary>
    public void ForgetProject(string path)
    {
        string normalized = PathSafety.NormalizeDirectory(path);
        RecentProjects.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        ProjectLastOpened.Remove(normalized);
        if (!HiddenProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase)) HiddenProjects.Add(normalized);
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
                settings.Theme = EditorTheme.Normalize(settings.Theme);
                settings.UserName = (settings.UserName ?? "").Trim();
                settings.AccessibilityMode = BlockAccessibility.Normalize(settings.AccessibilityMode);
                settings.WelcomeObjects = WelcomeBubbles.Normalize(settings.WelcomeObjects);
                settings.HiddenProjects = (settings.HiddenProjects ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                settings.ProjectLastOpened = new(settings.ProjectLastOpened ?? [], StringComparer.OrdinalIgnoreCase);
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
