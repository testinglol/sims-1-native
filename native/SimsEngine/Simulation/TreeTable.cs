using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Represents a mutable interaction table (TTAB) entry, supporting dynamic tuning overrides.</summary>
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

public static class TreeTable
{
    public static InteractionTable ApplyOverrides(uint guid, InteractionTable table) =>
        Content.TuningOverrideImporter.ApplyGuidOverrides(guid, table);
}
