using SimsEngine.Character;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class StairAndRouteAnimationParityTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static bool EnqueueGoHere(SimulationWorld world, XPerson person, int tileX, int tileY, int level)
    {
        var target = world.ObjectFactory?.Invoke(0x7c4)
            ?? throw new InvalidOperationException("Destination.iff (0x7c4) Go Here object is unavailable.");
        world.Add(target);
        world.InitializeCreatedObject(target);
        world.SetObjectPosition(target, new ObjectPosition(tileX * 16 + 8, tileY * 16 + 8, level));
        var choice = InteractionMenus.Read(world, person, target)
            .FirstOrDefault(c => c.ActionText is not null);
        return choice is not null && world.EnqueueInteractionAction(person, choice);
    }

    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(0.49f, 0)]
    [InlineData(0.50f, 1)]
    [InlineData(-0.49f, 0)]
    [InlineData(-0.50f, 0)]
    [InlineData(-0.51f, 7)]
    [InlineData(-0.60f, 7)]
    [InlineData(-1.49f, 7)]
    [InlineData(-1.51f, 6)]
    [InlineData(-4.0f, 4)]
    [InlineData(7.60f, 0)]
    [InlineData(float.NaN, 0)]
    public void ToObjectDirection_RoundsNegativeAndBoundaryDirectionsCleanlyIntoOctants(float direction, short expected)
    {
        Assert.Equal(expected, AvatarDirection.ToObjectDirection(direction));
    }

    [Fact]
    public void ResetPerson_StopsActiveOwnerSounds()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = bootstrap.World;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));

        var stoppedIds = new List<short>();
        world.StopOwnerSounds = id => stoppedIds.Add(id);

        world.ResetObjectForChangedCode(bella);
        Assert.Contains(bella.Id, stoppedIds);
    }

    [Fact]
    public void House05_Walking180TurnAndArrival_NeverSpins360OrFlipsHeadingOnOvershoot()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = bootstrap.World;
        world.AutonomyForced = false;
        world.AutonomyHost = null;

        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var startTileX = bella.Position.X >> 4;
        var startTileY = bella.Position.Y >> 4;
        var startRoom = world.LotRoomMaps[1].RoomAt(startTileX, startTileY);

        (int X, int Y)? chosen = null;
        for (var radius = 5; radius >= 3 && chosen is null; radius--)
        {
            foreach (var (stepX, stepY) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (-1, -1) })
            {
                var clear = true;
                for (var step = 1; step <= radius; step++)
                {
                    var cx = startTileX + stepX * step;
                    var cy = startTileY + stepY * step;
                    if (cx <= 1 || cy <= 1 || cx >= 62 || cy >= 62 ||
                        world.LotRoomMaps[1].RoomAt(cx, cy) != startRoom ||
                        !world.CanPlaceObject(bella, new ObjectPosition(cx * 16 + 8, cy * 16 + 8, 1), null))
                    {
                        clear = false;
                        break;
                    }
                }
                if (clear)
                {
                    chosen = (startTileX + stepX * radius, startTileY + stepY * radius);
                    break;
                }
            }
        }
        Assert.NotNull(chosen);
        var (goalTileX, goalTileY) = chosen!.Value;

        var toGoalDir = AvatarDirection.Toward((goalTileX - startTileX) * 16f, (goalTileY - startTileY) * 16f);
        var oppositeOctant = AvatarDirection.ToObjectDirection(toGoalDir + 4f);
        bella[ObjectData.Direction] = oppositeOctant;
        bella.AnimatorRootDirection = oppositeOctant;

        Assert.True(EnqueueGoHere(world, bella, goalTileX, goalTileY, 1));

        var controls = new SimulationClockControl(1, 1, 1);
        var turn180Starts = 0;
        string? prevClip = null;
        var prevRootDir = (float)oppositeOctant;
        var cumulativeRootTurn = 0f;
        var arrived = false;

        var turnLog = new List<string>();
        for (var tick = 0; tick < 350; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            var clip = bella.AnimationPlayback?.Clip.Name;
            if (clip != prevClip)
            {
                if (clip is not null && clip.Contains("turn-180", StringComparison.OrdinalIgnoreCase))
                    turn180Starts++;
                prevClip = clip;
            }

            var rootDir = bella.AnimatorRootDirection ?? bella[ObjectData.Direction];
            var delta = rootDir - prevRootDir;
            while (delta >= 4f) delta -= 8f;
            while (delta < -4f) delta += 8f;
            if (MathF.Abs(delta) > 0.01f)
                turnLog.Add($"t={tick}:st={bella.AnimatorControlState}/fl={bella.NativeRoute?.AnimatorFollowState}/sk={bella.NativeRoute?.MotionAnimationIndex}/dir={rootDir:F2}(d={delta:F2})/pos=({bella.AnimatorRootX:F1},{bella.AnimatorRootY:F1})->({bella.NativeRoute?.MotionTargetX},{bella.NativeRoute?.MotionTargetY})");
            cumulativeRootTurn += MathF.Abs(delta);
            prevRootDir = rootDir;

            if ((bella.Position.X >> 4) == goalTileX && (bella.Position.Y >> 4) == goalTileY &&
                bella.NativeRoute is null && tick > 5)
            {
                arrived = true;
                break;
            }
        }

        Assert.True(arrived, $"Bella did not finish walking to ({goalTileX},{goalTileY}) (pos={bella.Position}, routeResult={bella[PersonData.RouteResult]})");
        Assert.Equal(0, bella[PersonData.RouteResult]);
        Assert.True(turn180Starts <= 1, $"Expected standing-turn-180 to play at most once, but started {turn180Starts} times (360 spin regression).");
        // Rotation is measured in octant units where 8.0 units represent a full 360-degree turn.
        Assert.True(cumulativeRootTurn < 6.5f, $"AnimatorRootDirection accumulated {cumulativeRootTurn:F2} octants (>= 292 deg): {string.Join(" | ", turnLog)}");
    }

    [Fact]
    public void House05_LiveSimRoutesUpstairsAndDownstairsWithStairClimbAndDescendAnimations()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = bootstrap.World;
        world.AutonomyForced = false;
        world.AutonomyHost = null;

        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        Assert.Equal(1, bella.Position.Height);

        var lowPad = world.Objects.Values.First(o =>
            o.Position.Height == 1 &&
            o.TypeRecord?.Name is { } name &&
            name.Contains("Stair", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Low", StringComparison.OrdinalIgnoreCase));
        var hiPad = world.Objects.Values.First(o =>
            o.Position.Height == 2 &&
            o.TypeRecord?.Name is { } name &&
            name.Contains("Stair", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Hi", StringComparison.OrdinalIgnoreCase));

        (int X, int Y)? upstairsTile = null;
        var upstairsRoom = world.LotRoomMaps[2].RoomAt(hiPad.Position.X >> 4, hiPad.Position.Y >> 4);
        for (var tx = 10; tx < 54 && upstairsTile is null; tx++)
        {
            for (var ty = 10; ty < 54 && upstairsTile is null; ty++)
            {
                if (world.LotRoomMaps[2].RoomAt(tx, ty) != upstairsRoom) continue;
                if (Math.Abs(tx - (hiPad.Position.X >> 4)) + Math.Abs(ty - (hiPad.Position.Y >> 4)) < 2) continue;
                if (!world.CanPlaceObject(bella, new ObjectPosition(tx * 16 + 8, ty * 16 + 8, 2), null)) continue;
                upstairsTile = (tx, ty);
            }
        }
        Assert.NotNull(upstairsTile);

        var floor1StartTile = (X: bella.Position.X >> 4, Y: bella.Position.Y >> 4);
        Assert.True(EnqueueGoHere(world, bella, upstairsTile!.Value.X, upstairsTile.Value.Y, 2));

        var controls = new SimulationClockControl(1, 1, 1);
        var sawStairClimb = false;
        var sawWalkingUpStairsFlag = false;
        short? climbDirection = null;
        var reachedFloor2 = false;
        var climbClips = new List<string>();

        for (var tick = 0; tick < 900; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            var clip = bella.AnimationPlayback?.Clip.Name;
            if (!string.IsNullOrEmpty(clip) && !climbClips.Contains(clip))
                climbClips.Add(clip);

            if (clip is not null && clip.Contains("stair-climb", StringComparison.OrdinalIgnoreCase))
            {
                sawStairClimb = true;
                climbDirection ??= bella[ObjectData.Direction];
                if (bella[PersonData.WalkingUpStairs] != 0)
                    sawWalkingUpStairsFlag = true;
            }

            if (bella.Position.Height == 2 &&
                (bella.Position.X >> 4) == upstairsTile.Value.X &&
                (bella.Position.Y >> 4) == upstairsTile.Value.Y &&
                bella.NativeRoute is null)
            {
                reachedFloor2 = true;
                break;
            }
        }

        Assert.True(sawStairClimb, $"Expected a2o-stair-climb when routing floor 1 -> 2, saw [{string.Join(", ", climbClips)}]");
        Assert.True(sawWalkingUpStairsFlag, "Expected PersonData.WalkingUpStairs == 1 during a2o-stair-climb.");
        Assert.Equal(lowPad[ObjectData.Direction], climbDirection);
        Assert.True(reachedFloor2, $"Bella failed to arrive at floor 2 tile {upstairsTile} (pos={bella.Position}, routeResult={bella[PersonData.RouteResult]})");
        Assert.Equal(0, bella[PersonData.RouteResult]);
        Assert.Equal(0, bella[PersonData.WalkingUpStairs]);

        Assert.True(EnqueueGoHere(world, bella, floor1StartTile.X, floor1StartTile.Y, 1));

        var sawStairDescend = false;
        short? descendDirection = null;
        var reachedFloor1 = false;
        var descendClips = new List<string>();

        for (var tick = 0; tick < 900; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            var clip = bella.AnimationPlayback?.Clip.Name;
            if (!string.IsNullOrEmpty(clip) && !descendClips.Contains(clip))
                descendClips.Add(clip);

            if (clip is not null && clip.Contains("stair-descend", StringComparison.OrdinalIgnoreCase))
            {
                sawStairDescend = true;
                descendDirection ??= bella[ObjectData.Direction];
            }

            if (bella.Position.Height == 1 &&
                (bella.Position.X >> 4) == floor1StartTile.X &&
                (bella.Position.Y >> 4) == floor1StartTile.Y &&
                bella.NativeRoute is null)
            {
                reachedFloor1 = true;
                break;
            }
        }

        var expectedDescendDirection = (short)((hiPad[ObjectData.Direction] + 4) & 7);
        Assert.True(sawStairDescend, $"Expected a2o-stair-descend when routing floor 2 -> 1, saw [{string.Join(", ", descendClips)}]");
        Assert.Equal(expectedDescendDirection, descendDirection);
        Assert.True(reachedFloor1, $"Bella failed to return to floor 1 tile {floor1StartTile} (pos={bella.Position}, routeResult={bella[PersonData.RouteResult]})");
        Assert.Equal(0, bella[PersonData.RouteResult]);
    }
}
