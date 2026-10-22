using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VeilDesk.Inference;

public sealed class VisionPipelineOptions
{
    public required string ModelDirectory { get; init; }
    public bool UseDirectML { get; init; } = true;
    public bool UseHighResolutionPersonModel { get; init; }
    public int InferenceThreads { get; init; }

    public string NudeNetModelPath => Path.Combine(ModelDirectory, "nudenet-320n.onnx");
    public string PersonModelPath => Path.Combine(ModelDirectory,
        UseHighResolutionPersonModel ? "yolov8n-480.onnx" : "yolov8n-320.onnx");
    public string SegmentationModelPath => Path.Combine(ModelDirectory, "selfie_segmentation.onnx");
    public int PersonModelSize => UseHighResolutionPersonModel ? 480 : 320;

    public string? Validate()
    {
        foreach (var path in new[] { NudeNetModelPath, PersonModelPath, SegmentationModelPath })
        {
            if (!File.Exists(path)) return $"Missing model: {path}";
        }
        return null;
    }
}

/// <summary>
/// Owns the three ONNX Runtime sessions and reports which execution provider
/// actually initialised. No network access, telemetry, or remote inference.
/// </summary>
public sealed class VisionModels : IDisposable
{
    private readonly InferenceSession _nude;
    private readonly InferenceSession _person;
    private readonly InferenceSession _segmentation;
    private readonly string _nudeInput;
    private readonly string _personInput;
    private readonly string _segmentationInput;
    private readonly string _outputName;
    private readonly object _runGate = new();

    private VisionModels(
        InferenceSession nude,
        InferenceSession person,
        InferenceSession segmentation,
        string provider,
        string? dmlError)
    {
        _nude = nude;
        _person = person;
        _segmentation = segmentation;
        Provider = provider;
        DirectMLError = dmlError;
        _nudeInput = nude.InputMetadata.Keys.First();
        _personInput = person.InputMetadata.Keys.First();
        _segmentationInput = segmentation.InputMetadata.Keys.First();
        _outputName = nude.OutputMetadata.Keys.First();
    }

    public string Provider { get; }
    public string? DirectMLError { get; }
    public bool UsingDirectML => Provider == "DirectML";

    public static VisionModels Load(VisionPipelineOptions options)
    {
        var validation = options.Validate();
        if (validation is not null) throw new FileNotFoundException(validation);

        string? dmlError = null;
        if (options.UseDirectML)
        {
            InferenceSession? nude = null;
            InferenceSession? person = null;
            InferenceSession? seg = null;
            try
            {
                nude = CreateSession(options.NudeNetModelPath, true, options.InferenceThreads);
                person = CreateSession(options.PersonModelPath, true, options.InferenceThreads);
                seg = CreateSession(options.SegmentationModelPath, true, options.InferenceThreads);
                return new VisionModels(nude, person, seg, "DirectML", null);
            }
            catch (Exception ex)
            {
                dmlError = ex.Message;
                nude?.Dispose();
                person?.Dispose();
                seg?.Dispose();
            }
        }

        var cpuNude = CreateSession(options.NudeNetModelPath, false, options.InferenceThreads);
        var cpuPerson = CreateSession(options.PersonModelPath, false, options.InferenceThreads);
        var cpuSeg = CreateSession(options.SegmentationModelPath, false, options.InferenceThreads);
        return new VisionModels(cpuNude, cpuPerson, cpuSeg, "CPU", dmlError);
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
        if (directml) opts.AppendExecutionProvider_DML(0);
        try
        {
            return new InferenceSession(path, opts);
        }
        finally
        {
            opts.Dispose();
        }
    }

    public float[] RunNudeNet(float[] input, int size)
    {
        return Run(_nude, _nudeInput, _outputName, input, size);
    }

    public float[] RunPerson(float[] input, int size)
    {
        return Run(_person, _personInput, _person.OutputMetadata.Keys.First(), input, size);
    }

    public float[] RunSegmentation(float[] input)
    {
        return Run(_segmentation, _segmentationInput, _segmentation.OutputMetadata.Keys.First(),
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

    public void Dispose()
    {
        _nude.Dispose();
        _person.Dispose();
        _segmentation.Dispose();
    }
}
