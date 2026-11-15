using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>The FSC ambience format: header columns, note fields, jump points and skips.</summary>
public class FscSequenceTests
{
    private const string Fixture =
        "FSC V1.0\n" +
        "# a comment before the header\n" +
        "HEAD\t900\t1\t0\t127\t4\t8\t100\t4\t0\t0\t-\t-\t2\t3\n" +
        "# another comment\n" +
        "cells\n" +
        "0\t800\t1\t512\t256\t0\t10\t20\t5\t0\t3\t0\t2\t8\t-100\t100\t1\t7\t0\tbirds.wav\n" +
        "# a comment amid the notes\n" +
        "1\t1024\t0\t0\t1024\t1\t0\t0\t0\t1\t0\t1\t0\t0\t0\t0\t0\t0\t1\tNONE\n" +
        "too\tfew\n" +
        "\n" +
        "2\t512\t0\t1024\t0\t0\t0\t0\t0\t0\t1\t0\t0\t16\t0\t0\t0\t0\t0\twater.wav\n";

    private static FscSequence Read() => FscSequence.Read(Encoding.UTF8.GetBytes(Fixture));

    [Fact]
    public void HeaderColumnsLandInOrder()
    {
        var sequence = Read();
        Assert.Equal("FSC V1.0", sequence.VersionCode);
        Assert.Equal(900, sequence.MasterVolume);
        Assert.Equal(1, sequence.Priority);
        Assert.Equal(0, sequence.Min);
        Assert.Equal(127, sequence.Max);
        Assert.Equal(4, sequence.Rows);
        Assert.Equal(8, sequence.Columns);
        Assert.Equal(100, sequence.Tempo);
        Assert.Equal(4, sequence.BeatsPerBar);
        Assert.Equal(0, sequence.SelX);
        Assert.Equal(0, sequence.SelY);
        Assert.Equal(0, sequence.QuanX);
        Assert.Equal(0, sequence.QuanY);
        Assert.Equal(2, sequence.DiffX);
        Assert.Equal(3, sequence.DiffY);
    }

    [Fact]
    public void NotesSkipCommentsCellsShortAndBlankLines()
    {
        var sequence = Read();
        Assert.Equal(3, sequence.Notes.Count);
        Assert.Equal("birds.wav", sequence.Notes[0].Filename);
        Assert.Equal("NONE", sequence.Notes[1].Filename);
        Assert.Equal("water.wav", sequence.Notes[2].Filename);
    }

    [Fact]
    public void NoteFieldsLandInColumnOrder()
    {
        var first = Read().Notes[0];
        Assert.Equal(new FscNote(800, true, 512, 256, false, 10, 20, 5, false, 3, false, 2, 8, -100, 100, true, 7, false, "birds.wav"), first);
        var second = Read().Notes[1];
        Assert.True(second.Stereo);
        Assert.False(second.Rand);
        Assert.Equal(0, second.Prob);
    }

    [Fact]
    public void RandomJumpPointsAreTheRandNoteIndices()
    {
        Assert.Equal([0], Read().RandomJumpPoints);
    }

    [Fact]
    public void ShortHeaderThrows()
    {
        Assert.Throws<ResourceFormatException>(() => FscSequence.Read("v\nHEAD\t1\t2"u8.ToArray()));
    }

    [Fact]
    public void BadNoteNumberThrows()
    {
        var bad = Fixture.Replace("\t800\t", "\t8x0\t");
        Assert.Throws<ResourceFormatException>(() => FscSequence.Read(Encoding.UTF8.GetBytes(bad)));
    }

    [Fact]
    public void MissingHeaderThrows()
    {
        Assert.Throws<ResourceFormatException>(() => FscSequence.Read("# only a comment\n"u8.ToArray()));
    }
}
