namespace SimsEngine.Simulation;

/// <summary>The Expression primitive (opcode 2), after Sims.exe FUN_00452c85. Its operand holds the left data (u16 at
/// 0), the right data (u16 at 2), the operator (byte 5), the left owner (byte 6) and the right owner (byte 7).
/// <para>The right side is read first, then the left side, as a reference when the operator writes (DAT_00667244:
/// every operator but the six comparisons and "Flag Set?"). Comparisons answer whether they hold; every other
/// operator answers true. Arithmetic on shorts wraps to 16 bits. A float left side (a motive) takes "+=", "-=",
/// "Assign To:", "*=", "/=", "++ and &lt;" and "-- and &gt;"; the other writing operators need a short and raise
/// "write in a read-only area" for a float, as for a left side that cannot be written. After a write to my motives,
/// the stack object's motives or the stack object's motive[temp], the motive is clamped to -100..100.</para></summary>
public static class ExpressionPrimitive
{
    // DAT_00667244: whether each operator writes its left side.
    private static readonly bool[] Writes =
        [false, false, false, true, true, true, true, true, false, true, true, true, true, true, false, false, false, true, true, true, true];

    // _DAT_00666a60 and _DAT_0066674c.
    private const float MotiveMinimum = -100f, MotiveMaximum = 100f;

    public static bool Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var lhsData = (short)(operand[0] | operand[1] << 8);
        var rhsData = (short)(operand[2] | operand[3] << 8);
        var op = operand[5];
        // Sims.exe sign-extends the owner bytes.
        var lhsOwner = (DataOwner)(sbyte)operand[6];
        var rhsOwner = (DataOwner)(sbyte)operand[7];
        return Execute(context, lhsOwner, lhsData, (ExpressionOperator)op, rhsOwner, rhsData);
    }

    public static bool Execute(in TreeContext context, DataOwner lhsOwner, short lhsData, ExpressionOperator op, DataOwner rhsOwner, short rhsData)
    {
        // FUN_0044578b checks the active BHAV opcode before transferring a nonnegative object wait cell to the
        // current frame's first parameter. Variable access from any other primitive must not take that branch.
        var expressionContext = context with { ExpressionInstruction = true };
        var writes = (int)op < Writes.Length && Writes[(int)op];
        var rhs = Variables.Get(expressionContext, rhsOwner, rhsData);
        var lhs = writes ? Variables.Reference(expressionContext, lhsOwner, lhsData) : Variable.ReadOnly(Variables.Get(expressionContext, lhsOwner, lhsData));
        var left = lhs.Value;
        bool result;
        switch (op)
        {
            case ExpressionOperator.GreaterThan: result = rhs < left; break;
            case ExpressionOperator.LessThan: result = left < rhs; break;
            case ExpressionOperator.EqualTo: result = left == rhs; break;
            case ExpressionOperator.GreaterThanOrEqualTo: result = rhs <= left; break;
            case ExpressionOperator.LessThanOrEqualTo: result = left <= rhs; break;
            case ExpressionOperator.NotEqualTo: result = left != rhs; break;
            case ExpressionOperator.AddTo: Arithmetic(lhs, rhs, (a, b) => a + b, (a, b) => b + a); result = true; break;
            case ExpressionOperator.SubtractFrom: Arithmetic(lhs, rhs, (a, b) => a - b, (a, b) => a - b); result = true; break;
            case ExpressionOperator.AssignTo: Arithmetic(lhs, rhs, (_, b) => b, (_, b) => b); result = true; break;
            case ExpressionOperator.MultiplyBy: Arithmetic(lhs, rhs, (a, b) => a * b, (a, b) => b * a); result = true; break;
            case ExpressionOperator.DivideBy:
                if (rhs == 0) throw new SimulationException(SimulationError.DivideByZero);
                Arithmetic(lhs, rhs, (a, b) => a / b, (a, b) => a / b);
                result = true;
                break;
            case ExpressionOperator.IsFlagSet:
                result = (Flag(rhs) & left) != 0;
                break;
            case ExpressionOperator.SetFlag:
            {
                var flag = Flag(rhs);
                ShortOnly(lhs).Short = unchecked((short)((ushort)lhs.Short | (ushort)flag));
                result = true;
                break;
            }
            case ExpressionOperator.ClearFlag:
            {
                var flag = Flag(rhs);
                ShortOnly(lhs).Short = unchecked((short)((ushort)lhs.Short & ~(ushort)flag));
                result = true;
                break;
            }
            case ExpressionOperator.IncrementAndLessThan:
                result = Step(lhs, 1) < rhs;
                break;
            case ExpressionOperator.DecrementAndGreaterThan:
                result = rhs < Step(lhs, -1);
                break;
            case ExpressionOperator.ModuloBy:
            {
                ShortOnly(lhs);
                if (rhs == 0) throw new SimulationException(SimulationError.DivideByZero);
                // Both sides lose their sign first (a 16-bit negate, so -32768 stays -32768).
                var a = lhs.Short < 0 ? unchecked((short)-lhs.Short) : lhs.Short;
                var b = rhs < 0 ? unchecked((short)-rhs) : rhs;
                lhs.Short = (short)(a % b);
                result = true;
                break;
            }
            case ExpressionOperator.AndWith: ShortOnly(lhs).Short = (short)(lhs.Short & rhs); result = true; break;
            case ExpressionOperator.OrWith: ShortOnly(lhs).Short = (short)(lhs.Short | rhs); result = true; break;
            case ExpressionOperator.XorWith: ShortOnly(lhs).Short = (short)(lhs.Short ^ rhs); result = true; break;
            case ExpressionOperator.AssignSquareRoot:
                ShortOnly(lhs).Short = Variable.Truncate(Math.Sqrt(rhs));
                result = true;
                break;
            default:
                throw new SimulationException(SimulationError.BadOperator);
        }
        if (writes && lhs.IsFloat && lhsOwner is DataOwner.MyMotive or DataOwner.StackObjectMotive or DataOwner.StackObjectMotiveByTemporary)
            lhs.Float = Math.Clamp(lhs.Float, MotiveMinimum, MotiveMaximum);
        return result;
    }

    private static Variable ShortOnly(Variable variable) =>
        variable.IsShort ? variable : throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);

    private static void Arithmetic(Variable lhs, short rhs, Func<int, int, int> onShort, Func<float, float, float> onFloat)
    {
        if (lhs.IsShort) lhs.Short = unchecked((short)onShort(lhs.Short, rhs));
        else if (lhs.IsFloat) lhs.Float = onFloat(lhs.Float, rhs);
        else throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
    }

    // "Flag Set?", "Set Flag" and "Clear Flag" number flags from 1; a number above 16 is an error. Flag 0 shifts by 31
    // (the shift count is (n - 1) & 31): it tests the sign of the sign-extended left side and sets or clears nothing.
    private static int Flag(short rhs)
    {
        if ((ushort)rhs > 16) throw new SimulationException(SimulationError.FlagReferenceIsOutOfRange);
        return 1 << (((sbyte)rhs - 1) & 31);
    }

    // "++ and <" and "-- and >": step the left side by one, then compare its new value. Inferred for a float: Sims.exe
    // reads the compared value through the short reference, which a motive does not have; here the motive's new value
    // is compared truncated.
    private static short Step(Variable lhs, int by)
    {
        if (lhs.IsShort) return lhs.Short = unchecked((short)(lhs.Short + by));
        if (lhs.IsFloat) { lhs.Float += by; return Variable.Truncate(lhs.Float); }
        throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
    }
}
