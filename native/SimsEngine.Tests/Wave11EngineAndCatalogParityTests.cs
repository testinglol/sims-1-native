using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class Wave11EngineAndCatalogParityTests
{
    private static string? LocateRuntimeGameDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var runtimeGame = Path.Combine(dir.FullName, "runtime", "game");
            if (Directory.Exists(runtimeGame)) return runtimeGame;
            dir = dir.Parent;
        }
        return null;
    }

    private static ObjectEntry MakeObjectEntry(
        uint guid,
        ushort treeTableId,
        ushort masterId = 0,
        ushort subIndex = 0,
        ushort type = 4)
    {
        var fields = new ushort[XObject.DefinitionCount];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.SubIndex] = subIndex;
        fields[(int)ObjdField.TreeTableId] = treeTableId;
        fields[(int)ObjdField.Guid1] = unchecked((ushort)guid);
        fields[(int)ObjdField.Guid2] = unchecked((ushort)(guid >> 16));
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        var (file, chunk) = builder.InFile("OBJD");
        return new ObjectEntry(0, ObjectScan.Objects, $"{guid:X8}.iff", file, chunk, Objd.Read(file, chunk), null);
    }

    [Fact]
    public void FamilyCreateNew_DefaultsUnhousedValueAndFlagsToZeroWhilePreserving20000Budget()
    {
        var family = Family.CreateNew(familyNumber: 7, houseNumber: 0, members: [0x11112222u]);

        Assert.Equal(0, family.Value);
        Assert.Equal(20_000, family.Budget);
        Assert.Equal(0u, family.Flags);
        Assert.Equal(20_000, family.Budget + family.Value);

        var customized = Family.CreateNew(familyNumber: 8, houseNumber: 5, members: [0x33334444u],
            budget: 12_500, value: 7_500, flags: 3u);
        Assert.Equal(12_500, customized.Budget);
        Assert.Equal(7_500, customized.Value);
        Assert.Equal(3u, customized.Flags);
    }

    [Fact]
    public void FindInteractionTable_ReturnsNullWithoutThrowingForMinusOneTreeTableIds()
    {
        var masterless = MakeObjectEntry(0x10010001u, treeTableId: ushort.MaxValue);
        Assert.Null(masterless.FindInteractionTable());

        var part = MakeObjectEntry(0x10010002u, treeTableId: ushort.MaxValue, masterId: 1, subIndex: 0);
        var invalidMaster = MakeObjectEntry(0x10010003u, treeTableId: ushort.MaxValue, masterId: 1, subIndex: ushort.MaxValue);
        Assert.Null(part.FindInteractionTable(masterFallback: invalidMaster));
    }

    [Fact]
    public void ObjectRegistryScan_FindInteractionTableThrowsZeroExceptionsAcrossAllEntries()
    {
        var gameDir = LocateRuntimeGameDirectory();
        if (gameDir is null || !Directory.Exists(gameDir))
            return;

        var charactersPath = Path.Combine(gameDir, "UserData", "Characters");
        var registry = ObjectRegistry.Scan(gameDir, Directory.Exists(charactersPath) ? charactersPath : null);
        var library = GlobalLibrary.Open(gameDir);

        Assert.NotEmpty(registry.Entries);
        foreach (var entry in registry.Entries)
        {
            var scope = library.ScopeOf(entry);
            var master = registry.FindAlternativeType(entry);
            Assert.Null(Record.Exception(() => entry.FindInteractionTable(scope, language: 1, masterFallback: master)));
            if (entry.Definition[ObjdField.TreeTableId] == ushort.MaxValue)
                Assert.Null(Record.Exception(() => entry.FindInteractionTable()));
        }
    }

    [Fact]
    public void SavedLotObjectLoaderCreateInstance_RejectsMultiTileCatalogMasterUnlessToolObjectIsTrue()
    {
        const uint masterGuid = 0x20020001u;
        const uint partGuid = 0x20020002u;
        var masterEntry = MakeObjectEntry(masterGuid, treeTableId: 0, masterId: 9, subIndex: ushort.MaxValue, type: 4);
        var partEntry = MakeObjectEntry(partGuid, treeTableId: 0, masterId: 9, subIndex: 0, type: 4);

        var registry = new ObjectRegistry();
        registry.RegisterFile(masterEntry.File, masterEntry.Source, ObjectScan.Objects);
        registry.RegisterFile(partEntry.File, partEntry.Source, ObjectScan.Objects);

        var library = GlobalLibrary.Open(Path.Combine(Path.GetTempPath(), $"sims-wave11-noglobal-{Guid.NewGuid():N}"));
        var world = new SimulationWorld();

        Assert.Null(SavedLotObjectLoader.CreateInstance(
            world, masterGuid, registry, library, language: 1, registerSavedType: false, toolObject: false));

        var toolMaster = SavedLotObjectLoader.CreateInstance(
            world, masterGuid, registry, library, language: 1, registerSavedType: false, toolObject: true);
        Assert.NotNull(toolMaster);
        Assert.Equal(ObjectClass.MultiTileObject, toolMaster!.RuntimeClass);

        var livePart = SavedLotObjectLoader.CreateInstance(
            world, partGuid, registry, library, language: 1, registerSavedType: false, toolObject: false);
        Assert.NotNull(livePart);
    }

    [Fact]
    public void GenericSimsCall11_WithCompanionSnapshotsBothScopesAndWritesClockAndGuidsToMe()
    {
        const int meGuid = 0x11110001;
        const int selectedGuid = 0x22220002;
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(meGuid),
        });
        var selected = world.Add(new XPerson(101, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(selectedGuid),
        });
        me[PersonData.FamilyNumber] = 7;
        selected[PersonData.FamilyNumber] = 12;
        world.PersonRecordKeys.AddRange([meGuid, selectedGuid]);
        world.Clock.Restore(10, 20, 0);

        me.SetInteractionExecutorFloat(9, 42f);
        me.SetInteractionExecutorFloat(6, -15f);
        me.InteractionBehaviorSavedWord = 111;
        me.SetInteractionExecutorWord(0x62c, 112);

        selected.SetInteractionExecutorFloat(9, 88f);
        selected.SetInteractionExecutorFloat(6, 33f);
        selected.InteractionBehaviorSavedWord = 222;
        selected.SetInteractionExecutorWord(0x62c, 223);

        var meScope = world.InteractionExecutorScope(me);
        meScope.AddOrIncrement(1, selectedGuid, 1);

        me.Temporaries[0] = 21;
        me.Temporaries[1] = 1;
        me.Temporaries[2] = 25;
        me.Temporaries[3] = 0;

        byte[] operand = [0x11, 0, 0, 0, 0, 0, 0, 0];
        var result = GenericSimsCallPrimitive.Execute(
            new TreeContext(world, me, new StackFrame(0, [], [0, 0])), operand);

        Assert.Equal(PrimitiveResult.True, result);

        static short ReadScopeWord(InteractionScopeTable scope, int key, int mode)
        {
            short cursor = -1;
            Assert.True(scope.TryFindNext(key, ref cursor, mode, out var value),
                $"Expected scope entry ({key}, {mode}).");
            return unchecked((short)value);
        }

        var selectedScope = world.InteractionExecutorScope(selected);
        Assert.Equal((short)88, ReadScopeWord(selectedScope, 2, 0));
        Assert.Equal((short)33, ReadScopeWord(selectedScope, 2, 1));
        Assert.Equal((short)222, ReadScopeWord(selectedScope, 2, 9));
        Assert.Equal((short)223, ReadScopeWord(selectedScope, 2, 10));

        Assert.Equal((short)42, ReadScopeWord(meScope, 2, 0));
        Assert.Equal((short)-15, ReadScopeWord(meScope, 2, 1));
        Assert.Equal((short)111, ReadScopeWord(meScope, 2, 9));
        Assert.Equal((short)112, ReadScopeWord(meScope, 2, 10));

        Assert.Equal((short)11, ReadScopeWord(meScope, 2, 7));
        Assert.Equal((short)45, ReadScopeWord(meScope, 2, 8));
        short selectedClockCursor = -1;
        Assert.False(selectedScope.TryFindNext(2, ref selectedClockCursor, 7, out _));
        selectedClockCursor = -1;
        Assert.False(selectedScope.TryFindNext(2, ref selectedClockCursor, 8, out _));

        var pending = world.PendingLotSwitch;
        Assert.NotNull(pending);
        Assert.Equal((short)21, pending.RawTargetHouse);
        Assert.Equal(meGuid, pending.PersonGuid);
        Assert.Equal((short)7, pending.FamilyNumber);
        Assert.Equal(selectedGuid, pending.CompanionGuid);
    }
}
