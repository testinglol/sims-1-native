using System.Buffers.Binary;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>PALT, SPR#, SPR2 and DGRP on hand-built chunks that follow FUN_004b9685, FUN_004c173f, FUN_004c29b0,
/// FUN_004bf678 and FUN_004078d2.</summary>
public class SpriteTests
{
    private static byte[] BigEndian(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index * 4), values[index]);
        return bytes;
    }

    [Fact]
    public void PaletteSkipsEightBytesBeforeTheColours()
    {
        var chunk = new ChunkBuilder().U32(1).U32(2).U32(0).U32(0).Raw([10, 20, 30, 40, 50, 60]);
        var (file, iff) = chunk.InFile("PALT");
        Assert.Equal([new PaletteColor(10, 20, 30), new PaletteColor(40, 50, 60)], Palette.Read(file, iff).Colors);
    }

    [Fact]
    public void PaletteCountPastTheDataIsAnError()
    {
        var (file, iff) = new ChunkBuilder().U32(1).U32(256).U32(0).U32(0).Raw([1, 2, 3]).InFile("PALT");
        Assert.Throws<ResourceFormatException>(() => Palette.Read(file, iff));
    }

    [Fact]
    public void SpriteFrameCommandsDrawRowsAndPadLiteralRuns()
    {
        // Header (version, count, palette) and one offset: the frame starts at 16. Row 0: one literal pixel (the run
        // ends at an odd offset, so a padding byte follows), skip one pixel, fill one. Row 1 is left empty.
        var chunk = new ChunkBuilder().U32(504).U32(1).U32(7).U32(16)
            .U16(unchecked((ushort)-4)).U16(6).U16(2).U16(3)
            .U8(4).U8(0).U8(3).U8(1).U8(9).U8(0xEE).U8(1).U8(1).U8(2).U8(1).U8(7).U8(0).U8(5).U8(0);
        var (file, iff) = chunk.InFile("SPR#");
        var sprite = Sprite.Read(file, iff);
        Assert.Equal((7, false, 1), (sprite.PaletteId, sprite.BigEndian, sprite.Sets));
        var frame = sprite.Frames.Single();
        Assert.Equal((3, 2), (frame.Width, frame.Height));
        Assert.Equal((-4, 6), (frame.BoundsLeft, frame.BoundsTop));
        Assert.Equal([true, false, true, false, false, false], frame.Opaque);
        Assert.Equal((byte)9, frame.Indices[0]);
        Assert.Equal((byte)7, frame.Indices[2]);
        Assert.False(frame.WritesOutside);
    }

    [Fact]
    public void BigEndianSpriteSwapsHeaderOffsetsAndFrameSize()
    {
        var frame = new byte[] { 0, 7, 0xff, 0xfe, 0, 1, 0, 1, 4, 0, 2, 1, 5, 0, 5, 0 };
        var chunk = new ChunkBuilder().Raw(BigEndian(503, 2, 3, 20, 20)).Raw(frame);
        var (file, iff) = chunk.InFile("SPR#");
        var sprite = Sprite.Read(file, iff);
        Assert.Equal((true, 503u, 3, 2), (sprite.BigEndian, sprite.Version, sprite.PaletteId, sprite.Sets));
        Assert.All(sprite.Frames, decoded => Assert.Equal((7, -2), (decoded.BoundsLeft, decoded.BoundsTop)));
        Assert.All(sprite.Frames, decoded => Assert.Equal([true], decoded.Opaque));
    }

    [Fact]
    public void SpriteBefore502HasNoPaletteId()
    {
        var chunk = new ChunkBuilder().U32(500).U32(1).U32(12).U32(0).U16(0).U16(0).U8(5).U8(0);
        var (file, iff) = chunk.InFile("SPR#");
        Assert.Equal(-1, Sprite.Read(file, iff).PaletteId);
    }

    [Fact]
    public void DepthSpriteDecodesColourDepthAndAlphaRuns()
    {
        // Frame at 16: header, then row 0 with a (depth, colour) pair and a (depth, colour, alpha) triple padded to an
        // even offset; row 1 skips a pixel and draws one colour byte, padded again.
        var chunk = new ChunkBuilder().U32(1000).U32(1).S32(-1).U32(16)
            .U16(2).U16(2).U32(7).U16(0).U16(255).S16(3).S16(-4)
            .U16(0x0000).U16(0x2001).U8(10).U8(20).U16(0x4001).U8(11).U8(21).U8(15).U8(0)
            .U16(0x0000).U16(0x6001).U16(0xC001).U8(30).U8(0).U16(0xA000);
        var (file, iff) = chunk.InFile("SPR2");
        var sprite = DepthSprite.Read(file, iff);
        Assert.Equal((-1, true), (sprite.PaletteId, sprite.ReadBySimsExe));
        var frame = sprite.Frames.Single();
        Assert.Equal((7u, (short)-4, (short)3), (frame.Flags, frame.X, frame.Y));
        Assert.Equal([20, 21, 0, 30], frame.Indices);
        Assert.Equal([10, 11, 0, 0], frame.Depth);
        Assert.Equal([31, 15, 0, 31], frame.Alpha);
    }

    [Fact]
    public void DepthSpriteSkipRowsMovesTheNextRow()
    {
        var chunk = new ChunkBuilder().U32(1000).U32(1).S32(2).U32(16)
            .U16(1).U16(3).U32(1).U16(0).U16(0).S16(0).S16(0)
            .U16(0x8002).U16(0x0000).U16(0xC001).U8(5).U8(0).U16(0xA000);
        var (file, iff) = chunk.InFile("SPR2");
        var frame = DepthSprite.Read(file, iff).Frames.Single();
        Assert.Equal([0, 0, 31], frame.Alpha);
        Assert.Equal((byte)5, frame.Indices[2]);
    }

    [Fact]
    public void OldDrawGroupKeepsOnlyType3Sprites()
    {
        var chunk = new ChunkBuilder().S16(20001).S16(1).S16(2).U8(2).U8(1)
            .S16(-3).S16(100).S16(4).S16(1).S16(-5).S16(6).F32(0.5f)
            .S16(-5).S16(101).S16(0).S16(0).S16(0).S16(0).F32(0f);
        var (file, iff) = chunk.InFile("DGRP");
        var group = DrawGroup.Read(file, iff);
        Assert.Equal(1, group.DroppedSprites);
        var image = group.Images.Single();
        Assert.Equal((2, 1), (image.Direction, image.Zoom));
        Assert.Equal(new DrawGroupSprite(100, 4, -5, 6, 1, 0.5f, 0f, 0f), image.Sprites.Single());
    }

    [Fact]
    public void NewDrawGroupReadsThreeOffsetsFrom20004()
    {
        var chunk = new ChunkBuilder().S16(20004).U32(1).U32(4).U32(3).U32(1)
            .U32(128).U32(5).S32(-9).S32(4).F32(0.25f).U32(5).F32(1.5f).F32(-2f);
        var (file, iff) = chunk.InFile("DGRP");
        var image = DrawGroup.Read(file, iff).Images.Single();
        Assert.Equal((4, 3), (image.Direction, image.Zoom));
        Assert.Equal(new DrawGroupSprite(128, 5, -9, 4, 5, 0.25f, 1.5f, -2f), image.Sprites.Single());
    }
}
