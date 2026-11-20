namespace SimsEngine.Simulation.Neighborhood;

public sealed class ConnectedNeighborhoodOptions
{
    public bool Enabled { get; set; }
    public bool EnableAdjacentLotRendering { get; set; } = true;
    public bool EnableStoryProgression { get; set; } = true;
    public int MaxLiveSplitLots { get; set; } = 4;

    public ConnectedNeighborhoodOptions Clone() => new()
    {
        Enabled = Enabled,
        EnableAdjacentLotRendering = EnableAdjacentLotRendering,
        EnableStoryProgression = EnableStoryProgression,
        MaxLiveSplitLots = MaxLiveSplitLots,
    };

    public void Normalize()
    {
        if (MaxLiveSplitLots < 1)
            MaxLiveSplitLots = 1;
    }
}
