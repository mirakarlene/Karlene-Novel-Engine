using System.IO;
using System.Text.Json;

namespace KNE;

internal static class GameTextCatalog
{
    internal const string LanguageSettingLabel = "Язык игры";
    internal const string LanguageLoadFailed = "Не удалось загрузить перевод.";

    internal sealed record Result(GameTextEntry[] Entries, string[] SkippedFiles);

    internal static GameTextCategory ScreenCategory(GameScreen screen) =>
        screen.Kind == "main_menu" ? GameTextCategory.MainMenu : GameTextCategory.Interface;

    internal static Task<Result> ReadAsync(ProjectInfo project, IReadOnlyDictionary<string, string> drafts, CancellationToken token) =>
        Task.Run(() => Read(project, drafts, token), token);

    private static Result Read(ProjectInfo project, IReadOnlyDictionary<string, string> drafts, CancellationToken token)
    {
        var entries = new List<GameTextEntry>();
        var skipped = new List<string>();
        void Add(GameTextCategory category, string? text, string location)
        {
            if (!string.IsNullOrWhiteSpace(text)) entries.Add(new(category, text, location));
        }

        var game = project.Engine.Game;
        Add(GameTextCategory.MainMenu, project.Title, "project.json");
        foreach (string text in new[] { game.Subtitle, game.StartLabel, game.ContinueLabel, game.ExitLabel })
            Add(GameTextCategory.MainMenu, text, "engine/game.json");
        foreach (var screen in project.Engine.Screens.Screens)
            foreach (var element in screen.Elements.Where(element => element.Type is "text" or "button" or "toggle" or "slider"))
                if (element.Text is not ("{{title}}" or "{{subtitle}}" or "{{start_label}}" or "{{continue_label}}" or "{{exit_label}}"))
                    Add(ScreenCategory(screen), element.Text, "engine/screens.json · " + screen.Name);
        if (project.Engine.Screens.Find("settings") is { } settings &&
            !settings.Elements.Any(element => element.Action == "text_speed"))
            Add(GameTextCategory.Interface, ScreenLayouts.DialogueSpeedElement().Text, "Настройки игры");
        Add(GameTextCategory.Interface, LanguageSettingLabel, "Настройки игры");
        foreach (string notice in new[]
        {
            "Не удалось сохранить настройки игрока.", "Экран не найден.", "Экран отключён в оформлении.",
            "Экран сохранений отключён.", "Главное меню отключено.", "Загрузка игры…",
            "Выполнение…", "Выполнение остановлено.", "Игра сохранена.", LanguageLoadFailed
        })
            Add(GameTextCategory.Notifications, notice, "Движок KNE");

        var files = ProjectLoader.EnumerateFiles(project.Path, token).Files.Concat(drafts.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => ScriptDocument.IsVisualPath(path) || ScriptDocument.IsLuaPath(path) || ScriptDocument.IsCodePath(path))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(project.Path, file);
            try { ReadFile(file, relative); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
            { skipped.Add(relative + " · " + error.Message); }
        }
        return new(entries.ToArray(), skipped.ToArray());

        void ReadFile(string file, string relative)
        {
            string path = PathSafety.EnsureInside(project.Path, file);
            string source = drafts.TryGetValue(path, out var draft) ? draft : ScriptProtection.ReadText(path);
            if (ScriptDocument.IsVisualPath(file))
            {
                var graph = VisualScript.Parse(source);
                foreach (var node in graph.Nodes)
                {
                    string location = relative + " · " + node.Id;
                    switch (node.Kind)
                    {
                        case ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue or ScriptNodeKind.ReverseDialogue:
                            entries.Add(new(GameTextCategory.Dialogue, node.Text, location));
                            Add(GameTextCategory.Other, node.Speaker, location);
                            break;
                        case ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice:
                            Add(GameTextCategory.Interface, node.Text, location);
                            foreach (var option in node.Options)
                            {
                                Add(GameTextCategory.Interface, option.Text, location);
                                if (option.Lock is { ShowHint: true } rules) Add(GameTextCategory.Interface, rules.Hint, location);
                            }
                            break;
                        case ScriptNodeKind.TextInput:
                            foreach (string text in InputTexts(node.Input ?? new())) Add(GameTextCategory.Interface, text, location);
                            break;
                        case ScriptNodeKind.Title:
                            Add(GameTextCategory.Other, node.Text, location);
                            break;
                        case ScriptNodeKind.RegisterCharacter or ScriptNodeKind.UpdateCharacter:
                            Add(GameTextCategory.Other, node.Character?.Name, location);
                            break;
                        case ScriptNodeKind.Watermark:
                            if (node.SystemArguments?.GetValueOrDefault("text") is { Kind: "string" } watermark)
                                Add(GameTextCategory.Other, watermark.Value, location);
                            break;
                    }
                }
            }
            else
            {
                if (ScriptDocument.IsVisualLuaPath(file)) source = VisualScript.Parse(source).Compile();
                entries.AddRange(ProjectAnalyzer.ReadLuaGameText(source, relative));
            }
        }
    }

    internal static IEnumerable<string> InputTexts(TextInputSettings input) => [input.Prompt, input.Placeholder, input.FailureMessage];
}
