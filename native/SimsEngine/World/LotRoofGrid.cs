using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.World;

/// <summary>FUN_0056e1dc's room-to-cell pass, followed by FUN_0056ea87's planner. Rebuild from current lot layers and
/// objects, rather than retaining grown/shape/height bytes between rebuilds (FUN_0056e1c7 clears the original grid).
/// Room membership and outside state use RoomMap, including its documented edge-reachability inference.</summary>
public static class LotRoofGrid
{
    public static RoofGrid Build(IffFile lot, int level, IEnumerable<XObject> objects, bool grow = false)
    {
        var grid = Seed(lot, level, objects);
        grid.Plan(grow);
        return grid;
    }

    public static RoofGrid Build(IffFile lot, int level, IEnumerable<XObject> objects, RoofGrid? upperRoof)
    {
        var grid = Seed(lot, level, objects);
        grid.Plan(upperRoof);
        return grid;
    }

    /// <summary>Seeds four half-tile cells with bits 0 and 3 before growth, height and shape passes. Coordinates are
    /// [row = 2*y, column = 2*x], matching RoofGrid. The Sims.exe room-grid sentinel -5 tests both adjacent rooms;
    /// RoomMap represents such diagonal tiles with NoRoom and AdjacentRoomsAtTile.</summary>
    public static RoofGrid Seed(IffFile lot, int level, IEnumerable<XObject> objects)
    {
        if (level is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(level));
        var rooms = RoomMap.Build(lot, level, LotWalls.Size, LotWalls.Size);
        var terrain = LotTerrain.Read(lot);
        // FUN_0047870b leaves an omitted floor plane zero-filled.
        var upperFloors = level == 1 && (lot.Find(ResourceType.Parse("Arry"), 111) is not null ||
            lot.Find(ResourceType.Parse("Arry"), 101) is not null) ? LotFloors.Read(lot, 2) : null;
        var items = objects.ToArray();
        var localObjects = items.Where(item => item.Position.Height == level)
            .ToLookup(item => (X: item.Position.X >> 4, Y: item.Position.Y >> 4));
        var upperObjects = items.Where(item => item.Position.Height == 2)
            .Select(item => (X: item.Position.X >> 4, Y: item.Position.Y >> 4)).ToHashSet();
        var roofable = new Dictionary<ushort, bool>();
        foreach (var room in rooms.RoomIds)
        {
            // FUN_004689d6 reads the four terrain corner bytes for EVERY retained room tile. Each tile must be
            // flat, but different tiles need not share the same height. FUN_00467ee7 sets cXRoom +0x74 for a
            // category-8 object with Hidden == 0 and Room >= 0, provided the room key's low ten bits are nonzero.
            var tiles = rooms.TilesInRoom(room).ToArray();
            var flat = tiles.All(tile => terrain.IsFlatAt(tile.X, tile.Y, terrain.ReferenceHeight(tile.X, tile.Y)));
            var roomOverride = (room & 0x3ff) != 0 && tiles.Any(tile => localObjects[tile].Any(item =>
                item.Data[(int)ObjectData.Hidden] == 0 && item.RoomWord84 >= 0 &&
                item.Data[(int)ObjectData.Category] == 8));
            roofable[room] = flat && (roomOverride || !rooms.IsOutside(room));
        }
        bool Roofable(ushort room) => roofable.GetValueOrDefault(room);
        var grid = new RoofGrid(LotWalls.Size * 2);
        for (var x = 0; x < LotWalls.Size; x++)
        for (var y = 0; y < LotWalls.Size; y++)
        {
            var room = rooms.RoomAt(x, y);
            var has = room != RoomMap.NoRoom ? Roofable(room)
                : rooms.AdjacentRoomsAtTile(x, y) is { } pair && (Roofable(pair.First) || Roofable(pair.Second));
            // FUN_0056e1dc reads the level-2 FLOOR word at manager +0x28, not its room id. The second test
            // first checks level-2 object occupancy (FUN_004325af on the level-2 key), then iterates the level-1
            // objects at this tile (FUN_0043f371 on the level-1 key) and resolves their alternate definition
            // (FUN_004573f9), testing BuildModeType +0x8a == 3. Neither test applies upstairs.
            var blocked = level == 1 && ((upperFloors?[x, y] ?? 0) != 0 || upperObjects.Contains((x, y)) &&
                localObjects[(x, y)].Any(item => item.TypeRecord?.AutonomyAlternativeTypeFlag == 3));
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++) grid.Seed(y * 2 + dy, x * 2 + dx, has && !blocked, blocked);
        }
        return grid;
    }
}
