using System.Diagnostics;
using System.Numerics;
using SimsSDL;

namespace SimsSDL.UI;

/// <summary>Displays the catalog item details popup, rendering an item preview thumbnail, title, price,
/// description, and motive rating text over a 9-sliced frame.</summary>
public sealed class OriginalCatalogPopup
{
    public sealed class Item : IEquatable<Item>
    {
        public string Name { get; }
        public int Price { get; }
        public string Description { get; }
        public IReadOnlyList<string> Ratings { get; }
        public bool IsFloorPreview { get; set; }

        public Item(
            string name,
            int price,
            string description,
            IReadOnlyList<string> ratings,
            bool isFloorPreview = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Price = price;
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Ratings = ratings ?? throw new ArgumentNullException(nameof(ratings));
            IsFloorPreview = isFloorPreview;
        }

        public bool Equals(Item? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Price == other.Price
                && string.Equals(Description, other.Description, StringComparison.Ordinal)
                && IsFloorPreview == other.IsFloorPreview
                && Ratings.SequenceEqual(other.Ratings, StringComparer.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as Item);

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Name, StringComparer.Ordinal);
            hc.Add(Price);
            hc.Add(Description, StringComparer.Ordinal);
            hc.Add(IsFloorPreview);
            foreach (string rating in Ratings)
            {
                hc.Add(rating, StringComparer.Ordinal);
            }
            return hc.ToHashCode();
        }

        public static bool operator ==(Item? left, Item? right) => Equals(left, right);
        public static bool operator !=(Item? left, Item? right) => !Equals(left, right);
    }

    private readonly OriginalGameplayOverlay.Image _popupTiles;
    private readonly OriginalGameplayOverlay.Image _redPixel;
    private readonly SimsBitmapFont _placementFont;
    private readonly SimsBitmapFont _titleFont;
    private readonly SimsBitmapFont _bodyFont;
    private readonly UIDevice _device;

    private OriginalGameplayOverlay.Image? _placementErrorImage;
    private CGPoint _placementErrorPoint = CGPoint.Zero;
    private OriginalGameplayOverlay.Part? _thumbnail;
    private Item? _item;
    private double? _anchorMidX;
    private double? _hoverStartedAt;
    private double? _popupOpenedAt;
    private bool _quickTips = true;
    private Item? _cachedItem;
    private OriginalGameplayOverlay.Image? _titleImage;
    private OriginalGameplayOverlay.Image[] _descriptionImages = [];
    private OriginalGameplayOverlay.Image[] _ratingImages = [];

    public string? PlacementErrorText { get; private set; }

    public bool QuickTips
    {
        get => _quickTips;
        set
        {
            if (_quickTips != value)
            {
                _quickTips = value;
                _hoverStartedAt = null;
                _popupOpenedAt = null;
            }
        }
    }

    public bool InterfaceEffects { get; set; } = true;

    public static string Text(string name, int price, string description)
    {
        string header = price < 1 ? name : $"{name} -- ${price}";
        return $"{header}\n{description}";
    }

    public OriginalCatalogPopup(GameContent content, UIDevice device)
    {
        ArgumentNullException.ThrowIfNull(content);
        _device = device;
        _placementFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_07.ffn"));
        _titleFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        _bodyFont = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_08.ffn"));
        var bitmap = new SimsBitmap(content.Read("UIGraphics/CPanel/Backgrounds/PopupInfoTiles.bmp"));
        _popupTiles = OriginalGameplayOverlay.CreateImage(device, bitmap.Width, bitmap.Height, bitmap.Rgba, magenta: true);
        _redPixel = OriginalGameplayOverlay.CreateImage(device, 1, 1, [255, 0, 0, 255]);
    }

    public bool IsVisible => _item is not null && DelayElapsed;

    public void ClearTextCaches()
    {
        _cachedItem = null;
        _titleImage = null;
        _descriptionImages = [];
        _ratingImages = [];
        _placementErrorImage = null;
    }

    public bool IsFullyOpen
    {
        get
        {
            if (!IsVisible)
            {
                return false;
            }
            if (!InterfaceEffects || _popupOpenedAt is not { } opened)
            {
                return true;
            }
            return MonotonicNow() - opened >= 0.25;
        }
    }

    private bool DelayElapsed =>
        _hoverStartedAt is { } started && MonotonicNow() - started >= (_quickTips ? 0.0 : 0.750);

    public void Hover(Item? next, OriginalGameplayOverlay.Part? thumbnail = null, double? anchorMidX = null)
    {
        if (_item == next && SameThumbnail(_thumbnail, thumbnail))
        {
            _anchorMidX = next is null ? null : anchorMidX;
            return;
        }

        bool wasVisible = _item is not null && DelayElapsed;
        _thumbnail = thumbnail;
        _item = next;
        _anchorMidX = next is null ? null : anchorMidX;
        if (next is null)
        {
            _hoverStartedAt = null;
            _popupOpenedAt = null;
        }
        else if (!wasVisible)
        {
            double now = MonotonicNow();
            _hoverStartedAt = now;
            _popupOpenedAt = now + (_quickTips ? 0.0 : 0.750);
        }

        _cachedItem = null;
        _titleImage = null;
        _descriptionImages = [];
        _ratingImages = [];
    }

    public void ShowPlacementError(string? text, CGPoint point = default)
    {
        _placementErrorPoint = point;
        if (string.Equals(PlacementErrorText, text, StringComparison.Ordinal))
        {
            return;
        }

        PlacementErrorText = text;
        _placementErrorImage = null;
        if (text is not null)
        {
            _placementErrorImage = OriginalGameplayOverlay.CreateImage(_device, PlacementTooltip.RasterizeHiDpi(text, _placementFont));
        }
    }

    public IReadOnlyList<OriginalGameplayOverlay.Part> Parts(CGSize canvas, int? budget = null)
    {
        if (_placementErrorImage is { } errorImage)
        {
            return [new OriginalGameplayOverlay.Part(errorImage, PlacementTooltip.Rect(errorImage.Size, _placementErrorPoint, canvas))];
        }

        if (_item is not { } item || !DelayElapsed)
        {
            return [];
        }

        const double width = 559.0;
        const double textLeft = 178.0;
        int wrapWidth = (int)(width - 12.0 - textLeft);
        if (_cachedItem != item)
        {
            (byte R, byte G, byte B) labelColor = (195, 205, 205);
            string header = item.Price < 1 ? item.Name : $"{item.Name} -- ${item.Price}";
            _titleImage = OriginalGameplayOverlay.Text(header, _titleFont, _device, labelColor);

            string normalizedDesc = item.Description
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);

            if (normalizedDesc.Length == 0)
            {
                _descriptionImages = [];
            }
            else
            {
                var descLines = new List<OriginalGameplayOverlay.Image>();
                foreach (string paragraph in normalizedDesc.Split('\n', StringSplitOptions.None))
                {
                    foreach (string line in _bodyFont.WrappedLines(paragraph, wrapWidth))
                    {
                        descLines.Add(OriginalGameplayOverlay.Text(line, _bodyFont, _device, labelColor));
                    }
                }
                _descriptionImages = descLines.ToArray();
            }

            (byte R, byte G, byte B) ratingColor = (255, 255, 255);
            var ratingLines = new List<OriginalGameplayOverlay.Image>();
            foreach (string rating in item.Ratings)
            {
                foreach (string line in _bodyFont.WrappedLines(rating, wrapWidth))
                {
                    ratingLines.Add(OriginalGameplayOverlay.Text(line, _bodyFont, _device, ratingColor));
                }
            }
            _ratingImages = ratingLines.ToArray();
            _cachedItem = item;
        }

        int bodyLineCount = _descriptionImages.Length + _ratingImages.Length;
        int contentHeight = 12 + _titleFont.LineHeight + bodyLineCount * _bodyFont.LineHeight + 12;
        double height = Math.Max(127, contentHeight);
        double panelBottom = canvas.Height - 100.0;
        double elapsed = InterfaceEffects
            ? (_popupOpenedAt is { } opened ? Math.Max(0.0, MonotonicNow() - opened) : 0.25)
            : 0.25;
        double progress = Math.Min(1.0, Math.Max(0.0, elapsed / 0.25));
        double visibleHeight = Math.Round(height * progress, MidpointRounding.AwayFromZero);
        if (visibleHeight <= 0.0)
        {
            return [];
        }

        double x = OriginalUIScalingLayout.CatalogPopupOriginX(canvas.Width, popupWidth: width, anchorMidX: _anchorMidX);
        double y = panelBottom - visibleHeight;
        var result = new List<OriginalGameplayOverlay.Part>();

        double tileW = _popupTiles.Size.Width / 3.0;
        double tileH = _popupTiles.Size.Height / 3.0;
        double[] xs = [x, x + tileW, x + width - tileW, x + width];
        double[] ys = [y, y + tileH, y + height - tileH, y + height];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                var cell = new CGRect(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]);
                if (cell.MinY >= panelBottom)
                {
                    continue;
                }

                double clippedH = Math.Min(cell.MaxY, panelBottom) - cell.MinY;
                float vScale = (float)(clippedH / Math.Max(1.0, cell.Height)) / 3.0f;
                result.Add(new OriginalGameplayOverlay.Part(
                    _popupTiles,
                    new CGRect(cell.MinX, cell.MinY, cell.Width, clippedH),
                    new Vector4(col / 3.0f, row / 3.0f, 1.0f / 3.0f, vScale)));
            }
        }

        // Renders the object thumbnail directly over the popup frame without a solid background fill.
        double boxY = y + Math.Floor((height - 127.0) / 2.0);
        if (_thumbnail is { } thumbnail && thumbnail.Rect.Width > 0.0 && thumbnail.Rect.Height > 0.0)
        {
            double scale = Math.Min(1.0, Math.Min(160.0 / thumbnail.Rect.Width, 119.0 / thumbnail.Rect.Height));
            var size = new CGSize(
                Math.Round(thumbnail.Rect.Width * scale, MidpointRounding.AwayFromZero),
                Math.Round(thumbnail.Rect.Height * scale, MidpointRounding.AwayFromZero));
            double thumbX = x + Math.Floor((168.0 - size.Width) / 2.0);
            // Positions floor tile preview diamonds in the lower two-thirds of the preview area instead of
            // vertically centering them.
            double thumbY = item.IsFloorPreview ? boxY + 38.0 : boxY + Math.Floor((127.0 - size.Height) / 2.0);
            if (thumbY < panelBottom && size.Height > 0.0)
            {
                double clippedH = Math.Min(size.Height, panelBottom - thumbY);
                float uvH = thumbnail.UV.W * (float)(clippedH / size.Height);
                result.Add(new OriginalGameplayOverlay.Part(
                    thumbnail.Image,
                    new CGRect(thumbX, thumbY, size.Width, clippedH),
                    new Vector4(thumbnail.UV.X, thumbnail.UV.Y, thumbnail.UV.Z, uvH)));
            }
        }

        // Draws a 1-pixel red outline around the preview box when the item price exceeds available
        // household funds.
        if (budget is { } availableBudget && item.Price > availableBudget)
        {
            ReadOnlySpan<CGRect> redRects =
            [
                new CGRect(x + 4.0, boxY + 4.0, 160.0, 1.0),
                new CGRect(x + 4.0, boxY + 122.0, 160.0, 1.0),
                new CGRect(x + 4.0, boxY + 4.0, 1.0, 119.0),
                new CGRect(x + 163.0, boxY + 4.0, 1.0, 119.0),
            ];
            foreach (var r in redRects)
            {
                if (r.MinY >= panelBottom)
                {
                    continue;
                }
                double clippedH = Math.Min(r.MaxY, panelBottom) - r.MinY;
                result.Add(new OriginalGameplayOverlay.Part(_redPixel, new CGRect(r.MinX, r.MinY, r.Width, clippedH)));
            }
        }

        double titleY = y + 12.0;
        if (_titleImage is { } titleImage && titleY + titleImage.Size.Height <= panelBottom)
        {
            result.Add(new OriginalGameplayOverlay.Part(
                titleImage,
                new CGRect(x + textLeft, titleY, titleImage.Size.Width, titleImage.Size.Height)));
        }

        double lineY = titleY + _titleFont.LineHeight;
        foreach (var line in _descriptionImages)
        {
            if (line.Size.Width > 0.0 && lineY + line.Size.Height <= panelBottom)
            {
                result.Add(new OriginalGameplayOverlay.Part(
                    line,
                    new CGRect(x + textLeft, lineY, line.Size.Width, line.Size.Height)));
            }
            lineY += _bodyFont.LineHeight;
        }
        foreach (var line in _ratingImages)
        {
            if (line.Size.Width > 0.0 && lineY + line.Size.Height <= panelBottom)
            {
                result.Add(new OriginalGameplayOverlay.Part(
                    line,
                    new CGRect(x + textLeft, lineY, line.Size.Width, line.Size.Height)));
            }
            lineY += _bodyFont.LineHeight;
        }

        return result;
    }

    private static bool SameThumbnail(OriginalGameplayOverlay.Part? lhs, OriginalGameplayOverlay.Part? rhs) =>
        (lhs, rhs) switch
        {
            (null, null) => true,
            ({ } l, { } r) => ReferenceEquals(l.Image.Texture, r.Image.Texture)
                && l.Image.Size == r.Image.Size
                && l.Rect == r.Rect
                && l.UV == r.UV
                && l.Opacity == r.Opacity,
            _ => false,
        };

    private static double MonotonicNow() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
