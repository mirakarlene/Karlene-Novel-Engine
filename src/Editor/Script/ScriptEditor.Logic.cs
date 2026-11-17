using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    private void AddLogicFields(ScriptNode node)
    {
        if (!LogicCompiler.IsLogic(node.Kind) && node.Kind != ScriptNodeKind.Random) return;
        var s = JsonSerializer.Deserialize<LogicSettings>(JsonSerializer.Serialize(node.Logic ?? new() { List = node.Kind == ScriptNodeKind.Random ? "" : "inventory" }))!;
        var panel = new StackPanel(); ExtendedProperties.Children.Add(panel);
        bool list = node.Kind >= ScriptNodeKind.CreateList && node.Kind <= ScriptNodeKind.ListRandom || node.Kind == ScriptNodeKind.Random;
        if (list) Text("LogicListInput", node.Kind == ScriptNodeKind.Random ? "Список (пусто — числовой диапазон)" : "Имя массива", s.List, text => s.List = text.Trim());
        if (node.Kind is ScriptNodeKind.ListMerge or ScriptNodeKind.ListCopy) Text("LogicOtherListInput", node.Kind == ScriptNodeKind.ListCopy ? "Куда скопировать" : "Какой массив добавить", s.OtherList, text => s.OtherList = text.Trim());
        if (node.Kind == ScriptNodeKind.CreateList) Text("LogicInitialValuesInput", "Начальные значения (JSON-массив)", s.InitialValues, text => s.InitialValues = text, multiline: true);
        if (node.Kind is ScriptNodeKind.ListGet or ScriptNodeKind.ListSet or ScriptNodeKind.ListInsert or ScriptNodeKind.ListRemoveAt) Operand("LogicIndex", "Индекс (с 0)", s.Index, value => s.Index = value);
        if (node.Kind is ScriptNodeKind.ListAdd or ScriptNodeKind.ListRemove or ScriptNodeKind.ListSet or ScriptNodeKind.ListInsert or ScriptNodeKind.ListContains or ScriptNodeKind.ListIndexOf or ScriptNodeKind.StringConcat or ScriptNodeKind.StringLength or ScriptNodeKind.StringContains or ScriptNodeKind.StringReplace or ScriptNodeKind.StringUpper or ScriptNodeKind.StringLower or ScriptNodeKind.ConvertType or ScriptNodeKind.CopyVariable)
            Operand("LogicA", node.Kind == ScriptNodeKind.CopyVariable ? "Исходная переменная (тип: variable)" : "Значение A", s.A, value => s.A = value);
        if (node.Kind is ScriptNodeKind.StringConcat or ScriptNodeKind.StringContains or ScriptNodeKind.StringReplace) Operand("LogicB", node.Kind == ScriptNodeKind.StringReplace ? "Что заменить" : "Значение B", s.B, value => s.B = value);
        if (node.Kind == ScriptNodeKind.StringReplace) Operand("LogicC", "На что заменить", s.C, value => s.C = value);
        if (node.Kind == ScriptNodeKind.ConvertType) Choice("LogicConversionInput", "Преобразовать в", s.Conversion, ["number", "string", "boolean"], value => s.Conversion = value);
        if (node.Kind == ScriptNodeKind.CompoundCondition)
        {
            var tree = new StackPanel(); panel.Children.Add(tree); RenderCondition(tree, s.Condition);
        }
        else if (node.Kind is ScriptNodeKind.ListLength or ScriptNodeKind.ListGet or ScriptNodeKind.ListContains or ScriptNodeKind.ListIndexOf or ScriptNodeKind.ListIsEmpty or ScriptNodeKind.ListExists or ScriptNodeKind.ListRandom or ScriptNodeKind.StringConcat or ScriptNodeKind.StringLength or ScriptNodeKind.StringContains or ScriptNodeKind.StringReplace or ScriptNodeKind.StringUpper or ScriptNodeKind.StringLower or ScriptNodeKind.ConvertType or ScriptNodeKind.CopyVariable or ScriptNodeKind.ResetVariable)
        {
            var result = Text("LogicResultInput", node.Kind == ScriptNodeKind.ResetVariable ? "Переменная для сброса" : "Записать в state", node.Variable, _ => { });
            extendedEdits.Add(value => value.Variable = result.Text.Trim());
        }
        extendedEdits.Add(value => value.Logic = JsonSerializer.Deserialize<LogicSettings>(JsonSerializer.Serialize(s)));
        TextBox Text(string name, string label, string current, Action<string> assign, bool multiline = false)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Name = name, Text = current, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new(0, 3, 0, 8) };
            panel.Children.Add(input); input.TextChanged += (_, _) => { assign(input.Text); TrackProperties(); }; return input;
        }
        void Choice(string name, string label, string current, string[] values, Action<string> assign)
        {
            panel.Children.Add(new TextBlock { Text = label }); var input = new ComboBox { Name = name, ItemsSource = values, SelectedItem = current, Margin = new(0, 3, 0, 8) }; panel.Children.Add(input);
            input.SelectionChanged += (_, _) => { assign((string?)input.SelectedItem ?? current); TrackProperties(); };
        }
        void Operand(string prefix, string label, LogicOperand operand, Action<LogicOperand> assign)
        {
            string kind = operand.Kind, text = operand.Value;
            Choice(prefix + "KindInput", label + " · тип", kind, ["string", "number", "boolean", "variable", "list", "null"], value => { kind = value; assign(new(kind, text)); });
            Text(prefix + "ValueInput", "Литерал / имя переменной или массива", text, value => { text = value; assign(new(kind, text)); });
        }
        void RenderCondition(StackPanel host, LogicCondition condition)
        {
            host.Children.Clear(); var type = new ComboBox { ItemsSource = new[] { "compare", "and", "or", "not" }, SelectedItem = condition.Operator, Margin = new(0, 4, 0, 4) }; host.Children.Add(type);
            type.SelectionChanged += (_, _) =>
            {
                condition.Operator = (string)type.SelectedItem;
                if (condition.Operator != "compare" && condition.Children.Count == 0) condition.Children.Add(new());
                if (condition.Operator == "not" && condition.Children.Count > 1) condition.Children = [new() { Operator = "and", Children = condition.Children }];
                TrackProperties(); RenderCondition(host, condition);
            };
            if (condition.Operator == "compare")
            {
                var saved = panel; panel = host;
                Operand("ConditionA", "A", condition.A, value => condition.A = value);
                Choice("ConditionComparisonInput", "Сравнение", condition.Comparison, ["==", "~=", "<", ">", "<=", ">="], value => condition.Comparison = value);
                Operand("ConditionB", "B", condition.B, value => condition.B = value); panel = saved;
            }
            else
            {
                foreach (var child in condition.Children.ToArray())
                {
                    var nested = new StackPanel(); var card = new Border { BorderThickness = new(1), Padding = new(8), Margin = new(6, 3, 0, 3), Child = nested }; card.SetResourceReference(Border.BorderBrushProperty, "Editor.Border"); host.Children.Add(card); RenderCondition(nested, child);
                    if (condition.Operator != "not") { var remove = new Button { Content = "Удалить условие" }; remove.Click += (_, _) => { condition.Children.Remove(child); TrackProperties(); RenderCondition(host, condition); }; nested.Children.Add(remove); }
                }
                if (condition.Operator != "not") { var add = new Button { Content = "+ Условие / группа" }; add.Click += (_, _) => { condition.Children.Add(new()); TrackProperties(); RenderCondition(host, condition); }; host.Children.Add(add); }
            }
        }
    }
}
