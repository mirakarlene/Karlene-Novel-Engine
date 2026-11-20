using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

internal sealed class ProjectLifecycleEditor : StackPanel
{
    private readonly string root;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly TextBox splashSeconds = new(), idleSeconds = new();
    private static readonly string[] Keys = ["pre_menu_scene", "splash_image", "idle_script", "save_script", "exit_script", "menu_script", "error_script", "splash_seconds", "idle_seconds"];
    internal IEnumerable<TextBox> Inputs => fields.Values.Append(splashSeconds).Append(idleSeconds);

    internal ProjectLifecycleEditor(string projectRoot, JsonObject entries)
    {
        root = projectRoot;
        var startup = Heading("Запуск и события");
        AddPath(startup, entries, "pre_menu_scene", "Сцена до главного меню", "Сцены (*.knegraph;*.krl;*.lua;*.vlua)|*.knegraph;*.krl;*.lua;*.vlua");
        AddPath(startup, entries, "splash_image", "Сплэш-скрин", "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff");
        AddDuration(startup, splashSeconds, entries, "splash_seconds", "Время показа, с", ProjectLifecycle.DefaultSplashSeconds);
        AddScript(startup, entries, "menu_script", "Скрипт для главного меню");
        var runtime = Heading("Во время игры");
        AddScript(runtime, entries, "idle_script", "Скрипт при AFK");
        AddDuration(runtime, idleSeconds, entries, "idle_seconds", "Время без ввода, с", ProjectLifecycle.DefaultIdleSeconds);
        AddScript(runtime, entries, "save_script", "Скрипт при сохранении");
        AddScript(runtime, entries, "exit_script", "Скрипт при выходе");
        AddScript(runtime, entries, "error_script", "Скрипт при ошибке");
        Children.Add(ParameterLayout.Columns(startup, runtime));
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
        var input = new TextBox { Text = entries[key]?.GetValue<string>() ?? "", Padding = new(5), Margin = new(0, 4, 0, 8) };
        fields.Add(key, input);
        var browse = new Button { Content = "…", Width = 30, Margin = new(5, 4, 0, 8), ToolTip = title };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(input);
        panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(row);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = filter, InitialDirectory = root, Title = title };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = Path.GetRelativePath(root, dialog.FileName).Replace('\\', '/');
        };
    }

    private static void AddDuration(Panel panel, TextBox input, JsonObject entries, string key, string title, double fallback)
    {
        input.Text = (entries[key]?.GetValue<double>() ?? fallback).ToString(CultureInfo.InvariantCulture);
        input.Padding = new(5); input.Width = 100; input.HorizontalAlignment = HorizontalAlignment.Left; input.Margin = new(0, 4, 0, 8);
        panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(input);
    }

    private static StackPanel Heading(string title)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new(0, 0, 0, 9) });
        return panel;
    }
}
