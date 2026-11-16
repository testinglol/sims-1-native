namespace SimsEngine.Formats;

public enum JobTextKind { Title, Description, Offer }

/// <summary>Parses career tracks and progression levels from CARR resources in work.iff.</summary>
public sealed class CareerCatalog : SimsEngine.Simulation.ISimulationCareerHost
{
    private sealed record Level(int[] Requirements, int[] MotiveDeltas, int Salary, int StartHour, int EndHour, int CarType,
        string MaleUniformMesh, string FemaleUniformMesh, string UniformSkin);
    private sealed record Track(ushort Id, string Name, string[] JobNames, Level[] Levels);

    private readonly Dictionary<short, Track> _tracks;
    private readonly int[] _ids;
    private readonly IffFile _file;

    private CareerCatalog(IffFile file, Dictionary<short, Track> tracks, int[] ids)
    {
        _file = file;
        _tracks = tracks;
        _ids = ids;
    }

    public IReadOnlyList<int> CareerTable() => _ids;
    public bool CareerExists(short career) => _tracks.ContainsKey(career);

    public string? TrackName(short career) => _tracks.TryGetValue(career, out var track) ? track.Name : null;
    public string? JobTitle(short career, short level) => _tracks.TryGetValue(career, out var track) &&
        level >= 0 && level < track.JobNames.Length ? track.JobNames[level] : null;

    public string? JobOffer(short career, int level)
    {
        if (!_tracks.TryGetValue(career, out var track)) return null;
        var job = track.JobNames.Length == 0 ? string.Empty : track.JobNames[Math.Clamp(level, 0, track.JobNames.Length - 1)];
        return $"{track.Name} career track for a {job}.";
    }

    /// <summary>Retrieves localized career strings from the string table matching the career's resource ID.</summary>
    public string? JobString(short career, int index, byte language)
    {
        if (!_tracks.ContainsKey(career) || index < 0) return null;
        var chunk = _file.Find(ResourceType.Str, unchecked((ushort)career));
        if (chunk is null) return null;
        var strings = StringTable.Read(_file, chunk).ForLanguage(language);
        return (uint)index < strings.Count ? strings[index] : null;
    }

    /// <summary>Locates job strings within the career's string table using indexed offsets: title at 3 + 2
    /// * level, description at 4 + 2 * level, female title at 23 + level, and base offer text at entries 1
    /// and 2. Female Sims use the gendered variant when non-empty, falling back to the standard title.
    /// </summary>
    public string? JobText(short career, int level, JobTextKind kind, bool female, byte language)
    {
        if (!_tracks.TryGetValue(career, out var track) || (uint)level >= track.Levels.Length) return null;
        var chunk = _file.Find(ResourceType.Str, unchecked((ushort)career));
        if (chunk is null) return null;
        var strings = StringTable.Read(_file, chunk).ForLanguage(language);
        string? At(int index) => (uint)index < strings.Count ? strings[index] : null;
        string? Pick(int male, int femaleIndex) => female && !string.IsNullOrEmpty(At(femaleIndex)) ? At(femaleIndex) : At(male);
        return kind switch
        {
            JobTextKind.Title => Pick(3 + 2 * level, 23 + level),
            JobTextKind.Description => At(4 + 2 * level),
            _ => Pick(1, 2),
        };
    }

    /// <summary>Resolves a school performance grade string from string table 0x1001 in work.iff.</summary>
    public string? GradeString(int performance, byte language)
    {
        var chunk = _file.Find(ResourceType.Str, 0x1001);
        if (chunk is null || performance < 0) return null;
        var strings = StringTable.Read(_file, chunk).ForLanguage(language);
        return (uint)performance < strings.Count ? strings[performance] : "?";
    }

    public short? JobData(short career, short level, short field)
    {
        if (!_tracks.TryGetValue(career, out var track)) return null;
        if (field == 0) return checked((short)track.Levels.Length);
        if (level < 0 || level >= track.Levels.Length) return null;
        var entry = track.Levels[level];
        var value = field switch
        {
            1 => entry.Salary,
            >= 2 and <= 11 => entry.Requirements[field - 2],
            12 => entry.StartHour,
            13 => entry.EndHour,
            >= 14 and <= 20 => entry.MotiveDeltas[field - 14],
            21 => entry.CarType,
            22 => entry.MaleUniformMesh.Length == 0 ? 0 : 1,
            _ => (int?)null,
        };
        return value is { } result ? checked((short)result) : null;
    }

    public bool TryGetCareerUniform(short career, short level, bool female, out string meshTemplate, out string skinTemplate)
    {
        if (_tracks.TryGetValue(career, out var track) && (uint)level < (uint)track.Levels.Length)
        {
            var entry = track.Levels[level];
            meshTemplate = female && entry.FemaleUniformMesh.Length > 0 ? entry.FemaleUniformMesh : entry.MaleUniformMesh;
            skinTemplate = entry.UniformSkin;
            return meshTemplate.Length > 0;
        }
        meshTemplate = string.Empty;
        skinTemplate = string.Empty;
        return false;
    }

    public static CareerCatalog Read(IffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tracks = new Dictionary<short, Track>();
        var ids = new List<int>();
        foreach (var chunk in file.OfType(ResourceType.Parse("CARR")))
        {
            var track = ReadTrack(file, chunk);
            var key = unchecked((short)track.Id);
            if (!tracks.TryAdd(key, track))
                throw new ResourceFormatException(file.Source, chunk.ToString(), 0, $"duplicate career id {track.Id}");
            ids.Add(key);
        }
        return new CareerCatalog(file, tracks, ids.ToArray());
    }

    private static Track ReadTrack(IffFile file, IffChunk chunk)
    {
        var bytes = chunk.Data.ToArray();
        const int headerLength = 13;
        if (bytes.Length < headerLength)
            throw new ResourceFormatException(file.Source, chunk.ToString(), 0, "truncated CARR header");
        if (bytes[12] != 1)
            throw new ResourceFormatException(file.Source, chunk.ToString(), 12, $"unsupported CARR compression code {bytes[12]}");

        var nameEnd = bytes.AsSpan(headerLength).IndexOf((byte)0);
        if (nameEnd < 0) throw new ResourceFormatException(file.Source, chunk.ToString(), headerLength, "unterminated career name");
        var name = ResourceReader.Latin1(bytes.AsSpan(headerLength, nameEnd));
        var fieldStart = headerLength + nameEnd + 1;
        if ((name.Length & 1) != 0) fieldStart++;
        if (fieldStart > bytes.Length)
            throw new ResourceFormatException(file.Source, chunk.ToString(), fieldStart, "career name padding runs past the chunk");

        var decoder = new FieldDecoder(bytes, fieldStart, file.Source, chunk.ToString());
        var count = decoder.ReadInt32();
        if (count is < 1 or > 64)
            throw new ResourceFormatException(file.Source, chunk.ToString(), fieldStart, $"invalid career level count {count}");
        var levels = new Level[count];
        var jobNames = new string[count];
        for (var index = 0; index < count; index++)
        {
            var requirements = new int[10];
            for (var i = 0; i < requirements.Length; i++) requirements[i] = decoder.ReadInt32();
            var motives = new int[7];
            for (var i = 0; i < motives.Length; i++) motives[i] = decoder.ReadInt32();
            var salary = decoder.ReadInt32();
            var start = decoder.ReadInt32();
            var end = decoder.ReadInt32();
            var car = decoder.ReadInt32();
            jobNames[index] = decoder.ReadString(nextField: false);
            var maleUniformMesh = decoder.ReadString(nextField: false);
            var femaleUniformMesh = decoder.ReadString(nextField: false);
            var uniformSkin = decoder.ReadString(nextField: false);
            _ = decoder.ReadString(nextField: index + 1 < count);
            levels[index] = new Level(requirements, motives, salary, start, end, car,
                maleUniformMesh, femaleUniformMesh, uniformSkin);
        }
        return new Track(chunk.Id, name, jobNames, levels);
    }

    /// <summary>Decodes packed IFF field streams: one presence bit, a two-bit width selector, and MSB-first
    /// signed integer bits, with strings byte-aligned and padded to even offsets.</summary>
    private sealed class FieldDecoder(byte[] bytes, int start, string source, string chunk)
    {
        private static readonly int[] Widths = [6, 11, 21, 32];
        private int _byte = start;
        private int _bit;

        public int ReadInt32()
        {
            if (ReadBit() == 0) return 0;
            var selector = (int)ReadBits(2);
            var width = Widths[selector];
            var raw = ReadBits(width);
            var shift = 32 - width;
            return unchecked((int)(raw << shift)) >> shift;
        }

        public string ReadString(bool nextField)
        {
            var startAt = _bit is 0 or 8 ? _byte : _byte + 1;
            if (startAt < 0 || startAt >= bytes.Length)
                throw new ResourceFormatException(source, chunk, startAt, "missing career string");
            var end = bytes.AsSpan(startAt).IndexOf((byte)0);
            if (end < 0) throw new ResourceFormatException(source, chunk, startAt, "unterminated career string");
            var value = ResourceReader.Latin1(bytes.AsSpan(startAt, end));
            var after = startAt + end + 1;
            if ((after & 1) != 0) after++;
            if (after > bytes.Length)
                throw new ResourceFormatException(source, chunk, after, "career string padding runs past the chunk");
            _byte = after;
            _bit = nextField ? 0 : 8;
            return value;
        }

        private uint ReadBits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++) value = (value << 1) | ReadBit();
            return value;
        }

        private uint ReadBit()
        {
            if (_byte >= bytes.Length)
                throw new ResourceFormatException(source, chunk, _byte, "truncated field-encoded career data");
            var value = (uint)((bytes[_byte] >> (7 - _bit)) & 1);
            if (++_bit == 8) { _bit = 0; _byte++; }
            return value;
        }
    }
}
