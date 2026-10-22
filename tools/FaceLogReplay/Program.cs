using System.Globalization;
using System.Text.Json;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Tools.FaceLogReplay;

/// <summary>
/// Replays a recorded acceptance log through the face pipeline.
///
/// The log contains the raw face candidates, the tracks and the censor masks
/// of an earlier run. The "before" numbers are the recorded masks; the "after"
/// numbers are produced by feeding the same raw candidates through the current
/// candidate gate, tracker and composer. Only geometry, scores and reasons are
/// read. No screen content is involved.
/// </summary>
internal static class Program
{
    private const string Marker = " [acceptance-analysis] ";

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: FaceLogReplay <log-root> <settings.json> [output.json]");
            return 2;
        }
        var root = Path.GetFullPath(args[0]);
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(args[1]),
            SettingsStore.JsonOptions) ?? throw new Exception("settings missing");
        SettingsValidator.ValidateAndClamp(settings);

        var records = Load(root);
        if (records.Count == 0) throw new Exception("no acceptance records found");

        var bounds = new PixelRect(0, 0, 2560, 1600);
        var before = MeasureRecorded(records, bounds);
        var after = Replay(records, settings, bounds);

        var report = new
        {
            recordedUtc = DateTimeOffset.UtcNow,
            logRoot = root,
            analyses = records.Count,
            note = "Geometry replay of a recorded session. The recorded masks are the " +
                   "behaviour of the previous release; the replayed masks come from the " +
                   "current candidate gate, tracker and composer. Not a pixel or display " +
                   "exposure test.",
            before,
            after
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length > 2)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
            File.WriteAllText(args[2], json);
            Console.WriteLine($"Wrote {args[2]}");
        }
        else
        {
            Console.WriteLine(json);
        }
        return 0;
    }

    private sealed record Record(
        DateTimeOffset Time,
        long Stamp,
        IReadOnlyList<DetectionRecord> Candidates,
        IReadOnlyList<(int TrackId, CensorRegionKind Kind, PixelRect Box)> Masks);

    private static List<Record> Load(string root)
    {
        var records = new List<Record>();
        var seen = new HashSet<string>();
        foreach (var path in Directory.EnumerateFiles(root, "app.log*", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(path))
            {
                var marker = line.IndexOf(Marker, StringComparison.Ordinal);
                if (marker < 0) continue;
                if (!seen.Add(line)) continue;
                using var document = JsonDocument.Parse(line[(marker + Marker.Length)..]);
                var element = document.RootElement;
                if (!element.TryGetProperty("faces", out var faces)) continue;
                if (!element.TryGetProperty("masks", out var masks)) continue;

                var time = DateTimeOffset.Parse(line[..marker], CultureInfo.InvariantCulture);
                var age = element.TryGetProperty("frameAgeMs", out var ageElement) ? ageElement.GetInt64() : 0;
                var stamp = time.ToUnixTimeMilliseconds() - age;

                var candidates = new List<DetectionRecord>();
                foreach (var face in faces.EnumerateArray())
                {
                    var box = face.GetProperty("Box");
                    candidates.Add(new DetectionRecord(BodyRegion.Face,
                        face.GetProperty("Confidence").GetDouble(),
                        new PixelRect(box.GetProperty("X").GetDouble(), box.GetProperty("Y").GetDouble(),
                            box.GetProperty("Width").GetDouble(), box.GetProperty("Height").GetDouble())));
                }

                var recorded = new List<(int, CensorRegionKind, PixelRect)>();
                foreach (var mask in masks.EnumerateArray())
                {
                    if (mask.GetProperty("Source").GetInt32() != (int)BodyRegion.Face) continue;
                    if (!mask.TryGetProperty("TrackId", out var id) || id.ValueKind == JsonValueKind.Null) continue;
                    var box = mask.GetProperty("Box");
                    recorded.Add((id.GetInt32(),
                        (CensorRegionKind)mask.GetProperty("Kind").GetInt32(),
                        new PixelRect(box.GetProperty("X").GetDouble(), box.GetProperty("Y").GetDouble(),
                            box.GetProperty("Width").GetDouble(), box.GetProperty("Height").GetDouble())));
                }

                records.Add(new Record(time, stamp, candidates, recorded));
            }
        }
        records.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));
        return records;
    }

    private static object MeasureRecorded(List<Record> records, PixelRect bounds)
    {
        var maxFraction = 0.0;
        var maxSize = (0, 0);
        var count = 0;
        var totalArea = 0.0;
        foreach (var record in records)
        {
            foreach (var mask in record.Masks)
            {
                count++;
                totalArea += mask.Box.Area;
                var fraction = mask.Box.Area / bounds.Area;
                if (fraction > maxFraction)
                {
                    maxFraction = fraction;
                    maxSize = ((int)Math.Round(mask.Box.Width), (int)Math.Round(mask.Box.Height));
                }
            }
        }
        return new
        {
            Masks = count,
            MeanMaskScreenFraction = count == 0 ? 0 : totalArea / count / bounds.Area,
            MaximumMaskScreenFraction = maxFraction,
            MaximumMaskSize = $"{maxSize.Item1}x{maxSize.Item2}"
        };
    }

    private static object Replay(List<Record> records, AppSettings settings, PixelRect bounds)
    {
        var tracker = new SubjectTracker();
        var gate = new ConfidenceGate();
        var maxFraction = 0.0;
        var maxSize = (0, 0);
        var count = 0;
        var totalArea = 0.0;
        var rejected = 0;
        var accepted = 0;
        var rejectedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var largest = new List<(double Fraction, string Size, double Confidence, string Verdict)>();

        foreach (var record in records)
        {
            var (allowed, verdicts) = FaceCandidateGate.Evaluate(record.Candidates,
                settings.Detection.FaceConfidence, tracker.Snapshot());
            foreach (var verdict in verdicts)
            {
                if (verdict.Decision == FaceCandidateDecision.Accepted) { accepted++; continue; }
                rejected++;
                var key = verdict.Decision.ToString();
                rejectedBy[key] = rejectedBy.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            tracker.Update(allowed, record.Stamp, bounds);
            gate.RetainOnly(tracker.Snapshot().Select(t => t.Id));
            var now = record.Stamp;
            var regions = CensorComposer.Build(tracker.Snapshot(), settings, bounds, now, false, gate);
            foreach (var region in regions.Where(r => r.Source == BodyRegion.Face))
            {
                count++;
                totalArea += region.Box.Area;
                var fraction = region.Box.Area / bounds.Area;
                if (fraction > maxFraction)
                {
                    maxFraction = fraction;
                    maxSize = ((int)Math.Round(region.Box.Width), (int)Math.Round(region.Box.Height));
                }
                var source = record.Candidates.FirstOrDefault(c =>
                    c.Box.OverlapOfSmaller(region.Box) > 0.3);
                largest.Add((fraction,
                    $"{region.Box.Width:0}x{region.Box.Height:0}",
                    source?.Confidence ?? 0,
                    source is null ? "held" : "detected"));
            }
        }
        largest.Sort((a, b) => b.Fraction.CompareTo(a.Fraction));

        return new
        {
            Masks = count,
            CandidatesAccepted = accepted,
            CandidatesRejected = rejected,
            RejectedBy = rejectedBy,
            MeanMaskScreenFraction = count == 0 ? 0 : totalArea / count / bounds.Area,
            MaximumMaskScreenFraction = maxFraction,
            MaximumMaskSize = $"{maxSize.Item1}x{maxSize.Item2}",
            LargestMasks = largest.Take(8).Select(l => new
            {
                ScreenFraction = l.Fraction,
                Size = l.Size,
                SourceCandidateConfidence = l.Confidence,
                Origin = l.Verdict
            })
        };
    }
}
