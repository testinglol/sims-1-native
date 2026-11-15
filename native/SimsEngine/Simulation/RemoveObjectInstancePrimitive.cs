using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Remove Object Instance primitive (opcode 0x12), after Sims.exe FUN_00451baa.
/// <para>Operand: the selector (a short at byte 0): 0 is the running object (me), 1 the stack object and 2 no object
/// (id 0); any other value raises error 9 (Illegal owner field in data reference). The flags (byte 2): bit 0 set keeps
/// the run going when the running object is removed; bit 1 is passed to FUN_00442db0 inverted, which sets or clears the
/// object's 0x1000 flag.</para>
/// <para>Behavior: the object with the selected id is looked up. When there is none the primitive is false. Otherwise
/// FUN_00442db0 queues it for removal unless an attached group member or the target is already queued, and
/// FUN_00425ab3 saves a selected person's data to its neighbor record. The result is
/// <see cref="PrimitiveResult.Stop"/> when the removed id is the running object, <see cref="SimulationWorld.SelfRemovalEndsRun"/>
/// is set and bit 0 of the flags is clear. Otherwise it is true.</para>
/// <para>FUN_00442db0 walks attached objects through the host's original-geometry iterator, skips a group if one of its
/// members or the target is already queued, invokes the object's +0x50 removal callback, updates flag 0x1000 from operand
/// bit 1, then queues the target. A multi-tile part redirects to its recorded master unless Sims.exe setting 8 is enabled.
/// If the originally selected object is a person, FUN_00425ab3 copies its saved fields into its neighbor record.</para></summary>
public static class RemoveObjectInstancePrimitive
{
    private const uint PendingRemovalFlag = 0x1000;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var selector = (short)(operand[0] | operand[1] << 8);
        var flags = operand[2];
        var id = selector switch
        {
            0 => context.Me.Id,
            1 => context.Frame.StackObject,
            2 => (short)0,
            _ => throw new SimulationException(SimulationError.IllegalOwnerFieldInDataReference),
        };
        var world = context.World;
        var runningObjectId = context.Me.Id;
        var item = world.ObjectById(id);
        if (world.CaptureExecutionTrace && item is not null)
            world.ObjectRemovalTrace.Add(new ObjectRemovalDiagnostic(context.Me.Id, id, item.Id, world.CurrentExecutionLocation));
        if (item is null) return PrimitiveResult.False;
        var selected = item;

        if (item.Definition[(int)ObjdField.MasterId] != 0 && !SimulationWorld.HasFunction(item, 8))
        {
            // cXMTObject +0x190 stores the lead pointer and +0x3c its lot object id. A nonzero link is followed
            // by FUN_00442db0; with no lead, it leaves the selected part as the removal target.
            if (item.MultiTileMasterId != 0 && world.ObjectById(item.MultiTileMasterId) is { } master)
                item = master;
        }

        // FUN_0043270d/00432664 climb the target's +0x7e parent chain (FUN_004327c8 resolves each parent through
        // the occupant's table). If any ancestor is already pending, FUN_00442db0 returns without another callback
        // or queue insertion; otherwise it removes the target.
        var groupAlreadyPending = world.RemovalGroupParents(item).Any(member => world.PendingRemovals.Contains(member.Id));
        if (!groupAlreadyPending && !world.PendingRemovals.Contains(item.Id))
        {
            if (ReferenceEquals(item, selected) && SimulationWorld.HasFunction(selected, 8))
                world.BeginObjectRemoval(item, 1);
            else
                foreach (var member in world.MultiTileMembers(item))
                    world.BeginObjectRemoval(member, 1);
            item.Flags &= ~PendingRemovalFlag;
            if ((flags & 2) != 0) item.Flags |= PendingRemovalFlag;
            world.PendingRemovals.Add(item.Id);
        }
        if (selected is XPerson person) world.SavePersonRecord(person);
        return Result();

        PrimitiveResult Result() => id == runningObjectId && world.SelfRemovalEndsRun && (flags & 1) == 0
            ? PrimitiveResult.Stop
            : PrimitiveResult.True;
    }
}
