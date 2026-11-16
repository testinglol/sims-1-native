using System.Buffers.Binary;
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
    private readonly Action<object> write;
    private readonly object sync = new();
    private readonly SoundBank soundBank;

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
        public required int OwnerId { get; init; }
        public required bool Spatial { get; init; }
        public required bool Zoom { get; init; }
        public float Volume { get; set; } = 1.0f;
        public float BaseScale { get; init; } = 1.0f;
        public float Pan { get; set; }
        public ushort AmbienceGroupId { get; init; }
        public bool PausedBySimulation { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
    }

    private readonly Dictionary<long, VoiceInstance> activeVoices = [];
    private readonly Dictionary<int, List<long>> ownerVoices = [];
    private long sequence;
    private bool paused;
    private bool simulationPaused;
    private bool musicModeActive;
    private AmbienceSequencer? ambience;

    public bool Enabled { get; private set; } = true;
    public bool SuppressActiveDuplicates { get; set; }
    public int SimulationSpeed { get; private set; } = 1;
    public int ActiveVoiceCount { get { lock (sync) { return activeVoices.Count; } } }

    public HitAudioRuntime(string directory, string profile, Action<object> write, string? game = null)
    {
        this.directory = directory;
        this.profile = profile;
        this.write = write;
        soundBank = new SoundBank(game, directory);
        try { Directory.CreateDirectory(directory); } catch { }
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
                .OrderBy(v => v.IsLoop ? 1 : 0)
                .ThenBy(v => v.StartTime)
                .FirstOrDefault();
            if (victim != null)
            {
                StopVoice(victim.VoiceId);
            }
        }
    }

    private void StopVoice(long voiceId)
    {
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

            var res = soundBank.Resolve(name);
            if (res == null) return false;

            var isVox = soundBank.IsDemographicVoxEvent(name);
            var group = isVox ? "VOX" : res.Group;
            if (group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && res.IsLoop)
            {
                foreach (var existing in activeVoices.Values
                    .Where(v => v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && v.OwnerId <= 0)
                    .Select(v => v.VoiceId)
                    .ToArray())
                {
                    StopVoice(existing);
                }
            }
            var isSpatial = res.IsPositionalObjectTrack && ((ownerID.HasValue && ownerID.Value > 0) || isVox);
            var owner = ownerID is < 0 ? ownerID.Value : (!res.IsPositionalObjectTrack ? NeighborhoodOwnerID : (ownerID ?? NeighborhoodOwnerID));

            if (res.IsLoop && !group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && owner != 0 &&
                ownerVoices.TryGetValue(owner, out var existingIds) &&
                existingIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            StealVoiceIfFull();
            var id = ++sequence;

            var voice = new VoiceInstance
            {
                VoiceId = id,
                Name = name,
                File = res.RelativePath,
                Group = group,
                Duration = res.Duration,
                IsLoop = res.IsLoop,
                OwnerId = owner,
                Spatial = isSpatial,
                Zoom = isSpatial,
                Volume = volumes.GetValueOrDefault(group, 0.8f),
                BaseScale = 1.0f,
                Pan = 0f
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
                Console.Error.WriteLine($"[Audio] play cue={name} file={voice.File} group={voice.Group} loop={voice.IsLoop} owner={voice.OwnerId} spatial={voice.Spatial}");
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
                zoom = voice.Zoom
            });
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

    public bool PlayForSimulationObject(string name, short ownerID, bool pan, bool zoom, char? voiceTypeOverride = null)
    {
        if (!Enabled || ownerID < 0 || string.IsNullOrWhiteSpace(name)) return false;
        lock (sync)
        {
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
                    if (!active.IsLoop && !paused && !active.PausedBySimulation &&
                        (now - active.StartTime).TotalSeconds >= active.Duration)
                    {
                        StopVoice(active.VoiceId);
                    }
                }
            }

            var isVox = soundBank.IsDemographicVoxEvent(name);
            if (isVox && ownerID <= 0) return false;

            if (SuppressActiveDuplicates)
            {
                if (ownerID > 0 && ownerVoices.TryGetValue(ownerID, out var activeForOwner) && activeForOwner.Count > 0)
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

                if (SimulationSpeed > 1)
                {
                    foreach (var existing in activeVoices.Values)
                    {
                        if (existing.OwnerId > 0 && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
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
                res = soundBank.Resolve(name);
            }

            if (res == null) return false;

            var group = isVox ? "VOX" : res.Group;
            var isPositional = isVox || res.IsPositionalObjectTrack;
            if (isPositional && ownerID <= 0) return false;

            var loop = res.IsLoop ||
                       (soundBank.IsMock && isPositional && name.Contains("loop", StringComparison.OrdinalIgnoreCase));

            var effectiveOwner = isPositional ? (int)ownerID : NeighborhoodOwnerID;

            if (isPositional)
            {
                if (ownerVoices.TryGetValue(ownerID, out var existingIds) && existingIds.Count > 0)
                {
                    if (loop && existingIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            else if (group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && loop)
            {
                foreach (var existing in activeVoices.Values
                    .Where(v => v.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && v.OwnerId <= 0)
                    .Select(v => v.VoiceId)
                    .ToArray())
                {
                    StopVoice(existing);
                }
            }
            else if (loop && effectiveOwner != 0 && ownerVoices.TryGetValue(effectiveOwner, out var globalIds))
            {
                if (globalIds.Any(vid => activeVoices.TryGetValue(vid, out var v) && v.IsLoop && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            var effectiveSpatial = isPositional && pan;
            var effectiveZoom = isPositional && zoom;

            StealVoiceIfFull();
            var id = ++sequence;

            var voice = new VoiceInstance
            {
                VoiceId = id,
                Name = name,
                File = res.RelativePath,
                Group = group,
                Duration = res.Duration,
                IsLoop = loop,
                OwnerId = effectiveOwner,
                Spatial = effectiveSpatial,
                Zoom = effectiveZoom,
                Volume = volumes.GetValueOrDefault(group, 0.8f),
                BaseScale = 1.0f,
                Pan = 0f,
                StartTime = now
            };
            activeVoices[id] = voice;
            if (isPositional || (!group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase) && effectiveOwner != 0))
            {
                if (!ownerVoices.TryGetValue(effectiveOwner, out var list))
                {
                    list = [];
                    ownerVoices[effectiveOwner] = list;
                }
                list.Add(id);
            }

            if (LogAudio)
            {
                Console.Error.WriteLine($"[Audio] play cue={name} file={voice.File} group={voice.Group} loop={voice.IsLoop} owner={effectiveOwner} spatial={voice.Spatial}");
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
                ownerID = effectiveOwner,
                spatial = voice.Spatial,
                zoom = voice.Zoom
            });
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
            foreach (var voice in activeVoices.Values.Where(v => v.Group.Equals(normalizedGroup, StringComparison.OrdinalIgnoreCase)))
            {
                voice.Volume = Math.Clamp(volume * voice.BaseScale, 0.0f, 1.0f);
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
                    zoom = voice.Zoom
                });
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

    public void SetSimulationSpeed(int speed)
    {
        lock (sync)
        {
            SimulationSpeed = Math.Max(0, speed);
        }
    }

    public void SetMusicModeActive(bool active)
    {
        lock (sync)
        {
            if (musicModeActive == active) return;
            musicModeActive = active;
            foreach (var voice in activeVoices.Values)
            {
                if (!voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase)) continue;
                if (active && !voice.PausedBySimulation && !paused)
                {
                    voice.PausedBySimulation = true;
                    write(new { type = "audio", command = "pause", voice = voice.VoiceId });
                }
                else if (!active && !simulationPaused && !paused && voice.PausedBySimulation)
                {
                    voice.PausedBySimulation = false;
                    write(new { type = "audio", command = "resume", voice = voice.VoiceId });
                }
            }
        }
    }

    private static bool IsPauseExemptEvent(string name) =>
        name.StartsWith("event_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pauses or resumes in-world simulation audio when simulation speed changes, while allowing
    /// music streams, UI sounds, and event stingers to continue playing.</summary>
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
                    if (voice.Group.Equals("MUSIC", StringComparison.OrdinalIgnoreCase)) continue;
                    if (voice.OwnerId <= 0 && !voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsPauseExemptEvent(voice.Name)) continue;
                    if (blockingDialog && !voice.IsLoop && (now - voice.StartTime).TotalSeconds < 0.5) continue;

                    voice.PausedBySimulation = true;
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
            activeVoices.Clear();
            ownerVoices.Clear();
            write(new { type = "audio", command = "reset" });
        }
    }

    public void Tick(SimulationWorld world)
    {
        lock (sync)
        {
            if (!Enabled || paused) return;
            if (!simulationPaused)
            {
                foreach (var owner in ownerVoices.Keys.ToArray())
                {
                    if (owner > 0 && world.ObjectById((short)owner) is null)
                    {
                        RemoveOwner(owner);
                    }
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
            if (!simulationPaused && !musicModeActive)
            {
                ambience?.Advance(now);
            }
            foreach (var voice in activeVoices.Values.ToArray())
            {
                if (voice.PausedBySimulation) continue;
                var shouldExpire = !voice.IsLoop || voice.Group.Equals("AMBIENCE", StringComparison.OrdinalIgnoreCase);
                if (shouldExpire && (now - voice.StartTime).TotalSeconds >= voice.Duration)
                {
                    StopVoice(voice.VoiceId);
                }
            }
        }
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
            Console.Error.WriteLine($"[Audio] play cue={voice.Name}:{note.Filename} file={voice.File} group={voice.Group} loop={voice.IsLoop} owner={voice.OwnerId} spatial=False");
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
            zoom = voice.Zoom
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

    private sealed class AmbienceSequencer
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
            foreach (var note in sequence.Notes)
            {
                if (note.Loop2 && note.Prob > 0 &&
                    !string.IsNullOrWhiteSpace(note.Filename) &&
                    !note.Filename.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    audio.SpawnAmbienceNote(note, sequence, OwnerId, random);
                    return;
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

        private void NextBeat()
        {
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
            if (note.Rand || position >= sequence.Notes.Count)
            {
                Restart();
                note = sequence.Notes[position];
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

