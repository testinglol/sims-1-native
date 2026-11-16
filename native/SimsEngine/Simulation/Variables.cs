namespace SimsEngine.Simulation;

public readonly record struct TreeContext(SimulationWorld World, XObject Me, StackFrame Frame)
{
    public XObject? StackObject => World.ObjectByStackObject(Me, Frame.StackObject);

    public TreeStack? Stack { get; init; }

    /// <summary>Indicates whether variable resolution occurs within an Expression instruction, which
    /// enables copying a target object's wait timer into frame parameter 0.</summary>
    internal bool ExpressionInstruction { get; init; }
}

/// <summary>Represents a simulation variable or reference, storing standard data as signed 16-bit integers
/// and motives as floating-point values truncated toward zero when read.</summary>
public readonly struct Variable
{
    private readonly short[]? _shorts;
    private readonly float[]? _floats;
    private readonly int _index;

    public short Value { get; }

    private Variable(short value, short[]? shorts, float[]? floats, int index) => (Value, _shorts, _floats, _index) = (value, shorts, floats, index);

    internal static Variable ReadOnly(short value) => new(value, null, null, 0);
    internal static Variable Of(short[] values, int index, bool reference) => new(values[index], reference ? values : null, null, index);
    internal static Variable OfFloat(float[] values, int index, bool reference) => new(Truncate(values[index]), null, reference ? values : null, index);

    public bool IsShort => _shorts is not null;
    public bool IsFloat => _floats is not null;

    public short Short { get => _shorts![_index]; set => _shorts![_index] = value; }
    public float Float { get => _floats![_index]; set => _floats![_index] = value; }

    internal static short Truncate(double value) =>
        double.IsNaN(value) || value >= 2147483648.0 || value < -2147483648.0 ? (short)0 : unchecked((short)(int)value);
}

/// <summary>Resolves SimAntics variable access across data scopes, enforcing read-only boundaries where
/// only globals 34 (Lot Transit Info) and 36 (Enable Grass Simulation) permit writes.</summary>
public static class Variables
{
    private const int DefinitionTypeField = 9;

    public static short Get(in TreeContext context, DataOwner owner, short data) => Access(context, owner, data, false).Value;

    public static Variable Reference(in TreeContext context, DataOwner owner, short data) => Access(context, owner, data, true);

    public static void Set(in TreeContext context, DataOwner owner, short data, short value)
    {
        var variable = Reference(context, owner, data);
        if (variable.IsShort) variable.Short = value;
        else if (variable.IsFloat) variable.Float = value;
        else throw new SimulationException(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea);
    }

    private static SimulationException Error(SimulationError error) => new(error);

    private static XPerson Person(XObject item)
    {
        if (item.Definition[DefinitionTypeField] != XPerson.PersonType) throw Error(SimulationError.ObjectReferredToMustBeAPerson);
        return item as XPerson ?? throw new InvalidOperationException($"Object {item.Id} has the person type but no person data.");
    }

    private static Variable Element(short[] values, int index, bool reference, SimulationError error) =>
        index >= 0 && index < values.Length ? Variable.Of(values, index, reference) : throw Error(error);

    // Slot index maps to entry plus one because entry zero is a sentinel.
    private static Variable Slot(XObject item, short data) =>
        data >= 0 && data + 1 < item.Slots.Count ? Variable.ReadOnly(item.Slots[data + 1]) : throw Error(SimulationError.SlotNumberOutOfRange);

    private static short Temporary(XObject me, short data, SimulationError error = SimulationError.TemporaryReferenceOutOfRange) =>
        data is >= 0 and <= 7 ? me.Temporaries[data] : throw Error(error);

    private static Variable Access(in TreeContext context, DataOwner owner, short data, bool reference)
    {
        var (world, me, frame) = context;
        XObject StackObject(SimulationError error) => world.ObjectByStackObject(me, frame.StackObject) ?? throw Error(error);
        const SimulationError noStackObject = SimulationError.ReferenceToTreeParameterWhenNoParameterExists;
        switch (owner)
        {
            case DataOwner.MyAttribute:
                return Element(me.Attributes, data, reference, SimulationError.AttributeNumberOutOfRange);
            case DataOwner.StackObjectAttribute:
                return Element(StackObject(noStackObject).Attributes, data, reference, SimulationError.AttributeNumberOutOfRange);
            case DataOwner.My:
                if (data is < 0 or >= XObject.DataCount) throw Error(SimulationError.DataNumberOutOfRange);
                var myData = Variable.Of(me.Data, data, reference);
                if (data == (short)ObjectData.LockoutCount) LockoutCountRead(context, me);
                return myData;
            case DataOwner.StackObject:
                if (data is < 0 or >= XObject.DataCount) throw Error(SimulationError.DataNumberOutOfRange);
                var lockoutTarget = StackObject(noStackObject);
                var stackData = Variable.Of(lockoutTarget.Data, data, reference);
                if (data == (short)ObjectData.LockoutCount) LockoutCountRead(context, lockoutTarget);
                return stackData;
            case DataOwner.Global:
                if (data is < 0 or >= SimulationWorld.GlobalCount) throw Error(SimulationError.GlobalNumberOutOfRange);
                return Variable.Of(world.Globals, data, reference && data is (short)SimulationGlobal.LotTransitInfo or (short)SimulationGlobal.EnableGrassSimulation);
            case DataOwner.Literal:
                return Variable.ReadOnly(data);
            case DataOwner.Temporary:
                return Element(me.Temporaries, data, reference, SimulationError.TemporaryReferenceOutOfRange);
            case DataOwner.Parameter:
                return Element(frame.Parameters, data, reference, SimulationError.StackNumberOutOfRange);
            case DataOwner.StackObjectId:
                if (data != 0) throw Error(SimulationError.ReferenceToStackObjectOutOfRange);
                return Variable.Of(frame.StackObjectCell, 0, reference);
            case DataOwner.TemporaryByTemporary:
                return Element(me.Temporaries, Temporary(me, data), reference, SimulationError.TemporaryReferenceOutOfRange);
            case DataOwner.CheckTreeAdRange:
                return CheckTreeAd(world, data, 2, reference);
            case DataOwner.StackObjectTemporary:
            {
                var stackObject = StackObject(noStackObject);
                return Element(stackObject.Temporaries, data, reference, SimulationError.TemporaryReferenceOutOfRange);
            }
            case DataOwner.MyMotive:
                if (data is < 0 or > 15) throw Error(SimulationError.MotiveNumberOutOfRange);
                return Variable.OfFloat(Person(me).Motives, data, reference);
            case DataOwner.StackObjectMotive:
                if (data is < 0 or > 15) throw Error(SimulationError.MotiveNumberOutOfRange);
                return Variable.OfFloat(Person(StackObject(SimulationError.NoStackObjectPresentWhenRequired)).Motives, data, reference);
            case DataOwner.StackObjectSlot:
                return Slot(StackObject(SimulationError.NoStackObjectPresentWhenRequired), data);
            case DataOwner.StackObjectMotiveByTemporary:
            {
                var motive = Temporary(me, data);
                if (motive is < 0 or > 15) throw Error(SimulationError.MotiveNumberOutOfRange);
                return Variable.OfFloat(Person(StackObject(SimulationError.NoStackObjectPresentWhenRequired)).Motives, motive, reference);
            }
            case DataOwner.MyPersonData:
            {
                var person = Person(me);
                return Element(person.PersonData, data, reference, SimulationError.PersonDataFieldIsOutOfRange);
            }
            case DataOwner.StackObjectPersonData:
                if (data is < 0 or > 0xff) throw Error(SimulationError.PersonDataFieldIsOutOfRange);
                return Variable.Of(Person(StackObject(SimulationError.NoStackObjectPresentWhenRequired)).PersonData, data, reference);
            case DataOwner.MySlot:
                return Slot(me, data);
            case DataOwner.StackObjectDefinition:
                if ((ushort)data >= XObject.DefinitionCount) throw Error(SimulationError.IndexIntoObjectDefinitionOutOfRange);
                return Variable.ReadOnly(StackObject(noStackObject).Definition[data]);
            case DataOwner.StackObjectAttributeByParameter:
            {
                if (data < 0 || data >= frame.Parameters.Length) throw Error(SimulationError.StackNumberOutOfRange);
                var attribute = frame.Parameters[data];
                return Element(StackObject(noStackObject).Attributes, attribute, reference, SimulationError.AttributeNumberOutOfRange);
            }
            case DataOwner.RoomByTemporary0:
                return Variable.ReadOnly(Room(world, me.Temporaries[0], data));
            case DataOwner.NeighborInStackObject:
                if (data == 0) return Variable.ReadOnly(world.NeighborObjectKey(frame.StackObject));
                if (data == 1) return Variable.ReadOnly(world.NeighborBelongsToHouse(frame.StackObject));
                if (data == 2) return Variable.ReadOnly(world.NeighborWordE8(frame.StackObject));
                if (data == 5) return Variable.ReadOnly(world.NeighborFriendCount(frame.StackObject));
                if (data == 6) return Variable.ReadOnly(world.NeighborInteractionValue(frame.StackObject));
                if (data == 7) return Variable.ReadOnly(world.NeighborInteractionFlag(frame.StackObject, 0));
                if (data == 8) return Variable.ReadOnly(world.NeighborInteractionFlag(frame.StackObject, 1));
                if (data == 9) return Variable.ReadOnly(world.NeighborInteractionCount(frame.StackObject));
                return Variable.ReadOnly((short)0);
            case DataOwner.Local:
                return Element(frame.Locals, data, reference, SimulationError.StackNumberOutOfRange);
            case DataOwner.Constant:
                return Variable.ReadOnly(Constant(world, frame, data));
            case DataOwner.StackObjectDynamicSpriteFlagByTemporary:
            {
                var index = Temporary(me, data);
                var stackObject = StackObject(noStackObject);
                return Element(stackObject.DynamicSpriteFlags, index, reference, SimulationError.InvalidDynamicSpriteIndex);
            }
            case DataOwner.CheckTreeAdPersonalityVariable:
                return CheckTreeAd(world, data, 0, reference);
            case DataOwner.CheckTreeAdMin:
                return CheckTreeAd(world, data, 1, reference);
            case DataOwner.MyPersonDataByTemporary:
            {
                var person = Person(me);
                return Element(person.PersonData, Temporary(me, data), reference, SimulationError.PersonDataFieldIsOutOfRange);
            }
            case DataOwner.StackObjectPersonDataByTemporary:
            {
                var person = Person(StackObject(SimulationError.NoStackObjectPresentWhenRequired));
                return Element(person.PersonData, Temporary(me, data), reference, SimulationError.PersonDataFieldIsOutOfRange);
            }
            case DataOwner.NeighborPersonData:
            {
                var neighbor = world.NeighborManagerEntry(frame.StackObject)
                    ?? throw new SimulationException(SimulationError.NeighborNotFound,
                        $"NeighborPersonData data {data}, stack object {frame.StackObject}, neighbor slots {world.NeighborWalkOrder.Count}");
                if (data is < 0 or > 0xff) throw Error(SimulationError.PersonDataFieldIsOutOfRange);
                return Variable.ReadOnly(neighbor.PersonData[data]);
            }
            case DataOwner.JobDataByTemporary0And1:
            {
                var host = world.RequireCareerHost($"tree variable JobDataByTemporary0And1 data {data}");
                var career = me.Temporaries[0];
                if (!host.CareerExists(career)) throw Error(SimulationError.CareerIdNotFound);
                var level = career == -1 ? (short)0 : me.Temporaries[1];
                return Variable.ReadOnly(host.JobData(career, level, data) ?? throw Error(SimulationError.JobDataNotFound));
            }
            case DataOwner.NeighborhoodData:
                if (data is < 1 or > 15) throw Error(SimulationError.NeighborhoodDataIndexOutOfRange);
                return Variable.ReadOnly(world.NeighborhoodData[data]);
            case DataOwner.StackObjectFunction:
            {
                var stackObject = StackObject(noStackObject);
                return Variable.ReadOnly(SimulationWorld.FunctionTreeId(stackObject, data));
            }
            case DataOwner.MyTypeAttribute:
                return Element(me.TypeAttributes, data, reference, SimulationError.TypeAttributeNumberOutOfRange);
            case DataOwner.StackObjectTypeAttribute:
                return Element(StackObject(noStackObject).TypeAttributes, data, reference, SimulationError.TypeAttributeNumberOutOfRange);
            case DataOwner.NeighborObjectDefinition:
            {
                var neighbor = world.NeighborManagerEntry(frame.StackObject)
                    ?? throw new SimulationException(SimulationError.NeighborNotFound,
                        $"NeighborObjectDefinition data {data}, stack object {frame.StackObject}, neighbor slots {world.NeighborWalkOrder.Count}");
                if ((ushort)data >= XObject.DefinitionCount) throw Error(SimulationError.IndexIntoObjectDefinitionOutOfRange);
                return Variable.ReadOnly(neighbor.Definition[data]);
            }
            default:
                throw new SimulationException(SimulationError.IllegalOwnerFieldInDataReference,
                    $"owner {(short)owner}, data {data}, reference {reference}");
        }
    }

    private static void LockoutCountRead(in TreeContext context, XObject item)
    {
        var world = context.World;
        var managerIndex = item.ClockManagerIndex;
        if (managerIndex <= 0) return;

        var waitTicks = world.ClockManager.WaitTicksAt(managerIndex);
        if (context.ExpressionInstruction && waitTicks >= 0)
        {
            var currentFrame = context.Stack?.Top ?? context.Frame;
            if (currentFrame.Parameters.Length > 0)
                currentFrame.Parameters[0] = waitTicks;
        }
        if (Environment.GetEnvironmentVariable("SIMS_DBG_LOCKOUT") == item.Id.ToString()) Console.Error.WriteLine($"LOCKOUT read obj={item.Id} wait={waitTicks} by {context.Me.Id} tree {context.Frame.TreeId}.{context.Frame.Instruction}");
        world.ClockManager.SetWaitTicksAt(managerIndex, -2);
    }

    private static Variable CheckTreeAd(SimulationWorld world, short data, int field, bool reference)
    {
        if (data < 0 || data >= world.CheckTreeAds.Length / 3) throw Error(SimulationError.MotiveNumberOutOfRange);
        return Variable.Of(world.CheckTreeAds, data * 3 + field, reference);
    }

    // Outside (room -5) defaults field 1 to 1 and all other room fields to 0.
    private static short Room(SimulationWorld world, short room, short field)
    {
        if (field is < 0 or > 3) throw Error(SimulationError.BadRoomValueRequested);
        if (room == -5) return field == 1 ? (short)1 : (short)0;
        if (world.RoomManagerRecordsLoaded)
        {
            if (!world.HasRoomRecord(room)) throw Error(SimulationError.BadRoomIdSpecified);
            if (field == 1) return (short)(world.RoomIsOutside(room) ? 1 : 0);
            if (field == 2) return (short)(((room & 0x0c00) >> 10) + 1);
        }
        if (world.Host?.Room(room, field) is { } hostValue) return hostValue;
        if (!world.HasRoomRecord(room)) throw Error(SimulationError.BadRoomIdSpecified);
        return field switch
        {
            0 => (short)(world.PersonRoomMotiveEntries.TryGetValue(unchecked((ushort)room), out var entry)
                ? (int)SimulationWorld.DisplayedRoomLight(entry) : 0),
            1 => (short)(world.RoomIsOutside(room) ? 1 : 0),
            2 => (short)(((room & 0x0c00) >> 10) + 1),
            3 => (short)world.RoomArea(room),
            _ => 0
        };
    }

    /// <summary>Decodes a constant reference bitfield: bits 13-15 select the BCON scope (0 private at
    /// 0x1000, 1 semi-global at 0x2000, 2 global at 0x100), bits 7-12 offset the resource ID, and bits 0-6
    /// index the value.</summary>
    public static (ushort Bcon, int Index)? DecodeConstant(short data)
    {
        ushort? scope = ((data >> 13) & 7) switch { 0 => 0x1000, 1 => 0x2000, 2 => 0x100, _ => null };
        return scope is { } baseId ? ((ushort)(baseId + ((data >> 7) & 0x3f)), data & 0x7f) : null;
    }

    private static short Constant(SimulationWorld world, StackFrame frame, short data)
    {
        if (DecodeConstant(data) is not var (bcon, index)) throw Error(SimulationError.InvalidConstant);
        var values = frame.Table?.Constants(bcon, allowTuningOverride: true) ?? world.Host?.Constants(frame, bcon) ?? throw Error(SimulationError.InvalidConstant);
        if (index >= values.Count) throw Error(SimulationError.InvalidConstant);
        return values[index];
    }
}
