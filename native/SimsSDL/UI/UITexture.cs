namespace SimsSDL.UI;

/// <summary>A CPU-side UI image holding premultiplied sRGB RGBA pixels that are uploaded to the GPU on
/// first draw.</summary>
public sealed class UITexture
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public int Version { get; private set; }

    public UITexture(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[Math.Max(0, width) * Math.Max(0, height) * 4];
    }

    public unsafe void Replace(UIRegion region, int mipmapLevel, void* bytes, int bytesPerRow)
    {
        _ = mipmapLevel;
        if (bytes is null || region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0)
            return;

        if (region.Width == 0 || region.Height == 0)
        {
            Version++;
            return;
        }

        int rowBytes = checked(region.Width * 4);
        fixed (byte* dstBase = Pixels)
        {
            byte* srcBase = (byte*)bytes;
            for (int row = 0; row < region.Height; row++)
            {
                int target = checked(((region.Y + row) * Width + region.X) * 4);
                if (target + rowBytes > Pixels.Length)
                    return;

                Buffer.MemoryCopy(
                    srcBase + (long)row * bytesPerRow,
                    dstBase + target,
                    Pixels.Length - target,
                    rowBytes);
            }
        }
        Version++;
    }

    public unsafe void Replace(UIRegion region, int mipmapLevel, nint bytes, int bytesPerRow) =>
        Replace(region, mipmapLevel, (void*)bytes, bytesPerRow);

    public void Replace(UIRegion region, int mipmapLevel, ReadOnlySpan<byte> bytes, int bytesPerRow)
    {
        _ = mipmapLevel;
        if (region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0)
            return;

        if (region.Width == 0 || region.Height == 0)
        {
            Version++;
            return;
        }

        int rowBytes = checked(region.Width * 4);
        for (int row = 0; row < region.Height; row++)
        {
            int target = checked(((region.Y + row) * Width + region.X) * 4);
            long srcOffset = (long)row * bytesPerRow;
            if (target + rowBytes > Pixels.Length || srcOffset < 0 || srcOffset + rowBytes > bytes.Length)
                return;

            bytes.Slice((int)srcOffset, rowBytes).CopyTo(Pixels.AsSpan(target, rowBytes));
        }
        Version++;
    }

    public void Replace(UIRegion region, int mipmapLevel, byte[] bytes, int bytesPerRow)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Replace(region, mipmapLevel, (ReadOnlySpan<byte>)bytes, bytesPerRow);
    }
}

public struct UIRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public UIRegion(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static UIRegion Make2D(int x, int y, int width, int height) => new(x, y, width, height);
}

public sealed class UITextureDescriptor
{
    public enum PixelFormat
    {
        Rgba8UnormSrgb
    }

    public enum StorageModeKind
    {
        Shared
    }

    [Flags]
    public enum UsageKind
    {
        None = 0,
        ShaderRead = 1
    }

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public StorageModeKind StorageMode { get; set; } = StorageModeKind.Shared;
    public UsageKind Usage { get; set; } = UsageKind.ShaderRead;

    private UITextureDescriptor(PixelFormat format, int width, int height)
    {
        Format = format;
        Width = width;
        Height = height;
    }

    public static UITextureDescriptor Texture2DDescriptor(
        PixelFormat pixelFormat,
        int width,
        int height,
        bool mipmapped)
    {
        _ = mipmapped;
        return new UITextureDescriptor(pixelFormat, width, height);
    }
}

public readonly struct UIDevice
{
    public UITexture? MakeTexture(UITextureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Width <= 0 || descriptor.Height <= 0)
            return null;
        return new UITexture(descriptor.Width, descriptor.Height);
    }
}
