namespace SimsSDL.Client;

/// <summary>Imports an original Sims save directory into a separate native profile without modifying the source.</summary>
public static class OriginalSaveImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Runs <see cref="ImportSynchronously"/> on a background thread-pool task.</summary>
    public static Task<string> RunAsync(
        string gameRoot,
        string workspace,
        string sourceDirectory,
        string profileId,
        string? dataRoot = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportSynchronously(gameRoot, workspace, sourceDirectory, profileId, dataRoot), cancellationToken);

    /// <summary>Runs <see cref="ImportSynchronously"/> on a background thread-pool task.</summary>
    public static Task<string> Run(
        string gameRoot,
        string workspace,
        string sourceDirectory,
        string profileId,
        string? dataRoot = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(gameRoot, workspace, sourceDirectory, profileId, dataRoot, cancellationToken);

    /// <summary>Imports an original save directory into <paramref name="dataRoot"/> (or <paramref name="workspace"/> when omitted) and returns the camelCase JSON summary.</summary>
    public static string ImportSynchronously(
        string gameRoot,
        string workspace,
        string sourceDirectory,
        string profileId,
        string? dataRoot = null)
    {
        var storageRoot = dataRoot ?? workspace;
        try
        {
            // SimsSimulation is linked in-process via InternalsVisibleTo, avoiding a child dotnet process spawn.
            var imported = global::ProfileBootstrap.ImportOriginalUserData(
                gameRoot,
                Path.GetFullPath(storageRoot),
                sourceDirectory,
                profileId);
            var result = JsonSerializer.Serialize(imported, JsonOptions).Trim();
            if (string.IsNullOrEmpty(result))
                throw new ContentError("Original save import returned no result");
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            throw new ContentError(string.IsNullOrWhiteSpace(error.Message)
                ? "Original save import failed"
                : error.Message.Trim());
        }
    }
}
