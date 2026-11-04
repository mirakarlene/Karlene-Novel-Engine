using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KNE;

internal sealed record ProjectExeMetadata(string Author, string Team, string Version)
{
    internal static ProjectExeMetadata From(ProjectBuildSettings value) => new(value.Author, value.Team, value.Version);
}

internal sealed record ProjectBuildWrite(ProjectBuildSettings Settings, byte[] BuildJson, byte[] ProjectJson, byte[] OriginalProject, byte[]? OriginalBuild);

/// <summary>Canonical project.json EXE fields; missing values retain compatibility with older build.json files.</summary>
internal sealed record ProjectBuildMetadata(string Path, byte[] Original, JsonObject Configuration)
{
    internal static ProjectBuildMetadata Read(string root)
    {
        string path = PathSafety.ResolveRelative(root, "project.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("project.json больше 1 МБ.");
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            return new(path, bytes, JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("project.json должен содержать объект."));
        }
        catch (JsonException error) { throw new InvalidDataException("Некорректный project.json.", error); }
    }

    internal ProjectBuildSettings Apply(ProjectBuildSettings fallback)
    {
        var metadata = Object(Configuration, "metadata", create: false);
        string Field(string name, string previous)
        {
            if (metadata?[name] is not { } value) return previous;
            try { return value.GetValue<string>(); }
            catch (InvalidOperationException error) { throw new InvalidDataException("metadata." + name + " в project.json должен содержать строку.", error); }
        }
        return fallback with { Author = Field("author", fallback.Author), Team = Field("team", fallback.Team), Version = Field("version", fallback.Version) };
    }

    internal byte[] Updated(ProjectBuildSettings value, string? title)
    {
        var configuration = (JsonObject)Configuration.DeepClone(); var metadata = Object(configuration, "metadata", create: true)!;
        metadata["author"] = value.Author; metadata["team"] = value.Team; metadata["version"] = value.Version;
        if (title != null)
        {
            metadata["name"] = title; Object(configuration, "game_info", create: true)!["title"] = title;
            if (configuration["name"] != null) configuration["name"] = title;
        }
        return Encoding.UTF8.GetBytes(configuration.ToJsonString(ProjectConfiguration.JsonOptions));
    }

    internal static ProjectBuildSettings Resolve(string root, ProjectBuildSettings fallback) =>
        File.Exists(PathSafety.ResolveRelative(root, "project.json")) ? Read(root).Apply(fallback) : fallback;

    private static JsonObject? Object(JsonObject parent, string name, bool create)
    {
        if (parent[name] is JsonObject value) return value;
        if (parent[name] != null) throw new InvalidDataException(name + " в project.json должен содержать объект.");
        if (!create) return null;
        var added = new JsonObject(); parent[name] = added; return added;
    }

    internal static void CheckCurrent(string path, byte[]? expected)
    {
        PathSafety.RejectLinks(path);
        bool same = expected == null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        if (!same) throw new IOException("Файл изменён вне окна параметров. Откройте параметры заново: " + path);
    }

    internal static async Task ReplaceAsync(string path, byte[]? contents)
    {
        PathSafety.RejectLinks(path);
        if (contents == null) { File.Delete(path); return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, contents); PathSafety.RejectLinks(path); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
