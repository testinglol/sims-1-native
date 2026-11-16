namespace SimsEngine.Simulation;

/// <summary>Computes the world distance between a source object and the stack object, storing the result in
/// the specified temporary variable.</summary>
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

    /// <summary>Calculates distance between objects as the square root of (20 × altitude difference)² +
    /// (dx² + dy²) / 256. Targets with object type 7 return a distance of zero.</summary>
    public static double DistanceBetween(XObject self, XObject other)
    {
        if (other.Definition[9] == 7) return 0;
        var dx = unchecked(self.Position.X - other.Position.X);
        var dy = unchecked(self.Position.Y - other.Position.Y);
        var dz = unchecked((other.Position.Height - self.Position.Height) * 20);
        var planar = unchecked(dx * dx + dy * dy);
        return Math.Sqrt((double)dz * dz + planar * (double)(1f / 256f));
    }

    // Converts float to int16 with truncation, treating NaN and overflow as zero.
    private static short Ftol16(double value) =>
        double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0 ? (short)0 : unchecked((short)(long)value);
}
