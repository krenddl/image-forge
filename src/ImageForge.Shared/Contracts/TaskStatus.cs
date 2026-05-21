namespace ImageForge.Shared.Contracts;

// Status snapshot for a single processing task. Written to Redis by the worker
// and read by the API. Kept simple on purpose: this is the wire shape.
public sealed record TaskStatus(
    string TaskId,
    string State,
    int Progress,
    string? ResultPath,
    string? Error);
