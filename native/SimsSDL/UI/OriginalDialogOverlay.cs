using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

public sealed class OriginalDialogOverlay
{
    public readonly record struct Image(UITexture Texture, CGSize Size, int Columns = 1, int Rows = 1)
    {
        public CGSize FrameSize => new(
            Size.Width / Math.Max(1, Columns),
            Size.Height / Math.Max(1, Rows));

        public Vector4 TopLeftUV => new(
            0f,
            0f,
            1f / Math.Max(1, Columns),
            1f / Math.Max(1, Rows));

        public Vector4 FrameUV
        {
            get
            {
                float row = (Columns == 1 && Rows == 3) ? 1f : 0f;
                return new Vector4(
                    0f,
                    row / Math.Max(1, Rows),
                    1f / Math.Max(1, Columns),
                    1f / Math.Max(1, Rows));
            }
        }
    }

    public sealed record BookIngredientSlot(int Id, string Name, Image? Icon, CGRect Rect);

    public sealed record BookRow(int Index, string Title, CGRect Rect, IReadOnlyList<BookIngredientSlot> Ingredients);

    public sealed record ButtonLabels(Image Normal, Image Pressed, Image Hover, Image Disabled)
    {
        public Image Image(int state) => state switch
        {
            1 => Pressed,
            2 => Hover,
            3 => Disabled,
            _ => Normal
        };
    }

    public abstract record PhoneBookAction
    {
        private PhoneBookAction() { }
        public sealed record ChangedSelection : PhoneBookAction;
        public sealed record Call(int NeighborId) : PhoneBookAction;
        public sealed record Cancel : PhoneBookAction;
    }

    private const double ScrollbarWidth = 17; // Scrollbar texture contains 12 horizontal cells of 17x20 pixels each.
    private const double ScrollbarButtonHeight = 20;
    private const double ScrollbarReservedWidth = 20; // List boxes reserve a 20-pixel right margin for scrollbar controls.
    private const int PhoneBookVisibleRows = 10;
    private static readonly (byte R, byte G, byte B) DefaultTextColor = (195, 205, 205);
    private static readonly (byte R, byte G, byte B) SelectedTextColor = (0, 255, 255);
    private static readonly (byte R, byte G, byte B) HoverTextColor = (255, 255, 255);
    private static readonly (byte R, byte G, byte B) DisabledTextColor = (64, 93, 95);

    public GameDialog Dialog { get; }
    public SimsBitmapFont Font { get; }
    public SimsBitmapFont TitleFont { get; }
    public SimsBitmapFont DialogTitleFont { get; }
    public SimsBitmapFont MessageBodyFont { get; }
    public SimsBitmapFont InputFont { get; }
    public SimsBitmapFont ButtonFont { get; }
    public SimsBitmapFont TooltipFont { get; }
    public UIDevice Device { get; }

    /// <summary>Nine-slice dialog background frames for neighborhood screens (soft-edged) and lot views
    /// (beveled), where <c>Edge</c> specifies the slice border width in pixels.</summary>
    public (UITexture Texture, double Edge) NeighborhoodFrame { get; }
    public (UITexture Texture, double Edge) LotFrame { get; }
    public UITexture ButtonTexture { get; }
    public float ButtonSheetWidth { get; }
    public double ButtonCapWidth { get; }
    public UITexture ScrollbarTexture { get; }
    public Image? PeopleTemplate { get; }
    public Image? UnknownFace { get; }
    public Image? TutorialCloseBox { get; }
    public UITexture BodyTexture { get; }
    public UITexture InputBackground { get; }
    public UITexture InputBorderFocused { get; }
    public UITexture InputBorderUnfocused { get; }
    public UITexture TooltipBackground { get; }
    public UITexture TooltipBorder { get; }

    /// <summary>The magic book background bitmap exceeds its 600x382 content area so decorative borders
    /// overhang dialog controls.</summary>
    public Image? BookBackground { get; }
    public Image? BookRowBackground { get; }
    public Image? BookCatalogButton { get; }
    public IReadOnlyList<Image?> BookStationImages { get; }
    public IReadOnlyDictionary<int, Image> BookIngredientIcons { get; }
    public Image? PhoneBookBackground { get; }
    public Image? PhoneIcon { get; }
    public Image? Illustration { get; }
    public bool FramesIllustrationInPeopleTemplate { get; }
    public CGRect ImageRect { get; private set; }
    public CGRect PhoneIconRect { get; private set; }
    public Image Title { get; }
    public IReadOnlyList<Image> Labels { get; }
    public IReadOnlyList<ButtonLabels> ButtonLabelStates { get; }

    private readonly string _phoneBookCallVerbText;
    private readonly string _phoneBookEmptyBannerText;
    private readonly List<(Image Normal, Image Selected, Image Hover)> _phoneBookFamilyLabels;
    private readonly List<List<(Image Normal, Image Selected, Image Hover)>> _phoneBookContactLabels;
    private readonly List<List<Image?>> _phoneBookContactPortraits;

    public Image? PhoneBookBannerLabel { get; private set; }
    private string? _cachedPhoneBookBannerText;

    private List<Image> _message = [];
    public IReadOnlyList<Image> Message => _message;
    public CGRect Rect { get; private set; }
    public CGPoint TitleOrigin { get; private set; }
    public CGRect BodyRect { get; private set; }
    public CGRect InputRect { get; private set; }

    private List<CGRect> _buttonRects = [];
    public IReadOnlyList<CGRect> ButtonRects => _buttonRects;
    private double _messageSideOriginX;
    private double _messageFullOriginX;

    /// <summary>Scrollbar draw calls (up arrow, track, down arrow, and thumb) rendered from
    /// <c>WinScrol.bmp</c> for scrollable dialog bodies and lists.</summary>
    private readonly List<(UITexture Texture, CGRect Rect, Vector4 UV)> _scrollButtonDraws = [];
    public IReadOnlyList<(UITexture Texture, CGRect Rect, Vector4 UV)> ScrollButtonDraws => _scrollButtonDraws;
    private CGRect _messageScrollbarRect;
    private CGRect _phoneBookFamilyScrollbarRect;
    private CGRect _phoneBookContactScrollbarRect;
    private CGPoint? _lastHoverLocal;

    public CGRect TutorialCloseBoxRect { get; private set; }
    public bool TutorialCloseBoxHovered { get; private set; }
    public Image? Input { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public int? Hovered { get; private set; }
    public int? Pressed { get; private set; }

    private string _wrappedKey = string.Empty;
    private (CGSize Viewport, OriginalMessageDialogLayout.Result Solved)? _cachedMessageLayout;
    private List<bool> _messageSideFlags = [];
    private int _scroll;
    private int _familyScroll;
    private int _contactScroll;
    private int _selectedFamily;
    private int? _selectedContact;

    private List<(Image Image, CGRect Rect, bool Selected)> _phoneBookFamilyRows = [];
    public IReadOnlyList<(Image Image, CGRect Rect, bool Selected)> PhoneBookFamilyRows => _phoneBookFamilyRows;
    private List<(Image Image, CGRect Rect, bool Selected)> _phoneBookContactRows = [];
    public IReadOnlyList<(Image Image, CGRect Rect, bool Selected)> PhoneBookContactRows => _phoneBookContactRows;
    public CGRect PhoneBookFamilyRect { get; private set; }
    public CGRect PhoneBookContactRect { get; private set; }

    private readonly OriginalBookCatalog? _bookCatalog;
    public int BookPage { get; private set; }
    public int BookSelectedIndex { get; private set; }
    private int _bookAvailabilityContextItemID;

    private List<BookRow> _bookRows = [];
    public IReadOnlyList<BookRow> BookRows => _bookRows;
    private List<CGRect> _bookEmptyRowRects = [];
    public IReadOnlyList<CGRect> BookEmptyRowRects => _bookEmptyRowRects;
    private readonly List<Image> _bookTitleLines = [];
    public IReadOnlyList<Image> BookTitleLines => _bookTitleLines;
    public Image? BookDetailTitle { get; private set; }
    private List<Image> _bookDetail = [];
    public IReadOnlyList<Image> BookDetail => _bookDetail;
    public CGRect BookDetailRect { get; private set; }
    public CGRect BookListRect { get; private set; }
    public CGRect BookStationRect { get; private set; }
    public Image? BookStationImage { get; private set; }
    public Image? BookBack { get; }
    public Image? BookNext { get; }
    public ButtonLabels? BookBackLabels { get; }
    public ButtonLabels? BookNextLabels { get; }
    public Image? BookPageLabel { get; private set; }
    public CGRect BookBackRect { get; private set; }
    public CGRect BookPageRect { get; private set; }
    public CGRect BookNextRect { get; private set; }
    public bool BookBackHovered { get; private set; }
    public bool BookNextHovered { get; private set; }
    public bool BookBackPressed { get; private set; }
    public bool BookNextPressed { get; private set; }
    public bool CanBookBack => BookPage > 0;
    public bool CanBookNext
    {
        get
        {
            int pageCount = Math.Max(1, ((_bookCatalog?.Entries.Count ?? 0) + 3) / 4);
            return BookPage + 1 < pageCount;
        }
    }
    public int? BookHoveredRowIndex { get; private set; }
    public (Image Image, CGRect Rect)? BookIngredientTooltip { get; private set; }
    private string? _cachedBookTooltipText;
    private string? _cachedBookPageText;
    private string? _cachedBookDetailKey;
    public bool Submitting { get; set; }
    public bool InputFocused { get; private set; }

    public OriginalDialogOverlay(
        GameDialog dialog,
        SimsBitmapFont font,
        UIDevice device,
        GameContent content,
        string? sceneDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(content);

        dialog.Validate();
        Dialog = dialog;
        Font = font;

        // Bitmap font line heights exceed nominal point sizes (08 is 16px, 09 is 17px, 10 is 19px, 12 is
        // 23px, 14 is 27px), requiring a 12pt title font (13px cap) and 10pt body font (10px cap) to align
        // with standard button heights.
        var loaded12 = TryFont(content, "UIGraphics/Fonts/variablesans_12.ffn") ?? font;
        var loaded09 = TryFont(content, "UIGraphics/Fonts/variablesans_09.ffn") ?? font;
        TitleFont = loaded12;
        DialogTitleFont = loaded12;
        MessageBodyFont = loaded09;
        InputFont = loaded09;
        ButtonFont = loaded09;
        TooltipFont = TryFont(content, "UIGraphics/Fonts/variablesans_07.ffn")
            ?? TryFont(content, "UIGraphics/Fonts/variablesans_08.ffn")
            ?? loaded09;
        Device = device;
        InputFocused = dialog.Kind == 3 && dialog.Blocking;

        UITexture Upload(SimsBitmap bitmap, bool transparent) =>
            Texture(
                device,
                bitmap.Width,
                bitmap.Height,
                SpritePixels.PremultipliedSRGB(bitmap.Rgba, removeMagenta: transparent));

        ButtonLabels MakeButtonLabels(string caption) => new(
            Label(caption, ButtonFont, device, DefaultTextColor),
            Label(caption, ButtonFont, device, SelectedTextColor),
            Label(caption, ButtonFont, device, HoverTextColor),
            Label(caption, ButtonFont, device, DisabledTextColor));

        SimsBitmap? illustrationBitmap = null;
        try
        {
            illustrationBitmap = dialog.ReadImage(sceneDirectory);
        }
        catch
        {
            illustrationBitmap = null;
        }

        if (illustrationBitmap is not null)
        {
            var loadedIllustration = new Image(
                Upload(illustrationBitmap, transparent: true),
                new CGSize(illustrationBitmap.Width, illustrationBitmap.Height),
                dialog.Image?.Columns ?? 1,
                dialog.Image?.Rows ?? 1);
            Illustration = loadedIllustration;
            double frameW = loadedIllustration.FrameSize.Width;
            double frameH = loadedIllustration.FrameSize.Height;
            // Centers 35x41 Sim head portraits within the 45x45 frame of <c>PeopleTemplate.bmp</c>.
            FramesIllustrationInPeopleTemplate = frameW > 0 && frameW <= 41 && frameH > 0 && frameH <= 43
                && (loadedIllustration.Columns == 3
                    || ((int)Math.Round(frameW, MidpointRounding.AwayFromZero) == 35
                        && (int)Math.Round(frameH, MidpointRounding.AwayFromZero) == 41));
        }
        else
        {
            Illustration = null;
            FramesIllustrationInPeopleTemplate = false;
        }

        var tiler = new SimsTarga(content.Read("UIGraphics/Nbhd/NScreenDialogTiler.tga"));
        NeighborhoodFrame = (
            Texture(device, tiler.Width, tiler.Height, SpritePixels.PremultipliedSRGB(tiler.Rgba)),
            tiler.Width / 3.0);

        var popup = new SimsBitmap(content.Read("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp"));
        LotFrame = (Upload(popup, transparent: false), popup.Width / 3.0);

        var winBtnBitmap = new SimsBitmap(content.Read("UIGraphics/Shared/Sys/WinBtn.bmp"));
        ButtonTexture = Upload(winBtnBitmap, transparent: true);
        ButtonSheetWidth = Math.Max(1, winBtnBitmap.Width);
        ButtonCapWidth = Math.Max(1, (winBtnBitmap.Width / 4 - 1) / 2);

        ScrollbarTexture = Upload(new SimsBitmap(content.Read("UIGraphics/Shared/Sys/WinScrol.bmp")), transparent: false);

        PeopleTemplate = TryUploadSheet(content, "UIGraphics/cpanel/People/PeopleTemplate.bmp", columns: 4, rows: 1, transparent: true, Upload);
        UnknownFace = TryUploadSheet(content, "UIGraphics/cpanel/Buttons/UnknownFace.bmp", columns: 3, rows: 1, transparent: true, Upload);

        if (!dialog.Blocking && (dialog.Tutorial == true || dialog.Buttons.Count == 0))
        {
            var closeBitmap = new SimsBitmap(content.Read("UIGraphics/Shared/Sys/MinimizeBox.bmp"));
            TutorialCloseBox = new Image(
                Upload(closeBitmap, transparent: true),
                new CGSize(closeBitmap.Width / 4.0, closeBitmap.Height));
        }
        else
        {
            TutorialCloseBox = null;
        }

        BodyTexture = Upload(new SimsBitmap(content.Read("UIGraphics/Shared/Dlg/LBoxTile.bmp")), transparent: false);

        InputBackground = Texture(device, 1, 1, [0, 0, 82, 255]);
        InputBorderFocused = Texture(device, 1, 1, [195, 205, 205, 255]);
        InputBorderUnfocused = Texture(device, 1, 1, [64, 93, 95, 255]);
        TooltipBackground = Texture(device, 1, 1, [255, 255, 225, 255]);
        TooltipBorder = Texture(device, 1, 1, [0, 0, 0, 255]);

        if (dialog.IsBookScreen)
        {
            var bookBmp = new SimsBitmap(content.Read("UIGraphics/CPanel/Backgrounds/magicbookbkg.bmp"));
            BookBackground = new Image(Upload(bookBmp, transparent: false), new CGSize(bookBmp.Width, bookBmp.Height));
            BookRowBackground = TryUploadSheet(content, "UIGraphics/Nbhd/SpellPickItemBkg.bmp", columns: 1, rows: 1, transparent: false, Upload);
            BookCatalogButton = TryUploadSheet(content, "UIGraphics/cpanel/Buttons/CatalogBtn.bmp", columns: 1, rows: 1, transparent: true, Upload);

            string[] stationPaths =
            [
                "UIGraphics/cpanel/Backgrounds/SPELLBOOK_CharmMakerKid.bmp",
                "UIGraphics/cpanel/Backgrounds/SPELLBOOK_CharmMakerAdult.bmp",
                "UIGraphics/cpanel/Backgrounds/SPELLBOOK_WandCharger.bmp",
                "UIGraphics/cpanel/Backgrounds/SPELLBOOK_BakersOven.bmp",
                "UIGraphics/cpanel/Backgrounds/SPELLBOOK_NectarPress.bmp"
            ];
            var stations = new Image?[stationPaths.Length];
            for (int i = 0; i < stationPaths.Length; i++)
                stations[i] = TryUploadSheet(content, stationPaths[i], columns: 1, rows: 1, transparent: false, Upload);
            BookStationImages = stations;
        }
        else
        {
            BookBackground = null;
            BookRowBackground = null;
            BookCatalogButton = null;
            BookStationImages = [];
        }

        if (dialog.Kind == 10)
        {
            var strings = new OriginalUIStrings(content);
            _phoneBookCallVerbText = strings.PhoneBookCallVerb();
            string doesNotKnow = strings.PhoneBookDoesNotKnowAnyone();
            _phoneBookEmptyBannerText = string.IsNullOrEmpty(dialog.Title)
                ? doesNotKnow
                : $"{dialog.Title} {doesNotKnow}";

            var phoneBgBmp = new SimsBitmap(content.Read("UIGraphics/cpanel/Backgrounds/phonebookbkg.bmp"));
            PhoneBookBackground = new Image(
                Texture(device, phoneBgBmp.Width, phoneBgBmp.Height, SpritePixels.PremultipliedSRGB(phoneBgBmp.Rgba, removeMagenta: false)),
                new CGSize(phoneBgBmp.Width, phoneBgBmp.Height));

            PhoneIcon = TryUploadSheet(content, "UIGraphics/cpanel/Backgrounds/phoneicon.bmp", columns: 1, rows: 1, transparent: false, Upload);

            var families = dialog.PhoneBook ?? [];
            _phoneBookFamilyLabels = new List<(Image, Image, Image)>(families.Count);
            _phoneBookContactLabels = new List<List<(Image, Image, Image)>>(families.Count);
            _phoneBookContactPortraits = new List<List<Image?>>(families.Count);

            foreach (var family in families)
            {
                _phoneBookFamilyLabels.Add((
                    Label(family.Name, font, device, DefaultTextColor),
                    Label(family.Name, font, device, SelectedTextColor),
                    Label(family.Name, font, device, HoverTextColor)));

                var contactLabels = new List<(Image, Image, Image)>(family.Contacts.Count);
                var contactPortraits = new List<Image?>(family.Contacts.Count);
                foreach (var contact in family.Contacts)
                {
                    contactLabels.Add((
                        Label(contact.Name, font, device, DefaultTextColor),
                        Label(contact.Name, font, device, SelectedTextColor),
                        Label(contact.Name, font, device, HoverTextColor)));

                    SimsBitmap? portraitBmp = null;
                    try
                    {
                        portraitBmp = GameDialog.ReadBitmap(contact.Portrait, sceneDirectory);
                    }
                    catch
                    {
                        portraitBmp = null;
                    }

                    contactPortraits.Add(portraitBmp is not null
                        ? new Image(
                            Upload(portraitBmp, transparent: true),
                            new CGSize(portraitBmp.Width, portraitBmp.Height),
                            contact.Portrait?.Columns ?? 1,
                            contact.Portrait?.Rows ?? 1)
                        : null);
                }

                _phoneBookContactLabels.Add(contactLabels);
                _phoneBookContactPortraits.Add(contactPortraits);
            }
        }
        else
        {
            _phoneBookCallVerbText = "Call";
            _phoneBookEmptyBannerText = string.Empty;
            PhoneBookBackground = null;
            PhoneIcon = null;
            _phoneBookFamilyLabels = [];
            _phoneBookContactLabels = [];
            _phoneBookContactPortraits = [];
        }

        if (dialog.IsBookScreen)
        {
            var catalog = new OriginalBookCatalog(
                content,
                dialog.Kind,
                dialog.BookMode,
                dialog.BookAvailability,
                dialog.HideBookItem9,
                dialog.HideBookItem4);
            _bookCatalog = catalog;
            var backStates = MakeButtonLabels(catalog.Back);
            var nextStates = MakeButtonLabels(catalog.Next);
            BookBackLabels = backStates;
            BookNextLabels = nextStates;
            BookBack = backStates.Normal;
            BookNext = nextStates.Normal;

            var icons = new Dictionary<int, Image>();
            foreach (var (id, ingredient) in catalog.Ingredients)
            {
                if (ingredient.BitmapData is not { } bmpData)
                    continue;
                try
                {
                    var bmp = new SimsBitmap(bmpData);
                    icons[id] = new Image(Upload(bmp, transparent: false), new CGSize(bmp.Width, bmp.Height));
                }
                catch
                {
                }
            }
            BookIngredientIcons = icons;
        }
        else
        {
            _bookCatalog = null;
            BookBackLabels = null;
            BookNextLabels = null;
            BookBack = null;
            BookNext = null;
            BookIngredientIcons = new Dictionary<int, Image>();
        }

        string? bookTitle = _bookCatalog?.TitleFormat.Replace(
            "%s",
            string.IsNullOrEmpty(dialog.Title) ? "Family" : dialog.Title,
            StringComparison.Ordinal);

        if (bookTitle is not null)
        {
            IReadOnlyList<string> wrappedTitle;
            try
            {
                wrappedTitle = loaded12.WrappedLines(bookTitle, 240);
            }
            catch
            {
                wrappedTitle = [bookTitle];
            }

            foreach (string line in wrappedTitle.Take(2))
                _bookTitleLines.Add(Label(line, loaded12, device, DefaultTextColor));

            Title = _bookTitleLines.Count > 0
                ? _bookTitleLines[0]
                : Label(bookTitle, loaded12, device, DefaultTextColor);
        }
        else if (dialog.Kind == 10)
        {
            Title = Label(dialog.Title, loaded12, device, DefaultTextColor);
        }
        else
        {
            Title = Label(dialog.Title, DialogTitleFont, device, DefaultTextColor);
        }

        string? bookOK = _bookCatalog?.Ok;
        var loadedButtonStates = new List<ButtonLabels>(dialog.Buttons.Count);
        foreach (var button in dialog.Buttons)
        {
            string buttonText = dialog.IsBookScreen ? (bookOK ?? button.Label) : button.Label;
            loadedButtonStates.Add(MakeButtonLabels(buttonText));
        }
        ButtonLabelStates = loadedButtonStates;
        Labels = loadedButtonStates.Select(static s => s.Normal).ToArray();

        if (dialog.Kind == 3)
            UpdateText(string.Empty);
    }

    private static SimsBitmapFont? TryFont(GameContent content, string path)
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

    private static Image? TryUploadSheet(
        GameContent content,
        string path,
        int columns,
        int rows,
        bool transparent,
        Func<SimsBitmap, bool, UITexture> upload)
    {
        try
        {
            var bitmap = new SimsBitmap(content.Read(path));
            return new Image(upload(bitmap, transparent), new CGSize(bitmap.Width, bitmap.Height), columns, rows);
        }
        catch
        {
            return null;
        }
    }

    private static UITexture Texture(UIDevice device, int width, int height, byte[] pixels)
    {
        var descriptor = UITextureDescriptor.Texture2DDescriptor(
            UITextureDescriptor.PixelFormat.Rgba8UnormSrgb,
            width,
            height,
            mipmapped: true);
        var texture = device.MakeTexture(descriptor)
            ?? throw new ContentError("Cannot allocate game dialog texture");
        texture.Replace(new UIRegion(0, 0, width, height), 0, pixels, width * 4);
        return texture;
    }

    private static Image Label(string value, SimsBitmapFont font, UIDevice device, (byte R, byte G, byte B) color)
    {
        var raster = font.RasterizeHiDpi(value, color, logicalMargin: 0);
        return new Image(
            Texture(device, raster.PixelWidth, raster.PixelHeight, SpritePixels.PremultipliedSRGB(raster.Rgba)),
            new CGSize(raster.LogicalWidth, raster.LogicalHeight));
    }

    private static CGRect ScrollbarThumbRect(CGRect bar, int scroll, int maxScroll)
    {
        const double btnH = ScrollbarButtonHeight;
        double minY = bar.MinY + btnH;
        double maxY = Math.Max(minY, bar.MaxY - btnH * 2);
        double fraction = maxScroll > 0 ? Math.Clamp(scroll, 0, maxScroll) / (double)maxScroll : 0;
        double thumbY = minY + Math.Round(fraction * (maxY - minY), MidpointRounding.AwayFromZero);
        return new CGRect(bar.MinX, thumbY, bar.Width, btnH);
    }

    private static bool ApplyScrollbarClick(CGPoint point, CGRect bar, ref int scroll, int maxScroll, int pageStep)
    {
        if (maxScroll <= 0 || bar.Width <= 0 || !bar.Contains(point))
            return false;

        const double btnH = ScrollbarButtonHeight;
        if (point.Y < bar.MinY + btnH)
        {
            scroll = Math.Max(0, scroll - 1);
            return true;
        }
        if (point.Y >= bar.MaxY - btnH)
        {
            scroll = Math.Min(maxScroll, scroll + 1);
            return true;
        }

        var thumb = ScrollbarThumbRect(bar, scroll, maxScroll);
        if (point.Y < thumb.MinY)
            scroll = Math.Max(0, scroll - pageStep);
        else if (point.Y >= thumb.MaxY)
            scroll = Math.Min(maxScroll, scroll + pageStep);
        return true;
    }

    private void AppendVerticalScrollbar(CGRect bar, int scroll, int maxScroll)
    {
        const float cellW = 1.0f / 12.0f;
        const double btnH = ScrollbarButtonHeight;
        var upRect = new CGRect(bar.MinX, bar.MinY, bar.Width, btnH);
        var downRect = new CGRect(bar.MinX, bar.MaxY - btnH, bar.Width, btnH);
        var trackRect = new CGRect(bar.MinX, bar.MinY + btnH, bar.Width, Math.Max(0, bar.Height - btnH * 2));
        bool upActive = scroll > 0 && _lastHoverLocal is { } hoverUp && upRect.Contains(hoverUp);
        bool downActive = scroll < maxScroll && _lastHoverLocal is { } hoverDown && downRect.Contains(hoverDown);

        // Maps scrollbar texture cells: cells 0 and 1 for up button states, cell 2 for the thumb, cell 3
        // for the track, and cells 4 and 5 for down button states.
        _scrollButtonDraws.Add((ScrollbarTexture, upRect, new Vector4((upActive ? 1f : 0f) * cellW, 0f, cellW, 1f)));
        if (trackRect.Height > 0)
            _scrollButtonDraws.Add((ScrollbarTexture, trackRect, new Vector4(3f * cellW, 0f, cellW, 1f)));
        _scrollButtonDraws.Add((ScrollbarTexture, downRect, new Vector4((downActive ? 5f : 4f) * cellW, 0f, cellW, 1f)));
        if (maxScroll > 0 && trackRect.Height >= btnH)
        {
            var thumbRect = ScrollbarThumbRect(bar, scroll, maxScroll);
            _scrollButtonDraws.Add((ScrollbarTexture, thumbRect, new Vector4(2f * cellW, 0f, cellW, 1f)));
        }
    }

    public void Prepare(CGSize canvas, double scale)
    {
        _scrollButtonDraws.Clear();
        _messageScrollbarRect = CGRect.Zero;
        _phoneBookFamilyScrollbarRect = CGRect.Zero;
        _phoneBookContactScrollbarRect = CGRect.Zero;
        InputRect = CGRect.Zero;

        var viewport = new CGSize(canvas.Width / scale, canvas.Height / scale);
        if (Dialog.IsBookScreen)
        {
            PrepareBook(viewport);
            return;
        }
        if (Dialog.Kind == 10)
        {
            PreparePhoneBook(viewport);
            return;
        }

        CGSize rawImageFrame = Illustration?.FrameSize ?? CGSize.Zero;
        CGSize imageFrame = FramesIllustrationInPeopleTemplate && PeopleTemplate is not null
            ? new CGSize(45, 45)
            : rawImageFrame;

        OriginalMessageDialogLayout.Result layout;
        if (_cachedMessageLayout is { } cached && cached.Viewport == viewport)
        {
            layout = cached.Solved;
        }
        else
        {
            var labelWidths = new double[Labels.Count];
            for (int i = 0; i < Labels.Count; i++)
                labelWidths[i] = Labels[i].Size.Width;

            var solved = OriginalMessageDialogLayout.Solve(
                new OriginalMessageDialogLayout.Input(
                    viewport,
                    Dialog.Title,
                    Title.Size,
                    DialogTitleFont.LineHeight,
                    Dialog.Message,
                    MessageBodyFont,
                    imageFrame,
                    labelWidths,
                    hasTextInput: Dialog.Kind == 3,
                    hasTutorialCloseBox: TutorialCloseBox is not null,
                    tutorialCloseBoxSize: TutorialCloseBox?.Size ?? new CGSize(12, 11)));
            _cachedMessageLayout = (viewport, solved);
            layout = solved;
        }

        string nextWrapKey = $"{layout.EffectiveFullWrap}:{layout.EffectiveSideWrap}:{layout.EffectiveSideLimit}";
        if (!string.Equals(_wrappedKey, nextWrapKey, StringComparison.Ordinal))
        {
            var newMessages = new List<Image>(layout.Lines.Count);
            var newSideFlags = new List<bool>(layout.Lines.Count);
            foreach (var line in layout.Lines)
            {
                newMessages.Add(Label(line.Text, MessageBodyFont, Device, DefaultTextColor));
                newSideFlags.Add(line.Side);
            }
            _message = newMessages;
            _messageSideFlags = newSideFlags;
            _wrappedKey = nextWrapKey;
        }

        const double pad = OriginalMessageDialogLayout.Pad;
        Rect = layout.Rect;
        TitleOrigin = layout.TitleOrigin;
        BodyRect = layout.BodyRect;
        ImageRect = layout.ImageRect;
        InputRect = layout.InputRect;
        _buttonRects = [.. layout.ButtonRects];
        TutorialCloseBoxRect = layout.TutorialCloseBoxRect;
        _messageFullOriginX = Rect.MinX + pad;
        _messageSideOriginX = Rect.MinX + pad + layout.ImageColumn;

        int bodyLineHeight = Math.Max(1, MessageBodyFont.LineHeight);
        int visibleCount = Math.Max(1, (int)BodyRect.Height / bodyLineHeight);
        int maxMsgScroll = Math.Max(0, _message.Count - visibleCount);
        _scroll = Math.Clamp(_scroll, 0, maxMsgScroll);
        if (maxMsgScroll > 0)
        {
            _messageScrollbarRect = new CGRect(
                Rect.MaxX - pad - ScrollbarWidth,
                BodyRect.MinY,
                ScrollbarWidth,
                BodyRect.Height);
            AppendVerticalScrollbar(_messageScrollbarRect, _scroll, maxMsgScroll);
        }
    }

    private void PrepareBook(CGSize viewport)
    {
        // Sizes the book dialog to 600x382 pixels, centering it vertically within the viewport area above
        // the bottom 100-pixel HUD.
        const double width = 600;
        const double height = 382;
        double centerOffsetY = Math.Max(0, (viewport.Height - 100 - height) / 2.0);
        Rect = new CGRect(Math.Floor((viewport.Width - width) / 2.0), Math.Floor(centerOffsetY), width, height);

        // Defines book dialog layout: left column contains four 240x68 recipe rows at offset (49, 54);
        // right column contains header at (331, 8), description at (331, 54), and station artwork at (420,
        // 232).
        BookListRect = new CGRect(Rect.MinX + 49, Rect.MinY + 54, 240, 272);
        BodyRect = new CGRect(Rect.MinX + 331, Rect.MinY + 54, 240, 180);
        BookDetailRect = BodyRect;
        BookStationRect = new CGRect(Rect.MinX + 420, Rect.MinY + 232, 130, 90);

        var catalog = _bookCatalog?.Entries ?? [];
        int pageCount = Math.Max(1, (catalog.Count + 3) / 4);
        BookPage = Math.Clamp(BookPage, 0, pageCount - 1);
        int firstIndex = BookPage * 4;
        if (BookSelectedIndex < firstIndex || BookSelectedIndex >= Math.Min(catalog.Count, firstIndex + 4))
            SelectBook(firstIndex);

        string formatTemplate = _bookCatalog?.PageFormat ?? "Page %d of %d";
        string[] placeholders = formatTemplate.Split("%d", StringSplitOptions.None);
        string pageText = placeholders.Length >= 3
            ? $"{placeholders[0]}{BookPage + 1}{placeholders[1]}{pageCount}{placeholders[2]}"
            : $"Page {BookPage + 1} of {pageCount}";

        if (!string.Equals(_cachedBookPageText, pageText, StringComparison.Ordinal))
        {
            BookPageLabel = Label(pageText, TitleFont, Device, DefaultTextColor);
            _cachedBookPageText = pageText;
        }

        const double rowHeight = 68;
        int rowCount = Math.Min(4, Math.Max(0, catalog.Count - firstIndex));
        var rows = new List<BookRow>(rowCount);
        for (int row = 0; row < rowCount; row++)
        {
            int index = firstIndex + row;
            var entry = catalog[index];
            var rowRect = new CGRect(BookListRect.MinX, BookListRect.MinY + row * rowHeight, 240, rowHeight);
            // Positions up to three 45x45 ingredient buttons at horizontal 68-pixel increments starting at
            // x=29 within each recipe row.
            int slotCount = Math.Min(3, entry.IngredientIDs.Count);
            var slots = new List<BookIngredientSlot>(slotCount);
            for (int slotIdx = 0; slotIdx < slotCount; slotIdx++)
            {
                int ingID = entry.IngredientIDs[slotIdx];
                OriginalBookCatalog.Ingredient? info = _bookCatalog is not null
                    && _bookCatalog.Ingredients.TryGetValue(ingID, out var foundInfo)
                    ? foundInfo
                    : null;
                Image? icon = BookIngredientIcons.TryGetValue(ingID, out var foundIcon) ? foundIcon : null;
                var iconRect = new CGRect(rowRect.MinX + 29 + slotIdx * 68, rowRect.MinY + 6, 45, 45);
                slots.Add(new BookIngredientSlot(ingID, info?.Name ?? $"Ingredient {ingID}", icon, iconRect));
            }
            rows.Add(new BookRow(index, entry.Title, rowRect, slots));
        }
        _bookRows = rows;

        // Fills unused rows up to the 4-row maximum with unselected row background frames.
        var emptyRects = new List<CGRect>();
        for (int row = _bookRows.Count; row < 4; row++)
            emptyRects.Add(new CGRect(BookListRect.MinX, BookListRect.MinY + row * rowHeight, 240, rowHeight));
        _bookEmptyRowRects = emptyRects;

        string detailKey = $"{BookSelectedIndex}:{_bookAvailabilityContextItemID}";
        if (!string.Equals(_cachedBookDetailKey, detailKey, StringComparison.Ordinal))
        {
            if (BookSelectedIndex >= 0 && BookSelectedIndex < catalog.Count)
            {
                var entry = catalog[BookSelectedIndex];
                string resolvedTitle = _bookCatalog?.DetailTitle(entry, _bookAvailabilityContextItemID) ?? entry.Title;
                string resolvedDesc = _bookCatalog?.DetailDescription(entry, _bookAvailabilityContextItemID) ?? entry.Description;
                BookDetailTitle = Label(resolvedTitle, TitleFont, Device, DefaultTextColor);
                int maxLines = Math.Max(1, (int)BookDetailRect.Height / Math.Max(1, Font.LineHeight));
                var wrapped = Font.WrappedLines(resolvedDesc, (int)BookDetailRect.Width);
                var detailLines = new List<Image>();
                foreach (string line in wrapped.Take(maxLines))
                    detailLines.Add(Label(line, Font, Device, DefaultTextColor));
                _bookDetail = detailLines;

                int stationIdx = entry.StationIndex;
                BookStationImage = stationIdx >= 0 && stationIdx < BookStationImages.Count
                    ? BookStationImages[stationIdx]
                    : null;
            }
            else
            {
                BookDetailTitle = null;
                _bookDetail = [];
                BookStationImage = null;
            }
            _cachedBookDetailKey = detailKey;
        }

        // Magic book bottom buttons use 33px-tall 3-sliced frames with a 65px minimum width, padding text
        // by 20px to prevent clipping localized strings.
        const double buttonH = 33;
        double buttonY = Rect.MaxY - Math.Floor(buttonH * 3.0 / 2.0);
        double backW = Math.Max(65, Math.Ceiling((BookBack?.Size.Width ?? 0) + 20));
        double pageW = Math.Max(65, Math.Ceiling((BookPageLabel?.Size.Width ?? 0) + 12));
        double nextW = Math.Max(65, Math.Ceiling((BookNext?.Size.Width ?? 0) + 20));
        BookBackRect = new CGRect(Rect.MinX + 49, buttonY, backW, buttonH);
        double pageX = Math.Max(Rect.MinX + 130, BookBackRect.MaxX + 8);
        BookPageRect = new CGRect(pageX, buttonY, pageW, buttonH);
        double nextX = Math.Max(Rect.MinX + 195, BookPageRect.MaxX + 8);
        BookNextRect = new CGRect(nextX, buttonY, nextW, buttonH);

        var buttonRects = new List<CGRect>(Labels.Count);
        foreach (var label in Labels)
        {
            double okW = Math.Max(65, Math.Ceiling(label.Size.Width) + 20);
            buttonRects.Add(new CGRect(Rect.MaxX - okW - 20, buttonY, okW, buttonH));
        }
        _buttonRects = buttonRects;
    }

    public Image? CurrentPhoneBookPortrait
    {
        get
        {
            if (_selectedContact is int contactIdx
                && _selectedFamily >= 0 && _selectedFamily < _phoneBookContactPortraits.Count
                && contactIdx >= 0 && contactIdx < _phoneBookContactPortraits[_selectedFamily].Count
                && _phoneBookContactPortraits[_selectedFamily][contactIdx] is { } portrait)
            {
                return portrait;
            }
            return Illustration;
        }
    }

    private void PreparePhoneBook(CGSize viewport)
    {
        // Positions phone book window elements within a 652x426 frame, placing family entries at (34, 77)
        // and contact entries at (351, 77) across 10 visible rows.
        const double width = 652;
        const double height = 426;
        Rect = new CGRect(Math.Floor((viewport.Width - width) / 2.0), Math.Floor((viewport.Height - height) / 2.0), width, height);
        double rowHeight = Font.LineHeight + 3;
        const int visibleCount = PhoneBookVisibleRows;
        double listHeight = Math.Min(275, visibleCount * rowHeight);
        PhoneBookFamilyRect = new CGRect(Rect.MinX + 34, Rect.MinY + 77, 280, listHeight);
        PhoneBookContactRect = new CGRect(Rect.MinX + 351, Rect.MinY + 77, 280, listHeight);
        ImageRect = new CGRect(Rect.MinX + 10, Rect.MinY + 10, 45, 45);

        var families = Dialog.PhoneBook ?? [];
        if (families.Count == 0)
        {
            _selectedFamily = 0;
            _selectedContact = null;
        }
        else
        {
            _selectedFamily = Math.Clamp(_selectedFamily, 0, families.Count - 1);
        }

        IReadOnlyList<GameDialog.PhoneBookContact> contacts =
            _selectedFamily >= 0 && _selectedFamily < families.Count ? families[_selectedFamily].Contacts : [];
        if (contacts.Count == 0)
            _selectedContact = null;
        else if (_selectedContact is int selContact)
            _selectedContact = Math.Clamp(selContact, 0, contacts.Count - 1);
        else
            _selectedContact = 0;

        int maxFamilyScroll = Math.Max(0, families.Count - visibleCount);
        int maxContactScroll = Math.Max(0, contacts.Count - visibleCount);
        _familyScroll = Math.Clamp(_familyScroll, 0, maxFamilyScroll);
        _contactScroll = Math.Clamp(_contactScroll, 0, maxContactScroll);

        bool hasFamilyScrollbar = families.Count > visibleCount;
        bool hasContactScrollbar = contacts.Count > visibleCount;
        double familyRowWidth = hasFamilyScrollbar
            ? (PhoneBookFamilyRect.Width - ScrollbarReservedWidth)
            : (PhoneBookFamilyRect.Width - 4);
        double contactRowWidth = hasContactScrollbar
            ? (PhoneBookContactRect.Width - ScrollbarReservedWidth)
            : (PhoneBookContactRect.Width - 4);

        var familyRows = new List<(Image Image, CGRect Rect, bool Selected)>();
        int familyEnd = Math.Min(families.Count, _familyScroll + visibleCount);
        for (int index = _familyScroll; index < familyEnd; index++)
        {
            int row = index - _familyScroll;
            bool selected = index == _selectedFamily;
            var rowRect = new CGRect(
                PhoneBookFamilyRect.MinX,
                PhoneBookFamilyRect.MinY + row * rowHeight,
                familyRowWidth,
                rowHeight);
            bool isHovered = _lastHoverLocal is { } hoverLoc && rowRect.Contains(hoverLoc);
            var labelSet = _phoneBookFamilyLabels[index];
            var label = selected ? labelSet.Selected : isHovered ? labelSet.Hover : labelSet.Normal;
            familyRows.Add((label, rowRect, selected));
        }
        _phoneBookFamilyRows = familyRows;

        var contactRows = new List<(Image Image, CGRect Rect, bool Selected)>();
        int contactEnd = Math.Min(contacts.Count, _contactScroll + visibleCount);
        for (int index = _contactScroll; index < contactEnd; index++)
        {
            int row = index - _contactScroll;
            bool selected = index == _selectedContact;
            var rowRect = new CGRect(
                PhoneBookContactRect.MinX,
                PhoneBookContactRect.MinY + row * rowHeight,
                contactRowWidth,
                rowHeight);
            bool isHovered = _lastHoverLocal is { } hoverLoc && rowRect.Contains(hoverLoc);
            var labelSet = _phoneBookContactLabels[_selectedFamily][index];
            var label = selected ? labelSet.Selected : isHovered ? labelSet.Hover : labelSet.Normal;
            contactRows.Add((label, rowRect, selected));
        }
        _phoneBookContactRows = contactRows;

        if (hasFamilyScrollbar)
        {
            _phoneBookFamilyScrollbarRect = new CGRect(
                PhoneBookFamilyRect.MaxX - ScrollbarWidth,
                PhoneBookFamilyRect.MinY,
                ScrollbarWidth,
                PhoneBookFamilyRect.Height);
            AppendVerticalScrollbar(_phoneBookFamilyScrollbarRect, _familyScroll, maxFamilyScroll);
        }
        if (hasContactScrollbar)
        {
            _phoneBookContactScrollbarRect = new CGRect(
                PhoneBookContactRect.MaxX - ScrollbarWidth,
                PhoneBookContactRect.MinY,
                ScrollbarWidth,
                PhoneBookContactRect.Height);
            AppendVerticalScrollbar(_phoneBookContactScrollbarRect, _contactScroll, maxContactScroll);
        }

        string bannerText;
        if (_selectedContact is int activeContact
            && _selectedFamily >= 0 && _selectedFamily < families.Count
            && activeContact >= 0 && activeContact < contacts.Count)
        {
            bannerText = $"{_phoneBookCallVerbText} {contacts[activeContact].Name} {families[_selectedFamily].Name}?";
        }
        else
        {
            bannerText = _phoneBookEmptyBannerText;
        }

        if (!string.Equals(_cachedPhoneBookBannerText, bannerText, StringComparison.Ordinal))
        {
            PhoneBookBannerLabel = Label(bannerText, TitleFont, Device, DefaultTextColor);
            _cachedPhoneBookBannerText = bannerText;
        }

        // Calculates phone book action button layouts, enforcing a 100px minimum width with 20px text
        // padding and anchoring Call on the left and Cancel on the right.
        const double buttonHeight = 33;
        double buttonY = Rect.MaxY - Math.Floor(buttonHeight * 3.0 / 2.0);
        var buttonRects = new List<CGRect>(Labels.Count);
        for (int index = 0; index < Labels.Count; index++)
        {
            double buttonWidth = Math.Max(100, Math.Ceiling(Labels[index].Size.Width) + 20);
            double x = index == 0 ? Rect.MinX + 20 : Rect.MaxX - buttonWidth - 20;
            buttonRects.Add(new CGRect(x, buttonY, buttonWidth, buttonHeight));
        }
        _buttonRects = buttonRects;

        // Centers the 33x23 phone icon horizontally within the call button and offsets it 5 pixels from the
        // top edge.
        CGSize iconSize = PhoneIcon?.Size ?? new CGSize(33, 23);
        PhoneIconRect = new CGRect(
            Rect.MinX + Math.Floor((Rect.Width - 23) / 2.0),
            buttonY + 5,
            iconSize.Width,
            iconSize.Height);
    }

    public IReadOnlyList<(Image Image, CGRect Rect)> VisibleLines
    {
        get
        {
            int lineH = Math.Max(1, MessageBodyFont.LineHeight);
            int count = Math.Max(1, (int)BodyRect.Height / lineH);
            int start = Math.Clamp(_scroll, 0, _message.Count);
            int take = Math.Min(count, _message.Count - start);
            var lines = new List<(Image Image, CGRect Rect)>(take);
            bool centerShortPrompt = Illustration is null
                && _message.Count <= 2
                && _messageScrollbarRect.Width <= 0
                && Dialog.Kind != 3;
            for (int offset = 0; offset < take; offset++)
            {
                int lineIndex = start + offset;
                var image = _message[lineIndex];
                bool isSide = lineIndex < _messageSideFlags.Count && _messageSideFlags[lineIndex];
                double minX = centerShortPrompt
                    ? BodyRect.MinX + Math.Floor((BodyRect.Width - image.Size.Width) / 2.0)
                    : (isSide ? _messageSideOriginX : _messageFullOriginX);
                lines.Add((image, new CGRect(minX, BodyRect.MinY + offset * lineH, image.Size.Width, image.Size.Height)));
            }
            return lines;
        }
    }

    public bool Contains(CGPoint point, double scale) =>
        Rect.Contains(new CGPoint(point.X / scale, point.Y / scale));

    public void BlurInput()
    {
        if (!InputFocused)
            return;
        InputFocused = false;
        RefreshInputLabel();
    }

    public bool BeginButtonPress(CGPoint point, double scale)
    {
        if (Submitting)
            return false;

        var local = new CGPoint(point.X / scale, point.Y / scale);
        _lastHoverLocal = local;

        if (Dialog.IsBookScreen)
        {
            if (CanBookBack && BookBackRect.Contains(local))
            {
                BookBackPressed = true;
                BookBackHovered = true;
                BookNextPressed = false;
                Pressed = null;
                return true;
            }
            if (CanBookNext && BookNextRect.Contains(local))
            {
                BookNextPressed = true;
                BookNextHovered = true;
                BookBackPressed = false;
                Pressed = null;
                return true;
            }
        }

        for (int index = 0; index < _buttonRects.Count; index++)
        {
            if (!_buttonRects[index].Contains(local))
                continue;

            bool disabledCall = Dialog.Kind == 10 && index == 0 && SelectedPhoneBookContact() is null;
            if (disabledCall)
                return false;

            Pressed = index;
            Hovered = index;
            BookBackPressed = false;
            BookNextPressed = false;
            return true;
        }

        return false;
    }

    public bool EndButtonPress()
    {
        bool wasPressed = Pressed is not null || BookBackPressed || BookNextPressed;
        Pressed = null;
        BookBackPressed = false;
        BookNextPressed = false;
        return wasPressed;
    }

    public void ClearHover()
    {
        _lastHoverLocal = null;
        Hovered = null;
        TutorialCloseBoxHovered = false;
        BookBackHovered = false;
        BookNextHovered = false;
        BookHoveredRowIndex = null;
        BookIngredientTooltip = null;
    }

    public void Hover(CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        _lastHoverLocal = local;

        int? hoveredButton = null;
        for (int i = 0; i < _buttonRects.Count; i++)
        {
            if (_buttonRects[i].Contains(local))
            {
                hoveredButton = i;
                break;
            }
        }
        Hovered = hoveredButton;
        TutorialCloseBoxHovered = TutorialCloseBoxRect.Contains(local);

        if (Dialog.IsBookScreen)
        {
            int pageCount = Math.Max(1, ((_bookCatalog?.Entries.Count ?? 0) + 3) / 4);
            BookBackHovered = BookPage > 0 && BookBackRect.Contains(local);
            BookNextHovered = (BookPage + 1 < pageCount) && BookNextRect.Contains(local);

            int? hoveredRow = null;
            BookIngredientSlot? foundSlot = null;
            foreach (var row in _bookRows)
            {
                if (hoveredRow is null && row.Rect.Contains(local))
                    hoveredRow = row.Index;

                if (foundSlot is null)
                {
                    foreach (var slot in row.Ingredients)
                    {
                        if (slot.Rect.Contains(local))
                        {
                            foundSlot = slot;
                            break;
                        }
                    }
                }
            }
            BookHoveredRowIndex = hoveredRow;

            if (foundSlot is not null)
            {
                if (!string.Equals(_cachedBookTooltipText, foundSlot.Name, StringComparison.Ordinal) || BookIngredientTooltip is null)
                {
                    try
                    {
                        var tipImage = Label(foundSlot.Name, TooltipFont, Device, (0, 0, 0));
                        double width = tipImage.Size.Width + 8;
                        double height = tipImage.Size.Height + 6;
                        var tipRect = new CGRect(
                            Math.Floor(foundSlot.Rect.MidX - width / 2.0),
                            Math.Floor(foundSlot.Rect.MinY - height - 2),
                            width,
                            height);
                        BookIngredientTooltip = (tipImage, tipRect);
                        _cachedBookTooltipText = foundSlot.Name;
                    }
                    catch
                    {
                    }
                }
                else if (BookIngredientTooltip is { } existing)
                {
                    double width = existing.Rect.Width;
                    double height = existing.Rect.Height;
                    BookIngredientTooltip = (
                        existing.Image,
                        new CGRect(
                            Math.Floor(foundSlot.Rect.MidX - width / 2.0),
                            Math.Floor(foundSlot.Rect.MinY - height - 2),
                            width,
                            height));
                }
            }
            else
            {
                BookIngredientTooltip = null;
                _cachedBookTooltipText = null;
            }
        }
        else
        {
            BookBackHovered = false;
            BookNextHovered = false;
            BookHoveredRowIndex = null;
            BookIngredientTooltip = null;
        }
    }

    public byte? Choice(CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        if (!Submitting && TutorialCloseBoxRect.Contains(local))
            return 0;

        if (Dialog.Kind == 3)
        {
            bool wasFocused = InputFocused;
            InputFocused = InputRect.Contains(local);
            if (wasFocused != InputFocused)
                RefreshInputLabel();
        }

        int visibleCount = Math.Max(1, (int)BodyRect.Height / Math.Max(1, MessageBodyFont.LineHeight));
        int maxMsgScroll = Math.Max(0, _message.Count - visibleCount);
        if (ApplyScrollbarClick(local, _messageScrollbarRect, ref _scroll, maxMsgScroll, visibleCount))
            return null;

        if (Dialog.IsBookScreen)
        {
            int pageCount = Math.Max(1, ((_bookCatalog?.Entries.Count ?? 0) + 3) / 4);
            foreach (var row in _bookRows)
            {
                if (row.Rect.Contains(local))
                {
                    SelectBook(row.Index);
                    return null;
                }
            }
            if (BookBackRect.Contains(local) && BookPage > 0)
            {
                BookPage -= 1;
                SelectBook(BookPage * 4);
                return null;
            }
            if (BookNextRect.Contains(local) && BookPage + 1 < pageCount)
            {
                BookPage += 1;
                SelectBook(BookPage * 4);
                return null;
            }
        }

        if (Submitting)
            return null;

        for (int index = 0; index < _buttonRects.Count; index++)
        {
            if (_buttonRects[index].Contains(local))
                return Dialog.Buttons[index].Code;
        }
        return null;
    }

    public bool AcceptsTextInput => Dialog.Kind == 3 && !Submitting && (Dialog.Blocking || InputFocused);

    public void MoveBookSelection(int delta)
    {
        if (!Dialog.IsBookScreen || _bookCatalog?.Entries is not { Count: > 0 } entries)
            return;

        SelectBook(Math.Clamp(BookSelectedIndex + delta, 0, entries.Count - 1));
        BookPage = BookSelectedIndex / 4;
    }

    private void SelectBook(int index)
    {
        if (index == BookSelectedIndex
            || _bookCatalog?.Entries is not { } entries
            || BookSelectedIndex < 0 || BookSelectedIndex >= entries.Count
            || index < 0 || index >= entries.Count)
        {
            BookSelectedIndex = index;
            _cachedBookDetailKey = null;
            return;
        }

        if (Dialog.BookMode == 0)
            _bookAvailabilityContextItemID = entries[BookSelectedIndex].ResourceID;

        BookSelectedIndex = index;
        _cachedBookDetailKey = null;
    }

    public void MoveBookPage(int delta)
    {
        if (!Dialog.IsBookScreen)
            return;

        int pageCount = Math.Max(1, ((_bookCatalog?.Entries.Count ?? 0) + 3) / 4);
        BookPage = Math.Clamp(BookPage + delta, 0, pageCount - 1);
        SelectBook(BookPage * 4);
    }

    public PhoneBookAction? PhoneBookActionAt(CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        const int visibleCount = PhoneBookVisibleRows;
        int maxFamilyScroll = Math.Max(0, (Dialog.PhoneBook?.Count ?? 0) - visibleCount);
        if (ApplyScrollbarClick(local, _phoneBookFamilyScrollbarRect, ref _familyScroll, maxFamilyScroll, visibleCount))
            return new PhoneBookAction.ChangedSelection();

        int maxContactScroll = Math.Max(0, (PhoneBookFamilyAt(_selectedFamily)?.Contacts.Count ?? 0) - visibleCount);
        if (ApplyScrollbarClick(local, _phoneBookContactScrollbarRect, ref _contactScroll, maxContactScroll, visibleCount))
            return new PhoneBookAction.ChangedSelection();

        for (int row = 0; row < _phoneBookFamilyRows.Count; row++)
        {
            if (!_phoneBookFamilyRows[row].Rect.Contains(local))
                continue;

            int index = _familyScroll + row;
            if (index == _selectedFamily)
                return null;

            _selectedFamily = index;
            _selectedContact = PhoneBookFamilyAt(index)?.Contacts.Count > 0 ? 0 : null;
            _contactScroll = 0;
            return new PhoneBookAction.ChangedSelection();
        }

        for (int row = 0; row < _phoneBookContactRows.Count; row++)
        {
            if (!_phoneBookContactRows[row].Rect.Contains(local))
                continue;

            int index = _contactScroll + row;
            if (PhoneBookFamilyAt(_selectedFamily) is not { } family || index < 0 || index >= family.Contacts.Count)
                return null;

            _selectedContact = index;
            return new PhoneBookAction.ChangedSelection();
        }

        for (int button = 0; button < _buttonRects.Count; button++)
        {
            if (!_buttonRects[button].Contains(local))
                continue;

            if (Dialog.Buttons[button].Code == 0)
                return new PhoneBookAction.Cancel();

            if (_selectedContact is int activeContact
                && PhoneBookFamilyAt(_selectedFamily) is { } family
                && activeContact >= 0 && activeContact < family.Contacts.Count)
            {
                return new PhoneBookAction.Call(family.Contacts[activeContact].NeighborId);
            }
        }

        return null;
    }

    public void MovePhoneBookSelection(int delta)
    {
        if (PhoneBookFamilyAt(_selectedFamily) is not { Contacts.Count: > 0 } family)
            return;

        int nextContact = Math.Clamp((_selectedContact ?? 0) + delta, 0, family.Contacts.Count - 1);
        _selectedContact = nextContact;
        const int visibleCount = PhoneBookVisibleRows;
        if (nextContact < _contactScroll)
            _contactScroll = nextContact;
        else if (nextContact >= _contactScroll + visibleCount)
            _contactScroll = nextContact - visibleCount + 1;
    }

    public void MovePhoneBookFamily(int delta)
    {
        if (Dialog.PhoneBook is not { Count: > 0 } families)
            return;

        _selectedFamily = Math.Clamp(_selectedFamily + delta, 0, families.Count - 1);
        _selectedContact = PhoneBookFamilyAt(_selectedFamily)?.Contacts.Count > 0 ? 0 : null;
        _contactScroll = 0;
        const int visibleCount = PhoneBookVisibleRows;
        if (_selectedFamily < _familyScroll)
            _familyScroll = _selectedFamily;
        else if (_selectedFamily >= _familyScroll + visibleCount)
            _familyScroll = _selectedFamily - visibleCount + 1;
    }

    public int? SelectedPhoneBookContact()
    {
        if (PhoneBookFamilyAt(_selectedFamily) is not { } family
            || _selectedContact is not int idx
            || idx < 0 || idx >= family.Contacts.Count)
        {
            return null;
        }
        return family.Contacts[idx].NeighborId;
    }

    public void ScrollPhoneBook(float delta, CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        int amount = Math.Max(1, Math.Abs((int)Math.Round(delta, MidpointRounding.AwayFromZero)));
        const int visibleCount = PhoneBookVisibleRows;
        if (PhoneBookFamilyRect.Contains(local))
        {
            int count = Dialog.PhoneBook?.Count ?? 0;
            int step = delta > 0 ? -amount : amount;
            _familyScroll = Math.Clamp(_familyScroll + step, 0, Math.Max(0, count - visibleCount));
        }
        else
        {
            int count = PhoneBookFamilyAt(_selectedFamily)?.Contacts.Count ?? 0;
            int step = delta > 0 ? -amount : amount;
            _contactScroll = Math.Clamp(_contactScroll + step, 0, Math.Max(0, count - visibleCount));
        }
    }

    public IReadOnlyList<(string Label, CGRect Rect)> PhoneBookAccessibilityControls
    {
        get
        {
            if (Dialog.Kind != 10)
                return [];

            var result = new List<(string Label, CGRect Rect)>();
            var families = Dialog.PhoneBook ?? [];
            for (int i = 0; i < _phoneBookFamilyRows.Count && _familyScroll + i < families.Count; i++)
                result.Add((families[_familyScroll + i].Name, _phoneBookFamilyRows[i].Rect));

            var contacts = PhoneBookFamilyAt(_selectedFamily)?.Contacts ?? [];
            for (int i = 0; i < _phoneBookContactRows.Count && _contactScroll + i < contacts.Count; i++)
                result.Add((contacts[_contactScroll + i].Name, _phoneBookContactRows[i].Rect));

            for (int index = 0; index < Dialog.Buttons.Count && index < _buttonRects.Count; index++)
                result.Add((Dialog.Buttons[index].Label, _buttonRects[index]));

            const double btnH = ScrollbarButtonHeight;
            if (_phoneBookFamilyScrollbarRect.Width > 0)
            {
                result.Add(("Scroll Families Up", new CGRect(
                    _phoneBookFamilyScrollbarRect.MinX,
                    _phoneBookFamilyScrollbarRect.MinY,
                    _phoneBookFamilyScrollbarRect.Width,
                    btnH)));
                result.Add(("Scroll Families Down", new CGRect(
                    _phoneBookFamilyScrollbarRect.MinX,
                    _phoneBookFamilyScrollbarRect.MaxY - btnH,
                    _phoneBookFamilyScrollbarRect.Width,
                    btnH)));
            }
            if (_phoneBookContactScrollbarRect.Width > 0)
            {
                result.Add(("Scroll Contacts Up", new CGRect(
                    _phoneBookContactScrollbarRect.MinX,
                    _phoneBookContactScrollbarRect.MinY,
                    _phoneBookContactScrollbarRect.Width,
                    btnH)));
                result.Add(("Scroll Contacts Down", new CGRect(
                    _phoneBookContactScrollbarRect.MinX,
                    _phoneBookContactScrollbarRect.MaxY - btnH,
                    _phoneBookContactScrollbarRect.Width,
                    btnH)));
            }
            return result;
        }
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls()
    {
        if (Dialog.Kind == 10)
            return PhoneBookAccessibilityControls;

        var result = new List<(string Label, CGRect Rect)>();
        if (Dialog.IsBookScreen)
        {
            foreach (var row in _bookRows)
            {
                result.Add((row.Title, row.Rect));
                foreach (var slot in row.Ingredients)
                    result.Add((slot.Name, slot.Rect));
            }
            if (_bookCatalog is not null)
            {
                result.Add((_bookCatalog.Back, BookBackRect));
                result.Add((_bookCatalog.Next, BookNextRect));
            }
        }

        for (int index = 0; index < Dialog.Buttons.Count && index < _buttonRects.Count; index++)
        {
            var button = Dialog.Buttons[index];
            string buttonText = Dialog.IsBookScreen ? (_bookCatalog?.Ok ?? button.Label) : button.Label;
            result.Add((buttonText, _buttonRects[index]));
            if (!string.Equals(buttonText, button.Label, StringComparison.Ordinal))
                result.Add((button.Label, _buttonRects[index]));
        }

        if (_messageScrollbarRect.Width > 0)
        {
            const double btnH = ScrollbarButtonHeight;
            result.Add(("Scroll Up", new CGRect(
                _messageScrollbarRect.MinX,
                _messageScrollbarRect.MinY,
                _messageScrollbarRect.Width,
                btnH)));
            result.Add(("Scroll Down", new CGRect(
                _messageScrollbarRect.MinX,
                _messageScrollbarRect.MaxY - btnH,
                _messageScrollbarRect.Width,
                btnH)));
        }

        if (TutorialCloseBox is not null)
            result.Add(("Close", TutorialCloseBoxRect));

        return result;
    }

    private GameDialog.PhoneBookFamily? PhoneBookFamilyAt(int index)
    {
        if (Dialog.PhoneBook is not { } phoneBook || index < 0 || index >= phoneBook.Count)
            return null;
        return phoneBook[index];
    }

    public void ScrollMessage(float delta)
    {
        int visibleCount = Math.Max(1, (int)BodyRect.Height / Math.Max(1, MessageBodyFont.LineHeight));
        int rounded = (int)Math.Round(delta, MidpointRounding.AwayFromZero);
        _scroll = Math.Clamp(_scroll + rounded, 0, Math.Max(0, _message.Count - visibleCount));
    }

    private void RefreshInputLabel()
    {
        string display = AcceptsTextInput ? Text + "|" : Text;
        Input = Label(display, InputFont, Device, DefaultTextColor);
    }

    public void UpdateText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Limits text input to a maximum length of 64 characters.
        if (value.Length > 64)
            return;
        foreach (char c in value)
        {
            if (c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029' or '\0')
                return;
        }
        Text = value;
        RefreshInputLabel();
    }
}
