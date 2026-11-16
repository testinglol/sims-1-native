namespace SimsEngine.World;

/// <summary>A roof mesh vertex in tile units, where Height is vertical and UV coordinates map to the style
/// texture independently of pitch.</summary>
public readonly record struct RoofMeshVertex(float X, float Height, float Y, float U, float V);

/// <summary>Generates procedural 3D triangle roof meshes scaled at 3 units per tile, with side 1.5,
/// midpoint 0.75, standard peaks at 0.75 * pitch, and secondary peaks at 1.5 * pitch.</summary>
public static class RoofCellMesh
{
    /// <summary>Transforms roof cell vertices in tile units, rotating around the 0.25-tile center and
    /// adding 0.5 * pitch for each ring of cell height.</summary>
    public static IReadOnlyList<RoofMeshVertex> Build(RoofGrid grid, int row, int column, float pitch)
    {
        if (!grid.Has(row, column)) return [];
        var (resource, turns) = grid.MeshResource(row, column);
        var vertices = Build(resource, pitch).Select(vertex =>
        {
            var (x, y) = (turns & 3) switch
            {
                1 => (vertex.Y, 0.5f - vertex.X),
                2 => (0.5f - vertex.X, 0.5f - vertex.Y),
                3 => (0.5f - vertex.Y, vertex.X),
                _ => (vertex.X, vertex.Y),
            };
            // Maps roof grid row to lot Y and column to lot X at half-tile intervals.
            return vertex with { X = column * 0.5f + y, Y = row * 0.5f + x,
                Height = vertex.Height + grid.Height(row, column) * pitch * 0.5f };
        }).ToArray();
        // Preserves upward-facing triangle winding order after the axis swap.
        for (var i = 0; i < vertices.Length; i += 3) (vertices[i + 1], vertices[i + 2]) = (vertices[i + 2], vertices[i + 1]);
        return vertices;
    }

    public static IReadOnlyList<RoofMeshVertex> Build(int resource, float pitch)
    {
        if (!float.IsFinite(pitch) || pitch < 0 || pitch > LotRoof.MaxPitch)
            throw new ArgumentOutOfRangeException(nameof(pitch));
        if (resource == 255) return [];
        var vertices = resource switch
        {
            256 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 1, 1), new(0, 0, 0.5f, 1, 0), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0, 0, 0, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0, 1, 1),
                new(0.5f, 0, 0, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
                new(0, 0, 0.5f, 1, 1), new(0.5f, 0, 0.5f, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
            },
            264 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 0, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0, 0, 0, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0, 1, 1),
                new(0.5f, 0, 0, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
                new(0, 0, 0.5f, 1, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0, 0.25f, 0.25f, 1, 0.5f),
                new(0, 0, 0.5f, 1, 1), new(0.5f, 0, 0.5f, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
            },
            265 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 0, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.5f, 0.25f, 0.25f, 1, 0.5f),
                new(0, 0, 0, 0, 1), new(0.5f, 0.25f, 0.25f, 1, 0.5f), new(0.5f, 0, 0, 1, 1),
                new(0, 0.25f, 0.25f, 1, 0.5f), new(0, 0, 0.5f, 1, 1), new(0.5f, 0.25f, 0.25f, 0, 0.5f),
                new(0, 0, 0.5f, 1, 1), new(0.5f, 0, 0.5f, 0, 1), new(0.5f, 0.25f, 0.25f, 0, 0.5f),
            },
            266 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 0, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.5f, 0, 0, 1, 1),
                new(0.5f, 0, 0, 1, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0.5f, 0, 0, 0, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0, 0.25f, 0.25f, 1, 0.5f), new(0, 0, 0.5f, 1, 1),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0, 0, 0.5f, 0, 1), new(0.25f, 0.25f, 0.5f, 0, 0.5f),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.25f, 0.25f, 0.5f, 1, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
            },
            270 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 1, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.25f, 0.25f, 0, 1, 0.5f),
                new(0, 0, 0, 0, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0, 0.25f, 0.25f, 1, 0.5f), new(0, 0, 0.5f, 1, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0, 0, 0.5f, 0, 1), new(0.25f, 0.25f, 0.5f, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0.25f, 0.25f, 0, 0, 0.5f), new(0.25f, 0.25f, 0.5f, 1, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
                new(0.25f, 0.25f, 0, 0, 0.5f), new(0.5f, 0, 0.5f, 1, 1), new(0.5f, 0, 0, 0, 1),
            },
            271 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 0, 1), new(0, 0.25f, 0.25f, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0, 0.25f, 0.25f, 1, 0.5f), new(0, 0, 0.5f, 1, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0, 0, 0.5f, 0, 1), new(0.25f, 0.25f, 0.5f, 0, 0.5f),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.25f, 0.25f, 0.5f, 1, 0.5f), new(0.5f, 0, 0.5f, 1, 1),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0.5f, 0, 1), new(0.5f, 0.25f, 0.25f, 0, 0.5f),
                new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0.25f, 0.25f, 1, 0.5f), new(0.5f, 0, 0, 1, 1),
                new(0.25f, 0.25f, 0, 0, 0.5f), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.5f, 0, 0, 0, 1),
                new(0, 0, 0, 1, 1), new(0.25f, 0.25f, 0.25f, 0.5f, 0.5f), new(0.25f, 0.25f, 0, 1, 0.5f),
            },
            3 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 1, 1), new(0.5f, 0.5f, 0.5f, 0, 0), new(0.5f, 0.5f, 0, 1, 0),
                new(0, 0, 0, 1, 1), new(0, 0, 0.5f, 0, 1), new(0.5f, 0.5f, 0.5f, 0, 0),
            },
            4 => new RoofMeshVertex[]
            {
                new(0, 0, 0, 0, 1), new(0, 0.5f, 0.5f, 0, 0), new(0.5f, 0, 0, 1, 1),
                new(0.5f, 0, 0, 0, 1), new(0, 0.5f, 0.5f, 1, 0), new(0.5f, 0, 0.5f, 1, 1),
            },
            9 => new RoofMeshVertex[]
            {
                new(0, 0.5f, 0, 0, 1), new(0, 0, 0.5f, 1, 0.5f), new(0.5f, 0.5f, 0.5f, 0, 0),
                new(0, 0.5f, 0, 0, 0), new(0.5f, 0.5f, 0.5f, 0, 1), new(0.5f, 0, 0, 1, 0.5f),
            },
            13 => new RoofMeshVertex[]
            {
                new(0, 0.5f, 0, 0, 0), new(0, 0.5f, 0.5f, 1, 0), new(0.5f, 0, 0, 0, 1),
                new(0, 0.5f, 0.5f, 0, 0), new(0.5f, 0.5f, 0.5f, 1, 0), new(0.5f, 0, 0, 1, 1),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(resource), "Unknown roof mesh resource"),
        };
        for (var i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] with { Height = vertices[i].Height * pitch };
        return vertices;
    }
}
