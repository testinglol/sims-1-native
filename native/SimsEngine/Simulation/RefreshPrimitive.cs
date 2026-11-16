namespace SimsEngine.Simulation;

/// <summary>Executes the Refresh primitive (opcode 7) to request a display redraw (selector 0), dispatch
/// notification 0xF2 (selector 1), or recalculate the object's room assignment (selector 2).</summary>
public static class RefreshPrimitive
{
    private const short NoRoom = -5;
    private const int RefreshNotice = 0xF2;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var mode = (short)(operand[0] | operand[1] << 8);
        var selector = (short)(operand[2] | operand[3] << 8);
        if (mode != 0 && mode != 1) throw new SimulationException(SimulationError.BadParameterToUpdatePrimitive);
        var subject = mode == 0 ? me : world.ObjectByStackObject(me, frame.StackObject) ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        if (selector is < 0 or > 2) throw new SimulationException(SimulationError.BadParameterToUpdatePrimitive);
        switch (selector)
        {
            case 0:
                world.RequestDisplayRefresh();
                break;
            case 1:
                world.DispatchNotice(RefreshNotice, subject.Id);
                break;
            case 2:
            {
                var room = subject[ObjectData.Room];
                if (room != NoRoom)
                {
                    var roomId = unchecked((ushort)room);
                    var refresh = world.RefreshRoomRecord(roomId);
                    if (refresh == RoomRefreshResult.ManagerUnavailable && world.Host is { } host)
                        host.RefreshRoom(roomId);
                }
                break;
            }
        }
        return PrimitiveResult.True;
    }
}
