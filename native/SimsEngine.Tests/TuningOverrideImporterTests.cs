using System.Reflection;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class TuningOverrideImporterTests
{
    [Fact]
    public void Version2HeaderIsAccepted()
    {
        var registry = new ObjectRegistry();
        var text = "ObjFile\tVersion 2\n";
        var issues = TuningOverrideImporter.ApplyText(text, registry);
        Assert.DoesNotContain(issues, i => i.Contains("not supported"));
    }

    [Theory]
    [InlineData("Version 1")]
    [InlineData("Version 3")]
    [InlineData("Version 0")]
    public void NonVersion2HeaderIsRejected(string versionHeader)
    {
        var registry = new ObjectRegistry();
        var text = $"ObjFile\t{versionHeader}\n";
        var issues = TuningOverrideImporter.ApplyText(text, registry);
        Assert.Contains(issues, i => i.Contains("is not supported; Sims.exe accepts version 2"));
    }

    [Fact]
    public void MissingVersionHeaderIsRejected()
    {
        var registry = new ObjectRegistry();
        var text = "ObjFile\tNoVersionHere\n";
        var issues = TuningOverrideImporter.ApplyText(text, registry);
        Assert.Contains(issues, i => i.Contains("no parseable Version header"));
    }

    [Fact]
    public void ClearGuidOverridesResetsGlobalState()
    {
        var guid = 0x12345678u;
        var group = new GuidTuningOverrideGroup(guid);
        TuningOverrideImporter.SetGuidOverride(guid, group);
        Assert.NotNull(TuningOverrideImporter.GetGuidOverrides(guid));

        TuningOverrideImporter.ClearGuidOverrides();
        Assert.Null(TuningOverrideImporter.GetGuidOverrides(guid));
    }

    [Fact]
    public void FlaggedBconOverridesConstantsInBehaviorScope()
    {
        var (file, chunk) = CreateBconChunk(0x1001, tuningFlag: true, [10, 20, 30]);
        var scope = new BehaviorScope(file, file, null);

        Assert.Equal(10, scope.Constants(0x1001, allowTuningOverride: true)![0]);
        Assert.Equal(20, scope.Constants(0x1001, allowTuningOverride: true)![1]);

        scope.SetConstantOverride(0x1001, [99, 88, 30]);
        Assert.Equal(99, scope.Constants(0x1001, allowTuningOverride: true)![0]);
        Assert.Equal(88, scope.Constants(0x1001, allowTuningOverride: true)![1]);
        Assert.Equal(30, scope.Constants(0x1001, allowTuningOverride: true)![2]);

        scope.SetConstantOverride(0x1001, null);
        Assert.Equal(10, scope.Constants(0x1001, allowTuningOverride: true)![0]);
    }

    [Fact]
    public void GuidTuningGroupParsesMotiveAdsAndAttenuation()
    {
        var registry = new ObjectRegistry();
        var text = string.Join("\n",
            "ObjFile\tVersion 2",
            "Target\tGUID: 0xCAFEBABE",
            "0\t1\tmod\t15",
            "0\t2\tads\t18",
            "0\t0\tatt\thigh",
            ""
        );

        var issues = TuningOverrideImporter.ApplyText(text, registry);
        var group = TuningOverrideImporter.GetGuidOverrides(0xCAFEBABE);
        Assert.NotNull(group);
        Assert.True(group.Interactions.TryGetValue(0, out var ov));
        Assert.Equal(4u, ov.AttenuationCode);
        Assert.Equal(0.0f, ov.AttenuationValue);

        var mappedEnergy = TuningOverrideImporter.MapMotiveCode(1);
        var mappedComfort = TuningOverrideImporter.MapMotiveCode(2);
        Assert.True(ov.Motives.TryGetValue(mappedEnergy, out var energyOv));
        Assert.Equal((short)15, energyOv.DeltaAdjustment);

        Assert.True(ov.Motives.TryGetValue(mappedComfort, out var comfortOv));
        Assert.Equal((short)3, comfortOv.AdKind);
        Assert.Equal((short)1, comfortOv.ActiveFlag);
    }

    [Fact]
    public void ApplyGuidOverridesUpdatesInteractionTable()
    {
        var guid = 0xABCD1234u;
        var table = CreateInteractionTable();

        var group = new GuidTuningOverrideGroup(guid);
        var ov = new InteractionOverride
        {
            AttenuationCode = 2,
            AttenuationValue = 0.66f
        };
        ov.Motives[9] = new MotiveOverride { DeltaAdjustment = 50, AdKind = 1, ActiveFlag = 1 };
        group.Interactions[0] = ov;
        TuningOverrideImporter.SetGuidOverride(guid, group);

        var updated = TuningOverrideImporter.ApplyGuidOverrides(guid, table);
        Assert.Single(updated.Interactions);
        var interaction = updated.Interactions[0];
        Assert.Equal(2u, interaction.AttenuationCode);
        Assert.Equal(0.66f, interaction.AttenuationValue);
        Assert.True(interaction.Motives.Count > 9);
        Assert.Equal((short)50, interaction.Motives[9].Delta);
        Assert.Equal((short)1, interaction.Motives[9].Personality);
    }

    [Fact]
    public void ErrorResilienceSkipsMalformedRows()
    {
        var registry = new ObjectRegistry();
        var text = string.Join("\n",
            "ObjFile\tVersion 2",
            "Target\tGUID: 0x99999999",
            "notanumber\t1\tmod\t10",
            "0\t99\tmod\t10",
            "0\t1\tads\t999",
            "0\t0\tunknownkw\tfoo",
            ""
        );

        var issues = TuningOverrideImporter.ApplyText(text, registry);
        Assert.Contains(issues, i => i.Contains("invalid interaction index"));
        Assert.Contains(issues, i => i.Contains("invalid motive index"));
        Assert.Contains(issues, i => i.Contains("invalid advertisement impact"));
        Assert.Contains(issues, i => i.Contains("unrecognized keyword"));
    }

    [Theory]
    [InlineData("none", 1)]
    [InlineData("low", 2)]
    [InlineData("medium", 3)]
    [InlineData("high", 4)]
    [InlineData("UNKNOWN_LEVEL", 3)]
    public void AttenuationMappingMatchesSimsExe(string input, int expected)
    {
        Assert.Equal(expected, TuningOverrideImporter.GetGameAttFromDBAtt(input));
    }

    [Theory]
    [InlineData(18, 3)]
    [InlineData(19, 11)]
    [InlineData(20, 1)]
    [InlineData(21, 9)]
    [InlineData(22, 7)]
    [InlineData(23, 21)]
    [InlineData(24, 15)]
    [InlineData(25, 14)]
    [InlineData(26, 19)]
    [InlineData(27, 22)]
    [InlineData(28, 16)]
    [InlineData(33, 4)]
    [InlineData(34, 12)]
    [InlineData(35, 2)]
    [InlineData(36, 10)]
    [InlineData(37, 8)]
    [InlineData(999, -1)]
    public void AdvertisementImpactMappingMatchesMacAssembly(int impact, int expected)
    {
        Assert.Equal(expected, TuningOverrideImporter.GetGameAdFromDBImpact(impact));
    }

    private static (IffFile File, IffChunk Chunk) CreateBconChunk(ushort id, bool tuningFlag, short[] constants)
    {
        var builder = new ChunkBuilder();
        builder.U8((byte)constants.Length);
        builder.U8((byte)(tuningFlag ? 0x80 : 0x00));
        foreach (var c in constants) builder.S16(c);
        var bytes = ChunkBuilder.File(("BCON", id, builder.ToArray()));
        var file = new IffFile(bytes, "bcon-test.iff");
        return (file, file.Find(ResourceType.Bcon, id)!);
    }

    private static InteractionTable CreateInteractionTable()
    {
        var builder = new ChunkBuilder();
        builder.S16(1);
        builder.S16(6);
        builder.S16(1);
        builder.S16(0);
        builder.S32(10);
        builder.U32(0);
        builder.S32(0);
        builder.U32(1);
        builder.S32(BitConverter.SingleToInt32Bits(1.0f));
        builder.U32(0);
        builder.S32(0);
        for (var i = 0; i < 10; i++)
        {
            builder.S16(0);
            builder.S16(10);
            builder.S16(0);
        }
        var bytes = ChunkBuilder.File(("TTAB", 128, builder.ToArray()));
        var file = new IffFile(bytes, "ttab-test.iff");
        return InteractionTable.Read(file, file.Find(ResourceType.Ttab, 128)!);
    }
}
