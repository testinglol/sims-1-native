namespace SimsEngine.Simulation;

/// <summary>A decoded Special Effect dispatch observed by the headless engine. Sims.exe reaches FUN_004a6b68
/// after its gates and effect setup; when the renderer manager is absent that routine returns without starting
/// playback. Keeping the request here makes that source-defined boundary observable without claiming to render it.</summary>
public readonly record struct HeadlessSpecialEffectDispatch(int CallerId, int StackObjectId, SpecialEffectRequest Request);

/// <summary>Headless counterpart to the renderer-facing portion of Special Effect opcode 0x23.</summary>
public sealed class HeadlessSpecialEffectService
{
    private readonly List<HeadlessSpecialEffectDispatch> _dispatches = [];

    /// <summary>Requests that reached the source's playback boundary. They are not rendered in a headless world.</summary>
    public IReadOnlyList<HeadlessSpecialEffectDispatch> Dispatches => _dispatches;

    /// <summary>
    /// Records a request that reached the renderer boundary. FUN_004a6b68 is void and returns early when no renderer
    /// manager exists, but FUN_0044aae6 still returns Stop from that invocation; true means the primitive reached that
    /// source-defined wait boundary, not that a visible effect was rendered.
    /// </summary>
    public bool TryStart(XObject caller, XObject stackObject, SpecialEffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(stackObject);
        _dispatches.Add(new HeadlessSpecialEffectDispatch(caller.Id, stackObject.Id, request));
        return true;
    }
}
