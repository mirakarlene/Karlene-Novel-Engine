using System.IO;
using System.Text;

namespace KNE;

/// <summary>Creates project scripts without overwriting work and imports custom UI textures.</summary>
public static class ProjectScriptFiles
{
    /// <summary>Creates a Lua/KRL/C# template inside game, or returns an existing script unchanged.</summary>
    public static async Task<string> EnsureAsync(string root, string filePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        string game = PathSafety.ResolveRelative(root, "game"); string path = PathSafety.EnsureInside(game, filePath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".lua" or ".krl" or ".cs")) throw new ArgumentException("Скрипт должен иметь расширение .lua, .krl или .cs.");
        PathSafety.ValidateFileName(Path.GetFileName(path));
        if (File.Exists(path)) return Path.GetRelativePath(root, path).Replace('\\', '/');
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string template = extension == ".cs" ? """
            using KNE;

            public sealed class ProjectPlugin : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Log("Плагин выполнен");
                    // context.Set("score", 1);
                }
            }
            """ : "-- Скрипт проекта. Выполняется при входе в блок.\n-- state.score = 1\n";
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, template, new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested(); PathSafety.EnsureInside(game, path); File.Move(staging, path, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    /// <summary>Uses a project image directly, or copies an external image into game/ui with a unique name.</summary>
    public static async Task<string> ImportTextureAsync(string root, string source, CancellationToken token = default)
    {
        PathSafety.RejectLinks(source);
        if (Path.GetExtension(source).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")) throw new ArgumentException("Нужен файл изображения PNG/JPG/BMP/GIF.");
        string normalizedRoot = PathSafety.NormalizeDirectory(root);
        if (Path.GetFullPath(source).StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(root, PathSafety.EnsureInside(root, source)).Replace('\\', '/');
        string directory = PathSafety.ResolveRelative(root, "game/ui"); Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant());
        await ProjectImport.CopyFileAsync(root, source, target, token); return Path.GetRelativePath(root, target).Replace('\\', '/');
    }
}
