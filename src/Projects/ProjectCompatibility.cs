using System.IO;
using System.Text;
using System.Text.Json;

namespace KNE;

/// <summary>A schema incompatibility, distinct from malformed JSON or an unfinished graph.</summary>
public sealed record ProjectCompatibilityIssue(string File, string Reason, bool CanMigrate);

/// <summary>Detects graph schema changes without executing scripts or discarding unknown properties.</summary>
public static class ProjectCompatibility
{
    private static readonly JsonSerializerOptions LegacyOptions = new(VisualScript.JsonOptions) { PropertyNameCaseInsensitive = true };

    /// <summary>Checks graph files for obsolete blocks, property casing, and unsupported schema fields.</summary>
    public static async Task<IReadOnlyList<ProjectCompatibilityIssue>> InspectAsync(ProjectInfo project, CancellationToken cancellationToken = default)
    {
        var issues = new List<ProjectCompatibilityIssue>();
        foreach (var file in project.Files.Where(file => ScriptDocument.IsVisualPath(file.FilePath)))
        {
            string path = PathSafety.EnsureInside(project.Path, file.FilePath);
            if (new FileInfo(path).Length > 8 * 1024 * 1024) continue;
            string source = await File.ReadAllTextAsync(path, cancellationToken);
            var issue = await Task.Run(() => Inspect(Path.GetRelativePath(project.Path, path), source), cancellationToken);
            if (issue != null) issues.Add(issue);
        }
        return issues;
    }

    internal static ProjectCompatibilityIssue? Inspect(string file, string source)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(source); }
        catch (JsonException) { return null; } // Syntax corruption is not evidence of an engine upgrade.
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = json.RootElement;
            if (HasDuplicateProperties(root))
                return new(file, "Обнаружены повторяющиеся имена JSON-полей. Для миграции нужно устранить неоднозначность без потери данных.", false);
            var version = root.EnumerateObject().FirstOrDefault(property => property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)).Value;
            if (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number != 1)
                return new(file, $"Версия схемы {number}; этот движок поддерживает версию 1. Нужна совместимая версия KNE.", false);

            VisualScript? tolerant;
            try { tolerant = JsonSerializer.Deserialize<VisualScript>(source, LegacyOptions); }
            catch (JsonException error)
            {
                // An unmapped property is a structural format mismatch. A wrong value type is data corruption.
                if (error.Message.Contains("could not be mapped", StringComparison.Ordinal) ||
                    error.Path?.EndsWith(".Kind", StringComparison.Ordinal) == true)
                    return new(file, "Несовместимые поля или тип блока. " + error.Message + " Автоматическое удаление этих данных запрещено.", false);
                return null;
            }
            if (tolerant == null) return null;
            if (tolerant.Nodes?.Any(node => node != null && !Enum.IsDefined(node.Kind)) == true)
                return new(file, "Схема содержит тип блока, неизвестный этой версии движка.", false);
            bool legacy = tolerant.Nodes?.Any(node => node?.Kind == ScriptNodeKind.Condition) == true;
            try { _ = VisualScript.Parse(source); }
            catch (InvalidDataException error) when (error.InnerException is JsonException)
            {
                try { _ = VisualScript.Parse(JsonSerializer.Serialize(tolerant, VisualScript.JsonOptions)); }
                catch (InvalidDataException) { return null; }
                return new(file, "Старое написание имён JSON-полей; требуется нормализация формата.", true);
            }
            catch (InvalidDataException) { return null; }
            return legacy ? new(file, "Устаревший блок «Условие» будет заменён на «Ветвление» с сохранением ID, параметров и связей.", true) : null;
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }

    /// <summary>Migrates only recognized schemas; backs up exact original bytes before replacing any file.</summary>
    /// <returns>The directory of original files, each with an .original suffix to avoid loading it as a game script.</returns>
    public static async Task<string> MigrateAsync(ProjectInfo project, CancellationToken cancellationToken = default)
    {
        var issues = await InspectAsync(project, cancellationToken);
        if (issues.Count == 0) throw new InvalidOperationException("Проект не требует миграции.");
        if (issues.Any(issue => !issue.CanMigrate)) throw new InvalidDataException("Проект содержит неизвестный формат. Откройте его в совместимой версии KNE; исходные данные сохранены.");
        var changes = new List<(string Path, byte[] Original, byte[] Updated)>();
        foreach (var issue in issues)
        {
            string path = PathSafety.ResolveRelative(project.Path, issue.File);
            byte[] original = await File.ReadAllBytesAsync(path, cancellationToken);
            using var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string source = await reader.ReadToEndAsync(cancellationToken);
            if (Inspect(issue.File, source) is not { CanMigrate: true }) throw new IOException("Файл изменился во время проверки: " + issue.File);
            var model = JsonSerializer.Deserialize<VisualScript>(source, LegacyOptions)!;
            string migrated = VisualScript.Parse(JsonSerializer.Serialize(model, VisualScript.JsonOptions)).Serialize();
            changes.Add((path, original, Encoding.UTF8.GetBytes(migrated)));
        }
        string backup = PathSafety.ResolveRelative(project.Path, ".kne-backups/migration-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var change in changes)
        {
            string copy = PathSafety.ResolveRelative(backup, Path.GetRelativePath(project.Path, change.Path) + ".original");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            await File.WriteAllBytesAsync(copy, change.Original, cancellationToken);
        }
        var committed = new List<(string Path, byte[] Original, byte[] Updated)>();
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = PathSafety.EnsureInside(project.Path, change.Path);
                if (!(await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(change.Original))
                    throw new IOException("Файл изменён другой программой: " + path);
                string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(staging, change.Updated, cancellationToken);
                    PathSafety.EnsureInside(project.Path, path);
                    if (!(await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(change.Original))
                        throw new IOException("Файл изменён другой программой: " + path);
                    File.Move(staging, path, overwrite: true); committed.Add(change);
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
            }
        }
        catch
        {
            foreach (var change in committed.AsEnumerable().Reverse())
            {
                string path = PathSafety.EnsureInside(project.Path, change.Path);
                if (File.ReadAllBytes(path).SequenceEqual(change.Updated)) File.WriteAllBytes(path, change.Original);
            }
            throw;
        }
        return backup;
    }
}
