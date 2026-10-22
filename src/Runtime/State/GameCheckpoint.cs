using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

public sealed record StoryAction(string Kind, string ExpectedFrame, int Option = 0, string? Command = null, bool Cancel = false);

public sealed record GameCheckpoint(int Version, string ProjectPath, string Fingerprint, int Seed,
    IReadOnlyList<StoryAction> History, StoryFrame Frame, JsonElement State);

public interface IGameSession : IAsyncDisposable
{
    IReadOnlyList<string> PluginScreens => [];
    JsonElement Variables => default;
    Task<bool> InterceptInputAsync(PluginInput input, CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task NotifyPluginInputAsync(PluginInput input, CancellationToken cancellationToken = default) => Task.CompletedTask;
    RuntimeWindowSettings WindowSettings { get; }
    StoryFrame Story { get; }
    SceneVisuals Visuals { get; }
    double LogicMilliseconds => 0;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task AdvanceAsync(CancellationToken cancellationToken = default);
    Task ChooseAsync(int option, CancellationToken cancellationToken = default);
    Task SubmitInputAsync(string text, bool cancel = false, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Сессия не поддерживает ввод текста."));
    Task TickAsync(double seconds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task TriggerAsync(TriggerSignal signal, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ProcessCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>Atomic named save slots inside the project, with non-destructive import of legacy profile slots.</summary>
public sealed class GameSaveStore
{
    public string DirectoryPath { get; }
    private readonly string projectPath;

    public GameSaveStore(string projectPath, string? storageDirectory = null, string? legacyDirectory = null)
    {
        this.projectPath = PathSafety.NormalizeDirectory(projectPath);
        DirectoryPath = storageDirectory == null ? PerformanceSettingsStore.LoadProject(projectPath).ResolveSavesPath(projectPath) : PathSafety.NormalizeDirectory(storageDirectory);
        if ((legacyDirectory != null || storageDirectory == null) && !File.Exists(Path.Combine(DirectoryPath, ".legacy-import-complete"))) ImportLegacySlots(legacyDirectory ?? LegacyDirectory(projectPath));
    }

    /// <summary>Locates save slots created by versions that stored progress in the user profile.</summary>
    public static string LegacyDirectory(string projectPath)
    {
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.NormalizeDirectory(projectPath).ToUpperInvariant())))[..24];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "saves", identity);
    }

    /// <summary>Copies quick/auto slots without replacing project slots or deleting legacy files.</summary>
    public void ImportLegacySlots(string source)
    {
        foreach (string slot in new[] { "quick", "auto" })
        {
            string oldPath = PathSafety.ResolveRelative(source, slot + ".json");
            string target = SlotPath(slot);
            if (!File.Exists(oldPath) || File.Exists(target)) continue;
            Directory.CreateDirectory(DirectoryPath);
            try { File.Copy(oldPath, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { }
        }
    }

    public string SlotPath(string slot)
    {
        PathSafety.ValidateFileName(slot);
        if (slot.Length > 80 || slot.Contains('.') || slot.Equals("preferences", StringComparison.OrdinalIgnoreCase) || slot.Equals("settings", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Имя слота: до 80 символов без точек; preferences/settings зарезервированы.", nameof(slot));
        return PathSafety.ResolveRelative(DirectoryPath, slot + ".json");
    }

    public bool Exists(string slot) => File.Exists(SlotPath(slot));

    /// <summary>Enumerates readable and damaged slots for a manual selection screen.</summary>
    public async Task<IReadOnlyList<GameSaveEntry>> ListAsync(CancellationToken token = default)
    {
        PathSafety.RejectLinks(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        var entries = new List<GameSaveEntry>();
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            string slot = Path.GetFileNameWithoutExtension(path);
            try { _ = SlotPath(slot); } catch (ArgumentException) { continue; }
            string text;
            bool damaged = false;
            try { text = (await LoadAsync(slot, token)).Frame.Text; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { text = "Повреждённое сохранение"; damaged = true; }
            entries.Add(new(slot, File.GetLastWriteTimeUtc(path), text.Length > 100 ? text[..100] + "…" : text, damaged));
        }
        return entries.OrderByDescending(e => e.ModifiedUtc).ThenBy(e => e.Slot, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Deletes only the selected validated slot.</summary>
    public void Delete(string slot) => File.Delete(SlotPath(slot));

    /// <summary>Deletes recognized checkpoints belonging to this project, preserving preferences and unrelated files.</summary>
    public async Task<int> DeleteAllProgressAsync(CancellationToken token = default)
    {
        PathSafety.RejectLinks(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return 0;
        var targets = new List<string>();
        // Never recursively delete a selected directory. Check ownership and every individual path first.
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            string slot = Path.GetFileNameWithoutExtension(path);
            try
            {
                var value = await LoadAsync(slot, token);
                if (PathSafety.NormalizeDirectory(value.ProjectPath).Equals(projectPath, StringComparison.OrdinalIgnoreCase)) targets.Add(SlotPath(slot));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or System.Text.Json.JsonException) { }
        }
        token.ThrowIfCancellationRequested();
          foreach (string target in targets) { PathSafety.RejectLinks(target); File.Delete(target); }
          if (targets.Count > 0) await File.WriteAllTextAsync(PathSafety.ResolveRelative(DirectoryPath, ".legacy-import-complete"), "Progress was reset; do not restore legacy slots automatically.");
        return targets.Count;
    }

    public async Task SaveAsync(string slot, GameCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        string target = SlotPath(slot);
        string json = JsonSerializer.Serialize(checkpoint, ProjectConfiguration.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 8_000_000)
            throw new InvalidDataException("Состояние игры слишком большое для сохранения.");
        Directory.CreateDirectory(DirectoryPath);
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<GameCheckpoint> LoadAsync(string slot, CancellationToken cancellationToken = default)
    {
        string path = SlotPath(slot);
        if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("Файл сохранения слишком большой.");
        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            if (Encoding.UTF8.GetByteCount(json) > 8_000_000) throw new InvalidDataException("Файл сохранения слишком большой.");
            var checkpoint = JsonSerializer.Deserialize<GameCheckpoint>(json);
            if (checkpoint is not { Version: 1, History: not null, Frame: not null } ||
                checkpoint.State.ValueKind != JsonValueKind.Object || checkpoint.History.Count > 10_000 ||
                checkpoint.Frame.Speaker == null || checkpoint.Frame.Text == null || checkpoint.Frame.Options == null ||
                checkpoint.Frame.Options.Count > 20 || checkpoint.Frame.Options.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Некорректный формат сохранения.");
            return checkpoint;
        }
        catch (JsonException ex) { throw new InvalidDataException("Сохранение повреждено.", ex); }
    }
}

/// <summary>Metadata shown in the save selection screen.</summary>
public sealed record GameSaveEntry(string Slot, DateTime ModifiedUtc, string Preview, bool Damaged)
{
    public string ModifiedLabel => ModifiedUtc.ToLocalTime().ToString("g");
    public override string ToString() => $"{Slot} · {ModifiedUtc.ToLocalTime():g} · {Preview}";
}
