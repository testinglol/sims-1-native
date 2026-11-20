using System;
using System.Collections.Generic;
using System.Linq;

namespace SimsEngine.Simulation.Neighborhood;

public enum RoadNodeKind : byte
{
    PassThrough,
    FourWay,
    TJunction,
    CornerCurve,
    CulDeSac,
    Portal,
}

public enum TurnKind : byte
{
    Straight,
    RightTurn,
    LeftTurn,
    Curve,
    UTurn,
}

public enum BridgeKind : byte
{
    None,
    SmallBridge,
    StoneArchBridge,
    TrussBridge,
}

public enum SidewalkEdgeKind : byte
{
    Sidewalk,
    Crosswalk,
    BridgeWalkway,
    CornerConnector,
}

public sealed record MasterRoadSegment(
    int Id,
    string Name,
    float StartX,
    float StartY,
    float EndX,
    float EndY,
    float HalfWidth,
    float SidewalkOffset,
    int StartNodeId,
    int EndNodeId,
    BridgeKind Bridge = BridgeKind.None,
    bool IsLoopCurve = false,
    bool TwoWay = true)
{
    public bool IsBridge => Bridge != BridgeKind.None;
    public bool IsHorizontal => MathF.Abs(EndY - StartY) < 0.01f;
    public bool IsVertical => MathF.Abs(EndX - StartX) < 0.01f;
    public float Length => MathF.Sqrt((EndX - StartX) * (EndX - StartX) + (EndY - StartY) * (EndY - StartY));
}

public sealed record LaneTurnConnection(
    int IncomingSegmentId,
    int OutgoingSegmentId,
    TurnKind TurnKind,
    float EntryX,
    float EntryY,
    float ControlX,
    float ControlY,
    float ExitX,
    float ExitY);

public sealed record RoadIntersectionNode(
    int Id,
    float WorldX,
    float WorldY,
    RoadNodeKind Kind,
    IReadOnlyList<int> ConnectedSegmentIds,
    IReadOnlyList<LaneTurnConnection> TurnConnections);

public sealed record SidewalkNode(int Id, float WorldX, float WorldY, short? LotHouse = null);

public sealed record SidewalkEdge(
    int FromNodeId,
    int ToNodeId,
    float Length,
    SidewalkEdgeKind Kind,
    int? RoadSegmentId = null);

/// <summary>
/// Authoritative unified world road, intersection, bridge, and sidewalk graph spanning all 41 parcels
/// of Sims 1 Neighborhood 1 + Old Town (<c>nscreen_unleashed_coastal_4k.jpg</c>).
/// </summary>
public sealed class MasterRoadNetwork
{
    public const float DefaultHalfWidth = 3.5f;
    public const float DefaultSidewalkOffset = 5.0f;
    public const float DefaultLaneOffset = 1.35f;

    // Canonical avenue axes (all N + 0.5 tile centerlines matching HouseXX.iff Pattern 10/11).
    public const float V_OT_W = 200.5f;
    public const float V_OT_CW = 330.5f;
    public const float V_OT_CSW = 435.5f;
    public const float V_SIM_W = 430.5f;
    public const float V_OT_CE = 540.5f;
    public const float V_SIM_E = 645.5f;
    public const float V_EAST_1 = 830.5f;
    public const float V_EAST_2 = 930.5f;

    public const float H_SIM_N = -170.5f;
    public const float H_SIM_S = 15.5f;
    public const float H_MAIN = 120.5f;
    public const float H_OT_E_MD = 235.5f;
    public const float H_OT_MID = 305.5f;
    public const float H_SO_E = 365.5f;
    public const float H_SO_W = 445.5f;

    public static MasterRoadNetwork Default { get; } = BuildDefault();

    public IReadOnlyList<MasterRoadSegment> Segments { get; }
    public IReadOnlyList<RoadIntersectionNode> Intersections { get; }
    public IReadOnlyList<SidewalkNode> SidewalkNodes { get; }
    public IReadOnlyList<SidewalkEdge> SidewalkEdges { get; }
    public IReadOnlyDictionary<short, IReadOnlyList<int>> LotSidewalkNodeIds { get; }

    private readonly Dictionary<int, List<SidewalkEdge>> _sidewalkAdj;
    private readonly Dictionary<int, (int PosLeftNode, int PosRightNode, int NegLeftNode, int NegRightNode)> _segmentSidewalkEndpoints;
    private readonly List<(int SegId, int SideSign, List<int> OrderedNodeIds)> _segmentSidewalkChains;

    private MasterRoadNetwork(
        IReadOnlyList<MasterRoadSegment> segments,
        IReadOnlyList<RoadIntersectionNode> intersections,
        IReadOnlyList<SidewalkNode> sidewalkNodes,
        IReadOnlyList<SidewalkEdge> sidewalkEdges,
        IReadOnlyDictionary<short, IReadOnlyList<int>> lotSidewalkNodeIds,
        Dictionary<int, (int, int, int, int)> segmentSidewalkEndpoints,
        List<(int, int, List<int>)> segmentSidewalkChains)
    {
        Segments = segments;
        Intersections = intersections;
        SidewalkNodes = sidewalkNodes;
        SidewalkEdges = sidewalkEdges;
        LotSidewalkNodeIds = lotSidewalkNodeIds;
        _segmentSidewalkEndpoints = segmentSidewalkEndpoints;
        _segmentSidewalkChains = segmentSidewalkChains;

        _sidewalkAdj = new Dictionary<int, List<SidewalkEdge>>(sidewalkNodes.Count);
        foreach (var node in sidewalkNodes)
            _sidewalkAdj[node.Id] = [];
        foreach (var edge in sidewalkEdges)
        {
            _sidewalkAdj[edge.FromNodeId].Add(edge);
            _sidewalkAdj[edge.ToNodeId].Add(new SidewalkEdge(edge.ToNodeId, edge.FromNodeId, edge.Length, edge.Kind, edge.RoadSegmentId));
        }
    }

    public IReadOnlyList<NeighborhoodRoadStrip> ToRoadStrips(int originWorldX, int originWorldY)
    {
        var strips = new List<NeighborhoodRoadStrip>(Segments.Count + Intersections.Count);
        foreach (var seg in Segments)
        {
            float minX = MathF.Min(seg.StartX, seg.EndX);
            float maxX = MathF.Max(seg.StartX, seg.EndX);
            float minY = MathF.Min(seg.StartY, seg.EndY);
            float maxY = MathF.Max(seg.StartY, seg.EndY);
            int padX = seg.IsVertical ? (int)MathF.Ceiling(seg.HalfWidth) : 0;
            int padY = seg.IsHorizontal ? (int)MathF.Ceiling(seg.HalfWidth) : 0;
            if (!seg.IsHorizontal && !seg.IsVertical)
            {
                padX = (int)MathF.Ceiling(seg.HalfWidth);
                padY = (int)MathF.Ceiling(seg.HalfWidth);
            }

            int x0 = (int)MathF.Floor(minX - padX) - originWorldX;
            int y0 = (int)MathF.Floor(minY - padY) - originWorldY;
            int x1 = (int)MathF.Ceiling(maxX + padX) - originWorldX;
            int y1 = (int)MathF.Ceiling(maxY + padY) - originWorldY;
            if (x1 <= x0) x1 = x0 + 1;
            if (y1 <= y0) y1 = y0 + 1;

            strips.Add(new NeighborhoodRoadStrip(x0, y0, x1, y1, Horizontal: seg.IsHorizontal || MathF.Abs(seg.EndX - seg.StartX) >= MathF.Abs(seg.EndY - seg.StartY), IsIntersection: false));
        }

        foreach (var node in Intersections)
        {
            if (node.Kind is not (RoadNodeKind.FourWay or RoadNodeKind.TJunction or RoadNodeKind.CornerCurve))
                continue;
            int pad = (int)MathF.Ceiling(DefaultHalfWidth);
            int x0 = (int)MathF.Floor(node.WorldX - pad) - originWorldX;
            int y0 = (int)MathF.Floor(node.WorldY - pad) - originWorldY;
            int x1 = (int)MathF.Ceiling(node.WorldX + pad) - originWorldX;
            int y1 = (int)MathF.Ceiling(node.WorldY + pad) - originWorldY;
            strips.Add(new NeighborhoodRoadStrip(x0, y0, x1, y1, Horizontal: true, IsIntersection: true));
        }

        return strips;
    }

    public float DistanceToRoad(float worldX, float worldY, out MasterRoadSegment? nearest, out float sidewalkHalfWidth)
    {
        float bestDist = float.MaxValue;
        nearest = null;
        sidewalkHalfWidth = DefaultSidewalkOffset;

        for (int i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            float dx = seg.EndX - seg.StartX;
            float dy = seg.EndY - seg.StartY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6f) continue;

            float t = Math.Clamp(((worldX - seg.StartX) * dx + (worldY - seg.StartY) * dy) / lenSq, 0f, 1f);
            float px = seg.StartX + t * dx;
            float py = seg.StartY + t * dy;
            float d = MathF.Sqrt((worldX - px) * (worldX - px) + (worldY - py) * (worldY - py));
            if (d < bestDist)
            {
                bestDist = d;
                nearest = seg;
                sidewalkHalfWidth = seg.SidewalkOffset;
            }
        }

        return bestDist;
    }

    public bool TryFindSidewalkPath(
        short srcHouse,
        float sx,
        float sy,
        short dstHouse,
        float ex,
        float ey,
        List<(float X, float Y)> outWaypoints)
    {
        ArgumentNullException.ThrowIfNull(outWaypoints);
        outWaypoints.Clear();

        if (srcHouse == dstHouse)
        {
            outWaypoints.Add((sx, sy));
            outWaypoints.Add((ex, ey));
            return true;
        }

        if (!LotSidewalkNodeIds.TryGetValue(srcHouse, out var srcNodes) || srcNodes.Count == 0 ||
            !LotSidewalkNodeIds.TryGetValue(dstHouse, out var dstNodes) || dstNodes.Count == 0)
        {
            return false;
        }

        // Project source/destination gateway points onto their nearest lot sidewalk edge so Sims step
        // cleanly onto the sidewalk without cutting diagonally across neighboring yards.
        var (srcProjX, srcProjY, srcSegA, srcSegB) = ProjectOntoLotSidewalk(srcHouse, sx, sy, srcNodes);
        var (dstProjX, dstProjY, dstSegA, dstSegB) = ProjectOntoLotSidewalk(dstHouse, ex, ey, dstNodes);

        outWaypoints.Add((sx, sy));

        // Direct same-sidewalk-edge check (no intersection crossing or detour needed)
        if ((srcSegA == dstSegA && srcSegB == dstSegB) || (srcSegA == dstSegB && srcSegB == dstSegA))
        {
            outWaypoints.Add((srcProjX, srcProjY));
            outWaypoints.Add((dstProjX, dstProjY));
            outWaypoints.Add((ex, ey));
            return true;
        }

        // Dijkstra from srcProj (seeded onto srcSegA / srcSegB) to dstProj (terminating via dstSegA / dstSegB)
        int nCount = SidewalkNodes.Count;
        var dist = new float[nCount];
        var prev = new int[nCount];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(prev, -1);

        var pq = new PriorityQueue<int, float>();
        void SeedStart(int nodeId)
        {
            var n = SidewalkNodes[nodeId];
            float d = MathF.Sqrt((n.WorldX - srcProjX) * (n.WorldX - srcProjX) + (n.WorldY - srcProjY) * (n.WorldY - srcProjY));
            if (d < dist[nodeId])
            {
                dist[nodeId] = d;
                pq.Enqueue(nodeId, d);
            }
        }
        SeedStart(srcSegA);
        if (srcSegB != srcSegA) SeedStart(srcSegB);

        while (pq.TryDequeue(out int u, out float du))
        {
            if (du > dist[u]) continue;
            if (_sidewalkAdj.TryGetValue(u, out var edges))
            {
                for (int i = 0; i < edges.Count; i++)
                {
                    var e = edges[i];
                    float nd = du + e.Length;
                    if (nd < dist[e.ToNodeId])
                    {
                        dist[e.ToNodeId] = nd;
                        prev[e.ToNodeId] = u;
                        pq.Enqueue(e.ToNodeId, nd);
                    }
                }
            }
        }

        float DistToDstProj(int nodeId)
        {
            if (!float.IsFinite(dist[nodeId])) return float.PositiveInfinity;
            var n = SidewalkNodes[nodeId];
            return dist[nodeId] + MathF.Sqrt((n.WorldX - dstProjX) * (n.WorldX - dstProjX) + (n.WorldY - dstProjY) * (n.WorldY - dstProjY));
        }

        int bestEnd = DistToDstProj(dstSegA) <= DistToDstProj(dstSegB) ? dstSegA : dstSegB;
        if (!float.IsFinite(dist[bestEnd]))
            return false;

        var chain = new List<int>();
        for (int cur = bestEnd; cur != -1; cur = prev[cur])
            chain.Add(cur);
        chain.Reverse();

        outWaypoints.Add((srcProjX, srcProjY));
        foreach (int nid in chain)
        {
            var n = SidewalkNodes[nid];
            outWaypoints.Add((n.WorldX, n.WorldY));
        }
        outWaypoints.Add((dstProjX, dstProjY));
        outWaypoints.Add((ex, ey));
        return true;
    }

    private (float ProjX, float ProjY, int NodeA, int NodeB) ProjectOntoLotSidewalk(
        short house,
        float wx,
        float wy,
        IReadOnlyList<int> lotNodeIds)
    {
        float bestDistSq = float.MaxValue;
        float bestX = wx;
        float bestY = wy;
        int bestA = lotNodeIds[0];
        int bestB = lotNodeIds[0];

        foreach (int nid in lotNodeIds)
        {
            var n = SidewalkNodes[nid];
            float ndSq = (n.WorldX - wx) * (n.WorldX - wx) + (n.WorldY - wy) * (n.WorldY - wy);
            if (ndSq < bestDistSq)
            {
                bestDistSq = ndSq;
                bestX = n.WorldX;
                bestY = n.WorldY;
                bestA = nid;
                bestB = nid;
            }

            if (_sidewalkAdj.TryGetValue(nid, out var adj))
            {
                foreach (var e in adj)
                {
                    if (e.Kind is not (SidewalkEdgeKind.Sidewalk or SidewalkEdgeKind.BridgeWalkway))
                        continue;
                    var other = SidewalkNodes[e.ToNodeId];
                    float ex = other.WorldX - n.WorldX;
                    float ey = other.WorldY - n.WorldY;
                    float lenSq = ex * ex + ey * ey;
                    if (lenSq < 1e-4f) continue;
                    float t = Math.Clamp(((wx - n.WorldX) * ex + (wy - n.WorldY) * ey) / lenSq, 0f, 1f);
                    float px = n.WorldX + t * ex;
                    float py = n.WorldY + t * ey;
                    float dSq = (wx - px) * (wx - px) + (wy - py) * (wy - py);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestX = px;
                        bestY = py;
                        bestA = nid;
                        bestB = e.ToNodeId;
                    }
                }
            }
        }

        return (bestX, bestY, bestA, bestB);
    }

    private static MasterRoadNetwork BuildDefault()
    {
        var rawNodes = new Dictionary<string, (int Id, float X, float Y, RoadNodeKind Kind)>(StringComparer.Ordinal);
        var nodeList = new List<(int Id, float X, float Y, RoadNodeKind Kind)>();

        int Node(string key, float x, float y, RoadNodeKind kind)
        {
            if (rawNodes.TryGetValue(key, out var existing))
                return existing.Id;
            int id = nodeList.Count;
            var entry = (id, x, y, kind);
            rawNodes[key] = entry;
            nodeList.Add(entry);
            return id;
        }

        // 1. Define every road intersection, corner curve, portal, bridge abutment, and cul-de-sac
        // Sim Lane Loop
        int nSimNwS = Node("SIM_NW_S", V_SIM_W, -145.5f, RoadNodeKind.CornerCurve);
        int nSimNwE = Node("SIM_NW_E", 455.5f, H_SIM_N, RoadNodeKind.CornerCurve);
        int nSimNeW = Node("SIM_NE_W", 620.5f, H_SIM_N, RoadNodeKind.CornerCurve);
        int nSimNeS = Node("SIM_NE_S", V_SIM_E, -145.5f, RoadNodeKind.CornerCurve);
        int nSimSe  = Node("SIM_SE",   V_SIM_E, H_SIM_S, RoadNodeKind.CornerCurve);
        int nSimOtT = Node("SIM_OT_T", V_OT_CE, H_SIM_S, RoadNodeKind.TJunction);
        int nSimSwE = Node("SIM_SW_E", 450.5f, H_SIM_S, RoadNodeKind.CornerCurve);
        int nSimSwN = Node("SIM_SW_N", V_SIM_W, -4.5f, RoadNodeKind.CornerCurve);

        // Old Town West & Central
        int nPortalN  = Node("PORTAL_N",  V_OT_W, -30.0f, RoadNodeKind.Portal);
        int nPortalNw = Node("PORTAL_NW", 85.0f, H_MAIN, RoadNodeKind.Portal);
        int nMainW    = Node("MAIN_W",    V_OT_W, H_MAIN, RoadNodeKind.FourWay);
        int nOtCwN    = Node("OT_CW_N",   V_OT_CW, 79.0f, RoadNodeKind.CulDeSac);
        int nMainCw   = Node("MAIN_CW",   V_OT_CW, H_MAIN, RoadNodeKind.FourWay);

        int nSmallBrN = Node("SMALL_BR_N", V_OT_CE, 62.0f, RoadNodeKind.PassThrough);
        int nSmallBrS = Node("SMALL_BR_S", V_OT_CE, 98.0f, RoadNodeKind.PassThrough);
        int nMainCe   = Node("MAIN_CE",    V_OT_CE, H_MAIN, RoadNodeKind.FourWay);

        int nStoneBrW = Node("STONE_BR_W", 660.0f, H_MAIN, RoadNodeKind.PassThrough);
        int nStoneBrE = Node("STONE_BR_E", 724.0f, H_MAIN, RoadNodeKind.PassThrough);

        int nMidW     = Node("MID_W",   V_OT_W,   H_OT_MID, RoadNodeKind.TJunction);
        int nMidCw    = Node("MID_CW",  V_OT_CW,  H_OT_MID, RoadNodeKind.TJunction);
        int nMidCsw   = Node("MID_CSW", V_OT_CSW, H_OT_MID, RoadNodeKind.TJunction);
        int nMidCe    = Node("MID_CE",  V_OT_CE,  H_OT_MID, RoadNodeKind.TJunction);
        int nMidCul   = Node("MID_CUL", 615.5f,   H_OT_MID, RoadNodeKind.CulDeSac);

        int nSwCurveN = Node("SW_CURVE_N", V_OT_W, 395.5f, RoadNodeKind.CornerCurve);
        int nSwCurveM = Node("SW_CURVE_M", 225.5f, 430.5f, RoadNodeKind.CornerCurve);
        int nSwCurveE = Node("SW_CURVE_E", 265.5f, H_SO_W, RoadNodeKind.CornerCurve);
        int nSoCsw    = Node("SO_CSW",     V_OT_CSW, H_SO_W, RoadNodeKind.TJunction);
        int nTrussW   = Node("TRUSS_W",    585.0f, H_SO_W, RoadNodeKind.CornerCurve);
        int nTrussE   = Node("TRUSS_E",    715.0f, H_SO_E, RoadNodeKind.CornerCurve);

        // Old Town East
        int nEast1N   = Node("EAST1_N",    V_EAST_1, 76.0f, RoadNodeKind.CulDeSac);
        int nMainE1   = Node("MAIN_E1",    V_EAST_1, H_MAIN, RoadNodeKind.FourWay);
        int nEast2N   = Node("EAST2_N",    V_EAST_2, 76.0f, RoadNodeKind.CulDeSac);
        int nMainE2   = Node("MAIN_E2",    V_EAST_2, H_MAIN, RoadNodeKind.FourWay);

        int nPenNe    = Node("PEN_NE",     1010.5f, H_MAIN, RoadNodeKind.CornerCurve);
        int nPenE1    = Node("PEN_E1",     1035.5f, 145.5f, RoadNodeKind.CornerCurve);
        int nPenE2    = Node("PEN_E2",     1035.5f, 165.5f, RoadNodeKind.CornerCurve);
        int nPenSe    = Node("PEN_SE",     1010.5f, 185.5f, RoadNodeKind.CornerCurve);
        int nEast2Lp  = Node("EAST2_LP",   V_EAST_2, 185.5f, RoadNodeKind.TJunction);

        int nOtEMd1   = Node("OT_E_MD_1",  V_EAST_1, H_OT_E_MD, RoadNodeKind.TJunction);
        int nOtEMd2   = Node("OT_E_MD_2",  V_EAST_2, H_OT_E_MD, RoadNodeKind.TJunction);
        int nSoE1     = Node("SO_E_1",     V_EAST_1, H_SO_E, RoadNodeKind.TJunction);
        int nSeCurveN = Node("SE_CURVE_N", V_EAST_2, 335.5f, RoadNodeKind.CornerCurve);
        int nSeCurveW = Node("SE_CURVE_W", 900.5f, H_SO_E, RoadNodeKind.CornerCurve);

        // 2. Define every MasterRoadSegment connecting the nodes
        var segments = new List<MasterRoadSegment>(48);
        void Seg(string name, int n0, int n1, BridgeKind bridge = BridgeKind.None, bool curve = false)
        {
            var a = nodeList[n0];
            var b = nodeList[n1];
            segments.Add(new MasterRoadSegment(
                segments.Count, name, a.X, a.Y, b.X, b.Y,
                DefaultHalfWidth, DefaultSidewalkOffset, n0, n1, bridge, curve, TwoWay: true));
        }

        // Sim Lane Loop
        Seg("SimLane_W",         nSimNwS, nSimSwN);
        Seg("SimLane_Curve_NW",  nSimNwS, nSimNwE, curve: true);
        Seg("SimLane_N",         nSimNwE, nSimNeW);
        Seg("SimLane_Curve_NE",  nSimNeW, nSimNeS, curve: true);
        Seg("SimLane_E",         nSimNeS, nSimSe);
        Seg("SimLane_S_East",    nSimSe,  nSimOtT);
        Seg("SimLane_S_West",    nSimOtT, nSimSwE);
        Seg("SimLane_Curve_SW",  nSimSwE, nSimSwN, curve: true);

        // Main Coastal Arterial (H_MAIN)
        Seg("H_Main_Portal",     nPortalNw, nMainW);
        Seg("H_Main_West",       nMainW,    nMainCw);
        Seg("H_Main_Center",     nMainCw,   nMainCe);
        Seg("H_Main_ToBridge",   nMainCe,   nStoneBrW);
        Seg("H_Main_StoneBridge",nStoneBrW, nStoneBrE, bridge: BridgeKind.StoneArchBridge);
        Seg("H_Main_East1",      nStoneBrE, nMainE1);
        Seg("H_Main_East2",      nMainE1,   nMainE2);

        // East Peninsula Turnaround Loop
        Seg("Pen_N",             nMainE2, nPenNe);
        Seg("Pen_Curve_NE",      nPenNe,  nPenE1,   curve: true);
        Seg("Pen_E",             nPenE1,  nPenE2,   curve: true);
        Seg("Pen_Curve_SE",      nPenE2,  nPenSe,   curve: true);
        Seg("Pen_S",             nPenSe,  nEast2Lp);

        // Vertical Avenues
        Seg("V_OtW_North",       nPortalN,  nMainW);
        Seg("V_OtW_Mid",         nMainW,    nMidW);
        Seg("V_OtW_South",       nMidW,     nSwCurveN);

        Seg("V_OtCw_North",      nOtCwN,    nMainCw);
        Seg("V_OtCw_Mid",        nMainCw,   nMidCw);

        Seg("V_OtCe_North",      nSimOtT,   nSmallBrN);
        Seg("V_OtCe_SmallBridge",nSmallBrN, nSmallBrS, bridge: BridgeKind.SmallBridge);
        Seg("V_OtCe_ToMain",     nSmallBrS, nMainCe);
        Seg("V_OtCe_Mid",        nMainCe,   nMidCe);

        Seg("V_OtCsw_Mid",       nMidCsw,   nSoCsw);

        // Old Town Mid-West & Cul-de-Sac
        Seg("H_OtMid_W",         nMidW,     nMidCw);
        Seg("H_OtMid_C",         nMidCw,    nMidCsw);
        Seg("H_OtMid_E",         nMidCsw,   nMidCe);
        Seg("H_OtMid_Cul",       nMidCe,    nMidCul);

        // Southern Coastal Highway & Truss Bridge
        Seg("SW_Curve_1",        nSwCurveN, nSwCurveM, curve: true);
        Seg("SW_Curve_2",        nSwCurveM, nSwCurveE, curve: true);
        Seg("H_SoW_West",        nSwCurveE, nSoCsw);
        Seg("H_SoW_East",        nSoCsw,    nTrussW);
        Seg("H_So_TrussBridge",  nTrussW,   nTrussE,   bridge: BridgeKind.TrussBridge);
        Seg("H_SoE_West",        nTrussE,   nSoE1);
        Seg("H_SoE_East",        nSoE1,     nSeCurveW);
        Seg("SE_Curve",          nSeCurveW, nSeCurveN, curve: true);

        // Old Town East Verticals & Cross Street
        Seg("V_East1_North",     nEast1N,   nMainE1);
        Seg("V_East1_Mid",       nMainE1,   nOtEMd1);
        Seg("V_East1_South",     nOtEMd1,   nSoE1);

        Seg("V_East2_North",     nEast2N,   nMainE2);
        Seg("V_East2_Loop",      nMainE2,   nEast2Lp);
        Seg("V_East2_Mid",       nEast2Lp,  nOtEMd2);
        Seg("V_East2_South",     nOtEMd2,   nSeCurveN);

        Seg("H_OtEast_Mid",      nOtEMd1,   nOtEMd2);

        // 3. Build RoadIntersectionNode list + quadratic Bezier LaneTurnConnections
        var segsByNode = new Dictionary<int, List<int>>(nodeList.Count);
        for (int i = 0; i < nodeList.Count; i++) segsByNode[i] = [];
        foreach (var s in segments)
        {
            segsByNode[s.StartNodeId].Add(s.Id);
            segsByNode[s.EndNodeId].Add(s.Id);
        }

        var intersections = new List<RoadIntersectionNode>(nodeList.Count);
        for (int i = 0; i < nodeList.Count; i++)
        {
            var (_, nx, ny, kind) = nodeList[i];
            var conn = segsByNode[i];
            var turns = BuildLaneTurnConnections(i, nx, ny, kind, conn, segments);
            intersections.Add(new RoadIntersectionNode(i, nx, ny, kind, conn, turns));
        }

        // 4. Build SidewalkNode / SidewalkEdge graph & insert all 41 lots' frontage nodes
        var (swNodes, swEdges, lotSwMap, segEndpoints, segChains) = BuildSidewalkGraph(intersections, segments);

        return new MasterRoadNetwork(segments, intersections, swNodes, swEdges, lotSwMap, segEndpoints, segChains);
    }

    private static List<LaneTurnConnection> BuildLaneTurnConnections(
        int nodeId,
        float nx,
        float ny,
        RoadNodeKind kind,
        List<int> connSegIds,
        List<MasterRoadSegment> segments)
    {
        var turns = new List<LaneTurnConnection>();
        (float Dx, float Dy) OutDir(MasterRoadSegment seg)
        {
            float len = MathF.Max(seg.Length, 1e-4f);
            return seg.StartNodeId == nodeId
                ? ((seg.EndX - seg.StartX) / len, (seg.EndY - seg.StartY) / len)
                : ((seg.StartX - seg.EndX) / len, (seg.StartY - seg.EndY) / len);
        }

        foreach (int inId in connSegIds)
        {
            var segIn = segments[inId];
            var outFromNodeIn = OutDir(segIn);
            (float Dx, float Dy) inDir = (-outFromNodeIn.Dx, -outFromNodeIn.Dy); // pointing into node
            (float Dx, float Dy) inRight = (-inDir.Dy, inDir.Dx);
            float entryPull = MathF.Min(segIn.Length * 0.4f, segIn.HalfWidth + 1.5f);
            float entryX = nx - inDir.Dx * entryPull + inRight.Dx * DefaultLaneOffset;
            float entryY = ny - inDir.Dy * entryPull + inRight.Dy * DefaultLaneOffset;

            if (kind == RoadNodeKind.CulDeSac && connSegIds.Count == 1)
            {
                float exitX = nx - inDir.Dx * entryPull - inRight.Dx * DefaultLaneOffset;
                float exitY = ny - inDir.Dy * entryPull - inRight.Dy * DefaultLaneOffset;
                turns.Add(new LaneTurnConnection(inId, inId, TurnKind.UTurn, entryX, entryY, nx, ny, exitX, exitY));
                continue;
            }

            foreach (int outId in connSegIds)
            {
                if (outId == inId) continue;
                var segOut = segments[outId];
                (float Dx, float Dy) outDir = OutDir(segOut);
                (float Dx, float Dy) outRight = (-outDir.Dy, outDir.Dx);
                float exitPush = MathF.Min(segOut.Length * 0.4f, segOut.HalfWidth + 1.5f);
                float exitX = nx + outDir.Dx * exitPush + outRight.Dx * DefaultLaneOffset;
                float exitY = ny + outDir.Dy * exitPush + outRight.Dy * DefaultLaneOffset;

                float dot = inDir.Dx * outDir.Dx + inDir.Dy * outDir.Dy;
                float cross = inDir.Dx * outDir.Dy - inDir.Dy * outDir.Dx;
                TurnKind tk = kind == RoadNodeKind.CornerCurve
                    ? TurnKind.Curve
                    : dot > 0.7f ? TurnKind.Straight
                    : dot < -0.7f ? TurnKind.UTurn
                    : cross > 0f ? TurnKind.RightTurn : TurnKind.LeftTurn;

                // Control point = intersection of incoming and outgoing lane rays (or midpoint if parallel)
                float denom = inDir.Dx * outDir.Dy - inDir.Dy * outDir.Dx;
                float ctrlX, ctrlY;
                if (MathF.Abs(denom) > 0.05f)
                {
                    float t = ((exitX - entryX) * outDir.Dy - (exitY - entryY) * outDir.Dx) / denom;
                    ctrlX = entryX + inDir.Dx * t;
                    ctrlY = entryY + inDir.Dy * t;
                }
                else
                {
                    ctrlX = (entryX + exitX) * 0.5f;
                    ctrlY = (entryY + exitY) * 0.5f;
                }

                turns.Add(new LaneTurnConnection(inId, outId, tk, entryX, entryY, ctrlX, ctrlY, exitX, exitY));
            }
        }
        return turns;
    }

    private static (
        List<SidewalkNode> Nodes,
        List<SidewalkEdge> Edges,
        Dictionary<short, IReadOnlyList<int>> LotNodes,
        Dictionary<int, (int, int, int, int)> SegEndpoints,
        List<(int SegId, int SideSign, List<int> Ordered)> SegChains)
        BuildSidewalkGraph(List<RoadIntersectionNode> intersections, List<MasterRoadSegment> segments)
    {
        var nodes = new List<SidewalkNode>(256);
        var edges = new List<SidewalkEdge>(320);

        int AddNode(float x, float y, short? house = null)
        {
            // Reuse nearby non-lot corner node within 0.35 tiles so perpendicular sidewalks share exact vertices
            if (house is null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].LotHouse is not null) continue;
                    float dx = nodes[i].WorldX - x;
                    float dy = nodes[i].WorldY - y;
                    if (dx * dx + dy * dy <= 0.35f * 0.35f)
                        return nodes[i].Id;
                }
            }
            int id = nodes.Count;
            nodes.Add(new SidewalkNode(id, x, y, house));
            return id;
        }

        void AddEdge(int a, int b, SidewalkEdgeKind kind, int? segId = null)
        {
            if (a == b) return;
            var na = nodes[a];
            var nb = nodes[b];
            float len = MathF.Sqrt((nb.WorldX - na.WorldX) * (nb.WorldX - na.WorldX) + (nb.WorldY - na.WorldY) * (nb.WorldY - na.WorldY));
            edges.Add(new SidewalkEdge(a, b, len, kind, segId));
        }

        // Helper: setback along segment at each node kind so sidewalks meet cleanly at intersection corners
        static float NodeSetback(RoadIntersectionNode node) => node.Kind switch
        {
            RoadNodeKind.FourWay or RoadNodeKind.TJunction => DefaultSidewalkOffset,
            _ => 0f,
        };

        var segEndpoints = new Dictionary<int, (int, int, int, int)>(segments.Count);
        // Track per-segment side track endpoints so we can split-insert lot frontage nodes
        var trackEnds = new List<(int SegId, int SideSign, int StartNodeId, int EndNodeId, SidewalkEdgeKind Kind)>(segments.Count * 2);

        // Compute corner sidewalk vertices at every node
        var nodeLeftRightBySeg = new Dictionary<(int NodeId, int SegId), (int LeftNode, int RightNode)>();
        foreach (var seg in segments)
        {
            float len = MathF.Max(seg.Length, 1e-4f);
            float tx = (seg.EndX - seg.StartX) / len;
            float ty = (seg.EndY - seg.StartY) / len;
            float nx = -ty; // right-hand normal in screen coords
            float ny = tx;

            var n0 = intersections[seg.StartNodeId];
            var n1 = intersections[seg.EndNodeId];
            float sb0 = NodeSetback(n0);
            float sb1 = NodeSetback(n1);

            float s0x = n0.WorldX + tx * sb0;
            float s0y = n0.WorldY + ty * sb0;
            float s1x = n1.WorldX - tx * sb1;
            float s1y = n1.WorldY - ty * sb1;

            int pLeft = AddNode(s0x + nx * seg.SidewalkOffset, s0y + ny * seg.SidewalkOffset);
            int pRight = AddNode(s0x - nx * seg.SidewalkOffset, s0y - ny * seg.SidewalkOffset);
            int qLeft = AddNode(s1x + nx * seg.SidewalkOffset, s1y + ny * seg.SidewalkOffset);
            int qRight = AddNode(s1x - nx * seg.SidewalkOffset, s1y - ny * seg.SidewalkOffset);

            nodeLeftRightBySeg[(n0.Id, seg.Id)] = (pLeft, pRight);
            nodeLeftRightBySeg[(n1.Id, seg.Id)] = (qLeft, qRight);

            segEndpoints[seg.Id] = (pLeft, pRight, qLeft, qRight);
            var kind = seg.IsBridge ? SidewalkEdgeKind.BridgeWalkway : SidewalkEdgeKind.Sidewalk;
            trackEnds.Add((seg.Id, +1, pLeft, qLeft, kind));
            trackEnds.Add((seg.Id, -1, pRight, qRight, kind));
        }

        // Connect crosswalks & corners around every intersection node
        foreach (var node in intersections)
        {
            if (node.Kind == RoadNodeKind.FourWay)
            {
                int nw = AddNode(node.WorldX - DefaultSidewalkOffset, node.WorldY - DefaultSidewalkOffset);
                int ne = AddNode(node.WorldX + DefaultSidewalkOffset, node.WorldY - DefaultSidewalkOffset);
                int se = AddNode(node.WorldX + DefaultSidewalkOffset, node.WorldY + DefaultSidewalkOffset);
                int sw = AddNode(node.WorldX - DefaultSidewalkOffset, node.WorldY + DefaultSidewalkOffset);
                AddEdge(nw, ne, SidewalkEdgeKind.Crosswalk);
                AddEdge(ne, se, SidewalkEdgeKind.Crosswalk);
                AddEdge(se, sw, SidewalkEdgeKind.Crosswalk);
                AddEdge(sw, nw, SidewalkEdgeKind.Crosswalk);
            }
            else if (node.Kind == RoadNodeKind.TJunction)
            {
                // Identify which of the 4 cardinal directions (N,E,S,W) have segments
                bool hasE = false, hasW = false, hasN = false, hasS = false;
                foreach (int sid in node.ConnectedSegmentIds)
                {
                    var seg = segments[sid];
                    float ox = seg.StartNodeId == node.Id ? seg.EndX - seg.StartX : seg.StartX - seg.EndX;
                    float oy = seg.StartNodeId == node.Id ? seg.EndY - seg.StartY : seg.StartY - seg.EndY;
                    if (MathF.Abs(ox) >= MathF.Abs(oy))
                    {
                        if (ox > 0) hasE = true; else hasW = true;
                    }
                    else
                    {
                        if (oy > 0) hasS = true; else hasN = true;
                    }
                }
                int nw = AddNode(node.WorldX - DefaultSidewalkOffset, node.WorldY - DefaultSidewalkOffset);
                int ne = AddNode(node.WorldX + DefaultSidewalkOffset, node.WorldY - DefaultSidewalkOffset);
                int se = AddNode(node.WorldX + DefaultSidewalkOffset, node.WorldY + DefaultSidewalkOffset);
                int sw = AddNode(node.WorldX - DefaultSidewalkOffset, node.WorldY + DefaultSidewalkOffset);
                AddEdge(nw, ne, hasN ? SidewalkEdgeKind.Crosswalk : SidewalkEdgeKind.Sidewalk);
                AddEdge(ne, se, hasE ? SidewalkEdgeKind.Crosswalk : SidewalkEdgeKind.Sidewalk);
                AddEdge(se, sw, hasS ? SidewalkEdgeKind.Crosswalk : SidewalkEdgeKind.Sidewalk);
                AddEdge(sw, nw, hasW ? SidewalkEdgeKind.Crosswalk : SidewalkEdgeKind.Sidewalk);
            }
            else if (node.Kind == RoadNodeKind.CornerCurve && node.ConnectedSegmentIds.Count == 2)
            {
                var (aL, aR) = nodeLeftRightBySeg[(node.Id, node.ConnectedSegmentIds[0])];
                var (bL, bR) = nodeLeftRightBySeg[(node.Id, node.ConnectedSegmentIds[1])];
                // Connect the closer pairs across the corner (left-to-left or left-to-right depending on segment orientation)
                float dLL = DistSq(nodes[aL], nodes[bL]) + DistSq(nodes[aR], nodes[bR]);
                float dLR = DistSq(nodes[aL], nodes[bR]) + DistSq(nodes[aR], nodes[bL]);
                if (dLL <= dLR)
                {
                    AddEdge(aL, bL, SidewalkEdgeKind.CornerConnector);
                    AddEdge(aR, bR, SidewalkEdgeKind.CornerConnector);
                }
                else
                {
                    AddEdge(aL, bR, SidewalkEdgeKind.CornerConnector);
                    AddEdge(aR, bL, SidewalkEdgeKind.CornerConnector);
                }
                AddEdge(aL, aR, SidewalkEdgeKind.Crosswalk);
            }
            else if (node.Kind == RoadNodeKind.CulDeSac && node.ConnectedSegmentIds.Count == 1)
            {
                var (l, r) = nodeLeftRightBySeg[(node.Id, node.ConnectedSegmentIds[0])];
                AddEdge(l, r, SidewalkEdgeKind.Crosswalk);
            }
            else if (node.Kind == RoadNodeKind.PassThrough && node.ConnectedSegmentIds.Count == 2)
            {
                var (aL, aR) = nodeLeftRightBySeg[(node.Id, node.ConnectedSegmentIds[0])];
                var (bL, bR) = nodeLeftRightBySeg[(node.Id, node.ConnectedSegmentIds[1])];
                float dLL = DistSq(nodes[aL], nodes[bL]) + DistSq(nodes[aR], nodes[bR]);
                float dLR = DistSq(nodes[aL], nodes[bR]) + DistSq(nodes[aR], nodes[bL]);
                if (dLL <= dLR)
                {
                    AddEdge(aL, bL, SidewalkEdgeKind.CornerConnector);
                    AddEdge(aR, bR, SidewalkEdgeKind.CornerConnector);
                }
                else
                {
                    AddEdge(aL, bR, SidewalkEdgeKind.CornerConnector);
                    AddEdge(aR, bL, SidewalkEdgeKind.CornerConnector);
                }
            }
        }

        // Attach each of the 41 blueprint parcels' sidewalk frontage node(s) onto the matching sidewalk track
        var trackSplits = new Dictionary<int, List<(float T, int NodeId)>>(trackEnds.Count);
        for (int i = 0; i < trackEnds.Count; i++) trackSplits[i] = [];

        var lotSwMap = new Dictionary<short, IReadOnlyList<int>>(41);
        foreach (var parcel in NeighborhoodWorldBlueprint.Default.Parcels.Values.OrderBy(p => p.House))
        {
            var lotNodeIds = new List<int>(2);
            void AttachEdge(int edge)
            {
                float c = parcel.GetRoadCenterLocal(edge);
                float swMidX = edge switch
                {
                    1 => parcel.WorldTileX + c - DefaultSidewalkOffset,
                    3 => parcel.WorldTileX + c + DefaultSidewalkOffset,
                    _ => parcel.WorldTileX + parcel.LotSizeTiles * 0.5f,
                };
                float swMidY = edge switch
                {
                    0 => parcel.WorldTileY + c + DefaultSidewalkOffset,
                    2 => parcel.WorldTileY + c - DefaultSidewalkOffset,
                    _ => parcel.WorldTileY + parcel.LotSizeTiles * 0.5f,
                };

                // Find closest sidewalk track
                int bestTrack = -1;
                float bestDistSq = float.MaxValue;
                float bestT = 0f;
                float bestX = swMidX, bestY = swMidY;
                for (int ti = 0; ti < trackEnds.Count; ti++)
                {
                    var tr = trackEnds[ti];
                    var na = nodes[tr.StartNodeId];
                    var nb = nodes[tr.EndNodeId];
                    float dx = nb.WorldX - na.WorldX;
                    float dy = nb.WorldY - na.WorldY;
                    float lenSq = dx * dx + dy * dy;
                    if (lenSq < 1e-4f) continue;
                    float t = Math.Clamp(((swMidX - na.WorldX) * dx + (swMidY - na.WorldY) * dy) / lenSq, 0f, 1f);
                    float px = na.WorldX + t * dx;
                    float py = na.WorldY + t * dy;
                    float dSq = (swMidX - px) * (swMidX - px) + (swMidY - py) * (swMidY - py);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestTrack = ti;
                        bestT = t;
                        bestX = px;
                        bestY = py;
                    }
                }

                if (bestTrack >= 0)
                {
                    int lotNodeId = AddNode(bestX, bestY, parcel.House);
                    lotNodeIds.Add(lotNodeId);
                    trackSplits[bestTrack].Add((bestT, lotNodeId));
                }
            }

            AttachEdge(parcel.PrimaryRoadEdge);
            if (parcel.SecondaryRoadEdge is int secEdge)
                AttachEdge(secEdge);
            lotSwMap[parcel.House] = lotNodeIds;
        }

        // Emit split sidewalk edges along every track
        var segChains = new List<(int SegId, int SideSign, List<int> Ordered)>(trackEnds.Count);
        for (int ti = 0; ti < trackEnds.Count; ti++)
        {
            var tr = trackEnds[ti];
            var splits = trackSplits[ti];
            splits.Sort((a, b) => a.T.CompareTo(b.T));
            var ordered = new List<int>(splits.Count + 2) { tr.StartNodeId };
            foreach (var (_, nid) in splits)
                ordered.Add(nid);
            ordered.Add(tr.EndNodeId);
            for (int k = 0; k < ordered.Count - 1; k++)
                AddEdge(ordered[k], ordered[k + 1], tr.Kind, tr.SegId);
            segChains.Add((tr.SegId, tr.SideSign, ordered));
        }

        return (nodes, edges, lotSwMap, segEndpoints, segChains);

        static float DistSq(SidewalkNode a, SidewalkNode b) =>
            (a.WorldX - b.WorldX) * (a.WorldX - b.WorldX) + (a.WorldY - b.WorldY) * (a.WorldY - b.WorldY);
    }
}
