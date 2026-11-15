namespace SimsEngine.Simulation;

/// <summary>The Find Best Object for Function primitive (opcode 0x0e), after Sims.exe 0x44f2ff. It searches the lot for the
/// best object for a function, scores each candidate, and writes the best one's id into the frame's stack object.
/// <para>Operand: word 0 is the selector, word 2 and word 4 are the data owner and the data (the owner is read with
/// <see cref="Variables.Get"/> as FUN_00453181 reads it), and byte 6 is the flags.</para>
/// <para>Selectors: 0 to 3 and 6, 7, 10 to 14 each map to a score field (the object's Data word, 31, 32, 33, 36, 37, 38,
/// 61, 39, 64, 65 and 15) and a function number (18 to 21, 24, 25, 14, 28, 16, 17, 29). Selector 8 (sit) needs a person
/// running it whose field at +0x60e is at most 1, and then uses function 26 with score 50. Selector 15 scores 50 for each
/// candidate whose object data <see cref="ObjectData.Category"/> is 4 and uses no function. Any other selector answers false.</para>
/// <para>Order: a selector outside the table answers false, as does a sit selector the running object cannot take. With
/// flag 8 the owner is the object with the id that the owner variable gives (<see cref="SimulationWorld.ObjectById"/>); a
/// missing one raises "Could not find object reference." (23). Flag 4 then takes the room to match: the owner's room with
/// flag 8, the running object's otherwise.</para>
/// <para>Each candidate in the world's walk order (<see cref="SimulationWorld.WalkOrder"/>) is kept only when its LockoutCount
/// data is below 1 and its object Flags data lacks bit 0x200 (the two direct tests at +0x7c/+0x5a in Sims.exe), and it
/// passes the room flags: flag 1 skips an object in a room that
/// exists and is not outside (an object with no room, -5, is kept); flag 2 skips an object with no room and one in an
/// outside room; flag 4, when neither is set, keeps only objects in the match room. A room exists when its field 0 is
/// nonzero. Flag 1 takes precedence over flag 2, and flag 4 is tested only when both are clear.</para>
/// <para>For the other selectors the candidate needs a function entry with a tree id, and its score field (the candidate's
/// Data word; 50 for selector 8) must be nonzero. A nonzero check tree is run on the candidate (its type's behavior scope,
/// <see cref="TreeStack.RunToReturn"/>, with the self-removal flag cleared as FUN_00474b22 does) and must answer true. The
/// score is the field minus 3 times the distance from the running object (<see cref="GetDistanceToPrimitive.DistanceBetween"/>),
/// with the distance halved for selector 11 when it is above 1, and at least 1. The candidate with the highest score wins;
/// a tie keeps the earlier one. The primitive answers true and sets the stack object, or false when no candidate scores.</para>
/// <para>Inferred: the candidates are walked in <see cref="SimulationWorld.WalkOrder"/> (Sims.exe walks the object list's
/// links); the distance is from the running object (the call's ECX is not traced); a candidate whose type has no behavior
/// scope is skipped (Sims.exe would dereference it); the running object's score field is kept in a local, not in its
/// memory. The check tree's result is read as the tree's return, as the other nested runs do.</para></summary>
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
        // RunCheck executes an object-owned BHAV. That script can add/remove lot objects,
        // so enumerate the candidate set captured when this search began.
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

    /// <summary>The score field and the function number of a selector, or null when the selector refuses. Function 15 has
    /// no function (0) and the field -1.</summary>
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

    /// <summary>The room flags: the candidate is kept (true) when it passes the room test the flags pick.</summary>
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

    // FUN_004670a4 returns null on a missing room-tree key; the native manager map preserves that key lookup.
    private static bool RoomExists(SimulationWorld world, short room) => world.HasRoomRecord(room);

    /// <summary>Runs the candidate's check tree on the stack (FUN_00474b22) and answers its result.</summary>
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

    /// <summary>The score: the points minus 3 times the distance, with the distance halved for selector 11 above 1, and
    /// at least 1.</summary>
    private static double Score(XObject me, XObject candidate, short points, short selector)
    {
        var distance = GetDistanceToPrimitive.DistanceBetween(me, candidate);
        if (selector == OutsideSelector && distance > 1) distance *= 0.5;
        var score = points - ScoreFactor * distance;
        return score < MinimumScore ? MinimumScore : score;
    }
}
