namespace SimsEngine.Simulation;

/// <summary>The UI Effect primitive (opcode 0x22), after Sims.exe 0x44ad3f and Mac cXObject (ObjectSim.s 0x100E75D0). It has
/// no error exits: it always answers true.
/// <para>Operand: byte 0 is the effect type, byte 1 the owner and bytes 2 to 3 the data of the variable that gives the
/// object id (read as <see cref="Variables.Get"/> reads it, the Sims.exe FUN_00453181). Bit 0 of byte 4 is the effect's
/// flag.</para>
/// <para>Type 0 reads the variable and sets the object's effect (<see cref="ISimulationHost.SetObjectUiEffect"/>,
/// FUN_004765df), then reads it again and shows the object effect (<see cref="ISimulationHost.ShowObjectUiEffect"/>,
/// FUN_00476a13). Type 1 reads the variable and shows the object effect only. Type 2 reads it and shows the entry
/// effect (<see cref="ISimulationHost.ShowEntryUiEffect"/>, FUN_00476afb). Any other type does nothing.</para>
/// <para>Type 0 evaluates the variable separately for each call, as the original does.</para>
/// <para>Mac names the three calls Tut_FlashButtonByImageID (the value is a Res_CPanel image id),
/// Tut_FlashPersonPanelButton and Tut_FlashRelButton (the value is a neighbor id). A world without the full host gives
/// them to <see cref="World.TutorialFlash"/>.</para></summary>
public static class UiEffectPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        // The effect visuals are presentation only; a hostless world evaluates the operand variable and shows nothing.
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

/// <summary>The Mac tutorial flash calls the UI Effect primitive makes: Tut_FlashButtonByImageID,
/// Tut_FlashPersonPanelButton and Tut_FlashRelButton (TutorialEffects.s).</summary>
public enum TutorialFlashKind
{
    ImageButton,
    PersonButton,
    RelationshipButton,
}
