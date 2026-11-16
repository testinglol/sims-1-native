using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>A decoded 8-bit sprite frame, storing row-major palette indices and pixel visibility flags.</summary>
public sealed class IndexedFrame
{
    /// <summary>Signed coordinate bounds from the frame header used to offset the sprite shape during
    /// rendering.</summary>
    public required short BoundsLeft { get; init; }
    public required short BoundsTop { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Indices { get; init; }
    public required bool[] Opaque { get; init; }

    /// <summary>Indicates whether the frame contains drawing commands that extend beyond its declared
    /// dimensions.</summary>
    public bool WritesOutside { get; init; }
}

/// <summary>Parses 8-bit sprite (SPR#) resources. Decodes versioned headers, frame tables, and run-length
/// draw commands including row skips, pixel spans, repeating colors, and raw byte copies.</summary>
public sealed class Sprite
{
    public int PaletteId { get; }
    public uint Version { get; }
    public bool BigEndian { get; }
    public int Sets { get; }
    public IReadOnlyList<IndexedFrame> Frames { get; }

    private Sprite(uint version, bool bigEndian, int paletteId, int sets, IReadOnlyList<IndexedFrame> frames)
    {
        Version = version;
        BigEndian = bigEndian;
        PaletteId = paletteId;
        Sets = sets;
        Frames = frames;
    }

    public static Sprite Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var data = chunk.Data.Span;
        var bigEndian = reader.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) > 0xFFFF;
        uint U32(ref ResourceReader reader) => bigEndian ? reader.U32BigEndian() : reader.U32();
        var version = U32(ref reader);
        var count = U32(ref reader);
        var paletteId = -1;
        if (version >= 502)
        {
            var id = (int)U32(ref reader);
            if (id >= 0) paletteId = id;
        }
        if (count > reader.Remaining / 4) throw reader.Error($"{count} frame offsets do not fit the chunk");
        var offsets = new int[count];
        for (var index = 0; index < offsets.Length; index++) offsets[index] = (int)U32(ref reader);
        var frames = new IndexedFrame[count];
        for (var index = 0; index < frames.Length; index++)
        {
            reader.Position = offsets[index];
            frames[index] = ReadFrame(ref reader, bigEndian);
        }
        var sets = version switch { 501 or 503 => 2, 506 => 5, _ => 1 };
        return new Sprite(version, bigEndian, paletteId, sets, frames);
    }

    private static IndexedFrame ReadFrame(ref ResourceReader reader, bool bigEndian)
    {
        var start = reader.Position;
        var bounds = reader.Bytes(4);
        var boundsLeft = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(bounds) : BinaryPrimitives.ReadInt16LittleEndian(bounds);
        var boundsTop = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(bounds[2..]) : BinaryPrimitives.ReadInt16LittleEndian(bounds[2..]);
        int height = bigEndian ? reader.U16BigEndian() : reader.U16();
        int width = bigEndian ? reader.U16BigEndian() : reader.U16();
        var indices = new byte[width * height];
        var opaque = new bool[width * height];
        var outside = false;
        int row = -1, x = 0;
        void Put(int column, byte index)
        {
            if (row < 0 || row >= height || column < 0 || column >= width)
            {
                outside = true;
                return;
            }
            indices[row * width + column] = index;
            opaque[row * width + column] = true;
        }
        while (reader.Remaining >= 2)
        {
            var command = reader.U8();
            var count = reader.U8();
            switch (command)
            {
                case 1:
                    x += count;
                    break;
                case 2:
                    var color = reader.U8();
                    reader.U8();
                    for (var column = 0; column < count; column++) Put(x++, color);
                    break;
                case 3:
                    var pixels = reader.Bytes(count);
                    foreach (var pixel in pixels) Put(x++, pixel);
                    if (((reader.Position - start) & 1) != 0 && reader.Remaining > 0) reader.Position++;
                    break;
                case 4:
                    row++;
                    x = 0;
                    break;
                case 9:
                    row += count;
                    break;
                case 5:
                    return new IndexedFrame { BoundsLeft = boundsLeft, BoundsTop = boundsTop, Width = width, Height = height,
                        Indices = indices, Opaque = opaque, WritesOutside = outside };
            }
        }
        throw reader.Error("frame has no end command");
    }
}

/// <summary>A decoded depth sprite (SPR2) frame, containing depth values, color indices, and 5-bit alpha
/// values (0 to 31).</summary>
public sealed class DepthFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required uint Flags { get; init; }
    public required ushort PaletteId { get; init; }
    public required ushort TransparentIndex { get; init; }
    public required short X { get; init; }
    public required short Y { get; init; }
    public required byte[] Indices { get; init; }
    public required byte[] Depth { get; init; }
    public required byte[] Alpha { get; init; }
    public bool WritesOutside { get; init; }
}

/// <summary>Parses sprites with depth (SPR2). Decodes version 1000 and 1001 frame headers and 16-bit packed
/// command streams for depth, color, and 5-bit alpha blits.</summary>
public sealed class DepthSprite
{
    public uint Version { get; }

    public int PaletteId { get; }

    public IReadOnlyList<DepthFrame> Frames { get; }
    public bool ReadBySimsExe => Version == 1000;

    private DepthSprite(uint version, int paletteId, IReadOnlyList<DepthFrame> frames)
    {
        Version = version;
        PaletteId = paletteId;
        Frames = frames;
    }

    public static DepthSprite Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var version = reader.U32();
        var frames = new List<DepthFrame>();
        int paletteId;
        if (version == 1000)
        {
            var count = reader.U32();
            paletteId = reader.S32();
            if (count > reader.Remaining / 4) throw reader.Error($"{count} frame offsets do not fit the chunk");
            var offsets = new int[count];
            for (var index = 0; index < offsets.Length; index++) offsets[index] = reader.S32();
            foreach (var offset in offsets)
            {
                reader.Position = offset;
                frames.Add(ReadFrame(ref reader));
            }
        }
        else if (version == 1001)
        {
            paletteId = reader.S32();
            var count = reader.U32();
            if (count > reader.Remaining / 8) throw reader.Error($"{count} frames do not fit the chunk");
            for (var index = 0; index < count; index++)
            {
                reader.U32();
                var size = reader.S32();
                var next = reader.Position + size;
                frames.Add(ReadFrame(ref reader));
                if (size < 0 || next > reader.Length) throw reader.Error($"frame size {size} runs past the chunk");
                reader.Position = next;
            }
        }
        else throw reader.Error($"version {version} is not a sprite version");
        return new DepthSprite(version, paletteId < 0 ? -1 : paletteId, frames);
    }

    private static DepthFrame ReadFrame(ref ResourceReader reader)
    {
        int width = reader.U16();
        int height = reader.U16();
        var flags = reader.U32();
        var paletteId = reader.U16();
        var transparent = reader.U16();
        var y = reader.S16();
        var x = reader.S16();
        var indices = new byte[width * height];
        var depth = new byte[width * height];
        var alpha = new byte[width * height];
        var outside = false;
        int row = -1, nextRow = 0, column = 0;
        void Put(byte index, byte z, byte a)
        {
            if (row < 0 || row >= height || column < 0 || column >= width) outside = true;
            else
            {
                var pixel = row * width + column;
                indices[pixel] = index;
                depth[pixel] = z;
                alpha[pixel] = a;
            }
            column++;
        }
        while (reader.Remaining >= 2)
        {
            var word = reader.U16();
            var count = word & 0x1FFF;
            switch (word & 0xE000)
            {
                case 0x0000:
                    row = nextRow++;
                    column = 0;
                    break;
                case 0x8000:
                    nextRow += count;
                    break;
                case 0x6000:
                    column += count;
                    break;
                case 0x2000:
                    for (var pixel = 0; pixel < count; pixel++)
                    {
                        var pair = reader.Bytes(2);
                        Put(pair[1], pair[0], 31);
                    }
                    Align(ref reader);
                    break;
                case 0x4000:
                    for (var pixel = 0; pixel < count; pixel++)
                    {
                        var triple = reader.Bytes(3);
                        Put(triple[1], triple[0], triple[2]);
                    }
                    Align(ref reader);
                    break;
                case 0xC000:
                    foreach (var index in reader.Bytes(count)) Put(index, 0, 31);
                    Align(ref reader);
                    break;
                case 0xA000:
                    return new DepthFrame
                    {
                        Width = width, Height = height, Flags = flags, PaletteId = paletteId, TransparentIndex = transparent,
                        X = x, Y = y, Indices = indices, Depth = depth, Alpha = alpha, WritesOutside = outside,
                    };
            }
        }
        throw reader.Error("frame has no end command");
    }

    /// <summary>Aligns the reader to an even byte boundary relative to the chunk start.</summary>
    private static void Align(ref ResourceReader reader)
    {
        if ((reader.Position & 1) != 0 && reader.Remaining > 0) reader.Position++;
    }
}
