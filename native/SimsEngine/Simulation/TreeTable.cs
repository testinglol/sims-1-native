using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>
/// A mutable representation of an object's interaction table (TTAB) entry,
/// allowing dynamic tuning overrides to be bound per Sims.exe FUN_0043cc48
/// and Mac ObjectFolder::ImportConstants (ObjectFolder.s:2568-3190).
/// </summary>
public sealed class TreeTableEntry
{
    public ushort ActionTree { get; init; }
    public ushort GuardTree { get; init; }
    public uint Flags { get; set; }
    public int TtaIndex { get; init; }
    public uint AttenuationCode { get; set; }
    public float AttenuationValue { get; set; }
    public uint AutonomyThreshold { get; init; }
    public int JoiningIndex { get; init; }
    public List<SimsEngine.Formats.MotiveAdvertisement> Motives { get; set; } = [];
    public string? Name { get; set; }

    public static TreeTableEntry FromInteraction(Interaction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return new TreeTableEntry
        {
            ActionTree = interaction.ActionTree,
            GuardTree = interaction.GuardTree,
            Flags = interaction.Flags,
            TtaIndex = interaction.TtaIndex,
            AttenuationCode = interaction.AttenuationCode,
            AttenuationValue = interaction.AttenuationValue,
            AutonomyThreshold = interaction.AutonomyThreshold,
            JoiningIndex = interaction.JoiningIndex,
            Motives = interaction.Motives.ToList(),
            Name = interaction.Name
        };
    }

    public Interaction ToInteraction() => new(
        ActionTree,
        GuardTree,
        Flags,
        TtaIndex,
        AttenuationCode,
        AttenuationValue,
        AutonomyThreshold,
        JoiningIndex,
        Motives.ToArray(),
        Name);
}

/// <summary>
/// Utility methods for TTAB interaction tables and tuning override binding.
/// </summary>
public static class TreeTable
{
    /// <summary>
    /// Applies tuning overrides to the given interaction table.
    /// </summary>
    public static InteractionTable ApplyOverrides(uint guid, InteractionTable table) =>
        Content.TuningOverrideImporter.ApplyGuidOverrides(guid, table);
}
