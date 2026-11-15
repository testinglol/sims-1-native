using System.Numerics;

/// <summary>
/// Pure software 3D rasterizer for avatar head previews, including the 56×64 pie-head preview
/// and speech icons. Rasterizes posed head meshes produced by <see cref="AvatarSceneGeometry"/>.
/// </summary>
internal static class SimPortrait
{
    public const int PieHeadWidth = 128, PieHeadHeight = 128;

    private sealed record Triangle(Vector3 A, Vector3 B, Vector3 C, Vector2 Ta, Vector2 Tb, Vector2 Tc, Vector3 Na, Vector3 Nb, Vector3 Nc, Texture Image);
    private sealed record Texture(int Width, int Height, byte[] Rgba);

    private static readonly (int R, int G, int B)[] RelationshipColours = [(208, 0, 0), (136, 44, 40), (64, 92, 88), (32, 144, 72), (0, 200, 56)];
    private static readonly (int R, int G, int B) Navy = (0, 0, 82), Speech = (248, 252, 248), Black = (0, 0, 0), Edge = (0, 0, 248);

    private delegate (int R, int G, int B, int A)[] Shot(int width, int height);

    private const float BufferSize = 200f;
    private const float BufferCenter = 100f;
    private const float BufferPixelsPerBoneUnit = 118.35f;
    private static readonly (int Left, int Top, int Right, int Bottom) PortraitHeadWindow = (76, 45, 122, 125);

    /// <summary>Creates the front-facing PersonFinder face image used in the Create a Family member slots.</summary>
    internal static byte[] FaceBitmap(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes) =>
        Strip(PrepareShot(meshes, 0, 0, useFixedHeadWindow: true), 35, 41, 1, _ => Navy);

    /// <summary>Adds or replaces the portrait BMP_ chunks (2002..2006) in a Sim's character IFF file.</summary>
    internal static SimsEngine.Formats.IffFile WithPortraits(SimsEngine.Formats.IffFile iff, IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes)
    {
        // FUN_004e739b (004e739b.c:71-75), FUN_004e7584 (004e7584.c:79-83), FUN_004e77ad (004e77ad.c:28-31), and
        // FUN_004e853c (004e853c.c:61-65) pass fixed crop rect (76, 45, 122, 125) around HEAD bone origin (100, 100).
        // MakeSpeakingIcon passes cropRect = NULL so speech icons (2005/2006) auto-crop to head bounds.
        var front = PrepareShot(meshes, 0, 0, useFixedHeadWindow: true);
        var turned = PrepareShot(meshes, -30, 0, useFixedHeadWindow: false);
        var chunks = new (ushort Id, string Label, byte[] Data)[]
        {
            (2002, "faces", Strip(front, 35, 41, 3, _ => Navy)),
            (2003, "rel. images", Strip(front, 40, 40, 5, frame => RelationshipColours[frame])),
            (2004, "thumbnail", Bordered(front, 25)),
            (2005, "speech_med", Strip(turned, 16, 16, 1, _ => Speech)),
            (2006, "speech_large", Strip(turned, 32, 32, 1, _ => Speech)),
        };
        var bmpType = SimsEngine.Formats.ResourceType.Parse("BMP_");
        foreach (var (id, label, data) in chunks)
        {
            iff = iff.WithAddedOrReplacedChunk(bmpType, id, data, 0, label);
        }
        return iff;
    }

    /// <summary>The pie menu head of a native Sim, from its head appearance posed by <see cref="AvatarSceneGeometry"/>.
    /// cPieMenuHead::Draw (004623e6.c:181-313) rotates the HEAD mesh around a fixed 3D pivot at fixed head scale
    /// without re-cropping or shifting the bounding box per yaw/pitch angle.</summary>
    internal static byte[] PieHeadPreview(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes, float yaw, float pitch)
    {
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch) || Math.Abs(yaw) > 60 || Math.Abs(pitch) > 75)
            throw new ArgumentException("Invalid pie head orientation");
        var pixels = RenderPieHeadMeshes(meshes, yaw, pitch, PieHeadWidth, PieHeadHeight);
        var rgba = new byte[pixels.Length * 4];
        for (var index = 0; index < pixels.Length; index++)
            (rgba[index * 4], rgba[index * 4 + 1], rgba[index * 4 + 2], rgba[index * 4 + 3]) = ((byte)pixels[index].R, (byte)pixels[index].G, (byte)pixels[index].B, (byte)pixels[index].A);
        return rgba;
    }

    /// <summary>Mac <c>PersonFinder::GetSpeechIcon</c>'s allow-generate path: <c>GenerateBitmaps(4, 0)</c> invokes
    /// <c>MakeSpeakingIcon(2/3)</c>, which loads the white speech UI buffer (0x2328 medium, 0x2329 large) and renders
    /// the posed head over it with <c>MakePersonHeadImage(person, -5, 18, ...)</c>
    /// (<c>PeopleButtons.s</c> 0x102323F0; yaw -5.0 at <c>data.s</c> 0x105C0690, pitch 18.0 at 0x105C0694).
    /// </summary>
    internal static (int Width, int Height, byte[] Rgba) SpeechIcon(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes, int size = 16)
    {
        if (size is < 1 or > 128) throw new ArgumentException("Invalid speech image size");
        var pixels = RenderHeadMeshes(meshes, -5, 18, size, size);
        return CompositedOverWhite(pixels, size, size);
    }

    private static (int Width, int Height, byte[] Rgba) CompositedOverWhite((int R, int G, int B, int A)[] pixels, int width, int height)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Invalid speech image pixels");
        var rgba = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index++)
            Put(rgba, width, index % width, index / width, pixels[index], (255, 255, 255));
        return (width, height, rgba);
    }

    private static Shot PrepareShot(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes, float yaw, float pitch, bool useFixedHeadWindow)
    {
        var unrotatedHead = ExtractHeadPoints(meshes);
        var headOrigin = useFixedHeadWindow ? meshes.Select(m => m.HeadOrigin).FirstOrDefault(p => p.HasValue) : null;
        var headScale = meshes.Select(m => m.HeadScale).FirstOrDefault(s => s > 0f);
        if (headScale <= 0f) headScale = 1f;
        var pivot = headOrigin ?? ComputeBoundsCenter(unrotatedHead);
        // MakePersonHeadImage (004e7902.c:105-126) & cPieMenuHead::Draw (004623e6.c:286-305) apply local pitch first, then vertical yaw.
        // Positive pitch in Sims.exe tilts the head UP (+Y); System.Numerics CreateRotationX(+a) maps +Z to -Y, so negate pitchRad.
        var yawRad = yaw * MathF.PI / 180f;
        var pitchRad = pitch * MathF.PI / 180f;
        var view = Matrix4x4.CreateRotationX(-pitchRad) * Matrix4x4.CreateRotationY(yawRad);
        var triangles = new List<Triangle>();
        var headPoints = new List<Vector3>();
        foreach (var mesh in meshes)
        {
            var texture = Decode(mesh.Texture);
            var points = new Vector3[mesh.Positions.Length];
            var normals = new Vector3[points.Length];
            for (var index = 0; index < points.Length; index++)
            {
                var position = mesh.Positions[index] - pivot;
                points[index] = Vector3.Transform(position, view);
                var normal = mesh.Normals[index];
                normals[index] = Vector3.TransformNormal(normal, view);
                if (mesh.OnHead[index]) headPoints.Add(points[index]);
            }
            foreach (var (a, b, c) in mesh.Faces)
                triangles.Add(new Triangle(points[a], points[b], points[c],
                    mesh.UVs[a], mesh.UVs[b], mesh.UVs[c],
                    normals[a], normals[b], normals[c], texture));
        }
        return headOrigin.HasValue ? RasterizeFixedHeadWindow(triangles, headScale) : Rasterize(triangles, headPoints);
    }

    private static byte[] Strip(Shot shot, int width, int height, int frames, Func<int, (int R, int G, int B)> background)
    {
        var pixels = shot(width, height);
        var rgba = new byte[width * frames * height * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            var back = background(frame);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Put(rgba, width * frames, frame * width + x, y, pixels[y * width + x], back);
        }
        return BitmapCodec.Write(width * frames, height, rgba);
    }

    private static byte[] Bordered(Shot shot, int size)
    {
        var pixels = shot(size - 2, size - 2);
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
            Put(rgba, size, x, y, edge ? (0, 0, 0, 0) : pixels[(y - 1) * (size - 2) + x - 1], edge ? Edge : Black);
        }
        return BitmapCodec.Write(size, size, rgba);
    }

    private static List<Vector3> ExtractHeadPoints(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes)
    {
        var unrotatedHead = new List<Vector3>();
        foreach (var mesh in meshes)
            for (var index = 0; index < mesh.Positions.Length; index++)
                if (mesh.OnHead[index]) unrotatedHead.Add(mesh.Positions[index]);
        if (unrotatedHead.Count == 0) throw new InvalidDataException("The Sim has no head geometry");
        return unrotatedHead;
    }

    private static Vector3 ComputeBoundsCenter(List<Vector3> points)
    {
        float left = points.Min(p => p.X), right = points.Max(p => p.X);
        float top = points.Max(p => p.Y), chin = points.Min(p => p.Y);
        float front = points.Max(p => p.Z), back = points.Min(p => p.Z);
        return new Vector3((left + right) * 0.5f, (top + chin) * 0.5f, (front + back) * 0.5f);
    }

    private static (int R, int G, int B, int A)[] RenderPieHeadMeshes(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes, float yaw, float pitch, int width, int height)
    {
        var unrotatedHead = ExtractHeadPoints(meshes);
        var bonePivot = meshes.Select(m => m.HeadPivot).FirstOrDefault(p => p.HasValue);
        var boneHeight = meshes.Select(m => m.HeadHeight).FirstOrDefault(h => h > 0f);
        var pivot = bonePivot ?? ComputeBoundsCenter(unrotatedHead);
        var headHeight = boneHeight > 0f ? boneHeight : unrotatedHead.Max(p => p.Y) - unrotatedHead.Min(p => p.Y);

        // cPieMenuHead::Draw (004623e6.c:286-305) pitches around the head's ear-to-ear axis (+0x17) first,
        // then yaws around the upright vertical neck axis (+0x18) second so diagonal turns never introduce gimbal roll.
        // Positive pitch in Sims.exe (+cos(theta), case 0 +0.6 rad, +8 deg neutral) tilts UP (+Y), so negate pitchRad for CreateRotationX.
        var yawRad = yaw * MathF.PI / 180f;
        var pitchRad = pitch * MathF.PI / 180f;
        var view = Matrix4x4.CreateRotationX(-pitchRad) * Matrix4x4.CreateRotationY(yawRad);
        var triangles = new List<Triangle>();
        foreach (var mesh in meshes)
        {
            var texture = Decode(mesh.Texture);
            var points = new Vector3[mesh.Positions.Length];
            var normals = new Vector3[points.Length];
            for (var index = 0; index < points.Length; index++)
            {
                points[index] = Vector3.Transform(mesh.Positions[index] - pivot, view);
                normals[index] = Vector3.TransformNormal(mesh.Normals[index], view);
            }
            foreach (var (a, b, c) in mesh.Faces)
                triangles.Add(new Triangle(points[a], points[b], points[c],
                    mesh.UVs[a], mesh.UVs[b], mesh.UVs[c],
                    normals[a], normals[b], normals[c], texture));
        }

        const int Samples = 4;
        const float NominalHeadFrameHeight = 52f;
        int w = width * Samples, h = height * Samples;
        var scale = (NominalHeadFrameHeight * Samples) / (headHeight * 1.04f);
        return RasterizeProjected(triangles, width, height, w, h, p => new Vector3(w * 0.5f + p.X * scale, h * 0.5f - p.Y * scale, p.Z));
    }

    private static (int R, int G, int B, int A)[] RenderHeadMeshes(IReadOnlyList<AvatarSceneGeometry.PosedHeadMesh> meshes, float yaw, float pitch, int width, int height)
    {
        var unrotatedHead = ExtractHeadPoints(meshes);
        var pivot = ComputeBoundsCenter(unrotatedHead);
        // Posed without the Sim's facing, the head looks toward the player already; negate pitchRad so positive pitch tilts UP (+Y).
        var yawRad = yaw * MathF.PI / 180f;
        var pitchRad = pitch * MathF.PI / 180f;
        var view = Matrix4x4.CreateRotationX(-pitchRad) * Matrix4x4.CreateRotationY(yawRad);
        var triangles = new List<Triangle>();
        var headPoints = new List<Vector3>();
        foreach (var mesh in meshes)
        {
            var texture = Decode(mesh.Texture);
            var points = new Vector3[mesh.Positions.Length];
            var normals = new Vector3[points.Length];
            for (var index = 0; index < points.Length; index++)
            {
                var position = mesh.Positions[index] - pivot;
                points[index] = Vector3.Transform(position, view);
                var normal = mesh.Normals[index];
                normals[index] = Vector3.TransformNormal(normal, view);
                if (mesh.OnHead[index]) headPoints.Add(points[index]);
            }
            foreach (var (a, b, c) in mesh.Faces)
                triangles.Add(new Triangle(points[a], points[b], points[c],
                    mesh.UVs[a], mesh.UVs[b], mesh.UVs[c],
                    normals[a], normals[b], normals[c], texture));
        }
        return Rasterize(triangles, headPoints)(width, height);
    }

    /// <summary>Implements MakePersonHeadImage's fixed HEAD-bone window crop (004e7902.c:91-104, 228-294):
    /// projects into the 200x200 offscreen buffer centered at (100, 100), expands (76, 45, 122, 125) symmetrically
    /// to match the target width/height aspect ratio, and scales into the output frame.</summary>
    private static Shot RasterizeFixedHeadWindow(List<Triangle> triangles, float headScale)
    {
        var worldToBuffer = (BufferPixelsPerBoneUnit * 3f) / Math.Max(1e-4f, headScale);
        return (width, height) =>
        {
            const int Samples = 4;
            int w = width * Samples, h = height * Samples;
            var (left, top, right, bottom) = PortraitHeadWindow;
            var targetAspect = (float)width / Math.Max(1, height);
            var cropWidth = right - left;
            var cropHeight = bottom - top;
            var cropAspect = (float)cropWidth / cropHeight;
            if (cropAspect < targetAspect)
            {
                var expandedWidth = Math.Min((int)BufferSize, (int)(cropHeight * targetAspect));
                var remaining = expandedWidth - cropWidth;
                while (remaining > 1 && left > 0 && right < (int)BufferSize)
                {
                    left--;
                    right++;
                    remaining -= 2;
                }
                while (remaining > 0 && right < (int)BufferSize)
                {
                    right++;
                    remaining--;
                }
            }
            else if (cropAspect > targetAspect)
            {
                var expandedHeight = Math.Min((int)BufferSize, (int)(cropWidth / targetAspect));
                var remaining = expandedHeight - cropHeight;
                while (remaining > 1 && top > 0 && bottom < (int)BufferSize)
                {
                    top--;
                    bottom++;
                    remaining -= 2;
                }
                while (remaining > 0 && bottom < (int)BufferSize)
                {
                    bottom++;
                    remaining--;
                }
            }
            var scaleX = w / (float)Math.Max(1, right - left);
            var scaleY = h / (float)Math.Max(1, bottom - top);
            return RasterizeProjected(triangles, width, height, w, h, p =>
            {
                var bufX = BufferCenter + p.X * worldToBuffer;
                var bufY = BufferCenter - p.Y * worldToBuffer;
                return new Vector3((bufX - left) * scaleX, (bufY - top) * scaleY, p.Z);
            });
        };
    }

    private static Shot Rasterize(List<Triangle> triangles, List<Vector3> headPoints)
    {
        if (headPoints.Count == 0) throw new InvalidDataException("The Sim has no head geometry");
        float left = headPoints.Min(p => p.X), right = headPoints.Max(p => p.X);
        float top = headPoints.Max(p => p.Y), chin = headPoints.Min(p => p.Y);

        return (width, height) =>
        {
            const int Samples = 4;
            int w = width * Samples, h = height * Samples;
            // Crown to chin fills the frame height unless wide pet ears/snouts require width clamping; keep human portraits top-aligned and center shorter width-clamped heads vertically.
            var heightSpan = Math.Max(1e-4f, top - chin);
            var widthSpan = Math.Max(1e-4f, right - left);
            var scale = Math.Min(h / (heightSpan * 1.04f), w / (widthSpan * 1.08f));
            var offsetY = Math.Max(h * 0.01f, (h - heightSpan * scale) * 0.5f);
            var centreX = (left + right) / 2;
            return RasterizeProjected(triangles, width, height, w, h,
                p => new Vector3(w / 2f + (p.X - centreX) * scale, offsetY + (top - p.Y) * scale, p.Z));
        };
    }

    private static (int R, int G, int B, int A)[] RasterizeProjected(
        List<Triangle> triangles, int width, int height, int w, int h, Func<Vector3, Vector3> screen)
    {
        const int Samples = 4;
        var light = Vector3.Normalize(new Vector3(-0.35f, 0.45f, 1));
        var colour = new Vector4[w * h];
        var depth = new float[w * h];
        Array.Fill(depth, float.NegativeInfinity);
        foreach (var triangle in triangles)
        {
            Vector3 a = screen(triangle.A), b = screen(triangle.B), c = screen(triangle.C);
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                var wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                var wc = 1 - wa - wb;
                if (wa < 0 || wb < 0 || wc < 0) continue;
                var z = wa * a.Z + wb * b.Z + wc * c.Z;
                var at = y * w + x;
                if (z <= depth[at]) continue;
                var uv = triangle.Ta * wa + triangle.Tb * wb + triangle.Tc * wc;
                var image = triangle.Image;
                int tx = Math.Clamp((int)(uv.X * image.Width), 0, image.Width - 1), ty = Math.Clamp((int)(uv.Y * image.Height), 0, image.Height - 1);
                var texel = (ty * image.Width + tx) * 4;
                var normal = Vector3.Normalize(triangle.Na * wa + triangle.Nb * wb + triangle.Nc * wc);
                var shade = Math.Min(1.1f, 0.7f + 0.45f * Math.Max(0, Vector3.Dot(normal, light)));
                depth[at] = z;
                colour[at] = new Vector4(image.Rgba[texel] * shade, image.Rgba[texel + 1] * shade, image.Rgba[texel + 2] * shade, 1);
            }
        }
        var result = new (int, int, int, int)[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sum = Vector4.Zero;
            for (var sy = 0; sy < Samples; sy++)
            for (var sx = 0; sx < Samples; sx++)
            {
                var sample = colour[(y * Samples + sy) * w + x * Samples + sx];
                sum += new Vector4(sample.X * sample.W, sample.Y * sample.W, sample.Z * sample.W, sample.W);
            }
            var coverage = sum.W / (Samples * Samples);
            result[y * width + x] = sum.W > 0
                ? ((int)Math.Min(255, sum.X / sum.W), (int)Math.Min(255, sum.Y / sum.W), (int)Math.Min(255, sum.Z / sum.W), (int)(coverage * 255))
                : (0, 0, 0, 0);
        }
        return result;
    }

    private static void Put(byte[] rgba, int width, int x, int y, (int R, int G, int B, int A) pixel, (int R, int G, int B) back)
    {
        var offset = (y * width + x) * 4;
        rgba[offset] = (byte)((pixel.R * pixel.A + back.R * (255 - pixel.A)) / 255);
        rgba[offset + 1] = (byte)((pixel.G * pixel.A + back.G * (255 - pixel.A)) / 255);
        rgba[offset + 2] = (byte)((pixel.B * pixel.A + back.B * (255 - pixel.A)) / 255);
        rgba[offset + 3] = 255;
    }

    /// <summary>Avatar textures are BMPs or uncompressed/RLE truecolour TGAs.</summary>
    private static Texture Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            var (width, height, rgba) = BitmapCodec.Read(data);
            return new Texture(width, height, rgba);
        }
        if (data.Length < 18 || data[2] is not (2 or 10) || data[16] is not (24 or 32)) return new Texture(1, 1, [160, 160, 160, 255]);
        int w = BitConverter.ToUInt16(data, 12), h = BitConverter.ToUInt16(data, 14), bytes = data[16] / 8;
        var topDown = (data[17] & 0x20) != 0;
        var output = new byte[w * h * 4];
        var position = 18 + data[0];
        var pixel = 0;
        void Emit(int at)
        {
            if (pixel >= w * h || at + bytes > data.Length) { pixel++; return; }
            int x = pixel % w, y = pixel / w, row = topDown ? y : h - 1 - y, target = (row * w + x) * 4;
            (output[target], output[target + 1], output[target + 2], output[target + 3]) = (data[at + 2], data[at + 1], data[at], 255);
            pixel++;
        }
        while (pixel < w * h && position < data.Length)
        {
            if (data[2] == 2) { Emit(position); position += bytes; continue; }
            var header = data[position++];
            var count = (header & 0x7F) + 1;
            if ((header & 0x80) != 0) { for (var i = 0; i < count; i++) Emit(position); position += bytes; }
            else for (var i = 0; i < count; i++) { Emit(position); position += bytes; }
        }
        return new Texture(w, h, output);
    }
}
