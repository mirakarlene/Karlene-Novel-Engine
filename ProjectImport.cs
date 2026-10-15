using System.IO;
using System.Text;

namespace KNE;

/// <summary>Publishes imported files atomically without replacing project content.</summary>
public static class ProjectImport
{
    public static Task CopyFileAsync(string projectRoot, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        PublishAsync(projectRoot, destinationPath, async output =>
        {
            PathSafety.RejectLinks(sourcePath);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await input.CopyToAsync(output, cancellationToken);
        }, cancellationToken);

    public static Task WriteScriptAsync(string projectRoot, string destinationPath, string code, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(destinationPath), ".krl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Адаптацию сценария нужно сохранить в файл .krl.");
        ArgumentNullException.ThrowIfNull(code);
        byte[] bytes = new UTF8Encoding(false).GetBytes(code);
        return PublishAsync(projectRoot, destinationPath, output => output.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken);
    }

    private static async Task PublishAsync(string projectRoot, string destinationPath, Func<FileStream, Task> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string gameRoot = PathSafety.ResolveRelative(projectRoot, "game");
        string target = PathSafety.EnsureInside(gameRoot, destinationPath);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        string directory = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Папка назначения не существует.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Файл назначения уже существует. Выберите другое имя.");
        string temporary = PathSafety.EnsureInside(gameRoot, Path.Combine(directory, ".kne-import-" + Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await write(output);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(target);
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
