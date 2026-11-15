namespace SimsEngine.Formats;

/// <summary>One BHAV instruction: 12 bytes of opcode, true and false targets and an 8-byte operand. Sims.exe
/// FUN_00402611 masks the stored opcode with 0x7fff on load and uses bit 15 as the debugger's breakpoint flag.</summary>
public sealed record BhavInstruction(ushort Opcode, byte TrueTarget, byte FalseTarget, byte[] Operand, bool Breakpoint)
{
    /// <summary>Targets above 0xFA are not instructions. Sims.exe FUN_00474cae raises error 1100 for 0xFD (and for any
    /// other value above 0xFA), returns true for 0xFE and false for 0xFF; the loader does not translate them.</summary>
    public const byte Error = 0xFD, ReturnTrue = 0xFE, ReturnFalse = 0xFF;
}

/// <summary>A behavior tree (BHAV). Four header versions ship:
/// <list type="bullet">
/// <item>0x8000 to 0x8002: 12 bytes. u16 signature, int16 instruction count, type byte, argument-count byte,
/// local-count byte, then (inferred) a u32 tree version at +8.</item>
/// <item>0x8003: 13 bytes (inferred from the files; Sims.exe sees these already converted to the 12-byte form):
/// signature, type, argument and local bytes at +2..+4, u32 tree version at +5, u32 instruction count at +9.</item>
/// </list>
/// The argument and local counts follow Sims.exe FUN_004749b7: version 0x8000 trees always take 4 arguments and no
/// locals; 0x8001 trees take at most 4 of each; later versions use the stored bytes.</summary>
public sealed class Bhav
{
    public ushort Signature { get; }
    public byte Type { get; }
    public byte StoredArgumentCount { get; }
    public byte StoredLocalCount { get; }
    public uint TreeVersion { get; }
    public IReadOnlyList<BhavInstruction> Instructions { get; }

    /// <summary>Bytes after the last instruction (one shipped tree has them); Sims.exe ignores them.</summary>
    public int TrailingBytes { get; }

    public byte ArgumentCount => Signature switch
    {
        < 0x8001 => 4,
        0x8001 => Math.Min(StoredArgumentCount, (byte)4),
        _ => StoredArgumentCount,
    };

    public byte LocalCount => Signature switch
    {
        < 0x8001 => 0,
        0x8001 => Math.Min(StoredLocalCount, (byte)4),
        _ => StoredLocalCount,
    };

    /// <summary>Type 4 trees start at the instruction the caller names (clamped to the last one) instead of 0
    /// (FUN_004749b7).</summary>
    public bool StartsAtCallerInstruction => Type == 4;

    private Bhav(ushort signature, byte type, byte arguments, byte locals, uint version, IReadOnlyList<BhavInstruction> instructions, int trailing)
    {
        Signature = signature;
        Type = type;
        StoredArgumentCount = arguments;
        StoredLocalCount = locals;
        TreeVersion = version;
        Instructions = instructions;
        TrailingBytes = trailing;
    }

    public static Bhav Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var signature = reader.U16();
        byte type, arguments, locals;
        uint version;
        int count;
        if (signature == 0x8003)
        {
            type = reader.U8();
            arguments = reader.U8();
            locals = reader.U8();
            version = reader.U32();
            var stored = reader.U32();
            if (stored > short.MaxValue) throw reader.Error($"instruction count {stored} is above the 32767 the game indexes");
            count = (int)stored;
        }
        else if (signature is >= 0x8000 and <= 0x8002)
        {
            count = reader.S16();
            if (count < 0) throw reader.Error($"negative instruction count {count}");
            type = reader.U8();
            arguments = reader.U8();
            locals = reader.U8();
            reader.U8();
            version = reader.U32();
        }
        else throw reader.Error($"unknown BHAV signature 0x{signature:x4}");
        if ((long)count * 12 > reader.Remaining) throw reader.Error($"{count} instructions need {count * 12} bytes, {reader.Remaining} left");
        var instructions = new BhavInstruction[count];
        for (var index = 0; index < count; index++)
        {
            var opcode = reader.U16();
            var trueTarget = reader.U8();
            var falseTarget = reader.U8();
            instructions[index] = new BhavInstruction((ushort)(opcode & 0x7fff), trueTarget, falseTarget, reader.Bytes(8).ToArray(), (opcode & 0x8000) != 0);
        }
        return new Bhav(signature, type, arguments, locals, version, instructions, reader.Remaining);
    }
}

/// <summary>Behavior constants (BCON). The first u16 holds the count in its low byte and, in bit 15, the flag that
/// lets an object's tuning override replace the table (Sims.exe FUN_0040235e), followed by the int16 values.</summary>
public sealed record Bcon(bool TuningOverride, IReadOnlyList<short> Constants)
{
    public static Bcon Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var count = reader.U8();
        var flags = reader.U8();
        var values = new short[count];
        for (var index = 0; index < count; index++) values[index] = reader.S16();
        return new Bcon((flags & 0x80) != 0, values);
    }
}
