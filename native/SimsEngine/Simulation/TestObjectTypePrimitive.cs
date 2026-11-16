namespace SimsEngine.Simulation;

/// <summary>Tests whether an object matches a specified type ID, checking its primary type definition and
/// falling back to its alternative type entry.</summary>
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
