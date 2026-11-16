namespace SimsEngine.Simulation;

/// <summary>Triggers UI flashing highlights for tutorial steps (opcode 0x22), targeting control panel
/// buttons, person portraits, or relationship icons.</summary>
public static class UiEffectPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var host = context.World.Host;
        var flash = host is null ? context.World.TutorialFlash : null;
        var flag = (operand[4] & 1) != 0;
        switch (operand[0])
        {
            case 0:
            {
                var id = Id(context, operand);
                host?.SetObjectUiEffect(id, flag);
                flash?.Invoke(TutorialFlashKind.ImageButton, id, flag);
                id = Id(context, operand);
                host?.ShowObjectUiEffect(id, flag);
                flash?.Invoke(TutorialFlashKind.PersonButton, id, flag);
                break;
            }
            case 1:
            {
                var id = Id(context, operand);
                host?.ShowObjectUiEffect(id, flag);
                flash?.Invoke(TutorialFlashKind.PersonButton, id, flag);
                break;
            }
            case 2:
            {
                var id = Id(context, operand);
                host?.ShowEntryUiEffect(id, flag);
                flash?.Invoke(TutorialFlashKind.RelationshipButton, id, flag);
                break;
            }
        }
        return PrimitiveResult.True;
    }

    private static short Id(in TreeContext context, ReadOnlySpan<byte> operand) =>
        Variables.Get(context, (DataOwner)operand[1], (short)(operand[2] | operand[3] << 8));
}

public enum TutorialFlashKind
{
    ImageButton,
    PersonButton,
    RelationshipButton,
}
