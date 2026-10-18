using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    private void AddChoiceFields(ScriptNode node)
    {
        if (node.Kind is not (ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice)) return;
        if (node.Kind == ScriptNodeKind.TimedChoice)
        {
            var settings = node.ChoiceSettings ?? new ChoiceSettings();
            var seconds = Text("ChoiceSeconds", "Время на ответ, секунды (0–3600, не включая 0)", settings.Seconds.ToString("R", CultureInfo.InvariantCulture), ExtendedProperties);
            var fallback = Text("ChoiceDefaultOption", "Вариант по умолчанию (номер от 1)", settings.DefaultOption.ToString(CultureInfo.InvariantCulture), ExtendedProperties);
            var display = Combo("ChoiceTimerDisplay", "Отображение таймера", new[] { "Полоса наверху экрана", "Числовой таймер" }, settings.TimerDisplay == "numeric" ? 1 : 0, ExtendedProperties);
            extendedEdits.Add(value =>
            {
                if (!double.TryParse(seconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) ||
                    !int.TryParse(fallback.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    throw new InvalidDataException("Таймер: укажите число секунд с точкой и целый номер варианта.");
                var result = new ChoiceSettings { Seconds = duration, DefaultOption = index, TimerDisplay = display.SelectedIndex == 1 ? "numeric" : "bar" };
                result.Validate(value.Options.Count); value.ChoiceSettings = result;
            });
            ExtendedProperties.Children.Add(new TextBlock { Text = "Если вариант по умолчанию заблокирован, движок выберет первый доступный ответ. Таймер приостанавливается вместе с игрой.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
        }
        ExtendedProperties.Children.Add(new TextBlock { Text = "Условия блокировки ответов. После добавления строк примените свойства, чтобы настроить новые варианты.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        for (int i = 0; i < node.Options.Count; i++)
        {
            int index = i; var current = node.Options[i].Lock ?? new OptionLockSettings();
            var panel = new StackPanel();
            var expander = new Expander { Header = $"{i + 1}. {node.Options[i].Text}", Content = panel, Margin = new Thickness(0, 0, 0, 8) };
            ExtendedProperties.Children.Add(expander);
            string prefix = "ChoiceLock" + i;
            var enabled = Check(prefix + "Enabled", "Блокировать при выполнении условия", node.Options[i].Lock is not null, panel);
            var variable = Text(prefix + "Variable", "Переменная state / флаг", current.Variable, panel);
            var comparison = Combo(prefix + "Operator", "Сравнение", Enum.GetValues<ScriptComparison>(), (int)current.Operator, panel);
            var compareVariable = Text(prefix + "CompareVariable", "Сравнить с переменной state (пусто — со значением ниже)", current.CompareVariable, panel);
            var type = Combo(prefix + "ValueType", "Тип значения", new[] { "boolean", "number", "string" }, Array.IndexOf(new[] { "boolean", "number", "string" }, current.ValueType), panel);
            var literal = Text(prefix + "Value", "Значение (true / false для флага)", current.Value, panel);
            var effects = new[] { "dim", "strike", "glitch", "hide", "scramble" };
            var effect = Combo(prefix + "Effect", "Вид блокировки", new[] { "Затемнение", "Зачёркивание", "Глитч", "Скрытие букв", "Циклическое превращение букв" }, Array.IndexOf(effects, current.Effect), panel);
            var showHint = Check(prefix + "ShowHint", "Показать пояснение при наведении", current.ShowHint, panel);
            var hint = Text(prefix + "Hint", "Пояснение", current.Hint, panel);
            var placement = Combo(prefix + "HintPlacement", "Положение пояснения", new[] { "Поверх варианта", "Рядом с вариантом" }, current.HintPlacement == "overlay" ? 0 : 1, panel);
            extendedEdits.Add(value =>
            {
                if (index >= value.Options.Count) return;
                if (enabled.IsChecked != true) { value.Options[index].Lock = null; return; }
                var result = new OptionLockSettings
                {
                    Variable = variable.Text.Trim(), Operator = (ScriptComparison)Math.Max(0, comparison.SelectedIndex),
                    CompareVariable = compareVariable.Text.Trim(), ValueType = (string?)type.SelectedItem ?? "boolean", Value = literal.Text,
                    Effect = effects[Math.Max(0, effect.SelectedIndex)], ShowHint = showHint.IsChecked == true,
                    Hint = hint.Text, HintPlacement = placement.SelectedIndex == 0 ? "overlay" : "side"
                };
                result.Validate(); value.Options[index].Lock = result;
            });
        }

        TextBox Text(string name, string label, string value, Panel panel)
        {
            var field = new TextBox { Name = name, Text = value, Margin = new Thickness(0, 3, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(field);
            field.TextChanged += (_, _) => TrackProperties(); return field;
        }
        ComboBox Combo(string name, string label, Array values, int selected, Panel panel)
        {
            var field = new ComboBox { Name = name, ItemsSource = values, SelectedIndex = Math.Max(0, selected), Margin = new Thickness(0, 3, 0, 8) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(field);
            field.SelectionChanged += (_, _) => TrackProperties(); return field;
        }
        CheckBox Check(string name, string label, bool value, Panel panel)
        {
            var field = new CheckBox { Name = name, Content = label, IsChecked = value, Margin = new Thickness(0, 3, 0, 8) }; panel.Children.Add(field);
            field.Checked += (_, _) => TrackProperties(); field.Unchecked += (_, _) => TrackProperties(); return field;
        }
    }
}
