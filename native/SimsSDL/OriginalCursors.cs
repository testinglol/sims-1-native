namespace SimsSDL;

public sealed class OriginalCursors
{
    private readonly GameContent _content;
    private readonly Dictionary<string, nint> _cache = new(StringComparer.Ordinal);
    private string? _current;

    public static readonly IReadOnlyList<string> Names =
    [
        "Arrow", "HOURGLASS", "edit", "finger", "SimsDelete", "SimsLevel", "SimsLower", "SimsMove", "SimsPlace", "SimsRaise",
        "SimsRotate", "SimsRotateNW", "SimsRotateNE", "SimsRotateSW", "SimsRotateSE", "LEFT", "RIGHT", "UP", "DOWN", "UPRIGHT", "UPLEFT",
        "DOWNRIGHT", "DOWNLEFT", "EXPLORE", "DEADEXPL", "Pan", "LivePerson", "LiveNothing", "LivePet", "LiveNothingPet", "LiveObjectAvail",
        "LiveObjectUnavail", "ADDPTS", "MOVEIN", "BULLDOZE", "SimsFlip", "RemovePts", "taxi", "water", "van", "Rezone2", "trolleybus", "limo",
        "tram", "hole", "balloon",
    ];

    public OriginalCursors(GameContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
    }

    public void Show(string name)
    {
        if (string.Equals(name, _current, StringComparison.Ordinal))
            return;
        _current = name;
        nint cursor = Cursor(name);
        SDL_SetCursor(cursor != nint.Zero ? cursor : SDL_GetDefaultCursor());
    }

    private nint Cursor(string name)
    {
        if (_cache.TryGetValue(name, out nint cached))
            return cached;

        byte[]? data = ReadCursorBytes(name);
        nint cursor = data is not null ? MakeCursor(data) : nint.Zero;
        if (cursor == nint.Zero)
            Console.Error.WriteLine($"SimsSDL: cursor {name}.cur could not be loaded");
        _cache[name] = cursor;
        return cursor;
    }

    private byte[]? ReadCursorBytes(string name)
    {
        try
        {
            return _content.Read($"UIGraphics/Shared/cursors/{name}.cur");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes a Windows cursor containing an ICONDIR structure and a BITMAPINFOHEADER DIB whose
    /// height encompasses both the XOR image and AND mask.</summary>
    public static unsafe nint MakeCursor(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        SDL_Surface* surface = MakeSurface(data, out int hotX, out int hotY);
        if (surface == null)
            return nint.Zero;
        try
        {
            return SDL_CreateColorCursor(surface, hotX, hotY);
        }
        finally
        {
            SDL_DestroySurface(surface);
        }
    }

    public unsafe bool Dump(string name, string path)
    {
        byte[]? data = ReadCursorBytes(name);
        if (data is null)
            return false;
        SDL_Surface* surface = MakeSurface(data, out _, out _);
        if (surface == null)
            return false;
        try
        {
            return SDL_SaveBMP(surface, path);
        }
        finally
        {
            SDL_DestroySurface(surface);
        }
    }

    private static unsafe SDL_Surface* MakeSurface(byte[] bytes, out int hotX, out int hotY)
    {
        hotX = 0;
        hotY = 0;

        int U16(int at) => at >= 0 && at + 2 <= bytes.Length ? bytes[at] | (bytes[at + 1] << 8) : 0;
        int U32(int at) => U16(at) | (U16(at + 2) << 16);

        int iconType = U16(2);
        if (bytes.Length <= 22 || iconType is not (1 or 2) || U16(4) < 1)
            return null;

        // MOVEIN.cur is typed as an icon (1), storing planes instead of a hotspot.
        bool isCursor = iconType == 2;
        hotX = isCursor ? U16(10) : 0;
        hotY = isCursor ? U16(12) : 0;
        int offset = U32(18);
        if (offset < 0 || (long)offset + 40 > bytes.Length)
            return null;

        int header = U32(offset);
        if (header < 40)
            return null;

        int width = U32(offset + 4);
        int height = U32(offset + 8) / 2;
        int bits = U16(offset + 14);
        int colors = U32(offset + 32);
        if (width <= 0 || height <= 0 || width > 256 || height > 256)
            return null;
        if (bits is not (1 or 4 or 8 or 24 or 32) || U32(offset + 16) != 0)
            return null;

        if (bits <= 8 && colors == 0)
            colors = 1 << bits;
        if (colors < 0 || colors > 256)
            return null;

        long paletteStart = (long)offset + header;
        long xorStart = paletteStart + (bits <= 8 ? (long)colors * 4 : 0);
        int xorStride = (width * bits + 31) / 32 * 4;
        long andStart = xorStart + (long)xorStride * height;
        int andStride = (width + 31) / 32 * 4;
        if (paletteStart < 0 || xorStart < paletteStart || andStart < xorStart || andStart + (long)andStride * height > bytes.Length)
            return null;

        byte[] rgba = new byte[width * height * 4];
        bool anyAlpha = false;
        int palStartInt = (int)paletteStart;
        int xorStartInt = (int)xorStart;
        int andStartInt = (int)andStart;

        for (int row = 0; row < height; row++)
        {
            int source = height - 1 - row;
            for (int x = 0; x < width; x++)
            {
                int r;
                int g;
                int b;
                int a = 255;
                switch (bits)
                {
                    case 32:
                    {
                        int p = xorStartInt + source * xorStride + x * 4;
                        b = bytes[p];
                        g = bytes[p + 1];
                        r = bytes[p + 2];
                        a = bytes[p + 3];
                        if (a != 0)
                            anyAlpha = true;
                        break;
                    }
                    case 24:
                    {
                        int p = xorStartInt + source * xorStride + x * 3;
                        b = bytes[p];
                        g = bytes[p + 1];
                        r = bytes[p + 2];
                        break;
                    }
                    default:
                    {
                        int bitOffset = x * bits;
                        int val = bytes[xorStartInt + source * xorStride + bitOffset / 8];
                        int index = (val >> (8 - bits - (bitOffset % 8))) & ((1 << bits) - 1);
                        int p = palStartInt + index * 4;
                        if (p + 2 < bytes.Length)
                        {
                            b = bytes[p];
                            g = bytes[p + 1];
                            r = bytes[p + 2];
                        }
                        else
                        {
                            r = 0;
                            g = 0;
                            b = 0;
                        }
                        break;
                    }
                }

                bool masked = ((bytes[andStartInt + source * andStride + x / 8] >> (7 - (x % 8))) & 1) == 1;
                int o = (row * width + x) * 4;
                rgba[o] = (byte)r;
                rgba[o + 1] = (byte)g;
                rgba[o + 2] = (byte)b;
                rgba[o + 3] = (byte)a;
                if (bits != 32)
                    rgba[o + 3] = masked ? (byte)0 : (byte)255;
                else if (!anyAlpha)
                    rgba[o + 3] = masked ? (byte)0 : (byte)255;
            }
        }

        if (bits == 32 && !anyAlpha)
        {
            for (int row = 0; row < height; row++)
            {
                int source = height - 1 - row;
                for (int x = 0; x < width; x++)
                {
                    bool masked = ((bytes[andStartInt + source * andStride + x / 8] >> (7 - (x % 8))) & 1) == 1;
                    rgba[(row * width + x) * 4 + 3] = masked ? (byte)0 : (byte)255;
                }
            }
        }

        SDL_Surface* surface = SDL_CreateSurface(width, height, SDL_PIXELFORMAT_RGBA32);
        if (surface == null)
            return null;

        int rowBytes = width * 4;
        if (surface->pixels == nint.Zero || surface->pitch < rowBytes)
        {
            SDL_DestroySurface(surface);
            return null;
        }

        fixed (byte* srcBase = rgba)
        {
            byte* dstBase = (byte*)surface->pixels;
            for (int row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(
                    srcBase + row * rowBytes,
                    dstBase + row * surface->pitch,
                    surface->pitch,
                    rowBytes);
            }
        }

        return surface;
    }
}
