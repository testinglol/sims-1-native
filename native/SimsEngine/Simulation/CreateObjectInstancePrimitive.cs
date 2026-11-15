using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Create New Object Instance primitive (opcode 0x2a), after Sims.exe FUN_0044e108
/// (analysis/full-decompilation/0044e108.c).
/// <para>Operand: bytes 0 to 3 hold the created type's key, unless byte 5 bit 2 is set, when the key comes from
/// +0x8 of the frame stack object's one-based lot-manager neighbor-table entry
/// (<see cref="SimulationWorld.StackObjectCreateKey"/>, FUN_00425b48 calling FUN_0042573e; a missing entry or zero
/// key answers false). Byte 4 is the placement mode (0 to 14; a higher
/// value falls out of the switch and keeps the zero position, as Sims.exe does), byte 5 the flags, and byte 6 the
/// frame local that mode 8 reads and the local whose value mode 9 steps from.</para>
/// <para>Modes: 0 in front of the running object (the footprint offset of its direction,
/// <see cref="LotGeometry.FootprintOffset"/>); 1 on its own tile and 2 in the running Sim's first real slot
/// (the slot-zero request is normalized to hierarchy slot 1 by FUN_00432771);
/// 3 in front of the stack object (its footprint offset), constrained to
/// the stack object's room; 4 on the stack object's tile with it as the relative, in its room; 5 on the running
/// object's own tile with no relative; 6 off the world at (-16, -16) on level 1 with no placement; 8 on the tile
/// of the lot-table object named by the frame local (a negative index or one past the locals is error 8, an
/// unknown table entry answers false); 9 beside the running object in the direction its own direction plus the
/// local gives (cardinal directions only, anything else stays in place) with the facing the same sum plus 4
/// gives; 10 and 11 on its own tile with placement methods 2 and 3; 12 to 14 on the stack object's tile with
/// methods 2 to 4, in its room. A missing stack object answers false wherever a mode needs one, as Sims.exe does
/// when FUN_00407498 finds nothing.</para>
/// <para>Order: the same-type scan (byte 5 bit 1 answers false when an object of the created type already stands
/// on the target tile, except for hand and stack-slot modes), then the empty-tile scan (bit 3) and, when placing, the lot-bounds check, then the room
/// check (skipped without placement, for room -5, and when byte 5 bit 6 is set, which also clears the facing) and
/// the facing check (<see cref="ISimulationHost.FacingClear"/>), then the creation
/// (<see cref="ISimulationHost.CreateObjectInstance"/>; null answers false). The new object joins the world at the
/// worked-out position; a person is bound to its avatar (<see cref="ISimulationHost.InitCreatedPerson"/>), takes
/// the stack object's direction when byte 5 bit 5 is set (through
/// <see cref="ISimulationHost.RotateCreatedObject"/> except for a person, which takes it directly), is centered
/// on its tile for a multitile part (MasterId set) when the running object has function 8, and is placed through
/// the check and the action (<see cref="ISimulationHost.CanPlaceCreated"/> and
/// <see cref="ISimulationHost.PlaceCreated"/>; a refused placement discards it at once,
/// <see cref="ISimulationHost.DiscardObject"/>). A created person with a nonzero PersonType arrives when byte 5
/// bit 2 is set (<see cref="ISimulationHost.ArriveCreatedPerson"/>). Byte 5 bit 1 together with bit 4 is error 54
/// (<see cref="SimulationError.MoreThanOnePassingValueSpecified"/>). The frame's stack object becomes the new
/// object.</para>
/// <para>Mode 7 reads parameter 0 from the current frame (FUN_004752f8) and resolves it through the running
/// object's one-based object table (FUN_00407498). Flag 0x10 writes the running object's temporary 0 to parameter
/// 0; flag 0x02 writes the running object's raw +0xf0 key to parameter 0 and to the first frame's stack-object field
/// at +4 (FUN_00475115, FUN_004752f8, FUN_00453181 case 10). Inferred: the broader meaning of the neighbor-table
/// +0x8 dword; the multitile snap (MasterId with the tile-center
/// alignment when function 8 is present); the rotation converging to the masked direction.</para></summary>
public static class CreateObjectInstancePrimitive
{
    private const short NoRoomConstraint = -5;
    private const int NoFacing = -1;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        // A content-backed factory means the world can create and place the object itself; without one the host owns
        // the object construction and placement virtuals.
        var host = world.Host;
        var native = world.ObjectFactory is not null || host is null;
        var mode = operand[4];
        var flags = operand[5];
        // Modes 3, 4 and 12-14 call FUN_00407498 on the running object's +0xdc table.
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

        // The position, level, room, placement relative and method, and facing by mode: FUN_0044e108's switch on
        // operand byte 4. The room defaults to -5 (no constraint) and the facing to -1 (no check).
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
                // FUN_0044e108's mode-7 branch reads parameter 0 with FUN_004752F8, then indexes
                // the running object's +0xdc table through FUN_00407498. The generic resolver has
                // legacy fixture/id fallbacks that are not part of this Sims.exe path.
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
                // FUN_0044e108 case 8 reads the lot object's +0xdc table directly after the
                // local-index bounds check; keep this lookup native like mode 7.
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
            // FUN_00440b6d registers/activates the created instance and sets its clock-manager bit 16 via FUN_0044582f.
            // On the native path it also runs the object's constructor defaults and init tree (FUN_00431c90).
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
            // Hand objects face their holder, not the stack object.
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
            // FUN_00432728 forwards the relative object and the method as its slot request (FUN_00432771 then
            // adds one); FUN_00432a70 validates and FUN_004327d9 links the new object into that slot.
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

    /// <summary>FUN_00423621 / FUN_004234de / FUN_0042350b: after placing a dynamic multi-tile object (MasterId != 0
    /// and function 8 present), joins any cardinal-adjacent same-type multi-tile chains and runs function 8 on each
    /// member with the 8-direction neighbor bitmask in parameter 0.</summary>
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
