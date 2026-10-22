using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Vision;
using VeilDesk.Inference;

namespace VeilDesk.Tools.FaceScrollProbe;

/// <summary>
/// Measures censorship precision over reproducible scroll sequences.
///
/// The sequences are built from the licensed NASA fixture. No screen is
/// captured, no browser is opened and nothing is uploaded. The tool reports
/// true-face coverage, mask area that is not needed, ghost-mask duration and
/// the largest mask relative to the display.
/// </summary>
internal static class Program
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int FaceSource = 512;
    private const double FaceX = 178;
    private const double FaceY = 64;
    private const double FaceW = 94;
    private const double FaceH = 112;

    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var output = args.ElementAtOrDefault(1);
        var modelDirectory = Path.Combine(root, "models");
        if (!File.Exists(Path.Combine(modelDirectory, "face_detection_yunet_2023mar.onnx")))
        {
            Console.Error.WriteLine("The YuNet model is missing. Run scripts/fetch-models.ps1 first.");
            return 2;
        }

        var settings = new AppSettings();
        settings.Detection.EnabledRegions = new() { BodyRegion.Face };
        settings.Detection.FaceConfidence = 0.6;
        settings.Performance.UseDirectML = false;
        settings.Performance.InferenceThreads = 2;

        var options = VisionPipelineOptions.ForSettings(modelDirectory, settings);
        using var models = VisionModels.Load(options);
        using var pipeline = new VisionPipeline(options, models);

        var astronaut = LoadAstronaut(root);
        var background = BuildBackground(Width, Height);

        var sequences = new List<Sequence>
        {
            new("stationary", Enumerable.Range(0, 12).Select(i => 0.0).ToArray(), 900),
            new("slow-scroll", Enumerable.Range(0, 24).Select(i => i * 25.0).ToArray(), 900),
            new("fast-scroll", Enumerable.Range(0, 24).Select(i => i * 160.0).ToArray(), 900),
            new("scroll-stop", Enumerable.Range(0, 24).Select(i => i < 12 ? i * 60.0 : 660.0).ToArray(), 900),
            new("scroll-reversal", Enumerable.Range(0, 24).Select(i => i < 12 ? i * 90.0 : (11 - i) * 90.0 + 990.0).ToArray(), 900),
            new("face-enters", Enumerable.Range(0, 20).Select(i => 1200.0 - i * 90.0).ToArray(), 1200),
            new("face-leaves", Enumerable.Range(0, 20).Select(i => i * 90.0).ToArray(), 900),
            new("two-faces", Enumerable.Range(0, 20).Select(i => i * 70.0).ToArray(), 900),
            new("new-content", Enumerable.Range(0, 20).Select(i => i * 40.0).ToArray(), 900),
            new("large-face", Enumerable.Range(0, 16).Select(i => i * 45.0).ToArray(), 900),
            new("partial-face", Enumerable.Range(0, 12).Select(i => 0.0).ToArray(), -83),
            new("video-motion", Enumerable.Range(0, 16).Select(i => i * 30.0).ToArray(), 900),
        };

        var results = new List<object>();
        foreach (var sequence in sequences)
            results.Add(Run(sequence, pipeline, settings, astronaut, background));

        // Cost of the motion estimator on a full 2560x1600 capture.
        var motionCost = MeasureMotionCost(background, astronaut);

        var report = new
        {
            recordedUtc = DateTimeOffset.UtcNow,
            display = new { Width, Height },
            note = "Synthetic sequences built from the licensed NASA fixture. " +
                   "Geometry and timing measurements only; not a pixel or display exposure test.",
            motionEstimator = motionCost,
            sequences = results
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, json);
            Console.WriteLine($"Wrote {output}");
        }
        else
        {
            Console.WriteLine(json);
        }
        return 0;
    }

    private static object Run(Sequence sequence, VisionPipeline pipeline, AppSettings settings,
        byte[] astronaut, byte[] background)
    {
        var bounds = new PixelRect(0, 0, Width, Height);
        pipeline.StartProtection(settings, Environment.TickCount64);

        var faceSize = sequence.LargeFace ? 520 : sequence.PartialFace ? 900 : 190;
        var faceX = sequence.LargeFace ? 700 : sequence.PartialFace ? 500 : 860;
        var scale = faceSize / (double)FaceSource;
        var truthW = FaceW * scale;
        var truthH = FaceH * scale;

        var covered = 0;
        var observations = 0;
        var unnecessaryArea = 0.0;
        var maskArea = 0.0;
        var maxMaskFraction = 0.0;
        var maxMaskSize = (0, 0);
        var ghostMs = -1;
        var departedAt = -1;
        var detections = 0;
        var frames = 0;
        var latencies = new List<double>();
        var errors = 0;
        var rejected = 0;
        var accepted = 0;
        var samples = new List<object>();
        var legacyGhostMs = -1;
        var legacyGhostEnd = -1;
        var legacyMaxFraction = 0.0;
        var legacyMaxSize = (0, 0);
        var legacyUnnecessary = 0.0;

        for (var index = 0; index < sequence.Offsets.Length; index++)
        {
            var offset = sequence.Offsets[index];
            var faceTop = (int)Math.Round(sequence.StartTop - offset);
            var truth = new PixelRect(faceX + FaceX * scale, faceTop + FaceY * scale, truthW, truthH);
            var visible = truth.Clip(bounds);

            using var frame = Build(background, astronaut, sequence, offset, faceTop, faceSize, faceX);
            var timestamp = 1000 + index * 83;
            pipeline.ReportFrame(timestamp);
            var result = pipeline.Analyze(frame, settings, Environment.TickCount64);
            frames++;
            if (result.Error is not null) errors++;
            latencies.Add(result.TotalMs);
            detections += result.FaceDetections.Count;

            var masks = result.Regions.Where(r => r.Source == BodyRegion.Face).ToList();
            rejected += result.FaceVerdicts.Count(v => v.Decision != FaceCandidateDecision.Accepted);
            accepted += result.FaceVerdicts.Count(v => v.Decision == FaceCandidateDecision.Accepted);
            foreach (var mask in masks)
            {
                var area = mask.Box.Area;
                maskArea += area;
                var fraction = area / (Width * (double)Height);
                if (fraction > maxMaskFraction)
                {
                    maxMaskFraction = fraction;
                    maxMaskSize = ((int)Math.Round(mask.Box.Width), (int)Math.Round(mask.Box.Height));
                }
                if (visible.IsEmpty) continue;
                unnecessaryArea += Math.Max(0, area - mask.Box.IntersectionArea(visible));
            }

            if (!visible.IsEmpty)
            {
                observations++;
                if (masks.Any(m => m.Box.IntersectionArea(visible) >= visible.Area * 0.95)) covered++;
            }

            // The face is gone once its box left the display.
            if (visible.IsEmpty && departedAt < 0) departedAt = index * 83;
            if (departedAt >= 0 && index * 83 > departedAt)
            {
                var stale = masks.Any(m => m.Box.IntersectionArea(new PixelRect(
                    truth.X, truth.Y, truth.Width, truth.Height)) > 0);
                if (!stale && ghostMs < 0) ghostMs = index * 83 - departedAt;
            }

            if (samples.Count < 6)
                samples.Add(new
                {
                    Frame = index,
                    Truth = $"{truth.X:0},{truth.Y:0} {truth.Width:0}x{truth.Height:0}",
                    Candidates = result.FaceCandidates.Select(c => new
                    {
                        Box = $"{c.Box.X:0},{c.Box.Y:0} {c.Box.Width:0}x{c.Box.Height:0}",
                        Confidence = c.Confidence,
                        Quality = c.Evidence.Quality.ToString(),
                        Reason = c.Evidence.Reason
                    }),
                    Verdicts = result.FaceVerdicts.Select(v => $"{v.Decision}:{v.Reason}"),
                    Masks = masks.Select(m => $"{m.Box.X:0},{m.Box.Y:0} {m.Box.Width:0}x{m.Box.Height:0}")
                });

            // The previous release kept a missed face at its last position
            // until the persistence window expired.
            foreach (var track in result.Tracks.Where(t => t.Region == BodyRegion.Face))
            {
                if (index * 83 - track.LastSeenMs > settings.Censoring.TrackPersistenceMs) continue;
                var legacy = LegacyMask(track, settings, bounds);
                var fraction = legacy.Area / (Width * (double)Height);
                if (fraction > legacyMaxFraction)
                {
                    legacyMaxFraction = fraction;
                    legacyMaxSize = ((int)Math.Round(legacy.Width), (int)Math.Round(legacy.Height));
                }
                if (!visible.IsEmpty)
                    legacyUnnecessary += Math.Max(0, legacy.Area - legacy.IntersectionArea(visible));
                if (departedAt >= 0 &&
                    index * 83 - track.LastSeenMs <= settings.Censoring.TrackPersistenceMs)
                    legacyGhostEnd = index * 83;
            }
        }
        if (departedAt >= 0 && legacyGhostEnd >= 0)
            legacyGhostMs = legacyGhostEnd - departedAt;

        pipeline.StopProtection();

        var ordered = latencies.OrderBy(v => v).ToArray();
        return new
        {
            sequence.Name,
            Frames = frames,
            FaceObservations = observations,
            FaceCovered = covered,
            Coverage = observations == 0 ? 0 : covered / (double)observations,
            Detections = detections,
            CandidatesAccepted = accepted,
            CandidatesRejected = rejected,
            MeanMaskArea = frames == 0 ? 0 : maskArea / frames,
            UnnecessaryMaskArea = unnecessaryArea / frames,
            UnnecessaryMaskFraction = unnecessaryArea / (Width * (double)Height * frames),
            MaximumMaskScreenFraction = maxMaskFraction,
            MaximumMaskSize = $"{maxMaskSize.Item1}x{maxMaskSize.Item2}",
            GhostMaskMs = ghostMs,
            LegacyGhostMaskMs = legacyGhostMs,
            LegacyMaximumMaskScreenFraction = legacyMaxFraction,
            LegacyMaximumMaskSize = $"{legacyMaxSize.Item1}x{legacyMaxSize.Item2}",
            LegacyUnnecessaryMaskFraction = legacyUnnecessary / (Width * (double)Height * frames),
            Errors = errors,
            AnalysisP50Ms = ordered.Length == 0 ? 0 : ordered[ordered.Length / 2],
            AnalysisP95Ms = ordered.Length == 0 ? 0 : ordered[(int)(ordered.Length * 0.95)],
            Samples = samples
        };
    }

    /// <summary>
    /// The mask geometry of the previous release, computed from the same track
    /// state. It reproduces the rule that grew the margin of the swept
    /// rectangle and kept a missed face at its last position for the whole
    /// persistence window. It exists so that the same fixture can be measured
    /// before and after; it is not used by the application.
    /// </summary>
    private static PixelRect LegacyMask(VeilDesk.Core.Tracking.TrackedDetection track,
        AppSettings settings, PixelRect bounds)
    {
        var observed = track.Box;
        var margin = settings.Detection.FacePaddingFraction +
                     (track.Confidence >= settings.Detection.FaceConfidence ? 0 : 0.12);
        var lookAhead = Math.Clamp(settings.Censoring.PredictionHorizonMs, 0, 500) / 1000.0;
        var dx = Math.Clamp(track.VelocityX * lookAhead, -observed.Width * .25, observed.Width * .25);
        var dy = Math.Clamp(track.VelocityY * lookAhead, -observed.Height * .25, observed.Height * .25);
        var swept = observed.Union(observed.Offset(dx, dy));
        var px = observed.Width * margin;
        var py = observed.Height * margin;
        return new PixelRect(swept.X - px, swept.Y - py, swept.Width + 2 * px, swept.Height + 2 * py)
            .Expand(settings.Censoring.MaskExpansion).Clip(bounds);
    }

    private static object MeasureMotionCost(byte[] background, byte[] astronaut)
    {
        const int w = 2560;
        const int h = 1600;
        var source = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var row = (y % (background.Length / (Width * 4))) * Width * 4;
            Array.Copy(background, row, source, y * w * 4, Width * 4);
        }
        var pixels = new byte[w * h * 4];
        var estimator = new SceneMotionEstimator();
        var samples = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            // Shift the content a little so the estimator has real work.
            for (var y = 0; y < h; y++)
            {
                var from = ((y + i * 3) % h) * w * 4;
                Array.Copy(source, from, pixels, y * w * 4, w * 4);
            }
            var watch = Stopwatch.StartNew();
            estimator.Estimate(pixels, w, h, w * 4);
            watch.Stop();
            if (i >= 5) samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return new
        {
            Display = $"{w}x{h}",
            Passes = samples.Count,
            P50Ms = samples.Count == 0 ? 0 : samples[samples.Count / 2],
            P95Ms = samples.Count == 0 ? 0 : samples[(int)(samples.Count * 0.95)]
        };
    }

    private sealed record Sequence(string Name, double[] Offsets, double StartTop)
    {
        public bool LargeFace => Name == "large-face";
        public bool PartialFace => Name == "partial-face";
    }

    private static VideoFrame Build(byte[] background, byte[] astronaut, Sequence sequence,
        double offset, int faceTop, int faceSize, int faceX)
    {
        var frame = VideoFrame.Rent(Width, Height, Width * 4, 1, Environment.TickCount64, "scroll-fixture", new PixelRect(0, 0, Width, Height));
        var pixels = frame.Pixels;
        Array.Copy(background, pixels, background.Length);

        // Two faces for the multi-face sequence.
        Paste(astronaut, pixels, faceX, faceTop, faceSize);
        if (sequence.Name == "two-faces")
            Paste(astronaut, pixels, 220, (int)Math.Round(sequence.StartTop - offset) + 120, 150);

        if (sequence.Name == "new-content" && offset > 0)
        {
            // A completely different page replaces the old one.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var v = (byte)((i / 4 + (int)offset) % 251);
                pixels[i] = v;
                pixels[i + 1] = (byte)(255 - v);
                pixels[i + 2] = (byte)(v / 2);
            }
            Paste(astronaut, pixels, faceX, (int)Math.Round(sequence.StartTop - offset), faceSize);
        }

        if (sequence.Name == "video-motion")
        {
            // Content that changes completely every frame, like a playing
            // video. No stable displacement can be measured.
            var random = new Random(1000 + (int)offset);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var v = (byte)random.Next(0, 255);
                pixels[i] = v;
                pixels[i + 1] = (byte)(255 - v);
                pixels[i + 2] = (byte)(v / 2);
            }
            Paste(astronaut, pixels, faceX, (int)Math.Round(sequence.StartTop - offset), faceSize);
        }
        return frame;
    }

    private static void Paste(byte[] source, byte[] target, int x, int y, int size)
    {
        var scale = size / (double)FaceSource;
        for (var sy = 0; sy < size; sy++)
        {
            var destY = y + sy;
            if (destY < 0 || destY >= Height) continue;
            for (var sx = 0; sx < size; sx++)
            {
                var destX = x + sx;
                if (destX < 0 || destX >= Width) continue;
                var si = ((int)(sy / scale) * FaceSource + (int)(sx / scale)) * 4;
                var di = (destY * Width + destX) * 4;
                target[di] = source[si];
                target[di + 1] = source[si + 1];
                target[di + 2] = source[si + 2];
                target[di + 3] = 255;
            }
        }
    }

    private static byte[] BuildBackground(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var random = new Random(20261022);
        for (var i = 0; i < width * height; i++)
        {
            var shade = (byte)(200 + random.Next(0, 40));
            pixels[i * 4] = shade;
            pixels[i * 4 + 1] = shade;
            pixels[i * 4 + 2] = (byte)(shade - 10);
            pixels[i * 4 + 3] = 255;
        }
        for (var b = 0; b < 90; b++)
        {
            var x = random.Next(0, width - 220);
            var y = random.Next(0, height - 120);
            var w = random.Next(60, 220);
            var h = random.Next(20, 90);
            for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
            {
                var i = (yy * width + xx) * 4;
                pixels[i] = (byte)random.Next(60, 200);
                pixels[i + 1] = (byte)random.Next(60, 200);
                pixels[i + 2] = (byte)random.Next(60, 200);
            }
        }
        return pixels;
    }

    private static byte[] LoadAstronaut(string root)
    {
        var path = Path.Combine(root, "assets", "face-fixtures", "astronaut.png");
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path),
            System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var data = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(data, converted.PixelWidth * 4, 0);
        return data;
    }
}
