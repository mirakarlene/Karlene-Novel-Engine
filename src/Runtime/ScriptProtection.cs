using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KNE;

/// <summary>Authenticated release-source packaging. The player embeds its key; this deters casual extraction, not reverse engineering.</summary>
internal static class ScriptProtection
{
    internal static bool HasEmbeddedKey { get { using var key = typeof(ScriptProtection).Assembly.GetManifestResourceStream("KNE.ScriptKey"); return key != null; } }
    private static readonly byte[] Magic = "KNESRC01"u8.ToArray();
    internal static bool IsScript(string path) => Path.GetExtension(path).ToLowerInvariant() is ".krl" or ".lua" or ".cs" or ".knegraph";
    internal static byte[] Encrypt(byte[] source, byte[] key)
    {
        byte[] data = new byte[36 + source.Length]; Magic.CopyTo(data, 0); RandomNumberGenerator.Fill(data.AsSpan(8, 12));
        using var cipher = new AesGcm(key, 16); cipher.Encrypt(data.AsSpan(8, 12), source, data.AsSpan(36), data.AsSpan(20, 16), Magic); return data;
    }
    internal static byte[] ReadBytes(string path, byte[]? key = null)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024 + 36) throw new InvalidDataException("Сценарий больше 8 МБ.");
        byte[] data = File.ReadAllBytes(path);
        if (!data.AsSpan().StartsWith(Magic))
        {
            if (data.Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ.");
            return data;
        }
        if (data.Length < 36) throw new InvalidDataException("Повреждён зашифрованный сценарий.");
        if (key == null)
        {
            using var resource = typeof(ScriptProtection).Assembly.GetManifestResourceStream("KNE.ScriptKey") ?? throw new InvalidDataException("Ключ зашифрованного сценария отсутствует в игроке.");
            using var buffer = new MemoryStream(); resource.CopyTo(buffer); key = buffer.ToArray();
        }
        byte[] plain = new byte[data.Length - 36];
        try { using var cipher = new AesGcm(key, 16); cipher.Decrypt(data.AsSpan(8, 12), data.AsSpan(36), data.AsSpan(20, 16), plain, Magic); }
        catch (CryptographicException error) { throw new InvalidDataException("Сценарий повреждён или ключ неверен.", error); }
        return plain;
    }
    internal static string ReadText(string path)
    {
        using var reader = new StreamReader(new MemoryStream(ReadBytes(path)), new UTF8Encoding(false, true), true); return reader.ReadToEnd();
    }
    internal static async Task EncryptProjectAsync(string root, byte[] key, CancellationToken token)
    {
        foreach (string path in ProjectLoader.EnumerateFiles(root, token).Files.Where(IsScript))
        {
            token.ThrowIfCancellationRequested(); byte[] plain = await File.ReadAllBytesAsync(path, token); await File.WriteAllBytesAsync(path, Encrypt(plain, key), token);
        }
    }
}
