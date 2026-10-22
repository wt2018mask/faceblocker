using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // No default image and no automatic interactive startup.
        if (args.Length != 3 || args[0] != "--interactive" ||
            !int.TryParse(args[2], out var seconds) || seconds is < 5 or > 300) return 2;
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null, (seconds + 5) * 1000, Timeout.Infinite);
        var application = new Application();
        var bitmap = BitmapFrame.Create(new Uri(Path.GetFullPath(args[1])), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var window = new Window
        {
            Title = "VeilDesk explicit test subject - Escape closes",
            Width = 900, Height = 800, Left = 20, Top = 40,
            Content = new Image { Source = bitmap, Stretch = Stretch.Uniform }
        };
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => window.Close();
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
        application.Run(window);
        return 0;
    }
}
