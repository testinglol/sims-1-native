namespace SimsEngine.Simulation;

/// <summary>The Get Distance To primitive (opcode 0xb), after Sims.exe FUN_00451f67. Traced from the disassembly.
/// <para>Operand: the temporary that receives the distance is the short at 0 (0 to 7, or error 2). Byte 2 holds the flags:
/// bit 0 takes the object from a variable, the owner in byte 3 and the data short at 4. Without it the object is me (owner
/// My, data 11, my object id). The distance is stored as a short in my temporary.</para>
/// <para>The object and the stack object are looked up by id, each failing with error 23. The distance is
/// <see cref="DistanceBetween"/> from the object to the stack object. The primitive always returns true.</para></summary>
public static class GetDistanceToPrimitive
{
    private const int TemporaryCount = 8;
    private const short ObjectIdData = (short)ObjectData.ObjectId;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var index = (short)(operand[0] | operand[1] << 8);
        if (index is < 0 or >= TemporaryCount) throw new SimulationException(SimulationError.TemporaryReferenceOutOfRange);
        var byVariable = (operand[2] & 1) != 0;
        var targetId = byVariable
            ? Variables.Get(context, (DataOwner)operand[3], (short)(operand[4] | operand[5] << 8))
            : Variables.Get(context, DataOwner.My, ObjectIdData);
        var target = (context.World.ObjectTableEntry(targetId) ?? context.World.ObjectByStackObject(context.Me, targetId))
            ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        var stackObject = context.World.ObjectByStackObject(context.Me, context.Frame.StackObject)
            ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        context.Me.Temporaries[index] = Ftol16(DistanceBetween(target, stackObject));
        return PrimitiveResult.True;
    }

    /// <summary>FUN_004341ed: the distance from <paramref name="self"/> to <paramref name="other"/>. An <paramref name="other"/>
    /// of definition type 7 is 0.0 (the type's meaning is not traced). Otherwise it is the square root of
    /// (20 × height difference)² + (dx² + dy²) / 256, with the differences and their squares in int32 as Sims.exe
    /// computes them (imul and add). The 1/256 is a float32 constant (inferred from the fmul DWORD operand).</summary>
    public static double DistanceBetween(XObject self, XObject other)
    {
        if (other.Definition[9] == 7) return 0;
        var dx = unchecked(self.Position.X - other.Position.X);
        var dy = unchecked(self.Position.Y - other.Position.Y);
        var dz = unchecked((other.Position.Height - self.Position.Height) * 20);
        var planar = unchecked(dx * dx + dy * dy);
        return Math.Sqrt((double)dz * dz + planar * (double)(1f / 256f));
    }

    // FUN_0063604c: a truncating conversion to an int64 whose low 16 bits Sims.exe stores. NaN and out-of-range values
    // give the x87 indefinite value, whose low 16 bits are zero.
    private static short Ftol16(double value) =>
        double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0 ? (short)0 : unchecked((short)(long)value);
}
