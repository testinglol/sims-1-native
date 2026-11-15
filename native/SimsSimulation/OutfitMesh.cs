using System.Globalization;
using System.Numerics;
using System.Text;
using SimsEngine.Character;
using SimsEngine.Formats;

/// <summary>
/// Body meshes for authored outfits. A TS1 mesh (<c>.skn</c>, plain-text BMF) stores each vertex in the space of the one bone
/// it follows, grouped by bone, plus "blend" copies of some vertices in a second bone's space with a weight, which bend
/// joints. Export puts the mesh in the skeleton's rest pose as a Wavefront OBJ. Import re-skins an edited OBJ: every vertex
/// takes the bone and blend of the nearest original vertex and is converted back into those bones' spaces, so the rest pose
/// reproduces the OBJ exactly and the new shape follows the skeleton the way the nearest part of the original did.
/// </summary>
internal static class OutfitMesh
{
    private const int MaximumVertices = 30_000;
    private const int MaximumFaces = 60_000;

    /// <summary>A mesh vertex in the rest pose: position, normal and texture coordinate, with its bones.</summary>
    private readonly record struct Skinned(Vector3 Position, Vector3 Normal, Vector2 UV, int Bone, int BlendBone, float Weight);

    internal static Mesh Read(byte[] data)
    {
        Mesh mesh;
        try
        {
            mesh = Mesh.ReadText(data, "mesh.skn");
        }
        catch (ResourceFormatException error)
        {
            throw new InvalidDataException(error.Message, error);
        }
        if (mesh.Bindings.Count == 0 || mesh.TextureCoordinates.Count == 0 || mesh.Vertices.Count == 0)
            throw new InvalidDataException("The mesh has no vertices");
        return mesh;
    }

    /// <summary>The mesh's bone names, by binding.</summary>
    private static string[] Bones(Mesh mesh) => mesh.Bindings.Select(binding => mesh.Bones[binding.Bone]).ToArray();

    private static Matrix4x4[] BoneMatrices(string[] names, Skeleton skeleton)
    {
        var pose = Pose.Sample(skeleton, new Animation("bind", "", [], 0, 0, 0, []), new AnimationCurves([], []), 0);
        var byName = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        foreach (var bone in pose)
        {
            var matrix = Matrix4x4.CreateFromQuaternion(Pose.BoneMatrixRotation(bone.WorldRotation));
            matrix.M41 = bone.WorldTranslation.X;
            matrix.M42 = bone.WorldTranslation.Y;
            matrix.M43 = bone.WorldTranslation.Z;
            byName.TryAdd(bone.Name, matrix);
        }
        return names.Select(name => byName.TryGetValue(name, out var matrix)
            ? matrix
            : throw new InvalidDataException($"Skeleton {skeleton.Name} has no bone {name}")).ToArray();
    }

    /// <summary>Every vertex in the rest pose of <paramref name="skeleton"/>, blended the way the renderer blends it.</summary>
    private static Skinned[] RestPose(Mesh mesh, Skeleton skeleton)
    {
        var bones = BoneMatrices(Bones(mesh), skeleton);
        var realCount = mesh.TextureCoordinates.Count;
        var vertexBone = new int[realCount];
        var blendBone = new int[mesh.Blends.Count];
        for (var index = 0; index < mesh.Bindings.Count; index++)
        {
            var binding = mesh.Bindings[index];
            for (var vertex = 0; vertex < binding.VertexCount; vertex++) vertexBone[binding.FirstVertex + vertex] = index;
            for (var blend = 0; blend < binding.BlendCount; blend++) blendBone[binding.FirstBlend + blend] = index;
        }
        var result = new Skinned[realCount];
        for (var index = 0; index < result.Length; index++)
        {
            var vertex = mesh.Vertices[index];
            var normal = vertex.Normal == Vector3.Zero ? Vector3.UnitY : vertex.Normal;
            var matrix = bones[vertexBone[index]];
            result[index] = new Skinned(Vector3.Transform(vertex.Position, matrix), Vector3.TransformNormal(normal, matrix),
                                        mesh.TextureCoordinates[index], vertexBone[index], -1, 0);
        }
        for (var blend = 0; blend < mesh.Blends.Count; blend++)
        {
            var data = mesh.Blends[blend];
            if (data.Vertex < 0 || data.Vertex >= result.Length) throw new InvalidDataException("The mesh blends a vertex it does not have");
            var weight = data.Weight / 32768f;
            var matrix = bones[blendBone[blend]];
            var blendVertex = mesh.Vertices[realCount + blend];
            var blendNormal = blendVertex.Normal == Vector3.Zero ? Vector3.UnitY : blendVertex.Normal;
            var vertex = result[data.Vertex];
            result[data.Vertex] = vertex with
            {
                Position = Vector3.Lerp(vertex.Position, Vector3.Transform(blendVertex.Position, matrix), weight),
                Normal = Vector3.Lerp(vertex.Normal, Vector3.TransformNormal(blendNormal, matrix), weight),
                BlendBone = blendBone[blend], Weight = weight
            };
        }
        return result;
    }

    /// <summary>The mesh as a Wavefront OBJ in the rest pose. Texture V is flipped to OBJ's bottom-up convention.</summary>
    internal static string ExportObj(Mesh mesh, Skeleton skeleton, string name)
    {
        var vertices = RestPose(mesh, skeleton);
        var text = new StringBuilder();
        text.Append("# ").Append(name).Append(", rest pose of skeleton ").Append(skeleton.Name).Append('\n');
        text.Append("o ").Append(name).Append('\n');
        foreach (var vertex in vertices) text.Append(Invariant($"v {vertex.Position.X:R} {vertex.Position.Y:R} {vertex.Position.Z:R}\n"));
        foreach (var vertex in vertices) text.Append(Invariant($"vt {vertex.UV.X:R} {1 - vertex.UV.Y:R}\n"));
        foreach (var vertex in vertices)
        {
            var normal = vertex.Normal.LengthSquared() > 0 ? Vector3.Normalize(vertex.Normal) : Vector3.UnitY;
            text.Append(Invariant($"vn {normal.X:R} {normal.Y:R} {normal.Z:R}\n"));
        }
        foreach (var face in mesh.Faces)
        {
            text.Append('f');
            foreach (var corner in new[] { face.A, face.B, face.C })
            {
                var index = corner + 1;
                text.Append(' ').Append(index).Append('/').Append(index).Append('/').Append(index);
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A parsed OBJ: one vertex per distinct position/texture/normal corner, and triangles over them.</summary>
    private sealed record Obj(Vector3[] Positions, Vector2[] UVs, Vector3[] Normals, int[] Triangles);

    private static Obj ParseObj(string text, string what)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var corners = new Dictionary<(int, int, int), int>();
        var outPositions = new List<Vector3>();
        var outUVs = new List<Vector2>();
        var outNormals = new List<int>();
        var triangles = new List<int>();
        var lineNumber = 0;
        float Number(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number) || MathF.Abs(number) > 1000)
                throw new InvalidDataException($"{what} line {lineNumber}: {value} is not a coordinate");
            return number;
        }
        int Reference(string value, int count, string kind)
        {
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) || index == 0)
                throw new InvalidDataException($"{what} line {lineNumber}: {value} is not a {kind} index");
            var resolved = index > 0 ? index - 1 : count + index;
            if (resolved < 0 || resolved >= count) throw new InvalidDataException($"{what} line {lineNumber}: {kind} {value} does not exist");
            return resolved;
        }
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "v":
                    if (parts.Length < 4) throw new InvalidDataException($"{what} line {lineNumber}: a vertex needs x, y and z");
                    positions.Add(new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "vt":
                    if (parts.Length < 3) throw new InvalidDataException($"{what} line {lineNumber}: a texture coordinate needs u and v");
                    uvs.Add(new Vector2(Number(parts[1]), 1 - Number(parts[2])));
                    break;
                case "vn":
                    if (parts.Length < 4) throw new InvalidDataException($"{what} line {lineNumber}: a normal needs x, y and z");
                    normals.Add(new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "f":
                    if (parts.Length < 4) throw new InvalidDataException($"{what} line {lineNumber}: a face needs at least three corners");
                    var face = new int[parts.Length - 1];
                    for (var corner = 0; corner < face.Length; corner++)
                    {
                        var fields = parts[corner + 1].Split('/');
                        if (fields.Length < 2 || fields[1].Length == 0)
                            throw new InvalidDataException($"{what} line {lineNumber}: every corner needs a texture coordinate (v/vt or v/vt/vn)");
                        var key = (Reference(fields[0], positions.Count, "vertex"), Reference(fields[1], uvs.Count, "texture coordinate"),
                                   fields.Length > 2 && fields[2].Length > 0 ? Reference(fields[2], normals.Count, "normal") : -1);
                        if (!corners.TryGetValue(key, out var vertex))
                        {
                            vertex = outPositions.Count;
                            if (vertex >= MaximumVertices) throw new InvalidDataException($"{what} has more than {MaximumVertices} distinct vertices");
                            corners.Add(key, vertex);
                            outPositions.Add(positions[key.Item1]);
                            outUVs.Add(uvs[key.Item2]);
                            outNormals.Add(key.Item3);
                        }
                        face[corner] = vertex;
                    }
                    // Polygons become a fan of triangles.
                    for (var corner = 1; corner + 1 < face.Length; corner++)
                    {
                        triangles.Add(face[0]); triangles.Add(face[corner]); triangles.Add(face[corner + 1]);
                        if (triangles.Count / 3 > MaximumFaces) throw new InvalidDataException($"{what} has more than {MaximumFaces} triangles");
                    }
                    break;
                case "o" or "g" or "s" or "mtllib" or "usemtl" or "l":
                    break;
                default:
                    throw new InvalidDataException($"{what} line {lineNumber}: {parts[0]} is not supported");
            }
        }
        if (triangles.Count == 0) throw new InvalidDataException($"{what} has no faces");

        // Corners without a normal take the area-weighted normal of the faces at their position.
        var smooth = new Dictionary<Vector3, Vector3>();
        for (var index = 0; index < triangles.Count; index += 3)
        {
            var (a, b, c) = (outPositions[triangles[index]], outPositions[triangles[index + 1]], outPositions[triangles[index + 2]]);
            var normal = Vector3.Cross(b - a, c - a);
            foreach (var position in new[] { a, b, c }) smooth[position] = smooth.GetValueOrDefault(position) + normal;
        }
        var resolvedNormals = outNormals.Select((normal, index) =>
        {
            var value = normal >= 0 ? normals[normal] : smooth.GetValueOrDefault(outPositions[index]);
            return value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.UnitY;
        }).ToArray();
        return new Obj(outPositions.ToArray(), outUVs.ToArray(), resolvedNormals, triangles.ToArray());
    }

    /// <summary>
    /// Builds a mesh from an edited OBJ, skinned like <paramref name="template"/>. The result binds the template's bones in the
    /// template's order. Reports how far the farthest vertex is from the original surface it took its bones from.
    /// </summary>
    internal static (Mesh Mesh, float Farthest) Import(string objText, string what, Mesh template, Skeleton skeleton, string skinName, string textureName)
    {
        var obj = ParseObj(objText, what);
        var original = RestPose(template, skeleton);
        var boneNames = Bones(template);
        var bones = BoneMatrices(boneNames, skeleton);
        var inverse = bones.Select(matrix => Matrix4x4.Invert(matrix, out var inverted)
            ? inverted
            : throw new InvalidDataException("Singular bone matrix")).ToArray();

        // Nearest original vertex, preferring one with the same texture coordinate where several share a position.
        var farthest = 0f;
        var skinned = new Skinned[obj.Positions.Length];
        for (var index = 0; index < skinned.Length; index++)
        {
            var position = obj.Positions[index];
            var best = 0;
            var bestScore = float.MaxValue;
            for (var candidate = 0; candidate < original.Length; candidate++)
            {
                var score = Vector3.DistanceSquared(position, original[candidate].Position) + 1e-6f * Vector2.DistanceSquared(obj.UVs[index], original[candidate].UV);
                if (score < bestScore) (best, bestScore) = (candidate, score);
            }
            farthest = MathF.Max(farthest, Vector3.Distance(position, original[best].Position));
            skinned[index] = new Skinned(position, obj.Normals[index], obj.UVs[index], original[best].Bone, original[best].BlendBone, original[best].Weight);
        }

        // Vertices are stored grouped by bone, and blend copies grouped by their second bone.
        var order = Enumerable.Range(0, skinned.Length).OrderBy(index => skinned[index].Bone).ThenBy(index => index).ToArray();
        var newIndex = new int[skinned.Length];
        for (var index = 0; index < order.Length; index++) newIndex[order[index]] = index;
        var blends = order.Where(index => skinned[index].BlendBone >= 0 && skinned[index].Weight > 0)
            .OrderBy(index => skinned[index].BlendBone).ThenBy(index => newIndex[index]).ToArray();

        var faces = new MeshFace[obj.Triangles.Length / 3];
        for (var index = 0; index < faces.Length; index++)
            faces[index] = new MeshFace(newIndex[obj.Triangles[index * 3]], newIndex[obj.Triangles[index * 3 + 1]], newIndex[obj.Triangles[index * 3 + 2]]);

        var bindings = boneNames.Select((_, bone) =>
        {
            var first = Array.FindIndex(order, index => skinned[index].Bone == bone);
            var count = order.Count(index => skinned[index].Bone == bone);
            var firstBlend = Array.FindIndex(blends, index => skinned[index].BlendBone == bone);
            var blendCount = blends.Count(index => skinned[index].BlendBone == bone);
            return new MeshBinding(bone, first < 0 ? 0 : first, count, blendCount == 0 ? -1 : firstBlend, blendCount);
        }).ToArray();

        var coordinates = order.Select(index => skinned[index].UV).ToArray();
        var meshBlends = blends.Select(index => new MeshBlend(newIndex[index], (int)MathF.Round(skinned[index].Weight * 0x8000))).ToArray();
        var vertices = new MeshVertex[order.Length + blends.Length];
        for (var index = 0; index < order.Length; index++)
        {
            var vertex = skinned[order[index]];
            var normal = Vector3.TransformNormal(vertex.Normal, inverse[vertex.Bone]);
            vertices[index] = new MeshVertex(
                Vector3.Transform(vertex.Position, inverse[vertex.Bone]),
                normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY);
        }
        for (var index = 0; index < blends.Length; index++)
        {
            var vertex = skinned[blends[index]];
            var normal = Vector3.TransformNormal(vertex.Normal, inverse[vertex.BlendBone]);
            vertices[order.Length + index] = new MeshVertex(
                Vector3.Transform(vertex.Position, inverse[vertex.BlendBone]),
                normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY);
        }

        var mesh = new Mesh(skinName, textureName, boneNames, faces, bindings, coordinates, meshBlends, vertices);
        return (mesh, farthest);
    }

    /// <summary>Writes a mesh as TS1 plain-text BMF (<c>.skn</c>), in the order <see cref="Mesh.ReadText"/> reads it.</summary>
    internal static byte[] Write(Mesh mesh)
    {
        var text = new StringBuilder();
        void Line(FormattableString value) => text.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(mesh.Name).Append('\n').Append(mesh.Texture).Append('\n');
        Line($"{mesh.Bones.Count}");
        foreach (var bone in mesh.Bones) text.Append(bone).Append('\n');
        Line($"{mesh.Faces.Count}");
        foreach (var face in mesh.Faces) Line($"{face.A} {face.B} {face.C}");
        Line($"{mesh.Bindings.Count}");
        foreach (var binding in mesh.Bindings)
            Line($"{binding.Bone} {binding.FirstVertex} {binding.VertexCount} {(binding.BlendCount == 0 ? -1 : binding.FirstBlend)} {binding.BlendCount}");
        Line($"{mesh.TextureCoordinates.Count}");
        foreach (var uv in mesh.TextureCoordinates) Line($"{uv.X:R} {uv.Y:R}");
        Line($"{mesh.Blends.Count}");
        // Plain-text BMF lists the blended vertex first, then the weight in 1/32768ths.
        foreach (var blend in mesh.Blends) Line($"{blend.Vertex} {blend.Weight}");
        Line($"{mesh.Vertices.Count}");
        foreach (var vertex in mesh.Vertices)
            Line($"{vertex.Position.X:R} {vertex.Position.Y:R} {vertex.Position.Z:R} {vertex.Normal.X:R} {vertex.Normal.Y:R} {vertex.Normal.Z:R}");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
