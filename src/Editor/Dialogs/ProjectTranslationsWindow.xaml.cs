using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace KNE;

internal partial class ProjectTranslationsWindow : Window
{
    private sealed class TranslationDraft(string text) : INotifyPropertyChanged
    {
        public string Text
        {
            get => text;
            set
            {
                if (text == value) return;
                text = value;
                PropertyChanged?.Invoke(this, new(nameof(Text)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record Row(GameTextEntry Entry, string Id, string Heading, TranslationDraft Draft)
    {
        public string QuotedSource => Quote(Entry.Text);
        public string Location => Entry.Location;
    }

    private static readonly ProjectFileFormat[] Formats =
    [
        new(".txt", "TXT", "≡", "#53647B"), new(".md", "Markdown", "M↓", "#7166B5"),
        new(".json", "JSON", "{}", "#916219"), new(".pdf", "PDF", "P", "#AE483F"),
        new(".docx", "Word", "W", "#2856AD")
    ];
    private readonly ProjectInfo project;
    private readonly string originalLanguage;
    private readonly CancellationToken token;
    private readonly Dictionary<string, Dictionary<string, TranslationDraft>> drafts = new(StringComparer.Ordinal);
    private GameTextEntry[] catalog = [];
    private string[] skippedFiles = [];
    private Row[] rows = [];
    private string language = "";
    private bool operationInProgress;

    internal ProjectTranslationsWindow(Window owner, string theme, ProjectInfo project,
        IReadOnlyDictionary<string, string> openSources, CancellationToken token)
    {
        InitializeComponent();
        this.project = project;
        this.token = token;
        originalLanguage = ProjectTranslations.OriginalLanguage(project);
        Owner = owner;
        EditorTheme.Apply(this, theme);
        OriginalLanguageLabel.Text = ProjectTranslations.LanguageLabel(originalLanguage);
        foreach (var format in Formats)
        {
            AddExportButton(OriginalFormats, format, translate: false);
            AddExportButton(TranslationFormats, format, translate: true);
        }
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var source = await GameTextCatalog.ReadAsync(project, openSources, token);
            catalog = source.Entries;
            skippedFiles = source.SkippedFiles;
            var languages = ProjectTranslations.Languages(project).Skip(1).Concat(["en", "ru"]).Where(code => code != originalLanguage).Distinct().ToArray();
            LanguageInput.ItemsSource = languages;
            SelectLanguage(languages[0]);
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target } &&
            Background is SolidColorBrush fill)
            target.BackgroundColor = Color.FromRgb(fill.Color.R, fill.Color.G, fill.Color.B);
    }

    private void AddExportButton(Panel panel, ProjectFileFormat format, bool translate)
    {
        string label = "Экспорт " + (translate ? "перевода " : "оригинала ") + format.Extension;
        var button = new Button { Content = ProjectFileFormats.Icon(format), ToolTip = label, Padding = new(3), Margin = new(0, 0, 5, 4) };
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await RunAsync(() => ExportAsync(format, translate), opensPicker: true);
        panel.Children.Add(button);
    }

    private void SelectLanguage_Click(object sender, RoutedEventArgs args) =>
        _ = RunAsync(() => { SelectLanguage(LanguageInput.Text); return Task.CompletedTask; });

    private void SelectLanguage(string value)
    {
        string code = ProjectTranslations.LanguageCode(value) ?? throw new InvalidDataException("Введите код языка, например en или pt-br.");
        if (code == originalLanguage) throw new InvalidDataException("Язык перевода должен отличаться от исходного.");
        if (!drafts.TryGetValue(code, out var languageDraft))
        {
            var values = ProjectTranslations.Load(project, code).Values;
            languageDraft = values.ToDictionary(pair => pair.Key, pair => new TranslationDraft(pair.Value), StringComparer.Ordinal);
            drafts.Add(code, languageDraft);
        }

        language = code;
        LanguageInput.Text = code;
        TargetLanguageLabel.Text = ProjectTranslations.LanguageLabel(code);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        rows = catalog.GroupBy(entry => entry.Category).OrderBy(group => group.Key)
            .SelectMany(group => group.Select((entry, index) =>
            {
                string key = entry.Key;
                int occurrence = counts.GetValueOrDefault(key) + 1;
                counts[key] = occurrence;
                if (!languageDraft.TryGetValue(key, out var draft)) languageDraft.Add(key, draft = new(""));
                return new Row(entry, key + "-" + occurrence, index == 0 ? ProjectTranslations.CategoryLabel(group.Key) : "", draft);
            })).ToArray();
        Entries.ItemsSource = rows;
        if (rows.Length > 0) Entries.ScrollIntoView(rows[0]);
        Status.Text = $"Строк: {rows.Length}, реплик: {rows.Count(row => row.Entry.Category == GameTextCategory.Dialogue)}";
        if (skippedFiles.Length > 0)
        {
            Status.Text += $" · Пропущено файлов: {skippedFiles.Length}";
            Status.ToolTip = string.Join("\n", skippedFiles);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (language.Length == 0) return;
        var translated = drafts[language].Where(pair => pair.Value.Text.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.Ordinal);
        await ProjectTranslations.SaveAsync(project, language, translated, token);
        Status.Text = "Сохранён перевод · " + ProjectTranslations.LanguageLabel(language);
    });

    private async Task ExportAsync(ProjectFileFormat format, bool translate)
    {
        if (language.Length == 0) return;
        var picker = new SaveFileDialog { Filter = format.Filter, DefaultExt = format.Extension,
            FileName = "texts-" + (translate ? language : originalLanguage) + format.Extension, InitialDirectory = project.Path };
        if (picker.ShowDialog(this) != true) return;
        Body.IsEnabled = false;
        var data = rows.Select(row =>
        {
            string text = translate ? row.Draft.Text : row.Entry.Text;
            return new PluginDialogue(row.Id, ProjectTranslations.CategoryLabel(row.Entry.Category) + " · " + row.Location,
                format.Extension is ".docx" or ".pdf" ? Quote(text) : text);
        }).ToArray();
        byte[] bytes = await Task.Run(() => format.Extension is ".txt" or ".md"
            ? Encoding.UTF8.GetBytes(string.Join("\n\n", rows.GroupBy(row => row.Entry.Category).Select(group =>
                (format.Extension == ".md" ? "## " : "") + ProjectTranslations.CategoryLabel(group.Key) + "\n\n" +
                string.Join("\n\n", group.Select(row => Quote(translate ? row.Draft.Text : row.Entry.Text))))))
            : PluginDialogues.Export(data, format.Extension), token);
        await File.WriteAllBytesAsync(picker.FileName, bytes, token);
        Status.Text = "Экспорт завершён · " + Path.GetFileName(picker.FileName);
    }

    private async void Import_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (language.Length == 0) return;
        var picker = new OpenFileDialog { Filter = "Переводы (*.json;*.docx)|*.json;*.docx", InitialDirectory = project.Path };
        if (picker.ShowDialog(this) != true) return;
        Body.IsEnabled = false;
        if (new FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new InvalidDataException("Перевод больше 16 МБ.");
        byte[] data = await File.ReadAllBytesAsync(picker.FileName, token);
        string extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
        var imported = await Task.Run(() => PluginDialogues.Import(data, extension), token);
        var byId = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var changes = new Dictionary<TranslationDraft, string>();
        foreach (var item in imported)
        {
            if (!byId.TryGetValue(item.Id, out var row)) throw new InvalidDataException("Строка отсутствует в проекте: " + item.Id);
            string text = extension == ".docx" && item.Text.Length >= 2 && item.Text.StartsWith('«') && item.Text.EndsWith('»') ? item.Text[1..^1] : item.Text;
            if (changes.TryGetValue(row.Draft, out string? previous) && previous != text)
                throw new InvalidDataException("Найдены разные переводы одинакового текста: " + row.Location);
            changes[row.Draft] = text;
        }
        foreach (var (draft, text) in changes) draft.Text = text;
        Status.Text = $"Импортировано строк: {imported.Count}. Сохраните перевод.";
    }, opensPicker: true);

    private static string Quote(string text) => "«" + text + "»";

    private async Task RunAsync(Func<Task> action, bool opensPicker = false)
    {
        if (operationInProgress) return;
        operationInProgress = true;
        if (!opensPicker) Body.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Close(); }
        catch (Exception error) { Status.Text = error.Message; }
        finally { Body.IsEnabled = true; operationInProgress = false; }
    }
}
