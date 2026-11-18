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
    ReturnHome,
    Mods
}

public static class BannerActionExtensions
{
    private static readonly BannerAction[] AllBannerActions = Enum.GetValues<BannerAction>();

    public static IReadOnlyList<BannerAction> AllCases => AllBannerActions;

    public static IReadOnlyList<BannerAction> ActionsForMapMode(int mode)
    {
        if (mode == 1)
            return AllBannerActions.Where(static a => a != BannerAction.ReturnHome).ToArray();

        var list = new List<BannerAction>
        {
            BannerAction.ReturnHome,
            BannerAction.Mods,
            BannerAction.Internet,
            BannerAction.SimsLogo,
            BannerAction.ImportFamily,
            BannerAction.Bulldoze
        };
        if (mode == 7)
            list.Add(BannerAction.SelectFamily);
        return list;
    }

    public static string? VectorSvgStem(this BannerAction action) => action switch
    {
        BannerAction.SelectFamily => "movein",
        BannerAction.Bulldoze => "bulldoze",
        BannerAction.Quit => "exit",
        BannerAction.Internet => "inetbtn",
        BannerAction.Mods => "mods",
        BannerAction.Previous => "previous",
        BannerAction.Next => "next",
        BannerAction.ImportFamily => "import",
        BannerAction.Downtown => "downtown",
        BannerAction.SimsLogo => "thesimslogo",
        BannerAction.Vacation => "vacation",
        BannerAction.Rezone => "rezone",
        BannerAction.StudioTown => "studiotown",
        BannerAction.MagicTown => "magicland",
        BannerAction.ReturnHome => "return",
        _ => null
    };

    public static VectorBannerRenderer.ButtonKind VectorButtonKind(this BannerAction action) => action switch
    {
        BannerAction.Previous or BannerAction.Next => VectorBannerRenderer.ButtonKind.SmallArrow26,
        BannerAction.Internet or BannerAction.Mods => VectorBannerRenderer.ButtonKind.InsetRing39,
        _ => VectorBannerRenderer.ButtonKind.Medallion53
    };

    public static float RingCenterX1x(this BannerAction action) => action switch
    {
        BannerAction.Previous => 12.94f,
        BannerAction.Next => 13.19f,
        BannerAction.Internet or BannerAction.Mods => 19.97f,
        BannerAction.SelectFamily => 24.32f,
        BannerAction.ImportFamily => 24.56f,
        BannerAction.Downtown => 24.87f,
        BannerAction.ReturnHome => 27.14f,
        BannerAction.Quit => 25.59f,
        _ => 25.22f
    };

    public static bool HasLowerCollarBulge(this BannerAction action) =>
        action is BannerAction.SelectFamily
            or BannerAction.ImportFamily
            or BannerAction.Bulldoze
            or BannerAction.Rezone
            or BannerAction.Downtown
            or BannerAction.Vacation
            or BannerAction.StudioTown
            or BannerAction.MagicTown
            or BannerAction.Quit
            or BannerAction.ReturnHome;

    public static string Resource(this BannerAction action, int mapMode) => action switch
    {
        BannerAction.SelectFamily => "UIGraphics/NghUI/MoveIn.bmp",
        BannerAction.Bulldoze => mapMode == 1 ? "UIGraphics/NghUI/Bulldoze.bmp" : "UIGraphics/Downtown/Bulldoze.bmp",
        BannerAction.Quit => "UIGraphics/NghUI/Exit.bmp",
        BannerAction.Internet or BannerAction.Mods => "UIGraphics/NghUI/InetBtn.bmp",
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
        BannerAction.Mods => new CGPoint(724, 1.15),
        BannerAction.Internet => new CGPoint(762, 1.15),
        BannerAction.Credits => new CGPoint(195, 540),
        BannerAction.Previous => new CGPoint(106, 1.85),
        BannerAction.Next => new CGPoint(141, 1.85),
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

    public static int FrameCount(this BannerAction action) => action == BannerAction.SimsLogo ? 1 : 4;

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

    public static string FallbackTooltip(this BannerAction action) => action switch
    {
        BannerAction.ReturnHome => "Return to Neighborhood View",
        BannerAction.Mods => "Mods & Custom Content",
        _ => FallbackTooltips[action.TooltipIndex()]
    };

    public static int? MapMode(this BannerAction action) => action switch
    {
        BannerAction.Downtown => 2,
        BannerAction.Vacation => 3,
        BannerAction.StudioTown => 5,
        BannerAction.MagicTown => 7,
        _ => null
    };

    public static string? RequiredPackFolder(this BannerAction action) => action switch
    {
        BannerAction.Downtown => "ExpansionPack3",
        BannerAction.Vacation => "ExpansionPack4",
        BannerAction.StudioTown => "ExpansionPack6",
        BannerAction.MagicTown => "ExpansionPack7",
        _ => null
    };

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

        /// <summary>Lists importable files waiting in the profile import directory, where the presence of
        /// family, residential lot (.rdt), or community lot (.cmt) files enables the import control.</summary>
        [JsonPropertyName("imports")]
        public IReadOnlyList<Import>? Imports { get; init; }

        [JsonPropertyName("importEnabled")]
        public bool? ImportEnabled { get; init; }

        [JsonPropertyName("travelFilter")]
        public int? TravelFilter { get; init; }

        [JsonPropertyName("backgroundImagePath")]
        public string? BackgroundImagePath { get; init; }

        [JsonPropertyName("neighborhood")]
        public int? Neighborhood { get; init; }

        [JsonPropertyName("neighborhoods")]
        public IReadOnlyList<int>? Neighborhoods { get; init; }

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
        /// <summary>Allocates one opaque portrait image per family member, preserving slot alignment.</summary>
        IReadOnlyList<OriginalGameplayOverlay.Image?> MemberPortraits);

    private sealed record RolloverCard(
        int House,
        CGSize BoxSize,
        IReadOnlyList<OriginalGameplayOverlay.Image?> Lines,
        int LineHeight,
        IReadOnlyList<(NeighborhoodRolloverLayout.Rect Rect, OriginalGameplayOverlay.Image Image)> Portraits);

    private sealed record Animation(
        CGPoint Position,
        IReadOnlyList<OriginalGameplayOverlay.Image> Frames);

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
    private readonly ExtendedNeighborhoodBackdrop.Result? _extendedBackground;
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

    public IReadOnlyList<OriginalFamilyPickerOverlay.Family> Families { get; }

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
    private readonly GameContent _content;
    private readonly OriginalUIStrings _strings;
    private readonly SimsBitmapFont? _font;
    private readonly SimsBitmapFont? _tooltipFont;
    private OriginalGameplayOverlay.Image? _neighborhoodNumberImage;
    private readonly OriginalGameplayOverlay.Image? _rolloverBackground;
    private OriginalGameplayOverlay.Image? _banner;
    private bool _builtVectorBanner;
    private NeighborhoodLotFilter? _travelFilter;
    private int? _hoveredTravelFilter;
    private readonly OriginalGameplayOverlay.Image? _filterToolbar;
    private readonly List<OriginalGameplayOverlay.Image> _filterButtons = [];
    private readonly Dictionary<int, OriginalGameplayOverlay.Image> _filterMarkers = [];
    private Dictionary<BannerAction, OriginalGameplayOverlay.Image> _bannerButtons = [];
    private readonly Dictionary<BannerAction, string> _bannerTooltips;
    private readonly HashSet<BannerAction> _installedBannerActions;
    public BannerAction? HoveredBanner { get; private set; }
    private RolloverCard? _rolloverCache;

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

        _content = content;
        _strings = new OriginalUIStrings(content);
        _font = font;
        SimsBitmapFont? tipFont = font;
        foreach (string tipFontPath in new[] { "UIGraphics/Fonts/variablesans_09.ffn", "UIGraphics/Fonts/variablesans_08.ffn" })
        {
            try
            {
                tipFont = new SimsBitmapFont(content.Read(tipFontPath));
                break;
            }
            catch
            {
            }
        }
        _tooltipFont = tipFont;
        _device = device;

        if (font is null)
        {
            _rolloverBackground = null;
        }
        else
        {
            var tiler = new SimsTarga(content.Read("UIGraphics/Nbhd/HotSpotPopupTiler.tga"));
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

        OriginalGameplayOverlay.Image ClassicBitmapImage(string path)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        }

        OriginalGameplayOverlay.Image? TryBannerImage(string path)
        {
            try
            {
                return ClassicBitmapImage(path);
            }
            catch
            {
                return null;
            }
        }

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

        _vacationMark = TryBannerImage(manifest.Mode == 3
            ? "UIGraphics/VIsland/no_vacancy.bmp"
            : "UIGraphics/VIsland/vacationlogosmall.bmp");

        var uiStrings = new OriginalUIStrings(content);
        var actions = BannerActionExtensions.ActionsForMapMode(manifest.Mode);
        var tips = new Dictionary<BannerAction, string>();
        var installed = new HashSet<BannerAction>();

        foreach (var action in actions)
        {
            bool present = true;
            if (action.RequiredPackFolder() is { } packFolder)
            {
                string packPath = Path.Combine(content.Root, packFolder);
                present = Directory.Exists(packPath) || File.Exists(packPath);
            }
            if (present)
                installed.Add(action);

            if (action == BannerAction.Mods)
            {
                tips[action] = action.FallbackTooltip();
            }
            else if (!present && action.MissingPackTooltipIndex() is int missingIdx)
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

        _bannerTooltips = tips;
        _installedBannerActions = installed;
        RebuildBannerChrome(manifest.Mode);

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
        bool supportsExtendedBackdrop = ExtendedNeighborhoodBackdrop.Supports(content, manifest.BackgroundResource);
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
            bool featherEdges = supportsExtendedBackdrop;
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

                frames[frameIdx] = featherEdges
                    ? ExtendedNeighborhoodBackdrop.CreateFeatheredAnimationFrame(
                        device,
                        bitmap,
                        ox,
                        oy,
                        backgroundBitmap.Width,
                        backgroundBitmap.Height)
                    : OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
            }

            animations[layerIdx] = new Animation(new CGPoint(layer.PositionX, layer.PositionY), frames);
        }
        _animations = animations;
        _extendedBackground = supportsExtendedBackdrop
            ? ExtendedNeighborhoodBackdrop.TryCreate(
                content,
                device,
                manifest.BackgroundResource,
                manifest.BackgroundImagePath is not null,
                backgroundPixels,
                backgroundBitmap.Width,
                backgroundBitmap.Height,
                manifest.AnimationLayers)
            : null;

        _outerFill = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 50, 0, 255]);
        // Loads a wide background backdrop behind the centered 800x600 neighborhood map when the display
        // resolution exceeds 800 pixels in width.
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
                if (bmp.Width >= 64 && bmp.Height >= 64)
                {
                    // Extract a clean 64x64 9-slice frame from the outer 32px border of largeback.bmp so its
                    // baked-in 1024x768 center cutout shadow does not appear as a second floating box on widescreen/4K.
                    byte[] cleanFrame = new byte[64 * 64 * 4];
                    for (int y = 0; y < 64; y++)
                    {
                        int sy = y < 32 ? y : bmp.Height - (64 - y);
                        for (int x = 0; x < 64; x++)
                        {
                            int sx = x < 32 ? x : bmp.Width - (64 - x);
                            int src = (sy * bmp.Width + sx) * 4;
                            int dst = (y * 64 + x) * 4;
                            cleanFrame[dst] = bmp.Rgba[src];
                            cleanFrame[dst + 1] = bmp.Rgba[src + 1];
                            cleanFrame[dst + 2] = bmp.Rgba[src + 2];
                            cleanFrame[dst + 3] = 255;
                        }
                    }
                    loadedLargeBack = OriginalGameplayOverlay.CreateImage(device, 64, 64, cleanFrame);
                }
                else
                {
                    loadedLargeBack = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba);
                }
                break;
            }
            catch
            {
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
            foreach (string fontPath in new[] { "UIGraphics/Fonts/variablesans_12.ffn", "UIGraphics/Fonts/variablesans_11.ffn", "UIGraphics/Fonts/variablesans_10.ffn" })
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
            var memberPortraits = new OriginalGameplayOverlay.Image?[memberCount];
            for (int m = 0; m < memberCount; m++)
            {
                if (memberImages[m] is not { } memberPath)
                    continue;
                try
                {
                    var bmp = ReadHouseBitmap(memberPath, width: null, height: null);
                    memberPortraits[m] = OriginalGameplayOverlay.CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
                }
                catch
                {
                    memberPortraits[m] = null;
                }
            }

            houses[entryIdx] = new House(entry, closed, open, hitRect, memberPortraits);
        }
        _houses = houses;
    }

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

    private double TopChromeY(double top) => _extendedBackground is not null ? 0.0 : top;

    private (double Left, double Top, double Factor) Transform(CGSize canvas, float uiScale = 1f)
    {
        _ = uiScale;
        double w = _canvasWidth;
        double h = _canvasHeight;
        double fit = Math.Max(0.001, Math.Min(canvas.Width / w, canvas.Height / h));
        double factor = _extendedBackground is { } ext
            ? Math.Max(fit, Math.Max(canvas.Width / ext.Width, canvas.Height / ext.Height))
            : fit;
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

        var (left, top, factor) = Transform(canvas, uiScale);
        double chromeTop = TopChromeY(top);
        var logical = new CGPoint((point.X - left) / factor, (point.Y - chromeTop) / factor);
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

    /// <summary>Indicates whether a household is currently away on vacation, which prevents evicting the
    /// family.</summary>
    public bool IsOnVacation(int house) =>
        FindEntry(house)?.OnVacation == true;

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

    public void SetEvictMode(bool value)
    {
        EvictMode = value;
        _rolloverCache = null;
    }

    public void SetMovingInFamily(OriginalFamilyPickerOverlay.Family? value)
    {
        MovingInFamily = value;
        _rolloverCache = null;
    }

    public void SetRezoneMode(bool value)
    {
        RezoneMode = value;
        _rolloverCache = null;
    }

    private void RebuildBannerChrome(int mode)
    {
        bool vectorEnabled = SimsBitmapFont.SharpTextEnabled;
        _builtVectorBanner = vectorEnabled;
        int vectorScale = vectorEnabled ? 4 : 1;

        var actions = BannerActionExtensions.ActionsForMapMode(mode);
        string bannerResPath = mode == 1 ? "UIGraphics/NghUI/banner_neighborhood.bmp" : "UIGraphics/NghUI/banner_downtown.bmp";
        bool bannerOverriddenByMod = _content.Packs.Assets.ContainsKey(PackContentPaths.ResourceKey(bannerResPath));

        if (vectorEnabled && !bannerOverriddenByMod)
        {
            float[] collarCenters = actions
                .Where(static a => a.HasLowerCollarBulge())
                .Select(a => (float)a.Position(mode).X + a.RingCenterX1x())
                .OrderBy(static x => x)
                .ToArray();
            var renderedBar = VectorBannerRenderer.RenderBannerBar(collarCenters, vectorScale);
            _banner = OriginalGameplayOverlay.CreateImage(
                _device,
                renderedBar.PixelWidth,
                renderedBar.PixelHeight,
                renderedBar.Rgba,
                renderedBar.LogicalSize);
        }
        else
        {
            var barBmp = new SimsBitmap(_content.Read(bannerResPath));
            _banner = OriginalGameplayOverlay.CreateImage(_device, barBmp.Width, barBmp.Height, barBmp.Rgba, magenta: true);
        }

        var sheets = new Dictionary<BannerAction, OriginalGameplayOverlay.Image>();
        foreach (var action in actions)
        {
            string resPath = action.Resource(mode);
            bool modOverridden = action != BannerAction.Mods
                && _content.Packs.Assets.ContainsKey(PackContentPaths.ResourceKey(resPath));
            string? stem = action.VectorSvgStem();

            if (!modOverridden && stem is not null && (vectorEnabled || action == BannerAction.Mods) && VectorBannerRenderer.HasEmbeddedSvg(stem))
            {
                var raster = action == BannerAction.SimsLogo
                    ? VectorBannerRenderer.RenderSimsLogo(vectorScale)
                    : VectorBannerRenderer.RenderButtonStrip(stem, action.VectorButtonKind(), action.RingCenterX1x(), vectorScale);
                sheets[action] = OriginalGameplayOverlay.CreateImage(
                    _device,
                    raster.PixelWidth,
                    raster.PixelHeight,
                    raster.Rgba,
                    raster.LogicalSize);
            }
            else
            {
                var bmp = new SimsBitmap(_content.Read(resPath));
                sheets[action] = OriginalGameplayOverlay.CreateImage(_device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
            }
        }

        _bannerButtons = sheets;

        if (mode == 1 && Neighborhood > 0)
        {
            OriginalGameplayOverlay.Image? numImg = null;
            foreach (string fontPath in new[] { "UIGraphics/Fonts/variablesans_12.ffn", "UIGraphics/Fonts/variablesans_11.ffn", "UIGraphics/Fonts/variablesans_10.ffn" })
            {
                try
                {
                    var numberFont = new SimsBitmapFont(_content.Read(fontPath));
                    numImg = OriginalGameplayOverlay.Text(
                        Neighborhood.ToString(CultureInfo.InvariantCulture),
                        numberFont,
                        _device,
                        (255, 255, 255));
                    break;
                }
                catch
                {
                }
            }
            _neighborhoodNumberImage = numImg;
        }
    }

    public void ClearTextCaches()
    {
        _rolloverCache = null;
        _bannerTip = null;
        if (_builtVectorBanner != SimsBitmapFont.SharpTextEnabled || Mode == 1)
        {
            RebuildBannerChrome(Mode);
        }
    }

    /// <summary>Indicates whether a lot is completely bulldozed, which is required before the lot can be
    /// rezoned.</summary>
    public bool IsBulldozed(int house) => FindEntry(house)?.Bulldozed == true;

    public bool HasLot(int house) => FindEntry(house) is not null;

    public int ZoningIndex(int house) => FindEntry(house)?.Residential == true ? 0 : 1;

    public int? StateClass(int house)
    {
        if (FindEntry(house) is not { } entry)
            return null;
        return entry.Community == true ? 2 : entry.Residential ? 1 : null;
    }

    public int? Price(int house) => FindEntry(house)?.Price;

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

    /// <summary>Resolves the street address template by lot number: expansion lot ranges use
    /// expansion-specific strings, while standard lots look up street names in resource chunk STR# 2000 via
    /// STR# 2001, falling back to default string entries.</summary>
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
            // Downtown lots (21–31) and Vacation Island lots (40–49) prioritize street templates exported
            // from their respective expansion packages.
            if ((house is >= 21 and <= 31 || house is >= 40 and <= 49) && entry.StreetTemplate is { } template)
                return template;
            return _strings.NeighborhoodRollover(idx, "%s Sim Lane");
        }

        return entry.StreetTemplate ?? _strings.NeighborhoodRollover(7, "%s Sim Lane");
    }

    /// <summary>Builds a cached lot hover card pairing the opaque 9-sliced popup frame with Hi-DPI
    /// text lines and opaque 25x25 household portrait tiles.</summary>
    private RolloverCard? RolloverCardForHouse(int houseNumber)
    {
        if (_rolloverCache is { } cached && cached.House == houseNumber)
            return cached;

        if (_font is not { } font || _rolloverBackground is null)
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

        int memberCount = house.Entry.Occupied ? Math.Min(8, house.MemberPortraits.Count) : 0;
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

        var lineImages = new OriginalGameplayOverlay.Image?[layout.Lines.Count];
        for (int lineIndex = 0; lineIndex < layout.Lines.Count; lineIndex++)
        {
            string lineText = layout.Lines[lineIndex];
            if (string.IsNullOrEmpty(lineText))
                continue;
            try
            {
                lineImages[lineIndex] = OriginalGameplayOverlay.Text(lineText, font, _device, (255, 255, 255));
            }
            catch
            {
            }
        }

        int headPairs = Math.Min(layout.Heads.Count, house.MemberPortraits.Count);
        var portraits = new List<(NeighborhoodRolloverLayout.Rect Rect, OriginalGameplayOverlay.Image Image)>(headPairs);
        for (int i = 0; i < headPairs; i++)
        {
            if (house.MemberPortraits[i] is { } portrait)
                portraits.Add((layout.Heads[i], portrait));
        }

        var card = new RolloverCard(houseNumber, new CGSize(boxW, boxH), lineImages, font.LineHeight, portraits);
        _rolloverCache = card;
        return card;
    }

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

    public bool IsEnabled(BannerAction action) => action switch
    {
        BannerAction.Quit or BannerAction.Bulldoze or BannerAction.ReturnHome or BannerAction.SimsLogo or BannerAction.Mods => true,
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
        double baseY = action == BannerAction.Credits ? top : TopChromeY(top);
        return new CGRect(
            left + position.X * factor,
            baseY + position.Y * factor,
            sheet.Size.Width / action.FrameCount() * factor,
            sheet.Size.Height * factor);
    }

    public BannerAction? BannerActionAt(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
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
            && (_tooltipFont ?? _font) is { } font)
        {
            if (_bannerTip is not { } existing || existing.Action != action || !string.Equals(existing.Text, text, StringComparison.Ordinal))
            {
                _bannerTipStartedAt = null;
                try
                {
                    var image = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(text, font));
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

        if (_extendedBackground is { } extendedBackground)
        {
            items.Add(new DrawItem(
                extendedBackground.Image,
                new CGRect(
                    left + extendedBackground.OffsetX * factor,
                    top + extendedBackground.OffsetY * factor,
                    extendedBackground.Width * factor,
                    extendedBackground.Height * factor)));
        }
        else
        {
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
        }

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

        // Composites multi-frame water wave animations onto the map background before drawing lot overlays.
        AppendAnimations(multiFrame: true);

        foreach (var house in _houses)
        {
            var entry = house.Entry;
            double scale = factor / _houseScale;
            double x = left + entry.X * factor + (entry.XOff / 2.0 - entry.ClosedImageWidth / 2.0) * scale;
            double y = top + entry.Y * factor + (entry.BaseYOff / 2.0 - entry.ClosedImageHeight / 2.0) * scale;
            if (_extendedBackground is null || entry.LotHasHouse != false)
            {
                items.Add(new DrawItem(
                    house.Closed,
                    new CGRect(x, y, entry.ClosedImageWidth * scale, entry.ClosedImageHeight * scale)));
            }

            if (HoveredHouse == entry.House)
            {
                double openY = y - entry.AddYOff * scale;
                var openRect = new CGRect(x, openY, entry.OpenImageWidth * scale, entry.OpenImageHeight * scale);
                items.Add(new DrawItem(house.Open, openRect));

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

        // Renders single-frame scenery overlay animations above house graphics.
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

        double chromeTop = TopChromeY(top);
        if (_travelFilter is null && _banner is { } banner)
        {
            items.Add(new DrawItem(
                banner,
                new CGRect(
                    backgroundRect.MinX,
                    chromeTop,
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
            double x = left + 136.5 * factor - w / 2.0;
            double y = chromeTop + 20.70 * factor - h / 2.0;
            items.Add(new DrawItem(numberImage, new CGRect(x, y, w, h)));
        }

        if (_travelFilter is { } toolbarFilter && _filterToolbar is { } toolbar)
        {
            items.Add(new DrawItem(
                toolbar,
                new CGRect(left, chromeTop, toolbar.Size.Width * factor, toolbar.Size.Height * factor)));

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
                        chromeTop + 20 * factor,
                        sheet.Size.Width * factor / 4.0,
                        sheet.Size.Height * factor),
                    uv: new Vector4(frame / 4f, 0f, 0.25f, 1f)));
            }
        }

        AppendRolloverItems(left, top, factor, items);
        AppendBannerTipItems(canvas, uiScale, items);
        return items;
    }

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

    private void AppendBannerTipItems(CGSize canvas, float uiScale, List<DrawItem> outItems)
    {
        if (_bannerTip is not { } tip
            || _hoveredTipAction != tip.Action
            || PlacementTooltip.Progress(ref _bannerTipStartedAt, _bannerTipQuickTips) is not { } progress)
        {
            return;
        }

        var (left, top, factor) = Transform(canvas, uiScale);
        double tipScale = uiScale > 0f ? Math.Min(factor, Math.Max(0.25, uiScale)) : factor;
        double boundLeft = _extendedBackground is not null ? 0.0 : left;
        double boundTop = _extendedBackground is not null ? 0.0 : top;
        double panelRight = _extendedBackground is not null ? canvas.Width : left + _canvasWidth * factor;
        double panelBottom = _extendedBackground is not null ? canvas.Height : top + _canvasHeight * factor;
        var button = BannerRect(tip.Action, canvas, uiScale);
        double width = tip.Image.Size.Width * tipScale;
        double height = tip.Image.Size.Height * tipScale;
        double minX = boundLeft + 4 * factor;
        double maxX = Math.Max(minX, panelRight - width - 4 * factor);
        double x = Math.Min(Math.Max(minX, button.MidX - width / 2.0), maxX);
        double y = button.MaxY + 4 * factor;
        if (y + height > panelBottom - 4 * factor)
            y = Math.Max(boundTop + 4 * factor, button.MinY - height - 4 * factor);

        var rect = new CGRect(x, y, width, height);
        if (progress >= 1.0)
        {
            outItems.Add(new DrawItem(tip.Image, rect, opacity: 1f));
            return;
        }

        double clippedHeight = Math.Max(1.0, Math.Round(rect.Height * progress, MidpointRounding.AwayFromZero));
        double fraction = Math.Clamp(clippedHeight / rect.Height, 0.0, 1.0);
        outItems.Add(new DrawItem(
            tip.Image,
            new CGRect(rect.MinX, rect.MaxY - clippedHeight, rect.Width, clippedHeight),
            opacity: 1f,
            uv: new Vector4(0f, (float)(1.0 - fraction), 1f, (float)fraction)));
    }

    private void AppendRolloverItems(double left, double top, double factor, List<DrawItem> outItems)
    {
        if (HoveredHouse is not int hoveredHouse || _rolloverBackground is not { } background)
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
        if (house is null || RolloverCardForHouse(hoveredHouse) is not { } card)
            return;

        // Positions the tooltip with its left edge centered on the lot, placed one pixel above or two
        // pixels below when within three pixels of the screen top.
        var box = card.BoxSize;
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
        outItems.AddRange(NineSlice(
            background,
            new CGRect(x, y, box.Width * factor, box.Height * factor),
            requestedEdge: 22 * factor,
            sourceEdge: 22,
            opacity: 1f));

        const int inset = NeighborhoodRolloverLayout.Inset;
        for (int lineIndex = 0; lineIndex < card.Lines.Count; lineIndex++)
        {
            if (card.Lines[lineIndex] is not { } lineImg)
                continue;
            outItems.Add(new DrawItem(
                lineImg,
                new CGRect(
                    left + (originX + inset) * factor,
                    top + (originY + inset + lineIndex * card.LineHeight) * factor,
                    lineImg.Size.Width * factor,
                    lineImg.Size.Height * factor),
                opacity: 1f));
        }

        foreach (var (headRect, portraitImg) in card.Portraits)
        {
            double drawW = Math.Min(headRect.Width, portraitImg.Size.Width) * factor;
            double drawH = Math.Min(headRect.Height, portraitImg.Size.Height) * factor;
            outItems.Add(new DrawItem(
                portraitImg,
                new CGRect(
                    left + (originX + headRect.X) * factor,
                    top + (originY + headRect.Y) * factor,
                    drawW,
                    drawH),
                opacity: 1f));
        }
    }

    private static double MonotonicNowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
