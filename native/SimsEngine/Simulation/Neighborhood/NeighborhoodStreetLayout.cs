using System;
using System.Collections.Generic;
using System.Linq;

namespace SimsEngine.Simulation.Neighborhood;

/// <summary>Describes a single lot's placement in the deterministic isometric neighborhood world relative to the focused lot.</summary>
public sealed record NeighborhoodLotPlacement(
    short House,
    int TileOffsetX,
    int TileOffsetY,
    int RoadEdge,
    int Rotation,
    string LotName,
    string? FamilyName,
    bool Occupied,
    bool IsCommunity = false);

/// <summary>Describes a synthesized street/sidewalk segment connecting adjacent lots in tile coordinates relative to the focused lot.</summary>
public sealed record NeighborhoodRoadStrip(
    int MinTileX,
    int MinTileY,
    int MaxTileX,
    int MaxTileY,
    bool Horizontal,
    bool IsIntersection);

/// <summary>Complete deterministic layout of the focused lot, its nearest neighbor parcels on the town map, and road strips.</summary>
public sealed record NeighborhoodLayoutPlan(
    short FocusedHouse,
    IReadOnlyList<NeighborhoodLotPlacement> AdjacentLots,
    IReadOnlyList<NeighborhoodRoadStrip> RoadStrips,
    int MinTileX,
    int MinTileY,
    int MaxTileX,
    int MaxTileY);

/// <summary>
/// Computes deterministic isometric tile offsets for Connected Neighborhood parcels directly from
/// <see cref="NeighborhoodWorldBlueprint"/> (<c>LotLocations.iff#STR:4</c>) and <see cref="MasterRoadNetwork"/>,
/// keeping every Maxis lot in its natural world orientation (<c>Rotation = 0</c>) across both Sim Lane (1–10)
/// and Old Town (50–80).
/// </summary>
public static class NeighborhoodStreetLayout
{
    public const int LotSizeTiles = 64;
    public const int StreetGapTiles = 0;
    public const int LotPitchTiles = LotSizeTiles + StreetGapTiles;

    public static MasterRoadNetwork MasterNetwork => MasterRoadNetwork.Default;

    public static int CanonicalPrimaryRoadEdge(short house) => house switch
    {
        62 or 64 or 65 or 67 or 70 or 73 or 76 or 79 => 0,
        1 or 5 or 6 or 50 or 53 or 55 or 57 or 61 or 72 or 74 or 75 => 1,
        2 or 7 or 60 or 63 or 66 or 71 or 80 => 2,
        3 or 4 or 8 or 9 or 10 or 51 or 52 or 54 or 56 or 58 or 59 or 68 or 69 or 77 or 78 => 3,
        _ => 1,
    };

    public static int? CanonicalSecondaryRoadEdge(short house) => house switch
    {
        6 or 9 or 52 or 53 or 58 or 61 or 78 => 0,
        7 or 60 => 1,
        50 or 54 or 55 or 56 or 72 or 75 or 77 => 2,
        63 or 71 => 3,
        _ => null,
    };

    /// <summary>
    /// Computes the deterministic surrounding lot placements around <paramref name="focusedHouse"/>
    /// using the official town map blueprint coordinates and <see cref="MasterRoadNetwork"/>.
    /// </summary>
    public static NeighborhoodLayoutPlan ComputeLayout(
        short focusedHouse,
        IReadOnlyList<LotSummary> availableLots,
        int maxAdjacentLots = 14,
        int focusedRoadEdge = 2,
        int lotSizeTiles = LotSizeTiles,
        NeighborhoodWorldBlueprint? blueprint = null,
        int? cameraTileX = null,
        int? cameraTileY = null)
    {
        ArgumentNullException.ThrowIfNull(availableLots);
        int pitch = Math.Clamp(lotSizeTiles, 16, LotSizeTiles);

        if (focusedHouse <= 0 || maxAdjacentLots <= 0 || !IsUnifiedMapLot(focusedHouse))
        {
            return new NeighborhoodLayoutPlan(
                focusedHouse,
                [],
                [],
                0, 0, pitch, pitch);
        }

        var bp = blueprint ?? NeighborhoodWorldBlueprint.Default;
        var focusedCoord = bp.GetParcelOrDefault(focusedHouse);

        var allUnified = availableLots
            .Where(lot => lot.House > 0 && lot.House != focusedHouse && IsUnifiedMapLot(lot.House))
            .Select(lot =>
            {
                var coord = bp.GetParcelOrDefault(lot.House);
                int dx = coord.WorldTileX - focusedCoord.WorldTileX;
                int dy = coord.WorldTileY - focusedCoord.WorldTileY;
                int halfSz = coord.LotSizeTiles / 2;
                long focDistSq = (long)dx * dx + (long)dy * dy;
                long camDx = cameraTileX is int cx ? (dx + halfSz - cx) : dx;
                long camDy = cameraTileY is int cy ? (dy + halfSz - cy) : dy;
                long camDistSq = camDx * camDx + camDy * camDy;
                return (Lot: lot, Coord: coord, OffsetX: dx, OffsetY: dy, FocDistSq: focDistSq, CamDistSq: camDistSq);
            })
            .ToList();

        // Always keep the nearest neighbors around the focused lot loaded, and stream the rest around
        // the camera's current viewport center across Sim Lane + Old Town + Community Lots.
        var chosen = new List<(LotSummary Lot, NeighborhoodWorldBlueprint.ParcelCoord Coord, int OffsetX, int OffsetY, long FocDistSq, long CamDistSq)>(
            Math.Min(maxAdjacentLots, allUnified.Count));
        var chosenHouses = new HashSet<short>();

        int localAnchorCount = cameraTileX.HasValue && cameraTileY.HasValue ? Math.Min(6, maxAdjacentLots) : maxAdjacentLots;
        foreach (var item in allUnified
                     .OrderBy(x => x.FocDistSq)
                     .ThenByDescending(x => x.Lot.IsOccupied || x.Lot.IsCommunity)
                     .ThenBy(x => x.Lot.House)
                     .Take(localAnchorCount))
        {
            if (chosenHouses.Add(item.Lot.House))
                chosen.Add(item);
        }

        if (chosen.Count < maxAdjacentLots)
        {
            foreach (var item in allUnified
                         .Where(x => !chosenHouses.Contains(x.Lot.House))
                         .OrderBy(x => x.CamDistSq)
                         .ThenBy(x => x.FocDistSq)
                         .ThenByDescending(x => x.Lot.IsOccupied || x.Lot.IsCommunity)
                         .ThenBy(x => x.Lot.House)
                         .Take(maxAdjacentLots - chosen.Count))
            {
                if (chosenHouses.Add(item.Lot.House))
                    chosen.Add(item);
            }
        }

        var placements = new List<NeighborhoodLotPlacement>(chosen.Count);
        int focusedSz = focusedCoord.LotSizeTiles is > 0 and <= LotSizeTiles ? focusedCoord.LotSizeTiles : pitch;
        int minX = 0;
        int minY = 0;
        int maxX = focusedSz;
        int maxY = focusedSz;

        // Global camera pan bounds span ALL 41 unified blueprint parcels (Sim Lane + Old Town + Community Lots)
        foreach (var coord in bp.Parcels.Values)
        {
            int dx = coord.WorldTileX - focusedCoord.WorldTileX;
            int dy = coord.WorldTileY - focusedCoord.WorldTileY;
            int sz = coord.LotSizeTiles is > 0 and <= LotSizeTiles ? coord.LotSizeTiles : LotSizeTiles;
            minX = Math.Min(minX, dx);
            minY = Math.Min(minY, dy);
            maxX = Math.Max(maxX, dx + sz);
            maxY = Math.Max(maxY, dy + sz);
        }

        var roadStrips = MasterNetwork.ToRoadStrips(focusedCoord.WorldTileX, focusedCoord.WorldTileY);
        foreach (var strip in roadStrips)
        {
            minX = Math.Min(minX, strip.MinTileX);
            minY = Math.Min(minY, strip.MinTileY);
            maxX = Math.Max(maxX, strip.MaxTileX);
            maxY = Math.Max(maxY, strip.MaxTileY);
        }

        foreach (var item in chosen)
        {
            placements.Add(new NeighborhoodLotPlacement(
                House: item.Lot.House,
                TileOffsetX: item.OffsetX,
                TileOffsetY: item.OffsetY,
                RoadEdge: CanonicalPrimaryRoadEdge(item.Lot.House),
                Rotation: 0, // Every HouseXX.iff in Neighborhood 1 + Old Town shares the same natural Maxis orientation!
                LotName: string.IsNullOrWhiteSpace(item.Lot.LotName) ? $"Lot {item.Lot.House}" : item.Lot.LotName,
                FamilyName: item.Lot.FamilyName,
                Occupied: item.Lot.IsOccupied,
                IsCommunity: item.Lot.IsCommunity));

            int sz = item.Coord.LotSizeTiles is > 0 and <= LotSizeTiles ? item.Coord.LotSizeTiles : LotSizeTiles;
            minX = Math.Min(minX, item.OffsetX);
            minY = Math.Min(minY, item.OffsetY);
            maxX = Math.Max(maxX, item.OffsetX + sz);
            maxY = Math.Max(maxY, item.OffsetY + sz);
        }

        return new NeighborhoodLayoutPlan(focusedHouse, placements, roadStrips, minX, minY, maxX, maxY);
    }

    private static bool IsUnifiedMapLot(short house) =>
        NeighborhoodWorldBlueprint.IsUnifiedMapLot(house);
}
