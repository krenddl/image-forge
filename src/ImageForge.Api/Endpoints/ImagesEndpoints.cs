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

    private const string DefaultFormat = "webp";
    private const int DefaultMaxDimension = 1920;

    public static IEndpointRouteBuilder MapImagesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/images").WithTags("Images");

        group.MapPost("/", UploadAsync)
             .DisableAntiforgery()
             .WithName("UploadImage");

        group.MapGet("/{taskId}", GetStatusAsync)
             .WithName("GetImageStatus");

        return routes;
    }

    // POST /api/images
    // multipart/form-data:
    //   file          (required)  the image to process
    //   format        (optional)  "webp" | "jpg" | "jpeg" | "png"   default: webp
    //   maxDimension  (optional)  integer; 0 means no resize        default: 1920
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
}

public sealed record UploadResponse(string TaskId);
