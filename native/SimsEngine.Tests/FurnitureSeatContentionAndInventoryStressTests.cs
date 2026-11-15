using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

/// <summary>
/// Adversarial stress tests for seat contention, multi-slot routing, inventory, and fame calls:
/// 1. Concurrency: Two Sims routing to the exact same chair/sofa slot simultaneously (graceful rejection, no corruption/crashes).
/// 2. Contention: Multi-tile sofa with multiple slots (slot 0 occupied, slot 1 can still be routed to and occupied).
/// 3. Inventory: Rapid sequential token manipulations (modes 0-6, zero stack object, remote person via bit 13).
/// 4. Fame Calls 0x22 / 0x24: Rapid sequential validation of dynamic cast enforcement.
/// </summary>
public sealed class FurnitureSeatContentionAndInventoryStressTests
{
    private const int SampleType = 0x300;

    private static byte[] RoutingOperand(short index, short mode, byte flags = 0) =>
        [(byte)index, (byte)(index >> 8), (byte)mode, (byte)(mode >> 8), flags, 0xEE, 0xEE, 0xEE];

    private static byte[] InventoryOperand(byte mode, byte key1 = 7, ushort flags = 0, int type = SampleType) =>
        [mode, key1, (byte)flags, (byte)(flags >> 8),
            (byte)type, (byte)(type >> 8), (byte)(type >> 16), (byte)(type >> 24)];

    private static byte[] GenericOperand(short call) =>
        [(byte)call, (byte)((ushort)call >> 8), 0, 0, 0, 0, 0, 0];

    private static RoomMap OpenRoom(int width = 64, int height = 64)
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var tile = 0; tile < width * height; tile++) body.Raw(new byte[8]);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0);
        for (var tile = 0; tile < width * height; tile++) terrain.U8(0x10);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, terrain.ToArray())), "contention-test.iff");
        return RoomMap.Build(file, 1, width, height);
    }

    private static XPerson CreatePerson(SimulationWorld world, short id, int tileX, int tileY)
    {
        var person = world.Add(new XPerson(id, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.TypeRecord = new TypeRecord(id);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0800;
        person[PersonData.NeighborId] = id;
        world.SetObjectPosition(person, new ObjectPosition(16 * tileX + 8, 16 * tileY + 8, 1));
        return person;
    }

    private static TreeContext CreateContext(SimulationWorld world, XPerson person, short stackObjectId = 0)
    {
        var frame = new StackFrame(stackObjectId, [], [0, 0])
        {
            Instruction = 1,
        };
        return new TreeContext(world, person, frame);
    }

    private static PrimitiveResult StepRoute(TreeContext context, byte[] operand)
    {
        var result = GoToRoutingSlotPrimitive.Execute(context, operand);
        if (context.World.Host is null && context.Me is XPerson person && result == PrimitiveResult.Stop)
            context.World.TickAvatarAnimations(person);
        return result;
    }

    private static PrimitiveResult RunRouteToCompletion(TreeContext context, byte[] operand, int maxSteps = 1024)
    {
        var result = StepRoute(context, operand);
        for (var step = 0; step < maxSteps && result == PrimitiveResult.Stop; step++)
            result = StepRoute(context, operand);
        return result;
    }

    // =========================================================================
    // Area 1: Multi-Sim Concurrency & Seat Contention on Solid Chairs/Sofas
    // =========================================================================

    [Fact]
    public void TwoSimsTargetingExactSameDiningChairSlot_FirstWinsSecondGracefullyRejectedWithoutCrash()
    {
        var world = new SimulationWorld { Host = null };
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        // Solid dining chair at (10, 10). HeightCode = 1 (solid). NO Flags |= 0x10.
        var definition = PrimitiveTestWorld.Definition(0);
        var chair = world.Add(new XObject(10, definition));
        chair.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });
        world.SetObjectPosition(chair, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));

        // Sim 1 at (10, 8), Sim 2 at (10, 7)
        var sim1 = CreatePerson(world, 101, 10, 8);
        var sim2 = CreatePerson(world, 102, 10, 7);

        var ctx1 = CreateContext(world, sim1, chair.Id);
        var ctx2 = CreateContext(world, sim2, chair.Id);

        // Sim 1 routes to snap slot 0 and succeeds
        var result1 = RunRouteToCompletion(ctx1, RoutingOperand(0, 2));
        Assert.Equal(PrimitiveResult.True, result1);
        Assert.Equal(sim1.ClockManagerIndex, chair.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(chair.ClockManagerIndex, sim1[ObjectData.ParentId]);
        Assert.Equal(chair.Position, sim1.Position);

        // Sim 2 attempts to route to the exact same chair snap slot 0
        var result2 = RunRouteToCompletion(ctx2, RoutingOperand(0, 2));

        // Sim 2 must be gracefully rejected with PrimitiveResult.False
        Assert.Equal(PrimitiveResult.False, result2);
        Assert.Null(sim2.NativeRoute);
        Assert.False(ctx2.Frame.Waiting);

        // Sim 1 must remain seated and completely unaffected
        Assert.Equal(sim1.ClockManagerIndex, chair.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(chair.ClockManagerIndex, sim1[ObjectData.ParentId]);
        Assert.Equal(chair.Position, sim1.Position);
    }

    [Fact]
    public void TwoSimsInterleavedConcurrentSteps_ContendingForSameSolidChair_CleanRejectionAndNoCorruption()
    {
        var world = new SimulationWorld { Host = null };
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        // Solid dining chair at (15, 15). HeightCode = 1 (solid). NO Flags |= 0x10.
        var chair = world.Add(new XObject(20, PrimitiveTestWorld.Definition(0)));
        chair.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });
        world.SetObjectPosition(chair, new ObjectPosition(15 * 16 + 8, 15 * 16 + 8, 1));

        // Sim 1 is at (15, 12), Sim 2 is at (12, 15)
        var sim1 = CreatePerson(world, 201, 15, 12);
        var sim2 = CreatePerson(world, 202, 12, 15);

        var ctx1 = CreateContext(world, sim1, chair.Id);
        var ctx2 = CreateContext(world, sim2, chair.Id);

        var operand = RoutingOperand(0, 2);

        // Interleave execution steps
        var res1 = StepRoute(ctx1, operand);
        var res2 = StepRoute(ctx2, operand);

        for (var step = 0; step < 500 && (res1 == PrimitiveResult.Stop || res2 == PrimitiveResult.Stop); step++)
        {
            if (res1 == PrimitiveResult.Stop) res1 = StepRoute(ctx1, operand);
            if (res2 == PrimitiveResult.Stop) res2 = StepRoute(ctx2, operand);
        }

        // Exactly one Sim wins and the other is rejected
        Assert.True(
            (res1 == PrimitiveResult.True && res2 == PrimitiveResult.False) ||
            (res2 == PrimitiveResult.True && res1 == PrimitiveResult.False),
            $"Expected one True and one False, but got res1={res1}, res2={res2}");

        var winner = res1 == PrimitiveResult.True ? sim1 : sim2;
        var loser = res1 == PrimitiveResult.True ? sim2 : sim1;

        Assert.Equal(winner.ClockManagerIndex, chair.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(chair.ClockManagerIndex, winner[ObjectData.ParentId]);
        Assert.Equal(chair.Position, winner.Position);

        // Loser has no active route, no pending wait, and has not corrupted chair
        Assert.Null(loser.NativeRoute);
        Assert.NotEqual(chair.ClockManagerIndex, loser[ObjectData.ParentId]);
    }

    // =========================================================================
    // Area 2: Multi-Tile Sofa with Multiple Slots
    // =========================================================================

    [Fact]
    public void MultiTileSofa_WhenSlotZeroOccupied_SlotOneCanStillBeRoutedToAndOccupiedBySecondSim()
    {
        var world = new SimulationWorld { Host = null };
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        // 2-tile sofa: Master at (10, 10), Part2 at (11, 10). Both solid (HeightCode = 1). NO Flags |= 0x10.
        var def = PrimitiveTestWorld.Definition(0);
        var sofaMaster = world.Add(new XObject(30, def));
        var sofaPart2 = world.Add(new XObject(31, def) { MultiTileMasterId = sofaMaster.Id });
        sofaMaster.MultiTileNextId = sofaPart2.Id;

        // Seat 0 on sofaMaster, Seat 1 on sofaPart2
        sofaMaster.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        sofaPart2.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });

        // Lot routing slots: slot 0 for seat 0
        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });

        world.SetObjectPosition(sofaMaster, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        world.SetObjectPosition(sofaPart2, new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1));

        // Sim 1 at (10, 8), Sim 2 at (11, 8)
        var sim1 = CreatePerson(world, 301, 10, 8);
        var sim2 = CreatePerson(world, 302, 11, 8);

        var ctx1 = CreateContext(world, sim1, sofaMaster.Id);
        var ctx2 = CreateContext(world, sim2, sofaPart2.Id);

        // Sim 1 routes to Seat 0 on sofaMaster
        var res1 = RunRouteToCompletion(ctx1, RoutingOperand(0, 2));
        Assert.Equal(PrimitiveResult.True, res1);
        Assert.Equal(sim1.ClockManagerIndex, sofaMaster.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sofaMaster.ClockManagerIndex, sim1[ObjectData.ParentId]);
        Assert.Equal(sofaMaster.Position, sim1.Position);

        // While Sim 1 is seated on Seat 0, Sim 2 routes to Seat 1 on sofaPart2
        var res2 = RunRouteToCompletion(ctx2, RoutingOperand(0, 2));
        Assert.Equal(PrimitiveResult.True, res2);
        Assert.Equal(sim2.ClockManagerIndex, sofaPart2.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sofaPart2.ClockManagerIndex, sim2[ObjectData.ParentId]);
        Assert.Equal(sofaPart2.Position, sim2.Position);

        // Both Sims are simultaneously seated in their respective sofa slots!
        Assert.Equal(sim1.ClockManagerIndex, sofaMaster.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sim2.ClockManagerIndex, sofaPart2.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sofaMaster.Position, sim1.Position);
        Assert.Equal(sofaPart2.Position, sim2.Position);
    }

    [Fact]
    public void MultiTileSofa_TwoSimsRouteConcurrentlyToSeat0AndSeat1_BothOccupySuccessfully()
    {
        var world = new SimulationWorld { Host = null };
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        // 2-tile sofa: Master at (20, 20), Part2 at (21, 20)
        var def = PrimitiveTestWorld.Definition(0);
        var sofaMaster = world.Add(new XObject(40, def));
        var sofaPart2 = world.Add(new XObject(41, def) { MultiTileMasterId = sofaMaster.Id });
        sofaMaster.MultiTileNextId = sofaPart2.Id;
        sofaMaster.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        sofaPart2.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });

        // Lot routing slot 0 targets snap slot 0
        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });

        world.SetObjectPosition(sofaMaster, new ObjectPosition(20 * 16 + 8, 20 * 16 + 8, 1));
        world.SetObjectPosition(sofaPart2, new ObjectPosition(21 * 16 + 8, 20 * 16 + 8, 1));

        var sim1 = CreatePerson(world, 401, 20, 18);
        var sim2 = CreatePerson(world, 402, 21, 18);

        var ctx1 = CreateContext(world, sim1, sofaMaster.Id);
        var ctx2 = CreateContext(world, sim2, sofaPart2.Id);

        // Concurrently route both Sims to seat 0 and seat 1
        var operand = RoutingOperand(0, 2);
        var res1 = StepRoute(ctx1, operand);
        var res2 = StepRoute(ctx2, operand);

        for (var step = 0; step < 500 && (res1 == PrimitiveResult.Stop || res2 == PrimitiveResult.Stop); step++)
        {
            if (res1 == PrimitiveResult.Stop) res1 = StepRoute(ctx1, operand);
            if (res2 == PrimitiveResult.Stop) res2 = StepRoute(ctx2, operand);
        }

        Assert.Equal(PrimitiveResult.True, res1);
        Assert.Equal(PrimitiveResult.True, res2);

        // Both Sims occupy their respective seats on the multi-tile sofa simultaneously
        Assert.Equal(sim1.ClockManagerIndex, sofaMaster.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sim2.ClockManagerIndex, sofaPart2.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sofaMaster.Position, sim1.Position);
        Assert.Equal(sofaPart2.Position, sim2.Position);
    }

    [Fact]
    public void MultiTileSofa_FullContention_BothSlotsOccupied_ThirdAndFourthSimsGracefullyRejected()
    {
        var world = new SimulationWorld { Host = null };
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var def = PrimitiveTestWorld.Definition(0);
        var sofaMaster = world.Add(new XObject(50, def));
        var sofaPart2 = world.Add(new XObject(51, def) { MultiTileMasterId = sofaMaster.Id });
        sofaMaster.MultiTileNextId = sofaPart2.Id;
        sofaMaster.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
        sofaPart2.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });

        world.LotRoutingSlots.Add(new RoutingSlotRequest
        {
            Flags = 0xff,
            SnapTargetSlot = 0,
            MinProximity = 16,
            MaxProximity = 16,
            OptimalProximity = 16,
        });

        world.SetObjectPosition(sofaMaster, new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1));
        world.SetObjectPosition(sofaPart2, new ObjectPosition(13 * 16 + 8, 12 * 16 + 8, 1));

        var sim1 = CreatePerson(world, 501, 12, 10);
        var sim2 = CreatePerson(world, 502, 13, 10);

        var ctx1 = CreateContext(world, sim1, sofaMaster.Id);
        var ctx2 = CreateContext(world, sim2, sofaPart2.Id);

        // Occupy both seats
        Assert.Equal(PrimitiveResult.True, RunRouteToCompletion(ctx1, RoutingOperand(0, 2)));
        Assert.Equal(PrimitiveResult.True, RunRouteToCompletion(ctx2, RoutingOperand(0, 2)));

        // Sim 3 tries to sit on Seat 0 (sofaMaster)
        var sim3 = CreatePerson(world, 503, 12, 10);
        var ctx3 = CreateContext(world, sim3, sofaMaster.Id);
        var res3 = RunRouteToCompletion(ctx3, RoutingOperand(0, 2));
        Assert.Equal(PrimitiveResult.False, res3);
        Assert.Null(sim3.NativeRoute);

        // Sim 4 tries to sit on Seat 1 (sofaPart2)
        var sim4 = CreatePerson(world, 504, 13, 10);
        var ctx4 = CreateContext(world, sim4, sofaPart2.Id);
        var res4 = RunRouteToCompletion(ctx4, RoutingOperand(0, 2));
        Assert.Equal(PrimitiveResult.False, res4);
        Assert.Null(sim4.NativeRoute);

        // Sim 1 and Sim 2 remain safely seated
        Assert.Equal(sim1.ClockManagerIndex, sofaMaster.FootprintSlots[0].OccupantObjectId);
        Assert.Equal(sim2.ClockManagerIndex, sofaPart2.FootprintSlots[0].OccupantObjectId);
    }

    // =========================================================================
    // Area 3: Inventory Token Manipulations Under Rapid Sequential Calls
    // =========================================================================

    [Fact]
    public void InventoryTokens_RapidSequentialAddRemoveAdjust_StressTest()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(600, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        me[PersonData.NeighborId] = 60;
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));

        var neighborId = me[PersonData.NeighborId];
        var inventory = world.NeighborInventory(neighborId);

        // 1. Rapid sequential Add (Mode 0) of 200 distinct key combinations
        for (byte k = 1; k <= 50; k++)
        {
            for (var t = 1; t <= 4; t++)
            {
                var operand = InventoryOperand(0, key1: k, flags: 0, type: t * 100);
                var res = ManageInventoryPrimitive.Execute(context, operand);
                Assert.Equal(PrimitiveResult.True, res);
            }
        }
        Assert.Equal(200, inventory.Entries.Count);
        Assert.True(inventory.Changed);

        // 2. Rapid sequential FindIndex (Mode 3)
        // flags: bits 5..6 = 0 (temp 0 for count), bits 11..12 = 1 (temp 1 for index) -> 0x0800
        const ushort mode3Flags = 1 << 11;
        for (byte k = 1; k <= 50; k++)
        {
            var operand = InventoryOperand(3, key1: k, flags: mode3Flags, type: 100);
            var res = ManageInventoryPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, res);
            Assert.Equal(1, me.Temporaries[0]); // count is 1
            Assert.True(me.Temporaries[1] >= 0); // index is non-negative
        }

        // 3. Rapid sequential Adjust (Mode 2)
        // Temporaries[2] = count to subtract, Temporaries[3] = entry index
        me.Temporaries[2] = 1; // subtract 1 token
        for (var i = 199; i >= 100; i--)
        {
            me.Temporaries[3] = (short)i;
            var operand = InventoryOperand(2, flags: 0x030a); // flags bit 8..9 selects temp 3, bit 2..3 selects temp 2
            var res = ManageInventoryPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, res);
        }
        Assert.Equal(100, inventory.Entries.Count);

        // 4. Rapid sequential Remove (Mode 1)
        for (byte k = 1; k <= 25; k++)
        {
            var operand = InventoryOperand(1, key1: k, flags: 0, type: 100);
            var res = ManageInventoryPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, res);
        }
        Assert.Equal(75, inventory.Entries.Count);
    }

    [Fact]
    public void InventoryTokens_RapidSequentialModesFiveAndSix_WithZeroStackObject_StressTest()
    {
        var world = new SimulationWorld();
        var me = world.Add(new XPerson(700, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        me[PersonData.NeighborId] = 70;
        // StackObject is explicitly 0 to stress-test the defect fix!
        var context = new TreeContext(world, me, new StackFrame(0, [], [0, 0]));

        // Register 5 neighbors with distinct GUID keys
        for (short n = 1; n <= 5; n++)
        {
            var neighbor = new NeighborState(n, [])
            {
                TypeRecordKey = 0x5000 + n,
            };
            world.Neighbors[n] = neighbor;
            world.NeighborWalkOrder.Add(neighbor);
        }

        var inventory = world.NeighborInventory(me[PersonData.NeighborId]);

        // 200 rapid alternating calls between Mode 5 and Mode 6
        for (var step = 0; step < 200; step++)
        {
            var neighborId = (short)((step % 5) + 1);
            me.Temporaries[0] = neighborId;
            var mode = (byte)(step % 2 == 0 ? 5 : 6);
            var expectedKey = mode == 5 ? 1 : 3;

            var operand = InventoryOperand(mode);
            var res = ManageInventoryPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, res);

            // Verify that the inventory has exactly one entry of expectedKey with the neighbor's GUID
            var entry = Assert.Single(inventory.Entries, e => e.Key1 == expectedKey);
            Assert.Equal(world.Neighbors[neighborId].TypeRecordKey, entry.Key2);
            Assert.Equal(1, entry.Count);
        }

        // Stress-test missing neighbor with Mode 5 & 6
        me.Temporaries[0] = 999; // non-existent neighbor
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, InventoryOperand(5)));
        Assert.Equal(PrimitiveResult.False, ManageInventoryPrimitive.Execute(context, InventoryOperand(6)));
    }

    [Fact]
    public void InventoryTokens_CrossPersonManipulationViaFlagBit13_StressTest()
    {
        var world = new SimulationWorld();
        var sim1 = world.Add(new XPerson(801, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        sim1[PersonData.NeighborId] = 81;
        var sim2 = world.Add(new XPerson(802, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        sim2[PersonData.NeighborId] = 82;

        // sim1 is the running person, but flags bit 13 delegates inventory ownership to sim2
        var context = new TreeContext(world, sim1, new StackFrame(0, [], [0, 0]));
        sim1.Temporaries[4] = sim2.ClockManagerIndex; // temporary 4 holds sim2's clock manager index

        const ushort bit13Flag = 1 << 13;

        // 50 rapid sequential token additions into sim2's inventory from sim1's context
        for (byte k = 1; k <= 50; k++)
        {
            var operand = InventoryOperand(0, key1: k, flags: bit13Flag, type: 0x900);
            var res = ManageInventoryPrimitive.Execute(context, operand);
            Assert.Equal(PrimitiveResult.True, res);
        }

        // Invariants: sim2 received 50 tokens, sim1 received 0 tokens
        var inv1 = world.NeighborInventory(sim1[PersonData.NeighborId]);
        var inv2 = world.NeighborInventory(sim2[PersonData.NeighborId]);
        Assert.Empty(inv1.Entries);
        Assert.Equal(50, inv2.Entries.Count);

        // When temporary 4 points to a non-person or out of range index, throw SimulationException
        sim1.Temporaries[4] = 999;
        var ex = Assert.Throws<SimulationException>(() =>
            ManageInventoryPrimitive.Execute(context, InventoryOperand(0, flags: bit13Flag)));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, ex.Error);
    }

    // =========================================================================
    // Area 4: Generic Sims Call 0x22 & 0x24 Rapid Sequential Type Guard Stress
    // =========================================================================

    [Fact]
    public void GenericSimsCall22And24_RapidSequentialNonPersonStackObject_AlwaysThrowsSimulationError28()
    {
        var (context, runner, host) = PrimitiveTestWorld.Create();
        context.World.Host = null;

        var def = PrimitiveTestWorld.Definition(0);
        // Create 20 non-person objects
        for (short i = 50; i < 70; i++)
        {
            var nonPerson = context.World.Add(new XObject(i, def));
            context.Frame.StackObject = nonPerson.Id;

            // Opcode 0x22
            var ex22 = Assert.Throws<SimulationException>(() =>
                GenericSimsCallPrimitive.Execute(context, GenericOperand(0x22)));
            Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, ex22.Error);

            // Opcode 0x24
            var ex24 = Assert.Throws<SimulationException>(() =>
                GenericSimsCallPrimitive.Execute(context, GenericOperand(0x24)));
            Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, ex24.Error);
        }

        // Now test with valid XPerson objects
        for (short i = 70; i < 75; i++)
        {
            var person = context.World.Add(new XPerson(i, PrimitiveTestWorld.Definition(XPerson.PersonType))
            {
                TypeRecord = new TypeRecord(i),
            });
            context.Frame.StackObject = person.Id;

            // When no callback target, gracefully returns False without throwing
            context.World.GenericSimsCallRecordCallbackTarget = null;
            Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x22)));
            Assert.Equal(PrimitiveResult.False, GenericSimsCallPrimitive.Execute(context, GenericOperand(0x24)));
        }
    }
}
