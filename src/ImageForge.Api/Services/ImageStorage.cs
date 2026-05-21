namespace ImageForge.Api.Services;

// Thin wrapper around the filesystem so the rest of the API does not have
// to know where files physically live. Resolves the configured root once
// at startup and creates the uploads/results folders if missing.
public sealed class ImageStorage
{
    public string UploadsDirectory { get; }
    public string ResultsDirectory { get; }

    public ImageStorage(IConfiguration configuration, IHostEnvironment env)
    {
        var configured = configuration["Storage:Root"] ?? "storage";

        // Allow either an absolute path (e.g. from an env var in Docker) or
        // a path relative to the application's content root.
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));

        UploadsDirectory = Path.Combine(root, "uploads");
        ResultsDirectory = Path.Combine(root, "results");

        Directory.CreateDirectory(UploadsDirectory);
        Directory.CreateDirectory(ResultsDirectory);
    }

    public string BuildUploadPath(string taskId, string extension)
    {
        // Keep the original extension so the worker knows the source format.
        // Fallback to .bin keeps Path.Combine happy if the client sent no name.
        var safeExt = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
        return Path.Combine(UploadsDirectory, taskId + safeExt);
    }
}
