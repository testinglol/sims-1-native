using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SimsSDL.Core;

public static class OriginalUIScalingLayout
{
    public readonly record struct PanelBackLayout(
        double RightEdge,
        double LeftBodyWidth,
        double StretchBodyWidth,
        double RightCapX,
        double RightCapWidth,
        int StretchSampleColumn,
        int RightCapStartColumn)
    {
        public const double PanelOriginX = 220;

        /// <summary>Width of the right corner bevel taper in the control panel background bitmap, spanning
        /// columns 794 to 803.</summary>
        public const double RightBevelCapWidth = 10;
    }

    public const float DefaultUIScaleBoost = 1.15f;

    public static float AutomaticUIScale(int width, int height, float pixelDensity)
    {
        if (width <= 0 || height <= 0)
            return MathF.Max(1f, pixelDensity) * DefaultUIScaleBoost;
        float w = width;
        float h = height;
        float fit800x600 = MathF.Min(w / 800.0f, h / 600.0f);
        float targetScale = MathF.Max(1.0f, pixelDensity) * DefaultUIScaleBoost;
        return MathF.Max(0.25f, MathF.Min(targetScale, fit800x600));
    }

    /// <summary>Computes the aspect-fit scale for non-lot full screens, bounded by uiScale * 1.15 so 2K/4K
    /// viewports stay comfortably proportioned instead of over-zooming 800x600 edge-to-edge.</summary>
    public static double FullscreenOverlayScale(CGSize canvas, float uiScale = 1.0f)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0)
            return Math.Max(0.25, uiScale);
        double fit = Math.Min(canvas.Width / 800.0, canvas.Height / 600.0);
        double capped = uiScale > 0f ? Math.Min(fit, Math.Max(0.25, uiScale * 1.15)) : fit;
        return Math.Max(0.25, capped);
    }

    public static double LivePopupOriginX(
        double anchorMidX,
        double canvasWidth,
        double popupWidth = 559,
        double minX = 220,
        double rightMargin = 8)
    {
        double maxOrigin = Math.Max(8, canvasWidth - popupWidth - rightMargin);
        double effectiveMin = Math.Min(minX, maxOrigin);
        double desired = Math.Floor(anchorMidX - popupWidth / 2.0);
        return Math.Min(Math.Max(effectiveMin, desired), maxOrigin);
    }

    /// <summary>Computes control panel background slicing so fixed-width subpanels cap cleanly after their
    /// controls while catalog and scrolling subpanels expand across the full width.</summary>
    public static PanelBackLayout GetPanelBackLayout(
        double canvasWidth,
        bool expandsToCanvas,
        double compactRightEdge = 800,
        int bitmapWidth = 804)
    {
        const double originX = PanelBackLayout.PanelOriginX;
        const double capWidth = PanelBackLayout.RightBevelCapWidth;
        int bmpWidth = Math.Max((int)capWidth + 2, bitmapWidth);
        double maxBodyCols = bmpWidth - capWidth;
        double targetRight = expandsToCanvas ? canvasWidth : Math.Min(canvasWidth, compactRightEdge);
        double clampedRight = Math.Max(originX + capWidth, targetRight);
        double totalWidth = clampedRight - originX;
        double desiredBodyWidth = Math.Max(0, totalWidth - capWidth);
        double leftBodyWidth = Math.Min(maxBodyCols, desiredBodyWidth);
        double stretchBodyWidth = Math.Max(0, desiredBodyWidth - maxBodyCols);
        double rightCapX = originX + leftBodyWidth + stretchBodyWidth;
        return new PanelBackLayout(
            RightEdge: rightCapX + capWidth,
            LeftBodyWidth: leftBodyWidth,
            StretchBodyWidth: stretchBodyWidth,
            RightCapX: rightCapX,
            RightCapWidth: capWidth,
            StretchSampleColumn: Math.Max(0, (int)maxBodyCols - 1),
            RightCapStartColumn: (int)maxBodyCols);
    }

    public static double CatalogPopupOriginX(
        double canvasWidth,
        double popupWidth = 559,
        double catalogLeft = 220)
    {
        double catalogWidth = Math.Max(popupWidth, canvasWidth - catalogLeft);
        double centered = catalogLeft + Math.Floor((catalogWidth - popupWidth) / 2.0);
        return Math.Min(Math.Max(8, centered), Math.Max(8, canvasWidth - popupWidth));
    }
}

public static class OriginalTutorialHighlight
{
    private static readonly int[] Frames = [0, 1, 2, 1];

    /// <summary>Cycles the three tutorial highlight animation frames at six frames per second.</summary>
    public static int Frame(double elapsed)
    {
        int ticks = Math.Max(0, (int)(elapsed * 1000.0) / 167);
        return Frames[ticks % Frames.Length];
    }

    /// <summary>Centers the tutorial highlight on the control with a three-pixel offset above it.</summary>
    public static CGRect? Rect(CGRect button, int textureWidth, int textureHeight)
    {
        int width = textureWidth / 3;
        if (width <= 0 || textureHeight <= 0)
            return null;
        return new CGRect(
            button.MinX + ((int)button.Width - width) / 2,
            button.MinY - (textureHeight + 3),
            width,
            textureHeight);
    }
}

public sealed record OriginalSpeedControl(
    string Asset,
    CGPoint Position,
    CGSize Size,
    int Columns,
    int Rows,
    int Speed)
{
    public const double PanelHeight = 183;

    public static readonly IReadOnlyList<OriginalSpeedControl> All =
    [
        new("UIGraphics/cpanel/Buttons/pause.bmp", new CGPoint(85, 135), new CGSize(15, 15), 4, 2, 0),
        new("UIGraphics/cpanel/Buttons/Speed1.bmp", new CGPoint(105, 135), new CGSize(11, 15), 4, 1, 1),
        new("UIGraphics/cpanel/Buttons/Speed2.bmp", new CGPoint(120, 135), new CGSize(20, 15), 4, 1, 3),
        new("UIGraphics/cpanel/Buttons/Speed3.bmp", new CGPoint(141, 135), new CGSize(32, 15), 4, 1, 10),
    ];

    public static int? Hit(CGPoint point, CGSize canvas, double scale)
    {
        if (scale <= 0)
            return null;
        var local = new CGPoint(point.X / scale, (point.Y - canvas.Height + PanelHeight * scale) / scale);
        foreach (var control in All)
        {
            if (new CGRect(control.Position, control.Size).Contains(local))
                return control.Speed;
        }
        return null;
    }

    public int TooltipIndex => Speed switch { 0 => 15, 1 => 16, 3 => 17, 10 => 18, _ => 15 };

    public int ImageId => Speed switch { 0 => 2026, 1 => 4705, 3 => 4706, 10 => 4707, _ => 2026 };

    public static int? SelectedImageId(int speed)
    {
        foreach (var control in All)
        {
            if (control.Speed == speed)
                return control.ImageId;
        }
        return null;
    }
}

public sealed record OriginalUniversalControl(
    OriginalUniversalControl.Action Kind,
    string Asset,
    string Label,
    CGPoint Position,
    CGSize Size)
{
    public enum Action
    {
        Live,
        Buy,
        Build,
        Options,
        Help,
        ZoomIn,
        ZoomOut,
        WallsUp,
        WallsCutaway,
        WallsDown,
        Story1,
        Story2,
        Roofs,
        CameraLeft,
        CameraRight,
    }

    public bool Enabled => true;

    public int TooltipIndex => Kind switch
    {
        Action.Story1 => 0,
        Action.Story2 => 1,
        Action.Roofs => 2,
        Action.CameraLeft => 3,
        Action.CameraRight => 4,
        Action.ZoomIn => 5,
        Action.ZoomOut => 6,
        Action.WallsUp => 7,
        Action.WallsCutaway => 8,
        Action.WallsDown => 9,
        Action.Live => 10,
        Action.Buy => 11,
        Action.Build => 12,
        Action.Options => 13,
        Action.Help => 23,
        _ => 0,
    };

    public int ImageId => Kind switch
    {
        Action.Live => 2010,
        Action.Buy => 2008,
        Action.Build => 2003,
        Action.Options => 2009,
        Action.Help => 4993,
        Action.ZoomIn => 2013,
        Action.ZoomOut => 2014,
        Action.WallsUp => 2006,
        Action.WallsCutaway => 2004,
        Action.WallsDown => 2007,
        Action.Story1 => 2023,
        Action.Story2 => 2024,
        Action.Roofs => 2025,
        Action.CameraLeft => 2012,
        Action.CameraRight => 2011,
        _ => 2010,
    };

    public static readonly IReadOnlyList<OriginalUniversalControl> All =
    [
        new(Action.Live, "People.bmp", "Live mode", new CGPoint(10, 6), new CGSize(50, 50)),
        new(Action.Buy, "Objects.bmp", "Buy mode", new CGPoint(69, 20), new CGSize(45, 47)),
        new(Action.Build, "Arch.bmp", "Build mode", new CGPoint(117, 45), new CGSize(39, 39)),
        new(Action.Options, "Options.bmp", "Options", new CGPoint(193, 125), new CGSize(23, 23)),
        new(Action.ZoomIn, "ZoomIn.bmp", "Zoom in", new CGPoint(26, 105), new CGSize(27, 27)),
        new(Action.ZoomOut, "ZoomOut.bmp", "Zoom out", new CGPoint(26, 149), new CGSize(27, 27)),
        new(Action.WallsUp, "NoCut.bmp", "Walls up", new CGPoint(56, 80), new CGSize(21, 21)),
        new(Action.WallsCutaway, "Dynacut.bmp", "Wall cutaway", new CGPoint(80, 85), new CGSize(21, 21)),
        new(Action.WallsDown, "NoWall.bmp", "Walls down", new CGPoint(103, 95), new CGSize(22, 15)),
        new(Action.Roofs, "LevRoof.bmp", "Roof view", new CGPoint(32, 78), new CGSize(22, 19)),
        new(Action.Story1, "Lev1.BMP", "Ground floor", new CGPoint(5, 92), new CGSize(21, 11)),
        new(Action.Story2, "Lev2.BMP", "Second floor", new CGPoint(5, 77), new CGSize(22, 15)),
        new(Action.CameraLeft, "RotLeft.bmp", "Rotate view counterclockwise", new CGPoint(43, 127), new CGSize(27, 27)),
        new(Action.CameraRight, "RotRight.bmp", "Rotate view clockwise", new CGPoint(9, 127), new CGSize(27, 27)),
        new(Action.Help, "HelpButton.BMP", "Help", new CGPoint(5, 164), new CGSize(24, 19)),
    ];

    public CGRect Rect(CGSize canvas) =>
        new CGRect(Position, Size).OffsetBy(0, canvas.Height - OriginalSpeedControl.PanelHeight);

    public CGRect Rect(CGSize canvas, CGSize? helpButtonSize)
    {
        var size = Kind == Action.Help ? (helpButtonSize ?? Size) : Size;
        return new CGRect(Position, size).OffsetBy(0, canvas.Height - OriginalSpeedControl.PanelHeight);
    }

    public static OriginalUniversalControl? Hit(CGPoint point, CGSize canvas, double scale, CGSize? helpButtonSize = null)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            return null;
        var local = new CGPoint(point.X / scale, point.Y / scale);
        var scaledCanvas = new CGSize(canvas.Width / scale, canvas.Height / scale);
        for (int i = All.Count - 1; i >= 0; i--)
        {
            var control = All[i];
            if (control.Rect(scaledCanvas, helpButtonSize).Contains(local))
                return control;
        }
        return null;
    }
}

public static class OriginalBuyLayout
{
    public static readonly IReadOnlyList<string> Categories =
        ["Seating", "Surfaces", "Decorative", "Electronics", "Appliances", "Plumbing", "Lighting", "Misc"];

    public static readonly IReadOnlyList<string> RoomNames =
        ["Living", "Dining", "Bedroom", "Study", "Kitchen", "Bathroom", "Outside", "Misc"];

    public static readonly IReadOnlyList<string> FunctionSubsortNames =
        ["One", "Two", "Three", "Four", "Pets", "Magic", "Other", "All"];

    /// <summary>Bitmasks for catalog function subsort buttons, filtering bits 0x01 through 0x08 for buttons
    /// 0 to 3, 0x20 for Pets, 0x40 for Magic, 0x90 for Other, and 0xFF for All.</summary>
    public static readonly IReadOnlyList<int> FunctionSubsortMaskByButton =
        [0x01, 0x02, 0x04, 0x08, 0x20, 0x40, 0x90, 0xff];

    public static readonly IReadOnlyList<CGPoint> RoomPositions =
    [
        new(242, 57), new(281, 53), new(323, 57), new(361, 54),
        new(239, 103), new(284, 109), new(322, 105), new(364, 114),
    ];

    public static readonly IReadOnlyList<string> RoomSubSortNames =
        ["Seating", "Surfaces", "Decorative", "Electronics", "Appliances", "Plumbing", "Lighting", "Misc"];

    public static readonly IReadOnlyList<CGPoint> RoomSubSortPositions =
    [
        new(251, 60), new(289, 60), new(327, 60), new(365, 60),
        new(251, 108), new(289, 108), new(327, 108), new(365, 108),
    ];

    public static readonly CGPoint RoomBackPosition = new(218, 87);

    public static readonly IReadOnlyList<CGPoint> FunctionSubsortPositions = RoomSubSortPositions;

    public static readonly IReadOnlyList<CGPoint> Positions =
    [
        new(242, 58), new(279, 66), new(322, 51), new(369, 54),
        new(241, 111), new(285, 109), new(322, 105), new(364, 114),
    ];

    /// <summary>Maps function-sort catalog buttons to engine category indices, representing the base-2
    /// logarithm of each category flag.</summary>
    public static readonly IReadOnlyList<int> EngineCategories = [0, 1, 5, 3, 2, 4, 7, 6];

    public static int Columns(CGSize canvas)
    {
        if (!double.IsFinite(canvas.Width))
            return 1;
        return Math.Max(1, (int)Math.Min(32.0, Math.Max(0.0, canvas.Width - 427.0) / 45.0));
    }

    public static int Capacity(CGSize canvas) => Columns(canvas) * 2;

    public static CGRect Thumbnail(int index, CGSize canvas)
    {
        int cols = Columns(canvas);
        return new CGRect(415 + (index % cols) * 45, canvas.Height - 95 + (index / cols) * 45, 45, 45);
    }

    public static CGRect Arrow(bool forward, CGSize size, CGSize canvas)
    {
        double x = forward ? canvas.Width - 12 + (12 - size.Width) / 2.0 : 395 + (20 - size.Width) / 2.0;
        return new CGRect(x, canvas.Height - 50 - size.Height / 2.0, size.Width, size.Height);
    }
}

public static class OriginalBuildLayout
{
    public static readonly IReadOnlyList<string> Assets =
        ["Terrain", "Water", "Wall", "Wallpaper", "Stairs", "Fireplace", "Tree", "Floor", "Door", "Window", "Roof", "Hand"];

    public static readonly IReadOnlyList<CGPoint> Positions =
    [
        new(278, 4), new(325, 6), new(364, 4), new(408, 3), new(450, 4), new(502, 8),
        new(279, 57), new(317, 64), new(367, 55), new(409, 55), new(449, 55), new(502, 55),
    ];

    public static readonly IReadOnlyList<CGPoint> HistoryPositions = [new(231, 15), new(231, 53)];

    public static int HistoryFrameIndex(bool enabled) => enabled ? 0 : 3;

    /// <summary>Calculates visible catalog thumbnail columns, stopping 14 pixels before the right edge to
    /// prevent the next-page arrow from covering the final column.</summary>
    public static int Columns(CGSize canvas)
    {
        if (!double.IsFinite(canvas.Width))
            return 1;
        return Math.Max(1, Math.Min(32, (int)(Math.Max(0.0, canvas.Width - 572.0) / 45.0)));
    }

    public static int Capacity(CGSize canvas) => Columns(canvas) * 2;

    public static CGRect HistoryControl(int index, CGSize canvas, CGSize imageSize)
    {
        if (index < 0 || index >= HistoryPositions.Count || imageSize.Width <= 0 || imageSize.Height <= 0)
            return CGRect.Zero;
        return new CGRect(HistoryPositions[index], new CGSize(imageSize.Width / 4.0, imageSize.Height))
            .OffsetBy(0, canvas.Height - 100);
    }

    public static CGRect Arrow(bool forward, CGSize size, CGSize canvas) =>
        new(forward ? canvas.Width - 14 : 546, canvas.Height - 74, size.Width, size.Height);

    /// <summary>Inner dimension of the thumbnail template opening inside its 4-pixel border (37x37), used
    /// to scale catalog icons.</summary>
    public const double ThumbnailInner = 37;

    public static CGRect Thumbnail(int index, CGSize canvas)
    {
        int cols = Columns(canvas);
        return new CGRect(558 + (index % cols) * 45, (int)canvas.Height - 95 + (index / cols) * 45, 45, 45);
    }
}

public enum OriginalBuildObjectTool
{
    Door,
    Window,
    Stairs,
    Plants,
    Fireplace,
    Hand,
}

public static class OriginalBuildObjectToolExtensions
{
    public static readonly IReadOnlyList<OriginalBuildObjectTool> AllCases =
    [
        OriginalBuildObjectTool.Door,
        OriginalBuildObjectTool.Window,
        OriginalBuildObjectTool.Stairs,
        OriginalBuildObjectTool.Plants,
        OriginalBuildObjectTool.Fireplace,
        OriginalBuildObjectTool.Hand,
    ];

    public static int? Category(this OriginalBuildObjectTool tool) => tool switch
    {
        OriginalBuildObjectTool.Door => 8,
        OriginalBuildObjectTool.Window => 9,
        OriginalBuildObjectTool.Stairs => 10,
        OriginalBuildObjectTool.Plants => 11,
        OriginalBuildObjectTool.Fireplace => 12,
        OriginalBuildObjectTool.Hand => null,
        _ => null,
    };

    public static int ButtonIndex(this OriginalBuildObjectTool tool) => tool switch
    {
        OriginalBuildObjectTool.Door => 8,
        OriginalBuildObjectTool.Window => 9,
        OriginalBuildObjectTool.Stairs => 4,
        OriginalBuildObjectTool.Plants => 6,
        OriginalBuildObjectTool.Fireplace => 5,
        OriginalBuildObjectTool.Hand => 11,
        _ => 11,
    };

    public static string Label(this OriginalBuildObjectTool tool) => tool switch
    {
        OriginalBuildObjectTool.Door => "Doors",
        OriginalBuildObjectTool.Window => "Windows",
        OriginalBuildObjectTool.Stairs => "Stairs",
        OriginalBuildObjectTool.Plants => "Trees and plants",
        OriginalBuildObjectTool.Fireplace => "Fireplaces",
        OriginalBuildObjectTool.Hand => "Move objects",
        _ => "Move objects",
    };

    public static OriginalBuildObjectTool? CatalogTool(int category)
    {
        foreach (var tool in AllCases)
        {
            if (tool.Category() == category)
                return tool;
        }
        return null;
    }
}

public static class OriginalHouseholdCreationLayout
{
    public static readonly IReadOnlyDictionary<string, CGRect> Frames = new Dictionary<string, CGRect>(StringComparer.Ordinal)
    {
        ["name"] = new(260, 44, 282, 40),
        ["add"] = new(593, 103, 139, 103),
        ["delete"] = new(594, 208, 149, 99),
        ["edit"] = new(594, 311, 137, 99),
        ["familyDone"] = new(100, 529, 200, 62),
        ["familyCancel"] = new(500, 529, 200, 62),
        ["child"] = new(404, 112, 92, 90),
        ["adult"] = new(500, 102, 57, 100),
        ["light"] = new(396, 202, 55, 71),
        ["medium"] = new(452, 202, 46, 71),
        ["dark"] = new(499, 202, 57, 71),
        ["male"] = new(400, 275, 98, 111),
        ["female"] = new(498, 270, 57, 121),
        ["previousHead"] = new(598, 159, 18, 34),
        ["nextHead"] = new(714, 159, 18, 34),
        ["previousBody"] = new(582, 290, 18, 34),
        ["nextBody"] = new(730, 290, 18, 34),
        ["personality"] = new(80, 124, 225, 160),
        ["zodiac"] = new(126, 284, 132, 32),
        ["biography"] = new(24, 424, 752, 96),
        ["simDone"] = new(100, 529, 200, 62),
        ["simCancel"] = new(500, 529, 200, 62),
    };
}

public static class OriginalFamilyPickerLayout
{
    public static readonly CGPoint RowOrigin = new(41, 76);
    public static readonly CGSize RowSize = new(690, 68);
    public const double RowPitch = 68;
    public const int RowsPerPage = 6;
    public static readonly CGRect ScrollbarRect = new(763, 76, 25, 408);
    public const double ScrollArrowHeight = 30;
    public const double ScrollThumbHeight = 30;
    public static readonly CGRect AddRect = new(322, 530, 92, 62);
    public static readonly CGRect DeleteRect = new(414, 530, 92, 62);
    public static readonly CGRect MoveInRect = new(506, 530, 164, 62);

    public static CGRect MemberFrameRect(CGPoint rowOrigin, int index) =>
        new(rowOrigin.X + 110 + index * 48, rowOrigin.Y + 20, 45, 45);

    /// <summary>Centers the 25x25 family portrait thumbnail inside the 45x45 member frame.</summary>
    public static CGRect MemberPortraitRect(CGRect frame, CGSize? portraitSize = null)
    {
        var size = portraitSize ?? new CGSize(25, 25);
        return new CGRect(
            frame.MinX + Math.Floor((frame.Width - size.Width) / 2.0),
            frame.MinY + Math.Floor((frame.Height - size.Height) / 2.0),
            size.Width,
            size.Height);
    }

    public static CGRect CancelButtonRect(int textWidth)
    {
        double width = Math.Max(91.0, textWidth + 70);
        return new CGRect(Math.Floor(200 - width / 2.0), 529, width, 62);
    }
}

public static class OriginalCreditsLayout
{
    public const string DefaultMusic = "bkground_credits";
    public const string MagicTownMusic = "music_magictown_credits";

    public static string Music(int neighborhoodMode) =>
        neighborhoodMode == 7 ? MagicTownMusic : DefaultMusic;

    public static IReadOnlyList<ushort> Blocks(IEnumerable<string> installedFolders)
    {
        var lower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in installedFolders)
            lower.Add(folder);

        var ids = new List<ushort>();
        if (lower.Contains("ExpansionPack7")) ids.Add(171);
        if (lower.Contains("ExpansionPack6")) ids.Add(170);
        if (lower.Contains("ExpansionPack5")) ids.Add(169);
        if (lower.Contains("ExpansionPack4")) ids.Add(167);
        if (lower.Contains("ExpansionPack3")) ids.Add(166);
        if (lower.Contains("ExpansionPack2")) ids.Add(165);
        if (lower.Contains("Deluxe") || lower.Contains("ExpansionPackGOLD"))
        {
            ids.Add(168);
        }
        else
        {
            if (lower.Contains("ExpansionPack") || lower.Contains("ExpansionPack1")) ids.Add(164);
            ids.Add(163);
        }
        return ids;
    }

    public static IReadOnlyList<string> TokenizeEntry(string entry)
    {
        var scalars = entry.EnumerateRunes().ToArray();
        if (scalars.Length == 0)
            return [];

        var nulled = new Rune?[scalars.Length];
        for (int i = 0; i < scalars.Length; i++)
        {
            int v = scalars[i].Value;
            nulled[i] = (v == '\r' || v == '\n') ? null : scalars[i];
        }

        var result = new List<string>();
        int index = 0;
        var sb = new StringBuilder();
        while (index < nulled.Length)
        {
            int end = index;
            while (end < nulled.Length && nulled[end].HasValue)
                end++;

            sb.Clear();
            for (int pos = index; pos < end; pos++)
            {
                if (nulled[pos] is { } scalar)
                    sb.Append(scalar.ToString());
            }

            string token = sb.ToString();
            result.Add(token);
            if (token.Length > 0)
                index = end;

            do
            {
                index++;
                if (index >= nulled.Length)
                    break;
            }
            while (!nulled[index].HasValue);
        }

        if (scalars.Length > 1 && scalars[^2].Value == '\r' && scalars[^1].Value == '\n')
            result.Add(string.Empty);

        return result;
    }
}

public static class OriginalPictureInPictureLayout
{
    public static CGSize Size(int sizeIndex)
    {
        if (sizeIndex <= 0) return new CGSize(100, 100);
        if (sizeIndex == 1) return new CGSize(200, 200);
        return new CGSize(300, 300);
    }

    public static CGRect WindowRect(CGSize canvas, CGSize size, bool panelMinimized, bool expandedPanel)
    {
        double bottomOffset = panelMinimized ? 5 : expandedPanel ? 155 : 105;
        return new CGRect(
            canvas.Width - size.Width - 5,
            canvas.Height - size.Height - bottomOffset,
            size.Width,
            size.Height);
    }

    public static CGRect CloseBoxRect(CGRect windowRect, CGSize? buttonSize = null)
    {
        var size = buttonSize ?? new CGSize(12, 11);
        return new CGRect(
            windowRect.MaxX - size.Width - 4,
            windowRect.MinY + 4,
            size.Width,
            size.Height);
    }
}

public sealed record SimInteraction
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("interaction")]
    public required byte Interaction { get; init; }

    [JsonPropertyName("param0")]
    public required short Param0 { get; init; }

    [JsonPropertyName("global")]
    public required bool Global { get; init; }

    /// <summary>The clicked object is the default recipient, but the menu can also expose choices from
    /// eligible objects sharing the clicked tile.</summary>
    [JsonPropertyName("objectID")]
    public int? ObjectId { get; init; }

    [JsonPropertyName("plugin")]
    public string? Plugin { get; init; }

    [JsonPropertyName("entry")]
    public string? Entry { get; init; }

    [JsonConstructor]
    public SimInteraction() { }

    [SetsRequiredMembers]
    public SimInteraction(
        byte interaction,
        short param0,
        bool global,
        string? name = null,
        int? objectId = null,
        string? plugin = null,
        string? entry = null)
    {
        Name = name;
        Interaction = interaction;
        Param0 = param0;
        Global = global;
        ObjectId = objectId;
        Plugin = plugin;
        Entry = entry;
    }

    public Dictionary<string, object> InteractParameters(int objectId)
    {
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["objectID"] = ObjectId ?? objectId,
            ["interaction"] = (int)Interaction,
            ["param0"] = (int)Param0,
            ["global"] = Global,
        };
        if (Plugin is not null && Entry is not null)
        {
            parameters["plugin"] = Plugin;
            parameters["entry"] = Entry;
        }
        return parameters;
    }
}

public sealed class OriginalPieNode
{
    public string Label { get; }
    public SimInteraction? Action { get; }
    public List<OriginalPieNode> Children { get; }

    public OriginalPieNode(string label, SimInteraction? action, List<OriginalPieNode>? children = null)
    {
        Label = label;
        Action = action;
        Children = children ?? [];
    }

    public static List<OriginalPieNode> Build(IReadOnlyList<SimInteraction> entries)
    {
        if (entries.Count > 512)
            throw new ContentError("Interaction menu exceeds size limit");

        var roots = new List<OriginalPieNode>();

        static void Insert(ReadOnlySpan<string> labels, SimInteraction action, List<OriginalPieNode> nodes)
        {
            if (labels.IsEmpty)
                return;
            string label = labels[0];
            if (labels.Length == 1)
            {
                nodes.Add(new OriginalPieNode(label, action));
            }
            else
            {
                OriginalPieNode? existing = null;
                foreach (var node in nodes)
                {
                    if (node.Label == label && node.Action is null)
                    {
                        existing = node;
                        break;
                    }
                }
                if (existing is null)
                {
                    existing = new OriginalPieNode(label, null);
                    nodes.Add(existing);
                }
                Insert(labels[1..], action, existing.Children);
            }
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            string name = entry.Name;
            string[] labels = name.Split('/');
            if (name.Length > 512 || labels.Length > 16)
                throw new ContentError("Invalid interaction menu label");

            foreach (string label in labels)
            {
                if (label.Length == 0)
                    throw new ContentError("Invalid interaction menu label");
                foreach (char c in label)
                {
                    if (c < 32)
                        throw new ContentError("Invalid interaction menu label");
                }
            }

            Insert(labels.AsSpan(), entry, roots);
        }

        return roots;
    }
}

public sealed class OriginalPieLayout
{
    public CGPoint Center { get; }
    public IReadOnlyList<CGRect> Rectangles { get; }

    public OriginalPieLayout(
        CGPoint anchor,
        IReadOnlyList<CGSize> sizes,
        CGSize viewport,
        CGSize? centerSize = null,
        bool submenu = false)
    {
        int spokes = sizes.Count <= 2 ? 2 : sizes.Count <= 4 ? 4 : 8;
        const double cardinal = 60;
        const double diagonal = 30;
        var rectangles = new List<CGRect>(sizes.Count);

        for (int index = 0; index < sizes.Count; index++)
        {
            CGSize size = sizes[index];
            CGPoint position;
            if (index >= spokes)
            {
                CGRect previous = rectangles[index == spokes ? 0 : index == spokes + 1 ? spokes / 2 : index - 2];
                position = new CGPoint(
                    -size.Width / 2.0,
                    index % 2 == 0 ? previous.MinY - size.Height : previous.MaxY);
            }
            else
            {
                position = (index * 8 / spokes) switch
                {
                    0 => new CGPoint(-size.Width / 2.0, -cardinal - size.Height),
                    1 => new CGPoint(diagonal, -diagonal - size.Height),
                    2 => new CGPoint(cardinal, -size.Height / 2.0),
                    3 => new CGPoint(diagonal, diagonal),
                    4 => new CGPoint(-size.Width / 2.0, cardinal),
                    5 => new CGPoint(-diagonal - size.Width, diagonal),
                    6 => new CGPoint(-cardinal - size.Width, -size.Height / 2.0),
                    _ => new CGPoint(-diagonal - size.Width, -diagonal - size.Height),
                };
            }
            rectangles.Add(new CGRect(position, size));
        }

        CGRect initialBounds = submenu
            ? new CGRect(-8, -8, 16, 16)
            : new CGRect(-36, -40, 72, 80);

        if (centerSize is { Width: > 0, Height: > 0 } cs)
        {
            initialBounds = RectUnion(
                initialBounds,
                new CGRect(-cs.Width / 2.0, -cs.Height / 2.0, cs.Width, cs.Height));
        }

        CGRect bounds = initialBounds;
        foreach (CGRect r in rectangles)
            bounds = RectUnion(bounds, r);
        bounds = bounds.InsetBy(-4, -4);

        static double Clamped(double requested, double minimum, double maximum) =>
            maximum >= minimum ? Math.Min(maximum, Math.Max(minimum, requested)) : (minimum + maximum) / 2.0;

        Center = new CGPoint(
            Clamped(anchor.X, -bounds.MinX, viewport.Width - bounds.MaxX),
            Clamped(anchor.Y, -bounds.MinY, viewport.Height - bounds.MaxY));

        var shifted = new CGRect[rectangles.Count];
        for (int i = 0; i < rectangles.Count; i++)
            shifted[i] = rectangles[i].OffsetBy(Center.X, Center.Y);
        Rectangles = shifted;
    }

    private static CGRect RectUnion(CGRect a, CGRect b)
    {
        double minX = Math.Min(a.MinX, b.MinX);
        double minY = Math.Min(a.MinY, b.MinY);
        double maxX = Math.Max(a.MaxX, b.MaxX);
        double maxY = Math.Max(a.MaxY, b.MaxY);
        return new CGRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Determines the spoke index (0 to 7 clockwise from 12 o'clock) pointing toward the center of
    /// the menu entry.</summary>
    public static int Direction(CGRect rect, CGPoint center)
    {
        double turn = Math.Atan2(rect.MidX - center.X, center.Y - rect.MidY) / (2.0 * Math.PI);
        return (int)Math.Floor((turn < 0 ? turn + 1 : turn) * 8.0 + 0.5) % 8;
    }

    public int? Hit(CGPoint point)
    {
        for (int i = 0; i < Rectangles.Count; i++)
        {
            if (Rectangles[i].Contains(point))
                return i;
        }

        double x = point.X - Center.X;
        double y = point.Y - Center.Y;
        double radius = double.Hypot(x, y);
        if (!(radius > 35 && radius <= 150 && Rectangles.Count > 0))
            return null;

        int spokes = Rectangles.Count <= 2 ? 2 : Rectangles.Count <= 4 ? 4 : 8;
        double angle = Math.Atan2(x, -y);
        double turn = angle / (2.0 * Math.PI);
        int index = (int)Math.Floor((turn < 0 ? turn + 1 : turn) * spokes + 0.5) % spokes;
        return index < Rectangles.Count ? index : null;
    }
}

/// <summary>Full-screen destination vehicle loading screens displayed while travelling between
/// neighborhoods and expansion areas.</summary>
public enum OriginalTravelLoadScreen
{
    DowntownTaxi = 0,
    VacationSuv = 1,
    VacationCruiseShip = 2,
    OldTownTrolley = 3,
    StudioTownLimo = 4,
    StudioTownFanBus = 5,
    MagicTownBalloon = 6,
    MagicTownHole = 7,
}

public static class OriginalTravelLoadScreenExtensions
{
    public static bool PrefersHighRes(double logicalWidth) => logicalWidth > 800;

    public static IReadOnlyList<string> ResourcePaths(this OriginalTravelLoadScreen screen, bool highRes)
    {
        string suffix = highRes ? "1024x768.bmp" : "800x600.bmp";
        return screen switch
        {
            OriginalTravelLoadScreen.DowntownTaxi =>
                [$"UIGraphics/Downtown/Taxi_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.VacationSuv =>
                [$"UIGraphics/VIsland/vacation_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.VacationCruiseShip =>
                [$"UIGraphics/VIsland/vacation_loadscreen2_{suffix}", $"UIGraphics/VIsland/vacation_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.OldTownTrolley =>
                [$"UIGraphics/Community/Bus_loadscreen_{suffix}", $"UIGraphics/Nbhd/Bus_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.StudioTownLimo =>
                [$"UIGraphics/Studiotown/Studiotown_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.StudioTownFanBus =>
                [$"UIGraphics/Studiotown/Studiotown_loadscreen_fan_{suffix}", $"UIGraphics/Studiotown/Studiotown_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.MagicTownBalloon =>
                [$"UIGraphics/Magicland/magicland_loadscreen_{suffix}"],
            OriginalTravelLoadScreen.MagicTownHole =>
                [$"UIGraphics/Magicland/magicland_loadscreen_hole_{suffix}", $"UIGraphics/Magicland/magicland_loadscreen_{suffix}"],
            _ => [],
        };
    }

    /// <summary>Selects the travel loading screen for a trip, returning null when returning home or
    /// travelling to a residential lot.</summary>
    public static OriginalTravelLoadScreen? Select(
        int? sourceHouse,
        int destination,
        int? sourceStateClass = null,
        int? destinationStateClass = null,
        int travellerFameScore = 0,
        int travellerJobType = 0,
        int lotTransitInfo = 0,
        bool isReturnHome = false)
    {
        if (isReturnHome)
            return null;

        switch (destination)
        {
            case >= 21 and <= 30:
                return OriginalTravelLoadScreen.DowntownTaxi;

            case >= 40 and <= 48:
                bool fromResidential;
                if (sourceStateClass.HasValue)
                    fromResidential = sourceStateClass.Value == 1;
                else if (sourceHouse.HasValue)
                    fromResidential = sourceHouse.Value is < 40 or > 48;
                else
                    fromResidential = false;
                return fromResidential ? OriginalTravelLoadScreen.VacationCruiseShip : OriginalTravelLoadScreen.VacationSuv;

            case >= 81 and <= 89:
                bool isFanBusEligible = travellerFameScore > 0 && travellerJobType == 0;
                return isFanBusEligible ? OriginalTravelLoadScreen.StudioTownFanBus : OriginalTravelLoadScreen.StudioTownLimo;

            case >= 90 and <= 98:
                return lotTransitInfo == 0 ? OriginalTravelLoadScreen.MagicTownHole : OriginalTravelLoadScreen.MagicTownBalloon;

            default:
                if (destinationStateClass == 2)
                    return OriginalTravelLoadScreen.OldTownTrolley;
                return null;
        }
    }
}

/// <summary>Computes neighborhood lot rollover box sizing and text wrapping. Width is clamped between 150
/// and 320 pixels with 25-pixel margins, expanding by half up to twice if height exceeds two-thirds width,
/// followed by member portraits in rows.</summary>
public sealed class NeighborhoodRolloverLayout : IEquatable<NeighborhoodRolloverLayout>
{
    public const int Inset = 25;
    public const int Head = 25;

    public readonly record struct Rect(int X, int Y, int Width, int Height);

    public IReadOnlyList<string> Lines { get; }
    public int Width { get; }
    public int Height { get; }

    public IReadOnlyList<Rect> Heads { get; }

    internal static int HeadsHeight(int members, int textWidth) =>
        members > 0 ? 10 + (members > textWidth / Head ? 2 * Head : Head) : 0;

    public NeighborhoodRolloverLayout(
        string text,
        int lineHeight,
        Func<string, int> measure,
        Func<string, int, IReadOnlyList<string>> wrap)
        : this(text, lineHeight, 0, measure, wrap)
    {
    }

    public NeighborhoodRolloverLayout(
        string text,
        int lineHeight,
        int memberCount,
        Func<string, int> measure,
        Func<string, int, IReadOnlyList<string>> wrap)
    {
        string[] paragraphs = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int textWidth = measure(paragraphs.Length > 0 ? paragraphs[0] : string.Empty);
        int attempt = 0;
        var lines = new List<string>();
        while (true)
        {
            textWidth = Math.Min(320, Math.Max(150, textWidth));
            lines.Clear();
            foreach (string paragraph in paragraphs)
                lines.AddRange(wrap(paragraph, textWidth));
            int h = lines.Count * lineHeight + 2 * Inset + HeadsHeight(memberCount, textWidth);
            if (h < (textWidth + 2 * Inset) * 2 / 3 || attempt > 1)
                break;
            textWidth = textWidth * 3 / 2;
            attempt++;
        }

        Lines = lines;
        Width = textWidth + 2 * Inset;
        int height = lines.Count * lineHeight + 2 * Inset + HeadsHeight(memberCount, textWidth);
        Height = height;

        int perRow = Math.Max(1, textWidth / Head);
        bool twoRows = memberCount > perRow;
        int count = Math.Max(0, memberCount);
        var heads = new Rect[count];
        for (int member = 0; member < count; member++)
        {
            int row = member < perRow ? 0 : 1;
            int column = row == 0 ? member : member - perRow;
            int bottom = height - Inset - (row == 0 && twoRows ? Head : 0);
            heads[member] = new Rect(Inset + column * Head, bottom - Head, Head, Head);
        }
        Heads = heads;
    }

    public bool Equals(NeighborhoodRolloverLayout? other) =>
        other is not null &&
        Width == other.Width &&
        Height == other.Height &&
        Lines.SequenceEqual(other.Lines) &&
        Heads.SequenceEqual(other.Heads);

    public override bool Equals(object? obj) =>
        obj is NeighborhoodRolloverLayout other && Equals(other);

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Width);
        hc.Add(Height);
        foreach (string line in Lines) hc.Add(line);
        foreach (Rect head in Heads) hc.Add(head);
        return hc.ToHashCode();
    }
}
