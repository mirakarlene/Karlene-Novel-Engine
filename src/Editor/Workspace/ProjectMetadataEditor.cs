using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

/// <summary>Known project fields plus lossless editing of additional project.json sections.</summary>
internal sealed class ProjectMetadataEditor : StackPanel
{
    private JsonObject original;
    private readonly string root;
    private readonly Dictionary<string, TextBox> fields = [];
    internal TextBox Additional { get; } = new() { AcceptsReturn = true, AcceptsTab = true, MinHeight = 160, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static readonly string[] Paths = ["metadata.name", "metadata.author", "metadata.team", "metadata.version", "game_info.description", "metadata.icon", "metadata.default_language", "metadata.created_date", "metadata.kne_version", "entry_points.main_scene", "entry_points.init_script"];
    internal TextBox this[string path] => fields[path];
    internal ProjectMetadataEditor(ProjectInfo project, ProjectBuildSettingsEditor build, TextBox title)
    {
        root = project.Path; original = ProjectBuildMetadata.Read(root).Configuration;
        var remaining = (JsonObject)original.DeepClone();
        var labels = new[] { "Название проекта", "Автор / никнейм", "Команда разработчиков", "Версия", "Описание", "Иконка проекта (.ico / .png)", "Язык по умолчанию", "Дата создания", "Версия KNE", "Начальная сцена", "Скрипт инициализации" };
        for (int i = 0; i < Paths.Length; i++)
        {
            string path = Paths[i]; var parts = path.Split('.');
            TextBox input = path switch { "metadata.name" => title, "metadata.author" => build.Author, "metadata.team" => build.Team, "metadata.version" => build.Version, _ => new TextBox() };
            input.IsReadOnly = false; input.Width = double.NaN; input.FontSize = 12; input.FontWeight = FontWeights.Normal; input.Padding = new(6); input.Margin = new(0, 3, 0, 10);
            if (path is not ("metadata.author" or "metadata.team" or "metadata.version")) input.Text = original[parts[0]]?[parts[1]]?.GetValue<string>() ?? (path == "metadata.name" ? project.Title : path == "metadata.default_language" ? "ru" : "");
            if (path == "game_info.description") { input.AcceptsReturn = true; input.MinHeight = 65; input.TextWrapping = TextWrapping.Wrap; }
            fields.Add(path, input); Children.Add(new TextBlock { Text = labels[i] });
            if (path == "metadata.icon")
            {
                var row = new DockPanel(); var browse = new Button { Content = "Выбрать…", Margin = new(8, 0, 0, 10), Padding = new(10, 5, 10, 5) }; DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input); Children.Add(row);
                browse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Иконка (*.ico;*.png)|*.ico;*.png", InitialDirectory = root }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = Path.GetRelativePath(root, dialog.FileName).Replace('\\', '/'); };
            }
            else Children.Add(input);
            (remaining[parts[0]] as JsonObject)?.Remove(parts[1]);
        }
        Children.Add(new TextBlock { Text = "Остальные параметры project.json (JSON)", FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 5) });
        Children.Add(new TextBlock { Text = "Вложенные секции и дополнительные поля сохраняются. Пути задаются относительно проекта; иконку и музыку поместите в папку проекта.", TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new(0, 0, 0, 8) });
        Additional.Text = remaining.ToJsonString(ProjectConfiguration.JsonOptions); Children.Add(Additional);
    }
    internal JsonObject Draft()
    {
        JsonObject draft;
        try { draft = JsonNode.Parse(Additional.Text) as JsonObject ?? throw new InvalidDataException("Дополнительные параметры должны быть JSON-объектом."); }
        catch (JsonException error) { throw new InvalidDataException("Ошибка JSON дополнительных параметров: " + error.Message, error); }
        foreach (string path in Paths)
        {
            var parts = path.Split('.'); if (draft[parts[0]] is not JsonObject) draft[parts[0]] = new JsonObject();
            ((JsonObject)draft[parts[0]]!)[parts[1]] = fields[path].Text.Trim();
        }
        draft["metadata"]!["name"] = ProjectMaintenance.ValidateName(fields["metadata.name"].Text);
        if (draft["game_info"] is not JsonObject) draft["game_info"] = new JsonObject();
        draft["game_info"]!["title"] = fields["metadata.name"].Text.Trim();
        if (draft["name"] != null) draft["name"] = fields["metadata.name"].Text.Trim();
        string icon = fields["metadata.icon"].Text.Trim();
        if (icon != "" && (!File.Exists(PathSafety.ResolveRelative(root, icon)) || Path.GetExtension(icon).ToLowerInvariant() is not (".ico" or ".png"))) throw new InvalidDataException("Иконка — существующий .ico или .png внутри проекта.");
        string language = fields["metadata.default_language"].Text.Trim();
        try { if (language == "") throw new ArgumentException(); _ = System.Globalization.CultureInfo.GetCultureInfo(language); } catch (ArgumentException) { throw new InvalidDataException("Укажите код языка, например ru или en-US."); }
        foreach (string path in new[] { "entry_points.main_scene", "entry_points.init_script" })
            if (fields[path].Text.Trim() is { Length: > 0 } entry && !File.Exists(PathSafety.ResolveRelative(root, entry))) throw new InvalidDataException("Файл точки входа не найден: " + entry);
        _ = JsonSerializer.Deserialize<ProjectConfiguration>(draft, ProjectConfiguration.JsonOptions) ?? throw new InvalidDataException("Пустая конфигурация проекта.");
        return draft;
    }
    internal JsonObject Merge(JsonObject current, JsonObject draft) => (JsonObject)MergeNode(original, draft, current, "project.json")!;
    private static JsonNode? MergeNode(JsonNode? before, JsonNode? draft, JsonNode? current, string path)
    {
        if (JsonNode.DeepEquals(before, draft)) return current?.DeepClone();
        if (before is JsonObject a && draft is JsonObject b && current is JsonObject c)
        {
            var result = (JsonObject)c.DeepClone();
            foreach (string key in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
            {
                if (a.ContainsKey(key) == b.ContainsKey(key) && JsonNode.DeepEquals(a[key], b[key])) continue;
                if (!b.ContainsKey(key)) { if (!JsonNode.DeepEquals(a[key], c[key])) throw new IOException("Поле изменено вне окна: " + path + "." + key); result.Remove(key); }
                else result[key] = MergeNode(a[key], b[key], c[key], path + "." + key);
            }
            return result;
        }
        if (!JsonNode.DeepEquals(before, current)) throw new IOException("Поле изменено вне окна параметров: " + path);
        return draft?.DeepClone();
    }
    internal void Saved(JsonObject value) => original = (JsonObject)value.DeepClone();
}
