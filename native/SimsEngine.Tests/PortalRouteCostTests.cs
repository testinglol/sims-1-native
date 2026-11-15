using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class PortalRouteCostTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void MissingOrInvalidRouteCostReadsReturnTheExecutableSentinel(short routeId)
    {
        var portal = new XObject(1, new short[XObject.DefinitionCount]);

        Assert.Equal(XObject.UnallocatedPortalRouteCost, portal.PortalRouteCostAt(routeId));
    }

    [Fact]
    public void UnallocatedRouteCostMatchesTheOriginalExecutableConstantBits()
    {
        Assert.Equal(0, BitConverter.SingleToInt32Bits(XObject.UnallocatedPortalRouteCost));
    }

    [Fact]
    public void PortalTransitionDistanceUsesSourceHorizontalAndVerticalScales()
    {
        var from = new XObject(1, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(0, 0, 1) };
        var to = new XObject(2, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(32, 0, 1) };

        Assert.Equal(2f, RouteExecutor.PortalTransitionDistanceCost(from, to));
        to.Position = to.Position with { Height = 2 };
        Assert.Equal(MathF.Sqrt(404f), RouteExecutor.PortalTransitionDistanceCost(from, to));
    }

    [Fact]
    public void ObjectTypeSevenUsesTheExecutableZeroTransitionCost()
    {
        var from = new XObject(1, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(0, 0, 1) };
        var special = new XObject(2, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(160, 160, 3) };
        special.TypeRecord = new TypeRecord(2) { ObjectType = 7 };

        Assert.Equal(0f, RouteExecutor.PortalTransitionDistanceCost(from, special));
    }

    [Fact]
    public void PortalProjectionRelaxesSameRoomCostsAndCachesUnreachableFrontier()
    {
        var world = new SimulationWorld();
        var origin = world.Add(Portal(1, 0, 10));
        var sameRoom = world.Add(Portal(2, 32, 10));
        var otherRoom = world.Add(Portal(3, 48, 11));

        var routeId = world.InitializePortalRouteCosts(origin);
        var distance = RouteExecutor.ProjectPortalRouteDistance(world, origin, sameRoom);

        Assert.True(routeId > 0);
        Assert.Equal(3f, distance);
        Assert.Equal(0f, origin.PortalRouteCostAt(routeId));
        Assert.Equal(3f, sameRoom.PortalRouteCostAt(routeId));
        Assert.Equal(10000f, otherRoom.PortalRouteCostAt(routeId));
        sameRoom.Position = sameRoom.Position with { X = 160 };
        Assert.Equal(3f, RouteExecutor.ProjectPortalRouteDistance(world, origin, sameRoom));
        Assert.Equal(3f, sameRoom.PortalRouteCostAt(routeId));
    }

    [Fact]
    public void PortalProjectionRelaxesAcrossTheLinkedFunctionPart()
    {
        var world = new SimulationWorld();
        var origin = world.Add(Portal(1, 0, 10));
        var linked = world.Add(Portal(2, 160, 11));
        linked.TypeRecord = new TypeRecord(2)
        {
            FunctionTable = Enumerable.Repeat((ushort)0, 0x10)
                .Select((value, index) => index == 0x0f ? (ushort)4096 : value).ToArray(),
        };
        origin.MultiTileNextId = linked.Id;

        var routeId = world.InitializePortalRouteCosts(origin);
        var distance = RouteExecutor.ProjectPortalRouteDistance(world, origin, linked);

        Assert.Equal(11f, distance);
        Assert.Equal(0f, origin.PortalRouteCostAt(routeId));
        Assert.Equal(11f, linked.PortalRouteCostAt(routeId));
    }

    [Fact]
    public void PortalFunctionLookupFallsBackToTheMasterWhenTheChainEnds()
    {
        // FUN_00463804 takes +0x18c and, when it is empty, +0x190: a stair's last part reaches the function part through its master.
        var world = new SimulationWorld();
        var function = world.Add(FunctionPortal(1, 0, 10));
        var tail = world.Add(Portal(2, 160, 11));
        function.MultiTileNextId = tail.Id;
        tail.MultiTileMasterId = function.Id;

        Assert.Equal(function, RouteExecutor.PortalFunction(world, tail)?.FunctionObject);
    }

    [Fact]
    public void EachPortalProjectionOriginGetsAnIndependentRouteId()
    {
        var world = new SimulationWorld();
        var first = world.Add(Portal(1, 0, 10));
        var second = world.Add(Portal(2, 32, 10));

        var firstDistance = RouteExecutor.ProjectPortalRouteDistance(world, first, second);
        var secondDistance = RouteExecutor.ProjectPortalRouteDistance(world, second, first);
        var firstRouteId = first[ObjectData.RouteId];
        var secondRouteId = second[ObjectData.RouteId];

        Assert.Equal(3f, firstDistance);
        Assert.Equal(3f, secondDistance);
        Assert.NotEqual(firstRouteId, secondRouteId);
        Assert.Equal(0f, first.PortalRouteCostAt(firstRouteId));
        Assert.Equal(0f, second.PortalRouteCostAt(secondRouteId));
        Assert.Equal(3f, second.PortalRouteCostAt(firstRouteId));
        Assert.Equal(3f, first.PortalRouteCostAt(secondRouteId));
    }

    [Fact]
    public void PortalPlacementInvalidatesAndRebuildsPerOriginProjection()
    {
        var world = new SimulationWorld();
        var origin = world.Add(Portal(1, 0, 10));
        var movedPeer = world.Add(Portal(2, 32, 10));
        var routeId = world.InitializePortalRouteCosts(origin);
        Assert.Equal(3f, RouteExecutor.ProjectPortalRouteDistance(world, origin, movedPeer));
        Assert.Equal(3f, movedPeer.PortalRouteCostAt(routeId));
        world.SetObjectPosition(movedPeer, movedPeer.Position with { X = 64 });

        world.PlaceObject(origin, origin.Position);

        Assert.Equal(XObject.UninitializedPortalRouteCost, origin.PortalRouteCostAt(routeId));
        Assert.Equal(XObject.UninitializedPortalRouteCost, movedPeer.PortalRouteCostAt(routeId));
        var projectedDistance = RouteExecutor.ProjectPortalRouteDistance(world, origin, movedPeer);
        var dx = (float)origin.Position.X - movedPeer.Position.X;
        var dy = (float)origin.Position.Y - movedPeer.Position.Y;
        var dz = (movedPeer.Position.Height - origin.Position.Height) * 20f;
        var rebuiltCost = movedPeer.PortalRouteCostAt(routeId);
        Assert.Equal(rebuiltCost, projectedDistance);
        Assert.Equal(MathF.Sqrt((dx * dx + dy * dy) / 256f + dz * dz) + 1f, rebuiltCost);
        Assert.NotEqual(3f, rebuiltCost);
    }

    [Fact]
    public void DestinationSelectionRanksNothingWhenNoCurrentRoomPortalReachesTheDestinationRoom()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(0, 0, 1),
            RoomWord84 = 10,
        });
        var destination = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(320, 0, 1),
            RoomWord84 = 20,
        });
        world.Add(Portal(3, 0, 10));
        world.Add(Portal(4, 320, 20));
        world.InitializePortalRouteCosts(person);

        Assert.Null(RouteExecutor.SelectPortalForDestination(world, person, destination, 10, 20));
    }

    [Fact]
    public void DestinationSelectionUsesSourceProjectionCostAndEndpointDistances()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(0, 0, 1),
            RoomWord84 = 10,
        });
        var destination = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(320, 0, 1),
            RoomWord84 = 20,
        });
        var nearPortal = world.Add(Portal(3, 0, 10));
        var farPortal = world.Add(Portal(4, 160, 10));
        var nearFunctionPart = world.Add(FunctionPortal(5, 320, 20));
        var farFunctionPart = world.Add(FunctionPortal(6, 480, 20));
        var nearDestinationPortal = world.Add(Portal(7, 320, 20));
        var farDestinationPortal = world.Add(Portal(8, 480, 20));
        nearDestinationPortal.MultiTileNextId = nearFunctionPart.Id;
        farDestinationPortal.MultiTileNextId = farFunctionPart.Id;
        world.InitializePortalRouteCosts(person);

        // FUN_00463dd9 projects from each current-room portal; a link to the destination room is required.
        nearPortal.MultiTileNextId = nearFunctionPart.Id;
        farPortal.MultiTileNextId = farFunctionPart.Id;
        var selected = RouteExecutor.SelectPortalForDestination(world, person, destination, 10, 20);

        Assert.Same(nearPortal, selected);
        Assert.True(nearPortal.PortalRouteCostAt(person[ObjectData.RouteId]) <
                    farPortal.PortalRouteCostAt(person[ObjectData.RouteId]));
    }

    [Fact]
    public void DestinationDistanceCapRemainsAnEligiblePortalRouteCost()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(0, 0, 1),
            RoomWord84 = 10,
        });
        var destination = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(32000, 0, 1),
            RoomWord84 = 20,
        });
        var source = world.Add(Portal(3, 0, 10));
        var linkedFunction = world.Add(FunctionPortal(4, 16000, 20));
        var destinationPortal = world.Add(Portal(5, 0, 20));
        source.MultiTileNextId = linkedFunction.Id;
        destinationPortal.MultiTileNextId = linkedFunction.Id;
        world.InitializePortalRouteCosts(person);

        var selected = RouteExecutor.SelectPortalForDestination(world, person, destination, 10, 20);

        Assert.Same(source, selected);
        Assert.Equal(1000f, source.PortalRouteCostAt(person[ObjectData.RouteId]));
        Assert.Equal(1000f, source.PortalRouteCostAt(person[ObjectData.RouteId]));
    }

    private static XObject FunctionPortal(short id, int x, short room)
    {
        var function = Portal(id, x, room);
        function.TypeRecord = new TypeRecord(id)
        {
            FunctionTable = Enumerable.Repeat((ushort)0, 0x10)
                .Select((value, index) => index == 0x0f ? (ushort)4096 : value).ToArray(),
        };
        return function;
    }

    private static XObject Portal(short id, int x, short room) => new(id, PrimitiveTestWorld.Definition())
    {
        RuntimeClass = ObjectClass.Portal,
        TypeRecord = PortalRecord(id),
        Position = new ObjectPosition(x, 0, 1),
        RoomWord84 = room,
    };

    private static TypeRecord PortalRecord(short id)
    {
        var functions = new ushort[0x10];
        functions[0x0f] = 4096;
        return new TypeRecord(id) { FunctionTable = functions };
    }

    [Fact]
    public void RouteCostWriteGrowsTheOneBasedVectorWithUninitializedSentinels()
    {
        var portal = new XObject(1, new short[XObject.DefinitionCount]);

        portal.SetPortalRouteCost(3, 12.5f);

        Assert.Equal(XObject.UninitializedPortalRouteCost, portal.PortalRouteCostAt(1));
        Assert.Equal(XObject.UninitializedPortalRouteCost, portal.PortalRouteCostAt(2));
        Assert.Equal(12.5f, portal.PortalRouteCostAt(3));
        Assert.Equal(XObject.UnallocatedPortalRouteCost, portal.PortalRouteCostAt(4));
    }

    [Fact]
    public void RouteInitializationWritesTheRouteSlotToEveryLivePortal()
    {
        var world = new SimulationWorld();
        var occupiedObject = world.Add(new XObject(1, new short[XObject.DefinitionCount]));
        occupiedObject[ObjectData.RouteId] = 1;
        var owner = world.Add(new XObject(2, new short[XObject.DefinitionCount]));
        var firstPortal = world.Add(new XObject(3, new short[XObject.DefinitionCount])
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var secondPortal = world.Add(new XObject(4, new short[XObject.DefinitionCount])
        {
            RuntimeClass = ObjectClass.Portal,
        });
        secondPortal[ObjectData.RouteId] = 3;

        Assert.Equal((short)2, world.InitializePortalRouteCosts(owner));

        Assert.True(firstPortal.PortalRouteCosts.ContainsKey(2));
        Assert.True(secondPortal.PortalRouteCosts.ContainsKey(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, firstPortal.PortalRouteCostAt(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, secondPortal.PortalRouteCostAt(2));
    }

    [Fact]
    public void RouteInitializationResetsStaleCostsEvenWhenTheOwnerAlreadyHasAnId()
    {
        var world = new SimulationWorld();
        var owner = world.Add(new XObject(1, new short[XObject.DefinitionCount]));
        owner[ObjectData.RouteId] = 4;
        var firstPortal = world.Add(new XObject(2, new short[XObject.DefinitionCount])
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var secondPortal = world.Add(new XObject(3, new short[XObject.DefinitionCount])
        {
            RuntimeClass = ObjectClass.Portal,
        });
        firstPortal.SetPortalRouteCost(4, 8f);
        secondPortal.SetPortalRouteCost(4, 12f);

        Assert.Equal((short)4, world.InitializePortalRouteCosts(owner));

        Assert.Equal(XObject.UninitializedPortalRouteCost, firstPortal.PortalRouteCostAt(4));
        Assert.Equal(XObject.UninitializedPortalRouteCost, secondPortal.PortalRouteCostAt(4));
    }

    [Fact]
    public void PlacingAPortalResetsEveryAllocatedLivePortalRouteCost()
    {
        var world = new SimulationWorld();
        var placed = world.Add(new XObject(1, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var peer = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var unrelated = world.Add(new XObject(3, PrimitiveTestWorld.Definition()));
        placed.SetPortalRouteCost(2, 1004f);
        peer.SetPortalRouteCost(2, 1101f);
        unrelated.SetPortalRouteCost(2, 17f);

        world.PlaceObject(placed, new ObjectPosition(32, 48, 1));

        Assert.Equal(XObject.UninitializedPortalRouteCost, placed.PortalRouteCostAt(1));
        Assert.Equal(XObject.UninitializedPortalRouteCost, placed.PortalRouteCostAt(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, peer.PortalRouteCostAt(1));
        Assert.Equal(XObject.UninitializedPortalRouteCost, peer.PortalRouteCostAt(2));
        Assert.Equal(17f, unrelated.PortalRouteCostAt(2));
    }

    [Theory]
    [InlineData(0, 2u, 1, 0, 8u)]
    [InlineData(1, 2u, 1, 0, 8u)]
    [InlineData(2, 4u, 2, 1, 1u)]
    [InlineData(3, 4u, 2, 1, 1u)]
    [InlineData(4, 8u, 1, 2, 2u)]
    [InlineData(5, 8u, 1, 2, 2u)]
    [InlineData(6, 1u, 0, 1, 4u)]
    [InlineData(7, 1u, 0, 1, 4u)]
    public void PortalPlacementRefreshesItsWallStyleAndReciprocalSegment(short direction, uint wallBit,
        int neighborX, int neighborY, uint neighborBit)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomWithPortalWall(wallBit);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = world.Add(new XObject(1, definition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 24, 1),
        });
        portal[ObjectData.WallPlacementFlags] = 1;
        portal[ObjectData.Direction] = direction;

        world.PlaceObject(portal, portal.Position);

        var walls = world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value;
        Assert.Equal((ushort)1, walls.WallCategory(wallBit));
        Assert.Equal(0x10u, walls.UInt32At(0x28) & 0x10);
        Assert.Contains(1, world.PortalGeometryRefreshRequestedChannels);
        Assert.Empty(world.PortalWallRefreshRequestedChannels);
        var neighbor = world.LotRoomMaps[1].ExpandedWallAt(neighborX, neighborY)!.Value;
        Assert.Equal(neighborBit, neighbor.UInt32At(0) & neighborBit);
        var neighborCategoryBit = wallBit switch { 4u => 1u, 8u => 2u, _ => 0u };
        if (neighborCategoryBit != 0)
            Assert.Equal((ushort)1, neighbor.WallCategory(neighborCategoryBit));
    }

    [Fact]
    public void PortalRemovalRestoresDefaultWallCategoryBeforeRequestingRefresh()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = RoomWithPortalWall(2u);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 13;
        var portal = world.Add(new XObject(1, definition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 24, 1),
        });
        portal[ObjectData.WallPlacementFlags] = 1;
        world.PlaceObject(portal, portal.Position);
        Assert.Equal((ushort)13, world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value.WallCategory(2u));
        world.ConsumePortalWallRefreshRequests();

        var observed = new List<(PortalWallRefreshCoordinate Coordinate, bool ChannelSet, ushort Category)>();
        world.PortalWallRefreshRequested += coordinate => observed.Add((coordinate,
            world.PortalWallRefreshRequestedChannels.Contains(1),
            world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value.WallCategory(2u)));

        world.BeginObjectRemoval(portal, 1);

        Assert.Equal((ushort)1, world.LotRoomMaps[1].ExpandedWallAt(1, 1)!.Value.WallCategory(2u));
        Assert.Equal([(new PortalWallRefreshCoordinate(62, 1, 1), true, (ushort)1)], observed);
        Assert.Equal(new ObjectPosition(-16, -16, 1), portal.Position);
    }

    [Fact]
    public void PortalRemovalInvalidatesEveryAllocatedRouteCostVector()
    {
        var world = new SimulationWorld();
        var portal = world.Add(new XObject(1, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 24, 1),
        });
        var peer = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var person = world.Add(new XPerson(3, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        portal.SetPortalRouteCost(2, 3f);
        peer.SetPortalRouteCost(2, 5f);
        person.SetPortalRouteCost(2, 7f);

        world.BeginObjectRemoval(portal, 0);

        Assert.Equal(XObject.UninitializedPortalRouteCost, portal.PortalRouteCostAt(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, peer.PortalRouteCostAt(2));
        Assert.Equal(XObject.UninitializedPortalRouteCost, person.PortalRouteCostAt(2));
    }

    [Fact]
    public void LevelZeroPortalPlacementRequestsBothGeometryRefreshChannels()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[0] = RoomWithPortalWall(2u);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = world.Add(new XObject(1, definition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 24, 0),
        });
        portal[ObjectData.WallPlacementFlags] = 1;

        world.PlaceObject(portal, portal.Position);

        Assert.Equal(new[] { 1, 2 }, world.PortalGeometryRefreshRequestedChannels.Order().ToArray());
        Assert.Empty(world.PortalWallRefreshRequestedChannels);
    }

    [Fact]
    public void PortalGeometryRefreshConsumptionReturnsAndClearsPendingLevels()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[0] = RoomWithPortalWall(2u);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = world.Add(new XObject(1, definition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 24, 0),
        });
        portal[ObjectData.WallPlacementFlags] = 1;
        world.PlaceObject(portal, portal.Position);

        Assert.Equal(new[] { 1, 2 }, world.PortalGeometryRefreshRequestedChannels.Order().ToArray());
        Assert.Equal(new[] { 1, 2 }, world.ConsumePortalGeometryRefreshRequests());
        Assert.Empty(world.PortalGeometryRefreshRequestedChannels);
        Assert.Empty(world.ConsumePortalGeometryRefreshRequests());
    }

    [Theory]
    [InlineData(1, 62, 2)]
    [InlineData(2, 61, 62)]
    [InlineData(3, 1, 61)]
    public void PortalWallRefreshNotifiesListenersWithTheLevelMappedCoordinate(int level, byte first, byte second)
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[level] = RoomWithPortalWall(2u, 1, 2);
        var definition = PrimitiveTestWorld.Definition();
        definition[(int)ObjdField.WallStyle] = 1;
        var portal = world.Add(new XObject(1, definition)
        {
            RuntimeClass = ObjectClass.Portal,
            Position = new ObjectPosition(24, 40, level),
        });
        portal[ObjectData.WallPlacementFlags] = 1;
        var observed = new List<(PortalWallRefreshCoordinate Coordinate, bool ChannelSet, ushort Category)>();
        world.PortalWallRefreshRequested += coordinate => observed.Add((coordinate,
            world.PortalWallRefreshRequestedChannels.Contains(level),
            world.LotRoomMaps[level].ExpandedWallAt(1, 2)!.Value.WallCategory(2)));

        world.BeginObjectRemoval(portal, 1);

        Assert.Equal([(new PortalWallRefreshCoordinate(first, second, (byte)level), true, (ushort)1)], observed);
        Assert.Empty(world.PortalGeometryRefreshRequestedChannels);
    }

    [Fact]
    public void ReusingARankedPortalMarksItUsedOnItsFunctionPart()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenRoom();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[ObjectData.RouteId] = 4;
        var selected = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(2),
            Position = new ObjectPosition(24, 24, 1),
        });
        var peer = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(3),
            Position = new ObjectPosition(40, 24, 1),
        });
        var functionPart = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(4),
            Position = new ObjectPosition(24, 24, 1),
        });
        selected.SetPortalRouteCost(4, 1001f);
        peer.SetPortalRouteCost(4, 1007f);
        functionPart.SetPortalRouteCost(4, 1101f);

        RouteExecutor.RecordPortalRouteCostAfterSelection(world, person, selected, functionPart);

        Assert.Equal(1101f, selected.PortalRouteCostAt(4));
        Assert.Equal(1101f, functionPart.PortalRouteCostAt(4));
    }

    [Fact]
    public void FirstPortalUseTakesTheGreatestEstablishedRankInItsRoom()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenRoom();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        person[ObjectData.RouteId] = 4;
        var selected = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(2),
            Position = new ObjectPosition(24, 24, 1),
        });
        var peer = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(3),
            Position = new ObjectPosition(40, 24, 1),
        });
        var functionPart = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(4),
            Position = selected.Position,
        });
        peer.SetPortalRouteCost(4, 1007f);

        RouteExecutor.RecordPortalRouteCostAfterSelection(world, person, selected, functionPart);

        Assert.Equal(1007f, selected.PortalRouteCostAt(4));
        Assert.Equal(1007f, functionPart.PortalRouteCostAt(4));
    }

    [Fact]
    public void FirstUseRankPropagationGroupsByStoredRoomWordNotMapProjection()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenRoom();
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[ObjectData.RouteId] = 4;
        var selected = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(2),
            Position = new ObjectPosition(24, 24, 1),
            RoomWord84 = 10,
        });
        var sameStoredRoom = world.Add(new XObject(3, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(3),
            Position = new ObjectPosition(40, 24, 1),
            RoomWord84 = 10,
        });
        var sameMapDifferentStoredRoom = world.Add(new XObject(4, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(4),
            Position = new ObjectPosition(56, 24, 1),
            RoomWord84 = 11,
        });
        var functionPart = world.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
            TypeRecord = PortalRecord(5),
            Position = selected.Position,
            RoomWord84 = 10,
        });
        sameStoredRoom.SetPortalRouteCost(4, 1007f);
        sameMapDifferentStoredRoom.SetPortalRouteCost(4, 1090f);

        RouteExecutor.RecordPortalRouteCostAfterSelection(world, person, selected, functionPart);

        Assert.Equal(1007f, selected.PortalRouteCostAt(4));
        Assert.Equal(1007f, functionPart.PortalRouteCostAt(4));
    }

    [Fact]
    public void RouteSwitchMarksThePreviousPortalAndItsFunctionPartUsed()
    {
        var world = new SimulationWorld();
        var portal = world.Add(new XObject(1, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        var functionPart = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            RuntimeClass = ObjectClass.Portal,
        });
        portal.SetPortalRouteCost(4, 1007f);
        functionPart.SetPortalRouteCost(4, 1007f);

        RouteExecutor.MarkPortalRouteCostUsed(world, portal.Id, functionPart.Id, 4);

        Assert.Equal(1101f, portal.PortalRouteCostAt(4));
        Assert.Equal(1101f, functionPart.PortalRouteCostAt(4));
    }

    private static SimsEngine.World.RoomMap OpenRoom()
    {
        var walls = new ChunkBuilder().S16(0).S16(0).S32(4).S32(4).S32(8).U32(0);
        for (var tile = 0; tile < 4 * 4; tile++) walls.Raw(new byte[8]);
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, walls.ToArray())), "portal-cost-room.iff");
        return SimsEngine.World.RoomMap.Build(file, 1, 4, 4);
    }

    private static SimsEngine.World.RoomMap RoomWithPortalWall(uint wallBit, int wallX = 1, int wallY = 1)
    {
        var body = new ChunkBuilder().S16(0).S16(0).S32(4).S32(4).S32(8).U32(0);
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 4; y++)
        {
            var segments = (x, y) switch
            {
                (var currentX, var currentY) when currentX == wallX && currentY == wallY => (byte)wallBit,
                (var currentX, var currentY) when currentX == wallX - 1 && currentY == wallY && wallBit == 1 => (byte)4,
                (var currentX, var currentY) when currentX == wallX && currentY == wallY - 1 && wallBit == 2 => (byte)8,
                (var currentX, var currentY) when currentX == wallX + 1 && currentY == wallY && wallBit == 4 => (byte)1,
                (var currentX, var currentY) when currentX == wallX && currentY == wallY + 1 && wallBit == 8 => (byte)2,
                _ => (byte)0,
            };
            body.Raw([segments, 0x0f, 1, 1, 0, 0, 0, 0]);
        }
        var file = new IffFile(ChunkBuilder.File(("Arry", 2, body.ToArray())), "portal-wall-room.iff");
        return SimsEngine.World.RoomMap.Build(file, 1, 4, 4);
    }
}
