using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VeilDesk.App;
using VeilDesk.App.Capture;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Settings;

namespace LivePipelineProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--interactive")
        {
            Console.Error.WriteLine("No UI started. Deliberate invocation only: LivePipelineProbe --interactive <local-image> <local-data-directory>");
            return 2;
        }
        AppPaths.OverrideRoot = Path.GetFullPath(args[2]);
        using var hardDeadline = new System.Threading.Timer(_ => Environment.Exit(124), null, 45000, Timeout.Infinite);
        MonitorEnumerator.EnablePerMonitorDpiAwareness();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var application = new Application();
        var window = new Window
        {
            Title = "VeilDesk controlled pipeline probe - Escape stops test",
            Left = 40, Top = 40, Width = 1050, Height = 800,
            Content = new Image
            {
                Source = BitmapFrame.Create(new Uri(Path.GetFullPath(args[1])), BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
                Stretch = Stretch.Uniform
            }
        };
        var completed = false;
        var exitCode = 1;
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) cancel.Cancel(); };
        window.Closing += (_, e) => { if (!completed) { e.Cancel = true; cancel.Cancel(); } };
        window.Loaded += async (_, _) =>
        {
            await using var engine = new VeilDeskEngine(window.Dispatcher);
            try
            {
                var monitors = MonitorEnumerator.Enumerate();
                var settings = new AppSettings();
                settings.Performance.UseDirectML = false;
                settings.Censoring.Style = CensorStyle.Solid;
                var positiveSamples = 0;
                for (var run = 0; run < 2; run++)
                {
                    var protectedTargetSeen = false;
                    cancel.Token.ThrowIfCancellationRequested();
                    await engine.StartAsync(settings, monitors);
                    for (var sample = 0; sample < 8; sample++)
                    {
                        window.Left = 40 + sample * 18; // controlled real desktop movement
                        await Task.Delay(1000, cancel.Token);
                        var status = engine.GetStatus();
                        var monitor = status.Monitors.First();
                        if (!monitor.FailClosed && monitor.Regions > 0) { positiveSamples++; protectedTargetSeen = true; }
                        if (protectedTargetSeen && monitor.Tracks > 0 && monitor.Regions == 0 && !monitor.FailClosed)
                            throw new InvalidOperationException("Previously protected live target has no coverage");
                        Console.WriteLine($"run={run + 1} sample={sample} capture={monitor.CapturedFrames} analyses={monitor.Analyses} events={monitor.DetectionEvents} tracks={monitor.Tracks} regions={monitor.Regions} failClosed={monitor.FailClosed} excluded={status.OverlayExcludedFromCapture} overlays={application.Windows.OfType<OverlayWindow>().Count()} error={monitor.Error is not null}");
                    }
                    await engine.StopAsync();
                    var remaining = application.Windows.OfType<OverlayWindow>().Count();
                    Console.WriteLine($"STOP run={run + 1} running={engine.IsRunning} overlays={remaining}");
                    if (engine.IsRunning || remaining != 0) throw new InvalidOperationException("Stop left overlay windows");
                }
                Console.WriteLine($"live-positive-samples={positiveSamples}; physical pixel alignment and exposure duration UNVERIFIED");
                exitCode = positiveSamples > 0 ? 0 : 3;
            }
            catch (OperationCanceledException) { Console.WriteLine("CANCELLED; cleaning up owned pipeline"); exitCode = 4; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                await engine.StopAsync();
                completed = true;
                window.Close();
            }
        };
        application.Run(window);
        return exitCode;
    }
}
