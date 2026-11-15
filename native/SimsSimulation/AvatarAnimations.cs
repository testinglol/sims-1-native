using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Loads the animation names, frame counts and XEVT tables read by Sims.exe's character-data path.
/// FUN_005837c0 defines the character search path; FUN_00596834/FUN_00594536 read BCF/CMX motion and event records.
/// Standard-animation distance and root offset are also read for the native locomotion controller.</summary>
internal static class AvatarAnimations
{
    internal sealed record ParsedCharacterFile(CharacterSearchPath Path, CharacterFile File, CharacterData Data);

    internal sealed record SharedCharacterIndex(
        CharacterSearchPath Skins,
        CharacterSearchPath Textures,
        CharacterSearchPath Animation,
        IReadOnlyList<ParsedCharacterFile> ParsedFiles,
        IReadOnlyDictionary<string, CharacterFile[]> CurveFiles,
        IReadOnlyDictionary<string, List<Animation>> AnimationsByName);

    private static readonly ConditionalWeakTable<ObjectRegistry, SharedCharacterIndex> SharedIndices = new();
    private static readonly ConditionalWeakTable<ObjectRegistry, Dictionary<string, AvatarAnimationClip>> CachedCatalogs = new();
    private static readonly ConcurrentDictionary<string, SharedCharacterIndex> SharedIndicesByKey = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Dictionary<string, AvatarAnimationClip>> CachedCatalogsByKey = new(StringComparer.Ordinal);

    private static string BuildSearchPathCacheKey(string gameRoot, CharacterSearchPath skins, CharacterSearchPath animationPath)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hasher.AppendData(bytes);
            hasher.AppendData("\0"u8);
        }
        Feed(Path.GetFullPath(gameRoot));
        Feed(skins.Files.Count.ToString());
        foreach (var file in skins.Files)
        {
            Feed(file.Source);
            Feed(file.Name);
        }
        Feed(animationPath.Files.Count.ToString());
        foreach (var file in animationPath.Files)
        {
            Feed(file.Source);
            Feed(file.Name);
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    internal static SharedCharacterIndex GetSharedIndex(string gameRoot, ObjectRegistry? registry)
    {
        if (registry is not null)
            return SharedIndices.GetValue(registry, reg => BuildSharedIndex(gameRoot, reg, tolerateUnreadable: true));
        return BuildSharedIndex(gameRoot, null, tolerateUnreadable: false);
    }

    private static SharedCharacterIndex BuildSharedIndex(string gameRoot, ObjectRegistry? registry, bool tolerateUnreadable)
    {
        var skins = CharacterSearchPath.Skins(gameRoot, registry);
        var textures = CharacterSearchPath.Textures(gameRoot, registry);
        var animationPath = CharacterSearchPath.Animation(gameRoot, registry);
        var cacheKey = tolerateUnreadable ? BuildSearchPathCacheKey(gameRoot, skins, animationPath) : null;
        if (cacheKey is not null && SharedIndicesByKey.TryGetValue(cacheKey, out var cachedIndex))
            return cachedIndex;

        var seenFiles = new HashSet<(string Source, string Name)>();
        var pending = new List<(CharacterSearchPath Path, CharacterFile File)>(1024);
        foreach (var (path, file) in skins.Files.Select(f => (skins, f)).Concat(animationPath.Files.Select(f => (animationPath, f))))
        {
            if (!file.Extension.Equals(".bcf", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".cmx", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seenFiles.Add((file.Source, file.Name))) continue;
            pending.Add((path, file));
        }

        var decoded = new (CharacterData? Data, ExceptionDispatchInfo? Error)[pending.Count];
        Parallel.For(0, pending.Count, i =>
        {
            var (_, file) = pending[i];
            try
            {
                var bytes = file.Read();
                var data = file.Extension.Equals(".cmx", StringComparison.OrdinalIgnoreCase)
                    ? CharacterData.ReadText(bytes, file.Source + ":" + file.Name)
                    : CharacterData.ReadBinary(bytes, file.Source + ":" + file.Name);
                decoded[i] = (data, null);
            }
            catch (Exception error) when (tolerateUnreadable && error is ResourceFormatException or InvalidDataException or ArgumentException)
            {
                decoded[i] = (null, null);
            }
            catch (Exception error)
            {
                decoded[i] = (null, ExceptionDispatchInfo.Capture(error));
            }
        });

        var parsedFiles = new List<ParsedCharacterFile>(pending.Count);
        var animationsByName = new Dictionary<string, List<Animation>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pending.Count; i++)
        {
            decoded[i].Error?.Throw();
            if (decoded[i].Data is not { } data) continue;
            var (path, file) = pending[i];
            parsedFiles.Add(new ParsedCharacterFile(path, file, data));
            for (var a = 0; a < data.Animations.Count; a++)
            {
                var anim = data.Animations[a];
                if (!animationsByName.TryGetValue(anim.Name, out var list))
                {
                    list = new List<Animation>(1);
                    animationsByName[anim.Name] = list;
                }
                list.Add(anim);
            }
        }

        var seenCurves = new HashSet<(string Source, string Name)>();
        var curveGroups = new Dictionary<string, List<CharacterFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in skins.Files.Concat(animationPath.Files))
        {
            if (!file.Extension.Equals(".cfp", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seenCurves.Add((file.Source, file.Name))) continue;
            if (!curveGroups.TryGetValue(file.Name, out var list))
            {
                list = new List<CharacterFile>(1);
                curveGroups[file.Name] = list;
            }
            list.Add(file);
        }
        var curveFiles = new Dictionary<string, CharacterFile[]>(curveGroups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in curveGroups)
            curveFiles[key] = list.ToArray();

        var index = new SharedCharacterIndex(skins, textures, animationPath, parsedFiles, curveFiles, animationsByName);
        if (cacheKey is not null) SharedIndicesByKey.TryAdd(cacheKey, index);
        return index;
    }

    internal static void Populate(SimulationWorld world, IffFile? global, string gameRoot, string? userData = null,
        ObjectRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (global is null) return;
        var catalog = registry is not null
            ? CachedCatalogs.GetValue(registry, reg => ReadCatalog(gameRoot, reg))
            : ReadCatalog(gameRoot, null);
        AddTable(1, false, 128); // Global
        AddTable(2, false, 130); // Person stock
        AddTable(3, false, 156); // Misc, adult
        AddTable(3, true, 157);  // Misc, child
        AddStandardWalkFallbackTable(150);
        AddStandardWalkFallbackTable(151);
        AddStandardWalkFallbackTable(152);
        AddStandardWalkFallbackTable(153);
        AddStandardWalkFallbackTable(158);
        AddStandardWalkFallbackTable(159);
        AddStandardWalkFallbackTable(160);
        if (registry is not null)
            foreach (var entry in registry.Entries)
                AddCustomWalkTable(entry.Guid, entry.File);
        AddReachTable(false, 154);
        AddReachTable(true, 155);
        if (registry is not null)
            foreach (var entry in registry.Entries)
                AddObjectTable(entry.Guid, entry.File);

        void AddReachTable(bool child, ushort tableId)
        {
            // GetReachSkill asks the table of that age for ReachAnimIdx + 1 (one-based); an empty entry has no skill.
            var chunk = global.Find(ResourceType.Str, tableId);
            if (chunk is null) return;
            var names = StringTable.Read(global, chunk).ForLanguage(1);
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index].Trim();
                if (name.Length != 0 && catalog.TryGetValue(name, out var clip))
                    world.ReachAvatarAnimationTable[(child, index)] = clip;
            }
        }

        void AddStandardWalkFallbackTable(ushort tableId)
        {
            // SkillLookup.s::InitSkillLookup loads Global STR# 150-160. GetStdSkill checks the Sim's ObjSelector
            // STR# 150 first. On a pool tile it checks 158/159 for children by gender or 160 for adults; otherwise
            // it checks 151 for age <= 17 or 152/153 by gender. Empty entries fall back to Global STR# 150.
            // GetString(StdAnimIdx + 1) maps to this zero-based index.
            var chunk = global.Find(ResourceType.Str, tableId);
            if (chunk is null) return;
            var names = StringTable.Read(global, chunk).ForLanguage(1);
            for (var standardIndex = 0; standardIndex < names.Count; standardIndex++)
            {
                var name = names[standardIndex].Trim();
                if (name.Length != 0 && catalog.TryGetValue(name, out var clip))
                    world.StandardWalkFallbackAnimationTable[(tableId, standardIndex)] = clip;
            }
        }

        void AddCustomWalkTable(uint objectGuid, IffFile file)
        {
            // ObjSelector::GetCustomWalkAnimTable asks its private iResFiles for STR# 150.
            // Each registered selector therefore owns the table with its own object GUID.
            var chunk = file.Find(ResourceType.Str, 150);
            if (chunk is null) return;
            var names = StringTable.Read(file, chunk).ForLanguage(1);
            for (var standardIndex = 0; standardIndex < names.Count; standardIndex++)
            {
                var name = names[standardIndex].Trim();
                if (name.Length != 0 && catalog.TryGetValue(name, out var clip))
                    world.CustomWalkAvatarAnimationTable[(objectGuid, standardIndex)] = clip;
            }
        }

        void AddTable(byte source, bool child, ushort tableId)
        {
            var chunk = global.Find(ResourceType.Str, tableId);
            if (chunk is null) return;
            var names = StringTable.Read(global, chunk).ForLanguage(1);
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index].Trim();
                if (catalog.TryGetValue(name, out var clip))
                    world.AvatarAnimationTable[(source, child, index)] = clip;
            }
        }

        void AddObjectTable(uint objectGuid, IffFile file)
        {
            // cXObject's selector caches STR# 129 (adult/adult), 130 (child/child),
            // 131 (adult actor/child target), and 132 (child actor/adult target).
            // FUN_00457ccc/ d14/ d38/ d5c call FUN_00456a37 on this object's IFF.
            foreach (ushort tableId in new ushort[] { 129, 130, 131, 132 })
            {
                var chunk = file.Find(ResourceType.Str, tableId);
                if (chunk is null) continue;
                var names = StringTable.Read(file, chunk).ForLanguage(1);
                for (var index = 0; index < names.Count; index++)
                {
                    var name = names[index].Trim();
                    if (catalog.TryGetValue(name, out var clip))
                        world.ObjectAvatarAnimationTable[(unchecked((int)objectGuid), tableId, index)] = clip;
                }
            }
        }
    }

    internal static bool DispatchTimeProperty(XPerson person, AvatarAnimationPlayback playback, AvatarAnimationSoundEvent prop) =>
        SimulationWorld.ApplyAnimationTimeProperty(person, playback, prop);

    private static Dictionary<string, AvatarAnimationClip> ReadCatalog(string gameRoot, ObjectRegistry? registry)
    {
        // Sims.exe searches character BCF/CMX data in the skin path and GameData/Animation; the latter also carries
        // the original Animation.far archive. First matching resource wins, matching its ordered search paths.
        var shared = GetSharedIndex(gameRoot, registry);
        var cacheKey = registry is not null ? BuildSearchPathCacheKey(gameRoot, shared.Skins, shared.Animation) : null;
        if (cacheKey is not null && CachedCatalogsByKey.TryGetValue(cacheKey, out var cachedCatalog))
            return cachedCatalog;

        var curveFiles = shared.CurveFiles;
        var decodedCurves = new ConcurrentDictionary<(string Source, string Name, int TranslationFrames, int RotationFrames), AnimationCurves>();
        var result = new Dictionary<string, AvatarAnimationClip>(shared.AnimationsByName.Count, StringComparer.OrdinalIgnoreCase);
        var work = new List<Animation>(shared.AnimationsByName.Count);
        var seenAnimationNames = new HashSet<string>(shared.AnimationsByName.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var parsed in shared.ParsedFiles)
        {
            foreach (var animation in parsed.Data.Animations)
            {
                if (seenAnimationNames.Add(animation.Name)) work.Add(animation);
            }
        }
        var clips = new AvatarAnimationClip[work.Count];
        Parallel.For(0, work.Count, index =>
        {
            var animation = work[index];
            {
                var frameCount = animation.PlaybackFrameCount;
                // Practice::Apply (00597c54.c:39-40,136-137,240-241): MotionEvent.Time is already in milliseconds
                // and is compared against trunc(phase * Skill.Duration) within [0, trunc(Skill.Duration)].
                var durationLimit = animation.Floats[0] > 0f ? (int)animation.Floats[0] : int.MaxValue;
                List<AvatarAnimationEvent>? eventList = null;
                List<AvatarAnimationSoundEvent>? soundList = null;
                for (var m = 0; m < animation.Motions.Count; m++)
                {
                    var motionEvents = animation.Motions[m].Events;
                    for (var e = 0; e < motionEvents.Count; e++)
                    {
                        var item = motionEvents[e];
                        var timeMs = item.Time;
                        if (timeMs < 0 || timeMs > durationLimit) continue;
                        string? packAudio = null;
                        string? packCue = null;
                        string? packResource = null;
                        string? packAudioLoop = null;
                        string? packAudioStop = null;
                        for (var p = 0; p < item.Properties.Count; p++)
                        {
                            var property = item.Properties[p];
                            var propName = property.Name;
                            if (propName.Equals("xevt", StringComparison.OrdinalIgnoreCase))
                            {
                                if (short.TryParse(property.Value, System.Globalization.NumberStyles.Integer,
                                        System.Globalization.CultureInfo.InvariantCulture, out var xevtValue))
                                {
                                    eventList ??= [];
                                    eventList.Add(new AvatarAnimationEvent(timeMs, xevtValue));
                                }
                            }
                            else if (propName.Equals("sound", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("selectedsound", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("deselectedsound", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("footstep", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("interruptable", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("interruptible", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("lefthand", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("righthand", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("censor", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("dress", StringComparison.OrdinalIgnoreCase) ||
                                     propName.Equals("undress", StringComparison.OrdinalIgnoreCase))
                            {
                                var cue = property.Value.Trim();
                                if (cue.Length > 0)
                                {
                                    soundList ??= [];
                                    soundList.Add(new AvatarAnimationSoundEvent(timeMs, "", propName.ToLowerInvariant(), cue, false, false));
                                }
                            }
                            else if (propName.Equals("packaudio", StringComparison.OrdinalIgnoreCase)) packAudio = property.Value;
                            else if (propName.Equals("packcue", StringComparison.OrdinalIgnoreCase)) packCue = property.Value;
                            else if (propName.Equals("packresource", StringComparison.OrdinalIgnoreCase)) packResource = property.Value;
                            else if (propName.Equals("packaudioloop", StringComparison.OrdinalIgnoreCase)) packAudioLoop = property.Value;
                            else if (propName.Equals("packaudiostop", StringComparison.OrdinalIgnoreCase)) packAudioStop = property.Value;
                        }
                        if (packAudio is not null && packCue is not null)
                        {
                            soundList ??= [];
                            soundList.Add(new AvatarAnimationSoundEvent(timeMs, packAudio, packResource ?? "",
                                packCue, bool.TryParse(packAudioLoop, out var loop) && loop,
                                bool.TryParse(packAudioStop, out var stop) && stop));
                        }
                    }
                }
                IReadOnlyList<AvatarAnimationEvent> events;
                if (eventList is null) events = Array.Empty<AvatarAnimationEvent>();
                else
                {
                    eventList.Sort(static (a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
                    events = eventList.ToArray();
                }
                IReadOnlyList<AvatarAnimationSoundEvent> sounds;
                if (soundList is null) sounds = Array.Empty<AvatarAnimationSoundEvent>();
                else
                {
                    soundList.Sort(static (a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
                    sounds = soundList.ToArray();
                }
                // Skill::ReadFromFile stores the two record floats at +0x1c and +0x20;
                // SAnimator::StdAnimDist reads +0x20, so use the second parsed float.
                // Defer CFP curve decompression until locomotion actually queries a clip's root motion.
                var capturedAnimation = animation;
                var hasRootMotion = HasRootMotionCandidate(capturedAnimation, curveFiles);
                Lazy<(Vector3 Offset, IReadOnlyList<Vector3>? Samples)>? lazyRootMotion = hasRootMotion
                    ? new Lazy<(Vector3 Offset, IReadOnlyList<Vector3>? Samples)>(() =>
                    {
                        var samples = ReadStandardRootMotionSamples(capturedAnimation, curveFiles, decodedCurves, out var offset);
                        return (offset, samples);
                    }, LazyThreadSafetyMode.ExecutionAndPublication)
                    : null;
                var markerOne = FindLocomotionMarker(animation, markerValue: "1");
                var markerTwo = FindLocomotionMarker(animation, markerValue: "2");
                clips[index] = new AvatarAnimationClip(animation.Name, frameCount, events, sounds,
                    animation.Floats[1], default, lazyRootMotion is null ? Array.Empty<Vector3>() : null,
                    animation.Floats[0], markerOne, markerTwo, lazyRootMotion);
            }
        });
        for (var index = 0; index < work.Count; index++)
            result[work[index].Name] = clips[index];
        if (cacheKey is not null) CachedCatalogsByKey.TryAdd(cacheKey, result);
        return result;
    }

    private static bool HasRootMotionCandidate(Animation animation, IReadOnlyDictionary<string, CharacterFile[]> curveFiles)
    {
        for (var i = 0; i < animation.Motions.Count; i++)
        {
            var item = animation.Motions[i];
            if (item.Name.Equals("ROOT", StringComparison.OrdinalIgnoreCase))
                return item.FrameCount > 0 && item.Integers[2] >= 0 && curveFiles.ContainsKey(animation.CurveName + ".cfp");
        }
        return false;
    }

    /// <summary>Matches SAnimator::FindTagTime's motion order, reverse event order, and property order. It searches for
    /// an anchor property with the requested value, returning the event time and owning motion name.
    /// FUN_00588351 passes "anchor"; Mac StartVitaBoy uses 0x1061A184 + 0x46b, also "anchor".</summary>
    private static AvatarAnimationMarker? FindLocomotionMarker(Animation animation, string markerValue)
    {
        if (animation.FindTagTime("anchor", markerValue) is { } tag)
            // StartVitaBoy divides the integer marker time by Skill +0x1c (the parsed duration float).
            return new AvatarAnimationMarker(tag.Time / animation.Floats[0], tag.Bone);
        // FindTagTime initializes no result, writes an empty CTGString on failure, and returns -1.
        // StartVitaBoy still divides that result by Skill +0x1c before storing the normalized field.
        return new AvatarAnimationMarker(-1f / animation.Floats[0], "");
    }

    // Skill translations are Y-up; samples preserve authored body axes (X lateral, Y vertical, Z forward) in lot units.
    // Sims.exe FUN_0058dc76 converts skill translation with _DAT_00666a54 / _DAT_00671ed4 (16 / 3).
    private const float LotUnitsPerSkillUnit = 16f / 3f;

    private static IReadOnlyList<Vector3> ReadStandardRootMotionSamples(Animation animation,
        IReadOnlyDictionary<string, CharacterFile[]> curveFiles,
        ConcurrentDictionary<(string Source, string Name, int TranslationFrames, int RotationFrames), AnimationCurves> decodedCurves,
        out Vector3 standardOffset)
    {
        // SAnimator::StdAnimOffset constructs "ROOT" (lbl_1061A184 + 0x3f7), calls FindMotion on the
        // selected Skill, and returns GetTranslation(lastIndex) without unit conversion
        // (SAnimator.s 0x10358080–0x1035813C). The route animation path separately converts sampled
        // translations from skill units to lot units (Windows FUN_0058dc76).
        standardOffset = Vector3.Zero;
        var motion = animation.Motions.FirstOrDefault(item =>
            item.Name.Equals("ROOT", StringComparison.OrdinalIgnoreCase));
        if (motion is null || motion.FrameCount <= 0 || motion.Integers[2] < 0 ||
            !curveFiles.TryGetValue(animation.CurveName + ".cfp", out var candidates)) return [];

        foreach (var candidate in candidates)
        {
            try
            {
                var key = (candidate.Source, candidate.Name, animation.TranslationCount, animation.RotationCount);
                var curves = decodedCurves.GetOrAdd(key, _ =>
                    AnimationCurveFile.Read(candidate.Read(), candidate.Source + ":" + candidate.Name,
                        animation.TranslationCount, animation.RotationCount));
                var first = (long)motion.Integers[2] * 3;
                var end = first + (long)motion.FrameCount * 3;
                if (first < 0 || end > curves.Translations.Length) return [];
                var samples = new Vector3[motion.FrameCount];
                for (var frame = 0; frame < samples.Length; frame++)
                {
                    var offset = checked((int)first + frame * 3);
                    var translation = new Vector3(curves.Translations[offset], curves.Translations[offset + 1],
                        curves.Translations[offset + 2]);
                    if (frame == samples.Length - 1) standardOffset = translation;
                    samples[frame] = new Vector3(translation.X, translation.Y, translation.Z) * LotUnitsPerSkillUnit;
                }
                return samples;
            }
            catch (ResourceFormatException)
            {
                // Keep searching the ordered source path, as the character loader does.
            }
        }
        standardOffset = Vector3.Zero;
        return [];
    }
}
