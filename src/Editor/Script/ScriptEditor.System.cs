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
        foreach (string parameter in block.Parameters)
        {
            var initial = node.SystemArguments?.GetValueOrDefault(parameter) ?? SystemBlocks.DefaultOperand(parameter);
            arguments.Add(parameter, initial);
            string label = parameter switch
            {
                "key" => "Имя переменной", "slot" => "Имя слота", "format" => "Формат даты (например, dd.MM.yyyy)",
                "mode" => "Режим (write / append)", "unit" => "Единица (bytes / kib / mib / gib)",
                "unfreeze" => "Разморозка (left / right / key / timer)", "seconds" => "Длительность таймера, секунды",
                "path" => "Путь", "url" => "URL (http / https)", "text" => "Текст",
                "visible" => "Показывать (true / false)", "position" => "Положение",
                "opacity" => "Непрозрачность (0–100 %)", "action" => "Действие",
                "clock_format" => "Формат времени", "font_path" => "Файл шрифта (.ttf / .otf)",
                "alias" => "Имя для тега {font=...}", _ => "Значение"
            };
            panel.Children.Add(new TextBlock { Text = label });
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
            panel.Children.Add(kind);
            if (choices.Length == 0)
            {
                var value = new TextBox { Text = initial.Value, Margin = new(0, 3, 0, 8) };
                value.TextChanged += (_, _) => Changed(value.Text);
                kind.SelectionChanged += (_, _) => Changed(value.Text);
                panel.Children.Add(value);
            }
            else
            {
                var value = new ComboBox { IsEditable = true, ItemsSource = choices, Text = initial.Value, Margin = new(0, 3, 0, 8) };
                value.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed(value.Text)));
                value.SelectionChanged += (_, _) => Changed(value.SelectedItem as string ?? value.Text);
                kind.SelectionChanged += (_, _) => Changed(value.Text);
                panel.Children.Add(value);
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
