using System.Numerics;
using System.Runtime.CompilerServices;
using SimsEngine.Formats;

namespace SimsEngine.Character;

/// <summary>One bone's transform at a frame: its local transform (relative to its parent, as the bone's own transform
/// stands) and its world transform as produced by Mac's Bone::UpdateTransform routine.</summary>
public sealed record BonePose(string Name, string Parent, bool Animated, Vector3 LocalTranslation, Quaternion LocalRotation,
    Vector3 WorldTranslation, Quaternion WorldRotation);

/// <summary>The pose of a skeleton at one frame of an animation.
/// <list type="bullet">
/// <item><description>Motion to bone: each motion drives the bone with the same name (case-sensitive, exact), Sims.exe's motion
/// lookup (FUN_0059783a compares names with FUN_004317d5, a byte-for-byte compare). Motions naming no bone are ignored.
/// How a motion's bone index is set at load is not traced; the name rule is Inferred for that binding.</description></item>
/// <item><description>Curve sampling: a motion reads translation frame <c>Integers[2] + frame</c> (three floats) and rotation
/// frame <c>Integers[3] + frame</c> (four floats) from the animation's curve arrays. FUN_00597c54 reads
/// <c>(motion+0x24 + frame) * 12</c> from the translation array at animation+0x60 (the unlerped branch) and
/// <c>(motion+0x28 + frame) * 16</c> from the rotation array at animation+0x68. The motion's Integers sit at motion+0x1c to
/// +0x28 (FUN_00596834, kept as read by the reader).</description></item>
/// <item><description>Frame: the playback clamps the frame to the first motion's frame count minus one (FUN_00597a90 stores that
/// count as the playback length, FUN_00597c54 clamps to it). A frame below zero is zero. No motions means the bind pose.</description></item>
/// <item><description>Between frames: Windows FUN_00597c54 interpolates translation linearly and rotation with
/// BlendQuats1/FUN_005939a0: linear blending, shorter-arc sign correction, and normalization only when the resulting
/// magnitude differs from one by more than 0.00001 (also Mac skeleton.s 0x1036C180–0x1036C394). Windows suppresses
/// interpolation when the elapsed update spans at least two frame intervals; Mac Practice::Apply has a different gate
/// (skeleton.s 0x10361DC4–0x10361EA0). Native playback follows the Windows gate.</description></item>
/// <item><description>Bones with no motion keep the skeleton's transform as read (the bind pose). Inferred: FUN_00598dab copies
/// each bone's transform into the playback's bone object, and FUN_00597c54 writes only bones that have a track, so an
/// untracked bone keeps that copy.</description></item>
/// <item><description>Composition follows Mac <c>Bone::UpdateTransform</c> (skeleton.s 0x10366700–0x10366A40): the local
/// translation is rotated by the parent quaternion's <c>quattom3</c> matrix and added to the parent translation. For rotation,
/// Mac negates the parent quaternion's W, multiplies local * parent, then negates the result W. Roots use the skeleton root
/// transform. Missing parents are treated as roots because no linked parent is supplied to the update walk.</description></item>
/// </list></summary>
public static class Pose
{
    /// <summary>Advances SAnimator's transient HEAD posture angle. AdjustHead wraps the desired-minus-current
    /// direction delta into [-4, 4), moves by at most 0.2 direction units, clamps the accumulated angle to
    /// [-1.5, 1.5], and decays it by 0.8 when head seek is inactive (Mac SAnimator.s 0x1034DC1C–0x1034DD18;
    /// constants at lbl_1059BBC4 + 0x38/+0x54/+0x58/+0x5c/+0x60/+0x64/+0x68).</summary>
    public static float AdvanceHeadAdjustment(float current, float baselineDirection, float targetDirection, bool seeking)
        => AdvanceHeadAdjustment(current, baselineDirection, targetDirection, seeking, out _);

    public static float AdvanceHeadAdjustment(float current, float baselineDirection, float targetDirection, bool seeking,
        out bool hitLimit)
    {
        hitLimit = false;
        if (!seeking) return current * 0.8f;

        var delta = baselineDirection - targetDirection;
        while (delta < -4f) delta += 8f;
        while (delta >= 4f) delta -= 8f;
        delta = Math.Clamp(delta, -0.2f, 0.2f);
        var next = current + delta;
        hitLimit = next < -1.5f || next > 1.5f;
        return Math.Clamp(next, -1.5f, 1.5f);
    }

    /// <summary>Computes SAnimator's eight-way angular value from source horizontal X/Z coordinates. Both the baseline
    /// (actor registration to HeadFake registration) and target direction use atan2(deltaZ, -deltaX), converted to
    /// eighth-turn units and normalized to [0, 8). A zero-length vector leaves the stored value unchanged; the source
    /// tests squared length against zero (Windows FUN_005904d6, Mac SAnimator.s 0x1034E2D8–0x1034E3D0 and
    /// lbl_1059BC80 + 0x40).</summary>
    public static float HeadDirection(Vector2 origin, Vector2 point, float previous)
    {
        var deltaX = point.X - origin.X;
        var deltaZ = point.Y - origin.Y;
        if (deltaX * deltaX + deltaZ * deltaZ <= 0f) return previous;

        var direction = MathF.Atan2(deltaZ, -deltaX) * (8f / (2f * MathF.PI));
        direction %= 8f;
        if (direction < 0f) direction += 8f;
        return direction;
    }

    /// <summary>Applies AdjustHead's transient rotation to skeleton bone id 31 (the skeleton vector index used by
    /// <c>FindBone(0x1f)</c>). The source extracts the base X Euler angle from the converted quaternion matrix at
    /// indices 5/6, adds +0x210 × 0.125 × 2π, clamps the combined angle to [-1.2, 1.2], then multiplies the X Euler
    /// quaternion by the saved local quaternion (Mac SAnimator.s 0x1034DD48–0x1034DF07 and U3DQuaternion.s
    /// 0x103E2D70–0x103E2E54). Input/output poses retain authored Sims.exe quaternions.</summary>
    public static IReadOnlyList<BonePose> ApplyHeadAdjustment(Skeleton skeleton, IReadOnlyList<BonePose> pose,
        float adjustment)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.Count != skeleton.Bones.Count)
            throw new InvalidDataException("The head-adjustment pose does not match the skeleton bone count");
        const int headBoneId = 31;
        if (pose.Count <= headBoneId) return pose;

        var head = pose[headBoneId];
        var sourceRotation = head.LocalRotation;
        var sourceBaseAngle = MathF.Atan2(
            2f * (sourceRotation.X * sourceRotation.W - sourceRotation.Y * sourceRotation.Z),
            1f - 2f * (sourceRotation.X * sourceRotation.X + sourceRotation.Z * sourceRotation.Z));
        var sourceAngle = adjustment * (MathF.PI / 4f);
        var adjustedAngle = Math.Clamp(sourceAngle + sourceBaseAngle, -1.2f, 1.2f) - sourceBaseAngle;
        var sourceEuler = Quaternion.CreateFromAxisAngle(Vector3.UnitX, adjustedAngle);
        var adjustedRotation = Quaternion.Multiply(sourceEuler, sourceRotation);

        var local = new Local[pose.Count];
        for (var index = 0; index < pose.Count; index++)
        {
            var bone = pose[index];
            local[index] = new Local(bone.LocalTranslation, index == headBoneId ? adjustedRotation : bone.LocalRotation, bone.Animated);
        }
        return Recompose(skeleton, GetLayout(skeleton), local);
    }

    /// <summary>Selects SAnimator's cached locomotion stride for its current practice phase. Mac Tick uses
    /// +0x170 when phase is less than marker 2 or greater than or equal to marker 1; otherwise it uses +0x174
    /// (SAnimator.s 0x103507D4–0x1035080C).</summary>
    public static float LocomotionStrideAtPhase(float phase, float markerOnePhase, float markerTwoPhase,
        float markerOneStride, float markerTwoStride) =>
        phase < markerTwoPhase || phase >= markerOnePhase ? markerOneStride : markerTwoStride;

    /// <summary>Returns the world translation of a VitaBoy registration made from a posed bone and suffix.
    /// <c>FindRegistration</c> uses identity for an absent or unrecognized suffix. Its authored suffix transforms are
    /// Palm (Z rotation PI, translation (0.5,0,0)), Top (Z rotation -PI/2, translation (0.8,0,0)),
    /// HeadFake (Z rotation -PI/2, translation (0.8,0,100)), and Shoulder (Z rotation PI, translation (0,0,1));
    /// Mac VitaBoy.s 0x103813F0–0x10381760, corroborated by Windows FUN_005a14b9. The authored suffix offset is
    /// composed directly with the source bone transform.</summary>
    public static Vector3 RegistrationTranslation(BonePose bone, string? suffix)
    {
        ArgumentNullException.ThrowIfNull(bone);
        var offset = suffix switch
        {
            "Palm" => new Vector3(0.5f, 0f, 0f),
            "Top" => new Vector3(0.8f, 0f, 0f),
            "HeadFake" => new Vector3(0.8f, 0f, 100f),
            "Shoulder" => new Vector3(0f, 0f, 1f),
            _ => Vector3.Zero,
        };
        return bone.WorldTranslation + Vector3.Transform(offset, BoneMatrixRotation(bone.WorldRotation));
    }

    /// <summary>Returns the quaternion used by Mac Bone's cached absolute matrix. UpdateTransform stores the composed
    /// world quaternion with W negated, then negates W again before quattom3 builds the matrix (skeleton.s
    /// 0x10366758–0x10366794 and 0x103669D8–0x10366A14). The vertex pipeline multiplies that matrix as a row
    /// vector (RealMath.s 0x10005DAC–0x10005E38); System.Numerics' quaternion matrix has the transposed convention.
    /// Conjugating the input to quattom3 therefore leaves the stored quaternion, up to an irrelevant overall sign.</summary>
    public static Quaternion BoneMatrixRotation(Quaternion storedWorldRotation) => storedWorldRotation;

    /// <summary>Returns the frame distance to the next marker boundary used by SAnimator::Tick's near-waypoint branch.
    /// Tick targets marker 2 when outside [marker 2, marker 1), otherwise marker 1, and wraps a negative difference by
    /// one practice cycle (SAnimator.s 0x103508CC–0x103508EC).</summary>
    public static float FramesToNextLocomotionMarker(float phase, float markerOnePhase, float markerTwoPhase,
        int frameCount)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        var target = phase < markerTwoPhase || phase >= markerOnePhase ? markerTwoPhase : markerOnePhase;
        var phaseDelta = target - phase;
        if (phaseDelta < 0f) phaseDelta += 1f;
        return phaseDelta * frameCount;
    }

    /// <summary>Calculates the Practice::Fade arguments from SAnimator::Tick's near-waypoint branch. The target weight is
    /// max(distance / selected stride, 0.2) (constant at lbl_1059BBC4 + 0x5c); the end time is now plus the truncated
    /// next-marker duration, clamped to the 100 ms constant at lbl_10619D68 (SAnimator.s 0x10350824–0x10350958).</summary>
    public static (float TargetWeight, int DurationMilliseconds)? LocomotionFade(float distance, float stride,
        float phase, float markerOnePhase, float markerTwoPhase, float skillDurationMilliseconds, float walkingScale)
    {
        if (stride <= 0f || distance > stride || skillDurationMilliseconds <= 0f || walkingScale <= 0f)
            return null;

        var targetWeight = Math.Max(distance / stride, 0.2f);
        var markerDuration = FramesToNextLocomotionMarker(phase, markerOnePhase, markerTwoPhase, 1);
        var duration = (int)(markerDuration * skillDurationMilliseconds / walkingScale);
        return (targetWeight, Math.Max(duration, 100));
    }

    /// <summary>Evaluates the two SAnimator::StartVitaBoy locomotion stride values from marker bone transforms.
    /// Mac stores +0x170 as scale * (marker-2 bone-1 Z - marker-1 bone-2 Z), and +0x174 as
    /// scale * (marker-2 bone-2 Z - marker-2 bone-1 Z) (SAnimator.s 0x10358E8C–0x10359068).
    /// Marker times are normalized by Skill duration there; callers convert them to animation frames.</summary>
    public static (float MarkerOneStride, float MarkerTwoStride) LocomotionStrides(Skeleton skeleton,
        Animation animation, AnimationCurves curves, float markerOneFrame, string markerOneBone,
        float markerTwoFrame, string markerTwoBone)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(curves);
        ArgumentNullException.ThrowIfNull(markerOneBone);
        ArgumentNullException.ThrowIfNull(markerTwoBone);

        var atMarkerOne = Sample(skeleton, animation, curves, markerOneFrame);
        var atMarkerTwo = Sample(skeleton, animation, curves, markerTwoFrame);
        var boneIndices = GetLayout(skeleton).IndexByName;
        var markerOneAtMarkerTwoZ = Find(atMarkerTwo, boneIndices, markerOneBone).WorldTranslation.Z;
        var markerTwoAtMarkerOneZ = Find(atMarkerOne, boneIndices, markerTwoBone).WorldTranslation.Z;
        var markerTwoAtMarkerTwoZ = Find(atMarkerTwo, boneIndices, markerTwoBone).WorldTranslation.Z;
        const float unitsPerSkillUnit = 16f / 3f;
        return (unitsPerSkillUnit * (markerOneAtMarkerTwoZ - markerTwoAtMarkerOneZ),
            unitsPerSkillUnit * (markerTwoAtMarkerTwoZ - markerOneAtMarkerTwoZ));

        static BonePose Find(IReadOnlyList<BonePose> poses, Dictionary<string, int> indices, string name) =>
            indices.TryGetValue(name, out var index)
                ? poses[index]
                : throw new InvalidDataException($"Locomotion marker bone {name} is absent from the selected skeleton");
    }

    /// <summary>Returns the cached bind pose for <paramref name="skeleton"/> without allocating per frame.</summary>
    public static IReadOnlyList<BonePose> BindPose(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var layout = GetLayout(skeleton);
        if (layout.BindPose is { } bindPose) return bindPose;
        var bones = skeleton.Bones;
        var local = new Local[bones.Count];
        for (var i = 0; i < bones.Count; i++) local[i] = new Local(bones[i].Translation, bones[i].Rotation, false);
        return Recompose(skeleton, layout, local);
    }

    /// <summary>Returns the cached Ordinal bone-name-to-index table (first name wins) for <paramref name="skeleton"/>.</summary>
    public static IReadOnlyDictionary<string, int> BoneIndices(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return GetLayout(skeleton).IndexByName;
    }

    /// <summary>The pose of <paramref name="skeleton"/> at <paramref name="frame"/> of <paramref name="animation"/>, decoded from
    /// <paramref name="curves"/>. When <paramref name="loop"/> is true, the next sample after the final frame wraps to frame zero,
    /// matching Practice cycle mode 1. Bones are returned in the skeleton's order.</summary>
    /// <exception cref="InvalidDataException">A motion reads a curve frame past the end of its array.</exception>
    public static IReadOnlyList<BonePose> Sample(Skeleton skeleton, Animation animation, AnimationCurves curves, double frame,
        IReadOnlyList<BonePose>? basePose = null, float practiceWeight = 1f, bool loop = false,
        bool interpolateFrame = true)
    {
        var bones = skeleton.Bones;
        if (basePose is not null && basePose.Count != bones.Count)
            throw new InvalidDataException("The base pose does not match the skeleton bone count");
        var layout = GetLayout(skeleton);
        var length = animation.Motions.Count == 0 ? 0 : animation.Motions[0].FrameCount;
        if (basePose is null && (length <= 0 || practiceWeight <= 0f) && layout.BindPose is { } cachedBindPose)
            return cachedBindPose;

        var local = new Local[bones.Count];
        if (basePose is null)
        {
            for (var i = 0; i < bones.Count; i++)
                local[i] = new Local(bones[i].Translation, bones[i].Rotation, false);
        }
        else
        {
            for (var i = 0; i < bones.Count; i++)
            {
                var basis = basePose[i];
                local[i] = new Local(basis.LocalTranslation, basis.LocalRotation, basis.Animated);
            }
        }

        var index = layout.IndexByName;
        // Practice::Apply (00597c54.c:138) only applies motions and marks bones Animated when 0.0 < practiceWeight.
        if (length > 0 && practiceWeight > 0f)
        {
            // Practice clamps its current integer frame to the motion's last frame but retains the fractional phase.
            // Cycle mode 1 then selects frame zero as the following sample at the seam (skeleton.s
            // 0x10361D6C–0x10361E04). At an exact phase of one, the clamped last frame remains selected.
            var phaseFrame = Math.Clamp(frame, 0, loop ? length : length - 1);
            var frameFloor = (int)Math.Floor(phaseFrame);
            var first = Math.Min(frameFloor, length - 1);
            var fraction = !interpolateFrame || frameFloor >= length ? 0f : (float)(phaseFrame - frameFloor);
            var second = fraction <= 0 ? first : first + 1 < length ? first + 1 : loop ? 0 : first;
            if (second == first) fraction = 0f;
            var weight = second == first ? 0f : fraction;

            // Motions apply in file order, so a later motion for the same bone replaces an earlier one (FUN_00597c54 writes each track).
            for (var m = 0; m < animation.Motions.Count; m++)
            {
                var motion = animation.Motions[m];
                if (!index.TryGetValue(motion.Name, out var bone) || motion.FrameCount <= 0) continue;
                // Practice::Apply (00597c54.c) bounds each track against its own motion.FrameCount so mixed single-frame
                // static pose tracks and shorter secondary tracks never index past their per-motion curve span.
                var motionLast = motion.FrameCount - 1;
                var motionFirst = Math.Min(first, motionLast);
                var motionSecond = weight <= 0f || second == first
                    ? motionFirst
                    : second == 0 && loop && first == length - 1
                        ? 0
                        : first + 1 <= motionLast
                            ? first + 1
                            : motionFirst;
                var motionWeight = motionSecond == motionFirst ? 0f : weight;
                // A base index of -1 means the motion has no channel of that kind; the bone then keeps the value it already has
                // for that channel.
                // Bone.Integers[2] (+0x74 in Bone::ReadFromFile 00595963.c:74) is the CanBlend flag (0 on ROOT, 1 on body bones).
                // When CanBlend == 0 or practiceWeight >= 1.0f, Practice::Apply (00597c54.c:307-312, 343-349, 387, 422-428)
                // assigns the sampled translation/rotation directly without weighting by practiceWeight.
                var current = local[bone];
                var boneIntegers = bones[bone].Integers;
                var canBlend = boneIntegers.Length <= 2 || boneIntegers[2] != 0;
                var sampledTranslation = motion.Integers[2] >= 0
                    ? Lerp(Translation(curves, motion, motionFirst, animation.Name), Translation(curves, motion, motionSecond, animation.Name), motionWeight)
                    : current.Translation;
                var translation = motion.Integers[2] >= 0
                    ? (!canBlend || practiceWeight >= 1f
                        ? sampledTranslation
                        : Lerp(current.Translation, sampledTranslation, practiceWeight))
                    : current.Translation;
                var sampledRotation = motion.Integers[3] >= 0
                    ? Blend(Rotation(curves, motion, motionFirst, animation.Name), Rotation(curves, motion, motionSecond, animation.Name), motionWeight)
                    : current.Rotation;
                var rotation = motion.Integers[3] >= 0
                    ? (!canBlend || practiceWeight >= 1f
                        ? sampledRotation
                        : practiceWeight > 0.0001f ? Blend(current.Rotation, sampledRotation, practiceWeight) : current.Rotation)
                    : current.Rotation;
                local[bone] = new Local(translation, rotation, true);
            }
        }

        return Recompose(skeleton, layout, local);
    }

    private sealed class SkeletonLayout(Dictionary<string, int> indexByName, int[] parentIndices, int[]? evalOrder, BonePose[]? bindPose)
    {
        public readonly Dictionary<string, int> IndexByName = indexByName;
        public readonly int[] ParentIndices = parentIndices;
        public readonly int[]? EvalOrder = evalOrder;
        public readonly BonePose[]? BindPose = bindPose;
    }

    private static readonly ConditionalWeakTable<Skeleton, SkeletonLayout> Layouts = new();

    private static SkeletonLayout GetLayout(Skeleton skeleton) => Layouts.GetValue(skeleton, BuildLayout);

    private static SkeletonLayout BuildLayout(Skeleton skeleton)
    {
        var bones = skeleton.Bones;
        var count = bones.Count;
        // The first name wins when a skeleton repeats a name (Inferred: the decompilation does not show duplicates being rejected).
        var index = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var bone = 0; bone < count; bone++) index.TryAdd(bones[bone].Name, bone);

        // A parent name the skeleton does not contain makes the bone a root (Inferred: FUN_0059903f has no entry for an unknown parent).
        var parents = new int[count];
        for (var bone = 0; bone < count; bone++)
            parents[bone] = index.TryGetValue(bones[bone].Parent, out var parent) ? parent : -1;

        var state = new byte[count];
        var order = new int[count];
        var orderCount = 0;
        var hasCycle = false;
        for (var bone = 0; bone < count && !hasCycle; bone++) Visit(bone);

        if (hasCycle) return new SkeletonLayout(index, parents, null, null);

        var bindLocal = new Local[count];
        for (var bone = 0; bone < count; bone++)
            bindLocal[bone] = new Local(bones[bone].Translation, bones[bone].Rotation, false);
        var bindPose = RecomposeOrdered(bones, parents, order, bindLocal);
        return new SkeletonLayout(index, parents, order, bindPose);

        void Visit(int bone)
        {
            if (state[bone] == 2) return;
            if (state[bone] == 1) { hasCycle = true; return; }
            state[bone] = 1;
            var parent = parents[bone];
            if (parent >= 0) Visit(parent);
            if (hasCycle) return;
            state[bone] = 2;
            order[orderCount++] = bone;
        }
    }

    private static BonePose[] RecomposeOrdered(IReadOnlyList<Bone> bones, int[] parents, int[] evalOrder, Local[] local)
    {
        var count = bones.Count;
        var worlds = new (Vector3 Translation, Quaternion Rotation)[count];
        var result = new BonePose[count];
        for (var i = 0; i < evalOrder.Length; i++)
        {
            var boneIndex = evalOrder[i];
            var parentIndex = parents[boneIndex];
            var parentWorld = parentIndex < 0 ? (Vector3.Zero, Quaternion.Identity) : worlds[parentIndex];
            var world = Compose(parentWorld, local[boneIndex]);
            worlds[boneIndex] = world;
            var bone = bones[boneIndex];
            var loc = local[boneIndex];
            result[boneIndex] = new BonePose(bone.Name, bone.Parent, loc.Animated, loc.Translation, loc.Rotation,
                world.Translation, world.Rotation);
        }
        return result;
    }

    private static IReadOnlyList<BonePose> Recompose(Skeleton skeleton, SkeletonLayout layout, Local[] local)
    {
        if (layout.EvalOrder is { } order)
            return RecomposeOrdered(skeleton.Bones, layout.ParentIndices, order, local);

        var bones = skeleton.Bones;
        var parents = layout.ParentIndices;
        var worlds = new (Vector3 Translation, Quaternion Rotation)?[bones.Count];
        var inProgress = new bool[bones.Count];
        for (var bone = 0; bone < bones.Count; bone++) World(bone, parents, local, worlds, inProgress);

        var result = new BonePose[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var world = worlds[i]!.Value;
            result[i] = new BonePose(bone.Name, bone.Parent, local[i].Animated, local[i].Translation, local[i].Rotation,
                world.Translation, world.Rotation);
        }
        return result;
    }

    private readonly record struct Local(Vector3 Translation, Quaternion Rotation, bool Animated);

    /// <summary>The world transform of one bone using the Mac UpdateTransform rule. A cycle in the parent links is an error.</summary>
    private static (Vector3 Translation, Quaternion Rotation) World(int bone, int[] parents, Local[] local,
        (Vector3 Translation, Quaternion Rotation)?[] worlds, bool[] inProgress)
    {
        if (worlds[bone] is { } known) return known;
        if (inProgress[bone]) throw new InvalidDataException($"bone {bone} is its own ancestor");
        inProgress[bone] = true;
        var parent = parents[bone] < 0
            ? (Vector3.Zero, Quaternion.Identity)
            : World(parents[bone], parents, local, worlds, inProgress);
        var result = Compose(parent, local[bone]);
        inProgress[bone] = false;
        worlds[bone] = result;
        return result;
    }

    /// <summary>Matches Bone::UpdateTransform: quattom3(parent) rotates local translation; world rotation is
    /// WFlip(local * WFlip(parent)) (Mac skeleton.s 0x10366700–0x10366A40, Quaternion::operator* at SAnimator.s 0x1034DF70).</summary>
    private static (Vector3 Translation, Quaternion Rotation) Compose((Vector3 Translation, Quaternion Rotation) parent, Local child) =>
        (parent.Translation + Vector3.Transform(child.Translation, parent.Rotation),
            FlipW(Quaternion.Multiply(child.Rotation, FlipW(parent.Rotation))));

    private static Quaternion FlipW(Quaternion value) => new(value.X, value.Y, value.Z, -value.W);

    /// <summary>The translation of one motion at a frame: three floats at the motion's translation base plus the frame.</summary>
    private static Vector3 Translation(AnimationCurves curves, Motion motion, int frame, string animation)
    {
        var offset = Offset(motion.Integers[2], frame, 3, curves.Translations.Length, motion, animation, "translation");
        return new Vector3(curves.Translations[offset], curves.Translations[offset + 1], curves.Translations[offset + 2]);
    }

    /// <summary>The rotation of one motion at a frame: four floats (X, Y, Z, W) at the motion's rotation base plus the frame.</summary>
    private static Quaternion Rotation(AnimationCurves curves, Motion motion, int frame, string animation)
    {
        var offset = Offset(motion.Integers[3], frame, 4, curves.Rotations.Length, motion, animation, "rotation");
        return new Quaternion(curves.Rotations[offset], curves.Rotations[offset + 1], curves.Rotations[offset + 2], curves.Rotations[offset + 3]);
    }

    private static int Offset(int baseFrame, int frame, int channels, int values, Motion motion, string animation, string kind)
    {
        var index = (long)baseFrame + frame;
        var offset = index * channels;
        if (index < 0 || offset + channels > values)
            throw new InvalidDataException($"{animation}/{motion.Name}: {kind} frame {index} is past the {values / channels} frames of the curve");
        return (int)offset;
    }

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>Matches BlendQuats1/FUN_005939a0 for an in-range weight: linear interpolation along the shorter arc,
    /// normalizing only when the magnitude differs from one by more than 0.00001.</summary>
    private static Quaternion Blend(Quaternion a, Quaternion b, float t)
    {
        if (t == 0) return a;
        if (Quaternion.Dot(a, b) < 0) b = -b;
        var blended = new Quaternion(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        // Mac compares |magnitude - 1| with 0x3727C5AC (0.00001), then compares the magnitude itself with the same value.
        // It writes identity for a near-zero result; otherwise it leaves near-unit results bit-for-bit unnormalized.
        var magnitude = blended.Length();
        if (MathF.Abs(magnitude - 1f) <= 1e-5f) return blended;
        return magnitude <= 1e-5f ? Quaternion.Identity : Quaternion.Normalize(blended);
    }
}
