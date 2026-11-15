using System.Numerics;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>SKN (text) and BMF (binary) meshes in the order Sims.exe's writer FUN_0059ac03 lists them, on hand-built data.</summary>
public class MeshFileTests
{
    /// <summary>Two bones. Bone 0 owns real vertices 0 and 1 and has one blend copy of vertex 2; bone 1 owns vertex 2. The
    /// mesh has two triangles over the three real vertices and the one blend copy (vertex 3).</summary>
    private static byte[] Sample(bool text)
    {
        var writer = new CharacterWriter(text);
        writer.Str("xskin-test-PELVIS-BODY").Str("test-texture");
        writer.Int(2).Str("PELVIS").Str("SPINE");
        writer.Int(2).Ints(0, 1, 2).Ints(1, 2, 3);
        writer.Int(2).Ints(0, 0, 2, 0, 1).Ints(1, 2, 1, -1, 0);
        writer.Int(3).Floats(0, 0).Floats(1, 0).Floats(0, 1);
        // Text writes the vertex, then the weight; binary writes the weight, then the vertex.
        writer.Int(1);
        if (text) writer.Ints(2, 16384);
        else writer.Ints(16384, 2);
        writer.Int(4);
        writer.Floats(0, 0, 0, 0, 0, 1).Floats(1, 0, 0, 0, 0, 1).Floats(0, 1, 0, 0, 0, 1).Floats(0.5f, 0.5f, 0, 0, 0, 1);
        return writer.ToArray();
    }

    [Fact]
    public void TextAndBinaryMeshesHoldTheSameValues()
    {
        var text = Mesh.ReadText(Sample(true), "test.skn");
        var binary = Mesh.ReadBinary(Sample(false), "test.bmf");
        foreach (var mesh in new[] { text, binary })
        {
            Assert.Equal("xskin-test-PELVIS-BODY", mesh.Name);
            Assert.Equal("test-texture", mesh.Texture);
            Assert.Equal(["PELVIS", "SPINE"], mesh.Bones);
            Assert.Equal([new MeshFace(0, 1, 2), new MeshFace(1, 2, 3)], mesh.Faces);
            Assert.Equal([new MeshBinding(0, 0, 2, 0, 1), new MeshBinding(1, 2, 1, -1, 0)], mesh.Bindings);
            Assert.Equal([new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)], mesh.TextureCoordinates);
            Assert.Equal([new MeshBlend(2, 16384)], mesh.Blends);
            Assert.Equal(4, mesh.Vertices.Count);
            Assert.Equal(new MeshVertex(new Vector3(0.5f, 0.5f, 0), new Vector3(0, 0, 1)), mesh.Vertices[3]);
        }
    }

    [Fact]
    public void FaceIndexOutsideTheVerticesIsRejected()
    {
        var writer = new CharacterWriter(false).Str("n").Str("t").Int(1).Str("b").Int(1).Ints(0, 1, 9).Int(0).Int(0).Int(0).Int(1).Floats(0, 0, 0, 0, 0, 1);
        var error = Assert.Throws<ResourceFormatException>(() => Mesh.ReadBinary(writer.ToArray(), "face.bmf"));
        Assert.Contains("outside", error.Message);
    }

    [Fact]
    public void BindingNamingAMissingBoneIsRejected()
    {
        var writer = new CharacterWriter(false).Str("n").Str("t").Int(1).Str("only").Int(0).Int(1).Ints(5, 0, 0, -1, 0).Int(0).Int(0).Int(0);
        var error = Assert.Throws<ResourceFormatException>(() => Mesh.ReadBinary(writer.ToArray(), "bone.bmf"));
        Assert.Contains("bone 5", error.Message);
    }

    [Fact]
    public void BinaryBlendIsWeightThenVertexAndBindingsWithNoVerticesMayBeMinusOne()
    {
        // One real vertex (uv count 1). Bone 0 has no real vertices (first index -1) and one blend; the blend is written
        // weight first (16384), then its vertex (0).
        var writer = new CharacterWriter(false).Str("n").Str("t").Int(1).Str("only").Int(0).Int(1).Ints(0, -1, 0, 0, 1)
            .Int(1).Floats(0, 0).Int(1).Ints(16384, 0).Int(1).Floats(0, 0, 0, 0, 0, 1);
        var mesh = Mesh.ReadBinary(writer.ToArray(), "blend.bmf");
        Assert.Equal([new MeshBlend(0, 16384)], mesh.Blends);
        Assert.Equal(new MeshBinding(0, -1, 0, 0, 1), mesh.Bindings.Single());
    }

    [Fact]
    public void BlendCopyingAVertexThatIsNotRealIsRejected()
    {
        // The vertex is the second value in binary (7), past the one real vertex.
        var writer = new CharacterWriter(false).Str("n").Str("t").Int(1).Str("only").Int(0).Int(1).Ints(0, 0, 1, 0, 1)
            .Int(1).Floats(0, 0).Int(1).Ints(16384, 7).Int(1).Floats(0, 0, 0, 0, 0, 1);
        var error = Assert.Throws<ResourceFormatException>(() => Mesh.ReadBinary(writer.ToArray(), "blend.bmf"));
        Assert.Contains("real vertices", error.Message);
    }

    [Fact]
    public void BindingNamingAMissingBlendRangeIsRejected()
    {
        var writer = new CharacterWriter(false).Str("n").Str("t").Int(1).Str("only").Int(0).Int(1).Ints(0, 0, 0, 0, 2).Int(0).Int(0).Int(0);
        var error = Assert.Throws<ResourceFormatException>(() => Mesh.ReadBinary(writer.ToArray(), "blendrange.bmf"));
        Assert.Contains("blends", error.Message);
    }

    [Fact]
    public void TextMeshTruncatedInTheVerticesReportsALine()
    {
        var data = Sample(true);
        var error = Assert.Throws<ResourceFormatException>(() => Mesh.ReadText(data[..(data.Length - 20)], "short.skn"));
        Assert.Equal("short.skn", error.File);
    }
}
