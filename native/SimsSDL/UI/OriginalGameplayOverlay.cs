using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace SimsSDL.UI;

public sealed class OriginalGameplayOverlay
{
    public enum Subpanel
    {
        Needs,
        Relationships,
        House,
        Personality,
        Career,
        Fame,
        PetSkills,
        Interests,
        Inventory,
        ReportCard
    }

    public readonly record struct Image(UITexture Texture, CGSize Size);

    public struct Part
    {
        public Image Image { get; init; }
        public CGRect Rect { get; init; }
        public Vector4 UV { get; set; }
        public float Opacity { get; set; }

        public Part(Image image, CGRect rect)
            : this(image, rect, new Vector4(0f, 0f, 1f, 1f), 1f)
        {
        }

        public Part(Image image, CGRect rect, Vector4 uv, float opacity = 1f)
        {
            Image = image;
            Rect = rect;
            UV = uv;
            Opacity = opacity;
        }
    }

    private struct ActionAnimation
    {
        public CGPoint StartOrigin;
        public CGPoint TargetOrigin;
        public double StartedAt;

        public readonly CGPoint CurrentOrigin(double now, double duration = 0.5)
        {
            double elapsed = now - StartedAt;
            if (elapsed >= duration || duration <= 0.0)
            {
                return TargetOrigin;
            }

            double t = Math.Max(0.0, Math.Min(1.0, elapsed / duration));
            return new CGPoint(
                Math.Truncate(StartOrigin.X + (TargetOrigin.X - StartOrigin.X) * t),
                Math.Truncate(StartOrigin.Y + (TargetOrigin.Y - StartOrigin.Y) * t));
        }
    }

    private readonly record struct MotiveBarCacheKey(int Width, uint GreenRgb, uint RedRgb);
    private readonly record struct MotiveArrowStripCacheKey(bool Rising, uint C0, uint C1, uint C2, uint C3, uint C4);

    private static readonly (byte R, byte G, byte B) LivePopupTextColor = (195, 205, 205);
    private static readonly Subpanel[] MainTabSubpanels =
    [
        Subpanel.Needs,
        Subpanel.Relationships,
        Subpanel.House,
        Subpanel.Personality,
        Subpanel.Career,
        Subpanel.Fame,
        Subpanel.PetSkills,
        Subpanel.Interests,
        Subpanel.Inventory
    ];

    public static readonly CGRect FundsRect = new(118, 123, 62, 14);
    private static readonly CGRect FamilyFriendsSmileyRect = new(67, 135, 10, 10);
    private const double FamilyFriendsLabelX = 80.0;
    private const double FamilyFriendsTextY = 132.5;

    private readonly OriginalUIStrings _strings;
    private readonly Image _patch;
    private readonly Image _vectorPatch;
    private readonly Image _universal;
    private readonly Image _vectorUniversal;
    private readonly Image _vectorLcdFriendSmiley;
    private readonly Image _gadget;
    private readonly Image _vectorGadget;
    private readonly Image _people;
    private readonly Image _vectorPeople;
    private readonly Image _unknown;

    private Image ActiveGadget => SimsBitmapFont.SharpTextEnabled ? _vectorGadget : _gadget;
    private Image ActivePatch => SimsBitmapFont.SharpTextEnabled ? _vectorPatch : _patch;
    private Image ActiveUniversal => SimsBitmapFont.SharpTextEnabled ? _vectorUniversal : _universal;
    private Image ActivePeople => SimsBitmapFont.SharpTextEnabled ? _vectorPeople : _people;
    private readonly Image _iconFrame;
    private readonly Image _thumbTemplate;
    private readonly Image _catalogUnknown;
    private readonly Image _cancel;
    private readonly Image _greenBars;
    private readonly Image _vectorGreenBars;
    private readonly Image _redBars;
    private readonly Image _vectorRedBars;
    private readonly Image[] _buttons;
    private readonly Image?[] _vectorButtons;

    private Image ActiveGreenBars => SimsBitmapFont.SharpTextEnabled ? _vectorGreenBars : _greenBars;
    private Image ActiveRedBars => SimsBitmapFont.SharpTextEnabled ? _vectorRedBars : _redBars;
    private Image[] _labels = [];
    private Image[] _petLabels = [];
    private Image[] _traitLabels = [];
    private Image[] _petTraitLabels = [];
    private readonly Image _personalityBackground;
    private readonly Image _personalityBars;
    private readonly Image _houseBarsOff;

    private readonly Image _livePopupBackground;
    private readonly Image _bevelDivider;
    private double? _livePopupAnchorMidX;
    private int? _personalityPopupTrait;
    private int? _housePopupIndex;
    private string? _relationshipPopupText;
    private int? _careerSummaryPopupIndex;
    private int? _motivePopupIndex;
    private bool _zodiacPopupActive;
    private Image? _personalityPopupTitle;
    private Image[] _personalityPopupLines = [];
    private (Image Image, GameplayPanelState.Image Reference)? _personalityPopupPortrait;
    private readonly SimsBitmapFont _careerFont;
    private readonly SimsBitmapFont _clockFont;
    private readonly SimsBitmapFont _zodiacFont;
    private Image[] _skillLabels = [];
    private Image[] _dogSkillLabels = [];
    private Image[] _catSkillLabels = [];
    private readonly Image _skillBars;
    private readonly Image _skillBarsOff;
    private readonly Image _skillHighlight;
    private readonly Image _jobIcons;
    private readonly Image _vectorJobIcons;
    private readonly Image _salaryButton;
    private readonly Image _vectorSalaryButton;
    private readonly Image _performanceButton;
    private readonly Image _jobFriendSmiley;
    private readonly Image _vectorJobFriendSmiley;
    private readonly Image _fameBars;
    private readonly Image _fameBarsOff;
    private readonly Image _fameIcons;
    private readonly Image _vectorFameIcons;
    private readonly Image _fameFriendSmiley;
    private readonly Image _vectorFameFriendSmiley;
    private Image ActiveJobIcons => SimsBitmapFont.SharpTextEnabled ? _vectorJobIcons : _jobIcons;
    private Image ActiveSalaryButton => SimsBitmapFont.SharpTextEnabled ? _vectorSalaryButton : _salaryButton;
    private Image ActiveJobFriendSmiley => SimsBitmapFont.SharpTextEnabled ? _vectorJobFriendSmiley : _jobFriendSmiley;
    private Image ActiveFameFriendSmiley => SimsBitmapFont.SharpTextEnabled ? _vectorFameFriendSmiley : _fameFriendSmiley;
    private readonly Image[] _interestIcons;
    private readonly Image _interestBars;
    private readonly Image _interestBackground;
    private readonly Image[] _interestArrows;
    private readonly Image _relationshipUnknown;
    private readonly Image[] _relationshipIcons;
    private readonly Image[] _relationshipFilters;
    private readonly Image[] _vectorRelationshipFilters;
    private readonly Image[] _inventorySorts;
    private readonly Image[] _vectorInventorySorts;
    private readonly Image _missingInventoryIcon;
    private readonly bool _asianInventoryLayout;
    private int? _hoveredInventoryTile;
    private double? _inventoryHoverStartedAt;
    private (string Text, Image Image)? _inventoryTooltipCache;
    private OriginalGameplayLayout.PanelGrid _grid = new(800);
    private Dictionary<string, Image> _resources = new(StringComparer.Ordinal);
    private List<Part> _body = [];
    private List<Part> _statusBody = [];
    private List<(GameplayPanelState.Action Action, Image? Icon)> _queued = [];
    private Dictionary<string, ActionAnimation> _actionAnimations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _optimisticCancelledTokens = new(StringComparer.Ordinal);
    private CGPoint? _pendingPieChoiceOrigin;
    private Image? _needsTitleImage;
    private readonly Dictionary<int, OriginalGameplayLayout.MotiveRateTracker[]> _motiveTrackersBySim = [];
    private readonly Dictionary<int, int[]> _lastSampledMotivesBySim = [];
    private int? _lastSampledSimMinute;
    private HashSet<int> _trackedHouseholdSimIds = [];
    private int? _trackedSelectedSimId;
    private readonly OriginalGameplayLayout.MotiveArrowsState[] _motiveArrowsStates;
    private readonly OriginalGameplayLayout.MotiveBarState[] _motiveBarStates;
    private readonly (MotiveBarCacheKey Key, Image Image)?[] _cachedMotiveBars;
    private readonly (MotiveArrowStripCacheKey Key, Image Image)?[] _cachedMotiveArrowStrips;
    private string? _resourceRoot;
    private readonly Dictionary<string, Image> _indicatorImages = new(StringComparer.Ordinal);
    private string? _indicatorKey;
    private readonly Dictionary<CycleIndicator.Hover, (string Text, Image Image)> _seasonTooltips = [];
    private (Image Window, Image Strip, Image Drum)? _reel;
    private (double From, double To, int Frame)? _reelSpin;
    private (Image Image, double ExpiresAt)? _seasonBanner;
    private CycleIndicator.Hover? _indicatorHovered;
    private double? _indicatorHoverStartedAt;
    private (int Index, CGRect Rect)? _panelTip;
    private readonly Dictionary<int, Image> _panelTips = [];
    private (string Text, CGRect Rect)? _hoveredPeopleTip;
    private readonly Dictionary<string, Image> _peopleTips = new(StringComparer.Ordinal);
    private int? _hoveredNeed;
    private (int Id, string Name, CGRect Rect)? _hoveredPerson;
    private double? _personHoverStartedAt;
    private readonly Dictionary<string, Image> _personTips = new(StringComparer.Ordinal);
    private string? _suppressedTooltipIdentity;
    private bool _quickTips = true;
    private HashSet<UITexture>? _hoverTextures;
    private (string Text, Image Image)? _cachedClockText;
    private (string Text, Image Image)? _cachedFriendsText;
    private (string Text, Image Image)? _cachedBudgetText;

    private readonly CGPoint[] _buttonPositions =
    [
        new(421, 81),
        new(489, 54),
        new(489, 85),
        new(489, 116),
        new(457, 85),
        new(457, 54),
        OriginalGameplayLayout.InventoryButton
    ];

    public UIDevice Device { get; }
    public SimsBitmapFont Font { get; }
    public int? SkillPopupIndex { get; private set; }
    public int? RelationshipPopupId { get; private set; }
    public int InventoryPage { get; private set; }
    public OriginalGameplayLayout.InventorySort InventorySort { get; private set; } = OriginalGameplayLayout.InventorySort.Gift;
    public int RelationshipPage { get; private set; }
    public OriginalGameplayLayout.RelationshipFilter RelationshipFilter { get; private set; } = OriginalGameplayLayout.RelationshipFilter.All;
    public int InterestPage { get; private set; }
    /// <summary>The selected Sim head sprite for the pie menu center, arranged in nine frames: facing the
    /// player, followed by each radial spoke clockwise from the top.</summary>
    public Image? PieHead { get; private set; }
    public bool InterfaceEffects { get; set; } = true;
    public GameplayPanelState? State { get; private set; }
    public Subpanel CurrentSubpanel { get; private set; } = Subpanel.Needs;
    public int? HoveredAction { get; set; }
    public CGPoint? HoveredPanelPoint { get; set; }
    public PluginEnvironment? Environment { get; set; }

    /// <summary>The tutorial controller checks the active popup client's name rather than the selected
    /// subpanel.</summary>
    public string? TutorialPopupName
    {
        get
        {
            if (RelationshipPopupId is not null) return "rel";
            if (_motivePopupIndex is not null || CurrentSubpanel == Subpanel.Needs) return "motives";
            if (_careerSummaryPopupIndex == 0) return "job";
            if (_careerSummaryPopupIndex == 2) return "perf";
            if (_careerSummaryPopupIndex is 1 or 3) return "summary";
            if (_personalityPopupTrait is not null || _zodiacPopupActive || CurrentSubpanel == Subpanel.Personality) return "per.ity";
            if (SkillPopupIndex is not null) return "skill";
            if (_housePopupIndex is not null) return "house";
            if (CurrentSubpanel == Subpanel.Career) return "job";
            return null;
        }
    }

    public CycleIndicator.Hover? IndicatorHovered
    {
        get => _indicatorHovered;
        set
        {
            if (_indicatorHovered != value)
            {
                _indicatorHoverStartedAt = null;
            }

            _indicatorHovered = value;
        }
    }

    public (int Index, CGRect Rect)? PanelTip
    {
        get => _panelTip;
        set
        {
            if (_panelTip?.Index != value?.Index)
            {
                _indicatorHoverStartedAt = null;
            }

            _panelTip = value;
        }
    }

    public (string Text, CGRect Rect)? HoveredPeopleTip
    {
        get => _hoveredPeopleTip;
        set
        {
            if (!string.Equals(_hoveredPeopleTip?.Text, value?.Text, StringComparison.Ordinal))
            {
                _indicatorHoverStartedAt = null;
            }

            _hoveredPeopleTip = value;
        }
    }

    public int? HoveredNeed
    {
        get => _hoveredNeed;
        set
        {
            if (_hoveredNeed == value)
            {
                return;
            }

            _hoveredNeed = value;
            _indicatorHoverStartedAt = null;
            UpdateMotivePopup();
        }
    }

    public (int Id, string Name, CGRect Rect)? HoveredPerson
    {
        get => _hoveredPerson;
        set
        {
            if (_hoveredPerson?.Id != value?.Id || !string.Equals(_hoveredPerson?.Name, value?.Name, StringComparison.Ordinal))
            {
                _personHoverStartedAt = null;
            }

            _hoveredPerson = value;
        }
    }

    public bool QuickTips
    {
        get => _quickTips;
        set
        {
            if (_quickTips == value)
            {
                return;
            }

            _quickTips = value;
            _inventoryHoverStartedAt = null;
            _indicatorHoverStartedAt = null;
            _personHoverStartedAt = null;
        }
    }

    public bool ExpandsControlPanelToCanvas => true;

    private Image[] ScrollArrows => _interestArrows;
    private SimsBitmapFont LivePopupBodyFont => _asianInventoryLayout ? Font : _clockFont;
    private int StripRelationshipCapacity => _grid.RelationshipCapacity;
    private int StripInventoryCapacity => OriginalGameplayLayout.InventoryCapacity(_grid.Width);
    private int StripInterestCapacity => _grid.InterestCapacity;

    public OriginalGameplayOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(strings);

        Device = device;
        Font = font;
        _strings = strings;
        _asianInventoryLayout = OriginalGameplayLayout.UsesAsianInventoryLayout(content.Language.Code);

        int motiveCount = OriginalGameplayLayout.Motives.Count;
        _motiveArrowsStates = new OriginalGameplayLayout.MotiveArrowsState[motiveCount];
        _motiveBarStates = new OriginalGameplayLayout.MotiveBarState[motiveCount];
        for (int i = 0; i < motiveCount; i++)
        {
            _motiveArrowsStates[i] = new OriginalGameplayLayout.MotiveArrowsState();
            _motiveBarStates[i] = new OriginalGameplayLayout.MotiveBarState();
        }

        _cachedMotiveBars = new (MotiveBarCacheKey, Image)?[motiveCount];
        _cachedMotiveArrowStrips = new (MotiveArrowStripCacheKey, Image)?[motiveCount];

        Image Bitmap(string asset)
        {
            var bmp = new SimsBitmap(content.Read("UIGraphics/cpanel/" + asset));
            return CreateImage(device, bmp.Width, bmp.Height, bmp.Rgba, magenta: true);
        }

        _patch = Bitmap("Backgrounds/LivePatch.bmp");
        try { _vectorPatch = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFittedPanel("livepatch", scale: 8)); } catch { _vectorPatch = _patch; }
        var baseTarga = new SimsTarga(content.Read("UIGraphics/cpanel/Backgrounds/UniversalBack.tga"));
        _universal = CreateImage(device, baseTarga.Width, baseTarga.Height, baseTarga.Rgba);
        try { _vectorUniversal = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFittedPanel("universalback", scale: 8)); } catch { _vectorUniversal = _universal; }
        _vectorLcdFriendSmiley = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderLcdGreenSmiley(10, scale: 4));
        _gadget = Bitmap("Backgrounds/LiveGadget.bmp");
        _vectorGadget = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderLiveGadget(scale: 8));
        _people = Bitmap("People/PeopleTemplate.bmp");
        _vectorPeople = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderPeopleTemplateStrip(45, scale: 4));
        _unknown = Bitmap("Buttons/UnknownFace.bmp");

        var iconFrameBitmap = new SimsBitmap(content.Read("UIGraphics/cpanel/Buttons/IconFrame.bmp"));
        _iconFrame = CreateImage(device, iconFrameBitmap.Width, iconFrameBitmap.Height, iconFrameBitmap.Rgba, magenta: true, blue: true);

        var thumbTemplateBitmap = new SimsBitmap(content.Read("UIGraphics/cpanel/Catalog/ThumbTemplate.bmp"));
        _thumbTemplate = CreateImage(
            device,
            thumbTemplateBitmap.Width,
            thumbTemplateBitmap.Height,
            MaskIconFrameCorners(thumbTemplateBitmap.Rgba, thumbTemplateBitmap.Width, thumbTemplateBitmap.Height, 4, 1),
            magenta: true);

        var catalogUnknownBitmap = new SimsBitmap(content.Read("UIGraphics/cpanel/CatalogUnknown.bmp"));
        _catalogUnknown = CreateImage(
            device,
            catalogUnknownBitmap.Width,
            catalogUnknownBitmap.Height,
            MaskIconFrameCorners(catalogUnknownBitmap.Rgba, catalogUnknownBitmap.Width, catalogUnknownBitmap.Height, 2, 1),
            magenta: true);

        var cancelBitmap = new SimsBitmap(content.Read("UIGraphics/Other/QueueCancel.bmp"));
        _cancel = CreateImage(device, cancelBitmap.Width, cancelBitmap.Height, cancelBitmap.Rgba, magenta: true);

        _greenBars = Bitmap("Greenbars.bmp");
        _vectorGreenBars = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderLiveGadgetMoodBars(isGreen: true, scale: 8));
        _redBars = Bitmap("Redbars.bmp");
        _vectorRedBars = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderLiveGadgetMoodBars(isGreen: false, scale: 8));

        string[] buttonAssets = ["Mood", "Relationship", "Job", "House", "Personality", "Interest", "Gift"];
        _buttons = new Image[buttonAssets.Length];
        _vectorButtons = new Image?[buttonAssets.Length];
        for (int i = 0; i < buttonAssets.Length; i++)
        {
            var rawBmp = new SimsBitmap(content.Read("UIGraphics/cpanel/Buttons/" + buttonAssets[i] + ".bmp"));
            _buttons[i] = CreateImage(device, rawBmp.Width, rawBmp.Height, rawBmp.Rgba, magenta: true);
            int bw = Math.Max(1, rawBmp.Width / 4);
            int bh = Math.Max(1, rawBmp.Height);
            if (SimsVectorUiKit.TryRenderSubpanelTabStrip(buttonAssets[i], bw, bh, scale: 4) is { } vecTab)
            {
                _vectorButtons[i] = SimsVectorUiKit.ToOverlayImage(vecTab);
            }
            else
            {
                // Strip baked-in 1x LiveGadget.bmp grey outer frame fringe so 1x fallback buttons never leave a square halo on vector LiveGadget
                byte[] trimmed = MaskSubpanelOuterFringe(rawBmp.Rgba, rawBmp.Width, rawBmp.Height, columns: 4);
                _vectorButtons[i] = CreateImage(device, rawBmp.Width, rawBmp.Height, trimmed, magenta: false);
            }
        }

        _careerFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_07.ffn"));
        _clockFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_08.ffn"));
        _zodiacFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_12.ffn"));

        RebuildStaticLabels();

        _personalityBackground = Bitmap("Backgrounds/PersBkg.bmp");
        _personalityBars = Bitmap("HouseSubBars.bmp");
        _houseBarsOff = Bitmap("Backgrounds/HouseSubBarsOff.BMP");
        _livePopupBackground = Bitmap("Backgrounds/PopupInfoTiles.bmp");
        _bevelDivider = TryBitmap(Bitmap, "Backgrounds/OptionsToothpkVert.bmp")
            ?? CreateImage(device, 2, 1, [0, 0, 42, 255, 74, 90, 148, 255]);

        _skillBars = Bitmap("JobSubBars.bmp");
        _skillBarsOff = Bitmap("Backgrounds/JobSubBarsOff.bmp");
        _skillHighlight = CreateImage(device, 1, 1, [0xc3, 0xcd, 0xcd, 0xff]);
        _jobIcons = Bitmap("Buttons/JobIconMultiButton.BMP");
        _vectorJobIcons = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderJobIconMultiButton(scale: 4));
        _salaryButton = Bitmap("Buttons/SalaryButton.bmp");
        _vectorSalaryButton = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderSalaryButtonStrip(scale: 4));
        _performanceButton = Bitmap("Buttons/PerformanceButton.bmp");
        _jobFriendSmiley = Bitmap("Buttons/JobFriendSmiley.bmp");
        _vectorJobFriendSmiley = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFriendSmileyStrip(10, 11, scale: 4));
        _fameBars = Bitmap("FameSubBars.bmp");
        _fameBarsOff = Bitmap("Backgrounds/FameSubBarsOff.bmp");
        _fameIcons = Bitmap("Buttons/FameIconMultiButton.BMP");
        _vectorFameIcons = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFameIconMultiButton(scale: 4));
        _fameFriendSmiley = Bitmap("Buttons/FameFriendSmiley.bmp");
        _vectorFameFriendSmiley = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFriendSmileyStrip(22, 20, scale: 4));

        _interestIcons = new Image[OriginalGameplayLayout.Interests.Count];
        for (int i = 0; i < OriginalGameplayLayout.Interests.Count; i++)
        {
            _interestIcons[i] = Bitmap("Buttons/Interest" + OriginalGameplayLayout.Interests[i] + ".bmp");
        }

        _interestBars = Bitmap("InterestBars.bmp");
        _interestBackground = Bitmap("Backgrounds/InterestBarsBackground.bmp");
        _interestArrows = [Bitmap("Buttons/ScrollLeft.bmp"), Bitmap("Buttons/ScrollRight.bmp")];
        _relationshipUnknown = Bitmap("People/UnknownRel.bmp");
        _relationshipIcons = [Bitmap("Buttons/Smiley.bmp"), Bitmap("Buttons/Heart.bmp"), Bitmap("Buttons/HeartDeep.bmp")];
        _inventorySorts =
        [
            Bitmap("Buttons/InventoryMagicSort.bmp"),
            Bitmap("Buttons/InventoryOtherSort.bmp"),
            Bitmap("Buttons/InventoryGiftSort.bmp")
        ];
        _vectorInventorySorts =
        [
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderInventorySortStrip(0, scale: 4)),
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderInventorySortStrip(1, scale: 4)),
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderInventorySortStrip(2, scale: 4)),
        ];
        _relationshipFilters =
        [
            Bitmap("Buttons/RelFamilySort.bmp"),
            Bitmap("Buttons/RelFriendSort.bmp"),
            Bitmap("Buttons/RelStarSort.bmp"),
            Bitmap("Buttons/RelAllSort.bmp")
        ];
        _vectorRelationshipFilters =
        [
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderRelationshipFilterStrip(0, scale: 4)),
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderRelationshipFilterStrip(1, scale: 4)),
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderRelationshipFilterStrip(2, scale: 4)),
            SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderRelationshipFilterStrip(3, scale: 4)),
        ];
        _missingInventoryIcon = Bitmap("Catalog/BackButtons/BackCTwo.bmp");
    }

    private void RebuildStaticLabels()
    {
        int motiveCount = OriginalGameplayLayout.Motives.Count;
        _labels = new Image[motiveCount];
        _petLabels = new Image[motiveCount];
        for (int i = 0; i < motiveCount; i++)
        {
            string name = OriginalGameplayLayout.Motives[i];
            _labels[i] = Text(_strings.MotiveLabel(i, false, name), _clockFont, Device);
            _petLabels[i] = Text(_strings.MotiveLabel(i, true, name), _clockFont, Device);
        }

        int traitCount = OriginalGameplayLayout.Traits.Count;
        _traitLabels = new Image[traitCount];
        _petTraitLabels = new Image[traitCount];
        for (int i = 0; i < traitCount; i++)
        {
            string name = OriginalGameplayLayout.Traits[i];
            _traitLabels[i] = Text(_strings.PersonalityTraitLabel(i, false, name), _clockFont, Device);
            string petFallback = i == 4 ? "Friendly" : name;
            _petTraitLabels[i] = Text(_strings.PersonalityTraitLabel(i, true, petFallback), _clockFont, Device);
        }

        _skillLabels = new Image[OriginalGameplayLayout.Skills.Count];
        for (int i = 0; i < OriginalGameplayLayout.Skills.Count; i++)
        {
            _skillLabels[i] = Text(_strings.CareerSkill(OriginalGameplayLayout.Skills[i]), _careerFont, Device);
        }

        string[] dogFallbacks = ["House Breaking", "Obedience", "Tricks"];
        string[] catFallbacks = ["Hunting", "Obedience", "Tricks"];
        _dogSkillLabels = new Image[OriginalGameplayLayout.PetSkills.Count];
        _catSkillLabels = new Image[OriginalGameplayLayout.PetSkills.Count];
        for (int i = 0; i < OriginalGameplayLayout.PetSkills.Count; i++)
        {
            _dogSkillLabels[i] = Text(_strings.PetSkillTitle(i, false, dogFallbacks[i]), _careerFont, Device);
            _catSkillLabels[i] = Text(_strings.PetSkillTitle(i, true, catFallbacks[i]), _careerFont, Device);
        }
    }

    public void RefreshText()
    {
        RebuildStaticLabels();
        _seasonTooltips.Clear();
        _panelTips.Clear();
        _peopleTips.Clear();
        _personTips.Clear();
        _inventoryTooltipCache = null;
        _needsTitleImage = null;
        _cachedClockText = null;
        _cachedFriendsText = null;
        _cachedBudgetText = null;
        _personalityPopupTitle = null;
        _personalityPopupLines = [];

        if (_motivePopupIndex is not null)
        {
            UpdateMotivePopup();
        }
        else if (RelationshipPopupId is not null &&
                 State?.Selected?.Relationships is { } rels &&
                 rels.FirstOrDefault(r => r.NeighborId == RelationshipPopupId) is { } target)
        {
            _relationshipPopupText = null;
            UpdateRelationshipPopup(target);
        }

        if (State is not null && !string.IsNullOrEmpty(_resourceRoot))
        {
            Update(State, _resourceRoot);
        }
    }

    public static Image CreateImage(UIDevice device, SimsBitmapFont.HiDpiRaster raster) =>
        CreateImage(device, raster.PixelWidth, raster.PixelHeight, raster.Rgba, new CGSize(raster.LogicalWidth, raster.LogicalHeight));

    public static Image CreateImage(
        UIDevice device,
        int width,
        int height,
        byte[] pixels,
        bool magenta = false,
        bool blue = false,
        bool decontaminateEdges = false) =>
        CreateImage(device, width, height, pixels, new CGSize(width, height), magenta, blue, decontaminateEdges);

    public static Image CreateImage(
        UIDevice device,
        int width,
        int height,
        byte[] pixels,
        CGSize logicalSize,
        bool magenta = false,
        bool blue = false,
        bool decontaminateEdges = false)
    {
        _ = device;
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
        {
            throw new ContentError("Cannot allocate gameplay panel texture");
        }

        var rgba = SpritePixels.PremultipliedSRGB(
            pixels,
            removeMagenta: magenta,
            removeBlue: blue,
            decontaminateEdges: decontaminateEdges,
            width: width,
            height: height);
        return new Image(new UITexture(width, height, rgba), logicalSize);
    }

    /// <summary>
    /// Removes the static 1x LiveGadget.bmp lavender-grey perimeter pixels baked around the rounded border
    /// of 4-state subpanel buttons (e.g. Mood.bmp 32x39, Job/House/Interest/Gift 27x30) by detecting outer-2px
    /// pixels whose Cyan-minus-Red delta does not change between State 0 (Silver) and State 1 (Selected Cyan).
    /// </summary>
    private static byte[] MaskSubpanelOuterFringe(byte[] pixels, int width, int height, int columns)
    {
        var masked = (byte[])pixels.Clone();
        int cols = Math.Max(1, columns);
        if (cols < 2)
            return masked;

        int cellW = width / cols;
        for (int cy = 0; cy < height; cy++)
        {
            for (int cx = 0; cx < cellW; cx++)
            {
                if (cx > 1 && cx < cellW - 2 && cy > 1 && cy < height - 2)
                    continue;

                int i0 = (cy * width + cx) * 4;
                int i1 = (cy * width + cellW + cx) * 4;
                int cyanDelta0 = masked[i0 + 1] - masked[i0];
                int cyanDelta1 = masked[i1 + 1] - masked[i1];
                if (Math.Abs(cyanDelta1 - cyanDelta0) <= 10)
                {
                    for (int col = 0; col < cols; col++)
                    {
                        int off = (cy * width + col * cellW + cx) * 4;
                        masked[off] = 0;
                        masked[off + 1] = 0;
                        masked[off + 2] = 0;
                        masked[off + 3] = 0;
                    }
                }
            }
        }

        return masked;
    }

    /// <summary>Masks square icon corners by blitting the icon frame using blue transparency over a
    /// magenta-keyed composite buffer. Also preserves the four inner bevel corner pixels surrounding the
    /// portrait frame.</summary>
    public static byte[] MaskIconFrameCorners(byte[] pixels, int width, int height, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int cols = Math.Max(1, columns);
        int rws = Math.Max(1, rows);
        int cellWidth = width / cols;
        int cellHeight = height / rws;

        if (cellWidth == 35 && cellHeight == 41)
        {
            var masked = (byte[])pixels.Clone();
            (int X, int Y)[] corners = [(0, 0), (34, 0), (0, 40), (34, 40)];
            for (int row = 0; row < rws; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    foreach (var (cx, cy) in corners)
                    {
                        int offset = ((row * 41 + cy) * width + (col * 35 + cx)) * 4;
                        if (offset >= 0 && offset + 3 < masked.Length)
                        {
                            masked[offset] = 0;
                            masked[offset + 1] = 0;
                            masked[offset + 2] = 0;
                            masked[offset + 3] = 0;
                        }
                    }
                }
            }

            return masked;
        }

        if (cellWidth != 45 || cellHeight != 45)
        {
            return pixels;
        }

        var maskedCell = (byte[])pixels.Clone();
        for (int y = 0; y < height; y++)
        {
            int cy = y % 45;
            int dy = Math.Min(cy, 44 - cy);
            if (dy >= 3)
            {
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                int cx = x % 45;
                if (dy + Math.Min(cx, 44 - cx) < 3)
                {
                    int offset = (y * width + x) * 4;
                    if (offset >= 0 && offset + 3 < maskedCell.Length)
                    {
                        maskedCell[offset] = 0;
                        maskedCell[offset + 1] = 0;
                        maskedCell[offset + 2] = 0;
                        maskedCell[offset + 3] = 0;
                    }
                }
            }
        }

        return maskedCell;
    }

    public static string Grouped(int value)
    {
        string digits = Math.Abs((long)value).ToString(CultureInfo.InvariantCulture);
        int lead = digits.Length % 3;
        string result = lead > 0 ? digits[..lead] : string.Empty;
        for (int index = lead; index < digits.Length; index += 3)
        {
            if (result.Length > 0)
            {
                result += ",";
            }

            result += digits.Substring(index, 3);
        }

        return (value < 0 ? "-" : string.Empty) + (result.Length == 0 ? "0" : result);
    }

    public static Image Text(string value, SimsBitmapFont font, UIDevice device) =>
        Text(value, font, device, (255, 255, 255));

    public static Image Text(string value, SimsBitmapFont font, UIDevice device, byte r, byte g, byte b) =>
        Text(value, font, device, (r, g, b));

    public static Image Text(string value, SimsBitmapFont font, UIDevice device, (byte R, byte G, byte B) color)
    {
        ArgumentNullException.ThrowIfNull(font);
        return CreateImage(device, font.RasterizeHiDpi(value, color, logicalMargin: 0));
    }

    public CGRect FamilyFriendsRect(int count)
    {
        double textWidth = _clockFont.TextWidth(count.ToString(CultureInfo.InvariantCulture));
        double fontHeight = _clockFont.LineHeight;
        return OriginalGameplayLayout.FamilyFriendsButton(FundsRect, fontHeight, textWidth);
    }

    public void RecordPieChoice(CGPoint point)
    {
        _pendingPieChoiceOrigin = new CGPoint(point.X - 16, point.Y - 16);
    }

    public void Update(GameplayPanelState next, string root)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentException.ThrowIfNullOrEmpty(root);

        next.Validate();
        if (State?.SelectedSim != next.SelectedSim)
        {
            ClearLivePopupState();
        }

        if (!Supports(CurrentSubpanel, next))
        {
            CurrentSubpanel = Subpanel.Needs;
        }

        string resolvedRoot = ResolveRealPath(root);
        double now = MonotonicNowSeconds();
        UpdateMotiveTracking(next, resolvedRoot, (ulong)(Math.Max(0.0, now) * 1000.0));

        var used = new HashSet<string>(StringComparer.Ordinal);
        Image? LoadResource(GameplayPanelState.Image? reference)
        {
            if (reference is null)
            {
                return null;
            }

            reference.Validate();
            used.Add(reference.File);
            if (_resources.TryGetValue(reference.File, out var cached))
            {
                return cached;
            }

            string resolvedFile = ResolveRealPath(Path.Combine(resolvedRoot, reference.File));
            string? parentDir = Path.GetDirectoryName(resolvedFile);
            var fileInfo = new FileInfo(resolvedFile);
            if (!PathsEqual(parentDir, resolvedRoot) || !fileInfo.Exists || fileInfo.Length > 4L * 1024 * 1024)
            {
                throw new ContentError("Gameplay image escapes resource directory or exceeds size limit");
            }

            var bitmap = new SimsBitmap(File.ReadAllBytes(resolvedFile));
            if (bitmap.Width > 512 || bitmap.Height > 512 ||
                reference.Columns <= 0 || reference.Rows <= 0 ||
                bitmap.Width % reference.Columns != 0 || bitmap.Height % reference.Rows != 0)
            {
                throw new ContentError("Invalid gameplay image dimensions");
            }

            var masked = bitmap.HasAlpha
                ? bitmap.Rgba
                : MaskIconFrameCorners(bitmap.Rgba, bitmap.Width, bitmap.Height, reference.Columns, reference.Rows);
            var image = CreateImage(
                Device,
                bitmap.Width,
                bitmap.Height,
                masked,
                magenta: !bitmap.HasAlpha,
                decontaminateEdges: bitmap.HasAlpha);
            _resources[reference.File] = image;
            return image;
        }

        var parts = new List<Part>
        {
            new(ActiveUniversal, new CGRect(0, -33, 220, 183)),
            new(ActivePatch, new CGRect(7, -3, _patch.Size.Width, _patch.Size.Height)),
            new(ActiveGadget, new CGRect(413, 50, 108, 100))
        };
        if (SimsBitmapFont.SharpTextEnabled && ActiveUniversal.Texture != _universal.Texture)
        {
            parts.Add(new Part(_vectorLcdFriendSmiley, FamilyFriendsSmileyRect));
        }

        bool selectedIsPet = ((next.Selected?.Gender ?? 0) & 6) != 0;
        for (int index = 0; index < _buttons.Length; index++)
        {
            if ((selectedIsPet || next.Selected is null) && index is 5 or 6)
            {
                continue;
            }

            var image = SimsBitmapFont.SharpTextEnabled && _vectorButtons[index] is { } vBtn ? vBtn : _buttons[index];
            bool enabled = false;
            foreach (var panel in MainTabSubpanels)
            {
                if (panel.ButtonIndex() == index && Supports(panel, next))
                {
                    enabled = true;
                    break;
                }
            }

            float column = !enabled ? 0.75f : index == CurrentSubpanel.ButtonIndex() ? 0.25f : 0f;
            parts.Add(new Part(image, ButtonRect(index), new Vector4(column, 0f, 0.25f, 1f)));
        }

        int householdCount = Math.Min(OriginalGameplayLayout.MaxHouseholdPortraits, next.Household.Count);
        for (int index = 0; index < householdCount; index++)
        {
            var sim = next.Household[index];
            var portraitRect = OriginalGameplayLayout.Portrait(index);
            parts.Add(new Part(ActivePeople, portraitRect, new Vector4(sim.Id == next.SelectedSim ? 0.25f : 0f, 0f, 0.25f, 1f)));
            if (sim.Portrait is { } reference && LoadResource(reference) is { } portraitImage)
            {
                var faceSize = new CGSize(portraitImage.Size.Width / reference.Columns, portraitImage.Size.Height / reference.Rows);
                int expression = 0;
                parts.Add(new Part(
                    portraitImage,
                    new CGRect(
                        portraitRect.X + portraitRect.Width / 2.0 - faceSize.Width / 2.0,
                        portraitRect.Y + portraitRect.Height / 2.0 - faceSize.Height / 2.0,
                        faceSize.Width,
                        faceSize.Height),
                    new Vector4((float)expression / reference.Columns, 0f, 1f / reference.Columns, 1f / reference.Rows)));
            }
            else
            {
                parts.Add(new Part(_unknown, portraitRect, new Vector4(0f, 0f, 0.25f, 1f / 3f)));
            }
        }

        var bay = OriginalGameplayLayout.CenteredBay(_grid.Width);
        if (next.Selected is not null || (CurrentSubpanel == Subpanel.House && next.House is not null))
        {
            AppendBayDividers(bay, parts);
        }

        if (next.Selected is { } selected)
        {
            int mood = Math.Min(5, Math.Max(-5, (int)((selected.Mood + 100) * 0.05472637f - 5f)));
            bool vecBars = SimsBitmapFont.SharpTextEnabled;
            double barTexH = vecBars ? 26.0 : 25.0;
            if (mood > 0)
            {
                double topOff = (5 - mood) * 5.0;
                double drawH = barTexH - topOff;
                parts.Add(new Part(ActiveGreenBars, new CGRect(425, 53 + topOff, 27, drawH), new Vector4(0f, (float)(topOff / barTexH), 1f, (float)(drawH / barTexH))));
            }
            else if (mood < 0)
            {
                double extraTop = vecBars ? 1.0 : 0.0;
                double drawH = -mood * 5.0 + extraTop;
                parts.Add(new Part(ActiveRedBars, new CGRect(425, 123 - extraTop, 27, drawH), new Vector4(0f, 0f, 1f, (float)(drawH / barTexH))));
            }
            else
            {
                double gH = vecBars ? 3.0 : 2.0;
                double rH = vecBars ? 3.0 : 2.0;
                parts.Add(new Part(ActiveGreenBars, new CGRect(425, 76, 27, gH), new Vector4(0f, (float)((barTexH - gH) / barTexH), 1f, (float)(gH / barTexH))));
                parts.Add(new Part(ActiveRedBars, new CGRect(425, 123 - (vecBars ? 1.0 : 0.0), 27, rH), new Vector4(0f, 0f, 1f, (float)(rH / barTexH))));
            }

            if (CurrentSubpanel == Subpanel.Needs)
            {
                if (_needsTitleImage is not { } title)
                {
                    title = Text(_strings.NeedsTitle, Font, Device);
                    _needsTitleImage = title;
                }

                var titlePoint = OriginalGameplayLayout.NeedsHeaderOrigin(_grid.Width, _asianInventoryLayout);
                parts.Add(new Part(title, new CGRect(titlePoint.X, titlePoint.Y, title.Size.Width, title.Size.Height)));
                var activeLabels = ((selected.Gender ?? 0) & 6) != 0 ? _petLabels : _labels;
                for (int index = 0; index < activeLabels.Length; index++)
                {
                    var label = activeLabels[index];
                    var needRect = OriginalGameplayLayout.NeedRect(index, _grid.Width);
                    parts.Add(new Part(
                        label,
                        new CGRect(
                            needRect.X + needRect.Width / 2.0 - label.Size.Width / 2.0,
                            needRect.Y + (10 - _clockFont.LineHeight) / 2.0,
                            label.Size.Width,
                            label.Size.Height)));
                }
            }
            else if (CurrentSubpanel == Subpanel.House && next.House is { } houseValues)
            {
                AppendHouseParts(houseValues, parts);
            }
            else if (CurrentSubpanel == Subpanel.Relationships && selected.Relationships is { } relValues)
            {
                var title = Text(_strings.RelationshipsTitle, _careerFont, Device);
                var titlePoint = StripHeaderOrigin();
                parts.Add(new Part(title, new CGRect(titlePoint.X, titlePoint.Y, title.Size.Width, title.Size.Height)));
                var relationships = OriginalGameplayLayout.Relationships(relValues, RelationshipFilter);
                int capacity = StripRelationshipCapacity;
                RelationshipPage = Math.Min(RelationshipPage, _grid.PageCount(relationships.Count, capacity) - 1);

                var allFilters = Enum.GetValues<OriginalGameplayLayout.RelationshipFilter>();
                var activeRelFilters = SimsBitmapFont.SharpTextEnabled ? _vectorRelationshipFilters : _relationshipFilters;
                for (int index = 0; index < allFilters.Length; index++)
                {
                    parts.Add(new Part(
                        activeRelFilters[index],
                        RelationshipFilterRect(index),
                        new Vector4(allFilters[index] == RelationshipFilter ? 0.25f : 0f, 0f, 0.25f, 1f)));
                }

                int slot = 0;
                foreach (var relationship in relationships.Skip(RelationshipPage * capacity).Take(capacity))
                {
                    var slotRect = RelationshipSlotRect(slot++);
                    var image = LoadResource(relationship.Portrait) ?? _relationshipUnknown;
                    int columns = relationship.Portrait?.Columns ?? 5;
                    var faceSize = new CGSize(image.Size.Width / columns, image.Size.Height);
                    int expression = Math.Min(columns - 1, OriginalGameplayLayout.RelationshipExpression(relationship.Lifetime));
                    parts.Add(new Part(
                        image,
                        new CGRect(slotRect.X + slotRect.Width / 2.0 - faceSize.Width / 2.0, slotRect.Y, faceSize.Width, faceSize.Height),
                        new Vector4((float)expression / columns, 0f, 1f / columns, 1f)));

                    var daily = Text(Math.Min(100, Math.Max(-100, relationship.Daily)).ToString(CultureInfo.InvariantCulture), _careerFont, Device);
                    parts.Add(new Part(daily, new CGRect(slotRect.X, slotRect.Y + faceSize.Height - 2.0, daily.Size.Width, daily.Size.Height)));

                    double barY = slotRect.Y + faceSize.Height - 2.0 + _careerFont.LineHeight;
                    (int Value, double Y, int Height)[] bars =
                    [
                        (relationship.Daily, barY, 2),
                        (relationship.Lifetime, barY + 4.0, 4)
                    ];
                    foreach (var (value, y, height) in bars)
                    {
                        var barColors = OriginalGameplayLayout.BarColors(value);
                        int barW = OriginalGameplayLayout.RelationshipWidth(value);
                        var pixels = new byte[40 * height * 4];
                        for (int row = 0; row < height; row++)
                        {
                            for (int column = 0; column < 40; column++)
                            {
                                var rgb = column < barW ? barColors.Green : barColors.Red;
                                bool divider = (40 - barW > 1) && (column == barW - 1);
                                int offset = (row * 40 + column) * 4;
                                pixels[offset] = divider ? (byte)0 : rgb.X;
                                pixels[offset + 1] = divider ? (byte)0 : rgb.Y;
                                pixels[offset + 2] = divider ? (byte)0 : rgb.Z;
                                pixels[offset + 3] = 255;
                            }
                        }

                        var barImg = CreateImage(Device, 40, height, pixels);
                        parts.Add(new Part(barImg, new CGRect(slotRect.X + 2.0, y, 40.0, height)));
                    }

                    var lifetime = Text(Math.Min(100, Math.Max(-100, relationship.Lifetime)).ToString(CultureInfo.InvariantCulture), _careerFont, Device);
                    double valueY = barY + 10.0;
                    parts.Add(new Part(lifetime, new CGRect(slotRect.X + 2.0, valueY, lifetime.Size.Width, lifetime.Size.Height)));

                    double iconY = valueY - 2.0 + _careerFont.LineHeight;
                    if (relationship.Friend)
                    {
                        parts.Add(new Part(_relationshipIcons[0], new CGRect(slotRect.X + 2.0, iconY, 9, 9)));
                    }

                    if (relationship.Love || relationship.Crush)
                    {
                        parts.Add(new Part(_relationshipIcons[relationship.Love ? 2 : 1], new CGRect(slotRect.X + 14.0, iconY, 9, 9)));
                    }
                }

                for (int direction = 0; direction <= 1; direction++)
                {
                    bool available = direction == 0 ? RelationshipPage > 0 : (RelationshipPage + 1) * capacity < relationships.Count;
                    if (available)
                    {
                        parts.Add(new Part(ScrollArrows[direction], RelationshipArrowRect(direction == 1), new Vector4(0f, 0f, 0.25f, 1f)));
                    }
                }
            }
            else if (CurrentSubpanel == Subpanel.Inventory && selected.Inventory is { } invValues)
            {
                var items = OriginalGameplayLayout.InventoryItems(invValues, InventorySort);
                int capacity = StripInventoryCapacity;
                InventoryPage = Math.Min(InventoryPage, _grid.PageCount(items.Count, capacity) - 1);
                var title = Text(_strings.InventoryTitle, _careerFont, Device);
                var titlePoint = StripHeaderOrigin();
                parts.Add(new Part(title, new CGRect(titlePoint.X, titlePoint.Y, title.Size.Width, title.Size.Height)));

                var allSorts = Enum.GetValues<OriginalGameplayLayout.InventorySort>();
                var activeInvSorts = SimsBitmapFont.SharpTextEnabled ? _vectorInventorySorts : _inventorySorts;
                for (int index = 0; index < allSorts.Length; index++)
                {
                    parts.Add(new Part(
                        activeInvSorts[index],
                        InventorySortRect(index),
                        new Vector4(allSorts[index] == InventorySort ? 0.25f : 0f, 0f, 0.25f, 1f)));
                }

                int tileIndex = 0;
                foreach (var item in items.Skip(InventoryPage * capacity).Take(capacity))
                {
                    var tileRect = InventoryTileRect(tileIndex++);
                    double iconHeight;
                    if (item.Icon is { } reference && LoadResource(reference) is { } iconImage)
                    {
                        var iconSize = new CGSize(iconImage.Size.Width / reference.Columns, iconImage.Size.Height / reference.Rows);
                        iconHeight = iconSize.Height;
                        parts.Add(new Part(
                            iconImage,
                            new CGRect(tileRect.X + Math.Floor((tileRect.Width - iconSize.Width) / 2.0), tileRect.Y, iconSize.Width, iconSize.Height),
                            new Vector4(0f, 0f, 1f / reference.Columns, 1f / reference.Rows)));
                    }
                    else
                    {
                        var iconSize = new CGSize(_missingInventoryIcon.Size.Width / 2.0, _missingInventoryIcon.Size.Height);
                        iconHeight = iconSize.Height;
                        parts.Add(new Part(
                            _missingInventoryIcon,
                            new CGRect(tileRect.X + Math.Floor((tileRect.Width - iconSize.Width) / 2.0), tileRect.Y, iconSize.Width, iconSize.Height),
                            new Vector4(0f, 0f, 0.5f, 1f)));
                    }

                    var countImg = Text(item.Count.ToString(CultureInfo.InvariantCulture), _careerFont, Device);
                    parts.Add(new Part(
                        countImg,
                        new CGRect(tileRect.X + Math.Floor((tileRect.Width - countImg.Size.Width) / 2.0), tileRect.Y + iconHeight + 3.0, countImg.Size.Width, countImg.Size.Height)));
                }

                for (int direction = 0; direction <= 1; direction++)
                {
                    bool available = direction == 0 ? InventoryPage > 0 : (InventoryPage + 1) * capacity < items.Count;
                    if (available)
                    {
                        parts.Add(new Part(ScrollArrows[direction], InventoryArrowRect(direction == 1), new Vector4(0f, 0f, 0.25f, 1f)));
                    }
                }
            }
            else if (CurrentSubpanel == Subpanel.Personality && selected.Personality is { } traits)
            {
                bool isPet = ((selected.Gender ?? 0) & 6) != 0;
                var activeTraitLabels = isPet ? _petTraitLabels : _traitLabels;
                int labelWidth = MaxImageWidth(activeTraitLabels);
                var layout = OriginalGameplayLayout.PersonalitySubpanelLayout(_grid.Width, labelWidth, _asianInventoryLayout);
                var header = Text(_strings.PersonalityTitle, Font, Device);
                var headerPoint = OriginalGameplayLayout.InventoryTitle(_asianInventoryLayout);
                parts.Add(new Part(header, new CGRect(layout.HeaderX, headerPoint.Y, header.Size.Width, header.Size.Height)));

                double lw = labelWidth;
                for (int index = 0; index < activeTraitLabels.Length && index < layout.TraitRects.Count; index++)
                {
                    var label = activeTraitLabels[index];
                    var traitRect = layout.TraitRects[index];
                    double barX = traitRect.X + lw + 6.0;
                    double barY = traitRect.Y + Math.Max(0, (_clockFont.LineHeight - 14) / 2);
                    parts.Add(new Part(
                        label,
                        new CGRect(traitRect.X + lw + 3.0 - label.Size.Width, traitRect.Y + Math.Max(0, (14 - _clockFont.LineHeight) / 2), label.Size.Width, label.Size.Height)));
                    parts.Add(new Part(_personalityBackground, new CGRect(barX, barY, 40, 14)));
                    int traitVal = traits.TryGetValue(OriginalGameplayLayout.Traits[index], out var tv) ? tv : 0;
                    double barW = OriginalGameplayLayout.PersonalityWidth(traitVal);
                    if (barW > 0.0)
                    {
                        parts.Add(new Part(_personalityBars, new CGRect(barX, barY - 1.0, barW, 16.0), new Vector4(0f, 0f, (float)barW / 40f, 1f)));
                    }
                }

                if (!isPet && selected.Zodiac is { } stored)
                {
                    int zodiac = OriginalGameplayLayout.Zodiac(traits, stored);
                    int signIndex = zodiac - 1;
                    if (signIndex >= 0 && signIndex < OriginalGameplayLayout.Signs.Count)
                    {
                        string signName = _strings.Sign(signIndex, OriginalGameplayLayout.Signs[signIndex]);
                        double maxSignW = Math.Max(50.0, bay.Bay.MaxX - layout.ZodiacOrigin.X - 4.0);
                        var chosenFont = layout.UsesBottomAlignedZodiac || _zodiacFont.TextWidth(signName) <= maxSignW ? _zodiacFont : Font;
                        var signImg = Text(signName, chosenFont, Device);
                        double signY = layout.UsesBottomAlignedZodiac ? layout.ZodiacOrigin.Y - signImg.Size.Height : layout.ZodiacOrigin.Y;
                        parts.Add(new Part(signImg, new CGRect(layout.ZodiacOrigin.X, signY, signImg.Size.Width, signImg.Size.Height)));
                    }
                }
            }
            else if (CurrentSubpanel == Subpanel.Interests && selected.Interests is { } interestValues && selected.Career is { } interestCareer)
            {
                var indices = OriginalGameplayLayout.InterestIndices(interestCareer.Age);
                int capacity = StripInterestCapacity;
                InterestPage = Math.Min(InterestPage, _grid.PageCount(indices.Count, capacity) - 1);
                var title = Text(_strings.InterestsTitle(), _careerFont, Device);
                parts.Add(new Part(title, new CGRect(525 + ClassicStripOffset, 50, title.Size.Width, title.Size.Height)));

                int slot = 0;
                foreach (int index in indices.Skip(InterestPage * capacity).Take(capacity))
                {
                    var slotRect = InterestSlotRect(slot++);
                    var icon = _interestIcons[index];
                    parts.Add(new Part(icon, new CGRect(slotRect.X, slotRect.Y, icon.Size.Width, icon.Size.Height)));
                    var barRect = new CGRect(slotRect.X + icon.Size.Width + 5.0, slotRect.Y + 5.0, 40.0, 16.0);
                    parts.Add(new Part(_interestBackground, barRect));
                    int val = index < interestValues.Count ? interestValues[index] : 0;
                    double barW = OriginalGameplayLayout.PersonalityWidth(val);
                    if (barW > 0.0)
                    {
                        parts.Add(new Part(_interestBars, new CGRect(barRect.X, barRect.Y, barW, barRect.Height), new Vector4(0f, 0f, (float)barW / 40f, 1f)));
                    }
                }

                for (int direction = 0; direction <= 1; direction++)
                {
                    bool available = direction == 0 ? InterestPage > 0 : (InterestPage + 1) * capacity < indices.Count;
                    if (available)
                    {
                        parts.Add(new Part(_interestArrows[direction], InterestArrowRect(direction == 1), new Vector4(0f, 0f, 0.25f, 1f)));
                    }
                }
            }
            else if (CurrentSubpanel == Subpanel.PetSkills && selected.PetSkills is { } petSkillValues)
            {
                var (headerOrigin, skillSlots) = OriginalGameplayLayout.PetSkillsSubpanelLayout(_grid.Width);
                var header = Text(_strings.PetSkillsTitle(), _careerFont, Device);
                parts.Add(new Part(header, new CGRect(headerOrigin.X, headerOrigin.Y, header.Size.Width, header.Size.Height)));
                bool isCat = ((selected.Gender ?? 0) & 4) != 0;
                var activePetLabels = isCat ? _catSkillLabels : _dogSkillLabels;
                for (int index = 0; index < activePetLabels.Length && index < skillSlots.Count; index++)
                {
                    var label = activePetLabels[index];
                    var slot = skillSlots[index];
                    parts.Add(new Part(label, new CGRect(slot.Label.X + slot.Label.Width - label.Size.Width, slot.Label.Y, label.Size.Width, label.Size.Height)));
                    var barRect = slot.Bar;
                    parts.Add(new Part(_skillBarsOff, barRect));
                    int skillVal = petSkillValues.TryGetValue(OriginalGameplayLayout.PetSkills[index], out var sv) ? sv : 0;
                    double barW = OriginalGameplayLayout.PetSkillWidth(skillVal);
                    if (barW > 0.0)
                    {
                        parts.Add(new Part(_skillBars, new CGRect(barRect.X, barRect.Y, barW, barRect.Height), new Vector4(0f, 0f, (float)barW / 40f, 1f)));
                    }
                }
            }
            else if (CurrentSubpanel == Subpanel.Career && selected.Skills is { } skills && selected.Career is { } career)
            {
                var layout = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout);
                for (int index = 0; index < _skillLabels.Length && index < layout.Skills.Count; index++)
                {
                    var label = _skillLabels[index];
                    var slot = layout.Skills[index];
                    parts.Add(new Part(label, new CGRect(slot.Label.X + slot.Label.Width - label.Size.Width, slot.Label.Y, label.Size.Width, label.Size.Height)));
                    var barRect = slot.Bar;
                    parts.Add(new Part(_skillBarsOff, barRect));
                    int value = skills.TryGetValue(OriginalGameplayLayout.Skills[index], out var sv) ? sv : 0;
                    double barW = OriginalGameplayLayout.PersonalityWidth(value);
                    if (barW > 0.0)
                    {
                        parts.Add(new Part(_skillBars, new CGRect(barRect.X, barRect.Y, barW, barRect.Height), new Vector4(0f, 0f, (float)barW / 40f, 1f)));
                    }

                    if (OriginalGameplayLayout.RequiredSkillWidth(index, value, career) is { } required)
                    {
                        double marker = required;
                        parts.Add(new Part(_skillHighlight, new CGRect(barRect.X + marker - 1.0, barRect.Y, _skillHighlight.Size.Width, barRect.Height)));
                        parts.Add(new Part(_skillHighlight, new CGRect(barRect.X - 16.0, barRect.Y + barRect.Height - 1.0, marker + 16.0, 1.0)));
                    }
                }

                double sx = layout.JobIconOrigin.X;
                double summaryW = layout.JobSummaryRect.Width;
                int jobIconRow = career.HasJob && !career.IsSchool ? Math.Min(21, Math.Max(0, career.Type)) : 0;
                var activeJobIcons = ActiveJobIcons;
                double jobIconHeight = activeJobIcons.Size.Height / 22.0;
                double jobIconWidth = activeJobIcons.Size.Width / 4.0;
                parts.Add(new Part(
                    activeJobIcons,
                    new CGRect(sx, layout.JobIconOrigin.Y, jobIconWidth, jobIconHeight),
                    new Vector4(0f, jobIconRow / 22f, 0.25f, 1f / 22f)));

                var jobHeader = Text(_strings.CareerJobLabel(), _careerFont, Device);
                parts.Add(new Part(jobHeader, new CGRect(layout.HeaderOrigin.X, layout.HeaderOrigin.Y, jobHeader.Size.Width, jobHeader.Size.Height)));

                string titleText;
                if (!career.HasJob)
                {
                    titleText = _strings.CareerUnemployed();
                }
                else if (!string.IsNullOrEmpty(career.Title))
                {
                    titleText = career.Title;
                }
                else
                {
                    titleText = career.IsSchool ? _strings.CareerSchool() : string.Empty;
                }

                var wrappedLines = _careerFont.WrappedLines(titleText, Math.Max(40, (int)summaryW));
                int titleTop = 54 + (int)jobIconHeight;
                int maxLines = Math.Max(1, (45 - (int)jobIconHeight) / Math.Max(1, _careerFont.LineHeight));
                int lineCount = Math.Min(maxLines, wrappedLines.Count);
                for (int index = 0; index < lineCount; index++)
                {
                    var lineImage = Text(wrappedLines[index], _careerFont, Device);
                    parts.Add(new Part(lineImage, new CGRect(sx, titleTop + index * _careerFont.LineHeight, lineImage.Size.Width, lineImage.Size.Height)));
                }

                var activeSalaryBtn = ActiveSalaryButton;
                double salaryIconW = activeSalaryBtn.Size.Width / 4.0;
                double salaryIconH = activeSalaryBtn.Size.Height;
                parts.Add(new Part(
                    activeSalaryBtn,
                    new CGRect(sx, layout.SalaryRect.Y + Math.Floor((15.0 - salaryIconH) / 2.0), salaryIconW, salaryIconH),
                    new Vector4(0f, 0f, 0.25f, 1f)));

                string salary = career.HasJob && !career.IsSchool
                    ? "§" + (career.Salary ?? 0).ToString(CultureInfo.InvariantCulture)
                    : _strings.CareerUnavailable(false);
                var salaryImage = Text(salary, _careerFont, Device);
                double salaryTextW = Math.Min(Math.Max(1.0, summaryW - (salaryIconW + 3.0)), salaryImage.Size.Width);
                parts.Add(new Part(
                    salaryImage,
                    new CGRect(sx + salaryIconW + 3.0, layout.SalaryRect.Y, salaryTextW, salaryImage.Size.Height),
                    new Vector4(0f, 0f, (float)(salaryTextW / Math.Max(1.0, salaryImage.Size.Width)), 1f)));

                double perfIconW = _performanceButton.Size.Width / 4.0;
                double perfIconH = _performanceButton.Size.Height;
                parts.Add(new Part(
                    _performanceButton,
                    new CGRect(sx, layout.PerformanceRect.Y, perfIconW, perfIconH),
                    new Vector4(0f, 0f, 0.25f, 1f)));

                string rating = career.HasJob
                    ? career.PerformanceLabel ?? _strings.CareerUnavailable(true)
                    : _strings.CareerUnavailable(true);
                var ratingImage = Text(rating, _careerFont, Device);
                double ratingTextW = Math.Min(Math.Max(1.0, summaryW - (perfIconW + 3.0)), ratingImage.Size.Width);
                parts.Add(new Part(
                    ratingImage,
                    new CGRect(sx + perfIconW + 3.0, layout.PerformanceRect.Y, ratingTextW, ratingImage.Size.Height),
                    new Vector4(0f, 0f, (float)(ratingTextW / Math.Max(1.0, ratingImage.Size.Width)), 1f)));

                if (career.FriendsNeeded is { } requiredFriends)
                {
                    var friendsImage = Text(_strings.CareerNeedFriends(requiredFriends), _careerFont, Device);
                    parts.Add(new Part(friendsImage, new CGRect(sx, layout.FriendsRect.Y, friendsImage.Size.Width, friendsImage.Size.Height)));
                    var activeJobSmiley = ActiveJobFriendSmiley;
                    double friendIconW = activeJobSmiley.Size.Width / 4.0;
                    double friendIconH = activeJobSmiley.Size.Height;
                    parts.Add(new Part(
                        activeJobSmiley,
                        new CGRect(sx + friendsImage.Size.Width + 3.0, layout.FriendsRect.Y + 2.0, friendIconW, friendIconH),
                        new Vector4(0f, 0f, 0.25f, 1f)));
                }

                if (career.IsSchool && career.ReportCardGrade is not null)
                {
                    var reportLabel = Text(_strings.ReportCardLabel(), _careerFont, Device);
                    parts.Add(new Part(reportLabel, new CGRect(layout.ReportCardRect.X, layout.ReportCardRect.Y, reportLabel.Size.Width, reportLabel.Size.Height)));
                }
            }
            else if (CurrentSubpanel == Subpanel.ReportCard &&
                     selected.Career is { IsSchool: true, ReportCardGrade: { } grade })
            {
                var titleImg = Text(_strings.ReportCardLabel(), _careerFont, Device);
                var gradeImg = Text(grade, _zodiacFont, Device);
                double headerX = bay.Bay.MinX + (bay.IsFramed ? 24.0 : 5.0);
                var headerOrigin = _asianInventoryLayout ? new CGPoint(headerX, 55) : new CGPoint(headerX, 50);
                parts.Add(new Part(titleImg, new CGRect(headerOrigin.X, headerOrigin.Y, titleImg.Size.Width, titleImg.Size.Height)));
                parts.Add(new Part(
                    gradeImg,
                    new CGRect(
                        bay.Bay.MinX + Math.Floor((bay.Bay.Width - gradeImg.Size.Width) / 2.0),
                        50.0 + Math.Floor((100.0 - gradeImg.Size.Height) / 2.0),
                        gradeImg.Size.Width,
                        gradeImg.Size.Height)));
            }
            else if (CurrentSubpanel == Subpanel.Fame && selected.Fame is { } fame)
            {
                var layout = OriginalGameplayLayout.GetFameSubpanelLayout(_grid.Width, _asianInventoryLayout);
                var header = Text(_strings.FameHeader(), _careerFont, Device);
                parts.Add(new Part(header, new CGRect(layout.HeaderOrigin.X, layout.HeaderOrigin.Y, header.Size.Width, header.Size.Height)));
                var fameTrack = layout.StarBar;
                parts.Add(new Part(_fameBarsOff, fameTrack));
                double fameWidth = Math.Min(10, Math.Max(0, fame.Level)) * 8.0;
                if (fameWidth > 0.0)
                {
                    parts.Add(new Part(_fameBars, new CGRect(fameTrack.X, fameTrack.Y, fameWidth, fameTrack.Height), new Vector4(0f, 0f, (float)(fameWidth / 80.0), 1f)));
                }

                var titleImg = Text(fame.Title, _careerFont, Device);
                var titleBounds = layout.TitleRect;
                parts.Add(new Part(titleImg, new CGRect(titleBounds.X, titleBounds.Y, Math.Min(titleBounds.Width, titleImg.Size.Width), titleImg.Size.Height)));

                for (int index = 0; index < _skillLabels.Length && index < layout.Skills.Count; index++)
                {
                    var label = _skillLabels[index];
                    var slot = layout.Skills[index];
                    parts.Add(new Part(label, new CGRect(slot.Label.X + slot.Label.Width - label.Size.Width, slot.Label.Y, label.Size.Width, label.Size.Height)));
                    var barRect = slot.Bar;
                    parts.Add(new Part(_skillBarsOff, barRect));
                    int fameSkill = index < fame.Skills.Count ? fame.Skills[index] : 0;
                    double barW = OriginalGameplayLayout.PersonalityWidth(fameSkill);
                    if (barW > 0.0)
                    {
                        parts.Add(new Part(_skillBars, new CGRect(barRect.X, barRect.Y, barW, barRect.Height), new Vector4(0f, 0f, (float)(barW / 40.0), 1f)));
                    }

                    int requirement = index < fame.SkillRequirements.Count ? fame.SkillRequirements[index] : 0;
                    if (requirement > 0)
                    {
                        double marker = requirement * 4.0;
                        parts.Add(new Part(_skillHighlight, new CGRect(barRect.X + marker - 1.0, barRect.Y, _skillHighlight.Size.Width, barRect.Height)));
                        parts.Add(new Part(_skillHighlight, new CGRect(barRect.X - 16.0, barRect.Y + barRect.Height - 1.0, marker + 16.0, 1.0)));
                    }
                }

                var friendBounds = layout.FriendsRect;
                var activeFameSmiley = ActiveFameFriendSmiley;
                double friendIconW = activeFameSmiley.Size.Width / 4.0;
                double friendIconH = activeFameSmiley.Size.Height;
                parts.Add(new Part(activeFameSmiley, new CGRect(friendBounds.X, friendBounds.Y, friendIconW, friendIconH), new Vector4(0f, 0f, 0.25f, 1f)));

                string starPowerString = (fame.FriendStarPower * 0.5).ToString("0.0", CultureInfo.InvariantCulture);
                string friendLabel = fame.FriendStarPower < fame.FriendsNeeded * 2
                    ? _strings.FameNeedFriends(starPowerString, fame.FriendsNeeded)
                    : starPowerString;
                var friendText = Text(friendLabel, _careerFont, Device);
                parts.Add(new Part(friendText, new CGRect(friendBounds.X + friendIconW + 3.0, friendBounds.Y, friendText.Size.Width, friendText.Size.Height)));
            }
        }
        else if (CurrentSubpanel == Subpanel.House && next.House is { } unownedHouse)
        {
            AppendHouseParts(unownedHouse, parts);
        }

        int hour = next.Hours % 12 == 0 ? 12 : next.Hours % 12;
        string meridiem = _strings.PanelTip(next.Hours < 12 ? 21 : 22, next.Hours < 12 ? "AM" : "PM");
        var statusParts = new List<Part>();
        string clockText = $"{hour}:{next.Minutes:D2} {meridiem}";
        if (_cachedClockText is not { } cachedClock || !string.Equals(cachedClock.Text, clockText, StringComparison.Ordinal))
        {
            cachedClock = (clockText, Text(clockText, _clockFont, Device, (0x29, 0xe6, 0x5e)));
            _cachedClockText = cachedClock;
        }
        var clockImg = cachedClock.Image;
        statusParts.Add(new Part(clockImg, new CGRect(123.0 - clockImg.Size.Width / 2.0, 85.0, clockImg.Size.Width, clockImg.Size.Height)));

        if (next.FamilyFriends is { } familyFriends)
        {
            string friendsText = familyFriends.ToString(CultureInfo.InvariantCulture);
            if (_cachedFriendsText is not { } cachedFriends || !string.Equals(cachedFriends.Text, friendsText, StringComparison.Ordinal))
            {
                cachedFriends = (friendsText, Text(friendsText, _clockFont, Device, (0x29, 0xe6, 0x5e)));
                _cachedFriendsText = cachedFriends;
            }
            var countLabel = cachedFriends.Image;
            statusParts.Add(new Part(
                countLabel,
                new CGRect(FamilyFriendsLabelX, FamilyFriendsTextY, countLabel.Size.Width, countLabel.Size.Height)));
        }

        if (next.Budget is { } budget)
        {
            string budgetText = "§" + Grouped(budget);
            if (_cachedBudgetText is not { } cachedBudget || !string.Equals(cachedBudget.Text, budgetText, StringComparison.Ordinal))
            {
                cachedBudget = (budgetText, Text(budgetText, Font, Device, (0x29, 0xe6, 0x5e)));
                _cachedBudgetText = cachedBudget;
            }
            var money = cachedBudget.Image;
            statusParts.Add(new Part(money, new CGRect(178.0 - money.Size.Width, 125.0, money.Size.Width, money.Size.Height)));
        }

        if (Environment is { } env && env.Effect.Indicator is { } indicator)
        {
            Image LoadIndicatorAsset(string path)
            {
                string key = env.Asset(path);
                if (_indicatorImages.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                var targa = new SimsTarga(File.ReadAllBytes(key));
                if (targa.Width > 512 || targa.Height > 512)
                {
                    throw new ContentError("Plugin indicator image is too large");
                }

                var loaded = CreateImage(Device, targa.Width, targa.Height, targa.Rgba);
                _indicatorImages[key] = loaded;
                return loaded;
            }

            string combinedSeasonTip = string.IsNullOrWhiteSpace(indicator.WeatherTooltip)
                ? indicator.CycleTooltip
                : string.IsNullOrWhiteSpace(indicator.CycleTooltip)
                    ? indicator.WeatherTooltip
                    : $"{indicator.CycleTooltip} · {indicator.WeatherTooltip}";

            if (!_seasonTooltips.TryGetValue(CycleIndicator.Hover.Season, out var cachedTip)
                || !string.Equals(cachedTip.Text, combinedSeasonTip, StringComparison.Ordinal))
            {
                _seasonTooltips[CycleIndicator.Hover.Season] = (combinedSeasonTip, CreateImage(Device, PlacementTooltip.RasterizeHiDpi(combinedSeasonTip, _careerFont)));
            }

            string stripKey = env.Asset(indicator.Strip);
            if (!string.Equals(_indicatorKey, stripKey, StringComparison.Ordinal))
            {
                var strip = LoadIndicatorAsset(indicator.Strip);
                if (strip.Size.Width != CycleIndicator.Symbol || strip.Size.Height != (indicator.Count + 1) * CycleIndicator.Cell)
                {
                    throw new ContentError("Invalid plugin indicator strip");
                }

                _reel = (LoadIndicatorAsset(indicator.Window), strip, LoadIndicatorAsset(indicator.Overlay));
                _indicatorKey = stripKey;
                _reelSpin = null;
            }

            int idx = indicator.Index;
            int cnt = indicator.Count;
            if (cnt > 0)
            {
                if (_reelSpin is { } spin)
                {
                    int shown = ((int)Math.Round(spin.To, MidpointRounding.AwayFromZero) % cnt + cnt) % cnt;
                    if (idx != shown)
                    {
                        int delta = ((idx - shown) % cnt + cnt) % cnt;
                        _reelSpin = (ReelPosition, spin.To + delta + cnt, 0);
                        string seasonHeading = indicator.CycleTooltip.Split('—', StringSplitOptions.TrimEntries)[0];
                        if (!string.IsNullOrWhiteSpace(seasonHeading))
                        {
                            string bannerText = $"{seasonHeading} has arrived";
                            _seasonBanner = (CreateImage(Device, PlacementTooltip.RasterizeHiDpi(bannerText, _careerFont)), MonotonicNowSeconds() + 4.5);
                        }
                    }
                }
                else
                {
                    _reelSpin = (idx, idx, CycleIndicator.SpinFrames);
                }
            }
        }
        else
        {
            _seasonTooltips.Clear();
            IndicatorHovered = null;
            _reelSpin = null;
            _indicatorKey = null;
            _indicatorImages.Clear();
        }

        parts.AddRange(statusParts);

        var actions = new List<(GameplayPanelState.Action Action, Image? Icon)>();
        if (next.Selected?.Actions is { } rawActions)
        {
            foreach (var action in rawActions)
            {
                if (action.Visible)
                {
                    actions.Add((action, LoadResource(action.Icon)));
                }
            }
        }

        if (next.Selected?.Id != State?.Selected?.Id)
        {
            _actionAnimations.Clear();
            _optimisticCancelledTokens.Clear();
            _pendingPieChoiceOrigin = null;
        }

        var nextAnimations = new Dictionary<string, ActionAnimation>(StringComparer.Ordinal);
        var visibleTokens = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < actions.Count; index++)
        {
            string token = actions[index].Action.Token;
            visibleTokens.Add(token);
            var targetRect = OriginalGameplayLayout.Action(index);
            var target = new CGPoint(targetRect.X, targetRect.Y);
            if (_actionAnimations.TryGetValue(token, out var existing))
            {
                if (existing.TargetOrigin.X != target.X || existing.TargetOrigin.Y != target.Y)
                {
                    var current = InterfaceEffects ? existing.CurrentOrigin(now) : target;
                    nextAnimations[token] = new ActionAnimation
                    {
                        StartOrigin = current,
                        TargetOrigin = target,
                        StartedAt = InterfaceEffects ? now : 0.0
                    };
                }
                else
                {
                    nextAnimations[token] = existing;
                }
            }
            else if (_pendingPieChoiceOrigin is { } start && InterfaceEffects)
            {
                _pendingPieChoiceOrigin = null;
                nextAnimations[token] = new ActionAnimation
                {
                    StartOrigin = start,
                    TargetOrigin = target,
                    StartedAt = now
                };
            }
            else
            {
                nextAnimations[token] = new ActionAnimation
                {
                    StartOrigin = target,
                    TargetOrigin = target,
                    StartedAt = 0.0
                };
            }
        }

        _actionAnimations = nextAnimations;
        _optimisticCancelledTokens.IntersectWith(visibleTokens);
        PieHead = LoadResource(next.Selected?.PieHead);
        _body = parts;
        _statusBody = statusParts;
        _queued = actions;
        State = next;
        _resourceRoot = resolvedRoot;

        var staleKeys = new List<string>();
        foreach (var key in _resources.Keys)
        {
            if (!used.Contains(key))
            {
                staleKeys.Add(key);
            }
        }

        foreach (var key in staleKeys)
        {
            _resources.Remove(key);
        }

        if (RelationshipPopupId is { } relId)
        {
            var target = next.Selected?.Relationships?.FirstOrDefault(r => r.NeighborId == relId);
            if (target is not null)
            {
                UpdateRelationshipPopup(target);
            }
            else
            {
                RelationshipPopupId = null;
            }
        }
    }

    public void Choose(Subpanel panel)
    {
        if (State is not { } state || _resourceRoot is null || !Supports(panel, state))
        {
            return;
        }

        var previous = CurrentSubpanel;
        if (panel != CurrentSubpanel)
        {
            _personalityPopupTrait = null;
            _zodiacPopupActive = false;
            RelationshipPopupId = null;
            SkillPopupIndex = null;
            _housePopupIndex = null;
            _careerSummaryPopupIndex = null;
            _livePopupAnchorMidX = null;
            if (_motivePopupIndex is not null)
            {
                _motivePopupIndex = null;
                _personalityPopupTitle = null;
                _personalityPopupLines = [];
                _personalityPopupPortrait = null;
            }
        }

        CurrentSubpanel = panel;
        _hoveredInventoryTile = null;
        _inventoryHoverStartedAt = null;

        try
        {
            Update(state, _resourceRoot);
        }
        catch
        {
            CurrentSubpanel = previous;
            throw;
        }
    }

    public int? NeedIndexAtPanelPoint(CGPoint panel, CGSize canvas)
    {
        TryPrepare(canvas);
        if (CurrentSubpanel != Subpanel.Needs || State?.Selected is null)
        {
            return null;
        }

        for (int i = 0; i < 8; i++)
        {
            if (Contains(OriginalGameplayLayout.NeedRect(i, _grid.Width), panel))
            {
                return i;
            }
        }

        return null;
    }

    public void ClearHoverPopups()
    {
        ClearLivePopupState();
        ClearInventoryHover();
    }

    public bool ClickCareerSummary(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Career || State?.Selected?.Career is not { } career)
        {
            return false;
        }

        double top = canvas.Height - 150.0;
        var layout = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout);
        var reportRect = Offset(layout.ReportCardRect, 0, top);
        if (career.IsSchool && career.ReportCardGrade is not null &&
            Contains(reportRect, point) &&
            _strings.ReportCardPopup() is (var reportTitle, var reportDesc))
        {
            if (_careerSummaryPopupIndex == 4)
            {
                ClearLivePopupState();
                return true;
            }

            ClearLivePopupState();
            _careerSummaryPopupIndex = 4;
            _livePopupAnchorMidX = MidX(reportRect);
            SetLivePopup(reportTitle, reportDesc);
            return true;
        }

        var targets = new List<(int Index, CGRect Rect)>
        {
            (0, Offset(layout.JobSummaryRect, 0, top)),
            (1, Offset(layout.SalaryRect, 0, top)),
            (2, Offset(layout.PerformanceRect, 0, top))
        };
        if (career.FriendsNeeded is not null)
        {
            targets.Add((3, Offset(layout.FriendsRect, 0, top)));
        }

        foreach (var target in targets)
        {
            if (!Contains(target.Rect, point))
            {
                continue;
            }

            if (_strings.CareerSummaryPopup(target.Index, career) is not (var summaryTitle, var summaryDesc))
            {
                return false;
            }

            if (_careerSummaryPopupIndex == target.Index)
            {
                ClearLivePopupState();
                return true;
            }

            ClearLivePopupState();
            _careerSummaryPopupIndex = target.Index;
            _livePopupAnchorMidX = MidX(target.Rect);
            SetLivePopup(summaryTitle, summaryDesc);
            return true;
        }

        return false;
    }

    public bool ClickHouseHint(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.House || State?.House is not { } values)
        {
            return false;
        }

        double top = canvas.Height - 150.0;
        var layout = OriginalGameplayLayout.GetHouseSubpanelLayout(_grid.Width, _asianInventoryLayout);
        int?[] scores = [values.SizeScore, values.FurnishingsScore, values.YardScore, values.UpkeepScore, values.LayoutScore];
        string[] names = ["Size", "Furnishings", "Yard", "Upkeep", "Layout"];
        var targets = new List<(int Index, CGRect Rect)>();

        for (int index = 0; index < scores.Length; index++)
        {
            var label = Text(_strings.HouseLabel(index + 1, names[index]), _careerFont, Device);
            double labelX = layout.ScoreLabelRightXs[index] - label.Size.Width;
            double y = layout.ScoreLabelYs[index];
            var labelRect = Offset(new CGRect(labelX, y, label.Size.Width, label.Size.Height), 0, top);
            var barRect = Offset(layout.ScoreBars[index], 0, top);
            targets.Add((index, RectUnion(labelRect, barRect)));
        }

        (int Index, string? Text, CGRect Rect)[] details =
        [
            (5, values.SquareFeet is { } sqFt ? FormatHouseCount(_strings.HouseLabel(6, "Sq. Ft.: %d"), sqFt) : null, layout.InfoRects[0]),
            (6, values.Bedrooms is { } beds ? FormatHouseCount(_strings.HouseLabel(7, "Bedrooms: %d"), beds) : null, layout.InfoRects[1]),
            (7, values.Bathrooms is { } baths ? FormatHouseCount(_strings.HouseLabel(8, "Bathrooms: %d"), baths) : null, layout.InfoRects[2]),
            (8, values.LotSize is { } lotSize ? FormatHouseLot(lotSize) : null, layout.InfoRects[3])
        ];

        foreach (var (index, text, infoRect) in details)
        {
            if (text is null)
            {
                continue;
            }

            var label = Text(text, _careerFont, Device);
            var expanded = Offset(
                new CGRect(infoRect.X, infoRect.Y, Math.Max(infoRect.Width, label.Size.Width), Math.Max(15.0, label.Size.Height)),
                0,
                top);
            targets.Add((index, expanded));
        }

        foreach (var hit in targets)
        {
            if (!Contains(hit.Rect, point))
            {
                continue;
            }

            if (_strings.HouseHint(hit.Index) is not (var hintTitle, var hintDesc))
            {
                return false;
            }

            if (_housePopupIndex == hit.Index)
            {
                ClearLivePopupState();
            }
            else
            {
                ClearLivePopupState();
                _housePopupIndex = hit.Index;
                _livePopupAnchorMidX = MidX(hit.Rect);
                SetLivePopup(hintTitle, hintDesc);
            }

            return true;
        }

        return false;
    }

    public bool ClickPersonality(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Personality || State?.Selected is not { } selected || selected.Personality is not { } traits)
        {
            return false;
        }

        double top = canvas.Height - 150.0;
        bool isPet = ((selected.Gender ?? 0) & 6) != 0;
        var activeTraitLabels = isPet ? _petTraitLabels : _traitLabels;
        int labelWidth = MaxImageWidth(activeTraitLabels);
        var layout = OriginalGameplayLayout.PersonalitySubpanelLayout(_grid.Width, labelWidth, _asianInventoryLayout);
        var bay = OriginalGameplayLayout.CenteredBay(_grid.Width);

        if (!isPet && selected.Zodiac is { } stored)
        {
            int zodiac = OriginalGameplayLayout.Zodiac(traits, stored);
            int signIndex = zodiac - 1;
            if (signIndex >= 0 && signIndex < OriginalGameplayLayout.Signs.Count)
            {
                string signText = _strings.Sign(signIndex, OriginalGameplayLayout.Signs[signIndex]);
                double maxSignW = Math.Max(50.0, bay.Bay.MaxX - layout.ZodiacOrigin.X - 4.0);
                var chosenFont = layout.UsesBottomAlignedZodiac || _zodiacFont.TextWidth(signText) <= maxSignW ? _zodiacFont : Font;
                var signImg = Text(signText, chosenFont, Device);
                double signY = layout.UsesBottomAlignedZodiac ? layout.ZodiacOrigin.Y - signImg.Size.Height : layout.ZodiacOrigin.Y;
                var signRect = Offset(new CGRect(layout.ZodiacOrigin.X, signY, signImg.Size.Width, signImg.Size.Height), 0, top);
                if (Contains(signRect, point) && _strings.ZodiacPopup(zodiac) is (var zodiacTitle, var zodiacDesc))
                {
                    bool wasActive = _zodiacPopupActive;
                    ClearLivePopupState();
                    if (!wasActive)
                    {
                        _zodiacPopupActive = true;
                        _livePopupAnchorMidX = MidX(signRect);
                        SetLivePopup(zodiacTitle, zodiacDesc);
                    }

                    return true;
                }
            }
        }

        int hitTrait = -1;
        for (int i = 0; i < OriginalGameplayLayout.Traits.Count && i < layout.TraitRects.Count; i++)
        {
            if (Contains(Offset(layout.TraitRects[i], 0, top), point))
            {
                hitTrait = i;
                break;
            }
        }

        if (hitTrait < 0 ||
            !traits.TryGetValue(OriginalGameplayLayout.Traits[hitTrait], out int value) ||
            _strings.PersonalityPopup(hitTrait, value, isPet) is not (var traitName, var traitDesc))
        {
            return false;
        }

        int? nextTrait = PersonalityPopup.Selection(_personalityPopupTrait, hitTrait);
        ClearLivePopupState();
        _personalityPopupTrait = nextTrait;
        if (_personalityPopupTrait is not null)
        {
            _livePopupAnchorMidX = MidX(layout.TraitRects[hitTrait]);
            (Image, GameplayPanelState.Image)? portrait = null;
            if (selected.Portrait is { } reference && _resources.TryGetValue(reference.File, out var portraitImage))
            {
                portrait = (portraitImage, reference);
            }

            SetLivePopup(traitName, traitDesc, portrait);
        }

        return true;
    }

    public Subpanel? SubpanelAt(CGPoint point, CGSize canvas)
    {
        TryPrepare(canvas);
        var reportRect = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout).ReportCardRect;
        if (CurrentSubpanel == Subpanel.Career &&
            Contains(Offset(reportRect, 0, canvas.Height - 150.0), point) &&
            Supports(Subpanel.ReportCard, State))
        {
            return Subpanel.ReportCard;
        }

        foreach (var panel in MainTabSubpanels)
        {
            if (Supports(panel, State) && Contains(Offset(ButtonRect(panel.ButtonIndex()), 0, canvas.Height - 150.0), point))
            {
                return panel;
            }
        }

        return null;
    }

    public void ChooseInterestPage(int page)
    {
        if (CurrentSubpanel != Subpanel.Interests || State is not { } state || _resourceRoot is null || state.Selected?.Career is not { } career)
        {
            throw new ContentError("Invalid interest page");
        }

        int pageCount = _grid.PageCount(OriginalGameplayLayout.InterestIndices(career.Age).Count, StripInterestCapacity);
        if (page < 0 || page >= pageCount)
        {
            throw new ContentError("Invalid interest page");
        }

        InterestPage = page;
        Update(state, _resourceRoot);
    }

    public void ChooseRelationshipFilter(OriginalGameplayLayout.RelationshipFilter filter)
    {
        if (CurrentSubpanel != Subpanel.Relationships || State is not { } state || _resourceRoot is null)
        {
            throw new ContentError("Relationships panel is unavailable");
        }

        RelationshipFilter = filter;
        RelationshipPage = 0;
        Update(state, _resourceRoot);
    }

    public void ChooseRelationshipPage(int page)
    {
        if (CurrentSubpanel != Subpanel.Relationships || State is not { } state || _resourceRoot is null || state.Selected?.Relationships is not { } values)
        {
            throw new ContentError("Invalid relationship page");
        }

        int pageCount = _grid.PageCount(OriginalGameplayLayout.Relationships(values, RelationshipFilter).Count, StripRelationshipCapacity);
        if (page < 0 || page >= pageCount)
        {
            throw new ContentError("Invalid relationship page");
        }

        RelationshipPage = page;
        Update(state, _resourceRoot);
    }

    public bool RelationshipControl(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Relationships || State?.Selected?.Relationships is not { } values)
        {
            return false;
        }

        double offsetY = canvas.Height - 150.0;
        var allFilters = Enum.GetValues<OriginalGameplayLayout.RelationshipFilter>();
        for (int index = 0; index < allFilters.Length; index++)
        {
            if (Contains(Offset(RelationshipFilterRect(index), 0, offsetY), point))
            {
                ChooseRelationshipFilter(allFilters[index]);
                return true;
            }
        }

        ReadOnlySpan<bool> directions = [false, true];
        foreach (bool isNext in directions)
        {
            if (Contains(Offset(RelationshipArrowRect(isNext), 0, offsetY), point))
            {
                int page = RelationshipPage + (isNext ? 1 : -1);
                if (page >= 0 && page * StripRelationshipCapacity < OriginalGameplayLayout.Relationships(values, RelationshipFilter).Count)
                {
                    ChooseRelationshipPage(page);
                }

                return true;
            }
        }

        return false;
    }

    public bool ClickSkill(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        double top = canvas.Height - 150.0;
        var layout = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout);
        if (CurrentSubpanel != Subpanel.Career || State?.Selected?.Skills is null || State.Selected.Career is null)
        {
            return false;
        }

        int hitIndex = -1;
        for (int i = 0; i < OriginalGameplayLayout.Skills.Count && i < layout.Skills.Count; i++)
        {
            if (Contains(Offset(layout.Skills[i].HitRect, 0, top), point))
            {
                hitIndex = i;
                break;
            }
        }

        if (hitIndex < 0 || _strings.SkillPopup(hitIndex) is not (var popupTitle, var popupDesc))
        {
            return false;
        }

        if (SkillPopupIndex == hitIndex)
        {
            ClearLivePopupState();
            return true;
        }

        ClearLivePopupState();
        SkillPopupIndex = hitIndex;
        _livePopupAnchorMidX = MidX(layout.Skills[hitIndex].HitRect);
        SetLivePopup(popupTitle, popupDesc);
        return true;
    }

    public bool ClickPetSkill(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        double top = canvas.Height - 150.0;
        var (_, skillSlots) = OriginalGameplayLayout.PetSkillsSubpanelLayout(_grid.Width);
        bool isCat = ((State?.Selected?.Gender ?? 0) & 4) != 0;
        if (CurrentSubpanel != Subpanel.PetSkills || State?.Selected?.PetSkills is null)
        {
            return false;
        }

        int hitIndex = -1;
        for (int i = 0; i < OriginalGameplayLayout.PetSkills.Count && i < skillSlots.Count; i++)
        {
            if (Contains(Offset(skillSlots[i].HitRect, 0, top), point))
            {
                hitIndex = i;
                break;
            }
        }

        if (hitIndex < 0 || _strings.PetSkillPopup(hitIndex, isCat) is not (var popupTitle, var popupDesc))
        {
            return false;
        }

        if (SkillPopupIndex == hitIndex)
        {
            ClearLivePopupState();
            return true;
        }

        ClearLivePopupState();
        SkillPopupIndex = hitIndex;
        _livePopupAnchorMidX = MidX(skillSlots[hitIndex].HitRect);
        SetLivePopup(popupTitle, popupDesc);
        return true;
    }

    public bool ClickFameSkill(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Fame || State?.Selected?.Fame is not { } fame)
        {
            return false;
        }

        double top = canvas.Height - 150.0;
        var layout = OriginalGameplayLayout.GetFameSubpanelLayout(_grid.Width, _asianInventoryLayout);
        CGRect[] levelTargets =
        [
            Offset(layout.TitleRect, 0, top),
            Offset(layout.StarBar, 0, top)
        ];
        foreach (var target in levelTargets)
        {
            if (Contains(target, point) && _strings.FameLevelPopup(fame.Level) is (var levelTitle, var levelDesc))
            {
                if (_careerSummaryPopupIndex == 0)
                {
                    ClearLivePopupState();
                    return true;
                }

                ClearLivePopupState();
                _careerSummaryPopupIndex = 0;
                _livePopupAnchorMidX = MidX(target);
                SetLivePopup(levelTitle, levelDesc);
                return true;
            }
        }

        var friendsRect = Offset(layout.FriendsRect, 0, top);
        CGRect[] friendsTargets =
        [
            friendsRect,
            new CGRect(friendsRect.X, friendsRect.Y - 2.0, Math.Max(100.0, friendsRect.Width), 22.0)
        ];
        foreach (var target in friendsTargets)
        {
            if (Contains(target, point) && _strings.FameFriendsPopup() is (var friendsTitle, var friendsDesc))
            {
                if (_careerSummaryPopupIndex == 1)
                {
                    ClearLivePopupState();
                    return true;
                }

                ClearLivePopupState();
                _careerSummaryPopupIndex = 1;
                _livePopupAnchorMidX = MidX(target);
                SetLivePopup(friendsTitle, friendsDesc);
                return true;
            }
        }

        int hitIndex = -1;
        for (int i = 0; i < OriginalGameplayLayout.Skills.Count && i < layout.Skills.Count; i++)
        {
            if (Contains(Offset(layout.Skills[i].HitRect, 0, top), point))
            {
                hitIndex = i;
                break;
            }
        }

        if (hitIndex < 0 || _strings.SkillPopup(hitIndex) is not (var popupTitle, var popupDesc))
        {
            return false;
        }

        if (SkillPopupIndex == hitIndex)
        {
            ClearLivePopupState();
            return true;
        }

        ClearLivePopupState();
        SkillPopupIndex = hitIndex;
        _livePopupAnchorMidX = MidX(layout.Skills[hitIndex].HitRect);
        SetLivePopup(popupTitle, popupDesc);
        return true;
    }

    public GameplayPanelState.Relationship? RelationshipAt(CGPoint point, CGSize canvas)
    {
        TryPrepare(canvas);
        if (CurrentSubpanel != Subpanel.Relationships || State?.Selected?.Relationships is not { } values)
        {
            return null;
        }

        int capacity = StripRelationshipCapacity;
        double top = canvas.Height - 150.0;
        int slot = 0;
        foreach (var rel in OriginalGameplayLayout.Relationships(values, RelationshipFilter).Skip(RelationshipPage * capacity).Take(capacity))
        {
            if (Contains(Offset(RelationshipSlotRect(slot), 0, top), point))
            {
                return rel;
            }

            slot++;
        }

        return null;
    }

    public bool ClickRelationship(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Relationships || State?.Selected?.Relationships is not { } values)
        {
            return false;
        }

        int capacity = StripRelationshipCapacity;
        double top = canvas.Height - 150.0;
        int hitSlot = -1;
        GameplayPanelState.Relationship? hitTarget = null;
        int slot = 0;
        foreach (var rel in OriginalGameplayLayout.Relationships(values, RelationshipFilter).Skip(RelationshipPage * capacity).Take(capacity))
        {
            if (Contains(Offset(RelationshipSlotRect(slot), 0, top), point))
            {
                hitSlot = slot;
                hitTarget = rel;
                break;
            }

            slot++;
        }

        if (hitTarget is null || _strings.RelationshipPopup(hitTarget) is null)
        {
            return false;
        }

        if (RelationshipPopupId == hitTarget.NeighborId)
        {
            ClearLivePopupState();
        }
        else
        {
            ClearLivePopupState();
            RelationshipPopupId = hitTarget.NeighborId;
            _livePopupAnchorMidX = MidX(RelationshipSlotRect(hitSlot));
            UpdateRelationshipPopup(hitTarget);
        }

        return true;
    }

    public void ChooseInventorySort(OriginalGameplayLayout.InventorySort sort)
    {
        if (CurrentSubpanel != Subpanel.Inventory || State is not { } state || _resourceRoot is null)
        {
            throw new ContentError("Inventory panel is unavailable");
        }

        InventorySort = sort;
        InventoryPage = 0;
        _hoveredInventoryTile = null;
        _inventoryHoverStartedAt = null;
        Update(state, _resourceRoot);
    }

    public bool InventoryControl(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Inventory || State?.Selected?.Inventory is not { } values || State is not { } state || _resourceRoot is null)
        {
            return false;
        }

        double offset = canvas.Height - 150.0;
        var allSorts = Enum.GetValues<OriginalGameplayLayout.InventorySort>();
        for (int index = 0; index < allSorts.Length; index++)
        {
            if (Contains(Offset(InventorySortRect(index), 0, offset), point))
            {
                ChooseInventorySort(allSorts[index]);
                return true;
            }
        }

        int capacity = StripInventoryCapacity;
        ReadOnlySpan<bool> directions = [false, true];
        foreach (bool isNext in directions)
        {
            if (Contains(Offset(InventoryArrowRect(isNext), 0, offset), point))
            {
                int page = InventoryPage + (isNext ? 1 : -1);
                if (page >= 0 && page * capacity < OriginalGameplayLayout.InventoryItems(values, InventorySort).Count)
                {
                    InventoryPage = page;
                    _hoveredInventoryTile = null;
                    _inventoryHoverStartedAt = null;
                    Update(state, _resourceRoot);
                }

                return true;
            }
        }

        return false;
    }

    public bool HoverInventory(CGPoint point, CGSize canvas)
    {
        TryPrepare(canvas);
        int? nextTile = null;
        if (CurrentSubpanel == Subpanel.Inventory && State?.Selected?.Inventory is { } values)
        {
            var items = OriginalGameplayLayout.InventoryItems(values, InventorySort);
            int capacity = StripInventoryCapacity;
            double top = canvas.Height - 150.0;
            int visibleCount = Math.Min(capacity, Math.Max(0, items.Count - InventoryPage * capacity));
            for (int slot = 0; slot < visibleCount; slot++)
            {
                if (Contains(Offset(InventoryTileRect(slot), 0, top), point))
                {
                    nextTile = slot;
                    break;
                }
            }
        }

        if (nextTile != _hoveredInventoryTile)
        {
            _hoveredInventoryTile = nextTile;
            _inventoryHoverStartedAt = null;
        }

        return nextTile is not null;
    }

    public void ClearInventoryHover()
    {
        _hoveredInventoryTile = null;
        _inventoryHoverStartedAt = null;
        _hoveredPerson = null;
        _hoveredPeopleTip = null;
        HoveredPanelPoint = null;
        _panelTip = null;
        _indicatorHovered = null;
        _hoveredNeed = null;
        HoveredAction = null;
        _suppressedTooltipIdentity = null;
    }

    public void DismissTooltips()
    {
        _suppressedTooltipIdentity = CurrentTooltipIdentity;
        _inventoryHoverStartedAt = null;
        _indicatorHoverStartedAt = null;
        _personHoverStartedAt = null;
    }

    public (string Text, CGRect Rect)? PeopleTooltip(CGPoint point, CGSize canvas, bool panelMinimized = false)
    {
        TryPrepare(canvas);

        static (string Text, CGRect Rect)? NonEmpty(string text, CGRect rect) =>
            HasVisibleTooltipText(text) ? (text, rect) : null;

        double now = MonotonicNowSeconds();
        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        for (int index = 0; index < maxQueue; index++)
        {
            var rect = CurrentActionRect(index, now);
            if (Contains(rect, point) && _queued[index].Action.Name is { } name && NonEmpty(name, rect) is { } valid)
            {
                return valid;
            }
        }

        if (panelMinimized)
        {
            return null;
        }

        double top = canvas.Height - 150.0;
        ReadOnlySpan<bool> segments = [true, false];
        foreach (bool positive in segments)
        {
            var rect = Offset(OriginalGameplayLayout.MoodRatingSegment(positive), 0, top);
            if (Contains(rect, point))
            {
                return NonEmpty(_strings.PeopleRatingTip(15, "Mood"), rect);
            }
        }

        foreach (var panel in MainTabSubpanels)
        {
            if (!Supports(panel, State))
            {
                continue;
            }

            var rect = Offset(ButtonRect(panel.ButtonIndex()), 0, top);
            if (Contains(rect, point))
            {
                return NonEmpty(_strings.PeoplePanelTip(panel.ButtonIndex(), State?.Selected, panel.Label()), rect);
            }
        }

        if (CurrentSubpanel == Subpanel.Relationships)
        {
            int filterCount = Enum.GetValues<OriginalGameplayLayout.RelationshipFilter>().Length;
            for (int index = 0; index < filterCount; index++)
            {
                var rect = Offset(RelationshipFilterRect(index), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.RelationshipFilterTip(index, string.Empty), rect);
                }
            }

            var relationships = OriginalGameplayLayout.Relationships(State?.Selected?.Relationships ?? [], RelationshipFilter);
            int capacity = StripRelationshipCapacity;
            int slot = 0;
            foreach (var relationship in relationships.Skip(RelationshipPage * capacity).Take(capacity))
            {
                var rect = Offset(RelationshipSlotRect(slot++), 0, top);
                if (Contains(rect, point) && NonEmpty(relationship.Name, rect) is { } valid)
                {
                    return valid;
                }
            }

            if (RelationshipPage > 0)
            {
                var rect = Offset(RelationshipArrowRect(false), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(0, "Previous"), rect);
                }
            }

            if ((RelationshipPage + 1) * capacity < relationships.Count)
            {
                var rect = Offset(RelationshipArrowRect(true), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(1, "Next"), rect);
                }
            }
        }
        else if (CurrentSubpanel == Subpanel.Interests && State?.Selected?.Career is { } career)
        {
            var indices = OriginalGameplayLayout.InterestIndices(career.Age);
            int capacity = StripInterestCapacity;
            int slot = 0;
            foreach (int interestIndex in indices.Skip(InterestPage * capacity).Take(capacity))
            {
                var rect = Offset(InterestSlotRect(slot++), 0, top);
                if (Contains(rect, point) && interestIndex >= 0 && interestIndex < OriginalGameplayLayout.Interests.Count)
                {
                    string fallback = OriginalGameplayLayout.Interests[interestIndex];
                    return NonEmpty(_strings.InterestTip(interestIndex, fallback), rect);
                }
            }

            if (InterestPage > 0)
            {
                var rect = Offset(InterestArrowRect(false), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(0, "Previous"), rect);
                }
            }

            if ((InterestPage + 1) * capacity < indices.Count)
            {
                var rect = Offset(InterestArrowRect(true), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(1, "Next"), rect);
                }
            }
        }
        else if (CurrentSubpanel == Subpanel.Inventory && State?.Selected?.Inventory is { } values)
        {
            var allSorts = Enum.GetValues<OriginalGameplayLayout.InventorySort>();
            for (int index = 0; index < allSorts.Length; index++)
            {
                var rect = Offset(InventorySortRect(index), 0, top);
                if (Contains(rect, point))
                {
                    int tipIndex = allSorts[index] switch
                    {
                        OriginalGameplayLayout.InventorySort.Gift => 0,
                        OriginalGameplayLayout.InventorySort.Magic => 1,
                        _ => 2
                    };
                    return NonEmpty(_strings.InventorySortTip(tipIndex, string.Empty), rect);
                }
            }

            var items = OriginalGameplayLayout.InventoryItems(values, InventorySort);
            int capacity = StripInventoryCapacity;
            if (InventoryPage > 0)
            {
                var rect = Offset(InventoryArrowRect(false), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(0, "Previous"), rect);
                }
            }

            if ((InventoryPage + 1) * capacity < items.Count)
            {
                var rect = Offset(InventoryArrowRect(true), 0, top);
                if (Contains(rect, point))
                {
                    return NonEmpty(_strings.PeoplePageTip(1, "Next"), rect);
                }
            }
        }

        return null;
    }

    public bool PageInterests(CGPoint point, CGSize canvas)
    {
        Prepare(canvas);
        if (CurrentSubpanel != Subpanel.Interests || State?.Selected?.Career is not { } career)
        {
            return false;
        }

        double offset = canvas.Height - 150.0;
        ReadOnlySpan<bool> directions = [false, true];
        foreach (bool isNext in directions)
        {
            if (Contains(Offset(InterestArrowRect(isNext), 0, offset), point))
            {
                int page = InterestPage + (isNext ? 1 : -1);
                if (page >= 0 && page * StripInterestCapacity < OriginalGameplayLayout.InterestIndices(career.Age).Count)
                {
                    ChooseInterestPage(page);
                }

                return true;
            }
        }

        return false;
    }

    public void Prepare(CGSize canvas)
    {
        var next = new OriginalGameplayLayout.PanelGrid(canvas.Width);
        if (Equals(next, _grid))
        {
            return;
        }

        var previous = _grid;
        int previousRelationshipPage = RelationshipPage;
        int previousInterestPage = InterestPage;
        _grid = next;

        if (State is not { } state || _resourceRoot is not { } root)
        {
            return;
        }

        var relationships = OriginalGameplayLayout.Relationships(state.Selected?.Relationships ?? [], RelationshipFilter);
        RelationshipPage = next.ResizedPage(RelationshipPage, previous.RelationshipCapacity, next.RelationshipCapacity, relationships.Count);
        int interests = state.Selected?.Career is { } career ? OriginalGameplayLayout.InterestIndices(career.Age).Count : 0;
        InterestPage = next.ResizedPage(InterestPage, previous.InterestCapacity, next.InterestCapacity, interests);

        try
        {
            Update(state, root);
        }
        catch
        {
            _grid = previous;
            RelationshipPage = previousRelationshipPage;
            InterestPage = previousInterestPage;
            throw;
        }
    }

    public IReadOnlyList<Part> UniversalStatusParts(CGSize canvas)
    {
        double top = canvas.Height - 150.0;
        var parts = new List<Part>(_statusBody.Count + 4);
        if (SimsBitmapFont.SharpTextEnabled && ActiveUniversal.Texture != _universal.Texture)
        {
            parts.Add(new Part(_vectorLcdFriendSmiley, Offset(FamilyFriendsSmileyRect, 0, top)));
        }
        foreach (var part in _statusBody)
        {
            parts.Add(new Part(part.Image, Offset(part.Rect, 0, top), part.UV, part.Opacity));
        }

        AppendReelParts(top, parts);
        return parts;
    }

    public IReadOnlyList<Part> Parts(CGSize canvas, bool panelMinimized = false)
    {
        TryPrepare(canvas);
        double top = canvas.Height - 150.0;
        var hoverTextures = _hoverTextures;
        if (hoverTextures is null)
        {
            hoverTextures = new HashSet<UITexture>(ReferenceEqualityComparer.Instance)
            {
                _people.Texture,
                _vectorPeople.Texture,
                _salaryButton.Texture,
                _vectorSalaryButton.Texture,
                _performanceButton.Texture,
                _jobFriendSmiley.Texture,
                _vectorJobFriendSmiley.Texture,
                _fameFriendSmiley.Texture,
                _vectorFameFriendSmiley.Texture,
                _jobIcons.Texture,
                _vectorJobIcons.Texture,
                _fameIcons.Texture,
                _vectorFameIcons.Texture
            };
            foreach (var img in ScrollArrows) hoverTextures.Add(img.Texture);
            foreach (var img in _interestArrows) hoverTextures.Add(img.Texture);
            foreach (var img in _buttons) hoverTextures.Add(img.Texture);
            foreach (var img in _vectorButtons)
            {
                if (img is { } vBtn) hoverTextures.Add(vBtn.Texture);
            }
            foreach (var img in _relationshipFilters) hoverTextures.Add(img.Texture);
            foreach (var img in _vectorRelationshipFilters) hoverTextures.Add(img.Texture);
            foreach (var img in _inventorySorts) hoverTextures.Add(img.Texture);
            foreach (var img in _vectorInventorySorts) hoverTextures.Add(img.Texture);
            _hoverTextures = hoverTextures;
        }

        CGPoint? panelHover = null;
        if (HoveredPanelPoint is { } raw)
        {
            if (raw.Y >= 0 && raw.Y < 150)
            {
                panelHover = raw;
            }
            else if (canvas.Height > 150 && raw.Y >= top && raw.Y < canvas.Height)
            {
                panelHover = new CGPoint(raw.X, raw.Y - top);
            }
        }

        var parts = new List<Part>(_body.Count + 64);
        foreach (var part in _body)
        {
            if (panelMinimized && part.Rect.X >= 220.0)
            {
                continue;
            }

            var uv = part.UV;
            if (panelHover is { } hovered &&
                hoverTextures.Contains(part.Image.Texture) &&
                Math.Abs(uv.X) < 0.001f &&
                Math.Abs(uv.Z - 0.25f) < 0.001f &&
                Contains(part.Rect, hovered))
            {
                uv.X = 0.50f;
            }

            parts.Add(new Part(part.Image, Offset(part.Rect, 0, top), uv, part.Opacity));
        }

        double now = MonotonicNowSeconds();
        if (_seasonBanner is { } seasonBanner)
        {
            double remaining = seasonBanner.ExpiresAt - now;
            if (remaining > 0.0)
            {
                float alpha = remaining < 1.0 ? (float)Math.Clamp(remaining, 0.0, 1.0) : 1f;
                var bannerImg = seasonBanner.Image;
                parts.Add(new Part(
                    bannerImg,
                    new CGRect((canvas.Width - bannerImg.Size.Width) * 0.5, 14.0, bannerImg.Size.Width, bannerImg.Size.Height),
                    new Vector4(0f, 0f, 1f, 1f),
                    alpha));
            }
            else
            {
                _seasonBanner = null;
            }
        }
        if (!panelMinimized && CurrentSubpanel == Subpanel.Needs && State?.Selected is not null)
        {
            AppendNeedsDynamicParts(top, (ulong)(Math.Max(0.0, now) * 1000.0), parts);
        }

        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        for (int index = 0; index < maxQueue; index++)
        {
            var (action, iconImg) = _queued[index];
            var actionRect = CurrentActionRect(index, now);
            if (iconImg is { } image && action.Icon is { } reference)
            {
                double width = image.Size.Width / reference.Columns;
                double height = image.Size.Height / reference.Rows;
                double scale = Math.Min(1.0, Math.Min(45.0 / width, 45.0 / height));
                // Distinguishes Sim portraits (single faces, 3x1 emotion strips, or 4x3 fallback grids) to
                // composite them onto the portrait frame rather than centering as object icons.
                bool isSimPortrait = reference.Columns == 3 || reference.Rows == 3 ||
                                     (reference.Columns == 4 && reference.Rows == 3) ||
                                     ((int)Math.Round(width, MidpointRounding.AwayFromZero) == 35 &&
                                      (int)Math.Round(height, MidpointRounding.AwayFromZero) == 41);
                if (isSimPortrait)
                {
                    parts.Add(new Part(ActivePeople, actionRect, new Vector4(0f, 0f, 0.25f, 1f)));
                }
                else if (width * scale < 45.0 || height * scale < 45.0)
                {
                    parts.Add(new Part(_thumbTemplate, actionRect, new Vector4(0f, 0f, 0.25f, 1f)));
                }

                // Single-column three-row portrait sheets select row 1 for the neutral expression, whereas
                // 4x3 fallback face sheets use row 0.
                float row = reference.Columns == 1 && reference.Rows == 3 ? 1f : 0f;
                parts.Add(new Part(
                    image,
                    new CGRect(
                        MidX(actionRect) - width * scale / 2.0,
                        MidY(actionRect) - height * scale / 2.0,
                        width * scale,
                        height * scale),
                    new Vector4(0f, row / reference.Rows, 1f / reference.Columns, 1f / reference.Rows)));
            }
            else
            {
                parts.Add(new Part(_catalogUnknown, actionRect, new Vector4(0f, 0f, 0.5f, 1f)));
            }

            parts.Add(new Part(_iconFrame, actionRect, new Vector4(action.Active ? 0.5f : 0f, 0f, 0.5f, 1f)));
            if (IsActionCancelRequested(action))
            {
                parts.Add(new Part(_cancel, actionRect));
            }
        }

        AppendReelParts(top, parts);

        if (!panelMinimized &&
            (_motivePopupIndex is not null || _personalityPopupTrait is not null || _zodiacPopupActive ||
             RelationshipPopupId is not null || SkillPopupIndex is not null || _housePopupIndex is not null ||
             _careerSummaryPopupIndex is not null) &&
            _personalityPopupTitle is { } popupTitle)
        {
            var bodyFont = LivePopupBodyFont;
            int height = Math.Max(PersonalityPopup.MinimumHeight, 24 + Font.LineHeight + bodyFont.LineHeight * _personalityPopupLines.Length);
            double bottomOffset = RelationshipPopupId is null ? 100.0 : 150.0;
            double fallbackMidX = Math.Min(canvas.Width - PersonalityPopup.Width / 2.0 - 8.0, 620.0);
            double popupX = OriginalUIScalingLayout.LivePopupOriginX(
                _livePopupAnchorMidX ?? fallbackMidX,
                canvas.Width,
                PersonalityPopup.Width);
            var rect = new CGRect(popupX, canvas.Height - bottomOffset - height, PersonalityPopup.Width, height);
            if (SimsBitmapFont.SharpTextEnabled)
            {
                var vecPopup = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderPopupInfoFrame(
                    Math.Max(32, (int)Math.Round(rect.Width)),
                    Math.Max(32, (int)Math.Round(rect.Height)),
                    scale: 4));
                parts.Add(new Part(vecPopup, rect));
            }
            else
            {
                const double edge = 12.0;
                double[] xs = [rect.X, rect.X + edge, rect.X + rect.Width - edge, rect.X + rect.Width];
                double[] ys = [rect.Y, rect.Y + edge, rect.Y + rect.Height - edge, rect.Y + rect.Height];
                float[] uv = [0f, 1f / 3f, 2f / 3f, 1f];

                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        parts.Add(new Part(
                            _livePopupBackground,
                            new CGRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]),
                            new Vector4(uv[column], uv[row], uv[column + 1] - uv[column], uv[row + 1] - uv[row])));
                    }
                }
            }

            if (_personalityPopupPortrait is (var portrait, var reference))
            {
                var frameRect = new CGRect(Math.Floor(rect.X + (168.0 - 45.0) / 2.0), Math.Floor(MidY(rect) - 22.5), 45, 45);
                parts.Add(new Part(ActivePeople, frameRect, new Vector4(0f, 0f, 0.25f, 1f)));
                var faceSize = new CGSize(portrait.Size.Width / reference.Columns, portrait.Size.Height / reference.Rows);
                int mood = State?.Selected?.Mood ?? 0;
                int expression = reference.Columns >= 3 ? (mood > 30 ? 1 : mood < -30 ? 2 : 0) : 0;
                parts.Add(new Part(
                    portrait,
                    new CGRect(
                        Math.Floor(MidX(frameRect) - faceSize.Width / 2.0),
                        Math.Floor(MidY(frameRect) - faceSize.Height / 2.0),
                        faceSize.Width,
                        faceSize.Height),
                    new Vector4((float)expression / reference.Columns, 0f, 1f / reference.Columns, 1f / reference.Rows)));
            }

            double textX = rect.X + (_personalityPopupPortrait is not null ? PersonalityPopup.TextLeft : 15);
            double bodyStartY = rect.Y + 12.0 + Font.LineHeight;
            parts.Add(new Part(popupTitle, new CGRect(textX, rect.Y + 12.0, popupTitle.Size.Width, popupTitle.Size.Height)));
            for (int index = 0; index < _personalityPopupLines.Length; index++)
            {
                var line = _personalityPopupLines[index];
                parts.Add(new Part(line, new CGRect(textX, bodyStartY + index * bodyFont.LineHeight, line.Size.Width, line.Size.Height)));
            }
        }

        return parts;
    }

    public IReadOnlyList<Part> TooltipParts(CGSize canvas)
    {
        string? currentIdentity = CurrentTooltipIdentity;
        if (_suppressedTooltipIdentity is { } suppressed)
        {
            if (string.Equals(currentIdentity, suppressed, StringComparison.Ordinal))
            {
                return [];
            }

            _suppressedTooltipIdentity = null;
        }

        double top = canvas.Height - 150.0;
        if (_hoveredInventoryTile is { } invIndex && State?.Selected?.Inventory is { } values)
        {
            var items = OriginalGameplayLayout.InventoryItems(values, InventorySort);
            int capacity = StripInventoryCapacity;
            int itemIndex = InventoryPage * capacity + invIndex;
            if (itemIndex < 0 || itemIndex >= items.Count || invIndex >= capacity)
            {
                return [];
            }

            var item = items[itemIndex];
            string itemTitle = item.Name ?? $"Unknown Item ({item.Guid})";
            string itemDesc = item.Description ?? string.Empty;
            string text;
            if (HasVisibleTooltipText(itemTitle) && HasVisibleTooltipText(itemDesc))
            {
                text = $"{itemTitle}\n{itemDesc}";
            }
            else if (HasVisibleTooltipText(itemTitle))
            {
                text = itemTitle;
            }
            else if (HasVisibleTooltipText(itemDesc))
            {
                text = itemDesc;
            }
            else
            {
                return [];
            }

            if (PlacementTooltip.Progress(ref _inventoryHoverStartedAt, _quickTips) is not { } progress)
            {
                return [];
            }

            Image tooltip;
            if (_inventoryTooltipCache is { } cached && string.Equals(cached.Text, text, StringComparison.Ordinal))
            {
                tooltip = cached.Image;
            }
            else
            {
                try
                {
                    tooltip = CreateImage(Device, PlacementTooltip.RasterizeHiDpi(text, _careerFont));
                    _inventoryTooltipCache = (text, tooltip);
                }
                catch
                {
                    return [];
                }
            }

            var tileRect = Offset(InventoryTileRect(invIndex), 0, top);
            return
            [
                Grown(new Part(tooltip, PlacementTooltip.Rect(tooltip.Size, new CGPoint(MidX(tileRect), tileRect.Y - 1.0), canvas)), progress)
            ];
        }

        if (_hoveredPeopleTip is { } peopleTip && HasVisibleTooltipText(peopleTip.Text))
        {
            if (PlacementTooltip.Progress(ref _indicatorHoverStartedAt, _quickTips) is not { } progress)
            {
                return [];
            }

            if (CachedTooltipImage(_peopleTips, peopleTip.Text) is not { } tooltip)
            {
                return [];
            }

            return
            [
                Grown(new Part(tooltip, PlacementTooltip.Rect(tooltip.Size, new CGPoint(MidX(peopleTip.Rect), peopleTip.Rect.Y - 1.0), canvas)), progress)
            ];
        }

        if (_hoveredPerson is { } person && HasVisibleTooltipText(person.Name))
        {
            if (PlacementTooltip.Progress(ref _personHoverStartedAt, _quickTips) is not { } progress)
            {
                return [];
            }

            if (CachedTooltipImage(_personTips, person.Name) is not { } tooltip)
            {
                return [];
            }

            return
            [
                Grown(new Part(tooltip, PlacementTooltip.Rect(tooltip.Size, new CGPoint(MidX(person.Rect), person.Rect.Y - 1.0), canvas)), progress)
            ];
        }

        if (_panelTip is { } panelTip)
        {
            string text = _strings.PanelTip(panelTip.Index, string.Empty);
            if (!HasVisibleTooltipText(text))
            {
                return [];
            }

            if (PlacementTooltip.Progress(ref _indicatorHoverStartedAt, _quickTips) is not { } progress)
            {
                return [];
            }

            if (!_panelTips.TryGetValue(panelTip.Index, out var tooltip))
            {
                try
                {
                    tooltip = CreateImage(Device, PlacementTooltip.RasterizeHiDpi(text, _careerFont));
                    _panelTips[panelTip.Index] = tooltip;
                }
                catch
                {
                    return [];
                }
            }

            return
            [
                Grown(new Part(tooltip, PlacementTooltip.Rect(tooltip.Size, new CGPoint(MidX(panelTip.Rect), panelTip.Rect.Y - 1.0), canvas)), progress)
            ];
        }

        if (_indicatorHovered is { } hover && _seasonTooltips.TryGetValue(hover, out var seasonTip))
        {
            if (PlacementTooltip.Progress(ref _indicatorHoverStartedAt, _quickTips) is { } progress)
            {
                var control = Offset(hover == CycleIndicator.Hover.Weather ? CycleIndicator.GlyphRect : CycleIndicator.ReelRect, 0, top);
                return
                [
                    Grown(new Part(seasonTip.Image, PlacementTooltip.Rect(seasonTip.Image.Size, new CGPoint(MidX(control), control.Y - 1.0), canvas)), progress)
                ];
            }
        }

        return [];
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, bool panelMinimized = false)
    {
        TryPrepare(canvas);
        double now = MonotonicNowSeconds();
        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        var queueControls = new List<(string Label, CGRect Rect)>();
        for (int index = 0; index < maxQueue; index++)
        {
            var action = _queued[index].Action;
            if (!IsActionCancelRequested(action) && action.Cancellable)
            {
                queueControls.Add(("Cancel " + (action.Name ?? "action"), CurrentActionRect(index, now)));
            }
        }

        if (panelMinimized)
        {
            return queueControls;
        }

        double top = canvas.Height - 150.0;
        var controls = new List<(string Label, CGRect Rect)>();

        if (State?.Household is { } household)
        {
            int count = Math.Min(OriginalGameplayLayout.MaxHouseholdPortraits, household.Count);
            for (int offset = 0; offset < count; offset++)
            {
                var sim = household[offset];
                var portraitRect = Offset(OriginalGameplayLayout.Portrait(offset), 0, top);
                controls.Add(("Select " + sim.Name, portraitRect));
                if (!string.IsNullOrEmpty(sim.InternalName) &&
                    !string.Equals(sim.InternalName, sim.Name, StringComparison.OrdinalIgnoreCase))
                {
                    controls.Add(("Select " + sim.InternalName, portraitRect));
                }
            }
        }

        controls.AddRange(queueControls);

        var supportedPanels = new List<Subpanel>();
        foreach (var panel in MainTabSubpanels)
        {
            if (Supports(panel, State))
            {
                supportedPanels.Add(panel);
            }
        }

        foreach (var panel in supportedPanels)
        {
            controls.Add((_strings.PeoplePanelTip(panel.ButtonIndex(), State?.Selected, panel.Label()), Offset(ButtonRect(panel.ButtonIndex()), 0, top)));
        }

        foreach (var panel in supportedPanels)
        {
            controls.Add(("Show " + panel.Label(), Offset(ButtonRect(panel.ButtonIndex()), 0, top)));
        }

        if (CurrentSubpanel == Subpanel.Career && Supports(Subpanel.ReportCard, State))
        {
            var careerLayout = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout);
            controls.Add((_strings.ReportCardLabel(), Offset(careerLayout.ReportCardRect, 0, top)));
        }

        if (CurrentSubpanel == Subpanel.Career && State?.Selected?.Career is { } career)
        {
            var careerLayout = OriginalGameplayLayout.GetCareerSubpanelLayout(_grid.Width, _asianInventoryLayout);
            controls.Add(("Job summary", Offset(careerLayout.JobSummaryRect, 0, top)));
            controls.Add(("Salary summary", Offset(careerLayout.SalaryRect, 0, top)));
            controls.Add(("Performance summary", Offset(careerLayout.PerformanceRect, 0, top)));
            if (career.FriendsNeeded is not null)
            {
                controls.Add(("Friends needed summary", Offset(careerLayout.FriendsRect, 0, top)));
            }

            for (int index = 0; index < OriginalGameplayLayout.Skills.Count && index < careerLayout.Skills.Count; index++)
            {
                controls.Add(("Skill " + OriginalGameplayLayout.Skills[index], Offset(careerLayout.Skills[index].Label, 0, top)));
            }
        }
        else if (CurrentSubpanel == Subpanel.House)
        {
            var houseLayout = OriginalGameplayLayout.GetHouseSubpanelLayout(_grid.Width, _asianInventoryLayout);
            string[] scoreNames = ["Size", "Furnishings", "Yard", "Upkeep", "Layout"];
            for (int index = 0; index < scoreNames.Length && index < houseLayout.ScoreBars.Count; index++)
            {
                controls.Add(("House " + scoreNames[index], Offset(houseLayout.ScoreBars[index], 0, top)));
            }

            string[] infoNames = ["Sq. Ft.", "Bedrooms", "Bathrooms", "Lot"];
            for (int index = 0; index < infoNames.Length && index < houseLayout.InfoRects.Count; index++)
            {
                controls.Add(("House " + infoNames[index], Offset(houseLayout.InfoRects[index], 0, top)));
            }
        }
        else if (CurrentSubpanel == Subpanel.Fame)
        {
            var fameLayout = OriginalGameplayLayout.GetFameSubpanelLayout(_grid.Width, _asianInventoryLayout);
            controls.Add(("Fame level", Offset(fameLayout.StarBar, 0, top)));
            controls.Add(("Fame friends", Offset(fameLayout.FriendsRect, 0, top)));
            for (int index = 0; index < OriginalGameplayLayout.Skills.Count && index < fameLayout.Skills.Count; index++)
            {
                controls.Add(("Skill " + OriginalGameplayLayout.Skills[index], Offset(fameLayout.Skills[index].Label, 0, top)));
            }
        }
        else if (CurrentSubpanel == Subpanel.PetSkills)
        {
            var (_, petSkillSlots) = OriginalGameplayLayout.PetSkillsSubpanelLayout(_grid.Width);
            for (int index = 0; index < OriginalGameplayLayout.PetSkills.Count && index < petSkillSlots.Count; index++)
            {
                controls.Add(("Pet skill " + OriginalGameplayLayout.PetSkills[index], Offset(petSkillSlots[index].Label, 0, top)));
            }
        }
        else if (CurrentSubpanel == Subpanel.Personality && State?.Selected is { } selected && selected.Personality is not null)
        {
            bool isPet = ((selected.Gender ?? 0) & 6) != 0;
            var activeTraitLabels = isPet ? _petTraitLabels : _traitLabels;
            int labelWidth = MaxImageWidth(activeTraitLabels);
            var personalityLayout = OriginalGameplayLayout.PersonalitySubpanelLayout(_grid.Width, labelWidth, _asianInventoryLayout);
            for (int index = 0; index < OriginalGameplayLayout.Traits.Count && index < personalityLayout.TraitRects.Count; index++)
            {
                controls.Add(("Trait " + OriginalGameplayLayout.Traits[index], Offset(personalityLayout.TraitRects[index], 0, top)));
            }
        }

        if (CurrentSubpanel == Subpanel.Relationships)
        {
            var allFilters = Enum.GetValues<OriginalGameplayLayout.RelationshipFilter>();
            for (int index = 0; index < allFilters.Length; index++)
            {
                controls.Add((_strings.RelationshipFilterTip(index, string.Empty), Offset(RelationshipFilterRect(index), 0, top)));
            }

            for (int index = 0; index < allFilters.Length; index++)
            {
                controls.Add(($"Filter {RelationshipFilterName(allFilters[index])} relationships", Offset(RelationshipFilterRect(index), 0, top)));
            }

            var relationships = OriginalGameplayLayout.Relationships(State?.Selected?.Relationships ?? [], RelationshipFilter);
            int capacity = StripRelationshipCapacity;
            int slot = 0;
            foreach (var rel in relationships.Skip(RelationshipPage * capacity).Take(capacity))
            {
                controls.Add((rel.Name, Offset(RelationshipSlotRect(slot++), 0, top)));
            }

            if (RelationshipPage > 0)
            {
                var arrowRect = Offset(RelationshipArrowRect(false), 0, top);
                controls.Add((_strings.PeoplePageTip(0, "Previous relationships page"), arrowRect));
                controls.Add(("Previous relationships page", arrowRect));
            }

            if ((RelationshipPage + 1) * capacity < relationships.Count)
            {
                var arrowRect = Offset(RelationshipArrowRect(true), 0, top);
                controls.Add((_strings.PeoplePageTip(1, "Next relationships page"), arrowRect));
                controls.Add(("Next relationships page", arrowRect));
            }
        }
        else if (CurrentSubpanel == Subpanel.Inventory && State?.Selected?.Inventory is { } invValues)
        {
            var allSorts = Enum.GetValues<OriginalGameplayLayout.InventorySort>();
            for (int index = 0; index < allSorts.Length; index++)
            {
                int tipIndex = allSorts[index] switch
                {
                    OriginalGameplayLayout.InventorySort.Gift => 0,
                    OriginalGameplayLayout.InventorySort.Magic => 1,
                    _ => 2
                };
                controls.Add((_strings.InventorySortTip(tipIndex, "Show " + InventorySortName(allSorts[index]) + " inventory"), Offset(InventorySortRect(index), 0, top)));
            }

            for (int index = 0; index < allSorts.Length; index++)
            {
                controls.Add(("Show " + InventorySortName(allSorts[index]) + " inventory", Offset(InventorySortRect(index), 0, top)));
            }

            var items = OriginalGameplayLayout.InventoryItems(invValues, InventorySort);
            int capacity = StripInventoryCapacity;
            int slot = 0;
            foreach (var item in items.Skip(InventoryPage * capacity).Take(capacity))
            {
                controls.Add(($"{item.Name ?? "Item"} x{item.Count}", Offset(InventoryTileRect(slot++), 0, top)));
            }

            if (InventoryPage > 0)
            {
                var arrowRect = Offset(InventoryArrowRect(false), 0, top);
                controls.Add((_strings.PeoplePageTip(0, "Previous inventory page"), arrowRect));
                controls.Add(("Previous inventory page", arrowRect));
            }

            if ((InventoryPage + 1) * capacity < items.Count)
            {
                var arrowRect = Offset(InventoryArrowRect(true), 0, top);
                controls.Add((_strings.PeoplePageTip(1, "Next inventory page"), arrowRect));
                controls.Add(("Next inventory page", arrowRect));
            }
        }
        else if (CurrentSubpanel == Subpanel.Interests && State?.Selected?.Career is { } interestCareer)
        {
            var indices = OriginalGameplayLayout.InterestIndices(interestCareer.Age);
            int capacity = StripInterestCapacity;
            int slot = 0;
            foreach (int interestIndex in indices.Skip(InterestPage * capacity).Take(capacity))
            {
                controls.Add((_strings.InterestTip(interestIndex, OriginalGameplayLayout.Interests[interestIndex]), Offset(InterestSlotRect(slot++), 0, top)));
            }

            if (InterestPage > 0)
            {
                var arrowRect = Offset(InterestArrowRect(false), 0, top);
                controls.Add((_strings.PeoplePageTip(0, "Previous interests page"), arrowRect));
                controls.Add(("Previous interests page", arrowRect));
            }

            if ((InterestPage + 1) * capacity < indices.Count)
            {
                var arrowRect = Offset(InterestArrowRect(true), 0, top);
                controls.Add((_strings.PeoplePageTip(1, "Next interests page"), arrowRect));
                controls.Add(("Next interests page", arrowRect));
            }
        }

        return controls;
    }

    /// <summary>Resolves the screen bounds for tutorial button highlights, mapping the target identifier to
    /// a subpanel tab, household member portrait, or relationship button.</summary>
    public CGRect? TutorialButtonForImage(int imageId, CGSize canvas)
    {
        foreach (var panel in MainTabSubpanels)
        {
            if (panel.ImageId() == imageId && Supports(panel, State))
            {
                return Offset(ButtonRect(panel.ButtonIndex()), 0, canvas.Height - 150.0);
            }
        }

        return null;
    }

    public CGRect? TutorialButtonForSim(int simId, CGSize canvas)
    {
        if (State?.Household is not { } household)
        {
            return null;
        }

        int count = Math.Min(OriginalGameplayLayout.MaxHouseholdPortraits, household.Count);
        for (int index = 0; index < count; index++)
        {
            if (household[index].Id == simId)
            {
                return Offset(OriginalGameplayLayout.Portrait(index), 0, canvas.Height - 150.0);
            }
        }

        return null;
    }

    public CGRect? TutorialButtonForRelationship(int neighborId, CGSize canvas)
    {
        TryPrepare(canvas);
        if (CurrentSubpanel != Subpanel.Relationships)
        {
            return null;
        }

        var relationships = OriginalGameplayLayout.Relationships(State?.Selected?.Relationships ?? [], RelationshipFilter);
        int capacity = StripRelationshipCapacity;
        int slot = 0;
        foreach (var relationship in relationships.Skip(RelationshipPage * capacity).Take(capacity))
        {
            if (relationship.NeighborId == neighborId)
            {
                return Offset(RelationshipSlotRect(slot), 0, canvas.Height - 150.0);
            }

            slot++;
        }

        return null;
    }

    public int? Selection(CGPoint point, CGSize canvas)
    {
        if (State?.Household is not { } household)
        {
            return null;
        }

        double top = canvas.Height - 150.0;
        int count = Math.Min(OriginalGameplayLayout.MaxHouseholdPortraits, household.Count);
        for (int index = 0; index < count; index++)
        {
            if (Contains(Offset(OriginalGameplayLayout.Portrait(index), 0, top), point))
            {
                return household[index].Id;
            }
        }

        return null;
    }

    public void UpdatePersonHover(CGPoint point, CGSize canvas)
    {
        if (State?.Household is not { } household)
        {
            HoveredPerson = null;
            return;
        }

        double top = canvas.Height - 150.0;
        int count = Math.Min(OriginalGameplayLayout.MaxHouseholdPortraits, household.Count);
        for (int index = 0; index < count; index++)
        {
            var rect = Offset(OriginalGameplayLayout.Portrait(index), 0, top);
            if (Contains(rect, point))
            {
                var sim = household[index];
                string tip = sim.TooltipName ?? sim.Name;
                if (sim.IsOnFocusedLot == false && !string.IsNullOrWhiteSpace(sim.LotName))
                {
                    tip = $"{tip} ({sim.LotName})";
                }
                HoveredPerson = (sim.Id, tip, rect);
                return;
            }
        }

        HoveredPerson = null;
    }

    public CGRect CurrentActionRect(int index, double? now = null)
    {
        var slotRect = OriginalGameplayLayout.Action(index);
        if (index < 0 || index >= _queued.Count || !InterfaceEffects ||
            !_actionAnimations.TryGetValue(_queued[index].Action.Token, out var anim))
        {
            return slotRect;
        }

        var origin = anim.CurrentOrigin(now ?? MonotonicNowSeconds());
        return new CGRect(origin.X, origin.Y, slotRect.Width, slotRect.Height);
    }

    public void MarkActionCancelRequested(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _optimisticCancelledTokens.Add(token);
    }

    public int? HoveredActionIndex(CGPoint point, CGSize canvas)
    {
        double now = MonotonicNowSeconds();
        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        for (int index = 0; index < maxQueue; index++)
        {
            if (Contains(CurrentActionRect(index, now), point))
            {
                return index;
            }
        }

        return null;
    }

    public string? Cancellation(CGPoint point, CGSize canvas)
    {
        double now = MonotonicNowSeconds();
        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        for (int index = 0; index < maxQueue; index++)
        {
            var action = _queued[index].Action;
            if (!IsActionCancelRequested(action) && action.Cancellable && Contains(CurrentActionRect(index, now), point))
            {
                return action.Token;
            }
        }

        return null;
    }

    public bool HasUncancellableAction(CGPoint point, CGSize canvas)
    {
        double now = MonotonicNowSeconds();
        int maxQueue = Math.Min(_queued.Count, Math.Max(0, (int)((canvas.Width - 32.0) / 50.0)));
        for (int index = 0; index < maxQueue; index++)
        {
            var action = _queued[index].Action;
            if (!IsActionCancelRequested(action) && !action.Cancellable && Contains(CurrentActionRect(index, now), point))
            {
                return true;
            }
        }

        return false;
    }

    public double ActivePanelRightEdge(CGSize canvas) => canvas.Width;

    public static bool ContainsPanel(CGPoint point, CGSize canvas, bool panelMinimized = false)
    {
        if (panelMinimized)
        {
            return Contains(new CGRect(0, canvas.Height - 183, 220, 183), point);
        }

        return (point.Y >= canvas.Height - 100 && point.X >= 0 && point.X <= canvas.Width) ||
               Contains(new CGRect(0, canvas.Height - 183, 220, 183), point) ||
               Contains(new CGRect(220, canvas.Height - 150, 193, 150), point);
    }

    public bool ContainsPanelPoint(CGPoint point, CGSize canvas, bool panelMinimized = false) =>
        ContainsPanel(point, canvas, panelMinimized);

    private double ClassicStripOffset => 0.0;

    private double ReelPosition
    {
        get
        {
            if (_reelSpin is not { } spin)
            {
                return 0.0;
            }

            return CycleIndicator.Spin(spin.From, spin.To, (double)spin.Frame / CycleIndicator.SpinFrames);
        }
    }

    private string? CurrentTooltipIdentity
    {
        get
        {
            if (_hoveredInventoryTile is { } index)
            {
                return $"inv:{InventorySortName(InventorySort)}:{InventoryPage}:{index}";
            }

            if (_hoveredPeopleTip is { } tip && HasVisibleTooltipText(tip.Text))
            {
                return $"people:{tip.Text}:{(int)Math.Round(tip.Rect.X, MidpointRounding.AwayFromZero)}:{(int)Math.Round(tip.Rect.Y, MidpointRounding.AwayFromZero)}";
            }

            if (_hoveredPerson is { } person && HasVisibleTooltipText(person.Name))
            {
                return $"person:{person.Id}";
            }

            if (_panelTip is { } panelTip && HasVisibleTooltipText(_strings.PanelTip(panelTip.Index, string.Empty)))
            {
                return $"panel:{panelTip.Index}";
            }

            if (_indicatorHovered is { } hover && _seasonTooltips.ContainsKey(hover))
            {
                return $"ind:{hover}";
            }

            return null;
        }
    }

    private CGPoint StripHeaderOrigin() => OriginalGameplayLayout.InventoryTitle(_asianInventoryLayout);
    private CGRect RelationshipFilterRect(int index) => OriginalGameplayLayout.RelationshipFilterRect(index);
    private CGRect RelationshipSlotRect(int index) => OriginalGameplayLayout.Relationship(index);
    private CGRect RelationshipArrowRect(bool next) => _grid.RelationshipArrow(next);
    private CGRect InventorySortRect(int index) => OriginalGameplayLayout.InventorySortButton(index, _asianInventoryLayout);
    private CGRect InventoryTileRect(int index) => OriginalGameplayLayout.InventoryTile(index, _asianInventoryLayout);
    private CGRect InventoryArrowRect(bool next) => OriginalGameplayLayout.InventoryArrow(next, _grid.Width, _asianInventoryLayout);
    private CGRect InterestSlotRect(int index) => _grid.Interest(index);
    private CGRect InterestArrowRect(bool next) => _grid.InterestArrow(next);
    private CGRect ButtonRect(int index) => new(_buttonPositions[index].X, _buttonPositions[index].Y, _buttons[index].Size.Width / 4.0, _buttons[index].Size.Height);

    private bool IsActionCancelRequested(GameplayPanelState.Action action) =>
        action.CancelRequested || _optimisticCancelledTokens.Contains(action.Token);

    private void TryPrepare(CGSize canvas)
    {
        try
        {
            Prepare(canvas);
        }
        catch
        {
        }
    }

    private void AppendBevelDivider(double x, List<Part> parts) =>
        parts.Add(new Part(_bevelDivider, new CGRect(x, 55, 2, 89)));

    private void AppendBayDividers(OriginalGameplayLayout.CenteredSubpanelBay bay, List<Part> parts)
    {
        if (bay.DividerLeftX is { } leftX)
        {
            AppendBevelDivider(leftX, parts);
        }

        if (bay.DividerRightX is { } rightX)
        {
            AppendBevelDivider(rightX, parts);
        }
    }

    private void AppendHouseParts(GameplayPanelState.HousePanel values, List<Part> parts)
    {
        var layout = OriginalGameplayLayout.GetHouseSubpanelLayout(_grid.Width, _asianInventoryLayout);
        var header = Text(_strings.HouseLabel(0, "House"), _careerFont, Device);
        parts.Add(new Part(header, new CGRect(layout.HeaderOrigin.X, layout.HeaderOrigin.Y, header.Size.Width, header.Size.Height)));

        int?[] scores = [values.SizeScore, values.FurnishingsScore, values.YardScore, values.UpkeepScore, values.LayoutScore];
        string[] fallbacks = ["Size", "Furnishings", "Yard", "Upkeep", "Layout"];
        for (int index = 0; index < scores.Length; index++)
        {
            var label = Text(_strings.HouseLabel(index + 1, fallbacks[index]), _careerFont, Device);
            double y = layout.ScoreLabelYs[index];
            double labelX = layout.ScoreLabelRightXs[index] - label.Size.Width;
            parts.Add(new Part(label, new CGRect(labelX, y, label.Size.Width, label.Size.Height)));
            var barRect = layout.ScoreBars[index];
            parts.Add(new Part(_houseBarsOff, barRect));
            if (scores[index] is { } score)
            {
                int barW = Math.Min(10, Math.Max(0, score / 10)) * 4;
                if (barW > 0)
                {
                    parts.Add(new Part(_personalityBars, new CGRect(barRect.X, barRect.Y, barW, barRect.Height), new Vector4(0f, 0f, barW / 40f, 1f)));
                }
            }
        }

        if (values.SquareFeet is { } squareFeet)
        {
            var textImg = Text(FormatHouseCount(_strings.HouseLabel(6, "Sq. Ft.: %d"), squareFeet), _careerFont, Device);
            parts.Add(new Part(textImg, new CGRect(layout.InfoRects[0].X, layout.InfoRects[0].Y, textImg.Size.Width, textImg.Size.Height)));
        }

        if (values.Bedrooms is { } bedrooms)
        {
            var textImg = Text(FormatHouseCount(_strings.HouseLabel(7, "Bedrooms: %d"), bedrooms), _careerFont, Device);
            parts.Add(new Part(textImg, new CGRect(layout.InfoRects[1].X, layout.InfoRects[1].Y, textImg.Size.Width, textImg.Size.Height)));
        }

        if (values.Bathrooms is { } bathrooms)
        {
            var textImg = Text(FormatHouseCount(_strings.HouseLabel(8, "Bathrooms: %d"), bathrooms), _careerFont, Device);
            parts.Add(new Part(textImg, new CGRect(layout.InfoRects[2].X, layout.InfoRects[2].Y, textImg.Size.Width, textImg.Size.Height)));
        }

        if (values.LotSize is { } lotSize)
        {
            var textImg = Text(FormatHouseLot(lotSize), _careerFont, Device);
            parts.Add(new Part(textImg, new CGRect(layout.InfoRects[3].X, layout.InfoRects[3].Y, textImg.Size.Width, textImg.Size.Height)));
        }
    }

    private string FormatHouseLot(int lotSize)
    {
        string[] fallbackSizes = ["Small", "Medium", "Large"];
        string fallback = lotSize >= 0 && lotSize < fallbackSizes.Length ? fallbackSizes[lotSize] : "Large";
        string sizeName = _strings.HouseLotSize(lotSize, fallback);
        string format = _strings.HouseLabel(9, "Lot: %@");
        return format.Replace("%s", sizeName, StringComparison.Ordinal).Replace("%@", sizeName, StringComparison.Ordinal);
    }

    private static string FormatHouseCount(string format, int value) =>
        format.Replace("%d", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private void AppendReelParts(double top, List<Part> parts)
    {
        if (_reelSpin is not { } spin ||
            Environment?.Effect.Indicator is not { Count: > 0 } indicator ||
            _reel is not { } reel)
        {
            return;
        }

        var window = Offset(CycleIndicator.ReelRect, 0, top);
        if (indicator.Count == 4)
        {
            string seasonKey = $"lcd_season:{indicator.Index}";
            if (!_indicatorImages.TryGetValue(seasonKey, out var seasonImg))
            {
                seasonImg = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderLcdGreenSeasonIcon(indicator.Index, CycleIndicator.Width, scale: 4));
                _indicatorImages[seasonKey] = seasonImg;
            }
            parts.Add(new Part(seasonImg, window));
        }
        else
        {
            int row = CycleIndicator.StripRow(ReelPosition, indicator.Count);
            float height = (indicator.Count + 1) * CycleIndicator.Cell;
            parts.Add(new Part(reel.Window, window));
            parts.Add(new Part(
                reel.Strip,
                Inset(window, 1, 1),
                new Vector4(0f, row / height, 1f, CycleIndicator.Symbol / height)));
            parts.Add(new Part(reel.Drum, window));
        }
        if (spin.Frame < CycleIndicator.SpinFrames)
        {
            spin.Frame += 1;
            _reelSpin = spin;
        }
    }

    /// <summary>Updates rolling rate-of-change buffers for motive trends sampled once per Sim-minute.
    /// Paused states do not append duplicate samples, preserving active arrow indicator states.</summary>
    private void UpdateMotiveTracking(GameplayPanelState next, string resolvedRoot, ulong nowMs)
    {
        int motiveCount = OriginalGameplayLayout.Motives.Count;
        var nextHouseholdIds = new HashSet<int>();
        foreach (var sim in next.Household)
        {
            nextHouseholdIds.Add(sim.Id);
        }

        int simMinute = next.Hours * 60 + next.Minutes;
        bool clockReset = _lastSampledSimMinute is { } prevMin && (simMinute - prevMin + 1440) % 1440 > 60;

        if ((_resourceRoot is not null && !PathsEqual(_resourceRoot, resolvedRoot)) ||
            nextHouseholdIds.Count == 0 ||
            (_trackedHouseholdSimIds.Count > 0 && !_trackedHouseholdSimIds.Overlaps(nextHouseholdIds)) ||
            clockReset)
        {
            _motiveTrackersBySim.Clear();
            _lastSampledMotivesBySim.Clear();
            _lastSampledSimMinute = null;
            _trackedHouseholdSimIds.Clear();
            _trackedSelectedSimId = null;
            for (int index = 0; index < motiveCount; index++)
            {
                _motiveArrowsStates[index].Reset(hasTracker: false, nowMs: nowMs);
                _motiveBarStates[index] = new OriginalGameplayLayout.MotiveBarState();
            }
        }

        foreach (int id in _motiveTrackersBySim.Keys.ToArray())
        {
            if (!nextHouseholdIds.Contains(id))
            {
                _motiveTrackersBySim.Remove(id);
            }
        }

        foreach (int id in _lastSampledMotivesBySim.Keys.ToArray())
        {
            if (!nextHouseholdIds.Contains(id))
            {
                _lastSampledMotivesBySim.Remove(id);
            }
        }

        _trackedHouseholdSimIds = nextHouseholdIds;

        int[] MotiveVector(GameplayPanelState.Sim sim)
        {
            var vec = new int[motiveCount];
            for (int i = 0; i < motiveCount; i++)
            {
                int raw = sim.Motives.TryGetValue(OriginalGameplayLayout.Motives[i], out var v) ? v : 0;
                vec[i] = Math.Min(100, Math.Max(-100, raw));
            }

            return vec;
        }

        bool didSample = false;
        if (_lastSampledSimMinute is { } previousMinute)
        {
            int elapsedMinutes = (simMinute - previousMinute + 1440) % 1440;
            if (elapsedMinutes > 0)
            {
                int steps = Math.Min(elapsedMinutes, 10);
                foreach (var sim in next.Household)
                {
                    var current = MotiveVector(sim);
                    if (_motiveTrackersBySim.TryGetValue(sim.Id, out var trackers))
                    {
                        var previous = _lastSampledMotivesBySim.TryGetValue(sim.Id, out var prevVec) ? prevVec : current;
                        for (int step = 1; step <= steps; step++)
                        {
                            for (int index = 0; index < motiveCount; index++)
                            {
                                int sample = step == steps
                                    ? current[index]
                                    : OriginalGameplayLayout.MotiveRateTracker.RoundHalfAwayFromZero(
                                        previous[index] + (current[index] - previous[index]) * ((float)step / steps));
                                trackers[index].Record(sample);
                            }
                        }
                    }
                    else
                    {
                        trackers = new OriginalGameplayLayout.MotiveRateTracker[motiveCount];
                        for (int index = 0; index < motiveCount; index++)
                        {
                            trackers[index] = new OriginalGameplayLayout.MotiveRateTracker();
                            trackers[index].Record(current[index]);
                        }

                        _motiveTrackersBySim[sim.Id] = trackers;
                    }

                    _lastSampledMotivesBySim[sim.Id] = current;
                }

                _lastSampledSimMinute = simMinute;
                didSample = true;
            }
            else
            {
                foreach (var sim in next.Household)
                {
                    if (_motiveTrackersBySim.ContainsKey(sim.Id))
                    {
                        continue;
                    }

                    var current = MotiveVector(sim);
                    var trackers = new OriginalGameplayLayout.MotiveRateTracker[motiveCount];
                    for (int index = 0; index < motiveCount; index++)
                    {
                        trackers[index] = new OriginalGameplayLayout.MotiveRateTracker();
                        trackers[index].Record(current[index]);
                    }

                    _motiveTrackersBySim[sim.Id] = trackers;
                    _lastSampledMotivesBySim[sim.Id] = current;
                }
            }
        }
        else if (next.Household.Count > 0)
        {
            foreach (var sim in next.Household)
            {
                var current = MotiveVector(sim);
                var trackers = new OriginalGameplayLayout.MotiveRateTracker[motiveCount];
                for (int index = 0; index < motiveCount; index++)
                {
                    trackers[index] = new OriginalGameplayLayout.MotiveRateTracker();
                    trackers[index].Record(current[index]);
                }

                _motiveTrackersBySim[sim.Id] = trackers;
                _lastSampledMotivesBySim[sim.Id] = current;
            }

            _lastSampledSimMinute = simMinute;
            didSample = true;
        }

        if (next.Selected is { } selected)
        {
            var current = MotiveVector(selected);
            bool simChanged = _trackedSelectedSimId != selected.Id;
            _trackedSelectedSimId = selected.Id;
            _motiveTrackersBySim.TryGetValue(selected.Id, out var trackers);
            for (int index = 0; index < motiveCount; index++)
            {
                if (simChanged)
                {
                    _motiveBarStates[index].Reset(current[index], nowMs, InterfaceEffects);
                    _motiveArrowsStates[index].Reset(hasTracker: trackers is not null, nowMs: nowMs);
                    if (trackers is not null)
                    {
                        _motiveArrowsStates[index].Update(trackers[index].Rate, nowMs, InterfaceEffects);
                    }
                }
                else
                {
                    _motiveBarStates[index].SetValue(current[index], nowMs, InterfaceEffects);
                    if (didSample && trackers is not null)
                    {
                        _motiveArrowsStates[index].Update(trackers[index].Rate, nowMs, InterfaceEffects);
                    }
                }
            }
        }
        else if (_trackedSelectedSimId is not null)
        {
            _trackedSelectedSimId = null;
            for (int index = 0; index < motiveCount; index++)
            {
                _motiveArrowsStates[index].Reset(hasTracker: false, nowMs: nowMs);
            }
        }
    }

    private void AppendNeedsDynamicParts(double top, ulong nowMs, List<Part> dynamic)
    {
        // Quantizes animated motive-pulse color sampling to approximately 30 Hz so high-refresh display
        // loops can reuse cached textures.
        ulong sampledMs = (nowMs / 33UL) * 33UL;
        int motiveCount = OriginalGameplayLayout.Motives.Count;
        for (int index = 0; index < motiveCount; index++)
        {
            var needRect = OriginalGameplayLayout.NeedRect(index, _grid.Width);
            var barEval = _motiveBarStates[index].Evaluate(sampledMs);
            int width = OriginalGameplayLayout.BarWidth(_motiveBarStates[index].Value);
            var barKey = new MotiveBarCacheKey(width, barEval.GreenRgb, barEval.RedRgb);
            Image? barImage;
            if (_cachedMotiveBars[index] is { } cachedBar && cachedBar.Key == barKey)
            {
                barImage = cachedBar.Image;
            }
            else
            {
                var green = OriginalGameplayLayout.MotiveColorRamp.UnpackRgb(barEval.GreenRgb);
                var red = OriginalGameplayLayout.MotiveColorRamp.UnpackRgb(barEval.RedRgb);
                var pixels = new byte[60 * 5 * 4];
                for (int y = 0; y < 5; y++)
                {
                    for (int x = 0; x < 60; x++)
                    {
                        var color = x < width ? green : red;
                        bool isDivider = (60 - width > 1) && (x == width - 1);
                        int offset = (y * 60 + x) * 4;
                        pixels[offset] = isDivider ? (byte)0 : color.X;
                        pixels[offset + 1] = isDivider ? (byte)0 : color.Y;
                        pixels[offset + 2] = isDivider ? (byte)0 : color.Z;
                        pixels[offset + 3] = 255;
                    }
                }

                try
                {
                    var created = CreateImage(Device, 60, 5, pixels);
                    _cachedMotiveBars[index] = (barKey, created);
                    barImage = created;
                }
                catch
                {
                    barImage = _cachedMotiveBars[index]?.Image;
                }
            }

            if (barImage is { } resolvedBar)
            {
                dynamic.Add(new Part(resolvedBar, new CGRect(needRect.X + 20.0, needRect.Y + 12.0 + top, 60.0, 5.0)));
            }

            var arrowEval = _motiveArrowsStates[index].Evaluate(sampledMs);
            if (!arrowEval.Visible || arrowEval.Colors.Count != 5)
            {
                continue;
            }

            var stripKey = new MotiveArrowStripCacheKey(
                arrowEval.Rising,
                arrowEval.Colors[0],
                arrowEval.Colors[1],
                arrowEval.Colors[2],
                arrowEval.Colors[3],
                arrowEval.Colors[4]);
            Image? stripImage;
            if (_cachedMotiveArrowStrips[index] is { } cachedStrip && cachedStrip.Key == stripKey)
            {
                stripImage = cachedStrip.Image;
            }
            else
            {
                var pixels = new byte[19 * 5 * 4];
                var localStrip = OriginalGameplayLayout.MotiveArrowStripRect(new CGRect(0, 0, 0, 0), arrowEval.Rising);
                for (int arrowIndex = 0; arrowIndex < 5; arrowIndex++)
                {
                    uint colorRgb = arrowEval.Colors[arrowIndex];
                    if (colorRgb == OriginalGameplayLayout.MotiveArrowBackgroundRgb)
                    {
                        continue;
                    }

                    var rgb = OriginalGameplayLayout.MotiveColorRamp.UnpackRgb(colorRgb);
                    var origin = OriginalGameplayLayout.MotiveArrowOrigin(new CGRect(0, 0, 0, 0), arrowEval.Rising, arrowIndex);
                    int baseX = (int)(origin.X - localStrip.X);
                    for (int py = 0; py < 5; py++)
                    {
                        for (int px = 0; px < 3; px++)
                        {
                            if (!OriginalGameplayLayout.MotiveArrowMaskPixel(arrowEval.Rising, px, py))
                            {
                                continue;
                            }

                            int offset = (py * 19 + baseX + px) * 4;
                            pixels[offset] = rgb.X;
                            pixels[offset + 1] = rgb.Y;
                            pixels[offset + 2] = rgb.Z;
                            pixels[offset + 3] = 255;
                        }
                    }
                }

                try
                {
                    var created = CreateImage(Device, 19, 5, pixels);
                    _cachedMotiveArrowStrips[index] = (stripKey, created);
                    stripImage = created;
                }
                catch
                {
                    stripImage = _cachedMotiveArrowStrips[index]?.Image;
                }
            }

            if (stripImage is { } resolvedStrip)
            {
                var stripRect = Offset(OriginalGameplayLayout.MotiveArrowStripRect(needRect, arrowEval.Rising), 0, top);
                dynamic.Add(new Part(resolvedStrip, stripRect));
            }
        }
    }

    private void SetLivePopup(string title, string description, (Image, GameplayPanelState.Image)? portrait = null)
    {
        int leftInset = portrait is not null ? PersonalityPopup.TextLeft : 15;
        int wrapWidth = Math.Max(120, PersonalityPopup.TextRight - leftInset);
        var bodyFont = LivePopupBodyFont;
        _personalityPopupTitle = Text(title, Font, Device, LivePopupTextColor);
        var wrapped = bodyFont.WrappedLines(description, wrapWidth);
        var lineImages = new Image[wrapped.Count];
        for (int i = 0; i < wrapped.Count; i++)
        {
            lineImages[i] = Text(wrapped[i], bodyFont, Device, LivePopupTextColor);
        }

        _personalityPopupLines = lineImages;
        _personalityPopupPortrait = portrait;
    }

    private void UpdateMotivePopup()
    {
        bool isPet = ((State?.Selected?.Gender ?? 0) & 6) != 0;
        if (CurrentSubpanel != Subpanel.Needs ||
            _hoveredNeed is not { } index ||
            _strings.MotiveHelp(index, isPet) is not { } description)
        {
            if (_motivePopupIndex is null)
            {
                return;
            }

            _motivePopupIndex = null;
            _livePopupAnchorMidX = null;
            _personalityPopupTitle = null;
            _personalityPopupLines = [];
            _personalityPopupPortrait = null;
            return;
        }

        string title = _strings.MotiveLabel(index, isPet, OriginalGameplayLayout.Motives[index]);
        _personalityPopupTrait = null;
        _zodiacPopupActive = false;
        SkillPopupIndex = null;
        _housePopupIndex = null;
        RelationshipPopupId = null;
        _relationshipPopupText = null;
        _careerSummaryPopupIndex = null;
        _motivePopupIndex = index;
        _livePopupAnchorMidX = MidX(OriginalGameplayLayout.NeedRect(index, _grid.Width));
        try
        {
            SetLivePopup(title, description);
        }
        catch
        {
        }
    }

    private void ClearLivePopupState()
    {
        _motivePopupIndex = null;
        _personalityPopupTrait = null;
        _zodiacPopupActive = false;
        SkillPopupIndex = null;
        _housePopupIndex = null;
        RelationshipPopupId = null;
        _relationshipPopupText = null;
        _careerSummaryPopupIndex = null;
        _livePopupAnchorMidX = null;
        _personalityPopupTitle = null;
        _personalityPopupLines = [];
        _personalityPopupPortrait = null;
    }

    private bool Supports(Subpanel panel, GameplayPanelState? state) => panel switch
    {
        Subpanel.Needs => true,
        Subpanel.House => state?.House is not null,
        Subpanel.Relationships => state?.Selected?.Relationships is not null,
        Subpanel.Personality => state?.Selected?.Personality is not null,
        Subpanel.Career => state?.Selected?.HasHumanCareer == true && state.Selected.Fame is null,
        Subpanel.PetSkills => state?.Selected?.HasPetSkills == true,
        Subpanel.Fame => state?.Selected?.Fame is not null,
        Subpanel.Interests => state?.Selected?.HasHumanInterests == true && ((state.Selected.Gender ?? 0) & 6) == 0,
        Subpanel.Inventory => state?.Selected?.Inventory is not null && ((state.Selected.Gender ?? 0) & 6) == 0,
        Subpanel.ReportCard => state?.Selected?.Career is { IsSchool: true } c && c.ReportCardGrade is not null,
        _ => false
    };

    private void UpdateRelationshipPopup(GameplayPanelState.Relationship target)
    {
        if (_strings.RelationshipPopup(target) is not (var title, var description))
        {
            RelationshipPopupId = null;
            return;
        }

        string key = $"{title}\0{description}";
        if (string.Equals(_relationshipPopupText, key, StringComparison.Ordinal))
        {
            return;
        }

        SetLivePopup(title, description);
        _relationshipPopupText = key;
    }

    private Image? CachedTooltipImage(Dictionary<string, Image> cache, string text)
    {
        if (cache.TryGetValue(text, out var existing))
        {
            return existing;
        }

        try
        {
            var created = CreateImage(Device, PlacementTooltip.RasterizeHiDpi(text, _careerFont));
            cache[text] = created;
            return created;
        }
        catch
        {
            return null;
        }
    }

    private static Part Grown(Part part, double progress)
    {
        if (progress >= 1.0 || part.Rect.Height <= 0.0)
        {
            return part;
        }

        double lift = (1.0 - progress) * 2.0;
        return new Part(
            part.Image,
            new CGRect(part.Rect.X, part.Rect.Y + lift, part.Rect.Width, part.Rect.Height),
            part.UV,
            part.Opacity * (float)Math.Clamp(progress, 0.0, 1.0));
    }

    private static Image? TryBitmap(Func<string, Image> loader, string asset)
    {
        try
        {
            return loader(asset);
        }
        catch
        {
            return null;
        }
    }

    private static int MaxImageWidth(Image[] images)
    {
        int max = 0;
        foreach (var img in images)
        {
            int w = (int)img.Size.Width;
            if (w > max)
            {
                max = w;
            }
        }

        return max;
    }

    private static bool HasVisibleTooltipText(string text) => !string.IsNullOrWhiteSpace(text);

    private static string RelationshipFilterName(OriginalGameplayLayout.RelationshipFilter filter) => filter switch
    {
        OriginalGameplayLayout.RelationshipFilter.Family => "family",
        OriginalGameplayLayout.RelationshipFilter.Friends => "friends",
        OriginalGameplayLayout.RelationshipFilter.Famous => "famous",
        OriginalGameplayLayout.RelationshipFilter.All => "all",
        _ => "all"
    };

    private static string InventorySortName(OriginalGameplayLayout.InventorySort sort) => sort switch
    {
        OriginalGameplayLayout.InventorySort.Magic => "magic",
        OriginalGameplayLayout.InventorySort.Other => "other",
        OriginalGameplayLayout.InventorySort.Gift => "gift",
        _ => "gift"
    };

    private static double MidX(CGRect rect) => rect.X + rect.Width / 2.0;
    private static double MidY(CGRect rect) => rect.Y + rect.Height / 2.0;

    private static CGRect Offset(CGRect rect, double dx, double dy) =>
        new(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

    private static CGRect Inset(CGRect rect, double dx, double dy) =>
        new(rect.X + dx, rect.Y + dy, rect.Width - 2.0 * dx, rect.Height - 2.0 * dy);

    private static CGRect RectUnion(CGRect a, CGRect b)
    {
        double minX = Math.Min(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxX = Math.Max(a.X + a.Width, b.X + b.Width);
        double maxY = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new CGRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0 && rect.Height > 0 &&
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    private static bool PathsEqual(string? lhs, string? rhs) =>
        string.Equals(
            lhs,
            rhs,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveRealPath(string path) => LotScene.ResolveRealPath(path);

    private static double MonotonicNowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

public static class OriginalGameplayOverlaySubpanelExtensions
{
    public static int ButtonIndex(this OriginalGameplayOverlay.Subpanel subpanel) => subpanel switch
    {
        OriginalGameplayOverlay.Subpanel.Needs => 0,
        OriginalGameplayOverlay.Subpanel.Relationships => 1,
        OriginalGameplayOverlay.Subpanel.House => 3,
        OriginalGameplayOverlay.Subpanel.Personality => 4,
        OriginalGameplayOverlay.Subpanel.Career => 2,
        OriginalGameplayOverlay.Subpanel.Fame => 2,
        OriginalGameplayOverlay.Subpanel.PetSkills => 2,
        OriginalGameplayOverlay.Subpanel.Interests => 5,
        OriginalGameplayOverlay.Subpanel.Inventory => 6,
        OriginalGameplayOverlay.Subpanel.ReportCard => 2,
        _ => 0
    };

    public static int ImageId(this OriginalGameplayOverlay.Subpanel subpanel) =>
        subpanel.ButtonIndex() switch
        {
            0 => 4500,
            1 => 4504,
            2 => 4503,
            3 => 4502,
            4 => 4501,
            5 => 4505,
            6 => 4507,
            _ => 4500
        };

    public static string Label(this OriginalGameplayOverlay.Subpanel subpanel) => subpanel switch
    {
        OriginalGameplayOverlay.Subpanel.Needs => "Needs",
        OriginalGameplayOverlay.Subpanel.Relationships => "Relationships",
        OriginalGameplayOverlay.Subpanel.House => "House",
        OriginalGameplayOverlay.Subpanel.Personality => "Personality",
        OriginalGameplayOverlay.Subpanel.Career => "Career",
        OriginalGameplayOverlay.Subpanel.Fame => "Fame",
        OriginalGameplayOverlay.Subpanel.PetSkills => "Pet Skills",
        OriginalGameplayOverlay.Subpanel.Interests => "Interests",
        OriginalGameplayOverlay.Subpanel.Inventory => "Inventory",
        OriginalGameplayOverlay.Subpanel.ReportCard => "Report Card",
        _ => string.Empty
    };
}
