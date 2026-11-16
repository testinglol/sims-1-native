using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests.Simulation;

public sealed class SimAnticsRuntimeThreadResolutionTests
{
    private static ChunkBuilder Persist(int version, string tag, bool swap = false) =>
        new ChunkBuilder().U8(swap ? (byte)1 : (byte)0).U8(0).U8(0).U8(0).S32(version).Tag(new string(tag.Reverse().ToArray()));

    private static short[] PersonDefinition()
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = XPerson.PersonType;
        return definition;
    }

    private static InteractionTable Table(ushort guard = 0)
    {
        var builder = new ChunkBuilder().S16(1).S16(6).U16(4096).U16(guard).S32(0)
            .U32(0).S32(7).S32(0).F32(0).S32(0).S32(-1);
        var (file, chunk) = builder.InFile("TTAB");
        return InteractionTable.Read(file, chunk);
    }

    private static Bhav Tree(params (ushort Opcode, byte True, byte False, byte[] Operand)[] code)
    {
        var builder = new ChunkBuilder().U16(0x8002).S16((short)code.Length).U8(0).U8(4).U8(0).U8(0).U32(0);
        foreach (var item in code) builder.U16(item.Opcode).U8(item.True).U8(item.False).Raw(item.Operand);
        var (file, chunk) = builder.InFile("BHAV");
        return Bhav.Read(file, chunk);
    }

    [Fact]
    public void Version73Format4NeighborsReadFull256ShortPersonDataWithoutDrift()
    {
        var builder = Persist(0x49, "NBRS").U32(2)
            // Format 4 person records store 256 16-bit words of character data.
            .U32(1).S32(4).EvenString("Mortimer").S32(0).S32(1);
        for (var i = 0; i < 0x100; i++) builder.S16((short)i);
        builder.S16(34).U32(0xC07F6184)
            .S32(-1).U32(0)
            .U32(1).S32(4).EvenString("Bella").S32(0).S32(1);
        for (var i = 0; i < 0x100; i++) builder.S16((short)(i + 100));
        builder.S16(33).U32(0x807F6184)
            .S32(-1).U32(0);

        var (file, chunk) = builder.InFile("NBRS");
        var neighbors = Neighbors.Read(file, chunk);

        Assert.Equal(2, neighbors.SlotCount);
        Assert.Equal(2, neighbors.Entries.Count);

        var mortimer = neighbors.Entries[0];
        Assert.Equal(0, mortimer.Slot);
        Assert.Equal("Mortimer", mortimer.Name);
        Assert.Equal(34, mortimer.NeighborId);
        Assert.Equal(0xC07F6184u, mortimer.Guid);
        Assert.NotNull(mortimer.PersonData);
        Assert.Equal(256, mortimer.PersonData!.Count);
        Assert.Equal((short)0, mortimer.PersonData[0]);
        Assert.Equal((short)255, mortimer.PersonData[255]);

        var bella = neighbors.Entries[1];
        Assert.Equal(1, bella.Slot);
        Assert.Equal("Bella", bella.Name);
        Assert.Equal(33, bella.NeighborId);
        Assert.Equal(0x807F6184u, bella.Guid);
        Assert.NotNull(bella.PersonData);
        Assert.Equal(256, bella.PersonData!.Count);
        Assert.Equal((short)100, bella.PersonData[0]);
        Assert.Equal((short)355, bella.PersonData[255]);
    }

    [Fact]
    public void RelationshipPrimitiveResolvesNeighborFromWalkOrderSlot()
    {
        var world = new SimulationWorld();
        var mortimer = world.Add(new XPerson(1, PersonDefinition()));
        var cassandra = world.Add(new XPerson(2, PersonDefinition()));

        mortimer[PersonData.NeighborId] = 34;
        cassandra[PersonData.NeighborId] = 32;

        var mortimerNeighbor = new NeighborState(34, PersonDefinition());
        var cassandraNeighbor = new NeighborState(32, PersonDefinition());

        world.Neighbors[34] = mortimerNeighbor;
        world.Neighbors[32] = cassandraNeighbor;

        world.NeighborWalkOrder.Add(cassandraNeighbor);
        world.NeighborWalkOrder.Add(mortimerNeighbor);

        // SetToNext mode 5 sets the stack object to 1-based neighbor walk order slots.
        var context = new TreeContext(world, mortimer, new StackFrame(2, [], [0, 0]) { StackObject = 1 });

        mortimerNeighbor.Relationships.Resize(32, 1);
        mortimerNeighbor.Relationships.Set(32, 0, 85);

        // Relationship opcode 0x1A flag 2 reads the neighbor relationship value into the target variable.
        byte[] operand = [0, 0, 2, 0, (byte)DataOwner.Temporary, 0, 0, 0];
        var result = RelationshipPrimitive.Execute(context, operand);

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(85, mortimer.Temporaries[0]);
    }

    [Fact]
    public void InteractionTestEvaluatorCatchesSimulationExceptionsAndFailsGracefully()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)));

        // Opcode 0x1A raises error 21 when a required stack object is missing from the world.
        byte[] relOperand = [0, 0, 2, 0, (byte)DataOwner.Temporary, 0, 0, 0];
        var tree = Tree((0x1A, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, relOperand));

        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(22)
            {
                Interactions = Table(guard: 4100),
                BehaviorScope = new TreeTable((4100, tree))
            }
        });

        var action = new ActionQueueSlot
        {
            Person = person,
            Owner = target,
            InteractionId = 7
        };

        // Exceptions during interaction guard evaluation fail the interaction check and record the error
        // without terminating the sim thread.
        InteractionTestEvaluator.Evaluate(world, person, [action], autonomy: true);

        Assert.False((action.Flags & InteractionTestEvaluator.Passed) != 0);
        Assert.NotEqual(0, action.Flags & InteractionTestEvaluator.Evaluated);
        var error = Assert.Single(world.InteractionGuardErrors);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, error.Error);
        Assert.Empty(world.ThreadErrors);
    }
}
