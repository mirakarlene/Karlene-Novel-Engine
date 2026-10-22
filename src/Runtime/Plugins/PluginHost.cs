using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Owns source assemblies and their registrations for exactly one project session.</summary>
internal sealed class PluginHost : IDisposable
{
    private readonly CSharpPluginCompiler compiler = new();
    private NLua.Lua? editorLua;
    internal PluginApi Api { get; }
    internal PluginHost(string directory, bool editor, object engine) { Api = new(directory, editor) { Engine = engine }; }
    internal static HashSet<string> ManifestSourcePaths(string directory)
    {
        string folder = PathSafety.ResolveRelative(directory, "plugins"); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return paths;
        foreach (string path in Directory.EnumerateFiles(folder, "*.kneplugin.json").Take(65))
        {
            PathSafety.EnsureInside(directory, path);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Манифест плагина больше 64 КБ.");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Пустой манифест плагина.");
            paths.Add(PathSafety.ResolveRelative(directory, manifest.Source));
        }
        return paths;
    }
    internal void Load(Func<IPluginContext> context, CancellationToken token = default,
        IEnumerable<string>? applicationPlugins = null, bool includeProjectPlugins = true)
    {
        if (!EngineLaunch.PluginsEnabled) return;
        var loadedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in ApplicationPluginFiles.Normalize(applicationPlugins))
        {
            token.ThrowIfCancellationRequested(); PluginPrivileges.EnsureUserProcess();
            LoadSource(ApplicationPluginFiles.Validate(source), source, context);
            loadedSources.Add(source);
        }
        if (!includeProjectPlugins) return;
        string folder = PathSafety.ResolveRelative(Api.ProjectDirectory, "plugins");
        if (!Directory.Exists(folder)) return;
        var manifests = Directory.EnumerateFiles(folder, "*.kneplugin.json").OrderBy(p => p, StringComparer.Ordinal).Take(65).ToArray();
        if (manifests.Length > 64) throw new InvalidDataException("Не более 64 расширений проекта.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in manifests)
        {
            token.ThrowIfCancellationRequested(); PluginPrivileges.EnsureUserProcess();
            PathSafety.EnsureInside(Api.ProjectDirectory, path);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Манифест плагина больше 64 КБ.");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Пустой манифест плагина.");
            PluginApi.Id(manifest.Id);
            if (manifest.ApiVersion != 1) throw new InvalidDataException("Неподдерживаемая версия API плагина " + manifest.Id);
            if (!ids.Add(manifest.Id)) throw new InvalidDataException("Повторный ID плагина: " + manifest.Id);
            string source = PathSafety.ResolveRelative(Api.ProjectDirectory, manifest.Source);
            if (Path.GetExtension(source).ToLowerInvariant() is not (".cs" or ".lua") || new FileInfo(source).Length > 8 * 1024 * 1024) throw new InvalidDataException("Расширение требует исходный .cs или .lua до 8 МБ.");
            if (loadedSources.Add(source)) LoadSource(source, manifest.Source, context);
        }
    }
    private void LoadSource(string source, string diagnosticPath, Func<IPluginContext> context)
    {
        if (source.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            if (Api.Engine is LuaRuntime runtime) runtime.LoadLuaPluginManifest(diagnosticPath, File.ReadAllText(source));
            else
            {
                editorLua ??= new NLua.Lua(); editorLua.State.Encoding = System.Text.Encoding.UTF8;
                new LuaPluginModule(Api, editorLua).Install(); editorLua.DoString(File.ReadAllText(source), "@" + diagnosticPath);
            }
            return;
        }
        var invocation = context();
        try { compiler.Execute(diagnosticPath, File.ReadAllText(source), invocation); }
        finally { (invocation as IDisposable)?.Dispose(); }
    }
    internal void Execute(string path, string source, IPluginContext context) => compiler.Execute(path, source, context);
    public void Dispose() { Api.Dispose(); compiler.Dispose(); editorLua?.Dispose(); editorLua = null; }
}

internal sealed class EditorPluginContext(PluginApi api, CancellationToken cancellationToken = default) : IPluginContext
{
    public PluginApi Api => api;
    public string ProjectDirectory => api.ProjectDirectory;
    public CancellationToken CancellationToken => cancellationToken;
    public object? Get(string name) => throw new InvalidOperationException("Состояние игры доступно только в игровом worker; проверьте Api.IsEditor.");
    public void Set(string name, object? value) => throw new InvalidOperationException("Состояние игры доступно только в игровом worker; проверьте Api.IsEditor.");
    public void Log(string message) => EngineDiagnostics.Trace("Плагин редактора", message);
}
