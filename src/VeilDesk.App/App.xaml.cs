using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VeilDesk.Core.Settings;

namespace VeilDesk.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A stray paste, a disappearing drive or a race with the file system
        // must not end the protection silently. The failure is written to a
        // local log and the window stays usable.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(AppPaths.LogsDirectory, "app-errors.log");
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            File.AppendAllText(logPath,
                $"{DateTime.Now:O} {e.Exception.GetType().FullName}: {e.Exception.Message}{Environment.NewLine}" +
                $"{e.Exception.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the reason the app dies.
        }

        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"VeilDesk kept running. Details were written to:{Environment.NewLine}{logPath}",
            "VeilDesk error", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
