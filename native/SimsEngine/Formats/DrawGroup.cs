namespace SimsEngine.Formats;

/// <summary>One sprite of a draw group image: which SPR2 frame to draw, its pixel offset, and three floats Sims.exe
/// keeps together at +0x10 of the sprite entry, in stored order. They are an object-space offset; which axis each one
/// is has not been traced in Sims.exe.</summary>
public readonly record struct DrawGroupSprite(
    int SpriteId, int Frame, int X, int Y, uint Flags, float Offset1, float Offset2, float Offset3);

/// <summary>The sprites drawn for one direction and zoom level.</summary>
public sealed record DrawGroupImage(int Direction, int Zoom, IReadOnlyList<DrawGroupSprite> Sprites);

/// <summary>A draw group (DGRP), as Sims.exe FUN_0040780c and FUN_004078d2 read it: an s16 version picks the layout.
/// <para>Below 20003 (FUN_00407915): an s16 image count; per image an s16 sprite count, an s8 direction and an s8
/// zoom; per sprite an s16 type, sprite id, frame, flags, x and y, and from version 20001 the first offset float.
/// Sims.exe keeps only the sprites of type -3; the others (-5 in the game files) are dropped.</para>
/// <para>From 20003 (FUN_00407f40, FUN_00407b00, FUN_00408006, FUN_00407b36): a u32 image count; per image a u32
/// direction, zoom and sprite count; per sprite a u32 sprite id and frame, s32 x and y, the first offset float, u32
/// flags, and from version 20004 the second and third offset floats.</para></summary>
public sealed class DrawGroup
{
    public int Version { get; }
    public IReadOnlyList<DrawGroupImage> Images { get; }

    /// <summary>Sprites of the old layout that Sims.exe drops because their type is not -3.</summary>
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
