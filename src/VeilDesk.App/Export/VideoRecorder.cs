using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.App.Overlay;
using VeilDesk.Core.Export;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;

namespace VeilDesk.App.Export;

/// <summary>
/// Records a censored, downscaled MJPEG AVI from the newest local capture
/// frame. Frames are composited with the same <see cref="ExportService"/>
/// renderer used for screenshots. Coverage still depends on detection and
/// capture freshness; this is not a zero-exposure guarantee. Output is capped at 1280x800 and
/// 15 fps to keep CPU and disk use predictable on integrated GPUs.
/// </summary>
public sealed class VideoRecorder
{
    public const int FramesPerSecond = 15;
    public const int MaxWidth = 1280;
    public const int MaxHeight = 800;

    private readonly IRecordingSource _engine;
    private readonly AppSettings _settings;
    private readonly string _monitorId;
    private readonly bool _includeCensorship;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private MjpegAviWriter? _writer;
    private string? _path;
    private bool _started;
    private readonly object _lifecycle = new();
    private Task<string>? _stopTask;
    private volatile string? _error;
    private long _framesWritten;
    private long _sourceFrames;
    private long _startTimestamp;
    private long _endTimestamp;

    public VideoRecorder(IRecordingSource engine, AppSettings settings, string monitorId,
        bool includeCensorship, Dispatcher? dispatcher = null)
    {
        _engine = engine;
        _settings = settings.Clone();
        _monitorId = monitorId;
        _includeCensorship = includeCensorship;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public string? LastError => _error;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Captured (unique) frames; lower than <see cref="FramesWritten"/> when the timeline is filled.</summary>
    public long SourceFrames => Interlocked.Read(ref _sourceFrames);

    /// <summary>Measured capture rate of the recorder, in frames per second.</summary>
    public double MeasuredSourceFps
    {
        get
        {
            var source = SourceFrames;
            if (source <= 0 || _startTimestamp == 0) return 0;
            var end = Interlocked.Read(ref _endTimestamp);
            var seconds = Stopwatch.GetElapsedTime(_startTimestamp, end == 0 ? Stopwatch.GetTimestamp() : end).TotalSeconds;
            return seconds > 0 ? source / seconds : 0;
        }
    }

    public Task<string> StartAsync()
    {
        lock (_lifecycle)
        {
            if (_started) throw new InvalidOperationException("Recording is already running.");
            if (!_engine.IsRunning) throw new InvalidOperationException("Protection is not running.");
            var monitor = _engine.GetActiveMonitors().FirstOrDefault(m =>
                string.Equals(m.Id, _monitorId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected display is not being captured.");

            var scale = Math.Min(1.0, Math.Min((double)MaxWidth / monitor.Width,
                (double)MaxHeight / monitor.Height));
            var width = Math.Max(2, (int)Math.Round(monitor.Width * scale / 2) * 2);
            var height = Math.Max(2, (int)Math.Round(monitor.Height * scale / 2) * 2);

            var directory = string.IsNullOrWhiteSpace(_settings.Creator.ExportDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VeilDesk")
                : Environment.ExpandEnvironmentVariables(_settings.Creator.ExportDirectory);
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory,
                $"VeilDesk-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}-{Sanitize(monitor.FriendlyName)}.avi");
            _writer = new MjpegAviWriter(_path, width, height, FramesPerSecond);
            _startTimestamp = Stopwatch.GetTimestamp();
            _started = true;
            _loop = Task.Run(() => RecordLoopAsync(width, height, _cts.Token));
            return Task.FromResult(_path);
        }
    }

    public Task<string> StopAsync()
    {
        lock (_lifecycle)
        {
            if (!_started)
                throw new InvalidOperationException("Recording was not started.");
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task<string> StopCoreAsync()
    {
        _cts.Cancel();
        try
        {
            if (_loop is not null)
                await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
        }

        if (!string.IsNullOrEmpty(_error))
            throw new InvalidOperationException("Recording stopped with an error: " + _error + " (" + _path + ")");
        return _path!;
    }

    private async Task RecordLoopAsync(int width, int height, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
        // The AVI timeline is fixed at FramesPerSecond. Capture on this machine
        // can be slower than that, so a captured frame is repeated until the
        // timeline catches up with the wall clock. Without this the file would
        // declare 15 fps while containing fewer frames and would play back
        // faster than the recorded session.
        long timelineFrames = 0;
        (long Number, long Time)? previousSource = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var capture = _engine.TryGrabRecordingFrame(_monitorId);
                    if (capture is null)
                    {
                        await Task.Delay(15, token);
                        continue;
                    }

                    var bitmap = FrameImage.ToBitmap(capture.Frame);
                    BitmapSource output = bitmap;
                    if (_includeCensorship)
                    {
                        output = await _dispatcher.InvokeAsync(() => ExportService.Composite(
                            bitmap, capture.Monitor, capture.Regions, _settings, width, height), DispatcherPriority.Background, token);
                    }
                    else if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                    {
                        var scaled = new TransformedBitmap(bitmap,
                            new System.Windows.Media.ScaleTransform(
                                (double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight));
                        scaled.Freeze();
                        output = scaled;
                    }

                    var jpeg = await Task.Run(() => EncodeJpeg(output), token);
                    var elapsedSeconds = Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
                    var targetFrames = Math.Max(1, (long)Math.Floor(elapsedSeconds * FramesPerSecond) + 1);
                    while (!token.IsCancellationRequested && timelineFrames < targetFrames)
                    {
                        _writer!.WriteFrame(jpeg);
                        timelineFrames++;
                        Interlocked.Increment(ref _framesWritten);
                    }
                    var sourceKey = (capture.Frame.FrameNumber, capture.Frame.TimestampMs);
                    if (previousSource != sourceKey)
                    {
                        previousSource = sourceKey;
                        Interlocked.Increment(ref _sourceFrames);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    break;
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = interval - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try { await Task.Delay(remaining, token); } catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            // The loop owns the writer. Stop never disposes it underneath an
            // in-flight compositor, encoder or frame write.
            try { _writer?.Dispose(); }
            catch (Exception ex) { _error ??= ex.Message; }
            _writer = null;
            Interlocked.Exchange(ref _endTimestamp, Stopwatch.GetTimestamp());
        }
    }

    private static byte[] EncodeJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        return string.IsNullOrEmpty(result) ? "display" : result;
    }
}
