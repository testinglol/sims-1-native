using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave10CrashAndContainmentTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static XObject MakeObject(short id, ObjectPosition position = default, short parent = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        var item = new XObject(id, definition) { Position = position };
        item[ObjectData.AllowedHeightFlags] = 1;
        item[ObjectData.ParentId] = parent;
        return item;
    }

    private static XPerson MakePerson(short id, ObjectPosition position = default, PersonCharacter? saved = null)
    {
        var definition = new short[XObject.DefinitionCount];
        return new XPerson(id, definition)
        {
            Position = position,
            SavedCharacter = saved,
        };
    }

    [Fact]
    public void ObjectsAtTile_TerminatesOnSelfCycleMutualCycleAndMismatchedParentId()
    {
        // Self-referential slot + ParentId cycle: a.Slots[0] = a, a.ParentId = a
        {
            var world = new SimulationWorld();
            var pos = new ObjectPosition(0x20, 0x30, 1);
            var a = world.Add(MakeObject(1, pos));
            a.Slots.Add(a.ClockManagerIndex);
            a[ObjectData.ParentId] = a.ClockManagerIndex;

            Assert.Equal([a], world.ObjectsAtTile(2, 3, 1).ToArray());
        }

        // Mutual 2-node slot + ParentId cycle: a <-> b
        {
            var world = new SimulationWorld();
            var pos = new ObjectPosition(0x20, 0x30, 1);
            var a = world.Add(MakeObject(1, pos));
            var b = world.Add(MakeObject(2, pos, a.ClockManagerIndex));
            a.Slots.Add(b.ClockManagerIndex);
            b.Slots.Add(a.ClockManagerIndex);
            a[ObjectData.ParentId] = b.ClockManagerIndex;
            b[ObjectData.ParentId] = a.ClockManagerIndex;

            Assert.Equal([a, b], world.ObjectsAtTile(2, 3, 1).ToArray());
        }

        // Mismatched ParentId + SlotHierNumber that previously caused an infinite ascent loop
        {
            var world = new SimulationWorld();
            var pos = new ObjectPosition(0x20, 0x30, 1);
            var root = world.Add(MakeObject(1, pos));
            var childA = world.Add(MakeObject(2, pos, root.ClockManagerIndex));
            var childB = world.Add(MakeObject(3, pos, root.ClockManagerIndex));
            var grandchild = world.Add(MakeObject(4, pos, childA.ClockManagerIndex));
            root.Slots.AddRange([childA.ClockManagerIndex, childB.ClockManagerIndex]);
            childA.Slots.Add(grandchild.ClockManagerIndex);
            // Desynced ParentId / SlotHierNumber pointing back to childA itself
            childA[ObjectData.ParentId] = childA.ClockManagerIndex;
            childA[ObjectData.SlotHierNumber] = 0;
            grandchild[ObjectData.ParentId] = grandchild.ClockManagerIndex;
            grandchild[ObjectData.SlotHierNumber] = 0;

            Assert.Equal([root, childA, grandchild, childB], world.ObjectsAtTile(2, 3, 1).ToArray());
        }
    }

    [Fact]
    public void AvatarGeometry_SkipsUnknownSuitsAndResolvesChemsetAliasWithoutThrowing()
    {
        // Uninstalled body/head and unknown extra suit skip cleanly when no fallback exists
        var emptyGameDir = Path.Combine(Path.GetTempPath(), $"sims-wave10-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(emptyGameDir, "GameData", "Skins"));
        try
        {
            var emptyGeometry = AvatarSceneGeometry.For(emptyGameDir, new ObjectRegistry());
            var camera = new LotCamera(0, 64, 64);
            var missingPerson = MakePerson(99, new ObjectPosition(0x40, 0x40, 1),
                new PersonCharacter(0, 0,
                    [("b999ma_missing", ""), ("NONE", ""), ("NONE", ""), ("NONE", ""), ("c999ma_missing", "")],
                    [("suit-nonexistent-wave10.apr", ""), ("suit-chemset-heed-stars104.apr", "")],
                    [], null, []));

            var parts = emptyGeometry.Build(missingPerson, 1, 0, (_, _) => 0f, camera);
            Assert.Empty(parts);
            _ = emptyGeometry.RootPosition(missingPerson);
            Assert.NotNull(emptyGeometry.RootDirection(missingPerson));
        }
        finally
        {
            if (Directory.Exists(emptyGameDir))
                Directory.Delete(emptyGameDir, recursive: true);
        }

        // Real game assets: verify suit-chemset-heed-stars104.apr resolves via suit-chemsuit-heed-stars104
        // and unknown extra suits skip cleanly without crashing.
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var lotCamera = new LotCamera(0, 64, 64);

        var baseParts = geometry.Build(bella, 1, 0, (_, _) => 0f, lotCamera);
        Assert.NotEmpty(baseParts);

        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            ExtraParts = [("suit-nonexistent-wave10.apr", "")],
        };
        var unknownSkippedParts = geometry.Build(bella, 1, 0, (_, _) => 0f, lotCamera);
        Assert.Equal(baseParts.Count, unknownSkippedParts.Count);

        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            ExtraParts = [("suit-chemset-heed-stars104.apr", "")],
        };
        var chemsetParts = geometry.Build(bella, 1, 0, (_, _) => 0f, lotCamera);
        var hasLivinLarge = File.Exists(Path.Combine(gameDir, "ExpansionPack", "ExpansionPack.far"));
        if (hasLivinLarge)
            Assert.True(chemsetParts.Count > baseParts.Count,
                $"Expected chemset-heed-stars104 alias to attach mesh part(s): base={baseParts.Count}, withChemset={chemsetParts.Count}.");

        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            ExtraParts = [("CHEMSET-HEED-STARS104", "")],
        };
        var shortChemsetParts = geometry.Build(bella, 1, 0, (_, _) => 0f, lotCamera);
        if (hasLivinLarge)
            Assert.Equal(chemsetParts.Count, shortChemsetParts.Count);
    }

    [Fact]
    public void AvatarGeometry_HandlesNonePartsWithAnimatorBodyStringModeAndSuitlessOrNullNpcs()
    {
        var emptyGameDir = Path.Combine(Path.GetTempPath(), $"sims-wave10-suitless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(emptyGameDir, "GameData", "Skins"));
        try
        {
            var geometry = AvatarSceneGeometry.For(emptyGameDir, new ObjectRegistry());
            var camera = new LotCamera(0, 64, 64);

            // Null SavedCharacter / PresentationCharacter
            var nullCharNpc = MakePerson(101, new ObjectPosition(0x30, 0x30, 1), saved: null);
            Assert.Empty(geometry.Build(nullCharNpc, 1, 0, (_, _) => 0f, camera));
            _ = geometry.RootPosition(nullCharNpc);
            Assert.NotNull(geometry.RootDirection(nullCharNpc));

            // Short (< 5) Parts list padded cleanly
            var shortPartsNpc = MakePerson(102, new ObjectPosition(0x30, 0x30, 1),
                new PersonCharacter(0, 0, [("NONE", ""), ("NONE", "")], [], [], null, []));
            Assert.Empty(geometry.Build(shortPartsNpc, 1, 0, (_, _) => 0f, camera));
            _ = geometry.RootPosition(shortPartsNpc);

            // Suitless "NONE" NPC with an unknown ExtraPart
            var suitlessNpc = MakePerson(103, new ObjectPosition(0x30, 0x30, 1),
                new PersonCharacter(0, 0,
                    Enumerable.Repeat(("NONE", ""), 5).ToArray(),
                    [("suit-nonexistent-wave10", "")],
                    [], null, []));
            Assert.Empty(geometry.Build(suitlessNpc, 1, 0, (_, _) => 0f, camera));
            _ = geometry.RootPosition(suitlessNpc);
        }
        finally
        {
            if (Directory.Exists(emptyGameDir))
                Directory.Delete(emptyGameDir, recursive: true);
        }

        // With real game BodyStrings: AnimatorBodyStringMode = 2 with "NONE" saved parts resolves BodyStrings defaults
        // without throwing and ignores CurrentOutfit category switching.
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var realGeometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var lotCamera = new LotCamera(0, 64, 64);

        bella.PresentationCharacter = null;
        bella.AnimatorBodyStringMode = 2;
        bella[PersonData.CurrentOutfit] = 2;
        bella.SavedCharacter = bella.SavedCharacter! with
        {
            Parts = Enumerable.Repeat(("NONE", ""), 5).ToArray(),
            ExtraParts = [],
        };
        var mode2Parts = realGeometry.Build(bella, 1, 0, (_, _) => 0f, lotCamera);
        Assert.NotEmpty(mode2Parts);
        _ = realGeometry.RootPosition(bella);
    }

    [Fact]
    public void NativeSceneExporter_HandlesCyclicObjectContainmentAndSimAttachmentWithoutThrowing()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var tempOut = Path.Combine(Path.GetTempPath(), $"sims-wave10-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempOut);
        try
        {
            var boot = WorldBootstrap.Load(gameDir, house05Path!);
            var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
            // Self-referential HEAD slot attachment cycle on Bella
            bella[ObjectData.ParentId] = bella.ClockManagerIndex;
            bella[ObjectData.SlotHierNumber] = 2;
            while (bella.Slots.Count <= 2) bella.Slots.Add(0);
            bella.Slots[2] = bella.ClockManagerIndex;

            // Mutual containment cycle on two world objects
            var pos = new ObjectPosition(0x40, 0x40, 1);
            var objA = boot.World.Add(MakeObject(9001, pos));
            var objB = boot.World.Add(MakeObject(9002, pos));
            objA[ObjectData.ContainerId] = objB.Id;
            objB[ObjectData.ContainerId] = objA.Id;

            var scenePath = SceneExporter.Export(boot.Lot, boot.World, boot.Registry, gameDir, tempOut, 5, 1);
            Assert.True(File.Exists(scenePath));
        }
        finally
        {
            if (Directory.Exists(tempOut))
                Directory.Delete(tempOut, recursive: true);
        }
    }
}
