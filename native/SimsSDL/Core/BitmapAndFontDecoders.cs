namespace SimsSDL.Core;

/// <summary>
/// Decodes 8-bit (uncompressed or RLE8), 24-bit, and 32-bit Windows BMP images into top-down RGBA8888 pixels.
/// </summary>
public sealed class SimsBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public SimsBitmap(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        uint Unsigned(int offset, int count = 4)
        {
            if (offset < 0 || count < 0 || count > 4 || offset > data.Length - count)
                throw new ContentError("Truncated BMP header");
            uint value = 0;
            for (int i = 0; i < count; i++)
                value |= (uint)data[offset + i] << (i * 8);
            return value;
        }

        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M' || Unsigned(14) < 40)
            throw new ContentError("Unsupported BMP header");

        uint pixelOffsetU32 = Unsigned(10);
        int width = unchecked((int)Unsigned(18));
        int signedHeight = unchecked((int)Unsigned(22));
        if (signedHeight == int.MinValue)
            throw new ContentError("Unsupported BMP dimensions or encoding");

        int height = Math.Abs(signedHeight);
        int bits = (int)Unsigned(28, 2);
        uint compression = Unsigned(30);

        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height > 64_000_000 ||
            Unsigned(26, 2) != 1 || pixelOffsetU32 < 54 || pixelOffsetU32 > (uint)data.Length ||
            !((bits == 8 && (compression == 0 || compression == 1)) || ((bits == 24 || bits == 32) && compression == 0)))
        {
            throw new ContentError("Unsupported BMP dimensions or encoding");
        }

        int pixelOffset = (int)pixelOffsetU32;
        byte[] rgba = new byte[width * height * 4];
        var palette = new (byte R, byte G, byte B, byte A)[256];
        int paletteCount = 0;

        if (bits == 8)
        {
            uint dibHeaderSize = Unsigned(14);
            if (dibHeaderSize > (uint)(pixelOffset - 14))
                throw new ContentError("Invalid BMP palette");

            int paletteOffset = 14 + (int)dibHeaderSize;
            uint specified = Unsigned(46);
            int colors = specified == 0 ? 256 : (specified <= 256 ? (int)specified : -1);
            if (colors <= 0 || colors > 256 || (long)paletteOffset + colors * 4 > pixelOffset)
                throw new ContentError("Invalid BMP palette");

            for (int index = 0; index < colors; index++)
            {
                int off = paletteOffset + index * 4;
                palette[index] = (data[off + 2], data[off + 1], data[off], 255);
            }
            paletteCount = colors;
        }

        void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                throw new ContentError("BMP run exceeds image bounds");

            int row = signedHeight > 0 ? height - 1 - y : y;
            int off = (row * width + x) * 4;
            rgba[off] = r;
            rgba[off + 1] = g;
            rgba[off + 2] = b;
            rgba[off + 3] = a;
        }

        void IndexedPixel(int x, int y, byte index)
        {
            if (index >= paletteCount)
                throw new ContentError("BMP references a missing palette color");

            var (r, g, b, a) = palette[index];
            SetPixel(x, y, r, g, b, a);
        }

        if (compression == 1)
        {
            int offset = pixelOffset;
            int x = 0;
            int y = 0;
            bool ended = false;

            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                    IndexedPixel(column, row, 0);
            }

            byte Next()
            {
                if (offset >= data.Length)
                    throw new ContentError("Truncated BMP RLE stream");
                return data[offset++];
            }

            while (!ended)
            {
                int count = Next();
                byte value = Next();
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        IndexedPixel(x, y, value);
                        x++;
                    }
                }
                else
                {
                    switch (value)
                    {
                        case 0:
                            x = 0;
                            y++;
                            break;
                        case 1:
                            ended = true;
                            break;
                        case 2:
                            x += Next();
                            y += Next();
                            if (x > width || y >= height)
                                throw new ContentError("BMP delta exceeds image bounds");
                            break;
                        default:
                            for (int i = 0; i < value; i++)
                            {
                                IndexedPixel(x, y, Next());
                                x++;
                            }
                            if ((value & 1) != 0)
                                _ = Next();
                            break;
                    }
                }
            }
        }
        else
        {
            long strideLong = ((long)width * bits + 31) / 32 * 4;
            if (strideLong * height > data.Length - pixelOffset)
                throw new ContentError("Truncated BMP pixel data");

            int stride = (int)strideLong;
            int bytesPerPixel = bits / 8;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int off = pixelOffset + y * stride + x * bytesPerPixel;
                    if (bits == 8)
                    {
                        IndexedPixel(x, y, data[off]);
                    }
                    else
                    {
                        SetPixel(x, y, data[off + 2], data[off + 1], data[off], 255);
                    }
                }
            }
        }

        Width = width;
        Height = height;
        Rgba = rgba;
    }
}

/// <summary>
/// Decodes uncompressed and RLE-compressed TGA images (colormapped, truecolor, grayscale) to top-down RGBA8888.
/// </summary>
public sealed class SimsTarga
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public SimsTarga(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 18)
            throw new ContentError("Truncated TGA header");

        int Word(int off) => data[off] | (data[off + 1] << 8);

        int kind = data[2];
        int baseKind = kind >= 9 ? kind - 8 : kind;
        int width = Word(12);
        int height = Word(14);
        int bits = data[16];
        byte descriptor = data[17];
        int paletteStart = Word(3);
        int paletteCount = Word(5);
        int paletteBits = data[7];

        bool validKind = kind is 1 or 2 or 3 or 9 or 10 or 11;
        bool validEncoding =
            (baseKind == 1 && data[1] == 1 && bits is 8 or 16 && paletteCount > 0 && paletteStart + paletteCount <= 65536 && paletteBits is 16 or 24 or 32) ||
            (baseKind == 2 && data[1] == 0 && bits is 16 or 24 or 32) ||
            (baseKind == 3 && data[1] == 0 && bits is 8 or 16);

        if (!validKind || width <= 0 || height <= 0 || width > 4096 || height > 4096 || (descriptor & 0xC0) != 0 || !validEncoding)
            throw new ContentError("Unsupported TGA dimensions or encoding");

        int offset = 18 + data[0];

        byte Next()
        {
            if (offset >= data.Length)
                throw new ContentError("Truncated TGA pixel data");
            return data[offset++];
        }

        (byte R, byte G, byte B, byte A) ReadColor(int depth, bool alpha)
        {
            if (depth == 16)
            {
                ushort value = (ushort)(Next() | (Next() << 8));
                static byte Expand(int v) => (byte)((v & 31) * 255 / 31);
                return (
                    Expand(value >> 10),
                    Expand(value >> 5),
                    Expand(value),
                    (byte)(alpha && (value & 0x8000) == 0 ? 0 : 255));
            }

            byte blue = Next();
            byte green = Next();
            byte red = Next();
            byte opacity = depth == 32 ? Next() : (byte)255;
            return (red, green, blue, alpha ? opacity : (byte)255);
        }

        (byte R, byte G, byte B, byte A)[]? palette = null;
        if (baseKind == 1)
        {
            palette = new (byte R, byte G, byte B, byte A)[paletteCount];
            bool paletteAlpha = paletteBits == 32 || (descriptor & 15) > 0;
            for (int i = 0; i < paletteCount; i++)
                palette[i] = ReadColor(paletteBits, paletteAlpha);
        }

        (byte R, byte G, byte B, byte A) ReadPixel()
        {
            if (baseKind == 1)
            {
                int index = Next();
                if (bits == 16)
                    index |= Next() << 8;
                if (index < paletteStart || index >= paletteStart + paletteCount)
                    throw new ContentError("TGA references a missing palette color");
                return palette![index - paletteStart];
            }

            if (baseKind == 3)
            {
                byte value = Next();
                byte alpha = bits == 16 ? Next() : (byte)255;
                return (value, value, value, alpha);
            }

            return ReadColor(bits, (descriptor & 15) > 0);
        }

        int totalPixels = width * height;
        byte[] output = new byte[totalPixels * 4];
        int position = 0;

        void Append((byte R, byte G, byte B, byte A) value)
        {
            if (position >= totalPixels)
                throw new ContentError("TGA run exceeds image bounds");

            int row = position / width;
            int column = position % width;
            int x = (descriptor & 0x10) == 0 ? column : width - 1 - column;
            int y = (descriptor & 0x20) != 0 ? row : height - 1 - row;
            int target = (y * width + x) * 4;
            output[target] = value.R;
            output[target + 1] = value.G;
            output[target + 2] = value.B;
            output[target + 3] = value.A;
            position++;
        }

        while (position < totalPixels)
        {
            if (kind >= 9)
            {
                byte packet = Next();
                int count = (packet & 0x7F) + 1;
                if (count > totalPixels - position)
                    throw new ContentError("TGA run exceeds image bounds");

                if ((packet & 0x80) != 0)
                {
                    var value = ReadPixel();
                    for (int i = 0; i < count; i++)
                        Append(value);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        Append(ReadPixel());
                }
            }
            else
            {
                Append(ReadPixel());
            }
        }

        Width = width;
        Height = height;
        Rgba = output;
    }
}

/// <summary>
/// Sprite pixel post-processing: chroma keying, sRGB-correct premultiplication, and silhouette fringe decontamination.
/// </summary>
public static class SpritePixels
{
    private static readonly float[] SrgbToLinear = CreateSrgbToLinear();
    private static readonly byte[] LinearToSrgb = CreateLinearToSrgb();

    private static float[] CreateSrgbToLinear()
    {
        var table = new float[256];
        for (int value = 0; value < 256; value++)
        {
            double encoded = value / 255.0;
            double linear = encoded <= 0.04045
                ? encoded / 12.92
                : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            table[value] = (float)linear;
        }
        return table;
    }

    private static byte[] CreateLinearToSrgb()
    {
        var table = new byte[4096];
        for (int index = 0; index < 4096; index++)
        {
            double linearValue = index / 4095.0;
            double encoded = linearValue <= 0.0031308
                ? linearValue * 12.92
                : 1.055 * Math.Pow(linearValue, 1.0 / 2.4) - 0.055;
            int rounded = (int)Math.Round(encoded * 255.0, MidpointRounding.AwayFromZero);
            table[index] = (byte)Math.Clamp(rounded, 0, 255);
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte EncodeSrgb(float linearValue)
    {
        float clamped = Math.Clamp(linearValue, 0.0f, 1.0f);
        int idx = Math.Clamp((int)(clamped * 4095.0f + 0.5f), 0, 4095);
        return LinearToSrgb[idx];
    }

    public static byte[] PremultipliedSRGB(
        byte[] pixels,
        bool removeMagenta = false,
        bool removeBlue = false,
        bool decontaminateEdges = false,
        int width = 0,
        int height = 0)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length % 4 != 0)
            throw new ContentError("RGBA pixel data is not aligned");

        float[] linear = SrgbToLinear;
        byte[] output = new byte[pixels.Length];
        Buffer.BlockCopy(pixels, 0, output, 0, pixels.Length);

        bool hasSemiTransparent = false;
        bool hasOpaque = false;

        for (int offset = 0; offset < output.Length; offset += 4)
        {
            if (removeMagenta && output[offset] >= 180 && output[offset + 1] <= 70 && output[offset + 2] >= 180)
            {
                output[offset] = 0;
                output[offset + 1] = 0;
                output[offset + 2] = 0;
                output[offset + 3] = 0;
            }

            if (removeBlue && output[offset] <= 8 && output[offset + 1] <= 8 && output[offset + 2] >= 248)
            {
                output[offset] = 0;
                output[offset + 1] = 0;
                output[offset + 2] = 0;
                output[offset + 3] = 0;
            }

            byte alpha = output[offset + 3];
            if (alpha == 0)
            {
                output[offset] = 0;
                output[offset + 1] = 0;
                output[offset + 2] = 0;
            }
            else if (alpha >= 248)
            {
                hasOpaque = true;
            }
            else
            {
                hasSemiTransparent = true;
            }
        }

        int pixelCount = pixels.Length / 4;
        bool canDecontaminate = decontaminateEdges &&
                                hasSemiTransparent &&
                                hasOpaque &&
                                width > 1 &&
                                height > 1 &&
                                (long)width * height == pixelCount;

        if (canDecontaminate)
        {
            byte[] source = new byte[output.Length];
            Buffer.BlockCopy(output, 0, source, 0, output.Length);
            byte[] fringeTag = new byte[pixelCount];

            // Pass A: semi-transparent pixels bordering transparent exterior (or tight-cropped sprite bounds).
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                bool touchesVerticalBorder = y == 0 || y == height - 1;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    byte a = source[idx * 4 + 3];
                    if (a == 0 || a >= 248)
                        continue;

                    bool touchesExterior = touchesVerticalBorder || x == 0 || x == width - 1;
                    if (!touchesExterior)
                    {
                        for (int dy = -1; dy <= 1 && !touchesExterior; dy++)
                        {
                            int ny = y + dy;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                    continue;
                                if (source[(ny * width + (x + dx)) * 4 + 3] <= 8)
                                {
                                    touchesExterior = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (touchesExterior)
                        fringeTag[idx] = 1;
                }
            }

            // Pass B: second-ring silhouette fringe (without crossing opaque window/object frames).
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                int yMin = Math.Max(0, y - 1);
                int yMax = Math.Min(height - 1, y + 1);
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    if (fringeTag[idx] != 0)
                        continue;

                    byte a = source[idx * 4 + 3];
                    if (a == 0 || a >= 248)
                        continue;

                    int xMin = Math.Max(0, x - 1);
                    int xMax = Math.Min(width - 1, x + 1);
                    bool found = false;
                    for (int ny = yMin; ny <= yMax && !found; ny++)
                    {
                        for (int nx = xMin; nx <= xMax; nx++)
                        {
                            if (fringeTag[ny * width + nx] == 1)
                            {
                                fringeTag[idx] = 2;
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    int offset = idx * 4;
                    byte alpha = source[offset + 3];
                    if (alpha == 0 || alpha == 255)
                        continue;

                    float alphaScale = alpha * (1.0f / 255.0f);
                    if (fringeTag[idx] != 0)
                    {
                        float sumR = 0, sumG = 0, sumB = 0, weightSum = 0;
                        for (int radius = 1; radius <= 3 && weightSum == 0; radius++)
                        {
                            int yMin = Math.Max(0, y - radius);
                            int yMax = Math.Min(height - 1, y + radius);
                            int xMin = Math.Max(0, x - radius);
                            int xMax = Math.Min(width - 1, x + radius);
                            for (int ny = yMin; ny <= yMax; ny++)
                            {
                                int dy = ny - y;
                                for (int nx = xMin; nx <= xMax; nx++)
                                {
                                    int dx = nx - x;
                                    if (dx == 0 && dy == 0)
                                        continue;
                                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                                        continue;

                                    int nOff = (ny * width + nx) * 4;
                                    if (source[nOff + 3] < 248)
                                        continue;

                                    float w = 1.0f / (dx * dx + dy * dy);
                                    sumR += linear[source[nOff]] * w;
                                    sumG += linear[source[nOff + 1]] * w;
                                    sumB += linear[source[nOff + 2]] * w;
                                    weightSum += w;
                                }
                            }
                        }

                        if (weightSum > 0)
                        {
                            float invWeight = alphaScale / weightSum;
                            output[offset] = EncodeSrgb(sumR * invWeight);
                            output[offset + 1] = EncodeSrgb(sumG * invWeight);
                            output[offset + 2] = EncodeSrgb(sumB * invWeight);
                            continue;
                        }
                    }

                    output[offset] = EncodeSrgb(linear[source[offset]] * alphaScale);
                    output[offset + 1] = EncodeSrgb(linear[source[offset + 1]] * alphaScale);
                    output[offset + 2] = EncodeSrgb(linear[source[offset + 2]] * alphaScale);
                }
            }

            return output;
        }

        for (int offset = 0; offset < output.Length; offset += 4)
        {
            byte alpha = output[offset + 3];
            if (alpha > 0 && alpha < 255)
            {
                float alphaScale = alpha * (1.0f / 255.0f);
                output[offset] = EncodeSrgb(linear[output[offset]] * alphaScale);
                output[offset + 1] = EncodeSrgb(linear[output[offset + 1]] * alphaScale);
                output[offset + 2] = EncodeSrgb(linear[output[offset + 2]] * alphaScale);
            }
        }

        return output;
    }
}

/// <summary>
/// Decodes Sims FFN bitmap fonts (FNTF v101) and measures, wraps, and rasterizes labels.
/// </summary>
public sealed class SimsBitmapFont
{
    public readonly record struct Glyph(
        int Width,
        int Height,
        int X,
        int Y,
        int OffsetX,
        int OffsetY,
        int Advance);

    public int Width { get; }
    public int Height { get; }
    public byte[] Coverage { get; }
    public IReadOnlyDictionary<ushort, Glyph> Glyphs { get; }
    public int LineHeight { get; }
    public int SpaceAdvance { get; }

    public SimsBitmapFont(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        long Value(int offset, int count)
        {
            if (offset < 0 || count <= 0 || count > 4 || offset > data.Length - count)
                throw new ContentError("Truncated FFN font");
            long v = 0;
            for (int i = 0; i < count; i++)
                v |= (long)data[offset + i] << (i * 8);
            return v;
        }

        if (data.Length < 32 ||
            data.Length > 16 * 1024 * 1024 ||
            !data.AsSpan(0, 4).SequenceEqual("FNTF"u8) ||
            Value(4, 4) != data.Length ||
            Value(8, 2) != 101)
        {
            throw new ContentError("Unsupported FFN font header");
        }

        int count = (int)Value(10, 2);
        long tableLong = Value(20, 4);
        long atlasLong = Value(28, 4);
        if (count <= 0 ||
            count > 256 ||
            tableLong < 32 ||
            tableLong > data.Length - count * 11 ||
            atlasLong < tableLong + count * 11 ||
            atlasLong > data.Length - 16 ||
            Value((int)atlasLong, 4) != 0x7A)
        {
            throw new ContentError("Unsupported FFN glyph table or atlas");
        }

        int table = (int)tableLong;
        int atlas = (int)atlasLong;
        int width = (int)Value(atlas + 4, 2);
        int height = (int)Value(atlas + 6, 2);
        if (width <= 0 ||
            height <= 0 ||
            width > 4096 ||
            height > 4096 ||
            (width & 1) != 0 ||
            (long)width * height / 2 != data.Length - atlas - 16)
        {
            throw new ContentError("Invalid FFN atlas dimensions");
        }

        var records = new Dictionary<ushort, Glyph>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = table + index * 11;
            ushort code = (ushort)Value(offset, 2);
            int w = (int)Value(offset + 2, 1);
            int h = (int)Value(offset + 3, 1);
            int x = (int)Value(offset + 4, 2);
            int y = (int)Value(offset + 6, 2);
            int dx = unchecked((sbyte)data[offset + 9]);
            int dy = unchecked((sbyte)data[offset + 10]);

            if (records.ContainsKey(code) || w <= 0 || h <= 0 || x + w > width || y + h > height)
                throw new ContentError("Invalid FFN glyph rectangle");

            records[code] = new Glyph(w, h, x, y, dx, dy, w + dx);
        }

        var measured = new List<Glyph>(records.Count);
        foreach (var pair in records)
        {
            if (pair.Key != 160)
                measured.Add(pair.Value);
        }

        if (measured.Count == 0)
            throw new ContentError("FFN font has no measurable glyphs");

        int minimumY = 0;
        int maximumBottom = int.MinValue;
        long totalWidth = 0;
        foreach (Glyph g in measured)
        {
            if (g.OffsetY < minimumY)
                minimumY = g.OffsetY;
            int bottom = g.Height + g.OffsetY;
            if (bottom > maximumBottom)
                maximumBottom = bottom;
            totalWidth += g.Width;
        }

        LineHeight = maximumBottom - minimumY;
        SpaceAdvance = (int)Math.Floor(0.5 * totalWidth / measured.Count + 0.5);

        var normalized = new Dictionary<ushort, Glyph>(records.Count);
        foreach (var pair in records)
        {
            Glyph g = pair.Value;
            normalized[pair.Key] = g with { OffsetY = g.OffsetY - minimumY };
        }
        Glyphs = normalized;

        byte[] pixels = new byte[width * height];
        for (int index = 0; index < pixels.Length; index++)
        {
            byte packed = data[atlas + 16 + index / 2];
            int nibble = (index & 1) == 0 ? (packed >> 4) : (packed & 15);
            pixels[index] = (byte)(nibble * 17);
        }

        Width = width;
        Height = height;
        Coverage = pixels;
    }

    private Glyph? ResolveGlyph(ushort character)
    {
        if (Glyphs.TryGetValue(character, out Glyph direct))
            return direct;

        ushort? cp1252 = character switch
        {
            0x20AC => 0x80,
            0x201A => 0x82,
            0x0192 => 0x83,
            0x201E => 0x84,
            0x2026 => 0x85,
            0x2020 => 0x86,
            0x2021 => 0x87,
            0x02C6 => 0x88,
            0x2030 => 0x89,
            0x0160 => 0x8A,
            0x2039 => 0x8B,
            0x0152 => 0x8C,
            0x017D => 0x8E,
            0x2018 => 0x91,
            0x2019 => 0x92,
            0x201C => 0x93,
            0x201D => 0x94,
            0x2022 => 0x95,
            0x2013 => 0x96,
            0x2014 => 0x97,
            0x02DC => 0x98,
            0x2122 => 0x99,
            0x0161 => 0x9A,
            0x203A => 0x9B,
            0x0153 => 0x9C,
            0x017E => 0x9E,
            0x0178 => 0x9F,
            _ => null,
        };

        if (cp1252 is ushort mappedCode && Glyphs.TryGetValue(mappedCode, out Glyph mapped))
            return mapped;

        ushort fallbackCode = cp1252 ?? character;
        ushort? asciiFallback = fallbackCode switch
        {
            0x91 or 0x92 or 0x82 => 0x27,
            0x93 or 0x94 or 0x84 => 0x22,
            0x96 or 0x97 => 0x2D,
            0x85 => 0x2E,
            0x88 => 0x5E,
            0x8B => 0x3C,
            0x95 => 0x2A,
            0x98 => 0x7E,
            0x99 => 0x54,
            0x9B => 0x3E,
            0xA0 => 0x20,
            _ => null,
        };

        if (asciiFallback is ushort asciiCode && Glyphs.TryGetValue(asciiCode, out Glyph ascii))
            return ascii;

        return Glyphs.TryGetValue(63, out Glyph question) ? question : null;
    }

    public int Advance(ushort character)
    {
        if (character == 32)
            return SpaceAdvance;
        return ResolveGlyph(character)?.Advance ?? SpaceAdvance;
    }

    public int TextWidth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int total = 0;
        foreach (char ch in text)
            total += Advance(ch);
        return total;
    }

    private static readonly char[] WordSeparators = [' ', '\t'];

    public List<string> WrappedLines(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0 || text.Length > 8192)
            throw new ContentError("Invalid bitmap font paragraph");

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = new List<string>();
        string[] paragraphs = normalized.Split('\n', StringSplitOptions.None);

        foreach (string paragraph in paragraphs)
        {
            string line = string.Empty;
            string[] words = paragraph.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            foreach (string word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (TextWidth(candidate) <= width && candidate.Length <= 512)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(line);
                    line = string.Empty;
                }

                foreach (char character in word)
                {
                    string charCandidate = line + character;
                    if (line.Length > 0 && (TextWidth(charCandidate) > width || charCandidate.Length > 512))
                    {
                        lines.Add(line);
                        line = string.Empty;
                    }
                    line += character;
                }
            }

            lines.Add(line);
        }

        if (lines.Count > 512)
            throw new ContentError("Bitmap font paragraph has too many lines");

        return lines;
    }

    public (int Width, int Height, byte[] Rgba) Rasterize(string text, (byte R, byte G, byte B) color)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 512 || text.Contains('\n') || text.Contains('\r'))
            throw new ContentError("Invalid bitmap font label");

        int width = Math.Max(1, TextWidth(text) + 6);
        int height = LineHeight + 6;
        if (width > 8192 || height <= 0 || height > 1024)
            throw new ContentError("Bitmap font label exceeds size limits");

        byte[] output = new byte[width * height * 4];
        int cursor = 3;
        foreach (char ch in text)
        {
            ushort character = ch;
            if (character != 32 && ResolveGlyph(character) is Glyph glyph)
            {
                for (int y = 0; y < glyph.Height; y++)
                {
                    for (int x = 0; x < glyph.Width; x++)
                    {
                        int targetX = cursor + glyph.OffsetX + x;
                        int targetY = 3 + glyph.OffsetY + y;
                        if (targetX < 0 || targetY < 0 || targetX >= width || targetY >= height)
                            continue;

                        int off = (targetY * width + targetX) * 4;
                        output[off] = color.R;
                        output[off + 1] = color.G;
                        output[off + 2] = color.B;
                        output[off + 3] = Coverage[(glyph.Y + y) * Width + glyph.X + x];
                    }
                }
            }

            cursor += Advance(character);
        }

        return (width, height, output);
    }

    public (int Width, int Height, byte[] Rgba) Rasterize(string text, byte r, byte g, byte b) =>
        Rasterize(text, (r, g, b));
}
