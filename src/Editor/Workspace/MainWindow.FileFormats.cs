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
        var groups = new StackPanel { Margin = new Thickness(16) };
        var window = new Window
        {
            Owner = this, Title = import ? "Импорт файла" : "Создать файл",
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = groups
        };
        ProjectFileFormat? selected = null;
        foreach (var group in import ? ProjectFileFormats.ImportGroups : ProjectFileFormats.CreateGroups)
        {
            var row = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var format in group)
            {
                var tile = new StackPanel();
                var icon = ProjectFileFormats.Icon(format);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                tile.Children.Add(icon);
                tile.Children.Add(new TextBlock { Text = format.Title, TextAlignment = TextAlignment.Center });
                if (format.Extension.Length > 0)
                    tile.Children.Add(new TextBlock { Text = format.Extension, TextAlignment = TextAlignment.Center });
                var button = new Button { Content = tile, Width = 148, Height = 82, Margin = new Thickness(3) };
                button.Click += (_, _) => { selected = format; window.DialogResult = true; };
                row.Children.Add(button);
            }
            groups.Children.Add(row);
        }
        groups.Children.Add(new Button { Content = "Отмена", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 4, 12, 4) });
        EditorTheme.Apply(window, settings.Theme);
        return window.ShowDialog() == true ? selected : null;
    }

    private async void BtnCreateFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project || PickProjectFormat(import: false) is not { } format) return;
        string initial = format.Folder == null ? project.Path : Path.Combine(project.Path, format.Folder);
        var dialog = new SaveFileDialog
        {
            Title = "Создать файл", Filter = format.Filter, DefaultExt = format.Extension,
            InitialDirectory = Directory.Exists(initial) ? initial : project.Path,
            FileName = "new_file" + format.Extension, AddExtension = true, OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            string path = Path.ChangeExtension(dialog.FileName, format.Extension);
            if (format == ProjectFileFormats.Graph || ScriptDocument.IsShaderPath(path))
                path = ProjectShaderPaths.NewFile(project.Path, path, ScriptDocument.IsShaderPath(path));
            if (ScriptDocument.IsGraphPath(path) || ScriptDocument.IsVisualLuaPath(path))
                await ScriptDocument.CreateVisualAsync(project.Path, path, lifetime.Token);
            else
                await Task.Run(() => new ProjectFiles(project).Create(path, format.Extension == ".json" ? "{}" : ""), lifetime.Token);
            await OpenProjectAsync(project.Path);
            if (ScriptDocument.IsGraphPath(path) || ScriptDocument.IsLuaPath(path) || Path.GetExtension(path) == ".krl")
                await OpenScriptAsync(path);
        }, "Создание файла…");
    }
}
