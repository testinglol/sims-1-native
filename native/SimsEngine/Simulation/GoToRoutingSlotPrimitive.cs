namespace SimsEngine.Simulation;

/// <summary>Routes a Sim to a specific slot on the target object or lot route table, resolving destination
/// regions from authored SLOT resources.</summary>
public static class GoToRoutingSlotPrimitive
{
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
