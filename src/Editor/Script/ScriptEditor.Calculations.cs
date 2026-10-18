using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KNE;

public partial class ScriptEditor
{
    private sealed class WeightRow { public string Value { get; set; } = "0"; public string Weight { get; set; } = "1"; }
    private sealed class StepRow { public string Name { get; set; } = "result"; public string Expression { get; set; } = "0"; }

    /// <summary>Replaces only a formula recipe; output variable, connections and identity stay local.</summary>
    public void ApplyFormulaTemplate(CalculationSettings settings)
    {
        if (Selected is not { Kind: ScriptNodeKind.Formula } node) throw new InvalidOperationException("Выберите блок «Формула».");
        var copy = FormulaLibrary.Decode(FormulaLibrary.Encode(settings));
        string result = ExtendedProperties.Children.OfType<TextBox>().FirstOrDefault(input => input.Name == "CalculationResultInput")?.Text.Trim() ?? node.Variable;
        _ = CalculationCompiler.Compile(ScriptNodeKind.Formula, copy, result);
        Remember(); node.Calculation = copy; node.Variable = result; propertyDraft = false; Repaint();
    }

    private void AddCalculationFields(ScriptNode node)
    {
        if (!CalculationCompiler.IsCalculation(node.Kind)) return;
        var settings = node.Calculation ?? new CalculationSettings();
        Field("CalculationResultInput", "Записать результат в state", node.Variable, (value, text) => value.Variable = text.Trim());
        void Operand(string name, string label, string value, Func<CalculationSettings, string, CalculationSettings> update) =>
            Field(name, label, value, (node, text) => node.Calculation = update(node.Calculation ?? settings, text));
        if (node.Kind is not (ScriptNodeKind.Constant or ScriptNodeKind.WeightedRandom or ScriptNodeKind.Formula))
            Operand("CalculationAInput", node.Kind == ScriptNodeKind.Trigonometry ? "A · угол в радианах" : "A · число или выражение", settings.A, (s, v) => s with { A = v });
        if (node.Kind is ScriptNodeKind.Arithmetic or ScriptNodeKind.Modulo or ScriptNodeKind.Pow or ScriptNodeKind.Clamp or ScriptNodeKind.Lerp or ScriptNodeKind.Remap)
            Operand("CalculationBInput", node.Kind is ScriptNodeKind.Clamp or ScriptNodeKind.Remap ? "B · исходный минимум" : "B · число или выражение", settings.B, (s, v) => s with { B = v });
        if (node.Kind is ScriptNodeKind.Clamp or ScriptNodeKind.Remap)
            Operand("CalculationCInput", "C · исходный максимум", settings.C, (s, v) => s with { C = v });
        if (node.Kind == ScriptNodeKind.Remap)
            Operand("CalculationDInput", "D · новый минимум", settings.D, (s, v) => s with { D = v });
        if (node.Kind is ScriptNodeKind.Lerp or ScriptNodeKind.Remap)
            Operand("CalculationTInput", node.Kind == ScriptNodeKind.Lerp ? "t · смешивание от 0 до 1" : "Новый максимум", settings.T, (s, v) => s with { T = v });
        if (node.Kind == ScriptNodeKind.Arithmetic) Choose("CalculationOperatorInput", "Операция", settings.Operator, ["+", "-", "*", "/"], (s, v) => s with { Operator = v });
        if (node.Kind == ScriptNodeKind.Trigonometry) Choose("CalculationFunctionInput", "Функция", settings.Function, ["sin", "cos", "tan"], (s, v) => s with { Function = v });
        if (node.Kind == ScriptNodeKind.Constant) Choose("CalculationConstantInput", "Константа: π / τ / e / φ / π/2 / √2", settings.Constant, CalculationCompiler.Constants.ToArray(), (s, v) => s with { Constant = v });
        if (node.Kind == ScriptNodeKind.WeightedRandom)
        {
            Label("Значения и относительные веса (точка — разделитель)");
            var rows = new ObservableCollection<WeightRow>(settings.Weights.Select(row => new WeightRow { Value = row.Value, Weight = row.Weight.ToString("R", CultureInfo.InvariantCulture) }));
            var table = Table("CalculationWeightsInput", rows, ("Значение", "Value"), ("Вес", "Weight"));
            RowButtons(table, () => { if (rows.Count < 100) rows.Add(new()); }, () => { if (table.SelectedItem is WeightRow row) rows.Remove(row); });
            extendedEdits.Add(value =>
            {
                Commit(table);
                value.Calculation = (value.Calculation ?? settings) with { Weights = rows.Select(row => new WeightedNumber(row.Value, double.TryParse(row.Weight, NumberStyles.Float, CultureInfo.InvariantCulture, out double weight) ? weight : throw new ArgumentException("Вес должен быть конечным неотрицательным числом."))).ToList() };
            });
        }
        if (node.Kind == ScriptNodeKind.Formula)
        {
            Operand("FormulaNameInput", "Название формулы", settings.FormulaName, (s, v) => s with { FormulaName = v.Trim() });
            Label("Шаги по порядку · последний — результат");
            var rows = new ObservableCollection<StepRow>(settings.Steps.Select(step => new StepRow { Name = step.Name, Expression = step.Expression }));
            var table = Table("FormulaStepsInput", rows, ("Имя", "Name"), ("Выражение", "Expression"));
            RowButtons(table, () => { if (rows.Count < 64) { string name = "step" + (rows.Count + 1); while (rows.Any(row => row.Name == name)) name += "_"; rows.Add(new() { Name = name }); } }, () => { if (table.SelectedItem is StepRow row) rows.Remove(row); });
            extendedEdits.Add(value => { Commit(table); value.Calculation = (value.Calculation ?? settings) with { Steps = rows.Select(row => new FormulaStep(row.Name.Trim(), row.Expression)).ToList() }; });
            var copy = new Button { Name = "CopyFormulaButton", Content = "Скопировать формулу", Margin = new Thickness(0, 4, 0, 4) };
            copy.Click += (_, _) => Guard(() => { ApplyProperties(); Clipboard.SetText(FormulaLibrary.Encode(Selected!.Calculation!)); EditorStatus.Text = "Формула скопирована. Выберите другой блок «Формула» и вставьте её."; });
            var paste = new Button { Name = "PasteFormulaButton", Content = "Вставить формулу", Margin = new Thickness(0, 0, 0, 8) };
            paste.Click += (_, _) => Guard(() => ApplyFormulaTemplate(FormulaLibrary.Decode(Clipboard.GetText())));
            ExtendedProperties.Children.Add(copy); ExtendedProperties.Children.Add(paste);
            var templates = new ComboBox { Name = "FormulaTemplatesInput", DisplayMemberPath = "Label", Margin = new Thickness(0, 4, 0, 8) };
            var load = new Button { Name = "LoadFormulaTemplatesButton", Content = "Найти формулы в проекте", Margin = new Thickness(0, 0, 0, 8) };
            load.Click += async (_, _) =>
            {
                try
                {
                    load.IsEnabled = false; var saved = await FormulaLibrary.LoadAsync(projectRoot);
                    if (Selected?.Id != node.Id) return;
                    var drafts = Document!.Graph!.Nodes.Where(item => item.Kind == ScriptNodeKind.Formula && item.Calculation != null)
                        .Select(item => new FormulaTemplate(item.Calculation!.FormulaName + " · открытая схема · " + item.Id, FormulaLibrary.Decode(FormulaLibrary.Encode(item.Calculation))));
                    templates.ItemsSource = drafts.Concat(saved).ToArray(); EditorStatus.Text = "Выберите формулу: вставится независимая копия шагов.";
                }
                catch (Exception error) { EditorStatus.Text = "Формулы: " + error.Message; }
                finally { load.IsEnabled = true; }
            };
            templates.SelectionChanged += (_, _) => { if (templates.SelectedItem is FormulaTemplate template) Guard(() => ApplyFormulaTemplate(template.Settings)); };
            ExtendedProperties.Children.Add(load); ExtendedProperties.Children.Add(templates);
        }
        Label("Выражения: числа, переменные state, + − * / % ^, скобки, pi и функции sqrt, sin, clamp, lerp и другие. Подробности — в HTML-справке.");

        void Label(string text) => ExtendedProperties.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4) });
        void Field(string name, string label, string current, Action<ScriptNode, string> assign)
        {
            Label(label); var input = new TextBox { Name = name, Text = current, Margin = new Thickness(0, 0, 0, 8) }; ExtendedProperties.Children.Add(input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, input.Text));
        }
        void Choose(string name, string label, string current, string[] options, Func<CalculationSettings, string, CalculationSettings> assign)
        {
            Label(label); var input = new ComboBox { Name = name, ItemsSource = options, SelectedItem = current, Margin = new Thickness(0, 0, 0, 8) }; ExtendedProperties.Children.Add(input);
            input.SelectionChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => value.Calculation = assign(value.Calculation ?? settings, (string?)input.SelectedItem ?? current));
        }
        DataGrid Table(string name, object rows, params (string Header, string Path)[] columns)
        {
            var table = new DataGrid { Name = name, ItemsSource = (System.Collections.IEnumerable)rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 100, MaxHeight = 240, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var column in columns) table.Columns.Add(new DataGridTextColumn { Header = column.Header, Binding = new Binding(column.Path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            table.CellEditEnding += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(table); return table;
        }
        void RowButtons(DataGrid table, Action add, Action remove)
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; var plus = new Button { Content = "+ Строка", Margin = new Thickness(0, 0, 6, 0) }; var minus = new Button { Content = "− Строка" };
            plus.Click += (_, _) => { Commit(table); add(); TrackProperties(); }; minus.Click += (_, _) => { Commit(table); remove(); TrackProperties(); };
            buttons.Children.Add(plus); buttons.Children.Add(minus); ExtendedProperties.Children.Add(buttons);
        }
    }
    private static void Commit(DataGrid table) { table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true); }
}
