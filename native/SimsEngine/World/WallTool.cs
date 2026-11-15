using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>The build-mode wall tool's committed result for a straight drag (cNewWallTool and its planner, FUN_00493f54).
/// The planner edits a working copy of the wall grid and the mouse-up commit (FUN_0049401b) copies the records that
/// differ and charges the sum to expense category 7 (FUN_00492bc4). Each unit step follows FUN_004951a5 (add) and
/// FUN_004960a5 (add or erase); a step that fails leaves its edge unchanged and records the first error code
/// (0xb blocked by an object, 0x11 uneven terrain, 0xb on a Sim at an endpoint).
/// <para>Add: an existing segment is free; a missing one costs the style's price (0x6e57e8 table) and must fit the budget
/// together with the pending amount (FUN_00492b81, which sets +0xe060 and refuses the step otherwise). Unless the style is
/// a fence or balustrade (2, 12, 13, 14) the tile and its far neighbour must have flat terrain at the drag's start
/// height (FUN_004950b8); no non-Sim object on either side may forbid a wall there (WallPlacementFlags 0x100-0x800
/// rotated by the object's direction); a diagonal needs every object on its tile to be type 9. A tile flagged 0x20 in Arry 8 is refused without an error code, and one without the buildable bit 0 (Arry 8 / 108) refuses a non-fence wall with error 0x20. With a pool grid (Arry 9) a cardinal edge between two pool tiles and a diagonal on a pool tile are refused with error 0x1d.</para>
/// <para>Erase: the refund is trunc(object refunds + 0.8 * price of the segment's style) (float constant 0x6681ec). A
/// segment whose style is an opening style (3, 5, 6, 15, 22, 23) first sells the first door or window (type 8) of the owner
/// tile that names the edge, with its whole group, for trunc(0.8 * value + 0.5) (FUN_00497140, constant 0x666578). Drawing a wall over an
/// existing opening-style segment does that sale (FUN_004960a5) and resets the segment's style to 1; over any other existing segment it does nothing.</para>
/// <para>Multi-segment drags are committed through <c>WALL_STROKE</c>. The lot file keeps one style byte for a tile's second edge and its diagonal, so
/// a tile never holds both.</para></summary>
public sealed class WallTool(SimulationWorld world, LotWalls walls, LotTerrain terrain, WallCatalog catalog, IFamilyBudget budget, LotPool? pool = null, LotTiles? tiles = null, LotWater? water = null, LotFloors? floors = null)
{
    public const int ExpenseCategory = 7;
    private const double RefundFactor = 0.800000011920929; // the float constant at 0x6681ec
    private static readonly int[] OpeningStyles = [3, 5, 6, 15, 22, 23];
    private static readonly int[] FenceStyles = [2, 12, 13, 14];
    private static readonly (uint Local, ushort Flag)[] ClearFlags = [(1, 0x800), (8, 0x400), (2, 0x100), (4, 0x200)];
    private static readonly (uint Local, ushort Flag)[] OpeningFlags = [(1, 8), (8, 4), (2, 1), (4, 2)];

    /// <summary>Planner +0xe028: the amount a commit will charge (negative is a refund).</summary>
    public int Pending { get; private set; }

    /// <summary>A step was refused for lack of funds (tool +0xe060).</summary>
    public bool InsufficientFunds { get; private set; }

    /// <summary>How many steps failed and the first error code (planner +0xe064; 0 when none).</summary>
    public int Refused { get; private set; }

    public int Error { get; private set; }

    /// <summary>Draws or erases the straight (axis-aligned or 45 degree) line between two lattice corners and returns how
    /// many segments changed. Throws <see cref="ArgumentException"/> for a drag the original refuses outright.</summary>
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

    /// <summary>Draws the four sides selected by the SDL wall rectangle gesture. The original wall editor's
    /// rectangular gesture is an outline; it reuses the same per-edge placement, refusal, and cost rules as a line.</summary>
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
            // FUN_004960a5: a wall drawn over an existing opening-style segment sells the door or window on it and resets its style to 1.
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
            // FUN_004960a5 paints the diagonal's default side: 0x10 side 4 is byte 7, 0x20 side 1 is byte 6.
            // FUN_004960a5 also zeroes the first style word and the first pattern slot (file bytes 2 and 4); FUN_00493b0f writes
            // the pattern to the default side only, so the other side keeps whatever the record held.
            record[2] = 0;
            record[4] = 0;
            record[3] = (byte)style;
            record[bit == 0x10 ? 7 : 6] = (byte)pattern;
        }
        else
        {
            record[bit == 1 ? 2 : 3] = (byte)style;
            record[bit == 1 ? 4 : 5] = (byte)pattern;
            // FUN_00479954 mirrors a cardinal edge on the neighbour across it (bit 4 or 8), which keeps the other side's pattern.
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
            // FUN_004951a5 lines 693 to 786: doors/windows on the owner tile must pass CanSellObject (error 0x23), and
            // non-portal objects on either side of the edge may not require the wall via WallPlacementFlags (error 0xb).
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
            // FUN_004960a5 lines 175 to 193: when erasing a diagonal wall on a split-floor tile (0xff), restore the
            // flat floor pattern from the diagonal floor slot stored in the wall record.
            var targetFloors = overrideFloors ?? floors;
            if (targetFloors is not null && targetFloors[x, y] == byte.MaxValue)
                targetFloors[x, y] = record[4] != 0 ? record[4] : record[2];
        }
        record[0] &= (byte)~bit;
        if (!diagonal)
        {
            // FUN_00479954: the neighbour loses the mirror bit and the pattern of its side; the owner keeps its stale style and pattern.
            var neighbour = walls[bit == 1 ? x - 1 : x, bit == 1 ? y : y - 1];
            neighbour[0] &= (byte)(bit == 1 ? ~4 : ~8);
            neighbour[bit == 1 ? 7 : 6] = 0;
        }
        Pending -= refund;
        return true;
    }

    /// <summary>FUN_00497140: sells the first door or window (type 8) on the owner tile whose opening flags name the edge,
    /// with its whole group, and returns trunc(0.8 * value + 0.5); 0 when there is none.</summary>
    private int SellOpening(int x, int y, int bit)
    {
        var item = world.ObjectsAtTile(x, y, walls.Level).FirstOrDefault(candidate => candidate is not XPerson &&
            candidate.Definition[(int)ObjdField.Type] == 8 && OpensOn(candidate, bit));
        if (item is null || !world.CanSellObject(item)) return 0;
        var refund = unchecked((int)(world.ObjectSaleValue(item) * RefundFactor + 0.5));
        world.DemolishObject(item);
        return refund;
    }

    /// <summary>FUN_00496e92 lines 80 to 128: checks both tiles adjacent to the segment for a standing Sim (type 2) or
    /// an object (or multi-tile group member) currently in use (UseCount &gt; 0).</summary>
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

    /// <summary>FUN_00492bc4: charges the pending amount and clears it.</summary>
    public int Commit()
    {
        var amount = Pending;
        if (amount != 0) budget.AlterFamilyBudget(ExpenseCategory, amount);
        Pending = 0;
        return amount;
    }

    /// <summary>FUN_004951a5's neighbour test for a new segment: a cardinal edge may not run into a diagonal wall of the
    /// tiles that touch its ends (bit 1: 0x10 at (x-1,y-1) or (x,y+1), 0x20 at (x,y-1) or (x-1,y+1); bit 2: 0x10 at
    /// (x-1,y-1) or (x+1,y), 0x20 at (x-1,y) or (x+1,y-1)), and a diagonal may not meet a cardinal edge on its far corners
    /// (0x10: bits 4 or 8 at (x-1,y-1), bits 1 or 2 at (x+1,y+1); 0x20: bits 2 or 4 at (x-1,y+1), bits 1 or 8 at (x+1,y-1)).
    /// Tiles outside 1..62 are not looked at. The refusal sets no error code.</summary>
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

    /// <summary>FUN_0053c06c on the tile's record: a diagonal may not join a tile that holds any other wall bit (a mirror
    /// bit 4 or 8 included), and a cardinal edge may not run beside a tile across it that holds a diagonal (bit 1: the
    /// tile at x - 1, bit 2: y - 1). The refusal sets no error code.</summary>
    private bool TileTestFails(int x, int y, int bit)
    {
        if (bit is 0x10 or 0x20) return (walls[x, y][0] & 0xf) != 0;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return (walls[nx, ny][0] & 0x30) != 0;
    }

    /// <summary>FUN_004951a5's tile flag test (error 0x20): the owner tile and, for a cardinal edge, the tile across it
    /// both lack the buildable bit 0 (a diagonal looks at its own tile only). Fences skip it (FUN_00492b26 is 1).</summary>
    private bool Unbuildable(int x, int y, int bit, bool diagonal)
    {
        if (tiles!.Buildable(x, y, walls.Level)) return false;
        if (diagonal) return true;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return !tiles.Buildable(nx, ny, walls.Level);
    }

    /// <summary>FUN_004951a5's pool refusal (error 0x1d): a cardinal edge with pool tiles on both sides, or a diagonal on a pool tile.</summary>
    private bool PoolBlocks(int x, int y, int bit, bool diagonal)
    {
        if (!pool!.IsPool(x, y, walls.Level)) return false;
        if (diagonal) return true;
        var (nx, ny) = bit == 1 ? (x - 1, y) : (x, y - 1);
        return pool.IsPool(nx, ny, walls.Level);
    }

    /// <summary>FUN_004951a5's pond/water refusal (error 0x1d): a cardinal edge with water tiles on both sides, or a diagonal on a water tile.</summary>
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

    /// <summary>Non-Sim objects on the edge's two tiles whose flags (rotated by their direction) name that edge: the
    /// owner tile sees it as <paramref name="bit"/>, the neighbour across it as the opposite bit (4 or 8).</summary>
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
