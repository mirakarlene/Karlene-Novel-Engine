using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KNE;

public partial class MainWindow
{
    internal Action<string>? OpenHelpDocument { get; set; }
    internal string? HelpDocumentDirectory { get; set; }
    private string HelpCacheDirectory => HelpDocumentDirectory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "help");
    private async void BtnHelp_Click(object sender, RoutedEventArgs e)
    {
        if (WelcomePanel.Visibility == Visibility.Visible)
        {
            var help = new HelpChoicesWindow(this, settings.Theme);
            if (help.ShowDialog() != true) return;
            if (help.ShowTutorials) ShowPanel(TutorialsPanel);
            else await OpenGuideAsync();
            return;
        }
        await OpenGuideAsync();
    }

    private void TutorialsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(WelcomePanel);

    private Task OpenGuideAsync() => RunOperationAsync(async () =>
    {
        string path = await BlockHelpDocument.WriteAsync(HelpCacheDirectory, lifetime.Token);
        if (OpenHelpDocument is { } open) open(path);
        else using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
    }, "Открытие справки…");
}
