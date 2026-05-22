using ImageForge.Api.Hubs;
using ImageForge.Shared.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace ImageForge.Api.Services;

// Bridges Redis Pub/Sub to SignalR. Runs for the lifetime of the API
// process: subscribes to the status channel and, for every message
// the worker publishes, forwards the TaskStatus to the matching
// SignalR group so any browser watching that taskId gets a push.
public sealed class TaskStatusBroadcaster : BackgroundService
{
    private readonly TaskStatusStore _statusStore;
    private readonly IHubContext<TasksHub> _hub;
    private readonly ILogger<TaskStatusBroadcaster> _logger;

    public TaskStatusBroadcaster(
        TaskStatusStore statusStore,
        IHubContext<TasksHub> hub,
        ILogger<TaskStatusBroadcaster> logger)
    {
        _statusStore = statusStore;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _statusStore.SubscribeAsync(async status =>
        {
            try
            {
                await _hub.Clients
                    .Group(TasksHub.GroupName(status.TaskId))
                    .SendAsync("statusUpdate", status, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to push status update to SignalR for task {TaskId}",
                    status.TaskId);
            }
        });

        _logger.LogInformation("Subscribed to Redis status channel; pushing to SignalR group per task");

        // Keep the hosted service alive until the host is shutting down;
        // the actual work happens on Redis's subscription callback threads.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown.
        }
    }
}
