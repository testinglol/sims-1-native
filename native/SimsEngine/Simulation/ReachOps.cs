using SimsEngine.Formats;

namespace SimsEngine.Simulation;

internal interface IReachOps
{
    (XObject? Holder, int Value) GrabHolder(XObject target);
    float SlotPosition(XObject holder, int slot);
    int SlotCount(XObject item);
    bool NextAvatarEvent(XPerson me, out int code);
    bool ReachAnimationDone(XPerson me);
    void FinishReach(XPerson me);
    bool StartReachAnimation(XPerson me, float position);
    bool CanGrab(XObject item, ObjectPosition at, XObject who, int flag);
    void Grab(XObject item, ObjectPosition at, XObject who, int flag);
    bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag);
    void Place(XObject target, ObjectPosition at, XObject who, int flag);
}

internal sealed class HostReachOps(ISimulationHost host) : IReachOps
{
    public (XObject? Holder, int Value) GrabHolder(XObject target) => host.GrabHolder(target);
    public float SlotPosition(XObject holder, int slot) => host.SlotPosition(slot);
    public int SlotCount(XObject item) => item.Slots.Count;
    public bool NextAvatarEvent(XPerson me, out int code) => host.NextAvatarEvent(me, out code);
    public bool ReachAnimationDone(XPerson me) => host.ReachAnimationDone(me);
    public void FinishReach(XPerson me) => host.FinishReach(me);
    public bool StartReachAnimation(XPerson me, float position) => host.StartReachAnimation(me, position);
    public bool CanGrab(XObject item, ObjectPosition at, XObject who, int flag) => host.CanGrab(item, at, who, flag);
    public void Grab(XObject item, ObjectPosition at, XObject who, int flag) => host.Grab(item, at, who, flag);
    public bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag) => host.CanPlace(target, at, who, flag);
    public void Place(XObject target, ObjectPosition at, XObject who, int flag) => host.Place(target, at, who, flag);
}

/// <summary>Handles reach and put operations, selecting animation clips from Global.iff string tables 154
/// for adults or 155 for children based on target slot height.</summary>
internal sealed class ReachOps(SimulationWorld world, TreeStack? stack) : IReachOps
{
    // Projection scaling factor: 1 / (2 * sqrt(6)).
    private static readonly float SlotFactor = (float)(1.0 / (Math.Sqrt(6.0) + Math.Sqrt(6.0)));

    public (XObject? Holder, int Value) GrabHolder(XObject target)
    {
        var spec = world.PlacementSpec(target);
        return (spec.Holder, spec.Value);
    }

    public float SlotPosition(XObject holder, int slot) => world.SlotHeight(holder, slot) * SlotFactor;

    public int SlotCount(XObject item) => item.FootprintSlots.Count;

    public bool NextAvatarEvent(XPerson me, out int code)
    {
        code = -1;
        if (!me.TryDequeueAnimationEvent(out var value)) return false;
        code = value;
        return true;
    }

    public bool ReachAnimationDone(XPerson me) => me.AnimationPlayback is not { EndReached: false };

    public void FinishReach(XPerson me) => world.StopReachAnimation(me);

    public bool StartReachAnimation(XPerson me, float position)
    {
        var index = ReachIndex(position, me[PersonData.Sitting] == 1);
        if (index == -1) return false;
        // Ages 17 and below use child reach animations.
        var age = me[PersonData.PersonAge];
        var child = age <= 17;
        if (!world.ReachAvatarAnimationTable.TryGetValue((child, index), out var clip)) return false;
        me.PendingAnimationEvents.Clear();
        me.AnimationPlayback = new AvatarAnimationPlayback(clip, false, false, startedAtMils: me.AnimatorMilliseconds);
        me.AnimatorCurrentSkill = -1;
        me.AnimatorControlState = 0;
        return true;
    }

    // Selects reach animation band by target height, offset for seated Sims.
    internal static int ReachIndex(float position, bool sitting)
    {
        int index;
        if (position < 0f) index = 9;
        else if (position < 0.75f) index = 1;
        else if (position < 2f) index = 3;
        else if (position < 2.6666667f) index = 5;
        else if (position < 4f) index = 7;
        else return -1;
        return sitting ? index + 1 : index;
    }

    public bool CanGrab(XObject item, ObjectPosition at, XObject who, int flag) =>
        world.CanPlaceRelativeObject(item, at, who, flag);

    public void Grab(XObject item, ObjectPosition at, XObject who, int flag) =>
        world.PlaceObject(item, at, stack, who, checked((short)(flag + 1)));

    public bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag) =>
        world.CanPlaceRelativeObject(target, at, who, flag);

    public void Place(XObject target, ObjectPosition at, XObject who, int flag) =>
        world.PlaceObject(target, at, stack, who, checked((short)(flag + 1)));
}
