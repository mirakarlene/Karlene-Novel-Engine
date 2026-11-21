using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    private void AddSystemFields(ScriptNode node)
    {
        if (!SystemBlocks.All.TryGetValue(node.Kind, out var block)) return;
        var arguments = new Dictionary<string, LogicOperand>();
        var panel = new StackPanel();
        ExtendedProperties.Children.Add(panel);
        if (SystemBlocks.Note(node.Kind) is { } note)
            panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) });
        CheckBox? videoSkip = null;
        var videoRegion = new StackPanel();
        foreach (string parameter in block.Parameters)
        {
            var initial = node.SystemArguments?.GetValueOrDefault(parameter) ?? SystemBlocks.DefaultOperand(parameter);
            arguments.Add(parameter, initial);
            if (parameter is "allow_test_resume" or "video_allow_skip")
            {
                var check = new CheckBox
                {
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = parameter == "allow_test_resume"
                        ? "В тестовом билде можно разморозить через Ctrl+Shift+Esc досрочно" : "Разрешить пропуск видео" },
                    IsChecked = initial.Value == "true",
                    Margin = new(0, 3, 0, 6)
                };
                check.Checked += (_, _) => SetFlag(true);
                check.Unchecked += (_, _) => SetFlag(false);
                void SetFlag(bool enabled)
                {
                    arguments[parameter] = new("boolean", enabled ? "true" : "false");
                    TrackProperties();
                }
                if (parameter == "video_allow_skip")
                {
                    videoSkip = check;
                    panel.Children.Add(videoRegion);
                }
                panel.Children.Add(check);
                continue;
            }
            if (parameter == "video_skip_key")
            {
                var field = new StackPanel { Visibility = videoSkip!.IsChecked == true ? Visibility.Visible : Visibility.Collapsed };
                var shortcut = new TextBox { Text = initial.Value, MaxLength = 60, ToolTip = "Escape, Space, Enter или сочетание, например Ctrl+S" };
                shortcut.TextChanged += (_, _) =>
                {
                    arguments[parameter] = new("string", shortcut.Text.Trim());
                    TrackProperties();
                };
                videoSkip.Checked += (_, _) => field.Visibility = Visibility.Visible;
                videoSkip.Unchecked += (_, _) => field.Visibility = Visibility.Collapsed;
                field.Children.Add(new TextBlock { Text = "Клавиша или сочетание для пропуска" });
                field.Children.Add(shortcut);
                panel.Children.Add(field);
                continue;
            }
            string label = parameter switch
            {
                "key" => "Имя переменной", "slot" => "Имя слота", "format" => "Формат даты (например, dd.MM.yyyy)",
                "mode" => "Режим (write / append)", "unit" => "Единица (bytes / kib / mib / gib)",
                "unfreeze" => "Разморозка (left / right / key / timer)", "seconds" => "Длительность, секунды",
                "path" => "Путь", "url" => "URL (http / https)", "text" => "Текст",
                "visible" => "Показывать (true / false)", "position" => "Положение",
                "opacity" => "Непрозрачность (0–100 %)", "action" => "Действие",
                "clock_format" => "Формат времени", "font_path" => "Файл шрифта (.ttf / .otf)",
                "alias" => "Имя для тега {font=...}", "video_path" => "Файл видео относительно проекта",
                "video_x" => "Координата X", "video_y" => "Координата Y",
                "video_width" => "Ширина области", "video_height" => "Высота области", _ => "Значение"
            };
            var target = parameter is "video_x" or "video_y" or "video_width" or "video_height" ? videoRegion : panel;
            target.Children.Add(new TextBlock { Text = label });
            if (parameter == "video_mode")
            {
                var mode = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("fullscreen", "Весь экран"), new("area", "Область экрана")]);
                mode.SelectedValue = initial.Value;
                videoRegion.Visibility = initial.Value == "area" ? Visibility.Visible : Visibility.Collapsed;
                mode.SelectionChanged += (_, _) =>
                {
                    arguments[parameter] = new("string", (string?)mode.SelectedValue ?? "fullscreen");
                    videoRegion.Visibility = (string?)mode.SelectedValue == "area" ? Visibility.Visible : Visibility.Collapsed;
                    TrackProperties();
                };
                panel.Children.Add(mode);
                continue;
            }
            var kind = new ComboBox { ItemsSource = new[] { "string", "number", "boolean", "variable", "list" }, SelectedItem = initial.Kind };
            string[] choices = parameter switch
            {
                "visible" => ["true", "false"],
                "position" => ["top_left", "top_center", "top_right", "center_left", "center", "center_right", "bottom_left", "bottom_center", "bottom_right"],
                "action" => ["show", "hide", "reset"],
                "clock_format" => ["HH:mm:ss", "mm:ss:fff"],
                _ => []
            };
            void Changed(string value)
            {
                arguments[parameter] = new((string?)kind.SelectedItem ?? initial.Kind, value);
                TrackProperties();
            }
            target.Children.Add(kind);
            if (choices.Length == 0)
            {
                var value = new TextBox { Text = initial.Value, Margin = new(0, 3, 0, 8) };
                value.TextChanged += (_, _) => Changed(value.Text);
                kind.SelectionChanged += (_, _) => Changed(value.Text);
                target.Children.Add(value);
            }
            else
            {
                var value = new ComboBox { IsEditable = true, ItemsSource = choices, Text = initial.Value, Margin = new(0, 3, 0, 8) };
                value.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed(value.Text)));
                value.SelectionChanged += (_, _) => Changed(value.SelectedItem as string ?? value.Text);
                kind.SelectionChanged += (_, _) => Changed(value.Text);
                target.Children.Add(value);
            }
        }
        if (block.Result != "")
        {
            panel.Children.Add(new TextBlock { Text = block.Result == "list" ? "Имя списка для результата" : "Записать в state" });
            var result = new TextBox { Text = node.Variable };
            result.TextChanged += (_, _) => TrackProperties();
            panel.Children.Add(result);
            extendedEdits.Add(value => value.Variable = result.Text.Trim());
        }
        extendedEdits.Add(value => value.SystemArguments = new(arguments));
    }
}
