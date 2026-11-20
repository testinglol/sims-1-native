namespace SimsEngine.Formats;

/// <summary>Event mapping actions parsed from HOT [EventMapping] and [EventMappingEquate] sections.</summary>
public enum HitEventAction
{
    None = 0,
    SndobPlay = 1,
    SndobStop = 2,
    SndobKill = 3,
    SndobUpdate = 4,
    SndobSetVolume = 5,
    SndobSetPitch = 6,
    SndobSetPan = 7,
    SndobSetPosition = 8,
    SndobPause = 11,
    SndobUnpause = 12,
    SndobCancelNote = 19,
    KillAll = 20,
    Pause = 21,
    Unpause = 22,
    KillInstance = 23,
    TurnOnTv = 30,
    TurnOffTv = 31,
    UpdateSourceVolPan = 32,
    SetMusicMode = 36,
    PlayPiano = 43,
    SequenceTrackHitList = 100
}

/// <summary>HIT register indices matching SimsSound.hsm / HotDate.hsm and 0057ca49.c.</summary>
public static class HitReg
{
    public const int Arg1 = 0x01;
    public const int Arg2 = 0x02;
    public const int Arg3 = 0x03;
    public const int Arg4 = 0x04;
    public const int V1 = 0x05;
    public const int V2 = 0x06;
    public const int V3 = 0x07;
    public const int V4 = 0x08;
    public const int V5 = 0x09;
    public const int V6 = 0x0a;
    public const int V7 = 0x0b;
    public const int V8 = 0x0c;
    public const int H1 = 0x0d;
    public const int H2 = 0x0e;
    public const int H3 = 0x0f;
    public const int H4 = 0x10;

    public const int Priority = 0x11;
    public const int Vol = 0x12;
    public const int ExtVol = 0x13;
    public const int Pan = 0x14;
    public const int Pitch = 0x15;
    public const int Paused = 0x16;
    public const int FxType = 0x17;
    public const int FxLevel = 0x18;
    public const int DuckPri = 0x19;
    public const int Is3d = 0x1a;
    public const int IsHeadRelative = 0x1b;
    public const int MinDistance = 0x1c;
    public const int MaxDistance = 0x1d;
    public const int X = 0x1e;
    public const int Y = 0x1f;
    public const int Z = 0x20;
    public const int IsStreamed = 0x26;
    public const int FadeDest = 0x28;
    public const int FadeVar = 0x29;
    public const int FadeSpeed = 0x2a;
    public const int Preload = 0x2b;
    public const int IsLooped = 0x2c;
    public const int FadeOn = 0x2d;
    public const int IsPlaying = 0x2e;
    public const int Source = 0x2f;
    public const int Patch = 0x32;
    public const int Target = 0x35;
    public const int Interrupt = 0x37;
    public const int IsPositioned = 0x38;
    public const int Spl = 0x39;
    public const int MultipleInstances = 0x3a;

    // Global registers 0x64..0x7f
    public const int SimSpeed = 0x64;
    public const int MainSongNum = 0x6e;
    public const int MainMusicHitListId = 0x6f;
    public const int MainDuckPri = 0x7b;
    public const int MainVol = 0x7c;
    public const int MainFxType = 0x7d;
    public const int MainFxLevel = 0x7e;
    public const int MainPause = 0x7f;

    public const int DefaultPitch = 3600; // 0xe10
    public const int DefaultVolume = 1024; // 0x400
    public const int DefaultPan = 512; // 0x200
}

public sealed record HotPatchDef(
    int PatchId,
    string Symbol,
    string AssetPath,
    bool IsLoop,
    bool IsStreamed = false,
    bool Preload = false,
    bool Is3d = false,
    bool IsHeadRelative = false,
    int Pitch = HitReg.DefaultPitch
);

public sealed record HotTrackDef(
    int TrackId,
    string Symbol,
    string Group,
    int InitialVol,
    int DuckPriority,
    int Spl,
    int InitialPatchId,
    string Tkd,
    int? EntryOffset,
    bool IsLoopedHint,
    bool IsPositionalObjectTrack,
    HitBankModule? Bank = null,
    HitBankModule? CodeBank = null
);

public sealed record HitEventMapping(
    string EventName,
    HitEventAction Action,
    int? TrackId,
    int? MusicMode,
    HitBankModule? Bank = null
);

/// <summary>Compiled HIT/HOT module holding bytecode, TrackData offset table, tracks, patches, and hitlists.</summary>
public sealed class HitBankModule
{
    public string Name { get; }
    public byte[] HitBytes { get; }
    public Dictionary<int, int> TrackDataOffsets { get; } = [];
    public Dictionary<int, HotTrackDef> Tracks { get; } = [];
    public Dictionary<int, HotPatchDef> Patches { get; } = [];
    public Dictionary<string, HotPatchDef> PatchesBySymbol { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int[]> GlobalHitLists { get; } = [];
    public Dictionary<string, HitEventMapping> EventMappings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HitBankModule(string name, byte[]? hitBytes = null)
    {
        Name = name;
        HitBytes = hitBytes ?? [];
    }
}

/// <summary>HIT SmartList non-repeating shuffle container matching 0057d56c.c, 005806fe.c, 005806a8.c, 00580738.c.</summary>
public sealed class HitSmartList
{
    public List<int> Available { get; } = [];
    public Queue<int> Used { get; } = new();
    public int Crush { get; set; }
    public bool HasExplicitCrush { get; set; }

    public int TotalCount => Available.Count + Used.Count;

    public HitSmartList() { }

    public HitSmartList(IEnumerable<int> items, int? explicitCrush = null)
    {
        foreach (var item in items)
        {
            Available.Add(item);
        }
        if (explicitCrush.HasValue)
        {
            Crush = Math.Max(0, explicitCrush.Value);
            HasExplicitCrush = true;
        }
        else
        {
            Crush = TotalCount >> 1;
        }
    }

    public void Add(int id)
    {
        if (!Available.Contains(id) && !Used.Contains(id))
        {
            Available.Add(id);
        }
        if (!HasExplicitCrush)
        {
            Crush = TotalCount >> 1;
        }
    }

    public void Remove(int id)
    {
        Available.RemoveAll(x => x == id);
        if (Used.Contains(id))
        {
            var kept = new Queue<int>(Used.Count);
            while (Used.Count > 0)
            {
                var item = Used.Dequeue();
                if (item != id) kept.Enqueue(item);
            }
            while (kept.Count > 0) Used.Enqueue(kept.Dequeue());
        }
        if (!HasExplicitCrush)
        {
            Crush = TotalCount >> 1;
        }
        while (Used.Count > Crush && Used.Count > 0)
        {
            Available.Add(Used.Dequeue());
        }
    }

    public void Clear()
    {
        Available.Clear();
        Used.Clear();
        if (!HasExplicitCrush)
        {
            Crush = 0;
        }
    }

    public void SetCrush(int crush)
    {
        Crush = Math.Max(0, crush);
        HasExplicitCrush = true;
        while (Used.Count > Crush)
        {
            Available.Add(Used.Dequeue());
        }
    }

    public int? Choose(Random rng)
    {
        if (Available.Count == 0)
        {
            if (Used.Count == 0) return null;
            int recycled = Used.Dequeue();
            Used.Enqueue(recycled);
            return recycled;
        }

        int idx = rng.Next(Available.Count);
        int chosen = Available[idx];
        Available.RemoveAt(idx);
        Used.Enqueue(chosen);
        while (Used.Count > Crush && Used.Count > 0)
        {
            Available.Add(Used.Dequeue());
        }
        return chosen;
    }
}

public abstract record HitVmCommand;

public sealed record HitStartVoiceCommand(
    int ThreadId,
    int TrackId,
    int PatchId,
    string Group,
    bool IsLoop,
    float Gain,
    float Pan,
    float PitchScale,
    int SourceObjectId,
    bool IsPositionalObjectTrack,
    string EventName,
    HitBankModule? Bank = null
) : HitVmCommand;

public sealed record HitUpdateVoiceCommand(
    int ThreadId,
    long VoiceId,
    float Gain,
    float Pan,
    float PitchScale
) : HitVmCommand;

public sealed record HitStopVoiceCommand(
    int ThreadId,
    long VoiceId
) : HitVmCommand;

public sealed record HitChainPatchCommand(
    int ThreadId,
    long VoiceId,
    int PatchId,
    HitBankModule? Bank = null
) : HitVmCommand;
