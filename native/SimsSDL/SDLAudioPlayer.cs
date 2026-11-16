using System.Runtime.CompilerServices;
using NLayer;

namespace SimsSDL;

internal sealed class AudioMixer
{
    public struct Voice
    {
        public float[] Samples;
        public int Frame;
        public float Left;
        public float Right;
        public bool Loop;
        public bool Paused;
        public bool Outdoors;
        public float LowLeft;
        public float LowRight;

        public Voice(float[] samples)
        {
            Samples = samples;
            Frame = 0;
            Left = 1f;
            Right = 1f;
            Loop = false;
            Paused = false;
            Outdoors = false;
            LowLeft = 0f;
            LowRight = 0f;
        }
    }

    public delegate void VoiceMutator(ref Voice voice);

    private readonly Lock _lock = new();
    private readonly Dictionary<long, Voice> _voices = new();
    private readonly List<long> _ended = [];
    private readonly List<long> _activeKeys = [];
    private float _muffleGain = 1f;
    private float _muffleCoefficient = 1f;
    private float[] _scratch = [];

    public int Count
    {
        get
        {
            lock (_lock)
                return _voices.Count;
        }
    }

    public void SetMuffle(float gain, float coefficient)
    {
        lock (_lock)
        {
            _muffleGain = gain;
            _muffleCoefficient = coefficient;
        }
    }

    public void Set(long id, Voice? voice)
    {
        lock (_lock)
        {
            if (voice is Voice value)
                _voices[id] = value;
            else
                _voices.Remove(id);
        }
    }

    public void Update(long id, VoiceMutator change)
    {
        lock (_lock)
        {
            ref Voice voice = ref CollectionsMarshal.GetValueRefOrNullRef(_voices, id);
            if (!Unsafe.IsNullRef(ref voice))
                change(ref voice);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _voices.Clear();
            _ended.Clear();
        }
    }

    public long? EvictIfFull(int limit, long protectedVoice)
    {
        lock (_lock)
        {
            if (_voices.Count < limit)
                return null;

            long? nonLoopMin = null;
            long? anyMin = null;
            foreach (var kv in _voices)
            {
                if (kv.Key == protectedVoice)
                    continue;
                if (anyMin is null || kv.Key < anyMin.Value)
                    anyMin = kv.Key;
                if (!kv.Value.Loop && (nonLoopMin is null || kv.Key < nonLoopMin.Value))
                    nonLoopMin = kv.Key;
            }

            long? victim = nonLoopMin ?? anyMin;
            if (victim is long victimId)
                _voices.Remove(victimId);
            return victim;
        }
    }

    public List<long> TakeEnded()
    {
        lock (_lock)
        {
            if (_ended.Count == 0)
                return [];
            var result = new List<long>(_ended);
            _ended.Clear();
            return result;
        }
    }

    public unsafe void MixAndPut(nint stream, int frames)
    {
        if (frames <= 0)
            return;

        int requiredFloats = checked(frames * 2);
        lock (_lock)
        {
            if (_scratch.Length != requiredFloats)
                _scratch = new float[requiredFloats];
            else
                Array.Clear(_scratch);

            _activeKeys.Clear();
            foreach (long id in _voices.Keys)
                _activeKeys.Add(id);

            foreach (long id in _activeKeys)
            {
                ref Voice voice = ref CollectionsMarshal.GetValueRefOrNullRef(_voices, id);
                if (Unsafe.IsNullRef(ref voice) || voice.Paused)
                    continue;

                float[] source = voice.Samples;
                int total = source.Length / 2;
                if (total <= 0)
                    continue;

                int written = 0;
                while (written < frames)
                {
                    if (voice.Frame >= total)
                    {
                        if (!voice.Loop)
                            break;
                        voice.Frame = 0;
                    }

                    int run = Math.Min(frames - written, total - voice.Frame);
                    if (voice.Outdoors && _muffleCoefficient < 1f)
                    {
                        float a = _muffleCoefficient;
                        float leftGain = voice.Left * _muffleGain;
                        float rightGain = voice.Right * _muffleGain;
                        float low0 = voice.LowLeft;
                        float low1 = voice.LowRight;
                        for (int offset = 0; offset < run; offset++)
                        {
                            int input = (voice.Frame + offset) * 2;
                            int output = (written + offset) * 2;
                            low0 += a * (source[input] - low0);
                            low1 += a * (source[input + 1] - low1);
                            _scratch[output] += low0 * leftGain;
                            _scratch[output + 1] += low1 * rightGain;
                        }
                        voice.LowLeft = low0;
                        voice.LowRight = low1;
                        voice.Frame += run;
                        written += run;
                        continue;
                    }

                    for (int offset = 0; offset < run; offset++)
                    {
                        int input = (voice.Frame + offset) * 2;
                        int output = (written + offset) * 2;
                        _scratch[output] += source[input] * voice.Left;
                        _scratch[output + 1] += source[input + 1] * voice.Right;
                    }
                    voice.Frame += run;
                    written += run;
                }

                if (voice.Frame >= total && !voice.Loop)
                {
                    _voices.Remove(id);
                    _ended.Add(id);
                }
            }

            for (int i = 0; i < _scratch.Length; i++)
                _scratch[i] = Math.Clamp(_scratch[i], -1f, 1f);

            fixed (float* ptr = _scratch)
            {
                _ = SDL_PutAudioStreamData(stream, (nint)ptr, _scratch.Length * sizeof(float));
            }
        }
    }
}

public sealed class SDLAudioPlayer : IDisposable
{
    private const int Rate = 44_100;
    private const long LoadLoopVoice = long.MinValue + 1;
    private const int MaxCacheEntries = 96;

    private readonly string _root;
    private readonly AudioMixer _mixer = new();
    private readonly Dictionary<long, AudioEvent> _events = new();
    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private GCHandle _mixerHandle;
    private nint _stream;
    private bool _audioSubSystemInitialized;
    private int _disposed;

    public Action<long>? Ended { get; set; }
    public Func<int, bool, bool, (float Pan, float Gain)>? Spatial { get; set; }

    public unsafe SDLAudioPlayer(string session)
    {
        ArgumentException.ThrowIfNullOrEmpty(session);
        _root = ResolveRealPath(session);

        if (!SDL_InitSubSystem(SDL_INIT_AUDIO))
        {
            GC.SuppressFinalize(this);
            throw new SDLError("SDL_InitSubSystem(audio)");
        }
        _audioSubSystemInitialized = true;

        var spec = new SDL_AudioSpec
        {
            format = SDL_AUDIO_F32,
            channels = 2,
            freq = Rate,
        };
        _mixerHandle = GCHandle.Alloc(_mixer);
        delegate* unmanaged[Cdecl]<nint, nint, int, int, void> callback = &MixAudio;
        _stream = SDL_OpenAudioDeviceStream(
            SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
            in spec,
            (nint)callback,
            GCHandle.ToIntPtr(_mixerHandle));
        if (_stream == nint.Zero)
        {
            if (_mixerHandle.IsAllocated)
                _mixerHandle.Free();
            SDL_QuitSubSystem(SDL_INIT_AUDIO);
            _audioSubSystemInitialized = false;
            GC.SuppressFinalize(this);
            throw new SDLError("SDL_OpenAudioDeviceStream");
        }

        _ = SDL_ResumeAudioStreamDevice(_stream);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MixAudio(nint userdata, nint stream, int additional, int total)
    {
        if (userdata == nint.Zero || stream == nint.Zero || additional <= 0)
            return;

        try
        {
            var handle = GCHandle.FromIntPtr(userdata);
            if (handle.Target is AudioMixer mixer)
                mixer.MixAndPut(stream, additional / 8);
        }
        catch
        {
        }
    }

    public void Close()
    {
        ReleaseNative();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => Close();

    ~SDLAudioPlayer() => ReleaseNative();

    private void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Destroys the audio stream before freeing the mixer handle to guarantee the audio callback thread
        // has stopped.
        nint stream = Interlocked.Exchange(ref _stream, nint.Zero);
        if (stream != nint.Zero)
            SDL_DestroyAudioStream(stream);

        _mixer.Clear();

        if (_mixerHandle.IsAllocated)
            _mixerHandle.Free();

        if (_audioSubSystemInitialized)
        {
            SDL_QuitSubSystem(SDL_INIT_AUDIO);
            _audioSubSystemInitialized = false;
        }
    }

    public void Receive(AudioEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        evt.Validate();
        if (evt.Command == AudioEvent.AudioCommand.Reset)
        {
            Stop();
            return;
        }

        if (evt.Voice is not long id)
            return;

        switch (evt.Command)
        {
            case AudioEvent.AudioCommand.Play:
            {
                if (evt.File is not string file)
                    return;

                string audioRoot = Path.TrimEndingDirectorySeparator(Path.Combine(_root, "audio")) + Path.DirectorySeparatorChar;
                string normalizedRelative = file.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalizedRelative))
                    throw new ContentError("Audio resource escapes session or exceeds size limit");

                string resolvedPath = ResolveRealPath(Path.Combine(_root, normalizedRelative));
                var prefixComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!resolvedPath.StartsWith(audioRoot, prefixComparison) || !File.Exists(resolvedPath))
                    throw new ContentError("Audio resource escapes session or exceeds size limit");

                if (new FileInfo(resolvedPath).Length > 64L * 1024 * 1024)
                    throw new ContentError("Audio resource escapes session or exceeds size limit");

                float[] samples = Decode(resolvedPath);
                if (samples.Length == 0 || samples.Length / 2 > Rate * 3600)
                    throw new ContentError("Invalid decoded audio duration");

                if (evt.Loop == true && evt.OwnerID is int owner && owner != 0 && evt.Group != "AMBIENCE")
                {
                    var duplicates = new List<long>();
                    foreach (var kv in _events)
                    {
                        if (kv.Key != id && kv.Value.Loop == true && kv.Value.OwnerID == owner && kv.Value.File == file)
                            duplicates.Add(kv.Key);
                    }
                    foreach (long duplicateId in duplicates)
                    {
                        _mixer.Set(duplicateId, null);
                        _events.Remove(duplicateId);
                    }
                }

                if (_mixer.EvictIfFull(256, LoadLoopVoice) is long evicted)
                {
                    _events.Remove(evicted);
                    Ended?.Invoke(evicted);
                }

                _events[id] = evt;
                var voice = new AudioMixer.Voice(samples);
                Configure(ref voice, evt);
                _mixer.Set(id, voice);
                break;
            }
            case AudioEvent.AudioCommand.Update:
            {
                if (!_events.ContainsKey(id))
                    return;
                _events[id] = evt;
                _mixer.Update(id, (ref AudioMixer.Voice v) => Configure(ref v, evt));
                break;
            }
            case AudioEvent.AudioCommand.Pause:
                _mixer.Update(id, static (ref AudioMixer.Voice v) => v.Paused = true);
                break;
            case AudioEvent.AudioCommand.Resume:
                _mixer.Update(id, static (ref AudioMixer.Voice v) => v.Paused = false);
                break;
            case AudioEvent.AudioCommand.Stop:
                _mixer.Set(id, null);
                _events.Remove(id);
                break;
            case AudioEvent.AudioCommand.Reset:
                break;
        }
    }

    private void Configure(ref AudioMixer.Voice voice, AudioEvent evt)
    {
        (float Pan, float Gain)? position = (evt.Spatial == true || evt.Zoom == true) && (evt.OwnerID ?? -1) > 0
            ? Spatial?.Invoke(evt.OwnerID!.Value, evt.Spatial ?? false, evt.Zoom ?? false)
            : null;
        float volume = (evt.Volume ?? 1f) * (position?.Gain ?? 1f);
        float pan = evt.Spatial == true ? position?.Pan ?? evt.Pan ?? 0f : evt.Pan ?? 0f;
        voice.Left = volume * (pan > 0f ? 1f - pan : 1f);
        voice.Right = volume * (pan < 0f ? 1f + pan : 1f);
        voice.Loop = evt.Loop == true;
        voice.Outdoors = evt.Group == "AMBIENCE" || evt.Spatial == true;
    }

    public void SetEnvironmentAudio(float gain, float cutoff)
    {
        float coefficient = cutoff <= 0f ? 1f : 1f - MathF.Exp(-2f * MathF.PI * cutoff / Rate);
        _mixer.SetMuffle(Math.Clamp(gain, 0f, 1f), coefficient);
    }

    public void UpdateSpatial()
    {
        foreach (var kv in _events)
        {
            if (kv.Value.Spatial == true || kv.Value.Zoom == true)
            {
                AudioEvent evt = kv.Value;
                _mixer.Update(kv.Key, (ref AudioMixer.Voice v) => Configure(ref v, evt));
            }
        }
    }

    public void Poll()
    {
        foreach (long id in _mixer.TakeEnded())
        {
            _events.Remove(id);
            Ended?.Invoke(id);
        }
    }

    public void PlayLoadLoop(string path, float volume = 1f)
    {
        try
        {
            string full = ResolveRealPath(path);
            if (!File.Exists(full))
                return;
            float[] samples = Decode(full);
            if (samples.Length == 0)
                return;
            var voice = new AudioMixer.Voice(samples)
            {
                Left = volume,
                Right = volume,
                Loop = true,
            };
            _mixer.Set(LoadLoopVoice, voice);
        }
        catch
        {
        }
    }

    public void StopLoadLoop() => _mixer.Set(LoadLoopVoice, null);

    public void Stop()
    {
        _mixer.Clear();
        _events.Clear();
        nint stream = _stream;
        if (stream != nint.Zero)
            _ = SDL_ClearAudioStream(stream);
    }

    private unsafe float[] Decode(string path)
    {
        if (_cache.TryGetValue(path, out float[]? cached))
            return cached;

        byte[] data = File.ReadAllBytes(path);
        SDL_AudioSpec source;
        float[] samples;
        if (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            float[] pcm;
            int channels;
            int sampleRate;
            try
            {
                using var ms = new MemoryStream(data, writable: false);
                using var mp3 = new MpegFile(ms);
                channels = mp3.Channels;
                sampleRate = mp3.SampleRate;
                if (channels <= 0 || sampleRate <= 0)
                    throw new ContentError($"Cannot decode {Path.GetFileName(path)}");

                var chunks = new List<float[]>();
                int totalSamples = 0;
                const int chunkSize = 32768;
                while (true)
                {
                    float[] buf = new float[chunkSize];
                    int read = mp3.ReadSamples(buf, 0, buf.Length);
                    if (read <= 0)
                        break;
                    if (read < chunkSize)
                        Array.Resize(ref buf, read);
                    chunks.Add(buf);
                    totalSamples = checked(totalSamples + read);
                }

                pcm = new float[totalSamples];
                int offset = 0;
                foreach (float[] chunk in chunks)
                {
                    chunk.CopyTo(pcm, offset);
                    offset += chunk.Length;
                }
            }
            catch (Exception ex) when (ex is not ContentError)
            {
                throw new ContentError($"Cannot decode {Path.GetFileName(path)}: {ex.Message}");
            }

            if (pcm.Length == 0)
                throw new ContentError($"Cannot decode {Path.GetFileName(path)}");

            source = new SDL_AudioSpec
            {
                format = SDL_AUDIO_F32,
                channels = channels,
                freq = sampleRate,
            };
            fixed (float* pcmPtr = pcm)
            {
                samples = ConvertSamples(ref source, (nint)pcmPtr, checked(pcm.Length * sizeof(float)));
            }
        }
        else
        {
            if (data.Length == 0)
                throw new ContentError($"Cannot decode {Path.GetFileName(path)}: empty audio data");

            fixed (byte* dataPtr = data)
            {
                nint io = SDL_IOFromConstMem((nint)dataPtr, (nuint)data.Length);
                if (io == nint.Zero || !SDL_LoadWAV_IO(io, true, out source, out byte* buffer, out uint length) || buffer == null)
                    throw new ContentError($"Cannot decode {Path.GetFileName(path)}: {SDL_GetError()}");
                try
                {
                    samples = ConvertSamples(ref source, (nint)buffer, checked((int)length));
                }
                finally
                {
                    SDL_free((nint)buffer);
                }
            }
        }

        _cache[path] = samples;
        _cacheOrder.Enqueue(path);
        if (_cacheOrder.Count > MaxCacheEntries)
            _cache.Remove(_cacheOrder.Dequeue());
        return samples;
    }

    private unsafe float[] ConvertSamples(ref SDL_AudioSpec source, nint inputPtr, int bytes)
    {
        if (bytes <= 0 || inputPtr == nint.Zero)
            throw new ContentError($"Cannot convert audio: {SDL_GetError()}");

        var target = new SDL_AudioSpec
        {
            format = SDL_AUDIO_F32,
            channels = 2,
            freq = Rate,
        };
        if (!SDL_ConvertAudioSamples(in source, inputPtr, bytes, in target, out byte* output, out int length) || output == null)
            throw new ContentError($"Cannot convert audio: {SDL_GetError()}");
        try
        {
            int count = length / sizeof(float);
            float[] result = new float[count];
            if (count > 0)
                new ReadOnlySpan<float>(output, count).CopyTo(result);
            return result;
        }
        finally
        {
            SDL_free((nint)output);
        }
    }

    private static string ResolveRealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        string relative = Path.GetRelativePath(root, full);
        if (relative == ".")
            return root;

        string current = root;
        string[] segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.GetFullPath(Path.Combine(current, segments[i]));
            try
            {
                FileSystemInfo? target = File.Exists(current)
                    ? File.ResolveLinkTarget(current, returnFinalTarget: true)
                    : Directory.Exists(current)
                        ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                        : null;
                if (target is not null)
                    current = Path.GetFullPath(target.FullName);
            }
            catch
            {
            }
        }

        return current;
    }
}
