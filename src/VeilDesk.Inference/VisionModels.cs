using VeilDesk.Core.Settings;
using VeilDesk.Core.Detection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VeilDesk.Inference;

public sealed class VisionPipelineOptions
{
    public required string ModelDirectory { get; init; }
    public bool UseDirectML { get; init; } = true;
    public bool UseHighResolutionPersonModel { get; init; }
    public int InferenceThreads { get; init; }

    public bool EnableFace { get; init; }
    public bool EnableNude { get; init; } = true;
    public bool EnablePerson { get; init; } = true;
    public bool EnableSegmentation { get; init; } = true;
    public string FaceModelPath => Path.Combine(ModelDirectory, "face_detection_yunet_2023mar.onnx");
    public static VisionPipelineOptions ForSettings(string directory, AppSettings s) => new()
    {
        ModelDirectory = directory, UseDirectML = s.Performance.UseDirectML,
        InferenceThreads = s.Performance.InferenceThreads,
        UseHighResolutionPersonModel = s.Performance.Preset == PerformancePreset.High,
        EnableFace = s.Detection.EnabledRegions.Contains(BodyRegion.Face),
        EnableNude = s.Detection.EnabledRegions.Any(r => (int)r < 18) ||
            (s.Detection.WholeBodyEnabled && s.Detection.WholeBodyNudeOnly),
        EnablePerson = s.Detection.WholeBodyEnabled,
        EnableSegmentation = s.Detection.WholeBodyEnabled
    };

    public string NudeNetModelPath => Path.Combine(ModelDirectory, "nudenet-320n.onnx");
    public string PersonModelPath => Path.Combine(ModelDirectory,
        UseHighResolutionPersonModel ? "yolov8n-480.onnx" : "yolov8n-320.onnx");
    public string SegmentationModelPath => Path.Combine(ModelDirectory, "selfie_segmentation.onnx");
    public int PersonModelSize => UseHighResolutionPersonModel ? 480 : 320;

    public string? Validate()
    {
        foreach (var path in new[] { EnableFace ? FaceModelPath : null, EnableNude ? NudeNetModelPath : null,
            EnablePerson ? PersonModelPath : null, EnableSegmentation ? SegmentationModelPath : null }.OfType<string>())
        {
            if (!File.Exists(path)) return $"Missing model: {path}";
        }
        return null;
    }
}

/// <summary>
/// Owns the selected ONNX Runtime sessions and reports which execution provider
/// actually initialised. No network access, telemetry, or remote inference.
/// </summary>
public interface IVisionModelRunner : IDisposable
{
    string Provider { get; }
    string? DirectMLError { get; }
    float[] RunNudeNet(float[] input, int size);
    float[] RunPerson(float[] input, int size);
    float[] RunSegmentation(float[] input);
    IReadOnlyDictionary<string, float[]> RunFace(float[] input) => throw new NotSupportedException("Face model not loaded.");
}

public sealed class VisionModels : IVisionModelRunner
{
    private readonly InferenceSession? _nude;
    private readonly InferenceSession? _person;
    private readonly InferenceSession? _segmentation;
    private readonly InferenceSession? _face;
    private readonly string _nudeInput;
    private readonly string _personInput;
    private readonly string _segmentationInput;
    private readonly string _outputName;
    private readonly object _runGate = new();

    private VisionModels(
        InferenceSession? nude,
        InferenceSession? person,
        InferenceSession? segmentation,
        string provider,
        string? dmlError, InferenceSession? face = null)
    {
        _face = face;
        _nude = nude;
        _person = person;
        _segmentation = segmentation;
        Provider = provider;
        DirectMLError = dmlError;
        _nudeInput = nude?.InputMetadata.Keys.First() ?? "";
        _personInput = person?.InputMetadata.Keys.First() ?? "";
        _segmentationInput = segmentation?.InputMetadata.Keys.First() ?? "";
        _outputName = nude?.OutputMetadata.Keys.First() ?? "";
    }

    public string Provider { get; }
    public string? DirectMLError { get; }
    public bool UsingDirectML => Provider == "DirectML";

    public static VisionModels Load(VisionPipelineOptions options)
    {
        var validation = options.Validate();
        if (validation is not null) throw new FileNotFoundException(validation);

        string? dmlError = null;
        foreach (var dml in options.UseDirectML ? new[] { true, false } : new[] { false })
        {
            InferenceSession? nude = null, person = null, seg = null, face = null;
            try
            {
                if (options.EnableNude) nude = CreateSession(options.NudeNetModelPath, dml, options.InferenceThreads);
                if (options.EnablePerson) person = CreateSession(options.PersonModelPath, dml, options.InferenceThreads);
                if (options.EnableSegmentation) seg = CreateSession(options.SegmentationModelPath, dml, options.InferenceThreads);
                if (options.EnableFace)
                {
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(options.FaceModelPath)));
                    if (hash != "8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4")
                        throw new InvalidDataException("YuNet model integrity check failed.");
                    face = CreateSession(options.FaceModelPath, dml, options.InferenceThreads);
                    if (!face.InputMetadata.Values.First().Dimensions.SequenceEqual(new[] { 1, 3, 640, 640 }))
                        throw new InvalidDataException("Unexpected YuNet input shape.");
                }
                return new VisionModels(nude, person, seg, dml ? "DirectML" : "CPU", dmlError, face);
            }
            catch (Exception ex)
            {
                nude?.Dispose(); person?.Dispose(); seg?.Dispose(); face?.Dispose();
                if (!dml) throw;
                dmlError = ex.Message;
            }
        }
        throw new InvalidOperationException("No execution provider available.");
    }

    private static InferenceSession CreateSession(string path, bool directml, int threads)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = !directml,
        };
        if (threads > 0)
        {
            opts.IntraOpNumThreads = threads;
            opts.InterOpNumThreads = 1;
        }
        try
        {
            if (directml) opts.AppendExecutionProvider_DML(0);
            return new InferenceSession(path, opts);
        }
        finally
        {
            opts.Dispose();
        }
    }

    public float[] RunNudeNet(float[] input, int size)
    {
        return Run(_nude!, _nudeInput, _outputName, input, size);
    }

    public float[] RunPerson(float[] input, int size)
    {
        return Run(_person!, _personInput, _person!.OutputMetadata.Keys.First(), input, size);
    }

    public float[] RunSegmentation(float[] input)
    {
        return Run(_segmentation!, _segmentationInput, _segmentation!.OutputMetadata.Keys.First(),
            input, FramePreprocessor.SegmentationSize);
    }

    private float[] Run(InferenceSession session, string inputName, string outputName,
        float[] input, int size)
    {
        // A shared model set is used by one pipeline per monitor. ONNX Runtime
        // allows concurrent Run calls, but serialising avoids oversubscribing
        // the same CPU/iGPU and keeps latency predictable on laptops.
        lock (_runGate)
        {
            var tensor = new DenseTensor<float>(input, new[] { 1, 3, size, size });
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
            return results.First(r => r.Name == outputName).AsTensor<float>().ToArray();
        }
    }

    public IReadOnlyDictionary<string, float[]> RunFace(float[] input)
    {
        lock (_runGate)
        {
            var face = _face ?? throw new InvalidOperationException("Face model is disabled; restart after changing targets.");
            var tensor = new DenseTensor<float>(input, new[] { 1, 3, 640, 640 });
            using var result = face.Run(new[] { NamedOnnxValue.CreateFromTensor(face.InputMetadata.Keys.First(), tensor) });
            return result.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
        }
    }

    public void Dispose()
    {
        _nude?.Dispose();
        _person?.Dispose();
        _segmentation?.Dispose();
        _face?.Dispose();
    }
}
