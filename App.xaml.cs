using System.Windows;
using IconSwapper.Services;

namespace IconSwapper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(CommandLine.Run(e.Args));
            return;
        }

        new MainWindow().Show();
    }
}
