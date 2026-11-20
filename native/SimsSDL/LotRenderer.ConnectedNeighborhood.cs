using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using SimsEngine.Simulation.Neighborhood;
using SimsSDL.Core;

namespace SimsSDL;

public sealed partial class LotRenderer
{
    public sealed record AdjacentLotDescriptor(
        short House,
        int TileOffsetX,
        int TileOffsetY,
        string LotName,
        string? FamilyName,
        bool Occupied,
        bool HasDoor,
        bool IsLive,
        string ScenePath,
        long Revision,
        bool IsCommunity = false);

    public sealed record RoadStripDescriptor(
        int MinTileX,
        int MinTileY,
        int MaxTileX,
        int MaxTileY,
        bool Horizontal,
        bool IsIntersection);

    public sealed record AdjacentLotPick(
        short House,
        string LotName,
        string? FamilyName,
        bool Occupied,
        bool HasDoor,
        int? ObjectID,
        GroundTile? Tile,
        bool IsCommunity = false);

    public sealed record WorldTreeAssetDescriptor(
        string Id,
        string FilePath,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    public sealed record WorldVehicleAssetDescriptor(
        string Id,
        int Direction,
        string FilePath,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    private sealed record UploadedWorldTree(
        string Id,
        GPUTexture Texture,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    private sealed record UploadedVehicleView(
        GPUTexture Texture,
        int Width,
        int Height,
        float AnchorX,
        float AnchorY);

    private sealed record UploadedWorldVehicle(
        string Id,
        Dictionary<int, UploadedVehicleView> Views);

    private sealed class AmbientTrafficVehicle
    {
        public int RoadIndex { get; set; }
        public float SpanPos { get; set; }
        public int TravelSign { get; set; }
        public float SpeedTilesPerSec { get; set; }
        public int VehicleAssetIndex { get; set; }
    }

    private sealed class CachedAdjacentLot : IDisposable
    {
        public short House { get; }
        public int TileOffsetX { get; set; }
        public int TileOffsetY { get; set; }
        public string LotName { get; set; }
        public string? FamilyName { get; set; }
        public bool Occupied { get; set; }
        public bool HasDoor { get; set; }
        public bool IsCommunity { get; set; }
        public string LoadedScenePath { get; set; }
        public long LoadedRevision { get; set; }
        public LotRenderer Renderer { get; }

        public CachedAdjacentLot(
            short house,
            int tileOffsetX,
            int tileOffsetY,
            string lotName,
            string? familyName,
            bool occupied,
            bool hasDoor,
            bool isCommunity,
            string loadedScenePath,
            long loadedRevision,
            LotRenderer renderer)
        {
            House = house;
            TileOffsetX = tileOffsetX;
            TileOffsetY = tileOffsetY;
            LotName = lotName;
            FamilyName = familyName;
            Occupied = occupied;
            HasDoor = hasDoor;
            IsCommunity = isCommunity;
            LoadedScenePath = loadedScenePath;
            LoadedRevision = loadedRevision;
            Renderer = renderer;
        }

        public void Dispose() => Renderer.Dispose();
    }

    private readonly struct LotHeightSource
    {
        public readonly LotScene Scene;
        public readonly int Width;
        public readonly int Height;
        public readonly float Dcx;
        public readonly float Dcy;
        public readonly float BaseHeight;
        public readonly float StreetBaseH;
        public readonly float ClipMinX;
        public readonly float ClipMinY;
        public readonly float ClipMaxX;
        public readonly float ClipMaxY;
        public readonly IReadOnlyList<short> Heights;

        public LotHeightSource(
            LotScene scene,
            int width,
            int height,
            float dcx,
            float dcy,
            IReadOnlyList<short> heights,
            float streetBaseH)
        {
            Scene = scene;
            Width = width;
            Height = height;
            Dcx = dcx;
            Dcy = dcy;
            Heights = heights;
            BaseHeight = heights.Count > 0 ? heights[0] : 0f;
            StreetBaseH = streetBaseH;

            float cMinX = 0f, cMinY = 0f, cMaxX = width - 1, cMaxY = height - 1;
            if (scene.House > 0 && NeighborhoodWorldBlueprint.Default.Parcels.TryGetValue((short)scene.House, out var parcel))
            {
                void Clip(int edge)
                {
                    float c = parcel.GetRoadCenterLocal(edge);
                    switch (edge)
                    {
                        case 0: cMinY = MathF.Max(cMinY, c); break;
                        case 2: cMaxY = MathF.Min(cMaxY, c); break;
                        case 3: cMinX = MathF.Max(cMinX, c); break;
                        case 1: cMaxX = MathF.Min(cMaxX, c); break;
                    }
                }
                Clip(parcel.PrimaryRoadEdge);
                if (parcel.SecondaryRoadEdge is int sec)
                    Clip(sec);
            }
            ClipMinX = cMinX;
            ClipMinY = cMinY;
            ClipMaxX = cMaxX;
            ClipMaxY = cMaxY;
        }
    }

    private readonly List<CachedAdjacentLot> _adjacentLots = [];
    private IReadOnlyList<RoadStripDescriptor> _neighborhoodRoadStrips = [];
    private Vector4? _neighborhoodPanTileBounds;
    private int _worldOriginX;
    private int _worldOriginY;
    private int _focusedRoadEdge = 2;
    private float _focusedRoadCenter = 46.5f;
    private int _focusedSecondaryRoadEdge = -1;
    private float _focusedSecondaryRoadCenter;

    private struct MasterRoadSegment
    {
        public int Id;
        public float StartX;
        public float StartY;
        public float EndX;
        public float EndY;
        public float HalfW;
        public float SidewalkOffset;
        public bool IsBridge;
        public bool IsLoopCurve;
        public int StartNodeId;
        public int EndNodeId;
        public readonly bool Horizontal => MathF.Abs(EndY - StartY) < 0.01f;
        public readonly float Length => MathF.Sqrt((EndX - StartX) * (EndX - StartX) + (EndY - StartY) * (EndY - StartY));
    }
    private readonly record struct ShiftedIntersectionNode(
        int Id,
        float X,
        float Y,
        RoadNodeKind Kind,
        IReadOnlyList<int> ConnectedSegmentIds);
    private readonly List<ShiftedIntersectionNode> _masterIntersections = [];
    private readonly record struct GreenbeltTreeInstance(
        float SceneX,
        float SceneY,
        float Width,
        float Height,
        int TextureIndex,
        bool Flip,
        float DepthKey);

    private readonly List<GreenbeltTreeInstance> _greenbeltTrees = [];
    private readonly Dictionary<string, GPUTexture> _worldAssetTextureCache = new(StringComparer.Ordinal);
    private readonly List<UploadedWorldTree> _worldTreeAssets = [];
    private readonly List<UploadedWorldVehicle> _worldVehicleAssets = [];
    private readonly List<MasterRoadSegment> _masterRoadSegments = [];
    private readonly List<AmbientTrafficVehicle> _trafficVehicles = [];
    private float _masterRoadBaseAlt;
    private long _lastTrafficTickTimestamp;
    private GPUBuffer? _neighborhoodStreetBuffer;
    private int _neighborhoodStreetCamera = -1;
    private long _neighborhoodStreetSignature;

    private short? _pendingCenterOnHouse;

    public bool HasAdjacentNeighborhood => _adjacentLots.Count > 0;
    public int AdjacentLotCount => _adjacentLots.Count;
    public short AdjacentLotsForHouse { get; private set; }

    public void RequestCenterOnHouse(short house) => _pendingCenterOnHouse = house;

    public bool HasAdjacentHouse(short house) =>
        _adjacentLots.Any(entry => entry.House == house);

    public bool IsAdjacentHouseNearCamera(short house, Vector2 canvas, float maxTileDistance = 120f)
    {
        CachedAdjacentLot? entry = null;
        for (int i = 0; i < _adjacentLots.Count; i++)
        {
            if (_adjacentLots[i].House == house)
            {
                entry = _adjacentLots[i];
                break;
            }
        }
        if (entry is null)
            return false;

        float ox = entry.TileOffsetX;
        float oy = entry.TileOffsetY;
        if (ox * ox + oy * oy <= maxTileDistance * maxTileDistance)
            return true;

        if (TryGetCameraCenterLotTile(canvas, out int camTx, out int camTy))
        {
            float cdx = camTx - (ox + 32f);
            float cdy = camTy - (oy + 32f);
            if (cdx * cdx + cdy * cdy <= 96f * 96f)
                return true;
        }
        return false;
    }

    public bool TryGetCameraCenterLotTile(Vector2 canvas, out int tileX, out int tileY)
    {
        tileX = 32;
        tileY = 32;
        if (Scene is not { } scene || Viewport(canvas) is not { } viewport ||
            scene.OriginX is not float ox || scene.OriginY is not float oy)
        {
            return false;
        }

        Vector2 screenCenter = new(viewport.Size.X * 0.5f, viewport.Size.Y * 0.5f);
        if (viewport.ScenePoint(screenCenter) is not { } scenePt)
            return false;

        Vector2 lotSize = new(scene.LotWidth ?? 64, scene.LotHeight ?? 64);
        Vector2 projected = scenePt + new Vector2(ox, oy) - new Vector2(34f, 158f);
        Vector2 camPt = new(projected.Y / 32f + projected.X / 64f, projected.Y / 32f - projected.X / 64f);
        Vector2 lotPt = scene.Camera.LotPoint(camPt, lotSize);
        tileX = (int)MathF.Round(lotPt.X);
        tileY = (int)MathF.Round(lotPt.Y);
        return true;
    }

    private void ApplySeamlessConnectedLotTransition(LotScene oldScene, LotScene newScene)
    {
        bool forceCenter = _pendingCenterOnHouse == newScene.House;
        _pendingCenterOnHouse = null;
        int matchIdx = -1;
        if (!forceCenter)
        {
            for (int i = 0; i < _adjacentLots.Count; i++)
            {
                if (_adjacentLots[i].House == newScene.House)
                {
                    matchIdx = i;
                    break;
                }
            }
        }
        if (matchIdx < 0)
        {
            ClearAdjacentNeighborhood();
            _pan = Vector2.Zero;
            return;
        }

        var target = _adjacentLots[matchIdx];
        int dx = target.TileOffsetX;
        int dy = target.TileOffsetY;
        float scale = MathF.Max(UIScale, 0.01f) * _visualZoomFactor;

        Vector2 oldSize = new(oldScene.LotWidth ?? 64, oldScene.LotHeight ?? 64);
        Vector2 newSize = new(newScene.LotWidth ?? 64, newScene.LotHeight ?? 64);
        Vector2 focPt = oldScene.Camera.ProjectedPoint(new Vector2(dx, dy), oldSize);
        Vector2 adjP0 = newScene.Camera.ProjectedPoint(Vector2.Zero, newSize);
        Vector2 sceneDelta = focPt - adjP0;

        float streetAltDelta = (ComputeLotStreetHeightOffset(newScene) - ComputeLotStreetHeightOffset(oldScene)) * 7.5f;
        Vector2 originAlign = new(
            (newScene.OriginX ?? 0f) - (oldScene.OriginX ?? 0f),
            (newScene.OriginY ?? 0f) - (oldScene.OriginY ?? 0f) + streetAltDelta);
        Vector2 halfSizeDelta = new Vector2(newScene.Width - oldScene.Width, newScene.Height - oldScene.Height) * 0.5f;
        Vector2 panDelta = (sceneDelta + originAlign + halfSizeDelta) * scale;

        _worldOriginX += dx;
        _worldOriginY += dy;
        if (_neighborhoodPanTileBounds is { } b)
            _neighborhoodPanTileBounds = new Vector4(b.X - dx, b.Y - dy, b.Z - dx, b.W - dy);

        for (int i = 0; i < _adjacentLots.Count; i++)
        {
            if (i == matchIdx) continue;
            _adjacentLots[i].TileOffsetX -= dx;
            _adjacentLots[i].TileOffsetY -= dy;
        }
        target.Dispose();
        _adjacentLots.RemoveAt(matchIdx);

        _neighborhoodStreetCamera = -1;
        _pan = SimsSDL.Core.SceneViewport.ClampedPan(_pan + panDelta, newScene, scale, _neighborhoodPanTileBounds);
    }

    public void QueueAdjacentAnimation(short house, SceneAnimation frame)
    {
        foreach (var entry in _adjacentLots)
        {
            if (entry.House == house)
            {
                entry.Renderer.QueueAnimation(frame);
                return;
            }
        }
    }

    private void ApplyAdjacentPendingAnimations(nint command)
    {
        if (_adjacentLots.Count == 0)
            return;
        RebuildNeighborhoodStreetBuffer();
        TickAmbientTrafficVehicles();
        foreach (var entry in _adjacentLots)
        {
            try
            {
                entry.Renderer.ApplyPendingAnimation(command);
            }
            catch
            {
                // Ignore transient animation mismatches while a neighbor lot is switching revisions
            }
        }
    }

    public void SetAdjacentNeighborhood(
        IReadOnlyList<AdjacentLotDescriptor> lots,
        IReadOnlyList<RoadStripDescriptor> roadStrips,
        Vector4? panTileBounds,
        int focusedRoadEdge = 2,
        float focusedRoadCenter = 46.5f,
        int focusedSecondaryRoadEdge = -1,
        float focusedSecondaryRoadCenter = 0f,
        int worldOriginX = 0,
        int worldOriginY = 0,
        IReadOnlyList<WorldVehicleAssetDescriptor>? trafficSprites = null,
        IReadOnlyList<WorldTreeAssetDescriptor>? treeSprites = null,
        short focusedHouse = 0)
    {
        if (lots is null || lots.Count == 0)
        {
            ClearAdjacentNeighborhood();
            return;
        }

        var existingByHouse = _adjacentLots.ToDictionary(entry => entry.House);
        var nextList = new List<CachedAdjacentLot>(lots.Count);
        var keptHouses = new HashSet<short>();

        foreach (var desc in lots)
        {
            if (string.IsNullOrWhiteSpace(desc.ScenePath))
                continue;

            keptHouses.Add(desc.House);
            if (existingByHouse.TryGetValue(desc.House, out var existing))
            {
                existing.TileOffsetX = desc.TileOffsetX;
                existing.TileOffsetY = desc.TileOffsetY;
                existing.LotName = desc.LotName;
                existing.FamilyName = desc.FamilyName;
                existing.Occupied = desc.Occupied;
                existing.HasDoor = desc.HasDoor;
                existing.IsCommunity = desc.IsCommunity;
                if (!string.Equals(existing.LoadedScenePath, desc.ScenePath, StringComparison.Ordinal) ||
                    existing.LoadedRevision != desc.Revision)
                {
                    try
                    {
                        existing.Renderer.LoadScene(desc.ScenePath, desc.Revision);
                        existing.LoadedScenePath = desc.ScenePath;
                        existing.LoadedRevision = desc.Revision;
                    }
                    catch
                    {
                        continue;
                    }
                }
                nextList.Add(existing);
            }
            else
            {
                var sub = new LotRenderer(GPU)
                {
                    LiveMode = LiveMode,
                    UIScale = UIScale,
                    AmbientEnabled = false,
                    AntiAliasedSprites = AntiAliasedSprites,
                };
                try
                {
                    sub.LoadScene(desc.ScenePath, desc.Revision);
                    nextList.Add(new CachedAdjacentLot(
                        desc.House,
                        desc.TileOffsetX,
                        desc.TileOffsetY,
                        desc.LotName,
                        desc.FamilyName,
                        desc.Occupied,
                        desc.HasDoor,
                        desc.IsCommunity,
                        desc.ScenePath,
                        desc.Revision,
                        sub));
                }
                catch
                {
                    sub.Dispose();
                }
            }
        }

        foreach (var old in _adjacentLots)
        {
            if (!keptHouses.Contains(old.House))
                old.Dispose();
        }

        if (treeSprites is { Count: > 0 })
        {
            _worldTreeAssets.Clear();
            foreach (var ts in treeSprites)
            {
                if (TryLoadWorldAssetTexture(ts.FilePath, ts.Width, ts.Height) is { } tex)
                {
                    _worldTreeAssets.Add(new UploadedWorldTree(ts.Id, tex, ts.Width, ts.Height, ts.AnchorX, ts.AnchorY));
                }
            }
        }

        if (trafficSprites is { Count: > 0 })
        {
            var byId = new Dictionary<string, Dictionary<int, UploadedVehicleView>>(StringComparer.OrdinalIgnoreCase);
            foreach (var vs in trafficSprites)
            {
                if (TryLoadWorldAssetTexture(vs.FilePath, vs.Width, vs.Height) is not { } tex)
                    continue;
                if (!byId.TryGetValue(vs.Id, out var views))
                {
                    views = new Dictionary<int, UploadedVehicleView>(4);
                    byId[vs.Id] = views;
                }
                views[vs.Direction] = new UploadedVehicleView(tex, vs.Width, vs.Height, vs.AnchorX, vs.AnchorY);
            }
            if (byId.Count > 0)
            {
                _worldVehicleAssets.Clear();
                foreach (var (id, views) in byId)
                {
                    if (views.Count > 0)
                        _worldVehicleAssets.Add(new UploadedWorldVehicle(id, views));
                }
            }
        }

        _adjacentLots.Clear();
        _adjacentLots.AddRange(nextList);
        AdjacentLotsForHouse = nextList.Count > 0 ? focusedHouse : (short)0;
        _neighborhoodRoadStrips = roadStrips ?? [];
        _neighborhoodPanTileBounds = nextList.Count > 0 ? panTileBounds : null;
        _worldOriginX = worldOriginX;
        _worldOriginY = worldOriginY;
        _focusedRoadEdge = ((focusedRoadEdge % 4) + 4) % 4;
        _focusedRoadCenter = focusedRoadCenter;
        _focusedSecondaryRoadEdge = focusedSecondaryRoadEdge >= 0 ? ((focusedSecondaryRoadEdge % 4) + 4) % 4 : -1;
        _focusedSecondaryRoadCenter = focusedSecondaryRoadCenter;
        RebuildNeighborhoodStreetBuffer();
    }

    private GPUTexture? TryLoadWorldAssetTexture(string filePath, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(filePath) || width <= 0 || height <= 0)
            return null;

        if (_worldAssetTextureCache.TryGetValue(filePath, out var existing) && existing.Handle != nint.Zero)
            return existing;

        byte[]? raw = null;
        if (SimsSimulation.InMemorySessionAssets.TryReadBytes(filePath, out var memBytes))
            raw = memBytes;
        else if (File.Exists(filePath))
            raw = File.ReadAllBytes(filePath);

        if (raw is null || raw.Length != width * height * 4)
            return null;

        GPUTexture uploaded = GPU.UploadTextures([(width, height, false, raw)], [true])[0];
        _worldAssetTextureCache[filePath] = uploaded;
        return uploaded;
    }

    private void ReseedAmbientTrafficVehicles()
    {
        _trafficVehicles.Clear();
        if (_masterRoadSegments.Count == 0 || _worldVehicleAssets.Count == 0)
            return;

        var candidates = new List<(int Index, MasterRoadSegment Road, float Score)>(_masterRoadSegments.Count);
        for (int i = 0; i < _masterRoadSegments.Count; i++)
        {
            var r = _masterRoadSegments[i];
            float spanLen = r.Length;
            if (r.IsLoopCurve || spanLen < 24f)
                continue;
            float midX = (r.StartX + r.EndX) * 0.5f;
            float midY = (r.StartY + r.EndY) * 0.5f;
            float distToFocused = MathF.Sqrt((midX - 32f) * (midX - 32f) + (midY - 32f) * (midY - 32f));
            float score = (distToFocused <= 120f ? 400f : 0f) + MathF.Min(spanLen, 220f) - distToFocused * 1.5f;
            candidates.Add((i, r, score));
        }

        if (candidates.Count == 0)
            return;

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        const int maxVehicles = 20;

        void TryAddVehicle(int roadIdx, MasterRoadSegment road, float desiredPos, int dirSign, int assetIdx)
        {
            if (_trafficVehicles.Count >= maxVehicles)
                return;
            float len = road.Length;
            if (len < 12f)
                return;
            float pos = Math.Clamp(desiredPos, 4f, len - 4f);
            for (int i = 0; i < _trafficVehicles.Count; i++)
            {
                var existing = _trafficVehicles[i];
                if (existing.RoadIndex == roadIdx && existing.TravelSign == dirSign && MathF.Abs(existing.SpanPos - pos) < 16f)
                    return;
            }
            float speed = 5.5f + (_trafficVehicles.Count % 3) * 1.1f;
            _trafficVehicles.Add(new AmbientTrafficVehicle
            {
                RoadIndex = roadIdx,
                SpanPos = pos,
                TravelSign = dirSign,
                SpeedTilesPerSec = speed,
                VehicleAssetIndex = Math.Abs(assetIdx) % _worldVehicleAssets.Count,
            });
        }

        var (primaryIdx, primaryRoad, _) = candidates[0];
        float primaryLen = primaryRoad.Length;
        for (int i = 0; i < 6; i++)
        {
            float frac = (i + 0.5f) / 6f;
            TryAddVehicle(primaryIdx, primaryRoad, primaryLen * frac, (i & 1) == 0 ? 1 : -1, i);
        }

        for (int c = 1; c < candidates.Count && _trafficVehicles.Count < maxVehicles; c++)
        {
            var (roadIdx, road, _) = candidates[c];
            float len = road.Length;
            TryAddVehicle(roadIdx, road, len * 0.3f, 1, c * 2);
            TryAddVehicle(roadIdx, road, len * 0.7f, -1, c * 2 + 1);
        }

        _lastTrafficTickTimestamp = Stopwatch.GetTimestamp();
    }

    private void TickAmbientTrafficVehicles()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastTrafficTickTimestamp == 0)
        {
            _lastTrafficTickTimestamp = now;
            return;
        }
        double dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastTrafficTickTimestamp, now).TotalSeconds, 0.0, 0.1);
        _lastTrafficTickTimestamp = now;

        if (Speed <= 0 || _masterRoadSegments.Count == 0 || _trafficVehicles.Count == 0)
            return;

        float speedMul = Speed switch
        {
            1 => 1f,
            2 => 2.5f,
            _ => 5f,
        };

        for (int i = 0; i < _trafficVehicles.Count; i++)
        {
            var car = _trafficVehicles[i];
            if ((uint)car.RoadIndex >= (uint)_masterRoadSegments.Count)
                continue;
            var road = _masterRoadSegments[car.RoadIndex];
            float roadLen = road.Length;
            if (roadLen < 1f)
                continue;

            bool blockedAhead = false;
            for (int j = 0; j < _trafficVehicles.Count; j++)
            {
                if (i == j) continue;
                var other = _trafficVehicles[j];
                if (other.RoadIndex != car.RoadIndex || other.TravelSign != car.TravelSign)
                    continue;
                float delta = (other.SpanPos - car.SpanPos) * car.TravelSign;
                if (delta > 0f && delta < 11f)
                {
                    blockedAhead = true;
                    break;
                }
            }
            if (blockedAhead)
                continue;

            car.SpanPos += car.TravelSign * car.SpeedTilesPerSec * speedMul * (float)dt;
            if (car.TravelSign > 0 && car.SpanPos > roadLen + 2f)
                car.SpanPos = -2f;
            else if (car.TravelSign < 0 && car.SpanPos < -2f)
                car.SpanPos = roadLen + 2f;
        }
    }

    public void ClearAdjacentNeighborhood()
    {
        foreach (var entry in _adjacentLots)
            entry.Dispose();
        _adjacentLots.Clear();
        AdjacentLotsForHouse = 0;
        _neighborhoodRoadStrips = [];
        _neighborhoodPanTileBounds = null;
        if (_neighborhoodStreetBuffer is not null)
        {
            Retire(_neighborhoodStreetBuffer);
            _neighborhoodStreetBuffer = null;
        }
        foreach (var tex in _worldAssetTextureCache.Values)
            Retire(tex);
        _worldAssetTextureCache.Clear();
        _worldTreeAssets.Clear();
        _worldVehicleAssets.Clear();
        _masterRoadSegments.Clear();
        _masterIntersections.Clear();
        _trafficVehicles.Clear();
        _lastTrafficTickTimestamp = 0;
        _greenbeltTrees.Clear();
        _neighborhoodStreetCamera = -1;
        _neighborhoodStreetSignature = 0;
    }

    private static (int CamX, int CamY) RotateTileDelta(SimsSDL.Core.LotCamera camera, int dx, int dy) => camera switch
    {
        SimsSDL.Core.LotCamera.TopLeft => (dx, dy),
        SimsSDL.Core.LotCamera.TopRight => (-dy, dx),
        SimsSDL.Core.LotCamera.BottomRight => (-dx, -dy),
        SimsSDL.Core.LotCamera.BottomLeft => (dy, -dx),
        _ => (dx, dy),
    };

    /// <summary>
    /// Computes a lot's street-level terrain height relative to its (0,0) corner (<c>TerrainHeights[0]</c>)
    /// by averaging heights over its asphalt road tiles (Pattern 9), falling back to border tiles.
    /// Aligning every adjacent lot to the focused lot's street elevation prevents hilltop lots like Goth
    /// from causing vertical cliffs or floating neighbor fences/trees over the shared road.
    /// </summary>
    private static float ComputeLotStreetHeightOffset(LotScene scene)
    {
        if (scene.TerrainHeights is not { Count: > 0 } heights ||
            scene.LotWidth is not int w || scene.LotHeight is not int h ||
            w <= 0 || h <= 0 || heights.Count != w * h)
        {
            return 0f;
        }

        float baseH = heights[0];
        if (scene.Meshes is { Count: > 0 } meshes)
        {
            float sum = 0f;
            int count = 0;
            for (int i = 0; i < meshes.Count; i++)
            {
                var fl = meshes[i].Floor;
                if (fl is null || fl.Pattern != 9 || (meshes[i].Level ?? 1) != 1)
                    continue;
                int tx = Math.Clamp(fl.X, 0, w - 1);
                int ty = Math.Clamp(fl.Y, 0, h - 1);
                sum += heights[ty * w + tx] - baseH;
                count++;
            }
            if (count > 0)
                return sum / count;
        }

        // Fallback for roadless lots: average the 4 outer edges
        float edgeSum = 0f;
        int edgeCount = 0;
        for (int x = 0; x < w; x++)
        {
            edgeSum += (heights[x] - baseH) + (heights[(h - 1) * w + x] - baseH);
            edgeCount += 2;
        }
        for (int y = 1; y < h - 1; y++)
        {
            edgeSum += (heights[y * w] - baseH) + (heights[y * w + (w - 1)] - baseH);
            edgeCount += 2;
        }
        return edgeCount > 0 ? edgeSum / edgeCount : 0f;
    }

    private static Vector2 ComputeAdjacentOrigin(
        LotScene focusedScene,
        SceneViewport focusedViewport,
        LotScene adjacentScene,
        int tileOffsetX,
        int tileOffsetY)
    {
        Vector2 focSize = new(focusedScene.LotWidth ?? 64, focusedScene.LotHeight ?? 64);
        Vector2 adjSize = new(adjacentScene.LotWidth ?? 64, adjacentScene.LotHeight ?? 64);
        Vector2 focPt = focusedScene.Camera.ProjectedPoint(new Vector2(tileOffsetX, tileOffsetY), focSize);
        Vector2 adjP0 = adjacentScene.Camera.ProjectedPoint(Vector2.Zero, adjSize);
        Vector2 sceneDelta = focPt - adjP0;
        float streetAltDelta = (ComputeLotStreetHeightOffset(adjacentScene) - ComputeLotStreetHeightOffset(focusedScene)) * 7.5f;
        Vector2 originAlign = new(
            (adjacentScene.OriginX ?? 0f) - (focusedScene.OriginX ?? 0f),
            (adjacentScene.OriginY ?? 0f) - (focusedScene.OriginY ?? 0f) + streetAltDelta);
        return focusedViewport.Origin + (sceneDelta + originAlign) * focusedViewport.Scale;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        if (MathF.Abs(edge1 - edge0) < 1e-6f)
            return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float SampleBilinearHeight(IReadOnlyList<short> heights, int width, int height, float lx, float ly)
    {
        float cx = Math.Clamp(lx, 0f, width - 1);
        float cy = Math.Clamp(ly, 0f, height - 1);
        int x0 = (int)MathF.Floor(cx);
        int y0 = (int)MathF.Floor(cy);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float fx = cx - x0;
        float fy = cy - y0;

        float h00 = heights[y0 * width + x0];
        float h10 = heights[y0 * width + x1];
        float h01 = heights[y1 * width + x0];
        float h11 = heights[y1 * width + x1];

        float top = h00 * (1f - fx) + h10 * fx;
        float bot = h01 * (1f - fx) + h11 * fx;
        return top * (1f - fy) + bot * fy;
    }

    private void RebuildNeighborhoodStreetBuffer()
    {
        if (Scene is not { } scene || _adjacentLots.Count == 0 || _neighborhoodPanTileBounds is not { } bounds)
        {
            if (_neighborhoodStreetBuffer is not null)
            {
                Retire(_neighborhoodStreetBuffer);
                _neighborhoodStreetBuffer = null;
            }
            return;
        }

        int cam = (int)scene.Camera;
        var hash = new HashCode();
        hash.Add(cam);
        hash.Add(bounds.X);
        hash.Add(bounds.Y);
        hash.Add(bounds.Z);
        hash.Add(bounds.W);
        hash.Add(_worldOriginX);
        hash.Add(_worldOriginY);
        hash.Add(_focusedRoadEdge);
        hash.Add(_focusedRoadCenter);
        hash.Add(_focusedSecondaryRoadEdge);
        hash.Add(_focusedSecondaryRoadCenter);
        hash.Add(_worldTreeAssets.Count);
        hash.Add(_worldVehicleAssets.Count);
        hash.Add(scene.OriginX ?? 0f);
        hash.Add(scene.OriginY ?? 0f);
        hash.Add(scene.LotWidth ?? 64);
        hash.Add(scene.LotHeight ?? 64);
        hash.Add(_adjacentLots.Count);
        foreach (var adj in _adjacentLots)
        {
            hash.Add(adj.House);
            hash.Add(adj.TileOffsetX);
            hash.Add(adj.TileOffsetY);
            hash.Add(adj.Renderer.Scene is { } adjS ? (int)adjS.Camera : -1);
            hash.Add(adj.Renderer.Scene?.LotWidth ?? 0);
        }
        long sig = hash.ToHashCode();
        if (_neighborhoodStreetBuffer is not null && _neighborhoodStreetCamera == cam && _neighborhoodStreetSignature == sig)
            return;

        int focusedW = scene.LotWidth ?? 64;
        int focusedH = scene.LotHeight ?? 64;
        Vector2 focusedLotSize = new(focusedW, focusedH);
        Vector2 sceneOrigin = new(scene.OriginX ?? 0f, scene.OriginY ?? 0f);

        // Gather all loaded lot heightfields (focused lot + every adjacent lot), normalized to each lot's street level
        float focusedStreetH = ComputeLotStreetHeightOffset(scene);
        var heightSources = new List<LotHeightSource>(_adjacentLots.Count + 1);
        if (scene.TerrainHeights is { Count: > 0 } focusedHeights && focusedHeights.Count == focusedW * focusedH)
        {
            heightSources.Add(new LotHeightSource(scene, focusedW, focusedH, 0, 0, focusedHeights, focusedStreetH));
        }

        foreach (var adj in _adjacentLots)
        {
            if (adj.Renderer.Scene is not { } adjScene) continue;
            int aw = adjScene.LotWidth ?? 64;
            int ah = adjScene.LotHeight ?? 64;
            if (adjScene.TerrainHeights is { Count: > 0 } adjHeights && adjHeights.Count == aw * ah)
            {
                Vector2 focTargetCam = scene.Camera.CameraPoint(new Vector2(adj.TileOffsetX, adj.TileOffsetY), focusedLotSize);
                Vector2 adjCam0 = adjScene.Camera.CameraPoint(Vector2.Zero, new Vector2(aw, ah));
                float adjStreetH = ComputeLotStreetHeightOffset(adjScene);
                heightSources.Add(new LotHeightSource(
                    adjScene, aw, ah,
                    focTargetCam.X - adjCam0.X,
                    focTargetCam.Y - adjCam0.Y,
                    adjHeights, adjStreetH));
            }
        }

        // Compute 1-tile terrain grid window around currently loaded parcels + 28-tile countryside margin
        // (keeps 400x400 crispness tracking smoothly wherever the player pans across Sim Lane & Old Town!)
        int activeMinX = 0, activeMinY = 0, activeMaxX = focusedW, activeMaxY = focusedH;
        long sumCenterX = focusedW / 2, sumCenterY = focusedH / 2;
        int clusterCount = 1;
        foreach (var adj in _adjacentLots)
        {
            if (adj.Renderer.Scene is not { } adjS) continue;
            int aw = adjS.LotWidth ?? 64;
            int ah = adjS.LotHeight ?? 64;
            activeMinX = Math.Min(activeMinX, adj.TileOffsetX);
            activeMinY = Math.Min(activeMinY, adj.TileOffsetY);
            activeMaxX = Math.Max(activeMaxX, adj.TileOffsetX + aw);
            activeMaxY = Math.Max(activeMaxY, adj.TileOffsetY + ah);
            sumCenterX += adj.TileOffsetX + aw / 2;
            sumCenterY += adj.TileOffsetY + ah / 2;
            clusterCount++;
        }

        int minX = activeMinX - 28;
        int minY = activeMinY - 28;
        int maxX = activeMaxX + 28;
        int maxY = activeMaxY + 28;

        // Guard against extreme spans (400x400 tile window covers the loaded district + river + bridges + hills at 1-tile crispness)
        const int maxGridSpan = 400;
        if (maxX - minX > maxGridSpan)
        {
            int mid = (int)(sumCenterX / clusterCount);
            minX = mid - maxGridSpan / 2;
            maxX = mid + maxGridSpan / 2;
        }
        if (maxY - minY > maxGridSpan)
        {
            int mid = (int)(sumCenterY / clusterCount);
            minY = mid - maxGridSpan / 2;
            maxY = mid + maxGridSpan / 2;
        }

        int gridW = maxX - minX;
        int gridH = maxY - minY;

        // =========================================================================
        // Authoritative Master Road, Sidewalk & Bridge Network across all 41 lots
        // =========================================================================
        var masterNetwork = MasterRoadNetwork.Default;
        var allRoads = new List<MasterRoadSegment>(masterNetwork.Segments.Count);
        for (int i = 0; i < masterNetwork.Segments.Count; i++)
        {
            var s = masterNetwork.Segments[i];
            allRoads.Add(new MasterRoadSegment
            {
                Id = s.Id,
                StartX = s.StartX - _worldOriginX,
                StartY = s.StartY - _worldOriginY,
                EndX = s.EndX - _worldOriginX,
                EndY = s.EndY - _worldOriginY,
                HalfW = s.HalfWidth,
                SidewalkOffset = s.SidewalkOffset,
                IsBridge = s.IsBridge,
                IsLoopCurve = s.IsLoopCurve,
                StartNodeId = s.StartNodeId,
                EndNodeId = s.EndNodeId,
            });
        }
        _masterRoadSegments.Clear();
        _masterRoadSegments.AddRange(allRoads);

        _masterIntersections.Clear();
        for (int i = 0; i < masterNetwork.Intersections.Count; i++)
        {
            var n = masterNetwork.Intersections[i];
            _masterIntersections.Add(new ShiftedIntersectionNode(
                n.Id,
                n.WorldX - _worldOriginX,
                n.WorldY - _worldOriginY,
                n.Kind,
                n.ConnectedSegmentIds));
        }

        // Authentic Sims 1 street cross-section: asphalt (±halfW), 1-tile parkway, 1-tile concrete sidewalk (±sidewalkOffset)
        float DistanceToMasterRoad(float wx, float wy, out float localSidewalkHalfW)
        {
            return masterNetwork.DistanceToRoad(wx + _worldOriginX, wy + _worldOriginY, out _, out localSidewalkHalfW);
        }

        float RoadFlattenFactor(float wx, float wy)
        {
            float d = DistanceToMasterRoad(wx, wy, out float swHalfW);
            if (d >= float.MaxValue * 0.5f) return 1f;
            return SmoothStep(swHalfW, swHalfW + 10.0f, d);
        }

        // Use the focused lot's street-level elevation so unified roads and every aligned neighbor share one street grade
        float roadBaseH = focusedStreetH;
        float roadBaseAlt = roadBaseH * 7.5f;

        // 1. Evaluate smoothly blended heightfield across the entire unified neighborhood grid
        int gridStride = gridW + 1;
        var gridHeights = new float[gridStride * (gridH + 1)];
        var blurWeight = new float[gridStride * (gridH + 1)];
        var insideLotMask = new bool[gridStride * (gridH + 1)];
        for (int gy = 0; gy <= gridH; gy++)
        {
            int wy = minY + gy;
            int worldY = wy + _worldOriginY;
            for (int gx = 0; gx <= gridW; gx++)
            {
                int wx = minX + gx;
                int worldX = wx + _worldOriginX;
                Vector2 focCamPt = scene.Camera.CameraPoint(new Vector2(wx, wy), focusedLotSize);

                float sumH = 0f;
                float sumW = 0f;
                float maxSd = -999f;

                for (int s = 0; s < heightSources.Count; s++)
                {
                    var src = heightSources[s];
                    Vector2 adjCamPt = focCamPt - new Vector2(src.Dcx, src.Dcy);
                    Vector2 lotPt = src.Scene.Camera.LotPoint(adjCamPt, new Vector2(src.Width, src.Height));

                    float dx = MathF.Min(lotPt.X - src.ClipMinX, src.ClipMaxX - lotPt.X);
                    float dy = MathF.Min(lotPt.Y - src.ClipMinY, src.ClipMaxY - lotPt.Y);
                    float sd = (dx >= 0f && dy >= 0f)
                        ? MathF.Min(dx, dy)
                        : -MathF.Sqrt(MathF.Max(0f, -dx) * MathF.Max(0f, -dx) + MathF.Max(0f, -dy) * MathF.Max(0f, -dy));

                    if (sd > maxSd) maxSd = sd;
                    if (sd > -32f)
                    {
                        float w = SmoothStep(-32f, 4f, sd);
                        // Normalize every lot to the shared street plane so no cliffs form between lots
                        float h = (SampleBilinearHeight(src.Heights, src.Width, src.Height, lotPt.X, lotPt.Y) - src.BaseHeight)
                            - src.StreetBaseH + roadBaseH;
                        sumH += h * w;
                        sumW += w;
                    }
                }

                // Gentle deterministic countryside rolling hills outside all lot pads & roads (anchored to global world coords!)
                float riverDist = NeighborhoodWorldBlueprint.DistanceToCanonicalRiver(worldX, worldY);
                float riverHillFlatten = SmoothStep(9f, 22f, riverDist);
                float countryHill = 0.85f * riverHillFlatten * (MathF.Sin(worldX * 0.042f) * MathF.Cos(worldY * 0.038f) +
                                             0.45f * MathF.Sin((worldX + worldY) * 0.07f));
                float bgW = 0.55f * SmoothStep(4f, -32f, maxSd);
                float bgH = roadBaseH + countryHill * SmoothStep(4f, -24f, maxSd);
                float totalW = sumW + bgW;
                float blendedH = totalW > 1e-4f ? (sumH + bgH * bgW) / totalW : bgH;

                // Flatten roads cleanly (bridges cross the river cleanly at roadBaseH)
                float roadFlat = RoadFlattenFactor(wx, wy);
                blendedH = roadBaseH + (blendedH - roadBaseH) * roadFlat;

                // Carve natural grassy riverbank slope down along the canonical Neighborhood 1 river channel
                if (riverDist < 16f && maxSd < -4f)
                {
                    float bank = 1f - SmoothStep(6f, 16f, riverDist);
                    float bridgeProtect = 1f - roadFlat;
                    blendedH -= 1.6f * bank * (1f - bridgeProtect);
                }

                int idx = gy * gridStride + gx;
                gridHeights[idx] = blendedH;
                insideLotMask[idx] = maxSd >= -2f;
                // Smooth border/road transition zones organically while keeping house interiors crisp
                float borderSmooth = 1f - SmoothStep(4f, 18f, maxSd);
                float roadSmooth = roadFlat > 0f && roadFlat < 1f ? 1f : 0f;
                blurWeight[idx] = MathF.Max(borderSmooth, roadSmooth);
            }
        }

        // 6 passes of 3x3 Gaussian smoothing in border/road-transition zones to eliminate straight cliff creases
        var scratchH = new float[gridHeights.Length];
        for (int pass = 0; pass < 6; pass++)
        {
            Array.Copy(gridHeights, scratchH, gridHeights.Length);
            for (int gy = 1; gy < gridH; gy++)
            {
                int row = gy * gridStride;
                for (int gx = 1; gx < gridW; gx++)
                {
                    int idx = row + gx;
                    float bw = blurWeight[idx];
                    if (bw <= 1e-3f) continue;

                    float avg = (
                        scratchH[idx - gridStride - 1] + 2f * scratchH[idx - gridStride] + scratchH[idx - gridStride + 1] +
                        2f * scratchH[idx - 1] + 4f * scratchH[idx] + 2f * scratchH[idx + 1] +
                        scratchH[idx + gridStride - 1] + 2f * scratchH[idx + gridStride] + scratchH[idx + gridStride + 1]
                    ) * (1f / 16f);
                    gridHeights[idx] = scratchH[idx] + (avg - scratchH[idx]) * bw;
                }
            }
        }

        // Authentic Sims 1 directional sun lighting (matches SceneExporter.cs:1705-1770)
        const float sunLatitude = 0.31415927f;
        float sunLongitude = (cam & 3) switch
        {
            0 => MathF.PI * 0.25f,
            2 => MathF.PI * 0.75f,
            _ => MathF.PI * 1.25f,
        };
        float sunZ = MathF.Cos(sunLatitude);
        float sunX = MathF.Sin(sunLongitude) * MathF.Sin(sunLatitude);
        float sunY = -MathF.Cos(sunLongitude) * MathF.Sin(sunLatitude);

        float SlopeDotSq(float dx, float dy)
        {
            dx = Math.Clamp(dx, -5f, 5f);
            dy = Math.Clamp(dy, -5f, 5f);
            float dot = (2.82842712f * sunZ + dx * sunX + dy * sunY) / MathF.Sqrt(8f + dx * dx + dy * dy);
            return dot * dot;
        }

        float HeightGridAt(int gx, int gy)
        {
            gx = Math.Clamp(gx, 0, gridW);
            gy = Math.Clamp(gy, 0, gridH);
            return gridHeights[gy * (gridW + 1) + gx];
        }

        float TileTri0(int gx, int gy)
        {
            float h00 = HeightGridAt(gx, gy);
            float h01 = HeightGridAt(gx, gy + 1);
            float h10 = HeightGridAt(gx + 1, gy);
            return SlopeDotSq(h00 - h10, h01 - h00);
        }

        float TileTri1(int gx, int gy)
        {
            float h01 = HeightGridAt(gx, gy + 1);
            float h11 = HeightGridAt(gx + 1, gy + 1);
            float h10 = HeightGridAt(gx + 1, gy);
            return SlopeDotSq(h01 - h11, h10 - h11);
        }

        // Authentic Sims 1 grass palette (TerrainColors type 0, default grassState = 127)
        const byte defaultGrass = 127;
        const float grassBlend = defaultGrass / 255f;
        float baseLightR = 0x50 + (0x9d - 0x50) * grassBlend;
        float baseLightG = 0x74 + (0x75 - 0x74) * grassBlend;
        float baseLightB = 0x3b + (0x41 - 0x3b) * grassBlend;
        float baseDarkR = 0x0e + (0x51 - 0x0e) * grassBlend;
        float baseDarkG = 0x35 + (0x3c - 0x35) * grassBlend;
        float baseDarkB = 0x08 + (0x12 - 0x08) * grassBlend;
        byte darkR = (byte)MathF.Round(baseDarkR);
        byte darkG = (byte)MathF.Round(baseDarkG);
        byte darkB = (byte)MathF.Round(baseDarkB);

        // Precompute shaded terrain vertices on the unified grid
        var gridVerts = new LotScene.TerrainVertex[(gridW + 1) * (gridH + 1)];
        for (int gy = 0; gy <= gridH; gy++)
        {
            int wy = minY + gy;
            int worldY = wy + _worldOriginY;
            for (int gx = 0; gx <= gridW; gx++)
            {
                int wx = minX + gx;
                int worldX = wx + _worldOriginX;
                float h = gridHeights[gy * (gridW + 1) + gx];
                float altitude = h * 7.5f;
                Vector2 p = scene.Camera.ProjectedPoint(new Vector2(wx, wy), focusedLotSize, altitude) - sceneOrigin;

                float avgDotSq = (TileTri0(gx - 1, gy - 1) + TileTri1(gx - 1, gy - 1)
                    + TileTri0(gx, gy - 1) + TileTri1(gx - 1, gy)
                    + TileTri0(gx, gy) + TileTri1(gx, gy)) / 6f;

                uint hval = unchecked((uint)(worldX * 0x40de9 + worldY * 0x10dcd) ^ 0x40de9u);
                hval ^= hval >> 16;
                hval *= 0x7feb352du;
                hval ^= hval >> 15;
                int jitter = (int)Math.Min(hval % 3u, (hval >> 8) % 3u);
                if ((hval & 0x10000u) != 0) jitter = -jitter;
                int shade = Math.Clamp((int)((1f - avgDotSq) * 127f) + jitter, 0, 127);
                float t = shade / 128f;

                byte r = (byte)Math.Clamp((int)MathF.Round(baseLightR + (baseDarkR - baseLightR) * t), 0, 255);
                byte g = (byte)Math.Clamp((int)MathF.Round(baseLightG + (baseDarkG - baseLightG) * t), 0, 255);
                byte b = (byte)Math.Clamp((int)MathF.Round(baseLightB + (baseDarkB - baseLightB) * t), 0, 255);

                gridVerts[gy * (gridW + 1) + gx] = new LotScene.TerrainVertex(p.X, p.Y, r, g, b, darkR, darkG, darkB, defaultGrass);
            }
        }

        int terrainTriVerts = gridW * gridH * 6;
        var vertices = new List<LotScene.TerrainVertex>(terrainTriVerts + 8192);

        Vector2 ProjectFlat(float tx, float ty, float alt = 0f) =>
            scene.Camera.ProjectedPoint(new Vector2(tx, ty), focusedLotSize, alt) - sceneOrigin;

        void AddFlatQuad(float x0, float y0, float x1, float y1, byte r, byte g, byte b, byte dr, byte dg, byte db, byte grassFlag = 0, float alt = 0f)
        {
            Vector2 p00 = ProjectFlat(x0, y0, alt);
            Vector2 p10 = ProjectFlat(x1, y0, alt);
            Vector2 p11 = ProjectFlat(x1, y1, alt);
            Vector2 p01 = ProjectFlat(x0, y1, alt);

            var v00 = new LotScene.TerrainVertex(p00.X, p00.Y, r, g, b, dr, dg, db, grassFlag);
            var v10 = new LotScene.TerrainVertex(p10.X, p10.Y, r, g, b, dr, dg, db, grassFlag);
            var v11 = new LotScene.TerrainVertex(p11.X, p11.Y, r, g, b, dr, dg, db, grassFlag);
            var v01 = new LotScene.TerrainVertex(p01.X, p01.Y, r, g, b, dr, dg, db, grassFlag);

            vertices.Add(v00); vertices.Add(v10); vertices.Add(v11);
            vertices.Add(v00); vertices.Add(v11); vertices.Add(v01);
        }

        // 2. Wide outer countryside grass skirt (eliminates any blue void across the entire 41-lot world)
        float flatDotSq = SlopeDotSq(0f, 0f);
        float flatT = Math.Clamp((int)((1f - flatDotSq) * 127f), 0, 127) / 128f;
        byte flatR = (byte)MathF.Round(baseLightR + (baseDarkR - baseLightR) * flatT);
        byte flatG = (byte)MathF.Round(baseLightG + (baseDarkG - baseLightG) * flatT);
        byte flatB = (byte)MathF.Round(baseLightB + (baseDarkB - baseLightB) * flatT);
        const float outerPad = 256f;
        float skirtMinX = MathF.Min(minX, bounds.X) - outerPad;
        float skirtMinY = MathF.Min(minY, bounds.Y) - outerPad;
        float skirtMaxX = MathF.Max(maxX, bounds.Z) + outerPad;
        float skirtMaxY = MathF.Max(maxY, bounds.W) + outerPad;
        AddFlatQuad(skirtMinX, skirtMinY, skirtMaxX, skirtMaxY, flatR, flatG, flatB, darkR, darkG, darkB, defaultGrass, alt: roadBaseAlt);

        // 3. Emit the unified 1-tile-resolution blended terrain mesh
        for (int gy = 0; gy < gridH; gy++)
        {
            int row0 = gy * (gridW + 1);
            int row1 = (gy + 1) * (gridW + 1);
            for (int gx = 0; gx < gridW; gx++)
            {
                var v00 = gridVerts[row0 + gx];
                var v10 = gridVerts[row0 + gx + 1];
                var v11 = gridVerts[row1 + gx + 1];
                var v01 = gridVerts[row1 + gx];

                vertices.Add(v00); vertices.Add(v10); vertices.Add(v11);
                vertices.Add(v11); vertices.Add(v01); vertices.Add(v00);
            }
        }

        // 3b. Paint the canonical Neighborhood 1 / Old Town river water channel inside the carved river valley
        //     before rendering roads and bridges so bridges draw cleanly above the water.
        for (int gy = 0; gy <= gridH - 2; gy += 2)
        {
            int wy = minY + gy;
            for (int gx = 0; gx <= gridW - 2; gx += 2)
            {
                int idx00 = gy * gridStride + gx;
                if (insideLotMask[idx00] || insideLotMask[idx00 + 2] ||
                    insideLotMask[(gy + 2) * gridStride + gx] || insideLotMask[(gy + 2) * gridStride + gx + 2])
                {
                    continue;
                }

                int wx = minX + gx;
                float rDist = NeighborhoodWorldBlueprint.DistanceToCanonicalRiver(wx + _worldOriginX + 1f, wy + _worldOriginY + 1f);
                if (rDist > 9.2f)
                    continue;

                if (rDist <= 7.0f)
                {
                    AddFlatQuad(wx, wy, wx + 2f, wy + 2f, 34, 74, 126, 22, 52, 96, grassFlag: 0, alt: roadBaseAlt - 7.5f);
                }
                else
                {
                    AddFlatQuad(wx, wy, wx + 2f, wy + 2f, 46, 92, 142, 32, 68, 110, grassFlag: 0, alt: roadBaseAlt - 7.2f);
                }
            }
        }

        // 4. Paint the unified master Sims 1 road, sidewalk, crosswalk & bridge network across ALL 41 lots
        const byte swR = 166, swG = 166, swB = 160, swDr = 148, swDg = 148, swDb = 142;
        const byte curbR = 122, curbG = 122, curbB = 118, curbDr = 112, curbDg = 112, curbDb = 108;
        const byte bridgeR = 148, bridgeG = 144, bridgeB = 136, bridgeDr = 116, bridgeDg = 112, bridgeDb = 104;
        const byte aspR = 38, aspG = 40, aspB = 44, aspDr = 30, aspDg = 32, aspDb = 36;
        const byte yelR = 206, yelG = 166, yelB = 52, yelDr = 192, yelDg = 152, yelDb = 44;

        void AddOrientedRoadQuad(MasterRoadSegment r, float u0, float v0, float u1, float v1,
            byte cr, byte cg, byte cb, byte cdr, byte cdg, byte cdb, float altOffset = 0.2f)
        {
            float len = r.Length;
            if (len < 1e-4f) return;
            float tx = (r.EndX - r.StartX) / len;
            float ty = (r.EndY - r.StartY) / len;
            float nx = -ty;
            float ny = tx;
            float alt = roadBaseAlt + altOffset;

            Vector2 p00 = ProjectFlat(r.StartX + tx * u0 + nx * v0, r.StartY + ty * u0 + ny * v0, alt);
            Vector2 p10 = ProjectFlat(r.StartX + tx * u1 + nx * v0, r.StartY + ty * u1 + ny * v0, alt);
            Vector2 p11 = ProjectFlat(r.StartX + tx * u1 + nx * v1, r.StartY + ty * u1 + ny * v1, alt);
            Vector2 p01 = ProjectFlat(r.StartX + tx * u0 + nx * v1, r.StartY + ty * u0 + ny * v1, alt);

            var vert00 = new LotScene.TerrainVertex(p00.X, p00.Y, cr, cg, cb, cdr, cdg, cdb, 0);
            var vert10 = new LotScene.TerrainVertex(p10.X, p10.Y, cr, cg, cb, cdr, cdg, cdb, 0);
            var vert11 = new LotScene.TerrainVertex(p11.X, p11.Y, cr, cg, cb, cdr, cdg, cdb, 0);
            var vert01 = new LotScene.TerrainVertex(p01.X, p01.Y, cr, cg, cb, cdr, cdg, cdb, 0);

            vertices.Add(vert00); vertices.Add(vert10); vertices.Add(vert11);
            vertices.Add(vert00); vertices.Add(vert11); vertices.Add(vert01);
        }

        // 4a. Concrete sidewalks set back by the authentic 1-tile Sims 1 grass parkway strip
        for (int i = 0; i < allRoads.Count; i++)
        {
            var r = allRoads[i];
            float len = r.Length;
            float swIn = r.SidewalkOffset - 0.5f;
            float swOut = r.SidewalkOffset + 0.5f;
            AddOrientedRoadQuad(r, 0f, -swOut, len, -swIn, swR, swG, swB, swDr, swDg, swDb);
            AddOrientedRoadQuad(r, 0f, swIn, len, swOut, swR, swG, swB, swDr, swDg, swDb);
        }

        // 4b. Concrete curb edging along the asphalt edge + raised stone parapets on river bridges
        for (int i = 0; i < allRoads.Count; i++)
        {
            var r = allRoads[i];
            float len = r.Length;
            AddOrientedRoadQuad(r, 0f, -r.HalfW - 0.18f, len, r.HalfW + 0.18f, curbR, curbG, curbB, curbDr, curbDg, curbDb);

            for (float u = 0f; u < len; u += 2f)
            {
                float u0 = u;
                float u1 = MathF.Min(u + 2f, len);
                if (u1 <= u0 + 0.15f) continue;
                float midFrac = (u0 + u1) * 0.5f / MathF.Max(len, 1e-4f);
                float mx = r.StartX + (r.EndX - r.StartX) * midFrac + _worldOriginX;
                float my = r.StartY + (r.EndY - r.StartY) * midFrac + _worldOriginY;
                if (r.IsBridge || NeighborhoodWorldBlueprint.DistanceToCanonicalRiver(mx, my) <= 13.5f)
                {
                    AddOrientedRoadQuad(r, u0, -r.HalfW - 2.45f, u1, -r.HalfW - 2.0f, bridgeR, bridgeG, bridgeB, bridgeDr, bridgeDg, bridgeDb, altOffset: 0.45f);
                    AddOrientedRoadQuad(r, u0, r.HalfW + 2.0f, u1, r.HalfW + 2.45f, bridgeR, bridgeG, bridgeB, bridgeDr, bridgeDg, bridgeDb, altOffset: 0.45f);
                }
            }
        }

        // 4c. Dark asphalt roadbeds + intersection patches (drawn after sidewalks/curbs so joints stay seamless)
        for (int i = 0; i < allRoads.Count; i++)
        {
            var r = allRoads[i];
            AddOrientedRoadQuad(r, 0f, -r.HalfW, r.Length, r.HalfW, aspR, aspG, aspB, aspDr, aspDg, aspDb);
        }
        for (int i = 0; i < _masterIntersections.Count; i++)
        {
            var node = _masterIntersections[i];
            if (node.Kind is RoadNodeKind.PassThrough or RoadNodeKind.Portal) continue;
            float hw = MasterRoadNetwork.DefaultHalfWidth;
            AddFlatQuad(node.X - hw, node.Y - hw, node.X + hw, node.Y + hw, aspR, aspG, aspB, aspDr, aspDg, aspDb, grassFlag: 0, alt: roadBaseAlt + 0.2f);
        }

        // 4d. Classic Sims 1 golden-yellow dashed centerlines along every road (skipping intersections)
        for (int i = 0; i < allRoads.Count; i++)
        {
            var r = allRoads[i];
            float len = r.Length;
            if (len < 2f) continue;
            float tx = (r.EndX - r.StartX) / len;
            float ty = (r.EndY - r.StartY) / len;
            var nStart = _masterIntersections[r.StartNodeId];
            var nEnd = _masterIntersections[r.EndNodeId];
            float skipStart = nStart.Kind is RoadNodeKind.FourWay or RoadNodeKind.TJunction ? r.HalfW + 1.2f : 0.5f;
            float skipEnd = nEnd.Kind is RoadNodeKind.FourWay or RoadNodeKind.TJunction ? r.HalfW + 1.2f : 0.5f;
            for (float u = skipStart; u < len - skipEnd; u += 4f)
            {
                float u0 = u;
                float u1 = MathF.Min(u + 2.0f, len - skipEnd);
                if (u1 <= u0 + 0.25f) continue;
                AddOrientedRoadQuad(r, u0, -0.11f, u1, 0.11f, yelR, yelG, yelB, yelDr, yelDg, yelDb);
            }
        }

        // 4e. Classic Sims 1 double-line yellow crosswalks at every FourWay & TJunction intersection leg
        const float crosswalkBarW = 0.14f;
        for (int i = 0; i < _masterIntersections.Count; i++)
        {
            var node = _masterIntersections[i];
            if (node.Kind is not (RoadNodeKind.FourWay or RoadNodeKind.TJunction))
                continue;

            foreach (int segId in node.ConnectedSegmentIds)
            {
                var r = allRoads[segId];
                float len = r.Length;
                if (len < r.HalfW + 2.5f) continue;
                float barHalf = MathF.Max(1.0f, r.HalfW - 0.25f);
                float off1 = r.HalfW + 0.15f;
                float off2 = r.HalfW + 0.75f;
                if (r.StartNodeId == node.Id)
                {
                    AddOrientedRoadQuad(r, off1, -barHalf, off1 + crosswalkBarW, barHalf, yelR, yelG, yelB, yelDr, yelDg, yelDb);
                    AddOrientedRoadQuad(r, off2, -barHalf, off2 + crosswalkBarW, barHalf, yelR, yelG, yelB, yelDr, yelDg, yelDb);
                }
                else
                {
                    AddOrientedRoadQuad(r, len - off1 - crosswalkBarW, -barHalf, len - off1, barHalf, yelR, yelG, yelB, yelDr, yelDg, yelDb);
                    AddOrientedRoadQuad(r, len - off2 - crosswalkBarW, -barHalf, len - off2, barHalf, yelR, yelG, yelB, yelDr, yelDg, yelDb);
                }
            }
        }

        if (_neighborhoodStreetBuffer is not null)
            Retire(_neighborhoodStreetBuffer);

        _neighborhoodStreetBuffer = MakeTerrain(scene, vertices);
        _neighborhoodStreetCamera = cam;
        _neighborhoodStreetSignature = sig;
        _masterRoadBaseAlt = roadBaseAlt;
        ReseedAmbientTrafficVehicles();

        // 5. Scatter deterministic composited Trees.iff full-canopy tree sprites across the open greenbelt hills
        //    strictly anchored to global blueprint coords and outside all parcels, roads, and the river.
        _greenbeltTrees.Clear();
        if (_worldTreeAssets.Count > 0)
        {
            const int cellStep = 12;
            static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
            int startWorldY = FloorDiv(minY + _worldOriginY + 4, cellStep) * cellStep;
            int endWorldY = maxY + _worldOriginY - 4;
            int startWorldX = FloorDiv(minX + _worldOriginX + 4, cellStep) * cellStep;
            int endWorldX = maxX + _worldOriginX - 4;

            for (int worldY0 = startWorldY; worldY0 <= endWorldY; worldY0 += cellStep)
            {
                for (int worldX0 = startWorldX; worldX0 <= endWorldX; worldX0 += cellStep)
                {
                    uint h = unchecked((uint)(worldX0 * 73856093 ^ worldY0 * 19349663 ^ 0x5bd1e995));
                    h ^= h >> 15;
                    h *= 0x2c1b3c6du;
                    h ^= h >> 12;
                    if ((h % 100u) >= 42u) continue; // Organic ~42% wooded hillside clusters

                    int jx = (int)((h >> 8) % 7u) - 3;
                    int jy = (int)((h >> 12) % 7u) - 3;
                    int worldX = worldX0 + jx;
                    int worldY = worldY0 + jy;
                    int wx = worldX - _worldOriginX;
                    int wy = worldY - _worldOriginY;
                    int tx = wx - minX;
                    int ty = wy - minY;
                    if (tx < 2 || tx > gridW - 2 || ty < 2 || ty > gridH - 2) continue;

                    // Never place inside any road/sidewalk corridor or in/near the river channel
                    if (RoadFlattenFactor(wx, wy) < 0.99f) continue;
                    if (NeighborhoodWorldBlueprint.DistanceToCanonicalRiver(worldX, worldY) < 16f) continue;

                    // Never place inside any loaded lot footprint (leave 5-tile margin around yards)
                    Vector2 focCamPt = scene.Camera.CameraPoint(new Vector2(wx, wy), focusedLotSize);
                    bool insideAnyLot = false;
                    for (int s = 0; s < heightSources.Count; s++)
                    {
                        var src = heightSources[s];
                        Vector2 adjCamPt = focCamPt - new Vector2(src.Dcx, src.Dcy);
                        Vector2 lotPt = src.Scene.Camera.LotPoint(adjCamPt, new Vector2(src.Width, src.Height));
                        if (lotPt.X >= -5f && lotPt.X <= src.Width + 5f && lotPt.Y >= -5f && lotPt.Y <= src.Height + 5f)
                        {
                            insideAnyLot = true;
                            break;
                        }
                    }
                    if (insideAnyLot) continue;

                    float groundH = gridHeights[ty * gridStride + tx];
                    Vector2 groundPt = scene.Camera.ProjectedPoint(new Vector2(wx, wy), focusedLotSize, groundH * 7.5f) - sceneOrigin;
                    int treeIdx = (int)((h >> 16) % (uint)_worldTreeAssets.Count);
                    var tmpl = _worldTreeAssets[treeIdx];
                    bool flip = ((h >> 20) & 1u) != 0;
                    float drawX = groundPt.X - (flip ? tmpl.Width - tmpl.AnchorX : tmpl.AnchorX);
                    float drawY = groundPt.Y - tmpl.AnchorY;
                    _greenbeltTrees.Add(new GreenbeltTreeInstance(
                        drawX, drawY, tmpl.Width, tmpl.Height, treeIdx, flip, focCamPt.X + focCamPt.Y));
                }
            }
            _greenbeltTrees.Sort((a, b) => a.DepthKey.CompareTo(b.DepthKey));
        }
    }

    private unsafe void EncodeGreenbeltTrees(
        nint pass,
        nint command,
        Vector2 canvas,
        int sampleCount)
    {
        if (Scene is not { } scene || Viewport(canvas) is not { } viewport ||
            _greenbeltTrees.Count == 0 || _worldTreeAssets.Count == 0)
            return;

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        SDL_SetGPUScissor(pass, in scissor);
        nint pipeline = GPU.Pipeline(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat, sampleCount);
        SDL_BindGPUGraphicsPipeline(pass, pipeline);

        SimsSDL.Core.SceneLighting? lighting = ActiveLighting(scene);
        Vector4 tint = lighting?.Outside.Multiplier ?? Vector4.One;
        float scale = viewport.Scale;
        Vector2 origin = viewport.Origin;
        nint sampler = GPU.Sampler;
        int lastBoundTex = -1;

        for (int i = 0; i < _greenbeltTrees.Count; i++)
        {
            var tree = _greenbeltTrees[i];
            if ((uint)tree.TextureIndex >= (uint)_worldTreeAssets.Count)
                continue;

            float sx = origin.X + tree.SceneX * scale;
            float sy = origin.Y + tree.SceneY * scale;
            float sw = tree.Width * scale;
            float sh = tree.Height * scale;
            if (sx + sw < 0f || sy + sh < 0f || sx >= canvas.X || sy >= canvas.Y)
                continue;

            if (tree.TextureIndex != lastBoundTex)
            {
                GPUTexture tex = _worldTreeAssets[tree.TextureIndex].Texture;
                if (tex.Handle == nint.Zero) continue;
                var binding = new SDL_GPUTextureSamplerBinding { texture = tex.Handle, sampler = sampler };
                SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
                lastBoundTex = tree.TextureIndex;
            }

            var quad = new Quad(new Vector4(sx, sy, sw, sh), canvas, tree.Flip)
            {
                Tint = tint,
                DepthInterval = new Vector4(0f, 0f, 8.0f / 31.0f, AntiAliasedSprites ? 1f : 0f),
            };
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }
    }

    private unsafe void EncodeTrafficVehicles(
        nint pass,
        nint command,
        Vector2 canvas,
        int sampleCount)
    {
        if (Scene is not { } scene || Viewport(canvas) is not { } viewport ||
            _trafficVehicles.Count == 0 || _worldVehicleAssets.Count == 0 || _masterRoadSegments.Count == 0)
            return;

        Vector2 focusedLotSize = new(scene.LotWidth ?? 64, scene.LotHeight ?? 64);
        Vector2 sceneOrigin = new(scene.OriginX ?? 0f, scene.OriginY ?? 0f);
        float scale = viewport.Scale;
        Vector2 origin = viewport.Origin;
        int camRot = ((int)scene.Camera % 4 + 4) % 4;
        const float laneOffset = 1.35f;

        var drawCars = new List<(float DepthKey, GPUTexture Tex, Vector4 Rect)>(_trafficVehicles.Count);
        for (int i = 0; i < _trafficVehicles.Count; i++)
        {
            var car = _trafficVehicles[i];
            if ((uint)car.RoadIndex >= (uint)_masterRoadSegments.Count ||
                (uint)car.VehicleAssetIndex >= (uint)_worldVehicleAssets.Count)
                continue;

            var road = _masterRoadSegments[car.RoadIndex];
            float len = road.Length;
            if (len < 1e-4f) continue;
            float tx = (road.EndX - road.StartX) / len;
            float ty = (road.EndY - road.StartY) / len;
            float nx = -ty;
            float ny = tx;
            float wx = road.StartX + tx * car.SpanPos + nx * (car.TravelSign * laneOffset);
            float wy = road.StartY + ty * car.SpanPos + ny * (car.TravelSign * laneOffset);
            float dirX = tx * car.TravelSign;
            float dirY = ty * car.TravelSign;
            int travelDir = (int)MathF.Round(MathF.Atan2(dirY, dirX) / (MathF.PI * 0.5f)) * 2;
            travelDir = ((travelDir + 2) & 6);

            int viewDir = (travelDir + camRot * 2) & 6;
            var views = _worldVehicleAssets[car.VehicleAssetIndex].Views;
            if (!views.TryGetValue(viewDir, out var view))
                continue;

            Vector2 groundPt = scene.Camera.ProjectedPoint(new Vector2(wx, wy), focusedLotSize, _masterRoadBaseAlt + 0.15f) - sceneOrigin;
            float sx = origin.X + (groundPt.X - view.AnchorX) * scale;
            float sy = origin.Y + (groundPt.Y - view.AnchorY) * scale;
            float sw = view.Width * scale;
            float sh = view.Height * scale;
            if (sx + sw < 0f || sy + sh < 0f || sx >= canvas.X || sy >= canvas.Y)
                continue;

            Vector2 camPt = scene.Camera.CameraPoint(new Vector2(wx, wy), focusedLotSize);
            drawCars.Add((camPt.X + camPt.Y, view.Texture, new Vector4(sx, sy, sw, sh)));
        }

        if (drawCars.Count == 0)
            return;

        drawCars.Sort((a, b) => a.DepthKey.CompareTo(b.DepthKey));

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        SDL_SetGPUScissor(pass, in scissor);
        nint pipeline = GPU.Pipeline(GPUContext.Program.Sprite, GPUContext.DepthMode.Flat, sampleCount);
        SDL_BindGPUGraphicsPipeline(pass, pipeline);

        SimsSDL.Core.SceneLighting? lighting = ActiveLighting(scene);
        Vector4 tint = lighting?.Outside.Multiplier ?? Vector4.One;
        nint sampler = GPU.Sampler;
        nint lastHandle = nint.Zero;

        for (int i = 0; i < drawCars.Count; i++)
        {
            var item = drawCars[i];
            if (item.Tex.Handle == nint.Zero)
                continue;
            if (item.Tex.Handle != lastHandle)
            {
                var binding = new SDL_GPUTextureSamplerBinding { texture = item.Tex.Handle, sampler = sampler };
                SDL_BindGPUFragmentSamplers(pass, 0, &binding, 1);
                lastHandle = item.Tex.Handle;
            }
            var quad = new Quad(item.Rect, canvas, false)
            {
                Tint = tint,
                DepthInterval = new Vector4(0f, 0f, 8.0f / 31.0f, AntiAliasedSprites ? 1f : 0f),
            };
            SDL_PushGPUVertexUniformData(command, 0, (nint)(&quad), (uint)sizeof(Quad));
            SDL_DrawGPUPrimitives(pass, 6, 1, 0, 0);
        }
    }

    private unsafe void EncodeNeighborhoodStreets(
        nint pass,
        nint command,
        Vector2 canvas,
        int sampleCount)
    {
        if (Scene is not { } scene || Viewport(canvas) is not { } viewport)
            return;

        if (_neighborhoodStreetBuffer is null || _neighborhoodStreetBuffer.Handle == nint.Zero)
            return;

        var scissor = new SDL_Rect { x = 0, y = 0, w = (int)canvas.X, h = (int)canvas.Y };
        SDL_SetGPUScissor(pass, in scissor);
        nint pipeline = GPU.Pipeline(GPUContext.Program.Terrain, GPUContext.DepthMode.Flat, sampleCount);
        SDL_BindGPUGraphicsPipeline(pass, pipeline);
        nint handle = _neighborhoodStreetBuffer.Handle;
        SDL_BindGPUVertexStorageBuffers(pass, 0, &handle, 1);

        SimsSDL.Core.SceneLighting? lighting = ActiveLighting(scene);
        var transform = new Quad(new Vector4(viewport.Origin.X, viewport.Origin.Y, viewport.Scale, viewport.Scale), canvas)
        {
            Tint = lighting?.Outside.Multiplier ?? Vector4.One,
            UvRect = new Vector4(TerrainDetail, (float)EffectiveVisualZoom, 0f, 0f),
        };
        SDL_PushGPUVertexUniformData(command, 0, (nint)(&transform), (uint)sizeof(Quad));
        SDL_DrawGPUPrimitives(pass, (uint)_neighborhoodStreetBuffer.Count, 1, 0, 0);
    }

    private void EncodeConnectedNeighborhood(
        nint command,
        Vector2 canvas,
        GPUTexture targetColor,
        GPUTexture targetDepth,
        GPUTexture? resolveColor,
        int passSamples,
        bool deferTranslucentAvatars,
        ref nint activePass)
    {
        if (Scene is not { } focusedScene || Viewport(canvas) is not { } focusedViewport)
        {
            EncodeLot(activePass, command, canvas, passSamples, deferTranslucentAvatars: deferTranslucentAvatars);
            return;
        }

        // 1. Draw the unified blended neighborhood terrain, continuous road network, ambient SPR2 vehicles, and composited greenbelt trees first
        EncodeNeighborhoodStreets(activePass, command, canvas, passSamples);
        EncodeTrafficVehicles(activePass, command, canvas, passSamples);
        EncodeGreenbeltTrees(activePass, command, canvas, passSamples);

        // 2. Sort all visible lots (focused + adjacent) back-to-front by isometric depth (dcx + dcy)
        var drawList = new List<(int DepthKey, CachedAdjacentLot? Adjacent)>(_adjacentLots.Count + 1)
        {
            (0, null) // Focused lot at (0, 0)
        };

        foreach (var adj in _adjacentLots)
        {
            if (adj.Renderer.Scene is null) continue;
            var (dcx, dcy) = RotateTileDelta(focusedScene.Camera, adj.TileOffsetX, adj.TileOffsetY);
            drawList.Add((dcx + dcy, adj));
        }

        drawList.Sort((a, b) => a.DepthKey.CompareTo(b.DepthKey));

        // 3. Draw each lot's buildings, floors, trees, objects, and Sims in painter's order on top of the unified terrain & roads
        LotZoom activeZoom = EffectiveVisualZoom;
        for (int i = 0; i < drawList.Count; i++)
        {
            SDL_EndGPURenderPass(activePass);
            activePass = BeginPass(
                command,
                targetColor,
                targetDepth,
                clearColor: false,
                clearDepth: true,
                resolveColor: i == drawList.Count - 1 ? resolveColor : null,
                discardDepth: false);

            var item = drawList[i];
            if (item.Adjacent is null)
            {
                EncodeLot(
                    activePass,
                    command,
                    canvas,
                    passSamples,
                    deferTranslucentAvatars: deferTranslucentAvatars,
                    skipTerrainAndRoads: true);
            }
            else
            {
                var sub = item.Adjacent.Renderer;
                sub.UIScale = UIScale;
                sub.LiveMode = LiveMode;
                sub.AntiAliasedSprites = AntiAliasedSprites;
                if (sub.Scene is { } adjScene)
                {
                    Vector2 adjOrigin = ComputeAdjacentOrigin(
                        focusedScene,
                        focusedViewport,
                        adjScene,
                        item.Adjacent.TileOffsetX,
                        item.Adjacent.TileOffsetY);
                    sub.EncodeLot(
                        activePass,
                        command,
                        canvas,
                        passSamples,
                        deferTranslucentAvatars: false,
                        originOverride: adjOrigin,
                        scaleOverride: focusedViewport.Scale,
                        zoomOverride: activeZoom,
                        skipTerrainAndRoads: true);
                }
            }
        }
    }

    /// <summary>
    /// Hit-tests adjacent rendered neighbor lots at <paramref name="screenPoint"/> (front-to-back),
    /// returning the neighbor house, optional picked object ID, and ground tile when hit.
    /// </summary>
    public AdjacentLotPick? PickAdjacentLot(Vector2 screenPoint, Vector2 canvas)
    {
        if (Scene is not { } focusedScene || Viewport(canvas) is not { } focusedViewport || _adjacentLots.Count == 0)
            return null;

        // Test front-to-back (highest isometric depth first)
        var ordered = _adjacentLots
            .Where(a => a.Renderer.Scene is not null)
            .Select(a =>
            {
                var (dcx, dcy) = RotateTileDelta(focusedScene.Camera, a.TileOffsetX, a.TileOffsetY);
                return (Depth: dcx + dcy, Entry: a);
            })
            .OrderByDescending(x => x.Depth);

        foreach (var (_, entry) in ordered)
        {
            var sub = entry.Renderer;
            if (sub.Scene is not { } adjScene) continue;
            Vector2 adjOrigin = ComputeAdjacentOrigin(
                focusedScene,
                focusedViewport,
                adjScene,
                entry.TileOffsetX,
                entry.TileOffsetY);
            Vector2 adjScenePoint = (screenPoint - adjOrigin) / focusedViewport.Scale;
            if (adjScenePoint.X < 0f || adjScenePoint.Y < 0f || adjScenePoint.X > adjScene.Width || adjScenePoint.Y > adjScene.Height)
                continue;

            // Check if point falls inside the neighbor lot's tile diamond (or up to 3 tiles outside for tall roofs/trees/objects)
            if (adjScene.LotWidth is int lw && adjScene.LotHeight is int lh && adjScene.OriginX is float ox && adjScene.OriginY is float oy)
            {
                Vector2 projected = adjScenePoint + new Vector2(ox, oy) - new Vector2(34f, 158f);
                Vector2 camPt = new(projected.Y / 32f + projected.X / 64f, projected.Y / 32f - projected.X / 64f);
                Vector2 lotPt = adjScene.Camera.LotPoint(camPt, new Vector2(lw, lh));
                int tx = (int)MathF.Floor(lotPt.X);
                int ty = (int)MathF.Floor(lotPt.Y);
                float clipMinX = -3f, clipMinY = -3f, clipMaxX = lw + 3f, clipMaxY = lh + 3f;
                if (NeighborhoodWorldBlueprint.Default.Parcels.TryGetValue(entry.House, out var adjParcel))
                {
                    void ClipPick(int edge)
                    {
                        float c = adjParcel.GetRoadCenterLocal(edge);
                        switch (edge)
                        {
                            case 0: clipMinY = MathF.Max(clipMinY, c); break;
                            case 2: clipMaxY = MathF.Min(clipMaxY, c); break;
                            case 3: clipMinX = MathF.Max(clipMinX, c); break;
                            case 1: clipMaxX = MathF.Min(clipMaxX, c); break;
                        }
                    }
                    ClipPick(adjParcel.PrimaryRoadEdge);
                    if (adjParcel.SecondaryRoadEdge is int sec)
                        ClipPick(sec);
                }
                if (lotPt.X >= clipMinX && lotPt.X <= clipMaxX && lotPt.Y >= clipMinY && lotPt.Y <= clipMaxY)
                {
                    int? pickedObj = sub.EnsurePicker()?.ObjectAt(adjScenePoint, EffectiveVisualZoom);
                    bool insideFootprint = tx >= 0 && tx < lw && ty >= 0 && ty < lh;
                    if (insideFootprint || pickedObj is > 0)
                    {
                        return new AdjacentLotPick(
                            entry.House,
                            entry.LotName,
                            entry.FamilyName,
                            entry.Occupied,
                            entry.HasDoor,
                            pickedObj,
                            new GroundTile(Math.Clamp(tx, 0, lw - 1), Math.Clamp(ty, 0, lh - 1)),
                            entry.IsCommunity);
                    }
                }
            }
        }

        return null;
    }
}
