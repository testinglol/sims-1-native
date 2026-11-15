using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>The build-mode terrain tools (FUN_00491f06 raise and lower, FUN_00485361 level). Heights are one byte per
/// vertex, 0 to 0x40. Neighbouring vertices may differ by at most 5, so a change that exceeds it pulls the neighbour to
/// within 5 and carries on from there (FUN_00491375); the charge is the price per unit of height moved, counting the
/// units pulled along. A vertex only changes when no wall, floor or pool touches the four tiles around it on any level
/// and no object that needs flat ground sits on them (FUN_00491801). A refused edit changes nothing, as the original
/// restores its altitude snapshot, and a charge the household cannot afford is refused too (FUN_00492b81).</summary>
public sealed class TerrainTool(SimulationWorld world, LotTerrain terrain, IFamilyBudget budget, int unitPrice, LotTiles? tiles = null,
    LotPool? pool = null, IReadOnlyList<LotFloors>? floors = null, IReadOnlyList<LotWalls>? walls = null, LotWater? water = null)
{
    public const int ExpenseCategory = 7;
    public const int MaximumHeight = 0x40;
    private const int Slope = 5;
    private static readonly (int X, int Y)[] Neighbours = [(-1, 0), (0, -1), (1, 0), (0, 1)];

    /// <summary>Tool +0x20: what the stroke's commit will charge.</summary>
    public int Pending { get; private set; }

    /// <summary>Tool +0x3e: the last edit was refused for lack of funds.</summary>
    public bool InsufficientFunds { get; private set; }

    /// <summary>Tool +0x38 is nonzero after a refusal: the edit hit a vertex that cannot change.</summary>
    public bool Refused { get; private set; }

    /// <summary>FUN_00491113 for one vertex: moves it by <paramref name="delta"/> (clamped to 0..0x40) and lets the slope limit
    /// carry the change outwards. Returns the vertices whose height changed, or zero when the edit was refused.</summary>
    public int Raise(int x, int y, int target) => Apply([(x, y)], (_, _) => target, [(x, y)]);

    /// <summary>FUN_00491f06 smooth hill mode: jitters 7 times around (<paramref name="x"/>, <paramref name="y"/>) with
    /// CenteredJitter(4) (FUN_0046f59a), calls TrySmoothVertex (FUN_00490f92) on valid 4-neighbours and twice on the
    /// jittered center, rolling back terrain and pending cost on any refusal or insufficient funds.</summary>
    public int SmoothRaise(int x, int y, int delta, Func<int>? nextRandom = null)
    {
        var rng = nextRandom ?? (() => Random.Shared.Next(0, 0x8000));
        Refused = false;
        InsufficientFunds = false;
        var outerSaved = terrain.Snapshot();
        var outerPending = Pending;

        int CenteredJitter4()
        {
            var u1 = (int)((uint)rng() % 4);
            var u2 = (int)((uint)rng() % 4);
            var m = Math.Min(u1, u2);
            return (rng() & 1) != 0 ? -m : m;
        }

        bool TrySmoothVertex(int vx, int vy, int stepDelta)
        {
            if (vx < 2 || vx > LotWalls.Size - 2 || vy < 2 || vy > LotWalls.Size - 2 || !Editable(vx, vy))
            {
                Refused = true;
                return false;
            }
            var sum = 0;
            var count = 0;
            foreach (var (dx, dy) in Neighbours)
            {
                var nx = vx + dx;
                var ny = vy + dy;
                if (nx < 2 || nx > LotWalls.Size - 2 || ny < 2 || ny > LotWalls.Size - 2) continue;
                sum += terrain.Height(nx, ny);
                count++;
            }
            var cur = terrain.Height(vx, vy);
            var target = Math.Clamp((sum / count + cur) / 2 + stepDelta, 0, MaximumHeight);
            if (target == cur) return true;
            Raise(vx, vy, target);
            return !Refused && !InsufficientFunds;
        }

        for (var iteration = 0; iteration < 7; iteration++)
        {
            var rx = x + CenteredJitter4();
            var ry = y + CenteredJitter4();
            if (rx < 2 || rx > LotWalls.Size - 2 || ry < 2 || ry > LotWalls.Size - 2) continue;
            foreach (var (dx, dy) in Neighbours)
            {
                var nx = rx + dx;
                var ny = ry + dy;
                if (nx < 2 || nx > LotWalls.Size - 2 || ny < 2 || ny > LotWalls.Size - 2) continue;
                if (!TrySmoothVertex(nx, ny, delta))
                {
                    terrain.Restore(outerSaved);
                    Pending = outerPending;
                    return 0;
                }
            }
            if (!TrySmoothVertex(rx, ry, delta) || !TrySmoothVertex(rx, ry, delta))
            {
                terrain.Restore(outerSaved);
                Pending = outerPending;
                return 0;
            }
        }

        var changedCount = 0;
        for (var vx = 0; vx < LotWalls.Size; vx++)
        for (var vy = 0; vy < LotWalls.Size; vy++)
            if (terrain.Height(vx, vy) != outerSaved[(vx * LotWalls.Size + vy) * 4 + 1])
                changedCount++;
        return changedCount;
    }

    /// <summary>FUN_00485361: brings every vertex of the inclusive rectangle to <paramref name="height"/>.</summary>
    public int Level(int left, int top, int right, int bottom, int height)
    {
        var vertices = new List<(int X, int Y)>();
        for (var x = left; x <= right; x++)
        for (var y = top; y <= bottom; y++)
            vertices.Add((x, y));
        var ring = vertices.Where(vertex => vertex.X == left || vertex.X == right || vertex.Y == top || vertex.Y == bottom).ToList();
        return Apply(vertices, (_, _) => height, ring, (left, top, right, bottom));
    }

    private int Apply(List<(int X, int Y)> vertices, Func<int, int, int> target, List<(int X, int Y)> frontier,
        (int Left, int Top, int Right, int Bottom)? levelBounds = null)
    {
        Refused = false;
        InsufficientFunds = false;
        var saved = terrain.Snapshot();
        var cost = 0;
        var changed = new HashSet<(int X, int Y)>();
        var processed = new HashSet<(int X, int Y)>();
        foreach (var vertex in vertices)
        {
            var checkObjects = levelBounds is not { } bounds ||
                               vertex.X == bounds.Left || vertex.X == bounds.Right ||
                               vertex.Y == bounds.Top || vertex.Y == bounds.Bottom;
            if (!Editable(vertex.X, vertex.Y, checkObjects, levelBounds)) return Refuse(saved);
            var height = Math.Clamp(target(vertex.X, vertex.Y), 0, MaximumHeight);
            var current = terrain.Height(vertex.X, vertex.Y);
            if (height == current) continue;
            cost += Math.Abs(current - height) * unitPrice;
            terrain.SetHeight(vertex.X, vertex.Y, (byte)height);
            changed.Add(vertex);
        }
        if (levelBounds is { } rect)
        {
            // FUN_00485361 lines 113 to 171: interior tiles allow multi-tile objects without PlacementFlags 0x100 only
            // when every member of the linked chain lies within the leveled tile rectangle, charging $5 per tile.
            for (var tx = rect.Left; tx < rect.Right; tx++)
            for (var ty = rect.Top; ty < rect.Bottom; ty++)
            {
                foreach (var item in world.ObjectsAtTile(tx, ty, 1))
                {
                    if (item[ObjectData.Hidden] != 0 ||
                        item.Definition[(int)ObjdField.MasterId] == 0 ||
                        (unchecked((ushort)item[ObjectData.PlacementFlags]) & 0x100) != 0)
                        continue;
                    foreach (var member in world.MultiTileMembers(item))
                    {
                        var (mx, my) = MemberTile(world, member);
                        if (mx < rect.Left || mx >= rect.Right || my < rect.Top || my >= rect.Bottom)
                            return Refuse(saved);
                    }
                    cost += 5;
                }
            }
        }
        foreach (var vertex in vertices)
            if (!frontier.Contains(vertex)) processed.Add(vertex);
        var pending = frontier.ToList();
        for (var index = 0; index < pending.Count; index++)
        {
            var vertex = pending[index];
            processed.Add(vertex);
            int height = terrain.Height(vertex.X, vertex.Y);
            foreach (var (dx, dy) in Neighbours)
            {
                var neighbour = (X: vertex.X + dx, Y: vertex.Y + dy);
                if (neighbour.X < 2 || neighbour.Y < 2 || neighbour.X > LotWalls.Size - 2 || neighbour.Y > LotWalls.Size - 2) continue;
                var difference = terrain.Height(neighbour.X, neighbour.Y) - height;
                if (Math.Abs(difference) <= Slope || pending.Contains(neighbour)) continue;
                if (processed.Contains(neighbour) || !Editable(neighbour.X, neighbour.Y)) return Refuse(saved);
                var clampedNeighbour = Math.Clamp(difference > 0 ? height + Slope : height - Slope, 0, MaximumHeight);
                terrain.SetHeight(neighbour.X, neighbour.Y, (byte)clampedNeighbour);
                cost += (Math.Abs(difference) - Slope) * unitPrice;
                changed.Add(neighbour);
                pending.Add(neighbour);
            }
        }
        if (cost > 0 && Pending + cost > budget.ReadFamilyBudget())
        {
            InsufficientFunds = true;
            terrain.Restore(saved);
            return 0;
        }
        Pending += cost;
        return changed.Count;
    }

    private static (int X, int Y) MemberTile(SimulationWorld world, XObject member)
    {
        var shiftedX = member.Position.X >> 4;
        var shiftedY = member.Position.Y >> 4;
        if (member.Position.X >= LotWalls.Size || member.Position.Y >= LotWalls.Size ||
            world.ObjectsAtTile(shiftedX, shiftedY, Math.Max(1, (int)member.Position.Height)).Contains(member))
            return (shiftedX, shiftedY);
        return (member.Position.X, member.Position.Y);
    }

    private int Refuse(byte[] saved)
    {
        Refused = true;
        terrain.Restore(saved);
        return 0;
    }

    /// <summary>FUN_00491801: the vertex lies inside the lot, and the four tiles around it are free of walls, floors, pools and,
    /// when <paramref name="checkObjects"/> is true, objects that must stand on flat ground, on both levels.</summary>
    private bool Editable(int x, int y, bool checkObjects = true, (int Left, int Top, int Right, int Bottom)? levelBounds = null)
    {
        if (x < 2 || y < 2 || x > LotWalls.Size - 2 || y > LotWalls.Size - 2) return false;
        foreach (var (tileX, tileY) in new[] { (x, y), (x - 1, y), (x - 1, y - 1), (x, y - 1) })
        {
            if (tiles is not null && !tiles.InsideLot(tileX, tileY)) return false;
            if (floors is not null && floors.Any(level => level[tileX, tileY] != 0)) return false;
            if (walls is not null && walls.Any(level => level[tileX, tileY][0] != 0)) return false;
            if (pool?.Occupies(tileX, tileY, 1) == true) return false;
            if (water?.IsWater(tileX, tileY, 1) == true) return false;
            if (checkObjects)
            {
                var isInteriorTile = levelBounds is { } b && tileX >= b.Left && tileX < b.Right && tileY >= b.Top && tileY < b.Bottom;
                for (var level = 1; level <= 2; level++)
                    if (world.ObjectsAtTile(tileX, tileY, level).Any(item =>
                            !(isInteriorTile && level == 1 && item[ObjectData.Hidden] == 0 &&
                              item.Definition[(int)ObjdField.MasterId] != 0 &&
                              (unchecked((ushort)item[ObjectData.PlacementFlags]) & 0x100) == 0) &&
                            (unchecked((ushort)item[ObjectData.PlacementFlags]) & 1) == 0)) return false;
            }
        }
        return true;
    }

    /// <summary>FUN_00492bc4: charges the pending amount and clears it.</summary>
    public int Commit()
    {
        var amount = Pending;
        budget.AlterFamilyBudget(ExpenseCategory, amount);
        Pending = 0;
        return amount;
    }
}
