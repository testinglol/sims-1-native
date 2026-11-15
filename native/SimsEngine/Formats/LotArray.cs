using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>A lot array chunk (Arry): a grid of fixed-size elements that a lot file keeps one per map layer, as
/// Sims.exe FUN_004310b4 reads it.
/// <para>When the chunk starts with a nonzero s16 it is the old layout: s16 element size, s16 row count, s16 width,
/// then the elements from byte offset rows * 4 + 8 (Sims.exe skips that space without reading it). Otherwise the chunk
/// is a little-endian stream without the persist header (FUN_004644dc) that FUN_0043143a reads: two s16s (the first
/// being that zero), u32 row count (+8), u32 width (+4), u32 element size (+0x1c) and a u32 that selects run-length
/// coding when nonzero, then rows * width * element size bytes. Coded data is a list of runs, each an s16 header whose
/// bit 15 means "one byte repeated" and whose low 15 bits are the run length, followed by that one byte or that many
/// literal bytes; after every run a pad byte follows when the stream offset is odd.</para>
/// <para>Sims.exe does not check that a run stays inside the array; this reader rejects one that does not.</para></summary>
public sealed class LotArray
{
    public bool OldLayout { get; }
    public short Field0 { get; }
    public short Field1 { get; }
    public int Rows { get; }
    public int Width { get; }
    public int ElementSize { get; }
    public bool RunLengthCoded { get; }
    /// <summary>The elements, row by row.</summary>
    public byte[] Data { get; }

    private LotArray(bool oldLayout, short field0, short field1, int rows, int width, int elementSize, bool coded, byte[] data)
    {
        OldLayout = oldLayout;
        Field0 = field0;
        Field1 = field1;
        Rows = rows;
        Width = width;
        ElementSize = elementSize;
        RunLengthCoded = coded;
        Data = data;
    }

    /// <summary>The element at <paramref name="row"/>, <paramref name="column"/>.</summary>
    public ReadOnlySpan<byte> Element(int row, int column) =>
        Data.AsSpan((row * Width + column) * ElementSize, ElementSize);

    public static LotArray Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var first = reader.S16();
        if (first != 0)
        {
            var oldRows = reader.S16();
            var oldWidth = reader.S16();
            var oldSize = Size(ref reader, oldRows, oldWidth, first);
            reader.Bytes(oldRows * 4 + 8 - reader.Position);
            return new LotArray(true, first, 0, oldRows, oldWidth, first, false, reader.Bytes(oldSize).ToArray());
        }

        var field1 = reader.S16();
        var rows = reader.S32();
        var width = reader.S32();
        var elementSize = reader.S32();
        var coded = reader.U32() != 0;
        var data = new byte[Size(ref reader, rows, width, elementSize)];
        if (!coded) reader.Bytes(data.Length).CopyTo(data);
        else
        {
            var at = 0;
            while (at < data.Length)
            {
                var header = reader.U16();
                var length = header & 0x7fff;
                if (length > data.Length - at) throw reader.Error($"run of {length} bytes passes the end of the array");
                if ((header & 0x8000) != 0)
                {
                    // Sims.exe stores the byte at the run start even for a zero-length run.
                    var value = reader.U8();
                    data[at] = value;
                    data.AsSpan(at, length).Fill(value);
                }
                else reader.Bytes(length).CopyTo(data.AsSpan(at));
                at += length;
                if ((reader.Position & 1) != 0) reader.U8();
            }
        }
        return new LotArray(false, first, field1, rows, width, elementSize, coded, data);
    }

    /// <summary>The chunk bytes for this array's shape holding <paramref name="data"/>, written without run-length
    /// coding (the coded flag at +0x10 is clear, which Sims.exe FUN_0043143a reads as raw elements).</summary>
    public byte[] ToChunkData(ReadOnlySpan<byte> data, int? elementSize = null)
    {
        if (OldLayout) throw new NotSupportedException("Old-layout lot arrays are not written");
        var size = elementSize ?? ElementSize;
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(elementSize));
        if (data.Length != Rows * Width * size)
            throw new ArgumentException($"Array holds {Rows * Width * size} bytes, not {data.Length}", nameof(data));
        var chunk = new byte[20 + data.Length];
        BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(0), Field0);
        BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(2), Field1);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), Rows);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(12), size);
        data.CopyTo(chunk.AsSpan(20));
        return chunk;
    }

    private static int Size(ref ResourceReader reader, int rows, int width, int elementSize)
    {
        if (rows < 0 || width < 0 || elementSize < 0) throw reader.Error($"negative array size {rows} x {width} x {elementSize}");
        var size = (long)rows * width * elementSize;
        if (size > int.MaxValue || size > reader.Remaining * 0x8000L) throw reader.Error($"array of {size} bytes does not fit the chunk");
        return (int)size;
    }
}
