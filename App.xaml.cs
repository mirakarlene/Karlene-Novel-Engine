using System.Windows;

namespace KNE
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length == 1 && e.Args[0] == "--analyze-syntax")
            {
                int exitCode = Task.Run(SyntaxCheckWorker.RunAsync).GetAwaiter().GetResult();
                Shutdown(exitCode); return;
            }
            if (e.Args.Length is 2 or 3 or 4 && e.Args[0] == "--lua-worker")
            {
                int exitCode = Task.Run(() => LuaWorker.RunAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null, e.Args.Length == 4 && e.Args[3] == "--preview")).GetAwaiter().GetResult();
                Shutdown(exitCode);
                return;
            }
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
