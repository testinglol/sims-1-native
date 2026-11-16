using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Loads animation names, frame counts, and XEVT event tables from BCF and CMX files, including
/// distance and root offsets for locomotion.</summary>
internal static class AvatarAnimations
{
    internal sealed record ParsedCharacterFile(CharacterSearchPath Path, CharacterFile File, CharacterData Data);

    internal sealed class SharedCharacterIndex(
        CharacterSearchPath skins,
        Lazy<CharacterSearchPath> textures,
        CharacterSearchPath animation,
        IReadOnlyList<ParsedCharacterFile> parsedFiles,
        IReadOnlyDictionary<string, CharacterFile[]> curveFiles,
        IReadOnlyDictionary<string, List<Animation>> animationsByName)
    {
        public CharacterSearchPath Skins { get; } = skins;
        public CharacterSearchPath Textures => textures.Value;
        public CharacterSearchPath Animation { get; } = animation;
        public IReadOnlyList<ParsedCharacterFile> ParsedFiles { get; } = parsedFiles;
        public IReadOnlyDictionary<string, CharacterFile[]> CurveFiles { get; } = curveFiles;
        public IReadOnlyDictionary<string, List<Animation>> AnimationsByName { get; } = animationsByName;
    }

    private sealed record RegistryAnimationSelectors(
        (uint Guid, int StandardIndex, string Name)[] CustomWalkNames,
        (int Guid, ushort TableId, int Index, string Name)[] ObjectTableNames);

    private static readonly ConditionalWeakTable<ObjectRegistry, SharedCharacterIndex> SharedIndices = new();
    private static readonly ConditionalWeakTable<ObjectRegistry, Dictionary<string, AvatarAnimationClip>> CachedCatalogs = new();
    private static readonly ConditionalWeakTable<ObjectRegistry, RegistryAnimationSelectors> CachedSelectors = new();
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
        var lazyTextures = new Lazy<CharacterSearchPath>(() => CharacterSearchPath.Textures(gameRoot, registry), LazyThreadSafetyMode.ExecutionAndPublication);
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

        var index = new SharedCharacterIndex(skins, lazyTextures, animationPath, parsedFiles, curveFiles, animationsByName);
        if (cacheKey is not null) SharedIndicesByKey.TryAdd(cacheKey, index);
        return index;
    }

    private static RegistryAnimationSelectors GetRegistrySelectors(ObjectRegistry registry) =>
        CachedSelectors.GetValue(registry, static reg =>
        {
            var entries = reg.Entries;
            ReadOnlySpan<ushort> ids = [129, 130, 131, 132];
            var byFile = new Dictionary<IffFile, ((int StandardIndex, string Name)[]? Walk150, (ushort TableId, int Index, string Name)[]? ObjTables)>(
                ReferenceEqualityComparer.Instance);
            var customWalkNames = new List<(uint Guid, int StandardIndex, string Name)>();
            var objectTableNames = new List<(int Guid, ushort TableId, int Index, string Name)>();

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var file = entry.File;
                if (!byFile.TryGetValue(file, out var fileSelectors))
                {
                    (int StandardIndex, string Name)[]? walk150 = null;
                    var customChunk = file.Find(ResourceType.Str, 150);
                    if (customChunk is not null)
                    {
                        var names = StringTable.Read(file, customChunk).ForLanguage(1);
                        List<(int, string)>? list = null;
                        for (var standardIndex = 0; standardIndex < names.Count; standardIndex++)
                        {
                            var name = names[standardIndex].Trim();
                            if (name.Length == 0) continue;
                            list ??= [];
                            list.Add((standardIndex, name));
                        }
                        if (list is not null) walk150 = list.ToArray();
                    }

                    List<(ushort, int, string)>? objList = null;
                    for (var t = 0; t < ids.Length; t++)
                    {
                        var tableId = ids[t];
                        var chunk = file.Find(ResourceType.Str, tableId);
                        if (chunk is null) continue;
                        var names = StringTable.Read(file, chunk).ForLanguage(1);
                        for (var idx = 0; idx < names.Count; idx++)
                        {
                            var name = names[idx].Trim();
                            if (name.Length == 0) continue;
                            objList ??= [];
                            objList.Add((tableId, idx, name));
                        }
                    }
                    fileSelectors = (walk150, objList?.ToArray());
                    byFile[file] = fileSelectors;
                }

                if (fileSelectors.Walk150 is { } w)
                {
                    foreach (var (standardIndex, name) in w)
                        customWalkNames.Add((entry.Guid, standardIndex, name));
                }
                if (fileSelectors.ObjTables is { } o)
                {
                    var guidKey = unchecked((int)entry.Guid);
                    foreach (var (tableId, idx, name) in o)
                        objectTableNames.Add((guidKey, tableId, idx, name));
                }
            }

            return new RegistryAnimationSelectors(customWalkNames.ToArray(), objectTableNames.ToArray());
        });

    internal static void Populate(SimulationWorld world, IffFile? global, string gameRoot, string? userData = null,
        ObjectRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (global is null) return;
        var catalog = registry is not null
            ? CachedCatalogs.GetValue(registry, reg => ReadCatalog(gameRoot, reg))
            : ReadCatalog(gameRoot, null);
        AddTable(1, false, 128);
        AddTable(2, false, 130);
        AddTable(3, false, 156);
        AddTable(3, true, 157);
        AddStandardWalkFallbackTable(150);
        AddStandardWalkFallbackTable(151);
        AddStandardWalkFallbackTable(152);
        AddStandardWalkFallbackTable(153);
        AddStandardWalkFallbackTable(158);
        AddStandardWalkFallbackTable(159);
        AddStandardWalkFallbackTable(160);
        AddReachTable(false, 154);
        AddReachTable(true, 155);
        if (registry is not null)
        {
            var selectors = GetRegistrySelectors(registry);
            foreach (var (guid, standardIndex, name) in selectors.CustomWalkNames)
                if (catalog.TryGetValue(name, out var clip))
                    world.CustomWalkAvatarAnimationTable[(guid, standardIndex)] = clip;
            foreach (var (guid, tableId, index, name) in selectors.ObjectTableNames)
                if (catalog.TryGetValue(name, out var clip))
                    world.ObjectAvatarAnimationTable[(guid, tableId, index)] = clip;
        }

        void AddReachTable(bool child, ushort tableId)
        {
            // Queries the age-specific animation string table using a 1-based reach animation index; empty
            // entries indicate no skill animation.
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
            // Resolves skill animation tables from global string resources based on terrain and avatar
            // demographics: pool tiles check child gendered or adult tables, while dry tiles check age and
            // gender before falling back to table 150.
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
    }

    internal static bool DispatchTimeProperty(XPerson person, AvatarAnimationPlayback playback, AvatarAnimationSoundEvent prop) =>
        SimulationWorld.ApplyAnimationTimeProperty(person, playback, prop);

    private static Dictionary<string, AvatarAnimationClip> ReadCatalog(string gameRoot, ObjectRegistry? registry)
    {
        // Character animation and skeleton files are searched first in the skins directory and then in the
        // animation archive, taking the first match found.
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
                // Motion event timestamps are authored in milliseconds and trigger when matched against the
                // truncated playback phase scaled by total duration.
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
                // Animation headers store clip duration in the first float and total translation distance
                // in the second float.
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

    /// <summary>Searches an animation's motions and events in reverse order for an anchor property,
    /// returning the event time and owning motion.</summary>
    private static AvatarAnimationMarker? FindLocomotionMarker(Animation animation, string markerValue)
    {
        if (animation.FindTagTime("anchor", markerValue) is { } tag)
            return new AvatarAnimationMarker(tag.Time / animation.Floats[0], tag.Bone);
        return new AvatarAnimationMarker(-1f / animation.Floats[0], "");
    }

    // Animation translations use Y-up coordinates (X lateral, Y vertical, Z forward) and scale by 16/3 to
    // convert from animation skill units to lot units.
    private const float LotUnitsPerSkillUnit = 16f / 3f;

    private static IReadOnlyList<Vector3> ReadStandardRootMotionSamples(Animation animation,
        IReadOnlyDictionary<string, CharacterFile[]> curveFiles,
        ConcurrentDictionary<(string Source, string Name, int TranslationFrames, int RotationFrames), AnimationCurves> decodedCurves,
        out Vector3 standardOffset)
    {
        // Extracts the final translation of the ROOT motion in raw animation units without converting to
        // lot units.
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
            }
        }
        standardOffset = Vector3.Zero;
        return [];
    }
}
