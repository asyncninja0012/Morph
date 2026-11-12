using System.Runtime.InteropServices;

using ImageMagick;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Morph.Core.Jobs;
using Morph.Core.Packs;
using Morph.Engines.Imaging;

namespace Morph.Engines.Ai;

/// <summary>
/// Remove background (IT-4, PK-2): BiRefNet-lite (MIT) exported at 512×512, on ONNX Runtime's CPU provider, from the bg-model
/// pack (<c>packs\bg-model\&lt;version&gt;\model.onnx</c> + <c>onnxruntime.dll</c>). The picture is read upright, squeezed to
/// 512×512 and normalised (ImageNet mean/std); the model's mask is sigmoided, stretched back to the picture's size and
/// becomes its alpha channel. Saves <c>name-nobg.png</c>. The 1024×1024 export needed ~6 GB and ~17 s a picture on an 8-core
/// CPU; this one ~1.7 GB and ~3 s (plus ~3 s to load), with cut-outs that look the same at screen size.
/// </summary>
/// <param name="model">The model to use; null = the installed bg-model pack.</param>
public sealed class BackgroundEngine(string? model = null) : IEngine
{
    public const string PackId = "bg-model";
    private const int Side = 512;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    static BackgroundEngine()
    {
        // onnxruntime.dll ships in the pack, not with Morph; the build and tests have their own copy next to the exe.
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, assembly, path) =>
            name == "onnxruntime" && PackStore.Installed(PackStore.DefaultRoot, PackId, "onnxruntime.dll") is { } dll
                ? NativeLibrary.Load(dll) : NativeLibrary.TryLoad(name, assembly, path, out var handle) ? handle : 0);
    }

    public string Id => "ai";

    public async Task RunAsync(JobRequest job, OutputNamer namer, IProgress<JobProgress> progress, CancellationToken ct)
    {
        if (job.ToolId != "remove-bg")
        {
            throw new EngineException("This tool isn't available yet.", $"ai: unsupported tool {job.ToolId}");
        }

        var path = model ?? PackStore.Installed(PackStore.DefaultRoot, PackId, "model.onnx");
        if (path is null || !File.Exists(path))
        {
            throw new EngineException("Remove background needs Morph's background pack.", $"bg-model pack not installed ({path})");
        }

        progress.Report(new JobProgress(double.NaN, "Loading the model"));
        // No memory arena: it keeps every buffer it ever grew and took 4.5 GB instead of 1.7 GB (the Worker's limit is 4 GB), for 0.3 s.
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false, EnableMemoryPattern = false };
        using var session = await Task.Run(() => new InferenceSession(path, options), ct).ConfigureAwait(false);

        for (var i = 0; i < job.Inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var image = await ImagingEngine.ReadAsync(job.Inputs[i], ct).ConfigureAwait(false);
            using var mask = await Task.Run(() => Mask(session, image), ct).ConfigureAwait(false);
            mask.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
            image.Alpha(AlphaOption.Set);
            image.Composite(mask, CompositeOperator.CopyAlpha);
            image.Strip(); // the PNG is a new picture: no camera or location info carried over
            await image.WriteAsync(namer.Reserve(job.Inputs[i], ".png", "-nobg").Path, MagickFormat.Png, ct).ConfigureAwait(false);
            progress.Report(new JobProgress((i + 1.0) / job.Inputs.Count, null));
        }
    }

    // The model's view of the picture: 512×512 RGB, normalised; out comes one logit per pixel, 0..255 after the sigmoid.
    private static MagickImage Mask(InferenceSession session, MagickImage image)
    {
        using var small = (MagickImage)image.Clone();
        small.ColorSpace = ColorSpace.sRGB;
        small.Alpha(AlphaOption.Remove); // transparent areas count as the background colour
        small.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        using var pixels = small.GetPixelsUnsafe();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidOperationException("Magick gave no pixels.");

        var input = new DenseTensor<float>([1, 3, Side, Side]);
        var plane = Side * Side;
        for (var p = 0; p < plane; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                input.Buffer.Span[c * plane + p] = (rgb[p * 3 + c] / 255f - Mean[c]) / Std[c];
            }
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
        var logits = results[0].AsEnumerable<float>().ToArray(); // [1, 1, 512, 512]
        var alpha = new byte[plane];
        for (var p = 0; p < plane; p++)
        {
            alpha[p] = (byte)Math.Round(255 / (1 + MathF.Exp(-logits[p])));
        }

        var mask = new MagickImage();
        mask.ReadPixels(alpha, new PixelReadSettings(Side, Side, StorageType.Char, "I"));
        mask.ColorType = ColorType.Grayscale;
        return mask;
    }
}
