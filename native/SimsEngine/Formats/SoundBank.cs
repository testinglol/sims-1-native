using System.Buffers.Binary;
using System.Text;

namespace SimsEngine.Formats;

public sealed record SoundResolution(
    string RelativePath,
    string Group,
    float Duration,
    bool IsLoop,
    bool IsPositionalObjectTrack = true,
    bool IsPlaylist = false,
    float PitchScale = 1.0f,
    float VolumeScale = 1.0f,
    int? TrackId = null
);

/// <summary>Indexes audio archives, parses HIT equates and hitlists, decodes XA and UTK audio, and manages
/// ambient and mode playback.</summary>
public sealed class SoundBank : IDisposable
{
    public sealed record TrackInfo(int TrackId, string Symbol, string Group, int PatchId, string Tkd, bool IsLooped,
        int? Reg5 = null, int? Reg6 = null, int? Reg7 = null, bool IsPositionalObjectTrack = true,
        int? LoopPatchId = null, int? KilledTrackId = null)
    {
        internal HotBank? Bank { get; init; }
    }
    public sealed record PatchInfo(int PatchId, string Symbol, string AssetPath, bool IsLoop);

    internal sealed class HotBank
    {
        public Dictionary<int, TrackInfo> Tracks { get; } = [];
        public Dictionary<int, PatchInfo> Patches { get; } = [];
        public Dictionary<string, PatchInfo> PatchesBySymbol { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, List<int>> HitLists { get; } = [];
    }

    public bool IsMock { get; }
    public string SessionAudioDirectory { get; }
    public string? GameDirectory { get; }

    private readonly List<FarArchive> archives = [];
    private readonly Dictionary<string, (FarArchive Archive, FarArchive.Entry Entry)> farEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> diskFiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (int? TrackId, int? MusicMode, HotBank? Bank)> eventMappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HitEventMapping> fullEventMappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HitBankModule> hitBankModules = [];
    private readonly Dictionary<int, HotTrackDef> hotTracks = [];
    private readonly Dictionary<int, HotPatchDef> hotPatches = [];
    private readonly Dictionary<int, (int[] Items, int? ExplicitCrush)> autoHitLists = [];
    private int nextAutoPatchId = 0x20000;
    private readonly Dictionary<int, TrackInfo> tracks = [];
    private readonly Dictionary<int, PatchInfo> patches = [];
    private readonly Dictionary<string, PatchInfo> patchesBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PatchInfo> allPatches = [];
    private readonly Dictionary<int, List<int>> hitLists = [];
    private readonly Dictionary<string, int> lastChosenIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> multiVariationEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();

    private readonly Dictionary<int, List<string>> musicModes = [];
    private readonly Dictionary<string, SoundResolution> resolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    private static readonly string[] StrippedArchivePrefixes =
    [
        "sounddata/",
        "expansionshared/sound/",
        "expansionpack4/sound/",
        "expansionpack5/sound/",
        "expansionpack6/sound/",
        "expansionpack7/sound/",
        "expansionpack2/sound/",
        "expansionpack3/sound/",
        "expansionpack/",
        "deluxe/sound/",
        "deluxe/"
    ];

    private readonly Dictionary<string, List<string>> stationDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> stationTrackIndices = new(StringComparer.OrdinalIgnoreCase);

    public SoundBank(string? gameDirectory, string sessionAudioDirectory)
    {
        SessionAudioDirectory = sessionAudioDirectory;
        GameDirectory = gameDirectory;
        try { Directory.CreateDirectory(sessionAudioDirectory); } catch { }

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            IsMock = true;
            return;
        }

        IsMock = false;
        IndexFarArchives(gameDirectory);
        IndexHotFiles(gameDirectory);
        IndexMusic(gameDirectory);
    }

    public bool IsDemographicVoxEvent(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.StartsWith("vox", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("vox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("talk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("speech", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        lock (sync)
        {
            var track = ResolveMappedTrack(name);
            if (track != null && (track.Group == "VOX" || (track.Reg5.HasValue && (track.Reg6.HasValue || track.Reg7.HasValue))))
                return true;
            return false;
        }
    }

    private void IndexFarArchives(string gameDirectory)
    {
        var searchRoots = new[]
        {
            Path.Combine(gameDirectory, "SoundData"),
            Path.Combine(gameDirectory, "ExpansionPack"),
            Path.Combine(gameDirectory, "ExpansionPack2", "Sound"),
            Path.Combine(gameDirectory, "ExpansionPack3", "Sound"),
            Path.Combine(gameDirectory, "ExpansionShared", "Sound"),
            Path.Combine(gameDirectory, "ExpansionPack4", "Sound"),
            Path.Combine(gameDirectory, "ExpansionPack5", "Sound"),
            Path.Combine(gameDirectory, "ExpansionPack6", "Sound"),
            Path.Combine(gameDirectory, "ExpansionPack7", "Sound"),
            Path.Combine(gameDirectory, "Deluxe"),
            Path.Combine(gameDirectory, "Downloads")
        };

        var farPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in searchRoots)
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.far", SearchOption.AllDirectories))
                {
                    farPaths.Add(file);
                }
            }
        }

        foreach (var farPath in farPaths)
        {
            try
            {
                var archive = FarArchive.Open(farPath);
                archives.Add(archive);
                foreach (var entry in archive.Entries)
                {
                    var norm = entry.Name.Replace('\\', '/').TrimStart('/');
                    farEntries[norm] = (archive, entry);

                    var fileName = Path.GetFileName(norm);
                    if (!farEntries.ContainsKey(fileName)) farEntries[fileName] = (archive, entry);

                    foreach (var prefix in StrippedArchivePrefixes)
                    {
                        if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var stripped = norm.Substring(prefix.Length);
                            if (!farEntries.ContainsKey(stripped)) farEntries[stripped] = (archive, entry);
                        }
                    }

                    if (norm.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase))
                    {
                        var afterDownloads = norm.Substring("downloads/".Length);
                        if (!farEntries.ContainsKey(afterDownloads)) farEntries[afterDownloads] = (archive, entry);
                        var slashIdx = afterDownloads.IndexOf('/');
                        if (slashIdx >= 0 && slashIdx + 1 < afterDownloads.Length)
                        {
                            var afterSubfolder = afterDownloads.Substring(slashIdx + 1);
                            if (!farEntries.ContainsKey(afterSubfolder)) farEntries[afterSubfolder] = (archive, entry);
                        }
                    }
                }
            }
            catch { }
        }

        try
        {
            foreach (var ext in new[] { "*.xa", "*.wav", "*.utk", "*.mp3" })
            {
                foreach (var file in Directory.EnumerateFiles(gameDirectory, ext, SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/').TrimStart('/');
                    diskFiles[relative] = file;
                    var nameOnly = Path.GetFileName(file);
                    if (!diskFiles.ContainsKey(nameOnly)) diskFiles[nameOnly] = file;
                }
            }
        }
        catch { }
    }

    private void IndexHotFiles(string gameDirectory)
    {
        var hotFiles = new List<string>();
        try
        {
            hotFiles.AddRange(Directory.EnumerateFiles(gameDirectory, "*.hot", SearchOption.AllDirectories));
        }
        catch { }

        // Sort so expansion pack definitions override base game definitions.
        hotFiles.Sort((a, b) =>
        {
            int scoreA = a.Contains("Expansion", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            int scoreB = b.Contains("Expansion", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            return scoreA.CompareTo(scoreB);
        });

        var globalTkdSubroutines = new Dictionary<string, (HitBankModule Bank, int Offset)>(StringComparer.OrdinalIgnoreCase);
        foreach (var hotFile in hotFiles)
        {
            ParseHotFile(hotFile, globalTkdSubroutines);
        }

        foreach (var mod in hitBankModules)
        {
            foreach (var (tid, trk) in mod.Tracks.ToArray())
            {
                if (trk.EntryOffset.HasValue || string.IsNullOrEmpty(trk.Tkd)) continue;
                var rawSub = trk.Tkd.TrimStart('@');
                var subName = rawSub.StartsWith("tkd_", StringComparison.OrdinalIgnoreCase) ? rawSub[4..] : rawSub;
                if (globalTkdSubroutines.TryGetValue(subName, out var resolved))
                {
                    var updated = trk with { EntryOffset = resolved.Offset, CodeBank = resolved.Bank };
                    mod.Tracks[tid] = updated;
                    if (hotTracks.TryGetValue(tid, out var existing) && ReferenceEquals(existing, trk))
                    {
                        hotTracks[tid] = updated;
                    }
                }
            }
        }
    }

    private static string? FindSiblingFileIgnoreCase(string baseFilePath, string targetExtension)
    {
        var direct = Path.ChangeExtension(baseFilePath, targetExtension);
        if (File.Exists(direct)) return direct;
        var dir = Path.GetDirectoryName(baseFilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var expectedName = Path.GetFileName(direct);
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(candidate).Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch { }
        return null;
    }

    private void ParseHotFile(string hotFilePath, Dictionary<string, (HitBankModule Bank, int Offset)> globalTkdSubroutines)
    {
        try
        {
            var hsmSubroutineOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hsmTkdIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (FindSiblingFileIgnoreCase(hotFilePath, ".hsm") is { } hsmPath)
            {
                string? pendingTkdSubroutine = null;
                foreach (var hsmLine in File.ReadAllLines(hsmPath, Encoding.Latin1))
                {
                    var parts = hsmLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && TryParseIntOrHex(parts[1], out var val))
                    {
                        if (parts[0].StartsWith("tkd_", StringComparison.OrdinalIgnoreCase))
                        {
                            var subSymbol = parts[0][4..];
                            hsmTkdIds[subSymbol] = val;
                            pendingTkdSubroutine = subSymbol;
                        }
                        else if (pendingTkdSubroutine != null && parts[0].Equals(pendingTkdSubroutine, StringComparison.OrdinalIgnoreCase))
                        {
                            hsmSubroutineOffsets[parts[0]] = val;
                            pendingTkdSubroutine = null;
                        }
                        else
                        {
                            pendingTkdSubroutine = null;
                        }
                    }
                    else if (parts.Length > 0)
                    {
                        pendingTkdSubroutine = null;
                    }
                }
            }

            byte[]? hitBytes = null;
            if (FindSiblingFileIgnoreCase(hotFilePath, ".hit") is { } hitDiskPath)
            {
                hitBytes = File.ReadAllBytes(hitDiskPath);
            }
            else if (FindAsset(Path.GetFileNameWithoutExtension(hotFilePath) + ".hit") is { } hitEntry)
            {
                hitBytes = hitEntry.Archive.Read(hitEntry.Entry).ToArray();
            }

            var bank = new HotBank();
            var module = new HitBankModule(Path.GetFileNameWithoutExtension(hotFilePath), hitBytes);
            var equates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["kSndobPlay"] = 1,
                ["kSndobStop"] = 2,
                ["kSndobKill"] = 3,
                ["kSndobUpdate"] = 4,
                ["kSndobCancelNote"] = 19,
                ["kKillAll"] = 20,
                ["kPause"] = 21,
                ["kUnpause"] = 22,
                ["kKillInstance"] = 23,
                ["kTurnOnTv"] = 30,
                ["kTurnOffTv"] = 31,
                ["kSetMusicMode"] = 36,
                ["kPlayPiano"] = 43,
                ["kSequenceTrackHitList"] = 100,
                ["kDuckPriAlways"] = 0,
                ["kDuckPriLow"] = 10,
                ["kDuckPriNormal"] = 20,
                ["kDuckPriHigh"] = 30,
                ["kDuckPriHigher"] = 40,
                ["kDuckPriEvenHigher"] = 50,
                ["kDuckPriNever"] = 100,
                ["kSplInfinite"] = 0,
                ["kSplLoud"] = 10,
                ["kSplNormal"] = 20,
                ["kSplQuiet"] = 100,
                ["kSplStereo"] = 10001,
                ["kSplVox"] = 20
            };
            var trackDataOffsets = module.TrackDataOffsets;
            var pendingTracks = new List<(int TrackId, string Symbol, string Group, int InitialVol, int DuckPriority, int Spl, string PatchRaw, string Tkd, bool IsLooped, bool IsPositionalObjectTrack)>();
            var lines = File.ReadAllLines(hotFilePath, Encoding.Latin1);
            string? section = null;

            int ParseEquateOrInt(string token, int fallback = 0)
            {
                var t = token.Trim();
                if (equates.TryGetValue(t, out var eqVal)) return eqVal;
                if (TryParseIntOrHex(t, out var num)) return num;
                return fallback;
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim().ToLowerInvariant();
                    continue;
                }

                if (section == "eventmappingequate" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (TryParseIntOrHex(parts[1].Trim(), out var eqVal))
                    {
                        equates[parts[0].Trim()] = eqVal;
                    }
                }
                else if (section == "trackdata" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (TryParseIntOrHex(parts[0].Trim(), out var tkdId) && TryParseIntOrHex(parts[1].Trim(), out var byteOff))
                    {
                        trackDataOffsets[tkdId] = byteOff;
                    }
                }
                else if (section == "eventmapping" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    var evName = parts[0].Trim();
                    var valParts = parts[1].Split(',');
                    var action = valParts[0].Trim();
                    int actionCode = ParseEquateOrInt(action, 0);

                    if ((action.Equals("kSetMusicMode", StringComparison.OrdinalIgnoreCase) || actionCode == 36) && valParts.Length > 1 && int.TryParse(valParts[1].Trim(), out var mode))
                    {
                        eventMappings[evName] = (null, mode, bank);
                        var mapping = new HitEventMapping(evName, HitEventAction.SetMusicMode, null, mode, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (action.Equals("kPlayPiano", StringComparison.OrdinalIgnoreCase) || actionCode == 43)
                    {
                        var pianoTrk = valParts.Length > 1 && int.TryParse(valParts[1].Trim(), out var pTrk) && pTrk > 0 ? pTrk : 1906;
                        eventMappings[evName] = (pianoTrk, null, bank);
                        var mapping = new HitEventMapping(evName, HitEventAction.PlayPiano, pianoTrk, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (action.Equals("kSndobCancelNote", StringComparison.OrdinalIgnoreCase) || (valParts.Length == 1 && actionCode == 19))
                    {
                        var mapping = new HitEventMapping(evName, HitEventAction.SndobCancelNote, null, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (action.Equals("kKillInstance", StringComparison.OrdinalIgnoreCase) || (valParts.Length == 1 && actionCode == 23))
                    {
                        var mapping = new HitEventMapping(evName, HitEventAction.KillInstance, null, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (action.Equals("kKillAll", StringComparison.OrdinalIgnoreCase) || (valParts.Length == 1 && actionCode == 20))
                    {
                        var mapping = new HitEventMapping(evName, HitEventAction.KillAll, null, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (action.Equals("kTurnOffTv", StringComparison.OrdinalIgnoreCase) || (valParts.Length == 1 && actionCode == 31))
                    {
                        var mapping = new HitEventMapping(evName, HitEventAction.TurnOffTv, null, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (valParts.Length > 1 && int.TryParse(valParts[1].Trim(), out var trkId))
                    {
                        eventMappings[evName] = (trkId, null, bank);
                        var evAction = actionCode switch
                        {
                            2 => HitEventAction.SndobStop,
                            3 => HitEventAction.SndobKill,
                            30 => HitEventAction.TurnOnTv,
                            100 => HitEventAction.SequenceTrackHitList,
                            _ => HitEventAction.SndobPlay
                        };
                        var mapping = new HitEventMapping(evName, evAction, trkId, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                    else if (int.TryParse(action, out var directTrkId))
                    {
                        eventMappings[evName] = (directTrkId, null, bank);
                        var mapping = new HitEventMapping(evName, HitEventAction.SndobPlay, directTrkId, null, module);
                        module.EventMappings[evName] = mapping;
                        fullEventMappings[evName] = mapping;
                    }
                }
                else if (section == "track" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (int.TryParse(parts[0].Trim(), out var trkId))
                    {
                        var vals = parts[1].Split(',');
                        if (vals.Length >= 8)
                        {
                            var sym = vals[1].Trim();
                            var initialVol = ParseEquateOrInt(vals[2], HitReg.DefaultVolume);
                            var argsRaw = vals[3].Trim();
                            var duckPriority = ParseEquateOrInt(vals[4], 10);
                            var grpRaw = vals[5].Trim();
                            var group = grpRaw.Contains("Vox", StringComparison.OrdinalIgnoreCase) || grpRaw == "3" ? "VOX" :
                                        grpRaw.Contains("Music", StringComparison.OrdinalIgnoreCase) || grpRaw == "2" ? "MUSIC" : "FX";
                            var spl = ParseEquateOrInt(vals[6], 20);
                            var tkd = vals[7].Trim();
                            // Parameter 8 specifies the initial patch ID (or @PatchSymbol) rather than a loop flag.
                            var isLooped = tkd.Contains("GenericLooped", StringComparison.OrdinalIgnoreCase);
                            var patchRaw = vals.Length > 8 ? vals[8].Trim() : string.Empty;

                            var isStationOrPianoTrack =
                                sym.StartsWith("TrkRadio", StringComparison.OrdinalIgnoreCase) ||
                                sym.StartsWith("TrkBossa", StringComparison.OrdinalIgnoreCase) ||
                                sym.StartsWith("TrkClass", StringComparison.OrdinalIgnoreCase) ||
                                sym.StartsWith("station_", StringComparison.OrdinalIgnoreCase) ||
                                sym.Equals("Piano", StringComparison.OrdinalIgnoreCase);
                            var isPositionalObjectTrack =
                                !argsRaw.Equals("kArgsNormal", StringComparison.OrdinalIgnoreCase) &&
                                !argsRaw.Equals("kArgsNone", StringComparison.OrdinalIgnoreCase) &&
                                !argsRaw.Equals("kArgs", StringComparison.OrdinalIgnoreCase) &&
                                argsRaw != "0" &&
                                !(group == "MUSIC" && !isStationOrPianoTrack);

                            pendingTracks.Add((trkId, sym, group, initialVol, duckPriority, spl, patchRaw, tkd, isLooped, isPositionalObjectTrack));
                        }
                    }
                }
                else if (section == "patch" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (int.TryParse(parts[0].Trim(), out var patchId))
                    {
                        var vals = parts[1].Split(',');
                        if (vals.Length >= 3)
                        {
                            var sym = vals[1].Trim();
                            var pathIndex = 2;
                            for (int idx = 2; idx < vals.Length; idx++)
                            {
                                if (vals[idx].Trim().StartsWith('"')) { pathIndex = idx; break; }
                            }
                            var path = vals[pathIndex].Trim().Trim('"');
                            bool BoolCol(int offset) =>
                                vals.Length > pathIndex + offset &&
                                int.TryParse(vals[pathIndex + offset].Trim(), out var v) &&
                                v != 0;

                            bool isLoop = BoolCol(1);
                            bool isStreamed = BoolCol(2);
                            bool preload = BoolCol(3);
                            bool is3d = BoolCol(4);
                            bool isHeadRelative = BoolCol(5);

                            var patch = new PatchInfo(patchId, sym, path, isLoop);
                            bank.Patches[patchId] = patch;
                            bank.PatchesBySymbol[sym] = patch;
                            patches[patchId] = patch;
                            patchesBySymbol[sym] = patch;
                            allPatches.Add(patch);

                            var hotPatch = new HotPatchDef(patchId, sym, path, isLoop, isStreamed, preload, is3d, isHeadRelative);
                            module.Patches[patchId] = hotPatch;
                            module.PatchesBySymbol[sym] = hotPatch;
                            hotPatches[patchId] = hotPatch;
                        }
                    }
                }
                else if (section == "globalhitlist" && line.Contains('='))
                {
                    var parts = line.Split('=', 2);
                    if (int.TryParse(parts[0].Trim(), out var hitId))
                    {
                        var list = new List<int>();
                        var items = parts[1].Split(',');
                        foreach (var item in items)
                        {
                            var trimmed = item.Trim();
                            if (trimmed.Contains('-'))
                            {
                                var range = trimmed.Split('-');
                                if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end))
                                {
                                    for (int id = start; id <= end; id++) list.Add(id);
                                }
                            }
                            else if (int.TryParse(trimmed, out var id))
                            {
                                list.Add(id);
                            }
                        }
                        if (list.Count > 0)
                        {
                            bank.HitLists[hitId] = list;
                            hitLists[hitId] = list;
                            module.GlobalHitLists[hitId] = list.ToArray();
                        }
                    }
                }
            }

            int[] subroutineBoundaries = [];
            if (hitBytes != null)
            {
                bool isCanonical = module.Name.Equals("SimsSound", StringComparison.OrdinalIgnoreCase) ||
                                   module.Name.Equals("SimsGeneratedHitSource", StringComparison.OrdinalIgnoreCase);
                foreach (var (subSym, tkdId) in hsmTkdIds)
                {
                    if (trackDataOffsets.TryGetValue(tkdId, out var tdOff) && tdOff >= 0 && tdOff < hitBytes.Length)
                    {
                        if (isCanonical)
                            globalTkdSubroutines[subSym] = (module, tdOff);
                        else
                            globalTkdSubroutines.TryAdd(subSym, (module, tdOff));
                    }
                }
                foreach (var (subSym, hsmOff) in hsmSubroutineOffsets)
                {
                    if (hsmOff >= 0 && hsmOff < hitBytes.Length)
                    {
                        if (isCanonical)
                            globalTkdSubroutines[subSym] = (module, hsmOff);
                        else
                            globalTkdSubroutines.TryAdd(subSym, (module, hsmOff));
                    }
                }
            }
            if (hitBytes != null && pendingTracks.Count > 0)
            {
                subroutineBoundaries = hsmSubroutineOffsets.Values
                    .Concat(trackDataOffsets.Values)
                    .Where(off => off >= 0 && off < hitBytes.Length)
                    .Distinct()
                    .OrderBy(off => off)
                    .ToArray();
            }

            foreach (var pending in pendingTracks)
            {
                int patchId = 0;
                if (!string.IsNullOrEmpty(pending.PatchRaw))
                {
                    if (!TryParseIntOrHex(pending.PatchRaw, out patchId))
                    {
                        var pSym = pending.PatchRaw.TrimStart('@');
                        if (bank.PatchesBySymbol.TryGetValue(pSym, out var bp))
                            patchId = bp.PatchId;
                        else if (patchesBySymbol.TryGetValue(pSym, out var gp))
                            patchId = gp.PatchId;
                    }
                }

                int? reg5 = null, reg6 = null, reg7 = null, loopPatchId = null, killedTrackId = null;
                var isLooped = pending.IsLooped;
                int? resolvedOffset = null;
                if (hitBytes != null && pending.Tkd.Length > 0)
                {
                    var rawSub = pending.Tkd.TrimStart('@');
                    var subName = rawSub.StartsWith("tkd_", StringComparison.OrdinalIgnoreCase) ? rawSub[4..] : rawSub;
                    if (TryParseIntOrHex(rawSub, out var numericTkdId) && trackDataOffsets.TryGetValue(numericTkdId, out var numOff))
                        resolvedOffset = numOff;
                    else if (hsmTkdIds.TryGetValue(subName, out var tkdId) && trackDataOffsets.TryGetValue(tkdId, out var tdOff))
                        resolvedOffset = tdOff;
                    else if (hsmSubroutineOffsets.TryGetValue(subName, out var hsmOff))
                        resolvedOffset = hsmOff;

                    if (resolvedOffset.HasValue && resolvedOffset.Value >= 0 && resolvedOffset.Value < hitBytes.Length)
                    {
                        var subInfo = AnalyzeHitSubroutine(hitBytes, resolvedOffset.Value, subroutineBoundaries, trackDataOffsets);
                        if (patchId is 0 or 1)
                        {
                            reg5 = subInfo.Reg5;
                            reg6 = subInfo.Reg6;
                            reg7 = subInfo.Reg7;
                        }
                        loopPatchId = subInfo.LoopPatchId;
                        killedTrackId = subInfo.KilledTrackId;
                        if (pending.Group == "FX" && subInfo.HasLoopOpcode && !subInfo.HasTimerWait)
                        {
                            isLooped = true;
                        }
                    }
                }

                var trackInfo = new TrackInfo(
                    pending.TrackId, pending.Symbol, pending.Group, patchId, pending.Tkd,
                    isLooped, reg5, reg6, reg7, pending.IsPositionalObjectTrack, loopPatchId, killedTrackId)
                {
                    Bank = bank
                };
                bank.Tracks[pending.TrackId] = trackInfo;
                tracks[pending.TrackId] = trackInfo;

                var hotTrack = new HotTrackDef(
                    pending.TrackId,
                    pending.Symbol,
                    pending.Group,
                    pending.InitialVol,
                    pending.DuckPriority,
                    pending.Spl,
                    patchId,
                    pending.Tkd,
                    resolvedOffset,
                    isLooped,
                    pending.IsPositionalObjectTrack,
                    module);
                module.Tracks[pending.TrackId] = hotTrack;
                hotTracks[pending.TrackId] = hotTrack;
            }
            hitBankModules.Add(module);
        }
        catch { }
    }

    private void IndexMusic(string gameDirectory)
    {
        void AddModeMusic(int mode, params string[] relativeDirs)
        {
            foreach (var relativeDir in relativeDirs)
            {
                var dir = Path.Combine(gameDirectory, relativeDir);
                if (Directory.Exists(dir))
                {
                    var files = Directory.EnumerateFiles(dir, "*.mp3", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (files.Count == 0)
                    {
                        files = Directory.EnumerateFiles(dir, "*.wav", SearchOption.AllDirectories)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    if (files.Count > 0)
                    {
                        musicModes[mode] = files;
                        return;
                    }
                }
            }
        }

        void AddStationAudio(string stationKey, string relativeDir, params int[] autoPatchHitListIds)
        {
            AddStationAudioMulti(stationKey, [relativeDir], autoPatchHitListIds);
        }

        void AddStationAudioMulti(string stationKey, string[] relativeDirs, params int[] autoPatchHitListIds)
        {
            var collected = new List<string>();
            var subListIds = new List<int>();
            foreach (var relativeDir in relativeDirs)
            {
                var dir = Path.Combine(gameDirectory, relativeDir);
                if (!Directory.Exists(dir)) continue;

                // RadioStationAutoPatch (00574d8b.c, 00575062.c): group by immediate subdirectories when
                // present (e.g. TVStations/TV_Action/a..g), or treat the root directory as one sub-list.
                var subDirs = Directory.EnumerateDirectories(dir)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var scanDirs = subDirs.Count > 0 ? subDirs : [dir];
                foreach (var scanDir in scanDirs)
                {
                    var subFiles = new List<string>();
                    foreach (var ext in new[] { "*.mp3", "*.xa", "*.wav", "*.utk" })
                    {
                        subFiles.AddRange(Directory.EnumerateFiles(scanDir, ext, SearchOption.AllDirectories));
                    }
                    if (subFiles.Count == 0) continue;

                    subFiles.Sort(StringComparer.OrdinalIgnoreCase);
                    collected.AddRange(subFiles);

                    if (autoPatchHitListIds.Length > 0)
                    {
                        var subPatchIds = new List<int>(subFiles.Count);
                        foreach (var filePath in subFiles)
                        {
                            int pid = nextAutoPatchId++;
                            var relPath = Path.GetRelativePath(gameDirectory, filePath).Replace('\\', '/');
                            var sym = $"RadioStationAutoPatch_{Path.GetFileNameWithoutExtension(filePath)}_{pid}";
                            var hotPatch = new HotPatchDef(pid, sym, relPath, IsLoop: false);
                            hotPatches[pid] = hotPatch;
                            var patchInfo = new PatchInfo(pid, sym, relPath, false);
                            patches[pid] = patchInfo;
                            patchesBySymbol[sym] = patchInfo;
                            subPatchIds.Add(pid);
                        }
                        int subListId = nextAutoPatchId++;
                        autoHitLists[subListId] = (subPatchIds.ToArray(), 1_000_000);
                        hitLists[subListId] = subPatchIds;
                        subListIds.Add(subListId);
                    }
                }
            }
            if (collected.Count > 0)
            {
                stationDirectories[stationKey] = collected.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (subListIds.Count > 0)
                {
                    var topArr = subListIds.ToArray();
                    foreach (var hitId in autoPatchHitListIds)
                    {
                        autoHitLists[hitId] = (topArr, 1_000_000);
                        hitLists[hitId] = [.. subListIds];
                    }
                }
            }
        }

        AddModeMusic(1, Path.Combine("Music", "Modes", "Buy"));
        AddModeMusic(2, Path.Combine("Music", "Modes", "Build"));
        AddModeMusic(3, Path.Combine("Music", "Modes", "Nhood"));
        AddModeMusic(4, Path.Combine("Music", "Modes", "NhoodUS"));
        AddModeMusic(5, Path.Combine("Music", "Modes", "Load"), Path.Combine("Music", "Modes", "Superstar"));
        AddModeMusic(6, Path.Combine("Music", "Modes", "Credits"), Path.Combine("Music", "Modes", "MM_credits"), Path.Combine("Music", "Modes", "MM_Credits"));
        AddModeMusic(10, Path.Combine("Music", "Modes", "dt_nhood"));
        AddModeMusic(11, Path.Combine("Music", "Modes", "Vacation"));
        AddModeMusic(12, Path.Combine("Music", "Modes", "NhoodUS"));
        AddModeMusic(13, Path.Combine("Music", "Modes", "Superstar"));
        AddModeMusic(14, Path.Combine("Music", "Modes", "SuperstarTransition"), Path.Combine("Music", "Stations", "Superstar"), Path.Combine("Music", "Modes", "Superstar"));
        AddModeMusic(15, Path.Combine("Music", "Modes", "MakinMagic"));
        AddModeMusic(16, Path.Combine("Music", "Modes", "MM_Credits"), Path.Combine("Music", "Modes", "MM_credits"));
        AddModeMusic(17, Path.Combine("Music", "Modes", "MM_Build"));
        AddModeMusic(18, Path.Combine("Music", "Modes", "MM_Buy"));
        AddModeMusic(19, Path.Combine("Music", "Modes", "MM_Credits"), Path.Combine("Music", "Modes", "MM_credits"));

        AddStationAudio("radio_country", Path.Combine("Music", "Stations", "Country"), 232);
        AddStationAudio("radio_rock", Path.Combine("Music", "Stations", "Rock"), 233);
        AddStationAudio("radio_latin", Path.Combine("Music", "Stations", "Latin"), 234);
        AddStationAudio("radio_classical", Path.Combine("Music", "Stations", "Classica"), 235);
        AddStationAudio("radio_countrydance", Path.Combine("Music", "Stations", "CountryD"), 1586);
        AddStationAudio("radio_rap", Path.Combine("Music", "Stations", "Rap"), 1587);
        AddStationAudio("radio_rave", Path.Combine("Music", "Stations", "Rave"), 1588);
        AddStationAudio("radio_disco", Path.Combine("Music", "Stations", "Disco"), 1589);
        AddStationAudio("radio_beach", Path.Combine("Music", "Stations", "Beach"), 1590);
        AddStationAudio("station_ezcheap", Path.Combine("Music", "Stations", "EZ"), 65536);
        AddStationAudio("station_ezexp", Path.Combine("Music", "Stations", "EZExp"), 65537);
        AddStationAudio("station_unlcheap", Path.Combine("Music", "Stations", "UnlCheap"), 20900);
        AddStationAudio("station_unlspeak", Path.Combine("Music", "Stations", "UnlSpeak"), 20901, 20902);
        AddStationAudio("stereo_superstar", Path.Combine("Music", "Stations", "Superstar"), 100100);

        AddStationAudio("tv_action", Path.Combine("SoundData", "TVStations", "TV_Action"), 529);
        AddStationAudio("tv_cartoon", Path.Combine("SoundData", "TVStations", "TV_Comedy_cartoon"), 530);
        AddStationAudio("tv_romance", Path.Combine("SoundData", "TVStations", "TV_Romance"), 528);
        AddStationAudio("tv_mystery", Path.Combine("SoundData", "TVStations", "TV_Mystery"), 531);
        AddStationAudio("tv_horror", Path.Combine("SoundData", "TVStations", "TV_Mystery"));
        AddStationAudio("tv_superstar_animal", Path.Combine("ExpansionPack6", "Sound", "TVStations", "Animal"), 103000);
        AddStationAudio("tv_superstar_entertainment", Path.Combine("ExpansionPack6", "Sound", "TVStations", "Entertainment"), 103001);
        AddStationAudio("tv_superstar_news", Path.Combine("ExpansionPack6", "Sound", "TVStations", "News"), 103002);
        AddStationAudio("tv_superstar_sports", Path.Combine("ExpansionPack6", "Sound", "TVStations", "Sports"), 103003);
    }

    private static bool TryParseIntOrHex(string value, out int result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out result);
        return int.TryParse(value, out result);
    }

    private readonly record struct HitSubroutineInfo(
        int? Reg5, int? Reg6, int? Reg7, int? LoopPatchId, int? KilledTrackId, bool HasLoopOpcode, bool HasTimerWait);

    private static int NextSubroutineBoundary(int[] sortedBoundaries, int offset, int hitLength)
    {
        var idx = Array.BinarySearch(sortedBoundaries, offset);
        var nextIdx = idx >= 0 ? idx + 1 : ~idx;
        return nextIdx >= 0 && nextIdx < sortedBoundaries.Length && sortedBoundaries[nextIdx] > offset
            ? Math.Min(hitLength, sortedBoundaries[nextIdx])
            : hitLength;
    }

    /// <summary>Analyzes HIT bytecode instructions to extract patch registers, loop commands, timer waits,
    /// kill-track targets, and branch destinations.</summary>
    private static HitSubroutineInfo AnalyzeHitSubroutine(
        byte[] hit,
        int offset,
        int[] subroutineBoundaries,
        IReadOnlyDictionary<int, int> trackDataOffsets)
    {
        int? reg5 = null, reg6 = null, reg7 = null, regPatch = null, killedTrackId = null;
        int? lastReg5Load = null;
        bool hasLoopOpcode = false, hasTimerWait = false;

        var pendingOffsets = new Queue<int>();
        var visitedOffsets = new HashSet<int>();
        pendingOffsets.Enqueue(offset);
        visitedOffsets.Add(offset);

        while (pendingOffsets.Count > 0)
        {
            int startOffset = pendingOffsets.Dequeue();
            int limit = NextSubroutineBoundary(subroutineBoundaries, startOffset, hit.Length);
            int pc = startOffset;
            while (pc < limit)
            {
                byte op = hit[pc];
                if (op is 0x08 or 0x0c or 0x1b) // Stop on return (0x08), end (0x0C), or error (0x1B) opcodes.
                    break;
                if (op == 0x20) // Loop track command (0x20).
                {
                    hasLoopOpcode = true;
                    break;
                }
                if (op == 0x09) // Wait timer command (0x09).
                {
                    hasTimerWait = true;
                }
                else if (op == 0x05 && pc + 6 <= limit) // Load immediate command (0x05).
                {
                    byte reg = hit[pc + 1];
                    int val = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(pc + 2, 4));
                    if (reg == 5) lastReg5Load = val;
                    if (val is > 0 and < 200000 && !(reg == 5 && val == 200 && pc + 8 < limit && hit[pc + 8] == 0x18))
                    {
                        if (reg == 5) reg5 ??= val;
                        else if (reg == 6) reg6 ??= val;
                        else if (reg == 7) reg7 ??= val;
                        else if (reg == 0x32) regPatch = val; // Register 50 (0x32) holds the patch override ID.
                    }
                }
                else if (op == 0x33 && pc + 2 <= limit && hit[pc + 1] == 5 && lastReg5Load is > 0) // Kill track command (0x33).
                {
                    killedTrackId = lastReg5Load;
                }
                else if (op is 0x07 or 0x0d or (>= 0x3e and <= 0x43) && pc + 5 <= limit) // Branch commands targeting a 32-bit track identifier.
                {
                    int targetTkdId = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(pc + 1, 4));
                    if (visitedOffsets.Count < 8 &&
                        trackDataOffsets.TryGetValue(targetTkdId, out var targetOffset) &&
                        targetOffset >= 0 && targetOffset < hit.Length &&
                        visitedOffsets.Add(targetOffset))
                    {
                        pendingOffsets.Enqueue(targetOffset);
                    }
                    if (op == 0x07)
                        break;
                }

                int instrLen = HitInstructionLength(op);
                if (instrLen <= 0)
                    break;
                pc += instrLen;
            }
        }
        return new HitSubroutineInfo(reg5 ?? regPatch, reg6, reg7, regPatch, killedTrackId, hasLoopOpcode, hasTimerWait);
    }

    private TrackInfo? ResolveMappedTrack(string name)
    {
        TrackInfo? track = null;
        if (eventMappings.TryGetValue(name, out var mapping) && mapping.TrackId is { } trkId)
        {
            if (mapping.Bank != null)
                mapping.Bank.Tracks.TryGetValue(trkId, out track);
            track ??= tracks.GetValueOrDefault(trkId);
        }
        track ??= tracks.Values.FirstOrDefault(t => t.Symbol.Equals(name, StringComparison.OrdinalIgnoreCase));
        return track;
    }

    private PatchInfo? GetPatchForTrack(TrackInfo? track, int patchId)
    {
        if (track?.Bank != null && track.Bank.Patches.TryGetValue(patchId, out var bankPatch))
            return bankPatch;
        return patches.GetValueOrDefault(patchId);
    }

    private bool TryGetHitListForTrack(TrackInfo? track, int hitListId, out List<int> listIds)
    {
        if (track?.Bank != null && track.Bank.HitLists.TryGetValue(hitListId, out listIds!))
            return true;
        return hitLists.TryGetValue(hitListId, out listIds!);
    }

    private TrackInfo? GetSubTrackForTrack(TrackInfo? track, int subTrackId)
    {
        if (track?.Bank != null && track.Bank.Tracks.TryGetValue(subTrackId, out var bankTrack))
            return bankTrack;
        return tracks.GetValueOrDefault(subTrackId);
    }

    private PatchInfo? GetPatchBySymbolForTrack(TrackInfo? track, string symbol)
    {
        if (track?.Bank != null && track.Bank.PatchesBySymbol.TryGetValue(symbol, out var bankPatch))
            return bankPatch;
        return patchesBySymbol.GetValueOrDefault(symbol);
    }

    private static int HitInstructionLength(byte op) => op switch
    {
        0x08 or 0x0b or 0x0c or 0x0f or 0x1b or 0x1d or 0x1e or 0x20 or 0x21 or 0x25 or (>= 0x3a and <= 0x3d) or 0x46 => 1,
        0x02 or 0x03 or 0x09 or 0x0e or 0x17 or 0x19 or 0x1c or 0x23 or 0x24 or 0x26 or 0x27 or (>= 0x2f and <= 0x33) or 0x44 or 0x45 or 0x47 or 0x53 => 2,
        0x01 or 0x04 or 0x06 or (>= 0x10 and <= 0x16) or 0x1f or 0x22 or (>= 0x28 and <= 0x2c) or (>= 0x49 and <= 0x52) => 3,
        0x18 or 0x48 => 4,
        0x07 or 0x0d or (>= 0x3e and <= 0x43) => 5,
        0x05 or 0x2d or 0x2e => 6,
        _ => 0
    };

    public string? GetKilledTrackSymbol(string name)
    {
        if (IsMock || string.IsNullOrWhiteSpace(name)) return null;
        lock (sync)
        {
            var track = ResolveMappedTrack(name);
            if (track?.KilledTrackId is { } killedId &&
                GetSubTrackForTrack(track, killedId) is { } target &&
                !string.IsNullOrWhiteSpace(target.Symbol))
            {
                return target.Symbol;
            }
            return null;
        }
    }

    private int ChooseNonRepeatingIndex(string key, int count)
    {
        if (count <= 1) return 0;
        var next = random.Next(count);
        if (lastChosenIndex.TryGetValue(key, out var prev) && next == prev)
            next = (next + 1 + random.Next(count - 1)) % count;
        lastChosenIndex[key] = next;
        return next;
    }

    public SoundResolution? Resolve(string name, int? pianoSkillLevel = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        lock (sync)
        {
            if (pianoSkillLevel is null && !multiVariationEvents.Contains(name) && resolutionCache.TryGetValue(name, out var cached)) return cached;

            SoundResolution? res = null;
            if (!IsMock)
            {
                res = ResolveMusic(name, pianoSkillLevel) ?? ResolveAmbience(name) ?? ResolveHotEvent(name) ?? ResolveDirect(name);
            }

            if (res == null && IsMock)
            {
                res = CreateMockResolution(name);
            }

            if (res != null && pianoSkillLevel is null && res.Group != "MUSIC" && !multiVariationEvents.Contains(name)) resolutionCache[name] = res;
            return res;
        }
    }

    public SoundResolution? ResolveVoice(string name, char voiceCode, int? preferredVariation = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (sync)
        {
            var code = char.ToLowerInvariant(voiceCode);
            if (code != 'k' && code != 'f' && code != 'm') code = 'm';
            if (preferredVariation.HasValue)
            {
                var cacheKey = $"{name}:{code}:{preferredVariation.Value}";
                if (resolutionCache.TryGetValue(cacheKey, out var cached)) return cached;
                if (IsMock)
                {
                    var mockRes = CreateMockResolution($"{name}_{code}{preferredVariation.Value}");
                    resolutionCache[cacheKey] = mockRes;
                    return mockRes;
                }
            }
            else if (IsMock)
            {
                return CreateMockResolution($"{name}_{code}1");
            }

            var candidates = allPatches
                .Where(p => MatchesDemographic(p.Symbol, name, code))
                .OrderBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Fall back to adult voice patches if no child-specific patch exists.
            if (candidates.Count == 0 && code == 'k')
            {
                candidates = allPatches
                    .Where(p => MatchesDemographic(p.Symbol, name, 'f'))
                    .OrderBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (candidates.Count == 0)
                {
                    candidates = allPatches
                        .Where(p => MatchesDemographic(p.Symbol, name, 'm'))
                        .OrderBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }

            if (candidates.Count == 0)
            {
                var demoNames = code == 'k'
                    ? new[] { $"{name}k", $"{name}_k", $"{name}f", $"{name}_f", $"{name}m", $"{name}_m" }
                    : new[] { $"{name}{code}", $"{name}_{code}" };
                foreach (var demoName in demoNames)
                {
                    var demoTrack = ResolveMappedTrack(demoName);
                    if (demoTrack != null)
                    {
                        var lookupId = demoTrack.PatchId > 1 ? demoTrack.PatchId : (demoTrack.Reg5 ?? 0);
                        if (lookupId > 0)
                        {
                            if (TryGetHitListForTrack(demoTrack, lookupId, out var listIds))
                            {
                                foreach (var pid in listIds)
                                    if (GetPatchForTrack(demoTrack, pid) is { } p) candidates.Add(p);
                            }
                            else if (GetPatchForTrack(demoTrack, lookupId) is { } sp)
                            {
                                candidates.Add(sp);
                            }
                        }
                        if (candidates.Count > 0) break;
                    }
                }
            }

            // Match exact track names for generic smartlists and pet sound events.
            TrackInfo? track = null;
            if (candidates.Count == 0)
            {
                track = ResolveMappedTrack(name);

                if (track != null)
                {
                    int? hitListId = code switch
                    {
                        'm' => track.Reg5,
                        'f' => track.Reg6 ?? track.Reg5,
                        'k' => track.Reg7 ?? track.Reg6 ?? track.Reg5,
                        _ => track.Reg5
                    };
                    if (!hitListId.HasValue && track.PatchId > 1) hitListId = track.PatchId;
                    if (hitListId.HasValue)
                    {
                        if (TryGetHitListForTrack(track, hitListId.Value, out var patchIds))
                        {
                            foreach (var pid in patchIds)
                            {
                                if (GetPatchForTrack(track, pid) is { } p) candidates.Add(p);
                                else if (GetSubTrackForTrack(track, pid) is { } subTrack && subTrack.PatchId > 1 && GetPatchForTrack(subTrack, subTrack.PatchId) is { } tp)
                                    candidates.Add(tp);
                            }
                        }
                        else if (GetPatchForTrack(track, hitListId.Value) is { } singlePatch)
                        {
                            candidates.Add(singlePatch);
                        }
                    }
                    if (candidates.Count == 0 && GetPatchBySymbolForTrack(track, track.Symbol) is { } trackSymPatch)
                    {
                        candidates.Add(trackSymPatch);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                candidates = allPatches
                    .Where(p => p.Symbol.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (candidates.Count == 0) return null;

            int chosenIndex;
            if (preferredVariation.HasValue)
            {
                if (preferredVariation.Value > candidates.Count)
                    chosenIndex = ChooseNonRepeatingIndex($"vox:{name}:{code}", candidates.Count);
                else
                    chosenIndex = Math.Clamp(preferredVariation.Value - 1, 0, candidates.Count - 1);
            }
            else
            {
                chosenIndex = ChooseNonRepeatingIndex($"vox:{name}:{code}", candidates.Count);
            }

            var chosenPatch = candidates[chosenIndex];
            var isLooped = (track?.IsLooped ?? false) || chosenPatch.IsLoop;
            var res = DecodePatch(chosenPatch, "VOX", isLooped);
            if (res != null && preferredVariation.HasValue && preferredVariation.Value <= candidates.Count)
                resolutionCache[$"{name}:{code}:{preferredVariation.Value}"] = res;
            return res;
        }
    }

    private static bool MatchesDemographic(string symbol, string baseName, char code)
    {
        if (!symbol.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = symbol.Substring(baseName.Length).TrimStart('_');
        if (remainder.Length == 0) return false;
        return char.ToLowerInvariant(remainder[0]) == code;
    }

    private readonly Dictionary<int, int> modeTrackIndices = new();

    public HitVm CreateVm(Func<int, int, int>? getSourceDataField = null, Random? rng = null)
    {
        lock (sync)
        {
            return new HitVm(hitBankModules, autoHitLists, hotPatches, getSourceDataField, rng);
        }
    }

    public HitEventMapping? GetEventMapping(string name)
    {
        if (IsMock || string.IsNullOrWhiteSpace(name)) return null;
        lock (sync)
        {
            return fullEventMappings.GetValueOrDefault(name);
        }
    }

    public int? GetMusicMode(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals("bkground_buy1", StringComparison.OrdinalIgnoreCase) || name.Equals("buy", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("bkground_build", StringComparison.OrdinalIgnoreCase) || name.Equals("bkground_camera", StringComparison.OrdinalIgnoreCase) || name.Equals("build", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("bkground_nhood1", StringComparison.OrdinalIgnoreCase) || name.Equals("bkground_nhood", StringComparison.OrdinalIgnoreCase) || name.Equals("nhood", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Equals("bkground_load", StringComparison.OrdinalIgnoreCase) || name.Equals("load", StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Equals("bkground_credits", StringComparison.OrdinalIgnoreCase) || name.Equals("credits", StringComparison.OrdinalIgnoreCase)) return 6;
        if (name.Equals("station_dtnhood", StringComparison.OrdinalIgnoreCase) || name.Equals("station_dtbuy", StringComparison.OrdinalIgnoreCase)) return 10;
        if (name.Equals("station_vacation", StringComparison.OrdinalIgnoreCase)) return 11;
        if (name.Equals("station_unleashed", StringComparison.OrdinalIgnoreCase) || name.Equals("nhoodus", StringComparison.OrdinalIgnoreCase)) return 12;
        if (name.Equals("station_superstar", StringComparison.OrdinalIgnoreCase)) return 13;
        if (name.Equals("music_superstar_transition", StringComparison.OrdinalIgnoreCase)) return 14;
        if (name.Equals("music_magictown", StringComparison.OrdinalIgnoreCase)) return 15;
        if (name.Equals("music_magictown_credits", StringComparison.OrdinalIgnoreCase)) return 16;
        if (name.Equals("music_magictown_build", StringComparison.OrdinalIgnoreCase)) return 17;
        if (name.Equals("music_magictown_buy", StringComparison.OrdinalIgnoreCase)) return 18;
        if (name.Equals("bkground_live", StringComparison.OrdinalIgnoreCase) || name.Equals("live", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("bkground_option", StringComparison.OrdinalIgnoreCase) || name.Equals("bkground_options", StringComparison.OrdinalIgnoreCase)) return 7;
        if (name.Equals("bkground_family", StringComparison.OrdinalIgnoreCase)) return 8;
        if (name.Equals("bkground_fade", StringComparison.OrdinalIgnoreCase)) return 9;
        lock (sync)
        {
            if (eventMappings.TryGetValue(name, out var mapping) && mapping.MusicMode.HasValue)
                return mapping.MusicMode.Value;
        }
        return null;
    }

    public HotTrackDef? GetTrackForEvent(string name)
    {
        if (IsMock || string.IsNullOrWhiteSpace(name)) return null;
        lock (sync)
        {
            if (fullEventMappings.TryGetValue(name, out var mapping))
            {
                if (mapping.Action is HitEventAction.KillInstance or HitEventAction.KillAll or HitEventAction.SndobCancelNote or HitEventAction.SetMusicMode or HitEventAction.TurnOffTv)
                    return null;
                if (mapping.TrackId is { } tid)
                {
                    if (mapping.Bank != null && mapping.Bank.Tracks.TryGetValue(tid, out var bankTrack))
                        return bankTrack;
                    if (hotTracks.TryGetValue(tid, out var globalTrack))
                        return globalTrack;
                }
            }
            return hotTracks.Values.FirstOrDefault(t => t.Symbol.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SoundResolution? ResolvePatchById(
        int patchId,
        string group,
        bool isLoop,
        bool isPositionalObjectTrack,
        float pitchScale = 1.0f,
        float volumeScale = 1.0f,
        int? trackId = null,
        HitBankModule? bank = null)
    {
        if (IsMock) return null;
        lock (sync)
        {
            HotPatchDef? patch = null;
            if (bank != null)
            {
                bank.Patches.TryGetValue(patchId, out patch);
            }
            if (patch == null && trackId.HasValue && hotTracks.TryGetValue(trackId.Value, out var mappedTrack) && mappedTrack.Bank != null)
            {
                mappedTrack.Bank.Patches.TryGetValue(patchId, out patch);
            }
            if (patch == null && !hotPatches.TryGetValue(patchId, out patch))
                return null;

            var normAsset = patch.AssetPath.Replace('\\', '/').TrimStart('/');
            string? diskPath = null;
            if (diskFiles.TryGetValue(normAsset, out var dp) && File.Exists(dp))
            {
                diskPath = dp;
            }
            else if (!string.IsNullOrEmpty(GameDirectory))
            {
                var candidate = Path.Combine(GameDirectory, normAsset.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) diskPath = candidate;
            }

            if (Path.GetExtension(normAsset).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                if (diskPath == null)
                {
                    var foundMp3 = FindAsset(normAsset);
                    if (!foundMp3.HasValue) return null;
                    var destName = $"{Sanitize(patch.Symbol)}_{patchId}.mp3";
                    var destFile = Path.Combine(SessionAudioDirectory, destName);
                    if (!File.Exists(destFile))
                    {
                        try { File.WriteAllBytes(destFile, foundMp3.Value.Archive.Read(foundMp3.Value.Entry).ToArray()); } catch { return null; }
                    }
                    return new SoundResolution("audio/" + destName, group, CalculateMp3Duration(destFile), isLoop, isPositionalObjectTrack, IsPlaylist: false, PitchScale: pitchScale, VolumeScale: volumeScale, TrackId: trackId);
                }
                else
                {
                    var destName = $"{Sanitize(patch.Symbol)}_{patchId}.mp3";
                    var destFile = Path.Combine(SessionAudioDirectory, destName);
                    if (!File.Exists(destFile))
                    {
                        try { File.Copy(diskPath, destFile, true); } catch { return null; }
                    }
                    return new SoundResolution("audio/" + destName, group, CalculateMp3Duration(diskPath), isLoop, isPositionalObjectTrack, IsPlaylist: false, PitchScale: pitchScale, VolumeScale: volumeScale, TrackId: trackId);
                }
            }

            byte[]? data = null;
            var found = FindAsset(patch.AssetPath);
            if (found.HasValue)
                data = found.Value.Archive.Read(found.Value.Entry).ToArray();
            else if (diskPath != null)
                data = File.ReadAllBytes(diskPath);

            if (data == null || data.Length == 0) return null;
            var raw = DecodeRawAudio($"{patch.Symbol}_{patchId}", data, group, isLoop, isPositionalObjectTrack);
            return raw == null
                ? null
                : raw with { PitchScale = pitchScale, VolumeScale = volumeScale, TrackId = trackId };
        }
    }

    private SoundResolution? ResolveMusic(string name, int? pianoSkillLevel = null)
    {
        var mode = GetMusicMode(name);
        if (!mode.HasValue)
        {
            return ResolveStationOrPiano(name, pianoSkillLevel);
        }

        if (mode.Value is 0 or 7 or 8 or 9 || !musicModes.TryGetValue(mode.Value, out var files) || files.Count == 0) return null;

        for (int attempt = 0; attempt < files.Count; attempt++)
        {
            int trackIndex = 0;
            if (modeTrackIndices.TryGetValue(mode.Value, out var currentIdx))
            {
                trackIndex = currentIdx % files.Count;
                modeTrackIndices[mode.Value] = (trackIndex + 1) % files.Count;
            }
            else
            {
                modeTrackIndices[mode.Value] = 1 % files.Count;
            }

            var sourceFile = files[trackIndex];
            try
            {
                if (new FileInfo(sourceFile).Length <= 0) continue;
            }
            catch { continue; }

            var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
            var trackBase = Path.GetFileNameWithoutExtension(sourceFile);

            if (ext == ".wav")
            {
                var destFileName = $"{Sanitize(name)}_{trackIndex}_{Sanitize(trackBase)}.wav";
                var destPath = Path.Combine(SessionAudioDirectory, destFileName);
                if (!File.Exists(destPath))
                {
                    try { File.Copy(sourceFile, destPath, true); } catch { continue; }
                }
                float duration = ReadWavDuration(destPath);
                bool isLoopWav = trackBase.Contains("loop", StringComparison.OrdinalIgnoreCase);
                return new SoundResolution("audio/" + destFileName, "MUSIC", duration, isLoopWav, IsPositionalObjectTrack: false, IsPlaylist: !isLoopWav);
            }
            else
            {
                var destFileName = $"{Sanitize(name)}_{trackIndex}_{Sanitize(trackBase)}.mp3";
                var destPath = Path.Combine(SessionAudioDirectory, destFileName);
                if (!File.Exists(destPath))
                {
                    try { File.Copy(sourceFile, destPath, true); } catch { continue; }
                }

                float duration = CalculateMp3Duration(sourceFile);
                return new SoundResolution("audio/" + destFileName, "MUSIC", duration, false, IsPositionalObjectTrack: false, IsPlaylist: true);
            }
        }
        return null;
    }

    private SoundResolution? ResolveStationOrPiano(string name, int? pianoSkillLevel = null)
    {
        int? trackId = null;
        if (eventMappings.TryGetValue(name, out var mapping) && mapping.TrackId.HasValue)
            trackId = mapping.TrackId.Value;

        string? stationKey = null;
        var group = "MUSIC";

        if (name.Equals("radio_class", StringComparison.OrdinalIgnoreCase) || name.StartsWith("stereo_classical", StringComparison.OrdinalIgnoreCase) || trackId is 111 or 269)
            stationKey = "radio_classical";
        else if (name.Equals("radio_cntry", StringComparison.OrdinalIgnoreCase) || name.StartsWith("stereo_country", StringComparison.OrdinalIgnoreCase) || trackId is 110 or 260)
            stationKey = "radio_country";
        else if (name.Equals("radio_latin", StringComparison.OrdinalIgnoreCase) || name.StartsWith("stereo_jazz", StringComparison.OrdinalIgnoreCase) || trackId is 108 or 270)
            stationKey = "radio_latin";
        else if (name.Equals("radio_rock", StringComparison.OrdinalIgnoreCase) || name.StartsWith("stereo_rock", StringComparison.OrdinalIgnoreCase) || trackId is 109 or 280)
            stationKey = "radio_rock";
        else if (name.Equals("station_ezcheap", StringComparison.OrdinalIgnoreCase) || trackId == 65536)
            stationKey = "station_ezcheap";
        else if (name.Equals("station_ezexp", StringComparison.OrdinalIgnoreCase) || trackId == 65537)
            stationKey = "station_ezexp";
        else if (name.StartsWith("djbooth_countrydance", StringComparison.OrdinalIgnoreCase) || name.Equals("station_countrydance", StringComparison.OrdinalIgnoreCase) || trackId == 6681)
            stationKey = "radio_countrydance";
        else if (name.StartsWith("djbooth_rap", StringComparison.OrdinalIgnoreCase) || name.Equals("station_rap", StringComparison.OrdinalIgnoreCase) || trackId == 6682)
            stationKey = "radio_rap";
        else if (name.StartsWith("djbooth_rave", StringComparison.OrdinalIgnoreCase) || name.Equals("station_rave", StringComparison.OrdinalIgnoreCase) || trackId == 6683)
            stationKey = "radio_rave";
        else if (name.StartsWith("djbooth_disco", StringComparison.OrdinalIgnoreCase) || name.Equals("station_disco", StringComparison.OrdinalIgnoreCase) || trackId == 6684)
            stationKey = "radio_disco";
        else if (name.StartsWith("djbooth_beach", StringComparison.OrdinalIgnoreCase) || name.Equals("station_beach", StringComparison.OrdinalIgnoreCase) || trackId == 6685)
            stationKey = "radio_beach";
        else if (name.Equals("stereo_zydeco", StringComparison.OrdinalIgnoreCase) || name.Equals("speakers_unl_cheap", StringComparison.OrdinalIgnoreCase) || trackId == 22900)
            stationKey = "station_unlcheap";
        else if (name.Equals("stereo_unlspeakers", StringComparison.OrdinalIgnoreCase) || name.Equals("speakers_unl_exp", StringComparison.OrdinalIgnoreCase) || name.Equals("stereo_unleashedui", StringComparison.OrdinalIgnoreCase) || trackId is 22901 or 22902)
            stationKey = "station_unlspeak";
        else if (name.Equals("stereo_superstarspeaker", StringComparison.OrdinalIgnoreCase) || trackId == 100100)
            stationKey = "stereo_superstar";
        else if (name.Equals("tv_action_norm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_action_", StringComparison.OrdinalIgnoreCase) || trackId is 100 or 28)
        {
            stationKey = "tv_action";
            group = "FX";
        }
        else if (name.Equals("tv_horror_norm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_horror_", StringComparison.OrdinalIgnoreCase) || trackId == 102)
        {
            stationKey = "tv_horror";
            group = "FX";
        }
        else if (name.Equals("tv_cartoon_norm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_cartoon_", StringComparison.OrdinalIgnoreCase) || trackId is 104 or 29)
        {
            stationKey = "tv_cartoon";
            group = "FX";
        }
        else if (name.Equals("tv_romance_norm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_romance_", StringComparison.OrdinalIgnoreCase) || trackId is 106 or 26)
        {
            stationKey = "tv_romance";
            group = "FX";
        }
        else if (name.Equals("tv_mystery_norm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_mystery_", StringComparison.OrdinalIgnoreCase) || trackId is 107 or 27)
        {
            stationKey = "tv_mystery";
            group = "FX";
        }
        else if (name.Equals("tv_superstar_animal", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_superstar_animal", StringComparison.OrdinalIgnoreCase) || trackId == 103000)
        {
            stationKey = "tv_superstar_animal";
            group = "FX";
        }
        else if (name.Equals("tv_superstar_entertainment", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_superstar_entertainment", StringComparison.OrdinalIgnoreCase) || trackId == 103001)
        {
            stationKey = "tv_superstar_entertainment";
            group = "FX";
        }
        else if (name.Equals("tv_superstar_news", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_superstar_news", StringComparison.OrdinalIgnoreCase) || trackId == 103002)
        {
            stationKey = "tv_superstar_news";
            group = "FX";
        }
        else if (name.Equals("tv_superstar_sports", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tv_superstar_sports", StringComparison.OrdinalIgnoreCase) || trackId == 103003)
        {
            stationKey = "tv_superstar_sports";
            group = "FX";
        }

        if (stationKey != null && stationDirectories.TryGetValue(stationKey, out var files) && files.Count > 0)
        {
            multiVariationEvents.Add(name);
            for (int attempt = 0; attempt < files.Count; attempt++)
            {
                int trackIndex = 0;
                if (stationTrackIndices.TryGetValue(stationKey, out var currentIdx))
                {
                    trackIndex = currentIdx % files.Count;
                    stationTrackIndices[stationKey] = (trackIndex + 1) % files.Count;
                }
                else
                {
                    stationTrackIndices[stationKey] = 1 % files.Count;
                }

                var sourceFile = files[trackIndex];
                try
                {
                    if (new FileInfo(sourceFile).Length <= 0) continue;
                }
                catch { continue; }

                var ext = Path.GetExtension(sourceFile);
                if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    var trackBase = Path.GetFileNameWithoutExtension(sourceFile);
                    var destFileName = $"{Sanitize(stationKey)}_{trackIndex}_{Sanitize(trackBase)}.mp3";
                    var destPath = Path.Combine(SessionAudioDirectory, destFileName);
                    if (!File.Exists(destPath))
                    {
                        try { File.Copy(sourceFile, destPath, true); } catch { continue; }
                    }
                    float duration = CalculateMp3Duration(sourceFile);
                    return new SoundResolution("audio/" + destFileName, group, duration, false, IsPositionalObjectTrack: true, IsPlaylist: true);
                }
                else
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(sourceFile);
                        var trackBase = Path.GetFileNameWithoutExtension(sourceFile);
                        var raw = DecodeRawAudio($"{stationKey}_{trackIndex}_{trackBase}", bytes, group, false);
                        if (raw != null) return raw with { IsPlaylist = true };
                    }
                    catch { }
                }
            }
        }

        if (name.Equals("play_piano", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("piano_play", StringComparison.OrdinalIgnoreCase) ||
            trackId == 1906)
        {
            multiVariationEvents.Add(name);
            var pianoPatches = new List<PatchInfo>();
            if (pianoSkillLevel is { } skill)
            {
                var skillHitId = 1210 + Math.Clamp(skill, 0, 10);
                if (hitLists.TryGetValue(skillHitId, out var skillPatchIds))
                {
                    foreach (var pid in skillPatchIds)
                    {
                        if (patches.TryGetValue(pid, out var p)) pianoPatches.Add(p);
                    }
                }
            }
            if (pianoPatches.Count == 0)
            {
                for (int hitId = 1210; hitId <= 1220; hitId++)
                {
                    if (hitLists.TryGetValue(hitId, out var pIds))
                    {
                        foreach (var pid in pIds)
                        {
                            if (patches.TryGetValue(pid, out var p)) pianoPatches.Add(p);
                        }
                    }
                }
            }
            if (pianoPatches.Count == 0)
            {
                pianoPatches = allPatches
                    .Where(p => p.Symbol.StartsWith("PianoL", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.PatchId)
                    .ToList();
            }
            if (pianoPatches.Count > 0)
            {
                var key = pianoSkillLevel is { } level ? $"piano:play:{Math.Clamp(level, 0, 10)}" : "piano:play";
                var chosen = pianoPatches[ChooseNonRepeatingIndex(key, pianoPatches.Count)];
                return DecodePatch(chosen, "MUSIC", true);
            }
        }

        return null;
    }

    private static float CalculateMp3Duration(string filePath)
    {
        try
        {
            var bitrates = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            using var stream = File.OpenRead(filePath);
            var id3Header = new byte[10];
            if (stream.Read(id3Header, 0, 10) == 10 &&
                id3Header[0] == (byte)'I' && id3Header[1] == (byte)'D' && id3Header[2] == (byte)'3')
            {
                int tagSize = ((id3Header[6] & 0x7F) << 21) |
                              ((id3Header[7] & 0x7F) << 14) |
                              ((id3Header[8] & 0x7F) << 7) |
                              (id3Header[9] & 0x7F);
                stream.Position = Math.Min(stream.Length, 10L + tagSize);
            }
            else
            {
                stream.Position = 0;
            }

            var header = new byte[2048];
            int read = stream.Read(header, 0, header.Length);
            for (int i = 0; i < read - 4; i++)
            {
                if (header[i] == 0xFF && (header[i + 1] & 0xFE) == 0xFA)
                {
                    int brIdx = (header[i + 2] >> 4) & 0x0F;
                    int br = bitrates[brIdx] * 1000;
                    if (br > 0)
                    {
                        var len = stream.Length - stream.Position + read;
                        return Math.Clamp((float)(Math.Max(1, len) * 8.0 / br), 10.0f, 600.0f);
                    }
                }
            }
        }
        catch { }
        return 180.0f;
    }

    private readonly Dictionary<string, FscSequence> fscCache = new(StringComparer.OrdinalIgnoreCase);

    public FscSequence? ResolveAmbienceSequence(string name)
    {
        if (IsMock || string.IsNullOrWhiteSpace(name) || !name.StartsWith("setambience_", StringComparison.OrdinalIgnoreCase))
            return null;

        string fscPath = name.ToLowerInvariant() switch
        {
            "setambience_beach" => "Beach/beach.fsc",
            "setambience_park" or "setambience_downtown" => "Park/hotdatepark_amb.fsc",
            "setambience_magictown" => "MTOutdoors/magic_amb.fsc",
            _ => "Outdoors/sim_amb.fsc"
        };

        lock (sync)
        {
            if (fscCache.TryGetValue(fscPath, out var cached)) return cached;
            var entry = FindAsset(fscPath);
            if (entry.HasValue)
            {
                try
                {
                    var bytes = entry.Value.Archive.Read(entry.Value.Entry).ToArray();
                    var seq = FscSequence.Read(bytes, fscPath);
                    if (seq.Notes.Count > 0 && seq.Tempo > 0)
                    {
                        fscCache[fscPath] = seq;
                        return seq;
                    }
                }
                catch { }
            }
            return null;
        }
    }

    public SoundResolution? ResolveAmbienceNote(string filename, bool isLoop)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;
        lock (sync)
        {
            var noteRes = ResolveDirect(filename);
            if (noteRes == null) return null;
            return new SoundResolution(noteRes.RelativePath, "AMBIENCE", noteRes.Duration, isLoop, IsPositionalObjectTrack: false);
        }
    }

    private SoundResolution? ResolveAmbience(string name)
    {
        if (!name.StartsWith("setambience_", StringComparison.OrdinalIgnoreCase)) return null;

        var seq = ResolveAmbienceSequence(name);
        if (seq != null)
        {
            foreach (var preferLoop in new[] { true, false })
            {
                foreach (var note in seq.Notes)
                {
                    if (preferLoop && (!note.Loop2 || note.Prob == 0)) continue;
                    if (!string.IsNullOrWhiteSpace(note.Filename) && !note.Filename.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        var noteRes = ResolveDirect(note.Filename);
                        if (noteRes != null)
                        {
                            return new SoundResolution(noteRes.RelativePath, "AMBIENCE", noteRes.Duration, note.Loop2, IsPositionalObjectTrack: false);
                        }
                    }
                }
            }
        }

        return new SoundResolution("audio/" + Sanitize(name) + ".wav", "AMBIENCE", 30.0f, true, IsPositionalObjectTrack: false);
    }

    private SoundResolution? ResolveHotEvent(string name)
    {
        var track = ResolveMappedTrack(name);

        PatchInfo? patch = null;
        if (track != null)
        {
            if (track.LoopPatchId is > 0)
            {
                patch = GetPatchForTrack(track, track.LoopPatchId.Value);
            }
            if (patch == null && track.PatchId > 1)
            {
                patch = GetPatchForTrack(track, track.PatchId);
            }

            var lookupId = track.LoopPatchId ?? (track.PatchId > 1 ? track.PatchId : (track.Reg5 ?? track.Reg6 ?? track.Reg7 ?? track.PatchId));
            if (patch == null && lookupId > 0 && !name.StartsWith("vox_", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetHitListForTrack(track, lookupId, out var listIds) && listIds.Count > 0)
                {
                    var listPatches = new List<PatchInfo>();
                    foreach (var pid in listIds)
                    {
                        if (GetPatchForTrack(track, pid) is { } p) listPatches.Add(p);
                        else if (GetSubTrackForTrack(track, pid) is { } subTrack && subTrack.PatchId > 1 && GetPatchForTrack(subTrack, subTrack.PatchId) is { } tp)
                            listPatches.Add(tp);
                    }
                    if (listPatches.Count > 1) multiVariationEvents.Add(name);
                    if (listPatches.Count > 0)
                        patch = listPatches[ChooseNonRepeatingIndex($"fx:{name}", listPatches.Count)];
                }
                patch ??= GetPatchForTrack(track, lookupId);
            }
            patch ??= GetPatchBySymbolForTrack(track, track.Symbol);
        }

        patch ??= patchesBySymbol.GetValueOrDefault(name);

        if (patch == null)
        {
            // Match voice prefix or suffix patterns for demographic voice variants.
            if (name.StartsWith("vox", StringComparison.OrdinalIgnoreCase) || name.EndsWith("vox", StringComparison.OrdinalIgnoreCase))
            {
                patch = allPatches.FirstOrDefault(p => p.Symbol.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            }

            if (patch == null && (name.Contains("mourn", StringComparison.OrdinalIgnoreCase) || (track?.Symbol.Contains("mourn", StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                patch = patchesBySymbol.GetValueOrDefault("vox_cry_hardf1") ??
                        patchesBySymbol.GetValueOrDefault("vox_cry_hardm1") ??
                        allPatches.FirstOrDefault(p => p.Symbol.Contains("cry_hard", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (patch != null)
        {
            var isVoxGroup = name.StartsWith("vox", StringComparison.OrdinalIgnoreCase) || name.EndsWith("vox", StringComparison.OrdinalIgnoreCase);
            var group = track?.Group ?? (isVoxGroup ? "VOX" : "FX");
            var isPositional = track?.IsPositionalObjectTrack ?? !group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase);
            var res = DecodePatch(patch, group, (track?.IsLooped ?? false) || patch.IsLoop, isPositional);
            if (res != null) return res;
        }

        return null;
    }

    private SoundResolution? ResolveDirect(string name)
    {
        var found = FindAsset(name);
        if (!found.HasValue)
        {
            found = FindAsset(name + ".xa") ?? FindAsset(name + ".wav") ?? FindAsset(name + ".utk");
        }

        if (found.HasValue)
        {
            var data = found.Value.Archive.Read(found.Value.Entry).Span;
            var group = name.StartsWith("vox", StringComparison.OrdinalIgnoreCase) || name.EndsWith("vox", StringComparison.OrdinalIgnoreCase) ? "VOX" : "FX";
            var loop = IsDirectAssetNameLooped(name);
            return DecodeRawAudio(name, data, group, loop);
        }

        return null;
    }

    private static bool IsDirectAssetNameLooped(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith("loop", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_lp", StringComparison.OrdinalIgnoreCase);
    }

    private (FarArchive Archive, FarArchive.Entry Entry)? FindAsset(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        if (farEntries.TryGetValue(norm, out var entry)) return entry;

        var nameOnly = Path.GetFileName(norm);
        if (farEntries.TryGetValue(nameOnly, out entry)) return entry;

        foreach (var prefix in StrippedArchivePrefixes)
        {
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = norm.Substring(prefix.Length);
                if (farEntries.TryGetValue(stripped, out entry)) return entry;
            }
        }

        if (norm.StartsWith("downloads/", StringComparison.OrdinalIgnoreCase))
        {
            var afterDownloads = norm.Substring("downloads/".Length);
            if (farEntries.TryGetValue(afterDownloads, out entry)) return entry;
            var slashIdx = afterDownloads.IndexOf('/');
            if (slashIdx >= 0 && slashIdx + 1 < afterDownloads.Length)
            {
                var afterSubfolder = afterDownloads.Substring(slashIdx + 1);
                if (farEntries.TryGetValue(afterSubfolder, out entry)) return entry;
            }
        }

        return null;
    }

    private SoundResolution? DecodePatch(PatchInfo patch, string group, bool isLoop, bool isPositionalObjectTrack = true)
    {
        byte[]? data = null;
        var found = FindAsset(patch.AssetPath);
        if (found.HasValue)
        {
            data = found.Value.Archive.Read(found.Value.Entry).ToArray();
        }
        else if (diskFiles.TryGetValue(patch.AssetPath.Replace('\\', '/'), out var diskPath) && File.Exists(diskPath))
        {
            data = File.ReadAllBytes(diskPath);
        }

        if (data == null || data.Length == 0) return null;

        return DecodeRawAudio(patch.Symbol, data, group, isLoop, isPositionalObjectTrack);
    }

    private SoundResolution? DecodeRawAudio(string name, ReadOnlySpan<byte> data, string group, bool isLoop, bool isPositionalObjectTrack = true)
    {
        var destFileName = Sanitize(name) + ".wav";
        var destPath = Path.Combine(SessionAudioDirectory, destFileName);

        if (File.Exists(destPath))
        {
            float cachedDuration = ReadWavDuration(destPath);
            return new SoundResolution("audio/" + destFileName, group, cachedDuration, isLoop, isPositionalObjectTrack);
        }

        if (XaAudio.IsXa(data))
        {
            try
            {
                var (wav, _, _, _, duration) = XaAudio.Decode(data);
                File.WriteAllBytes(destPath, wav);
                return new SoundResolution("audio/" + destFileName, group, duration, isLoop, isPositionalObjectTrack);
            }
            catch
            {
                return null;
            }
        }

        if (UtkAudio.IsUtk(data))
        {
            try
            {
                var (wav, _, _, _, duration) = UtkAudio.Decode(data);
                File.WriteAllBytes(destPath, wav);
                return new SoundResolution("audio/" + destFileName, group, duration, isLoop, isPositionalObjectTrack);
            }
            catch
            {
                return null;
            }
        }

        if (data.Length >= 44 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F')
        {
            File.WriteAllBytes(destPath, data.ToArray());
            float duration = ReadWavDuration(destPath);
            return new SoundResolution("audio/" + destFileName, group, duration, isLoop, isPositionalObjectTrack);
        }

        return null;
    }

    private static float ReadWavDuration(string wavPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(wavPath);
            if (bytes.Length >= 44)
            {
                var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
                var channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2));
                var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34, 2));
                var dataLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
                int bytesPerFrame = channels * (bitsPerSample / 8);
                if (sampleRate > 0 && bytesPerFrame > 0)
                {
                    int frames = dataLen / bytesPerFrame;
                    return (float)frames / sampleRate;
                }
            }
        }
        catch { }
        return 1.0f;
    }

    private SoundResolution CreateMockResolution(string name)
    {
        var destFileName = Sanitize(name) + ".wav";
        var destPath = Path.Combine(SessionAudioDirectory, destFileName);
        var isMusic = name.Contains("bkground", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("music", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("station", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("radio_", StringComparison.OrdinalIgnoreCase) ||
                      name.StartsWith("stereo_", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("play_piano", StringComparison.OrdinalIgnoreCase) ||
                      name.Equals("piano_play", StringComparison.OrdinalIgnoreCase);
        var isAmbience = name.StartsWith("setambience", StringComparison.OrdinalIgnoreCase);
        var isVox = name.StartsWith("vox", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("vox", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("talk", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("speech", StringComparison.OrdinalIgnoreCase);
        var isSting = name.StartsWith("sting", StringComparison.OrdinalIgnoreCase);
        var group = isVox ? "VOX" : ((isMusic || isSting) ? "MUSIC" : (isAmbience ? "AMBIENCE" : "FX"));
        var loop = (isMusic && !isSting) || isAmbience || name.Contains("loop", StringComparison.OrdinalIgnoreCase);
        var isPositional = !isAmbience && !isSting && !name.Contains("bkground", StringComparison.OrdinalIgnoreCase);
        float duration = loop ? 30.0f : 1.0f;

        if (!File.Exists(destPath))
        {
            try
            {
                int sampleRate = 22050;
                int frameCount = (int)(sampleRate * duration);
                var pcm = new byte[frameCount * 2];
                var wav = XaAudio.CreateRiffWav(sampleRate, 1, 16, pcm);
                File.WriteAllBytes(destPath, wav);
            }
            catch { }
        }

        return new SoundResolution("audio/" + destFileName, group, duration, loop, isPositional);
    }

    private static string Sanitize(string name)
    {
        var sanitized = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_')).ToLowerInvariant();
        return string.IsNullOrEmpty(sanitized) ? "sound" : sanitized;
    }

    public void Dispose()
    {
        archives.Clear();
        farEntries.Clear();
        diskFiles.Clear();
        resolutionCache.Clear();
    }
}
