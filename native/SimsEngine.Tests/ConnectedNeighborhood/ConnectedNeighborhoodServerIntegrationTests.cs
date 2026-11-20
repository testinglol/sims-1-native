using System.Text.Json;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.Tests.E2E;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests.ConnectedNeighborhood;

[Collection("Neighborhoods")]
public sealed class ConnectedNeighborhoodServerIntegrationTests
{
    [Fact]
    public void VanillaPreservation_DefaultDisabled_KeepsVanillaStateAndRejectsFocusLot()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);
        EnsureHouseExists(profile.UserData, 5, 21);
        using var session = E2EHarness.StartServer(profile, house: 5);

        Assert.False(session.Server.ConnectedNeighborhoodEnabled);
        Assert.False(session.World!.Neighborhood.TrackingEnabled);

        var state = session.State();
        Assert.False(state.TryGetProperty("connectedNeighborhood", out _));
        var firstSim = state.GetProperty("sims")[0];
        Assert.False(firstSim.TryGetProperty("lotId", out _));
        Assert.False(firstSim.TryGetProperty("isOnFocusedLot", out _));

        var focusResp = session.HandleJson("focusLot", new { house = 21 });
        Assert.False(focusResp.GetProperty("ok").GetBoolean());

        // Vanilla save omits connectedNeighborhood from SaveEnvelope
        var saveResp = session.HandleJson("save", new { slot = "vanilla-check", lot = false });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        var envelopePath = saveResp.GetProperty("result").GetProperty("path").GetString()!;
        var envelopeJson = File.ReadAllText(envelopePath);
        Assert.DoesNotContain("connectedNeighborhood", envelopeJson, StringComparison.OrdinalIgnoreCase);

        var loadResp = session.HandleJson("load", new { slot = "vanilla-check" });
        Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
        Assert.False(session.Server.ConnectedNeighborhoodEnabled);
    }

    [Fact]
    public void SetConnectedNeighborhood_PopulatesStateMetadataAndSimLocationFields()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5);
        using var session = E2EHarness.StartServer(profile, house: 5);

        var toggleResp = session.HandleJson("setConnectedNeighborhood", new
        {
            enabled = true,
            adjacentRendering = true,
            storyProgression = true,
            maxLiveSplitLots = 4,
        });
        Assert.True(toggleResp.GetProperty("ok").GetBoolean(), toggleResp.ToString());
        Assert.True(session.Server.ConnectedNeighborhoodEnabled);
        Assert.True(session.World!.Neighborhood.TrackingEnabled);

        var state = session.State();
        Assert.True(state.TryGetProperty("connectedNeighborhood", out var cn));
        Assert.True(cn.GetProperty("enabled").GetBoolean());
        Assert.Equal(5, cn.GetProperty("focusedHouse").GetInt32());
        Assert.Equal(5, cn.GetProperty("homeHouse").GetInt32());

        var activeLots = cn.GetProperty("activeLots");
        Assert.True(activeLots.GetArrayLength() >= 1);
        Assert.Contains(activeLots.EnumerateArray(), lot => lot.GetProperty("house").GetInt32() == 5 && lot.GetProperty("isFocused").GetBoolean());

        var sims = state.GetProperty("sims");
        Assert.True(sims.GetArrayLength() >= 1);
        var sim0 = sims[0];
        Assert.Equal(5, sim0.GetProperty("lotId").GetInt32());
        Assert.True(sim0.GetProperty("isOnFocusedLot").GetBoolean());
        Assert.True(sim0.TryGetProperty("activity", out _));
        Assert.True(sim0.TryGetProperty("lotName", out _));

        // Toggling back off clears pool and restores vanilla State() shape
        var offResp = session.HandleJson("setConnectedNeighborhood", new { enabled = false });
        Assert.True(offResp.GetProperty("ok").GetBoolean(), offResp.ToString());
        Assert.False(session.Server.ConnectedNeighborhoodEnabled);
        var offState = session.State();
        Assert.False(offState.TryGetProperty("connectedNeighborhood", out _));
    }

    [Fact]
    public void ThreePlusLots_PersistAcrossFocusLotAndPendingLotSwitch_WithLruDehydrationAndRehydration()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7, 1, 21),
            "Expected residential/community houses 5, 7, 1, 21 to exist in game directory.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 1, 2, 3, 4, 5, 7, 21);
        var occupied = EnsureThreeOccupiedHouses(profile, preferred: [5, 7, 1, 2, 3, 4], count: 3);
        var h0 = occupied[0];
        var h1 = occupied[1];
        var h2 = occupied[2];

        using var session = E2EHarness.StartServer(profile, house: h0);

        // Use maxLiveSplitLots=2 across 3+ lots so LRU dehydration + rehydration is exercised
        var enableResp = session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 2 });
        Assert.True(enableResp.GetProperty("ok").GetBoolean(), enableResp.ToString());

        // Mutate h0 (sim skill + clock ticks)
        var sim0 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        var sim0Guid = unchecked((uint)sim0.TypeRecord!.Key);
        sim0[PersonData.CookingHouseBrokenSkill] = 777;
        session.BatchRun(5);
        var ticks0Before = session.World.Clock.Ticks;
        Assert.True(ticks0Before >= 5);

        // Focus h1
        var focus1 = session.HandleJson("focusLot", new { house = h1 });
        Assert.True(focus1.GetProperty("ok").GetBoolean(), focus1.ToString());
        Assert.Equal((int)h1, focus1.GetProperty("result").GetProperty("house").GetInt32());
        session.BatchRun(8);
        var sim1 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        var sim1Guid = unchecked((uint)sim1.TypeRecord!.Key);
        sim1[PersonData.LogicSkill] = 666;
        session.BatchRun(2);
        Assert.Contains(session.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == unchecked((int)sim1Guid));
        var ticks1Before = session.World.Clock.Ticks;
        Assert.True(ticks1Before >= 10);

        // Focus h2 -> exceeds maxLiveSplitLots=2, dehydrates LRU non-home/non-focused lot (h1)
        var focus2 = session.HandleJson("focusLot", new { house = h2 });
        Assert.True(focus2.GetProperty("ok").GetBoolean(), focus2.ToString());
        var sim2 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0 || p[PersonData.NeighborId] > 0);
        var sim2Guid = unchecked((uint)sim2.TypeRecord!.Key);
        sim2[PersonData.CreativitySkill] = 555;
        session.BatchRun(6);
        var ticks2Before = session.World.Clock.Ticks;
        Assert.True(ticks2Before >= 6);

        Assert.True(session.Server.SessionPool.LiveSessions.Count <= 2);
        Assert.True(session.Server.SessionPool.TryGetSaved(h1, out _));

        // Travel from h2 to Community Lot 21 via PendingLotSwitch
        var travellerGuid = sim2.TypeRecord!.Key;
        var travellerFamily = sim2[PersonData.FamilyNumber];
        session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, travellerFamily);
        session.BatchRun(1);
        session.DrainEvents();
        Assert.Equal(21, session.State().GetProperty("house").GetInt32());

        // Focus back to h1 (rehydrated from SavedLotStates), h2, and h0 using TypeRecord.Key
        var back1 = session.HandleJson("focusLot", new { house = h1 });
        Assert.True(back1.GetProperty("ok").GetBoolean(), back1.ToString());
        var h1People = session.World!.WalkOrder.OfType<XPerson>().ToArray();
        var restoredSim1 = h1People.FirstOrDefault(p => p.TypeRecord?.Key == unchecked((int)sim1Guid));
        Assert.True(restoredSim1 is not null,
            $"Expected sim1Guid=0x{sim1Guid:X8} on h1={h1}, found [{string.Join(", ", h1People.Select(p => $"{p.Id}:0x{(uint)(p.TypeRecord?.Key ?? 0):X8}(fam={p[PersonData.FamilyNumber]},nid={p[PersonData.NeighborId]})"))}].");
        Assert.Equal((short)666, restoredSim1![PersonData.LogicSkill]);
        Assert.True(session.World.Clock.Ticks >= ticks1Before);

        var back2 = session.HandleJson("focusLot", new { house = h2 });
        Assert.True(back2.GetProperty("ok").GetBoolean(), back2.ToString());
        var restoredSim2 = session.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == unchecked((int)sim2Guid));
        Assert.Equal((short)555, restoredSim2[PersonData.CreativitySkill]);
        Assert.True(session.World.Clock.Ticks >= ticks2Before);

        var back0 = session.HandleJson("focusLot", new { house = h0 });
        Assert.True(back0.GetProperty("ok").GetBoolean(), back0.ToString());
        var restoredSim0 = session.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == unchecked((int)sim0Guid));
        Assert.Equal((short)777, restoredSim0[PersonData.CookingHouseBrokenSkill]);
        Assert.True(session.World.Clock.Ticks >= ticks0Before);
    }

    [Fact]
    public void MultiLotSaveCloseLoad_RestoresAllThreeLotsClocksAndSimStates()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7, 1),
            "Expected residential houses 5, 7, 1 to exist in game directory.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 1, 2, 3, 4, 5, 7);
        var occupied = EnsureThreeOccupiedHouses(profile, preferred: [5, 7, 1, 2, 3, 4], count: 3);
        var h0 = occupied[0];
        var h1 = occupied[1];
        var h2 = occupied[2];

        string envelopePath;
        int ticks0, ticks1, ticks2;
        uint sim0Guid, sim1Guid, sim2Guid;
        ushort fam0ChunkId, fam1ChunkId, fam2ChunkId;

        using (var session = E2EHarness.StartServer(profile, house: h0))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

            var sim0 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
            sim0Guid = unchecked((uint)sim0.TypeRecord!.Key);
            fam0ChunkId = session.Server.SessionPool.LiveSessions[h0].ActiveFamilyChunkId!.Value;
            sim0[PersonData.CookingHouseBrokenSkill] = 811;
            session.BatchRun(5);

            Assert.True(session.HandleJson("focusLot", new { house = h1 }).GetProperty("ok").GetBoolean());
            session.BatchRun(7);
            var sim1 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
            sim1Guid = unchecked((uint)sim1.TypeRecord!.Key);
            fam1ChunkId = session.Server.SessionPool.LiveSessions[h1].ActiveFamilyChunkId!.Value;
            sim1[PersonData.MechanicalTricksSkill] = 722;
            session.BatchRun(2);

            Assert.True(session.HandleJson("focusLot", new { house = h2 }).GetProperty("ok").GetBoolean());
            var sim2 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0 || p[PersonData.NeighborId] > 0);
            sim2Guid = unchecked((uint)sim2.TypeRecord!.Key);
            fam2ChunkId = session.Server.SessionPool.LiveSessions[h2].ActiveFamilyChunkId!.Value;
            sim2[PersonData.CharismaObedienceSkill] = 633;
            session.BatchRun(9);

            // Capture exact clocks right before save
            ticks2 = session.World.Clock.Ticks;
            ticks1 = session.Server.SessionPool.LiveSessions[h1].World.Clock.Ticks;
            ticks0 = session.Server.SessionPool.LiveSessions[h0].World.Clock.Ticks;
            Assert.True(ticks0 >= 5);
            Assert.True(ticks1 >= 7);
            Assert.True(ticks2 >= 9);

            var saveResp = session.HandleJson("save", new { slot = "cn-multi", lot = true });
            Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
            envelopePath = saveResp.GetProperty("result").GetProperty("path").GetString()!;

            // Verify FAMI chunks on disk remain distinct for all three occupied houses
            var savedNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
            var famiType = ResourceType.Parse("FAMI");
            Assert.Equal((int)h0, Family.Read(savedNgh, savedNgh.Find(famiType, fam0ChunkId)!).HouseNumber);
            Assert.Equal((int)h1, Family.Read(savedNgh, savedNgh.Find(famiType, fam1ChunkId)!).HouseNumber);
            Assert.Equal((int)h2, Family.Read(savedNgh, savedNgh.Find(famiType, fam2ChunkId)!).HouseNumber);

            using (var envDoc = JsonDocument.Parse(File.ReadAllBytes(envelopePath)))
            {
                var cn = envDoc.RootElement.GetProperty("connectedNeighborhood");
                Assert.True(cn.GetProperty("options").GetProperty("enabled").GetBoolean());
                Assert.Equal((int)h2, cn.GetProperty("focusedHouse").GetInt32());
                Assert.Equal((int)h0, cn.GetProperty("homeHouse").GetInt32());
                var lots = cn.GetProperty("lots");
                Assert.True(lots.TryGetProperty(h1.ToString(), out _));
                Assert.True(lots.TryGetProperty(h0.ToString(), out _));
            }

            var closeResp = session.HandleJson("closeLot", new { save = false });
            Assert.True(closeResp.GetProperty("ok").GetBoolean(), closeResp.ToString());
            Assert.Null(session.World);
        }

        // Start a brand-new server in vanilla mode (Enabled=false) and load the Connected Neighborhood save
        using (var restoredSession = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.False(restoredSession.Server.ConnectedNeighborhoodEnabled);

            var loadResp = restoredSession.HandleJson("load", new { slot = "cn-multi" });
            Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
            Assert.True(restoredSession.Server.ConnectedNeighborhoodEnabled);
            Assert.Equal((int)h2, restoredSession.State().GetProperty("house").GetInt32());

            var restoredSim2 = restoredSession.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == unchecked((int)sim2Guid));
            Assert.Equal((short)633, restoredSim2[PersonData.CharismaObedienceSkill]);
            Assert.Equal(ticks2, restoredSession.World.Clock.Ticks);

            // Switch focus to h1 and h0 and verify persisted clocks & skills
            Assert.True(restoredSession.HandleJson("focusLot", new { house = h1 }).GetProperty("ok").GetBoolean());
            var restoredSim1 = restoredSession.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == unchecked((int)sim1Guid));
            Assert.Equal((short)722, restoredSim1[PersonData.MechanicalTricksSkill]);
            Assert.Equal(ticks1, restoredSession.World.Clock.Ticks);

            Assert.True(restoredSession.HandleJson("focusLot", new { house = h0 }).GetProperty("ok").GetBoolean());
            var restoredSim0 = restoredSession.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == unchecked((int)sim0Guid));
            Assert.Equal((short)811, restoredSim0[PersonData.CookingHouseBrokenSkill]);
            Assert.Equal(ticks0, restoredSession.World.Clock.Ticks);

            // Also verify turning connected mode off after loading a CN save works cleanly without errors
            Assert.True(restoredSession.HandleJson("setConnectedNeighborhood", new { enabled = false }).GetProperty("ok").GetBoolean());
            Assert.False(restoredSession.Server.ConnectedNeighborhoodEnabled);
            Assert.False(restoredSession.State().TryGetProperty("connectedNeighborhood", out _));
        }
    }

    [Fact]
    public void FamiIntegrity_PreservedAcrossDehydrationAndCanonicalLotSave()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7), "Expected houses 5 and 7.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 7);
        using var session = E2EHarness.StartServer(profile, house: 5);

        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 1 }).GetProperty("ok").GetBoolean());
        var fam5ChunkId = session.Server.SessionPool.LiveSessions[5].ActiveFamilyChunkId!.Value;
        var origNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var fam5Orig = Family.Read(origNgh, origNgh.Find(ResourceType.Parse("FAMI"), fam5ChunkId)!);

        // focusLot(7) with maxLiveSplitLots=1 dehydrates House 5
        Assert.True(session.HandleJson("focusLot", new { house = 7 }).GetProperty("ok").GetBoolean());
        var fam7ChunkId = session.Server.SessionPool.LiveSessions[7].ActiveFamilyChunkId!.Value;
        var fam7Orig = Family.Read(origNgh, origNgh.Find(ResourceType.Parse("FAMI"), fam7ChunkId)!);

        // Save twice with lot=true (previously 1st save corrupted FAMI 1 in memory and 2nd save wrote corruption to disk)
        Assert.True(session.HandleJson("save", new { slot = "fami-1", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("save", new { slot = "fami-2", lot = true }).GetProperty("ok").GetBoolean());

        var diskNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famiType = ResourceType.Parse("FAMI");
        var fam5Disk = Family.Read(diskNgh, diskNgh.Find(famiType, fam5ChunkId)!);
        var fam7Disk = Family.Read(diskNgh, diskNgh.Find(famiType, fam7ChunkId)!);

        Assert.Equal(5, fam5Disk.HouseNumber);
        Assert.Equal(fam5Orig.Members.Count, fam5Disk.Members.Count);
        Assert.Equal(7, fam7Disk.HouseNumber);
        Assert.Equal(fam7Orig.Members.Count, fam7Disk.Members.Count);
    }

    [Fact]
    public void SimlessBuildMode_AndBudget999999_PreservedAcrossDehydrationAndRehydration()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 1), "Expected houses 5 and 1.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 1);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 1 }).GetProperty("ok").GetBoolean());

        var f1 = session.HandleJson("focusLot", new { house = 1 });
        Assert.True(f1.GetProperty("ok").GetBoolean(), f1.ToString());
        Assert.True(f1.GetProperty("result").GetProperty("simless").GetBoolean());
        Assert.Equal((short)1, session.World![SimulationGlobal.SimlessBuildMode]);
        Assert.Equal(999999, session.State().GetProperty("budget").GetInt32());

        // Dehydrate House 1 by focusing House 5, then rehydrate House 1
        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.True(session.Server.SessionPool.TryGetSaved(1, out _));

        var f1Back = session.HandleJson("focusLot", new { house = 1 });
        Assert.True(f1Back.GetProperty("ok").GetBoolean(), f1Back.ToString());
        Assert.True(f1Back.GetProperty("result").GetProperty("simless").GetBoolean());
        Assert.Equal((short)1, session.World![SimulationGlobal.SimlessBuildMode]);
        Assert.Equal(999999, session.State().GetProperty("budget").GetInt32());
    }

    [Fact]
    public void FocusLotInvalidHouse_PreservesTileCursorAndState()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5), "Expected house 5.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var catalogResp = session.HandleJson("catalog", new { });
        Assert.True(catalogResp.GetProperty("ok").GetBoolean(), catalogResp.ToString());
        var budget = session.State().GetProperty("budget").GetInt32();
        var item = catalogResp.GetProperty("result").GetProperty("objects").EnumerateArray()
            .First(i => i.GetProperty("price").GetInt32() > 0 && i.GetProperty("price").GetInt32() <= budget);
        var guid = item.GetProperty("guid").GetUInt32();
        Assert.True(session.HandleJson("mode", new { mode = "buy" }).GetProperty("ok").GetBoolean());
        var previewResp = session.HandleJson("previewObject", new { guid, x = 25 * 16, y = 25 * 16, level = 1, direction = 1 });
        Assert.True(previewResp.GetProperty("ok").GetBoolean(), previewResp.ToString());
        var cursorCountBefore = session.World!.Objects.Values.Count(o => o.TypeRecord?.Key == 0x437);
        Assert.Equal(1, cursorCountBefore);

        var invalidFocus = session.HandleJson("focusLot", new { house = 999 });
        Assert.False(invalidFocus.GetProperty("ok").GetBoolean());
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());

        var cursorCountAfter = session.World.Objects.Values.Count(o => o.TypeRecord?.Key == 0x437);
        Assert.Equal(cursorCountBefore, cursorCountAfter);
    }

    [Fact]
    public void StartFromHouseZero_FocusResidentialThenCommunity_PreservesHomeHouseAndPersistsNeighborhood()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 21), "Expected houses 5 and 21.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);
        using var session = E2EHarness.StartServer(profile, house: 0);

        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var f5 = session.HandleJson("focusLot", new { house = 5 });
        Assert.True(f5.GetProperty("ok").GetBoolean(), f5.ToString());
        Assert.Equal(5, f5.GetProperty("result").GetProperty("homeHouse").GetInt32());

        var f21 = session.HandleJson("focusLot", new { house = 21 });
        Assert.True(f21.GetProperty("ok").GetBoolean(), f21.ToString());
        Assert.True(f21.GetProperty("result").GetProperty("simless").GetBoolean());
        Assert.False(f21.GetProperty("result").GetProperty("away").GetBoolean());
        Assert.Equal(5, f21.GetProperty("result").GetProperty("homeHouse").GetInt32());

        var saveResp = session.HandleJson("save", new { slot = "from-zero", lot = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        var envelopePath = saveResp.GetProperty("result").GetProperty("path").GetString()!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(envelopePath));
        Assert.True(doc.RootElement.TryGetProperty("neighborhoodState", out var nghState));
        Assert.False(string.IsNullOrEmpty(nghState.GetString()));
        Assert.Equal(5, doc.RootElement.GetProperty("connectedNeighborhood").GetProperty("homeHouse").GetInt32());
    }

    [Fact]
    public void PendingLotSwitch_FollowedByBatchRun_PreservesNeighborhoodManagerLocation()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 21), "Expected houses 5 and 21.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var traveller = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        var travellerGuid = traveller.TypeRecord!.Key;
        var travellerFamily = traveller[PersonData.FamilyNumber];
        var travellerNeighborId = traveller[PersonData.NeighborId];

        session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, travellerFamily);
        session.BatchRun(1);
        Assert.Equal(21, session.State().GetProperty("house").GetInt32());
        Assert.Equal(21, session.Server.NeighborhoodState.GetSimLocation(travellerNeighborId));

        // Run subsequent ticks while House 5 remains live in the pool
        session.BatchRun(3);
        Assert.Equal(21, session.Server.NeighborhoodState.GetSimLocation(travellerNeighborId));
        var details = session.Server.NeighborhoodState.GetSimDetails(travellerNeighborId);
        Assert.NotNull(details);
        Assert.True(details!.IsVisiting);
        Assert.Contains(travellerNeighborId, session.Server.NeighborhoodState.GetLotSummary(21)!.VisitorSimIds);

        var activeLots = session.State().GetProperty("connectedNeighborhood").GetProperty("activeLots");
        var lot5Entry = activeLots.EnumerateArray().First(l => l.GetProperty("house").GetInt32() == 5);
        Assert.DoesNotContain((int)travellerNeighborId, lot5Entry.GetProperty("simIds").EnumerateArray().Select(e => e.GetInt32()));

        // Focusing back to House 5 while Sim is still at 21 must preserve location 21
        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        session.BatchRun(2);
        Assert.Equal(21, session.Server.NeighborhoodState.GetSimLocation(travellerNeighborId));
    }

    [Fact]
    public void BackgroundSession_NoticeAudioSelectionAndDirtyIsolation()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7), "Expected houses 5 and 7.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 7);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        Assert.True(session.HandleJson("focusLot", new { house = 7 }).GetProperty("ok").GetBoolean());
        var bgSim = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        Assert.True(session.HandleJson("speed", new { value = 1 }).GetProperty("ok").GetBoolean());

        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("save", new { slot = "clean-baseline", lot = false }).GetProperty("ok").GetBoolean());
        session.DrainEvents();

        var focusedSimBefore = session.State().GetProperty("selectedSim").GetInt32();
        var bgSession = session.Server.SessionPool.LiveSessions[7];
        bgSession.SaveDirty = false;

        // Dispatch selection/state/sound notices directly on unfocused background session 7
        bgSession.World.DispatchNotice(0xe4, bgSim.Id);
        bgSession.World.DispatchNotice(0xda, 0);
        var bgSoundPlayed = bgSession.World.PlaySoundEvent?.Invoke("ui_button_click", bgSim.Id, true, true) ?? false;
        session.BatchRun(2);

        var leakedEvents = session.DrainEvents();
        Assert.DoesNotContain(leakedEvents, e => e.TryGetProperty("type", out var t) && t.GetString() is "state" or "tutorialFlash");
        Assert.Equal(focusedSimBefore, session.State().GetProperty("selectedSim").GetInt32());
        Assert.False(bgSoundPlayed);
        Assert.True(bgSession.SaveDirty);
    }

    [Fact]
    public void FocusLot_LotValuationParity_NoDoubleCounting()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7), "Expected houses 5 and 7.");

        int baselineLotValue, baselineObjects, baselineArch;
        using (var baselineProfile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 7))
        using (var baselineSession = E2EHarness.StartServer(baselineProfile, house: 7))
        {
            var br = baselineSession.State().GetProperty("budgetReport");
            baselineLotValue = br.GetProperty("lotValue").GetInt32();
            baselineObjects = br.GetProperty("objects").GetInt32();
            baselineArch = br.GetProperty("architecture").GetInt32();
        }

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 7);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 1 }).GetProperty("ok").GetBoolean());

        Assert.True(session.HandleJson("focusLot", new { house = 7 }).GetProperty("ok").GetBoolean());
        var brFresh = session.State().GetProperty("budgetReport");
        Assert.Equal(baselineLotValue, brFresh.GetProperty("lotValue").GetInt32());
        Assert.Equal(baselineObjects, brFresh.GetProperty("objects").GetInt32());
        Assert.Equal(baselineArch, brFresh.GetProperty("architecture").GetInt32());

        // Dehydrate House 7 via LRU eviction then rehydrate
        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = 7 }).GetProperty("ok").GetBoolean());
        var brHydrated = session.State().GetProperty("budgetReport");
        Assert.Equal(baselineLotValue, brHydrated.GetProperty("lotValue").GetInt32());
        Assert.Equal(baselineObjects, brHydrated.GetProperty("objects").GetInt32());
        Assert.Equal(baselineArch, brHydrated.GetProperty("architecture").GetInt32());
    }

    [Fact]
    public void FocusLotResidential_AllowsBuyObject_WhilePendingLotSwitchRestrictsAway()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7, 21), "Expected houses 5, 7, 21.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 7, 21);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var f7 = session.HandleJson("focusLot", new { house = 7 });
        Assert.True(f7.GetProperty("ok").GetBoolean(), f7.ToString());
        Assert.False(f7.GetProperty("result").GetProperty("away").GetBoolean());

        var catalogResp = session.HandleJson("catalog", new { });
        Assert.True(catalogResp.GetProperty("ok").GetBoolean(), catalogResp.ToString());
        var budget = session.State().GetProperty("budget").GetInt32();
        var item = catalogResp.GetProperty("result").GetProperty("objects").EnumerateArray()
            .First(i => i.GetProperty("price").GetInt32() > 0 && i.GetProperty("price").GetInt32() <= budget);
        var guid = item.GetProperty("guid").GetUInt32();

        JsonElement? buySuccess = null;
        for (var tx = 20; tx <= 40 && buySuccess is null; tx += 2)
        {
            for (var ty = 20; ty <= 40 && buySuccess is null; ty += 2)
            {
                var attempt = session.HandleJson("buyObject", new { guid, x = tx * 16 + 8, y = ty * 16 + 8, level = 1, direction = 1 });
                if (attempt.GetProperty("ok").GetBoolean())
                    buySuccess = attempt;
            }
        }
        Assert.NotNull(buySuccess);

        // Travel via PendingLotSwitch to House 21 -> Away == true, buyObject blocked
        var sim7 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, sim7.TypeRecord!.Key, sim7[PersonData.FamilyNumber]);
        session.BatchRun(1);
        Assert.True(session.State().GetProperty("away").GetBoolean());
        var awayBuy = session.HandleJson("buyObject", new { guid, x = 25 * 16 + 8, y = 25 * 16 + 8, level = 1, direction = 1 });
        Assert.False(awayBuy.GetProperty("ok").GetBoolean());
        Assert.Contains("away lot", awayBuy.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetConnectedNeighborhood_ReconcilesSuspendedHomeLotAndClearsStaleLocationsOnToggle()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 21), "Expected houses 5 and 21.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);
        using var session = E2EHarness.StartServer(profile, house: 5);

        // Travel in vanilla mode from 5 -> 21 (populates suspendedHomeLot)
        var traveller = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        var travellerGuid = traveller.TypeRecord!.Key;
        var travellerFamily = traveller[PersonData.FamilyNumber];
        var travellerNeighborId = traveller[PersonData.NeighborId];
        session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, travellerFamily);
        session.BatchRun(1);
        Assert.Equal(21, session.State().GetProperty("house").GetInt32());
        Assert.True(session.State().GetProperty("away").GetBoolean());

        // Enabling CN absorbs suspendedHomeLot into SessionPool and preserves homeHouse == 5
        var onResp = session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 });
        Assert.True(onResp.GetProperty("ok").GetBoolean(), onResp.ToString());
        Assert.Equal(5, onResp.GetProperty("result").GetProperty("homeHouse").GetInt32());
        Assert.True(session.Server.SessionPool.TryGetSaved(5, out _));

        // Disabling CN while still on away lot 21 restores suspendedHomeLot so PendingLotSwitch(-1) returns to 5
        var offResp = session.HandleJson("setConnectedNeighborhood", new { enabled = false });
        Assert.True(offResp.GetProperty("ok").GetBoolean(), offResp.ToString());
        session.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, travellerFamily);
        session.BatchRun(1);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.False(session.State().GetProperty("away").GetBoolean());

        // Toggle true -> mutate NeighborhoodState -> false -> true resets stale location back to 5
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());
        session.Server.NeighborhoodState.UpdateSimLocation(travellerNeighborId, 21);
        Assert.Equal(21, session.Server.NeighborhoodState.GetSimLocation(travellerNeighborId));

        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = false }).GetProperty("ok").GetBoolean());
        Assert.Empty(session.Server.NeighborhoodState.GetAllSimLocations());

        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(travellerNeighborId));
    }

    [Fact]
    public void SetConnectedNeighborhood_DisableWhileVisitingAwayLot_PreservesSuspendedHomeAndAllowsReturnOrReenable()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 21), "Expected houses 5 and 21.");

        // 1. true -> PendingLotSwitch(21) -> false -> true restores homeHouse == 5 and house 5 in pool
        using (var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21))
        using (var session = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

            var traveller = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
            session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
            session.BatchRun(1);
            Assert.Equal(21, session.State().GetProperty("house").GetInt32());
            Assert.True(session.State().GetProperty("away").GetBoolean());

            var offResp = session.HandleJson("setConnectedNeighborhood", new { enabled = false });
            Assert.True(offResp.GetProperty("ok").GetBoolean(), offResp.ToString());
            var offState = session.State();
            Assert.True(offState.GetProperty("away").GetBoolean());
            Assert.Equal(5, offState.GetProperty("homeHouse").GetInt32());

            var reOnResp = session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 });
            Assert.True(reOnResp.GetProperty("ok").GetBoolean(), reOnResp.ToString());
            Assert.Equal(5, reOnResp.GetProperty("result").GetProperty("homeHouse").GetInt32());
            Assert.True(session.Server.SessionPool.TryGetSaved(5, out _) || session.Server.SessionPool.TryGetLive(5, out _));
            Assert.True(session.State().GetProperty("away").GetBoolean());
        }

        // 2. true -> PendingLotSwitch(21) -> false -> PendingLotSwitch(-1) returns cleanly to home house 5
        using (var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21))
        using (var session = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

            var traveller = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
            var travellerGuid = traveller.TypeRecord!.Key;
            var travellerFamily = traveller[PersonData.FamilyNumber];
            session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, travellerFamily);
            session.BatchRun(1);
            Assert.Equal(21, session.State().GetProperty("house").GetInt32());

            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = false }).GetProperty("ok").GetBoolean());
            Assert.True(session.State().GetProperty("away").GetBoolean());
            Assert.Equal(5, session.State().GetProperty("homeHouse").GetInt32());

            session.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, travellerFamily);
            session.BatchRun(1);
            Assert.Equal(5, session.State().GetProperty("house").GetInt32());
            Assert.False(session.State().GetProperty("away").GetBoolean());
        }

        // 3. Simless focusLot(21) -> false does NOT create a bogus away/suspendedHome state
        using (var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21))
        using (var session = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());
            var focus21 = session.HandleJson("focusLot", new { house = 21 });
            Assert.True(focus21.GetProperty("ok").GetBoolean(), focus21.ToString());
            Assert.True(focus21.GetProperty("result").GetProperty("simless").GetBoolean());
            Assert.False(focus21.GetProperty("result").GetProperty("away").GetBoolean());

            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = false }).GetProperty("ok").GetBoolean());
            var vanillaState = session.State();
            Assert.False(vanillaState.GetProperty("away").GetBoolean());
            Assert.Equal(0, vanillaState.GetProperty("homeHouse").GetInt32());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CrossLotNeighbors_RelationshipsPreservedAcrossLruEvictionAndMultiLotSaveLoad(int maxLiveSplitLots)
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 7, 1),
            "Expected residential houses 5, 7, 1 to exist in game directory.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 1, 2, 3, 4, 5, 7);
        var occupied = EnsureThreeOccupiedHouses(profile, preferred: [5, 7, 1, 2, 3, 4], count: 3);
        var h0 = occupied[0];
        var h1 = occupied[1];
        var h2 = occupied[2];

        short n0Id, n1Id, n2Id;

        using (var session = E2EHarness.StartServer(profile, house: h0))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots }).GetProperty("ok").GetBoolean());
            n0Id = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0)[PersonData.NeighborId];

            Assert.True(session.HandleJson("focusLot", new { house = h1 }).GetProperty("ok").GetBoolean());
            n1Id = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0)[PersonData.NeighborId];
            var n1OnH1 = session.World.Neighbors[n1Id];
            var n0OnH1 = session.World.Neighbors[n0Id];
            n1OnH1.Relationships.Resize(n0Id, 3);
            n1OnH1.Relationships.Set(n0Id, 0, 77);
            n1OnH1.Relationships.Set(n0Id, 1, 55);
            session.World.MarkRelationshipsChanged(n1OnH1, n0OnH1);

            Assert.True(session.HandleJson("focusLot", new { house = h2 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, session.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(55, session.World.Neighbors[n1Id].Relationships.Get(n0Id, 1));
            n2Id = session.World.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0)[PersonData.NeighborId];
            var n2OnH2 = session.World.Neighbors[n2Id];
            var n1OnH2 = session.World.Neighbors[n1Id];
            n2OnH2.Relationships.Resize(n1Id, 3);
            n2OnH2.Relationships.Set(n1Id, 0, 88);
            session.World.MarkRelationshipsChanged(n2OnH2, n1OnH2);

            Assert.True(session.HandleJson("focusLot", new { house = h0 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, session.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, session.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            var n0OnH0 = session.World.Neighbors[n0Id];
            var n2OnH0 = session.World.Neighbors[n2Id];
            n0OnH0.Relationships.Resize(n2Id, 3);
            n0OnH0.Relationships.Set(n2Id, 0, 64);
            session.World.MarkRelationshipsChanged(n0OnH0, n2OnH0);

            Assert.True(session.HandleJson("focusLot", new { house = h1 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, session.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, session.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            Assert.Equal(64, session.World.Neighbors[n0Id].Relationships.Get(n2Id, 0));

            Assert.True(session.HandleJson("focusLot", new { house = h2 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, session.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, session.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            Assert.Equal(64, session.World.Neighbors[n0Id].Relationships.Get(n2Id, 0));

            var saveResp = session.HandleJson("save", new { slot = $"nbrs-m{maxLiveSplitLots}", lot = true });
            Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());

            // Verify Neighborhood.iff on disk contains all 3 cross-lot relationships
            var diskNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
            var nbrsChunk = diskNgh.OfType(ResourceType.Parse("NBRS")).First();
            var diskNbrs = Neighbors.ReadRecoveringSupportedPrefix(diskNgh, nbrsChunk);
            var diskN1 = diskNbrs.Entries.First(e => e.NeighborId == n1Id);
            var diskN2 = diskNbrs.Entries.First(e => e.NeighborId == n2Id);
            var diskN0 = diskNbrs.Entries.First(e => e.NeighborId == n0Id);
            Assert.Equal([77, 55, 0], diskN1.Relationships[n0Id]);
            Assert.Equal([88, 0, 0], diskN2.Relationships[n1Id]);
            Assert.Equal([64, 0, 0], diskN0.Relationships[n2Id]);

            Assert.True(session.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        }

        using (var restored = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(restored.HandleJson("load", new { slot = $"nbrs-m{maxLiveSplitLots}" }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, restored.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, restored.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            Assert.Equal(64, restored.World.Neighbors[n0Id].Relationships.Get(n2Id, 0));

            Assert.True(restored.HandleJson("focusLot", new { house = h1 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, restored.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, restored.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            Assert.Equal(64, restored.World.Neighbors[n0Id].Relationships.Get(n2Id, 0));

            Assert.True(restored.HandleJson("focusLot", new { house = h0 }).GetProperty("ok").GetBoolean());
            Assert.Equal(77, restored.World!.Neighbors[n1Id].Relationships.Get(n0Id, 0));
            Assert.Equal(88, restored.World.Neighbors[n2Id].Relationships.Get(n1Id, 0));
            Assert.Equal(64, restored.World.Neighbors[n0Id].Relationships.Get(n2Id, 0));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SimlessCommunityLot_DirtyChangesInPoolFlushToDiskOnSaveLotTrue(int maxLiveSplitLots)
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;
        Assert.True(E2EHarness.HasHouses(gameDir, 5, 21), "Expected houses 5 and 21.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);

        uint placedGuid = 0;
        int countBefore = 0;

        using (var session = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots }).GetProperty("ok").GetBoolean());

            var f21 = session.HandleJson("focusLot", new { house = 21 });
            Assert.True(f21.GetProperty("ok").GetBoolean(), f21.ToString());
            Assert.True(f21.GetProperty("result").GetProperty("simless").GetBoolean());
            Assert.False(f21.GetProperty("result").GetProperty("away").GetBoolean());

            var catalogResp = session.HandleJson("catalog", new { });
            Assert.True(catalogResp.GetProperty("ok").GetBoolean(), catalogResp.ToString());
            foreach (var item in catalogResp.GetProperty("result").GetProperty("objects").EnumerateArray())
            {
                var candidateGuid = item.GetProperty("guid").GetUInt32();
                if (item.GetProperty("price").GetInt32() <= 0) continue;
                var initialCount = session.World!.Objects.Values.Count(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == candidateGuid);
                for (var tx = 18; tx <= 44 && placedGuid == 0; tx += 2)
                {
                    for (var ty = 18; ty <= 44 && placedGuid == 0; ty += 2)
                    {
                        var buy = session.HandleJson("buyObject", new { guid = candidateGuid, x = tx * 16 + 8, y = ty * 16 + 8, level = 1, direction = 1 });
                        if (buy.GetProperty("ok").GetBoolean())
                        {
                            placedGuid = candidateGuid;
                            countBefore = initialCount;
                        }
                    }
                }
                if (placedGuid != 0) break;
            }
            Assert.NotEqual(0u, placedGuid);
            Assert.Equal(countBefore + 1, session.World!.Objects.Values.Count(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == placedGuid));

            Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
            if (maxLiveSplitLots == 1)
            {
                Assert.True(session.Server.SessionPool.TryGetSaved(21, out var saved21));
                Assert.True(saved21.SaveDirty);
            }
            else
            {
                Assert.True(session.Server.SessionPool.TryGetLive(21, out var live21));
                Assert.True(live21.SaveDirty);
            }

            var saveResp = session.HandleJson("save", new { slot = $"comm-dirty-m{maxLiveSplitLots}", lot = true });
            Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());

            if (maxLiveSplitLots == 1)
            {
                Assert.True(session.Server.SessionPool.TryGetSaved(21, out var saved21After));
                Assert.False(saved21After.SaveDirty);
            }
            else
            {
                Assert.True(session.Server.SessionPool.TryGetLive(21, out var live21After));
                Assert.False(live21After.SaveDirty);
            }
        }

        // Verify House21.iff on disk has the newly placed object when opened by a fresh server
        using (var fresh = E2EHarness.StartServer(profile, house: 21))
        {
            var countDisk = fresh.World!.Objects.Values.Count(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == placedGuid);
            Assert.Equal(countBefore + 1, countDisk);
        }
    }

    private static short[] EnsureThreeOccupiedHouses(IsolatedProfileScope profile, short[] preferred, int count = 3)
    {
        var occupied = FindOccupiedHouses(profile.UserData, preferred, count);
        var candidates = new short[] { 1, 2, 3, 4, 6, 8, 9, 10 };

        while (occupied.Length < count)
        {
            var targetHouse = candidates.FirstOrDefault(h => !occupied.Contains(h) &&
                (File.Exists(Path.Combine(profile.UserData, "Houses", $"House{h:D2}.iff")) ||
                 E2EHarness.FindHouseSource(profile.GameDir, 1, h) is not null));
            Assert.True(targetHouse > 0, $"No vacant candidate residential house available to seed {count} occupied lots.");

            var targetLotPath = Path.Combine(profile.UserData, "Houses", $"House{targetHouse:D2}.iff");
            if (!File.Exists(targetLotPath))
            {
                var src = E2EHarness.FindHouseSource(profile.GameDir, 1, targetHouse)!;
                Directory.CreateDirectory(Path.GetDirectoryName(targetLotPath)!);
                File.Copy(src, targetLotPath, overwrite: true);
            }

            // Ensure target lot is affordable and pick a Family Bin family (HouseNumber == 0)
            var lotIff = IffFile.Open(targetLotPath);
            var lotBasePrice = 0;
            if (lotIff.Find(ResourceType.Parse("SIMI"), 1) is { } simiChunk)
            {
                var simi = SimulationState.Read(lotIff, simiChunk).WithValuation(0, 0);
                lotBasePrice = Math.Max(0, simi.Field0x9C);
                lotIff = lotIff.WithChunkDataResized(simiChunk.Type, simiChunk.Id, simi.WriteChunkData());
                File.WriteAllBytes(targetLotPath, lotIff.Bytes.ToArray());
            }

            var nghPath = Path.Combine(profile.UserData, "Neighborhood.iff");
            var ngh = IffFile.Open(nghPath);
            var famiType = ResourceType.Parse("FAMI");
            ushort binFamilyId = 0;
            Family? binFamily = null;
            foreach (var chunk in ngh.OfType(famiType))
            {
                if (chunk.Id == 0) continue;
                var fam = Family.Read(ngh, chunk);
                if (fam.HouseNumber == 0 && fam.Members.Count > 0)
                {
                    binFamilyId = chunk.Id;
                    binFamily = fam;
                    break;
                }
            }
            Assert.True(binFamilyId != 0 && binFamily is not null,
                "Expected at least one Family Bin family with members in Neighborhood.iff.");

            if (binFamily!.Budget < lotBasePrice + 5000)
            {
                var boosted = binFamily.WithBudget(lotBasePrice + 10000);
                var existingChunk = ngh.Find(famiType, binFamilyId)!;
                ngh = ngh.WithAddedOrReplacedChunk(famiType, binFamilyId, boosted.Write(binFamilyId), existingChunk.Flags, existingChunk.Label);
                File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());
            }

            using (var seedSession = E2EHarness.StartServer(profile, house: 0))
            {
                var openResp = seedSession.HandleJson("openLot", new { house = targetHouse, moveInFamily = (int)binFamilyId });
                Assert.True(openResp.GetProperty("ok").GetBoolean(), openResp.ToString());
                var saveResp = seedSession.HandleJson("save", new { slot = $"seed-occ-{targetHouse}", lot = true });
                Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
                var closeResp = seedSession.HandleJson("closeLot", new { save = false });
                Assert.True(closeResp.GetProperty("ok").GetBoolean(), closeResp.ToString());
            }

            occupied = FindOccupiedHouses(profile.UserData, preferred, count);
        }

        Assert.True(occupied.Length >= count,
            $"Expected at least {count} occupied houses after seeding, found [{string.Join(", ", occupied)}].");
        return occupied;
    }

    private static short[] FindOccupiedHouses(string userData, short[] preferred, int count)
    {
        var nghPath = Path.Combine(userData, "Neighborhood.iff");
        if (!File.Exists(nghPath)) return [];
        var ngh = IffFile.Open(nghPath);
        var occupied = new HashSet<short>();
        foreach (var chunk in ngh.OfType(ResourceType.Parse("FAMI")))
        {
            if (chunk.Id == 0) continue;
            var fam = Family.Read(ngh, chunk);
            if (fam.HouseNumber is > 0 and <= short.MaxValue && fam.Members.Count > 0 &&
                File.Exists(Path.Combine(userData, "Houses", $"House{fam.HouseNumber:D2}.iff")))
            {
                occupied.Add((short)fam.HouseNumber);
            }
        }
        var result = new List<short>();
        foreach (var p in preferred)
        {
            if (occupied.Contains(p) && !result.Contains(p))
                result.Add(p);
        }
        foreach (var h in occupied.Order())
        {
            if (!result.Contains(h))
                result.Add(h);
        }
        return result.Take(count).ToArray();
    }

    private static void EnsureHouseExists(string userData, short sourceHouse, params short[] targetHouses)
    {
        var sourcePath = Path.Combine(userData, "Houses", $"House{sourceHouse:D2}.iff");
        foreach (var targetHouse in targetHouses)
        {
            var targetPath = Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff");
            if (!File.Exists(targetPath) && File.Exists(sourcePath))
                File.Copy(sourcePath, targetPath);
        }
    }

    [Fact]
    public void VisitLot_House06_UngreetedBlocksFridgeAndDoorKnockUnlocks()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 6))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 6);
        // Move bin family #3 (Roomies) into House06 just like T4_02/T2_F4_01.
        var nghPath = Path.Combine(profile.UserData, "Neighborhood.iff");
        var ngh = IffFile.Open(nghPath);
        var famiType = ResourceType.Parse("FAMI");
        foreach (var chunk in ngh.OfType(famiType).ToArray())
        {
            if (chunk.Id == 0) continue;
            var fam = Family.Read(ngh, chunk);
            if (fam.HouseNumber == 6)
            {
                ngh = ngh.WithAddedOrReplacedChunk(famiType, chunk.Id, fam.WithHouseNumber(0).Write(chunk.Id), chunk.Flags, chunk.Label);
            }
        }
        File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());
        using (var seedSession = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(seedSession.HandleJson("openLot", new { house = (short)6, moveInFamily = 3 }).GetProperty("ok").GetBoolean());
            Assert.True(seedSession.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());
        }

        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());
        var goth1 = session.State().GetProperty("sims")[0];
        var goth1Nid = goth1.GetProperty("neighborID").GetInt16();

        Assert.True(session.HandleJson("visitLot", new { house = (short)6, simId = goth1Nid, knock = false }).GetProperty("ok").GetBoolean());
        session.Step(5);

        var world6 = session.World!;
        var visitor = world6.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == goth1Nid);
        var state6 = session.State();
        var initMsg = $"initPos=({visitor.Position.X/16f:F2},{visitor.Position.Y/16f:F2})";
        var fridge = state6.GetProperty("objects").EnumerateArray().First(o =>
            (o.TryGetProperty("name", out var n) && (n.GetString() ?? "").Contains("Fridge", StringComparison.OrdinalIgnoreCase)) ||
            (o.TryGetProperty("catalogName", out var c) && (c.GetString() ?? "").Contains("Fridge", StringComparison.OrdinalIgnoreCase)));
        var fridgeObj = world6.ObjectById(fridge.GetProperty("id").GetInt16())!;
        var fridgeLead = InteractionMenus.Target(world6, fridgeObj);
        var menuResp = session.HandleJson("menu", new { objectID = fridge.GetProperty("id").GetInt32() });
        var entries = menuResp.GetProperty("result").EnumerateArray().ToArray();
        Assert.Empty(entries);

        var door = state6.GetProperty("objects").EnumerateArray().First(o =>
            (o.TryGetProperty("buildModeType", out var bmt) && bmt.TryGetInt32(out var tv) && tv == 1) ||
            (o.TryGetProperty("name", out var n) && (n.GetString() ?? "").Contains("Door", StringComparison.OrdinalIgnoreCase)));
        var doorId = door.GetProperty("id").GetInt32();
        var doorMenu = session.HandleJson("menu", new { objectID = doorId });
        var doorEntries = doorMenu.GetProperty("result").EnumerateArray().ToArray();
        var knockEntry = Assert.Single(doorEntries, e =>
            (e.GetProperty("name").GetString() ?? "").Contains("Knock", StringComparison.OrdinalIgnoreCase));

        var knockResp = session.HandleJson("interact", new
        {
            objectID = doorId,
            interaction = knockEntry.GetProperty("interaction").GetInt32(),
            param0 = knockEntry.GetProperty("param0").GetInt32(),
            global = knockEntry.GetProperty("global").GetBoolean(),
        });
        Assert.True(knockResp.GetProperty("ok").GetBoolean(), knockResp.ToString());
        Assert.Equal(1, visitor[PersonData.GreetStatus]);

        var postKnockFridgeMenu = session.HandleJson("menu", new { objectID = fridge.GetProperty("id").GetInt32() });
        var postEntries = postKnockFridgeMenu.GetProperty("result").EnumerateArray().ToArray();
        Assert.True(postEntries.Length > 0,
            "Expected Fridge menu entries to unlock after knocking on door.");

        visitor[Motive.Hunger] = -30f;
        var mealEntry = postEntries.FirstOrDefault(e =>
            (e.GetProperty("name").GetString() ?? "").Contains("Meal", StringComparison.OrdinalIgnoreCase) ||
            (e.GetProperty("name").GetString() ?? "").Contains("Snack", StringComparison.OrdinalIgnoreCase) ||
            (e.GetProperty("name").GetString() ?? "").Contains("Lunch", StringComparison.OrdinalIgnoreCase));
        if (mealEntry.ValueKind != JsonValueKind.Object) mealEntry = postEntries[0];
        Assert.True(session.HandleJson("interact", new
        {
            objectID = fridge.GetProperty("id").GetInt32(),
            interaction = mealEntry.GetProperty("interaction").GetInt32(),
            param0 = mealEntry.GetProperty("param0").GetInt32(),
            global = mealEntry.GetProperty("global").GetBoolean(),
        }).GetProperty("ok").GetBoolean());
        for (var b = 0; b < 24; b++)
            session.BatchRun(30);
        Assert.True(visitor[Motive.Hunger] > -30f,
            $"Expected Hunger > -30 after eating, got {visitor[Motive.Hunger]} at pos=({visitor.Position.X/16f:F1},{visitor.Position.Y/16f:F1}).");
    }

    [Fact]
    public void Wave1Caveat1_DeleteCharacterOnLotA_PrunesNeighborAndRelationshipsOnLiveLotB()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var world5 = session.World!;
        // Find a non-family character neighbor in world5.Neighbors
        var famiGuids = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"))
            .OfType(ResourceType.Parse("FAMI"))
            .Where(static c => c.Id != 0)
            .SelectMany(c => Family.Read(IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff")), c).Members)
            .Select(static g => unchecked((int)g))
            .ToHashSet();

        var victim = world5.Neighbors.Values.First(n =>
            SimulationWorld.IsCharacter(n) &&
            !famiGuids.Contains(n.TypeRecordKey) &&
            n.PersonRecordVersion > 0 &&
            n.PersonData[(int)PersonData.FamilyNumber] == 0);
        var goth1 = world5.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] > 0);
        var goth1Nbr = world5.Neighbors[goth1[PersonData.NeighborId]];
        goth1Nbr.Relationships.Resize(victim.Id, 2);
        goth1Nbr.Relationships.Set(victim.Id, 0, 55);
        world5.MarkRelationshipsChanged(goth1Nbr, victim);

        // Open House02 live in the pool so both Lot 5 and Lot 2 are live
        Assert.True(session.HandleJson("focusLot", new { house = 2 }).GetProperty("ok").GetBoolean());
        var world2 = session.World!;
        Assert.True(world2.Neighbors.ContainsKey(victim.Id));
        var goth1On2 = world2.Neighbors[goth1Nbr.Id];
        Assert.Equal(55, goth1On2.Relationships.Get(victim.Id, 0));

        // Focus back to Lot 5 and delete victim character
        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.True(world5.DeleteCharacter(victim, key => famiGuids.Contains(key)));

        // Focusing Lot 2 syncs NBRS deletions and prunes victim + stale relationship from world2
        Assert.True(session.HandleJson("focusLot", new { house = 2 }).GetProperty("ok").GetBoolean());
        Assert.False(world2.Neighbors.ContainsKey(victim.Id));
        Assert.Equal(0, goth1On2.Relationships.Count(victim.Id));
    }

    [Fact]
    public void Wave1Caveat2_BackgroundPoolSession_UsesSessionAwarePausePredicateOnPendingLotSwitch()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 21);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var sim = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        session.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, sim.TypeRecord!.Key, sim[PersonData.FamilyNumber]);
        session.BatchRun(1);
        session.DrainEvents();
        Assert.Equal(21, session.State().GetProperty("house").GetInt32());

        var world21 = session.World!;
        Assert.NotNull(world21.AutonomyHost);
        Assert.False(world21.AutonomyHost!.AutonomyLotState().Paused);

        // Focus House05 and pause House05 (speed=0) while keeping House21 running (speed=1) in pool
        Assert.True(session.HandleJson("focusLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        session.Server.SessionPool.LiveSessions[21].Speed = 1;
        Assert.True(session.HandleJson("speed", new { speed = 0 }).GetProperty("ok").GetBoolean());
        Assert.False(world21.AutonomyHost.AutonomyLotState().Paused);

        // Pause House21 in pool -> its session-aware AutonomyHost reports paused
        session.Server.SessionPool.LiveSessions[21].Speed = 0;
        Assert.True(world21.AutonomyHost.AutonomyLotState().Paused);
    }

    [Fact]
    public void StagedWalkGateway_VisitAndReturnHome_WalkTruePopulatesTransitOverridesAndCancelsCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());

        var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        var walkResp = session.HandleJson("visitLot", new { house = (short)2, simId = goth1Nid, knock = false, walk = true });
        Assert.True(walkResp.GetProperty("ok").GetBoolean(), walkResp.ToString());
        Assert.True(walkResp.GetProperty("result").GetProperty("inTransit").GetBoolean());
        Assert.True(session.Server.ActiveCrossLotTransits.ContainsKey(goth1Nid));

        var sawSeamOverride = false;
        var arrivedOnLot2 = false;
        for (var i = 0; i < 120; i++)
        {
            session.Step(1);
            if (session.World!.TransitVisualOverrides.Count > 0)
            {
                sawSeamOverride = true;
                var ov = session.World.TransitVisualOverrides.Values.First();
                Assert.InRange(ov.SeamProgress, 0f, 1f);
                Assert.InRange(ov.TileX, 0f, 63.999f);
                Assert.InRange(ov.TileY, 0f, 63.999f);
            }
            if (session.State().GetProperty("house").GetInt32() == 2)
            {
                arrivedOnLot2 = true;
                break;
            }
        }

        Assert.True(sawSeamOverride, "Expected OffLotSeamWalk to populate SimulationWorld.TransitVisualOverrides during walk: true transit.");
        Assert.True(arrivedOnLot2, "Expected staged walk transit to arrive on House02.");
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);

        // Start walk: true returnHome, then cancel mid-transit via immediate returnHome
        var returnWalk = session.HandleJson("returnHome", new { simId = goth1Nid, walk = true });
        Assert.True(returnWalk.GetProperty("ok").GetBoolean(), returnWalk.ToString());
        Assert.True(session.Server.ActiveCrossLotTransits.ContainsKey(goth1Nid));
        session.Step(2);

        var immediateReturn = session.HandleJson("returnHome", new { simId = goth1Nid, walk = false });
        Assert.True(immediateReturn.GetProperty("ok").GetBoolean(), immediateReturn.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
    }

    [Fact]
    public void VisitLot_SaveLotTrueWhileVisitingAwayResidential_PreservesDiskAndFamiOwnership()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 6))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 6);
        // Move bin family #3 (Roomies) into House06
        using (var seed = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(seed.HandleJson("openLot", new { house = (short)6, moveInFamily = 3 }).GetProperty("ok").GetBoolean());
            Assert.True(seed.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());
        }

        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());
        var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        Assert.True(session.HandleJson("visitLot", new { house = (short)6, simId = goth1Nid, knock = true }).GetProperty("ok").GetBoolean());
        var saveResp = session.HandleJson("save", new { slot = "away-res-save", lot = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        Assert.Equal("away", saveResp.GetProperty("result").GetProperty("lotSkipped").GetString());

        // Fresh server opening House06 directly must NOT contain visitor goth1Nid on disk, and Roomies FAMI stays on House06
        using var check = E2EHarness.StartServer(profile, house: 6);
        Assert.DoesNotContain(check.World!.WalkOrder.OfType<XPerson>(), p => p[PersonData.NeighborId] == goth1Nid);
        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var fam3 = Family.Read(ngh, ngh.Find(ResourceType.Parse("FAMI"), 3)!);
        Assert.Equal(6, fam3.HouseNumber);
    }

    [Fact]
    public void VanillaDisabled_RejectsVisitLotReturnHomeAndPreservesVanillaMenus()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);

        Assert.False(session.Server.ConnectedNeighborhoodEnabled);
        Assert.False(session.World!.Neighborhood.TrackingEnabled);

        var visitResp = session.HandleJson("visitLot", new { house = (short)2, knock = true });
        Assert.False(visitResp.GetProperty("ok").GetBoolean());

        var returnResp = session.HandleJson("returnHome", new { });
        Assert.False(returnResp.GetProperty("ok").GetBoolean());

        var state = session.State();
        Assert.False(state.TryGetProperty("connectedNeighborhood", out _));
        Assert.False(state.GetProperty("sims")[0].TryGetProperty("lotId", out _));
    }

    [Fact]
    public void StoryProgressionToggleOff_FreezesInactiveLotMotivesAcrossBatchRun()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 6))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 6);
        using (var seed = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(seed.HandleJson("openLot", new { house = (short)6, moveInFamily = 3 }).GetProperty("ok").GetBoolean());
            Assert.True(seed.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());
        }

        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, storyProgression = false }).GetProperty("ok").GetBoolean());
        var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        Assert.True(session.HandleJson("visitLot", new { house = (short)6, simId = goth1Nid, knock = false }).GetProperty("ok").GetBoolean());
        var hostBefore = session.World!.WalkOrder.OfType<XPerson>()
            .Where(p => p[PersonData.NeighborId] != goth1Nid && p[PersonData.FamilyNumber] > 0)
            .ToDictionary(p => p[PersonData.NeighborId], p => p[Motive.Hunger]);
        Assert.NotEmpty(hostBefore);

        Assert.True(session.HandleJson("returnHome", new { simId = goth1Nid }).GetProperty("ok").GetBoolean());
        session.BatchRun(600);

        Assert.True(session.HandleJson("visitLot", new { house = (short)6, simId = goth1Nid, knock = false }).GetProperty("ok").GetBoolean());
        foreach (var p in session.World!.WalkOrder.OfType<XPerson>().Where(p => hostBefore.ContainsKey(p[PersonData.NeighborId])))
            Assert.Equal(hostBefore[p[PersonData.NeighborId]], p[Motive.Hunger], 1);
    }

    [Fact]
    public void Stage2Transit_InterruptedByReturnHomeGroundInteractOrInteract_CancelsTransitCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true }).GetProperty("ok").GetBoolean());

        var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        static void AdvanceUntilStage2(ServerSession srv, short nid)
        {
            for (var i = 0; i < 120; i++)
            {
                srv.Step(1);
                if (srv.World!.TransitVisualOverrides.Count > 0 &&
                    srv.Server.ActiveCrossLotTransits.TryGetValue(nid, out var tr) &&
                    tr.Phase == SimsEngine.Simulation.Neighborhood.CrossLotTransitPhase.OffLotSeamWalk)
                {
                    return;
                }
            }
            Assert.Fail("Sim never entered Stage 2 OffLotSeamWalk.");
        }

        // 1. Interrupt Stage 2 outbound visitLot(walk: true) with returnHome(walk: false)
        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = goth1Nid, knock = false, walk = true }).GetProperty("ok").GetBoolean());
        AdvanceUntilStage2(session, goth1Nid);
        var retResp = session.HandleJson("returnHome", new { simId = goth1Nid, walk = false });
        Assert.True(retResp.GetProperty("ok").GetBoolean(), retResp.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);
        session.BatchRun(25);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(goth1Nid));

        // 2. Interrupt Stage 2 outbound visitLot(walk: true) with groundInteract ("Go Here")
        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = goth1Nid, knock = false, walk = true }).GetProperty("ok").GetBoolean());
        AdvanceUntilStage2(session, goth1Nid);
        var gMenu = session.HandleJson("groundMenu", new { x = 32 * 16 + 8, y = 32 * 16 + 8, level = 1 }).GetProperty("result");
        Assert.True(gMenu.GetArrayLength() > 0);
        var gChoice = gMenu[0];
        var gInt = session.HandleJson("groundInteract", new
        {
            x = 32 * 16 + 8,
            y = 32 * 16 + 8,
            level = 1,
            interaction = gChoice.GetProperty("interaction").GetByte(),
            param0 = gChoice.GetProperty("param0").GetInt16(),
        });
        Assert.True(gInt.GetProperty("ok").GetBoolean(), gInt.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);
        session.BatchRun(25);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(goth1Nid));

        // 3. Interrupt Stage 2 outbound visitLot(walk: true) with object interact
        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = goth1Nid, knock = false, walk = true }).GetProperty("ok").GetBoolean());
        AdvanceUntilStage2(session, goth1Nid);
        (int objId, byte inter, short p0)? foundObj = null;
        foreach (var obj in session.World!.WalkOrder.Where(o => o is not XPerson && o.TypeRecord is not null))
        {
            var menuResp = session.HandleJson("menu", new { objectID = obj.Id });
            if (!menuResp.GetProperty("ok").GetBoolean()) continue;
            var arr = menuResp.GetProperty("result");
            if (arr.GetArrayLength() > 0)
            {
                var c = arr[0];
                foundObj = (c.TryGetProperty("objectID", out var oid) && oid.ValueKind == System.Text.Json.JsonValueKind.Number ? oid.GetInt32() : obj.Id,
                    c.GetProperty("interaction").GetByte(),
                    c.GetProperty("param0").GetInt16());
                break;
            }
        }
        Assert.NotNull(foundObj);
        var oInt = session.HandleJson("interact", new
        {
            objectID = foundObj.Value.objId,
            interaction = foundObj.Value.inter,
            param0 = foundObj.Value.p0,
            global = false,
        });
        Assert.True(oInt.GetProperty("ok").GetBoolean(), oInt.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);
        session.BatchRun(25);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(goth1Nid));
    }

    [Fact]
    public void MaxLiveSplitLots1_EvictionProtectedDuringActiveTransit_CompletesWalkCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 1))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2, 1);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 1 }).GetProperty("ok").GetBoolean());
        var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = goth1Nid, knock = false, walk = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("returnHome", new { simId = goth1Nid, walk = true }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());

        // House02 must stay live in pool while transit is active, then complete return to House05
        Assert.True(session.Server.SessionPool.LiveSessions.ContainsKey(2));
        session.BatchRun(120);

        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(goth1Nid));
        Assert.True(session.Server.SessionPool.LiveSessions.Count <= 1);
    }

    [Fact]
    public void MidTransitCompanionRedirection_DoesNotYankRedirectedCompanion()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 1))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2, 1);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var sims = session.State().GetProperty("sims");
        var simA = sims[0].GetProperty("neighborID").GetInt16();
        var simB = sims[1].GetProperty("neighborID").GetInt16();

        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = simA, companionIds = new[] { simB }, knock = false, walk = true }).GetProperty("ok").GetBoolean());
        session.Step(2);

        // Redirect companion simB independently to House01
        Assert.True(session.HandleJson("visitLot", new { house = (short)1, simId = simB, knock = false, walk = false }).GetProperty("ok").GetBoolean());
        Assert.Equal(1, session.Server.NeighborhoodState.GetSimLocation(simB));

        Assert.True(session.HandleJson("focusLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        session.BatchRun(120);

        Assert.Equal(2, session.Server.NeighborhoodState.GetSimLocation(simA));
        Assert.Equal(1, session.Server.NeighborhoodState.GetSimLocation(simB));
    }

    [Fact]
    public void Wave1Caveat1_DehydratedLotRehydration_PrunesDeletedCharacterAndRelationships()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 1))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2, 1);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 2 }).GetProperty("ok").GetBoolean());

        var world5 = session.World!;
        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famiGuids = ngh.OfType(ResourceType.Parse("FAMI"))
            .Where(static c => c.Id != 0)
            .SelectMany(c => Family.Read(ngh, c).Members)
            .Select(static g => unchecked((int)g))
            .ToHashSet();

        var victim = world5.Neighbors.Values.First(n =>
            SimulationWorld.IsCharacter(n) &&
            !famiGuids.Contains(n.TypeRecordKey) &&
            n.PersonRecordVersion > 0 &&
            n.PersonData[(int)PersonData.FamilyNumber] == 0);
        var goth1 = world5.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] > 0);
        var goth1Nid = goth1[PersonData.NeighborId];
        var goth1Nbr = world5.Neighbors[goth1Nid];
        goth1Nbr.Relationships.Resize(victim.Id, 2);
        goth1Nbr.Relationships.Set(victim.Id, 0, 55);
        world5.MarkRelationshipsChanged(goth1Nbr, victim);

        // Open Lot 2, then open Lot 1 (evicting/dehydrating Lot 2 to SavedLotStates)
        Assert.True(session.HandleJson("focusLot", new { house = (short)2 }).GetProperty("ok").GetBoolean());
        Assert.True(session.World!.Neighbors.ContainsKey(victim.Id));
        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());
        Assert.False(session.Server.SessionPool.LiveSessions.ContainsKey(2));
        Assert.True(session.Server.SessionPool.SavedLotStates.ContainsKey(2));

        // Focus Lot 5 and delete victim character
        Assert.True(session.HandleJson("focusLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        Assert.True(world5.DeleteCharacter(victim, key => famiGuids.Contains(key)));

        // Rehydrating Lot 2 must prune victim and stale relationship
        Assert.True(session.HandleJson("focusLot", new { house = (short)2 }).GetProperty("ok").GetBoolean());
        Assert.False(session.World!.Neighbors.ContainsKey(victim.Id));
        Assert.Equal(0, session.World!.Neighbors[goth1Nid].Relationships.Count(victim.Id));
    }

    [Fact]
    public void SaveLoadWhileVisitingAway_DoesNotPhantomSeedHomeHouseOrOverwriteAwayNbrs()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, storyProgression = true }).GetProperty("ok").GetBoolean());

        var sims = session.State().GetProperty("sims");
        var sim0 = sims[0].GetProperty("neighborID").GetInt16();
        var comp = Enumerable.Range(1, sims.GetArrayLength() - 1)
            .Select(i => sims[i].GetProperty("neighborID").GetInt16())
            .ToArray();

        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = sim0, companionIds = comp, knock = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("save", new { slot = "all-away", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("load", new { slot = "all-away" }).GetProperty("ok").GetBoolean());

        // Set a distinct cooking skill on live sim0 on House02
        var liveSim0 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == sim0);
        liveSim0[PersonData.CookingHouseBrokenSkill] = 432;
        session.World.SavePersonRecord(liveSim0);

        session.BatchRun(600);

        // House05 must NOT have phantom-seeded any away Goth Sims into StoryProgressionController
        if (session.Server.StoryProgressionController.TryGetLot(5, out var lot5))
            Assert.Empty(lot5.Sims);
        Assert.Equal(2, session.Server.NeighborhoodState.GetSimLocation(sim0));
        Assert.Equal((short)432, liveSim0[PersonData.CookingHouseBrokenSkill]);
    }

    [Fact]
    public void FocusLot_SetMotive_SaveLoadBatchRun_PreservesModifiedMotiveOverStaleController()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 6))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 6);
        using (var seed = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(seed.HandleJson("openLot", new { house = (short)6, moveInFamily = 3 }).GetProperty("ok").GetBoolean());
            Assert.True(seed.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());
        }

        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, storyProgression = true }).GetProperty("ok").GetBoolean());

        Assert.True(session.HandleJson("focusLot", new { house = (short)6 }).GetProperty("ok").GetBoolean());
        session.BatchRun(30);
        var host = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0);
        var hostNid = host[PersonData.NeighborId];
        Assert.True(session.HandleJson("setMotive", new { objectID = host.Id, motive = "Fun", value = -90f }).GetProperty("ok").GetBoolean());

        Assert.True(session.HandleJson("focusLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        session.BatchRun(60);
        Assert.True(session.HandleJson("save", new { slot = "probe5a", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("load", new { slot = "probe5a" }).GetProperty("ok").GetBoolean());

        session.BatchRun(600);
        Assert.True(session.HandleJson("focusLot", new { house = (short)6 }).GetProperty("ok").GetBoolean());
        var reloadedHost = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == hostNid);
        // -90f plus at most two +25f autonomy steps stays well below 0; stale init (~31f) would jump to ~78f
        Assert.InRange(reloadedHost[Motive.Fun], -100f, 0f);
    }

    [Fact]
    public void InactiveProgression_FlushToNeighborhood_PreservesXPersonRelationshipsAndFamiBudgetAcrossVisitAndFreshOpen()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 6))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 6);
        using (var seed = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(seed.HandleJson("openLot", new { house = (short)6, moveInFamily = 3 }).GetProperty("ok").GetBoolean());
            Assert.True(seed.HandleJson("closeLot", new { save = true }).GetProperty("ok").GetBoolean());
        }

        short sim0Nid;
        short sim1Nid;
        int expectedRel;
        int expectedBudget;
        using (var session = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, storyProgression = true }).GetProperty("ok").GetBoolean());
            session.BatchRun(1800);

            Assert.True(session.Server.StoryProgressionController.TryGetLot(6, out var lot6));
            Assert.True(lot6.Sims.Count >= 2);
            sim0Nid = lot6.Sims[0].NeighborId;
            sim1Nid = lot6.Sims[1].NeighborId;
            expectedRel = lot6.Sims[0].Relationships[sim1Nid][0];
            Assert.True(expectedRel > 0);

            lot6.BudgetDelta += 500;
            var saveResp = session.HandleJson("save", new { slot = "probe3cd", lot = true });
            Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());

            // Path A: visitLot(6) after save must hydrate XPerson.Relationships (not int.MinValue)
            var goth1Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();
            Assert.True(session.HandleJson("visitLot", new { house = (short)6, simId = goth1Nid, knock = true }).GetProperty("ok").GetBoolean());
            var host0 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == sim0Nid);
            Assert.True(host0.Relationships.Count(sim1Nid) > 0);
            Assert.Equal(expectedRel, host0.Relationships.Get(sim1Nid, 0));

            expectedBudget = session.World.FamilyBudget!.ReadFamilyBudget();
            // Note: while visiting House06, FamilyBudget is House06's budget; verify it reflects the +500 salary
            Assert.True(expectedBudget >= 9000);
        }

        // Path B: fresh server opening House06 with ConnectedNeighborhood enabled sees +500 FAMI Budget AND XPerson.Relationships
        using var fresh = E2EHarness.StartServer(profile, house: 6);
        Assert.True(fresh.HandleJson("setConnectedNeighborhood", new { enabled = true, storyProgression = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(expectedBudget, fresh.World!.FamilyBudget!.ReadFamilyBudget());
        var freshHost0 = fresh.World.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == sim0Nid);
        Assert.True(freshHost0.Relationships.Count(sim1Nid) > 0);
        Assert.Equal(expectedRel, freshHost0.Relationships.Get(sim1Nid, 0));
    }

    [Fact]
    public void Stage1AndStage2Transit_ActionsExportAndCancelAction_CancelsTransitCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var sim0 = session.State().GetProperty("sims")[0];
        var simObjId = sim0.GetProperty("id").GetInt16();
        var simNid = sim0.GetProperty("neighborID").GetInt16();

        static JsonElement FindConnectedTransitAction(JsonElement statePayload, short nid)
        {
            foreach (var s in statePayload.GetProperty("sims").EnumerateArray())
            {
                if (s.GetProperty("neighborID").GetInt16() != nid) continue;
                foreach (var act in s.GetProperty("actions").EnumerateArray())
                {
                    var tok = act.GetProperty("token").GetString() ?? "";
                    if (tok.StartsWith("connected-transit-", StringComparison.Ordinal))
                        return act;
                }
            }
            return default;
        }

        // 1. Stage 1 (RoutingToGateway) exports cancellable action and cancelAction cancels cleanly
        var walk1 = session.HandleJson("visitLot", new { house = (short)2, simId = simNid, knock = false, walk = true });
        Assert.True(walk1.GetProperty("ok").GetBoolean(), walk1.ToString());
        Assert.True(session.Server.ActiveCrossLotTransits.TryGetValue(simNid, out var transit1));
        Assert.Equal(SimsEngine.Simulation.Neighborhood.CrossLotTransitPhase.RoutingToGateway, transit1.Phase);

        var stage1Action = FindConnectedTransitAction(session.State(), simNid);
        Assert.Equal(JsonValueKind.Object, stage1Action.ValueKind);
        Assert.True(stage1Action.GetProperty("cancellable").GetBoolean());
        Assert.True(stage1Action.GetProperty("active").GetBoolean());
        var stage1Token = stage1Action.GetProperty("token").GetString()!;

        var cancel1 = session.HandleJson("cancelAction", new { objectID = simObjId, token = stage1Token });
        Assert.True(cancel1.GetProperty("ok").GetBoolean(), cancel1.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);
        Assert.Equal(JsonValueKind.Undefined, FindConnectedTransitAction(session.State(), simNid).ValueKind);

        session.BatchRun(25);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(simNid));
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World.TransitVisualOverrides);

        // 2. Stage 2 (OffLotSeamWalk) exports cancellable action and cancelAction cancels cleanly
        var walk2 = session.HandleJson("visitLot", new { house = (short)2, simId = simNid, knock = false, walk = true });
        Assert.True(walk2.GetProperty("ok").GetBoolean(), walk2.ToString());
        var enteredStage2 = false;
        for (var i = 0; i < 120; i++)
        {
            session.Step(1);
            if (session.World.TransitVisualOverrides.Count > 0 &&
                session.Server.ActiveCrossLotTransits.TryGetValue(simNid, out var transit2) &&
                transit2.Phase == SimsEngine.Simulation.Neighborhood.CrossLotTransitPhase.OffLotSeamWalk)
            {
                enteredStage2 = true;
                break;
            }
        }
        Assert.True(enteredStage2, "Sim never entered Stage 2 OffLotSeamWalk.");

        var stage2Action = FindConnectedTransitAction(session.State(), simNid);
        Assert.Equal(JsonValueKind.Object, stage2Action.ValueKind);
        Assert.True(stage2Action.GetProperty("cancellable").GetBoolean());
        Assert.True(stage2Action.GetProperty("active").GetBoolean());
        var stage2Token = stage2Action.GetProperty("token").GetString()!;

        var cancel2 = session.HandleJson("cancelAction", new { objectID = simObjId, token = stage2Token });
        Assert.True(cancel2.GetProperty("ok").GetBoolean(), cancel2.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World.TransitVisualOverrides);
        Assert.Equal(JsonValueKind.Undefined, FindConnectedTransitAction(session.State(), simNid).ValueKind);

        session.BatchRun(25);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(simNid));
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World.TransitVisualOverrides);

        // 3. Vanilla mode rejects connected-transit token identically to vanilla
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = false }).GetProperty("ok").GetBoolean());
        var vanillaReject = session.HandleJson("cancelAction", new { objectID = simObjId, token = stage2Token });
        Assert.False(vanillaReject.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Wave1Caveat1_ThreeLotEvictionOrder_DeletedCharacterDoesNotResurrect()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 1, 3))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2, 1, 3);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 2 }).GetProperty("ok").GetBoolean());

        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famiGuids = ngh.OfType(ResourceType.Parse("FAMI"))
            .Where(static c => c.Id != 0)
            .SelectMany(c => Family.Read(ngh, c).Members)
            .Select(static g => unchecked((int)g))
            .ToHashSet();

        // Focus House02 and delete a non-family character
        Assert.True(session.HandleJson("focusLot", new { house = (short)2 }).GetProperty("ok").GetBoolean());
        var world2 = session.World!;
        var victim = world2.Neighbors.Values.First(n =>
            SimulationWorld.IsCharacter(n) &&
            !famiGuids.Contains(n.TypeRecordKey) &&
            n.PersonRecordVersion > 0 &&
            n.PersonData[(int)PersonData.FamilyNumber] == 0);
        Assert.True(world2.DeleteCharacter(victim, key => famiGuids.Contains(key)));

        // Focus House01: House02 is evicted while House05 (HomeHouse) remains live
        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());
        Assert.False(session.Server.SessionPool.LiveSessions.ContainsKey(2));
        Assert.True(session.Server.SessionPool.LiveSessions.ContainsKey(5));

        // Lower maxLiveSplitLots to 1 and focus House03 (evicting House05)
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = (short)3 }).GetProperty("ok").GetBoolean());
        Assert.False(session.Server.SessionPool.LiveSessions.ContainsKey(5));
        Assert.False(session.World!.Neighbors.ContainsKey(victim.Id));

        // Save and verify Neighborhood.iff NBRS on disk does not contain victim
        var saveResp = session.HandleJson("save", new { slot = "caveat1-3lot", lot = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        var diskNgh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var nbrsChunk = diskNgh.OfType(ResourceType.Parse("NBRS")).First();
        var diskNbrs = Neighbors.ReadRecoveringSupportedPrefix(diskNgh, nbrsChunk);
        Assert.DoesNotContain(diskNbrs.Entries, e => e.NeighborId == victim.Id);
    }

    [Fact]
    public void ReturnHome_Stage2CancelOnUnoccupiedHouse02_DoesNotDeleteVisitorViaPedestrianPortal()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 4 }).GetProperty("ok").GetBoolean());

        var sim0Nid = session.State().GetProperty("sims")[0].GetProperty("neighborID").GetInt16();

        // 1. Visit unoccupied residential House02 (immediate transfer)
        var visitResp = session.HandleJson("visitLot", new { house = (short)2, simId = sim0Nid, knock = false, walk = false });
        Assert.True(visitResp.GetProperty("ok").GetBoolean(), visitResp.ToString());
        Assert.Equal(2, session.State().GetProperty("house").GetInt32());

        // 2. Initiate returnHome(walk: true) and step until Stage 2 (OffLotSeamWalk)
        var retWalk = session.HandleJson("returnHome", new { simId = sim0Nid, walk = true });
        Assert.True(retWalk.GetProperty("ok").GetBoolean(), retWalk.ToString());

        var enteredStage2 = false;
        for (var i = 0; i < 120; i++)
        {
            session.Step(1);
            if (session.World!.TransitVisualOverrides.Count > 0 &&
                session.Server.ActiveCrossLotTransits.TryGetValue(sim0Nid, out var transit) &&
                transit.Phase == SimsEngine.Simulation.Neighborhood.CrossLotTransitPhase.OffLotSeamWalk)
            {
                enteredStage2 = true;
                break;
            }
        }
        Assert.True(enteredStage2, "Sim never entered Stage 2 OffLotSeamWalk on House02.");

        var stateBeforeCancel = session.State();
        var simOn2 = stateBeforeCancel.GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("neighborID").GetInt16() == sim0Nid);
        var sim2ObjId = simOn2.GetProperty("id").GetInt16();
        Assert.True(sim2ObjId > 0);
        var cancelToken = simOn2.GetProperty("actions").EnumerateArray()
            .Select(a => a.GetProperty("token").GetString() ?? "")
            .First(t => t.StartsWith("connected-transit-", StringComparison.Ordinal));

        // Stale native- token must still be rejected consistently even while connected transit is active
        var staleNativeReject = session.HandleJson("cancelAction", new { objectID = sim2ObjId, token = $"native-{sim2ObjId}-999999" });
        Assert.False(staleNativeReject.GetProperty("ok").GetBoolean());
        Assert.Contains("no longer cancellable", staleNativeReject.GetProperty("error").GetString() ?? "", StringComparison.OrdinalIgnoreCase);

        // 3. Cancel Stage 2 returnHome via cancelAction and run 25 ticks on House02
        var cancelResp = session.HandleJson("cancelAction", new { objectID = sim2ObjId, token = cancelToken });
        Assert.True(cancelResp.GetProperty("ok").GetBoolean(), cancelResp.ToString());
        Assert.Empty(session.Server.ActiveCrossLotTransits);
        Assert.Empty(session.World!.TransitVisualOverrides);

        session.BatchRun(25);

        Assert.Equal(2, session.State().GetProperty("house").GetInt32());
        Assert.Equal(2, session.Server.NeighborhoodState.GetSimLocation(sim0Nid));
        var activePersonOn2 = session.World!.WalkOrder.OfType<XPerson>()
            .FirstOrDefault(p => p[PersonData.NeighborId] == sim0Nid);
        Assert.NotNull(activePersonOn2);
        Assert.True(activePersonOn2.Id > 0);

        var postCancelSims = session.State().GetProperty("sims").EnumerateArray().ToArray();
        Assert.DoesNotContain(postCancelSims, s => s.GetProperty("id").GetInt16() == 0);
        var simStateOn2 = postCancelSims.First(s => s.GetProperty("neighborID").GetInt16() == sim0Nid);
        Assert.True(simStateOn2.GetProperty("id").GetInt16() > 0);
        Assert.Equal(2, simStateOn2.GetProperty("lotId").GetInt32());

        // 4. Subsequent returnHome(walk: false) cleanly returns the Sim to House05
        var retResp = session.HandleJson("returnHome", new { simId = sim0Nid, walk = false });
        Assert.True(retResp.GetProperty("ok").GetBoolean(), retResp.ToString());
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(sim0Nid));
        Assert.Contains(session.World!.WalkOrder.OfType<XPerson>(), p => p[PersonData.NeighborId] == sim0Nid);
        Assert.DoesNotContain(session.State().GetProperty("sims").EnumerateArray(), s => s.GetProperty("id").GetInt16() == 0);

        // 5. Defense-in-depth: autonomous visitor removal via PendingRemovals (RemoveObjectInstance 0x12) on away lot
        // cleanly transfers the Sim home to House05 rather than dropping them into id: 0 limbo.
        Assert.True(session.HandleJson("visitLot", new { house = (short)2, simId = sim0Nid, knock = false, walk = false }).GetProperty("ok").GetBoolean());
        var visitorOn2 = session.World!.WalkOrder.OfType<XPerson>().First(p => p[PersonData.NeighborId] == sim0Nid);
        session.World.PendingRemovals.Add(visitorOn2.Id);
        session.Step(1);
        Assert.Equal(5, session.State().GetProperty("house").GetInt32());
        Assert.Equal(5, session.Server.NeighborhoodState.GetSimLocation(sim0Nid));
        Assert.Contains(session.World!.WalkOrder.OfType<XPerson>(), p => p[PersonData.NeighborId] == sim0Nid);
        Assert.DoesNotContain(session.State().GetProperty("sims").EnumerateArray(), s => s.GetProperty("id").GetInt16() == 0);
    }

    [Fact]
    public void DeletedNeighborSlotReuse_CreateHouseholdAndMakeNewCharacter_DoesNotDeleteNewSim()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 1))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: true, neighborhood: 1, copyDownloads: false, 5, 2, 1);
        using var session = E2EHarness.StartServer(profile, house: 5);
        Assert.True(session.HandleJson("setConnectedNeighborhood", new { enabled = true, maxLiveSplitLots = 2 }).GetProperty("ok").GetBoolean());

        var nghBefore = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famiGuids = nghBefore.OfType(ResourceType.Parse("FAMI"))
            .Where(static c => c.Id != 0)
            .SelectMany(c => Family.Read(nghBefore, c).Members)
            .Select(static g => unchecked((int)g))
            .ToHashSet();

        // 1. Create an unhoused character via MakeNewCharacterPrimitive in the lowest free slot, then save(lot: true)
        // so DeleteUnhousedCharacters deletes it and records (freedNid, oldGuid) in RemovedNeighbors / globallyRemovedNeighbors.
        var actor = session.World!.WalkOrder.OfType<XPerson>().First();
        var prepFrame = new StackFrame(actor.Id, new short[8], [0, 1, 0]);
        var prepCtx = new TreeContext(session.World, actor, prepFrame);
        Span<byte> prepOperand = [0, 1, 2, 1, 0, 0, 0, 0];
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(prepCtx, prepOperand));
        var freedNid = prepFrame.StackObject;
        var oldGuid = unchecked((uint)session.World.Neighbors[freedNid].TypeRecordKey);

        Assert.True(session.HandleJson("save", new { slot = "slot-reuse-pre", lot = true }).GetProperty("ok").GetBoolean());
        Assert.False(session.World.Neighbors.ContainsKey(freedNid));
        var nghAfterDelete = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var nbrsAfterDelete = Neighbors.ReadRecoveringSupportedPrefix(nghAfterDelete, nghAfterDelete.OfType(ResourceType.Parse("NBRS")).First());
        Assert.DoesNotContain(nbrsAfterDelete.Entries, e => e.NeighborId == freedNid);

        // 2. Create a bin household (moveIn: false), deterministically reusing freedNid with a new GUID
        var createBinResp = session.HandleJson("createHousehold", new
        {
            familyName = "ReuseBin",
            moveIn = false,
            members = new[]
            {
                new { name = "BinReuse", bio = "Slot reuse test", code = "fa", skin = "lgt" }
            }
        });
        Assert.True(createBinResp.GetProperty("ok").GetBoolean(), createBinResp.ToString());
        var binFamilyId = (ushort)createBinResp.GetProperty("result").GetProperty("family").GetInt32();

        var nghAfterBin = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var binFamily = Family.Read(nghAfterBin, nghAfterBin.Find(ResourceType.Parse("FAMI"), binFamilyId)!);
        var binMemberGuid = binFamily.Members[0];
        Assert.NotEqual(oldGuid, binMemberGuid);
        var nbrsAfterBin = Neighbors.ReadRecoveringSupportedPrefix(nghAfterBin, nghAfterBin.OfType(ResourceType.Parse("NBRS")).First());
        var binNeighbor = Assert.Single(nbrsAfterBin.Entries, e => e.Guid == binMemberGuid);
        Assert.Equal(freedNid, binNeighbor.NeighborId);

        // Save again and focus another lot: the newly created Sim must NOT be deleted from NBRS or live world.Neighbors
        Assert.True(session.HandleJson("save", new { slot = "slot-reuse-bin", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = (short)2 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("save", new { slot = "slot-reuse-bin2", lot = true }).GetProperty("ok").GetBoolean());

        var nghAfterBinSave = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var nbrsAfterBinSave = Neighbors.ReadRecoveringSupportedPrefix(nghAfterBinSave, nghAfterBinSave.OfType(ResourceType.Parse("NBRS")).First());
        Assert.Contains(nbrsAfterBinSave.Entries, e => e.NeighborId == freedNid && e.Guid == binMemberGuid);

        // 3. Test MakeNewCharacterPrimitive reusing a deleted slot across two live lots
        Assert.True(session.HandleJson("focusLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        var actor5 = session.World!.WalkOrder.OfType<XPerson>().First();
        var victimFrame = new StackFrame(actor5.Id, new short[8], [0, 1, 0]);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(new TreeContext(session.World, actor5, victimFrame), prepOperand));
        var victim2Nid = victimFrame.StackObject;
        var victim2Nbr = session.World.Neighbors[victim2Nid];
        Assert.True(session.World.DeleteCharacter(victim2Nbr, key => famiGuids.Contains(key)));

        var makeFrame = new StackFrame(actor5.Id, new short[8], [0, 1, 0]);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(new TreeContext(session.World, actor5, makeFrame), prepOperand));
        var createdNid = makeFrame.StackObject;
        Assert.Equal(victim2Nid, createdNid);
        var createdNbr = session.World.Neighbors[createdNid];
        createdNbr.PersonData[(int)PersonData.FamilyNumber] = (short)((session.World.FamilyBudget as FamilyBudget)?.FamilyChunkId ?? 1);
        var createdGuid = unchecked((uint)createdNbr.TypeRecordKey);
        session.World.SaveAllPersistentPersonData();

        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("save", new { slot = "slot-reuse-make", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(session.World!.Neighbors.TryGetValue(createdNid, out var liveOn1));
        Assert.Equal(unchecked((int)createdGuid), liveOn1.TypeRecordKey);

        var nghAfterMake = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var nbrsAfterMake = Neighbors.ReadRecoveringSupportedPrefix(nghAfterMake, nghAfterMake.OfType(ResourceType.Parse("NBRS")).First());
        Assert.Contains(nbrsAfterMake.Entries, e => e.NeighborId == createdNid && e.Guid == createdGuid);

        // 4. Test createHousehold(moveIn: true) on an open unoccupied lot (House01) after deleting an unhoused character
        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());
        var createMoveInResp = session.HandleJson("createHousehold", new
        {
            familyName = "ReuseMoveIn",
            moveIn = true,
            house = (short)1,
            members = new[]
            {
                new { name = "MovedInSim", bio = "Move-in slot reuse", code = "ma", skin = "lgt" }
            }
        });
        Assert.True(createMoveInResp.GetProperty("ok").GetBoolean(), createMoveInResp.ToString());
        Assert.True(session.HandleJson("save", new { slot = "slot-reuse-movein", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        Assert.True(session.HandleJson("focusLot", new { house = (short)1 }).GetProperty("ok").GetBoolean());

        var nghFinal = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var moveInFamilyId = (ushort)createMoveInResp.GetProperty("result").GetProperty("family").GetInt32();
        var moveInFamily = Family.Read(nghFinal, nghFinal.Find(ResourceType.Parse("FAMI"), moveInFamilyId)!);
        var moveInGuid = moveInFamily.Members[0];
        var nbrsFinal = Neighbors.ReadRecoveringSupportedPrefix(nghFinal, nghFinal.OfType(ResourceType.Parse("NBRS")).First());
        Assert.Contains(nbrsFinal.Entries, e => e.Guid == moveInGuid);
        Assert.Contains(session.World!.Neighbors.Values, n => unchecked((uint)n.TypeRecordKey) == moveInGuid);
    }
}
