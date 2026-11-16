using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>Represents a lot array (Arry) grid resource. Older formats store 16-bit dimensions with data
/// offset by <c>rows * 4 + 8</c>, while modern formats store 32-bit dimensions and support optional RLE
/// compression where bit 15 flags repeated bytes and runs align to even byte offsets.</summary>
public sealed class LotArray
{
    public bool OldLayout { get; }
    public short Field0 { get; }
    public short Field1 { get; }
    public int Rows { get; }
    public int Width { get; }
    public int ElementSize { get; }
    public bool RunLengthCoded { get; }
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

    /// <summary>Serializes the array data as an uncompressed Arry chunk.</summary>
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
