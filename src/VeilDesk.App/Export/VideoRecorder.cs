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
/// renderer used for screenshots, so a recorded file cannot leak an
/// uncensored crop. The recorder deliberately caps output at 1280x800 and
/// 15 fps to keep CPU and disk use predictable on integrated GPUs.
/// </summary>
public sealed class VideoRecorder
{
    public const int FramesPerSecond = 15;
    public const int MaxWidth = 1280;
    public const int MaxHeight = 800;

    private readonly VeilDeskEngine _engine;
    private readonly AppSettings _settings;
    private readonly string _monitorId;
    private readonly bool _includeCensorship;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private MjpegAviWriter? _writer;
    private string? _path;
    private bool _started;
    private bool _stopping;
    private volatile string? _error;
    private long _framesWritten;

    public VideoRecorder(VeilDeskEngine engine, AppSettings settings, string monitorId,
        bool includeCensorship)
    {
        _engine = engine;
        _settings = settings.Clone();
        _monitorId = monitorId;
        _includeCensorship = includeCensorship;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public string? LastError => _error;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public async Task<string> StartAsync()
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
            $"VeilDesk-{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(monitor.FriendlyName)}.avi");
        _writer = new MjpegAviWriter(_path, width, height, FramesPerSecond);
        _started = true;
        _loop = Task.Run(() => RecordLoopAsync(width, height, _cts.Token));
        await Task.CompletedTask;
        return _path;
    }

    public async Task<string> StopAsync()
    {
        if (!_started)
            return _path ?? throw new InvalidOperationException("Recording was not started.");
        if (_stopping)
            return _path!;
        _stopping = true;
        _cts.Cancel();
        try
        {
            if (_loop is not null)
                await _loop.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
            _error ??= "The recorder did not stop within four seconds.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try { _writer?.Dispose(); }
            catch (Exception ex) { _error ??= ex.Message; }
            _writer = null;
            _cts.Dispose();
        }

        if (!string.IsNullOrEmpty(_error))
            throw new InvalidOperationException("Recording stopped with an error: " + _error + " (" + _path + ")");
        return _path!;
    }

    private async Task RecordLoopAsync(int width, int height, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
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

                // Capture is already fixed at 15 fps; duplicate only when the
                // source has not produced a new frame so the AVI stays CFR.
                var bitmap = FrameImage.ToBitmap(capture.Frame);
                BitmapSource output = bitmap;
                if (_includeCensorship)
                {
                    output = await _dispatcher.InvokeAsync(() => ExportService.Composite(
                        bitmap, capture.Monitor, capture.Regions, _settings, width, height));
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
                _writer?.WriteFrame(jpeg);
                Interlocked.Increment(ref _framesWritten);
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
