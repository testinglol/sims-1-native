using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;

internal sealed record AppearanceChoice(string Id, string Texture, string BodyType = "", string Replacement = "");
internal sealed record AppearanceCatalog(string Gender, string Age, string Skin, AppearanceChoice[] Heads, AppearanceChoice[] Bodies);

internal static class HouseholdAppearance
{
    private static readonly Dictionary<string, (Dictionary<(string Collection, string SimType), List<string>> Collections, string[] Textures)> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static AppearanceCatalog Catalog(string game, ObjectRegistry? registry, string gender, string age, string skin)
    {
        if (gender is not ("m" or "f")) throw new ArgumentException("Gender must be m or f", nameof(gender));
        if (age is not ("adult" or "child")) throw new ArgumentException("Age must be adult or child", nameof(age));
        if (skin is not ("lgt" or "med" or "drk")) throw new ArgumentException("Skin must be lgt, med or drk", nameof(skin));

        var child = age == "child";
        var simType = gender + (child ? "c" : "a");
        var bodyAvatarType = simType + (child ? "chd" : "");

        var (collections, textures) = Load(game, registry);

        IEnumerable<string> GetAppearances(string collection, params string[] simTypes)
        {
            var seenAppearances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in simTypes)
            {
                if (!collections.TryGetValue((collection, type), out var list)) continue;
                foreach (var item in list)
                    if (seenAppearances.Add(item))
                        yield return item;
            }
        }

        var headsList = child
            ? GetAppearances("c", simType, "uc")
            : GetAppearances("c", simType);
        var bodiesList = child
            ? GetAppearances("b", bodyAvatarType, "ucchd")
            : GetAppearances("b", bodyAvatarType);

        var heads = headsList
            .Where(IsHeadSuitEligible)
            .SelectMany(item => MakeChoices(item, skin, textures, false))
            .DistinctBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bodies = bodiesList
            .SelectMany(item => MakeChoices(item, skin, textures, true))
            .DistinctBy(choice => choice.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (heads.Length == 0 || bodies.Length == 0)
            throw new InvalidOperationException($"No installed head or clothing choices are available for {gender}/{age}/{skin}");

        return new AppearanceCatalog(gender, age, skin, heads, bodies);
    }

    internal static byte[] BuildCharacterBodyStrings(
        StringTable? templateTable,
        string game,
        ObjectRegistry? registry,
        bool isChild,
        bool isFemale,
        string? bodyChoice,
        string? headChoice,
        string skin)
    {
        var normalizedSkin = (skin ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "med" => "med",
            "drk" => "drk",
            _ => "lgt",
        };
        var genderCode = isFemale ? "f" : "m";
        var ageCode = isChild ? "child" : "adult";
        AppearanceCatalog? catalog = null;
        if (!string.IsNullOrWhiteSpace(game) && Directory.Exists(game))
        {
            try { catalog = Catalog(game, registry, genderCode, ageCode, normalizedSkin); }
            catch (Exception error) when (error is InvalidOperationException or DirectoryNotFoundException or IOException) { }
        }

        var values = templateTable is not null
            ? templateTable.ForLanguage(1).ToList()
            : [];
        while (values.Count < 28) values.Add(string.Empty);

        var resolvedBody = ResolveSuit(bodyChoice, catalog?.Bodies, "BODY");
        var resolvedHead = ResolveSuit(headChoice, catalog?.Heads, "HEAD-HEAD");
        if (string.IsNullOrWhiteSpace(resolvedBody))
            resolvedBody = values[1];
        if (string.IsNullOrWhiteSpace(resolvedHead))
            resolvedHead = values[2];

        var genderChar = isFemale ? 'f' : 'm';
        var handCode = isChild ? 'u' : genderChar;
        var bodyType = isChild ? "chd" : InferAdultBodyType(resolvedBody);

        void SetOneBased(int oneBasedIndex, string value) => values[oneBasedIndex - 1] = value;

        SetOneBased(1, isChild ? "child" : "adult");
        if (!string.IsNullOrEmpty(resolvedBody)) SetOneBased(2, resolvedBody);
        if (!string.IsNullOrEmpty(resolvedHead)) SetOneBased(3, resolvedHead);
        SetOneBased(13, isFemale ? "female" : "male");
        SetOneBased(14, isChild ? "9" : "27");
        SetOneBased(15, normalizedSkin);
        SetOneBased(16, isChild
            ? $"nuchd_01,BODY=nuchd{normalizedSkin}_01"
            : $"n{genderChar}{bodyType}_01,BODY=n{genderChar}{bodyType}{normalizedSkin}_01");
        SetOneBased(17, isChild
            ? $"nuchd_01,BODY=u{genderChar}chd{normalizedSkin}_{(isFemale ? "undies01" : "briefs01")}"
            : $"n{genderChar}{bodyType}_01,BODY=u{genderChar}{bodyType}{normalizedSkin}_{(isFemale ? "undies01" : "briefs01")}");
        SetOneBased(18, $"H{handCode}LO,HAND=huao{normalizedSkin}");
        SetOneBased(19, $"H{handCode}RO,HAND=huao{normalizedSkin}");
        SetOneBased(20, $"H{handCode}LP,HAND=huap{normalizedSkin}");
        SetOneBased(21, $"H{handCode}RP,HAND=huap{normalizedSkin}");
        SetOneBased(22, $"H{handCode}LC,HAND=huac{normalizedSkin}");
        SetOneBased(23, $"H{handCode}RC,HAND=huac{normalizedSkin}");
        SetOneBased(28, isChild
            ? ",BODY="
            : $"f{genderChar}{bodyType}_01,BODY=f{genderChar}{bodyType}{normalizedSkin}_01");

        return templateTable is not null
            ? templateTable.WriteLanguageValues(values, 1)
            : StringTable.CreateLanguageValues(values, 1);
    }

    internal static string? ResolveSuit(
        string? choice,
        IReadOnlyList<AppearanceChoice>? catalogChoices,
        string bindingKey)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return catalogChoices?.FirstOrDefault()?.Replacement;

        var trimmed = choice.Trim();
        if (catalogChoices is not null)
        {
            foreach (var item in catalogChoices)
                if (string.Equals(item.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                    return item.Replacement;
        }

        var pipe = trimmed.IndexOf('|');
        if (pipe > 0 && pipe < trimmed.Length - 1)
            return $"{trimmed[..pipe]},{bindingKey}={trimmed[(pipe + 1)..]}";

        return trimmed;
    }

    private static string InferAdultBodyType(string? resolvedBody)
    {
        if (string.IsNullOrWhiteSpace(resolvedBody)) return "fit";
        var comma = resolvedBody.IndexOf(',');
        var suit = (comma >= 0 ? resolvedBody[..comma] : resolvedBody).Trim();
        if (suit.EndsWith(".apr", StringComparison.OrdinalIgnoreCase)) suit = suit[..^4];
        var underscore = suit.IndexOf('_');
        var prefix = (underscore >= 0 ? suit[..underscore] : suit).ToLowerInvariant();
        if (prefix.EndsWith("lgt", StringComparison.Ordinal) ||
            prefix.EndsWith("med", StringComparison.Ordinal) ||
            prefix.EndsWith("drk", StringComparison.Ordinal))
        {
            prefix = prefix[..^3];
        }
        if (prefix.EndsWith("fat", StringComparison.Ordinal) || prefix.Contains("fat", StringComparison.Ordinal)) return "fat";
        if (prefix.EndsWith("skn", StringComparison.Ordinal) || prefix.Contains("skn", StringComparison.Ordinal)) return "skn";
        return "fit";
    }

    private static (Dictionary<(string Collection, string SimType), List<string>> Collections, string[] Textures) Load(string game, ObjectRegistry? registry)
    {
        var cacheKey = string.IsNullOrWhiteSpace(game) ? string.Empty : Path.GetFullPath(game);
        lock (Cache)
        {
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

            var skins = CharacterSearchPath.Skins(game, registry);
            var collections = new Dictionary<(string Collection, string SimType), List<string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string collection, string simType, string appearance)
            {
                var key = (collection, simType);
                if (!collections.TryGetValue(key, out var appearances)) collections.Add(key, appearances = []);
                if (!appearances.Contains(appearance, StringComparer.OrdinalIgnoreCase)) appearances.Add(appearance);
            }

            foreach (var file in skins.Files)
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
                catch (Exception error) when (error is ResourceFormatException or InvalidDataException or ArgumentException or FormatException)
                {
                    continue;
                }

                foreach (var appearance in data.Appearances)
                {
                    var item = appearance.Name.Trim().ToLowerInvariant();
                    if (item.Length < 4 || !char.IsDigit(item[1]) || !char.IsDigit(item[2]) || !char.IsDigit(item[3]) || !seen.Add(item)) continue;
                    var separator = item.IndexOf('_');
                    var type = item[4..(separator < 0 ? item.Length : separator)];
                    if (type.EndsWith("lgt", StringComparison.Ordinal)) type = type[..^3];
                    else if (type.EndsWith("med", StringComparison.Ordinal) || type.EndsWith("drk", StringComparison.Ordinal)) type = type[..^3];
                    Add(item[0].ToString(), type, item);
                    if (item[0] == 'b' && (type.EndsWith("fat", StringComparison.Ordinal) || type.EndsWith("fit", StringComparison.Ordinal) || type.EndsWith("skn", StringComparison.Ordinal)))
                        Add("b", type[..^3], item);
                }
            }

            var textures = CharacterSearchPath.Textures(game, registry).Files
                .Where(file => file.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                               file.Extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileNameWithoutExtension(file.Name))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var loaded = (collections, textures);
            Cache[cacheKey] = loaded;
            return loaded;
        }
    }

    internal static IEnumerable<AppearanceChoice> MakeChoices(string item, string skin, IReadOnlyList<string> textures, bool body)
    {
        var prefix = ExtractID(item);
        var underscore = item.IndexOf('_');
        if (body && underscore < 3) yield break;
        var bodyType = body ? item.Substring(underscore - 3, 3).ToLowerInvariant() : "";
        foreach (var texture in textures.Where(name => ContainsRequiredStringsInOrder(name, prefix, skin)))
        {
            var id = item + "|" + texture;
            if (!body) yield return new AppearanceChoice(id, texture, Replacement: $"{item},HEAD-HEAD={texture}");
            else yield return new AppearanceChoice(id, texture, bodyType, $"{item},BODY={texture}");
        }
    }

    internal static bool ContainsRequiredStringsInOrder(string value, string first, string second)
    {
        var firstIndex = FindAsciiCaseInsensitive(value, first, 0);
        if (firstIndex < 0) return false;
        var secondIndex = FindAsciiCaseInsensitive(value, second, firstIndex + first.Length);
        return secondIndex >= 0;
    }

    private static int FindAsciiCaseInsensitive(string value, string required, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index <= value.Length - required.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < required.Length; offset++)
            {
                if (AsciiLower(value[index + offset]) == AsciiLower(required[offset])) continue;
                matched = false;
                break;
            }
            if (matched) return index;
        }
        return -1;
    }

    private static char AsciiLower(char value) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private static string ExtractID(string item)
    {
        var underscore = item.IndexOf('_');
        return underscore < 0 ? item : item[..underscore];
    }

    internal static bool IsHeadSuitEligible(string item) => item.Length < 2 || item[1] is not ('t' or 'T');
}
