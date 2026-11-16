using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Implements placement location searching for objects using five placement modes, supporting
/// multi-tile alignment, directional tile offset searches, and proximity lot searches. Placements are
/// validated against terrain, room, and footprint constraints before finalizing the object position.</summary>
public static class FindLocationForPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var host = world.Host;
        var native = world.LotRoomMaps.Count != 0 || host is null;
        ISimulationHost Host() => host ?? world.RequireHost();
        var stackObject = world.ObjectByStackObject(me, frame.StackObject)
            ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var flags = operand[2];
        var mode = operand[0];

        XObject? target = null;
        var x = 16;
        var y = 16;
        var height = 1;
        if ((flags & 1) != 0)
        {
            var local = operand[1];
            if (local >= frame.Locals.Length) throw new SimulationException(SimulationError.LocalOutOfRange);
            target = world.ObjectByStackObject(me, frame.Locals[local]) ?? world.ObjectById(frame.Locals[local])
                ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
            x = target.Position.X;
            y = target.Position.Y;
            height = target.Position.Height;
        }

        switch (mode)
        {
            case 1:
                if (native)
                {
                    // Unlinks and re-roots the multi-tile object chain before executing the removal
                    // callback when function 8 is defined.
                    if (stackObject.Definition[(int)ObjdField.MasterId] != 0 &&
                        (stackObject.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal) &&
                        (SimulationWorld.HasFunction(stackObject, 8) || SimulationWorld.HasFunction(me, 8)))
                        world.DetachMultiTilePart(stackObject);
                    foreach (var member in world.MultiTileMembers(stackObject))
                        world.BeginObjectRemoval(member, 1);
                    if (stackObject is XPerson removedPerson)
                        world.RefreshPersonPlacementCallbacksAfterMovement(removedPerson);
                    return PrimitiveResult.True;
                }
                if (stackObject.Definition[(int)ObjdField.MasterId] != 0 &&
                    (SimulationWorld.HasFunction(stackObject, 8) || SimulationWorld.HasFunction(me, 8)))
                    Host().AlignMultitileParts(stackObject);
                Host().FinalizePlacement(stackObject);
                return PrimitiveResult.True;
            case 2:
            {
                var place = target ?? me;
                var steps = (((place[ObjectData.Direction] + 5) % 8 - stackObject[ObjectData.Direction] + 8) % 8) / 2;
                var at = place.Position;
                if (native)
                {
                    world.RotateObjectForPlacement(stackObject, steps);
                    if (!world.CanPlaceObject(stackObject, at, context.Stack)) return PrimitiveResult.False;
                    world.PlaceObject(stackObject, at, context.Stack);
                    if (stackObject is XPerson placedPerson)
                        world.RefreshPersonPlacementCallbacksAfterMovement(placedPerson);
                    return PrimitiveResult.True;
                }
                Host().RotateForPlacement(stackObject, steps);
                if (!Host().CanPlaceAt(stackObject, at)) return PrimitiveResult.False;
                Host().PlaceAt(stackObject, at);
                Host().FinalizePlacement(stackObject);
                return PrimitiveResult.True;
            }
            case 3:
            case 4:
            {
                if (target is null) throw new SimulationException(SimulationError.StartObjectRequired);
                var direction = target[ObjectData.Direction];
                if (mode == 4) direction = (short)((direction + 2) % 8);
                var parameters = SearchParameters(x, y, height, direction, flags, hasTarget: true);
                if (native)
                {
                    if (!world.FindLocation(stackObject, parameters, out var nativeFound, context.Stack)) return PrimitiveResult.False;
                    var nativeAt = new ObjectPosition(nativeFound.X, nativeFound.Y, height);
                    if (!world.CanPlaceObject(stackObject, nativeAt, context.Stack)) return PrimitiveResult.False;
                    world.PlaceObject(stackObject, nativeAt, context.Stack);
                    if (stackObject is XPerson placedPerson)
                        world.RefreshPersonPlacementCallbacksAfterMovement(placedPerson);
                    return PrimitiveResult.True;
                }
                if (!Host().FindLocation(stackObject, parameters, out var found)) return PrimitiveResult.False;
                var at = new ObjectPosition(found.X, found.Y, height);
                if (!Host().CanPlaceAt(stackObject, at)) return PrimitiveResult.False;
                Host().PlaceAt(stackObject, at);
                Host().FinalizePlacement(stackObject);
                return PrimitiveResult.True;
            }
            default:
            {
                var parameters = SearchParameters(x, y, height, -1, flags, randomizeCenter: mode == 5, hasTarget: target is not null);
                if (native)
                {
                    // Mode 5 searches retry once without the center-room constraint if the initial search
                    // fails, whereas modes 3 and 4 perform only one search.
                    ObjectPosition nativePosition = default;
                    var nativeFound = false;
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        if (world.FindLocation(stackObject, parameters, out nativePosition, context.Stack))
                        {
                            nativeFound = true;
                            break;
                        }
                        parameters.ContinueFlag = 0;
                    }
                    if (!nativeFound) return PrimitiveResult.False;
                    var nativeAt = new ObjectPosition(nativePosition.X, nativePosition.Y, height);
                    if (!world.CanPlaceObject(stackObject, nativeAt, context.Stack)) return PrimitiveResult.False;
                    world.PlaceObject(stackObject, nativeAt, context.Stack);
                    if (stackObject is XPerson placedPerson)
                        world.RefreshPersonPlacementCallbacksAfterMovement(placedPerson);
                    return PrimitiveResult.True;
                }
                ObjectPosition at = default;
                var found = false;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (Host().FindLocation(stackObject, parameters, out var next))
                    {
                        at = new ObjectPosition(next.X, next.Y, height);
                        found = true;
                        break;
                    }
                    parameters.ContinueFlag = 0;
                }
                if (!found) return PrimitiveResult.False;
                if (!Host().CanPlaceAt(stackObject, at)) return PrimitiveResult.False;
                Host().PlaceAt(stackObject, at);
                Host().FinishLocationSearch(stackObject);
                Host().FinalizePlacement(stackObject);
                return PrimitiveResult.True;
            }
        }
    }

    private static FindLocationParams SearchParameters(int x, int y, int height, short direction, byte flags,
        bool randomizeCenter = false, bool hasTarget = false) =>
        new()
        {
            CenterX = x,
            CenterY = y,
            Height = height,
            Direction = direction,
            FlagB = (byte)(((~flags) >> 1) & 1),
            FlagC = (byte)((flags >> 2) & 1),
            RandomizeCenter = randomizeCenter,
            HasTarget = hasTarget,
        };
}
