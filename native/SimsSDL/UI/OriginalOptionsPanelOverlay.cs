using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Serialization;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>
/// The original control-panel Options mode. Button offsets come from UIText STR# 145 (<c>optionstrs</c>), which stores
/// "(x;y)" positions before each button's tooltip: Neighborhood (88;6), Save (29;6), Quit (159;6), Graphics (29;58),
/// Sound (89;58) and Play (151;58). The executable creates the buttons from the ID table at 0x66c364 in that order
/// (FUN_00514110). Offsets are relative to the sub-panel origin, which sits right of the 220-pixel universal panel on
/// top of <c>PanelBack.bmp</c>.
/// </summary>
public sealed class OriginalOptionsPanelOverlay
{
    public enum Button
    {
        Neighborhood,
        Save,
        Quit,
        Graphics,
        Sound,
        Play,
        /// <summary>
        /// Shown on the Play page only (FUN_004f2661 runs when it is clicked). FUN_005154a3 does not use the STR# 145 entry 68
        /// position: it puts the control 3 pixels from the right edge of the 580-pixel sub-panel, 7 pixels down.
        /// </summary>
        ResetTutorial
    }

    public enum Page
    {
        None,
        Graphics,
        Sound,
        Play
    }

    /// <summary>
    /// Persisted Play Options (UIText STR# 145 entries 54–67 and 72–74). Only options the native client honors are enabled.
    /// </summary>
    public record struct PlayOptionsState
    {
        [JsonPropertyName("autoCenter")]
        public bool AutoCenter { get; set; } = true;

        [JsonPropertyName("freeWill")]
        public bool FreeWill { get; set; } = true;

        [JsonPropertyName("edgeScrolling")]
        public bool EdgeScrolling { get; set; } = true;

        [JsonPropertyName("quickTips")]
        public bool QuickTips { get; set; } = true;

        [JsonPropertyName("lighting")]
        public bool Lighting { get; set; } = true;

        // RestoreFactorySettings initializes the Mac option manager's backing flag to 1.
        [JsonPropertyName("simInBackground")]
        public bool SimInBackground { get; set; } = true;

        // RestoreFactorySettings enables these flags and sets both detail levels to high.
        [JsonPropertyName("antiAlias")]
        public bool AntiAlias { get; set; } = true;

        [JsonPropertyName("shadows")]
        public bool Shadows { get; set; } = true;

        [JsonPropertyName("interfaceEffects")]
        public bool InterfaceEffects { get; set; } = true;

        [JsonPropertyName("autoSnapshot")]
        public bool AutoSnapshot { get; set; } = true;

        [JsonPropertyName("livePIP")]
        public bool LivePIP { get; set; } = true;

        [JsonPropertyName("exportHTML")]
        public bool ExportHTML { get; set; } = true;

        /// <summary>
        /// Graphics page Terrain Detail and Character Detail: 0 low, 1 medium, 2 high. Sims.exe's two default presets
        /// (FUN_004e7045, FUN_004e711d) set them to high/high and medium/low; absent values mean high.
        /// </summary>
        [JsonPropertyName("terrainDetail")]
        public int TerrainDetail { get; set; } = 2;

        [JsonPropertyName("characterDetail")]
        public int CharacterDetail { get; set; } = 2;

        public PlayOptionsState() { }

        public readonly int Detail(DetailSetting setting) =>
            Math.Clamp(setting == DetailSetting.Terrain ? TerrainDetail : CharacterDetail, 0, 2);
    }

    /// <summary>The Graphics page's two three-way radio rows (FUN_0051449d).</summary>
    public enum DetailSetting
    {
        Terrain,
        Character
    }

    public enum PlayOption
    {
        // Play page, column B (STR# 145 entries 54/55): original order of the option text blocks.
        AutoCenter,
        FreeWill,
        EdgeScrolling,
        SimInBackground,
        // Play page, column A (placed after column B, see PlayColumn): the second group of text blocks after Reset Tutorial.
        QuickTips,
        AutoSnapshot,
        LivePIP,
        ExportHTML,
        // Graphics page (entries 12/13).
        AntiAlias,
        Shadows,
        Lighting,
        InterfaceEffects
    }

    private abstract record PopupTopic
    {
        public sealed record OptionTopic(PlayOption Option) : PopupTopic;
        public sealed record DetailTopic(DetailSetting Setting) : PopupTopic;
        public sealed record SoundTopic(int Index) : PopupTopic;
        public sealed record ResetTutorialTopic : PopupTopic;
    }

    private readonly struct SlidePopupEntry
    {
        public OriginalGameplayOverlay.Image Title { get; init; }
        public List<OriginalGameplayOverlay.Image> Body { get; init; }
        public OriginalGameplayOverlay.Image Detail { get; init; }
    }

    private struct RowLayout
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    public static readonly CGPoint SubpanelOrigin = new(220, 50);

    private static readonly Button[] AllButtons = Enum.GetValues<Button>();
    private static readonly DetailSetting[] AllDetailSettings = Enum.GetValues<DetailSetting>();
    private static readonly PlayOption[] AllPlayOptions = Enum.GetValues<PlayOption>();

    /// <summary>STR# 145 entries 54/55: first checkbox row and row size, relative to the sub-panel origin.</summary>
    private RowLayout _playRows = new() { Origin = new CGPoint(275, 9), Size = new CGSize(200, 20) };

    /// <summary>
    /// Graphics page (FUN_0051449d reads STR# 145 in order): checkbox labels at entry 12 with row size 13, checkboxes at
    /// 34 with row size 35, detail labels ending at 26 with row size 27, 80 x 20 radio rows (36) at 37 with row size 38.
    /// </summary>
    private RowLayout _graphicsLabels = new() { Origin = new CGPoint(275, 9), Size = new CGSize(200, 20) };
    private RowLayout _graphicsBoxes = new() { Origin = new CGPoint(245, 7), Size = new CGSize(200, 20) };
    private RowLayout _detailLabels = new() { Origin = new CGPoint(480, 30), Size = new CGSize(200, 26) };
    private CGSize _radioSize = new(80, 20);
    private RowLayout _radioRows = new() { Origin = new CGPoint(485, 30), Size = new CGSize(200, 26) };

    private readonly OriginalGameplayOverlay.Image _radio;
    private readonly UIDevice _device;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly SimsBitmapFont _labelFont;
    private readonly SimsBitmapFont _levelFont;
    private readonly SimsBitmapFont _popupTitleFont;
    private readonly SimsBitmapFont _popupBodyFont;
    private readonly OriginalGameplayOverlay.Image _popupTiles;
    private readonly OriginalGameplayOverlay.Image _toothpickHoriz;
    private readonly OriginalGameplayOverlay.Image _toothpickVert;
    private readonly Dictionary<PopupTopic, SlidePopupEntry> _slidePopups = new();
    private PopupTopic? _activePopupTopic;
    private double? _popupOpenedAt;
    private (string Text, OriginalGameplayOverlay.Image Image)? _tooltipImage;
    private string? _tooltipText;
    private CGPoint _tooltipAnchor = CGPoint.Zero;
    private double? _tooltipStartedAt;
    private bool _tooltipQuickTips = true;
    private Button? _suppressedTooltipButton;
    private readonly Dictionary<DetailSetting, OriginalGameplayOverlay.Image> _detailTitles = new();
    private readonly Dictionary<DetailSetting, OriginalGameplayOverlay.Image> _detailHoverTitles = new();
    private readonly Dictionary<DetailSetting, string> _detailAbout = new();
    private readonly List<OriginalGameplayOverlay.Image> _levelLabels = [];
    private readonly OriginalGameplayOverlay.Image _checkbox;
    private readonly Dictionary<PlayOption, OriginalGameplayOverlay.Image> _playLabels = new();
    private readonly Dictionary<PlayOption, OriginalGameplayOverlay.Image> _playHoverLabels = new();
    private readonly Dictionary<PlayOption, string> _playAbout = new();
    private readonly string _resetTutorialAbout;
    private readonly OriginalGameplayOverlay.Image _universal;
    private readonly OriginalGameplayOverlay.Image _patch;
    private readonly Dictionary<Button, OriginalGameplayOverlay.Image> _buttons = new();
    private readonly Dictionary<Button, CGPoint> _positions = new();

    private PlayOptionsState _playOptions = new();
    private bool _lotSaveDisabled;
    private double? _pageTransitionStarted;
    private double _pageFadeInDelay;
    private List<OriginalGameplayOverlay.Part>? _transitionFromParts;
    private List<OriginalGameplayOverlay.Part> _lastPageContent = [];

    public IReadOnlyDictionary<Button, string> Labels { get; }
    public Page CurrentPage { get; private set; } = Page.None;
    public Button? Hovered { get; private set; }
    public DetailSetting? HoveredDetail { get; private set; }
    public (DetailSetting Setting, int Level)? HoveredRadio { get; private set; }
    public PlayOption? HoveredOption { get; private set; }

    public PlayOptionsState PlayOptions
    {
        get => _playOptions;
        set => _playOptions = value;
    }

    /// <summary>Set when on a non-residential / away lot (<c>005154a3.c:229-231</c>, <c>00517545.c:73-82</c>).</summary>
    public bool LotSaveDisabled
    {
        get => _lotSaveDisabled;
        set
        {
            _lotSaveDisabled = value;
            if (Hovered is { } hovered && !IsEnabled(hovered))
            {
                Hovered = null;
                _suppressedTooltipButton = null;
                _tooltipText = null;
                _tooltipStartedAt = null;
            }
            if (HoveredOption is { } hoveredOption && !IsEnabled(hoveredOption))
            {
                HoveredOption = null;
            }
        }
    }

    public bool IsTransitioning
    {
        get
        {
            if (!_playOptions.InterfaceEffects || _pageTransitionStarted is not { } started) return false;
            var totalDuration = CurrentPage != Page.None ? _pageFadeInDelay + 0.333 : 0.333;
            return NowSeconds() - started < totalDuration;
        }
    }

    /// <summary>STR# 145 entries 42/43: the first sound slider row and its row size.</summary>
    public static (CGPoint Origin, CGSize Size) SoundRows(OriginalUIStrings strings) =>
        (OriginalUIStrings.Point(strings.OptionString(42, "(305;13)")) ?? new CGPoint(305, 13),
         OriginalUIStrings.Point(strings.OptionString(43, "(200;28)")) is { } s ? new CGSize(s.X, s.Y) : new CGSize(200, 28));

    public OriginalOptionsPanelOverlay(GameContent content, UIDevice device, OriginalUIStrings strings, SimsBitmapFont font)
    {
        _device = device;
        _tooltipFont = font;
        var optFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_09.ffn") ?? font;
        var smallFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_08.ffn") ?? optFont;
        var titleFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? font;
        _labelFont = optFont;
        _levelFont = smallFont;
        _popupTitleFont = titleFont;
        _popupBodyFont = optFont;

        OriginalGameplayOverlay.Image Bitmap(string path)
        {
            var image = new SimsBitmap(content.Read(path));
            return OriginalGameplayOverlay.CreateImage(device, image.Width, image.Height, image.Rgba, magenta: true);
        }

        OriginalGameplayOverlay.Image MakeText(string text, SimsBitmapFont f, (byte R, byte G, byte B) color)
        {
            var raster = f.Rasterize(text, color);
            return OriginalGameplayOverlay.CreateImage(device, raster.Width, raster.Height, raster.Rgba);
        }

        List<OriginalGameplayOverlay.Image> MakeLines(string raw)
        {
            var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            var wrapped = new List<string>();
            foreach (var paragraph in normalized.Split('\n'))
            {
                var lines = optFont.WrappedLines(paragraph, 357);
                if (lines.Count == 0)
                {
                    wrapped.Add(string.Empty);
                }
                else
                {
                    wrapped.AddRange(lines);
                }
            }

            var result = new List<OriginalGameplayOverlay.Image>(wrapped.Count);
            foreach (var line in wrapped)
            {
                if (string.IsNullOrEmpty(line))
                {
                    result.Add(new OriginalGameplayOverlay.Image(new UITexture(1, 1, [0, 0, 0, 0]), CGSize.Zero));
                }
                else
                {
                    result.Add(MakeText(line, optFont, (195, 205, 205)));
                }
            }
            return result;
        }

        SlidePopupEntry MakePopup(int titleIndex, int bodyIndex, string fallbackTitle, string asset)
        {
            var rawTitle = strings.OptionString(titleIndex, fallbackTitle).Trim();
            var rawBody = strings.OptionString(bodyIndex, string.Empty);
            return new SlidePopupEntry
            {
                Title = MakeText(rawTitle, titleFont, (255, 255, 255)),
                Body = MakeLines(rawBody),
                Detail = Bitmap(asset)
            };
        }

        var baseTga = new SimsTarga(content.Read("UIGraphics/cpanel/Backgrounds/UniversalBack.tga"));
        _universal = OriginalGameplayOverlay.CreateImage(device, baseTga.Width, baseTga.Height, baseTga.Rgba);
        _patch = Bitmap("UIGraphics/cpanel/Backgrounds/OptionsPatch.bmp");
        _popupTiles = Bitmap("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp");
        _toothpickHoriz = Bitmap("UIGraphics/CPanel/Backgrounds/OptionsToothpkHoriz.bmp");
        _toothpickVert = Bitmap("UIGraphics/CPanel/Backgrounds/OptionsToothpkVert.bmp");

        var labels = new Dictionary<Button, string>();
        foreach (var button in AllButtons)
        {
            var image = Bitmap(button.Asset());
            _buttons[button] = image;
            var pos = button == Button.ResetTutorial
                ? null
                : OriginalUIStrings.Point(strings.OptionString(button.StringIndex(), string.Empty));
            _positions[button] = pos ?? (button == Button.ResetTutorial
                ? new CGPoint(580 - image.Size.Width / 4.0 - 3, 7)
                : button.FallbackPosition());
            labels[button] = strings.OptionString(button.StringIndex() + 1, button.FallbackLabel());
        }
        Labels = labels;

        CGPoint? Point(int index) => OriginalUIStrings.Point(strings.OptionString(index, string.Empty));
        CGSize? Size(int index) => Point(index) is { } p ? new CGSize(p.X, p.Y) : null;

        _checkbox = Bitmap("UIGraphics/cpanel/Buttons/optcheckbox.bmp");
        if (Point(54) is { } playOrigin) _playRows.Origin = playOrigin;
        if (Size(55) is { } playSize) _playRows.Size = playSize;

        _radio = Bitmap("UIGraphics/cpanel/Buttons/optradio.bmp");
        if (Point(12) is { } glOrigin) _graphicsLabels.Origin = glOrigin;
        if (Size(13) is { } glSize) _graphicsLabels.Size = glSize;
        if (Point(34) is { } gbOrigin) _graphicsBoxes.Origin = gbOrigin;
        if (Size(35) is { } gbSize) _graphicsBoxes.Size = gbSize;
        if (Point(26) is { } dlOrigin) _detailLabels.Origin = dlOrigin;
        if (Size(27) is { } dlSize) _detailLabels.Size = dlSize;
        if (Size(36) is { } rsSize) _radioSize = rsSize;
        if (Point(37) is { } rrOrigin) _radioRows.Origin = rrOrigin;
        if (Size(38) is { } rrSize) _radioRows.Size = rrSize;

        foreach (var setting in AllDetailSettings)
        {
            var labelStr = strings.OptionString(setting.StringIndex(), setting.FallbackLabel());
            _detailTitles[setting] = MakeText(labelStr, optFont, (195, 205, 205));
            _detailHoverTitles[setting] = MakeText(labelStr, optFont, (255, 255, 255));
            _detailAbout[setting] = strings.OptionString(setting.StringIndex() + 2, string.Empty);
            _slidePopups[new PopupTopic.DetailTopic(setting)] = MakePopup(
                setting.StringIndex() + 1,
                setting.StringIndex() + 2,
                setting.FallbackLabel(),
                setting.PopupAsset());
        }

        string[] levelFallbacks = ["Low", "Med", "High"];
        for (var i = 0; i < levelFallbacks.Length; i++)
        {
            _levelLabels.Add(MakeText(strings.OptionString(39 + i, levelFallbacks[i]), smallFont, (195, 205, 205)));
        }

        foreach (var option in AllPlayOptions)
        {
            var labelStr = strings.OptionString(option.StringIndex(), option.FallbackLabel());
            _playLabels[option] = MakeText(labelStr, optFont, (195, 205, 205));
            _playHoverLabels[option] = MakeText(labelStr, optFont, (255, 255, 255));
            _playAbout[option] = strings.OptionString(option.StringIndex() + 2, string.Empty);
            _slidePopups[new PopupTopic.OptionTopic(option)] = MakePopup(
                option.StringIndex() + 1,
                option.StringIndex() + 2,
                option.FallbackLabel(),
                option.PopupAsset());
        }

        (int Index, int TitleIdx, string FallbackTitle, string Asset)[] soundSpecs =
        [
            (0, 45, "Sound Effects", "UIGraphics/CPanel/PopupOptSFX.bmp"),
            (1, 48, "Music", "UIGraphics/CPanel/PopupOptMusic.bmp"),
            (2, 51, "Vox", "UIGraphics/CPanel/PopupOptVox.bmp")
        ];
        foreach (var (idx, titleIdx, fallbackTitle, asset) in soundSpecs)
        {
            _slidePopups[new PopupTopic.SoundTopic(idx)] = MakePopup(titleIdx, titleIdx + 1, fallbackTitle, asset);
        }

        _resetTutorialAbout = strings.OptionString(71, string.Empty);
        _slidePopups[new PopupTopic.ResetTutorialTopic()] = MakePopup(
            70,
            71,
            "About Reset Tutorial",
            "UIGraphics/CPanel/PopupResetTutorial.bmp");
    }

    private static SimsBitmapFont? TryLoadFont(GameContent content, string path)
    {
        try
        {
            return new SimsBitmapFont(content.Read(path));
        }
        catch
        {
            return null;
        }
    }

    private static double NowSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public bool IsEnabled(Button button) =>
        !(_lotSaveDisabled && (button == Button.ResetTutorial || button == Button.Save));

    public bool IsEnabled(PlayOption option)
    {
        if (!option.IsImplemented()) return false;
        if (_lotSaveDisabled && option is PlayOption.LivePIP or PlayOption.AutoSnapshot or PlayOption.QuickTips)
        {
            return false;
        }
        return true;
    }

    /// <summary>Reset Tutorial belongs to the Play page; the other buttons are always shown.</summary>
    public bool IsShown(Button button) => button != Button.ResetTutorial || CurrentPage == Page.Play;

    public void Select(Page page)
    {
        // FUN_005dcbd1 / FUN_005d9dba: clicking an Options button dismisses any active/pending tooltip until hover moves elsewhere.
        if (Hovered is { } hovered) _suppressedTooltipButton = hovered;
        _tooltipText = null;
        _tooltipStartedAt = null;
        if (CurrentPage == page) return;
        var hadActivePage = CurrentPage != Page.None;
        _transitionFromParts = _playOptions.InterfaceEffects ? _lastPageContent : null;
        _pageFadeInDelay = _playOptions.InterfaceEffects && hadActivePage ? 0.333 : 0.0;
        CurrentPage = page;
        _pageTransitionStarted = _playOptions.InterfaceEffects ? NowSeconds() : null;
        _activePopupTopic = null;
        _popupOpenedAt = null;
    }

    private CGRect Rect(Button button, double top)
    {
        if (!_buttons.TryGetValue(button, out var image) || !_positions.TryGetValue(button, out var position))
        {
            return CGRect.Zero;
        }
        // Option button sheets hold normal, selected, hovered and disabled frames side by side.
        return new CGRect(
            SubpanelOrigin.X + position.X,
            top + SubpanelOrigin.Y + position.Y,
            image.Size.Width / 4.0,
            image.Size.Height);
    }

    public Button? ButtonAt(CGPoint point, CGSize canvas)
    {
        var top = canvas.Height - 150;
        foreach (var button in AllButtons)
        {
            if (IsShown(button) && Contains(Rect(button, top), point))
            {
                return button;
            }
        }
        return null;
    }

    public void Hover(CGPoint point, CGSize canvas, int? hoveredSoundIndex = null)
    {
        var top = canvas.Height - 150;
        var hitButton = ButtonAt(point, canvas);
        Button? nextHovered = hitButton is { } b && IsEnabled(b) ? b : null;
        if (nextHovered != _suppressedTooltipButton)
        {
            _suppressedTooltipButton = null;
        }
        Hovered = nextHovered;
        HoveredOption = OptionAt(point, canvas);
        HoveredRadio = RadioAt(point, canvas);

        DetailSetting? detailHit = null;
        if (CurrentPage == Page.Graphics)
        {
            foreach (var setting in AllDetailSettings)
            {
                if (Contains(DetailRowRect(setting, top), point))
                {
                    detailHit = setting;
                    break;
                }
            }
        }
        HoveredDetail = detailHit;

        // FUN_00516284 opens cWinSlidePopup (+0x194/+0x198) for hovered/selected option items.
        PopupTopic? newTopic = null;
        if (HoveredOption is { } option) newTopic = new PopupTopic.OptionTopic(option);
        else if (HoveredDetail is { } detail) newTopic = new PopupTopic.DetailTopic(detail);
        else if (CurrentPage == Page.Sound && hoveredSoundIndex is { } soundIdx) newTopic = new PopupTopic.SoundTopic(soundIdx);
        else if (Hovered == Button.ResetTutorial) newTopic = new PopupTopic.ResetTutorialTopic();

        if (newTopic is not null)
        {
            _popupOpenedAt ??= NowSeconds();
            _activePopupTopic = newTopic;
        }
        else if (point.Y < canvas.Height - 100 || (Hovered is not null && Hovered != Button.ResetTutorial))
        {
            _activePopupTopic = null;
            _popupOpenedAt = null;
        }

        // cWinOptions assigns STR# 145's short button text to the 7 icon buttons with SetToolTipsText.
        Button? tipButton = Hovered is { } h && h != _suppressedTooltipButton ? h : null;
        var text = tipButton is { } tb && Labels.TryGetValue(tb, out var lbl) ? lbl : null;
        if (!string.Equals(text, _tooltipText, StringComparison.Ordinal))
        {
            _tooltipText = text;
            _tooltipStartedAt = null;
        }
        if (tipButton is { } activeBtn && text is not null)
        {
            var buttonRect = Rect(activeBtn, top);
            _tooltipAnchor = new CGPoint(point.X, Math.Min(point.Y, buttonRect.MinY - 1));
        }
    }

    public void ClearHover()
    {
        Hovered = null;
        _suppressedTooltipButton = null;
        HoveredOption = null;
        HoveredRadio = null;
        HoveredDetail = null;
        _activePopupTopic = null;
        _popupOpenedAt = null;
        _tooltipText = null;
        _tooltipStartedAt = null;
    }

    /// <summary>The shared tooltip control's delay and unfurl duration follow cOptionsMgr::SetQuickTips.</summary>
    public List<OriginalGameplayOverlay.Part> TooltipParts(CGSize canvas, bool enabled, bool quickTips = true)
    {
        if (_tooltipQuickTips != quickTips)
        {
            _tooltipStartedAt = null;
            _tooltipQuickTips = quickTips;
        }
        if (!enabled || _tooltipText is null)
        {
            _tooltipStartedAt = null;
            return [];
        }

        var progress = PlacementTooltip.Progress(ref _tooltipStartedAt, quickTips);
        if (progress is null) return [];

        try
        {
            OriginalGameplayOverlay.Image image;
            if (_tooltipImage is { } cached && string.Equals(cached.Text, _tooltipText, StringComparison.Ordinal))
            {
                image = cached.Image;
            }
            else
            {
                var raster = PlacementTooltip.Rasterize(_tooltipText, _tooltipFont);
                image = OriginalGameplayOverlay.CreateImage(_device, raster.Width, raster.Height, raster.Rgba);
                _tooltipImage = (_tooltipText, image);
            }

            var rect = PlacementTooltip.Rect(image.Size, new CGPoint(_tooltipAnchor.X, _tooltipAnchor.Y - 1), canvas);
            // FUN_005d9f30 (005d9f30.c:80, 86-88): truncate unfurl height and keep tooltip hidden while height < 1.
            var height = Math.Floor(rect.Height * progress.Value);
            if (height < 1 || rect.Height <= 0) return [];
            return
            [
                new OriginalGameplayOverlay.Part(
                    image,
                    new CGRect(rect.MinX, rect.MaxY - height, rect.Width, height),
                    new Vector4(0f, (float)(1.0 - height / rect.Height), 1f, (float)(height / rect.Height)))
            ];
        }
        catch
        {
            return [];
        }
    }

    private CGRect OptionRect(PlayOption option, double top)
    {
        if (option.Page() == Page.Graphics)
        {
            // FUN_0051449d (0051449d.c:105-112): each Graphics label button is sized to its rendered string width, not the 200px row stride.
            var box = _graphicsBoxes.Origin;
            var label = _graphicsLabels.Origin;
            var labelWidth = _playLabels.TryGetValue(option, out var img)
                ? img.Size.Width
                : _graphicsLabels.Size.Width - (label.X - box.X);
            return new CGRect(
                SubpanelOrigin.X + box.X,
                top + SubpanelOrigin.Y + box.Y + option.Row() * _graphicsBoxes.Size.Height,
                label.X - box.X + labelWidth,
                _graphicsBoxes.Size.Height);
        }

        var column = PlayColumn(option);
        var width = (_playLabels.TryGetValue(option, out var playImg) ? playImg.Size.Width : _playRows.Size.Width - 30)
            + column.Label - column.Box;
        return new CGRect(
            SubpanelOrigin.X + column.Box,
            top + SubpanelOrigin.Y + column.Y + option.Row() * _playRows.Size.Height,
            width,
            _playRows.Size.Height);
    }

    /// <summary>
    /// FUN_005154a3: column B's labels start at STR# 145 entry 54 with each checkbox a quarter of the checkbox sheet's
    /// width to their left; column A's labels start 70 pixels past column B's checkboxes plus its widest label, at the same Y.
    /// Positions are relative to the sub-panel origin.
    /// </summary>
    private (double Box, double Label, double Y) PlayColumn(PlayOption option)
    {
        var inset = _checkbox.Size.Width / 4.0;
        var boxB = _playRows.Origin.X - inset;
        if (!option.IsColumnA()) return (boxB, _playRows.Origin.X, _playRows.Origin.Y);
        double widest = 0;
        foreach (var candidate in AllPlayOptions)
        {
            if (candidate.Page() == Page.Play && !candidate.IsColumnA() &&
                _playLabels.TryGetValue(candidate, out var lbl) && lbl.Size.Width > widest)
            {
                widest = lbl.Size.Width;
            }
        }
        var label = boxB + widest + 70;
        return (label - inset, label, _playRows.Origin.Y);
    }

    public PlayOption? OptionAt(CGPoint point, CGSize canvas)
    {
        if (CurrentPage is not (Page.Play or Page.Graphics)) return null;
        var top = canvas.Height - 150;
        foreach (var option in AllPlayOptions)
        {
            if (option.Page() == CurrentPage && IsEnabled(option) && Contains(OptionRect(option, top), point))
            {
                return option;
            }
        }
        return null;
    }

    /// <summary>The original "About" help text for the hovered Play, Graphics, or Sound option.</summary>
    public string? AboutText(CGPoint point, CGSize canvas)
    {
        DetailSetting? detail = null;
        if (CurrentPage == Page.Graphics)
        {
            var top = canvas.Height - 150;
            foreach (var setting in AllDetailSettings)
            {
                if (Contains(DetailRowRect(setting, top), point))
                {
                    detail = setting;
                    break;
                }
            }
        }

        string? result = null;
        if (OptionAt(point, canvas) is { } opt && _playAbout.TryGetValue(opt, out var optAbout))
        {
            result = optAbout;
        }
        else if (detail is { } d && _detailAbout.TryGetValue(d, out var detAbout))
        {
            result = detAbout;
        }
        else if (ButtonAt(point, canvas) == Button.ResetTutorial)
        {
            result = _resetTutorialAbout;
        }

        return string.IsNullOrEmpty(result) ? null : result;
    }

    private List<OriginalGameplayOverlay.Part> SlidePopupParts(CGSize canvas)
    {
        if (_activePopupTopic is not { } topic || !_slidePopups.TryGetValue(topic, out var entry)) return [];
        var textHeight = (int)entry.Title.Size.Height + entry.Body.Count * _popupBodyFont.LineHeight;
        double height = Math.Max(127, textHeight + 24);
        const double width = 540;
        var panelBottom = canvas.Height - 100;
        var elapsed = _playOptions.InterfaceEffects
            ? (_popupOpenedAt is { } openedAt ? NowSeconds() - openedAt : 0.25)
            : 0.25;
        var progress = Math.Clamp(elapsed / 0.25, 0.0, 1.0);
        var visibleHeight = Math.Round(height * progress, MidpointRounding.AwayFromZero);
        if (visibleHeight <= 0) return [];

        var x = Math.Min(Math.Max(8, SubpanelOrigin.X + 580 - width), canvas.Width - width);
        var y = panelBottom - visibleHeight;
        var parts = new List<OriginalGameplayOverlay.Part>();
        var tileW = _popupTiles.Size.Width / 3.0;
        var tileH = _popupTiles.Size.Height / 3.0;
        double[] xs = [x, x + tileW, x + width - tileW, x + width];
        double[] ys = [y, y + tileH, y + height - tileH, y + height];

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var cell = new CGRect(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]);
                if (cell.MinY >= panelBottom) continue;
                var clippedH = Math.Min(cell.MaxY, panelBottom) - cell.MinY;
                var vScale = (float)(clippedH / Math.Max(1.0, cell.Height)) / 3.0f;
                parts.Add(new OriginalGameplayOverlay.Part(
                    _popupTiles,
                    new CGRect(cell.MinX, cell.MinY, cell.Width, clippedH),
                    new Vector4(col / 3.0f, row / 3.0f, 1.0f / 3.0f, vScale)));
            }
        }

        var detail = entry.Detail;
        var detailX = x + Math.Floor((168 - detail.Size.Width) / 2.0);
        var detailY = y + Math.Floor((height - detail.Size.Height) / 2.0);
        if (detailY < panelBottom)
        {
            var clippedH = Math.Min(detail.Size.Height, panelBottom - detailY);
            parts.Add(new OriginalGameplayOverlay.Part(
                detail,
                new CGRect(detailX, detailY, detail.Size.Width, clippedH),
                new Vector4(0f, 0f, 1f, (float)(clippedH / detail.Size.Height))));
        }

        var titleY = y + 12;
        if (titleY + entry.Title.Size.Height <= panelBottom)
        {
            parts.Add(new OriginalGameplayOverlay.Part(
                entry.Title,
                new CGRect(x + 178, titleY, entry.Title.Size.Width, entry.Title.Size.Height)));
        }

        var lineY = titleY + _popupTitleFont.LineHeight;
        foreach (var line in entry.Body)
        {
            if (line.Size.Width > 0 && lineY + line.Size.Height <= panelBottom)
            {
                parts.Add(new OriginalGameplayOverlay.Part(
                    line,
                    new CGRect(x + 178, lineY, line.Size.Width, line.Size.Height)));
            }
            lineY += _popupBodyFont.LineHeight;
        }

        return parts;
    }

    /// <summary>Radio buttons sit at the left, centre and right of the 80-pixel row (FUN_005378dd).</summary>
    private CGRect RadioRect(DetailSetting setting, int level, double top)
    {
        var button = _radio.Size.Width / 6.0;
        var x = SubpanelOrigin.X + _radioRows.Origin.X + level * (_radioSize.Width - button) / 2.0;
        return new CGRect(
            Math.Floor(x),
            top + SubpanelOrigin.Y + _radioRows.Origin.Y + (int)setting * _radioRows.Size.Height,
            button,
            _radio.Size.Height);
    }

    /// <summary>The label and its radio row, for the About text.</summary>
    private CGRect DetailRowRect(DetailSetting setting, double top)
    {
        double widest = 0;
        foreach (var img in _detailTitles.Values)
        {
            if (img.Size.Width > widest) widest = img.Size.Width;
        }
        var y = top + SubpanelOrigin.Y + _detailLabels.Origin.Y + (int)setting * _detailLabels.Size.Height;
        return new CGRect(
            SubpanelOrigin.X + _detailLabels.Origin.X - widest,
            y,
            widest + _radioRows.Origin.X - _detailLabels.Origin.X + _radioSize.Width,
            _radioSize.Height);
    }

    public (DetailSetting Setting, int Level)? RadioAt(CGPoint point, CGSize canvas)
    {
        if (CurrentPage != Page.Graphics) return null;
        var top = canvas.Height - 150;
        foreach (var setting in AllDetailSettings)
        {
            for (var level = 0; level < 3; level++)
            {
                if (Contains(RadioRect(setting, level, top), point))
                {
                    return (setting, level);
                }
            }
        }
        return null;
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        var top = canvas.Height - 150;
        var result = new List<(string Label, CGRect Rect)>();
        foreach (var button in AllButtons)
        {
            if (IsEnabled(button) && IsShown(button))
            {
                result.Add((Labels.TryGetValue(button, out var lbl) ? lbl : button.FallbackLabel(), Rect(button, top)));
            }
        }

        if (CurrentPage is not (Page.Play or Page.Graphics)) return result;

        foreach (var option in AllPlayOptions)
        {
            if (option.Page() == CurrentPage && IsEnabled(option))
            {
                result.Add((LabelText(option) + (option.Value(_playOptions) ? ", on" : ", off"), OptionRect(option, top)));
            }
        }

        if (CurrentPage == Page.Graphics)
        {
            string[] levels = ["Low", "Med", "High"];
            foreach (var setting in AllDetailSettings)
            {
                if (!setting.IsImplemented()) continue;
                for (var level = 0; level < 3; level++)
                {
                    var state = _playOptions.Detail(setting) == level ? ", on" : ", off";
                    result.Add(($"{setting.FallbackLabel()} {levels[level]}{state}", RadioRect(setting, level, top)));
                }
            }
        }

        return result;
    }

    private static string LabelText(PlayOption option) => option.FallbackLabel();

    public List<OriginalGameplayOverlay.Part> Parts(CGSize canvas)
    {
        var top = canvas.Height - 150;
        var parts = SlidePopupParts(canvas);
        parts.Add(new OriginalGameplayOverlay.Part(_universal, new CGRect(0, top - 33, _universal.Size.Width, _universal.Size.Height)));
        // FUN_0053a26b blits OptionsPatch (mode 3) at universal-local (0xb7, 0x53) = (183, 83).
        parts.Add(new OriginalGameplayOverlay.Part(_patch, new CGRect(183, top - 33 + 83, _patch.Size.Width, _patch.Size.Height)));
        // FUN_00516165 blits OptionsToothpkHoriz at subpanel-local (0x10, 0x33) = (16, 51) and OptionsToothpkVert at (0xde, 6) = (222, 6).
        parts.Add(new OriginalGameplayOverlay.Part(
            _toothpickHoriz,
            new CGRect(SubpanelOrigin.X + 16, top + SubpanelOrigin.Y + 51, _toothpickHoriz.Size.Width, _toothpickHoriz.Size.Height)));
        parts.Add(new OriginalGameplayOverlay.Part(
            _toothpickVert,
            new CGRect(SubpanelOrigin.X + 222, top + SubpanelOrigin.Y + 6, _toothpickVert.Size.Width, _toothpickVert.Size.Height)));

        foreach (var button in AllButtons)
        {
            if (button == Button.ResetTutorial || !IsShown(button)) continue;
            if (!_buttons.TryGetValue(button, out var image)) continue;
            var selected = (button == Button.Sound && CurrentPage == Page.Sound) ||
                (button == Button.Graphics && CurrentPage == Page.Graphics) ||
                (button == Button.Play && CurrentPage == Page.Play);
            // FUN_005e3a26: col 0 = normal, col 1 = selected, col 2 = hovered, col 3 = disabled.
            var frame = !IsEnabled(button) ? 3.0f : selected ? 1.0f : Hovered == button ? 2.0f : 0.0f;
            parts.Add(new OriginalGameplayOverlay.Part(image, Rect(button, top), new Vector4(frame / 4.0f, 0f, 0.25f, 1f)));
        }

        float effectOpacity;
        float? outgoingOpacity = null;
        if (_pageTransitionStarted is { } started && _playOptions.InterfaceEffects)
        {
            var elapsed = NowSeconds() - started;
            var outProgress = Math.Clamp((float)(elapsed / 0.333), 0f, 1f);
            if (outProgress < 1f)
            {
                outgoingOpacity = 1f - outProgress;
            }
            if (CurrentPage != Page.None)
            {
                var inProgress = elapsed > _pageFadeInDelay
                    ? Math.Clamp((float)((elapsed - _pageFadeInDelay) / 0.333), 0f, 1f)
                    : 0f;
                effectOpacity = inProgress;
                if (inProgress >= 1f)
                {
                    _pageTransitionStarted = null;
                    _transitionFromParts = null;
                    _pageFadeInDelay = 0.0;
                }
            }
            else
            {
                effectOpacity = 0f;
                if (outProgress >= 1f)
                {
                    _pageTransitionStarted = null;
                    _transitionFromParts = null;
                    _pageFadeInDelay = 0.0;
                }
            }
        }
        else
        {
            if (!_playOptions.InterfaceEffects)
            {
                _pageTransitionStarted = null;
                _transitionFromParts = null;
                _pageFadeInDelay = 0.0;
            }
            effectOpacity = 1f;
        }

        var pageContent = new List<OriginalGameplayOverlay.Part>();
        if (CurrentPage == Page.Play && _buttons.TryGetValue(Button.ResetTutorial, out var resetImg))
        {
            var frame = !IsEnabled(Button.ResetTutorial) ? 3.0f : Hovered == Button.ResetTutorial ? 2.0f : 0.0f;
            pageContent.Add(new OriginalGameplayOverlay.Part(
                resetImg,
                Rect(Button.ResetTutorial, top),
                new Vector4(frame / 4.0f, 0f, 0.25f, 1f)));
        }

        if (CurrentPage is Page.Play or Page.Graphics)
        {
            foreach (var option in AllPlayOptions)
            {
                if (option.Page() != CurrentPage) continue;
                var area = OptionRect(option, top);
                // Checkbox sheet: [off, on] x [normal, hover, disabled], 20 pixels wide each.
                var pair = !IsEnabled(option) ? 2.0f : HoveredOption == option ? 1.0f : 0.0f;
                var frame = pair * 2.0f + (option.Value(_playOptions) ? 1.0f : 0.0f);
                var label = HoveredOption == option
                    ? (_playHoverLabels.TryGetValue(option, out var hImg) ? hImg : _playLabels.GetValueOrDefault(option))
                    : _playLabels.GetValueOrDefault(option);
                var labelY = option.Page() == Page.Graphics
                    ? top + SubpanelOrigin.Y + _graphicsLabels.Origin.Y + option.Row() * _graphicsLabels.Size.Height
                    : top + SubpanelOrigin.Y + PlayColumn(option).Y + option.Row() * _playRows.Size.Height;
                var boxY = option.Page() == Page.Graphics
                    ? area.MinY
                    : labelY + (_labelFont.LineHeight - (int)_checkbox.Size.Height) / 2;

                pageContent.Add(new OriginalGameplayOverlay.Part(
                    _checkbox,
                    new CGRect(area.MinX, boxY, _checkbox.Size.Width / 6.0, _checkbox.Size.Height),
                    new Vector4(frame / 6.0f, 0f, 1.0f / 6.0f, 1f)));

                if (label.Size.Width > 0 && label.Size.Height > 0)
                {
                    var labelX = option.Page() == Page.Graphics
                        ? SubpanelOrigin.X + _graphicsLabels.Origin.X
                        : SubpanelOrigin.X + PlayColumn(option).Label;
                    pageContent.Add(new OriginalGameplayOverlay.Part(
                        label,
                        new CGRect(labelX, labelY, label.Size.Width, label.Size.Height)));
                }
            }
        }

        if (CurrentPage == Page.Graphics)
        {
            foreach (var setting in AllDetailSettings)
            {
                var title = HoveredDetail == setting
                    ? (_detailHoverTitles.TryGetValue(setting, out var dh) ? dh : _detailTitles.GetValueOrDefault(setting))
                    : _detailTitles.GetValueOrDefault(setting);
                if (title.Size.Width > 0 && title.Size.Height > 0)
                {
                    // FUN_0051449d calls FUN_005e4ada(2) (right-aligned text) on each detail label so its right edge sits at x = 480.
                    pageContent.Add(new OriginalGameplayOverlay.Part(
                        title,
                        new CGRect(
                            SubpanelOrigin.X + _detailLabels.Origin.X - title.Size.Width,
                            top + SubpanelOrigin.Y + _detailLabels.Origin.Y + (int)setting * _detailLabels.Size.Height,
                            title.Size.Width,
                            title.Size.Height)));
                }

                for (var level = 0; level < 3; level++)
                {
                    // Radio sheet: [off, on] x [normal, hover, disabled], like the checkbox sheet.
                    var isHovered = HoveredRadio is { } hr && hr.Setting == setting && hr.Level == level;
                    var pair = !setting.IsImplemented() ? 2.0f : isHovered ? 1.0f : 0.0f;
                    var frame = pair * 2.0f + (_playOptions.Detail(setting) == level ? 1.0f : 0.0f);
                    pageContent.Add(new OriginalGameplayOverlay.Part(
                        _radio,
                        RadioRect(setting, level, top),
                        new Vector4(frame / 6.0f, 0f, 1.0f / 6.0f, 1f)));
                }
            }

            // FUN_0051449d: Low/Med/High share a control from 8 pixels left of the first radio row to 5 past its end,
            // 7 pixels above it; FUN_00537ca4 gives each label a centred third.
            var first = RadioRect(DetailSetting.Terrain, 0, top);
            var spanMinX = first.MinX - 8;
            var spanWidth = _radioSize.Width + 13;
            var third = Math.Floor(spanWidth / 3.0);
            for (var level = 0; level < _levelLabels.Count; level++)
            {
                var label = _levelLabels[level];
                pageContent.Add(new OriginalGameplayOverlay.Part(
                    label,
                    new CGRect(
                        Math.Floor(spanMinX + level * third + (third - label.Size.Width) / 2.0),
                        first.MinY - 7 - _levelFont.LineHeight,
                        label.Size.Width,
                        label.Size.Height)));
            }
        }

        _lastPageContent = pageContent;
        if (_transitionFromParts is { } outgoing && outgoingOpacity is { } outAlpha && outAlpha > 0f)
        {
            foreach (var p in outgoing)
            {
                parts.Add(new OriginalGameplayOverlay.Part(p.Image, p.Rect, p.UV, outAlpha));
            }
        }

        if (effectOpacity > 0f)
        {
            foreach (var p in pageContent)
            {
                parts.Add(new OriginalGameplayOverlay.Part(p.Image, p.Rect, p.UV, effectOpacity));
            }
        }

        return parts;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}

public static class OriginalOptionsPanelOverlayExtensions
{
    public static string Asset(this OriginalOptionsPanelOverlay.Button button) => button switch
    {
        OriginalOptionsPanelOverlay.Button.Neighborhood => "UIGraphics/cpanel/Buttons/OptNbhd.bmp",
        OriginalOptionsPanelOverlay.Button.Save => "UIGraphics/cpanel/Buttons/OptSave.BMP",
        OriginalOptionsPanelOverlay.Button.Quit => "UIGraphics/cpanel/Buttons/OptExit.bmp",
        OriginalOptionsPanelOverlay.Button.Graphics => "UIGraphics/cpanel/Buttons/optgraphics.bmp",
        OriginalOptionsPanelOverlay.Button.Sound => "UIGraphics/cpanel/Buttons/optsound.bmp",
        OriginalOptionsPanelOverlay.Button.Play => "UIGraphics/cpanel/Buttons/optplay.bmp",
        OriginalOptionsPanelOverlay.Button.ResetTutorial => "UIGraphics/cpanel/Buttons/opttutreset.BMP",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
    };

    /// <summary>STR# 145 index of the "(x;y)" position; the tooltip follows at the next index.</summary>
    public static int StringIndex(this OriginalOptionsPanelOverlay.Button button) => (int)button switch
    {
        0 => 0,
        1 => 2,
        2 => 4,
        3 => 6,
        4 => 8,
        5 => 10,
        6 => 68,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
    };

    public static CGPoint FallbackPosition(this OriginalOptionsPanelOverlay.Button button) => (int)button switch
    {
        0 => new CGPoint(88, 6),
        1 => new CGPoint(29, 6),
        2 => new CGPoint(159, 6),
        3 => new CGPoint(29, 58),
        4 => new CGPoint(89, 58),
        5 => new CGPoint(151, 58),
        6 => new CGPoint(555, 7),
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
    };

    public static string FallbackLabel(this OriginalOptionsPanelOverlay.Button button) => (int)button switch
    {
        0 => "Neighborhood",
        1 => "Save",
        2 => "Quit",
        3 => "Graphics Options",
        4 => "Sound Options",
        5 => "Play Options",
        6 => "Reset Tutorial",
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, null)
    };

    /// <summary>STR# 145 index of the label; the "About" title and text follow at +1 and +2.</summary>
    public static int StringIndex(this OriginalOptionsPanelOverlay.DetailSetting setting) =>
        setting == OriginalOptionsPanelOverlay.DetailSetting.Terrain ? 28 : 31;

    public static string FallbackLabel(this OriginalOptionsPanelOverlay.DetailSetting setting) =>
        setting == OriginalOptionsPanelOverlay.DetailSetting.Terrain ? "Terrain Detail" : "Character Detail";

    public static string PopupAsset(this OriginalOptionsPanelOverlay.DetailSetting setting) => setting switch
    {
        OriginalOptionsPanelOverlay.DetailSetting.Terrain => "UIGraphics/CPanel/PopupOptTerrainDetail.bmp",
        OriginalOptionsPanelOverlay.DetailSetting.Character => "UIGraphics/CPanel/PopupOptCharDetail.bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null)
    };

    public static bool IsImplemented(this OriginalOptionsPanelOverlay.DetailSetting _) => true;

    public static OriginalOptionsPanelOverlay.Page Page(this OriginalOptionsPanelOverlay.PlayOption option) =>
        (int)option < 8 ? OriginalOptionsPanelOverlay.Page.Play : OriginalOptionsPanelOverlay.Page.Graphics;

    public static int Row(this OriginalOptionsPanelOverlay.PlayOption option) => (int)option % 4;

    public static bool IsColumnA(this OriginalOptionsPanelOverlay.PlayOption option) =>
        (int)option is >= 4 and < 8;

    /// <summary>STR# 145 index of the label; the "About" title and text follow at +1 and +2.</summary>
    public static int StringIndex(this OriginalOptionsPanelOverlay.PlayOption option) => (int)option switch
    {
        0 => 56,
        1 => 59,
        2 => 62,
        3 => 65,
        4 => 72,
        5 => 75,
        6 => 78,
        7 => 81,
        8 => 14,
        9 => 17,
        10 => 20,
        11 => 23,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
    };

    public static string FallbackLabel(this OriginalOptionsPanelOverlay.PlayOption option) => (int)option switch
    {
        0 => "Auto-Centering",
        1 => "Free Will",
        2 => "Edge Scrolling",
        3 => "Sim In Background",
        4 => "Quick Tips",
        5 => "Auto Snapshot",
        6 => "Live PIP",
        7 => "Export HTML",
        8 => "Anti-alias",
        9 => "Shadows",
        10 => "Lighting",
        11 => "Interface Effects",
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
    };

    public static string PopupAsset(this OriginalOptionsPanelOverlay.PlayOption option) => option switch
    {
        OriginalOptionsPanelOverlay.PlayOption.AutoCenter => "UIGraphics/CPanel/PopupOptAutoCenter.bmp",
        OriginalOptionsPanelOverlay.PlayOption.FreeWill => "UIGraphics/CPanel/PopupOptFreeWill.bmp",
        OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling => "UIGraphics/CPanel/PopupOptEdgeScroll.bmp",
        OriginalOptionsPanelOverlay.PlayOption.SimInBackground => "UIGraphics/CPanel/PopupOptSimInBack.bmp",
        OriginalOptionsPanelOverlay.PlayOption.QuickTips => "UIGraphics/CPanel/PopupOptQuickTips.bmp",
        OriginalOptionsPanelOverlay.PlayOption.AutoSnapshot => "UIGraphics/CPanel/PopupOptAutoSnap.bmp",
        OriginalOptionsPanelOverlay.PlayOption.LivePIP => "UIGraphics/CPanel/PopupOptLivePIP.bmp",
        OriginalOptionsPanelOverlay.PlayOption.ExportHTML => "UIGraphics/CPanel/PopupExportHTML.bmp",
        OriginalOptionsPanelOverlay.PlayOption.AntiAlias => "UIGraphics/CPanel/PopupOptAntiAlias.bmp",
        OriginalOptionsPanelOverlay.PlayOption.Shadows => "UIGraphics/CPanel/PopupOptShadows.bmp",
        OriginalOptionsPanelOverlay.PlayOption.Lighting => "UIGraphics/CPanel/PopupOptLighting.bmp",
        OriginalOptionsPanelOverlay.PlayOption.InterfaceEffects => "UIGraphics/CPanel/PopupOptTransUI.bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
    };

    public static bool IsImplemented(this OriginalOptionsPanelOverlay.PlayOption _) => true;

    public static bool Value(
        this OriginalOptionsPanelOverlay.PlayOption option,
        OriginalOptionsPanelOverlay.PlayOptionsState options) => option switch
    {
        OriginalOptionsPanelOverlay.PlayOption.AutoCenter => options.AutoCenter,
        OriginalOptionsPanelOverlay.PlayOption.FreeWill => options.FreeWill,
        OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling => options.EdgeScrolling,
        OriginalOptionsPanelOverlay.PlayOption.QuickTips => options.QuickTips,
        OriginalOptionsPanelOverlay.PlayOption.Lighting => options.Lighting,
        OriginalOptionsPanelOverlay.PlayOption.SimInBackground => options.SimInBackground,
        OriginalOptionsPanelOverlay.PlayOption.AntiAlias => options.AntiAlias,
        OriginalOptionsPanelOverlay.PlayOption.Shadows => options.Shadows,
        OriginalOptionsPanelOverlay.PlayOption.InterfaceEffects => options.InterfaceEffects,
        OriginalOptionsPanelOverlay.PlayOption.ExportHTML => options.ExportHTML,
        OriginalOptionsPanelOverlay.PlayOption.AutoSnapshot => options.AutoSnapshot,
        OriginalOptionsPanelOverlay.PlayOption.LivePIP => options.LivePIP,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
    };

    public static OriginalOptionsPanelOverlay.PlayOptionsState Toggled(
        this OriginalOptionsPanelOverlay.PlayOption option,
        OriginalOptionsPanelOverlay.PlayOptionsState options)
    {
        var copy = options;
        switch (option)
        {
            case OriginalOptionsPanelOverlay.PlayOption.Lighting:
                copy.Lighting = !copy.Lighting;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.AutoCenter:
                copy.AutoCenter = !copy.AutoCenter;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.FreeWill:
                copy.FreeWill = !copy.FreeWill;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling:
                copy.EdgeScrolling = !copy.EdgeScrolling;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.QuickTips:
                copy.QuickTips = !copy.QuickTips;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.SimInBackground:
                copy.SimInBackground = !copy.SimInBackground;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.ExportHTML:
                copy.ExportHTML = !copy.ExportHTML;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.AntiAlias:
                copy.AntiAlias = !copy.AntiAlias;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.Shadows:
                copy.Shadows = !copy.Shadows;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.InterfaceEffects:
                copy.InterfaceEffects = !copy.InterfaceEffects;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.AutoSnapshot:
                copy.AutoSnapshot = !copy.AutoSnapshot;
                break;
            case OriginalOptionsPanelOverlay.PlayOption.LivePIP:
                copy.LivePIP = !copy.LivePIP;
                break;
        }
        return copy;
    }
}
