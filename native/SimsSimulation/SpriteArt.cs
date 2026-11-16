using System.Buffers.Binary;
using System.IO.Compression;

internal static class SpriteArt
{
    /// <summary>A decoded sprite frame with RGBA pixels and a depth byte per pixel, where 255 indicates
    /// transparency.</summary>
    internal sealed class Frame
    {
        public required int Width;
        public required int Height;
        public short X, Y;
        public required byte[] Rgba;
        public required byte[] Depth;
    }

    private const int MaximumDimension = 2048;

    /// <summary>Decodes a SPR2 chunk body using RGB triplets provided by the referenced PALT chunk.</summary>
    internal static (uint DefaultPalette, List<Frame> Frames) DecodeSprite(byte[] data, Func<ushort, byte[]?> palette)
    {
        var version = Read32(data, 0);
        var frames = new List<Frame>();
        uint defaultPalette;
        if (version == 1000)
        {
            var count = Read32(data, 4);
            defaultPalette = Read32(data, 8);
            if (count > 4096 || 12 + count * 4 > data.Length) throw new InvalidDataException("Sprite frame table is truncated");
            for (var index = 0; index < count; index++)
                frames.Add(DecodeFrame(data, (int)Read32(data, 12 + index * 4), (ushort)defaultPalette, true, palette));
        }
        else if (version == 1001)
        {
            defaultPalette = Read32(data, 4);
            var count = Read32(data, 8);
            if (count > 4096) throw new InvalidDataException("Sprite has too many frames");
            var offset = 12;
            for (var index = 0; index < count; index++)
            {
                if (offset + 8 > data.Length) throw new InvalidDataException("Sprite frame is truncated");
                var size = (int)Read32(data, offset + 4);
                frames.Add(DecodeFrame(data, offset + 8, (ushort)defaultPalette, false, palette));
                offset += 8 + size;
            }
        }
        else throw new InvalidDataException($"Unsupported sprite version {version}");
        return (defaultPalette, frames);
    }

    private static Frame DecodeFrame(byte[] data, int offset, ushort defaultPalette, bool shared, Func<ushort, byte[]?> palette)
    {
        if (offset < 0 || offset + 16 > data.Length) throw new InvalidDataException("Sprite frame is out of range");
        int width = Read16(data, offset), height = Read16(data, offset + 2);
        var paletteID = Read16(data, offset + 8);
        if (shared || paletteID == 0 || paletteID == 0xA3A3) paletteID = defaultPalette;
        var transparent = Read16(data, offset + 10);
        var frame = new Frame
        {
            Width = width, Height = height, Y = (short)Read16(data, offset + 12), X = (short)Read16(data, offset + 14),
            Rgba = new byte[width * height * 4], Depth = Enumerable.Repeat((byte)255, width * height).ToArray()
        };
        if (width > MaximumDimension || height > MaximumDimension) throw new InvalidDataException("Sprite frame is too large");
        var colors = palette(paletteID) ?? new byte[768];
        void Put(int x, int y, byte index, byte alpha, byte depth)
        {
            if (x >= width || y >= height) throw new InvalidDataException("Sprite frame writes outside its bounds");
            var pixel = y * width + x;
            if (index * 3 + 2 < colors.Length)
            {
                frame.Rgba[pixel * 4] = colors[index * 3];
                frame.Rgba[pixel * 4 + 1] = colors[index * 3 + 1];
                frame.Rgba[pixel * 4 + 2] = colors[index * 3 + 2];
            }
            frame.Rgba[pixel * 4 + 3] = alpha;
            frame.Depth[pixel] = depth;
        }
        var cursor = offset + 16;
        var row = 0;
        while (cursor + 2 <= data.Length)
        {
            var marker = Read16(data, cursor);
            cursor += 2;
            var command = marker >> 13;
            var count = marker & 0x1FFF;
            if (command == 5) break;
            if (command == 4) { row += count; continue; }
            if (command != 0) throw new InvalidDataException($"Unknown sprite row command {command}");
            var end = cursor + count - 2;
            if (end > data.Length) throw new InvalidDataException("Sprite row is truncated");
            var x = 0;
            while (cursor + 2 <= end)
            {
                var pixels = Read16(data, cursor);
                cursor += 2;
                var kind = pixels >> 13;
                var run = pixels & 0x1FFF;
                switch (kind)
                {
                    case 1:
                    case 2:
                        var size = kind == 1 ? 2 : 3;
                        if (cursor + run * size > end) throw new InvalidDataException("Sprite pixels are truncated");
                        for (var column = 0; column < run; column++, cursor += size)
                            Put(x++, row, data[cursor + 1], kind == 1 ? (byte)255 : (byte)Math.Min(255, data[cursor + 2] * 255 / 31), data[cursor]);
                        if (kind == 2 && run * 3 % 2 != 0) cursor++;
                        break;
                    case 3:
                        x += run;
                        break;
                    case 6:
                        if (cursor + run > end) throw new InvalidDataException("Sprite pixels are truncated");
                        for (var column = 0; column < run; column++) Put(x++, row, data[cursor++], 255, 0);
                        if (run % 2 != 0) cursor++;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown sprite pixel command {kind}");
                }
            }
            cursor = end;
            row++;
        }
        return frame;
    }

    /// <summary>Encodes frames as a SPR2 chunk using version 1000 with a single shared palette for all
    /// frames.</summary>
    internal static byte[] EncodeSprite(IReadOnlyList<Frame> frames, ushort paletteID, byte[] palette)
    {
        var lookup = new Dictionary<int, byte>();
        byte Index(byte red, byte green, byte blue)
        {
            var key = red << 16 | green << 8 | blue;
            if (lookup.TryGetValue(key, out var found)) return found;
            var best = 0;
            var distance = int.MaxValue;
            for (var index = 0; index < 255; index++)
            {
                int dr = palette[index * 3] - red, dg = palette[index * 3 + 1] - green, db = palette[index * 3 + 2] - blue;
                var current = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (current < distance) (best, distance) = (index, current);
            }
            return lookup[key] = (byte)best;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1000u);
        writer.Write((uint)frames.Count);
        writer.Write((uint)paletteID);
        var table = stream.Position;
        foreach (var _ in frames) writer.Write(0u);
        var offsets = new List<uint>();
        foreach (var frame in frames)
        {
            offsets.Add((uint)stream.Position);
            writer.Write((ushort)frame.Width);
            writer.Write((ushort)frame.Height);
            writer.Write(7u);
            writer.Write(paletteID);
            writer.Write((ushort)255);
            writer.Write(frame.Y);
            writer.Write(frame.X);
            var blank = 0;
            for (var y = 0; y < frame.Height; y++)
            {
                using var line = new MemoryStream();
                using var output = new BinaryWriter(line);
                var x = 0;
                var solid = false;
                while (x < frame.Width)
                {
                    var command = Command(frame, y * frame.Width + x);
                    var run = 0;
                    var bytes = new List<byte>();
                    while (x < frame.Width && run < 0x1FFF && Command(frame, y * frame.Width + x) == command)
                    {
                        var pixel = y * frame.Width + x;
                        var index = Index(frame.Rgba[pixel * 4], frame.Rgba[pixel * 4 + 1], frame.Rgba[pixel * 4 + 2]);
                        if (command is 1 or 2) { bytes.Add(frame.Depth[pixel]); bytes.Add(index); }
                        if (command == 2) bytes.Add((byte)((frame.Rgba[pixel * 4 + 3] * 31 + 254) / 255));
                        if (command == 6) bytes.Add(index);
                        run++;
                        x++;
                    }
                    // A trailing transparent run is implied by the end of the row.
                    if (command == 3 && x == frame.Width) break;
                    solid |= command != 3;
                    if (bytes.Count % 2 != 0) bytes.Add(0);
                    output.Write((ushort)(command << 13 | run));
                    output.Write(bytes.ToArray());
                }
                if (!solid) { blank++; continue; }
                FlushBlank(writer, ref blank);
                var content = line.ToArray();
                if (content.Length + 2 > 0x1FFF) throw new InvalidDataException("A sprite row is too large to encode");
                writer.Write((ushort)(content.Length + 2));
                writer.Write(content);
            }
            FlushBlank(writer, ref blank);
            writer.Write((ushort)(5 << 13));
        }
        writer.Flush();
        stream.Position = table;
        foreach (var offset in offsets) writer.Write(offset);
        writer.Flush();
        return stream.ToArray();
    }

    private static void FlushBlank(BinaryWriter writer, ref int blank)
    {
        while (blank > 0)
        {
            var rows = Math.Min(blank, 0x1FFF);
            writer.Write((ushort)(4 << 13 | rows));
            blank -= rows;
        }
    }

    private static int Command(Frame frame, int pixel)
    {
        var alpha = frame.Rgba[pixel * 4 + 3];
        if (alpha == 0) return 3;
        if (alpha < 255) return 2;
        return frame.Depth[pixel] > 0 ? 1 : 6;
    }

    /// <summary>Generates a median-cut palette of up to 255 colors across all visible pixels, reserving
    /// palette entry 255 for transparency.</summary>
    internal static byte[] Quantize(IEnumerable<Frame> frames)
    {
        var counts = new Dictionary<int, int>();
        foreach (var frame in frames)
            for (var pixel = 0; pixel < frame.Width * frame.Height; pixel++)
                if (frame.Rgba[pixel * 4 + 3] > 0)
                {
                    var key = frame.Rgba[pixel * 4] << 16 | frame.Rgba[pixel * 4 + 1] << 8 | frame.Rgba[pixel * 4 + 2];
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
        var boxes = new List<List<(int Color, int Count)>> { counts.Select(pair => (pair.Key, pair.Value)).ToList() };
        while (boxes.Count < 255)
        {
            var splittable = boxes.Where(box => box.Count > 1).ToList();
            if (splittable.Count == 0) break;
            var (box, channel, _) = splittable.Select(box =>
            {
                var ranges = Enumerable.Range(0, 3).Select(channel => box.Max(item => Channel(item.Color, channel)) - box.Min(item => Channel(item.Color, channel))).ToArray();
                var widest = Array.IndexOf(ranges, ranges.Max());
                return (box, widest, score: (long)ranges[widest] * box.Sum(item => (long)item.Count));
            }).MaxBy(item => item.score);
            box.Sort((left, right) => Channel(left.Color, channel).CompareTo(Channel(right.Color, channel)));
            var total = box.Sum(item => (long)item.Count);
            long running = 0;
            var split = 1;
            for (; split < box.Count - 1; split++)
            {
                running += box[split - 1].Count;
                if (running * 2 >= total) break;
            }
            boxes.Remove(box);
            boxes.Add(box.Take(split).ToList());
            boxes.Add(box.Skip(split).ToList());
        }
        var palette = new byte[768];
        for (var index = 0; index < boxes.Count; index++)
        {
            var box = boxes[index];
            var weight = box.Sum(item => (long)item.Count);
            for (var channel = 0; channel < 3; channel++)
                palette[index * 3 + channel] = weight == 0 ? (byte)0 : (byte)Math.Round(box.Sum(item => (double)Channel(item.Color, channel) * item.Count) / weight);
        }
        // Sets the unused transparent palette entry to magenta.
        palette[765] = 255;
        palette[767] = 255;
        return palette;
    }

    private static int Channel(int color, int channel) => color >> (16 - channel * 8) & 0xFF;

    internal static byte[] WritePng(int width, int height, byte[] pixels, bool gray)
    {
        var channels = gray ? 1 : 4;
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels, y * width * channels, width * channels);
            }
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)(gray ? 0 : 6);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc(body));
        stream.Write(length);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ crc >> 1 : crc >> 1;
        return crc;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ crc >> 8;
        return crc ^ 0xFFFFFFFF;
    }

    internal static (int Width, int Height, byte[] Rgba) ReadPng(byte[] data, string name)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }))
            throw new InvalidDataException($"{name} is not a PNG image");
        int width = 0, height = 0, colorType = -1;
        byte[] palette = [], transparency = [];
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            if (length < 0 || offset + 12 + length > data.Length) throw new InvalidDataException($"{name} is truncated");
            var body = data.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    colorType = body[9];
                    if (body[8] != 8 || body[12] != 0)
                        throw new InvalidDataException($"{name} must be an 8-bit, non-interlaced PNG");
                    if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension) throw new InvalidDataException($"{name} has invalid dimensions");
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": transparency = body.ToArray(); break;
                case "IDAT": compressed.Write(body); break;
            }
            offset += 12 + length;
            if (type == "IEND") break;
        }
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"{name} has an unsupported color type") };
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var count = zlib.Read(raw, read, raw.Length - read);
                if (count == 0) throw new InvalidDataException($"{name} has too little image data");
                read += count;
            }
        }
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int value = raw[y * (stride + 1) + 1 + x];
                int left = x >= channels ? pixels[y * stride + x - channels] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int corner = x >= channels && y > 0 ? pixels[(y - 1) * stride + x - channels] : 0;
                value += filter switch
                {
                    0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new InvalidDataException($"{name} has an invalid row filter")
                };
                pixels[y * stride + x] = (byte)value;
            }
        }
        var rgba = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var source = pixel * channels;
            byte red, green, blue, alpha = 255;
            switch (colorType)
            {
                case 0: red = green = blue = pixels[source]; break;
                case 4: red = green = blue = pixels[source]; alpha = pixels[source + 1]; break;
                case 2: (red, green, blue) = (pixels[source], pixels[source + 1], pixels[source + 2]); break;
                case 3:
                    var index = pixels[source];
                    if (index * 3 + 2 >= palette.Length) throw new InvalidDataException($"{name} uses a color outside its palette");
                    (red, green, blue) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2]);
                    if (index < transparency.Length) alpha = transparency[index];
                    break;
                default: (red, green, blue, alpha) = (pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]); break;
            }
            (rgba[pixel * 4], rgba[pixel * 4 + 1], rgba[pixel * 4 + 2], rgba[pixel * 4 + 3]) = (red, green, blue, alpha);
        }
        return (width, height, rgba);
    }

    private static int Paeth(int left, int up, int corner)
    {
        var estimate = left + up - corner;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toCorner = Math.Abs(estimate - corner);
        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : corner;
    }

    /// <summary>Fills depth for newly visible pixels using the depth of the nearest originally visible
    /// pixel.</summary>
    internal static void FillDepth(Frame frame, byte[] originalDepth, byte[] originalRgba)
    {
        var count = frame.Width * frame.Height;
        var known = new byte[count];
        var queue = new Queue<int>();
        for (var pixel = 0; pixel < count; pixel++)
            if (originalRgba[pixel * 4 + 3] > 0)
            {
                known[pixel] = 1;
                frame.Depth[pixel] = originalDepth[pixel];
                queue.Enqueue(pixel);
            }
        if (queue.Count == 0)
        {
            for (var pixel = 0; pixel < count; pixel++) frame.Depth[pixel] = (byte)(frame.Rgba[pixel * 4 + 3] > 0 ? 0 : 255);
            return;
        }
        while (queue.Count > 0)
        {
            var pixel = queue.Dequeue();
            int x = pixel % frame.Width, y = pixel / frame.Width;
            foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
            {
                if (nx < 0 || ny < 0 || nx >= frame.Width || ny >= frame.Height) continue;
                var next = ny * frame.Width + nx;
                if (known[next] != 0) continue;
                known[next] = 1;
                frame.Depth[next] = frame.Depth[pixel];
                queue.Enqueue(next);
            }
        }
        for (var pixel = 0; pixel < count; pixel++)
            if (frame.Rgba[pixel * 4 + 3] == 0) frame.Depth[pixel] = 255;
    }

    private static uint Read32(byte[] data, int offset) =>
        offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : throw new InvalidDataException("Sprite is truncated");

    private static ushort Read16(byte[] data, int offset) =>
        offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : throw new InvalidDataException("Sprite is truncated");
}
