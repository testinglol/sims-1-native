using System.Globalization;
using System.Runtime.CompilerServices;
using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>
/// Native counterpart of Sims.exe's character wardrobe/body-strings scope (FUN_00408b9d, FUN_00409377, FUN_004094f4,
/// FUN_00408c57, FUN_004096c5, FUN_0040a3c6, FUN_00409953..FUN_0040a1af, FUN_0040a5ea, FUN_0040afbd, FUN_00587bed).
/// </summary>
public static class WardrobeSuitService
{
    public sealed record SuitChoice(string Appearance, string BodyTexture, string HandTexture);

    private static readonly char[] CategoryCodes = ['b', 'f', 's', 'l', 'w', 'h'];
    private static readonly ConditionalWeakTable<ObjectRegistry, CatalogCache> Catalogs = new();

    private sealed class CatalogCache
    {
        public CatalogCache(string[] appearances, HashSet<string> appearanceSet, string[] textures, HashSet<string> textureSet)
        {
            Appearances = appearances;
            AppearanceSet = appearanceSet;
            Textures = textures;
            TextureSet = textureSet;
        }

        public string[] Appearances { get; }
        public HashSet<string> AppearanceSet { get; }
        public string[] Textures { get; }
        public HashSet<string> TextureSet { get; }
    }

    public static bool HasCatalog(ObjectRegistry? registry) => GetCatalog(registry) is not null;

    /// <summary>FUN_00409377: loads the person's BodyStrings STR# resource into <paramref name="scope"/>'s 1-based
    /// runtime context (padded to 35 entries) and populates any missing category defaults (0x1f, 0x22, 0x23).</summary>
    public static InteractionBehaviorRuntimeContext BeginPersonScope(
        SimulationWorld world,
        XPerson person,
        InteractionBehaviorScope scope)
    {
        var context = scope.RuntimeContext;
        var entry = ResolvePersonEntry(world.ObjectRegistry, person);
        PopulateContextFromEntry(world.ObjectRegistry, entry, world.Language, context, persistInitializedDefaults: true);
        if (entry is null && person.SavedCharacter is { } saved && saved.Parts.Count > 0 && !string.IsNullOrEmpty(saved.Parts[0].Item1))
        {
            var body = StripAprSuffix(saved.Parts[0].Item1);
            var tex = saved.Parts[0].Item2;
            context.SetString(0x02, string.IsNullOrEmpty(tex) ? body : $"{body},{tex}");
        }
        return context;
    }

    internal static void PopulateContextFromEntry(
        ObjectRegistry? registry,
        ObjectEntry? entry,
        byte language,
        InteractionBehaviorRuntimeContext context,
        bool persistInitializedDefaults)
    {
        context.ClearStrings();
        if (entry is null) return;

        var stringsId = entry.Definition[ObjdField.BodyStringsId];
        var chunk = stringsId == 0 ? null : entry.File.Find(ResourceType.Str, stringsId);
        var values = chunk is null ? [] : StringTable.Read(entry.File, chunk).ForLanguage(language);
        var count = Math.Max(35, values.Count);
        for (var i = 1; i <= count; i++)
            context.SetString(i, i <= values.Count ? values[i - 1] : string.Empty);

        // FUN_00409377 lines 17-68: initialize empty formal (0x1f), winter (0x22), and high-fashion (0x23) entries
        // for human Sims whose everyday suit (0x02) is populated.
        var body2 = (context.StringAt(0x02) ?? string.Empty).Trim();
        var genderMode = ResolveGenderMode(context, null);
        if (body2.Length == 0 || genderMode is < 0 or > 1) return;

        var catalog = GetCatalog(registry);
        if (catalog is null) return;

        var isChild = ResolveIsChild(context, null);
        var skin = NormalizeSkin(context.StringAt(0x0f));
        var build = ExtractAdultBuild(body2);
        var dirty = false;

        if (isChild)
        {
            if (string.IsNullOrEmpty(context.StringAt(0x1f)))
            {
                context.SetString(0x1f, "ADDED");
                dirty = true;
            }
            if (string.IsNullOrEmpty(context.StringAt(0x23)))
            {
                context.SetString(0x23, "ADDED");
                dirty = true;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(context.StringAt(0x1f)))
            {
                context.SetString(0x1f, FindFirstCategorySuit(catalog, 'f', genderMode, false, build, skin) ?? "ADDED");
                dirty = true;
            }
            if (string.IsNullOrEmpty(context.StringAt(0x23)))
            {
                context.SetString(0x23, FindFirstCategorySuit(catalog, 'h', genderMode, false, build, skin) ?? "ADDED");
                dirty = true;
            }
        }

        if (string.IsNullOrEmpty(context.StringAt(0x22)))
        {
            context.SetString(0x22, FindFirstCategorySuit(catalog, 'w', genderMode, isChild, build, skin) ?? "ADDED");
            dirty = true;
        }

        if (dirty && persistInitializedDefaults && registry is not null)
            PersistContextToEntry(registry, entry, language, context);
    }

    /// <summary>FUN_004094f4 / FUN_0040a5ea / FUN_0040afbd / FUN_00587bed: recomputes dependent body strings
    /// (nude, undies, handgroups, body-build category sync), writes the updated STR# back to the registry entry,
    /// and refreshes the live XPerson's SavedCharacter/PresentationCharacter parts.</summary>
    public static void EndPersonScope(
        SimulationWorld world,
        XPerson? person,
        InteractionBehaviorScope scope,
        bool commit)
    {
        if (!commit || scope.QueryContext is not { } context) return;
        var registry = world.ObjectRegistry;
        var entry = person is not null ? ResolvePersonEntry(registry, person) : null;
        CommitContext(registry, entry, world.Language, context, person);
    }

    internal static IffFile CommitContextToFile(
        ObjectRegistry? registry,
        IffFile file,
        ushort bodyStringsId,
        byte language,
        InteractionBehaviorRuntimeContext context)
    {
        if (GetCatalog(registry) is { } catalog)
            SyncDependentBodyStrings(catalog, context);

        var values = ExtractOneBasedStrings(context);
        if (bodyStringsId == 0) return file;
        var existingChunk = file.Find(ResourceType.Str, bodyStringsId);
        var encoded = existingChunk is not null
            ? StringTable.Read(file, existingChunk).WriteLanguageValues(values, language)
            : StringTable.CreateLanguageValues(values, language);
        return existingChunk is not null
            ? file.WithChunkDataResized(ResourceType.Str, bodyStringsId, encoded)
            : file.WithAddedChunk(ResourceType.Str, bodyStringsId, encoded);
    }

    internal static void CommitContext(
        ObjectRegistry? registry,
        ObjectEntry? entry,
        byte language,
        InteractionBehaviorRuntimeContext context,
        XPerson? person)
    {
        if (GetCatalog(registry) is { } catalog)
            SyncDependentBodyStrings(catalog, context);

        if (registry is not null && entry is not null)
            PersistContextToEntry(registry, entry, language, context);

        if (person is not null)
            RefreshPersonCharacterFromContext(person, context);
    }

    /// <summary>FUN_00408c57 / FUN_004096c5 / FUN_0040a3c6 / FUN_00409953..FUN_0040a1af: builds the ordered
    /// action list for wardrobe suit category <paramref name="listIndex"/> (0..5 = b, f, s, l, w, h) or heads ('c').</summary>
    public static InteractionBehaviorActionList? GetActionList(
        SimulationWorld world,
        XPerson? person,
        InteractionBehaviorRuntimeContext? context,
        int listIndex,
        bool headsList = false)
    {
        var registry = world.ObjectRegistry;
        var catalog = GetCatalog(registry);
        if (catalog is null)
        {
            if (!headsList && person?.SavedCharacter is { } character)
            {
                var fallbackSuits = character.ExtraParts
                    .Where(part => !string.IsNullOrEmpty(part.Item1))
                    .Select(part => new InteractionBehaviorAction(string.Empty, part.Item1))
                    .ToList();
                if (fallbackSuits.Count > 0) return new InteractionBehaviorActionList(fallbackSuits);
            }
            return null;
        }

        if (!headsList && (uint)listIndex >= CategoryCodes.Length)
            return null;

        InteractionBehaviorRuntimeContext effectiveContext;
        if (context is not null && context.StringCount() > 0)
        {
            effectiveContext = context;
        }
        else
        {
            effectiveContext = new InteractionBehaviorRuntimeContext();
            if (person is not null)
                PopulateContextFromEntry(registry, ResolvePersonEntry(registry, person), world.Language, effectiveContext, persistInitializedDefaults: false);
        }

        var genderMode = ResolveGenderMode(effectiveContext, person);
        var isChild = ResolveIsChild(effectiveContext, person);
        var skin = NormalizeSkin(effectiveContext.StringAt(0x0f));

        if (headsList)
        {
            var headActions = BuildHeadActions(catalog, genderMode, isChild, skin);
            return new InteractionBehaviorActionList(headActions);
        }

        var category = CategoryCodes[listIndex];
        var choices = BuildCategoryChoices(catalog, category, genderMode, isChild, skin);
        var actions = new List<InteractionBehaviorAction>(choices.Count);
        foreach (var choice in choices)
            actions.Add(new InteractionBehaviorAction(choice.Appearance, "BODY=" + choice.BodyTexture));
        return new InteractionBehaviorActionList(actions);
    }

    /// <summary>Enumerates wardrobe/pet choices using the exact same appearance and texture ordering as
    /// <see cref="GetActionList"/>, including handgroup texture resolution for preview rendering.</summary>
    public static SuitChoice[] GetChoices(
        ObjectRegistry registry,
        byte language,
        XPerson subject,
        bool pet,
        int outfitType,
        string? gameRootFallback = null)
    {
        var catalog = GetCatalog(registry, gameRootFallback);
        if (catalog is null) return [];
        if (!pet && (uint)outfitType >= CategoryCodes.Length) return [];

        var context = new InteractionBehaviorRuntimeContext();
        PopulateContextFromEntry(registry, ResolvePersonEntry(registry, subject), language, context, persistInitializedDefaults: false);

        var genderMode = ResolveGenderMode(context, subject);
        if (pet && genderMode is < 2 or > 5)
        {
            var gender = subject[PersonData.Gender];
            genderMode = (gender & 2) != 0 ? 2 : (gender & 4) != 0 ? 4 : genderMode;
        }

        var isChild = !pet && ResolveIsChild(context, subject);
        var skin = pet ? string.Empty : NormalizeSkin(context.StringAt(0x0f));
        var category = pet ? 'b' : CategoryCodes[outfitType];
        return BuildCategoryChoices(catalog, category, genderMode, isChild, skin).ToArray();
    }

    public static int ResolvePersonGenderMode(ObjectRegistry registry, byte language, XPerson person)
    {
        var context = new InteractionBehaviorRuntimeContext();
        PopulateContextFromEntry(registry, ResolvePersonEntry(registry, person), language, context, persistInitializedDefaults: false);
        return ResolveGenderMode(context, person);
    }

    private static List<InteractionBehaviorAction> BuildHeadActions(
        CatalogCache catalog,
        int genderMode,
        bool isChild,
        string skin)
    {
        var result = new List<InteractionBehaviorAction>();
        if (genderMode is < 0 or > 1 || skin.Length == 0) return result;
        var expectedGender = genderMode == 0 ? 'm' : 'f';
        var expectedAge = isChild ? 'c' : 'a';

        // FUN_0040a3c6: matches 'c' + 3 chars (excluding 't'/'T' at index 1) + [m|f|u] + [c|a],
        // then pairs with every installed texture starting with appearance[..6] + skin.
        foreach (var appearance in catalog.Appearances)
        {
            if (appearance.Length < 6) continue;
            if (FoldAscii(appearance[0]) != 'c') continue;
            if (FoldAscii(appearance[1]) == 't') continue;
            var g = FoldAscii(appearance[4]);
            if (g != expectedGender && g != 'u') continue;
            if (FoldAscii(appearance[5]) != expectedAge) continue;

            var texPrefix = appearance[..6] + skin;
            foreach (var texture in catalog.Textures)
            {
                if (texture.StartsWith(texPrefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(new InteractionBehaviorAction(appearance, "BODY=" + texture));
            }
        }
        return result;
    }

    private static List<SuitChoice> BuildCategoryChoices(
        CatalogCache catalog,
        char category,
        int genderMode,
        bool isChild,
        string skin)
    {
        var result = new List<SuitChoice>();
        if (genderMode is >= 2 and <= 5)
        {
            // FUN_004096c5 lines 41-50: pets only populate category 'b' ("dog" for 2..3, "cat" for 4..5).
            if (category != 'b') return result;
            var species = genderMode < 4 ? "dog" : "cat";
            foreach (var appearance in catalog.Appearances)
            {
                if (appearance.Length < 7 || FoldAscii(appearance[0]) != 'b') continue;
                if (!appearance.AsSpan(4, 3).Equals(species, StringComparison.OrdinalIgnoreCase)) continue;
                var texPrefix = appearance[..7];
                foreach (var texture in catalog.Textures)
                {
                    if (texture.StartsWith(texPrefix, StringComparison.OrdinalIgnoreCase))
                        result.Add(new SuitChoice(appearance, texture, string.Empty));
                }
            }
            return result;
        }

        if (genderMode is < 0 or > 1 || skin.Length == 0) return result;
        var expectedGender = genderMode == 0 ? 'm' : 'f';
        var expectedAge = isChild ? 'c' : 'a';

        // FUN_004096c5 / FUN_00409953..FUN_0040a1af: matches <category> + 3 chars + [m|f|u] + [c|a] + 3 chars,
        // then pairs with every installed texture starting with appearance[..9] + skin.
        foreach (var appearance in catalog.Appearances)
        {
            if (appearance.Length < 9 || FoldAscii(appearance[0]) != category) continue;
            var g = FoldAscii(appearance[4]);
            if (g != expectedGender && g != 'u') continue;
            if (FoldAscii(appearance[5]) != expectedAge) continue;

            var texPrefix = appearance[..9] + skin;
            var handGroup = ResolveHandSuffix(catalog, appearance, skin);
            foreach (var texture in catalog.Textures)
            {
                if (texture.StartsWith(texPrefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(new SuitChoice(appearance, texture, handGroup));
            }
        }
        return result;
    }

    private static void SyncDependentBodyStrings(CatalogCache catalog, InteractionBehaviorRuntimeContext context)
    {
        var genderMode = ResolveGenderMode(context, null);
        if (genderMode is >= 2 and <= 5)
        {
            // FUN_0040a5ea lines 214-247: dog/cat bodies clear head and human outfit/hand slots to "NONE".
            context.SetString(0x03, "NONE");
            for (var idx = 0x10; idx <= 0x23; idx++)
                context.SetString(idx, "NONE");
            return;
        }

        if (genderMode is < 0 or > 1) return;
        var isChild = ResolveIsChild(context, null);
        var skin = NormalizeSkin(context.StringAt(0x0f));
        if (skin.Length == 0) return;

        var everyday = (context.StringAt(0x02) ?? string.Empty).Trim();
        var bodyAppearance = ExtractAppearanceName(everyday);
        if (bodyAppearance.Length == 0) return;

        var genderChar = genderMode == 0 ? 'm' : 'f';
        var handMeshGender = isChild ? 'u' : genderChar;
        var handTextureSuffix = ResolveHandSuffix(catalog, bodyAppearance, skin);

        // FUN_0040afbd lines 186-234: open (0x12/0x13), pointing (0x14/0x15), clenched (0x16/0x17) hands.
        var openTex = ResolveHandTextureName(catalog, "huao", handTextureSuffix, skin);
        var pointTex = ResolveHandTextureName(catalog, "huap", handTextureSuffix, skin);
        var fistTex = ResolveHandTextureName(catalog, "huac", handTextureSuffix, skin);
        context.SetString(0x12, $"H{handMeshGender}LO,HAND={openTex}");
        context.SetString(0x13, $"H{handMeshGender}RO,HAND={openTex}");
        context.SetString(0x14, $"H{handMeshGender}LP,HAND={pointTex}");
        context.SetString(0x15, $"H{handMeshGender}RP,HAND={pointTex}");
        context.SetString(0x16, $"H{handMeshGender}LC,HAND={fistTex}");
        context.SetString(0x17, $"H{handMeshGender}RC,HAND={fistTex}");

        if (isChild)
        {
            // FUN_0040afbd lines 153-185: child nude (0x10) and swimwear/undies fallback (0x11).
            var nudeApp = catalog.AppearanceSet.Contains("nuchd_01") ? "nuchd_01" : null;
            var nudeTex = FindMatchingTexture(catalog, $"nuchd{skin}_") ?? FindMatchingTexture(catalog, $"nuchd{skin}");
            if (nudeApp is not null && nudeTex is not null)
                context.SetString(0x10, $"{nudeApp},BODY={nudeTex}");
            var undiesTex = FindMatchingTexture(catalog, $"u{genderChar}chd{skin}_") ?? FindMatchingTexture(catalog, $"uuchd{skin}_");
            if (nudeApp is not null && undiesTex is not null)
                context.SetString(0x11, $"{nudeApp},BODY={undiesTex}");
            if (string.IsNullOrEmpty(context.StringAt(0x1f))) context.SetString(0x1f, "ADDED");
            if (string.IsNullOrEmpty(context.StringAt(0x23))) context.SetString(0x23, "ADDED");
            if (string.IsNullOrEmpty(context.StringAt(0x22)))
                context.SetString(0x22, FindFirstCategorySuit(catalog, 'w', genderMode, true, string.Empty, skin) ?? "ADDED");
            return;
        }

        var build = ExtractAdultBuild(everyday);
        if (build.Length == 3)
        {
            var nudeCandidate = $"n{genderChar}{build}_01";
            var nudeTex = FindMatchingTexture(catalog, $"n{genderChar}{build}{skin}_") ?? FindMatchingTexture(catalog, $"n{genderChar}{build}{skin}");
            if (catalog.AppearanceSet.Contains(nudeCandidate) && nudeTex is not null)
                context.SetString(0x10, $"{nudeCandidate},BODY={nudeTex}");

            var undiesCandidate = $"n{genderChar}{build}_01";
            var undiesTex = FindMatchingTexture(catalog, $"u{genderChar}{build}{skin}_");
            if (catalog.AppearanceSet.Contains(undiesCandidate) && undiesTex is not null)
                context.SetString(0x11, $"{undiesCandidate},BODY={undiesTex}");

            // FUN_0040a5ea: keep adult category outfits aligned with skin/gender when empty or from a different build.
            SyncAdultCategorySlot(catalog, context, 0x1f, 'f', genderMode, build, skin, fallbackAdded: true);
            SyncAdultCategorySlot(catalog, context, 0x20, 's', genderMode, build, skin, fallbackAdded: false);
            SyncAdultCategorySlot(catalog, context, 0x21, 'l', genderMode, build, skin, fallbackAdded: false);
            SyncAdultCategorySlot(catalog, context, 0x22, 'w', genderMode, build, skin, fallbackAdded: true);
            SyncAdultCategorySlot(catalog, context, 0x23, 'h', genderMode, build, skin, fallbackAdded: true);
        }
    }

    private static void SyncAdultCategorySlot(
        CatalogCache catalog,
        InteractionBehaviorRuntimeContext context,
        int oneBasedSlot,
        char category,
        int genderMode,
        string build,
        string skin,
        bool fallbackAdded)
    {
        var current = (context.StringAt(oneBasedSlot) ?? string.Empty).Trim();
        if (current.Length == 0)
        {
            var initial = FindFirstCategorySuit(catalog, category, genderMode, false, build, skin);
            if (initial is not null) context.SetString(oneBasedSlot, initial);
            else if (fallbackAdded) context.SetString(oneBasedSlot, "ADDED");
            return;
        }
        if (current.Equals("ADDED", StringComparison.OrdinalIgnoreCase) ||
            current.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return;

        // Only replace an existing category outfit if its gender or skin no longer matches the Sim;
        // purchased outfits of another adult body build (skn/fit/fat) remain valid in Hot Date+ wardrobes!
        var app = ExtractAppearanceName(current);
        if (app.Length < 9) return;
        var expectedGender = genderMode == 0 ? 'm' : 'f';
        var g = FoldAscii(app[4]);
        var comma = current.IndexOf(',');
        var binding = comma >= 0 ? current[(comma + 1)..] : string.Empty;
        var skinMismatch = binding.Length > 0 && !binding.Contains(skin, StringComparison.OrdinalIgnoreCase);
        if ((g != expectedGender && g != 'u') || FoldAscii(app[5]) != 'a' || skinMismatch)
        {
            var replacement = FindFirstCategorySuit(catalog, category, genderMode, false, build, skin);
            context.SetString(oneBasedSlot, replacement ?? (fallbackAdded ? "ADDED" : string.Empty));
        }
    }

    private static string? FindFirstCategorySuit(
        CatalogCache catalog,
        char category,
        int genderMode,
        bool isChild,
        string build,
        string skin)
    {
        if (genderMode is < 0 or > 1 || skin.Length == 0) return null;
        var expectedGender = genderMode == 0 ? 'm' : 'f';
        var expectedAge = isChild ? 'c' : 'a';

        foreach (var appearance in catalog.Appearances)
        {
            if (appearance.Length < 9 || FoldAscii(appearance[0]) != category) continue;
            var g = FoldAscii(appearance[4]);
            if (g != expectedGender && g != 'u') continue;
            if (FoldAscii(appearance[5]) != expectedAge) continue;
            if (!isChild && build.Length == 3 && !appearance.AsSpan(6, 3).Equals(build, StringComparison.OrdinalIgnoreCase))
                continue;
            var texPrefix = appearance[..9] + skin;
            var tex = FindMatchingTexture(catalog, texPrefix);
            if (tex is not null) return $"{appearance},BODY={tex}";
        }

        if (!isChild && build.Length == 3)
            return FindFirstCategorySuit(catalog, category, genderMode, false, string.Empty, skin);
        return null;
    }

    private static string? FindMatchingTexture(CatalogCache catalog, string prefix)
    {
        foreach (var texture in catalog.Textures)
            if (texture.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return texture;
        return null;
    }

    private static string ResolveHandSuffix(CatalogCache catalog, string appearance, string skin)
    {
        var underscore = appearance.IndexOf('_');
        if (underscore >= 0 && underscore + 1 < appearance.Length)
        {
            var rawWithUnderscore = appearance[underscore..];
            var rawWithoutUnderscore = appearance[(underscore + 1)..];
            if (catalog.TextureSet.Contains("huao" + rawWithUnderscore)) return rawWithUnderscore;
            if (catalog.TextureSet.Contains("huao" + rawWithoutUnderscore)) return rawWithoutUnderscore;
        }
        return skin;
    }

    private static string ResolveHandTextureName(CatalogCache catalog, string prefix4, string suffix, string skin)
    {
        if (suffix.Length > 0 && catalog.TextureSet.Contains(prefix4 + suffix))
            return (prefix4 + suffix).ToLowerInvariant();
        return (prefix4 + skin).ToLowerInvariant();
    }

    private static void PersistContextToEntry(
        ObjectRegistry registry,
        ObjectEntry entry,
        byte language,
        InteractionBehaviorRuntimeContext context)
    {
        var stringsId = entry.Definition[ObjdField.BodyStringsId];
        if (stringsId == 0) return;
        var values = ExtractOneBasedStrings(context);
        var existingChunk = entry.File.Find(ResourceType.Str, stringsId);
        var encoded = existingChunk is not null
            ? StringTable.Read(entry.File, existingChunk).WriteLanguageValues(values, language)
            : StringTable.CreateLanguageValues(values, language);
        var updatedFile = existingChunk is not null
            ? entry.File.WithChunkDataResized(ResourceType.Str, stringsId, encoded)
            : entry.File.WithAddedChunk(ResourceType.Str, stringsId, encoded);
        registry.ReplaceEntryFile(entry.Guid, updatedFile);
    }

    private static List<string> ExtractOneBasedStrings(InteractionBehaviorRuntimeContext context)
    {
        var count = Math.Max(35, context.StringCount());
        var values = new List<string>(count);
        for (var i = 1; i <= count; i++)
            values.Add(context.StringAt(i) ?? string.Empty);
        return values;
    }

    private static void RefreshPersonCharacterFromContext(XPerson person, InteractionBehaviorRuntimeContext context)
    {
        var bodySpec = (context.StringAt(0x02) ?? string.Empty).Trim();
        if (bodySpec.Length == 0) return;

        var headSpec = (context.StringAt(0x03) ?? string.Empty).Trim();
        var leftHandSpec = (context.StringAt(0x12) ?? string.Empty).Trim();
        var rightHandSpec = (context.StringAt(0x13) ?? string.Empty).Trim();
        var genderMode = ResolveGenderMode(context, person);
        if (genderMode >= 0)
            person.AnimatorBodyStringMode = (short)genderMode;

        var character = person.SavedCharacter ?? new PersonCharacter(
            0, 0, Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray(), [], [string.Empty, string.Empty, string.Empty], 3, new float[9]);
        var parts = character.Parts.Count >= 5
            ? character.Parts.ToArray()
            : Enumerable.Repeat((string.Empty, string.Empty), 5).ToArray();

        parts[0] = ParsePartPair(bodySpec);
        if (genderMode is >= 2 and <= 5)
        {
            parts[2] = (string.Empty, string.Empty);
            parts[3] = (string.Empty, string.Empty);
            parts[4] = (string.Empty, string.Empty);
        }
        else
        {
            if (leftHandSpec.Length > 0 && !leftHandSpec.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                parts[2] = ParsePartPair(leftHandSpec);
            if (rightHandSpec.Length > 0 && !rightHandSpec.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                parts[3] = ParsePartPair(rightHandSpec);
            if (headSpec.Length > 0 && !headSpec.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                parts[4] = ParsePartPair(headSpec);
        }

        person.SavedCharacter = character with { Parts = parts };
        if (person.PresentationCharacter is { } presentation &&
            (person[PersonData.CurrentOutfit] == 0 || genderMode is >= 2 and <= 5))
        {
            var presParts = presentation.Parts.Count >= 5 ? presentation.Parts.ToArray() : parts.ToArray();
            presParts[0] = parts[0];
            presParts[2] = parts[2];
            presParts[3] = parts[3];
            if (parts[4].Item1.Length > 0 || genderMode is >= 2 and <= 5)
                presParts[4] = parts[4];
            person.PresentationCharacter = presentation with { Parts = presParts };
        }
    }

    private static (string, string) ParsePartPair(string specification)
    {
        var comma = specification.IndexOf(',');
        var appearance = (comma >= 0 ? specification[..comma] : specification).Trim();
        var binding = comma >= 0 ? specification[(comma + 1)..].Trim() : string.Empty;
        return (appearance, binding);
    }

    private static string StripAprSuffix(string value) =>
        value.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static string ExtractAppearanceName(string bodyString)
    {
        var comma = bodyString.IndexOf(',');
        var app = (comma >= 0 ? bodyString[..comma] : bodyString).Trim();
        return StripAprSuffix(app);
    }

    private static string ExtractAdultBuild(string bodyString)
    {
        var app = ExtractAppearanceName(bodyString);
        if (app.Length < 9 || FoldAscii(app[5]) != 'a') return string.Empty;
        var build = app.Substring(6, 3).ToLowerInvariant();
        return build is "skn" or "fit" or "fat" ? build : string.Empty;
    }

    private static int ResolveGenderMode(InteractionBehaviorRuntimeContext context, XPerson? person)
    {
        var raw = (context.StringAt(0x0d) ?? string.Empty).Trim().ToLowerInvariant();
        var mapped = raw switch
        {
            "male" => 0,
            "female" => 1,
            "dogmale" => 2,
            "dogfemale" => 3,
            "catmale" => 4,
            "catfemale" => 5,
            _ => -1,
        };
        if (mapped >= 0) return mapped;
        if (person is not null)
        {
            if (person.AnimatorBodyStringMode is >= 0 and <= 5 &&
                (person.AnimatorBodyStringMode > 0 || person[PersonData.Gender] == 0))
            {
                var g = person[PersonData.Gender];
                if ((g & 2) != 0) return (g & 1) != 0 ? 3 : 2;
                if ((g & 4) != 0) return (g & 1) != 0 ? 5 : 4;
                if (person.AnimatorBodyStringMode > 0) return person.AnimatorBodyStringMode;
                return g != 0 ? 1 : 0;
            }
            var gender = person[PersonData.Gender];
            if ((gender & 2) != 0) return (gender & 1) != 0 ? 3 : 2;
            if ((gender & 4) != 0) return (gender & 1) != 0 ? 5 : 4;
            return gender != 0 ? 1 : 0;
        }
        return -1;
    }

    private static bool ResolveIsChild(InteractionBehaviorRuntimeContext context, XPerson? person)
    {
        var rawAge = (context.StringAt(0x0e) ?? string.Empty).Trim();
        if (int.TryParse(rawAge, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAge) && parsedAge > 0)
            return parsedAge < 18;
        var typeToken = (context.StringAt(0x01) ?? string.Empty).Trim();
        if (typeToken.Equals("child", StringComparison.OrdinalIgnoreCase))
            return true;
        if (typeToken.Equals("adult", StringComparison.OrdinalIgnoreCase))
            return false;
        return person is not null && person[PersonData.PersonAge] is > 0 and < 18;
    }

    private static string NormalizeSkin(string? rawSkin)
    {
        var trimmed = (rawSkin ?? string.Empty).Trim().ToLowerInvariant();
        return trimmed is "lgt" or "med" or "drk" ? trimmed : trimmed;
    }

    private static ObjectEntry? ResolvePersonEntry(ObjectRegistry? registry, XPerson person) =>
        registry is not null && person.TypeRecord is { } type
            ? registry.Find(unchecked((uint)type.Key))
            : null;

    private static CatalogCache? GetCatalog(ObjectRegistry? registry, string? gameRootFallback = null)
    {
        if (registry is null) return null;
        var gameRoot = registry.GameRoot ?? gameRootFallback;
        if (gameRoot is not { Length: > 0 } || !Directory.Exists(gameRoot))
            return null;
        return Catalogs.GetValue(registry, reg => BuildCatalog(reg.GameRoot ?? gameRoot, reg));
    }

    private static CatalogCache BuildCatalog(string gameRoot, ObjectRegistry registry)
    {
        // FUN_005837c0 + FUN_0059f6cf + FUN_005a009d: load appearances from both .cmx and .bcf across Skins and Animation paths.
        var skinsPath = CharacterSearchPath.Skins(gameRoot, registry);
        var animPath = CharacterSearchPath.Animation(gameRoot, registry);
        var appearances = new List<string>();
        var appearanceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in skinsPath.Files.Concat(animPath.Files))
        {
            var isCmx = file.Extension.Equals(".cmx", StringComparison.OrdinalIgnoreCase);
            var isBcf = !isCmx && file.Extension.Equals(".bcf", StringComparison.OrdinalIgnoreCase);
            if (!isCmx && !isBcf) continue;

            CharacterData data;
            try
            {
                var bytes = file.Read();
                data = isCmx
                    ? CharacterData.ReadText(bytes, file.Source)
                    : CharacterData.ReadBinary(bytes, file.Source);
            }
            catch (Exception error) when (error is ResourceFormatException or InvalidDataException or ArgumentException)
            {
                continue;
            }

            foreach (var appearance in data.Appearances)
            {
                var name = appearance.Name.Trim();
                // FUN_004092b7 lines 20-23 skip "suit-" accessory/interaction suits.
                if (name.Length == 0 || name.StartsWith("suit-", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (appearanceSet.Add(name))
                    appearances.Add(name);
            }
        }

        // FUN_004089ca: ordered .bmp and .tga character textures.
        var texturesPath = CharacterSearchPath.Textures(gameRoot, registry);
        var textures = new List<string>();
        var textureSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in texturesPath.Files)
        {
            if (!file.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                continue;
            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            if (baseName.Length > 0 && textureSet.Add(baseName))
                textures.Add(baseName);
        }

        return new CatalogCache(appearances.ToArray(), appearanceSet, textures.ToArray(), textureSet);
    }

    private static char FoldAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
