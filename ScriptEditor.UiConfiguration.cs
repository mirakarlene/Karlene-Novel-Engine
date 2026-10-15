using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

public partial class ScriptEditor
{
    private void AddUiConfigurationFields(ScriptNode node)
    {
        string target = node.Kind == ScriptNodeKind.ConfigureDialogue ? "dialogue" : node.UiTarget;
        var current = node.Ui ?? GameplayUiStyle.ForTarget(target);
        if (node.Kind == ScriptNodeKind.ConfigureButton)
        {
            var targetInput = new ComboBox { Name = "UiTargetInput", ItemsSource = new[] { "choice", "next", "save", "load", "auto_save", "pause" }, SelectedItem = target, Margin = new Thickness(0, 3, 0, 8) };
            Label("Кнопка (choice — варианты выбора)", targetInput);
            targetInput.SelectionChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value => value.UiTarget = (string?)targetInput.SelectedItem ?? target);
        }
        var numbers = new Dictionary<string, string>
        {
            [nameof(current.X)] = "X от выбранного края", [nameof(current.Y)] = "Y от выбранного края",
            [nameof(current.Width)] = "Ширина", [nameof(current.Height)] = "Высота", [nameof(current.FontSize)] = "Размер шрифта",
            [nameof(current.Opacity)] = "Прозрачность (0–1)", [nameof(current.CornerRadius)] = "Скругление углов",
            [nameof(current.PaddingLeft)] = "Отступ слева", [nameof(current.PaddingTop)] = "Отступ сверху",
            [nameof(current.PaddingRight)] = "Отступ справа", [nameof(current.PaddingBottom)] = "Отступ снизу"
        };
        foreach (var pair in numbers)
        {
            var property = typeof(GameplayUiStyle).GetProperty(pair.Key)!;
            var input = new TextBox { Name = "Ui" + pair.Key + "Input", Text = ((double)property.GetValue(current)!).ToString("R", CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 8) };
            Label(pair.Value, input); input.TextChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value =>
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                    throw new InvalidDataException(pair.Value + ": укажите конечное число с точкой.");
                Set(value, property, number);
            });
        }
        foreach (var pair in new[] { (nameof(current.FontFamily), "Семейство шрифта"), (nameof(current.TextColor), "Цвет текста"), (nameof(current.BackgroundColor), "Цвет фона"), (nameof(current.Texture), "Текстура (пусто — оформление движка)") })
        {
            var property = typeof(GameplayUiStyle).GetProperty(pair.Item1)!;
            var input = new TextBox { Name = "Ui" + pair.Item1 + "Input", Text = (string?)property.GetValue(current) ?? "", Margin = new Thickness(0, 3, 0, 8) };
            Label(pair.Item2, input); input.TextChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value => Set(value, property, pair.Item1 == nameof(current.Texture) && string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim()));
            if (pair.Item1 == nameof(current.Texture))
            {
                var browse = new Button { Content = "Загрузить текстуру…", Margin = new Thickness(0, 0, 0, 8) };
                browse.Click += async (_, _) => await GuardAsync(async () =>
                {
                    var dialog = new OpenFileDialog { Filter = "Изображение|*.png;*.jpg;*.jpeg;*.bmp;*.gif", InitialDirectory = projectRoot };
                    if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                    input.Text = await ProjectScriptFiles.ImportTextureAsync(projectRoot, dialog.FileName);
                }); ExtendedProperties.Children.Add(browse);
            }
        }
        Choices(nameof(current.FontWeight), "Насыщенность шрифта", ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"]);
        Choices(nameof(current.HorizontalAlignment), "Расположение по горизонтали", ["left", "center", "right"]);
        Choices(nameof(current.VerticalAlignment), "Расположение по вертикали", ["top", "center", "bottom"]);
        Choices(nameof(current.TextAlignment), "Выравнивание текста", ["left", "center", "right", "justify"]);

        void Label(string label, Control input) { ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); ExtendedProperties.Children.Add(input); }
        void Set(ScriptNode value, PropertyInfo property, object? content)
        {
            var next = (value.Ui ?? GameplayUiStyle.ForTarget(value.Kind == ScriptNodeKind.ConfigureDialogue ? "dialogue" : value.UiTarget)) with { };
            property.SetValue(next, content); value.Ui = next;
        }
        void Choices(string name, string label, string[] choices)
        {
            var property = typeof(GameplayUiStyle).GetProperty(name)!;
            var input = new ComboBox { Name = "Ui" + name + "Input", ItemsSource = choices, SelectedItem = property.GetValue(current), Margin = new Thickness(0, 3, 0, 8) };
            Label(label, input); input.SelectionChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => Set(value, property, input.SelectedItem));
        }
    }
}
