namespace SimsEngine.Simulation;

/// <summary>The Random Number primitive (opcode 8), after Sims.exe FUN_00451eab. Its operand holds the result (data u16
/// at 0, owner byte 2) and the range (data u16 at 4, owner byte 6). An owner byte of 128 and up is not an owner, so it
/// raises "Illegal owner field" (9) whichever way it is read.
/// <para>Sims.exe takes the result as a reference first, then reads the range. A range of zero raises "Random number
/// range evaluated to zero." (4). A result that is neither a short nor a float (a read-only owner) raises "Expression
/// attempting to write in a read-only area." (19), before the random source is asked. Otherwise the value is
/// <c>random % (uint)(int)range</c>: a short gets its low 16 bits, a float (a motive) gets the value as an int. A
/// negative range is a large unsigned divisor, so the value is the random number itself (inferred from the unsigned
/// arithmetic). The primitive always answers true.</para></summary>
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
