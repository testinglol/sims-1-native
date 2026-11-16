using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public class PedestrianPortalRoutingTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static RoomMap CreateEmptyRoomMap(int width = 64, int height = 64)
    {
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0);
        for (var i = 0; i < width * height; i++) wallBody.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var emptyLot = new IffFile(ChunkBuilder.File(("Arry", 2, wallBody.ToArray())), "lot.iff");
        return RoomMap.Build(emptyLot, 1, width, height);
    }

    [Fact]
    public void House05_PaperGirlCanRouteToSidewalkPedestrianPortal()
    {
        var gameDir = LocateGameDirectory();
        SimulationWorld world;
        XObject portal;
        XPerson paperGirl;

        var slot8 = new RoutingSlotRequest
        {
            Flags = 0xA0FF,
            MinProximity = 16,
            MaxProximity = 64,
            OptimalProximity = 16,
            Facing = -3,
            Resolution = 8
        };

        if (gameDir != null && File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
        {
            var lotPath = Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
            var bootstrap = WorldBootstrap.Load(gameDir, lotPath);
            world = bootstrap.World;

            portal = world.WalkOrder.FirstOrDefault(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == 0x81E6BEF9 &&
                                                          (o.Position.X >> 4) == 41 && (o.Position.Y >> 4) <= 2)
                     ?? world.ObjectById(6)
                     ?? throw new InvalidOperationException("Pedestrian portal object 6 missing from House 5.");

            paperGirl = world.Add(new XPerson(999, PrimitiveTestWorld.Definition(XPerson.PersonType)));
            paperGirl.TypeRecord = new TypeRecord(999) { FootprintMask = 0 };
            paperGirl[ObjectData.AllowedHeightFlags] = 1;
            paperGirl[ObjectData.Direction] = 1;
            world.SetObjectPosition(paperGirl, new ObjectPosition(35 * 16 + 8, 10 * 16 + 8, 1));
        }
        else
        {
            world = new SimulationWorld();
            world.LotRoomMaps[1] = CreateEmptyRoomMap(64, 64);

            portal = world.Add(new XObject(6, PrimitiveTestWorld.Definition()));
            portal.TypeRecord = new TypeRecord(unchecked((int)0x81E6BEF9));
            portal.RuntimeClass = ObjectClass.Portal;
            world.SetObjectPosition(portal, new ObjectPosition(41 * 16 + 8, 1 * 16 + 8, 1));

            paperGirl = world.Add(new XPerson(999, PrimitiveTestWorld.Definition(XPerson.PersonType)));
            paperGirl.TypeRecord = new TypeRecord(999) { FootprintMask = 0 };
            paperGirl[ObjectData.AllowedHeightFlags] = 1;
            paperGirl[ObjectData.Direction] = 1;
            world.SetObjectPosition(paperGirl, new ObjectPosition(35 * 16 + 8, 10 * 16 + 8, 1));
        }

        var goal = RouteExecutor.SelectDestination(world, paperGirl, portal, slot8);

        Assert.True(goal.HasValue, $"Goal was null. Reason: '{world.LastRouteCandidateFailureReason}'");
        Assert.NotEqual("outside routable tile bounds", world.LastRouteCandidateFailureReason);

        var arrivalFacing = RouteExecutor.RelativeArrivalFacing(
            world, paperGirl, portal, slot8, goal.Value.Position, goal.Value.SearchDirection);
        Assert.Equal(1, arrivalFacing);
    }

    [Fact]
    public void IsWalkableCandidate_PerimeterBoundaries_PortalsAllowed_NonPortalsStrictlyGated()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = CreateEmptyRoomMap(64, 64);

        var person = world.Add(new XPerson(999, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.TypeRecord = new TypeRecord(999) { FootprintMask = 0 };
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.Direction] = 1;
        world.SetObjectPosition(person, new ObjectPosition(32 * 16 + 8, 32 * 16 + 8, 1));

        var slot = new RoutingSlotRequest
        {
            Flags = 0x0100,
            SnapTargetSlot = 0,
            Facing = -3
        };

        var perimeterCoords = new (int X, int Y, string Label)[]
        {
            (0, 10, "Perimeter (0, y) - Left edge"),
            (10, 0, "Perimeter (x, 0) - Top edge"),
            (63, 10, "Perimeter (Width-1, y) - Right edge"),
            (10, 63, "Perimeter (x, Height-1) - Bottom edge")
        };

        foreach (var (tileX, tileY, label) in perimeterCoords)
        {
            var pos = new ObjectPosition(tileX * 16 + 8, tileY * 16 + 8, 1);

            var nonPortal = world.Add(new XObject(100, PrimitiveTestWorld.Definition()));
            nonPortal.TypeRecord = new TypeRecord(0x12345678);
            nonPortal.RuntimeClass = ObjectClass.Object;
            nonPortal.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 1 });
            world.SetObjectPosition(nonPortal, pos);

            var nonPortalGoal = RouteExecutor.SelectDestination(world, person, nonPortal, slot);
            Assert.Null(nonPortalGoal);
            Assert.Equal("outside routable tile bounds", world.LastRouteCandidateFailureReason);
            world.Remove(nonPortal.Id);

            var portalByKey = world.Add(new XObject(101, PrimitiveTestWorld.Definition()));
            portalByKey.TypeRecord = new TypeRecord(unchecked((int)0x81E6BEF9));
            portalByKey.RuntimeClass = ObjectClass.Object;
            world.SetObjectPosition(portalByKey, pos);

            RouteExecutor.SelectDestination(world, person, portalByKey, slot);
            Assert.NotEqual("outside routable tile bounds", world.LastRouteCandidateFailureReason);
            world.Remove(portalByKey.Id);

            var portalByClass = world.Add(new XObject(102, PrimitiveTestWorld.Definition()));
            portalByClass.TypeRecord = new TypeRecord(unchecked((int)0x99999999));
            portalByClass.RuntimeClass = ObjectClass.Portal;
            world.SetObjectPosition(portalByClass, pos);

            RouteExecutor.SelectDestination(world, person, portalByClass, slot);
            Assert.NotEqual("outside routable tile bounds", world.LastRouteCandidateFailureReason);
            world.Remove(portalByClass.Id);
        }
    }

    [Fact]
    public void IsWalkableCandidate_OutOfBounds_AlwaysRejectedEvenForPortals()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = CreateEmptyRoomMap(64, 64);

        var person = world.Add(new XPerson(999, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person.TypeRecord = new TypeRecord(999) { FootprintMask = 0 };
        person[ObjectData.AllowedHeightFlags] = 1;
        world.SetObjectPosition(person, new ObjectPosition(32 * 16 + 8, 32 * 16 + 8, 1));

        var slot = new RoutingSlotRequest { Flags = 0x0100, Facing = -3 };

        var outOfBoundsCoords = new (int X, int Y, string Label)[]
        {
            (-1, 10, "Outside left (-1, y)"),
            (64, 10, "Outside right (Width, y)"),
            (10, -1, "Outside top (x, -1)"),
            (10, 64, "Outside bottom (x, Height)")
        };

        foreach (var (tileX, tileY, label) in outOfBoundsCoords)
        {
            var pos = new ObjectPosition(tileX * 16 + 8, tileY * 16 + 8, 1);
            var portal = world.Add(new XObject(200, PrimitiveTestWorld.Definition()));
            portal.TypeRecord = new TypeRecord(unchecked((int)0x81E6BEF9));
            portal.RuntimeClass = ObjectClass.Portal;
            world.SetObjectPosition(portal, pos);

            var goal = RouteExecutor.SelectDestination(world, person, portal, slot);
            Assert.Null(goal);
            Assert.Equal("outside map tile bounds", world.LastRouteCandidateFailureReason);
            world.Remove(portal.Id);
        }
    }
}
