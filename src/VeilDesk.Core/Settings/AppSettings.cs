using System.Text.Json.Serialization;

namespace VeilDesk.Core.Settings;

public enum CensorStyle
{
    Solid,
    Blur,
    Pixelate,
    Image,
    Text,
    Outline,
    Silhouette,
    Marker,
    NightVision,
    FrostedGlass
}

public enum PerformancePreset
{
    Low,
    Balanced,
    High,
    Custom
}

public sealed class DetectionSettings
{
    public HashSet<Core.Detection.BodyRegion> EnabledRegions { get; set; } = new()
    {
        Core.Detection.BodyRegion.FemaleBreastExposed,
        Core.Detection.BodyRegion.FemaleGenitaliaExposed,
        Core.Detection.BodyRegion.MaleGenitaliaExposed,
        Core.Detection.BodyRegion.ButtocksExposed,
        Core.Detection.BodyRegion.AnusExposed
    };

    public double Confidence { get; set; } = 0.35;
    public double NmsIou { get; set; } = 0.45;

    public bool WholeBodyEnabled { get; set; }
    public bool WholeBodyNudeOnly { get; set; } = true;
    public bool WholeBodyFemale { get; set; } = true;
    public bool WholeBodyMale { get; set; } = true;
    public double PersonConfidence { get; set; } = 0.35;
    public double WholeBodyConfidence { get; set; } = 0.5;
}

public sealed class CensorSettings
{
    public CensorStyle Style { get; set; } = CensorStyle.Solid;
    public string Color { get; set; } = "#0B0D12";
    public double Opacity { get; set; } = 1.0;
    public double BorderThickness { get; set; }
    public string BorderColor { get; set; } = "#FFFFFF";
    public double CornerRadius { get; set; } = 3;
    public double BlurRadius { get; set; } = 18;
    public int PixelBlock { get; set; } = 14;
    public string CustomText { get; set; } = "HIDDEN";
    public string TextColor { get; set; } = "#FFFFFF";
    public string ImagePath { get; set; } = "";
    public bool Reverse { get; set; }
    public double OutlineMargin { get; set; } = 4;
    public double MaskExpansion { get; set; } = 6;
    public double MotionPadding { get; set; } = 0.18;
    public int PredictionHorizonMs { get; set; } = 80;
    /// <summary>
    /// How long a censor mask stays valid after the last detection that
    /// supported it. This is the window in which a temporary detection failure
    /// does not expose the subject.
    /// </summary>
    public int TrackPersistenceMs { get; set; } = 2000;
    public bool FailClosed { get; set; } = true;

    /// <summary>
    /// How long the pipeline may go without a successful analysis before the
    /// whole output is covered.
    /// </summary>
    public int FailClosedAfterMs { get; set; } = 1500;

    /// <summary>
    /// Minimum time the safety cover stays in place before it may be lifted.
    /// This is the hysteresis that stops the cover from blinking on and off.
    /// </summary>
    public int FailClosedRecoveryMs { get; set; } = 1000;

    /// <summary>
    /// Number of consecutive successful analyses required before the safety
    /// cover is lifted. A new inference attempt alone never clears it.
    /// </summary>
    public int FailClosedRecoveryPasses { get; set; } = 2;

    /// <summary>
    /// Hysteresis ratio for the confidence gate. A mask is released when the
    /// smoothed confidence falls below <c>threshold * ratio</c>, so a single
    /// marginal detection cannot make the mask disappear.
    /// </summary>
    public double ConfidenceReleaseRatio { get; set; } = 0.7;
    public bool ShowDebugBoxes { get; set; }
    public bool ShowSubjectLabels { get; set; }
}

public sealed class PerformanceSettings
{
    public PerformancePreset Preset { get; set; } = PerformancePreset.Balanced;
    public int ScanRateHz { get; set; } = 12;
    public int BodyScanRateHz { get; set; } = 6;
    public bool AdaptiveScanning { get; set; } = true;
    public double StaticSceneThreshold { get; set; } = 0.004;
    public int SegmentationRateHz { get; set; } = 4;
    public bool UseDirectML { get; set; }
    public int OverlayFps { get; set; } = 60;
    public int OverlaySourceWidth { get; set; } = 1280;
    public int MaxSegmentationsPerFrame { get; set; } = 2;
    public int InferenceThreads { get; set; }
}

public sealed class CaptureSettings
{
    /// <summary>Empty means every connected display.</summary>
    public List<string> EnabledMonitorIds { get; set; } = new();
    public bool CaptureCursor { get; set; }
    public int CaptureIntervalMs { get; set; } = 16;
    public bool ExcludeOwnWindows { get; set; } = true;
}

public sealed class CreatorSettings
{
    public string ExportDirectory { get; set; } = "";
    public int VideoFps { get; set; } = 30;
    public string VideoFormat { get; set; } = "mp4";
    public bool IncludeCursor { get; set; }
    public bool Watermark { get; set; }
    public string WatermarkText { get; set; } = "Protected with VeilDesk";
}

public sealed class SessionSettings
{
    public bool TrackStats { get; set; } = true;
    public bool Achievements { get; set; } = true;
    public bool DiscordPresence { get; set; }
}

public sealed class UiSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Midnight";
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; }
    public bool OnboardingComplete { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public DetectionSettings Detection { get; set; } = new();
    public CensorSettings Censoring { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public CreatorSettings Creator { get; set; } = new();
    public SessionSettings Sessions { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    [JsonIgnore]
    public bool AnyRegionEnabled => Detection.EnabledRegions.Count > 0 || Detection.WholeBodyEnabled;

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions) ?? new AppSettings();
    }
}

public static class SettingsValidator
{
    public static void ValidateAndClamp(AppSettings settings)
    {
        var d = settings.Detection;
        d.Confidence = Math.Clamp(d.Confidence, 0.05, 0.95);
        d.NmsIou = Math.Clamp(d.NmsIou, 0.1, 0.95);
        d.PersonConfidence = Math.Clamp(d.PersonConfidence, 0.05, 0.95);
        d.WholeBodyConfidence = Math.Clamp(d.WholeBodyConfidence, 0.05, 0.95);
        d.EnabledRegions ??= new();

        var c = settings.Censoring;
        c.Opacity = Math.Clamp(c.Opacity, 0.1, 1.0);
        c.BorderThickness = Math.Clamp(c.BorderThickness, 0, 24);
        c.CornerRadius = Math.Clamp(c.CornerRadius, 0, 64);
        c.BlurRadius = Math.Clamp(c.BlurRadius, 0, 80);
        c.PixelBlock = Math.Clamp(c.PixelBlock, 2, 96);
        c.OutlineMargin = Math.Clamp(c.OutlineMargin, 0, 64);
        c.MaskExpansion = Math.Clamp(c.MaskExpansion, 0, 96);
        c.MotionPadding = Math.Clamp(c.MotionPadding, 0, 1.0);
        c.PredictionHorizonMs = Math.Clamp(c.PredictionHorizonMs, 0, 500);
        c.TrackPersistenceMs = Math.Clamp(c.TrackPersistenceMs, 0, 5000);
        c.FailClosedAfterMs = Math.Clamp(c.FailClosedAfterMs, 0, 5000);
        c.FailClosedRecoveryMs = Math.Clamp(c.FailClosedRecoveryMs, 0, 10000);
        c.FailClosedRecoveryPasses = Math.Clamp(c.FailClosedRecoveryPasses, 1, 10);
        c.ConfidenceReleaseRatio = Math.Clamp(c.ConfidenceReleaseRatio, 0.1, 1.0);

        // Safety invariant: the censor mask must stay valid at least as long
        // as the pipeline is allowed to produce no evidence. Otherwise the
        // mask would expire first and the safety cover would only arrive
        // afterwards, leaving a window in which nothing is censored at all.
        if (c.TrackPersistenceMs < c.FailClosedAfterMs) c.TrackPersistenceMs = c.FailClosedAfterMs;
        if (!IsHexColor(c.Color)) c.Color = "#0B0D12";
        if (!IsHexColor(c.BorderColor)) c.BorderColor = "#FFFFFF";
        if (!IsHexColor(c.TextColor)) c.TextColor = "#FFFFFF";
        c.CustomText = (c.CustomText ?? "").Trim();
        if (c.CustomText.Length > 64) c.CustomText = c.CustomText[..64];

        var p = settings.Performance;
        p.ScanRateHz = Math.Clamp(p.ScanRateHz, 1, 60);
        p.BodyScanRateHz = Math.Clamp(p.BodyScanRateHz, 1, 30);
        p.StaticSceneThreshold = Math.Clamp(p.StaticSceneThreshold, 0.0001, 0.25);
        p.SegmentationRateHz = Math.Clamp(p.SegmentationRateHz, 1, 30);
        p.OverlayFps = Math.Clamp(p.OverlayFps, 15, 144);
        p.OverlaySourceWidth = Math.Clamp(p.OverlaySourceWidth, 640, 3840);
        p.MaxSegmentationsPerFrame = Math.Clamp(p.MaxSegmentationsPerFrame, 1, 8);
        p.InferenceThreads = Math.Clamp(p.InferenceThreads, 0, 32);

        var cap = settings.Capture;
        cap.EnabledMonitorIds ??= new();
        cap.CaptureIntervalMs = Math.Clamp(cap.CaptureIntervalMs, 8, 200);

        var creator = settings.Creator;
        creator.VideoFps = Math.Clamp(creator.VideoFps, 10, 60);
        creator.VideoFormat = creator.VideoFormat is "mp4" or "mkv" ? creator.VideoFormat : "mp4";
        creator.WatermarkText = (creator.WatermarkText ?? "").Trim();
        if (creator.WatermarkText.Length > 80) creator.WatermarkText = creator.WatermarkText[..80];
        // A folder that is not a usable absolute path (a pasted URL, a
        // relative path, illegal characters) is dropped instead of being
        // persisted and later crashing a file write.
        creator.ExportDirectory = (creator.ExportDirectory ?? "").Trim();
        if (creator.ExportDirectory.Length > 0)
        {
            if (ExportDirectory.TryResolve(creator.ExportDirectory, out var resolved, out _))
                creator.ExportDirectory = resolved;
            else
                creator.ExportDirectory = string.Empty;
        }

        if (settings.Ui.Language is not ("en" or "ko")) settings.Ui.Language = "en";
        if (settings.SchemaVersion < 1) settings.SchemaVersion = 1;
    }

    public static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (6 or 8)) return false;
        return s.All(Uri.IsHexDigit);
    }
}
