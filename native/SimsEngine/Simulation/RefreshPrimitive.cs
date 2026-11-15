namespace SimsEngine.Simulation;

/// <summary>The Refresh primitive (opcode 7), after Sims.exe 0x451d3f (not in the decompilation; read from the
/// disassembly). Word 0 of the operand is the subject: 0 is the running object, 1 the stack object. Word 2 is the
/// selector: 0 the display refresh, 1 a notice, 2 a room refresh. Any other value of either word raises "Bad parameter to
/// update primitive." (3).
/// <para>Sims.exe checks the subject mode first, so a bad mode raises error 3 before any object is looked up. A stack
/// object that is missing then raises "No stack object present when required." (21). Only after the subject does it
/// check the selector, so a missing stack object wins over a bad selector.</para>
/// <para>Selector 0 records a native display-refresh request (FUN_004341a6 with 0) and forwards it to the host when
/// present. Selector 1 sends notice 0xF2 with the subject's id through the native FUN_0040766a listener chain.
/// Selector 2 refreshes the subject's Room field (<see cref="ObjectData.Room"/>) unless it is -5, which is no room
/// (FUN_00466bdf). When the runtime room-manager tree is available, a record is marked dirty in the world and a missing
/// key is a no-op; a world without that tree and without a host has no room to mark, so it is a no-op. The primitive always answers true.</para>
/// <para>Inferred: the running object is never null here, so Sims.exe's error 3 for a null running object has no port
/// case.</para></summary>
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
