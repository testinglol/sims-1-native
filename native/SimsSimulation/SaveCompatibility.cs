using System.Globalization;

/// <summary>
/// Decides whether a native save can load under the current pack stack, and keeps per-slot backups.
/// An identical pack fingerprint always loads. Otherwise every saved pack must still be enabled; a pack whose version
/// changed must list the saved version in its manifest's <c>saveMigration.from</c>; newly enabled packs are allowed.
/// Changed content without any version change is rejected, as before. A saved pack that is no longer enabled rejects the
/// save unless the caller opts into removing it; the caller then strips that pack's objects (see <see cref="NativePackRemoval"/>).
/// </summary>
internal static class SaveCompatibility
{
    internal sealed record PackRecord(string Id, string Version);

    internal sealed record Result(bool Migrated, string[] Upgraded, string[] Added, string[] Removed)
    {
        /// <summary>Old object GUIDs of upgraded packs and the objects their saved instances load as.</summary>
        public Dictionary<uint, uint> Objects { get; init; } = [];
    }

    /// <summary>The save needs packs that are not enabled. Loading again with <c>removePacks</c> removes their content.</summary>
    internal sealed class MissingPacksException(string[] packs) : Exception(
        $"This save requires {string.Join(", ", packs)}, which {(packs.Length == 1 ? "is" : "are")} not enabled; load it with removePacks to remove that content")
    {
        public string[] Packs { get; } = packs;
    }

    public const int MaxBackupsPerSlot = 5;

    public static Result Check(string savedFingerprint, PackRecord[]? savedPacks, string currentFingerprint, IReadOnlyList<PluginHost.PackDescriptor> currentPacks,
        bool removeMissing = false)
    {
        if (savedFingerprint == currentFingerprint) return new Result(false, [], [], []);
        if (savedPacks == null) throw new InvalidDataException("Save requires the same engine and content pack stack");
        var current = currentPacks.ToDictionary(pack => pack.Id, StringComparer.Ordinal);
        var upgraded = new List<string>();
        var removed = new List<string>();
        var objects = new Dictionary<uint, uint>();
        foreach (var saved in savedPacks)
        {
            if (!current.TryGetValue(saved.Id, out var pack))
            {
                removed.Add(saved.Id + "@" + saved.Version);
                continue;
            }
            if (pack.Version == saved.Version) continue;
            if (!pack.MigratesFrom.Contains(saved.Version, StringComparer.Ordinal))
                throw new InvalidDataException($"Pack {saved.Id} {pack.Version} does not declare a save migration from version {saved.Version}");
            upgraded.Add($"{saved.Id}@{saved.Version}->{pack.Version}");
            foreach (var migration in pack.Objects ?? []) objects.TryAdd(migration.From, migration.To);
        }
        if (removed.Count > 0 && !removeMissing) throw new MissingPacksException(removed.ToArray());
        var savedIds = savedPacks.Select(pack => pack.Id).ToHashSet(StringComparer.Ordinal);
        var added = currentPacks.Where(pack => !savedIds.Contains(pack.Id)).Select(pack => pack.Id + "@" + pack.Version).ToArray();
        if (upgraded.Count == 0 && added.Length == 0 && removed.Count == 0)
            throw new InvalidDataException("Pack contents changed without a version change; this save cannot be loaded safely");
        return new Result(true, upgraded.ToArray(), added, removed.ToArray()) { Objects = objects };
    }

    public static string BackupDirectory(string profile) => Path.Combine(profile, "Saves", "backups");

    /// <summary>Copies an existing slot into the backup directory and prunes the oldest copies beyond the limit.</summary>
    public static string? Backup(string profile, string slot, string slotPath)
    {
        if (!File.Exists(slotPath)) return null;
        var directory = BackupDirectory(profile);
        Directory.CreateDirectory(directory);
        // Tick-resolution stamps keep names sortable; a collision advances the stamp so newer copies always sort later.
        var stamp = DateTime.UtcNow;
        string destination;
        do
        {
            destination = Path.Combine(directory, $"{slot}-{stamp.ToString("yyyyMMdd'T'HHmmssfffffff", CultureInfo.InvariantCulture)}.json");
            stamp = stamp.AddTicks(1);
        } while (File.Exists(destination));
        File.Copy(slotPath, destination);
        foreach (var stale in List(profile, slot).Skip(MaxBackupsPerSlot)) File.Delete(Path.Combine(directory, stale));
        return Path.GetFileName(destination);
    }

    /// <summary>Backup file names for a slot, newest first.</summary>
    public static string[] List(string profile, string slot)
    {
        var directory = BackupDirectory(profile);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, slot + "-*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => IsBackupOf(name, slot))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBackupOf(string name, string slot)
    {
        var stamp = name.AsSpan(slot.Length + 1);
        return name.StartsWith(slot + "-", StringComparison.Ordinal) && stamp.Length >= 22 && char.IsAsciiDigit(stamp[0]) && stamp[8] == 'T';
    }

    /// <summary>Replaces a slot with one of its backups. The current slot is itself backed up first.</summary>
    public static object Restore(string profile, string slot, string slotPath, string backup)
    {
        if (!List(profile, slot).Contains(backup, StringComparer.Ordinal)) throw new ArgumentException("Unknown backup for this save slot");
        // Read first: backing up the current slot may prune the oldest backup, which can be the one being restored.
        var contents = File.ReadAllBytes(Path.Combine(BackupDirectory(profile), backup));
        var previous = Backup(profile, slot, slotPath);
        var temporary = slotPath + ".restore";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, slotPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new { slot, restored = backup, previous };
    }
}
