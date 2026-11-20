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
            var initial = node.SystemArguments?.GetValueOrDefault(parameter)
                ?? new LogicOperand(parameter == "seconds" ? "number" : "string", SystemBlocks.Default(parameter));
            arguments.Add(parameter, initial);
            string label = parameter switch
            {
                "key" => "Имя переменной", "slot" => "Имя слота", "format" => "Формат даты (например, dd.MM.yyyy)",
                "mode" => "Режим (write / append)", "unit" => "Единица (bytes / kib / mib / gib)",
                "unfreeze" => "Разморозка (left / right / key / timer)", "seconds" => "Длительность таймера, секунды",
                "path" => "Путь", "url" => "URL (http / https)", "text" => "Текст", _ => "Значение"
            };
            panel.Children.Add(new TextBlock { Text = label });
            var kind = new ComboBox { ItemsSource = new[] { "string", "number", "boolean", "variable", "list" }, SelectedItem = initial.Kind };
            var value = new TextBox { Text = initial.Value, Margin = new(0, 3, 0, 8) };
            void Changed()
            {
                arguments[parameter] = new((string?)kind.SelectedItem ?? initial.Kind, value.Text);
                TrackProperties();
            }
            kind.SelectionChanged += (_, _) => Changed();
            value.TextChanged += (_, _) => Changed();
            panel.Children.Add(kind);
            panel.Children.Add(value);
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
