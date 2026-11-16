using System.IO.Compression;

namespace SimsSDL.Core;

public sealed class PNGImage
{
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7Passes =
    [
        (0, 0, 8, 8),
        (4, 0, 8, 8),
        (0, 4, 4, 8),
        (2, 0, 4, 4),
        (0, 2, 2, 4),
        (1, 0, 2, 2),
        (0, 1, 1, 2),
    ];

    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public PNGImage(byte[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (width is < 1 or > 4096 ||
            height is < 1 or > 4096 ||
            data.Length > 64 * 1024 * 1024 ||
            data.Length < 8 ||
            !data.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            throw Invalid();
        }

        int offset = 8;
        bool hasHeader = false;
        int hdrWidth = 0;
        int hdrHeight = 0;
        int hdrDepth = 0;
        int hdrColour = 0;
        int hdrInterlace = 0;
        byte[] palette = [];
        byte[] transparency = [];
        using var compressed = new MemoryStream();
        bool ended = false;

        while (offset + 12 <= data.Length)
        {
            uint lengthU32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (lengthU32 > (uint)(data.Length - offset - 12))
                throw Invalid();

            int length = (int)lengthU32;
            int bodyOffset = offset + 8;
            ReadOnlySpan<byte> typeSpan = data.AsSpan(offset + 4, 4);
            offset += 12 + length;

            if (typeSpan.SequenceEqual("IHDR"u8))
            {
                if (hasHeader || length != 13 || data[bodyOffset + 10] != 0 || data[bodyOffset + 11] != 0 || data[bodyOffset + 12] > 1)
                    throw Invalid();

                uint wU32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyOffset, 4));
                uint hU32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyOffset + 4, 4));
                if (wU32 > int.MaxValue || hU32 > int.MaxValue)
                    throw Invalid();

                hdrWidth = (int)wU32;
                hdrHeight = (int)hU32;
                hdrDepth = data[bodyOffset + 8];
                hdrColour = data[bodyOffset + 9];
                hdrInterlace = data[bodyOffset + 12];
                hasHeader = true;
            }
            else if (typeSpan.SequenceEqual("PLTE"u8))
            {
                palette = data.AsSpan(bodyOffset, length).ToArray();
            }
            else if (typeSpan.SequenceEqual("tRNS"u8))
            {
                transparency = data.AsSpan(bodyOffset, length).ToArray();
            }
            else if (typeSpan.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data, bodyOffset, length);
            }
            else if (typeSpan.SequenceEqual("IEND"u8))
            {
                ended = true;
                break;
            }
        }

        int channels = hdrColour switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => -1,
        };

        bool validDepth = hdrColour switch
        {
            0 => hdrDepth is 1 or 2 or 4 or 8 or 16,
            3 => hdrDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => hdrDepth is 8 or 16,
            _ => false,
        };

        if (!ended ||
            !hasHeader ||
            hdrWidth != width ||
            hdrHeight != height ||
            channels < 0 ||
            !validDepth ||
            (hdrColour == 3 && (palette.Length == 0 || palette.Length % 3 != 0)))
        {
            throw Invalid();
        }

        byte[] raw = DecompressZLib(compressed);

        int depth = hdrDepth;
        int bitsPerPixel = channels * depth;
        int filterStride = Math.Max(1, bitsPerPixel / 8);
        byte[] pixels = new byte[width * height * 4];
        int cursor = 0;

        int Sample(byte[] row, int index) => depth switch
        {
            16 => row[index * 2],
            8 => row[index],
            _ => (row[(index * depth) / 8] >> (8 - depth - ((index * depth) % 8))) & ((1 << depth) - 1),
        };

        int Raw16(byte[] row, int index) =>
            depth == 16 ? ((row[index * 2] << 8) | row[index * 2 + 1]) : Sample(row, index);

        int scale = depth < 8 ? 255 / ((1 << depth) - 1) : 1;

        void Pass(int x0, int y0, int dx, int dy)
        {
            int columns = (width - x0 + dx - 1) / dx;
            int rows = (height - y0 + dy - 1) / dy;
            if (columns <= 0 || rows <= 0)
                return;

            int rowBytes = (columns * bitsPerPixel + 7) / 8;
            byte[] previous = new byte[rowBytes];
            byte[] row = new byte[rowBytes];

            for (int y = 0; y < rows; y++)
            {
                if (cursor + 1 + rowBytes > raw.Length)
                    throw Invalid();

                byte filter = raw[cursor];
                Buffer.BlockCopy(raw, cursor + 1, row, 0, rowBytes);
                cursor += 1 + rowBytes;

                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= filterStride ? row[i - filterStride] : 0;
                    int b = previous[i];
                    int c = i >= filterStride ? previous[i - filterStride] : 0;
                    int add = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw Invalid(),
                    };
                    row[i] = unchecked((byte)(row[i] + add));
                }

                Buffer.BlockCopy(row, 0, previous, 0, rowBytes);

                for (int x = 0; x < columns; x++)
                {
                    int outOff = ((y0 + y * dy) * width + x0 + x * dx) * 4;
                    int r = 0, g = 0, b = 0, alpha = 255;

                    switch (hdrColour)
                    {
                        case 0:
                            int grey0 = Sample(row, x) * scale;
                            r = grey0;
                            g = grey0;
                            b = grey0;
                            if (transparency.Length >= 2 && Raw16(row, x) == ((transparency[0] << 8) | transparency[1]))
                                alpha = 0;
                            break;
                        case 2:
                            r = Sample(row, x * 3);
                            g = Sample(row, x * 3 + 1);
                            b = Sample(row, x * 3 + 2);
                            if (transparency.Length >= 6 &&
                                Raw16(row, x * 3) == ((transparency[0] << 8) | transparency[1]) &&
                                Raw16(row, x * 3 + 1) == ((transparency[2] << 8) | transparency[3]) &&
                                Raw16(row, x * 3 + 2) == ((transparency[4] << 8) | transparency[5]))
                            {
                                alpha = 0;
                            }
                            break;
                        case 3:
                            int idx = Sample(row, x);
                            if (idx * 3 + 2 >= palette.Length)
                                throw Invalid();
                            r = palette[idx * 3];
                            g = palette[idx * 3 + 1];
                            b = palette[idx * 3 + 2];
                            if (idx < transparency.Length)
                                alpha = transparency[idx];
                            break;
                        case 4:
                            int grey4 = Sample(row, x * 2);
                            r = grey4;
                            g = grey4;
                            b = grey4;
                            alpha = Sample(row, x * 2 + 1);
                            break;
                        default:
                            r = Sample(row, x * 4);
                            g = Sample(row, x * 4 + 1);
                            b = Sample(row, x * 4 + 2);
                            alpha = Sample(row, x * 4 + 3);
                            break;
                    }

                    pixels[outOff] = (byte)r;
                    pixels[outOff + 1] = (byte)g;
                    pixels[outOff + 2] = (byte)b;
                    pixels[outOff + 3] = (byte)alpha;
                }
            }
        }

        if (hdrInterlace == 1)
        {
            foreach (var (x0, y0, dx, dy) in Adam7Passes)
                Pass(x0, y0, dx, dy);
        }
        else
        {
            Pass(0, 0, 1, 1);
        }

        Width = width;
        Height = height;
        Rgba = pixels;
    }

    private static ContentError Invalid() => new("PNG dimensions or format differ from scene metadata");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    private static byte[] DecompressZLib(MemoryStream compressed)
    {
        if (compressed.Length < 6)
            throw new ContentError("Invalid zlib header");

        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
            using var decompressed = new MemoryStream();
            zlib.CopyTo(decompressed);
            return decompressed.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new ContentError($"Invalid PNG deflate stream: {ex.Message}");
        }
    }
}

public static class PNGEncoder
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) == 1 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length)
            throw new ContentError("PNG pixel data does not match its size");

        byte[] zlibBytes;
        using (var zlibOut = new MemoryStream())
        {
            using (var z = new ZLibStream(zlibOut, CompressionLevel.Fastest, leaveOpen: true))
            {
                int rowBytes = width * 4;
                ReadOnlySpan<byte> filterNone = [0];
                for (int row = 0; row < height; row++)
                {
                    z.Write(filterNone);
                    z.Write(rgba, row * rowBytes, rowBytes);
                }
            }
            zlibBytes = zlibOut.ToArray();
        }

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        using var output = new MemoryStream(8 + 25 + 12 + zlibBytes.Length + 12);
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        output.Write(signature);
        WriteChunk(output, "IHDR"u8, ihdr);
        WriteChunk(output, "IDAT"u8, zlibBytes);
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        output.Write(header);
        output.Write(type);
        output.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        output.Write(crcBytes);
    }
}
