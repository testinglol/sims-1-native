namespace SimsEngine.Simulation;

/// <summary>Represents a special effect dispatch observed in headless mode without active visual rendering.</summary>
public readonly record struct HeadlessSpecialEffectDispatch(int CallerId, int StackObjectId, SpecialEffectRequest Request);

/// <summary>Captures special effect invocations in headless mode when no visual renderer is present.</summary>
public sealed class HeadlessSpecialEffectService
{
    private readonly List<HeadlessSpecialEffectDispatch> _dispatches = [];

    public IReadOnlyList<HeadlessSpecialEffectDispatch> Dispatches => _dispatches;

    public bool TryStart(XObject caller, XObject stackObject, SpecialEffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(stackObject);
        _dispatches.Add(new HeadlessSpecialEffectDispatch(caller.Id, stackObject.Id, request));
        return true;
    }
}
