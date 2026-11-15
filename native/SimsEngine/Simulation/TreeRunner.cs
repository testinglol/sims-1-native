using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Runs a behavior tree to its return with the primitives ported so far. Dispatch follows Sims.exe
/// FUN_00452288: the stored opcode is masked with 0x7fff (bit 15 is the debugger breakpoint, already split out by
/// <see cref="Bhav.Read"/>), the operand is the 8 bytes after the 4-byte header, and each ported opcode runs its
/// primitive: 0 Sleep (<see cref="SleepPrimitive"/>), 1 Generic Sims Call (FUN_0044f7e6, <see cref="GenericSimsCallPrimitive"/>),
/// 2 Expression (FUN_00452c85, <see cref="ExpressionPrimitive"/>),
/// 4 Grab (0x451e1a, <see cref="GrabPrimitive"/>), 5 Drop (0x454470, <see cref="DropPrimitive"/>),
/// 7 Refresh (0x451d3f), 8 Random Number (FUN_00451eab), 9 Burn (0x44d9af), 0xa Tutorial (0x44d946), 0xb Get Distance To, 0xc Get Direction To, 0xd Push Interaction (0x44cd6b), 0xe Find Best Object For Function
/// (0x44f2ff), 0xf Breakpoint (FUN_00451e74), 0x10 Find Location For (FUN_0044b408, <see cref="FindLocationForPrimitive"/>),
/// 0x12 Remove Object Instance (FUN_00451baa), 0x13 Make New Character (0x44ae48, <see cref="MakeNewCharacterPrimitive"/>),
/// 0x14 Run Functional Tree (FUN_0044f6c1), 0x15 Show String (FUN_00451af7),
/// 0x17 Play Sound Event (FUN_00452862), 0x18 Old Relationship (0x44d5d3), 0x19 Alter Budget, 0x1a Relationship
/// (0x44d1bc), 0x1c Run Tree By Name (FUN_0044ba66), 0x1f Set to Next (0x44ebc0, implemented modes per the primitive's port notes), 0x20 Test Object Type
/// (FUN_0044adcc), 0x21 Find 5 Worst Motives (0x44d0b7), 0x22 UI Effect (0x44ad3f), 0x23 Special Effect (0x44aae6), 0x24 Dialog (0x451592, <see cref="DialogPrimitive"/>),
/// 0x29 Set Balloon/Headline (0x44b764), 0x2a Create New Object Instance
/// (FUN_0044e108, <see cref="CreateObjectInstancePrimitive"/>), 0x2b Drop Onto (0x44e9aa, <see cref="DropOntoPrimitive"/>),
/// 0x2e Snapshot (0x4549b6, <see cref="SnapshotPrimitive"/>), 0x30 Stop All Sounds (0x45257a), 0x31 Notify Stack Object Out Of Room
/// (0x452199), 0x32 Change Action String (0x44cc47) and 0x33 Manage Inventory (FUN_00455338,
/// <see cref="ManageInventoryPrimitive"/>).
/// Any other opcode the main dispatcher does not list throws error 5 (<see cref="SimulationError.IllegalPrimitiveCode"/>).
/// Sims.exe's person dispatcher FUN_0045cd1f is the vtable slot 0 of the person class only (the PERS class at 0x667378,
/// built for definition type 2 by FUN_004409db and FUN_00455bb9); the other object classes have FUN_00452288 in slot 0
/// (vtables 0x666d34 and 0x666c20). FUN_0045cd1f runs its own opcodes 3 (<see cref="FindBestInteractionPrimitive"/>), 6
/// (<see cref="ChangeSuitAccessoryPrimitive"/>), 0x11 (<see cref="IdleForInputPrimitive"/>), 0x16 (<see cref="LookTowardsPrimitive"/>),
/// 0x1b (<see cref="GoToRelativePositionPrimitive"/>), 0x1d (<see cref="SetMotiveChangePrimitive"/>), 0x1e
/// (<see cref="GosubFoundActionPrimitive"/>), 0x25 (<see cref="TestSimInteractingWithPrimitive"/>), 0x2c
/// (<see cref="AnimateSimPrimitive"/>), 0x2d (<see cref="GoToRoutingSlotPrimitive"/>) and 0x2f (<see cref="ReachPutPrimitive"/>)
/// for a person; for any other object those are error 5, since
/// FUN_00452288 does not list them. Every opcode either dispatcher lists is ported; anything else is error 5.
/// <para>Stepping, branching, returns, gosubs and the error exits are <see cref="TreeStack"/>'s, after
/// FUN_00474cae and FUN_00474b22: 0xFE returns true, 0xFF returns false, 0xFD raises error 1100, and a target
/// past the last instruction raises 1102. Trees start at instruction 0, or at <c>start</c> clamped to the last
/// one for type 4 trees (FUN_004749b7).</para></summary>
public static class TreeRunner
{
    /// <summary>The opcodes FUN_0045cd1f ports for a person (see the summary); other objects do not reach them.</summary>
    private static readonly HashSet<ushort> PersonPrimitives = [3, 6, 0x11, 0x16, 0x1b, 0x1d, 0x1e, 0x25, 0x2c, 0x2d, 0x2f];

    /// <summary>Creates a transient interpreter context for native editor callbacks that run synchronously outside a
    /// saved object thread. The caller passes it to the same placement entry point, which runs each source function-6/9
    /// callback through this runner and its ordinary primitive dispatch.</summary>
    public static TreeStack CreateTransientStack(SimulationWorld world, XObject owner)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);
        return new Runner(world, owner)
        {
            DefaultParameters = owner.Temporaries,
            StepLimit = world.TreeStepLimit,
        };
    }

    /// <summary>Runs <paramref name="tree"/> (as id <paramref name="id"/>) to its return. Gosubs to other ids look
    /// in <paramref name="table"/>.</summary>
    public static bool Run(in TreeContext context, Bhav tree, int start = 0, ITreeTable? table = null, short id = 0x1000)
    {
        return RunWithParameters(context.World, context.Me, new RootTable(tree, id, table), id,
            context.Frame.StackObject, [(short)start]);
    }

    /// <summary>FUN_00474b22 runs a nested tree with an explicit argument block and stack-object key.
    /// Guard calls use the target's code scope while the person remains the running object.</summary>
    public static bool RunWithParameters(SimulationWorld world, XObject me, ITreeTable? table, short id,
        short stackObject, ReadOnlySpan<short> parameters)
    {
        var savedRemoval = world.SelfRemovalEndsRun;
        var savedActionWrite = world.ActionStringRecordWriteEnabled;
        world.SelfRemovalEndsRun = false;
        world.ActionStringRecordWriteEnabled = false;
        try
        {
            var runner = new Runner(world, me) { DefaultParameters = me.Temporaries, StepLimit = world.TreeStepLimit };
            return runner.RunToReturn(table, parameters, false, id, stackObject);
        }
        finally
        {
            world.SelfRemovalEndsRun = savedRemoval;
            world.ActionStringRecordWriteEnabled = savedActionWrite;
        }
    }

    /// <summary>FUN_00474979/FUN_004749b7 append an initializer frame to the object's resumable stack. The reset caller
    /// supplies four zero words and the behavior scope selected from the object's type record.</summary>
    public static TreeStack? StartObjectTree(SimulationWorld world, XObject owner, ITreeTable table, short id,
        short stackObject, short codeOwner, ReadOnlySpan<short> parameters)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(table);
        var runner = new Runner(world, owner)
        {
            DefaultParameters = owner.Temporaries,
            StepLimit = world.TreeStepLimit,
        };
        runner.RestoreFrame(new StackFrame(0, [], []) { TreeId = -1, Waiting = true });
        if (!runner.Push(table, parameters, false, id, codeOwner)) return null;
        runner.Top!.StackObject = stackObject;
        return runner;
    }

    /// <summary>Copies a live interpreter stack to the format written by FUN_00474801. The stack size is the sum of
    /// FUN_00475305's aligned frame allocations, including its 16-byte frame header.</summary>
    public static void SaveFrames(XObject owner, TreeStack stack)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(stack);
        owner.SavedStack.Clear();
        var size = 0;
        foreach (var frame in stack.Frames)
        {
            var tree = frame.TreeId == -1 ? ushort.MaxValue : unchecked((ushort)frame.TreeId);
            if (frame.TreeBreakpoint) tree |= 0x8000;
            owner.SavedStack.Add(new ObjectStackFrame(frame.StackObject, tree, checked((short)frame.Instruction),
                frame.Parameters.ToArray(), frame.Locals.ToArray(), frame.WaitState, frame.CodeOwner));
            size = checked(size + (((frame.Parameters.Length + 3 + frame.Locals.Length) & ~3) * 2 + 0x10));
        }
        owner.SavedStackSize = size;
    }

    /// <summary>Runs one restored object's active tree until it stops or yields. Sims.exe's FUN_00452237 object
    /// simulation tail (LockoutCount and runtime-slot ticking) belongs to the caller, after this result is known.</summary>
    public static bool RunRestoredObjectStack(SimulationWorld world, XObject owner)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);
        if (!world.SavedExecutionStacks.TryGetValue(owner.Id, out var stack))
            throw new InvalidOperationException($"Object {owner.Id} has no restored execution stack.");

        // FUN_0044350d derives DAT_006c4d60 from both the object-manager vector and eligible neighborhood records;
        // the TreeStack default is only a test/interpreter fallback.
        stack.StepLimit = Math.Max(stack.StepLimit, world.TreeStepLimit);
        // An all-0xFF gosub passes the runner's default block (FUN_004749b7 reads runner +0x24). The saved stacks show
        // it holding the running object's temporaries: global 367 sets Temp 0 to 450 and its all-0xFF Sleep records
        // parameter 450. Inferred: the block is the object's temporary array.
        stack.DefaultParameters = owner.Temporaries;
        var stopped = stack.Run();
        SaveFrames(owner, stack);
        if (stack.Frames.Count == 0) world.SavedExecutionStacks.Remove(owner.Id);
        return stopped;
    }

    /// <summary>Rebuilds the interpreter frames saved by each object's serializer. Sims.exe FUN_0043c0f9 restores
    /// the trailing code-owner index to that type record's behavior scope; <paramref name="resolveCodeOwner"/> must
    /// perform that registry lookup. FUN_00474801 stores the resolved scope on the frame, which may differ from the
    /// object's own scope. A 0xffff tree has no active tree and uses no scope.</summary>
    public static TreeStack RestoreSavedStack(SimulationWorld world, XObject owner, Func<short, ITreeTable?> resolveCodeOwner)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(resolveCodeOwner);
        var runner = new Runner(world, owner);
        foreach (var saved in owner.SavedStack)
        {
            var noTree = saved.Tree == ushort.MaxValue;
            var treeId = noTree ? (short)-1 : (short)(saved.Tree & 0x7fff);
            var breakpoint = !noTree && (saved.Tree & 0x8000) != 0;
            var table = noTree ? null : resolveCodeOwner(saved.CodeOwner);
            var tree = noTree ? null : table?.Find(treeId);
            runner.RestoreFrame(new StackFrame(saved.StackObject, saved.Parameters.ToArray(), saved.Locals.ToArray())
            {
                Tree = tree,
                TreeId = treeId,
                TreeBreakpoint = breakpoint,
                Instruction = saved.Node,
                WaitState = noTree && saved.Value0x08 == 0 ? 1 : saved.Value0x08,
                CodeOwner = saved.CodeOwner,
                Table = noTree ? null : table,
            });
        }
        return runner;
    }

    private sealed class RootTable(Bhav tree, short id, ITreeTable? rest) : ITreeTable
    {
        public Bhav? Find(short treeId) => treeId == id ? tree : rest?.Find(treeId);
        public IReadOnlyList<short>? Constants(ushort id) => rest?.Constants(id);
    }

    private sealed class Runner(SimulationWorld world, XObject me) : TreeStack
    {
        public override int EffectiveDepth =>
            world.SavedExecutionStacks.TryGetValue(me.Id, out var activeStack) && !ReferenceEquals(this, activeStack)
                ? activeStack.Frames.Count + Frames.Count
                : Frames.Count;

        protected override object BeginRunToReturn()
        {
            var saved = (world.SelfRemovalEndsRun, world.ActionStringRecordWriteEnabled);
            world.SelfRemovalEndsRun = false;
            world.ActionStringRecordWriteEnabled = false;
            return saved;
        }

        protected override void EndRunToReturn(object? state)
        {
            if (state is not ValueTuple<bool, bool> saved) return;
            world.SelfRemovalEndsRun = saved.Item1;
            world.ActionStringRecordWriteEnabled = saved.Item2;
        }

        protected override void Called(StackFrame caller, short treeId, short[]? parameters)
        {
            if (world.CaptureExecutionTrace)
                world.TreeCallTrace.Add(new SimulationTreeCallTrace(me.Id, caller.TreeId, caller.Instruction,
                    treeId, parameters?.ToArray()));
        }

        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            if (world.CaptureExecutionTrace)
                world.ExecutionTrace.Add(new SimulationTraceEntry(me.Id, frame.TreeId, frame.Instruction, instruction.Opcode,
                    Convert.ToHexString(instruction.Operand), frame.StackObject, (me as XPerson)?.Temporaries.ToArray() ?? [],
                    frame.Parameters.ToArray(), frame.Locals.ToArray()));
            PrimitiveResult result;
            try
            {
                result = Dispatch(new TreeContext(world, me, frame) { Stack = this }, instruction);
            }
            catch (Exception error)
            {
                error.Data["executionLocation"] = world.CurrentExecutionLocation;
                throw;
            }
            if (world.CaptureExecutionTrace)
                world.PrimitiveResultTrace.Add(new SimulationPrimitiveResultTrace(me.Id, frame.TreeId, frame.Instruction,
                    instruction.Opcode, result, frame.Parameters.ToArray(), frame.CodeOwner));
            return result;
        }

        protected override void Popped(StackFrame frame)
        {
            if (me is not XPerson person) return;
            person.NativeRouteParents.RemoveAll(route => ReferenceEquals(route.OwnerFrame, frame));
            if (ReferenceEquals(person.NativeRoute?.OwnerFrame, frame))
            {
                if (person.NativeRouteParents.Count > 0)
                {
                    var last = person.NativeRouteParents.Count - 1;
                    person.NativeRoute = person.NativeRouteParents[last];
                    person.NativeRouteParents.RemoveAt(last);
                }
                else
                {
                    person.NativeRoute = null;
                }
            }
            var currentDepth = EffectiveDepth;
            PersonInteractionStack.Release(person, currentDepth);
            if (person.PendingActionRequests.Count == 0) return;
            var request = person.PendingActionRequests[^1];
            if (currentDepth != request.Priority - 1 && currentDepth >= request.Priority) return;

            person.PendingActionRequests.RemoveAt(person.PendingActionRequests.Count - 1);
            if (currentDepth == request.Priority - 1)
            {
                foreach (var surviving in person.PendingActionRequests)
                    if (surviving.Flag) person.SetCurrentActionSecondaryReference(surviving.Target);
            }
            if (person.PendingActionRequests.Count == 0)
                world.ApplyStartedInteractionAction(person, ActionQueueSlot.EmptyRecord());
        }
    }

    private static PrimitiveResult Dispatch(in TreeContext context, BhavInstruction instruction)
    {
        var objectType = context.Me.Definition[(int)ObjdField.Type];
        int? objectGuid = context.Me.TypeRecord is { Key: not 0 } typeRecord ? typeRecord.Key : null;
        var tileX = context.Me.Position.X >> 4;
        var tileY = context.Me.Position.Y >> 4;
        var tileValue = context.World.TileValueAt(tileX, tileY, context.Me.Position.Height);
        var stackObject = context.StackObject;
        var stackType = stackObject?.TypeRecord is { } stackRecord ? $", GUID 0x{stackRecord.Key:X8}" : string.Empty;
        context.World.CurrentExecutionLocation = $"object {context.Me.Id} (OBJD type {objectType}, GUID {(objectGuid is { } guid ? $"0x{guid:X8}" : "unknown")}), BHAV {context.Frame.TreeId}, instruction {context.Frame.Instruction}, opcode 0x{instruction.Opcode:X}, operand {Convert.ToHexString(instruction.Operand)}, stack key {context.Frame.StackObject}, stack object {(stackObject is null ? "none" : $"{stackObject.Id} (OBJD type {stackObject.Definition[(int)ObjdField.Type]}{stackType})")}, position {context.Me.Position}, tile key {tileValue}";
        var operand = instruction.Operand;
        switch (instruction.Opcode)
        {
            case InteractionLifecycle.AcquireLaneOpcode:
            case InteractionLifecycle.ReleaseLaneOpcode:
            case InteractionLifecycle.SetAttachmentOpcode:
            case InteractionLifecycle.InterruptRequestedOpcode:
                return InteractionLifecycle.Execute(context, instruction);
            case 0: return SleepPrimitive.Execute(context, operand);
            case 1: return GenericSimsCallPrimitive.Execute(context, operand);
            case 2: return ExpressionPrimitive.Execute(context, operand) ? PrimitiveResult.True : PrimitiveResult.False;
            case 4: return GrabPrimitive.Execute(context, operand);
            case 5: return DropPrimitive.Execute(context, operand);
            case 7: return RefreshPrimitive.Execute(context, operand);
            case 8: return RandomNumberPrimitive.Execute(context, operand);
            case 9: return BurnPrimitive.Execute(context, operand);
            case 0xa: return TutorialPrimitive.Execute(context, operand);
            case 0xb: return GetDistanceToPrimitive.Execute(context, operand);
            case 0xc: return GetDirectionToPrimitive.Execute(context, operand);
            case 0xd: return PushInteractionPrimitive.Execute(context, operand);
            case 0xe: return FindBestObjectForFunctionPrimitive.Execute(context, operand);
            case 0xf: return BreakpointPrimitive.Execute(context, operand);
            case 0x10: return FindLocationForPrimitive.Execute(context, operand);
            case 0x12: return RemoveObjectInstancePrimitive.Execute(context, operand);
            case 0x13: return MakeNewCharacterPrimitive.Execute(context, operand);
            case 0x14: return RunFunctionalTreePrimitive.Execute(context, operand);
            case 0x15: return ShowStringPrimitive.Execute(context, operand);
            case 0x17: return PlaySoundEventPrimitive.Execute(context, operand);
            case 0x18: return OldRelationshipPrimitive.Execute(context, operand);
            case 0x19: return AlterBudgetPrimitive.Execute(context, operand);
            case 0x1a: return RelationshipPrimitive.Execute(context, operand);
            case 0x1c: return RunTreeByNamePrimitive.Execute(context, operand);
            case 0x1f: return SetToNextPrimitive.Execute(context, operand);
            case 0x20: return TestObjectTypePrimitive.Execute(context, operand);
            case 0x21: return Find5WorstMotivesPrimitive.Execute(context, operand);
            case 0x22: return UiEffectPrimitive.Execute(context, operand);
            case 0x23: return SpecialEffectPrimitive.Execute(context, operand);
            case 0x24: return DialogPrimitive.Execute(context, operand);
            case 0x29: return SetBalloonHeadlinePrimitive.Execute(context, operand);
            case 0x2a: return CreateObjectInstancePrimitive.Execute(context, operand);
            case 0x2b: return DropOntoPrimitive.Execute(context, operand);
            case 0x2e: return SnapshotPrimitive.Execute(context, operand);
            case 0x30: return StopAllSoundsPrimitive.Execute(context, operand);
            case 0x31: return NotifyStackObjectOutOfRoomPrimitive.Execute(context, operand);
            case 0x32: return ChangeActionStringPrimitive.Execute(context, operand);
            case 0x33: return ManageInventoryPrimitive.Execute(context, operand);
        }
        if (context.Me is XPerson && PersonPrimitives.Contains(instruction.Opcode)) return PersonDispatch(context, instruction);
        // Only the person class's vtable (0x667378) runs FUN_0045cd1f; for any other object its opcodes reach
        // FUN_00452288's default, error 5. Every opcode FUN_00452288 lists is ported.
        throw new SimulationException(SimulationError.IllegalPrimitiveCode);
    }

    /// <summary>FUN_0045cd1f for a person: its ported opcodes, each with its primitive. Reached only when the running object
    /// is a person (<see cref="XPerson"/>).</summary>
    private static PrimitiveResult PersonDispatch(in TreeContext context, BhavInstruction instruction)
    {
        var operand = instruction.Operand;
        return instruction.Opcode switch
        {
            3 => FindBestInteractionPrimitive.Execute(context, operand),
            6 => ChangeSuitAccessoryPrimitive.Execute(context, operand),
            0x11 => IdleForInputPrimitive.Execute(context, operand),
            0x16 => LookTowardsPrimitive.Execute(context, operand),
            0x1b => GoToRelativePositionPrimitive.Execute(context, operand),
            0x1d => SetMotiveChangePrimitive.Execute(context, operand),
            0x1e => GosubFoundActionPrimitive.Execute(context),
            0x25 => TestSimInteractingWithPrimitive.Execute(context, operand),
            0x2c => AnimateSimPrimitive.Execute(context, operand),
            0x2d => GoToRoutingSlotPrimitive.Execute(context, operand),
            _ => ReachPutPrimitive.Execute(context, operand),
        };
    }
}
