using System.Buffers.Binary;
using SimsEngine.Content;
using SimsSDL.Core;

namespace SimsEngine.Tests;

public class CatalogThumbnailAntialiasingTests
{
    private static byte[] Make32BitBmp(int width, int height, (byte R, byte G, byte B, byte A)[] pixels)
    {
        var data = new byte[54 + width * height * 4];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(34), width * height * 4);
        for (int i = 0; i < pixels.Length; i++)
        {
            int off = 54 + i * 4;
            data[off] = pixels[i].B;
            data[off + 1] = pixels[i].G;
            data[off + 2] = pixels[i].R;
            data[off + 3] = pixels[i].A;
        }
        return data;
    }

    [Fact]
    public void SimsBitmap_32BitRgba_PreservesSemiTransparentAlphaAndSetsHasAlpha()
    {
        var bmpBytes = Make32BitBmp(2, 2, [
            (0, 0, 0, 0),
            (200, 100, 50, 128),
            (255, 0, 255, 255),
            (40, 80, 120, 255),
        ]);

        var bitmap = new SimsBitmap(bmpBytes);
        Assert.True(bitmap.HasAlpha);
        Assert.Equal(0, bitmap.Rgba[3]);
        Assert.Equal(128, bitmap.Rgba[7]);
        Assert.Equal(255, bitmap.Rgba[11]);
        Assert.Equal(255, bitmap.Rgba[15]);
    }

    [Fact]
    public void SimsBitmap_32BitLegacyZeroOrOpaqueAlpha_KeepsHasAlphaFalse()
    {
        var legacyBgrx = new SimsBitmap(Make32BitBmp(2, 1, [
            (10, 20, 30, 0),
            (40, 50, 60, 0),
        ]));
        Assert.False(legacyBgrx.HasAlpha);
        Assert.Equal(255, legacyBgrx.Rgba[3]);
        Assert.Equal(255, legacyBgrx.Rgba[7]);

        var colorKeyed = new SimsBitmap(Make32BitBmp(2, 1, [
            (255, 0, 255, 255),
            (10, 20, 30, 255),
        ]));
        Assert.False(colorKeyed.HasAlpha);
        Assert.Equal(255, colorKeyed.Rgba[3]);
        Assert.Equal(255, colorKeyed.Rgba[7]);
    }

    [Fact]
    public void ObjectCatalog_RenderedPreview_ProducesAntialiasedAlphaWithTransparentBorder()
    {
        var gameDir = TestGameLocator.Locate();
        if (gameDir is null) return;

        var registry = ObjectRegistry.Scan(gameDir);

        byte[]? previewBmp = null;
        foreach (var entry in registry.Entries)
        {
            if (entry.Definition[SimsEngine.Formats.ObjdField.FunctionSort] == 0) continue;
            var candidate = SimsSimulation.ObjectCatalog.RenderedIcon(entry);
            if (candidate is null) continue;
            var decoded = new SimsBitmap(candidate);
            if (!decoded.HasAlpha) continue;
            bool hasSemiTransparent = false;
            for (int i = 3; i < decoded.Rgba.Length; i += 4)
            {
                if (decoded.Rgba[i] is > 0 and < 255)
                {
                    hasSemiTransparent = true;
                    break;
                }
            }
            if (hasSemiTransparent)
            {
                previewBmp = candidate;
                break;
            }
        }

        Assert.NotNull(previewBmp);
        var bmp = new SimsBitmap(previewBmp!);
        Assert.True(bmp.HasAlpha);
        // 1px transparent border around the rendered object prevents CLAMP_TO_EDGE clipping.
        for (int x = 0; x < bmp.Width; x++)
        {
            Assert.Equal(0, bmp.Rgba[x * 4 + 3]);
            Assert.Equal(0, bmp.Rgba[((bmp.Height - 1) * bmp.Width + x) * 4 + 3]);
        }
        for (int y = 0; y < bmp.Height; y++)
        {
            Assert.Equal(0, bmp.Rgba[y * bmp.Width * 4 + 3]);
            Assert.Equal(0, bmp.Rgba[(y * bmp.Width + bmp.Width - 1) * 4 + 3]);
        }
    }
}
