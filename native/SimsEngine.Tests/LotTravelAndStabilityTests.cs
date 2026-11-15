using System.Text;
using System.Text.Json;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class LotTravelAndStabilityTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    [Fact]
    public void RunGenericSimsCall11_SetsPendingLotSwitch_OnlyForTravelModes()
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        me.TypeRecord = new TypeRecord(1234);
        me[PersonData.NeighborId] = 2;
        me[PersonData.FamilyNumber] = 7;
        me.SetObjectTableEntry(4, me);
        context.World.PersonRecordKeys.Add(1234);
        context.World.PersonRecordNeighborIds.Add(2);
        context.World.Clock.Restore(9, 15, 0);

        // temporary3 == 2 only persists without switching lots.
        context.World.RunGenericSimsCall11Native(me, 21, 1, 0, 2);
        Assert.Null(context.World.PendingLotSwitch);

        // temporary3 == 0 requests solo travel using scoped/self person target.
        var scope = context.World.InteractionExecutorScope(me);
        scope.AddOrIncrement(1, 0, 1234);
        context.World.RunGenericSimsCall11Native(me, 21, 1, 0, 0);
        Assert.Equal(new SimulationWorld.LotSwitchRequest(21, 1234, 7), context.World.PendingLotSwitch);

        // temporary3 == 1 requests whole-family travel (PersonGuid == 0).
        context.World.PendingLotSwitch = null;
        context.World.RunGenericSimsCall11Native(me, -1, 0, 30, 1);
        Assert.Equal(new SimulationWorld.LotSwitchRequest(-1, 0, 7), context.World.PendingLotSwitch);
    }

    [Fact]
    public void SetLanguageStringEntry_AndUpdateCtssStrings_PreserveForeignLanguageEntries()
    {
        var builder = new ChunkBuilder().S16(-3).S16(4)
            .U8(1).CString("Old Name").CString("")
            .U8(1).CString("Old Bio").CString("")
            .U8(2).CString("UK Name").CString("")
            .U8(2).CString("UK Bio").CString("");
        var (file, chunk) = builder.InFile("STR#");
        var table = StringTable.Read(file, chunk);

        var updatedBytes = SimulationServer.UpdateCtssStrings(table, "New Name", "New Bio");
        var (updatedFile, updatedChunk) = new ChunkBuilder().Raw(updatedBytes).InFile("STR#");
        var reparsed = StringTable.Read(updatedFile, updatedChunk);

        var english = reparsed.ForLanguage(1);
        var ukEnglish = reparsed.ForLanguage(2);
        Assert.Equal("New Name", english[0]);
        Assert.Equal("New Bio", english[1]);
        Assert.Equal("UK Name", ukEnglish[0]);
        Assert.Equal("UK Bio", ukEnglish[1]);

        var entries = reparsed.Entries.ToList();
        SimulationServer.SetLanguageStringEntry(entries, 14, "female");
        Assert.Equal("female", entries.Where(e => e.Language == 1).ElementAt(13).Value);
        Assert.Equal("UK Name", entries.Where(e => e.Language == 2).ElementAt(0).Value);
    }

    [Fact]
    public void CyclicSlotContainment_PreventsInvalidDataExceptionAndRejectsCycle()
    {
        var world = new SimulationWorld();
        var parent = world.Add(new XObject(1, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(168, 168, 1) });
        var child = world.Add(new XObject(2, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(-16, -16, 1) });

        // Force a degenerate mutual-slot cycle that could arise in corrupted/live saves.
        parent.Slots.AddRange([0, child.ClockManagerIndex]);
        child.Slots.AddRange([0, parent.ClockManagerIndex]);
        parent[ObjectData.ContainerId] = child.ClockManagerIndex;
        parent[ObjectData.ParentId] = child.ClockManagerIndex;
        child[ObjectData.ContainerId] = parent.ClockManagerIndex;
        child[ObjectData.ParentId] = parent.ClockManagerIndex;

        var tileObjects = world.ObjectsAtTile(parent.Position.X >> 4, parent.Position.Y >> 4, parent.Position.Height).ToArray();
        Assert.Contains(parent, tileObjects);
        Assert.Contains(child, tileObjects);
        Assert.False(world.TryPlaceInFirstRealSlot(parent, child));
        Assert.False(world.TryPlaceInFirstRealSlot(child, parent));
    }

    [Fact]
    public void NativeSimulationServer_LotTravel_SwitchesToDowntownAndReturnsHome()
    {
        var gameDir = LocateGameDirectory();
        var homePath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var downtownPath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House21.iff");
        var nghPath = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Neighborhood.iff");
        if (gameDir is null || !File.Exists(homePath) || !File.Exists(downtownPath) || !File.Exists(nghPath))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-travel-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(tempRoot, "profile");
            var userData = Path.Combine(profile, "UserData");
            Directory.CreateDirectory(Path.Combine(userData, "Houses"));
            var charsDir = Path.Combine(userData, "Characters");
            Directory.CreateDirectory(charsDir);
            foreach (var charFile in Directory.EnumerateFiles(Path.Combine(gameDir, "UserData", "Characters")))
                File.Copy(charFile, Path.Combine(charsDir, Path.GetFileName(charFile)));
            File.Copy(nghPath!, Path.Combine(userData, "Neighborhood.iff"));
            var zoningPath = Path.Combine(gameDir!, "UserData", "LotZoning.iff");
            if (File.Exists(zoningPath))
                File.Copy(zoningPath, Path.Combine(userData, "LotZoning.iff"));
            File.Copy(homePath!, Path.Combine(userData, "Houses", "House05.iff"));
            File.Copy(downtownPath!, Path.Combine(userData, "Houses", "House21.iff"));

            var session = Path.Combine(tempRoot, "session");
            Directory.CreateDirectory(session);
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);

            JsonElement[] DrainEvents()
            {
                writer.Flush();
                var text = Encoding.UTF8.GetString(outputStream.GetBuffer(), 0, (int)outputStream.Length);
                outputStream.SetLength(0);
                outputStream.Position = 0;
                return text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                    .ToArray();
            }

            DrainEvents();

            var homeWorld = server.World!;
            var traveller = homeWorld.WalkOrder.OfType<XPerson>()
                .First(person => person[PersonData.FamilyNumber] > 0 && (person.TypeRecord?.Key ?? 0) != 0);
            var travellerGuid = traveller.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];

            // Seed GenericSimsCall 11 arrival clock (14:25) and trigger solo lot switch to Downtown lot 21.
            var scope = homeWorld.InteractionExecutorScope(traveller);
            scope.AddOrIncrement(2, 7, 14);
            scope.AddOrIncrement(2, 8, 25);
            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, familyNumber);

            server.BatchRun(1);
            var awayEvents = DrainEvents();
            var awayLotSwitchingIndex = Array.FindIndex(awayEvents, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "lotSwitching");
            var awayLotChangedIndex = Array.FindLastIndex(awayEvents, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.True(awayLotSwitchingIndex >= 0 && awayLotSwitchingIndex < awayLotChangedIndex,
                "Expected lotSwitching event to be flushed before lotChanged.");
            var awayLotSwitching = awayEvents[awayLotSwitchingIndex];
            Assert.Equal(5, awayLotSwitching.GetProperty("fromHouse").GetInt32());
            Assert.Equal(21, awayLotSwitching.GetProperty("house").GetInt32());
            Assert.True(awayLotSwitching.GetProperty("away").GetBoolean());
            Assert.False(awayLotSwitching.GetProperty("returnHome").GetBoolean());
            if (File.Exists(zoningPath))
                Assert.Equal(1, awayLotSwitching.GetProperty("sourceStateClass").GetInt32());

            var awayLotChanged = awayEvents[awayLotChangedIndex];
            Assert.Equal(21, awayLotChanged.GetProperty("house").GetInt32());
            Assert.True(awayLotChanged.GetProperty("away").GetBoolean());

            var awayWorld = server.World!;
            Assert.NotSame(homeWorld, awayWorld);
            Assert.Equal(14, awayWorld.Clock.Hour);
            Assert.Equal(25, awayWorld.Clock.Minute);

            // Arrival on Downtown lot 21 immediately instantiates the traveller (FUN_004e9855), selects them,
            // and runs 30 ticks without simulation errors.
            var arrivedTraveller = awayWorld.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p.TypeRecord?.Key == travellerGuid);
            Assert.NotNull(arrivedTraveller);

            var arrivedState = awayEvents.Last(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "state").GetProperty("state");
            Assert.Equal(arrivedTraveller!.Id, arrivedState.GetProperty("selectedSim").GetInt32());

            var ticksDone = server.BatchRun(300);
            Assert.Equal(300, ticksDone);
            Assert.DoesNotContain(awayEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "error");
            Assert.True(SimulationWorld.IsInsideLotEdge(arrivedTraveller),
                $"Expected arrived traveller on House 21 to be placed onto lot, got ({arrivedTraveller.Position.X},{arrivedTraveller.Position.Y},{arrivedTraveller.Position.Height})");

            // Home family house assignment in Neighborhood.iff must remain untouched (House 5) while away.
            var midNgh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
            var midFamily = midNgh.OfType(ResourceType.Parse("FAMI")).Select(c => Family.Read(midNgh, c))
                .First(f => f.Members.Contains(unchecked((uint)travellerGuid)));
            Assert.Equal(5, midFamily.HouseNumber);

            // Trigger return home (-1) and verify restoration of House 5.
            awayWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, 0, familyNumber);
            server.BatchRun(1);
            var homeEvents = DrainEvents();
            var returnLotSwitchingIndex = Array.FindIndex(homeEvents, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "lotSwitching");
            var returnLotChangedIndex = Array.FindLastIndex(homeEvents, e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.True(returnLotSwitchingIndex >= 0 && returnLotSwitchingIndex < returnLotChangedIndex,
                "Expected return-home lotSwitching event to be flushed before lotChanged.");
            var returnLotSwitching = homeEvents[returnLotSwitchingIndex];
            Assert.Equal(21, returnLotSwitching.GetProperty("fromHouse").GetInt32());
            Assert.Equal(5, returnLotSwitching.GetProperty("house").GetInt32());
            Assert.False(returnLotSwitching.GetProperty("away").GetBoolean());
            Assert.True(returnLotSwitching.GetProperty("returnHome").GetBoolean());

            var returnLotChanged = homeEvents[returnLotChangedIndex];
            Assert.Equal(5, returnLotChanged.GetProperty("house").GetInt32());
            Assert.False(returnLotChanged.GetProperty("away").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
