using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

internal sealed class PersistentGameStore
{
    private const int MaxBytes = 1_048_576;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement<object?>(null);
    private readonly string path;

    internal PersistentGameStore(string directory) => path = PathSafety.ResolveRelative(directory, "persistent");

    internal static async Task CopyIfMissingAsync(string sourceDirectory, string destinationDirectory)
    {
        string source = PathSafety.ResolveRelative(sourceDirectory, "persistent");
        string destination = PathSafety.ResolveRelative(destinationDirectory, "persistent");
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination) || !File.Exists(source)) return;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > MaxBytes) throw new InvalidDataException("Файл persistent больше 1 МБ.");
        Directory.CreateDirectory(destinationDirectory);
        string staging = PathSafety.ResolveRelative(destinationDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output);
            PathSafety.RejectLinks(destination);
            try { File.Move(staging, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { File.Delete(staging); }
    }

    internal JsonElement Get(string key)
    {
        ValidateKey(key);
        return Access((data, _) => data.Values.GetValueOrDefault(key, Empty));
    }

    internal bool Contains(string key)
    {
        ValidateKey(key);
        return Access((data, _) => data.Values.ContainsKey(key));
    }

    internal void Set(string key, JsonElement value)
    {
        ValidateKey(key);
        EngineSettings.ValidateState(value, 0);
        Access((data, save) =>
        {
            data.Values[key] = value;
            save(data);
            return true;
        });
    }

    internal void Remove(string key)
    {
        ValidateKey(key);
        Access((data, save) =>
        {
            if (data.Values.Remove(key)) save(data);
            return true;
        });
    }

    internal void Clear() => Access((_, save) =>
    {
        save(new Data());
        return true;
    });

    internal long LaunchCount => Access((data, _) => data.Launches);

    internal void RecordLaunch() => Access((data, save) =>
    {
        save(data with { Launches = checked(data.Launches + 1) });
        return true;
    });

    private T Access<T>(Func<Data, Action<Data>, T> action)
    {
        // Multiple game instances share one file. Hold a path-specific lock across read/modify/replace.
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, "KNE.Persistent." + identity);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException("Хранилище persistent занято другим экземпляром игры.");
        }
        catch (AbandonedMutexException) { /* The previous owner has exited. */ }
        try
        {
            PathSafety.RejectLinks(path);
            if (!File.Exists(path)) return action(new Data(), Save);
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) throw new InvalidDataException("Файл persistent больше 1 МБ.");
            var data = JsonSerializer.Deserialize<Data>(stream) ?? throw new InvalidDataException("Пустой файл persistent.");
            if (data.Version != 1 || data.Values == null || data.Values.Count > 10_000 || data.Launches < 0)
                throw new InvalidDataException("Некорректный файл persistent.");
            foreach (var (key, value) in data.Values)
            {
                ValidateKey(key);
                EngineSettings.ValidateState(value, 0);
            }
            stream.Dispose();
            return action(data, Save);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private void Save(Data data)
    {
        if (data.Values.Count > 10_000) throw new InvalidDataException("Слишком много переменных persistent.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Файл persistent больше 1 МБ.");
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = PathSafety.ResolveRelative(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            PathSafety.RejectLinks(path);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void ValidateKey(string key)
    {
        if (key.Length is 0 or > 80 || key.Any(char.IsControl))
            throw new ArgumentException("Имя переменной: от 1 до 80 символов без управляющих знаков.");
    }

    private sealed record Data
    {
        public int Version { get; init; } = 1;
        public long Launches { get; init; }
        public Dictionary<string, JsonElement> Values { get; init; } = new(StringComparer.Ordinal);
    }
}
