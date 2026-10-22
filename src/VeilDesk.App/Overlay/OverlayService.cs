using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;

namespace VeilDesk.App.Overlay;

/// <summary>
/// UI-thread owner of the per-monitor overlay windows. The engine may call
/// Render from a worker through the dispatcher; updates are coalesced so a
/// slow inference pass can never build a backlog of stale overlays.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingFrame> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _timer;
    private bool _disposed;
    private long _lastTopmostMs;

    public OverlayService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsRunning { get; private set; }
    public bool AllExcludedFromCapture { get; private set; }

    public void Start(IReadOnlyList<MonitorDescriptor> monitors)
    {
        Stop();
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => Start(monitors));
            return;
        }

        foreach (var monitor in monitors)
        {
            var window = new OverlayWindow(monitor);
            _windows[monitor.Id] = window;
            window.ShowOverlay();
        }
        IsRunning = true;
        AllExcludedFromCapture = _windows.Values.All(w => w.IsExcludedFromCapture);

        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public void Stop()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Stop);
            return;
        }

        _timer?.Stop();
        _timer = null;
        foreach (var window in _windows.Values)
        {
            try { window.Close(); } catch { }
        }
        _windows.Clear();
        IsRunning = false;
        lock (_pending) _pending.Clear();
        AllExcludedFromCapture = false;
    }

    /// <summary>Queues the latest regions; older queued regions are replaced.</summary>
    public void Render(string monitorId, IReadOnlyList<CensorRegion> regions, AppSettings settings,
        BitmapSource? snapshot)
    {
        if (!IsRunning || _disposed) return;
        lock (_pending)
        {
            if (!IsRunning || _disposed) return;
            _pending[monitorId] = new PendingFrame(regions, settings, snapshot);
        }
    }

    public void RenderImmediate(string monitorId, IReadOnlyList<CensorRegion> regions, AppSettings settings)
    {
        _dispatcher.VerifyAccess();
        if (_windows.TryGetValue(monitorId, out var window)) window.Render(regions, settings, null);
    }

    public void Clear(string monitorId)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Clear(monitorId));
            return;
        }
        if (_windows.TryGetValue(monitorId, out var window)) window.Clear();
    }

    private void Flush()
    {
        if (!IsRunning) return;
        List<KeyValuePair<string, PendingFrame>> frames;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            frames = _pending.ToList();
            _pending.Clear();
        }

        var now = Environment.TickCount64;
        if (now - _lastTopmostMs >= 1000)
        {
            // Fullscreen apps and games may join the topmost band after the
            // overlay was created. Reassert once per second without activating.
            _lastTopmostMs = now;
            foreach (var window in _windows.Values)
            {
                try { window.ShowOverlay(); } catch { }
            }
        }

        foreach (var (monitorId, frame) in frames)
        {
            if (_windows.TryGetValue(monitorId, out var window))
                window.Render(frame.Regions, frame.Settings, frame.Snapshot);
        }
    }

    /// <summary>Reasserts topmost and updates DPI after a display change.</summary>
    public void ReassertTopmost()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(ReassertTopmost);
            return;
        }
        foreach (var window in _windows.Values) window.ShowOverlay();
    }

    private sealed record PendingFrame(IReadOnlyList<CensorRegion> Regions, AppSettings Settings,
        BitmapSource? Snapshot);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
