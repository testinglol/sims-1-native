using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Shared action eligibility and guard execution from Sims.exe FUN_00458485.
/// The caller builds the candidate list; when a menu context is active, passing candidates become base choices unless their
/// check tree emitted dynamic choices. Joining and action startup remain separate.</summary>
public static class InteractionTestEvaluator
{
    public const int Evaluated = 4, Passed = 8, Hidden = 16;

    /// <summary>Evaluates a candidate batch and restores the person's eight temporary values once at the end,
    /// matching FUN_004583ff/00458449. Check-tree errors propagate instead of becoming unavailable interactions.</summary>
    public static void Evaluate(SimulationWorld world, XPerson person, IReadOnlyList<ActionQueueSlot> actions, bool autonomy)
    {
        var savedTemps = person.Temporaries.ToArray();
        try
        {
            foreach (var action in actions)
            {
                var menu = world.InteractionMenu;
                var previousCount = menu?.Choices.Count ?? 0;
                if (menu is not null) menu.Current = action;
                EvaluateOne(world, person, action, autonomy);
                if (menu is not null && menu.Choices.Count == previousCount && (action.Flags & (Passed | Hidden)) == Passed)
                    menu.AddBase(action);
            }
        }
        finally { savedTemps.CopyTo(person.Temporaries, 0); }
    }

    private static void EvaluateOne(SimulationWorld world, XPerson person, ActionQueueSlot action, bool autonomy)
    {
        action.Flags = (action.Flags & ~Passed) | Evaluated;
        var target = action.Owner;
        var interaction = target?.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10);
        if (target is null || interaction is null) return;
        person[ObjectData.HideInteraction] = 0;
        var eligible = (target[ObjectData.Flags] & 0x200) == 0 && (!autonomy || target[ObjectData.LockoutCount] < 1);
        if (eligible && (interaction.Flags & 1) == 0 && person.SelectionWord5CC != 0 &&
            (world.StateClass(person.StateValue) == 1 || (interaction.Flags & 0x20) == 0))
        {
            eligible = false;
            // FUN_00458485 restores eligibility for categories 4/7 only when its avatar animation key
            // has a positive (0, key) entry in the person's interaction scope.
            if (target[ObjectData.Category] is 4 or 7)
                eligible = world.HasVisitorInventoryEntry(person);
        }
        var age = person[PersonData.PersonAge];
        var gender = person[PersonData.Gender];
        if (age != 0 && age < 18) eligible &= (interaction.Flags & 0x10) == 0;
        else if ((gender & 2) != 0) eligible &= (interaction.Flags & 0x400) != 0;
        else if ((gender & 4) != 0) eligible &= (interaction.Flags & 0x200) != 0;
        else eligible &= (interaction.Flags & 0x40) == 0;
        if (autonomy && world.StateClass(person.StateValue) == 1 && person.SelectionWord5CC != 0 &&
            (gender & 6) != 0 && person[PersonData.GreetStatus] == 0 && target[ObjectData.Room] != 0)
            eligible = false;

        var passed = false;
        if (eligible)
        {
            var checkTreeAds = new short[48];
            for (var index = 0; index < Math.Min(interaction.Motives.Count, 16); index++)
            {
                var motive = interaction.Motives[index];
                checkTreeAds[index * 3] = motive.Personality;
                checkTreeAds[index * 3 + 1] = motive.Minimum;
                checkTreeAds[index * 3 + 2] = motive.Delta;
            }
            if (interaction.GuardTree == 0) passed = true;
            else
            {
                short[] parameters = [autonomy ? (short)1 : (short)0, action.RecordKind == 1 ? (short)1 : (short)0,
                    action.RecordKind == 1 && action.Parameters.Length > 2 ? action.Parameters[2] : (short)0, 0];
                var scope = target.TypeRecord!.BehaviorScope
                    ?? throw new InvalidOperationException($"Object {target.Id}: native interaction check has no behavior scope.");
                var previousCheckState = world.EvaluatingInteractionTest;
                var previousCheckTreeAds = world.CheckTreeAds;
                world.EvaluatingInteractionTest = true;
                world.CheckTreeAds = checkTreeAds;
                try
                {
                    passed = TreeRunner.RunWithParameters(world, person, scope,
                        unchecked((short)interaction.GuardTree), target.StackObjectKey, parameters);
                }
                catch (SimulationException error)
                {
                    var scopeSource = scope is SimsEngine.Content.BehaviorScope behaviorScope
                        ? behaviorScope.Private.Source
                        : scope.GetType().Name;
                    var detail =
                        $"interaction guard BHAV {interaction.GuardTree} for object {target.Id}, type {target.TypeRecord.Key}, " +
                        $"scope {scopeSource}, TTAB entry {interaction.TtaIndex}; " +
                        $"{world.CurrentExecutionLocation ?? "no current instruction"}: {error.Message}";
                    // In Sims.exe treesim.s RunCheckTree (FUN_00458485 / FUN_00474b22), action guards run on an isolated
                    // check thread. Any check failure or error simply causes the candidate interaction to be rejected (false)
                    // without crashing or resetting the actor's active simulation thread.
                    world.InteractionGuardErrors.Add(new InteractionGuardError(person.Id, target.Id,
                        interaction.TtaIndex, interaction.GuardTree, error.Error, detail));
                    passed = false;
                }
                catch (TreeRunException error)
                {
                    var scopeSource = scope is SimsEngine.Content.BehaviorScope behaviorScope
                        ? behaviorScope.Private.Source
                        : scope.GetType().Name;
                    throw new TreeRunException(error.Error,
                        $"interaction guard BHAV {interaction.GuardTree} for object {target.Id}, type {target.TypeRecord.Key}, " +
                        $"scope {scopeSource}, " +
                        $"TTAB entry {interaction.TtaIndex}: {error.Message}");
                }
                finally
                {
                    world.EvaluatingInteractionTest = previousCheckState;
                    world.CheckTreeAds = previousCheckTreeAds;
                }
            }

            // FUN_00458485: when target and actor are in different rooms, override motive 13's delta to -minimum
            // so FUN_004214d4 evaluates minimum + delta = 0 for the Room motive across rooms.
            if (target[ObjectData.Room] != person[ObjectData.Room])
                checkTreeAds[13 * 3 + 2] = unchecked((short)-checkTreeAds[13 * 3 + 1]);

            var evaluatedAds = new MotiveAdvertisement[16];
            for (var index = 0; index < evaluatedAds.Length; index++)
                evaluatedAds[index] = new MotiveAdvertisement(
                    checkTreeAds[index * 3], checkTreeAds[index * 3 + 1], checkTreeAds[index * 3 + 2]);
            action.EvaluatedAdvertisements = evaluatedAds;
        }
        action.Flags = (action.Flags & ~(Passed | Hidden)) | (passed ? Passed : 0) |
            (person[ObjectData.HideInteraction] != 0 ? Hidden : 0);
    }
}
