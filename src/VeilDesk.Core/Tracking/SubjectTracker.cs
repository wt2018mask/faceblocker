using VeilDesk.Core.Detection;
using VeilDesk.Core.Geometry;
using VeilDesk.Core.Vision;
using DetectionRecord = VeilDesk.Core.Detection.Detection;

namespace VeilDesk.Core.Tracking;

public enum PersonGender
{
    Unknown,
    Female,
    Male
}

/// <summary>A live tracked prediction. Instances are mutated only by the tracker.</summary>
public sealed class TrackedDetection
{
    public required int Id { get; init; }
    public required BodyRegion Region { get; init; }
    public PixelRect Box { get; internal set; }
    public PixelRect SmoothedBox { get; internal set; }

    /// <summary>
    /// The best current estimate of where the subject is. It equals
    /// <see cref="Box"/> while the subject is detected. When the subject is
    /// missed but the visible content is measured to have moved, the estimate
    /// follows that content so the mask stays on the subject.
    /// </summary>
    public PixelRect CompensatedBox { get; internal set; }

    /// <summary>
    /// Smoothed confidence of the track. This is an exponential moving average
    /// of the raw detector scores, so a single marginal detection cannot make
    /// the confidence collapse and the censor mask disappear.
    /// </summary>
    public double Confidence { get; internal set; }

    /// <summary>Confidence reported by the most recent matching detection.</summary>
    public double RawConfidence { get; internal set; }

    /// <summary>The best raw confidence this track has ever received.</summary>
    public double PeakConfidence { get; internal set; }

    public double VelocityX { get; internal set; }
    public double VelocityY { get; internal set; }

    /// <summary>
    /// Velocity relative to the content the subject belongs to. It removes the
    /// measured motion of the visible content, so a face that only scrolls with
    /// the page is not predicted to move inside the page.
    /// </summary>
    public double ContentVelocityX { get; internal set; }
    public double ContentVelocityY { get; internal set; }

    public long FirstSeenMs { get; init; }
    public long LastSeenMs { get; internal set; }
    public long LastPredictionMs { get; internal set; }
    public int Missed { get; internal set; }

    /// <summary>Evidence of the most recent matching raw candidate.</summary>
    public FaceCandidateEvidence? FaceEvidence { get; internal set; }

    public byte[]? Mask { get; internal set; }
    public int MaskWidth { get; internal set; }
    public int MaskHeight { get; internal set; }
    public PixelRect MaskBox { get; internal set; }
    public bool HasExposedEvidence { get; internal set; }
    public long LastExposureEvidenceMs { get; internal set; }
    public PersonGender Gender { get; internal set; }
    public bool IsPerson => Region == BodyRegion.Person;

    /// <summary>Milliseconds since the subject was last detected.</summary>
    public long AgeMs(long nowMs) => Math.Max(0, nowMs - LastSeenMs);

    internal TrackedDetection Copy() => (TrackedDetection)MemberwiseClone();

    public PixelRect PredictedBox(long nowMs, int horizonMs, double motionPadding, PixelRect bounds)
    {
        if (Region == BodyRegion.Face)
        {
            // Holding a missed detection is a temporal policy, not permission
            // to extrapolate it across the desktop for the full hold interval.
            // Keep the complete observed box and a bounded local prediction.
            return VeilDesk.Core.Censoring.FaceMaskGeometry.Predict(CompensatedBox,
                ContentVelocityX, ContentVelocityY, horizonMs, motionPadding, bounds);
        }
        var seconds = Math.Max(0, nowMs - LastSeenMs) / 1000.0 + Math.Max(0, horizonMs) / 1000.0;
        // Preserve the existing person/anatomical prediction policy.
        var predicted = SmoothedBox.Offset(VelocityX * seconds, VelocityY * seconds);
        var speed = Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);
        // Uncertainty grows with time since detection and with observed speed.
        var uncertainty = motionPadding + Math.Min(0.8, speed / 3000.0) * 0.25 + Math.Min(0.4, seconds);
        predicted = predicted.ExpandFraction(uncertainty);
        return predicted.Clip(bounds);
    }
}

public sealed class TrackerOptions
{
    public double MinimumIoU { get; set; } = 0.08;
    public double MaximumCenterDistanceFraction { get; set; } = 0.35;
    public double VelocitySmoothing { get; set; } = 0.45;
    public double BoxSmoothing { get; set; } = 0.65;
    public int MaxMissedFrames { get; set; } = 30;
    public int EvidenceMemoryMs { get; set; } = 2000;

    /// <summary>
    /// Weight of the newest detector score in the smoothed track confidence.
    /// 1.0 would disable smoothing; 0.4 keeps a marginal detection from
    /// removing a censor mask that a strong detection just established.
    /// </summary>
    public double ConfidenceSmoothing { get; set; } = 0.4;

    /// <summary>
    /// How far the area of a new detection may differ from the area of the
    /// track it would take over. Outside this band the detection is treated as
    /// a different subject, unless it is clearly more confident than the
    /// strongest detection that ever established the track.
    /// </summary>
    public double MaximumAreaRatio { get; set; } = 4.0;
}

/// <summary>
/// Lightweight online multi-object tracker. It intentionally avoids external
/// native tracking dependencies: association by IoU/center distance, exponential
/// box smoothing, constant-velocity prediction and configurable persistence.
/// </summary>
public sealed class SubjectTracker
{
    private readonly List<TrackedDetection> _tracks = new();
    private readonly object _gate = new();
    private int _nextId;
    private long _lastUpdateMs;
    private bool _hasLastUpdate;
    private readonly TrackerOptions _options;

    public SubjectTracker(TrackerOptions? options = null)
    {
        _options = options ?? new TrackerOptions();
    }

    public IReadOnlyList<TrackedDetection> Snapshot()
    {
        lock (_gate) return _tracks.Select(t => t.Copy()).ToList();
    }

    public int ActiveTrackCount { get { lock (_gate) return _tracks.Count; } }

    /// <summary>
    /// Updates the tracks. <paramref name="motion"/> carries the measured
    /// motion of the visible content; it is optional so that callers without
    /// motion evidence keep the previous behaviour.
    /// </summary>
    public IReadOnlyList<TrackedDetection> Update(IReadOnlyList<DetectionRecord> detections, long nowMs,
        PixelRect bounds, SceneMotion? motion = null)
    {
        lock (_gate)
        {
            var dt = _hasLastUpdate ? Math.Max(0, nowMs - _lastUpdateMs) / 1000.0 : 0.0;
            _lastUpdateMs = nowMs;
            _hasLastUpdate = true;

            var unmatchedTracks = new HashSet<int>(_tracks.Select(t => t.Id));
            var unmatchedDetections = new List<DetectionRecord>(detections);

            // Higher-confidence and larger detections claim tracks first.
            var orderedDetections = detections
                .OrderByDescending(d => d.Confidence)
                .ThenByDescending(d => d.Box.Area)
                .ToList();

            foreach (var detection in orderedDetections)
            {
                TrackedDetection? bestTrack = null;
                var bestScore = double.NegativeInfinity;
                foreach (var track in _tracks)
                {
                    if (!unmatchedTracks.Contains(track.Id)) continue;
                    if (track.Region != detection.Region) continue;
                    if ((nowMs - track.LastSeenMs) > 3000) continue;
                    if (!ScaleCompatible(track, detection)) continue;

                    var iou = track.PredictedBox(nowMs, 0, 0.05, bounds).IoU(detection.Box);
                    var diag = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                    var distance = track.SmoothedBox.CenterDistance(detection.Box) / Math.Max(1, diag);
                    if (iou < _options.MinimumIoU && distance > _options.MaximumCenterDistanceFraction) continue;

                    var score = iou * 0.75 + (1.0 - Math.Min(1.0, distance)) * 0.25;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTrack = track;
                    }
                }

                if (bestTrack is null)
                {
                    var track = new TrackedDetection
                    {
                        Id = Interlocked.Increment(ref _nextId),
                        Region = detection.Region,
                        Box = detection.Box,
                        SmoothedBox = detection.Box,
                        CompensatedBox = detection.Box,
                        Confidence = detection.Confidence,
                        RawConfidence = detection.Confidence,
                        PeakConfidence = detection.Confidence,
                        FirstSeenMs = nowMs,
                        LastSeenMs = nowMs,
                        LastPredictionMs = nowMs
                    };
                    track.FaceEvidence = detection.Face;
                    ApplyMask(track, detection);
                    _tracks.Add(track);
                    unmatchedDetections.Remove(detection);
                    continue;
                }

                var previousBox = bestTrack.Region == BodyRegion.Face ? bestTrack.Box : bestTrack.SmoothedBox;
                var previousCenterX = previousBox.CenterX;
                var previousCenterY = previousBox.CenterY;
                // Missed passes are not observations of this face. Dividing
                // its displacement by only the last global update interval
                // inflates its speed; smoothed coordinates also invent motion
                // after a scroll has stopped.
                var velocityDt = bestTrack.Region == BodyRegion.Face
                    ? Math.Max(0, nowMs - bestTrack.LastSeenMs) / 1000.0 : dt;
                if (velocityDt > 0.001)
                {
                    var vx = (detection.Box.CenterX - previousCenterX) / velocityDt;
                    var vy = (detection.Box.CenterY - previousCenterY) / velocityDt;
                    vx = Math.Clamp(vx, -6000, 6000);
                    vy = Math.Clamp(vy, -6000, 6000);
                    bestTrack.VelocityX = bestTrack.VelocityX * (1 - _options.VelocitySmoothing) + vx * _options.VelocitySmoothing;
                    bestTrack.VelocityY = bestTrack.VelocityY * (1 - _options.VelocitySmoothing) + vy * _options.VelocitySmoothing;
                }

                var alpha = _options.BoxSmoothing;
                bestTrack.SmoothedBox = Lerp(bestTrack.SmoothedBox, detection.Box, alpha);
                bestTrack.Box = detection.Box;
                bestTrack.CompensatedBox = detection.Box;
                // The raw detector score is kept for diagnostics, but the
                // confidence that decides whether a censor mask is drawn is
                // smoothed. Without this a single marginal detection removes
                // the mask and the next strong detection restores it, which is
                // exactly the visible flicker this project must not have.
                var confidenceAlpha = Math.Clamp(_options.ConfidenceSmoothing, 0.01, 1.0);
                bestTrack.Confidence = bestTrack.Confidence * (1.0 - confidenceAlpha) +
                    detection.Confidence * confidenceAlpha;
                bestTrack.RawConfidence = detection.Confidence;
                bestTrack.PeakConfidence = Math.Max(bestTrack.PeakConfidence, detection.Confidence);
                bestTrack.LastSeenMs = nowMs;
                bestTrack.LastPredictionMs = nowMs;
                bestTrack.Missed = 0;
                bestTrack.FaceEvidence = detection.Face;
                ApplyMask(bestTrack, detection);
                unmatchedTracks.Remove(bestTrack.Id);
                unmatchedDetections.Remove(detection);
            }

            for (var i = _tracks.Count - 1; i >= 0; i--)
            {
                var track = _tracks[i];
                if (unmatchedTracks.Contains(track.Id))
                {
                    track.Missed++;
                    // A missed face follows the content it belongs to. Without
                    // this the mask stays at the position the face had before
                    // the page scrolled, which is what the user sees as a
                    // stale block that only disappears after the hold window.
                    if (motion is { IsReliable: true })
                    {
                        var (dx, dy) = motion.At(track.CompensatedBox.CenterX, track.CompensatedBox.CenterY);
                        track.CompensatedBox = track.CompensatedBox.Offset(dx, dy);
                    }
                    // Face hold time is elapsed time, not the caller's inference FPS.
                    if ((track.Region != BodyRegion.Face && track.Missed > _options.MaxMissedFrames) ||
                        nowMs - track.LastSeenMs > 5000)
                        _tracks.RemoveAt(i);
                }
                else
                {
                    ApplyContentVelocity(track, motion, dt);
                }
            }

            RetireDepartedTracks(nowMs, bounds, motion);
            UpdatePersonEvidence(nowMs);
            return _tracks.Select(t => t.Copy()).ToList();
        }
    }

    /// <summary>
    /// A detection may only take over a track when it is plausibly the same
    /// subject. A screen-sized rectangle must not be allowed to replace a
    /// small, well established face: the recorded session showed exactly that
    /// and it turned a 134x158 face into a 795x977 mask.
    /// </summary>
    private bool ScaleCompatible(TrackedDetection track, DetectionRecord detection)
    {
        var reference = track.Region == BodyRegion.Face ? track.Box : track.SmoothedBox;
        var referenceArea = Math.Max(1.0, reference.Area);
        var detectionArea = Math.Max(1.0, detection.Box.Area);
        var ratio = detectionArea / referenceArea;
        var limit = Math.Max(1.0, _options.MaximumAreaRatio);
        if (ratio <= limit && ratio >= 1.0 / limit) return true;
        // A very different size is only accepted when the new detection is
        // clearly stronger than anything that ever established the track.
        return detection.Confidence > track.PeakConfidence;
    }

    /// <summary>
    /// Removes the measured content motion from the observed velocity. The
    /// remaining velocity is the movement of the subject inside the content.
    /// </summary>
    private static void ApplyContentVelocity(TrackedDetection track, SceneMotion? motion, double dt)
    {
        if (motion is null || !motion.IsReliable || dt <= 0.0005)
        {
            track.ContentVelocityX = track.VelocityX;
            track.ContentVelocityY = track.VelocityY;
            return;
        }
        var (dx, dy) = motion.At(track.CompensatedBox.CenterX, track.CompensatedBox.CenterY);
        track.ContentVelocityX = track.VelocityX - dx / dt;
        track.ContentVelocityY = track.VelocityY - dy / dt;
    }

    /// <summary>
    /// Retires tracks whose content left the display.
    ///
    /// This is the difference between a mask that lingers for the whole hold
    /// window and one that disappears as soon as the content is gone. The
    /// retirement is also required for safety: a track whose subject left the
    /// display has no mask any more, and the coverage guard would otherwise
    /// ask for a whole-screen cover.
    ///
    /// A replaced scene is deliberately *not* a reason to retire a track. A
    /// detection gap looks the same as a new page, and the hold window exists
    /// exactly to cover such a gap. A replaced scene only stops the motion
    /// compensation, because there is no displacement to follow.
    /// </summary>
    private void RetireDepartedTracks(long nowMs, PixelRect bounds, SceneMotion? motion)
    {
        if (motion is null || !motion.IsReliable) return;
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i];
            if (track.Region != BodyRegion.Face) continue;
            if (track.Missed == 0) continue;
            if (track.CompensatedBox.Clip(bounds).IsEmpty)
            {
                // The face left the display together with the content it
                // belonged to.
                _tracks.RemoveAt(i);
            }
        }
    }

    private void ApplyMask(TrackedDetection track, DetectionRecord detection)
    {
        if (detection.HasMask)
        {
            track.Mask = detection.Mask;
            track.MaskWidth = detection.MaskWidth;
            track.MaskHeight = detection.MaskHeight;
            track.MaskBox = detection.Box;
        }
        else if (!track.IsPerson)
        {
            track.Mask = null;
            track.MaskWidth = 0;
            track.MaskHeight = 0;
        }
    }

    private void UpdatePersonEvidence(long nowMs)
    {
        var bodyTracks = _tracks.Where(t => !t.IsPerson && nowMs - t.LastSeenMs <= _options.EvidenceMemoryMs).ToList();
        foreach (var person in _tracks.Where(t => t.IsPerson))
        {
            var personBox = person.SmoothedBox.ExpandFraction(0.12);
            var overlapping = bodyTracks
                .Where(b => b.SmoothedBox.CenterX >= personBox.X - 32 &&
                            b.SmoothedBox.CenterX <= personBox.Right + 32 &&
                            b.SmoothedBox.CenterY >= personBox.Y - 32 &&
                            b.SmoothedBox.CenterY <= personBox.Bottom + 32 &&
                            (b.SmoothedBox.OverlapOfSmaller(personBox) > 0.15 || b.SmoothedBox.Intersects(personBox)))
                .ToList();

            var exposed = overlapping.Any(b => b.Region.IsExplicitExposed());
            if (exposed)
            {
                person.HasExposedEvidence = true;
                person.LastExposureEvidenceMs = nowMs;
            }
            else if (nowMs - person.LastExposureEvidenceMs > _options.EvidenceMemoryMs)
            {
                person.HasExposedEvidence = false;
            }

            var female = overlapping.Any(b => b.Region.IsGenderFemale());
            var male = overlapping.Any(b => b.Region.IsGenderMale());
            person.Gender = female && !male ? PersonGender.Female
                : male && !female ? PersonGender.Male
                : PersonGender.Unknown;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _lastUpdateMs = 0;
            _hasLastUpdate = false;
        }
    }

    private static PixelRect Lerp(PixelRect a, PixelRect b, double t)
        => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Width + (b.Width - a.Width) * t,
            a.Height + (b.Height - a.Height) * t);
}
