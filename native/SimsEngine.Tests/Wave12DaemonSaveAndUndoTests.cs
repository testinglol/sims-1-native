using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Wave12DaemonSaveAndUndoTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static (string TempRoot, string Profile, string UserData, string Session) CreateIsolatedProfile(
        string gameDir, bool copyCharacters = true, params short[] houses)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave12-" + Guid.NewGuid().ToString("N"));
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
        foreach (var extra in new[] { "LotLocations.iff", "StreetNames.iff" })
        {
            var extraPath = Path.Combine(gameDir, "UserData", extra);
            if (File.Exists(extraPath))
                File.Copy(extraPath, Path.Combine(userData, extra));
        }

        foreach (var house in houses)
        {
            var src = Path.Combine(gameDir, "UserData", "Houses", $"House{house:D2}.iff");
            if (!File.Exists(src) && house is >= 40 and <= 48)
                src = Path.Combine(gameDir, "TemplateVacation", "Houses", $"House{house:D2}.iff");
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

    private static int EnsureAffordableVacantLot(string userData, short sourceHouse, short targetHouse)
    {
        var nghPath = Path.Combine(userData, "Neighborhood.iff");
        var ngh = IffFile.Open(nghPath);
        foreach (var chunk in ngh.OfType(ResourceType.Parse("FAMI")).ToArray())
        {
            if (chunk.Id == 0) continue;
            var family = Family.Read(ngh, chunk);
            if (family.HouseNumber == targetHouse)
            {
                var updated = family.WithHouseNumber(0);
                ngh = ngh.WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), chunk.Id, updated.Write(chunk.Id), chunk.Flags, chunk.Label);
            }
        }
        File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());

        var targetPath = Path.Combine(userData, "Houses", $"House{targetHouse:D2}.iff");
        if (!File.Exists(targetPath))
        {
            var srcPath = Path.Combine(userData, "Houses", $"House{sourceHouse:D2}.iff");
            File.Copy(srcPath, targetPath);
        }
        var lot = IffFile.Open(targetPath);
        var lotPrice = 0;
        if (lot.Find(ResourceType.Parse("SIMI"), 1) is { } simiChunk)
        {
            var simi = SimulationState.Read(lot, simiChunk).WithValuation(0, 0);
            lotPrice = Math.Max(0, simi.Field0x9C);
            lot = lot.WithChunkDataResized(simiChunk.Type, simiChunk.Id, simi.WriteChunkData());
            File.WriteAllBytes(targetPath, lot.Bytes.ToArray());
        }
        return lotPrice;
    }

    [Fact]
    public void LotlessDaemon_NeighborhoodWarmup_CreateHouseholdMoveIn_OpenAndCloseLot()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5);
        try
        {
            Neighborhoods.Current = 1;
            var lotPrice = EnsureAffordableVacantLot(userData, sourceHouse: 5, targetHouse: 2);

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            Assert.Null(server.World);

            Assert.True(server.Handle("""{"id":"aud1","op":"audio","enabled":true}"""));
            var audResp = ResponseFor(DrainEvents(outputStream, writer), "aud1");
            Assert.True(audResp.GetProperty("ok").GetBoolean());
            Assert.True(audResp.GetProperty("result").GetProperty("enabled").GetBoolean());

            Assert.True(server.Handle("""{"id":"warm1","op":"warmup"}"""));
            var warmResp = ResponseFor(DrainEvents(outputStream, writer), "warm1");
            Assert.True(warmResp.GetProperty("ok").GetBoolean());
            Assert.True(warmResp.GetProperty("result").GetProperty("warmed").GetBoolean());
            Assert.True(warmResp.GetProperty("result").GetProperty("entries").GetInt32() > 0);

            Assert.True(server.Handle("""{"id":"cat1","op":"appearanceCatalog","gender":"f","age":"adult","skin":"lgt"}"""));
            var catResp = ResponseFor(DrainEvents(outputStream, writer), "cat1");
            Assert.True(catResp.GetProperty("ok").GetBoolean());

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "binA",
                op = "createHousehold",
                familyName = "BinAlpha",
                moveIn = false,
                members = new[] { new { name = "Alpha", bio = "A", code = "fa", skin = "lgt" } },
            })));
            var famA = ResponseFor(DrainEvents(outputStream, writer), "binA").GetProperty("result").GetProperty("family").GetInt32();

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "binB",
                op = "createHousehold",
                familyName = "BinBeta",
                moveIn = false,
                members = new[] { new { name = "Beta", bio = "B", code = "ma", skin = "med" } },
            })));
            var famB = ResponseFor(DrainEvents(outputStream, writer), "binB").GetProperty("result").GetProperty("family").GetInt32();

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "nbEdit",
                op = "neighborhood",
                mode = 1,
                edit = $"delete:{famA}; delete:{famB}",
                webScenes = false,
            })));
            var nbEditResp = ResponseFor(DrainEvents(outputStream, writer), "nbEdit");
            Assert.True(nbEditResp.GetProperty("ok").GetBoolean());
            var nghAfterDelete = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
            Assert.Null(nghAfterDelete.Find(ResourceType.Parse("FAMI"), checked((ushort)famA)));
            Assert.Null(nghAfterDelete.Find(ResourceType.Parse("FAMI"), checked((ushort)famB)));

            if (Directory.Exists(Path.Combine(gameDir, "UserData2")))
            {
                Assert.True(server.Handle("""{"id":"nbSwitch2","op":"neighborhood","mode":1,"neighborhood":2}"""));
                Assert.True(ResponseFor(DrainEvents(outputStream, writer), "nbSwitch2").GetProperty("ok").GetBoolean());
                Assert.Equal(2, Neighborhoods.Current);
                Assert.True(File.Exists(Path.Combine(profile, "UserData2", "Neighborhood.iff")));

                Assert.True(server.Handle("""{"id":"nbSwitch1","op":"neighborhood","mode":1,"neighborhood":1}"""));
                Assert.True(ResponseFor(DrainEvents(outputStream, writer), "nbSwitch1").GetProperty("ok").GetBoolean());
                Assert.Equal(1, Neighborhoods.Current);
            }

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "casMoveIn",
                op = "createHousehold",
                familyName = "LotlessCas",
                moveIn = true,
                house = 2,
                members = new[] { new { name = "Clara", bio = "Lotless CAS", code = "fa", skin = "lgt" } },
            })));
            var casEvents = DrainEvents(outputStream, writer);
            var casResp = ResponseFor(casEvents, "casMoveIn");
            Assert.True(casResp.GetProperty("ok").GetBoolean(), casResp.ToString());
            var casResult = casResp.GetProperty("result");
            Assert.Equal(2, casResult.GetProperty("house").GetInt32());
            Assert.Equal(20_000 - lotPrice, casResult.GetProperty("budget").GetInt32());
            Assert.NotNull(server.World);
            Assert.Equal((short)0, server.World![SimulationGlobal.SimlessBuildMode]);
            Assert.Contains(casEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "frame");
            Assert.Contains(casEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "state");

            Assert.True(server.Handle("""{"id":"close1","op":"closeLot","save":true}"""));
            var closeEvents = DrainEvents(outputStream, writer);
            var closeResp = ResponseFor(closeEvents, "close1");
            Assert.True(closeResp.GetProperty("ok").GetBoolean());
            Assert.True(closeResp.GetProperty("result").GetProperty("saved").GetBoolean());
            Assert.Null(server.World);
            var closedState = closeEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "state").GetProperty("state");
            Assert.Equal(0, closedState.GetProperty("house").GetInt32());

            Assert.True(server.Handle("""{"id":"open1","op":"openLot","house":2}"""));
            var openEvents = DrainEvents(outputStream, writer);
            var openResp = ResponseFor(openEvents, "open1");
            Assert.True(openResp.GetProperty("ok").GetBoolean());
            Assert.Equal(2, openResp.GetProperty("result").GetProperty("house").GetInt32());
            Assert.False(openResp.GetProperty("result").GetProperty("simless").GetBoolean());
            Assert.NotNull(server.World);
            Assert.Contains(openEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "frame");
            Assert.Contains(openEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "state");
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VacationTravel_PersistsHomeLotOnDeparture_WritesHouse40OnSave_AndRestoresSuspendedHomeOnLoad()
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
            var traveller = homeWorld.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            Assert.NotNull(traveller);
            var travellerGuid = traveller!.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];

            traveller[PersonData.CookingHouseBrokenSkill] = 777;
            homeWorld.Clock.Restore(9, 45, 0);
            var beforeLeaveBytes = File.ReadAllBytes(house05Path);
            var initialHouse40Bytes = File.ReadAllBytes(house40Path);

            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            Assert.True(File.Exists(house05Path + ".runtime.json"));
            var persistedHomeBytes = File.ReadAllBytes(house05Path);
            Assert.False(beforeLeaveBytes.SequenceEqual(persistedHomeBytes));

            var vacWorld = server.World!;
            Assert.NotSame(homeWorld, vacWorld);
            vacWorld.Clock.Restore(16, 20, 0);

            Assert.True(server.Handle("""{"id":"saveVac","op":"save","slot":"vac-slot","lot":true}"""));
            var saveResp = ResponseFor(DrainEvents(outputStream, writer), "saveVac");
            Assert.True(saveResp.GetProperty("ok").GetBoolean());
            var saveResult = saveResp.GetProperty("result");
            Assert.True(saveResult.GetProperty("lot").GetBoolean());
            Assert.True(File.Exists(house40Path + ".runtime.json"));
            Assert.False(initialHouse40Bytes.SequenceEqual(File.ReadAllBytes(house40Path)));

            var envelopePath = saveResult.GetProperty("path").GetString()!;
            using var envelopeDoc = JsonDocument.Parse(File.ReadAllText(envelopePath));
            Assert.Equal(40, envelopeDoc.RootElement.GetProperty("house").GetInt32());
            var suspendedHome = envelopeDoc.RootElement.GetProperty("suspendedHome");
            Assert.Equal(5, suspendedHome.GetProperty("homeHouse").GetInt32());

            Assert.True(server.Handle("""{"id":"closeVac","op":"closeLot","save":false}"""));
            DrainEvents(outputStream, writer);
            Assert.Null(server.World);

            Assert.True(server.Handle("""{"id":"loadVac","op":"load","slot":"vac-slot"}"""));
            var loadEvents = DrainEvents(outputStream, writer);
            var loadResp = ResponseFor(loadEvents, "loadVac");
            Assert.True(loadResp.GetProperty("ok").GetBoolean());
            Assert.Equal(40, loadResp.GetProperty("result").GetProperty("house").GetInt32());
            Assert.NotNull(server.World);
            Assert.Equal(16, server.World!.Clock.Hour);
            Assert.Equal(20, server.World.Clock.Minute);

            server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
            server.BatchRun(1);
            var returnEvents = DrainEvents(outputStream, writer);
            var lotChanged = returnEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.Equal(5, lotChanged.GetProperty("house").GetInt32());
            Assert.False(lotChanged.GetProperty("away").GetBoolean());
            var returnedTraveller = server.World.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == travellerGuid);
            Assert.NotNull(returnedTraveller);
            Assert.Equal((short)777, returnedTraveller![PersonData.CookingHouseBrokenSkill]);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildModeObjects_BuyMoveSellUndoRedo_TerrainStrokeCoalescing_AndBuyModeClearsStack()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
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

            Assert.True(server.Handle("""{"id":"pause","op":"speed","value":0}"""));
            Assert.True(server.Handle("""{"id":"edit","op":"view","editing":true,"doorWindowTool":true}"""));
            DrainEvents(outputStream, writer);

            var world = server.World!;
            for (int quiet = 0, pass = 0; quiet < 4 && pass < 32; pass++)
            {
                world.WakeSimulateOnPauseObjects();
                world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, world.Clock.Ticks);
                if (world.DialogRecords.Any(d => d.Visible && d.State < 2))
                {
                    quiet = 0;
                    while (world.DialogRecords.FirstOrDefault(d => d.Visible && d.State < 2) is { } dlg)
                    {
                        byte responseCode = dlg.Kind is 1 or 2 or 14 ? (byte)1 : (byte)0;
                        world.RespondMessageDialog(dlg.Token, responseCode);
                    }
                }
                else if (world.DialogRecords.Count == 0)
                {
                    quiet++;
                }
                else
                {
                    quiet = 0;
                }
            }
            DrainEvents(outputStream, writer);
            var budget = Assert.IsType<FamilyBudget>(world.FamilyBudget);
            budget.ActiveFamily = budget.ActiveFamily!.WithBudget(50_000);

            var lotIff = IffFile.Open(Path.Combine(userData, "Houses", "House05.iff"));
            var initTerrain = LotTerrain.Read(lotIff);
            var initWalls = LotWalls.Read(lotIff, 1);
            var initFloors = LotFloors.Read(lotIff, 1);
            var initTiles = LotTiles.Read(lotIff);
            var roomMap = world.LotRoomMaps.GetValueOrDefault(1);
            var (wx, wy) = Enumerable.Range(12, 38).SelectMany(y => Enumerable.Range(12, 38).Select(x => (x, y)))
                .First(p =>
                {
                    var refH = initTerrain.ReferenceHeight(p.x, p.y);
                    for (var dy = 0; dy <= 4; dy++)
                        for (var dx = 0; dx <= 4; dx++)
                        {
                            var nx = p.x + dx;
                            var ny = p.y + dy;
                            if (!initTiles.InsideLot(nx, ny)) return false;
                            if (world.TileValueAt(nx, ny, 1) != 0) return false;
                            if (roomMap is not null && !roomMap.IsOutside(roomMap.RoomAt(nx, ny))) return false;
                            if (initWalls[nx, ny][0] != 0) return false;
                            if (initFloors[nx, ny] != 0) return false;
                            if (initTerrain.ReferenceHeight(nx, ny) != refH) return false;
                        }
                    return true;
                });

            var reg = ObjectRegistry.Scan(gameDir, Path.Combine(userData, "Characters"));
            ObjectEntry? ResolveMasterEntry(ObjectEntry tile) =>
                tile.Definition[ObjdField.MasterId] is var mId and not 0
                    ? reg.Entries.FirstOrDefault(c => ReferenceEquals(c.File, tile.File) &&
                                                      c.Definition[ObjdField.MasterId] == mId &&
                                                      c.Definition[ObjdField.SubIndex] == ushort.MaxValue)
                    : null;

            var doorCandidates = world.WalkOrder.Where(o =>
            {
                if (o.RuntimeClass != ObjectClass.Portal || o.MultiTileMasterId != 0 ||
                    o.Definition[(int)ObjdField.WallStyle] == 0 || !world.CanSellObject(o))
                    return false;
                var tileEntry = reg.Find(unchecked((uint)(o.TypeRecord?.Key ?? 0)));
                if (tileEntry is null) return false;
                var effective = ResolveMasterEntry(tileEntry) ?? tileEntry;
                return effective.Definition[ObjdField.RoomSort] == 0 &&
                       effective.Definition[ObjdField.BuildModeType] is 1 or 2;
            }).ToList();
            var existingDoor = doorCandidates.FirstOrDefault(o =>
            {
                var tileEntry = reg.Find(unchecked((uint)(o.TypeRecord?.Key ?? 0)))!;
                var effective = ResolveMasterEntry(tileEntry) ?? tileEntry;
                return effective.Definition[ObjdField.BuildModeType] == 1;
            }) ?? doorCandidates.First();
            var doorOrigId = existingDoor.Id;
            var doorLeadTileGuid = existingDoor.TypeRecord!.Key;
            var doorOrigPos = existingDoor.Position;
            var doorOrigDir = (byte)(1 << (existingDoor[ObjectData.Direction] & 6));
            var doorTileEntry = reg.Find(unchecked((uint)doorLeadTileGuid))!;
            var doorCatalogGuid = (ResolveMasterEntry(doorTileEntry) ?? doorTileEntry).Guid;

            var budgetBeforeDoorSell = budget.ReadFamilyBudget();
            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "sellExistingDoor",
                op = "sellObject",
                objectID = doorOrigId,
            })));
            var sellExistingDoorResp = ResponseFor(DrainEvents(outputStream, writer), "sellExistingDoor");
            Assert.True(sellExistingDoorResp.GetProperty("ok").GetBoolean(), sellExistingDoorResp.ToString());
            Assert.Null(world.ObjectById(doorOrigId));
            var budgetAfterDoorSell = sellExistingDoorResp.GetProperty("result").GetProperty("budget").GetInt32();
            Assert.True(budgetAfterDoorSell >= budgetBeforeDoorSell);

            Assert.True(server.Handle("""{"id":"undoDoorSell","op":"undo"}"""));
            var undoDoorSellResp = ResponseFor(DrainEvents(outputStream, writer), "undoDoorSell");
            Assert.True(undoDoorSellResp.GetProperty("ok").GetBoolean(), undoDoorSellResp.ToString());
            Assert.True(undoDoorSellResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal(budgetBeforeDoorSell, undoDoorSellResp.GetProperty("result").GetProperty("budget").GetInt32());
            Assert.Contains(world.WalkOrder, o => o.TypeRecord?.Key == doorLeadTileGuid && o.Position == doorOrigPos);

            Assert.True(server.Handle("""{"id":"redoDoorSell","op":"redo"}"""));
            var redoDoorSellResp = ResponseFor(DrainEvents(outputStream, writer), "redoDoorSell");
            Assert.True(redoDoorSellResp.GetProperty("ok").GetBoolean(), redoDoorSellResp.ToString());
            Assert.True(redoDoorSellResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal(budgetAfterDoorSell, redoDoorSellResp.GetProperty("result").GetProperty("budget").GetInt32());
            Assert.DoesNotContain(world.WalkOrder, o => o.TypeRecord?.Key == doorLeadTileGuid && o.Position == doorOrigPos);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "buyDoorAgain",
                op = "buyObject",
                guid = doorCatalogGuid,
                x = doorOrigPos.X,
                y = doorOrigPos.Y,
                level = doorOrigPos.Height,
                direction = doorOrigDir,
            })));
            var buyDoorAgainResp = ResponseFor(DrainEvents(outputStream, writer), "buyDoorAgain");
            Assert.True(buyDoorAgainResp.GetProperty("ok").GetBoolean(), buyDoorAgainResp.ToString());
            var boughtDoorId = checked((short)buyDoorAgainResp.GetProperty("result").GetProperty("objectID").GetInt32());
            Assert.NotNull(world.ObjectById(boughtDoorId));

            Assert.True(server.Handle("""{"id":"undoDoorBuy","op":"undo"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "undoDoorBuy").GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Null(world.ObjectById(boughtDoorId));
            Assert.True(server.Handle("""{"id":"redoDoorBuy","op":"redo"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "redoDoorBuy").GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Contains(world.WalkOrder, o => o.TypeRecord?.Key == doorLeadTileGuid && o.Position == doorOrigPos);

            var plantCandidates = reg.Entries
                .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                            e.Definition[ObjdField.RoomSort] == 0 &&
                            e.Definition[ObjdField.BuildModeType] == 4 &&
                            e.Definition[ObjdField.MasterId] == 0 &&
                            ObjectCatalog.TryGetEntry(e, 1, out var cat) && cat is { Price: > 0 and <= 500 })
                .OrderBy(e => e.Definition[ObjdField.Price])
                .ToList();
            Assert.NotEmpty(plantCandidates);

            var budgetBeforePlant = budget.ReadFamilyBudget();
            short plantId = 0;
            uint plantGuid = 0;
            var budgetAfterBuyPlant = 0;
            foreach (var candidate in plantCandidates)
            {
                if (!server.Handle(JsonSerializer.Serialize(new
                {
                    id = $"buyPlant_{candidate.Guid}",
                    op = "buyObject",
                    guid = candidate.Guid,
                    x = (wx + 1) * 16 + 8,
                    y = (wy + 1) * 16 + 8,
                    level = 1,
                    direction = 1,
                }))) continue;
                var buyPlantResp = ResponseFor(DrainEvents(outputStream, writer), $"buyPlant_{candidate.Guid}");
                if (buyPlantResp.GetProperty("ok").GetBoolean())
                {
                    plantGuid = candidate.Guid;
                    plantId = checked((short)buyPlantResp.GetProperty("result").GetProperty("objectID").GetInt32());
                    budgetAfterBuyPlant = buyPlantResp.GetProperty("result").GetProperty("budget").GetInt32();
                    break;
                }
            }
            Assert.True(plantId > 0);
            Assert.True(budgetAfterBuyPlant < budgetBeforePlant);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "movePlant",
                op = "moveObject",
                objectID = plantId,
                x = (wx + 2) * 16 + 8,
                y = (wy + 1) * 16 + 8,
                level = 1,
                direction = 1,
            })));
            var movePlantResp = ResponseFor(DrainEvents(outputStream, writer), "movePlant");
            Assert.True(movePlantResp.GetProperty("ok").GetBoolean(), movePlantResp.ToString());
            Assert.Equal((wx + 2) * 16 + 8, world.ObjectById(plantId)!.Position.X);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "sellPlant",
                op = "sellObject",
                objectID = plantId,
            })));
            var sellPlantResp = ResponseFor(DrainEvents(outputStream, writer), "sellPlant");
            Assert.True(sellPlantResp.GetProperty("ok").GetBoolean(), sellPlantResp.ToString());
            var budgetAfterSellPlant = sellPlantResp.GetProperty("result").GetProperty("budget").GetInt32();
            Assert.Null(world.ObjectById(plantId));

            Assert.True(server.Handle("""{"id":"uSell","op":"undo"}"""));
            var uSellResp = ResponseFor(DrainEvents(outputStream, writer), "uSell");
            Assert.True(uSellResp.GetProperty("ok").GetBoolean(), uSellResp.ToString());
            Assert.True(uSellResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal(budgetAfterBuyPlant, uSellResp.GetProperty("result").GetProperty("budget").GetInt32());
            var restoredPlant = world.WalkOrder.FirstOrDefault(o =>
                o.TypeRecord?.Key == unchecked((int)plantGuid) && o.Position.X == (wx + 2) * 16 + 8 && o.Position.Y == (wy + 1) * 16 + 8);
            Assert.NotNull(restoredPlant);

            Assert.True(server.Handle("""{"id":"uMove","op":"undo"}"""));
            var uMoveResp = ResponseFor(DrainEvents(outputStream, writer), "uMove");
            Assert.True(uMoveResp.GetProperty("ok").GetBoolean(), uMoveResp.ToString());
            Assert.True(uMoveResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal((wx + 1) * 16 + 8, restoredPlant!.Position.X);

            Assert.True(server.Handle("""{"id":"uBuy","op":"undo"}"""));
            var uBuyResp = ResponseFor(DrainEvents(outputStream, writer), "uBuy");
            Assert.True(uBuyResp.GetProperty("ok").GetBoolean(), uBuyResp.ToString());
            Assert.Equal(budgetBeforePlant, uBuyResp.GetProperty("result").GetProperty("budget").GetInt32());
            Assert.Null(world.ObjectById(restoredPlant.Id));

            Assert.True(server.Handle("""{"id":"rBuy","op":"redo"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "rBuy").GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.True(server.Handle("""{"id":"rMove","op":"redo"}"""));
            Assert.True(ResponseFor(DrainEvents(outputStream, writer), "rMove").GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.True(server.Handle("""{"id":"rSell","op":"redo"}"""));
            var rSellResp = ResponseFor(DrainEvents(outputStream, writer), "rSell");
            Assert.True(rSellResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal(budgetAfterSellPlant, rSellResp.GetProperty("result").GetProperty("budget").GetInt32());

            var budgetBeforeTerrain = budget.ReadFamilyBudget();
            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "ter1",
                op = "architectureBatch",
                strokeId = "drag-1",
                commands = new[] { new { command = "TERRAIN_RAISE", x = wx + 1, y = wy + 3, style = initTerrain.ReferenceHeight(wx + 1, wy + 3) + 8, pattern = 0 } },
            })));
            var ter1Resp = ResponseFor(DrainEvents(outputStream, writer), "ter1");
            Assert.True(ter1Resp.GetProperty("ok").GetBoolean());

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "ter2",
                op = "architectureBatch",
                strokeId = "drag-1",
                commands = new[] { new { command = "TERRAIN_RAISE", x = wx + 2, y = wy + 3, style = initTerrain.ReferenceHeight(wx + 2, wy + 3) + 8, pattern = 0 } },
            })));
            var ter2Resp = ResponseFor(DrainEvents(outputStream, writer), "ter2");
            Assert.True(ter2Resp.GetProperty("ok").GetBoolean());

            Assert.True(server.Handle("""{"id":"uTer","op":"undo"}"""));
            var uTerResp = ResponseFor(DrainEvents(outputStream, writer), "uTer");
            Assert.True(uTerResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.Equal(budgetBeforeTerrain, uTerResp.GetProperty("result").GetProperty("budget").GetInt32());

            var buyCandidates = reg.Entries
                .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                            e.Definition[ObjdField.RoomSort] != 0 &&
                            e.Definition[ObjdField.BuildModeType] == 0 &&
                            e.Definition[ObjdField.MasterId] == 0 &&
                            ObjectCatalog.TryGetEntry(e, 1, out var cat) && cat is { Price: > 0 and <= 500 })
                .OrderBy(e => e.Definition[ObjdField.Price])
                .ToList();
            Assert.NotEmpty(buyCandidates);
            var buyPlaced = false;
            foreach (var buyCandidate in buyCandidates)
            {
                if (!server.Handle(JsonSerializer.Serialize(new
                {
                    id = $"buyChair_{buyCandidate.Guid}",
                    op = "buyObject",
                    guid = buyCandidate.Guid,
                    x = (wx + 3) * 16 + 8,
                    y = (wy + 1) * 16 + 8,
                    level = 1,
                    direction = 1,
                }))) continue;
                var buyChairResp = ResponseFor(DrainEvents(outputStream, writer), $"buyChair_{buyCandidate.Guid}");
                if (buyChairResp.GetProperty("ok").GetBoolean())
                {
                    buyPlaced = true;
                    break;
                }
            }
            Assert.True(buyPlaced);

            Assert.True(server.Handle("""{"id":"uCleared","op":"undo"}"""));
            var uClearedResp = ResponseFor(DrainEvents(outputStream, writer), "uCleared");
            Assert.False(uClearedResp.GetProperty("result").GetProperty("applied").GetBoolean());
            Assert.False(uClearedResp.GetProperty("result").GetProperty("canUndo").GetBoolean());
            Assert.False(uClearedResp.GetProperty("result").GetProperty("canRedo").GetBoolean());
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
