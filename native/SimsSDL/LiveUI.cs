using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using SimsSDL.Core;
using SimsSDL.UI;

namespace SimsSDL;

public sealed class LiveUI
{
    public GameContent Content { get; }
    public SimsBitmapFont Font { get; }

    public SimsBitmapFont MediumFont { get; }

    public SimsBitmapFont SplashFont { get; }

    public OriginalGameplayOverlay Gameplay { get; }
    public OriginalBuyOverlay Buy { get; }
    public OriginalBuildOverlay Build { get; }
    public OriginalOptionsPanelOverlay OptionsPanel { get; }
    public OriginalUIStrings Strings { get; }
    public OriginalCameraOverlay Camera { get; private set; }
    public OriginalPhotoAlbumOverlay Album { get; private set; }
    public OriginalHelpOverlay HelpOverlay { get; private set; }
    public OriginalBudgetReportOverlay BudgetReport { get; private set; }
    public OriginalSoundOptionsOverlay SoundOptions { get; }

    public OriginalCatalogPopup CatalogPopup { get; }
    public OriginalCheatConsoleOverlay CheatConsole { get; }
    public IReadOnlyList<SceneAnimation.OverheadFeedbackDescriptor>? ActiveOverheadFeedback { get; set; }

    private readonly Dictionary<uint, UITexture> _overheadBgTextures = new();
    private readonly Dictionary<(string Text, uint FgRgb), OriginalGameplayOverlay.Image> _overheadTextImages = new();

    private readonly UITexture _panel;
    private readonly UITexture _backPatch;

    private readonly UITexture? _setup;
    private (UITexture Texture, CGSize Size)? _splashCopyright;
    private bool? _appliedSharpText;
    private readonly UITexture _black = new(1, 1, [0, 0, 0, 255]);

    public OriginalTravelLoadScreen? TravelLoadScreen { get; set; }
    private (OriginalTravelLoadScreen Screen, bool HighRes, UITexture Texture)? _cachedTravelLoadTexture;

    private bool _loading;

    public bool Loading
    {
        get => _loading;
        set
        {
            _loading = value;
            if (!value)
            {
                TravelLoadScreen = null;
                LoadingText = null;
                _loadingLine = null;
                _previousLoadingLine = null;
                _loadingLineTransitionStart = null;
            }
        }
    }

    private string? _loadingText;

    public string? LoadingText
    {
        get => _loadingText;
        set
        {
            if (string.Equals(_loadingText, value, StringComparison.Ordinal))
                return;

            _loadingText = value;
            if (string.IsNullOrEmpty(value))
            {
                _loadingLine = null;
                _previousLoadingLine = null;
                _loadingLineTransitionStart = null;
                return;
            }

            if (_loadingLine is { } current && string.Equals(current.Text, value, StringComparison.Ordinal))
                return;

            try
            {
                var raster = SplashFont.RasterizeHiDpi(value, 255, 255, 255);
                if (raster.LogicalWidth <= 0 || raster.LogicalHeight <= 0)
                    return;

                byte[] pixels = SpritePixels.PremultipliedSRGB(raster.Rgba, removeMagenta: false);
                _previousLoadingLine = _loadingLine;
                _loadingLine = (
                    value,
                    new UITexture(raster.PixelWidth, raster.PixelHeight, pixels),
                    new CGSize(raster.LogicalWidth, raster.LogicalHeight));
                _loadingLineTransitionStart = CurrentUptime();
            }
            catch (ContentError)
            {
            }
        }
    }

    private (string Text, UITexture Texture, CGSize Size)? _loadingLine;
    private (string Text, UITexture Texture, CGSize Size)? _previousLoadingLine;
    private double? _loadingLineTransitionStart;

    public bool HelpVisible { get; set; }
    public bool BudgetReportVisible { get; set; }

    public OriginalGameplayOverlay.Image? PictureInPicture { get; set; }

    public static CGSize PictureInPictureSize(int sizeIndex) =>
        OriginalPictureInPictureLayout.Size(sizeIndex);

    public CGSize PictureInPictureLogicalSize { get; set; } = new(100, 100);

    private string? _pictureInPictureCaption;
    public string? PictureInPictureCaption
    {
        get => _pictureInPictureCaption;
        set
        {
            if (!string.Equals(_pictureInPictureCaption, value, StringComparison.Ordinal))
                _cachedPipCaption = null;
            _pictureInPictureCaption = value;
        }
    }

    public bool PictureInPictureCloseHovered { get; set; }
    public bool PictureInPictureClosePressed { get; set; }
    public bool PanelMinimized { get; set; }

    private readonly UITexture _pictureInPictureBorder = new(1, 1, [0, 0, 0, 255]);
    private readonly UITexture? _closeBoxTexture;
    private readonly UITexture _vectorCloseBoxTexture;
    private (string Text, int Width, List<OriginalGameplayOverlay.Image> Lines)? _cachedPipCaption;

    public sealed class DisposePanelState
    {
        public int ObjectID { get; set; }
        public string CatalogName { get; set; } = string.Empty;
        public int ResaleValue { get; set; }
        public bool Sellable { get; set; }
        public int BuildModeType { get; set; }
        public OriginalGameplayOverlay.Image? Preview { get; set; }

        public DisposePanelState() { }

        public DisposePanelState(
            int objectID,
            string catalogName,
            int resaleValue,
            bool sellable,
            int buildModeType,
            OriginalGameplayOverlay.Image? preview = null)
        {
            ObjectID = objectID;
            CatalogName = catalogName;
            ResaleValue = resaleValue;
            Sellable = sellable;
            BuildModeType = buildModeType;
            Preview = preview;
        }
    }

    private DisposePanelState? _disposePanel;
    public DisposePanelState? DisposePanel
    {
        get => _disposePanel;
        set
        {
            _disposePanel = value;
            _cachedDisposePrompt = null;
        }
    }

    private readonly string[] _disposeStrings;
    private (string Key, List<OriginalGameplayOverlay.Image> Lines)? _cachedDisposePrompt;

    public bool ShowsDisposePanel =>
        (BuyMode || BuildMode)
        && !OptionsMode
        && !Camera.Visible
        && Buy.Selection.Target is { IsPlaced: true };

    public CGRect DisposePanelRect(CGSize canvas) =>
        new(220, canvas.Height - 100, Math.Max(0, canvas.Width - 220), 100);

    /// <summary>Positions the picture-in-picture window at the bottom-right above the control panel, offset
    /// vertically by 5 pixels when minimized, 105 pixels when normal, or 155 pixels when expanded.</summary>
    public CGRect? PictureInPictureRect(CGSize canvas, double scale)
    {
        _ = scale;
        if (PictureInPicture is null)
            return null;

        bool expandedPanel = TutorialIcon is not null || Content.Language.Code is 15 or 17 or 18 or 19 or 20;
        return OriginalPictureInPictureLayout.WindowRect(
            canvas,
            PictureInPictureLogicalSize,
            PanelMinimized,
            expandedPanel);
    }

    public CGRect? PictureInPictureCloseRect(CGSize canvas, double scale)
    {
        if (PictureInPictureRect(canvas, scale) is not { } frame)
            return null;

        double w = (_closeBoxTexture?.Width ?? 48) / 4.0;
        double h = _closeBoxTexture?.Height ?? 11;
        return OriginalPictureInPictureLayout.CloseBoxRect(frame, new CGSize(w, h));
    }

    public OriginalBudgetReportOverlay.ReportValues? BudgetReportValues { get; private set; }
    public OriginalGameplayOverlay.Image? TutorialIcon { get; private set; }
    public int TutorialIconColumns { get; private set; } = 1;
    public int TutorialIconRows { get; private set; } = 1;
    private string? _tutorialIconKey;
    private const double TutorialPanelHeight = 150;

    private readonly UITexture? _tutorialHighlightTexture;
    public (CGRect Button, double ShownAt)? TutorialHighlight { get; private set; }

    private CGSize? _lastLogicalCanvas;
    private readonly List<UITexture> _universalTextures = [];
    private readonly List<UITexture?> _vectorUniversalTextures = [];
    private readonly List<UITexture> _speedTextures = [];
    private readonly List<UITexture> _vectorSpeedTextures = [];

    public CGSize? HelpButtonSize
    {
        get
        {
            for (int i = 0; i < OriginalUniversalControl.All.Count; i++)
            {
                if (OriginalUniversalControl.All[i].Kind == OriginalUniversalControl.Action.Help && i < _universalTextures.Count)
                {
                    var texture = _universalTextures[i];
                    return new CGSize(texture.Width / 4.0, texture.Height);
                }
            }
            return null;
        }
    }

    private bool _buyMode;
    public bool BuyMode
    {
        get => _buyMode;
        set
        {
            bool changed = _buyMode != value;
            _buyMode = value;
            if (changed)
            {
                CatalogPopup.Hover(null);
                try { CatalogPopup.ShowPlacementError(null); } catch { }
            }
        }
    }

    private bool _buildMode;
    public bool BuildMode
    {
        get => _buildMode;
        set
        {
            bool changed = _buildMode != value;
            _buildMode = value;
            if (changed)
            {
                CatalogPopup.Hover(null);
                try { CatalogPopup.ShowPlacementError(null); } catch { }
            }
        }
    }

    public bool OptionsMode { get; set; }
    public bool AwayBuyBuildLocked { get; set; }
    public OriginalUniversalControl.Action? HoveredUniversalControl { get; set; }

    public bool IsControlEnabled(OriginalUniversalControl.Action action) => action switch
    {
        OriginalUniversalControl.Action.Buy or OriginalUniversalControl.Action.Build => !AwayBuyBuildLocked,
        _ => true
    };

    public int? HoveredSpeedControl { get; set; }
    private double? _livePauseStartedAt;

    public bool IsModalDialogBlocking =>
        GameDialog?.Dialog.Blocking == true && GameDialog?.Submitting == false;

    public bool WallsUp { get; set; }
    public string WallMode { get; set; } = "down";
    public bool RoofsVisible { get; set; }
    public int ViewLevel { get; set; } = 1;

    private OriginalPieOverlay? _pieMenu;
    public OriginalPieOverlay? PieMenu
    {
        get => _pieMenu;
        set
        {
            if (!ReferenceEquals(_pieMenu, value))
                PieHeadImage = null;
            _pieMenu = value;
        }
    }

    public OriginalGameplayOverlay.Image? PieHeadImage { get; private set; }

    public OriginalDialogOverlay? GameDialog { get; set; }

    private float _roofPitch = 0.66f;
    public float RoofPitch
    {
        get => _roofPitch;
        set
        {
            _roofPitch = value;
            Build.SelectedRoofPitch = value;
        }
    }

    public ushort RoofStyle { get; set; } = 16;

    public OriginalNeighborhoodOverlay? Neighborhood { get; set; }
    public OriginalFamilyPickerOverlay? FamilyPicker { get; set; }
    public OriginalModsOverlay? ModsOverlay { get; set; }

    public OriginalCreditsOverlay? Credits { get; set; }

    public OriginalHouseholdCreationOverlay? HouseholdCreation { get; set; }

    public OriginalOutfitPickerOverlay? OutfitPicker { get; set; }
    public GenericSimsCallChooserOverlay? GenericSimsCallPicker { get; set; }

    public OriginalPluginPanelOverlay? PluginPanel { get; set; }

    public (int Index, IReadOnlyDictionary<int, Vector4> Rects)? PreviewPlacement { get; private set; }

    public UIDevice Device { get; }

    public bool ShowsLot => !Loading && Neighborhood is null && HouseholdCreation is null;

    public static float AutomaticUIScale(int width, int height, float pixelDensity) =>
        OriginalUIScalingLayout.AutomaticUIScale(width, height, pixelDensity);

    public static double FullscreenOverlayScale(CGSize canvas, float uiScale = 1.0f) =>
        OriginalUIScalingLayout.FullscreenOverlayScale(canvas, uiScale);

    public bool ObjectEditing =>
        BuyMode || (BuildMode && (
            Build.ObjectTool is not null
            || Build.SelectedObjectGUID is not null
            || Buy.Selection.Target is not null
            || (Build.TerrainMode is null && Build.Selected is null
                && (Build.Kind == BuildCatalogPage.CatalogKind.WallStyle || Build.Kind == BuildCatalogPage.CatalogKind.Water))));

    public int? SelectedCatalogPrice
    {
        get
        {
            if (BuildMode && (Build.SelectedObjectGUID ?? Buy.Selected) is uint guid)
            {
                if (Build.Page?.Items is { } pageItems)
                {
                    foreach (var item in pageItems)
                    {
                        if (item.Guid == guid)
                            return item.Price;
                    }
                }
                if (Build.ObjectPage?.Items is { } objItems)
                {
                    foreach (var item in objItems)
                    {
                        if (item.Guid == guid)
                            return item.Price;
                    }
                }
                return Buy.SelectedItem?.Price;
            }
            return Buy.SelectedItem?.Price;
        }
    }

    public bool ChooseCatalogObject(uint guid)
    {
        if (BuildMode && Build.ObjectTool is null && (Build.Kind == BuildCatalogPage.CatalogKind.Water || Build.Kind == BuildCatalogPage.CatalogKind.WallStyle))
        {
            Build.ChooseObject(guid, Gameplay.State?.Budget);
            if (Build.SelectedObjectGUID != guid)
                return false;
            Buy.Selection.ChooseCatalog(guid);
            return true;
        }

        if (BuildMode && Build.ObjectTool is not null)
        {
            Build.ChooseObject(guid, Gameplay.State?.Budget);
            if (Build.SelectedObjectGUID != guid)
                return false;
            Buy.Selection.ChooseCatalog(guid);
            return true;
        }

        if (Buy.Choose(guid, Gameplay.State?.Budget))
        {
            if (BuildMode)
                Build.CancelSelection();
            return true;
        }

        return false;
    }

    public LiveUI(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
        Device = new UIDevice();

        UITexture LoadTexture(string path, bool transparent = false)
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return new UITexture(
                bitmap.Width,
                bitmap.Height,
                SpritePixels.PremultipliedSRGB(bitmap.Rgba, removeMagenta: transparent));
        }

        UITexture? TryLoadTexture(string path, bool transparent = false)
        {
            try
            {
                return LoadTexture(path, transparent);
            }
            catch
            {
                return null;
            }
        }

        byte[]? TryRead(string path)
        {
            try
            {
                return content.Read(path);
            }
            catch
            {
                return null;
            }
        }

        _panel = LoadTexture("UIGraphics/cpanel/Backgrounds/PanelBack.bmp");
        _backPatch = LoadTexture("UIGraphics/cpanel/Backgrounds/BackPatch.bmp");
        string localizedSetup = SetupBitmapPath(content.Language.Code);
        _setup = TryLoadTexture(localizedSetup) ?? TryLoadTexture("UIGraphics/Other/setup.bmp");
        Font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        MediumFont = TryRead("UIGraphics/Fonts/variablesans_12.ffn") is { } medData
            ? TryFont(medData) ?? Font
            : Font;
        SplashFont = TryRead("UIGraphics/Fonts/variablesans_18.ffn") is { } splashData
            ? TryFont(splashData) ?? Font
            : Font;
        _closeBoxTexture = TryLoadTexture("UIGraphics/Shared/Sys/CloseBox.bmp", transparent: true);
        _vectorCloseBoxTexture = SimsVectorUiKit.ToTexture(SimsVectorUiKit.RenderWindowSmallBoxStrip(isCloseX: true, scale: 4));

        byte[]? uiTextData = TryRead("GameData/UIText.iff") ?? TryRead("UIText.iff");
        _disposeStrings = uiTextData is not null
            ? ReadIffStringChunk(uiTextData, content.Language.Code, 136) ?? []
            : [];

        var strings = new OriginalUIStrings(content);
        Strings = strings;

        RebuildSplashCopyright();

        Gameplay = new OriginalGameplayOverlay(content, Font, Device, strings);
        Buy = new OriginalBuyOverlay(content, Font, Device, strings);
        Build = new OriginalBuildOverlay(content, Device, Font, strings);
        Build.SelectedRoofPitch = _roofPitch;
        OptionsPanel = new OriginalOptionsPanelOverlay(content, Device, strings, Font);
        SoundOptions = new OriginalSoundOptionsOverlay(content, Font, Device, strings);
        Camera = new OriginalCameraOverlay(content, Device, strings);
        Album = new OriginalPhotoAlbumOverlay(content, Font, Device, strings);
        HelpOverlay = new OriginalHelpOverlay(content, MediumFont, Device, strings);
        BudgetReport = new OriginalBudgetReportOverlay(content, Font, Device, strings);
        CatalogPopup = new OriginalCatalogPopup(content, Device);
        CheatConsole = new OriginalCheatConsoleOverlay(Font, Device);

        foreach (var control in OriginalUniversalControl.All)
            _universalTextures.Add(LoadTexture("UIGraphics/cpanel/Buttons/" + control.Asset, transparent: true));
        foreach (var control in OriginalUniversalControl.All)
        {
            var sz = control.Kind == OriginalUniversalControl.Action.Help
                ? (HelpButtonSize ?? new CGSize(10, 15))
                : control.Size;
            var vecRaster = SimsVectorUiKit.TryRenderUniversalGeometricControlStrip(
                control.Kind,
                Math.Max(1, (int)Math.Round(sz.Width)),
                Math.Max(1, (int)Math.Round(sz.Height)),
                scale: 4);
            _vectorUniversalTextures.Add(vecRaster is { } vr ? SimsVectorUiKit.ToTexture(vr) : null);
        }
        foreach (var control in OriginalSpeedControl.All)
        {
            _speedTextures.Add(LoadTexture(control.Asset, transparent: true));
            _vectorSpeedTextures.Add(SimsVectorUiKit.ToTexture(SimsVectorUiKit.RenderSpeedControlStrip(control.Speed, scale: 4)));
        }

        _tutorialHighlightTexture = TryLoadTexture("UIGraphics/CPanel/TutHigh.bmp", transparent: true);
        _appliedSharpText = SimsBitmapFont.SharpTextEnabled;
    }

    private void RebuildSplashCopyright()
    {
        (UITexture Texture, CGSize Size)? copyrightEntry = null;
        try
        {
            var raster = SplashFont.RasterizeHiDpi(Strings.SplashCopyright(), 255, 255, 255);
            if (raster.LogicalWidth > 0 && raster.LogicalHeight > 0)
            {
                byte[] pixels = SpritePixels.PremultipliedSRGB(raster.Rgba, removeMagenta: false);
                copyrightEntry = (
                    new UITexture(raster.PixelWidth, raster.PixelHeight, pixels),
                    new CGSize(raster.LogicalWidth, raster.LogicalHeight));
            }
        }
        catch
        {
            copyrightEntry = null;
        }
        _splashCopyright = copyrightEntry;
    }

    public void ApplySharpText(bool enabled)
    {
        if (_appliedSharpText == enabled && SimsBitmapFont.SharpTextEnabled == enabled)
        {
            return;
        }

        SimsBitmapFont.SharpTextEnabled = enabled;
        _appliedSharpText = enabled;
        RebuildSplashCopyright();
        _cachedPipCaption = null;
        _cachedDisposePrompt = null;

        if (!string.IsNullOrEmpty(_loadingText))
        {
            var loading = _loadingText;
            _loadingText = null;
            _loadingLine = null;
            _previousLoadingLine = null;
            LoadingText = loading;
        }

        OptionsPanel.RefreshText();
        SoundOptions.RefreshText();
        Gameplay.RefreshText();
        Buy.ClearTextCaches();
        Build.ClearTextCaches();
        Neighborhood?.ClearTextCaches();
        ModsOverlay?.ClearTextCaches();
        CatalogPopup.ClearTextCaches();
        CheatConsole.ClearTextCaches();
        _overheadTextImages.Clear();

        bool cameraVisible = Camera.Visible;
        int cameraSize = Camera.Size;
        CGSize cameraCustom = Camera.CustomDimensions;
        int cameraQuality = Camera.Quality;
        Camera = new OriginalCameraOverlay(Content, Device, Strings)
        {
            Visible = cameraVisible,
            Size = cameraSize,
            CustomDimensions = cameraCustom,
            Quality = cameraQuality
        };

        if (!Album.Visible)
        {
            Album = new OriginalPhotoAlbumOverlay(Content, Font, Device, Strings);
        }

        int helpSelected = HelpOverlay.Selected;
        HelpOverlay = new OriginalHelpOverlay(Content, MediumFont, Device, Strings);
        if (helpSelected > 0)
        {
            HelpOverlay.MoveSelection(helpSelected);
        }

        BudgetReport = new OriginalBudgetReportOverlay(Content, Font, Device, Strings);
    }

    private static SimsBitmapFont? TryFont(byte[] data)
    {
        try
        {
            return new SimsBitmapFont(data);
        }
        catch
        {
            return null;
        }
    }

    public void TutorialFlash(string target, int value, bool on)
    {
        if (_lastLogicalCanvas is not { } canvas)
            return;

        bool gameplayPanel = !BuyMode && !BuildMode && !OptionsMode && !PanelMinimized;
        CGRect? button = null;

        switch (target)
        {
            case "image":
                OriginalUniversalControl? universalMatch = null;
                foreach (var c in OriginalUniversalControl.All)
                {
                    if (c.ImageId == value)
                    {
                        universalMatch = c;
                        break;
                    }
                }
                if (universalMatch is not null)
                {
                    button = universalMatch.Rect(canvas, HelpButtonSize);
                }
                else
                {
                    OriginalSpeedControl? speedMatch = null;
                    foreach (var c in OriginalSpeedControl.All)
                    {
                        if (c.ImageId == value)
                        {
                            speedMatch = c;
                            break;
                        }
                    }
                    if (speedMatch is not null)
                    {
                        button = new CGRect(
                            speedMatch.Position.X,
                            canvas.Height - OriginalSpeedControl.PanelHeight + speedMatch.Position.Y,
                            speedMatch.Size.Width,
                            speedMatch.Size.Height);
                    }
                    else if (gameplayPanel)
                    {
                        button = Gameplay.TutorialButtonForImage(value, canvas);
                    }
                }
                break;

            case "person":
                if (gameplayPanel)
                    button = Gameplay.TutorialButtonForSim(value, canvas);
                break;

            case "relationship":
                if (gameplayPanel)
                    button = Gameplay.TutorialButtonForRelationship(value, canvas);
                break;
        }

        if (button is not { } targetRect)
            return;

        if (on)
            TutorialHighlight = (targetRect, TutorialHighlight?.ShownAt ?? CurrentUptime());
        else
            TutorialHighlight = null;
    }

    /// <summary>Calculates the tutorial highlight animation frame and placement, cycling frames 0, 1, 2, 1,
    /// 0 at 6 Hz and centering the indicator 3 pixels above the target button.</summary>
    private (CGRect Rect, int Frame)? TutorialHighlightDraw(UITexture texture)
    {
        if (TutorialHighlight is not { } highlight)
            return null;
        if (OriginalTutorialHighlight.Rect(highlight.Button, texture.Width, texture.Height) is not { } rect)
            return null;
        return (rect, OriginalTutorialHighlight.Frame(CurrentUptime() - highlight.ShownAt));
    }

    private static readonly HashSet<string> PanelStateKeys = new(StringComparer.Ordinal)
    {
        "selectedSim",
        "hours",
        "minutes",
        "budget",
        "familyFriends",
        "sims",
        "canUndo",
        "canRedo",
        "housePanel"
    };

    public void UpdatePieHeadPreview(IReadOnlyDictionary<string, JsonElement> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!TryGetInt32(result, "width", out int width)
            || !TryGetInt32(result, "height", out int height)
            || width is < 1 or > 128
            || height is < 1 or > 128
            || !result.TryGetValue("pixels", out var pixelsEl)
            || pixelsEl.ValueKind != JsonValueKind.String
            || pixelsEl.GetString() is not { } encoded
            || Encoding.UTF8.GetByteCount(encoded) > 96_000)
        {
            throw new ContentError("Invalid pie head preview");
        }

        byte[] pixels;
        try
        {
            pixels = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new ContentError("Invalid pie head preview");
        }

        if (pixels.Length != width * height * 4)
            throw new ContentError("Invalid pie head preview");

        PieHeadImage = CreateOverlayImage(width, height, pixels);
    }

    private readonly ArrayBufferWriter<byte> _panelStateBuffer = new(4096);

    public void Update(IReadOnlyDictionary<string, JsonElement> state, string root)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(root);

        WallsUp = state.TryGetValue("wallsUp", out var wu) && wu.ValueKind == JsonValueKind.True;
        WallMode = state.TryGetValue("wallMode", out var wm) && wm.ValueKind == JsonValueKind.String && wm.GetString() is { } modeStr
            ? modeStr
            : (WallsUp ? "up" : "down");

        _panelStateBuffer.ResetWrittenCount();
        using (var writer = new Utf8JsonWriter(_panelStateBuffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in state)
            {
                if (PanelStateKeys.Contains(key))
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        var panelState = JsonSerializer.Deserialize<GameplayPanelState>(_panelStateBuffer.WrittenSpan, PackContentPaths.JsonReadOptions)
            ?? throw new ContentError("Invalid gameplay panel state");
        Gameplay.Update(panelState, root);

        if (state.TryGetValue("budgetReport", out var reportEl)
            && reportEl.ValueKind == JsonValueKind.Object
            && TryReadIntArray(reportEl, "today", 8, out int[] today)
            && TryReadIntArray(reportEl, "threeDay", 8, out int[] threeDay))
        {
            int daysSinceMoveIn = reportEl.TryGetProperty("daysSinceMoveIn", out var daysEl) && daysEl.TryGetInt32(out int d) ? d : 0;
            int netWorth = reportEl.TryGetProperty("netWorth", out var nwEl) && nwEl.TryGetInt32(out int nw) ? nw : (panelState.Budget ?? 0);
            BudgetReportValues = new OriginalBudgetReportOverlay.ReportValues(today, threeDay, daysSinceMoveIn, netWorth);
        }
        else
        {
            BudgetReportValues = null;
            BudgetReportVisible = false;
        }

        Buy.UpdateStatus(panelState);
        ClockMinutes = panelState.Hours * 60 + panelState.Minutes;

        if (state.TryGetValue("tutorialIcon", out var iconEl) && iconEl.ValueKind == JsonValueKind.Object)
        {
            if (iconEl.TryGetProperty("fallback", out var fallbackEl) && fallbackEl.ValueKind == JsonValueKind.True)
            {
                if (string.Equals(_tutorialIconKey, "fallback", StringComparison.Ordinal) && TutorialIcon is not null)
                    return;

                byte[] pixels = new byte[45 * 45 * 4];
                Array.Fill(pixels, (byte)255);
                for (int y = 0; y < 45; y++)
                {
                    for (int x = 0; x < 45; x++)
                    {
                        if (x == 0 || y == 0 || x == 44 || y == 44)
                        {
                            int offset = (y * 45 + x) * 4;
                            pixels[offset] = 0;
                            pixels[offset + 1] = 0;
                            pixels[offset + 2] = 0;
                            pixels[offset + 3] = 255;
                        }
                    }
                }
                TutorialIcon = CreateOverlayImage(45, 45, pixels);
                TutorialIconColumns = 1;
                TutorialIconRows = 1;
                _tutorialIconKey = "fallback";
                return;
            }

            if (iconEl.TryGetProperty("file", out var fileEl)
                && fileEl.ValueKind == JsonValueKind.String
                && fileEl.GetString() is { } file
                && IsSingleFilename(file))
            {
                int columns = Math.Max(1, iconEl.TryGetProperty("columns", out var colEl) && colEl.TryGetInt32(out int c) ? c : 1);
                int rows = Math.Max(1, Math.Min(6, iconEl.TryGetProperty("rows", out var rowEl) && rowEl.TryGetInt32(out int r) ? r : 1));
                string? safeFile = ResolveRootFile(root, file);
                if (safeFile is not null)
                {
                    string key = $"{safeFile}:{columns}x{rows}";
                    if (string.Equals(_tutorialIconKey, key, StringComparison.Ordinal) && TutorialIcon is not null)
                    {
                        return;
                    }

                    if (TryLoadBitmapFromFile(safeFile, maxWidth: 512, maxHeight: int.MaxValue) is { } bitmap
                        && bitmap.Height % rows == 0)
                    {
                        TutorialIcon = CreateOverlayImage(bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
                        TutorialIconColumns = columns;
                        TutorialIconRows = rows;
                        _tutorialIconKey = key;
                        return;
                    }
                }
            }
        }

        TutorialIcon = null;
        TutorialIconColumns = 1;
        TutorialIconRows = 1;
        _tutorialIconKey = null;
    }

    public bool IsSelected(OriginalUniversalControl.Action action) => action switch
    {
        OriginalUniversalControl.Action.Live => !BuyMode && !BuildMode && !OptionsMode && !Camera.Visible,
        OriginalUniversalControl.Action.Buy => BuyMode,
        OriginalUniversalControl.Action.Build => BuildMode,
        OriginalUniversalControl.Action.WallsUp => WallsUp,
        OriginalUniversalControl.Action.WallsCutaway => string.Equals(WallMode, "cutaway", StringComparison.Ordinal),
        OriginalUniversalControl.Action.WallsDown => string.Equals(WallMode, "down", StringComparison.Ordinal),
        OriginalUniversalControl.Action.Story1 => !RoofsVisible && ViewLevel == 1,
        OriginalUniversalControl.Action.Story2 => !RoofsVisible && ViewLevel == 2,
        OriginalUniversalControl.Action.Roofs => RoofsVisible,
        OriginalUniversalControl.Action.Options => OptionsMode,
        _ => false
    };

    public List<int> SetButtonImages(int speed)
    {
        var images = new List<int>();
        foreach (var control in OriginalUniversalControl.All)
        {
            if (IsSelected(control.Kind))
                images.Add(control.ImageId);
        }

        int effectiveSpeed = IsModalDialogBlocking ? 0 : speed;
        if (!BuyMode && !BuildMode && !OptionsMode && !Camera.Visible
            && OriginalSpeedControl.SelectedImageId(effectiveSpeed) is int speedImage)
        {
            images.Add(speedImage);
        }

        if (!BuyMode && !BuildMode && !OptionsMode && !Camera.Visible && !PanelMinimized)
            images.Add(Gameplay.CurrentSubpanel.ImageId());

        return images;
    }

    public CGRect? TutorialIconRect(CGSize canvas)
    {
        if (TutorialIcon is not { } image)
            return null;

        double width = image.Size.Width / TutorialIconColumns;
        return new CGRect(
            canvas.Width - width - 1,
            canvas.Height - TutorialPanelHeight + 1,
            width,
            image.Size.Height / TutorialIconRows);
    }

    public int? ClockMinutes { get; private set; }

    public static string SetupBitmapPath(int languageCode) => languageCode switch
    {
        2 => "UIGraphics/Other/setupuk.bmp",
        3 => "UIGraphics/Other/Setupfr.BMP",
        4 => "UIGraphics/Other/Setupde.bmp",
        5 => "UIGraphics/Other/SetupIt.bmp",
        6 => "UIGraphics/Other/Setupsp.bmp",
        7 => "UIGraphics/Other/SetupDu.bmp",
        8 => "UIGraphics/Other/SetupDN.bmp",
        9 => "UIGraphics/Other/SetupSw.bmp",
        10 => "UIGraphics/Other/SetupNO.bmp",
        11 => "UIGraphics/Other/SetupFI.bmp",
        14 => "UIGraphics/Other/SetupPg.bmp",
        15 => "UIGraphics/Other/Setupjp.bmp",
        16 => "UIGraphics/Other/SetupPo.bmp",
        17 => "UIGraphics/Other/SetupSC.bmp",
        18 => "UIGraphics/Other/SetupTC.bmp",
        19 => "UIGraphics/Other/SetupTh.bmp",
        20 => "UIGraphics/Other/SetupKo.bmp",
        _ => "UIGraphics/Other/setup.bmp"
    };

    private UITexture? TravelLoadTexture(OriginalTravelLoadScreen screen, bool highRes)
    {
        if (_cachedTravelLoadTexture is { } cached && cached.Screen == screen && cached.HighRes == highRes)
            return cached.Texture;

        foreach (string path in screen.ResourcePaths(highRes))
        {
            try
            {
                byte[] data = Content.Read(path);
                var bitmap = new SimsBitmap(data);
                byte[] pixels = SpritePixels.PremultipliedSRGB(bitmap.Rgba, removeMagenta: false);
                var texture = new UITexture(bitmap.Width, bitmap.Height, pixels);
                _cachedTravelLoadTexture = (screen, highRes, texture);
                return texture;
            }
            catch
            {
            }
        }
        return null;
    }

    public List<UIDraw> Draws(
        Vector2 canvas,
        float scale,
        int speed,
        SceneViewport? viewport = null,
        LotZoom zoom = LotZoom.Medium)
    {
        var draws = new List<UIDraw>();
        PreviewPlacement = null;

        Vector4 ScaledRect(CGRect r) =>
            new((float)r.MinX * scale, (float)r.MinY * scale, (float)r.Width * scale, (float)r.Height * scale);

        Vector4 PixelRect(CGRect r) =>
            new((float)r.MinX, (float)r.MinY, (float)r.Width, (float)r.Height);

        var size = new CGSize(canvas.X, canvas.Y);
        var logical = new CGSize(canvas.X / scale, canvas.Y / scale);

        if (!ShowsLot)
        {
            float screenScale = (float)FullscreenOverlayScale(size, scale);
            var screenLogical = new CGSize(canvas.X / screenScale, canvas.Y / screenScale);

            if (Loading)
            {
                draws.Add(new UIDraw(_black, new Vector4(0f, 0f, canvas.X, canvas.Y)));
                bool prefersHighResTravel = OriginalTravelLoadScreenExtensions.PrefersHighRes(logical.Width);
                if (TravelLoadScreen is { } screen
                    && (TravelLoadTexture(screen, prefersHighResTravel)
                        ?? TravelLoadTexture(screen, false)
                        ?? TravelLoadTexture(screen, true)) is { } travelTex)
                {
                    float fit = MathF.Max(0.001f, MathF.Min(canvas.X / travelTex.Width, canvas.Y / travelTex.Height));
                    float width = travelTex.Width * fit;
                    float height = travelTex.Height * fit;
                    draws.Add(new UIDraw(travelTex, new Vector4((canvas.X - width) / 2f, (canvas.Y - height) / 2f, width, height)));
                }
                else
                {
                    float boxWidth = _setup?.Width ?? 800;
                    float boxHeight = _setup?.Height ?? 600;
                    float fit = MathF.Max(0.001f, MathF.Min(canvas.X / boxWidth, canvas.Y / boxHeight));
                    float splashW = boxWidth * fit;
                    float splashH = boxHeight * fit;
                    float splashX = (canvas.X - splashW) / 2f;
                    float splashY = (canvas.Y - splashH) / 2f;

                    if (_setup is not null)
                        draws.Add(new UIDraw(_setup, new Vector4(splashX, splashY, splashW, splashH)));

                    float lineHeight = SplashFont.LineHeight;
                    if (_splashCopyright is { } copyright)
                    {
                        float cw = (float)copyright.Size.Width * fit;
                        float ch = (float)copyright.Size.Height * fit;
                        float cy = splashY + ((boxHeight < 701f ? boxHeight - 3f * lineHeight : boxHeight - (lineHeight * 5f) / 2f) * fit);
                        draws.Add(new UIDraw(copyright.Texture, new Vector4(splashX + (splashW - cw) / 2f, cy, cw, ch)));
                    }

                    if (_loadingLine is { } line)
                    {
                        float py = splashY + (boxHeight - (lineHeight * 3f) / 2f) * fit;
                        double now = CurrentUptime();
                        double elapsed = _loadingLineTransitionStart is { } startedAt ? Math.Max(0.0, now - startedAt) : 1.0;
                        float t = (float)Math.Min(1.0, elapsed);

                        void AppendClippedText(UITexture tex, CGSize texSize, float x)
                        {
                            float w = (float)texSize.Width;
                            float h = (float)texSize.Height;
                            float visX0 = MathF.Max(0f, x);
                            float visX1 = MathF.Min(boxWidth, x + w);
                            if (visX1 <= visX0 || w <= 0f)
                                return;

                            float u0 = (visX0 - x) / w;
                            float u1 = (visX1 - x) / w;
                            draws.Add(new UIDraw(
                                tex,
                                new Vector4(splashX + visX0 * fit, py, (visX1 - visX0) * fit, h * fit),
                                new Vector4(u0, 0f, u1 - u0, 1f)));
                        }

                        if (t < 1f && _previousLoadingLine is { } previous)
                        {
                            float prevW = (float)previous.Size.Width;
                            float prevStartX = (boxWidth - prevW) / 2f;
                            float prevX = prevStartX - (prevStartX + prevW) * t;
                            AppendClippedText(previous.Texture, previous.Size, prevX);
                        }
                        else if (t >= 1f)
                        {
                            _previousLoadingLine = null;
                        }

                        float curW = (float)line.Size.Width;
                        float targetX = (boxWidth - curW) / 2f;
                        float curX = t < 1f ? boxWidth - (boxWidth - targetX) * t : targetX;
                        AppendClippedText(line.Texture, line.Size, curX);
                    }
                }

                if (GameDialog is not null)
                    DrawDialog(GameDialog, canvas, screenScale, draws);
                AppendHelp(screenLogical, screenScale, draws);
                return draws;
            }

            if (HouseholdCreation is not null)
            {
                var hcItems = HouseholdCreation.DrawItems(size, scale);
                for (int i = 0; i < hcItems.Count; i++)
                {
                    var item = hcItems[i];
                    draws.Add(new UIDraw(item.Image.Texture, PixelRect(item.Rect), item.UV));
                    if (i == 0)
                    {
                        var rects = HouseholdCreation.PreviewRects(size, scale);
                        if (rects.Count > 0)
                        {
                            var mapped = new Dictionary<int, Vector4>(rects.Count);
                            foreach (var kv in rects)
                                mapped[kv.Key] = PixelRect(kv.Value);
                            PreviewPlacement = (draws.Count - 1, mapped);
                        }
                    }
                }

                if (GameDialog is not null)
                    DrawDialog(GameDialog, canvas, screenScale, draws);
                AppendHelp(screenLogical, screenScale, draws);
                return draws;
            }

            if (Neighborhood is not null)
            {
                if (Credits is not null)
                {
                    draws.Add(new UIDraw(_black, new Vector4(0f, 0f, canvas.X, canvas.Y)));
                    foreach (var item in Credits.DrawItems(size, scale))
                        draws.Add(new UIDraw(item.Image.Texture, PixelRect(item.Rect), item.UV, item.Opacity));
                }
                else if (FamilyPicker is not null)
                {
                    foreach (var item in FamilyPicker.DrawItems(size, scale))
                        draws.Add(new UIDraw(item.Image.Texture, PixelRect(item.Rect), item.UV));
                }
                else
                {
                    foreach (var item in Neighborhood.DrawItems(size, scale))
                        draws.Add(new UIDraw(item.Image.Texture, PixelRect(item.Rect), item.UV, item.Opacity));
                    if (ModsOverlay is not null)
                    {
                        foreach (var item in ModsOverlay.DrawItems(size, scale))
                            draws.Add(new UIDraw(item.Image.Texture, PixelRect(item.Rect), item.UV, item.Opacity));
                    }
                }

                AppendCheatConsole(screenScale, draws);
                if (GameDialog is not null)
                    DrawDialog(GameDialog, canvas, screenScale, draws);
                AppendHelp(screenLogical, screenScale, draws);
                return draws;
            }
        }

        AppendOverheadFeedback(viewport, zoom, scale, draws);

        float top = canvas.Y - 150f * scale;
        bool minimizedLive = PanelMinimized && !Camera.Visible && !BuildMode && !BuyMode && !OptionsMode;
        Gameplay.Prepare(logical);
        bool disposeActive = ShowsDisposePanel;

        if (!minimizedLive)
        {
            draws.Add(new UIDraw(_backPatch, new Vector4(168f * scale, top + 50f * scale, _backPatch.Width * scale, _backPatch.Height * scale)));

            var panelSlices = OriginalUIScalingLayout.GetPanelBackLayout(
                logical.Width,
                expandsToCanvas: true,
                bitmapWidth: _panel.Width);
            float panelOriginX = (float)OriginalUIScalingLayout.PanelBackLayout.PanelOriginX * scale;
            float panelTopY = top + 50f * scale;
            float panelHeight = 100f * scale;

            if (SimsBitmapFont.SharpTextEnabled)
            {
                double totalCols = panelSlices.LeftBodyWidth + panelSlices.StretchBodyWidth + panelSlices.RightCapWidth;
                int vecPanelW = Math.Max(64, (int)Math.Round(totalCols));
                var vecPanelTex = SimsVectorUiKit.ToTexture(SimsVectorUiKit.RenderLivePanelBack(vecPanelW, 100, scale: 3));
                draws.Add(new UIDraw(
                    vecPanelTex,
                    new Vector4(panelOriginX, panelTopY, (float)totalCols * scale, panelHeight)));
            }
            else
            {
                float invPanelW = 1f / Math.Max(1, _panel.Width);

                if (panelSlices.LeftBodyWidth > 0)
                {
                    float leftCols = (float)panelSlices.LeftBodyWidth;
                    draws.Add(new UIDraw(
                        _panel,
                        new Vector4(panelOriginX, panelTopY, leftCols * scale, panelHeight),
                        new Vector4(0f, 0f, leftCols * invPanelW, 1f)));
                }

                if (panelSlices.StretchBodyWidth > 0)
                {
                    float stretchX = ((float)OriginalUIScalingLayout.PanelBackLayout.PanelOriginX + (float)panelSlices.LeftBodyWidth) * scale;
                    float stretchW = (float)panelSlices.StretchBodyWidth * scale;
                    float sampleCol = panelSlices.StretchSampleColumn;
                    draws.Add(new UIDraw(
                        _panel,
                        new Vector4(stretchX, panelTopY, stretchW, panelHeight),
                        new Vector4(sampleCol * invPanelW, 0f, invPanelW, 1f)));
                }

                float capCols = (float)panelSlices.RightCapWidth;
                float capStartCol = panelSlices.RightCapStartColumn;
                draws.Add(new UIDraw(
                    _panel,
                    new Vector4((float)panelSlices.RightCapX * scale, panelTopY, capCols * scale, panelHeight),
                    new Vector4(capStartCol * invPanelW, 0f, capCols * invPanelW, 1f)));
            }
        }

        IReadOnlyList<OriginalGameplayOverlay.Part> rawModeParts = Camera.Visible
            ? Camera.PanelParts(logical)
            : BuildMode
                ? Build.Parts(logical, Gameplay.State?.Budget, Buy.Selected, Gameplay.State?.CanUndo, Gameplay.State?.CanRedo)
                : BuyMode
                    ? Buy.Parts(logical, Gameplay.State?.Budget)
                    : OptionsMode
                        ? OptionsPanel.Parts(logical)
                        : [];

        var parts = new List<OriginalGameplayOverlay.Part>();
        if (Camera.Visible || BuildMode || BuyMode || OptionsMode)
        {
            foreach (var part in rawModeParts)
            {
                if (!disposeActive || part.Rect.MinX < 220)
                    parts.Add(part);
            }
            parts.AddRange(Gameplay.UniversalStatusParts(logical));
        }
        else
        {
            parts.AddRange(Gameplay.Parts(logical, PanelMinimized));
        }

        bool quickTips = OptionsPanel.PlayOptions.QuickTips;
        CatalogPopup.QuickTips = quickTips;
        CatalogPopup.InterfaceEffects = OptionsPanel.PlayOptions.InterfaceEffects;

        bool hasPlacementError = CatalogPopup.PlacementErrorText is not null;
        IReadOnlyList<OriginalGameplayOverlay.Part> popupParts =
            (BuyMode || BuildMode) && (hasPlacementError || (!disposeActive && GameDialog is null && PieMenu is null))
                ? CatalogPopup.Parts(logical, Gameplay.State?.Budget)
                : [];
        IReadOnlyList<OriginalGameplayOverlay.Part> catalogCard = (hasPlacementError || disposeActive) ? [] : popupParts;
        IReadOnlyList<OriginalGameplayOverlay.Part> placementErrorDraws = hasPlacementError ? popupParts : [];

        foreach (var part in parts)
            draws.Add(new UIDraw(part.Image.Texture, ScaledRect(part.Rect), part.UV, part.Opacity));

        if (disposeActive && DisposePanel is { } dispose)
        {
            if (dispose.Preview is { } preview && preview.Size.Width > 0 && preview.Size.Height > 0)
            {
                double fit = Math.Min(1.0, Math.Min(160.0 / preview.Size.Width, 92.0 / preview.Size.Height));
                double w = Math.Floor(preview.Size.Width * fit);
                double h = Math.Floor(preview.Size.Height * fit);
                var previewRect = new CGRect(
                    220 + Math.Floor((168 - w) / 2.0),
                    logical.Height - 100 + Math.Floor((100 - h) / 2.0),
                    w,
                    h);
                draws.Add(new UIDraw(preview.Texture, ScaledRect(previewRect)));
            }

            var promptLines = DisposePromptImages(dispose);
            double lineH = MediumFont.LineHeight;
            double totalPromptH = promptLines.Count * lineH;
            double promptTop = logical.Height - 100 + Math.Floor((100.0 - totalPromptH) / 2.0);
            for (int lineIndex = 0; lineIndex < promptLines.Count; lineIndex++)
            {
                var line = promptLines[lineIndex];
                if (line.Size.Width <= 0)
                    continue;
                var lineRect = new CGRect(
                    398 - 3,
                    promptTop + lineIndex * lineH - 3,
                    line.Size.Width,
                    line.Size.Height);
                draws.Add(new UIDraw(line.Texture, ScaledRect(lineRect)));
            }
        }

        foreach (var item in SoundOptions.DrawItems(logical))
            draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV, item.Opacity));

        for (int index = 0; index < OriginalUniversalControl.All.Count; index++)
        {
            var control = OriginalUniversalControl.All[index];
            bool enabled = control.Enabled && IsControlEnabled(control.Kind);
            float buttonFrame = !enabled ? 3f
                : IsSelected(control.Kind) ? 1f
                : HoveredUniversalControl == control.Kind ? 2f : 0f;
            var univTex = SimsBitmapFont.SharpTextEnabled && index < _vectorUniversalTextures.Count && _vectorUniversalTextures[index] is { } vTex
                ? vTex
                : _universalTextures[index];
            draws.Add(new UIDraw(
                univTex,
                ScaledRect(control.Rect(logical, HelpButtonSize)),
                new Vector4(buttonFrame / 4f, 0f, 0.25f, 1f)));
        }

        if (TutorialIcon is { } icon && TutorialIconRect(logical) is { } iconBounds)
        {
            draws.Add(new UIDraw(
                icon.Texture,
                ScaledRect(iconBounds),
                new Vector4(0f, 0f, 1f / TutorialIconColumns, 1f / TutorialIconRows)));
        }

        bool nonLiveMode = BuyMode || BuildMode || OptionsMode || Camera.Visible;
        bool modalPaused = IsModalDialogBlocking;
        int effectiveSpeed = modalPaused ? 0 : speed;
        if (!nonLiveMode && !modalPaused && speed == 0)
        {
            _livePauseStartedAt ??= CurrentUptime();
        }
        else
        {
            _livePauseStartedAt = null;
        }

        for (int index = 0; index < OriginalSpeedControl.All.Count; index++)
        {
            var control = OriginalSpeedControl.All[index];
            float ctrlState = nonLiveMode ? 3f
                : control.Speed == effectiveSpeed ? 1f
                : (!modalPaused && HoveredSpeedControl == control.Speed) ? 2f : 0f;

            float row;
            if (!nonLiveMode && control.Speed == 0 && control.Rows > 1)
            {
                if (modalPaused && speed > 0)
                {
                    row = 1f;
                }
                else if (speed == 0 && OptionsPanel.PlayOptions.InterfaceEffects && _livePauseStartedAt is { } started)
                {
                    int halfSeconds = (int)(Math.Max(0.0, CurrentUptime() - started) * 2.0);
                    row = halfSeconds % control.Rows;
                }
                else
                {
                    row = 0f;
                }
            }
            else
            {
                row = 0f;
            }

            var bounds = new Vector4(
                (float)control.Position.X * scale,
                canvas.Y - (float)OriginalSpeedControl.PanelHeight * scale + (float)control.Position.Y * scale,
                (float)control.Size.Width * scale,
                (float)control.Size.Height * scale);
            var spdTex = SimsBitmapFont.SharpTextEnabled && index < _vectorSpeedTextures.Count
                ? _vectorSpeedTextures[index]
                : _speedTextures[index];
            draws.Add(new UIDraw(
                spdTex,
                bounds,
                new Vector4(ctrlState / control.Columns, row / control.Rows, 1f / control.Columns, 1f / control.Rows)));
        }

        foreach (var part in catalogCard)
            draws.Add(new UIDraw(part.Image.Texture, ScaledRect(part.Rect), part.UV, part.Opacity));

        _lastLogicalCanvas = logical;
        if (_tutorialHighlightTexture is not null && TutorialHighlightDraw(_tutorialHighlightTexture) is { } highlightDraw)
        {
            draws.Add(new UIDraw(
                _tutorialHighlightTexture,
                ScaledRect(highlightDraw.Rect),
                new Vector4(highlightDraw.Frame / 3f, 0f, 1f / 3f, 1f)));
        }

        Gameplay.QuickTips = quickTips;
        Gameplay.InterfaceEffects = OptionsPanel.PlayOptions.InterfaceEffects;

        if (PieMenu is not null)
        {
            PieMenu.Prepare(size, scale);
            if (PieMenu.CenterLabel is null && PieHeadImage is { } renderedHead && PieMenu.Layout is { } layoutFromHost)
            {
                var bounds = new CGRect(
                    layoutFromHost.Center.X - renderedHead.Size.Width / 2.0,
                    layoutFromHost.Center.Y - renderedHead.Size.Height / 2.0,
                    renderedHead.Size.Width,
                    renderedHead.Size.Height);
                draws.Add(new UIDraw(renderedHead.Texture, ScaledRect(bounds)));
            }
            else if (PieMenu.CenterLabel is null && Gameplay.PieHead is { } spriteHead && PieMenu.Layout is { } layoutFromSprite)
            {
                // Nine frames: center faces player, remaining 8 point clockwise from top.
                double width = spriteHead.Size.Width / 3.0;
                double height = spriteHead.Size.Height / 3.0;
                int headFrame = PieMenu.Highlighted is int hi
                    ? 1 + OriginalPieLayout.Direction(layoutFromSprite.Rectangles[hi], layoutFromSprite.Center)
                    : 0;
                var bounds = new CGRect(
                    layoutFromSprite.Center.X - width / 2.0,
                    layoutFromSprite.Center.Y - height / 2.0,
                    width,
                    height);
                draws.Add(new UIDraw(
                    spriteHead.Texture,
                    ScaledRect(bounds),
                    new Vector4((headFrame % 3) / 3f, (headFrame / 3) / 3f, 1f / 3f, 1f / 3f)));
            }

            if (PieMenu.CenterLabel is { } centerLabel && PieMenu.Layout is { } centerLayout)
            {
                var bounds = new CGRect(
                    centerLayout.Center.X - centerLabel.Size.Width / 2.0,
                    centerLayout.Center.Y - centerLabel.Size.Height / 2.0,
                    centerLabel.Size.Width,
                    centerLabel.Size.Height);
                draws.Add(new UIDraw(centerLabel.Background, ScaledRect(bounds)));
                draws.Add(new UIDraw(centerLabel.States[2], ScaledRect(bounds)));
            }

            for (int index = 0; index < PieMenu.Labels.Count; index++)
            {
                if (PieMenu.Layout is null || index >= PieMenu.Layout.Rectangles.Count)
                    continue;
                var bounds = PieMenu.Layout.Rectangles[index];
                var label = PieMenu.Labels[index];
                draws.Add(new UIDraw(label.Background, ScaledRect(bounds)));
                draws.Add(new UIDraw(label.States[PieMenu.Highlighted == index ? 1 : 0], ScaledRect(bounds)));
            }
        }

        if (OutfitPicker is not null)
        {
            foreach (var item in OutfitPicker.DrawItems(logical))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));

            PreviewPlacement = (
                draws.Count - 1,
                new Dictionary<int, Vector4> { [0] = ScaledRect(OutfitPicker.PreviewRect(logical)) });
        }

        if (GenericSimsCallPicker is not null)
        {
            foreach (var item in GenericSimsCallPicker.DrawItems(logical))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));
        }

        if (PluginPanel is not null && GameDialog is null && !SoundOptions.IsVisible)
        {
            foreach (var item in PluginPanel.DrawItems(logical))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));
        }

        foreach (var part in placementErrorDraws)
            draws.Add(new UIDraw(part.Image.Texture, ScaledRect(part.Rect), part.UV, part.Opacity));

        if (PictureInPicture is { } pipImage
            && PictureInPictureRect(logical, scale) is { } frame
            && !Album.Visible
            && !Camera.Visible)
        {
            draws.Add(new UIDraw(pipImage.Texture, ScaledRect(frame)));

            if (!string.IsNullOrEmpty(PictureInPictureCaption))
            {
                var lines = PipCaptionImages(PictureInPictureCaption, Math.Max(20, (int)frame.Width - 10));
                double lineH = MediumFont.LineHeight;
                double baseY = frame.MaxY - 5 - lines.Count * lineH;
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var line = lines[lineIndex];
                    if (line.Size.Width <= 0)
                        continue;
                    var lineRect = new CGRect(
                        frame.MinX + 5 - 3,
                        baseY + lineIndex * lineH - 3,
                        line.Size.Width,
                        line.Size.Height);
                    draws.Add(new UIDraw(line.Texture, ScaledRect(lineRect)));
                }
            }

            draws.Add(new UIDraw(_pictureInPictureBorder, ScaledRect(new CGRect(frame.MinX, frame.MinY, frame.Width, 2))));
            draws.Add(new UIDraw(_pictureInPictureBorder, ScaledRect(new CGRect(frame.MinX, frame.MaxY - 2, frame.Width, 2))));
            draws.Add(new UIDraw(_pictureInPictureBorder, ScaledRect(new CGRect(frame.MinX, frame.MinY + 2, 2, Math.Max(0, frame.Height - 4)))));
            draws.Add(new UIDraw(_pictureInPictureBorder, ScaledRect(new CGRect(frame.MaxX - 2, frame.MinY + 2, 2, Math.Max(0, frame.Height - 4)))));

            if (_closeBoxTexture is not null && PictureInPictureCloseRect(logical, scale) is { } closeRect)
            {
                float col = PictureInPictureClosePressed ? 1f : PictureInPictureCloseHovered ? 2f : 0f;
                var closeTex = SimsBitmapFont.SharpTextEnabled ? _vectorCloseBoxTexture : _closeBoxTexture;
                draws.Add(new UIDraw(closeTex, ScaledRect(closeRect), new Vector4(col / 4f, 0f, 0.25f, 1f)));
            }
        }

        foreach (var item in Camera.Draws(logical, Album.Visible))
            draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));

        if (GameDialog is null && PieMenu is null)
        {
            if (OptionsMode)
            {
                foreach (var part in OptionsPanel.TooltipParts(logical, true, quickTips))
                    draws.Add(new UIDraw(part.Image.Texture, ScaledRect(part.Rect), part.UV));
            }

            foreach (var part in Gameplay.TooltipParts(logical))
                draws.Add(new UIDraw(part.Image.Texture, ScaledRect(part.Rect), part.UV));

            foreach (var item in Camera.TooltipParts(logical, quickTips))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));
        }

        if (Album.Visible)
        {
            foreach (var item in Album.Draws(logical))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));

            if (GameDialog is null && PieMenu is null)
            {
                foreach (var item in Album.TooltipParts(logical, quickTips))
                    draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV));
            }
        }

        AppendCheatConsole(scale, draws);

        if (GameDialog is not null)
            DrawDialog(GameDialog, canvas, scale, draws);

        if (BudgetReportVisible && BudgetReportValues is { } values && Gameplay.State?.Budget is int budget)
        {
            foreach (var item in BudgetReport.DrawItems(values, budget, logical, quickTips))
                draws.Add(new UIDraw(item.Image.Texture, ScaledRect(item.Rect), item.UV, item.Opacity));
        }

        AppendHelp(logical, scale, draws);
        return draws;
    }

    public OriginalGameplayOverlay.Image? LoadDisposePreview(IReadOnlyDictionary<string, JsonElement> reference, string root)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(root);

        if (!reference.TryGetValue("file", out var fileEl)
            || fileEl.ValueKind != JsonValueKind.String
            || fileEl.GetString() is not { } file
            || !IsSingleFilename(file))
        {
            return null;
        }

        string? safePath = ResolveRootFile(root, file);
        if (safePath is null)
            return null;

        if (TryLoadBitmapFromFile(safePath, maxWidth: 512, maxHeight: 512) is not { } bitmap)
            return null;

        return CreateOverlayImage(bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
    }

    private List<OriginalGameplayOverlay.Image> RasterizeMediumLines(string text, int width, (byte R, byte G, byte B) color)
    {
        List<string> wrapped;
        try
        {
            wrapped = MediumFont.WrappedLines(text, width);
        }
        catch
        {
            return [];
        }

        var empty = new OriginalGameplayOverlay.Image(_black, new CGSize(0, 0));
        var result = new List<OriginalGameplayOverlay.Image>(wrapped.Count);
        foreach (string line in wrapped)
        {
            if (string.IsNullOrEmpty(line))
            {
                result.Add(empty);
                continue;
            }

            try
            {
                result.Add(OriginalGameplayOverlay.CreateImage(Device, MediumFont.RasterizeHiDpi(line, color, logicalMargin: 3)));
            }
            catch
            {
                result.Add(empty);
            }
        }
        return result;
    }

    private List<OriginalGameplayOverlay.Image> PipCaptionImages(string caption, int width)
    {
        if (_cachedPipCaption is { } cached
            && string.Equals(cached.Text, caption, StringComparison.Ordinal)
            && cached.Width == width)
        {
            return cached.Lines;
        }

        var lines = RasterizeMediumLines(caption, width, (195, 205, 205));
        _cachedPipCaption = (caption, width, lines);
        return lines;
    }

    private List<OriginalGameplayOverlay.Image> DisposePromptImages(DisposePanelState state)
    {
        int baseIndex = state.BuildModeType == 0 ? 0 : 3;
        int entryIndex = !state.Sellable
            ? (baseIndex + 2)
            : state.ResaleValue == 0
                ? baseIndex
                : (baseIndex + 1);

        string[] fallbacks =
        [
            "Press \"Delete\" or click here \nto dispose of %.",
            "Press \"Delete\" or click here \nto sell % for $.",
            "% cannot be deleted.",
            "Press \"Delete\" or click here \nto dispose of %.",
            "Press \"Delete\" or click here \nto sell % for $.",
            "% cannot be deleted."
        ];

        string template = entryIndex < _disposeStrings.Length && !string.IsNullOrEmpty(_disposeStrings[entryIndex])
            ? _disposeStrings[entryIndex]
            : fallbacks[entryIndex];
        string itemName = string.IsNullOrEmpty(state.CatalogName) ? "<Unknown>" : state.CatalogName;
        string prompt = ReplaceFirst(template, "%", itemName);
        prompt = ReplaceFirst(prompt, "$", OriginalUIStrings.Simoleons(state.ResaleValue));

        if (_cachedDisposePrompt is { } cached && string.Equals(cached.Key, prompt, StringComparison.Ordinal))
            return cached.Lines;

        var lines = RasterizeMediumLines(prompt, 390, (195, 205, 205));
        _cachedDisposePrompt = (prompt, lines);
        return lines;
    }

    private void AppendHelp(CGSize canvas, float scale, List<UIDraw> draws)
    {
        if (!HelpVisible)
            return;

        foreach (var item in HelpOverlay.DrawItems(canvas))
        {
            draws.Add(new UIDraw(
                item.Image.Texture,
                new Vector4((float)item.Rect.MinX * scale, (float)item.Rect.MinY * scale, (float)item.Rect.Width * scale, (float)item.Rect.Height * scale),
                item.UV));
        }
    }

    private void DrawDialog(OriginalDialogOverlay dialog, Vector2 canvas, float scale, List<UIDraw> draws)
    {
        dialog.Prepare(new CGSize(canvas.X, canvas.Y), scale);

        void AddPart(UITexture texture, CGRect rect, Vector4? uv = null)
        {
            var resolvedUv = uv ?? new Vector4(0f, 0f, 1f, 1f);
            draws.Add(new UIDraw(
                texture,
                new Vector4((float)rect.MinX * scale, (float)rect.MinY * scale, (float)rect.Width * scale, (float)rect.Height * scale),
                resolvedUv));
        }

        void AddCentred(OriginalDialogOverlay.Image image, double x, double y) =>
            AddPart(image.Texture, new CGRect(Math.Floor(x - image.Size.Width / 2.0), y, image.Size.Width, image.Size.Height));

        void DrawNineSliceFrame(UITexture texture, double edge, CGRect rect)
        {
            if (SimsBitmapFont.SharpTextEnabled)
            {
                int fw = Math.Max(32, (int)Math.Round(rect.Width));
                int fh = Math.Max(32, (int)Math.Round(rect.Height));
                bool isNeighborhoodSoft = ReferenceEquals(texture, dialog.NeighborhoodFrame.Texture);
                var vecFrame = SimsVectorUiKit.ToTexture(
                    isNeighborhoodSoft
                        ? SimsVectorUiKit.RenderNeighborhoodDialogFrame(fw, fh, scale: 4)
                        : SimsVectorUiKit.RenderPopupInfoFrame(fw, fh, scale: 4));
                AddPart(vecFrame, rect);
                return;
            }

            double[] xs = [rect.MinX, rect.MinX + edge, rect.MaxX - edge, rect.MaxX];
            double[] ys = [rect.MinY, rect.MinY + edge, rect.MaxY - edge, rect.MaxY];
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    AddPart(
                        texture,
                        new CGRect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]),
                        new Vector4(column / 3f, row / 3f, 1f / 3f, 1f / 3f));
                }
            }
        }

        void DrawTileButton(CGRect bounds, int state)
        {
            if (SimsBitmapFont.SharpTextEnabled)
            {
                int bw = Math.Max(24, (int)Math.Round(bounds.Width));
                int bh = Math.Max(20, (int)Math.Round(bounds.Height));
                var vecBtn = SimsVectorUiKit.ToTexture(SimsVectorUiKit.RenderSlantedButtonStrip(bw, bh, scale: 4));
                AddPart(vecBtn, bounds, new Vector4(state * 0.25f, 0f, 0.25f, 1f));
                return;
            }

            float sheetW = dialog.ButtonSheetWidth;
            float framePx = sheetW / 4f;
            float basePx = state * framePx;
            float capPx = (float)dialog.ButtonCapWidth;
            double capW = Math.Min(dialog.ButtonCapWidth, Math.Floor(bounds.Width / 2.0));
            double midW = Math.Max(0.0, bounds.Width - capW * 2.0);

            AddPart(
                dialog.ButtonTexture,
                new CGRect(bounds.MinX, bounds.MinY, capW, bounds.Height),
                new Vector4(basePx / sheetW, 0f, capPx / sheetW, 1f));
            if (midW > 0)
            {
                AddPart(
                    dialog.ButtonTexture,
                    new CGRect(bounds.MinX + capW, bounds.MinY, midW, bounds.Height),
                    new Vector4((basePx + capPx) / sheetW, 0f, MathF.Max(1f, framePx - capPx * 2f) / sheetW, 1f));
            }
            AddPart(
                dialog.ButtonTexture,
                new CGRect(bounds.MaxX - capW, bounds.MinY, capW, bounds.Height),
                new Vector4((basePx + framePx - capPx) / sheetW, 0f, capPx / sheetW, 1f));
        }

        void DrawPeopleFramedPortrait(OriginalDialogOverlay.Image portrait, CGRect targetRect, Vector4 uv)
        {
            double w = portrait.FrameSize.Width;
            double h = portrait.FrameSize.Height;
            if (w > 0 && h > 0 && w <= 41 && h <= 43 && dialog.PeopleTemplate is { } template)
            {
                AddPart(template.Texture, targetRect, template.TopLeftUV);
                var faceRect = new CGRect(
                    targetRect.MinX + Math.Floor((targetRect.Width - w) / 2.0),
                    targetRect.MinY + Math.Floor((targetRect.Height - h) / 2.0),
                    w,
                    h);
                AddPart(portrait.Texture, faceRect, uv);
            }
            else
            {
                AddPart(portrait.Texture, targetRect, uv);
            }
        }

        var frame = dialog.Rect;
        if (dialog.PhoneBookBackground is { } background)
        {
            var art = dialog.LotFrame;
            DrawNineSliceFrame(art.Texture, art.Edge, frame);
            AddPart(background.Texture, new CGRect(frame.MinX + 17, frame.MinY + 8, background.Size.Width, background.Size.Height));
            if (dialog.PhoneIcon is { } phoneIcon)
                AddPart(phoneIcon.Texture, dialog.PhoneIconRect);

            if ((dialog.CurrentPhoneBookPortrait ?? dialog.UnknownFace) is { } portrait)
                DrawPeopleFramedPortrait(portrait, dialog.ImageRect, portrait.TopLeftUV);
            else if (dialog.PeopleTemplate is { } template)
                AddPart(template.Texture, dialog.ImageRect, template.TopLeftUV);

            if (dialog.PhoneBookBannerLabel is { } banner)
            {
                var bannerRect = new CGRect(frame.MinX + 75, frame.MinY + 16, 540, 25);
                AddCentred(banner, bannerRect.MidX, Math.Floor(bannerRect.MidY - banner.Size.Height / 2.0));
            }

            foreach (var row in dialog.PhoneBookFamilyRows)
            {
                AddPart(
                    row.Image.Texture,
                    new CGRect(row.Rect.MinX + 4, Math.Floor(row.Rect.MidY - row.Image.Size.Height / 2.0), row.Image.Size.Width, row.Image.Size.Height));
            }
            foreach (var row in dialog.PhoneBookContactRows)
            {
                AddPart(
                    row.Image.Texture,
                    new CGRect(row.Rect.MinX + 4, Math.Floor(row.Rect.MidY - row.Image.Size.Height / 2.0), row.Image.Size.Width, row.Image.Size.Height));
            }
        }
        else if (dialog.Dialog.IsBookScreen)
        {
            var art = dialog.LotFrame;
            DrawNineSliceFrame(art.Texture, art.Edge, frame);

            for (int lineIdx = 0; lineIdx < dialog.BookTitleLines.Count; lineIdx++)
            {
                var titleLine = dialog.BookTitleLines[lineIdx];
                AddCentred(titleLine, frame.MinX + 169, frame.MinY + 8 + lineIdx * dialog.TitleFont.LineHeight);
            }
            if (dialog.BookTitleLines.Count == 0)
                AddCentred(dialog.Title, frame.MinX + 169, frame.MinY + 8);
            if (dialog.BookDetailTitle is { } detailTitle)
                AddCentred(detailTitle, frame.MinX + 451, frame.MinY + 8);

            foreach (var row in dialog.BookRows)
            {
                if (dialog.BookRowBackground is { } rowBg)
                {
                    float rowState = row.Index == dialog.BookSelectedIndex ? 1f
                        : dialog.BookHoveredRowIndex == row.Index ? 2f : 0f;
                    AddPart(rowBg.Texture, row.Rect, new Vector4(0f, rowState / 3f, 1f, 1f / 3f));
                }

                foreach (var slot in row.Ingredients)
                {
                    if (dialog.BookCatalogButton is { } catBtn)
                        AddPart(catBtn.Texture, slot.Rect, new Vector4(0f, 0f, 0.25f, 1f));

                    if (slot.Icon is { } icon)
                    {
                        bool twoFrames = icon.Size.Width >= 46;
                        double iconW = twoFrames ? Math.Floor(icon.Size.Width / 2.0) : icon.Size.Width;
                        double iconH = icon.Size.Height;
                        var iconRect = new CGRect(
                            slot.Rect.MinX + Math.Floor((slot.Rect.Width - iconW) / 2.0),
                            slot.Rect.MinY + Math.Floor((slot.Rect.Height - iconH) / 2.0),
                            iconW,
                            iconH);
                        AddPart(icon.Texture, iconRect, new Vector4(0f, 0f, twoFrames ? 0.5f : 1f, 1f));
                    }
                }
            }

            if (dialog.BookRowBackground is { } emptyRowBg)
            {
                foreach (var emptyRect in dialog.BookEmptyRowRects)
                    AddPart(emptyRowBg.Texture, emptyRect, new Vector4(0f, 0f, 1f, 1f / 3f));
            }

            for (int index = 0; index < dialog.BookDetail.Count; index++)
            {
                var line = dialog.BookDetail[index];
                AddPart(
                    line.Texture,
                    new CGRect(dialog.BookDetailRect.MinX, dialog.BookDetailRect.MinY + index * dialog.Font.LineHeight, line.Size.Width, line.Size.Height));
            }

            if (dialog.BookStationImage is { } station)
                AddPart(station.Texture, dialog.BookStationRect);

            if (dialog.BookBackLabels is { } backLabels)
            {
                int backState = !dialog.CanBookBack ? 3
                    : (dialog.BookBackPressed && dialog.BookBackHovered) ? 1
                    : dialog.BookBackHovered ? 2 : 0;
                DrawTileButton(dialog.BookBackRect, backState);
                var backLabel = backLabels.Image(backState);
                AddCentred(backLabel, dialog.BookBackRect.MidX, Math.Floor(dialog.BookBackRect.MidY - backLabel.Size.Height / 2.0));
            }

            if (dialog.BookPageLabel is { } pageLabel)
                AddCentred(pageLabel, dialog.BookPageRect.MidX, Math.Floor(dialog.BookPageRect.MidY - pageLabel.Size.Height / 2.0));

            if (dialog.BookNextLabels is { } nextLabels)
            {
                int nextState = !dialog.CanBookNext ? 3
                    : (dialog.BookNextPressed && dialog.BookNextHovered) ? 1
                    : dialog.BookNextHovered ? 2 : 0;
                DrawTileButton(dialog.BookNextRect, nextState);
                var nextLabel = nextLabels.Image(nextState);
                AddCentred(nextLabel, dialog.BookNextRect.MidX, Math.Floor(dialog.BookNextRect.MidY - nextLabel.Size.Height / 2.0));
            }
        }
        else
        {
            var art = ShowsLot ? dialog.LotFrame : dialog.NeighborhoodFrame;
            DrawNineSliceFrame(art.Texture, art.Edge, frame);

            if (!string.IsNullOrEmpty(dialog.Dialog.Title))
                AddPart(dialog.Title.Texture, new CGRect(dialog.TitleOrigin.X, dialog.TitleOrigin.Y, dialog.Title.Size.Width, dialog.Title.Size.Height));

            if (dialog.Illustration is { } image)
            {
                if (dialog.UsesPictureDialogStub)
                {
                    if (dialog.PictureDialogStub is { } stub)
                        AddPart(stub.Texture, dialog.ImageRect, stub.TopLeftUV);

                    var portraitRect = new CGRect(
                        dialog.ImageRect.MinX + Math.Floor((dialog.ImageRect.Width - 45.0) / 2.0),
                        dialog.ImageRect.MinY + Math.Floor((dialog.ImageRect.Height - 45.0) / 2.0),
                        45.0,
                        45.0);

                    if (dialog.IsPrecompositedPeoplePortrait && SimsBitmapFont.SharpTextEnabled && dialog.PeopleTemplate is { } hiDpiTpl)
                    {
                        AddPart(hiDpiTpl.Texture, portraitRect, hiDpiTpl.TopLeftUV);
                        AddPart(
                            image.Texture,
                            new CGRect(portraitRect.MinX + 3.0, portraitRect.MinY + 2.0, 39.0, 41.0),
                            new Vector4(3f / 45f, 2f / 45f, 39f / 45f, 41f / 45f));
                    }
                    else
                    {
                        DrawPeopleFramedPortrait(image, portraitRect, image.FrameUV);
                    }
                }
                else
                {
                    AddPart(image.Texture, dialog.ImageRect, image.FrameUV);
                }
            }

            foreach (var line in dialog.VisibleLines)
                AddPart(line.Image.Texture, line.Rect);

            if (dialog.Input is { } input)
            {
                var inputBox = dialog.InputRect;
                var borderTex = dialog.InputFocused ? dialog.InputBorderFocused : dialog.InputBorderUnfocused;
                double borderPx = SimsBitmapFont.SharpTextEnabled && scale > 1.01f ? Math.Max(0.5, 1.0 / scale) : 1.0;
                AddPart(dialog.InputBackground, inputBox);
                AddPart(borderTex, new CGRect(inputBox.MinX, inputBox.MinY, inputBox.Width, borderPx));
                AddPart(borderTex, new CGRect(inputBox.MinX, inputBox.MaxY - borderPx, inputBox.Width, borderPx));
                AddPart(borderTex, new CGRect(inputBox.MinX, inputBox.MinY + borderPx, borderPx, Math.Max(0, inputBox.Height - borderPx * 2.0)));
                AddPart(borderTex, new CGRect(inputBox.MaxX - borderPx, inputBox.MinY + borderPx, borderPx, Math.Max(0, inputBox.Height - borderPx * 2.0)));

                double maxInputW = Math.Max(0, inputBox.Width - 8);
                double drawW = Math.Min(input.Size.Width, maxInputW);
                if (drawW > 0 && input.Size.Width > 0)
                {
                    float uStart = input.Size.Width > maxInputW
                        ? (float)((input.Size.Width - drawW) / input.Size.Width)
                        : 0f;
                    AddPart(
                        input.Texture,
                        new CGRect(inputBox.MinX + 4, Math.Floor(inputBox.MidY - input.Size.Height / 2.0), drawW, input.Size.Height),
                        new Vector4(uStart, 0f, (float)(drawW / input.Size.Width), 1f));
                }
            }
        }

        foreach (var item in dialog.ScrollButtonDraws)
            AddPart(item.Texture, item.Rect, item.UV);

        if (dialog.TutorialCloseBox is { } closeBox)
        {
            AddPart(
                closeBox.Texture,
                dialog.TutorialCloseBoxRect,
                new Vector4(dialog.TutorialCloseBoxHovered ? 0.5f : 0f, 0f, 0.25f, 1f));
        }

        for (int index = 0; index < dialog.ButtonLabelStates.Count; index++)
        {
            var bounds = dialog.ButtonRects[index];
            bool disabledCall = dialog.Dialog.Kind == 10 && index == 0 && dialog.SelectedPhoneBookContact() is null;
            int btnState = (dialog.Submitting || disabledCall) ? 3
                : (dialog.Pressed == index && dialog.Hovered == index) ? 1
                : dialog.Hovered == index ? 2 : 0;
            DrawTileButton(bounds, btnState);
            var label = dialog.ButtonLabelStates[index].Image(btnState);
            AddCentred(label, bounds.MidX, Math.Floor(bounds.MidY - dialog.ButtonFont.CapMidYPx));
        }

        if (dialog.BookIngredientTooltip is { } tip)
        {
            AddPart(dialog.TooltipBorder, new CGRect(tip.Rect.MinX - 1, tip.Rect.MinY - 1, tip.Rect.Width + 2, tip.Rect.Height + 2));
            AddPart(dialog.TooltipBackground, tip.Rect);
            AddPart(
                tip.Image.Texture,
                new CGRect(tip.Rect.MinX + 4, tip.Rect.MinY + 3, tip.Image.Size.Width, tip.Image.Size.Height));
        }
    }

    private OriginalGameplayOverlay.Image CreateOverlayImage(
        int width,
        int height,
        byte[] pixels,
        bool magenta = false,
        bool blue = false)
    {
        var descriptor = UITextureDescriptor.Texture2DDescriptor(
            UITextureDescriptor.PixelFormat.Rgba8UnormSrgb,
            width,
            height,
            mipmapped: false);
        var texture = Device.MakeTexture(descriptor)
            ?? throw new ContentError("Cannot allocate gameplay panel texture");
        byte[] rgba = SpritePixels.PremultipliedSRGB(pixels, removeMagenta: magenta, removeBlue: blue);
        texture.Replace(new UIRegion(0, 0, width, height), 0, rgba, width * 4);
        return new OriginalGameplayOverlay.Image(texture, new CGSize(width, height));
    }

    private static double CurrentUptime() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static string ReplaceFirst(string text, string target, string replacement)
    {
        int index = text.IndexOf(target, StringComparison.Ordinal);
        return index >= 0
            ? string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + target.Length))
            : text;
    }

    private static bool TryGetInt32(IReadOnlyDictionary<string, JsonElement> dict, string key, out int value)
    {
        if (dict.TryGetValue(key, out var element) && element.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryReadIntArray(JsonElement obj, string property, int requiredCount, out int[] values)
    {
        values = [];
        if (!obj.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() != requiredCount)
            return false;

        var result = new int[requiredCount];
        int i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetInt32(out int v))
                return false;
            result[i++] = v;
        }
        values = result;
        return true;
    }

    private static bool IsSingleFilename(string file) =>
        !string.IsNullOrEmpty(file)
        && file.IndexOfAny(['/', '\\', ':', '\0']) < 0
        && file is not "." and not ".."
        && string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal);

    private static string? ResolveRootFile(string root, string file)
    {
        try
        {
            string directory = PackContentPaths.ResolveSymlinksInPath(root);
            string candidate = Path.Combine(directory, file);
            if (PackContentPaths.IsSymbolicLink(candidate))
                return null;
            string resolved = PackContentPaths.ResolveSymlinksInPath(candidate);
            string? parent = Path.GetDirectoryName(resolved);
            if (parent is null)
                return null;

            string trimmedParent = PackContentPaths.TrimTrailingSeparator(parent);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(trimmedParent, PackContentPaths.TrimTrailingSeparator(directory), comparison)
                ? resolved
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static SimsBitmap? TryLoadBitmapFromFile(string path, int maxWidth, int maxHeight)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4L * 1024 * 1024)
                return null;

            var bitmap = new SimsBitmap(File.ReadAllBytes(path));
            if (bitmap.Width <= 0 || bitmap.Width > maxWidth || bitmap.Height <= 0 || bitmap.Height > maxHeight)
                return null;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string[]? ReadIffStringChunk(byte[] data, int languageCode, ushort targetChunkId)
    {
        if (data.Length < 64)
            return null;

        int headerLen = Math.Min(60, data.Length);
        int nul = Array.IndexOf(data, (byte)0, 0, headerLen);
        int asciiLen = nul >= 0 ? nul : headerLen;
        string header = Encoding.ASCII.GetString(data, 0, asciiLen);
        if (!header.StartsWith("IFF FILE 2.", StringComparison.Ordinal))
            return null;

        int position = 64;
        while (position + 76 <= data.Length)
        {
            uint chunkSizeU32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
            if (chunkSizeU32 < 76 || chunkSizeU32 > int.MaxValue)
                return null;

            int chunkSize = (int)chunkSizeU32;
            if (position > data.Length - chunkSize)
                return null;

            bool isStr = data[position] == (byte)'S'
                && data[position + 1] == (byte)'T'
                && data[position + 2] == (byte)'R'
                && data[position + 3] == (byte)'#';
            ushort chunkId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 8, 2));

            if (isStr && chunkId == targetChunkId)
                return ReadStringSetForLanguage(data.AsSpan(position + 76, chunkSize - 76), languageCode);

            position += chunkSize;
        }

        return null;
    }

    private static string[]? ReadStringSetForLanguage(ReadOnlySpan<byte> chunkBody, int languageCode)
    {
        if (chunkBody.Length < 2)
            return null;

        short format = BinaryPrimitives.ReadInt16LittleEndian(chunkBody[..2]);
        int pos = 2;
        if (pos == chunkBody.Length)
            return [];
        if (pos + 2 > chunkBody.Length)
            return null;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(chunkBody.Slice(pos, 2));
        pos += 2;

        var languages = new List<string>[20];
        for (int i = 0; i < 20; i++)
            languages[i] = [];

        switch (format)
        {
            case 0:
                for (int i = 0; i < count; i++)
                {
                    if (!TryReadPascalString(chunkBody, ref pos, utf8: false, out string s))
                        return null;
                    languages[0].Add(s);
                }
                break;

            case -1:
                for (int i = 0; i < count; i++)
                {
                    if (!TryReadCString(chunkBody, ref pos, utf8: true, out string s))
                        return null;
                    languages[0].Add(s);
                }
                break;

            case -2:
                for (int i = 0; i < count; i++)
                {
                    if (!TryReadCString(chunkBody, ref pos, utf8: false, out string s)
                        || !TryReadCString(chunkBody, ref pos, utf8: false, out _))
                    {
                        return null;
                    }
                    languages[0].Add(s);
                }
                break;

            case -3:
            {
                int selectedLang = Math.Clamp(languageCode, 1, 20);
                int previousLang = -1;
                int selectedIndex = 0;
                int englishIndex = 0;
                for (int i = 0; i < count; i++)
                {
                    if (pos >= chunkBody.Length)
                        return null;
                    int lang = chunkBody[pos++];
                    if (!TryReadCString(chunkBody, ref pos, utf8: false, out string s)
                        || !TryReadCString(chunkBody, ref pos, utf8: false, out _))
                    {
                        return null;
                    }
                    if (lang == 0)
                        lang = selectedLang;
                    if (lang != previousLang)
                    {
                        selectedIndex = 0;
                        englishIndex = 0;
                        previousLang = lang;
                    }
                    if (lang == selectedLang)
                    {
                        var bucket = languages[selectedLang - 1];
                        if (selectedIndex < bucket.Count)
                            bucket[selectedIndex] = s;
                        else
                            bucket.Add(s);
                        selectedIndex++;
                    }
                    if (lang == 1)
                    {
                        var bucket = languages[0];
                        if (englishIndex < bucket.Count)
                            bucket[englishIndex] = s;
                        else
                            bucket.Add(s);
                        englishIndex++;
                    }
                }
                break;
            }

            default:
                return null;
        }

        int languageIndex = languageCode is >= 1 and <= 20 ? languageCode - 1 : 0;
        var chosen = languages[languageIndex].Count > 0 ? languages[languageIndex] : languages[0];
        return [.. chosen];
    }

    private static bool TryReadPascalString(ReadOnlySpan<byte> body, ref int pos, bool utf8, out string value)
    {
        value = string.Empty;
        if (pos >= body.Length)
            return false;
        int len = body[pos++];
        if (len > body.Length - pos)
            return false;
        value = DecodeString(body.Slice(pos, len), utf8);
        pos += len;
        return true;
    }

    private static bool TryReadCString(ReadOnlySpan<byte> body, ref int pos, bool utf8, out string value)
    {
        value = string.Empty;
        int start = pos;
        int relEnd = body[start..].IndexOf((byte)0);
        if (relEnd < 0)
            return false;
        value = DecodeString(body.Slice(start, relEnd), utf8);
        pos = start + relEnd + 1;
        return true;
    }

    private static string DecodeString(ReadOnlySpan<byte> bytes, bool utf8)
    {
        if (utf8)
        {
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Fall back to Windows-1252 when a -1 string contains legacy bytes.
            }
        }
        return DecodeWindows1252(bytes);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly char[] Cp1252High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178'
    ];

    private static string DecodeWindows1252(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                byte b = src[i];
                chars[i] = b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;
            }
        });
    }

    private void AppendCheatConsole(float drawScale, List<UIDraw> draws)
    {
        if (!CheatConsole.Visible)
            return;

        foreach (var item in CheatConsole.DrawItems(drawScale))
            draws.Add(item);
    }

    private void AppendOverheadFeedback(SceneViewport? viewport, LotZoom zoom, float uiScale, List<UIDraw> draws)
    {
        if (viewport is not { } vp || ActiveOverheadFeedback is not { Count: > 0 } items)
            return;

        int zoomInt = zoom switch
        {
            LotZoom.Far => 1,
            LotZoom.Medium => 2,
            _ => 3,
        };
        float verticalOffset = vp.Scale > 2f * uiScale + 1e-4f
            ? (0xaf * 0.5f) * vp.Scale
            : ((0xaf >> (3 - zoomInt)) + (zoomInt == 1 ? 4 : 0)) * uiScale;

        foreach (var item in items)
        {
            if (item.HasSprite || string.IsNullOrEmpty(item.Text))
                continue;

            if (!_overheadBgTextures.TryGetValue(item.BackgroundRgb, out var bgTex))
            {
                byte r = (byte)((item.BackgroundRgb >> 16) & 0xff);
                byte g = (byte)((item.BackgroundRgb >> 8) & 0xff);
                byte b = (byte)(item.BackgroundRgb & 0xff);
                bgTex = new UITexture(1, 1, SpritePixels.PremultipliedSRGB([r, g, b, 255], removeMagenta: false));
                _overheadBgTextures[item.BackgroundRgb] = bgTex;
            }

            var textKey = (item.Text, item.ForegroundRgb);
            if (!_overheadTextImages.TryGetValue(textKey, out var textImg))
            {
                byte fr = (byte)((item.ForegroundRgb >> 16) & 0xff);
                byte fg = (byte)((item.ForegroundRgb >> 8) & 0xff);
                byte fb = (byte)(item.ForegroundRgb & 0xff);
                var raster = Font.RasterizeHiDpi(item.Text, fr, fg, fb, logicalMargin: 0);
                textImg = OriginalGameplayOverlay.CreateImage(Device, raster);
                _overheadTextImages[textKey] = textImg;
            }

            if (textImg.Size.Width <= 0 || textImg.Size.Height <= 0)
                continue;

            float textW = (float)textImg.Size.Width * uiScale;
            float textH = (float)textImg.Size.Height * uiScale;
            float pad = 4f * uiScale;
            float pillW = textW + pad;
            float pillH = textH + pad;

            float anchorX = vp.Origin.X + item.X * vp.Scale;
            float anchorY = vp.Origin.Y + item.Y * vp.Scale - verticalOffset - item.StackIndex * (pillH + 2f * uiScale);
            float pillX = MathF.Round(anchorX - pillW * 0.5f);
            float pillY = MathF.Round(anchorY - pillH * 0.5f);

            if (!string.Equals(item.Kind, "relationship", StringComparison.OrdinalIgnoreCase))
                draws.Add(new UIDraw(bgTex, new Vector4(pillX, pillY, pillW, pillH)));
            draws.Add(new UIDraw(textImg.Texture, new Vector4(pillX + pad * 0.5f, pillY + pad * 0.5f, textW, textH)));
        }
    }
}
