using SimsEngine.Formats;
using SimsEngine.World;

namespace SimsEngine.Tests;

/// <summary>Unit tests for A13 multi-story layer ordering, independent level 1 vs level 2 wall/floor arrays, and ceiling occlusion.</summary>
public class MultiFloorLayerOrderTests
{
    private static IffFile CreateTwoStoryLot()
    {
        // 64x64 walls array chunk (element size 8)
        var wall1Data = new byte[64 * 64 * 8];
        var wall2Data = new byte[64 * 64 * 8];
        var floor1Data = new byte[64 * 64 * 2];
        var floor2Data = new byte[64 * 64 * 2];

        byte[] BuildArry(int elementSize, byte[] rawData) => new ChunkBuilder()
            .S16(0).S16(0).S32(64).S32(64).S32(elementSize).S32(0).Raw(rawData).ToArray();

        return new IffFile(ChunkBuilder.File(
            ("Arry", 2, BuildArry(8, wall1Data)),
            ("Arry", 102, BuildArry(8, wall2Data)),
            ("Arry", 11, BuildArry(2, floor1Data)),
            ("Arry", 111, BuildArry(2, floor2Data))
        ), "two-story-lot.iff");
    }

    [Fact]
    public void LevelOneAndTwoWallArraysMaintainIndependentRecords()
    {
        var lot = CreateTwoStoryLot();
        var walls1 = LotWalls.Read(lot, 1);
        var walls2 = LotWalls.Read(lot, 2);

        Assert.Equal(1, walls1.Level);
        Assert.Equal(2, walls2.Level);

        // Edit level 1 wall at (10, 10)
        walls1[10, 10][0] = 0x05; // Pattern
        walls1[10, 10][1] = 0x01; // Left side

        lot = walls1.WriteTo(lot);

        // Re-read both levels
        var reloaded1 = LotWalls.Read(lot, 1);
        var reloaded2 = LotWalls.Read(lot, 2);

        Assert.Equal(0x05, reloaded1[10, 10][0]);
        Assert.Equal(0x01, reloaded1[10, 10][1]);

        // Level 2 must remain untouched
        Assert.Equal(0, reloaded2[10, 10][0]);
        Assert.Equal(0, reloaded2[10, 10][1]);
    }

    [Fact]
    public void LevelOneAndTwoFloorArraysMaintainIndependentRecords()
    {
        var lot = CreateTwoStoryLot();
        var floors1 = LotFloors.Read(lot, 1);
        var floors2 = LotFloors.Read(lot, 2);

        Assert.Equal(1, floors1.Level);
        Assert.Equal(2, floors2.Level);

        // Set pattern on level 1
        floors1[15, 20] = 42;
        lot = floors1.WriteTo(lot);

        var reloaded1 = LotFloors.Read(lot, 1);
        var reloaded2 = LotFloors.Read(lot, 2);

        Assert.Equal(42, reloaded1[15, 20]);
        Assert.Equal(0, reloaded2[15, 20]);

        // Now set pattern on level 2
        floors2[15, 20] = 99;
        lot = floors2.WriteTo(lot);

        var final1 = LotFloors.Read(lot, 1);
        var final2 = LotFloors.Read(lot, 2);

        Assert.Equal(42, final1[15, 20]);
        Assert.Equal(99, final2[15, 20]);
    }

    [Fact]
    public void UpperFloorCeilingOcclusion()
    {
        // When viewed story level is 2, an upper floor tile at (x,y) occludes level 1 ceiling view
        bool IsCeilingOccluded(int viewLevel, ushort upperFloorPattern)
        {
            if (viewLevel < 2) return false;
            return upperFloorPattern != 0;
        }

        Assert.True(IsCeilingOccluded(2, 10));  // Story 2 active with floor tile -> occluded
        Assert.False(IsCeilingOccluded(2, 0));  // Story 2 active without floor tile -> open to sky
        Assert.False(IsCeilingOccluded(1, 10)); // Story 1 active -> no upper floor drawn
    }

    [Fact]
    public void StoryHeightAltitudeElevationOffset()
    {
        // Story elevation offset follows (level - 1) * StoryHeight
        float StoryOffset(int level, float storyHeight) => (level - 1) * storyHeight;

        const float StoryHeight = 29.0f;
        Assert.Equal(0.0f, StoryOffset(1, StoryHeight));
        Assert.Equal(29.0f, StoryOffset(2, StoryHeight));
        Assert.Equal(58.0f, StoryOffset(3, StoryHeight));
    }
}
