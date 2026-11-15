using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Wave13ReleaseAndBugfixTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static (string TempRoot, string Profile, string UserData, string Session) CreateIsolatedProfile(
        string gameDir, params short[] houses)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave13-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
        var userData = Path.Combine(profile, "UserData");
        Directory.CreateDirectory(Path.Combine(userData, "Houses"));
        var charsDir = Path.Combine(userData, "Characters");
        Directory.CreateDirectory(charsDir);
        foreach (var charFile in Directory.EnumerateFiles(Path.Combine(gameDir, "UserData", "Characters")))
            File.Copy(charFile, Path.Combine(charsDir, Path.GetFileName(charFile)));

        File.Copy(Path.Combine(gameDir, "UserData", "Neighborhood.iff"), Path.Combine(userData, "Neighborhood.iff"));
        foreach (var extra in new[] { "LotZoning.iff", "LotLocations.iff", "StreetNames.iff" })
        {
            var extraPath = Path.Combine(gameDir, "UserData", extra);
            if (File.Exists(extraPath))
                File.Copy(extraPath, Path.Combine(userData, extra));
        }

        foreach (var house in houses)
        {
            var src = Path.Combine(gameDir, "UserData", "Houses", $"House{house:D2}.iff");
            if (File.Exists(src))
                File.Copy(src, Path.Combine(userData, "Houses", $"House{house:D2}.iff"));
        }

        var session = Path.Combine(tempRoot, "session");
        Directory.CreateDirectory(session);
        return (tempRoot, profile, userData, session);
    }

    private static JsonElement[] DrainEvents(MemoryStream outputStream, StreamWriter writer)
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

    private static JsonElement ResponseFor(JsonElement[] events, string id) =>
        events.Single(e => e.TryGetProperty("id", out var idEl) && idEl.GetString() == id);

    [Fact]
    public void LookTowardsModes2And3_TurnInPlaceWithoutWalking_AndFaceOppositeDirections()
    {
        var (context, me, _) = PrimitiveTestWorld.Create(stackObject: 2);
        context.World.Host = null;
        me[ObjectData.AllowedHeightFlags] = 1;
        me[ObjectData.PlacementFlags] = 0x0800;
        me.TypeRecord = new TypeRecord(0x1234) { FootprintMask = 0 };
        var origin = new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 0);
        me.Position = origin;
        me[ObjectData.Direction] = 0;

        var other = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition(0)));
        context.World.SetObjectPosition(other, new ObjectPosition(12 * 16 + 8, 10 * 16 + 8, 0));

        // Mode 2: Turn Towards Stack Object (other at +X => East = direction 2)
        byte[] opTowards = [2, 0, 0xEE, 0xEE, 2, 0, 0xEE, 0xEE];
        Assert.Equal(PrimitiveResult.Stop, LookTowardsPrimitive.Execute(context, opTowards));

        Assert.NotNull(me.NativeRoute);
        Assert.True(me.NativeRoute!.LookTowardsFollow);
        Assert.NotNull(me.NativeRoute.LookTowardsHeading);
        Assert.Equal(-1, me.NativeRoute.AnimatorFollowState);
        Assert.Equal(origin, me.Position);

        var result = PrimitiveResult.Stop;
        for (var i = 0; i < 80 && result == PrimitiveResult.Stop; i++)
        {
            context.World.TickAvatarAnimations(me);
            result = LookTowardsPrimitive.Execute(context, opTowards);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(me.NativeRoute);
        Assert.Equal(origin, me.Position);
        Assert.Equal((short)2, me[ObjectData.Direction]);

        // Mode 3: Turn Away From Stack Object (East=2 -> away is West=6)
        context.Frame.Waiting = false;
        byte[] opAway = [3, 0, 0xEE, 0xEE, 2, 0, 0xEE, 0xEE];
        Assert.Equal(PrimitiveResult.Stop, LookTowardsPrimitive.Execute(context, opAway));
        result = PrimitiveResult.Stop;
        for (var i = 0; i < 80 && result == PrimitiveResult.Stop; i++)
        {
            context.World.TickAvatarAnimations(me);
            result = LookTowardsPrimitive.Execute(context, opAway);
        }

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Null(me.NativeRoute);
        Assert.Equal(origin, me.Position);
        Assert.Equal((short)6, me[ObjectData.Direction]);
    }

    [Fact]
    public void RelativeArrivalFacing_FacesAnchorOrigin_WhenLowFlagsAreZero()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 2);
        var person = (XPerson)context.Me;
        var anchor = context.World.Add(new XObject(2, PrimitiveTestWorld.Definition()));
        context.World.SetObjectPosition(anchor, new ObjectPosition(10 * 16 + 8, 10 * 16 + 8, 1));
        anchor[ObjectData.Direction] = 4; // South

        // Destination East (+1 tile) from anchor origin: facing toward anchor (-2) is West (6), away (-1) is East (2).
        var destEast = new ObjectPosition(11 * 16 + 8, 10 * 16 + 8, 1);
        var slotToward = new RoutingSlotRequest { Facing = -2, Flags = 0 };
        var slotAway = new RoutingSlotRequest { Facing = -1, Flags = 0 };

        Assert.Equal(6, RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slotToward, destEast, null));
        Assert.Equal(2, RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slotAway, destEast, null));
        // Coincident destination falls back to anchor's own direction (or opposite for -1).
        Assert.Equal(4, RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slotToward, anchor.Position, null));
        Assert.Equal(0, RouteExecutor.RelativeArrivalFacing(context.World, person, anchor, slotAway, anchor.Position, null));
    }

    [Fact]
    public void OldTownHouse70_GothTravel_Completes300TicksWithoutCrashOrError()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var house70 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House70.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(house70) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, _, session) = CreateIsolatedProfile(gameDir, 5, 70);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            Assert.NotNull(server.World);
            DrainEvents(outputStream, writer);

            var homeWorld = server.World!;
            var traveller = homeWorld.WalkOrder.OfType<XPerson>()
                .FirstOrDefault(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            Assert.NotNull(traveller);
            var travellerGuid = traveller!.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];

            homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(70, travellerGuid, familyNumber);
            Assert.Equal(1, server.BatchRun(1));

            var arrivalEvents = DrainEvents(outputStream, writer);
            var lotSwitching = arrivalEvents.Single(e => e.TryGetProperty("type", out var t) && t.GetString() == "lotSwitching");
            Assert.True(lotSwitching.GetProperty("away").GetBoolean());
            Assert.Equal(70, lotSwitching.GetProperty("house").GetInt32());

            var lotChanged = arrivalEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "lotChanged");
            Assert.Equal(70, lotChanged.GetProperty("house").GetInt32());
            Assert.True(lotChanged.GetProperty("away").GetBoolean());
            Assert.Contains(arrivalEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "environment");

            var stateAfterArrival = arrivalEvents.Last(e => e.TryGetProperty("type", out var t) && t.GetString() == "state")
                .GetProperty("state");
            Assert.Equal(70, stateAfterArrival.GetProperty("house").GetInt32());
            Assert.Equal(5, stateAfterArrival.GetProperty("homeHouse").GetInt32());
            Assert.True(stateAfterArrival.GetProperty("away").GetBoolean());
            Assert.Equal(JsonValueKind.Null, stateAfterArrival.GetProperty("simulationError").ValueKind);

            Assert.NotNull(server.World);
            Assert.NotSame(homeWorld, server.World);
            Assert.NotNull(server.World!.UiTextFile);

            // Simulate 300 ticks (~10s @ 30 Hz) on Old Town House70 and assert zero uncaught crashes or simulationError.
            var completed = server.BatchRun(300);
            var tickEvents = DrainEvents(outputStream, writer);
            Assert.Equal(300, completed);

            Assert.True(server.Handle("""{"id":"stateAfter300","op":"state"}"""));
            var finalEvents = DrainEvents(outputStream, writer);
            var finalState = ResponseFor(finalEvents, "stateAfter300").GetProperty("result");
            var simError = finalState.GetProperty("simulationError");
            Assert.True(simError.ValueKind == JsonValueKind.Null,
                $"Expected no simulationError after 300 ticks on House70, got: {simError}");
            Assert.DoesNotContain(tickEvents, e => e.TryGetProperty("type", out var t) && t.GetString() == "error");
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AwayLotEditingLock_BlocksBuyBuildOnHouse70_AllowsSimlessCommunityEdit_AndUnlocksOnReturnHome()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        var house70 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House70.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(house70) ||
            !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
            return;

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, _, session) = CreateIsolatedProfile(gameDir, 5, 70);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            DrainEvents(outputStream, writer);

            var traveller = server.World!.WalkOrder.OfType<XPerson>()
                .First(p => p[PersonData.FamilyNumber] > 0 && (p.TypeRecord?.Key ?? 0) != 0);
            var travellerGuid = traveller.TypeRecord!.Key;
            var familyNumber = traveller[PersonData.FamilyNumber];

            server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(70, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            // Visiting House70: view & mode clamp editing/buy/build off, and every edit RPC fails.
            Assert.True(server.Handle("""{"id":"v1","op":"view","editing":true,"doorWindowTool":true}"""));
            var v1Resp = ResponseFor(DrainEvents(outputStream, writer), "v1");
            Assert.True(v1Resp.GetProperty("ok").GetBoolean());
            Assert.False(v1Resp.GetProperty("result").GetProperty("editingView").GetBoolean());

            Assert.True(server.Handle("""{"id":"m1","op":"mode","mode":"buy"}"""));
            var m1Resp = ResponseFor(DrainEvents(outputStream, writer), "m1");
            Assert.True(m1Resp.GetProperty("ok").GetBoolean());
            Assert.Equal(0, m1Resp.GetProperty("result").GetProperty("mode").GetInt32());

            var blockedOps = new[]
            {
                ("buy", """{"id":"buy","op":"buyObject","guid":1,"tileX":1,"tileY":1,"level":1,"direction":0}"""),
                ("sell", """{"id":"sell","op":"sellObject","objectID":1}"""),
                ("move", """{"id":"move","op":"moveObject","objectID":1,"tileX":1,"tileY":1,"level":1,"direction":0}"""),
                ("arch", """{"id":"arch","op":"architecture","kind":"floor","tileX":1,"tileY":1,"level":1}"""),
                ("archBatch", """{"id":"archBatch","op":"architectureBatch","kind":"floor","ops":[]}"""),
                ("roof", """{"id":"roof","op":"roof","styleIndex":0,"pitch":30}"""),
                ("undo", """{"id":"undo","op":"undo"}"""),
                ("redo", """{"id":"redo","op":"redo"}"""),
            };
            foreach (var (id, payload) in blockedOps)
            {
                Assert.True(server.Handle(payload));
                var resp = ResponseFor(DrainEvents(outputStream, writer), id);
                Assert.False(resp.GetProperty("ok").GetBoolean());
                Assert.Contains("away lot", resp.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            }

            // Return home -> Buy/Build unlocks immediately.
            server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
            server.BatchRun(1);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"homeState","op":"state"}"""));
            var homeState = ResponseFor(DrainEvents(outputStream, writer), "homeState").GetProperty("result");
            Assert.Equal(5, homeState.GetProperty("house").GetInt32());
            Assert.Equal(5, homeState.GetProperty("homeHouse").GetInt32());
            Assert.False(homeState.GetProperty("away").GetBoolean());

            Assert.True(server.Handle("""{"id":"vHome","op":"view","editing":true}"""));
            var vHomeResp = ResponseFor(DrainEvents(outputStream, writer), "vHome");
            Assert.True(vHomeResp.GetProperty("ok").GetBoolean());
            Assert.True(vHomeResp.GetProperty("result").GetProperty("editingView").GetBoolean());

            // Simless community lot build/buy opened directly from neighborhood remains allowed.
            Assert.True(server.Handle("""{"id":"close","op":"closeLot","save":false}"""));
            DrainEvents(outputStream, writer);
            Assert.True(server.Handle("""{"id":"open70","op":"openLot","house":70}"""));
            var open70Resp = ResponseFor(DrainEvents(outputStream, writer), "open70");
            Assert.True(open70Resp.GetProperty("ok").GetBoolean());
            Assert.True(open70Resp.GetProperty("result").GetProperty("simless").GetBoolean());
            Assert.False(open70Resp.GetProperty("result").GetProperty("away").GetBoolean());

            Assert.True(server.Handle("""{"id":"vSimless","op":"view","editing":true}"""));
            var vSimlessResp = ResponseFor(DrainEvents(outputStream, writer), "vSimless");
            Assert.True(vSimlessResp.GetProperty("ok").GetBoolean());
            Assert.True(vSimlessResp.GetProperty("result").GetProperty("editingView").GetBoolean());
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
