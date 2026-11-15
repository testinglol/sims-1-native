namespace SimsEngine.Simulation;

/// <summary>Pure lot geometry functions recovered from Sims.exe.</summary>
public static class LotGeometry
{
    /// <summary>Sims.exe FUN_00415c5b constructs the lot manager with dimension 0x40 via FUN_00479161.</summary>
    public const int LotDimension = 0x40;

    /// <summary>FUN_00479318 stores its orientation argument masked with 3 at lot-manager +0x84.</summary>
    public static int NormalizeOrientation(int orientation) => orientation & 3;

    /// <summary>FUN_0047bd91's 0x400-unit transform as called by FUN_00434895 with the inverse of the lot
    /// orientation. Coordinates are in the game's 0..1023 lot-unit space.</summary>
    public static ObjectPosition TransformSearchCenter(ObjectPosition position, int orientation)
    {
        orientation = NormalizeOrientation(orientation);
        return orientation switch
        {
            0 => position,
            1 => position with { X = 0x3ff - position.Y, Y = position.X },
            2 => position with { X = 0x3ff - position.X, Y = 0x3ff - position.Y },
            _ => position with { X = position.Y, Y = 0x3ff - position.X },
        };
    }

    /// <summary>
    /// FUN_00407761 builds DAT_006DE81C and FUN_00434FFD indexes it as a 64 × 64 pair table.
    /// For tile coordinates inside the lot, return the precomputed pair for orientations 1–3.
    /// Orientation 0 is the caller's unchanged path. False leaves unusual/out-of-lot coordinates
    /// to the caller, since the original helper indexes outside this table for them.
    /// </summary>
    public static bool TryTransformTile(sbyte x, sbyte y, int orientation, out sbyte transformedX, out sbyte transformedY)
    {
        transformedX = x;
        transformedY = y;
        if (orientation is < 1 or > 3 || x is < 0 or >= 64 || y is < 0 or >= 64) return false;

        (var tx, var ty) = orientation switch
        {
            1 => (63 - y, (int)x),
            2 => (63 - x, 63 - y),
            3 => ((int)y, 63 - x),
            _ => throw new InvalidOperationException("Validated lot orientation must be between 1 and 3."),
        };
        transformedX = (sbyte)tx;
        transformedY = (sbyte)ty;
        return true;
    }

    /// <summary>
    /// FUN_00479318 transforms the four lot corners and stores their extrema at manager +0x70..+0x7c.
    /// Sims.exe creates 64 × 64 lots; the size parameter also makes the corner calculation independently testable.
    /// </summary>
    public static LotCoordinateBounds TransformLotBounds(int dimension, int orientation)
    {
        if (dimension is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(dimension), "Lot dimension must fit the 64 × 64 transform table.");
        orientation = NormalizeOrientation(orientation);
        var edge = (sbyte)(dimension - 1);
        Span<(sbyte X, sbyte Y)> corners = [(0, 0), (edge, 0), (0, edge), (edge, edge)];
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var corner in corners)
        {
            var x = corner.X;
            var y = corner.Y;
            if (orientation != 0 && !TryTransformTile(x, y, orientation, out x, out y))
                throw new InvalidOperationException("A validated lot corner must be covered by the transform table.");
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        return new LotCoordinateBounds(minX, maxX, minY, maxY);
    }

    /// <summary>
    /// FUN_004222bd/FUN_004222ef: the one-tile footprint offset for a direction. Sims.exe masks the
    /// direction to three bits and maps it onto the eight isometric neighbors, then scales each axis by 16.
    /// FUN_004549b6 adds the first component to the +0xf4 field (Y) and the second to +0xf8 (X), so the
    /// returned X and Y are the table's second and first components.
    /// </summary>
    public static (int X, int Y) FootprintOffset(short direction) => (direction & 7) switch
    {
        0 => (0, -16),
        1 => (16, -16),
        2 => (16, 0),
        3 => (16, 16),
        4 => (0, 16),
        5 => (-16, 16),
        6 => (-16, 0),
        7 => (-16, -16),
        _ => throw new InvalidOperationException("A three-bit direction must be between 0 and 7."),
    };

    /// <summary>FUN_004549b6's free SLOT projection: convert the local X/Y slot offset to a lot position after
    /// rotating it by the anchor object's direction. The original scales tile coordinates by 3/16, transforms the
    /// vector, scales back by 16/3 and rounds half values away from zero in FUN_00454ff9.</summary>
    public static ObjectPosition SlotPosition(ObjectPosition anchor, short direction, float slotX, float slotY)
    {
        var angle = (direction & 7) * (MathF.PI / 4f);
        var sin = MathF.Sin(angle);
        var cos = MathF.Cos(angle);
        var x = MathF.Round(cos * slotX - sin * slotY, MidpointRounding.AwayFromZero);
        var y = MathF.Round(sin * slotX + cos * slotY, MidpointRounding.AwayFromZero);
        return anchor with { X = checked(anchor.X + (int)x), Y = checked(anchor.Y + (int)y) };
    }
}
