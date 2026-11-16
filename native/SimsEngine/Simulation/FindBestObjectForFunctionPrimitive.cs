namespace SimsEngine.Simulation;

/// <summary>Searches the lot for the best object matching a function selector, scoring candidates by their
/// score field minus three times the distance to the caller. Candidates are filtered by lockout count,
/// visibility flags, room matching rules, and validation check trees, with the highest-scoring object
/// placed on the stack.</summary>
public static class FindBestObjectForFunctionPrimitive
{
    private const int FlagOutside = 1;
    private const int FlagOutsideSkip = 2;
    private const int FlagSameRoom = 4;
    private const int FlagOwner = 8;
    private const int SitSelector = 8;
    private const int ClassSelector = 15;
    private const int OutsideSelector = 11;
    private const int SitFunction = 0x1a;
    private const int ClassSearchValue = 4;
    private const int ClassScore = 50;
    private const int NoRoom = -5;
    private const int PersonFieldAt60E = 65;
    private const short NoData = -1;
    private const double ScoreFactor = 3.0;
    private const double MinimumScore = 1.0;

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        var selector = (short)(operand[0] | operand[1] << 8);
        var flags = operand[6];

        var criterion = Criterion(me, selector);
        if (criterion is not { } found) return PrimitiveResult.False;

        XObject? owner = null;
        if ((flags & FlagOwner) != 0)
        {
            var value = Variables.Get(context, (DataOwner)(short)(operand[4] | operand[5] << 8), (short)(operand[2] | operand[3] << 8));
            owner = world.ObjectByStackObject(me, value) ?? world.ObjectById(value) ?? throw new SimulationException(SimulationError.CouldNotFindObjectReference);
        }
        var matchRoom = (flags & FlagSameRoom) != 0 ? (owner ?? me)[ObjectData.Room] : (short)0;

        XObject? best = null;
        var bestScore = 0f;
        // Snapshots the candidate set before evaluation because running a candidate's check script can add
        // or remove objects on the lot.
        foreach (var candidate in world.WalkOrder.ToArray())
        {
            if (candidate[ObjectData.LockoutCount] >= 1 ||
                (candidate[ObjectData.Flags] & 0x200) != 0 ||
                !PassesRooms(world, candidate, flags, matchRoom))
                continue;
            if (selector == ClassSelector)
            {
                if (candidate[ObjectData.Category] != ClassSearchValue) continue;
                me[ObjectData.FunctionScore] = ClassScore;
            }
            else
            {
                var (tree, check) = SimulationWorld.FunctionEntry(candidate, found.Function);
                if (tree == 0) continue;
                if (found.Field == NoData)
                {
                    me[ObjectData.FunctionScore] = ClassScore;
                }
                else
                {
                    var basePoints = candidate.Data[found.Field];
                    me[ObjectData.FunctionScore] = basePoints;
                    if (basePoints == 0) continue;
                }
                if (check != 0 && (!RunCheck(context, candidate, check) || me[ObjectData.FunctionScore] == 0)) continue;
            }
            var points = me[ObjectData.FunctionScore];
            var score = Score(me, candidate, points, selector);
            if (bestScore < score)
            {
                bestScore = (float)score;
                best = candidate;
            }
        }

        if (best is null) return PrimitiveResult.False;
        frame.StackObject = best.StackObjectKey;
        return PrimitiveResult.True;
    }

    private static (short Field, short Function)? Criterion(XObject me, short selector) => selector switch
    {
        0 => (31, 0x12),
        1 => (32, 0x13),
        2 => (33, 0x14),
        3 => (36, 0x15),
        6 => (37, 0x18),
        7 => (38, 0x19),
        SitSelector => me is XPerson person && person.PersonData[PersonFieldAt60E] <= 1 ? (NoData, SitFunction) : null,
        10 => (61, 0x0e),
        11 => (39, 0x1c),
        12 => (64, 0x10),
        13 => (65, 0x11),
        14 => (15, 0x1d),
        ClassSelector => (NoData, 0),
        _ => null,
    };

    private static bool PassesRooms(SimulationWorld world, XObject candidate, byte flags, short matchRoom)
    {
        if ((flags & (FlagOutside | FlagOutsideSkip | FlagSameRoom)) == 0) return true;
        var room = candidate[ObjectData.Room];
        if ((flags & FlagOutside) != 0)
            return room == NoRoom || !(RoomExists(world, room) && !world.RoomIsOutside(room));
        if ((flags & FlagOutsideSkip) != 0)
            return room != NoRoom && !(RoomExists(world, room) && world.RoomIsOutside(room));
        if ((flags & FlagSameRoom) != 0) return room == matchRoom;
        return true;
    }

    private static bool RoomExists(SimulationWorld world, short room) => world.HasRoomRecord(room);

    private static bool RunCheck(in TreeContext context, XObject candidate, short check)
    {
        var world = context.World;
        var stack = context.Stack ?? throw new InvalidOperationException("Find Best Object for Function needs the tree stack it runs on.");
        var scope = candidate.TypeRecord?.BehaviorScope;
        if (scope is null) return false;
        var saved = world.SelfRemovalEndsRun;
        world.SelfRemovalEndsRun = false;
        try
        {
            return stack.RunToReturn(scope, [], true, check, candidate.StackObjectKey);
        }
        finally
        {
            world.SelfRemovalEndsRun = saved;
        }
    }

    /// <summary>Calculates candidate score as the base points minus three times distance, halving distance
    /// for selector 11 when greater than one, with a minimum score of one.</summary>
    private static double Score(XObject me, XObject candidate, short points, short selector)
    {
        var distance = GetDistanceToPrimitive.DistanceBetween(me, candidate);
        if (selector == OutsideSelector && distance > 1) distance *= 0.5;
        var score = points - ScoreFactor * distance;
        return score < MinimumScore ? MinimumScore : score;
    }
}
