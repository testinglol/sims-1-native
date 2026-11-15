using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class InteractionMenuTests
{
    [Fact]
    public void ReadPreservesTtabDiscoveryOrderWithoutAlphabeticalSorting()
    {
        var chunk = new ChunkBuilder().S16(3).S16(8);
        chunk.U16(4096).U16(0).S32(0).U32(0).S32(2).U32(0).F32(0).U32(0).S32(0);
        chunk.U16(4097).U16(0).S32(0).U32(0).S32(0).U32(0).F32(0).U32(0).S32(0);
        chunk.U16(4098).U16(0).S32(0).U32(0).S32(1).U32(0).F32(0).U32(0).S32(0);
        var (file, resource) = chunk.InFile("TTAB");
        var table = InteractionTable.Read(file, resource).WithNames(entry => entry.TtaIndex switch
        {
            2 => "Zeta",
            0 => "Alpha",
            1 => "Middle",
            _ => null,
        });

        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2))
        {
            TypeRecord = new TypeRecord(1),
        });
        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(2)
            {
                Interactions = table,
            },
        });

        var choices = InteractionMenus.Read(world, person, target);

        Assert.Equal(["Zeta", "Alpha", "Middle"], choices.Select(choice => choice.ActionText));
        Assert.Equal([2, 0, 1], choices.Select(choice => choice.RawDword10));
    }

    [Fact]
    public void ChangeActionStringAddsMenuChoiceAfterExpandingDynamicTokens()
    {
        var (treeContext, person, host) = PrimitiveTestWorld.Create(stackObject: 5);
        person[PersonData.NeighborId] = 7;
        treeContext.World.Neighbors[7] = new NeighborState(7, PrimitiveTestWorld.Definition()) { Name = "Bella" };
        var bookcase = treeContext.World.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(5) { Name = "Bookcase" },
        });
        host.Strings[(3, 0)] = "$Me: Study $Object";
        treeContext.World.InteractionMenu = new InteractionMenuContext(treeContext.World)
        {
            Current = new ActionQueueSlot { Person = person, Owner = bookcase },
        };

        Assert.Equal(PrimitiveResult.True, ChangeActionStringPrimitive.Execute(treeContext, [3, 0, 0, 0, 1, 0, 0, 0]));

        var choice = Assert.Single(treeContext.World.InteractionMenu.Choices);
        Assert.Equal("Bella: Study Bookcase", choice.ActionText);
        Assert.Equal(bookcase.Id, choice.Parameters[0]);
    }

    [Fact]
    public void MeTokenUsesCatalogNameInsteadOfInternalNeighborName()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2))
        { TypeRecord = new TypeRecord(1) { Name = "user00009 - Mortimer", CatalogName = "Mortimer" } });
        person[PersonData.NeighborId] = 7;
        world.Neighbors[7] = new NeighborState(7, PrimitiveTestWorld.Definition()) { Name = "user00009" };
        var menu = new InteractionMenuContext(world) { Current = new ActionQueueSlot { Person = person, Owner = person } };
        menu.Add("$Me received a point", person.Id, new StackFrame(person.Id, [], []));
        Assert.Equal("Mortimer received a point", Assert.Single(menu.Choices).ActionText);
    }

    [Fact]
    public void DynamicActionStringResolvesActorAndObjectNames()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2))
        {
            TypeRecord = new TypeRecord(1),
        });
        person[PersonData.NeighborId] = 7;
        world.Neighbors[7] = new NeighborState(7, PrimitiveTestWorld.Definition()) { Name = "Bella" };
        var bookcase = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(2) { Name = "Bookcase" },
        });
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = bookcase },
        };
        var frame = new StackFrame(bookcase.Id, [42], [13, 5]);
        person.Temporaries[1] = 7;
        bookcase.Data[9] = 11;

        // Only spellings in the Sims.exe literal table (0x6c4044-0x6c4100) and the Mac ObjectSim.s table expand.
        context.Add("$Me: Study $Object ($Local:1/$Attrib:9/$TimeLocal:0:1/$$Local:1)", bookcase.Id, frame);

        Assert.Equal("Bella: Study Bookcase (5/11/1:05pm/$5)", Assert.Single(context.Choices).ActionText);
        Assert.Equal(bookcase.Id, context.Choices[0].Parameters[0]);
    }

    [Fact]
    public void DynamicActionStringResolvesCatalogAndLocalObjectTokens()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)));
        var bookcase = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(2) { Name = "Bookshelf - Moderate", CatalogName = "Oak Bookcase" },
        });
        var refrigerator = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(3) { Name = "Fridge", CatalogName = "Refrigerator", CatalogDescription = "A cold storage appliance" },
        });
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = bookcase },
        };
        var frame = new StackFrame(bookcase.Id, [], [refrigerator.Id]);

        context.Add("$Object / $NameLocal:0 / $CatalogLocal:0", bookcase.Id, frame);

        Assert.Equal("Oak Bookcase / Refrigerator / A cold storage appliance",
            Assert.Single(context.Choices).ActionText);
    }

    [Fact]
    public void UnknownDynamicActionStringCommandsStayInTheText()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)));
        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = target },
        };

        // FUN_0044bf5a copies a command it cannot match unchanged; spellings absent from Sims.exe are not expanded.
        context.Add("Study $Unknown:2 $DynamicObjectName", target.Id, new StackFrame(target.Id, [], []));

        Assert.Equal("Study $Unknown:2 $DynamicObjectName", Assert.Single(context.Choices).ActionText);
    }

    private sealed class FakeMenuTextSources : ISimulationMenuTextSources
    {
        public string? FamilyName(int familyNumber) => familyNumber == 3 ? "Goth" : null;
        public int? FamilyAssets(int familyNumber) => familyNumber == 3 ? 1234 : null;
        public string? FameTitle(int level) => level == 2 ? "Notable" : null;
    }

    [Fact]
    public void SourceBackedTokensResolveFameTitlesAndFamilyAssets()
    {
        var world = new SimulationWorld { MenuTextSources = new FakeMenuTextSources() };
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)) { TypeRecord = new TypeRecord(1) });
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = person },
        };
        var frame = new StackFrame(person.Id, [], [2, 3]);

        Assert.Equal("Notable / 1234", context.Expand("$FameTitleLocal:0 / $FamilyAssets:1", person, frame));
    }

    [Fact]
    public void ExpandKeepsATokenItCannotResolve()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)) { TypeRecord = new TypeRecord(1) });
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = person },
        };

        Assert.Equal("Go $FameTitleLocal:0", context.Expand("Go $FameTitleLocal:0", person, new StackFrame(person.Id, [], [2])));
    }

    [Fact]
    public void RecognizedCommandWithoutAValueKeepsItsTextEvenInStrictMenus()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)) { TypeRecord = new TypeRecord(1) });
        var context = new InteractionMenuContext(world) { Current = new ActionQueueSlot { Person = person, Owner = person } };

        context.Add("Go $Local:9 and $Attrib:99", person.Id, new StackFrame(person.Id, [], [1]));

        Assert.Equal("Go $Local:9 and $Attrib:99", Assert.Single(context.Choices).ActionText);
    }

    [Fact]
    public void NeighborLocalNamesTheNeighborAndFamilyAndStopsAtAMissingTableEntry()
    {
        var world = new SimulationWorld { MenuTextSources = new FakeMenuTextSources() };
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)) { TypeRecord = new TypeRecord(1) });
        var neighbor = new NeighborState(7, PrimitiveTestWorld.Definition()) { Name = "Bella", TypeRecordKey = 55 };
        neighbor.PersonData[(int)PersonData.FamilyNumber] = 3;
        world.NeighborWalkOrder.Add(neighbor);
        world.SetNeighborTableEntry(1, world.Add(new XObject(2, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(55) }));
        world.SetNeighborTableEntry(2, world.Add(new XObject(3, PrimitiveTestWorld.Definition()) { TypeRecord = new TypeRecord(99) }));
        var context = new InteractionMenuContext(world);
        var frame = new StackFrame(person.Id, [], [1, 2, 1]);

        Assert.Equal("Bella Goth / $NeighborLocal:1 / $NeighborLocal:2", context.Expand("$NeighborLocal:0 / $NeighborLocal:1 / $NeighborLocal:2", person, frame));
        world.Language = 15;
        Assert.Equal("Goth Bella", context.Expand("$NeighborLocal:0", person, frame));
    }

    [Fact]
    public void FamilyTokenReportsTheFamilyNumberToTheCaller()
    {
        var world = new SimulationWorld { MenuTextSources = new FakeMenuTextSources() };
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(2)) { TypeRecord = new TypeRecord(1) });
        var neighbor = new NeighborState(7, PrimitiveTestWorld.Definition()) { Name = "Bella" };
        neighbor.PersonData[(int)PersonData.FamilyNumber] = 3;
        world.NeighborWalkOrder.Add(neighbor);
        var context = new InteractionMenuContext(world);

        Assert.Equal("The Goth home", context.Expand("The $Family home", person, new StackFrame(1, [], [])));
        Assert.Equal((short)3, context.FamilyOutput);
    }
}
