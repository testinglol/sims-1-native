using System.Numerics;

namespace SimsEngine.Formats;

public readonly record struct MeshFace(int A, int B, int C);

/// <summary>Defines the vertex and blend ranges influenced by a bone, where base vertices index the primary
/// vertex array and blend indices reference weighted copies.</summary>
public readonly record struct MeshBinding(int Bone, int FirstVertex, int VertexCount, int FirstBlend, int BlendCount);

/// <summary>Represents a weighted vertex blend with weights normalized to 32768. In text (SKN) files the
/// vertex index precedes the weight, whereas binary (BMF) files store the weight first.</summary>
public readonly record struct MeshBlend(int Vertex, int Weight);

/// <summary>Represents a vertex position and normal expressed in bone-local space.</summary>
public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal);

public sealed record Mesh(string Name, string Texture, IReadOnlyList<string> Bones, IReadOnlyList<MeshFace> Faces,
    IReadOnlyList<MeshBinding> Bindings, IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<MeshBlend> Blends,
    IReadOnlyList<MeshVertex> Vertices)
{
    public static Mesh ReadText(ReadOnlyMemory<byte> data, string source) => Read(CharacterCursor.Text(data, source));

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
            // Text stores vertex then weight; binary stores weight then vertex.
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

    /// <summary>Validates mesh range indices, allowing empty vertex or blend ranges to specify -1 for their
    /// start index.</summary>
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
