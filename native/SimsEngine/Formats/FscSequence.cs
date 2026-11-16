using System.Globalization;
using System.Text;

namespace SimsEngine.Formats;

public sealed record FscNote(
    ushort Volume,
    bool Rand,
    ushort LrPan,
    ushort FbPan,
    bool Rand2,
    ushort Fin,
    ushort Fout,
    ushort Delay,
    bool Rand3,
    ushort Loop,
    bool Loop2,
    ushort Quant,
    ushort Prob,
    short PitchL,
    short PitchR,
    bool Fast,
    ushort GroupId,
    bool Stereo,
    string Filename);

/// <summary>Represents an FSC audio ambience sequence. The sequencer evaluates one note per beat (60 /
/// tempo seconds), where loop counts sustain playback, a probability value out of 16 gates execution, and a
/// randomize flag branches playback to an indexed jump point.</summary>
public sealed class FscSequence
{
    public string VersionCode { get; }
    public ushort MasterVolume { get; }
    public ushort Priority { get; }
    public ushort Min { get; }
    public ushort Max { get; }
    public ushort Rows { get; }
    public ushort Columns { get; }
    public ushort Tempo { get; }
    public ushort BeatsPerBar { get; }
    public ushort SelX { get; }
    public ushort SelY { get; }
    public ushort QuanX { get; }
    public ushort QuanY { get; }
    public ushort DiffX { get; }
    public ushort DiffY { get; }
    public IReadOnlyList<FscNote> Notes { get; }

    public IReadOnlyList<int> RandomJumpPoints { get; }

    private FscSequence(string versionCode, ushort[] header, List<FscNote> notes, List<int> randomJumpPoints)
    {
        VersionCode = versionCode;
        MasterVolume = header[0];
        Priority = header[1];
        Min = header[2];
        Max = header[3];
        Rows = header[4];
        Columns = header[5];
        Tempo = header[6];
        BeatsPerBar = header[7];
        SelX = header[8];
        SelY = header[9];
        QuanX = header[10];
        QuanY = header[11];
        DiffX = header[12];
        DiffY = header[13];
        Notes = notes;
        RandomJumpPoints = randomJumpPoints;
    }

    /// <summary>Parses an FSC ambience sequence file where comments begin with '#', followed by a header
    /// line and 20 tab-delimited columns per note entry.</summary>
    public static FscSequence Read(byte[] bytes, string source = "FSC")
    {
        var data = bytes.AsSpan();
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
        var lines = new List<(string Text, int Offset)>();
        var start = 0;
        for (var index = 0; index <= data.Length; index++)
        {
            var breaks = index == data.Length || data[index] == (byte)'\r' || data[index] == (byte)'\n';
            if (!breaks) continue;
            lines.Add((Encoding.UTF8.GetString(data.Slice(start, index - start)), start));
            if (index < data.Length && data[index] == (byte)'\r' && index + 1 < data.Length && data[index + 1] == (byte)'\n') index++;
            start = index + 1;
        }
        var number = 0;
        var end = data.Length;
        (string Text, int Offset) Next()
        {
            while (number < lines.Count)
            {
                var line = lines[number++];
                if (!line.Text.StartsWith('#')) return line;
            }
            throw new ResourceFormatException(source, null, end, "missing header line");
        }

        var versionCode = Next();
        var headerLine = Next();
        var headerFields = headerLine.Text.Split('\t');
        if (headerFields.Length < 15) throw new ResourceFormatException(source, null, headerLine.Offset, $"header has {headerFields.Length} columns, expected at least 15");
        var header = new ushort[14];
        for (var index = 0; index < 14; index++)
        {
            if (headerFields[index + 1].StartsWith('-'))
            {
                if (index is not (10 or 11)) throw new ResourceFormatException(source, null, headerLine.Offset, $"header column {index + 1} is '{headerFields[index + 1]}'");
                header[index] = 0;
            }
            else if (!ushort.TryParse(headerFields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out header[index]))
                throw new ResourceFormatException(source, null, headerLine.Offset, $"header column {index + 1} is '{headerFields[index + 1]}'");
        }

        var notes = new List<FscNote>();
        var randomJumpPoints = new List<int>();
        while (number < lines.Count)
        {
            var (text, offset) = lines[number++];
            if (text.StartsWith('#')) continue;
            if (text.StartsWith("cells", StringComparison.Ordinal)) continue;
            if (text.Length == 0) continue;
            var values = text.Split('\t');
            if (values.Length != 20) continue;
            var note = new FscNote(
                Number(source, offset, values[1]), Flag(values[2]), Number(source, offset, values[3]),
                Number(source, offset, values[4]), Flag(values[5]), Number(source, offset, values[6]),
                Number(source, offset, values[7]), Number(source, offset, values[8]), Flag(values[9]),
                Number(source, offset, values[10]), Flag(values[11]), Number(source, offset, values[12]),
                Number(source, offset, values[13]), Signed(source, offset, values[14]), Signed(source, offset, values[15]),
                Flag(values[16]), Number(source, offset, values[17]), Flag(values[18]), values[19]);
            if (note.Rand) randomJumpPoints.Add(notes.Count);
            notes.Add(note);
        }
        return new FscSequence(versionCode.Text, header, notes, randomJumpPoints);
    }

    private static ushort Number(string source, int offset, string value)
    {
        if (ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)) return result;
        throw new ResourceFormatException(source, null, offset, $"'{value}' is not a number");
    }

    private static short Signed(string source, int offset, string value)
    {
        if (short.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)) return result;
        throw new ResourceFormatException(source, null, offset, $"'{value}' is not a number");
    }

    private static bool Flag(string value) => value != "0";
}
