using System.Globalization;

namespace SimsSDL.UI;

/// <summary>
/// <c>FUN_005109fe</c> builds the 600x382 book screen (<c>cWinMagicBook</c>) for Dialog styles 0 (Spellbook) and 1 (Cookbook).
/// Style 1 (<c>DAT_006f38b0 = 2</c>) uses <c>magic.iff</c> STR# 14/10 for recipe titles/descriptions and
/// <c>MagicMasterSpells.iff</c> STR# 5 for the 3 ingredient IDs per recipe (ordered 1..9 per <c>FUN_004ecfea</c>).
/// Style 0 uses adult mode (<c>DAT_006f38b0 = 1</c>: <c>MagicMasterSpells.iff</c> STR# 15 order, STR# 3 ingredients, <c>magic.iff</c> STR# 12/8)
/// or child mode (<c>DAT_006f38b0 = 0</c>: <c>MagicMasterSpells.iff</c> STR# 16 order, STR# 4 ingredients, <c>magic.iff</c> STR# 13/9).
/// <c>FUN_005117ea</c> calls Global BHAV 496 ("Magic - Has Made Spell/Recipe?") for the selected spell; an unmade spell
/// shows its 3 ingredient icons on the left page, while the right page title and description show Unknown Charm /
/// Unknown Spell / Unknown Magic and the discovery hint from index 0 (or 24) of the corresponding STR# table.
/// </summary>
public sealed class OriginalBookCatalog
{
    public readonly record struct Entry(
        int ResourceID,
        string Title,
        string Description,
        IReadOnlyList<int> IngredientIDs,
        int StationIndex);

    public readonly record struct Ingredient(
        int Id,
        string Name,
        byte[]? BitmapData);

    private readonly record struct AvailabilityKey(int Context, int Item);

    private readonly string _unknownCharmTitle;
    private readonly string _unknownSpellTitle;
    private readonly string _unknownCharmDescription;
    private readonly string _unknownSpellDescription;
    private readonly Dictionary<AvailabilityKey, bool> _madeByContext;

    public bool IsCookbook { get; }
    public bool IsAdultSpellbook { get; }
    public IReadOnlyList<Entry> Entries { get; }
    public IReadOnlyDictionary<int, Ingredient> Ingredients { get; }
    public string TitleFormat { get; }
    public string Ok { get; }
    public string Back { get; }
    public string Next { get; }
    public string PageFormat { get; }

    public OriginalBookCatalog(
        GameContent content,
        byte kind,
        byte? mode = null,
        IReadOnlyList<GameDialog.BookAvailabilityEntry>? availability = null,
        bool? hideBookItem9 = null,
        bool? hideBookItem4 = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        bool isCookbook = kind == 15;
        bool adultSpellbook = (mode ?? 1) == 1;
        IsCookbook = isCookbook;
        IsAdultSpellbook = adultSpellbook;

        IReadOnlyList<string> titles = [];
        IReadOnlyList<string> descriptions = [];
        string titleFormat = isCookbook ? "%s Family Cookbook" : "%s Family Spellbook";
        string ok = "OK";
        string back = "Back";
        string next = "Next";
        string pageFormat = "Page %d of %d";
        string unknownCharmTitle = adultSpellbook ? "Unknown Charm" : "Unknown Magic";
        string unknownSpellTitle = "Unknown Spell";
        string unknownCharmDescription = string.Empty;
        string unknownSpellDescription = string.Empty;

        // FUN_005109fe line 66 calls FUN_005e50fa(1), which is UIText.iff STR# 152 (0x98) index 1 ("Cancel").
        byte[]? uiTextData = TryReadContent(content, "GameData/UIText.iff", "UIText.iff");
        if (uiTextData is not null &&
            OriginalStringResource.TryParse(uiTextData, content.Language.Code, new HashSet<ushort>([152]))?.Tables.TryGetValue(152, out var dlgStrings) == true &&
            dlgStrings.Count > 1 && dlgStrings[1].Length > 0)
        {
            ok = dlgStrings[1];
        }

        // FUN_005109fe lines 44, 78, 94, 136, 146 and FUN_00511d39 line 90 load
        // Spellbook/Cookbook titles (1, 2), Back (3), Next (4), and Page %d of %d (5) from magic.iff STR# 1.
        byte[]? magicStrings = TryReadContent(content, "GameData/magic.iff", "magic.iff");
        if (magicStrings is not null &&
            OriginalStringResource.TryParse(magicStrings, content.Language.Code, new HashSet<ushort>([1, 8, 9, 10, 12, 13, 14])) is { } magicResource)
        {
            IReadOnlyList<string> ui = magicResource.Tables.TryGetValue(1, out var uiList) ? uiList : [];
            int titleIndex = isCookbook ? 1 : 0;
            if (titleIndex < ui.Count && ui[titleIndex].Length > 0) titleFormat = ui[titleIndex];
            if (ui.Count > 2 && ui[2].Length > 0) back = ui[2];
            if (ui.Count > 3 && ui[3].Length > 0) next = ui[3];
            if (ui.Count > 4 && ui[4].Length > 0) pageFormat = ui[4];

            if (isCookbook)
            {
                titles = magicResource.Tables.TryGetValue(14, out var cookTitles) ? cookTitles : [];
                descriptions = magicResource.Tables.TryGetValue(10, out var cookDescs) ? cookDescs : [];
            }
            else if (adultSpellbook)
            {
                titles = magicResource.Tables.TryGetValue(12, out var adultTitles) ? adultTitles : [];
                descriptions = magicResource.Tables.TryGetValue(8, out var adultDescs) ? adultDescs : [];
                if (titles.Count > 0 && titles[0].Length > 0) unknownCharmTitle = titles[0];
                if (titles.Count > 24 && titles[24].Length > 0) unknownSpellTitle = titles[24];
                if (descriptions.Count > 0 && descriptions[0].Length > 0) unknownCharmDescription = descriptions[0];
                if (descriptions.Count > 24 && descriptions[24].Length > 0) unknownSpellDescription = descriptions[24];
            }
            else
            {
                titles = magicResource.Tables.TryGetValue(13, out var kidTitles) ? kidTitles : [];
                descriptions = magicResource.Tables.TryGetValue(9, out var kidDescs) ? kidDescs : [];
                if (titles.Count > 0 && titles[0].Length > 0) unknownCharmTitle = titles[0];
                if (descriptions.Count > 0 && descriptions[0].Length > 0) unknownCharmDescription = descriptions[0];
                unknownSpellTitle = unknownCharmTitle;
                unknownSpellDescription = unknownCharmDescription;
            }
        }

        string masterArchivePath = Path.Combine(content.Root, "ExpansionPack7", "ExpansionPack7.far");
        string unleashedArchivePath = Path.Combine(content.Root, "ExpansionPack5", "ExpansionPack5.far");
        string superstarArchivePath = Path.Combine(content.Root, "ExpansionPack6", "ExpansionPack6.far");
        bool hasUnleashed = File.Exists(unleashedArchivePath);
        bool hasSuperstar = File.Exists(superstarArchivePath);

        var adultOrder = new List<int>();
        var kidOrder = new List<int>();
        IReadOnlyList<string> adultRecipes = [];
        IReadOnlyList<string> kidRecipes = [];
        IReadOnlyList<string> cookRecipes = [];
        IReadOnlyList<string> fallbackIngredientNames = [];
        IReadOnlyList<string> ingredientGUIDStrings = [];

        FARArchive? masterArchive = TryOpenArchive(masterArchivePath);
        byte[]? masterData = masterArchive is not null ? TryReadArchiveEntry(masterArchive, "MagicMasterSpells.iff") : null;
        if (masterData is not null &&
            OriginalStringResource.TryParse(masterData, content.Language.Code, new HashSet<ushort>([2, 3, 4, 5, 7, 15, 16])) is { } masterStrings)
        {
            fallbackIngredientNames = masterStrings.Tables.TryGetValue(2, out var t2) ? t2 : [];
            adultRecipes = masterStrings.Tables.TryGetValue(3, out var t3) ? t3 : [];
            kidRecipes = masterStrings.Tables.TryGetValue(4, out var t4) ? t4 : [];
            cookRecipes = masterStrings.Tables.TryGetValue(5, out var t5) ? t5 : [];
            ingredientGUIDStrings = masterStrings.Tables.TryGetValue(7, out var t7) ? t7 : [];

            if (masterStrings.Tables.TryGetValue(15, out var t15))
            {
                for (int i = 1; i < t15.Count; i++)
                {
                    if (int.TryParse(t15[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    {
                        adultOrder.Add(id);
                    }
                }
            }

            if (masterStrings.Tables.TryGetValue(16, out var t16))
            {
                for (int i = 1; i < t16.Count; i++)
                {
                    if (int.TryParse(t16[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    {
                        kidOrder.Add(id);
                    }
                }
            }
        }

        static IReadOnlyList<int> ParseIngredients(IReadOnlyList<string> table, int resourceID)
        {
            if (resourceID < 0 || resourceID >= table.Count)
            {
                return [];
            }

            string[] parts = table[resourceID].Split(',');
            var ids = new List<int>(parts.Length);
            foreach (string part in parts)
            {
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    ids.Add(parsed);
                }
            }
            return ids;
        }

        var builtEntries = new List<Entry>();
        if (isCookbook)
        {
            // FUN_004ec793 + FUN_004ecfea: Cookbook (DAT_006f38b0 == 2) iterates 1..9 directly.
            for (int resourceID = 1; resourceID <= 9; resourceID++)
            {
                if ((resourceID == 7 && !hasUnleashed) ||
                    (resourceID == 9 && hideBookItem9 == true) ||
                    (resourceID == 4 && hideBookItem4 == true))
                {
                    continue;
                }

                if (resourceID >= titles.Count || titles[resourceID].Length == 0)
                {
                    continue;
                }

                builtEntries.Add(new Entry(
                    resourceID,
                    titles[resourceID],
                    resourceID < descriptions.Count ? descriptions[resourceID] : string.Empty,
                    ParseIngredients(cookRecipes, resourceID),
                    resourceID > 4 ? 3 : 4));
            }
        }
        else if (adultSpellbook)
        {
            // FUN_004ec793's adult list omits source row 12, the Unleashed spell (22) when bit 0x20 is absent,
            // and The Price of Fame (10) when bit 0x40 is absent.
            for (int offset = 0; offset < adultOrder.Count; offset++)
            {
                int resourceID = adultOrder[offset];
                int sourceRow = offset + 1;
                if (sourceRow == 12 || (resourceID == 22 && !hasUnleashed) || (resourceID == 10 && !hasSuperstar))
                {
                    continue;
                }

                string title = resourceID >= 0 && resourceID < titles.Count && titles[resourceID].Length > 0
                    ? titles[resourceID]
                    : $"Spell {offset + 1}";
                string description = resourceID >= 0 && resourceID < descriptions.Count ? descriptions[resourceID] : string.Empty;
                builtEntries.Add(new Entry(
                    resourceID,
                    title,
                    description,
                    ParseIngredients(adultRecipes, resourceID),
                    resourceID < 12 ? 1 : 2));
            }
        }
        else
        {
            // Child mode (DAT_006f38b0 == 0) uses STR# 16 order, STR# 4 ingredients, and magic.iff STR# 13/9.
            for (int offset = 0; offset < kidOrder.Count; offset++)
            {
                int resourceID = kidOrder[offset];
                if ((resourceID == 9 && hideBookItem9 == true) || (resourceID == 4 && hideBookItem4 == true))
                {
                    continue;
                }

                string title = resourceID >= 0 && resourceID < titles.Count && titles[resourceID].Length > 0
                    ? titles[resourceID]
                    : $"Spell {offset + 1}";
                string description = resourceID >= 0 && resourceID < descriptions.Count ? descriptions[resourceID] : string.Empty;
                builtEntries.Add(new Entry(
                    resourceID,
                    title,
                    description,
                    ParseIngredients(kidRecipes, resourceID),
                    0));
            }
        }
        Entries = builtEntries;

        // Resolve ingredient GUIDs (STR# 7) against TokensMagic.iff and GardenCrops.iff (FUN_004ec25d, FUN_004ec5aa, FUN_004ec5f0).
        var guidToCatalogChunk = new Dictionary<uint, (byte[]? Bmp, string? Name)>();
        var iffSources = new List<byte[]>(2);
        if (masterArchive is not null && TryReadArchiveEntry(masterArchive, "TokensMagic.iff") is { } tokensData)
        {
            iffSources.Add(tokensData);
        }
        if (hasUnleashed &&
            TryOpenArchive(unleashedArchivePath) is { } unleashedArchive &&
            TryReadArchiveEntry(unleashedArchive, "GardenCrops.iff") is { } cropsData)
        {
            iffSources.Add(cropsData);
        }

        foreach (byte[] iffData in iffSources)
        {
            foreach (var (guid, value) in ParseObjectCatalogChunks(iffData, content.Language.Code))
            {
                guidToCatalogChunk[guid] = value;
            }
        }

        var resolvedIngredients = new Dictionary<int, Ingredient>(31);
        for (int id = 1; id <= 31; id++)
        {
            string guidString = id < ingredientGUIDStrings.Count ? ingredientGUIDStrings[id].Trim() : string.Empty;
            (byte[]? Bmp, string? Name)? lookup =
                uint.TryParse(guidString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint guid) &&
                guidToCatalogChunk.TryGetValue(guid, out var found)
                    ? found
                    : null;

            string fallbackName = id < fallbackIngredientNames.Count
                ? fallbackIngredientNames[id].Trim()
                : $"Ingredient {id}";
            string name = lookup is { Name: { Length: > 0 } resolvedName } ? resolvedName : fallbackName;
            resolvedIngredients[id] = new Ingredient(id, name, lookup?.Bmp);
        }
        Ingredients = resolvedIngredients;

        TitleFormat = titleFormat;
        Ok = ok;
        Back = back;
        Next = next;
        PageFormat = pageFormat;
        _unknownCharmTitle = unknownCharmTitle;
        _unknownSpellTitle = unknownSpellTitle;
        _unknownCharmDescription = unknownCharmDescription;
        _unknownSpellDescription = unknownSpellDescription;

        _madeByContext = new Dictionary<AvailabilityKey, bool>();
        if (availability is not null)
        {
            foreach (var item in availability)
            {
                _madeByContext[new AvailabilityKey(item.ContextItemId, item.ItemId)] = item.Made;
            }
        }
    }

    public bool IsMade(Entry entry, int contextItemId)
    {
        if (IsCookbook)
        {
            return true;
        }
        if (_madeByContext.TryGetValue(new AvailabilityKey(contextItemId, entry.ResourceID), out bool exact))
        {
            return exact;
        }
        if (contextItemId != 0 && _madeByContext.TryGetValue(new AvailabilityKey(0, entry.ResourceID), out bool fallback))
        {
            return fallback;
        }
        return true;
    }

    public string DetailTitle(Entry entry, int contextItemId)
    {
        if (IsMade(entry, contextItemId))
        {
            return entry.Title;
        }
        return !IsAdultSpellbook || entry.ResourceID < 12 ? _unknownCharmTitle : _unknownSpellTitle;
    }

    public string DetailDescription(Entry entry, int contextItemId)
    {
        if (IsMade(entry, contextItemId))
        {
            return entry.Description;
        }
        string unknown = !IsAdultSpellbook || entry.ResourceID < 12 ? _unknownCharmDescription : _unknownSpellDescription;
        return unknown.Length == 0 ? entry.Description : unknown;
    }

    private static Dictionary<uint, (byte[]? Bmp, string? Name)> ParseObjectCatalogChunks(byte[] data, int languageCode)
    {
        if (data.Length < 64)
        {
            return [];
        }

        var objdToCatID = new Dictionary<uint, ushort>();
        var bmps = new Dictionary<ushort, byte[]>();
        var ctssChunks = new HashSet<ushort>();
        int position = 64;

        while (position + 76 <= data.Length)
        {
            uint chunkSizeValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
            if (chunkSizeValue < 76 || chunkSizeValue > int.MaxValue)
            {
                break;
            }
            int chunkSize = (int)chunkSizeValue;
            if (position > data.Length - chunkSize)
            {
                break;
            }

            string type = Encoding.Latin1.GetString(data, position, 4);
            ushort chunkID = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 8, 2));
            int bodyStart = position + 76;
            int bodyCount = chunkSize - 76;

            if (string.Equals(type, "OBJD", StringComparison.Ordinal) && bodyCount >= 0x54)
            {
                uint guid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(bodyStart + 28, 4));
                ushort catID = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(bodyStart + 0x52, 2));
                if (guid != 0 && catID != 0)
                {
                    objdToCatID[guid] = catID;
                    ctssChunks.Add(catID);
                }
            }
            else if (string.Equals(type, "BMP_", StringComparison.Ordinal) || string.Equals(type, "BMAP", StringComparison.Ordinal))
            {
                bmps[chunkID] = data.AsSpan(bodyStart, bodyCount).ToArray();
            }

            position += chunkSize;
        }

        var ctssTables = ParseCTSSTitles(data, languageCode, ctssChunks);
        var result = new Dictionary<uint, (byte[]? Bmp, string? Name)>(objdToCatID.Count);
        foreach (var (guid, catID) in objdToCatID)
        {
            bmps.TryGetValue(catID, out byte[]? bmp);
            ctssTables.TryGetValue(catID, out string? name);
            result[guid] = (bmp, name);
        }
        return result;
    }

    private static Dictionary<ushort, string> ParseCTSSTitles(byte[] data, int languageCode, HashSet<ushort> chunks)
    {
        if (chunks.Count == 0 || data.Length < 64)
        {
            return [];
        }

        byte[] copy = (byte[])data.Clone();
        int position = 64;
        while (position + 76 <= copy.Length)
        {
            uint chunkSizeValue = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(position + 4, 4));
            if (chunkSizeValue < 76 || chunkSizeValue > int.MaxValue)
            {
                break;
            }
            int chunkSize = (int)chunkSizeValue;
            if (position > copy.Length - chunkSize)
            {
                break;
            }

            ushort chunkID = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(position + 8, 2));
            if (chunks.Contains(chunkID) &&
                copy[position] == 0x43 && copy[position + 1] == 0x54 && copy[position + 2] == 0x53 && copy[position + 3] == 0x53)
            {
                copy[position] = 0x53;     // 'S'
                copy[position + 1] = 0x54; // 'T'
                copy[position + 2] = 0x52; // 'R'
                copy[position + 3] = 0x23; // '#'
            }

            position += chunkSize;
        }

        if (OriginalStringResource.TryParse(copy, languageCode, chunks) is not { } resource)
        {
            return [];
        }

        var titles = new Dictionary<ushort, string>(resource.Tables.Count);
        foreach (var (cid, entries) in resource.Tables)
        {
            if (entries.Count > 0 && entries[0].Length > 0)
            {
                titles[cid] = entries[0];
            }
        }
        return titles;
    }

    private static byte[]? TryReadContent(GameContent content, params string[] paths)
    {
        foreach (string path in paths)
        {
            try
            {
                return content.Read(path);
            }
            catch
            {
                // Try fallback path.
            }
        }
        return null;
    }

    private static FARArchive? TryOpenArchive(string path)
    {
        try
        {
            return File.Exists(path) ? new FARArchive(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadArchiveEntry(FARArchive archive, string path)
    {
        try
        {
            return archive.Read(path);
        }
        catch
        {
            return null;
        }
    }
}
