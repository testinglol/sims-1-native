using SimsEngine.Content;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class CreateObjectInstancePrimitiveTests
{
    private const int Guid = 0x1234;

    private sealed class TestStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, SimsEngine.Formats.BhavInstruction instruction) =>
            throw new NotSupportedException();
    }

    private static byte[] Operand(int guid = Guid, byte mode = 5, byte flags = 0, byte local = 0) =>
        [(byte)guid, (byte)(guid >> 8), (byte)(guid >> 16), (byte)(guid >> 24), mode, flags, local, 0];

    private static XObject Made(short id, short type = 0)
    {
        var definition = PrimitiveTestWorld.Definition(type);
        var made = new XObject(id, definition);
        made[ObjectData.AllowedHeightFlags] = 1;
        made.Position = new ObjectPosition(0, 0, 0);
        return made;
    }

    private static byte[] AssignLiteral(ObjectData field, short value) =>
        [(byte)field, 0, (byte)value, (byte)(value >> 8), 0, (byte)ExpressionOperator.AssignTo, (byte)DataOwner.My, (byte)DataOwner.Literal];

    private static XPerson MadePerson(short id)
    {
        var made = new XPerson(id, PrimitiveTestWorld.Definition(XPerson.PersonType));
        made.Position = new ObjectPosition(0, 0, 0);
        return made;
    }

    private static (TreeContext Context, PrimitiveTestHost Host, SimulationWorld World) Setup(
        short meId = 100, short meType = 0, int x = 32, int y = 48, int level = 0, short stackObject = 0, short[]? locals = null)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var me = meType == XPerson.PersonType
            ? world.Add(new XPerson(meId, PrimitiveTestWorld.Definition(meType)))
            : world.Add(new XObject(meId, PrimitiveTestWorld.Definition(meType)));
        me.Position = new ObjectPosition(x, y, level);
        var frame = new StackFrame(stackObject, [], locals ?? [0, 0]);
        return (new TreeContext(world, me, frame), host, world);
    }

    [Fact]
    public void AContentBackedFactoryCreatesAndPlacesWithoutTheHost()
    {
        var (context, host, world) = Setup(x: 32, y: 48, level: 1, meType: XPerson.PersonType);
        context.Me.TypeRecord = new TypeRecord(context.Me.Id);
        // Newly instantiated objects clear their data and run their init tree (entry point 0) to establish
        // placement flags; person overlap checks are bypassed when object flag bit 2 is set while bit 1 is
        // clear.
        var init = new ChunkBuilder().U16(0x8002).S16(2).U8(0).U8(4).U8(0).U8(0).U32(0)
            .U16(2).U8(1).U8(0xFF).Raw(AssignLiteral(ObjectData.AllowedHeightFlags, 1))
            .U16(2).U8(0xFE).U8(0xFF).Raw(AssignLiteral(ObjectData.Flags, 4));
        var (initFile, _) = init.InFile("BHAV", 4096);
        var initScope = new BehaviorScope(initFile, initFile, initFile);
        world.ObjectFactory = _ =>
        {
            var created = Made(world.NextObjectId());
            created.TypeRecord = new TypeRecord(Guid) { FunctionTable = [4096], BehaviorScope = initScope };
            return created;
        };
        world.CaptureExecutionTrace = true;
        host.CreateObjectAnswer = _ => throw new InvalidOperationException("the host factory must not be used");

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 5)));

        var created = world.Objects[context.Frame.StackObject];
        Assert.Equal(new ObjectPosition(32, 48, 1), created.Position);
        Assert.Equal((short)1, created.Id);
        Assert.Equal((short)1, created.ClockManagerIndex);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("CreateObjectInstance", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("CanPlaceCreated", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("PlaceCreated", StringComparison.Ordinal));
        Assert.Equal(new ObjectCreationDiagnostic(100, 1, Guid, 5, 0, null,
            new ObjectPosition(32, 48, 1), 0, true), Assert.Single(world.ObjectCreationTrace));
    }

    [Fact]
    public void Mode5_CreatesOnOwnTileAndMakesItTheStackObject()
    {
        var (context, host, world) = Setup();
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Equal((short)7, context.Frame.StackObject);
        Assert.Equal(new ObjectPosition(32, 48, 0), world.Objects[7].Position);
        Assert.Equal(0x0001ffffu, world.ClockManager.Entries[6].Flags);
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,32,48,0,null,0)", "PlaceCreated(7,32,48,0,null,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode1_PassesMeAsThePlacementRelative()
    {
        var (context, host, _) = Setup();
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 1)));
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,32,48,0,100,0)", "PlaceCreated(7,32,48,0,100,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode2_CreatesInTheRunningSimsHandWithoutTilePlacement()
    {
        var (context, host, world) = Setup(meType: XPerson.PersonType);
        context.Me.Slots.AddRange([0, 0]);
        context.Me[ObjectData.Direction] = 5;
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 2)));

        var created = world.Objects[7];
        Assert.Equal((short)7, context.Frame.StackObject);
        Assert.Equal((short)5, created[ObjectData.Direction]);
        Assert.Equal((short)0, context.Me.Slots[0]);
        Assert.Equal(created.ClockManagerIndex, context.Me.Slots[1]);
        Assert.Equal(context.Me.ClockManagerIndex, created[ObjectData.ContainerId]);
        Assert.Equal(context.Me.ClockManagerIndex, created[ObjectData.ParentId]);
        Assert.Equal((short)0, created[ObjectData.SlotNumber]);
        Assert.Equal((short)1, created[ObjectData.SlotHierNumber]);
        Assert.Equal(context.Me.Position, created.Position);
        Assert.Equal(["CreateObjectInstance(4660)"], host.Calls);
    }

    [Fact]
    public void Mode2_WhenTheHandIsOccupied_RemovesTheUnplacedObjectAndAnswersFalse()
    {
        var (context, host, world) = Setup(meType: XPerson.PersonType);
        context.Me.Slots.AddRange([0, 9]);
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 2)));

        Assert.DoesNotContain((short)7, world.Objects.Keys);
        Assert.Equal(["CreateObjectInstance(4660)", "DiscardObject(7)"], host.Calls);
    }

    [Fact]
    public void Mode0_StepsByTheFootprintOffsetOfMyDirection()
    {
        var (context, host, world) = Setup();
        world.Objects[100][ObjectData.Direction] = 2;
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 0)));
        Assert.Equal(new ObjectPosition(48, 48, 0), world.Objects[7].Position);
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,48,48,0,null,0)", "PlaceCreated(7,48,48,0,null,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode6_CreatesOffWorldWithNoPlacement()
    {
        var (context, host, world) = Setup();
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 6)));
        Assert.Equal((short)7, context.Frame.StackObject);
        Assert.Equal(new ObjectPosition(-16, -16, 1), world.Objects[7].Position);
        Assert.Equal(["CreateObjectInstance(4660)"], host.Calls);
    }

    [Fact]
    public void DuplicateTypeOnTile_AnswersFalse()
    {
        var (context, host, _) = Setup();
        host.TileHasObjectAnswer = (_, _, _, key) => key == Guid;
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 2)));
        Assert.Equal(["TileHasObject(2,3,0,4660)"], host.Calls);
    }

    [Fact]
    public void NonEmptyTile_AnswersFalse()
    {
        var (context, host, _) = Setup();
        host.TileHasObjectAnswer = (_, _, _, key) => key is null;
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 8)));
        Assert.Equal(["TileHasObject(2,3,0,null)"], host.Calls);
    }

    [Fact]
    public void OutsideTheLot_AnswersFalse()
    {
        var (context, host, _) = Setup(x: 0);
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void PlacementUsesTheNativeLotBoundaryWithoutAHostQuery()
    {
        var (context, host, world) = Setup(x: 62 * 16, y: 16);
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Equal(62 * 16, world.Objects[7].Position.X);
        Assert.DoesNotContain(host.Calls, call => call == "LotSize");

        (context, host, _) = Setup(x: 63 * 16, y: 16);
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void RoomMismatchOnTheStackObjectsTile_AnswersFalse()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition());
        stack.Position = new ObjectPosition(64, 64, 1);
        stack.RoomWord84 = 3;
        world.Add(stack);
        world.Objects[100].SetObjectTableEntry(5, stack);
        host.RoomAtTileAnswer = (_, _, _) => 7;
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 4)));
        Assert.Equal(["RoomAtTile(4,4,1)"], host.Calls);
    }

    [Fact]
    public void Mode4_UsesTheObjectsRawRoomWordInsteadOfHostRoomLookup()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(64, 64, 1),
            RoomWord84 = 3,
        };
        world.Add(stack);
        world.Objects[100].SetObjectTableEntry(5, stack);
        host.RoomAtTileAnswer = (_, _, _) => 3;
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 4)));
        Assert.Equal(
            ["RoomAtTile(4,4,1)", "CreateObjectInstance(4660)", "CanPlaceCreated(7,64,64,1,5,0)", "PlaceCreated(7,64,64,1,5,0)"],
            host.Calls);
    }

    [Fact]
    public void FacingMismatchBesideMe_AnswersFalse()
    {
        var (context, host, _) = Setup(meType: XPerson.PersonType);
        host.FacingClearAnswer = (_, _, _, _) => false;
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 9)));
        Assert.Equal(["FacingClear(2,2,0,4)"], host.Calls);
    }

    [Fact]
    public void FailedCreation_AnswersFalse()
    {
        var (context, host, _) = Setup();
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Equal(["CreateObjectInstance(4660)"], host.Calls);
    }

    [Fact]
    public void Bit2_TakesTheKeyFromTheStackObject()
    {
        var (context, host, world) = Setup(stackObject: 5);
        while (world.NeighborWalkOrder.Count < 5) world.NeighborWalkOrder.Add(null);
        world.NeighborWalkOrder[4] = new NeighborState(5, []) { TypeRecordKey = 99, FriendEligibilityObjectFlagsB4 = 4 };
        host.CreateObjectAnswer = key => key == 99 ? Made(7) : null;
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 4)));
        Assert.Equal(
            ["CreateObjectInstance(99)", "CanPlaceCreated(7,32,48,0,null,0)", "PlaceCreated(7,32,48,0,null,0)"],
            host.Calls);
    }

    [Fact]
    public void Bit2_WithoutAStackObject_AnswersFalse()
    {
        var (context, host, _) = Setup();
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 4)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Bit2_NeighborWithoutSelector_AnswersFalse()
    {
        var (context, host, world) = Setup(stackObject: 5);
        while (world.NeighborWalkOrder.Count < 5) world.NeighborWalkOrder.Add(null);
        world.NeighborWalkOrder[4] = new NeighborState(5, []) { TypeRecordKey = 99 };
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 4)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Bit2_WithZeroNeighborKeyAnswersFalseBeforeCreation()
    {
        var (context, host, world) = Setup(stackObject: 5);
        world.SetNeighborTableEntry(5, new XObject(5, PrimitiveTestWorld.Definition()));

        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 4)));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Bit6_SkipsTheRoomAndFacingChecks()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition());
        stack.Position = new ObjectPosition(64, 64, 1);
        world.Add(stack);
        world.Objects[100].SetObjectTableEntry(5, stack);
        host.RoomAtTileAnswer = (_, _, _) => 7;
        host.FacingClearAnswer = (_, _, _, _) => false;
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 4, flags: 0x40)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("RoomAtTile") || call.StartsWith("FacingClear"));
    }

    [Fact]
    public void Mode7_UsesParameterZeroAsOneBasedObjectTableKey()
    {
        var (context, host, world) = Setup();
        var target = world.Add(Made(5));
        target.Position = new ObjectPosition(64, 80, 2);
        world.Objects[100].SetObjectTableEntry(2, target);
        context = context with { Frame = new StackFrame(0, [2], []) };
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 7)));
        Assert.Equal(new ObjectPosition(64, 80, 2), world.Objects[7].Position);
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,64,80,2,null,0)", "PlaceCreated(7,64,80,2,null,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode7_WithoutParameterZeroIsStackNumberOutOfRange()
    {
        var (context, _, _) = Setup();
        var error = Assert.Throws<SimulationException>(() => CreateObjectInstancePrimitive.Execute(context, Operand(mode: 7)));
        Assert.Equal(SimulationError.StackNumberOutOfRange, error.Error);
    }

    [Fact]
    public void Mode7_WithMissingTableEntryAnswersFalse()
    {
        var (context, host, world) = Setup();
        context = context with { Frame = new StackFrame(0, [2], []) };
        host.ObjectTable[2] = world.Add(Made(5));

        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 7)));
        Assert.DoesNotContain("ObjectTableEntry(2)", host.Calls);
        Assert.DoesNotContain("CreateObjectInstance(4660)", host.Calls);
    }

    [Fact]
    public void TemporaryZeroPassingFlag_WritesParameterZero()
    {
        var (context, host, world) = Setup();
        world.Objects[100].Temporaries[0] = 23;
        context = context with { Frame = new StackFrame(0, [9], []) };
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 0x10)));
        Assert.Equal((short)23, context.Frame.Parameters[0]);
    }

    [Fact]
    public void TemporaryZeroPassingFlag_DoesNothingWhenFrameHasNoParameters()
    {
        var (context, host, _) = Setup();
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 0x10)));
    }

    [Fact]
    public void RawStackObjectKeyPassingFlag_WritesParameterAndFirstFrameStackObject()
    {
        var (context, host, world) = Setup();
        var me = world.Objects[100];
        me.RequestKey = 55;
        var root = new StackFrame(4, [], []);
        var current = new StackFrame(9, [9], []);
        var stack = new TestStack();
        stack.Frames.Add(root);
        stack.Frames.Add(current);
        context = context with { Frame = current, Stack = stack };
        host.CreateObjectAnswer = _ => Made(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 2)));
        Assert.Equal((short)55, current.Parameters[0]);
        Assert.Equal((short)55, root.StackObject);
        Assert.Equal((short)7, current.StackObject);
    }

    [Fact]
    public void BothPassingValueBits_AreError54()
    {
        var (context, host, _) = Setup();
        host.CreateObjectAnswer = _ => Made(7);
        var error = Assert.Throws<SimulationException>(() => CreateObjectInstancePrimitive.Execute(context, Operand(flags: 0x12)));
        Assert.Equal(SimulationError.MoreThanOnePassingValueSpecified, error.Error);
    }

    [Fact]
    public void MissingStackObject_AnswersFalse()
    {
        var (context, host, world) = Setup(stackObject: 5);
        host.ObjectTable[5] = world.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 4)));
        Assert.DoesNotContain("ObjectTableEntry(5)", host.Calls);
        Assert.DoesNotContain("CreateObjectInstance(4660)", host.Calls);
    }

    [Fact]
    public void Mode8_CopiesOnlyTheTableObjectsPosition()
    {
        var (context, host, world) = Setup(locals: [3, 0]);
        var target = new XObject(9, PrimitiveTestWorld.Definition());
        target.Position = new ObjectPosition(80, 96, 1);
        world.Add(target);
        world.Objects[100].SetObjectTableEntry(3, target);
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 8)));
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,80,96,1,null,0)", "PlaceCreated(7,80,96,1,null,0)"],
            host.Calls);
    }

    [Fact]
    public void Mode8_WithMissingNativeEntryDoesNotUseHostFallback()
    {
        var (context, host, world) = Setup(locals: [3, 0]);
        host.ObjectTable[3] = world.Add(Made(9));

        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 8)));
        Assert.DoesNotContain("ObjectTableEntry(3)", host.Calls);
        Assert.DoesNotContain("CreateObjectInstance(4660)", host.Calls);
    }

    [Fact]
    public void DirectionFlag_DoesNotUseHostOnlyStackTableEntry()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var hostOnly = new XObject(5, PrimitiveTestWorld.Definition());
        hostOnly[ObjectData.Direction] = 6;
        host.ObjectTable[5] = hostOnly;
        host.CreateObjectAnswer = _ => MadePerson(7);

        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(mode: 6, flags: 0x20)));
        Assert.Equal((short)0, world.Objects[7][ObjectData.Direction]);
        Assert.DoesNotContain("ObjectTableEntry(5)", host.Calls);
    }

    [Fact]
    public void Mode8_LocalPastTheLocals_IsError8()
    {
        var (context, _, _) = Setup(locals: [0, 0]);
        var error = Assert.Throws<SimulationException>(() => CreateObjectInstancePrimitive.Execute(context, Operand(mode: 8, local: 2)));
        Assert.Equal(SimulationError.StackNumberOutOfRange, error.Error);
    }

    [Fact]
    public void RefusedPlacement_DiscardsAndRemovesTheObject()
    {
        var (context, host, world) = Setup();
        host.CreateObjectAnswer = _ => Made(7);
        host.PlacementAnswer = false;
        Assert.Equal(PrimitiveResult.False, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.False(world.Objects.ContainsKey(7));
        Assert.Equal(
            ["CreateObjectInstance(4660)", "CanPlaceCreated(7,32,48,0,null,0)", "DiscardObject(7)"],
            host.Calls);
    }

    [Fact]
    public void CreatedPerson_IsBoundToItsAvatar()
    {
        var (context, host, _) = Setup();
        host.CreateObjectAnswer = _ => MadePerson(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Contains("InitCreatedPerson(7)", host.Calls);
    }

    [Fact]
    public void CreatedPersonWithType_Arrives()
    {
        var (context, host, world) = Setup(stackObject: 5);
        while (world.NeighborWalkOrder.Count < 5) world.NeighborWalkOrder.Add(null);
        world.NeighborWalkOrder[4] = new NeighborState(5, []) { TypeRecordKey = Guid, FriendEligibilityObjectFlagsB4 = 4 };
        var person = MadePerson(7);
        person[PersonData.PersonType] = 1;
        host.CreateObjectAnswer = _ => person;
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 4)));
        Assert.Contains("ArriveCreatedPerson(7)", host.Calls);
    }

    [Fact]
    public void DirectionCopy_PersonTakesItDirectly()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition());
        stack[ObjectData.Direction] = 6;
        world.Add(stack);
        world.Objects[100].SetObjectTableEntry(5, stack);
        var person = MadePerson(7);
        host.CreateObjectAnswer = _ => person;
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 0x20)));
        Assert.Equal((short)6, person[ObjectData.Direction]);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("RotateCreatedObject"));
    }

    [Fact]
    public void DirectionCopy_ObjectRotatesToTheMaskedDirection()
    {
        var (context, host, world) = Setup(stackObject: 5);
        var stack = new XObject(5, PrimitiveTestWorld.Definition());
        stack[ObjectData.Direction] = 7;
        world.Add(stack);
        world.Objects[100].SetObjectTableEntry(5, stack);
        host.CreateObjectAnswer = _ => Made(7);
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand(flags: 0x20)));
        Assert.Equal((short)6, world.Objects[7][ObjectData.Direction]);
        Assert.Contains("RotateCreatedObject(7)", host.Calls);
    }

    [Fact]
    public void MultitilePart_CentersOnItsTile()
    {
        var (context, host, world) = Setup(x: 33, y: 33);
        var functionTable = new ushort[0x20];
        functionTable[8] = 1;
        world.Objects[100].TypeRecord = new TypeRecord(1) { FunctionTable = functionTable };
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)SimsEngine.Formats.ObjdField.MasterId] = 1;
        var made = new XObject(7, definition);
        made[ObjectData.AllowedHeightFlags] = 1;
        host.CreateObjectAnswer = _ => made;
        Assert.Equal(PrimitiveResult.True, CreateObjectInstancePrimitive.Execute(context, Operand()));
        Assert.Equal(new ObjectPosition(40, 40, 0), world.Objects[7].Position);
        Assert.Contains("CanPlaceCreated(7,40,40,0,null,0)", host.Calls);
    }
}
