using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    private void AddTextInputFields(ScriptNode node)
    {
        if (node.Kind != ScriptNodeKind.TextInput) return;
        var input = node.Input ?? new TextInputSettings();
        Text("InputVariable", "Ответ в переменной state (латиница, цифры и _)", input.Variable, (s, text) => s with { Variable = text.Trim() });
        Text("InputPrompt", "Текст перед полем ввода", input.Prompt, (s, text) => s with { Prompt = text });
        Text("InputMaximumLength", "Максимальная длина (1–256)", input.MaximumLength.ToString(CultureInfo.InvariantCulture), (s, text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value is >= 1 and <= 256 ? s with { MaximumLength = value } : throw new InvalidDataException("Длина ответа: целое число от 1 до 256."));
        var modes = new ComboBox { Name = "InputValidationMode", ItemsSource = new[] { "Без проверки", "Точное совпадение", "Регулярное выражение" }, SelectedIndex = (int)input.ValidationMode, Margin = new Thickness(0, 3, 0, 8) };
        Add("Режим проверки", modes); modes.SelectionChanged += (_, _) => TrackProperties();
        extendedEdits.Add(value => value.Input = (value.Input ?? input) with { ValidationMode = (TextInputValidationMode)Math.Max(0, modes.SelectedIndex) });
        Text("InputExpectedValue", "Ожидаемый ответ (точное совпадение)", input.ExpectedValue, (s, text) => s with { ExpectedValue = text });
        Check("InputIgnoreCase", "Игнорировать регистр", input.IgnoreCase, (s, enabled) => s with { IgnoreCase = enabled });
        Text("InputPattern", "Регулярное выражение (совпадение всей строки)", input.Pattern, (s, text) => s with { Pattern = text });
        Text("InputFailureMessage", "Сообщение при неверном ответе", input.FailureMessage, (s, text) => s with { FailureMessage = text });
        Text("InputPlaceholder", "Заполнитель пустого поля", input.Placeholder, (s, text) => s with { Placeholder = text });
        Check("InputAutoFocus", "Автофокус поля ввода", input.AutoFocus, (s, enabled) => s with { AutoFocus = enabled });
        Text("InputMask", "Маска: 0 — цифра, L — латиница, A — буква/цифра, & — символ; \\ — экранирование", input.InputMask, (s, text) => s with { InputMask = text });
        Check("InputIsPassword", "Скрывать ответ символами пароля", input.IsPassword, (s, enabled) => s with { IsPassword = enabled });
        Check("InputBranchOnResult", "Разветвлять по результату (выход «Успех»)", input.BranchOnResult, (s, enabled) => s with { BranchOnResult = enabled });
        Check("InputHasErrorOutput", "Добавить выход «Ошибка» (иначе повторить ввод)", input.HasErrorOutput, (s, enabled) => s with { HasErrorOutput = enabled });
        Check("InputHasCancelOutput", "Добавить выход «Отмена» и кнопку «Назад»", input.HasCancelOutput, (s, enabled) => s with { HasCancelOutput = enabled });
        ExtendedProperties.Children.Add(new TextBlock { Text = "Без выхода «Отмена» кнопка «Назад» скрыта, Escape не отменяет ввод. Ответ сохраняется как текст, без выполнения Lua-кода.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });

        void Add(string label, Control control) { ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); ExtendedProperties.Children.Add(control); }
        void Text(string name, string label, string current, Func<TextInputSettings, string, TextInputSettings> update)
        {
            var field = new TextBox { Name = name, Text = current, Margin = new Thickness(0, 3, 0, 8) }; Add(label, field);
            field.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => value.Input = update(value.Input ?? input, field.Text));
        }
        void Check(string name, string label, bool current, Func<TextInputSettings, bool, TextInputSettings> update)
        {
            var field = new CheckBox { Name = name, Content = label, IsChecked = current, Margin = new Thickness(0, 3, 0, 8) }; ExtendedProperties.Children.Add(field);
            field.Checked += (_, _) => TrackProperties(); field.Unchecked += (_, _) => TrackProperties(); extendedEdits.Add(value => value.Input = update(value.Input ?? input, field.IsChecked == true));
        }
    }
}
