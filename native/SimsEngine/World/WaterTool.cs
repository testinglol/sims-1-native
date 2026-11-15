using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>The pond/water tool's rectangular add/remove operation (cWaterTool, FUN_00497d79, FUN_0049847a, FUN_00498291,
/// FUN_00498182, FUN_0047740f, and FUN_0047759d). Ponds are a ground-floor-only operation stored in Arry 10.</summary>
public sealed class WaterTool(SimulationWorld world, LotWater water, LotFloors floors, LotPool pools, LotTiles tiles,
    LotTerrain terrain, LotWalls walls, FloorCatalog catalog, IFamilyBudget budget, Func<int, bool>? isWaterObject = null)
{
    public const int ExpenseCategory = 7;

    public PoolToolResult Rectangle(int fromX, int fromY, int toX, int toY, bool erase)
    {
        if (floors.Level != 1) throw new ArgumentException("Water can only be edited on the ground floor");
        var left = Math.Min(fromX, toX);
        var right = Math.Max(fromX, toX);
        var top = Math.Min(fromY, toY);
        var bottom = Math.Max(fromY, toY);
        if (left < 1 || top < 1 || right >= LotWalls.Size - 1 || bottom >= LotWalls.Size - 1)
            return Refused("outside-lot", erase);

        var reference = terrain.Corners(fromX, fromY).ToArray();
        var changed = new List<(int X, int Y)>();
        for (var x = left; x <= right; x++)
        for (var y = top; y <= bottom; y++)
        {
            if (water.IsWater(x, y, 1) != erase) continue;
            if (!tiles.InsideLot(x, y) || (!erase && (!tiles.Buildable(x, y, 1) || !SameCorners(x, y, reference))))
                return Refused("unbuildable", erase);
            var objects = world.ObjectsAtTile(x, y, 1)
                .Where(item => item[ObjectData.Hidden] == 0 && item.Definition[(int)ObjdField.Type] != 9)
                .ToArray();
            if (!erase)
            {
                if (objects.Any(item => !(item.Definition[(int)ObjdField.RoomSort] == 0 &&
                        (item.Definition[(int)ObjdField.BuildModeType] == 2 || item.TypeRecord?.AutonomyAlternativeTypeFlag == 2))))
                    return Refused("occupied", erase);
                if (pools.Occupies(x, y, 1)) return Refused("pool", erase);
                if (WallBlocks(x, y, left, top, right, bottom)) return Refused("blocked-by-wall", erase);
            }
            else if (objects.Length > 0)
            {
                if (objects.Any(item =>
                        (item.Definition[(int)ObjdField.RoomSort] == 0 &&
                         (item.Definition[(int)ObjdField.BuildModeType] == 7 || item.TypeRecord?.AutonomyAlternativeTypeFlag == 7)) ||
                        (isWaterObject?.Invoke(item.TypeRecord?.Key ?? 0) ?? false)))
                    return Refused("occupied", erase);
                return Refused("occupied", erase);
            }
            changed.Add((x, y));
        }

        if (changed.Count == 0) return Refused(erase ? "not-water" : "already-water", erase);

        var price = catalog.WaterPrice();
        var charge = erase ? 0 : checked(price * changed.Count);
        if (charge > budget.ReadFamilyBudget()) return Refused("insufficient-funds", erase, insufficientFunds: true);

        foreach (var (x, y) in changed)
        {
            if (erase)
            {
                water.ClearWater(x, y);
                if (floors[x, y] == 65534) floors[x, y] = 0;
            }
            else
            {
                water.SetWater(x, y);
                floors[x, y] = 0;
            }
        }
        if (charge != 0) budget.AlterFamilyBudget(ExpenseCategory, charge);
        return new PoolToolResult(true, erase ? "WATER_ERASE" : "WATER_RECT", changed.Count, charge, null, false,
            budget.ReadFamilyBudget());
    }

    /// <summary>FUN_0049847a lines 108 to 138: a tile with wall data is refused for a diagonal wall (0x30), or for a
    /// cardinal wall edge when the neighbour across that edge is inside the rectangle or is already water.</summary>
    private bool WallBlocks(int x, int y, int left, int top, int right, int bottom)
    {
        if ((walls[x, y][0] & 0x30) != 0) return true;
        (int Dx, int Dy)[] directions = [(-1, 0), (0, -1), (1, 0), (0, 1)];
        foreach (var (dx, dy) in directions)
        {
            var nx = x + dx;
            var ny = y + dy;
            var inside = nx >= left && nx <= right && ny >= top && ny <= bottom;
            if (!inside && !water.IsWater(nx, ny, 1)) continue;
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

    private PoolToolResult Refused(string reason, bool erase, bool insufficientFunds = false) =>
        new(false, erase ? "WATER_ERASE" : "WATER_RECT", 0, 0, reason, insufficientFunds, budget.ReadFamilyBudget());
}
