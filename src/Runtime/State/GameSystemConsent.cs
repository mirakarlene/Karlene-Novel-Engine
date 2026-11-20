using System.IO;
using System.Text.Json;

namespace KNE;

internal sealed class GameSystemConsent(string directory)
{
    internal const string FileName = "system-permissions.json";
    private readonly string path = PathSafety.ResolveRelative(directory, FileName);
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<bool> AllowAsync(string category, Func<Task<bool>> ask, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            PathSafety.RejectLinks(path);
            var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                using var input = File.OpenRead(path);
                if (input.Length > 4096) throw new InvalidDataException("Файл разрешений системы слишком большой.");
                decisions = await JsonSerializer.DeserializeAsync<Dictionary<string, bool>>(input, cancellationToken: token)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("Файл разрешений системы пуст.");
            }
            if (decisions.TryGetValue(category, out bool allowed)) return allowed;
            allowed = await ask().ConfigureAwait(false);
            decisions[category] = allowed;
            Directory.CreateDirectory(directory);
            string temporary = PathSafety.ResolveRelative(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(decisions), token).ConfigureAwait(false);
                PathSafety.RejectLinks(path);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
            return allowed;
        }
        finally { gate.Release(); }
    }
}
