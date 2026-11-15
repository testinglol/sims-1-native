using System.Buffers.Binary;

/// <summary>Small BMP reader/writer used by the native renderer and its asset pipeline.</summary>
internal static class BitmapCodec
{
    internal static byte[] Write(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid RGBA bitmap dimensions");
        var stride = checked((width * 3 + 3) & ~3);
        var data = new byte[checked(54 + stride * height)];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(34), stride * height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = (y * width + x) * 4;
            var target = 54 + (height - 1 - y) * stride + x * 3;
            data[target] = rgba[source + 2]; data[target + 1] = rgba[source + 1]; data[target + 2] = rgba[source];
        }
        return data;
    }

    /// <summary>Decodes uncompressed 8/24/32-bit and RLE8 BMP data to top-down RGBA.</summary>
    internal static (int Width, int Height, byte[] Rgba) Read(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') throw new InvalidDataException("Not a BMP");
        var pixels = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var header = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        if (rawHeight == int.MinValue) throw new InvalidDataException("BMP dimensions are invalid");
        var height = Math.Abs(rawHeight);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30));
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || pixels < 0 || pixels > data.Length || header < 40 || 14 + header > data.Length)
            throw new InvalidDataException("BMP dimensions are invalid");
        var rgba = new byte[checked(width * height * 4)];
        int Row(int y) => rawHeight > 0 ? height - 1 - y : y;
        void Put(int x, int y, byte r, byte g, byte b)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            var offset = (Row(y) * width + x) * 4;
            (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]) = (r, g, b, 255);
        }
        if (bits == 8)
        {
            var used = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(46));
            var paletteCount = used > 0 ? Math.Min(used, 256) : 256;
            var palette = new byte[paletteCount][];
            for (var i = 0; i < paletteCount; i++)
            {
                var offset = 14 + header + i * 4;
                if (offset + 3 > data.Length) throw new InvalidDataException("BMP palette is truncated");
                palette[i] = [data[offset + 2], data[offset + 1], data[offset]];
            }
            void Index(int x, int y, byte value)
            {
                if (value >= palette.Length) throw new InvalidDataException("BMP palette index is out of range");
                Put(x, y, palette[value][0], palette[value][1], palette[value][2]);
            }
            if (compression == 0)
            {
                var stride = (width + 3) & ~3;
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var offset = pixels + y * stride + x;
                    if (offset >= data.Length) throw new InvalidDataException("BMP pixel data is truncated");
                    Index(x, y, data[offset]);
                }
            }
            else if (compression == 1)
            {
                var position = pixels; var column = 0; var line = 0;
                while (position + 1 < data.Length && line < height)
                {
                    var count = data[position++]; var value = data[position++];
                    if (count > 0) { for (var i = 0; i < count; i++) Index(column++, line, value); continue; }
                    if (value == 0) { column = 0; line++; }
                    else if (value == 1) break;
                    else if (value == 2)
                    {
                        if (position + 1 >= data.Length) throw new InvalidDataException("BMP RLE data is truncated");
                        column += data[position++]; line += data[position++];
                    }
                    else
                    {
                        for (var i = 0; i < value; i++)
                        {
                            if (position >= data.Length) throw new InvalidDataException("BMP RLE data is truncated");
                            Index(column++, line, data[position++]);
                        }
                        if ((value & 1) != 0) position++;
                    }
                }
            }
            else throw new InvalidDataException("Unsupported 8-bit BMP compression");
        }
        else if ((bits == 24 || bits == 32) && compression is 0 or 3)
        {
            var size = bits / 8;
            var stride = checked((width * size + 3) & ~3);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = pixels + y * stride + x * size;
                if (offset + size > data.Length) throw new InvalidDataException("BMP pixel data is truncated");
                Put(x, y, data[offset + 2], data[offset + 1], data[offset]);
            }
        }
        else throw new InvalidDataException($"Unsupported {bits}-bit BMP");
        return (width, height, rgba);
    }
}
