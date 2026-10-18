using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    private string selectedSpeakerId = "";
    private string? characterPickerNode;
    private int characterPickerGeneration;

    private static bool IsDialogue(ScriptNodeKind kind) => kind is ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue;
    private static bool HasCharacterPicker(ScriptNodeKind kind) => IsDialogue(kind) || kind is ScriptNodeKind.SetSpeaker or ScriptNodeKind.UpdateCharacter;

    private void UpdateSpeakerReference()
    {
        if (SpeakerReference != null) SpeakerReference.Text = selectedSpeakerId.Length == 0
            ? "Пустое поле использует спикера по умолчанию, если он установлен. Кнопка «Рассказчик» явно отключает спикера для этой реплики."
            : "Профиль: " + selectedSpeakerId + ". Правка имени переключит на ручной ввод.";
    }

    private void AddNarrativeFields(ScriptNode node)
    {
        if (node.Kind is ScriptNodeKind.RegisterCharacter or ScriptNodeKind.UpdateCharacter)
        {
            var profile = node.Character ?? new CharacterProfile();
            Text("CharacterIdInput", "ID (латиница, без пробелов)", profile.Id, (value, text) => value.Character = (value.Character ?? profile) with { Id = text.Trim() });
            Text("CharacterNameInput", "Отображаемое имя", profile.Name, (value, text) => value.Character = (value.Character ?? profile) with { Name = text });
            Text("CharacterNameColorInput", "Цвет имени", profile.NameColor, (value, text) => value.Character = (value.Character ?? profile) with { NameColor = text.Trim() });
            Choice("CharacterAlignmentInput", "Выравнивание текста по умолчанию", profile.TextAlignment, ["left", "center", "right", "justify"], (value, text) => value.Character = (value.Character ?? profile) with { TextAlignment = text });
            Text("CharacterFontInput", "Семейство шрифта", profile.FontFamily, (value, text) => value.Character = (value.Character ?? profile) with { FontFamily = text.Trim() });
            Number("CharacterFontSizeInput", "Размер шрифта", profile.FontSize, (value, number) => value.Character = (value.Character ?? profile) with { FontSize = number });
            Hint(node.Kind == ScriptNodeKind.UpdateCharacter
                ? "ID должен совпадать с уже зарегистрированным персонажем. Изменения применяются к следующим репликам, не изменяя уже показанный фрейм."
                : "Профиль задаёт только имя и типографику. Для изображения используйте блок «Отобразить спрайт». Регистрация должна выполниться до реплики с этим профилем.");
        }
        if (HasCharacterPicker(node.Kind) && !IsDialogue(node.Kind))
        {
            if (node.Kind == ScriptNodeKind.SetSpeaker)
            {
                Text("DefaultSpeakerIdInput", "ID зарегистрированного персонажа", node.SpeakerId, (value, text) => value.SpeakerId = text.Trim());
                Hint("Спикер применяется к следующим репликам с пустым именем. Вызванные и параллельные ветки наследуют его; изменения в них не затрагивают родителя или соседние ветки.");
            }
            var choose = new Button { Name = "SelectNarrativeCharacterButton", Content = "Выбрать профиль персонажа…", Margin = new Thickness(0, 3, 0, 8) };
            choose.Click += SelectCharacter_Click; ExtendedProperties.Children.Add(choose);
        }
        if (IsDialogue(node.Kind))
            Check("ExplicitNarratorInput", "Рассказчик: игнорировать спикера по умолчанию", node.Narration, (value, enabled) => value.Narration = enabled);
        if (node.Kind is ScriptNodeKind.Branching or ScriptNodeKind.CompareVariables)
        {
            Text("BranchVariableInput", "Первая переменная state", node.Variable, (value, text) => { ValidateKey(text); value.Variable = text.Trim(); });
            Choice("BranchComparisonInput", "Сравнение", node.Comparison.ToString(), Enum.GetNames<ScriptComparison>(), (value, text) => value.Comparison = Enum.Parse<ScriptComparison>(text));
            if (node.Kind == ScriptNodeKind.CompareVariables)
            {
                Text("ComparedVariableInput", "Вторая переменная state", node.CompareVariable, (value, text) => { ValidateKey(text); value.CompareVariable = text.Trim(); });
                Hint("Сравниваются текущие значения двух переменных. Блок не выводит реплики и не ожидает действий игрока. Для >, <, ≥, ≤ обе переменные должны быть числами.");
            }
            else
            {
                Text("BranchValueInput", "Значение для сравнения", node.Value, (value, text) => value.Value = text);
                Choice("BranchValueTypeInput", "Тип значения", node.ValueKind.ToString(), Enum.GetNames<ScriptValueKind>(), (value, text) => value.ValueKind = Enum.Parse<ScriptValueKind>(text));
                Hint("Выбирает ветку по переменной и постоянному значению. Выходы «Да» и «Нет» можно подключить к любым блокам.");
            }
        }
        if (node.Kind is ScriptNodeKind.TextPause or ScriptNodeKind.ClearText or ScriptNodeKind.AutoDialogue)
            Number("NarrativeDurationInput", node.Kind == ScriptNodeKind.AutoDialogue ? "Через сколько секунд продолжить" : "Длительность, секунды", node.Duration, (value, number) => value.Duration = number);
        if (node.Kind is ScriptNodeKind.TextPause or ScriptNodeKind.AutoDialogue)
            Check("SkippableInput", "Разрешить пропуск ожидания по клику", node.Skippable, (value, enabled) => value.Skippable = enabled);
        if (node.Kind == ScriptNodeKind.ClearText)
            Check("AnimatedClearInput", "Анимация стирания (выключено — мгновенно)", node.Animated, (value, enabled) => value.Animated = enabled);
        if (node.Kind == ScriptNodeKind.RandomBranch)
        {
            var weights = new TextBox { Name = "RandomWeightsInput", AcceptsReturn = true, MinHeight = 60, Text = string.Join(Environment.NewLine, node.Options.Select(option => option.Weight.ToString("R", CultureInfo.InvariantCulture))), Margin = new Thickness(0, 3, 0, 8) };
            Add("Веса веток — по одному на строку, в порядке названий", weights);
            weights.TextChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value =>
            {
                string[] lines = weights.Text.Replace("\r", "").TrimEnd('\n').Split('\n');
                if (lines.Length != value.Options.Count) throw new InvalidDataException("Число весов должно совпадать с числом случайных веток.");
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!double.TryParse(lines[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double weight) || !double.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException("Вес ветки: конечное число от 0, с точкой в дробной части.");
                    value.Options[i].Weight = weight;
                }
            });
            Hint("Веса относительные: 60 и 40 дают 60% и 40%, 3 и 2 — те же вероятности. Нулевой вес исключает ветку. Каждому выходу задайте продолжение.");
        }
        if (node.Kind == ScriptNodeKind.EffectDialogue)
        {
            var effect = node.TextEffect ?? new TextEffectSettings();
            string[] labels = ["Дрожание · Shake", "Волна · Wave", "Покачивание · Sway", "Прыжки · Bounce", "Плавание · Float", "Взрыв · Explode", "Пульсация · Pulse", "Кручение · Spin", "Сжатие / растяжение · Squash", "Глитч · Glitch", "Размытие · Blur", "Растворение · Dissolve", "Печать хаосом · Scramble", "Радуга · Rainbow", "Пульсация цвета · Color Cycle", "Растекание · Drip"];
            var kind = new ComboBox { Name = "TextEffectKindInput", ItemsSource = labels, SelectedIndex = TextEffectSettings.Kinds.ToList().IndexOf(effect.Kind), Margin = new Thickness(0, 3, 0, 8) };
            Add("Эффект всего текста", kind); kind.SelectionChanged += (_, _) => TrackProperties();
            extendedEdits.Add(value => value.TextEffect = (value.TextEffect ?? effect) with { Kind = TextEffectSettings.Kinds[Math.Max(0, kind.SelectedIndex)] });
            Number("TextEffectSpeedInput", "Скорость эффекта (больше 0, до 100)", effect.Speed, (value, number) => value.TextEffect = (value.TextEffect ?? effect) with { Speed = number });
            Check("TextEffectLoopInput", "Повторять эффект циклично", effect.Loop, (value, enabled) => value.TextEffect = (value.TextEffect ?? effect) with { Loop = enabled });
            Text("TextEffectColor1Input", "Первый цвет (пульсация цвета)", effect.Color1, (value, text) => value.TextEffect = (value.TextEffect ?? effect) with { Color1 = text.Trim() });
            Text("TextEffectColor2Input", "Второй цвет (пульсация цвета)", effect.Color2, (value, text) => value.TextEffect = (value.TextEffect ?? effect) with { Color2 = text.Trim() });
        }

        void Add(string label, Control control) { ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); ExtendedProperties.Children.Add(control); }
        static void ValidateKey(string text) { if (!LuaSource.IsIdentifier(text.Trim()) || text.Trim().Length > 80) throw new InvalidDataException("Имя переменной: латиница, цифры и _, без цифры в начале и ключевых слов Lua."); }
        void Hint(string text) => ExtendedProperties.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 10) });
        void Text(string name, string label, string current, Action<ScriptNode, string> assign)
        {
            var input = new TextBox { Name = name, Text = current, Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, input.Text));
        }
        void Number(string name, string label, double current, Action<ScriptNode, double> assign)
        {
            var input = new TextBox { Name = name, Text = current.ToString("R", CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(value =>
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < 0 || number > 3600)
                    throw new InvalidDataException(label + ": укажите число от 0 до 3600 с точкой.");
                assign(value, number);
            });
        }
        void Choice(string name, string label, string current, string[] options, Action<ScriptNode, string> assign)
        {
            var input = new ComboBox { Name = name, ItemsSource = options, SelectedItem = current, Margin = new Thickness(0, 3, 0, 8) }; Add(label, input);
            input.SelectionChanged += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, (string?)input.SelectedItem ?? current));
        }
        void Check(string name, string label, bool current, Action<ScriptNode, bool> assign)
        {
            var input = new CheckBox { Name = name, Content = label, IsChecked = current, Margin = new Thickness(0, 3, 0, 8) }; ExtendedProperties.Children.Add(input);
            input.Checked += (_, _) => TrackProperties(); input.Unchecked += (_, _) => TrackProperties(); extendedEdits.Add(value => assign(value, input.IsChecked == true));
        }
    }

    private sealed record CharacterEntry(CharacterProfile Profile, string Source)
    {
        public string Label => Profile.Name + " (" + Profile.Id + ") · " + Source;
    }

    private async void SelectCharacter_Click(object sender, RoutedEventArgs e)
    {
        int generation = ++characterPickerGeneration;
        try
        {
            ApplyProperties();
            if (Document?.Graph is not { } graph || Selected is not { } selected || !HasCharacterPicker(selected.Kind)) return;
            characterPickerNode = selected.Id;
            CharacterPickerOverlay.Visibility = Visibility.Visible;
            CharacterProfiles.ItemsSource = null; ChooseCharacterButton.IsEnabled = false;
            CharacterPickerStatus.Text = "Поиск профилей в блок-схемах проекта…";
            string root = projectRoot, currentPath = Document.FilePath;
            var entries = graph.Nodes.Where(node => node.Kind == ScriptNodeKind.RegisterCharacter && node.Character != null)
                .Select(node => new CharacterEntry(node.Character!, "текущая схема")).ToList();
            var external = await Task.Run(() => ReadProjectCharacters(root, currentPath));
            if (generation != characterPickerGeneration || CharacterPickerOverlay.Visibility != Visibility.Visible) return;
            var profiles = entries.Concat(external).GroupBy(entry => entry.Profile.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(entry => entry.Profile.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Profile.Id, StringComparer.Ordinal).ToArray();
            CharacterProfiles.ItemsSource = profiles;
            CharacterProfiles.SelectedItem = profiles.FirstOrDefault(entry => entry.Profile.Id == selectedSpeakerId) ?? profiles.FirstOrDefault();
            ChooseCharacterButton.IsEnabled = profiles.Length > 0;
            CharacterPickerStatus.Text = profiles.Length == 0 ? "Профилей пока нет. Добавьте блок «Персонаж» и настройте его." : "Регистрация профиля должна выполниться до этой реплики. Несохранённые правки текущей схемы имеют приоритет.";
        }
        catch (Exception error) { CharacterPickerStatus.Text = error.Message; EditorStatus.Text = error.Message; }
    }

    private static List<CharacterEntry> ReadProjectCharacters(string root, string currentPath)
    {
        var result = new List<CharacterEntry>();
        foreach (string path in ProjectLoader.EnumerateFiles(root, CancellationToken.None).Files.Where(ScriptDocument.IsVisualPath))
        {
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                PathSafety.EnsureInside(root, path);
                if (new FileInfo(path).Length > 8_000_000) continue;
                var graph = VisualScript.Parse(File.ReadAllText(path));
                result.AddRange(graph.Nodes.Where(node => node.Kind == ScriptNodeKind.RegisterCharacter && node.Character != null)
                    .Select(node => new CharacterEntry(node.Character!, Path.GetRelativePath(root, path).Replace('\\', '/'))));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { /* A broken draft is diagnosed when it is opened, not by the picker. */ }
        }
        return result;
    }

    private void ChooseCharacter_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (CharacterProfiles.SelectedItem is not CharacterEntry entry || Selected?.Id != characterPickerNode) return;
        SetDialogueSpeaker(entry.Profile.Id, entry.Profile.Name); CloseCharacterPicker();
    });

    private void Narrator_Click(object sender, RoutedEventArgs e) => Guard(() => { ApplyProperties(); SetDialogueSpeaker("", "", narration: true); });
    private void CancelCharacter_Click(object sender, RoutedEventArgs e) => CloseCharacterPicker();
    private void CloseCharacterPicker() { characterPickerGeneration++; characterPickerNode = null; CharacterPickerOverlay.Visibility = Visibility.Collapsed; }
    private void SetDialogueSpeaker(string id, string name, bool narration = false)
    {
        if (Document?.Graph is not { } graph || Selected is not { } selected || !HasCharacterPicker(selected.Kind)) return;
        if (IsDialogue(selected.Kind) && selected.SpeakerId == id && selected.Speaker == name && selected.Narration == narration) return;
        var copy = VisualScript.Parse(graph.Serialize()); var node = copy.Nodes.Single(item => item.Id == selected.Id);
        if (node.Kind == ScriptNodeKind.UpdateCharacter && CharacterProfiles.SelectedItem is CharacterEntry entry) node.Character = entry.Profile;
        else { node.SpeakerId = id; node.Speaker = node.Kind == ScriptNodeKind.SetSpeaker ? "" : name; node.Narration = narration; }
        _ = copy.Serialize(); Remember(); Document.Graph = copy; Repaint();
    }
}
