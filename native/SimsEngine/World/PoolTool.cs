using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

public sealed record PoolToolResult(bool Applied, string Command, int Changed, int Charge, string? Refused,
    bool InsufficientFunds, int Budget);

/// <summary>The pool tool's rectangular add/remove operation (cPoolTool::Click and cPoolLayer::AddPool/RemovePool).
/// Pools are a ground-floor-only operation. Arry 9 stores water/deck occupancy while the ground-floor pattern plane
/// stores the matching 65535 pool artwork sentinel.</summary>
public sealed class PoolTool(SimulationWorld world, LotFloors floors, LotPool pools, LotTiles tiles,
    LotTerrain terrain, LotWalls walls, FloorCatalog catalog, IFamilyBudget budget, Func<int, bool>? isPoolAccessory = null, LotWater? water = null)
{
    public const int ExpenseCategory = 7;

    public PoolToolResult Rectangle(int fromX, int fromY, int toX, int toY, bool erase)
    {
        if (floors.Level != 1) throw new ArgumentException("Pools can only be edited on the ground floor");
        var left = Math.Min(fromX, toX);
        var right = Math.Max(fromX, toX);
        var top = Math.Min(fromY, toY);
        var bottom = Math.Max(fromY, toY);
        if (left < 1 || top < 1 || right >= LotWalls.Size - 1 || bottom >= LotWalls.Size - 1)
            return Refused("outside-lot");

        // cPoolTool::AllTilesPoolable error 0x11: the corner heights Float stores from the start tile must all be equal, and
        // every tile of the rectangle must carry exactly those four corners.
        var reference = terrain.Corners(fromX, fromY).ToArray();
        var changed = new List<(int X, int Y)>();
        for (var x = left; x <= right; x++)
        for (var y = top; y <= bottom; y++)
        {
            if (pools.IsPool(x, y, 1) != erase) continue;
            if (!tiles.InsideLot(x, y) || (!erase && (!tiles.Buildable(x, y, 1) || !SameCorners(x, y, reference)))) return Refused("unbuildable");
            if (!erase && water?.IsWater(x, y, 1) == true) return Refused("water");
            // cPoolTool::AllTilesPoolable (error 0xb, FUN_0048fc3a) ignores hidden objects and type 9 cursors, and only
            // exempts windows (FUN_00487d5b(selector, 4): RoomSort == 0 && BuildModeType == 2).
            if (!erase && world.ObjectsAtTile(x, y, 1).Any(item =>
                    item[ObjectData.Hidden] == 0 && item.Definition[(int)ObjdField.Type] != 9 &&
                    !(item.Definition[(int)ObjdField.RoomSort] == 0 &&
                      (item.Definition[(int)ObjdField.BuildModeType] == 2 || item.TypeRecord?.AutonomyAlternativeTypeFlag == 2))))
                return Refused("occupied");
            if (erase && DepoolRefusal(x, y) is { } refusal) return Refused(refusal);
            if (!erase && WallBlocks(x, y, left, top, right, bottom)) return Refused("blocked-by-wall");
            changed.Add((x, y));
        }

        if (changed.Count == 0) return Refused(erase ? "not-pool" : "already-pool");

        var price = catalog.Price(65535);
        var charge = erase ? 0 : checked(price * changed.Count);
        if (charge > budget.ReadFamilyBudget()) return Refused("insufficient-funds", insufficientFunds: true);

        foreach (var (x, y) in changed)
        {
            if (erase) pools.RemovePool(x, y);
            else pools.AddPool(x, y);
            floors[x, y] = erase ? (ushort)0 : (ushort)65535;
        }
        pools.RebuildDeck(tiles, terrain, walls, (x, y) => world.ObjectsAtTile(x, y, 1).Any());
        if (charge != 0) budget.AlterFamilyBudget(ExpenseCategory, charge);
        return new PoolToolResult(true, erase ? "POOL_ERASE" : "POOL_RECT", changed.Count, charge, null, false,
            budget.ReadFamilyBudget());
    }

    /// <summary>cPoolTool::AllTilesDepoolable errors 0x2c and 0x2d. When the tile's room (value below 0xfffb) is a pool
    /// room (Room::IsPool, the byte set from HasPool of the room's first tile), any lot object whose +0x84 room equals it
    /// refuses with 0x2c. Then any object on the tile whose master BuildModeType (+0x8a) is 7, a pool accessory, refuses
    /// with 0x2d.</summary>
    private string? DepoolRefusal(int x, int y)
    {
        if (world.LotRoomMaps.TryGetValue(1, out var rooms) && rooms.RoomAt(x, y) is var room and < 0xfffb && pools.IsPool(x, y, 1)
            && world.ObjectTableEntries.Any(item => item is not null && (ushort)item[ObjectData.Room] == room))
            return "pool-room-not-empty";
        return world.ObjectsAtTile(x, y, 1).Any(item =>
                item[ObjectData.Hidden] == 0 && item.Definition[(int)ObjdField.Type] != 9 &&
                ((item.Definition[(int)ObjdField.RoomSort] == 0 &&
                  (item.Definition[(int)ObjdField.BuildModeType] == 7 || item.TypeRecord?.AutonomyAlternativeTypeFlag == 7)) ||
                 (isPoolAccessory?.Invoke(item.TypeRecord?.Key ?? 0) ?? false)))
            ? "pool-accessory"
            : null;
    }

    /// <summary>cPoolTool::AllTilesPoolable error 7 (FUN_0048fc3a lines 107 to 137). A tile that carries wall data is refused
    /// for a diagonal wall (segments 0x30), or for a wall on one of its four edges (TileWalls::GetWallBetween) when the
    /// neighbour across that edge lies inside the rectangle or is already a pool tile (layer value below 0x10). Native stores
    /// the -x and -y edges in the tile's own record, so the +x and +y edges are read from either the mirror bits or the neighbour's.</summary>
    private bool WallBlocks(int x, int y, int left, int top, int right, int bottom)
    {
        if ((walls[x, y][0] & 0x30) != 0) return true;
        (int Dx, int Dy)[] directions = [(-1, 0), (0, -1), (1, 0), (0, 1)];
        foreach (var (dx, dy) in directions)
        {
            var nx = x + dx;
            var ny = y + dy;
            var inside = nx >= left && nx <= right && ny >= top && ny <= bottom;
            if (!inside && !pools.IsPool(nx, ny, 1)) continue;
            var edge = dx < 0 ? walls[x, y][0] & 1
                : dy < 0 ? walls[x, y][0] & 2
                : dx > 0 ? (walls[x, y][0] & 4) | (walls[nx, ny][0] & 1)
                : (walls[x, y][0] & 8) | (walls[nx, ny][0] & 2);
            if (edge != 0) return true;
        }
        return false;
    }

    private bool SameCorners(int x, int y, byte[] reference)
    {
        if (reference[0] != reference[1] || reference[0] != reference[2] || reference[0] != reference[3]) return false;
        var corners = terrain.Corners(x, y);
        return corners[0] == reference[0] && corners[1] == reference[1] && corners[2] == reference[2] && corners[3] == reference[3];
    }

    private PoolToolResult Refused(string reason, bool insufficientFunds = false) =>
        new(false, reason == "not-pool" ? "POOL_ERASE" : "POOL_RECT", 0, 0, reason, insufficientFunds,
            budget.ReadFamilyBudget());
}
