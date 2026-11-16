using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class Wave11CasAndFamilyBinTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static IffFile CreateEmptyIff(string source)
    {
        var header = new byte[IffFile.HeaderSize];
        "IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0"u8.CopyTo(header);
        return new IffFile(header, source);
    }

    private static Neighbor CreateNeighbor(int slot, string name, uint guid, short genderFlags)
    {
        var personData = new short[88];
        personData[(int)PersonData.PersonAge] = 27;
        personData[(int)PersonData.Gender] = genderFlags;
        return new Neighbor(
            Slot: slot,
            RecordVersion: 9,
            Name: name,
            Field0x1C: 0,
            PersonData: personData,
            NeighborId: checked((short)(slot + 1)),
            Guid: guid,
            Relationships: new Dictionary<int, IReadOnlyList<int>>(),
            StoredFormat: 10,
            PersonDataTrailingWord: 0);
    }

    private static string BuildCmx(params string[] appearanceNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("version 300");
        sb.AppendLine("0");
        sb.AppendLine(appearanceNames.Length.ToString());
        foreach (var name in appearanceNames)
        {
            sb.AppendLine(name);
            sb.AppendLine("0");
            sb.AppendLine("0");
            sb.AppendLine("1");
            sb.AppendLine("PELVIS");
            sb.AppendLine($"xskin-{name}-PELVIS-BODY");
            sb.AppendLine("0");
            sb.AppendLine("0");
        }
        sb.AppendLine("0");
        return sb.ToString();
    }

    [Fact]
    public void UnhousedFamilies_IncludesFlagsZeroExcludesStraysAndNormalizesLegacyNetWorth()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sims-wave11-bin-{Guid.NewGuid():N}");
        try
        {
            var gameDir = Path.Combine(tempRoot, "game");
            var userDir = Path.Combine(tempRoot, "profile", "UserData");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(Path.Combine(userDir, "Characters"));

            const uint maxisGuid = 0x10000001u;
            const uint legacyGuid = 0x10000002u;
            const uint strayPetGuid = 0x10000003u;
            const uint strayFlaggedHumanGuid = 0x10000004u;
            const uint housedGuid = 0x10000005u;

            var neighbors = new Neighbor[]
            {
                CreateNeighbor(0, "MaxisSim", maxisGuid, 0),
                CreateNeighbor(1, "LegacySim", legacyGuid, 1),
                CreateNeighbor(2, "StrayDog", strayPetGuid, 2),
                CreateNeighbor(3, "HiddenSim", strayFlaggedHumanGuid, 0),
                CreateNeighbor(4, "HousedSim", housedGuid, 0),
            };
            var neighborsByGuid = neighbors.ToDictionary(n => n.Guid, n => n);

            var maxisFamily = Family.CreateNew(1, 0, [maxisGuid], 20_000).WithValue(0);
            var legacyFamily = Family.CreateNew(2, 0, [legacyGuid], 20_000);
            var strayPetFamily = Family.CreateNew(3, 0, [strayPetGuid], 20_000).WithValue(0).WithFields(flags: 0x18u);
            var hiddenFamily = Family.CreateNew(4, 0, [strayFlaggedHumanGuid], 20_000).WithValue(0).WithFields(flags: 0x18u);
            var housedFamily = Family.CreateNew(5, 1, [housedGuid], 15_000).WithValue(5_000).WithFields(flags: 1u);

            Assert.True(NeighborhoodExport.IsPetsOnly(strayPetFamily, neighborsByGuid));
            Assert.False(NeighborhoodExport.IsPetsOnly(maxisFamily, neighborsByGuid));

            var nghPath = Path.Combine(userDir, "Neighborhood.iff");
            var iff = CreateEmptyIff(nghPath)
                .WithAddedOrReplacedChunk(ResourceType.Parse("NBRS"), 1, new Neighbors(73, neighbors.Length, neighbors).WriteChunkData(swap: false))
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 1, maxisFamily.Write(1), 0, "MaxisBin")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 1, StringTable.CreateLanguageValues(["MaxisBin"], 1), 0, "MaxisBin")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 2, legacyFamily.Write(2), 0, "LegacyBin")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 2, StringTable.CreateLanguageValues(["LegacyBin"], 1), 0, "LegacyBin")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 3, strayPetFamily.Write(3), 0, "Strays")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 3, StringTable.CreateLanguageValues(["Strays"], 1), 0, "Strays")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 4, hiddenFamily.Write(4), 0, "HiddenStrayFlag")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 4, StringTable.CreateLanguageValues(["HiddenStrayFlag"], 1), 0, "HiddenStrayFlag")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMI"), 5, housedFamily.Write(5), 0, "Housed")
                .WithAddedOrReplacedChunk(ResourceType.Parse("FAMs"), 5, StringTable.CreateLanguageValues(["Housed"], 1), 0, "Housed");

            File.WriteAllBytes(nghPath, iff.Bytes.ToArray());

            var exported = NeighborhoodExport.UnhousedFamilies(
                userDir,
                gameDir,
                language: 1,
                existingRegistry: new ObjectRegistry());

            Assert.Equal(["MaxisBin", "LegacyBin"], exported.Select(f => f.Name).ToArray());

            var maxis = exported[0];
            Assert.Equal(20_000, maxis.Budget);
            Assert.Equal(20_000, maxis.NetWorth);

            var legacy = exported[1];
            Assert.Equal(20_000, legacy.Budget);
            Assert.Equal(20_000, legacy.NetWorth);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_SyntheticCmxAndUnisexChildAssetsAreLoadedAndDeduplicated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sims-wave11-cas-{Guid.NewGuid():N}");
        try
        {
            var skinsDir = Path.Combine(tempRoot, "GameData", "Skins");
            Directory.CreateDirectory(skinsDir);

            File.WriteAllText(
                Path.Combine(skinsDir, "first.cmx"),
                BuildCmx("c001uc_unisex", "b001ucchd_01", "c002fc_girl", "b002fcchd_01", "c099fa_cmx", "b099fafit_cmx"));
            File.WriteAllText(
                Path.Combine(skinsDir, "second.cmx"),
                BuildCmx("c001uc_unisex", "b001ucchd_01", "c099fa_cmx", "b100fafit_cmx"));

            foreach (var tex in new[]
            {
                "C001UClgt_unisex",
                "B001UCChdlgt_01",
                "C002FClgt_girl",
                "B002FCChdlgt_01",
                "C099FAlgt_cmx",
                "B099FAFitlgt_cmx",
                "B100FAFitlgt_cmx",
            })
            {
                File.WriteAllBytes(Path.Combine(skinsDir, tex + ".bmp"), []);
            }

            var childCatalog = HouseholdAppearance.Catalog(tempRoot, null, "f", "child", "lgt");
            Assert.Equal(
                ["c002fc_girl|C002FClgt_girl", "c001uc_unisex|C001UClgt_unisex"],
                childCatalog.Heads.Select(h => h.Id).ToArray());
            Assert.Equal(
                ["b002fcchd_01|B002FCChdlgt_01", "b001ucchd_01|B001UCChdlgt_01"],
                childCatalog.Bodies.Select(b => b.Id).ToArray());

            var adultCatalog = HouseholdAppearance.Catalog(tempRoot, null, "f", "adult", "lgt");
            Assert.Equal(
                ["c099fa_cmx|C099FAlgt_cmx"],
                adultCatalog.Heads.Select(h => h.Id).ToArray());
            Assert.Equal(
                ["b099fafit_cmx|B099FAFitlgt_cmx", "b100fafit_cmx|B100FAFitlgt_cmx"],
                adultCatalog.Bodies.Select(b => b.Id).ToArray());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Catalog_AgainstInstalledGame_IncludesCmxItemsAndUnisexChildBodies()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null) return;

        var childCatalog = HouseholdAppearance.Catalog(gameDir, null, "f", "child", "lgt");
        Assert.NotEmpty(childCatalog.Heads);
        Assert.NotEmpty(childCatalog.Bodies);
        Assert.Contains(childCatalog.Heads, h => h.Id.StartsWith("c009fc_wizd|", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(childCatalog.Bodies, b => b.Id.StartsWith("b013fcchd_wizd|", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(childCatalog.Bodies.Length, childCatalog.Bodies.Select(b => b.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var adultCatalog = HouseholdAppearance.Catalog(gameDir, null, "f", "adult", "lgt");
        Assert.NotEmpty(adultCatalog.Heads);
        Assert.NotEmpty(adultCatalog.Bodies);
        Assert.Equal(adultCatalog.Bodies.Length, adultCatalog.Bodies.Select(b => b.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BuildCharacterBodyStrings_WritesShippedStr200IndicesAndRoundTrips()
    {
        static StringTable RoundTrip(byte[] bytes)
        {
            var iff = CreateEmptyIff("User00000.iff").WithAddedChunk(ResourceType.Str, 200, bytes);
            var chunk = iff.Find(ResourceType.Str, 200)!;
            var table = StringTable.Read(iff, chunk);
            Assert.Equal(-3, table.Format);
            Assert.True(table.ReadBySimsExe);
            return table;
        }

        var templateSeed = Enumerable.Repeat(string.Empty, 30).ToArray();
        templateSeed[3] = "HEAD";
        templateSeed[4] = "Top";
        templateSeed[5] = "R_HAND";
        templateSeed[6] = "Palm";
        var templateBytes = StringTable.CreateLanguageValues(templateSeed, 1);
        var templateIff = CreateEmptyIff("template.iff").WithAddedChunk(ResourceType.Str, 200, templateBytes);
        var templateTable = StringTable.Read(templateIff, templateIff.Find(ResourceType.Str, 200)!);

        var maleBytes = HouseholdAppearance.BuildCharacterBodyStrings(
            templateTable,
            game: string.Empty,
            registry: null,
            isChild: false,
            isFemale: false,
            bodyChoice: "b002mafat_01|B002MAFatlgt_slob",
            headChoice: "c010ma_baldbeard|C010MAlgt_baldbeard01",
            skin: "lgt");
        var maleStrings = RoundTrip(maleBytes).ForLanguage(1);

        Assert.Equal("adult", maleStrings[0]);
        Assert.Equal("b002mafat_01,BODY=B002MAFatlgt_slob", maleStrings[1]);
        Assert.Equal("c010ma_baldbeard,HEAD-HEAD=C010MAlgt_baldbeard01", maleStrings[2]);
        Assert.Equal("HEAD", maleStrings[3]);
        Assert.Equal("Top", maleStrings[4]);
        Assert.Equal("male", maleStrings[12]);
        Assert.Equal("27", maleStrings[13]);
        Assert.Equal("lgt", maleStrings[14]);
        Assert.Equal("nmfat_01,BODY=nmfatlgt_01", maleStrings[15]);
        Assert.Equal("nmfat_01,BODY=umfatlgt_briefs01", maleStrings[16]);
        Assert.Equal("HmLO,HAND=huaolgt", maleStrings[17]);
        Assert.Equal("HmRO,HAND=huaolgt", maleStrings[18]);
        Assert.Equal("HmLP,HAND=huaplgt", maleStrings[19]);
        Assert.Equal("HmRP,HAND=huaplgt", maleStrings[20]);
        Assert.Equal("HmLC,HAND=huaclgt", maleStrings[21]);
        Assert.Equal("HmRC,HAND=huaclgt", maleStrings[22]);
        Assert.Equal("fmfat_01,BODY=fmfatlgt_01", maleStrings[27]);

        var femaleBytes = HouseholdAppearance.BuildCharacterBodyStrings(
            templateTable: null,
            game: string.Empty,
            registry: null,
            isChild: false,
            isFemale: true,
            bodyChoice: "b003fafit_01|B003FAFitdrk_casual",
            headChoice: "c001fa_test|C001FAdrk_test",
            skin: "drk");
        var femaleStrings = RoundTrip(femaleBytes).ForLanguage(1);

        Assert.Equal("adult", femaleStrings[0]);
        Assert.Equal("b003fafit_01,BODY=B003FAFitdrk_casual", femaleStrings[1]);
        Assert.Equal("c001fa_test,HEAD-HEAD=C001FAdrk_test", femaleStrings[2]);
        Assert.Equal("female", femaleStrings[12]);
        Assert.Equal("27", femaleStrings[13]);
        Assert.Equal("drk", femaleStrings[14]);
        Assert.Equal("nffit_01,BODY=nffitdrk_01", femaleStrings[15]);
        Assert.Equal("nffit_01,BODY=uffitdrk_undies01", femaleStrings[16]);
        Assert.Equal("HfLO,HAND=huaodrk", femaleStrings[17]);
        Assert.Equal("HfRO,HAND=huaodrk", femaleStrings[18]);
        Assert.Equal("HfLP,HAND=huapdrk", femaleStrings[19]);
        Assert.Equal("HfRP,HAND=huapdrk", femaleStrings[20]);
        Assert.Equal("HfLC,HAND=huacdrk", femaleStrings[21]);
        Assert.Equal("HfRC,HAND=huacdrk", femaleStrings[22]);
        Assert.Equal("fffit_01,BODY=fffitdrk_01", femaleStrings[27]);

        var childBytes = HouseholdAppearance.BuildCharacterBodyStrings(
            templateTable: null,
            game: string.Empty,
            registry: null,
            isChild: true,
            isFemale: true,
            bodyChoice: "b009fcchd_01|B009FCChdmed_Nerd",
            headChoice: "c006fc_nerd|C006FCmed_nerd",
            skin: "med");
        var childStrings = RoundTrip(childBytes).ForLanguage(1);

        Assert.Equal("child", childStrings[0]);
        Assert.Equal("b009fcchd_01,BODY=B009FCChdmed_Nerd", childStrings[1]);
        Assert.Equal("c006fc_nerd,HEAD-HEAD=C006FCmed_nerd", childStrings[2]);
        Assert.Equal("female", childStrings[12]);
        Assert.Equal("9", childStrings[13]);
        Assert.Equal("med", childStrings[14]);
        Assert.Equal("nuchd_01,BODY=nuchdmed_01", childStrings[15]);
        Assert.Equal("nuchd_01,BODY=ufchdmed_undies01", childStrings[16]);
        Assert.Equal("HuLO,HAND=huaomed", childStrings[17]);
        Assert.Equal("HuRO,HAND=huaomed", childStrings[18]);
        Assert.Equal("HuLP,HAND=huapmed", childStrings[19]);
        Assert.Equal("HuRP,HAND=huapmed", childStrings[20]);
        Assert.Equal("HuLC,HAND=huacmed", childStrings[21]);
        Assert.Equal("HuRC,HAND=huacmed", childStrings[22]);
        Assert.Equal(",BODY=", childStrings[27]);
    }
}
