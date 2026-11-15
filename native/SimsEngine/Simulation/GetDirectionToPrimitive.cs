namespace SimsEngine.Simulation;

/// <summary>The Get Direction To primitive (opcode 0xc), after Sims.exe FUN_00452043. Traced from the disassembly.
/// <para>Operand: the variable that receives the direction is a reference, named by the data short at 0 and the owner
/// short at 2. Byte 4 bit 0 takes the object from a variable: the owner is byte 5 and the data the short at 6. Without it
/// the object is me (owner My, data 11). Errors: 23 when the object is missing, and 21 when there is no stack object.</para>
/// <para>The direction is <see cref="DirectionBetween"/> from the object to the stack object. Sims.exe stores it as
/// (direction + 8) % 8 in the variable, which keeps a direction 0 to 7 unchanged. The primitive always returns true.
/// A read-only or non-short variable is refused with error 19 (inferred: Sims.exe writes through the reference without
/// that check, which is not traced).</para></summary>
public static class GetDirectionToPrimitive
{
    private const short ObjectIdData = (short)ObjectData.ObjectId;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var output = Variables.Reference(context, (DataOwner)(short)(operand[2] | operand[3] << 8), (short)(operand[0] | operand[1] << 8));
        var byVariable = (operand[4] & 1) != 0;
        var self = context.World.ObjectById(byVariable
            ? Variables.Get(context, (DataOwner)operand[5], (short)(operand[6] | operand[7] << 8))
            : Variables.Get(context, DataOwner.My, ObjectIdData))
            ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        var stackObject = context.World.ObjectByStackObject(context.Me, context.Frame.StackObject)
            ?? throw new SimulationException(SimulationError.NoStackObjectPresentWhenRequired);
        var direction = DirectionBetween(self.Position, stackObject.Position);
        var stored = unchecked((short)(direction + 8));
        if (!output.IsShort) throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
        output.Short = (short)(stored % 8);
        return PrimitiveResult.True;
    }

    /// <summary>FUN_00452129: the direction 0 to 7 from <paramref name="self"/> to <paramref name="other"/>, from
    /// dx = other.X − self.X (cWorldPt[1]) and dy = other.Y − self.Y (cWorldPt[0]). The eight values run round the
    /// compass from 0 (dy dominant, dy &lt; 0) through 2 (dx dominant, dx &gt;= 0), 4 (dy dominant, dy &gt;= 0) and
    /// 6 (dx dominant, dx &lt; 0), with the diagonals odd. Equal positions give 3.</summary>
    public static short DirectionBetween(ObjectPosition self, ObjectPosition other)
    {
        var dx = unchecked(other.X - self.X);
        var dy = unchecked(other.Y - self.Y);
        var absoluteX = unchecked(dx < 0 ? -dx : dx);
        var absoluteY = unchecked(dy < 0 ? -dy : dy);
        int result;
        if (unchecked(absoluteX * 2) < absoluteY) result = 4;
        else result = absoluteX <= unchecked(absoluteY * 2) ? 3 : 2;
        if (dx < 0) result = result switch { 2 => 6, 3 => 5, _ => result };
        if (dy < 0) result = result switch { 3 => 1, 4 => 0, 5 => 7, _ => result };
        return (short)result;
    }
}
