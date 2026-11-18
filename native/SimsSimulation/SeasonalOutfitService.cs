using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>Resolves seasonal winter clothing through the character's OBJD Body Strings resource (and Coat Rack
/// per-Sim overrides) as transient presentation state without modifying the save baseline.</summary>
internal sealed class SeasonalOutfitService(string gameRoot, ObjectRegistry registry)
{
    private sealed class Transition(bool wearingWinter, PersonCharacter? previousPresentation,
        PersonCharacter? targetPresentation, AvatarAnimationPlayback? previousPlayback, AvatarAnimationPlayback changePlayback)
    {
        internal bool WearingWinter { get; } = wearingWinter;
        internal PersonCharacter? PreviousPresentation { get; } = previousPresentation;
        internal PersonCharacter? TargetPresentation { get; } = targetPresentation;
        internal AvatarAnimationPlayback? PreviousPlayback { get; } = previousPlayback;
        internal AvatarAnimationPlayback ChangePlayback { get; } = changePlayback;
        internal bool OutfitApplied { get; set; }
    }

    private readonly Dictionary<short, bool> outsideStates = [];
    private readonly Dictionary<short, int?> suitOverrides = [];
    private readonly Dictionary<short, Transition> transitions = [];
    private readonly AvatarSceneGeometry geometry = AvatarSceneGeometry.For(gameRoot, registry);
    private SimulationWorld? worldForTick;

    internal bool IsWearing(XPerson person) => person.PresentationCharacter is not null;

    internal void SetSuitOverride(short simId, int? suitCategory)
    {
        if (suitCategory is null) suitOverrides.Remove(simId);
        else suitOverrides[simId] = suitCategory;
    }

    internal void Tick(SimulationWorld world, int? requestedSuit)
    {
        worldForTick = world;
        var people = world.WalkOrder.OfType<XPerson>().ToDictionary(person => person.Id);
        foreach (var id in outsideStates.Keys.Where(id => !people.ContainsKey(id)).ToArray())
        {
            if (transitions.Remove(id, out var removed))
                removed.ChangePlayback.EventQueue.Clear();
            outsideStates.Remove(id);
            suitOverrides.Remove(id);
        }

        foreach (var person in people.Values)
        {
            var eligible = person[PersonData.PersonType] < 2 && person[PersonData.IsGhost] == 0 &&
                person[PersonData.CurrentOutfit] == 0 && person.SavedCharacter is { Parts.Count: 5 };
            var outside = eligible && world.RoomIsOutside(person.RoomWord84);
            if (!outsideStates.TryGetValue(person.Id, out var wasOutside)) wasOutside = false;
            outsideStates[person.Id] = outside;

            var hasOverride = suitOverrides.TryGetValue(person.Id, out var manualSuit);
            var wantsWinter = eligible && (hasOverride ? manualSuit == 17 : outside && requestedSuit == 17);

            if (transitions.TryGetValue(person.Id, out var active))
            {
                if (!eligible || wantsWinter != active.WearingWinter)
                {
                    Cancel(person, active);
                    transitions.Remove(person.Id);
                }
                else
                {
                    if (!active.OutfitApplied &&
                        (active.ChangePlayback.EventQueue.Contains((short)101) || person.PendingAnimationEvents.Remove((short)101)))
                    {
                        ApplyPresentation(person, active);
                        active.OutfitApplied = true;
                    }
                    if (active.ChangePlayback.EndReached)
                    {
                        if (!active.OutfitApplied) ApplyPresentation(person, active);
                        RestorePreviousPlayback(person, active);
                        person.NativeOutfitTransitionActive = false;
                        transitions.Remove(person.Id);
                    }
                    continue;
                }
            }

            if (!eligible || (!hasOverride && requestedSuit != 17))
            {
                person.PresentationCharacter = null;
                continue;
            }

            var wearingWinter = person.PresentationCharacter is not null;
            if (wantsWinter == wearingWinter) continue;
            if (!hasOverride && outside == wasOutside && !outside) continue;
            if (person.NativeRouteParents.Count > 0 ||
                person.NativeRoute is { PendingPortalId: not 0 } or { AnimatorFollowState: -1 })
            {
                outsideStates[person.Id] = wasOutside;
                continue;
            }
            Start(person, wantsWinter);
        }
    }

    private void Start(XPerson person, bool wearingWinter)
    {
        var baseline = person.SavedCharacter!;
        var animationName = PersonAgeCategory.IsChild(person[PersonData.PersonAge]) ? "c2o-clotheschange" : "a2o-clotheschange";
        var clip = worldForTick?.AvatarAnimationTable.Values.FirstOrDefault(item =>
            item.Name.Equals(animationName, StringComparison.OrdinalIgnoreCase));
        if (clip is null || !clip.Events.Any(item => item.Value == 101)) return;

        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: false,
            startedAtMils: person.AnimatorMilliseconds)
        {
            Interruptible = false,
        };
        PersonCharacter? target = null;
        if (wearingWinter)
        {
            var suit = geometry.ResolveWinterSuit(person);
            if (suit is null) return;
            var parts = baseline.Parts.ToArray();
            var comma = suit.IndexOf(',');
            parts[0] = comma >= 0 ? (suit[..comma], suit[(comma + 1)..]) : (suit, "");
            target = baseline with { Parts = parts };
        }
        var transition = new Transition(wearingWinter, person.PresentationCharacter, target, person.AnimationPlayback, playback);
        transitions[person.Id] = transition;
        person.AnimationPlayback = playback;
        person.NativeOutfitTransitionActive = true;
    }

    internal void Reset(SimulationWorld? world)
    {
        if (world is not null)
        {
            foreach (var person in world.WalkOrder.OfType<XPerson>())
                if (transitions.Remove(person.Id, out var active)) Cancel(person, active);
        }
        outsideStates.Clear();
        suitOverrides.Clear();
        transitions.Clear();
        worldForTick = null;
    }

    private static void ApplyPresentation(XPerson person, Transition transition) => person.PresentationCharacter = transition.TargetPresentation;

    private static void RestorePreviousPlayback(XPerson person, Transition transition)
    {
        transition.PreviousPlayback?.Tick(deliverAnimationEvents: false, person.AnimatorMilliseconds);
        person.AnimationPlayback = transition.PreviousPlayback;
    }

    private static void Cancel(XPerson person, Transition transition)
    {
        person.PresentationCharacter = transition.PreviousPresentation;
        if (ReferenceEquals(person.AnimationPlayback, transition.ChangePlayback))
            RestorePreviousPlayback(person, transition);
        person.NativeOutfitTransitionActive = false;
        transition.ChangePlayback.EventQueue.Clear();
    }
}
