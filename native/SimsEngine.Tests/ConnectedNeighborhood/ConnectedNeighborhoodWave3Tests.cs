using System.Collections.Generic;
using System.Linq;
using SimsEngine.Simulation.Neighborhood;
using Xunit;

namespace SimsEngine.Tests.ConnectedNeighborhood;

public sealed class ConnectedNeighborhoodWave3Tests
{
    [Fact]
    public void StreetLayout_ComputesDeterministicNonOverlappingAdjacentLotsAndRoads()
    {
        var lots = new List<LotSummary>
        {
            new() { House = 1, FamilyId = 1, LotName = "1 Sim Lane", FamilyName = "Roomies" },
            new() { House = 5, FamilyId = 2, LotName = "5 Sim Lane", FamilyName = "Goth" },
            new() { House = 2, FamilyId = 3, LotName = "2 Sim Lane" },
            new() { House = 6, FamilyId = 0, LotName = "6 Sim Lane" },
            new() { House = 7, FamilyId = 4, LotName = "7 Sim Lane", FamilyName = "Newbie" },
            new() { House = 55, FamilyId = 5, LotName = "Old Town Market" },
            new() { House = 21, FamilyId = 0, LotName = "Downtown Plaza", Zoning = "community" },
        };

        var plan = NeighborhoodStreetLayout.ComputeLayout(focusedHouse: 5, lots, maxAdjacentLots: 10, focusedRoadEdge: 1);

        Assert.Equal(5, plan.FocusedHouse);
        Assert.Equal(5, plan.AdjacentLots.Count);
        Assert.DoesNotContain(plan.AdjacentLots, p => p.House == 5);
        Assert.DoesNotContain(plan.AdjacentLots, p => p.House == 21); // Downtown excluded from Sim Lane + Old Town unified map

        // Map-accurate deterministic neighbors around Goth (House 5): includes Sim Lane & Old Town on same map
        var neighborHouses = plan.AdjacentLots.Select(p => p.House).ToHashSet();
        Assert.Contains((short)1, neighborHouses);
        Assert.Contains((short)2, neighborHouses);
        Assert.Contains((short)7, neighborHouses);
        Assert.Contains((short)55, neighborHouses);

        // No two lots overlap with each other or with the focused lot (real parcels are up to 48-64 tiles wide;
        // LotLocations.iff#STR:4 guarantees >= 48 tile residential pitch, with zero overlaps verified across all 41 IFFs).
        const int minParcelPitch = 48;
        foreach (var p in plan.AdjacentLots)
        {
            Assert.Equal(0, p.Rotation);
            bool separatedFromOrigin =
                p.TileOffsetX + minParcelPitch <= 0 ||
                p.TileOffsetX >= minParcelPitch ||
                p.TileOffsetY + minParcelPitch <= 0 ||
                p.TileOffsetY >= minParcelPitch;
            Assert.True(separatedFromOrigin, $"Adjacent lot {p.House} ({p.TileOffsetX},{p.TileOffsetY}) overlaps focused lot.");
        }

        // Deterministic map symmetry: focusing House 7 (Newbie) places House 5 (Goth) at exact inverse offset
        var fromNewbie = NeighborhoodStreetLayout.ComputeLayout(focusedHouse: 7, lots, maxAdjacentLots: 10, focusedRoadEdge: 1);
        var newbieFromGoth = Assert.Single(plan.AdjacentLots, p => p.House == 7);
        var gothFromNewbie = Assert.Single(fromNewbie.AdjacentLots, p => p.House == 5);
        Assert.Equal(-newbieFromGoth.TileOffsetX, gothFromNewbie.TileOffsetX);
        Assert.Equal(-newbieFromGoth.TileOffsetY, gothFromNewbie.TileOffsetY);
    }

    [Fact]
    public void StreetLayout_ReturnsEmptyPlanWhenDisabledOrZeroLimit()
    {
        var lots = new List<LotSummary>
        {
            new() { House = 1, FamilyId = 1, LotName = "1 Sim Lane" },
            new() { House = 5, FamilyId = 2, LotName = "5 Sim Lane" },
        };

        var plan = NeighborhoodStreetLayout.ComputeLayout(focusedHouse: 1, lots, maxAdjacentLots: 0);
        Assert.Empty(plan.AdjacentLots);
        Assert.Empty(plan.RoadStrips);
        Assert.Equal(0, plan.MinTileX);
        Assert.Equal(NeighborhoodStreetLayout.LotSizeTiles, plan.MaxTileX);
    }

    [Fact]
    public void NeighborhoodStreetGraph_ContinuousPath_SimLaneAndOldTownCommunity()
    {
        var blueprint = NeighborhoodWorldBlueprint.Default;
        var srcGateway = new SimsEngine.Simulation.ObjectPosition(58 * 16 + 8, 32 * 16 + 8, 1);

        // 1. Sim Lane -> Sim Lane (House 5 Goth -> House 7 Newbie)
        var dstSimLane = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(5, 7, null, null, blueprint);
        var pathSimLane = NeighborhoodStreetGraph.ComputeSidewalkPath(blueprint, 5, srcGateway, 7, dstSimLane);
        Assert.True(pathSimLane.Count >= 2);
        var srcP5 = blueprint.GetParcelOrDefault(5);
        var dstP7 = blueprint.GetParcelOrDefault(7);
        Assert.InRange(pathSimLane[0].WorldX, srcP5.WorldTileX + srcGateway.X / 16f - 0.1f, srcP5.WorldTileX + srcGateway.X / 16f + 0.1f);
        Assert.InRange(pathSimLane[0].WorldY, srcP5.WorldTileY + srcGateway.Y / 16f - 0.1f, srcP5.WorldTileY + srcGateway.Y / 16f + 0.1f);
        Assert.InRange(pathSimLane[^1].WorldX, dstP7.WorldTileX + dstSimLane.X / 16f - 0.1f, dstP7.WorldTileX + dstSimLane.X / 16f + 0.1f);
        Assert.InRange(pathSimLane[^1].WorldY, dstP7.WorldTileY + dstSimLane.Y / 16f - 0.1f, dstP7.WorldTileY + dstSimLane.Y / 16f + 0.1f);
        for (int i = 1; i < pathSimLane.Count; i++)
        {
            float dx = pathSimLane[i].WorldX - pathSimLane[i - 1].WorldX;
            float dy = pathSimLane[i].WorldY - pathSimLane[i - 1].WorldY;
            float seg = System.MathF.Sqrt(dx * dx + dy * dy);
            Assert.True(float.IsFinite(seg) && seg > 0f && seg <= 16.5f);
        }

        // 2. Sim Lane -> Old Town Community Lot (House 5 Goth -> House 71)
        var dstOldTown = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(5, 71, null, null, blueprint);
        var pathOldTown = NeighborhoodStreetGraph.ComputeSidewalkPath(blueprint, 5, srcGateway, 71, dstOldTown);
        Assert.True(pathOldTown.Count >= 3);
        var dstP71 = blueprint.GetParcelOrDefault(71);
        Assert.InRange(pathOldTown[^1].WorldX, dstP71.WorldTileX + dstOldTown.X / 16f - 0.1f, dstP71.WorldTileX + dstOldTown.X / 16f + 0.1f);
        Assert.InRange(pathOldTown[^1].WorldY, dstP71.WorldTileY + dstOldTown.Y / 16f - 0.1f, dstP71.WorldTileY + dstOldTown.Y / 16f + 0.1f);
        Assert.True(NeighborhoodStreetGraph.ComputeTotalDistance(pathOldTown) > NeighborhoodStreetGraph.ComputeTotalDistance(pathSimLane));
    }

    [Fact]
    public void ComputeLayout_StreamsOldTownAndCommunityLots_OnCameraPan()
    {
        var blueprint = NeighborhoodWorldBlueprint.Default;
        var lots = new List<LotSummary>();
        for (short h = 1; h <= 10; h++)
            lots.Add(new LotSummary { House = h, FamilyId = h <= 7 ? h : (short)0, LotName = $"Lot {h}" });
        for (short h = 50; h <= 80; h++)
        {
            bool comm = h is 58 or 61 or (>= 70 and <= 75);
            lots.Add(new LotSummary { House = h, FamilyId = 0, LotName = $"Old Town {h}", Zoning = comm ? "community" : "residential" });
        }

        var foc = blueprint.GetParcelOrDefault(5);
        var oldTownCenter = blueprint.GetParcelOrDefault(71);
        int camOffsetX = oldTownCenter.WorldTileX - foc.WorldTileX + 32;
        int camOffsetY = oldTownCenter.WorldTileY - foc.WorldTileY + 32;

        var plan = NeighborhoodStreetLayout.ComputeLayout(
            focusedHouse: 5,
            lots,
            maxAdjacentLots: 14,
            blueprint: blueprint,
            cameraTileX: camOffsetX,
            cameraTileY: camOffsetY);

        Assert.Equal(14, plan.AdjacentLots.Count);
        Assert.Contains(plan.AdjacentLots, p => p.House == 71 && p.IsCommunity);
        Assert.Contains(plan.AdjacentLots, p => p.IsCommunity);
        Assert.Contains(plan.AdjacentLots, p => p.House >= 50 && p.House <= 80);
    }

    [Fact]
    public void MasterRoadNetwork_And_All41Lots_ZeroPlayableYardOverlap_AlignedAndConnected()
    {
        var bp = NeighborhoodWorldBlueprint.Default;
        var net = MasterRoadNetwork.Default;
        var houses = Enumerable.Range(1, 10).Concat(Enumerable.Range(50, 31)).Select(h => (short)h).ToArray();
        Assert.Equal(41, houses.Length);
        Assert.Equal(41, bp.Parcels.Count);

        // 1. Zero playable-yard overlap across all 41*40/2 pairs & exact MasterRoadNetwork alignment
        var parcels = houses.Select(h => bp.GetParcelOrDefault(h)).ToArray();
        for (int i = 0; i < parcels.Length; i++)
        {
            var p = parcels[i];
            Assert.Equal(NeighborhoodStreetLayout.CanonicalPrimaryRoadEdge(p.House), p.PrimaryRoadEdge);
            Assert.Equal(NeighborhoodStreetLayout.CanonicalSecondaryRoadEdge(p.House), p.SecondaryRoadEdge);

            foreach (int edge in p.SecondaryRoadEdge is int sec ? new[] { p.PrimaryRoadEdge, sec } : new[] { p.PrimaryRoadEdge })
            {
                float c = p.GetRoadCenterLocal(edge);
                float rx = edge is 1 or 3 ? p.WorldTileX + c : p.WorldTileX + p.LotSizeTiles * 0.5f;
                float ry = edge is 0 or 2 ? p.WorldTileY + c : p.WorldTileY + p.LotSizeTiles * 0.5f;
                float dist = net.DistanceToRoad(rx, ry, out _, out _);
                Assert.True(dist <= 0.5f, $"Lot {p.House} edge {edge} center ({rx},{ry}) is {dist:F2} tiles from MasterRoadNetwork.");
            }

            var (aMinX, aMinY, aMaxX, aMaxY) = p.GetPlayableLocalBounds();
            float ax0 = p.WorldTileX + aMinX, ay0 = p.WorldTileY + aMinY;
            float ax1 = p.WorldTileX + aMaxX, ay1 = p.WorldTileY + aMaxY;
            for (int j = i + 1; j < parcels.Length; j++)
            {
                var q = parcels[j];
                var (bMinX, bMinY, bMaxX, bMaxY) = q.GetPlayableLocalBounds();
                float bx0 = q.WorldTileX + bMinX, by0 = q.WorldTileY + bMinY;
                float bx1 = q.WorldTileX + bMaxX, by1 = q.WorldTileY + bMaxY;
                bool separated = ax1 <= bx0 || bx1 <= ax0 || ay1 <= by0 || by1 <= ay0;
                Assert.True(separated, $"Playable yard overlap between House {p.House} and House {q.House}.");
            }
        }

        // 2. Zero disconnected stubs in MasterRoadNetwork (except Portals and CulDeSacs) + 3 bridges
        foreach (var node in net.Intersections)
        {
            if (node.Kind is RoadNodeKind.Portal or RoadNodeKind.CulDeSac)
                Assert.Single(node.ConnectedSegmentIds);
            else
                Assert.True(node.ConnectedSegmentIds.Count >= 2, $"Disconnected stub at node {node.Id} ({node.WorldX},{node.WorldY}) kind={node.Kind}.");
        }
        Assert.Equal(3, net.Segments.Count(s => s.IsBridge));

        // 3. Full sidewalk connectivity from House 5 to all other 40 lots & non-empty RoadStrips
        var lots = houses.Select(h => new LotSummary { House = h, LotName = $"Lot {h}" }).ToList();
        var plan = NeighborhoodStreetLayout.ComputeLayout(5, lots, maxAdjacentLots: 14, blueprint: bp);
        Assert.NotEmpty(plan.RoadStrips);

        var srcGw = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(5, 5, null, null, bp);
        foreach (var target in houses)
        {
            if (target == 5) continue;
            var dstGw = NeighborhoodStreetGraph.ResolveArrivalGatewayTile(5, target, null, null, bp);
            var path = NeighborhoodStreetGraph.ComputeSidewalkPath(bp, 5, srcGw, target, dstGw);
            Assert.True(path.Count >= 2, $"Missing sidewalk path 5 -> {target}");
            for (int k = 1; k < path.Count; k++)
            {
                float dx = path[k].WorldX - path[k - 1].WorldX;
                float dy = path[k].WorldY - path[k - 1].WorldY;
                Assert.InRange(System.MathF.Sqrt(dx * dx + dy * dy), 0.001f, 16.5f);
            }
        }
    }
}
