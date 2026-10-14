using System.Text.Json;
using System.Text.Json.Serialization;

namespace KNE;

/// <summary>Shared project.json contract. Unknown fields survive configuration updates.</summary>
public sealed class ProjectConfiguration
{
    [JsonPropertyName("authoring_mode")] public ProjectAuthoringMode AuthoringMode { get; set; } = ProjectAuthoringMode.Code;
    [JsonPropertyName("metadata")] public ProjectMetadata? Metadata { get; set; }
    [JsonPropertyName("game_info")] public GameInfo? GameInfo { get; set; }
    [JsonPropertyName("entry_points")] public EntryPoints? EntryPoints { get; set; }
    [JsonPropertyName("name")] public string? LegacyName { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public string DisplayName(string fallback) =>
        !string.IsNullOrWhiteSpace(Metadata?.Name) ? Metadata.Name :
        !string.IsNullOrWhiteSpace(LegacyName) ? LegacyName : fallback;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>The initial source format chosen when creating a project; both formats remain available.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectAuthoringMode>))]
public enum ProjectAuthoringMode { Code, Visual }

public sealed class ProjectMetadata
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("created_date")] public string? CreatedDate { get; set; }
    [JsonPropertyName("kne_version")] public string KneVersion { get; set; } = "1.0.0";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class GameInfo
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class EntryPoints
{
    [JsonPropertyName("main_scene")] public string? MainScene { get; set; }
    [JsonPropertyName("init_script")] public string? InitScript { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
