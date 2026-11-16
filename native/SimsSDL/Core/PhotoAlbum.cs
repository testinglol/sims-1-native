namespace SimsSDL.Core;

public sealed class PhotoAlbum
{
    public sealed record class Photo
    {
        public string Stem { get; }
        public string Filename { get; }
        public string Caption { get; set; }

        public Photo(string stem, string filename, string caption)
        {
            Stem = stem;
            Filename = filename;
            Caption = caption;
        }
    }

    public sealed class Image(int width, int height, byte[] rgba)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Rgba { get; } = rgba;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private List<Photo> _photos = [];

    public string Directory { get; }
    public IReadOnlyList<Photo> Photos => _photos;

    public PhotoAlbum(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory = NormalizeDirectoryPath(directory);
        Reload();
    }

    public void Reload()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            _photos = [];
            return;
        }

        var files = new List<FileInfo>();
        foreach (string filePath in System.IO.Directory.EnumerateFiles(Directory))
        {
            var info = new FileInfo(filePath);
            if (!info.Exists ||
                (info.Attributes & FileAttributes.Directory) != 0 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.LinkTarget is not null)
            {
                continue;
            }

            if (!string.Equals(info.Extension, ".jpg", StringComparison.OrdinalIgnoreCase))
                continue;

            string stem = Path.GetFileNameWithoutExtension(info.Name);
            if (stem.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase))
                continue;

            files.Add(info);
        }

        files.Sort((a, b) => CompareNatural(a.Name, b.Name));

        var loaded = new List<Photo>(Math.Min(files.Count, 4096));
        int limit = Math.Min(files.Count, 4096);
        for (int i = 0; i < limit; i++)
        {
            FileInfo file = files[i];
            string stem = Path.GetFileNameWithoutExtension(file.Name);
            string captionPath = ResolveSafePath(stem + ".txt");
            string caption = ReadCaption(captionPath);
            loaded.Add(new Photo(stem, file.Name, caption));
        }

        _photos = loaded;
    }

    public Image ImageAt(int index)
    {
        if ((uint)index >= (uint)_photos.Count)
            throw new ContentError("Photo is no longer in the album");

        string filePath = ResolveSafePath(_photos[index].Filename);
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length > 32L * 1024 * 1024)
            throw new ContentError("Photo is too large");

        byte[] data = File.ReadAllBytes(filePath);
        StbImageSharp.ImageResult result;
        try
        {
            result = StbImageSharp.ImageResult.FromMemory(data, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception ex)
        {
            throw new ContentError($"Invalid album JPEG: {ex.Message}");
        }

        if (result is null ||
            result.Data is null ||
            result.Width <= 0 ||
            result.Height <= 0 ||
            result.Width > 8192 ||
            result.Height > 8192 ||
            (long)result.Width * result.Height > 16_000_000 ||
            result.Data.Length != result.Width * result.Height * 4)
        {
            throw new ContentError("Invalid album JPEG");
        }

        return new Image(result.Width, result.Height, result.Data);
    }

    public Photo Capture(byte[] rgba, int width, int height, float quality, string caption)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(caption);

        if (width <= 0 ||
            height <= 0 ||
            width > 8192 ||
            height > 8192 ||
            (long)width * height > 16_000_000 ||
            (long)width * height * 4 != rgba.Length ||
            !float.IsFinite(quality) ||
            quality is < 0f or > 1f ||
            Encoding.UTF8.GetByteCount(caption) > 65536)
        {
            throw new ContentError("Invalid photo capture");
        }

        int jpegQuality = Math.Clamp((int)MathF.Round(quality * 100.0f), 1, 100);
        byte[] photoBytes = EncodeJpeg(rgba, width, height, jpegQuality);

        var (thumbW, thumbH, thumbRgba) = CreateThumbnailRgba(rgba, width, height, 128);
        byte[] thumbBytes = EncodeJpeg(thumbRgba, thumbW, thumbH, jpegQuality);

        System.IO.Directory.CreateDirectory(Directory);

        int maxId = 0;
        foreach (Photo existing in _photos)
        {
            int parsed = ParseTrailingNumber(existing.Stem);
            if (parsed > maxId)
                maxId = parsed;
        }

        int identifier = maxId + 1;
        while (File.Exists(Path.Combine(Directory, $"Snapshot{identifier}.jpg")))
            identifier++;

        string stem = $"Snapshot{identifier}";
        string thumbPath = ResolveSafePath(stem + "_thumb.jpg");
        string captionPath = ResolveSafePath(stem + ".txt");
        string photoPath = ResolveSafePath(stem + ".jpg");
        string[] created = [thumbPath, captionPath, photoPath];

        try
        {
            WriteAtomic(thumbPath, thumbBytes);
            WriteAtomic(captionPath, Encoding.UTF8.GetBytes(caption));
            WriteAtomic(photoPath, photoBytes);
        }
        catch
        {
            foreach (string path in created)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
            throw;
        }

        Reload();
        return new Photo(stem, stem + ".jpg", caption);
    }

    public void SetCaption(string caption, int index)
    {
        ArgumentNullException.ThrowIfNull(caption);
        if ((uint)index >= (uint)_photos.Count || Encoding.UTF8.GetByteCount(caption) > 65536)
            return;

        string captionPath = ResolveSafePath(_photos[index].Stem + ".txt");
        WriteAtomic(captionPath, Encoding.UTF8.GetBytes(caption));
        _photos[index].Caption = caption;
    }

    public void Delete(int index)
    {
        if ((uint)index >= (uint)_photos.Count)
            throw new ContentError("Photo is no longer in the album");

        string stem = _photos[index].Stem;
        string[] files =
        [
            ResolveSafePath(_photos[index].Filename),
            ResolveSafePath(stem + "_thumb.jpg"),
            ResolveSafePath(stem + ".txt"),
        ];

        foreach (string path in files)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        Reload();
    }

    private string ResolveSafePath(string name)
    {
        string combined = Path.GetFullPath(Path.Combine(Directory, name));
        if (File.Exists(combined))
        {
            var info = new FileInfo(combined);
            if (info.LinkTarget is not null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    combined = Path.GetFullPath(target.FullName);
            }
        }

        string? parent = Path.GetDirectoryName(combined);
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, Directory, cmp))
            throw new ContentError("Photo escapes album directory");

        return combined;
    }

    private static string NormalizeDirectoryPath(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (System.IO.Directory.Exists(full))
        {
            var info = new DirectoryInfo(full);
            if (info.LinkTarget is not null)
            {
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    full = Path.GetFullPath(target.FullName);
            }
        }

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
    }

    private static string ReadCaption(string captionPath)
    {
        try
        {
            var info = new FileInfo(captionPath);
            if (!info.Exists || info.Length > 65536)
                return string.Empty;

            byte[] data = File.ReadAllBytes(captionPath);
            try
            {
                return StrictUtf8.GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(data);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[] EncodeJpeg(byte[] rgba, int width, int height, int quality)
    {
        using var stream = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WriteJpg(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream, quality);
        byte[] bytes = stream.ToArray();
        if (bytes.Length == 0)
            throw new ContentError("Cannot encode JPEG");
        return bytes;
    }

    private static (int Width, int Height, byte[] Rgba) CreateThumbnailRgba(byte[] rgba, int width, int height, int maxSize)
    {
        if (width <= maxSize && height <= maxSize)
            return (width, height, rgba);

        double scale = maxSize / (double)Math.Max(width, height);
        int thumbW = Math.Max(1, (int)Math.Round(width * scale));
        int thumbH = Math.Max(1, (int)Math.Round(height * scale));
        byte[] thumb = new byte[thumbW * thumbH * 4];

        for (int ty = 0; ty < thumbH; ty++)
        {
            double sy = ((ty + 0.5) * height / thumbH) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, height - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            double fy = Math.Clamp(sy - y0, 0.0, 1.0);

            for (int tx = 0; tx < thumbW; tx++)
            {
                double sx = ((tx + 0.5) * width / thumbW) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, width - 1);
                int x1 = Math.Min(x0 + 1, width - 1);
                double fx = Math.Clamp(sx - x0, 0.0, 1.0);

                int i00 = (y0 * width + x0) * 4;
                int i10 = (y0 * width + x1) * 4;
                int i01 = (y1 * width + x0) * 4;
                int i11 = (y1 * width + x1) * 4;
                int dst = (ty * thumbW + tx) * 4;

                for (int c = 0; c < 3; c++)
                {
                    double top = rgba[i00 + c] * (1.0 - fx) + rgba[i10 + c] * fx;
                    double bot = rgba[i01 + c] * (1.0 - fx) + rgba[i11 + c] * fx;
                    thumb[dst + c] = (byte)Math.Clamp((int)Math.Round(top * (1.0 - fy) + bot * fy), 0, 255);
                }
                thumb[dst + 3] = 255;
            }
        }

        return (thumbW, thumbH, thumb);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        string tempPath = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
            throw;
        }
    }

    private static int ParseTrailingNumber(string stem)
    {
        int end = stem.Length;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(stem[start - 1]))
            start--;

        return start < end && int.TryParse(stem.AsSpan(start, end - start), out int value) ? value : 0;
    }

    private static int CompareNatural(string left, string right)
    {
        int i = 0;
        int j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                int iStart = i;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                int jStart = j;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;

                ReadOnlySpan<char> leftDigits = left.AsSpan(iStart, i - iStart).TrimStart('0');
                ReadOnlySpan<char> rightDigits = right.AsSpan(jStart, j - jStart).TrimStart('0');
                if (leftDigits.Length != rightDigits.Length)
                    return leftDigits.Length.CompareTo(rightDigits.Length);

                int digitCmp = leftDigits.SequenceCompareTo(rightDigits);
                if (digitCmp != 0)
                    return digitCmp;
                continue;
            }

            int cmp = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
            if (cmp != 0)
                return cmp;
            i++;
            j++;
        }

        return left.Length.CompareTo(right.Length);
    }
}
