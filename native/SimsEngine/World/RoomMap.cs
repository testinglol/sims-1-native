using SimsEngine.Formats;
using System.Buffers.Binary;

namespace SimsEngine.World;

/// <summary>The room of each tile of one level, computed as Sims.exe's room fill does when a lot loads (FUN_00479f0c).
/// A house file stores no room layer.
/// <para>Confirmed from the decompilation: each level is filled separately from its walls layer (Arry 2 for level 1,
/// 102 for level 2, 8-byte records indexed [x][y]). Tiles are seeded in scan order, outer loop x and inner loop y, and
/// each seed takes the next room number. The fill steps to the four neighbours of a tile unless the tile's own record
/// blocks that side (FUN_004351e4): side 1 is towards x - 1, 2 towards y - 1, 4 towards x + 1 and 8 towards y + 1, and a
/// side blocks when its segment bit is set and its wall style is not 2, 0xc, 0xd or 0xe. Sides 1 and 2 take their
/// style from record bytes 2 and 3; sides 4 and 8 from byte 2 of the x + 1 tile and byte 3 of the y + 1 tile
/// (FUN_00479737, FUN_0053bb44). The fill does not step onto a tile with a diagonal wall (byte 0 &amp; 0x30) whose style
/// (byte 3) divides rooms. On level 1, connectivity also requires matching water classification: Arry 9 values below
/// 0x10 form one class, other values the other; upper levels require matching zero/nonzero floor-grid state
/// (FUN_00479f0c).</para>
/// <para>The original routine collects each room's three-byte tile coordinates with a FIFO traversal; native retains
/// visitation order for <see cref="TilesInRoom"/>. Its source-side selector order is 8, 2, 4, 1. Room keys follow
/// Sims.exe's `((level + 0x3f) * 0x400) | roomIndex` encoding in `FUN_00479f0c`, narrowed to
/// 16 bits when stored. Thus level 1's base is 0 and level 2's is 0x400; bits 10–11 are the group read by room-manager
/// refresh. A diagonal wall tile has no single tile room; <see cref="RoomAtPosition"/> resolves each half against the
/// neighboring room ids as `FUN_00433c6a` does. The special floor sentinels (0xfffb to 0xffff) remain unmodelled.</para></summary>
/// <summary>One original eight-byte Arry wall-layer record.</summary>
public readonly record struct RoomWallTile(byte Segments, byte Byte1, byte Side1Style, byte Side2Style, byte Byte4, byte Byte5, byte Byte6, byte Byte7);

/// <summary>The runtime 14-byte wall-grid cell Sims.exe FUN_0047870b expands from an Arry wall record: two bytes,
/// followed by six words whose values are the corresponding source bytes zero-extended to 16 bits.</summary>
public readonly record struct RoomRuntimeWallCell(
    byte Segments, byte Byte1, ushort Word2, ushort Word4, ushort Word6, ushort Word8, ushort Word10, ushort Word12)
{
    public static RoomRuntimeWallCell FromSavedRecord(RoomWallTile tile) => new(
        tile.Segments, tile.Byte1, tile.Side1Style, tile.Side2Style, tile.Byte4, tile.Byte5, tile.Byte6, tile.Byte7);
}

/// <summary>The 44-byte expanded wall record returned by Sims.exe FUN_00479737. Field offsets are exposed as bytes
/// because the original record mixes 32-bit and 16-bit members and is consumed by several rendering helpers.</summary>
public readonly record struct RoomExpandedWallRecord(byte[] Bytes)
{
    public byte[] ToArray() => (byte[])Bytes.Clone();
    public uint UInt32At(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public ushort UInt16At(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset, 2));

    /// <summary>Sims.exe FUN_004633e3 writes the wall style to each selected one of six category words and sets
    /// the record's 0x10 overlay flag. The input record remains unchanged.</summary>
    public RoomExpandedWallRecord WithWallStyleOverlay(ushort wallStyle, uint selectedSegments)
    {
        var bytes = ToArray();
        for (var index = 0; index < 6; index++)
        {
            if ((selectedSegments & (1u << index)) == 0) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + index * 2, 2), wallStyle);
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x28, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28, 4), flags | 0x10);
        return new RoomExpandedWallRecord(bytes);
    }

    /// <summary>Reads the packed two-bit side orientation selected by Sims.exe FUN_0053bd4d. Cardinal wall bits use
    /// the word at expanded-record offset 4; diagonal bits return the source's default orientation 3.</summary>
    public byte WallSideOrientation(uint wallBit)
    {
        var index = CategoryIndex(wallBit);
        return index < 4 ? (byte)((UInt32At(4) >> (index * 2)) & 3) : (byte)3;
    }

    /// <summary>Writes the packed cardinal side orientation as Sims.exe FUN_0053bd78 does. Diagonal masks leave the
    /// record unchanged because the source writer only stores indices below 4.</summary>
    public RoomExpandedWallRecord WithWallSideOrientation(uint wallBit, byte orientation)
    {
        var index = CategoryIndex(wallBit);
        if (index >= 4) return this;
        var bytes = ToArray();
        var shift = index * 2;
        var directions = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        directions = (directions & ~(3u << shift)) | ((uint)(orientation & 3) << shift);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), directions);
        return new RoomExpandedWallRecord(bytes);
    }

    /// <summary>Rotates the six segment bits and wall-category words in Sims.exe FUN_0053bdc6. Cardinal orientation
    /// and adjacent-category fields rotate with their segments; diagonal orientation remains the source default and
    /// diagonal adjacent-category remapping is not represented.</summary>
    public RoomExpandedWallRecord WithRotatedSides(int quarterTurns)
    {
        var turns = quarterTurns & 3;
        var bytes = ToArray();
        var sourceSegments = UInt32At(0);
        var rotatedSegments = sourceSegments & ~0x3fu;
        var sourceDirections = UInt32At(4);
        uint rotatedDirections = sourceDirections & ~0xffu;
        var sourceCategories = new ushort[6];
        var sourceAdjacent = new ushort[4];
        var sourceDiagonalAdjacent = new ushort[4];
        for (var index = 0; index < sourceCategories.Length; index++)
            sourceCategories[index] = UInt16At(8 + index * 2);
        for (var index = 0; index < sourceAdjacent.Length; index++)
            sourceAdjacent[index] = UInt16At(20 + index * 2);
        for (var index = 0; index < sourceDiagonalAdjacent.Length; index++)
            sourceDiagonalAdjacent[index] = UInt16At(28 + index * 2);
        Array.Clear(bytes, 28, 8);

        for (var sourceIndex = 0; sourceIndex < 6; sourceIndex++)
        {
            var destinationIndex = sourceIndex < 4
                ? (sourceIndex + turns) & 3
                : 4 + ((sourceIndex - 4) ^ (turns & 1));
            var sourceBit = 1u << sourceIndex;
            var destinationBit = 1u << destinationIndex;
            if ((sourceSegments & sourceBit) != 0) rotatedSegments |= destinationBit;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8 + destinationIndex * 2, 2), sourceCategories[sourceIndex]);
            if (sourceIndex < 4)
            {
                var orientation = (sourceDirections >> (sourceIndex * 2)) & 3;
                rotatedDirections |= orientation << (destinationIndex * 2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20 + destinationIndex * 2, 2), sourceAdjacent[sourceIndex]);
            }
        }
        for (var sourceIndex = 0; sourceIndex < sourceDiagonalAdjacent.Length; sourceIndex++)
        {
            var destinationIndex = DiagonalAdjacentRotation[turns, sourceIndex];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28 + destinationIndex * 2, 2),
                sourceDiagonalAdjacent[sourceIndex]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), rotatedSegments);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), rotatedDirections);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x28, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28, 4), flags | 0x10);
        return new RoomExpandedWallRecord(bytes);
    }

    // FUN_0053bdc6 selects the 0x10/0x20 adjacent words through the four rotation rows in
    // DAT_006f45d4; FUN_00493b0f routes those values to the matching 0x10/0x20/0x40/0x80 slots.
    private static readonly int[,] DiagonalAdjacentRotation =
    {
        { 0, 1, 2, 3 },
        { 3, 2, 0, 1 },
        { 1, 0, 3, 2 },
        { 2, 3, 1, 0 },
    };

    /// <summary>Returns the wall category index Sims.exe FUN_00467ece selects for a single wall bit.
    /// FUN_00435221(mask, 0) is the trailing-zero index.</summary>
    public static int CategoryIndex(uint wallBit)
    {
        if (wallBit == 0 || (wallBit & (wallBit - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(wallBit), "Expected one set wall bit.");
        return System.Numerics.BitOperations.TrailingZeroCount(wallBit);
    }

    /// <summary>Reads the per-wall category word used by FUN_00467ece. The table starts at expanded-record offset 8.</summary>
    public ushort WallCategory(uint wallBit) => UInt16At(8 + CategoryIndex(wallBit) * 2);

    /// <summary>Reads the adjacent-wall category used by FUN_00467e89 for a single wall bit. The table starts at
    /// expanded-record offset 20. Side selectors follow FUN_00435221: selectors 1 and 4 advance one word, 3 advances two.</summary>
    public ushort AdjacentWallCategory(uint wallBit, int sideSelector = 0)
    {
        var index = CategoryIndex(wallBit);
        var adjusted = sideSelector switch { 1 or 4 => index + 1, 3 => index + 2, _ => index };
        return UInt16At(20 + adjusted * 2);
    }

    /// <summary>Category ids that Sims.exe FUN_00467c56 counts as wall/floor-bearing categories.</summary>
    public static bool IsRoomWallCategory(ushort category) => category is 1 or 3 or 5 or 6 or 7 or 8 or 9 or 15 or 22 or 23;
}

/// <summary>Known per-tile inputs exposed for Sims.exe FUN_00467c93: the raw ground Arry 9 byte (read by x/y even on
/// upper levels), the per-level floor-grid word expanded from Arry floor data, and the 44-byte expanded wall record.</summary>
public readonly record struct RoomTileStatisticsInput(byte? RawGroundWaterGridByte, ushort? FloorGridWord, RoomExpandedWallRecord Walls);

/// <summary>Per-tile deltas to the room-stat record fields updated by Sims.exe FUN_00467c93.</summary>
public readonly record struct RoomTileStatisticsContribution(int Counter38, int Counter3C, int Counter54, int Counter60, int Counter64);

/// <summary>The four-byte, two-candidate light-coordinate cell used by Sims.exe FUN_004b523c and
/// FUN_004b53b3. Each candidate is a signed tile offset from the light source; 0x7f marks an empty pair.</summary>
public readonly record struct RoomLightCoordinateCell(sbyte FirstX, sbyte FirstY, sbyte SecondX, sbyte SecondY)
{
    public const sbyte EmptyCoordinate = 0x7f;
    public static RoomLightCoordinateCell Empty => new(EmptyCoordinate, EmptyCoordinate, EmptyCoordinate, EmptyCoordinate);

    public int Count => (FirstX != EmptyCoordinate ? 1 : 0) + (SecondX != EmptyCoordinate ? 1 : 0);

    /// <summary>Ports FUN_004b523c: fill the first available pair, then retain the two candidates nearest
    /// the origin by squared distance. Equal distances preserve the existing pair.</summary>
    public RoomLightCoordinateCell AddCandidate(sbyte x, sbyte y)
    {
        var count = Count;
        if (count == 0) return this with { FirstX = x, FirstY = y };
        if (count == 1) return this with { SecondX = x, SecondY = y };

        var distance = x * x + y * y;
        if (distance < FirstX * FirstX + FirstY * FirstY)
            return this with { FirstX = x, FirstY = y };
        if (SecondX * SecondX + SecondY * SecondY <= distance) return this;
        return this with { SecondX = x, SecondY = y };
    }

    /// <summary>Ports FUN_004b53b3: remove the matching pair; if the first is removed, shift the second forward.</summary>
    public RoomLightCoordinateCell RemoveCandidate(sbyte x, sbyte y)
    {
        if (Count == 1)
            return FirstX == x && FirstY == y ? Empty : this;
        if (Count != 2) return this;
        if (FirstX == x && FirstY == y)
            return this with { FirstX = SecondX, FirstY = SecondY, SecondX = EmptyCoordinate, SecondY = EmptyCoordinate };
        return SecondX == x && SecondY == y
            ? this with { SecondX = EmptyCoordinate, SecondY = EmptyCoordinate }
            : this;
    }
}

/// <summary>Known room-object deltas from Sims.exe FUN_00467ee7. Illumination callbacks can be applied when the
/// caller supplies the source grid, level and runtime radius inputs.</summary>
public readonly record struct RoomObjectStatisticsContribution(int EligibleObjects, int Category4Objects,
    int Category5To7Objects, bool? Category8Flag, int PersonObjects, int RoomImpactSum26,
    int RoomImpactSum28, int NonnegativeImpactCount2A, int LightingObjects140,
    int Type8LightContribution22, int OtherLightContribution24, int ZeroContributionLightCallbacks);

/// <summary>FCNS inputs loaded by Sims.exe FUN_004666a9 before room refresh and consumed by FUN_00467a62.
/// Fallbacks are the corresponding values passed by the executable at its DAT_* addresses.</summary>
public sealed record RoomScoreTuning
{
    /// <summary>Sims.exe DAT_00667638, the 0.8 indoor automatic-light ceiling used by FUN_004688e4/FUN_00467a62.</summary>
    public const float DaylightCeiling = 0.8f;
    public float ObjectLightContributionFactor { get; init; } = 30f;
    public float AreaClipToScore { get; init; } = 60f;
    public float AreaScoreForZeroTiles { get; init; } = -30f;
    public float AreaScoreForClipTile { get; init; } = 30f;
    public float LightScoreForZero { get; init; } = -20f;
    public float LightScoreForOne { get; init; } = 20f;
    public float RoomImpactEffectiveAreaMin { get; init; } = 10f;
    public float RoomImpactEffectiveAreaMax { get; init; } = 45f;
    public float RoomImpactMultiplier { get; init; } = 10f;
    public float OutdoorRoomImpactFactor { get; init; } = 2f;
    public float OutdoorGoodObjectCountFactor { get; init; } = 1f;
    public float OutdoorObjectDivisor { get; init; } = 3f;
    public float OutdoorDaylightBonus { get; init; }
    public float OutdoorDawnDuskBonus { get; init; } = -20f;
    public float OutdoorNighttimeBonus { get; init; } = -50f;
    public float FloorScoreForZero { get; init; } = -40f;
    public float FloorScoreFor100 { get; init; }
    public float WallScoreForZero { get; init; } = -40f;
    public float WallScoreFor100 { get; init; }

    /// <summary>Ports FUN_00467a62's score calculation. Values are the source cXRoom fields at the listed
    /// offsets; daylight is FUN_004674e7's time-scaled outside light and outdoor indicates FUN_00468851.
    /// The caller supplies runtime state explicitly because room refresh is not yet scheduled by the native clock.</summary>
    public RoomScoreResult Calculate(in RoomScoreInput input)
    {
        var tileArea = input.Field38 / 2;
        float light;
        float score;
        if (input.Outdoor)
        {
            light = input.Daylight;
            score = input.Field50 * OutdoorRoomImpactFactor + input.Field54 * OutdoorGoodObjectCountFactor;
        }
        else
        {
            var windowLight = tileArea == 0
                ? float.NaN
                : (input.Field44 * 0.01f / tileArea) * ObjectLightContributionFactor;
            var lampLight = tileArea == 0
                ? float.NaN
                : (input.Field48 * 0.01f / tileArea) * ObjectLightContributionFactor;
            var daylightContribution = MathF.Min(windowLight * input.Daylight, input.Daylight);
            var combinedLight = daylightContribution + lampLight;
            light = combinedLight < 0.01f ? 0f : combinedLight > 0.99f ? 1f : combinedLight;

            var effectiveImpactArea = Math.Clamp(tileArea, RoomImpactEffectiveAreaMin, RoomImpactEffectiveAreaMax);
            var clippedArea = MathF.Min(tileArea, AreaClipToScore);
            var floorRange = FloorScoreFor100 - FloorScoreForZero;
            var wallRange = WallScoreFor100 - WallScoreForZero;
            score = (effectiveImpactArea == 0 ? float.NaN : input.Field50 * RoomImpactMultiplier / effectiveImpactArea)
                + (input.Field3C / (float)input.Field38) * floorRange + FloorScoreForZero
                + (input.Field64 / (float)input.Field60) * wallRange + WallScoreForZero
                + light * (LightScoreForOne - LightScoreForZero) + LightScoreForZero
                + (clippedArea / AreaClipToScore) * (AreaScoreForClipTile - AreaScoreForZeroTiles)
                + AreaScoreForZeroTiles;
            if (score >= -100f)
            {
                if (score > 100f) score = 100f;
            }
            else score = -100f;
        }

        var keepRoomLight = input.Outdoor ||
            (input.LightOverrideFlag75 == 0 &&
             (input.LightsEnabledFlag73 == 0 || input.LightingObjectCount140 != 0 ||
              input.PersonObjectCount142 == 0 || input.DaylightCeiling <= light));
        var tileLight = keepRoomLight ? light : input.DaylightCeiling;

        return new RoomScoreResult(score, light, tileLight);
    }

    /// <summary>Loads the exact tuning keys and ROM fallbacks used by FUN_004666a9.</summary>
    public static RoomScoreTuning FromSimulatorConstants(SimsEngine.Formats.SimulatorConstants constants)
    {
        ArgumentNullException.ThrowIfNull(constants);
        return new RoomScoreTuning
        {
            ObjectLightContributionFactor = constants.Get("object light contribution factor", 30f),
            AreaClipToScore = constants.Get("area clip to score", 60f),
            AreaScoreForZeroTiles = constants.Get("area score for zero tiles", -30f),
            AreaScoreForClipTile = constants.Get("area score for clip tile", 30f),
            LightScoreForZero = constants.Get("light score for 0.0", -20f),
            LightScoreForOne = constants.Get("light score for 1.0", 20f),
            RoomImpactEffectiveAreaMin = constants.Get("room impact effective area min", 10f),
            RoomImpactEffectiveAreaMax = constants.Get("room impact effective area max", 45f),
            RoomImpactMultiplier = constants.Get("room impact multiplier", 10f),
            OutdoorRoomImpactFactor = constants.Get("outdoor room impact factor", 2f),
            OutdoorGoodObjectCountFactor = constants.Get("outdoor good object count factor", 1f),
            OutdoorObjectDivisor = constants.Get("outdoor object divisor", 3f),
            OutdoorDaylightBonus = constants.Get("outdoor daylight bonus", 0f),
            OutdoorDawnDuskBonus = constants.Get("outdoor dawn/dusk bonus", -20f),
            OutdoorNighttimeBonus = constants.Get("outdoor nighttime bonus", -50f),
            FloorScoreForZero = constants.Get("floor score for 0%", -40f),
            FloorScoreFor100 = constants.Get("floor score for 100%", 0f),
            WallScoreForZero = constants.Get("wall score for 0%", -40f),
            WallScoreFor100 = constants.Get("wall score for 100%", 0f),
        };
    }
}

/// <summary>Source cXRoom counters consumed by FUN_00467a62; raw offsets avoid implying a native refresh lifecycle.</summary>
public readonly record struct RoomScoreInput(
    int Field38, int Field3C, int Field44, int Field48, int Field50, int Field54,
    int Field60, int Field64,
    float Daylight, bool Outdoor,
    byte LightOverrideFlag75 = 0, byte LightsEnabledFlag73 = 0,
    int LightingObjectCount140 = 0, int PersonObjectCount142 = 0,
    float DaylightCeiling = RoomScoreTuning.DaylightCeiling);

/// <summary>Initial value of Sims.exe DAT_006f010c, the time-of-day daylight scale read by FUN_004674e7. It is
/// zero-filled in the executable image and has no direct writer in Sims.exe's code.</summary>
public static class RoomRuntimeScalarDefaults
{
    public const float SimsExecutableInitialDaylightScalar = 0f;
    /// <summary>FUN_004674e7 scales phases 1 and 3 by the 0.5 float at Sims.exe VA 0x00666578; phase 2 is dark and
    /// other phases use the full scalar.</summary>
    public static float DaylightForTimePhase(int phase, float daylightScalar) =>
        daylightScalar * (phase switch { 1 or 3 => 0.5f, 2 => 0f, _ => 1f });
}

public readonly record struct RoomScoreResult(float RoomScore, float Light, float TileLight = 0f);

/// <summary>The two-level, 64×64 room light candidate grid used by FUN_004b52ba/FUN_004b5090. Each cell keeps
/// at most two source-relative offsets through <see cref="RoomLightCoordinateCell"/>.</summary>
public sealed class RoomLightCoordinateGrid
{
    public const int GridSize = 64;
    /// <summary>Initial value of Sims.exe DAT_006f011c: the address is in the zero-filled tail of PE .data. No direct
    /// writer exists in the executable image; runtime overrides must be supplied explicitly if an external owner sets it.</summary>
    public const float SimsExecutableInitialLightingScalar = 0f;
    private readonly RoomLightCoordinateCell[] _cells =
        Enumerable.Repeat(RoomLightCoordinateCell.Empty, GridSize * GridSize * 2).ToArray();

    /// <summary>FUN_004b5090 and FUN_004b52ba use __ftol(DAT_006f011c / DAT_006687c0 + DAT_00667270).
    /// In Sims.original.exe the divisor is 3 and the additive constant is 0; the runtime scalar remains caller input.</summary>
    public static int RadiusFromLightingScalar(float lightingScalar) =>
        checked((int)MathF.Truncate(lightingScalar / 3f));

    /// <summary>Reads using Sims.exe's three-byte coordinate order. The light-cell linear index is
    /// <c>second * 64 + first</c>, confirmed by FUN_004b52ba's address calculation.</summary>
    public RoomLightCoordinateCell At(int firstCoordinate, int secondCoordinate, int level) =>
        IsCellInBounds(firstCoordinate, secondCoordinate, level)
            ? _cells[Index(firstCoordinate, secondCoordinate, level)]
            : RoomLightCoordinateCell.Empty;

    /// <summary>Removes a source offset from every in-bounds grid cell in FUN_004b52ba's square radius, in source order.</summary>
    public int RemoveSource(int centerFirstCoordinate, int centerSecondCoordinate, int level, float lightingScalar,
        int mapDimension = GridSize)
    {
        if (!IsInterior(centerFirstCoordinate, centerSecondCoordinate, level, mapDimension)) return 0;
        var radius = RadiusFromLightingScalar(lightingScalar);
        var updated = 0;
        for (var yOffset = -radius; yOffset <= radius; yOffset++)
        for (var xOffset = -radius; xOffset <= radius; xOffset++)
        {
            var first = centerFirstCoordinate + xOffset;
            var second = centerSecondCoordinate + yOffset;
            if (!IsInterior(first, second, level, mapDimension)) continue;
            var index = Index(first, second, level);
            var next = _cells[index].RemoveCandidate(unchecked((sbyte)xOffset), unchecked((sbyte)yOffset));
            if (next == _cells[index]) continue;
            _cells[index] = next;
            updated++;
        }
        return updated;
    }

    /// <summary>Ports FUN_004b5090's same-room test and two-neighbor exception for the -5 room sentinel. The optional
    /// neighbor resolver represents FUN_004672d0; when it returns either adjacent room equal to the source room, that
    /// cell receives the light candidate. Grid traversal is Y outer, X inner as in the executable.</summary>
    public int AddSource(int centerFirstCoordinate, int centerSecondCoordinate, int level, float lightingScalar,
        Func<int, int, int, short> roomAt,
        Func<int, int, int, (short? First, short? Second)>? adjacentRooms = null,
        bool bridgeUnassignedRoomCells = false, int mapDimension = GridSize)
    {
        ArgumentNullException.ThrowIfNull(roomAt);
        if (!IsInterior(centerFirstCoordinate, centerSecondCoordinate, level, mapDimension)) return 0;
        var sourceRoom = roomAt(centerFirstCoordinate, centerSecondCoordinate, level);
        var radius = RadiusFromLightingScalar(lightingScalar);
        var updated = 0;
        for (var yOffset = -radius; yOffset <= radius; yOffset++)
        for (var xOffset = -radius; xOffset <= radius; xOffset++)
        {
            var first = centerFirstCoordinate + xOffset;
            var second = centerSecondCoordinate + yOffset;
            if (!IsInterior(first, second, level, mapDimension)) continue;
            var candidateRoom = roomAt(first, second, level);
            var include = candidateRoom == sourceRoom;
            if (!include && candidateRoom == -5 && bridgeUnassignedRoomCells && adjacentRooms is not null)
            {
                var (adjacentFirstRoom, adjacentSecondRoom) = adjacentRooms(first, second, level);
                include = adjacentFirstRoom == sourceRoom || adjacentSecondRoom == sourceRoom;
            }
            if (!include) continue;
            var index = Index(first, second, level);
            var next = _cells[index].AddCandidate(unchecked((sbyte)xOffset), unchecked((sbyte)yOffset));
            if (next == _cells[index]) continue;
            _cells[index] = next;
            updated++;
        }
        return updated;
    }

    private static bool IsInterior(int x, int y, int level, int mapDimension) =>
        level is 1 or 2 && mapDimension is > 2 and <= GridSize && x > 0 && y > 0 &&
        x < mapDimension - 1 && y < mapDimension - 1;

    private static bool IsCellInBounds(int x, int y, int level) =>
        x is >= 0 and < GridSize && y is >= 0 and < GridSize && level is 1 or 2;

    private static int Index(int firstCoordinate, int secondCoordinate, int level) =>
        (level - 1) * GridSize * GridSize + secondCoordinate * GridSize + firstCoordinate;
}

/// <summary>Supplies the unresolved runtime light scalar and room-grid lookup to the callbacks from FUN_00467ee7.
/// Source coordinate bytes are ordered Y then X; the level callback corresponds to cXObject +0x6a.</summary>
public sealed record RoomLightGridCallbackContext(
    RoomLightCoordinateGrid Grid,
    float LightingScalar,
    Func<SimsEngine.Simulation.XObject, int> LevelAt,
    Func<int, int, int, short> RoomAt,
    Func<int, int, int, (short? First, short? Second)>? AdjacentRooms = null,
    bool BridgeUnassignedRoomCells = false,
    int MapDimension = RoomLightCoordinateGrid.GridSize)
{
    /// <summary>FUN_00467ee7 removes an object's old candidate coverage, then re-adds it only for nonzero
    /// LightingContribution0100. The radius scalar remains explicit because no Sims.exe writer has been identified.</summary>
    public void RefreshObject(SimsEngine.Simulation.XObject item, bool hasNonzeroContribution)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(Grid);
        ArgumentNullException.ThrowIfNull(LevelAt);
        ArgumentNullException.ThrowIfNull(RoomAt);
        var firstCoordinate = item.Position.Y >> 4;
        var secondCoordinate = item.Position.X >> 4;
        var level = LevelAt(item);
        Grid.RemoveSource(firstCoordinate, secondCoordinate, level, LightingScalar, MapDimension);
        if (hasNonzeroContribution)
            Grid.AddSource(firstCoordinate, secondCoordinate, level, LightingScalar, RoomAt, AdjacentRooms,
                BridgeUnassignedRoomCells, MapDimension);
    }
}

/// <summary>Known cXRoom counters written by FUN_00467c93 and FUN_00467ee7 while FUN_00466d14 walks a room.
/// <see cref="ToScoreInput"/> maps the counters consumed by FUN_00467a62. The score result at +0x68 and light at
/// +0x6c remain separate; light-candidate callbacks use <see cref="RoomLightGridCallbackContext"/>.</summary>
public sealed class RoomRuntimeStatistics
{
    public int EligibleObjects20 { get; private set; }
    public int Type8LightContribution22 { get; private set; }
    public int OtherLightContribution24 { get; private set; }
    public int RoomImpactSum26 { get; private set; }
    public int RoomImpactSum28 { get; private set; }
    public int NonnegativeImpactCount2A { get; private set; }
    public int Category4Objects2C { get; private set; }
    public int Category5To7Objects2E { get; private set; }
    public int Counter38 { get; private set; }
    public bool Category8Flag3A { get; private set; }
    public int Counter3C { get; private set; }
    public int LightingObjects140 { get; private set; }
    public int PersonObjects142 { get; private set; }
    public int ZeroContributionLightCallbacks { get; private set; }
    public int Counter54 { get; private set; }
    public int Counter60 { get; private set; }
    public int Counter64 { get; private set; }

    /// <summary>Builds the source-offset score inputs read by FUN_00467a62 from counters accumulated by
    /// FUN_00467c93/FUN_00467ee7. The object-walk's word indices +0x22/+0x24 are byte offsets +0x44/+0x48.
    /// Runtime environment inputs remain explicit because the refresh owner supplies them separately.</summary>
    public RoomScoreInput ToScoreInput(float daylight, bool outdoor, byte lightOverrideFlag75 = 0,
        byte lightsEnabledFlag73 = 0, float daylightCeiling = RoomScoreTuning.DaylightCeiling) => new(
        Field38: Counter38,
        Field3C: Counter3C,
        Field44: Type8LightContribution22,
        Field48: OtherLightContribution24,
        Field50: RoomImpactSum28,
        // Tile statistics write the room's byte +0x54 directly; object statistics use ushort index +0x2a,
        // also byte +0x54. FUN_00467a62 consumes their accumulated sum.
        Field54: Counter54 + NonnegativeImpactCount2A,
        Field60: Counter60,
        Field64: Counter64,
        Daylight: daylight,
        Outdoor: outdoor,
        LightOverrideFlag75: lightOverrideFlag75,
        LightsEnabledFlag73: lightsEnabledFlag73,
        LightingObjectCount140: LightingObjects140,
        PersonObjectCount142: PersonObjects142,
        DaylightCeiling: daylightCeiling);

    /// <summary>Applies one tile's known FUN_00467c93 counter deltas, preserving traversal order.</summary>
    public void Add(in RoomTileStatisticsContribution contribution)
    {
        Counter38 += contribution.Counter38;
        Counter3C += contribution.Counter3C;
        Counter54 += contribution.Counter54;
        Counter60 += contribution.Counter60;
        Counter64 += contribution.Counter64;
    }

    /// <summary>Applies one tile's object walk from FUN_00467ee7. A category-8 flag is an overwrite, not a sum.</summary>
    public void Add(in RoomObjectStatisticsContribution contribution)
    {
        EligibleObjects20 += contribution.EligibleObjects;
        Type8LightContribution22 += contribution.Type8LightContribution22;
        OtherLightContribution24 += contribution.OtherLightContribution24;
        RoomImpactSum26 += contribution.RoomImpactSum26;
        RoomImpactSum28 += contribution.RoomImpactSum28;
        NonnegativeImpactCount2A += contribution.NonnegativeImpactCount2A;
        Category4Objects2C += contribution.Category4Objects;
        Category5To7Objects2E += contribution.Category5To7Objects;
        if (contribution.Category8Flag is { } category8) Category8Flag3A = category8;
        LightingObjects140 += contribution.LightingObjects140;
        PersonObjects142 += contribution.PersonObjects;
        ZeroContributionLightCallbacks += contribution.ZeroContributionLightCallbacks;
    }
}

public static class RoomObjectStatistics
{
    /// <summary>Runs the known `FUN_00467ee7` object contribution for one source tile coordinate, using the
    /// tile root and contained-object preorder exposed by <see cref="SimsEngine.Simulation.SimulationWorld.ObjectsAtTile"/>.</summary>
    public static RoomObjectStatisticsContribution AccumulateTile(SimsEngine.Simulation.SimulationWorld world,
        int tileX, int tileY, int level, ushort statisticsWord0, bool roomBranchFlag,
        Func<SimsEngine.Simulation.XObject, bool> lightingGate, RoomLightGridCallbackContext? lightGrid = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        return AccumulateKnown(world.ObjectsAtTile(tileX, tileY, level), statisticsWord0, roomBranchFlag, lightingGate,
            lightGrid);
    }

    /// <summary>Accumulates source-defined object counters in input order. Category 8 overwrites its flag for each
    /// eligible object, as FUN_00467ee7 does; <paramref name="statisticsWord0"/> supplies the stats record's first word.
    /// The callback supplies the result of the current object's source `FUN_00432644` light gate. A zero light
    /// contribution removes old light-grid coverage; a nonzero contribution removes then adds coverage when the
    /// optional runtime callback context is supplied.</summary>
    public static RoomObjectStatisticsContribution AccumulateKnown(IEnumerable<SimsEngine.Simulation.XObject> objects,
        ushort statisticsWord0, bool roomBranchFlag, Func<SimsEngine.Simulation.XObject, bool> lightingGate,
        RoomLightGridCallbackContext? lightGrid = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(lightingGate);
        var eligible = 0;
        var category4 = 0;
        var category5To7 = 0;
        var persons = 0;
        var impactSum26 = 0;
        var impactSum28 = 0;
        var nonnegativeImpactCount2A = 0;
        var lightingObjects140 = 0;
        var type8LightContribution22 = 0;
        var otherLightContribution24 = 0;
        var zeroContributionLightCallbacks = 0;
        bool? category8Flag = null;
        foreach (var item in objects)
        {
            if (item[SimsEngine.Simulation.ObjectData.Hidden] != 0 || item.RoomWord84 < 0) continue;
            eligible++;
            var category = item[SimsEngine.Simulation.ObjectData.Category];
            if (category == 4) category4++;
            else if (category is >= 5 and < 8) category5To7++;
            else if (category == 8) category8Flag = (statisticsWord0 & 0x3ff) != 0;
            var definitionType = item.Definition[(int)SimsEngine.Formats.ObjdField.Type];
            if (definitionType == SimsEngine.Simulation.XPerson.PersonType) persons++;
            var roomImpact = item[SimsEngine.Simulation.ObjectData.RoomImpact];
            impactSum26 += roomImpact;
            if (!roomBranchFlag || roomImpact < 0) impactSum28 += roomImpact;
            if (roomImpact >= 0) nonnegativeImpactCount2A++;

            if ((item[SimsEngine.Simulation.ObjectData.FlagField2] & 0x400) != 0 && lightingGate(item))
            {
                if (definitionType != 8) lightingObjects140++;
                var contribution = item[SimsEngine.Simulation.ObjectData.LightingContribution0100];
                if (contribution == 0)
                {
                    zeroContributionLightCallbacks++;
                    lightGrid?.RefreshObject(item, hasNonzeroContribution: false);
                }
                else
                {
                    if (definitionType == 8) type8LightContribution22 += contribution;
                    else otherLightContribution24 += contribution;
                    lightGrid?.RefreshObject(item, hasNonzeroContribution: true);
                }
            }
        }
        return new RoomObjectStatisticsContribution(eligible, category4, category5To7, category8Flag, persons,
            impactSum26, impactSum28, nonnegativeImpactCount2A, lightingObjects140, type8LightContribution22,
            otherLightContribution24, zeroContributionLightCallbacks);
    }
}

public static class RoomTileStatistics
{
    /// <summary>Accumulates the source-defined branches of FUN_00467c93. The branch flag is the result of
    /// FUN_00468851 for the room being refreshed. The diagonal selector records are read from Sims.original.exe
    /// at 0x6c4760 and 0x6c4774; this helper is not scheduled in the tick loop.</summary>
    public static RoomTileStatisticsContribution AccumulateKnown(in RoomTileStatisticsInput input, bool roomBranchFlag)
    {
        var groundByte = input.RawGroundWaterGridByte
            ?? throw new InvalidOperationException("FUN_00467c93 requires the ground grid byte.");
        var counter54 = groundByte < 0x10 ? 1 : 0;
        if (roomBranchFlag) return new RoomTileStatisticsContribution(2, 0, counter54, 0, 0);

        var flags = input.Walls.UInt32At(0);
        var diagonals = flags & 0x30;
        if (diagonals != 0)
        {
            // DAT_006c4760: {0x10,2,4,4,2}; DAT_006c4774: {0x20,1,3,1,3}.
            // FUN_00467c93 picks the first record when bit 0x10 is present, otherwise the 0x20 record.
            var diagonal = (diagonals & 0x10) != 0;
            var bit = diagonal ? 0x10u : 0x20u;
            var firstSelector = diagonal ? 2 : 1;
            var secondSelector = diagonal ? 4 : 3;
            var baseIndex = RoomExpandedWallRecord.CategoryIndex(bit);
            var baseCategory = input.Walls.WallCategory(bit);
            // The diagonal path's separate sVar3 read is intentionally at fixed record offset 8 or 20,
            // as the original stack offsets show; it does not use the adjusted selector index.
            var selectedCategory = firstSelector == 1 ? input.Walls.UInt16At(8) : input.Walls.UInt16At(20);
            var adjacentPresent = input.Walls.UInt16At(20 + AdjustedIndex(baseIndex, firstSelector) * 2) != 0 ||
                                  input.Walls.UInt16At(20 + AdjustedIndex(baseIndex, secondSelector) * 2) != 0;
            return new RoomTileStatisticsContribution(
                Counter38: 1,
                Counter3C: selectedCategory != 0 ? 1 : 0,
                Counter54: counter54,
                Counter60: RoomExpandedWallRecord.IsRoomWallCategory(baseCategory) ? 1 : 0,
                Counter64: RoomExpandedWallRecord.IsRoomWallCategory(baseCategory) && adjacentPresent ? 1 : 0);
        }

        var floorWord = input.FloorGridWord
            ?? throw new InvalidOperationException("FUN_00467c93 requires the per-level floor grid word.");
        var counter38 = 2;
        var counter3C = floorWord != 0 ? 2 : 0;
        var counter60 = 0;
        var counter64 = 0;
        for (uint bit = 1; bit < 0x20; bit <<= 1)
        {
            if ((flags & bit) == 0) continue;
            if (!RoomExpandedWallRecord.IsRoomWallCategory(input.Walls.WallCategory(bit))) continue;
            counter60++;
            if (input.Walls.AdjacentWallCategory(bit) != 0) counter64++;
        }
        return new RoomTileStatisticsContribution(counter38, counter3C, counter54, counter60, counter64);
    }

    private static int AdjustedIndex(int index, int selector) => selector switch
    {
        1 or 4 => index + 1,
        3 => index + 2,
        _ => index,
    };
}

public sealed class RoomMap
{
    /// <summary>Room value of tiles that belong to no room.</summary>
    public const ushort NoRoom = 0xFF;

    // FUN_004351e4: wall styles 2, 0xc, 0xd and 0xe (inferred: fences and railings) do not divide rooms.
    private static bool Open(int style) => style is 2 or 0xc or 0xd or 0xe;

    private readonly ushort[] _rooms;
    private readonly RoomWallTile[] _wallTiles;
    private readonly RoomRuntimeWallCell[] _runtimeWallTiles;
    private readonly byte[] _roomKeyWallAdjacency;
    private readonly Dictionary<(int X, int Y), Dictionary<int, ushort>> _wallStyleOverlays = [];
    private readonly byte[] _diagonalDividers;
    private readonly byte[] _diagonalWalls;
    private readonly byte[]? _waterGrid;
    private readonly ushort[]? _floorGrid;
    private readonly HashSet<ushort> _outsideRooms = [];
    private readonly IReadOnlyDictionary<ushort, (int X, int Y)[]> _roomTileOrder;

    public int Width { get; }
    public int Height { get; }
    public int Level { get; }
    public int RoomCount { get; }
    public IReadOnlyList<ushort> RoomIds { get; }
    /// <summary>True when the saved lot supplied Sims.exe's per-room outside flag grid. Without it, outside state is inferred from edge reachability.</summary>
    public bool HasAuthoritativeOutsideRooms { get; }
    public bool HasWaterGrid => _waterGrid is not null;

    /// <summary>Reads the raw Arry 9 byte used by Sims.exe FUN_004790e8 and FUN_00434895. Water checks only
    /// apply on level 1; absence of the layer follows the original helper's no-water result.</summary>
    public bool TryGetWaterGridByte(int x, int y, out byte value)
    {
        if (Level == 1 && _waterGrid is not null && x >= 0 && y >= 0 && x < Width && y < Height)
        {
            value = _waterGrid[y * Width + x];
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Returns the level-1 placement terrain byte for an in-bounds tile. Sims.exe FUN_00432a70 reads the
    /// manager +0x60 grid directly. FUN_00477a73 constructs it through FUN_00461651/00461b8d,
    /// which fills every byte with 0xff before FUN_0047870b imports Arry 9.</summary>
    public byte WaterGridByteAt(int x, int y)
    {
        if (Level != 1 || (uint)x >= Width || (uint)y >= Height) return 0;
        return _waterGrid is null ? (byte)0xff : _waterGrid[y * Width + x];
    }

    /// <summary>Reads the current level's zero-extended Arry floor word. Sims.exe FUN_00433166 uses this grid for
    /// upper-level placement checks; unlike the Arry 9 ground grid, it is available on each floor map.</summary>
    public bool TryGetFloorGridWord(int x, int y, out ushort value)
    {
        if (_floorGrid is not null && x >= 0 && y >= 0 && x < Width && y < Height)
        {
            value = _floorGrid[y * Width + x];
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>Returns the placement floor-grid word for an in-bounds tile. Sims.exe FUN_00433166 directly reads
    /// the per-level manager grid; when the imported Arry floor plane is absent, native represents its cells as zero.</summary>
    public ushort FloorGridWordOrZeroAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return 0;
        return _floorGrid is null ? (ushort)0 : _floorGrid[y * Width + x];
    }

    /// <summary>Whether this diagonal wall divides room fill; search uses <see cref="HasDiagonalWall"/> instead.</summary>
    public bool HasRoomDividingDiagonal(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        return _diagonalDividers[y * Width + x] != 0;
    }

    /// <summary>Whether the wall record has either diagonal bit (0x10 or 0x20), regardless of wall style.
    /// Sims.exe FUN_00434895 rejects all such candidates, while room fill only treats dividing styles as barriers.</summary>
    public bool HasDiagonalWall(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        return _diagonalWalls[y * Width + x] != 0;
    }

    /// <summary>The static saved-wall portion of Sims.exe's FUN_00435a8f directional blocker mask.</summary>
    public uint RouteBlockingMaskAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return 0;
        var segments = _runtimeWallTiles[y * Width + x].Segments;
        uint mask = 0;
        if ((segments & 0x01) != 0) mask |= 0x40;
        if ((segments & 0x02) != 0) mask |= 0x01;
        if ((segments & 0x04) != 0) mask |= 0x04;
        if ((segments & 0x08) != 0) mask |= 0x10;
        return mask;
    }

    /// <summary>Applies FUN_00435a8f's mask rotation and low-bit direction test.</summary>
    public static bool BlocksRouteDirection(uint mask, int direction)
    {
        direction &= 7;
        if (direction != 0)
        {
            mask <<= (8 - direction) & 31;
            mask = (mask & 0xff) | ((mask >> 8) & 0xff);
        }
        return (mask & 1) != 0;
    }

    public bool BlocksRouteDirectionAt(int x, int y, int direction) =>
        BlocksRouteDirection(RouteBlockingMaskAt(x, y), direction);

    private RoomMap(int width, int height, int level, ushort[] rooms, RoomWallTile[] wallTiles,
        RoomRuntimeWallCell[] runtimeWallTiles, byte[] diagonalDividers, byte[] diagonalWalls, byte[]? waterGrid,
        ushort[]? floorGrid, Dictionary<ushort, (int X, int Y)[]> roomTileOrder, int roomCount, byte[]? tileFlags = null)
    {
        Width = width;
        Height = height;
        Level = level;
        _rooms = rooms;
        _wallTiles = wallTiles;
        _runtimeWallTiles = runtimeWallTiles;
        _roomKeyWallAdjacency = new byte[width * height];
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            _roomKeyWallAdjacency[y * width + x] = ComputeRoomKeyWallAdjacency(x, y);
        _diagonalDividers = diagonalDividers;
        _diagonalWalls = diagonalWalls;
        _waterGrid = waterGrid;
        _floorGrid = floorGrid;
        _roomTileOrder = roomTileOrder;
        RoomCount = roomCount;
        RoomIds = rooms.Where(room => room != NoRoom).Distinct().Order().ToArray();
        HasAuthoritativeOutsideRooms = tileFlags is not null;
        // FUN_00468851 reads bit 2 of the level's tile flag grid (Arry 8 / 108, via FUN_0044530c) at the room's first
        // tile. A lot file without that grid keeps the older edge-reachability inference.
        foreach (var (room, tiles) in roomTileOrder)
        {
            if (tiles.Length == 0) continue;
            if (tileFlags is not null) { if ((tileFlags[tiles[0].Y * width + tiles[0].X] & 4) != 0) _outsideRooms.Add(room); }
            else if (tiles.Any(t => t.X == 0 || t.Y == 0 || t.X == width - 1 || t.Y == height - 1)) _outsideRooms.Add(room);
        }
    }

    /// <summary>Inferred outside state used by FUN_00468851. Sims.exe reads bit 2 from a separate per-level room grid;
    /// its writer remains unresolved. This retains the prior edge-reachability approximation explicitly.</summary>
    public bool IsOutside(ushort room) => _outsideRooms.Contains(room);

    /// <summary>FUN_00466d14's separate cXRoom +0x75 input from FUN_004790e8: on level 1, read Arry 9 at the room's
    /// first retained tile and return true when its byte is below 0x10. Other levels or a missing grid return false.</summary>
    public bool LightControlFlag75(ushort room)
    {
        if (Level != 1 || _waterGrid is null || !_roomTileOrder.TryGetValue(room, out var tiles) || tiles.Length == 0)
            return false;
        var (x, y) = tiles[0];
        return _waterGrid[y * Width + x] < 0x10;
    }

    /// <summary>Enumerates wall-derived floor cells for <paramref name="room"/> in Sims.exe `FUN_00479f0c`'s FIFO
    /// flood-fill order. `FUN_00468cf3` copies those packed three-byte coordinates into the cXRoom tile vector.</summary>
    public IEnumerable<(int X, int Y)> TilesInRoom(ushort room)
    {
        if (_roomTileOrder.TryGetValue(room, out var tiles))
            foreach (var tile in tiles) yield return tile;
    }

    /// <summary>Returns the integer area value FUN_004681ec exposes for route candidate capacity: the source room's
    /// +0x38 counter divided by two. FUN_00467c93 adds two for ordinary cells and one for diagonal-wall cells.</summary>
    public int RoomHalfArea(ushort room, bool roomBranchFlag)
    {
        var counter38 = 0;
        foreach (var (x, y) in TilesInRoom(room))
        {
            var input = StatisticsInputAt(x, y)
                ?? throw new InvalidDataException($"Room {room} contains a tile without wall statistics at ({x}, {y}).");
            counter38 += RoomTileStatistics.AccumulateKnown(input, roomBranchFlag).Counter38;
        }
        return counter38 / 2;
    }

    /// <summary>Returns all eight original wall-layer bytes; the runtime wall grid consumed by Sims.exe FUN_00479737 is a distinct, transformed representation.
    /// Returns null outside the loaded lot bounds.</summary>
    public RoomWallTile? WallAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height ? _wallTiles[y * Width + x] : null;

    /// <summary>Returns the 14-byte runtime cell created by Sims.exe FUN_0047870b from the saved wall record.</summary>
    public RoomRuntimeWallCell? RuntimeWallAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height ? _runtimeWallTiles[y * Width + x] : null;

    /// <summary>Computes Sims.exe FUN_00479dca's room-key edge byte from the surrounding 2x2 runtime wall cells.
    /// The manager's byte-grid lifecycle is not yet modeled, but this reader's source bit mapping is confirmed.</summary>
    public byte RoomKeyWallAdjacencyAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return 0;
        return ComputeRoomKeyWallAdjacency(x, y);
    }

    /// <summary>Reads the per-level byte grid initialized by FUN_00479f0c and updated by FUN_00479954.</summary>
    public byte RoomKeyWallAdjacencyGridByteAt(int x, int y) =>
        (uint)x < Width && (uint)y < Height ? _roomKeyWallAdjacency[y * Width + x] : (byte)0;

    private byte ComputeRoomKeyWallAdjacency(int x, int y)
    {
        byte Segments(int cellX, int cellY)
        {
            if (cellX < 1 || cellY < 1 || cellX >= Width - 1 || cellY >= Height - 1) return 0;
            return _runtimeWallTiles[cellY * Width + cellX].Segments;
        }

        var northwest = Segments(x - 1, y - 1);
        var northeast = Segments(x, y - 1);
        var southwest = Segments(x - 1, y);
        var southeast = Segments(x, y);
        byte result = 0;
        if ((northwest & 0x08) != 0) result |= 0x02;
        if ((northwest & 0x20) != 0) result |= 0x10;
        if ((northeast & 0x01) != 0) result |= 0x01;
        if ((northeast & 0x10) != 0) result |= 0x80;
        if ((southwest & 0x04) != 0) result |= 0x04;
        if ((southwest & 0x10) != 0) result |= 0x20;
        if ((southeast & 0x02) != 0) result |= 0x08;
        if ((southeast & 0x20) != 0) result |= 0x40;
        return result;
    }

    private void RefreshRoomKeyWallAdjacencyWindow(int x, int y)
    {
        foreach (var (cellX, cellY) in new[] { (x, y), (x + 1, y), (x, y + 1), (x + 1, y + 1) })
            if ((uint)cellX < Width && (uint)cellY < Height)
                _roomKeyWallAdjacency[cellY * Width + cellX] = ComputeRoomKeyWallAdjacency(cellX, cellY);
    }

    /// <summary>Expands a runtime wall cell and its cardinal neighbors as Sims.exe FUN_00479737 and FUN_0053bb44 do.
    /// Out-of-lot neighbors contribute false. The lower/right wall-detail overlays are also applied.</summary>
    public RoomExpandedWallRecord? ExpandedWallAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        var cell = _runtimeWallTiles[y * Width + x];
        bool HasDiagonal(int nx, int ny) => nx >= 0 && ny >= 0 && nx < Width && ny < Height &&
                                            (_runtimeWallTiles[ny * Width + nx].Segments & 0x30) != 0;
        var bytes = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), cell.Segments);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), cell.Byte1);
        Write16(8, cell.Word2);
        Write16(10, cell.Word4);
        // FUN_0053bb44 leaves offsets 12 and 14 untouched. FUN_00479737 fills those category slots
        // only when the below/right neighbor overlays exist; the original caller's stack bytes otherwise
        // remain undefined, so this value-initialized record uses zero until an overlay writes them.
        Write16(16, cell.Word4);
        Write16(18, cell.Word4);
        Write16(20, cell.Word6);
        Write16(22, cell.Word8);
        Write16(24, cell.Word12);
        Write16(26, cell.Word10);
        Write16(28, (cell.Segments & 0x10) != 0 ? cell.Word10 : (ushort)0);
        Write16(30, (cell.Segments & 0x10) != 0 ? cell.Word12 : (ushort)0);
        Write16(32, (cell.Segments & 0x20) != 0 ? cell.Word10 : (ushort)0);
        Write16(34, (cell.Segments & 0x20) != 0 ? cell.Word12 : (ushort)0);
        var neighbors = (HasDiagonal(x - 1, y) ? 4 : 0) |
                        (HasDiagonal(x, y - 1) ? 8 : 0) |
                        (HasDiagonal(x, y + 1) ? 1 : 0) |
                        (HasDiagonal(x + 1, y) ? 2 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), (uint)(0x10 | neighbors));

        // FUN_00479737 (00479737.c:74,92) calls FUN_004633e3(wallStyle, selectedSegments) to overlay the (x, y+1)
        // cell's Word4 (+0x04) into segment 8 (offset 14) and the (x+1, y) cell's Word2 (+0x02) into segment 4
        // (offset 12) when their corresponding segment side exists.
        if (y + 1 < Height && (_runtimeWallTiles[(y + 1) * Width + x].Segments & 2) != 0)
        {
            Write16(14, _runtimeWallTiles[(y + 1) * Width + x].Word4);
            OverlayDirection((byte)((_runtimeWallTiles[(y + 1) * Width + x].Byte1 & 0x0c) >> 2), 8);
        }
        if (x + 1 < Width && (_runtimeWallTiles[y * Width + x + 1].Segments & 1) != 0)
        {
            Write16(12, _runtimeWallTiles[y * Width + x + 1].Word2);
            OverlayDirection((byte)(_runtimeWallTiles[y * Width + x + 1].Byte1 & 3), 4);
        }
        if (_wallStyleOverlays.TryGetValue((x, y), out var overlays))
        {
            foreach (var (index, style) in overlays)
                Write16(8 + index * 2, style);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), flags | 0x10);
        }
        return new RoomExpandedWallRecord(bytes);

        void Write16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
        void OverlayDirection(byte value, uint mask)
        {
            var index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            var shift = index * 2;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            flags = (flags & ~(3u << shift)) | ((uint)value << shift);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), flags);
        }
    }

    /// <summary>Applies Sims.exe FUN_00479954's cardinal runtime-cell mutations after FUN_004633e3 has overlaid the
    /// transient expanded record. Segment presence comes from that record; selected categories remain an overlay.</summary>
    public bool ApplyWallStyleOverlay(int x, int y, ushort wallStyle, uint selectedSegments,
        RoomExpandedWallRecord refreshedWall)
    {
        if (ExpandedWallAt(x, y) is null) return false;
        var currentIndex = y * Width + x;
        var current = _runtimeWallTiles[currentIndex];
        var sourceSegments = refreshedWall.UInt32At(0);
        var currentSegments = current.Segments;
        var currentByte1 = current.Byte1;
        var word2 = current.Word2;
        var word4 = current.Word4;
        var word6 = current.Word6;
        var word8 = current.Word8;
        var word10 = current.Word10;
        var word12 = current.Word12;

        // FUN_00479954 synchronizes the two diagonal segment bits before its cardinal writes. The source assigns
        // the segment byte directly when either diagonal is present (so bit 0x20 wins if both are set), while an
        // absent diagonal clears only its own bit. Cardinal branches below then OR their sides into that byte.
        if ((sourceSegments & 0x10) != 0) currentSegments = 0x10;
        else currentSegments &= 0xef;
        // FUN_00479954's 0x10 branch copies the selected category and its adjacent categories into the
        // runtime cell's Word2/4/6/10/12 fields. These cached words intentionally remain when the segment clears.
        if ((sourceSegments & 0x10) != 0)
        {
            word2 = refreshedWall.WallCategory(0x01);
            word4 = refreshedWall.WallCategory(0x10);
            word6 = refreshedWall.AdjacentWallCategory(0x01);
            word10 = refreshedWall.AdjacentWallCategory(0x10, 2);
            word12 = refreshedWall.AdjacentWallCategory(0x10, 4);
        }
        if ((sourceSegments & 0x20) != 0) currentSegments = 0x20;
        else currentSegments &= 0xdf;
        // The following source branch writes Word2/4/8/10/12; it runs after 0x10 and therefore
        // supplies the final overlapping Word4/10/12 values when both diagonal bits are present.
        if ((sourceSegments & 0x20) != 0)
        {
            word2 = refreshedWall.WallCategory(0x01);
            word4 = refreshedWall.WallCategory(0x20);
            word8 = refreshedWall.AdjacentWallCategory(0x01);
            word10 = refreshedWall.AdjacentWallCategory(0x20, 1);
            word12 = refreshedWall.AdjacentWallCategory(0x20, 3);
        }

        if ((sourceSegments & 0x01) != 0)
        {
            currentSegments |= 0x01;
            currentByte1 |= refreshedWall.WallSideOrientation(0x01);
            word2 = refreshedWall.WallCategory(0x01);
            word6 = refreshedWall.AdjacentWallCategory(0x01);
            if (x > 0) AddSegment(x - 1, y, 0x04);
        }
        else if ((currentSegments & 0x01) != 0)
        {
            currentSegments &= 0xfe;
            if (x > 0) RemoveSegment(x - 1, y, 0x04, clearWord12: true);
        }

        if ((sourceSegments & 0x02) != 0)
        {
            currentSegments |= 0x02;
            currentByte1 |= (byte)(refreshedWall.WallSideOrientation(0x02) << 2);
            word4 = refreshedWall.WallCategory(0x02);
            word8 = refreshedWall.AdjacentWallCategory(0x02);
            if (y > 0) AddSegment(x, y - 1, 0x08);
        }
        else if ((currentSegments & 0x02) != 0)
        {
            currentSegments &= 0xfd;
            if (y > 0) RemoveSegment(x, y - 1, 0x08, clearWord10: true);
        }

        if ((sourceSegments & 0x04) != 0)
        {
            currentSegments |= 0x04;
            word12 = refreshedWall.AdjacentWallCategory(0x04);
            if (x + 1 < Width)
                AddSegment(x + 1, y, 0x01, word2: refreshedWall.WallCategory(0x04),
                    orientation: refreshedWall.WallSideOrientation(0x04));
        }
        else if ((currentSegments & 0x04) != 0)
        {
            currentSegments &= 0xfb;
            if (x + 1 < Width) RemoveSegment(x + 1, y, 0x01, clearWord6: true);
        }

        if ((sourceSegments & 0x08) != 0)
        {
            currentSegments |= 0x08;
            word10 = refreshedWall.AdjacentWallCategory(0x08);
            if (y + 1 < Height)
                AddSegment(x, y + 1, 0x02, word4: refreshedWall.WallCategory(0x08),
                    orientation: refreshedWall.WallSideOrientation(0x08), orientationShift: 2);
        }
        else if ((currentSegments & 0x08) != 0)
        {
            currentSegments &= 0xf7;
            if (y + 1 < Height) RemoveSegment(x, y + 1, 0x02, clearWord8: true);
        }

        _runtimeWallTiles[currentIndex] = current with
        {
            Segments = currentSegments,
            Byte1 = currentByte1,
            Word2 = word2,
            Word4 = word4,
            Word6 = word6,
            Word8 = word8,
            Word10 = word10,
            Word12 = word12,
        };

        if (!_wallStyleOverlays.TryGetValue((x, y), out var overlays))
            _wallStyleOverlays[(x, y)] = overlays = [];
        for (var index = 0; index < 6; index++)
            if ((selectedSegments & (1u << index)) != 0) overlays[index] = wallStyle;
        RefreshRoomKeyWallAdjacencyWindow(x, y);
        return true;

        void AddSegment(int tileX, int tileY, byte segment, ushort? word2 = null, ushort? word4 = null,
            byte? orientation = null, int orientationShift = 0)
        {
            var index = tileY * Width + tileX;
            var cell = _runtimeWallTiles[index];
            _runtimeWallTiles[index] = cell with
            {
                Segments = (byte)(cell.Segments | segment),
                Word2 = word2 ?? cell.Word2,
                Word4 = word4 ?? cell.Word4,
                Byte1 = orientation is { } side
                    ? (byte)(cell.Byte1 | ((side & 3) << orientationShift))
                    : cell.Byte1,
            };
        }

        void RemoveSegment(int tileX, int tileY, byte segment, bool clearWord6 = false, bool clearWord8 = false,
            bool clearWord10 = false, bool clearWord12 = false)
        {
            var index = tileY * Width + tileX;
            var cell = _runtimeWallTiles[index];
            _runtimeWallTiles[index] = cell with
            {
                Segments = (byte)(cell.Segments & ~segment),
                Word6 = clearWord6 ? (ushort)0 : cell.Word6,
                Word8 = clearWord8 ? (ushort)0 : cell.Word8,
                Word10 = clearWord10 ? (ushort)0 : cell.Word10,
                Word12 = clearWord12 ? (ushort)0 : cell.Word12,
            };
        }
    }

    /// <summary>Exposes known tile inputs for FUN_00467c93. That routine reads Arry 9 by x/y without a level check,
    /// even though FUN_004790e8 only applies the water test at level 1. The short grid at DAT_006e54dc + 0x20 is
    /// expanded from the current level's Arry floor layer by FUN_0047870b.</summary>
    public RoomTileStatisticsInput? StatisticsInputAt(int x, int y)
    {
        var walls = ExpandedWallAt(x, y);
        if (walls is null) return null;
        // FUN_00461b8d supplies the 0xff no-pool sentinel when Arry 9 is absent.
        byte? groundByte = _waterGrid is not null ? _waterGrid[y * Width + x] : (byte)0xff;
        // The room manager's per-level floor grid is present even without an Arry floor layer and remains zero-filled.
        ushort? floorWord = _floorGrid is not null ? _floorGrid[y * Width + x] : (ushort)0;
        return new RoomTileStatisticsInput(groundByte, floorWord, walls.Value);
    }

    public ushort RoomAt(int x, int y) => _rooms[y * Width + x];

    /// <summary>The room-dividing diagonal bit (0x10 or 0x20) on a tile, or 0.</summary>
    public byte DiagonalKind(int x, int y) => InBounds(x, y) ? _diagonalDividers[y * Width + x] : (byte)0;

    /// <summary>Resolves the two room keys beside an unassigned diagonal-divider tile, matching the adjacent-room
    /// lookup used by Sims.exe FUN_004672d0 when FUN_004b5090 bridges light candidates across that tile.</summary>
    public (ushort First, ushort Second)? AdjacentRoomsAtTile(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return null;
        var diagonal = _diagonalDividers[y * Width + x];
        if ((diagonal & 0x10) != 0)
            return (RoomAtPosition(x * 16 + 1, y * 16 + 1), RoomAtPosition(x * 16 + 14, y * 16 + 14));
        if ((diagonal & 0x20) != 0)
            return (RoomAtPosition(x * 16 + 14, y * 16 + 1), RoomAtPosition(x * 16 + 1, y * 16 + 14));
        return null;
    }

    /// <summary>Resolves a fixed-point world position to a room. Sims.exe FUN_00433c6a (00433c6a.c:24-85) checks the
    /// fractional coordinates when the tile has a dividing diagonal (puVar4[1] is worldX / row cVar7, *puVar4 is worldY /
    /// col cVar12), then probes the X-orthogonal cell RoomAt(adjacentX, y) first and falls back to RoomAt(x, adjacentY)
    /// when that cell is -5 (NoRoom).</summary>
    public ushort RoomAtPosition(int worldX, int worldY)
    {
        var x = worldX >> 4;
        var y = worldY >> 4;
        if (x < 0 || y < 0 || x >= Width || y >= Height) return NoRoom;
        var room = RoomAt(x, y);
        if (room != NoRoom) return room;
        var index = y * Width + x;
        var diagonal = _diagonalDividers[index];
        if (diagonal == 0) return NoRoom;

        var fractionX = worldX & 0x0f;
        var fractionY = worldY & 0x0f;
        int adjacentX, adjacentY;
        if ((diagonal & 0x10) != 0)
        {
            if (fractionY < 16 - fractionX) { adjacentX = x - 1; adjacentY = y - 1; }
            else { adjacentX = x + 1; adjacentY = y + 1; }
        }
        else
        {
            if (fractionX < fractionY) { adjacentX = x - 1; adjacentY = y + 1; }
            else { adjacentX = x + 1; adjacentY = y - 1; }
        }

        if (InBounds(adjacentX, y))
        {
            room = RoomAt(adjacentX, y);
            if (room != NoRoom) return room;
        }
        if (InBounds(x, adjacentY))
        {
            room = RoomAt(x, adjacentY);
            if (room != NoRoom) return room;
        }
        return NoRoom;
    }

    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>Whether the file stores a walls layer for the level (FUN_0047870b loads two levels).</summary>
    public static bool HasLevel(IffFile file, int level) => file.Find(ResourceType.Parse("Arry"), WallLayerId(level)) is not null;

    private static ushort WallLayerId(int level) => (ushort)((level - 1) * 100 + 2);
    // FUN_00477a73 constructs manager +0x60 through FUN_00461651 (0xff-filled pool grid).
    // FUN_0047870b imports Arry 9 into that grid; Arry 8 is the separate buildability/tile-flag plane.
    private static ushort WaterLayerId => 9;
    private static ushort FloorLayerId(int level) => (ushort)((level - 1) * 100 + 1);

    /// <summary>Fills one level of a width by height lot from its walls layer.</summary>
    public static RoomMap Build(IffFile file, int level, int width, int height, bool importRuntimeWallGrid = true)
    {
        var walls = ReadLayer(file, WallLayerId(level));
        var waterChunk = file.Find(ResourceType.Parse("Arry"), WaterLayerId);
        var groundGrid = waterChunk is null ? null : LotArray.Read(file, waterChunk);
        if (groundGrid is not null && (width > groundGrid.Rows || height > groundGrid.Width || groundGrid.ElementSize != 1))
            throw new ArgumentOutOfRangeException(nameof(width), "lot is larger than the stored ground water layer or its element size is invalid");
        var floorChunk = file.Find(ResourceType.Parse("Arry"), (ushort)(FloorLayerId(level) + 10))
            ?? file.Find(ResourceType.Parse("Arry"), FloorLayerId(level));
        var savedFloorGrid = floorChunk is null ? null : LotArray.Read(file, floorChunk);
        if (savedFloorGrid is not null && (width > savedFloorGrid.Rows || height > savedFloorGrid.Width || savedFloorGrid.ElementSize is not (1 or 2)))
            throw new ArgumentOutOfRangeException(nameof(width), "lot is larger than the stored floor layer or its element size is invalid");
        var classification = level == 1 ? groundGrid : savedFloorGrid;
        if (width > walls.Rows || height > walls.Width) throw new ArgumentOutOfRangeException(nameof(width), "lot is larger than the stored layer");
        byte[]? waterGrid = null;
        if (groundGrid is not null)
        {
            waterGrid = new byte[width * height];
            for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
                waterGrid[y * width + x] = groundGrid.Element(x, y)[0];
        }
        byte[]? tileFlags = null;
        if (file.Find(ResourceType.Parse("Arry"), (ushort)(level == 1 ? 8 : 108)) is { } flagChunk
            && LotArray.Read(file, flagChunk) is { ElementSize: 1 } flagGrid && width <= flagGrid.Rows && height <= flagGrid.Width)
        {
            tileFlags = new byte[width * height];
            for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
                tileFlags[y * width + x] = flagGrid.Element(x, y)[0];
        }
        ushort[]? floorGrid = null;
        if (savedFloorGrid is not null)
        {
            floorGrid = new ushort[width * height];
            for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
                floorGrid[y * width + x] = savedFloorGrid.ElementSize == 2
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(savedFloorGrid.Element(x, y))
                    : savedFloorGrid.Element(x, y)[0];
        }
        // FUN_00479f0c captures seed class from Arry 9 (< 0x10) on level 1, and the floor grid's nonzero state above it.
        bool Classification(int x, int y) => classification is not null && (level == 1
            ? classification.Element(x, y)[0] < 0x10
            : floorGrid![y * width + x] != 0);
        // Sims.exe indexes the layer [x][y] (FUN_0047c7b7 picks row x). A record holds the wall segments (byte 0) and,
        // from byte 2, one style byte a side: byte 2 for side 1 (towards x - 1) and byte 3 for side 2 (towards y - 1).
        ReadOnlySpan<byte> Record(int x, int y) => walls.Element(x, y);
        // FUN_00479737 takes the style of side 4 (towards x + 1) from the x + 1 tile's side 1, and of side 8 (towards
        // y + 1) from the y + 1 tile's side 2, when that tile has the wall. Inferred: without it the side blocks.
        bool Blocks(int x, int y, int side)
        {
            var record = Record(x, y);
            if ((record[0] & side) != side) return false;
            int? style = side switch
            {
                1 => record[2],
                2 => record[3],
                4 => x + 1 < width && (Record(x + 1, y)[0] & 1) != 0 ? Record(x + 1, y)[2] : null,
                _ => y + 1 < height && (Record(x, y + 1)[0] & 2) != 0 ? Record(x, y + 1)[3] : null,
            };
            return style is not { } s || !Open(s);
        }
        // FUN_00479f0c does not step onto a diagonal tile whose wall divides rooms. Inferred for now: such a tile
        // belongs to no room (Sims.exe gives each half its own room, not read yet).
        bool Diagonal(int x, int y) => (Record(x, y)[0] & 0x30) != 0 && !Open(Record(x, y)[3]);

        var rooms = new ushort[width * height];
        var roomKeyBase = unchecked((ushort)((level + 0x3f) * 0x400));
        var wallTiles = new RoomWallTile[width * height];
        var runtimeWallTiles = new RoomRuntimeWallCell[width * height];
        var diagonalDividers = new byte[width * height];
        var diagonalWalls = new byte[width * height];
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            var record = Record(x, y);
            var index = y * width + x;
            wallTiles[index] = new RoomWallTile(record[0], record[1], record[2], record[3], record[4], record[5], record[6], record[7]);
            // FUN_0047870b clears the runtime wall-grid buffers for upper level bands on SIMI versions <= 0x16.
            // Keep the source Arry record for room partition/classification work, but don't expose it as a loaded
            // runtime wall cell in that legacy case.
            runtimeWallTiles[index] = importRuntimeWallGrid
                ? RoomRuntimeWallCell.FromSavedRecord(wallTiles[index])
                : default;
            diagonalWalls[index] = (byte)(record[0] & 0x30);
            if ((record[0] & 0x30) != 0 && !Open(record[3])) diagonalDividers[index] = (byte)(record[0] & 0x30);
        }
        Array.Fill(rooms, NoRoom);
        var assigned = new bool[width * height];
        var roomTileOrder = new Dictionary<ushort, (int X, int Y)[]>();
        var queue = new Queue<(int x, int y)>();
        ushort next = 0;
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (assigned[y * width + x] || Diagonal(x, y)) continue;
            var room = (ushort)(roomKeyBase | next++);
            var classificationClass = Classification(x, y);
            var visited = new List<(int X, int Y)> { (x, y) };
            assigned[y * width + x] = true;
            rooms[y * width + x] = room;
            queue.Enqueue((x, y));
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                foreach (var (dx, dy, side) in FloodFillSides)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    if (assigned[ny * width + nx] || Diagonal(nx, ny) || Classification(nx, ny) != classificationClass || Blocks(cx, cy, side)) continue;
                    assigned[ny * width + nx] = true;
                    rooms[ny * width + nx] = room;
                    visited.Add((nx, ny));
                    queue.Enqueue((nx, ny));
                }
            }
            roomTileOrder.Add(room, visited.ToArray());
        }
        return new RoomMap(width, height, level, rooms, wallTiles, runtimeWallTiles, diagonalDividers, diagonalWalls,
            waterGrid, floorGrid, roomTileOrder, next, tileFlags);
    }

    // FUN_00479f0c probes selectors 8, 2, 4, 1; FUN_004351e4 maps these to +y, -y, +x, -x.
    private static readonly (int dx, int dy, int side)[] FloodFillSides = [(0, 1, 8), (0, -1, 2), (1, 0, 4), (-1, 0, 1)];

    private static LotArray ReadLayer(IffFile file, ushort id)
    {
        var chunk = file.Find(ResourceType.Parse("Arry"), id)
            ?? throw new ResourceFormatException(file.Source, null, 0, $"no lot layer {id}");
        return LotArray.Read(file, chunk);
    }
}
