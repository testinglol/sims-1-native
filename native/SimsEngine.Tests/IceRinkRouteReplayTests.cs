using SimsEngine.Simulation;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public sealed class IceRinkRouteReplayTests
{
    [Theory]
    [InlineData(13, 80, 88)]
    [InlineData(14, 80, 40)]
    public void InFootprintSkateSlotsAcceptPassThroughChildTiles(int slotIndex, int slotX, int slotY)
    {
        var (context, world, sim, rink, parts) = CreateRink(slotIndex, slotX, slotY, tileFlags: 4);
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);
        var destination = new ObjectPosition(10 * 16 + 8 + slotX, 10 * 16 + 8 + slotY, 1);
        var result = RouteExecutor.Start(world, sim, context.Frame, rink, rink.RoutingSlots[slotIndex], null);
        for (var tick = 0; tick < 2048 && result != PrimitiveResult.True && result != PrimitiveResult.False; tick++)
        {
            if (result == PrimitiveResult.Stop) world.TickAvatarAnimations(sim);
            result = RouteExecutor.Continue(world, sim, context.Frame, null);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(destination, sim.Position);
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "destination-selected");
        Assert.Equal(99, parts.Count);
        Assert.All(parts.Skip(1), part => Assert.Equal(rink.Id, part.MultiTileMasterId));
    }

    [Fact]
    public void PassThroughRinkTilesDoNotPermitAnUnrelatedSolidObjectAtTheArrivalPoint()
    {
        var (context, world, sim, rink, _) = CreateRink(13, 80, 88, tileFlags: 4);
        var blocker = world.Add(new XObject(500, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(500),
        });
        var destination = new ObjectPosition(10 * 16 + 8 + 80, 10 * 16 + 8 + 88, 1);
        world.PlaceObject(blocker, destination);

        var result = RouteExecutor.Start(world, sim, context.Frame, rink, rink.RoutingSlots[13], null);

        Assert.Equal(PrimitiveResult.False, result);
        Assert.Contains(world.RouteCandidateTrace, entry =>
            entry.FailureReason.StartsWith("person placement callback rejected candidate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(13, 80, 88)]
    [InlineData(14, 80, 40)]
    public void SolidRinkTilesAreRejectedByThePersonFootprintCheck(int slotIndex, int slotX, int slotY)
        => AssertRinkRouteRejected(slotIndex, slotX, slotY, tileFlags: 0, expectedFailure: "person placement callback rejected candidate");

    private static void AssertRinkRouteRejected(int slotIndex, int slotX, int slotY, short tileFlags, string expectedFailure)
    {
        var (context, world, sim, rink, parts) = CreateRink(slotIndex, slotX, slotY, tileFlags);
        var result = RouteExecutor.Start(world, sim, context.Frame, rink, rink.RoutingSlots[slotIndex], null);

        Assert.Equal(PrimitiveResult.False, result);
        Assert.Equal(new ObjectPosition(8 * 16 + 8, 10 * 16 + 8, 1), sim.Position);
        Assert.Contains(world.NativeRouteTrace, entry => entry.Phase == "destination-rejected");
        Assert.NotEmpty(world.RouteCandidateTrace);
        Assert.Contains(world.RouteCandidateTrace, entry => entry.FailureReason.StartsWith(expectedFailure,
            StringComparison.Ordinal));
        Assert.Equal(99, parts.Count);
        Assert.All(parts.Skip(1), part => Assert.Equal(rink.Id, part.MultiTileMasterId));
        for (var index = 0; index < parts.Count; index++)
            Assert.Equal(index + 1 < parts.Count ? parts[index + 1].Id : (short)0, parts[index].MultiTileNextId);
        Assert.All(parts.Skip(1), part => Assert.InRange(part.Position.X >> 4, 10, 20));
        Assert.All(parts.Skip(1), part => Assert.InRange(part.Position.Y >> 4, 10, 18));
    }

    private static (TreeContext Context, SimulationWorld World, XPerson Sim, XObject Rink, List<XObject> Parts)
        CreateRink(int slotIndex, int slotX, int slotY, short tileFlags)
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.RoutingEnabled = true;
        world.CaptureExecutionTrace = true;
        world.LotRoomMaps[1] = OpenRoom();

        var sim = Assert.IsType<XPerson>(context.Me);
        sim.TypeRecord = new TypeRecord(sim.Id);
        sim[ObjectData.AllowedHeightFlags] = 1;
        sim[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(sim, new ObjectPosition(8 * 16 + 8, 10 * 16 + 8, 1));

        var rink = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            RuntimeClass = SimsEngine.Formats.ObjectClass.MultiTileObject,
            TypeRecord = new TypeRecord(2),
        });
        for (var i = 0; i <= 26; i++) rink.RoutingSlots.Add(new RoutingSlotRequest());
        rink.RoutingSlots[slotIndex] = new RoutingSlotRequest { X = slotX, Y = slotY, Flags = 0 };
        var parts = new List<XObject> { rink };
        short nextId = 200;
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 11; x++)
        {
            if (x == 0 && y == 0) continue;
            var partId = nextId++;
            var part = world.Add(new XObject(partId, PrimitiveTestWorld.Definition(0))
            {
                RuntimeClass = SimsEngine.Formats.ObjectClass.MultiTileObject,
                TypeRecord = new TypeRecord(partId),
                MultiTileMasterId = rink.Id,
                SavedMultiTileState = new SimsEngine.Formats.MultiTileState(
                    x, y, 0, 0, 0, 0, rink.Id, null),
            });
            parts.Add(part);
        }
        rink.MultiTileNextId = parts[1].Id;
        for (var i = 1; i < parts.Count; i++)
        {
            parts[i][ObjectData.Flags] = tileFlags;
            parts[i].MultiTileNextId = i + 1 < parts.Count ? parts[i + 1].Id : (short)0;
        }
        rink[ObjectData.Flags] = tileFlags;
        world.PlaceObject(rink, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        return (context, world, sim, rink, parts);
    }

    private static SimsEngine.World.RoomMap OpenRoom()
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(8).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) body.Raw(new byte[8]);
        var terrain = new ChunkBuilder().S16(0).S16(0).S32(64).S32(64).S32(1).U32(0);
        for (var tile = 0; tile < 64 * 64; tile++) terrain.U8(0x10);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray()), ("Arry", 9, terrain.ToArray())),
            "ice-rink-route-replay.iff");
        return SimsEngine.World.RoomMap.Build(file, 1, 64, 64);
    }
}
