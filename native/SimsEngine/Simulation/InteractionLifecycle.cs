using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Coordinates interaction lifecycle operations gated by resource capability masks, tracking lane
/// ownership by actor, object, and action sequence.</summary>
public sealed class InteractionLifecycle
{
    public const ushort AcquireLaneOpcode = 0x00F0;
    public const ushort ReleaseLaneOpcode = 0x00F1;
    public const ushort SetAttachmentOpcode = 0x00F2;
    public const ushort InterruptRequestedOpcode = 0x00F3;
    private const byte SupportedVersion = 1;
    private const byte RinkCapability = 1;
    private const byte AcquireBit = 1;
    private const byte ReleaseBit = 2;
    private const byte AttachmentBit = 4;
    private const byte InterruptBit = 8;
    public const string SkateAttachment = "ice-rink.skates";

    private sealed class RinkState(XObject rink, int laneCount)
    {
        public XObject Rink { get; } = rink;
        public int LaneCount { get; } = laneCount;
        public Dictionary<int, Lease> Leases { get; } = [];
        public Queue<Waiter> Waiters { get; } = new();
        public HashSet<int> UsedLanes { get; } = [];
    }

    private sealed record Waiter(XPerson Actor, int ActionSequence);
    private sealed record Lease(XPerson Actor, int ActionSequence, int Lane);
    private readonly Dictionary<short, RinkState> _rinks = [];
    private readonly HashSet<(short Actor, int Sequence)> _exitRequests = [];

    public static PrimitiveResult Execute(in TreeContext context, BhavInstruction instruction)
    {
        if (context.Frame.Table is not BehaviorScope scope || !HasRinkCapability(scope.Private))
            throw new SimulationException(SimulationError.IllegalPrimitiveCode,
                $"Native interaction opcode 0x{instruction.Opcode:X4} requires NLCF rink.v1.");
        var mask = ReadCapabilityMask(scope.Private);
        var bit = instruction.Opcode switch
        {
            AcquireLaneOpcode => AcquireBit,
            ReleaseLaneOpcode => ReleaseBit,
            SetAttachmentOpcode => AttachmentBit,
            InterruptRequestedOpcode => InterruptBit,
            _ => throw new SimulationException(SimulationError.IllegalPrimitiveCode,
                $"Unsupported native interaction opcode 0x{instruction.Opcode:X4}.")
        };
        if ((mask & bit) == 0)
            throw new SimulationException(SimulationError.IllegalPrimitiveCode,
                $"NLCF rink.v1 does not enable operation 0x{instruction.Opcode:X4}.");
        var operand = instruction.Operand.AsSpan();
        return instruction.Opcode switch
        {
            AcquireLaneOpcode => Acquire(context, operand),
            ReleaseLaneOpcode => Release(context, operand),
            SetAttachmentOpcode => SetAttachment(context, operand),
            InterruptRequestedOpcode => InterruptRequested(context, operand),
            _ => throw new SimulationException(SimulationError.IllegalPrimitiveCode)
        };
    }

    private static bool HasRinkCapability(IffFile file) => file.Find(ResourceType.NativeLifecycleCapability, 0) is not null;

    private static byte ReadCapabilityMask(IffFile file)
    {
        var chunk = file.Find(ResourceType.NativeLifecycleCapability, 0)
            ?? throw new SimulationException(SimulationError.IllegalPrimitiveCode, "Missing NLCF capability resource.");
        var data = chunk.Data.Span;
        if (data.Length != 8 || !data[..4].SequenceEqual("S1LC"u8) || data[4] != SupportedVersion || data[6] != 0 || data[7] != 0)
            throw new SimulationException(SimulationError.IllegalPrimitiveCode, "Unsupported or malformed NLCF capability version.");
        if ((data[5] & ~15) != 0)
            throw new SimulationException(SimulationError.IllegalPrimitiveCode, "NLCF contains unknown operation bits.");
        return data[5];
    }

    private static PrimitiveResult Acquire(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        ValidateZeroPadding(operand, 2, "acquire_interaction_lane");
        var laneCount = operand[0];
        var local = operand[1];
        if (laneCount is < 1 or > 8 || local >= context.Frame.Locals.Length)
            throw BadOperand("acquire_interaction_lane", "lane count must be 1..8 and local must exist");
        if (context.Me is not XPerson actor || context.StackObject is not { } rink)
            return PrimitiveResult.False;
        var action = actor.CurrentActionRecord;
        if (actor.ActiveActionCancelRequested)
        {
            context.World.InteractionLifecycle.FinalizeAction(actor, action?.Sequence ?? 0);
            return PrimitiveResult.False;
        }
        if (action?.Owner is null || !ReferenceEquals(action.Owner, rink))
            return PrimitiveResult.False;
        var result = context.World.InteractionLifecycle.Acquire(rink, actor, action.Sequence, laneCount, out var lane);
        if (result == PrimitiveResult.True) context.Frame.Locals[local] = checked((short)lane);
        return result;
    }

    private static PrimitiveResult Release(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        ValidateZeroPadding(operand, 0, "release_interaction_lane");
        if (context.Me is not XPerson actor || context.StackObject is not { } rink) return PrimitiveResult.False;
        var action = actor.CurrentActionRecord;
        if (action?.Owner is null) return PrimitiveResult.False;
        context.World.InteractionLifecycle.Release(rink, actor, action.Sequence);
        return PrimitiveResult.True;
    }

    private static PrimitiveResult SetAttachment(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        ValidateZeroPadding(operand, 2, "set_interaction_attachment");
        if (operand[0] != 1 || operand[1] > 1)
            throw BadOperand("set_interaction_attachment", "attachment kind 1 and boolean state 0/1 are required");
        if (context.Me is not XPerson actor || context.StackObject is not { } rink) return PrimitiveResult.False;
        var action = actor.CurrentActionRecord;
        if (action?.Owner is null || !context.World.InteractionLifecycle.OwnsAction(rink, actor, action.Sequence))
            return PrimitiveResult.False;
        SetAttachment(actor, operand[1] != 0);
        return PrimitiveResult.True;
    }

    private static PrimitiveResult InterruptRequested(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        ValidateZeroPadding(operand, 0, "interaction_interrupt_requested");
        if (context.Me is not XPerson actor || context.StackObject is not { } rink) return PrimitiveResult.True;
        var action = actor.CurrentActionRecord;
        if (action?.Owner is null || !ReferenceEquals(action.Owner, rink)) return PrimitiveResult.True;
        return context.World.InteractionLifecycle.InterruptRequested(actor, action.Sequence)
            ? PrimitiveResult.True : PrimitiveResult.False;
    }

    private static void ValidateZeroPadding(ReadOnlySpan<byte> operand, int used, string operation)
    {
        if (operand.Length != 8 || operand[used..].IndexOfAnyExcept((byte)0) >= 0)
            throw BadOperand(operation, "operand must be eight bytes with zeroed reserved bytes");
    }

    private static SimulationException BadOperand(string operation, string detail) =>
        new(SimulationError.IllegalPrimitiveCode, $"Invalid {operation} operand: {detail}.");

    private PrimitiveResult Acquire(XObject rink, XPerson actor, int actionSequence, int laneCount, out int lane)
    {
        lane = -1;
        if (!_rinks.TryGetValue(rink.Id, out var state) || !ReferenceEquals(state.Rink, rink))
            _rinks[rink.Id] = state = new RinkState(rink, laneCount);
        if (state.LaneCount != laneCount)
            throw new InvalidOperationException($"Rink {rink.Id} lane count changed while its lifecycle state is live.");

        if (state.Leases.TryGetValue(actor.Id, out var current) && ReferenceEquals(current.Actor, actor) && current.ActionSequence == actionSequence)
        {
            lane = current.Lane;
            return PrimitiveResult.True;
        }

        var waiter = state.Waiters.FirstOrDefault(item => ReferenceEquals(item.Actor, actor) && item.ActionSequence == actionSequence);
        if (waiter is null)
        {
            state.Waiters.Enqueue(new Waiter(actor, actionSequence));
            waiter = state.Waiters.Last();
        }
        if (!ReferenceEquals(state.Waiters.Peek(), waiter) || state.UsedLanes.Count == state.LaneCount)
            return PrimitiveResult.Stop; // Keep this BHAV instruction and retry on a later scheduler pass.

        state.Waiters.Dequeue();
        lane = Enumerable.Range(0, state.LaneCount).First(index => !state.UsedLanes.Contains(index));
        state.UsedLanes.Add(lane);
        state.Leases[actor.Id] = new Lease(actor, actionSequence, lane);
        return PrimitiveResult.True;
    }

    private void Release(XObject rink, XPerson actor, int actionSequence)
    {
        if (!_rinks.TryGetValue(rink.Id, out var state) || !ReferenceEquals(state.Rink, rink)) return;
        if (state.Leases.TryGetValue(actor.Id, out var lease) && ReferenceEquals(lease.Actor, actor) && lease.ActionSequence == actionSequence)
        {
            state.Leases.Remove(actor.Id);
            state.UsedLanes.Remove(lease.Lane);
            actor.InteractionAttachments.Remove(SkateAttachment);
        }
        RemoveWaiter(state, actor, actionSequence);
        Prune(state);
    }

    public bool OwnsAction(XObject rink, XPerson actor, int actionSequence) =>
        _rinks.TryGetValue(rink.Id, out var state) && ReferenceEquals(state.Rink, rink) &&
        state.Leases.TryGetValue(actor.Id, out var lease) && ReferenceEquals(lease.Actor, actor) && lease.ActionSequence == actionSequence;

    public int LeaseCount(XObject rink) =>
        _rinks.TryGetValue(rink.Id, out var state) && ReferenceEquals(state.Rink, rink) ? state.Leases.Count : 0;

    public int WaiterCount(XObject rink) =>
        _rinks.TryGetValue(rink.Id, out var state) && ReferenceEquals(state.Rink, rink) ? state.Waiters.Count : 0;

    private static void SetAttachment(XPerson actor, bool visible)
    {
        if (visible) actor.InteractionAttachments.Add(SkateAttachment);
        else actor.InteractionAttachments.Remove(SkateAttachment);
    }

    public bool InterruptRequested(XPerson actor, int actionSequence) =>
        actor.ActiveActionCancelRequested || _exitRequests.Contains((actor.Id, actionSequence));

    /// <summary>Requests an authored exit-check interrupt for the action currently owning a lane, leaving
    /// other actions on the immediate cancellation path.</summary>
    public bool TryRequestGracefulExit(XPerson actor, int actionSequence)
    {
        if (!_rinks.Values.Any(state => state.Leases.TryGetValue(actor.Id, out var lease) &&
                                        ReferenceEquals(lease.Actor, actor) && lease.ActionSequence == actionSequence))
            return false;
        _exitRequests.Add((actor.Id, actionSequence));
        return true;
    }

    public void FinalizeAction(XPerson actor, int actionSequence)
    {
        foreach (var state in _rinks.Values.ToArray())
        {
            Release(state.Rink, actor, actionSequence);
        }
        _exitRequests.Remove((actor.Id, actionSequence));
    }

    public void CancelWaiter(XObject rink, XPerson actor, int actionSequence)
    {
        if (!_rinks.TryGetValue(rink.Id, out var state) || !ReferenceEquals(state.Rink, rink)) return;
        RemoveWaiter(state, actor, actionSequence);
        Prune(state);
    }

    public void FinalizeActor(XPerson actor)
    {
        foreach (var state in _rinks.Values.ToArray())
        {
            foreach (var lease in state.Leases.Values.Where(item => ReferenceEquals(item.Actor, actor)).ToArray())
                Release(state.Rink, actor, lease.ActionSequence);
            foreach (var waiter in state.Waiters.Where(item => ReferenceEquals(item.Actor, actor)).ToArray())
                RemoveWaiter(state, actor, waiter.ActionSequence);
            Prune(state);
        }
        actor.InteractionAttachments.Remove(SkateAttachment);
        _exitRequests.RemoveWhere(item => item.Actor == actor.Id);
    }

    public void FinalizeRink(XObject rink)
    {
        if (!_rinks.TryGetValue(rink.Id, out var state) || !ReferenceEquals(state.Rink, rink)) return;
        foreach (var lease in state.Leases.Values.ToArray())
            lease.Actor.InteractionAttachments.Remove(SkateAttachment);
        _rinks.Remove(rink.Id);
    }

    private static void RemoveWaiter(RinkState state, XPerson actor, int actionSequence)
    {
        if (!state.Waiters.Any(item => ReferenceEquals(item.Actor, actor) && item.ActionSequence == actionSequence)) return;
        var remaining = state.Waiters.Where(item => !ReferenceEquals(item.Actor, actor) || item.ActionSequence != actionSequence).ToArray();
        state.Waiters.Clear();
        foreach (var waiter in remaining) state.Waiters.Enqueue(waiter);
    }

    private void Prune(RinkState state)
    {
        if (state.Leases.Count == 0 && state.Waiters.Count == 0 && _rinks.TryGetValue(state.Rink.Id, out var current) && ReferenceEquals(current, state))
            _rinks.Remove(state.Rink.Id);
    }
}
