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
        if (args.Length == 1 && args[0] == "--inventory")
        {
            MonitorEnumerator.EnablePerMonitorDpiAwareness();
            var displays = MonitorEnumerator.Enumerate();
            Console.WriteLine($"attached-windows-displays={displays.Count}; physical multi-monitor testing requires actual hardware");
            foreach (var display in displays)
                Console.WriteLine($"display width={display.Width} height={display.Height} x={display.X} y={display.Y} dpi={display.DpiScale:F2} primary={display.IsPrimary}");
            return 0;
        }
        var cancelInitialization = args.Contains("--initialization-cancel");
        args = args.Where(a => a != "--initialization-cancel").ToArray();
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
                if (cancelInitialization)
                {
                    using var entered = new ManualResetEventSlim();
                    using var release = new ManualResetEventSlim();
                    await using var initializing = new VeilDeskEngine(window.Dispatcher, options =>
                    {
                        entered.Set();
                        if (!release.Wait(3000)) throw new TimeoutException("test model loader was not released");
                        return VeilDesk.Inference.VisionModels.Load(options);
                    });
                    var start = initializing.StartAsync(settings, monitors);
                    if (!await Task.Run(() => entered.Wait(2000))) throw new InvalidOperationException("model initialization was not reached");
                    var stop = initializing.StopAsync();
                    try
                    {
                        await Task.Delay(100);
                        if (application.Windows.OfType<OverlayWindow>().Any()) throw new InvalidOperationException("initialization cancellation left a cover visible");
                    }
                    finally { release.Set(); }
                    var cancelled = false;
                    try { await start; } catch (OperationCanceledException) { cancelled = true; }
                    await stop;
                    if (!cancelled || initializing.IsRunning) throw new InvalidOperationException("initialization did not cancel cleanly");
                    Console.WriteLine("PASS initialization cancellation: overlay removed before loader completion; no capture started; cancelled start; clean stop");
                }
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
                        var placements = engine.MeasureOverlayPlacements();
                        foreach (var placement in placements)
                        {
                            if (placement.ExpectedWindow != placement.ActualWindow)
                                throw new InvalidOperationException("Native overlay bounds do not match physical display bounds");
                            var maximum = placement.Regions.Select(r => r.MaximumEdgeError).DefaultIfEmpty(0).Max();
                            if (maximum > 1.0) throw new InvalidOperationException("Rendered mask is misaligned in physical coordinates");
                            Console.WriteLine($"placement dpi={placement.DpiScale:F2} masks={placement.Regions.Count} maxEdgeErrorPixels={maximum:F3} detectedMasks={placement.Regions.Count(r => r.Kind == VeilDesk.Core.Censoring.CensorRegionKind.Detection)}");
                        }
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
