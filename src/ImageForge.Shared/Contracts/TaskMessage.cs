namespace ImageForge.Shared.Contracts;

// What the Api publishes into RabbitMQ. The worker reads it back to know
// which file to process, into what format, and how much to resize.
public sealed record TaskMessage(
    string TaskId,
    string SourcePath,
    string TargetFormat,
    int? MaxDimension);
