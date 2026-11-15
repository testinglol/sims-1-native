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
    }

    private readonly SimsBitmapFont _clockFont;
    private readonly SimsBitmapFont _tooltipFont;
    private readonly OriginalGameplayOverlay.Image _universal;
    private readonly OriginalGameplayOverlay.Image _patch;
    private readonly OriginalGameplayOverlay.Image[] _buttons;
    private readonly OriginalGameplayOverlay.Image[] _roomButtons;
    private readonly OriginalGameplayOverlay.Image[] _subSortButtons;
    private readonly OriginalGameplayOverlay.Image[] _backButtons;
    private readonly OriginalGameplayOverlay.Image[][] _functionSubSortButtons;
    private readonly OriginalGameplayOverlay.Image[] _functionBackButtons;
    private readonly OriginalUIStrings _strings;
    private readonly OriginalGameplayOverlay.Image _frame;
    private readonly OriginalGameplayOverlay.Image _unknown;
    private readonly OriginalGameplayOverlay.Image _previous;
    private readonly OriginalGameplayOverlay.Image _next;

    private List<OriginalGameplayOverlay.Image?> _icons = [];
    private List<OriginalGameplayOverlay.Image?> _previews = [];
    private readonly Dictionary<string, OriginalGameplayOverlay.Image> _priceTips = new(StringComparer.Ordinal);
    private List<OriginalGameplayOverlay.Part> _footer = [];
    private string _statusKey = string.Empty;

    public UIDevice Device { get; }
    public SimsBitmapFont Font { get; }
    public CGPoint? HoveredPoint { get; private set; }
    public BuyCatalogPage? Page { get; private set; }
    public int Category { get; private set; }

    /// <summary>FUN_0053a478 flips the sort type between 0 (room) and 1 (function) when the Buy button is clicked again in buy mode.</summary>
    public bool RoomMode { get; private set; }
    public int Room { get; private set; }

    /// <summary>FUN_005063ab: clicking a room button enters its sub-sort level (+0xa4 = 1), where the same eight buttons pick the function and a back button returns.</summary>
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
        _patch = LoadBitmap("Backgrounds/BuyPatch.bmp");

        _buttons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.Categories.Count];
        for (int i = 0; i < OriginalBuyLayout.Categories.Count; i++)
            _buttons[i] = LoadBitmap("Catalog/BuyF" + OriginalBuyLayout.Categories[i] + ".bmp");

        _roomButtons = new OriginalGameplayOverlay.Image[OriginalBuyLayout.RoomNames.Count];
        for (int i = 0; i < OriginalBuyLayout.RoomNames.Count; i++)
            _roomButtons[i] = LoadBitmap("Catalog/BuyR" + OriginalBuyLayout.RoomNames[i] + ".bmp");

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
                images[4] = LoadBitmap("Catalog/SubSortIcons/Miscellaneous/BuySubSortPets.bmp");
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
        _unknown = LoadBitmap("CatalogUnknown.bmp");
        _previous = LoadBitmap("Buttons/ScrollLeftBuy.bmp");
        _next = LoadBitmap("Buttons/ScrollRight.bmp");
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

    public void Update(BuyCatalogPage page, string root, bool buildObjects = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(root);

        page.Validate(buildObjects);
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

        return OriginalGameplayOverlay.CreateImage(Device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
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
        // FUN_00539fe8 renders the control-panel clock at y = 85 in 8pt (variablesans_08.ffn) and household funds at y = 125 in 10pt.
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
        // FUN_00505151 disables room sub-sort buttons whose function count in the selected room is zero.
        if (Page?.FunctionCounts is not { Count: 8 } counts)
            return true;
        return counts[OriginalBuyLayout.EngineCategories[index]] > 0;
    }

    private bool FunctionSubsortVisible(int index) => index is >= 0 and <= 3 || index >= 6 || FunctionCategory == 7;

    private bool FunctionSubsortEnabled(int index)
    {
        // UpdateViewFromCPState disables a product filter when no catalog product matches it.
        if (Page?.FunctionSubsortCounts is not { Count: 8 } counts)
            return true;
        return counts[index] > 0;
    }

    private CGRect ArrowRect(bool forward, CGSize canvas)
    {
        var image = forward ? _next : _previous;
        return OriginalBuyLayout.Arrow(forward, new CGSize(image.Size.Width / 4.0, image.Size.Height), canvas);
    }

    public (string Text, CGRect Rect)? ToolTipAt(CGPoint point, CGSize canvas)
    {
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
            if (currentPage.NextOffset(OriginalBuyLayout.Capacity(canvas)) is not null && Contains(nextRect, point))
                return (_strings.PeoplePageTip(1, "Next catalog page"), nextRect);
        }

        return null;
    }

    public List<(string Label, CGRect Rect)> Controls(CGSize canvas)
    {
        var names = FunctionSubSorting
            ? OriginalBuyLayout.FunctionSubsortNames
            : SubSorting
                ? OriginalBuyLayout.RoomSubSortNames
                : RoomMode
                    ? OriginalBuyLayout.RoomNames
                    : OriginalBuyLayout.Categories;

        var controls = new List<(string Label, CGRect Rect)>();
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

        if (SubSorting || FunctionSubSorting)
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
            int capacity = OriginalBuyLayout.Capacity(canvas);
            int count = Math.Min(currentPage.Items.Count, capacity);
            for (int index = 0; index < count; index++)
            {
                var item = currentPage.Items[index];
                controls.Add((item.Name + " — §" + item.Price, OriginalBuyLayout.Thumbnail(index, canvas)));
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

        int capacity = OriginalBuyLayout.Capacity(canvas);
        if (currentPage.Offset > 0 && Contains(ArrowRect(false, canvas), point))
            return new Action.Page(Math.Max(0, currentPage.Offset - capacity));
        if (currentPage.NextOffset(capacity) is { } nextOffset && Contains(ArrowRect(true, canvas), point))
            return new Action.Page(nextOffset);

        int count = Math.Min(currentPage.Items.Count, capacity);
        for (int index = 0; index < count; index++)
        {
            if (Contains(OriginalBuyLayout.Thumbnail(index, canvas), point))
                return new Action.Select(currentPage.Items[index].Guid);
        }

        return null;
    }

    /// <summary>The icon of the item in thumbnail slot <c>index</c>, at its own size.</summary>
    private OriginalGameplayOverlay.Part IconAt(int index, GameplayPanelState.Image? iconSpec, bool highlighted = false) =>
        OriginalBuildOverlay.Icon(index >= 0 && index < _icons.Count ? _icons[index] : null, iconSpec, _unknown, highlighted);

    public void Hover(CGPoint? point) => HoveredPoint = point;

    public void ClearHover() => HoveredPoint = null;

    /// <summary>
    /// cWinCatalogBtn::CreateButton (004cc877.c lines 58-66) + GetToolTipWindow (004cc9df.c lines 12-14):
    /// immediate price tag tooltip colored black when affordable or red (RGB 255, 0, 0) when budget &lt; item.price.
    /// </summary>
    private OriginalGameplayOverlay.Image? PriceTooltip(int price, bool unaffordable)
    {
        string key = $"{price}:{(unaffordable ? 1 : 0)}";
        if (_priceTips.TryGetValue(key, out var cached))
            return cached;

        try
        {
            string label = "§" + OriginalGameplayOverlay.Grouped(price);
            (byte R, byte G, byte B) color = unaffordable ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0);
            var glyphs = _tooltipFont.Rasterize(label, color);
            if (glyphs.Width <= 0 || glyphs.Height <= 0)
                return null;

            int width = glyphs.Width + 6;
            int height = glyphs.Height + 6;
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    bool border = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                    rgba[index] = border ? (byte)0 : (byte)255;
                    rgba[index + 1] = border ? (byte)0 : (byte)255;
                    rgba[index + 2] = border ? (byte)0 : (byte)224;
                    rgba[index + 3] = 255;
                }
            }

            byte[] source = glyphs.Rgba;
            for (int y = 0; y < glyphs.Height; y++)
            {
                for (int x = 0; x < glyphs.Width; x++)
                {
                    int src = (y * glyphs.Width + x) * 4;
                    int alpha = source[src + 3];
                    if (alpha <= 0)
                        continue;

                    int dst = ((y + 3) * width + (x + 3)) * 4;
                    int inv = 255 - alpha;
                    rgba[dst] = (byte)((source[src] * alpha + rgba[dst] * inv) / 255);
                    rgba[dst + 1] = (byte)((source[src + 1] * alpha + rgba[dst + 1] * inv) / 255);
                    rgba[dst + 2] = (byte)((source[src + 2] * alpha + rgba[dst + 2] * inv) / 255);
                }
            }

            var image = OriginalGameplayOverlay.CreateImage(Device, width, height, rgba);
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
        if (Page is not { } currentPage)
            return null;

        int capacity = OriginalBuyLayout.Capacity(canvas);
        int count = Math.Min(currentPage.Items.Count, capacity);
        for (int index = 0; index < count; index++)
        {
            if (!Contains(OriginalBuyLayout.Thumbnail(index, canvas), point))
                continue;

            var item = currentPage.Items[index];
            var preview = index < _previews.Count ? _previews[index] : null;
            var icon = index < _icons.Count ? _icons[index] : null;
            return (
                new OriginalCatalogPopup.Item(item.Name, item.Price, item.Description, item.Ratings ?? []),
                OriginalBuildOverlay.Icon(preview ?? icon, preview is null ? item.Icon : item.Preview, _unknown));
        }

        return null;
    }

    public List<OriginalGameplayOverlay.Part> Parts(CGSize canvas, int? budget = null)
    {
        double top = canvas.Height - 150;
        var parts = new List<OriginalGameplayOverlay.Part>
        {
            new(_universal, new CGRect(0, top - 33, _universal.Size.Width, _universal.Size.Height)),
            new(_patch, new CGRect(64, top - 33 + 35, _patch.Size.Width, _patch.Size.Height))
        };

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

        if (SubSorting || FunctionSubSorting)
        {
            var back = FunctionSubSorting ? _functionBackButtons[FunctionCategory] : _backButtons[Room];
            var rect = BackRect(canvas);
            bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
            parts.Add(new OriginalGameplayOverlay.Part(back, rect, new Vector4(hovered ? 0.50f : 0f, 0f, 0.25f, 1f)));
        }

        if (Page is { } currentPage)
        {
            OriginalGameplayOverlay.Part? hoveredPricePart = null;
            int capacity = OriginalBuyLayout.Capacity(canvas);
            int count = Math.Min(currentPage.Items.Count, capacity);

            for (int index = 0; index < count; index++)
            {
                var item = currentPage.Items[index];
                var rect = OriginalBuyLayout.Thumbnail(index, canvas);
                bool chosen = Selected is { } selGuid && item.Guid == selGuid;
                bool hovered = HoveredPoint is { } hp && Contains(rect, hp);
                float state = chosen ? 1f : hovered ? 2f : 0f;
                parts.Add(new OriginalGameplayOverlay.Part(_frame, rect, new Vector4(state / 4f, 0f, 0.25f, 1f)));
                parts.Add(OriginalBuildOverlay.Fit(IconAt(index, item.Icon, highlighted: chosen || hovered), rect));

                if (hovered && PriceTooltip(item.Price, budget is { } b && item.Price > b) is { } tip)
                {
                    // cWinCatalogBtn::PositionToolTip (005e8b76.c line 10): bottom-left of the hovered catalog cell.
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
