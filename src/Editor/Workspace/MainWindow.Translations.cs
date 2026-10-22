using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private void ShowPluginTranslations()
    {
        var graph = ActiveEditor.GetPluginGraph() ?? throw new InvalidOperationException("Откройте блок-схему для перевода диалогов.");
        var api = editorPlugins!.Api;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Title = "Диалоги и перевод", Owner = this, Content = panel, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        EditorTheme.Apply(dialog, settings.Theme);
        panel.Children.Add(new TextBlock { Text = "ID реплик связывают перевод с исходными блоками. JSON и DOCX поддерживают обратный импорт; PDF предназначен для чтения.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        foreach (string extension in new[] { ".json", ".docx", ".pdf" }.Concat(api.formats.Select(f => f.Extension)).Distinct())
        {
            var button = new Button { Content = "Экспорт " + extension, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8) };
            button.Click += async (_, _) =>
            {
                var picker = new SaveFileDialog { Filter = extension + "|*" + extension, DefaultExt = extension, FileName = "dialogues" + extension, InitialDirectory = currentProject!.Path };
                if (picker.ShowDialog(dialog) != true) return;
                panel.IsEnabled = false;
                try { byte[] data = await Task.Run(() => api.ExportDialogues(graph, extension)); await File.WriteAllBytesAsync(picker.FileName, data); status.Text = "Сохранено: " + picker.FileName; }
                catch (Exception error) { status.Text = error.Message; }
                finally { panel.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }
        var import = new Button { Content = "Импорт перевода…", Padding = new Thickness(8), Margin = new Thickness(0, 12, 0, 0) };
        import.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Переводы|*.json;*.docx" + string.Concat(api.formats.Select(f => ";*" + f.Extension)), InitialDirectory = currentProject!.Path };
            if (picker.ShowDialog(dialog) != true) return;
            panel.IsEnabled = false;
            try
            {
                if (new FileInfo(picker.FileName).Length > 16 * 1024 * 1024) throw new InvalidDataException("Перевод больше 16 МБ.");
                byte[] data = await File.ReadAllBytesAsync(picker.FileName); var translated = await Task.Run(() => api.ImportDialogues(graph, data, Path.GetExtension(picker.FileName)));
                ActiveEditor.ReplacePluginGraph(translated); dialog.Close();
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { panel.IsEnabled = true; }
        };
        panel.Children.Add(import); panel.Children.Add(status); dialog.ShowDialog();
    }
}
