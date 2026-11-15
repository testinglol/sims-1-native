using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class RelativeArrivalFacingTests
{
    [Fact]
    public void ArrivalFacingMinusThree_PreservesExistingTransitHeading()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        person[ObjectData.Direction] = 5; // Sim transit heading (5)

        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        anchor[ObjectData.Direction] = 2; // Anchor direction (2)

        var slot = new RoutingSlotRequest { Facing = -3, Flags = 0xA0FF };
        var facing = RouteExecutor.RelativeArrivalFacing(
            context.World, person, anchor, slot, new ObjectPosition(100, 100, 1), null);

        // Mac XRouting.s:1280 and Sims.exe FUN_00483fc1 return immediately on Facing -3, preserving heading
        Assert.Equal(5, facing);
    }

    [Fact]
    public void ArrivalFacingMinusTwo_FacesDirectlyTowardAnchorPosition()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        anchor[ObjectData.Direction] = 2;

        var slot = new RoutingSlotRequest { Facing = -2, Flags = 0x1 };

        // 1. Sim destination is South (+Y) of anchor at (10, 12). Direction from (10, 12) to (10, 10) is deltaY = -2 -> 0 (FUN_00452129)
        var destSouth = new ObjectPosition(10 * 16 + 8, 12 * 16 + 8, 1);
        var facingNorth = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, destSouth, null);
        Assert.Equal(0, facingNorth);

        // 2. Sim destination is West (-X) of anchor at (8, 10). Direction from (8, 10) to (10, 10) is deltaX = +2 -> 2 (FUN_00452129)
        var destWest = new ObjectPosition(8 * 16 + 8, 10 * 16 + 8, 1);
        var facingEast = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, destWest, null);
        Assert.Equal(2, facingEast);

        // 3. Sim destination is North (-Y) of anchor at (10, 8). Direction from (10, 8) to (10, 10) is deltaY = +2 -> 4 (FUN_00452129)
        var destNorth = new ObjectPosition(10 * 16 + 8, 8 * 16 + 8, 1);
        var facingSouth = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, destNorth, null);
        Assert.Equal(4, facingSouth);

        // 4. Sim destination is East (+X) of anchor at (12, 10). Direction from (12, 10) to (10, 10) is deltaX = -2 -> 6 (FUN_00452129)
        var destEast = new ObjectPosition(12 * 16 + 8, 10 * 16 + 8, 1);
        var facingWest = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, destEast, null);
        Assert.Equal(6, facingWest);

        // 5. Sim destination is on the exact anchor tile (10, 10). Falls back to opposite anchor direction: (2 + 4) & 7 = 6
        var destCenter = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1);
        var facingOpposite = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, destCenter, null);
        Assert.Equal(6, facingOpposite);
    }

    [Fact]
    public void ArrivalFacingMinusThree_StressTest_AllEightDirections_PreservesSimHeading()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));

        var slot = new RoutingSlotRequest { Facing = -3, Flags = 0xA0FF };

        for (short simDir = 0; simDir < 8; simDir++)
        {
            person[ObjectData.Direction] = simDir;

            for (short anchorDir = 0; anchorDir < 8; anchorDir++)
            {
                anchor[ObjectData.Direction] = anchorDir;

                // Test arbitrary destination points and search directions
                var dest = new ObjectPosition((simDir + 10) * 16, (anchorDir + 10) * 16, 1);
                var facing = RouteExecutor.RelativeArrivalFacing(
                    context.World, person, anchor, slot, dest, routeSearchDirection: (short)((simDir + 3) & 7));

                Assert.Equal(simDir, facing);
            }
        }
    }

    [Fact]
    public void ArrivalFacingMinusTwo_StressTest_AllRelativeOffsets_FacesDirectlyTowardAnchor()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        var anchorX = 10 * 16 + 8;
        var anchorY = 10 * 16 + 8;
        context.World.SetObjectPosition(anchor, new ObjectPosition(anchorX, anchorY, 1));
        anchor[ObjectData.Direction] = 0;

        var slot = new RoutingSlotRequest { Facing = -2, Flags = 0x1 };

        // Test all 8 cardinal/diagonal relative offsets from destination towards anchor (FUN_00452129)
        var testCases = new (int OffsetX, int OffsetY, short ExpectedFacing, string Label)[]
        {
            (0, -32, 4, "North of anchor -> faces South (4)"),
            (32, -32, 5, "NE of anchor -> faces SW (5)"),
            (32, 0, 6, "East of anchor -> faces West (6)"),
            (32, 32, 7, "SE of anchor -> faces NW (7)"),
            (0, 32, 0, "South of anchor -> faces North (0)"),
            (-32, 32, 1, "SW of anchor -> faces NE (1)"),
            (-32, 0, 2, "West of anchor -> faces East (2)"),
            (-32, -32, 3, "NW of anchor -> faces SE (3)"),
        };

        foreach (var (offsetX, offsetY, expectedFacing, label) in testCases)
        {
            var dest = new ObjectPosition(anchorX + offsetX, anchorY + offsetY, 1);
            var facing = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, dest, null);
            Assert.True(expectedFacing == facing,
                $"Failed for {label}: expected facing {expectedFacing}, got {facing}");
        }
    }

    [Fact]
    public void ArrivalFacingMinusTwo_StressTest_CoLocatedDestination_ReversesAnchorDirection()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        var anchorPos = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1);
        context.World.SetObjectPosition(anchor, anchorPos);

        var slot = new RoutingSlotRequest { Facing = -2, Flags = 0x1 };

        for (short anchorDir = 0; anchorDir < 8; anchorDir++)
        {
            anchor[ObjectData.Direction] = anchorDir;
            var expectedFacing = (short)((anchorDir + 4) & 7);

            var facing = RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slot, anchorPos, null);
            Assert.Equal(expectedFacing, facing);
        }
    }
}
