using System.Numerics;
using System.Text.Json.Serialization;
using SimsSDL;

namespace SimsSDL.UI;

public sealed record PieMenuPreview
{
    [JsonPropertyName("entries")]
    public required IReadOnlyList<SimInteraction> Entries { get; init; }

    [JsonPropertyName("objectID")]
    public required int ObjectID { get; init; }

    [JsonPropertyName("selectedSim")]
    public required int SelectedSim { get; init; }

    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    public static PieMenuPreview Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ContentError($"Missing pie menu fixture: {path}");
        }
        if (info.Length > 1024 * 1024)
        {
            throw new ContentError("Pie menu fixture exceeds size limit");
        }

        byte[] data = File.ReadAllBytes(path);
        var fixture = JsonSerializer.Deserialize<PieMenuPreview>(data)
            ?? throw new ContentError("Invalid pie menu fixture");
        if (fixture.Entries is null || !double.IsFinite(fixture.X) || !double.IsFinite(fixture.Y))
        {
            throw new ContentError("Invalid pie menu position");
        }

        return fixture;
    }
}

public sealed class OriginalPieOverlay
{
    public const int PageSize = 12;

    public readonly record struct Label(
        UITexture Background,
        IReadOnlyList<UITexture> States,
        CGSize Size);

    private readonly CGPoint _rootAnchor;
    private readonly List<int> _path = [];
    private int _pageOffset;
    private int _currentLevelCount;
    private double _deadzoneAngle;
    private bool _hadHighlightedSpoke;

    public int ObjectID { get; }
    public int SelectedSim { get; }
    public IReadOnlyList<OriginalPieNode> Roots { get; }
    public SimsBitmapFont Font { get; }
    public UIDevice Device { get; }
    public CGPoint Anchor { get; private set; }
    public IReadOnlyList<OriginalPieNode> Nodes { get; private set; } = [];
    public IReadOnlyList<Label> Labels { get; private set; } = [];
    public Label? CenterLabel { get; private set; }
    public int? Highlighted { get; set; }
    public OriginalPieLayout? Layout { get; private set; }

    /// <summary>
    /// Yaw and pitch, in degrees, of the head drawn in the middle, turned toward the pointer as in the original.
    /// </summary>
    public Vector2 HeadOrientation { get; private set; } = new(0f, 8f);
    public bool CursorInDeadzone { get; private set; } = true;

    public OriginalPieOverlay(
        IReadOnlyList<SimInteraction> entries,
        int objectID,
        int selectedSim,
        CGPoint anchor,
        SimsBitmapFont font,
        UIDevice device)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(font);
        Roots = OriginalPieNode.Build(entries);
        if (Roots.Count == 0)
        {
            throw new ContentError("Interaction menu has no available labels");
        }

        ObjectID = objectID;
        SelectedSim = selectedSim;
        Anchor = anchor;
        _rootAnchor = anchor;
        Font = font;
        Device = device;
        Rebuild();
    }

    private UITexture CreateTexture(int width, int height, byte[] pixels) =>
        OriginalGameplayOverlay.CreateImage(Device, width, height, pixels).Texture;

    private Label MakeLabel(string text, ref int budget)
    {
        int width = Math.Max(1, Font.TextWidth(text) + 6);
        int height = Font.LineHeight + 6;
        budget -= width * height * 16;
        if (budget < 0)
        {
            throw new ContentError("Pie menu texture memory limit exceeded");
        }

        var background = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int edge = Math.Min(y, height - 1 - y);
            int margin = edge == 0 ? 5 : edge == 1 ? 3 : edge == 2 ? 2 : edge < 5 ? 1 : 0;
            for (int x = margin; x < width - margin; x++)
            {
                int offset = (y * width + x) * 4;
                background[offset] = 0;
                background[offset + 1] = 40;
                background[offset + 2] = 140;
                background[offset + 3] = 255;
            }
        }

        // cLiveModePanel::Init (005186da.c lines 48-57, 76-78) & cWinPieMenu::Draw (005eae1b.c lines 36-64):
        // normal (+0xd8) = RGB(165, 195, 214), hover (+0xdc) = RGB(0, 255, 255), active/submenu header (+0xe0) = RGB(255, 255, 255).
        ReadOnlySpan<(byte R, byte G, byte B)> colors = [(165, 195, 214), (0, 255, 255), (255, 255, 255)];
        var states = new UITexture[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var image = Font.Rasterize(text, colors[i]);
            states[i] = CreateTexture(image.Width, image.Height, image.Rgba);
        }

        return new Label(CreateTexture(width, height, background), states, new CGSize(width, height));
    }

    private void Rebuild()
    {
        IReadOnlyList<OriginalPieNode> current = Roots;
        string? activeParentLabel = null;
        foreach (int index in _path)
        {
            activeParentLabel = current[index].Label;
            current = current[index].Children;
        }

        _currentLevelCount = current.Count;
        if (_pageOffset >= current.Count)
        {
            _pageOffset = 0;
        }

        int take = Math.Min(PageSize, Math.Max(0, current.Count - _pageOffset));
        var pageNodes = new OriginalPieNode[take];
        for (int i = 0; i < take; i++)
        {
            pageNodes[i] = current[_pageOffset + i];
        }

        int budget = 64 * 1024 * 1024;
        var next = new Label[pageNodes.Length];
        for (int i = 0; i < pageNodes.Length; i++)
        {
            next[i] = MakeLabel(pageNodes[i].Label, ref budget);
        }

        Label? nextCenter = activeParentLabel is not null ? MakeLabel(activeParentLabel, ref budget) : null;
        Nodes = pageNodes;
        Labels = next;
        CenterLabel = nextCenter;
        Highlighted = null;
        CursorInDeadzone = true;
        _deadzoneAngle = 0.0;
        Layout = null;
    }

    public void Prepare(CGSize canvas, double scale)
    {
        var sizes = new CGSize[Labels.Count];
        for (int i = 0; i < Labels.Count; i++)
        {
            sizes[i] = Labels[i].Size;
        }

        Layout = new OriginalPieLayout(
            Anchor,
            sizes,
            new CGSize(canvas.Width / scale, Math.Max(1.0, canvas.Height / scale - 150.0)),
            centerSize: CenterLabel?.Size,
            submenu: _path.Count > 0);
        Anchor = Layout.Center;
    }

    public bool HasMultiplePages => _currentLevelCount > PageSize;

    public bool NextPage()
    {
        // 0048d6b6.c lines 192, 323-326: Tab advances the 12-item window (+100) by 12 and wraps to 0.
        if (_currentLevelCount <= PageSize)
        {
            return false;
        }

        int nextOffset = _pageOffset + PageSize;
        _pageOffset = nextOffset < _currentLevelCount ? nextOffset : 0;
        Rebuild();
        return true;
    }

    public void Hover(CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        Highlighted = Layout?.Hit(local);
        if (Layout?.Center is not { } center)
        {
            return;
        }

        double dx = local.X - center.X;
        double dy = local.Y - center.Y;
        double radius = double.Hypot(dx, dy);
        const double innerRadius = 35.0;
        CursorInDeadzone = Highlighted is null && radius <= innerRadius;

        // 005eb598.c lines 58-63 & 004623e6.c lines 89-97: cursor angle is 0 inside the 35px deadzone and atan2(dx, -dy) outside;
        // base cursor-follow contribution scales by sqrt(radius / 2000), with +cos(theta) tilting UP (+pitch).
        double theta = radius > innerRadius ? Math.Atan2(dx, -dy) : 0.0;
        double cursorScale = radius > innerRadius ? Math.Sqrt(radius / 2000.0) : 0.0;
        double yawRad = Math.Sin(theta) * cursorScale;
        double pitchRad = Math.Cos(theta) * cursorScale;

        // 004623e6.c lines 56-88, 213-248: when a spoke is hovered, map (highlighted, count) to compass direction 0..7
        // and add cardinal (0.6 rad) or diagonal (0.4 rad) spoke snap offset.
        if (Highlighted is { } index)
        {
            _hadHighlightedSpoke = true;
            _deadzoneAngle = 0.0;
            int count = Labels.Count;
            int direction = count < 2
                ? 0
                : count == 2
                    ? (index & 1) << 2
                    : count < 5
                        ? (index & 3) * 2
                        : index > 7
                            ? (index & 1) << 2
                            : index;

            switch (direction)
            {
                case 0:
                    pitchRad += 0.6;
                    break;
                case 1:
                    yawRad += 0.4;
                    pitchRad += 0.4;
                    break;
                case 2:
                    yawRad += 0.6;
                    break;
                case 3:
                    yawRad += 0.4;
                    pitchRad -= 0.4;
                    break;
                case 4:
                    pitchRad -= 0.6;
                    break;
                case 5:
                    yawRad -= 0.4;
                    pitchRad -= 0.4;
                    break;
                case 6:
                    yawRad -= 0.6;
                    break;
                case 7:
                    yawRad -= 0.4;
                    pitchRad += 0.4;
                    break;
            }
        }
        else if (radius <= innerRadius)
        {
            // Keep the head facing forward on open and inside the 35px center deadzone; damp back toward 0 after leaving a spoke.
            if (_hadHighlightedSpoke)
            {
                _deadzoneAngle *= 0.85;
                if (Math.Abs(_deadzoneAngle) < 1e-4)
                {
                    _deadzoneAngle = 0.0;
                    _hadHighlightedSpoke = false;
                }
            }
            else
            {
                _deadzoneAngle = 0.0;
            }

            yawRad = _deadzoneAngle;
            pitchRad = 0.0;
        }
        else
        {
            _deadzoneAngle = 0.0;
        }

        const double degrees = 180.0 / Math.PI;
        HeadOrientation = new Vector2(
            (float)Math.Clamp(yawRad * degrees, -60.0, 60.0),
            (float)Math.Clamp(8.0 + pitchRad * degrees, -45.0, 60.0));
    }

    /// <summary>
    /// 004623e6.c lines 262-277, 343-346: damps any residual center-deadzone head angle smoothly back to center.
    /// </summary>
    public bool TickDeadzoneRotation()
    {
        if (!CursorInDeadzone || Math.Abs(_deadzoneAngle) <= 1e-4)
        {
            return false;
        }

        _deadzoneAngle *= 0.85;
        if (Math.Abs(_deadzoneAngle) <= 1e-4)
        {
            _deadzoneAngle = 0.0;
            _hadHighlightedSpoke = false;
        }

        const double degrees = 180.0 / Math.PI;
        HeadOrientation = new Vector2(
            (float)Math.Clamp(_deadzoneAngle * degrees, -60.0, 60.0),
            8f);
        return true;
    }

    /// <summary>
    /// The screen point at the middle of the shown entry with this label, for scripted input; <see cref="Prepare"/> must have run.
    /// </summary>
    public CGPoint? Point(string label, double scale)
    {
        int index = -1;
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (string.Equals(Nodes[i].Label, label, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0 || Layout is null || index >= Layout.Rectangles.Count)
        {
            return null;
        }

        var rect = Layout.Rectangles[index];
        return new CGPoint(rect.MidX * scale, rect.MidY * scale);
    }

    public (SimInteraction? Action, bool KeepOpen) Choose(CGPoint point, double scale)
    {
        var local = new CGPoint(point.X / scale, point.Y / scale);
        // 005eb598.c lines 22-34: explicit label rectangle hits are checked before the inner/outer radius ring check.
        int? hitIndex = Layout?.Hit(local);
        if (hitIndex is null && Layout?.Center is { } center && double.Hypot(local.X - center.X, local.Y - center.Y) <= 35.0 && _path.Count > 0)
        {
            _path.RemoveAt(_path.Count - 1);
            if (_path.Count == 0)
            {
                Anchor = _rootAnchor;
            }
            _pageOffset = 0;
            Rebuild();
            return (null, true);
        }

        if (hitIndex is not { } index || index < 0 || index >= Nodes.Count)
        {
            return (null, false);
        }

        if (Nodes[index].Action is { } action)
        {
            return (action, false);
        }

        // 0048d6b6.c lines 302-309: entering a submenu anchors the submenu at the cursor position where the parent item was clicked.
        Anchor = local;
        _path.Add(_pageOffset + index);
        _pageOffset = 0;
        try
        {
            Rebuild();
        }
        catch
        {
            _path.RemoveAt(_path.Count - 1);
            throw;
        }

        return (null, true);
    }
}
