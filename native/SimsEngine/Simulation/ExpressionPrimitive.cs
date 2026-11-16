namespace SimsEngine.Simulation;

/// <summary>Implements the Expression primitive (opcode 0x02). Evaluates right-hand then left-hand operands
/// across variable scopes, wrapping 16-bit integer arithmetic and clamping written motives to the -100 to
/// 100 range.</summary>
public static class ExpressionPrimitive
{
    private static readonly bool[] Writes =
        [false, false, false, true, true, true, true, true, false, true, true, true, true, true, false, false, false, true, true, true, true];

    private const float MotiveMinimum = -100f, MotiveMaximum = 100f;

    public static bool Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var lhsData = (short)(operand[0] | operand[1] << 8);
        var rhsData = (short)(operand[2] | operand[3] << 8);
        var op = operand[5];
        var lhsOwner = (DataOwner)(sbyte)operand[6];
        var rhsOwner = (DataOwner)(sbyte)operand[7];
        return Execute(context, lhsOwner, lhsData, (ExpressionOperator)op, rhsOwner, rhsData);
    }

    public static bool Execute(in TreeContext context, DataOwner lhsOwner, short lhsData, ExpressionOperator op, DataOwner rhsOwner, short rhsData)
    {
        // Flags the evaluation context as an Expression instruction so variable resolution can transfer
        // nonnegative object wait values to parameter 0.
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

    // Flag operations use 1-based indexing (1 through 16); flag index 0 tests the sign bit without
    // modifying the value.
    private static int Flag(short rhs)
    {
        if ((ushort)rhs > 16) throw new SimulationException(SimulationError.FlagReferenceIsOutOfRange);
        return 1 << (((sbyte)rhs - 1) & 31);
    }

    private static short Step(Variable lhs, int by)
    {
        if (lhs.IsShort) return lhs.Short = unchecked((short)(lhs.Short + by));
        if (lhs.IsFloat) { lhs.Float += by; return Variable.Truncate(lhs.Float); }
        throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
    }
}
