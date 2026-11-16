using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave9ParityRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static void AdvanceWorldTicks(SimulationWorld world, int count, Func<bool>? stopWhen = null)
    {
        var control = new SimulationClockControl(1, 1, 1);
        for (var i = 0; i < count; i++)
        {
            world.AdvanceSimulationTick(control, 0, world.Clock.Ticks);
            if (stopWhen?.Invoke() == true)
                return;
        }
    }

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
            ("Arry", 9, water.ToArray())), "wave9-open-room.iff"), 1, width, height);
    }

    private static (int TileX, int TileY) FindClearOutdoorArea(
        SimulationWorld world, XObject primary, int radiusTiles = 2)
    {
        var originalPos = primary.Position;
        var map = world.LotRoomMaps.GetValueOrDefault(1);
        for (var ty = 6; ty <= LotGeometry.LotDimension - 7; ty++)
        {
            for (var tx = 6; tx <= LotGeometry.LotDimension - 7; tx++)
            {
                var ringClear = true;
                for (var dy = -radiusTiles; dy <= radiusTiles && ringClear; dy++)
                {
                    for (var dx = -radiusTiles; dx <= radiusTiles; dx++)
                    {
                        var nx = tx + dx;
                        var ny = ty + dy;
                        if (world.TileValueAt(nx, ny, 1) != 0)
                        {
                            ringClear = false;
                            break;
                        }
                        if (map is not null && !map.IsOutside(map.RoomAt(nx, ny)))
                        {
                            ringClear = false;
                            break;
                        }
                    }
                }
                if (!ringClear) continue;

                var pos = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
                if (world.CanPlaceObjectFromEditor(primary, pos, out _, out _))
                {
                    primary.Position = originalPos;
                    return (tx, ty);
                }
            }
        }
        primary.Position = originalPos;
        throw new InvalidOperationException("Unable to locate a clear outdoor placement region on House05.");
    }

    private static ActionQueueSlot? FindMenuChoice(
        SimulationWorld world, XPerson actor, IReadOnlyList<XObject> members, string name)
    {
        foreach (var member in members)
        {
            var match = InteractionMenus.Read(world, actor, member)
                .FirstOrDefault(choice => string.Equals(choice.ActionText, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return null;
    }

    [Fact]
    public void NotifyPersonObjectReferenceRecipients_DoesNotSkipNextPersonWhenResetShrinksWalkOrderAndSkipsReinitWhenKilled()
    {
        var (baseContext, _, host) = PrimitiveTestWorld.Create();
        var world = baseContext.World;
        world.LotRoomMaps[1] = OpenOutdoorRoomMap();

        world.Remove(100);

        var earlierHelper = world.Add(new XObject(9, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(0x9001),
            Position = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1),
        });

        var subjectFunctions = new ushort[0x20];
        subjectFunctions[0] = 4096;
        var subjectInit = PrimitiveTestWorld.Tree(0,
            (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(0, 42, (byte)ExpressionOperator.AssignTo, (byte)DataOwner.MyAttribute, (byte)DataOwner.Literal)));
        var subjectObj = world.Add(new XObject(11, PrimitiveTestWorld.Definition(), attributes: 4)
        {
            TypeRecord = new TypeRecord(0x9002)
            {
                FunctionTable = subjectFunctions,
                BehaviorScope = new TreeTable((4096, subjectInit)),
            },
            Position = new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1),
        });
        subjectObj.Attributes[0] = 99;

        var personA = world.Add(new XPerson(15, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x9003) { ObjectType = XPerson.PersonType },
            Position = new ObjectPosition(14 * 16 + 8, 14 * 16 + 8, 1),
        });
        personA[ObjectData.AllowedHeightFlags] = 1;
        personA[ObjectData.PlacementFlags] = 0x0801;
        PersonInteractionStack.Register(personA, subjectObj, 1, false);

        var personB = world.Add(new XPerson(16, PrimitiveTestWorld.Definition(XPerson.PersonType))
        {
            TypeRecord = new TypeRecord(0x9004) { ObjectType = XPerson.PersonType },
            Position = new ObjectPosition(16 * 16 + 8, 16 * 16 + 8, 1),
        });
        personB.IdleFloat7A8 = -100;
        personB.ActionQueue[0] = new ActionQueueSlot
        {
            Owner = subjectObj,
            Person = personB,
            Secondary = subjectObj,
            ActionTree = 4097,
            Threshold = 50,
            Flags = 1,
        };
        personB.ActionQueueEnd = 1;

        host.OnPersonObjectReferenceNotification = (recipient, subject) =>
        {
            if (ReferenceEquals(recipient, personA) && ReferenceEquals(subject, subjectObj))
                world.Remove(earlierHelper.Id);
        };

        Assert.True(world.KillObject(subjectObj.Id));

        Assert.Equal((short)99, subjectObj.Attributes[0]);
        Assert.Empty(subjectObj.SavedStack);

        Assert.Equal(personB.ActionQueueCurrent, personB.ActionQueueEnd);
        Assert.All(personB.ActionQueue, slot => Assert.False(ReferenceEquals(slot.Owner, subjectObj)));

        var deadOwnerTree = PrimitiveTestWorld.Tree(0,
            (2, BhavInstruction.ReturnTrue, BhavInstruction.ReturnFalse,
                PrimitiveTestWorld.Expression(0, 1, (byte)ExpressionOperator.EqualTo)));
        var removedOwner = new XObject(77, PrimitiveTestWorld.Definition())
        {
            Flags = 0x40u,
            TypeRecord = new TypeRecord(0x9005)
            {
                BehaviorScope = new TreeTable((4097, deadOwnerTree)),
            },
        };
        personB.ActionQueue[personB.ActionQueueEnd & 7] = new ActionQueueSlot
        {
            Owner = removedOwner,
            Person = personB,
            Secondary = removedOwner,
            ActionTree = 4097,
            Threshold = 50,
        };
        personB.ActionQueueEnd++;
        personB.InputFlags = 0;

        var stack = TreeRunner.CreateTransientStack(world, personB);
        var frame = new StackFrame(0, [0], [0, 0]);
        stack.Frames.Add(frame);
        world.SavedExecutionStacks[personB.Id] = stack;
        world.Host = null;

        var context = new TreeContext(world, personB, frame) { Stack = stack };
        ReadOnlySpan<byte> idleOperand = [0, 0, 1, 0, 0, 0, 0, 0];
        var result = IdleForInputPrimitive.Execute(context, idleOperand);

        Assert.Equal(PrimitiveResult.Stop, result);
        Assert.Single(stack.Frames);
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    public void ChessTablePlay_FailsWithoutChairAndSitsToRaiseLogicWithFacingDiningChair()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        const uint chessTableGuid = 0x94aa32f4;
        const uint diningChairGuid = 0xe7938987;

        var chess = Assert.IsAssignableFrom<XObject>(world.ObjectFactory?.Invoke(unchecked((int)chessTableGuid)));
        chess.Position = new ObjectPosition(-16, -16, 1);
        world.Add(chess);
        world.InitializeCreatedObject(chess);
        IReadOnlyList<XObject> chessGroup = [chess];
        world.FaceCreatedObject(chess, chessGroup, 0);

        var (cx, cy) = FindClearOutdoorArea(world, chess, radiusTiles: 3);
        var chessPos = new ObjectPosition(cx * 16 + 8, cy * 16 + 8, 1);
        Assert.True(world.CanPlaceObjectFromEditor(chess, chessPos, out var chessRef, out var chessRefSlot));
        world.PlaceCreatedObjectFromEditor(chess, chessPos, chessRef, chessRefSlot);

        var bellaNear = new ObjectPosition((cx + 2) * 16 + 8, (cy + 2) * 16 + 8, 1);
        Assert.True(world.CanPlaceObject(bella, bellaNear));
        world.PlaceObject(bella, bellaNear);

        var playNoChair = FindMenuChoice(world, bella, chessGroup, "Play");
        Assert.NotNull(playNoChair);
        Assert.True(world.EnqueueInteractionAction(bella, playNoChair!));
        AdvanceWorldTicks(world, 300, () =>
            bella.CurrentActionRecord?.Owner is null && bella.ActionQueueCurrent == bella.ActionQueueEnd &&
            bella.NativeRoute is null);
        Assert.Equal((short)0, bella[PersonData.Sitting]);
        Assert.Empty(world.ThreadErrors);

        var chair = Assert.IsAssignableFrom<XObject>(world.ObjectFactory?.Invoke(unchecked((int)diningChairGuid)));
        chair.Position = new ObjectPosition(-16, -16, 1);
        world.Add(chair);
        world.InitializeCreatedObject(chair);
        world.FaceCreatedObject(chair, [chair], 4);
        var chairPos = LotGeometry.SlotPosition(chess.Position, 0, 0, -16);
        Assert.True(world.CanPlaceObjectFromEditor(chair, chairPos, out var chairRef, out var chairRefSlot));
        world.PlaceCreatedObjectFromEditor(chair, chairPos, chairRef, chairRefSlot);

        bella[Motive.Fun] = -60;
        bella[Motive.Energy] = 85;
        bella[Motive.Comfort] = -20;
        bella[Motive.Hunger] = 80;
        var initialLogic = bella[PersonData.LogicSkill];

        var playWithChair = FindMenuChoice(world, bella, chessGroup, "Play");
        Assert.NotNull(playWithChair);
        Assert.True(world.EnqueueInteractionAction(bella, playWithChair!));

        var sawSittingInChair = false;
        AdvanceWorldTicks(world, 2500, () =>
        {
            if (bella[PersonData.Sitting] == 1 && bella[ObjectData.ContainerId] == chair.ClockManagerIndex)
                sawSittingInChair = true;
            return sawSittingInChair && bella[PersonData.LogicSkill] > initialLogic;
        });

        Assert.True(sawSittingInChair, $"Expected Bella to sit in facing dining chair {chair.Id} while playing chess.");
        Assert.True(bella[PersonData.LogicSkill] > initialLogic,
            $"Expected Bella's LogicSkill ({bella[PersonData.LogicSkill]}) to exceed initial ({initialLogic}).");
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    public void ExerciseMachineWorkOut_CreatesThreePartGroupAndRaisesBodySkillWithoutThreadErrors()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        const uint exerciseMachineGuid = 0xddecd497;
        var machineGroup = world.ObjectGroupFactory?.Invoke(unchecked((int)exerciseMachineGuid));
        Assert.NotNull(machineGroup);
        Assert.Equal(3, machineGroup!.Count);

        var machineLead = machineGroup[0];
        world.FaceCreatedObject(machineLead, machineGroup, 0);
        var (tx, ty) = FindClearOutdoorArea(world, machineLead, radiusTiles: 3);
        var machinePos = new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 1);
        Assert.True(world.CanPlaceObjectFromEditor(machineLead, machinePos, out var refObj, out var refSlot));
        world.PlaceCreatedObjectFromEditor(machineLead, machinePos, refObj, refSlot);

        var bellaNear = new ObjectPosition((tx + 2) * 16 + 8, (ty + 2) * 16 + 8, 1);
        Assert.True(world.CanPlaceObject(bella, bellaNear));
        world.PlaceObject(bella, bellaNear);

        bella[Motive.Energy] = 90;
        bella[Motive.Hunger] = 80;
        bella[Motive.Hygiene] = 80;
        bella[Motive.Comfort] = 80;
        var initialBody = bella[PersonData.BodyFurnitureSkill];

        var workOut = FindMenuChoice(world, bella, machineGroup, "Work Out");
        Assert.NotNull(workOut);
        Assert.True(world.EnqueueInteractionAction(bella, workOut!));

        AdvanceWorldTicks(world, 3000, () => bella[PersonData.BodyFurnitureSkill] > initialBody);

        Assert.True(bella[PersonData.BodyFurnitureSkill] > initialBody,
            $"Expected Bella's BodySkill ({bella[PersonData.BodyFurnitureSkill]}) to exceed initial ({initialBody}).");
        Assert.Empty(world.ThreadErrors);
    }

    [Fact]
    public void BathtubTakeABath_WakesSleepAndRaisesHygieneWithoutThreadErrors()
    {
        var gameDir = LocateGameDirectory();
        var house05Path = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05Path))
            return;

        var boot = WorldBootstrap.Load(gameDir, house05Path!);
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        bella[Motive.Hygiene] = -60;
        bella[Motive.Energy] = 85;
        bella[Motive.Hunger] = 80;
        bella[Motive.Bladder] = 80;

        ActionQueueSlot? takeBath = null;
        foreach (var candidate in world.WalkOrder)
        {
            takeBath = InteractionMenus.Read(world, bella, candidate)
                .FirstOrDefault(choice => string.Equals(choice.ActionText, "Take a Bath", StringComparison.OrdinalIgnoreCase));
            if (takeBath is not null)
                break;
        }
        Assert.NotNull(takeBath);

        var initialHygiene = bella[Motive.Hygiene];
        Assert.True(world.EnqueueInteractionAction(bella, takeBath!));

        AdvanceWorldTicks(world, 3000, () => bella[Motive.Hygiene] > initialHygiene + 20f);

        Assert.True(bella[Motive.Hygiene] > initialHygiene + 20f,
            $"Expected Bella's Hygiene ({bella[Motive.Hygiene]}) to rise above {initialHygiene + 20f}.");
        Assert.Empty(world.ThreadErrors);
    }
}
