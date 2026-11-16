using System.Numerics;
using System.Runtime.CompilerServices;
using SimsEngine.Formats;

namespace SimsEngine.Character;

public sealed record BonePose(string Name, string Parent, bool Animated, Vector3 LocalTranslation, Quaternion LocalRotation,
    Vector3 WorldTranslation, Quaternion WorldRotation);

/// <summary>Evaluates skeletal poses by sampling per-bone curve tracks, interpolating translations and
/// shortest-arc rotations across frames, and composing transforms down the bone hierarchy.</summary>
public static class Pose
{
    /// <summary>Advances the transient head posture angle toward a target direction, stepping by at most
    /// 0.2 units within [-1.5, 1.5] and decaying by a factor of 0.8 when target seeking is inactive.</summary>
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

    /// <summary>Computes an eight-way direction heading from horizontal X/Z displacements using
    /// atan2(deltaZ, -deltaX) in eighth-turn units normalized to [0, 8), preserving the previous heading if
    /// the displacement vector is zero.</summary>
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

    /// <summary>Applies transient head rotation to bone index 31 by adjusting the bone's base pitch angle,
    /// clamping the result to [-1.2, 1.2] radians before re-multiplying the local rotation quaternion.
    /// </summary>
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

    /// <summary>Selects the active locomotion stride based on cycle phase, returning the primary stride
    /// when phase is outside [markerTwoPhase, markerOnePhase) and the secondary stride otherwise.</summary>
    public static float LocomotionStrideAtPhase(float phase, float markerOnePhase, float markerTwoPhase,
        float markerOneStride, float markerTwoStride) =>
        phase < markerTwoPhase || phase >= markerOnePhase ? markerOneStride : markerTwoStride;

    /// <summary>Returns the world translation of a bone registration point, composing the bone transform
    /// with authored offsets for known suffixes such as Palm, Top, HeadFake, and Shoulder.</summary>
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

    public static Quaternion BoneMatrixRotation(Quaternion storedWorldRotation) => storedWorldRotation;

    /// <summary>Calculates the frame distance from the current animation phase to the upcoming footstep
    /// marker boundary, wrapping across cycle boundaries.</summary>
    public static float FramesToNextLocomotionMarker(float phase, float markerOnePhase, float markerTwoPhase,
        int frameCount)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        var target = phase < markerTwoPhase || phase >= markerOnePhase ? markerTwoPhase : markerOnePhase;
        var phaseDelta = target - phase;
        if (phaseDelta < 0f) phaseDelta += 1f;
        return phaseDelta * frameCount;
    }

    /// <summary>Calculates locomotion fade parameters near a waypoint, scaling target weight to distance
    /// over stride with a minimum of 0.2 and bounding duration to the next marker interval.</summary>
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

    /// <summary>Evaluates locomotion stride distances from bone displacements along the Z axis between
    /// animation footstep markers.</summary>
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

    public static IReadOnlyDictionary<string, int> BoneIndices(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return GetLayout(skeleton).IndexByName;
    }

    /// <summary>Samples a skeleton pose at the specified animation frame, wrapping the trailing
    /// interpolation sample to frame zero when looping is enabled.</summary>
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
        if (length > 0 && practiceWeight > 0f)
        {
            // Clamps integer frame to last frame and wraps next sample to frame zero on loop.
            var phaseFrame = Math.Clamp(frame, 0, loop ? length : length - 1);
            var frameFloor = (int)Math.Floor(phaseFrame);
            var first = Math.Min(frameFloor, length - 1);
            var fraction = !interpolateFrame || frameFloor >= length ? 0f : (float)(phaseFrame - frameFloor);
            var second = fraction <= 0 ? first : first + 1 < length ? first + 1 : loop ? 0 : first;
            if (second == first) fraction = 0f;
            var weight = second == first ? 0f : fraction;

            // Later motions for the same bone override earlier tracks in file order.
            for (var m = 0; m < animation.Motions.Count; m++)
            {
                var motion = animation.Motions[m];
                if (!index.TryGetValue(motion.Name, out var bone) || motion.FrameCount <= 0) continue;
                // Bounds frame sampling to each motion's track length for mixed-duration tracks.
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
                // Bones with CanBlend disabled bypass blend weighting and assign directly.
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
        var index = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var bone = 0; bone < count; bone++) index.TryAdd(bones[bone].Name, bone);

        // Bones referencing unknown parent names are treated as root bones.
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

    /// <summary>Composes a parent world transform with a child local transform by rotating child
    /// translation and multiplying orientations.</summary>
    private static (Vector3 Translation, Quaternion Rotation) Compose((Vector3 Translation, Quaternion Rotation) parent, Local child) =>
        (parent.Translation + Vector3.Transform(child.Translation, parent.Rotation),
            FlipW(Quaternion.Multiply(child.Rotation, FlipW(parent.Rotation))));

    private static Quaternion FlipW(Quaternion value) => new(value.X, value.Y, value.Z, -value.W);

    private static Vector3 Translation(AnimationCurves curves, Motion motion, int frame, string animation)
    {
        var offset = Offset(motion.Integers[2], frame, 3, curves.Translations.Length, motion, animation, "translation");
        return new Vector3(curves.Translations[offset], curves.Translations[offset + 1], curves.Translations[offset + 2]);
    }

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

    /// <summary>Blends two quaternions along the shorter arc using linear interpolation, normalizing only
    /// when the resulting magnitude deviates from one by more than 1e-5.</summary>
    private static Quaternion Blend(Quaternion a, Quaternion b, float t)
    {
        if (t == 0) return a;
        if (Quaternion.Dot(a, b) < 0) b = -b;
        var blended = new Quaternion(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        // Leaves near-unit quaternions unnormalized to preserve original precision.
        var magnitude = blended.Length();
        if (MathF.Abs(magnitude - 1f) <= 1e-5f) return blended;
        return magnitude <= 1e-5f ? Quaternion.Identity : Quaternion.Normalize(blended);
    }
}
