using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.App.Capture;
using VeilDesk.App.Export;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Diagnostics;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Sessions;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

namespace VeilDesk.App;

public sealed record MonitorRuntimeStatus(
    string MonitorId,
    string Name,
    int Width,
    int Height,
    double CaptureFps,
    long CapturedFrames,
    long DroppedFrames,
    double AnalysisFps,
    long Analyses,
    double P50Ms,
    double P95Ms,
    int Tracks,
    int Regions,
    long DetectionEvents,
    bool FailClosed,
    DateTime? LastFrameUtc,
    string? Error,
    string? CaptureError)
{
    public bool HasRecentFrame => LastFrameUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(3);
}

public sealed record EngineStatus(
    bool Running,
    string Provider,
    string? DirectMLError,
    DateTimeOffset? StartedAt,
    IReadOnlyList<MonitorRuntimeStatus> Monitors,
    long TotalAnalyses,
    long TotalDetections,
    int PeakTracks,
    bool OverlayRunning,
    bool OverlayExcludedFromCapture,
    string? Error)
{
    public static readonly EngineStatus Stopped = new(false, "-", null, null,
        Array.Empty<MonitorRuntimeStatus>(), 0, 0, 0, false, false, null);
}

/// <summary>A retained frame and matching censor regions for the recorder.</summary>
public sealed class RecordingFrame : IDisposable
{
    public RecordingFrame(VideoFrame frame, MonitorDescriptor monitor,
        IReadOnlyList<CensorRegion> regions)
    {
        Frame = frame;
        Monitor = monitor;
        Regions = regions;
    }

    public VideoFrame Frame { get; }
    public MonitorDescriptor Monitor { get; }
    public IReadOnlyList<CensorRegion> Regions { get; }

    public void Dispose() => Frame.Dispose();
}

/// <summary>
/// Coordinates the private, on-device pipeline: WGC capture per display,
/// one shared ONNX model set, one tracker per display, a 30 Hz overlay refresh
/// that uses prediction between inferences, fail-closed supervision, session
/// statistics, and screenshot export.
/// </summary>
public sealed class VeilDeskEngine : IAsyncDisposable
{
    private readonly CaptureService _capture = new();
    private readonly Dispatcher _ui;
    private readonly OverlayService _overlay;
    private readonly List<MonitorRunner> _runners = new();
    private readonly object _stateGate = new();

    private VisionModels? _models;
    private AppSettings _settings = new();
    private CancellationTokenSource? _cts;
    private Task[] _analysisTasks = Array.Empty<Task>();
    private System.Threading.Timer? _overlayTimer;
    private DateTimeOffset? _startedAt;
    private volatile bool _running;
    private long _totalAnalyses;
    private long _totalDetections;
    private int _peakTracks;
    private string? _error;
    private string _provider = "-";
    private string? _dmlError;

    public VeilDeskEngine(Dispatcher uiDispatcher)
    {
        _ui = uiDispatcher;
        _overlay = new OverlayService(uiDispatcher);
    }

    public bool IsRunning => _running;
    public AppSettings Settings => _settings;
    public string Provider => _provider;
    public string ProviderDetail => _dmlError is null ? _provider : $"{_provider} ({_dmlError})";

    public event Action? StateChanged;

    public async Task StartAsync(AppSettings settings, IReadOnlyList<MonitorDescriptor> allMonitors)
    {
        if (_running) await StopAsync();
        _error = null;
        var snapshot = settings.Clone();
        SettingsValidator.ValidateAndClamp(snapshot);
        _settings = snapshot;

        var selected = snapshot.Capture.EnabledMonitorIds.Count == 0
            ? allMonitors.ToList()
            : allMonitors.Where(m => snapshot.Capture.EnabledMonitorIds.Contains(m.Id,
                StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("No display is selected in Settings → Displays.");

        var modelDirectory = ModelLocator.Find()
            ?? throw new InvalidOperationException(
                "Model files were not found. Run scripts/fetch-models.ps1, then restart. Searched: " +
                ModelLocator.DescribeSearched());

        var options = new VisionPipelineOptions
        {
            ModelDirectory = modelDirectory,
            UseDirectML = snapshot.Performance.UseDirectML,
            UseHighResolutionPersonModel = snapshot.Performance.Preset == PerformancePreset.High,
            InferenceThreads = snapshot.Performance.InferenceThreads
        };

        try
        {
            _provider = "loading models";
            _models = await Task.Run(() => VisionModels.Load(options));
            _provider = _models.Provider;
            _dmlError = _models.DirectMLError;

            var captureFps = Math.Clamp(snapshot.Performance.ScanRateHz + 4, 10, 30);
            await _capture.StartAsync(selected,
                new CaptureOptions(captureFps, snapshot.Capture.CaptureCursor));

            await _ui.InvokeAsync(() => _overlay.Start(selected));

            _runners.Clear();
            foreach (var monitor in selected)
            {
                var runner = new MonitorRunner(monitor, new VisionPipeline(options, _models));
                if (snapshot.Censoring.FailClosed)
                {
                    // Cover the display immediately; the first inference pass
                    // clears fail-closed as soon as analysis succeeds.
                    var guard = CensorComposer.Build(Array.Empty<TrackedDetection>(), snapshot,
                        monitor.LocalBounds, Environment.TickCount64, true);
                    _overlay.Render(monitor.Id, guard, snapshot, null);
                }
                _runners.Add(runner);
            }

            _cts = new CancellationTokenSource();
            _startedAt = DateTimeOffset.Now;
            _running = true;
            _totalAnalyses = 0;
            _totalDetections = 0;
            _peakTracks = 0;

            _analysisTasks = _runners.Select(r => Task.Run(() => AnalysisLoopAsync(r, _cts.Token))).ToArray();
            StartOverlayTimer();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            await CleanupAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_running && _analysisTasks.Length == 0) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try
        {
            await Task.WhenAll(_analysisTasks).WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        PersistSession();
        await CleanupAsync();
        StateChanged?.Invoke();
    }

    private async Task CleanupAsync()
    {
        _overlayTimer?.Dispose();
        _overlayTimer = null;
        _cts?.Dispose();
        _cts = null;
        _analysisTasks = Array.Empty<Task>();

        await _ui.InvokeAsync(() => _overlay.Stop());
        await _capture.StopAsync();
        foreach (var runner in _runners)
        {
            runner.Pipeline.Dispose();
            runner.Dispose();
        }
        _runners.Clear();
        _models?.Dispose();
        _models = null;
        _startedAt = null;
    }

    private void StartOverlayTimer()
    {
        _overlayTimer = new System.Threading.Timer(_ =>
        {
            if (!_running) return;
            try { RenderOverlays(); } catch { /* overlay refresh is best-effort */ }
        }, null, 10, 33);
    }

    private void RenderOverlays()
    {
        var now = Environment.TickCount64;
        foreach (var runner in _runners)
        {
            var regions = runner.Pipeline.GetRegions(_settings, runner.Monitor.LocalBounds, now);
            runner.LastRegionCount = regions.Count;
            runner.LastFailClosed = runner.Pipeline.FailClosedActive(_settings, now);
            var snapshot = Volatile.Read(ref runner.Snapshot);
            _overlay.Render(runner.Monitor.Id, regions, _settings, snapshot);
        }
    }

    private async Task AnalysisLoopAsync(MonitorRunner runner, CancellationToken token)
    {
        var c = _settings;
        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(c.Performance.ScanRateHz, 1, 60));
        var started = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            var frame = _capture.TryTakeFrame(runner.Monitor.Id);
            if (frame is null)
            {
                try { await Task.Delay(8, token); } catch (OperationCanceledException) { break; }
                continue;
            }

            var passStart = Stopwatch.GetTimestamp();
            try
            {
                var outcome = ProcessFrame(runner, frame);
                if (outcome.Analyzed)
                {
                    runner.Latency.Add(outcome.AnalysisMs);
                    Interlocked.Increment(ref runner.Analyses);
                    Interlocked.Add(ref _totalAnalyses, 1);
                    Interlocked.Add(ref _totalDetections, outcome.Detections);
                    Interlocked.Add(ref runner.DetectionEvents, outcome.Detections);
                    Volatile.Write(ref runner.LastAnalysisMs, Environment.TickCount64);
                    var tracks = outcome.TrackCount;
                    if (tracks > Volatile.Read(ref _peakTracks))
                        Interlocked.Exchange(ref _peakTracks, tracks);
                }
                Volatile.Write(ref runner.Error, outcome.Error);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref runner.Error, ex.Message);
                try { await Task.Delay(200, token); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                frame.Dispose();
            }

            var elapsed = Stopwatch.GetElapsedTime(passStart);
            var remaining = interval - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, token); } catch (OperationCanceledException) { break; }
            }
            else
            {
                try { await Task.Delay(1, token); } catch (OperationCanceledException) { break; }
            }
        }
        _ = started;
    }

    private FrameOutcome ProcessFrame(MonitorRunner runner, VideoFrame frame)
    {
        var c = _settings;
        var now = Environment.TickCount64;
        var thumb = runner.Scene.ComputeThumbprint(frame.Span, frame.Width, frame.Height, frame.Stride);
        var delta = runner.Scene.Update(thumb);

        var due = !c.Performance.AdaptiveScanning ||
                  delta >= c.Performance.StaticSceneThreshold ||
                  now - Interlocked.Read(ref runner.LastAnalysisMs) >= 750 ||
                  Interlocked.Read(ref runner.Analyses) == 0;
        if (!due) return FrameOutcome.Skipped;

        // WPF bitmap creation must stay off the UI thread; the result is frozen
        // so the overlay and exporter can read it from any thread.
        BitmapSource? snapshot = null;
        if (NeedsSnapshot(c.Censoring.Style) &&
            now - Interlocked.Read(ref runner.LastSnapshotMs) >= 180)
        {
            try
            {
                snapshot = FrameImage.ToBitmap(frame);
                Interlocked.Exchange(ref runner.LastSnapshotMs, now);
            }
            catch
            {
                snapshot = null;
            }
        }

        var result = runner.Pipeline.Analyze(frame, c, now);
        if (snapshot is not null) Volatile.Write(ref runner.Snapshot, snapshot);
        var detectionEvents = result.NudeDetections.Count + result.PersonDetections.Count;
        runner.LastRegionCount = result.Regions.Count;
        runner.LastFailClosed = runner.Pipeline.FailClosedActive(c, now);
        runner.LastTracks = result.Tracks.Count;
        return new FrameOutcome(true, result.TotalMs, result.Tracks.Count, detectionEvents,
            result.Error, delta);
    }

    private static bool NeedsSnapshot(CensorStyle style) => style is CensorStyle.Blur or
        CensorStyle.Pixelate or CensorStyle.FrostedGlass or CensorStyle.NightVision;

    public async Task<string> SaveScreenshotAsync(string monitorId, bool includeCensorship,
        string? path = null)
    {
        if (!_running) throw new InvalidOperationException("Protection is not running.");
        var runner = _runners.FirstOrDefault(r =>
            string.Equals(r.Monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Unknown display.");
        using var frame = _capture.TryGrabLatestFrame(monitorId)
            ?? throw new InvalidOperationException("No frame is available yet for this display.");
        var now = Environment.TickCount64;
        var regions = runner.Pipeline.GetRegions(_settings, runner.Monitor.LocalBounds, now);
        return await ExportService.SaveScreenshotAsync(frame, runner.Monitor, regions, _settings,
            includeCensorship, path, _ui);
    }

    public IReadOnlyList<MonitorDescriptor> GetActiveMonitors()
        => _runners.Select(r => r.Monitor).ToList();

    /// <summary>
    /// Takes a retained capture frame plus the region snapshot used by the
    /// video exporter. The caller owns and must dispose the returned frame.
    /// </summary>
    public RecordingFrame? TryGrabRecordingFrame(string monitorId)
    {
        if (!_running) return null;
        var runner = _runners.FirstOrDefault(r =>
            string.Equals(r.Monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (runner is null) return null;
        var frame = _capture.TryGrabLatestFrame(monitorId);
        if (frame is null) return null;
        try
        {
            var now = Environment.TickCount64;
            var regions = runner.Pipeline.GetRegions(_settings, runner.Monitor.LocalBounds, now);
            return new RecordingFrame(frame, runner.Monitor, regions);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public EngineStatus GetStatus()
    {
        var now = Environment.TickCount64;
        var capture = _capture.GetStatus().ToDictionary(s => s.MonitorId,
            StringComparer.OrdinalIgnoreCase);
        var monitors = new List<MonitorRuntimeStatus>(_runners.Count);
        foreach (var runner in _runners)
        {
            capture.TryGetValue(runner.Monitor.Id, out var cap);
            var latency = runner.Latency.Snapshot();
            var elapsed = _startedAt is { } start
                ? Math.Max(0.001, (DateTimeOffset.Now - start).TotalSeconds)
                : 1.0;
            monitors.Add(new MonitorRuntimeStatus(
                runner.Monitor.Id,
                runner.Monitor.FriendlyName,
                cap?.Width ?? runner.Monitor.Width,
                cap?.Height ?? runner.Monitor.Height,
                cap?.MeasuredFps ?? 0,
                cap?.FramesDelivered ?? 0,
                cap?.FramesDropped ?? 0,
                Interlocked.Read(ref runner.Analyses) / elapsed,
                Interlocked.Read(ref runner.Analyses),
                latency.P50Ms,
                latency.P95Ms,
                (int)runner.LastTracks,
                runner.LastRegionCount,
                Interlocked.Read(ref runner.DetectionEvents),
                runner.LastFailClosed,
                cap?.LastFrameUtc,
                Volatile.Read(ref runner.Error),
                cap?.Error));
        }

        return new EngineStatus(
            _running,
            _provider,
            _dmlError,
            _startedAt,
            monitors,
            Interlocked.Read(ref _totalAnalyses),
            Interlocked.Read(ref _totalDetections),
            Volatile.Read(ref _peakTracks),
            _overlay.IsRunning,
            _overlay.AllExcludedFromCapture,
            _error);
    }

    private void PersistSession()
    {
        try
        {
            if (_startedAt is not { } start) return;
            var end = DateTimeOffset.Now;
            var record = new SessionRecord
            {
                StartedUtc = start,
                EndedUtc = end,
                DurationSeconds = Math.Max(0, (end - start).TotalSeconds),
                MonitorCount = _runners.Count,
                DetectionCount = (int)Math.Min(int.MaxValue, Interlocked.Read(ref _totalDetections)),
                PeakTrackCount = Volatile.Read(ref _peakTracks),
                Style = _settings.Censoring.Style.ToString(),
                AppVersion = typeof(VeilDeskEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
            };
            new SessionStore().Append(record);
        }
        catch
        {
            // Session history is optional; never fail shutdown over it.
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        var copy = settings.Clone();
        SettingsValidator.ValidateAndClamp(copy);
        _settings = copy;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _capture.DisposeAsync();
        _overlay.Dispose();
    }

    private sealed class MonitorRunner : IDisposable
    {
        public MonitorRunner(MonitorDescriptor monitor, VisionPipeline pipeline)
        {
            Monitor = monitor;
            Pipeline = pipeline;
        }

        public MonitorDescriptor Monitor { get; }
        public VisionPipeline Pipeline { get; }
        public SceneChangeDetector Scene { get; } = new();
        public LatencyTracker Latency { get; } = new(256);
        public long Analyses;
        public long LastAnalysisMs;
        public long LastSnapshotMs;
        public long LastTracks;
        public long DetectionEvents;
        public int LastRegionCount;
        public bool LastFailClosed;
        public string? Error;
        public BitmapSource? Snapshot;

        public void Dispose()
        {
        }
    }

    private readonly record struct FrameOutcome(
        bool Analyzed, double AnalysisMs, int TrackCount, int Detections, string? Error, double Delta)
    {
        public static readonly FrameOutcome Skipped = new(false, 0, 0, 0, null, 0);
    }
}
