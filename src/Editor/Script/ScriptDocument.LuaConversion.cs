using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KNE;

public sealed partial class ScriptDocument
{
    internal async Task<string> ConvertLuaAsync(CancellationToken token = default)
    {
        if (!IsLuaPath(FilePath)) throw new InvalidOperationException("Конвертация доступна для .lua и .vlua.");
        string source = PathSafety.EnsureInside(root, FilePath);
        string target = PathSafety.EnsureInside(root, Path.ChangeExtension(source, IsVisual ? ".lua" : ".vlua"));
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Файл назначения уже существует: " + Path.GetFileName(target));
        string content;
        Encoding encoding = new UTF8Encoding(false);
        if (IsVisual)
        {
            content = Graph!.Compile();
            encoding = Graph.LuaEncoding switch
            {
                "utf8-bom" => new UTF8Encoding(true), "utf16-le" => Encoding.Unicode, "utf16-be" => Encoding.BigEndianUnicode,
                "utf32-le" => Encoding.UTF32, "utf32-be" => new UTF32Encoding(true, true), _ => encoding
            };
        }
        else
        {
            var graph = VisualLua.ParseSource(Code);
            graph.LuaEncoding = DetectLuaEncoding(source);
            content = graph.Serialize();
        }
        if ((long)encoding.GetByteCount(content) + encoding.GetPreamble().Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Результат конвертации больше 8 МБ.");
        string sourceRelative = Path.GetRelativePath(root, source).Replace('\\', '/');
        string targetRelative = Path.GetRelativePath(root, target).Replace('\\', '/');
        string configPath = PathSafety.ResolveRelative(root, "project.json");
        byte[]? config = ProjectSettingsTransaction.Read(configPath);
        byte[]? updatedConfig = UpdateEntryPoints(config, sourceRelative, targetRelative);
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backup = source + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool displaced = false, installed = false;
        try
        {
            await File.WriteAllTextAsync(staging, content, encoding, token);
            if (!File.Exists(source) || await File.ReadAllTextAsync(source, token) != diskContent)
                throw new IOException("Исходный файл изменён вне редактора.");
            ProjectBuildMetadata.CheckCurrent(configPath, config);
            token.ThrowIfCancellationRequested();
            PathSafety.EnsureInside(root, source);
            File.Move(source, backup);
            displaced = true;
            File.Move(staging, target);
            installed = true;
            if (updatedConfig != null)
                await ProjectSettingsTransaction.CommitAsync(new Dictionary<string, byte[]> { [configPath] = updatedConfig },
                    new Dictionary<string, byte[]?> { [configPath] = config });
        }
        catch
        {
            if (displaced) File.Move(backup, source);
            if (installed && await File.ReadAllTextAsync(target) == content) File.Delete(target);
            throw;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        try { File.Delete(backup); }
        catch (IOException error) { EngineDiagnostics.Trace("Осталась временная копия Lua", error.Message); }
        catch (UnauthorizedAccessException error) { EngineDiagnostics.Trace("Осталась временная копия Lua", error.Message); }
        return target;
    }

    private static string? DetectLuaEncoding(string source)
    {
        using var stream = File.OpenRead(source);
        Span<byte> prefix = stackalloc byte[4];
        int length = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        var bytes = prefix[..length];
        if (bytes.StartsWith(Encoding.UTF32.Preamble)) return "utf32-le";
        if (bytes.StartsWith(new UTF32Encoding(true, true).Preamble)) return "utf32-be";
        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble)) return "utf16-be";
        if (bytes.StartsWith(Encoding.Unicode.Preamble)) return "utf16-le";
        return bytes.StartsWith(Encoding.UTF8.Preamble) ? "utf8-bom" : null;
    }

    private static byte[]? UpdateEntryPoints(byte[]? original, string source, string target)
    {
        if (original == null) return null;
        var json = JsonNode.Parse(original)?.AsObject() ?? throw new InvalidDataException("Некорректный project.json.");
        var entry = json["entry_points"] as JsonObject ?? new();
        bool changed = Update("main_scene", ["game/scenes/main.lua", "main.lua"]) |
            Update("init_script", ["game/scripts/gamelogic.lua", "game/scripts/game_init.lua", "gamelogic.lua"]);
        if (!changed) return null;
        if (entry.Parent == null) json["entry_points"] = entry;
        return JsonSerializer.SerializeToUtf8Bytes(json, ProjectConfiguration.JsonOptions);

        bool Update(string key, string[] defaults)
        {
            string? value = entry[key]?.GetValue<string>()?.Replace('\\', '/');
            if (value != null && !value.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                value == null && !defaults.Contains(source, StringComparer.OrdinalIgnoreCase)) return false;
            entry[key] = target;
            return true;
        }
    }
}
