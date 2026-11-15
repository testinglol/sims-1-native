using System.Numerics;

namespace SimsEngine.Formats;

/// <summary>One triangle: three indexes into <see cref="Mesh.Vertices"/>.</summary>
public readonly record struct MeshFace(int A, int B, int C);

/// <summary>The vertices one bone owns. <see cref="FirstVertex"/> and <see cref="VertexCount"/> index the real vertices,
/// which are the first <see cref="Mesh.TextureCoordinates"/>.Count entries of <see cref="Mesh.Vertices"/>.
/// <see cref="FirstBlend"/> (-1 when there are none) and <see cref="BlendCount"/> index <see cref="Mesh.Blends"/>.</summary>
public readonly record struct MeshBinding(int Bone, int FirstVertex, int VertexCount, int FirstBlend, int BlendCount);

/// <summary>A blend: the real vertex it copies (<see cref="Vertex"/>) and the weight of the copy in 1/32768ths. The text form
/// writes the vertex first; the binary form writes the weight first, as Sims.exe's writer FUN_005996e6 does (the first value
/// is the struct's first field, the weight, and the installed BMF files hold the weight in that place).</summary>
public readonly record struct MeshBlend(int Vertex, int Weight);

/// <summary>A vertex's position and normal, in the space of the bone it belongs to.</summary>
public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal);

/// <summary>A mesh (<c>.skn</c> text, <c>.bmf</c> binary): its name, texture, bone names, faces, bindings, texture
/// coordinates, blends and vertices. Sims.exe's writer FUN_0059ac03 lists them in that order; the text and binary forms
/// hold the same values.</summary>
public sealed record Mesh(string Name, string Texture, IReadOnlyList<string> Bones, IReadOnlyList<MeshFace> Faces,
    IReadOnlyList<MeshBinding> Bindings, IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<MeshBlend> Blends,
    IReadOnlyList<MeshVertex> Vertices)
{
    /// <summary>The bytes of a text mesh (SKN).</summary>
    public static Mesh ReadText(ReadOnlyMemory<byte> data, string source) => Read(CharacterCursor.Text(data, source));

    /// <summary>The bytes of a binary mesh (BMF).</summary>
    public static Mesh ReadBinary(ReadOnlyMemory<byte> data, string source) => Read(CharacterCursor.Binary(data, source));

    private static Mesh Read(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var texture = cursor.Text();
        var bones = new string[cursor.Count(1)];
        for (var index = 0; index < bones.Length; index++) bones[index] = cursor.Text();
        var faces = new MeshFace[cursor.Count(12)];
        for (var index = 0; index < faces.Length; index++) faces[index] = new MeshFace(cursor.Int32(), cursor.Int32(), cursor.Int32());
        var bindings = new MeshBinding[cursor.Count(20)];
        for (var index = 0; index < bindings.Length; index++)
            bindings[index] = new MeshBinding(cursor.Int32(), cursor.Int32(), cursor.Int32(), cursor.Int32(), cursor.Int32());
        var coordinates = new Vector2[cursor.Count(8)];
        for (var index = 0; index < coordinates.Length; index++) coordinates[index] = new Vector2(cursor.Float32(), cursor.Float32());
        var blends = new MeshBlend[cursor.Count(8)];
        for (var index = 0; index < blends.Length; index++)
        {
            // Text: vertex, then weight. Binary: weight, then vertex (see MeshBlend).
            var first = cursor.Int32();
            var second = cursor.Int32();
            blends[index] = cursor.IsText ? new MeshBlend(first, second) : new MeshBlend(second, first);
        }
        var vertices = new MeshVertex[cursor.Count(24)];
        for (var index = 0; index < vertices.Length; index++)
        {
            var position = new Vector3(cursor.Float32(), cursor.Float32(), cursor.Float32());
            var normal = new Vector3(cursor.Float32(), cursor.Float32(), cursor.Float32());
            vertices[index] = new MeshVertex(position, normal);
        }
        cursor.ExpectEnd();
        Validate(cursor, bones.Length, faces, bindings, coordinates.Length, blends, vertices.Length);
        return new Mesh(name, texture, bones, faces, bindings, coordinates, blends, vertices);
    }

    /// <summary>Indexes that name ranges must stay inside them. Sims.exe trusts them; here a bad range is an error. A binding
    /// with no real vertices may carry -1 as its first index, and one with no blends may carry -1 as its first blend (the
    /// installed meshes do both). Every installed blend copies a real vertex.</summary>
    private static void Validate(CharacterCursor cursor, int bones, MeshFace[] faces, MeshBinding[] bindings, int realVertices,
        MeshBlend[] blends, int vertices)
    {
        foreach (var face in faces)
            if (face.A < 0 || face.B < 0 || face.C < 0 || Math.Max(face.A, Math.Max(face.B, face.C)) >= vertices)
                throw cursor.Error($"face ({face.A}, {face.B}, {face.C}) names a vertex outside the {vertices} vertices");
        foreach (var binding in bindings)
        {
            if (binding.Bone < 0 || binding.Bone >= bones) throw cursor.Error($"binding names bone {binding.Bone} of {bones}");
            if (binding.VertexCount < 0 || (binding.VertexCount > 0 && binding.FirstVertex < 0)
                || binding.FirstVertex + binding.VertexCount > realVertices)
                throw cursor.Error($"binding of bone {binding.Bone} names real vertices {binding.FirstVertex}+{binding.VertexCount} of {realVertices}");
            if (binding.BlendCount < 0 || (binding.BlendCount > 0 && binding.FirstBlend < 0)
                || binding.FirstBlend + binding.BlendCount > blends.Length)
                throw cursor.Error($"binding of bone {binding.Bone} names blends {binding.FirstBlend}+{binding.BlendCount} of {blends.Length}");
        }
        foreach (var blend in blends)
            if (blend.Vertex < 0 || blend.Vertex >= realVertices)
                throw cursor.Error($"blend copies vertex {blend.Vertex} of {realVertices} real vertices");
    }
}
