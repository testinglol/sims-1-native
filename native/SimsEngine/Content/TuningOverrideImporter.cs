using System.Globalization;
using System.Reflection;
using SimsEngine.Formats;

namespace SimsEngine.Content;

/// <summary>
/// A motive advertisement override for a specific motive in an interaction.
/// </summary>
public sealed class MotiveOverride
{
    public short? DeltaAdjustment { get; set; }
    public short? AdKind { get; set; }
    public short? ActiveFlag { get; set; }
}

/// <summary>
/// Overrides for an interaction entry in an object's TTAB.
/// </summary>
public sealed class InteractionOverride
{
    public uint? AttenuationCode { get; set; }
    public float? AttenuationValue { get; set; }
    public Dictionary<int, MotiveOverride> Motives { get; } = [];
}

/// <summary>
/// A group of interaction tuning overrides for an object GUID.
/// </summary>
public sealed class GuidTuningOverrideGroup
{
    public uint Guid { get; }
    public Dictionary<int, InteractionOverride> Interactions { get; } = [];

    public GuidTuningOverrideGroup(uint guid) => Guid = guid;

    public InteractionTable Apply(InteractionTable table)
    {
        if (Interactions.Count == 0) return table;

        var list = new List<Interaction>(table.Interactions.Count);
        for (var i = 0; i < table.Interactions.Count; i++)
        {
            var interaction = table.Interactions[i];
            // Check if there is an override for this interaction by 0-based index or 1-based index or TtaIndex
            if (!Interactions.TryGetValue(i, out var ov) &&
                !Interactions.TryGetValue(i + 1, out ov) &&
                !Interactions.TryGetValue(interaction.TtaIndex, out ov))
            {
                list.Add(interaction);
                continue;
            }

            var attCode = ov.AttenuationCode ?? interaction.AttenuationCode;
            var attVal = ov.AttenuationValue ?? interaction.AttenuationValue;
            var motives = interaction.Motives.ToList();

            foreach (var (motiveIndex, motiveOv) in ov.Motives)
            {
                if (motiveIndex < 0) continue;
                while (motives.Count <= motiveIndex)
                {
                    motives.Add(new MotiveAdvertisement(0, 0, 0));
                }

                var current = motives[motiveIndex];
                var min = motiveOv.ActiveFlag ?? current.Minimum;
                var delta = motiveOv.DeltaAdjustment.HasValue
                    ? unchecked((short)(motiveOv.DeltaAdjustment.Value - current.Minimum))
                    : current.Delta;
                var personality = motiveOv.AdKind ?? current.Personality;

                motives[motiveIndex] = new MotiveAdvertisement(min, delta, personality);
            }

            list.Add(interaction with
            {
                AttenuationCode = attCode,
                AttenuationValue = attVal,
                Motives = motives
            });
        }

        var ctor = typeof(InteractionTable).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return (InteractionTable)ctor.Invoke([table.Version, table.FieldEncoded, list]);
    }
}

/// <summary>
/// Reads the BCON and GUID parts of the Windows startup <c>GameData/Tuning.txt</c> import
/// (<c>FUN_0043cc48</c> / Mac <c>ObjectFolder::ImportConstants</c>).
/// </summary>
public static class TuningOverrideImporter
{
    private const string VersionMarker = "Version ";
    private const string GuidMarker = "GUID:";
    private static readonly Dictionary<uint, GuidTuningOverrideGroup> s_guidOverrides = new();
    private static readonly object s_lock = new();

    public static void ClearGuidOverrides()
    {
        lock (s_lock)
        {
            s_guidOverrides.Clear();
        }
    }

    public static void SetGuidOverride(uint guid, GuidTuningOverrideGroup group)
    {
        lock (s_lock)
        {
            s_guidOverrides[guid] = group;
        }
    }

    public static GuidTuningOverrideGroup? GetGuidOverrides(uint guid)
    {
        lock (s_lock)
        {
            return s_guidOverrides.TryGetValue(guid, out var group) ? group : null;
        }
    }

    public static InteractionTable ApplyGuidOverrides(uint guid, InteractionTable table)
    {
        var group = GetGuidOverrides(guid);
        return group is not null ? group.Apply(table) : table;
    }

    /// <summary>
    /// Maps attenuation string ("none", "low", "medium", "high") to attenuation code (1..4)
    /// per Mac ObjectFolder.s:3200 (GetGameAttFromDBAtt) and Sims.exe FUN_0043cc48.
    /// </summary>
    public static int GetGameAttFromDBAtt(string attenuation)
    {
        if (string.Equals(attenuation, "none", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(attenuation, "low", StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(attenuation, "medium", StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(attenuation, "high", StringComparison.OrdinalIgnoreCase)) return 4;
        return 3; // Default is Medium (ObjectFolder.s:3315)
    }

    /// <summary>
    /// Maps database impact values (18..37) to game advertisement kinds (1..22)
    /// per Mac ObjectFolder.s:3325 (GetGameAdFromDBImpact) and jump table lbl_106005A4.
    /// </summary>
    public static int GetGameAdFromDBImpact(int impact) => impact switch
    {
        18 => 3,
        19 => 11,
        20 => 1,
        21 => 9,
        22 => 7,
        23 => 21,
        24 => 15,
        25 => 14,
        26 => 19,
        27 => 22,
        28 => 16,
        33 => 4,
        34 => 12,
        35 => 2,
        36 => 10,
        37 => 8,
        _ => -1,
    };

    /// <summary>
    /// Maps 1..9 motive index codes from Tuning.txt to internal motive numbers (9, 6, 5, 15, 7, 8, 3, 13, 14)
    /// per Mac ObjectFolder.s:2163 and jump table lbl_1060057C.
    /// </summary>
    public static int MapMotiveCode(int code) => code switch
    {
        0 => 0,
        1 => 9,  // Energy
        2 => 6,  // Comfort
        3 => 5,  // Hunger
        4 => 15, // Hygiene / other
        5 => 7,
        6 => 8,  // Bladder / Fun
        7 => 3,  // Social
        8 => 13, // Room
        9 => 14, // Autonomy
        _ => -1,
    };

    public static IReadOnlyList<string> Apply(string gameRoot, ObjectRegistry registry)
    {
        var issues = new List<string>();
        var gameData = ObjectRegistry.Resolve(gameRoot, "GameData");
        if (gameData is null) return issues;

        var path = Directory.EnumerateFiles(gameData)
            .FirstOrDefault(candidate => string.Equals(Path.GetFileName(candidate), "Tuning.txt",
                StringComparison.OrdinalIgnoreCase));
        if (path is null) return issues;

        string text;
        try
        {
            text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
        }
        catch (IOException error)
        {
            issues.Add($"Could not read {path}: {error.Message}");
            return issues;
        }

        return ApplyText(text, registry, gameData);
    }

    public static IReadOnlyList<string> ApplyText(string text, ObjectRegistry registry, string? gameData = null)
    {
        var issues = new List<string>();
        var rows = ParseRows(text);
        if (rows.Count == 0 || rows.Max(row => row.Length) < 2)
        {
            issues.Add("Tuning.txt must contain rows with at least two tab-separated columns.");
            return issues;
        }

        var versionCell = rows[0][1];
        var markerIndex = versionCell.IndexOf(VersionMarker, StringComparison.Ordinal);
        if (markerIndex < 0 || !TryParseDecimalPrefix(versionCell[(markerIndex + VersionMarker.Length)..], out var version))
        {
            issues.Add("Tuning.txt has no parseable Version header in row 1, column 2.");
            return issues;
        }

        // FUN_0043cc48 compares the parsed value with 2 before clearing any ObjResFile overrides.
        if (version != 2)
        {
            issues.Add($"Tuning.txt version {version} is not supported; Sims.exe accepts version 2.");
            return issues;
        }
        if (rows.Count < 2)
        {
            issues.Add("Tuning.txt has no data rows.");
            return issues;
        }

        // Clear prior constant overrides and GUID overrides
        ClearGuidOverrides();
        var clearedFiles = new HashSet<IffFile>(ReferenceEqualityComparer.Instance);
        foreach (var entry in registry.Entries)
            if (clearedFiles.Add(entry.File)) BehaviorScope.ClearConstantOverridesForFile(entry.File);

        var rowIndex = 1;
        while (rowIndex < rows.Count)
        {
            var row = rows[rowIndex];
            var fileKey = Cell(row, 0);
            var selector = Cell(row, 1);
            if (fileKey.Length == 0)
            {
                rowIndex++;
                continue;
            }

            if (selector.StartsWith(GuidMarker, StringComparison.Ordinal))
            {
                rowIndex = ParseGuidGroup(rows, rowIndex, registry, issues);
                continue;
            }

            var currentFile = gameData is not null ? FindObjectFile(registry, gameData, fileKey) : null;
            if (currentFile is null)
            {
                issues.Add($"Tuning.txt row {rowIndex + 1} names an unknown object file '{fileKey}'.");
                rowIndex = SkipGroup(rows, rowIndex + 1);
                continue;
            }

            if (!TryParseDecimalPrefix(selector, out var constantIdValue) ||
                constantIdValue <= 0 || constantIdValue > ushort.MaxValue)
            {
                issues.Add($"Tuning.txt row {rowIndex + 1} has an invalid BCON id '{selector}'.");
                rowIndex = SkipGroup(rows, rowIndex + 1);
                continue;
            }

            var constantId = (ushort)constantIdValue;
            var bconChunk = currentFile.Find(ResourceType.Bcon, constantId);
            if (bconChunk is null)
            {
                issues.Add($"Tuning.txt row {rowIndex + 1} refers to missing BCON {constantId}.");
                rowIndex = SkipGroup(rows, rowIndex + 1);
                continue;
            }

            var bcon = Bcon.Read(currentFile, bconChunk);
            if (!bcon.TuningOverride)
            {
                issues.Add($"Tuning.txt row {rowIndex + 1} refers to unflagged BCON {constantId}.");
                rowIndex = SkipGroup(rows, rowIndex + 1);
                continue;
            }

            var values = bcon.Constants.ToArray();
            var valueRow = rowIndex + 1;
            while (valueRow < rows.Count && Cell(rows[valueRow], 0).Length != 0)
            {
                var indexText = Cell(rows[valueRow], 0);
                var valueText = Cell(rows[valueRow], 1);
                if (TryParseDecimalPrefix(indexText, out var index) &&
                    TryParseDecimalPrefix(valueText, out var value) &&
                    index >= 0 && index < values.Length)
                {
                    values[index] = unchecked((short)value);
                }
                else
                {
                    issues.Add($"Tuning.txt row {valueRow + 1} has an invalid BCON index/value pair.");
                }
                valueRow++;
            }

            BehaviorScope.SetConstantOverrideForFile(currentFile, constantId, values);
            rowIndex = SkipGroup(rows, valueRow);
        }

        return issues;
    }

    private static int ParseGuidGroup(IReadOnlyList<string[]> rows, int headerRowIndex, ObjectRegistry registry, List<string> issues)
    {
        var row = rows[headerRowIndex];
        var selector = Cell(row, 1);
        var guidText = selector[GuidMarker.Length..].Trim();
        if (guidText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            guidText = guidText[2..];

        uint guid;
        var parsedGuid = uint.TryParse(guidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out guid)
            || uint.TryParse(guidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid);

        if (!parsedGuid)
        {
            issues.Add($"Tuning.txt row {headerRowIndex + 1} names an unknown object GUID '{guidText}'.");
            return SkipGroup(rows, headerRowIndex + 1);
        }

        if (registry.Entries.Count > 0 && registry.Find(guid) is null)
        {
            issues.Add($"Tuning.txt row {headerRowIndex + 1} names an unknown object GUID '{guidText}'.");
            return SkipGroup(rows, headerRowIndex + 1);
        }

        var group = new GuidTuningOverrideGroup(guid);
        var valueRow = headerRowIndex + 1;

        while (valueRow < rows.Count && Cell(rows[valueRow], 0).Length != 0)
        {
            var dataRow = rows[valueRow];
            var entryIndexText = Cell(dataRow, 0);
            var motiveCodeText = Cell(dataRow, 1);
            var keyword = Cell(dataRow, 2).Trim();
            var paramText = Cell(dataRow, 3).Trim();

            if (!TryParseDecimalPrefix(entryIndexText, out var entryIndex) || entryIndex < 0)
            {
                issues.Add($"Tuning.txt row {valueRow + 1} has an invalid interaction index '{entryIndexText}'.");
                valueRow++;
                continue;
            }

            if (!group.Interactions.TryGetValue(entryIndex, out var interactionOverride))
            {
                interactionOverride = new InteractionOverride();
                group.Interactions[entryIndex] = interactionOverride;
            }

            if (string.Equals(keyword, "att", StringComparison.OrdinalIgnoreCase))
            {
                var attCode = GetGameAttFromDBAtt(paramText);
                interactionOverride.AttenuationCode = (uint)attCode;
                interactionOverride.AttenuationValue = attCode switch
                {
                    1 => 1.0f,
                    2 => 0.66f,
                    3 => 0.33f,
                    4 => 0.0f,
                    _ => 0.33f
                };
            }
            else if (string.Equals(keyword, "ads", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseDecimalPrefix(motiveCodeText, out var rawMotive) || MapMotiveCode(rawMotive) is not (>= 0 and var mappedMotive))
                {
                    issues.Add($"Tuning.txt row {valueRow + 1} has an invalid motive index '{motiveCodeText}'.");
                }
                else if (!TryParseDecimalPrefix(paramText, out var impact) || GetGameAdFromDBImpact(impact) is not (>= 0 and var mappedAd))
                {
                    issues.Add($"Tuning.txt row {valueRow + 1} has an invalid advertisement impact '{paramText}'.");
                }
                else
                {
                    if (!interactionOverride.Motives.TryGetValue(mappedMotive, out var motiveOv))
                    {
                        motiveOv = new MotiveOverride();
                        interactionOverride.Motives[mappedMotive] = motiveOv;
                    }
                    motiveOv.AdKind = (short)mappedAd;
                    motiveOv.ActiveFlag = 1;
                }
            }
            else if (string.Equals(keyword, "mod", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(keyword, "max", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseDecimalPrefix(motiveCodeText, out var rawMotive) || MapMotiveCode(rawMotive) is not (>= 0 and var mappedMotive))
                {
                    issues.Add($"Tuning.txt row {valueRow + 1} has an invalid motive index '{motiveCodeText}'.");
                }
                else if (!TryParseDecimalPrefix(paramText, out var delta))
                {
                    issues.Add($"Tuning.txt row {valueRow + 1} has an invalid motive delta '{paramText}'.");
                }
                else
                {
                    if (!interactionOverride.Motives.TryGetValue(mappedMotive, out var motiveOv))
                    {
                        motiveOv = new MotiveOverride();
                        interactionOverride.Motives[mappedMotive] = motiveOv;
                    }
                    motiveOv.DeltaAdjustment = unchecked((short)delta);
                }
            }
            else
            {
                issues.Add($"Tuning.txt row {valueRow + 1} has an unrecognized keyword '{keyword}'.");
            }

            valueRow++;
        }

        SetGuidOverride(guid, group);
        return SkipGroup(rows, valueRow);
    }

    private static List<string[]> ParseRows(string text)
    {
        var rows = new List<string[]>();
        foreach (var line in text.Split('\n'))
        {
            var withoutCarriageReturn = line.EndsWith('\r') ? line[..^1] : line;
            rows.Add(withoutCarriageReturn.Split('\t', StringSplitOptions.None));
        }
        if (text.EndsWith('\n')) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private static int SkipGroup(IReadOnlyList<string[]> rows, int rowIndex)
    {
        while (rowIndex < rows.Count && Cell(rows[rowIndex], 0).Length != 0) rowIndex++;
        return rowIndex + (rowIndex < rows.Count ? 1 : 0);
    }

    private static string Cell(IReadOnlyList<string> row, int column) =>
        column < row.Count ? row[column] : string.Empty;

    private static bool TryParseDecimalPrefix(string value, out int number)
    {
        number = 0;
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        var end = start;
        if (end < value.Length && value[end] is '+' or '-') end++;
        var digitsStart = end;
        while (end < value.Length && value[end] is >= '0' and <= '9') end++;
        return end > digitsStart && int.TryParse(value.AsSpan(start, end - start), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number);
    }

    private static IffFile? FindObjectFile(ObjectRegistry registry, string gameData, string key)
    {
        var normalizedKey = NormalizePath(key);
        var matches = new HashSet<IffFile>(ReferenceEqualityComparer.Instance);
        foreach (var entry in registry.Entries)
        {
            var sourceParts = entry.Source.Split("::", 2, StringSplitOptions.None);
            var sourcePath = sourceParts[0];
            var resourceName = sourceParts.Length == 2 ? sourceParts[1] : Path.GetFileName(sourcePath);
            var archiveRelative = NormalizePath(Path.GetRelativePath(gameData, sourcePath));
            var resourceRelative = NormalizePath(resourceName);
            var archiveMemberPath = NormalizePath($"{archiveRelative}/{resourceRelative}");
            if (string.Equals(archiveRelative, normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resourceRelative, normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(archiveMemberPath, normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizePath(Path.GetFileName(resourceName)), normalizedKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(resourceName), key, StringComparison.OrdinalIgnoreCase))
                matches.Add(entry.File);
        }
        return matches.Count == 1 ? matches.Single() : null;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');
}
