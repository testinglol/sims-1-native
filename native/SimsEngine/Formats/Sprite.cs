using System.Buffers.Binary;

namespace SimsEngine.Formats;

/// <summary>A decoded frame of an 8-bit sprite: one palette index per pixel, row by row, and whether the frame
/// draws that pixel.</summary>
public sealed class IndexedFrame
{
    /// <summary>The first two signed words in the frame header. Sims.exe exposes these through
    /// cRenderer::GetShapeBounds (Mac) / FUN_004c8ec9 (Windows); the draw path uses Top when positioning a shape.</summary>
    public required short BoundsLeft { get; init; }
    public required short BoundsTop { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Indices { get; init; }
    public required bool[] Opaque { get; init; }

    /// <summary>The frame writes pixels outside its own width and height; Sims.exe draws them, clipped only by the
    /// screen. They are left out here.</summary>
    public bool WritesOutside { get; init; }
}

/// <summary>An 8-bit sprite (SPR#), as Sims.exe FUN_004c173f and FUN_004c1ad4 load it: a u32 version, a u32 frame
/// count, from version 502 a u32 palette id, then one u32 frame offset per frame. Sims.exe uses the palette id only
/// from version 502 and when it is not negative, and the global default palette otherwise. Versions 501 and 503 hold
/// two sets of frames (half the count each) and version 506 five.
/// <para>A frame starts with an 8-byte header (the signed left/top bound words, then height and width as u16) and
/// continues with byte pairs (command, count), drawn by FUN_004c29b0: 4 starts the next row (the count is the row's
/// byte length, used to skip clipped rows), 9 skips <c>count</c> rows, 1 skips <c>count</c> pixels, 2 repeats the
/// next byte <c>count</c> times (the pair after it is the colour and a padding byte), 3 copies <c>count</c> bytes
/// and pads to an even offset from the frame start, and 5 ends the frame. Other commands are skipped.</para>
/// <para>Chunks from the Macintosh files are big-endian; FUN_004c90d0 swaps the header, the offsets and each frame's
/// four header u16s. Which chunks it is applied to is decided by the chunk loader; here a version that does not fit
/// 16 bits marks a big-endian chunk (inferred from the game files, where every such version swaps to 502 or
/// 503).</para></summary>
public sealed class Sprite
{
    /// <summary>The palette id when the sprite names one, or -1 for the default palette.</summary>
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

/// <summary>A decoded frame of a sprite with depth (SPR2). <see cref="Alpha"/> is 0 where the frame draws nothing,
/// the stored 5-bit value for pixels drawn with alpha, and 31 for opaque pixels (inferred: the stored values run from
/// 0 to 31).</summary>
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

/// <summary>A sprite with depth (SPR2). Version 1000, the one Sims.exe reads (FUN_004c173f keeps the whole chunk),
/// has a u32 version, a u32 frame count, a u32 palette id (negative for the default palette) and one u32 frame offset
/// per frame (FUN_004c828c). Version 1001, from The Sims Online, has the version, palette id and count, then the
/// frames one after another, each after a u32 version and a u32 byte size (layout inferred from the game files).
/// <para>A frame starts with a 16-byte header: width and height as u16 (used by every blitter), a u32 of flags, the
/// palette id and the transparent colour index as u16 (names inferred), then the y and x offsets as s16 (added to the
/// draw position by FUN_004c828c). FUN_004c828c reads only the size and the offsets; the palette Sims.exe draws with
/// is the sprite's own. The rest is a stream of u16 commands, the top 3 bits the command and the low 13 a
/// count, drawn by FUN_004bf678 and its variants: 0x0000 starts the next row (the count is the row's byte length,
/// used to skip clipped rows), 0x8000 skips <c>count</c> rows, 0x6000 skips <c>count</c> pixels, 0x2000 draws
/// <c>count</c> (depth, colour) byte pairs, 0x4000 <c>count</c> (depth, colour, alpha) triples, 0xC000 <c>count</c>
/// colour bytes, and 0xA000 ends the frame. After each run of pixel bytes the stream pads to an even offset; other
/// commands are skipped.</para></summary>
public sealed class DepthSprite
{
    public uint Version { get; }

    /// <summary>The sprite's palette id, or -1 for the default palette.</summary>
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

    /// <summary>Sims.exe aligns the stream pointer itself, inside its copy of the whole chunk, so the padding is to an
    /// even offset from the chunk start.</summary>
    private static void Align(ref ResourceReader reader)
    {
        if ((reader.Position & 1) != 0 && reader.Remaining > 0) reader.Position++;
    }
}
