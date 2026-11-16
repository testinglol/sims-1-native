namespace SimsSDL.Client;

/// <summary>Imports a legacy save directory into a separate profile without modifying the source files.</summary>
public static class OriginalSaveImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task<string> RunAsync(
        string gameRoot,
        string workspace,
        string sourceDirectory,
        string profileId,
        string? dataRoot = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportSynchronously(gameRoot, workspace, sourceDirectory, profileId, dataRoot), cancellationToken);

    public static Task<string> Run(
        string gameRoot,
        string workspace,
        string sourceDirectory,
        string profileId,
        string? dataRoot = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(gameRoot, workspace, sourceDirectory, profileId, dataRoot, cancellationToken);

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
