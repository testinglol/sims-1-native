namespace SimsSDL.Client;

/// <summary>Provides content pack operations including loading packs, validating manifests and GUIDs, and
/// managing plugin trust without mounting content.</summary>
public static class PackTool
{
    private static readonly JsonSerializerOptions IndentedCamelOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public abstract record Command
    {
        private Command() { }

        public sealed record Report : Command;
        public sealed record ListPlugins : Command;
        public sealed record TrustPlugin(string PackId) : Command;
        public sealed record RevokePlugin(string PackId) : Command;
        public sealed record AllocateObjectGuid(string PackId, string Name) : Command;
        public sealed record ReserveObjectGuid(string PackId, string Name) : Command;

        /// <summary>Validates each pack individually and collectively with the base game, reporting all
        /// discovered issues.</summary>
        public sealed record Validate : Command;

        public sealed record Mount(string Path) : Command;
    }

    /// <summary>Loads game content alongside custom packs discovered from subdirectories containing
    /// <c>pack.json</c>.</summary>
    public static GameContent LoadContent(string game, string? packs)
    {
        ArgumentException.ThrowIfNullOrEmpty(game);
        var directories = new List<string>();
        if (!string.IsNullOrEmpty(packs) && Directory.Exists(packs))
        {
            foreach (var entry in Directory.EnumerateDirectories(packs))
            {
                if (File.Exists(Path.Combine(entry, "pack.json")))
                {
                    directories.Add(entry);
                }
            }
        }

        return new GameContent(game, directories);
    }

    /// <summary>Validates packs individually, against each other for dependency and GUID conflicts, and
    /// against the base game for resource overrides.</summary>
    public static (byte[] Report, bool Valid) Validate(string game, string? packs)
    {
        ArgumentException.ThrowIfNullOrEmpty(game);
        var directories = new List<string>();
        if (!string.IsNullOrEmpty(packs) && Directory.Exists(packs))
        {
            try
            {
                directories = Directory.EnumerateDirectories(packs)
                    .Where(dir => File.Exists(Path.Combine(dir, "pack.json")))
                    .OrderBy(dir => Path.GetFileName(dir), StringComparer.Ordinal)
                    .ToList();
            }
            catch
            {
                directories = [];
            }
        }

        var problems = new List<SortedDictionary<string, string>>();
        var readable = new List<(string Directory, ContentPack Pack)>();
        var listing = new List<SortedDictionary<string, object?>>();

        static string ErrorMessage(Exception error) => error switch
        {
            ContentError contentError => contentError.Message,
            JsonException jsonException => jsonException.Message,
            _ => error.Message,
        };

        void AddProblem(string pack, string stage, string problem) =>
            problems.Add(new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["pack"] = pack,
                ["problem"] = problem,
                ["stage"] = stage,
            });

        foreach (var directory in directories)
        {
            var dirName = Path.GetFileName(directory);
            try
            {
                var pack = new ContentPack(directory);
                readable.Add((directory, pack));
                listing.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["directory"] = dirName,
                    ["files"] = pack.Assets.Count,
                    ["id"] = pack.Manifest.Id,
                    ["panels"] = pack.PanelFiles.Count,
                    ["plugin"] = pack.PluginAssembly is not null,
                    ["version"] = pack.Manifest.Version,
                });
            }
            catch (Exception error)
            {
                AddProblem(dirName, "pack", ErrorMessage(error));
                listing.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["directory"] = dirName,
                    ["unreadable"] = true,
                });
            }
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in readable)
        {
            var id = entry.Pack.Manifest.Id;
            var dirName = Path.GetFileName(entry.Directory);
            if (seen.TryGetValue(id, out var first))
            {
                AddProblem(id, "together", $"Duplicate pack ID in {first} and {dirName}");
            }
            else
            {
                seen[id] = dirName;
            }
        }

        foreach (var entry in readable)
        {
            foreach (var dependency in entry.Pack.Manifest.Dependencies)
            {
                var match = readable
                    .Select(candidate => candidate.Pack)
                    .FirstOrDefault(pack => string.Equals(pack.Manifest.Id, dependency.Id, StringComparison.Ordinal));
                if (match is null)
                {
                    AddProblem(entry.Pack.Manifest.Id, "together", $"Requires {dependency.Id}, which is not in the pack folder");
                }
                else if (!string.Equals(dependency.Version, "*", StringComparison.Ordinal) &&
                         !string.Equals(match.Manifest.Version, dependency.Version, StringComparison.Ordinal))
                {
                    AddProblem(
                        entry.Pack.Manifest.Id,
                        "together",
                        $"Requires {dependency.Id} version {dependency.Version}, found {match.Manifest.Version}");
                }
            }
        }

        var stackProblem = problems.Any(problem => string.Equals(problem["stage"], "together", StringComparison.Ordinal));
        foreach (var entry in readable)
        {
            try
            {
                _ = new PackStack([entry.Pack]);
            }
            catch (Exception error)
            {
                var msg = ErrorMessage(error);
                if (msg.Contains("requires", StringComparison.Ordinal))
                {
                    continue;
                }

                AddProblem(entry.Pack.Manifest.Id, "alone", msg);
                stackProblem = true;
            }
        }

        if (!stackProblem && readable.Count > 0)
        {
            try
            {
                _ = new PackStack(readable.Select(entry => entry.Pack).ToList());
            }
            catch (Exception error)
            {
                AddProblem("(all)", "together", ErrorMessage(error));
                stackProblem = true;
            }
        }

        if (!stackProblem && readable.Count > 0)
        {
            try
            {
                _ = new GameContent(game, readable.Select(entry => entry.Directory).ToList());
            }
            catch (Exception error)
            {
                AddProblem("(all)", "base game", ErrorMessage(error));
                foreach (var entry in readable)
                {
                    PackStack? alone = null;
                    try
                    {
                        alone = new PackStack([entry.Pack]);
                    }
                    catch
                    {
                        alone = null;
                    }

                    if (alone is null || alone.Ordered.Count != 1)
                    {
                        continue;
                    }

                    try
                    {
                        _ = new GameContent(game, [entry.Directory]);
                    }
                    catch (Exception aloneError)
                    {
                        AddProblem(entry.Pack.Manifest.Id, "base game alone", ErrorMessage(aloneError));
                    }
                }
            }
        }

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["packs"] = listing,
            ["problems"] = problems,
            ["valid"] = problems.Count == 0,
        };
        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(result, IndentedCamelOptions);
        return (reportBytes, problems.Count == 0);
    }

    public static byte[] Run(Command command, GameContent content, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        switch (command)
        {
            case Command.Validate:
                throw new ContentError("validate runs before content loads; use PackTool.Validate");

            case Command.Report:
                return JsonSerializer.SerializeToUtf8Bytes(content.Packs.Report(), IndentedCamelOptions);

            case Command.Mount(var path):
            {
                var destination = Path.GetFullPath(path);
                content.Materialize(destination);
                var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["fingerprint"] = content.Packs.Report().Fingerprint,
                    ["mount"] = destination,
                };
                return JsonSerializer.SerializeToUtf8Bytes(payload, IndentedCamelOptions);
            }

            case Command.AllocateObjectGuid(var packId, var name):
                return JsonSerializer.SerializeToUtf8Bytes(content.AllocateObjectGUID(packId, name), IndentedCamelOptions);

            case Command.ReserveObjectGuid(var packId, var name):
                return JsonSerializer.SerializeToUtf8Bytes(content.ReserveObjectGUID(packId, name), IndentedCamelOptions);

            case Command.ListPlugins or Command.TrustPlugin or Command.RevokePlugin:
            {
                var store = new PluginTrustStore(dataRoot);
                var plugins = content.Packs.PluginReports();
                if (command is Command.TrustPlugin trustCommand)
                {
                    var plugin = plugins.FirstOrDefault(p =>
                        string.Equals(p.PackID, trustCommand.PackId, StringComparison.Ordinal));
                    if (plugin is null)
                    {
                        throw new ContentError($"No enabled pack named {trustCommand.PackId} declares a plugin");
                    }

                    store.Trust(plugin);
                }
                else if (command is Command.RevokePlugin revokeCommand)
                {
                    if (!store.Revoke(revokeCommand.PackId))
                    {
                        throw new ContentError($"Plugin {revokeCommand.PackId} was not trusted");
                    }
                }

                var listing = plugins.Select(plugin => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["apiVersion"] = plugin.ApiVersion,
                    ["entryType"] = plugin.EntryType,
                    ["packID"] = plugin.PackID,
                    ["sha256"] = plugin.Sha256,
                    ["stateVersion"] = plugin.StateVersion,
                    ["trusted"] = store.IsTrusted(plugin),
                    ["version"] = plugin.Version,
                }).ToList();

                var result = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["plugins"] = listing,
                    ["trustStore"] = store.Url,
                };
                return JsonSerializer.SerializeToUtf8Bytes(result, IndentedCamelOptions);
            }

            default:
                throw new ContentError($"Unsupported pack command: {command}");
        }
    }

    public static IReadOnlyList<(PackPluginReport Plugin, string Name)> UntrustedPlugins(GameContent content, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        try
        {
            var plugins = content.Packs.PluginReports();
            if (plugins.Count == 0)
            {
                return [];
            }

            var store = new PluginTrustStore(dataRoot);
            return plugins
                .Where(plugin => !store.IsTrusted(plugin))
                .Select(plugin =>
                {
                    var packName = content.Packs.Ordered
                        .FirstOrDefault(pack => string.Equals(pack.Manifest.Id, plugin.PackID, StringComparison.Ordinal))
                        ?.Manifest.Name ?? plugin.PackID;
                    return (plugin, packName);
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void Trust(PackPluginReport plugin, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);

        var store = new PluginTrustStore(dataRoot);
        store.Trust(plugin);
    }
}
