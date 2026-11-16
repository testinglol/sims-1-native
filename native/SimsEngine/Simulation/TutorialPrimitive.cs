namespace SimsEngine.Simulation;

/// <summary>Claims or releases the lot's tutorial ownership slot (opcode 0x0A), dismissing dialog records
/// owned by any prior holder and broadcasting a tutorial change notice.</summary>
public static class TutorialPrimitive
{
    private const byte ClaimMode = 0;
    private const byte ReleaseMode = 1;
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
