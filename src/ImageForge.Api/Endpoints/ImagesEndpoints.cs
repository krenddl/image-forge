using ImageForge.Api.Services;
using ImageForge.Shared.Contracts;
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

        group.MapGet("/{taskId}", GetStatus)
             .WithName("GetImageStatus");

        return routes;
    }

    // POST /api/images
    // multipart/form-data with field "file"; returns { taskId }.
    private static async Task<Results<Ok<UploadResponse>, BadRequest<string>>> UploadAsync(
        [FromForm] IFormFile file,
        ImageStorage storage,
        QueuePublisher publisher,
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

        // Publish the work item; the worker will pick it up asynchronously.
        // Target format and max dimension will become user-configurable in M6.
        publisher.Publish(new TaskMessage(
            TaskId: taskId,
            SourcePath: sourcePath,
            TargetFormat: "webp",
            MaxDimension: 1920));

        // Status persistence in Redis lands in M5.
        return TypedResults.Ok(new UploadResponse(taskId));
    }

    // GET /api/images/{taskId}
    // M2 stub: always returns "pending". Real status comes from Redis in M5.
    private static Ok<TaskStatus> GetStatus(string taskId)
    {
        var status = new TaskStatus(
            TaskId: taskId,
            State: TaskState.Pending,
            Progress: 0,
            ResultPath: null,
            Error: null);

        return TypedResults.Ok(status);
    }
}

public sealed record UploadResponse(string TaskId);
