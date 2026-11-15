using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimsSDL;

namespace SimsSDL.UI;

public enum BannerAction
{
    SelectFamily,
    Bulldoze,
    Quit,
    Internet,
    Credits,
    Previous,
    Next,
    ImportFamily,
    Downtown,
    SimsLogo,
    Vacation,
    Rezone,
    StudioTown,
    MagicTown,
    /// <summary>Returns from an expansion map to the base neighborhood (Res_Nbhd 5209).</summary>
    ReturnHome
}

public static class BannerActionExtensions
{
    private static readonly BannerAction[] AllBannerActions = Enum.GetValues<BannerAction>();

    public static IReadOnlyList<BannerAction> AllCases => AllBannerActions;

    /// <summary>
    /// The buttons the original creates on a map of the given lot-location mode (1 is the base neighborhood).
    /// </summary>
    public static IReadOnlyList<BannerAction> ActionsForMapMode(int mode)
    {
        if (mode == 1)
            return AllBannerActions.Where(static a => a != BannerAction.ReturnHome).ToArray();

        var list = new List<BannerAction>
        {
            BannerAction.ReturnHome,
            BannerAction.Internet,
            BannerAction.SimsLogo,
            BannerAction.ImportFamily,
            BannerAction.Bulldoze
        };
        if (mode == 7)
            list.Add(BannerAction.SelectFamily);
        return list;
    }

    public static string Resource(this BannerAction action, int mapMode) => action switch
    {
        BannerAction.SelectFamily => "UIGraphics/NghUI/MoveIn.bmp",
        BannerAction.Bulldoze => mapMode == 1 ? "UIGraphics/NghUI/Bulldoze.bmp" : "UIGraphics/Downtown/Bulldoze.bmp",
        BannerAction.Quit => "UIGraphics/NghUI/Exit.bmp",
        BannerAction.Internet => "UIGraphics/NghUI/InetBtn.bmp",
        BannerAction.Credits => "UIGraphics/Nbhd/CreditBtn.BMP",
        BannerAction.Previous => "UIGraphics/NghUI/Previous.bmp",
        BannerAction.Next => "UIGraphics/NghUI/Next.bmp",
        BannerAction.ImportFamily => mapMode switch
        {
            2 => "UIGraphics/Downtown/Import.bmp",
            3 => "UIGraphics/VIsland/Import.bmp",
            5 => "UIGraphics/Studiotown/Import.bmp",
            7 => "UIGraphics/Magicland/Import.bmp",
            _ => "UIGraphics/NghUI/Import.bmp"
        },
        BannerAction.Downtown => "UIGraphics/NghUI/Downtown.bmp",
        BannerAction.SimsLogo => "UIGraphics/Downtown/TheSimsLogo.bmp",
        BannerAction.Vacation => "UIGraphics/NghUI/Vacation.bmp",
        BannerAction.Rezone => "UIGraphics/NghUI/Rezone.bmp",
        BannerAction.StudioTown => "UIGraphics/NghUI/Studiotown.bmp",
        BannerAction.MagicTown => "UIGraphics/NghUI/Magicland.bmp",
        BannerAction.ReturnHome => "UIGraphics/Downtown/Return.bmp",
        _ => "UIGraphics/NghUI/MoveIn.bmp"
    };

    public static CGPoint Position(this BannerAction action, int mapMode) => action switch
    {
        BannerAction.SelectFamily => new CGPoint(200, 0),
        BannerAction.Bulldoze => new CGPoint(mapMode == 1 ? 300 : 373, 0),
        BannerAction.Quit => new CGPoint(600, 0),
        BannerAction.Internet => new CGPoint(762, 0),
        BannerAction.Credits => new CGPoint(195, 540),
        BannerAction.Previous => new CGPoint(106, 0),
        BannerAction.Next => new CGPoint(141, 0),
        BannerAction.ImportFamily => new CGPoint(mapMode == 1 ? 250 : 422, 0),
        BannerAction.Downtown => new CGPoint(400, 0),
        BannerAction.SimsLogo => new CGPoint(5, 0),
        BannerAction.Vacation => new CGPoint(450, 0),
        BannerAction.Rezone => new CGPoint(350, 0),
        BannerAction.StudioTown => new CGPoint(500, 0),
        BannerAction.MagicTown => new CGPoint(550, 0),
        BannerAction.ReturnHome => new CGPoint(323, 0),
        _ => CGPoint.Zero
    };

    /// <summary>Frames side by side in the button sheet.</summary>
    public static int FrameCount(this BannerAction action) => action == BannerAction.SimsLogo ? 1 : 4;

    /// <summary>STR# 151 (<c>NghbBtnTips</c>) tooltip index.</summary>
    public static int TooltipIndex(this BannerAction action) => action switch
    {
        BannerAction.SelectFamily => 0,
        BannerAction.Bulldoze => 1,
        BannerAction.Quit => 2,
        BannerAction.Internet => 3,
        BannerAction.Previous => 4,
        BannerAction.Next => 5,
        BannerAction.ImportFamily => 6,
        BannerAction.Downtown => 7,
        BannerAction.Credits or BannerAction.SimsLogo => 8,
        BannerAction.Vacation => 9,
        BannerAction.Rezone => 10,
        BannerAction.StudioTown => 11,
        BannerAction.MagicTown => 12,
        BannerAction.ReturnHome => 0,
        _ => 0
    };

    private static readonly string[] FallbackTooltips =
    [
        "Select or Create Family",
        "Evict or Bulldoze",
        "Quit",
        "View Web Pages",
        "Switch To Previous Neighborhood",
        "Switch To Next Neighborhood",
        "Import Family",
        "Switch to Downtown View",
        "Credits",
        "Switch to Vacation Island View",
        "Evict or Rezone",
        "Switch to Studio Town View",
        "Switch to Magic Town View"
    ];

    public static string FallbackTooltip(this BannerAction action) =>
        action == BannerAction.ReturnHome
            ? "Return to Neighborhood View"
            : FallbackTooltips[action.TooltipIndex()];

    /// <summary>The expansion map a travel button opens (UserData lot-location modes), if it is one.</summary>
    public static int? MapMode(this BannerAction action) => action switch
    {
        BannerAction.Downtown => 2,
        BannerAction.Vacation => 3,
        BannerAction.StudioTown => 5,
        BannerAction.MagicTown => 7,
        _ => null
    };

    /// <summary>The expansion folder whose presence enables a travel button (Hot Date, Vacation, Superstar, Makin' Magic).</summary>
    public static string? RequiredPackFolder(this BannerAction action) => action switch
    {
        BannerAction.Downtown => "ExpansionPack3",
        BannerAction.Vacation => "ExpansionPack4",
        BannerAction.StudioTown => "ExpansionPack6",
        BannerAction.MagicTown => "ExpansionPack7",
        _ => null
    };

    /// <summary>STR# 225 "please purchase" tooltip index shown when the pack is missing.</summary>
    public static int? MissingPackTooltipIndex(this BannerAction action) => action switch
    {
        BannerAction.Downtown => 0,
        BannerAction.Vacation => 1,
        BannerAction.StudioTown => 3,
        BannerAction.MagicTown => 4,
        _ => null
    };

    public static bool IsTravelButton(this BannerAction action) => action.MapMode() is not null;
}

public sealed class OriginalNeighborhoodOverlay
{
    public sealed record Manifest
    {
        public sealed record AnimationLayer
        {
            [JsonPropertyName("positionX")]
            public float PositionX { get; init; }

            [JsonPropertyName("positionY")]
            public float PositionY { get; init; }

            [JsonPropertyName("frameResources")]
            public IReadOnlyList<string> FrameResources { get; init; } = [];
        }

        public sealed record Entry
        {
            public sealed record ScrapbookPhotoSource
            {
                [JsonPropertyName("image")]
                public string Image { get; init; } = string.Empty;

                [JsonPropertyName("thumbnail")]
                public string? Thumbnail { get; init; }

                [JsonPropertyName("caption")]
                public string Caption { get; init; } = string.Empty;
            }

            public sealed record HouseRenderScenes
            {
                [JsonPropertyName("thumbnail")]
                public string Thumbnail { get; init; } = string.Empty;

                [JsonPropertyName("exterior")]
                public string Exterior { get; init; } = string.Empty;

                [JsonPropertyName("floor1")]
                public string Floor1 { get; init; } = string.Empty;

                [JsonPropertyName("floor2")]
                public string? Floor2 { get; init; }
            }

            [JsonPropertyName("house")]
            public int House { get; init; }

            [JsonPropertyName("x")]
            public float X { get; init; }

            [JsonPropertyName("y")]
            public float Y { get; init; }

            [JsonPropertyName("closedImagePath")]
            public string ClosedImagePath { get; init; } = string.Empty;

            [JsonPropertyName("openImagePath")]
            public string OpenImagePath { get; init; } = string.Empty;

            [JsonPropertyName("closedImageWidth")]
            public int ClosedImageWidth { get; init; }

            [JsonPropertyName("closedImageHeight")]
            public int ClosedImageHeight { get; init; }

            [JsonPropertyName("openImageWidth")]
            public int OpenImageWidth { get; init; }

            [JsonPropertyName("openImageHeight")]
            public int OpenImageHeight { get; init; }

            [JsonPropertyName("thumbWidth")]
            public int ThumbWidth { get; init; }

            [JsonPropertyName("thumbHeight")]
            public int ThumbHeight { get; init; }

            [JsonPropertyName("xOff")]
            public int XOff { get; init; }

            [JsonPropertyName("baseYOff")]
            public int BaseYOff { get; init; }

            [JsonPropertyName("addYOff")]
            public int AddYOff { get; init; }

            [JsonPropertyName("occupied")]
            public bool Occupied { get; init; }

            [JsonPropertyName("residential")]
            public bool Residential { get; init; }

            [JsonPropertyName("availableForCreation")]
            public bool AvailableForCreation { get; init; }

            [JsonPropertyName("price")]
            public int? Price { get; init; }

            [JsonPropertyName("community")]
            public bool? Community { get; init; }

            [JsonPropertyName("filterFlags")]
            public int? FilterFlags { get; init; }

            [JsonPropertyName("houseName")]
            public string? HouseName { get; init; }

            [JsonPropertyName("houseDescription")]
            public string? HouseDescription { get; init; }

            [JsonPropertyName("street")]
            public int? Street { get; init; }

            [JsonPropertyName("streetTemplate")]
            public string? StreetTemplate { get; init; }

            [JsonPropertyName("familyName")]
            public string? FamilyName { get; init; }

            [JsonPropertyName("familyNetWorth")]
            public int? FamilyNetWorth { get; init; }

            [JsonPropertyName("familyCashBalance")]
            public int? FamilyCashBalance { get; init; }

            [JsonPropertyName("familyFriends")]
            public int? FamilyFriends { get; init; }

            [JsonPropertyName("scrapbookPhotos")]
            public IReadOnlyList<ScrapbookPhotoSource>? ScrapbookPhotos { get; init; }

            [JsonPropertyName("bulldozed")]
            public bool? Bulldozed { get; init; }

            [JsonPropertyName("tutorialHouse")]
            public bool? TutorialHouse { get; init; }

            [JsonPropertyName("lotHasHouse")]
            public bool? LotHasHouse { get; init; }

            [JsonPropertyName("popupName")]
            public string? PopupName { get; init; }

            [JsonPropertyName("popupDescription")]
            public string? PopupDescription { get; init; }

            /// <summary>Each member's character-file thumbnail (BMP_ 2004), or null where it has none.</summary>
            [JsonPropertyName("memberImages")]
            public IReadOnlyList<string?>? MemberImages { get; init; }

            [JsonPropertyName("memberNames")]
            public IReadOnlyList<string>? MemberNames { get; init; }

            [JsonPropertyName("memberProfiles")]
            public IReadOnlyList<NeighborhoodWebExport.MemberProfile?>? MemberProfiles { get; init; }

            [JsonPropertyName("houseSquareFeet")]
            public int? HouseSquareFeet { get; init; }

            [JsonPropertyName("houseBathrooms")]
            public int? HouseBathrooms { get; init; }

            [JsonPropertyName("houseBedrooms")]
            public int? HouseBedrooms { get; init; }

            [JsonPropertyName("houseFloors")]
            public int? HouseFloors { get; init; }

            [JsonPropertyName("houseValue")]
            public int? HouseValue { get; init; }

            [JsonPropertyName("houseSizeRating")]
            public int? HouseSizeRating { get; init; }

            [JsonPropertyName("houseFurnishingsRating")]
            public int? HouseFurnishingsRating { get; init; }

            [JsonPropertyName("houseYardRating")]
            public int? HouseYardRating { get; init; }

            [JsonPropertyName("houseUpkeepRating")]
            public int? HouseUpkeepRating { get; init; }

            [JsonPropertyName("houseLayoutRating")]
            public int? HouseLayoutRating { get; init; }

            [JsonPropertyName("renderScenes")]
            public HouseRenderScenes? RenderScenes { get; init; }

            [JsonPropertyName("onVacation")]
            public bool? OnVacation { get; init; }
        }

        public sealed record Import
        {
            [JsonPropertyName("file")]
            public string File { get; init; } = string.Empty;

            [JsonPropertyName("familyName")]
            public string FamilyName { get; init; } = string.Empty;

            [JsonPropertyName("house")]
            public int House { get; init; }

            [JsonPropertyName("members")]
            public IReadOnlyList<string> Members { get; init; } = [];
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("mode")]
        public int Mode { get; init; }

        [JsonPropertyName("backgroundResource")]
        public string BackgroundResource { get; init; } = string.Empty;

        [JsonPropertyName("canvasWidth")]
        public int CanvasWidth { get; init; }

        [JsonPropertyName("canvasHeight")]
        public int CanvasHeight { get; init; }

        [JsonPropertyName("scale")]
        public float Scale { get; init; }

        [JsonPropertyName("pulsate")]
        public bool Pulsate { get; init; }

        [JsonPropertyName("frameDuration")]
        public int FrameDuration { get; init; }

        [JsonPropertyName("music")]
        public string? Music { get; init; }

        [JsonPropertyName("ambientSound")]
        public string? AmbientSound { get; init; }

        [JsonPropertyName("animationLayers")]
        public IReadOnlyList<AnimationLayer> AnimationLayers { get; init; } = [];

        [JsonPropertyName("entries")]
        public IReadOnlyList<Entry> Entries { get; init; } = [];

        [JsonPropertyName("families")]
        public IReadOnlyList<OriginalFamilyPickerOverlay.Family>? Families { get; init; }

        /// <summary>
        /// <c>.FAM</c> files waiting in the profile's <c>Import</c> folder, and whether the Import button is enabled
        /// (it also counts <c>*.rdt</c> and <c>*.cmt</c> files, as the original does).
        /// </summary>
        [JsonPropertyName("imports")]
        public IReadOnlyList<Import>? Imports { get; init; }

        [JsonPropertyName("importEnabled")]
        public bool? ImportEnabled { get; init; }

        /// <summary>The Unleashed destination picker's saved filter (STR# 6 string 3 of LotLocations, cWinNeighborhoodUL::LoadCurrentFilter).</summary>
        [JsonPropertyName("travelFilter")]
        public int? TravelFilter { get; init; }

        [JsonPropertyName("backgroundImagePath")]
        public string? BackgroundImagePath { get; init; }

        /// <summary>The numbered neighborhood (<c>UserData</c> is 1) and every neighborhood installed in the profile.</summary>
        [JsonPropertyName("neighborhood")]
        public int? Neighborhood { get; init; }

        [JsonPropertyName("neighborhoods")]
        public IReadOnlyList<int>? Neighborhoods { get; init; }

        /// <summary>Display names pack towns give themselves, by neighborhood number.</summary>
        [JsonPropertyName("neighborhoodNames")]
        public Dictionary<string, string>? NeighborhoodNames { get; init; }
    }

    public readonly record struct DrawItem
    {
        public OriginalGameplayOverlay.Image Image { get; init; }
        public CGRect Rect { get; init; }
        public float Opacity { get; init; }
        public Vector4 UV { get; init; }

        public DrawItem(
            OriginalGameplayOverlay.Image image,
            CGRect rect,
            float opacity = 1f,
            Vector4? uv = null)
        {
            Image = image;
            Rect = rect;
            Opacity = opacity;
            UV = uv ?? new Vector4(0f, 0f, 1f, 1f);
        }
    }

    private sealed record House(
        Manifest.Entry Entry,
        OriginalGameplayOverlay.Image Closed,
        OriginalGameplayOverlay.Image Open,
        CGRect HitRect,
        /// <summary>One slot per family member; a member without a thumbnail keeps its place and draws nothing.</summary>
        IReadOnlyList<SimsBitmap?> Members);

    private sealed record Animation(
        CGPoint Position,
        IReadOnlyList<OriginalGameplayOverlay.Image> Frames);

    /// <summary>Funds a newly created family starts with (original <c>money_for_new_family</c> tuning).</summary>
    public const int NewFamilyFunds = 20_000;

    private static readonly IReadOnlyDictionary<int, string> RolloverFallbacks = new Dictionary<int, string>
    {
        [0] = "The %s Family\r\nNet Worth: %s\r\nFriends: %d",
        [1] = "%s\r\n%s\r\nCost: %s\r\nPick or create a family and move them in to play.",
        [2] = "%s\r\n%s\r\nCost: %s\r\nThe %s family only has %s.  They can't afford this house.",
        [3] = "%s\r\n%s\r\nCost: %s\r\nThis could be the house of your dreams!  If you buy, the family will have %s to spend on household items.",
        [4] = "%s\r\n%s\r\nCost: %s\r\nThe %s family only has %s.  They can't afford this lot.",
        [5] = "%s\r\n%s\r\nCost: %s\r\nThis could be the lot of your dreams!  If you buy this lot, the family will have %s to spend to build a house and buy household items.",
        [6] = "To learn how to play the game...",
        [8] = "Can't move in to the tutorial house!",
        [9] = "The %s Family\r\nNet Worth: %s\r\nFriends: %d\r\nClick to evict this family.",
        [10] = "Click to demolish this house.",
        [11] = "There is no one to evict and nothing to demolish.",
        [17] = "%s\r\n%s\r\nThis is a community lot.",
        [18] = "%s\r\n%s\r\nYou cannot move in here. It is not a residential lot. ",
        [12] = "You cannot bulldoze the tutorial house until after you do the tutorial.",
        [19] = "%s\r\n%s\r\nClick to rezone this lot.",
        [20] = "%s\r\n%s\r\nThis is a Residential lot. You cannot visit here.",
        [21] = "%s\r\n%s\r\nThis is a Community lot. Please click here to visit.",
        [22] = "No Name",
        [23] = "No Description"
    };

    public OriginalGameplayOverlay.Image Background { get; }
    private readonly OriginalGameplayOverlay.Image _outerFill;
    private readonly OriginalGameplayOverlay.Image? _largeBackground;
    private readonly OriginalGameplayOverlay.Image _frameShadow;
    private readonly OriginalGameplayOverlay.Image _frameHighlight;
    public int Mode { get; }
    public int Neighborhood { get; }
    private readonly Dictionary<int, string> _townNames;
    public IReadOnlyList<int> Neighborhoods { get; }
    private readonly UIDevice _device;
    private readonly string _sessionRoot;
    public string? Music { get; }
    public string? AmbientSound { get; }
    private readonly House[] _houses;
    private readonly IReadOnlyList<Manifest.Entry> _entries;

    /// <summary>Unhoused families offered by the Select a Family screen (base neighborhood only).</summary>
    public IReadOnlyList<OriginalFamilyPickerOverlay.Family> Families { get; }

    /// <summary>Families the Import button offers, in the host's order (its <c>import:INDEX</c> edit counts in this order).</summary>
    public IReadOnlyList<Manifest.Import> Imports { get; }

    private readonly bool _importEnabled;
    private readonly int _savedTravelFilter;
    public int? HoveredHouse { get; private set; }
    private readonly OriginalGameplayOverlay.Image? _vacationMark;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly float _houseScale;
    private readonly bool _pulsate;
    private readonly double _frameDuration;
    private readonly Animation[] _animations;
    private readonly double _startedAt = MonotonicNowSeconds();
    private readonly OriginalUIStrings _strings;
    private readonly SimsBitmapFont? _font;
    private readonly OriginalGameplayOverlay.Image? _neighborhoodNumberImage;
    private readonly SimsTarga? _rolloverTiler;
    private readonly OriginalGameplayOverlay.Image? _rolloverBackground;
    private readonly OriginalGameplayOverlay.Image? _banner;
    private NeighborhoodLotFilter? _travelFilter;
    private int? _hoveredTravelFilter;
    private readonly OriginalGameplayOverlay.Image? _filterToolbar;
    private readonly List<OriginalGameplayOverlay.Image> _filterButtons = [];
    private readonly Dictionary<int, OriginalGameplayOverlay.Image> _filterMarkers = [];
    private readonly Dictionary<BannerAction, OriginalGameplayOverlay.Image> _bannerButtons;
    private readonly Dictionary<BannerAction, string> _bannerTooltips;
    private readonly HashSet<BannerAction> _installedBannerActions;
    public BannerAction? HoveredBanner { get; private set; }
    private (int House, OriginalGameplayOverlay.Image Image, CGSize Size)? _rolloverCache;

    /// <summary>The tooltip under the banner button the pointer rests on (any button, enabled or not), with its text drawn once.</summary>
    private (BannerAction Action, string Text, OriginalGameplayOverlay.Image Image)? _bannerTip;
    private BannerAction? _hoveredTipAction;
    private double? _bannerTipStartedAt;
    private bool _bannerTipQuickTips = true;

    public bool EvictMode { get; private set; }
    public OriginalFamilyPickerOverlay.Family? MovingInFamily { get; private set; }
    public bool RezoneMode { get; private set; }

    public OriginalNeighborhoodOverlay(
        GameContent content,
        UIDevice device,
        string manifestPath,
        string sessionPath,
        SimsBitmapFont? font = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(manifestPath);
        ArgumentNullException.ThrowIfNull(sessionPath);

        _strings = new OriginalUIStrings(content);
        _font = font;
        _device = device;

        if (font is null)
        {
            _rolloverTiler = null;
            _rolloverBackground = null;
        }
        else
        {
            // cWinLotPopup (0054fcb8) 9-slices HotSpotPopupTiler.tga into offscreen RGB+alpha surfaces and composites text + BMP_ 2004 heads onto it.
            var tiler = new SimsTarga(content.Read("UIGraphics/Nbhd/HotSpotPopupTiler.tga"));
            _rolloverTiler = tiler;
            _rolloverBackground = OriginalGameplayOverlay.CreateImage(device, tiler.Width, tiler.Height, tiler.Rgba);
        }

        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
            throw new FileNotFoundException("Neighborhood manifest not found", manifestPath);
        if (manifestInfo.Length > 4 * 1024 * 1024)
            throw new ContentError("Neighborhood manifest exceeds 4 MiB");

        byte[] manifestData = File.ReadAllBytes(manifestPath);
        if (manifestData.Length > 4 * 1024 * 1024)
            throw new ContentError("Neighborhood manifest exceeds 4 MiB");

        var manifest = JsonSerializer.Deserialize<Manifest>(manifestData)
            ?? throw new ContentError("Neighborhood manifest decoded to null");

        OriginalGameplayOverlay.Image BannerImage(string path)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

        OriginalGameplayOverlay.Image? TryBannerImage(string path)
        {
            try
            {
                return BannerImage(path);
            }
            catch
            {
                return null;
            }
        }

        // The base map has the full banner; expansion maps show the Downtown-style banner with a Return button.
        if (manifest.Mode == 4)
        {
            _filterToolbar = TryBannerImage("UIGraphics/NghUI/filter_toolbar_Unleashed.bmp");
            string[] filterNames = ["DogCat", "SmallAnimal", "Shopping", "Gardening", "Food", "Recreation", "Lodging"];
            foreach (string name in filterNames)
            {
                if (TryBannerImage($"UIGraphics/NghUI/Filter{name}Btn.bmp") is { } buttonImg)
                    _filterButtons.Add(buttonImg);
            }

            (int Mask, string Name)[] markerSpecs =
            [
                (16, "filterdogcat"),
                (32, "filtersmallanimal"),
                (8, "filtershopping"),
                (4, "filtergardening"),
                (2, "filterfood"),
                (64, "filterpark"),
                (1, "filterlodging")
            ];
            foreach (var (mask, name) in markerSpecs)
            {
                if (TryBannerImage($"UIGraphics/CPanel/{name}.bmp") is { } markerImg)
                    _filterMarkers[mask] = markerImg;
            }
        }

        bool baseMap = manifest.Mode == 1;
        _banner = BannerImage(baseMap ? "UIGraphics/NghUI/banner_neighborhood.bmp" : "UIGraphics/NghUI/banner_downtown.bmp");
        // A highlighted vacation lot shows Res_Nbhd kNghVacationLogo (5441), or kVacationNoVacancy (5510) on Vacation Island.
        _vacationMark = TryBannerImage(manifest.Mode == 3
            ? "UIGraphics/VIsland/no_vacancy.bmp"
            : "UIGraphics/VIsland/vacationlogosmall.bmp");

        var uiStrings = new OriginalUIStrings(content);
        var actions = BannerActionExtensions.ActionsForMapMode(manifest.Mode);
        var sheets = new Dictionary<BannerAction, OriginalGameplayOverlay.Image>();
        var tips = new Dictionary<BannerAction, string>();
        var installed = new HashSet<BannerAction>();

        foreach (var action in actions)
        {
            sheets[action] = BannerImage(action.Resource(manifest.Mode));
            bool present = true;
            if (action.RequiredPackFolder() is { } packFolder)
            {
                string packPath = Path.Combine(content.Root, packFolder);
                present = Directory.Exists(packPath) || File.Exists(packPath);
            }
            if (present)
                installed.Add(action);

            if (!present && action.MissingPackTooltipIndex() is int missingIdx)
            {
                tips[action] = uiStrings.MissingPackTip(missingIdx, "Please purchase the expansion pack to use this");
            }
            else
            {
                tips[action] = action == BannerAction.ReturnHome
                    ? uiStrings.ReturnTip(action.FallbackTooltip())
                    : uiStrings.NeighborhoodButtonTip(action.TooltipIndex(), action.FallbackTooltip());
            }
        }

        _bannerButtons = sheets;
        _bannerTooltips = tips;
        _installedBannerActions = installed;

        if (manifest.SchemaVersion != 2 || manifest.Mode <= 0 || manifest.CanvasWidth != 800 || manifest.CanvasHeight != 600
            || !float.IsFinite(manifest.Scale) || manifest.Scale <= 0 || manifest.Scale > 4
            || manifest.Entries.Count > 256 || manifest.FrameDuration <= 0 || manifest.FrameDuration > 600
            || manifest.AnimationLayers.Count > 16
            || manifest.Entries.Select(static e => e.House).Distinct().Count() != manifest.Entries.Count)
        {
            throw new ContentError("Invalid neighborhood manifest");
        }

        string root = LotScene.ResolveRealPath(sessionPath);
        _sessionRoot = root;

        byte[] backgroundData;
        if (manifest.BackgroundImagePath is { } bgPath)
        {
            string resolvedBg = ResolveSessionFile(bgPath)
                ?? throw new ContentError("Plugin background escapes session");
            backgroundData = File.ReadAllBytes(resolvedBg);
        }
        else
        {
            backgroundData = content.Read(manifest.BackgroundResource);
        }

        var backgroundBitmap = new SimsBitmap(backgroundData);
        if (backgroundBitmap.Width != manifest.CanvasWidth || backgroundBitmap.Height != manifest.CanvasHeight)
            throw new ContentError("Neighborhood background dimensions differ from its manifest");

        byte[] backgroundPixels = (byte[])backgroundBitmap.Rgba.Clone();
        var animations = new Animation[manifest.AnimationLayers.Count];
        for (int layerIdx = 0; layerIdx < manifest.AnimationLayers.Count; layerIdx++)
        {
            var layer = manifest.AnimationLayers[layerIdx];
            if (!float.IsFinite(layer.PositionX) || !float.IsFinite(layer.PositionY)
                || layer.FrameResources.Count == 0 || layer.FrameResources.Count > 32)
            {
                throw new ContentError("Invalid neighborhood animation layer");
            }

            int ox = (int)Math.Round(layer.PositionX, MidpointRounding.AwayFromZero);
            int oy = (int)Math.Round(layer.PositionY, MidpointRounding.AwayFromZero);
            bool firstFrameComposited = false;
            var frames = new OriginalGameplayOverlay.Image[layer.FrameResources.Count];
            for (int frameIdx = 0; frameIdx < layer.FrameResources.Count; frameIdx++)
            {
                var bitmap = new SimsBitmap(content.Read(layer.FrameResources[frameIdx]));
                if (bitmap.Width > 4096 || bitmap.Height > 4096)
                    throw new ContentError("Neighborhood animation frame exceeds 4096 pixels");

                if (!firstFrameComposited)
                {
                    firstFrameComposited = true;
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        int dy = oy + y;
                        if (dy < 0 || dy >= backgroundBitmap.Height)
                            continue;
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            int dx = ox + x;
                            if (dx < 0 || dx >= backgroundBitmap.Width)
                                continue;
                            int src = (y * bitmap.Width + x) * 4;
                            byte r = bitmap.Rgba[src], g = bitmap.Rgba[src + 1], b = bitmap.Rgba[src + 2];
                            if (r >= 180 && g <= 70 && b >= 180)
                                continue;
                            int dst = (dy * backgroundBitmap.Width + dx) * 4;
                            backgroundPixels[dst] = r;
                            backgroundPixels[dst + 1] = g;
                            backgroundPixels[dst + 2] = b;
                            backgroundPixels[dst + 3] = 255;
                        }
                    }
                }

                frames[frameIdx] = OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
            }

            animations[layerIdx] = new Animation(new CGPoint(layer.PositionX, layer.PositionY), frames);
        }
        _animations = animations;

        _outerFill = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 50, 0, 255]);
        // 0055e843.c:161-162, 00556bc9.c:149, 0054a15f.c:162, 00568a84.c:175, 00564927.c:123 load resource 0x1389 (5001, largeback.bmp)
        // behind the centered 800x600 map whenever the screen exceeds 800px wide.
        string[] largeBackCandidates = manifest.Mode switch
        {
            5 => ["UIGraphics/Studiotown/largeback.bmp", "UIGraphics/Downtown/largeback.bmp", "UIGraphics/Magicland/largeback.bmp"],
            7 => ["UIGraphics/Magicland/largeback.bmp", "UIGraphics/Downtown/largeback.bmp", "UIGraphics/Studiotown/largeback.bmp"],
            _ => ["UIGraphics/Downtown/largeback.bmp", "UIGraphics/Studiotown/largeback.bmp", "UIGraphics/Magicland/largeback.bmp"]
        };

        OriginalGameplayOverlay.Image? loadedLargeBack = null;
        foreach (string path in largeBackCandidates)
        {
            try
            {
                var bmp = new SimsBitmap(content.Read(path));
                loadedLargeBack = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba);
                break;
            }
            catch
            {
                // Try next largeback candidate.
            }
        }
        _largeBackground = loadedLargeBack;

        _frameShadow = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 6, 48, 255]);
        _frameHighlight = OriginalGameplayOverlay.CreateImage(device, 1, 1, [74, 90, 148, 255]);
        Background = OriginalGameplayOverlay.CreateImage(device, backgroundBitmap.Width, backgroundBitmap.Height, backgroundPixels);

        Mode = manifest.Mode;
        Neighborhood = manifest.Neighborhood ?? 1;

        if (manifest.Mode == 1)
        {
            OriginalGameplayOverlay.Image? numImg = null;
            foreach (string fontPath in new[] { "UIGraphics/Fonts/variablesans_11.ffn", "UIGraphics/Fonts/variablesans_10.ffn" })
            {
                try
                {
                    var numberFont = new SimsBitmapFont(content.Read(fontPath));
                    numImg = OriginalGameplayOverlay.Text(
                        Neighborhood.ToString(CultureInfo.InvariantCulture),
                        numberFont,
                        device,
                        (255, 255, 255));
                    break;
                }
                catch
                {
                    // Fall back to smaller font if unavailable.
                }
            }
            _neighborhoodNumberImage = numImg;
        }
        else
        {
            _neighborhoodNumberImage = null;
        }

        Neighborhoods = (manifest.Neighborhoods ?? [Neighborhood])
            .Where(static n => n is >= 1 and <= 99)
            .ToArray();

        var townNames = new Dictionary<int, string>();
        if (manifest.NeighborhoodNames is not null)
        {
            foreach (var (key, value) in manifest.NeighborhoodNames)
            {
                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) && value is not null)
                    townNames[num] = value.Length > 40 ? value[..40] : value;
            }
        }
        _townNames = townNames;

        _entries = manifest.Entries;
        Families = manifest.Families ?? [];
        Imports = (manifest.Imports ?? []).Take(256).ToArray();
        _importEnabled = manifest.ImportEnabled ?? false;
        _savedTravelFilter = manifest.TravelFilter ?? 2;
        _canvasWidth = manifest.CanvasWidth;
        _canvasHeight = manifest.CanvasHeight;
        _houseScale = manifest.Scale;
        _pulsate = manifest.Pulsate;
        _frameDuration = manifest.FrameDuration / 60.0;
        Music = manifest.Music;
        AmbientSound = manifest.AmbientSound;

        var houses = new House[manifest.Entries.Count];
        for (int entryIdx = 0; entryIdx < manifest.Entries.Count; entryIdx++)
        {
            var entry = manifest.Entries[entryIdx];
            if (entry.House is < 0 or > 100
                || !float.IsFinite(entry.X) || !float.IsFinite(entry.Y)
                || entry.ClosedImageWidth <= 0 || entry.ClosedImageHeight <= 0
                || entry.OpenImageWidth <= 0 || entry.OpenImageHeight <= 0
                || entry.ClosedImageWidth > 2048 || entry.ClosedImageHeight > 2048
                || entry.OpenImageWidth > 2048 || entry.OpenImageHeight > 2048)
            {
                throw new ContentError("Invalid neighborhood house entry");
            }

            var closedBitmap = ReadHouseBitmap(entry.ClosedImagePath, entry.ClosedImageWidth, entry.ClosedImageHeight);
            var openBitmap = ReadHouseBitmap(entry.OpenImagePath, entry.OpenImageWidth, entry.OpenImageHeight);
            var closed = OriginalGameplayOverlay.CreateImage(device, closedBitmap.Width, closedBitmap.Height, closedBitmap.Rgba, magenta: true);
            var open = OriginalGameplayOverlay.CreateImage(device, openBitmap.Width, openBitmap.Height, openBitmap.Rgba, magenta: true);

            double width = entry.ClosedImageWidth / (double)manifest.Scale;
            double height = entry.ClosedImageHeight / (double)manifest.Scale;
            var hitRect = new CGRect(entry.X - width / 2.0, entry.Y - height / 2.0, width, height);

            var memberImages = entry.MemberImages ?? [];
            int memberCount = Math.Min(8, memberImages.Count);
            var members = new SimsBitmap?[memberCount];
            for (int m = 0; m < memberCount; m++)
            {
                if (memberImages[m] is not { } memberPath)
                    continue;
                try
                {
                    members[m] = ReadHouseBitmap(memberPath, width: null, height: null);
                }
                catch
                {
                    members[m] = null;
                }
            }

            houses[entryIdx] = new House(entry, closed, open, hitRect, members);
        }
        _houses = houses;
    }

    /// <summary>
    /// The neighborhood after <paramref name="current"/> in the installed list, wrapping at either end like the original
    /// Previous/Next buttons (<c>0x0042a368</c> previous, <c>0x0042a3ca</c> next).
    /// </summary>
    public static int? NeighborhoodAfter(int current, IReadOnlyList<int> installed, bool forward)
    {
        ArgumentNullException.ThrowIfNull(installed);
        int[] sorted = installed.Distinct().OrderBy(static x => x).ToArray();
        if (sorted.Length <= 1)
            return null;

        int index = Array.IndexOf(sorted, current);
        if (index < 0)
            return sorted[0];

        return sorted[(index + (forward ? 1 : sorted.Length - 1)) % sorted.Length];
    }

    public static int? ModeForDialogKind(byte kind) => NeighborhoodTravel.ModeForDialogKind(kind);

    private string? ResolveSessionFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string candidate = Path.IsPathRooted(path)
            ? path
            : Path.Combine(_sessionRoot, path);
        string resolved = LotScene.ResolveRealPath(candidate);
        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootPrefix = _sessionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, cmp))
            return null;

        var info = new FileInfo(resolved);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        return resolved;
    }

    private SimsBitmap ReadHouseBitmap(string path, int? width, int? height)
    {
        string file = ResolveSessionFile(path)
            ?? throw new ContentError("Neighborhood house image escapes its session or exceeds 4 MiB");
        var info = new FileInfo(file);
        if (info.Length > 4 * 1024 * 1024)
            throw new ContentError("Neighborhood house image escapes its session or exceeds 4 MiB");

        var bitmap = new SimsBitmap(File.ReadAllBytes(file));
        bool validWidth = width is int expectedW ? bitmap.Width == expectedW : bitmap.Width <= 64;
        bool validHeight = height is int expectedH ? bitmap.Height == expectedH : bitmap.Height <= 64;
        if (!validWidth || !validHeight)
            throw new ContentError("Neighborhood house image dimensions differ from its manifest");

        return bitmap;
    }

    private (double Left, double Top, double Factor) Transform(CGSize canvas, float uiScale = 1f)
    {
        double w = _canvasWidth;
        double h = _canvasHeight;
        double cap = Math.Max(0.25, uiScale);
        double factor = Math.Max(0.001, Math.Min(cap, Math.Min(canvas.Width / w, canvas.Height / h)));
        double left = (canvas.Width - w * factor) / 2.0;
        double top = (canvas.Height - h * factor) / 2.0;
        return (left, top, factor);
    }

    private CGPoint Logical(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        return new CGPoint((point.X - left) / factor, (point.Y - top) / factor);
    }

    public void UpdateHover(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var logical = Logical(point, canvas, uiScale);
        var bounds = new CGRect(0, 0, _canvasWidth, _canvasHeight);
        if (!bounds.Contains(logical))
        {
            HoveredHouse = null;
            return;
        }

        int? bestHouse = null;
        double bestDistSq = double.PositiveInfinity;
        foreach (var house in _houses)
        {
            if (!house.HitRect.Contains(logical))
                continue;
            double dx = logical.X - house.HitRect.MidX;
            double dy = logical.Y - house.HitRect.MidY;
            double distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestHouse = house.Entry.House;
            }
        }
        HoveredHouse = bestHouse;
    }

    /// <summary>The Unleashed destination picker uses one category at a time.</summary>
    public void EnableTravelFilter()
    {
        if (Mode != 4 || _filterToolbar is null || _filterButtons.Count != 7)
            return;

        int[] communityFlags = _entries
            .Where(static e => e.Community == true)
            .Select(static e => e.FilterFlags ?? 0)
            .ToArray();
        _travelFilter = new NeighborhoodLotFilter(communityFlags, _savedTravelFilter);
    }

    /// <summary>The filter mode cWinNeighborhoodUL::Shutdown passes to SaveCurrentFilter.</summary>
    public int? TravelFilterMode => _travelFilter?.SelectedMask;

    public bool ClickTravelFilter(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        if (_travelFilter is null || TravelFilterIndex(point, canvas, uiScale) is not int index)
            return false;

        _travelFilter.Select(index);
        return true;
    }

    private int? TravelFilterIndex(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        if (_travelFilter is null)
            return null;

        var logical = Logical(point, canvas, uiScale);
        var positions = NeighborhoodLotFilter.Positions;
        for (int i = 0; i < positions.Count; i++)
        {
            if (new CGRect(positions[i], 20, 50, 52).Contains(logical))
                return i;
        }
        return null;
    }

    public int? HouseAt(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        UpdateHover(point, canvas, uiScale);
        return HoveredHouse;
    }

    public bool CanCreateHousehold(int house) =>
        (Mode == 1 || Mode == 7) && FindEntry(house)?.AvailableForCreation == true;

    /// <summary>Whether the family living on a lot is away on vacation, which blocks evicting it (0x00552c63).</summary>
    public bool IsOnVacation(int house) =>
        FindEntry(house)?.OnVacation == true;

    /// <summary>The family living on a lot, shaped like a picker row, for eviction prompts.</summary>
    public OriginalFamilyPickerOverlay.Family? Occupant(int house)
    {
        if (FindEntry(house) is not { Occupied: true, Residential: true } entry)
            return null;

        return new OriginalFamilyPickerOverlay.Family(
            Id: 0,
            Name: entry.FamilyName ?? string.Empty,
            Budget: 0,
            NetWorth: entry.FamilyNetWorth ?? 0,
            Friends: entry.FamilyFriends ?? 0,
            Members: [],
            MemberProfiles: entry.MemberProfiles,
            MemberThumbnails: ThumbnailData(entry.MemberImages));
    }

    /// <summary>While active, the Bulldoze/Evict banner button is drawn pressed and the next lot click proposes an eviction.</summary>
    public void SetEvictMode(bool value)
    {
        EvictMode = value;
        _rolloverCache = null;
    }

    /// <summary>A bin family being moved in: lot rollovers show its Move-in entries.</summary>
    public void SetMovingInFamily(OriginalFamilyPickerOverlay.Family? value)
    {
        MovingInFamily = value;
        _rolloverCache = null;
    }

    /// <summary>While active, the Rezone banner button is drawn pressed and the next lot click starts a rezoning.</summary>
    public void SetRezoneMode(bool value)
    {
        RezoneMode = value;
        _rolloverCache = null;
    }

    /// <summary>Whether a lot's house is already bulldozed (no house on the buildable area), which the original game requires before rezoning.</summary>
    public bool IsBulldozed(int house) => FindEntry(house)?.Bulldozed == true;

    public bool HasLot(int house) => FindEntry(house) is not null;

    /// <summary>The lot's current zoning as the original STR# 251 names it: 0 Residential, 1 Community.</summary>
    public int ZoningIndex(int house) => FindEntry(house)?.Residential == true ? 0 : 1;

    /// <summary>The lot's <c>Sims.exe</c> <c>StateClass</c> (<c>1</c> residential, <c>2</c> community), or <c>null</c> if absent from the map.</summary>
    public int? StateClass(int house)
    {
        if (FindEntry(house) is not { } entry)
            return null;
        return entry.Community == true ? 2 : entry.Residential ? 1 : null;
    }

    public int? Price(int house) => FindEntry(house)?.Price;

    /// <summary>Vacant residential lots on the base or Magic Town map, in house order.</summary>
    public IReadOnlyList<int> VacantResidentialLots =>
        (Mode == 1 || Mode == 7)
            ? _entries.Where(static e => e.Residential && !e.Occupied).Select(static e => e.House).OrderBy(static h => h).ToArray()
            : [];

    public bool IsVacantResidential(int house) =>
        (Mode == 1 || Mode == 7) && FindEntry(house) is { Residential: true, Occupied: false };

    private Manifest.Entry? FindEntry(int house)
    {
        foreach (var entry in _entries)
        {
            if (entry.House == house)
                return entry;
        }
        return null;
    }

    /// <summary>
    /// The original rollover text for a lot: the STR# 134 entry Mac <c>cWinLotPopup::GetPopupIndex</c> picks, formatted as
    /// <c>cWinLotPopup::SetHouseInfo</c> does. The travel picker is the Old Town map built as <c>cWinNeighborhoodUL(false)</c>,
    /// whose lot mode is Visit.
    /// </summary>
    public string? RolloverText(int house)
    {
        if (FindEntry(house) is not { } entry)
            return null;

        LotPopupIndex.Zoning zoning = entry.Community == true
            ? LotPopupIndex.Zoning.Community
            : entry.Residential
                ? LotPopupIndex.Zoning.Residential
                : LotPopupIndex.Zoning.Unlisted;

        LotPopupIndex.Mode popupMode = _travelFilter is not null
            ? LotPopupIndex.Mode.Visit
            : EvictMode
                ? LotPopupIndex.Mode.Evict
                : RezoneMode
                    ? LotPopupIndex.Mode.Rezone
                    : MovingInFamily is not null
                        ? LotPopupIndex.Mode.MoveIn
                        : LotPopupIndex.Mode.Browse;

        int price = entry.Price ?? 0;
        bool canAffordLot = false;
        if (MovingInFamily is { } family)
        {
            if (price <= family.Budget)
            {
                if (entry.House is >= 90 and <= 92)
                {
                    int requiredMagiCoins = entry.House == 91 ? 150 : 100;
                    canAffordLot = (family.MagiCoins ?? 0) >= requiredMagiCoins;
                }
                else
                {
                    canAffordLot = true;
                }
            }
        }

        int index = LotPopupIndex.Index(
            popupMode,
            zoning,
            entry.TutorialHouse == true,
            entry.Occupied,
            entry.LotHasHouse == true,
            canAffordLot);

        string Template(int idx) =>
            _strings.NeighborhoodRollover(idx, RolloverFallbacks.GetValueOrDefault(idx, string.Empty));

        var lot = new LotPopupText.Lot(
            entry.House,
            zoning,
            OriginalUIStrings.Format(StreetTemplate(entry), [entry.House.ToString(CultureInfo.InvariantCulture)]),
            entry.PopupName ?? Template(22),
            entry.PopupDescription ?? Template(23),
            OriginalUIStrings.Simoleons(price),
            entry.FamilyName ?? string.Empty,
            OriginalUIStrings.Simoleons(entry.FamilyNetWorth ?? 0),
            entry.FamilyFriends ?? 0);

        LotPopupText.MovingIn? movingIn = MovingInFamily is { } fam
            ? new LotPopupText.MovingIn(
                fam.Name,
                OriginalUIStrings.Simoleons(fam.Budget),
                OriginalUIStrings.Simoleons(fam.Budget - price))
            : null;

        return LotPopupText.Text(
            index,
            popupMode == LotPopupIndex.Mode.Visit,
            lot,
            movingIn,
            Template,
            static (tpl, args) => OriginalUIStrings.Format(tpl, [.. args]));
    }

    /// <summary>
    /// The address template, chosen by house number as the original's rollover (<c>0x0054fcb8</c>) does: pack lot ranges use
    /// their STR# 134 lines, and other lots use StreetNames.iff STR# 2000 at their STR# 2001 index, else STR# 134 entry 7.
    /// </summary>
    private string StreetTemplate(Manifest.Entry entry)
    {
        int house = entry.House;
        int? index = house switch
        {
            >= 21 and <= 31 => house < 24 ? 13 : 14,
            >= 40 and <= 49 => house < 43 ? 15 : 16,
            >= 81 and <= 89 => house < 84 ? 24 : 25,
            >= 93 and <= 99 => 27,
            _ => null
        };

        if (index is int idx)
        {
            // Downtown and Vacation Island lots first use the street from Downtown.iff or Vacation.iff, which the host exports.
            if ((house is >= 21 and <= 31 || house is >= 40 and <= 49) && entry.StreetTemplate is { } template)
                return template;
            return _strings.NeighborhoodRollover(idx, "%s Sim Lane");
        }

        return entry.StreetTemplate ?? _strings.NeighborhoodRollover(7, "%s Sim Lane");
    }

    /// <summary>
    /// cWinLotPopup::SetHouseInfo (0054fcb8) composites 9-sliced HotSpotPopupTiler.tga, wrapped white text, and 25x25
    /// BMP_ 2004 thumbnails into one offscreen surface (d4) and cWinLotPopup::Draw (0054fbc7 -> 00498cab) alpha-blends
    /// that single surface with fade - 0x145 = -79 (center alpha 255 - 79 = 176).
    /// </summary>
    private (OriginalGameplayOverlay.Image Image, CGSize Size)? RolloverComposite(int houseNumber)
    {
        if (_rolloverCache is { } cached && cached.House == houseNumber)
            return (cached.Image, cached.Size);

        if (_font is not { } font || _rolloverTiler is not { } tiler)
            return null;

        House? house = null;
        foreach (var candidate in _houses)
        {
            if (candidate.Entry.House == houseNumber)
            {
                house = candidate;
                break;
            }
        }
        if (house is null || RolloverText(houseNumber) is not { } text)
            return null;

        int memberCount = house.Entry.Occupied ? Math.Min(8, house.Members.Count) : 0;
        var layout = new NeighborhoodRolloverLayout(
            text,
            font.LineHeight,
            memberCount,
            font.TextWidth,
            (paragraph, width) =>
            {
                try
                {
                    return font.WrappedLines(paragraph, width);
                }
                catch
                {
                    return [paragraph];
                }
            });

        int boxW = layout.Width;
        int boxH = layout.Height;
        if (layout.Lines.Count == 0 || boxW <= 0 || boxH <= 0)
            return null;

        byte[] rgba = new byte[boxW * boxH * 4];
        int srcEdgeX = Math.Max(1, tiler.Width / 3);
        int srcEdgeY = Math.Max(1, tiler.Height / 3);
        int dstEdgeX = Math.Min(srcEdgeX, boxW / 2);
        int dstEdgeY = Math.Min(srcEdgeY, boxH / 2);
        int midDstW = Math.Max(1, boxW - dstEdgeX * 2);
        int midSrcW = Math.Max(1, tiler.Width - srcEdgeX * 2);
        int midDstH = Math.Max(1, boxH - dstEdgeY * 2);
        int midSrcH = Math.Max(1, tiler.Height - srcEdgeY * 2);

        for (int dy = 0; dy < boxH; dy++)
        {
            int sy = dy < dstEdgeY
                ? dy
                : dy >= boxH - dstEdgeY
                    ? tiler.Height - (boxH - dy)
                    : srcEdgeY + (dy - dstEdgeY) * midSrcH / midDstH;

            for (int dx = 0; dx < boxW; dx++)
            {
                int sx = dx < dstEdgeX
                    ? dx
                    : dx >= boxW - dstEdgeX
                        ? tiler.Width - (boxW - dx)
                        : srcEdgeX + (dx - dstEdgeX) * midSrcW / midDstW;

                int src = (sy * tiler.Width + sx) * 4;
                int dst = (dy * boxW + dx) * 4;
                int rawA = tiler.Rgba[src + 3] - 79;
                rgba[dst] = tiler.Rgba[src];
                rgba[dst + 1] = tiler.Rgba[src + 1];
                rgba[dst + 2] = tiler.Rgba[src + 2];
                rgba[dst + 3] = rawA < 8 ? (byte)0 : (byte)Math.Min(255, rawA);
            }
        }

        const int inset = NeighborhoodRolloverLayout.Inset;
        for (int lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
        {
            (int Width, int Height, byte[] Rgba) raster;
            try
            {
                raster = font.Rasterize(layout.Lines[lineIndex], 255, 255, 255);
            }
            catch
            {
                continue;
            }

            int baseX = inset - 3;
            int baseY = inset - 3 + lineIndex * font.LineHeight;
            for (int sy = 0; sy < raster.Height; sy++)
            {
                int dy = baseY + sy;
                if (dy < 0 || dy >= boxH)
                    continue;
                for (int sx = 0; sx < raster.Width; sx++)
                {
                    int dx = baseX + sx;
                    if (dx < 0 || dx >= boxW)
                        continue;
                    int src = (sy * raster.Width + sx) * 4;
                    int cov = raster.Rgba[src + 3];
                    if (cov <= 0)
                        continue;
                    int dst = (dy * boxW + dx) * 4;
                    rgba[dst] = (byte)((rgba[dst] * (255 - cov) + raster.Rgba[src] * cov + 127) / 255);
                    rgba[dst + 1] = (byte)((rgba[dst + 1] * (255 - cov) + raster.Rgba[src + 1] * cov + 127) / 255);
                    rgba[dst + 2] = (byte)((rgba[dst + 2] * (255 - cov) + raster.Rgba[src + 2] * cov + 127) / 255);
                }
            }
        }

        int headPairs = Math.Min(layout.Heads.Count, house.Members.Count);
        for (int i = 0; i < headPairs; i++)
        {
            if (house.Members[i] is not { } bitmap)
                continue;
            var head = layout.Heads[i];
            int copyW = Math.Min(head.Width, bitmap.Width);
            int copyH = Math.Min(head.Height, bitmap.Height);
            for (int sy = 0; sy < copyH; sy++)
            {
                int dy = head.Y + sy;
                if (dy < 0 || dy >= boxH)
                    continue;
                for (int sx = 0; sx < copyW; sx++)
                {
                    int dx = head.X + sx;
                    if (dx < 0 || dx >= boxW)
                        continue;
                    int src = (sy * bitmap.Width + sx) * 4;
                    byte r = bitmap.Rgba[src], g = bitmap.Rgba[src + 1], b = bitmap.Rgba[src + 2];
                    if (r >= 180 && g <= 70 && b >= 180)
                        continue;
                    int dst = (dy * boxW + dx) * 4;
                    rgba[dst] = r;
                    rgba[dst + 1] = g;
                    rgba[dst + 2] = b;
                }
            }
        }

        OriginalGameplayOverlay.Image image;
        try
        {
            image = OriginalGameplayOverlay.CreateImage(_device, boxW, boxH, rgba);
        }
        catch
        {
            return null;
        }

        var size = new CGSize(boxW, boxH);
        _rolloverCache = (houseNumber, image, size);
        return (image, size);
    }

    /// <summary>The housed and unhoused families on the map, for the Web page export.</summary>
    public IReadOnlyList<NeighborhoodWebExport.Family> WebFamilies(
        IReadOnlyDictionary<int, NeighborhoodWebExport.HouseViews>? houseViews = null)
    {
        var result = new List<NeighborhoodWebExport.Family>();
        foreach (var entry in _entries)
        {
            if (!entry.Occupied || !entry.Residential || string.IsNullOrEmpty(entry.FamilyName))
                continue;

            NeighborhoodWebExport.HouseViews? views = null;
            houseViews?.TryGetValue(entry.House, out views);
            result.Add(new NeighborhoodWebExport.Family(
                name: entry.FamilyName,
                lot: entry.House,
                street: entry.Street ?? 0,
                members: entry.MemberImages?.Count ?? 0,
                friends: entry.FamilyFriends ?? 0,
                cashBalance: entry.FamilyCashBalance,
                memberNames: entry.MemberNames ?? [],
                memberThumbnails: ThumbnailData(entry.MemberImages),
                memberProfiles: entry.MemberProfiles ?? [],
                scrapbookPhotos: ScrapbookPhotos(entry.ScrapbookPhotos),
                houseViews: views,
                houseSquareFeet: entry.HouseSquareFeet,
                houseBathrooms: entry.HouseBathrooms,
                houseBedrooms: entry.HouseBedrooms,
                houseFloors: entry.HouseFloors,
                houseValue: entry.HouseValue,
                houseSizeRating: entry.HouseSizeRating,
                houseFurnishingsRating: entry.HouseFurnishingsRating,
                houseYardRating: entry.HouseYardRating,
                houseUpkeepRating: entry.HouseUpkeepRating,
                houseLayoutRating: entry.HouseLayoutRating));
        }

        foreach (var fam in Families)
        {
            result.Add(new NeighborhoodWebExport.Family(
                name: fam.Name,
                lot: 0,
                street: 0,
                members: fam.Members.Count,
                friends: fam.Friends,
                memberNames: fam.Members,
                memberProfiles: fam.MemberProfiles ?? []));
        }

        return result;
    }

    public IReadOnlyList<(int House, string Thumbnail, string Exterior, string Floor1, string? Floor2)> WebHouseRenderRequests
    {
        get
        {
            var requests = new List<(int, string, string, string, string?)>();
            foreach (var entry in _entries)
            {
                if (!entry.Occupied || !entry.Residential || entry.RenderScenes is not { } scenes)
                    continue;
                if (ResolveSessionFile(scenes.Thumbnail) is not { } thumbnail
                    || ResolveSessionFile(scenes.Exterior) is not { } exterior
                    || ResolveSessionFile(scenes.Floor1) is not { } floor1)
                {
                    continue;
                }

                string? floor2 = scenes.Floor2 is not null ? ResolveSessionFile(scenes.Floor2) : null;
                requests.Add((entry.House, thumbnail, exterior, floor1, floor2));
            }
            return requests;
        }
    }

    private IReadOnlyList<byte[]?> ThumbnailData(IReadOnlyList<string?>? paths)
    {
        if (paths is null || paths.Count == 0)
            return [];

        var result = new byte[]?[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            if (paths[i] is not { } path)
                continue;
            if (ResolveSessionFile(path) is not { } file)
                continue;
            try
            {
                result[i] = File.ReadAllBytes(file);
            }
            catch
            {
                result[i] = null;
            }
        }
        return result;
    }

    private IReadOnlyList<NeighborhoodWebExport.ScrapbookPhoto> ScrapbookPhotos(
        IReadOnlyList<Manifest.Entry.ScrapbookPhotoSource>? sources)
    {
        if (sources is null || sources.Count == 0)
            return [];

        var result = new NeighborhoodWebExport.ScrapbookPhoto[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            result[i] = new NeighborhoodWebExport.ScrapbookPhoto(
                WebImageData(source.Image),
                source.Thumbnail is not null ? WebImageData(source.Thumbnail) : null,
                source.Caption);
        }
        return result;
    }

    private byte[]? WebImageData(string path)
    {
        if (ResolveSessionFile(path) is not { } file)
            return null;
        var info = new FileInfo(file);
        if (info.Length > 32 * 1024 * 1024)
            return null;
        try
        {
            return File.ReadAllBytes(file);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a banner button works: Quit, Evict/Bulldoze and Return always do; Import needs a file in the Import folder;
    /// travel buttons need their expansion installed.
    /// </summary>
    public bool IsEnabled(BannerAction action) => action switch
    {
        BannerAction.Quit or BannerAction.Bulldoze or BannerAction.ReturnHome or BannerAction.SimsLogo => true,
        BannerAction.ImportFamily => _importEnabled,
        BannerAction.SelectFamily => Mode == 1 || Mode == 7,
        BannerAction.Credits => Mode == 1,
        BannerAction.Internet => Mode is 1 or 2 or 3 or 5,
        BannerAction.Previous or BannerAction.Next => Mode == 1 && Neighborhoods.Distinct().Count() > 1,
        BannerAction.Downtown or BannerAction.Vacation or BannerAction.StudioTown or BannerAction.MagicTown =>
            _installedBannerActions.Contains(action),
        BannerAction.Rezone => true,
        _ => false
    };

    private CGRect BannerRect(BannerAction action, CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        if (!_bannerButtons.TryGetValue(action, out var sheet))
            return CGRect.Zero;

        var position = action.Position(Mode);
        return new CGRect(
            left + position.X * factor,
            top + position.Y * factor,
            sheet.Size.Width / action.FrameCount() * factor,
            sheet.Size.Height * factor);
    }

    public BannerAction? BannerActionAt(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        // Sims.exe wires both CreditBtn (index 4) and TheSimsLogo (index 9 on base/UL, index 2 on sub-hoods) to cWinCredits
        // (005607f3.c:79-80, 005591ff.c:82-83, 0054b9fa.c:25, 0056a5f4.c:35, 00565c9a.c:35, 00552fa9.c:26).
        foreach (var action in BannerActionExtensions.AllCases)
        {
            if (_bannerButtons.ContainsKey(action) && BannerRect(action, canvas, uiScale).Contains(point))
                return action;
        }
        return null;
    }

    public void UpdateBannerHover(CGPoint point, CGSize canvas, bool quickTips, float uiScale = 1f)
    {
        _hoveredTravelFilter = TravelFilterIndex(point, canvas, uiScale);
        var hit = BannerActionAt(point, canvas, uiScale);
        HoveredBanner = hit is { } a && IsEnabled(a) ? a : null;
        _hoveredTipAction = hit;

        if (_hoveredTipAction is { } action
            && BannerTooltip(point, canvas, uiScale) is { } text
            && _font is { } font)
        {
            if (_bannerTip is not { } existing || existing.Action != action || !string.Equals(existing.Text, text, StringComparison.Ordinal))
            {
                _bannerTipStartedAt = null;
                try
                {
                    var raster = font.Rasterize(text, 255, 255, 255);
                    var image = OriginalGameplayOverlay.CreateImage(_device, raster.Width, raster.Height, raster.Rgba);
                    _bannerTip = (action, text, image);
                }
                catch
                {
                    _bannerTip = null;
                }
            }
        }
        else
        {
            _bannerTip = null;
            _bannerTipStartedAt = null;
        }

        if (_bannerTipQuickTips != quickTips)
        {
            _bannerTipStartedAt = null;
            _bannerTipQuickTips = quickTips;
        }
    }

    public string? BannerTooltip(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        if (BannerActionAt(point, canvas, uiScale) is not { } action)
            return null;
        if (!_bannerTooltips.TryGetValue(action, out string? text))
            return null;

        // Previous and Next name the town they lead to when a pack town gave itself a name.
        if ((action == BannerAction.Previous || action == BannerAction.Next)
            && Mode == 1
            && NeighborhoodAfter(Neighborhood, Neighborhoods, forward: action == BannerAction.Next) is int target
            && target != Neighborhood
            && _townNames.TryGetValue(target, out string? name))
        {
            return $"{text}: {name}";
        }
        return text;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> BannerControls(CGSize canvas, float uiScale = 1f)
    {
        bool hasDedicatedCreditsButton = _bannerButtons.ContainsKey(BannerAction.Credits) && IsEnabled(BannerAction.Credits);
        var result = new List<(string Label, CGRect Rect)>();
        foreach (var action in BannerActionExtensions.AllCases)
        {
            if (!_bannerButtons.ContainsKey(action) || !IsEnabled(action))
                continue;
            if (action == BannerAction.SimsLogo && hasDedicatedCreditsButton)
                continue;

            string label = _bannerTooltips.GetValueOrDefault(action) ?? action.FallbackTooltip();
            result.Add((label, BannerRect(action, canvas, uiScale)));
        }
        return result;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, factor) = Transform(canvas, uiScale);
        var result = new (string Label, CGRect Rect)[_houses.Length];
        for (int i = 0; i < _houses.Length; i++)
        {
            var house = _houses[i];
            var rect = new CGRect(
                left + house.HitRect.MinX * factor,
                top + house.HitRect.MinY * factor,
                house.HitRect.Width * factor,
                house.HitRect.Height * factor);
            // Scripted checks key on these labels; the original rollover text is available from RolloverText.
            int id = house.Entry.House;
            string label = CanCreateHousehold(id)
                ? $"Create a household on vacant lot {id}"
                : $"Open house {id}";
            result[i] = (label, rect);
        }
        return result;
    }

    public IReadOnlyList<DrawItem> DrawItems(CGSize canvas, float uiScale = 1f, double? time = null)
    {
        double now = time ?? MonotonicNowSeconds();
        var (left, top, factor) = Transform(canvas, uiScale);
        var backgroundRect = new CGRect(left, top, _canvasWidth * factor, _canvasHeight * factor);
        var items = new List<DrawItem>();

        if (left > 0 || top > 0)
        {
            if (_largeBackground is { } largeBackground)
            {
                items.AddRange(NineSlice(
                    largeBackground,
                    new CGRect(0, 0, canvas.Width, canvas.Height),
                    requestedEdge: 16 * factor,
                    sourceEdge: 16));
                double border = Math.Max(1.0, factor);
                items.Add(new DrawItem(_frameShadow, new CGRect(backgroundRect.MinX - border, backgroundRect.MinY - border, backgroundRect.Width + border * 2, border)));
                items.Add(new DrawItem(_frameShadow, new CGRect(backgroundRect.MinX - border, backgroundRect.MinY, border, backgroundRect.Height)));
                items.Add(new DrawItem(_frameHighlight, new CGRect(backgroundRect.MinX - border, backgroundRect.MaxY, backgroundRect.Width + border * 2, border)));
                items.Add(new DrawItem(_frameHighlight, new CGRect(backgroundRect.MaxX, backgroundRect.MinY, border, backgroundRect.Height)));
            }
            else
            {
                items.Add(new DrawItem(_outerFill, new CGRect(0, 0, canvas.Width, canvas.Height)));
            }
        }

        items.Add(new DrawItem(Background, backgroundRect));

        void AppendAnimations(bool multiFrame)
        {
            foreach (var animation in _animations)
            {
                int count = animation.Frames.Count;
                if ((count > 1) != multiFrame)
                    continue;
                int totalFrames = _pulsate ? count * 2 - 2 : count;
                int cycleFrames = Math.Max(1, totalFrames);
                double phase = Math.Max(0.0, now - _startedAt) / _frameDuration;
                int frame = (int)phase % cycleFrames;
                int current = frame < count ? frame : Math.Max(0, totalFrames - frame);
                var currentImage = animation.Frames[current];
                items.Add(new DrawItem(
                    currentImage,
                    new CGRect(
                        left + animation.Position.X * factor,
                        top + animation.Position.Y * factor,
                        currentImage.Size.Width * factor,
                        currentImage.Size.Height * factor)));
            }
        }

        // Multi-frame water wave animations are composited onto the map background before house lots (0056bf55, 00566ef1).
        AppendAnimations(multiFrame: true);

        foreach (var house in _houses)
        {
            var entry = house.Entry;
            double scale = factor / _houseScale;
            double x = left + entry.X * factor + (entry.XOff / 2.0 - entry.ClosedImageWidth / 2.0) * scale;
            double y = top + entry.Y * factor + (entry.BaseYOff / 2.0 - entry.ClosedImageHeight / 2.0) * scale;
            items.Add(new DrawItem(
                house.Closed,
                new CGRect(x, y, entry.ClosedImageWidth * scale, entry.ClosedImageHeight * scale)));

            if (HoveredHouse == entry.House)
            {
                double openY = y - entry.AddYOff * scale;
                var openRect = new CGRect(x, openY, entry.OpenImageWidth * scale, entry.OpenImageHeight * scale);
                items.Add(new DrawItem(house.Open, openRect));

                // 0x0054e5e9 composites the mark centered on the highlighted image of a lot whose family is on vacation.
                if (entry.OnVacation == true && _vacationMark is { } mark)
                {
                    double markW = mark.Size.Width * scale;
                    double markH = mark.Size.Height * scale;
                    items.Add(new DrawItem(
                        mark,
                        new CGRect(openRect.MidX - markW / 2.0, openRect.MidY - markH / 2.0, markW, markH)));
                }
            }
        }

        // Single-frame foreground overlays (visland_port, visland_trees, Studiotown/DScreen_top_layer) draw over houses.
        AppendAnimations(multiFrame: false);

        if (_travelFilter is { } activeFilter && _filterMarkers.TryGetValue(activeFilter.SelectedMask, out var marker))
        {
            ReadOnlySpan<int> pulseFrames = [0, 1, 2, 2, 1, 0];
            int frame = pulseFrames[(int)(Math.Max(0.0, now - _startedAt) * (2.0 * Math.PI)) % 6];
            foreach (var house in _houses)
            {
                var entry = house.Entry;
                if (!activeFilter.Marks(entry.Community == true, entry.FilterFlags ?? 0))
                    continue;
                double yOffset = entry.House == 23 ? 10 : 70;
                items.Add(new DrawItem(
                    marker,
                    new CGRect(
                        left + (entry.X - 24) * factor,
                        top + (entry.Y - yOffset) * factor,
                        marker.Size.Width * factor / 3.0,
                        marker.Size.Height * factor),
                    uv: new Vector4(frame / 3f, 0f, 1f / 3f, 1f)));
            }
        }

        if (_travelFilter is null && _banner is { } banner)
        {
            items.Add(new DrawItem(
                banner,
                new CGRect(
                    backgroundRect.MinX,
                    backgroundRect.MinY,
                    banner.Size.Width * factor,
                    banner.Size.Height * factor)));
        }

        if (_travelFilter is null)
        {
            foreach (var action in BannerActionExtensions.AllCases)
            {
                if (!_bannerButtons.TryGetValue(action, out var sheet))
                    continue;
                float frame = !IsEnabled(action) ? 3f
                    : ((action == BannerAction.Bulldoze && EvictMode) || (action == BannerAction.Rezone && RezoneMode)) ? 2f
                    : HoveredBanner == action ? 1f : 0f;
                float frames = action.FrameCount();
                items.Add(new DrawItem(
                    sheet,
                    BannerRect(action, canvas, uiScale),
                    uv: new Vector4(MathF.Min(frame, frames - 1f) / frames, 0f, 1f / frames, 1f)));
            }
        }

        if (_travelFilter is null && _neighborhoodNumberImage is { } numberImage)
        {
            double w = numberImage.Size.Width * factor;
            double h = numberImage.Size.Height * factor;
            double x = left + 130.5 * factor - w / 2.0;
            double y = top + 26.0 * factor - h / 2.0;
            items.Add(new DrawItem(numberImage, new CGRect(x, y, w, h)));
        }

        if (_travelFilter is { } toolbarFilter && _filterToolbar is { } toolbar)
        {
            items.Add(new DrawItem(
                toolbar,
                new CGRect(left, top, toolbar.Size.Width * factor, toolbar.Size.Height * factor)));

            var masks = NeighborhoodLotFilter.Masks;
            var positions = NeighborhoodLotFilter.Positions;
            for (int index = 0; index < masks.Count && index < _filterButtons.Count; index++)
            {
                var sheet = _filterButtons[index];
                float frame = !toolbarFilter.IsEnabled(index) ? 3f
                    : toolbarFilter.SelectedMask == masks[index] ? 2f
                    : _hoveredTravelFilter == index ? 1f : 0f;
                items.Add(new DrawItem(
                    sheet,
                    new CGRect(
                        left + positions[index] * factor,
                        top + 20 * factor,
                        sheet.Size.Width * factor / 4.0,
                        sheet.Size.Height * factor),
                    uv: new Vector4(frame / 4f, 0f, 0.25f, 1f)));
            }
        }

        AppendRolloverItems(left, top, factor, items);
        AppendBannerTipItems(canvas, uiScale, items);
        return items;
    }

    /// <summary>Draws a tiler image with its <paramref name="sourceEdge"/>-pixel corners and edges kept at <paramref name="requestedEdge"/> points and its centre stretched.</summary>
    public static IReadOnlyList<DrawItem> NineSlice(
        OriginalGameplayOverlay.Image image,
        CGRect rect,
        double requestedEdge,
        double sourceEdge,
        float opacity = 1f) =>
        NineSlice(image, rect, edge: requestedEdge, sourceEdge: sourceEdge, opacity: opacity, outList: null);

    private static IReadOnlyList<DrawItem> NineSlice(
        OriginalGameplayOverlay.Image image,
        CGRect rect,
        double edge,
        double sourceEdge,
        float opacity,
        List<DrawItem>? outList)
    {
        double clampedEdge = Math.Min(edge, Math.Min(rect.Width / 2.0, rect.Height / 2.0));
        float u = (float)Math.Min(1.0 / 3.0, sourceEdge / Math.Max(1.0, image.Size.Width));
        float v = (float)Math.Min(1.0 / 3.0, sourceEdge / Math.Max(1.0, image.Size.Height));
        double[] xs = [rect.MinX, rect.MinX + clampedEdge, rect.MaxX - clampedEdge, rect.MaxX];
        double[] ys = [rect.MinY, rect.MinY + clampedEdge, rect.MaxY - clampedEdge, rect.MaxY];
        float[] us = [0f, u, 1f - u, 1f];
        float[] vs = [0f, v, 1f - v, 1f];
        var items = outList ?? [];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double w = xs[column + 1] - xs[column];
                double h = ys[row + 1] - ys[row];
                if (w <= 0 || h <= 0)
                    continue;

                items.Add(new DrawItem(
                    image,
                    new CGRect(xs[column], ys[row], w, h),
                    opacity: opacity,
                    uv: new Vector4(us[column], vs[row], us[column + 1] - us[column], vs[row + 1] - vs[row])));
            }
        }
        return items;
    }

    /// <summary>The banner button tooltip: the same translucent panel as the lot rollover, centred under the button.</summary>
    private void AppendBannerTipItems(CGSize canvas, float uiScale, List<DrawItem> outItems)
    {
        if (_bannerTip is not { } tip
            || _hoveredTipAction != tip.Action
            || _rolloverBackground is not { } background
            || PlacementTooltip.Progress(ref _bannerTipStartedAt, _bannerTipQuickTips) is not { } progress)
        {
            return;
        }

        var (left, top, factor) = Transform(canvas, uiScale);
        double panelRight = left + _canvasWidth * factor;
        double panelBottom = top + _canvasHeight * factor;
        var button = BannerRect(tip.Action, canvas, uiScale);
        double width = tip.Image.Size.Width * factor + 16 * factor;
        double height = tip.Image.Size.Height * factor + 8 * factor;
        double minX = left + 4 * factor;
        double maxX = Math.Max(minX, panelRight - width - 4 * factor);
        double x = Math.Min(Math.Max(minX, button.MidX - width / 2.0), maxX);
        double y = button.MaxY + 6 * factor;
        if (y + height > panelBottom - 4 * factor)
            y = Math.Max(top + 4 * factor, button.MinY - height - 6 * factor);

        var tipItems = new List<DrawItem>();
        NineSlice(
            background,
            new CGRect(x, y, width, height),
            edge: 14 * factor,
            sourceEdge: 20,
            opacity: 176f / 255f,
            outList: tipItems);
        tipItems.Add(new DrawItem(
            tip.Image,
            new CGRect(x + 8 * factor, y + 4 * factor, tip.Image.Size.Width * factor, tip.Image.Size.Height * factor)));

        if (progress >= 1.0)
        {
            outItems.AddRange(tipItems);
            return;
        }

        foreach (var item in tipItems)
        {
            double clippedHeight = Math.Max(1.0, Math.Round(item.Rect.Height * progress, MidpointRounding.AwayFromZero));
            double fraction = clippedHeight / item.Rect.Height;
            outItems.Add(new DrawItem(
                item.Image,
                new CGRect(item.Rect.MinX, item.Rect.MaxY - clippedHeight, item.Rect.Width, clippedHeight),
                opacity: item.Opacity,
                uv: new Vector4(
                    item.UV.X,
                    item.UV.Y + item.UV.W * (float)(1.0 - fraction),
                    item.UV.Z,
                    item.UV.W * (float)fraction)));
        }
    }

    private void AppendRolloverItems(double left, double top, double factor, List<DrawItem> outItems)
    {
        if (HoveredHouse is not int hoveredHouse)
            return;

        House? house = null;
        foreach (var candidate in _houses)
        {
            if (candidate.Entry.House == hoveredHouse)
            {
                house = candidate;
                break;
            }
        }
        if (house is null || RolloverComposite(hoveredHouse) is not { } composite)
            return;

        // The original tooltip placement (0x005ddae6, drawn by 0x005d9f30): the box's left edge at the middle of the lot,
        // one pixel above it, or two pixels below it when that would come within 3 pixels of the top, then moved inside the screen.
        var box = composite.Size;
        var hit = house.HitRect;
        double originX = Math.Truncate((hit.MinX + hit.MaxX) / 2.0);
        double originY = hit.MinY - box.Height - 1;
        if (originY < 3)
            originY = hit.MaxY + 2;
        if (originX < 0)
            originX = 0;
        else if (originX + box.Width >= _canvasWidth)
            originX = _canvasWidth - box.Width - 1;

        if (originY < 0)
            originY = 0;
        else if (originY + box.Height >= _canvasHeight)
            originY = _canvasHeight - box.Height - 1;

        double x = left + originX * factor;
        double y = top + originY * factor;
        outItems.Add(new DrawItem(
            composite.Image,
            new CGRect(x, y, box.Width * factor, box.Height * factor)));
    }

    private static double MonotonicNowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
