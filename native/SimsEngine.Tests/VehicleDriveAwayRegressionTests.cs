using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.Tests.E2E;
using SimsEngine.World;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class VehicleDriveAwayRegressionTests
{
    private static byte[] SnapOperand(short slotIndex, short mode, byte flags = 0) =>
        [(byte)slotIndex, (byte)(slotIndex >> 8), (byte)mode, (byte)(mode >> 8), flags, 0, 0, 0];

    private static RoomMap FlatOutdoorMap()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0).Raw(new byte[64 * 64 * 8]);
        var lot = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "flat.iff");
        return RoomMap.Build(lot, 1, 64, 64);
    }

    private static LotTerrain TerrainWithBorderCliffAt(int lotSize)
    {
        var data = new byte[LotGeometry.LotDimension * LotGeometry.LotDimension * 4];
        for (var x = 0; x < LotGeometry.LotDimension; x++)
        {
            var offset = (x * LotGeometry.LotDimension + (lotSize - 1)) * 4;
            data[offset] = 20;
            data[offset + 1] = 0;
            data[offset + 2] = 0;
            data[offset + 3] = 20;
        }
        var chunk = new ChunkBuilder()
            .S16(0).S16(0)
            .S32(LotGeometry.LotDimension).S32(LotGeometry.LotDimension).S32(4).U32(0)
            .Raw(data);
        var (file, iffChunk) = chunk.InFile("Arry", id: 0);
        return LotTerrain.Read(file);
    }

    [Fact]
    public void Snapshot_AtActiveLotBorderCliff_ReportsOutOfBoundsPrimitiveResult2InsteadOfSlopeFailure()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 10);
        var world = context.World;
        world.Host = null;
        world[SimulationGlobal.LotSize] = 52;
        world.LotRoomMaps[1] = FlatOutdoorMap();
        world.LotTerrain = TerrainWithBorderCliffAt(52);

        var def = PrimitiveTestWorld.Definition();
        def[(int)ObjdField.Type] = 4;
        var vehicle = world.Add(new XObject(10, def) { TypeRecord = new TypeRecord(10) });
        vehicle[ObjectData.Direction] = 4; // +Y
        vehicle[ObjectData.AllowedHeightFlags] = 1;
        vehicle[ObjectData.PlacementFlags] = 3; // AllowOnFloor | AllowOnTerrain, AllowOnSlope = 0
        vehicle.RoutingSlots.Add(new RoutingSlotRequest { X = 0, Y = -16, SnapTargetSlot = -1 });
        world.PlaceObject(vehicle, new ObjectPosition(45 * 16 + 8, 50 * 16 + 8, 1));

        var vehicleContext = context with
        {
            Me = vehicle,
            Frame = new StackFrame(vehicle.Id, [], []),
        };

        var result = SnapshotPrimitive.Execute(vehicleContext, SnapOperand(slotIndex: 0, mode: 3));

        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(1, world.LastPlacementFailureCode);
        Assert.Equal((short)2, vehicle[ObjectData.PrimitiveResult]);
    }

    [Fact]
    public void Snapshot_PassesCheckStack_SoAllowIntersectionFunctionFivePermitsOverlap()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 10);
        var world = context.World;
        world.Host = null;
        world[SimulationGlobal.LotSize] = 52;
        world.LotRoomMaps[1] = FlatOutdoorMap();

        // Function 5 (allow intersection) returns True (0xfe) via a no-op expression (0 == 0).
        var bhavBytes = new ChunkBuilder()
            .U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(0x0002).U8(0xfe).U8(0xff).Raw([0, 0, 0, 0, 0, 2, 7, 7])
            .ToArray();
        var iff = new IffFile(ChunkBuilder.File(("BHAV", 0x1005, bhavBytes)), "vehicle.iff");
        var scope = new BehaviorScope(iff, iff, null);

        var vehicleDef = PrimitiveTestWorld.Definition();
        vehicleDef[(int)ObjdField.Type] = 4;
        vehicleDef[(int)ObjdField.TileWidth] = 16;
        var vehicle = world.Add(new XObject(10, vehicleDef)
        {
            TypeRecord = new TypeRecord(10)
            {
                Name = "Test Vehicle",
                BehaviorScope = scope,
                FunctionTable = [0, 0, 0, 0, 0, 0x1005],
            },
        });
        vehicle[ObjectData.Direction] = 4;
        vehicle[ObjectData.AllowedHeightFlags] = 1;
        vehicle[ObjectData.PlacementFlags] = 3;
        vehicle.RoutingSlots.Add(new RoutingSlotRequest { X = 0, Y = -16, SnapTargetSlot = -1 });
        world.PlaceObject(vehicle, new ObjectPosition(45 * 16 + 8, 48 * 16 + 8, 1));

        var portalDef = PrimitiveTestWorld.Definition();
        portalDef[(int)ObjdField.Type] = 4;
        portalDef[(int)ObjdField.TileWidth] = 16;
        var portal = world.Add(new XObject(11, portalDef) { TypeRecord = new TypeRecord(11) });
        portal[ObjectData.AllowedHeightFlags] = 1;
        portal[ObjectData.PlacementFlags] = 3;
        world.PlaceObject(portal, new ObjectPosition(45 * 16 + 8, 49 * 16 + 8, 1));

        var frame = new StackFrame(vehicle.Id, [], []);
        var runner = new AllowIntersectionRunner(world, vehicle);
        runner.Frames.Add(frame);
        var vehicleContext = new TreeContext(world, vehicle, frame) { Stack = runner };

        var result = SnapshotPrimitive.Execute(vehicleContext, SnapOperand(slotIndex: 0, mode: 3));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(49, vehicle.Position.Y >> 4);
    }

    private sealed class AllowIntersectionRunner(SimulationWorld world, XObject me) : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            instruction.Opcode == 2 && ExpressionPrimitive.Execute(new TreeContext(world, me, frame) { Stack = this }, instruction.Operand)
                ? PrimitiveResult.True
                : PrimitiveResult.False;
    }

    [Fact]
    public void ComputeSaveHouseInfo_PreservesConfiguredLotSizeBelow64()
    {
        var world = new SimulationWorld();
        world[SimulationGlobal.LotSize] = 52;
        world.LotRoomMaps[1] = FlatOutdoorMap();

        world.ComputeSaveHouseInfo(out _, out var lotSize, out _, out _);

        Assert.Equal(52, lotSize);
        Assert.Equal((short)52, world[SimulationGlobal.LotSize]);
    }

    [Fact]
    public void SchoolBusAndCarpool_DriveAwayCleanlyWithoutGettingStuckBeepingAtRoadEdge()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;

        Assert.Equal((short)52, world[SimulationGlobal.LotSize]);
        Assert.Equal(52, world.ActiveLotDimension);

        server.HandleJson("freeWill", new { enabled = false });

        var honkTicks = new List<(int Tick, string Sound)>();
        var previousPlaySound = world.PlaySoundEvent;
        var totalTicks = 0;

        world.PlaySoundEvent = (name, ownerId, loop, unknown) =>
        {
            if (name.Contains("honk", StringComparison.OrdinalIgnoreCase))
                honkTicks.Add((totalTicks, name));
            return previousPlaySound?.Invoke(name, ownerId, loop, unknown) ?? true;
        };

        // Start just before 08:00 when both the school bus and carpool arrive on House 5.
        world.Clock.Restore(7, 58, 0);

        int? busArrivedTick = null;
        int? busStartedLeavingTick = null;
        int? busDepartedTick = null;
        (int X, int Y) lastBusPos = default;
        var busHonksWhileLeaving = 0;

        for (var step = 0; step < 6000; step++)
        {
            while (world.HasVisibleBlockingDialog)
            {
                var blocking = world.DialogRecords.FirstOrDefault(d => d.Visible && d.Blocking && d.State < 2);
                if (blocking is null) break;
                server.HandleJson("dialogResponse", new { token = blocking.Token, code = (byte)0 });
            }
            totalTicks++;
            server.BatchRun(1);

            var bus = world.WalkOrder.FirstOrDefault(o =>
                o.MultiTileMasterId == 0 &&
                (o.TypeRecord?.Name?.Contains("School Bus", StringComparison.OrdinalIgnoreCase) ?? false));

            if (bus is not null)
            {
                if (busArrivedTick is null)
                {
                    busArrivedTick = totalTicks;
                    lastBusPos = (bus.Position.X, bus.Position.Y);
                }
                else
                {
                    if (busStartedLeavingTick is null && world.Clock.Hour >= 9 &&
                        (bus.Position.X != lastBusPos.X || bus.Position.Y != lastBusPos.Y))
                    {
                        busStartedLeavingTick = totalTicks;
                    }
                    lastBusPos = (bus.Position.X, bus.Position.Y);
                }

                if (busStartedLeavingTick is not null && honkTicks.Count > 0 && honkTicks[^1].Tick == totalTicks)
                    busHonksWhileLeaving++;
            }
            else if (busArrivedTick is not null && busDepartedTick is null)
            {
                busDepartedTick = totalTicks;
                break;
            }
        }

        Assert.True(busArrivedTick is not null, "School bus never arrived on House 5.");
        Assert.True(busDepartedTick is not null, $"School bus never departed (last pos=({lastBusPos.X >> 4},{lastBusPos.Y >> 4})).");
        Assert.Equal(0, busHonksWhileLeaving);
        Assert.True(busStartedLeavingTick is not null && (busDepartedTick.Value - busStartedLeavingTick.Value) < 300,
            $"School bus took too long to drive away: {busDepartedTick - busStartedLeavingTick} ticks.");
    }
}
