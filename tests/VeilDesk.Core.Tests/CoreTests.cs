using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Export;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Packs;
using VeilDesk.Core.Sessions;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using VeilDesk.Core.Vision;
using DetectionRecord = VeilDesk.Core.Detection.Detection;
using Xunit;

namespace VeilDesk.Core.Tests;

public class GeometryAndDecoderTests
{
    [Fact]
    public void Rect_ClipsAndComputesIou()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 100, 100);
        Assert.Equal(2500, a.IntersectionArea(b), 3);
        Assert.Equal(2500.0 / 17500.0, a.IoU(b), 6);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Clip(b));
        Assert.Equal(150, a.Union(b).Right);
    }

    [Fact]
    public void Letterbox_MapsNudeNetBottomRightPadding()
    {
        var t = LetterboxTransform.CreateBottomRight(640, 480, 320);
        Assert.Equal(0.5, t.Scale, 6);
        var box = t.MapCenterBoxToSource(160, 160, 80, 40).Clip(new PixelRect(0, 0, 640, 480));
        Assert.Equal(240, box.X, 3);
        Assert.Equal(280, box.Y, 3);
        Assert.Equal(160, box.Width, 3);
        Assert.Equal(80, box.Height, 3);
    }

    [Fact]
    public void NudeNetDecoder_ReturnsBestClassAndNms()
    {
        const int anchors = 3;
        const int channels = 22;
        var output = new float[channels * anchors];
        void SetBox(int a, float cx, float cy, float w, float h, int cls, float score)
        {
            output[0 * anchors + a] = cx;
            output[1 * anchors + a] = cy;
            output[2 * anchors + a] = w;
            output[3 * anchors + a] = h;
            output[(4 + cls) * anchors + a] = score;
        }
        var transform = LetterboxTransform.CreateBottomRight(640, 480, 320);
        SetBox(0, 160, 160, 80, 40, (int)BodyRegion.FemaleBreastExposed, 0.9f);
        SetBox(1, 162, 160, 80, 40, (int)BodyRegion.FemaleBreastExposed, 0.8f);
        SetBox(2, 60, 60, 20, 20, (int)BodyRegion.FaceFemale, 0.6f);

        var detections = DetectionDecoder.DecodeNudeNet(output, channels, anchors, transform, 0.5, 0.45);
        Assert.Equal(2, detections.Count);
        var breast = Assert.Single(detections, d => d.Region == BodyRegion.FemaleBreastExposed);
        Assert.Equal(0.9, breast.Confidence, 3);
        Assert.Contains(detections, d => d.Region == BodyRegion.FaceFemale);
    }

    [Fact]
    public void YoloDecoder_ReturnsPersonOnly()
    {
        const int anchors = 2;
        const int channels = 84;
        var output = new float[channels * anchors];
        var transform = LetterboxTransform.CreateCentered(640, 640, 320, 320);
        output[0 * anchors + 0] = 100;
        output[1 * anchors + 0] = 120;
        output[2 * anchors + 0] = 40;
        output[3 * anchors + 0] = 100;
        output[(4 + 0) * anchors + 0] = 0.95f;
        output[(4 + 2) * anchors + 0] = 0.99f;

        var detections = DetectionDecoder.DecodeYoloClass(output, channels, anchors, 0, transform, 0.3, 0.45);
        var person = Assert.Single(detections);
        Assert.Equal(BodyRegion.Person, person.Region);
        Assert.Equal(0.95, person.Confidence, 3);
    }

    [Fact]
    public void Segmentation_MaskKeepsLargestComponent()
    {
        const int w = 8, h = 8;
        var logits = new float[w * h];
        // Two blobs: a single pixel and a 3x3 block, far enough apart that
        // one-pixel dilation cannot merge them.
        logits[0] = 5;
        for (var y = 5; y < 8; y++) for (var x = 5; x < 8; x++) logits[y * w + x] = 5;
        var mask = SegmentationDecoder.ProbabilitiesToMask(logits, w, h);
        var clean = SegmentationDecoder.CleanMask(mask, w, h);
        Assert.Equal(255, clean[6 * w + 6]);
        Assert.True(clean[0] == 0, string.Join(",", clean));
    }
}

public class TrackerAndComposerTests
{
    private static AppSettings PrivacySettings()
    {
        var s = new AppSettings();
        s.Detection.WholeBodyEnabled = false;
        s.Censoring.FailClosed = false;
        s.Censoring.TrackPersistenceMs = 550;
        s.Censoring.PredictionHorizonMs = 80;
        return s;
    }

    [Fact]
    public void Tracker_PredictsMovingSubject()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1920, 1080);
        tracker.Update(new[] { new DetectionRecord(BodyRegion.FemaleBreastExposed, 0.9, new PixelRect(100, 100, 100, 100)) }, 0, bounds);
        var tracks = tracker.Update(new[] { new DetectionRecord(BodyRegion.FemaleBreastExposed, 0.9, new PixelRect(140, 100, 100, 100)) }, 100, bounds);
        var track = Assert.Single(tracks);
        Assert.True(track.VelocityX > 0);
        var predicted = track.PredictedBox(100, 80, 0.1, bounds);
        Assert.True(predicted.CenterX > track.SmoothedBox.CenterX);
        Assert.Single(tracks);
        Assert.Equal(track.Id, track.Id);
    }

    [Fact]
    public void Tracker_KeepsIdAndExpiresAfterPersistence()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 1000);
        var tracks = tracker.Update(new[] { new DetectionRecord(BodyRegion.ButtocksExposed, 0.8, new PixelRect(100, 100, 100, 100)) }, 0, bounds);
        var id = Assert.Single(tracks).Id;
        tracks = tracker.Update(Array.Empty<DetectionRecord>(), 300, bounds);
        Assert.Equal(id, Assert.Single(tracks).Id);
        var settings = PrivacySettings();
        var regions = CensorComposer.Build(tracks, settings, bounds, 300, false);
        Assert.Single(regions);
        regions = CensorComposer.Build(tracks, settings, bounds, 700, false);
        Assert.Empty(regions);
    }

    [Fact]
    public void Tracker_AssociatesBodyPartsWithPersonEvidence()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 1000);
        var detections = new[]
        {
            new DetectionRecord(BodyRegion.Person, 0.9, new PixelRect(100, 100, 300, 700)),
            new DetectionRecord(BodyRegion.FemaleBreastExposed, 0.9, new PixelRect(180, 250, 80, 70)),
            new DetectionRecord(BodyRegion.FaceFemale, 0.8, new PixelRect(200, 110, 60, 60))
        };
        var tracks = tracker.Update(detections, 0, bounds);
        var person = Assert.Single(tracks, t => t.IsPerson);
        Assert.True(person.HasExposedEvidence);
        Assert.Equal(PersonGender.Female, person.Gender);
    }

    [Fact]
    public void Composer_WholeBodyNudeOnlyRequiresEvidence()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 1000);
        var settings = PrivacySettings();
        settings.Detection.WholeBodyEnabled = true;
        settings.Detection.WholeBodyNudeOnly = true;
        settings.Detection.WholeBodyConfidence = 0.5;

        var tracks = tracker.Update(new[] { new DetectionRecord(BodyRegion.Person, 0.9, new PixelRect(100, 100, 300, 700)) }, 0, bounds);
        Assert.Empty(CensorComposer.Build(tracks, settings, bounds, 0, false));

        tracks = tracker.Update(new[]
        {
            new DetectionRecord(BodyRegion.Person, 0.9, new PixelRect(100, 100, 300, 700)),
            new DetectionRecord(BodyRegion.FemaleBreastExposed, 0.9, new PixelRect(180, 250, 80, 70))
        }, 100, bounds);
        var regions = CensorComposer.Build(tracks, settings, bounds, 100, false);
        Assert.Contains(regions, r => r.Kind == CensorRegionKind.WholePerson);
    }

    [Fact]
    public void Composer_HonoursGenderAndFailClosed()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 1000);
        var settings = PrivacySettings();
        settings.Detection.WholeBodyEnabled = true;
        settings.Detection.WholeBodyNudeOnly = false;
        settings.Detection.WholeBodyFemale = true;
        settings.Detection.WholeBodyMale = false;

        var tracks = tracker.Update(new[]
        {
            new DetectionRecord(BodyRegion.Person, 0.9, new PixelRect(100, 100, 300, 700)),
            new DetectionRecord(BodyRegion.FaceMale, 0.9, new PixelRect(200, 110, 60, 60))
        }, 0, bounds);
        Assert.Empty(CensorComposer.Build(tracks, settings, bounds, 0, false));

        var regions = CensorComposer.Build(Array.Empty<TrackedDetection>(), settings, bounds, 0, true);
        var fail = Assert.Single(regions);
        Assert.Equal(CensorRegionKind.FailClosed, fail.Kind);
        Assert.Equal(bounds, fail.Box);
    }

    [Fact]
    public void Composer_RespectsConfidenceAndEnabledRegions()
    {
        var tracker = new SubjectTracker();
        var bounds = new PixelRect(0, 0, 1000, 1000);
        var settings = PrivacySettings();
        var tracks = tracker.Update(new[] { new DetectionRecord(BodyRegion.FaceFemale, 0.9, new PixelRect(1, 1, 50, 50)) }, 0, bounds);
        Assert.Empty(CensorComposer.Build(tracks, settings, bounds, 0, false));
        settings.Detection.EnabledRegions.Add(BodyRegion.FaceFemale);
        Assert.Single(CensorComposer.Build(tracks, settings, bounds, 0, false));
    }
}

public class SettingsTests
{
    [Fact]
    public void Settings_RoundTripAndClamp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "veiltest-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = store.Load();
        s.Performance.ScanRateHz = 999;
        s.Detection.Confidence = -2;
        s.Censoring.Color = "not-a-color";
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(60, loaded.Performance.ScanRateHz);
        Assert.Equal(0.05, loaded.Detection.Confidence, 3);
        Assert.Equal("#0B0D12", loaded.Censoring.Color);
        Assert.True(File.Exists(store.Path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Settings_CorruptFileFallsBackAndIsQuarantined()
    {
        var dir = Path.Combine(Path.GetTempPath(), "veiltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(dir, "settings.json.corrupt-*"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void HexColourValidation_AcceptsOnlyHex()
    {
        Assert.True(SettingsValidator.IsHexColor("#AABBCC"));
        Assert.True(SettingsValidator.IsHexColor("AABBCCDD"));
        Assert.False(SettingsValidator.IsHexColor("#GGGGGG"));
        Assert.False(SettingsValidator.IsHexColor("red"));
    }
}

public class SceneAndSchedulerTests
{
    [Fact]
    public void SceneChange_DetectsChangedFrame()
    {
        var detector = new SceneChangeDetector();
        const int w = 32, h = 18;
        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        for (var i = 0; i < a.Length; i += 4) { a[i] = 10; a[i + 1] = 10; a[i + 2] = 10; a[i + 3] = 255; }
        Array.Copy(a, b, a.Length);
        for (var i = 0; i < b.Length; i += 4) { b[i] = 220; b[i + 1] = 220; b[i + 2] = 220; }
        var ta = detector.ComputeThumbprint(a, w, h, w * 4);
        var tb = detector.ComputeThumbprint(b, w, h, w * 4);
        Assert.Equal(0, SceneChangeDetector.Difference(ta, ta), 6);
        var delta = detector.Update(ta);
        Assert.Equal(1.0, delta, 6);
        Assert.True(detector.Update(tb) > 0.5);
    }

    [Fact]
    public void Scheduler_ThrottlesButKeepsSafetyScan()
    {
        var scheduler = new InferenceScheduler();
        Assert.True(scheduler.ShouldRun(0, 1, 10, true, 0.005));
        scheduler.MarkRun(0);
        Assert.False(scheduler.ShouldRun(50, 0.0001, 10, true, 0.005));
        Assert.True(scheduler.ShouldRun(100, 0.1, 10, true, 0.005));
        scheduler.MarkRun(100);
        Assert.True(scheduler.ShouldRun(1100, 0.0001, 10, true, 0.005));
    }
}

public class PackTests
{
    private static string CreatePack(string manifest, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "veilpack-" + Guid.NewGuid().ToString("N") + ".veilpack");
        using var fs = File.Create(path);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        var m = zip.CreateEntry("manifest.json");
        using (var w = new StreamWriter(m.Open())) w.Write(manifest);
        foreach (var (name, data) in entries)
        {
            var e = zip.CreateEntry(name);
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    [Fact]
    public void Pack_ValidatesAndExtracts()
    {
        var manifest = """
        {"format":"veilpack/1","id":"test.pack","name":"Test Pack","version":"1.0.0","author":"Test","description":"Synthetic test pack","license":"CC0","assets":[{"path":"images/a.png","type":"image"}],"textPresets":[{"text":"HIDDEN","color":"#FFFFFF"}]}
        """;
        var path = CreatePack(manifest, ("images/a.png", new byte[] { 1, 2, 3 }));
        var dest = Path.Combine(Path.GetTempPath(), "veilpack-out-" + Guid.NewGuid().ToString("N"));
        var result = PackValidator.ValidateAndExtract(path, dest);
        Assert.True(result.Success, result.Error);
        Assert.Equal("test.pack", result.Manifest!.Id);
        Assert.True(File.Exists(Path.Combine(dest, "images", "a.png")));
        File.Delete(path);
        Directory.Delete(dest, true);
    }

    [Fact]
    public void Pack_RejectsTraversalAndExecutables()
    {
        var manifest = """{"format":"veilpack/1","id":"x","name":"X","version":"1"}""";
        var path = CreatePack(manifest, ("../evil.png", new byte[] { 1 }));
        var dest = Path.Combine(Path.GetTempPath(), "veilpack-out-" + Guid.NewGuid().ToString("N"));
        Assert.False(PackValidator.ValidateAndExtract(path, dest).Success);

        var path2 = CreatePack(manifest, ("payload.exe", new byte[] { 1 }));
        Assert.False(PackValidator.ValidateAndExtract(path2, dest).Success);
        File.Delete(path); File.Delete(path2);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
    }

    [Fact]
    public void Pack_RejectsWrongFormat()
    {
        var manifest = """{"format":"veilpack/999","id":"x","name":"X","version":"1"}""";
        var path = CreatePack(manifest);
        var dest = Path.Combine(Path.GetTempPath(), "veilpack-out-" + Guid.NewGuid().ToString("N"));
        var result = PackValidator.ValidateAndExtract(path, dest);
        Assert.False(result.Success);
        Assert.Contains("Unsupported", result.Error);
        File.Delete(path);
    }
}

public class AviWriterTests
{
    [Fact]
    public void MjpegAvi_WritesValidStreamingContainerAndIndex()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "veildesk-" + Guid.NewGuid().ToString("N") + ".avi");
        var frames = new[]
        {
            new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 },
            new byte[] { 0xFF, 0xD8, 0x03, 0x04, 0x05, 0x06, 0x07, 0xFF, 0xD9 },
            new byte[] { 0xFF, 0xD8, 0x08, 0xFF, 0xD9 }
        };

        using (var writer = new MjpegAviWriter(path, 640, 360, 15))
        {
            Assert.Equal(path, writer.OutPath);
            foreach (var frame in frames) writer.WriteFrame(frame);
            Assert.Equal(3, writer.FrameCount);
            writer.Finish();
            writer.Finish(); // idempotent
        }

        var data = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Ascii(data, 0));
        Assert.Equal((uint)(data.Length - 8), ReadU32(data, 4));
        Assert.Equal("AVI ", Ascii(data, 8));
        Assert.Equal("LIST", Ascii(data, 12));
        Assert.Equal("hdrl", Ascii(data, 20));

        var avih = 24;
        Assert.Equal("avih", Ascii(data, avih));
        Assert.Equal(56u, ReadU32(data, avih + 4));
        Assert.Equal(3u, ReadU32(data, avih + 8 + 16)); // total frames
        Assert.Equal(640u, ReadU32(data, avih + 8 + 32));
        Assert.Equal(360u, ReadU32(data, avih + 8 + 36));

        var hdrlSize = ReadU32(data, 16);
        var moviList = 20 + (int)hdrlSize;
        Assert.Equal("LIST", Ascii(data, moviList));
        var moviSize = ReadU32(data, moviList + 4);
        Assert.Equal("movi", Ascii(data, moviList + 8));

        var moviFourCc = moviList + 8;
        // LIST size must include the four-byte 'movi' FOURCC, not just frames.
        var expectedMoviSize = 4 + frames.Sum(f => 8 + f.Length + (f.Length & 1));
        Assert.Equal((uint)expectedMoviSize, moviSize);
        var idx = moviList + 8 + (int)moviSize;
        Assert.Equal("idx1", Ascii(data, idx));
        Assert.Equal((uint)(frames.Length * 16), ReadU32(data, idx + 4));

        var entry = idx + 8;
        long expectedChunk = moviList + 12;
        for (var i = 0; i < frames.Length; i++, entry += 16)
        {
            Assert.Equal("00dc", Ascii(data, entry));
            Assert.Equal(0x10u, ReadU32(data, entry + 4));
            var offset = ReadU32(data, entry + 8);
            var size = ReadU32(data, entry + 12);
            Assert.Equal((uint)frames[i].Length, size);
            Assert.Equal(expectedChunk, (long)moviFourCc + offset);
            Assert.Equal("00dc", Ascii(data, (int)(moviFourCc + offset)));
            Assert.Equal(frames[i], data.AsSpan((int)(moviFourCc + offset + 8), (int)size).ToArray());
            expectedChunk += 8 + size + (size & 1);
        }
        Assert.Equal(idx, (int)expectedChunk);
        Assert.Equal(0u, data.Length & 1u);

        File.Delete(path);
    }

    [Fact]
    public void MjpegAvi_RejectsInvalidArgumentsAndEmptyFrames()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "veildesk-" + Guid.NewGuid().ToString("N") + ".avi");
        Assert.Throws<ArgumentOutOfRangeException>(() => new MjpegAviWriter(path, 0, 10, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MjpegAviWriter(path, 10, 10, 0));
        using (var writer = new MjpegAviWriter(path, 10, 10, 15))
        {
            Assert.Throws<ArgumentException>(() => writer.WriteFrame(ReadOnlySpan<byte>.Empty));
        }
        File.Delete(path);
    }

    private static string Ascii(byte[] data, int offset)
        => Encoding.ASCII.GetString(data, offset, 4);

    private static uint ReadU32(byte[] data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}

public class BuiltInPackTests
{
    [Fact]
    public void BuiltInPacks_AreValidAndCarryCensoringPresets()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "builtin-packs");
        Assert.True(Directory.Exists(directory), "Built-in packs were not copied to the test output.");
        var files = Directory.GetFiles(directory, "*.veilpack");
        Assert.True(files.Length >= 4, "Expected at least four built-in packs.");

        foreach (var file in files)
        {
            var destination = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "veildesk-builtin-check-" + Guid.NewGuid().ToString("N")[..10]);
            try
            {
                var result = PackValidator.ValidateAndExtract(file, destination);
                Assert.True(result.Success, System.IO.Path.GetFileName(file) + ": " + result.Error);
                var manifest = result.Manifest!;
                Assert.Equal(PackFormat.Current, manifest.Format);
                Assert.StartsWith("builtin.", manifest.Id);
                Assert.True(manifest.SettingsPreset!.ContainsKey("Censoring"));

                var preset = JsonSerializer.Deserialize<CensorSettings>(
                    manifest.SettingsPreset["Censoring"].GetRawText(), SettingsStore.JsonOptions);
                Assert.NotNull(preset);
                Assert.NotEmpty(preset.Color);

                foreach (var asset in manifest.Assets)
                    Assert.True(File.Exists(System.IO.Path.Combine(destination, asset.Path)),
                        "Missing extracted asset: " + asset.Path);
            }
            finally
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
            }
        }
    }
}

public class SessionTests
{
    [Fact]
    public void Sessions_SummarizeAndAchievements()
    {
        var path = Path.Combine(Path.GetTempPath(), "sessions-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var store = new SessionStore(path);
        store.Append(new SessionRecord { StartedUtc = DateTimeOffset.UtcNow, EndedUtc = DateTimeOffset.UtcNow, DurationSeconds = 400, DetectionCount = 12, PeakTrackCount = 3 });
        store.Append(new SessionRecord { StartedUtc = DateTimeOffset.UtcNow.AddDays(-1), EndedUtc = DateTimeOffset.UtcNow, DurationSeconds = 300, DetectionCount = 8, PeakTrackCount = 2 });
        var summary = store.Summarize();
        Assert.Equal(2, summary.SessionCount);
        Assert.Equal(700, summary.TotalSeconds, 3);
        Assert.Equal(400, summary.LongestSeconds, 3);
        Assert.Equal(20, summary.TotalDetections);
        Assert.Equal(2, summary.CurrentStreakDays);
        var achievements = store.EvaluateAchievements();
        Assert.True(achievements.Single(a => a.Id == "first-shield").Unlocked);
        Assert.True(achievements.Single(a => a.Id == "ten-minutes").Unlocked);
        store.DeleteAll();
        Assert.Empty(store.LoadAll());
        if (File.Exists(path)) File.Delete(path);
    }
}
