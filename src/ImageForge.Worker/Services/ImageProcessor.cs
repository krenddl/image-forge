using ImageForge.Shared.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ImageForge.Worker.Services;

// The "what" of the worker: take a TaskMessage, load the source image,
// optionally resize, re-encode in the requested format, write to results/.
// Progress is reported through a caller-supplied callback so the processor
// itself stays unaware of Redis or any other storage.
public sealed class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly WorkerStorage _storage;

    public ImageProcessor(WorkerStorage storage, ILogger<ImageProcessor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(
        TaskMessage message,
        Func<int, Task> reportProgress,
        CancellationToken ct)
    {
        // 25% — file opened and decoded into memory.
        using var image = await Image.LoadAsync(message.SourcePath, ct);

        // Apply EXIF orientation BEFORE any further work. Phone cameras store
        // landscape pixels with an orientation tag telling viewers "rotate 90
        // / 180 / 270 when displaying". WebP doesn't preserve EXIF reliably,
        // so we bake the rotation into the pixels themselves; the exported
        // file will be visually correct regardless of consumer EXIF support.
        image.Mutate(x => x.AutoOrient());

        await reportProgress(25);

        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // 60% — resize finished (or skipped).
        if (message.MaxDimension is int max && (originalWidth > max || originalHeight > max))
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(max, max),
                Mode = ResizeMode.Max
            }));
        }
        await reportProgress(60);

        IImageEncoder encoder = SelectEncoder(message.TargetFormat);

        // 90% — encoded and written to disk; the consumer will mark 100/done.
        var resultPath = _storage.BuildResultPath(message.TaskId, message.TargetFormat);
        await image.SaveAsync(resultPath, encoder, ct);
        await reportProgress(90);

        _logger.LogInformation(
            "Processed task {TaskId}: {OriginalW}x{OriginalH} -> {NewW}x{NewH} as {Format} ({Path})",
            message.TaskId, originalWidth, originalHeight, image.Width, image.Height,
            message.TargetFormat, resultPath);

        return resultPath;
    }

    private static IImageEncoder SelectEncoder(string targetFormat)
    {
        return targetFormat.ToLowerInvariant() switch
        {
            "webp" => new WebpEncoder { Quality = 80 },
            "jpg" or "jpeg" => new JpegEncoder { Quality = 80 },
            "png" => new PngEncoder(),
            _ => throw new NotSupportedException($"Target format '{targetFormat}' is not supported.")
        };
    }
}
