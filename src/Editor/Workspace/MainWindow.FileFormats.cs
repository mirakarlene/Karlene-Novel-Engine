using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private ProjectFileFormat? PickProjectFormat(bool import)
    {
        var window = BuildFormatPicker(import);
        ProjectFileFormat? selected = null;
        foreach (var row in ((StackPanel)window.Content).Children.OfType<UniformGrid>())
            foreach (var button in row.Children.OfType<Button>())
                button.Click += (_, _) =>
                {
                    var format = (ProjectFileFormat)button.Tag;
                    if (format.IsPlaceholder)
                    {
                        new EditorPromptWindow("KNE", "KNE ещё на стадии разработки.", settings.Theme) { Owner = window }.ShowDialog();
                        return;
                    }
                    selected = format;
                    window.DialogResult = true;
                };
        window.Owner = this;
        EditorTheme.Apply(window, settings.Theme);
        return window.ShowDialog() == true ? selected : null;
    }

    internal static Window BuildFormatPicker(bool import)
    {
        var groups = new StackPanel { Margin = new Thickness(16) };
        var window = new Window
        {
            Title = import ? "Импорт файла" : "Создать файл",
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = groups
        };
        foreach (var group in import ? ProjectFileFormats.ImportGroups : ProjectFileFormats.CreateGroups)
        {
            var row = new UniformGrid { Columns = import ? 4 : 3, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var format in group)
            {
                var tile = new StackPanel();
                if (!format.IsPlaceholder)
                {
                    var icon = ProjectFileFormats.Icon(format);
                    icon.HorizontalAlignment = HorizontalAlignment.Center;
                    tile.Children.Add(icon);
                }
                var label = new TextBlock { Text = format.Title, TextAlignment = TextAlignment.Center };
                if (format.IsPlaceholder) label.FontSize = 26;
                tile.Children.Add(label);
                if (format.Extension.Length > 0)
                    tile.Children.Add(new TextBlock { Text = format.Extension, TextAlignment = TextAlignment.Center });
                var button = new Button { Content = tile, Tag = format, Width = 148, Height = 82, Margin = new Thickness(3) };
                row.Children.Add(button);
            }
            groups.Children.Add(row);
        }
        groups.Children.Add(new Button { Content = "Отмена", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 4, 12, 4) });
        return window;
    }

    private async void BtnCreateFile_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        if (currentProject is not { } project || PickProjectFormat(import: false) is not { } format) return;
        string initial = format.Folder == null ? Path.Combine(project.Path, browserDirectory) : Path.Combine(project.Path, format.Folder);
        string? target = PickNamedDestination(project.Path, initial, format.IsDirectory ? "Новая папка" : "new_file", format.Extension);
        if (target == null) return;
        await CreateProjectEntryAsync(project, format, target, lifetime.Token);
        await OpenProjectAsync(project.Path);
        if (!format.IsDirectory && (ScriptDocument.IsGraphPath(target) || ScriptDocument.IsCodePath(target))) await OpenScriptAsync(target);
    }, "Создание файла…");

    internal static Task CreateProjectEntryAsync(ProjectInfo project, ProjectFileFormat format, string path, CancellationToken token)
    {
        if (!format.IsDirectory && ScriptDocument.IsGraphPath(path)) return ScriptDocument.CreateVisualAsync(project.Path, path, token);
        return Task.Run(() =>
        {
            if (format.IsDirectory) ProjectDirectories.Create(project.Path, path);
            else new ProjectFiles(project).Create(path, Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "{}" : "");
        }, token);
    }

    private string? PickNamedDestination(string allowedRoot, string initial, string suggestedName, string extension)
    {
        string prompt = extension.Length == 0 ? "Введите имя:" : $"Введите имя. Формат {extension} выбран.";
        return PromptName("Имя", prompt, suggestedName) is { } name
            ? PickDestinationForName(allowedRoot, initial, name, extension) : null;
    }

    private string? PickDestinationForName(string allowedRoot, string initial, string name, string extension)
    {
        name = CompleteFileName(name, extension);
        string? directory = PickDestinationFolder(allowedRoot, initial);
        return directory == null ? null : PathSafety.EnsureInside(allowedRoot, Path.Combine(directory, name));
    }

    private string? PromptName(string title, string prompt, string initialName)
    {
        var dialog = new EditorPromptWindow(title, prompt, settings.Theme, initialName) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    internal static string CompleteFileName(string name, string extension)
    {
        PathSafety.ValidateFileName(name);
        if (extension.Length > 0 && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
        PathSafety.ValidateFileName(name);
        return name;
    }

    private string? PickDestinationFolder(string allowedRoot, string initial)
    {
        string root = PathSafety.NormalizeDirectory(allowedRoot);
        var dialog = CreateDestinationFolderDialog(initial, root);
        if (dialog.ShowDialog(this) != true) return null;
        string selected = PathSafety.NormalizeDirectory(dialog.FolderName);
        if (selected.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            PathSafety.RejectLinks(root);
            return root;
        }
        return PathSafety.EnsureInside(root, selected);
    }

    internal static OpenFolderDialog CreateDestinationFolderDialog(string initial, string root)
    {
        // The Windows shell rejects mixed separators even when Directory.Exists accepts the path.
        initial = PathSafety.NormalizeDirectory(initial);
        return new OpenFolderDialog
        {
            Title = "Выберите папку назначения",
            InitialDirectory = Directory.Exists(initial) ? initial : root
        };
    }
}
