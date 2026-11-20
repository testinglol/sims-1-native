using System.Buffers.Binary;

namespace SimsSDL.Core;

/// <summary>Minimal pure-C# TrueType/OpenType outline parser and anti-aliased scanline rasterizer used
/// to render high-DPI UI typography without native font dependencies.</summary>
public sealed class TrueTypeFont
{
    private readonly record struct ContourPoint(float X, float Y, bool OnCurve);
    private readonly record struct Contour(ContourPoint[] Points);
    private readonly record struct Edge(float X0, float Y0, float X1, float Y1);
    private readonly record struct GlyphOutline(Contour[] Contours, short XMin, short YMin, short XMax, short YMax);

    private const ushort PlatformUnicode = 0;
    private const ushort PlatformWindows = 3;
    private const ushort WindowsEncBmp = 1;
    private const ushort WindowsEncFull = 10;

    private static readonly Lazy<TrueTypeFont?> CachedRegular = new(() => LoadSystemFont(bold: false));
    private static readonly Lazy<TrueTypeFont?> CachedBold = new(() => LoadSystemFont(bold: true));

    public static TrueTypeFont? DefaultRegular => CachedRegular.Value;
    public static TrueTypeFont? DefaultBold => CachedBold.Value ?? CachedRegular.Value;

    private readonly byte[] _data;
    private readonly int _hmtxOffset;
    private readonly int _locaOffset;
    private readonly int _glyfOffset;
    private readonly int _kernSubtableOffset;
    private readonly ushort _kernPairCount;
    private readonly int _cmapSubtableOffset;
    private readonly ushort _cmapFormat;
    private readonly short _indexToLocFormat;
    private readonly ushort _numGlyphs;
    private readonly ushort _numberOfHMetrics;
    private readonly Dictionary<ushort, GlyphOutline> _outlineCache = new();
    private readonly Lock _cacheLock = new();

    public ushort UnitsPerEm { get; }
    public float CapHeightUnits { get; }
    public float CapInkSpanUnits { get; }
    public float CapBottomUnits { get; }
    public float StemWidthUnits { get; }
    public short AscentUnits { get; }
    public short DescentUnits { get; }

    public TrueTypeFont(byte[] data, int fontIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        int sfntOffset = ResolveSfntOffset(data, fontIndex);
        if (sfntOffset < 0 || sfntOffset + 12 > data.Length)
            throw new ContentError("Unsupported TrueType header");

        ushort numTables = U16(sfntOffset + 4);
        int head = -1, hhea = -1, maxp = -1, hmtx = -1, loca = -1, glyf = -1, cmap = -1, os2 = -1, kern = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = sfntOffset + 12 + i * 16;
            if (rec + 16 > data.Length)
                throw new ContentError("Truncated TrueType table directory");

            uint tag = U32(rec);
            int offset = (int)U32(rec + 8);
            if (offset < 0 || offset >= data.Length)
                continue;

            switch (tag)
            {
                case 0x68656164: head = offset; break; // head
                case 0x68686561: hhea = offset; break; // hhea
                case 0x6D617870: maxp = offset; break; // maxp
                case 0x686D7478: hmtx = offset; break; // hmtx
                case 0x6C6F6361: loca = offset; break; // loca
                case 0x676C7966: glyf = offset; break; // glyf
                case 0x636D6170: cmap = offset; break; // cmap
                case 0x4F532F32: os2 = offset; break;  // OS/2
                case 0x6B65726E: kern = offset; break; // kern
            }
        }

        if (head < 0 || hhea < 0 || maxp < 0 || hmtx < 0 || loca < 0 || glyf < 0 || cmap < 0)
            throw new ContentError("TrueType font lacks required outline tables");

        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm == 0)
            throw new ContentError("TrueType unitsPerEm cannot be zero");
        _indexToLocFormat = S16(head + 50);

        AscentUnits = S16(hhea + 4);
        DescentUnits = S16(hhea + 6);
        _numberOfHMetrics = U16(hhea + 34);
        _numGlyphs = U16(maxp + 4);
        if (_numberOfHMetrics == 0 || _numGlyphs == 0)
            throw new ContentError("TrueType font has no glyphs");

        _hmtxOffset = hmtx;
        _locaOffset = loca;
        _glyfOffset = glyf;

        (_cmapSubtableOffset, _cmapFormat) = SelectCmapSubtable(cmap);
        if (_cmapSubtableOffset < 0)
            throw new ContentError("TrueType font has no supported Unicode cmap subtable");

        (_kernSubtableOffset, _kernPairCount) = SelectKernFormat0(kern);
        CapHeightUnits = MeasureCapHeight(os2);
        (CapInkSpanUnits, CapBottomUnits) = MeasureCapMetrics();
        StemWidthUnits = MeasureStemWidth();
    }

    private (int PairsOffset, ushort PairCount) SelectKernFormat0(int kernOffset)
    {
        if (kernOffset < 0 || kernOffset + 4 > _data.Length)
            return (-1, 0);
        ushort version = U16(kernOffset);
        if (version != 0)
            return (-1, 0);
        ushort nTables = U16(kernOffset + 2);
        int sub = kernOffset + 4;
        for (int i = 0; i < nTables; i++)
        {
            if (sub + 14 > _data.Length)
                break;
            ushort subLen = U16(sub + 2);
            ushort coverage = U16(sub + 4);
            int format = coverage >> 8;
            bool horizontal = (coverage & 1) != 0 && (coverage & 2) == 0 && (coverage & 4) == 0;
            if (format == 0 && horizontal)
            {
                ushort nPairs = U16(sub + 6);
                int pairsOff = sub + 14;
                if (pairsOff + nPairs * 6 <= _data.Length)
                    return (pairsOff, nPairs);
            }
            if (subLen < 6)
                break;
            sub += subLen;
        }
        return (-1, 0);
    }

    public float GetKerningUnits(uint leftCodePoint, uint rightCodePoint)
    {
        if (_kernSubtableOffset < 0 || _kernPairCount == 0)
            return 0f;
        ushort gLeft = GlyphIndex(leftCodePoint);
        ushort gRight = GlyphIndex(rightCodePoint);
        if (gLeft == 0 || gRight == 0)
            return 0f;
        uint key = ((uint)gLeft << 16) | gRight;
        int lo = 0, hi = _kernPairCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int off = _kernSubtableOffset + mid * 6;
            uint midKey = U32(off);
            if (midKey == key)
                return S16(off + 4);
            if (midKey < key)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return 0f;
    }

    public bool HasGlyph(uint codePoint) => GlyphIndex(codePoint) != 0;

    public (float LeftUnits, float RightUnits)? HorizontalInkBounds(uint codePoint)
    {
        ushort glyphId = GlyphIndex(codePoint);
        if (glyphId == 0)
            return null;

        var outline = LoadOutline(glyphId, depth: 0);
        return outline.Contours.Length > 0 ? (outline.XMin, outline.XMax) : null;
    }

    public float AdvanceWidthUnits(uint codePoint)
    {
        ushort glyphId = GlyphIndex(codePoint);
        return AdvanceWidthForGlyph(glyphId);
    }

    public float RenderGlyph(
        uint codePoint,
        float originX,
        float baselineY,
        float pxPerUnit,
        (byte R, byte G, byte B) color,
        byte[] rgba,
        int dstWidth,
        int dstHeight,
        float erodePx = 0f)
    {
        ushort glyphId = GlyphIndex(codePoint);
        float advanceUnits = AdvanceWidthForGlyph(glyphId);
        if (glyphId == 0 && codePoint == 32)
            return advanceUnits * pxPerUnit;

        var outline = LoadOutline(glyphId, depth: 0);
        if (outline.Contours.Length > 0)
        {
            var edges = FlattenContours(outline.Contours, originX, baselineY, pxPerUnit);
            if (edges.Count > 0)
                RasterizeEdges(edges, color, rgba, dstWidth, dstHeight, erodePx);
        }

        return advanceUnits * pxPerUnit;
    }

    private (float SpanUnits, float BottomUnits) MeasureCapMetrics()
    {
        float spanSum = 0f;
        float bottomSum = 0f;
        int count = 0;
        foreach (uint ch in new uint[] { 'H', 'E', 'L', 'M' })
        {
            ushort gid = GlyphIndex(ch);
            if (gid == 0)
                continue;

            var outline = LoadOutline(gid, depth: 0);
            if (outline.Contours.Length == 0 || outline.YMax <= outline.YMin)
                continue;

            spanSum += outline.YMax - outline.YMin;
            bottomSum += outline.YMin;
            count++;
        }

        return count > 0
            ? (spanSum / count, bottomSum / count)
            : (CapHeightUnits, 0f);
    }

    private float MeasureStemWidth()
    {
        ushort gid = GlyphIndex('L');
        if (gid != 0)
        {
            var outline = LoadOutline(gid, depth: 0);
            if (outline.Contours.Length > 0 && outline.YMax > outline.YMin)
            {
                float yMid = 0.5f * (outline.YMin + outline.YMax);
                var edges = FlattenContours(outline.Contours, originX: 0f, baselineY: 0f, scale: 1f);
                var hits = new List<(float X, int Wind)>(16);
                float ySub = -yMid;
                for (int i = 0; i < edges.Count; i++)
                {
                    Edge e = edges[i];
                    bool up = e.Y0 <= ySub && e.Y1 > ySub;
                    bool down = e.Y1 <= ySub && e.Y0 > ySub;
                    if (!up && !down)
                        continue;

                    float t = (ySub - e.Y0) / (e.Y1 - e.Y0);
                    hits.Add((e.X0 + (e.X1 - e.X0) * t, up ? 1 : -1));
                }

                if (hits.Count >= 2)
                {
                    hits.Sort(HitComparer);
                    int wind = 0;
                    float enterX = 0f;
                    foreach (var hit in hits)
                    {
                        if (wind == 0)
                            enterX = hit.X;
                        wind += hit.Wind;
                        if (wind == 0)
                        {
                            float width = hit.X - enterX;
                            if (width > 1f)
                                return width;
                        }
                    }
                }
            }
        }

        return UnitsPerEm * 0.11f;
    }

    private float MeasureCapHeight(int os2Offset)
    {
        foreach (uint ch in new uint[] { 'H', 'M', 'A' })
        {
            ushort gid = GlyphIndex(ch);
            if (gid != 0)
            {
                var outline = LoadOutline(gid, depth: 0);
                if (outline.YMax > 0)
                    return outline.YMax;
            }
        }

        if (os2Offset >= 0 && os2Offset + 90 <= _data.Length)
        {
            ushort version = U16(os2Offset);
            if (version >= 2)
            {
                short capHeight = S16(os2Offset + 88);
                if (capHeight > 0)
                    return capHeight;
            }
        }

        return AscentUnits > 0 ? AscentUnits * 0.72f : UnitsPerEm * 0.72f;
    }

    private static int ResolveSfntOffset(byte[] data, int fontIndex)
    {
        if (data.Length < 12)
            return -1;

        uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        if (tag == 0x74746366) // ttcf
        {
            if (data.Length < 16)
                return -1;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
            if ((uint)fontIndex >= count || 12 + (fontIndex + 1) * 4 > data.Length)
                return -1;
            return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + fontIndex * 4, 4));
        }

        return tag is 0x00010000 or 0x74727565 ? 0 : -1;
    }

    private (int Offset, ushort Format) SelectCmapSubtable(int cmapOffset)
    {
        if (cmapOffset + 4 > _data.Length)
            return (-1, 0);

        ushort count = U16(cmapOffset + 2);
        int bestOffset = -1;
        ushort bestFormat = 0;
        int bestPriority = -1;

        for (int i = 0; i < count; i++)
        {
            int rec = cmapOffset + 4 + i * 8;
            if (rec + 8 > _data.Length)
                break;

            ushort platform = U16(rec);
            ushort encoding = U16(rec + 2);
            int subOffset = cmapOffset + (int)U32(rec + 4);
            if (subOffset < 0 || subOffset + 4 > _data.Length)
                continue;

            ushort format = U16(subOffset);
            if (format is not (4 or 12))
                continue;

            int priority = (platform, encoding, format) switch
            {
                (PlatformWindows, WindowsEncFull, 12) => 5,
                (PlatformUnicode, 4, 12) => 4,
                (PlatformWindows, WindowsEncBmp, 4) => 3,
                (PlatformUnicode, _, 12) => 2,
                (PlatformUnicode, _, 4) => 1,
                _ => -1,
            };

            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestOffset = subOffset;
                bestFormat = format;
            }
        }

        return (bestOffset, bestFormat);
    }

    private ushort GlyphIndex(uint codePoint)
    {
        if (_cmapFormat == 12)
        {
            if (_cmapSubtableOffset + 16 > _data.Length)
                return 0;

            uint numGroups = U32(_cmapSubtableOffset + 12);
            int low = 0;
            int high = (int)numGroups - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                int group = _cmapSubtableOffset + 16 + mid * 12;
                if (group + 12 > _data.Length)
                    return 0;

                uint start = U32(group);
                uint end = U32(group + 4);
                if (codePoint < start)
                    high = mid - 1;
                else if (codePoint > end)
                    low = mid + 1;
                else
                {
                    uint startGlyph = U32(group + 8);
                    uint gid = startGlyph + (codePoint - start);
                    return gid < _numGlyphs ? (ushort)gid : (ushort)0;
                }
            }

            return 0;
        }

        if (_cmapFormat == 4 && codePoint <= 0xFFFF)
        {
            if (_cmapSubtableOffset + 14 > _data.Length)
                return 0;

            int segCount = U16(_cmapSubtableOffset + 6) / 2;
            int endCountBase = _cmapSubtableOffset + 14;
            int startCountBase = endCountBase + segCount * 2 + 2;
            int idDeltaBase = startCountBase + segCount * 2;
            int idRangeOffsetBase = idDeltaBase + segCount * 2;
            if (idRangeOffsetBase + segCount * 2 > _data.Length)
                return 0;

            ushort c = (ushort)codePoint;
            int low = 0;
            int high = segCount - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                ushort end = U16(endCountBase + mid * 2);
                if (c > end)
                {
                    low = mid + 1;
                    continue;
                }

                ushort start = U16(startCountBase + mid * 2);
                if (c < start)
                {
                    high = mid - 1;
                    continue;
                }

                short idDelta = S16(idDeltaBase + mid * 2);
                int rangeEntry = idRangeOffsetBase + mid * 2;
                ushort idRangeOffset = U16(rangeEntry);
                ushort glyphId;
                if (idRangeOffset == 0)
                {
                    glyphId = unchecked((ushort)(c + idDelta));
                }
                else
                {
                    int target = rangeEntry + idRangeOffset + (c - start) * 2;
                    if (target + 2 > _data.Length)
                        return 0;
                    ushort mapped = U16(target);
                    glyphId = mapped == 0 ? (ushort)0 : unchecked((ushort)(mapped + idDelta));
                }

                return glyphId < _numGlyphs ? glyphId : (ushort)0;
            }
        }

        return 0;
    }

    private float AdvanceWidthForGlyph(ushort glyphId)
    {
        int index = Math.Min((int)glyphId, _numberOfHMetrics - 1);
        int offset = _hmtxOffset + index * 4;
        return offset + 2 <= _data.Length ? U16(offset) : UnitsPerEm * 0.5f;
    }

    private (int Start, int End) GlyphByteRange(ushort glyphId)
    {
        if (glyphId >= _numGlyphs)
            return (0, 0);

        if (_indexToLocFormat == 0)
        {
            int pos = _locaOffset + glyphId * 2;
            if (pos + 4 > _data.Length)
                return (0, 0);
            return (U16(pos) * 2, U16(pos + 2) * 2);
        }

        int pos32 = _locaOffset + glyphId * 4;
        if (pos32 + 8 > _data.Length)
            return (0, 0);
        return ((int)U32(pos32), (int)U32(pos32 + 4));
    }

    private GlyphOutline LoadOutline(ushort glyphId, int depth)
    {
        if (depth > 8)
            return new GlyphOutline([], 0, 0, 0, 0);

        lock (_cacheLock)
        {
            if (_outlineCache.TryGetValue(glyphId, out GlyphOutline cached))
                return cached;
        }

        var (startRel, endRel) = GlyphByteRange(glyphId);
        if (endRel <= startRel)
        {
            var empty = new GlyphOutline([], 0, 0, 0, 0);
            lock (_cacheLock) _outlineCache[glyphId] = empty;
            return empty;
        }

        int off = _glyfOffset + startRel;
        int end = _glyfOffset + endRel;
        if (off < 0 || end > _data.Length || off + 10 > end)
            return new GlyphOutline([], 0, 0, 0, 0);

        short numContours = S16(off);
        short xMin = S16(off + 2);
        short yMin = S16(off + 4);
        short xMax = S16(off + 6);
        short yMax = S16(off + 8);

        GlyphOutline decoded;
        if (numContours >= 0)
            decoded = DecodeSimpleGlyph(off + 10, end, numContours, xMin, yMin, xMax, yMax);
        else
            decoded = DecodeCompositeGlyph(off + 10, end, depth, xMin, yMin, xMax, yMax);

        lock (_cacheLock) _outlineCache[glyphId] = decoded;
        return decoded;
    }

    private GlyphOutline DecodeSimpleGlyph(int cursor, int end, int numContours, short xMin, short yMin, short xMax, short yMax)
    {
        if (numContours == 0 || cursor + numContours * 2 + 2 > end)
            return new GlyphOutline([], xMin, yMin, xMax, yMax);

        var endPts = new int[numContours];
        for (int i = 0; i < numContours; i++)
        {
            endPts[i] = U16(cursor + i * 2);
            if (i > 0 && endPts[i] <= endPts[i - 1])
                return new GlyphOutline([], xMin, yMin, xMax, yMax);
        }

        cursor += numContours * 2;
        ushort insLen = U16(cursor);
        cursor += 2 + insLen;
        if (cursor > end)
            return new GlyphOutline([], xMin, yMin, xMax, yMax);

        int pointCount = endPts[^1] + 1;
        var flags = new byte[pointCount];
        int fi = 0;
        while (fi < pointCount && cursor < end)
        {
            byte flag = _data[cursor++];
            flags[fi++] = flag;
            if ((flag & 0x08) != 0)
            {
                if (cursor >= end)
                    return new GlyphOutline([], xMin, yMin, xMax, yMax);
                byte repeat = _data[cursor++];
                for (int r = 0; r < repeat && fi < pointCount; r++)
                    flags[fi++] = flag;
            }
        }

        if (fi != pointCount)
            return new GlyphOutline([], xMin, yMin, xMax, yMax);

        var xs = new short[pointCount];
        var ys = new short[pointCount];

        int xVal = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x02) != 0)
            {
                if (cursor >= end) return new GlyphOutline([], xMin, yMin, xMax, yMax);
                int dx = _data[cursor++];
                xVal += (flag & 0x10) != 0 ? dx : -dx;
            }
            else if ((flag & 0x10) == 0)
            {
                if (cursor + 2 > end) return new GlyphOutline([], xMin, yMin, xMax, yMax);
                xVal += S16(cursor);
                cursor += 2;
            }
            xs[i] = (short)xVal;
        }

        int yVal = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte flag = flags[i];
            if ((flag & 0x04) != 0)
            {
                if (cursor >= end) return new GlyphOutline([], xMin, yMin, xMax, yMax);
                int dy = _data[cursor++];
                yVal += (flag & 0x20) != 0 ? dy : -dy;
            }
            else if ((flag & 0x20) == 0)
            {
                if (cursor + 2 > end) return new GlyphOutline([], xMin, yMin, xMax, yMax);
                yVal += S16(cursor);
                cursor += 2;
            }
            ys[i] = (short)yVal;
        }

        var contours = new List<Contour>(numContours);
        int start = 0;
        for (int c = 0; c < numContours; c++)
        {
            int count = endPts[c] - start + 1;
            if (count >= 2)
            {
                var pts = new ContourPoint[count];
                for (int k = 0; k < count; k++)
                {
                    int idx = start + k;
                    pts[k] = new ContourPoint(xs[idx], ys[idx], (flags[idx] & 0x01) != 0);
                }
                contours.Add(new Contour(pts));
            }
            start = endPts[c] + 1;
        }

        return new GlyphOutline([.. contours], xMin, yMin, xMax, yMax);
    }

    private GlyphOutline DecodeCompositeGlyph(int cursor, int end, int depth, short xMin, short yMin, short xMax, short yMax)
    {
        const ushort Arg1And2AreWords = 0x0001;
        const ushort ArgsAreXyValues = 0x0002;
        const ushort WeHaveAScale = 0x0008;
        const ushort MoreComponents = 0x0020;
        const ushort WeHaveAnXAndYScale = 0x0040;
        const ushort WeHaveATwoByTwo = 0x0080;

        var combined = new List<Contour>();
        ushort compFlags;
        do
        {
            if (cursor + 4 > end)
                break;

            compFlags = U16(cursor);
            ushort childId = U16(cursor + 2);
            cursor += 4;

            float dx = 0f;
            float dy = 0f;
            if ((compFlags & Arg1And2AreWords) != 0)
            {
                if (cursor + 4 > end) break;
                if ((compFlags & ArgsAreXyValues) != 0)
                {
                    dx = S16(cursor);
                    dy = S16(cursor + 2);
                }
                cursor += 4;
            }
            else
            {
                if (cursor + 2 > end) break;
                if ((compFlags & ArgsAreXyValues) != 0)
                {
                    dx = unchecked((sbyte)_data[cursor]);
                    dy = unchecked((sbyte)_data[cursor + 1]);
                }
                cursor += 2;
            }

            float m00 = 1f, m01 = 0f, m10 = 0f, m11 = 1f;
            if ((compFlags & WeHaveAScale) != 0)
            {
                if (cursor + 2 > end) break;
                m00 = m11 = F2Dot14(cursor);
                cursor += 2;
            }
            else if ((compFlags & WeHaveAnXAndYScale) != 0)
            {
                if (cursor + 4 > end) break;
                m00 = F2Dot14(cursor);
                m11 = F2Dot14(cursor + 2);
                cursor += 4;
            }
            else if ((compFlags & WeHaveATwoByTwo) != 0)
            {
                if (cursor + 8 > end) break;
                m00 = F2Dot14(cursor);
                m01 = F2Dot14(cursor + 2);
                m10 = F2Dot14(cursor + 4);
                m11 = F2Dot14(cursor + 6);
                cursor += 8;
            }

            var child = LoadOutline(childId, depth + 1);
            foreach (var contour in child.Contours)
            {
                var transformed = new ContourPoint[contour.Points.Length];
                for (int i = 0; i < contour.Points.Length; i++)
                {
                    var p = contour.Points[i];
                    float nx = p.X * m00 + p.Y * m10 + dx;
                    float ny = p.X * m01 + p.Y * m11 + dy;
                    transformed[i] = new ContourPoint(nx, ny, p.OnCurve);
                }
                combined.Add(new Contour(transformed));
            }
        }
        while ((compFlags & MoreComponents) != 0);

        return new GlyphOutline([.. combined], xMin, yMin, xMax, yMax);
    }

    private static List<Edge> FlattenContours(Contour[] contours, float originX, float baselineY, float scale)
    {
        var edges = new List<Edge>(64);
        foreach (var contour in contours)
        {
            var src = contour.Points;
            if (src.Length < 2)
                continue;

            var ring = new List<ContourPoint>(src.Length + 1);
            if (src[0].OnCurve)
            {
                ring.AddRange(src);
            }
            else if (src[^1].OnCurve)
            {
                ring.Add(src[^1]);
                for (int i = 0; i < src.Length - 1; i++)
                    ring.Add(src[i]);
            }
            else
            {
                var startMid = new ContourPoint((src[^1].X + src[0].X) * 0.5f, (src[^1].Y + src[0].Y) * 0.5f, true);
                ring.Add(startMid);
                ring.AddRange(src);
            }

            (float X, float Y) ToPixel(ContourPoint p) => (originX + p.X * scale, baselineY - p.Y * scale);
            void EmitLine((float X, float Y) a, (float X, float Y) b)
            {
                if (MathF.Abs(a.Y - b.Y) > 1e-5f)
                    edges.Add(new Edge(a.X, a.Y, b.X, b.Y));
            }

            void EmitQuad((float X, float Y) a, (float X, float Y) c, (float X, float Y) b)
            {
                float mx = 0.5f * (a.X + b.X);
                float my = 0.5f * (a.Y + b.Y);
                float dx = c.X - mx;
                float dy = c.Y - my;
                float err = MathF.Sqrt(dx * dx + dy * dy);
                int steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(err / 0.12f)), 1, 20);
                var prev = a;
                for (int s = 1; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    float omt = 1f - t;
                    float x = omt * omt * a.X + 2f * omt * t * c.X + t * t * b.X;
                    float y = omt * omt * a.Y + 2f * omt * t * c.Y + t * t * b.Y;
                    var next = (x, y);
                    EmitLine(prev, next);
                    prev = next;
                }
            }

            int count = ring.Count;
            var curr = ToPixel(ring[0]);
            for (int i = 1; i <= count; i++)
            {
                var pt = ring[i == count ? 0 : i];
                if (pt.OnCurve)
                {
                    var next = ToPixel(pt);
                    EmitLine(curr, next);
                    curr = next;
                }
                else
                {
                    var ctrl = ToPixel(pt);
                    var peek = ring[i + 1 >= count ? 0 : i + 1];
                    if (peek.OnCurve)
                    {
                        var next = ToPixel(peek);
                        EmitQuad(curr, ctrl, next);
                        curr = next;
                        i++;
                    }
                    else
                    {
                        var nextCtrl = ToPixel(peek);
                        var mid = ((ctrl.X + nextCtrl.X) * 0.5f, (ctrl.Y + nextCtrl.Y) * 0.5f);
                        EmitQuad(curr, ctrl, mid);
                        curr = mid;
                    }
                }
            }
        }

        return edges;
    }

    private static void RasterizeEdges(
        List<Edge> edges,
        (byte R, byte G, byte B) color,
        byte[] rgba,
        int dstWidth,
        int dstHeight,
        float erodePx = 0f)
    {
        float minEdgeY = float.PositiveInfinity;
        float maxEdgeY = float.NegativeInfinity;
        float minEdgeX = float.PositiveInfinity;
        float maxEdgeX = float.NegativeInfinity;
        for (int i = 0; i < edges.Count; i++)
        {
            Edge e = edges[i];
            if (e.Y0 < minEdgeY) minEdgeY = e.Y0;
            if (e.Y1 < minEdgeY) minEdgeY = e.Y1;
            if (e.Y0 > maxEdgeY) maxEdgeY = e.Y0;
            if (e.Y1 > maxEdgeY) maxEdgeY = e.Y1;
            if (e.X0 < minEdgeX) minEdgeX = e.X0;
            if (e.X1 < minEdgeX) minEdgeX = e.X1;
            if (e.X0 > maxEdgeX) maxEdgeX = e.X0;
            if (e.X1 > maxEdgeX) maxEdgeX = e.X1;
        }

        int pad = erodePx < -0.01f ? 1 : 0;
        int minRow = Math.Clamp((int)MathF.Floor(minEdgeY) - pad, 0, dstHeight - 1);
        int maxRow = Math.Clamp((int)MathF.Ceiling(maxEdgeY) + pad, 0, dstHeight - 1);
        int minCol = Math.Clamp((int)MathF.Floor(minEdgeX) - pad, 0, dstWidth - 1);
        int maxCol = Math.Clamp((int)MathF.Ceiling(maxEdgeX) + pad, 0, dstWidth - 1);
        int rowSpan = maxCol - minCol + 1;
        int boxH = maxRow - minRow + 1;
        if (rowSpan <= 0 || boxH <= 0)
            return;

        const int SubSamples = 8;
        const float InvSub = 1f / SubSamples;
        var covBuf = new float[boxH * rowSpan];
        var hits = new (float X, int Wind)[Math.Max(16, edges.Count)];

        for (int py = minRow; py <= maxRow; py++)
        {
            int boxRow = (py - minRow) * rowSpan;
            for (int sub = 0; sub < SubSamples; sub++)
            {
                float ySub = py + (sub + 0.5f) * InvSub;
                int hitCount = 0;
                for (int i = 0; i < edges.Count; i++)
                {
                    Edge e = edges[i];
                    bool up = e.Y0 <= ySub && e.Y1 > ySub;
                    bool down = e.Y1 <= ySub && e.Y0 > ySub;
                    if (!up && !down)
                        continue;

                    float t = (ySub - e.Y0) / (e.Y1 - e.Y0);
                    float xHit = e.X0 + (e.X1 - e.X0) * t;
                    if (hitCount >= hits.Length)
                        Array.Resize(ref hits, hits.Length * 2);
                    hits[hitCount++] = (xHit, up ? 1 : -1);
                }

                if (hitCount < 2)
                    continue;

                Array.Sort(hits, 0, hitCount, HitComparer);
                int wind = 0;
                float enterX = 0f;
                for (int h = 0; h < hitCount; h++)
                {
                    var hit = hits[h];
                    if (wind == 0)
                        enterX = hit.X;
                    wind += hit.Wind;
                    if (wind == 0)
                    {
                        float x0 = MathF.Max(enterX, minCol);
                        float x1 = MathF.Min(hit.X, maxCol + 1f);
                        if (x1 <= x0)
                            continue;

                        int pxStart = Math.Clamp((int)MathF.Floor(x0), minCol, maxCol);
                        int pxEnd = Math.Clamp((int)MathF.Floor(x1 - 1e-5f), minCol, maxCol);
                        for (int px = pxStart; px <= pxEnd; px++)
                        {
                            float overlap = MathF.Min(px + 1f, x1) - MathF.Max(px, x0);
                            if (overlap > 0f)
                            {
                                int idx = boxRow + (px - minCol);
                                covBuf[idx] = MathF.Min(1f, covBuf[idx] + overlap * InvSub);
                            }
                        }
                    }
                }
            }
        }

        if (MathF.Abs(erodePx) > 0.01f && rowSpan >= 2 && boxH >= 2)
        {
            bool dilate = erodePx < 0f;
            float r = Math.Clamp(MathF.Abs(erodePx), 0f, 0.9f);
            float keep = 1f - r;
            var tempBuf = new float[covBuf.Length];
            for (int y = 0; y < boxH; y++)
            {
                int row = y * rowSpan;
                for (int x = 0; x < rowSpan; x++)
                {
                    float left = x > 0 ? covBuf[row + x - 1] : 0f;
                    float right = x + 1 < rowSpan ? covBuf[row + x + 1] : 0f;
                    float c = covBuf[row + x];
                    float nb = dilate ? MathF.Max(c, MathF.Max(left, right)) : MathF.Min(c, MathF.Min(left, right));
                    tempBuf[row + x] = c * keep + nb * r;
                }
            }

            for (int y = 0; y < boxH; y++)
            {
                int row = y * rowSpan;
                for (int x = 0; x < rowSpan; x++)
                {
                    float up = y > 0 ? tempBuf[(y - 1) * rowSpan + x] : 0f;
                    float down = y + 1 < boxH ? tempBuf[(y + 1) * rowSpan + x] : 0f;
                    float c = tempBuf[row + x];
                    float nb = dilate ? MathF.Max(c, MathF.Max(up, down)) : MathF.Min(c, MathF.Min(up, down));
                    covBuf[row + x] = c * keep + nb * r;
                }
            }
        }

        float lum = (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;
        float gamma = lum > 0.5f ? 0.92f : 1.04f;

        for (int y = 0; y < boxH; y++)
        {
            int dstRow = (minRow + y) * dstWidth;
            int boxRow = y * rowSpan;
            for (int x = 0; x < rowSpan; x++)
            {
                float cov = covBuf[boxRow + x];
                if (cov <= 0.002f)
                    continue;

                float adj = cov >= 0.999f ? 1f : MathF.Pow(cov, gamma);
                int a = Math.Clamp((int)(adj * 255f + 0.5f), 0, 255);
                if (a == 0)
                    continue;

                int off = (dstRow + minCol + x) * 4;
                int oldA = rgba[off + 3];
                int newA = oldA + ((255 - oldA) * a + 127) / 255;
                rgba[off] = color.R;
                rgba[off + 1] = color.G;
                rgba[off + 2] = color.B;
                rgba[off + 3] = (byte)Math.Clamp(newA, 0, 255);
            }
        }
    }

    private static readonly IComparer<(float X, int Wind)> HitComparer =
        Comparer<(float X, int Wind)>.Create(static (a, b) => a.X.CompareTo(b.X));

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));
    private short S16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
    private float F2Dot14(int offset) => S16(offset) / 16384f;

    private static TrueTypeFont? LoadSystemFont(bool bold)
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            if (bold)
            {
                candidates.Add("/System/Library/Fonts/Supplemental/Comic Sans MS Bold.ttf");
                candidates.Add("/Library/Fonts/Comic Sans MS Bold.ttf");
            }
            candidates.Add("/System/Library/Fonts/Supplemental/Comic Sans MS.ttf");
            candidates.Add("/Library/Fonts/Comic Sans MS.ttf");
        }
        else if (OperatingSystem.IsWindows())
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(fonts))
            {
                if (bold)
                    candidates.Add(Path.Combine(fonts, "comicbd.ttf"));
                candidates.Add(Path.Combine(fonts, "comic.ttf"));
            }
        }
        else
        {
            string[] linuxNames = bold
                ? ["ComicSansMSBold.ttf", "comicbd.ttf", "ComicNeue-Bold.ttf", "Comic Sans MS Bold.ttf"]
                : ["ComicSansMS.ttf", "comic.ttf", "ComicNeue-Regular.ttf", "Comic Sans MS.ttf"];
            string[] roots =
            [
                "/usr/share/fonts",
                "/usr/local/share/fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts")
            ];
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (string name in linuxNames)
                {
                    try
                    {
                        foreach (string match in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                        {
                            candidates.Add(match);
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return new TrueTypeFont(File.ReadAllBytes(path));
            }
            catch
            {
            }
        }

        return null;
    }
}
