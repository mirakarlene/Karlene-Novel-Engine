using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

internal sealed class ProjectLifecycleEditor
{
    private readonly string root;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly TextBox splashSeconds = new(), idleSeconds = new();
    internal StackPanel StartupFields { get; } = new();
    internal StackPanel EventFields { get; } = new();
    internal StackPanel TimingFields { get; } = new();
    private static readonly string[] Keys = ["pre_menu_scene", "splash_image", "idle_script", "save_script", "exit_script", "menu_script", "error_script", "splash_seconds", "idle_seconds"];
    internal IEnumerable<TextBox> Inputs => fields.Values.Append(splashSeconds).Append(idleSeconds);

    internal ProjectLifecycleEditor(string projectRoot, JsonObject entries)
    {
        root = projectRoot;
        var startup = StartupFields;
        AddPath(startup, entries, "pre_menu_scene", "Сцена до главного меню", "Сцены (*.knegraph;*.krl;*.lua;*.vlua)|*.knegraph;*.krl;*.lua;*.vlua");
        AddPath(startup, entries, "splash_image", "Сплэш-скрин", "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff");
        AddDuration(TimingFields, splashSeconds, entries, "splash_seconds", "Показ, с", "Время показа заставки в секундах", ProjectLifecycle.DefaultSplashSeconds);
        AddScript(startup, entries, "menu_script", "Скрипт для главного меню");
        var runtime = EventFields;
        AddScript(runtime, entries, "idle_script", "Скрипт при AFK");
        AddDuration(TimingFields, idleSeconds, entries, "idle_seconds", "AFK, с", "Время без ввода в секундах", ProjectLifecycle.DefaultIdleSeconds);
        AddScript(runtime, entries, "save_script", "Скрипт при сохранении");
        AddScript(runtime, entries, "exit_script", "Скрипт при выходе");
        AddScript(runtime, entries, "error_script", "Скрипт при ошибке");
    }

    internal static void Mask(JsonObject? entries)
    {
        if (entries != null) foreach (string key in Keys) entries.Remove(key);
    }

    internal void Write(JsonObject entries)
    {
        foreach (var field in fields)
        {
            string path = field.Value.Text.Trim();
            if (path.Length == 0) { entries.Remove(field.Key); continue; }
            string full = PathSafety.ResolveRelative(root, path);
            if (!File.Exists(full)) throw new InvalidDataException("Файл не найден: " + path);
            bool valid = field.Key switch
            {
                "splash_image" => ProjectLifecycle.SupportsImage(full),
                "pre_menu_scene" => ScriptDocument.IsExecutablePath(full),
                _ => ScriptDocument.IsCodePath(full) || ScriptDocument.IsVisualLuaPath(full)
            };
            if (!valid) throw new InvalidDataException("Недопустимый формат файла: " + path);
            entries[field.Key] = path.Replace('\\', '/');
        }
        WriteDuration("splash_seconds", "splash_image", splashSeconds, ProjectLifecycle.DefaultSplashSeconds, 60);
        WriteDuration("idle_seconds", "idle_script", idleSeconds, ProjectLifecycle.DefaultIdleSeconds, 86400);

        void WriteDuration(string key, string required, TextBox input, double fallback, double maximum)
        {
            if (entries[required] == null) { entries.Remove(key); return; }
            string text = input.Text.Trim().Replace(',', '.');
            double? value = text.Length == 0 ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new InvalidDataException("Укажите время в секундах.");
            entries[key] = ProjectLifecycle.Duration(value, fallback, maximum);
        }
    }

    private void AddScript(Panel panel, JsonObject entries, string key, string title) =>
        AddPath(panel, entries, key, title, "Скрипты (*.lua;*.vlua;*.krl)|*.lua;*.vlua;*.krl");

    private void AddPath(Panel panel, JsonObject entries, string key, string title, string filter)
    {
        var input = new TextBox { Text = entries[key]?.GetValue<string>() ?? "" };
        ProjectMetadataEditor.StyleField(input);
        fields.Add(key, input);
        var browse = new Button { Content = "…", Width = 24, Margin = new(4, 0, 0, 0), Padding = new(0), ToolTip = title };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(row);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = filter, InitialDirectory = root, Title = title };
            if (dialog.ShowDialog(Window.GetWindow(input)) == true) input.Text = Path.GetRelativePath(root, dialog.FileName).Replace('\\', '/');
        };
    }

    private static void AddDuration(Panel panel, TextBox input, JsonObject entries, string key, string title, string description, double fallback)
    {
        input.Text = (entries[key]?.GetValue<double>() ?? fallback).ToString(CultureInfo.InvariantCulture);
        ProjectMetadataEditor.StyleField(input);
        input.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(input, description);
        panel.Children.Add(new TextBlock { Text = title, ToolTip = description }); panel.Children.Add(input);
    }

}
