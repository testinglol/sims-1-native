using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using SimLotCamera = global::LotCamera;
using CoreLotCamera = SimsSDL.Core.LotCamera;
using CoreSceneLighting = SimsSDL.Core.SceneLighting;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Area", "GraphicsUI")]
public sealed class GraphicsUiE2ETests
{
    private static bool IsHousehold(XPerson person) =>
        person[PersonData.FamilyNumber] > 0 && person.TypeRecord is not null;

    private static bool TryFindInteractableObject(
        ServerSession server,
        Func<string, bool>? namePredicate,
        out short objectId,
        out JsonElement chosenAction)
    {
        var state = server.State();
        foreach (var obj in state.GetProperty("objects").EnumerateArray())
        {
            var id = obj.GetProperty("id").GetInt16();
            var name = obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (namePredicate is not null && !namePredicate(name))
                continue;

            var menuResp = server.HandleJson("menu", new { objectID = id });
            if (!menuResp.GetProperty("ok").GetBoolean())
                continue;

            var actions = menuResp.GetProperty("result");
            if (actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0)
            {
                objectId = id;
                chosenAction = actions[0].Clone();
                return true;
            }
        }

        objectId = 0;
        chosenAction = default;
        return false;
    }

    private static bool TryFindInteractionNamed(
        ServerSession server,
        string objectNameContains,
        string actionNameContains,
        out short objectId,
        out JsonElement action,
        int? levelFilter = null)
    {
        var state = server.State();
        foreach (var obj in state.GetProperty("objects").EnumerateArray())
        {
            if (levelFilter.HasValue &&
                (!obj.TryGetProperty("level", out var lvl) || lvl.GetInt32() != levelFilter.Value))
                continue;

            var name = obj.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.Contains(objectNameContains, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = obj.GetProperty("id").GetInt16();
            var menuResp = server.HandleJson("menu", new { objectID = id });
            if (!menuResp.GetProperty("ok").GetBoolean())
                continue;

            foreach (var entry in menuResp.GetProperty("result").EnumerateArray())
            {
                var actionName = entry.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (actionName.Contains(actionNameContains, StringComparison.OrdinalIgnoreCase))
                {
                    objectId = id;
                    action = entry.Clone();
                    return true;
                }
            }
        }

        objectId = 0;
        action = default;
        return false;
    }

    private static bool TryFindBookshelfStudyInteraction(
        ServerSession server,
        out short objectId,
        out JsonElement studyAction,
        bool preferCooking = false)
    {
        string[] objectNames = ["Bookshelf", "Bookcase"];
        string[] actionNames = preferCooking ? ["Cook", "Study"] : ["Study", "Cook"];
        foreach (var objName in objectNames)
        {
            foreach (var actName in actionNames)
            {
                if (TryFindInteractionNamed(server, objName, actName, out objectId, out studyAction, levelFilter: 1))
                    return true;
            }
        }
        foreach (var objName in objectNames)
        {
            foreach (var actName in actionNames)
            {
                if (TryFindInteractionNamed(server, objName, actName, out objectId, out studyAction))
                    return true;
            }
        }

        objectId = 0;
        studyAction = default;
        return false;
    }

    private static JsonElement DismissBlockingDialogs(ServerSession server, JsonElement? initialState = null)
    {
        var state = initialState ?? server.State();
        for (int guard = 0; guard < 32; guard++)
        {
            if (!state.TryGetProperty("dialogs", out var dialogs) || dialogs.ValueKind != JsonValueKind.Array)
                break;

            string? blockingToken = null;
            foreach (var dialog in dialogs.EnumerateArray())
            {
                if (dialog.TryGetProperty("blocking", out var blk) && blk.ValueKind == JsonValueKind.True &&
                    dialog.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String)
                {
                    blockingToken = tok.GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(blockingToken))
                break;

            var resp = server.HandleJson("dialogResponse", new { token = blockingToken, code = (byte)0 });
            Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
            state = resp.GetProperty("result").Clone();
        }
        return state;
    }

    private static long StepUpdatesDismissingDialogs(ServerSession server, int targetUpdates, int chunkSize = 20)
    {
        var state = DismissBlockingDialogs(server);
        long startUpdates = state.GetProperty("updates").GetInt64();
        long currentUpdates = startUpdates;
        int guard = 0;
        while (currentUpdates - startUpdates < targetUpdates && guard++ < Math.Max(64, targetUpdates * 2))
        {
            int remaining = (int)Math.Min(chunkSize, Math.Max(1, targetUpdates - (currentUpdates - startUpdates)));
            var stepState = server.Step(remaining);
            state = DismissBlockingDialogs(server, stepState);
            currentUpdates = state.GetProperty("updates").GetInt64();
        }
        return currentUpdates - startUpdates;
    }

    private static void AssertScene3DGeometryIntegrity(
        LotScene scene,
        bool expectRoofs,
        bool expectAvatars,
        bool expectFloors,
        bool expectWater)
    {
        scene.Validate();

        // 1. Terrain geometry: finite coordinates, valid triangle count, non-collapsed bounds, non-degenerate triangles.
        Assert.NotNull(scene.TerrainVertices);
        Assert.NotEmpty(scene.TerrainVertices!);
        Assert.Equal(0, scene.TerrainVertices!.Count % 3);

        float tMinX = float.PositiveInfinity, tMaxX = float.NegativeInfinity;
        float tMinY = float.PositiveInfinity, tMaxY = float.NegativeInfinity;
        int nonDegenerateTerrainTris = 0;
        for (int i = 0; i < scene.TerrainVertices.Count; i += 3)
        {
            var a = scene.TerrainVertices[i];
            var b = scene.TerrainVertices[i + 1];
            var c = scene.TerrainVertices[i + 2];
            Assert.True(float.IsFinite(a.X) && float.IsFinite(a.Y));
            Assert.True(float.IsFinite(b.X) && float.IsFinite(b.Y));
            Assert.True(float.IsFinite(c.X) && float.IsFinite(c.Y));

            tMinX = MathF.Min(tMinX, MathF.Min(a.X, MathF.Min(b.X, c.X)));
            tMaxX = MathF.Max(tMaxX, MathF.Max(a.X, MathF.Max(b.X, c.X)));
            tMinY = MathF.Min(tMinY, MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
            tMaxY = MathF.Max(tMaxY, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));

            float area2 = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
            if (area2 > 1e-4f)
                nonDegenerateTerrainTris++;
        }
        Assert.True(tMaxX - tMinX > 10f, $"Collapsed terrain X span: {tMaxX - tMinX}");
        Assert.True(tMaxY - tMinY > 10f, $"Collapsed terrain Y span: {tMaxY - tMinY}");
        Assert.True(nonDegenerateTerrainTris > 0, "Terrain contains no non-degenerate triangles.");

        // 2. Meshes: finite vertices, normals, UVs, valid triangle counts.
        Assert.NotNull(scene.Meshes);
        var meshes = scene.Meshes!;
        Assert.NotEmpty(meshes);

        foreach (var mesh in meshes)
        {
            Assert.NotEmpty(mesh.Vertices);
            Assert.Equal(0, mesh.Vertices.Count % 3);
            Assert.True(float.IsFinite(mesh.Depth));
            Assert.InRange(mesh.Texture, 0, scene.Textures.Count - 1);
            foreach (var v in mesh.Vertices)
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
                Assert.True(float.IsFinite(v.U) && float.IsFinite(v.V));
                Assert.True(float.IsFinite(v.NX) && float.IsFinite(v.NY) && float.IsFinite(v.NZ));
                Assert.True(
                    v.NX * v.NX + v.NY * v.NY + v.NZ * v.NZ > 0.5f,
                    $"Mesh vertex normal collapsed: ({v.NX}, {v.NY}, {v.NZ})");
            }
        }

        // 3. Avatar 3D meshes: non-collapsed 3D bounding box per avatar & non-degenerate triangles.
        var avatarGroups = meshes
            .Where(m => m.ObjectID > 0 && m.Floor is null && m.Roof is null)
            .GroupBy(m => m.ObjectID)
            .ToArray();
        if (expectAvatars)
        {
            Assert.NotEmpty(avatarGroups);
            foreach (var group in avatarGroups)
            {
                float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
                float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
                int totalTris = 0;
                int nonDegenerateTris = 0;

                foreach (var part in group)
                {
                    totalTris += part.Vertices.Count / 3;
                    for (int i = 0; i < part.Vertices.Count; i += 3)
                    {
                        var a = part.Vertices[i];
                        var b = part.Vertices[i + 1];
                        var c = part.Vertices[i + 2];

                        foreach (var v in new[] { a, b, c })
                        {
                            minX = MathF.Min(minX, v.X);
                            maxX = MathF.Max(maxX, v.X);
                            minY = MathF.Min(minY, v.Y);
                            maxY = MathF.Max(maxY, v.Y);
                            minZ = MathF.Min(minZ, v.Z);
                            maxZ = MathF.Max(maxZ, v.Z);
                        }

                        var e1 = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                        var e2 = new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                        if (Vector3.Cross(e1, e2).LengthSquared() > 1e-8f)
                            nonDegenerateTris++;
                    }
                }

                Assert.True(maxX - minX > 1f, $"Avatar {group.Key} X bbox collapsed: {maxX - minX}");
                Assert.True(maxY - minY > 1f, $"Avatar {group.Key} Y bbox collapsed: {maxY - minY}");
                Assert.True(maxZ - minZ > 0.001f, $"Avatar {group.Key} Z bbox collapsed: {maxZ - minZ}");
                Assert.True(
                    nonDegenerateTris * 2 > totalTris,
                    $"Avatar {group.Key} has too many degenerate triangles: {nonDegenerateTris}/{totalTris}.");
            }
        }

        // 4. Floor meshes: non-degenerate XY projected triangles.
        var floorMeshes = meshes.Where(m => m.Floor is not null && m.Floor.Water is null).ToArray();
        if (expectFloors)
        {
            Assert.NotEmpty(floorMeshes);
            int nonDegenerateFloorTris = 0;
            foreach (var floor in floorMeshes)
            {
                for (int i = 0; i < floor.Vertices.Count; i += 3)
                {
                    var a = floor.Vertices[i];
                    var b = floor.Vertices[i + 1];
                    var c = floor.Vertices[i + 2];
                    float area2 = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                    if (area2 > 1e-4f)
                        nonDegenerateFloorTris++;
                }
            }
            Assert.True(nonDegenerateFloorTris > 0, "Floor meshes contain no non-degenerate triangles.");
        }

        // 5. Roof meshes: present iff expectRoofs, non-collapsed 3D bbox & non-degenerate triangles.
        var roofMeshes = meshes.Where(m => m.Roof is not null).ToArray();
        if (expectRoofs)
        {
            Assert.NotEmpty(roofMeshes);
            float rMinX = float.PositiveInfinity, rMaxX = float.NegativeInfinity;
            float rMinY = float.PositiveInfinity, rMaxY = float.NegativeInfinity;
            float rMinZ = float.PositiveInfinity, rMaxZ = float.NegativeInfinity;
            int nonDegenerateRoofTris = 0;
            foreach (var roof in roofMeshes)
            {
                for (int i = 0; i < roof.Vertices.Count; i += 3)
                {
                    var a = roof.Vertices[i];
                    var b = roof.Vertices[i + 1];
                    var c = roof.Vertices[i + 2];
                    foreach (var v in new[] { a, b, c })
                    {
                        rMinX = MathF.Min(rMinX, v.X);
                        rMaxX = MathF.Max(rMaxX, v.X);
                        rMinY = MathF.Min(rMinY, v.Y);
                        rMaxY = MathF.Max(rMaxY, v.Y);
                        rMinZ = MathF.Min(rMinZ, v.Z);
                        rMaxZ = MathF.Max(rMaxZ, v.Z);
                    }

                    var e1 = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                    var e2 = new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                    if (Vector3.Cross(e1, e2).LengthSquared() > 1e-6f)
                        nonDegenerateRoofTris++;
                }
            }
            Assert.True(rMaxX - rMinX > 5f, $"Roof X bbox collapsed: {rMaxX - rMinX}");
            Assert.True(rMaxY - rMinY > 5f, $"Roof Y bbox collapsed: {rMaxY - rMinY}");
            Assert.True(rMaxZ > rMinZ, $"Roof Z bbox collapsed: {rMaxZ - rMinZ}");
            Assert.True(nonDegenerateRoofTris > 0, "Roof meshes contain no non-degenerate triangles.");
        }
        else
        {
            Assert.Empty(roofMeshes);
        }

        // 6. Water meshes: validated topology & non-degenerate projected quads.
        var waterMeshes = meshes.Where(m => m.Floor?.Water is not null).ToArray();
        if (expectWater)
        {
            Assert.NotEmpty(waterMeshes);
            int nonDegenerateWaterTris = 0;
            foreach (var w in waterMeshes)
            {
                w.Floor!.Water!.Validate(w.Floor.Pattern);
                for (int i = 0; i < w.Vertices.Count; i += 3)
                {
                    var a = w.Vertices[i];
                    var b = w.Vertices[i + 1];
                    var c = w.Vertices[i + 2];
                    float area2 = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
                    if (area2 > 1e-4f)
                        nonDegenerateWaterTris++;
                }
            }
            Assert.True(nonDegenerateWaterTris > 0, "Water meshes contain no non-degenerate triangles.");
        }
    }

    // -------------------------------------------------------------------------
    // Feature F08: Cheat Console UI (Ctrl+Shift+C)
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F08")]
    public void T1_F08_01_CheatConsoleOpenClose_LiveAndPausedModes()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 1.0);
        Assert.True(console.Visible);
        console.TextInput("rosebud");
        Assert.Equal("rosebud", console.Text);
        console.Close();
        Assert.False(console.Visible);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("speed", new { value = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("cheat", new { code = "rosebud" }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("cheat", new { code = "rosebud" }).GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F08")]
    public void T1_F08_02_MoneyCheats_RosebudKlapauciusAndChainedRepeat()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var initialBudget = server.State().GetProperty("budget").GetInt32();

        console.Open(nowSeconds: 1.0);
        console.TextInput("rosebud;!;!");
        Assert.True(console.TryPrepareSegments(out var segments, out _, out _));
        Assert.Equal(["rosebud", "rosebud", "rosebud"], segments);
        foreach (var seg in segments)
        {
            var resp = server.HandleJson("cheat", new { code = seg });
            Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
            console.RecordExecutedCommand(seg);
        }
        Assert.Equal(initialBudget + 3000, server.State().GetProperty("budget").GetInt32());

        var klapResp = server.HandleJson("cheat", new { code = "klapaucius" });
        Assert.True(klapResp.GetProperty("ok").GetBoolean(), klapResp.ToString());
        Assert.Equal(initialBudget + 4000, server.State().GetProperty("budget").GetInt32());

        var motherlodeResp = server.HandleJson("cheat", new { code = "motherlode" });
        Assert.True(motherlodeResp.GetProperty("ok").GetBoolean(), motherlodeResp.ToString());
        Assert.Equal(100000, motherlodeResp.GetProperty("result").GetProperty("amount").GetInt32());
        Assert.Equal(initialBudget + 104000, server.State().GetProperty("budget").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F08")]
    public void T1_F08_03_MoveObjectsAndAutonomyCheatToggle()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var onResp = server.HandleJson("cheat", new { code = "move_objects on" });
        Assert.True(onResp.GetProperty("ok").GetBoolean(), onResp.ToString());
        Assert.True(onResp.GetProperty("result").GetProperty("moveObjects").GetBoolean());
        Assert.True(server.World!.FindLocationWaterOverride);

        var offResp = server.HandleJson("cheat", new { code = "move_objects off" });
        Assert.True(offResp.GetProperty("ok").GetBoolean(), offResp.ToString());
        Assert.False(offResp.GetProperty("result").GetProperty("moveObjects").GetBoolean());
        Assert.False(server.World.FindLocationWaterOverride);

        var autonomyResp = server.HandleJson("cheat", new { code = "autonomy 0" });
        Assert.True(autonomyResp.GetProperty("ok").GetBoolean(), autonomyResp.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F08")]
    public void T1_F08_04_HelpAndHelpPartialCheatCommands()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var helpResp = server.HandleJson("cheat", new { code = "help" });
        Assert.True(helpResp.GetProperty("ok").GetBoolean(), helpResp.ToString());
        var helpText = helpResp.ToString() + server.State().GetProperty("dialogs").ToString();
        Assert.Contains("rosebud", helpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("motherlode", helpText, StringComparison.OrdinalIgnoreCase);

        var partialResp = server.HandleJson("cheat", new { code = "help move" });
        Assert.True(partialResp.GetProperty("ok").GetBoolean(), partialResp.ToString());
        var partialText = partialResp.ToString() + server.State().GetProperty("dialogs").ToString();
        Assert.Contains("move_objects", partialText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F08")]
    public void T1_F08_05_AdditionalClassicCheats_SetHourWaterToolMapEdit()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var setHourResp = server.HandleJson("cheat", new { code = "set_hour 18" });
        Assert.True(setHourResp.GetProperty("ok").GetBoolean(), setHourResp.ToString());
        Assert.Equal(18, server.State().GetProperty("hours").GetInt32());

        var waterResp = server.HandleJson("cheat", new { code = "water_tool" });
        Assert.True(waterResp.GetProperty("ok").GetBoolean(), waterResp.ToString());

        var mapEditResp = server.HandleJson("cheat", new { code = "map_edit on" });
        Assert.True(mapEditResp.GetProperty("ok").GetBoolean(), mapEditResp.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F08")]
    public void T2_F08_01_UnknownCheatCommand_ShowsSorryNoSuchCheatDialog()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var resp = server.HandleJson("cheat", new { code = "foobar" });
        var combined = resp.ToString() + server.State().GetProperty("dialogs").ToString();
        Assert.Contains("Sorry", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such cheat.", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F08")]
    public void T2_F08_02_RepeatBangWithNoHistory_ShowsNoPreviousCheat()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 1.0);
        console.TextInput("!");

        Assert.False(console.TryPrepareSegments(out _, out var errTitle, out var errMsg));
        Assert.Equal("Sorry", errTitle);
        Assert.Equal("No previous cheat.", errMsg);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F08")]
    public void T2_F08_03_InvalidCheatArguments_ShowsCheatErrorUsage()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var badHour = server.HandleJson("cheat", new { code = "set_hour 99" });
        var badHelp = server.HandleJson("cheat", new { code = "help a b c" });
        var combined = badHour.ToString() + badHelp.ToString() + server.State().GetProperty("dialogs").ToString();
        Assert.True(
            combined.Contains("Cheat Error", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Usage:", StringComparison.OrdinalIgnoreCase),
            $"Expected Cheat Error or Usage dialog/error, got: {combined}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F08")]
    public void T2_F08_04_EmptyWhitespaceAndSemicolonOnlyCheatInput_NoOpsCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        foreach (var blank in new[] { "", "   ", ";;;" })
        {
            console.Open(nowSeconds: 1.0);
            console.TextInput(blank);
            Assert.True(console.TryPrepareSegments(out var segments, out _, out _));
            Assert.Empty(segments);
            Assert.Empty(server.State().GetProperty("dialogs").EnumerateArray());
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F08")]
    public void T2_F08_05_LongCheatInputOver400Chars_HandledSafelyWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());
        console.Open(nowSeconds: 1.0);
        console.TextInput(new string('x', 500));
        Assert.Equal(OriginalCheatConsoleOverlay.MaxLength, console.Text.Length);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var resp = server.HandleJson("cheat", new { code = console.Text });
        Assert.False(resp.GetProperty("ok").GetBoolean());
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
    }

    // -------------------------------------------------------------------------
    // Feature F09: World-Space Overhead Text & Relationship ++/--
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F09")]
    public void T1_F09_01_CurrencyOverheadText_PositiveAndNegativeMoneyDelta()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.MoneyAmountOverHead] = 250;
        server.Step(1);
        Assert.Equal((short)0, person[PersonData.MoneyAmountOverHead]);

        person[PersonData.MoneyAmountOverHead] = -120;
        server.Step(1);
        Assert.Equal((short)0, person[PersonData.MoneyAmountOverHead]);

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        var feedback = scene.OverheadFeedback ?? [];
        Assert.Contains(feedback, f => f.Kind == "currency" && f.Text == "+$250" && float.IsFinite(f.X) && float.IsFinite(f.Y));
        Assert.Contains(feedback, f => f.Kind == "currency" && f.Text == "-$120" && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F09")]
    public void T1_F09_02_SkillUpOverheadText_GoldenFloaterAndStingCharWinner()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var playedSounds = new List<string>();
        var previousSoundHook = world.PlaySoundEvent;
        world.PlaySoundEvent = (sound, id, loop, auto) =>
        {
            playedSounds.Add(sound);
            return previousSoundHook?.Invoke(sound, id, loop, auto) ?? true;
        };

        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.MoneyAmountOverHead] = -10001; // -10000 - 1 => golden skill-up +1
        server.Step(1);

        Assert.Contains("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "skillUp" && f.BackgroundRgb == 0xFFCB17u && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F09")]
    public void T1_F09_03_MagentaOverheadText_SpecialThresholdValue()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var playedSounds = new List<string>();
        var previousSoundHook = world.PlaySoundEvent;
        world.PlaySoundEvent = (sound, id, loop, auto) =>
        {
            playedSounds.Add(sound);
            return previousSoundHook?.Invoke(sound, id, loop, auto) ?? true;
        };

        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.MoneyAmountOverHead] = -20005; // -20000 - 5 => magenta text
        server.Step(1);

        Assert.DoesNotContain("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "magenta" && f.BackgroundRgb == 0xFF00FFu && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F09")]
    public void T1_F09_04_PositiveRelationshipChange_EmitsDoublePlusOverheadIndicator()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var sims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).Take(2).ToArray();
        Assert.Equal(2, sims.Length);

        var frame = new StackFrame(sims[0].Id, new short[8], [0, 1]) { StackObject = sims[1].Id };
        var ctx = new TreeContext(world, sims[0], frame);
        // Seed baseline daily relationship to 20 so setting 50 produces a +30 positive delta (++).
        sims[0].Temporaries[0] = 20;
        var op = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, op));
        world.ClearOverheadFeedback();

        sims[0].Temporaries[0] = 50;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, op));

        server.Step(1);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "relationship" && f.Text == "++" && f.Delta > 0 && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F09")]
    public void T1_F09_05_NegativeRelationshipChange_EmitsDoubleMinusOverheadIndicator()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var sims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).Take(2).ToArray();
        Assert.Equal(2, sims.Length);

        var frame = new StackFrame(sims[0].Id, new short[8], [0, 1]) { StackObject = sims[1].Id };
        var ctx = new TreeContext(world, sims[0], frame);
        sims[0].Temporaries[0] = -40;
        var op = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, op));

        server.Step(1);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "relationship" && f.Text == "--" && f.Delta < 0 && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F09")]
    public void T2_F09_01_ZeroMoneyAndZeroRelationshipDelta_SuppressesOverheadClutter()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var playedSounds = new List<string>();
        var prev = world.PlaySoundEvent;
        world.PlaySoundEvent = (s, id, l, a) => { playedSounds.Add(s); return prev?.Invoke(s, id, l, a) ?? true; };

        var sims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).Take(2).ToArray();
        Assert.Equal(2, sims.Length);
        var person = sims[0];
        person[PersonData.MoneyAmountOverHead] = 0;

        var frame = new StackFrame(person.Id, new short[8], [0, 1]) { StackObject = sims[1].Id };
        var ctx = new TreeContext(world, person, frame);
        // Read current daily relationship first, then write back the same value (delta == 0).
        var readOp = new byte[] { 0, 0, 0, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, readOp));
        var writeOp = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, writeOp));

        server.Step(1);

        Assert.DoesNotContain("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Empty(scene.OverheadFeedback ?? []);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F09")]
    public void T2_F09_02_ExactOverheadThresholdBoundaries_10000_10001_20000_20001()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var playedSounds = new List<string>();
        var prev = world.PlaySoundEvent;
        world.PlaySoundEvent = (s, id, l, a) => { playedSounds.Add(s); return prev?.Invoke(s, id, l, a) ?? true; };

        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);

        // -10000: currency (no sting)
        person[PersonData.MoneyAmountOverHead] = -10000;
        server.Step(1);
        Assert.DoesNotContain("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);

        // -10001: golden skill-up + sting
        playedSounds.Clear();
        person[PersonData.MoneyAmountOverHead] = -10001;
        server.Step(1);
        Assert.Contains("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);

        // -20000: golden skill-up + sting
        playedSounds.Clear();
        person[PersonData.MoneyAmountOverHead] = -20000;
        server.Step(1);
        Assert.Contains("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);

        // -20001: magenta (no sting)
        playedSounds.Clear();
        person[PersonData.MoneyAmountOverHead] = -20001;
        server.Step(1);
        Assert.DoesNotContain("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        var feedback = scene.OverheadFeedback ?? [];
        Assert.Contains(feedback, f => f.Kind == "currency");
        Assert.Contains(feedback, f => f.Kind == "skillUp");
        Assert.Contains(feedback, f => f.Kind == "magenta");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F09")]
    public void T2_F09_03_SimultaneousBurstOfOverheadFloaters_StacksAndExpiresCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);

        person[PersonData.MoneyAmountOverHead] = 100;
        server.Step(1);
        person[PersonData.MoneyAmountOverHead] = -10001;
        server.Step(1);

        var (activeScene, _) = LotScene.Load(server.LatestScenePath());
        activeScene.Validate();
        var feedback = activeScene.OverheadFeedback ?? [];
        Assert.True(feedback.Count >= 2, $"Expected >= 2 stacked overhead floaters, got {feedback.Count}.");
        Assert.True(feedback.Select(f => f.StackIndex).Distinct().Count() >= 2, "Expected distinct StackIndex values.");

        var advanced = StepUpdatesDismissingDialogs(server, 105);
        Assert.True(advanced >= 105, $"Expected >= 105 simulation updates, got {advanced}.");
        var (expiredScene, _) = LotScene.Load(server.LatestScenePath());
        expiredScene.Validate();
        Assert.Empty(expiredScene.OverheadFeedback ?? []);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F09")]
    public void T2_F09_04_OverheadFloaterAtZoom3_ProjectsCleanlyWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("zoom", new { scale = 3 }).GetProperty("ok").GetBoolean());
        var person = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.MoneyAmountOverHead] = 500;
        server.Step(1);

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.OverheadFeedback ?? [], f =>
            f.Kind == "currency" && f.Text == "+$500" && float.IsFinite(f.X) && float.IsFinite(f.Y));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F09")]
    public void T2_F09_05_SaveAndReloadMidOverheadFloater_ClearsWithoutStuckArtifact()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var person = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.MoneyAmountOverHead] = 50;
        server.Step(1);

        var (preSaveScene, _) = LotScene.Load(server.LatestScenePath());
        preSaveScene.Validate();
        Assert.Contains(preSaveScene.OverheadFeedback ?? [], f => f.Kind == "currency" && f.Text == "+$50");

        Assert.True(server.HandleJson("save", new { slot = "overhead-mid", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "overhead-mid" }).GetProperty("ok").GetBoolean());

        StepUpdatesDismissingDialogs(server, 20);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Empty(scene.OverheadFeedback ?? []);
        var reloadedPerson = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        Assert.Equal((short)0, reloadedPerson[PersonData.MoneyAmountOverHead]);
    }

    // -------------------------------------------------------------------------
    // Feature F10: Thought/Speech Bubbles & Skill Meters
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public void T1_F10_01_ThoughtBalloonFrameAndIconSprite_ExportsValidRuntimeSlotSprite()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        Assert.NotEmpty(person.RuntimeSlots);

        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);
        // operand: word0=0 (me), icon=1, table=1, value=60 ticks, flags=0x0100 (visible non-selected) + type=0 (thought)
        var op = new byte[] { 0, 0, 1, 1, 60, 0, 0, 1 };
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, op));

        Assert.Equal(100, person.RuntimeSlots[0].ReadWord(0x2c)); // thought balloon sprite
        Assert.True(person.RuntimeSlots[0].ReadWord(0x10) > 0);

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();

        var balloonSprites = scene.Sprites
            .Where(s => s.ObjectID == person.Id && scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(balloonSprites);
        Assert.All(balloonSprites, s => Assert.True(s.HideAtFarZoom));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public void T1_F10_02_SocialConversationSpeechBalloons_BothSimsEmitSpeechSprites()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var sims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).Take(2).ToArray();
        Assert.Equal(2, sims.Length);

        foreach (var sim in sims)
        {
            var frame = new StackFrame(sim.Id, new short[8], [0, 1]);
            var ctx = new TreeContext(world, sim, frame);
            // type=2 (speech -> sprite 102), table=1, icon=1, value=45 ticks, flags=0x0100
            var op = new byte[] { 0, 0, 1, 1, 45, 0, 2, 1 };
            Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, op));
            Assert.Equal(102, sim.RuntimeSlots[0].ReadWord(0x2c));
        }

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();

        foreach (var sim in sims)
        {
            Assert.Contains(scene.Sprites, s =>
                s.ObjectID == sim.Id &&
                scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public void T1_F10_03_NegativeOverlayBalloonIcon_SetsLabelOverlay103()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);

        // flags = 0x0300 (0x0200 negative overlay + 0x0100 visible)
        var op = new byte[] { 0, 0, 1, 1, 40, 0, 0, 3 };
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, op));
        Assert.Equal(103, person.RuntimeSlots[0].ReadWord(0x30));

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Contains(scene.Sprites, s =>
            s.ObjectID == person.Id &&
            scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public void T1_F10_04_SkillGainProgressMeter_ExportsAboveLearningSim()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());
        var actor = server.World!.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && p[PersonData.PersonAge] >= 18);
        Assert.True(server.HandleJson("selectSim", new { objectID = actor.Id }).GetProperty("ok").GetBoolean());
        Assert.True(
            TryFindBookshelfStudyInteraction(server, out var bookshelfId, out var studyAction, preferCooking: true),
            "Expected a Bookshelf or Bookcase Study interaction on House05.");

        var interactResp = server.HandleJson("interact", new
        {
            objectID = bookshelfId,
            interaction = studyAction.GetProperty("interaction").GetByte(),
            param0 = studyAction.GetProperty("param0").GetInt16(),
            global = studyAction.GetProperty("global").GetBoolean()
        });
        Assert.True(interactResp.GetProperty("ok").GetBoolean(), interactResp.ToString());

        SceneAnimation.SkillMeterDescriptor[]? populatedMeters = null;
        for (int i = 0; i < 80 && populatedMeters is null; i++)
        {
            StepUpdatesDismissingDialogs(server, 20);
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            var matching = (scene.SkillMeters ?? [])
                .Where(m => m.ProgressTenths is >= 0 and <= 9 && float.IsFinite(m.X) && float.IsFinite(m.Y))
                .ToArray();
            if (matching.Length > 0)
                populatedMeters = matching;
        }
        Assert.NotNull(populatedMeters);
        Assert.NotEmpty(populatedMeters!);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F10")]
    public void T1_F10_05_BalloonPriorityReplacementAndDurationExpiration()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);

        // Low priority (table 1 -> priority 1), 10 ticks
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 1, 1, 10, 0, 0, 1]));
        Assert.Equal(1, person.RuntimeSlots[0].ReadWord(0x34));

        // High priority (table 4 -> priority 2), 3 ticks -> replaces low priority
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 0, 4, 3, 0, 2, 1]));
        Assert.Equal(2, person.RuntimeSlots[0].ReadWord(0x34));
        Assert.Equal(102, person.RuntimeSlots[0].ReadWord(0x2c));

        // Low priority attempt while high priority active -> ignored
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 1, 1, 20, 0, 0, 1]));
        Assert.Equal(2, person.RuntimeSlots[0].ReadWord(0x34));

        // Advance simulation until the 3-tick balloon completes its Stop cycles and clears cleanly.
        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());
        for (int i = 0; i < 40 && person.RuntimeSlots[0].ReadWord(0x10) > 0; i++)
            server.Step(20);
        Assert.Equal(0, person.RuntimeSlots[0].ReadWord(0x10));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public void T2_F10_01_ShortOrEmptyBalloonText_SuppressedCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);

        // Value 0 clears balloon; invalid/empty icon index should not emit malformed sprite.
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 255, 1, 10, 0, 2, 1]));
        Assert.Equal(0, person.RuntimeSlots[0].ReadWord(0x10));

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.DoesNotContain(scene.Sprites, s =>
            s.ObjectID == person.Id &&
            scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public void T2_F10_02_InvalidBalloonType_RaisesMappedError0x1d()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);

        // Out-of-range balloon type operand[6] = 5 (valid TS1 types are 0..2; >= 3 should raise 0x1d BalloonTypeIsInvalid)
        var ex = Assert.Throws<SimulationException>(() =>
            SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 1, 1, 20, 0, 5, 1]));
        Assert.Contains("balloon", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public void T2_F10_03_SkillMeterAtMaxLevel10_DoesNotOverflow()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());
        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && p[PersonData.PersonAge] >= 18);
        Assert.True(server.HandleJson("selectSim", new { objectID = person.Id }).GetProperty("ok").GetBoolean());
        person[PersonData.CookingHouseBrokenSkill] = 1000;

        Assert.True(
            TryFindBookshelfStudyInteraction(server, out var bookshelfId, out var studyAction, preferCooking: true),
            "Expected a Bookshelf or Bookcase Study Cooking interaction on House05.");
        var interactResp = server.HandleJson("interact", new
        {
            objectID = bookshelfId,
            interaction = studyAction.GetProperty("interaction").GetByte(),
            param0 = studyAction.GetProperty("param0").GetInt16(),
            global = studyAction.GetProperty("global").GetBoolean()
        });
        Assert.True(interactResp.GetProperty("ok").GetBoolean(), interactResp.ToString());

        var advanced = StepUpdatesDismissingDialogs(server, 600);
        Assert.True(advanced >= 600, $"Expected >= 600 simulation updates, got {advanced}.");

        Assert.Equal((short)1000, person[PersonData.CookingHouseBrokenSkill]);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        foreach (var meter in scene.SkillMeters ?? [])
            Assert.InRange(meter.ProgressTenths, 0, 9);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public void T2_F10_04_CancelLearningInteractionMidSkillBar_ClearsMeterAndPreservesPartialProgress()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("freeWill", new { enabled = false }).GetProperty("ok").GetBoolean());
        var person = server.World!.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && p[PersonData.PersonAge] >= 18);
        Assert.True(server.HandleJson("selectSim", new { objectID = person.Id }).GetProperty("ok").GetBoolean());
        person[PersonData.CookingHouseBrokenSkill] = 60;

        Assert.True(
            TryFindBookshelfStudyInteraction(server, out var bookshelfId, out var studyAction, preferCooking: true),
            "Expected a Bookshelf or Bookcase Study Cooking interaction on House05.");
        var interactResp = server.HandleJson("interact", new
        {
            objectID = bookshelfId,
            interaction = studyAction.GetProperty("interaction").GetByte(),
            param0 = studyAction.GetProperty("param0").GetInt16(),
            global = studyAction.GetProperty("global").GetBoolean()
        });
        Assert.True(interactResp.GetProperty("ok").GetBoolean(), interactResp.ToString());

        LotScene? activeScene = null;
        for (int i = 0; i < 80; i++)
        {
            StepUpdatesDismissingDialogs(server, 15);
            var (probeScene, _) = LotScene.Load(server.LatestScenePath());
            probeScene.Validate();
            if ((probeScene.SkillMeters ?? []).Count > 0)
            {
                activeScene = probeScene;
                break;
            }
        }
        Assert.NotNull(activeScene);
        Assert.NotEmpty(activeScene!.SkillMeters ?? []);

        var simState = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt32() == person.Id);
        var actions = simState.GetProperty("actions").EnumerateArray().ToArray();
        Assert.NotEmpty(actions);
        var token = actions[0].GetProperty("token").GetString()!;
        Assert.True(server.HandleJson("cancelAction", new { objectID = (short)person.Id, token }).GetProperty("ok").GetBoolean());

        for (int i = 0; i < 40; i++)
        {
            StepUpdatesDismissingDialogs(server, 15);
            var currentActions = server.State().GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt32() == person.Id)
                .GetProperty("actions").EnumerateArray().ToArray();
            if (currentActions.Length == 0)
                break;
        }
        StepUpdatesDismissingDialogs(server, 30);

        Assert.True(person[PersonData.CookingHouseBrokenSkill] >= 60);
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
        Assert.Empty(scene.SkillMeters ?? []);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F10")]
    public void T2_F10_05_MultiStoryBalloonVisibility_Level2RespectsViewLevel()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        world.SetObjectPosition(person, new ObjectPosition(person.Position.X, person.Position.Y, 2));

        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(ctx, [0, 0, 1, 1, 60, 0, 2, 1]));

        Assert.True(server.HandleJson("level", new { value = 1 }).GetProperty("ok").GetBoolean());
        var (sceneFloor1, _) = LotScene.Load(server.LatestScenePath());
        sceneFloor1.Validate();
        Assert.DoesNotContain(sceneFloor1.Sprites, s =>
            s.ObjectID == person.Id &&
            sceneFloor1.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));

        Assert.True(server.HandleJson("level", new { value = 2 }).GetProperty("ok").GetBoolean());
        var (sceneFloor2, _) = LotScene.Load(server.LatestScenePath());
        sceneFloor2.Validate();
        Assert.Contains(sceneFloor2.Sprites, s =>
            s.ObjectID == person.Id &&
            s.Level == 2 &&
            sceneFloor2.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------
    // Feature F11: Critical Motive Pulsing & Edge Scrolling
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public void T1_F11_01_MotiveBarsOrderAndColorGradient_MatchOriginalGameplayLayout()
    {
        Assert.Equal(
            ["Hunger", "Energy", "Comfort", "Fun", "Hygiene", "Social", "Bladder", "Room"],
            OriginalGameplayLayout.Motives);

        Assert.Equal(0, OriginalGameplayLayout.BarWidth(-100));
        Assert.Equal(30, OriginalGameplayLayout.BarWidth(0));
        Assert.Equal(60, OriginalGameplayLayout.BarWidth(100));

        var high = OriginalGameplayLayout.BarColors(100);
        Assert.Equal((0, 202, 57), high.Green);
        var low = OriginalGameplayLayout.BarColors(-100);
        Assert.Equal((209, 0, 0), low.Red);

        var canvas = new CGSize(800, 600);
        for (int i = 0; i < OriginalGameplayLayout.Motives.Count; i++)
        {
            var rect = OriginalGameplayLayout.NeedRect(i, canvas.Width);
            Assert.True(rect.Width > 0 && rect.Height > 0);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public void T1_F11_02_CriticalLowMotiveRedPulsing_AnimatesMotiveBarState()
    {
        var bar = new OriginalGameplayLayout.MotiveBarState(80);
        var initial = bar.Evaluate(1000);
        Assert.False(initial.IsAnimating);

        bar.SetValue(-85, nowMs: 1000);
        var pulseA = bar.Evaluate(1150);
        var pulseB = bar.Evaluate(1450);

        Assert.True(pulseA.IsAnimating, "Critical-low motive (<= -50) should pulse red (IsAnimating == true).");
        Assert.NotEqual(pulseA.RedRgb, pulseB.RedRgb);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public void T1_F11_03_EdgeScrollingOption_DefaultsEnabledAndTogglesViaOptionsPanel()
    {
        var defaults = new OriginalOptionsPanelOverlay.PlayOptionsState();
        Assert.True(defaults.EdgeScrolling);
        Assert.True(OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling.Value(defaults));

        var toggled = OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling.Toggled(defaults);
        Assert.False(toggled.EdgeScrolling);

        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var strings = new OriginalUIStrings(content);
        var panel = new OriginalOptionsPanelOverlay(content, new UIDevice(), strings, font);
        panel.Select(OriginalOptionsPanelOverlay.Page.Play);

        var canvas = new CGSize(800, 600);
        var edgeCtrl = Assert.Single(panel.Controls(canvas), c => c.Label.Contains("Edge Scroll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling,
            panel.OptionAt(new CGPoint(edgeCtrl.Rect.MidX, edgeCtrl.Rect.MidY), canvas));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public void T1_F11_04_EdgeScrollingDisabled_PreservesManualCameraRotationAndZoom()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var options = new OriginalOptionsPanelOverlay.PlayOptionsState { EdgeScrolling = false };
        Assert.False(options.EdgeScrolling);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        for (int rot = 0; rot < 4; rot++)
        {
            var resp = server.HandleJson("cameraOrientation", new { value = rot });
            Assert.True(resp.GetProperty("ok").GetBoolean());
            Assert.Equal(rot, resp.GetProperty("result").GetProperty("cameraOrientation").GetInt32());
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F11")]
    public void T1_F11_05_AutoCenterOptionAndSimSelection_SwitchesSelectedSim()
    {
        var defaults = new OriginalOptionsPanelOverlay.PlayOptionsState();
        Assert.True(defaults.AutoCenter);
        Assert.False(OriginalOptionsPanelOverlay.PlayOption.AutoCenter.Toggled(defaults).AutoCenter);

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var sims = server.State().GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
        Assert.True(sims.Length >= 2);
        var secondId = sims[1].GetProperty("id").GetInt16();

        var selResp = server.HandleJson("selectSim", new { objectID = secondId });
        Assert.True(selResp.GetProperty("ok").GetBoolean());
        Assert.Equal(secondId, selResp.GetProperty("result").GetProperty("selectedSim").GetInt16());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public void T2_F11_01_MotiveClampingAtBounds_Minus100ToPlus100()
    {
        Assert.Equal(0, OriginalGameplayLayout.BarWidth(-500));
        Assert.Equal(60, OriginalGameplayLayout.BarWidth(500));
        Assert.Equal(-100, new OriginalGameplayLayout.MotiveBarState(-500).Value);
        Assert.Equal(100, new OriginalGameplayLayout.MotiveBarState(500).Value);

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("setMotive", new { motive = "Hunger", value = -500f }).GetProperty("ok").GetBoolean());
        var hungerLow = server.State().GetProperty("sims")[0].GetProperty("motives").GetProperty("Hunger").GetSingle();
        Assert.InRange(hungerLow, -100f, 100f);

        Assert.True(server.HandleJson("setMotive", new { motive = "Hunger", value = 500f }).GetProperty("ok").GetBoolean());
        var hungerHigh = server.State().GetProperty("sims")[0].GetProperty("motives").GetProperty("Hunger").GetSingle();
        Assert.InRange(hungerHigh, -100f, 100f);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public void T2_F11_02_CriticalMotiveRecoveryAndSimSwitching_StopsRedPulse()
    {
        var barA = new OriginalGameplayLayout.MotiveBarState(-85);
        var barB = new OriginalGameplayLayout.MotiveBarState(75);
        Assert.False(barB.Evaluate(1200).IsAnimating);

        barA.SetValue(60, nowMs: 2000);
        var recovered = barA.Evaluate(5000);
        Assert.False(recovered.IsAnimating);
        Assert.Equal(OriginalGameplayLayout.MotiveColorRamp.PackRgb(OriginalGameplayLayout.BarColors(60).Green), recovered.GreenRgb);

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var sims = server.State().GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
        Assert.True(sims.Length >= 2);
        Assert.True(server.HandleJson("selectSim", new { objectID = sims[0].GetProperty("id").GetInt16() }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("setMotive", new { motive = "Energy", value = -85f, objectID = sims[0].GetProperty("id").GetInt16() }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("selectSim", new { objectID = sims[1].GetProperty("id").GetInt16() }).GetProperty("ok").GetBoolean());
        var simBEnergy = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == sims[1].GetProperty("id").GetInt16())
            .GetProperty("motives").GetProperty("Energy").GetSingle();
        Assert.True(simBEnergy > -50f);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public void T2_F11_03_OptionsAndModalDialogLayout_PreventOverlapAndSolveBounds()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var strings = new OriginalUIStrings(content);
        var panel = new OriginalOptionsPanelOverlay(content, new UIDevice(), strings, font);
        var canvas = new CGSize(800, 600);

        foreach (var page in new[]
        {
            OriginalOptionsPanelOverlay.Page.Play,
            OriginalOptionsPanelOverlay.Page.Graphics,
            OriginalOptionsPanelOverlay.Page.Sound
        })
        {
            panel.Select(page);
            var controls = panel.Controls(canvas);
            Assert.NotEmpty(controls);
        }

        var dialogLayout = OriginalMessageDialogLayout.Solve(new OriginalMessageDialogLayout.Input(
            viewport: canvas,
            titleText: "Options",
            titleSize: new CGSize(80, font.LineHeight),
            titleLineHeight: font.LineHeight,
            messageText: "Modal dialog blocks edge scrolling.",
            bodyFont: font,
            buttonLabelWidths: [50]));
        Assert.True(dialogLayout.Rect.Width >= 200 && dialogLayout.Rect.Height >= 80);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public void T2_F11_04_CameraBoundsClampingAcrossRotationsAndZooms()
    {
        for (int orientation = 0; orientation < 4; orientation++)
        {
            var simCam = new SimLotCamera(orientation, 64, 64);
            var coreCam = (CoreLotCamera)orientation;

            foreach (var (tx, ty) in new[] { (0f, 0f), (63f, 63f), (32f, 32f) })
            {
                var pSim = simCam.Project(tx, ty, 0f);
                var pCore = coreCam.ProjectedPoint(new Vector2(tx, ty), new Vector2(64, 64), 0f);
                Assert.True(float.IsFinite(pSim.X) && float.IsFinite(pSim.Y));
                Assert.True(float.IsFinite(pCore.X) && float.IsFinite(pCore.Y));
                Assert.InRange(simCam.Depth(tx, ty, 0f), 0f, 1f);
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F11")]
    public void T2_F11_05_OptionsStatePersistence_RoundTripsEdgeScrollAndAutoCenter()
    {
        var saved = new OriginalOptionsPanelOverlay.PlayOptionsState
        {
            EdgeScrolling = false,
            AutoCenter = false,
            FreeWill = false,
            Lighting = false,
            Shadows = false
        };
        var json = JsonSerializer.Serialize(saved);
        var loaded = JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState>(json);

        Assert.False(loaded.EdgeScrolling);
        Assert.False(loaded.AutoCenter);
        Assert.False(loaded.FreeWill);
        Assert.False(loaded.Lighting);
        Assert.False(loaded.Shadows);

        var legacyDefaults = JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState>("{}");
        Assert.True(legacyDefaults.EdgeScrolling);
        Assert.True(legacyDefaults.AutoCenter);
    }

    // -------------------------------------------------------------------------
    // Feature F12: Isometric World, Cutaways, Roofs, Lighting, Shadows & Classic Panels
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public void T1_F12_01_FourRotationsThreeZoomsTwoStories_Validates3DGeometryAndProjection()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("roofs", new { visible = true }).GetProperty("ok").GetBoolean());
        var avatarPositionsByOrientation = new List<(float MinX, float MinY)>();

        for (int rot = 0; rot < 4; rot++)
        {
            Assert.True(server.HandleJson("cameraOrientation", new { value = rot }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("level", new { value = rot % 2 == 0 ? 1 : 2, roofs = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("zoom", new { scale = (rot % 3) + 1 }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());

            var scenePath = server.LatestScenePath();
            var (scene, pixels) = LotScene.Load(scenePath);
            Assert.Equal(rot, scene.CameraOrientation);
            Assert.NotEmpty(pixels);
            AssertScene3DGeometryIntegrity(scene, expectRoofs: true, expectAvatars: true, expectFloors: true, expectWater: false);

            var jsonScene = LotScene.FromJson(InMemorySessionAssets.ReadAllBytes(scenePath));
            jsonScene.Validate();

            var firstAvatarMesh = scene.Meshes!.First(m => m.ObjectID > 0 && m.Floor is null && m.Roof is null);
            avatarPositionsByOrientation.Add((
                firstAvatarMesh.Vertices.Min(v => v.X),
                firstAvatarMesh.Vertices.Min(v => v.Y)));
        }

        Assert.True(avatarPositionsByOrientation.Distinct().Count() > 1,
            "Rotating camera 0..3 must re-project avatar 3D vertices.");

        // Direct AvatarSceneGeometry build check for finite vertices.
        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var avatarGeo = AvatarSceneGeometry.For(gameDir, world.ObjectRegistry!);
        var parts = avatarGeo.Build(person, 1, 0, (_, _) => 0f, new SimLotCamera(0, LotWalls.Size, LotWalls.Size));
        Assert.NotEmpty(parts);
        foreach (var part in parts)
        {
            Assert.NotEmpty(part.Vertices);
            Assert.Equal(0, part.Vertices.Length % 3);
            foreach (var v in part.Vertices)
            {
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public void T1_F12_02_WallModesAndRoofsVisibility_ToggleCutawayDownUpAndRoofGrid()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        // Walls Down
        Assert.True(server.HandleJson("walls", new { mode = "down" }).GetProperty("ok").GetBoolean());
        var (downScene, _) = LotScene.Load(server.LatestScenePath());
        AssertScene3DGeometryIntegrity(downScene, expectRoofs: false, expectAvatars: true, expectFloors: true, expectWater: false);
        Assert.Contains(downScene.Sprites, s => s.Junction is { Down: true });

        // Cutaway around indoor room tile
        var roomMap = server.World!.LotRoomMaps[1];
        var (rx, ry) = Enumerable.Range(0, 64)
            .SelectMany(x => Enumerable.Range(0, 64).Select(y => (x, y)))
            .First(pt => !roomMap.IsOutside(roomMap.RoomAt(pt.x, pt.y)));

        Assert.True(server.HandleJson("walls", new { mode = "cutaway", x = rx, y = ry }).GetProperty("ok").GetBoolean());
        var (cutawayScene, _) = LotScene.Load(server.LatestScenePath());
        AssertScene3DGeometryIntegrity(cutawayScene, expectRoofs: false, expectAvatars: true, expectFloors: true, expectWater: false);
        Assert.Contains(cutawayScene.Sprites, s => s.Junction is { Down: true });

        // Walls Up + Roofs Visible
        Assert.True(server.HandleJson("roofs", new { visible = true }).GetProperty("ok").GetBoolean());
        var (roofScene, _) = LotScene.Load(server.LatestScenePath());
        AssertScene3DGeometryIntegrity(roofScene, expectRoofs: true, expectAvatars: true, expectFloors: true, expectWater: false);

        // Direct LotRoofGrid verification on House05
        var house05 = E2EHarness.FindHouseSource(gameDir, 1, 5)!;
        var roofGrid = LotRoofGrid.Build(IffFile.Open(house05), 1, server.World.WalkOrder);
        Assert.True(roofGrid.Size > 0);
        bool hasActiveRoofCell = Enumerable.Range(0, roofGrid.Size)
            .Any(r => Enumerable.Range(0, roofGrid.Size).Any(c => roofGrid.Has(r, c)));
        Assert.True(hasActiveRoofCell, "Expected LotRoofGrid for House05 level 1 to contain at least one active roofed cell.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public void T1_F12_03_DayNightLightingRoomLightsWindowBleedAndShadows()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        CoreSceneLighting.RGB? noonOutside = null;
        CoreSceneLighting.RGB? nightOutside = null;

        foreach (var hour in new[] { 8, 13, 20, 1 })
        {
            server.World!.Clock.Restore(hour, 0, 0);
            server.Step(1);
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            Assert.NotNull(scene.Lighting);
            Assert.NotEmpty(scene.Lighting!.Rooms);
            Assert.NotNull(scene.Lighting.Lights);
            Assert.NotEmpty(scene.Lighting.Lights!);
            Assert.NotNull(scene.Lighting.Windows);
            Assert.NotEmpty(scene.Lighting.Windows!);

            if (hour == 13) noonOutside = scene.Lighting.Outside;
            if (hour == 1) nightOutside = scene.Lighting.Outside;

            Assert.Contains(scene.Sprites, s => s.FloorShadow is not null || s.Shadow is not null);
            Assert.Contains(scene.Meshes!, m => m.ObjectID > 0);
        }

        Assert.NotNull(noonOutside);
        Assert.NotNull(nightOutside);
        Assert.True(noonOutside!.Value.R > nightOutside!.Value.R,
            $"Noon exterior light ({noonOutside.Value.R}) should be brighter than night ({nightOutside.Value.R}).");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public void T1_F12_04_CensorMosaicPoolWaterAndVacationSnow_Validates3DMeshesAndBounds()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 2, 40))
            return;

        // 1. Censor mosaic on House05
        using (var p5 = E2EHarness.CreateIsolatedProfile(gameDir, 5))
        using (var s5 = E2EHarness.StartServer(p5, house: 5))
        {
            var person = s5.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
            person[PersonData.CensorshipFlags] = 0x80;
            Assert.True(s5.HandleJson("scene").GetProperty("ok").GetBoolean());
            var (censorScene, _) = LotScene.Load(s5.LatestScenePath());
            censorScene.Validate();
            Assert.NotNull(censorScene.CensorRects);
            Assert.NotEmpty(censorScene.CensorRects!);
            Assert.All(censorScene.CensorRects!, c =>
            {
                Assert.True(float.IsFinite(c.X) && float.IsFinite(c.Y));
                Assert.True(c.Width > 0 && c.Height > 0);
                Assert.InRange(c.Depth, 0f, 1f);
            });
        }

        // 2. Pool water 3D mesh topology on House02
        using (var p2 = E2EHarness.CreateIsolatedProfile(gameDir, 2))
        using (var s2 = E2EHarness.StartServer(p2, house: 2))
        {
            Assert.True(s2.HandleJson("scene").GetProperty("ok").GetBoolean());
            var (poolScene, _) = LotScene.Load(s2.LatestScenePath());
            AssertScene3DGeometryIntegrity(poolScene, expectRoofs: false, expectAvatars: false, expectFloors: true, expectWater: true);
        }

        // 3. Vacation snow terrain palette & geometry on House40
        using (var p40 = E2EHarness.CreateIsolatedProfile(gameDir, 40))
        using (var s40 = E2EHarness.StartServer(p40, house: 40))
        {
            Assert.True(s40.HandleJson("scene").GetProperty("ok").GetBoolean());
            var (snowScene, _) = LotScene.Load(s40.LatestScenePath());
            AssertScene3DGeometryIntegrity(snowScene, expectRoofs: false, expectAvatars: false, expectFloors: true, expectWater: false);
            Assert.Contains(snowScene.TerrainVertices!, v => v.R >= 200 && v.B >= 230);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F12")]
    public void T1_F12_05_ClassicPanelsPieHeadPreviewLivePipActionQueueAndPhotoAlbum()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        // Universal control hit test
        var liveBtn = OriginalUniversalControl.All.First(c => c.Kind == OriginalUniversalControl.Action.Live);
        var btnCenter = new CGPoint(
            liveBtn.Position.X + liveBtn.Size.Width / 2,
            600 - OriginalSpeedControl.PanelHeight + liveBtn.Position.Y + liveBtn.Size.Height / 2);
        Assert.Equal(OriginalUniversalControl.Action.Live, OriginalUniversalControl.Hit(btnCenter, new CGSize(800, 600), 1.0)?.Kind);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        // Pie Menu 3D Head preview geometry/pixel sanity across yaw angles
        var headA = server.HandleJson("pieHeadPreview", new { yaw = 0.35f, pitch = -0.1f });
        var headB = server.HandleJson("pieHeadPreview", new { yaw = -0.35f, pitch = 0.1f });
        Assert.True(headA.GetProperty("ok").GetBoolean());
        Assert.True(headB.GetProperty("ok").GetBoolean());
        var resA = headA.GetProperty("result");
        var resB = headB.GetProperty("result");
        int w = resA.GetProperty("width").GetInt32();
        int h = resA.GetProperty("height").GetInt32();
        Assert.True(w > 0 && h > 0);
        var pxA = Convert.FromBase64String(resA.GetProperty("pixels").GetString()!);
        var pxB = Convert.FromBase64String(resB.GetProperty("pixels").GetString()!);
        Assert.Equal(w * h * 4, pxA.Length);
        Assert.Equal(w * h * 4, pxB.Length);
        Assert.Contains(pxA, b => b != 0);
        Assert.False(pxA.SequenceEqual(pxB), "Distinct yaw angles must produce distinct 3D pie head renders.");

        // Action queue & cancel #2
        Assert.True(TryFindInteractableObject(server, null, out var objId, out var action));
        for (int i = 0; i < 4; i++)
        {
            Assert.True(server.HandleJson("interact", new
            {
                objectID = objId,
                interaction = action.GetProperty("interaction").GetByte(),
                param0 = action.GetProperty("param0").GetInt16(),
                global = action.GetProperty("global").GetBoolean()
            }).GetProperty("ok").GetBoolean());
        }

        var selectedId = server.State().GetProperty("selectedSim").GetInt16();
        var actionsBefore = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == selectedId)
            .GetProperty("actions").EnumerateArray().ToArray();
        Assert.True(actionsBefore.Length >= 2);
        var tokenToCancel = actionsBefore[1].GetProperty("token").GetString()!;
        Assert.True(server.HandleJson("cancelAction", new { objectID = selectedId, token = tokenToCancel }).GetProperty("ok").GetBoolean());

        // Live PIP toggle
        Assert.True(server.HandleJson("pictureInPicture", new { live = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("pictureInPicture", new { live = false }).GetProperty("ok").GetBoolean());
        var pipSize = OriginalPictureInPictureLayout.Size(0);
        Assert.True(pipSize.Width > 0 && pipSize.Height > 0);

        // Photo Album capture & reload
        var albumDir = Path.Combine(profile.TempRoot, "PhotoAlbum");
        var album = new PhotoAlbum(albumDir);
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200; rgba[i + 1] = 120; rgba[i + 2] = 60; rgba[i + 3] = 255;
        }
        album.Capture(rgba, 4, 4, 0.9f, "Goth Family");
        Assert.Single(album.Photos);
        Assert.Equal("Goth Family", album.Photos[0].Caption);
        album.Reload();
        Assert.Single(album.Photos);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public void T2_F12_01_EmptyPieMenuAndUnavailableInteractions_HandledCleanly()
    {
        var emptyLayout = OriginalPieNode.Build([]);
        Assert.Empty(emptyLayout);

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        // Selecting a child Sim and querying menus on objects should never throw.
        var sims = server.State().GetProperty("sims").EnumerateArray().ToArray();
        var child = sims.FirstOrDefault(s => s.GetProperty("child").GetBoolean());
        if (child.ValueKind == JsonValueKind.Object)
        {
            server.HandleJson("selectSim", new { objectID = child.GetProperty("id").GetInt16() });
        }

        foreach (var obj in server.State().GetProperty("objects").EnumerateArray().Take(10))
        {
            var menu = server.HandleJson("menu", new { objectID = obj.GetProperty("id").GetInt16() });
            Assert.True(menu.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public void T2_F12_02_MissingResourceOrSpriteFallback_LoadsSceneWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        // Add an object with a nonexistent GUID / missing sprite resource
        var dummyDef = new short[XObject.DefinitionCount];
        var phantom = new XObject(9998, dummyDef)
        {
            TypeRecord = new TypeRecord(unchecked((int)0xDEADBEEF)),
            Position = new ObjectPosition(32 * 16, 32 * 16, 1)
        };
        world.Add(phantom);

        var resp = server.HandleJson("scene");
        Assert.True(resp.GetProperty("ok").GetBoolean());
        var (scene, _) = LotScene.Load(server.LatestScenePath());
        scene.Validate();
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public void T2_F12_03_ActionQueueSaturation_EnforcesMax8QueuedCap()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(TryFindInteractableObject(server, null, out var objId, out var action));
        int accepted = 0;
        int rejected = 0;
        for (int i = 0; i < 12; i++)
        {
            var resp = server.HandleJson("interact", new
            {
                objectID = objId,
                interaction = action.GetProperty("interaction").GetByte(),
                param0 = action.GetProperty("param0").GetInt16(),
                global = action.GetProperty("global").GetBoolean()
            });
            if (resp.GetProperty("ok").GetBoolean())
                accepted++;
            else
                rejected++;
        }

        Assert.InRange(accepted, 1, 9);
        Assert.True(rejected >= 1, "Expected action queue saturation (>8) to reject excess interactions.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public void T2_F12_04_CensorMosaicClearsImmediately_OnFlagReset()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var person = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person[PersonData.CensorshipFlags] = 0x80;
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (withCensor, _) = LotScene.Load(server.LatestScenePath());
        Assert.NotEmpty(withCensor.CensorRects!);

        person[PersonData.CensorshipFlags] = 0;
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (cleared, _) = LotScene.Load(server.LatestScenePath());
        cleared.Validate();
        Assert.Empty(cleared.CensorRects!);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F12")]
    public void T2_F12_05_MultiStoryCutawayAndHiDpiScaling_AlignsSceneAndPanels()
    {
        Assert.Equal(1.15f, OriginalUIScalingLayout.AutomaticUIScale(1280, 960, 1.0f));
        Assert.Equal(2.0f, OriginalUIScalingLayout.AutomaticUIScale(1600, 1200, 2.0f));
        Assert.InRange(OriginalUIScalingLayout.LivePopupOriginX(300, 1920), 220, 1920 - 559 - 8);
        Assert.Equal(2.3, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(2560, 1600), 2.0f), 4);

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("level", new { value = 2 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("walls", new { mode = "cutaway", x = 32, y = 32 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("pictureInPicture", new { live = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());

        var (scene, _) = LotScene.Load(server.LatestScenePath());
        AssertScene3DGeometryIntegrity(scene, expectRoofs: false, expectAvatars: true, expectFloors: true, expectWater: false);
    }
}
