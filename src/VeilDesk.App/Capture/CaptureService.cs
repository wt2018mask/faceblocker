using System.Diagnostics;
using System.Windows.Threading;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Vision;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace VeilDesk.App.Capture;

public sealed record CaptureOptions(int FramesPerSecond = 15, bool CaptureCursor = false);

public sealed record CaptureStatus(
    string MonitorId,
    string MonitorName,
    int Width,
    int Height,
    long FramesDelivered,
    long FramesDropped,
    double MeasuredFps,
    DateTime? LastFrameUtc,
    string? Error)
{
    public bool HasRecentFrame => LastFrameUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(3);
}

/// <summary>
/// Owns Windows Graphics Capture for every selected display. All WinRT and
/// D3D11 objects live on one dedicated STA thread with a Dispatcher message
/// pump, because GraphicsCaptureItem/pool/session are apartment-affine. The
/// rest of the app only sees immutable <see cref="VideoFrame"/>s and status
/// snapshots. One frame is retained per display so a screenshot can be taken
/// even while the desktop is static and WGC is not producing new frames.
/// </summary>
public sealed class CaptureService : IAsyncDisposable
{
    private readonly object _lifecycle = new();
    private readonly List<MonitorCaptureSession> _sessions = new();

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private TaskCompletionSource? _dispatcherReady;
    private D3D11Context? _d3d;
    private CaptureOptions _options = new();
    private bool _running;
    private long _startedTicks;

    public bool IsRunning => Volatile.Read(ref _running);

    public async Task StartAsync(IReadOnlyList<MonitorDescriptor> monitors, CaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        await EnsureThreadAsync().ConfigureAwait(false);
        await _dispatcher!.InvokeAsync(() =>
        {
            StartCore(monitors, options);
            return true;
        }).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        try
        {
            await dispatcher.InvokeAsync(StopCore).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Dispatcher already shutting down.
        }
    }

    public IReadOnlyList<CaptureStatus> GetStatus()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return Array.Empty<CaptureStatus>();
        if (dispatcher.CheckAccess()) return BuildStatus();
        try
        {
            return dispatcher.Invoke(BuildStatus);
        }
        catch (TaskCanceledException)
        {
            return Array.Empty<CaptureStatus>();
        }
    }

    /// <summary>
    /// Returns a retained reference to the newest captured frame for a display.
    /// The caller must dispose it. Safe to call while capture is running.
    /// </summary>
    public VideoFrame? TryGrabLatestFrame(string monitorId)
    {
        MonitorCaptureSession? session;
        lock (_lifecycle) session = FindSession(monitorId);
        return session?.GrabLatestFrame();
    }

    public VideoFrame? TryGrabLatestFrameAny()
    {
        MonitorCaptureSession[] sessions;
        lock (_lifecycle) sessions = _sessions.ToArray();
        foreach (var session in sessions)
        {
            var frame = session.GrabLatestFrame();
            if (frame is not null) return frame;
        }
        return null;
    }

    /// <summary>Takes the newest unconsumed analysis frame for a display.</summary>
    public VideoFrame? TryTakeFrame(string monitorId)
    {
        MonitorCaptureSession? session;
        lock (_lifecycle) session = FindSession(monitorId);
        return session?.Mailbox.TakeLatest();
    }

    private MonitorCaptureSession? FindSession(string monitorId)
        => _sessions.FirstOrDefault(s =>
            string.Equals(s.Monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase));

    private async Task EnsureThreadAsync()
    {
        if (_thread is not null) return;
        lock (_lifecycle)
        {
            if (_thread is not null) return;
            _dispatcherReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(CaptureThreadMain)
            {
                Name = "VeilDesk WGC capture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        await _dispatcherReady!.Task.ConfigureAwait(false);
    }

    private void CaptureThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(_dispatcher));
        _dispatcherReady!.SetResult();
        try
        {
            Dispatcher.Run();
        }
        finally
        {
            StopCore();
            _d3d?.Dispose();
            _d3d = null;
        }
    }

    private void StartCore(IReadOnlyList<MonitorDescriptor> monitors, CaptureOptions options)
    {
        if (_running) StopCore();
        _options = options;
        _d3d ??= D3D11Context.Create();

        var available = MonitorEnumerator.EnumerateNative()
            .ToDictionary(m => m.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var requested in monitors)
        {
            if (!available.TryGetValue(requested.Id, out var native))
            {
                errors.Add($"{requested.FriendlyName}: display is no longer connected");
                continue;
            }

            try
            {
                var device = _d3d.WinRtDevice ?? throw new InvalidOperationException("Direct3D device is not available.");
                var item = WgcInterop.CreateItemForMonitor(native.Handle);
                if (item.Size.Width <= 0 || item.Size.Height <= 0)
                {
                    throw new InvalidOperationException("Capture item has an empty size.");
                }

                var pool = Direct3D11CaptureFramePool.Create(
                    device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    item.Size);
                var session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = options.CaptureCursor;

                var monitorSession = new MonitorCaptureSession(requested, item, pool, session, _d3d, options);
                pool.FrameArrived += monitorSession.OnFrameArrived;
                session.StartCapture();
                lock (_lifecycle) _sessions.Add(monitorSession);
            }
            catch (Exception ex)
            {
                errors.Add($"{requested.FriendlyName}: {ex.Message}");
            }
        }

        if (_sessions.Count == 0)
        {
            throw new InvalidOperationException(errors.Count > 0
                ? "No display could be captured. " + string.Join("; ", errors)
                : "No display could be captured.");
        }

        _startedTicks = Stopwatch.GetTimestamp();
        Volatile.Write(ref _running, true);
    }

    private void StopCore()
    {
        if (!_running && _sessions.Count == 0) return;
        Volatile.Write(ref _running, false);
        MonitorCaptureSession[] sessions;
        lock (_lifecycle)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // Shutdown must proceed even if a display disappeared mid-stop.
            }
        }
    }

    private List<CaptureStatus> BuildStatus()
    {
        MonitorCaptureSession[] sessions;
        lock (_lifecycle) sessions = _sessions.ToArray();
        var result = new List<CaptureStatus>(sessions.Length);
        foreach (var session in sessions)
        {
            result.Add(session.Status(_startedTicks));
        }
        return result;
    }

    public async Task ShutdownAsync()
    {
        await StopAsync().ConfigureAwait(false);
        var dispatcher = _dispatcher;
        var thread = _thread;
        if (dispatcher is null || thread is null) return;
        try
        {
            await dispatcher.InvokeAsync(() => dispatcher.InvokeShutdown()).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
        thread.Join(TimeSpan.FromSeconds(2));
        lock (_lifecycle)
        {
            _thread = null;
            _dispatcher = null;
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private sealed class MonitorCaptureSession : IDisposable
    {
        private readonly FrameMailbox _mailbox = new();
        private readonly object _frameGate = new();
        private readonly object _d3dGate = new();
        private readonly D3D11Context _d3d;
        private readonly long _minIntervalTicks;
        private readonly long _startTicks = Stopwatch.GetTimestamp();

        private VideoFrame? _latest;
        private nint _staging;
        private TextureDescription _stagingDesc;
        private long _lastProcessTicks;
        private long _lastFrameUtcTicks;
        private long _frames;
        private long _dropped;
        private long _frameNumber;
        private string? _error;
        private bool _disposed;

        public MonitorCaptureSession(MonitorDescriptor monitor, GraphicsCaptureItem item,
            Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, D3D11Context d3d,
            CaptureOptions options)
        {
            Monitor = monitor;
            Item = item;
            Pool = pool;
            Session = session;
            _d3d = d3d;
            _minIntervalTicks = Math.Max(1, Stopwatch.Frequency / Math.Clamp(options.FramesPerSecond, 1, 60));
        }

        public MonitorDescriptor Monitor { get; }
        public GraphicsCaptureItem Item { get; }
        public Direct3D11CaptureFramePool Pool { get; }
        public GraphicsCaptureSession Session { get; }
        public FrameMailbox Mailbox => _mailbox;
        public string? Error => Volatile.Read(ref _error);

        public void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;

                var now = Stopwatch.GetTimestamp();
                if (_lastProcessTicks != 0 && now - _lastProcessTicks < _minIntervalTicks)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                ProcessFrame(frame, now);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _error, ex.Message);
            }
        }

        private void ProcessFrame(Direct3D11CaptureFrame frame, long now)
        {
            TextureDescription desc;
            VideoFrame? local = null;
            try
            {
                lock (_d3dGate)
                {
                    desc = _d3d.Describe(frame.Surface);
                    if (desc.Width <= 0 || desc.Height <= 0) return;
                    if (_staging == 0 || _stagingDesc.Width != desc.Width ||
                        _stagingDesc.Height != desc.Height || _stagingDesc.Format != desc.Format)
                    {
                        if (_staging != 0) _d3d.ReleaseTexture(_staging);
                        _staging = _d3d.CreateStaging(desc);
                        _stagingDesc = desc;
                    }

                    var capturedAt = DateTime.UtcNow;
                    var timestampMs = (long)Stopwatch.GetElapsedTime(_startTicks, now).TotalMilliseconds;
                    local = VideoFrame.Rent(desc.Width, desc.Height, desc.Width * 4,
                        Interlocked.Increment(ref _frameNumber), timestampMs, Monitor.Id,
                        Monitor.Bounds, capturedAt);
                    _d3d.CopyTo(local.Pixels, desc.Width * 4, desc.Width, desc.Height,
                        frame.Surface, _staging);
                }

                // Retain a second reference for the screenshot slot before the
                // mailbox can hand the first reference to a consumer.
                local.Retain();
                // Publish transfers the original reference to the mailbox.
                _mailbox.Publish(local);
                VideoFrame? previous;
                lock (_frameGate)
                {
                    previous = _latest;
                    _latest = local;
                }
                previous?.Dispose();
                local = null;

                _lastProcessTicks = now;
                Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _frames);
                Volatile.Write(ref _error, null);
            }
            finally
            {
                local?.Dispose();
            }
        }

        public VideoFrame? GrabLatestFrame()
        {
            lock (_frameGate)
            {
                if (_latest is null) return null;
                _latest.Retain();
                return _latest;
            }
        }

        public CaptureStatus Status(long startedTicks)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedTicks).TotalSeconds;
            var frames = Interlocked.Read(ref _frames);
            var lastTicks = Interlocked.Read(ref _lastFrameUtcTicks);
            DateTime? last = lastTicks > 0 ? new DateTime(lastTicks, DateTimeKind.Utc) : null;
            var latest = _latest;
            return new CaptureStatus(
                Monitor.Id,
                Monitor.FriendlyName,
                latest?.Width ?? Monitor.Width,
                latest?.Height ?? Monitor.Height,
                frames,
                Interlocked.Read(ref _dropped),
                elapsed > 0.25 ? frames / elapsed : 0,
                last,
                Error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Pool.FrameArrived -= OnFrameArrived; } catch { }
            try { Session.Dispose(); } catch { }
            try { Pool.Dispose(); } catch { }
            lock (_frameGate)
            {
                _latest?.Dispose();
                _latest = null;
            }
            _mailbox.Clear();
            lock (_d3dGate)
            {
                if (_staging != 0)
                {
                    _d3d.ReleaseTexture(_staging);
                    _staging = 0;
                }
            }
        }
    }
}
