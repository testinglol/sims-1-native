using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>Manages build-mode wall placement, validation, and erasure across cardinal and diagonal
/// segments. Enforces terrain flatness, object placement flags, buildability flags from Arry 8/108, pool
/// exclusions from Arry 9, and handles opening sales and partial refunds when replacing or demolishing
/// walls.</summary>
public sealed class WallTool(SimulationWorld world, LotWalls walls, LotTerrain terrain, WallCatalog catalog, IFamilyBudget budget, LotPool? pool = null, LotTiles? tiles = null, LotWater? water = null, LotFloors? floors = null)
{
    public const int ExpenseCategory = 7;
    private const double RefundFactor = 0.800000011920929;
    private static readonly int[] OpeningStyles = [3, 5, 6, 15, 22, 23];
    private static readonly int[] FenceStyles = [2, 12, 13, 14];
    private static readonly (uint Local, ushort Flag)[] ClearFlags = [(1, 0x800), (8, 0x400), (2, 0x100), (4, 0x200)];
    private static readonly (uint Local, ushort Flag)[] OpeningFlags = [(1, 8), (8, 4), (2, 1), (4, 2)];

    public int Pending { get; private set; }

    public bool InsufficientFunds { get; private set; }

    public int Refused { get; private set; }

    public int Error { get; private set; }

    /// <summary>Draws or erases a straight axis-aligned or 45-degree line of wall segments between two
    /// lattice corners. Returns the number of modified segments.</summary>
    public int Line(int fromX, int fromY, int toX, int toY, int style, int pattern, bool erase)
    {
        Validate(fromX, fromY);
        Validate(toX, toY);
        var dx = toX - fromX;
        var dy = toY - fromY;
        if (dx != 0 && dy != 0 && Math.Abs(dx) != Math.Abs(dy)) throw new NotSupportedException("A wall line must be axis-aligned or 45-degree diagonal");
        if (dx == 0 && dy == 0) return 0;
        if (!erase && !catalog.Contains(style)) throw new ArgumentException($"Wall style {style} is not in the build catalog");
        if (pattern is < 0 or > 255) throw new ArgumentException("Wall pattern must fit a byte");

        var edges = new List<(int X, int Y, int Bit)>();
        if (dy == 0)
            for (var x = Math.Min(fromX, toX); x < Math.Max(fromX, toX); x++) edges.Add((x, fromY, 2));
        else if (dx == 0)
            for (var y = Math.Min(fromY, toY); y < Math.Max(fromY, toY); y++) edges.Add((fromX, y, 1));
        else
        {
            var left = fromX < toX ? (X: fromX, Y: fromY) : (X: toX, Y: toY);
            var rising = (dx > 0) == (dy > 0);
            for (var i = 0; i < Math.Abs(dx); i++)
                edges.Add(rising ? (left.X + i, left.Y + i, 0x20) : (left.X + i, left.Y - i - 1, 0x10));
        }

        var reference = terrain.ReferenceHeight(fromX, fromY);
        var applied = 0;
        InsufficientFunds = false;
        foreach (var (x, y, bit) in edges)
        {
            if (erase ? Erase(x, y, bit) : Add(x, y, bit, style, pattern, reference)) applied++;
        }
        return applied;
    }

    /// <summary>Draws a rectangular wall outline using the same per-edge placement, refusal, and cost rules
    /// as line drawing.</summary>
    public int Rectangle(int x, int y, int width, int height, int style, int pattern)
    {
        if (width <= 0 || height <= 0 || width > 128 || height > 128)
            throw new ArgumentException("A wall rectangle must have positive extents no greater than 128 tiles");
        var corners = new[]
        {
            (x, y, x + width, y),
            (x, y, x, y + height),
            (x, y + height, x + width, y + height),
            (x + width, y, x + width, y + height),
        };
        var changed = 0;
        var insufficientFunds = false;
        foreach (var (fromX, fromY, toX, toY) in corners)
        {
            changed += Line(fromX, fromY, toX, toY, style, pattern, erase: false);
            insufficientFunds |= InsufficientFunds;
        }
        InsufficientFunds = insufficientFunds;
        return changed;
    }

    private bool Add(int x, int y, int bit, int style, int pattern, byte reference)
    {
        var record = walls[x, y];
        var diagonal = bit is 0x10 or 0x20;
        if ((record[0] & bit) != 0)
        {
            // Overwriting an opening style sells doors/windows and resets style to 1.
            if (diagonal || !OpeningStyles.Contains(record[bit == 1 ? 2 : 3])) return false;
            var sold = SellOpening(x, y, bit);
            if (sold == 0) return false;
            record[bit == 1 ? 2 : 3] = 1;
            Pending -= sold;
            return true;
        }
        if (diagonal ? (record[0] & 0x33) != 0 : (record[0] & 0x30) != 0) return Fail(0xb);
        if (SegmentBlockedBySimOrInUseObject(x, y, bit, diagonal)) return Fail(0xb);
        if (!FenceStyles.Contains(style))
        {
            var flat = terrain.IsFlatAt(x, y, reference);
            if (!diagonal)
            {
                var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
                flat &= terrain.IsFlatAt(nx, ny, reference);
            }
            if (!flat) return Fail(0x11);
        }
        if (tiles is not null && !tiles.InsideLot(x, y)) return Fail(0);
        if (TileTestFails(x, y, bit)) return Fail(0);
        if (tiles is not null && !FenceStyles.Contains(style) && Unbuildable(x, y, bit, diagonal)) return Fail(0x20);
        var objects = world.ObjectsAtTile(x, y, walls.Level).Where(item => item is not XPerson).ToArray();
        if (diagonal)
        {
            if (objects.Any(item => item.Definition[(int)ObjdField.Type] != 9)) return Fail(0xb);
        }
        else if (EdgeObjects(x, y, bit, ClearFlags).Any()) return Fail(0xb);
        if (pool is not null && PoolBlocks(x, y, bit, diagonal)) return Fail(0x1d);
        if (water is not null && WaterBlocks(x, y, bit, diagonal)) return Fail(0x1d);
        if (DiagonalConflict(x, y, bit)) return Fail(0);
        var cost = catalog.Price(style);
        if (cost >= 1 && Pending + cost > budget.ReadFamilyBudget())
        {
            InsufficientFunds = true;
            return Fail(0);
        }
        record[0] |= (byte)bit;
        if (diagonal)
        {
            // Zero cardinal style and pattern slots when initializing a diagonal wall.
            record[2] = 0;
            record[4] = 0;
            record[3] = (byte)style;
            record[bit == 0x10 ? 7 : 6] = (byte)pattern;
        }
        else
        {
            record[bit == 1 ? 2 : 3] = (byte)style;
            record[bit == 1 ? 4 : 5] = (byte)pattern;
            // Mirror the cardinal edge on the adjacent tile using opposite bit 4 or 8.
            walls[bit == 1 ? x - 1 : x, bit == 1 ? y : y - 1][0] |= (byte)(bit == 1 ? 4 : 8);
        }
        Pending += cost;
        return true;
    }

    public bool Erase(int x, int y, int bit, LotFloors? overrideFloors = null)
    {
        var record = walls[x, y];
        if ((record[0] & bit) == 0) return false;
        var diagonal = bit is 0x10 or 0x20;
        if (!diagonal)
        {
            // Existing doors and windows must be sellable before modifying the wall edge.
            if (world.ObjectsAtTile(x, y, walls.Level).Any(item =>
                    item is not XPerson && item.Definition[(int)ObjdField.Type] == 8 && !world.CanSellObject(item)))
                return Fail(0x23);
            if (EdgeObjects(x, y, bit, OpeningFlags).Any(item => item.Definition[(int)ObjdField.Type] != 8))
                return Fail(0xb);
        }
        var style = record[diagonal ? 3 : bit == 1 ? 2 : 3];
        var objectRefund = !diagonal && OpeningStyles.Contains(style) ? SellOpening(x, y, bit) : 0;
        if (objectRefund > 0) record[bit == 1 ? 2 : 3] = 1;
        var refund = unchecked((int)(objectRefund + catalog.Price(style) * RefundFactor));
        if (diagonal)
        {
            // Erasing a diagonal wall on a split floor (0xFF) restores the flat pattern.
            var targetFloors = overrideFloors ?? floors;
            if (targetFloors is not null && targetFloors[x, y] == byte.MaxValue)
                targetFloors[x, y] = record[4] != 0 ? record[4] : record[2];
        }
        record[0] &= (byte)~bit;
        if (!diagonal)
        {
            // Clear the neighbor's mirrored wall bit when erasing a cardinal edge.
            var neighbour = walls[bit == 1 ? x - 1 : x, bit == 1 ? y : y - 1];
            neighbour[0] &= (byte)(bit == 1 ? ~4 : ~8);
            neighbour[bit == 1 ? 7 : 6] = 0;
        }
        Pending -= refund;
        return true;
    }

    /// <summary>Sells the door or window occupying the specified wall edge, along with its multi-tile
    /// group, returning 80% depreciated value rounded to the nearest integer.</summary>
    private int SellOpening(int x, int y, int bit)
    {
        var item = world.ObjectsAtTile(x, y, walls.Level).FirstOrDefault(candidate => candidate is not XPerson &&
            candidate.Definition[(int)ObjdField.Type] == 8 && OpensOn(candidate, bit));
        if (item is null || !world.CanSellObject(item)) return 0;
        var refund = unchecked((int)(world.ObjectSaleValue(item) * RefundFactor + 0.5));
        world.DemolishObject(item);
        return refund;
    }

    private bool SegmentBlockedBySimOrInUseObject(int x, int y, int bit, bool diagonal)
    {
        var candidates = diagonal
            ? new[] { (x, y) }
            : bit == 1 ? new[] { (x, y), (x - 1, y) } : new[] { (x, y), (x, y - 1) };
        foreach (var (tx, ty) in candidates)
        {
            if (tx < 1 || ty < 1 || tx > LotWalls.Size - 2 || ty > LotWalls.Size - 2) continue;
            foreach (var item in world.ObjectsAtTile(tx, ty, walls.Level))
            {
                if (item[ObjectData.Hidden] != 0) continue;
                if (item is XPerson || item.Definition[(int)ObjdField.Type] == 2 || item[ObjectData.UseCount] > 0)
                    return true;
                if (world.MultiTileMembers(item).Any(member => member[ObjectData.UseCount] > 0))
                    return true;
            }
        }
        return false;
    }

    private static bool OpensOn(XObject item, int bit)
    {
        var flags = unchecked((ushort)item[ObjectData.WallPlacementFlags]);
        var turns = (item[ObjectData.Direction] & 7) >> 1;
        foreach (var (local, flag) in OpeningFlags)
            if ((flags & flag) != 0 && SimulationWorld.RotatePlacementWallBit(local, turns) == (uint)bit) return true;
        return false;
    }

    public int Commit()
    {
        var amount = Pending;
        if (amount != 0) budget.AlterFamilyBudget(ExpenseCategory, amount);
        Pending = 0;
        return amount;
    }

    /// <summary>Checks for invalid corner intersections between cardinal walls and adjacent diagonal walls
    /// within lot bounds.</summary>
    private bool DiagonalConflict(int x, int y, int bit)
    {
        bool Has(int tx, int ty, int mask) => tx is >= 1 and <= LotWalls.Size - 2 && ty is >= 1 and <= LotWalls.Size - 2 && (walls[tx, ty][0] & mask) != 0;
        return bit switch
        {
            1 => Has(x - 1, y - 1, 0x10) || Has(x, y + 1, 0x10) || Has(x, y - 1, 0x20) || Has(x - 1, y + 1, 0x20),
            2 => Has(x - 1, y - 1, 0x10) || Has(x + 1, y, 0x10) || Has(x - 1, y, 0x20) || Has(x + 1, y - 1, 0x20),
            0x10 => Has(x - 1, y - 1, 4 | 8) || Has(x + 1, y + 1, 1 | 2),
            0x20 => Has(x - 1, y + 1, 2 | 4) || Has(x + 1, y - 1, 1 | 8),
            _ => false,
        };
    }

    /// <summary>Validates that diagonal walls do not share tiles with existing cardinal or mirror wall
    /// segments, and that cardinal edges do not border adjacent diagonal wall tiles.</summary>
    private bool TileTestFails(int x, int y, int bit)
    {
        if (bit is 0x10 or 0x20) return (walls[x, y][0] & 0xf) != 0;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return (walls[nx, ny][0] & 0x30) != 0;
    }

    /// <summary>Checks whether tiles lack the buildable flag in the terrain grid. Non-fence walls require
    /// buildable flags on both sides of a cardinal edge, whereas diagonal walls inspect only the host tile.
    /// </summary>
    private bool Unbuildable(int x, int y, int bit, bool diagonal)
    {
        if (tiles!.Buildable(x, y, walls.Level)) return false;
        if (diagonal) return true;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return !tiles.Buildable(nx, ny, walls.Level);
    }

    /// <summary>Determines whether pool placement blocks the wall, disallowing cardinal edges between two
    /// pool tiles and diagonal walls directly on pool tiles.</summary>
    private bool PoolBlocks(int x, int y, int bit, bool diagonal)
    {
        if (!pool!.IsPool(x, y, walls.Level)) return false;
        if (diagonal) return true;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return pool.IsPool(nx, ny, walls.Level);
    }

    /// <summary>Determines whether pond terrain blocks the wall, disallowing cardinal edges between two
    /// water tiles and diagonal walls directly on water tiles.</summary>
    private bool WaterBlocks(int x, int y, int bit, bool diagonal)
    {
        if (!water!.IsWater(x, y, walls.Level)) return false;
        if (diagonal) return true;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return water.IsWater(nx, ny, walls.Level);
    }

    private bool Fail(int code)
    {
        Refused++;
        if (Error == 0 && code != 0) Error = code;
        return false;
    }

    private void Validate(int x, int y)
    {
        if (x < 1 || y < 1 || x > LotWalls.Size - 2 || y > LotWalls.Size - 2)
            throw new ArgumentException("A wall must start and end inside the lot border");
        if (world.ObjectsAtTile(x, y, walls.Level).Any(item => item is XPerson))
            throw new ArgumentException("A Sim is standing on the wall's end tile");
    }

    /// <summary>Enumerates objects on either side of a wall edge whose placement flags match that boundary,
    /// accounting for object rotation.</summary>
    private IEnumerable<XObject> EdgeObjects(int x, int y, int bit, (uint Local, ushort Flag)[] table)
    {
        var (nx, ny, opposite) = bit == 1 ? (x - 1, y, 4u) : (x, y - 1, 8u);
        foreach (var (tileX, tileY, mask) in new[] { (x, y, (uint)bit), (nx, ny, opposite) })
            foreach (var item in world.ObjectsAtTile(tileX, tileY, walls.Level))
            {
                if (item is XPerson) continue;
                var flags = unchecked((ushort)item[ObjectData.WallPlacementFlags]);
                var turns = (item[ObjectData.Direction] & 7) >> 1;
                foreach (var (local, flag) in table)
                    if ((flags & flag) != 0 && SimulationWorld.RotatePlacementWallBit(local, turns) == mask) { yield return item; break; }
            }
    }
}
