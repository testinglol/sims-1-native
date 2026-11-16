using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave3ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static RoomMap OpenRoom(int width = 16, int height = 16)
    {
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(new byte[width * height * 8]);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "wave3-open-room.iff"), 1, width, height);
    }

    private static RoomMap PartitionedRoomWithGap(int width = 16, int height = 16, int wallX = 6,
        int gapMinY = 4, int gapMaxY = 6)
    {
        var wallBytes = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
        {
            if (y >= gapMinY && y <= gapMaxY) continue;
            var rightIndex = (wallX * height + y) * 8;
            wallBytes[rightIndex] = 0x01;
            wallBytes[rightIndex + 2] = 1;
            if (wallX > 0)
            {
                var leftIndex = ((wallX - 1) * height + y) * 8;
                wallBytes[leftIndex] |= 0x04;
            }
        }
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)1, width * height).ToArray());
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(wallBytes);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "wave3-gap-room.iff"), 1, width, height);
    }

    private static List<short> CollectSetToNextTileWalk(SimulationWorld world, XObject runner, short initialTargetId)
    {
        ReadOnlySpan<byte> walkTileOperand = [0, 0, 0, 0, 8, 0, 0, 0];
        var frame = new StackFrame(initialTargetId, [], []);
        var context = new TreeContext(world, runner, frame);
        var visited = new List<short>();
        for (var step = 0; step < 8; step++)
        {
            var result = SetToNextPrimitive.Execute(context, walkTileOperand);
            if (result != PrimitiveResult.True) break;
            visited.Add(frame.StackObject);
            if (frame.StackObject == initialTargetId) break;
        }
        return visited;
    }

    [Fact]
    public void FloorTileOverlap_WalkingPersonsAcrossFloorObjectPreservesSlotsZeroSetToNextAndRestoresTileRoot()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = OpenRoom();
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var personA = (XPerson)context.Me;
        personA.TypeRecord = new TypeRecord(1) { ObjectType = XPerson.PersonType };
        if (personA.Slots.Count == 0) personA.Slots.Add(0);
        personA[ObjectData.AllowedHeightFlags] = 1;
        personA[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(personA, new ObjectPosition(4 * 16 + 8, 5 * 16 + 8, 1));

        var personB = world.Add(new XPerson(30, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(30) { ObjectType = XPerson.PersonType },
        });
        personB.Slots.Add(0);
        personB[ObjectData.AllowedHeightFlags] = 1;
        personB[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(personB, new ObjectPosition(6 * 16 + 8, 7 * 16 + 8, 1));

        var puddle = world.Add(new XObject(40, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(40),
        });
        puddle.Slots.Add(0);
        puddle[ObjectData.AllowedHeightFlags] = 1;
        puddle[ObjectData.Flags] = 4;
        world.PlaceObject(puddle, new ObjectPosition(6 * 16 + 8, 5 * 16 + 8, 1));

        Assert.Equal(puddle.ClockManagerIndex, world.TileValueAt(6, 5, 1));
        Assert.Equal([puddle], world.ObjectsAtTile(6, 5, 1).ToArray());

        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(2),
        });
        target.Slots.Add(0);
        target[ObjectData.Flags] = 4;
        world.PlaceObject(target, new ObjectPosition(8 * 16 + 8, 5 * 16 + 8, 1));

        var sawPersonOnPuddleTile = false;
        var verifiedMultiPersonOverlap = false;
        var routeResult = RouteExecutor.Start(world, personA, context.Frame, target, new RoutingSlotRequest(), null);
        for (var step = 0; step < 160 && routeResult == PrimitiveResult.Stop; step++)
        {
            routeResult = RouteExecutor.Continue(world, personA, context.Frame, null);
            if ((personA.Position.X >> 4) == 6 && (personA.Position.Y >> 4) == 5)
            {
                sawPersonOnPuddleTile = true;
                Assert.Equal(personA.ClockManagerIndex, world.TileValueAt(6, 5, 1));
                Assert.Equal(puddle.ClockManagerIndex, personA.Slots[0]);
                Assert.Equal(personA.ClockManagerIndex, puddle[ObjectData.ParentId]);
                Assert.Equal((short)0, puddle[ObjectData.SlotHierNumber]);
                Assert.Equal([personA, puddle], world.ObjectsAtTile(6, 5, 1).ToArray());

                var singleSimWalk = CollectSetToNextTileWalk(world, personA, personA.StackObjectKey);
                Assert.Equal([puddle.StackObjectKey, personA.StackObjectKey], singleSimWalk);

                if (!verifiedMultiPersonOverlap)
                {
                    verifiedMultiPersonOverlap = true;
                    world.PlaceObject(personB, new ObjectPosition(6 * 16 + 8, 5 * 16 + 8, 1));
                    Assert.Equal(personB.ClockManagerIndex, world.TileValueAt(6, 5, 1));
                    Assert.Equal(personA.ClockManagerIndex, personB.Slots[0]);
                    Assert.Equal([personB, personA, puddle], world.ObjectsAtTile(6, 5, 1).ToArray());

                    var multiSimWalk = CollectSetToNextTileWalk(world, personB, personB.StackObjectKey);
                    Assert.Equal([personA.StackObjectKey, puddle.StackObjectKey, personB.StackObjectKey], multiSimWalk);

                    world.PlaceObject(personB, new ObjectPosition(6 * 16 + 8, 7 * 16 + 8, 1));
                    Assert.Equal((short)0, personB.Slots[0]);
                    Assert.Equal(personA.ClockManagerIndex, world.TileValueAt(6, 5, 1));
                    Assert.Equal([personA, puddle], world.ObjectsAtTile(6, 5, 1).ToArray());
                }
            }
            if (routeResult == PrimitiveResult.Stop)
                world.TickAvatarAnimations(personA);
        }

        Assert.Equal(PrimitiveResult.True, routeResult);
        Assert.True(sawPersonOnPuddleTile, "Expected routing Sim to step onto the puddle tile (6, 5).");
        Assert.Equal((short)0, puddle[ObjectData.ParentId]);
        Assert.Equal((short)-1, puddle[ObjectData.SlotHierNumber]);
        Assert.Equal(puddle.ClockManagerIndex, world.TileValueAt(6, 5, 1));
        Assert.Equal([puddle], world.ObjectsAtTile(6, 5, 1).ToArray());

        Assert.Equal(target.ClockManagerIndex, personA.Slots[0]);
        world.PlaceObject(personA, new ObjectPosition(8 * 16 + 8, 6 * 16 + 8, 1));
        Assert.Equal((short)0, personA.Slots[0]);
        Assert.Equal(target.ClockManagerIndex, world.TileValueAt(8, 5, 1));
    }

    [Fact]
    public void House05_WalkingOverPuddleSetToNextGossipSocialAndAutonomousBathroomSinkHaveNoTileCycle()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var lotPath = Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var bootstrap = WorldBootstrap.Load(gameDir, lotPath, autonomyForced: true);
        var world = bootstrap.World;

        var cassandra = Assert.IsType<XPerson>(world.ObjectById(16));
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var mortimer = Assert.IsType<XPerson>(world.ObjectById(21));

        const uint floodTileGuid = 0x3E7470F6;
        var mortimerStart = mortimer.Position;
        var mortimerStartTile = (X: mortimerStart.X >> 4, Y: mortimerStart.Y >> 4, Level: (int)mortimerStart.Height);

        ObjectPosition? openNeighbor = null;
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1) })
        {
            var candidatePos = new ObjectPosition((mortimerStartTile.X + dx) * 16 + 8,
                (mortimerStartTile.Y + dy) * 16 + 8, mortimerStart.Height);
            if (world.TileValueAt(mortimerStartTile.X + dx, mortimerStartTile.Y + dy, mortimerStartTile.Level) == 0 &&
                world.CanPlaceObject(mortimer, candidatePos))
            {
                openNeighbor = candidatePos;
                break;
            }
        }
        Assert.NotNull(openNeighbor);

        var socialPuddle = world.ObjectFactory?.Invoke(unchecked((int)floodTileGuid));
        Assert.NotNull(socialPuddle);
        socialPuddle!.Position = new ObjectPosition(-16, -16, mortimerStart.Height);
        world.Add(socialPuddle);
        world.InitializeCreatedObject(socialPuddle);
        world.PlaceObject(socialPuddle, openNeighbor!.Value);

        var puddleTileX = openNeighbor.Value.X >> 4;
        var puddleTileY = openNeighbor.Value.Y >> 4;
        var puddleLevel = (int)openNeighbor.Value.Height;
        Assert.Equal(socialPuddle.ClockManagerIndex, world.TileValueAt(puddleTileX, puddleTileY, puddleLevel));

        world.PlaceObject(mortimer, openNeighbor.Value);
        Assert.Equal(mortimer.ClockManagerIndex, world.TileValueAt(puddleTileX, puddleTileY, puddleLevel));
        Assert.Equal(socialPuddle.ClockManagerIndex, mortimer.Slots[0]);
        Assert.Equal([mortimer, socialPuddle], world.ObjectsAtTile(puddleTileX, puddleTileY, puddleLevel).ToArray());
        Assert.Equal([socialPuddle.StackObjectKey, mortimer.StackObjectKey],
            CollectSetToNextTileWalk(world, mortimer, mortimer.StackObjectKey));

        world.PlaceObject(mortimer, mortimerStart);
        Assert.Equal((short)0, mortimer.Slots[0]);
        Assert.Equal(socialPuddle.ClockManagerIndex, world.TileValueAt(puddleTileX, puddleTileY, puddleLevel));
        Assert.Equal([socialPuddle], world.ObjectsAtTile(puddleTileX, puddleTileY, puddleLevel).ToArray());

        var sink = world.WalkOrder.FirstOrDefault(item =>
            (item.TypeRecord?.Name?.Contains("sink", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (item.TypeRecord?.CatalogName?.Contains("sink", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.NotNull(sink);

        XObject? sinkPuddle = null;
        var sinkTileX = sink!.Position.X >> 4;
        var sinkTileY = sink.Position.Y >> 4;
        foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, 1), (1, -1), (-1, -1) })
        {
            var candidate = world.ObjectFactory?.Invoke(unchecked((int)floodTileGuid));
            if (candidate is null) break;
            candidate.Position = new ObjectPosition(-16, -16, sink.Position.Height);
            world.Add(candidate);
            world.InitializeCreatedObject(candidate);
            var pos = new ObjectPosition((sinkTileX + dx) * 16 + 8, (sinkTileY + dy) * 16 + 8, sink.Position.Height);
            if (world.TileValueAt(sinkTileX + dx, sinkTileY + dy, sink.Position.Height) == 0 &&
                world.CanPlaceObject(candidate, pos))
            {
                world.PlaceObject(candidate, pos);
                sinkPuddle = candidate;
                break;
            }
            world.Remove(candidate.Id);
        }

        foreach (var sim in new[] { bella, mortimer })
        {
            foreach (var motive in new[] { Motive.Hunger, Motive.Comfort, Motive.Energy, Motive.Hygiene, Motive.Bladder, Motive.Fun })
                sim[motive] = 90f;
            sim[Motive.Social] = -40f;
        }
        foreach (var motive in new[] { Motive.Hunger, Motive.Comfort, Motive.Energy, Motive.Bladder, Motive.Fun, Motive.Social })
            cassandra[motive] = 85f;
        cassandra[Motive.Hygiene] = -80f;

        var socialTarget = InteractionMenus.Target(world, mortimer);
        var socialChoices = InteractionMenus.Read(world, bella, socialTarget);
        var gossip = socialChoices.FirstOrDefault(choice =>
                choice.ActionText?.Contains("Gossip", StringComparison.OrdinalIgnoreCase) ?? false)
            ?? socialChoices.FirstOrDefault(choice =>
                choice.ActionText?.StartsWith("Talk", StringComparison.OrdinalIgnoreCase) ?? false);
        Assert.NotNull(gossip);
        Assert.True(world.EnqueueInteractionAction(bella, gossip!));

        var trackedObjects = new List<XObject> { cassandra, bella, mortimer, socialPuddle };
        if (sinkPuddle is not null) trackedObjects.Add(sinkPuddle);

        var controls = new SimulationClockControl(1, 1, 1);
        for (var tick = 0; tick < 480; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            foreach (var obj in trackedObjects)
            {
                if (obj.Position.X < 0 || obj.Position.Y < 0 || obj.Position.Height < 1) continue;
                var tx = obj.Position.X >> 4;
                var ty = obj.Position.Y >> 4;
                var tileObjects = world.ObjectsAtTile(tx, ty, obj.Position.Height).ToArray();
                Assert.Equal(tileObjects.Length, tileObjects.Select(item => item.Id).Distinct().Count());
                _ = CollectSetToNextTileWalk(world, bella, obj.StackObjectKey);
            }
        }
    }

    [Fact]
    public void RoutePlanner_SolidFloorObstacleDisplacedIntoSlotZeroStillBlocksPathWhileAuthoredSlotChildIsSkipped()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var world = context.World;
        world.Host = null;
        world.LotRoomMaps[1] = PartitionedRoomWithGap(width: 16, height: 16, wallX: 6, gapMinY: 4, gapMaxY: 6);
        GoToRoutingSlotPrimitiveTests.AddWalkClips(world);

        var person = (XPerson)context.Me;
        person.TypeRecord = new TypeRecord(1) { ObjectType = XPerson.PersonType };
        if (person.Slots.Count == 0) person.Slots.Add(0);
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0801;
        world.PlaceObject(person, new ObjectPosition(2 * 16 + 8, 5 * 16 + 8, 1));

        var ringOffsets = new[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1) };
        for (var i = 0; i < ringOffsets.Length; i++)
        {
            var id = checked((short)(10 + i));
            var (dx, dy) = ringOffsets[i];
            var sideBlocker = world.Add(new XObject(id, PrimitiveTestWorld.Definition(0))
            {
                TypeRecord = new TypeRecord(id),
            });
            sideBlocker.Slots.Add(0);
            sideBlocker[ObjectData.AllowedHeightFlags] = 1;
            sideBlocker[ObjectData.RoutePenalty] = 0;
            sideBlocker[ObjectData.Flags] = 0;
            world.PlaceObject(sideBlocker, new ObjectPosition((10 + dx) * 16 + 8, (5 + dy) * 16 + 8, 1));
        }

        var goal = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(2),
        });
        goal.Slots.Add(0);
        goal[ObjectData.Flags] = 4;
        world.PlaceObject(goal, new ObjectPosition(10 * 16 + 8, 5 * 16 + 8, 1));

        var rug = world.Add(new XObject(20, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(20),
        });
        rug.Slots.AddRange([0, 0]);
        rug.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 2 });
        rug[ObjectData.AllowedHeightFlags] = 1;
        rug[ObjectData.Flags] = 4;
        world.PlaceObject(rug, new ObjectPosition(9 * 16 + 8, 5 * 16 + 8, 1));

        var containedProp = world.Add(new XObject(21, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(21),
        });
        containedProp.Slots.Add(0);
        containedProp[ObjectData.AllowedHeightFlags] = 2;
        containedProp[ObjectData.RoutePenalty] = 0;
        containedProp[ObjectData.Flags] = 0;
        world.PlaceObject(containedProp, new ObjectPosition(9 * 16 + 8, 5 * 16 + 8, 1),
            placementContainer: rug, placementSlotNumber: 1);

        Assert.Same(rug, world.ResolveParentContainer(containedProp));
        var clearResult = RouteExecutor.Start(world, person, context.Frame, goal, new RoutingSlotRequest(), null);
        Assert.Equal(PrimitiveResult.Stop, clearResult);
        clearResult = RouteExecutor.Continue(world, person, context.Frame, null);
        Assert.Equal(PrimitiveResult.Stop, clearResult);
        Assert.Equal((short)0, person[PersonData.RouteResult]);
        person.NativeRoute = null;
        context.Frame.Waiting = false;
        world.PlaceObject(person, new ObjectPosition(2 * 16 + 8, 5 * 16 + 8, 1));
        world.Remove(containedProp.Id);
        world.Remove(rug.Id);

        var solidFloorObstacle = world.Add(new XObject(22, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(22),
        });
        solidFloorObstacle.Slots.Add(0);
        solidFloorObstacle[ObjectData.AllowedHeightFlags] = 1;
        solidFloorObstacle[ObjectData.RoutePenalty] = 0;
        solidFloorObstacle[ObjectData.Flags] = 0;
        world.PlaceObject(solidFloorObstacle, new ObjectPosition(9 * 16 + 8, 5 * 16 + 8, 1));

        var distantSim = world.Add(new XPerson(23, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(23) { ObjectType = XPerson.PersonType },
        });
        distantSim.Slots.Add(0);
        distantSim[ObjectData.AllowedHeightFlags] = 1;
        distantSim[ObjectData.PlacementFlags] = 0x0801;
        distantSim.Position = new ObjectPosition(9 * 16 + 8, 5 * 16 + 8, 1);
        distantSim.RoomWord84 = solidFloorObstacle.RoomWord84;
        distantSim.Slots[0] = solidFloorObstacle.ClockManagerIndex;
        solidFloorObstacle[ObjectData.ParentId] = distantSim.ClockManagerIndex;
        solidFloorObstacle[ObjectData.SlotHierNumber] = 0;

        Assert.NotEqual((short)0, solidFloorObstacle[ObjectData.ParentId]);
        Assert.Equal((short)0, solidFloorObstacle[ObjectData.SlotHierNumber]);
        Assert.Null(world.ResolveParentContainer(solidFloorObstacle));

        var blockedResult = RouteExecutor.Start(world, person, context.Frame, goal, new RoutingSlotRequest(), null);
        for (var step = 0; step < 8 && blockedResult == PrimitiveResult.Stop; step++)
        {
            blockedResult = RouteExecutor.Continue(world, person, context.Frame, null);
            if (blockedResult == PrimitiveResult.Stop)
                world.TickAvatarAnimations(person);
        }
        Assert.Equal(PrimitiveResult.False, blockedResult);
        Assert.Equal((short)1, person[PersonData.RouteResult]);
    }

    [Fact]
    public void InteractionMenuContext_TimeLocalFormatsMidnightAndNoonInTwelveHourClock()
    {
        var world = new SimulationWorld();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(1) { CatalogName = "Bella" },
        });
        var target = world.Add(new XObject(2, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(2) { CatalogName = "Alarm Clock" },
        });
        var context = new InteractionMenuContext(world)
        {
            Current = new ActionQueueSlot { Person = person, Owner = target },
        };

        var midnightFrame = new StackFrame(target.Id, [], [0, 5]);
        context.Add("Wake at $TimeLocal:0:1", target.Id, midnightFrame);
        Assert.Equal("Wake at 12:05am", context.Choices[0].ActionText);
        Assert.Equal("12:05am", context.Expand("$TimeLocal:0:1", person, midnightFrame));

        var noonFrame = new StackFrame(target.Id, [], [12, 30]);
        context.Add("Lunch at $TimeLocal:0:1", target.Id, noonFrame);
        Assert.Equal("Lunch at 12:30pm", context.Choices[1].ActionText);
        Assert.Equal("12:30pm", context.Expand("$TimeLocal:0:1", person, noonFrame));
    }
}
