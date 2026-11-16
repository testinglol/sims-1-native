using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Wave11TravelAndHouseholdServerTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static (string TempRoot, string Profile, string UserData, string Session) CreateIsolatedProfile(
        string gameDir, bool copyCharacters = true, params short[] houses)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave11-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void CreateHousehold_MoveInFalse_WritesUnhousedZeroFlagZeroValue_AndNeighborhoodRpcReturnsIt()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5);
        try
        {
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "create1",
                op = "createHousehold",
                familyName = "WaveElevenBin",
                moveIn = false,
                members = new[]
                {
                    new { name = "Jane", bio = "Bin tester", code = "fa", skin = "lgt" },
                },
            })));
            var createEvents = DrainEvents(outputStream, writer);
            var createResp = ResponseFor(createEvents, "create1");
            Assert.True(createResp.GetProperty("ok").GetBoolean());
            var createResult = createResp.GetProperty("result");
            var familyChunkId = checked((ushort)createResult.GetProperty("family").GetInt32());
            Assert.Equal(0, createResult.GetProperty("house").GetInt32());
            Assert.Equal(20_000, createResult.GetProperty("budget").GetInt32());

            var ngh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
            var famiChunk = ngh.Find(ResourceType.Parse("FAMI"), familyChunkId);
            Assert.NotNull(famiChunk);
            var createdFamily = Family.Read(ngh, famiChunk!);
            Assert.Equal(0u, createdFamily.Flags);
            Assert.Equal(0, createdFamily.Value);
            Assert.Equal(0, createdFamily.HouseNumber);
            Assert.Equal(20_000, createdFamily.Budget);

            Assert.True(server.Handle("""{"id":"nb1","op":"neighborhood","mode":1}"""));
            var nbEvents = DrainEvents(outputStream, writer);
            var nbResp = ResponseFor(nbEvents, "nb1");
            Assert.True(nbResp.GetProperty("ok").GetBoolean());

            var neighborhoodJsonPath = Path.Combine(session, "neighborhood.json");
            Assert.True(File.Exists(neighborhoodJsonPath));
            using var nbDoc = JsonDocument.Parse(File.ReadAllText(neighborhoodJsonPath));
            var families = nbDoc.RootElement.GetProperty("families").EnumerateArray().ToArray();
            var binEntry = families.FirstOrDefault(f =>
                f.TryGetProperty("name", out var nameProp) &&
                string.Equals(nameProp.GetString(), "WaveElevenBin", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.Object, binEntry.ValueKind);
            Assert.Equal(20_000, binEntry.GetProperty("budget").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateHousehold_ChildAndFemaleAdult_SetsExactCharacterBodyStrings()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5);
        try
        {
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            var existingCharFiles = new HashSet<string>(
                Directory.EnumerateFiles(Path.Combine(userData, "Characters"), "User*.iff"),
                StringComparer.OrdinalIgnoreCase);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "create2",
                op = "createHousehold",
                familyName = "ChildCheck",
                moveIn = false,
                members = new object[]
                {
                    new { name = "Elena", bio = "Adult mom", code = "fa", skin = "drk", child = false },
                    new { name = "Maya", bio = "Kid", code = "fc", skin = "med", child = true },
                },
            })));
            var events = DrainEvents(outputStream, writer);
            var resp = ResponseFor(events, "create2");
            Assert.True(resp.GetProperty("ok").GetBoolean());

            var createdFiles = Directory.EnumerateFiles(Path.Combine(userData, "Characters"), "User*.iff")
                .Where(path => !existingCharFiles.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(2, createdFiles.Length);

            var familyChunkId = checked((ushort)resp.GetProperty("result").GetProperty("family").GetInt32());
            var ngh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
            var family = Family.Read(ngh, ngh.Find(ResourceType.Parse("FAMI"), familyChunkId)!);
            Assert.Equal(2, family.Members.Count);

            var byGuid = createdFiles.ToDictionary(
                path =>
                {
                    var iff = IffFile.Open(path);
                    var objdChunk = iff.Find(ResourceType.Parse("OBJD"), 128)!;
                    return BinaryPrimitives.ReadUInt32LittleEndian(objdChunk.Data.Span.Slice(0x1c, 4));
                },
                path => IffFile.Open(path));

            var childIff = byGuid[family.Members[1]];
            var childStrChunk = childIff.Find(ResourceType.Str, 200);
            Assert.NotNull(childStrChunk);
            var childStrings = StringTable.Read(childIff, childStrChunk!).ForLanguage(1);

            Assert.Equal("child", childStrings[0]);
            Assert.Equal("female", childStrings[12]);
            Assert.Equal("9", childStrings[13]);
            Assert.Equal("med", childStrings[14]);
            Assert.StartsWith("nuchd_01,", childStrings[15], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuLO,HAND=huaomed", childStrings[17], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuRO,HAND=huaomed", childStrings[18], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuLP,HAND=huapmed", childStrings[19], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuRP,HAND=huapmed", childStrings[20], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuLC,HAND=huacmed", childStrings[21], StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("HuRC,HAND=huacmed", childStrings[22], StringComparison.OrdinalIgnoreCase);

            var adultIff = byGuid[family.Members[0]];
            var adultStrings = StringTable.Read(adultIff, adultIff.Find(ResourceType.Str, 200)!).ForLanguage(1);
            Assert.Equal("adult", adultStrings[0]);
            Assert.Equal("female", adultStrings[12]);
            Assert.Equal("27", adultStrings[13]);
            Assert.Equal("drk", adultStrings[14]);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LotTravel_CompanionArrivalClock99_AndReturnHomeSyncsMotivesNbrsAndNewMember()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var house21 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House21.iff");
        var nghPath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Neighborhood.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(house21) || !File.Exists(nghPath))
            return;

        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: true, 5, 21);
        try
        {
            File.Copy(house21!, Path.Combine(userData, "Houses", "House99.iff"));

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "adopt-seed",
                op = "createHousehold",
                familyName = "StraySeed",
                moveIn = false,
                members = new[]
                {
                    new { name = "AdoptedFriend", bio = "Joined during trip", code = "fa", skin = "lgt" },
                },
            })));
            var seedEvents = DrainEvents(outputStream, writer);
            var seedFamilyChunkId = checked((ushort)ResponseFor(seedEvents, "adopt-seed").GetProperty("result").GetProperty("family").GetInt32());
            var nghSeed = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
            var adoptedGuid = Family.Read(nghSeed, nghSeed.Find(ResourceType.Parse("FAMI"), seedFamilyChunkId)!).Members[0];

            server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            var homeWorld = server.World!;
            var householdPeople = homeWorld.WalkOrder.OfType<XPerson>()
                .Where(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0)
                .ToArray();
            Assert.True(householdPeople.Length >= 2, "Expected House05 to have at least two household Sims.");
            var traveller = householdPeople[0];
            var companion = householdPeople[1];
            var travellerGuid = traveller.TypeRecord!.Key;
            var companionGuid = companion.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];

            var travellerScope99 = homeWorld.InteractionExecutorScope(traveller);
            travellerScope99.AddOrIncrement(2, 0, 35);
            travellerScope99.AddOrIncrement(2, 4, 60);
            var companionScope99 = homeWorld.InteractionExecutorScope(companion);
            companionScope99.AddOrIncrement(2, 4, 45);
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(99, travellerGuid, familyNumber);

            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            var awayWorld = server.World!;
            Assert.NotSame(homeWorld, awayWorld);
            Assert.Equal(12, awayWorld.Clock.Hour);
            Assert.Equal(0, awayWorld.Clock.Minute);

            var arrivedTraveller = awayWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == travellerGuid);
            var arrivedCompanion = awayWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == companionGuid);
            Assert.NotNull(arrivedTraveller);
            Assert.NotNull(arrivedCompanion);
            Assert.Equal(35f, arrivedTraveller!.Motives[(int)Motive.Bladder]);
            Assert.Equal(60f, arrivedTraveller.Motives[(int)Motive.Hunger]);
            Assert.Equal(45f, arrivedCompanion!.Motives[(int)Motive.Hunger]);

            arrivedTraveller[PersonData.CookingHouseBrokenSkill] = 450;
            arrivedTraveller[PersonData.FameScore] = 350;
            var returnScope = awayWorld.InteractionExecutorScope(arrivedTraveller);
            returnScope.AddOrIncrement(2, 0, 77);
            returnScope.AddOrIncrement(2, 4, -25);
            returnScope.AddOrIncrement(2, 7, 18);
            returnScope.AddOrIncrement(2, 8, 10);

            var awayBudget = Assert.IsType<FamilyBudget>(awayWorld.FamilyBudget);
            awayBudget.ActiveFamily = awayBudget.ActiveFamily!.WithMember(adoptedGuid);

            awayWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
            server.BatchRun(1);
            var returnEvents = DrainEvents(outputStream, writer);

            var returnLotChanged = returnEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.Equal(5, returnLotChanged.GetProperty("house").GetInt32());
            Assert.False(returnLotChanged.GetProperty("away").GetBoolean());

            var returnedWorld = server.World!;
            Assert.Equal(18, returnedWorld.Clock.Hour);
            Assert.Equal(10, returnedWorld.Clock.Minute);

            var returnedTraveller = returnedWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == travellerGuid);
            Assert.NotNull(returnedTraveller);
            Assert.Equal((short)450, returnedTraveller![PersonData.CookingHouseBrokenSkill]);
            Assert.Equal((short)350, returnedTraveller[PersonData.FameScore]);
            Assert.Equal(77f, returnedTraveller.Motives[(int)Motive.Bladder]);
            Assert.Equal(-25f, returnedTraveller.Motives[(int)Motive.Hunger]);
            Assert.Equal(0, returnedWorld.InteractionExecutorScope(returnedTraveller).CountValue(2));

            var adoptedOnHome = returnedWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == unchecked((int)adoptedGuid));
            Assert.NotNull(adoptedOnHome);
            Assert.True(adoptedOnHome!.Position.X >= 0 && adoptedOnHome.Position.Y >= 0,
                $"Expected adopted member to be placed inside home lot, got ({adoptedOnHome.Position.X},{adoptedOnHome.Position.Y})");

            server.BatchRun(5);
            var postReturnEvents = DrainEvents(outputStream, writer);
            Assert.DoesNotContain(postReturnEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "error");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ArchitectureBatch_SmoothTerrainRaise_TargetZeroOnFlatTile_IsNoOp()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var (tempRoot, profile, userData, session) = CreateIsolatedProfile(gameDir, copyCharacters: false, 5);
        try
        {
            var house05Path = Path.Combine(userData, "Houses", "House05.iff");
            var houseLot = IffFile.Open(house05Path);
            var lotTiles = LotTiles.Read(houseLot);
            var terrain = LotTerrain.Read(houseLot);
            (int X, int Y)? flatVertex = null;
            for (var vy = 10; vy <= LotWalls.Size - 11 && flatVertex is null; vy++)
            for (var vx = 10; vx <= LotWalls.Size - 11; vx++)
            {
                if (lotTiles.InsideLot(vx, vy))
                {
                    flatVertex = (vx, vy);
                    break;
                }
            }
            Assert.NotNull(flatVertex);
            var (tx, ty) = flatVertex!.Value;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
                terrain.SetHeight(tx + dx, ty + dy, 0);
            File.WriteAllBytes(house05Path, terrain.WriteTo(houseLot).Bytes.ToArray());

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.True(server.Handle("""{"id":"1","op":"speed","value":0}"""));
            Assert.True(server.Handle("""{"id":"2","op":"view","editing":true,"doorWindowTool":false}"""));
            var baselineEvents = DrainEvents(outputStream, writer);
            var scenePath = baselineEvents.Last(e => e.TryGetProperty("scene", out _)).GetProperty("scene").GetString()!;
            using var baselineScene = JsonDocument.Parse(File.ReadAllText(scenePath));
            var lotWidth = baselineScene.RootElement.GetProperty("lotWidth").GetInt32();
            var baselineHeights = baselineScene.RootElement.GetProperty("terrainHeights").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(0, baselineHeights[ty * lotWidth + tx]);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "3",
                op = "architectureBatch",
                commands = new[] { new { command = "TERRAIN_RAISE", x = tx, y = ty, style = 0, pattern = 1 } },
            })));
            var batchEvents = DrainEvents(outputStream, writer);
            var resp = ResponseFor(batchEvents, "3");
            Assert.True(resp.GetProperty("ok").GetBoolean());
            var result = resp.GetProperty("result");
            Assert.Equal(0, result.GetProperty("changed").GetInt32());
            Assert.Equal(0, result.GetProperty("charge").GetInt32());
            Assert.False(result.GetProperty("canUndo").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
