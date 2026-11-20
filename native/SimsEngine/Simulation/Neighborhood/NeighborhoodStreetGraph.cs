using System;
using System.Collections.Generic;
using System.Linq;
using SimsEngine.Formats;

namespace SimsEngine.Simulation.Neighborhood;

/// <summary>
/// Routes walking Sims continuously in global world-tile space along sidewalks, street corridors,
/// crosswalk intersections, and stone river bridges between any two unified-map lots
/// (Sim Lane 1–10 and Old Town 50–80, residential and community).
/// </summary>
public static class NeighborhoodStreetGraph
{
    // Canonical river bridge crossing points matching MasterRoadNetwork.Default bridges.
    private static readonly (float X, float Y)[] RiverBridges =
    [
        (540.5f, 80.0f),  // Small bridge on V_OT_CE
        (692.0f, 120.5f), // Two-arch stone bridge on H_MAIN
        (650.0f, 405.5f), // Large southern truss bridge on coastal highway
    ];

    public static ObjectPosition ResolveArrivalGatewayTile(
        short sourceHouse,
        short targetHouse,
        IffFile? targetLot,
        SimulationWorld? targetWorld,
        NeighborhoodWorldBlueprint? blueprint = null,
        int lotSizeTiles = NeighborhoodStreetLayout.LotSizeTiles)
    {
        var bp = blueprint ?? NeighborhoodWorldBlueprint.Default;
        var dstParcel = bp.GetParcelOrDefault(targetHouse);
        var srcParcel = bp.GetParcelOrDefault(sourceHouse);

        int resolvedSize = targetWorld is not null && targetWorld.Globals.Length > 23 && targetWorld.Globals[23] is > 0 and <= 64
            ? targetWorld.Globals[23]
            : (dstParcel.LotSizeTiles is > 0 and <= 64 ? dstParcel.LotSizeTiles : lotSizeTiles);
        int clampedSize = Math.Clamp(resolvedSize, 16, NeighborhoodStreetLayout.LotSizeTiles);
        int edge = NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(targetHouse);
        float roadCenter = dstParcel.GetRoadCenterLocal(edge);

        // Aim for the concrete sidewalk band inside the target parcel edge (center ± 5.0 tiles).
        int swCoord = edge is 0 or 3
            ? Math.Clamp((int)MathF.Floor(roadCenter + 5.0f), 4, clampedSize - 5)
            : Math.Clamp((int)MathF.Floor(roadCenter - 5.0f), 4, clampedSize - 5);

        (int baseTx, int baseTy) = edge switch
        {
            0 => (clampedSize / 2, swCoord),
            1 => (swCoord, clampedSize / 2),
            2 => (clampedSize / 2, swCoord),
            _ => (swCoord, clampedSize / 2),
        };

        // Bias slightly along the road toward the source direction for natural sidewalk entry
        int dx = srcParcel.WorldTileX - dstParcel.WorldTileX;
        int dy = srcParcel.WorldTileY - dstParcel.WorldTileY;
        if (edge is 0 or 2)
            baseTx = Math.Clamp(clampedSize / 2 + Math.Sign(dx) * 8, 8, clampedSize - 9);
        else
            baseTy = Math.Clamp(clampedSize / 2 + Math.Sign(dy) * 8, 8, clampedSize - 9);

        if (targetWorld is not null && targetWorld.WalkOrder.OfType<XPerson>().FirstOrDefault() is { } sampleSim)
        {
            if (GatewayRouting.TryFindWalkableNear(targetWorld, sampleSim, baseTx, baseTy, maxRadius: 8, spreadIndex: 0, requireInsideLot: true, out var walkable))
                return walkable;
            return GatewayRouting.ResolveWalkableSidewalkTile(targetWorld, targetLot, sampleSim, spreadIndex: 0);
        }

        return new ObjectPosition((short)(baseTx * 16 + 8), (short)(baseTy * 16 + 8), 1);
    }

    public static IReadOnlyList<(float WorldX, float WorldY)> ComputeSidewalkPath(
        NeighborhoodWorldBlueprint blueprint,
        short sourceHouse,
        ObjectPosition sourceGatewayTile,
        short targetHouse,
        ObjectPosition destGatewayTile,
        int lotSizeTiles = NeighborhoodStreetLayout.LotSizeTiles)
    {
        ArgumentNullException.ThrowIfNull(blueprint);

        var srcParcel = blueprint.GetParcelOrDefault(sourceHouse);
        var dstParcel = blueprint.GetParcelOrDefault(targetHouse);
        int srcSize = lotSizeTiles == NeighborhoodStreetLayout.LotSizeTiles && srcParcel.LotSizeTiles is > 0 and <= 64
            ? srcParcel.LotSizeTiles
            : lotSizeTiles;
        int dstSize = lotSizeTiles == NeighborhoodStreetLayout.LotSizeTiles && dstParcel.LotSizeTiles is > 0 and <= 64
            ? dstParcel.LotSizeTiles
            : lotSizeTiles;

        float srcLx = sourceGatewayTile.X > 0 ? sourceGatewayTile.X / 16f : srcSize * 0.5f;
        float srcLy = sourceGatewayTile.Y > 0 ? sourceGatewayTile.Y / 16f : srcParcel.GetRoadCenterLocal(NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(sourceHouse));
        float dstLx = destGatewayTile.X > 0 ? destGatewayTile.X / 16f : dstSize * 0.5f;
        float dstLy = destGatewayTile.Y > 0 ? destGatewayTile.Y / 16f : dstParcel.GetRoadCenterLocal(NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(targetHouse));

        float sx = srcParcel.WorldTileX + srcLx;
        float sy = srcParcel.WorldTileY + srcLy;
        float ex = dstParcel.WorldTileX + dstLx;
        float ey = dstParcel.WorldTileY + dstLy;

        var rawWaypoints = new List<(float X, float Y)>(32);
        if (MasterRoadNetwork.Default.TryFindSidewalkPath(sourceHouse, sx, sy, targetHouse, ex, ey, rawWaypoints) &&
            rawWaypoints.Count >= 2)
        {
            return SubdivideAndDeduplicate(rawWaypoints, maxSegmentLengthTiles: 16f);
        }

        // Fallback for non-unified / custom lots outside the 41-lot map
        rawWaypoints.Clear();
        rawWaypoints.Add((sx, sy));

        int srcEdge = NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(sourceHouse);
        int dstEdge = NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(targetHouse);
        float srcCenter = srcParcel.GetRoadCenterLocal(srcEdge);
        float dstCenter = dstParcel.GetRoadCenterLocal(dstEdge);

        float srcRoadX = srcEdge switch
        {
            1 => srcParcel.WorldTileX + srcCenter - 5.0f,
            3 => srcParcel.WorldTileX + srcCenter + 5.0f,
            _ => sx,
        };
        float srcRoadY = srcEdge switch
        {
            0 => srcParcel.WorldTileY + srcCenter + 5.0f,
            2 => srcParcel.WorldTileY + srcCenter - 5.0f,
            _ => sy,
        };
        float dstRoadX = dstEdge switch
        {
            1 => dstParcel.WorldTileX + dstCenter - 5.0f,
            3 => dstParcel.WorldTileX + dstCenter + 5.0f,
            _ => ex,
        };
        float dstRoadY = dstEdge switch
        {
            0 => dstParcel.WorldTileY + dstCenter + 5.0f,
            2 => dstParcel.WorldTileY + dstCenter - 5.0f,
            _ => ey,
        };

        AppendOrthogonalSegment(rawWaypoints, srcRoadX, srcRoadY, horizontalFirst: srcEdge is 0 or 2);

        if (CrossesCanonicalRiver(srcRoadX, srcRoadY, dstRoadX, dstRoadY))
        {
            var bridge = SelectNearestBridge(srcRoadX, srcRoadY, dstRoadX, dstRoadY);
            AppendOrthogonalSegment(rawWaypoints, bridge.X - 18f, bridge.Y, horizontalFirst: srcEdge is 0 or 2);
            AppendOrthogonalSegment(rawWaypoints, bridge.X + 18f, bridge.Y, horizontalFirst: true);
        }

        AppendOrthogonalSegment(rawWaypoints, dstRoadX, dstRoadY, horizontalFirst: dstEdge is 1 or 3);
        AppendOrthogonalSegment(rawWaypoints, ex, ey, horizontalFirst: dstEdge is 0 or 2);

        return SubdivideAndDeduplicate(rawWaypoints, maxSegmentLengthTiles: 16f);
    }

    public static float ComputeTotalDistance(IReadOnlyList<(float WorldX, float WorldY)> waypoints)
    {
        if (waypoints is null || waypoints.Count < 2)
            return 0f;

        float total = 0f;
        for (int i = 1; i < waypoints.Count; i++)
        {
            float dx = waypoints[i].WorldX - waypoints[i - 1].WorldX;
            float dy = waypoints[i].WorldY - waypoints[i - 1].WorldY;
            total += MathF.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static void AppendOrthogonalSegment(
        List<(float X, float Y)> points,
        float targetX,
        float targetY,
        bool horizontalFirst)
    {
        var last = points[^1];
        if (MathF.Abs(last.X - targetX) > 0.25f && MathF.Abs(last.Y - targetY) > 0.25f)
        {
            if (horizontalFirst)
                points.Add((targetX, last.Y));
            else
                points.Add((last.X, targetY));
        }
        points.Add((targetX, targetY));
    }

    private static bool CrossesCanonicalRiver(float x0, float y0, float x1, float y1)
    {
        const int samples = 8;
        for (int i = 1; i < samples; i++)
        {
            float t = i / (float)samples;
            float px = x0 + (x1 - x0) * t;
            float py = y0 + (y1 - y0) * t;
            if (NeighborhoodWorldBlueprint.DistanceToCanonicalRiver(px, py) < NeighborhoodWorldBlueprint.DefaultRiverHalfWidthTiles)
                return true;
        }
        return false;
    }

    private static (float X, float Y) SelectNearestBridge(float sx, float sy, float ex, float ey)
    {
        float bestScore = float.MaxValue;
        var best = RiverBridges[1];
        foreach (var b in RiverBridges)
        {
            float d0 = MathF.Sqrt((b.X - sx) * (b.X - sx) + (b.Y - sy) * (b.Y - sy));
            float d1 = MathF.Sqrt((ex - b.X) * (ex - b.X) + (ey - b.Y) * (ey - b.Y));
            float score = d0 + d1;
            if (score < bestScore)
            {
                bestScore = score;
                best = b;
            }
        }
        return best;
    }

    private static List<(float WorldX, float WorldY)> SubdivideAndDeduplicate(
        List<(float X, float Y)> raw,
        float maxSegmentLengthTiles)
    {
        var result = new List<(float WorldX, float WorldY)>(raw.Count * 2);
        if (raw.Count == 0)
            return result;

        result.Add(raw[0]);
        for (int i = 1; i < raw.Count; i++)
        {
            var prev = result[^1];
            var next = raw[i];
            float dx = next.X - prev.WorldX;
            float dy = next.Y - prev.WorldY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < 0.05f)
                continue;

            int steps = Math.Max(1, (int)MathF.Ceiling(dist / maxSegmentLengthTiles));
            for (int s = 1; s <= steps; s++)
            {
                float t = s / (float)steps;
                result.Add((prev.WorldX + dx * t, prev.WorldY + dy * t));
            }
        }

        // Preserve exact start and end coordinates even if final segment was < 0.05f
        if (raw.Count >= 2 && result.Count >= 2)
        {
            var lastRaw = raw[^1];
            var lastRes = result[^1];
            if (MathF.Abs(lastRes.WorldX - lastRaw.X) > 1e-4f || MathF.Abs(lastRes.WorldY - lastRaw.Y) > 1e-4f)
            {
                result[^1] = (lastRaw.X, lastRaw.Y);
            }
        }
        else if (raw.Count >= 2 && result.Count == 1)
        {
            result.Add((raw[^1].X, raw[^1].Y));
        }

        return result;
    }
}
