namespace SimsEngine.Simulation;

/// <summary>The Snapshot primitive (opcode 0x2e), after Sims.exe 0x4549b6 (read from the decompilation). It works out a position
/// near the stack object for the running object and runs the search for it (<see cref="ISimulationHost.RunSnapshot"/>, FUN_004550e0).
/// <para>Operand: word 0 is the parameter index that mode 0 reads (not ported with the other list modes). Word 1 is the mode.
/// Byte 4 are the flags: bit 0 asks for the difference from the running object, bit 1 selects the avatar tail the host runs
/// (Sims.exe reads it after the search), and bit 2 is the slot flag.</para>
/// <para>Order: the stack object must exist ("No stack object present when required.", 21). Then the mode:</para>
/// <list type="bullet">
/// <item>Mode 1 uses the stack object's position and passes it as the relative object with slot index 0, so the running
/// object is placed into the stack object's first real footprint slot.</item>
/// <item>Mode 2 is the stack object's position plus its footprint offset for its direction (<see cref="LotGeometry.FootprintOffset"/>,
/// FUN_004222bd, added as Sims.exe adds it). There is no target.</item>
/// <item>Mode 0 validates word 0 as a frame parameter index (error 8), then uses that parameter as an index into the stack
/// object's routing-slot list. Modes 3 and 4 use word 0 directly as a slot index; mode 3 reads the stack object's routing
/// slots and mode 4 reads its lot-slot table. Each list miss is error 27. Free slots with no snap target project their local
/// offsets through <see cref="LotGeometry.SlotPosition"/>. Snap-target entries use the target's 0x24-byte footprint-slot records.</item>
/// <item>Any other mode raises "Invalid snapping mode specified." (36).</item>
/// </list>
/// <para>With flag bit 0, the difference from the running object's position is passed (its Y, then its X, and its direction
/// minus the selected direction).</para>
/// <para>Native lot execution sends both free-position and footprint-slot results through the corresponding native
/// placement validator and apply path.</para>
/// <para>Inferred: the name. The primitive finds a position and does not take a picture.</para>
/// </summary>
public static class SnapshotPrimitive
{
    private const short StackObjectMode = 1;
    private const short FrontMode = 2;
    private const byte DifferenceFlag = 1;
    private const byte SlotFlag = 4;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, _) = context;
        var stackObject = context.StackObject ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var mode = (short)(operand[2] | operand[3] << 8);
        var flags = operand[4];
        XObject? target;
        ObjectPosition position;
        var snapTargetSlot = -1;
        var snapTargetFlag = false;
        var directionMask = me[ObjectData.Direction] & 7;
        switch (mode)
        {
            case StackObjectMode:
                target = stackObject;
                position = stackObject.Position;
                snapTargetSlot = 0; // FUN_004549b6 supplies the stack object and slot zero to FUN_004550e0.
                break;
            case FrontMode:
                target = null;
                directionMask = stackObject[ObjectData.Direction];
                var offset = LotGeometry.FootprintOffset(stackObject[ObjectData.Direction]);
                position = stackObject.Position with { X = stackObject.Position.X + offset.X, Y = stackObject.Position.Y + offset.Y };
                break;
            case 0:
            {
                var parameterIndex = (short)(operand[0] | operand[1] << 8);
                if (parameterIndex < 0 || parameterIndex >= context.Frame.Parameters.Length)
                    throw new SimulationException(SimulationError.StackNumberOutOfRange);
                var slotIndex = context.Frame.Parameters[parameterIndex];
                if (slotIndex < 0 || slotIndex >= stackObject.RoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, stackObject.RoutingSlots[slotIndex]);
                break;
            }
            case 3:
            {
                var slotIndex = (short)(operand[0] | operand[1] << 8);
                if (slotIndex < 0 || slotIndex >= stackObject.RoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, stackObject.RoutingSlots[slotIndex]);
                break;
            }
            case 4:
            {
                var slotIndex = (short)(operand[0] | operand[1] << 8);
                if (world.Host is { } slotHost)
                {
                    if (slotIndex < 0 || slotIndex >= slotHost.LotRoutingSlotCount(stackObject))
                        throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                    (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                        ProjectSlot(world, me, stackObject, slotHost.LotRoutingSlotAt(stackObject, slotIndex));
                    break;
                }
                if (slotIndex < 0 || slotIndex >= world.LotRoutingSlots.Count)
                    throw new SimulationException(SimulationError.SlotNumberOutOfRange);
                // FUN_004549b6 mode 4 indexes the lot's +0x9c slot table through the stack object's shared manager.
                // SavedLotObjectLoader restores this same table from the lot's SLOT resource.
                (target, position, snapTargetSlot, snapTargetFlag, directionMask) =
                    ProjectSlot(world, me, stackObject, world.LotRoutingSlots[slotIndex]);
                break;
            }
            default:
                throw new SimulationException(SimulationError.InvalidSnappingModeSpecified);
        }
        (int Dy, int Dx, int Ddir)? relative = (flags & DifferenceFlag) != 0
            ? (me.Position.Y - position.Y, me.Position.X - position.X,
                me[ObjectData.Direction] - directionMask)
            : null;
        if (world.Host is { } host)
            return host.RunSnapshot(me, target, position, snapTargetSlot, snapTargetFlag, directionMask, (flags & SlotFlag) != 0, relative);
        var (displacementX, displacementY, displacementDirection) =
            ComputeAnimatorDisplacements(me, target, position, snapTargetSlot, directionMask, flags);
        world.ResetPlacementFailure();
        PrimitiveResult result;
        if (snapTargetSlot >= 0 && target is not null)
            result = SnapshotExecutor.PlaceInFootprintSlot(world, me, target, snapTargetSlot, directionMask,
                snapTargetFlag, (flags & SlotFlag) != 0, context.Stack);
        else if (target is null)
            result = SnapshotExecutor.PlaceAtFreePosition(world, me, position, directionMask, snapTargetFlag, context.Stack);
        else
            throw new NotSupportedException("Native Snapshot candidate search is not implemented for this mode/slot.");
        if (result == PrimitiveResult.True && me is XPerson person)
            person.SetAnimatorDisplacements(displacementX, displacementY, displacementDirection);
        return ApplyFailureTail(world, me, flags, result);
    }

    /// <summary>FUN_004549b6 (004549b6.c:159-228): records pre-placement position/direction deltas when flag bit 0 is set,
    /// adds the rotated footprint-slot (X, Y) offset when snapping into a target footprint slot, and writes them to
    /// SAnimator::SetAnimDisplacements (vtable +0x2c) on a successful person placement.</summary>
    private static (int X, int Y, int Direction) ComputeAnimatorDisplacements(
        XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, int directionMask, byte flags)
    {
        var dx = (flags & DifferenceFlag) != 0 ? me.Position.X - position.X : 0;
        var dy = (flags & DifferenceFlag) != 0 ? me.Position.Y - position.Y : 0;
        var ddir = (flags & DifferenceFlag) != 0 ? me[ObjectData.Direction] - directionMask : 0;
        if (target is not null && (uint)snapTargetSlot < (uint)target.FootprintSlots.Count)
        {
            var slot = target.FootprintSlots[snapTargetSlot];
            var (slotOffsetX, slotOffsetY) = (target[ObjectData.Direction] & 7) switch
            {
                0 => ((int)slot.X, (int)slot.Y),
                2 => ((int)-slot.Y, (int)slot.X),
                4 => ((int)-slot.X, (int)-slot.Y),
                6 => ((int)slot.Y, (int)-slot.X),
                _ => (0, 0),
            };
            dx += slotOffsetX;
            dy += slotOffsetY;
        }
        return (dx, dy, ddir);
    }

    /// <summary>The tail of FUN_004549b6 after FUN_004550e0 answers. It clears the running object's primitive result
    /// (+0xb2) and, when the placement failed, records why: result 1 when the flag bit 1 is set, the failure is the
    /// 0x0b overlap, the blocker is a Sim and the Sim in the way accepts the request to move
    /// (<see cref="RouteExecutor.MoveOutOfWay"/>, FUN_0045beca, at the running Sim's current action priority);
    /// otherwise result 2 for failure code 1. The blocker's object id goes to +0xb6, or 0 when there is none.</summary>
    private static PrimitiveResult ApplyFailureTail(SimulationWorld world, XObject me, byte flags, PrimitiveResult result)
    {
        me[ObjectData.PrimitiveResult] = 0;
        if (result == PrimitiveResult.True) return result;

        var blocker = world.LastPlacementBlockingObjectId == 0 ? null
            : world.WalkOrder.FirstOrDefault(candidate => candidate.ClockManagerIndex == world.LastPlacementBlockingObjectId);
        if (me is XPerson person && (flags & 2) != 0 && world.LastPlacementFailureCode == 0x0b &&
            blocker is XPerson blockerPerson &&
            RouteExecutor.MoveOutOfWay(world, blockerPerson, person.CurrentActionRecord?.Threshold ?? 0))
            me[ObjectData.PrimitiveResult] = 1;
        if (me[ObjectData.PrimitiveResult] == 0 && world.LastPlacementFailureCode == 1)
            me[ObjectData.PrimitiveResult] = 2;
        me[ObjectData.PrimitiveResultId] = blocker is null ? (short)0 : blocker.ClockManagerIndex;
        return result;
    }

    /// <summary>FUN_004549b6's valid-entry path for modes 0, 3, and 4. A negative snap-target index projects the local
    /// offset here; a nonnegative index validates against the stack object's 0x24-byte footprint-slot vector (error 32)
    /// and is passed with its source flag.</summary>
    private static (XObject? Target, ObjectPosition Position, int SnapTargetSlot, bool SnapTargetFlag, int Direction) ProjectSlot(
        SimulationWorld world, XObject me, XObject stackObject, RoutingSlotRequest slot)
    {
        var snapTargetFlag = (slot.Flags & 0x800) != 0;
        short direction;
        if ((slot.Flags & 0x1000) == 0)
        {
            direction = me[ObjectData.Direction];
        }
        else
        {
            var allowed = (byte)slot.Flags;
            var relativeDirection = 0;
            while (relativeDirection < 7 && (allowed & (1 << relativeDirection)) == 0)
                relativeDirection++;
            if (relativeDirection == 7) relativeDirection = 0;
            direction = (short)(stackObject[ObjectData.Direction] + relativeDirection);
        }

        if (slot.SnapTargetSlot >= 0)
        {
            if ((world.Host is null || stackObject.FootprintSlots.Count > 0) &&
                (uint)slot.SnapTargetSlot >= (uint)stackObject.FootprintSlots.Count)
                throw new SimulationException(SimulationError.BadTargetSlotSpecifiedInRoutingSlot);
            return (stackObject, stackObject.Position, slot.SnapTargetSlot, snapTargetFlag, direction & 7);
        }

        return (null, LotGeometry.SlotPosition(stackObject.Position,
            stackObject[ObjectData.Direction], slot.X, slot.Y), -1, snapTargetFlag, direction & 7);
    }
}
