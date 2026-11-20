using System.Buffers.Binary;
using System.Text.Json;
using SimsNative.Plugins;
using SimsEngine.Simulation;
using SimsEngine.Formats;

internal sealed class HitAudioRuntime : IDisposable
{
    public const int NeighborhoodOwnerID = -1;
    public const int MaxVoices = 64;
    private static readonly bool LogAudio = Environment.GetEnvironmentVariable("SIMS_LOG_AUDIO") == "1";

    private readonly string directory;
    private readonly string profile;
    private readonly string? gameDirectory;
    private readonly Action<object> write;
    private readonly object sync = new();
    private readonly SoundBank soundBank;
    private readonly HitVm? hitVm;

    private readonly Dictionary<string, float> volumes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FX"] = 0.8f,
        ["VOX"] = 0.8f,
        ["MUSIC"] = 0.8f,
        ["AMBIENCE"] = 1.0f
    };

    internal sealed class VoiceInstance
    {
        public required long VoiceId { get; init; }
        public required string Name { get; init; }
        public required string File { get; init; }
        public required string Group { get; init; }
        public required float Duration { get; init; }
        public required bool IsLoop { get; init; }
        public bool IsPlaylist { get; init; }
        public bool ManagedByHitVm { get; init; }
        public int HitThreadId { get; init; }
        public bool IsModeMusic { get; init; }
        public required int OwnerId { get; init; }
        public int SourceOwnerId { get; init; }
        public required bool Spatial { get; init; }
        public required bool Zoom { get; init; }
        public float Volume { get; set; } = 1.0f;
        public float BaseScale { get; set; } = 1.0f;
        public float Pan { get; set; }
        public float PitchScale { get; set; } = 1.0f;
        public ushort AmbienceGroupId { get; init; }
        public bool PausedBySimulation { get; set; }
        public DateTime? PausedAt { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
    }

    private readonly Dictionary<long, VoiceInstance> activeVoices = [];
    private readonly Dictionary<int, List<long>> ownerVoices = [];
    private readonly Dictionary<int, DateTime> lastCarHonkByOwner = [];
    private readonly Dictionary<int, long> threadToVoice = [];
    private readonly Dictionary<long, int> voiceToThread = [];
    private readonly Dictionary<int, (int OwnerId, bool Spatial, bool Zoom)> pendingThreadContext = [];
    private readonly Dictionary<int, (int ObjId, char VoiceCode)> threadDemographicOverrides = [];
    private long sequence;
    private bool paused;
    private bool simulationPaused;
    private bool musicModeActive;
    private AmbienceSequencer? ambience;
    private SimulationWorld? lastWorld;
    private DateTime lastVmTick = DateTime.UtcNow;
    private DateTime? globalPausedAt;
    private int lastVmDuckPriority;

    public bool Enabled { get; private set; } = true;
    public bool SuppressActiveDuplicates { get; set; }
    public int SimulationSpeed { get; private set; } = 1;
    public int ActiveVoiceCount { get { lock (sync) { return activeVoices.Count; } } }

    public HitAudioRuntime(string directory, string profile, Action<object> write, string? game = null)
    {
        this.directory = directory;
        this.profile = profile;
        this.gameDirectory = game;
        this.write = write;
        soundBank = new SoundBank(game, directory);
        try { Directory.CreateDirectory(directory); } catch { }
        LoadPersistedVolumes();
        if (!soundBank.IsMock)
        {
            hitVm = soundBank.CreateVm(GetSourceDataField);
            hitVm.ThreadStarted += OnHitThreadStarted;
            hitVm.ThreadKilled += OnHitThreadKilled;
        }
    }

    private void OnHitThreadStarted(HitVm.TrackThread child, HitVm.TrackThread? parent)
    {
        if (parent == null) return;
        if (pendingThreadContext.TryGetValue(parent.InstanceId, out var ctx))
        {
            pendingThreadContext[child.InstanceId] = ctx;
        }
        if (threadDemographicOverrides.TryGetValue(parent.InstanceId, out var ov))
        {
            threadDemographicOverrides[child.InstanceId] = ov;
        }
    }

    private void OnHitThreadKilled(int threadId)
    {
        pendingThreadContext.Remove(threadId);
        threadDemographicOverrides.Remove(threadId);
    }

    public static float AttenuationGain(float distance, float minDist = 5.0f, float rolloff = 1.0f, float maxDist = 40.0f)
    {
        if (distance <= minDist) return 1.0f;
        if (distance >= maxDist) return 0.0f;
        return minDist / (minDist + rolloff * (distance - minDist));
    }

    public static byte[] CreateWav(int sampleRate, short channels, short bitsPerSample, ReadOnlySpan<byte> pcmData) =>
        XaAudio.CreateRiffWav(sampleRate, channels, bitsPerSample, pcmData);

    private void StealVoiceIfFull()
    {
        if (activeVoices.Count >= MaxVoices)
        {
            var victim = activeVoices.Values
                .OrderBy(v => v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && v.OwnerId <= 0 ? 2 :
                              (v.IsLoop || v.Name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                .ThenBy(v => v.StartTime)
                .FirstOrDefault();
            if (victim != null)
            {
                StopVoice(victim.VoiceId);
            }
        }
    }

    private void StopVoice(long voiceId, bool killHitThread = true)
    {
        if (voiceToThread.Remove(voiceId, out var threadId))
        {
            if (threadToVoice.TryGetValue(threadId, out var mappedVoice) && mappedVoice == voiceId)
            {
                threadToVoice.Remove(threadId);
            }
            if (killHitThread)
            {
                hitVm?.KillThread(threadId);
            }
        }
        if (activeVoices.Remove(voiceId, out var voice))
        {
            if (ownerVoices.TryGetValue(voice.OwnerId, out var list))
            {
                list.Remove(voiceId);
                if (list.Count == 0) ownerVoices.Remove(voice.OwnerId);
            }
            write(new
            {
                type = "audio",
                command = "stop",
                voice = voiceId
            });
        }
    }

    private bool IsModeMusicEvent(string name, string group, bool isPositional) =>
        soundBank.GetMusicMode(name) != null ||
        (soundBank.IsMock &&
         group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) &&
         !isPositional &&
         !name.StartsWith("sting", StringComparison.OrdinalIgnoreCase));

    private void StopNonPositionalMusic()
    {
        foreach (var existing in activeVoices.Values
            .Where(v => v.IsModeMusic)
            .Select(v => v.VoiceId)
            .ToArray())
        {
            StopVoice(existing);
        }
    }

    public static string LotAmbienceEvent(short house)
    {
        if (house is 28 or 29 or (>= 46 and <= 48)) return "setambience_beach";
        if (house is (>= 21 and <= 30) or (>= 43 and <= 45)) return "setambience_park";
        if (house is >= 90 and <= 92) return "setambience_magictown";
        return "setambience_backyard";
    }

    public bool PlayLotAmbience(short house) => Play(LotAmbienceEvent(house));

    public bool Play(string name, int? ownerID = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(name)) return false;
        lock (sync)
        {
            if (!soundBank.IsMock)
            {
                if (HandleSpecialControlEventLocked(name, ownerID))
                    return true;
            }

            if (soundBank.GetKilledTrackSymbol(name) is { } killedSymbol)
            {
                StopVoicesByCueName(killedSymbol);
                return true;
            }

            if (!soundBank.IsMock &&
                (ownerID == null || ownerID.Value <= 0) &&
                name.StartsWith("setambience_", StringComparison.OrdinalIgnoreCase))
            {
                if (StartAmbience(name, ownerID ?? NeighborhoodOwnerID))
                    return true;
            }

            if (!soundBank.IsMock && hitVm != null &&
                soundBank.GetMusicMode(name) is null &&
                !name.Equals("play_piano", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("piano_play", StringComparison.OrdinalIgnoreCase) &&
                soundBank.GetTrackForEvent(name) is { } hotTrack)
            {
                var argOwner = ownerID ?? 0;
                var checkOwner = ownerID is < 0 ? ownerID.Value : (!hotTrack.IsPositionalObjectTrack ? NeighborhoodOwnerID : (ownerID ?? NeighborhoodOwnerID));
                if (!hotTrack.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && checkOwner != 0 &&
                    ownerVoices.TryGetValue(checkOwner, out var existingLoopedIds) &&
                    existingLoopedIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                var isSpatialHint = hotTrack.IsPositionalObjectTrack && ((ownerID.HasValue && ownerID.Value > 0) || hotTrack.Group == "VOX");
                if (StartHitTrackLocked(hotTrack, name, argOwner, checkOwner, isSpatialHint, isSpatialHint, null))
                    return true;
                if (hotTrack.Group == "VOX")
                    return false;
            }

            var res = soundBank.Resolve(name);
            if (res == null) return false;

            var isVox = soundBank.IsDemographicVoxEvent(name);
            var group = isVox ? "VOX" : res.Group;
            var isModeMusic = IsModeMusicEvent(name, group, res.IsPositionalObjectTrack);
            if (isModeMusic)
            {
                StopNonPositionalMusic();
            }
            var isSpatial = res.IsPositionalObjectTrack && ((ownerID.HasValue && ownerID.Value > 0) || isVox);
            var owner = ownerID is < 0 ? ownerID.Value : (!res.IsPositionalObjectTrack ? NeighborhoodOwnerID : (ownerID ?? NeighborhoodOwnerID));

            if (res.IsLoop && !group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && owner != 0 &&
                ownerVoices.TryGetValue(owner, out var existingIds) &&
                existingIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            EmitNewVoiceLocked(
                name,
                res.RelativePath,
                group,
                res.Duration,
                res.IsLoop,
                res.IsPlaylist,
                owner,
                isSpatial,
                isSpatial,
                res.VolumeScale,
                0f,
                res.PitchScale,
                managedByHitVm: false,
                hitThreadId: 0,
                isModeMusic: isModeMusic);
            return true;
        }
    }

    private readonly Dictionary<short, (string LastEvent, int LastVariation, DateTime LastTime)> speakerVoiceHistory = new();
    private readonly Dictionary<short, char> speakerDemographics = new();

    public void SetSpeakerDemographic(short speakerId, char voiceCode)
    {
        lock (sync) { speakerDemographics[speakerId] = voiceCode; }
    }

    public bool Play(string name, string? cue, int? ownerID = null) => Play(name, ownerID);

    public bool IsDemographicVoxEvent(string name) => soundBank.IsDemographicVoxEvent(name);

    private void StopVoicesByCueName(string cueName)
    {
        foreach (var voiceId in activeVoices.Values
            .Where(v => v.Name.Equals(cueName, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.VoiceId)
            .ToArray())
        {
            StopVoice(voiceId);
        }
    }

    public bool PlayForSimulationObject(string name, short ownerID, bool pan, bool zoom, char? voiceTypeOverride = null, int? pianoSkillLevel = null)
    {
        if (!Enabled || ownerID < 0 || string.IsNullOrWhiteSpace(name)) return false;
        lock (sync)
        {
            if (!soundBank.IsMock && HandleSpecialControlEventLocked(name, ownerID))
                return true;

            if (soundBank.GetKilledTrackSymbol(name) is { } killedSymbol)
            {
                StopVoicesByCueName(killedSymbol);
                return true;
            }

            var now = DateTime.UtcNow;
            if (SuppressActiveDuplicates)
            {
                foreach (var active in activeVoices.Values.ToArray())
                {
                    if (!active.IsLoop && !active.IsPlaylist && !active.ManagedByHitVm && !paused && !active.PausedBySimulation)
                    {
                        var effectiveDuration = active.Duration / Math.Clamp(active.PitchScale, 0.0625f, 4.0f);
                        if ((now - active.StartTime).TotalSeconds >= effectiveDuration)
                            StopVoice(active.VoiceId);
                    }
                }
            }

            var isVox = soundBank.IsDemographicVoxEvent(name);
            if (isVox && ownerID <= 0) return false;

            if (SuppressActiveDuplicates)
            {
                if (ownerID > 0)
                {
                    if (ownerVoices.TryGetValue(ownerID, out var activeForOwner) && activeForOwner.Count > 0)
                    {
                        foreach (var vid in activeForOwner)
                        {
                            if (!activeVoices.TryGetValue(vid, out var existing)) continue;
                            if (existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                return true;
                            if (isVox && existing.Group.Equals("VOX", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }

                    foreach (var existing in activeVoices.Values)
                    {
                        if (existing.SourceOwnerId == ownerID && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                if (SimulationSpeed > 1 && !isVox)
                {
                    foreach (var existing in activeVoices.Values)
                    {
                        if ((existing.OwnerId > 0 || existing.SourceOwnerId > 0) &&
                            !existing.Group.Equals("VOX", StringComparison.OrdinalIgnoreCase) &&
                            existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    if (ownerID > 0 && IsCarHonkEvent(name) &&
                        lastCarHonkByOwner.TryGetValue(ownerID, out var lastHonk))
                    {
                        var cooldownSeconds = SimulationSpeed >= 10 ? 8.0 : 5.0;
                        if ((now - lastHonk).TotalSeconds < cooldownSeconds)
                            return true;
                    }
                }
            }

            if (!soundBank.IsMock && hitVm != null &&
                soundBank.GetMusicMode(name) is null &&
                !name.Equals("play_piano", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("piano_play", StringComparison.OrdinalIgnoreCase) &&
                soundBank.GetTrackForEvent(name) is { } hotTrack)
            {
                var trackPositional = isVox || hotTrack.IsPositionalObjectTrack;
                if (trackPositional && ownerID <= 0) return false;
                var trackOwner = trackPositional ? (int)ownerID : NeighborhoodOwnerID;

                if (trackPositional &&
                    ownerVoices.TryGetValue(ownerID, out var existingIds) &&
                    existingIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (StartHitTrackLocked(hotTrack, name, ownerID, trackOwner, trackPositional && pan, trackPositional && zoom, voiceTypeOverride))
                {
                    if (isVox)
                    {
                        speakerVoiceHistory[ownerID] = (name, 1, now);
                    }
                    return true;
                }
                if (isVox) return false;
            }

            SoundResolution? res = null;
            if (isVox)
            {
                var voiceCode = voiceTypeOverride ?? speakerDemographics.GetValueOrDefault(ownerID, 'm');
                int? variation = soundBank.IsMock ? 1 : null;

                if (speakerVoiceHistory.TryGetValue(ownerID, out var history))
                {
                    if (history.LastEvent.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        (now - history.LastTime).TotalSeconds < 3.0)
                    {
                        if (soundBank.IsMock)
                            variation = history.LastVariation == 1 ? 2 : 1;
                    }
                }

                res = soundBank.ResolveVoice(name, voiceCode, variation);
                if (res != null)
                {
                    speakerVoiceHistory[ownerID] = (name, variation ?? 1, now);
                }
            }
            else
            {
                res = soundBank.Resolve(name, pianoSkillLevel);
            }

            if (res == null) return false;

            var group = isVox ? "VOX" : res.Group;
            var isPositional = isVox || res.IsPositionalObjectTrack;
            if (isPositional && ownerID <= 0) return false;
            var isModeMusic = IsModeMusicEvent(name, group, isPositional);

            var loop = res.IsLoop ||
                       (soundBank.IsMock && isPositional && name.Contains("loop", StringComparison.OrdinalIgnoreCase));

            var effectiveOwner = isPositional ? (int)ownerID : NeighborhoodOwnerID;

            if (isPositional)
            {
                if (ownerVoices.TryGetValue(ownerID, out var existingIds) && existingIds.Count > 0)
                {
                    if ((loop || res.IsPlaylist) && existingIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    if (!soundBank.IsMock && (loop || res.IsPlaylist) && group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var oldVid in existingIds
                            .Where(vid => activeVoices.TryGetValue(vid, out var v) && (v.IsLoop || v.IsPlaylist) && v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase))
                            .ToArray())
                        {
                            StopVoice(oldVid);
                        }
                    }
                }
            }
            else if (isModeMusic)
            {
                StopNonPositionalMusic();
            }
            else if (loop && effectiveOwner != 0 && ownerVoices.TryGetValue(effectiveOwner, out var globalIds))
            {
                if (globalIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            var effectiveSpatial = isPositional && pan;
            var effectiveZoom = isPositional && zoom;

            EmitNewVoiceLocked(
                name,
                res.RelativePath,
                group,
                res.Duration,
                loop,
                res.IsPlaylist,
                effectiveOwner,
                effectiveSpatial,
                effectiveZoom,
                res.VolumeScale,
                0f,
                res.PitchScale,
                managedByHitVm: false,
                hitThreadId: 0,
                isModeMusic: isModeMusic,
                sourceOwnerId: ownerID);
            return true;
        }
    }

    public void PlayResource(string path, int ownerID, string cue, bool loop)
    {
        if (!Enabled || ownerID <= 0) return;
        PlayForSimulationObject(cue, (short)ownerID, true, true);
    }

    public void StopResource(int ownerID, string cue)
    {
        if (string.IsNullOrWhiteSpace(cue))
        {
            RemoveOwner(ownerID);
            return;
        }
        lock (sync)
        {
            if (ownerVoices.TryGetValue(ownerID, out var list))
            {
                foreach (var voiceId in list.ToArray())
                {
                    if (activeVoices.TryGetValue(voiceId, out var voice) &&
                        voice.Name.Equals(cue, StringComparison.OrdinalIgnoreCase))
                    {
                        StopVoice(voiceId);
                    }
                }
            }
        }
    }

    public void RemoveOwner(int ownerID)
    {
        lock (sync)
        {
            lastCarHonkByOwner.Remove(ownerID);
            if (ambience?.OwnerId == ownerID)
            {
                ambience = null;
            }
            if (ownerVoices.Remove(ownerID, out var list))
            {
                foreach (var voiceId in list.ToArray())
                {
                    StopVoice(voiceId);
                }
            }
            hitVm?.KillThreadsForOwner(ownerID);
            DrainHitVmCommands(ownerID, pan: true, zoom: true);
        }
    }

    public void End(long voiceID)
    {
        lock (sync)
        {
            if (activeVoices.Remove(voiceID, out var voice))
            {
                if (ownerVoices.TryGetValue(voice.OwnerId, out var list))
                {
                    list.Remove(voiceID);
                    if (list.Count == 0) ownerVoices.Remove(voice.OwnerId);
                }

                if (voice.ManagedByHitVm && hitVm != null)
                {
                    voiceToThread.Remove(voiceID);
                    if (threadToVoice.TryGetValue(voice.HitThreadId, out var mapped) && mapped == voiceID)
                    {
                        threadToVoice.Remove(voice.HitThreadId);
                    }
                    hitVm.OnVoiceEnded(voiceID);
                    var ctx = pendingThreadContext.TryGetValue(voice.HitThreadId, out var saved)
                        ? saved
                        : (voice.OwnerId, voice.Spatial, voice.Zoom);
                    DrainHitVmCommands(ctx.OwnerId, ctx.Spatial, ctx.Zoom);
                }
                else if (voice.IsPlaylist)
                {
                    if (voice.OwnerId > 0)
                        PlayForSimulationObject(voice.Name, (short)voice.OwnerId, voice.Spatial, voice.Zoom);
                    else
                        Play(voice.Name, voice.OwnerId);
                }
            }
        }
    }

    public void Enable(bool enabled)
    {
        lock (sync)
        {
            if (Enabled == enabled) return;
            Enabled = enabled;
            if (!enabled) Reset();
        }
    }

    public void SetVolume(string group, float volume)
    {
        lock (sync)
        {
            var normalizedGroup = group?.ToUpperInvariant() ?? "";
            if (!volumes.ContainsKey(normalizedGroup) || !float.IsFinite(volume) || volume < 0.0f || volume > 1.0f)
            {
                throw new ArgumentException("Invalid audio group volume");
            }

            volumes[normalizedGroup] = volume;
            SavePersistedVolumes();
            foreach (var voice in activeVoices.Values.Where(v => v.Group.Equals(normalizedGroup, StringComparison.OrdinalIgnoreCase)))
            {
                voice.Volume = Math.Clamp(volume * voice.BaseScale, 0.0f, 1.0f);
                EmitVoiceUpdateLocked(voice);
            }
        }
    }

    public void StopGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        lock (sync)
        {
            foreach (var voiceId in activeVoices.Values
                .Where(v => v.Group.Equals(group, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.VoiceId)
                .ToArray())
            {
                StopVoice(voiceId);
            }
        }
    }

    public void StopModeMusic()
    {
        lock (sync)
        {
            StopNonPositionalMusic();
        }
    }

    public void SetSimulationSpeed(int speed)
    {
        lock (sync)
        {
            SimulationSpeed = Math.Max(0, speed);
            hitVm?.SetGlobalRegister(HitReg.SimSpeed, SimulationSpeed);
        }
    }

    public void SetMusicModeActive(bool active)
    {
        lock (sync)
        {
            if (musicModeActive == active) return;
            musicModeActive = active;
            var now = DateTime.UtcNow;
            if (!active && !simulationPaused && !paused)
            {
                ambience?.OnUnpaused(now);
            }
            foreach (var voice in activeVoices.Values)
            {
                if (!voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase)) continue;
                if (active && !voice.PausedBySimulation && !paused)
                {
                    voice.PausedBySimulation = true;
                    voice.PausedAt = now;
                    write(new { type = "audio", command = "pause", voice = voice.VoiceId });
                }
                else if (!active && !simulationPaused && !paused && voice.PausedBySimulation)
                {
                    voice.PausedBySimulation = false;
                    if (voice.PausedAt is { } pausedAt)
                    {
                        voice.StartTime += now - pausedAt;
                        voice.PausedAt = null;
                    }
                    write(new { type = "audio", command = "resume", voice = voice.VoiceId });
                }
            }
        }
    }

    private static bool IsPauseExemptEvent(string name) =>
        name.StartsWith("event_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("sting", StringComparison.OrdinalIgnoreCase);

    private static bool IsCarHonkEvent(string name) =>
        name.Contains("honk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pauses or resumes in-world simulation audio when simulation speed changes, while allowing
    /// mode music streams, UI sounds, and event stingers to continue playing.</summary>
    public void SetSimulationPaused(bool paused, bool blockingDialog = false)
    {
        lock (sync)
        {
            if (simulationPaused == paused) return;
            simulationPaused = paused;
            var now = DateTime.UtcNow;
            if (paused)
            {
                foreach (var voice in activeVoices.Values)
                {
                    if (voice.PausedBySimulation) continue;
                    if (voice.IsModeMusic || (voice.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && voice.OwnerId <= 0)) continue;
                    if (voice.OwnerId <= 0 && voice.SourceOwnerId <= 0 && !voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsPauseExemptEvent(voice.Name)) continue;
                    if (blockingDialog && !voice.IsLoop && (now - voice.StartTime).TotalSeconds < 0.5) continue;

                    voice.PausedBySimulation = true;
                    voice.PausedAt = now;
                    write(new
                    {
                        type = "audio",
                        command = "pause",
                        voice = voice.VoiceId
                    });
                }
            }
            else
            {
                ambience?.OnUnpaused(now);
                foreach (var voice in activeVoices.Values)
                {
                    if (!voice.PausedBySimulation) continue;
                    if (musicModeActive && voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase)) continue;
                    voice.PausedBySimulation = false;
                    if (voice.PausedAt is { } pausedAt)
                    {
                        voice.StartTime += now - pausedAt;
                        voice.PausedAt = null;
                    }
                    write(new
                    {
                        type = "audio",
                        command = "resume",
                        voice = voice.VoiceId
                    });
                }
            }
        }
    }

    public void SetPaused(bool paused)
    {
        lock (sync)
        {
            if (this.paused == paused) return;
            this.paused = paused;
            var now = DateTime.UtcNow;
            if (hitVm != null)
                hitVm.IsPaused = paused;
            if (paused)
            {
                globalPausedAt = now;
            }
            else if (globalPausedAt is { } pausedAt)
            {
                var delta = now - pausedAt;
                foreach (var voice in activeVoices.Values)
                {
                    voice.StartTime += delta;
                }
                globalPausedAt = null;
                lastVmTick = now;
            }

            var command = paused ? "pause" : "resume";
            foreach (var voice in activeVoices.Values)
            {
                write(new
                {
                    type = "audio",
                    command,
                    voice = voice.VoiceId
                });
            }
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            ambience = null;
            musicModeActive = false;
            globalPausedAt = null;
            lastVmTick = DateTime.UtcNow;
            activeVoices.Clear();
            ownerVoices.Clear();
            lastCarHonkByOwner.Clear();
            threadToVoice.Clear();
            voiceToThread.Clear();
            pendingThreadContext.Clear();
            threadDemographicOverrides.Clear();
            lastVmDuckPriority = 0;
            hitVm?.Reset();
            hitVm?.DrainCommands();
            write(new { type = "audio", command = "reset" });
        }
    }

    public void Tick(SimulationWorld world)
    {
        lock (sync)
        {
            lastWorld = world;
            if (!Enabled || paused) return;
            foreach (var owner in ownerVoices.Keys.ToArray())
            {
                if (owner > 0 && world.ObjectById((short)owner) is null)
                {
                    RemoveOwner(owner);
                }
            }
            Tick();
        }
    }

    public void Tick()
    {
        lock (sync)
        {
            if (!Enabled || paused) return;
            var now = DateTime.UtcNow;
            if (hitVm != null)
            {
                float dt = (float)Math.Clamp((now - lastVmTick).TotalSeconds, 0.0, 0.1);
                lastVmTick = now;
                if (dt > 0f)
                {
                    hitVm.Tick(dt);
                    DrainHitVmCommands(NeighborhoodOwnerID, pan: false, zoom: false);
                }
            }
            if (!simulationPaused && !musicModeActive)
            {
                ambience?.Advance(now);
            }
            foreach (var voice in activeVoices.Values.ToArray())
            {
                if (voice.PausedBySimulation) continue;
                if (voice.IsPlaylist || voice.ManagedByHitVm) continue;
                var shouldExpire = !voice.IsLoop || voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase);
                var effectiveDuration = voice.Duration / Math.Clamp(voice.PitchScale, 0.0625f, 4.0f);
                if (shouldExpire && (now - voice.StartTime).TotalSeconds >= effectiveDuration)
                {
                    StopVoice(voice.VoiceId);
                }
            }
        }
    }

    private void KillOwnerNonAmbienceSoundsLocked(int ownerId)
    {
        if (ownerId <= 0) return;
        if (ownerVoices.TryGetValue(ownerId, out var ids))
        {
            foreach (var vid in ids.ToArray())
            {
                if (activeVoices.TryGetValue(vid, out var v) && !v.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase))
                {
                    StopVoice(vid);
                }
            }
        }
        hitVm?.KillThreadsForOwner(ownerId);
        DrainHitVmCommands(ownerId, pan: true, zoom: true);
    }

    private bool HandleSpecialControlEventLocked(string name, int? ownerID)
    {
        var mode = soundBank.GetMusicMode(name);
        if (mode is 0 or 7 or 8 or 9)
        {
            StopNonPositionalMusic();
            if (mode == 0) SetMusicModeActive(false);
            else if (mode is 7 or 8) SetMusicModeActive(true);
            return true;
        }

        var mapping = soundBank.GetEventMapping(name);
        if (mapping != null)
        {
            switch (mapping.Action)
            {
                case HitEventAction.KillInstance:
                case HitEventAction.TurnOffTv:
                {
                    if (ownerID is > 0)
                        KillOwnerNonAmbienceSoundsLocked(ownerID.Value);
                    return true;
                }
                case HitEventAction.TurnOnTv:
                {
                    if (ownerID is > 0)
                        KillOwnerNonAmbienceSoundsLocked(ownerID.Value);
                    return false;
                }
                case HitEventAction.KillAll:
                {
                    foreach (var vid in activeVoices.Keys.ToArray())
                    {
                        StopVoice(vid);
                    }
                    threadToVoice.Clear();
                    voiceToThread.Clear();
                    pendingThreadContext.Clear();
                    threadDemographicOverrides.Clear();
                    lastVmDuckPriority = 0;
                    hitVm?.Reset();
                    hitVm?.DrainCommands();
                    return true;
                }
                case HitEventAction.SndobCancelNote:
                {
                    var targetOwner = ownerID ?? NeighborhoodOwnerID;
                    var stationVoice = activeVoices.Values
                        .Where(v =>
                            v.OwnerId == targetOwner &&
                            (v.ManagedByHitVm || v.IsPlaylist) &&
                            (v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) || v.Name.StartsWith("tv_", StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(v => v.ManagedByHitVm && hitVm?.GetThread(v.HitThreadId)?.FadeKillPending == true ? 1 : 0)
                        .ThenByDescending(v => v.VoiceId)
                        .FirstOrDefault();
                    if (stationVoice != null)
                    {
                        long vid = stationVoice.VoiceId;
                        StopVoice(vid, killHitThread: !stationVoice.ManagedByHitVm);
                        if (stationVoice.ManagedByHitVm && hitVm != null)
                        {
                            hitVm.OnVoiceEnded(vid);
                            var ctx = pendingThreadContext.TryGetValue(stationVoice.HitThreadId, out var saved)
                                ? saved
                                : (stationVoice.OwnerId, stationVoice.Spatial, stationVoice.Zoom);
                            DrainHitVmCommands(ctx.OwnerId, ctx.Spatial, ctx.Zoom);
                        }
                        else if (stationVoice.IsPlaylist)
                        {
                            if (stationVoice.OwnerId > 0)
                                PlayForSimulationObject(stationVoice.Name, (short)stationVoice.OwnerId, stationVoice.Spatial, stationVoice.Zoom);
                            else
                                Play(stationVoice.Name, stationVoice.OwnerId);
                        }
                    }
                    return true;
                }
            }
        }
        return false;
    }

    private bool StartHitTrackLocked(
        HotTrackDef track,
        string eventName,
        int arg1,
        int ownerId,
        bool spatial,
        bool zoom,
        char? voiceTypeOverride)
    {
        if (hitVm == null) return false;
        long beforeSeq = sequence;
        var thread = hitVm.StartTrack(track, arg1, eventName: eventName, runInitialBurst: false, voiceOwnerId: ownerId);
        if (thread == null) return false;
        pendingThreadContext[thread.InstanceId] = (ownerId, spatial, zoom);
        if (voiceTypeOverride.HasValue)
        {
            threadDemographicOverrides[thread.InstanceId] = (arg1, voiceTypeOverride.Value);
        }
        hitVm.RunInitialBurst(thread);
        var failedPatchThreads = new HashSet<int>();
        DrainHitVmCommands(ownerId, spatial, zoom, failedPatchThreads);
        foreach (var failedTid in failedPatchThreads)
        {
            hitVm.KillThread(failedTid);
        }
        if (sequence == beforeSeq && failedPatchThreads.Count > 0)
        {
            hitVm.KillThread(thread.InstanceId);
            DrainHitVmCommands(ownerId, spatial, zoom);
            return false;
        }
        if (failedPatchThreads.Count > 0)
        {
            DrainHitVmCommands(ownerId, spatial, zoom);
        }
        return sequence > beforeSeq || hitVm.GetThread(thread.InstanceId) != null;
    }

    private void DrainHitVmCommands(int defaultOwnerId, bool pan, bool zoom, HashSet<int>? failedPatchThreads = null)
    {
        if (hitVm == null) return;
        foreach (var cmd in hitVm.DrainCommands())
        {
            switch (cmd)
            {
                case HitStartVoiceCommand start:
                {
                    var res = soundBank.ResolvePatchById(
                        start.PatchId,
                        start.Group,
                        start.IsLoop,
                        start.IsPositionalObjectTrack,
                        start.PitchScale,
                        start.Gain,
                        start.TrackId,
                        start.Bank);
                    if (res == null)
                    {
                        hitVm.ClearPendingVoiceBind(start.ThreadId);
                        failedPatchThreads?.Add(start.ThreadId);
                        break;
                    }

                    var ctx = pendingThreadContext.TryGetValue(start.ThreadId, out var savedCtx)
                        ? savedCtx
                        : (OwnerId: defaultOwnerId, Spatial: pan, Zoom: zoom);
                    pendingThreadContext[start.ThreadId] = ctx;

                    var isVox = res.Group == "VOX" || soundBank.IsDemographicVoxEvent(start.EventName);
                    var isPositional = isVox || res.IsPositionalObjectTrack;
                    var effectiveOwner = ctx.OwnerId < 0
                        ? ctx.OwnerId
                        : (isPositional
                            ? (start.SourceObjectId > 0 ? start.SourceObjectId : ctx.OwnerId)
                            : NeighborhoodOwnerID);

                    var isModeMusic = IsModeMusicEvent(start.EventName, res.Group, isPositional);
                    var sourceOwnerId = start.SourceObjectId > 0 ? start.SourceObjectId : (ctx.OwnerId > 0 ? ctx.OwnerId : 0);
                    if (isModeMusic)
                    {
                        StopNonPositionalMusic();
                    }
                    else if (!soundBank.IsMock && isPositional && effectiveOwner > 0 &&
                             res.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) &&
                             ownerVoices.TryGetValue(effectiveOwner, out var existingStationIds))
                    {
                        foreach (var oldVid in existingStationIds
                            .Where(vid => activeVoices.TryGetValue(vid, out var v) &&
                                          v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) &&
                                          v.HitThreadId != start.ThreadId)
                            .ToArray())
                        {
                            StopVoice(oldVid);
                        }
                    }

                    long voiceId = EmitNewVoiceLocked(
                        start.EventName,
                        res.RelativePath,
                        res.Group,
                        res.Duration,
                        res.IsLoop,
                        isPlaylist: false,
                        effectiveOwner,
                        isPositional && ctx.Spatial,
                        isPositional && ctx.Zoom,
                        start.Gain,
                        start.Pan,
                        start.PitchScale,
                        managedByHitVm: true,
                        hitThreadId: start.ThreadId,
                        isModeMusic: isModeMusic,
                        sourceOwnerId: sourceOwnerId);

                    threadToVoice[start.ThreadId] = voiceId;
                    voiceToThread[voiceId] = start.ThreadId;
                    hitVm.BindVoice(start.ThreadId, voiceId);
                    break;
                }
                case HitUpdateVoiceCommand upd:
                {
                    if (activeVoices.TryGetValue(upd.VoiceId, out var voice))
                    {
                        voice.BaseScale = Math.Clamp(upd.Gain, 0f, 1f);
                        voice.Pan = Math.Clamp(upd.Pan, -1f, 1f);
                        voice.PitchScale = Math.Clamp(upd.PitchScale, 0.0625f, 4f);
                        voice.Volume = Math.Clamp(volumes.GetValueOrDefault(voice.Group, 0.8f) * voice.BaseScale, 0f, 1f);
                        EmitVoiceUpdateLocked(voice);
                    }
                    break;
                }
                case HitStopVoiceCommand stop:
                {
                    StopVoice(stop.VoiceId, killHitThread: false);
                    break;
                }
                case HitChainPatchCommand chain:
                {
                    // Queue/replace active voice with chained patch on the same thread.
                    if (activeVoices.TryGetValue(chain.VoiceId, out var oldVoice))
                    {
                        var ctx = pendingThreadContext.TryGetValue(chain.ThreadId, out var savedCtx)
                            ? savedCtx
                            : (oldVoice.OwnerId, oldVoice.Spatial, oldVoice.Zoom);
                        StopVoice(chain.VoiceId, killHitThread: false);
                        var res = soundBank.ResolvePatchById(
                            chain.PatchId,
                            oldVoice.Group,
                            oldVoice.IsLoop,
                            oldVoice.Spatial || oldVoice.OwnerId > 0,
                            oldVoice.PitchScale,
                            oldVoice.BaseScale,
                            bank: chain.Bank);
                        if (res != null)
                        {
                            long nextVid = EmitNewVoiceLocked(
                                oldVoice.Name,
                                res.RelativePath,
                                res.Group,
                                res.Duration,
                                res.IsLoop,
                                isPlaylist: false,
                                ctx.OwnerId,
                                ctx.Spatial,
                                ctx.Zoom,
                                oldVoice.BaseScale,
                                oldVoice.Pan,
                                oldVoice.PitchScale,
                                managedByHitVm: true,
                                hitThreadId: chain.ThreadId,
                                isModeMusic: oldVoice.IsModeMusic);
                            threadToVoice[chain.ThreadId] = nextVid;
                            voiceToThread[nextVid] = chain.ThreadId;
                            hitVm.BindVoice(chain.ThreadId, nextVid);
                        }
                    }
                    break;
                }
            }
        }

        // Propagate global ducking (0057fa52.c:50-56) to non-VM mode music voices as well.
        int currentDuck = hitVm.MainDuckPriority;
        if (currentDuck != lastVmDuckPriority)
        {
            lastVmDuckPriority = currentDuck;
            float duckFactor = currentDuck > 10 ? 0.5f : 1.0f;
            foreach (var voice in activeVoices.Values)
            {
                if (!voice.ManagedByHitVm && voice.IsModeMusic)
                {
                    voice.BaseScale = duckFactor;
                    voice.Volume = Math.Clamp(volumes.GetValueOrDefault("MUSIC", 0.8f) * duckFactor, 0f, 1f);
                    EmitVoiceUpdateLocked(voice);
                }
            }
        }
    }

    private long EmitNewVoiceLocked(
        string name,
        string file,
        string group,
        float duration,
        bool isLoop,
        bool isPlaylist,
        int owner,
        bool spatial,
        bool zoom,
        float baseScale,
        float pan,
        float pitchScale,
        bool managedByHitVm,
        int hitThreadId,
        bool isModeMusic = false,
        int sourceOwnerId = 0)
    {
        StealVoiceIfFull();
        var id = ++sequence;
        var master = volumes.GetValueOrDefault(group, 0.8f);
        float initialDuck = (isModeMusic && hitVm != null && hitVm.MainDuckPriority > 10) ? 0.5f : 1.0f;
        float effectiveBaseScale = Math.Clamp(baseScale * initialDuck, 0f, 1f);
        var startedAt = DateTime.UtcNow;
        var voice = new VoiceInstance
        {
            VoiceId = id,
            Name = name,
            File = file,
            Group = group,
            Duration = duration,
            IsLoop = isLoop,
            IsPlaylist = isPlaylist,
            ManagedByHitVm = managedByHitVm,
            HitThreadId = hitThreadId,
            IsModeMusic = isModeMusic,
            OwnerId = owner,
            SourceOwnerId = sourceOwnerId > 0 ? sourceOwnerId : (owner > 0 ? owner : 0),
            Spatial = spatial,
            Zoom = zoom,
            Volume = Math.Clamp(master * effectiveBaseScale, 0f, 1f),
            BaseScale = effectiveBaseScale,
            Pan = Math.Clamp(pan, -1f, 1f),
            PitchScale = Math.Clamp(pitchScale, 0.0625f, 4f),
            StartTime = startedAt
        };
        activeVoices[id] = voice;
        if (voice.SourceOwnerId > 0 && IsCarHonkEvent(name))
        {
            lastCarHonkByOwner[voice.SourceOwnerId] = startedAt;
        }
        if (owner != 0)
        {
            if (!ownerVoices.TryGetValue(owner, out var list))
            {
                list = [];
                ownerVoices[owner] = list;
            }
            list.Add(id);
        }

        if (LogAudio)
        {
            Console.Error.WriteLine($"[Audio] play cue={name} file={voice.File} group={voice.Group} loop={voice.IsLoop} owner={voice.OwnerId} spatial={voice.Spatial} pitch={voice.PitchScale:0.###}");
        }
        write(new
        {
            type = "audio",
            command = "play",
            voice = id,
            file = voice.File,
            duration = voice.Duration,
            group = voice.Group,
            volume = voice.Volume,
            pan = voice.Pan,
            loop = voice.IsLoop,
            ownerID = voice.OwnerId,
            spatial = voice.Spatial,
            zoom = voice.Zoom,
            pitchScale = voice.PitchScale
        });
        return id;
    }

    private void EmitVoiceUpdateLocked(VoiceInstance voice)
    {
        write(new
        {
            type = "audio",
            command = "update",
            voice = voice.VoiceId,
            file = voice.File,
            duration = voice.Duration,
            group = voice.Group,
            volume = voice.Volume,
            pan = voice.Pan,
            loop = voice.IsLoop,
            ownerID = voice.OwnerId,
            spatial = voice.Spatial,
            zoom = voice.Zoom,
            pitchScale = voice.PitchScale
        });
    }

    private int GetSourceDataField(int objId, int fieldId)
    {
        if (fieldId == 0)
        {
            if (hitVm?.CurrentThreadId is { } tid &&
                threadDemographicOverrides.TryGetValue(tid, out var ov) &&
                ov.ObjId == objId)
            {
                return char.ToLowerInvariant(ov.VoiceCode) switch
                {
                    'p' => 1000,
                    'k' => 2,
                    'f' => 1,
                    _ => 0
                };
            }
            if (speakerDemographics.TryGetValue((short)objId, out var code))
            {
                return char.ToLowerInvariant(code) switch
                {
                    'p' => 1000,
                    'k' => 2,
                    'f' => 1,
                    _ => 0
                };
            }
            if (lastWorld != null && objId > 0 && lastWorld.ObjectById((short)objId) is XPerson person)
            {
                if ((person[PersonData.RenderDisplayFlags] & 0x0C) != 0)
                    return 1000;
                if (person[PersonData.PersonAge] < 12)
                    return 2;
                return person[PersonData.Gender] != 0 ? 1 : 0;
            }
            return 0;
        }

        if (lastWorld != null && objId > 0 && lastWorld.ObjectById((short)objId) is XPerson p)
        {
            return fieldId switch
            {
                1 => p[PersonData.PersonAge],
                2 => p[PersonData.NiceLoyalPersonality] / 10,
                3 => p[PersonData.ActivePlayfulPersonality] / 10,
                5 => p[PersonData.NeatQuietPersonality] / 10,
                6 => p[PersonData.GenerousPersonality10NPCStillUseThis] / 10,
                7 => p[PersonData.PlayfulSmartPersonality] / 10,
                11 => p[PersonData.OutgoingFriendlyPersonality] / 10,
                _ => 50
            };
        }
        return fieldId == 1 ? 25 : 50;
    }

    private string? ResolveAudioSettingsPath()
    {
        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathRooted(profile) || !Directory.Exists(profile))
            return null;
        if (IsGameInstallDirectory(profile, gameDirectory))
            return null;
        return Path.Combine(profile, "Settings", "audio.json");
    }

    private static bool IsGameInstallDirectory(string candidatePath, string? gameDir)
    {
        try
        {
            var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                var fullGame = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (string.Equals(fullCandidate, fullGame, cmp))
                    return true;
            }
            return File.Exists(Path.Combine(fullCandidate, "Sims.exe")) ||
                   Directory.Exists(Path.Combine(fullCandidate, "SoundData"));
        }
        catch
        {
            return true;
        }
    }

    private void LoadPersistedVolumes()
    {
        var path = ResolveAudioSettingsPath();
        if (path == null || !File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            string[] keys = ["FX", "MUSIC", "VOX", "AMBIENCE"];
            if (root.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number &&
                schema.TryGetInt32(out int ver) &&
                ver == 1 &&
                root.TryGetProperty("volumes", out var arr) &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() == 4)
            {
                float[] parsed = new float[4];
                for (int i = 0; i < 4; i++)
                {
                    if (arr[i].ValueKind != JsonValueKind.Number || !arr[i].TryGetSingle(out float v) || !float.IsFinite(v))
                        return;
                    parsed[i] = Math.Clamp(v, 0f, 1f);
                }
                for (int i = 0; i < 4; i++)
                {
                    volumes[keys[i]] = parsed[i];
                }
                return;
            }

            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var prop) &&
                    prop.ValueKind == JsonValueKind.Number &&
                    prop.TryGetSingle(out float v) &&
                    float.IsFinite(v))
                {
                    volumes[key] = Math.Clamp(v, 0f, 1f);
                }
            }
        }
        catch { }
    }

    private void SavePersistedVolumes()
    {
        var path = ResolveAudioSettingsPath();
        if (path == null) return;
        try
        {
            var settingsDir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(settingsDir);
            var payload = new
            {
                schemaVersion = 1,
                volumes = new[]
                {
                    volumes.GetValueOrDefault("FX", 0.8f),
                    volumes.GetValueOrDefault("MUSIC", 0.8f),
                    volumes.GetValueOrDefault("VOX", 0.8f),
                    volumes.GetValueOrDefault("AMBIENCE", 1.0f)
                }
            };
            var tmpPath = Path.Combine(settingsDir, $".audio-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(payload));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch { }
    }

    private bool StartAmbience(string name, int owner)
    {
        var seq = soundBank.ResolveAmbienceSequence(name);
        if (seq == null) return false;
        if (ambience != null &&
            ambience.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            ambience.OwnerId == owner)
        {
            return true;
        }

        StopGroup("AMBIENCE");
        ambience = new AmbienceSequencer(this, name, owner, seq);
        if (!simulationPaused && !musicModeActive && !paused)
        {
            ambience.StartInitialBedNote();
        }
        return true;
    }

    private void SpawnAmbienceNote(FscNote note, FscSequence seq, int owner, Random random)
    {
        var res = soundBank.ResolveAmbienceNote(note.Filename, note.Loop2);
        if (res == null) return;

        var baseScale = Math.Clamp((note.Volume / 1024f) * (seq.MasterVolume / 1024f), 0.02f, 1.0f);
        if (note.Rand)
        {
            baseScale -= (float)(random.NextDouble() * (baseScale / 3.0f));
        }

        var pan = Math.Clamp((note.LrPan - 512) / 512f, -1.0f, 1.0f);
        if (note.Rand2 && note.LrPan == 512)
        {
            pan = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        var beatLength = 60.0f / Math.Max((ushort)1, seq.Tempo);
        var duration = note.Loop2
            ? Math.Max(res.Duration, Math.Max(1, (int)note.Loop) * Math.Max(1, (int)seq.BeatsPerBar) * beatLength)
            : res.Duration;

        StealVoiceIfFull();
        var id = ++sequence;
        var masterVol = volumes.GetValueOrDefault("AMBIENCE", 1.0f);
        var shouldStartPaused = simulationPaused || musicModeActive;
        float pitchScale = 1.0f;
        if (note.PitchL != 0 || note.PitchR != 0)
        {
            int lo = Math.Min(note.PitchL, note.PitchR);
            int hi = Math.Max(note.PitchL, note.PitchR);
            double semitones = lo == hi ? lo : lo + random.NextDouble() * (hi - lo);
            pitchScale = Math.Clamp(MathF.Pow(2.0f, (float)(semitones / 12.0)), 0.0625f, 4.0f);
        }

        var voice = new VoiceInstance
        {
            VoiceId = id,
            Name = ambience?.Name ?? "setambience_backyard",
            File = res.RelativePath,
            Group = "AMBIENCE",
            Duration = duration,
            IsLoop = note.Loop2,
            OwnerId = owner,
            Spatial = false,
            Zoom = false,
            Volume = Math.Clamp(masterVol * baseScale, 0.0f, 1.0f),
            BaseScale = baseScale,
            Pan = pan,
            PitchScale = pitchScale,
            AmbienceGroupId = note.GroupId,
            PausedBySimulation = shouldStartPaused,
            StartTime = DateTime.UtcNow
        };
        activeVoices[id] = voice;
        if (owner != 0)
        {
            if (!ownerVoices.TryGetValue(owner, out var list))
            {
                list = [];
                ownerVoices[owner] = list;
            }
            list.Add(id);
        }

        if (LogAudio)
        {
            Console.Error.WriteLine($"[Audio] play cue={voice.Name}:{note.Filename} file={voice.File} group={voice.Group} loop={voice.IsLoop} owner={voice.OwnerId} spatial=False pitch={voice.PitchScale:0.###}");
        }
        write(new
        {
            type = "audio",
            command = "play",
            voice = id,
            file = voice.File,
            duration = voice.Duration,
            group = voice.Group,
            volume = voice.Volume,
            pan = voice.Pan,
            loop = voice.IsLoop,
            ownerID = voice.OwnerId,
            spatial = voice.Spatial,
            zoom = voice.Zoom,
            pitchScale = voice.PitchScale
        });

        if (shouldStartPaused)
        {
            write(new
            {
                type = "audio",
                command = "pause",
                voice = id
            });
        }
    }

    internal sealed class AmbienceSequencer
    {
        private readonly HitAudioRuntime audio;
        private readonly FscSequence sequence;
        private readonly Random random = new();
        private readonly float beatLength;
        private DateTime lastTick = DateTime.UtcNow;
        private float elapsed;
        private int position;
        private int loopCount = -1;

        public string Name { get; }
        public int OwnerId { get; }

        public AmbienceSequencer(HitAudioRuntime audio, string name, int ownerId, FscSequence sequence)
        {
            this.audio = audio;
            Name = name;
            OwnerId = ownerId;
            this.sequence = sequence;
            beatLength = 60.0f / Math.Max((ushort)1, sequence.Tempo);
            Restart();
        }

        public void OnUnpaused(DateTime now)
        {
            lastTick = now;
        }

        public void StartInitialBedNote()
        {
            foreach (var preferLoop in new[] { true, false })
            {
                foreach (var note in sequence.Notes)
                {
                    if (preferLoop && !note.Loop2) continue;
                    if (note.Prob > 0 &&
                        !string.IsNullOrWhiteSpace(note.Filename) &&
                        !note.Filename.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        audio.SpawnAmbienceNote(note, sequence, OwnerId, random);
                        return;
                    }
                }
            }
        }

        public void Advance(DateTime now)
        {
            var dt = (float)Math.Clamp((now - lastTick).TotalSeconds, 0.0, 0.25);
            lastTick = now;
            elapsed += dt;
            while (elapsed >= beatLength)
            {
                elapsed -= beatLength;
                NextBeat();
            }
        }

        private void Restart() =>
            position = sequence.RandomJumpPoints.Count == 0
                ? 0
                : sequence.RandomJumpPoints[random.Next(sequence.RandomJumpPoints.Count)];

        internal void NextBeat()
        {
            if (sequence.Notes.Count == 0)
                return;

            var activeAmbience = audio.activeVoices.Values
                .Where(v => v.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (activeAmbience.Count == 0)
            {
                StartInitialBedNote();
                return;
            }

            if (activeAmbience.Count >= Math.Max(1, (int)sequence.Max))
                return;

            if (loopCount > 0)
            {
                loopCount--;
                return;
            }

            if (position >= sequence.Notes.Count)
                Restart();

            var note = sequence.Notes[position++];
            if (note.Rand)
            {
                Restart();
                note = sequence.Notes[position++];
            }

            if (string.IsNullOrWhiteSpace(note.Filename) ||
                note.Filename.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                note.Prob == 0)
            {
                return;
            }

            if (note.GroupId != 0 && activeAmbience.Any(v => v.AmbienceGroupId == note.GroupId))
                return;

            if (random.Next(20) < note.Prob)
            {
                audio.SpawnAmbienceNote(note, sequence, OwnerId, random);
            }
            loopCount = note.Loop2 ? Math.Max(0, (int)note.Loop - 1) : 0;
        }
    }

    public object State()
    {
        lock (sync)
        {
            return new
            {
                enabled = Enabled,
                voices = activeVoices.Count,
                volumes = new Dictionary<string, float>(volumes)
            };
        }
    }

    public void Dispose()
    {
        Reset();
        soundBank.Dispose();
    }
}

