using SimsEngine.Formats;
using SimsEngine.Content;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class AutonomyCandidateEligibilityTests
{
    [Fact]
    public void RequiresTheResolvedMultitileRootAndAtLeastOneAllowedInteraction()
    {
        var root = new XObject(1, PrimitiveTestWorld.Definition());
        var part = new XObject(2, PrimitiveTestWorld.Definition());
        var disallowed = Table((100, 0), (20, 0x80));
        var allowed = Table((100, 0), (20, 0x80), (99, 0));

        Assert.False(AutonomyCandidateEligibility.IsEligible(root, part, allowed));
        Assert.False(AutonomyCandidateEligibility.IsEligible(root, root, null));
        Assert.False(AutonomyCandidateEligibility.IsEligible(root, root, disallowed));
        Assert.True(AutonomyCandidateEligibility.IsEligible(root, root, allowed));
    }

    [Fact]
    public void WorldInitializesAndCanClearTheSourceManagerBit()
    {
        var world = new SimulationWorld();
        var root = world.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        var part = world.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        var eligibleTable = Table((99, 0));

        Assert.False(world.InitializeAutonomyCandidate(part, root, eligibleTable));
        Assert.True(world.InitializeAutonomyCandidate(root, root, eligibleTable));
        Assert.Equal(0x00080000u, world.ClockManager.FlagsAt(root.ClockManagerIndex) & 0x00080000u);
        Assert.False(world.InitializeAutonomyCandidate(root, root, Table((100, 0))));
        Assert.Equal(0u, world.ClockManager.FlagsAt(root.ClockManagerIndex) & 0x00080000u);
    }

    [Fact]
    public void TreeTableLookupUsesTheDefinitionIdAndFallsThroughToSemiGlobal()
    {
        const ushort treeTableId = 7;
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = 0x5678;
        fields[(int)ObjdField.TreeTableId] = treeTableId;
        var objdBody = new ChunkBuilder();
        foreach (var field in fields) objdBody.U16(field);
        var privateFile = new IffFile(ChunkBuilder.File(("OBJD", 128, objdBody.ToArray())), "private.iff");
        var objd = privateFile.OfType(ResourceType.Objd).Single();
        var entry = new ObjectEntry(0, ObjectScan.Objects, "private.iff", privateFile, objd, Objd.Read(privateFile, objd), null);
        var ttab = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(0)
            .S32(0).U32(0).F32(0).U32(12).S32(0);
        var unrelatedGlobal = new IffFile(ChunkBuilder.File(("TTAB", treeTableId, new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(0)
            .S32(0).U32(0).F32(0).U32(99).S32(0).ToArray())), "Global.iff");
        var semiGlobal = new IffFile(ChunkBuilder.File(("TTAB", treeTableId, ttab.ToArray())), "SemiGlobal.iff");
        var scope = new BehaviorScope(unrelatedGlobal, privateFile, semiGlobal);

        var table = entry.FindInteractionTable(scope);

        Assert.Equal((uint)12, Assert.Single(table!.Interactions).AutonomyThreshold);
    }

    [Fact]
    public void TreeTableMinusOneRequiresItsOriginalMasterFallback()
    {
        static ObjectEntry Entry(ushort treeTableId, ushort interactionGroup, string name)
        {
            var fields = new ushort[XObject.DefinitionCount];
            fields[(int)ObjdField.Guid1] = 0x5678;
            fields[(int)ObjdField.TreeTableId] = treeTableId;
            fields[(int)ObjdField.InteractionGroup] = interactionGroup;
            var body = new ChunkBuilder();
            foreach (var field in fields) body.U16(field);
            var file = new IffFile(ChunkBuilder.File(("OBJD", 128, body.ToArray())), name);
            var objd = file.OfType(ResourceType.Objd).Single();
            return new ObjectEntry(0, ObjectScan.Objects, name, file, objd, Objd.Read(file, objd), null);
        }

        var instance = Entry(ushort.MaxValue, 44, "instance.iff");
        var master = Entry(9, 3, "master.iff");
        // Autonomy evaluation takes the interaction table ID from the master definition but resolves the
        // chunk within the candidate object's own package.
        var table = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(0)
            .S32(0).U32(0).F32(0).U32(12).S32(0);
        var instanceFile = new IffFile(ChunkBuilder.File(("OBJD", 128, new ChunkBuilder().Raw(instance.Chunk.Data.Span).ToArray()), ("TTAB", 9, table.ToArray())), "instance.iff");
        var instanceObjd = instanceFile.OfType(ResourceType.Objd).Single();
        instance = instance with { File = instanceFile, Chunk = instanceObjd, Definition = Objd.Read(instanceFile, instanceObjd) };
        Assert.Null(instance.FindInteractionTable());
        Assert.Equal((uint)12, Assert.Single(instance.FindInteractionTable(masterFallback: master)!.Interactions).AutonomyThreshold);
    }

    [Fact]
    public void InteractionNamesUseTheRequestedLanguageAndEnglishFallback()
    {
        const ushort treeTableId = 7;
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = 0x5678;
        fields[(int)ObjdField.TreeTableId] = treeTableId;
        var objdBody = new ChunkBuilder();
        foreach (var field in fields) objdBody.U16(field);
        var ttab = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(0)
            .S32(0).U32(0).F32(0).U32(12).S32(0);
        var names = new ChunkBuilder().S16(-3).S16(2)
            .U8(1).CString("Use").CString("")
            .U8(2).CString("Utiliser").CString("");
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, objdBody.ToArray()), ("TTAB", treeTableId, ttab.ToArray()),
            ("TTAs", treeTableId, names.ToArray())), "localized.iff");
        var objdChunk = file.OfType(ResourceType.Objd).Single();
        var entry = new ObjectEntry(0, ObjectScan.Objects, "localized.iff", file, objdChunk, Objd.Read(file, objdChunk), null);

        Assert.Equal("Use", Assert.Single(entry.FindInteractionTable(language: 1)!.Interactions).Name);
        Assert.Equal("Utiliser", Assert.Single(entry.FindInteractionTable(language: 2)!.Interactions).Name);
        Assert.Equal("Use", Assert.Single(entry.FindInteractionTable(language: 3)!.Interactions).Name);
    }

    [Fact]
    public void ZeroTreeTableIdDoesNotLoadTheDefaultTtab()
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Guid1] = 0x1234;
        var objdBody = new ChunkBuilder();
        foreach (var field in fields) objdBody.U16(field);
        var ttab = new ChunkBuilder().S16(1).S16(8).U16(4100).U16(0).S32(0).U32(0)
            .S32(0).U32(0).F32(0).U32(0).S32(0);
        var privateFile = new IffFile(ChunkBuilder.File(("OBJD", 128, objdBody.ToArray()), ("TTAB", 128, ttab.ToArray())), "default.iff");
        var objd = privateFile.OfType(ResourceType.Objd).Single();
        var entry = new ObjectEntry(0, ObjectScan.Objects, "default.iff", privateFile, objd, Objd.Read(privateFile, objd), null);

        Assert.Null(entry.FindInteractionTable());
    }

    private static InteractionTable Table(params (uint Threshold, uint Flags)[] rows)
    {
        var chunk = new ChunkBuilder().S16((short)rows.Length).S16(8);
        foreach (var (threshold, flags) in rows)
            chunk.U16(4100).U16(0).S32(0).U32(flags).S32(0).U32(0).F32(0).U32(threshold).S32(0);
        var (file, resource) = chunk.InFile("TTAB");
        return InteractionTable.Read(file, resource);
    }
}
