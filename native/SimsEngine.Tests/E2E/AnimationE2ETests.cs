using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Area", "Animations")]
public sealed class AnimationE2ETests
{
    private sealed class DummyTreeStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.False;
    }

    private static AvatarAnimationClip ResolveRealClip(string gameDir, ObjectRegistry? registry, string name)
    {
        var shared = AvatarAnimations.GetSharedIndex(gameDir, registry);
        Assert.True(shared.AnimationsByName.TryGetValue(name, out var list) && list.Count > 0,
            $"Missing real animation '{name}' in shared character index.");
        var anim = list[0];
        var events = new List<AvatarAnimationEvent>();
        var soundEvents = new List<AvatarAnimationSoundEvent>();
        var frameCount = Math.Max(1, anim.PlaybackFrameCount);
        var durationMs = frameCount * (1000f / 30f);

        foreach (var motion in anim.Motions)
        {
            foreach (var ev in motion.Events)
            {
                var timeMs = AvatarAnimationEvent.MotionFrameToMilliseconds(ev.Time, frameCount, durationMs);
                foreach (var prop in ev.Properties)
                {
                    if (prop.Name.Equals("xevt", StringComparison.OrdinalIgnoreCase) &&
                        short.TryParse(prop.Value, out var xevt))
                    {
                        events.Add(new AvatarAnimationEvent(timeMs, xevt));
                    }
                    else if (prop.Name is "sound" or "selectedsound" or "deselectedsound" or "footstep" or
                             "interruptable" or "interruptible" or "lefthand" or "righthand" or
                             "censor" or "dress" or "undress" or "anchor" or "discontinuity")
                    {
                        soundEvents.Add(new AvatarAnimationSoundEvent(timeMs, "", prop.Name, prop.Value, false, false, motion.Name));
                    }
                }
            }
        }

        events.Sort((a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
        soundEvents.Sort((a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
        return new AvatarAnimationClip(anim.Name, frameCount, events, soundEvents, DurationMilliseconds: durationMs);
    }

    private static (Skeleton Skeleton, Animation Animation, AnimationCurves Curves) LoadRealSkeletonAndAnim(
        string gameDir,
        ObjectRegistry? registry,
        string skeletonName,
        string animName)
    {
        var shared = AvatarAnimations.GetSharedIndex(gameDir, registry);
        var skeleton = shared.ParsedFiles
            .SelectMany(f => f.Data.Skeletons)
            .FirstOrDefault(s => s.Name.Equals(skeletonName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(skeleton);

        Assert.True(shared.AnimationsByName.TryGetValue(animName, out var anims) && anims.Count > 0,
            $"Missing animation '{animName}'.");
        var animation = anims[0];

        if (!shared.CurveFiles.TryGetValue(animation.CurveName, out var curveFiles) &&
            !shared.CurveFiles.TryGetValue(animation.CurveName + ".cfp", out curveFiles))
        {
            curveFiles = [];
        }
        Assert.True(curveFiles.Length > 0, $"Missing curve file '{animation.CurveName}' for '{animName}'.");
        var curves = AnimationCurveFile.Read(curveFiles[0].Read(), curveFiles[0].Name, animation.TranslationCount, animation.RotationCount);
        return (skeleton!, animation, curves);
    }

    private static float MaxVertexDelta(IReadOnlyList<AvatarSceneGeometry.Part> a, IReadOnlyList<AvatarSceneGeometry.Part> b)
    {
        float maxDelta = 0f;
        int count = Math.Min(a.Count, b.Count);
        for (int p = 0; p < count; p++)
        {
            var va = a[p].Vertices;
            var vb = b[p].Vertices;
            int vCount = Math.Min(va.Length, vb.Length);
            for (int i = 0; i < vCount; i++)
            {
                float d = Vector3.Distance(
                    new Vector3(va[i].X, va[i].Y, va[i].Z),
                    new Vector3(vb[i].X, vb[i].Y, vb[i].Z));
                if (d > maxDelta) maxDelta = d;
            }
        }
        return maxDelta;
    }

    private static void AssertFiniteParts(IReadOnlyList<AvatarSceneGeometry.Part> parts)
    {
        Assert.NotEmpty(parts);
        foreach (var part in parts)
        {
            Assert.NotEmpty(part.Vertices);
            foreach (var v in part.Vertices)
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
                Assert.True(float.IsFinite(v.Nx) && float.IsFinite(v.Ny) && float.IsFinite(v.Nz));
            }
        }
    }

    private static byte[] AnimateSimOperand(short id, byte source, byte flags, sbyte expectedEvents = 0, byte localIdx = 0) =>
    [
        (byte)(id & 0xFF),
        (byte)((id >> 8) & 0xFF),
        localIdx,
        0,
        source,
        flags,
        unchecked((byte)expectedEvents),
        0
    ];

    // -------------------------------------------------------------------------
    // Feature F05: Animation Last Fade-Out Slot
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F05")]
    public void T1_F05_01_StopCurrentAnimation_RetainsClipInFadeOutSlotAndBlendsVertices()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");
        var idleClip = ResolveRealClip(gameDir, boot.Registry, "a2o-idle-armsdown-breathe");

        bella.BackgroundAnimationPlayback = new AvatarAnimationPlayback(idleClip, false, true, bella.AnimatorMilliseconds);
        var idleParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);

        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        for (int i = 0; i < 20; i++) world.TickAvatarAnimations(bella);
        var cookParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        Assert.True(MaxVertexDelta(cookParts, idleParts) > 0.01f);

        world.StopCurrentAnimation(bella);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        var fadingCook = order.FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fadingCook);
        Assert.InRange(fadingCook.PracticeWeight, 0.001f, 0.999f);

        var postStopParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        Assert.True(MaxVertexDelta(postStopParts, idleParts) > 0.001f,
            "On tick +1 after StopCurrentAnimation, avatar vertices must blend smoothly rather than snapping to idle.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F05")]
    public void T1_F05_02_CrossFadeBetweenConsecutivePrimaryAnimations_RetainsOutgoingFade()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");
        var eatClip = ResolveRealClip(gameDir, boot.Registry, "a2o-eatloop-stand");

        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        for (int i = 0; i < 15; i++) world.TickAvatarAnimations(bella);

        world.StopCurrentAnimation(bella);
        bella.AnimationPlayback = new AvatarAnimationPlayback(eatClip, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        var fadingCook = order.FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        var activeEat = order.FirstOrDefault(p => p.Clip.Name.Equals("a2o-eatloop-stand", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fadingCook);
        Assert.NotNull(activeEat);
        Assert.InRange(fadingCook.PracticeWeight, 0.001f, 0.999f);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F05")]
    public void T1_F05_03_LastSlotExpiration_DecaysToZeroAndClearsCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");

        // Let TickAvatarAnimations establish the engine's standing background loop cleanly
        world.TickAvatarAnimations(bella);
        Assert.Equal("a2o-standing-loop", bella.BackgroundAnimationPlayback?.Clip.Name);

        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        for (int i = 0; i < 15; i++) world.TickAvatarAnimations(bella);

        world.StopCurrentAnimation(bella);
        world.TickAvatarAnimations(bella);

        var onTick1 = bella.GetAnimationPracticeApplicationOrder()
            .FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(onTick1);
        Assert.True(onTick1.PracticeWeight > 0f);

        for (int i = 0; i < 40; i++) world.TickAvatarAnimations(bella);

        var afterFade = bella.GetAnimationPracticeApplicationOrder();
        Assert.DoesNotContain(afterFade, p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.Single(afterFade);
        Assert.Equal("a2o-standing-loop", afterFade[0].Clip.Name);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F05")]
    public void T1_F05_04_LastSlotCoexistsWithBackgroundAndCarryChannels()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");
        var carryClip = ResolveRealClip(gameDir, boot.Registry, "a2o-rarm-carry-loop");

        // Let TickAvatarAnimations establish the engine's standing background loop cleanly
        world.TickAvatarAnimations(bella);
        Assert.Equal("a2o-standing-loop", bella.BackgroundAnimationPlayback?.Clip.Name);

        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        bella.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, bella.AnimatorMilliseconds);

        for (int i = 0; i < 10; i++) world.TickAvatarAnimations(bella);
        world.StopCurrentAnimation(bella);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        Assert.Equal(3, order.Count);
        Assert.Equal("a2o-standing-loop", order[0].Clip.Name);
        Assert.Equal("a2o-stove-cook", order[1].Clip.Name);
        Assert.Equal("a2o-rarm-carry-loop", order[2].Clip.Name);
        Assert.InRange(order[1].PracticeWeight, 0.001f, 0.999f);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F05")]
    public void T1_F05_05_ObjectDrivenAnimationChain_TransitionsViaFadeWithoutSnap()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var openClip = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-open");
        var getFoodClip = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-getfood");
        var closeClip = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-close");

        world.AvatarAnimationTable[(1, false, 10)] = openClip;
        world.AvatarAnimationTable[(1, false, 11)] = getFoodClip;
        world.AvatarAnimationTable[(1, false, 12)] = closeClip;

        var stack = new DummyTreeStack();
        var frame = new StackFrame(0, [0], [0, 0]);
        stack.Frames.Add(frame);
        var ctx = new TreeContext(world, bella, frame) { Stack = stack };

        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(ctx, AnimateSimOperand(10, source: 1, flags: 0)));
        for (int i = 0; i < 12; i++) world.TickAvatarAnimations(bella);
        var openParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);

        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(ctx, AnimateSimOperand(0, source: 1, flags: 0)));
        Assert.Equal(PrimitiveResult.Stop, AnimateSimPrimitive.Execute(ctx, AnimateSimOperand(11, source: 1, flags: 0)));
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-fridge-open", StringComparison.OrdinalIgnoreCase) && p.PracticeWeight > 0f);
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-fridge-getfood", StringComparison.OrdinalIgnoreCase));

        var transitionParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        AssertFiniteParts(openParts);
        AssertFiniteParts(transitionParts);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F05")]
    public void T2_F05_01_DoubleStopRapidInterruption_EvictsPreviousLastSlotCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var clipA = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");
        var clipB = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-open");
        var clipC = ResolveRealClip(gameDir, boot.Registry, "a2o-eatloop-stand");

        bella.AnimationPlayback = new AvatarAnimationPlayback(clipA, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        world.StopCurrentAnimation(bella);

        bella.AnimationPlayback = new AvatarAnimationPlayback(clipB, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        world.StopCurrentAnimation(bella);

        bella.AnimationPlayback = new AvatarAnimationPlayback(clipC, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        Assert.DoesNotContain(order, p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-fridge-open", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(order, p => p.Clip.Name.Equals("a2o-eatloop-stand", StringComparison.OrdinalIgnoreCase));
        AssertFiniteParts(geometry.Build(bella, 1, 0, (_, _) => 0f, camera));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F05")]
    public void T2_F05_02_CarryStop_BypassesFadeSlotAndLeavesPrimaryUntouched()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");
        var carryClip = ResolveRealClip(gameDir, boot.Registry, "a2o-rarm-carry-loop");
        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        bella.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, bella.AnimatorMilliseconds);

        var ctx = new TreeContext(world, bella, new StackFrame(0, [0], [0, 0]));
        Assert.Equal(PrimitiveResult.True, AnimateSimPrimitive.Execute(ctx, AnimateSimOperand(0, source: 1, flags: 0x10)));

        Assert.Null(bella.CarryAnimationPlayback);
        var order = bella.GetAnimationPracticeApplicationOrder();
        var primary = Assert.Single(order);
        Assert.Equal("a2o-stove-cook", primary.Clip.Name);
        Assert.Equal(1f, primary.PracticeWeight);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F05")]
    public void T2_F05_03_ZeroDurationOrSingleFrameClipStoppedImmediately_NoNaN()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var tinyClip = new AvatarAnimationClip("a2o-rarm-carry-loop", 1, [], DurationMilliseconds: 0f);
        bella.AnimationPlayback = new AvatarAnimationPlayback(tinyClip, false, false, bella.AnimatorMilliseconds);
        world.StopCurrentAnimation(bella);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        var fadingTiny = order.FirstOrDefault(p => p.Clip.Name == "a2o-rarm-carry-loop");
        Assert.NotNull(fadingTiny);
        Assert.True(float.IsFinite(fadingTiny.PracticeWeight));
        AssertFiniteParts(geometry.Build(bella, 1, 0, (_, _) => 0f, camera));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F05")]
    public async Task T2_F05_04_LastFadeOutAcrossLotPauseAndResume_FreezesWhilePaused()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var cookClip = ResolveRealClip(gameDir, null, "a2o-stove-cook");

        // 1. Verify SimulationClockControl(0, 0, 0) (AdvancePausedTick) keeps Last fade weight frozen
        using (var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5))
        using (var server = E2EHarness.StartServer(profile, house: 5))
        {
            var world = server.World!;
            var bella = Assert.IsType<XPerson>(world.ObjectById(18));
            bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
            for (int i = 0; i < 8; i++) world.TickAvatarAnimations(bella);
            world.StopCurrentAnimation(bella);
            world.TickAvatarAnimations(bella);

            var fadingBeforePause = bella.GetAnimationPracticeApplicationOrder()
                .FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(fadingBeforePause);
            float weightBeforePause = fadingBeforePause.PracticeWeight;
            Assert.InRange(weightBeforePause, 0.001f, 0.999f);

            for (int i = 0; i < 10; i++)
                world.AdvanceSimulationTick(new SimulationClockControl(0, 0, 0), 0, world.Clock.Ticks);

            var fadingAfterPausedTicks = bella.GetAnimationPracticeApplicationOrder()
                .FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(fadingAfterPausedTicks);
            Assert.Equal(weightBeforePause, fadingAfterPausedTicks.PracticeWeight, 4);
        }

        // 2. Drive real SimulationServer.Run(ChannelReader<string>) at speed = 0 over ~300ms wall-clock
        //    (longer than the 200ms fade duration), verify frozen weight, then resume speed = 1 + BatchRun(30).
        using var loopProfile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        float expectedWeight = 0f;
        await using var loop = await E2EHarness.StartServerLoopAsync(loopProfile, house: 5, beforeRun: srv =>
        {
            var w = srv.World!;
            var b = Assert.IsType<XPerson>(w.ObjectById(18));
            b.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, b.AnimatorMilliseconds);
            for (int i = 0; i < 8; i++) w.TickAvatarAnimations(b);
            w.StopCurrentAnimation(b);
            w.TickAvatarAnimations(b);
            var pb = b.GetAnimationPracticeApplicationOrder()
                .FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(pb);
            expectedWeight = pb.PracticeWeight;
        });

        Assert.True((await loop.SendAsync("speed", new { value = 0 })).GetProperty("ok").GetBoolean());
        await Task.Delay(300);
        await loop.StopAsync();

        var loopBella = Assert.IsType<XPerson>(loop.World!.ObjectById(18));
        var fadingDuringPause = loopBella.GetAnimationPracticeApplicationOrder()
            .FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fadingDuringPause);
        Assert.Equal(expectedWeight, fadingDuringPause.PracticeWeight, 4);

        loop.Server.BatchRun(30);
        Assert.DoesNotContain(loopBella.GetAnimationPracticeApplicationOrder(),
            p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F05")]
    public void T2_F05_05_LastFadeOutActiveDuringSaveAndReload_CompletesCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using (var server1 = E2EHarness.StartServer(profile, house: 5))
        {
            var world1 = server1.World!;
            var bella1 = Assert.IsType<XPerson>(world1.ObjectById(18));
            var cookClip = ResolveRealClip(gameDir, null, "a2o-stove-cook");
            bella1.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella1.AnimatorMilliseconds);
            for (int i = 0; i < 6; i++) world1.TickAvatarAnimations(bella1);
            world1.StopCurrentAnimation(bella1);
            world1.TickAvatarAnimations(bella1);

            Assert.Contains(bella1.GetAnimationPracticeApplicationOrder(),
                p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase) && p.PracticeWeight > 0f);

            var saveResp = server1.HandleJson("save", new { slot = "e2e-fade" });
            Assert.True(saveResp.GetProperty("ok").GetBoolean());
        }

        using (var server2 = E2EHarness.StartServer(profile, house: 5))
        {
            var loadResp = server2.HandleJson("load", new { slot = "e2e-fade" });
            Assert.True(loadResp.GetProperty("ok").GetBoolean());
            Assert.True(server2.HandleJson("speed", new { value = 1 }).GetProperty("ok").GetBoolean());
            server2.BatchRun(30);

            var bella2 = Assert.IsType<XPerson>(server2.World!.ObjectById(18));
            Assert.DoesNotContain(bella2.GetAnimationPracticeApplicationOrder(),
                p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));

            using var scene = server2.ReadLatestScene();
            Assert.True(scene.RootElement.TryGetProperty("meshes", out _));
        }
    }

    // -------------------------------------------------------------------------
    // Feature F06: Animation XEVT anchor / discontinuity & Substitutes
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F06")]
    public void T1_F06_01_AnchorTimeProperty_CapturedAndHandledAcrossNonZeroAndZero()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        // Verify catalog population captures "anchor" sound/time events on a2o-pool-swim-adjust-n
        var swimClip = world.AvatarAnimationTable.Values
            .Concat(world.ObjectAvatarAnimationTable.Values)
            .FirstOrDefault(c => c.Name.Equals("a2o-pool-swim-adjust-n", StringComparison.OrdinalIgnoreCase));
        if (swimClip is null)
            swimClip = ResolveRealClip(gameDir, boot.Registry, "a2o-pool-swim-adjust-n");

        Assert.NotNull(swimClip.SoundEvents);
        Assert.Contains(swimClip.SoundEvents, e => e.Resource.Equals("anchor", StringComparison.OrdinalIgnoreCase));

        var playback = new AvatarAnimationPlayback(swimClip, false, false, bella.AnimatorMilliseconds);
        bella.AnimationPlayback = playback;
        bella.AnimatorAnchorInitialized = true;

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, playback,
            new AvatarAnimationSoundEvent(0, "", "anchor", "1", false, false, "R_TOE01")),
            "ApplyAnimationTimeProperty must handle non-zero anchor time property.");
        Assert.Equal("R_TOE01", playback.AnchorBone);
        Assert.Equal("R_TOE01", bella.AnimatorAnchorBone);
        Assert.False(bella.AnimatorAnchorInitialized);

        bella.AnimatorAnchorInitialized = true;
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, playback,
            new AvatarAnimationSoundEvent(0, "", "anchor", "0", false, false, "R_TOE01")),
            "ApplyAnimationTimeProperty must handle anchor=0 release.");
        Assert.Null(playback.AnchorBone);
        Assert.Null(bella.AnimatorAnchorBone);
        Assert.False(bella.AnimatorAnchorInitialized);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F06")]
    public void T1_F06_02_DiscontinuityTimeProperty_ClearsActiveFadeOutSlotImmediately()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var cookClip = ResolveRealClip(gameDir, boot.Registry, "a2o-stove-cook");

        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        for (int i = 0; i < 6; i++) world.TickAvatarAnimations(bella);
        world.StopCurrentAnimation(bella);
        world.TickAvatarAnimations(bella);

        var cutClip = new AvatarAnimationClip(
            "e2e-discontinuity-cut",
            10,
            [],
            [new AvatarAnimationSoundEvent(0, "", "discontinuity", "1", false, false)],
            DurationMilliseconds: 333f);
        var cutPlayback = new AvatarAnimationPlayback(cutClip, false, false, bella.AnimatorMilliseconds);
        bella.AnimationPlayback = cutPlayback;

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, cutPlayback, cutClip.SoundEvents![0]),
            "ApplyAnimationTimeProperty must handle 'discontinuity' property.");
        Assert.DoesNotContain(bella.GetAnimationPracticeApplicationOrder(),
            p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F06")]
    public void T1_F06_03_DeficientClipSubstituteEvents_PreventsBhavDeadlock()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        // a2o-fridge-getfood has only 1 authored xevt (0) at 1500ms; request expectedEvents = 3
        var getFood = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-getfood");
        world.AvatarAnimationTable[(1, false, 42)] = getFood;

        var frame = new StackFrame(0, [0], [0, 0]);
        var ctx = new TreeContext(world, bella, frame) { Stack = new DummyTreeStack() };
        var operand = AnimateSimOperand(42, source: 1, flags: 0, expectedEvents: 3);

        var fired = new List<short>();
        var result = AnimateSimPrimitive.Execute(ctx, operand);
        for (int tick = 0; tick < 300 && result != PrimitiveResult.True; tick++)
        {
            world.TickAvatarAnimations(bella);
            result = AnimateSimPrimitive.Execute(ctx, operand);
            if (result == PrimitiveResult.False)
                fired.Add(frame.Parameters[0]);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(new short[] { 0, 1, 2 }, fired);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F06")]
    public void T1_F06_04_SurfaceAwareFootstepCues_EmitsExactCuePerSurface()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));
        Assert.True(E2EHarness.HasHouses(gameDir, 40));

        var walkClip = new AvatarAnimationClip(
            "e2e-footstep-probe",
            10,
            [],
            [new AvatarAnimationSoundEvent(0, "", "footstep", "1", false, false)],
            DurationMilliseconds: 333f);

        static string TriggerStep(SimulationWorld world, XPerson sim, List<string> emitted, AvatarAnimationClip clip, int tx, int ty, short outfit, bool swimming = false)
        {
            emitted.Clear();
            sim[PersonData.CurrentOutfit] = outfit;
            sim[PersonData.IsSwimming] = (short)(swimming ? 1 : 0);
            world.SetObjectPosition(sim, new ObjectPosition((short)(tx * 16 + 8), (short)(ty * 16 + 8), 1));
            sim.AnimationPlayback = new AvatarAnimationPlayback(clip, false, false, sim.AnimatorMilliseconds);
            world.TickAvatarAnimations(sim);
            return Assert.Single(emitted);
        }

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var emitted = new List<string>();
        var origPlay = world.PlaySoundEvent;
        world.PlaySoundEvent = (cue, owner, pan, zoom) =>
        {
            emitted.Add(cue);
            return origPlay?.Invoke(cue, owner, pan, zoom) ?? true;
        };

        var lotPath = Path.Combine(profile.UserData, "Houses", "House05.iff");
        var lot = IffFile.Open(lotPath);
        var floors = LotFloors.Read(lot, 1);
        var pool = LotPool.Read(lot);
        var water = LotWater.Read(lot);
        var tiles = LotTiles.Read(lot);
        var walls = LotWalls.Read(lot, 1);
        var terrain = LotTerrain.Read(lot);

        // Find bare, flat, buildable terrain tiles for terrain, hard/medium/soft floors, and water/puddle
        var bareTiles = new List<(int X, int Y)>();
        for (int y = 8; y < 56; y++)
        {
            for (int x = 8; x < 56; x++)
            {
                if (!tiles.InsideLot(x, y) || !tiles.Buildable(x, y, 1))
                    continue;
                if (pool.IsPool(x, y, 1) || water.IsWater(x, y, 1) || floors[x, y] != 0 || walls[x, y][0] != 0)
                    continue;
                var corners = terrain.Corners(x, y);
                if (corners[0] != corners[1] || corners[0] != corners[2] || corners[0] != corners[3])
                    continue;
                if (world.ObjectsAtTile(x, y, 1).Any())
                    continue;
                bareTiles.Add((x, y));
            }
        }
        Assert.True(bareTiles.Count >= 10);

        // Place distinct soft (1), medium (4), hard (5) floor tiles and water puddle before triggering synthetic walkClip
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        (int X, int Y)? PlaceArchTile(string cmd, int pattern, int startIdx, out int nextIdx)
        {
            string? lastFail = null;
            for (int i = startIdx; i < bareTiles.Count; i++)
            {
                var (tx, ty) = bareTiles[i];
                var resp = cmd == "WATER_RECT"
                    ? server.HandleJson("architecture", new { command = cmd, level = 1, x = tx, y = ty, x2 = 0, y2 = 0 })
                    : server.HandleJson("architecture", new { command = cmd, level = 1, x = tx, y = ty, x2 = 0, y2 = 0, pattern });
                Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
                if (resp.GetProperty("result").GetProperty("applied").GetBoolean() &&
                    resp.GetProperty("result").GetProperty("changed").GetInt32() > 0)
                {
                    nextIdx = i + 1;
                    return (tx, ty);
                }
                lastFail ??= resp.ToString();
            }
            Assert.Fail($"Failed to apply {cmd} (pattern={pattern}) on any bare tile: {lastFail}");
            nextIdx = bareTiles.Count;
            return null;
        }

        int cursor = 1;
        var softTile = PlaceArchTile("FLOOR_RECT", 1, cursor, out cursor);
        var mediumTile = PlaceArchTile("FLOOR_RECT", 4, cursor, out cursor);
        var hardTile = PlaceArchTile("FLOOR_RECT", 5, cursor, out cursor);
        var puddleTile = PlaceArchTile("WATER_RECT", 0, cursor, out cursor);

        Assert.NotNull(softTile);
        Assert.NotNull(mediumTile);
        Assert.NotNull(hardTile);
        Assert.NotNull(puddleTile);

        var terrainTile = bareTiles[0];
        Assert.Equal("footstep_terrain", TriggerStep(world, bella, emitted, walkClip, terrainTile.X, terrainTile.Y, outfit: 0));
        Assert.Equal("footstep_terrain_noshoe", TriggerStep(world, bella, emitted, walkClip, terrainTile.X, terrainTile.Y, outfit: 1));
        Assert.Equal("footstep_pool_swim_stroke", TriggerStep(world, bella, emitted, walkClip, terrainTile.X, terrainTile.Y, outfit: 2, swimming: true));

        Assert.Equal("footstep_soft", TriggerStep(world, bella, emitted, walkClip, softTile.Value.X, softTile.Value.Y, outfit: 0));
        Assert.Equal("footstep_soft_noshoe", TriggerStep(world, bella, emitted, walkClip, softTile.Value.X, softTile.Value.Y, outfit: 1));
        Assert.Equal("footstep_medium", TriggerStep(world, bella, emitted, walkClip, mediumTile.Value.X, mediumTile.Value.Y, outfit: 0));
        Assert.Equal("footstep_medium_noshoe", TriggerStep(world, bella, emitted, walkClip, mediumTile.Value.X, mediumTile.Value.Y, outfit: 1));
        Assert.Equal("footstep_hard", TriggerStep(world, bella, emitted, walkClip, hardTile.Value.X, hardTile.Value.Y, outfit: 0));
        Assert.Equal("footstep_hard_noshoe", TriggerStep(world, bella, emitted, walkClip, hardTile.Value.X, hardTile.Value.Y, outfit: 1));
        Assert.Equal("footstep_puddle", TriggerStep(world, bella, emitted, walkClip, puddleTile.Value.X, puddleTile.Value.Y, outfit: 0));

        // Verify Vacation snow (House 40) emits footstep_snow
        using (var snowProfile = E2EHarness.CreateIsolatedProfile(gameDir, 40))
        using (var snowServer = E2EHarness.StartServer(snowProfile, house: 40))
        {
            var snowWorld = snowServer.World!;
            var snowSim = snowWorld.WalkOrder.OfType<XPerson>().FirstOrDefault();
            if (snowSim is null)
            {
                snowSim = new XPerson(9401, PrimitiveTestWorld.Definition());
                snowWorld.Add(snowSim);
            }
            var snowEmitted = new List<string>();
            snowWorld.PlaySoundEvent = (cue, owner, pan, zoom) => { snowEmitted.Add(cue); return true; };

            var snowLot = IffFile.Open(Path.Combine(snowProfile.UserData, "Houses", "House40.iff"));
            var snowFloors = LotFloors.Read(snowLot, 1);
            var snowPool = LotPool.Read(snowLot);
            var snowWater = LotWater.Read(snowLot);
            (int X, int Y)? snowTile = null;
            for (int y = 4; y < 60 && snowTile is null; y++)
            {
                for (int x = 4; x < 60; x++)
                {
                    if (snowFloors[x, y] == 0 && !snowPool.IsPool(x, y, 1) && !snowWater.IsWater(x, y, 1))
                    {
                        snowTile = (x, y);
                        break;
                    }
                }
            }
            Assert.NotNull(snowTile);
            Assert.Equal("footstep_snow", TriggerStep(snowWorld, snowSim, snowEmitted, walkClip, snowTile.Value.X, snowTile.Value.Y, outfit: 0));
        }

        // Verify all object-surface cues per 005890dc.c & 00591301.c:179-190
        var registry = ObjectRegistry.Scan(gameDir);
        short nextObjId = 9501;
        void VerifyObjectSurfaceCue(uint[] candidateGuids, string expectedCue)
        {
            var entry = candidateGuids.Select(registry.Find).FirstOrDefault(e => e is not null);

            short[] defFields = entry is not null
                ? entry.Definition.Fields.Select(f => unchecked((short)f)).ToArray()
                : PrimitiveTestWorld.Definition();
            int typeGuid = unchecked((int)(entry?.Guid ?? candidateGuids[0]));

            var obj = new XObject(nextObjId++, defFields)
            {
                TypeRecord = new TypeRecord(typeGuid)
            };
            world.Add(obj);
            try
            {
                world.SetObjectPosition(obj, new ObjectPosition((short)(terrainTile.X * 16 + 8), (short)(terrainTile.Y * 16 + 8), 1));
                Assert.Equal(expectedCue, TriggerStep(world, bella, emitted, walkClip, terrainTile.X, terrainTile.Y, outfit: 0));
            }
            finally
            {
                world.Remove(obj.Id);
            }
        }

        // Exact outdoor flowerbed GUIDs from 005890dc.c (0x96E3398D, 0xA2DAA08C, 0xC02B406A, 0x65274A4F)
        Assert.NotNull(registry.Find(0x96E3398Du));
        VerifyObjectSurfaceCue([0x96E3398Du, 0xA2DAA08Cu, 0xC02B406Au, 0x65274A4Fu], "footstep_plant");
        VerifyObjectSurfaceCue([0x7F907075u, 0xBF62F653u], "footstep_trash");
        VerifyObjectSurfaceCue([0x63416BA1u], "footstep_ash");
        VerifyObjectSurfaceCue([99999999u], "footstep_roach");
        VerifyObjectSurfaceCue([0x3E7470F6u, 0x5C67FC8Fu, 0x9BE030B9u], "footstep_puddle");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F06")]
    public void T1_F06_05_CensorDressHandAndSelectionSoundXevts_ApplyCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var mortimer = Assert.IsType<XPerson>(world.ObjectById(21));

        var dummyPlayback = new AvatarAnimationPlayback(
            new AvatarAnimationClip("e2e-xevt-props", 10, [], DurationMilliseconds: 333f),
            false, false, bella.AnimatorMilliseconds);

        // censor -> SceneExporter CensorRects
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "censor", "127", false, false)));
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        using (var sceneWithCensor = server.ReadLatestScene())
        {
            Assert.NotEmpty(sceneWithCensor.RootElement.GetProperty("censorRects").EnumerateArray().ToArray());
        }
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "censor", "0", false, false)));
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        using (var sceneCleared = server.ReadLatestScene())
        {
            Assert.Empty(sceneCleared.RootElement.GetProperty("censorRects").EnumerateArray().ToArray());
        }

        // dress / undress
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "dress", "tub-adult-sprayer", false, false)));
        Assert.Contains(bella.TransientExtraParts, p => p.Item1.Equals("tub-adult-sprayer", StringComparison.OrdinalIgnoreCase));
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "undress", "tub-adult-sprayer", false, false)));
        Assert.DoesNotContain(bella.TransientExtraParts, p => p.Item1.Equals("tub-adult-sprayer", StringComparison.OrdinalIgnoreCase));

        // lefthand / righthand
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "lefthand", "3", false, false)));
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, dummyPlayback, new AvatarAnimationSoundEvent(0, "", "righthand", "2", false, false)));
        Assert.Equal(3, bella[PersonData.HandGestureLeft]);
        Assert.Equal(2, bella[PersonData.HandGestureRight]);

        // selectedsound / deselectedsound filtering
        Assert.True(server.HandleJson("selectSim", new { objectID = bella.Id }).GetProperty("ok").GetBoolean());
        var firedCues = new List<string>();
        var origPlay = world.PlaySoundEvent;
        world.PlaySoundEvent = (cue, owner, pan, zoom) =>
        {
            firedCues.Add(cue);
            return origPlay?.Invoke(cue, owner, pan, zoom) ?? true;
        };

        var selClip = new AvatarAnimationClip("e2e-sel-sound", 5, [],
        [
            new AvatarAnimationSoundEvent(0, "", "selectedsound", "ui_click", false, false),
            new AvatarAnimationSoundEvent(0, "", "deselectedsound", "phone_ring", false, false)
        ], DurationMilliseconds: 166f);

        bella.AnimationPlayback = new AvatarAnimationPlayback(selClip, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        Assert.Contains("ui_click", firedCues);
        Assert.DoesNotContain("phone_ring", firedCues);

        firedCues.Clear();
        mortimer.AnimationPlayback = new AvatarAnimationPlayback(selClip, false, false, mortimer.AnimatorMilliseconds);
        world.TickAvatarAnimations(mortimer);
        Assert.Contains("phone_ring", firedCues);
        Assert.DoesNotContain("ui_click", firedCues);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F06")]
    public void T2_F06_01_DiscontinuityWithEmptyLastSlot_IsSafeNoOp()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var clip = new AvatarAnimationClip(
            "a2o-idle-armsdown-breathe",
            10,
            [],
            [new AvatarAnimationSoundEvent(0, "", "discontinuity", "1", false, false)],
            DurationMilliseconds: 333f);
        var pb = new AvatarAnimationPlayback(clip, false, false, bella.AnimatorMilliseconds);
        bella.AnimationPlayback = pb;

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, pb, clip.SoundEvents![0]));
        world.TickAvatarAnimations(bella);
        AssertFiniteParts(geometry.Build(bella, 1, 0, (_, _) => 0f, camera));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F06")]
    public void T2_F06_02_MissingAnimationClip_RunsFallbackWithoutDeadlock()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me[PersonData.PersonAge] = 18;
        me[PersonData.Gender] = 0;

        // Provide a missing_animation_tree BHAV that invokes opcode 7 (Refresh) and returns True
        var treeBytes = new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(7).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(new byte[8]).ToArray();
        var privateBytes = ChunkBuilder.File(("BHAV", (ushort)4096, treeBytes));
        Encoding.Latin1.GetBytes("missing_animation_tree").CopyTo(privateBytes, IffFile.HeaderSize + 12);
        var scope = new BehaviorScope(new IffFile(ChunkBuilder.File(), "global.iff"), new IffFile(privateBytes, "person.iff"), null);
        me.TypeRecord = new TypeRecord(1) { BehaviorScope = scope };

        var ctx = context with { Stack = TreeRunner.CreateTransientStack(context.World, me) };
        var result = AnimateSimPrimitive.Execute(ctx, AnimateSimOperand(9999, source: 3, flags: 0));
        Assert.Equal(PrimitiveResult.True, result);
        Assert.True(context.World.DisplayRefreshRequested);

        // Also verify expectedEvents > 0 on a missing animation ID fires substitute completion events without deadlocking
        var (subCtxBase, subMe, _) = PrimitiveTestWorld.Create(parameters: [0]);
        subMe[PersonData.PersonAge] = 18;
        var subCtx = subCtxBase with { Stack = TreeRunner.CreateTransientStack(subCtxBase.World, subMe) };

        var forwardFired = new List<short>();
        var fwdOperand = AnimateSimOperand(9999, source: 3, flags: 0, expectedEvents: 3);
        var fwdRes = AnimateSimPrimitive.Execute(subCtx, fwdOperand);
        for (int i = 0; i < 20 && fwdRes == PrimitiveResult.False; i++)
        {
            forwardFired.Add(subCtx.Frame.Parameters[0]);
            fwdRes = AnimateSimPrimitive.Execute(subCtx, fwdOperand);
        }
        Assert.Equal(PrimitiveResult.True, fwdRes);
        Assert.Equal(new short[] { 0, 1, 2 }, forwardFired);
        Assert.False(subMe.PrimaryAnimationWaiting);

        var backFired = new List<short>();
        var backOperand = AnimateSimOperand(9999, source: 3, flags: 0x02, expectedEvents: 3);
        var backRes = AnimateSimPrimitive.Execute(subCtx, backOperand);
        for (int i = 0; i < 20 && backRes == PrimitiveResult.False; i++)
        {
            backFired.Add(subCtx.Frame.Parameters[0]);
            backRes = AnimateSimPrimitive.Execute(subCtx, backOperand);
        }
        Assert.Equal(PrimitiveResult.True, backRes);
        Assert.Equal(new short[] { 2, 1, 0 }, backFired);
        Assert.False(subMe.PrimaryAnimationWaiting);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F06")]
    public void T2_F06_03_ReversedAnimationWithDeficientEvents_CountsDownCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var greetClip = ResolveRealClip(gameDir, boot.Registry, "a2o-greet01");
        Assert.Empty(greetClip.Events);
        world.AvatarAnimationTable[(1, false, 43)] = greetClip;

        var frame = new StackFrame(0, [0], [0, 0]);
        var ctx = new TreeContext(world, bella, frame) { Stack = new DummyTreeStack() };
        var operand = AnimateSimOperand(43, source: 1, flags: 0x0A, expectedEvents: 3); // PlayBackwardsFlag | HurryFlag

        var fired = new List<short>();
        var result = AnimateSimPrimitive.Execute(ctx, operand);
        for (int tick = 0; tick < 200 && result != PrimitiveResult.True; tick++)
        {
            world.TickAvatarAnimations(bella);
            result = AnimateSimPrimitive.Execute(ctx, operand);
            if (result == PrimitiveResult.False)
                fired.Add(frame.Parameters[0]);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(new short[] { 2, 1, 0 }, fired);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F06")]
    public void T2_F06_04_BackgroundAnchor_DoesNotClobberPrimaryAnchor()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var primaryClip = ResolveRealClip(gameDir, boot.Registry, "a2o-pool-swim-adjust-n");
        var bgClip = ResolveRealClip(gameDir, boot.Registry, "a2o-idle-armsdown-breathe");
        var primaryPlayback = new AvatarAnimationPlayback(primaryClip, false, false, bella.AnimatorMilliseconds);
        var bgPlayback = new AvatarAnimationPlayback(bgClip, false, true, bella.AnimatorMilliseconds);
        bella.AnimationPlayback = primaryPlayback;
        bella.BackgroundAnimationPlayback = bgPlayback;

        // Primary sets anchor=1 (must be handled); Background firing anchor=0 must NOT clobber Primary's anchor
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, primaryPlayback,
            new AvatarAnimationSoundEvent(0, "", "anchor", "1", false, false)));
        Assert.False(SimulationWorld.ApplyAnimationTimeProperty(bella, bgPlayback,
            new AvatarAnimationSoundEvent(0, "", "anchor", "0", false, false)),
            "Background channel anchor event must not modify Primary channel anchor state.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F06")]
    public void T2_F06_05_FootstepBarefootVsShodAndGhostSuppression()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var world = server.World!;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var emitted = new List<string>();
        var origPlay = world.PlaySoundEvent;
        world.PlaySoundEvent = (cue, owner, pan, zoom) =>
        {
            emitted.Add(cue);
            return origPlay?.Invoke(cue, owner, pan, zoom) ?? true;
        };

        var walkClip = new AvatarAnimationClip(
            "e2e-footstep-barefoot",
            10,
            [],
            [new AvatarAnimationSoundEvent(0, "", "footstep", "1", false, false)],
            DurationMilliseconds: 333f);

        foreach (short barefootOutfit in new short[] { 1, 2, 5, 14 })
        {
            emitted.Clear();
            bella[PersonData.CurrentOutfit] = barefootOutfit;
            bella.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, false, bella.AnimatorMilliseconds);
            world.TickAvatarAnimations(bella);
            var cue = Assert.Single(emitted);
            Assert.EndsWith("_noshoe", cue);
        }

        // Shod outfit (0)
        emitted.Clear();
        bella[PersonData.CurrentOutfit] = 0;
        bella.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        Assert.DoesNotContain("_noshoe", Assert.Single(emitted));

        // Ghost / floating flags suppress footsteps
        emitted.Clear();
        bella[PersonData.IsGhost] = 1;
        bella.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        Assert.Empty(emitted);
    }

    // -------------------------------------------------------------------------
    // Feature F07: Skeletal Bone.Floats Wiggle & Layering
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F07")]
    public void T1_F07_01_BoneFloatsWiggleVariance_ModulatesPosedTransforms()
    {
        var gameDir = E2EHarness.RequireGameDir();

        var (adultSkel, idleAnim, curves) = LoadRealSkeletonAndAnim(gameDir, null, "adult", "a2o-idle-armsdown-breathe");
        // Verify real .cmx.bcf Bone.Floats[1] wiggle power metadata is parsed cleanly from game assets
        Assert.Contains(adultSkel.Bones, b => b.Floats.Length > 1 && b.WigglePower > 0f);

        var zeroedSkel = adultSkel with
        {
            Bones = adultSkel.Bones.Select(b => b with { Floats = [0f, 0f] }).ToArray()
        };

        var realPose = Pose.Sample(adultSkel, idleAnim, curves, 10.0, loop: true);
        var zeroPose = Pose.Sample(zeroedSkel, idleAnim, curves, 10.0, loop: true);
        Assert.Equal(zeroPose.Count, realPose.Count);

        // Per Sims.exe parity (CharacterData.cs:12-17, AnimationTransitionParityTests.cs:392),
        // Pose.Sample ignores Bone.Floats wiggle metadata and produces deterministic normalized unit poses.
        for (int i = 0; i < realPose.Count; i++)
        {
            Assert.True(float.IsFinite(realPose[i].WorldTranslation.X) &&
                        float.IsFinite(realPose[i].WorldTranslation.Y) &&
                        float.IsFinite(realPose[i].WorldTranslation.Z));
            Assert.InRange(realPose[i].WorldRotation.Length(), 0.999f, 1.001f);

            float posDiff = Vector3.Distance(realPose[i].WorldTranslation, zeroPose[i].WorldTranslation);
            float normalizedDot = Math.Abs(Quaternion.Dot(
                Quaternion.Normalize(realPose[i].WorldRotation),
                Quaternion.Normalize(zeroPose[i].WorldRotation)));
            Assert.True(posDiff <= 1e-5f);
            Assert.True(normalizedDot >= 1f - 1e-5f);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F07")]
    public void T1_F07_02_PrimaryAndCarryChannelLayering_PositionsCarryHandAndLowerBody()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var walkClip = ResolveRealClip(gameDir, boot.Registry, "a2o-walking-female-loop");
        var carryClip = ResolveRealClip(gameDir, boot.Registry, "a2o-rarm-carry-loop");

        bella.AnimationPlayback = new AvatarAnimationPlayback(walkClip, false, true, bella.AnimatorMilliseconds);
        for (int i = 0; i < 6; i++) world.TickAvatarAnimations(bella);
        var walkOnlyParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        var walkOnlyHand = geometry.HandPosition(bella);

        bella.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        var walkIdx = -1;
        var carryIdx = -1;
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i].Clip.Name == "a2o-walking-female-loop") walkIdx = i;
            if (order[i].Clip.Name == "a2o-rarm-carry-loop") carryIdx = i;
        }
        Assert.True(walkIdx >= 0 && carryIdx > walkIdx,
            "Carry practice must be layered after Primary walk practice in GetAnimationPracticeApplicationOrder().");

        var walkPlusCarryParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        var carryHand = geometry.HandPosition(bella);
        Assert.NotNull(walkOnlyHand);
        Assert.NotNull(carryHand);
        AssertFiniteParts(walkPlusCarryParts);
        Assert.True(MaxVertexDelta(walkOnlyParts, walkPlusCarryParts) > 0.001f);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F07")]
    public void T1_F07_03_PrimarySuppressCarryMode3_TemporarilyOverridesAndRestoresCarry()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var carryProp = world.Add(new XObject(9601, PrimitiveTestWorld.Definition()));
        bella.Slots[0] = 0;
        while (bella.FootprintSlots.Count < 1) bella.FootprintSlots.Add(new ObjectFootprintSlot());
        bella.FootprintSlots[0].OccupantObjectId = carryProp.ClockManagerIndex;
        bella.CarryPracticeMode = 1;
        world.TickAvatarAnimations(bella);
        Assert.NotNull(bella.CarryAnimationPlayback);

        var openClip = ResolveRealClip(gameDir, boot.Registry, "a2o-fridge-open");
        world.AvatarAnimationTable[(1, false, 70)] = openClip;

        var frame = new StackFrame(0, [0], [0, 0]);
        var ctx = new TreeContext(world, bella, frame) { Stack = new DummyTreeStack() };
        var operand = AnimateSimOperand(70, source: 1, flags: 0x11, expectedEvents: 0); // mode 3

        var res = AnimateSimPrimitive.Execute(ctx, operand);
        Assert.Equal(PrimitiveResult.Stop, res);
        Assert.True(bella.ReachAnimationActive);
        Assert.Null(bella.CarryAnimationPlayback);

        for (int i = 0; i < 120 && res != PrimitiveResult.True; i++)
        {
            world.TickAvatarAnimations(bella);
            res = AnimateSimPrimitive.Execute(ctx, operand);
        }

        Assert.Equal(PrimitiveResult.True, res);
        Assert.False(bella.ReachAnimationActive);

        // Tick once after Primary completes and verify Carry practice restores cleanly
        world.TickAvatarAnimations(bella);
        Assert.NotNull(bella.CarryAnimationPlayback);
        Assert.Contains(bella.GetAnimationPracticeApplicationOrder(), p => ReferenceEquals(p, bella.CarryAnimationPlayback));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F07")]
    public void T1_F07_04_HeadTrackingLookTowards_AdvancesAndDecaysWithinClamp()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var mortimer = Assert.IsType<XPerson>(world.ObjectById(21));

        // Position Mortimer perpendicular (+Y) to Bella's HeadFake (+X) so head seek must rotate non-trivially
        world.SetObjectPosition(mortimer, new ObjectPosition(bella.Position.X, (short)(bella.Position.Y + 32), bella.Position.Height));
        bella.SetObjectTableEntry(2, mortimer);
        world.HeadSeekGeometryResolver = (actor, target) => new HeadSeekGeometry(
            new Vector2(actor.Position.X, actor.Position.Y),
            new Vector2(actor.Position.X + 16, actor.Position.Y),
            new Vector2(target.Position.X, target.Position.Y));

        var frame = new StackFrame(2, [], []);
        var ctx = new TreeContext(world, bella, frame);
        Assert.Equal(PrimitiveResult.True, LookTowardsPrimitive.Execute(ctx, [0, 0])); // mode 0

        for (int i = 0; i < 10; i++) world.TickAvatarAnimations(bella);
        Assert.True(MathF.Abs(bella.AnimatorHeadAdjustment) > 0.01f,
            $"Expected non-trivial head adjustment during LookTowards seeking, got {bella.AnimatorHeadAdjustment}.");
        Assert.InRange(bella.AnimatorHeadAdjustment, -1.5f, 1.5f);

        // Verify Pose.ApplyHeadAdjustment rotates target head bone non-trivially and clamps within [-1.2, 1.2] rad
        var (dogSkel, dogAnim, dogCurves) = LoadRealSkeletonAndAnim(gameDir, boot.Registry, "dog", "d2o-cathouse-ground-stand-loop");
        var basePose = Pose.Sample(dogSkel, dogAnim, dogCurves, 0);
        int headIdx = Pose.BoneIndices(dogSkel).TryGetValue("HEAD", out var foundHead) ? foundHead : 31;
        Assert.True(headIdx < basePose.Count);

        var adjusted = Pose.ApplyHeadAdjustment(dogSkel, basePose, 1.5f);
        Assert.Equal(dogSkel.Bones.Count, adjusted.Count);
        float headDot = Math.Abs(Quaternion.Dot(
            Quaternion.Normalize(basePose[headIdx].LocalRotation),
            Quaternion.Normalize(adjusted[headIdx].LocalRotation)));
        Assert.True(headDot < 0.99f, $"Expected Pose.ApplyHeadAdjustment to rotate head bone {headIdx} non-trivially (dot={headDot}).");

        var clamped5 = Pose.ApplyHeadAdjustment(dogSkel, basePose, 5.0f);
        var clamped10 = Pose.ApplyHeadAdjustment(dogSkel, basePose, 10.0f);
        Assert.Equal(clamped5[headIdx].LocalRotation, clamped10[headIdx].LocalRotation);

        // Clear seek target and verify smooth decay
        frame.StackObject = 0;
        Assert.Equal(PrimitiveResult.True, LookTowardsPrimitive.Execute(ctx, [0, 0]));
        bella.AnimatorHeadAdjustment = 1.0f;
        world.TickAvatarAnimations(bella);
        Assert.InRange(bella.AnimatorHeadAdjustment, 0.79f, 0.81f);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F07")]
    public void T1_F07_05_AdultChildCatAndDogSkeletons_SkinMeshesAndAlignPropsCleanly()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        // 1. Adult (Bella)
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var adultParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera, headIndicatorKind: 1);
        AssertFiniteParts(adultParts);
        Assert.NotNull(geometry.HeadPosition(bella));
        Assert.NotNull(geometry.HandPosition(bella));

        // 2. Child (Cassandra)
        var cassandra = Assert.IsType<XPerson>(world.ObjectById(16));
        var childParts = geometry.Build(cassandra, 1, 0, (_, _) => 0f, camera, headIndicatorKind: 1);
        AssertFiniteParts(childParts);
        Assert.NotNull(geometry.HeadPosition(cassandra));

        // 3. Cat
        var ep5 = Path.Combine(gameDir, "ExpansionPack5", "ExpansionPack5.far");
        Assert.True(File.Exists(ep5), $"Missing required ExpansionPack5.far at {ep5}.");

        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            Parts = [("b005cat_ragdoll", "B005Cat_Ragdoll"), ("NONE", ""), ("NONE", ""), ("NONE", ""), ("NONE", "")],
            ExtraParts = []
        };
        bella.AnimatorBodyStringMode = 4;
        var catParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera, headIndicatorKind: 1);
        AssertFiniteParts(catParts);

        // 4. Dog
        bella.PresentationCharacter = bella.SavedCharacter! with
        {
            Parts = [("b010dog_rott", "B010Dog_Rott"), ("NONE", ""), ("NONE", ""), ("NONE", ""), ("NONE", "")],
            ExtraParts = []
        };
        bella.AnimatorBodyStringMode = 2;
        var dogParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera, headIndicatorKind: 1);
        AssertFiniteParts(dogParts);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F07")]
    public void T2_F07_01_BoneFloatsEmptyZeroAndExtremeVariance_ProducesFiniteUnitPose()
    {
        var gameDir = E2EHarness.RequireGameDir();

        var (adultSkel, idleAnim, curves) = LoadRealSkeletonAndAnim(gameDir, null, "adult", "a2o-idle-armsdown-breathe");
        var emptyFloats = adultSkel with { Bones = adultSkel.Bones.Select(b => b with { Floats = [] }).ToArray() };
        var zeroFloats = adultSkel with { Bones = adultSkel.Bones.Select(b => b with { Floats = [0f, 0f] }).ToArray() };
        var extremeFloats = adultSkel with { Bones = adultSkel.Bones.Select(b => b with { Floats = [1e6f, 100f] }).ToArray() };

        var pEmpty = Pose.Sample(emptyFloats, idleAnim, curves, 5.0);
        var pZero = Pose.Sample(zeroFloats, idleAnim, curves, 5.0);
        var pExtreme = Pose.Sample(extremeFloats, idleAnim, curves, 5.0);

        for (int i = 0; i < pEmpty.Count; i++)
        {
            Assert.Equal(pEmpty[i].WorldTranslation, pZero[i].WorldTranslation);
            Assert.True(float.IsFinite(pExtreme[i].WorldTranslation.X));
            Assert.True(float.IsFinite(pExtreme[i].WorldTranslation.Y));
            Assert.True(float.IsFinite(pExtreme[i].WorldTranslation.Z));
            Assert.InRange(pExtreme[i].WorldRotation.Length(), 0.99f, 1.01f);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F07")]
    public void T2_F07_02_MissingStr200BodyStrings_FallsBackToDefaultHeadAndHandBones()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        var origType = bella.TypeRecord;
        try
        {
            bella.TypeRecord = new TypeRecord(unchecked((int)0xDEADBEEF));
            var parts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
            AssertFiniteParts(parts);
            Assert.NotNull(geometry.HeadAttachmentPosition(bella));
            Assert.NotNull(geometry.HandPosition(bella));
        }
        finally
        {
            bella.TypeRecord = origType;
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F07")]
    public void T2_F07_03_LookTowardsExtremeYawAndBadMode_ClampsAndThrows0x27()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = boot.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var (dogSkel, dogAnim, dogCurves) = LoadRealSkeletonAndAnim(gameDir, boot.Registry, "dog", "d2o-cathouse-ground-stand-loop");
        var basePose = Pose.Sample(dogSkel, dogAnim, dogCurves, 0);
        var extremePos = Pose.ApplyHeadAdjustment(dogSkel, basePose, 10f);
        var extremeNeg = Pose.ApplyHeadAdjustment(dogSkel, basePose, -10f);
        var clampedPos = Pose.ApplyHeadAdjustment(dogSkel, basePose, 2f);
        Assert.Equal(clampedPos[31].LocalRotation, extremePos[31].LocalRotation);
        Assert.True(float.IsFinite(extremeNeg[31].LocalRotation.W));

        var badCtx = new TreeContext(world, bella, new StackFrame(0, [], []));
        var ex = Assert.Throws<SimulationException>(() => LookTowardsPrimitive.Execute(badCtx, [99, 0]));
        Assert.Equal(SimulationError.BadParameterInLookTowardsPrimitive, ex.Error);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F07")]
    public void T2_F07_04_MissingOptionalAccessoryMesh_SkipsGracefullyAndRendersBody()
    {
        var gameDir = E2EHarness.RequireGameDir();
        Assert.True(E2EHarness.HasHouses(gameDir, 5));

        var boot = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var bella = Assert.IsType<XPerson>(boot.World.ObjectById(18));
        var geometry = AvatarSceneGeometry.For(gameDir, boot.Registry);
        var camera = new LotCamera(0, 64, 64);

        bella.DressAccessory("nonexistent-acc-e2e.apr");
        try
        {
            var parts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
            AssertFiniteParts(parts);
        }
        finally
        {
            bella.UndressAccessory("nonexistent-acc-e2e.apr");
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F07")]
    public void T2_F07_05_NonAlignedLargeFrameDelta_DisablesSubTickInterpolation()
    {
        var gameDir = E2EHarness.RequireGameDir();

        var (adultSkel, cookAnim, curves) = LoadRealSkeletonAndAnim(gameDir, null, "adult", "a2o-stove-cook");
        var frameCount = Math.Max(1, cookAnim.PlaybackFrameCount);
        var durationMs = frameCount * (1000f / 30f);
        var clip = new AvatarAnimationClip(cookAnim.Name, frameCount, [], DurationMilliseconds: durationMs);

        var playback = new AvatarAnimationPlayback(clip, backwards: false, loop: false, startedAtMils: 0);
        // Jump >= 2 frame intervals (> 66.6ms)
        playback.Tick(deliverAnimationEvents: false, animatorMils: 100);
        Assert.False(playback.InterpolatePose);

        var snapped = Pose.Sample(adultSkel, cookAnim, curves, playback.Frame, interpolateFrame: playback.InterpolatePose);
        var integerSample = Pose.Sample(adultSkel, cookAnim, curves, Math.Floor(playback.Frame), interpolateFrame: false);
        for (int i = 0; i < snapped.Count; i++)
        {
            Assert.Equal(integerSample[i].WorldTranslation, snapped[i].WorldTranslation);
        }
    }
}
