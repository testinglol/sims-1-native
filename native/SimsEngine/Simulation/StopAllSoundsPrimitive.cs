namespace SimsEngine.Simulation;

/// <summary>Stops all sounds attached to a specific owner object rather than globally, targeting either the
/// running object or the stack object.</summary>
public static class StopAllSoundsPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var word0 = (short)(operand[0] | operand[1] << 8);
        var ownerId = word0 == 0
            ? me.Id
            : context.StackObject?.Id ?? (!me.HasRequestKey ? frame.StackObject : (short)0);
        if (ownerId != 0)
        {
            if (world.Host is { } host) host.StopOwnerSounds(ownerId);
            else world.StopOwnerSounds?.Invoke(ownerId);
        }
        return PrimitiveResult.True;
    }
}
