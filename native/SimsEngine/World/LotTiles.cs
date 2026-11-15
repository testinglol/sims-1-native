using SimsEngine.Formats;

namespace SimsEngine.World;

/// <summary>The lot's per-level tile flag grids (Arry 8 for level 1, Arry 108 for level 2, one byte per tile), which
/// FUN_0047870b loads into lot manager +0x34 and +0x38. Bit 0x20 marks ground outside the playable lot: while the
/// debug flag DAT_006e4a84 is zero, FUN_00488924 refuses every build tool on such a tile, and it always reads the
/// level-1 grid. Bit 0 marks a tile the player may build on (FUN_004893e4, which reads the grid of the tile's level).
/// A lot without a grid has no restriction from it.</summary>
public sealed class LotTiles
{
    private readonly LotArray?[] grids;

    private LotTiles(LotArray?[] grids) => this.grids = grids;

    public static LotTiles Read(IffFile lot)
    {
        LotArray? Grid(ushort id)
        {
            if (lot.Find(ResourceType.Parse("Arry"), id) is not { } chunk) return null;
            var array = LotArray.Read(lot, chunk);
            return array.Rows == LotWalls.Size && array.Width == LotWalls.Size && array.ElementSize == 1 ? array : null;
        }
        return new LotTiles([Grid(8), Grid(108)]);
    }

    /// <summary>FUN_00488924: false for a tile flagged 0x20 in the level-1 grid.</summary>
    public bool InsideLot(int x, int y) => grids[0] is not { } ground || (ground.Element(x, y)[0] & 0x20) == 0;

    /// <summary>FUN_004893e4 bit 0 for the tile's level.</summary>
    public bool Buildable(int x, int y, int level) =>
        level is < 1 or > 2 || grids[level - 1] is not { } grid || (grid.Element(x, y)[0] & 1) != 0;
}
