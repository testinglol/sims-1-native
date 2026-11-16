using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Executes the Play Sound Event primitive (opcode 0x17) to trigger positional audio on either the
/// current object or the stack object.</summary>
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
        var host = world.Host;
        if (host is not null)
        {
            if ((host.PlaySoundEvent(BankOne, eventId) || host.PlaySoundEvent(BankTwo, eventId)) && ownerId != 0)
                host.PlayPositionalSound(ownerId);
        }
        else if (world.PlaySoundEvent is { } play && frame.Table is Content.BehaviorScope scope)
        {
            // Searches for FWAV sound chunks in private or semi-global scope before falling back to global
            // scope.
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
