using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class Wave10SimAnticsParityTests
{
    private sealed class TestRouteStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.True;
    }

    private static RoomMap OpenRoom(int width = 16, int height = 16)
    {
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(new byte[width * height * 8]);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "wave10-open-room.iff"), 1, width, height);
    }

    private static Bhav ReturnTrueBhav()
    {
        var expression = PrimitiveTestWorld.Expression(0, 0, (byte)ExpressionOperator.EqualTo);
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(4).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(expression).ToArray();
        var (file, chunk) = new ChunkBuilder().Raw(body).InFile("BHAV");
        return Bhav.Read(file, chunk);
    }

    private static ObjectEntry Definition(uint guid, ushort type = 0, ushort masterId = 0)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        var (file, chunk) = builder.InFile("OBJD");
        return new ObjectEntry(0, ObjectScan.Objects, $"{guid:X8}.iff", file, chunk, Objd.Read(file, chunk), null);
    }

    private static ObjectState SavedState(MultiTileState? multiTile = null) =>
        new(
            [14, 22, 100, 104, 256, 512],
            2,
            0x1234,
            [1, 2],
            Enumerable.Range(0, 8).Select(value => (short)(value + 3)).ToArray(),
            Enumerable.Range(0, XObject.DataCount).Select(value => (short)(value + 10)).ToArray(),
            16,
            [],
            [],
            [8, 9],
            [6, 7],
            1234,
            null,
            multiTile);

    [Fact]
    public void SeatedSimOnSeatWithoutSitStandFunctions_GoToRoutingSlotReturnsFalseWithoutClearingContainer()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(person.Id);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0801;

        var benchFunctions = new ushort[0x1c];
        var bench = world.Add(new XObject(3, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(3)
            {
                FunctionTable = benchFunctions,
                BehaviorScope = new TreeTable((4097, ReturnTrueBhav())),
            },
        });
        bench[ObjectData.Flags] = 4;
        world.PlaceObject(bench, new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1));

        person.Position = bench.Position;
        person.RoomWord84 = bench.RoomWord84;
        person[PersonData.Sitting] = 1;
        person[ObjectData.ContainerId] = bench.Id;

        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        target[ObjectData.Flags] = 4;
        world.PlaceObject(target, new ObjectPosition(12 * 16 + 8, 8 * 16 + 8, 1));

        var stack = new TestRouteStack();
        var resultWithoutStand = RouteExecutor.Start(
            world, person, context.Frame, target, new RoutingSlotRequest(), stack);

        Assert.Equal(PrimitiveResult.False, resultWithoutStand);
        Assert.False(context.Frame.Waiting);
        Assert.Null(person.NativeRoute);
        Assert.Equal((short)1, person[PersonData.RouteResult]);
        Assert.Equal((short)1, person[PersonData.Sitting]);
        Assert.Equal(bench.Id, person[ObjectData.ContainerId]);

        // A seat defining a stand action (0x1B) without a sit action (0x1A) fails routing immediately to
        // prevent invalid movement while seated.
        benchFunctions[0x1b] = 4097;
        person[PersonData.RouteResult] = 0;

        var resultWithoutSit = RouteExecutor.Start(
            world, person, context.Frame, target, new RoutingSlotRequest(), stack);

        Assert.Equal(PrimitiveResult.False, resultWithoutSit);
        Assert.False(context.Frame.Waiting);
        Assert.Null(person.NativeRoute);
        Assert.Empty(stack.Frames);
        Assert.Equal((short)1, person[PersonData.RouteResult]);
        Assert.Equal((short)1, person[PersonData.Sitting]);
        Assert.Equal(bench.Id, person[ObjectData.ContainerId]);
    }

    [Fact]
    public void SetToNextWalkTile_UnplacedPersonOnOccupiedLevelOneTile_ReturnsFalseWithoutMutatingStackObject()
    {
        var (context, person, _) = PrimitiveTestWorld.Create();
        var world = context.World;
        world.Host = null;

        person.Position = new ObjectPosition(4 * 16 + 8, 5 * 16 + 8, 1);

        var placed = world.Add(new XObject(74, PrimitiveTestWorld.Definition()));
        placed.Slots.Add(0);
        placed[ObjectData.AllowedHeightFlags] = 1;
        world.PlaceObject(placed, new ObjectPosition(4 * 16 + 8, 5 * 16 + 8, 1));

        Assert.Equal([placed], world.ObjectsAtTile(4, 5, 1).ToArray());

        context.Frame.StackObject = person.StackObjectKey;
        ReadOnlySpan<byte> operand = [0, 0, 0, 0, 8, 0, 0, 0];

        var result = SetToNextPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(person.StackObjectKey, context.Frame.StackObject);
    }

    [Fact]
    public void SavedLotRestore_MultiTileLeadWithSelfLeadObjectId_ClearsLeadMasterIdAndLinksAllPartsAcyclically()
    {
        const uint masterGuid = 0x11111111;
        const uint secondGuid = 0x22222222;
        const uint thirdGuid = 0x33333333;
        var master = Definition(masterGuid, 0, masterId: 1);
        var second = Definition(secondGuid, 0, masterId: 1);
        var third = Definition(thirdGuid, 0, masterId: 1);
        var saved = new[]
        {
            new LotObject(211, 1, [], 0, SavedState(new MultiTileState(1, 2, 3, 4, 5, 6, 211, []))),
            new LotObject(212, 2, [], 0, SavedState(new MultiTileState(1, 2, 3, 4, 5, 6, 211, []))),
            new LotObject(213, 3, [], 0, SavedState(new MultiTileState(1, 2, 3, 4, 5, 6, 211, []))),
        };
        var types = new[]
        {
            new ObjectTypeEntry(masterGuid, 0, 0, 1, 0, "Lead", 0),
            new ObjectTypeEntry(secondGuid, 0, 0, 2, 1, "Part2", 0),
            new ObjectTypeEntry(thirdGuid, 0, 0, 3, 2, "Part3", 0),
        };
        var definitions = new Dictionary<uint, ObjectEntry>
        {
            [masterGuid] = master,
            [secondGuid] = second,
            [thirdGuid] = third,
        };

        var world = SavedLotObjectLoader.Restore(saved, types, guid => definitions.GetValueOrDefault(guid), _ => null);

        var lead = world.ObjectById(211)!;
        var part2 = world.ObjectById(212)!;
        var part3 = world.ObjectById(213)!;

        Assert.Equal((short)0, lead.MultiTileMasterId);
        Assert.Equal((short)211, part2.MultiTileMasterId);
        Assert.Equal((short)211, part3.MultiTileMasterId);
        Assert.Equal((short)212, lead.MultiTileNextId);
        Assert.Equal((short)213, part2.MultiTileNextId);
        Assert.Equal((short)0, part3.MultiTileNextId);
    }
}
