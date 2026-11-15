namespace SimsEngine.Simulation;

/// <summary>The Go To Relative Position primitive (opcode 0x1b), after Sims.exe FUN_0045c7a0 (labelled "Go To Relative
/// Position" in Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running
/// object is a person.
/// <para>Operand: byte 2, the direction (signed); byte 3, the facing (signed); byte 6, the flags. Bytes 0, 1, 4, 5 and 7
/// are not read.</para>
/// <para>A waiting frame (<see cref="StackFrame.Waiting"/>) continues its route with no target
/// (<see cref="ISimulationHost.Route"/>). Otherwise a routing slot is built with the defaults of FUN_0046de64
/// (<see cref="RoutingSlotRequest"/>). Standing is 1 (FUN_0046dfa0). FUN_0046df67 multiplies the 1, 1, 1 tile
/// proximities by 16 before storing them; the default direction branch uses 16, 32, 16. Flag 0x4000 is set when byte 6 bit 1 is clear and flag 0x8000 when bit 2
/// is set (the flags are written to byte 0x1d of the slot).</para>
/// <para>Direction (the low byte of the flags; FUN_0046df0d sets one bit): 0, 2, 4 and 6 set the bit of their own number;
/// 1, 3, 5 and 7 set the two bits either side of it (1 sets 0 and 2, 7 sets 6 and 0); -2 clears the direction bits
/// (FUN_0046df08); any other value sets bits 0, 2, 4 and 6 and makes the proximities 1, 2 and 1 (FUN_0046df67's default
/// branch). Facing: -2 gives -2 (FUN_0046df34), -1 gives -3 (FUN_0046df2c), 0 to 7 gives that value (FUN_0046df3c). Any
/// other facing answers false, with no route (FUN_0045451c only prints the debugger's stack message, not ported).</para>
/// <para>FUN_0045c7a0 forwards availability execution to FUN_0045aecf; it has no interaction-test-mode early return.</para>
/// <para>Otherwise the route answers what <see cref="ISimulationHost.Route"/> answers. FUN_0045aecf's result is the
/// handler's, so Again and Stop pass through unchanged.</para></summary>
public static class GoToRelativePositionPrimitive
{
    // FUN_0046df0d: sets bit <paramref name="direction"/> of the flags. Only directions 0 to 7 are used here.
    private static void AddDirection(RoutingSlotRequest slot, int direction) => slot.Flags |= 1u << direction;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        if (frame.Waiting)
            return world.Host is { } waitingHost
                ? waitingHost.Route(me, frame, null)
                : RouteExecutor.Continue(world, me, frame, context.Stack);
        // FUN_0046de64 (the defaults), then FUN_0046dfa0(0, 1) (Standing) and FUN_0046df67(1, 1, 1) (the proximities).
        var slot = new RoutingSlotRequest
        {
            Standing = 1, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16,
        };
        if ((operand[6] & 2) == 0) slot.Flags |= 0x4000;
        if ((operand[6] & 4) != 0) slot.Flags |= 0x8000;
        // FUN_004549b6 extracts operand byte 6 bit 2 and passes it as FUN_004550e0's final argument.
        slot.CheckCandidateFootprintAgainstWall = (operand[6] & 4) != 0;

        var direction = (sbyte)operand[2];
        switch (direction)
        {
            case -2:
                slot.Flags &= ~0xffu;
                break;
            case 1 or 3 or 5 or 7:
                AddDirection(slot, direction - 1);
                AddDirection(slot, (direction + 1) % 8);
                break;
            case 0 or 2 or 4 or 6:
                AddDirection(slot, direction);
                break;
            default:
                AddDirection(slot, 0);
                AddDirection(slot, 2);
                AddDirection(slot, 4);
                AddDirection(slot, 6);
                slot.MaxProximity = 32;
                break;
        }

        var facing = (sbyte)operand[3];
        switch (facing)
        {
            case -2:
                slot.Facing = -2;
                break;
            case -1:
                slot.Facing = -3;
                break;
            case >= 0 and <= 7:
                slot.Facing = facing;
                break;
            default:
                return PrimitiveResult.False;
        }
        if (world.Host is { } host) return host.Route(me, frame, slot);
        if (context.StackObject is not { } anchor || !world.RoutingEnabled)
            return PrimitiveResult.False;
        return RouteExecutor.Start(world, me, frame, anchor, slot, context.Stack);
    }
}
