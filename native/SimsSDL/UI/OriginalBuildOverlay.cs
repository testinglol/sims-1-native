using System.Globalization;
using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalBuildOverlay
{
    public abstract record Action
    {
        private protected Action() { }

        public sealed record Page(int Offset) : Action;
        public sealed record Select(ushort Id) : Action;
        public sealed record Kind(BuildCatalogPage.CatalogKind Value) : Action;
        public sealed record ObjectTool(OriginalBuildObjectTool Tool) : Action;
        public sealed record SelectObject(uint Guid) : Action;
        public sealed record Terrain : Action
        {
            public static readonly Terrain Instance = new();
        }
        public sealed record TerrainMode(TerrainBrushMode Mode) : Action;
        public sealed record RoofPitch(float Pitch) : Action;
        public sealed record History(ConstructionHistoryAction Value) : Action;
    }

    private static readonly (TerrainBrushMode Mode, string Label)[] TerrainModes =
    [
        (TerrainBrushMode.Lower, "Lower terrain"),
        (TerrainBrushMode.Level, "Level terrain"),
        (TerrainBrushMode.Raise, "Raise terrain"),
        (TerrainBrushMode.Grass, "Grass")
    ];

    private static readonly float[] RoofPitchValues = [0f, 0.4f, 0.6f, 1f];

    private static readonly HashSet<int> EnabledTools = BuildEnabledTools();

    private readonly UIDevice _device;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly OriginalGameplayOverlay.Image _universal;
    private readonly OriginalGameplayOverlay.Image _vectorUniversal;
    private OriginalGameplayOverlay.Image ActiveUniversal => SimsBitmapFont.SharpTextEnabled ? _vectorUniversal : _universal;
    private readonly OriginalGameplayOverlay.Image _patch;
    private readonly OriginalGameplayOverlay.Image[] _buttons;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly OriginalGameplayOverlay.Image _vectorFrame;
    private OriginalGameplayOverlay.Image ActiveFrame => SimsBitmapFont.SharpTextEnabled ? _vectorFrame : _frame;
    private readonly OriginalGameplayOverlay.Image _unknown;
    private readonly OriginalGameplayOverlay.Image _previous;
    private readonly OriginalGameplayOverlay.Image _next;
    private readonly OriginalGameplayOverlay.Image[] _terrainModeIcons;
    private readonly OriginalGameplayOverlay.Image[] _terrainModePreviews;
    private readonly OriginalGameplayOverlay.Image _poolToolIcon;
    private readonly OriginalGameplayOverlay.Image _waterToolIcon;
    private readonly OriginalGameplayOverlay.Image _poolPopupPreview;
    private readonly OriginalGameplayOverlay.Image _waterPopupPreview;
    private readonly OriginalGameplayOverlay.Image[] _roofPitchButtons;
    private readonly OriginalGameplayOverlay.Image _roofPatternTemplate;
    private readonly OriginalGameplayOverlay.Image _roofPitchPreview;
    private readonly OriginalGameplayOverlay.Image _roofPatternFallbackPreview;
    private readonly OriginalGameplayOverlay.Image[] _historyIcons;
    private readonly OriginalUIStrings _strings;

    private List<OriginalGameplayOverlay.Image?> _icons = [];
    private List<OriginalGameplayOverlay.Image?> _previews = [];
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _priceTips = new(StringComparer.Ordinal);

    public CGPoint? HoveredPoint { get; private set; }
    public OriginalBuildObjectTool? ObjectTool { get; private set; }
    public BuyCatalogPage? ObjectPage { get; private set; }
    public BuildCatalogPage.CatalogKind Kind { get; private set; } = BuildCatalogPage.CatalogKind.Floor;
    public BuildCatalogPage? Page { get; private set; }
    public ushort? Selected { get; private set; }
    public uint? SelectedObjectGUID { get; private set; }
    public float SelectedRoofPitch { get; set; } = 0.66f;
    public TerrainBrushMode? TerrainMode { get; private set; }

    public BuildCatalogPage.Item? SelectedItem =>
        Selected is { } selectedId
            ? Page?.Items.FirstOrDefault(item => item.Id == selectedId && item.Guid is null)
            : null;

    public void ClearTextCaches() => _priceTips.Clear();

    public OriginalBuildOverlay(GameContent content, UIDevice device, SimsBitmapFont font, OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(strings);

        _device = device;
        _strings = strings;

        SimsBitmapFont loadedTooltipFont = font;
        try
        {
            loadedTooltipFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_07.ffn"));
        }
        catch
        {
        }
        _tooltipFont = loadedTooltipFont;

        OriginalGameplayOverlay.Image LoadBitmap(string path)
        {
            var pixels = new SimsBitmap(content.Read("UIGraphics/cpanel/" + path));
            return OriginalGameplayOverlay.CreateImage(device, pixels.Width, pixels.Height, pixels.Rgba, magenta: true);
        }

        OriginalGameplayOverlay.Image LoadVectorTool(string path, string vectorKey)
        {
            var pixels = new SimsBitmap(content.Read("UIGraphics/cpanel/" + path));
            if (SimsBitmapFont.SharpTextEnabled)
            {
                int w1x = Math.Max(1, pixels.Width / 4);
                int h1x = Math.Max(1, pixels.Height);
                if (SimsVectorUiKit.TryRenderBuildToolStrip(vectorKey, w1x, h1x, scale: 4) is { } vec)
                    return SimsVectorUiKit.ToOverlayImage(vec);
            }
            return OriginalGameplayOverlay.CreateImage(device, pixels.Width, pixels.Height, pixels.Rgba, magenta: true);
        }

        OriginalGameplayOverlay.Image LoadVectorPreview(string path, string vectorKey)
        {
            var pixels = new SimsBitmap(content.Read("UIGraphics/cpanel/" + path));
            if (SimsBitmapFont.SharpTextEnabled &&
                SimsVectorUiKit.TryRenderBuildPopupPreview(vectorKey, pixels.Width, pixels.Height, scale: 4) is { } vec)
            {
                return SimsVectorUiKit.ToOverlayImage(vec);
            }
            return OriginalGameplayOverlay.CreateImage(device, pixels.Width, pixels.Height, pixels.Rgba, magenta: true);
        }

        var targa = new SimsTarga(content.Read("UIGraphics/cpanel/Backgrounds/UniversalBack.tga"));
        _universal = OriginalGameplayOverlay.CreateImage(device, targa.Width, targa.Height, targa.Rgba);
        try { _vectorUniversal = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFittedPanel("universalback", scale: 8)); } catch { _vectorUniversal = _universal; }
        _patch = LoadBitmap("Backgrounds/BuildPatch.bmp");

        _buttons = new OriginalGameplayOverlay.Image[OriginalBuildLayout.Assets.Count];
        for (int i = 0; i < OriginalBuildLayout.Assets.Count; i++)
            _buttons[i] = LoadVectorTool("Buttons/ToolBtn" + OriginalBuildLayout.Assets[i] + ".bmp", OriginalBuildLayout.Assets[i]);

        _frame = LoadBitmap("Catalog/ThumbTemplate.bmp");
        _vectorFrame = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderThumbTemplateStrip(45, scale: 4));
        _unknown = LoadBitmap("CatalogUnknown.bmp");
        _previous = LoadBitmap("Buttons/ScrollLeft.bmp");
        _next = LoadBitmap("Buttons/ScrollRight.bmp");

        _terrainModeIcons =
        [
            LoadVectorTool("Build/TerrainDownIcon.BMP", "TerrainDownIcon"),
            LoadVectorTool("Build/TerrainLevelIcon.BMP", "TerrainLevelIcon"),
            LoadVectorTool("Build/TerrainUpIcon.BMP", "TerrainUpIcon"),
            LoadVectorTool("HDBuild/TerrainGrassIcon.BMP", "TerrainGrassIcon")
        ];
        _terrainModePreviews =
        [
            LoadVectorPreview("Build/PopupTerrainDown.bmp", "PopupTerrainDown"),
            LoadVectorPreview("Build/PopupTerrainLevel.bmp", "PopupTerrainLevel"),
            LoadVectorPreview("Build/PopupTerrainUp.BMP", "PopupTerrainUp"),
            LoadVectorPreview("HDBuild/popupGrass.bmp", "popupGrass")
        ];

        _poolToolIcon = LoadBitmap("Build/PoolToolIcon.bmp");
        _waterToolIcon = LoadBitmap("HDBuild/WaterToolIcon.bmp");
        _poolPopupPreview = LoadBitmap("Build/PopupPool.BMP");
        _waterPopupPreview = LoadBitmap("HDBuild/PopupWaterTool.bmp");

        _roofPitchButtons =
        [
            LoadBitmap("Build/roofflat.bmp"),
            LoadBitmap("Build/roofshallow.bmp"),
            LoadBitmap("Build/roofmedium.bmp"),
            LoadBitmap("Build/roofsteep.bmp")
        ];
        _roofPatternTemplate = LoadBitmap("Build/roofpatterntemplate.bmp");
        _roofPitchPreview = LoadVectorPreview("Build/popuproofpitch.bmp", "popuproofpitch");
        _roofPatternFallbackPreview = LoadBitmap("Build/popuproofpattern.bmp");

        _historyIcons =
        [
            LoadVectorTool("Buttons/Undo.bmp", "Undo"),
            LoadVectorTool("Buttons/Redo.bmp", "Redo")
        ];
    }

    private static HashSet<int> BuildEnabledTools()
    {
        var set = new HashSet<int> { 0, 7, 3, 2, 1, 10 };
        foreach (var tool in OriginalBuildObjectToolExtensions.AllCases)
            set.Add(tool.ButtonIndex());
        return set;
    }

    public void Hover(CGPoint? point) => HoveredPoint = point;

    public void ClearHover() => HoveredPoint = null;

    public void Clear()
    {
        Page = null;
        ObjectPage = null;
        _icons = [];
        _previews = [];
        Selected = null;
        SelectedObjectGUID = null;
    }

    public void ChangeKind(BuildCatalogPage.CatalogKind kind)
    {
        Clear();
        ObjectTool = null;
        TerrainMode = null;
        Kind = kind;
    }

    public void ChangeTerrainMode(TerrainBrushMode mode)
    {
        Clear();
        ObjectTool = null;
        TerrainMode = mode;
    }

    public void ChangeObjectTool(OriginalBuildObjectTool tool)
    {
        Clear();
        TerrainMode = null;
        ObjectTool = tool;
    }

    public void CancelSelection()
    {
        Selected = null;
        SelectedObjectGUID = null;
    }

    public void Choose(ushort id)
    {
        if (Page?.Items.Any(item => item.Id == id && item.Guid is null) == true)
        {
            Selected = id;
            SelectedObjectGUID = null;
        }
    }

    public void ChooseObject(uint guid, int? budget = null)
    {
        _ = budget;
        bool inPage = Page?.Items.Any(item => item.Guid == guid) == true;
        bool inObjectPage = ObjectPage?.Items.Any(item => item.Guid == guid) == true;
        if (!inPage && !inObjectPage)
            return;

        Selected = null;
        SelectedObjectGUID = guid;
    }

    public void Update(BuildCatalogPage page, string root)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(root);

        page.Validate();
        Kind = page.Kind;
        ObjectTool = null;
        TerrainMode = null;
        ObjectPage = null;

        string resolvedRoot = PackContentPaths.ResolveSymlinksInPath(Path.GetFullPath(root));
        var icons = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);
        var previews = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);

        foreach (var item in page.Items)
        {
            if (item.Guid is null && item.Icon is { } frameSpec && (frameSpec.Columns is < 1 or > 4 || frameSpec.Rows != 1))
                throw new ContentError("Invalid build thumbnail frames");

            icons.Add(LoadThumbnail(
                item.Icon,
                resolvedRoot,
                "Build thumbnail escapes scene directory or exceeds size limit",
                "Invalid build thumbnail dimensions"));
            previews.Add(LoadThumbnail(
                item.Preview,
                resolvedRoot,
                "Build thumbnail escapes scene directory or exceeds size limit",
                "Invalid build thumbnail dimensions"));
        }

        Page = page;
        _icons = icons;
        _previews = previews;

        if (Selected is { } selId && !page.Items.Any(item => item.Id == selId && item.Guid is null))
            Selected = null;
        if (SelectedObjectGUID is { } selGuid && !page.Items.Any(item => item.Guid == selGuid))
            SelectedObjectGUID = null;
    }

    public void UpdateObjects(BuyCatalogPage page, string root)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(root);

        page.Validate(buildObjects: true);
        var tool = OriginalBuildObjectToolExtensions.CatalogTool(page.Category)
            ?? throw new ContentError("Unknown build object category");

        string resolvedRoot = PackContentPaths.ResolveSymlinksInPath(Path.GetFullPath(root));
        var icons = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);
        var previews = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);

        foreach (var item in page.Items)
        {
            icons.Add(LoadThumbnail(
                item.Icon,
                resolvedRoot,
                "Build object thumbnail escapes scene directory or exceeds size limit",
                "Invalid build object thumbnail dimensions"));
            previews.Add(LoadThumbnail(
                item.Preview,
                resolvedRoot,
                "Build object thumbnail escapes scene directory or exceeds size limit",
                "Invalid build object thumbnail dimensions"));
        }

        Clear();
        ObjectTool = tool;
        ObjectPage = page;
        _icons = icons;
        _previews = previews;
    }

    private OriginalGameplayOverlay.Image? LoadThumbnail(
        GameplayPanelState.Image? image,
        string resolvedRoot,
        string escapeMessage,
        string dimensionMessage)
    {
        if (image is null)
            return null;

        string? safeFile = ResolveRootFile(resolvedRoot, image.File);
        if (safeFile is null)
            throw new ContentError(escapeMessage);

        var fileInfo = new FileInfo(safeFile);
        if (!fileInfo.Exists || fileInfo.Length > 4L * 1024 * 1024)
            throw new ContentError(escapeMessage);

        if (image.Columns <= 0 || image.Rows <= 0)
            throw new ContentError(dimensionMessage);

        var bitmap = new SimsBitmap(File.ReadAllBytes(safeFile));
        if (bitmap.Width <= 0 || bitmap.Height <= 0
            || bitmap.Width > 512 || bitmap.Height > 512
            || bitmap.Width % image.Columns != 0 || bitmap.Height % image.Rows != 0)
        {
            throw new ContentError(dimensionMessage);
        }

        return OriginalGameplayOverlay.CreateImage(
            _device,
            bitmap.Width,
            bitmap.Height,
            bitmap.Rgba,
            magenta: !bitmap.HasAlpha,
            decontaminateEdges: bitmap.HasAlpha);
    }

    private static string? ResolveRootFile(string resolvedRoot, string file)
    {
        if (string.IsNullOrEmpty(file)
            || file.IndexOfAny(['/', '\\', ':', '\0']) >= 0
            || file is "." or ".."
            || !string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal))
        {
            return null;
        }

        string candidate = Path.Combine(resolvedRoot, file);
        if (PackContentPaths.IsSymbolicLink(candidate))
            return null;

        string resolvedFile = PackContentPaths.ResolveSymlinksInPath(candidate);
        string? parent = Path.GetDirectoryName(resolvedFile);
        if (parent is null)
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
                PackContentPaths.TrimTrailingSeparator(parent),
                PackContentPaths.TrimTrailingSeparator(resolvedRoot),
                comparison)
            ? resolvedFile
            : null;
    }

    /// <summary>Calculates the number of roof pattern columns fitting between the grid origin at x=608 and
    /// the right scroll button at canvas width minus 14.</summary>
    public static int RoofColumns(CGSize canvas)
    {
        if (!double.IsFinite(canvas.Width))
            return 1;
        return Math.Clamp((int)(Math.Max(0.0, canvas.Width - 622.0) / 45.0), 1, 32);
    }

    public static int RoofCapacity(CGSize canvas) => RoofColumns(canvas) * 2;

    public static CGRect RoofThumbnail(int index, CGSize canvas)
    {
        int cols = RoofColumns(canvas);
        return new CGRect(608 + (index % cols) * 45, (int)canvas.Height - 94 + (index / cols) * 45, 45, 45);
    }

    private bool ShowsRoofPanel => ObjectTool is null && TerrainMode is null && Kind == BuildCatalogPage.CatalogKind.Roof;

    public int VisibleCapacity(CGSize canvas) =>
        ShowsRoofPanel ? RoofCapacity(canvas) : OriginalBuildLayout.Capacity(canvas);

    public CGRect ItemThumbnail(int index, CGSize canvas) =>
        ShowsRoofPanel ? RoofThumbnail(index, canvas) : OriginalBuildLayout.Thumbnail(index, canvas);

    private List<(string Label, CGRect Rect)> ToolControls(CGSize canvas)
    {
        (int Index, string Fallback)[] fixedTools =
        [
            (0, "Terrain"),
            (1, "Water"),
            (7, "Floor patterns"),
            (3, "Wallpaper patterns"),
            (2, "Walls and fences"),
            (10, "Roof")
        ];

        var result = new List<(string Label, CGRect Rect)>(fixedTools.Length + OriginalBuildObjectToolExtensions.AllCases.Count);
        foreach (var (index, fallback) in fixedTools)
            result.Add((_strings.BuildToolTip(index, fallback), ToolRect(index, canvas)));

        foreach (var tool in OriginalBuildObjectToolExtensions.AllCases)
            result.Add((_strings.BuildToolTip(tool.ButtonIndex(), tool.Label()), ToolRect(tool.ButtonIndex(), canvas)));

        return result;
    }

    private string RoofPitchTip(int index) => index switch
    {
        0 => _strings.RoofString(12, "Flat Roof"),
        1 => _strings.RoofString(1, "Shallow Roof Pitch"),
        2 => _strings.RoofString(3, "Medium Roof Pitch"),
        _ => _strings.RoofString(5, "Steep Roof Pitch")
    };

    public (string Text, CGRect Rect)? ToolTipAt(CGPoint point, CGSize canvas, bool canUndo = false, bool canRedo = false)
    {
        static (string Text, CGRect Rect)? NonEmpty(string text, CGRect rect) =>
            string.IsNullOrWhiteSpace(text) ? null : (text, rect);

        foreach (var control in ToolControls(canvas))
        {
            if (Contains(control.Rect, point))
                return NonEmpty(control.Label, control.Rect);
        }

        var undoRect = OriginalBuildLayout.HistoryControl(0, canvas, _historyIcons[0].Size);
        if (canUndo && Contains(undoRect, point))
            return NonEmpty(_strings.BuildToolTip(12, "Undo Last"), undoRect);

        var redoRect = OriginalBuildLayout.HistoryControl(1, canvas, _historyIcons[1].Size);
        if (canRedo && Contains(redoRect, point))
            return NonEmpty(_strings.BuildToolTip(13, "Redo Last"), redoRect);

        if (ShowsRoofPanel)
        {
            for (int i = 0; i < 4; i++)
            {
                var pitchRect = RoofPitchControl(i, canvas);
                if (Contains(pitchRect, point))
                    return NonEmpty(RoofPitchTip(i), pitchRect);
            }
        }

        int capacity = VisibleCapacity(canvas);
        bool canPrev = (ObjectPage?.Offset ?? Page?.Offset ?? 0) > 0 && TerrainMode is null;
        bool canNext = (ObjectPage?.NextOffset(capacity) ?? Page?.NextOffset(capacity)) is not null && TerrainMode is null;

        var prevRect = ArrowRect(false, canvas);
        if (canPrev && Contains(prevRect, point))
            return NonEmpty(_strings.BuildToolTip(14, "Previous Page"), prevRect);

        var nextRect = ArrowRect(true, canvas);
        if (canNext && Contains(nextRect, point))
            return NonEmpty(_strings.BuildToolTip(15, "Next Page"), nextRect);

        return null;
    }

    private CGRect ArrowRect(bool forward, CGSize canvas)
    {
        var image = forward ? _next : _previous;
        var size = new CGSize(image.Size.Width / 4.0, image.Size.Height);
        if (ShowsRoofPanel)
        {
            // Positions the left roof scroll button at (596, canvas height - 75) and the right button at
            // (canvas width - 14, canvas height - 75).
            return new CGRect(forward ? canvas.Width - 14 : 596, canvas.Height - 75, size.Width, size.Height);
        }
        return OriginalBuildLayout.Arrow(forward, size, canvas);
    }

    /// <summary>Calculates roof pitch button rectangles from coordinate pairs stored in string table 147,
    /// applying a 4-pixel horizontal offset.</summary>
    private CGRect RoofPitchControl(int index, CGSize canvas)
    {
        double[] yOffsets = [71, 50, 30, 1];
        var image = _roofPitchButtons[index];
        var size = new CGSize(image.Size.Width / 4.0, image.Size.Height);
        return new CGRect(538 + 6, canvas.Height - 95 + yOffsets[index], size.Width, size.Height);
    }

    private bool IsRoofPitchSelected(int index) => index switch
    {
        0 => SelectedRoofPitch < 0.2f,
        1 => MathF.Abs(SelectedRoofPitch - 0.4f) < 0.1f,
        2 => MathF.Abs(SelectedRoofPitch - 0.6f) < 0.1f,
        _ => SelectedRoofPitch > 0.8f
    };

    private CGRect ToolRect(int index, CGSize canvas) =>
        new CGRect(
                OriginalBuildLayout.Positions[index],
                new CGSize(_buttons[index].Size.Width / 4.0, _buttons[index].Size.Height))
            .OffsetBy(0, canvas.Height - 100);

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        (int Index, string Fallback)[] fixedTools =
        [
            (0, "Terrain"),
            (1, "Water"),
            (7, "Floor patterns"),
            (3, "Wallpaper patterns"),
            (2, "Walls and fences"),
            (10, "Roof")
        ];

        var result = new List<(string Label, CGRect Rect)>();
        foreach (var (index, fallback) in fixedTools)
            result.Add((fallback, ToolRect(index, canvas)));

        foreach (var tool in OriginalBuildObjectToolExtensions.AllCases)
            result.Add((tool.Label(), ToolRect(tool.ButtonIndex(), canvas)));

        foreach (var item in ToolControls(canvas))
        {
            if (!result.Any(existing => existing.Label == item.Label && existing.Rect == item.Rect))
                result.Add(item);
        }

        result.Add(("Undo construction", OriginalBuildLayout.HistoryControl(0, canvas, _historyIcons[0].Size)));
        result.Add(("Redo construction", OriginalBuildLayout.HistoryControl(1, canvas, _historyIcons[1].Size)));

        if (ShowsRoofPanel)
        {
            for (int i = 0; i < RoofPitchValues.Length; i++)
            {
                result.Add((
                    "Roof pitch " + RoofPitchValues[i].ToString("0.0", CultureInfo.InvariantCulture),
                    RoofPitchControl(i, canvas)));
            }
        }

        int capacity = VisibleCapacity(canvas);
        if (TerrainMode is not null)
        {
            for (int i = 0; i < TerrainModes.Length; i++)
                result.Add((TerrainModes[i].Label, OriginalBuildLayout.Thumbnail(i, canvas)));
        }
        else if (ObjectPage is { } objPage)
        {
            int count = Math.Min(objPage.Items.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                var item = objPage.Items[i];
                result.Add((item.Name + " — §" + item.Price, OriginalBuildLayout.Thumbnail(i, canvas)));
            }
            if (objPage.Offset > 0)
                result.Add(("Previous build catalog page", ArrowRect(false, canvas)));
            if (objPage.NextOffset(capacity) is not null)
                result.Add(("Next build catalog page", ArrowRect(true, canvas)));
        }
        else if (Page is { } currentPage)
        {
            int count = Math.Min(currentPage.Items.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                var item = currentPage.Items[i];
                result.Add((item.Name + " — §" + item.Price, ItemThumbnail(i, canvas)));
            }
            if (currentPage.Offset > 0)
                result.Add(("Previous build catalog page", ArrowRect(false, canvas)));
            if (currentPage.NextOffset(capacity) is not null)
                result.Add(("Next build catalog page", ArrowRect(true, canvas)));
        }

        return result;
    }

    public Action? ActionAt(CGPoint point, CGSize canvas)
    {
        foreach (var objectTool in OriginalBuildObjectToolExtensions.AllCases)
        {
            if (Contains(ToolRect(objectTool.ButtonIndex(), canvas), point))
                return new Action.ObjectTool(objectTool);
        }

        if (Contains(ToolRect(0, canvas), point)) return Action.Terrain.Instance;
        if (Contains(ToolRect(10, canvas), point)) return new Action.Kind(BuildCatalogPage.CatalogKind.Roof);
        if (Contains(ToolRect(1, canvas), point)) return new Action.Kind(BuildCatalogPage.CatalogKind.Water);
        if (Contains(ToolRect(2, canvas), point)) return new Action.Kind(BuildCatalogPage.CatalogKind.WallStyle);
        if (Contains(ToolRect(7, canvas), point)) return new Action.Kind(BuildCatalogPage.CatalogKind.Floor);
        if (Contains(ToolRect(3, canvas), point)) return new Action.Kind(BuildCatalogPage.CatalogKind.Wall);

        if (Contains(OriginalBuildLayout.HistoryControl(0, canvas, _historyIcons[0].Size), point))
            return new Action.History(ConstructionHistoryAction.Undo);
        if (Contains(OriginalBuildLayout.HistoryControl(1, canvas, _historyIcons[1].Size), point))
            return new Action.History(ConstructionHistoryAction.Redo);

        if (ShowsRoofPanel)
        {
            for (int i = 0; i < 4; i++)
            {
                if (Contains(RoofPitchControl(i, canvas), point))
                    return new Action.RoofPitch(RoofPitchValues[i]);
            }
        }

        int capacity = VisibleCapacity(canvas);
        if (TerrainMode is not null)
        {
            for (int i = 0; i < TerrainModes.Length; i++)
            {
                if (Contains(OriginalBuildLayout.Thumbnail(i, canvas), point))
                    return new Action.TerrainMode(TerrainModes[i].Mode);
            }
        }
        else if (ObjectPage is { } objPage)
        {
            if (objPage.Offset > 0 && Contains(ArrowRect(false, canvas), point))
                return new Action.Page(Math.Max(0, objPage.Offset - capacity));
            if (objPage.NextOffset(capacity) is { } nextOffset && Contains(ArrowRect(true, canvas), point))
                return new Action.Page(nextOffset);

            int count = Math.Min(objPage.Items.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                if (Contains(OriginalBuildLayout.Thumbnail(i, canvas), point))
                    return new Action.SelectObject(objPage.Items[i].Guid);
            }
        }
        else if (Page is { } currentPage)
        {
            if (currentPage.Offset > 0 && Contains(ArrowRect(false, canvas), point))
                return new Action.Page(Math.Max(0, currentPage.Offset - capacity));
            if (currentPage.NextOffset(capacity) is { } nextOffset && Contains(ArrowRect(true, canvas), point))
                return new Action.Page(nextOffset);

            int count = Math.Min(currentPage.Items.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                if (Contains(ItemThumbnail(i, canvas), point))
                {
                    var item = currentPage.Items[i];
                    return item.Guid is { } guid ? new Action.SelectObject(guid) : new Action.Select(item.Id);
                }
            }
        }

        return null;
    }

    /// <summary>Extracts a catalog icon from its sprite sheet, using the first frame for normal display,
    /// the second frame for selected or hovered states, or the first half of the fallback texture.</summary>
    public static OriginalGameplayOverlay.Part Icon(
        OriginalGameplayOverlay.Image? image,
        GameplayPanelState.Image? iconSpec,
        OriginalGameplayOverlay.Image unknown,
        bool highlighted = false)
    {
        int columns = image is null ? 2 : iconSpec?.Columns ?? 1;
        int rows = image is null ? 1 : iconSpec?.Rows ?? 1;
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        var chosen = image ?? unknown;
        var size = new CGSize(chosen.Size.Width / columns, chosen.Size.Height / rows);
        float col = columns > 1 && highlighted ? 1f : 0f;
        return new OriginalGameplayOverlay.Part(
            chosen,
            new CGRect(CGPoint.Zero, size),
            new Vector4(col / columns, 0f, 1f / columns, 1f / rows));
    }

    public static OriginalGameplayOverlay.Part Fit(OriginalGameplayOverlay.Part icon, CGRect rect)
    {
        var size = icon.Rect.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return new OriginalGameplayOverlay.Part(icon.Image, rect, icon.UV);

        double scale = Math.Min(1.0, Math.Min(OriginalBuildLayout.ThumbnailInner / size.Width, OriginalBuildLayout.ThumbnailInner / size.Height));
        double w = Math.Max(1.0, Math.Floor(size.Width * scale));
        double h = Math.Max(1.0, Math.Floor(size.Height * scale));
        return new OriginalGameplayOverlay.Part(
            icon.Image,
            new CGRect(rect.MinX + Math.Floor((rect.Width - w) / 2.0), rect.MinY + Math.Floor((rect.Height - h) / 2.0), w, h),
            icon.UV);
    }

    private OriginalGameplayOverlay.Part IconAt(int index, GameplayPanelState.Image? iconSpec, bool highlighted = false) =>
        Icon(index >= 0 && index < _icons.Count ? _icons[index] : null, iconSpec, _unknown, highlighted);

    /// <summary>Renders the catalog price tag tooltip, styling text black when affordable or red when the
    /// item price exceeds the current budget.</summary>
    private OriginalGameplayOverlay.Image? PriceTooltip(int price, bool unaffordable)
    {
        string key = $"{price}:{(unaffordable ? 1 : 0)}";
        if (_priceTips.TryGetValue(key, out var cached))
            return cached;

        try
        {
            string label = "§" + OriginalGameplayOverlay.Grouped(price);
            (byte R, byte G, byte B) color = unaffordable ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0);
            var image = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeBoxHiDpi(label, _tooltipFont, color));
            _priceTips[key] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public (OriginalCatalogPopup.Item Item, OriginalGameplayOverlay.Part Icon)? PopupAt(CGPoint point, CGSize canvas)
    {
        if (TerrainMode is not null)
        {
            for (int i = 0; i < TerrainModes.Length; i++)
            {
                if (!Contains(OriginalBuildLayout.Thumbnail(i, canvas), point))
                    continue;

                var mode = TerrainModes[i].Mode;
                var info = _strings.TerrainCatalogItem(mode);
                var preview = _terrainModePreviews[i];
                return (
                    new OriginalCatalogPopup.Item(info.Name, info.Price, info.Description, []),
                    new OriginalGameplayOverlay.Part(preview, new CGRect(CGPoint.Zero, preview.Size)));
            }
            return null;
        }

        int capacity = VisibleCapacity(canvas);
        if (ObjectPage is { } objPage)
        {
            int count = Math.Min(objPage.Items.Count, capacity);
            for (int i = 0; i < count; i++)
            {
                if (!Contains(OriginalBuildLayout.Thumbnail(i, canvas), point))
                    continue;

                var item = objPage.Items[i];
                var preview = i < _previews.Count ? _previews[i] : null;
                var icon = i < _icons.Count ? _icons[i] : null;
                return (
                    new OriginalCatalogPopup.Item(item.Name, item.Price, item.Description, item.Ratings ?? []),
                    Icon(preview ?? icon, preview is null ? item.Icon : item.Preview, _unknown));
            }
            return null;
        }

        if (ShowsRoofPanel)
        {
            for (int i = 0; i < 4; i++)
            {
                if (Contains(RoofPitchControl(i, canvas), point))
                {
                    string title = _strings.RoofString(14, "Roof Pitch");
                    string desc = _strings.RoofString(15, string.Empty);
                    return (
                        new OriginalCatalogPopup.Item(title, 0, desc, []),
                        new OriginalGameplayOverlay.Part(_roofPitchPreview, new CGRect(CGPoint.Zero, _roofPitchPreview.Size)));
                }
            }

            if (Page is not { } roofPage)
                return null;

            int roofCount = Math.Min(roofPage.Items.Count, capacity);
            for (int i = 0; i < roofCount; i++)
            {
                if (!Contains(RoofThumbnail(i, canvas), point))
                    continue;

                string title = _strings.RoofString(16, "Roof Patterns");
                string desc = _strings.RoofString(17, string.Empty);
                var previewImage = (i < _previews.Count ? _previews[i] : null) ?? _roofPatternFallbackPreview;
                return (
                    new OriginalCatalogPopup.Item(title, 0, desc, []),
                    new OriginalGameplayOverlay.Part(previewImage, new CGRect(CGPoint.Zero, previewImage.Size)));
            }
            return null;
        }

        if (Page is not { } currentPage)
            return null;

        int pageCount = Math.Min(currentPage.Items.Count, capacity);
        for (int i = 0; i < pageCount; i++)
        {
            if (!Contains(OriginalBuildLayout.Thumbnail(i, canvas), point))
                continue;

            var item = currentPage.Items[i];
            if (Kind == BuildCatalogPage.CatalogKind.Water && item.Guid is null)
            {
                var preview = item.Id == 65535 ? _poolPopupPreview : _waterPopupPreview;
                return (
                    new OriginalCatalogPopup.Item(item.Name, item.Price, item.Description, []),
                    new OriginalGameplayOverlay.Part(preview, new CGRect(CGPoint.Zero, preview.Size)));
            }

            var previewImg = i < _previews.Count ? _previews[i] : null;
            var iconImg = i < _icons.Count ? _icons[i] : null;
            return (
                new OriginalCatalogPopup.Item(
                    item.Name,
                    item.Price,
                    item.Description,
                    [],
                    Kind == BuildCatalogPage.CatalogKind.Floor && item.Guid is null),
                Icon(previewImg ?? iconImg, previewImg is null ? item.Icon : item.Preview, _unknown));
        }

        return null;
    }

    public List<OriginalGameplayOverlay.Part> Parts(
        CGSize canvas,
        int? budget = null,
        uint? selectedObject = null,
        bool? canUndo = null,
        bool? canRedo = null)
    {
        double top = canvas.Height - 183;
        var result = new List<OriginalGameplayOverlay.Part>
        {
            new(ActiveUniversal, new CGRect(0, top, _universal.Size.Width, _universal.Size.Height)),
            new(_patch, new CGRect(113, top + 51, _patch.Size.Width, _patch.Size.Height))
        };

        int selectedTool = TerrainMode is not null
            ? 0
            : ObjectTool is { } tool
                ? tool.ButtonIndex()
                : Kind switch
                {
                    BuildCatalogPage.CatalogKind.Floor => 7,
                    BuildCatalogPage.CatalogKind.Wall => 3,
                    BuildCatalogPage.CatalogKind.Water => 1,
                    BuildCatalogPage.CatalogKind.Roof => 10,
                    _ => 2
                };

        for (int index = 0; index < _buttons.Length; index++)
        {
            var rect = ToolRect(index, canvas);
            bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
            float uvX = !EnabledTools.Contains(index) ? 0.75f : index == selectedTool ? 0.25f : hovered ? 0.50f : 0f;
            result.Add(new OriginalGameplayOverlay.Part(_buttons[index], rect, new Vector4(uvX, 0f, 0.25f, 1f)));
        }

        for (int index = 0; index < _historyIcons.Length; index++)
        {
            bool enabled = index == 0 ? canUndo == true : canRedo == true;
            var rect = OriginalBuildLayout.HistoryControl(index, canvas, _historyIcons[index].Size);
            bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
            float uvX = !enabled ? 0.75f : hovered ? 0.50f : 0f;
            result.Add(new OriginalGameplayOverlay.Part(_historyIcons[index], rect, new Vector4(uvX, 0f, 0.25f, 1f)));
        }

        if (ShowsRoofPanel)
        {
            for (int index = 0; index < 4; index++)
            {
                var rect = RoofPitchControl(index, canvas);
                bool selected = IsRoofPitchSelected(index);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float uvX = selected ? 0.25f : hovered ? 0.50f : 0f;
                result.Add(new OriginalGameplayOverlay.Part(_roofPitchButtons[index], rect, new Vector4(uvX, 0f, 0.25f, 1f)));
            }
        }

        int capacity = VisibleCapacity(canvas);
        if (ObjectPage is { } objPage)
        {
            OriginalGameplayOverlay.Part? hoveredPricePart = null;
            int count = Math.Min(objPage.Items.Count, capacity);
            for (int index = 0; index < count; index++)
            {
                var item = objPage.Items[index];
                var rect = OriginalBuildLayout.Thumbnail(index, canvas);
                bool chosen = (selectedObject ?? SelectedObjectGUID) is { } targetGuid && item.Guid == targetGuid;
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float state = chosen ? 1f : hovered ? 2f : 0f;
                result.Add(new OriginalGameplayOverlay.Part(ActiveFrame, rect, new Vector4(state / 4f, 0f, 0.25f, 1f)));
                OriginalGameplayOverlay.Part objThumbPart = SimsBitmapFont.SharpTextEnabled
                    && index < _previews.Count
                    && _previews[index] is { } hdObjPreview
                    && item.Preview is not null
                    ? Icon(hdObjPreview, item.Preview, _unknown)
                    : IconAt(index, item.Icon, highlighted: chosen || hovered);
                result.Add(Fit(objThumbPart, rect));
                if (hovered && ObjectTool != OriginalBuildObjectTool.Hand
                    && PriceTooltip(item.Price, budget is { } b && item.Price > b) is { } tip)
                {
                    hoveredPricePart = new OriginalGameplayOverlay.Part(
                        tip,
                        new CGRect(rect.MinX, rect.MaxY - tip.Size.Height, tip.Size.Width, tip.Size.Height));
                }
            }

            if (hoveredPricePart is { } pricePart)
                result.Add(pricePart);

            foreach (bool forward in new[] { false, true })
            {
                bool enabled = forward ? objPage.NextOffset(capacity) is not null : objPage.Offset > 0;
                var rect = ArrowRect(forward, canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float uvX = !enabled ? 0.75f : hovered ? 0.50f : 0f;
                result.Add(new OriginalGameplayOverlay.Part(forward ? _next : _previous, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
            }
        }

        if (TerrainMode is not null)
        {
            OriginalGameplayOverlay.Part? hoveredPricePart = null;
            for (int index = 0; index < TerrainModes.Length; index++)
            {
                var mode = TerrainModes[index].Mode;
                var slot = OriginalBuildLayout.Thumbnail(index, canvas);
                var icon = _terrainModeIcons[index];
                var frameSize = new CGSize(icon.Size.Width / 4.0, icon.Size.Height);
                var rect = new CGRect(
                    slot.MinX + Math.Floor((slot.Width - frameSize.Width) / 2.0),
                    slot.MinY + Math.Floor((slot.Height - frameSize.Height) / 2.0),
                    frameSize.Width,
                    frameSize.Height);
                bool chosen = mode == TerrainMode;
                bool hovered = HoveredPoint is { } hp && Contains(slot, hp);
                float uvX = chosen ? 0.25f : hovered ? 0.50f : 0f;
                result.Add(new OriginalGameplayOverlay.Part(icon, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
                if (hovered)
                {
                    var info = _strings.TerrainCatalogItem(mode);
                    if (PriceTooltip(info.Price, budget is { } b && info.Price > b) is { } tip)
                    {
                        hoveredPricePart = new OriginalGameplayOverlay.Part(
                            tip,
                            new CGRect(slot.MinX, slot.MaxY - tip.Size.Height, tip.Size.Width, tip.Size.Height));
                    }
                }
            }

            if (hoveredPricePart is { } pricePart)
                result.Add(pricePart);
        }

        if (Page is { } currentPage)
        {
            OriginalGameplayOverlay.Part? hoveredPricePart = null;
            int count = Math.Min(currentPage.Items.Count, capacity);
            for (int index = 0; index < count; index++)
            {
                var item = currentPage.Items[index];
                var rect = ItemThumbnail(index, canvas);
                bool chosen = item.Guid is { } guid
                    ? (SelectedObjectGUID ?? selectedObject) is { } targetGuid && guid == targetGuid
                    : Selected is { } selId && item.Id == selId;
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);

                if (ShowsRoofPanel)
                {
                    float uvX = chosen ? 0.25f : hovered ? 0.50f : 0f;
                    result.Add(new OriginalGameplayOverlay.Part(_roofPatternTemplate, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
                    if (index < _icons.Count && _icons[index] is { } roofImage && roofImage.Size.Width > 0 && roofImage.Size.Height > 0)
                    {
                        // Draws the roof pattern texture within a 36x36 pixel area offset (+4, +4) inside
                        // the template frame.
                        double w = Math.Min(36.0, roofImage.Size.Width);
                        double h = Math.Min(36.0, roofImage.Size.Height);
                        double ox = 4.0 + Math.Floor((36.0 - w) / 2.0);
                        double oy = 4.0 + Math.Floor((36.0 - h) / 2.0);
                        result.Add(new OriginalGameplayOverlay.Part(
                            roofImage,
                            new CGRect(rect.MinX + ox, rect.MinY + oy, w, h),
                            new Vector4(0f, 0f, (float)(w / roofImage.Size.Width), (float)(h / roofImage.Size.Height))));
                    }
                }
                else if (Kind == BuildCatalogPage.CatalogKind.Water && item.Guid is null)
                {
                    // Pool and water tool icons use 4-frame 45x45 button sheets indexed by selection and
                    // hover state.
                    var toolSheet = item.Id == 65535 ? _poolToolIcon : _waterToolIcon;
                    float uvX = chosen ? 0.25f : hovered ? 0.50f : 0f;
                    result.Add(new OriginalGameplayOverlay.Part(toolSheet, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
                }
                else
                {
                    float state = chosen ? 1f : hovered ? 2f : 0f;
                    result.Add(new OriginalGameplayOverlay.Part(ActiveFrame, rect, new Vector4(state / 4f, 0f, 0.25f, 1f)));
                    OriginalGameplayOverlay.Part buildThumbPart = SimsBitmapFont.SharpTextEnabled
                        && item.Guid is not null
                        && index < _previews.Count
                        && _previews[index] is { } hdBuildPreview
                        && item.Preview is not null
                        ? Icon(hdBuildPreview, item.Preview, _unknown)
                        : IconAt(index, item.Icon, highlighted: chosen || hovered);
                    result.Add(Fit(buildThumbPart, rect));
                }

                if (hovered && !ShowsRoofPanel && PriceTooltip(item.Price, budget is { } b && item.Price > b) is { } tip)
                {
                    hoveredPricePart = new OriginalGameplayOverlay.Part(
                        tip,
                        new CGRect(rect.MinX, rect.MaxY - tip.Size.Height, tip.Size.Width, tip.Size.Height));
                }
            }

            if (hoveredPricePart is { } pricePart)
                result.Add(pricePart);

            foreach (bool forward in new[] { false, true })
            {
                bool enabled = forward ? currentPage.NextOffset(capacity) is not null : currentPage.Offset > 0;
                var rect = ArrowRect(forward, canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float uvX = !enabled ? 0.75f : hovered ? 0.50f : 0f;
                result.Add(new OriginalGameplayOverlay.Part(forward ? _next : _previous, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
            }
        }

        return result;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0 && rect.Height > 0 &&
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
