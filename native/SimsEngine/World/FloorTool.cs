using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>The build-mode floor tool's flat-tile paint and erase (cNewFloorTool, FUN_00488de7 / FUN_00489bba).
/// Charges accumulate in a pending amount (tool +0x20) that the mouse-up commit (FUN_00492bc4) charges to expense
/// category 7 (tool +0x24, set by FUN_00492ad8).
/// <para>Per tile, as the original decides: the tile must lie strictly inside the lot border; a tile that already holds
/// the pattern is left alone; otherwise the cost is the pattern's price (a refund of trunc(-0.5 - 0.8 price) for the
/// existing pattern when erasing), an unaffordable positive cost (FUN_00492b81) refuses with the insufficient-funds flag,
/// painting needs every object on the tile to have PlacementFlags bit 0 (FUN_004892c1 via FUN_004893e4) and erasing needs
/// bit 1, plus bit 9 above the ground floor (FUN_0048941c); a failed object test leaves the tile unchanged.</para>
/// <para>FUN_00488924 refuses every tile flagged 0x20 in Arry 8 (ground outside the playable lot) before anything else.
/// Painting also needs flat terrain (FUN_004892c1), the tile's buildable bit 0 in its level's grid (FUN_004893e4: Arry 8 or 108)
/// and no pool (FUN_004790e8 on Arry 9, ground floor only).</para></summary>
public sealed class FloorTool(SimulationWorld world, LotFloors floors, FloorCatalog catalog, IFamilyBudget budget, LotTerrain? terrain = null, LotPool? pool = null, LotTiles? tiles = null, LotWalls? walls = null, LotWater? water = null)
{
    public const int ExpenseCategory = 7;
    private const double RefundFactor = 0.800000011920929; // the float constant at 0x667d70

    /// <summary>Tool +0x20: the amount a commit will charge (negative is a refund).</summary>
    public int Pending { get; private set; }

    /// <summary>Tool +0x3e: the last tile was refused for lack of funds.</summary>
    public bool InsufficientFunds { get; private set; }

    /// <summary>FUN_00489bba: applies the pattern (0 erases) to every tile of the rectangle, first coordinate outermost.</summary>
    public int Rectangle(int fromX, int fromY, int toX, int toY, int pattern, int half = 0)
    {
        var changed = 0;
        for (var x = Math.Min(fromX, toX); x <= Math.Max(fromX, toX); x++)
        for (var y = Math.Min(fromY, toY); y <= Math.Max(fromY, toY); y++)
        {
            // cNewFloorTool::Drag uses ChangeFloor for a gesture that stays in one tile; a rectangle
            // spanning tiles paints both sides of any diagonal tile via its two FloorPattern slots.
            var singleTile = fromX == toX && fromY == toY;
            if (Tile(x, y, pattern, out var tileChanged, singleTile ? half : 0, !singleTile) && tileChanged) changed++;
        }
        return changed;
    }

    /// <summary>FUN_00488de7's room mode (cNewFloorTool::DoRoom): applies the ordinary per-tile floor change to
    /// the clicked room's wall-derived tile list. The same affordability, terrain, pool, and object checks apply.</summary>
    public int Fill(RoomMap rooms, int x, int y, int pattern, int half = 0)
    {
        if ((uint)x >= LotFloors.Size || (uint)y >= LotFloors.Size) return 0;
        if (half is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(half));
        // DoRoom resolves the clicked quadrant when a diagonal wall leaves the tile's center unassigned.
        var (sampleX, sampleY) = half switch
        {
            1 => (12, 4),
            2 => (4, 12),
            3 => (4, 4),
            4 => (12, 12),
            _ => (8, 8),
        };
        var room = rooms.RoomAtPosition(x * 16 + sampleX, y * 16 + sampleY);
        if (room == RoomMap.NoRoom) return 0;
        var initialPending = Pending;
        var snapshot = new List<(int X, int Y, ushort Floor, byte Wall2, byte Wall4, bool HadWall)>();
        var changed = 0;

        bool TryTile(int tileX, int tileY, int tileHalf = 0)
        {
            var prevFloor = floors[tileX, tileY];
            byte wall2 = 0, wall4 = 0;
            var hadWall = walls is not null;
            if (walls is not null)
            {
                var wall = walls[tileX, tileY];
                wall2 = wall[2];
                wall4 = wall[4];
            }
            if (!Tile(tileX, tileY, pattern, out var tileChanged, tileHalf))
            {
                if (InsufficientFunds)
                {
                    for (var i = snapshot.Count - 1; i >= 0; i--)
                    {
                        var item = snapshot[i];
                        floors[item.X, item.Y] = item.Floor;
                        if (item.HadWall && walls is not null)
                        {
                            var wall = walls[item.X, item.Y];
                            wall[2] = item.Wall2;
                            wall[4] = item.Wall4;
                        }
                    }
                    Pending = initialPending;
                    InsufficientFunds = true;
                    return false;
                }
                return true;
            }
            if (tileChanged)
            {
                snapshot.Add((tileX, tileY, prevFloor, wall2, wall4, hadWall));
                changed++;
            }
            return true;
        }

        foreach (var (tileX, tileY) in rooms.TilesInRoom(room).ToArray())
            if (!TryTile(tileX, tileY)) return 0;
        // A dividing diagonal has no center room cell. Mac DoRoom keeps each room-facing half as an
        // FTilePt entry; reproduce that by resolving the four quadrant samples against the selected room.
        if (walls is not null)
        {
            for (var tileY = 1; tileY < LotFloors.Size - 1; tileY++)
            for (var tileX = 1; tileX < LotFloors.Size - 1; tileX++)
            {
                var diagonal = walls[tileX, tileY][0] & 0x30;
                if (diagonal == 0) continue;
                var candidates = diagonal == 0x20 ? new[] { (Half: 1, X: 12, Y: 4), (Half: 2, X: 4, Y: 12) }
                    : new[] { (Half: 3, X: 4, Y: 4), (Half: 4, X: 12, Y: 12) };
                var matching = candidates.Where(candidate => rooms.RoomAtPosition(tileX * 16 + candidate.X, tileY * 16 + candidate.Y) == room).ToArray();
                foreach (var candidate in matching)
                    if (!TryTile(tileX, tileY, candidate.Half)) return 0;
            }
        }
        return changed;
    }

    /// <summary>FUN_00488de7 for one flat tile. Returns the function's result (false when the tile is outside the
    /// border or the cost was refused); <paramref name="changed"/> says whether the floor was rewritten.</summary>
    public bool Tile(int x, int y, int pattern, out bool changed, int half = 0, bool bothDiagonalSides = false)
    {
        changed = false;
        if (x < 1 || y < 1 || x > LotFloors.Size - 2 || y > LotFloors.Size - 2) return false;
        if (tiles is not null && !tiles.InsideLot(x, y)) return false;
        if (walls is not null)
        {
            var wall = walls[x, y];
            if ((wall[0] & 0x30) != 0)
                return DiagonalTile(x, y, pattern, half, bothDiagonalSides, wall, out changed);
        }
        var existing = floors[x, y];
        var isDeck = floors.Level == 1 && pool?.IsDeck(x, y, 1) == true;
        if (existing == pattern && !(pattern == 0 && isDeck)) return true;
        var objects = world.ObjectsAtTile(x, y, floors.Level).ToArray();
        int cost;
        if (pattern == 0)
        {
            cost = unchecked((int)(-0.5 - catalog.Price(existing) * RefundFactor));
            if (!Affordable(cost)) return Refuse();
            InsufficientFunds = false;
            var level = floors.Level > 1;
            if (objects.Any(item => (unchecked((ushort)item[ObjectData.PlacementFlags]) & 2) == 0 ||
                    (level && (unchecked((ushort)item[ObjectData.PlacementFlags]) & 0x200) == 0))) return true;
        }
        else
        {
            cost = catalog.Price(pattern);
            if (!Affordable(cost)) return Refuse();
            InsufficientFunds = false;
            if (!FlatTile(x, y) || tiles?.Buildable(x, y, floors.Level) == false || pool?.IsPool(x, y, floors.Level) == true || water?.IsWater(x, y, floors.Level) == true || objects.Any(item => (unchecked((ushort)item[ObjectData.PlacementFlags]) & 1) == 0)) return true;
        }
        floors[x, y] = checked((ushort)pattern);
        if (isDeck) pool!.ClearDeck(x, y);
        Pending += cost;
        changed = true;
        return true;
    }

    /// <summary>Mac ChangeFloor stores the two diagonal floor patterns in the existing wall record, alongside the
    /// single-cell floor sentinel. The rendered split reads TopLeftPattern/TopLeftStyle (record bytes 4/2).</summary>
    private bool DiagonalTile(int x, int y, int pattern, int half, bool bothSides, Span<byte> wall, out bool changed)
    {
        changed = false;
        if (half is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(half));
        var diagonal = wall[0] & 0x30;
        var targetByte = diagonal == 0x20
            ? half == 1 ? 4 : half == 2 ? 2 : 2
            : half == 3 ? 2 : half == 4 ? 4 : 2;
        var otherByte = targetByte == 2 ? 4 : 2;
        var existing = wall[targetByte];
        // Mac branches around pricing and the both-sides mutation together when the selected
        // quadrant already matches, even if the opposite slot differs.
        if (existing == pattern) return true;

        // FUN_00488de7 prices the requested pattern, not the previous diagonal slot. Its +0x18
        // flag is the ChangeFloor both-sides argument: DoRectangle passes true (full price), while
        // single-quadrant DoRoom/Drag paths pass false (half price). The diagonal branch uses this
        // same rule for pattern 0, so erasing a diagonal half has the catalog's pattern-0 price.
        var cost = catalog.Price(pattern);
        if (!bothSides) cost = unchecked((cost + (cost < 0 ? 1 : 0)) >> 1);
        if (!Affordable(cost)) return Refuse();
        InsufficientFunds = false;
        var objects = world.ObjectsAtTile(x, y, floors.Level).ToArray();
        if (pattern == 0)
        {
            var level = floors.Level > 1;
            if (objects.Any(item => (unchecked((ushort)item[ObjectData.PlacementFlags]) & 2) == 0 ||
                    (level && (unchecked((ushort)item[ObjectData.PlacementFlags]) & 0x200) == 0))) return true;
        }
        else if (!FlatTile(x, y) || tiles?.Buildable(x, y, floors.Level) == false || pool?.IsPool(x, y, floors.Level) == true || water?.IsWater(x, y, floors.Level) == true ||
                 objects.Any(item => (unchecked((ushort)item[ObjectData.PlacementFlags]) & 1) == 0)) return true;

        var previousFloor = floors[x, y];
        wall[targetByte] = unchecked((byte)pattern);
        if (bothSides)
        {
            if (wall[otherByte] != pattern) wall[otherByte] = unchecked((byte)pattern);
        }
        else if (previousFloor != byte.MaxValue)
        {
            wall[otherByte] = unchecked((byte)previousFloor);
        }
        // FUN_00488de7 sets the base floor plane to 0xff unless erasing leaves both diagonal sides at 0.
        floors[x, y] = (ushort)(pattern == 0 && wall[otherByte] == 0 ? 0 : byte.MaxValue);
        Pending += cost;
        changed = true;
        return true;
    }

    /// <summary>FUN_004892c1's terrain test: a floor goes on a tile whose four corner heights are equal.</summary>
    private bool FlatTile(int x, int y)
    {
        if (terrain is null) return true;
        var corners = terrain.Corners(x, y);
        return corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3];
    }

    /// <summary>FUN_00492bc4: charges the pending amount and clears it.</summary>
    public int Commit()
    {
        var amount = Pending;
        budget.AlterFamilyBudget(ExpenseCategory, amount);
        Pending = 0;
        return amount;
    }

    /// <summary>FUN_00492b81: a refund or free tile always passes, a charge must fit the budget with the pending amount.</summary>
    private bool Affordable(int cost) => cost < 1 || Pending + cost <= budget.ReadFamilyBudget();

    private bool Refuse()
    {
        InsufficientFunds = true;
        return false;
    }
}
