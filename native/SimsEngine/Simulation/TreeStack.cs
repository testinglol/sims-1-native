using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Where a frame finds the trees it calls: Sims.exe FUN_0040204f looks a tree id up in the table the
/// frame holds at +0xc (the object's <see cref="Content.BehaviorScope"/>).</summary>
public interface ITreeTable
{
    Bhav? Find(short id);

    /// <summary>FUN_0040235e: the code owner's BCON values for this resource id, or null when absent. The native BHAV
    /// constant-read caller passes its override flag as true.</summary>
    IReadOnlyList<short>? Constants(ushort id, bool allowTuningOverride = false) => null;
}

/// <summary>The tree runner's own errors, the int codes FUN_00474cae and FUN_00474b22 throw. The game shows no text
/// for them; the names are descriptive.</summary>
public enum TreeRunError
{
    /// <summary>1000: a gosub named a tree that exists but could not be entered (no tree table, or no instruction
    /// to start at).</summary>
    TreeNotEntered = 1000,
    /// <summary>1001: a return with only one frame on the stack.</summary>
    ReturnFromLastFrame = 1001,
    /// <summary>1100: a branch to 0xFD, or an instruction index above 0xFA that is not 0xFE or 0xFF.</summary>
    BadTarget = 1100,
    /// <summary>1101: the step budget (DAT_006c4d60) ran out.</summary>
    StepLimit = 1101,
    /// <summary>1102: the tree or the instruction a frame names does not exist.</summary>
    TreeOrInstructionMissing = 1102,
    /// <summary>1103: a tree run to completion (FUN_00474b22) stopped or yielded instead of returning.</summary>
    NestedRunSuspended = 1103,
}

public sealed class TreeRunException(TreeRunError error, string? detail = null)
    : Exception($"Tree run error {(int)error}: {error}{(detail is null ? string.Empty : $" ({detail})")}")
{
    public TreeRunError Error { get; } = error;
}

/// <summary>What a primitive asks the runner to do (the return value of vtable slot 0 in FUN_00474cae).</summary>
public enum PrimitiveResult
{
    /// <summary>-1: stop this tick; the instruction runs again next time. Any value the step does not name acts
    /// the same.</summary>
    Yield = -1,
    /// <summary>0: take the false branch.</summary>
    False = 0,
    /// <summary>1: take the true branch.</summary>
    True = 1,
    /// <summary>2: end the run (the step returns 1).</summary>
    Stop = 2,
    /// <summary>3: keep running without branching; the same instruction runs again on the next step.</summary>
    Again = 3,
}

/// <summary>What one step leaves the runner doing (FUN_00474cae's return value).</summary>
public enum StepResult
{
    Continue = 0,
    Stop = 1,
    Yield = 2,
}

/// <summary>A tree stack and its interpreter, after Sims.exe FUN_00474cae (one step), FUN_00474ff5 (run until a
/// primitive stops or yields), FUN_00474b22 (run a tree to its return) and FUN_004749b7 (push a tree's frame).
/// <para>Instruction targets are the bytes the file stores: 0x00 to 0xFA are instructions, 0xFE returns true,
/// 0xFF returns false, and 0xFD or anything else raises <see cref="TreeRunError.BadTarget"/>.</para>
/// <para>Subclasses run the primitives (vtable slot 0), see each popped frame (slot 8) and handle breakpoints
/// (slot 0xC).</para></summary>
public abstract class TreeStack
{
    /// <summary>The frames, bottom first (the vector at +0x10/+0x14).</summary>
    public List<StackFrame> Frames { get; } = [];

    /// <summary>Two 32-bit temporaries shared by every frame in this execution thread; no recovered Mac parser command
    /// consumes them.</summary>
    public int[] TempXL { get; } = new int[2];

    /// <summary>Instructions run since the counter was last reset (+4).</summary>
    public int Steps { get; set; }

    /// <summary>The step budget (DAT_006c4d60). Sims.exe FUN_0044350d sets it to
    /// 10000 + 15 times the object-manager vector length + 15 times the product of two FUN_00427162 counts.
    /// That helper scans a separate +0x148/+0x14c record vector and tests a linked object's +0xb4 flags through
    /// FUN_00423f63. Restored lot stacks receive the complete world-derived value; this default is only the
    /// standalone interpreter fallback for synthetic stacks without a world.</summary>
    public int StepLimit { get; set; } = 10000;

    /// <summary>The target of the last branch taken (+0x1c); FUN_00474b22 reads the run's result from it.</summary>
    public int LastTarget { get; private set; }

    /// <summary>The result of the last branch or return (+0x20).</summary>
    public bool LastResult { get; private set; }

    /// <summary>The parameters a gosub passes when its operand is all 0xFF bytes (the block at +0x24).</summary>
    public short[] DefaultParameters { get; set; } = new short[4];

    public StackFrame? Top => Frames.Count == 0 ? null : Frames[^1];

    /// <summary>Logical stack depth for +0xb00 interaction references and pending action requests. When a transient
    /// runner executes a synchronous nested tree (FUN_00474b22) on an object that already owns an active execution
    /// stack, its frames sit above that active stack rather than at depth 0.</summary>
    public virtual int EffectiveDepth => Frames.Count;

    /// <summary>Runs one primitive (opcode below 0x100) for <paramref name="frame"/>.</summary>
    protected abstract PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction);

    /// <summary>Called when a BHAV instruction calls another tree, before its frame is pushed.</summary>
    protected virtual void Called(StackFrame caller, short treeId, short[]? parameters) { }

    /// <summary>Called after each frame is popped (vtable +8).</summary>
    protected virtual void Popped(StackFrame frame) { }

    /// <summary>Called after a run-to-return tree was pushed and before its first instruction. Sims.exe's
    /// FUN_00474b22 subclasses use this boundary to save and clear process-wide execution gates.</summary>
    protected virtual object? BeginRunToReturn() => null;

    /// <summary>Restores any process-wide gates changed by <see cref="BeginRunToReturn"/>.</summary>
    protected virtual void EndRunToReturn(object? state) { }

    /// <summary>A frame or instruction carries the breakpoint flag (vtable +0xC). The step returns what this
    /// returns. Inferred: the game's handler reports "Tree break encountered.".</summary>
    protected virtual StepResult Breakpoint(StackFrame frame, BhavInstruction? instruction) =>
        throw new SimulationException(SimulationError.TreeBreakEncountered);

    /// <summary>Pushes the frame for tree <paramref name="treeId"/> (FUN_004749b7). The table is
    /// <paramref name="table"/>, or the top frame's when it is null; the stack object is the top frame's. The
    /// parameters come from <paramref name="parameters"/>, or <see cref="DefaultParameters"/> when it is null.
    /// Returns false when there is no table, the tree is missing, or it has no instruction to start at.</summary>
    public bool Push(ITreeTable? table, ReadOnlySpan<short> parameters, bool useDefault, short treeId, short codeOwner = 0)
    {
        var top = Top;
        table ??= top?.Table;
        if (table is null) return false;
        var tree = table.Find(treeId);
        if (tree is null) return false;
        var source = useDefault ? DefaultParameters : parameters;
        var count = tree.Instructions.Count;
        var start = 0;
        if (tree.StartsAtCallerInstruction)
        {
            start = source.Length > 0 ? source[0] : 0;
            if (start >= count) start = count - 1;
        }
        if (start < 0 || start >= count) return false;
        // FUN_00475318 copies as many parameters as the tree takes, reading past the source's end if it is
        // shorter; this port fills those with 0 (inferred).
        var arguments = new short[tree.ArgumentCount];
        for (var index = 0; index < arguments.Length && index < source.Length; index++) arguments[index] = source[index];
        Frames.Add(new StackFrame(top?.StackObject ?? 0, arguments, new short[tree.LocalCount])
        {
            Tree = tree,
            TreeId = treeId,
            Table = table,
            CodeOwner = codeOwner,
            Instruction = start,
        });
        return true;
    }

    /// <summary>Pushes the frame a primitive's call makes on this stack (the vtable +0x34 call that FUN_0044f6c1 and
    /// FUN_0044ba66 make): tree <paramref name="treeId"/> from <paramref name="table"/>, with the default parameters and
    /// <paramref name="stackObject"/> as its raw stack-object key. Inferred: a call with no parameter block takes the default
    /// block, as a gosub does. Returns false when the tree cannot be pushed (see <see cref="Push"/>).</summary>
    public bool PushFor(ITreeTable table, short stackObject, short treeId)
    {
        if (!Push(table, [], true, treeId)) return false;
        Top!.StackObject = stackObject;
        return true;
    }

    /// <summary>Pushes a functional tree with the caller-supplied parameter block (FUN_004749b7). The person
    /// vtable path used by Gosub Found Action passes its zeroed four-word block instead of the runner defaults.</summary>
    public bool PushFor(ITreeTable table, short stackObject, short treeId, ReadOnlySpan<short> parameters)
    {
        if (!Push(table, parameters, false, treeId)) return false;
        Top!.StackObject = stackObject;
        return true;
    }

    /// <summary>Restores one decoded frame without running tree-entry initialization (the inverse of
    /// FUN_00475130/FUN_00474801). The saved locals, parameters, instruction and wait word are already initialized
    /// by the object serializer and must not be replaced with BHAV defaults.</summary>
    internal void RestoreFrame(StackFrame frame) => Frames.Add(frame);

    /// <summary>The parameters a gosub's operand passes: the four int16 values, or null when every byte is
    /// 0xFF (FUN_00474cae then passes the thread's default block). Inferred for trees taking more than four:
    /// FUN_00475318 reads on into the next instruction's bytes, which the port does from the tree as stored.</summary>
    public static short[]? GosubParameters(Bhav tree, int index)
    {
        var operand = tree.Instructions[index].Operand;
        if (operand.All(value => value == 0xFF)) return null;
        var bytes = new List<byte>(operand);
        for (var next = index + 1; next < tree.Instructions.Count; next++)
        {
            var instruction = tree.Instructions[next];
            var opcode = (ushort)(instruction.Opcode | (instruction.Breakpoint ? 0x8000 : 0));
            bytes.Add((byte)opcode);
            bytes.Add((byte)(opcode >> 8));
            bytes.Add(instruction.TrueTarget);
            bytes.Add(instruction.FalseTarget);
            bytes.AddRange(instruction.Operand);
        }
        var values = new short[bytes.Count / 2];
        for (var at = 0; at < values.Length; at++) values[at] = (short)(bytes[at * 2] | bytes[at * 2 + 1] << 8);
        return values;
    }

    /// <summary>One step of the top frame (FUN_00474cae).</summary>
    public StepResult Step()
    {
        var frame = Top ?? throw new TreeRunException(TreeRunError.ReturnFromLastFrame);
        if (frame.Table is null)
        {
            Pop();
            return StepResult.Continue;
        }
        if (frame.TreeBreakpoint) return Breakpoint(frame, null);
        var index = frame.Instruction;
        if (index is < 0 or > 0xFA)
        {
            if (index is not (BhavInstruction.ReturnTrue or BhavInstruction.ReturnFalse))
                throw new TreeRunException(TreeRunError.BadTarget);
            Return(index == BhavInstruction.ReturnTrue);
            return StepResult.Continue;
        }
        var tree = frame.Tree as Bhav;
        if (tree is null || index >= tree.Instructions.Count) throw new TreeRunException(TreeRunError.TreeOrInstructionMissing);
        var instruction = tree.Instructions[index];
        if (instruction.Breakpoint) return Breakpoint(frame, instruction);
        if (++Steps >= StepLimit) throw new TreeRunException(TreeRunError.StepLimit, $"tree {frame.TreeId}, instruction {index}, stack {frame.StackObject}");
        if (instruction.Opcode < 0x100)
        {
            switch (RunPrimitive(frame, instruction))
            {
                case PrimitiveResult.False: Branch(frame, instruction.FalseTarget, false); break;
                case PrimitiveResult.True: Branch(frame, instruction.TrueTarget, true); break;
                case PrimitiveResult.Stop: return StepResult.Stop;
                case PrimitiveResult.Again: return StepResult.Continue;
                default: return StepResult.Yield;
            }
            return StepResult.Continue;
        }
        var parameters = GosubParameters(tree, index);
        var id = (short)instruction.Opcode;
        Called(frame, id, parameters);
        if (!Push(null, parameters, parameters is null, id))
            throw new TreeRunException(frame.Table.Find(id) is null ? TreeRunError.TreeOrInstructionMissing : TreeRunError.TreeNotEntered);
        return StepResult.Continue;
    }

    /// <summary>Steps until a primitive stops (true) or yields (false), with the step counter reset first
    /// (FUN_00474ff5).</summary>
    public bool Run()
    {
        Steps = 0;
        while (true)
        {
            // A reset initializer is pushed as the object's new root stack. Its saved 0xFFFF marker
            // sits below the root frame and turns the final return into a completed manager callback.
            if (Top is { TreeId: -1, Tree: null })
            {
                RemoveTop();
                return true;
            }
            var result = Step();
            if (result == StepResult.Stop) return true;
            if (result == StepResult.Yield) return false;
        }
    }

    /// <summary>Runs tree <paramref name="treeId"/> on top of the current stack until it returns, and gives its
    /// result (FUN_00474b22). A marker frame (tree 0xFFFF, waiting) sits under the tree, so the return does not
    /// branch the caller. The step counter starts at 0 and is put back afterwards. Returns false when the tree
    /// cannot be pushed. Not modelled: the flag at +0x28 that makes it return false at once, and the global
    /// DAT_006c4d5c it clears for the run.</summary>
    public bool RunToReturn(ITreeTable? table, ReadOnlySpan<short> parameters, bool useDefault, short treeId, short stackObject)
    {
        var marker = new StackFrame(0, [], []) { TreeId = -1, Waiting = true };
        Frames.Add(marker);
        if (!Push(table, parameters, useDefault, treeId))
        {
            RemoveTop();
            return false;
        }
        Top!.StackObject = stackObject;
        var saved = Steps;
        Steps = 0;
        var runState = BeginRunToReturn();
        try
        {
            while (true)
            {
                if (Top!.TreeId == -1)
                {
                    RemoveTop();
                    return LastTarget == BhavInstruction.ReturnTrue;
                }
                var result = Step();
                if (result is StepResult.Stop or StepResult.Yield) throw new TreeRunException(TreeRunError.NestedRunSuspended);
            }
        }
        finally
        {
            EndRunToReturn(runState);
            Steps = saved;
        }
    }

    private void Pop()
    {
        if (Frames.Count < 2) throw new TreeRunException(TreeRunError.ReturnFromLastFrame);
        RemoveTop();
    }

    /// <summary>FUN_00475463 and the vtable +8 call, without the two-frame check (FUN_00474b22 pops its marker
    /// this way).</summary>
    private void RemoveTop()
    {
        var frame = Frames[^1];
        Frames.RemoveAt(Frames.Count - 1);
        Popped(frame);
    }

    private void Return(bool result)
    {
        var returning = Top!;
        Pop();
        var caller = Frames[^1];
        LastResult = result;
        // A waiting caller (a synchronous child call, or a primitive that started a pushed tree) is not branched; it reads the
        // result itself (inferred for primitives).
        if (caller.Waiting) return;
        if (caller.Tree is not Bhav tree || caller.Instruction < 0 || caller.Instruction >= tree.Instructions.Count)
            throw new TreeRunException(TreeRunError.TreeOrInstructionMissing);
        var instruction = tree.Instructions[caller.Instruction];
        var target = result ? instruction.TrueTarget : instruction.FalseTarget;
        if (target == BhavInstruction.Error)
            throw new TreeRunException(TreeRunError.BadTarget,
                $"returning from tree {returning.TreeId} at return target {returning.Instruction} with result {(result ? "true" : "false")} " +
                $"into caller tree {caller.TreeId} instruction {caller.Instruction} selected the error target");
        caller.Instruction = target;
        LastTarget = target;
    }

    private void Branch(StackFrame frame, byte target, bool result)
    {
        if (target == BhavInstruction.Error)
            throw new TreeRunException(TreeRunError.BadTarget,
                $"tree {frame.TreeId} instruction {frame.Instruction} selected the error target for result {(result ? "true" : "false")}");
        frame.Instruction = target;
        LastTarget = target;
        LastResult = result;
        frame.Waiting = false;
    }
}
