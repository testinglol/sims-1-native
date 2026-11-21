using System.Globalization;
using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalBuyOverlay
{
    public abstract record Action
    {
        private protected Action() { }

        public sealed record Category(int Value) : Action;
        public sealed record FunctionCategory(int Index) : Action;
        public sealed record FunctionSubsort(int Index) : Action;
        public sealed record FunctionBack : Action
        {
            public static readonly FunctionBack Instance = new();
        }
        public sealed record Room(int Index) : Action;
        public sealed record SubSort(int Index) : Action;
        public sealed record Back : Action
        {
            public static readonly Back Instance = new();
        }
        public sealed record Page(int Offset) : Action;
        public sealed record Select(uint Guid) : Action;
        public sealed record PluginTab(string PackId, string TabId) : Action;
        public sealed record PluginCollection(string PackId, string TabId, string CollectionId) : Action;
        public sealed record ExitPluginView : Action
        {
            public static readonly ExitPluginView Instance = new();
        }
    }

    private readonly SimsBitmapFont _clockFont;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly OriginalGameplayOverlay.Image _universal;
    private readonly OriginalGameplayOverlay.Image _vectorUniversal;
    private OriginalGameplayOverlay.Image ActiveUniversal => SimsBitmapFont.SharpTextEnabled ? _vectorUniversal : _universal;
    private readonly OriginalGameplayOverlay.Image _patch;
    private readonly OriginalGameplayOverlay.Image[] _buttons;
    private readonly OriginalGameplayOverlay.Image[] _roomButtons;
    private readonly OriginalGameplayOverlay.Image[] _subSortButtons;
    private readonly OriginalGameplayOverlay.Image[] _backButtons;
    private readonly OriginalGameplayOverlay.Image[][] _functionSubSortButtons;
    private readonly OriginalGameplayOverlay.Image[] _functionBackButtons;
    private readonly OriginalUIStrings _strings;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly OriginalGameplayOverlay.Image _vectorFrame;
    private OriginalGameplayOverlay.Image ActiveFrame => SimsBitmapFont.SharpTextEnabled ? _vectorFrame : _frame;
    private readonly OriginalGameplayOverlay.Image _unknown;
    private readonly OriginalGameplayOverlay.Image _previous;
    private readonly OriginalGameplayOverlay.Image _next;
    private readonly OriginalGameplayOverlay.Image? _pluginCategoryIcon;

    private List<OriginalGameplayOverlay.Image?> _icons = [];
    private List<OriginalGameplayOverlay.Image?> _previews = [];
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _priceTips = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _pluginTextCache = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, OriginalGameplayOverlay.Image> _solidColorCache = new();
    private List<OriginalGameplayOverlay.Part> _footer = [];
    private string _statusKey = string.Empty;

    private IReadOnlyList<BuyCatalogPluginView> _pluginViews = [];
    private int _pluginScrollOffset;
    private const int PluginVisibleRows = 4;
    private const int MaxDisplayedPluginViews = 6;
    private int DisplayedPluginViewCount => Math.Min(_pluginViews.Count, MaxDisplayedPluginViews);

    public IReadOnlyList<BuyCatalogPluginView> PluginViews => _pluginViews;
    public BuyCatalogPluginView? ActivePluginView { get; private set; }
    public string? ActivePluginCollectionId { get; private set; }
    public bool PluginViewMode => ActivePluginView is not null;

    public UIDevice Device { get; }
    public SimsBitmapFont Font { get; }
    public CGPoint? HoveredPoint { get; private set; }
    public BuyCatalogPage? Page { get; private set; }
    public int Category { get; private set; }

    /// <summary>Toggles catalog sorting between room view and function view when activating buy mode while
    /// already in buy mode.</summary>
    public bool RoomMode { get; private set; }
    public int Room { get; private set; }

    /// <summary>Indicates whether the catalog is in room sub-sort mode, where category buttons filter by
    /// item function within the chosen room.</summary>
    public bool SubSorting { get; private set; }
    public int SubSort { get; private set; }
    public bool FunctionSubSorting { get; private set; }
    public int FunctionCategory { get; private set; }
    public int FunctionSubsort { get; private set; } = 7;

    public BuySelection Selection { get; } = new();

    public uint? Selected =>
        Selection.Target is { IsCatalog: true } target ? target.CatalogGuid : null;

    public int Direction => Selection.Direction;

    public BuyCatalogPage.Item? SelectedItem =>
        Selected is { } guid ? Page?.Items.FirstOrDefault(item => item.Guid == guid) : null;

    public OriginalBuyOverlay(GameContent content, SimsBitmapFont font, UIDevice device, OriginalUIStrings strings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(strings);

        Device = device;
        Font = font;
        _clockFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_08.ffn") ?? font;
        _tooltipFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_07.ffn") ?? font;
        _strings = strings;

        OriginalGameplayOverlay.Image LoadBitmap(string path)
        {
            var pixels = new SimsBitmap(content.Read("UIGraphics/cpanel/" + path));
            return OriginalGameplayOverlay.CreateImage(device, pixels.Width, pixels.Height, pixels.Rgba, magenta: true);
        }

        var targa = new SimsTarga(content.Read("UIGraphics/cpanel/Backgrounds/UniversalBack.tga"));
        _universal = OriginalGameplayOverlay.CreateImage(device, targa.Width, targa.Height, targa.Rgba);
        try { _vectorUniversal = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderFittedPanel("universalback", scale: 8)); } catch { _vectorUniversal = _universal; }
        _patch = LoadBitmap("Backgrounds/BuyPatch.bmp");

        OriginalGameplayOverlay.Image LoadCategoryButton(string path, string vectorKey)
        {
            var pixels = new SimsBitmap(content.Read("UIGraphics/cpanel/" + path));
            if (SimsBitmapFont.SharpTextEnabled)
            {
                int w1x = Math.Max(1, pixels.Width / 4);
                int h1x = Math.Max(1, pixels.Height);
                if (SimsVectorUiKit.TryRenderBuyCategoryStrip(vectorKey, w1x, h1x, scale: 4) is { } vec)
                    return SimsVectorUiKit.ToOverlayImage(vec);
            }
            return OriginalGameplayOverlay.CreateImage(device, pixels.Width, pixels.Height, pixels.Rgba, magenta: true);
        }

        _buttons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.Categories.Count];
        for (int i = 0; i < OriginalBuyLayout.Categories.Count; i++)
            _buttons[i] = LoadCategoryButton("Catalog/BuyF" + OriginalBuyLayout.Categories[i] + ".bmp", "BuyF" + OriginalBuyLayout.Categories[i]);

        _roomButtons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.RoomNames.Count];
        for (int i = 0; i < OriginalBuyLayout.RoomNames.Count; i++)
            _roomButtons[i] = LoadCategoryButton("Catalog/BuyR" + OriginalBuyLayout.RoomNames[i] + ".bmp", "BuyR" + OriginalBuyLayout.RoomNames[i]);

        _subSortButtons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.RoomSubSortNames.Count];
        for (int i = 0; i < OriginalBuyLayout.RoomSubSortNames.Count; i++)
            _subSortButtons[i] = LoadBitmap("Catalog/SubSortIcons/RoomSubsort/BuySubSortRoom" + OriginalBuyLayout.RoomSubSortNames[i] + ".bmp");

        _backButtons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.RoomNames.Count];
        for (int i = 0; i < OriginalBuyLayout.RoomNames.Count; i++)
            _backButtons[i] = LoadBitmap("Catalog/BackButtons/BackR" + OriginalBuyLayout.RoomNames[i] + ".bmp");

        var otherButton = LoadBitmap("Catalog/SubSortIcons/BuySubSortOther.bmp");
        var allButton = LoadBitmap("Catalog/SubSortIcons/BuySubSortAll.bmp");

        _functionBackButtons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.Categories.Count];
        for (int i = 0; i < OriginalBuyLayout.Categories.Count; i++)
            _functionBackButtons[i] = LoadBitmap("Catalog/BackButtons/BackF" + OriginalBuyLayout.Categories[i] + ".bmp");

        var functionSubSortGroups = new OriginalGameplayOverlay.Image[OriginalBuyLayout.Categories.Count][];
        string[] baseSortNames = ["One", "Two", "Three", "Four"];
        for (int index = 0; index < OriginalBuyLayout.Categories.Count; index++)
        {
            string name = OriginalBuyLayout.Categories[index];
            string folder = index == 7 ? "Miscellaneous" : name;
            var images = new OriginalGameplayOverlay.Image[8];
            for (int sortIdx = 0; sortIdx < baseSortNames.Length; sortIdx++)
                images[sortIdx] = LoadBitmap($"Catalog/SubSortIcons/{folder}/BuySubSort{baseSortNames[sortIdx]}.bmp");

            if (index == 7)
            {
                images[4] = LoadCategoryButton("Catalog/SubSortIcons/Miscellaneous/BuySubSortPets.bmp", "BuySubSortPets");
                images[5] = LoadBitmap("Catalog/SubSortIcons/Miscellaneous/BuySubSortMagic.bmp");
            }
            else
            {
                images[4] = images[0];
                images[5] = images[1];
            }

            images[6] = otherButton;
            images[7] = allButton;
            functionSubSortGroups[index] = images;
        }
        _functionSubSortButtons = functionSubSortGroups;

        _frame = LoadBitmap("Catalog/ThumbTemplate.bmp");
        _vectorFrame = SimsVectorUiKit.ToOverlayImage(SimsVectorUiKit.RenderThumbTemplateStrip(45, scale: 4));
        _unknown = LoadBitmap("CatalogUnknown.bmp");
        _previous = LoadBitmap("Buttons/ScrollLeftBuy.bmp");
        _next = LoadBitmap("Buttons/ScrollRight.bmp");
        try
        {
            if (SimsVectorUiKit.TryRenderBuyCategoryStrip(
                "BuyFFolders",
                (int)OriginalBuyLayout.PluginCategoryIconWidth,
                (int)OriginalBuyLayout.PluginCategoryIconHeight,
                scale: 4) is { } folderStrip)
            {
                _pluginCategoryIcon = SimsVectorUiKit.ToOverlayImage(folderStrip);
            }
        }
        catch
        {
            _pluginCategoryIcon = null;
        }
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

    public bool Choose(uint guid, int? budget = null)
    {
        _ = budget;
        if (Page?.Items.Any(item => item.Guid == guid) != true)
            return false;

        Selection.ChooseCatalog(guid);
        return true;
    }

    public void CancelSelection() => Selection.Cancel();

    public void Rotate(int steps = 1) => Selection.Rotate(steps);

    public void ToggleMode()
    {
        RoomMode = !RoomMode;
        SubSorting = false;
        FunctionSubSorting = false;
        Category = RoomMode ? -1 : OriginalBuyLayout.EngineCategories[0];
        Room = 0;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void ClearRoom(int room)
    {
        FunctionSubSorting = false;
        Room = room;
        SubSorting = true;
        SubSort = 0;
        Category = OriginalBuyLayout.EngineCategories[0];
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void ClearSubSort(int subSort)
    {
        SubSort = subSort;
        Category = OriginalBuyLayout.EngineCategories[subSort];
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void LeaveSubSort()
    {
        SubSorting = false;
        Category = -1;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void ClearCategory(int category)
    {
        RoomMode = false;
        SubSorting = false;
        FunctionSubSorting = false;
        Category = category;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void ClearFunctionCategory(int index)
    {
        RoomMode = false;
        SubSorting = false;
        FunctionSubSorting = true;
        FunctionCategory = index;
        Category = OriginalBuyLayout.EngineCategories[index];
        FunctionSubsort = 7;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void ClearFunctionSubsort(int index)
    {
        FunctionSubsort = index;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void LeaveFunctionSubsort()
    {
        FunctionSubSorting = false;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void SetPluginViews(IReadOnlyList<BuyCatalogPluginView> views)
    {
        _pluginViews = views ?? [];
        if (ActivePluginView is not null)
        {
            var refreshed = _pluginViews.FirstOrDefault(v =>
                string.Equals(v.PackId, ActivePluginView.PackId, StringComparison.Ordinal)
                && string.Equals(v.TabId, ActivePluginView.TabId, StringComparison.Ordinal));
            if (refreshed is null)
            {
                ExitPluginView();
            }
            else
            {
                ActivePluginView = refreshed;
                if (ActivePluginCollectionId is null
                    || !refreshed.Collections.Any(c => string.Equals(c.Id, ActivePluginCollectionId, StringComparison.Ordinal)))
                {
                    ActivePluginCollectionId = refreshed.Collections.FirstOrDefault()?.Id;
                }
                int maxScroll = Math.Max(0, refreshed.Collections.Count - PluginVisibleRows);
                _pluginScrollOffset = Math.Clamp(_pluginScrollOffset, 0, maxScroll);
            }
        }
    }

    public bool EnterPluginView(string packId, string tabId)
    {
        var view = _pluginViews.FirstOrDefault(v =>
            string.Equals(v.PackId, packId, StringComparison.Ordinal)
            && string.Equals(v.TabId, tabId, StringComparison.Ordinal));
        if (view is null)
            return false;

        ActivePluginView = view;
        ActivePluginCollectionId = view.Collections.FirstOrDefault()?.Id;
        _pluginScrollOffset = 0;
        RoomMode = false;
        SubSorting = false;
        FunctionSubSorting = false;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
        return true;
    }

    public bool SelectPluginCollection(string packId, string tabId, string collectionId)
    {
        var view = _pluginViews.FirstOrDefault(v =>
            string.Equals(v.PackId, packId, StringComparison.Ordinal)
            && string.Equals(v.TabId, tabId, StringComparison.Ordinal));
        if (view is null || !view.Collections.Any(c => string.Equals(c.Id, collectionId, StringComparison.Ordinal)))
            return false;

        ActivePluginView = view;
        ActivePluginCollectionId = collectionId;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
        return true;
    }

    public void ExitPluginView()
    {
        ActivePluginView = null;
        ActivePluginCollectionId = null;
        _pluginScrollOffset = 0;
        Page = null;
        _icons = [];
        _previews = [];
        Selection.Cancel();
    }

    public void Update(BuyCatalogPage page, string root, bool buildObjects = false, bool pluginCollection = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(root);

        page.Validate(buildObjects, pluginCollection);
        string resolvedRoot = PackContentPaths.ResolveSymlinksInPath(Path.GetFullPath(root));
        var icons = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);
        var previews = new List<OriginalGameplayOverlay.Image?>(page.Items.Count);

        foreach (var item in page.Items)
        {
            icons.Add(LoadThumbnail(item.Icon, resolvedRoot));
            previews.Add(LoadThumbnail(item.Preview, resolvedRoot));
        }

        Page = page;
        Category = page.Category;
        if (page.Room is { } pageRoom)
            Room = pageRoom;
        if (page.Subsort is { } pageSubsort)
            FunctionSubsort = pageSubsort;
        _icons = icons;
        _previews = previews;

        if (Selected is { } selGuid && !page.Items.Any(item => item.Guid == selGuid))
            Selection.Cancel();
    }

    private OriginalGameplayOverlay.Image? LoadThumbnail(GameplayPanelState.Image? image, string resolvedRoot)
    {
        if (image is null)
            return null;

        string? safeFile = ResolveRootFile(resolvedRoot, image.File);
        if (safeFile is null)
            throw new ContentError("Catalog thumbnail escapes scene directory or exceeds size limit");

        var fileInfo = new FileInfo(safeFile);
        if (!fileInfo.Exists || fileInfo.Length > 4L * 1024 * 1024)
            throw new ContentError("Catalog thumbnail escapes scene directory or exceeds size limit");

        if (image.Columns <= 0 || image.Rows <= 0)
            throw new ContentError("Invalid catalog thumbnail dimensions");

        var bitmap = new SimsBitmap(File.ReadAllBytes(safeFile));
        if (bitmap.Width <= 0 || bitmap.Height <= 0
            || bitmap.Width > 512 || bitmap.Height > 512
            || bitmap.Width % image.Columns != 0 || bitmap.Height % image.Rows != 0)
        {
            throw new ContentError("Invalid catalog thumbnail dimensions");
        }

        return OriginalGameplayOverlay.CreateImage(
            Device,
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

    public void UpdateStatus(GameplayPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        int hour = state.Hours % 12 == 0 ? 12 : state.Hours % 12;
        string meridiem = _strings.PanelTip(state.Hours < 12 ? 21 : 22, state.Hours < 12 ? "AM" : "PM");
        string clock = string.Create(CultureInfo.InvariantCulture, $"{hour}:{state.Minutes:D2} {meridiem}");
        string money = state.Budget is { } budget ? "§" + OriginalGameplayOverlay.Grouped(budget) : string.Empty;
        string key = clock + money;
        if (string.Equals(key, _statusKey, StringComparison.Ordinal))
            return;

        var parts = new List<OriginalGameplayOverlay.Part>(2);
        // Renders the control panel clock at y=85 in 8pt font and household funds at y=125 in 10pt font.
        (string Value, SimsBitmapFont ItemFont, double X, double Y)[] fields =
        [
            (clock, _clockFont, 123, 85),
            (money, Font, 178, 125)
        ];

        foreach (var (value, itemFont, x, y) in fields)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            var image = OriginalGameplayOverlay.Text(value, itemFont, Device, 0x29, 0xe6, 0x5e);
            parts.Add(new OriginalGameplayOverlay.Part(
                image,
                new CGRect(x - image.Size.Width / (y == 85 ? 2.0 : 1.0), y, image.Size.Width, image.Size.Height)));
        }

        _footer = parts;
        _statusKey = key;
    }

    public void ClearTextCaches()
    {
        _priceTips.Clear();
        _pluginTextCache.Clear();
        _statusKey = string.Empty;
        _footer.Clear();
    }

    private CGRect PluginCategoryRect(int index, CGSize canvas)
    {
        var pos = OriginalBuyLayout.PluginCategoryPosition(index);
        return new CGRect(
            pos.X,
            pos.Y + (canvas.Height - 150),
            OriginalBuyLayout.PluginCategoryIconWidth,
            OriginalBuyLayout.PluginCategoryIconHeight);
    }

    private CGRect PluginBackRect(CGSize canvas)
    {
        double top = canvas.Height - 150;
        return new CGRect(222, top + 52, 22, 92);
    }

    private CGRect PluginListRect(CGSize canvas)
    {
        double top = canvas.Height - 150;
        double rightEdge = 392.0 + OriginalBuyLayout.ExtraLeftWidth(DisplayedPluginViewCount);
        double leftEdge = 247.0;
        return new CGRect(leftEdge, top + 52, Math.Max(120.0, rightEdge - leftEdge), 92);
    }

    private CGRect PluginRowRect(int visibleIndex, CGSize canvas, bool hasScrollbar)
    {
        var list = PluginListRect(canvas);
        double rowW = hasScrollbar ? list.Width - 18 : list.Width - 4;
        return new CGRect(list.X + 2, list.Y + 2 + visibleIndex * 22.5, rowW, 21.5);
    }

    private CGRect PluginScrollButtonRect(bool up, CGSize canvas)
    {
        var list = PluginListRect(canvas);
        return new CGRect(list.MaxX - 16, up ? list.Y + 2 : list.MaxY - 18, 14, 16);
    }

    private OriginalGameplayOverlay.Image SolidImage(byte r, byte g, byte b, byte a = 255)
    {
        uint key = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
        if (_solidColorCache.TryGetValue(key, out var cached))
            return cached;
        var img = OriginalGameplayOverlay.CreateImage(Device, 1, 1, [r, g, b, a]);
        _solidColorCache[key] = img;
        return img;
    }

    private OriginalGameplayOverlay.Image? PluginLabelImage(string text, byte r, byte g, byte b)
    {
        string key = $"{r}:{g}:{b}:{text}";
        if (_pluginTextCache.TryGetValue(key, out var cached))
            return cached;
        try
        {
            var img = OriginalGameplayOverlay.Text(text, _tooltipFont, Device, r, g, b);
            _pluginTextCache[key] = img;
            return img;
        }
        catch
        {
            return null;
        }
    }

    public List<OriginalGameplayOverlay.Part> StatusParts(CGSize canvas)
    {
        var result = new List<OriginalGameplayOverlay.Part>(_footer.Count);
        double dy = canvas.Height - 150;
        foreach (var part in _footer)
            result.Add(new OriginalGameplayOverlay.Part(part.Image, part.Rect.OffsetBy(0, dy), part.UV));
        return result;
    }

    private CGRect CategoryRect(int index, CGSize canvas)
    {
        var image = FunctionSubSorting
            ? _functionSubSortButtons[FunctionCategory][index]
            : SubSorting
                ? _subSortButtons[index]
                : RoomMode
                    ? _roomButtons[index]
                    : _buttons[index];
        var positions = FunctionSubSorting
            ? OriginalBuyLayout.FunctionSubsortPositions
            : SubSorting
                ? OriginalBuyLayout.RoomSubSortPositions
                : RoomMode
                    ? OriginalBuyLayout.RoomPositions
                    : OriginalBuyLayout.Positions;
        return new CGRect(positions[index], new CGSize(image.Size.Width / 4.0, image.Size.Height))
            .OffsetBy(0, canvas.Height - 150);
    }

    private CGRect BackRect(CGSize canvas)
    {
        var image = FunctionSubSorting ? _functionBackButtons[FunctionCategory] : _backButtons[Room];
        return new CGRect(OriginalBuyLayout.RoomBackPosition, new CGSize(image.Size.Width / 4.0, image.Size.Height))
            .OffsetBy(0, canvas.Height - 150);
    }

    private bool SubSortEnabled(int index)
    {
        // Disables function sub-sort buttons when no items in the active room belong to that category.
        if (Page?.FunctionCounts is not { Count: 8 } counts)
            return true;
        return counts[OriginalBuyLayout.EngineCategories[index]] > 0;
    }

    private bool FunctionSubsortVisible(int index) => index is >= 0 and <= 3 || index >= 6 || FunctionCategory == 7;

    private bool FunctionSubsortEnabled(int index)
    {
        if (Page?.FunctionSubsortCounts is not { Count: 8 } counts)
            return true;
        return counts[index] > 0;
    }

    private CGRect ArrowRect(bool forward, CGSize canvas)
    {
        var image = forward ? _next : _previous;
        return OriginalBuyLayout.Arrow(forward, new CGSize(image.Size.Width / 4.0, image.Size.Height), canvas, DisplayedPluginViewCount);
    }

    public (string Text, CGRect Rect)? ToolTipAt(CGPoint point, CGSize canvas)
    {
        int pluginCount = DisplayedPluginViewCount;
        if (ActivePluginView is { } activeView)
        {
            var backRect = PluginBackRect(canvas);
            if (Contains(backRect, point))
                return (_strings.CatalogBackTip("Back"), backRect);

            bool hasScroll = activeView.Collections.Count > PluginVisibleRows;
            int visibleCount = Math.Min(PluginVisibleRows, Math.Max(0, activeView.Collections.Count - _pluginScrollOffset));
            for (int r = 0; r < visibleCount; r++)
            {
                var rowRect = PluginRowRect(r, canvas, hasScroll);
                if (Contains(rowRect, point))
                {
                    var col = activeView.Collections[_pluginScrollOffset + r];
                    return (col.Tooltip ?? $"{col.Label} ({col.Count})", rowRect);
                }
            }
            if (Page is { } pluginPage)
            {
                var prevRect = ArrowRect(false, canvas);
                if (pluginPage.Offset > 0 && Contains(prevRect, point))
                    return (_strings.PeoplePageTip(0, "Previous catalog page"), prevRect);
                var nextRect = ArrowRect(true, canvas);
                if (pluginPage.NextOffset(OriginalBuyLayout.Capacity(canvas, pluginCount)) is not null && Contains(nextRect, point))
                    return (_strings.PeoplePageTip(1, "Next catalog page"), nextRect);
            }
            return null;
        }

        for (int vi = 0; vi < pluginCount; vi++)
        {
            var catRect = PluginCategoryRect(vi, canvas);
            if (Contains(catRect, point))
            {
                var v = _pluginViews[vi];
                string tip = string.IsNullOrWhiteSpace(v.Hotkey)
                    ? v.TabLabel
                    : $"{v.TabLabel} ({v.Hotkey.ToUpperInvariant()})";
                return (tip, catRect);
            }
        }

        if (SubSorting || FunctionSubSorting)
        {
            var backRect = BackRect(canvas);
            if (Contains(backRect, point))
                return (_strings.CatalogBackTip("Back"), backRect);
        }

        var names = FunctionSubSorting
            ? OriginalBuyLayout.FunctionSubsortNames
            : SubSorting
                ? OriginalBuyLayout.RoomSubSortNames
                : RoomMode
                    ? OriginalBuyLayout.RoomNames
                    : OriginalBuyLayout.Categories;

        for (int index = 0; index < 8; index++)
        {
            if (FunctionSubSorting && !FunctionSubsortVisible(index))
                continue;

            var rect = CategoryRect(index, canvas);
            if (!Contains(rect, point))
                continue;

            if (FunctionSubSorting && !FunctionSubsortEnabled(index))
                return null;
            if (SubSorting && !SubSortEnabled(index))
                return null;

            string fallback = names[index];
            string label = FunctionSubSorting
                ? _strings.CatalogFunctionSubsortTip(FunctionCategory, index, fallback)
                : _strings.CatalogCategoryTip(index, RoomMode && !SubSorting, fallback);
            return (label, rect);
        }

        if (Page is { } currentPage)
        {
            var prevRect = ArrowRect(false, canvas);
            if (currentPage.Offset > 0 && Contains(prevRect, point))
                return (_strings.PeoplePageTip(0, "Previous catalog page"), prevRect);

            var nextRect = ArrowRect(true, canvas);
            if (currentPage.NextOffset(OriginalBuyLayout.Capacity(canvas, pluginCount)) is not null && Contains(nextRect, point))
                return (_strings.PeoplePageTip(1, "Next catalog page"), nextRect);
        }

        return null;
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        int pluginCount = DisplayedPluginViewCount;
        var names = FunctionSubSorting
            ? OriginalBuyLayout.FunctionSubsortNames
            : SubSorting
                ? OriginalBuyLayout.RoomSubSortNames
                : RoomMode
                    ? OriginalBuyLayout.RoomNames
                    : OriginalBuyLayout.Categories;

        var controls = new List<(string Label, CGRect Rect)>();
        if (ActivePluginView is not null)
        {
            controls.Add(("Back", PluginBackRect(canvas)));
        }
        else
        {
            for (int index = 0; index < 8; index++)
            {
                if (FunctionSubSorting && !FunctionSubsortVisible(index))
                    continue;

                var rect = CategoryRect(index, canvas);
                string fallback = names[index];
                string tip = FunctionSubSorting
                    ? _strings.CatalogFunctionSubsortTip(FunctionCategory, index, fallback)
                    : _strings.CatalogCategoryTip(index, RoomMode && !SubSorting, fallback);
                string prefixed = "Buy: " + fallback;

                controls.Add((fallback, rect));
                if (!string.Equals(tip, fallback, StringComparison.Ordinal))
                    controls.Add((tip, rect));
                controls.Add((prefixed, rect));
            }

            for (int vi = 0; vi < pluginCount; vi++)
            {
                var v = _pluginViews[vi];
                var rect = PluginCategoryRect(vi, canvas);
                controls.Add((v.TabLabel, rect));
                controls.Add(("Buy: " + v.TabLabel, rect));
            }
        }

        if (ActivePluginView is null && (SubSorting || FunctionSubSorting))
        {
            string backFallback = FunctionSubSorting ? "Back to function categories" : "Back to rooms";
            string backTip = _strings.CatalogBackTip(backFallback);
            var rect = BackRect(canvas);
            controls.Add((backFallback, rect));
            if (!string.Equals(backTip, backFallback, StringComparison.Ordinal))
                controls.Add((backTip, rect));
        }

        if (Page is { } currentPage)
        {
            int capacity = OriginalBuyLayout.Capacity(canvas, pluginCount);
            int count = Math.Min(currentPage.Items.Count, capacity);
            for (int index = 0; index < count; index++)
            {
                var item = currentPage.Items[index];
                controls.Add((item.Name + " — §" + item.Price, OriginalBuyLayout.Thumbnail(index, canvas, pluginCount)));
            }

            if (currentPage.Offset > 0)
                controls.Add(("Previous catalog page", ArrowRect(false, canvas)));
            if (currentPage.NextOffset(capacity) is not null)
                controls.Add(("Next catalog page", ArrowRect(true, canvas)));
        }

        return controls;
    }

    public Action? ActionAt(CGPoint point, CGSize canvas)
    {
        int pluginCount = DisplayedPluginViewCount;
        if (ActivePluginView is { } activeView)
        {
            if (Contains(PluginBackRect(canvas), point))
                return Action.ExitPluginView.Instance;

            bool hasScroll = activeView.Collections.Count > PluginVisibleRows;
            if (hasScroll)
            {
                if (Contains(PluginScrollButtonRect(up: true, canvas), point))
                {
                    _pluginScrollOffset = Math.Max(0, _pluginScrollOffset - 1);
                    return null;
                }
                if (Contains(PluginScrollButtonRect(up: false, canvas), point))
                {
                    int maxScroll = Math.Max(0, activeView.Collections.Count - PluginVisibleRows);
                    _pluginScrollOffset = Math.Min(maxScroll, _pluginScrollOffset + 1);
                    return null;
                }
            }

            int visibleCount = Math.Min(PluginVisibleRows, Math.Max(0, activeView.Collections.Count - _pluginScrollOffset));
            for (int r = 0; r < visibleCount; r++)
            {
                if (Contains(PluginRowRect(r, canvas, hasScroll), point))
                {
                    var col = activeView.Collections[_pluginScrollOffset + r];
                    return new Action.PluginCollection(activeView.PackId, activeView.TabId, col.Id);
                }
            }

            if (Page is not { } pluginPage)
                return null;

            int pluginCap = OriginalBuyLayout.Capacity(canvas, pluginCount);
            if (pluginPage.Offset > 0 && Contains(ArrowRect(false, canvas), point))
                return new Action.Page(Math.Max(0, pluginPage.Offset - pluginCap));
            if (pluginPage.NextOffset(pluginCap) is { } pluginNext && Contains(ArrowRect(true, canvas), point))
                return new Action.Page(pluginNext);

            int pluginItemCount = Math.Min(pluginPage.Items.Count, pluginCap);
            for (int index = 0; index < pluginItemCount; index++)
            {
                if (Contains(OriginalBuyLayout.Thumbnail(index, canvas, pluginCount), point))
                    return new Action.Select(pluginPage.Items[index].Guid);
            }
            return null;
        }

        for (int vi = 0; vi < pluginCount; vi++)
        {
            if (Contains(PluginCategoryRect(vi, canvas), point))
            {
                var v = _pluginViews[vi];
                return new Action.PluginTab(v.PackId, v.TabId);
            }
        }

        if ((SubSorting || FunctionSubSorting) && Contains(BackRect(canvas), point))
            return FunctionSubSorting ? Action.FunctionBack.Instance : Action.Back.Instance;

        for (int index = 0; index < 8; index++)
        {
            if (!Contains(CategoryRect(index, canvas), point))
                continue;

            if (FunctionSubSorting)
                return FunctionSubsortVisible(index) && FunctionSubsortEnabled(index) ? new Action.FunctionSubsort(index) : null;
            if (SubSorting)
                return SubSortEnabled(index) ? new Action.SubSort(index) : null;
            return RoomMode ? new Action.Room(index) : new Action.FunctionCategory(index);
        }

        if (Page is not { } currentPage)
            return null;

        int capacity = OriginalBuyLayout.Capacity(canvas, pluginCount);
        if (currentPage.Offset > 0 && Contains(ArrowRect(false, canvas), point))
            return new Action.Page(Math.Max(0, currentPage.Offset - capacity));
        if (currentPage.NextOffset(capacity) is { } nextOffset && Contains(ArrowRect(true, canvas), point))
            return new Action.Page(nextOffset);

        int count = Math.Min(currentPage.Items.Count, capacity);
        for (int index = 0; index < count; index++)
        {
            if (Contains(OriginalBuyLayout.Thumbnail(index, canvas, pluginCount), point))
                return new Action.Select(currentPage.Items[index].Guid);
        }

        return null;
    }

    private OriginalGameplayOverlay.Part IconAt(int index, GameplayPanelState.Image? iconSpec, bool highlighted = false) =>
        OriginalBuildOverlay.Icon(index >= 0 && index < _icons.Count ? _icons[index] : null, iconSpec, _unknown, highlighted);

    public void Hover(CGPoint? point) => HoveredPoint = point;

    public void ClearHover() => HoveredPoint = null;

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
            var image = OriginalGameplayOverlay.CreateImage(Device, PlacementTooltip.RasterizeBoxHiDpi(label, _tooltipFont, color));
            _priceTips[key] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public (OriginalCatalogPopup.Item Item, OriginalGameplayOverlay.Part Icon, double AnchorMidX)? PopupAt(CGPoint point, CGSize canvas)
    {
        if (Page is not { } currentPage)
            return null;

        int pluginCount = DisplayedPluginViewCount;
        int capacity = OriginalBuyLayout.Capacity(canvas, pluginCount);
        int count = Math.Min(currentPage.Items.Count, capacity);
        for (int index = 0; index < count; index++)
        {
            var thumbRect = OriginalBuyLayout.Thumbnail(index, canvas, pluginCount);
            if (!Contains(thumbRect, point))
                continue;

            var item = currentPage.Items[index];
            var preview = index < _previews.Count ? _previews[index] : null;
            var icon = index < _icons.Count ? _icons[index] : null;
            return (
                new OriginalCatalogPopup.Item(item.Name, item.Price, item.Description, item.Ratings ?? []),
                OriginalBuildOverlay.Icon(preview ?? icon, preview is null ? item.Icon : item.Preview, _unknown),
                thumbRect.MidX);
        }

        return null;
    }

    public List<OriginalGameplayOverlay.Part> Parts(CGSize canvas, int? budget = null)
    {
        double top = canvas.Height - 150;
        var parts = new List<OriginalGameplayOverlay.Part>
        {
            new(ActiveUniversal, new CGRect(0, top - 33, _universal.Size.Width, _universal.Size.Height)),
            new(_patch, new CGRect(64, top - 33 + 35, _patch.Size.Width, _patch.Size.Height))
        };

        int pluginCount = DisplayedPluginViewCount;
        if (ActivePluginView is { } activePluginView)
        {
            var backRect = PluginBackRect(canvas);
            bool backHovered = HoveredPoint is { } bhp && Contains(backRect, bhp);
            parts.Add(new OriginalGameplayOverlay.Part(
                backHovered ? SolidImage(115, 215, 255, 245) : SolidImage(72, 118, 198, 242),
                backRect));
            parts.Add(new OriginalGameplayOverlay.Part(
                backHovered ? SolidImage(36, 98, 172, 245) : SolidImage(16, 36, 92, 240),
                new CGRect(backRect.X + 1, backRect.Y + 1, backRect.Width - 2, backRect.Height - 2)));
            if (PluginLabelImage("<", backHovered ? (byte)255 : (byte)215, backHovered ? (byte)255 : (byte)235, 255) is { } backImg)
            {
                double bx = backRect.X + Math.Max(1, (backRect.Width - backImg.Size.Width) * 0.5);
                double by = backRect.Y + Math.Max(1, (backRect.Height - backImg.Size.Height) * 0.5);
                parts.Add(new OriginalGameplayOverlay.Part(backImg, new CGRect(bx, by, backImg.Size.Width, backImg.Size.Height)));
            }

            var listRect = PluginListRect(canvas);
            parts.Add(new OriginalGameplayOverlay.Part(SolidImage(72, 118, 198, 242), listRect));
            parts.Add(new OriginalGameplayOverlay.Part(SolidImage(10, 20, 62, 245), new CGRect(listRect.X + 1, listRect.Y + 1, listRect.Width - 2, listRect.Height - 2)));

            bool hasScroll = activePluginView.Collections.Count > PluginVisibleRows;
            if (activePluginView.Collections.Count == 0)
            {
                if (PluginLabelImage("No CC folders found", 165, 185, 220) is { } emptyImg)
                {
                    parts.Add(new OriginalGameplayOverlay.Part(emptyImg, new CGRect(listRect.X + 8, listRect.Y + 38, emptyImg.Size.Width, emptyImg.Size.Height)));
                }
            }
            else
            {
                int visibleCount = Math.Min(PluginVisibleRows, Math.Max(0, activePluginView.Collections.Count - _pluginScrollOffset));
                for (int r = 0; r < visibleCount; r++)
                {
                    var col = activePluginView.Collections[_pluginScrollOffset + r];
                    var rowRect = PluginRowRect(r, canvas, hasScroll);
                    bool selected = string.Equals(col.Id, ActivePluginCollectionId, StringComparison.Ordinal);
                    bool hovered = HoveredPoint is { } hp && Contains(rowRect, hp);
                    var rowImg = selected
                        ? SolidImage(36, 118, 184, 242)
                        : hovered
                            ? SolidImage(28, 62, 134, 230)
                            : SolidImage(16, 30, 82, 218);
                    parts.Add(new OriginalGameplayOverlay.Part(rowImg, rowRect));

                    string label = col.Label.Length > 17 ? col.Label[..16] + "…" : col.Label;
                    string countText = $"({col.Count})";
                    if (PluginLabelImage(label, selected ? (byte)255 : (byte)215, selected ? (byte)255 : (byte)230, 255) is { } nameImg)
                    {
                        double ny = rowRect.Y + Math.Max(1, (rowRect.Height - nameImg.Size.Height) * 0.5);
                        parts.Add(new OriginalGameplayOverlay.Part(nameImg, new CGRect(rowRect.X + 5, ny, Math.Min(nameImg.Size.Width, rowRect.Width - 36), nameImg.Size.Height)));
                    }
                    if (PluginLabelImage(countText, selected ? (byte)160 : (byte)150, selected ? (byte)245 : (byte)190, 240) is { } cntImg)
                    {
                        double cy = rowRect.Y + Math.Max(1, (rowRect.Height - cntImg.Size.Height) * 0.5);
                        parts.Add(new OriginalGameplayOverlay.Part(cntImg, new CGRect(rowRect.MaxX - cntImg.Size.Width - 4, cy, cntImg.Size.Width, cntImg.Size.Height)));
                    }
                }

                if (hasScroll)
                {
                    int maxScroll = Math.Max(0, activePluginView.Collections.Count - PluginVisibleRows);
                    foreach (bool up in new[] { true, false })
                    {
                        var btnRect = PluginScrollButtonRect(up, canvas);
                        bool enabled = up ? _pluginScrollOffset > 0 : _pluginScrollOffset < maxScroll;
                        bool hovered = enabled && HoveredPoint is { } hp && Contains(btnRect, hp);
                        var btnImg = !enabled
                            ? SolidImage(20, 30, 62, 180)
                            : hovered
                                ? SolidImage(52, 112, 194, 242)
                                : SolidImage(28, 62, 128, 235);
                        parts.Add(new OriginalGameplayOverlay.Part(btnImg, btnRect));
                        if (PluginLabelImage(up ? "^" : "v", enabled ? (byte)235 : (byte)110, enabled ? (byte)245 : (byte)125, enabled ? (byte)255 : (byte)150) is { } arrImg)
                        {
                            double ax = btnRect.X + Math.Max(1, (btnRect.Width - arrImg.Size.Width) * 0.5);
                            double ay = btnRect.Y + Math.Max(0, (btnRect.Height - arrImg.Size.Height) * 0.5);
                            parts.Add(new OriginalGameplayOverlay.Part(arrImg, new CGRect(ax, ay, arrImg.Size.Width, arrImg.Size.Height)));
                        }
                    }
                }
            }
        }
        else
        {
            var displayedButtons = FunctionSubSorting
                ? _functionSubSortButtons[FunctionCategory]
                : SubSorting
                    ? _subSortButtons
                    : RoomMode
                        ? _roomButtons
                        : _buttons;

            for (int index = 0; index < displayedButtons.Length; index++)
            {
                if (FunctionSubSorting && !FunctionSubsortVisible(index))
                    continue;

                var rect = CategoryRect(index, canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float frame = FunctionSubSorting
                    ? (!FunctionSubsortEnabled(index) ? 0.75f : index == FunctionSubsort ? 0.25f : hovered ? 0.50f : 0f)
                    : SubSorting
                        ? (!SubSortEnabled(index) ? 0.75f : index == SubSort ? 0.25f : hovered ? 0.50f : 0f)
                        : (RoomMode ? index == Room && Page is not null : OriginalBuyLayout.EngineCategories[index] == Category)
                            ? 0.25f
                            : hovered ? 0.50f : 0f;

                parts.Add(new OriginalGameplayOverlay.Part(displayedButtons[index], rect, new Vector4(frame, 0f, 0.25f, 1f)));
            }

            for (int vi = 0; vi < pluginCount; vi++)
            {
                var rect = PluginCategoryRect(vi, canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                if (_pluginCategoryIcon is { } folderIcon)
                {
                    float frame = hovered ? 0.50f : 0f;
                    parts.Add(new OriginalGameplayOverlay.Part(folderIcon, rect, new Vector4(frame, 0f, 0.25f, 1f)));
                }
                else
                {
                    parts.Add(new OriginalGameplayOverlay.Part(
                        hovered ? SolidImage(90, 175, 240, 240) : SolidImage(55, 105, 175, 220),
                        rect));
                }
            }

            if (SubSorting || FunctionSubSorting)
            {
                var back = FunctionSubSorting ? _functionBackButtons[FunctionCategory] : _backButtons[Room];
                var rect = BackRect(canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                parts.Add(new OriginalGameplayOverlay.Part(back, rect, new Vector4(hovered ? 0.50f : 0f, 0f, 0.25f, 1f)));
            }
        }

        if (Page is { } currentPage)
        {
            OriginalGameplayOverlay.Part? hoveredPricePart = null;
            int capacity = OriginalBuyLayout.Capacity(canvas, pluginCount);
            int count = Math.Min(currentPage.Items.Count, capacity);

            for (int index = 0; index < count; index++)
            {
                var item = currentPage.Items[index];
                var rect = OriginalBuyLayout.Thumbnail(index, canvas, pluginCount);
                bool chosen = Selected is { } selGuid && item.Guid == selGuid;
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float state = chosen ? 1f : hovered ? 2f : 0f;
                parts.Add(new OriginalGameplayOverlay.Part(ActiveFrame, rect, new Vector4(state / 4f, 0f, 0.25f, 1f)));
                OriginalGameplayOverlay.Part thumbPart = SimsBitmapFont.SharpTextEnabled
                    && index < _previews.Count
                    && _previews[index] is { } hdPreview
                    && item.Preview is not null
                    ? OriginalBuildOverlay.Icon(hdPreview, item.Preview, _unknown)
                    : IconAt(index, item.Icon, highlighted: chosen || hovered);
                parts.Add(OriginalBuildOverlay.Fit(thumbPart, rect));

                if (hovered && PriceTooltip(item.Price, budget is { } b && item.Price > b) is { } tip)
                {
                    // Positions the price tooltip at the bottom-left corner of the hovered catalog cell.
                    hoveredPricePart = new OriginalGameplayOverlay.Part(
                        tip,
                        new CGRect(rect.MinX, rect.MaxY - tip.Size.Height, tip.Size.Width, tip.Size.Height));
                }
            }

            if (hoveredPricePart is { } pricePart)
                parts.Add(pricePart);

            foreach (bool forward in new[] { false, true })
            {
                bool enabled = forward ? currentPage.NextOffset(capacity) is not null : currentPage.Offset > 0;
                var rect = ArrowRect(forward, canvas);
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float uvX = !enabled ? 0.75f : hovered ? 0.50f : 0f;
                parts.Add(new OriginalGameplayOverlay.Part(forward ? _next : _previous, rect, new Vector4(uvX, 0f, 0.25f, 1f)));
            }
        }

        return parts;
    }

    private static bool Contains(CGRect rect, CGPoint point) =>
        rect.Width > 0 && rect.Height > 0 &&
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
}
