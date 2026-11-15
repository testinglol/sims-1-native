using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

/// <summary>Resolves the five appearance pairs stored on Sims.exe's person character (+0xadc) and skins those original
/// meshes into the lot scene. FUN_0059187a reads those pairs from saved Sim state; FUN_0059903f links skeleton bones by
/// name, and FUN_005d337d draws textured indexed mesh triangles. Saved appearance resources are used when installed;
/// when imported Sim data names absent resources, the preview selects a compatible installed body/head mesh so one
/// missing custom resource cannot suppress the rest of the household. Character data and CFP channels stay in their
/// authored Sims.exe coordinates; bone hierarchy composition follows the Mac Bone::UpdateTransform routine. Root
/// placement follows SAnimator::UpdateRootPos; VitaBoy then composes its retained
/// uniform scale with the supplied heading and translation before installing the skeleton root.</summary>
internal sealed class AvatarSceneGeometry
{
    // UpdateRootPos uses the Y component written by ZBufferLookup::GenerateLookupTables: the two doubles at
    // lbl_1059B280 + 0x30/+0x38 sum to 9.3005113679684 source units. Its lot coordinates use 3/16 per FTile
    // (lbl_1059BBC4 + 0x40), while native root coordinates use 1/16, so convert this basis component by 3.
    private const float AnimatorLevelHeight = 3.1001704f;

    internal sealed record Vertex(float X, float Y, float Z, float U, float V, float Nx, float Ny, float Nz);
    internal sealed record Part(byte[] Texture, string TextureHash, string Encoding, int Width, int Height, Vertex[] Vertices, bool IgnoreRoomLighting = false);
    internal sealed record CensorBounds(float X, float Y, float Width, float Height, float Depth);

    // FUN_005a21ae stores the search path a suit was read from; the suit's meshes are found on that path.
    private sealed record AppearanceSource(Appearance Appearance, CharacterFile File, CharacterData Data, CharacterSearchPath Path);
    private sealed record TextureSource(byte[] Bytes, string Sha1Hex, string Encoding, int Width, int Height, string FileName);
    private sealed record AnimationSource(Animation Animation, AnimationCurves Curves);
    private sealed record SkateMesh(Vector3[] Positions, Vector2[] UVs, Vector3[] Normals,
        (int Position, int UV, int Normal)[] Corners, TextureSource Texture);
    private sealed record SkateFrame(float[] Translation, float[] Rotation);
    private sealed record SkateSideFrame(string SlotJoint, SkateFrame Slot, string FootJoint, SkateFrame Foot);
    private sealed record SkateSkeletonFrames(string Age, Dictionary<string, SkateSideFrame> Frames);
    private sealed record SkateAttachmentFrames(int SchemaVersion, string CoordinateSystem,
        Dictionary<string, SkateSkeletonFrames> Skeletons);

    private sealed class SkeletonInfo
    {
        public readonly Skeleton Skeleton;
        public readonly HashSet<string> BoneSet;
        public readonly Dictionary<string, int> IndexByNameIgnoreCase;
        public readonly Dictionary<string, int> IndexByNameOrdinal;
        public readonly string Signature;

        public SkeletonInfo(Skeleton skeleton)
        {
            Skeleton = skeleton;
            var count = skeleton.Bones.Count;
            BoneSet = new HashSet<string>(count, StringComparer.Ordinal);
            IndexByNameIgnoreCase = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
            IndexByNameOrdinal = new Dictionary<string, int>(count, StringComparer.Ordinal);
            var sigBuilder = new StringBuilder(skeleton.Name.Length + count * 8);
            sigBuilder.Append(skeleton.Name).Append('\0');
            for (var i = 0; i < count; i++)
            {
                var name = skeleton.Bones[i].Name;
                BoneSet.Add(name);
                IndexByNameIgnoreCase.TryAdd(name, i);
                IndexByNameOrdinal.TryAdd(name, i);
                if (i > 0) sigBuilder.Append('|');
                sigBuilder.Append(name);
            }
            Signature = sigBuilder.ToString();
        }
    }

    private sealed class SkeletonMeshMap(int[]? boneIndices, string? missingBoneName)
    {
        public readonly int[]? BoneIndices = boneIndices;
        public readonly string? MissingBoneName = missingBoneName;
    }

    private sealed class MeshBindingLayout
    {
        public readonly bool HasInvalidBoneIndex;
        public readonly int[] BoneForVertex;
        public readonly int[] BlendBoneForVertex;
        public readonly float[] BlendWeightForVertex;
        public readonly int[] BlendVertexForVertex;
        public readonly bool[] IsLeftFootVertex;
        public readonly bool[] IsRightFootVertex;
        public readonly bool[] IsHeadBoneVertex;
        public readonly int[] FlatFaceIndices;
        public readonly (int A, int B, int C)[] FacesTuples;
        private readonly ConditionalWeakTable<Skeleton, SkeletonMeshMap> skeletonMaps = new();

        public MeshBindingLayout(Mesh mesh)
        {
            var vertexCount = mesh.TextureCoordinates.Count;
            BoneForVertex = new int[vertexCount];
            BlendBoneForVertex = new int[vertexCount];
            Array.Fill(BlendBoneForVertex, -1);
            BlendWeightForVertex = new float[vertexCount];
            BlendVertexForVertex = new int[vertexCount];
            foreach (var binding in mesh.Bindings)
            {
                if (binding.Bone < 0 || binding.Bone >= mesh.Bones.Count)
                {
                    HasInvalidBoneIndex = true;
                    break;
                }
                for (var vertex = binding.FirstVertex; vertex < binding.FirstVertex + binding.VertexCount; vertex++)
                    BoneForVertex[vertex] = binding.Bone;
                for (var index = binding.FirstBlend; index < binding.FirstBlend + binding.BlendCount; index++)
                {
                    var blend = mesh.Blends[index];
                    BlendBoneForVertex[blend.Vertex] = binding.Bone;
                    BlendWeightForVertex[blend.Vertex] = blend.Weight / 32768f;
                    BlendVertexForVertex[blend.Vertex] = vertexCount + index;
                }
            }

            IsLeftFootVertex = new bool[vertexCount];
            IsRightFootVertex = new bool[vertexCount];
            IsHeadBoneVertex = new bool[vertexCount];
            if (!HasInvalidBoneIndex && mesh.Bones.Count > 0)
            {
                var boneIsLeftFoot = new bool[mesh.Bones.Count];
                var boneIsRightFoot = new bool[mesh.Bones.Count];
                var boneIsHead = new bool[mesh.Bones.Count];
                for (var b = 0; b < mesh.Bones.Count; b++)
                {
                    var name = mesh.Bones[b];
                    boneIsLeftFoot[b] = name.Equals("L_FOOT", StringComparison.OrdinalIgnoreCase);
                    boneIsRightFoot[b] = name.Equals("R_FOOT", StringComparison.OrdinalIgnoreCase);
                    boneIsHead[b] = name.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
                }
                for (var v = 0; v < vertexCount; v++)
                {
                    var primary = BoneForVertex[v];
                    var blendBone = BlendBoneForVertex[v];
                    IsLeftFootVertex[v] = boneIsLeftFoot[primary] || (blendBone >= 0 && boneIsLeftFoot[blendBone]);
                    IsRightFootVertex[v] = boneIsRightFoot[primary] || (blendBone >= 0 && boneIsRightFoot[blendBone]);
                    IsHeadBoneVertex[v] = boneIsHead[primary];
                }
            }

            var faceCount = mesh.Faces.Count;
            FlatFaceIndices = new int[faceCount * 3];
            FacesTuples = new (int A, int B, int C)[faceCount];
            for (var f = 0; f < faceCount; f++)
            {
                var face = mesh.Faces[f];
                FlatFaceIndices[f * 3] = face.A;
                FlatFaceIndices[f * 3 + 1] = face.B;
                FlatFaceIndices[f * 3 + 2] = face.C;
                FacesTuples[f] = (face.A, face.B, face.C);
            }
        }

        public SkeletonMeshMap ResolveSkeleton(Mesh mesh, Skeleton skeleton) =>
            skeletonMaps.GetValue(skeleton, s =>
            {
                var info = GetSkeletonInfo(s);
                foreach (var binding in mesh.Bindings)
                {
                    var boneName = mesh.Bones[binding.Bone];
                    if (!info.IndexByNameOrdinal.ContainsKey(boneName))
                        return new SkeletonMeshMap(null, boneName);
                }
                var map = new int[mesh.Bones.Count];
                for (var b = 0; b < mesh.Bones.Count; b++)
                    map[b] = info.IndexByNameOrdinal.TryGetValue(mesh.Bones[b], out var idx) ? idx : -1;
                return new SkeletonMeshMap(map, null);
            });
    }

    private static readonly ConditionalWeakTable<ObjectRegistry, AvatarSceneGeometry> Instances = new();
    private static readonly ConditionalWeakTable<Skeleton, SkeletonInfo> SkeletonInfos = new();
    private static readonly ConditionalWeakTable<Mesh, MeshBindingLayout> MeshLayouts = new();
    private static readonly TextureSource FallbackWhiteTexture = CreateSolidWhiteTexture();
    private static SkeletonInfo GetSkeletonInfo(Skeleton skeleton) => SkeletonInfos.GetValue(skeleton, static s => new SkeletonInfo(s));
    private static MeshBindingLayout GetMeshLayout(Mesh mesh) => MeshLayouts.GetValue(mesh, static m => new MeshBindingLayout(m));

    private readonly string gameRoot;
    private readonly ObjectRegistry registry;
    private readonly CharacterSearchPath skins;
    private readonly CharacterSearchPath textures;
    private readonly Dictionary<string, AppearanceSource> appearances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mesh> meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextureSource> textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(byte R, byte G, byte B), (byte[] Bytes, string Hash)> plumbobTextures = [];
    private readonly Dictionary<(IffFile File, ushort ChunkId), IReadOnlyList<string>> bodyStringsCache = [];
    private readonly Dictionary<string, SkateMesh> skateMeshes = new(StringComparer.OrdinalIgnoreCase);
    private SkateAttachmentFrames? skateAttachmentFrames;
    private readonly Dictionary<string, AnimationSource?> animations = new(StringComparer.OrdinalIgnoreCase);
    private readonly AvatarAnimations.SharedCharacterIndex sharedIndex;
    private CharacterSearchPath? animationPath;
    private IReadOnlyDictionary<string, CharacterFile[]>? curveFiles;
    private readonly List<Skeleton> skeletons = [];
    private readonly List<SkeletonInfo> uniqueSkeletons = [];
    private readonly Dictionary<short, Vector3> headPositions = [];
    private readonly Dictionary<short, Vector3> headAttachmentPositions = [];
    private readonly Dictionary<short, Vector3> handPositions = [];
    private readonly Dictionary<short, Vector3> leftHandPositions = [];
    private readonly Dictionary<short, Vector3> pelvisPositions = [];
    private readonly Dictionary<short, Vector3> headSeekRegistrations = [];
    private readonly Dictionary<short, Vector3> headFakeRegistrations = [];
    private readonly Dictionary<short, Vector3> avatarRootPositions = [];
    private readonly Dictionary<short, float> avatarRootDirections = [];
    private readonly Dictionary<short, CensorBounds> censorBounds = [];
    private readonly Dictionary<short, (long Updates, float RootX, float RootY, int Level)> lastTickRoots = [];
    private readonly Dictionary<short, (long Updates, float RootX, float RootY, int Level)> prevTickRoots = [];

    private AvatarSceneGeometry(string gameRoot, ObjectRegistry registry)
    {
        this.gameRoot = gameRoot;
        this.registry = registry;
        sharedIndex = AvatarAnimations.GetSharedIndex(gameRoot, registry);
        skins = sharedIndex.Skins;
        textures = sharedIndex.Textures;
        animationPath = sharedIndex.Animation;
        curveFiles = sharedIndex.CurveFiles;
        // FUN_005837c0 reads the skins path, then the animation path (FUN_005a009d -> FUN_0059fad3);
        // both readers register skeletons and suits, so adult/child skeletons and hand suits come from Animation.
        for (var i = 0; i < sharedIndex.ParsedFiles.Count; i++)
        {
            var parsed = sharedIndex.ParsedFiles[i];
            skeletons.AddRange(parsed.Data.Skeletons);
            for (var a = 0; a < parsed.Data.Appearances.Count; a++)
            {
                var appearance = parsed.Data.Appearances[a];
                appearances.TryAdd(appearance.Name, new AppearanceSource(appearance, parsed.File, parsed.Data, parsed.Path));
            }
        }
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skeleton in skeletons)
        {
            var info = GetSkeletonInfo(skeleton);
            if (seenSignatures.Add(info.Signature)) uniqueSkeletons.Add(info);
        }
    }

    internal static AvatarSceneGeometry For(string gameRoot, ObjectRegistry registry) =>
        Instances.GetValue(registry, _ => new AvatarSceneGeometry(gameRoot, registry));

    /// <summary>A mesh of the head appearance posed without facing, scale or projection, as the pie menu's head draws it.
    /// <see cref="OnHead"/> marks the vertices bound to the HEAD bone, <see cref="HeadPivot"/>/<see cref="HeadHeight"/>
    /// carry the pie-head skull center and nominal height, and <see cref="HeadOrigin"/>/<see cref="HeadScale"/> carry
    /// the HEAD bone origin and actor scale for MakePersonHeadImage's fixed (76,45,122,125) portrait window.</summary>
    internal sealed record PosedHeadMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] UVs, bool[] OnHead,
        (int A, int B, int C)[] Faces, byte[] Texture, Vector3? HeadPivot = null, float HeadHeight = 0f,
        Vector3? HeadOrigin = null, float HeadScale = 1f);

    internal IReadOnlyList<Part> Build(XPerson person, int level, int cameraOrientation,
        Func<float, float, float> elevation, LotCamera camera, List<PosedHeadMesh>? headMeshes = null,
        Vector3? attachmentRoot = null, float? inheritedRootDirection = null, int headIndicatorKind = 0,
        float subTickAlpha = 0f, long updates = 0)
    {
        var character = person.PresentationCharacter ?? person.SavedCharacter;
        if (character is null)
        {
            PrepareRootTransform(person, level, elevation, camera, headMeshes, attachmentRoot, inheritedRootDirection, subTickAlpha, updates);
            if (headMeshes is null) censorBounds.Remove(person.Id);
            return Array.Empty<Part>();
        }
        var effectiveParts = character.Parts.Count >= 5
            ? character.Parts
            : character.Parts.Concat(Enumerable.Repeat(("NONE", ""), 5 - character.Parts.Count)).ToArray();

        var (bodyPair, leftHandPair, rightHandPair, headPair) = ResolveEffectiveParts(person, effectiveParts);
        var selected = new List<(string Name, string? Texture, string? TextureFilter, string SavedName, int Role)>();
        const int RoleBody = 0;
        const int RoleHand = 1;
        const int RoleHead = 2;
        const int RoleExtra = 3;
        AddPair(bodyPair, RoleBody); // body
        AddPair(leftHandPair, RoleHand); // left hand
        AddPair(rightHandPair, RoleHand); // right hand
        AddPair(headPair, RoleHead); // head
        var extraPartStart = selected.Count;
        var seenExtraParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extraPart in character.ExtraParts.Concat(person.TransientExtraParts))
        {
            var normalizedExtra = NormalizeAppearance(extraPart.Item1.Trim());
            if (normalizedExtra.Length > 0 && !normalizedExtra.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                seenExtraParts.Add(normalizedExtra))
                AddPair(extraPart, RoleExtra);
        }
        if (extraPartStart == 0 && appearances.Count > 0)
        {
            var fallbackMarker = PersonFallbackMarker(person);
            if (BodyFallbackByMarker(fallbackMarker) is { } fallbackBody)
                selected.Insert(extraPartStart++, (fallbackBody, null, null, fallbackBody, RoleBody));
            if (HeadFallbackByMarker(fallbackMarker) is { } fallbackHead)
                selected.Insert(extraPartStart++, (fallbackHead.Appearance.Name, null, null, fallbackHead.Appearance.Name, RoleHead));
        }
        if (selected.Count == 0)
        {
            PrepareRootTransform(person, level, elevation, camera, headMeshes, attachmentRoot, inheritedRootDirection, subTickAlpha, updates);
            if (headMeshes is null) censorBounds.Remove(person.Id);
            return Array.Empty<Part>();
        }

        var resolved = new List<(string Name, AppearanceSource source, (Binding Binding, Mesh Mesh, TextureSource Texture)[] bindings, bool IsExtraPart)>();
        for (var selectedIndex = 0; selectedIndex < selected.Count; selectedIndex++)
        {
            var isExtraPart = selectedIndex >= extraPartStart;
            var item = selected[selectedIndex];
            var normalizedName = NormalizeAppearance(item.Name);
            var source = appearances.GetValueOrDefault(normalizedName);
            if (source is null)
            {
                if (normalizedName.StartsWith("accessory-", StringComparison.OrdinalIgnoreCase))
                    source = appearances.GetValueOrDefault(normalizedName["accessory-".Length..]);
                else if (normalizedName.StartsWith("suit-", StringComparison.OrdinalIgnoreCase))
                    source = appearances.GetValueOrDefault(normalizedName["suit-".Length..]);
                // ExpansionPack STR# 304 names "suit-chemset-heed-stars104.apr", while accessory-chemset-heed-stars104.cmx.bcf
                // registers "suit-chemsuit-heed-stars104" (Maxis BCF typo).
                if (source is null &&
                    (normalizedName.Equals("suit-chemset-heed-stars104", StringComparison.OrdinalIgnoreCase) ||
                     normalizedName.Equals("chemset-heed-stars104", StringComparison.OrdinalIgnoreCase)))
                    source = appearances.GetValueOrDefault("suit-chemsuit-heed-stars104");
            }
            if (source is null && (item.Role == RoleBody || normalizedName.StartsWith("b", StringComparison.OrdinalIgnoreCase)))
            {
                var fallback = BodyFallback(normalizedName) ?? BodyFallbackByMarker(PersonFallbackMarker(person));
                source = fallback is null ? null : appearances.GetValueOrDefault(fallback);
            }
            if (source is null && (item.Role == RoleHead || normalizedName.StartsWith("c", StringComparison.OrdinalIgnoreCase)))
                source = HeadFallback(normalizedName) ?? HeadFallbackByMarker(PersonFallbackMarker(person));
            if (source is null)
            {
                // The saved appearance can name a downloaded or otherwise absent character file. Missing hands are
                // optional; a body or head uses the nearest compatible installed original appearance when available.
                // Added suits/accessories (Change Suit/Accessory extra parts) and missing suits skip cleanly via
                // VitaBoy's suit lookup (0058a17c.c:51, 0059187a.c:112,149) rather than aborting avatar draw.
                if (item.Role == RoleHand || normalizedName.StartsWith("h", StringComparison.OrdinalIgnoreCase) || isExtraPart)
                    continue;
                continue;
            }
            var bindingsList = new List<(Binding Binding, Mesh Mesh, TextureSource Texture)>(source.Appearance.Bindings.Count);
            foreach (var binding in source.Appearance.Bindings)
            {
                Mesh mesh;
                try { mesh = ReadMesh(source.Path, binding.Mesh); }
                catch (InvalidDataException) when (isExtraPart) { continue; }
                // VitaBoy::CreateDeformableMesh (00599e94.c:27-49, 0059996c.c:59-63): a "FILTER=texture" override
                // (e.g. "HEAD-HEAD=c003fclgt_goth" or "BODY=...") applies only to bindings whose mesh name contains FILTER,
                // leaving companion bindings in the same suit (glasses SPECS, hats, props) on their own authored mesh.Texture.
                var useOverride = item.Texture is not null &&
                    (item.TextureFilter is null || binding.Mesh.Contains(item.TextureFilter, StringComparison.OrdinalIgnoreCase));
                var requestedTexture = useOverride ? item.Texture! : mesh.Texture;
                TextureSource texture;
                try { texture = ReadTexture(requestedTexture); }
                catch (InvalidDataException) when (useOverride)
                {
                    try { texture = ReadTexture(mesh.Texture); }
                    catch (InvalidDataException) when (isExtraPart) { texture = FallbackWhiteTexture; }
                }
                catch (InvalidDataException) when (isExtraPart)
                {
                    // Sims.exe FUN_005d337d:49-50 binds NULL Direct3D texture when an accessory/prop mesh names an
                    // unshipped texture (e.g. "coffeecup" on coffeecupbigempty/full, "white" on suit-RRings103).
                    texture = FallbackWhiteTexture;
                }
                bindingsList.Add((binding, mesh, texture));
            }
            var bindings = bindingsList.ToArray();
            if (bindings.Length == 0)
                continue;
            resolved.Add((item.Name, source, bindings, isExtraPart));
        }

        if (resolved.Count == 0)
        {
            PrepareRootTransform(person, level, elevation, camera, headMeshes, attachmentRoot, inheritedRootDirection, subTickAlpha, updates);
            if (headMeshes is null) censorBounds.Remove(person.Id);
            return Array.Empty<Part>();
        }

        var coreRequiredBones = new HashSet<string>(StringComparer.Ordinal);
        var allRequiredBones = new HashSet<string>(StringComparer.Ordinal);
        AppearanceSource? bodySource = null;
        var primaryBodyName = selected[0].Name;
        for (var r = 0; r < resolved.Count; r++)
        {
            var entry = resolved[r];
            if (bodySource is null && entry.Name.Equals(primaryBodyName, StringComparison.OrdinalIgnoreCase))
                bodySource = entry.source;
            for (var b = 0; b < entry.bindings.Length; b++)
            {
                var meshBones = entry.bindings[b].Mesh.Bones;
                for (var mb = 0; mb < meshBones.Count; mb++)
                {
                    var boneName = meshBones[mb];
                    allRequiredBones.Add(boneName);
                    if (!entry.IsExtraPart) coreRequiredBones.Add(boneName);
                }
            }
        }
        bodySource ??= resolved[0].source;
        Skeleton skeleton;
        try { skeleton = SelectSkeleton(bodySource, allRequiredBones, person); }
        catch (InvalidDataException) when (allRequiredBones.Count > coreRequiredBones.Count)
        {
            skeleton = SelectSkeleton(bodySource, coreRequiredBones, person);
        }
        var skeletonInfo = GetSkeletonInfo(skeleton);
        // Mac Bone::ReadFromFile and the CFP loader retain the authored channel values. These values
        // are used directly by Sims.exe's avatar transform path.
        var pose = headMeshes is null
            ? Pose.ApplyHeadAdjustment(skeleton, AnimationPose(person, skeleton, subTickAlpha), person.AnimatorHeadAdjustment)
            : Pose.BindPose(skeleton);
        var boneMatrices = new Matrix4x4[pose.Count];
        for (var b = 0; b < pose.Count; b++)
        {
            var bone = pose[b];
            var matrix = Matrix4x4.CreateFromQuaternion(Pose.BoneMatrixRotation(bone.WorldRotation));
            matrix.M41 = bone.WorldTranslation.X;
            matrix.M42 = bone.WorldTranslation.Y;
            matrix.M43 = bone.WorldTranslation.Z;
            boneMatrices[b] = matrix;
        }

        var (tileX, tileY, altitude, rootDirection) = PrepareRootTransform(
            person, level, elevation, camera, headMeshes, attachmentRoot, inheritedRootDirection, subTickAlpha, updates);
        // In lot coordinates, direction 0 is -LotY, 2 is +LotX, 4 is +LotY, and 6 is -LotX (FUN_00452129, FUN_004222ef;
        // cWorldPt stores LotY at +0xf4 and LotX at +0xf8). The original adult HEAD skin maps facial vertices to
        // positive local Z (xskin-C592MA_Tom-HEAD-HEAD.skn). In avatar world coordinates, matrix Z maps to LotX and
        // matrix X maps to LotY via AvatarCoordinates.ToLot. Rotating local +Z by SAnimator::UpdateRootPos's
        // (rootDirection + 6) * pi/4 (005920ea.c:106) yields (sin facing, 0, cos facing), whose ToLot vector
        // (cos facing, sin facing) in (LotX, LotY) aligns the authored +Z forward vector with the Sim's facing.
        var facing = AvatarDirection.LotFacingAngle(rootDirection);
        // An attached animator inherits its owner's root heading, while the named slot joint contributes its
        // translation only. Mac SAnimator::Tick reads bone +0x3c/+0x40/+0x44 into
        // a translation transform, then composes that with the owner root transform (0x10351634–0x103516B8).
        var facingMatrix = headMeshes is null ? Matrix4x4.CreateRotationY(facing) : Matrix4x4.Identity;
        var headName = NormalizeAppearance(headPair.Item1.Trim());
        var headResolved = headName.Length > 0 && !headName.Equals("NONE", StringComparison.OrdinalIgnoreCase)
            ? resolved.FirstOrDefault(item => item.Name.Equals(headName, StringComparison.OrdinalIgnoreCase))
            : default;
        // FUN_004e8249 (004e8249.c:20-35) and cPieMenuHead::Draw (004623e6.c:128-132) iterate all suits on the
        // character and collect only bindings attached to the "HEAD" bone, excluding quadruped PELVIS body bindings
        // on single-suit pets while retaining HEAD-bound accessories across ExtraParts.
        var headBindingsList = headMeshes is null
            ? null
            : resolved.SelectMany(item => item.bindings)
                .Where(item => item.Binding.Bone.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (headBindingsList is { Length: 0 })
            headBindingsList = headResolved.bindings ?? resolved[^1].bindings;
        var headBindings = headBindingsList?.Select(item => item.Mesh).ToHashSet();
        var skullMeshes = headBindingsList?
            .Where(item => item.Binding.Mesh.Contains("HEAD-HEAD", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Mesh)
            .ToHashSet();
        if (skullMeshes is { Count: 0 })
        {
            skullMeshes = headBindingsList?
                .Where(item => !item.Binding.Mesh.Contains("-HAIR", StringComparison.OrdinalIgnoreCase) &&
                               !item.Binding.Mesh.Contains("-HAT", StringComparison.OrdinalIgnoreCase) &&
                               !item.Binding.Mesh.Contains("-ACC", StringComparison.OrdinalIgnoreCase) &&
                               !item.Binding.Mesh.Contains("-SPECS", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Mesh)
                .ToHashSet();
        }
        if (skullMeshes is { Count: 0 }) skullMeshes = headBindings;
        // The source updates VitaBoy scale only on every tenth Simulate pass and retains its prior value when
        // ScalingSim is out of range; use the retained runtime transform value for rendered geometry.
        var scale = person.AnimatorBodyScale;
        // Animator::SetRootTransform copies the supplied transform to +0x38; VitaBoy::SetRootTransform
        // composes its internal transform with that root matrix before installing the result on Skeleton
        // (Animator.s 0x10476D10–0x10476DE4; vitaboy.s 0x10382AB0–0x10382B2C). Keep the same order here:
        // bone pose, root heading, actor scale, then root translation.
        // UpdateRootPos writes FTile Y to source matrix X and FTile X to source matrix Z. Preserve that
        // source transform here; avatar vertices are converted back to lot (X,Y) only at scene projection.
        var rootTransform = facingMatrix * Matrix4x4.CreateScale(scale / 3f) *
                           Matrix4x4.CreateTranslation(AvatarCoordinates.RootTranslation(tileX, tileY, altitude));
        var bodyStrings = ReadBodyStrings(person);
        CacheHeadSeekRegistrations(person, pose, skeletonInfo, bodyStrings, rootTransform);
        BonePose? FindBoneIgnoreCase(string name) =>
            skeletonInfo.IndexByNameIgnoreCase.TryGetValue(name, out var idx) ? pose[idx] : null;
        var headIndex = skeletonInfo.IndexByNameIgnoreCase.TryGetValue("HEAD", out var foundHeadIdx) ? foundHeadIdx : -1;
        var head = headIndex >= 0 ? pose[headIndex] : null;
        Vector3? headOrigin = null;
        Vector3? pieHeadPivot = null;
        var pieHeadHeight = 0.24f * scale;
        if (head is not null)
        {
            if (headMeshes is not null)
            {
                // MakePersonHeadImage (004e7902.c:105-126, 165-179) and cPieMenuHead::Draw (004623e6.c:133, 286-311)
                // overwrite the HEAD bone matrix with a standalone upright camera-facing HEAD transform (local +X crown -> +Y,
                // local +Y ear -> -X, local +Z nose -> +Z with 10 deg isometric camera elevation), avoiding quadruped neck pitch.
                var uprightHead = Matrix4x4.CreateRotationZ(MathF.PI * 0.5f) * Matrix4x4.CreateRotationX(10f * MathF.PI / 180f);
                uprightHead.M41 = head.WorldTranslation.X;
                uprightHead.M42 = head.WorldTranslation.Y;
                uprightHead.M43 = head.WorldTranslation.Z;
                boneMatrices[headIndex] = uprightHead;
            }
            var headRoot = Vector3.Transform(head.WorldTranslation, rootTransform);
            // rootTransform already uses native (lot X, height, lot Y); the attachment cache stores
            // (tile X, tile Y, altitude) like RootPosition, so reorder the components without swapping X/Z.
            headPositions[person.Id] = AvatarCoordinates.ToLot(headRoot);
            headOrigin = headRoot;
            pieHeadPivot = headRoot + new Vector3(0f, 0.105f * scale, 0f);
        }
        var headAttachmentBoneName = bodyStrings is { Count: > 3 } && !string.IsNullOrWhiteSpace(bodyStrings[3])
            ? bodyStrings[3]
            : "HEAD";
        var headAttachmentSuffix = bodyStrings is { Count: > 4 } && !string.IsNullOrWhiteSpace(bodyStrings[4])
            ? bodyStrings[4]
            : "Top";
        var headAttachmentBone = FindBoneIgnoreCase(headAttachmentBoneName) ?? head;
        if (headAttachmentBone is not null)
        {
            var headAttachmentRoot = Vector3.Transform(Pose.RegistrationTranslation(headAttachmentBone, headAttachmentSuffix), rootTransform);
            headAttachmentPositions[person.Id] = AvatarCoordinates.ToLot(headAttachmentRoot);
        }
        var handBoneName = bodyStrings is { Count: > 5 } && !string.IsNullOrWhiteSpace(bodyStrings[5])
            ? bodyStrings[5]
            : "R_HAND";
        var handSuffix = bodyStrings is { Count: > 6 } && !string.IsNullOrWhiteSpace(bodyStrings[6])
            ? bodyStrings[6]
            : "Palm";
        var hand = FindBoneIgnoreCase(handBoneName) ?? FindBoneIgnoreCase("R_HAND");
        if (hand is not null)
        {
            var handRoot = Vector3.Transform(Pose.RegistrationTranslation(hand, handSuffix), rootTransform);
            handPositions[person.Id] = AvatarCoordinates.ToLot(handRoot);
        }
        var leftHand = FindBoneIgnoreCase("L_HAND");
        if (leftHand is not null)
        {
            var leftHandRoot = Vector3.Transform(Pose.RegistrationTranslation(leftHand, handSuffix), rootTransform);
            leftHandPositions[person.Id] = AvatarCoordinates.ToLot(leftHandRoot);
        }
        else
        {
            leftHandPositions.Remove(person.Id);
        }
        var pelvis = FindBoneIgnoreCase("PELVIS");
        if (pelvis is not null)
        {
            var pelvisRoot = Vector3.Transform(pelvis.WorldTranslation, rootTransform);
            pelvisPositions[person.Id] = AvatarCoordinates.ToLot(pelvisRoot);
        }
        var parts = new List<Part>();
        var hasSkates = person.InteractionAttachments.Contains(InteractionLifecycle.SkateAttachment);
        Dictionary<string, List<Vector3>>? skateFootPositions = hasSkates
            ? new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase) { ["left"] = [], ["right"] = [] }
            : null;
        var leftFootPositions = skateFootPositions?["left"];
        var rightFootPositions = skateFootPositions?["right"];
        // SAnimator::StartVitaBoy (00588351.c:42-48), SAnimator::UpdateCensorSuit (0058fafa.c:8-28),
        // SuitInstance::Init (00595645.c:23-24), SAnimator::Draw (00590c09.c:160-296), and
        // cLotRenderer::DrawCensorRects (004ac916.c:51-104):
        // bit 0x80 censors the full visible avatar bounding box; bits 0x01..0x7f instantiate the hidden
        // "<skeleton.Name>-needs" suit and censor the projected vertices of bindings whose Integers[0] mask matches.
        var rawCensorFlags = headMeshes is null ? unchecked((ushort)person[PersonData.CensorshipFlags]) : (ushort)0;
        var fullBodyCensor = (rawCensorFlags & 0x80) != 0;
        var censorMask = fullBodyCensor ? 0x7f : (rawCensorFlags & 0x7f);
        var needsSource = !fullBodyCensor && censorMask != 0
            ? appearances.GetValueOrDefault($"{skeleton.Name}-needs")
            : null;
        var hasCensorBounds = false;
        var censorMinX = float.MaxValue;
        var censorMinY = float.MaxValue;
        var censorMaxX = float.MinValue;
        var censorMaxY = float.MinValue;
        var censorMinDepth = float.MaxValue;

        foreach (var (_, _, bindings, isExtraPart) in resolved)
        foreach (var (outfitBinding, mesh, texture) in bindings)
        {
            var censorThisBinding = rawCensorFlags != 0 &&
                (fullBodyCensor || (needsSource is null && outfitBinding.Integers.Length > 0 && (outfitBinding.Integers[0] & censorMask) != 0));
            if (mesh.Faces.Count * 3 > 250_000) throw new InvalidDataException($"Sim {person.Id} avatar mesh exceeds the scene vertex limit");
            var meshLayout = GetMeshLayout(mesh);
            if (meshLayout.HasInvalidBoneIndex) throw new InvalidDataException($"Sim {person.Id} mesh binding names an unknown bone");
            var skeletonMap = meshLayout.ResolveSkeleton(mesh, skeleton);
            if (skeletonMap.BoneIndices is not { } meshBoneToSkeleton)
            {
                // VitaBoy::CreateDeformableMesh (00599e94.c:76-85) drops an accessory mesh whose bone is absent on
                // the Sim's skeleton rather than failing the character draw.
                if (isExtraPart) continue;
                throw new InvalidDataException($"Sim {person.Id} skeleton {skeleton.Name} has no mesh bone {skeletonMap.MissingBoneName}");
            }

            var boneForVertex = meshLayout.BoneForVertex;
            var blendBoneForVertex = meshLayout.BlendBoneForVertex;
            var blendWeightForVertex = meshLayout.BlendWeightForVertex;
            var blendVertexForVertex = meshLayout.BlendVertexForVertex;
            var posed = new (Vector3 Position, Vector3 Normal, Vector2 UV)[mesh.TextureCoordinates.Count];
            var skateScale = scale / 3f;
            for (var vertexIndex = 0; vertexIndex < posed.Length; vertexIndex++)
            {
                var matrix = boneMatrices[meshBoneToSkeleton[boneForVertex[vertexIndex]]];
                var source = mesh.Vertices[vertexIndex];
                var position = Vector3.Transform(source.Position, matrix);
                var normal = Vector3.TransformNormal(source.Normal, matrix);
                var blendBone = blendBoneForVertex[vertexIndex];
                if (blendBone >= 0)
                {
                    var blendMatrix = boneMatrices[meshBoneToSkeleton[blendBone]];
                    var blendSource = mesh.Vertices[blendVertexForVertex[vertexIndex]];
                    var blendPosition = Vector3.Transform(blendSource.Position, blendMatrix);
                    var blendNormal = Vector3.TransformNormal(blendSource.Normal, blendMatrix);
                    var blendWeight = blendWeightForVertex[vertexIndex];
                    position = Vector3.Lerp(position, blendPosition, blendWeight);
                    normal = Vector3.Lerp(normal, blendNormal, blendWeight);
                }
                normal = Vector3.TransformNormal(normal, facingMatrix);
                if (hasSkates)
                {
                    if (meshLayout.IsLeftFootVertex[vertexIndex])
                        leftFootPositions!.Add(Vector3.Transform(position, facingMatrix) * skateScale);
                    else if (meshLayout.IsRightFootVertex[vertexIndex])
                        rightFootPositions!.Add(Vector3.Transform(position, facingMatrix) * skateScale);
                }
                var rootPosition = Vector3.Transform(position, rootTransform);
                posed[vertexIndex] = (rootPosition, normal, mesh.TextureCoordinates[vertexIndex]);
            }

            if (headBindings is not null && headBindings.Contains(mesh))
            {
                var framesHead = skullMeshes is null || skullMeshes.Contains(mesh);
                var headPositionsOut = new Vector3[posed.Length];
                var headNormalsOut = new Vector3[posed.Length];
                var headUvsOut = new Vector2[posed.Length];
                var onHeadOut = new bool[posed.Length];
                for (var v = 0; v < posed.Length; v++)
                {
                    headPositionsOut[v] = posed[v].Position;
                    headNormalsOut[v] = posed[v].Normal;
                    headUvsOut[v] = posed[v].UV;
                    onHeadOut[v] = framesHead && meshLayout.IsHeadBoneVertex[v];
                }
                headMeshes!.Add(new PosedHeadMesh(headPositionsOut, headNormalsOut,
                    headUvsOut, onHeadOut,
                    meshLayout.FacesTuples, texture.Bytes, pieHeadPivot, pieHeadHeight, headOrigin, scale));
            }
            var flatFaces = meshLayout.FlatFaceIndices;
            var vertices = new Vertex[flatFaces.Length];
            for (var output = 0; output < flatFaces.Length; output++)
            {
                var item = posed[flatFaces[output]];
                var lotPosition = AvatarCoordinates.ToLot(item.Position);
                var worldX = lotPosition.X;
                var worldY = lotPosition.Y;
                var point = camera.Project(worldX, worldY, item.Position.Y * 40);
                var vertexDepth = camera.Depth(worldX, worldY, item.Position.Y);
                if (censorThisBinding)
                {
                    hasCensorBounds = true;
                    if (point.X < censorMinX) censorMinX = point.X;
                    if (point.Y < censorMinY) censorMinY = point.Y;
                    if (point.X > censorMaxX) censorMaxX = point.X;
                    if (point.Y > censorMaxY) censorMaxY = point.Y;
                    if (vertexDepth < censorMinDepth) censorMinDepth = vertexDepth;
                }
                var normal = camera.CameraNormal(new Microsoft.Xna.Framework.Vector3(item.Normal.Z, item.Normal.Y, item.Normal.X));
                if (normal.LengthSquared() > 0) normal.Normalize();
                vertices[output] = new Vertex(point.X, point.Y, vertexDepth,
                    item.UV.X, item.UV.Y, normal.X, normal.Y, normal.Z);
            }
            parts.Add(new Part(texture.Bytes, texture.Sha1Hex, texture.Encoding, texture.Width, texture.Height, vertices));
        }
        if (headMeshes is null)
        {
            if (needsSource is not null)
            {
                foreach (var needsBinding in needsSource.Appearance.Bindings)
                {
                    if (needsBinding.Integers.Length == 0 || (needsBinding.Integers[0] & censorMask) == 0) continue;
                    Mesh needsMesh;
                    try { needsMesh = ReadMesh(needsSource.Path, needsBinding.Mesh); }
                    catch (InvalidDataException) { continue; }
                    if (needsMesh.TextureCoordinates.Count == 0 || needsMesh.TextureCoordinates.Count > 250_000) continue;

                    var needsLayout = GetMeshLayout(needsMesh);
                    if (needsLayout.HasInvalidBoneIndex) continue;
                    if (needsLayout.ResolveSkeleton(needsMesh, skeleton).BoneIndices is not { } needsBoneMap) continue;

                    var needsBoneForVertex = needsLayout.BoneForVertex;
                    var needsBlendBone = needsLayout.BlendBoneForVertex;
                    var needsBlendWeight = needsLayout.BlendWeightForVertex;
                    var needsBlendVertex = needsLayout.BlendVertexForVertex;
                    // SAnimator::Draw (00590c09.c:230-265) projects each deformed primary vertex of the
                    // invisible "<skeleton>-needs" suit without creating visible scene parts (0059e041.c:19-21).
                    for (var vertexIndex = 0; vertexIndex < needsMesh.TextureCoordinates.Count; vertexIndex++)
                    {
                        var matrix = boneMatrices[needsBoneMap[needsBoneForVertex[vertexIndex]]];
                        var position = Vector3.Transform(needsMesh.Vertices[vertexIndex].Position, matrix);
                        var blendBone = needsBlendBone[vertexIndex];
                        if (blendBone >= 0)
                        {
                            var blendMatrix = boneMatrices[needsBoneMap[blendBone]];
                            var blendPosition = Vector3.Transform(needsMesh.Vertices[needsBlendVertex[vertexIndex]].Position, blendMatrix);
                            position = Vector3.Lerp(position, blendPosition, needsBlendWeight[vertexIndex]);
                        }
                        var rootPosition = Vector3.Transform(position, rootTransform);
                        var lotPosition = AvatarCoordinates.ToLot(rootPosition);
                        var point = camera.Project(lotPosition.X, lotPosition.Y, rootPosition.Y * 40);
                        var needsDepth = camera.Depth(lotPosition.X, lotPosition.Y, rootPosition.Y);
                        hasCensorBounds = true;
                        if (point.X < censorMinX) censorMinX = point.X;
                        if (point.Y < censorMinY) censorMinY = point.Y;
                        if (point.X > censorMaxX) censorMaxX = point.X;
                        if (point.Y > censorMaxY) censorMaxY = point.Y;
                        if (needsDepth < censorMinDepth) censorMinDepth = needsDepth;
                    }
                }
            }

            if (hasCensorBounds && censorMinX < censorMaxX && censorMinY < censorMaxY)
            {
                var pelvisLot = pelvisPositions.TryGetValue(person.Id, out var cachedPelvis)
                    ? cachedPelvis
                    : new Vector3(tileX, tileY, altitude + 0.5f);
                var baseCensorDepth = Math.Min(camera.Depth(pelvisLot.X, pelvisLot.Y, pelvisLot.Z) - 0.0013428f, censorMinDepth - 0.0001f);
                var censorDepth = Math.Clamp(baseCensorDepth, 0.001f, 0.999f);
                // cLotRenderer::DrawCensorRects (004ac916.c:64-83) expands censor bounds by 1/2/4 px at zoom 1/2/3
                // (2f in medium-zoom scene units, scaled by LotZoom.factor 0.5/1.0/2.0).
                censorBounds[person.Id] = new CensorBounds(censorMinX - 2f, censorMinY - 2f,
                    censorMaxX - censorMinX + 4f, censorMaxY - censorMinY + 4f, censorDepth);
            }
            else
            {
                censorBounds.Remove(person.Id);
            }
        }
        if (hasSkates)
            AddSkateParts(person, parts, skateFootPositions!, tileX, tileY, altitude, camera);
        if (headMeshes is null && headIndicatorKind is 1 or 2 or 3 && head is not null)
            AddHeadIndicatorPart(person, headIndicatorKind, head, facingMatrix, rootTransform, camera, parts, subTickAlpha);
        return parts;

        void AddPair((string, string) pair, int role)
        {
            var name = pair.Item1.Trim();
            // Sims.exe writes an empty part name as "NONE" (FUN_0058fb9f, 0x6c0fd4) and FUN_0040a5ea sets it for the parts of
            // people that have none, so it reads back as no part.
            if (name.Length == 0 || name.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;
            var equals = pair.Item2.IndexOf('=');
            var filter = equals >= 0 ? pair.Item2[..equals].Trim() : null;
            if (filter?.Length == 0) filter = null;
            var texture = equals >= 0 ? pair.Item2[(equals + 1)..].Trim() : (pair.Item2.Length > 0 ? pair.Item2.Trim() : null);
            if (texture?.Length == 0) texture = null;
            selected.Add((name.EndsWith(".apr", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name, texture, filter, name, role));
        }
    }

    private (float TileX, float TileY, float Altitude, float RootDirection) PrepareRootTransform(
        XPerson person, int level, Func<float, float, float> elevation, LotCamera camera,
        List<PosedHeadMesh>? headMeshes, Vector3? attachmentRoot, float? inheritedRootDirection,
        float subTickAlpha, long updates)
    {
        // SAnimator::FollowOneStep retains floating root coordinates in +0x80/+0x84 while
        // cXPerson placement is rounded back to an FTile point. Use the retained animator
        // coordinates for the rendered root so the avatar does not lose the sub-point part
        // of its movement on each scene build.
        var route = person.NativeRoute;
        var rootFtileX = (person.AnimatorRootX is { } rootX && float.IsFinite(rootX)
            ? rootX
            : route is { } activeRoute && float.IsFinite(activeRoute.MotionX)
                ? activeRoute.MotionX
            : person.Position.X) + person.AnimatorDisplacementX;
        var rootFtileY = (person.AnimatorRootY is { } rootY && float.IsFinite(rootY)
            ? rootY
            : route is { } activeRouteY && float.IsFinite(activeRouteY.MotionY)
                ? activeRouteY.MotionY
            : person.Position.Y) + person.AnimatorDisplacementY;
        if (headMeshes is null && updates > 0)
        {
            if (!lastTickRoots.TryGetValue(person.Id, out var lastRoot) || lastRoot.Updates != updates)
            {
                if (lastRoot.Updates > 0) prevTickRoots[person.Id] = lastRoot;
                lastRoot = (updates, rootFtileX, rootFtileY, level);
                lastTickRoots[person.Id] = lastRoot;
            }
            if (subTickAlpha > 0f && route is { } subRoute && person.AnimationPlayback is { RouteDriven: true } &&
                prevTickRoots.TryGetValue(person.Id, out var prevRoot) && prevRoot.Level == level &&
                prevRoot.Updates == updates - 1)
            {
                var velX = lastRoot.RootX - prevRoot.RootX;
                var velY = lastRoot.RootY - prevRoot.RootY;
                var stepSq = velX * velX + velY * velY;
                var toTargetX = subRoute.MotionTargetX - lastRoot.RootX;
                var toTargetY = subRoute.MotionTargetY - lastRoot.RootY;
                var toTargetSq = toTargetX * toTargetX + toTargetY * toTargetY;
                if (stepSq is > 0.0001f and < 16f && toTargetSq > 0.0001f && (velX * toTargetX + velY * toTargetY) > 0f)
                {
                    var factor = MathF.Min(subTickAlpha, MathF.Sqrt(toTargetSq / stepSq));
                    rootFtileX = lastRoot.RootX + velX * factor;
                    rootFtileY = lastRoot.RootY + velY * factor;
                }
            }
        }
        var tileX = rootFtileX / 16f;
        var tileY = rootFtileY / 16f;
        // UpdateRootPos adds 0.5 and uses fctiwz on each root FTile coordinate before GetFAlt; preserve that 1/16-tile
        // sample point rather than sampling the unrounded animator position (SAnimator.s 0x1034B5E4–0x1034B6A4;
        // cFixedWorld::GetFAlt at world.s 0x10159E50). Elevation uses the native scene terrain sampler.
        var levelDelta = level - 1;
        // GenerateLookupTables writes the level basis (.5, 9.300511..., .5) in source root units. UpdateRootPos
        // rotates that vector around Y by -viewerRotation * pi/2 before adding it to the root. The horizontal
        // components become camera-space lot offsets after the 3/16-to-1/16 conversion, so inverse-rotate them
        // into lot coordinates before LotCamera projects the scene (ZBufferLookup.s 0x101FAC10–0x101FAE44;
        // SAnimator.s 0x1034B740–0x1034B840).
        var levelTileOffset = levelDelta / 6f;
        var levelLotOffset = camera.SourceVector(levelTileOffset, levelTileOffset);
        tileX += levelLotOffset.X;
        tileY += levelLotOffset.Y;
        var altitude = elevation(AvatarCoordinates.ElevationSample(rootFtileX),
                           AvatarCoordinates.ElevationSample(rootFtileY)) / 40 +
                       levelDelta * AnimatorLevelHeight;
        if (attachmentRoot is { } inheritedRoot)
        {
            tileX = inheritedRoot.X;
            tileY = inheritedRoot.Y;
            altitude = inheritedRoot.Z;
        }
        avatarRootPositions[person.Id] = new Vector3(tileX, tileY, altitude);
        // Keep the retained animator direction for the native facing transform, with the parent's direction taking
        // precedence for an animated Sim attached through the parent's HEAD object slot. Mac UpdateRootPos converts
        // root direction +0x88 and displacement direction +0x94 to (direction + 6) * PI/4; the native angle below is
        // equivalent modulo 2 PI. This transform stays in lot coordinates; camera.Project below applies viewer
        // orientation to each resulting vertex. Mac rotates coordinates in its own viewer-space transform
        // (SAnimator.s 0x1034B61C–0x1034B990), so applying camera rotation to facingMatrix as well would rotate it twice.
        var rootDirection = AvatarDirection.RootDirection(person.AnimatorRootDirection,
            person[ObjectData.Direction], person.AnimatorDisplacementDirection, inheritedRootDirection);
        avatarRootDirections[person.Id] = rootDirection;
        return (tileX, tileY, altitude, rootDirection);
    }

    private string? ResolveCategorySuit(IReadOnlyList<string>? values, int index, char category, int? fallbackIndex = null)
    {
        if (values is null) return null;
        static string StripComment(string value)
        {
            var semicolon = value.IndexOf(';');
            return (semicolon >= 0 ? value[..semicolon] : value).Trim();
        }
        var existing = values.Count > index ? StripComment(values[index]) : "";
        if (existing.Equals("ADDED", StringComparison.OrdinalIgnoreCase)) existing = "";
        if (existing.Length == 0 && fallbackIndex is { } fallback && values.Count > fallback)
        {
            existing = StripComment(values[fallback]);
            if (existing.Equals("ADDED", StringComparison.OrdinalIgnoreCase)) existing = "";
        }
        if (existing.Length > 0) return existing;
        if (values.Count <= 14 || values.Count <= 1) return null;

        // Body Strings entry 1 stores the avatar type prefix after "xskin-"/"bskin-" (index 4 up to '_'),
        // and derives the texture prefix from the outfit name before '_' plus the selected skin.
        var avatarType = StripComment(values[1]).ToLowerInvariant();
        if (avatarType.Length <= 4) return null;
        avatarType = avatarType[4..];
        var separator = avatarType.IndexOf('_');
        if (separator >= 0) avatarType = avatarType[..separator];
        var skin = StripComment(values[14]).ToLowerInvariant();
        if (avatarType.Length == 0 || skin.Length == 0) return null;
        var texturesByPrefix = textures.Files.Select(file => Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant()).ToArray();

        foreach (var name in appearances.Keys)
        {
            var candidate = name.ToLowerInvariant();
            if (candidate.Length < 5 || candidate[0] != category ||
                !char.IsDigit(candidate[1]) || !char.IsDigit(candidate[2]) || !char.IsDigit(candidate[3])) continue;
            var underscore = candidate.IndexOf('_');
            var candidateType = candidate[4..(underscore < 0 ? candidate.Length : underscore)];
            if (candidateType.EndsWith("lgt", StringComparison.Ordinal) || candidateType.EndsWith("med", StringComparison.Ordinal) ||
                candidateType.EndsWith("drk", StringComparison.Ordinal)) candidateType = candidateType[..^3];
            if (!candidateType.Equals(avatarType, StringComparison.Ordinal)) continue;
            var idEnd = candidate.IndexOf('_');
            if (idEnd < 0) idEnd = candidate.Length;
            var prefix = candidate[..idEnd] + skin;
            var texture = texturesByPrefix.FirstOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
            if (texture is not null) return $"{candidate},BODY={texture}";
        }
        return null;
    }

    /// <summary>Implements SAnimator::UpdateSuits (0058a17c.c:33-250): resolves single-suit pet/collar entries
    /// when OBJD VitaBoyFlags bit 0x8 is set, active human outfits from PersonData.CurrentOutfit, and hand gesture
    /// appearances from PersonData.HandGestureLeft / HandGestureRight against the Sim's BodyStrings table, while
    /// preserving PresentationCharacter overrides when CurrentOutfit is 0.</summary>
    private ((string, string) Body, (string, string) LeftHand, (string, string) RightHand, (string, string) Head) ResolveEffectiveParts(
        XPerson person, IReadOnlyList<(string, string)> parts)
    {
        var body = parts[0];
        var leftHand = parts[2];
        var rightHand = parts[3];
        var head = parts[4];

        var values = ReadBodyStrings(person);
        var objectEntry = person.TypeRecord is { } type ? registry.Find(unchecked((uint)type.Key)) : null;
        // SAnimator::UpdateSuits (0058a17c.c:33-59): single-suit actors (pets/creatures with OBJD +0xCC bit 0x8)
        // index CurrentOutfit directly into BodyStrings (1-based CurrentOutfit + 1 == 0-based CurrentOutfit) and
        // bypass human outfit categories and hand-gesture slots. Runs before AnimatorBodyStringMode checks.
        if (objectEntry is not null && (objectEntry.Definition[ObjdField.VitaBoyFlags] & 8) != 0)
        {
            if (IsUnsetPart(body.Item1))
            {
                var fallbackPetBody = BodyString(values, 1);
                if (fallbackPetBody.Length > 0 && !fallbackPetBody.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                    body = ParseSuitSpecification(fallbackPetBody);
            }
            var petOutfit = (int)person[PersonData.CurrentOutfit];
            if (person.PresentationCharacter is null && petOutfit > 0)
            {
                var petSuit = BodyString(values, petOutfit);
                if (petSuit.Length > 0 && !petSuit.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                    body = ParseSuitSpecification(petSuit);
            }
            return (body, leftHand, rightHand, head);
        }

        var defaultLeftHand = BodyString(values, 17);
        var defaultRightHand = BodyString(values, 18);
        if (person.PresentationCharacter is null)
        {
            if (IsUnsetPart(body.Item1))
            {
                var fallbackBody = BodyString(values, 1);
                if (fallbackBody.Length > 0) body = ParseSuitSpecification(fallbackBody);
            }
            if (IsUnsetPart(head.Item1))
            {
                var fallbackHead = BodyString(values, 2);
                if (fallbackHead.Length > 0) head = ParseSuitSpecification(fallbackHead);
            }
            // SAnimator::UpdateSuits (0058a17c.c:118-178) restores idle hands (gesture 0) from BodyStrings 18/19 (0-based 17/18)
            // even when SavedCharacter previously serialized "NONE" while carrying an item (gesture 3).
            if (defaultLeftHand.Length > 0)
                leftHand = ParseSuitSpecification(defaultLeftHand);
            if (defaultRightHand.Length > 0)
                rightHand = ParseSuitSpecification(defaultRightHand);
        }

        // SAnimator::UpdateSuits (0058a17c.c:67-69): AnimatorBodyStringMode bits 0x6 force CurrentOutfit to 0
        // without bypassing BodyStrings default body/head/hand resolution or hand gestures.
        var currentOutfit = (person.AnimatorBodyStringMode & 6) != 0 ? 0 : (int)person[PersonData.CurrentOutfit];
        if (currentOutfit != 0)
        {
            if (defaultLeftHand.Length > 0) leftHand = ParseSuitSpecification(defaultLeftHand);
            if (defaultRightHand.Length > 0) rightHand = ParseSuitSpecification(defaultRightHand);
            switch (currentOutfit)
            {
                case 1:
                    if (ResolveCategorySuit(values, 15, 'f') is { } formal && formal.Length > 0)
                        body = ParseSuitSpecification(formal);
                    break;
                case 2:
                    if (ResolveCategorySuit(values, 31, 's', fallbackIndex: 16) is { } swim && swim.Length > 0)
                        body = ParseSuitSpecification(swim);
                    break;
                case 4:
                {
                    var nudeBody = BodyString(values, 27);
                    var nudeLeft = BodyString(values, 28);
                    var nudeRight = BodyString(values, 29);
                    var nudeOverride = BodyString(values, 30);
                    if (int.TryParse(BodyString(values, 13), NumberStyles.Integer, CultureInfo.InvariantCulture, out var age) && age < 18)
                    {
                        if (nudeBody.Length > 0 && !nudeBody.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                            body = ParseSuitSpecification(nudeBody);
                    }
                    else if (nudeOverride.Length > 0 && !nudeOverride.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                    {
                        body = ParseSuitSpecification(nudeOverride);
                    }
                    else
                    {
                        if (nudeBody.Length > 0 && !nudeBody.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                            body = ParseSuitSpecification(nudeBody);
                        if (nudeLeft.Length > 0 && !nudeLeft.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                            leftHand = ParseSuitSpecification(nudeLeft);
                        if (nudeRight.Length > 0 && !nudeRight.Equals("ADDED", StringComparison.OrdinalIgnoreCase))
                            rightHand = ParseSuitSpecification(nudeRight);
                    }
                    break;
                }
                case 5:
                    if (ResolveCategorySuit(values, 32, 'l') is { } pajamas && pajamas.Length > 0)
                        body = ParseSuitSpecification(pajamas);
                    break;
                case 17:
                    if (ResolveCategorySuit(values, 33, 'w') is { } winter && winter.Length > 0)
                        body = ParseSuitSpecification(winter);
                    break;
                case 18:
                    if (ResolveCategorySuit(values, 34, 'h') is { } highFashion && highFashion.Length > 0)
                        body = ParseSuitSpecification(highFashion);
                    break;
            }
        }

        leftHand = (int)person[PersonData.HandGestureLeft] switch
        {
            1 => ResolveGesturePart(values, 19, leftHand),
            2 => ResolveGesturePart(values, 21, leftHand),
            3 => ("NONE", ""),
            4 => ResolveGesturePart(values, 25, leftHand),
            _ => leftHand,
        };
        rightHand = (int)person[PersonData.HandGestureRight] switch
        {
            1 => ResolveGesturePart(values, 20, rightHand),
            2 => ResolveGesturePart(values, 22, rightHand),
            3 => ("NONE", ""),
            4 => ResolveGesturePart(values, 26, rightHand),
            _ => rightHand,
        };
        return (body, leftHand, rightHand, head);

        static bool IsUnsetPart(string name) =>
            string.IsNullOrWhiteSpace(name) || name.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase);

        // FUN_00455edc (00455edc.c:27-30) strips inline ';' comments before splitting ',' and '='.
        static string BodyString(IReadOnlyList<string>? table, int index)
        {
            if (table is null || table.Count <= index) return "";
            var raw = table[index];
            var semicolon = raw.IndexOf(';');
            return (semicolon >= 0 ? raw[..semicolon] : raw).Trim();
        }

        static (string, string) ResolveGesturePart(IReadOnlyList<string>? table, int index, (string, string) fallback)
        {
            var raw = BodyString(table, index);
            return raw.Length > 0 ? ParseSuitSpecification(raw) : fallback;
        }

        static (string, string) ParseSuitSpecification(string specification)
        {
            var comma = specification.IndexOf(',');
            var appearance = (comma >= 0 ? specification[..comma] : specification).Trim();
            if (!appearance.Equals("NONE", StringComparison.OrdinalIgnoreCase) &&
                !appearance.EndsWith(".apr", StringComparison.OrdinalIgnoreCase))
                appearance += ".apr";
            var binding = comma >= 0 ? specification[(comma + 1)..].Trim() : "";
            return (appearance, binding);
        }
    }

    internal Vector3? HeadPosition(XPerson person) => headPositions.TryGetValue(person.Id, out var position) ? position : null;

    internal Vector3? HeadAttachmentPosition(XPerson person) =>
        headAttachmentPositions.TryGetValue(person.Id, out var position) ? position : null;

    internal Vector3? HandPosition(XPerson person) => handPositions.TryGetValue(person.Id, out var position) ? position : null;

    internal Vector3? LeftHandPosition(XPerson person) =>
        leftHandPositions.TryGetValue(person.Id, out var position) ? position : null;

    internal Vector3? PelvisPosition(XPerson person) =>
        pelvisPositions.TryGetValue(person.Id, out var position) ? position : null;

    /// <summary>Maps cXPerson's hierarchy slots to VitaBoy registrations per cXPerson::Draw (00459f0f.c:25-177),
    /// SAnimator::GetRegistration (005928fd.c), and SAnimator::Tick child-animator attachment (0058dc76.c:149-209):
    /// slots 1 and 5 -> R_HAND Palm, slot 2 -> HEAD bone, slot 3 -> PELVIS, slot 4 -> HEAD Top.</summary>
    internal Vector3? SlotAttachmentPosition(XPerson person, int hierarchySlot) => hierarchySlot switch
    {
        1 or 5 => HandPosition(person),
        2 => HeadPosition(person),
        3 => PelvisPosition(person) ?? LeftHandPosition(person),
        4 => HeadAttachmentPosition(person),
        _ => null,
    };

    internal CensorBounds? Censor(XPerson person) =>
        censorBounds.TryGetValue(person.Id, out var bounds) ? bounds : null;

    private IReadOnlyList<string>? ReadBodyStrings(XPerson person)
    {
        var objectEntry = person.TypeRecord is { } type ? registry.Find(unchecked((uint)type.Key)) : null;
        var stringsId = objectEntry?.Definition[ObjdField.BodyStringsId] ?? 0;
        if (stringsId == 0) return null;
        var key = (objectEntry!.File, stringsId);
        if (bodyStringsCache.TryGetValue(key, out var cached)) return cached;
        var bodyChunk = objectEntry.File.Find(ResourceType.Str, stringsId);
        if (bodyChunk is null) return null;
        var parsed = StringTable.Read(objectEntry.File, bodyChunk).ForLanguage(1);
        bodyStringsCache[key] = parsed;
        return parsed;
    }

    internal Vector3? HeadSeekRegistration(XPerson person) =>
        headSeekRegistrations.TryGetValue(person.Id, out var position) ? position : null;

    internal Vector3? HeadFakeRegistration(XPerson person) =>
        headFakeRegistrations.TryGetValue(person.Id, out var position) ? position : null;

    internal void ClearHeadSeekRegistrations(XPerson person)
    {
        headSeekRegistrations.Remove(person.Id);
        headFakeRegistrations.Remove(person.Id);
        person.AnimatorHeadSeekRegistrationsAvailable = false;
    }

    /// <summary>Builds the two registrations consumed by SAnimator::UpdateHeadDirection. SetupRegistrations uses
    /// Body Strings entries 4/5 for +0x13c, and entry 4 with the authored HeadFake suffix for +0x144
    /// (Mac SAnimator.s 0x1034B2F0–0x1034B45C). A missing body-strings resource leaves the registration absent,
    /// matching FindRegistration returning null for a missing bone.</summary>
    private void CacheHeadSeekRegistrations(XPerson person, IReadOnlyList<BonePose> pose, SkeletonInfo skeletonInfo,
        IReadOnlyList<string>? values, Matrix4x4 rootTransform)
    {
        ClearHeadSeekRegistrations(person);
        if (values is null || values.Count <= 4) return;

        var actor = Registration(values[3], values[4]);
        if (actor is { } actorPosition) headSeekRegistrations[person.Id] = actorPosition;
        else headSeekRegistrations.Remove(person.Id);
        var headFake = Registration(values[3], "HeadFake");
        if (headFake is { } headFakePosition) headFakeRegistrations[person.Id] = headFakePosition;
        else headFakeRegistrations.Remove(person.Id);
        person.AnimatorHeadSeekRegistrationsAvailable = actor.HasValue && headFake.HasValue;

        Vector3? Registration(string boneName, string? suffix)
        {
            if (!skeletonInfo.IndexByNameOrdinal.TryGetValue(boneName, out var boneIdx)) return null;
            var position = Pose.RegistrationTranslation(pose[boneIdx], suffix);
            return Vector3.Transform(position, rootTransform);
        }
    }

    /// <summary>Collects facing-free posed head meshes for the source <c>MakeSpeakingIcon</c> fallback without
    /// disturbing the scene build: <c>Build</c> refreshes the per-Sim head/root caches, which the scene path retains
    /// for placement and attachment resolution, so the prior entries are restored afterwards.</summary>
    internal List<PosedHeadMesh> BuildHeadMeshes(XPerson person, int level, int cameraOrientation,
        Func<float, float, float> elevation, LotCamera camera)
    {
        var hadHead = headPositions.TryGetValue(person.Id, out var head);
        var hadHeadAttachment = headAttachmentPositions.TryGetValue(person.Id, out var headAttachment);
        var hadHand = handPositions.TryGetValue(person.Id, out var hand);
        var hadLeftHand = leftHandPositions.TryGetValue(person.Id, out var leftHand);
        var hadPelvis = pelvisPositions.TryGetValue(person.Id, out var pelvis);
        var hadRoot = avatarRootPositions.TryGetValue(person.Id, out var root);
        var hadDirection = avatarRootDirections.TryGetValue(person.Id, out var direction);
        var hadHeadSeek = headSeekRegistrations.TryGetValue(person.Id, out var headSeek);
        var hadHeadFake = headFakeRegistrations.TryGetValue(person.Id, out var headFake);
        var hadHeadSeekRegistrationsAvailable = person.AnimatorHeadSeekRegistrationsAvailable;
        try
        {
            var meshes = new List<PosedHeadMesh>();
            Build(person, level, cameraOrientation, elevation, camera, meshes);
            return meshes;
        }
        finally
        {
            if (hadHead) headPositions[person.Id] = head; else headPositions.Remove(person.Id);
            if (hadHeadAttachment) headAttachmentPositions[person.Id] = headAttachment; else headAttachmentPositions.Remove(person.Id);
            if (hadHand) handPositions[person.Id] = hand; else handPositions.Remove(person.Id);
            if (hadLeftHand) leftHandPositions[person.Id] = leftHand; else leftHandPositions.Remove(person.Id);
            if (hadPelvis) pelvisPositions[person.Id] = pelvis; else pelvisPositions.Remove(person.Id);
            if (hadRoot) avatarRootPositions[person.Id] = root; else avatarRootPositions.Remove(person.Id);
            if (hadDirection) avatarRootDirections[person.Id] = direction; else avatarRootDirections.Remove(person.Id);
            if (hadHeadSeek) headSeekRegistrations[person.Id] = headSeek; else headSeekRegistrations.Remove(person.Id);
            if (hadHeadFake) headFakeRegistrations[person.Id] = headFake; else headFakeRegistrations.Remove(person.Id);
            person.AnimatorHeadSeekRegistrationsAvailable = hadHeadSeekRegistrationsAvailable;
        }
    }

    internal float? RootDirection(XPerson person) => avatarRootDirections.TryGetValue(person.Id, out var direction) ? direction : null;

    internal Vector3 RootPosition(XPerson person) => avatarRootPositions.TryGetValue(person.Id, out var position)
        ? position
        : throw new InvalidOperationException($"Sim {person.Id} avatar root position was not prepared");

    private IReadOnlyList<BonePose> AnimationPose(XPerson person, Skeleton skeleton, float subTickAlpha = 0f)
    {
        var pose = Pose.BindPose(skeleton);
        foreach (var practice in person.GetAnimationPracticeApplicationOrder()) pose = Sample(practice, pose);
        return pose;

        IReadOnlyList<BonePose> Sample(AvatarAnimationPlayback playback, IReadOnlyList<BonePose> basis)
        {
            if (ResolveAnimation(playback.Clip.Name) is not { } source)
                throw new InvalidDataException($"Sim {person.Id} is playing {playback.Clip.Name}, but its original animation curves are not installed");

            var sampleFrame = playback.Frame;
            if (subTickAlpha > 0f && !playback.EndReached && playback.InterpolatePose &&
                playback.Clip.DurationMilliseconds > 0f && playback.Clip.FrameCount > 0)
            {
                var frameCount = (float)playback.Clip.FrameCount;
                sampleFrame += (subTickAlpha * 42f * playback.PracticeScale / playback.Clip.DurationMilliseconds) * frameCount;
                if (playback.Loop)
                {
                    sampleFrame %= frameCount;
                    if (sampleFrame < 0f) sampleFrame += frameCount;
                }
                else
                {
                    sampleFrame = Math.Clamp(sampleFrame, 0f, Math.Max(0f, frameCount - 1f));
                }
            }
            return Pose.Sample(skeleton, source.Animation, source.Curves, sampleFrame, basis, playback.PracticeWeight,
                playback.Loop, playback.InterpolatePose);
        }
    }

    /// <summary>Computes the two cached SAnimator strides from the active skill's marker bones on the same skeleton
    /// selected for this Sim's body appearance. StartVitaBoy evaluates these at the authored marker frames.</summary>
    internal (float MarkerOneStride, float MarkerTwoStride)? LocomotionStrides(XPerson person, string animationName)
    {
        if ((person.PresentationCharacter ?? person.SavedCharacter) is not { Parts.Count: 5 } character ||
            ResolveAnimation(animationName) is not { } animationSource)
            return null;

        var (bodyPair, leftHandPair, rightHandPair, headPair) = ResolveEffectiveParts(person, character.Parts);
        var bodyName = NormalizeAppearance(bodyPair.Item1);
        var body = ResolveAppearance(bodyName);
        if (body is null) return null;

        try
        {
            var coreNames = new[] { leftHandPair.Item1, rightHandPair.Item1, headPair.Item1 }
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Equals("NONE", StringComparison.OrdinalIgnoreCase));
            var sourceList = new List<AppearanceSource> { body };
            foreach (var name in coreNames)
            {
                var normalized = NormalizeAppearance(name);
                var source = ResolveAppearance(normalized);
                if (source is null && !normalized.StartsWith('h')) return null;
                if (source is not null) sourceList.Add(source);
            }
            var requiredBones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sourceList)
                foreach (var binding in source.Appearance.Bindings)
                    requiredBones.UnionWith(ReadMesh(source.Path, binding.Mesh).Bones);
            var skeleton = SelectSkeleton(body, requiredBones, person);
            var gameSkeleton = skeleton;
            var markerOne = LocomotionMarker(animationSource.Animation, "1");
            var markerTwo = LocomotionMarker(animationSource.Animation, "2");
            if (markerOne is not { Bone.Length: > 0 } || markerTwo is not { Bone.Length: > 0 }) return null;
            var frameCount = animationSource.Animation.PlaybackFrameCount;
            if (frameCount <= 0) return null;
            // Character bones and CFP channels are evaluated in their authored Sims.exe coordinate space.
            return Pose.LocomotionStrides(gameSkeleton, animationSource.Animation, animationSource.Curves,
                markerOne.Value.NormalizedTime * frameCount, markerOne.Value.Bone,
                markerTwo.Value.NormalizedTime * frameCount, markerTwo.Value.Bone);
        }
        catch (InvalidDataException)
        {
            // The selected skeleton may omit a marker track/bone. StartVitaBoy cannot derive its source stride then.
            return null;
        }

        static AvatarAnimationMarker? LocomotionMarker(Animation animation, string value) =>
            animation.FindTagTime("anchor", value) is { } tag
                ? new AvatarAnimationMarker(tag.Time / animation.Floats[0], tag.Bone)
                : null;

        AppearanceSource? ResolveAppearance(string name)
        {
            var source = appearances.GetValueOrDefault(name);
            if (source is not null) return source;
            if (name.StartsWith('b'))
                return BodyFallback(name) is { } bodyFallback ? appearances.GetValueOrDefault(bodyFallback) : null;
            return name.StartsWith('c') ? HeadFallback(name) : null;
        }
    }

    private AnimationSource? ResolveAnimation(string name)
    {
        if (animations.TryGetValue(name, out var cached)) return cached;
        var curvesIndex = curveFiles ?? sharedIndex.CurveFiles;
        if (sharedIndex.AnimationsByName.TryGetValue(name, out var animationCandidates))
        {
            for (var i = 0; i < animationCandidates.Count; i++)
            {
                var animation = animationCandidates[i];
                if (!curvesIndex.TryGetValue(animation.CurveName + ".cfp", out var candidates)) continue;
                for (var c = 0; c < candidates.Length; c++)
                {
                    var candidate = candidates[c];
                    try
                    {
                        var curves = AnimationCurveFile.Read(candidate.Read(), candidate.Source + ":" + candidate.Name,
                            animation.TranslationCount, animation.RotationCount);
                        return animations[name] = new AnimationSource(animation, curves);
                    }
                    catch (ResourceFormatException) { }
                }
            }
        }
        animations[name] = null;
        return null;
    }

    private string? BodyFallback(string name) => BodyFallbackByMarker(Category(name));

    private string? BodyFallbackByMarker(string marker)
    {
        var common = marker switch
        {
            "ma" => "b007mafit_01",
            "fa" => "b010fafit_01",
            _ => null,
        };
        if (common is not null && appearances.ContainsKey(common)) return common;
        return appearances.Values.FirstOrDefault(source => source.Appearance.Name.StartsWith('b') &&
            source.Appearance.Name.Length > 5 && Category(source.Appearance.Name) == marker &&
            source.Appearance.Bindings.Any(binding => binding.Mesh.Contains("BODY", StringComparison.OrdinalIgnoreCase)))?.Appearance.Name
            ?? appearances.Values.FirstOrDefault(source => source.Appearance.Name.StartsWith('b') &&
                source.Appearance.Bindings.Any(binding => binding.Mesh.Contains("BODY", StringComparison.OrdinalIgnoreCase)))?.Appearance.Name;
    }

    private AppearanceSource? HeadFallback(string name) => HeadFallbackByMarker(Category(name));

    private AppearanceSource? HeadFallbackByMarker(string marker) =>
        appearances.Values.FirstOrDefault(source => source.Appearance.Name.StartsWith('c') &&
            source.Appearance.Name.Length > 5 && Category(source.Appearance.Name) == marker &&
            source.Appearance.Bindings.Any(binding => binding.Mesh.Contains("HEAD-HEAD", StringComparison.OrdinalIgnoreCase)))
        ?? appearances.Values.FirstOrDefault(source => source.Appearance.Name.StartsWith('c') &&
            source.Appearance.Bindings.Any(binding => binding.Mesh.Contains("HEAD-HEAD", StringComparison.OrdinalIgnoreCase)));

    private static string PersonFallbackMarker(XPerson person)
    {
        if (person.AnimatorBodyStringMode is 2 or 3) return "dg";
        if (person.AnimatorBodyStringMode is 4 or 5) return "ct";
        var isChild = PersonAgeCategory.IsChild(person[PersonData.PersonAge]);
        var isFemale = person[PersonData.Gender] == 1;
        return (isChild, isFemale) switch
        {
            (true, true) => "fc",
            (true, false) => "mc",
            (false, true) => "fa",
            _ => "ma",
        };
    }

    private static string Category(string name) => name.Length >= 6 ? name.Substring(4, 2).ToLowerInvariant() : "";

    private Skeleton SelectSkeleton(AppearanceSource body, HashSet<string> requiredBones, XPerson person)
    {
        // SAnimator::StartVitaBoy (00588351.c:20-36) reads the skeleton name from BodyStrings 1-based entry 1
        // (0-based index 0: "adult", "child", "dog", "cat") and overrides it to "child" when 0 < PersonAge < 18.
        var bodyAppearanceCategory = Category(body.Appearance.Name);
        var isChildAppearance = bodyAppearanceCategory is "mc" or "fc" or "uc" ||
            body.Appearance.Name.Contains("Chd_", StringComparison.OrdinalIgnoreCase);
        var bodyStrings = ReadBodyStrings(person);
        var bodySkeleton = bodyStrings is { Count: > 0 } ? bodyStrings[0].Trim() : "";
        var preferred = PersonAgeCategory.IsChild(person[PersonData.PersonAge]) || isChildAppearance
            ? "child"
            : !string.IsNullOrWhiteSpace(bodySkeleton)
                ? bodySkeleton
                : person.AnimatorBodyStringMode is 2 or 3
                    ? "dog"
                    : person.AnimatorBodyStringMode is 4 or 5
                        ? "cat"
                        : "adult";
        Skeleton? bestSkeleton = null;
        var bestPreferred = false;
        var bestCoverage = -1;
        HashSet<string>? seenBodySignatures = body.Data.Skeletons.Count > 0
            ? new HashSet<string>(body.Data.Skeletons.Count, StringComparer.Ordinal)
            : null;
        for (var i = 0; i < body.Data.Skeletons.Count; i++)
        {
            var info = GetSkeletonInfo(body.Data.Skeletons[i]);
            if (!seenBodySignatures!.Add(info.Signature)) continue;
            EvaluateCandidate(info);
        }
        for (var i = 0; i < uniqueSkeletons.Count; i++)
        {
            var info = uniqueSkeletons[i];
            if (seenBodySignatures is not null && seenBodySignatures.Contains(info.Signature)) continue;
            EvaluateCandidate(info);
        }
        return bestSkeleton
            ?? throw new InvalidDataException($"No installed avatar skeleton covers Sim {person.Id}'s saved mesh bones");

        void EvaluateCandidate(SkeletonInfo info)
        {
            if (!info.BoneSet.IsSupersetOf(requiredBones)) return;
            var isPreferred = info.Skeleton.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase);
            if (bestSkeleton is not null && bestPreferred && !isPreferred) return;
            var coverage = 0;
            var bones = info.Skeleton.Bones;
            for (var b = 0; b < bones.Count; b++)
                if (requiredBones.Contains(bones[b].Name)) coverage++;
            if (bestSkeleton is null || (isPreferred && !bestPreferred) || coverage > bestCoverage)
            {
                bestSkeleton = info.Skeleton;
                bestPreferred = isPreferred;
                bestCoverage = coverage;
            }
        }
    }

    private Mesh ReadMesh(CharacterSearchPath path, string name)
    {
        CharacterFile resource;
        try
        {
            resource = Find(path, name, ".skn", ".bmf");
        }
        catch (InvalidDataException)
        {
            animationPath ??= CharacterSearchPath.Animation(gameRoot, registry);
            var fallbackPath = ReferenceEquals(path, skins) ? animationPath : skins;
            resource = Find(fallbackPath, name, ".skn", ".bmf");
        }
        if (meshes.TryGetValue(resource.Source, out var cached)) return cached;
        var bytes = resource.Read();
        var parsed = resource.Extension.Equals(".skn", StringComparison.OrdinalIgnoreCase)
            ? Mesh.ReadText(bytes, resource.Source)
            : Mesh.ReadBinary(bytes, resource.Source);
        meshes.Add(resource.Source, parsed);
        return parsed;
    }

    private static TextureSource CreateSolidWhiteTexture()
    {
        var bytes = new byte[4 * 4 * 4];
        Array.Fill(bytes, (byte)255);
        return new TextureSource(bytes, Convert.ToHexString(SHA1.HashData(bytes), 0, 6), "rgba", 4, 4, "__white_fallback");
    }

    private TextureSource ReadTexture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Saved avatar binding has no texture name");
        var resource = Find(textures, name, ".bmp", ".tga");
        if (textureCache.TryGetValue(resource.Source, out var cached)) return cached;
        var bytes = resource.Read().ToArray();
        int width, height;
        if (resource.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 54 && bytes[0] == 'B' && bytes[1] == 'M')
        {
            width = BitConverter.ToInt32(bytes, 18);
            height = Math.Abs(BitConverter.ToInt32(bytes, 22));
        }
        else if (resource.Extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 18)
        {
            width = BitConverter.ToUInt16(bytes, 12);
            height = BitConverter.ToUInt16(bytes, 14);
        }
        else throw new InvalidDataException($"Avatar texture {name} is not a readable BMP or TGA");
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException($"Avatar texture {name} has invalid dimensions");
        var result = new TextureSource(bytes, Convert.ToHexString(SHA1.HashData(bytes), 0, 6), resource.Extension[1..].ToLowerInvariant(), width, height, resource.Name);
        textureCache.Add(resource.Source, result);
        return result;
    }

    /// <summary>Computes the Plumbob diffuse RGB color from the parent Sim's truncated integer Mood motive
    /// matching VitaBoy::Draw (0058da16.c:98, 0058dc76.c:203-205, 00585fbd.c:90-162).
    /// Kind 1 is head-arrow (selected Sim), kind 2 is head-arrow-date (Hot Date companion, swaps G and B),
    /// and kind 3 is head-star (Superstar non-selected celebrity, solid gold).</summary>
    internal static (byte R, byte G, byte B) PlumbobColor(int indicatorKind, int mood)
    {
        var r = 1.0f;
        var g = 1.0f;
        var b = 1.0f;
        if (indicatorKind == 3)
        {
            b = 0.0f;
        }
        else
        {
            var fVar3 = Math.Clamp(mood * 0.01f, -1.0f, 1.0f);
            if (fVar3 >= 0.0f)
            {
                r = 1.0f - fVar3;
                b = 1.0f - fVar3;
                if (fVar3 < 0.5f) b -= 0.5f - fVar3;
            }
            else
            {
                var fVar4 = 1.0f - fVar3 * fVar3;
                b = fVar4;
                g = fVar4;
                if (fVar3 > -0.5f) b -= fVar3 + 0.5f;
            }
            if (indicatorKind == 2)
                (g, b) = (b, g);
        }
        return (
            (byte)Math.Clamp((int)MathF.Round(r * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(g * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(b * 255f), 0, 255));
    }

    private void AddHeadIndicatorPart(XPerson person, int indicatorKind, BonePose head,
        Matrix4x4 facingMatrix, Matrix4x4 parentRootTransform, LotCamera camera, List<Part> parts,
        float subTickAlpha = 0f)
    {
        var appearanceName = indicatorKind switch
        {
            1 => "head-arrow",
            2 => "head-arrow-date",
            3 => "head-star",
            _ => null,
        };
        if (appearanceName is null || !appearances.TryGetValue(appearanceName, out var source)) return;

        var colorKey = PlumbobColor(indicatorKind, (int)person[Motive.Mood]);
        if (!plumbobTextures.TryGetValue(colorKey, out var plumbobTex))
        {
            var bytes = new byte[4 * 4 * 4];
            for (var i = 0; i < bytes.Length; i += 4)
            {
                bytes[i] = colorKey.R;
                bytes[i + 1] = colorKey.G;
                bytes[i + 2] = colorKey.B;
                bytes[i + 3] = 255;
            }
            plumbobTex = (bytes, Convert.ToHexString(SHA1.HashData(bytes), 0, 6));
            plumbobTextures[colorKey] = plumbobTex;
        }
        var textureBytes = plumbobTex.Bytes;
        var textureHash = plumbobTex.Hash;

        // VitaBoy::Draw (00585fbd.c:163-179) sets the synthetic "root" skeleton's single ROOT bone to
        // translation (0, 3.0, 0) and Euler rotation (PI/2, 0, 2*PI * elapsedMs * 0.001 * 0.5).
        var elapsedMs = person.AnimatorMilliseconds + subTickAlpha * 42f;
        var spinAngle = (MathF.PI + MathF.PI) * elapsedMs * 0.001f * 0.5f;
        var sx = MathF.Sin(MathF.PI * 0.25f);
        var cx = MathF.Cos(MathF.PI * 0.25f);
        var sz = MathF.Sin(spinAngle * 0.5f);
        var cz = MathF.Cos(spinAngle * 0.5f);
        var localRotation = new Quaternion(sx * cz, sx * sz, cx * sz, cx * cz);
        var worldRotation = Quaternion.Conjugate(localRotation);
        var boneMatrix = Matrix4x4.CreateFromQuaternion(Pose.BoneMatrixRotation(worldRotation));
        boneMatrix.M42 = 3.0f;

        // SAnimator::Tick (0058da16.c:83-94, 0058dc76.c:189-202) places the indicator animator at the parent
        // Sim's HEAD bone world position with the parent's root heading and the indicator's 1/3 VitaBoy scale.
        var headRoot = Vector3.Transform(head.WorldTranslation, parentRootTransform);
        var indicatorRootTransform = facingMatrix * Matrix4x4.CreateScale(1f / 3f) *
                                     Matrix4x4.CreateTranslation(headRoot);

        foreach (var binding in source.Appearance.Bindings)
        {
            var mesh = ReadMesh(source.Path, binding.Mesh);
            if (mesh.Faces.Count == 0 || mesh.Faces.Count * 3 > 250_000) continue;
            var posed = new (Vector3 Position, Vector3 Normal, Vector2 UV)[mesh.TextureCoordinates.Count];
            for (var vertexIndex = 0; vertexIndex < posed.Length; vertexIndex++)
            {
                var vertex = mesh.Vertices[vertexIndex];
                var position = Vector3.Transform(vertex.Position, boneMatrix);
                var normal = Vector3.TransformNormal(vertex.Normal, boneMatrix);
                normal = Vector3.TransformNormal(normal, facingMatrix);
                var rootPosition = Vector3.Transform(position, indicatorRootTransform);
                posed[vertexIndex] = (rootPosition, normal, mesh.TextureCoordinates[vertexIndex]);
            }
            var flatFaces = GetMeshLayout(mesh).FlatFaceIndices;
            var vertices = new Vertex[flatFaces.Length];
            for (var output = 0; output < flatFaces.Length; output++)
            {
                var item = posed[flatFaces[output]];
                var lotPosition = AvatarCoordinates.ToLot(item.Position);
                var worldX = lotPosition.X;
                var worldY = lotPosition.Y;
                var point = camera.Project(worldX, worldY, item.Position.Y * 40);
                var normal = camera.CameraNormal(new Microsoft.Xna.Framework.Vector3(item.Normal.Z, item.Normal.Y, item.Normal.X));
                if (normal.LengthSquared() > 0) normal.Normalize();
                vertices[output] = new Vertex(point.X, point.Y, camera.Depth(worldX, worldY, item.Position.Y),
                    item.UV.X, item.UV.Y, normal.X, normal.Y, normal.Z);
            }
            parts.Add(new Part(textureBytes, textureHash, "rgba", 4, 4, vertices, IgnoreRoomLighting: true));
        }
    }

    /// <summary>Builds the source skate blades using named ankle-grip CRES frames. The accessory CRES roots are
    /// identity, and the Person SLOT names link those frames to the skeleton. Convert the complete source slot-to-foot
    /// transform into the native actor basis and apply it to this Sim's animated foot. The numeric BHAV IDs 20/21 remain
    /// source interaction operands; resolving them to anatomy is not part of this renderer adapter.</summary>
    private void AddSkateParts(XPerson person, List<Part> parts,
        IReadOnlyDictionary<string, List<Vector3>> footPositions,
        float tileX, float tileY, float altitude, LotCamera camera)
    {
        var age = PersonAgeCategory.IsChild(person[PersonData.PersonAge]) ? "child" : "adult";
        Add("left");
        Add("right");

        void Add(string side)
        {
            var stem = $"accessoryskateice{age}{side}";
            var mesh = ReadSkateMesh(stem);
            var points = footPositions[side];
            if (points.Count < 3)
                throw new InvalidDataException($"Sim {person.Id} has no skinned {side} foot vertices for skate attachment {stem}");
            var footMin = new Vector3(points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z));
            var footMax = new Vector3(points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z));
            var shoeSpanX = footMax.X - footMin.X;
            var shoeSpanZ = footMax.Z - footMin.Z;
            var longAxisIsX = shoeSpanX > shoeSpanZ;
            var shoeLength = Math.Max(0.12f, Math.Max(shoeSpanX, shoeSpanZ) * 1.12f);
            var shoeWidth = Math.Max(0.035f, Math.Min(Math.Max(shoeSpanX, 0.04f), Math.Max(shoeSpanZ, 0.04f)));
            var sourceMinX = mesh.Positions.Min(point => -point.X);
            var sourceMaxX = mesh.Positions.Max(point => -point.X);
            var sourceMinY = mesh.Positions.Min(point => point.Y);
            var sourceMaxY = mesh.Positions.Max(point => point.Y);
            var sourceMinZ = mesh.Positions.Min(point => point.Z);
            var sourceMaxZ = mesh.Positions.Max(point => point.Z);

            // The source export supplies a thin blade only. Fit its long axis to the actual skinned shoe
            // bounds and put it directly below the sole; the source pack has no boot mesh to import.
            var vertices = new Vertex[mesh.Corners.Length];
            for (var index = 0; index < mesh.Corners.Length; index++)
            {
                var corner = mesh.Corners[index];
                var source = mesh.Positions[corner.Position];
                var along = (source.Y - sourceMinY) / Math.Max(0.0001f, sourceMaxY - sourceMinY);
                var across = (-source.X - sourceMinX) / Math.Max(0.0001f, sourceMaxX - sourceMinX);
                var height = (source.Z - sourceMinZ) / Math.Max(0.0001f, sourceMaxZ - sourceMinZ);
                var centerX = (footMin.X + footMax.X) * 0.5f;
                var centerZ = (footMin.Z + footMax.Z) * 0.5f;
                var posed = longAxisIsX
                    ? new Vector3(centerX - shoeLength * 0.5f + along * shoeLength,
                        footMin.Y - 0.012f + height * 0.004f,
                        centerZ - shoeWidth * 0.5f + across * shoeWidth)
                    : new Vector3(centerX - shoeWidth * 0.5f + across * shoeWidth,
                        footMin.Y - 0.012f + height * 0.004f,
                        centerZ - shoeLength * 0.5f + along * shoeLength);
                var lotPosition = AvatarCoordinates.ToLot(posed);
                var worldX = tileX + lotPosition.X;
                var worldY = tileY + lotPosition.Y;
                var bladeDepth = camera.Depth(worldX, worldY, posed.Y + altitude);
                var point = camera.Project(worldX, worldY, posed.Y * 40 + altitude * 40);
                var normal = camera.CameraNormal(new Microsoft.Xna.Framework.Vector3(0, 1, 0));
                if (normal.LengthSquared() > 0) normal.Normalize();
                var uv = mesh.UVs[corner.UV];
                vertices[index] = new Vertex(point.X, point.Y, bladeDepth,
                    uv.X, uv.Y, normal.X, normal.Y, normal.Z);
            }
            parts.Add(new Part(mesh.Texture.Bytes, mesh.Texture.Sha1Hex, mesh.Texture.Encoding, mesh.Texture.Width, mesh.Texture.Height, vertices));
        }
    }

    private static readonly Matrix4x4 SourceAxis = new(
        -1, 0, 0, 0,
         0, 0, 1, 0,
         0, 1, 0, 0,
         0, 0, 0, 1);
    private static readonly Matrix4x4 SourceAxisInverse = Matrix4x4.Transpose(SourceAxis);

    private static Vector3 SourcePositionNative(Vector3 source)
    {
        // Source GMDC positions and CRES translations are both in source meters. The native skeleton
        // stores three internal units per meter, and the avatar projection below divides by three.
        // Convert positions here so the blade keeps its authored physical size through that projection.
        return Vector3.Transform(source * 3, SourceAxis);
    }

    private static Matrix4x4 BoneMatrix(BonePose bone)
    {
        var matrix = Matrix4x4.CreateFromQuaternion(bone.WorldRotation);
        matrix.M41 = bone.WorldTranslation.X;
        matrix.M42 = bone.WorldTranslation.Y;
        matrix.M43 = bone.WorldTranslation.Z;
        return matrix;
    }

    private static Matrix4x4 SourceFrameMatrix(SkateFrame frame)
    {
        if (frame is null || frame.Translation is null || frame.Rotation is null ||
            frame.Translation.Length != 3 || frame.Rotation.Length != 4 ||
            frame.Translation.Any(value => !float.IsFinite(value)) || frame.Rotation.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Source skate attachment frame has invalid CRES transform data");
        var rotation = new Quaternion(frame.Rotation[0], frame.Rotation[1], frame.Rotation[2], frame.Rotation[3]);
        if (rotation.LengthSquared() < 0.99f || rotation.LengthSquared() > 1.01f)
            throw new InvalidDataException("Source skate attachment frame has a non-unit CRES quaternion");
        var matrix = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        matrix.M41 = frame.Translation[0];
        matrix.M42 = frame.Translation[1];
        matrix.M43 = frame.Translation[2];
        return matrix;
    }

    private SkateAttachmentFrames ReadSkateAttachmentFrames()
    {
        if (skateAttachmentFrames is not null) return skateAttachmentFrames;
        var file = skins.Find("ice-skate-attachment-frames.txt")
            ?? throw new InvalidDataException("Source skate ankle-grip frame metadata is missing in the installed pack");
        var metadata = JsonSerializer.Deserialize<SkateAttachmentFrames>(file.Read().Span,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Source skate ankle-grip frame metadata is empty");
        if (metadata.SchemaVersion != 1 || metadata.CoordinateSystem is null ||
            !metadata.CoordinateSystem.Equals("source Sims 2 CRES; Z-up", StringComparison.Ordinal))
            throw new InvalidDataException("Source skate ankle-grip frame metadata has an unsupported schema or coordinate system");
        if (metadata.Skeletons is null)
            throw new InvalidDataException("Source skate attachment metadata has no adult/child skeleton table");
        foreach (var (age, leftFoot, rightFoot) in new[] { ("adult", "L_FOOT", "R_FOOT"), ("child", "L_FOOT", "R_FOOT") })
        {
            if (!metadata.Skeletons.TryGetValue(age, out var skeleton) ||
                skeleton is null || skeleton.Age is null || !skeleton.Age.Equals(age, StringComparison.OrdinalIgnoreCase) ||
                skeleton.Frames is null || skeleton.Frames.Count != 2 ||
                !skeleton.Frames.TryGetValue("left", out var left) || !skeleton.Frames.TryGetValue("right", out var right) ||
                left is null || right is null || left.Slot is null || left.Foot is null || right.Slot is null || right.Foot is null ||
                left.SlotJoint is null || right.SlotJoint is null || left.FootJoint is null || right.FootJoint is null ||
                !left.SlotJoint.Equals("l_ankle_grip", StringComparison.OrdinalIgnoreCase) ||
                !right.SlotJoint.Equals("r_ankle_grip", StringComparison.OrdinalIgnoreCase) ||
                !left.FootJoint.Equals(leftFoot, StringComparison.OrdinalIgnoreCase) ||
                !right.FootJoint.Equals(rightFoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source skate attachment metadata has invalid {age} left/right ankle-grip bindings");
            _ = SourceFrameMatrix(left.Slot);
            _ = SourceFrameMatrix(left.Foot);
            _ = SourceFrameMatrix(right.Slot);
            _ = SourceFrameMatrix(right.Foot);
        }
        skateAttachmentFrames = metadata;
        return metadata;
    }

    private SkateMesh ReadSkateMesh(string stem)
    {
        if (skateMeshes.TryGetValue(stem, out var cached)) return cached;
        var obj = skins.Find(stem + ".obj")
            ?? throw new InvalidDataException($"Source skate attachment mesh {stem}.obj is not installed in the installed pack");
        var mtl = skins.Find(stem + ".mtl")
            ?? throw new InvalidDataException($"Source skate attachment material {stem}.mtl is missing");
        var textureName = Encoding.ASCII.GetString(mtl.Read().Span).Split('\n')
            .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("map_Kd", StringComparison.OrdinalIgnoreCase))?[1]
            ?? throw new InvalidDataException($"Source skate attachment material {stem}.mtl has no diffuse texture");
        var textureFile = skins.Find(textureName)
            ?? throw new InvalidDataException($"Source skate texture {textureName} is missing in the installed pack");
        var textureBytes = textureFile.Read().ToArray();
        if (textureFile.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && textureBytes.Length >= 24 &&
            textureBytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(textureBytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(textureBytes.AsSpan(20, 4));
            if (width is < 1 or > 4096 || height is < 1 or > 4096)
                throw new InvalidDataException($"Source skate texture {textureName} has invalid dimensions");
            var model = ParseSkateObj(Encoding.ASCII.GetString(obj.Read().Span), stem,
                new TextureSource(textureBytes, Convert.ToHexString(SHA1.HashData(textureBytes), 0, 6), "png", width, height, textureFile.Name));
            skateMeshes.Add(stem, model);
            return model;
        }
        throw new InvalidDataException($"Source skate texture {textureName} is not a PNG");
    }

    private static SkateMesh ParseSkateObj(string source, string stem, TextureSource texture)
    {
        var positions = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var corners = new List<(int Position, int UV, int Normal)>();
        var lineNumber = 0;
        foreach (var rawLine in source.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            float Number(int index)
            {
                if (index >= fields.Length || !float.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                    !float.IsFinite(value) || MathF.Abs(value) > 1000)
                    throw new InvalidDataException($"Skate OBJ {stem}.obj line {lineNumber} has an invalid coordinate");
                return value;
            }
            if (fields[0] == "v") positions.Add(new Vector3(Number(1), Number(2), Number(3)));
            else if (fields[0] == "vt") uvs.Add(new Vector2(Number(1), Number(2)));
            else if (fields[0] == "vn") normals.Add(new Vector3(Number(1), Number(2), Number(3)));
            else if (fields[0] == "f")
            {
                if (fields.Length != 4) throw new InvalidDataException($"Skate OBJ {stem}.obj line {lineNumber} is not a triangle");
                for (var corner = 1; corner <= 3; corner++)
                {
                    var indexes = fields[corner].Split('/');
                    if (indexes.Length != 3 || !int.TryParse(indexes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ||
                        !int.TryParse(indexes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uv) ||
                        !int.TryParse(indexes[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                        p < 1 || p > positions.Count || uv < 1 || uv > uvs.Count || n < 1 || n > normals.Count)
                        throw new InvalidDataException($"Skate OBJ {stem}.obj line {lineNumber} has an invalid face index");
                    corners.Add((p - 1, uv - 1, n - 1));
                }
            }
            if (positions.Count > 10_000 || uvs.Count > 10_000 || normals.Count > 10_000 || corners.Count > 60_000)
                throw new InvalidDataException($"Source skate mesh {stem}.obj exceeds the native scene limits");
        }
        if (positions.Count == 0 || uvs.Count == 0 || normals.Count == 0 || corners.Count == 0 || corners.Count % 3 != 0)
            throw new InvalidDataException($"Source skate mesh {stem}.obj has incomplete geometry");
        return new SkateMesh(positions.ToArray(), uvs.ToArray(), normals.ToArray(), corners.ToArray(), texture);
    }

    private static CharacterFile Find(CharacterSearchPath path, string name, params string[] extensions)
    {
        var stem = Path.GetFileName(name.Trim());
        if (Path.HasExtension(stem)) return path.Find(stem)
            ?? throw new InvalidDataException($"Avatar resource {name} is missing from the original search path");
        foreach (var extension in extensions)
            if (path.Find(stem + extension) is { } found) return found;
        throw new InvalidDataException($"Avatar resource {name} is missing from the original search path");
    }

    private static string NormalizeAppearance(string name)
    {
        var trimmed = name.Trim();
        foreach (var suffix in new[] { ".cmx.bcf", ".apr", ".bcf", ".cmx" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length];
        }
        return trimmed;
    }
}
