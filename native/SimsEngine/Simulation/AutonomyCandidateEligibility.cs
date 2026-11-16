using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Evaluates interaction table entries for autonomy eligibility once an object's multitile root
/// and interaction table are resolved.</summary>
public static class AutonomyCandidateEligibility
{
    public static bool IsEligible(XObject item, XObject root, InteractionTable? interactions)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(root);
        if (!ReferenceEquals(item, root) || interactions is null) return false;

        return interactions.Interactions.Any(interaction =>
            interaction.AutonomyThreshold < 100 && (interaction.Flags & 0x80) == 0);
    }
}
