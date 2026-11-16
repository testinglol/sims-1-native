using System.Numerics;
using SimsEngine.Character;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class PoseTests
{
    [Theory]
    [InlineData(0f, 0.1f, 0f, true, 0.1f)]
    [InlineData(0f, 0f, 7.9f, true, 0.1f)]
    [InlineData(0f, 7.9f, 0f, true, -0.1f)]
    [InlineData(1.45f, 0.2f, 0f, true, 1.5f)]
    [InlineData(-1.45f, 0f, 0.2f, true, -1.5f)]
    [InlineData(1f, 0f, 0f, false, 0.8f)]
    public void HeadAdjustmentFollowsSourceWrapStepClampAndDecay(float current, float baseline, float target,
        bool seeking, float expected)
    {
        Assert.Equal(expected, Pose.AdvanceHeadAdjustment(current, baseline, target, seeking), 5);
    }

    [Theory]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 2f)]
    [InlineData(1f, 0f, 4f)]
    [InlineData(0f, -1f, 6f)]
    public void HeadDirectionUsesSourceEightWayAngle(float x, float z, float expected)
    {
        Assert.Equal(expected, Pose.HeadDirection(Vector2.Zero, new Vector2(x, z), 1.25f), 5);
    }

    [Fact]
    public void HeadDirectionRetainsStoredValueForCoincidentPoints() =>
        Assert.Equal(3.25f, Pose.HeadDirection(Vector2.One, Vector2.One, 3.25f));

    [Theory]
    [InlineData(1f, 0.3926991f)]
    [InlineData(2f, 0.6f)]
    public void HeadAdjustmentRotatesSkeletonBoneId31AndClampsSourceEuler(float adjustment, float expectedAngle)
    {
        var bones = Enumerable.Range(0, 32).Select(index => new Bone($"bone-{index}", "", [], Vector3.Zero,
            new Quaternion(0f, 0f, 0f, -1f), [], [])).ToArray();
        var skeleton = new Skeleton("head-adjust-test", bones);
        var pose = Pose.Sample(skeleton, new Animation("bind", "", [], 0, 0, 0, []), new AnimationCurves([], []), 0);

        var adjusted = Pose.ApplyHeadAdjustment(skeleton, pose, adjustment);

        Assert.Equal(new Quaternion(-MathF.Sin(expectedAngle), 0f, 0f, -MathF.Cos(expectedAngle)), adjusted[31].LocalRotation);
        Assert.Equal(pose[30].LocalRotation, adjusted[30].LocalRotation);
        Assert.Equal(pose[31].LocalTranslation, adjusted[31].LocalTranslation);
    }

    /// <summary>A 90-degree rotation about the Z axis represented by a unit quaternion whose Z and W
    /// components are the sine and cosine of 45 degrees.</summary>
    private const float Half = 0.70710678f;

    private static readonly Quaternion QuarterTurnZ = new(0, 0, Half, Half);

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 0.7853982f)]
    [InlineData(2, 1.5707963f)]
    [InlineData(3, 2.3561945f)]
    [InlineData(4, 3.1415927f)]
    [InlineData(5, 3.9269908f)]
    [InlineData(6, 4.7123890f)]
    [InlineData(7, 5.4977870f)]
    public void SavedDirectionIndexTurnsTheAvatarThroughAllEightFacings(short direction, float expected)
    {
        Assert.Equal(expected, AvatarDirection.Angle(direction), precision: 5);
    }

    [Theory]
    [InlineData(-1, 5.4977870f)]
    [InlineData(8, 0f)]
    public void SavedDirectionWrapsLikeThePersonDirectionValue(short direction, float expected)
    {
        Assert.Equal(expected, AvatarDirection.Angle(direction), precision: 5);
    }

    [Fact]
    public void AnimatorDirectionKeepsFractionalRootFacing()
    {
        Assert.Equal(5.25f * (MathF.PI / 4), AvatarDirection.Angle(5.25f), precision: 5);
        Assert.Equal(0.25f * (MathF.PI / 4), AvatarDirection.Angle(-7.75f), precision: 5);
    }

    [Fact]
    public void AttachedAnimatorInheritsParentsFractionalRootHeading()
    {
        var parentRoot = AvatarDirection.RootDirection(1.25f, 6, 0.5f);
        Assert.Equal(1.75f, parentRoot);
        Assert.Equal(parentRoot, AvatarDirection.RootDirection(4.5f, 3, -0.25f, parentRoot));
        Assert.Equal(3.25f, AvatarDirection.RootDirection(null, 3, 0.25f));
    }

    [Theory]
    [InlineData("Palm", 1.5f, 2f, 3f)]
    [InlineData("Top", 1.8f, 2f, 3f)]
    [InlineData("HeadFake", 1.8f, 2f, 103f)]
    [InlineData("Shoulder", 1f, 2f, 4f)]
    [InlineData("unknown", 1f, 2f, 3f)]
    public void RegistrationSuffixUsesTheAuthoredVitaBoyBoneOffset(string suffix, float x, float y, float z)
    {
        var bone = new BonePose("HEAD", "PELVIS", false, Vector3.Zero, Quaternion.Identity,
            new Vector3(1f, 2f, 3f), Quaternion.Identity);

        AssertVector(new Vector3(x, y, z), Pose.RegistrationTranslation(bone, suffix));
    }

    [Fact]
    public void RegistrationSuffixTranslationFollowsThePosedBoneRotation()
    {
        var bone = new BonePose("HEAD", "PELVIS", false, Vector3.Zero, Quaternion.Identity,
            new Vector3(1f, 2f, 3f), QuarterTurnZ);

        AssertVector(new Vector3(1f, 2.8f, 3f), Pose.RegistrationTranslation(bone, "Top"));
    }

    [Fact]
    public void BoneMatrixUsesMacVertexPipelineMatrixOrder()
    {
        var storedWorldRotation = new Quaternion(0f, 0f, Half, -Half);

        AssertVector(-Vector3.UnitY, Vector3.Transform(Vector3.UnitX,
            Pose.BoneMatrixRotation(storedWorldRotation)));
    }

    [Theory]
    [InlineData(0f, -1f, 0f)]
    [InlineData(1f, -1f, 1f)]
    [InlineData(1f, 0f, 2f)]
    [InlineData(1f, 1f, 3f)]
    [InlineData(0f, 1f, 4f)]
    [InlineData(-1f, 1f, 5f)]
    [InlineData(-1f, 0f, 6f)]
    [InlineData(-1f, -1f, 7f)]
    public void RouteVectorUsesTheEightAnimatorDirectionSectors(float deltaX, float deltaY, float direction)
    {
        Assert.Equal(direction, AvatarDirection.Toward(deltaX, deltaY), precision: 5);
    }

    [Theory]
    [InlineData(7.9f, 0.1f, 0.2f, 0.1f)]
    [InlineData(0f, 4f, 0.2f, 7.8f)]
    public void AnimatorDirectionApproachesAlongTheWrappedShortestDelta(float current, float target, float step, float expected)
    {
        Assert.Equal(expected, AvatarDirection.Approach(current, target, step), precision: 5);
    }

    [Theory]
    [InlineData(0f, 0.1f, 0.2f, false)]
    [InlineData(0f, 0.3f, 0.2f, true)]
    [InlineData(7.9f, 0.1f, 0.2f, false)]
    [InlineData(7.7f, 0.1f, 0.2f, true)]
    public void AnimatorDirectionReportsWhetherTheSourceTurnLimitClamped(float current, float target, float step,
        bool expectedClamped)
    {
        _ = AvatarDirection.Approach(current, target, step, out var clamped);
        Assert.Equal(expectedClamped, clamped);
    }

    private static Bone MakeBone(string name, string parent, Vector3 translation, Quaternion? rotation = null) =>
        new(name, parent, [], translation, rotation ?? Quaternion.Identity, [0, 0, name == "ROOT" ? 0 : 1], [0, 0]);

    private static Skeleton Dog() => WithBones(
        MakeBone("ROOT", "NULL", new Vector3(1, 0, 0)),
        MakeBone("PELVIS", "ROOT", new Vector3(0, 2, 0)),
        MakeBone("HEAD", "PELVIS", new Vector3(0, 0, 3)));

    private static Skeleton WithBones(params Bone[] bones) => new("dog", bones);

    /// <summary>Creates a motion where <paramref name="translationBase"/> and
    /// <paramref name="rotationBase"/> define the base offsets for translation and rotation frames in the
    /// curve channel array.</summary>
    private static Motion MakeMotion(string name, int frames, int translationBase = 0, int rotationBase = 0) =>
        new(name, frames, 0f, [0, 0, translationBase, rotationBase], [], []);

    private static Animation MakeAnimation(int translationCount, int rotationCount, params Motion[] motions) =>
        new("walk", "xskill-walk", [0f, 0f], 0, translationCount, rotationCount, motions);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected} but was {actual}");
    }

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1 - 1e-5f, $"expected {expected} but was {actual}");
    }

    private static BonePose Named(IReadOnlyList<BonePose> poses, string name) => Assert.Single(poses, pose => pose.Name == name);

    [Fact]
    public void UnanimatedBonesKeepTheBindPoseAndComposeWithTheirParents()
    {
        var animation = MakeAnimation(1, 1, MakeMotion("ROOT", 1));
        var curves = new AnimationCurves([5, 0, 0], [0, 0, Half, Half]);

        var poses = Pose.Sample(Dog(), animation, curves, 0);

        var root = Named(poses, "ROOT");
        Assert.True(root.Animated);
        AssertVector(new Vector3(5, 0, 0), root.LocalTranslation);
        AssertRotation(QuarterTurnZ, root.LocalRotation);
        AssertVector(new Vector3(5, 0, 0), root.WorldTranslation);
        AssertRotation(new Quaternion(0, 0, -Half, Half), root.WorldRotation);

        var pelvis = Named(poses, "PELVIS");
        Assert.False(pelvis.Animated);
        AssertVector(new Vector3(0, 2, 0), pelvis.LocalTranslation);
        AssertRotation(Quaternion.Identity, pelvis.LocalRotation);
        AssertVector(new Vector3(7, 0, 0), pelvis.WorldTranslation);
        AssertRotation(new Quaternion(0, 0, -Half, Half), pelvis.WorldRotation);

        var head = Named(poses, "HEAD");
        Assert.False(head.Animated);
        AssertVector(new Vector3(7, 0, 3), head.WorldTranslation);
        AssertRotation(new Quaternion(0, 0, -Half, Half), head.WorldRotation);
    }

    [Fact]
    public void LocomotionStridesUseTheTwoSourceMarkerBoneZDifferences()
    {
        var skeleton = WithBones(
            MakeBone("MARKER_ONE", "NULL", Vector3.Zero),
            MakeBone("MARKER_TWO", "NULL", Vector3.Zero));
        var animation = MakeAnimation(4, 0,
            MakeMotion("MARKER_ONE", 2, translationBase: 0, rotationBase: -1),
            MakeMotion("MARKER_TWO", 2, translationBase: 2, rotationBase: -1));
        var curves = new AnimationCurves(
            [0, 0, 10, 0, 0, 20, 0, 0, 3, 0, 0, 7], []);

        var strides = Pose.LocomotionStrides(skeleton, animation, curves,
            markerOneFrame: 0, markerOneBone: "MARKER_ONE",
            markerTwoFrame: 1, markerTwoBone: "MARKER_TWO");

        Assert.Equal((20f - 3f) * (16f / 3f), strides.MarkerOneStride, precision: 5);
        Assert.Equal((7f - 20f) * (16f / 3f), strides.MarkerTwoStride, precision: 5);
    }

    [Theory]
    [InlineData(0.1f, 0.6f, 0.2f, 170f)]
    [InlineData(0.2f, 0.6f, 0.2f, 174f)]
    [InlineData(0.59f, 0.6f, 0.2f, 174f)]
    [InlineData(0.6f, 0.6f, 0.2f, 170f)]
    [InlineData(0.9f, 0.6f, 0.2f, 170f)]
    public void LocomotionStrideSelectionUsesTheSourceMarkerInterval(float phase, float markerOne,
        float markerTwo, float expected)
    {
        var actual = Pose.LocomotionStrideAtPhase(phase, markerOne, markerTwo,
            markerOneStride: 170f, markerTwoStride: 174f);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.1f, 1f)]
    [InlineData(0.2f, 4f)]
    [InlineData(0.6f, 6f)]
    [InlineData(0.9f, 3f)]
    public void LocomotionPacingTargetsTheNextSourceMarkerAndWraps(float phase, float expectedFrames)
    {
        Assert.Equal(expectedFrames,
            Pose.FramesToNextLocomotionMarker(phase, markerOnePhase: 0.6f, markerTwoPhase: 0.2f, frameCount: 10),
            precision: 5);
    }

    [Theory]
    [InlineData(0.5f, 50f, 100f, 1000f, 2f, 0.5f, 100)]
    [InlineData(0.3f, 10f, 100f, 1000f, 2f, 0.2f, 104)]
    [InlineData(0.1f, 100f, 100f, 2000f, 1f, 1f, 200)]
    public void LocomotionFadeUsesSourceWeightAndMarkerDeadline(float phase, float distance, float stride,
        float skillDuration, float walkingScale, float expectedWeight, int expectedDuration)
    {
        var fade = Pose.LocomotionFade(distance, stride, phase,
            markerOnePhase: 0.51f, markerTwoPhase: 0.2f, skillDurationMilliseconds: skillDuration,
            walkingScale: walkingScale);

        Assert.Equal((expectedWeight, expectedDuration), fade);
    }

    [Fact]
    public void LocomotionFadeDoesNotStartOutsideTheSelectedStride()
    {
        Assert.Null(Pose.LocomotionFade(distance: 100.01f, stride: 100f, phase: 0.4f,
            markerOnePhase: 0.6f, markerTwoPhase: 0.2f, skillDurationMilliseconds: 1000f,
            walkingScale: 1f));
    }

    [Fact]
    public void EachMotionReadsItsOwnTranslationAndRotationBase()
    {
        var curves = new AnimationCurves(
            [0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0],
            [0, 0, 0, 1, 0, 0, Half, Half, 0, 0, 0, 1, 0, 0, 0, 1]);
        var animation = MakeAnimation(4, 4, MakeMotion("PELVIS", 2, translationBase: 2, rotationBase: 1));

        var first = Named(Pose.Sample(Dog(), animation, curves, 0), "PELVIS");
        AssertVector(new Vector3(2, 0, 0), first.LocalTranslation);
        AssertRotation(QuarterTurnZ, first.LocalRotation);

        var second = Named(Pose.Sample(Dog(), animation, curves, 1), "PELVIS");
        AssertVector(new Vector3(3, 0, 0), second.LocalTranslation);
        AssertRotation(Quaternion.Identity, second.LocalRotation);
    }

    [Fact]
    public void SecondaryAnimationLayersOverPrimaryPoseAndRecomputesWorldBones()
    {
        var skeleton = Dog();
        var primary = Pose.Sample(skeleton, MakeAnimation(1, 0,
            new Motion("ROOT", 1, 0f, [0, 0, 0, -1], [], [])), new AnimationCurves([5, 0, 0], []), 0);
        var carry = new Animation("carry", "carry", [0f, 0f], 0, 0, 1,
            [new Motion("HEAD", 1, 0f, [0, 0, -1, 0], [], [])]);

        var layered = Pose.Sample(skeleton, carry, new AnimationCurves([], [0, 0, Half, Half]), 0, primary);

        AssertVector(new Vector3(5, 0, 0), Named(layered, "ROOT").LocalTranslation);
        AssertRotation(QuarterTurnZ, Named(layered, "HEAD").LocalRotation);
        AssertVector(new Vector3(5, 2, 3), Named(layered, "HEAD").WorldTranslation);
        AssertRotation(new Quaternion(0, 0, -Half, Half), Named(layered, "HEAD").WorldRotation);
    }

    [Fact]
    public void PracticeWeightBlendsItsTracksIntoTheAlreadyAppliedPose()
    {
        var skeleton = Dog();
        var basis = Pose.Sample(skeleton, MakeAnimation(2, 0,
            new Motion("ROOT", 1, 0f, [0, 0, 0, -1], [], []),
            new Motion("PELVIS", 1, 0f, [0, 0, 1, -1], [], [])), new AnimationCurves([5, 0, 0, 5, 0, 0], []), 0);
        var overlay = MakeAnimation(2, 0,
            new Motion("ROOT", 1, 0f, [0, 0, 0, -1], [], []),
            new Motion("PELVIS", 1, 0f, [0, 0, 1, -1], [], []));

        var halfPose = Pose.Sample(skeleton, overlay, new AnimationCurves([9, 0, 0, 9, 0, 0], []), 0, basis, 0.5f);
        var zeroPose = Pose.Sample(skeleton, overlay, new AnimationCurves([9, 0, 0, 9, 0, 0], []), 0, basis, 0f);

        // When a bone's blend flag is disabled, its transform is assigned directly for any positive weight
        // rather than interpolated.
        AssertVector(new Vector3(7, 0, 0), Named(halfPose, "PELVIS").LocalTranslation);
        AssertVector(new Vector3(9, 0, 0), Named(halfPose, "ROOT").LocalTranslation);
        AssertVector(new Vector3(5, 0, 0), Named(zeroPose, "PELVIS").LocalTranslation);
        AssertVector(new Vector3(5, 0, 0), Named(zeroPose, "ROOT").LocalTranslation);
    }

    [Fact]
    public void AMotionWithoutTranslationChannelKeepsTheBoneTranslation()
    {
        // A translation base index of -1 indicates rotation-only motion channels, leaving bone translation
        // at the bind pose.
        var animation = MakeAnimation(1, 1, MakeMotion("PELVIS", 1, translationBase: -1, rotationBase: 0));
        var curves = new AnimationCurves([9, 0, 0], [0, 0, Half, Half]);

        var pelvis = Named(Pose.Sample(Dog(), animation, curves, 0), "PELVIS");
        AssertVector(new Vector3(0, 2, 0), pelvis.LocalTranslation);
        AssertRotation(QuarterTurnZ, pelvis.LocalRotation);
    }

    [Fact]
    public void FramesClampToTheFirstMotionsFrameCount()
    {
        var animation = MakeAnimation(2, 2, MakeMotion("ROOT", 2));
        var curves = new AnimationCurves([0, 0, 0, 7, 0, 0], [0, 0, 0, 1, 0, 0, 0, 1]);

        AssertVector(new Vector3(7, 0, 0), Named(Pose.Sample(Dog(), animation, curves, 9), "ROOT").LocalTranslation);
        AssertVector(new Vector3(0, 0, 0), Named(Pose.Sample(Dog(), animation, curves, -4), "ROOT").LocalTranslation);
    }

    [Fact]
    public void FractionalFramesBlendTranslationLinearlyAndRotationAlongTheShortArc()
    {
        var animation = MakeAnimation(2, 2, MakeMotion("ROOT", 2));
        var curves = new AnimationCurves([0, 0, 0, 2, 0, 0], [0, 0, 0, 1, 0, 0, Half, Half]);

        var halfway = Named(Pose.Sample(Dog(), animation, curves, 0.5), "ROOT");
        AssertVector(new Vector3(1, 0, 0), halfway.LocalTranslation);
        AssertRotation(new Quaternion(0, 0, MathF.Sin(MathF.PI / 8), MathF.Cos(MathF.PI / 8)), halfway.LocalRotation);

        AssertVector(new Vector3(2, 0, 0), Named(Pose.Sample(Dog(), animation, curves, 1.5), "ROOT").LocalTranslation);
    }

    [Fact]
    public void SuppressedFrameInterpolationUsesTheCurrentIntegerFrame()
    {
        var animation = MakeAnimation(2, 0, MakeMotion("ROOT", 2, rotationBase: -1));
        var curves = new AnimationCurves([4, 0, 0, 8, 0, 0], []);

        var pose = Named(Pose.Sample(Dog(), animation, curves, 0.5, interpolateFrame: false), "ROOT");

        AssertVector(new Vector3(4, 0, 0), pose.LocalTranslation);
    }

    [Fact]
    public void LoopingPoseInterpolatesFromItsLastFrameBackToFrameZero()
    {
        var animation = MakeAnimation(2, 0, MakeMotion("ROOT", 2, rotationBase: -1));
        var curves = new AnimationCurves([4, 0, 0, 8, 0, 0], []);

        var seam = Named(Pose.Sample(Dog(), animation, curves, 1.5, loop: true), "ROOT");
        var exactEndpoint = Named(Pose.Sample(Dog(), animation, curves, 2, loop: true), "ROOT");

        AssertVector(new Vector3(6, 0, 0), seam.LocalTranslation);
        AssertVector(new Vector3(8, 0, 0), exactEndpoint.LocalTranslation);
    }

    [Fact]
    public void TheShorterArcIsUsedWhenTheKeysAreOnOppositeHemispheres()
    {
        var animation = MakeAnimation(2, 2, MakeMotion("ROOT", 2));
        // Quaternion interpolation takes the shortest spherical arc, correctly blending antipodal rotations
        // with negated signs.
        var curves = new AnimationCurves([0, 0, 0, 0, 0, 0], [0, 0, 0, 1, 0, 0, -Half, -Half]);

        var halfway = Named(Pose.Sample(Dog(), animation, curves, 0.5), "ROOT");
        AssertRotation(new Quaternion(0, 0, MathF.Sin(MathF.PI / 8), MathF.Cos(MathF.PI / 8)), halfway.LocalRotation);
    }

    [Fact]
    public void QuaternionBlendPreservesResultsWithinTheSourceUnitMagnitudeTolerance()
    {
        const float w = 1.000005f;
        var animation = MakeAnimation(0, 1, MakeMotion("ROOT", 1, translationBase: -1));
        var curves = new AnimationCurves([], [0, 0, 0, w]);

        var rotation = Named(Pose.Sample(Dog(), animation, curves, 0), "ROOT").LocalRotation;

        Assert.Equal(w, rotation.W);
    }

    [Fact]
    public void QuaternionBlendUsesIdentityForAResultBelowTheSourceMagnitudeTolerance()
    {
        var animation = MakeAnimation(0, 2, MakeMotion("ROOT", 2, translationBase: -1));
        var curves = new AnimationCurves([], [0, 0, 0, 0.000005f, 0, 0, 0, 0.000005f]);

        var rotation = Named(Pose.Sample(Dog(), animation, curves, 0.5), "ROOT").LocalRotation;

        Assert.Equal(Quaternion.Identity, rotation);
    }

    [Fact]
    public void MotionsNamingNoBoneAreIgnoredAndNamesMatchExactly()
    {
        var animation = MakeAnimation(1, 1, MakeMotion("pelvis", 1));
        var curves = new AnimationCurves([9, 0, 0], [0, 0, 0, 1]);

        var poses = Pose.Sample(Dog(), animation, curves, 0);

        Assert.All(poses, pose => Assert.False(pose.Animated));
        AssertVector(new Vector3(0, 2, 0), Named(poses, "PELVIS").LocalTranslation);
    }

    [Fact]
    public void ParentsAfterChildrenComposeTheSameWorldTransform()
    {
        var animation = MakeAnimation(1, 1, MakeMotion("ROOT", 1));
        var curves = new AnimationCurves([5, 0, 0], [0, 0, Half, Half]);
        var inOrder = Pose.Sample(Dog(), animation, curves, 0);

        var reversed = WithBones(
            MakeBone("HEAD", "PELVIS", new Vector3(0, 0, 3)),
            MakeBone("PELVIS", "ROOT", new Vector3(0, 2, 0)),
            MakeBone("ROOT", "NULL", new Vector3(1, 0, 0)));
        var poses = Pose.Sample(reversed, animation, curves, 0);

        foreach (var name in new[] { "ROOT", "PELVIS", "HEAD" })
        {
            AssertVector(Named(inOrder, name).WorldTranslation, Named(poses, name).WorldTranslation);
            AssertRotation(Named(inOrder, name).WorldRotation, Named(poses, name).WorldRotation);
        }
    }

    [Fact]
    public void ABoneWhoseParentIsMissingIsARoot()
    {
        var skeleton = new Skeleton("dog", [MakeBone("LOOSE", "MISSING", new Vector3(1, 2, 3))]);
        var animation = MakeAnimation(0, 0);

        var pose = Assert.Single(Pose.Sample(skeleton, animation, new AnimationCurves([], []), 0));
        AssertVector(new Vector3(1, 2, 3), pose.WorldTranslation);
    }

    [Fact]
    public void NoMotionsGivesTheBindPoseComposed()
    {
        var animation = MakeAnimation(0, 0);

        var poses = Pose.Sample(Dog(), animation, new AnimationCurves([], []), 3);

        Assert.All(poses, pose => Assert.False(pose.Animated));
        AssertVector(new Vector3(1, 0, 0), Named(poses, "ROOT").WorldTranslation);
        AssertVector(new Vector3(1, 2, 0), Named(poses, "PELVIS").WorldTranslation);
        AssertVector(new Vector3(1, 2, 3), Named(poses, "HEAD").WorldTranslation);
    }

    [Fact]
    public void AMotionReadingPastTheCurveArrayIsAnError()
    {
        var animation = MakeAnimation(1, 2, MakeMotion("ROOT", 2));
        var curves = new AnimationCurves([0, 0, 0], [0, 0, 0, 1, 0, 0, 0, 1]);

        var error = Assert.Throws<InvalidDataException>(() => Pose.Sample(Dog(), animation, curves, 1));
        Assert.Contains("translation frame 1", error.Message);
    }

    [Fact]
    public void ParentCyclesAreAnError()
    {
        var skeleton = new Skeleton("dog", [MakeBone("ROOT", "PELVIS", Vector3.Zero), MakeBone("PELVIS", "ROOT", Vector3.Zero)]);

        Assert.Throws<InvalidDataException>(() => Pose.Sample(skeleton, MakeAnimation(0, 0), new AnimationCurves([], []), 0));
    }
}
