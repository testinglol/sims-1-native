using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The source interaction-table test in Sims.exe FUN_004359a1, once the object's multitile root and its
/// TTAB resource have been resolved. FUN_004760ae stores interaction flags at +0x14 and autonomy threshold at +0x1c.</summary>
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
