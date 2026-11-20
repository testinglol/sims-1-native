using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

internal sealed class LotSession
{
    public required short House { get; init; }
    public required SimulationWorld World { get; set; }
    public required IffFile Lot { get; set; }
    public ushort? ActiveFamilyChunkId { get; set; }
    public bool Away { get; set; }
    public short HomeHouse { get; set; }
    public IReadOnlyList<int>? RestrictedActivePersonKeys { get; set; }
    public long LastTick { get; set; }
    public (ushort FamilyChunkId, int VacationHouse)? CommittedVacationState { get; set; }

    public short SelectedSim { get; set; }
    public int ViewLevel { get; set; } = 1;
    public short ViewerMode { get; set; }
    public int Speed { get; set; }
    public int LastRunningSpeed { get; set; } = 1;
    public int AutoSpeedSaved { get; set; } = -1;
    public int DialogResumeSpeed { get; set; }
    public bool SaveDirty { get; set; }
    public bool EditingView { get; set; }
    public bool DoorWindowBuildToolActive { get; set; }
    public bool RoofsVisible { get; set; }
    public (int X, int Y)? CutawayTile { get; set; }
    public List<ushort> CutawayRooms { get; } = [];
    public long ExportedCutawaySimKey { get; set; } = -1;
    public (int FurnishingsObjects, int ArchitectureObjects) InitialObjectValuation { get; set; }
    public IffFile? InitialValuationLot { get; set; }
    public bool LotValuationEdited { get; set; }
    public Stack<SimulationServer.IBuildHistoryEntry> UndoStack { get; } = new();
    public Stack<SimulationServer.IBuildHistoryEntry> RedoStack { get; } = new();
    public SceneExporter.AnimationState AnimationState { get; } = new();
    public ulong? LastStreamedObjectsSignature { get; set; }
    public long LastFocusedOrder { get; set; }
}

internal sealed record DehydratedLotSession(
    short House,
    ushort? FamilyChunkId,
    byte[] LotBytes,
    SimulationServer.SaveRuntimeMetadata Runtime,
    int Speed,
    bool Away,
    short HomeHouse,
    IReadOnlyList<int>? RestrictedActivePersonKeys,
    long LastTick,
    short SelectedSim,
    bool SaveDirty = false,
    (ushort FamilyChunkId, int VacationHouse)? CommittedVacationState = null,
    int ViewLevel = 1,
    short ViewerMode = 0,
    bool RoofsVisible = false,
    bool EditingView = false,
    bool DoorWindowBuildToolActive = false,
    (int X, int Y)? CutawayTile = null,
    IReadOnlyList<ushort>? CutawayRooms = null,
    int LastRunningSpeed = 1,
    int ClockTicks = 0,
    int ClockDay = 0);

internal sealed class LotSessionPool
{
    private readonly Dictionary<short, LotSession> _live = new();
    private readonly Dictionary<short, DehydratedLotSession> _saved = new();
    private long _focusSequence;

    public short FocusedHouse { get; set; }
    public short HomeHouse { get; set; }
    public IReadOnlyDictionary<short, LotSession> LiveSessions => _live;
    public IReadOnlyDictionary<short, DehydratedLotSession> SavedLotStates => _saved;

    public void PutLive(LotSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.LastFocusedOrder = ++_focusSequence;
        _live[session.House] = session;
        _saved.Remove(session.House);
    }

    public bool TryGetLive(short house, out LotSession session) => _live.TryGetValue(house, out session!);
    public bool TryGetSaved(short house, out DehydratedLotSession saved) => _saved.TryGetValue(house, out saved!);

    public void PutSaved(DehydratedLotSession saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        _saved[saved.House] = saved;
        _live.Remove(saved.House);
    }

    public LotSession? SelectEvictionCandidate(
        short keepFocusedHouse,
        short keepHomeHouse,
        IReadOnlySet<short>? protectedHouses = null) =>
        _live.Values
            .Where(s => s.House != keepFocusedHouse &&
                        s.House != keepHomeHouse &&
                        (protectedHouses is null || !protectedHouses.Contains(s.House)))
            .OrderBy(s => s.LastFocusedOrder)
            .FirstOrDefault()
        ?? _live.Values
            .Where(s => s.House != keepFocusedHouse &&
                        (protectedHouses is null || !protectedHouses.Contains(s.House)))
            .OrderBy(s => s.LastFocusedOrder)
            .FirstOrDefault();

    public void RemoveLive(short house) => _live.Remove(house);

    public void Clear()
    {
        _live.Clear();
        _saved.Clear();
        FocusedHouse = 0;
        HomeHouse = 0;
        _focusSequence = 0;
    }
}
