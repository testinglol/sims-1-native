using System.Globalization;
using System.Text;

namespace SimsEngine.Formats;

/// <summary>One note of an FSC ambience sequence: the sample to play with its volume, pan, repeat
/// count and play probability. Field order is the tab-separated column order of the game files.</summary>
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

/// <summary>An FSC ambience sequence: the tab-separated plaintext format the game stores in
/// SoundData/Sound.far, ExpansionShared/Sound/Sound.far and ExpansionPack7/Sound/Sound.far
/// (Outdoors\sim_amb.fsc, Beach\beach.fsc, Park\hotdatepark_amb.fsc, MTOutdoors\magic_amb.fsc).
/// The sequencer reads one note per beat (60 / tempo seconds): a note with Rand set ends the
/// current run and restarts from a random jump point, Loop holds the note for extra beats, and
/// Prob gates the play on a draw below it out of 16. Those stepping semantics are inferred, not
/// verified against Sims.exe: its note-stepping tick has not been located in the decompilation,
/// and the original file format does not document the randomization rule.
/// </summary>
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

    /// <summary>Note indices with Rand set: the jump targets a restart picks from.</summary>
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

    /// <summary>Reads a sequence from its file bytes (UTF-8, like the previous StreamReader parse).
    /// Comment lines start with '#', the header is the first non-comment line after the version code,
    /// and note lines carry exactly 20 tab-separated columns; anything else is skipped. Malformed data
    /// throws a <see cref="ResourceFormatException"/> naming <paramref name="source"/> and the byte offset.</summary>
    public static FscSequence Read(byte[] bytes, string source = "FSC")
    {
        var data = bytes.AsSpan();
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
        // Lines split the way StreamReader.ReadLine does (\r\n, \r or \n), so both readers see the same rows.
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

    /// <summary>Anything but "0" counts as set, matching the previous reader exactly: these are
    /// Maxis-shipped files the old parse already loads, so acceptance stays identical.</summary>
    private static bool Flag(string value) => value != "0";
}
