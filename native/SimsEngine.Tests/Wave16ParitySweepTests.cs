using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using SdlSceneLighting = SimsSDL.Core.SceneLighting;

namespace SimsEngine.Tests;

public sealed class Wave16ParitySweepTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static RoomMap RoomWithVerticalWall(int wallX)
    {
        var records = new byte[64 * 64 * 8];
        for (var y = 0; y < 64; y++)
        {
            records[(7 * 64 + y) * 8] |= 4;
            records[(wallX * 64 + y) * 8] = 1;
            records[(wallX * 64 + y) * 8 + 2] = 1;
        }
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(records);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "wave16-wall-test.iff");
        return RoomMap.Build(file, 1, 64, 64);
    }

    private static RoomMap RoomMapWithGroundAndRooms(int specialX, int specialY, byte groundClass)
    {
        const int width = 8;
        const int height = 8;
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        var water = new byte[width * height];
        Array.Fill(water, (byte)0x10);
        water[specialX * height + specialY] = groundClass;
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            byte wallFlags = 0;
            if (x == 4 && y == 4) wallFlags = 0x0f;
            else if (x == 3 && y == 4) wallFlags = 0x04;
            else if (x == 5 && y == 4) wallFlags = 0x01;
            else if (x == 4 && y == 3) wallFlags = 0x08;
            else if (x == 4 && y == 5) wallFlags = 0x02;
            walls.Raw([wallFlags, 0x0f, 0, 0, 0, 0, 0, 0]);
        }
        var waterLayer = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0).Raw(water);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray()), ("Arry", 9, waterLayer.ToArray())),
            "wave16-water-rooms.iff");
        return RoomMap.Build(file, 1, width, height);
    }

    private static LotTerrain SlopedTerrainAt(int tileX, int tileY)
    {
        var data = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(4).U32(0);
        for (var x = 0; x < 64; x++)
        for (var y = 0; y < 64; y++)
        {
            if (x == tileX && y == tileY)
                data.Raw([0, 4, 0, 4]);
            else
                data.Raw([0, 0, 0, 0]);
        }
        return LotTerrain.Read(new IffFile(ChunkBuilder.File(("Arry", 0, data.ToArray())), "wave16-terrain.iff"));
    }

    private static (SimulationWorld World, XPerson Person, XObject Anchor) RouteFixture()
    {
        var world = new SimulationWorld { Host = null };
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);
        var personDef = PrimitiveTestWorld.Definition(XPerson.PersonType);
        personDef[(int)ObjdField.TreeTableId] = 0;
        var person = world.Add(new XPerson(100, personDef));
        person.TypeRecord = new TypeRecord(person.Id);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0800;
        var anchorDef = PrimitiveTestWorld.Definition();
        var anchor = world.Add(new XObject(200, anchorDef));
        anchor[ObjectData.AllowedHeightFlags] = 1;
        anchor[ObjectData.Flags] |= 0x10;
        anchor[ObjectData.Direction] = 4;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        anchor.Slots.Add(0);
        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });
        return (world, person, anchor);
    }

    [Fact]
    public void SavedLotSerialization_PersistsClockManagerFlagsOnNewlyCreatedObjects()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var sample = boot.World.WalkOrder.First(o =>
            o is not XPerson && o.TypeRecord is { Key: not 0 } &&
            boot.World.SavedObjectTypes.Values.Any(t => t.Guid == unchecked((uint)o.TypeRecord.Key)));

        short newId = (short)(boot.World.WalkOrder.Max(o => o.Id) + 1);
        var created = new XObject(newId, PrimitiveTestWorld.Definition())
        {
            TypeRecord = sample.TypeRecord,
            Position = new ObjectPosition(8 * 16 + 8, 8 * 16 + 8, 1),
        };
        created[ObjectData.AllowedHeightFlags] = 1;
        boot.World.Add(created);
        Assert.InRange(created.ClockManagerIndex, 1, boot.World.ClockManager.Entries.Count);

        const uint expectedFlags = 0x0000005Au;
        boot.World.ClockManager.Entries[created.ClockManagerIndex - 1].Flags = expectedFlags;

        var (savedLot, _) = boot.World.WriteSavedLotObjectsTo(boot.Lot);
        var objm = savedLot.OfType(ResourceType.Parse("ObjM")).First();
        var savedTypes = ObjectTypeTable.Read(savedLot, savedLot.OfType(ResourceType.Parse("objt")).First());
        var reread = LotObjects.Read(savedLot, objm, savedTypes, guid => boot.Registry.Find(guid)?.Definition);
        var savedNew = Assert.Single(reread.Objects, o => o.ObjectId == newId);
        Assert.NotNull(savedNew.Decoded);
        Assert.Equal(unchecked((int)expectedFlags), savedNew.Decoded!.ManagerFlags);
    }

    private sealed class DummyTreeStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.False;
    }

    [Fact]
    public void GhostRouting_RoutesThroughWallsWithoutPortalsWhileLivingSimFails()
    {
        static PrimitiveResult RunRoute(SimulationWorld world, XPerson person, XObject anchor)
        {
            var stack = new DummyTreeStack();
            var frame = new StackFrame(person.Id, new short[8], new short[4]) { StackObject = anchor.Id };
            stack.Frames.Add(frame);
            var ctx = new TreeContext(world, person, frame) { Stack = stack };
            byte[] operand = [0, 0, 2, 0, 0, 0, 0, 0];
            var result = GoToRoutingSlotPrimitive.Execute(ctx, operand);
            for (var step = 0; step < 512 && result == PrimitiveResult.Stop; step++)
            {
                world.TickAvatarAnimations(person);
                result = GoToRoutingSlotPrimitive.Execute(ctx, operand);
            }
            return result;
        }

        var (world, person, anchor) = RouteFixture();
        world.LotRoomMaps[1] = RoomWithVerticalWall(8);
        world.SetObjectPosition(person, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        world.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        Assert.Equal(PrimitiveResult.False, RunRoute(world, person, anchor));

        var (ghostWorld, ghost, ghostAnchor) = RouteFixture();
        ghostWorld.LotRoomMaps[1] = RoomWithVerticalWall(8);
        ghost[PersonData.IsGhost] = 1;
        ghostWorld.SetObjectPosition(ghost, new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1));
        ghostWorld.SetObjectPosition(ghostAnchor, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));
        Assert.Equal(PrimitiveResult.True, RunRoute(ghostWorld, ghost, ghostAnchor));
        Assert.True(ghost.Position.X >> 4 >= 8);
    }

    [Fact]
    public void PlacementValidation_WaterSupportSlopeGateAndSameHeightCenterpieceExclusion()
    {
        var waterWorld = new SimulationWorld();
        waterWorld.LotRoomMaps[1] = RoomMapWithGroundAndRooms(2, 2, groundClass: 0x10);
        waterWorld.SetPlacementEligibilityAt(2, 2, 1);
        var waterPos = new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 1);

        var landObj = waterWorld.Add(new XObject(1, PrimitiveTestWorld.Definition()));
        landObj[ObjectData.AllowedHeightFlags] = 1;
        landObj[ObjectData.PlacementFlags] = 0x0003;
        Assert.False(waterWorld.CanPlaceObject(landObj, waterPos));
        Assert.Equal(0x1d, waterWorld.LastPlacementFailureCode);

        var bridgeSupport = waterWorld.Add(new XObject(2, PrimitiveTestWorld.Definition()) { Position = waterPos });
        bridgeSupport[ObjectData.AllowedHeightFlags] = 1;
        bridgeSupport[ObjectData.Flags] = 0x1004;
        waterWorld.SetObjectPosition(bridgeSupport, waterPos);
        landObj[ObjectData.Flags] = 0x0004;
        Assert.True(waterWorld.CanPlaceObject(landObj, waterPos));

        var poolObj = waterWorld.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        poolObj[ObjectData.AllowedHeightFlags] = 1;
        poolObj[ObjectData.PlacementFlags] = 0x1200;
        Assert.False(waterWorld.CanPlaceObject(poolObj, new ObjectPosition(2 * 16 + 8, 2 * 16 + 8, 2)));
        Assert.Equal(0x1f, waterWorld.LastPlacementFailureCode);

        var slopeWorld = new SimulationWorld { LotTerrain = SlopedTerrainAt(5, 5) };
        var slopedPos = new ObjectPosition(5 * 16 + 8, 5 * 16 + 8, 1);
        var groundItem = slopeWorld.Add(new XObject(4, PrimitiveTestWorld.Definition()));
        groundItem[ObjectData.AllowedHeightFlags] = 1;
        groundItem[ObjectData.PlacementFlags] = 0x0003;
        Assert.False(slopeWorld.CanPlaceObject(groundItem, slopedPos));
        Assert.Equal(0x11, slopeWorld.LastPlacementFailureCode);

        groundItem[ObjectData.PlacementFlags] = 0x0103;
        Assert.True(slopeWorld.CanPlaceObject(groundItem, slopedPos));

        var slotWorld = new SimulationWorld();
        var tablePos = new ObjectPosition(3 * 16 + 8, 3 * 16 + 8, 1);
        var table = slotWorld.Add(new XObject(5, PrimitiveTestWorld.Definition()) { Position = tablePos });
        table[ObjectData.SupportStrength] = 100;
        var existingLamp = slotWorld.Add(new XObject(6, PrimitiveTestWorld.Definition()) { Position = tablePos });
        existingLamp[ObjectData.Weight] = 1;
        existingLamp[ObjectData.AllowedHeightFlags] = 2;
        table.FootprintSlots.Add(new ObjectFootprintSlot
        {
            X = 0, Y = 0, HeightCode = 2, OccupantObjectId = existingLamp.ClockManagerIndex
        });
        table.FootprintSlots.Add(new ObjectFootprintSlot
        {
            X = 1, Y = 1, HeightCode = 2, OccupantObjectId = 0
        });
        var secondItem = slotWorld.Add(new XObject(7, PrimitiveTestWorld.Definition()));
        secondItem[ObjectData.Weight] = 1;
        secondItem[ObjectData.AllowedHeightFlags] = 2;
        Assert.False(slotWorld.CanPlaceRouteCandidate(secondItem, tablePos, table, 1));
        Assert.NotEqual(0, slotWorld.LastPlacementFailureCode);

        existingLamp[ObjectData.Flags] = 0x2000;
        secondItem[ObjectData.Flags] = 0x2000;
        Assert.True(slotWorld.CanPlaceRouteCandidate(secondItem, tablePos, table, 1));
    }

    [Fact]
    public void SceneLighting_RespectsDisplayedRoomLightAndShadesPoolAttachmentsAtFullLight()
    {
        var roomMap = RoomMapWithGroundAndRooms(2, 2, groundClass: 0x10);
        var world = new SimulationWorld();
        world.Clock.Restore(22, 0, 0);
        world.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = roomMap });

        var enclosedRoom = roomMap.RoomAt(4, 4);
        var outerRoom = roomMap.RoomAt(1, 1);
        Assert.NotEqual(enclosedRoom, outerRoom);

        var baseEntry = world.PersonRoomMotiveEntries[enclosedRoom];
        world.PersonRoomMotiveEntries[enclosedRoom] = baseEntry with
        {
            LightLevel6c = 0f,
            LightingObjectCount140 = 0,
            PersonObjectCount142 = 1,
            LightsEnabledFlag73 = 1,
            LightOverrideFlag75 = 0,
        };

        var litNight = SceneLighting.Build(world, roomMap, 1);
        Assert.NotNull(litNight);
        var litR = litNight!.Rooms[enclosedRoom].R;

        world.PersonRoomMotiveEntries[enclosedRoom] = baseEntry with
        {
            LightLevel6c = 0f,
            LightingObjectCount140 = 0,
            PersonObjectCount142 = 1,
            LightsEnabledFlag73 = 0,
            LightOverrideFlag75 = 0,
        };
        var darkNight = SceneLighting.Build(world, roomMap, 1);
        Assert.NotNull(darkNight);
        Assert.True(darkNight!.Rooms[enclosedRoom].R < litR);

        var sdlJson = JsonSerializer.Serialize(darkNight, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sdlLighting = SdlSceneLighting.FromJson(sdlJson);
        Assert.True(sdlLighting.Multiplier(SdlSceneLighting.PoolAttachmentRoom).X > sdlLighting.Multiplier(enclosedRoom).X);
    }

    [Fact]
    public void SceneExporter_TintsGhostSimUnlitAndSuppressesShadowAndFarZoomRuntimeSprites()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        bella[PersonData.IsGhost] = 1;
        bella.AnimationPlayback = null;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sims-wave16-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scenePath = SceneExporter.Export(boot.Lot, boot.World, boot.Registry, gameDir, tempDir, 5, 1);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(scenePath));
            var meshes = doc.RootElement.GetProperty("meshes").EnumerateArray().ToArray();
            Assert.NotEmpty(meshes);

            var tintedMeshes = meshes.Where(m => m.TryGetProperty("tint", out var t) && t.ValueKind == JsonValueKind.Array).ToArray();
            Assert.NotEmpty(tintedMeshes);
            foreach (var mesh in tintedMeshes)
            {
                var tint = mesh.GetProperty("tint").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                Assert.Equal(4, tint.Length);
                Assert.InRange(tint[0], 17f / 255f - 0.01f, 17f / 255f + 0.01f);
                Assert.InRange(tint[1], 0.99f, 1.01f);
                Assert.InRange(tint[2], 241f / 255f - 0.01f, 241f / 255f + 0.01f);
                Assert.InRange(tint[3], 128f / 255f - 0.01f, 128f / 255f + 0.01f);
                Assert.True(!mesh.TryGetProperty("room", out var roomProp) || roomProp.ValueKind == JsonValueKind.Null);
            }

            var sprites = doc.RootElement.GetProperty("sprites").EnumerateArray().ToArray();
            Assert.NotEmpty(sprites);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AvatarSceneGeometry_ResolvesCareerSkeletonCostumeTrunkAndMagicSuits()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var people = boot.World.WalkOrder.OfType<XPerson>().ToArray();
        var bella = people.First(p => p[PersonData.PersonAge] >= 18 && (p[PersonData.Gender] & 1) != 0);
        var cassandra = people.First(p => p[PersonData.PersonAge] is > 0 and < 18);
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var careerHost = SimCareerCatalog.Load(gameDir);
        Assert.NotNull(careerHost);

        short origBellaOutfit = bella[PersonData.CurrentOutfit];
        short origBellaJob = bella[PersonData.JobType];
        short origBellaLevel = bella[PersonData.JobPromotionLevel];
        short origBellaCostume = bella[PersonData.CostumeTrunkOffsetHouseParty];
        short origBellaHandL = bella[PersonData.HandGestureLeft];
        short origBellaHandR = bella[PersonData.HandGestureRight];
        short origCassOutfit = cassandra[PersonData.CurrentOutfit];
        try
        {
            bella[PersonData.CurrentOutfit] = 3;
            var careerTrackId = (short)careerHost!.CareerTable().First(id =>
                careerHost.TryGetCareerUniform((short)id, 0, true, out var mesh, out _) &&
                !string.IsNullOrEmpty(mesh) && !mesh.Equals("none", StringComparison.OrdinalIgnoreCase));
            bella[PersonData.JobType] = careerTrackId;
            bella[PersonData.JobPromotionLevel] = 0;
            var careerParts = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.NotEqual("", careerParts.Body.Item1);
            Assert.Contains(".apr", careerParts.Body.Item1, StringComparison.OrdinalIgnoreCase);

            cassandra[PersonData.CurrentOutfit] = 3;
            cassandra[PersonData.JobType] = careerTrackId;
            cassandra[PersonData.JobPromotionLevel] = 9;
            var childCareer = geometry.ResolveEffectivePartsForTest(cassandra, careerHost);
            Assert.False(childCareer.Body.Item1.Equals("NONE", StringComparison.OrdinalIgnoreCase));

            bella[PersonData.CurrentOutfit] = 4;
            bella[PersonData.HandGestureLeft] = 3;
            bella[PersonData.HandGestureRight] = 1;
            var nudeParts = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Equal("NONE", nudeParts.LeftHand.Item1);
            Assert.NotEqual("NONE", nudeParts.RightHand.Item1);
            bella[PersonData.HandGestureLeft] = 0;
            bella[PersonData.HandGestureRight] = 0;

            bella[PersonData.CurrentOutfit] = 5;
            var pjParts = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Contains("pajama", pjParts.Body.Item1, StringComparison.OrdinalIgnoreCase);

            bella[PersonData.CurrentOutfit] = 6;
            var skelAdult = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Contains("skeleton_01", skelAdult.Body.Item1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("c_skeleton", skelAdult.Head.Item1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("_GUAO_skel", skelAdult.LeftHand.Item2, StringComparison.OrdinalIgnoreCase);

            bella[PersonData.CurrentOutfit] = 7;
            var negAdult = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Contains("Skeleneg_01", negAdult.Body.Item2, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("_GUAO_sneg", negAdult.RightHand.Item2, StringComparison.OrdinalIgnoreCase);

            cassandra[PersonData.CurrentOutfit] = 6;
            var skelChild = geometry.ResolveEffectivePartsForTest(cassandra, careerHost);
            Assert.Contains("skeletonchd_01", skelChild.Body.Item1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("c_kidskull", skelChild.Head.Item1, StringComparison.OrdinalIgnoreCase);

            bella[PersonData.CurrentOutfit] = 16;
            bella[PersonData.CostumeTrunkOffsetHouseParty] = 2;
            var disco = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Contains("ct-disco-", disco.Body.Item1, StringComparison.OrdinalIgnoreCase);

            bella[PersonData.CurrentOutfit] = 38;
            bella[PersonData.HandGestureLeft] = 2;
            var beekeeper = geometry.ResolveEffectivePartsForTest(bella, careerHost);
            Assert.Contains("MagicMAFat_BeeKeeper", beekeeper.Body.Item1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HUAC_BeeKeeper", beekeeper.LeftHand.Item2, StringComparison.OrdinalIgnoreCase);

            cassandra[PersonData.CurrentOutfit] = 60;
            var magicSkel = geometry.ResolveEffectivePartsForTest(cassandra, careerHost);
            Assert.Contains("MagicMCChd_Skeleton", magicSkel.Body.Item1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GUAO_SkeletonChild", magicSkel.LeftHand.Item2, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            bella[PersonData.CurrentOutfit] = origBellaOutfit;
            bella[PersonData.JobType] = origBellaJob;
            bella[PersonData.JobPromotionLevel] = origBellaLevel;
            bella[PersonData.CostumeTrunkOffsetHouseParty] = origBellaCostume;
            bella[PersonData.HandGestureLeft] = origBellaHandL;
            bella[PersonData.HandGestureRight] = origBellaHandR;
            cassandra[PersonData.CurrentOutfit] = origCassOutfit;
        }
    }
}
