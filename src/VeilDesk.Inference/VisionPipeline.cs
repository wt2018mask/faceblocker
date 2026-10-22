using System.Diagnostics;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Settings;
using VeilDesk.Core.Tracking;
using VeilDesk.Core.Vision;

namespace VeilDesk.Inference;

public sealed record AnalysisResult(
    IReadOnlyList<Detection> NudeDetections,
    IReadOnlyList<Detection> PersonDetections,
    IReadOnlyList<TrackedDetection> Tracks,
    IReadOnlyList<CensorRegion> Regions,
    double PreprocessMs,
    double PersonMs,
    double NudeMs,
    double SegmentMs,
    double TotalMs,
    bool SegmentationRan,
    string? Error);

/// <summary>
/// One complete local analysis pass over a monitor frame: person detection,
/// anatomical detection, optional per-person segmentation, tracking, and
/// censor-region composition. The pipeline is deliberately single-consumer;
/// the app serialises calls on one worker thread.
/// </summary>
public sealed class VisionPipeline : IDisposable
{
    private readonly VisionPipelineOptions _options;
    private readonly VisionModels _models;
    private readonly SubjectTracker _tracker = new();
    private readonly float[] _nudeInput = new float[3 * FramePreprocessor.NudeNetSize * FramePreprocessor.NudeNetSize];
    private readonly float[] _personInput;
    private readonly float[] _segInput = new float[3 * FramePreprocessor.SegmentationSize * FramePreprocessor.SegmentationSize];
    private readonly object _gate = new();

    private long _lastSegmentationMs = long.MinValue / 2;
    private long _lastSuccessMs = -1;
    private int _consecutiveFailures;
    private string? _lastError;

    public VisionPipeline(VisionPipelineOptions options)
    {
        _options = options;
        _models = VisionModels.Load(options);
        _personInput = new float[3 * options.PersonModelSize * options.PersonModelSize];
    }

    public string Provider => _models.Provider;
    public string? DirectMLError => _models.DirectMLError;
    public string? LastError => _lastError;
    public int ModelPersonSize => _options.PersonModelSize;

    public AnalysisResult Analyze(VideoFrame frame, AppSettings settings, long nowMs)
    {
        var total = Stopwatch.StartNew();
        double preprocessMs = 0, personMs = 0, nudeMs = 0, segmentMs = 0;
        var segmentationRan = false;
        try
        {
            lock (_gate)
            {
                var personSize = _options.PersonModelSize;

                var sw = Stopwatch.StartNew();
                FramePreprocessor.PreprocessYolo(frame, personSize, _personInput);
                preprocessMs += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var personOutput = _models.RunPerson(_personInput, personSize);
                personMs = sw.Elapsed.TotalMilliseconds;

                var personTransform = LetterboxTransform.CreateCentered(frame.Width, frame.Height, personSize, personSize);
                var people = DetectionDecoder.DecodeYoloClass(
                    personOutput, 4 + 80, AnchorCount(personSize), 0, personTransform,
                    settings.Detection.PersonConfidence, settings.Detection.NmsIou);

                sw.Restart();
                FramePreprocessor.PreprocessNudeNet(frame, _nudeInput);
                preprocessMs += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var nudeOutput = _models.RunNudeNet(_nudeInput, FramePreprocessor.NudeNetSize);
                nudeMs = sw.Elapsed.TotalMilliseconds;

                var nudeTransform = LetterboxTransform.CreateBottomRight(
                    frame.Width, frame.Height, FramePreprocessor.NudeNetSize);
                var body = DetectionDecoder.DecodeNudeNet(
                    nudeOutput, 22, 2100, nudeTransform,
                    confidenceThreshold: 0.20,
                    nmsIou: settings.Detection.NmsIou);

                var peopleWithMasks = people;
                if (settings.Censoring.Style == CensorStyle.Silhouette &&
                    people.Count > 0 &&
                    nowMs - _lastSegmentationMs >= 1000.0 / Math.Clamp(settings.Performance.SegmentationRateHz, 1, 30))
                {
                    sw.Restart();
                    peopleWithMasks = AttachMasks(frame, people, settings);
                    segmentMs = sw.Elapsed.TotalMilliseconds;
                    segmentationRan = true;
                    _lastSegmentationMs = nowMs;
                }

                var all = new List<Detection>(peopleWithMasks.Count + body.Count);
                all.AddRange(peopleWithMasks);
                all.AddRange(body);

                var tracks = _tracker.Update(all, nowMs, frame.MonitorBounds);
                _lastSuccessMs = nowMs;
                _consecutiveFailures = 0;
                _lastError = null;

                var regions = CensorComposer.Build(tracks, settings, frame.MonitorBounds, nowMs,
                    FailClosedActive(settings, nowMs));

                return new AnalysisResult(body, peopleWithMasks, tracks, regions,
                    preprocessMs, personMs, nudeMs, segmentMs, total.Elapsed.TotalMilliseconds,
                    segmentationRan, null);
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _lastError = ex.Message;
            return new AnalysisResult(Array.Empty<Detection>(), Array.Empty<Detection>(),
                Array.Empty<TrackedDetection>(), GetRegions(settings, frame.MonitorBounds, nowMs),
                preprocessMs, personMs, nudeMs, segmentMs, total.Elapsed.TotalMilliseconds,
                segmentationRan, ex.Message);
        }
    }

    private IReadOnlyList<Detection> AttachMasks(VideoFrame frame, IReadOnlyList<Detection> people, AppSettings settings)
    {
        var result = new List<Detection>(people.Count);
        var max = Math.Clamp(settings.Performance.MaxSegmentationsPerFrame, 1, 8);
        for (var i = 0; i < people.Count; i++)
        {
            var person = people[i];
            if (i >= max)
            {
                result.Add(person);
                continue;
            }

            FramePreprocessor.PreprocessSegmentation(frame, person.Box, _segInput);
            var probabilities = _models.RunSegmentation(_segInput);
            var mask = SegmentationDecoder.CleanMask(
                SegmentationDecoder.ProbabilitiesToMask(probabilities,
                    FramePreprocessor.SegmentationSize, FramePreprocessor.SegmentationSize),
                FramePreprocessor.SegmentationSize, FramePreprocessor.SegmentationSize);

            result.Add(mask.Length == 0
                ? person
                : person with
                {
                    Mask = mask,
                    MaskWidth = FramePreprocessor.SegmentationSize,
                    MaskHeight = FramePreprocessor.SegmentationSize
                });
        }
        return result;
    }

    private static int AnchorCount(int size) => size switch
    {
        320 => 2100,
        480 => 4725,
        _ => (size / 8) * (size / 8) + (size / 16) * (size / 16) + (size / 32) * (size / 32)
    };

    public bool FailClosedActive(AppSettings settings, long nowMs)
    {
        if (!settings.Censoring.FailClosed) return false;
        if (_lastSuccessMs < 0) return true;
        if (_consecutiveFailures >= 2) return true;
        return nowMs - _lastSuccessMs > settings.Censoring.FailClosedAfterMs;
    }

    public IReadOnlyList<CensorRegion> GetRegions(AppSettings settings, VeilDesk.Core.Geometry.PixelRect bounds, long nowMs)
    {
        lock (_gate)
        {
            return CensorComposer.Build(_tracker.Snapshot(), settings, bounds, nowMs,
                FailClosedActive(settings, nowMs));
        }
    }

    public IReadOnlyList<TrackedDetection> SnapshotTracks()
    {
        lock (_gate) return _tracker.Snapshot();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _tracker.Reset();
            _lastSuccessMs = -1;
            _consecutiveFailures = 0;
            _lastError = null;
            _lastSegmentationMs = long.MinValue / 2;
        }
    }

    public void Dispose() => _models.Dispose();
}
