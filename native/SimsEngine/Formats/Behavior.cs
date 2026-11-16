namespace SimsEngine.Formats;

/// <summary>Represents a 12-byte behavior instruction consisting of an opcode masked to 15 bits, a
/// breakpoint flag in bit 15, true/false branch targets, and an 8-byte operand.</summary>
public sealed record BhavInstruction(ushort Opcode, byte TrueTarget, byte FalseTarget, byte[] Operand, bool Breakpoint)
{
    /// <summary>Terminal branch targets for behavior instructions, where values above 0xFA signal execution
    /// error (0xFD), return true (0xFE), or return false (0xFF).</summary>
    public const byte Error = 0xFD, ReturnTrue = 0xFE, ReturnFalse = 0xFF;
}

/// <summary>Represents a behavior tree chunk (BHAV). Header versions 0x8000–0x8002 use a 12-byte layout
/// while 0x8003 uses a 13-byte layout; argument and local variable counts are fixed to 4 and 0 in 0x8000,
/// bounded to 4 in 0x8001, and read directly in subsequent versions.</summary>
public sealed class Bhav
{
    public ushort Signature { get; }
    public byte Type { get; }
    public byte StoredArgumentCount { get; }
    public byte StoredLocalCount { get; }
    public uint TreeVersion { get; }
    public IReadOnlyList<BhavInstruction> Instructions { get; }

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

    /// <summary>Indicates whether this behavior tree is a subroutine (type 4) that starts at a
    /// caller-specified entry instruction index instead of instruction zero.</summary>
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

/// <summary>Represents a behavior constants chunk (BCON), storing 16-bit values preceded by a header word
/// whose low byte contains the count and whose bit 15 enables runtime tuning overrides.</summary>
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
