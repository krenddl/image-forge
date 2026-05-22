using Microsoft.AspNetCore.SignalR;

namespace ImageForge.Api.Hubs;

// SignalR hub the browser connects to. The client calls SubscribeToTask
// after it knows its taskId; from that point on the server will push
// "statusUpdate" messages with the latest TaskStatus until the client
// unsubscribes or disconnects.
public sealed class TasksHub : Hub
{
    // Add the calling connection to the per-task group. SignalR groups
    // are how we target broadcasts to "all clients interested in task X"
    // without keeping a separate map ourselves.
    public Task SubscribeToTask(string taskId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, GroupName(taskId));
    }

    public Task UnsubscribeFromTask(string taskId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(taskId));
    }

    // Centralize the naming so the subscriber service uses the exact
    // same group key when broadcasting.
    public static string GroupName(string taskId) => $"task:{taskId}";
}
