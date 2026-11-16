namespace SimsEngine.World;

/// <summary>Manages the roof cell grid. Each cell occupies two bytes: byte 0 stores roofed (bit 0),
/// secondary kind (bit 1), grown (bit 2), never-roof (bit 3), and shape index (bits 4-7); byte 1 stores
/// height.</summary>
public sealed class RoofGrid(int size)
{
    private const byte Roofed = 1, Second = 2, Grown = 4, Blocked = 8;
    private static readonly int[] Dx = [-1, 0, 1, 0, -1, 1, 1, -1];
    private static readonly int[] Dy = [0, 1, 0, -1, -1, -1, 1, 1];
    private static readonly int[] KindMasks = [0x2c, 0x46, 0x83, 0x19, 0x6e, 0xc7, 0x9b, 0x3d];
    private static readonly int[] ShapeDx = [0, 1, -1, 0, -1, 1, 1, -1];
    private static readonly int[] ShapeDy = [1, 0, 0, -1, -1, -1, 1, 1];
    private static readonly int[] ShapeMasks = [0xc, 10, 5, 3, 7, 0xb, 0xe, 0xd];

    // Maps each shape index to a mesh resource ID and a quarter-turn rotation count.
    private static readonly byte[] PlainResources = [0, 0, 8, 2, 8, 1, 10, 1, 8, 3, 10, 2, 9, 1, 14, 2, 8, 0, 9, 0, 10, 0, 14, 1, 10, 3, 14, 3, 14, 0, 15, 0];
    private static readonly byte[] SecondResources = [255, 255, 4, 1, 4, 2, 3, 0, 4, 0, 3, 3, 9, 1, 13, 1, 4, 3, 9, 0, 3, 1, 13, 2, 3, 2, 13, 0, 13, 3, 255, 255];

    private readonly byte[] flags = new byte[size * size];
    private readonly byte[] heights = new byte[size * size];

    public int Size => size;

    public byte Flags(int row, int column) => flags[row * size + column];

    public int Height(int row, int column) => heights[row * size + column];

    public bool Has(int row, int column) => (Flags(row, column) & Roofed) != 0;

    public int Shape(int row, int column) => Flags(row, column) >> 4;

    /// <summary>Resolves the mesh resource ID and quarter turns for a cell: standard cells add 0x100 to the
    /// table entry, while secondary cells use the raw entry (255 indicates none).</summary>
    public (int Id, int QuarterTurns) MeshResource(int row, int column)
    {
        var flagsValue = Flags(row, column);
        var shape = flagsValue >> 4;
        var second = (flagsValue & Second) != 0;
        var table = second ? SecondResources : PlainResources;
        var id = table[shape * 2];
        return (second ? id : 0x100 + id, table[shape * 2 + 1]);
    }

    /// <summary>Identifies a secondary roof cell, characterized by having a taller neighbor or a corner
    /// pattern of equal-height neighbors.</summary>
    public bool IsSecondKind(int row, int column) => (Flags(row, column) & Second) != 0;

    public void Seed(int row, int column, bool roofed, bool blocked)
    {
        var index = row * size + column;
        flags[index] = (byte)((flags[index] & ~(Roofed | Blocked)) | (roofed ? Roofed : 0) | (blocked ? Blocked : 0));
    }

    /// <summary>Executes the roof planning passes, optionally running growth against upper roofs and
    /// iteratively solving heights, secondary kinds, and shapes until stable or capped at nine iterations.
    /// </summary>
    public void Plan(bool grow = false, RoofGrid? upperRoof = null)
    {
        if (grow)
            for (var i = 0; i < flags.Length; i++)
                if ((flags[i] & Roofed) != 0 && (flags[i] & Grown) == 0) flags[i] &= unchecked((byte)~Roofed);
        if (upperRoof is not null)
            for (var i = 0; i < Math.Min(flags.Length, upperRoof.flags.Length); i++)
                if ((upperRoof.flags[i] & Roofed) != 0 && (upperRoof.flags[i] & Grown) == 0)
                    flags[i] &= unchecked((byte)~Roofed);
        Grow(upperRoof);
        for (var round = 0; round < 9; round++)
        {
            Heights();
            if (!Kinds()) continue;
            Shapes();
            break;
        }
    }

    public void Plan(RoofGrid? upperRoof) => Plan(false, upperRoof);

    private bool Open(int row, int column) => (flags[row * size + column] & Roofed) == 0 || (flags[row * size + column] & Grown) != 0;

    /// <summary>Expands roof coverage to unblocked adjacent cells whose neighbors have an original roof,
    /// without propagating from previously grown cells or extending beneath upper-level roofs.</summary>
    private void Grow(RoofGrid? upperRoof = null)
    {
        for (var row = 1; row < size - 1; row++)
        for (var column = 1; column < size - 1; column++)
        {
            var index = row * size + column;
            if ((flags[index] & Roofed) != 0 || (flags[index] & Blocked) != 0) continue;
            var adjacent = false;
            for (var i = 0; i < 8 && !adjacent; i++) adjacent = !Open(row + Dy[i], column + Dx[i]);
            if (!adjacent) continue;
            if (upperRoof is not null && index < upperRoof.flags.Length &&
                (upperRoof.flags[index] & Roofed) != 0 && (upperRoof.flags[index] & Grown) == 0) continue;
            flags[index] |= Roofed | Grown;
        }
    }

    /// <summary>Calculates roof height as the number of concentric square rings around a cell that are
    /// entirely roofed.</summary>
    private void Heights()
    {
        for (var row = 1; row < size - 1; row++)
        for (var column = 1; column < size - 1; column++)
        {
            if ((flags[row * size + column] & Roofed) == 0) continue;
            for (var ring = 1; ; ring++)
            {
                if (!RingRoofed(row, column, ring))
                {
                    heights[row * size + column] = (byte)(ring - 1);
                    break;
                }
            }
        }
    }

    private bool RingRoofed(int row, int column, int ring)
    {
        // Walks around the square ring perimeter clockwise starting from the top-left corner.
        int[] stepRow = [0, 1, 0, -1], stepColumn = [1, 0, -1, 0];
        int y = row - ring, x = column - ring;
        for (var side = 0; side < 4; side++)
        for (var step = 0; step < ring * 2; step++)
        {
            if (y < 0 || y >= size || x < 0 || x >= size || (flags[y * size + x] & Roofed) == 0) return false;
            y += stepRow[side];
            x += stepColumn[side];
        }
        return true;
    }

    /// <summary>Updates secondary roof cell flags based on neighbor heights, returning false if a grown
    /// cell was removed to trigger another iteration.</summary>
    private bool Kinds()
    {
        for (var row = 1; row < size - 1; row++)
        for (var column = 1; column < size - 1; column++)
        {
            var index = row * size + column;
            if ((flags[index] & Roofed) == 0) continue;
            var height = heights[index];
            var taller = false;
            var same = 0;
            for (var i = 0; i < 8; i++)
            {
                var neighbour = (row + Dy[i]) * size + column + Dx[i];
                if ((flags[neighbour] & Roofed) == 0) continue;
                if (heights[neighbour] == height) same |= 1 << i;
                else if (height < heights[neighbour])
                {
                    taller = true;
                    break;
                }
            }
            if (same != 0 && !taller) taller = KindMasks.Any(mask => (mask & same) == mask);
            if (taller) flags[index] |= Second;
            else if ((flags[index] & Grown) != 0) flags[index] &= unchecked((byte)~Roofed);
            else flags[index] &= unchecked((byte)~Second);
            if ((flags[index] & Roofed) == 0) return false;
        }
        return true;
    }

    private void Shapes()
    {
        for (var row = 1; row < size - 1; row++)
        for (var column = 1; column < size - 1; column++)
        {
            var index = row * size + column;
            if ((flags[index] & Roofed) == 0) continue;
            var height = heights[index];
            int mask;
            if ((flags[index] & Second) == 0)
            {
                mask = 0;
                for (var i = 0; i < 4; i++)
                {
                    var neighbour = (row + ShapeDy[i]) * size + column + ShapeDx[i];
                    if ((flags[neighbour] & Roofed) != 0 && (flags[neighbour] & Second) == 0 && heights[neighbour] == height)
                        mask |= 1 << i;
                }
            }
            else
            {
                mask = 0xf;
                for (var i = 0; i < 8; i++)
                {
                    var neighbour = (row + ShapeDy[i]) * size + column + ShapeDx[i];
                    if ((flags[neighbour] & Roofed) == 0 || heights[neighbour] < height) mask &= ShapeMasks[i];
                }
            }
            flags[index] = (byte)((flags[index] & 0x0f) | (mask << 4));
        }
    }
}
