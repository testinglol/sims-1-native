using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL.Core;

namespace SimsEngine.Tests;

public sealed class Wave11SimAnticsAndRoutingTests
{
    private static RoomMap OpenOutdoorRoomMap(int width = LotGeometry.LotDimension, int height = LotGeometry.LotDimension)
    {
        var floor = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(new byte[width * height]);
        var walls = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(8).U32(0)
            .Raw(new byte[width * height * 8]);
        var water = new ChunkBuilder().S16(0).S16(0).S32(width).S32(height).S32(1).U32(0)
            .Raw(Enumerable.Repeat((byte)0x10, width * height).ToArray());
        return RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 1, floor.ToArray()),
            ("Arry", 2, walls.ToArray()),
            ("Arry", 9, water.ToArray())), "wave11-open-room.iff"), 1, width, height);
    }

    [Fact]
    public void ResetPersonForChangedCode_ClearsSittingAndPostureFields()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenOutdoorRoomMap();
        var seat = world.Add(new XObject(10, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1),
        });
        var person = world.Add(new XPerson(15, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x9100) { ObjectType = XPerson.PersonType },
            Position = new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1),
        });
        person[ObjectData.AllowedHeightFlags] = 1;
        person[ObjectData.PlacementFlags] = 0x0801;
        person[PersonData.Sitting] = 1;
        person[PersonData.WalkingUpStairs] = 1;
        person[PersonData.ObjectSocialObjectID] = seat.Id;
        person[ObjectData.Hidden] = 1;
        person[ObjectData.RoutePreference] = 2;
        PersonInteractionStack.Register(person, seat, 1, false);

        Assert.True(world.KillObject(seat.Id));

        Assert.Equal((short)0, person[PersonData.Sitting]);
        Assert.Equal((short)0, person[PersonData.WalkingUpStairs]);
        Assert.Equal((short)0, person[PersonData.ObjectSocialObjectID]);
        Assert.Equal((short)0, person[ObjectData.Hidden]);
        Assert.Equal((short)0, person[ObjectData.RoutePreference]);
    }

    [Fact]
    public void MarkerlessWalkClipAtDestination_CompletesLocomotionFadeAndBlocksStepOn0x0b()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenOutdoorRoomMap();
        var walkClip = new AvatarAnimationClip("a2o-walk", 10, [], StandardDistance: 3f, DurationMilliseconds: 333.333f);
        world.StandardWalkFallbackAnimationTable[(150, 0x14)] = walkClip;
        world.LocomotionStrideResolver = (_, _) => null;

        var person = world.Add(new XPerson(15, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x9101) { ObjectType = XPerson.PersonType },
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
            AnimatorRootX = 10 * 16 + 8,
            AnimatorRootY = 10 * 16 + 8,
            AnimatorControlState = 3,
            AnimatorWalkingScale = 1f,
            AnimatorMilliseconds = 1000,
        });
        person.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, true)
        {
            RouteDriven = true,
            PracticeWeight = 1f,
        };
        person.NativeRoute = new RouteState(
            person.Id,
            person.Position,
            new RoutingSlotRequest(),
            MotionTargetTileX: 10,
            MotionTargetTileY: 10,
            MotionTargetX: 10 * 16 + 8,
            MotionTargetY: 10 * 16 + 8,
            MotionTargetIsDestination: true,
            MotionX: 10 * 16 + 8,
            MotionY: 10 * 16 + 8,
            MotionAnimationIndex: 0x14);

        world.TickAvatarAnimations(person);

        Assert.True(person.NativeRoute!.LocomotionFadeEndMils > person.AnimatorMilliseconds);
        Assert.Equal(1, person.AnimatorLocomotionFadeGate);
        Assert.Equal(1, person.AnimatorRouteTransitionState);

        var anchor = world.Add(new XObject(20, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1),
        });
        anchor[ObjectData.AllowedHeightFlags] = 1;
        anchor.FootprintSlots.Add(new ObjectFootprintSlot { HeightCode = 5 });
        person[ObjectData.AllowedHeightFlags] = 1;
        person.AnimatorRootX = 10 * 16 + 15.5f;
        person.AnimatorRootY = 10 * 16 + 8;
        person.NativeRoute = new RouteState(
            anchor.Id,
            new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1),
            new RoutingSlotRequest { SnapTargetSlot = 0, MinProximity = 16, MaxProximity = 16, OptimalProximity = 16 },
            MotionTargetTileX: 11,
            MotionTargetTileY: 10,
            MotionTargetX: 11 * 16 + 8,
            MotionTargetY: 10 * 16 + 8,
            MotionTargetIsDestination: true,
            MotionX: 10 * 16 + 15.5f,
            MotionY: 10 * 16 + 8,
            MotionAnimationIndex: 0x14);

        RouteExecutor.TickRouteRootMovement(world, person, null);

        Assert.True(person.NativeRoute!.FollowPlacementRejected);
        Assert.Equal(3, person.NativeRoute.AnimatorFollowState);
        Assert.Equal(10 * 16 + 15.5f, person.AnimatorRootX);
        Assert.Equal(10 * 16 + 8f, person.AnimatorRootY);
    }

    [Fact]
    public void MultiTileInteractionLead_ResolvesThroughMultiTileMembersEvenWhenMasterIdIsZero()
    {
        var world = new SimulationWorld();
        var chunk = new ChunkBuilder().S16(1).S16(8)
            .U16(4096).U16(0).S32(0).U32(0).S32(0).U32(0).F32(0).U32(0).S32(0);
        var (file, resource) = chunk.InFile("TTAB");
        var interactions = InteractionTable.Read(file, resource);

        var leadDef = PrimitiveTestWorld.Definition();
        leadDef[(int)ObjdField.MasterId] = 1;
        leadDef[(int)ObjdField.InteractionGroup] = -2;
        leadDef[(int)ObjdField.SubIndex] = 0;

        var subDef = PrimitiveTestWorld.Definition();
        subDef[(int)ObjdField.MasterId] = 1;
        subDef[(int)ObjdField.InteractionGroup] = 2;
        subDef[(int)ObjdField.SubIndex] = 1;

        var lead = world.Add(new XObject(30, leadDef)
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = 0,
            MultiTileNextId = 31,
            TypeRecord = new TypeRecord(0x9102) { Interactions = interactions },
        });
        var subPart = world.Add(new XObject(31, subDef)
        {
            RuntimeClass = ObjectClass.MultiTileObject,
            MultiTileMasterId = 0,
        });

        Assert.Same(lead, FindBestInteractionPrimitive.ResolveInteractionLead(world, subPart));
        Assert.Same(lead, InteractionMenus.Target(world, subPart));
    }

    [Fact]
    public void FindLocationMode5_ScattersAroundTargetWithinBounds()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenOutdoorRoomMap();
        world.RandomState = 0x12345678u;

        var anchor = world.Add(new XObject(40, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(30 * 16 + 8, 30 * 16 + 8, 1),
        });
        anchor[ObjectData.AllowedHeightFlags] = 1;

        var me = world.Add(new XObject(41, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(30 * 16 + 8, 30 * 16 + 8, 1),
        });

        var distinctPositions = new HashSet<(int X, int Y)>();
        for (var i = 0; i < 6; i++)
        {
            var stackObj = world.Add(new XObject((short)(50 + i), PrimitiveTestWorld.Definition())
            {
                Position = new ObjectPosition(-16, -16, 1),
            });
            stackObj[ObjectData.AllowedHeightFlags] = 1;

            var context = new TreeContext(world, me, new StackFrame(stackObj.ClockManagerIndex, [], [anchor.ClockManagerIndex]));
            var operand = new byte[] { 5, 0, 1, 0, 0, 0, 0, 0 };
            Assert.Equal(PrimitiveResult.True, FindLocationForPrimitive.Execute(context, operand));
            distinctPositions.Add((stackObj.Position.X >> 4, stackObj.Position.Y >> 4));
            Assert.InRange(stackObj.Position.X >> 4, 24, 36);
            Assert.InRange(stackObj.Position.Y >> 4, 24, 36);
        }

        Assert.True(distinctPositions.Count >= 3, $"Expected scattered mode-5 placements, got {distinctPositions.Count} unique tiles.");
    }

    [Fact]
    public void DirectAnimationFrame_AllowsDroppedIntermediateFramesWithoutContentError()
    {
        var baseScene = new LotScene
        {
            SchemaVersion = 1,
            House = 5,
            Width = 640,
            Height = 480,
            Textures = [new LotScene.Texture("base.rgba", 1, 1)],
            Sprites = [new LotScene.Sprite { Texture = 0, X = 0, Y = 0, Width = 1, Height = 1, Flip = false, ObjectID = null, Layer = 0, Level = 1 }],
            Limitations = [],
        };

        var frame2AfterDrop = new SceneAnimation
        {
            SchemaVersion = 1,
            BaseRevision = 1,
            Sequence = 2,
            Updates = 2,
            TextureStart = 1,
            Textures = [new LotScene.Texture("dyn1.rgba", 1, 1), new LotScene.Texture("dyn2.rgba", 1, 1)],
            Sprites = [new LotScene.Sprite { Texture = 2, X = 1, Y = 1, Width = 1, Height = 1, Flip = false, ObjectID = 9, Layer = 1, Level = 1 }],
            Meshes = [],
            IsTrustedInProcess = true,
        };
        var applied1 = frame2AfterDrop.Applying(baseScene, revision: 1, after: 0);
        Assert.Equal(3, applied1.Textures.Count);

        var frame3 = frame2AfterDrop with
        {
            Sequence = 3,
            Updates = 3,
            Textures = [new LotScene.Texture("dyn1.rgba", 1, 1)],
            Sprites = [new LotScene.Sprite { Texture = 1, X = 2, Y = 2, Width = 1, Height = 1, Flip = false, ObjectID = 9, Layer = 1, Level = 1 }],
        };
        var applied2 = frame3.Applying(applied1, revision: 1, after: 2);
        Assert.Equal(2, applied2.Textures.Count);
        Assert.Equal("dyn1.rgba", applied2.Textures[1].File);
    }

    [Fact]
    public void RouteCrossingDirection_ProjectsForwardAlongFinalSegment()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
        };
        person.RefreshFootprintBounds();
        var path = new List<RouteExecutor.RouteWaypoint>
        {
            new(10, 12, 10 * 16 + 8, 12 * 16 + 8, 10 * 16 + 8, 12 * 16 + 8, false),
            new(10, 10, 10 * 16 + 8, 10 * 16 + 8, 10 * 16 + 8, 10 * 16 + 8, true),
        };

        var dir = RouteExecutor.RouteCrossingDirection(person, path);

        Assert.Equal((short)0, dir);
    }

    [Fact]
    public void TryPath_RejectsImpassableObstacleInteriorCellsAndClampsSmoothedWaypointTiles()
    {
        var world = new SimulationWorld();
        world.LotRoomMaps[1] = OpenOutdoorRoomMap();
        var person = world.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
        });
        person[ObjectData.AllowedHeightFlags] = 1;
        var anchor = world.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(14 * 16 + 8, 10 * 16 + 8, 1),
        });
        anchor[ObjectData.AllowedHeightFlags] = 1;

        short id = 100;
        for (var tx = 13; tx <= 15; tx++)
        for (var ty = 9; ty <= 11; ty++)
        {
            if (tx == 14 && ty == 10) continue;
            var blocker = world.Add(new XObject(id++, PrimitiveTestWorld.Definition())
            {
                Position = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1),
            });
            blocker[ObjectData.AllowedHeightFlags] = 1;
            blocker[ObjectData.RoutePenalty] = 0;
            world.SetObjectPosition(blocker, blocker.Position);
        }

        Assert.False(RouteExecutor.TryPath(world, person, new ObjectPosition(14 * 16 + 8, 10 * 16 + 8, 1), anchor, out _, out _));

        var openWorld = new SimulationWorld();
        openWorld.LotRoomMaps[1] = OpenOutdoorRoomMap();
        var mover = openWorld.Add(new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            Position = new ObjectPosition(8 * 16 + 2, 10 * 16 + 2, 1),
        });
        mover[ObjectData.AllowedHeightFlags] = 1;
        var goalAnchor = openWorld.Add(new XObject(2, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(14 * 16 + 15, 13 * 16 + 15, 1),
        });
        goalAnchor[ObjectData.AllowedHeightFlags] = 1;
        Assert.True(RouteExecutor.TryPath(openWorld, mover, goalAnchor.Position, goalAnchor, out var path, out _));
        Assert.All(path, wp =>
        {
            Assert.InRange(wp.TileX, 0, LotGeometry.LotDimension - 1);
            Assert.InRange(wp.TileY, 0, LotGeometry.LotDimension - 1);
        });
    }
}
