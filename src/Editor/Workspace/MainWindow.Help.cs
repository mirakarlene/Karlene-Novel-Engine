using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KNE;

public partial class MainWindow
{
    internal Action<string>? OpenHelpDocument { get; set; }
    internal string? HelpDocumentDirectory { get; set; }
    private async void BtnHelp_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        string directory = HelpDocumentDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "help");
        string path = await BlockHelpDocument.WriteAsync(directory, lifetime.Token);
        if (OpenHelpDocument is { } open) open(path);
        else using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
    }, "Открытие справки…");
}
