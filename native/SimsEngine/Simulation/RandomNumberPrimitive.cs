namespace SimsEngine.Simulation;

/// <summary>Executes the Random Number primitive (opcode 8) to generate and store a pseudo-random value
/// within a specified range, raising an error if the range is zero or the target is read-only.</summary>
public static class RandomNumberPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var resultData = (short)(operand[0] | operand[1] << 8);
        var resultOwner = (DataOwner)operand[2];
        var rangeData = (short)(operand[4] | operand[5] << 8);
        var rangeOwner = (DataOwner)operand[6];
        return Execute(context, resultOwner, resultData, rangeOwner, rangeData);
    }

    public static PrimitiveResult Execute(in TreeContext context, DataOwner resultOwner, short resultData, DataOwner rangeOwner, short rangeData)
    {
        var result = Variables.Reference(context, resultOwner, resultData);
        var range = Variables.Get(context, rangeOwner, rangeData);
        if (range == 0) throw new SimulationException(SimulationError.RandomNumberRangeEvaluatedToZero);
        if (!result.IsShort && !result.IsFloat) throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
        var value = context.World.NextRandom() % (uint)(int)range;
        if (result.IsShort) result.Short = unchecked((short)value);
        else result.Float = unchecked((int)value);
        return PrimitiveResult.True;
    }
}
