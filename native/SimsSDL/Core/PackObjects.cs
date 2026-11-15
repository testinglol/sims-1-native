using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record PackObjectDefinition(
    [property: JsonPropertyName("guid")] uint Guid,
    [property: JsonPropertyName("chunkID")] ushort ChunkID,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("owner")] string Owner);

public sealed partial class PackStack
{
    private static readonly Dictionary<uint, int> ObjdVersionMinimumSizes = new()
    {
        [136] = 160,
        [138] = 190,
        [139] = 192,
        [140] = 194,
        [141] = 194,
        [142] = 210
    };

    internal static string? ObjectResourceName(string path)
    {
        var parts = path.Split("::", StringSplitOptions.None);
        if (parts.Length != 2 && !path.StartsWith("downloads/", StringComparison.Ordinal))
        {
            return null;
        }

        var lastPart = parts[^1];
        var slashIdx = lastPart.LastIndexOf('/');
        var name = slashIdx >= 0 ? lastPart[(slashIdx + 1)..] : lastPart;
        if (name.Length == 0 ||
            !name.EndsWith(".iff", StringComparison.Ordinal) ||
            name.Contains("globals", StringComparison.Ordinal))
        {
            return null;
        }

        return name;
    }

    internal static Dictionary<string, string> BuildObjectResourceIndex(IReadOnlyDictionary<string, string> assets)
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);

        void Insert(string path)
        {
            if (ObjectResourceName(path) is not { } name)
            {
                return;
            }

            if (resources.TryGetValue(name, out var previous))
            {
                throw new ContentError(
                    $"Object resource name {name} conflicts: {previous} and {path}. Use a unique filename or an explicit override at the same resource path");
            }

            resources[name] = path;
        }

        foreach (var (path, source) in assets.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (path.EndsWith(".far", StringComparison.Ordinal))
            {
                var archive = new FARArchive(source);
                foreach (var entry in archive.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                {
                    Insert(path + "::" + entry);
                }
            }
            else
            {
                Insert(path);
            }
        }

        return resources;
    }

    internal static List<PackObjectDefinition> CollectObjectDefinitions(
        IReadOnlyDictionary<string, string> assets,
        IReadOnlyDictionary<string, string> owners)
    {
        var definitions = new List<PackObjectDefinition>();
        var identities = new Dictionary<uint, PackObjectDefinition>();

        void Collect(byte[] data, string path, string owner)
        {
            foreach (var definition in ReadObjects(data, path, owner))
            {
                if (ObjectResourceName(path) is null)
                {
                    throw new ContentError(
                        $"Object definition {path} is outside the engine's object lookup. Put loose object IFF files under Downloads or inside a FAR archive, and avoid filenames containing globals");
                }

                if (identities.TryGetValue(definition.Guid, out var previous))
                {
                    var guidHex = PackContentPaths.HexGUID(definition.Guid);
                    throw new ContentError(
                        $"Object GUID {guidHex} conflicts: {previous.Owner}:{previous.Path}#{previous.ChunkID} and {owner}:{path}#{definition.ChunkID}. Give new objects unique GUIDs; replace existing objects at the same resource path with an explicit override");
                }

                identities[definition.Guid] = definition;
                definitions.Add(definition);
            }
        }

        foreach (var (path, source) in assets.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            var owner = owners[path];
            if (path.EndsWith(".iff", StringComparison.Ordinal))
            {
                Collect(File.ReadAllBytes(source), path, owner);
            }
            else if (path.EndsWith(".far", StringComparison.Ordinal))
            {
                var archive = new FARArchive(source);
                foreach (var entry in archive.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                {
                    if (entry.EndsWith(".iff", StringComparison.Ordinal))
                    {
                        Collect(archive.Read(entry), path + "::" + entry, owner);
                    }
                }
            }
        }

        definitions.Sort(static (a, b) => a.Guid.CompareTo(b.Guid));
        return definitions;
    }

    internal HashSet<uint> ValidateBaseObjects(string baseDir, bool collectAll = false)
    {
        if (!collectAll && ObjectResources.Count == 0 && Reservations.Count == 0)
        {
            return [];
        }

        var canonicalBase = PackContentPaths.ResolveSymlinksInPath(baseDir);
        var basePrefix = PackContentPaths.TrimTrailingSeparator(canonicalBase);
        var slashPrefix = basePrefix.EndsWith('/') ? basePrefix : basePrefix + "/";

        var custom = new Dictionary<uint, PackObjectDefinition>(Objects.Count);
        foreach (var obj in Objects)
        {
            custom.Add(obj.Guid, obj);
        }

        var reserved = new Dictionary<uint, ObjectGUIDReservation>(Reservations.Count);
        foreach (var reservation in Reservations)
        {
            reserved.Add(reservation.Guid, reservation);
        }

        var identities = new HashSet<uint>();

        void Check(byte[] data, string path, bool validate)
        {
            foreach (var original in ReadObjects(data, path, "base", allowZero: true))
            {
                identities.Add(original.Guid);
                if (!validate)
                {
                    continue;
                }

                if (custom.TryGetValue(original.Guid, out var replacement))
                {
                    throw new ContentError(
                        $"Object GUID {PackContentPaths.HexGUID(original.Guid)} conflicts: base:{original.Path}#{original.ChunkID} and {replacement.Owner}:{replacement.Path}#{replacement.ChunkID}. Declare an override at the original resource path or assign a unique GUID");
                }

                if (reserved.TryGetValue(original.Guid, out var reservation))
                {
                    throw new ContentError(
                        $"Reserved object GUID {PackContentPaths.HexGUID(original.Guid)} conflicts: base:{original.Path} and {reservation.Owner}:{reservation.Name}");
                }
            }

            if (validate && ObjectResourceName(path) is { } name && ObjectResources.TryGetValue(name, out var replacedPath))
            {
                throw new ContentError(
                    $"Object resource name {name} conflicts: base:{path} and {replacedPath}. Use a unique filename or declare an override at the original resource path");
            }
        }

        if (!Directory.Exists(canonicalBase))
        {
            throw new ContentError("Cannot enumerate base object resources");
        }

        foreach (var file in PackContentPaths.EnumerateFilesRecursively(canonicalBase))
        {
            var resolved = PackContentPaths.ResolveSymlinksInPath(file);
            if (!PackContentPaths.IsUnderCanonicalRoot(resolved, canonicalBase))
            {
                throw new ContentError("Base object resource escapes game directory");
            }

            var path = PackContentPaths.ResourceKey(PackContentPaths.NormalizeSeparators(resolved)[slashPrefix.Length..]);
            if (!collectAll && Assets.ContainsKey(path))
            {
                continue;
            }

            var hasOverride = Assets.ContainsKey(path);
            if (path.EndsWith(".far", StringComparison.Ordinal))
            {
                var archive = new FARArchive(resolved);
                foreach (var entry in archive.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
                {
                    if (!entry.EndsWith(".iff", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var lastSlash = entry.LastIndexOf('/');
                    var entryName = lastSlash >= 0 ? entry[(lastSlash + 1)..] : entry;
                    if (!entryName.Contains("globals", StringComparison.Ordinal))
                    {
                        Check(archive.Read(entry), path + "::" + entry, !hasOverride);
                    }
                }
            }
            else if (path.StartsWith("downloads/", StringComparison.Ordinal) &&
                     path.EndsWith(".iff", StringComparison.Ordinal) &&
                     !Path.GetFileName(file).Contains("globals", StringComparison.OrdinalIgnoreCase))
            {
                Check(File.ReadAllBytes(resolved), path, !hasOverride);
            }
        }

        return identities;
    }

    private static List<PackObjectDefinition> ReadObjects(byte[] data, string path, string owner, bool allowZero = false)
    {
        if (data.Length < 64)
        {
            throw new ContentError($"Invalid pack IFF header: {path}");
        }

        var headerBuilder = new StringBuilder(60);
        for (var i = 0; i < 60; i++)
        {
            var b = data[i];
            if (b >= 0x80)
            {
                throw new ContentError($"Invalid pack IFF header: {path}");
            }
            if (b != 0)
            {
                headerBuilder.Append((char)b);
            }
        }

        var header = headerBuilder.ToString();
        if (header != "IFF FILE 2.5:TYPE FOLLOWED BY SIZE JAMIE DOORNBOS & MAXIS 1" &&
            header != "IFF FILE 2.0:TYPE FOLLOWED BY SIZE JAMIE DOORNBOS & MAXIS 1")
        {
            throw new ContentError($"Invalid pack IFF header: {path}");
        }

        var offset = 64;
        var result = new List<PackObjectDefinition>();
        while (offset < data.Length)
        {
            if (data.Length - offset < 76)
            {
                throw new ContentError($"Truncated pack IFF chunk: {path}");
            }

            var sizeU32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            if (sizeU32 < 76 || sizeU32 > (uint)(data.Length - offset))
            {
                throw new ContentError($"Invalid pack IFF chunk size: {path}");
            }

            var size = (int)sizeU32;
            if (data[offset] == (byte)'O' && data[offset + 1] == (byte)'B' &&
                data[offset + 2] == (byte)'J' && data[offset + 3] == (byte)'D')
            {
                if (size < 80)
                {
                    throw new ContentError($"Truncated object definition: {path}");
                }

                var payload = offset + 76;
                var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload, 4));
                if (!ObjdVersionMinimumSizes.TryGetValue(version, out var minimum) || size - 76 < minimum)
                {
                    throw new ContentError($"Unsupported or truncated OBJD version {version}: {path}");
                }

                var guid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 28, 4));
                if (guid == 0 && allowZero)
                {
                    offset += size;
                    continue;
                }

                if (guid == 0)
                {
                    throw new ContentError($"Pack object GUID cannot be zero: {path}");
                }

                var chunkID = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8, 2));
                result.Add(new PackObjectDefinition(guid, chunkID, path, owner));
            }

            offset += size;
        }

        return result;
    }
}
