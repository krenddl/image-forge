namespace ImageForge.Shared.Contracts;

// State is stored in Redis as a plain string for cross-service compatibility,
// so we keep it as a set of string constants rather than an enum.
public static class TaskState
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
}
