using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public sealed record ObjectGUIDReservation(
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("guid")] uint Guid);

public static class ObjectGUIDAllocator
{
    public static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            Encoding.UTF8.GetByteCount(name) > 128 ||
            name != name.ToLowerInvariant() ||
            !name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
        {
            throw new ContentError(
                "Object identity must use 1..128 lowercase ASCII letters, digits, dots, underscores or hyphens");
        }
    }

    public static uint Propose(string packID, string name, IReadOnlySet<uint> used)
    {
        Validate(name);
        if (string.IsNullOrEmpty(packID))
        {
            throw new ContentError("Object allocation requires a pack ID");
        }

        for (var attempt = 0; attempt < 4096; attempt++)
        {
            var payload = $"sims-native-object-v1\0{packID}\0{name}\0{attempt}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            var prefix = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, 4));
            var candidate = 0xE0000000u | (prefix & 0x0FFFFFFFu);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new ContentError("Object GUID allocation exhausted collision retries");
    }

    internal static List<ObjectGUIDReservation> CollectReservations(
        IReadOnlyList<ContentPack> packs,
        IReadOnlyList<PackObjectDefinition> objects)
    {
        var result = new List<ObjectGUIDReservation>();
        var owners = new Dictionary<uint, ObjectGUIDReservation>();
        var definitions = new Dictionary<uint, PackObjectDefinition>(objects.Count);
        foreach (var obj in objects)
        {
            definitions.Add(obj.Guid, obj);
        }

        foreach (var pack in packs)
        {
            var entries = pack.Manifest.ObjectGUIDs;
            if (entries is null || entries.Count == 0)
            {
                continue;
            }

            if (entries.Count > 10000)
            {
                throw new ContentError("Pack object reservation limit exceeded");
            }

            foreach (var (name, guid) in entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                Validate(name);
                if (guid == 0)
                {
                    throw new ContentError("Reserved object GUID cannot be zero");
                }

                var reservation = new ObjectGUIDReservation(pack.Manifest.Id, name, guid);
                if (owners.TryGetValue(guid, out var previous))
                {
                    throw new ContentError(
                        $"Reserved object GUID {PackContentPaths.HexGUID(guid)} conflicts: {previous.Owner}:{previous.Name} and {reservation.Owner}:{name}");
                }

                if (definitions.TryGetValue(guid, out var definition) && definition.Owner != pack.Manifest.Id)
                {
                    var container = definition.Path.Split("::", StringSplitOptions.None)[0];
                    var replacement = packs.FirstOrDefault(p => p.Manifest.Id == definition.Owner);
                    var overrides = new HashSet<string>(
                        (replacement?.Manifest.Overrides ?? []).Select(PackContentPaths.ResourceKey),
                        StringComparer.Ordinal);
                    if (!pack.Assets.ContainsKey(container) || !overrides.Contains(container))
                    {
                        throw new ContentError($"Reserved object GUID belongs to {definition.Owner}:{definition.Path}");
                    }
                }

                owners[guid] = reservation;
                result.Add(reservation);
            }
        }

        return result;
    }
}

public sealed partial class GameContent
{
    public ObjectGUIDReservation ReserveObjectGUID(string packID, string name)
    {
        var reservation = AllocateObjectGUID(packID, name);
        var pack = Packs.Ordered.FirstOrDefault(p => p.Manifest.Id == packID)
            ?? throw new ContentError("Missing reservation pack");

        var manifestPath = Path.Combine(pack.Directory, "pack.json");
        var canonicalRoot = PackContentPaths.ResolveSymlinksInPath(pack.Directory);
        var canonicalTarget = PackContentPaths.ResolveSymlinksInPath(manifestPath);
        if (!PackContentPaths.IsUnderCanonicalRoot(canonicalTarget, canonicalRoot))
        {
            throw new ContentError("Reservation manifest escapes its pack");
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            using var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var refAdded = false;
            var locked = false;
            try
            {
                fs.SafeFileHandle.DangerousAddRef(ref refAdded);
                var fd = unchecked((int)fs.SafeFileHandle.DangerousGetHandle());
                const int LOCK_EX = 2;
                if (FlockNative(fd, LOCK_EX) != 0)
                {
                    throw new ContentError("Cannot lock object reservation manifest");
                }
                locked = true;

                WriteReservationAtomically(manifestPath, pack, name, reservation);
            }
            finally
            {
                if (locked)
                {
                    var fd = unchecked((int)fs.SafeFileHandle.DangerousGetHandle());
                    const int LOCK_UN = 8;
                    _ = FlockNative(fd, LOCK_UN);
                }
                if (refAdded)
                {
                    fs.SafeFileHandle.DangerousRelease();
                }
            }
        }
        else
        {
            WriteReservationAtomically(manifestPath, pack, name, reservation);
        }

        return reservation;
    }

    public ObjectGUIDReservation AllocateObjectGUID(string packID, string name)
    {
        ObjectGUIDAllocator.Validate(name);
        if (!Packs.Ordered.Any(p => p.Manifest.Id == packID))
        {
            throw new ContentError($"Enable the target pack before allocating object GUIDs: {packID}");
        }

        if (Packs.Reservations.FirstOrDefault(r => r.Owner == packID && r.Name == name) is { } existing)
        {
            return existing;
        }

        var used = Packs.ValidateBaseObjects(Root, collectAll: true);
        foreach (var obj in Packs.Objects)
        {
            used.Add(obj.Guid);
        }
        foreach (var reserved in Packs.Reservations)
        {
            used.Add(reserved.Guid);
        }

        return new ObjectGUIDReservation(packID, name, ObjectGUIDAllocator.Propose(packID, name, used));
    }

    private static void WriteReservationAtomically(
        string manifestPath,
        ContentPack pack,
        string name,
        ObjectGUIDReservation reservation)
    {
        var currentBytes = File.ReadAllBytes(manifestPath);
        if (!currentBytes.AsSpan().SequenceEqual(pack.ManifestData))
        {
            throw new ContentError("Pack manifest changed during allocation; reload before reserving an object GUID");
        }

        if (pack.Manifest.ObjectGUIDs is { } currentMap &&
            currentMap.TryGetValue(name, out var existingGuid) &&
            existingGuid == reservation.Guid)
        {
            return;
        }

        JsonObject? document;
        try
        {
            document = JsonNode.Parse(pack.ManifestData) as JsonObject;
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
        {
            throw new ContentError("Invalid pack manifest document");
        }

        var identities = pack.Manifest.ObjectGUIDs is not null
            ? new Dictionary<string, uint>(pack.Manifest.ObjectGUIDs, StringComparer.Ordinal)
            : new Dictionary<string, uint>(StringComparer.Ordinal);
        if (identities.Count >= 10000)
        {
            throw new ContentError("Pack object reservation limit exceeded");
        }

        identities[name] = reservation.Guid;
        var guidObj = new JsonObject();
        foreach (var (key, value) in identities.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            guidObj[key] = value;
        }
        document["objectGUIDs"] = guidObj;

        var serialized = SerializeSortedJson(document);
        var dir = Path.GetDirectoryName(manifestPath) ?? pack.Directory;
        var tmp = Path.Combine(dir, ".pack.json.tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(tmp, serialized);
            File.Move(tmp, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                }
            }
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int FlockNative(int fd, int operation);
}
