using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ReachPutPrimitiveTests
{
    private sealed class ReachTreeStack(SimulationWorld world, XObject me) : TreeStack
    {
        public short LastStackObject { get; private set; }

        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction)
        {
            if (instruction.Opcode != 2) throw new InvalidOperationException($"Unexpected test primitive {instruction.Opcode}.");
            LastStackObject = frame.StackObject;
            var context = new TreeContext(world, me, frame) { Stack = this };
            return ExpressionPrimitive.Execute(context, instruction.Operand) ? PrimitiveResult.True : PrimitiveResult.False;
        }
    }

    private static byte[] Operand(int mode, int flag = 0, int parameter = 0) =>
        [(byte)mode, (byte)(mode >> 8), (byte)flag, (byte)(flag >> 8), (byte)parameter, (byte)(parameter >> 8), 0xEE, 0xEE];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) World(short stackObject = 2, short[]? parameters = null)
    {
        var (context, me, host) = PrimitiveTestWorld.Create(parameters ?? [0], stackObject);
        me.Position = new ObjectPosition(10, 20, 0);
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0)) { Position = new ObjectPosition(30, 40, 5) });
        return (context, me, host);
    }

    private static BehaviorScope NamedScope(string name, ushort id)
    {
        var operand = PrimitiveTestWorld.Expression(0, 7, (byte)ExpressionOperator.AssignTo);
        var body = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(operand).ToArray();
        var bytes = ChunkBuilder.File(("BHAV", id, body));
        Encoding.Latin1.GetBytes(name).CopyTo(bytes, IffFile.HeaderSize + 12);
        var file = new IffFile(bytes, "reach-target.iff");
        return new BehaviorScope(file, file, null);
    }

    [Fact]
    public void ModeNeitherStartsTheReachAtScaleMinusOne()
    {
        var (context, person, host) = World();
        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(2)));
        Assert.Equal(1, context.Frame.WaitState);
        Assert.True(person.ReachAnimationActive);
        Assert.Equal(new[] { "StartReachAnimation(100,-1)" }, host.Calls);
    }

    [Fact]
    public void WithoutAStackObjectTheStartAnswersFalse()
    {
        var (context, _, host) = World(stackObject: 0);
        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Empty(host.Calls);
        Assert.Equal(0, context.Frame.WaitState);
    }

    [Fact]
    public void AGrabWithoutAHolderStartsAtScaleZeroAndAFailedStartAnswersByTheFlag()
    {
        var (context, _, host) = World();
        host.StartReachAnswer = false;
        Assert.Equal(PrimitiveResult.True, ReachPutPrimitive.Execute(context, Operand(0, 0)));
        Assert.Equal(new[] { "GrabHolder(2)", "StartReachAnimation(100,0)" }, host.Calls);
        Assert.Equal(0, context.Frame.WaitState);

        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(0, 1)));
    }

    [Fact]
    public void AGrabHolderScalesTheReachByTheSlotPositionTimesThree()
    {
        var (context, _, host) = World();
        var holder = new XObject(4, PrimitiveTestWorld.Definition(0));
        holder.Slots.AddRange([1, 2]);
        host.HolderAnswer = (holder, 1);
        host.SlotPositionAnswer = 2f;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(new[] { "GrabHolder(2)", "SlotPosition(1)", "StartReachAnimation(100,6)" }, host.Calls);
        Assert.Equal(1, context.Frame.WaitState);
    }

    [Fact]
    public void NativeReachStartClearsAnimatorControlStateAfterStartingTheReachPractice()
    {
        var (context, person, _) = World();
        person[PersonData.PersonAge] = 18;
        person.AnimatorControlState = 3;
        person.AnimatorCurrentSkill = 17;
        person.PendingAnimationEvents.AddRange([8, 9]);
        context.World.ReachAvatarAnimationTable[(false, 1)] = new AvatarAnimationClip("reach", 2, []);

        Assert.True(new ReachOps(context.World, null).StartReachAnimation(person, 0f));

        Assert.Equal(0, person.AnimatorControlState);
        Assert.Equal(-1, person.AnimatorCurrentSkill);
        Assert.Equal("reach", person.AnimationPlayback!.Clip.Name);
        Assert.Empty(person.PendingAnimationEvents);
    }

    [Fact]
    public void NativeReachStartLeavesAnimatorControlStateWhenNoReachPracticeCanStart()
    {
        var (context, person, _) = World();
        person[PersonData.PersonAge] = 18;
        person.AnimatorControlState = 3;
        person.AnimatorCurrentSkill = 17;
        person.PendingAnimationEvents.Add(8);

        Assert.False(new ReachOps(context.World, null).StartReachAnimation(person, 0f));

        Assert.Equal(3, person.AnimatorControlState);
        Assert.Equal(17, person.AnimatorCurrentSkill);
        Assert.Null(person.AnimationPlayback);
        Assert.Equal([8], person.PendingAnimationEvents);
    }

    [Fact]
    public void AGrabValueOutsideTheHoldersSlotsHasNoTarget()
    {
        var (context, _, host) = World();
        var holder = new XObject(4, PrimitiveTestWorld.Definition(0));
        holder.Slots.AddRange([1, 2]);
        host.HolderAnswer = (holder, 2);

        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(new[] { "GrabHolder(2)" }, host.Calls);
    }

    [Fact]
    public void AStartedReachWaitsForAnEventOrTheAnimationAndThenFinishesOnItsFlag()
    {
        var (context, person, host) = World();
        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.True(person.ReachAnimationActive);
        host.Calls.Clear();
        context.Frame.WaitState = 1;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(new[] { "NextAvatarEvent(100)", "ReachAnimationDone(100)" }, host.Calls);

        host.AvatarEvents.Enqueue(1);
        host.Calls.Clear();
        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(1, context.Frame.WaitState);

        host.AvatarEvents.Enqueue(0);
        host.Calls.Clear();
        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(2, context.Frame.WaitState);
        Assert.True(person.ReachAnimationActive);
        Assert.Equal(new[] { "NextAvatarEvent(100)", "NextAvatarEvent(100)", "ReachAnimationDone(100)", "GrabHolder(2)" }, host.Calls);

        host.ReachDoneAnswer = true;
        Assert.Equal(PrimitiveResult.True, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.False(person.ReachAnimationActive);
    }

    [Fact]
    public void AGrabThatCanBeDoneGrabsAndTreatsAnUnresolvedNamedTreeAsANoop()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(
            new[] { "NextAvatarEvent(100)", "ReachAnimationDone(100)", "GrabHolder(2)", "CanGrab(2,10,20,0,100,0)", "Grab(2,10,20,0,100,0)" },
            host.Calls);
    }

    [Fact]
    public void AGrabRunsTheResolvedCtGrabTreeOnTheTargetObject()
    {
        var (context, me, host) = World();
        context.World.ObjectById(2)!.TypeRecord = new TypeRecord(1) { BehaviorScope = NamedScope("CT - Grab", 4096) };
        context = context with { Stack = new ReachTreeStack(context.World, me) };
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal((short)7, me.Temporaries[0]);
        Assert.Equal(2, context.Frame.WaitState);
    }

    [Fact]
    public void APutRunsTheResolvedCtPutTreeOnTheSlotTarget()
    {
        var (context, me, host) = World(parameters: [1]);
        context.World.ObjectById(2)!.Slots.AddRange([9, 9]);
        me.Slots.AddRange([0, 3]);
        var target = new XObject(3, PrimitiveTestWorld.Definition(0))
        {
            TypeRecord = new TypeRecord(2) { BehaviorScope = NamedScope("CT - Put", 4097) },
        };
        context.World.Add(target);
        target.RequestKey = 44;
        var stack = new ReachTreeStack(context.World, me);
        context = context with { Stack = stack };
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(1, 1)));
        Assert.Equal((short)7, me.Temporaries[0]);
        Assert.Equal((short)44, stack.LastStackObject);
        Assert.Equal(2, context.Frame.WaitState);
    }

    [Fact]
    public void ARefusedGrabWaitsForTheAnimationThenAnswersFalse()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;
        host.PlacementAnswer = false;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(3, context.Frame.WaitState);

        host.Calls.Clear();
        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(0, 1)));
        Assert.Equal(new[] { "ReachAnimationDone(100)", "FinishReach(100)" }, host.Calls);
    }

    [Fact]
    public void APutPlacesTheStackObjectOnTheFirstSlotObjectAtTheSlotScale()
    {
        var (context, me, host) = World(parameters: [1]);
        context.World.ObjectById(2)!.Slots.AddRange([9, 9]);
        me.Slots.AddRange([0, 3]);
        context.World.Add(new XObject(3, PrimitiveTestWorld.Definition(0)));
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(1, 1)));
        Assert.Equal(
            new[] { "NextAvatarEvent(100)", "ReachAnimationDone(100)", "SlotPosition(1)", "CanPlace(3,30,40,5,2,1)", "Place(3,30,40,5,2,1)" },
            host.Calls);
    }

    [Fact]
    public void APutResolvesTheHeldFirstRealSlotByManagerKey()
    {
        var (context, me, host) = World(parameters: [1]);
        var held = new XObject(3, PrimitiveTestWorld.Definition(0));
        context.World.Add(held);
        held.ClockManagerIndex = 120;
        me.SetObjectTableEntry(120, held);
        me.Slots.AddRange([0, 120]);
        context.World.ObjectById(2)!.Slots.AddRange([9, 9]);
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(1, 1)));
        Assert.Contains("CanPlace(3,30,40,5,2,1)", host.Calls);
    }

    [Fact]
    public void APutWithoutASlotObjectIsRefused()
    {
        var (context, _, host) = World(parameters: [1]);
        context.World.ObjectById(2)!.Slots.AddRange([9, 9]);
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(1, 1)));
        Assert.Equal(3, context.Frame.WaitState);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("CanPlace"));
    }

    [Fact]
    public void APutParameterOutsideTheFrameIsStackNumberOutOfRange()
    {
        var (context, _, host) = World(parameters: []);
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => ReachPutPrimitive.Execute(context, Operand(1, 1))));
    }

    [Fact]
    public void AnUnknownModeWithAFlagStopsAndKeepsThePhase()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(2, 1)));
        Assert.Equal(1, context.Frame.WaitState);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("GrabHolder"));
    }

    [Fact]
    public void AModeOutsideZeroToTwoIsNoTargetAndAnswersByTheFlag()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 1;
        host.ReachDoneAnswer = true;

        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(5, 1)));
        Assert.Equal(PrimitiveResult.True, ReachPutPrimitive.Execute(context, Operand(5, 0)));
    }

    [Fact]
    public void TheActingAndRefusedPhasesWaitForTheAnimationAndFinish()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 2;
        Assert.Equal(PrimitiveResult.Stop, ReachPutPrimitive.Execute(context, Operand(0)));
        host.ReachDoneAnswer = true;
        Assert.Equal(PrimitiveResult.True, ReachPutPrimitive.Execute(context, Operand(0)));

        context.Frame.WaitState = 3;
        host.Calls.Clear();
        Assert.Equal(PrimitiveResult.False, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Equal(new[] { "ReachAnimationDone(100)", "FinishReach(100)" }, host.Calls);
    }

    [Fact]
    public void AnUnknownPhaseYields()
    {
        var (context, _, host) = World();
        context.Frame.WaitState = 9;
        Assert.Equal(PrimitiveResult.Yield, ReachPutPrimitive.Execute(context, Operand(0)));
        Assert.Empty(host.Calls);
    }
}
