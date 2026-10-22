using System.IO;
using VeilDesk.Core.Settings;
using VeilDesk.Inference;

namespace VeilDesk.App;

/// <summary>
/// Finds the model directory without embedding weights into the executable.
/// Release builds copy models next to the app; development builds search up
/// from the output folder so running from the repository works unchanged.
/// </summary>
public static class ModelLocator
{
    public static string? Find(AppSettings? settings = null)
    {
        var candidates = new List<string>();
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "models"));
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeilDesk", "models");
        candidates.Add(local);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "models"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "manifest.json")) &&
                (File.Exists(Path.Combine(candidate, "nudenet-320n.onnx")) ||
                 File.Exists(Path.Combine(candidate, "face_detection_yunet_2023mar.onnx"))) &&
                (settings is null || VisionPipelineOptions.ForSettings(candidate, settings).Validate() is null))
                return candidate;
        }
        return null;
    }

    public static string DescribeSearched()
        => string.Join("; ", new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeilDesk", "models")
        });
}
