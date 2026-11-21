using System.IO;
using System.Windows;

namespace KNE;

public partial class MainWindow
{
    private async void ClearEditorCache_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        if (MessageBox.Show(this, "Удалить временные файлы редактора и очистить кэш ресурсов?", "Очистка кэша",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string profile = Path.GetDirectoryName(settingsStore.SettingsPath)!;
        string help = HelpCacheDirectory;
        await Task.Run(() => ClearEditorCacheFiles(profile, help), lifetime.Token);
        WindowsFileIcons.Clear();
        GraphicsTextures.Clear();
        if (Application.Current is { } app)
            foreach (var game in app.Windows.OfType<GameWindow>()) game.ClearResourceCache();
        OperationStatus.Text = "Кэш редактора очищен.";
    }, "Очистка кэша…");

    internal static void ClearEditorCacheFiles(string profile, string help)
    {
        string cache = PathSafety.ResolveRelative(profile, "cache");
        if (Directory.Exists(cache)) ProjectDirectories.Delete(profile, cache);
        string manual = PathSafety.ResolveRelative(help, "blocks.html");
        if (File.Exists(manual)) File.Delete(manual);
    }
}
