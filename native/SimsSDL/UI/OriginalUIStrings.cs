using System.Globalization;
using System.Text;

namespace SimsSDL.UI;

public sealed class OriginalUIStrings
{
    private static readonly string[] DefaultPeoplePanelTips =
    [
        "Mood",
        "Relationships",
        "Job",
        "House",
        "Personality",
        "Interests",
        "Inventory",
        "Skills",
        "Grades",
        "Not Available to Pets",
    ];

    /// <summary>
    /// The startup splash progress sequence in <c>cSimsApp::InitApp</c> order (<c>004fbaaa</c>, table at <c>0x006ca0a4</c> into STR# 155).
    /// </summary>
    public static readonly IReadOnlyList<int> SplashProgressOrder = [3, 5, 6, 7, 8, 14, 9, 16, 10, 4, 11, 12, 13];

    private readonly IReadOnlyDictionary<ushort, IReadOnlyList<string>> _tables;
    private readonly int _languageCode;
    private readonly string? _inventoryTitleText;
    private readonly IReadOnlyList<string> _motiveLabels;
    private readonly IReadOnlyList<string> _petMotiveLabels;
    private readonly IReadOnlyList<string> _personalityStrings;
    private readonly IReadOnlyList<string> _relationshipStrings;
    private readonly IReadOnlyList<string> _petPersonalityStrings;
    private readonly IReadOnlyList<string> _peoplePanelNames;
    private readonly IReadOnlyList<string> _houseLabels;
    private readonly IReadOnlyList<string> _houseSizes;
    private readonly IReadOnlyList<string> _housePopupText;
    private readonly IReadOnlyList<string> _buildTooltips;
    private readonly IReadOnlyList<string> _interestTooltips;
    private readonly IReadOnlyList<string> _jobLabels;
    private readonly IReadOnlyList<string> _jobPopupText;
    private readonly IReadOnlyList<string> _fameStrings;
    private readonly IReadOnlyList<string> _petSkillPopupTitles;
    private readonly IReadOnlyList<string> _petSkillPopupDescriptions;
    private readonly IReadOnlyList<string> _reportCardStrings;
    private readonly string? _reportCardTitleText;
    private readonly IReadOnlyList<string> _terrainCatalogStrings;

    public OriginalUIStrings(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _languageCode = content.Language.Code;

        // The Live panel's inventory strip title is STR# 141 entry 0 of Live.iff (FUN_0051fc72), not UIText.iff's chunk 141.
        byte[]? liveData = TryReadContent(content, "GameData/Live.iff", "Live.iff");
        IReadOnlyDictionary<ushort, IReadOnlyList<string>> liveStrings = liveData is not null
            ? OriginalStringResource.TryParse(
                liveData,
                content.Language.Code,
                new HashSet<ushort>([130, 134, 141, 0x83, 0x84, 0xaa, 0x85, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8e, 0xab, 0xac, 0xb4]))?.Tables
                ?? new Dictionary<ushort, IReadOnlyList<string>>()
            : new Dictionary<ushort, IReadOnlyList<string>>();

        _inventoryTitleText = liveStrings.TryGetValue(141, out var invList) && invList.Count > 0 ? invList[0] : null;
        _motiveLabels = GetTable(liveStrings, 130);
        _petMotiveLabels = GetTable(liveStrings, 0xb4);
        _personalityStrings = GetTable(liveStrings, 0x83);
        _relationshipStrings = GetTable(liveStrings, 0x84);
        _petPersonalityStrings = GetTable(liveStrings, 0xaa);
        _peoplePanelNames = GetTable(liveStrings, 134);
        _houseLabels = GetTable(liveStrings, 0x85);
        _housePopupText = GetTable(liveStrings, 0x87);
        _houseSizes = GetTable(liveStrings, 0x8a);
        _interestTooltips = GetTable(liveStrings, 0x8c);
        _jobLabels = GetTable(liveStrings, 0x88);
        _jobPopupText = GetTable(liveStrings, 0x89);
        _petSkillPopupTitles = GetTable(liveStrings, 0xab);
        _petSkillPopupDescriptions = GetTable(liveStrings, 0xac);
        _reportCardStrings = GetTable(liveStrings, 0x8b);
        _reportCardTitleText = _reportCardStrings.Count > 0 ? _reportCardStrings[0] : null;

        byte[]? fameData = TryReadContent(content, "GameData/fame.iff", "fame.iff");
        _fameStrings = fameData is not null
            && OriginalStringResource.TryParse(fameData, content.Language.Code, new HashSet<ushort>([1]))?.Tables.TryGetValue(1, out var fameList) == true
            ? fameList
            : [];

        byte[]? buildData = TryReadContent(content, "GameData/Build.iff", "Build.iff");
        _terrainCatalogStrings = buildData is not null
            && OriginalStringResource.TryParse(buildData, content.Language.Code, new HashSet<ushort>([0x80]))?.Tables.TryGetValue(0x80, out var buildList) == true
            ? buildList
            : [];

        byte[]? uiTextData = TryReadContent(content, "GameData/UIText.iff", "UIText.iff");
        _tables = uiTextData is not null
            ? OriginalStringResource.TryParse(uiTextData, content.Language.Code)?.Tables
                ?? new Dictionary<ushort, IReadOnlyList<string>>()
            : new Dictionary<ushort, IReadOnlyList<string>>();

        _buildTooltips = GetTable(_tables, 0x8b);
    }

    /// <summary>
    /// The <c>Credits.iff</c> text blocks (STR# 163 base game, 164–171 per expansion) by chunk id.
    /// </summary>
    public static IReadOnlyDictionary<ushort, IReadOnlyList<string>> CreditBlocks(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[]? data = TryReadContent(content, "GameData/Credits.iff", "Credits.iff");
        if (data is null)
        {
            return new Dictionary<ushort, IReadOnlyList<string>>();
        }

        var chunks = new HashSet<ushort>();
        for (ushort id = 163; id <= 171; id++)
        {
            chunks.Add(id);
        }

        return OriginalStringResource.TryParse(data, content.Language.Code, chunks)?.Tables
            ?? new Dictionary<ushort, IReadOnlyList<string>>();
    }

    /// <summary>
    /// <c>cSimsApp::InitInstance</c> (<c>004ef301</c>) reads <c>UIText.iff</c> STR# 0x9a (154, MiscStrings) entry 5 for the splash copyright line.
    /// </summary>
    public string SplashCopyright() =>
        Value(154, 5, "The Sims is (c) 1999 by Maxis, Electronic Arts");

    /// <summary>
    /// The loading screen's progress line: STR# 0x9b ("splashprogress") entry for the load phase code's position in the table at <c>0x006ca0a4</c>.
    /// </summary>
    public string? SplashProgress(int position)
    {
        string text = Value(155, position, string.Empty);
        return text.Length == 0 ? null : text;
    }

    public IReadOnlyList<string> SplashProgressSequence(int startIndex = 3)
    {
        int start = 0;
        for (int i = 0; i < SplashProgressOrder.Count; i++)
        {
            if (SplashProgressOrder[i] == startIndex)
            {
                start = i;
                break;
            }
        }

        var result = new List<string>();
        for (int i = start; i < SplashProgressOrder.Count; i++)
        {
            if (SplashProgress(SplashProgressOrder[i]) is { } text && !text.StartsWith('!'))
            {
                result.Add(text);
            }
        }
        return result;
    }

    /// <summary>UIText.iff STR# 180 (0xb4, PhoneBookDialog): entry 0 "Call", entry 1 "Call", entry 2 "doesn't know anyone." (FUN_0052242d, FUN_00522be9).</summary>
    public string PhoneBookCallVerb() => Value(180, 0, "Call");
    public string PhoneBookCallButton() => Value(180, 1, "Call");
    public string PhoneBookDoesNotKnowAnyone() => Value(180, 2, "doesn't know anyone.");

    public string Camera(int index, string fallback) => Value(140, index, fallback);

    /// <summary><c>cWinPictureInPicture::Init</c> assigns UIText STR# 156 entry 0 to its window tooltip.</summary>
    public string PictureInPictureTip() => Value(156, 0, "Click to center");

    public string Album(int index, string fallback) => Value(144, index, fallback);
    public string AlbumDescription() => Value(154, 3, "No Description");

    /// <summary><c>cWinPeople::BuildPersonalityButtons</c> loads the human and pet popup tables from <c>Live.iff</c>.</summary>
    public (string Name, string Description)? PersonalityPopup(int trait, int value, bool pet)
    {
        if (trait is < 0 or >= 5)
        {
            return null;
        }

        int points = value / 100;
        int category = points < 3 ? 0 : points > 7 ? 2 : 1;
        int start = 1 + trait * 7;
        int nameIndex = start + 1 + category;
        int descriptionIndex = start + 4 + category;
        var table = pet ? _petPersonalityStrings : _personalityStrings;
        if (descriptionIndex >= table.Count)
        {
            return null;
        }

        string name = table[nameIndex];
        string description = table[descriptionIndex];
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description) ? null : (name, description);
    }

    /// <summary>The Job panel's skill button popup: <c>Live.iff</c> STR# 137 holds a name and a hint for each skill in panel order.</summary>
    public (string Title, string Description)? SkillPopup(int index)
    {
        if (index < 0 || index * 2 + 1 >= _jobPopupText.Count)
        {
            return null;
        }
        return (_jobPopupText[index * 2], _jobPopupText[index * 2 + 1]);
    }

    /// <summary><c>cWinSubpanelJob::SetupSummaryClient</c> (<c>00530bbd</c>) uses entries 12–31 of <c>Live.iff</c> STR# 0x89 for its 4 summary buttons.</summary>
    public (string Title, string Description)? CareerSummaryPopup(int index, GameplayPanelState.Career career)
    {
        ArgumentNullException.ThrowIfNull(career);

        string? LiveEntry(int entry) =>
            entry >= 0 && entry < _jobPopupText.Count && _jobPopupText[entry].Length > 0 ? _jobPopupText[entry] : null;

        if (index is < 0 or > 3)
        {
            return null;
        }

        bool hasAdultJob = career.HasJob && !career.IsSchool;
        if (index == 0)
        {
            if (!hasAdultJob)
            {
                if (LiveEntry(12) is not { } joblessTitle || LiveEntry(13) is not { } joblessDesc)
                {
                    return null;
                }
                return (joblessTitle, joblessDesc);
            }

            if (LiveEntry(23) is not { } template || career.StartHour is not { } start || career.EndHour is not { } end)
            {
                return null;
            }

            string FormatHour(int hour)
            {
                if (_languageCode is 1 or 2 or 19)
                {
                    int hour12 = hour % 12 == 0 ? 12 : hour % 12;
                    string marker = PanelTip(hour < 12 ? 21 : 22, hour < 12 ? "AM" : "PM");
                    return $"{hour12}:00 {marker}";
                }
                return $"{hour:D2}:00";
            }

            string description = template
                .Replace("$Start", FormatHour(start), StringComparison.Ordinal)
                .Replace("$End", FormatHour(end), StringComparison.Ordinal)
                .Replace("$CarTime", FormatHour((start + 23) % 24), StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(career.JobDescription))
            {
                description += "\n" + career.JobDescription;
            }

            return (career.Title ?? string.Empty, description);
        }

        if (index == 1)
        {
            if (LiveEntry(14) is not { } salaryTitle || LiveEntry(hasAdultJob ? 15 : 24) is not { } salaryDesc)
            {
                return null;
            }
            return (salaryTitle, salaryDesc);
        }

        if (index == 3)
        {
            if (LiveEntry(18) is not { } daysTitle || LiveEntry(19) is not { } daysDesc)
            {
                return null;
            }
            return (daysTitle, daysDesc);
        }

        if (LiveEntry(16) is not { } perfTitle)
        {
            return null;
        }

        int descriptionIndex;
        if (!hasAdultJob)
        {
            descriptionIndex = 22;
        }
        else
        {
            int rating = career.Performance < -60 ? 0
                : career.Performance < -30 ? 1
                : career.Performance < 30 ? 2
                : career.Performance < 60 ? 3
                : 4;
            ReadOnlySpan<int> descriptions = career.NextLevelRequirements is null
                ? [27, 28, 29, 30, 31]
                : [17, 20, 21, 25, 26];
            descriptionIndex = descriptions[rating];
        }

        if (LiveEntry(descriptionIndex) is not { } perfDesc)
        {
            return null;
        }

        return (perfTitle, perfDesc);
    }

    /// <summary>CatSkills and DogSkills load button names from <c>Live.iff</c> STR# 0xab (after entry 0 "Pet Skills") and title/description pairs from 0xac.</summary>
    public string PetSkillTitle(int index, bool cat = false, string fallback = "")
    {
        int entry = index switch
        {
            0 => cat ? 4 : 1,
            1 => 3,
            2 => 2,
            _ => -1,
        };
        if (entry < 0 || entry >= _petSkillPopupTitles.Count)
        {
            return fallback;
        }

        string candidate = _petSkillPopupTitles[entry];
        return candidate.Length > 0 ? candidate : fallback;
    }

    public (string Title, string Description)? PetSkillPopup(int index, bool cat = false)
    {
        int pair = index switch
        {
            0 => cat ? 3 : 0,
            1 => 2,
            2 => 1,
            _ => -1,
        };
        if (pair < 0)
        {
            return null;
        }

        int titleIndex = pair * 2;
        int descriptionIndex = titleIndex + 1;
        if (descriptionIndex >= _petSkillPopupDescriptions.Count)
        {
            return null;
        }

        string title = _petSkillPopupDescriptions[titleIndex];
        string description = _petSkillPopupDescriptions[descriptionIndex];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    /// <summary><c>cWinPeople::Init</c> loads <c>UIText.iff</c> STR# 165 (0xa5, SignPopups) as 12 alternating title/description pairs.</summary>
    public (string Title, string Description)? ZodiacPopup(int zodiac)
    {
        if (zodiac is < 1 or > 12 || !_tables.TryGetValue(165, out var entries))
        {
            return null;
        }

        int titleIndex = (zodiac - 1) * 2;
        int descriptionIndex = titleIndex + 1;
        if (descriptionIndex >= entries.Count)
        {
            return null;
        }

        string title = entries[titleIndex];
        string description = entries[descriptionIndex];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    public (string Title, string Description)? RelationshipPopup(GameplayPanelState.Relationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        if (_relationshipStrings.Count < 15)
        {
            return null;
        }

        int level = Math.Min(4, Math.Max(0, (Math.Clamp(relationship.Lifetime, -100, 100) + 99) / 40));
        ReadOnlySpan<int> mapping = [0, 3, 1, 4, 2];
        int index = mapping[level];
        int pair = index == 2 && relationship.Crush ? 13
            : index == 2 && relationship.Friend ? 11
            : 1 + index * 2;

        string details = relationship.Name;
        if (details.Length > 0)
        {
            int stored = relationship.Zodiac ?? 0;
            if (stored >= 0)
            {
                int zodiac = OriginalGameplayLayout.Zodiac(
                    relationship.Personality ?? new Dictionary<string, int>(),
                    stored);
                details += " (" + Sign(zodiac - 1, OriginalGameplayLayout.Signs[zodiac - 1]) + ")";
            }
            details += " -- ";
        }

        details += relationship.Description ?? string.Empty;
        return (_relationshipStrings[pair], _relationshipStrings[pair + 1] + "\n\n" + details);
    }

    public string InsufficientFunds() => Value(137, 24, "Insufficient funds");

    public string Family(int index, string fallback) => Value(129, index, fallback);
    public string FamilyCancelConfirmationTitle() => Family(7, "Cancel?");
    public string FamilyCancelConfirmationMessage() => Family(8, "Are you sure you want to cancel and lose this family?");
    public string FamilyDoneConfirmationTitle() => Family(13, "Finished?");
    public string FamilyDoneConfirmationMessage() => Family(14, "Are you sure you are finished? You cannot return to the Create A Family screen to edit this family again.");
    public string FamilyDeleteConfirmationTitle() => Family(5, "Delete Sim?");
    public string FamilyDeleteConfirmationMessage() => Family(6, "Are you sure you would like to delete this Sim?");

    public string Sim(int index, string fallback) => Value(130, index, fallback);
    public string Sign(int index, string fallback) => Value(164, index, fallback);

    public string SaveHouseTitle() => Value(153, 15, "Save House");
    public string SaveConfirmationMessage() => Value(153, 4, "Do you want to save the game?");
    public string LoadHouseTitle() => Value(153, 19, "Load House");
    public string LoadConfirmationMessage() => Value(131, 8, "Are you sure?");

    public string QuitTitle() => Value(153, 1, "Quit?");

    /// <summary>STR# 153 entry 5, the title of the save-before-quitting prompt.</summary>
    public string QuitSaveTitle() => Value(153, 5, "Quit?");

    /// <summary>STR# 153 entries 7–9, the Reset Tutorial question and its failure (FUN_004f2661).</summary>
    public string ResetTutorialTitle() => Value(153, 7, "Reset Tutorial");
    public string ResetTutorialMessage() => Value(153, 8, "Are you sure you want to reset the tutorial? The current family in that lot will be evicted.");
    public string ResetTutorialFailedMessage() => Value(153, 9, "The tutorial could not be reset.");

    public string QuitMessage() => Value(153, 2, "Are you sure you want to quit?");
    public string SaveBeforeQuitMessage() => Value(153, 6, "Do you want to save before quitting?");
    public string SaveFailedMessage() => Value(153, 14, "An error was encountered saving the house.");
    public string LoadFailedMessage() => Value(153, 18, "The house could not be loaded.");

    public string PickFamily(int index, string fallback) => Value(128, index, fallback);

    public string PurchaseHouseTitle() => Value(132, 0, "Purchase House?");
    public string PurchaseHouseMessage() => Value(132, 1, "Are you sure you want to buy this house?");

    /// <summary>STR# 225: "Please Purchase The Sims Hot Date / Vacation / … to be able to visit …".</summary>
    public string MissingPackTip(int index, string fallback) => Value(225, index, fallback);

    /// <summary>STR# 169 entry 0, the Return button tooltip on the expansion maps.</summary>
    public string ReturnTip(string fallback) => Value(169, 0, fallback);

    /// <summary><c>cWinPeople::InitRelationshipSorts</c> loads UIText STR# 240 and assigns its four rows to the family, friend, star, and all filters.</summary>
    public string RelationshipFilterTip(int index, string fallback) => Value(240, index, fallback);

    /// <summary><c>cWinPeople::InitInventorySorts</c> loads UIText STR# 241 and assigns its three rows to the magic, other, and gift sort buttons.</summary>
    public string InventorySortTip(int index, string fallback) => Value(241, index, fallback);

    /// <summary><c>cWinPeople</c>'s gift and interest panels assign STR# 154 entries 0 and 1 to their previous/next page controls.</summary>
    public string PeoplePageTip(int index, string fallback) => Value(154, index, fallback);

    /// <summary><c>cWinCatalog</c> assigns UIText STR# 210 entry 0 to its back button.</summary>
    public string CatalogBackTip(string fallback) => Value(210, 0, fallback);

    /// <summary><c>cWinCatalog</c> loads UIText STR# 150; entries 0–7 are room categories and 8–15 are function categories.</summary>
    public string CatalogCategoryTip(int index, bool room, string fallback) =>
        Value(150, (room ? 0 : 8) + index, fallback);

    /// <summary><c>cWinCatalog</c> loads STR# 200–207 for each function category's four primary sub-sort tips, then STR# 210 for shared choices.</summary>
    public string CatalogFunctionSubsortTip(int category, int index, string fallback) =>
        index switch
        {
            >= 0 and < 4 => Value((ushort)(200 + category), index, fallback),
            4 => Value(210, 3, fallback),
            5 => Value(210, 4, fallback),
            6 => Value(210, 1, fallback),
            7 => Value(210, 2, fallback),
            _ => fallback,
        };

    /// <summary><c>cWinPeople</c>'s two Mood meter segments use UIText STR# 154 entry 15.</summary>
    public string PeopleRatingTip(int index, string fallback) => Value(154, index, fallback);

    /// <summary>A control panel button's tooltip (STR# 138, VCtlTips; the Help button uses entry 23).</summary>
    public string PanelTip(int index, string fallback) => Value(138, index, fallback);

    /// <summary><c>cWinArch::Init</c> loads <c>UIText.iff</c> STR# 139 (0x8b) and assigns entries 0..11 to the build tools and 12..15 to Undo, Redo, Previous Page, Next Page (00500572).</summary>
    public string BuildToolTip(int index, string fallback)
    {
        if (index < 0 || index >= _buildTooltips.Count)
        {
            return fallback;
        }
        string entry = _buildTooltips[index];
        return string.IsNullOrWhiteSpace(entry) ? fallback : entry;
    }

    /// <summary><c>cWinRoof::Init</c> loads <c>UIText.iff</c> STR# 147 (0x93) for roof pitch button positions/tooltips and catalog popup title/description (0052921e).</summary>
    public string RoofString(int index, string fallback) => Value(147, index, fallback);

    /// <summary><c>FUN_004cd2d8</c> loads <c>Build.iff</c> STR# 128 (0x80) as 4 triples (price, name, description): Raise, Lower, Level, Grass.</summary>
    public (int Price, string Name, string Description) TerrainCatalogItem(TerrainBrushMode mode)
    {
        var (triple, fallbackLabel) = mode switch
        {
            TerrainBrushMode.Raise => (0, "Raise"),
            TerrainBrushMode.Lower => (1, "Lower"),
            TerrainBrushMode.Level => (2, "Level"),
            TerrainBrushMode.Grass => (3, "Grass"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        int baseIndex = triple * 3;
        int price = baseIndex < _terrainCatalogStrings.Count
            && int.TryParse(_terrainCatalogStrings[baseIndex].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPrice)
            ? parsedPrice
            : 1;
        string name = baseIndex + 1 < _terrainCatalogStrings.Count && _terrainCatalogStrings[baseIndex + 1].Length > 0
            ? _terrainCatalogStrings[baseIndex + 1]
            : fallbackLabel;
        string description = baseIndex + 2 < _terrainCatalogStrings.Count
            ? _terrainCatalogStrings[baseIndex + 2]
            : string.Empty;
        return (price, name, description);
    }

    /// <summary><c>cWinInterest::Init</c> loads <c>Live.iff</c> STR# 0x8c (entry 0 is panel title; entries 1..19 are interest icon tooltips).</summary>
    public string InterestTip(int index, string fallback)
    {
        int stringIndex = index + 1;
        if (stringIndex < 0 || stringIndex >= _interestTooltips.Count)
        {
            return fallback;
        }
        string entry = _interestTooltips[stringIndex];
        return string.IsNullOrWhiteSpace(entry) ? fallback : entry;
    }

    /// <summary><c>Live.iff</c> STR# 130 (human) and STR# 180 / 0xb4 (pet) pair each need's name with its popup description.</summary>
    public string? MotiveHelp(int index, bool pet = false)
    {
        int stringIndex = index * 2 + 2;
        if (stringIndex < 0)
        {
            return null;
        }
        if (pet && stringIndex < _petMotiveLabels.Count && _petMotiveLabels[stringIndex].Length > 0)
        {
            return _petMotiveLabels[stringIndex];
        }
        return stringIndex < _motiveLabels.Count && _motiveLabels[stringIndex].Length > 0 ? _motiveLabels[stringIndex] : null;
    }

    /// <summary>
    /// <c>cWinPeople::Init</c> assigns <c>Live.iff</c> STR# 134 (<c>ModeTips</c>, <c>005186da.c:205-230</c>) entries 0–6 to its seven main panel buttons.
    /// <c>SetPerson</c> (<c>0051c61b.c:46-156</c>) then replaces buttons +0x15c/+0x168/+0x16c (indices 2/5/6) according to pet and child state.
    /// </summary>
    public string PeoplePanelTip(int index, GameplayPanelState.Sim? selected, string fallback)
    {
        int stringIndex;
        if ((index == 2 || index == 5 || index == 6) && selected is not null)
        {
            if (selected.Gender is { } gender && (gender & 6) != 0)
            {
                stringIndex = index == 2 ? 7 : 9;
            }
            else if (selected.Career is { Age: > 0 and < 18 })
            {
                stringIndex = index == 2 ? 8 : index == 5 ? 5 : 6;
            }
            else
            {
                stringIndex = index == 2 ? 2 : index == 5 ? 5 : 6;
            }
        }
        else
        {
            stringIndex = index;
        }

        if (stringIndex >= 0 && stringIndex < _peoplePanelNames.Count && !string.IsNullOrWhiteSpace(_peoplePanelNames[stringIndex]))
        {
            return _peoplePanelNames[stringIndex];
        }
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }
        return stringIndex >= 0 && stringIndex < DefaultPeoplePanelTips.Length ? DefaultPeoplePanelTips[stringIndex] : fallback;
    }

    /// <summary>The budget dialog loads <c>UIText.iff</c> STR# 146 (Mac resource 0x92); these row captions are localized by the installed language.</summary>
    public string BudgetCategory(int index, string fallback) => Value(146, index, fallback);
    public string BudgetLabel(int index, string fallback) => Value(146, index, fallback);

    /// <summary>The Mac help window reads STR# 166 as alternating topic and explanation strings.</summary>
    public IReadOnlyList<(string Title, string Text)> HelpTopics
    {
        get
        {
            var entries = GetTable(_tables, 166);
            if (entries.Count == 0)
            {
                return [];
            }

            var topics = new List<(string Title, string Text)>((entries.Count + 1) / 2);
            for (int index = 0; index < entries.Count; index += 2)
            {
                topics.Add((entries[index], index + 1 < entries.Count ? entries[index + 1] : string.Empty));
            }
            return topics;
        }
    }

    public string InventoryTitle => _inventoryTitleText ?? "Inventory";

    public string NeedsTitle =>
        _motiveLabels.Count > 0 && _motiveLabels[0].Length > 0 ? _motiveLabels[0] : "Needs";

    public string RelationshipsTitle =>
        _relationshipStrings.Count > 0 && _relationshipStrings[0].Length > 0 ? _relationshipStrings[0] : "Relationships";

    public string PersonalityTitle =>
        _personalityStrings.Count > 0 && _personalityStrings[0].Length > 0 ? _personalityStrings[0] : "Personality";

    /// <summary><c>FUN_0051dfa3</c> and <c>FUN_00520bfe</c> load human (STR# 0x83) and pet (STR# 0xaa) trait button captions at index <c>1 + trait * 7</c>.</summary>
    public string PersonalityTraitLabel(int index, bool pet = false, string fallback = "")
    {
        var table = pet ? _petPersonalityStrings : _personalityStrings;
        int stringIndex = 1 + index * 7;
        return stringIndex >= 0 && stringIndex < table.Count && table[stringIndex].Length > 0 ? table[stringIndex] : fallback;
    }

    /// <summary>Sims.exe <c>FUN_005186da</c> loads <c>Live.iff</c> STR# 0x82 (human) and STR# 0xb4 (pet); motive names alternate with help text.</summary>
    public string MotiveLabel(int index, bool pet = false, string fallback = "")
    {
        int stringIndex = index * 2 + 1;
        if (stringIndex < 0)
        {
            return fallback;
        }
        if (pet && stringIndex < _petMotiveLabels.Count && _petMotiveLabels[stringIndex].Length > 0)
        {
            return _petMotiveLabels[stringIndex];
        }
        return stringIndex < _motiveLabels.Count && _motiveLabels[stringIndex].Length > 0 ? _motiveLabels[stringIndex] : fallback;
    }

    /// <summary><c>Live.iff</c> STR# 0x85 (<c>HouseSubpanelLabels</c>): House title, five rating names, then house metadata labels.</summary>
    public string HouseLabel(int index, string fallback) =>
        index >= 0 && index < _houseLabels.Count && _houseLabels[index].Length > 0 ? _houseLabels[index] : fallback;

    /// <summary><c>Live.iff</c> STR# 0x8a (<c>HouseSubpanelSize</c>): localized small, medium and large lot names.</summary>
    public string HouseLotSize(int index, string fallback) =>
        index >= 0 && index < _houseSizes.Count && _houseSizes[index].Length > 0 ? _houseSizes[index] : fallback;

    /// <summary><c>Live.iff</c> STR# 0x87 (<c>HouseSubpanelPopupText</c>): title and description pairs for the House panel hints.</summary>
    public (string Title, string Description)? HouseHint(int index)
    {
        int titleIndex = index * 2;
        if (titleIndex < 0 || titleIndex + 1 >= _housePopupText.Count)
        {
            return null;
        }

        string title = _housePopupText[titleIndex];
        string description = _housePopupText[titleIndex + 1];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    public string NeighborhoodButtonTip(int index, string fallback) => Value(151, index, fallback);

    public string BulldozeTitle() => Value(131, 0, "Bulldoze House?");
    public string BulldozeMessage() => Value(131, 1, "Do you want to bulldoze this house?");

    /// <summary>STR# 250 and 251: the rezone question and zoning type names (0 Residential, 1 Community).</summary>
    public string RezoneTitle() => Value(250, 0, "Rezone House?");
    public string RezoneMessage() => Value(250, 1, "This lot is currently zoned as a %s lot. Please choose what type of lot you want this to be:");
    public string ZoningType(int index) => Value(251, index, index == 0 ? "Residential" : "Community");

    /// <summary>STR# 131 entries 6–9: confirmations before a lot can be rezoned.</summary>
    public string RezoneEvictTitle() => Value(131, 6, "Are You Sure?");
    public string RezoneEvictMessage() => Value(131, 7, "The %s family has net worth of %s. You cannot rezone this lot until the family that is living there is evicted. Proceed with the eviction?");
    public string RezoneBulldozeTitle() => Value(131, 8, "Are you sure?");
    public string RezoneBulldozeMessage() => Value(131, 9, "You cannot rezone this lot until it has been bulldozed. Proceed with the bulldoze?");

    public string EvictTitle() => Value(131, 2, "Evict Family?");
    public string VacationEvictTitle() => Value(131, 17, "Not So Fast!");
    public string VacationEvictMessage() => Value(131, 16, "This family is currently on vacation. Strict laws forbid evictions of Sims while they are away from home.");
    public string EvictMessage() => Value(131, 3, "The %s family has net worth of %s.  Do you want to evict them?");
    public string EvictFailedMessage() => Value(131, 11, "Could not evict!");

    public string NothingToBulldozeTitle() => Value(131, 4, "Nothing to Bulldoze");
    public string NothingToBulldozeMessage() => Value(131, 5, "There is nothing here to bulldoze.");

    /// <summary>Wardrobe (STR# 220) and pet adoption (STR# 221) carousel text: 0 next, 1 previous, 2 buy, 3 cancel, 4 title.</summary>
    public string OutfitText(bool pet, int index, string fallback) =>
        Value((ushort)(pet ? 221 : 220), index, fallback);

    public string SorryTitle() => Value(132, 2, "Sorry");
    public string CannotAffordMessage() => Value(132, 3, "You can't afford it.");
    public string MoveInErrorTitle() => Value(132, 18, "Error!");
    public string MoveInErrorMessage() => Value(132, 19, "Could not move in!");
    public string CommunityLotTitle() => Value(132, 12, "Community Lot");
    public string CommunityLotMessage() => Value(132, 13, "You cannot move a family into a community lot.");
    public string HouseOccupiedTitle() => Value(132, 14, "House Occupied");
    public string HouseOccupiedMessage() => Value(132, 15, "This house is occupied by another family.");
    public string ImportFamilyTitle() => Value(143, 8, "Import Family");
    public string CorruptedFileTitle() => Value(154, 21, "Corrupted File");
    public string CorruptedFileMessage(string path) =>
        Value(154, 22, "The following file is corrupted: %s").Replace("%s", path, StringComparison.Ordinal);

    /// <summary>Neighborhood lot rollover templates from STR# 134 (NghRollover), with <c>%s</c>/<c>%d</c> placeholders.</summary>
    public string NeighborhoodRollover(int index, string fallback) => Value(134, index, fallback);

    /// <summary>Fills <c>%s</c> and <c>%d</c> placeholders in order, the way the original printf-style templates are used.</summary>
    public static string Format(string template, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);

        var sb = new StringBuilder(template.Length);
        int argIndex = 0;
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '%' && i + 1 < template.Length && (template[i + 1] == 's' || template[i + 1] == 'd'))
            {
                if (argIndex < arguments.Count)
                {
                    sb.Append(arguments[argIndex++]);
                }
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Simoleon amounts as the original UI writes them, e.g. <c>§20,000</c>.</summary>
    public static string Simoleons(int amount) =>
        "§" + amount.ToString("#,##0", CultureInfo.InvariantCulture);

    public string FatalErrorTitle() => Value(153, 21, "Fatal Error");
    public string YesButton() => Value(152, 2, "Yes");
    public string CancelButton() => Value(152, 1, "Cancel");
    public string NoButton() => Value(152, 3, "No");
    public string OkButton() => Value(152, 0, "OK");
    public string SaveMenuTitle() => Value(145, 3, "Save");
    public string LoadMenuTitle() => Value(153, 19, "Load");
    public string OptionString(int index, string fallback) => Value(145, index, fallback);

    /// <summary>Parses the original <c>"(x;y)"</c> layout strings stored in UIText tables.</summary>
    public static CGPoint? Point(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!trimmed.StartsWith('(') || !trimmed.EndsWith(')'))
        {
            return null;
        }

        string inner = trimmed[1..^1];
        int sep = inner.IndexOf(';');
        if (sep < 0 || inner.IndexOf(';', sep + 1) >= 0)
        {
            return null;
        }

        if (!int.TryParse(inner.AsSpan(0, sep).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(inner.AsSpan(sep + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            return null;
        }

        return new CGPoint(x, y);
    }

    public string SoundOptionsTitle() => Value(145, 9, "Sound Options");
    public string SoundEffectVolume() => Value(145, 44, "F/X");
    public string SoundMusicVolume() => Value(145, 47, "Music");
    public string SoundVoiceVolume() => Value(145, 50, "Vox");

    public string CareerSkill(string name)
    {
        int skillIndex = -1;
        for (int i = 0; i < OriginalGameplayLayout.Skills.Count; i++)
        {
            if (string.Equals(OriginalGameplayLayout.Skills[i], name, StringComparison.Ordinal))
            {
                skillIndex = i;
                break;
            }
        }

        if (skillIndex >= 0 && skillIndex + 1 < _jobLabels.Count && _jobLabels[skillIndex + 1].Length > 0)
        {
            return _jobLabels[skillIndex + 1];
        }

        int entry = name switch
        {
            "Cooking" => 7,
            "Mechanical" => 8,
            "Logic" => 9,
            "Body" => 10,
            "Creativity" => 11,
            "Charisma" => 12,
            _ => -1,
        };
        if (entry < 0)
        {
            return name;
        }

        string localized = Value(160, entry, "+ " + name);
        int start = 0;
        while (start < localized.Length && (localized[start] == '+' || char.IsWhiteSpace(localized[start])))
        {
            start++;
        }
        return localized[start..];
    }

    public string CareerUnemployed()
    {
        if (7 < _jobLabels.Count && _jobLabels[7].Length > 0)
        {
            return _jobLabels[7];
        }
        return Value(158, 0, "Unemployed");
    }

    public string CareerUnavailable(bool performance = false)
    {
        int index = performance ? 9 : 8;
        if (index < _jobLabels.Count && _jobLabels[index].Length > 0)
        {
            return _jobLabels[index];
        }
        return "n/a";
    }

    public string CareerNeedFriends(int count)
    {
        string template = 10 < _jobLabels.Count && _jobLabels[10].Length > 0 ? _jobLabels[10] : "Need %d";
        return Format(template, [count.ToString(CultureInfo.InvariantCulture)]);
    }

    public string CareerSchool() => Value(154, 16, "School");

    public string CareerJobLabel()
    {
        if (_jobLabels.Count > 0 && _jobLabels[0].Length > 0)
        {
            return _jobLabels[0];
        }
        return Value(166, 44, "Job");
    }

    public string PetSkillsTitle()
    {
        if (_petSkillPopupTitles.Count > 0 && _petSkillPopupTitles[0].Length > 0)
        {
            return _petSkillPopupTitles[0];
        }
        return "Pet Skills";
    }

    public string FameHeader()
    {
        if (_fameStrings.Count > 0 && _fameStrings[0].Length > 0)
        {
            return _fameStrings[0];
        }
        return "Fame";
    }

    public string FameNeedFriends(string starPower, int needed)
    {
        string template = 12 < _fameStrings.Count && _fameStrings[12].Length > 0 ? _fameStrings[12] : "%s (Need %d)";
        return Format(template, [starPower, needed.ToString(CultureInfo.InvariantCulture)]);
    }

    public (string Title, string Description)? FameLevelPopup(int level)
    {
        int clamped = Math.Clamp(level, 0, 10);
        int titleIndex = 13 + clamped * 2;
        int descIndex = titleIndex + 1;
        if (descIndex >= _fameStrings.Count)
        {
            return null;
        }

        string title = _fameStrings[titleIndex];
        string description = _fameStrings[descIndex];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    public (string Title, string Description)? FameFriendsPopup()
    {
        if (38 >= _fameStrings.Count)
        {
            return null;
        }

        string title = _fameStrings[37];
        string description = _fameStrings[38];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    public (string Title, string Description)? ReportCardPopup()
    {
        if (2 >= _reportCardStrings.Count)
        {
            return null;
        }

        string title = _reportCardStrings[1];
        string description = _reportCardStrings[2];
        return title.Length > 0 && description.Length > 0 ? (title, description) : null;
    }

    /// <summary><c>Live.iff</c> STR# 139 (0x8b) is loaded by <c>cWinSubpanelReportCard::Init</c> (<c>00532b92</c>).</summary>
    public string ReportCardLabel() => _reportCardTitleText ?? "Report Card";

    public string InterestsTitle()
    {
        if (_interestTooltips.Count > 0 && _interestTooltips[0].Length > 0)
        {
            return _interestTooltips[0];
        }
        return Value(166, 42, "Interests");
    }

    public string Control(string key) => key switch
    {
        "familyName" => Family(12, "Family name"),
        "addSim" => Family(0, "Add Sim"),
        "editSim" => Family(2, "Edit selected Sim"),
        "deleteSim" => Family(1, "Remove selected Sim"),
        "familyCancel" => Family(4, "Cancel family"),
        "familyDone" => Family(3, "Done"),
        "simName" => Sim(26, "Sim name"),
        "male" => Sim(3, "Male"),
        "female" => Sim(2, "Female"),
        "adult" => Sim(0, "Adult"),
        "child" => Sim(1, "Child"),
        "light" => Sim(6, "Light skin"),
        "medium" => Sim(5, "Medium skin"),
        "dark" => Sim(4, "Dark skin"),
        "previousHead" => Sim(9, "Previous head"),
        "nextHead" => Sim(10, "Next head"),
        "previousBody" => Sim(7, "Previous outfit"),
        "nextBody" => Sim(8, "Next outfit"),
        "personality" => Sim(16, "Personality traits"),
        "biography" => Sim(24, "Biography"),
        "simCancel" => Sim(12, "Cancel"),
        "simDone" => Sim(11, "Done"),
        _ => key,
    };

    private string Value(ushort table, int index, string fallback)
    {
        if (_tables.TryGetValue(table, out var strings) &&
            index >= 0 &&
            index < strings.Count &&
            !string.IsNullOrWhiteSpace(strings[index]))
        {
            return strings[index];
        }
        return fallback;
    }

    private static IReadOnlyList<string> GetTable(IReadOnlyDictionary<ushort, IReadOnlyList<string>> source, ushort key) =>
        source.TryGetValue(key, out var list) ? list : [];

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
}

public sealed class OriginalStringResource
{
    private static readonly Encoding Cp1252Encoding;
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<ushort> DefaultChunks =
    [
        128, 129, 130, 131, 132, 134, 137, 138, 139, 140, 143, 144, 145, 146, 147, 150, 151, 152,
        153, 154, 155, 156, 158, 160, 164, 165, 166, 169, 200, 201, 202, 203, 204, 205, 206, 207,
        210, 220, 221, 225, 240, 241, 250, 251,
    ];

    static OriginalStringResource()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252Encoding = Encoding.GetEncoding(1252);
    }

    public IReadOnlyDictionary<ushort, IReadOnlyList<string>> Tables { get; }

    public OriginalStringResource(byte[] data, int languageCode, IReadOnlySet<ushort>? chunks = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Tables = ParseTables(data, languageCode, chunks)
            ?? throw new ContentError("Invalid IFF string resource");
    }

    private OriginalStringResource(IReadOnlyDictionary<ushort, IReadOnlyList<string>> tables)
    {
        Tables = tables;
    }

    public static OriginalStringResource? TryParse(byte[] data, int languageCode, IReadOnlySet<ushort>? chunks = null)
    {
        if (data is null)
        {
            return null;
        }
        return ParseTables(data, languageCode, chunks) is { } tables
            ? new OriginalStringResource(tables)
            : null;
    }

    private static IReadOnlyDictionary<ushort, IReadOnlyList<string>>? ParseTables(
        byte[] data,
        int languageCode,
        IReadOnlySet<ushort>? chunks)
    {
        if (data.Length < 64)
        {
            return null;
        }

        int headerEnd = Array.IndexOf(data, (byte)0, 0, 60);
        if (headerEnd < 0)
        {
            headerEnd = 60;
        }

        string header = Encoding.ASCII.GetString(data, 0, headerEnd);
        if (!header.StartsWith("IFF FILE 2.", StringComparison.Ordinal))
        {
            return null;
        }

        var targetChunks = chunks ?? DefaultChunks;
        int position = 64;
        var parsed = new Dictionary<ushort, List<string>[]>();

        while (position + 76 <= data.Length)
        {
            uint chunkSizeValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
            if (chunkSizeValue < 76 || chunkSizeValue > int.MaxValue)
            {
                return null;
            }

            int chunkSize = (int)chunkSizeValue;
            if (position > data.Length - chunkSize)
            {
                return null;
            }

            string chunkType = Encoding.ASCII.GetString(data, position, 4);
            ushort chunkID = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 8, 2));
            if (string.Equals(chunkType, "STR#", StringComparison.Ordinal) && targetChunks.Contains(chunkID))
            {
                int start = position + 76;
                int end = position + chunkSize;
                var languageSets = ReadStringChunk(data, start, end, languageCode);
                if (languageSets is null)
                {
                    return null;
                }
                parsed[chunkID] = languageSets;
            }

            position += chunkSize;
        }

        byte selectedLanguage = (byte)Math.Clamp(languageCode, byte.MinValue, byte.MaxValue);
        int languageIndex = selectedLanguage is >= 1 and <= 20 ? selectedLanguage - 1 : 0;
        var selected = new Dictionary<ushort, IReadOnlyList<string>>(parsed.Count);
        foreach (var (chunkID, languageSets) in parsed)
        {
            var strings = languageIndex < languageSets.Length ? languageSets[languageIndex] : [];
            selected[chunkID] = strings.Count == 0
                ? (languageSets.Length > 0 ? languageSets[0] : Array.Empty<string>())
                : strings;
        }

        return selected;
    }

    private static List<string>[]? ReadStringChunk(byte[] data, int start, int end, int languageCode)
    {
        if (start + 2 > end)
        {
            return null;
        }

        short format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start, 2));
        int position = start + 2;

        static List<string>[] CreateEmptyLanguageSets()
        {
            var sets = new List<string>[20];
            for (int i = 0; i < sets.Length; i++)
            {
                sets[i] = [];
            }
            return sets;
        }

        if (position == end)
        {
            return CreateEmptyLanguageSets();
        }

        if (position + 2 > end)
        {
            return null;
        }

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, 2));
        position += 2;

        var languages = CreateEmptyLanguageSets();
        switch (format)
        {
            case 0:
                for (int i = 0; i < count; i++)
                {
                    if (ReadPascalString(data, ref position, end, utf8: false) is not { } value)
                    {
                        return null;
                    }
                    languages[0].Add(value);
                }
                return languages;

            case -1:
                for (int i = 0; i < count; i++)
                {
                    if (ReadCString(data, ref position, end, utf8: true) is not { } value)
                    {
                        return null;
                    }
                    languages[0].Add(value);
                }
                return languages;

            case -2:
                for (int i = 0; i < count; i++)
                {
                    if (ReadCString(data, ref position, end, utf8: false) is not { } value ||
                        ReadCString(data, ref position, end, utf8: false) is null)
                    {
                        return null;
                    }
                    languages[0].Add(value);
                }
                return languages;

            case -3:
            {
                int selectedLanguage = Math.Clamp(languageCode, 1, 20);
                int previousLanguage = -1;
                int selectedIndex = 0;
                int englishIndex = 0;
                for (int i = 0; i < count; i++)
                {
                    if (position >= end)
                    {
                        return null;
                    }

                    int language = data[position++];
                    if (ReadCString(data, ref position, end, utf8: false) is not { } value ||
                        ReadCString(data, ref position, end, utf8: false) is null)
                    {
                        return null;
                    }

                    if (language == 0)
                    {
                        language = selectedLanguage;
                    }
                    if (language != previousLanguage)
                    {
                        selectedIndex = 0;
                        englishIndex = 0;
                        previousLanguage = language;
                    }
                    if (language == selectedLanguage)
                    {
                        var target = languages[selectedLanguage - 1];
                        if (selectedIndex < target.Count)
                        {
                            target[selectedIndex] = value;
                        }
                        else
                        {
                            target.Add(value);
                        }
                        selectedIndex++;
                    }
                    if (language == 1)
                    {
                        var englishTarget = languages[0];
                        if (englishIndex < englishTarget.Count)
                        {
                            englishTarget[englishIndex] = value;
                        }
                        else
                        {
                            englishTarget.Add(value);
                        }
                        englishIndex++;
                    }
                }
                return languages;
            }

            default:
                return null;
        }
    }

    private static string? ReadPascalString(byte[] data, ref int position, int end, bool utf8)
    {
        if (position >= end)
        {
            return null;
        }

        int length = data[position++];
        if (position > end - length)
        {
            return null;
        }

        string value = DecodeString(data.AsSpan(position, length), utf8);
        position += length;
        return value;
    }

    private static string? ReadCString(byte[] data, ref int position, int end, bool utf8)
    {
        if (position > end)
        {
            return null;
        }

        int start = position;
        while (position < end && data[position] != 0)
        {
            position++;
        }
        if (position >= end)
        {
            return null;
        }

        string value = DecodeString(data.AsSpan(start, position - start), utf8);
        position++;
        return value;
    }

    private static string DecodeString(ReadOnlySpan<byte> bytes, bool utf8)
    {
        if (utf8)
        {
            try
            {
                return Utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Fall back to Windows-1252 when a -1 chunk contains non-UTF-8 legacy bytes.
            }
        }

        return Cp1252Encoding.GetString(bytes);
    }
}
