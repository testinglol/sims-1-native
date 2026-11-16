namespace SimsEngine.Formats;

/// <summary>Represents a sprite entry within a draw group image, defining the sprite frame, pixel offsets,
/// rendering flags, and 3D object-space offset.</summary>
public readonly record struct DrawGroupSprite(
    int SpriteId, int Frame, int X, int Y, uint Flags, float Offset1, float Offset2, float Offset3);

public sealed record DrawGroupImage(int Direction, int Zoom, IReadOnlyList<DrawGroupSprite> Sprites);

/// <summary>Represents a draw group (DGRP) resource. In versions below 20003, image and sprite counts are
/// 16-bit and only sprite type -3 entries are retained; versions 20003 and later widen fields to 32-bit and
/// include 3D offsets.</summary>
public sealed class DrawGroup
{
    public int Version { get; }
    public IReadOnlyList<DrawGroupImage> Images { get; }

    /// <summary>Number of legacy-format sprite records skipped because their sprite type was not -3.</summary>
    public int DroppedSprites { get; }

    private DrawGroup(int version, IReadOnlyList<DrawGroupImage> images, int droppedSprites)
    {
        Version = version;
        Images = images;
        DroppedSprites = droppedSprites;
    }

    public static DrawGroup Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        int version = reader.S16();
        var dropped = 0;
        var images = version < 20003 ? ReadOld(ref reader, version, ref dropped) : ReadNew(ref reader, version);
        return new DrawGroup(version, images, dropped);
    }

    private static List<DrawGroupImage> ReadOld(ref ResourceReader reader, int version, ref int dropped)
    {
        int imageCount = reader.S16();
        if (imageCount < 0 || imageCount > reader.Remaining / 4) throw reader.Error($"{imageCount} images do not fit the chunk");
        var images = new List<DrawGroupImage>(imageCount);
        for (var image = 0; image < imageCount; image++)
        {
            int spriteCount = reader.S16();
            int direction = reader.S8();
            int zoom = reader.S8();
            if (spriteCount < 0) throw reader.Error($"sprite count {spriteCount} is negative");
            var sprites = new List<DrawGroupSprite>();
            for (var sprite = 0; sprite < spriteCount; sprite++)
            {
                int type = reader.S16();
                int id = reader.S16();
                int frame = reader.S16();
                var flags = (uint)(ushort)reader.S16();
                int x = reader.S16();
                int y = reader.S16();
                var offset1 = version > 20000 ? BitConverter.Int32BitsToSingle(reader.S32()) : 0f;
                if (type == -3) sprites.Add(new DrawGroupSprite(id, frame, x, y, flags, offset1, 0f, 0f));
                else dropped++;
            }
            images.Add(new DrawGroupImage(direction, zoom, sprites));
        }
        return images;
    }

    private static List<DrawGroupImage> ReadNew(ref ResourceReader reader, int version)
    {
        var imageCount = reader.U32();
        if (imageCount > reader.Remaining / 12) throw reader.Error($"{imageCount} images do not fit the chunk");
        var spriteSize = version >= 20004 ? 32 : 24;
        var images = new List<DrawGroupImage>((int)imageCount);
        for (var image = 0; image < imageCount; image++)
        {
            var direction = (int)reader.U32();
            var zoom = (int)reader.U32();
            var spriteCount = reader.U32();
            if (spriteCount > reader.Remaining / spriteSize) throw reader.Error($"{spriteCount} sprites do not fit the chunk");
            var sprites = new List<DrawGroupSprite>((int)spriteCount);
            for (var sprite = 0; sprite < spriteCount; sprite++)
            {
                var id = (int)reader.U32();
                var frame = (int)reader.U32();
                var x = reader.S32();
                var y = reader.S32();
                var offset1 = BitConverter.Int32BitsToSingle(reader.S32());
                var flags = reader.U32();
                float offset2 = 0f, offset3 = 0f;
                if (version >= 20004)
                {
                    offset2 = BitConverter.Int32BitsToSingle(reader.S32());
                    offset3 = BitConverter.Int32BitsToSingle(reader.S32());
                }
                sprites.Add(new DrawGroupSprite(id, frame, x, y, flags, offset1, offset2, offset3));
            }
            images.Add(new DrawGroupImage(direction, zoom, sprites));
        }
        return images;
    }
}
