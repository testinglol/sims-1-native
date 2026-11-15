using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Find Location For primitive (opcode 0x10), after Sims.exe FUN_0044b408 (disassembled at 0x44b408:
/// ECX is the running object, the frame holds the stack object id, ESI is the operand).
/// <para>Operand: byte 0 is the mode; with byte 2 bit 0 set, byte 1 names the frame local holding the target
/// object (an index past the locals is error 51, <see cref="SimulationError.LocalOutOfRange"/>; a missing object
/// is error 23, <see cref="SimulationError.CouldNotFindObjectReference"/>). A missing stack object is error 21
/// (<see cref="SimulationError.NoStackObjectPresentWhenRequired"/>).</para>
/// <para>Modes: 1 aligns a multitile part when the stack object's MasterId is set and the running object has
/// function 8, then invokes vtable +0x50 (the object-removal callback); 2 turns the stack object by the quadrant math on both directions
/// (vtable +0x4c) and places it at the target's tile, or the running object's without the bit-0 target; 3 and 4
/// require the bit-0 target (otherwise error 61, <see cref="SimulationError.StartObjectRequired"/>) and place it
/// past the lot search in its direction (mode 4 turned by 2); 5 and anything else place it past the lot search around the target, or tile (1, 1) without it; this branch makes
/// one initial search and at most one retry with ContinueFlag cleared (FUN_0044b408). The search writes the found tile
/// (<see cref="ISimulationHost.FindLocation"/>); its height is the target's, or level 1 without it. Every placed
/// results in modes 2 through 5 run the check (<see cref="ISimulationHost.CanPlaceAt"/>) and the action
/// (<see cref="ISimulationHost.PlaceAt"/>), then finalize (<see cref="ISimulationHost.FinalizePlacement"/>);
/// mode 1 instead runs vtable +0x50. The retried search paths also settle the search
/// (<see cref="ISimulationHost.FinishLocationSearch"/>, vtable +0x74). A refused check or a failed search
/// answers false.</para>
/// <para>Not ported natively: the lot search itself (FUN_00434895, needs the lot topology), the multitile
/// alignment (FUN_0042349d), the remaining +0x50 callback work, and the +0x74 virtual, whose state effect is untraced.
/// Inferred: the search-parameter meanings beyond the fill values, the running object as the function-8 owner,
/// and the default center (tile (1, 1)) and height (1), which fall out of the vestigial constant setup.</para></summary>
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
                // FUN_0044b408's +0x50 callback is FUN_004331d4, which removes the object from the lot position.
                if (native)
                {
                    // The source first calls FUN_0042349d for an MT object when function 8 exists; its
                    // FUN_00422909 prefix unlinks/re-roots the multitile chain before the removal callback.
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
                var parameters = SearchParameters(x, y, height, direction, flags);
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
                var parameters = SearchParameters(x, y, height, -1, flags);
                if (native)
                {
                    // FUN_0044b408 retries the default/mode-5 search once with +0x14 cleared when
                    // the center-room search fails. Modes 3/4 return after one search instead.
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
                // Sims.exe FUN_0044b408 calls FUN_00434895 once with ContinueFlag=1, then once more with
                // ContinueFlag=0 after failure. There are two searches total, not three.
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

    private static FindLocationParams SearchParameters(int x, int y, int height, short direction, byte flags) =>
        new()
        {
            CenterX = x,
            CenterY = y,
            Height = height,
            Direction = direction,
            FlagB = (byte)(((~flags) >> 1) & 1),
            FlagC = (byte)((flags >> 2) & 1),
        };
}
