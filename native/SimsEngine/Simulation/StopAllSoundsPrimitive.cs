namespace SimsEngine.Simulation;

/// <summary>The Stop All Sounds primitive (opcode 0x30), after Sims.exe 0x45257a (read from the disassembly; no decompilation).
/// <para>Operand: word 0 names the owner. 0 is the running object; any other value is the stack object's id, taken as it is
/// (whether or not that object is on the lot). The sounds of that owner are stopped (<see cref="ISimulationHost.StopOwnerSounds"/>,
/// FUN_0057a9a6). The primitive always answers true.</para>
/// <para>Name: the primitive does not stop every sound. It stops the sounds of one owner.</para>
/// <para>Not modelled: the debug trace the handler writes when the debug flag at DAT_006e504c is set. It changes no game state
/// and is left out, as <see cref="PlaySoundEventPrimitive"/> leaves out its own.</para>
/// </summary>
public static class StopAllSoundsPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var word0 = (short)(operand[0] | operand[1] << 8);
        var ownerId = word0 == 0
            ? me.Id
            : context.StackObject?.Id ?? (!me.HasRequestKey ? frame.StackObject : (short)0);
        // Owner audio is presentation only; a hostless world stops nothing and the primitive still answers true.
        if (ownerId != 0)
        {
            if (world.Host is { } host) host.StopOwnerSounds(ownerId);
            else world.StopOwnerSounds?.Invoke(ownerId);
        }
        return PrimitiveResult.True;
    }
}
