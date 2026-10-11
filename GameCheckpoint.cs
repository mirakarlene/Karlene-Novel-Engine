using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

public sealed record StoryAction(string Kind, string ExpectedFrame, int Option = 0, string? Command = null);

public sealed record GameCheckpoint(int Version, string ProjectPath, string Fingerprint, int Seed,
    IReadOnlyList<StoryAction> History, StoryFrame Frame, JsonElement State);

public interface IGameSession : IAsyncDisposable
{
    RuntimeWindowSettings WindowSettings { get; }
    StoryFrame Story { get; }
    SceneVisuals Visuals { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task AdvanceAsync(CancellationToken cancellationToken = default);
    Task ChooseAsync(int option, CancellationToken cancellationToken = default);
    Task ProcessCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>Atomic manual and automatic slots stored per project in the user profile.</summary>
public sealed class GameSaveStore
{
    public string DirectoryPath { get; }

    public GameSaveStore(string projectPath, string? storageDirectory = null)
    {
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathSafety.NormalizeDirectory(projectPath).ToUpperInvariant())))[..24];
        DirectoryPath = storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "saves", identity);
    }

    public string SlotPath(string slot)
    {
        if (slot is not ("quick" or "auto")) throw new ArgumentException("Неизвестный слот сохранения.", nameof(slot));
        return Path.Combine(DirectoryPath, slot + ".json");
    }

    public bool Exists(string slot) => File.Exists(SlotPath(slot));

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
                checkpoint.State.ValueKind != JsonValueKind.Object || checkpoint.History.Count > 10_000)
                throw new InvalidDataException("Некорректный формат сохранения.");
            return checkpoint;
        }
        catch (JsonException ex) { throw new InvalidDataException("Сохранение повреждено.", ex); }
    }
}
