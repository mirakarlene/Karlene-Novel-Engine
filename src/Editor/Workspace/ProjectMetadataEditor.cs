using System.Diagnostics;
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
    internal TextBox Additional { get; } = new() { AcceptsReturn = true, AcceptsTab = true, Height = 42, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static readonly string[] Paths = ["metadata.name", "metadata.author", "metadata.team", "metadata.version", "game_info.description", "metadata.icon", "metadata.default_language", "metadata.created_date", "metadata.kne_version", "entry_points.main_scene", "entry_points.init_script"];
    internal TextBox this[string path] => fields[path];
    internal Button OpenExternalEditor { get; } = new() { Content = "Открыть во внешнем редакторе", Padding = new(6, 2, 6, 2) };
    internal ProjectLifecycleEditor Lifecycle { get; }
    internal ProjectMetadataEditor(ProjectInfo project, ProjectBuildSettingsEditor build, TextBox title)
    {
        root = project.Path; original = ProjectBuildMetadata.Read(root).Configuration;
        var remaining = (JsonObject)original.DeepClone();
        Lifecycle = new(root, original["entry_points"] as JsonObject ?? new JsonObject());
        ProjectLifecycleEditor.Mask(remaining["entry_points"] as JsonObject);
        var labels = new[] { "Название проекта", "Автор / никнейм", "Команда разработчиков", "Версия", "Описание", "Иконка проекта (.ico / .png)", "Язык по умолчанию", "Дата создания", "Версия KNE", "Начальная сцена", "Скрипт инициализации" };
        for (int i = 0; i < Paths.Length; i++)
        {
            string path = Paths[i]; var parts = path.Split('.');
            TextBox input = path switch { "metadata.name" => title, "metadata.author" => build.Author, "metadata.team" => build.Team, "metadata.version" => build.Version, _ => new TextBox() };
            StyleField(input);
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
        Additional.Text = remaining.ToJsonString(ProjectConfiguration.JsonOptions);
        var items = Children.Cast<UIElement>().ToArray(); Children.Clear();
        var metadata = new StackPanel(); var technicalLeft = new StackPanel(); var technicalRight = new StackPanel();
        for (int i = 0; i < Paths.Length; i++)
        {
            var column = i < 7 ? metadata : i < 9 ? technicalLeft : technicalRight;
            column.Children.Add(items[i * 2]); column.Children.Add(items[i * 2 + 1]);
        }
        ParameterLayout.Compact(metadata);
        var description = fields["game_info.description"];
        description.Height = 115;
        description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Children.Add(metadata);
        Children.Add(build.UseProjectNameForBuild);
        AddStartupFields(technicalLeft, technicalRight, Lifecycle.StartupFields, Lifecycle.EventFields);
        var header = new DockPanel { Margin = new(0, 5, 0, 3) };
        DockPanel.SetDock(OpenExternalEditor, Dock.Right);
        header.Children.Add(OpenExternalEditor);
        header.Children.Add(new TextBlock { Text = "Остальные параметры project.json", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(header);
        StyleField(Additional);
        Additional.Padding = new(4, 2, 4, 2);
        Additional.Margin = new(0);
        Additional.ToolTip = "Вложенные секции и дополнительные поля project.json. Пути задаются относительно проекта.";
        Children.Add(Additional);
    }

    private void AddStartupFields(params StackPanel[] groups)
    {
        var fields = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new(0, 6, -6, 0) };
        foreach (var group in groups)
        {
            var controls = group.Children.Cast<FrameworkElement>().ToArray();
            group.Children.Clear();
            for (int index = 0; index < controls.Length; index += 2)
            {
                fields.Children.Add(CompactEntry((TextBlock)controls[index], controls[index + 1]));
            }
        }
        var timing = Lifecycle.TimingFields.Children.Cast<FrameworkElement>().ToArray();
        Lifecycle.TimingFields.Children.Clear();
        fields.Children.Add(new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 2,
            Children = { CompactEntry((TextBlock)timing[0], timing[1]), CompactEntry((TextBlock)timing[2], timing[3]) }
        });
        Children.Add(fields);
    }

    private static StackPanel CompactEntry(TextBlock label, FrameworkElement input)
    {
        label.FontSize = 11;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.ToolTip ??= label.Text;
        input.Margin = new(0);
        var text = input is DockPanel row ? row.Children.OfType<TextBox>().Single() : (TextBox)input;
        text.Margin = new(0);
        text.Padding = new(4, 2, 4, 2);
        return new StackPanel { Margin = new(0, 0, 6, 3), Children = { label, input } };
    }

    internal ProcessStartInfo ExternalEditorStartInfo(string? editorPath) =>
        EditorLauncher.CreateStartInfo(editorPath, PathSafety.ResolveRelative(root, "project.json"));
    internal static void StyleField(TextBox input)
    {
        input.IsReadOnly = false;
        input.Width = double.NaN;
        input.FontSize = 12;
        input.FontWeight = FontWeights.Normal;
        input.Padding = new(6);
        input.Margin = new(0, 3, 0, 10);
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
        Lifecycle.Write((JsonObject)draft["entry_points"]!);
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
