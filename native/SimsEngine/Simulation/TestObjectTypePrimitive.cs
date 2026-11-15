namespace SimsEngine.Simulation;

/// <summary>The Test Object Type primitive (opcode 0x20), after Sims.exe FUN_0044adcc. Its operand holds the type to
/// test (a dword at 0), the stack object's id (data u16 at 4, owner byte 6, read as an unsigned byte).
/// <para>The id is read first. An id with no object raises "Could not find object reference." (23). An object with no
/// type entry (+0x11c) answers false. Otherwise the answer is true when the entry's key is the type. When it is not,
/// Sims.exe asks for the entry's cached alternative (FUN_004573f9) and compares that entry's key the same way. The
/// answer is 1 or 0, so the primitive answers true or false.</para>
/// <para>Inferred: Sims.exe reads the alternative's key without a null check, so a missing alternative would fault in
/// the original. The port answers false for it. The meaning of the key (the object type's id) is inferred from the
/// name.</para></summary>
public static class TestObjectTypePrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var type = operand[0] | operand[1] << 8 | operand[2] << 16 | operand[3] << 24;
        var data = (short)(operand[4] | operand[5] << 8);
        var owner = (DataOwner)operand[6];
        var id = Variables.Get(context, owner, data);
        var item = context.World.ObjectById(id) ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        var entry = item.TypeRecord;
        if (entry is null) return PrimitiveResult.False;
        if (entry.Key == type) return PrimitiveResult.True;
        return entry.AlternativeTypeKey == type ? PrimitiveResult.True : PrimitiveResult.False;
    }
}
