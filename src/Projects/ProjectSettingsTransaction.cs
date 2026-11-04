using System.IO;

namespace KNE;

/// <summary>Stages related engine settings before replacing any file, and restores exact originals on failure.</summary>
internal static class ProjectSettingsTransaction
{
    internal static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    internal static async Task CommitAsync(IReadOnlyDictionary<string, byte[]> values, IReadOnlyDictionary<string, byte[]?> originals)
    {
        var temporary = new Dictionary<string, string>(); var committed = new List<string>();
        try
        {
            foreach (var pair in values)
            {
                ProjectBuildMetadata.CheckCurrent(pair.Key, originals[pair.Key]);
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                string temp = pair.Key + "." + Guid.NewGuid().ToString("N") + ".tmp"; temporary.Add(pair.Key, temp);
                await File.WriteAllBytesAsync(temp, pair.Value);
            }
            foreach (string path in values.Keys) ProjectBuildMetadata.CheckCurrent(path, originals[path]);
            foreach (string path in values.Keys) { ProjectBuildMetadata.CheckCurrent(path, originals[path]); File.Move(temporary[path], path, true); committed.Add(path); }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (string path in committed.AsEnumerable().Reverse())
                try { ProjectBuildMetadata.CheckCurrent(path, values[path]); await ProjectBuildMetadata.ReplaceAsync(path, originals[path]); } catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count > 0) throw new AggregateException("Не удалось отменить запись настроек.", new[] { error }.Concat(failures));
            throw;
        }
        finally { foreach (string temp in temporary.Values) if (File.Exists(temp)) File.Delete(temp); }
    }
}
