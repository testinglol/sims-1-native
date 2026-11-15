namespace SimsEngine.Simulation;

/// <summary>The Go To Routing Slot primitive (opcode 0x2d), after Sims.exe FUN_0045ad3a (labelled "Go To Routing Slot" in
/// Behavior.iff STR# 139). Sims.exe reaches it through the person dispatcher FUN_0045cd1f, so the running object is a person.
/// <para>Operand: bytes 0 and 1, a short index; bytes 2 and 3, a short mode; byte 4, the flags. Bytes 5 to 7 are not read.</para>
/// <para>A waiting frame (<see cref="StackFrame.Waiting"/>) continues its route with no target
/// (the route executor). Otherwise the frame's stack object must exist, or the primitive raises error 52
/// (<see cref="SimulationError.ObjectForRoutingDoesNotExistOrIsOutOfBounds"/>). Routing must run
/// (<see cref="SimulationWorld.RoutingEnabled"/>); if it does not the answer is false, with nothing else done.</para>
/// <para>The mode picks the slot. Mode 0 reads the index from the frame's parameter at the index, and that short is the
/// slot of the stack object. Mode 1 uses the index as the slot of the stack object's source-loaded type-3 SLOT vector.
/// Mode 2 uses the index as a slot of the native lot route table loaded from SLOT resource 100. Any other mode, and a slot
/// outside the vector (negative, or past its end), raises error 37
/// (<see cref="SimulationError.InvalidRoutingSlotSpecified"/>). Sims.exe's first check on the mode 0 parameter (a negative
/// index with a parameter count not above it) never holds for a byte count, so it is not repeated. Sims.exe
/// then reads the parameter without a bound check; here a parameter past the frame's parameters raises error 8
/// (<see cref="SimulationError.StackNumberOutOfRange"/>), a port choice.</para>
/// <para>The slot's flag 0x4000 is cleared, then set again when byte 4 bit 0 is clear. The slot is changed in place, as Sims.exe
/// changes the object's slot. The route then runs with that slot, and the answer is the route's. Opcode 0x2d enters
/// FUN_0045ad3a and passes the resolved request to FUN_00483572, which samples directional endpoint regions from the
/// SLOT's authored min/max range, resolution, and direction mask. The native route search checks candidate placement
/// and reachability, then pushes a crossing portal's function 0x0f before continuing.</para></summary>
public static class GoToRoutingSlotPrimitive
{
    // Flag 0x4000 of the slot (bit 6 of byte 0x1d in Sims.exe).
    private const uint RouteFlag = 0x4000;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        if (frame.Waiting) return Route(world, me, frame, null, context.Stack);
        var stackObject = context.StackObject ?? throw new SimulationException(SimulationError.ObjectForRoutingDoesNotExistOrIsOutOfBounds);
        var host = world.Host;
        if (host is not null ? !host.RoutingEnabled() : !world.RoutingEnabled) return PrimitiveResult.False;

        var index = (short)(operand[0] | operand[1] << 8);
        var mode = (short)(operand[2] | operand[3] << 8);
        var slot = mode switch
        {
            0 => StackSlot(stackObject, ParameterValue(context, index)),
            1 => StackSlot(stackObject, index),
            2 => host is null ? LotSlot(world, index) : LotSlot(host, me, index),
            _ => throw new SimulationException(SimulationError.InvalidRoutingSlotSpecified),
        };

        slot.Flags &= ~RouteFlag;
        if ((operand[4] & 1) == 0) slot.Flags |= RouteFlag;
        return world.Host is { } routeHost
            ? routeHost.Route(me, frame, slot)
            : RouteExecutor.Start(world, me, frame, stackObject, slot, context.Stack);
    }

    private static PrimitiveResult Route(SimulationWorld world, XObject me, StackFrame frame, RoutingSlotRequest? slot, TreeStack? stack) =>
        world.Host is { } host
            ? host.Route(me, frame, slot)
            : RouteExecutor.Continue(world, me, frame, stack);

    // Mode 0: the frame's parameter at the index. Past the parameters, Sims.exe reads the memory after them; the port raises
    // error 8 instead (a port choice).
    private static short ParameterValue(in TreeContext context, short index)
    {
        var parameters = context.Frame.Parameters;
        return index >= 0 && index < parameters.Length ? parameters[index] : throw new SimulationException(SimulationError.StackNumberOutOfRange);
    }

    private static RoutingSlotRequest StackSlot(XObject stackObject, int index) =>
        index >= 0 && index < stackObject.RoutingSlots.Count
            ? stackObject.RoutingSlots[index]
            : throw new SimulationException(SimulationError.InvalidRoutingSlotSpecified);

    private static RoutingSlotRequest LotSlot(ISimulationHost host, XObject me, int index) =>
        index >= 0 && index < host.LotRoutingSlotCount(me)
            ? host.LotRoutingSlotAt(me, index)
            : throw new SimulationException(SimulationError.InvalidRoutingSlotSpecified);

    private static RoutingSlotRequest LotSlot(SimulationWorld world, int index) =>
        index >= 0 && index < world.LotRoutingSlots.Count
            ? world.LotRoutingSlots[index]
            : throw new SimulationException(SimulationError.InvalidRoutingSlotSpecified);
}
