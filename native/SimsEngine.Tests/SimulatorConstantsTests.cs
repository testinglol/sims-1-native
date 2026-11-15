using System.Buffers.Binary;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SimulatorConstantsTests
{
    [Fact]
    public void ReadsSourceVersionOneRowsWithEvenPaddingAndCaseInsensitiveFirstMatch()
    {
        var file = FileWithConstants(
            ("friendship threshold", 50f, "relationship minimum"),
            ("Min Autonomy Score For Family", 0.0000001f, "family score"),
            ("FRIENDSHIP THRESHOLD", 75f, "duplicate ignored"));
        var chunk = Assert.Single(file.OfType(ResourceType.Fcns));

        var constants = SimulatorConstants.Read(file, chunk);
        var tuning = FindBestInteractionTuning.FromSimulatorConstants(constants);
        var world = new SimulationWorld();
        world.ApplySimulatorConstants(constants);

        Assert.Equal(3, constants.Entries.Count);
        Assert.Equal(50, tuning.FriendshipThreshold);
        Assert.Equal(50, world.FriendshipThreshold);
        Assert.Equal(0.0000001f, tuning.FamilyMinimum);
        Assert.Equal(25f, constants.Get("missing value", 25f));
    }

    [Fact]
    public void MissingEntriesUseOriginalRomDefaults()
    {
        var tuning = FindBestInteractionTuning.FromSimulatorConstants(SimulatorConstants.Empty);

        Assert.Equal(25, tuning.FriendshipThreshold);
        Assert.Equal(0.2f, tuning.FamilyMinimum);
        Assert.Equal(0.05f, tuning.VisitorMinimum);
        Assert.Equal(10, tuning.RandomSelectionCount);
    }

    [Fact]
    public void RoomScoreTuningUsesTheSourceDefaultsAndFcnsOverrides()
    {
        var defaults = SimsEngine.World.RoomScoreTuning.FromSimulatorConstants(SimulatorConstants.Empty);
        Assert.Equal(30f, defaults.ObjectLightContributionFactor);
        Assert.Equal(60f, defaults.AreaClipToScore);
        Assert.Equal(-30f, defaults.AreaScoreForZeroTiles);
        Assert.Equal(30f, defaults.AreaScoreForClipTile);
        Assert.Equal(-20f, defaults.LightScoreForZero);
        Assert.Equal(20f, defaults.LightScoreForOne);
        Assert.Equal(10f, defaults.RoomImpactEffectiveAreaMin);
        Assert.Equal(45f, defaults.RoomImpactEffectiveAreaMax);
        Assert.Equal(10f, defaults.RoomImpactMultiplier);
        Assert.Equal(2f, defaults.OutdoorRoomImpactFactor);
        Assert.Equal(1f, defaults.OutdoorGoodObjectCountFactor);
        Assert.Equal(3f, defaults.OutdoorObjectDivisor);
        Assert.Equal(0f, defaults.OutdoorDaylightBonus);
        Assert.Equal(-20f, defaults.OutdoorDawnDuskBonus);
        Assert.Equal(-50f, defaults.OutdoorNighttimeBonus);
        Assert.Equal(-40f, defaults.FloorScoreForZero);
        Assert.Equal(0f, defaults.FloorScoreFor100);
        Assert.Equal(-40f, defaults.WallScoreForZero);
        Assert.Equal(0f, defaults.WallScoreFor100);

        var file = FileWithConstants(
            ("object light contribution factor", 12f, "light"),
            ("light score for 1.0", 18f, "bright"),
            ("outdoor dawn/dusk bonus", -7f, "dusk"),
            ("floor score for 100%", 3f, "floor"));
        var constants = SimulatorConstants.Read(file, Assert.Single(file.OfType(ResourceType.Fcns)));
        var world = new SimulationWorld();
        world.ApplySimulatorConstants(constants);

        Assert.Equal(12f, world.RoomScoreTuning.ObjectLightContributionFactor);
        Assert.Equal(18f, world.RoomScoreTuning.LightScoreForOne);
        Assert.Equal(-7f, world.RoomScoreTuning.OutdoorDawnDuskBonus);
        Assert.Equal(3f, world.RoomScoreTuning.FloorScoreFor100);
        Assert.Equal(defaults.WallScoreForZero, world.RoomScoreTuning.WallScoreForZero);
    }

    [Fact]
    public void RejectsUnsupportedVersionAndTrailingBytes()
    {
        var unsupported = FileWithConstants([("threshold", 1f, "")], version: 2);
        var unsupportedChunk = Assert.Single(unsupported.OfType(ResourceType.Fcns));
        Assert.Throws<ResourceFormatException>(() => SimulatorConstants.Read(unsupported, unsupportedChunk));

        var trailing = FileWithConstants([("threshold", 1f, "")], appendTrailingByte: true);
        var trailingChunk = Assert.Single(trailing.OfType(ResourceType.Fcns));
        Assert.Throws<ResourceFormatException>(() => SimulatorConstants.Read(trailing, trailingChunk));
    }

    private static IffFile FileWithConstants(
        params (string Name, float Value, string Description)[] entries) =>
        FileWithConstants(entries, 1, appendTrailingByte: false);

    private static IffFile FileWithConstants(
        (string Name, float Value, string Description)[] entries,
        int version = 1,
        bool appendTrailingByte = false)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.Latin1, leaveOpen: true))
        {
            writer.Write(0u);
            writer.Write(version);
            writer.Write("SNCF"u8);
            writer.Write(entries.Length);
            foreach (var (name, value, description) in entries)
            {
                WritePaddedString(writer, name);
                writer.Write(value);
                WritePaddedString(writer, description);
            }
            if (appendTrailingByte) writer.Write((byte)0xaa);
        }

        var chunkSize = IffFile.ChunkHeaderSize + (int)body.Length;
        var bytes = new byte[IffFile.HeaderSize + chunkSize];
        "IFF FILE"u8.CopyTo(bytes);
        var chunk = bytes.AsSpan(IffFile.HeaderSize, IffFile.ChunkHeaderSize);
        "FCNS"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[4..], (uint)chunkSize);
        BinaryPrimitives.WriteUInt16BigEndian(chunk[8..], 2);
        body.ToArray().CopyTo(bytes, IffFile.HeaderSize + IffFile.ChunkHeaderSize);
        return new IffFile(bytes, "synthetic-global.iff");
    }

    private static void WritePaddedString(BinaryWriter writer, string value)
    {
        writer.Write(System.Text.Encoding.Latin1.GetBytes(value));
        writer.Write((byte)0);
        if (writer.BaseStream.Position % 2 != 0) writer.Write((byte)0);
    }
}
