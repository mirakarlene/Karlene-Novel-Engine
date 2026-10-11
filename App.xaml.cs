using System.Windows;

namespace KNE
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args is ["--lua-worker", var projectPath])
            {
                int exitCode = Task.Run(() => LuaWorker.RunAsync(projectPath)).GetAwaiter().GetResult();
                Shutdown(exitCode);
                return;
            }
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
