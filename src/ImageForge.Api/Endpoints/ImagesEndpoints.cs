using ImageForge.Api.Services;
using ImageForge.Shared.Contracts;
using ImageForge.Shared.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

// Disambiguate from System.Threading.Tasks.TaskStatus.
using TaskStatus = ImageForge.Shared.Contracts.TaskStatus;

namespace ImageForge.Api.Endpoints;

public static class ImagesEndpoints
{
    // Target formats we accept from clients. Worker uses the same set.
    private static readonly HashSet<string> AllowedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "webp", "jpg", "jpeg", "png"
    };

    // MIME types that browsers / clients send for image uploads we support.
    // "image/jpg" is non-standard but seen in the wild; accept it for kindness.
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    private const string DefaultFormat = "webp";
    private const int DefaultMaxDimension = 1920;
    private const long MaxUploadBytes = 20L * 1024 * 1024; // 20 MB

    public static IEndpointRouteBuilder MapImagesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/images").WithTags("Images");

        group.MapPost("/", UploadAsync)
             .DisableAntiforgery()
             .WithName("UploadImage")
             // Swashbuckle 6.x cannot describe [FromForm] IFormFile in minimal
             // APIs, so we hide the endpoint from Swagger UI while keeping it
             // fully functional. The README documents the multipart shape.
             .ExcludeFromDescription();

        group.MapGet("/{taskId}", GetStatusAsync)
             .WithName("GetImageStatus")
             .WithSummary("Get the current status snapshot for a task")
             .Produces<TaskStatus>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{taskId}/result", GetResultAsync)
             .WithName("GetImageResult")
             .WithSummary("Download the processed file once the task is done")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status400BadRequest)
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{taskId}/source", GetSourceAsync)
             .WithName("GetImageSource")
             .WithSummary("Download the originally uploaded file (for before/after view)")
             .Produces(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// POST /api/images. Validates and stores the upload, seeds a "pending"
    /// status in Redis, publishes a TaskMessage to RabbitMQ.
    /// </summary>
    /// <remarks>
    /// Form fields:
    ///   file          (required)  the image to process; jpeg/png/webp; max 20 MB
    ///   format        (optional)  webp | jpg | jpeg | png        (default: webp)
    ///   maxDimension  (optional)  pixels; 0 = no resize          (default: 1920)
    /// </remarks>
    private static async Task<Results<Ok<UploadResponse>, BadRequest<string>>> UploadAsync(
        [FromForm] IFormFile file,
        [FromForm] string? format,
        [FromForm] int? maxDimension,
        ImageStorage storage,
        QueuePublisher publisher,
        TaskStatusStore statusStore,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.BadRequest("File is empty.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return TypedResults.BadRequest(
                $"File too large ({file.Length:N0} bytes). Max allowed: {MaxUploadBytes:N0} bytes.");
        }

        if (!AllowedContentTypes.Contains(file.ContentType ?? string.Empty))
        {
            return TypedResults.BadRequest(
                $"Unsupported content type '{file.ContentType}'. Allowed: {string.Join(", ", AllowedContentTypes)}.");
        }

        // Normalize the format: trim, lowercase, default to webp.
        var targetFormat = string.IsNullOrWhiteSpace(format)
            ? DefaultFormat
            : format.Trim().ToLowerInvariant();

        if (!AllowedFormats.Contains(targetFormat))
        {
            return TypedResults.BadRequest(
                $"Unsupported format '{format}'. Allowed: {string.Join(", ", AllowedFormats)}.");
        }

        // Normalize maxDimension: 0 means "do not resize". Negative is invalid.
        int? effectiveMaxDim;
        if (maxDimension is null)
        {
            effectiveMaxDim = DefaultMaxDimension;
        }
        else if (maxDimension.Value < 0)
        {
            return TypedResults.BadRequest("maxDimension must be >= 0.");
        }
        else if (maxDimension.Value == 0)
        {
            effectiveMaxDim = null; // no resize
        }
        else
        {
            effectiveMaxDim = maxDimension.Value;
        }

        var taskId = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(file.FileName);
        var sourcePath = storage.BuildUploadPath(taskId, extension);

        await using (var output = File.Create(sourcePath))
        {
            await file.CopyToAsync(output, ct);
        }

        // Seed the status in Redis BEFORE publishing so a GET right after the
        // POST sees "pending" instead of 404 (no race window). Broadcast it
        // too so a client that has already subscribed to the SignalR group
        // gets the initial "pending" snapshot.
        await statusStore.SetAndBroadcastAsync(new TaskStatus(
            TaskId: taskId,
            State: TaskState.Pending,
            Progress: 0,
            ResultPath: null,
            Error: null));

        publisher.Publish(new TaskMessage(
            TaskId: taskId,
            SourcePath: sourcePath,
            TargetFormat: targetFormat,
            MaxDimension: effectiveMaxDim));

        return TypedResults.Ok(new UploadResponse(taskId));
    }

    private static async Task<Results<Ok<TaskStatus>, NotFound>> GetStatusAsync(
        string taskId,
        TaskStatusStore statusStore)
    {
        var status = await statusStore.GetAsync(taskId);
        return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
    }

    // GET /api/images/{taskId}/result
    // Streams the processed file once it's ready. 404 while not done yet.
    private static async Task<Results<FileStreamHttpResult, NotFound, BadRequest<string>>> GetResultAsync(
        string taskId,
        TaskStatusStore statusStore)
    {
        var status = await statusStore.GetAsync(taskId);
        if (status is null)
        {
            return TypedResults.NotFound();
        }
        if (status.State != TaskState.Done || status.ResultPath is null)
        {
            return TypedResults.BadRequest($"Task is not done yet (state: {status.State}).");
        }
        if (!File.Exists(status.ResultPath))
        {
            return TypedResults.NotFound();
        }

        var stream = File.OpenRead(status.ResultPath);
        var contentType = GuessContentType(status.ResultPath);
        var fileName = Path.GetFileName(status.ResultPath);
        return TypedResults.Stream(stream, contentType, fileName);
    }

    // GET /api/images/{taskId}/source
    // Streams the originally uploaded file. Used by the before/after slider.
    private static async Task<Results<FileStreamHttpResult, NotFound>> GetSourceAsync(
        string taskId,
        ImageStorage storage,
        TaskStatusStore statusStore)
    {
        // We don't store the source path explicitly; find the upload by
        // matching the taskId prefix. The uploads folder is small per task.
        var status = await statusStore.GetAsync(taskId);
        if (status is null)
        {
            return TypedResults.NotFound();
        }

        var match = Directory
            .EnumerateFiles(storage.UploadsDirectory, taskId + ".*")
            .FirstOrDefault();
        if (match is null)
        {
            return TypedResults.NotFound();
        }

        var stream = File.OpenRead(match);
        var contentType = GuessContentType(match);
        return TypedResults.Stream(stream, contentType, Path.GetFileName(match));
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };
}

public sealed record UploadResponse(string TaskId);
