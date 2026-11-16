using SimsEngine.Simulation;
using SimsEngine.World;

/// <summary>Computes room lighting by summing window and lamp contributions, then clamps sprite light
/// levels to [0.5, 1.0] by day and [0.5, 0.8] during other times of day.</summary>
internal static class SceneLighting
{
    internal record RGB(byte R, byte G, byte B);
    internal record Room(int Id, bool Outside, int Ambient, int Daylight, byte R, byte G, byte B);
    internal record State(RGB Outside, Room[] Rooms, RGB Backdrop, int Minutes, int Phase, Light[]? Lights = null,
        Window[]? Windows = null);
    internal record Light(float X, float Y, float Toward, int Strength, int Level);
    internal record Window(int X, int Y, int Level);

    /// <summary>Determines whether an object is a direct point light source rather than an ambient room
    /// lamp, requiring an active lit flag, nonzero lighting contribution, and the light source flag set.
    /// </summary>
    public static bool IsLightSource(XObject item) =>
        item is not XPerson && item[ObjectData.Hidden] == 0 && item.RoomWord84 >= 0 &&
        (item[ObjectData.FlagField2] & 0x400) != 0 && item[ObjectData.LightingContribution0100] != 0 &&
        item[ObjectData.LightSource] == 1 && InsideLot(item);

    /// <summary>Identifies window portal tiles via bit 0x4000 of FlagField2, allowing natural daylight to
    /// illuminate connected rooms.</summary>
    public static bool IsWindow(XObject item) =>
        item is not XPerson && (item[ObjectData.FlagField2] & 0x4000) != 0 && item.Position.X >= 0 && item.Position.Y >= 0;

    /// <summary>Vertical light source elevation offset, representing 5 world units above the floor at 3
    /// world units per tile.</summary>
    public const float LightLift = 5f / 3f;

    private static bool InsideLot(XObject item)
    {
        var x = item.Position.X >> 4;
        var y = item.Position.Y >> 4;
        return x > 0 && y > 0 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1;
    }

    public static State? Build(SimulationWorld world, RoomMap? roomMap, short house)
    {
        var allRoomIds = new SortedSet<ushort>();
        if (roomMap is not null)
        {
            foreach (var id in roomMap.RoomIds) allRoomIds.Add(id);
        }
        foreach (var map in world.LotRoomMaps.Values)
        {
            foreach (var id in map.RoomIds) allRoomIds.Add(id);
        }
        if (allRoomIds.Count == 0) return null;
        var phase = world.Clock.TimeOfDay;
        var outside = Level(ClampSpriteLight(OutsideLight(phase), phase));
        // Room keys encode the building story in upper bits, beginning at zero for level 1 and 0x400 for
        // level 2, while preserving the unassigned outdoor room index of 0xFF.
        var maxRoomId = Math.Max((int)allRoomIds.Max, RoomMap.NoRoom);
        var rooms = Enumerable.Range(0, maxRoomId + 1)
            .Select(id => new Room(id, true, 0, 100, outside, outside, outside)).ToArray();
        foreach (var roomId in allRoomIds)
        {
            var id = (int)roomId;
            if (world.RoomIsOutside(checked((short)roomId)))
            {
                rooms[id] = new Room(id, true, 0, 100, outside, outside, outside);
                continue;
            }
            if (!world.PersonRoomMotiveEntries.TryGetValue(roomId, out var entry))
                throw new InvalidDataException($"Room {id} has no native room-motive record");
            var light = ClampSpriteLight(SimulationWorld.DisplayedRoomLight(entry), phase);
            var level = Level(light);
            rooms[id] = new Room(id, false, Math.Min(100, entry.LightingObjectCount140), 0, level, level, level);
        }
        return new State(new RGB(outside, outside, outside), rooms, Backdrop(phase, house),
            world.Clock.Hour * 60 + world.Clock.Minute, phase);
    }

    private static float OutsideLight(int phase) => phase switch { 0 => 1f, 1 or 3 => 0.5f, _ => 0f };

    private static float ClampSpriteLight(float room, int phase) =>
        Math.Clamp(room, 0.5f, phase == 0 ? 1f : 0.8f);

    private static RGB Backdrop(int phase, short house) => house == 99 ? new RGB(0x14, 0x14, 0x8c) : phase switch
    {
        0 => new RGB(0x73, 0x73, 0x73),
        1 or 3 => new RGB(0x40, 0x40, 0x40),
        _ => new RGB(0x1a, 0x1a, 0x1a),
    };

    private static byte Level(float light) => (byte)Math.Clamp((int)Math.Round(light * 255), 0, 255);
}
