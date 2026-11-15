namespace SimsEngine.Simulation;

/// <summary>The Tutorial primitive (opcode 0x0a), after Sims.exe 0x44d946 (no decompilation; read from the disassembly, which
/// ends at the Burn handler's start). It claims or releases the lot's tutorial sim, the lot table's +0xa8, through FUN_00444bf6
/// (reads it) and FUN_00444c08 (writes it).
/// <para>Operand: byte 0 is the mode.</para>
/// <list type="bullet">
/// <item>Mode 0 claims the tutorial for the running object. It is refused (answers false) when any sim holds it, the running
/// object included, because FUN_00444c08 answers 0 when both the holder and the new value are set. Otherwise the holder is set,
/// notice 0x107 is dispatched to the native listeners and the primitive answers true.</item>
/// <item>Mode 1 releases it. When the running object holds it, the holder is cleared, the notice is dispatched and the primitive
/// answers true. Otherwise it answers false.</item>
/// <item>Any other mode raises "Invalid action in tutorial primitive." (53).</item>
/// </list>
/// <para>When changing a nonempty holder, FUN_00444c08 dismisses each dialog record owned by the previous holder before
/// changing the tutorial owner and sending the notice; these UI-manager effects are delegated to the host.</para>
/// <para>Inferred: the name. The primitive does not start or show a tutorial; it only holds the lot's single tutorial slot.</para>
/// </summary>
public static class TutorialPrimitive
{
    private const byte ClaimMode = 0;
    private const byte ReleaseMode = 1;
    /// <summary>The notice FUN_00444c08 sends when the holder changes (0x107).</summary>
    public const int TutorialNotice = 0x107;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, _) = context;
        switch (operand[0])
        {
            case ClaimMode:
                if (world.TutorialOwner is not null) return PrimitiveResult.False;
                SetOwner(world, me);
                return PrimitiveResult.True;
            case ReleaseMode:
                if (world.TutorialOwner != me) return PrimitiveResult.False;
                SetOwner(world, null);
                return PrimitiveResult.True;
            default:
                throw new SimulationException(SimulationError.InvalidActionInTutorialPrimitive);
        }
    }

    /// <summary>FUN_00444c08's write: dismiss the previous holder's dialog records, then change the owner and dispatch notice 0x107.</summary>
    private static void SetOwner(SimulationWorld world, XObject? owner)
    {
        var previous = world.TutorialOwner;
        if (previous is not null)
        {
            if (world.Host is { } host)
            {
                if (host.DialogRecordState(previous) is not null) host.DismissDialogRecords(previous);
            }
            else
            {
                // FUN_00444c08 walks every lot dialog record owned by the previous tutorial holder.
                // A native-only world owns these records itself, so it must apply the same terminal
                // state without requiring the external UI host.
                foreach (var record in world.DialogRecords)
                {
                    if (!ReferenceEquals(record.Owner, previous)) continue;
                    record.State = 5;
                    record.Visible = false;
                }
            }
        }
        world.TutorialOwner = owner;
        world.DispatchNotice(TutorialNotice, 0);
    }
}
