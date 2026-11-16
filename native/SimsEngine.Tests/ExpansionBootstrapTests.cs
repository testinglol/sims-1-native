using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public class ExpansionBootstrapTests
{
    private static (SimulationWorld World, string UserData) CreateTestEnvironment()
    {
        var world = new SimulationWorld();
        world.ObjectFactory = guid =>
        {
            var obj = new XObject((short)(world.WalkOrder.Count + 1), PrimitiveTestWorld.Definition());
            obj.TypeRecord = new TypeRecord(guid);
            return obj;
        };
        var tempUserData = Path.Combine(Path.GetTempPath(), $"sims-userdata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempUserData);
        var zoningBytes = ChunkBuilder.File(
            ("STR#", 1, new ChunkBuilder()
                .S16(-3).S16(2)
                .U8(1).CString("1, residential").CString("")
                .U8(1).CString("25, community").CString("")
                .ToArray())
        );
        File.WriteAllBytes(Path.Combine(tempUserData, "LotZoning.iff"), zoningBytes);
        return (world, tempUserData);
    }

    private static Family CreateTestFamily()
    {
        var (file, chunk) = new ChunkBuilder().U8(0).U8(0).U8(0).U8(0).S32(7).Tag("IMAF")
            .S32(1).S32(1).S32(20000).S32(0).S32(0).U32(1).U32(0)
            .InFile("FAMI", 1);
        return Family.Read(file, chunk);
    }

    [Fact]
    public void EnsureSpellboundController_CreatesControllerOnResidentialLot()
    {
        var (world, userData) = CreateTestEnvironment();
        try
        {
            var activeFamily = CreateTestFamily();

            var controller = ExpansionBootstrap.EnsureSpellboundController(world, userData, house: 1, activeFamily);
            Assert.NotNull(controller);
            Assert.Equal(ExpansionBootstrap.SpellboundControllerGuid, controller.TypeRecord?.Key);
            Assert.Contains(controller, world.WalkOrder);

            var duplicate = ExpansionBootstrap.EnsureSpellboundController(world, userData, house: 1, activeFamily);
            Assert.Null(duplicate);
            Assert.Single(world.WalkOrder, o => o.TypeRecord?.Key == ExpansionBootstrap.SpellboundControllerGuid);

            var (worldWithKit, userData2) = CreateTestEnvironment();
            var kit = worldWithKit.Add(new XObject(10, PrimitiveTestWorld.Definition()));
            kit.TypeRecord = new TypeRecord(ExpansionBootstrap.MagicStarterKitGuid);
            var controllerWithKit = ExpansionBootstrap.EnsureSpellboundController(worldWithKit, userData2, house: 1, activeFamily);
            Assert.Null(controllerWithKit);

            var (commWorld, userData3) = CreateTestEnvironment();
            var commController = ExpansionBootstrap.EnsureSpellboundController(commWorld, userData3, house: 25, activeFamily);
            Assert.Null(commController);

            var (emptyWorld, userData4) = CreateTestEnvironment();
            var emptyController = ExpansionBootstrap.EnsureSpellboundController(emptyWorld, userData4, house: 1, activeFamily: null);
            Assert.Null(emptyController);
        }
        finally
        {
            try { Directory.Delete(userData, true); } catch { }
        }
    }

    [Fact]
    public void EnsurePhonePlugins_DiscoversAllFiveExpansionPlugins()
    {
        var (world, userData) = CreateTestEnvironment();
        try
        {
            ExpansionBootstrap.EnsurePhonePlugins(world);

            Assert.Equal(5, ExpansionBootstrap.ExpansionPhonePluginGuids.Length);
            foreach (var guid in ExpansionBootstrap.ExpansionPhonePluginGuids)
            {
                var plugin = world.WalkOrder.FirstOrDefault(o => o.TypeRecord?.Key == guid);
                Assert.NotNull(plugin);
                // Expansion phone plugins must use category 1 so that phone lookup scripts can find them
                // via object search mode 6.
                Assert.Equal(1, plugin[ObjectData.Category]);
            }

            ExpansionBootstrap.EnsurePhonePlugins(world);
            Assert.Equal(5, world.WalkOrder.Count);
        }
        finally
        {
            try { Directory.Delete(userData, true); } catch { }
        }
    }

    [Fact]
    public void EnsurePhonePlugins_StressTest_TenCalls_MaintainsIdempotencyAndCategoryOne()
    {
        var (world, userData) = CreateTestEnvironment();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                ExpansionBootstrap.EnsurePhonePlugins(world);

                Assert.Equal(5, world.WalkOrder.Count);

                foreach (var guid in ExpansionBootstrap.ExpansionPhonePluginGuids)
                {
                    var matching = world.WalkOrder.Where(o => o.TypeRecord?.Key == guid).ToList();
                    Assert.Single(matching);
                    Assert.Equal(1, matching[0][ObjectData.Category]);
                }
            }
        }
        finally
        {
            try { Directory.Delete(userData, true); } catch { }
        }
    }

    [Fact]
    public void EnsureSpellboundController_StressTest_CommunityLotAndStarterKit_NeverSpawns()
    {
        var (world, userData) = CreateTestEnvironment();
        try
        {
            var activeFamily = CreateTestFamily();

            for (int i = 0; i < 5; i++)
            {
                var controller = ExpansionBootstrap.EnsureSpellboundController(world, userData, house: 25, activeFamily);
                Assert.Null(controller);
                Assert.DoesNotContain(world.WalkOrder, o => o.TypeRecord?.Key == ExpansionBootstrap.SpellboundControllerGuid);
            }

            var (worldWithKit, userData2) = CreateTestEnvironment();
            try
            {
                var kit = worldWithKit.Add(new XObject(50, PrimitiveTestWorld.Definition()));
                kit.TypeRecord = new TypeRecord(ExpansionBootstrap.MagicStarterKitGuid);

                for (int i = 0; i < 5; i++)
                {
                    var controller = ExpansionBootstrap.EnsureSpellboundController(worldWithKit, userData2, house: 1, activeFamily);
                    Assert.Null(controller);
                    Assert.DoesNotContain(worldWithKit.WalkOrder, o => o.TypeRecord?.Key == ExpansionBootstrap.SpellboundControllerGuid);
                }
            }
            finally
            {
                try { Directory.Delete(userData2, true); } catch { }
            }

            foreach (var invalidHouse in new[] { 0, -1, -50 })
            {
                var controller = ExpansionBootstrap.EnsureSpellboundController(world, userData, invalidHouse, activeFamily);
                Assert.Null(controller);
            }
        }
        finally
        {
            try { Directory.Delete(userData, true); } catch { }
        }
    }
}
