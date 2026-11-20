using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsEngine.Simulation.Neighborhood;

public enum CrossLotTransitPhase
{
    None = 0,
    RoutingToGateway = 1,
    OffLotSeamWalk = 2,
    ArrivingOnDestination = 3,
    Completed = 4,
}

public readonly record struct CrossLotTransitVisualOverride(
    float TileX,
    float TileY,
    float Direction,
    float SeamProgress,
    short FromHouse,
    short ToHouse,
    float? WorldRelTileX = null,
    float? WorldRelTileY = null);

public sealed class CrossLotTransitState
{
    public short SimNeighborId { get; init; }
    public short SimObjectId { get; set; }
    public short SourceHouse { get; init; }
    public short TargetHouse { get; init; }
    public bool IsReturnHome { get; init; }
    public bool KnockOnArrival { get; init; }
    public List<short> CompanionNeighborIds { get; init; } = [];
    public CrossLotTransitPhase Phase { get; set; }
    public ObjectPosition SourceGatewayTile { get; set; }
    public ObjectPosition DestinationEntryTile { get; set; }
    public ObjectPosition DestinationArrivalTarget { get; set; }
    public float SeamProgress { get; set; }
    public int StageTicksElapsed { get; set; }
    public int MaxStageTicks { get; set; } = 90;
    public List<(float WorldX, float WorldY)> WorldWaypoints { get; init; } = [];
    public int WaypointIndex { get; set; }
    public float CurrentWorldX { get; set; }
    public float CurrentWorldY { get; set; }
    public float TotalWorldDistance { get; set; }
    public float TraveledWorldDistance { get; set; }
    public ObjectPosition DestinationGatewayTile { get; set; }
}

public static class GatewayRouting
{
    public const uint PedestrianPortalGuid = 0x81E6BEF9u;
    private static readonly ResourceType ArryType = ResourceType.Parse("Arry");

    public static void EnsureTransitWalkAnimation(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);

        if (person.AnimationPlayback is { RouteDriven: false, Loop: true })
            return;

        int walkSkill = person[ObjectData.WalkStyle] == 1 ? 21 : 20;
        var clip = RouteExecutor.ResolveWalkAnimation(world, person, walkSkill)
            ?? RouteExecutor.ResolveWalkAnimation(world, person, 20);
        if (clip is null)
            return;

        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: true, startedAtMils: person.AnimatorMilliseconds)
        {
            RouteDriven = false,
            Interruptible = true,
        };
        world.BeginPrimaryAnimation(person, playback);
    }

    public static void ClearTransitWalkAnimation(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);
        if (person.AnimationPlayback is { RouteDriven: false, Loop: true })
            world.StopCurrentAnimation(person);
    }

    public static XObject? FindPrimaryGatewayPortal(SimulationWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        XObject? secondaryPortal = null;
        XObject? mailboxFallback = null;
        foreach (var obj in world.WalkOrder)
        {
            var guid = unchecked((uint)(obj.TypeRecord?.Key ?? 0));
            if (guid == PedestrianPortalGuid)
                return obj;
            if (secondaryPortal is null && RouteExecutor.IsGatewayPortal(obj))
                secondaryPortal = obj;
            if (mailboxFallback is null && IsMailboxObject(obj))
                mailboxFallback = obj;
        }

        return secondaryPortal ?? mailboxFallback;
    }

    public static ObjectPosition ResolveWalkableSidewalkTile(
        SimulationWorld world,
        IffFile? lot,
        XPerson sim,
        int spreadIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sim);

        var (focusX, focusY) = FindApproachFocus(world);

        if (lot is not null && TryFindSidewalkTileFromFloors(world, lot, sim, focusX, focusY, spreadIndex, requireInsideLot: false, out var sidewalkFloorPos))
            return sidewalkFloorPos;

        var orderedPortals = world.WalkOrder
            .Where(static obj => unchecked((uint)(obj.TypeRecord?.Key ?? 0)) == PedestrianPortalGuid)
            .OrderBy(obj =>
            {
                var dx = (obj.Position.X >> 4) - focusX;
                var dy = (obj.Position.Y >> 4) - focusY;
                return dx * dx + dy * dy;
            });

        foreach (var obj in orderedPortals)
        {
            var ptx = Math.Clamp(obj.Position.X >> 4, 1, LotGeometry.LotDimension - 2);
            var pty = Math.Clamp(obj.Position.Y >> 4, 1, LotGeometry.LotDimension - 2);
            if (TryFindWalkableNear(world, sim, ptx, pty, maxRadius: 6, spreadIndex, requireInsideLot: false, out var portalPos))
                return portalPos;
        }

        var fallbackPortal = FindPrimaryGatewayPortal(world);
        if (fallbackPortal is not null)
        {
            var ptx = Math.Clamp(fallbackPortal.Position.X >> 4, 1, LotGeometry.LotDimension - 2);
            var pty = Math.Clamp(fallbackPortal.Position.Y >> 4, 1, LotGeometry.LotDimension - 2);
            if (TryFindWalkableNear(world, sim, ptx, pty, maxRadius: 6, spreadIndex, requireInsideLot: false, out var fbPos))
                return fbPos;
        }

        if (TryFindOutdoorPerimeterTile(world, sim, spreadIndex, requireInsideLot: true, out var innerPerimeterPos))
            return innerPerimeterPos;

        if (TryFindOutdoorPerimeterTile(world, sim, spreadIndex, requireInsideLot: false, out var outerPerimeterPos))
            return outerPerimeterPos;

        var center = LotGeometry.LotDimension / 2;
        if (TryFindWalkableNear(world, sim, center, center, maxRadius: 20, spreadIndex, requireInsideLot: true, out var centerNearby))
            return centerNearby;

        if (world.FindGoodLocation(sim, center, center, out var goodPos))
            return ClampPositionToLotBounds(goodPos, innerPlayable: true);

        return new ObjectPosition(center * 16 + 8, center * 16 + 8, 1);
    }

    public static ObjectPosition ResolveArrivalWalkTarget(
        SimulationWorld world,
        IffFile? lot,
        XPerson sim,
        ObjectPosition entryPos)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sim);

        var clampedEntry = ClampPositionToLotBounds(entryPos, innerPlayable: true);
        var entryTx = clampedEntry.X >> 4;
        var entryTy = clampedEntry.Y >> 4;
        var (focusX, focusY) = FindApproachFocus(world);
        var vecX = focusX - entryTx;
        var vecY = focusY - entryTy;
        var vecLen = MathF.Sqrt(vecX * vecX + vecY * vecY);
        if (vecLen < 1f)
            return clampedEntry;

        var maxSteps = Math.Min(6, (int)MathF.Floor(vecLen));
        for (var dist = maxSteps; dist >= 1; dist--)
        {
            var tx = Math.Clamp((int)MathF.Round(entryTx + vecX * dist / vecLen), 1, LotGeometry.LotDimension - 2);
            var ty = Math.Clamp((int)MathF.Round(entryTy + vecY * dist / vecLen), 1, LotGeometry.LotDimension - 2);
            if (tx == entryTx && ty == entryTy) continue;
            var room = world.RoomAtTile(tx, ty, 1);
            if (!world.RoomIsOutside(room)) continue;
            var candidate = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
            if (IsWalkablePlacement(world, sim, candidate, requireInsideLot: false))
                return candidate;
        }

        if (TryFindWalkableNear(world, sim, entryTx, entryTy, maxRadius: 4, spreadIndex: 1, requireInsideLot: false, out var nearPos) &&
            (nearPos.X != clampedEntry.X || nearPos.Y != clampedEntry.Y))
        {
            return nearPos;
        }

        return clampedEntry;
    }

    public static bool HasPortalLeaveStack(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);

        if (!world.SavedExecutionStacks.TryGetValue(person.Id, out var stack))
            return false;

        foreach (var frame in stack.Frames)
        {
            if (frame.TreeId is 374 or 8286 or 8293)
                return true;
            if (frame.StackObject > 0 && world.ObjectById(frame.StackObject) is { } stackObj)
            {
                var guid = unchecked((uint)(stackObj.TypeRecord?.Key ?? 0));
                if (guid == PedestrianPortalGuid || RouteExecutor.IsGatewayPortal(stackObj))
                    return true;
            }
        }

        return false;
    }

    public static bool IsNearGatewayOrPortal(
        SimulationWorld world,
        ObjectPosition pos,
        ObjectPosition gatewayPos,
        int minTileDistance = 2)
    {
        ArgumentNullException.ThrowIfNull(world);

        var minDistSq = minTileDistance * minTileDistance;
        var tx = pos.X >> 4;
        var ty = pos.Y >> 4;

        if (gatewayPos.X >= 0 && gatewayPos.Y >= 0)
        {
            var gtx = gatewayPos.X >> 4;
            var gty = gatewayPos.Y >> 4;
            var dx = tx - gtx;
            var dy = ty - gty;
            if (dx * dx + dy * dy < minDistSq)
                return true;
        }

        foreach (var obj in world.WalkOrder)
        {
            var guid = unchecked((uint)(obj.TypeRecord?.Key ?? 0));
            if (guid != PedestrianPortalGuid) continue;
            var ptx = obj.Position.X >> 4;
            var pty = obj.Position.Y >> 4;
            var dx = tx - ptx;
            var dy = ty - pty;
            if (dx * dx + dy * dy < minDistSq)
                return true;
        }

        return false;
    }

    public static ObjectPosition ResolveSafeNonPortalSidewalkTile(
        SimulationWorld world,
        IffFile? lot,
        XPerson sim,
        ObjectPosition avoidGatewayPos,
        int minTileDistance = 2)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sim);

        var portalTiles = world.WalkOrder
            .Where(static obj => unchecked((uint)(obj.TypeRecord?.Key ?? 0)) == PedestrianPortalGuid)
            .Select(static obj => (X: obj.Position.X >> 4, Y: obj.Position.Y >> 4))
            .ToList();
        var clampedAvoid = ClampPositionToLotBounds(avoidGatewayPos, innerPlayable: true);
        var avoidTx = clampedAvoid.X >> 4;
        var avoidTy = clampedAvoid.Y >> 4;

        bool IsFarEnough(int tx, int ty, int requiredDist)
        {
            var minDistSq = requiredDist * requiredDist;
            var dx = tx - avoidTx;
            var dy = ty - avoidTy;
            if (dx * dx + dy * dy < minDistSq)
                return false;
            foreach (var (px, py) in portalTiles)
            {
                var pdx = tx - px;
                var pdy = ty - py;
                if (pdx * pdx + pdy * pdy < minDistSq)
                    return false;
            }
            return true;
        }

        var arrival = ResolveArrivalWalkTarget(world, lot, sim, clampedAvoid);
        var arrTx = arrival.X >> 4;
        var arrTy = arrival.Y >> 4;
        if (arrTx is >= 1 and <= LotGeometry.LotDimension - 2 &&
            arrTy is >= 1 and <= LotGeometry.LotDimension - 2 &&
            IsFarEnough(arrTx, arrTy, minTileDistance) &&
            IsWalkablePlacement(world, sim, arrival, requireInsideLot: false))
        {
            return ClampPositionToLotBounds(arrival, innerPlayable: true);
        }

        var (focusX, focusY) = FindApproachFocus(world);
        var maxTile = LotGeometry.LotDimension - 2;
        for (var reqDist = Math.Max(1, minTileDistance); reqDist >= 1; reqDist--)
        {
            var candidates = new List<(int X, int Y, int Score)>();
            for (var radius = reqDist; radius <= 8; radius++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;
                    var tx = avoidTx + dx;
                    var ty = avoidTy + dy;
                    if (tx < 1 || ty < 1 || tx > maxTile || ty > maxTile)
                        continue;
                    if (!IsFarEnough(tx, ty, reqDist))
                        continue;
                    var candidate = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
                    if (!IsWalkablePlacement(world, sim, candidate, requireInsideLot: false))
                        continue;
                    var outsidePenalty = world.RoomIsOutside(world.RoomAtTile(tx, ty, 1)) ? 0 : 10000;
                    var focusDistSq = (tx - focusX) * (tx - focusX) + (ty - focusY) * (ty - focusY);
                    candidates.Add((tx, ty, outsidePenalty + focusDistSq));
                }
            }

            if (candidates.Count > 0)
            {
                candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));
                var best = candidates[0];
                return ClampPositionToLotBounds(new ObjectPosition(best.X * 16 + 8, best.Y * 16 + 8, 1), innerPlayable: true);
            }
        }

        return ClampPositionToLotBounds(sim.Position.X >= 0 && sim.Position.Y >= 0 ? sim.Position : arrival, innerPlayable: true);
    }

    public static ObjectPosition ClampPositionToLotBounds(ObjectPosition pos, bool innerPlayable = false)
    {
        var minCoord = innerPlayable ? 16 : 0;
        var maxCoord = innerPlayable ? (LotGeometry.LotDimension - 2) * 16 + 8 : LotGeometry.LotDimension * 16 - 1;
        var x = Math.Clamp(pos.X, minCoord, maxCoord);
        var y = Math.Clamp(pos.Y, minCoord, maxCoord);
        var height = Math.Clamp(pos.Height, (short)1, (short)2);
        return new ObjectPosition(x, y, height);
    }

    public static void CancelPersonRoutesAndActions(SimulationWorld world, XPerson person)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(person);

        world.CancelDirectPositionRoutes(person);
        for (var i = 0; i < person.ActionQueue.Length; i++)
            person.ActionQueue[i] = ActionQueueSlot.EmptyRecord();
        person.ActionQueueCurrent = 0;
        person.ActionQueueEnd = 0;
        if (person.CurrentActionRecord is { Owner: not null })
            world.ApplyStartedInteractionAction(person, ActionQueueSlot.EmptyRecord());
        else
            person.CurrentActionRecord = ActionQueueSlot.EmptyRecord();
        person.ClearPreviousActionRecord();
        person.ActiveActionCancelRequested = false;
    }

    private static bool IsMailboxObject(XObject obj)
    {
        var name = obj.TypeRecord?.Name;
        var catalog = obj.TypeRecord?.CatalogName;
        return (name is not null && name.Contains("Mailbox", StringComparison.OrdinalIgnoreCase)) ||
               (catalog is not null && catalog.Contains("Mailbox", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWalkablePlacement(SimulationWorld world, XPerson sim, ObjectPosition candidate, bool requireInsideLot)
    {
        var tx = candidate.X >> 4;
        var ty = candidate.Y >> 4;
        if (requireInsideLot && world.LotTileFlags is { } flags && !flags.InsideLot(tx, ty))
            return false;
        return world.CanPlaceObject(sim, candidate);
    }

    internal static bool TryFindWalkableNear(
        SimulationWorld world,
        XPerson sim,
        int anchorX,
        int anchorY,
        int maxRadius,
        int spreadIndex,
        bool requireInsideLot,
        out ObjectPosition found)
    {
        var minTile = 1;
        var maxTile = LotGeometry.LotDimension - 2;
        var remainingSkips = Math.Max(0, spreadIndex);
        ObjectPosition? firstValid = null;

        for (var radius = 0; radius <= maxRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    continue;
                var tx = anchorX + dx;
                var ty = anchorY + dy;
                if (tx < minTile || ty < minTile || tx > maxTile || ty > maxTile)
                    continue;

                var candidate = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
                if (!IsWalkablePlacement(world, sim, candidate, requireInsideLot))
                    continue;

                firstValid ??= candidate;
                if (remainingSkips == 0)
                {
                    found = candidate;
                    return true;
                }
                remainingSkips--;
            }
        }

        if (firstValid is { } fallback)
        {
            found = fallback;
            return true;
        }

        found = default;
        return false;
    }

    private static (int X, int Y) FindApproachFocus(SimulationWorld world)
    {
        long sumX = 0;
        long sumY = 0;
        var indoorCount = 0;
        foreach (var obj in world.WalkOrder)
        {
            if (obj.Position.Height != 1 || obj is XPerson) continue;
            if (obj[ObjectData.Room] <= 0) continue;
            var tx = obj.Position.X >> 4;
            var ty = obj.Position.Y >> 4;
            if (tx <= 0 || ty <= 0 || tx >= LotGeometry.LotDimension - 1 || ty >= LotGeometry.LotDimension - 1)
                continue;
            sumX += tx;
            sumY += ty;
            indoorCount++;
        }

        var centerX = indoorCount > 0 ? (int)(sumX / indoorCount) : LotGeometry.LotDimension / 2;
        var centerY = indoorCount > 0 ? (int)(sumY / indoorCount) : LotGeometry.LotDimension / 2;

        (int X, int Y)? bestDoor = null;
        var bestDistSq = int.MaxValue;
        foreach (var obj in world.WalkOrder)
        {
            if (obj.Position.Height != 1) continue;
            var isDoor = (obj.TypeRecord?.Name?.Contains("Door", StringComparison.OrdinalIgnoreCase) == true) ||
                         (obj.TypeRecord?.CatalogName?.Contains("Door", StringComparison.OrdinalIgnoreCase) == true) ||
                         (obj.RuntimeClass == ObjectClass.Portal && obj.Definition[(int)ObjdField.BuildModeType] == 1);
            if (!isDoor) continue;
            var tx = Math.Clamp(obj.Position.X >> 4, 1, LotGeometry.LotDimension - 2);
            var ty = Math.Clamp(obj.Position.Y >> 4, 1, LotGeometry.LotDimension - 2);
            var distSq = (tx - centerX) * (tx - centerX) + (ty - centerY) * (ty - centerY);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestDoor = (tx, ty);
            }
        }

        return bestDoor ?? (centerX, centerY);
    }

    private static bool TryFindSidewalkTileFromFloors(
        SimulationWorld world,
        IffFile lot,
        XPerson sim,
        int focusX,
        int focusY,
        int spreadIndex,
        bool requireInsideLot,
        out ObjectPosition found)
    {
        var advanced = ReadArray(lot, 11);
        var standard = ReadArray(lot, 1);
        if (advanced is null && standard is null)
        {
            found = default;
            return false;
        }

        var streetSidewalkCandidates = new List<(int X, int Y)>();
        var lotSidewalkCandidates = new List<(int X, int Y)>();
        var roadCandidates = new List<(int X, int Y)>();
        var maxTile = LotGeometry.LotDimension - 2;
        var flags = world.LotTileFlags;

        for (var y = 1; y <= maxTile; y++)
        for (var x = 1; x <= maxTile; x++)
        {
            var room = world.RoomAtTile(x, y, 1);
            if (!world.RoomIsOutside(room)) continue;

            ushort pattern = 0;
            if (advanced is not null && advanced.ElementSize >= 2 && x < advanced.Rows && y < advanced.Width)
                pattern = BitConverter.ToUInt16(advanced.Element(x, y)[..2]);
            else if (standard is not null && x < standard.Rows && y < standard.Width)
                pattern = standard.Element(x, y)[0];

            if (pattern is 10 or 11 or 12)
            {
                if (flags is not null && !flags.InsideLot(x, y))
                    streetSidewalkCandidates.Add((x, y));
                else
                    lotSidewalkCandidates.Add((x, y));
            }
            else if (pattern == 9)
            {
                roadCandidates.Add((x, y));
            }
        }

        if (TryPickCandidateList(world, sim, streetSidewalkCandidates, focusX, focusY, spreadIndex, requireInsideLot, out found))
            return true;
        if (TryPickCandidateList(world, sim, lotSidewalkCandidates, focusX, focusY, spreadIndex, requireInsideLot, out found))
            return true;
        if (TryPickCandidateList(world, sim, roadCandidates, focusX, focusY, spreadIndex, requireInsideLot, out found))
            return true;

        found = default;
        return false;
    }

    private static bool TryPickCandidateList(
        SimulationWorld world,
        XPerson sim,
        List<(int X, int Y)> candidates,
        int focusX,
        int focusY,
        int spreadIndex,
        bool requireInsideLot,
        out ObjectPosition found)
    {
        if (candidates.Count == 0)
        {
            found = default;
            return false;
        }

        candidates.Sort((a, b) =>
        {
            var da = (a.X - focusX) * (a.X - focusX) + (a.Y - focusY) * (a.Y - focusY);
            var db = (b.X - focusX) * (b.X - focusX) + (b.Y - focusY) * (b.Y - focusY);
            return da.CompareTo(db);
        });

        var skipped = 0;
        ObjectPosition? firstValid = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var (tx, ty) = candidates[i];
            var candidate = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
            if (!IsWalkablePlacement(world, sim, candidate, requireInsideLot))
                continue;

            firstValid ??= candidate;
            if (skipped >= spreadIndex)
            {
                found = candidate;
                return true;
            }
            skipped++;
        }

        if (firstValid is { } fallback)
        {
            found = fallback;
            return true;
        }

        found = default;
        return false;
    }

    private static bool TryFindOutdoorPerimeterTile(
        SimulationWorld world,
        XPerson sim,
        int spreadIndex,
        bool requireInsideLot,
        out ObjectPosition found)
    {
        var maxTile = LotGeometry.LotDimension - 2;
        var skipped = 0;
        ObjectPosition? firstValid = null;

        int[] edgeRows = [maxTile, maxTile - 1, 1, 2];
        foreach (var y in edgeRows)
        {
            for (var x = 1; x <= maxTile; x++)
            {
                var room = world.RoomAtTile(x, y, 1);
                if (!world.RoomIsOutside(room)) continue;
                var candidate = new ObjectPosition(x * 16 + 8, y * 16 + 8, 1);
                if (!IsWalkablePlacement(world, sim, candidate, requireInsideLot)) continue;
                firstValid ??= candidate;
                if (skipped >= spreadIndex)
                {
                    found = candidate;
                    return true;
                }
                skipped++;
            }
        }

        if (firstValid is { } fallback)
        {
            found = fallback;
            return true;
        }

        found = default;
        return false;
    }

    private static LotArray? ReadArray(IffFile lot, int id)
    {
        var chunk = lot.Find(ArryType, (ushort)id);
        return chunk is null ? null : LotArray.Read(lot, chunk);
    }
}
