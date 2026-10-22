using System.Diagnostics;
using VeilDesk.Core.Censoring;
using VeilDesk.Core.Detection;
using VeilDesk.Core.Safety;
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
    private readonly IVisionModelRunner _models;
    private readonly bool _ownsModels;
    private readonly SubjectTracker _tracker = new();
    private readonly ConfidenceGate _confidenceGate = new();
    private readonly CoverageGuard _coverageGuard = new();
    private readonly ProtectionSupervisor _supervisor = new();
    private readonly float[] _nudeInput = new float[3 * FramePreprocessor.NudeNetSize * FramePreprocessor.NudeNetSize];
    private readonly float[] _personInput;
    private readonly float[] _segInput = new float[3 * FramePreprocessor.SegmentationSize * FramePreprocessor.SegmentationSize];
    private readonly object _gate = new();
    // Never hold this short publication lock while running ONNX. Rendering
    // must keep evaluating freshness even when inference stalls.
    private readonly object _publicationGate = new();

    private long _lastSegmentationMs = long.MinValue / 2;
    private long _lastBodyMs = long.MinValue / 2;
    private IReadOnlyList<Detection> _lastBodyDetections = Array.Empty<Detection>();
    private int _consecutiveFailures;
    private string? _lastError;

    public VisionPipeline(VisionPipelineOptions options)
        : this(options, VisionModels.Load(options), ownsModels: true)
    {
    }

    /// <summary>
    /// Uses a model set shared by several monitor pipelines. The caller owns
    /// <paramref name="models"/> and must outlive every pipeline using it.
    /// </summary>
    public VisionPipeline(VisionPipelineOptions options, IVisionModelRunner models)
        : this(options, models, ownsModels: false)
    {
    }

    private VisionPipeline(VisionPipelineOptions options, IVisionModelRunner models, bool ownsModels)
    {
        _options = options;
        _models = models;
        _ownsModels = ownsModels;
        _personInput = new float[3 * options.PersonModelSize * options.PersonModelSize];
    }

    public string Provider => _models.Provider;
    public string? DirectMLError => _models.DirectMLError;
    public string? LastError => _lastError;
    public int ModelPersonSize => _options.PersonModelSize;

    /// <summary>Consecutive failed analysis passes.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Begins a protection session for this display. Until the first
    /// successful analysis the supervisor keeps the fail-closed state, so the
    /// whole output is covered from the first frame.
    /// </summary>
    public void StartProtection(AppSettings settings, long nowMs)
    {
        lock (_gate)
        lock (_publicationGate)
        {
            _tracker.Reset();
            _confidenceGate.Reset();
            _coverageGuard.Reset();
            _consecutiveFailures = 0;
            _lastError = null;
            _lastSegmentationMs = long.MinValue / 2;
            _lastBodyMs = long.MinValue / 2;
            _lastBodyDetections = Array.Empty<Detection>();
            _supervisor.ApplySettings(settings);
            _supervisor.Start(nowMs);
        }
    }

    public AnalysisResult Analyze(VideoFrame frame, AppSettings settings, long nowMs)
    {
        var total = Stopwatch.StartNew();
        double preprocessMs = 0, personMs = 0, nudeMs = 0, segmentMs = 0;
        var segmentationRan = false;

        // A new inference attempt is not evidence that protection works. The
        // supervisor keeps the fail-closed state until the pipeline proves
        // itself again.
        _supervisor.ApplySettings(settings);
        _supervisor.ReportAttempt(nowMs);

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

                IReadOnlyList<Detection> body;
                var bodyDue = nowMs - _lastBodyMs >=
                    1000.0 / Math.Clamp(settings.Performance.BodyScanRateHz, 1, 30);
                if (bodyDue)
                {
                    sw.Restart();
                    FramePreprocessor.PreprocessNudeNet(frame, _nudeInput);
                    preprocessMs += sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    var nudeOutput = _models.RunNudeNet(_nudeInput, FramePreprocessor.NudeNetSize);
                    nudeMs = sw.Elapsed.TotalMilliseconds;

                    var nudeTransform = LetterboxTransform.CreateBottomRight(
                        frame.Width, frame.Height, FramePreprocessor.NudeNetSize);
                    body = DetectionDecoder.DecodeNudeNet(
                        nudeOutput, 22, 2100, nudeTransform,
                        confidenceThreshold: 0.20,
                        nmsIou: settings.Detection.NmsIou);
                    _lastBodyDetections = body;
                    _lastBodyMs = nowMs;
                }
                else
                {
                    // Body detections are only needed on their own cadence. Between
                    // scans the tracker extrapolates the last boxes.
                    body = Array.Empty<Detection>();
                }

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

                IReadOnlyList<TrackedDetection> tracks;
                IReadOnlyList<CensorRegion> regions;
                lock (_publicationGate)
                {
                    var completedMs = nowMs + total.ElapsedMilliseconds;
                    if (_supervisor.State != ProtectionState.Stopped &&
                        frame.TimestampMs > _supervisor.LastSuccessMs &&
                        completedMs - frame.TimestampMs <= settings.Censoring.FailClosedAfterMs)
                    {
                        _tracker.Update(all, frame.TimestampMs, frame.LocalBounds);
                        _supervisor.ReportSuccess(frame.TimestampMs);
                    }
                    _consecutiveFailures = 0;
                    _lastError = null;
                    tracks = _tracker.Snapshot();
                    regions = GetRegions(settings, frame.LocalBounds, completedMs);
                }

                return new AnalysisResult(body, peopleWithMasks, tracks, regions,
                    preprocessMs, personMs, nudeMs, segmentMs, total.Elapsed.TotalMilliseconds,
                    segmentationRan, null);
            }
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _lastError = ex.Message;
            _supervisor.ReportFailure(nowMs + total.ElapsedMilliseconds, ex.GetType().Name);
            return new AnalysisResult(Array.Empty<Detection>(), Array.Empty<Detection>(),
                Array.Empty<TrackedDetection>(), GetRegions(settings, frame.LocalBounds, nowMs),
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
        _supervisor.ApplySettings(settings);
        _supervisor.Evaluate(nowMs);
        return _supervisor.EffectiveFailClosed(settings);
    }

    /// <summary>Current protection state, used for diagnostics and logging.</summary>
    public ProtectionState ProtectionState => _supervisor.State;

    /// <summary>Reason for the current protection state.</summary>
    public string? ProtectionReason => _supervisor.Reason;

    /// <summary>Raised when the protection state changes.</summary>
    public event Action<ProtectionState, ProtectionState, string?>? ProtectionStateChanged
    {
        add => _supervisor.StateChanged += value;
        remove => _supervisor.StateChanged -= value;
    }

    /// <summary>A new frame was captured for this display.</summary>
    public void ReportFrame(long nowMs) => _supervisor.ReportFrame(nowMs);

    /// <summary>The capture layer for this display failed.</summary>
    public void ReportCaptureFailure(string? error) => _supervisor.ReportCaptureFailure(error);

    /// <summary>The capture layer for this display is healthy again.</summary>
    public void ReportCaptureHealthy() => _supervisor.ReportCaptureHealthy();

    /// <summary>
    /// Protection was explicitly stopped. This is not a safety pause: the
    /// overlay is removed completely instead of covering the screen.
    /// </summary>
    public void StopProtection()
    {
        lock (_publicationGate) _supervisor.Stop();
    }

    public IReadOnlyList<CensorRegion> GetRegions(AppSettings settings, VeilDesk.Core.Geometry.PixelRect bounds, long nowMs)
    {
        lock (_publicationGate)
        {
            _supervisor.ApplySettings(settings);
            if (_supervisor.State == ProtectionState.Stopped) return Array.Empty<CensorRegion>();
            var tracks = _tracker.Snapshot();
            _confidenceGate.RetainOnly(tracks.Select(t => t.Id));
            var masks = CensorComposer.Build(tracks, settings, bounds, nowMs, false, _confidenceGate);
            var missingCoverage = _coverageGuard.RequiresCover(tracks.Select(t => t.Id),
                masks.Where(r => r.TrackId.HasValue).Select(r => r.TrackId!.Value),
                tracks.Where(t => !t.IsPerson && settings.Detection.EnabledRegions.Contains(t.Region) &&
                    nowMs - t.LastSeenMs <= settings.Censoring.TrackPersistenceMs).Select(t => t.Id));
            _supervisor.ReportCoverage(!missingCoverage);
            _supervisor.Evaluate(nowMs);
            if (!_supervisor.EffectiveFailClosed(settings)) return masks;
            return masks.Concat(CensorComposer.Build(Array.Empty<TrackedDetection>(), settings, bounds,
                nowMs, true)).ToList();
        }
    }

    public IReadOnlyList<TrackedDetection> SnapshotTracks()
    {
        return _tracker.Snapshot();
    }

    public void Reset()
    {
        lock (_gate)
        lock (_publicationGate)
        {
            _supervisor.Stop();
            _tracker.Reset();
            _confidenceGate.Reset();
            _coverageGuard.Reset();
            _consecutiveFailures = 0;
            _lastError = null;
            _lastSegmentationMs = long.MinValue / 2;
            _lastBodyMs = long.MinValue / 2;
            _lastBodyDetections = Array.Empty<Detection>();
        }
    }

    public void Dispose()
    {
        StopProtection();
        lock (_gate)
            if (_ownsModels) _models.Dispose();
    }
}
