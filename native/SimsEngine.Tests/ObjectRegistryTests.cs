using System.Buffers.Binary;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ObjectRegistryTests
{
    [Fact]
    public void ObjectDefinitionReadsSignedThumbnailGraphicIndex()
    {
        var fields = Enumerable.Range(0, 81).Select(_ => (ushort)0).ToArray();
        fields[(int)ObjdField.BaseGraphic] = 300;
        fields[(int)ObjdField.NumGraphics] = 4;
        fields[(int)ObjdField.ThumbnailGraphic] = 2;
        fields[(int)ObjdField.FrontDirection] = 3;
        var definitionChunk = new ChunkBuilder();
        foreach (var field in fields) definitionChunk.U16(field);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definitionChunk.ToArray())), "thumbnail.iff");
        var chunk = file.Find(ResourceType.Objd, 128)!;

        var definition = Objd.Read(file, chunk);

        Assert.Equal((short)2, definition.ThumbnailGraphicIndex);
        Assert.Equal(7, definition.ThumbnailDirectionIndex);
        fields[(int)ObjdField.ThumbnailGraphic] = ushort.MaxValue;
        definitionChunk = new ChunkBuilder();
        foreach (var field in fields) definitionChunk.U16(field);
        file = new IffFile(ChunkBuilder.File(("OBJD", 128, definitionChunk.ToArray())), "thumbnail.iff");
        definition = Objd.Read(file, file.Find(ResourceType.Objd, 128)!);
        Assert.Equal((short)-1, definition.ThumbnailGraphicIndex);
    }

    [Fact]
    public void ObjectEntryReadsLocalizedCatalogNameAndDescription()
    {
        var fields = Enumerable.Range(0, 81).Select(_ => (ushort)0).ToArray();
        fields[(int)ObjdField.Guid1] = 0x1234;
        fields[(int)ObjdField.CatalogStringsId] = 7;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var catalog = new ChunkBuilder().S16(-3).S16(4)
            .U8(1).CString("English Chair").CString("")
            .U8(1).CString("A sturdy seat").CString("")
            .U8(5).CString("French Chair").CString("")
            .U8(5).CString("Une chaise solide").CString("").ToArray();
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definition.ToArray()), ("CTSS", 7, catalog)), "catalog.iff");
        var objd = file.Find(ResourceType.Objd, 128)!;
        var entry = new ObjectEntry(0, ObjectScan.Objects, file.Source, file, objd, Objd.Read(file, objd), null);

        Assert.Equal("French Chair", entry.CatalogString(0, 5));
        Assert.Equal("Une chaise solide", entry.CatalogString(1, 5));
        Assert.Equal("English Chair", entry.CatalogString(0, 2));
        Assert.Null(entry.CatalogString(2, 1));
    }

    [Fact]
    public void ObjectEntryLoadsEnabledObjfActionFunctionsByObjdChunkId()
    {
        var fields = Enumerable.Range(0, 81).Select(_ => (ushort)0).ToArray();
        fields[(int)ObjdField.Guid1] = 0x1234;
        fields[(int)ObjdField.UsesFnTable] = 1;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var objf = new ChunkBuilder().U32(0).U32(1).Tag("fJBO").U32(3)
            .U16(10).U16(20).U16(30).U16(40).U16(50).U16(60).ToArray();
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definition.ToArray()), ("OBJf", 128, objf)), "functions.iff");
        var objd = file.Find(ResourceType.Objd, 128)!;
        var entry = new ObjectEntry(0, ObjectScan.Objects, file.Source, file, objd, Objd.Read(file, objd), null);

        Assert.Equal(new ushort[] { 20, 40, 60 }, entry.ActionFunctions);
        Assert.Equal(new (ushort Guard, ushort Action)[] { (10, 20), (30, 40), (50, 60) }, entry.FunctionEntries);
    }

    [Fact]
    public void ObjectEntryBuildsLegacyActionFunctionsFromSourceObjdFields()
    {
        var fields = Enumerable.Range(0, 81).Select(_ => (ushort)0).ToArray();
        fields[(int)ObjdField.Guid1] = 0x1234;
        fields[31] = 0x4567;
        var definition = new ChunkBuilder();
        foreach (var field in fields) definition.U16(field);
        var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definition.ToArray())), "legacy-functions.iff");
        var objd = file.Find(ResourceType.Objd, 128)!;
        var entry = new ObjectEntry(0, ObjectScan.Objects, file.Source, file, objd, Objd.Read(file, objd), null);

        Assert.Equal(30, entry.ActionFunctions.Count);
        Assert.Equal((ushort)0x4567, entry.ActionFunctions[25]);
        Assert.All(entry.FunctionEntries, function => Assert.Equal((ushort)0, function.Guard));
    }

    private static byte[] Archive(string name, byte[] payload)
    {
        var encodedName = Encoding.ASCII.GetBytes(name);
        const int dataOffset = 16;
        var manifestOffset = dataOffset + payload.Length;
        var bytes = new byte[manifestOffset + 4 + 16 + encodedName.Length];
        "FAR!byAZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)manifestOffset);
        payload.CopyTo(bytes, dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestOffset), 1);
        var record = manifestOffset + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record + 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record + 8), dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(record + 12), (uint)encodedName.Length);
        encodedName.CopyTo(bytes, record + 16);
        return bytes;
    }

    private static byte[] Definition(uint guid, bool disabled = false, ushort type = 0, ushort masterId = 0,
        ushort subIndex = 0, ushort buildModeType = 0, ushort treeTableId = 0) =>
        ChunkBuilder.File(("OBJD", 4096, DefinitionBody(guid, disabled, type, masterId, subIndex, buildModeType, treeTableId)));

    private static byte[] DefinitionBody(uint guid, bool disabled = false, ushort type = 0, ushort masterId = 0,
        ushort subIndex = 0, ushort buildModeType = 0, ushort treeTableId = 0)
    {
        var body = new ChunkBuilder();
        for (var field = 0; field <= (int)ObjdField.BuildModeType; field++)
            body.U16(field switch
            {
                9 => type,
                7 => treeTableId,
                10 => masterId,
                11 => subIndex,
                14 => (ushort)guid,
                15 => (ushort)(guid >> 16),
                16 => (ushort)(disabled ? 1 : 0),
                69 => buildModeType,
                _ => 0,
            });
        return body.ToArray();
    }

    private static void MarkHousePartyInstalled(string game)
    {
        Directory.CreateDirectory(Path.Combine(game, "ExpansionShared", "Sound"));
        File.WriteAllBytes(Path.Combine(game, "ExpansionShared", "Sound", "houseparty.hot"), []);
        Directory.CreateDirectory(Path.Combine(game, "GameData"));
        File.WriteAllBytes(Path.Combine(game, "GameData", "Ranger2.iff"), []);
    }

    [Fact]
    public void LaterScansReplaceAGuidAndDisabledDefinitionsAreSkippedOnRequest()
    {
        var game = Directory.CreateTempSubdirectory("registry").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "GameData", "Objects"));
            Directory.CreateDirectory(Path.Combine(game, "ExpansionPack2"));
            MarkHousePartyInstalled(game);
            File.WriteAllBytes(Path.Combine(game, "GameData", "Objects", "Chair.iff"), Definition(0x1234));
            File.WriteAllBytes(Path.Combine(game, "GameData", "Objects", "Lamp.iff"), Definition(0x5678, disabled: true));
            File.WriteAllBytes(Path.Combine(game, "ExpansionPack2", "Chair2.iff"), Definition(0x1234));

            var registry = ObjectRegistry.Scan(game);
            Assert.Equal([ObjectScan.Objects, ObjectScan.HouseParty, ObjectScan.ExpansionShared], registry.Folders.Select(folder => folder.Scan));
            Assert.Equal(3, registry.Entries.Count);
            Assert.Equal(ObjectScan.HouseParty, registry.Find(0x1234)!.Scan);
            Assert.Equal(2, registry.Find(0x1234)!.Index);
            Assert.NotNull(registry.Find(0x5678));

            Assert.Null(ObjectRegistry.Scan(game, skipDisabled: true).Find(0x5678));
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void FlatInstallationScansRootObjectsAndExpansionFarPackagesInGameOrder()
    {
        var game = Directory.CreateTempSubdirectory("registry-flat").FullName;
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(game, "Objects"));
            File.WriteAllBytes(Path.Combine(objects.FullName, "Chair.iff"), Definition(0x1234));
            File.WriteAllBytes(Path.Combine(game, "ExpansionPack2.far"), Archive("Furniture/Chair2.iff", Definition(0x1234)));
            MarkHousePartyInstalled(game);

            var registry = ObjectRegistry.Scan(game);

            Assert.Equal(2, registry.Entries.Count);
            Assert.Equal(ObjectScan.Objects, registry.Entries[0].Scan);
            Assert.Equal(ObjectScan.HouseParty, registry.Find(0x1234)!.Scan);
            Assert.Equal(Path.Combine(game, "ExpansionPack2.far") + "::Furniture/Chair2.iff", registry.Find(0x1234)!.Source);
            Assert.Equal([(ObjectScan.HouseParty, Path.Combine(game, "ExpansionPack2.far"))], registry.Archives);
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void FlatInstallationScansNamedDownloadPackagesAfterPeopleAndBeforeExpansionPacks()
    {
        var game = Directory.CreateTempSubdirectory("registry-flat-downloads").FullName;
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(game, "Objects"));
            File.WriteAllBytes(Path.Combine(objects.FullName, "Chair.iff"), Definition(0x1234));
            var people = Directory.CreateDirectory(Path.Combine(game, "Characters"));
            File.WriteAllBytes(Path.Combine(people.FullName, "Person.iff"), Definition(0x5678));
            var global = Directory.CreateDirectory(Path.Combine(game, "Global"));
            File.WriteAllBytes(Path.Combine(global.FullName, "Global.iff"), Definition(0x9abc));
            var package = Directory.CreateDirectory(Path.Combine(game, "DownloadedChair"));
            File.WriteAllBytes(Path.Combine(package.FullName, "DownloadedChair.far"), Archive("Chair.iff", Definition(0x1234)));
            File.WriteAllBytes(Path.Combine(game, "ExpansionPack2.far"), Archive("Chair2.iff", Definition(0x1234)));
            MarkHousePartyInstalled(game);

            var registry = ObjectRegistry.Scan(game);

            Assert.Equal([ObjectScan.Objects, ObjectScan.People, ObjectScan.Downloads, ObjectScan.HouseParty],
                registry.Entries.Select(entry => entry.Scan));
            Assert.Equal(ObjectScan.Downloads, registry.Entries[2].Scan);
            Assert.Equal(ObjectScan.HouseParty, registry.Find(0x1234)!.Scan);
            Assert.NotNull(registry.Find(0x5678));
            Assert.Null(registry.Find(0x9abc));
            Assert.Contains(registry.Folders, folder => folder.Scan == ObjectScan.Downloads && Path.GetFileName(folder.Item2) == "DownloadedChair");
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void AlternativeTypeIsTheFirstSameFileSameClassMasterWithSubIndexMinusOne()
    {
        var game = Directory.CreateTempSubdirectory("registry-alternative-type").FullName;
        try
        {
            var objects = Directory.CreateDirectory(Path.Combine(game, "Objects"));
            File.WriteAllBytes(Path.Combine(objects.FullName, "A-Group.iff"), ChunkBuilder.File(
                ("OBJD", 128, DefinitionBody(0x1001, type: 0, masterId: 7, subIndex: ushort.MaxValue, buildModeType: 4)),
                ("OBJD", 129, DefinitionBody(0x1002, type: 0, masterId: 7, subIndex: ushort.MaxValue, buildModeType: 5)),
                ("OBJD", 130, DefinitionBody(0x1003, type: 0, masterId: 7, subIndex: 2, buildModeType: 9))));
            File.WriteAllBytes(Path.Combine(objects.FullName, "B-OtherFile.iff"),
                Definition(0x1004, type: 0, masterId: 7, subIndex: ushort.MaxValue, buildModeType: 6));
            File.WriteAllBytes(Path.Combine(objects.FullName, "E-Portal.iff"),
                Definition(0x1005, type: 8, masterId: 7, subIndex: ushort.MaxValue, buildModeType: 3));
            File.WriteAllBytes(Path.Combine(objects.FullName, "F-Single.iff"),
                Definition(0x1006, type: 0, buildModeType: 8));

            var registry = ObjectRegistry.Scan(game);
            var part = registry.Find(0x1003)!;

            Assert.Equal("A-Group.iff", Path.GetFileName(registry.FindAlternativeType(part)!.Source));
            Assert.Equal((ushort)4, registry.FindAlternativeType(part)!.Definition[ObjdField.BuildModeType]);
            Assert.Equal("E-Portal.iff", Path.GetFileName(registry.FindAlternativeType(registry.Find(0x1005)!)!.Source));
            Assert.Null(registry.FindAlternativeType(registry.Find(0x1006)!));
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void APartResolvesTheMastersTableIdInItsOwnPrivateFile()
    {
        static ObjectEntry Entry(string name, uint guid, ushort treeTableId, ushort subIndex, ushort action, ushort guard)
        {
            var definitionFile = new IffFile(Definition(guid, type: 0, masterId: 7, subIndex: subIndex, treeTableId: treeTableId), name);
            var definition = Assert.Single(definitionFile.OfType(ResourceType.Objd)).Data.ToArray();
            var ttab = new ChunkBuilder().S16(1).S16(8).U16(action).U16(guard).S32(0).U32(0).S32(0)
                .S32(0).F32(0).S32(0).S32(0).ToArray();
            var file = new IffFile(ChunkBuilder.File(("OBJD", 128, definition), ("TTAB", 129, ttab)), name);
            var chunk = file.Find(ResourceType.Objd, 128)!;
            return new ObjectEntry(0, ObjectScan.Objects, name, file, chunk, Objd.Read(file, chunk), null);
        }

        var master = Entry("Mailbox master.iff", 0x1001, 129, ushort.MaxValue, 4099, 4105);
        var part = Entry("Mailbox part.iff", 0x1002, ushort.MaxValue, 2, 4108, 4107);

        var resolved = Assert.Single(part.FindInteractionTable(masterFallback: master)!.Interactions);

        Assert.Equal((ushort)4108, resolved.ActionTree);
        Assert.Equal((ushort)4107, resolved.GuardTree);
    }

    [Fact]
    public void MasterlessNegativeTreeTableIsNotEagerlyResolvedDuringLotRestore()
    {
        var file = new IffFile(Definition(0x1234, treeTableId: ushort.MaxValue), "masterless-no-table.iff");
        var chunk = Assert.Single(file.OfType(ResourceType.Objd));
        var entry = new ObjectEntry(0, ObjectScan.Objects, file.Source, file, chunk, Objd.Read(file, chunk), null);

        Assert.True(entry.HasNoMasterInteractionTable);
        Assert.Null(entry.FindInteractionTable());
    }
}
