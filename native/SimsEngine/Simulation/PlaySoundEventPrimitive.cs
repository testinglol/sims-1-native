using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Play Sound Event primitive (opcode 0x17), after Sims.exe FUN_00452862.
/// <para>Operand: the sound event id (a short at byte 0). The flags (byte 4): 0xFF, or a value with bit 1 clear, makes the
/// running object the owner; otherwise the stack object is (the handler tests bit 1 of byte 4). The volume and sample
/// rate bytes are not read, as the original does not read them either.</para>
/// <para>Behavior: the event is tried in sound bank 0 and then bank 1. When a bank accepts it, the positional part is played
/// for the owner (<see cref="ISimulationHost.PlayPositionalSound"/>). When neither bank accepts it, nothing more happens.
/// The primitive always answers true.</para>
/// <para>Not modelled: the debug logging the handler does when a debug flag is set (DAT_006e504c, DAT_006f29f4); the
/// FUN_00452bb4 call before the play; and the branch after a play that calls FUN_004a6ba5 for a person (definition type 2)
/// when the global flag at DAT_006e486c+0x5b is set (the flag's meaning is not traced, so the port skips the branch).</para></summary>
public static class PlaySoundEventPrimitive
{
    private const int BankOne = 0;
    private const int BankTwo = 1;
    private const byte AnyOwnerFlags = 0xFF;
    private const byte StackObjectOwnerMask = 2;
    private const int FlagsField = 4;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var eventId = (short)(operand[0] | operand[1] << 8);
        var flags = operand[FlagsField];
        var useMe = flags == AnyOwnerFlags || (flags & StackObjectOwnerMask) == 0;
        var ownerId = useMe
            ? me.Id
            : context.StackObject?.Id ?? (!me.HasRequestKey ? frame.StackObject : (short)0);
        // Audio is a presentation effect; a hostless world simply plays nothing and the primitive still answers true.
        var host = world.Host;
        if (host is not null)
        {
            if ((host.PlaySoundEvent(BankOne, eventId) || host.PlaySoundEvent(BankTwo, eventId)) && ownerId != 0)
                host.PlayPositionalSound(ownerId);
        }
        else if (world.PlaySoundEvent is { } play && frame.Table is Content.BehaviorScope scope)
        {
            // FUN_00401f6f is the executing code owner's IFF (private or semi-global); FUN_00401f73 is the global/object IFF.
            // Sims.exe tries those sound banks in this order and the FWAV chunk names the HIT event.
            var id = unchecked((ushort)eventId);
            var (file, chunk) = scope.Private.Find(ResourceType.Fwav, id) is { } privateChunk
                ? (scope.Private, privateChunk)
                : scope.SemiGlobal?.Find(ResourceType.Fwav, id) is { } semiGlobalChunk
                    ? (scope.SemiGlobal, semiGlobalChunk)
                    : (scope.Global, scope.Global.Find(ResourceType.Fwav, id));
            if (chunk is not null)
            {
                var reader = file.Reader(chunk);
                var name = reader.CString();
                var pan = flags == AnyOwnerFlags || (flags & 8) == 0;
                var zoom = flags == AnyOwnerFlags || (flags & 4) == 0;
                play(name, ownerId, pan, zoom);
            }
        }
        return PrimitiveResult.True;
    }
}
