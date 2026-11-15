using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Native data services consumed by Sims.exe's Find Best Interaction pipeline.</summary>
internal sealed class AutonomyHost : ISimulationAutonomyHost
{
    private readonly SimulationWorld world;
    private readonly int lotKey;
    private readonly Func<bool> paused;
    private readonly Func<XPerson, XObject, short, string, float, float>? scoreAutonomy;

    public AutonomyHost(SimulationWorld world, IffFile global, int lotKey, Func<bool> paused,
        Func<XPerson, XObject, short, string, float, float>? scoreAutonomy = null)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(global);
        this.lotKey = lotKey;
        this.paused = paused ?? throw new ArgumentNullException(nameof(paused));
        this.scoreAutonomy = scoreAutonomy;
    }

    public float ScoreAutonomy(XPerson person, XObject target, short actionIndex, string actionName, float score) =>
        scoreAutonomy is null ? score : scoreAutonomy(person, target, actionIndex, actionName, score);

    public AutonomyLot AutonomyLotState()
    {
        // FUN_0045dea5 reads the lot key and lot family id separately. The loaded residential lot number is the
        // available native key; its home family id is projected from the first household person. The original
        // DAT_006f2a04 gate has no decoded native field yet; false is the observed base-residential default.
        var homeFamily = world[SimulationGlobal.CurrentFamily] != 0
            ? world[SimulationGlobal.CurrentFamily]
            : world.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(person => person[PersonData.PersonType] == 0)?[PersonData.FamilyNumber] ?? (short)0;
        var activePerson = world.ActivePersonAt(0)
            ?? (world.ActivePersonRecordId(0) is { } activeId ? world.ObjectById(activeId) as XPerson : null)
            ?? world.WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.SelectionWord5CC != 0);
        return new AutonomyLot(world.AutonomyForced ?? false, paused(), false, homeFamily != 0 ? homeFamily : lotKey, homeFamily,
            world.LotTransformParameter, activePerson);
    }

}
