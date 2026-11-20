using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Evaluates candidate action eligibility and executes guards, converting passing candidates to
/// menu choices unless the check tree emitted dynamic choices.</summary>
public static class InteractionTestEvaluator
{
    public const int Evaluated = 4, Passed = 8, Hidden = 16;

    /// <summary>Evaluates a candidate interaction batch and restores the Sim's eight temporary values upon
    /// completion.</summary>
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
        var interaction = world.EffectiveInteractions(target)?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10);
        if (target is null || interaction is null) return;
        person[ObjectData.HideInteraction] = 0;
        var eligible = (target[ObjectData.Flags] & 0x200) == 0 && (!autonomy || target[ObjectData.LockoutCount] < 1);
        var connectedResidentialVisitor = !autonomy && world.Neighborhood.TrackingEnabled && person.SelectionWord5CC != 0 &&
            world.StateClass(person.StateValue != 0 ? person.StateValue : world.NeighborInteractionGlobalStateValue) == 1;
        if (eligible && (interaction.Flags & 1) == 0 && person.SelectionWord5CC != 0 &&
            (world.StateClass(person.StateValue) == 1 || (interaction.Flags & 0x20) == 0) &&
            !(connectedResidentialVisitor && person[PersonData.GreetStatus] > 0))
        {
            eligible = false;
            // Category 4 and 7 objects require a positive visitor inventory entry in the Sim's interaction
            // scope to be eligible.
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
        if (connectedResidentialVisitor && person[PersonData.GreetStatus] <= 0 &&
            target is not XPerson && target[ObjectData.Room] != 0)
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
                    // Action guards run on an isolated thread where failures or errors reject the
                    // interaction without disrupting the actor's thread.
                    world.InteractionGuardErrors.Add(new InteractionGuardError(person.Id, target.Id,
                        interaction.TtaIndex, interaction.GuardTree, error.Error, detail));
                    passed = false;
                }
                catch (TreeRunException error)
                {
                    var scopeSource = scope is SimsEngine.Content.BehaviorScope behaviorScope
                        ? behaviorScope.Private.Source
                        : scope.GetType().Name;
                    if (error.Error == TreeRunError.NestedRunSuspended)
                    {
                        throw new TreeRunException(error.Error,
                            $"interaction guard BHAV {interaction.GuardTree} for object {target.Id}, type {target.TypeRecord.Key}, " +
                            $"scope {scopeSource}, " +
                            $"TTAB entry {interaction.TtaIndex}: {error.Message}");
                    }
                    var detail =
                        $"interaction guard BHAV {interaction.GuardTree} for object {target.Id}, type {target.TypeRecord.Key}, " +
                        $"scope {scopeSource}, TTAB entry {interaction.TtaIndex}; " +
                        $"{world.CurrentExecutionLocation ?? "no current instruction"}: InteractionGuardError:{error.Error}:{error.Message}";
                    world.InteractionGuardErrors.Add(new InteractionGuardError(person.Id, target.Id,
                        interaction.TtaIndex, interaction.GuardTree, unchecked((SimulationError)(short)error.Error), detail));
                    passed = false;
                }
                finally
                {
                    world.EvaluatingInteractionTest = previousCheckState;
                    world.CheckTreeAds = previousCheckTreeAds;
                }
            }

            // When target and actor are in different rooms, sets motive 13 (Room) delta to negative minimum
            // so the score evaluates to zero.
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
