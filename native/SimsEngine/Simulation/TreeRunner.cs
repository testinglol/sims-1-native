using SimsEngine.Formats;

namespace SimsEngine.Simulation;

public static class TreeRunner
{
    private static readonly HashSet<ushort> PersonPrimitives = [3, 6, 0x11, 0x16, 0x1b, 0x1d, 0x1e, 0x25, 0x2c, 0x2d, 0x2f];

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

    public static bool Run(in TreeContext context, Bhav tree, int start = 0, ITreeTable? table = null, short id = 0x1000)
    {
        return RunWithParameters(context.World, context.Me, new RootTable(tree, id, table), id,
            context.Frame.StackObject, [(short)start]);
    }

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

    public static bool RunRestoredObjectStack(SimulationWorld world, XObject owner)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(owner);
        if (!world.SavedExecutionStacks.TryGetValue(owner.Id, out var stack))
            throw new InvalidOperationException($"Object {owner.Id} has no restored execution stack.");

        stack.StepLimit = Math.Max(stack.StepLimit, world.TreeStepLimit);
        stack.DefaultParameters = owner.Temporaries;
        var stopped = stack.Run();
        SaveFrames(owner, stack);
        if (stack.Frames.Count == 0) world.SavedExecutionStacks.Remove(owner.Id);
        return stopped;
    }

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
        var operand = instruction.Operand;
        context.World.SetExecutionLocation(
            context.Me,
            context.Frame.TreeId,
            context.Frame.Instruction,
            instruction.Opcode,
            operand,
            context.Frame.StackObject,
            context.StackObject);
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
        throw new SimulationException(SimulationError.IllegalPrimitiveCode);
    }

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
