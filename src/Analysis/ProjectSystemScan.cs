using System.IO;

namespace KNE;

internal static class ProjectSystemScan
{
    internal static async Task<string> RunAsync(ProjectInfo project, string statistics, CancellationToken token)
    {
        var errors = new List<string>();
        int scenes = 0, links = 0, dynamic = 0, assets = 0;
        var snapshot = ProjectLoader.EnumerateFiles(project.Path, token);
        errors.AddRange(snapshot.Warnings.Take(50));
        try { _ = await ProjectLoader.LoadProjectAsync(project.Path, token).ConfigureAwait(false); }
        catch (Exception error) when (Reportable(error)) { errors.Add("Конфигурация: " + error.Message); }

        try
        {
            var navigation = await SceneNavigationIndex.BuildAsync(project, token: token).ConfigureAwait(false);
            scenes = navigation.Nodes.Count;
            links = navigation.Links.Count;
            dynamic = navigation.DynamicReferences;
            errors.AddRange(navigation.Nodes.SelectMany(node => node.Errors.Select(error => node.File + ": " + error)).Distinct().Take(60));
        }
        catch (Exception error) when (Reportable(error)) { errors.Add("Сцены: " + error.Message); }

        void CheckAsset(string name, bool audio = false)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            token.ThrowIfCancellationRequested();
            assets++;
            try
            {
                string path = ResourcePaths.Resolve(project.Path, name, audio: audio);
                using var _ = File.OpenRead(path);
            }
            catch (Exception error) when (Reportable(error))
            {
                if (errors.Count < 150) errors.Add(name + ": " + error.Message);
            }
        }

        var engine = project.Engine;
        CheckAsset(engine.Theme.BackgroundImage ?? "");
        CheckAsset(engine.Game.MenuMusic ?? "", audio: true);
        CheckAsset(project.Configuration.Metadata?.Icon ?? "");
        foreach (var image in engine.Screens.Screens.SelectMany(screen => screen.Elements).Where(element => element.Type == "image"))
            CheckAsset(image.Image ?? "");
        foreach (var scene in engine.ScenePerformance.Scenes.Values.Concat(engine.ScenePerformance.Functions.Values.SelectMany(value => value.Values)))
            CheckAsset(scene.Music ?? "", audio: true);
        await using var syntax = new SyntaxCheckSession();
        foreach (string path in snapshot.Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ScriptDocument.IsVisualPath(path))
                {
                    var graph = VisualScript.Parse(ScriptProtection.ReadText(path));
                    foreach (var block in graph.Nodes)
                    {
                        if (block.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character)
                            CheckAsset(block.Path);
                        if (block.Kind is ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.Voice)
                            CheckAsset(block.Path, audio: true);
                        if (block.Kind == ScriptNodeKind.ShaderContainer)
                            CheckAsset(block.Path);
                    }
                }
                else if (Path.GetExtension(path).ToLowerInvariant() is ".krl" or ".lua")
                {
                    string text = ScriptProtection.ReadText(path);
                    if (Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = await syntax.CheckAsync(LuaSource.Prepare(StartupScripts.Declaration(text).Source), token).ConfigureAwait(false);
                        if (result.Error != null && errors.Count < 150) errors.Add(Path.GetRelativePath(project.Path, path) + ": " + result.Error);
                    }
                    CheckLiteralAssets(text, CheckAsset);
                }
            }
            catch (Exception error) when (Reportable(error))
            {
                if (errors.Count < 150) errors.Add(Path.GetRelativePath(project.Path, path) + ": " + error.Message);
            }
        }
        string summary = $"Проверка проекта\nФайлов: {snapshot.Files.Count} · Функций сцен: {scenes} · Связей: {links}\nСтатических ссылок на ресурсы: {assets} · Динамических переходов: {dynamic}\n{statistics}";
        if (errors.Count == 0) return summary + "\nЗамечаний по проверенным данным нет.";
        return summary + "\n\nЗамечания:\n" + string.Join("\n", errors.Distinct().Take(50).Select(error => error.Length <= 500 ? error : error[..500] + "…"));
    }

    private static void CheckLiteralAssets(string source, Action<string, bool> check)
    {
        var tokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        for (int i = 0; i + 3 < tokens.Length; i++)
        {
            string name = source[tokens[i].Start..tokens[i].End];
            if (name is not ("background" or "show_character" or "play_music" or "play_sound") ||
                source[tokens[i + 1].Start..tokens[i + 1].End] != "(") continue;
            var literal = tokens[i + 2];
            string next = source[tokens[i + 3].Start..tokens[i + 3].End];
            if (literal.Kind != LuaTokenKind.String || next is not ("," or ")")) continue;
            string raw = source[literal.Start..literal.End];
            if (raw.Length >= 2 && raw[0] is '\'' or '"' && !raw.Contains('\\'))
                check(raw[1..^1], name is "play_music" or "play_sound");
        }
    }

    private static bool Reportable(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException;
}
