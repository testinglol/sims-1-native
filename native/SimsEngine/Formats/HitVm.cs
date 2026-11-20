using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Stateful HIT bytecode VM executing 0x01-0x53 opcodes, SmartLists, register fades,
/// seqgroups, ducking, and sample-end waits per 0057e3dc.c, 0057dac6.c, and 0057dbfe.c.</summary>
public sealed class HitVm
{
    public const float SecondsPerTick = 0.005f; // 200 Hz HIT clock (0057163b.c:462)
    public const int MaxInstructionsPerTick = 100; // 0057dbfe.c:14
    public const int MaxTicksPerStep = 40;

    public sealed class TrackThread
    {
        public required int InstanceId { get; init; }
        public required HotTrackDef Track { get; init; }
        public required HitBankModule Bank { get; init; }
        public HitBankModule CodeBank { get; init; } = null!;
        public required string EventName { get; init; }
        public int[] Registers { get; } = new int[0x64];
        public int Pc { get; set; }
        public int LoopPoint { get; set; }
        public Stack<int> CallStack { get; } = new();
        public bool Zf { get; set; }
        public bool Gt { get; set; }
        public bool Lt { get; set; }
        public long WaitUntilTick { get; set; }
        public long NoteOffTick { get; set; }
        public bool WaitingForSample { get; set; }
        public bool FadeKillPending { get; set; }
        public int TargetThreadOrTrackId { get; set; }
        public int SourceObjectId { get; set; }
        public int VoiceOwnerId { get; set; }
        public int? ParentThreadId { get; set; }
        public long ActiveVoiceId { get; set; }
        public bool PendingVoiceBind { get; set; }
        public HitSmartList LocalSmartList { get; } = new();
        public HitSmartList ActiveSmartList { get; set; }
        public bool Killed { get; set; }

        public TrackThread()
        {
            ActiveSmartList = LocalSmartList;
        }
    }

    private readonly List<HitBankModule> modules = [];
    private readonly Dictionary<int, HotTrackDef> tracksById = [];
    private readonly Dictionary<int, HotPatchDef> patchesById = [];
    private readonly Dictionary<HitBankModule, Dictionary<int, HitSmartList>> moduleSmartLists = [];
    private readonly Dictionary<int, HitSmartList> extraSmartLists = [];
    private readonly Dictionary<int, HitSmartList> globalSmartLists = [];
    private readonly Dictionary<int, TrackThread> threads = [];
    private readonly Dictionary<long, int> voiceToThread = [];
    private readonly Dictionary<int, int> seqGroups = []; // groupId -> thread.InstanceId
    private readonly Dictionary<int, int> duckers = []; // thread.InstanceId -> duckPriority
    private readonly int[] globalRegisters = new int[0x20]; // 0x64..0x7f
    private readonly Func<int, int, int>? getSourceDataField;
    private readonly Random rng;
    private readonly Queue<HitVmCommand> pendingCommands = new();

    private long currentTick = 1;
    private float tickAccumulator;
    private int nextThreadId = 1;
    private readonly HitSmartList emptySmartList = new();

    public long CurrentTick => currentTick;
    public int ActiveThreadCount => threads.Count;
    public int MainDuckPriority => globalRegisters[HitReg.MainDuckPri - 0x64];
    public bool IsPaused { get; set; }
    public int? CurrentThreadId { get; private set; }
    public event Action<TrackThread, TrackThread?>? ThreadStarted;
    public event Action<int>? ThreadKilled;

    public HitVm(
        IEnumerable<HitBankModule> bankModules,
        IReadOnlyDictionary<int, (int[] Items, int? ExplicitCrush)>? extraHitLists = null,
        IReadOnlyDictionary<int, HotPatchDef>? extraPatches = null,
        Func<int, int, int>? getSourceDataField = null,
        Random? rng = null)
    {
        this.getSourceDataField = getSourceDataField;
        this.rng = rng ?? new Random();

        globalRegisters[HitReg.SimSpeed - 0x64] = 1;
        globalRegisters[HitReg.MainVol - 0x64] = HitReg.DefaultVolume;

        foreach (var mod in bankModules)
        {
            modules.Add(mod);
            foreach (var (tid, trk) in mod.Tracks)
            {
                tracksById[tid] = trk;
            }
            foreach (var (pid, pch) in mod.Patches)
            {
                patchesById[pid] = pch;
            }
            var modLists = new Dictionary<int, HitSmartList>();
            foreach (var (hid, items) in mod.GlobalHitLists)
            {
                if (items.Length > 0)
                {
                    var list = new HitSmartList(items);
                    modLists[hid] = list;
                    globalSmartLists[hid] = list;
                }
            }
            moduleSmartLists[mod] = modLists;
        }

        if (extraPatches != null)
        {
            foreach (var (pid, pch) in extraPatches)
            {
                patchesById[pid] = pch;
            }
        }

        if (extraHitLists != null)
        {
            foreach (var (hid, (items, crush)) in extraHitLists)
            {
                extraSmartLists[hid] = new HitSmartList(items, crush);
            }
        }
    }

    public int GetGlobalRegister(int reg)
    {
        if (reg is >= 0x64 and <= 0x7f)
            return globalRegisters[reg - 0x64];
        return 0;
    }

    public void SetGlobalRegister(int reg, int value)
    {
        if (reg is >= 0x64 and <= 0x7f)
            globalRegisters[reg - 0x64] = value;
    }

    public TrackThread? GetThread(int instanceId) => threads.GetValueOrDefault(instanceId);

    public IReadOnlyList<HitVmCommand> DrainCommands()
    {
        if (pendingCommands.Count == 0) return [];
        var list = new List<HitVmCommand>(pendingCommands.Count);
        while (pendingCommands.Count > 0)
            list.Add(pendingCommands.Dequeue());
        return list;
    }

    public TrackThread? StartTrack(
        HotTrackDef track,
        int arg1 = 0,
        int arg2 = 0,
        int arg3 = 0,
        int arg4 = 0,
        string? eventName = null,
        bool runInitialBurst = true,
        int? voiceOwnerId = null)
    {
        var bank = track.Bank ?? modules.FirstOrDefault();
        var codeBank = track.CodeBank ?? bank;
        if (bank == null || codeBank == null) return null;
        if (track.EntryOffset is not { } entryOffset || entryOffset < 0 || entryOffset >= codeBank.HitBytes.Length)
            return null;

        var thread = new TrackThread
        {
            InstanceId = nextThreadId++,
            Track = track,
            Bank = bank,
            CodeBank = codeBank,
            EventName = eventName ?? track.Symbol,
            Pc = entryOffset,
            LoopPoint = entryOffset,
            SourceObjectId = arg1,
            VoiceOwnerId = voiceOwnerId ?? (track.IsPositionalObjectTrack ? arg1 : -1)
        };

        thread.Registers[HitReg.Arg1] = arg1;
        thread.Registers[HitReg.Arg2] = arg2;
        thread.Registers[HitReg.Arg3] = arg3;
        thread.Registers[HitReg.Arg4] = arg4;
        thread.Registers[HitReg.Priority] = 0x20;
        thread.Registers[HitReg.Vol] = track.InitialVol > 0 ? track.InitialVol : HitReg.DefaultVolume;
        thread.Registers[HitReg.ExtVol] = HitReg.DefaultVolume;
        thread.Registers[HitReg.Pan] = HitReg.DefaultPan;
        thread.Registers[HitReg.Pitch] = HitReg.DefaultPitch;
        thread.Registers[HitReg.DuckPri] = track.DuckPriority;
        thread.Registers[HitReg.MinDistance] = 100;
        thread.Registers[HitReg.MaxDistance] = 10000;
        thread.Registers[HitReg.IsLooped] = track.IsLoopedHint ? 1 : 0;
        thread.Registers[HitReg.Source] = arg1;
        thread.Registers[HitReg.Patch] = track.InitialPatchId;
        thread.Registers[HitReg.Spl] = track.Spl > 0 ? track.Spl : 20;

        threads[thread.InstanceId] = thread;

        if (runInitialBurst)
        {
            StepThread(thread);
        }

        return thread;
    }

    public void RunInitialBurst(TrackThread thread) => StepThread(thread);

    public void BindVoice(int threadId, long voiceId)
    {
        if (threads.TryGetValue(threadId, out var thread))
        {
            if (thread.ActiveVoiceId != 0 && thread.ActiveVoiceId != voiceId)
            {
                voiceToThread.Remove(thread.ActiveVoiceId);
            }
            thread.PendingVoiceBind = false;
            thread.ActiveVoiceId = voiceId;
            thread.Registers[HitReg.IsPlaying] = 1;
            voiceToThread[voiceId] = threadId;
        }
    }

    public void ClearPendingVoiceBind(int threadId)
    {
        if (threads.TryGetValue(threadId, out var thread))
        {
            thread.PendingVoiceBind = false;
        }
    }

    public bool OnVoiceEnded(long voiceId)
    {
        if (!voiceToThread.Remove(voiceId, out var threadId))
            return false;

        if (!threads.TryGetValue(threadId, out var thread) || thread.Killed)
            return false;

        if (thread.ActiveVoiceId == voiceId)
        {
            thread.ActiveVoiceId = 0;
            thread.Registers[HitReg.IsPlaying] = 0;
        }

        if (thread.WaitingForSample || IsPreSampleWait(thread))
        {
            thread.WaitingForSample = false;
            thread.WaitUntilTick = currentTick;
            StepThread(thread);
        }

        return true;
    }

    private static bool IsPreSampleWait(TrackThread thread)
    {
        // Station/TV tracks execute `wait 250` before `wait_samp` (0x0b) while a voice is playing.
        // If the voice ends early (e.g., short clip or radio_next_song), resume directly into wait_samp.
        var hit = thread.CodeBank.HitBytes;
        return thread.Pc >= 0 && thread.Pc < hit.Length && hit[thread.Pc] == 0x0b;
    }

    public void KillThreadsForOwner(int ownerId)
    {
        foreach (var thread in threads.Values.Where(t => t.VoiceOwnerId == ownerId).ToArray())
        {
            KillThread(thread);
        }
    }

    public void KillThreadsForTrackId(int trackId)
    {
        foreach (var thread in threads.Values.Where(t => t.Track.TrackId == trackId || t.InstanceId == trackId).ToArray())
        {
            KillThread(thread);
        }
    }

    public void KillThread(int threadId)
    {
        if (threads.TryGetValue(threadId, out var thread))
        {
            KillThread(thread);
        }
    }

    public void Reset()
    {
        foreach (var thread in threads.Values.ToArray())
        {
            if (thread.ActiveVoiceId != 0)
            {
                pendingCommands.Enqueue(new HitStopVoiceCommand(thread.InstanceId, thread.ActiveVoiceId));
            }
        }
        threads.Clear();
        voiceToThread.Clear();
        seqGroups.Clear();
        tickAccumulator = 0f;
        if (duckers.Count > 0)
        {
            duckers.Clear();
            globalRegisters[HitReg.MainDuckPri - 0x64] = 0;
        }
    }

    public void Tick(float deltaSeconds)
    {
        if (IsPaused || deltaSeconds <= 0f) return;
        tickAccumulator += deltaSeconds;
        int steps = 0;
        while (tickAccumulator >= SecondsPerTick && steps < MaxTicksPerStep)
        {
            tickAccumulator -= SecondsPerTick;
            StepOneTick();
            steps++;
        }
        if (tickAccumulator > SecondsPerTick * MaxTicksPerStep)
        {
            tickAccumulator = 0f;
        }
    }

    public void StepTicks(int tickCount)
    {
        for (int i = 0; i < tickCount; i++)
        {
            StepOneTick();
        }
    }

    private void StepOneTick()
    {
        currentTick++;
        foreach (var thread in threads.Values.ToArray())
        {
            if (thread.Killed) continue;
            if (thread.Registers[HitReg.Paused] != 0) continue;

            StepThreadFade(thread);
            if (thread.Killed) continue;

            if (thread.FadeKillPending && thread.Registers[HitReg.FadeOn] == 0)
            {
                thread.FadeKillPending = false;
                KillThread(thread);
                continue;
            }

            if (thread.NoteOffTick != 0 && currentTick >= thread.NoteOffTick)
            {
                thread.NoteOffTick = 0;
                if (thread.ActiveVoiceId != 0)
                {
                    pendingCommands.Enqueue(new HitStopVoiceCommand(thread.InstanceId, thread.ActiveVoiceId));
                    voiceToThread.Remove(thread.ActiveVoiceId);
                    thread.ActiveVoiceId = 0;
                    thread.Registers[HitReg.IsPlaying] = 0;
                }
            }

            if (thread.WaitUntilTick != 0 && currentTick >= thread.WaitUntilTick)
            {
                StepThread(thread);
            }
        }
    }

    private void StepThreadFade(TrackThread thread)
    {
        // 0057dac6.c:17-36
        if (thread.Registers[HitReg.FadeOn] == 0) return;

        int targetReg = thread.Registers[HitReg.FadeVar];
        if (!IsValidRegister(targetReg))
        {
            KillThread(thread);
            return;
        }
        int dest = thread.Registers[HitReg.FadeDest];
        int speed = thread.Registers[HitReg.FadeSpeed];
        int current = GetReg(thread, targetReg, forceLocal: false);
        if (thread.Killed) return;

        if (speed == 0)
        {
            thread.Registers[HitReg.FadeOn] = 0;
            return;
        }

        if ((current < dest) != (speed > 0))
        {
            speed = -speed;
        }

        int next = current + speed;
        if ((speed > 0 && next > dest) || (speed < 0 && next < dest) || next == dest)
        {
            thread.Registers[HitReg.FadeOn] = 0;
            next = dest;
        }

        SetReg(thread, targetReg, next, forceLocal: false);
    }

    private void StepThread(TrackThread thread)
    {
        if (thread.Killed) return;
        thread.WaitUntilTick = 0;
        thread.WaitingForSample = false;

        var hit = thread.CodeBank.HitBytes;
        if (hit.Length == 0 || thread.Pc < 0 || thread.Pc >= hit.Length)
        {
            KillThread(thread);
            return;
        }

        int? prevThreadId = CurrentThreadId;
        CurrentThreadId = thread.InstanceId;
        try
        {
            for (int budget = 0; budget < MaxInstructionsPerTick; budget++)
            {
                if (thread.Killed || thread.WaitUntilTick != 0 || thread.Pc < 0 || thread.Pc >= hit.Length)
                    break;

                if (!ExecuteSingleInstruction(thread, hit))
                    break;
            }

            if (!thread.Killed && thread.WaitUntilTick == 0)
            {
                if (thread.Pc < 0 || thread.Pc >= hit.Length)
                    KillThread(thread);
                else
                    thread.WaitUntilTick = currentTick + 1;
            }
        }
        finally
        {
            CurrentThreadId = prevThreadId;
        }
    }

    private bool ExecuteSingleInstruction(TrackThread thread, byte[] hit)
    {
        int instrStart = thread.Pc;
        byte op = hit[thread.Pc++];

        switch (op)
        {
            case 0x01: // note r1, r2
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                int r1 = hit[thread.Pc++];
                int r2 = hit[thread.Pc++];
                if (!TryReadReg(thread, r1, false, out int noteArg) || !TryReadReg(thread, r2, false, out int dur))
                    return false;
                thread.NoteOffTick = currentTick + Math.Max(0, dur);
                EmitNoteOn(thread, noteArg);
                return true;
            }
            case 0x02: // note_on b1
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte b1 = hit[thread.Pc++];
                EmitNoteOn(thread, b1);
                return true;
            }
            case 0x03: // note_off b1
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                thread.Pc++;
                StopThreadVoice(thread);
                return true;
            }
            case 0x04: // loadb dest, u8
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte val = hit[thread.Pc++];
                return TryWriteReg(thread, dest, val, false);
            }
            case 0x05: // loadl dest, i32
            {
                if (thread.Pc + 5 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                int val = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                return TryWriteReg(thread, dest, val, false);
            }
            case 0x06: // set dest, src
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, src, false, out int val)) return false;
                return TryWriteReg(thread, dest, val, false);
            }
            case 0x07: // call i32 tkdId
            {
                if (thread.Pc + 4 > hit.Length) { KillThread(thread); return false; }
                int tkdId = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                if (ResolveTrackDataOffset(thread.CodeBank, tkdId, out int targetPc))
                {
                    thread.CallStack.Push(thread.Pc);
                    thread.Pc = targetPc;
                }
                else
                {
                    KillThread(thread);
                    return false;
                }
                return true;
            }
            case 0x08: // return
            case 0x0c: // end
            {
                if (thread.CallStack.Count > 0)
                {
                    thread.Pc = thread.CallStack.Pop();
                    return true;
                }
                KillThread(thread);
                return false;
            }
            case 0x09: // wait src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int rawTicks)) return false;
                int ticks = Math.Max(0, rawTicks);
                if (ticks == 0) return true;
                thread.WaitUntilTick = currentTick + ticks;
                return false;
            }
            case 0x0b: // wait_samp
            {
                if (thread.ActiveVoiceId == 0 && !thread.PendingVoiceBind)
                    return true;
                thread.Pc = instrStart;
                thread.WaitingForSample = true;
                thread.WaitUntilTick = currentTick + 2;
                return false;
            }
            case 0x0d: // jump i32 tkdId
            {
                if (thread.Pc + 4 > hit.Length) { KillThread(thread); return false; }
                int tkdId = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                if (ResolveTrackDataOffset(thread.CodeBank, tkdId, out int targetPc))
                {
                    thread.Pc = targetPc;
                    return true;
                }
                KillThread(thread);
                return false;
            }
            case 0x0e: // test src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int v)) return false;
                thread.Zf = v == 0;
                thread.Gt = v > 0;
                thread.Lt = v < 0;
                return true;
            }
            case 0x0f: // nop
            case 0x1d: // add_to_group
            case 0x1e: // remove_from_group
            case 0x46: // seqgroup_wait
                return true;
            case 0x10: // add
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a + b, false);
            }
            case 0x11: // sub
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a - b, false);
            }
            case 0x12: // div
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int dividend) || !TryReadReg(thread, src, false, out int divisor)) return false;
                if (divisor != 0)
                {
                    return TryWriteReg(thread, dest, dividend / divisor, false);
                }
                return true;
            }
            case 0x13: // mul
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a * b, false);
            }
            case 0x14: // cmp r1, r2
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte r1 = hit[thread.Pc++];
                byte r2 = hit[thread.Pc++];
                if (!TryReadReg(thread, r1, false, out int a) || !TryReadReg(thread, r2, false, out int b)) return false;
                thread.Zf = a == b;
                thread.Gt = a > b;
                thread.Lt = a < b;
                return true;
            }
            case 0x15: // less dest, src
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a < b ? 1 : 0, false);
            }
            case 0x16: // greater dest, src
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a > b ? 1 : 0, false);
            }
            case 0x17: // not dest
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a)) return false;
                return TryWriteReg(thread, dest, a == 0 ? 1 : 0, false);
            }
            case 0x18: // rand dest, minReg, maxReg
            {
                if (thread.Pc + 3 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte minR = hit[thread.Pc++];
                byte maxR = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, minR, false, out int minVal) || !TryReadReg(thread, maxR, false, out int maxVal)) return false;
                int span = maxVal - minVal;
                int chosen = span > 0 ? minVal + rng.Next(span) : minVal;
                return TryWriteReg(thread, dest, chosen, false);
            }
            case 0x19: // abs dest
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int val)) return false;
                return TryWriteReg(thread, dest, val == int.MinValue ? int.MaxValue : Math.Abs(val), false);
            }
            case 0x1b: // error
            {
                KillThread(thread);
                return false;
            }
            case 0x1c: // assert src
            {
                if (thread.Pc + 1 <= hit.Length) thread.Pc++;
                KillThread(thread);
                return false;
            }
            case 0x1f: // get_var dest, src
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, src, false, out int indirectReg)) return false;
                if (!TryReadReg(thread, indirectReg, false, out int indirectVal)) return false;
                return TryWriteReg(thread, dest, indirectVal, false);
            }
            case 0x20: // loop
            {
                thread.Pc = thread.LoopPoint;
                return true;
            }
            case 0x21: // set_loop
            {
                thread.LoopPoint = thread.Pc;
                return true;
            }
            case 0x22: // callback r1, r2
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte r1 = hit[thread.Pc++];
                byte r2 = hit[thread.Pc++];
                if (!IsValidRegister(r1) || !IsValidRegister(r2)) { KillThread(thread); return false; }
                return true;
            }
            case 0x23: // smart_add src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int val)) return false;
                thread.ActiveSmartList.Add(val);
                return true;
            }
            case 0x24: // smart_remove src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int val)) return false;
                thread.ActiveSmartList.Remove(val);
                return true;
            }
            case 0x25: // smart_removeall
            {
                thread.ActiveSmartList.Clear();
                return true;
            }
            case 0x26: // smart_setcrit src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int val)) return false;
                thread.ActiveSmartList.SetCrush(val);
                return true;
            }
            case 0x27: // smart_choose dest
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                int? chosen = thread.ActiveSmartList.Choose(rng);
                if (chosen.HasValue)
                {
                    return TryWriteReg(thread, dest, chosen.Value, false);
                }
                return true;
            }
            case 0x28: // and (a > b per 0057e3dc.c:444-449)
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a > b ? 1 : 0, false);
            }
            case 0x29: // nand
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, (a == 0 || b == 0) ? 1 : 0, false);
            }
            case 0x2a: // or
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a | b, false);
            }
            case 0x2b: // nor
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, (a == 0 && b == 0) ? 1 : 0, false);
            }
            case 0x2c: // xor
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a) || !TryReadReg(thread, src, false, out int b)) return false;
                return TryWriteReg(thread, dest, a ^ b, false);
            }
            case 0x2d: // max (cap to min(reg, imm32) per 0057e3dc.c:526-540)
            {
                if (thread.Pc + 5 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                int imm = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                if (!TryReadReg(thread, dest, false, out int a)) return false;
                return TryWriteReg(thread, dest, Math.Min(a, imm), false);
            }
            case 0x2e: // min (floor to max(reg, imm32) per 0057e3dc.c:541-555)
            {
                if (thread.Pc + 5 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                int imm = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                if (!TryReadReg(thread, dest, false, out int a)) return false;
                return TryWriteReg(thread, dest, Math.Max(a, imm), false);
            }
            case 0x2f: // inc dest
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a)) return false;
                return TryWriteReg(thread, dest, a + 1, false);
            }
            case 0x30: // dec dest
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                if (!TryReadReg(thread, dest, false, out int a)) return false;
                return TryWriteReg(thread, dest, a - 1, false);
            }
            case 0x31: // printreg src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(src)) { KillThread(thread); return false; }
                return true;
            }
            case 0x32: // play_trk src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int targetTrkId)) return false;
                if (TryFindTrack(thread.Bank, targetTrkId, out var childTrack))
                {
                    var child = StartTrack(
                        childTrack,
                        GetReg(thread, HitReg.Arg1, true),
                        GetReg(thread, HitReg.Arg2, true),
                        GetReg(thread, HitReg.Arg3, true),
                        0,
                        childTrack.Symbol,
                        runInitialBurst: false,
                        voiceOwnerId: childTrack.IsPositionalObjectTrack ? thread.VoiceOwnerId : -1);
                    if (child != null)
                    {
                        child.ParentThreadId = thread.InstanceId;
                        child.SourceObjectId = GetReg(thread, HitReg.Source, false);
                        child.Registers[HitReg.Source] = child.SourceObjectId;
                        ThreadStarted?.Invoke(child, thread);
                        StepThread(child);
                    }
                }
                return true;
            }
            case 0x33: // kill_trk src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int targetId)) return false;
                KillThreadsForTrackId(targetId);
                return true;
            }
            case 0x3a: // test1
                return true;
            case 0x3b: // test2 (0xe10)
                SetReg(thread, HitReg.Pitch, 0xe10, false);
                return true;
            case 0x3c: // test3 (0x1004)
                SetReg(thread, HitReg.Pitch, 0x1004, false);
                return true;
            case 0x3d: // test4 (0x1210)
                SetReg(thread, HitReg.Pitch, 0x1210, false);
                return true;
            case 0x3e: // ifeq
            case 0x3f: // ifne
            case 0x40: // ifgt
            case 0x41: // iflt
            case 0x42: // ifge
            case 0x43: // ifle
            {
                if (thread.Pc + 4 > hit.Length) { KillThread(thread); return false; }
                int tkdId = BinaryPrimitives.ReadInt32LittleEndian(hit.AsSpan(thread.Pc, 4));
                thread.Pc += 4;
                bool cond = op switch
                {
                    0x3e => thread.Zf,
                    0x3f => !thread.Zf,
                    0x40 => thread.Gt && !thread.Zf,
                    0x41 => thread.Lt && !thread.Zf,
                    0x42 => thread.Gt || thread.Zf,
                    0x43 => thread.Lt || thread.Zf,
                    _ => false
                };
                if (cond && ResolveTrackDataOffset(thread.CodeBank, tkdId, out int targetPc))
                {
                    thread.Pc = targetPc;
                }
                return true;
            }
            case 0x44: // smart_setlist src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int listId)) return false;
                if (listId == 0)
                {
                    thread.ActiveSmartList = thread.LocalSmartList;
                }
                else if (moduleSmartLists.TryGetValue(thread.Bank, out var bankLists) && bankLists.TryGetValue(listId, out var bankList))
                {
                    thread.ActiveSmartList = bankList;
                }
                else if (thread.Bank.GlobalHitLists.TryGetValue(listId, out var arr) && arr.Length > 0)
                {
                    var created = new HitSmartList(arr);
                    if (bankLists == null)
                    {
                        bankLists = [];
                        moduleSmartLists[thread.Bank] = bankLists;
                    }
                    bankLists[listId] = created;
                    globalSmartLists.TryAdd(listId, created);
                    thread.ActiveSmartList = created;
                }
                else if (extraSmartLists.TryGetValue(listId, out var extraList))
                {
                    thread.ActiveSmartList = extraList;
                }
                else if (globalSmartLists.TryGetValue(listId, out var list))
                {
                    thread.ActiveSmartList = list;
                }
                else
                {
                    thread.ActiveSmartList = emptySmartList;
                }
                return true;
            }
            case 0x45: // seqgroup_kill src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int grp)) return false;
                if (seqGroups.TryGetValue(grp, out var priorId) &&
                    priorId != 0 &&
                    priorId != thread.InstanceId &&
                    threads.TryGetValue(priorId, out var prior) &&
                    !prior.Killed)
                {
                    if (GetReg(prior, HitReg.ExtVol, false) == 0)
                    {
                        KillThread(prior);
                    }
                    else if (!prior.FadeKillPending)
                    {
                        SetReg(prior, HitReg.FadeSpeed, 200, false);
                        SetReg(prior, HitReg.FadeVar, HitReg.ExtVol, false);
                        SetReg(prior, HitReg.FadeDest, 0, false);
                        SetReg(prior, HitReg.FadeOn, 1, false);
                        prior.FadeKillPending = true;
                    }
                }
                seqGroups[grp] = thread.InstanceId;
                return true;
            }
            case 0x47: // seqgroup_return src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int grp)) return false;
                if (seqGroups.TryGetValue(grp, out var ownerId) &&
                    ownerId != 0 &&
                    ownerId != thread.InstanceId &&
                    threads.ContainsKey(ownerId))
                {
                    KillThread(thread);
                    return false;
                }
                seqGroups[grp] = thread.InstanceId;
                return true;
            }
            case 0x48: // getsrcdatafield dest, objReg, fieldReg
            {
                if (thread.Pc + 3 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte objReg = hit[thread.Pc++];
                byte fieldReg = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, objReg, false, out int objId) || !TryReadReg(thread, fieldReg, false, out int fieldId)) return false;
                int val = getSourceDataField?.Invoke(objId, fieldId) ?? 0;
                return TryWriteReg(thread, dest, val, false);
            }
            case 0x49: // seqgroup_trkid grpReg, dest (0057e3dc.c:791-800)
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte grpReg = hit[thread.Pc++];
                byte dest = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, grpReg, false, out int grp)) return false;
                int ownerTid = 0;
                if (seqGroups.TryGetValue(grp, out var ownerInst) && threads.TryGetValue(ownerInst, out var ownerThread))
                {
                    ownerTid = ownerThread.Track.TrackId;
                }
                return TryWriteReg(thread, dest, ownerTid, false);
            }
            case 0x4a: // setll (local -> local)
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, src, forceLocal: true, out int val)) return false;
                return TryWriteReg(thread, dest, val, forceLocal: true);
            }
            case 0x4b: // setlt (local -> target)
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, src, forceLocal: true, out int val)) return false;
                return TryWriteReg(thread, dest, val, forceLocal: false);
            }
            case 0x4c: // settl (target -> local)
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte dest = hit[thread.Pc++];
                byte src = hit[thread.Pc++];
                if (!IsValidRegister(dest)) { KillThread(thread); return false; }
                if (!TryReadReg(thread, src, forceLocal: false, out int val)) return false;
                return TryWriteReg(thread, dest, val, forceLocal: true);
            }
            case 0x4d: // waiteq
            case 0x4e: // waitne
            case 0x4f: // waitgt
            case 0x50: // waitlt
            case 0x51: // waitge
            case 0x52: // waitle
            {
                if (thread.Pc + 2 > hit.Length) { KillThread(thread); return false; }
                byte r1 = hit[thread.Pc++];
                byte r2 = hit[thread.Pc++];
                if (!TryReadReg(thread, r1, false, out int a) || !TryReadReg(thread, r2, false, out int b)) return false;
                bool satisfied = op switch
                {
                    0x4d => a == b,
                    0x4e => a != b,
                    0x4f => a > b,
                    0x50 => a < b,
                    0x51 => a >= b,
                    0x52 => a <= b,
                    _ => true
                };
                if (satisfied) return true;
                thread.Pc = instrStart;
                thread.WaitUntilTick = currentTick + 2;
                return false;
            }
            case 0x53: // chain_patch src
            {
                if (thread.Pc + 1 > hit.Length) { KillThread(thread); return false; }
                byte src = hit[thread.Pc++];
                if (!TryReadReg(thread, src, false, out int patchId)) return false;
                if (thread.ActiveVoiceId != 0)
                {
                    pendingCommands.Enqueue(new HitChainPatchCommand(thread.InstanceId, thread.ActiveVoiceId, patchId, thread.Bank));
                }
                else
                {
                    SetReg(thread, HitReg.Patch, patchId, false);
                    EmitNoteOn(thread, 0);
                }
                return true;
            }
            default:
            {
                KillThread(thread);
                return false;
            }
        }
    }

    private static bool IsValidRegister(int reg) => reg is >= 1 and <= 0x7f;

    private bool TryReadReg(TrackThread thread, int reg, bool forceLocal, out int value)
    {
        if (!IsValidRegister(reg))
        {
            KillThread(thread);
            value = 0;
            return false;
        }
        value = GetReg(thread, reg, forceLocal);
        return !thread.Killed;
    }

    private bool TryWriteReg(TrackThread thread, int reg, int value, bool forceLocal)
    {
        if (!IsValidRegister(reg))
        {
            KillThread(thread);
            return false;
        }
        SetReg(thread, reg, value, forceLocal);
        return !thread.Killed;
    }

    public int GetReg(TrackThread thread, int reg, bool forceLocal)
    {
        if (!IsValidRegister(reg))
        {
            KillThread(thread);
            return 0;
        }

        if (reg is >= 0x64 and <= 0x7f)
            return globalRegisters[reg - 0x64];

        if (!forceLocal && reg != HitReg.Target && thread.TargetThreadOrTrackId != 0)
        {
            var target = FindTargetThread(thread.TargetThreadOrTrackId);
            if (target != null && target != thread)
                return GetReg(target, reg, forceLocal: true);
        }

        if (reg is >= 1 and < 0x64)
            return thread.Registers[reg];

        return 0;
    }

    public void SetReg(TrackThread thread, int reg, int value, bool forceLocal)
    {
        if (!IsValidRegister(reg))
        {
            KillThread(thread);
            return;
        }

        thread.Zf = value == 0;
        thread.Gt = value > 0;
        thread.Lt = value < 0;

        if (reg is >= 0x64 and <= 0x7f)
        {
            if (reg == HitReg.MainDuckPri)
            {
                UpdateDucker(thread.InstanceId, value);
            }
            else
            {
                globalRegisters[reg - 0x64] = value;
                if (reg == HitReg.MainVol)
                {
                    RefreshAllVoiceVolumes();
                }
            }
            return;
        }

        if (!forceLocal && reg != HitReg.Target && thread.TargetThreadOrTrackId != 0)
        {
            var target = FindTargetThread(thread.TargetThreadOrTrackId);
            if (target != null && target != thread)
            {
                SetReg(target, reg, value, forceLocal: true);
                return;
            }
        }

        if (reg is < 1 or >= 0x64) return;

        thread.Registers[reg] = value;

        if (reg == HitReg.Target)
        {
            thread.TargetThreadOrTrackId = value;
            return;
        }

        if (thread.ActiveVoiceId != 0 && reg is HitReg.Vol or HitReg.ExtVol or HitReg.Pan or HitReg.Pitch or HitReg.DuckPri)
        {
            EmitVoiceUpdate(thread);
        }
    }

    private void UpdateDucker(int threadId, int duckValue)
    {
        if (duckValue == 0)
            duckers.Remove(threadId);
        else
            duckers[threadId] = duckValue;

        int maxDuck = 0;
        foreach (var v in duckers.Values)
        {
            if (v > maxDuck) maxDuck = v;
        }

        if (globalRegisters[HitReg.MainDuckPri - 0x64] != maxDuck)
        {
            globalRegisters[HitReg.MainDuckPri - 0x64] = maxDuck;
            RefreshAllVoiceVolumes();
        }
    }

    private void RefreshAllVoiceVolumes()
    {
        foreach (var thread in threads.Values)
        {
            if (!thread.Killed && thread.ActiveVoiceId != 0)
            {
                EmitVoiceUpdate(thread);
            }
        }
    }

    private void EmitVoiceUpdate(TrackThread thread)
    {
        var (gain, pan, pitchScale) = ComputeChannelParams(thread);
        pendingCommands.Enqueue(new HitUpdateVoiceCommand(thread.InstanceId, thread.ActiveVoiceId, gain, pan, pitchScale));
    }

    private void EmitNoteOn(TrackThread thread, int noteArg)
    {
        int patchId = thread.Registers[HitReg.Patch];
        if (patchId <= 1 && noteArg > 1)
        {
            patchId = noteArg;
        }
        if (patchId <= 0) return;

        StopThreadVoice(thread);

        HotPatchDef? patch = ResolvePatch(thread.Bank, patchId);
        bool isLoop = thread.Registers[HitReg.IsLooped] != 0 || (patch?.IsLoop ?? false);
        // Radio/TV dynamic AutoPatch clips play non-looped so wait_samp advances on Ended.
        if (patchId >= 0x20000)
        {
            isLoop = false;
        }
        var (gain, pan, pitchScale) = ComputeChannelParams(thread, patch);
        thread.PendingVoiceBind = true;
        pendingCommands.Enqueue(new HitStartVoiceCommand(
            thread.InstanceId,
            thread.Track.TrackId,
            patchId,
            thread.Track.Group,
            isLoop,
            gain,
            pan,
            pitchScale,
            thread.SourceObjectId,
            thread.Track.IsPositionalObjectTrack,
            thread.EventName,
            thread.Bank));
    }

    private (float Gain, float Pan, float PitchScale) ComputeChannelParams(TrackThread thread, HotPatchDef? patch = null)
    {
        int vol = Math.Max(0, thread.Registers[HitReg.Vol]);
        int extVol = Math.Max(0, thread.Registers[HitReg.ExtVol]);
        int mainVol = Math.Max(0, globalRegisters[HitReg.MainVol - 0x64]);
        float gain = Math.Clamp((vol / 1024.0f) * (extVol / 1024.0f) * (mainVol / 1024.0f), 0.0f, 1.0f);

        int mainDuck = globalRegisters[HitReg.MainDuckPri - 0x64];
        if (mainDuck != 0 && thread.Registers[HitReg.DuckPri] < mainDuck)
        {
            gain *= 0.5f;
        }

        int panRaw = thread.Registers[HitReg.Pan];
        float pan = Math.Clamp((panRaw - 512) / 512.0f, -1.0f, 1.0f);

        int trackPitch = thread.Registers[HitReg.Pitch];
        patch ??= ResolvePatch(thread.Bank, thread.Registers[HitReg.Patch]);
        int patchPitch = patch?.Pitch ?? HitReg.DefaultPitch;
        int combinedPitch = trackPitch + patchPitch - HitReg.DefaultPitch;
        float pitchScale = combinedPitch == HitReg.DefaultPitch
            ? 1.0f
            : Math.Clamp(MathF.Pow(2.0f, (combinedPitch - HitReg.DefaultPitch) / 1200.0f), 0.0625f, 4.0f);

        return (gain, pan, pitchScale);
    }

    private void StopThreadVoice(TrackThread thread)
    {
        thread.PendingVoiceBind = false;
        if (thread.ActiveVoiceId != 0)
        {
            long vid = thread.ActiveVoiceId;
            thread.ActiveVoiceId = 0;
            thread.Registers[HitReg.IsPlaying] = 0;
            voiceToThread.Remove(vid);
            pendingCommands.Enqueue(new HitStopVoiceCommand(thread.InstanceId, vid));
        }
    }

    private void KillThread(TrackThread thread)
    {
        if (thread.Killed) return;
        thread.Killed = true;
        StopThreadVoice(thread);
        threads.Remove(thread.InstanceId);

        if (duckers.ContainsKey(thread.InstanceId))
        {
            UpdateDucker(thread.InstanceId, 0);
        }

        foreach (var grp in seqGroups.Where(kv => kv.Value == thread.InstanceId).Select(kv => kv.Key).ToArray())
        {
            seqGroups.Remove(grp);
        }

        ThreadKilled?.Invoke(thread.InstanceId);
    }

    private TrackThread? FindTargetThread(int targetId)
    {
        if (threads.TryGetValue(targetId, out var byInst) && !byInst.Killed)
            return byInst;
        return threads.Values.FirstOrDefault(t => !t.Killed && t.Track.TrackId == targetId);
    }

    private static bool ResolveTrackDataOffset(HitBankModule bank, int tkdId, out int offset)
    {
        if (bank.TrackDataOffsets.TryGetValue(tkdId, out offset) && offset >= 0 && offset < bank.HitBytes.Length)
            return true;
        offset = 0;
        return false;
    }

    private bool TryFindTrack(HitBankModule preferredBank, int trackId, out HotTrackDef track)
    {
        if (preferredBank.Tracks.TryGetValue(trackId, out track!)) return true;
        return tracksById.TryGetValue(trackId, out track!);
    }

    private HotPatchDef? ResolvePatch(HitBankModule preferredBank, int patchId)
    {
        if (preferredBank.Patches.TryGetValue(patchId, out var bankPatch))
            return bankPatch;
        return patchesById.GetValueOrDefault(patchId);
    }
}
