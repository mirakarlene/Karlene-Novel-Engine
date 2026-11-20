using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

internal sealed record SystemCall(string Operation, JsonElement Arguments);
internal sealed record SystemReply(JsonElement Value, string? Error = null)
{
    internal static SystemReply From<T>(T value) => new(JsonSerializer.SerializeToElement(value));
    internal static SystemReply Failure(string error) => new(JsonSerializer.SerializeToElement<object?>(null), error);
}
public sealed record SystemReplayEntry(string Operation, JsonElement Arguments, JsonElement Value, string? Error);

internal sealed class GameSystemServices
{
    private readonly ProjectInfo project;
    private readonly GameSaveStore saves;
    private readonly PersistentGameStore persistent;
    private readonly bool savesWritable = true;
    internal string ConsentDirectory { get; }
    internal double PlayedSeconds { get; set; }

    internal GameSystemServices(ProjectInfo project, GameSaveStore? saves = null)
    {
        this.project = project;
        if (saves != null) this.saves = saves;
        else
        {
            var configured = StandalonePlayer.RuntimeSaves(project);
            var storage = GameStartupDiagnostics.SelectStorage(project, configured.DirectoryPath);
            savesWritable = storage.Effective.Writable;
            this.saves = storage.Effective.Directory == configured.DirectoryPath ? configured : new(project.Path, storage.Effective.Directory);
        }
        var locations = PersistentDirectories(project.Path, this.saves.DirectoryPath);
        persistent = new(locations.For(project.Engine.Performance.PersistentLocation));
        ConsentDirectory = locations.Portable ? locations.Local : locations.Profile;
    }

    internal static (string Slots, string Local, string Profile, bool Portable, bool Msix) PersistentDirectories(string root, string slots)
    {
        var manifest = StandalonePlayer.RuntimeManifest;
        string identity = StandalonePlayer.SaveIdentity(root);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..24];
        string profile = manifest != null
            ? (manifest with { SaveLocation = "appdata" }).SavesDirectory(AppContext.BaseDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "persistent", key);
        string local = manifest != null
            ? (manifest with { SaveLocation = "local" }).SavesDirectory(AppContext.BaseDirectory)
            : PathSafety.ResolveRelative(root, "saves");
        return (slots, local, profile, manifest?.ReleaseMode == "portable", manifest?.ReleaseMode == "msix");
    }

    internal void RecordLaunch() => persistent.RecordLaunch();

    internal async Task<SystemReply> InvokeAsync(SystemCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (call.Operation)
        {
            case "persistent_set": persistent.Set(String(call, 0), Arg(call, 1)); break;
            case "persistent_get": return SystemReply.From(persistent.Get(String(call, 0)));
            case "persistent_has": return SystemReply.From(persistent.Contains(String(call, 0)));
            case "persistent_delete": persistent.Remove(String(call, 0)); break;
            case "persistent_clear_confirmed": persistent.Clear(); break;
            case "persistent_clear": return SystemReply.Failure("Сброс persistent требует подтверждения в окне игры.");
            case "save_exists": return SystemReply.From(saves.Exists(String(call, 0)));
            case "save_read":
            {
                string key = String(call, 1);
                PersistentGameStore.ValidateKey(key);
                var checkpoint = await LoadOwnedAsync(String(call, 0), token).ConfigureAwait(false);
                return checkpoint.State.TryGetProperty(key, out var value) ? SystemReply.From(value) : SystemReply.From<object?>(null);
            }
            case "save_delete":
            {
                string slot = String(call, 0);
                if (saves.Exists(slot))
                {
                    await LoadOwnedAsync(slot, token).ConfigureAwait(false);
                    saves.Delete(slot);
                }
                break;
            }
            case "save_list": return SystemReply.From(await ReadSlotsAsync(token).ConfigureAwait(false));
            case "save_checkpoint":
            {
                if (!savesWritable) return SystemReply.From(false);
                var checkpoint = Arg(call, 1).Deserialize<GameCheckpoint>() ?? throw new InvalidDataException("Пустая точка сохранения.");
                DemandOwnership(checkpoint);
                await saves.SaveAsync(String(call, 0), checkpoint, token).ConfigureAwait(false);
                break;
            }
            case "real_time":
            {
                var now = DateTime.Now;
                return SystemReply.From(new { hour = now.Hour, minute = now.Minute, weekday = ((int)now.DayOfWeek + 6) % 7 + 1 });
            }
            case "real_date":
            {
                var now = DateTime.Today;
                string format = String(call, 0, "dd.MM.yyyy");
                if (format.Length > 80) throw new ArgumentException("Формат даты слишком длинный.");
                return SystemReply.From(new { day = now.Day, month = now.Month, year = now.Year, formatted = now.ToString(format, CultureInfo.InvariantCulture) });
            }
            case "play_time": return SystemReply.From(PlayedSeconds / 60d);
            case "build_version": return SystemReply.From(project.Configuration.Metadata?.Version ?? "");
            case "build_info":
                var metadata = project.Configuration.Metadata;
                return SystemReply.From(new
                {
                    name = project.Title, version = metadata?.Version ?? "", author = metadata?.Author ?? "",
                    team = metadata?.Team ?? "", format = StandalonePlayer.RuntimeManifest?.ReleaseMode ?? "vm"
                });
            case "device_info": return SystemReply.From(OperatingSystem.IsWindows() ? "Windows" : System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            case "launch_count": return SystemReply.From(persistent.LaunchCount);
            case "project_path": return SystemReply.From(project.Path);
            default: return SystemReply.Failure("Для этой операции требуется окно игры.");
        }
        return SystemReply.From<object?>(null);
    }

    internal SystemReply InvokeFile(SystemCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int textLimit = 1_048_576;
        string input = String(call, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (input.StartsWith(@"\\?\", StringComparison.Ordinal) || input.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Пути к устройствам Windows недоступны.");
        string canonical = Path.GetFullPath(input, project.Path);
        string? root = Path.GetPathRoot(canonical);
        foreach (string component in canonical[(root?.Length ?? 0)..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            PathSafety.ValidateFileName(component);
        string path = string.Equals(Path.TrimEndingDirectorySeparator(canonical), project.Path, StringComparison.OrdinalIgnoreCase)
            ? project.Path
            : project.Engine.Security.ResolveFile(project.Path, canonical);
        PathSafety.RejectLinks(path);
        if (Path.GetFileName(path).Equals("persistent", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals(GameSystemConsent.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для файла используются отдельные команды KNE.");
        switch (call.Operation)
        {
            case "read_text_file":
            {
                using var source = File.OpenRead(path);
                if (source.Length > textLimit) throw new InvalidDataException("Текстовый файл больше 1 МБ.");
                var data = new byte[(int)source.Length];
                source.ReadExactly(data);
                string value = new UTF8Encoding(false, true).GetString(data);
                return SystemReply.From(value.StartsWith('\uFEFF') ? value[1..] : value);
            }
            case "write_text_file":
            {
                string content = String(call, 1);
                long bytes = Encoding.UTF8.GetByteCount(content);
                string mode = String(call, 2, "write");
                if (mode is not ("write" or "append")) throw new ArgumentException("Укажите write или append.");
                if (mode == "append" && File.Exists(path)) bytes += new FileInfo(path).Length;
                if (bytes > textLimit) throw new InvalidDataException("Текстовый файл больше 1 МБ.");
                if (mode == "append") File.AppendAllText(path, content);
                else File.WriteAllText(path, content);
                break;
            }
            case "path_exists": return SystemReply.From(File.Exists(path) || Directory.Exists(path));
            case "file_size":
            {
                double unit = String(call, 1, "bytes") switch
                {
                    "bytes" => 1, "kib" => 1024, "mib" => 1_048_576, "gib" => 1_073_741_824,
                    _ => throw new ArgumentException("Укажите bytes, kib, mib или gib.")
                };
                return SystemReply.From(new FileInfo(path).Length / unit);
            }
            case "list_files":
            {
                var files = new List<string>();
                foreach (string file in Directory.EnumerateFiles(path))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    if (files.Count == 10_000) throw new InvalidDataException("В папке больше 10 000 файлов.");
                    files.Add(Path.GetFileName(file));
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                return SystemReply.From(files);
            }
            case "create_folder": Directory.CreateDirectory(path); break;
            default: throw new ArgumentException("Неизвестная файловая команда.");
        }
        return SystemReply.From<object?>(null);
    }

    private void DemandOwnership(GameCheckpoint checkpoint)
    {
        if (!string.Equals(checkpoint.ProjectPath, StandalonePlayer.SaveIdentity(project.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сохранение принадлежит другой игре.");
    }

    private async Task<GameCheckpoint> LoadOwnedAsync(string slot, CancellationToken token)
    {
        var checkpoint = await saves.LoadAsync(slot, token).ConfigureAwait(false);
        DemandOwnership(checkpoint);
        return checkpoint;
    }

    private async Task<IReadOnlyList<string>> ReadSlotsAsync(CancellationToken token)
    {
        var result = new List<string>();
        foreach (var entry in await saves.ListAsync(token).ConfigureAwait(false))
        {
            if (entry.Damaged) continue;
            try { await LoadOwnedAsync(entry.Slot, token).ConfigureAwait(false); }
            catch (InvalidDataException) { continue; }
            if (result.Count == 10_000) throw new InvalidDataException("Больше 10 000 слотов сохранений.");
            result.Add(entry.Slot);
        }
        return result;
    }

    internal static JsonElement Arg(SystemCall call, int index)
    {
        if (call.Arguments.ValueKind == JsonValueKind.Array && index < call.Arguments.GetArrayLength())
            return call.Arguments[index];
        throw new ArgumentException("Не задан аргумент " + (index + 1) + ".");
    }

    internal static string String(SystemCall call, int index, string? fallback = null)
    {
        if (fallback != null && call.Arguments.ValueKind == JsonValueKind.Array && index >= call.Arguments.GetArrayLength())
            return fallback;
        var value = Arg(call, index);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Аргумент должен быть строкой.");
    }
}

internal static class PersistentDirectorySelection
{
    internal static string For(this (string Slots, string Local, string Profile, bool Portable, bool Msix) locations, string location) =>
        locations.Portable ? locations.Local : location switch
        {
            "appdata" => locations.Profile,
            "local" => locations.Msix ? locations.Profile : locations.Local,
            _ => locations.Slots
        };
}
