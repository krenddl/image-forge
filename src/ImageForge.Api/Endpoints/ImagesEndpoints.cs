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
    public static IEndpointRouteBuilder MapImagesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/images").WithTags("Images");

        group.MapPost("/", UploadAsync)
             .DisableAntiforgery() // multipart upload without an HTML form / token
             .WithName("UploadImage");

        group.MapGet("/{taskId}", GetStatusAsync)
             .WithName("GetImageStatus");

        return routes;
    }

    // POST /api/images
    // multipart/form-data with field "file"; returns { taskId }.
    private static async Task<Results<Ok<UploadResponse>, BadRequest<string>>> UploadAsync(
        [FromForm] IFormFile file,
        ImageStorage storage,
        QueuePublisher publisher,
        TaskStatusStore statusStore,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.BadRequest("File is empty.");
        }

        // Compact, URL-safe id; uniqueness is enough for both Redis key and filename.
        var taskId = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(file.FileName);
        var sourcePath = storage.BuildUploadPath(taskId, extension);

        await using (var output = File.Create(sourcePath))
        {
            await file.CopyToAsync(output, ct);
        }

        // Seed the status in Redis BEFORE publishing so a GET right after the
        // POST sees "pending" instead of 404 (no race window).
        await statusStore.SetAsync(new TaskStatus(
            TaskId: taskId,
            State: TaskState.Pending,
            Progress: 0,
            ResultPath: null,
            Error: null));

        // Publish the work item; the worker will pick it up asynchronously.
        publisher.Publish(new TaskMessage(
            TaskId: taskId,
            SourcePath: sourcePath,
            TargetFormat: "webp",
            MaxDimension: 1920));

        return TypedResults.Ok(new UploadResponse(taskId));
    }

    // GET /api/images/{taskId}
    // Reads the current snapshot from Redis. 404 if the task id is unknown
    // (never uploaded, or status expired).
    private static async Task<Results<Ok<TaskStatus>, NotFound>> GetStatusAsync(
        string taskId,
        TaskStatusStore statusStore)
    {
        var status = await statusStore.GetAsync(taskId);
        return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
    }
}

public sealed record UploadResponse(string TaskId);
