using System;
using System.IO;
using System.Windows;

namespace MdReaderSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = SetupOptions.Parse(e.Args);

        if (options.Silent)
        {
            var exitCode = 0;
            try
            {
                if (options.Uninstall)
                {
                    SetupService.Uninstall(options.InstallDir);
                }
                else
                {
                    SetupService.Install(options.InstallDir, options.Extensions, options.Associate, options.StartMenu, options.Desktop);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "MdReaderSetup.log"), ex.ToString());
                exitCode = 1;
            }

            Shutdown(exitCode);
            return;
        }

        if (options.Uninstall)
        {
            new UninstallWindow(options).Show();
        }
        else
        {
            new MainWindow(options).Show();
        }
    }
}
