using System.Text;
using System.Text.Json;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.Simulation.Neighborhood;
using SimsEngine.Tests.E2E;
using SimsEngine.World;
using SimsSDL;
using SimsSDL.Core;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests.ConnectedNeighborhood;

public sealed class ConnectedNeighborhoodUnitTests
{
    [Fact]
    public void ConnectedNeighborhoodOptions_DefaultsAndNormalize()
    {
        var opts = new ConnectedNeighborhoodOptions();
        Assert.False(opts.Enabled);
        Assert.True(opts.EnableAdjacentLotRendering);
        Assert.True(opts.EnableStoryProgression);
        Assert.Equal(4, opts.MaxLiveSplitLots);

        opts.Enabled = true;
        opts.MaxLiveSplitLots = 0;
        opts.Normalize();
        Assert.Equal(1, opts.MaxLiveSplitLots);

        var copy = opts.Clone();
        copy.MaxLiveSplitLots = 6;
        copy.EnableAdjacentLotRendering = false;
        Assert.Equal(1, opts.MaxLiveSplitLots);
        Assert.True(opts.EnableAdjacentLotRendering);
    }

    [Fact]
    public void ModManager_DiscoversConnectedNeighborhoodPackWithNoPluginAndDefaultDisabled()
    {
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null) return;
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-cn-pack-" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(tempRoot, "game");
        var dataRoot = Path.Combine(tempRoot, "data");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "UserData"));
            Directory.CreateDirectory(Path.Combine(gameDir, "GameData"));
            Directory.CreateDirectory(dataRoot);

            var discovered = ModManager.DiscoverAll(gameDir, workspace: repoRoot, dataRoot: dataRoot, cliPacks: null);
            var pack = Assert.Single(discovered, static m =>
                m.LoadedPack?.Manifest.Id == "local.connected-neighborhood");

            Assert.Equal("Connected Neighborhood", pack.DisplayName);
            Assert.False(pack.DefaultEnabled);
            Assert.Null(pack.PluginReport);
            Assert.Null(pack.LoadError);
            Assert.Contains("Gameplay", pack.CategoryTags);
            Assert.Contains("Neighborhood", pack.CategoryTags);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SaveCompatibility_OptionalConnectedNeighborhoodPack_SucceedsAcrossAllFourCombinations()
    {
        var cnPack = new SaveCompatibility.PackRecord("local.connected-neighborhood", "1.0.0");
        var cnDesc = new PluginHost.PackDescriptor("local.connected-neighborhood", "1.0.0", []);
        var seasonsPack = new SaveCompatibility.PackRecord("local.seasons", "1.0.0");
        var seasonsDesc = new PluginHost.PackDescriptor("local.seasons", "1.0.0", []);

        // 1. Vanilla -> Vanilla
        var r1 = SaveCompatibility.Check("fp-vanilla", [], "fp-vanilla", []);
        Assert.False(r1.Migrated);

        // 2. Vanilla -> Connected
        var r2 = SaveCompatibility.Check("fp-vanilla", [], "fp-connected", [cnDesc]);
        Assert.False(r2.Migrated);
        Assert.Empty(r2.Added);
        Assert.Empty(r2.Removed);

        // 3. Connected -> Connected
        var r3 = SaveCompatibility.Check("fp-connected", [cnPack], "fp-connected", [cnDesc]);
        Assert.False(r3.Migrated);

        // 4. Connected -> Vanilla (must NEVER throw MissingPacksException)
        var r4 = SaveCompatibility.Check("fp-connected", [cnPack], "fp-vanilla", []);
        Assert.False(r4.Migrated);
        Assert.Empty(r4.Added);
        Assert.Empty(r4.Removed);

        // 5. Real missing pack alongside optional pack still throws MissingPacksException for the real pack only
        var ex = Assert.Throws<SaveCompatibility.MissingPacksException>(() =>
            SaveCompatibility.Check("fp-both", [cnPack, seasonsPack], "fp-vanilla", []));
        Assert.Equal(["local.seasons@1.0.0"], ex.Packs);

        // 6. Real pack present + optional pack toggled off -> not migrated
        var r6 = SaveCompatibility.Check("fp-both", [cnPack, seasonsPack], "fp-seasons", [seasonsDesc]);
        Assert.False(r6.Migrated);
    }

    [Fact]
    public void SaveCompatibility_CoreFingerprint_DetectsRequiredPackTamperingAndEnforcesOrdinalOptionalIds()
    {
        var cnPack = new SaveCompatibility.PackRecord("local.connected-neighborhood", "1.0.0");
        var cnDesc = new PluginHost.PackDescriptor("local.connected-neighborhood", "1.0.0", []);
        var seasonsPack = new SaveCompatibility.PackRecord("local.seasons", "1.0.0");
        var seasonsDesc = new PluginHost.PackDescriptor("local.seasons", "1.0.0", []);

        // 1. Optional pack removed + required pack tampered without version bump -> MUST throw
        Assert.Throws<InvalidDataException>(() =>
            SaveCompatibility.Check(
                "fp-cn-seasons",
                [cnPack, seasonsPack],
                "fp-seasons-tampered",
                [seasonsDesc],
                savedCoreFingerprint: "fp-seasons-v1",
                currentCoreFingerprint: "fp-seasons-tampered"));

        // 2. Optional pack added + required pack tampered without version bump -> MUST throw
        Assert.Throws<InvalidDataException>(() =>
            SaveCompatibility.Check(
                "fp-seasons-v1",
                [seasonsPack],
                "fp-cn-seasons-tampered",
                [cnDesc, seasonsDesc],
                savedCoreFingerprint: "fp-seasons-v1",
                currentCoreFingerprint: "fp-seasons-tampered"));

        // 3. Optional pack removed + required pack untampered -> allowed cleanly
        var ok = SaveCompatibility.Check(
            "fp-cn-seasons",
            [cnPack, seasonsPack],
            "fp-seasons-v1",
            [seasonsDesc],
            savedCoreFingerprint: "fp-seasons-v1",
            currentCoreFingerprint: "fp-seasons-v1");
        Assert.False(ok.Migrated);

        // 4. OptionalFeaturePackIds uses StringComparer.Ordinal (uppercase ID does NOT match)
        var upperCnPack = new SaveCompatibility.PackRecord("LOCAL.CONNECTED-NEIGHBORHOOD", "1.0.0");
        var missing = Assert.Throws<SaveCompatibility.MissingPacksException>(() =>
            SaveCompatibility.Check("fp-upper", [upperCnPack], "fp-vanilla", []));
        Assert.Equal(["LOCAL.CONNECTED-NEIGHBORHOOD@1.0.0"], missing.Packs);
    }

    [Fact]
    public void PackStack_Report_ComputesCoreFingerprintExcludingOptionalPack()
    {
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null) return;
        var cnPackDir = Path.Combine(repoRoot, "packs", "connected-neighborhood");
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-pack-corefp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var corePackDir = Path.Combine(tempRoot, "core-pack");
            Directory.CreateDirectory(Path.Combine(corePackDir, "files", "gamedata"));
            File.WriteAllText(Path.Combine(corePackDir, "files", "gamedata", "test.txt"), "v1");
            File.WriteAllText(Path.Combine(corePackDir, "pack.json"), """
                {
                  "schemaVersion": 1,
                  "id": "local.core-test",
                  "name": "Core Test",
                  "version": "1.0.0",
                  "baseGameVersion": "1.90",
                  "dependencies": [],
                  "overrides": []
                }
                """);

            var cnOnlyReport = new PackStack([new ContentPack(cnPackDir)]).Report();
            Assert.Equal("unmounted-base-1.90", cnOnlyReport.CoreFingerprint);

            var coreOnlyReport = new PackStack([new ContentPack(corePackDir)]).Report();
            Assert.Null(coreOnlyReport.CoreFingerprint);

            var bothReport = new PackStack([new ContentPack(corePackDir), new ContentPack(cnPackDir)]).Report();
            Assert.Equal(coreOnlyReport.Fingerprint, bothReport.CoreFingerprint);
            Assert.NotEqual(bothReport.Fingerprint, bothReport.CoreFingerprint);

            // Tampering core asset changes CoreFingerprint
            File.WriteAllText(Path.Combine(corePackDir, "files", "gamedata", "test.txt"), "tampered");
            var tamperedBothReport = new PackStack([new ContentPack(corePackDir), new ContentPack(cnPackDir)]).Report();
            Assert.NotEqual(coreOnlyReport.Fingerprint, tamperedBothReport.CoreFingerprint);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NeighborhoodManager_UpdateSimLocation_GuardsBoundsAndCommunityHomeLot()
    {
        var mgr = new NeighborhoodManager();
        mgr.UpdateSimLocation(0, 1);
        mgr.UpdateSimLocation(10, 0);
        mgr.UpdateSimLocation(10, -5);
        mgr.UpdateSimLocation(10, 40000);
        Assert.Empty(mgr.GetAllSimLocations());
        Assert.Empty(mgr.GetAllLotSummaries());

        mgr.UpdateSimLocation(99, 21);
        var details = mgr.GetSimDetails(99);
        Assert.NotNull(details);
        Assert.Equal(0, details!.HomeLotId);
        Assert.Equal(21, details.CurrentLotId);
        Assert.True(details.IsVisiting);
        var lot21 = mgr.GetLotSummary(21);
        Assert.NotNull(lot21);
        Assert.Contains((short)99, lot21!.VisitorSimIds);
        Assert.DoesNotContain((short)99, lot21.ResidentSimIds);
    }

    [Fact]
    public void NeighborhoodManager_SyncFromNeighborhood_ResetLocationsClearsLotObjectIdAndTransitActivity()
    {
        const uint sim1Guid = 0x10000011u;
        var fam1 = Family.CreateNew(familyNumber: 1, houseNumber: 1, members: [sim1Guid]);
        var pd1 = new short[256];
        pd1[(int)PersonData.FamilyNumber] = 1;
        var neighbors = new Neighbors(73, 1,
        [
            new Neighbor(0, 9, "Bella Goth", 0, pd1, 1, sim1Guid, new Dictionary<int, IReadOnlyList<int>>(), StoredFormat: 10),
        ]);
        var ngh = EmptyIff()
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 1, fam1.Write(1))
            .WithAddedOrReplacedChunk(ResourceType.Parse("NBRS"), 1, neighbors.WriteChunkData(swap: false));

        var mgr = new NeighborhoodManager();
        mgr.SyncFromNeighborhood(ngh);
        mgr.OnSimLotSwitched(1, fromHouse: 1, toHouse: 2, isVisiting: true, lotObjectId: 42);
        Assert.Equal((short)42, mgr.GetSimDetails(1)?.LotObjectId);
        Assert.Equal("In transit", mgr.GetSimDetails(1)?.ActivitySummary);

        mgr.SyncFromNeighborhood(ngh, resetLocations: false);
        Assert.Equal(2, mgr.GetSimLocation(1));
        Assert.Equal((short)42, mgr.GetSimDetails(1)?.LotObjectId);

        mgr.SyncFromNeighborhood(ngh, resetLocations: true);
        Assert.Equal(1, mgr.GetSimLocation(1));
        Assert.Null(mgr.GetSimDetails(1)?.LotObjectId);
        Assert.False(mgr.GetSimDetails(1)?.IsVisiting);
        Assert.Equal("Idle", mgr.GetSimDetails(1)?.ActivitySummary);
        Assert.Null(mgr.GetSimLocationByLotObjectId(1, 42));
    }

    [Fact]
    public void NeighborhoodManager_SyncFromWorld_PreservesTrackedSimOnOtherActiveLot()
    {
        var mgr = new NeighborhoodManager { TrackingEnabled = true };
        var world5 = new SimulationWorld { ActiveLotNumber = 5, Neighborhood = mgr };
        var personDef = new short[108];
        personDef[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(12, personDef)
        {
            TypeRecord = new TypeRecord(unchecked((int)0x20000012u)) { Name = "Traveller" },
        };
        person[PersonData.FamilyNumber] = 5;
        world5.Neighbors[34] = new NeighborState(34, personDef)
        {
            TypeRecordKey = unchecked((int)0x20000012u),
            Name = "Traveller",
            PersonRecordVersion = 9,
        };
        world5.Add(person);
        world5.BindPersonToNeighborRecord(person);
        Assert.Equal(5, mgr.GetSimLocation(34));

        // Sim 34 switches to lot 21 while still present in world5.WalkOrder
        mgr.OnSimLotSwitched(34, fromHouse: 5, toHouse: 21, isVisiting: true, lotObjectId: 7);
        Assert.Equal(21, mgr.GetSimLocation(34));

        // Background sync of world5 must preserve lot 21
        mgr.SyncFromWorld(world5, 5, preserveOtherLotLocations: true);
        Assert.Equal(21, mgr.GetSimLocation(34));
        Assert.True(mgr.GetSimDetails(34)?.IsVisiting);

        // Focused sync of world5 with activeLotIds containing 21 must also preserve lot 21
        mgr.SyncFromWorld(world5, 5, preserveOtherLotLocations: false, activeLotIds: new HashSet<int> { 5, 21 });
        Assert.Equal(21, mgr.GetSimLocation(34));
    }

    [Fact]
    public void NeighborhoodManager_TracksSimLocationsLotMetadataAndSnapshotRoundTrip()
    {
        var mgr = new NeighborhoodManager();
        Assert.Null(mgr.GetSimLocation(999));

        mgr.RegisterLotMetadata(1, new LotSummary
        {
            House = 1,
            FamilyId = 1,
            FamilyName = "Goth",
            LotName = "Goth Manor",
            Zoning = "residential",
        });
        mgr.RegisterLotMetadata(2, new LotSummary
        {
            House = 2,
            FamilyId = 2,
            FamilyName = "Newbie",
            LotName = "7 Sim Lane",
            Zoning = "residential",
        });
        mgr.RegisterLotMetadata(21, new LotSummary
        {
            House = 21,
            LotName = "Old Town Plaza",
            Zoning = "community",
        });

        mgr.UpdateSimLocation(10, 1);
        mgr.UpdateSimLocation(20, 2);
        mgr.OnSimLotSwitched(10, fromHouse: 1, toHouse: 21, isVisiting: true, lotObjectId: 5);

        Assert.Equal(21, mgr.GetSimLocation(10));
        Assert.Equal(2, mgr.GetSimLocation(20));

        var details10 = mgr.GetSimDetails(10);
        Assert.NotNull(details10);
        Assert.Equal(1, details10!.HomeLotId);
        Assert.Equal(21, details10.CurrentLotId);
        Assert.True(details10.IsVisiting);
        Assert.True(details10.IsAway);
        Assert.False(details10.IsAtHome);
        Assert.Equal((short)5, details10.LotObjectId);
        Assert.Same(details10, mgr.GetSimLocationByLotObjectId(21, 5));

        var lot21 = mgr.GetLotSummary(21);
        Assert.NotNull(lot21);
        Assert.True(lot21!.IsCommunity);
        Assert.Contains((short)10, lot21.VisitorSimIds);

        var snapshot = mgr.CaptureSnapshot();
        var json = JsonSerializer.Serialize(snapshot);
        var restoredSnapshot = JsonSerializer.Deserialize<NeighborhoodSnapshot>(json);

        var mgr2 = new NeighborhoodManager();
        mgr2.RestoreSnapshot(restoredSnapshot);

        Assert.Equal(21, mgr2.GetSimLocation(10));
        Assert.Equal(2, mgr2.GetSimLocation(20));
        Assert.Equal("Goth", mgr2.GetLotSummary(1)?.FamilyName);
        Assert.True(mgr2.GetSimDetails(10)?.IsVisiting);
        Assert.Contains((short)10, mgr2.GetLotSummary(21)!.VisitorSimIds);
    }

    [Fact]
    public void NeighborhoodManager_SyncFromNeighborhoodAndWorldHooks_RespectsTrackingEnabledGate()
    {
        const uint sim1Guid = 0x10000001u;
        const uint sim2Guid = 0x10000002u;
        const uint townieGuid = 0x10000003u;

        var fam1 = Family.CreateNew(familyNumber: 1, houseNumber: 1, members: [sim1Guid]);
        var fam2 = Family.CreateNew(familyNumber: 2, houseNumber: 2, members: [sim2Guid]);

        var pd1 = new short[256];
        pd1[(int)PersonData.FamilyNumber] = 1;
        var pd2 = new short[256];
        pd2[(int)PersonData.FamilyNumber] = 2;
        var pd3 = new short[256];

        var neighbors = new Neighbors(73, 3,
        [
            new Neighbor(0, 9, "Bella Goth", 0, pd1, 1, sim1Guid, new Dictionary<int, IReadOnlyList<int>>(), StoredFormat: 10),
            new Neighbor(1, 9, "Bob Newbie", 0, pd2, 2, sim2Guid, new Dictionary<int, IReadOnlyList<int>>(), StoredFormat: 10),
            new Neighbor(2, 9, "Townie Tina", 0, pd3, 3, townieGuid, new Dictionary<int, IReadOnlyList<int>>(), StoredFormat: 10),
        ]);

        var ngh = EmptyIff()
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 1, fam1.Write(1))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 1, EncodeStrMinusThree("Goth"))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 2, fam2.Write(2))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 2, EncodeStrMinusThree("Newbie"))
            .WithAddedOrReplacedChunk(ResourceType.Parse("NBRS"), 1, neighbors.WriteChunkData(swap: false));

        var zoning = EmptyIff()
            .WithAddedOrReplacedChunk(ResourceType.Str, 1, EncodeStrMinusThree("1, residential", "21, community"));

        var mgr = new NeighborhoodManager();
        mgr.SyncFromNeighborhood(ngh, zoning);

        Assert.Equal(1, mgr.GetSimLocation(1));
        Assert.Equal(2, mgr.GetSimLocation(2));
        Assert.Equal(0, mgr.GetSimLocation(3));
        Assert.Equal("Goth", mgr.GetLotSummary(1)?.FamilyName);
        Assert.Equal("Newbie", mgr.GetLotSummary(2)?.FamilyName);
        Assert.Equal("community", mgr.GetLotSummary(21)?.Zoning);
        Assert.Same(mgr.GetSimDetails(1), mgr.GetSimLocationByGuid(sim1Guid));

        // Moving Sim 1 to lot 21 is preserved across metadata re-sync when resetLocations=false
        mgr.UpdateSimLocation(1, 21);
        mgr.SyncFromNeighborhood(ngh, zoning, resetLocations: false);
        Assert.Equal(21, mgr.GetSimLocation(1));
        mgr.SyncFromNeighborhood(ngh, zoning, resetLocations: true);
        Assert.Equal(1, mgr.GetSimLocation(1));

        // Verify World.BindPersonToNeighborRecord and World.Remove respect TrackingEnabled
        var world = new SimulationWorld { ActiveLotNumber = 5 };
        Assert.False(world.Neighborhood.TrackingEnabled);

        var personDef = new short[108];
        personDef[(int)ObjdField.Type] = XPerson.PersonType;
        var person = new XPerson(1, personDef)
        {
            TypeRecord = new TypeRecord(unchecked((int)0x20000001u)) { Name = "TestSim" },
        };
        world.Neighbors[4] = new NeighborState(4, personDef)
        {
            TypeRecordKey = unchecked((int)0x20000001u),
            Name = "TestSim",
            PersonRecordVersion = 9,
        };
        world.Add(person);
        world.BindPersonToNeighborRecord(person);
        Assert.Null(world.Neighborhood.GetSimLocation(4));

        world.Neighborhood.TrackingEnabled = true;
        var person2 = new XPerson(2, personDef)
        {
            TypeRecord = new TypeRecord(unchecked((int)0x20000002u)) { Name = "TrackedSim" },
        };
        world.Neighbors[5] = new NeighborState(5, personDef)
        {
            TypeRecordKey = unchecked((int)0x20000002u),
            Name = "TrackedSim",
            PersonRecordVersion = 9,
        };
        world.Add(person2);
        world.BindPersonToNeighborRecord(person2);
        Assert.Equal(5, world.Neighborhood.GetSimLocation(5));
        Assert.Equal((short)2, world.Neighborhood.GetSimDetails(5)?.LotObjectId);

        world.Remove(2);
        Assert.Null(world.Neighborhood.GetSimDetails(5)?.LotObjectId);
    }

    [Fact]
    public void LotSessionPool_TracksLiveAndSavedSessions_EvictsLruRespectingFocusedAndHome()
    {
        var pool = new LotSessionPool { FocusedHouse = 1, HomeHouse = 2 };

        LotSession MakeSession(short house) => new()
        {
            House = house,
            World = new SimulationWorld { ActiveLotNumber = house },
            Lot = EmptyIff(),
            HomeHouse = 2,
        };

        pool.PutLive(MakeSession(2)); // Home (order 1)
        pool.PutLive(MakeSession(3)); // Other (order 2)
        pool.PutLive(MakeSession(4)); // Other (order 3)
        pool.PutLive(MakeSession(1)); // Focused (order 4)

        var evict = pool.SelectEvictionCandidate(keepFocusedHouse: 1, keepHomeHouse: 2);
        Assert.NotNull(evict);
        Assert.Equal((short)3, evict!.House);

        pool.PutSaved(new DehydratedLotSession(
            3, null, [], new SimulationServer.SaveRuntimeMetadata(14, 1), 1, false, 2, null, 10, 0));
        Assert.False(pool.TryGetLive(3, out _));
        Assert.True(pool.TryGetSaved(3, out _));

        // Next candidate skips Home (2) and Focused (1) -> picks 4
        var evict2 = pool.SelectEvictionCandidate(keepFocusedHouse: 1, keepHomeHouse: 2);
        Assert.NotNull(evict2);
        Assert.Equal((short)4, evict2!.House);
    }

    [Fact]
    public void SaveEnvelope_ConnectedNeighborhoodNullByDefault_BackwardsCompatible()
    {
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var envelope = new SimulationServer.SaveEnvelope(
            2, "native-simsengine-v0", "fp", 5, 100, "AQID",
            Packs: [], Plugins: new Dictionary<string, PluginHost.PluginSaveRecord>());
        var json = JsonSerializer.Serialize(envelope, jsonOpts);

        Assert.DoesNotContain("connectedNeighborhood", json, StringComparison.OrdinalIgnoreCase);

        var roundTrip = JsonSerializer.Deserialize<SimulationServer.SaveEnvelope>(json, jsonOpts);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.ConnectedNeighborhood);
        Assert.Equal((short)5, roundTrip.House);
    }

    [Fact]
    public void World_MarkRelationshipsChanged_TracksDirectedPairsWithoutMarkingTargetOutgoing()
    {
        var world = new SimulationWorld();
        var def = new short[108];
        var n1 = new NeighborState(1, def);
        var n2 = new NeighborState(2, def);

        world.MarkRelationshipsChanged(n1, n2);

        Assert.Contains((short)1, world.DirtyRelationshipNeighbors);
        Assert.DoesNotContain((short)2, world.DirtyRelationshipNeighbors);
        Assert.Contains(((short)1, (short)2), world.DirtyRelationshipPairs);
        Assert.DoesNotContain(((short)2, (short)1), world.DirtyRelationshipPairs);
    }

    [Fact]
    public void RouteExecutor_IsGatewayPortal_ClassifiesPortalsVsStairs()
    {
        var pedDef = new short[108];
        pedDef[(int)ObjdField.Type] = 4;
        var pedPortal = new XObject(1, pedDef)
        {
            TypeRecord = new TypeRecord(unchecked((int)GatewayRouting.PedestrianPortalGuid)) { Name = "Portal - Pedestrian" },
            Position = new ObjectPosition(32 * 16 + 8, 32 * 16 + 8, 1),
        };
        Assert.True(RouteExecutor.IsGatewayPortal(pedPortal));

        var sentinelDef = new short[108];
        sentinelDef[(int)ObjdField.Type] = 8;
        var sentinelObj = new XObject(2, sentinelDef)
        {
            RuntimeClass = ObjectClass.Portal,
            RoomWord84 = unchecked((short)0xFFFB),
            Position = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1),
        };
        Assert.True(RouteExecutor.IsGatewayPortal(sentinelObj));

        // Unplaced non-portal object (ObjectType == 4 with RoomWord84 == 0xFFFB) must NOT pass as a gateway portal
        var unplacedDef = new short[108];
        unplacedDef[(int)ObjdField.Type] = 4;
        var unplacedObj = new XObject(6, unplacedDef)
        {
            TypeRecord = new TypeRecord(0x33333333) { ObjectType = 4, Name = "Unplaced Chair" },
            RoomWord84 = unchecked((short)0xFFFB),
            Position = new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1),
        };
        Assert.False(RouteExecutor.IsGatewayPortal(unplacedObj));

        // Stairs use ObjectType == 7 and must NOT be misclassified as gateway portals
        var stairDef = new short[108];
        stairDef[(int)ObjdField.Type] = 7;
        var stairs = new XObject(3, stairDef)
        {
            TypeRecord = new TypeRecord(0x12345678) { ObjectType = 7, Name = "Stairs" },
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = 1,
            RoomWord84 = 1,
            Position = new ObjectPosition(1 * 16 + 8, 1 * 16 + 8, 1),
        };
        Assert.False(RouteExecutor.IsGatewayPortal(stairs));

        // Perimeter ObjectClass.Portal (ObjectType == 8) -> true; interior non-0xFFFB -> false
        var portalDef = new short[108];
        portalDef[(int)ObjdField.Type] = 8;
        var edgePortal = new XObject(4, portalDef)
        {
            TypeRecord = new TypeRecord(0x22222222) { ObjectType = 8, Name = "Edge Portal" },
            RuntimeClass = ObjectClass.Portal,
            MultiTileMasterId = 4,
            RoomWord84 = 1,
            Position = new ObjectPosition(2 * 16 + 8, 30 * 16 + 8, 1),
        };
        Assert.True(RouteExecutor.IsGatewayPortal(edgePortal));

        var interiorPortal = new XObject(5, portalDef)
        {
            TypeRecord = new TypeRecord(0x22222222) { ObjectType = 8, Name = "Interior Door" },
            RuntimeClass = ObjectClass.Portal,
            MultiTileMasterId = 5,
            RoomWord84 = 1,
            Position = new ObjectPosition(25 * 16 + 8, 25 * 16 + 8, 1),
        };
        Assert.False(RouteExecutor.IsGatewayPortal(interiorPortal));
    }

    [Fact]
    public void GatewayRouting_ClampPositionToLotBounds_And_ResolveAcrossRealHousesWithCompanionSpread()
    {
        var low = GatewayRouting.ClampPositionToLotBounds(new ObjectPosition(-20, -10, 0), innerPlayable: false);
        Assert.Equal(0, low.X);
        Assert.Equal(0, low.Y);
        Assert.Equal((short)1, low.Height);

        var high = GatewayRouting.ClampPositionToLotBounds(new ObjectPosition(2000, 2000, 5), innerPlayable: true);
        Assert.Equal((LotGeometry.LotDimension - 2) * 16 + 8, high.X);
        Assert.Equal((LotGeometry.LotDimension - 2) * 16 + 8, high.Y);
        Assert.Equal((short)2, high.Height);

        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        short[] realHouses = [1, 2, 3, 5, 6, 7, 21, 40, 58];
        var existingHouses = realHouses.Where(h => E2EHarness.HasHouses(gameDir, h)).ToArray();
        if (existingHouses.Length == 0)
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, existingHouses);

        foreach (var h in existingHouses)
        {
            var lotPath = Path.Combine(profile.UserData, "Houses", $"House{h:D2}.iff");
            var setup = WorldBootstrap.Load(
                profile.GameDir,
                lotPath,
                language: 1,
                isPaused: () => true,
                h,
                userDataPath: profile.UserData);
            var w = setup.World;
            var sim = w.WalkOrder.OfType<XPerson>().FirstOrDefault();
            if (sim is null)
            {
                var anyNbr = w.Neighbors.Values.First(n => n.PersonRecordVersion > 0 && n.TypeRecordKey != 0);
                sim = Assert.IsType<XPerson>(w.ObjectFactory!(anyNbr.TypeRecordKey));
                w.Add(sim);
                w.InitializeCreatedObject(sim);
                w.InitializeCreatedPerson(sim);
            }

            var spreadPositions = new HashSet<(int X, int Y)>();
            for (var spread = 0; spread < 3; spread++)
            {
                var pos = GatewayRouting.ResolveWalkableSidewalkTile(w, setup.Lot, sim, spreadIndex: spread);
                var tx = pos.X >> 4;
                var ty = pos.Y >> 4;
                Assert.InRange(tx, 1, LotGeometry.LotDimension - 2);
                Assert.InRange(ty, 1, LotGeometry.LotDimension - 2);
                Assert.False(tx == 0 && ty == 0);
                Assert.True(w.CanPlaceObject(sim, pos), $"House {h} spread {spread} resolved non-placeable ({tx},{ty}).");
                spreadPositions.Add((tx, ty));
            }
            Assert.Equal(3, spreadPositions.Count);
        }
    }

    [Fact]
    public void NeighborLotController_MotiveDecayAndClamping_BoundedAcross7200Ticks()
    {
        var controller = new NeighborLotController();
        var sim = new InactiveSimState
        {
            NeighborId = 10,
            Guid = 0x10000010u,
            Name = "Test Resident",
            FamilyId = 2,
        };
        sim.Motives[(int)Motive.Energy] = 95f;
        sim.Motives[(int)Motive.Hunger] = 90f;
        sim.Motives[(int)Motive.Comfort] = 90f;
        sim.Motives[(int)Motive.Hygiene] = 90f;
        sim.Motives[(int)Motive.Bladder] = 90f;
        sim.Motives[(int)Motive.Social] = float.NaN; // non-finite sanitized
        sim.Motives[(int)Motive.Fun] = 250f;         // over-bound clamped
        Array.Copy(sim.Motives, sim.SavedValues7CC, 16);

        controller.RegisterInactiveLot(2, new InactiveLotState
        {
            House = 2,
            FamilyChunkId = 2,
            FamilyNumber = 2,
            IsActive = false,
            Sims = [sim],
        });

        // 7200 ticks = 4 sim-hours across 60-tick decay + 300-tick autonomy passes
        for (var i = 0; i < 120; i++)
            controller.TickNeighborhood(60, currentHour: (18 + i / 30) % 24, currentMinute: (i * 2) % 60, currentDay: 1);

        Assert.True(controller.TryGetLot(2, out var lot2));
        var updated = Assert.Single(lot2.Sims);
        Assert.True(updated.DirtyMotives);
        for (var m = 0; m < updated.Motives.Length; m++)
        {
            Assert.True(float.IsFinite(updated.Motives[m]), $"Motive {m} was non-finite.");
            Assert.InRange(updated.Motives[m], -100f, 100f);
        }
        Assert.NotEqual(90f, updated.Motives[(int)Motive.Hunger]);
    }

    [Fact]
    public void NeighborLotController_CareerAndSchoolSchedules_TransitionOnAndOffShift()
    {
        var controller = new NeighborLotController();
        controller.Configure(null, new MockCareerHost(startHour: 9, endHour: 15, salary: 180));

        var adult = new InactiveSimState
        {
            NeighborId = 20,
            Guid = 0x10000020u,
            Name = "Working Adult",
            FamilyId = 4,
        };
        adult.PersonData[(int)PersonData.PersonAge] = 30;
        adult.PersonData[(int)PersonData.JobType] = 1;
        adult.PersonData[(int)PersonData.JobPromotionLevel] = 1;
        adult.PersonData[(int)PersonData.JobPerformance] = 10;

        var child = new InactiveSimState
        {
            NeighborId = 21,
            Guid = 0x10000021u,
            Name = "School Kid",
            FamilyId = 4,
        };
        child.PersonData[(int)PersonData.PersonAge] = 10;

        var mgr = new NeighborhoodManager();
        mgr.UpdateSimLocation(20, 3);
        mgr.UpdateSimLocation(21, 3);

        controller.RegisterInactiveLot(3, new InactiveLotState
        {
            House = 3,
            FamilyChunkId = 4,
            FamilyNumber = 4,
            IsActive = false,
            Sims = [adult, child],
        });

        // 10:00 -> on shift (9..15)
        controller.TickNeighborhood(60, currentHour: 10, currentMinute: 0, currentDay: 1, mgr);
        Assert.True(controller.TryGetLot(3, out var onShiftLot));
        Assert.True(onShiftLot.Sims[0].IsAtWorkOrSchool);
        Assert.Equal("At work", onShiftLot.Sims[0].ActivitySummary);
        Assert.Equal((short)1, onShiftLot.Sims[0].PersonData[(int)PersonData.IsPersonAtWorkSchool]);
        Assert.True(onShiftLot.Sims[1].IsAtWorkOrSchool);
        Assert.Equal("At school", onShiftLot.Sims[1].ActivitySummary);
        Assert.Equal("At work", mgr.GetSimDetails(20)?.ActivitySummary);
        Assert.Equal("At school", mgr.GetSimDetails(21)?.ActivitySummary);

        // 16:00 -> off shift -> salary credited, performance boosted, activity reset
        controller.TickNeighborhood(60, currentHour: 16, currentMinute: 0, currentDay: 1, mgr);
        Assert.True(controller.TryGetLot(3, out var offShiftLot));
        Assert.False(offShiftLot.Sims[0].IsAtWorkOrSchool);
        Assert.Equal((short)0, offShiftLot.Sims[0].PersonData[(int)PersonData.IsPersonAtWorkSchool]);
        Assert.Equal((short)12, offShiftLot.Sims[0].PersonData[(int)PersonData.JobPerformance]);
        Assert.Equal(180, offShiftLot.BudgetDelta);
        Assert.False(offShiftLot.Sims[1].IsAtWorkOrSchool);
        Assert.DoesNotContain("Work", offShiftLot.Sims[0].ActivitySummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("School", offShiftLot.Sims[1].ActivitySummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(controller.RecentEvents.Count >= 4);
    }

    [Fact]
    public void NeighborLotController_VacantCommunityActiveAndMissingLots_SkippedWithoutPhantoms()
    {
        const uint activeSimGuid = 0x30000001u;
        const uint missingLotGuid = 0x30000002u;
        const uint communityGuid = 0x30000003u;

        var famActive = Family.CreateNew(familyNumber: 1, houseNumber: 5, members: [activeSimGuid]);
        var famMissing = Family.CreateNew(familyNumber: 2, houseNumber: 19, members: [missingLotGuid]);
        var famCommunity = Family.CreateNew(familyNumber: 3, houseNumber: 21, members: [communityGuid]);
        var famVacant = Family.CreateNew(familyNumber: 4, houseNumber: 6, members: []);

        var ngh = EmptyIff()
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 1, famActive.Write(1))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 2, famMissing.Write(2))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 3, famCommunity.Write(3))
            .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 4, famVacant.Write(4));

        var tempDir = Path.Combine(Path.GetTempPath(), "sims-nlc-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Houses"));
        try
        {
            var controller = new NeighborLotController();
            controller.EnsureSeededFromNeighborhood(
                ngh,
                tempDir,
                activeLotIds: new HashSet<int> { 5 });

            Assert.False(controller.TryGetLot(5, out _));
            Assert.False(controller.TryGetLot(19, out _));
            Assert.False(controller.TryGetLot(21, out _));
            Assert.False(controller.TryGetLot(6, out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void NeighborLotController_SnapshotRoundTripAndApplyToWorld_PreservesMotivesSkillsRelationships()
    {
        const uint sim1Guid = 0x40000001u;
        const uint sim2Guid = 0x40000002u;

        var controller = new NeighborLotController();
        var sim1 = new InactiveSimState
        {
            NeighborId = 11,
            Guid = sim1Guid,
            Name = "Sim One",
            FamilyId = 2,
            Relationships = new Dictionary<int, int[]> { [12] = [40, 20] },
        };
        sim1.Motives[(int)Motive.Fun] = 65f;
        sim1.PersonData[(int)PersonData.CookingHouseBrokenSkill] = 300;
        Array.Copy(sim1.Motives, sim1.SavedValues7CC, 16);

        var sim2 = new InactiveSimState
        {
            NeighborId = 12,
            Guid = sim2Guid,
            Name = "Sim Two",
            FamilyId = 2,
            Relationships = new Dictionary<int, int[]> { [11] = [40, 20] },
        };
        sim2.Motives[(int)Motive.Social] = -20f;
        Array.Copy(sim2.Motives, sim2.SavedValues7CC, 16);

        controller.RegisterInactiveLot(2, new InactiveLotState
        {
            House = 2,
            FamilyChunkId = 2,
            FamilyNumber = 2,
            IsActive = false,
            BudgetDelta = 250,
            Sims = [sim1, sim2],
        });

        controller.TickNeighborhood(300, currentHour: 12, currentMinute: 0, currentDay: 1);

        var json = JsonSerializer.Serialize(controller.CaptureSnapshot());
        var restoredSnap = JsonSerializer.Deserialize<NeighborLotControllerSnapshot>(json);

        var restored = new NeighborLotController();
        restored.RestoreSnapshot(restoredSnap);
        Assert.True(restored.TryGetLot(2, out var restoredLot));
        Assert.True(restoredLot.Sims[0].DirtyMotives);
        Assert.True(restoredLot.Sims[0].DirtyRelationships);

        // Apply to a live SimulationWorld
        var world = new SimulationWorld { ActiveLotNumber = 2 };
        var def = new short[108];
        def[(int)ObjdField.Type] = XPerson.PersonType;
        var p1 = new XPerson(1, def) { TypeRecord = new TypeRecord(unchecked((int)sim1Guid)) { Name = "Sim One" } };
        p1[PersonData.NeighborId] = 11;
        var p2 = new XPerson(2, def) { TypeRecord = new TypeRecord(unchecked((int)sim2Guid)) { Name = "Sim Two" } };
        p2[PersonData.NeighborId] = 12;
        world.Neighbors[11] = new NeighborState(11, def) { TypeRecordKey = unchecked((int)sim1Guid), Name = "Sim One", PersonRecordVersion = 10 };
        world.Neighbors[12] = new NeighborState(12, def) { TypeRecordKey = unchecked((int)sim2Guid), Name = "Sim Two", PersonRecordVersion = 10 };
        world.Add(p1);
        world.Add(p2);

        var applied = restored.ApplyToWorld(world, 2);
        Assert.True(applied);
        Assert.Equal(restoredLot.Sims[0].Motives[(int)Motive.Fun], p1[Motive.Fun]);
        Assert.True(world.Neighbors[11].Relationships.Get(12, 0) >= 43);
        Assert.Equal(250, restoredLot.BudgetDelta); // world.FamilyBudget is null in standalone world test
    }

    [Fact]
    public void InteractionTestEvaluator_VanillaGate_PreservesClassicBehaviorWhenTrackingDisabled()
    {
        var world = new SimulationWorld { ActiveLotNumber = 6 };
        world.StateClasses.Add(new StateClassEntry(6, 1));
        Assert.False(world.Neighborhood.TrackingEnabled);

        var personDef = new short[108];
        personDef[(int)ObjdField.Type] = XPerson.PersonType;
        var visitor = new XPerson(1, personDef)
        {
            SelectionWord5CC = 1,
            StateValue = 6,
        };
        visitor[PersonData.GreetStatus] = 0;

        static InteractionTable BuildTable(string name, int flags)
        {
            var chunk = new ChunkBuilder().S16(1).S16(8);
            chunk.U16(0).U16(0).S32(0).U32((uint)flags).S32(0).U32(0).F32(0).U32(0).S32(0);
            var (file, resource) = chunk.InFile("TTAB");
            return InteractionTable.Read(file, resource).WithNames(_ => name);
        }

        var objDef = new short[108];
        objDef[(int)ObjdField.Type] = 4;
        var fridge = new XObject(2, objDef)
        {
            TypeRecord = new TypeRecord(100)
            {
                Interactions = BuildTable("Have A Meal", 0x0),
            },
        };
        fridge[ObjectData.Room] = 3;

        var toilet = new XObject(3, objDef)
        {
            TypeRecord = new TypeRecord(101)
            {
                Interactions = BuildTable("Use", 0x1),
            },
        };
        toilet[ObjectData.Room] = 3;

        // Vanilla disabled (TrackingEnabled == false): AllowVisitors=1 Toilet allowed even at GreetStatus=0; AllowVisitors=0 Fridge blocked even at GreetStatus=1.
        var toiletSlot = new ActionQueueSlot { Person = visitor, Owner = toilet, Secondary = toilet, RawDword10 = 0 };
        InteractionTestEvaluator.Evaluate(world, visitor, [toiletSlot], autonomy: false);
        Assert.True((toiletSlot.Flags & InteractionTestEvaluator.Passed) != 0);

        visitor[PersonData.GreetStatus] = 1;
        var fridgeSlot = new ActionQueueSlot { Person = visitor, Owner = fridge, Secondary = fridge, RawDword10 = 0 };
        InteractionTestEvaluator.Evaluate(world, visitor, [fridgeSlot], autonomy: false);
        Assert.True((fridgeSlot.Flags & InteractionTestEvaluator.Passed) == 0);

        // Connected Neighborhood enabled (TrackingEnabled == true): GreetStatus=0 blocks indoor Toilet; GreetStatus=1 unlocks Fridge.
        world.Neighborhood.TrackingEnabled = true;
        visitor[PersonData.GreetStatus] = 0;
        var toiletSlotBlocked = new ActionQueueSlot { Person = visitor, Owner = toilet, Secondary = toilet, RawDword10 = 0 };
        InteractionTestEvaluator.Evaluate(world, visitor, [toiletSlotBlocked], autonomy: false);
        Assert.True((toiletSlotBlocked.Flags & InteractionTestEvaluator.Passed) == 0);

        visitor[PersonData.GreetStatus] = 1;
        var fridgeSlotUnlocked = new ActionQueueSlot { Person = visitor, Owner = fridge, Secondary = fridge, RawDword10 = 0 };
        InteractionTestEvaluator.Evaluate(world, visitor, [fridgeSlotUnlocked], autonomy: false);
        Assert.True((fridgeSlotUnlocked.Flags & InteractionTestEvaluator.Passed) != 0);
    }

    [Fact]
    public void NeighborLotController_PetAutonomyGuard_SkipsHumanChoresAndSkills()
    {
        var controller = new NeighborLotController();
        var pet = new InactiveSimState
        {
            NeighborId = 20,
            Guid = 0xAABBCC01,
            Name = "Buddy",
            FamilyId = 2,
        };
        pet.PersonData[(int)PersonData.Gender] = 2; // Dog bit
        pet.Motives[(int)Motive.Hunger] = -40f;
        pet.Motives[(int)Motive.Energy] = 80f;
        pet.Motives[(int)Motive.Fun] = 10f;
        Array.Copy(pet.Motives, pet.SavedValues7CC, 16);

        controller.RegisterInactiveLot(2, new InactiveLotState
        {
            House = 2,
            FamilyChunkId = 2,
            FamilyNumber = 2,
            IsActive = false,
            Sims = [pet],
        });

        var activities = new HashSet<string>();
        for (var i = 0; i < 6; i++)
        {
            controller.TickNeighborhood(300, currentHour: 14, currentMinute: 0, currentDay: 1);
            Assert.True(controller.TryGetLot(2, out var lot));
            activities.Add(lot.Sims[0].ActivitySummary);
        }

        Assert.DoesNotContain("Cooking", activities);
        Assert.DoesNotContain("Using toilet", activities);
        Assert.DoesNotContain("Showering", activities);
        Assert.DoesNotContain("Exercising", activities);
        Assert.DoesNotContain("Playing chess", activities);
        Assert.DoesNotContain("Cleaning", activities);
        Assert.DoesNotContain("Painting", activities);

        Assert.True(controller.TryGetLot(2, out var finalLot));
        var finalPet = finalLot.Sims[0];
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.CookingHouseBrokenSkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.CreativitySkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.LogicSkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.BodyFurnitureSkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.CleaningSkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.MechanicalTricksSkill]);
        Assert.Equal(0, finalPet.PersonData[(int)PersonData.CharismaObedienceSkill]);
    }

    [Fact]
    public void NeighborLotController_FlushToNeighborhood_PreservesDirtyRelationshipsAndFlushesBudgetDelta()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        var nghPath = Path.Combine(profile.UserData, "Neighborhood.iff");
        var ngh = IffFile.Open(nghPath);
        var famiType = ResourceType.Parse("FAMI");
        var fam2Chunk = ngh.OfType(famiType).First(c => c.Id == 2);
        var fam2 = Family.Read(ngh, fam2Chunk).WithHouseNumber(2).WithBudget(8500);
        ngh = ngh.WithAddedOrReplacedChunk(famiType, fam2Chunk.Id, fam2.Write(fam2Chunk.Id), fam2Chunk.Flags, fam2Chunk.Label);

        var nbrsChunk = ngh.OfType(ResourceType.Parse("NBRS")).First();
        var nbrs = Neighbors.ReadRecoveringSupportedPrefix(ngh, nbrsChunk);
        var memberGuid = fam2.Members[0];
        var nbrEntry = nbrs.Entries.First(e => e.Guid == memberGuid);
        var otherEntry = nbrs.Entries.First(e => e.NeighborId != nbrEntry.NeighborId);

        var controller = new NeighborLotController();
        var sim1 = new InactiveSimState
        {
            NeighborId = nbrEntry.NeighborId,
            Guid = memberGuid,
            Name = nbrEntry.Name ?? $"Sim {nbrEntry.NeighborId}",
            FamilyId = unchecked((short)fam2.FamilyNumber),
        };
        sim1.Motives[(int)Motive.Social] = -30f;
        var sim2 = new InactiveSimState
        {
            NeighborId = otherEntry.NeighborId,
            Guid = otherEntry.Guid,
            Name = otherEntry.Name ?? $"Sim {otherEntry.NeighborId}",
            FamilyId = unchecked((short)fam2.FamilyNumber),
        };
        sim2.Motives[(int)Motive.Social] = -30f;

        controller.RegisterInactiveLot(2, new InactiveLotState
        {
            House = 2,
            FamilyChunkId = fam2Chunk.Id,
            FamilyNumber = unchecked((short)fam2.FamilyNumber),
            IsActive = false,
            BudgetDelta = 500,
            Sims = [sim1, sim2],
        });

        controller.TickNeighborhood(300, currentHour: 12, currentMinute: 0, currentDay: 1);
        Assert.True(controller.TryGetLot(2, out var lot2));
        Assert.True(lot2.Sims[0].DirtyRelationships);
        Assert.True(lot2.Sims[0].DirtyRelationshipsForNbrs);

        var updatedNgh = controller.FlushToNeighborhood(ngh, out var changed, flushBudgets: true, out var flushedBudgets);
        Assert.True(changed);
        Assert.Equal(500, flushedBudgets[2]);
        Assert.Equal(0, lot2.BudgetDelta);
        Assert.Equal(9000, Family.Read(updatedNgh, updatedNgh.Find(famiType, fam2Chunk.Id)!).Budget);

        // DirtyRelationships remains true for ApplyToWorld while DirtyRelationshipsForNbrs is cleared
        Assert.True(lot2.Sims[0].DirtyRelationships);
        Assert.False(lot2.Sims[0].DirtyRelationshipsForNbrs);

        var world = new SimulationWorld { ActiveLotNumber = 2 };
        var def = new short[108];
        def[(int)ObjdField.Type] = XPerson.PersonType;
        var p1 = new XPerson(1, def) { TypeRecord = new TypeRecord(unchecked((int)memberGuid)) };
        p1[PersonData.NeighborId] = nbrEntry.NeighborId;
        var p2 = new XPerson(2, def) { TypeRecord = new TypeRecord(unchecked((int)otherEntry.Guid)) };
        p2[PersonData.NeighborId] = otherEntry.NeighborId;
        world.Neighbors[nbrEntry.NeighborId] = new NeighborState(nbrEntry.NeighborId, def) { TypeRecordKey = unchecked((int)memberGuid), PersonRecordVersion = 10 };
        world.Neighbors[otherEntry.NeighborId] = new NeighborState(otherEntry.NeighborId, def) { TypeRecordKey = unchecked((int)otherEntry.Guid), PersonRecordVersion = 10 };
        world.Add(p1);
        world.Add(p2);

        Assert.True(controller.ApplyToWorld(world, 2));
        Assert.True(p1.Relationships.Count(otherEntry.NeighborId) > 0);
        Assert.False(lot2.Sims[0].DirtyRelationships);
    }

    [Fact]
    public void NeighborLotController_EnsureSeededFromNeighborhood_SkipsAwayHouseholdSimsTrackedByManager()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var manager = new NeighborhoodManager();
        manager.SyncFromUserData(profile.UserData);

        var fam5 = Family.Read(ngh, ngh.OfType(ResourceType.Parse("FAMI")).First(c => c.Id != 0 && Family.Read(ngh, c).HouseNumber == 5));
        var awayGuid = fam5.Members[0];
        var awaySim = manager.GetSimLocationByGuid(awayGuid)!;
        manager.OnSimLotSwitched(awaySim.SimId, 5, 2, isVisiting: true, lotObjectId: 99);

        // Pass an empty saved House05 IFF so ObjM has no Sims; fallback family.Members must skip awayGuid
        var emptyHouse5Bytes = EmptyIff().Bytes.ToArray();
        var controller = new NeighborLotController();
        controller.EnsureSeededFromNeighborhood(
            ngh,
            profile.UserData,
            activeLotIds: new HashSet<int> { 2 },
            savedLotBytes: new Dictionary<short, byte[]> { [5] = emptyHouse5Bytes },
            manager: manager);

        Assert.True(controller.TryGetLot(5, out var lot5));
        Assert.DoesNotContain(lot5.Sims, s => s.Guid == awayGuid);
        Assert.Equal(fam5.Members.Count - 1, lot5.Sims.Count);
    }

    private sealed class MockCareerHost(short startHour, short endHour, short salary) : ISimulationCareerHost
    {
        public IReadOnlyList<int> CareerTable() => [1];
        public bool CareerExists(short career) => career == 1;
        public short? JobData(short career, short level, short field) => field switch
        {
            0 => 1,
            1 => salary,
            12 => startHour,
            13 => endHour,
            _ => 0,
        };
    }

    private static IffFile EmptyIff()
    {
        var header = new byte[64];
        Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1").CopyTo(header, 0);
        return new IffFile(header, "synthetic.iff");
    }

    private static byte[] EncodeStrMinusThree(params string[] values)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        writer.Write((short)-3);
        writer.Write(checked((short)values.Length));
        foreach (var value in values)
        {
            writer.Write((byte)1);
            writer.Write(Encoding.Latin1.GetBytes(value));
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        writer.Flush();
        return ms.ToArray();
    }

    private static string? TryFindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "packs", "connected-neighborhood", "pack.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
