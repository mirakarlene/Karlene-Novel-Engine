using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

internal sealed record SnapshotFile(string Path, long Length, string Sha256);
internal sealed record SnapshotManifest(int Version, string Project, string Kind, DateTimeOffset Created, SnapshotFile[] Files);
internal sealed record ProjectSnapshot(string Archive, SnapshotManifest Manifest)
{
    public string Label => $"{Manifest.Created.ToLocalTime():dd.MM.yyyy HH:mm:ss} · файлов: {Manifest.Files.Length}";
}

/// <summary>Separate, project-scoped archives with bounded retention and verified file-only history restoration.</summary>
internal sealed class ProjectSnapshots(string storage)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal string DirectoryFor(string project, string kind)
    {
        if (kind is not ("backup" or "history")) throw new ArgumentException("Неизвестный тип копии.");
        string root = PathSafety.NormalizeDirectory(project), target = PathSafety.NormalizeDirectory(storage);
        PathSafety.RejectLinks(root); PathSafety.RejectLinks(target);
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase) || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Копии должны храниться вне папки проекта.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        return PathSafety.ResolveRelative(target, key + "/" + kind);
    }

    internal Task<ProjectSnapshot?> CreateAsync(string project, string kind, int keep, IEnumerable<string>? files = null, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { return await CreateCoreAsync(project, kind, keep, files, token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, token);

    private async Task<ProjectSnapshot?> CreateCoreAsync(string project, string kind, int keep, IEnumerable<string>? files, CancellationToken token, bool prune = true)
    {
        if (keep is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(keep));
        project = PathSafety.NormalizeDirectory(project);
        string folder = DirectoryFor(project, kind);
        var selected = (files ?? EnumerateProject(project, token)).Select(path => PathSafety.EnsureInside(project, path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0) return null;
        if (selected.Length > 50000) throw new InvalidDataException("В копии допускается до 50000 файлов.");
        Directory.CreateDirectory(folder);
        string temporary = PathSafety.ResolveRelative(folder, ".snapshot-" + Guid.NewGuid().ToString("N") + ".tmp");
        var entries = new List<SnapshotFile>();
        try
        {
            using (var zip = new ZipArchive(File.Create(temporary), ZipArchiveMode.Create))
            {
                if (kind == "backup" && files == null)
                {
                    zip.CreateEntry("project/");
                    foreach (string directory in EnumerateProject(project, token, directories: true))
                        zip.CreateEntry("project/" + Path.GetRelativePath(project, directory).Replace('\\', '/') + "/");
                }
                foreach (string path in selected)
                {
                    token.ThrowIfCancellationRequested(); PathSafety.RejectLinks(path);
                    string relative = Path.GetRelativePath(project, path).Replace('\\', '/');
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var entry = zip.CreateEntry("project/" + relative, CompressionLevel.Fastest);
                    var modified = File.GetLastWriteTimeUtc(path); if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
                    using var output = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[81920]; long length = 0; int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); length += read; }
                    entries.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset())));
                }
                var manifest = new SnapshotManifest(1, project, kind, DateTimeOffset.UtcNow, entries.ToArray());
                using var metadata = zip.CreateEntry("snapshot.json").Open();
                await JsonSerializer.SerializeAsync(metadata, manifest, cancellationToken: token).ConfigureAwait(false);
            }
            var previous = List(project, kind).FirstOrDefault();
            if (kind == "history" && previous != null && previous.Manifest.Files.SequenceEqual(entries)) return previous;
            token.ThrowIfCancellationRequested();
            string archive = PathSafety.ResolveRelative(folder, $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}.zip");
            File.Move(temporary, archive);
            var created = Read(archive, project, kind);
            if (prune) Prune(project, kind, keep, token);
            return created;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Prune(string project, string kind, int keep, CancellationToken token)
    {
        foreach (var old in List(project, kind).Skip(keep)) { token.ThrowIfCancellationRequested(); PathSafety.RejectLinks(old.Archive); File.Delete(old.Archive); }
    }

    internal IReadOnlyList<ProjectSnapshot> List(string project, string kind)
    {
        string folder = DirectoryFor(project, kind);
        if (!Directory.Exists(folder)) return [];
        var result = new List<ProjectSnapshot>();
        foreach (string path in Directory.EnumerateFiles(folder, "*.zip"))
        {
            try { result.Add(Read(path, project, kind)); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { EngineDiagnostics.Trace("Повреждённая копия пропущена", Path.GetFileName(path) + ": " + error.Message); }
        }
        return result.OrderByDescending(item => item.Manifest.Created).ThenByDescending(item => item.Archive, StringComparer.Ordinal).ToArray();
    }

    private static ProjectSnapshot Read(string archive, string project, string kind)
    {
        PathSafety.RejectLinks(archive);
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry("snapshot.json") ?? throw new InvalidDataException("В копии нет описания.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Слишком большое описание копии.");
        using var input = entry.Open();
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(input) ?? throw new InvalidDataException("Пустое описание копии.");
        if (manifest.Version != 1 || manifest.Kind != kind || !string.Equals(manifest.Project, PathSafety.NormalizeDirectory(project), StringComparison.OrdinalIgnoreCase) || manifest.Files == null || manifest.Files.Length > 50000)
            throw new InvalidDataException("Копия принадлежит другому проекту или имеет неподдерживаемый формат.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') || file.Length < 0 || file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) || !paths.Add(file.Path)) throw new InvalidDataException("Некорректный список файлов копии.");
            try { _ = PathSafety.ResolveRelative(project, file.Path); }
            catch (ArgumentException error) { throw new InvalidDataException("Небезопасный путь в копии.", error); }
        }
        return new(archive, manifest);
    }

    internal Task RestoreAsync(string project, ProjectSnapshot snapshot, CancellationToken token = default, int keep = 25) => Task.Run(async () =>
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        string stage = Path.Combine(Path.GetTempPath(), "kne-restore-" + Guid.NewGuid().ToString("N"));
        var applied = new List<(string Target, string? Previous)>();
        string? addedGuard = null;
        try
        {
            string folder = DirectoryFor(project, "history");
            string archive = PathSafety.EnsureInside(folder, snapshot.Archive);
            var verified = Read(archive, project, "history");
            Directory.CreateDirectory(stage);
            using (var zip = ZipFile.OpenRead(archive))
            {
                for (int i = 0; i < verified.Manifest.Files.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var file = verified.Manifest.Files[i];
                    if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Файл истории больше 64 МБ.");
                    var entry = zip.GetEntry("project/" + file.Path) ?? throw new InvalidDataException("В копии отсутствует " + file.Path);
                    if (entry.Length != file.Length) throw new InvalidDataException("Размер файла в копии изменён.");
                    string target = PathSafety.ResolveRelative(project, file.Path); PathSafety.RejectLinks(target);
                    using var input = entry.Open();
                    using (var output = File.Create(Path.Combine(stage, i + ".new"))) await input.CopyToAsync(output, token).ConfigureAwait(false);
                    using var staged = File.OpenRead(Path.Combine(stage, i + ".new"));
                    if (Convert.ToHexString(await SHA256.HashDataAsync(staged, token).ConfigureAwait(false)) != file.Sha256) throw new InvalidDataException("Контрольная сумма файла не совпадает: " + file.Path);
                }
            }
            // Do not prune history until every replacement succeeds: a failed restore must keep the chosen version.
            var known = List(project, "history").Select(item => item.Archive).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var guard = await CreateCoreAsync(project, "history", keep, verified.Manifest.Files.Select(file => PathSafety.ResolveRelative(project, file.Path)).Where(File.Exists), token, prune: false).ConfigureAwait(false);
            if (guard != null && !known.Contains(guard.Archive)) addedGuard = guard.Archive;
            for (int i = 0; i < verified.Manifest.Files.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string target = PathSafety.ResolveRelative(project, verified.Manifest.Files[i].Path); PathSafety.RejectLinks(target);
                string? previous = File.Exists(target) ? Path.Combine(stage, i + ".old") : null;
                if (previous != null) File.Copy(target, previous);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Replace(Path.Combine(stage, i + ".new"), target);
                applied.Add((target, previous));
            }
            try { Prune(project, "history", keep, CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { EngineDiagnostics.Trace("Очистка истории после успешного отката", error.Message); }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception>();
            foreach (var (target, previous) in applied.AsEnumerable().Reverse())
                try { if (previous != null) Replace(previous, target); else File.Delete(target); }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            if (addedGuard != null && failures.Count == 0)
                try { PathSafety.RejectLinks(addedGuard); File.Delete(addedGuard); }
                catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException) { EngineDiagnostics.Trace("Очистка временной версии после ошибки отката", cleanupFailure.Message); }
            if (failures.Count > 0) throw new AggregateException("Не удалось полностью восстановить файлы после ошибки отката.", new[] { failure }.Concat(failures));
            throw;
        }
        finally
        {
            try
            {
                string verifiedStage = PathSafety.EnsureInside(Path.GetTempPath(), stage);
                if (!Path.GetFileName(verifiedStage).StartsWith("kne-restore-", StringComparison.Ordinal)) throw new IOException("Некорректная временная папка.");
                if (Directory.Exists(verifiedStage)) Directory.Delete(verifiedStage, true);
            }
            finally { gate.Release(); }
        }
    }, token);

    private static void Replace(string source, string target)
    {
        string temp = target + ".kne-restore-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temp); File.Move(temp, target, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static IEnumerable<string> EnumerateProject(string root, CancellationToken token, bool directories = false)
    {
        var folders = new Stack<string>(); folders.Push(root);
        while (folders.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested(); PathSafety.RejectLinks(folder);
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            { PathSafety.RejectLinks(entry); if (Directory.Exists(entry)) { folders.Push(entry); if (directories) yield return entry; } else if (!directories) yield return entry; }
        }
    }
}
