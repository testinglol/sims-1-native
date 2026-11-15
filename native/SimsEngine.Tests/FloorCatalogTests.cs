using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsEngine.Tests;

public sealed class FloorCatalogTests
{
    [Fact]
    public void OpensSortedFlrResourcesAtTheFirstDynamicPatternId()
    {
        var root = Path.Combine(Path.GetTempPath(), "floor-catalog-" + Guid.NewGuid().ToString("N"));
        var floors = Path.Combine(root, "GameData", "Floors");
        Directory.CreateDirectory(floors);
        try
        {
            var buildStrings = new ChunkBuilder().S16(-3).S16(3)
                .U8(1).CString("10").CString("").U8(1).CString("Base").CString("").U8(1).CString("base").CString("");
            File.WriteAllBytes(Path.Combine(root, "GameData", "Build.iff"),
                ChunkBuilder.File(("STR#", 0x82, buildStrings.ToArray())));
            File.WriteAllBytes(Path.Combine(floors, "zeta.flr"), FloorFile("Zeta", "42"));
            File.WriteAllBytes(Path.Combine(floors, "alpha.flr"), FloorFile("Alpha", "37"));

            var catalog = FloorCatalog.Open(root);

            Assert.Equal(37, catalog.Price(256));
            Assert.Equal(42, catalog.Price(257));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IncludesProviderAssignedFlrIdsBeyondBuildStringCatalog()
    {
        var strings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString("10").CString("")
            .U8(1).CString("Carpet").CString("")
            .U8(1).CString("base floor").CString("");
        var build = new IffFile(ChunkBuilder.File(("STR#", 0x82, strings.ToArray())), "Build.iff");

        var catalog = FloorCatalog.Read(build, dynamicFloors:
        [
            KeyValuePair.Create(256, 37),
            KeyValuePair.Create(65534, 100),
        ]);

        Assert.True(catalog.Contains(1));
        Assert.True(catalog.Contains(256));
        Assert.Equal(37, catalog.Price(256));
        Assert.False(catalog.Contains(65534));
    }

    private static byte[] FloorFile(string name, string price)
    {
        var strings = new ChunkBuilder().S16(-3).S16(3)
            .U8(1).CString(name).CString("")
            .U8(1).CString(price).CString("")
            .U8(1).CString("test floor").CString("");
        return ChunkBuilder.File(("STR#", 0, strings.ToArray()));
    }
}
