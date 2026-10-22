using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VeilDesk.Core.Export;

namespace AviProbe;

/// <summary>
/// Offline validation for the MJPEG AVI writer: renders real JPEG frames,
/// writes a CFR file, then asks Windows Media Foundation (via WPF MediaPlayer)
/// to open and decode it. No ffmpeg and no network access are involved.
/// </summary>
internal static class Program
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Fps = 15;
    private const int FrameCount = 30;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--inspect")
            return InspectExisting(Path.GetFullPath(args[1]));

        var source = args.Length > 0 && File.Exists(args[0]) ? args[0] : FindLatestScreenshot();
        var output = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetTempPath(), "veildesk-avi-probe.avi");
        var poster = Path.ChangeExtension(output, ".decoded.png");

        Console.WriteLine($"source: {source ?? "(synthetic colour bars)"}");
        Console.WriteLine($"output: {output}");
        var background = source is null ? null : Load(source);

        try
        {
            using (var writer = new MjpegAviWriter(output, Width, Height, Fps))
            {
                for (var i = 0; i < FrameCount; i++)
                {
                    var frame = RenderFrame(background, i);
                    writer.WriteFrame(EncodeJpeg(frame));
                }
                writer.Finish();
                Console.WriteLine($"written: {writer.FrameCount} frames, {writer.Length:N0} bytes");
            }

            var playback = PlaybackCheck(output, poster);
            Console.WriteLine(playback.Opened
                ? $"playback: MEDIA_OPENED duration={playback.Duration} decoder={playback.Decoder}"
                : $"playback: FAILED {playback.Error}");
            if (playback.PosterNonBlackPercent is { } percent)
                Console.WriteLine($"poster: {poster} nonBlack={percent:0.0}%");
            return playback.Opened && playback.PosterNonBlackPercent is > 50 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("AVI probe failed: " + ex);
            return 1;
        }
    }

    private static BitmapSource RenderFrame(BitmapSource? background, int index)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background is not null)
            {
                dc.DrawImage(background, new Rect(0, 0, Width, Height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null,
                    new Rect(0, 0, Width, Height));
            }
            else
            {
                var brush = new LinearGradientBrush(
                    Color.FromRgb(12, 16, 28), Color.FromRgb(70, 54, 140), 25);
                dc.DrawRectangle(brush, null, new Rect(0, 0, Width, Height));
                for (var i = 0; i < 8; i++)
                {
                    var color = Color.FromRgb((byte)(40 + i * 25), (byte)(80 + i * 15), (byte)(170 - i * 12));
                    dc.DrawRectangle(new SolidColorBrush(color), null,
                        new Rect(i * Width / 8.0, Height - 34, Width / 8.0, 34));
                }
            }

            var x = (index * (Width - 160) / (FrameCount - 1.0));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 255, 82, 110)), null,
                new Rect(x, 70, 160, 100));
            dc.DrawText(new FormattedText($"VEILDESK AVI · frame {index + 1:00}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 26,
                    Brushes.White, 1.0),
                new Point(24, 24));
            dc.DrawText(new FormattedText($"{Width}x{Height} · {Fps} fps · MJPEG",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15,
                    Brushes.White, 1.0),
                new Point(26, 58));
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? FindLatestScreenshot()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VeilDesk");
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.png")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static int InspectExisting(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("AVI not found: " + path);
            return 1;
        }

        var info = ReadAviInfo(path);
        Console.WriteLine($"inspect: {path}");
        Console.WriteLine($"  bytes={info.FileBytes:N0} frames={info.FrameCount} " +
                          $"moviBytes={info.MoviBytes:N0} idx1={(info.HasIndex ? "yes" : "no")}");
        var poster = Path.ChangeExtension(path, ".decoded.png");
        var playback = PlaybackCheck(path, poster, info.Width, info.Height);
        Console.WriteLine(playback.Opened
            ? $"  playback: MEDIA_OPENED duration={playback.Duration} decoder={playback.Decoder}"
            : $"  playback: FAILED {playback.Error}");
        if (playback.PosterNonBlackPercent is { } percent)
            Console.WriteLine($"  poster: {poster} nonBlack={percent:0.0}%");
        return playback.Opened && playback.PosterNonBlackPercent is > 50 ? 0 : 2;
    }

    private sealed record AviInfo(long FileBytes, int FrameCount, long MoviBytes, bool HasIndex,
        int Width, int Height);

    private static AviInfo ReadAviInfo(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || Ascii(data, 0) != "RIFF" || Ascii(data, 8) != "AVI ")
            throw new InvalidDataException("Not a RIFF AVI file.");
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        if (riffSize + 8 > data.Length)
            throw new InvalidDataException("RIFF size exceeds the file length.");

        var frameCount = 0;
        long moviBytes = 0;
        var hasIndex = false;
        var width = 0;
        var height = 0;
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var id = Ascii(data, pos);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            var payload = pos + 8;
            if (id == "LIST" && payload + 4 <= data.Length)
            {
                var type = Ascii(data, payload);
                if (type == "movi")
                {
                    moviBytes = size;
                    var child = payload + 4;
                    var end = payload + size;
                    while (child + 8 <= end)
                    {
                        var childId = Ascii(data, child);
                        var childSize = BinaryPrimitives.ReadUInt32LittleEndian(
                            data.AsSpan(child + 4, 4));
                        if (childId == "00dc") frameCount++;
                        child += 8 + (int)childSize + ((int)childSize & 1);
                    }
                }
                else if (type == "hdrl" && size >= 56)
                {
                    // avih follows the hdrl FOURCC: size, then the 56-byte header.
                    var avih = payload + 4;
                    if (avih + 8 + 36 <= data.Length && Ascii(data, avih) == "avih")
                    {
                        width = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                            data.AsSpan(avih + 8 + 32, 4));
                        height = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                            data.AsSpan(avih + 8 + 36, 4));
                    }
                }
            }
            else if (id == "idx1")
            {
                hasIndex = true;
            }
            pos += 8 + (int)size + ((int)size & 1);
        }
        return new AviInfo(data.Length, frameCount, moviBytes, hasIndex, width, height);
    }

    private static string Ascii(byte[] data, int offset)
        => Encoding.ASCII.GetString(data, offset, 4);

    private sealed record PlaybackResult(bool Opened, string? Error, string Duration,
        string? Decoder, double? PosterNonBlackPercent);

    private static PlaybackResult PlaybackCheck(string path, string posterPath,
        int requestedWidth = Width, int requestedHeight = Height)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        var opened = false;
        var failed = false;
        string? error = null;
        var duration = "-";

        player.MediaOpened += (_, _) =>
        {
            opened = true;
            duration = player.NaturalDuration.HasTimeSpan
                ? player.NaturalDuration.TimeSpan.ToString(@"hh\:mm\:ss\.fff")
                : "unknown";
        };
        player.MediaFailed += (_, e) =>
        {
            failed = true;
            error = e.ErrorException?.Message ?? "MediaFailed";
        };

        player.Open(new Uri(path, UriKind.Absolute));
        Wait(TimeSpan.FromSeconds(8), () => opened || failed);
        if (!opened)
        {
            player.Close();
            return new PlaybackResult(false, failed ? error : "Timed out waiting for MediaOpened",
                duration, null, null);
        }

        var videoWidth = player.NaturalVideoWidth > 0 ? player.NaturalVideoWidth : requestedWidth;
        var videoHeight = player.NaturalVideoHeight > 0 ? player.NaturalVideoHeight : requestedHeight;
        // The probe samples shortly after opening; WMF may need a moment to
        // produce the first decoded frame at the requested position.
        player.Play();
        Wait(TimeSpan.FromMilliseconds(900), () => false);
        player.Pause();
        player.Position = TimeSpan.FromMilliseconds(250);
        Wait(TimeSpan.FromMilliseconds(350), () => false);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, videoWidth, videoHeight));
            dc.DrawVideo(player, new Rect(0, 0, videoWidth, videoHeight));
        }
        var decoded = new RenderTargetBitmap(videoWidth, videoHeight, 96, 96, PixelFormats.Pbgra32);
        decoded.Render(visual);
        decoded.Freeze();

        var pixels = new byte[videoWidth * videoHeight * 4];
        decoded.CopyPixels(pixels, videoWidth * 4, 0);
        var nonBlack = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8 || pixels[i + 1] > 8 || pixels[i + 2] > 8) nonBlack++;
        }
        var percent = 100.0 * nonBlack / (videoWidth * videoHeight);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoded));
        using (var stream = File.Create(posterPath)) encoder.Save(stream);

        var decoder = player.NaturalVideoWidth > 0
            ? $"{player.NaturalVideoWidth}x{player.NaturalVideoHeight}"
            : "unknown";
        player.Close();
        return new PlaybackResult(true, null, duration, decoder, percent);
    }

    private static void Wait(TimeSpan timeout, Func<bool> finished)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        var start = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            if (finished() || DateTime.UtcNow - start >= timeout)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
