using System.Numerics;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public class House05ChairSittingTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void House05_BellaCanRouteAroundPorchFenceAndSitInTableTuckedPatioChair224()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var lotPath = Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var bootstrap = WorldBootstrap.Load(gameDir, lotPath);
        var world = bootstrap.World;
        world.AutonomyForced = false;
        world.AutonomyHost = null;

        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var chair224 = Assert.IsAssignableFrom<XObject>(world.ObjectById(224));
        var target = InteractionMenus.Target(world, chair224);
        var sit = InteractionMenus.Read(world, bella, target)
            .FirstOrDefault(c => string.Equals(c.ActionText, "Sit", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sit);
        Assert.True(world.EnqueueInteractionAction(bella, sit!));

        var controls = new SimulationClockControl(1, 1, 1);
        var seated = false;
        for (var tick = 0; tick < 450; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            if (bella[PersonData.Sitting] != 0 && bella[ObjectData.ParentId] == chair224.Id)
            {
                seated = true;
                break;
            }
        }

        Assert.True(seated, $"Bella failed to sit in chair 224 (pos={bella.Position}, routeResult={bella[PersonData.RouteResult]})");
        Assert.Equal(0, bella[PersonData.RouteResult]);
    }

    [Fact]
    public void PieHeadPreview_UsesUnclipped128x128CanvasFixedSkullPivotAndZeroRoll()
    {
        Assert.Equal(128, SimPortrait.PieHeadWidth);
        Assert.Equal(128, SimPortrait.PieHeadHeight);

        var lotOffset = new Vector3(45f, 0.55f, 35f);
        var positions = new[]
        {
            lotOffset + new Vector3(-0.08f, -0.10f,  0.08f),
            lotOffset + new Vector3( 0.08f, -0.10f,  0.08f),
            lotOffset + new Vector3( 0.00f,  0.10f,  0.08f),
            lotOffset + new Vector3(-0.08f, -0.10f, -0.08f),
            lotOffset + new Vector3( 0.08f, -0.10f, -0.08f),
            lotOffset + new Vector3( 0.00f,  0.10f, -0.08f),
            lotOffset + new Vector3(-0.10f, -0.14f, -0.13f),
            lotOffset + new Vector3( 0.10f, -0.14f, -0.13f),
            lotOffset + new Vector3( 0.00f,  0.11f, -0.13f),
        };
        var normals = Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray();
        var uvs = Enumerable.Repeat(new Vector2(0.5f, 0.5f), positions.Length).ToArray();
        var onHead = new[] { true, true, true, true, true, true, false, false, false };
        var faces = new[] { (0, 1, 2), (3, 4, 5), (6, 7, 8) };
        var texture = BitmapCodec.Write(2, 2,
        [
            200, 120, 100, 255,  200, 120, 100, 255,
            200, 120, 100, 255,  200, 120, 100, 255,
        ]);
        var mesh = new AvatarSceneGeometry.PosedHeadMesh(positions, normals, uvs, onHead, faces, texture);

        const int size = SimPortrait.PieHeadWidth;
        var turned = SimPortrait.PieHeadPreview([mesh], -45f, 30f);
        Assert.Equal(size * size * 4, turned.Length);

        for (var i = 0; i < size; i++)
        {
            Assert.Equal(0, turned[(0 * size + i) * 4 + 3]);
            Assert.Equal(0, turned[((size - 1) * size + i) * 4 + 3]);
            Assert.Equal(0, turned[(i * size + 0) * 4 + 3]);
            Assert.Equal(0, turned[(i * size + (size - 1)) * 4 + 3]);
        }

        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var bella = Assert.IsType<XPerson>(bootstrap.World.ObjectById(18));
        var bellaHeadMeshes = AvatarSceneGeometry.For(gameDir, bootstrap.Registry)
            .BuildHeadMeshes(bella, 1, 0, (_, _) => 0f, new LotCamera(0, 64, 64));
        Assert.NotEmpty(bellaHeadMeshes);
        Assert.NotNull(bellaHeadMeshes[0].HeadPivot);
        Assert.True(bellaHeadMeshes[0].HeadHeight > 0f);

        foreach (var (yaw, pitch) in new[] { (0f, 8f), (-45f, 35f), (45f, -30f) })
        {
            var bellaTurned = SimPortrait.PieHeadPreview(bellaHeadMeshes, yaw, pitch);
            for (var i = 0; i < size; i++)
            {
                Assert.Equal(0, bellaTurned[(0 * size + i) * 4 + 3]);
                Assert.Equal(0, bellaTurned[((size - 1) * size + i) * 4 + 3]);
                Assert.Equal(0, bellaTurned[(i * size + 0) * 4 + 3]);
                Assert.Equal(0, bellaTurned[(i * size + (size - 1)) * 4 + 3]);
            }
        }

        // Accessory meshes bound to a head suit (such as glasses via SPECS) must not have their authored
        // textures overwritten by head texture overrides.
        var cassandra = Assert.IsType<XPerson>(bootstrap.World.ObjectById(16));
        var cassandraHeadMeshes = AvatarSceneGeometry.For(gameDir, bootstrap.Registry)
            .BuildHeadMeshes(cassandra, 1, 0, (_, _) => 0f, new LotCamera(0, 64, 64));
        if (cassandraHeadMeshes.Count >= 2)
        {
            var distinctTextures = cassandraHeadMeshes.Select(m => Convert.ToBase64String(m.Texture)).Distinct().Count();
            Assert.True(distinctTextures >= 2, "Companion head accessory mesh (glasses) must keep its own authored texture instead of inheriting HEAD-HEAD skin texture.");
        }
    }

    [Theory]
    [InlineData(21)]
    [InlineData(16)]
    public void House05_SimsCanTakeBathViaTubSecondarySubPart113(short simId)
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
            return;

        var bootstrap = WorldBootstrap.Load(gameDir, Path.Combine(gameDir, "UserData", "Houses", "House05.iff"));
        var world = bootstrap.World;
        world.AutonomyForced = false;
        world.AutonomyHost = null;

        var sim = Assert.IsType<XPerson>(world.ObjectById(simId));
        sim[Motive.Hygiene] = -50f;
        var tubSubPart113 = Assert.IsAssignableFrom<XObject>(world.ObjectById(113));
        var target = InteractionMenus.Target(world, tubSubPart113);
        var bath = InteractionMenus.Read(world, sim, target)
            .FirstOrDefault(c => string.Equals(c.ActionText, "Take a Bath", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bath);
        Assert.True(world.EnqueueInteractionAction(sim, bath!));

        var controls = new SimulationClockControl(1, 1, 1);
        var startedBath = false;
        var censoredDuringBath = false;
        var playedAnims = new List<string>();
        for (var tick = 0; tick < 1200; tick++)
        {
            world.AdvanceSimulationTick(controls, 0, world.Clock.Ticks);
            var anim = sim.AnimationPlayback?.Clip.Name;
            if (!string.IsNullOrEmpty(anim) && !playedAnims.Contains(anim))
                playedAnims.Add(anim);
            if (anim is not null && anim.Contains("tub", StringComparison.OrdinalIgnoreCase))
                startedBath = true;
            if (startedBath && sim[PersonData.CensorshipFlags] != 0)
            {
                censoredDuringBath = true;
                break;
            }
        }

        Assert.True(startedBath, $"Sim {simId} failed to start bath via TubM 113 (pos={sim.Position}, routeResult={sim[PersonData.RouteResult]}, anims=[{string.Join(", ", playedAnims)}])");
        Assert.True(censoredDuringBath, $"Sim {simId} started bath via TubM 113 without setting CensorshipFlags (anims=[{string.Join(", ", playedAnims)}])");

        var geometry = AvatarSceneGeometry.For(gameDir, bootstrap.Registry);
        geometry.Build(sim, 1, 0, (_, _) => 0f, new LotCamera(0, 64, 64));
        var censor = geometry.Censor(sim);
        Assert.NotNull(censor);
        Assert.True(censor!.Width > 8f && censor.Height > 8f,
            $"Expected non-empty <skeleton>-needs censor bounds for bathing Sim {simId}, got {censor.Width}x{censor.Height}");
    }
}
