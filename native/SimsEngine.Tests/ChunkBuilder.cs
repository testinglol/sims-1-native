using System.Buffers.Binary;
using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>Writes a chunk body the way the game files store it (little-endian), and wraps it in a one-chunk IFF.</summary>
internal sealed class ChunkBuilder
{
    private readonly List<byte> _bytes = [];

    public int Length => _bytes.Count;

    public ChunkBuilder U8(byte value) { _bytes.Add(value); return this; }
    public ChunkBuilder S16(short value) => Raw(BitConverter.GetBytes(value));
    public ChunkBuilder U16(ushort value) => Raw(BitConverter.GetBytes(value));
    public ChunkBuilder S32(int value) => Raw(BitConverter.GetBytes(value));
    public ChunkBuilder U32(uint value) => Raw(BitConverter.GetBytes(value));
    public ChunkBuilder F32(float value) => Raw(BitConverter.GetBytes(value));
    public ChunkBuilder Tag(string tag) => Raw(Encoding.ASCII.GetBytes(tag));
    public ChunkBuilder CString(string text) => Raw(Encoding.Latin1.GetBytes(text + "\0"));

    /// <summary>A zero-terminated string padded to an even offset from the chunk start with <paramref name="pad"/>.</summary>
    public ChunkBuilder EvenString(string text, byte pad = 0xA3)
    {
        CString(text);
        if ((_bytes.Count & 1) != 0) _bytes.Add(pad);
        return this;
    }

    /// <summary>A UTF-8 string with a 7-bit variable-length size (The Sims Online layout).</summary>
    public ChunkBuilder VarString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = bytes.Length;
        do
        {
            var part = (byte)(length & 0x7f);
            length >>= 7;
            _bytes.Add(length > 0 ? (byte)(part | 0x80) : part);
        } while (length > 0);
        return Raw(bytes);
    }

    public ChunkBuilder Raw(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) _bytes.Add(value);
        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    /// <summary>An IFF 2.5 file holding this body as its only chunk, and that chunk.</summary>
    public (IffFile File, IffChunk Chunk) InFile(string type, ushort id = 4096)
    {
        var iff = new IffFile(FileBytes(type, id), "test.iff");
        return (iff, iff.Chunks.Single());
    }

    /// <summary>The bytes of an IFF 2.5 file holding this body as its only chunk.</summary>
    public byte[] FileBytes(string type, ushort id = 4096) => File((type, id, ToArray()));

    /// <summary>The bytes of an IFF 2.5 file holding these chunks in order.</summary>
    public static byte[] File(params (string Type, ushort Id, byte[] Body)[] chunks)
    {
        var file = new byte[IffFile.HeaderSize + chunks.Sum(chunk => IffFile.ChunkHeaderSize + chunk.Body.Length)];
        Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1").CopyTo(file, 0);
        var at = IffFile.HeaderSize;
        foreach (var (type, id, body) in chunks)
        {
            var header = file.AsSpan(at);
            BinaryPrimitives.WriteUInt32BigEndian(header, ResourceType.Parse(type).Code);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(IffFile.ChunkHeaderSize + body.Length));
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], id);
            body.CopyTo(header[IffFile.ChunkHeaderSize..]);
            at += IffFile.ChunkHeaderSize + body.Length;
        }
        return file;
    }
}
