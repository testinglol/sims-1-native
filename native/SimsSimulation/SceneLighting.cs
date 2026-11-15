using SimsEngine.Simulation;
using SimsEngine.World;

/// <summary>Builds the scene's room light table the way Sims.exe shades sprites: outside light is 1 by day and 0.5 at
/// dawn/dusk, window and lamp contributions are summed per room, and sprite light is clamped to [0.5, 1] by day and
/// [0.5, 0.8] otherwise. The native room refresh already stores the unclamped room light in LightLevel6c, so this
/// helper only applies the sprite clamp and the saved clock's phase.</summary>
internal static class SceneLighting
{
    internal record RGB(byte R, byte G, byte B);
    internal record Room(int Id, bool Outside, int Ambient, int Daylight, byte R, byte G, byte B);
    internal record State(RGB Outside, Room[] Rooms, RGB Backdrop, int Minutes, int Phase, Light[]? Lights = null,
        Window[]? Windows = null);
    /// <summary>A per-pixel light: medium-zoom scene pixels, the camera-depth <c>toward</c> value, the object's
    /// LightingContribution and its level.</summary>
    internal record Light(float X, float Y, float Toward, int Strength, int Level);
    internal record Window(int X, int Y, int Level);

    /// <summary>FUN_00467ee7 pushes a light into the room's source list only for an object that is lit (FlagField2
    /// 0x400, the inside-lot gate FUN_00432644 and a nonzero LightingContribution at +0xb0) and whose LightSource word
    /// (+0x6a) is 1, so a lamp without that word only shades its room. It reads the object's own position (+0xf4/+0xf8,
    /// not a slot) and its floor height (FUN_00478f70), turns them into world units (FUN_00454f9c: 3 per tile) and
    /// adds <see cref="LightLift"/> world units of height.</summary>
    public static bool IsLightSource(XObject item) =>
        item is not XPerson && item[ObjectData.Hidden] == 0 && item.RoomWord84 >= 0 &&
        (item[ObjectData.FlagField2] & 0x400) != 0 && item[ObjectData.LightingContribution0100] != 0 &&
        item[ObjectData.LightSource] == 1 && InsideLot(item);

    /// <summary>A window tile: FlagField2 bit 0x4000, the portal flag the room daylight walk reads.</summary>
    public static bool IsWindow(XObject item) =>
        item is not XPerson && (item[ObjectData.FlagField2] & 0x4000) != 0 && item.Position.X >= 0 && item.Position.Y >= 0;

    /// <summary>_DAT_00667678 (5.0 world units) over FUN_00454f9c's 3 world units per tile.</summary>
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
        var outside = Level(SpriteLight(OutsideLight(phase), phase, false, false));
        // FUN_00479f0c encodes level into the source room key ((level + 0x3f) * 0x400) | roomIndex,
        // narrowed to 16 bits: level 1 keys begin at zero, level 2 at 0x400, while RoomMap.NoRoom (0xff)
        // is preserved on outdoor/no-room sprites and must stay in range even on single-story lots.
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
            var light = SpriteLight(entry.LightLevel6c, phase, entry.LightingObjectCount140 == 0,
                entry.PersonObjectCount142 != 0);
            var level = Level(light);
            rooms[id] = new Room(id, false, Math.Min(100, entry.LightingObjectCount140), 0, level, level, level);
        }
        return new State(new RGB(outside, outside, outside), rooms, Backdrop(phase, house),
            world.Clock.Hour * 60 + world.Clock.Minute, phase);
    }

    /// <summary>FUN_004674e7: outside light is 1 by day, 0.5 at dawn and dusk, 0 at night.</summary>
    private static float OutsideLight(int phase) => phase switch { 0 => 1f, 1 or 3 => 0.5f, _ => 0f };

    /// <summary>FUN_004b4722/FUN_00466c7b: sprites clamp to [0.5, 1] by day and [0.5, 0.8] otherwise; an occupied room
    /// without a lit lamp is shown at least at 0.8 (FUN_004688e4).</summary>
    private static float SpriteLight(float room, int phase, bool lampless, bool occupied)
    {
        if (lampless && occupied && room < 0.8f) room = 0.8f;
        return Math.Clamp(room, 0.5f, phase == 0 ? 1f : 0.8f);
    }

    /// <summary>FUN_004aabcd: the screen fill behind the lot; house 99 uses the special dark-blue fill.</summary>
    private static RGB Backdrop(int phase, short house) => house == 99 ? new RGB(0x14, 0x14, 0x8c) : phase switch
    {
        0 => new RGB(0x73, 0x73, 0x73),
        1 or 3 => new RGB(0x40, 0x40, 0x40),
        _ => new RGB(0x1a, 0x1a, 0x1a),
    };

    private static byte Level(float light) => (byte)Math.Clamp((int)Math.Round(light * 255), 0, 255);
}
