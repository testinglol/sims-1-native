using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Implements the Create New Object Instance primitive (opcode 0x2a), instantiating objects by
/// GUID or neighbor table lookup and placing them according to modes 0 through 14 (in front, on tile, in
/// containment slots, off-world, or relative to stack/local targets). Applies tile occupancy, room
/// constraint, and facing checks before finalizing placement and setting the new instance as the stack
/// object.</summary>
public static class CreateObjectInstancePrimitive
{
    private const short NoRoomConstraint = -5;
    private const int NoFacing = -1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var host = world.Host;
        var native = world.ObjectFactory is not null || host is null;
        var mode = operand[4];
        var flags = operand[5];
        var tableStackObject = me.ObjectTableEntry(frame.StackObject);

        int guidKey;
        if ((flags & 4) == 0)
        {
            guidKey = operand[0] | operand[1] << 8 | operand[2] << 16 | operand[3] << 24;
        }
        else
        {
            guidKey = world.StackObjectCreateKey(frame.StackObject);
            if (guidKey == 0) return PrimitiveResult.False;
        }

        var position = me.Position;
        var level = position.Height;
        var room = NoRoomConstraint;
        XObject? relativeTo = null;
        var method = 0;
        var facing = NoFacing;
        int x, y;
        switch (mode)
        {
            case 0:
            {
                var offset = LotGeometry.FootprintOffset(me[ObjectData.Direction]);
                x = position.X + offset.X;
                y = position.Y + offset.Y;
                break;
            }
            case 1:
                x = position.X;
                y = position.Y;
                relativeTo = me;
                break;
            case 2:
                x = position.X;
                y = position.Y;
                relativeTo = me;
                break;
            case 3:
            {
                if (tableStackObject is null) return PrimitiveResult.False;
                var offset = LotGeometry.FootprintOffset(tableStackObject[ObjectData.Direction]);
                x = tableStackObject.Position.X + offset.X;
                y = tableStackObject.Position.Y + offset.Y;
                level = tableStackObject.Position.Height;
                room = tableStackObject.RoomWord84;
                break;
            }
            case 4:
            {
                if (tableStackObject is null) return PrimitiveResult.False;
                x = tableStackObject.Position.X;
                y = tableStackObject.Position.Y;
                level = tableStackObject.Position.Height;
                room = tableStackObject.RoomWord84;
                relativeTo = tableStackObject;
                break;
            }
            case 5:
                x = position.X;
                y = position.Y;
                break;
            case 6:
                x = -16;
                y = -16;
                level = 1;
                break;
            case 7:
                if (frame.Parameters.Length == 0)
                    throw new SimulationException(SimulationError.StackNumberOutOfRange);
                var parameterObject = me.ObjectTableEntry(frame.Parameters[0]);
                if (parameterObject is null) return PrimitiveResult.False;
                x = parameterObject.Position.X;
                y = parameterObject.Position.Y;
                level = parameterObject.Position.Height;
                break;
            case 8:
            {
                var index = (sbyte)operand[6];
                if (index < 0 || index >= frame.Locals.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
                var target = me.ObjectTableEntry(frame.Locals[index]);
                if (target is null) return PrimitiveResult.False;
                x = target.Position.X;
                y = target.Position.Y;
                level = target.Position.Height;
                break;
            }
            case 9:
            {
                var step = Variables.Get(context, DataOwner.Local, (short)(sbyte)operand[6]);
                var direction = me[ObjectData.Direction];
                var (ox, oy) = (((direction + step) % 8) & 7) switch
                {
                    0 => (0, -16),
                    2 => (16, 0),
                    4 => (0, 16),
                    6 => (-16, 0),
                    _ => (0, 0),
                };
                x = position.X + ox;
                y = position.Y + oy;
                facing = (direction + 4 + step) % 8;
                break;
            }
            case 10:
                x = position.X;
                y = position.Y;
                relativeTo = me;
                method = 2;
                break;
            case 0xb:
                x = position.X;
                y = position.Y;
                relativeTo = me;
                method = 3;
                break;
            case 0xc:
            case 0xd:
            case 0xe:
            {
                if (tableStackObject is null) return PrimitiveResult.False;
                x = tableStackObject.Position.X;
                y = tableStackObject.Position.Y;
                level = tableStackObject.Position.Height;
                room = tableStackObject.RoomWord84;
                relativeTo = tableStackObject;
                method = mode - 10;
                break;
            }
            default:
                x = 0;
                y = 0;
                break;
        }

        var inMyHand = mode == 2;
        var place = mode is not (2 or 6);
        var tileX = x >> 4;
        var tileY = y >> 4;
        if (((flags & 1) != 0 || (!native && (flags & 2) != 0)) && !inMyHand && (native ? world.TileHasObject(tileX, tileY, level, guidKey) : host!.TileHasObject(tileX, tileY, level, guidKey))) return PrimitiveResult.False;
        if ((flags & 8) != 0 && !inMyHand && (native ? world.TileHasObject(tileX, tileY, level, null) : host!.TileHasObject(tileX, tileY, level, null))) return PrimitiveResult.False;
        if (place)
        {
            var lotSize = LotGeometry.LotDimension;
            if (tileX <= 0 || tileY <= 0 || tileX >= lotSize - 1 || tileY >= lotSize - 1) return PrimitiveResult.False;
        }
        if ((flags & 0x40) != 0)
        {
            room = NoRoomConstraint;
            facing = NoFacing;
        }
        if (place && room != NoRoomConstraint &&
            (native ? world.RoomAtTile(tileX, tileY, level) != room : host!.RoomAtTile(tileX, tileY, level) != room))
            return PrimitiveResult.False;
        if (facing != NoFacing && (native ? !world.FacingClear(tileX, tileY, level, facing) : !host!.FacingClear(tileX, tileY, level, facing)))
            return PrimitiveResult.False;

        var createdGroup = native ? world.ObjectGroupFactory?.Invoke(guidKey) : null;
        if (createdGroup is { Count: 0 }) return PrimitiveResult.False;
        var created = createdGroup is not null
            ? createdGroup[0]
            : world.ObjectFactory is not null ? world.ObjectFactory(guidKey) : host?.CreateObjectInstance(guidKey);
        if (created is null) return PrimitiveResult.False;
        if (createdGroup is null)
        {
            world.Add(created);
            if (native && created is XPerson createdPerson)
            {
                world.InitializeCreatedPerson(createdPerson);
                world.InitializeCreatedObject(createdPerson);
                world.InitializeCreatedPerson(createdPerson);
                if (world.NeighborInteractionManagerTypeKeys.Count > 0 &&
                    world.WalkOrder.OfType<XPerson>().All(item => item.TypeRecord is not null))
                {
                    world.RefreshNeighborInteractionManager();
                }
            }
            else if (native) world.InitializeCreatedObject(created);
            else world.ClockManager.SetTickEnabled(created, true);
        }
        world.SetObjectPosition(created, new ObjectPosition(x, y, level));

        if (created.Definition[(int)ObjdField.Type] == XPerson.PersonType && created is XPerson person)
            host?.InitCreatedPerson(person);

        if (inMyHand)
        {
            // Hand objects face their holder rather than inheriting stack object facing.
            created[ObjectData.Direction] = (short)(me[ObjectData.Direction] & 7);
        }
        else if ((flags & 0x20) != 0 && tableStackObject is not null)
        {
            if (created is XPerson)
            {
                created[ObjectData.Direction] = tableStackObject[ObjectData.Direction];
            }
            else if (createdGroup is not null)
            {
                world.FaceCreatedObject(created, createdGroup, (short)(tableStackObject[ObjectData.Direction] & 6));
                host?.RotateCreatedObject(created);
            }
            else
            {
                created[ObjectData.Direction] = (short)(tableStackObject[ObjectData.Direction] & 6);
                host?.RotateCreatedObject(created);
            }
        }

        if (created.Definition[(int)ObjdField.MasterId] != 0 &&
            (SimulationWorld.HasFunction(created, 8) || (!native && SimulationWorld.HasFunction(me, 8))))
        {
            x = (x & ~7) | 8;
            y = (y & ~7) | 8;
            if (!inMyHand) world.SetObjectPosition(created, new ObjectPosition(x, y, level));
        }

        if (place)
        {
            var at = new ObjectPosition(x, y, level);
            var canPlace = !native ? host!.CanPlaceCreated(created, at, relativeTo, method)
                : relativeTo is not null ? world.CanPlaceRelativeObject(created, at, relativeTo, method)
                : world.CanPlaceCreatedObject(created, at, null, context.Stack);
            if (world.CaptureExecutionTrace)
                world.ObjectCreationTrace.Add(new ObjectCreationDiagnostic(me.Id, created.Id, guidKey, (byte)mode, (byte)flags,
                    world.CurrentExecutionLocation, at, relativeTo?.ClockManagerIndex ?? 0, canPlace));
            if (!canPlace)
            {
                host?.DiscardObject(created);
                if (createdGroup is null) world.Remove(created.Id);
                else world.RemoveCreatedObjectGroup(created);
                return PrimitiveResult.False;
            }
            if (native && relativeTo is not null)
                world.PlaceObject(created, at, context.Stack, relativeTo, checked((short)(method + 1)));
            else if (native) world.PlaceObject(created, at, context.Stack);
            else host!.PlaceCreated(created, at, relativeTo, method);

            if (native && created.Definition[(int)ObjdField.MasterId] != 0 && SimulationWorld.HasFunction(created, 8))
                RunDynamicMultiTileCallback(world, created);
        }
        else if (world.CaptureExecutionTrace)
        {
            world.ObjectCreationTrace.Add(new ObjectCreationDiagnostic(me.Id, created.Id, guidKey, (byte)mode, (byte)flags,
                world.CurrentExecutionLocation, new ObjectPosition(x, y, level), relativeTo?.ClockManagerIndex ?? 0, null));
        }

        if (inMyHand && !world.PlaceCreatedObjectInHand(me, created))
        {
            host?.DiscardObject(created);
            if (createdGroup is null) world.Remove(created.Id);
            else world.RemoveCreatedObjectGroup(created);
            return PrimitiveResult.False;
        }

        if ((flags & 4) != 0 && created is XPerson arrived && arrived[PersonData.PersonType] != 0)
        {
            if (native) world.ArriveCreatedPerson(arrived);
            else host?.ArriveCreatedPerson(arrived);
        }

        if ((flags & 2) != 0 && (flags & 0x10) != 0)
            throw new SimulationException(SimulationError.MoreThanOnePassingValueSpecified);
        if (((flags & 2) != 0 || (flags & 0x10) != 0) &&
            world.SavedExecutionStacks.TryGetValue(created.Id, out var createdStack) &&
            createdStack.Frames.Count > 0)
        {
            var targetFrame = createdStack.Frames.FirstOrDefault(candidate => candidate.TreeId != -1) ?? createdStack.Frames[0];
            var passedValue = (flags & 2) != 0 ? me.StackObjectKey : me.Temporaries[0];
            if (targetFrame.Parameters.Length > 0)
                targetFrame.Parameters[0] = passedValue;
            else if (targetFrame.Locals.Length > 0)
                targetFrame.Locals[0] = passedValue;
            if ((flags & 2) != 0)
            {
                targetFrame.StackObject = frame.StackObject;
                createdStack.Frames[0].StackObject = frame.StackObject;
            }
            TreeRunner.SaveFrames(created, createdStack);
        }
        else if ((flags & 2) != 0 && frame.Parameters.Length > 0)
        {
            var stack = context.Stack ?? throw new InvalidOperationException(
                "Create Object flag 0x02 needs the active tree stack to update its first frame's stack-object field.");
            frame.Parameters[0] = me.StackObjectKey;
            if (stack.Frames.Count > 0) stack.Frames[0].StackObject = me.StackObjectKey;
        }
        else if ((flags & 0x10) != 0 && frame.Parameters.Length > 0)
        {
            frame.Parameters[0] = me.Temporaries[0];
        }

        frame.StackObject = created.StackObjectKey;
        return PrimitiveResult.True;
    }

    /// <summary>After placing a dynamic multi-tile object, connects adjacent same-type multi-tile chains
    /// and executes function 8 on each member with an 8-direction neighbor bitmask in parameter 0.</summary>
    private static void RunDynamicMultiTileCallback(SimulationWorld world, XObject created)
    {
        var typeKey = created.TypeRecord?.Key;
        if (typeKey is not null)
        {
            for (short dir = 0; dir < 8; dir += 2)
            {
                var (ox, oy) = LotGeometry.FootprintOffset(dir);
                var adjTileX = (created.Position.X + ox) >> 4;
                var adjTileY = (created.Position.Y + oy) >> 4;
                foreach (var other in world.WalkOrder)
                {
                    if (ReferenceEquals(other, created) || other.TypeRecord?.Key != typeKey) continue;
                    if (other.Position.Height != created.Position.Height ||
                        (other.Position.X >> 4) != adjTileX || (other.Position.Y >> 4) != adjTileY) continue;
                    JoinMultiTileChains(world, other, created);
                }
            }
        }

        var members = world.MultiTileMembers(created);
        var memberSet = new HashSet<short>(members.Select(m => m.Id));
        foreach (var member in members)
        {
            var tree = SimulationWorld.FunctionTreeId(member, 8);
            if (tree == 0 || member.TypeRecord?.BehaviorScope is not { } scope) continue;
            var mask = 0;
            for (var step = 0; step < 8; step++)
            {
                var (ox, oy) = LotGeometry.FootprintOffset((short)((member[ObjectData.Direction] + step) % 8));
                var checkTileX = (member.Position.X + ox) >> 4;
                var checkTileY = (member.Position.Y + oy) >> 4;
                if (world.WalkOrder.Any(other =>
                        memberSet.Contains(other.Id) &&
                        other.Position.Height == member.Position.Height &&
                        (other.Position.X >> 4) == checkTileX &&
                        (other.Position.Y >> 4) == checkTileY))
                {
                    mask |= 1 << step;
                }
            }
            TreeRunner.RunWithParameters(world, member, scope, tree, 0, [unchecked((short)mask), 0, 0, 0]);
        }
    }

    private static void JoinMultiTileChains(SimulationWorld world, XObject first, XObject second)
    {
        var lead1 = first.MultiTileMasterId == 0 ? first : world.ObjectById(first.MultiTileMasterId) ?? first;
        var lead2 = second.MultiTileMasterId == 0 ? second : world.ObjectById(second.MultiTileMasterId) ?? second;
        if (ReferenceEquals(lead1, lead2)) return;

        var tail = first;
        var visited = new HashSet<short>();
        while (tail.MultiTileNextId != 0 && visited.Add(tail.Id) && world.ObjectById(tail.MultiTileNextId) is { } next)
            tail = next;
        tail.MultiTileNextId = lead2.Id;

        var current = lead2;
        visited.Clear();
        while (current is not null && visited.Add(current.Id))
        {
            current.MultiTileMasterId = lead1.Id;
            current = current.MultiTileNextId == 0 ? null : world.ObjectById(current.MultiTileNextId);
        }
    }
}
