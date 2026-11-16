using System.Text;
using System.Text.Json;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave10TerrainSmoothTests
{
    private sealed class Budget(int funds) : IFamilyBudget
    {
        public int Funds { get; private set; } = funds;
        public int ReadFamilyBudget() => Funds;
        public void AlterFamilyBudget(int expenseType, int amount) => Funds -= amount;
    }

    private static IffFile CreateFlatLot(byte height = 0)
    {
        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();

        var terrainData = new byte[64 * 64 * 4];
        if (height != 0) Array.Fill(terrainData, height);
        var wallData = new byte[64 * 64 * 8];
        var floorData = new byte[64 * 64 * 2];
        var tileFlags = Enumerable.Repeat((byte)1, 64 * 64).ToArray();

        return new IffFile(ChunkBuilder.File(
            ("Arry", 0, BuildArry(4, terrainData)),
            ("Arry", 2, BuildArry(8, wallData)),
            ("Arry", 8, BuildArry(1, tileFlags)),
            ("Arry", 11, BuildArry(2, floorData))
        ), "lot.iff");
    }

    private static Func<int> DeterministicJitterSequence(params (int X, int Y)[] offsets)
    {
        var values = new List<int>(offsets.Length * 6);
        foreach (var (x, y) in offsets)
        {
            values.Add(Math.Abs(x));
            values.Add(Math.Abs(x));
            values.Add(x < 0 ? 1 : 0);
            values.Add(Math.Abs(y));
            values.Add(Math.Abs(y));
            values.Add(y < 0 ? 1 : 0);
        }
        var index = 0;
        return () => values[index++ % values.Count];
    }

    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void SmoothRaise_DeterministicJitter_RaisesBroadMoundAndChargesTotalHeightUnits()
    {
        var lot = CreateFlatLot();
        var terrain = LotTerrain.Read(lot);
        var budget = new Budget(1000);
        const int unitPrice = 3;
        var tool = new TerrainTool(new SimulationWorld(), terrain, budget, unitPrice,
            LotTiles.Read(lot), LotPool.Read(lot), [LotFloors.Read(lot, 1)], [LotWalls.Read(lot, 1)], LotWater.Read(lot));

        var rng = DeterministicJitterSequence((0, 0), (2, 0), (-2, 0), (0, 2), (0, -2), (1, 1), (-1, -1));
        var changed = tool.SmoothRaise(20, 20, 1, rng);

        Assert.Equal(23, changed);
        Assert.False(tool.Refused);
        Assert.False(tool.InsufficientFunds);
        Assert.Equal((byte)2, terrain.Height(20, 20));
        Assert.Equal((byte)2, terrain.Height(22, 20));
        Assert.Equal((byte)2, terrain.Height(18, 20));
        Assert.Equal((byte)2, terrain.Height(20, 22));
        Assert.Equal((byte)2, terrain.Height(20, 18));
        Assert.Equal((byte)2, terrain.Height(21, 21));
        Assert.Equal((byte)2, terrain.Height(19, 19));
        Assert.Equal((byte)1, terrain.Height(23, 20));
        Assert.Equal((byte)0, terrain.Height(24, 20));

        var totalHeightUnits = 0;
        for (var vx = 0; vx < LotWalls.Size; vx++)
        for (var vy = 0; vy < LotWalls.Size; vy++)
            totalHeightUnits += terrain.Height(vx, vy);

        Assert.Equal(34, totalHeightUnits);
        Assert.Equal(totalHeightUnits * unitPrice, tool.Pending);
        Assert.Equal(totalHeightUnits * unitPrice, tool.Commit());
        Assert.Equal(1000 - totalHeightUnits * unitPrice, budget.Funds);
        Assert.Equal(0, tool.Pending);
    }

    [Fact]
    public void SmoothRaise_RollsBackTerrainAndPending_OnUneditableWallOrFloorVertex()
    {
        var lot = CreateFlatLot();
        var terrain = LotTerrain.Read(lot);
        var floors = LotFloors.Read(lot, 1);
        var walls = LotWalls.Read(lot, 1);
        var budget = new Budget(1000);
        const int unitPrice = 2;
        var tool = new TerrainTool(new SimulationWorld(), terrain, budget, unitPrice,
            LotTiles.Read(lot), LotPool.Read(lot), [floors], [walls], LotWater.Read(lot));

        Assert.Equal(1, tool.Raise(30, 30, 2));
        Assert.Equal(4, tool.Pending);

        walls[22, 20][0] = 1;
        var wallRng = DeterministicJitterSequence((0, 0), (2, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0));
        Assert.Equal(0, tool.SmoothRaise(20, 20, 1, wallRng));
        Assert.True(tool.Refused);
        Assert.False(tool.InsufficientFunds);
        Assert.Equal(4, tool.Pending);
        Assert.Equal((byte)0, terrain.Height(20, 20));
        Assert.Equal((byte)0, terrain.Height(19, 20));
        Assert.Equal((byte)2, terrain.Height(30, 30));

        walls[22, 20][0] = 0;
        floors[22, 20] = 1;
        var floorRng = DeterministicJitterSequence((0, 0), (2, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0));
        Assert.Equal(0, tool.SmoothRaise(20, 20, 1, floorRng));
        Assert.True(tool.Refused);
        Assert.False(tool.InsufficientFunds);
        Assert.Equal(4, tool.Pending);
        Assert.Equal((byte)0, terrain.Height(20, 20));
        Assert.Equal((byte)0, terrain.Height(19, 20));
        Assert.Equal((byte)2, terrain.Height(30, 30));
    }

    [Fact]
    public void SmoothRaise_RollsBackTerrainAndPending_OnInsufficientBudget()
    {
        var lot = CreateFlatLot();
        var terrain = LotTerrain.Read(lot);
        var budget = new Budget(40);
        const int unitPrice = 5;
        var tool = new TerrainTool(new SimulationWorld(), terrain, budget, unitPrice,
            LotTiles.Read(lot), LotPool.Read(lot), [LotFloors.Read(lot, 1)], [LotWalls.Read(lot, 1)], LotWater.Read(lot));

        Assert.Equal(1, tool.Raise(30, 30, 2));
        Assert.Equal(10, tool.Pending);

        var rng = DeterministicJitterSequence((0, 0), (2, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0));
        Assert.Equal(0, tool.SmoothRaise(20, 20, 1, rng));
        Assert.True(tool.InsufficientFunds);
        Assert.False(tool.Refused);
        Assert.Equal(10, tool.Pending);
        Assert.Equal((byte)0, terrain.Height(20, 20));
        Assert.Equal((byte)0, terrain.Height(19, 20));
        Assert.Equal((byte)2, terrain.Height(30, 30));
    }

    [Fact]
    public void NativeSimulationServer_ArchitectureBatchSmoothTerrain_SupportsUndoRedoAndSingleArchitectureCommand()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave10-terrain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
            var userData = Path.Combine(profile, "UserData");
            Directory.CreateDirectory(Path.Combine(userData, "Houses"));
            File.Copy(Path.Combine(gameDir, "UserData", "Neighborhood.iff"), Path.Combine(userData, "Neighborhood.iff"));
            var house05CopyPath = Path.Combine(userData, "Houses", "House05.iff");
            File.Copy(Path.Combine(gameDir, "UserData", "Houses", "House05.iff"), house05CopyPath);

            var session = Path.Combine(tempRoot, "session");
            Directory.CreateDirectory(session);
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);

            JsonElement[] DrainEvents()
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

            static JsonElement ResponseFor(JsonElement[] events, string id) =>
                events.Single(e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id);

            static JsonDocument LoadLatestScene(JsonElement[] events)
            {
                var scenePath = events.Last(e => e.TryGetProperty("scene", out _)).GetProperty("scene").GetString()!;
                return JsonDocument.Parse(File.ReadAllText(scenePath));
            }

            static int[] ReadHeights(JsonDocument sceneDoc) =>
                sceneDoc.RootElement.GetProperty("terrainHeights").EnumerateArray().Select(v => v.GetInt32()).ToArray();

            Assert.True(server.Handle("""{"id":"1","op":"speed","value":0}"""));
            Assert.True(server.Handle("""{"id":"2","op":"view","editing":true,"doorWindowTool":false}"""));
            var baselineEvents = DrainEvents();
            using var baselineScene = LoadLatestScene(baselineEvents);

            var world = Assert.IsType<SimulationWorld>(server.World);
            var budgetBefore = world.FamilyBudget!.ReadFamilyBudget();
            var lotWidth = baselineScene.RootElement.GetProperty("lotWidth").GetInt32();
            var lotHeight = baselineScene.RootElement.GetProperty("lotHeight").GetInt32();
            var baselineHeights = ReadHeights(baselineScene);

            var houseLot = IffFile.Open(house05CopyPath);
            var lotTiles = LotTiles.Read(houseLot);
            var floors1 = LotFloors.Read(houseLot, 1);
            var floors2 = LotFloors.Read(houseLot, 2);
            var walls1 = LotWalls.Read(houseLot, 1);
            var walls2 = LotWalls.Read(houseLot, 2);
            var pool = LotPool.Read(houseLot);
            var water = LotWater.Read(houseLot);

            (int X, int Y)? clearVertex = null;
            for (var vy = 6; vy <= lotHeight - 7 && clearVertex is null; vy++)
            for (var vx = 6; vx <= lotWidth - 7; vx++)
            {
                var allClear = true;
                for (var ny = vy - 5; ny <= vy + 4 && allClear; ny++)
                for (var nx = vx - 5; nx <= vx + 4; nx++)
                {
                    if (!lotTiles.InsideLot(nx, ny) ||
                        floors1[nx, ny] != 0 || floors2[nx, ny] != 0 ||
                        walls1[nx, ny][0] != 0 || walls2[nx, ny][0] != 0 ||
                        pool.Occupies(nx, ny, 1) || water.IsWater(nx, ny, 1) ||
                        world.ObjectsAtTile(nx, ny, 1).Any() ||
                        world.ObjectsAtTile(nx, ny, 2).Any())
                    {
                        allClear = false;
                        break;
                    }
                }
                if (allClear) clearVertex = (vx, vy);
            }
            Assert.NotNull(clearVertex);
            var (tx, ty) = clearVertex!.Value;
            var baseHeight = baselineHeights[ty * lotWidth + tx];

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "3",
                op = "architectureBatch",
                commands = new[] { new { command = "TERRAIN_RAISE", x = tx, y = ty, style = baseHeight + 1, pattern = 1 } }
            })));
            var batchEvents = DrainEvents();
            var batchResp = ResponseFor(batchEvents, "3");
            Assert.True(batchResp.GetProperty("ok").GetBoolean());
            var batchResult = batchResp.GetProperty("result");
            Assert.True(batchResult.GetProperty("applied").GetBoolean());
            var changed = batchResult.GetProperty("changed").GetInt32();
            var charge = batchResult.GetProperty("charge").GetInt32();
            Assert.True(changed > 1, $"Expected smooth raise to modify multiple vertices, got {changed}.");
            Assert.True(charge > 0);
            Assert.True(batchResult.GetProperty("canUndo").GetBoolean());
            Assert.False(batchResult.GetProperty("canRedo").GetBoolean());
            var budgetAfterRaise = batchResult.GetProperty("budget").GetInt32();
            Assert.Equal(budgetBefore - charge, budgetAfterRaise);

            using var raisedScene = LoadLatestScene(batchEvents);
            var raisedHeights = ReadHeights(raisedScene);
            var sceneDiffCount = Enumerable.Range(0, baselineHeights.Length).Count(i => raisedHeights[i] != baselineHeights[i]);
            Assert.Equal(changed, sceneDiffCount);

            Assert.True(server.Handle("""{"id":"4","op":"undo"}"""));
            var undoEvents = DrainEvents();
            var undoResp = ResponseFor(undoEvents, "4");
            Assert.True(undoResp.GetProperty("ok").GetBoolean());
            var undoResult = undoResp.GetProperty("result");
            Assert.True(undoResult.GetProperty("applied").GetBoolean());
            Assert.Equal(-charge, undoResult.GetProperty("charge").GetInt32());
            Assert.Equal(budgetBefore, undoResult.GetProperty("budget").GetInt32());
            Assert.False(undoResult.GetProperty("canUndo").GetBoolean());
            Assert.True(undoResult.GetProperty("canRedo").GetBoolean());
            using var undoneScene = LoadLatestScene(undoEvents);
            Assert.Equal(baselineHeights, ReadHeights(undoneScene));

            Assert.True(server.Handle("""{"id":"5","op":"redo"}"""));
            var redoEvents = DrainEvents();
            var redoResp = ResponseFor(redoEvents, "5");
            Assert.True(redoResp.GetProperty("ok").GetBoolean());
            var redoResult = redoResp.GetProperty("result");
            Assert.True(redoResult.GetProperty("applied").GetBoolean());
            Assert.Equal(charge, redoResult.GetProperty("charge").GetInt32());
            Assert.Equal(budgetAfterRaise, redoResult.GetProperty("budget").GetInt32());
            Assert.True(redoResult.GetProperty("canUndo").GetBoolean());
            Assert.False(redoResult.GetProperty("canRedo").GetBoolean());
            using var redoneScene = LoadLatestScene(redoEvents);
            Assert.Equal(raisedHeights, ReadHeights(redoneScene));

            var targetSingleHeight = raisedHeights[ty * lotWidth + tx] + 2;
            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "6",
                op = "architecture",
                command = "TERRAIN_RAISE",
                x = tx,
                y = ty,
                style = targetSingleHeight,
                pattern = 0
            })));
            var singleEvents = DrainEvents();
            var singleResp = ResponseFor(singleEvents, "6");
            Assert.True(singleResp.GetProperty("ok").GetBoolean());
            var singleResult = singleResp.GetProperty("result");
            Assert.True(singleResult.GetProperty("applied").GetBoolean());
            Assert.True(singleResult.GetProperty("changed").GetInt32() >= 1);
            Assert.True(singleResult.GetProperty("charge").GetInt32() > 0);
            Assert.True(singleResult.GetProperty("canUndo").GetBoolean());
            using var singleScene = LoadLatestScene(singleEvents);
            var singleHeights = ReadHeights(singleScene);
            Assert.Equal(targetSingleHeight, singleHeights[ty * lotWidth + tx]);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
