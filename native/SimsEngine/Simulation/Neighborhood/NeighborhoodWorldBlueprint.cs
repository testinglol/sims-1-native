using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SimsEngine.Formats;

namespace SimsEngine.Simulation.Neighborhood;

/// <summary>
/// Authoritative world-map blueprint for the unified Sims 1 + Old Town neighborhood (Houses 1–10 and 50–80),
/// aligning every parcel's exact <c>HouseXX.iff</c> road edge(s) and <c>Globals[23]</c> size onto
/// <see cref="MasterRoadNetwork.Default"/> while preserving <c>LotLocations.iff#STR:4</c> relative town geometry.
/// </summary>
public sealed class NeighborhoodWorldBlueprint
{
    public const float MapPixelsPerTile = 0.90f;
    public const float DefaultRiverHalfWidthTiles = 9.5f;

    public readonly record struct ParcelCoord(
        short House,
        int MapX,
        int MapY,
        int WorldTileX,
        int WorldTileY,
        int LotSizeTiles = 64,
        int PrimaryRoadEdge = 1,
        int? SecondaryRoadEdge = null)
    {
        /// <summary>
        /// Exact local centerline coordinate (along X for edges 1/3, along Y for edges 0/2) of the road
        /// band inside this lot's <c>HouseXX.iff</c> <c>Pattern 9/10/11</c> grid.
        /// </summary>
        public float GetRoadCenterLocal(int edge) => edge switch
        {
            0 => 5.5f,
            3 => House == 3 ? 3.5f : 5.5f,
            1 => House == 7 ? 46.5f : LotSizeTiles - 5.5f,
            2 => LotSizeTiles - 5.5f,
            _ => LotSizeTiles - 5.5f,
        };

        /// <summary>
        /// Returns the parcel's playable interior bounds in local tiles (excluding the shared road/sidewalk strip
        /// on any road-facing edge so opposite-side lots never overlap playable yards).
        /// </summary>
        public (float MinX, float MinY, float MaxX, float MaxY) GetPlayableLocalBounds()
        {
            float minX = 0f, minY = 0f, maxX = LotSizeTiles, maxY = LotSizeTiles;
            var self = this;
            void ClipEdge(int edge)
            {
                float c = self.GetRoadCenterLocal(edge);
                switch (edge)
                {
                    case 0: minY = MathF.Max(minY, c + 4.5f); break;
                    case 2: maxY = MathF.Min(maxY, c - 4.5f); break;
                    case 3: minX = MathF.Max(minX, c + 4.5f); break;
                    case 1: maxX = MathF.Min(maxX, c - 4.5f); break;
                }
            }
            ClipEdge(PrimaryRoadEdge);
            if (SecondaryRoadEdge is int sec)
                ClipEdge(sec);
            return (minX, minY, maxX, maxY);
        }
    }

    public readonly record struct RiverSegment(float X0, float Y0, float X1, float Y1, float HalfWidthTiles);

    /// <summary>
    /// All 41 Unleashed town-map lots (<c>1..10</c>, <c>50..80</c>) with their exact <c>LotLocations.iff#STR:4</c>
    /// map coordinates, road-snapped <c>(WorldTileX, WorldTileY)</c> aligned to <see cref="MasterRoadNetwork.Default"/>,
    /// actual <c>HouseXX.iff</c> <c>Globals[23]</c> <c>LotSizeTiles</c>, and verified primary/secondary road edges.
    /// </summary>
    private static readonly ParcelCoord[] CanonicalUnleashedParcels =
    [
        new( 1, 480,  84,  380, -135, 56, 1, null),
        new( 2, 722,  75,  470, -221, 56, 2, null),
        new( 3, 578, 142,  427, -135, 54, 3, null),
        new( 4, 506, 177,  425,  -65, 50, 3, null),
        new( 5, 658, 214,  599, -110, 52, 1, null),
        new( 6, 456, 262,  503,   10, 43, 1, 0),
        new( 7, 589, 249,  599,  -27, 48, 2, 1),
        new( 8, 735, 265,  640, -105, 50, 3, null),
        new( 9, 538, 303,  535,   10, 43, 3, 0),
        new(10, 667, 299,  640,  -40, 50, 3, null),

        new(50,  87, 104,  159,   79, 47, 1, 2),
        new(51, 226, 110,  195,   10, 41, 3, null),
        new(52,  84, 182,  195,  115, 47, 3, 0),
        new(53, 147, 213,  289,  115, 47, 1, 0),
        new(54, 163, 143,  195,   79, 47, 3, 2),
        new(55, 227, 172,  289,   79, 47, 1, 2),
        new(56, 303, 213,  325,   79, 47, 3, 2),
        new(57,  74, 253,  295,  220, 41, 1, null),
        new(58, 225, 262,  325,  115, 54, 3, 0),
        new(59, 133, 296,  325,  225, 41, 3, null),
        new(60, 181, 331,  499,  264, 47, 2, 1),
        new(61,  52, 347,  394,  300, 47, 1, 0),
        new(62, 208, 396,  485,  300, 41, 0, null),
        new(63,  76, 412,  430,  404, 47, 2, 3),
        new(64,  14, 458,  445,  440, 41, 0, null),
        new(65,  77, 490,  510,  440, 41, 0, null),
        new(66, 143, 444,  500,  405, 46, 2, null),
        new(67, 273, 428,  560,  300, 41, 0, null),
        new(68, 298, 350,  535,  210, 41, 3, null),
        new(69, 359, 319,  535,  145, 41, 3, null),
        new(70, 421, 368,  590,  115, 60, 0, null),
        new(71, 699, 413,  825,   81, 45, 2, 3),
        new(72, 765, 444,  891,   81, 45, 1, 2),
        new(73, 595, 453,  735,  115, 60, 0, null),
        new(74, 534, 504,  795,  240, 41, 1, null),
        new(75, 467, 534,  791,  326, 45, 1, 2),
        new(76, 400, 577,  780,  360, 41, 0, null),
        new(77, 672, 508,  825,  194, 47, 3, 2),
        new(78, 592, 551,  825,  230, 47, 3, 0),
        new(79, 660, 580,  885,  230, 41, 0, null),
        new(80, 730, 542,  885,  200, 41, 2, null),
    ];

    /// <summary>
    /// Official Sim Lane / Old Town river channel in global blueprint world tile coordinates,
    /// matching <c>nscreen_unleashed_coastal_4k.jpg</c> and passing cleanly under the Stone Arch Bridge
    /// (<c>X≈692, Y=120.5</c>) and Southern Coastal Truss Bridge (<c>X≈645, Y≈405</c>) with &gt;= 29 tiles
    /// clearance from every parcel footprint.
    /// </summary>
    public static readonly (float X, float Y)[] CanonicalRiverWaypoints =
    [
        (660f, -230f),
        (718f, -140f),
        (725f,  -40f),
        (725f,   40f),
        (692f,  120.5f),
        (672f,  215f),
        (658f,  310f),
        (645f,  405f),
        (625f,  515f),
    ];

    public static NeighborhoodWorldBlueprint Default { get; } = CreateDefault();

    private readonly Dictionary<short, ParcelCoord> _parcels;

    public IReadOnlyDictionary<short, ParcelCoord> Parcels => _parcels;

    private NeighborhoodWorldBlueprint(Dictionary<short, ParcelCoord> parcels)
    {
        _parcels = parcels;
    }

    public static (int WorldX, int WorldY) ProjectMapPointToWorldTiles(int mapX, int mapY)
    {
        int wx = (int)MathF.Round((mapY * 2f + mapX) / (2f * MapPixelsPerTile));
        int wy = (int)MathF.Round((mapY * 2f - mapX) / (2f * MapPixelsPerTile));
        return (wx, wy);
    }

    public static (float WorldX, float WorldY) ProjectMapPointToWorldTilesFloat(float mapX, float mapY)
    {
        float wx = (mapY * 2f + mapX) / (2f * MapPixelsPerTile);
        float wy = (mapY * 2f - mapX) / (2f * MapPixelsPerTile);
        return (wx, wy);
    }

    public static bool IsUnifiedMapLot(short house) =>
        house is (>= 1 and <= 10) or (>= 50 and <= 80);

    private static NeighborhoodWorldBlueprint CreateDefault()
    {
        var dict = new Dictionary<short, ParcelCoord>(CanonicalUnleashedParcels.Length);
        foreach (var p in CanonicalUnleashedParcels)
            dict[p.House] = p;
        return new NeighborhoodWorldBlueprint(dict);
    }

    public static NeighborhoodWorldBlueprint LoadFromUserData(string? userDataPath)
    {
        if (string.IsNullOrWhiteSpace(userDataPath))
            return Default;

        var locPath = Path.Combine(userDataPath, "LotLocations.iff");
        if (!File.Exists(locPath))
            return Default;

        try
        {
            var iff = IffFile.Open(locPath);
            // Prefer STR# 4 (Unleashed full Sim Lane + Old Town map), fall back to STR# 1
            var chunk = iff.Find(ResourceType.Str, 4) ?? iff.Find(ResourceType.Str, 1);
            if (chunk is null)
                return Default;

            var table = StringTable.Read(iff, chunk).ForLanguage(1);
            var dict = new Dictionary<short, ParcelCoord>(Default._parcels);
            for (int i = 0; i < table.Count; i++)
            {
                var fields = table[i].Split(',');
                if (fields.Length < 3) continue;
                if (!short.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) continue;
                if (!int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mx)) continue;
                if (!int.TryParse(fields[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var my)) continue;
                if (!IsUnifiedMapLot(h)) continue;

                // Preserve road-snapped coordinates for canonical stock LotLocations entries;
                // only dynamically project modded/custom coordinates that differ from stock.
                if (Default._parcels.TryGetValue(h, out var canonical) &&
                    canonical.MapX == mx &&
                    canonical.MapY == my)
                {
                    dict[h] = canonical;
                }
                else
                {
                    var (wx, wy) = ProjectMapPointToWorldTiles(mx, my);
                    dict[h] = canonical.House == h
                        ? canonical with { MapX = mx, MapY = my, WorldTileX = wx, WorldTileY = wy }
                        : new ParcelCoord(h, mx, my, wx, wy);
                }
            }
            return new NeighborhoodWorldBlueprint(dict);
        }
        catch
        {
            return Default;
        }
    }

    public ParcelCoord GetParcelOrDefault(short house)
    {
        if (_parcels.TryGetValue(house, out var coord))
            return coord;

        int idx = house >= 50 ? house - 50 : Math.Max(0, house - 1);
        int wx = (idx % 6) * 72;
        int wy = (idx / 6) * 72;
        return new ParcelCoord(house, 0, 0, wx, wy);
    }

    public bool TryGetRelativeOffset(short focusedHouse, short targetHouse, out int offsetX, out int offsetY)
    {
        if (!_parcels.TryGetValue(focusedHouse, out var foc) || !_parcels.TryGetValue(targetHouse, out var tgt))
        {
            offsetX = 0;
            offsetY = 0;
            return false;
        }
        offsetX = tgt.WorldTileX - foc.WorldTileX;
        offsetY = tgt.WorldTileY - foc.WorldTileY;
        return true;
    }

    public static float DistanceToCanonicalRiver(float worldX, float worldY)
    {
        float bestSq = float.MaxValue;
        for (int i = 0; i < CanonicalRiverWaypoints.Length - 1; i++)
        {
            var (ax, ay) = CanonicalRiverWaypoints[i];
            var (bx, by) = CanonicalRiverWaypoints[i + 1];
            float vx = bx - ax;
            float vy = by - ay;
            float lenSq = vx * vx + vy * vy;
            float t = lenSq > 1e-5f
                ? Math.Clamp(((worldX - ax) * vx + (worldY - ay) * vy) / lenSq, 0f, 1f)
                : 0f;
            float cx = ax + t * vx;
            float cy = ay + t * vy;
            float dx = worldX - cx;
            float dy = worldY - cy;
            float dSq = dx * dx + dy * dy;
            if (dSq < bestSq) bestSq = dSq;
        }
        return MathF.Sqrt(bestSq);
    }
}
