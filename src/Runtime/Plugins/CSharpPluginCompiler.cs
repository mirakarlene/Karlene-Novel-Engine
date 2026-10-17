using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace KNE;

/// <summary>Compiles project-owned C# source in memory, without invoking an installed SDK or writing executable files.</summary>
internal sealed class CSharpPluginCompiler : IDisposable
{
    private readonly Dictionary<string, CompiledPlugin> cache = new(StringComparer.Ordinal);
    private static readonly Lazy<MetadataReference[]> References = new(CreateReferences);

    public void Execute(string relativePath, string source, IPluginContext context)
    {
        string key = relativePath + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (!cache.TryGetValue(key, out var plugin))
        {
            plugin = Compile(relativePath, source, context.CancellationToken);
            cache.Add(key, plugin);
        }
        try { plugin.Instance.Execute(context); }
        catch (Exception error)
        {
            throw new PluginSourceException(relativePath, FindLine(error, relativePath),
                "Ошибка C#-плагина: " + error.Message, error);
        }
    }

    private static CompiledPlugin Compile(string path, string source, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(LanguageVersion.CSharp12), path, cancellationToken: cancellationToken);
        string name = "KnePlugin_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(name, [tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug,
                deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        using var assemblyStream = new MemoryStream();
        using var symbolsStream = new MemoryStream();
        var emitted = compilation.Emit(assemblyStream, symbolsStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb), cancellationToken: cancellationToken);
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(30).ToArray();
            var first = errors.FirstOrDefault();
            int? line = first?.Location.IsInSource == true ? first.Location.GetLineSpan().StartLinePosition.Line + 1 : null;
            throw new PluginSourceException(path, line, "Не удалось скомпилировать C#-плагин:\n" + string.Join("\n", errors.Select(d => d.ToString())));
        }
        var loadContext = new AssemblyLoadContext(name, isCollectible: true);
        try
        {
            assemblyStream.Position = 0;
            symbolsStream.Position = 0;
            var assembly = loadContext.LoadFromStream(assemblyStream, symbolsStream);
            var implementations = assembly.GetTypes().Where(t => t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition &&
                typeof(IKnePlugin).IsAssignableFrom(t)).ToArray();
            if (implementations.Length != 1)
                throw new PluginSourceException(path, null, "C#-файл должен объявлять ровно один public класс IKnePlugin.");
            var constructor = implementations[0].GetConstructor(Type.EmptyTypes)
                ?? throw new PluginSourceException(path, null, "IKnePlugin требует public конструктор без параметров.");
            return new(loadContext, (IKnePlugin)constructor.Invoke(null));
        }
        catch (Exception error)
        {
            loadContext.Unload();
            if (error is PluginSourceException) throw;
            throw new PluginSourceException(path, FindLine(error, path), "Не удалось создать C#-плагин: " + (error.InnerException?.Message ?? error.Message), error);
        }
    }

    private static int? FindLine(Exception error, string path)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            var frame = new StackTrace(current, true).GetFrames()?.FirstOrDefault(f =>
                string.Equals(f.GetFileName(), path, StringComparison.OrdinalIgnoreCase) && f.GetFileLineNumber() > 0);
            if (frame != null) return frame.GetFileLineNumber();
        }
        return null;
    }

    private static MetadataReference[] CreateReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(IKnePlugin).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    public void Dispose()
    {
        foreach (var plugin in cache.Values) plugin.Context.Unload();
        cache.Clear();
    }

    private sealed record CompiledPlugin(AssemblyLoadContext Context, IKnePlugin Instance);
}

internal sealed class PluginSourceException(string file, int? line, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string File { get; } = file;
    public int? Line { get; } = line;
}
