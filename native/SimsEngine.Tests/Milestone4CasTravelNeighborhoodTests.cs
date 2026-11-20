using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Milestone4CasTravelNeighborhoodTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static (string TempRoot, string Profile, string UserData, string Session) CreateIsolatedProfile(
        string gameDir, bool copyCharacters = true, params short[] houses)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-m4-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
        var userData = Path.Combine(profile, "UserData");
        Directory.CreateDirectory(Path.Combine(userData, "Houses"));
        var charsDir = Path.Combine(userData, "Characters");
        Directory.CreateDirectory(charsDir);
        if (copyCharacters)
        {
            foreach (var charFile in Directory.EnumerateFiles(Path.Combine(gameDir, "UserData", "Characters")))
                File.Copy(charFile, Path.Combine(charsDir, Path.GetFileName(charFile)));
        }

        File.Copy(Path.Combine(gameDir, "UserData", "Neighborhood.iff"), Path.Combine(userData, "Neighborhood.iff"));
        var zoningPath = Path.Combine(gameDir, "UserData", "LotZoning.iff");
        if (File.Exists(zoningPath))
            File.Copy(zoningPath, Path.Combine(userData, "LotZoning.iff"));
        var locationsPath = Path.Combine(gameDir, "UserData", "LotLocations.iff");
        if (File.Exists(locationsPath))
            File.Copy(locationsPath, Path.Combine(userData, "LotLocations.iff"));

        foreach (var house in houses)
        {
            var src = Path.Combine(gameDir, "UserData", "Houses", $"House{house:D2}.iff");
            if (File.Exists(src))
                File.Copy(src, Path.Combine(userData, "Houses", $"House{house:D2}.iff"));
        }

        var session = Path.Combine(tempRoot, "session");
        Directory.CreateDirectory(session);
        return (tempRoot, profile, userData, session);
    }

    private static JsonElement[] DrainEvents(MemoryStream outputStream, StreamWriter writer)
    {
        writer.Flush();
        var text = Encoding.UTF8.GetString(outputStream.GetBuffer(), 0, (int)outputStream.Length);
        outputStream.SetLength(0);
        outputStream.Position = 0;
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
    }

    private static JsonElement ResponseFor(JsonElement[] events, string id) =>
        events.Single(e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id);

    private static int ReadFamilyVacationField(string userData, ushort familyChunkId)
    {
        var ngh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
        var chunk = ngh.Find(ResourceType.Parse("FAMI"), familyChunkId)!;
        return Family.Read(ngh, chunk).Field0x13C;
    }

    [Fact]
    public void VacationTravel_ReopenFromNeighborhood_OpenLotAndLoad_RestoresSuspendedHomeAndSyncsOnReturn()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5, 40);
        try
        {
            Neighborhoods.Current = 1;
            var house05Path = Path.Combine(userData, "Houses", "House05.iff");
            var house40Path = Path.Combine(userData, "Houses", "House40.iff");
            if (!File.Exists(house40Path))
                File.Copy(house05Path, house40Path);

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            var homeWorld = server.World!;
            var householdPeople = homeWorld.WalkOrder.OfType<XPerson>()
                .Where(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0)
                .ToArray();
            Assert.True(householdPeople.Length >= 2);
            var traveller = householdPeople[0];
            var companion = householdPeople[1];
            var travellerGuid = traveller.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];
            var companionNeighborId = companion[PersonData.NeighborId];
            var familyChunkId = ((FamilyBudget)homeWorld.FamilyBudget!).FamilyChunkId!.Value;

            // Leave for Vacation 40.
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            var vacWorld = server.World!;
            var vacTraveller = vacWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
            vacTraveller[PersonData.MechanicalTricksSkill] = 654;
            vacTraveller.Relationships.Set(companionNeighborId, 0, 88);
            var vacTravellerNeighbor = vacWorld.Neighbors[vacTraveller[PersonData.NeighborId]];
            vacTravellerNeighbor.Relationships.Set(companionNeighborId, 0, 88);
            vacWorld.MarkRelationshipsChanged(vacTravellerNeighbor, vacWorld.Neighbors[companionNeighborId]);
            vacWorld.FamilyBudget!.AlterFamilyBudget(0, 250);
            var expectedBudget = vacWorld.FamilyBudget.ReadFamilyBudget();
            vacWorld.NeighborInventory(vacTraveller[PersonData.NeighborId]).Add(7, 0x12345678, 3);

            // Commit House40.iff + Field0x13C=40, then close without saving in memory.
            Assert.True(server.Handle("""{"id":"saveVac","op":"save","slot":"vac-canonical","lot":true}"""));
            var saveResp = ResponseFor(DrainEvents(outputStream, writer), "saveVac");
            Assert.True(saveResp.GetProperty("ok").GetBoolean());
            Assert.Equal(40, ReadFamilyVacationField(userData, familyChunkId));

            Assert.True(server.Handle("""{"id":"closeVac","op":"closeLot","save":false}"""));
            DrainEvents(outputStream, writer);

            // Reopen House05 via openLot -> redirects to House40 and restores suspendedHomeLot!
            Assert.True(server.Handle("""{"id":"open5","op":"openLot","house":5}"""));
            var openEvents = DrainEvents(outputStream, writer);
            var openResp = ResponseFor(openEvents, "open5");
            Assert.True(openResp.GetProperty("ok").GetBoolean());
            var openResult = openResp.GetProperty("result");
            Assert.Equal(40, openResult.GetProperty("house").GetInt32());
            Assert.Equal(5, openResult.GetProperty("homeHouse").GetInt32());
            Assert.True(openResult.GetProperty("away").GetBoolean());

            // Also verify SimulationServer.Load(house: 5) restores homeHouse = 5 when redirected to 40.
            using var loadStream = new MemoryStream();
            using var loadWriter = new StreamWriter(loadStream, new UTF8Encoding(false)) { AutoFlush = true };
            var reloadedServer = SimulationServer.Load(gameDir, session, profile, 5, loadWriter);
            Assert.NotNull(reloadedServer.World);
            Assert.True(reloadedServer.Handle("""{"id":"s1","op":"state"}"""));
            var reloadState = ResponseFor(DrainEvents(loadStream, loadWriter), "s1").GetProperty("result");
            Assert.Equal(40, reloadState.GetProperty("house").GetInt32());
            Assert.Equal(5, reloadState.GetProperty("homeHouse").GetInt32());

            // Return home from reopened openLot session: must sync skills, relationships, budget, inventory, and clear Field0x13C on save.
            server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
            server.BatchRun(1);
            var returnEvents = DrainEvents(outputStream, writer);
            var lotChanged = returnEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.Equal(5, lotChanged.GetProperty("house").GetInt32());
            Assert.False(lotChanged.GetProperty("away").GetBoolean());

            var returnedWorld = server.World!;
            var returnedTraveller = returnedWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
            Assert.Equal((short)654, returnedTraveller[PersonData.MechanicalTricksSkill]);
            Assert.Equal(88, returnedTraveller.Relationships.Get(companionNeighborId, 0));
            Assert.Equal(expectedBudget, returnedWorld.FamilyBudget!.ReadFamilyBudget());
            Assert.Equal(3, returnedWorld.NeighborInventory(returnedTraveller[PersonData.NeighborId]).Entries
                .Where(e => e.Key1 == 7 && e.Key2 == 0x12345678).Sum(e => e.Count));

            Assert.True(server.Handle("""{"id":"saveHome","op":"save","slot":"home-after","lot":true}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "saveHome").GetProperty("ok").GetBoolean());
            Assert.Equal(0, ReadFamilyVacationField(userData, familyChunkId));
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VacationTravel_UnsavedExit_ReconcilesField0x13CAtomically_AndSupportsHouse49AndMagicTown90()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5, 49);
        try
        {
            Neighborhoods.Current = 1;
            var house05Path = Path.Combine(userData, "Houses", "House05.iff");
            var house49Path = Path.Combine(userData, "Houses", "House49.iff");
            if (!File.Exists(house49Path))
                File.Copy(house05Path, house49Path);

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            DrainEvents(outputStream, writer);

            var homeWorld = server.World!;
            var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            var travellerGuid = traveller.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];
            var familyChunkId = ((FamilyBudget)homeWorld.FamilyBudget!).FamilyChunkId!.Value;

            // (a) Depart to House49 without saving -> Field0x13C must stay 0 on disk, even if inventory triggers PersistNeighborhoodStateImmediately.
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(49, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);
            server.World!.NeighborInventory(traveller[PersonData.NeighborId]).Add(7, 0x11112222, 1);
            Assert.Equal(0, ReadFamilyVacationField(userData, familyChunkId));

            // Close without saving -> Field0x13C remains 0; reopening house 5 opens house 5 (not 49), and Evict is not blocked.
            Assert.True(server.Handle("""{"id":"closeUnsaved","op":"closeLot","save":false}"""));
            DrainEvents(outputStream, writer);
            Assert.Equal(0, ReadFamilyVacationField(userData, familyChunkId));

            Assert.True(server.Handle("""{"id":"reopen5","op":"openLot","house":5}"""));
            var reopenResp = ResponseFor(DrainEvents(outputStream, writer), "reopen5");
            Assert.Equal(5, reopenResp.GetProperty("result").GetProperty("house").GetInt32());

            // (b) Now travel to 49 and save (lot:true) -> commits Field0x13C=49 atomically.
            var w2 = server.World!;
            w2.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(49, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"save49","op":"save","slot":"vac49","lot":true}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "save49").GetProperty("ok").GetBoolean());
            Assert.Equal(49, ReadFamilyVacationField(userData, familyChunkId));

            // Return home without saving -> quit -> disk Field0x13C stays 49 matching House49.iff!
            server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);
            Assert.False(server.Handle("""{"id":"q1","op":"quit"}"""));
            DrainEvents(outputStream, writer);
            Assert.Equal(49, ReadFamilyVacationField(userData, familyChunkId));

            // (c) Verify Magic Town residential House90 also participates in vacation redirect when occupied and Field0x13C==49.
            var house90Path = Path.Combine(userData, "Houses", "House90.iff");
            File.Copy(house05Path, house90Path, overwrite: true);
            if (File.Exists(house49Path + ".runtime.json"))
                File.Delete(house49Path + ".runtime.json");
            var nghPath = Path.Combine(userData, "Neighborhood.iff");
            var ngh = IffFile.Open(nghPath);
            var famChunk = ngh.Find(ResourceType.Parse("FAMI"), familyChunkId)!;
            var fam = Family.Read(ngh, famChunk).WithHouseNumber(90);
            ngh = ngh.WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), familyChunkId, fam.Write(familyChunkId));
            File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());

            using var mtStream = new MemoryStream();
            using var mtWriter = new StreamWriter(mtStream, new UTF8Encoding(false)) { AutoFlush = true };
            var mtServer = SimulationServer.Load(gameDir, session, profile, 90, mtWriter);
            Assert.NotNull(mtServer.World);
            Assert.True(mtServer.Handle("""{"id":"mtState","op":"state"}"""));
            var mtState = ResponseFor(DrainEvents(mtStream, mtWriter), "mtState").GetProperty("result");
            Assert.Equal(49, mtState.GetProperty("house").GetInt32());
            Assert.Equal(90, mtState.GetProperty("homeHouse").GetInt32());
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ReturnHome_HydratesUpdatedNeighborhoodTattOverPreDepartureLotTatt()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var house21 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House21.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(house21) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5, 21);
        try
        {
            Neighborhoods.Current = 1;
            var house05Path = Path.Combine(userData, "Houses", "House05.iff");
            var h5Iff = IffFile.Open(house05Path);
            var h5World = WorldBootstrap.Load(gameDir, house05Path, 1, () => false, 5, userDataPath: userData).World;
            var sharedObj = h5World.Objects.Values.FirstOrDefault(o => (o.TypeRecord?.Key ?? 0) != 0 && o.TypeAttributes.Length > 0);
            if (sharedObj is null) return;

            var targetGuid = unchecked((uint)sharedObj.TypeRecord!.Key);
            var initialTatt = TypeAttributes.Write(new Dictionary<uint, short[]> { [targetGuid] = [11] }, 0x49);
            h5Iff = h5Iff.WithAddedOrReplacedChunk(ResourceType.Parse("TATT"), 1, initialTatt);
            File.WriteAllBytes(house05Path, h5Iff.Bytes.ToArray());

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            DrainEvents(outputStream, writer);

            var homeObj = server.World!.Objects.Values.First(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == targetGuid);
            homeObj.TypeAttributes[0] = 22;

            var traveller = server.World.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            // Simulate an away-lot TATT update written into Neighborhood.iff.
            var nghPath = Path.Combine(userData, "Neighborhood.iff");
            var ngh = IffFile.Open(nghPath);
            var updatedTatt = TypeAttributes.Write(new Dictionary<uint, short[]> { [targetGuid] = [99] }, 0x49);
            ngh = ngh.WithAddedOrReplacedChunk(ResourceType.Parse("TATT"), 1, updatedTatt);
            File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());
            // Reload server state via save/load or trigger PersistNeighborhoodStateImmediately: instead update via away lot object if present, or reopen away save.
            var awayShared = server.World!.Objects.Values.FirstOrDefault(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == targetGuid);
            if (awayShared is not null)
            {
                awayShared.TypeAttributes[0] = 99;
            }
            else
            {
                // Ensure Neighborhood.iff in memory has TATT=99 by reopening house 21 or testing via a dummy object with same GUID.
                var dummy = new XObject(9999, PrimitiveTestWorld.Definition(0)) { TypeRecord = new TypeRecord(unchecked((int)targetGuid)) };
                dummy.TypeAttributes = [99];
                server.World.Add(dummy);
            }

            server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, traveller.TypeRecord.Key, traveller[PersonData.FamilyNumber]);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            var returnedObj = server.World!.Objects.Values.First(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == targetGuid);
            Assert.Equal((short)99, returnedObj.TypeAttributes[0]);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AwayTravel_SoloOrPartialHousehold_PersistsRestrictedActivePersonKeysAcrossSaveAndLoad()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var house21 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House21.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(house21) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5, 21);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            DrainEvents(outputStream, writer);

            var homeWorld = server.World!;
            var members = homeWorld.WalkOrder.OfType<XPerson>()
                .Where(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0)
                .ToArray();
            Assert.True(members.Length >= 2);
            var soloTraveller = members[0];
            var soloGuid = soloTraveller.TypeRecord!.Key;
            var stayHomeGuid = members[1].TypeRecord!.Key;
            var familyNumber = soloTraveller[PersonData.FamilyNumber];

            // Solo trip to Downtown 21.
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, soloGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            var awayWorld = server.World!;
            Assert.Equal([soloGuid], awayWorld.ActivePersonRecordKeys);
            Assert.DoesNotContain(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayHomeGuid);

            Assert.True(server.Handle("""{"id":"saveSolo","op":"save","slot":"solo-away","lot":false}"""));
            var saveResp = ResponseFor(DrainEvents(outputStream, writer), "saveSolo");
            var envPath = saveResp.GetProperty("result").GetProperty("path").GetString()!;
            using (var doc = JsonDocument.Parse(File.ReadAllText(envPath)))
            {
                var restricted = doc.RootElement.GetProperty("restrictedActivePersonKeys").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                Assert.Equal([soloGuid], restricted);
            }

            Assert.True(server.Handle("""{"id":"closeSolo","op":"closeLot","save":false}"""));
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"loadSolo","op":"load","slot":"solo-away"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "loadSolo").GetProperty("ok").GetBoolean());
            Assert.Equal([soloGuid], server.World!.ActivePersonRecordKeys);
            Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayHomeGuid);

            // Backward compatibility: legacy v13 envelope without restrictedActivePersonKeys still loads cleanly.
            var envJson = JsonDocument.Parse(File.ReadAllText(envPath)).RootElement;
            var legacyObj = new Dictionary<string, object?>();
            foreach (var prop in envJson.EnumerateObject())
            {
                if (prop.Name == "restrictedActivePersonKeys") continue;
                if (prop.Name == "runtime")
                {
                    var rtDict = new Dictionary<string, object?>();
                    foreach (var rtProp in prop.Value.EnumerateObject())
                    {
                        if (rtProp.Name == "restrictedActivePersonKeys") continue;
                        rtDict[rtProp.Name] = rtProp.Name == "version" ? 13 : rtProp.Value.Clone();
                    }
                    legacyObj["runtime"] = rtDict;
                }
                else
                {
                    legacyObj[prop.Name] = prop.Value.Clone();
                }
            }
            File.WriteAllBytes(envPath, JsonSerializer.SerializeToUtf8Bytes(legacyObj));

            Assert.True(server.Handle("""{"id":"loadLegacy","op":"load","slot":"solo-away"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "loadLegacy").GetProperty("ok").GetBoolean());
            Assert.Equal([soloGuid], server.World!.ActivePersonRecordKeys);

            // Returning home restores all family members.
            server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, soloGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);
            Assert.Contains(stayHomeGuid, server.World!.ActivePersonRecordKeys);
            Assert.Contains(soloGuid, server.World.ActivePersonRecordKeys);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TravelFilter_And_NeighborhoodCheats_PersistPerModeSlotsAndAcceptNessieAndCars()
    {
        var gameDir = LocateGameDirectory();
        var lotLocations = gameDir is null ? null : Path.Combine(gameDir, "UserData", "LotLocations.iff");
        if (gameDir is null || !File.Exists(lotLocations) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: false, 5);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            DrainEvents(outputStream, writer);

            // Default (omitted mode) saves to Unleashed slot 3.
            Assert.True(server.Handle("""{"id":"tfDefault","op":"travelFilter","filter":32}"""));
            var tfDefault = ResponseFor(DrainEvents(outputStream, writer), "tfDefault");
            Assert.True(tfDefault.GetProperty("ok").GetBoolean());
            Assert.Equal(3, tfDefault.GetProperty("result").GetProperty("slot").GetInt32());

            // Explicit modes 2->1, 3->2, 5->4, 7->5.
            foreach (var (mode, filter, expectedSlot) in new[] { (2, 16, 1), (3, 64, 2), (5, 0x200, 4), (7, 0x1000, 5) })
            {
                Assert.True(server.Handle(JsonSerializer.Serialize(new { id = $"tf_{mode}", op = "travelFilter", mode, filter })));
                var resp = ResponseFor(DrainEvents(outputStream, writer), $"tf_{mode}");
                Assert.True(resp.GetProperty("ok").GetBoolean());
                Assert.Equal(expectedSlot, resp.GetProperty("result").GetProperty("slot").GetInt32());

                Assert.True(server.Handle(JsonSerializer.Serialize(new { id = $"ngh_{mode}", op = "neighborhood", mode })));
                var nghResp = ResponseFor(DrainEvents(outputStream, writer), $"ngh_{mode}");
                Assert.True(nghResp.GetProperty("ok").GetBoolean());
                Assert.Equal(filter, nghResp.GetProperty("result").GetProperty("travelFilter").GetInt32());
            }

            // Cheats: nessie + cars.
            Assert.True(server.Handle("""{"id":"cNessie","op":"cheat","code":"nessie"}"""));
            var nessieEvents = DrainEvents(outputStream, writer);
            Assert.Contains(nessieEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "neighborhoodCheat" && e.GetProperty("code").GetString() == "nessie");
            Assert.True(ResponseFor(nessieEvents, "cNessie").GetProperty("ok").GetBoolean());

            Assert.True(server.Handle("""{"id":"cCars","op":"cheat","code":"cars 2 3"}"""));
            var carsEvents = DrainEvents(outputStream, writer);
            Assert.Contains(carsEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "neighborhoodCheat" && e.GetProperty("code").GetString() == "cars" && e.GetProperty("speed").GetInt32() == 2 && e.GetProperty("count").GetInt32() == 3);
            Assert.True(ResponseFor(carsEvents, "cCars").GetProperty("ok").GetBoolean());
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void OriginalNeighborhoodOverlay_MagicTownRollover_And_ExpansionTravelFilters_MatchParity()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "sims-m4-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var bmpBytes = BitmapCodec.Write(4, 4, new byte[4 * 4 * 4]);
            var closedPath = Path.Combine(tempDir, "lot-closed.bmp");
            var openPath = Path.Combine(tempDir, "lot-open.bmp");
            File.WriteAllBytes(closedPath, bmpBytes);
            File.WriteAllBytes(openPath, bmpBytes);

            static string WriteManifest(string dir, int mode, int travelFilter, object[] entries)
            {
                var path = Path.Combine(dir, $"manifest-{mode}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    mode,
                    backgroundResource = mode == 5 ? "UIGraphics/Studiotown/DScreen.bmp" : mode == 7 ? "UIGraphics/Magicland/DScreen.bmp" : "UIGraphics/Downtown/DScreen.bmp",
                    canvasWidth = 800,
                    canvasHeight = 600,
                    scale = 1f,
                    pulsate = false,
                    frameDuration = 15,
                    travelFilter,
                    animationLayers = Array.Empty<object>(),
                    entries
                }));
                return path;
            }

            var content = new GameContent(gameDir);
            var device = new UIDevice();

            // 1. Magic Town (mode 7): rollover formats MagiCoins for lots 90-92 + 7-button filter toolbar.
            var mtManifest = WriteManifest(tempDir, 7, 0x1000,
            [
                new
                {
                    house = 91, x = 100f, y = 100f,
                    closedImagePath = closedPath, openImagePath = openPath,
                    closedImageWidth = 4, closedImageHeight = 4, openImageWidth = 4, openImageHeight = 4,
                    thumbWidth = 4, thumbHeight = 4, xOff = 0, baseYOff = 0, addYOff = 0,
                    occupied = false, residential = true, availableForCreation = true, price = 15000,
                    community = false, filterFlags = 0, popupName = "Spooky Hollow", popupDescription = "Creepy",
                    lotHasHouse = true
                },
                new
                {
                    house = 93, x = 200f, y = 200f,
                    closedImagePath = closedPath, openImagePath = openPath,
                    closedImageWidth = 4, closedImageHeight = 4, openImageWidth = 4, openImageHeight = 4,
                    thumbWidth = 4, thumbHeight = 4, xOff = 0, baseYOff = 0, addYOff = 0,
                    occupied = false, residential = false, availableForCreation = false, price = 0,
                    community = true, filterFlags = 0x1000 | 0x800, popupName = "Carnival", popupDescription = "Rides",
                    lotHasHouse = true
                }
            ]);

            var mtOverlay = new OriginalNeighborhoodOverlay(content, device, mtManifest, tempDir);
            Assert.Contains("MagiCoins: 150", mtOverlay.RolloverText(91), StringComparison.Ordinal);

            mtOverlay.SetMovingInFamily(new OriginalFamilyPickerOverlay.Family(1, "Spell", 20000, 20000, 0, [], null, null, MagiCoins: 50));
            Assert.Contains("MagiCoins: 150", mtOverlay.RolloverText(91), StringComparison.Ordinal);
            Assert.Contains("50 MagiCoins", mtOverlay.RolloverText(91), StringComparison.Ordinal);

            mtOverlay.SetMovingInFamily(new OriginalFamilyPickerOverlay.Family(1, "RichSpell", 20000, 20000, 0, [], null, null, MagiCoins: 200));
            Assert.Contains("enough Simoleons and MagiCoins", mtOverlay.RolloverText(91), StringComparison.Ordinal);

            mtOverlay.SetMovingInFamily(null);
            mtOverlay.EnableTravelFilter();
            Assert.Equal(0x1000, mtOverlay.TravelFilterMode);
            // Click 7th button (Magic = 0x800 at x=525..575, y=20..72)
            Assert.True(mtOverlay.ClickTravelFilter(new CGPoint(550, 40), new CGSize(800, 600)));
            Assert.Equal(0x800, mtOverlay.TravelFilterMode);

            // 2. Studio Town (mode 5): 9-button filter toolbar + cars cheat.
            var stManifest = WriteManifest(tempDir, 5, 0x400,
            [
                new
                {
                    house = 81, x = 200f, y = 200f,
                    closedImagePath = closedPath, openImagePath = openPath,
                    closedImageWidth = 4, closedImageHeight = 4, openImageWidth = 4, openImageHeight = 4,
                    thumbWidth = 4, thumbHeight = 4, xOff = 0, baseYOff = 0, addYOff = 0,
                    occupied = false, residential = false, availableForCreation = false, price = 0,
                    community = true, filterFlags = 0x80 | 0x400, popupName = "Spa", popupDescription = "Relax",
                    lotHasHouse = true
                }
            ]);
            var stOverlay = new OriginalNeighborhoodOverlay(content, device, stManifest, tempDir);
            stOverlay.EnableTravelFilter();
            Assert.Equal(0x400, stOverlay.TravelFilterMode);
            // Click 6th button (Spa = 0x80 at x=425..475)
            Assert.True(stOverlay.ClickTravelFilter(new CGPoint(450, 40), new CGSize(800, 600)));
            Assert.Equal(0x80, stOverlay.TravelFilterMode);
            Assert.True(stOverlay.TriggerCars(speed: 2, count: 2));
            Assert.NotEmpty(stOverlay.DrawItems(new CGSize(800, 600), time: 1.0));

            // 3. Downtown (mode 2): travel filter + Nessie trigger.
            var dtManifest = WriteManifest(tempDir, 2, 16,
            [
                new
                {
                    house = 21, x = 200f, y = 200f,
                    closedImagePath = closedPath, openImagePath = openPath,
                    closedImageWidth = 4, closedImageHeight = 4, openImageWidth = 4, openImageHeight = 4,
                    thumbWidth = 4, thumbHeight = 4, xOff = 0, baseYOff = 0, addYOff = 0,
                    occupied = false, residential = false, availableForCreation = false, price = 0,
                    community = true, filterFlags = 16 | 2, popupName = "Diner", popupDescription = "Eat",
                    lotHasHouse = true
                }
            ]);
            var dtOverlay = new OriginalNeighborhoodOverlay(content, device, dtManifest, tempDir);
            dtOverlay.EnableTravelFilter();
            Assert.Equal(16, dtOverlay.TravelFilterMode);
            Assert.True(dtOverlay.ClickTravelFilter(new CGPoint(450, 40), new CGSize(800, 600)));
            Assert.Equal(2, dtOverlay.TravelFilterMode);
            Assert.True(dtOverlay.TriggerNessie(time: 0.0));
            Assert.False(dtOverlay.TriggerNessie(time: 0.1));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void InLotPortraitRefresh_UpdatesBmpChunksAndStatePortrait_OnWardrobeAndMakeNewCharacter()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            var world = server.World!;
            var bella = Assert.IsType<XPerson>(world.ObjectById(18));
            var bellaGuid = unchecked((uint)bella.TypeRecord!.Key);
            var registry = world.ObjectRegistry!;
            var beforeEntry = registry.Find(bellaGuid)!;
            var beforeBmp2002 = beforeEntry.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();

            Assert.True(server.Handle("""{"id":"st0","op":"state"}"""));
            var st0 = ResponseFor(DrainEvents(outputStream, writer), "st0").GetProperty("result");
            var beforePortrait = st0.GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt32() == bella.Id)
                .GetProperty("portrait").GetProperty("file").GetString();

            // Cycle Bella's head via GenericSimsCall 0x1d (heads) or 0x18 (body) so head mesh changes and 0x20000000 fires.
            var headActions = WardrobeSuitService.GetActionList(world, bella, null, 0, headsList: true);
            Assert.NotNull(headActions);
            Assert.True(headActions!.Entries.Count > 1);
            var currentHead = bella.SavedCharacter?.Parts[4].Item1 ?? string.Empty;
            var alternateHeadIndex = Enumerable.Range(0, headActions.Entries.Count)
                .First(i => !headActions.Entries[i].First.Equals(currentHead, StringComparison.OrdinalIgnoreCase));

            var beginScope = InteractionBehaviorScope.Initialize();
            var scopeCtx = WardrobeSuitService.BeginPersonScope(world, bella, beginScope);
            var altAction = headActions.Entries[alternateHeadIndex];
            scopeCtx.SetString(0x03, $"{altAction.First},HEAD={altAction.First}");
            WardrobeSuitService.CommitContext(registry, beforeEntry, world.Language, scopeCtx, bella);
            world.DispatchNotice(0x20000000, 0);

            var noticeEvents = DrainEvents(outputStream, writer);
            Assert.Contains(noticeEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "state");

            var afterEntry = registry.Find(bellaGuid)!;
            var afterBmp2002 = afterEntry.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
            Assert.False(beforeBmp2002.SequenceEqual(afterBmp2002));

            Assert.True(server.Handle("""{"id":"st1","op":"state"}"""));
            var st1 = ResponseFor(DrainEvents(outputStream, writer), "st1").GetProperty("result");
            var afterPortrait = st1.GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt32() == bella.Id)
                .GetProperty("portrait").GetProperty("file").GetString();
            Assert.NotEqual(beforePortrait, afterPortrait);

            // Save writes regenerated BMP_ 2002 to UserData/Characters/UserXXXXX.iff on disk.
            Assert.True(server.Handle("""{"id":"sv1","op":"save","slot":"portrait-save","lot":true}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "sv1").GetProperty("ok").GetBoolean());
            var diskCharPath = Path.Combine(userData, "Characters", Path.GetFileName(afterEntry.Source));
            var diskIff = IffFile.Open(diskCharPath);
            Assert.True(afterBmp2002.SequenceEqual(diskIff.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray()));

            // Also verify MakeNewCharacterPrimitive (0x13) triggers portrait generation on tick.
            var existingGuids = registry.Entries.Where(e => e.Scan == ObjectScan.People).Select(e => e.Guid).ToHashSet();
            var makeFrame = new StackFrame(bella.Id, new short[8], [0, 1, 0]);
            var makeCtx = new TreeContext(world, bella, makeFrame);
            Span<byte> makeOperand = [0, 1, 2, 1, 0, 0, 0, 0]; // mode 1 = adult female
            var makeResult = MakeNewCharacterPrimitive.Execute(makeCtx, makeOperand);
            Assert.Equal(PrimitiveResult.True, makeResult);
            var createdNeighbor = world.Neighbors[makeFrame.StackObject];
            var createdGuid = unchecked((uint)createdNeighbor.TypeRecordKey);
            Assert.DoesNotContain(createdGuid, existingGuids);

            server.BatchRun(1);
            DrainEvents(outputStream, writer);
            var createdEntry = registry.Find(createdGuid);
            Assert.NotNull(createdEntry);
            Assert.NotNull(createdEntry!.File.Find(ResourceType.Parse("BMP_"), 2002));
            Assert.NotNull(createdEntry.File.Find(ResourceType.Parse("BMP_"), 2003));
            Assert.NotNull(createdEntry.File.Find(ResourceType.Parse("BMP_"), 2004));
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HouseholdCreationOverlay_EditSimClearsStaleCatalog_And_GatesDoneOnAdult()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var content = new GameContent(gameDir);
        var device = new UIDevice();
        var canvas = new CGSize(800, 600);
        static void NoopSubmit(HouseholdCreationRequest _) { }
        static CGPoint ControlCenter(OriginalHouseholdCreationOverlay ov, CGSize sz, string label)
        {
            var match = ov.Controls(sz).First(c => c.Label.Contains(label, StringComparison.OrdinalIgnoreCase));
            return new CGPoint(match.Rect.MidX, match.Rect.MidY);
        }

        // Verify child-only validation is rejected by HouseholdCreationRequest.Validate and Done stays gated on Family page.
        var childOnlyReq = new HouseholdCreationRequest(
            "KidOnly",
            [new HouseholdCreationRequest.Member("Kid", "", "mcchd", "lgt", [500, 500, 500, 500, 500])]);
        Assert.Throws<ContentError>(() => childOnlyReq.Validate());

        var childCatalog = new HouseholdAppearanceCatalog(
            "m", "child", "med",
            [new HouseholdAppearanceCatalog.Choice("c001mc_01", "c001mcmed_01", "fit")],
            [new HouseholdAppearanceCatalog.Choice("b001mcchd_01", "b001mcchdmed_01", "fit")]);
        var femaleAdultCatalog = new HouseholdAppearanceCatalog(
            "f", "adult", "med",
            [
                new HouseholdAppearanceCatalog.Choice("c001fa_01", "c001famed_01", "fit"),
                new HouseholdAppearanceCatalog.Choice("c002fa_01", "c002famed_01", "fit")
            ],
            [
                new HouseholdAppearanceCatalog.Choice("b001fafit_01", "b001fafitmed_01", "fit"),
                new HouseholdAppearanceCatalog.Choice("b002fafit_01", "b002fafitmed_01", "fit")
            ]);
        var maleAdultCatalog = new HouseholdAppearanceCatalog(
            "m", "adult", "drk",
            [
                new HouseholdAppearanceCatalog.Choice("c001ma_01", "c001madrk_01", "fit"),
                new HouseholdAppearanceCatalog.Choice("c002ma_01", "c002madrk_01", "fit")
            ],
            [
                new HouseholdAppearanceCatalog.Choice("b001mafit_01", "b001mafitdrk_01", "fit"),
                new HouseholdAppearanceCatalog.Choice("b002mafit_01", "b002mafitdrk_01", "fit")
            ]);

        var childOverlay = new OriginalHouseholdCreationOverlay(content, device);
        childOverlay.Click(new CGPoint(300, 60), canvas, NoopSubmit); // Family name field
        foreach (var ch in "KidOnly") childOverlay.Key(ch.ToString());
        childOverlay.Click(ControlCenter(childOverlay, canvas, "Add"), canvas, NoopSubmit);
        childOverlay.Click(new CGPoint(300, 64), canvas, NoopSubmit); // Sim name field
        foreach (var ch in "Kid") childOverlay.Key(ch.ToString());
        childOverlay.Click(ControlCenter(childOverlay, canvas, "Child"), canvas, NoopSubmit);
        childOverlay.SetAppearanceCatalog(childCatalog);
        childOverlay.Click(new CGPoint(260, 140), canvas, NoopSubmit); // Spend 1 personality point
        childOverlay.Click(ControlCenter(childOverlay, canvas, "Done"), canvas, NoopSubmit);
        Assert.True(childOverlay.ShowsFamilyPage);
        Assert.False(childOverlay.SubmitFamilyIfReady(NoopSubmit));

        // Verify Edit Sim clears stale _appearanceCatalog when editing a member of another gender/skin.
        var overlay = new OriginalHouseholdCreationOverlay(content, device);
        overlay.Click(new CGPoint(300, 60), canvas, NoopSubmit);
        foreach (var ch in "CatalogRace") overlay.Key(ch.ToString());

        // Add female adult Alice (member 0).
        overlay.Click(ControlCenter(overlay, canvas, "Add"), canvas, NoopSubmit);
        overlay.Click(new CGPoint(300, 64), canvas, NoopSubmit);
        foreach (var ch in "Alice") overlay.Key(ch.ToString());
        overlay.Click(ControlCenter(overlay, canvas, "Female"), canvas, NoopSubmit);
        overlay.SetAppearanceCatalog(femaleAdultCatalog);
        overlay.Click(new CGPoint(260, 140), canvas, NoopSubmit);
        overlay.Click(ControlCenter(overlay, canvas, "Done"), canvas, NoopSubmit);
        Assert.True(overlay.ShowsFamilyPage);

        // Add male adult dark Bob (member 1), leaving _appearanceCatalog = maleAdultCatalog.
        overlay.Click(ControlCenter(overlay, canvas, "Add"), canvas, NoopSubmit);
        overlay.Click(new CGPoint(300, 64), canvas, NoopSubmit);
        foreach (var ch in "Bob") overlay.Key(ch.ToString());
        overlay.Click(ControlCenter(overlay, canvas, "Dark"), canvas, NoopSubmit);
        overlay.SetAppearanceCatalog(maleAdultCatalog);
        overlay.Click(new CGPoint(260, 140), canvas, NoopSubmit);
        overlay.Click(ControlCenter(overlay, canvas, "Done"), canvas, NoopSubmit);
        Assert.True(overlay.ShowsFamilyPage);

        // Select Alice (slot 0) and click Edit Sim.
        var slot0 = OriginalHouseholdCreationOverlay.MemberSlot(0);
        overlay.Click(new CGPoint(slot0.MidX, slot0.MidY), canvas, NoopSubmit);
        string? requestedGender = null;
        overlay.Click(ControlCenter(overlay, canvas, "Edit"), canvas, NoopSubmit, appearanceCatalog: (g, _, _) => requestedGender = g);
        Assert.Equal("f", requestedGender);
        Assert.False(overlay.ShowsFamilyPage);

        // Before femaleAdultCatalog arrives, Next Head must NOT cycle into stale maleAdultCatalog!
        overlay.Click(ControlCenter(overlay, canvas, "Next Head"), canvas, NoopSubmit);
        Assert.Null(overlay.AppearancePreviewSelection());

        // Deliver femaleAdultCatalog, cycle Next Head to c002fa_01, finish Edit Sim, and submit family.
        overlay.SetAppearanceCatalog(femaleAdultCatalog);
        overlay.Click(ControlCenter(overlay, canvas, "Next Head"), canvas, NoopSubmit);
        overlay.Click(ControlCenter(overlay, canvas, "Done"), canvas, NoopSubmit);
        Assert.True(overlay.ShowsFamilyPage);

        HouseholdCreationRequest? submitted = null;
        Assert.True(overlay.SubmitFamilyIfReady(req => submitted = req));
        Assert.NotNull(submitted);
        Assert.Equal("c002fa_01", submitted!.Members[0].HeadChoice);
    }

    [Fact]
    public void RealExportedManifests_Modes2_3_5_7_EnableTravelFilters_RenderMarkers_ClassifyCommunity_AndPreserveMode7()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null
            || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff"))
            || !File.Exists(Path.Combine(gameDir, "UserData", "LotLocations.iff"))
            || !File.Exists(Path.Combine(gameDir, "UserData", "LotZoning.iff")))
        {
            return;
        }

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, _, session) = CreateIsolatedProfile(gameDir, copyCharacters: false);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            DrainEvents(outputStream, writer);

            var content = new GameContent(gameDir);
            var device = new UIDevice();
            var canvas = new CGSize(800, 600);
            static int CountMarkers(IReadOnlyList<OriginalNeighborhoodOverlay.DrawItem> items) =>
                items.Count(static item => MathF.Abs(item.UV.Z - (1f / 3f)) < 0.001f);

            // Modes 2 (Downtown), 3 (Vacation), 5 (Studio Town), 7 (Magic Town), and 4 (Unleashed).
            foreach (int mode in new[] { 2, 3, 5, 7, 4 })
            {
                Assert.True(server.Handle($$"""{"id":"ngh{{mode}}","op":"neighborhood","mode":{{mode}}}"""));
                var resp = ResponseFor(DrainEvents(outputStream, writer), $"ngh{mode}");
                Assert.True(resp.GetProperty("ok").GetBoolean());

                var manifestPath = Path.Combine(session, "neighborhood.json");
                Assert.True(File.Exists(manifestPath));
                using var manifestDoc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                var entries = manifestDoc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
                Assert.NotEmpty(entries);

                if (mode == 2)
                {
                    Assert.All(entries, e =>
                    {
                        var h = e.GetProperty("house").GetInt32();
                        Assert.InRange(h, 21, 30);
                        Assert.True(e.GetProperty("community").GetBoolean());
                    });
                }
                else if (mode == 3)
                {
                    Assert.All(entries, e =>
                    {
                        var h = e.GetProperty("house").GetInt32();
                        Assert.InRange(h, 40, 49);
                        Assert.True(e.GetProperty("community").GetBoolean());
                    });
                }
                else if (mode == 5)
                {
                    Assert.All(entries, e =>
                    {
                        var h = e.GetProperty("house").GetInt32();
                        Assert.InRange(h, 81, 89);
                        Assert.True(e.GetProperty("community").GetBoolean());
                    });
                }
                else if (mode == 7)
                {
                    foreach (var e in entries)
                    {
                        var h = e.GetProperty("house").GetInt32();
                        if (h is >= 90 and <= 92)
                        {
                            Assert.False(e.GetProperty("community").GetBoolean());
                            Assert.True(e.GetProperty("residential").GetBoolean());
                        }
                        else if (h is >= 93 and <= 98)
                        {
                            Assert.True(e.GetProperty("community").GetBoolean());
                            Assert.False(e.GetProperty("residential").GetBoolean());
                        }
                    }
                }
                else if (mode == 4)
                {
                    Assert.Contains(entries, e => e.GetProperty("community").GetBoolean());
                    Assert.Contains(entries, e => !e.GetProperty("community").GetBoolean() && e.GetProperty("residential").GetBoolean());
                }

                var overlay = new OriginalNeighborhoodOverlay(content, device, manifestPath, session);
                Assert.Equal(0, CountMarkers(overlay.DrawItems(canvas, time: 0.0)));
                overlay.EnableTravelFilter();
                Assert.NotEqual(0, overlay.AvailableTravelFilterMask);

                var (clickPoint, expectedMask) = mode switch
                {
                    2 => (new CGPoint(500, 40), 0x40),  // Recreation in Downtown
                    3 => (new CGPoint(500, 40), 0x40),  // Recreation in Vacation
                    5 => (new CGPoint(500, 40), 0x100), // Movie in Studio Town
                    7 => (new CGPoint(550, 40), 0x800), // Magic in Magic Town
                    _ => (new CGPoint(500, 40), 0x40),  // Recreation in Unleashed
                };
                Assert.True((overlay.AvailableTravelFilterMask & expectedMask) != 0, $"Mode {mode} missing expected mask 0x{expectedMask:X}");
                Assert.True(overlay.ClickTravelFilter(clickPoint, canvas));
                Assert.Equal(expectedMask, overlay.TravelFilterMode);

                var filteredDrawItems = overlay.DrawItems(canvas, time: 0.0);
                Assert.True(CountMarkers(filteredDrawItems) > 0, $"Expected >0 filter markers in mode {mode}");

                if (mode == 2)
                {
                    Assert.Equal(2, overlay.StateClass(21));
                    var text21 = overlay.RolloverText(21);
                    Assert.False(string.IsNullOrWhiteSpace(text21));
                }
                else if (mode == 3)
                {
                    Assert.Equal(2, overlay.StateClass(40));
                    var text40 = overlay.RolloverText(40);
                    Assert.False(string.IsNullOrWhiteSpace(text40));
                }
                else if (mode == 5)
                {
                    Assert.Equal(2, overlay.StateClass(81));
                    var text81 = overlay.RolloverText(81);
                    Assert.False(string.IsNullOrWhiteSpace(text81));
                }
                else if (mode == 7)
                {
                    Assert.Equal(1, overlay.StateClass(90));
                    Assert.Equal(2, overlay.StateClass(93));
                    Assert.Contains("Creepy Hollow", overlay.RolloverText(90), StringComparison.OrdinalIgnoreCase);
                }
                else if (mode == 4)
                {
                    var communityLot = entries.First(e => e.GetProperty("community").GetBoolean()).GetProperty("house").GetInt32();
                    var residentialLot = entries.First(e => e.GetProperty("residential").GetBoolean()).GetProperty("house").GetInt32();
                    Assert.Equal(2, overlay.StateClass(communityLot));
                    Assert.Equal(1, overlay.StateClass(residentialLot));
                    Assert.Contains("This is a Community lot. Please click here to visit.", overlay.RolloverText(communityLot), StringComparison.Ordinal);
                }
            }

            // Also verify OriginalNeighborhoodOverlay handles a raw manifest where NeighborhoodExport wrote community: false
            // (including House 49 in Vacation Mode 3, which formats STR# 134[21] when classified as Zoning.Community).
            Assert.True(server.Handle("""{"id":"ngh3raw","op":"neighborhood","mode":3}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "ngh3raw").GetProperty("ok").GetBoolean());
            var rawManifestPath = Path.Combine(session, "neighborhood.json");
            var rawRoot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(rawManifestPath))!;
            var rawEntries = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(rawRoot["entries"].GetRawText())!;
            foreach (var entry in rawEntries)
                entry["community"] = false;
            var template40 = rawEntries.First(e => ((JsonElement)e["house"]!).GetInt32() == 40);
            var synthetic49 = new Dictionary<string, object?>(template40)
            {
                ["house"] = 49,
                ["community"] = false,
                ["residential"] = false
            };
            rawEntries.Add(synthetic49);
            rawRoot["entries"] = JsonSerializer.SerializeToElement(rawEntries);
            var patchedRawPath = Path.Combine(session, "neighborhood-raw3.json");
            File.WriteAllBytes(patchedRawPath, JsonSerializer.SerializeToUtf8Bytes(rawRoot));

            var rawOverlay = new OriginalNeighborhoodOverlay(content, device, patchedRawPath, session);
            rawOverlay.EnableTravelFilter();
            Assert.NotEqual(0, rawOverlay.AvailableTravelFilterMask);
            Assert.Equal(2, rawOverlay.StateClass(40));
            Assert.Equal(2, rawOverlay.StateClass(49));
            Assert.Contains("This is a Community lot. Please click here to visit.", rawOverlay.RolloverText(49), StringComparison.Ordinal);
            Assert.True(rawOverlay.ClickTravelFilter(new CGPoint(500, 40), canvas));
            Assert.Equal(0x40, rawOverlay.TravelFilterMode);
            Assert.True(CountMarkers(rawOverlay.DrawItems(canvas, time: 0.0)) > 0);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NeighborhoodOverlay_NessieActivation_EmitsNessieSfx_And_ResolvesInSoundBank()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, _, session) = CreateIsolatedProfile(gameDir, copyCharacters: false);
        try
        {
            Neighborhoods.Current = 1;
            var audioTempDir = Path.Combine(tempRoot, "sound");
            using var bank = new SoundBank(gameDir, audioTempDir);
            var nessieRes = bank.Resolve("nessie_sfx");
            Assert.NotNull(nessieRes);
            Assert.False(nessieRes!.IsLoop);
            Assert.Contains("nessie_sfx", nessieRes.RelativePath, StringComparison.OrdinalIgnoreCase);

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            DrainEvents(outputStream, writer);

            var content = new GameContent(gameDir);
            var device = new UIDevice();
            var canvas = new CGSize(800, 600);

            // Mode 1 (Base/Unleashed neighborhood) supports Nessie and emits "nessie_sfx" on surfacing.
            Assert.True(server.Handle("""{"id":"n1","op":"neighborhood","mode":1}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "n1").GetProperty("ok").GetBoolean());
            var manifest1 = Path.Combine(session, "neighborhood.json");
            var overlay1 = new OriginalNeighborhoodOverlay(content, device, manifest1, session);
            var played = new List<string>();
            overlay1.PlaySound = played.Add;

            Assert.True(overlay1.TriggerNessie(time: 0.0));
            Assert.Equal(["nessie_sfx"], played);
            // Already active: must not re-trigger or replay SFX.
            Assert.False(overlay1.TriggerNessie(time: 0.1));
            Assert.Equal(["nessie_sfx"], played);

            // Advancing past Nessie swim completion clears active state so a subsequent surfacing emits SFX again.
            _ = overlay1.DrawItems(canvas, time: 100.0);
            Assert.True(overlay1.TriggerNessie(time: 101.0));
            Assert.Equal(["nessie_sfx", "nessie_sfx"], played);

            // Mode 5 (Studio Town) does not support Nessie and must not emit SFX.
            Assert.True(server.Handle("""{"id":"n5","op":"neighborhood","mode":5}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "n5").GetProperty("ok").GetBoolean());
            var overlay5 = new OriginalNeighborhoodOverlay(content, device, manifest1, session);
            var played5 = new List<string>();
            overlay5.PlaySound = played5.Add;
            Assert.False(overlay5.TriggerNessie(time: 0.0));
            Assert.Empty(played5);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
