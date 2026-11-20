using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Area", "SimBuildSave")]
public sealed class SimulationBuildSaveE2ETests
{
    private sealed class SingleTreeTable(short id, Bhav tree) : ITreeTable
    {
        public Bhav? Find(short treeId) => treeId == id ? tree : null;
    }

    private sealed class DictionaryTreeTable(params (short Id, Bhav Tree)[] entries) : ITreeTable
    {
        private readonly Dictionary<short, Bhav> _trees = entries.ToDictionary(e => e.Id, e => e.Tree);
        public Bhav? Find(short treeId) => _trees.TryGetValue(treeId, out var tree) ? tree : null;
    }

    private static byte[] GenericCallOperand(short call) =>
        [(byte)call, (byte)((ushort)call >> 8), 0, 0, 0, 0, 0, 0];

    private static byte[] BurnOperand(byte mode = 0, byte flags = 0) =>
        [mode, flags, 0, 0, 0, 0, 0, 0];

    private static byte[] SpecialEffectOperand(short seconds, byte byte2 = 0, byte byte3 = 0, byte flags = 0, byte byte5 = 0) =>
        [(byte)seconds, (byte)(seconds >> 8), byte2, byte3, flags, byte5, 0, 0];

    private static byte[] ExpressionOperand(short lhs, short rhs, ExpressionOperator op) =>
        [(byte)lhs, (byte)(lhs >> 8), (byte)rhs, (byte)(rhs >> 8), 0, (byte)op, 8, 7];

    private static Bhav BuildBhav(byte type, params (ushort Opcode, byte True, byte False, byte[] Operand)[] instructions)
    {
        var chunk = new ChunkBuilder().U16(0x8002).S16((short)instructions.Length).U8(type).U8(0).U8(0).U8(0).U32(0);
        foreach (var (opcode, trueTarget, falseTarget, operand) in instructions)
            chunk.U16(opcode).U8(trueTarget).U8(falseTarget).Raw(operand);
        var (file, iffChunk) = chunk.InFile("BHAV");
        return Bhav.Read(file, iffChunk);
    }

    private static XPerson FirstHouseholdSim(SimulationWorld world) =>
        world.WalkOrder.OfType<XPerson>().First(p => p[PersonData.FamilyNumber] > 0 && p.TypeRecord is not null);

    private static void DismissBlockingDialogs(ServerSession server)
    {
        var world = server.World;
        if (world is null)
            return;

        for (var guard = 0; guard < 16 && world.HasVisibleBlockingDialog; guard++)
        {
            var blocking = world.DialogRecords.FirstOrDefault(d => d.Visible && d.Blocking && d.State < 2);
            if (blocking is null)
                break;

            var resp = server.HandleJson("dialogResponse", new { token = blocking.Token, code = (byte)0 });
            Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
        }
    }

    private static (int X, int Y) FindFlatClearTileBlock(IffFile houseIff, SimulationWorld world, int width, int height, int level = 1)
    {
        var walls = LotWalls.Read(houseIff, level);
        var tiles = LotTiles.Read(houseIff);
        var terrain = LotTerrain.Read(houseIff);
        var pool = LotPool.Read(houseIff);
        var water = LotWater.Read(houseIff);
        var floors = LotFloors.Read(houseIff, level);

        for (var y = 6; y < LotWalls.Size - height - 4; y++)
        for (var x = 6; x < LotWalls.Size - width - 4; x++)
        {
            var baseHeight = terrain.ReferenceHeight(x, y);
            var clear = true;
            for (var dy = 0; dy < height && clear; dy++)
            for (var dx = 0; dx < width && clear; dx++)
            {
                var tx = x + dx;
                var ty = y + dy;
                if (!tiles.InsideLot(tx, ty) ||
                    !tiles.Buildable(tx, ty, level) ||
                    (level == 1 && (pool.IsPool(tx, ty, 1) || water.IsWater(tx, ty, 1))) ||
                    walls[tx, ty][0] != 0 ||
                    floors[tx, ty] != 0 ||
                    !terrain.IsFlatAt(tx, ty, baseHeight) ||
                    world.ObjectsAtTile(tx, ty, level).Any())
                {
                    clear = false;
                }
            }
            if (clear)
                return (x, y);
        }

        throw new InvalidOperationException($"Unable to find clear {width}x{height} block on level {level}.");
    }

    private static List<(ObjectEntry Entry, ObjectCatalogEntry Cat)> CatalogMatches(
        ObjectRegistry registry,
        Func<ObjectEntry, ObjectCatalogEntry, bool> predicate)
    {
        var matches = new List<(ObjectEntry Entry, ObjectCatalogEntry Cat)>();
        foreach (var entry in registry.Entries)
        {
            if (ObjectCatalog.TryGetEntry(entry, 1, out var cat) && cat is not null && predicate(entry, cat))
                matches.Add((entry, cat));
        }
        return matches;
    }

    private static (short ObjectId, uint Guid, int Price) BuyFirstCatalogMatch(
        ServerSession server,
        ObjectRegistry registry,
        int tileX,
        int tileY,
        int level,
        Func<ObjectEntry, ObjectCatalogEntry, bool> predicate,
        byte[]? directions = null)
    {
        directions ??= [1, 4, 16, 64];
        var candidates = CatalogMatches(registry, predicate)
            .OrderBy(pair => pair.Cat.Price)
            .ThenBy(pair => pair.Entry.Guid)
            .ToArray();
        Assert.NotEmpty(candidates);

        foreach (var (entry, cat) in candidates)
        {
            foreach (var dir in directions)
            {
                var resp = server.HandleJson("buyObject", new
                {
                    guid = entry.Guid,
                    x = tileX * 16 + 8,
                    y = tileY * 16 + 8,
                    level,
                    direction = dir
                });
                if (resp.GetProperty("ok").GetBoolean())
                {
                    var result = resp.GetProperty("result");
                    DismissBlockingDialogs(server);
                    return (checked((short)result.GetProperty("objectID").GetInt32()), entry.Guid, cat.Price);
                }
            }
        }

        throw new InvalidOperationException($"Failed to place any matching catalog object at ({tileX},{tileY},{level}).");
    }

    private static bool TryBuyObjectInRegion(
        ServerSession server,
        uint guid,
        int startX,
        int startY,
        int width,
        int height,
        int level,
        out short objectId)
    {
        byte[] dirs = [1, 4, 16, 64];
        for (var dy = 0; dy < height; dy++)
        {
            for (var dx = 0; dx < width; dx++)
            {
                var wx = (startX + dx) * 16 + 8;
                var wy = (startY + dy) * 16 + 8;
                foreach (var dir in dirs)
                {
                    DismissBlockingDialogs(server);
                    var resp = server.HandleJson("buyObject", new
                    {
                        guid,
                        x = wx,
                        y = wy,
                        level,
                        direction = dir
                    });
                    if (resp.GetProperty("ok").GetBoolean())
                    {
                        DismissBlockingDialogs(server);
                        objectId = checked((short)resp.GetProperty("result").GetProperty("objectID").GetInt32());
                        return true;
                    }
                }
            }
        }

        objectId = 0;
        return false;
    }

    private static void EnsureObjectOnLot(SimulationWorld world, uint guid, ObjectPosition pos)
    {
        if (world.WalkOrder.Any(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == guid))
            return;
        if (world.ObjectFactory?.Invoke(unchecked((int)guid)) is not { } created)
            return;
        world.Add(created);
        world.InitializeCreatedObject(created);
        world.PlaceObject(created, pos);
    }

    // -------------------------------------------------------------------------
    // Feature F18: GenericSimsCall Production Wiring (No PrimitiveTestHost)
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public void T1_F18_01_ProductionLotValuationAndNgbhSync_Calls00_08_09()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 2, 2);
        var registry = world.ObjectRegistry!;

        var initialValuation = server.State().GetProperty("budgetReport").GetProperty("propertyValue").GetInt32();
        var (_, _, boughtPrice) = BuyFirstCatalogMatch(
            server, registry, tx, ty, 1,
            (e, cat) => e.Definition[ObjdField.MasterId] == 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        cat.Price is > 10 and <= 500);

        world[SimulationGlobal.IsTutorialHouseNoYes] = 1;
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x00)));
        Assert.Equal((short)0, world[SimulationGlobal.IsTutorialHouseNoYes]);
        Assert.Equal((short)1, world.NeighborhoodData[1]);
        Assert.Equal((short)0, world.NeighborhoodData[2]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x08)));
        Assert.Equal((short)2, world.NeighborhoodData[1]);

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x09)));
        Assert.Equal((short)3, world.NeighborhoodData[1]);

        var updatedReport = server.State().GetProperty("budgetReport");
        var updatedValuation = updatedReport.GetProperty("propertyValue").GetInt32();
        Assert.True(updatedValuation >= initialValuation + boughtPrice,
            $"Expected production valuation to reflect placed object ({boughtPrice}): before={initialValuation}, after={updatedValuation}.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public void T1_F18_02_GenericSimsCall03_DismissesActiveProductionDialog()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var dialog = world.CreateMessageDialog(
            sim, null, "Production Dialog", "Tutorial/Notification active", "OK", null, null,
            blocking: true, awaitsResponse: true);
        Assert.NotNull(dialog);
        Assert.True(world.HasVisibleDialog);
        Assert.NotEmpty(server.State().GetProperty("dialogs").EnumerateArray());

        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x03)));

        Assert.False(world.HasVisibleDialog);
        Assert.Empty(server.State().GetProperty("dialogs").EnumerateArray());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public void T1_F18_03_GenericSimsCall05_CalculatesProductionNetWorthOrUpkeepWithoutThrow()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var familyNumber = sim[PersonData.FamilyNumber];
        Assert.True(familyNumber > 0);
        var budgetBefore = server.State().GetProperty("budget").GetInt32();

        sim.Temporaries[0] = familyNumber;
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));
        var result = GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x05));

        Assert.Equal(PrimitiveResult.True, result);
        var budgetAfter = server.State().GetProperty("budget").GetInt32();
        Assert.True(budgetAfter != budgetBefore, $"Expected GenericSimsCall 0x05 to update household funds from {budgetBefore}, got {budgetAfter}.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public void T1_F18_04_GenericSimsCall2A_RefreshesSelectionAcrossStateClassesWithoutThrow()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sims = world.WalkOrder.OfType<XPerson>()
            .Where(p => p[PersonData.FamilyNumber] > 0 && p.TypeRecord is not null)
            .ToArray();
        Assert.True(sims.Length >= 2, "House05 must contain at least 2 household Sims.");

        var caller = sims[0];
        var target = sims[1];
        caller.SetObjectTableEntry(1, target);
        caller.Temporaries[0] = 1;
        var ctx = new TreeContext(world, caller, new StackFrame(0, [], [0, 0]));

        // StateClass 1 (home lot)
        target.StateValue = 5;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2a)));
        Assert.NotEqual(0u, target.Flags & 2u);
        Assert.Equal(target.Id, server.State().GetProperty("selectedSim").GetInt32());

        // StateClass 2 (away/community state) must not throw "manager slot object is missing"
        caller.SetObjectTableEntry(1, caller);
        caller.Temporaries[0] = 1;
        caller.StateValue = 21;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2a)));
        Assert.NotEqual(0u, caller.Flags & 2u);
        Assert.Equal(caller.Id, server.State().GetProperty("selectedSim").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F18")]
    public void T1_F18_05_GenericSimsCall1AAnd2B_ExecuteProductionStateUpdates()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));

        // 0x1a: resolves active/stored slot person record and sets Temporaries[1] = 1
        sim.Temporaries[0] = -1;
        sim.Temporaries[1] = -1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x1a)));
        Assert.True(sim.Temporaries[0] >= 0);
        Assert.Equal((short)1, sim.Temporaries[1]);

        // 0x2b: LotUpkeep updates Magic Master Spells controller (0xB6C90029) attributes when present
        EnsureObjectOnLot(world, 0xB6C90029u, new ObjectPosition(4 * 16 + 8, 4 * 16 + 8, 1));
        var magicMaster = world.ObjectByTypeRecordKey(unchecked((int)0xB6C90029));
        Assert.NotNull(magicMaster);
        Assert.True(magicMaster!.Attributes.Length >= 6);
        for (var i = 0; i < 6; i++)
            magicMaster.Attributes[i] = 0;
        sim.LotUpkeepGateByte14C = true;
        sim.TableWord10C = world.GlobalPerson.LotUpkeepMatchWord22;
        sim.StateValue = 5;
        sim.LotUpkeepPointer128Present = true;
        sim.LotUpkeepMinimumResources[0] = 12;
        sim.LotUpkeepMinimumResources[1] = 24;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2b)));
        Assert.True(world.LotUpkeepManagerPresent);
        Assert.True(magicMaster.Attributes[0] >= 12);
        Assert.True(magicMaster.Attributes[1] >= 24);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public void T2_F18_01_InvalidGenericSimsCallSubcall_RaisesError2AAndContainedInThread()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));

        var ex2C = Assert.Throws<SimulationException>(() =>
            GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2c)));
        Assert.Equal(SimulationError.InvalidGlobalSimCall, ex2C.Error);

        var ex99 = Assert.Throws<SimulationException>(() =>
            GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(99)));
        Assert.Equal(SimulationError.InvalidGlobalSimCall, ex99.Error);

        // Execute inside an object tree tick -> SimulationServer catches into ThreadErrors cleanly
        var runner = new XObject(9001, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(4 * 16 + 8, 4 * 16 + 8, 1)
        };
        runner[ObjectData.AllowedHeightFlags] = 1;
        world.Add(runner);
        world.ClockManager.SetTickEnabled(runner, true);
        DismissBlockingDialogs(server);

        var badTree = BuildBhav(0, (1, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, GenericCallOperand(0x2c)));
        var stack = TreeRunner.StartObjectTree(world, runner, new SingleTreeTable(4096, badTree), 4096, runner.Id, runner.Id, []);
        Assert.NotNull(stack);
        world.SavedExecutionStacks[runner.Id] = stack!;

        var errCountBefore = world.ThreadErrors.Count;
        server.Step(1);
        Assert.True(world.ThreadErrors.Count > errCountBefore);
        Assert.Equal(runner.Id, world.ThreadErrors[^1].ObjectId);
        Assert.True(server.State().GetProperty("house").GetInt32() == 5);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public void T2_F18_02_GenericSimsCall05_OnEmptyOrCommunityLot_ReturnsFalseSafely()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var simlessLot = E2EHarness.FindHouseSource(gameDir, 1, 21) is not null ? (short)21 : (short)5;
        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, simlessLot);
        using var server = E2EHarness.StartServer(profile, house: simlessLot);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        if (simlessLot == 5)
        {
            world[SimulationGlobal.SimlessBuildMode] = 1;
        }

        var caller = world.WalkOrder.OfType<XObject>().First();
        caller.Temporaries[0] = 9999; // non-existent family/object key
        var ctx = new TreeContext(world, caller, new StackFrame(0, [], [0, 0]));

        var budgetBefore = server.State().GetProperty("budget").GetInt32();
        var result = GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x05));
        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(budgetBefore, server.State().GetProperty("budget").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public void T2_F18_03_GenericSimsCall2A_ZeroSlotOrNonPerson_NoOpsCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var sim = FirstHouseholdSim(world);
        var nonPerson = world.WalkOrder.First(o => o is not XPerson);
        var selectedBefore = server.State().GetProperty("selectedSim").GetInt32();
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));

        sim.Temporaries[0] = 0;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2a)));
        Assert.Equal(selectedBefore, server.State().GetProperty("selectedSim").GetInt32());

        sim.SetObjectTableEntry(1, nonPerson);
        sim.Temporaries[0] = 1;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x2a)));
        Assert.Equal(selectedBefore, server.State().GetProperty("selectedSim").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public void T2_F18_04_GenericSimsCall03_WithNoDialogsOpen_IsIdempotentNoOp()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        DismissBlockingDialogs(server);
        Assert.Empty(world.DialogRecords);

        var sim = FirstHouseholdSim(world);
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x03)));
        Assert.Empty(world.DialogRecords);
        Assert.False(world.HasVisibleDialog);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F18")]
    public void T2_F18_05_GenericSimsCallInitialization_OnFreshMoveInBeforeSave()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var createResp = server.HandleJson("createHousehold", new
        {
            familyName = "FreshF18",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Alice", bio = "Fresh", code = "fa", skin = "lgt" }
            }
        });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.Null(world.Host);

        var sim = FirstHouseholdSim(world);
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x00)));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x08)));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, GenericCallOperand(0x09)));
        Assert.Equal((short)3, world.NeighborhoodData[1]);
        Assert.NotNull(world.SavedSimulationState);
        Assert.True(server.State().GetProperty("budget").GetInt32() > 0);
    }

    // -------------------------------------------------------------------------
    // Feature F19: SimAntics VM, SpecialEffect, Emergent NPCs & Routing
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public void T1_F19_01_FireAndFirefighterEmergentLoop_SpreadsAndRunsCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 6, 6);
        var wallResp = server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = tx, y = ty, x2 = 4, y2 = 4, style = 1, pattern = 0 });
        Assert.True(wallResp.GetProperty("ok").GetBoolean(), wallResp.ToString());
        var floorResp = server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = tx, y = ty, x2 = 4, y2 = 4, pattern = 1 });
        Assert.True(floorResp.GetProperty("ok").GetBoolean(), floorResp.ToString());

        // Place stove (0xEB686709) and Smoke Alarm (0xEEA7D425) inside the enclosed room
        Assert.True(TryBuyObjectInRegion(server, 0xEB686709u, tx, ty, 4, 4, 1, out var burnableId), "Failed to place stove inside room.");
        var burnable = world.ObjectById(burnableId)!;
        burnable[ObjectData.FlagField2] = unchecked((short)(burnable[ObjectData.FlagField2] | 0x0020));

        Assert.NotNull(registry.Find(0xEEA7D425u));
        var alarmBought = TryBuyObjectInRegion(server, 0xEEA7D425u, tx, ty, 4, 4, 1, out _);
        Assert.True(alarmBought, "Expected Smoke Alarm (0xEEA7D425) to be placed inside room.");

        var sim = FirstHouseholdSim(world);
        var burnCtx = new TreeContext(world, sim, new StackFrame(burnable.Id, [], []));
        var burnResult = BurnPrimitive.Execute(burnCtx, BurnOperand(mode: 0, flags: 0));
        Assert.Equal(PrimitiveResult.True, burnResult);

        var initialFires = world.WalkOrder.Where(o => o.TypeRecord?.Key == BurnPrimitive.FireType).ToArray();
        Assert.NotEmpty(initialFires);

        var sawFirefighter = false;
        for (var round = 0; round < 45; round++)
        {
            DismissBlockingDialogs(server);
            server.BatchRun(30);
            if (world.WalkOrder.OfType<XPerson>().Any(p => unchecked((uint)(p.TypeRecord?.Key ?? 0)) == 0x14C180BAu))
                sawFirefighter = true;
            if (sawFirefighter && world.WalkOrder.All(o => o.TypeRecord?.Key != BurnPrimitive.FireType))
                break;
        }

        var remainingFires = world.WalkOrder.Count(o => o.TypeRecord?.Key == BurnPrimitive.FireType);
        Assert.True(sawFirefighter, "Expected Smoke Alarm to summon Firefighter (0x14C180BA).");
        Assert.Equal(0, remainingFires);
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public void T1_F19_02_BurglarAndAlarmEmergentLoop_ExecutesWithoutErrors()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 6, 6);
        Assert.True(server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = tx, y = ty, x2 = 4, y2 = 4, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = tx, y = ty, x2 = 4, y2 = 4, pattern = 1 }).GetProperty("ok").GetBoolean());

        Assert.NotNull(registry.Find(0xAB647747u));
        Assert.True(TryBuyObjectInRegion(server, 0xAB647747u, tx, ty, 4, 4, 1, out var alarmId), "Expected Burglar Alarm (0xAB647747) to be placed inside room.");
        var alarmObj = world.ObjectById(alarmId);
        Assert.NotNull(alarmObj);

        world.Clock.Restore(2, 0, 0);
        world[SimulationGlobal.Hour] = 2;
        world[SimulationGlobal.Minute] = 0;
        var burglarEntry = registry.Find(0xC7857D8Au);
        Assert.NotNull(burglarEntry);

        EnsureObjectOnLot(world, 0xC7857D8Au, new ObjectPosition((tx + 2) * 16 + 8, (ty + 2) * 16 + 8, 1));
        var burglar = world.WalkOrder.FirstOrDefault(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0xC7857D8Au);
        Assert.NotNull(burglar);
        var initialPos = burglar!.Position;

        for (var round = 0; round < 10; round++)
        {
            DismissBlockingDialogs(server);
            server.BatchRun(30);
        }

        Assert.Empty(world.ThreadErrors);
        Assert.Equal((short)1, alarmObj!.Attributes[1]);
        Assert.Contains(world.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0x8535DBF8u && o.Attributes[0] == 15);
        Assert.NotEqual(initialPos, burglar.Position);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public void T1_F19_03_EmergentNpcsAndControllers_SpawnAndTickWithoutThreadErrors()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        // Verify key emergent NPC/object definitions resolve in real game catalog
        var emergentGuids = new[]
        {
            0xD840F488u, // Bills
            0xA82892C3u, // RepoMan
            0x99D14201u, // SocialWorker
            0xA23F4AEAu, // GrimReaper
            0x60344A9Bu, // TragicClown
            0x7D2089B6u, // Tragic Clown Painting
        };
        var resolvedCount = emergentGuids.Count(g => registry.Find(g) is not null);
        Assert.True(resolvedCount >= 3, $"Expected at least 3 emergent NPC/object definitions in TS1 catalog, found {resolvedCount}.");

        // Place Bills unconditionally
        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 2, 2);
        Assert.NotNull(registry.Find(0xD840F488u));
        EnsureObjectOnLot(world, 0xD840F488u, new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1));
        Assert.Contains(world.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0xD840F488u);

        // Verify Tragic Clown Painting (0x7D2089B6) & TrClownGen (0x7A655BED) are present
        if (world.WalkOrder.All(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) != 0x7D2089B6u))
        {
            var (px, py) = FindFlatClearTileBlock(houseIff, world, 2, 2);
            EnsureObjectOnLot(world, 0x7D2089B6u, new ObjectPosition(px * 16 + 8, py * 16 + 8, 1));
        }
        Assert.Contains(world.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0x7D2089B6u);
        Assert.Contains(world.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0x7A655BEDu);

        // Disable Free Will and drop Fun/Social/Comfort to -95 so TrClownGen spawns TragicClown (0x60344A9B)
        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());
        foreach (var hSim in world.WalkOrder.OfType<XPerson>().Where(p => p[PersonData.FamilyNumber] > 0).ToArray())
        {
            foreach (var motiveName in new[] { "Fun", "Social", "Comfort" })
            {
                var motiveResp = server.HandleJson("setMotive", new { objectID = hSim.Id, motive = motiveName, value = -95f });
                Assert.True(motiveResp.GetProperty("ok").GetBoolean(), motiveResp.ToString());
            }
        }

        for (var round = 0; round < 18; round++)
        {
            DismissBlockingDialogs(server);
            server.BatchRun(200);
            if (world.WalkOrder.OfType<XPerson>().Any(p => unchecked((uint)(p.TypeRecord?.Key ?? 0)) == 0x60344A9Bu))
                break;
        }

        Assert.Contains(world.WalkOrder.OfType<XPerson>(), p => unchecked((uint)(p.TypeRecord?.Key ?? 0)) == 0x60344A9Bu);
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public void T1_F19_04_CareerCatalogAndSpecialEffectPrimitive_ExecuteCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.NotNull(world.CareerHost);

        // Career progression verification via real CareerHost
        var careers = world.CareerHost!.CareerTable();
        Assert.NotEmpty(careers);
        var sim = FirstHouseholdSim(world);
        var chosenCareer = (short)careers[0];
        sim[PersonData.JobType] = chosenCareer;
        sim[PersonData.JobPromotionLevel] = 0;
        var simState = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt32() == sim.Id);
        Assert.Equal(chosenCareer, simState.GetProperty("jobType").GetInt32());

        // SpecialEffectPrimitive 0x23 3-tick headless dispatch verification
        var target = world.WalkOrder.First(o => o is not XPerson);
        var frame = new StackFrame(target.Id, [], []);
        var ctx = new TreeContext(world, sim, frame);
        var operand = SpecialEffectOperand(seconds: 2, byte2: 7, byte3: 8, flags: 0, byte5: 9);

        var beforeDispatches = world.HeadlessSpecialEffects.Dispatches.Count;
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(ctx, operand));
        Assert.Equal((short)1, world.GlobalObjectTableEffectWords[11]);

        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(ctx, operand));
        Assert.Equal(beforeDispatches + 1, world.HeadlessSpecialEffects.Dispatches.Count);
        var dispatched = world.HeadlessSpecialEffects.Dispatches[^1];
        Assert.Equal(sim.Id, dispatched.CallerId);
        Assert.Equal(target.Id, dispatched.StackObjectId);
        Assert.Equal(new SpecialEffectRequest(2000, 7, 8, 0, 9), dispatched.Request);

        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(ctx, operand));
        Assert.False(world.PrimitiveTicks.ContainsKey(frame));
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F19")]
    public void T1_F19_05_MultiStoryRoutingMoveOutOfWayAndFreeWillAutonomy()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        // 1. Multi-story routing to level 2 floor tile on House05 (if level 2 with stairs exists)
        var sim = world.WalkOrder.OfType<XPerson>()
            .FirstOrDefault(p => p[PersonData.FamilyNumber] > 0 && p.Position.Height == 1 && p.TypeRecord is not null)
            ?? FirstHouseholdSim(world);
        if (sim.Id != server.State().GetProperty("selectedSim").GetInt32())
        {
            Assert.True(server.HandleJson("selectSim", new { objectID = sim.Id }).GetProperty("ok").GetBoolean());
        }
        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var hiPad = world.Objects.Values.FirstOrDefault(o =>
            o.Position.Height == 2 &&
            o.TypeRecord?.Name is { } name &&
            name.Contains("Stair", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Hi", StringComparison.OrdinalIgnoreCase));
        (int X, int Y)? level2Goal = null;
        if (hiPad is not null && world.LotRoomMaps.TryGetValue(2, out var level2RoomMap))
        {
            var upstairsRoom = level2RoomMap.RoomAt(hiPad.Position.X >> 4, hiPad.Position.Y >> 4);
            for (var y = 10; y < 54 && level2Goal is null; y++)
            for (var x = 10; x < 54 && level2Goal is null; x++)
            {
                if (level2RoomMap.RoomAt(x, y) != upstairsRoom)
                    continue;
                if (Math.Abs(x - (hiPad.Position.X >> 4)) + Math.Abs(y - (hiPad.Position.Y >> 4)) < 2)
                    continue;
                if (world.CanPlaceObject(sim, new ObjectPosition(x * 16 + 8, y * 16 + 8, 2), null) &&
                    !world.ObjectsAtTile(x, y, 2).Any())
                {
                    level2Goal = (x, y);
                }
            }
        }

        Assert.NotNull(level2Goal);
        var goal = level2Goal!.Value;
        var gMenu = server.HandleJson("groundMenu", new { x = goal.X * 16 + 8, y = goal.Y * 16 + 8, level = 2 });
        Assert.True(gMenu.GetProperty("ok").GetBoolean(), gMenu.ToString());
        var goHere = gMenu.GetProperty("result").EnumerateArray().FirstOrDefault();
        Assert.Equal(JsonValueKind.Object, goHere.ValueKind);
        var gInteract = server.HandleJson("groundInteract", new
        {
            x = goal.X * 16 + 8,
            y = goal.Y * 16 + 8,
            level = 2,
            interaction = goHere.GetProperty("interaction").GetInt32(),
            param0 = goHere.GetProperty("param0").GetInt32()
        });
        Assert.True(gInteract.GetProperty("ok").GetBoolean(), gInteract.ToString());
        for (var step = 0; step < 90; step++)
        {
            server.BatchRun(10);
            if (sim.Position.Height == 2 && sim.NativeRoute is null)
                break;
        }
        Assert.Equal(2, sim.Position.Height);
        Assert.Equal((short)0, sim[PersonData.RouteResult]);

        // 2. MoveOutOfWay on an idle Sim
        var otherSim = world.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.Id != sim.Id && p[PersonData.FamilyNumber] > 0) ?? sim;
        otherSim.CurrentActionRecord = null;
        otherSim.ActionQueueCurrent = 0;
        otherSim.ActionQueueEnd = 0;
        Assert.True(RouteExecutor.MoveOutOfWay(world, otherSim, 37));
        Assert.True(otherSim.ActionQueueEnd > otherSim.ActionQueueCurrent);

        // 3. Free Will autonomy with low Hunger + fridge/food available
        var (fx, fy) = FindFlatClearTileBlock(houseIff, world, 2, 2);
        var hasFridge = world.WalkOrder.Any(o => o.TypeRecord?.Interactions?.Interactions.Any(i =>
            i.Name?.Contains("Snack", StringComparison.OrdinalIgnoreCase) == true ||
            i.Name?.Contains("Meal", StringComparison.OrdinalIgnoreCase) == true) == true);
        if (!hasFridge)
        {
            BuyFirstCatalogMatch(
                server, registry, fx, fy, 1,
                (e, cat) => e.Definition[ObjdField.MasterId] == 0 &&
                            e.Definition[ObjdField.BuildModeType] == 0 &&
                            cat.Price is > 0 and <= 1500 &&
                            e.CatalogString(0, 1)?.Contains("Fridge", StringComparison.OrdinalIgnoreCase) == true);
        }

        var fwResp = server.HandleJson("freeWill", new { enabled = true });
        Assert.True(fwResp.GetProperty("ok").GetBoolean(), fwResp.ToString());
        Assert.Equal((short)1, world[SimulationGlobal.FreeWill]);
        server.HandleJson("setMotive", new { objectID = sim.Id, motive = "Hunger", value = -75f });

        server.BatchRun(120);
        Assert.Empty(world.ThreadErrors);
        Assert.True(world.AutonomyAttemptTrace.Count > 0 || sim.CurrentActionRecord is not null || sim.ActionQueueEnd > sim.ActionQueueCurrent,
            "Expected Free Will ON with low Hunger to evaluate or queue autonomous interactions.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public void T2_F19_01_WalledInUnreachableTarget_FailsRouteWithoutCrossingWalls()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 4, 4);

        // Enclose tiles (tx+1, ty+1)..(tx+2, ty+2) with 4 walls and zero doors (x2/y2 are width/height)
        var wallResp = server.HandleJson("architecture", new
        {
            command = "WALL_RECT",
            level = 1,
            x = tx + 1,
            y = ty + 1,
            x2 = 2,
            y2 = 2,
            style = 1,
            pattern = 0
        });
        Assert.True(wallResp.GetProperty("ok").GetBoolean(), wallResp.ToString());

        var sim = FirstHouseholdSim(world);
        Assert.False(sim.Position.X >> 4 >= tx + 1 && sim.Position.X >> 4 < tx + 3 &&
                     sim.Position.Y >> 4 >= ty + 1 && sim.Position.Y >> 4 < ty + 3);

        var targetX = (tx + 1) * 16 + 8;
        var targetY = (ty + 1) * 16 + 8;
        var gMenu = server.HandleJson("groundMenu", new { x = targetX, y = targetY, level = 1 });
        Assert.True(gMenu.GetProperty("ok").GetBoolean(), gMenu.ToString());
        var goHere = gMenu.GetProperty("result").EnumerateArray().First();

        var gInteract = server.HandleJson("groundInteract", new
        {
            x = targetX,
            y = targetY,
            level = 1,
            interaction = goHere.GetProperty("interaction").GetInt32(),
            param0 = goHere.GetProperty("param0").GetInt32()
        });
        Assert.True(gInteract.GetProperty("ok").GetBoolean(), gInteract.ToString());

        server.BatchRun(250);
        var simTileX = sim.Position.X >> 4;
        var simTileY = sim.Position.Y >> 4;
        Assert.False(simTileX >= tx + 1 && simTileX < tx + 3 && simTileY >= ty + 1 && simTileY < ty + 3,
            $"Sim illegally crossed enclosing walls to ({simTileX},{simTileY}).");
        Assert.NotEqual((short)0, sim[PersonData.RouteResult]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public void T2_F19_02_VmStepLimitAndEmptyTree_ContainedCleanlyWithoutHang()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var sim = FirstHouseholdSim(world);
        var ctx = new TreeContext(world, sim, new StackFrame(0, [], [0, 0]));

        // 1. Infinite self-loop BHAV raises TreeRunError.StepLimit (1101 == 0x44d)
        var loopBhav = BuildBhav(0, (2, 0, 0, ExpressionOperand(0, 0, ExpressionOperator.EqualTo)));
        var loopEx = Assert.Throws<TreeRunException>(() => TreeRunner.Run(ctx, loopBhav));
        Assert.Equal(TreeRunError.StepLimit, loopEx.Error);
        Assert.Equal(0x44d, (int)loopEx.Error);

        // 2. Gosub to empty BHAV raises TreeRunError.TreeNotEntered (1000)
        var callerBhav = BuildBhav(0, (4097, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse, [0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0]));
        var emptyBhav = BuildBhav(0);
        var subTable = new DictionaryTreeTable((4096, callerBhav), (4097, emptyBhav));
        var emptyEx = Assert.Throws<TreeRunException>(() => TreeRunner.Run(ctx, callerBhav, table: subTable, id: 4096));
        Assert.Equal(TreeRunError.TreeNotEntered, emptyEx.Error);
        Assert.Equal(1000, (int)emptyEx.Error);

        // 3. Running infinite loop on an object stack during Server.Step is caught in ThreadErrors
        var obj = world.Add(new XObject(9002, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(4 * 16 + 8, 5 * 16 + 8, 1)
        });
        obj[ObjectData.AllowedHeightFlags] = 1;
        world.ClockManager.SetTickEnabled(obj, true);
        DismissBlockingDialogs(server);
        var stack = TreeRunner.StartObjectTree(world, obj, new SingleTreeTable(4096, loopBhav), 4096, obj.Id, obj.Id, []);
        Assert.NotNull(stack);
        world.SavedExecutionStacks[obj.Id] = stack!;

        var errorsBefore = world.ThreadErrors.Count;
        server.Step(1);
        Assert.Equal(errorsBefore + 1, world.ThreadErrors.Count);
        Assert.Equal(obj.Id, world.ThreadErrors[^1].ObjectId);
        Assert.False(world.SavedExecutionStacks.ContainsKey(obj.Id));
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public void T2_F19_03_FireOnPoolBoundaryAndOutOfBounds_ContainedSafely()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var sim = FirstHouseholdSim(world);
        var oobOrigin = world.Add(new XObject(9003, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(0, 10 * 16 + 8, 1)
        });
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(new TreeContext(world, sim, new StackFrame(oobOrigin.Id, [], [])), BurnOperand()));

        var farOrigin = world.Add(new XObject(9004, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(63 * 16 + 8, 10 * 16 + 8, 1)
        });
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(new TreeContext(world, sim, new StackFrame(farOrigin.Id, [], [])), BurnOperand()));

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 5, 5);
        var poolResp = server.HandleJson("architecture", new { command = "POOL_RECT", level = 1, x = tx + 1, y = ty + 1, x2 = 2, y2 = 2 });
        Assert.True(poolResp.GetProperty("ok").GetBoolean(), poolResp.ToString());

        var isolated = world.Add(new XObject(9005, new short[XObject.DefinitionCount])
        {
            Position = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1)
        });
        isolated[ObjectData.AllowedHeightFlags] = 1;
        world.PlaceObject(isolated, isolated.Position);

        var burnOk = BurnPrimitive.Execute(new TreeContext(world, sim, new StackFrame(isolated.Id, [], [])), BurnOperand());
        Assert.Equal(PrimitiveResult.True, burnOk);

        server.BatchRun(120);
        Assert.Empty(world.ThreadErrors);
        // Pool tiles must never catch fire
        for (var py = ty + 1; py <= ty + 2; py++)
        for (var px = tx + 1; px <= tx + 2; px++)
        {
            Assert.DoesNotContain(world.ObjectsAtTile(px, py, 1), o => o.TypeRecord?.Key == BurnPrimitive.FireType);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public void T2_F19_04_MissedCarpoolAndSchoolGradeBoundaries_HandledWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        Assert.NotNull(world.CareerHost);

        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());

        var sims = world.WalkOrder.OfType<XPerson>().Where(p => p[PersonData.FamilyNumber] > 0).ToArray();
        var adult = sims.FirstOrDefault(p => p[PersonData.PersonAge] >= 18 && p[PersonData.JobType] == 9)
                    ?? sims.First(p => p[PersonData.PersonAge] >= 18);
        var child = sims.FirstOrDefault(p => p[PersonData.PersonAge] is > 0 and < 18);

        adult[PersonData.JobType] = 9;
        adult[PersonData.JobPromotionLevel] = 0;
        adult[PersonData.JobPerformance] = -100;
        adult[PersonData.LastDayAtWork] = 0;

        Assert.NotNull(child);
        child![PersonData.JobPerformance] = -100;

        // Day 0: miss carpool window (06:30 -> 10:30)
        world.Clock.Restore(6, 30, 0);
        for (var i = 0; i < 4; i++)
        {
            DismissBlockingDialogs(server);
            server.BatchRun(1800);
        }

        // Midnight rollover
        world.Clock.Restore(23, 59, 56);
        DismissBlockingDialogs(server);
        server.BatchRun(10);

        // Day 1: miss carpool window again (06:29 -> 11:29) -> fired dialog + JobType reset to 0
        world.Clock.Restore(6, 29, 0);
        var sawFiredDialog = false;
        for (var i = 0; i < 5 && !sawFiredDialog; i++)
        {
            DismissBlockingDialogs(server);
            server.BatchRun(1800);
            sawFiredDialog = world.DialogRecords.Any(d =>
                (d.Title?.Contains("Fired", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Message?.Contains("lost the job", StringComparison.OrdinalIgnoreCase) ?? false));
        }

        DismissBlockingDialogs(server);
        server.BatchRun(30);

        Assert.True(sawFiredDialog, "Expected Fired / lost the job dialog after missing work two consecutive days.");
        Assert.Equal((short)0, adult[PersonData.JobType]);
        Assert.Empty(world.ThreadErrors);
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F19")]
    public void T2_F19_05_FreeWillOff_SuppressesAutonomousObjectQueuing()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var fwOff = server.HandleJson("freeWill", new { enabled = false });
        Assert.True(fwOff.GetProperty("ok").GetBoolean(), fwOff.ToString());
        Assert.False(server.State().GetProperty("freeWill").GetBoolean());
        Assert.Equal((short)0, world[SimulationGlobal.FreeWill]);

        var sim = FirstHouseholdSim(world);
        sim.CurrentActionRecord = null;
        sim.ActionQueueCurrent = 0;
        sim.ActionQueueEnd = 0;
        server.HandleJson("setMotive", new { objectID = sim.Id, motive = "Hunger", value = -50f });
        server.HandleJson("setMotive", new { objectID = sim.Id, motive = "Fun", value = -50f });

        server.BatchRun(120);
        DismissBlockingDialogs(server);

        var simState = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt32() == sim.Id);
        Assert.Equal(0, simState.GetProperty("actions").GetArrayLength());
        Assert.Empty(world.ThreadErrors);
    }

    // -------------------------------------------------------------------------
    // Feature F20: Build / Buy / Save-Load & Stub Cleanup
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public void T1_F20_01_BuildModeToolsWallsFloorsPoolRoofAndUndoRedo()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 7, 7);

        // Walls (rectangle + diagonal), wallpaper fill, floor rectangle, pool rectangle
        var wallRect = server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = tx, y = ty, x2 = 3, y2 = 3, style = 1, pattern = 0 });
        Assert.True(wallRect.GetProperty("ok").GetBoolean(), wallRect.ToString());
        Assert.True(wallRect.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        var diagWall = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = tx + 4, y = ty, x2 = tx + 5, y2 = ty + 1, style = 1, pattern = 0 });
        Assert.True(diagWall.GetProperty("ok").GetBoolean(), diagWall.ToString());

        var patternFill = server.HandleJson("architecture", new { command = "PATTERN_FILL", level = 1, x = tx + 1, y = ty + 1, pattern = 1 });
        Assert.True(patternFill.GetProperty("ok").GetBoolean(), patternFill.ToString());

        var floorRect = server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = tx, y = ty, x2 = 3, y2 = 3, pattern = 1 });
        Assert.True(floorRect.GetProperty("ok").GetBoolean(), floorRect.ToString());
        Assert.True(floorRect.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        var poolRect = server.HandleJson("architecture", new { command = "POOL_RECT", level = 1, x = tx + 4, y = ty + 3, x2 = 2, y2 = 2 });
        Assert.True(poolRect.GetProperty("ok").GetBoolean(), poolRect.ToString());
        Assert.True(poolRect.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        // Door (category 8) & Window (category 9)
        BuyFirstCatalogMatch(server, registry, tx, ty, 1,
            (_, cat) => cat.Categories == (1 << 8) && cat.Price <= 500);
        BuyFirstCatalogMatch(server, registry, tx + 1, ty, 1,
            (_, cat) => cat.Categories == (1 << 9) && cat.Price <= 500);

        // Roof style & pitch
        var roofStyles = LotRoof.Styles(gameDir);
        Assert.NotEmpty(roofStyles);
        var roofResp = server.HandleJson("roof", new { style = roofStyles[0], pitch = 1.25f });
        Assert.True(roofResp.GetProperty("ok").GetBoolean(), roofResp.ToString());
        Assert.InRange(roofResp.GetProperty("result").GetProperty("roofPitch").GetSingle(), 1.24f, 1.26f);

        // Undo & Redo on build history
        var stateBeforeUndo = server.State();
        Assert.True(stateBeforeUndo.GetProperty("canUndo").GetBoolean());
        var budgetBeforeUndo = stateBeforeUndo.GetProperty("budget").GetInt32();

        var undoResp = server.HandleJson("undo");
        Assert.True(undoResp.GetProperty("ok").GetBoolean(), undoResp.ToString());
        Assert.True(undoResp.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.True(undoResp.GetProperty("result").GetProperty("canRedo").GetBoolean());

        var redoResp = server.HandleJson("redo");
        Assert.True(redoResp.GetProperty("ok").GetBoolean(), redoResp.ToString());
        Assert.True(redoResp.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.Equal(budgetBeforeUndo, redoResp.GetProperty("result").GetProperty("budget").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public void T1_F20_02_TerrainToolsRaiseFlattenAndWaterTool()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var walls1 = LotWalls.Read(houseIff, 1);
        var walls2 = LotWalls.Read(houseIff, 2);
        var floors1 = LotFloors.Read(houseIff, 1);
        var floors2 = LotFloors.Read(houseIff, 2);
        var tiles = LotTiles.Read(houseIff);
        var initTerrain = LotTerrain.Read(houseIff);
        var pool = LotPool.Read(houseIff);
        var water = LotWater.Read(houseIff);
        int tx = -1, ty = -1;
        for (var cy = 6; cy < LotWalls.Size - 10 && tx < 0; cy++)
        for (var cx = 6; cx < LotWalls.Size - 10; cx++)
        {
            var h0 = initTerrain.ReferenceHeight(cx, cy);
            var ok = true;
            for (var dy = 0; dy < 6 && ok; dy++)
            for (var dx = 0; dx < 6 && ok; dx++)
            {
                var ux = cx + dx;
                var uy = cy + dy;
                if (!tiles.InsideLot(ux, uy) ||
                    pool.IsPool(ux, uy, 1) || water.IsWater(ux, uy, 1) ||
                    walls1[ux, uy][0] != 0 || walls2[ux, uy][0] != 0 ||
                    floors1[ux, uy] != 0 || floors2[ux, uy] != 0 ||
                    !initTerrain.IsFlatAt(ux, uy, h0) ||
                    world.ObjectsAtTile(ux, uy, 1).Any() ||
                    world.ObjectsAtTile(ux, uy, 2).Any())
                {
                    ok = false;
                }
            }
            if (ok) { tx = cx; ty = cy; break; }
        }
        Assert.True(tx >= 0, "Expected 6x6 clear flat terrain block across levels 1 and 2 on House05.");
        var baseHeight = initTerrain.ReferenceHeight(tx + 3, ty + 3);

        var raiseResp = server.HandleJson("architectureBatch", new
        {
            commands = new[]
            {
                new { command = "TERRAIN_RAISE", x = tx + 3, y = ty + 3, style = baseHeight + 8, pattern = 0 }
            }
        });
        Assert.True(raiseResp.GetProperty("ok").GetBoolean(), raiseResp.ToString());
        Assert.True(raiseResp.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        // Sloped tiles block flat floor placement (x2=0, y2=0 targets the single sloped tile (tx+3, ty+3))
        var slopedFloor = server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = tx + 3, y = ty + 3, x2 = 0, y2 = 0, pattern = 1 });
        Assert.True(slopedFloor.GetProperty("ok").GetBoolean(), slopedFloor.ToString());
        Assert.Equal(0, slopedFloor.GetProperty("result").GetProperty("changed").GetInt32());

        // Flatten pad back to baseHeight
        var flattenResp = server.HandleJson("architectureBatch", new
        {
            commands = new[]
            {
                new { command = "TERRAIN_FLATTEN", x = tx + 2, y = ty + 2, x2 = 2, y2 = 2, style = (int)baseHeight }
            }
        });
        Assert.True(flattenResp.GetProperty("ok").GetBoolean(), flattenResp.ToString());
        Assert.True(flattenResp.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        // Apply water tool
        var waterResp = server.HandleJson("architecture", new { command = "WATER_RECT", level = 1, x = tx + 2, y = ty + 2, x2 = 2, y2 = 2 });
        Assert.True(waterResp.GetProperty("ok").GetBoolean(), waterResp.ToString());
        Assert.True(waterResp.GetProperty("result").GetProperty("changed").GetInt32() > 0);

        var saveResp = server.HandleJson("save", new { slot = "terrain-water", lot = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        var savedHouse = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        Assert.True(LotWater.Read(savedHouse).IsWater(tx + 2, ty + 2, 1));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public void T1_F20_03_BuyModeCatalogPagesRotationAndDepreciationSell()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        for (var cat = 0; cat <= 7; cat++)
        {
            var page = server.HandleJson("catalogPage", new { category = cat, offset = 0, count = 4, subsort = 0 });
            Assert.True(page.GetProperty("ok").GetBoolean(), page.ToString());
            Assert.Equal(JsonValueKind.Array, page.GetProperty("result").GetProperty("items").ValueKind);
        }

        foreach (var kind in new[] { "floor", "wall", "wallStyle", "roof", "water" })
        {
            var bPage = server.HandleJson("buildCatalogPage", new { kind, offset = 0, count = 4 });
            Assert.True(bPage.GetProperty("ok").GetBoolean(), bPage.ToString());
            Assert.Equal(JsonValueKind.Array, bPage.GetProperty("result").GetProperty("items").ValueKind);
        }

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 4, 4);
        var budgetBeforeBuy = server.State().GetProperty("budget").GetInt32();

        var (objId, _, price) = BuyFirstCatalogMatch(
            server, registry, tx + 1, ty + 1, 1,
            (e, cat) => e.Definition[ObjdField.MasterId] == 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        e.Definition[ObjdField.InitialDepreciation] > 0 &&
                        cat.Price is >= 50 and <= 800,
            directions: [1]);
        Assert.Equal(budgetBeforeBuy - price, server.State().GetProperty("budget").GetInt32());

        // Rotate across all 4 directions via moveObject
        foreach (var dir in new byte[] { 4, 16, 64, 1 })
        {
            DismissBlockingDialogs(server);
            var rot = server.HandleJson("moveObject", new { objectID = objId, x = (tx + 1) * 16 + 8, y = (ty + 1) * 16 + 8, level = 1, direction = dir });
            Assert.True(rot.GetProperty("ok").GetBoolean(), rot.ToString());
        }

        // Cross midnight rollover -> daily depreciation reduces CurrentValue below purchase price
        world.Clock.Restore(23, 59, 58);
        server.Step(2);
        DismissBlockingDialogs(server);

        var obj = world.ObjectById(objId)!;
        var currentVal = obj[ObjectData.CurrentValue];
        Assert.True(currentVal < price, $"Expected midnight depreciation to reduce value below {price}, got {currentVal}.");

        var sellResp = server.HandleJson("sellObject", new { objectID = objId });
        Assert.True(sellResp.GetProperty("ok").GetBoolean(), sellResp.ToString());
        var refund = sellResp.GetProperty("result").GetProperty("refund").GetInt32();
        Assert.True(refund < price && refund >= 0);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public void T1_F20_04_SaveAndLoadHouseAndNeighborhoodRoundTrip()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        int expectedBudget;
        uint boughtGuid;
        int boughtX;
        int boughtY;
        short simId;
        int simX;
        int simY;
        int wx;
        int wy;

        using (var server1 = E2EHarness.StartServer(profile, house: 5))
        {
            Assert.NotNull(server1.World);
            var world1 = server1.World!;
            var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
            (wx, wy) = FindFlatClearTileBlock(houseIff, world1, 3, 3);

            var wallResp = server1.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = wx, y = wy, x2 = wx + 2, y2 = wy, style = 1, pattern = 0 });
            Assert.True(wallResp.GetProperty("ok").GetBoolean(), wallResp.ToString());

            var (objId, guid, _) = BuyFirstCatalogMatch(
                server1, world1.ObjectRegistry!, wx + 1, wy + 1, 1,
                (e, cat) => e.Definition[ObjdField.MasterId] == 0 &&
                            e.Definition[ObjdField.BuildModeType] == 0 &&
                            e.Definition[ObjdField.RoomSort] != 0 &&
                            cat.Price is > 0 and <= 500);
            boughtGuid = guid;
            var obj = world1.ObjectById(objId)!;
            boughtX = obj.Position.X;
            boughtY = obj.Position.Y;

            var sim = FirstHouseholdSim(world1);
            simId = sim.Id;
            simX = sim.Position.X;
            simY = sim.Position.Y;
            Assert.True(server1.HandleJson("setMotive", new { objectID = simId, motive = "Fun", value = 42f }).GetProperty("ok").GetBoolean());
            Assert.True(server1.HandleJson("cheat", new { code = "rosebud" }).GetProperty("ok").GetBoolean());

            expectedBudget = server1.State().GetProperty("budget").GetInt32();
            var saveResp = server1.HandleJson("save", new { slot = "e2e-roundtrip", lot = true });
            Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
            Assert.True(saveResp.GetProperty("result").GetProperty("saved").GetBoolean());
        }

        using (var server2 = E2EHarness.StartServer(profile, house: 5))
        {
            var loadResp = server2.HandleJson("load", new { slot = "e2e-roundtrip" });
            Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
            Assert.NotNull(server2.World);
            var world2 = server2.World!;

            Assert.Equal(expectedBudget, server2.State().GetProperty("budget").GetInt32());
            Assert.Contains(world2.WalkOrder, o =>
                unchecked((uint)(o.TypeRecord?.Key ?? 0)) == boughtGuid &&
                o.Position.X == boughtX && o.Position.Y == boughtY);

            var reloadedSim = Assert.IsType<XPerson>(world2.ObjectById(simId));
            Assert.Equal(simX, reloadedSim.Position.X);
            Assert.Equal(simY, reloadedSim.Position.Y);
            Assert.InRange(reloadedSim[Motive.Fun], 41f, 43f);

            var reloadedLot = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
            Assert.NotEqual(0, LotWalls.Read(reloadedLot, 1)[wx, wy][0]);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F20")]
    public void T1_F20_05_ClassicSaveCompatibility_WithoutNeighborhoodStubs()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        var housePath = Path.Combine(profile.UserData, "Houses", "House05.iff");
        var rawIff = IffFile.Open(housePath);

        Assert.NotNull(rawIff.OfType(ResourceType.Parse("HOUS")).FirstOrDefault());
        Assert.NotNull(rawIff.OfType(ResourceType.Parse("objt")).FirstOrDefault());
        Assert.NotNull(rawIff.OfType(ResourceType.Parse("ObjM")).FirstOrDefault());
        Assert.NotNull(rawIff.OfType(ResourceType.Parse("SIMI")).FirstOrDefault());
        Assert.NotNull(rawIff.OfType(ResourceType.Parse("Arry")).FirstOrDefault());
        var houseHeader = House.Read(rawIff, rawIff.OfType(ResourceType.Parse("HOUS")).First());
        Assert.True(houseHeader.Version > 0);

        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var state = server.State();
        Assert.Equal(5, state.GetProperty("house").GetInt32());
        Assert.NotEmpty(state.GetProperty("sims").EnumerateArray());

        var saveResp = server.HandleJson("save", new { slot = "classic", canonical = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        Assert.True(saveResp.GetProperty("result").GetProperty("saved").GetBoolean());

        var quitResp = server.HandleJson("quit");
        Assert.True(quitResp.GetProperty("ok").GetBoolean(), quitResp.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public void T2_F20_01_InvalidPlacementAndInsufficientFunds_RejectedWithoutMutation()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        var stateBefore = server.State();
        var budgetBefore = stateBefore.GetProperty("budget").GetInt32();
        var objCountBefore = stateBefore.GetProperty("objects").GetArrayLength();

        var sampleItem = CatalogMatches(registry, (e, cat) => e.Definition[ObjdField.MasterId] == 0 && cat.Price is > 0 and <= 500)
            .OrderBy(p => p.Cat.Price)
            .First();

        // Out-of-bounds buyObject
        var oobBuy = server.HandleJson("buyObject", new { guid = sampleItem.Entry.Guid, x = 70 * 16, y = 10 * 16, level = 1, direction = 1 });
        Assert.False(oobBuy.GetProperty("ok").GetBoolean());

        // Pool outside lot bounds
        var oobPool = server.HandleJson("architecture", new { command = "POOL_RECT", level = 1, x = -1, y = 5, x2 = 4, y2 = 4 });
        if (oobPool.GetProperty("ok").GetBoolean())
        {
            Assert.False(oobPool.GetProperty("result").GetProperty("applied").GetBoolean());
        }

        // Insufficient funds buyObject
        var expensiveItem = CatalogMatches(registry, (_, cat) => cat.Price > 500)
            .OrderByDescending(p => p.Cat.Price)
            .First();
        world.FamilyBudget!.AlterFamilyBudget(0, budgetBefore); // reduce funds to 0
        Assert.Equal(0, server.State().GetProperty("budget").GetInt32());

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 3, 3);
        var unaffordableBuy = server.HandleJson("buyObject", new { guid = expensiveItem.Entry.Guid, x = tx * 16 + 8, y = ty * 16 + 8, level = 1, direction = 1 });
        Assert.False(unaffordableBuy.GetProperty("ok").GetBoolean());

        var stateAfter = server.State();
        Assert.Equal(0, stateAfter.GetProperty("budget").GetInt32());
        Assert.Equal(objCountBefore, stateAfter.GetProperty("objects").GetArrayLength());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public void T2_F20_02_InUseObjectBlockedUnlessMoveObjectsOn()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;
        var registry = world.ObjectRegistry!;

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 3, 3);
        var (objId, _, _) = BuyFirstCatalogMatch(
            server, registry, tx, ty, 1,
            (e, cat) => e.Definition[ObjdField.MasterId] == 0 &&
                        e.Definition[ObjdField.BuildModeType] == 0 &&
                        e.Definition[ObjdField.RoomSort] != 0 &&
                        cat.Price is > 0 and <= 500);

        var obj = world.ObjectById(objId)!;
        obj[ObjectData.UseCount] = 1;

        DismissBlockingDialogs(server);
        var blockedMove = server.HandleJson("moveObject", new { objectID = objId, x = (tx + 1) * 16 + 8, y = ty * 16 + 8, level = 1, direction = 1 });
        Assert.False(blockedMove.GetProperty("ok").GetBoolean());

        var blockedSell = server.HandleJson("sellObject", new { objectID = objId });
        Assert.False(blockedSell.GetProperty("ok").GetBoolean());

        Assert.True(server.HandleJson("cheat", new { code = "move_objects on" }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var allowedMove = server.HandleJson("moveObject", new { objectID = objId, x = (tx + 1) * 16 + 8, y = ty * 16 + 8, level = 1, direction = 1 });
        Assert.True(allowedMove.GetProperty("ok").GetBoolean(), allowedMove.ToString());

        Assert.True(server.HandleJson("cheat", new { code = "move_objects off" }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var reblockedSell = server.HandleJson("sellObject", new { objectID = objId });
        Assert.False(reblockedSell.GetProperty("ok").GetBoolean());

        Assert.True(server.HandleJson("cheat", new { code = "move_objects on" }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var allowedSell = server.HandleJson("sellObject", new { objectID = objId });
        Assert.True(allowedSell.GetProperty("ok").GetBoolean(), allowedSell.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public void T2_F20_03_UndoRedoStackBoundariesAndRedoClearance()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);
        var world = server.World!;

        var emptyUndo = server.HandleJson("undo");
        Assert.True(emptyUndo.GetProperty("ok").GetBoolean(), emptyUndo.ToString());
        Assert.False(emptyUndo.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.False(emptyUndo.GetProperty("result").GetProperty("canUndo").GetBoolean());

        var houseIff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House05.iff"));
        var (tx, ty) = FindFlatClearTileBlock(houseIff, world, 4, 4);

        Assert.True(server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = tx, y = ty, x2 = tx + 1, y2 = ty, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = tx, y = ty + 1, x2 = tx + 1, y2 = ty + 1, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());

        var undoOnce = server.HandleJson("undo");
        Assert.True(undoOnce.GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.True(undoOnce.GetProperty("result").GetProperty("canRedo").GetBoolean());

        // New edit clears stale redo history
        var newEdit = server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = tx, y = ty + 2, x2 = tx + 1, y2 = ty + 2, style = 1, pattern = 0 });
        Assert.True(newEdit.GetProperty("ok").GetBoolean(), newEdit.ToString());
        Assert.False(newEdit.GetProperty("result").GetProperty("canRedo").GetBoolean());

        var staleRedo = server.HandleJson("redo");
        Assert.True(staleRedo.GetProperty("ok").GetBoolean(), staleRedo.ToString());
        Assert.False(staleRedo.GetProperty("result").GetProperty("applied").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public void T2_F20_04_SaveFailurePreservesExistingSaveAndBackups()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("save", new { slot = "protect" }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("save", new { slot = "protect" }).GetProperty("ok").GetBoolean());

        var backupsBefore = server.HandleJson("saveBackups", new { slot = "protect" });
        Assert.True(backupsBefore.GetProperty("ok").GetBoolean(), backupsBefore.ToString());
        var backupNames = backupsBefore.GetProperty("result").GetProperty("backups").EnumerateArray()
            .Select(b => b.GetString()!).ToArray();
        Assert.NotEmpty(backupNames);

        var savesDir = Path.Combine(profile.Profile, "Saves");
        if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
        {
            var originalMode = File.GetUnixFileMode(savesDir);
            try
            {
                File.SetUnixFileMode(savesDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                var failedSave = server.HandleJson("save", new { slot = "protect" });
                Assert.False(failedSave.GetProperty("ok").GetBoolean());
            }
            finally
            {
                File.SetUnixFileMode(savesDir, originalMode);
            }
        }
        else
        {
            Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
            var failedSave = server.HandleJson("save", new { slot = "protect" });
            Assert.False(failedSave.GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("openLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        }

        var backupsAfter = server.HandleJson("saveBackups", new { slot = "protect" });
        Assert.True(backupsAfter.GetProperty("ok").GetBoolean(), backupsAfter.ToString());
        Assert.Contains(backupNames[0], backupsAfter.GetProperty("result").GetProperty("backups").EnumerateArray().Select(b => b.GetString()));

        var restoreResp = server.HandleJson("restoreBackup", new { slot = "protect", backup = backupNames[0] });
        Assert.True(restoreResp.GetProperty("ok").GetBoolean(), restoreResp.ToString());
        var loadResp = server.HandleJson("load", new { slot = "protect" });
        Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F20")]
    public void T2_F20_05_MissingPackAndUnknownObjectGuid_HandledGracefullyOnLoad()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        // 1. SaveCompatibility enforces MissingPacksException unless removeMissing == true
        var missingPack = new SaveCompatibility.PackRecord("local.missing-pack", "1.0.0");
        Assert.Throws<SaveCompatibility.MissingPacksException>(() =>
            SaveCompatibility.Check("fp-with-missing", [missingPack], "fp-vanilla", [], removeMissing: false));
        var migrated = SaveCompatibility.Check("fp-with-missing", [missingPack], "fp-vanilla", [], removeMissing: true);
        Assert.True(migrated.Migrated);
        Assert.Contains("local.missing-pack@1.0.0", migrated.Removed);

        // 2. End-to-end save envelope with missing pack + missing object GUID on House05
        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.NotNull(server.World);

        Assert.True(server.HandleJson("save", new { slot = "missing-pack-e2e" }).GetProperty("ok").GetBoolean());

        var storageSlot = Neighborhoods.StorageSlot("missing-pack-e2e", Neighborhoods.Current);
        var envelopePath = Path.Combine(profile.Profile, "Saves", storageSlot + ".json");
        var envelopeBytes = File.ReadAllBytes(envelopePath);
        var envelope = JsonSerializer.Deserialize<SimulationServer.SaveEnvelope>(envelopeBytes, E2EHarness.CamelCaseJson)!;

        // Mutate OBJT inside saved lot to swap a non-person object GUID to an unknown GUID (0xDEADBEEF)
        var savedLot = new IffFile(Convert.FromBase64String(envelope.State), "House05.iff");
        var objtChunk = savedLot.OfType(ResourceType.Parse("objt")).First();
        var objtTable = ObjectTypeTable.Read(savedLot, objtChunk);
        var entries = objtTable.Entries.ToList();
        var replaceIdx = entries.FindIndex(e => e.Guid != 0 && server.World!.ObjectRegistry!.Find(e.Guid)?.Definition[ObjdField.Type] != XPerson.PersonType);
        if (replaceIdx >= 0)
        {
            entries[replaceIdx] = entries[replaceIdx] with { Guid = 0xDEADBEEFu };
            var body = objtChunk.Data.Span;
            var swap = body.Length > 0 && body[0] != 0;
            var tag = body.Length >= 12 ? Encoding.ASCII.GetString(body[8..12]) : "tjbo";
            savedLot = savedLot.WithChunkDataResized(objtChunk.Type, objtChunk.Id, objtTable.Write(swap, tag, entries));
        }

        var tamperedEnvelope = envelope with
        {
            PackFingerprint = "fp-with-missing-pack",
            Packs = [missingPack],
            State = Convert.ToBase64String(savedLot.Bytes.Span)
        };
        File.WriteAllBytes(envelopePath, JsonSerializer.SerializeToUtf8Bytes(tamperedEnvelope, E2EHarness.CamelCaseJson));

        var blockedLoad = server.HandleJson("load", new { slot = "missing-pack-e2e", removePacks = false });
        Assert.False(blockedLoad.GetProperty("ok").GetBoolean());
        Assert.Contains("local.missing-pack", blockedLoad.GetProperty("error").GetString(), StringComparison.Ordinal);

        var migratedLoad = server.HandleJson("load", new { slot = "missing-pack-e2e", removePacks = true });
        Assert.True(migratedLoad.GetProperty("ok").GetBoolean(), migratedLoad.ToString());
        Assert.Contains(server.LastEvents, ev => ev.TryGetProperty("type", out var t) && t.GetString() == "saveMigrated");
        Assert.DoesNotContain(server.World!.WalkOrder, o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0xDEADBEEFu);
    }
}
