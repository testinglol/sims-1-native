namespace SimsEngine.World;

/// <summary>One vertex of a roof resource mesh, in tile units. Height is vertical; X and Y are the horizontal axes.
/// UVs belong to the original style bitmap and are independent of the pitch.</summary>
public readonly record struct RoofMeshVertex(float X, float Height, float Y, float U, float V);

/// <summary>Sims.exe's roof resource provider (vtable 0x6714f0, dispatch 0x56f880). Resource numbers above 255
/// select FUN_0056f8ab, the others FUN_00570888. Both construct triangles with FUN_005c5a00; these are meshes,
/// not SPR/SPR2 resources. The source uses 3 world units per tile: side 1.5, midpoint .75, ordinary peak .75*pitch,
/// second-kind peak 1.5*pitch. The branch constants and UV seams below port those construction calls.</summary>
public static class RoofCellMesh
{
    /// <summary>FUN_0056eb75's cell-local transform, in tile units before terrain/story and camera projection.
    /// Rotation is about the .25-tile centre; FUN_00570d26 contributes .5*pitch for each ring of cell height.</summary>
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
            // FUN_004ca6b6 maps the grid row to model X and column to model Z. Swap the model's
            // horizontal axes when returning native lot X/Y coordinates.
            return vertex with { X = column * 0.5f + y, Y = row * 0.5f + x,
                Height = vertex.Height + grid.Height(row, column) * pitch * 0.5f };
        }).ToArray();
        // The axis swap changes handedness. Preserve upward-facing winding in the native coordinate system.
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
