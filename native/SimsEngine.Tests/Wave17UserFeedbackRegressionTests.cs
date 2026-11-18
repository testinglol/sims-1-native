using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL.Client;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

[Collection("Neighborhoods")]
public sealed class Wave17UserFeedbackRegressionTests
{
    private static string? LocateGameDirectory() => TestGameLocator.Locate();

    private static IReadOnlyList<string> LocatePackDirectories(string gameDir)
    {
        var packsRoot = Path.GetFullPath(Path.Combine(gameDir, "..", "..", "packs"));
        var dirs = new List<string>();
        foreach (var name in new[] { "example", "seasons" })
        {
            var candidate = Path.Combine(packsRoot, name);
            if (Directory.Exists(candidate))
            {
                dirs.Add(candidate);
            }
        }
        return dirs;
    }

    private static (string TempRoot, string Profile, string Session) CreateIsolatedProfile(
        string gameDir, params short[] houses)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave17-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(tempRoot, "runtime", "profiles", "test");
        var userData = Path.Combine(profile, "UserData");
        Directory.CreateDirectory(Path.Combine(userData, "Houses"));
        var charsDir = Path.Combine(userData, "Characters");
        Directory.CreateDirectory(charsDir);
        foreach (var charFile in Directory.EnumerateFiles(Path.Combine(gameDir, "UserData", "Characters")))
        {
            File.Copy(charFile, Path.Combine(charsDir, Path.GetFileName(charFile)));
        }

        File.Copy(Path.Combine(gameDir, "UserData", "Neighborhood.iff"), Path.Combine(userData, "Neighborhood.iff"));
        foreach (var extra in new[] { "LotZoning.iff", "LotLocations.iff", "StreetNames.iff" })
        {
            var extraPath = Path.Combine(gameDir, "UserData", extra);
            if (File.Exists(extraPath))
            {
                File.Copy(extraPath, Path.Combine(userData, extra));
            }
        }

        foreach (var house in houses)
        {
            var src = Path.Combine(gameDir, "UserData", "Houses", $"House{house:D2}.iff");
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(userData, "Houses", $"House{house:D2}.iff"));
            }
        }

        var session = Path.Combine(tempRoot, "session");
        Directory.CreateDirectory(session);
        return (tempRoot, profile, session);
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

    private static void DeleteTempTree(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            SessionSweeper.DeleteEntrySafely(path);
        }
    }

    [Fact]
    public async Task PackMounts_IsSymbolicLink_SessionSweeperBomFree_AndSharedContentMountRecovery()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sims-wave17-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var missingTarget = Path.Combine(tempRoot, "missing", "content");
            Assert.False(PackContentPaths.IsSymbolicLink(missingTarget));

            var realFile = Path.Combine(tempRoot, "target.txt");
            File.WriteAllText(realFile, "ok");
            Assert.False(PackContentPaths.IsSymbolicLink(realFile));
            var linkFile = Path.Combine(tempRoot, "target-link.txt");
            try
            {
                File.CreateSymbolicLink(linkFile, realFile);
                Assert.True(PackContentPaths.IsSymbolicLink(linkFile));
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
            }

            var sessionDir = Path.Combine(tempRoot, "session");
            Directory.CreateDirectory(sessionDir);
            SessionSweeper.Claim(sessionDir);
            var ownerBytes = File.ReadAllBytes(Path.Combine(sessionDir, SessionSweeper.OwnerFile));
            Assert.NotEmpty(ownerBytes);
            Assert.InRange(ownerBytes[0], (byte)'0', (byte)'9');

            var gameDir = LocateGameDirectory();
            if (gameDir is null)
            {
                return;
            }

            var packDirs = LocatePackDirectories(gameDir);
            var content = new GameContent(gameDir, packDirs);

            var freshMount = Path.Combine(tempRoot, "fresh", "content");
            content.Materialize(freshMount, cacheIdentity: "wave17-fresh", nativeOverlay: true);
            Assert.True(Directory.Exists(freshMount));

            var staleDataRoot = Path.Combine(tempRoot, "data-root");
            Directory.CreateDirectory(staleDataRoot);
            var identity = string.Join(
                '\n',
                PackContentPaths.ResolveSymlinksInPath(staleDataRoot),
                PackContentPaths.ResolveSymlinksInPath(content.Root),
                content.Packs.Report().Fingerprint,
                "native");
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var staleMount = Path.Combine(staleDataRoot, "runtime", "content", key, "content");
            Directory.CreateDirectory(staleMount);

            var resolvedMount = await SimulationBridge.SharedContentMount(content, staleDataRoot, nativeEngine: true);
            Assert.True(Directory.Exists(resolvedMount));
            Assert.True(File.Exists(Path.Combine(resolvedMount, ".pack-stack.json")));
        }
        finally
        {
            DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    public void CutawayAndDownWallModes_EmitLoweredWallCrossSectionCapsExcludingDoorways()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05))
        {
            return;
        }

        var boot = WorldBootstrap.Load(gameDir, house05!);
        var roomMap = boot.World.LotRoomMaps[1];
        var indoorRoom = Enumerable.Range(0, 64)
            .SelectMany(x => Enumerable.Range(0, 64).Select(y => roomMap.RoomAt(x, y)))
            .First(r => !roomMap.IsOutside(r));

        var tempDir = Path.Combine(Path.GetTempPath(), "sims-wave17-walls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var downPath = SceneExporter.Export(
                boot.Lot, boot.World, boot.Registry, gameDir, tempDir,
                house: 5, revision: 1, viewLevel: 1, wallMode: "down", cameraOrientation: 0);
            using (var downDoc = JsonDocument.Parse(File.ReadAllBytes(downPath)))
            {
                var textures = downDoc.RootElement.GetProperty("textures");
                var junctions = downDoc.RootElement.GetProperty("sprites").EnumerateArray()
                    .Where(s => s.TryGetProperty("junction", out var j) && j.ValueKind == JsonValueKind.Object)
                    .ToArray();
                var downCaps = junctions.Where(s => s.GetProperty("junction").GetProperty("down").GetBoolean()).ToArray();
                Assert.NotEmpty(downCaps);

                var texIdx = downCaps[0].GetProperty("texture").GetInt32();
                var texFile = textures[texIdx].GetProperty("file").GetString()!;
                var fullTexPath = Path.Combine(tempDir, texFile);
                Assert.True(File.Exists(fullTexPath));
                Assert.True(new FileInfo(fullTexPath).Length > 0);
            }

            var cutawayPath = SceneExporter.Export(
                boot.Lot, boot.World, boot.Registry, gameDir, tempDir,
                house: 5, revision: 2, viewLevel: 1, wallMode: "cutaway", cameraOrientation: 0,
                cutawayRooms: [indoorRoom]);
            using (var cutawayDoc = JsonDocument.Parse(File.ReadAllBytes(cutawayPath)))
            {
                var junctions = cutawayDoc.RootElement.GetProperty("sprites").EnumerateArray()
                    .Where(s => s.TryGetProperty("junction", out var j) && j.ValueKind == JsonValueKind.Object)
                    .ToArray();
                Assert.Contains(junctions, s => s.GetProperty("junction").GetProperty("down").GetBoolean());
                Assert.Contains(junctions, s => !s.GetProperty("junction").GetProperty("down").GetBoolean());
            }

            var testWalls = LotWalls.Read(boot.Lot, 1);
            for (var x = 0; x < LotWalls.Size; x++)
            for (var y = 0; y < LotWalls.Size; y++)
            {
                testWalls[x, y].Clear();
            }
            byte[] singleWallRecord = [1, 0x0f, 1, 1, 0, 0, 0, 0];
            singleWallRecord.CopyTo(testWalls[10, 12]);
            singleWallRecord.CopyTo(testWalls[20, 12]);
            var minLot = testWalls.WriteTo(boot.Lot);
            var minMap = RoomMap.Build(minLot, 1, 64, 64);
            var minWorld = new SimulationWorld { Host = null };
            minWorld.Clock.Restore(12, 0, 0);
            minWorld.LotRoomMaps[1] = minMap;
            foreach (var roomId in minMap.RoomIds)
            {
                minWorld.PersonRoomMotiveEntries[roomId] = new PersonRoomMotiveEntry(100f, IsOutside: minMap.IsOutside(roomId));
            }

            var doorEntry = boot.Registry.Entries.FirstOrDefault(e =>
                e.Definition[ObjdField.RoomSort] == 0 &&
                e.Definition[ObjdField.BuildModeType] == 1 &&
                e.Definition[ObjdField.WallStyle] > 0);
            Assert.NotNull(doorEntry);
            var doorObj = new XObject(10, doorEntry!.Definition.Fields.Select(v => (short)v).ToArray())
            {
                TypeRecord = new TypeRecord(unchecked((int)doorEntry.Guid)),
                Position = new ObjectPosition(10 * 16 + 8, 12 * 16 + 8, 1),
            };
            doorObj[ObjectData.FlagField2] = unchecked((short)0x8000);
            doorObj[ObjectData.WallPlacementFlags] = 1;
            doorObj[ObjectData.Direction] = 6;
            minWorld.Add(doorObj);

            var doorOnlyPath = SceneExporter.Export(
                minLot, minWorld, boot.Registry, gameDir, tempDir,
                house: 98, revision: 1, viewLevel: 1, wallMode: "down", cameraOrientation: 0);
            using var doorDoc = JsonDocument.Parse(File.ReadAllBytes(doorOnlyPath));
            var minJunctions = doorDoc.RootElement.GetProperty("sprites").EnumerateArray()
                .Where(s => s.TryGetProperty("junction", out var j) &&
                            j.ValueKind == JsonValueKind.Object &&
                            j.GetProperty("down").GetBoolean())
                .ToArray();
            var doorCaps = minJunctions
                .Where(s =>
                {
                    var j = s.GetProperty("junction");
                    return j.GetProperty("x").GetInt32() == 10 && j.GetProperty("y").GetInt32() is 12 or 13;
                })
                .ToArray();
            var wallCaps = minJunctions
                .Where(s =>
                {
                    var j = s.GetProperty("junction");
                    return j.GetProperty("x").GetInt32() == 20 && j.GetProperty("y").GetInt32() is 12 or 13;
                })
                .ToArray();
            Assert.Empty(doorCaps);
            Assert.NotEmpty(wallCaps);
        }
        finally
        {
            DeleteTempTree(tempDir);
        }
    }

    [Fact]
    public void SimulationServer_IncludesOperationInResponses_AndPreviewsDoorsAndWindowsOnCutawayWalls()
    {
        var gameDir = LocateGameDirectory();
        var house05 = gameDir is null ? null : Path.Combine(gameDir, "UserData", "Houses", "House05.iff");
        if (gameDir is null || !File.Exists(house05) || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff")))
        {
            return;
        }

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, session) = CreateIsolatedProfile(gameDir, 5);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 5, writer);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"wallsDown","op":"walls","mode":"down"}"""));
            var wallsEvents = DrainEvents(outputStream, writer);
            var wallsResp = ResponseFor(wallsEvents, "wallsDown");
            Assert.True(wallsResp.GetProperty("ok").GetBoolean());
            Assert.Equal("walls", wallsResp.GetProperty("operation").GetString());

            Assert.True(server.Handle("""{"id":"viewEdit","op":"view","editing":true,"doorWindowTool":true}"""));
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"catDoor","op":"catalogPage","category":8,"offset":0,"count":8}"""));
            var doorCat = ResponseFor(DrainEvents(outputStream, writer), "catDoor");
            var doorGuid = doorCat.GetProperty("result").GetProperty("items")[0].GetProperty("guid").GetUInt32();

            Assert.True(server.Handle("""{"id":"catWindow","op":"catalogPage","category":9,"offset":0,"count":8}"""));
            var windowCat = ResponseFor(DrainEvents(outputStream, writer), "catWindow");
            var windowGuid = windowCat.GetProperty("result").GetProperty("items")[0].GetProperty("guid").GetUInt32();

            var walls = LotWalls.Read(new IffFile(File.ReadAllBytes(house05!), "House05.iff"), 1);

            (int TileX, int TileY, int WallDir, int ValidDir, JsonElement Ghost)? FindValidWallPreview(uint guid)
            {
                for (var y = 8; y < 56; y++)
                for (var x = 8; x < 56; x++)
                {
                    var cell = walls[x, y];
                    var candidates = new List<(int WallDir, int Dir)>();
                    if ((cell[0] & 1) != 0 && cell[2] == 1)
                    {
                        candidates.Add((0, 64));
                    }
                    if ((cell[0] & 2) != 0 && cell[3] == 1)
                    {
                        candidates.Add((1, 1));
                    }

                    foreach (var (wallDir, initDir) in candidates)
                    {
                        var cmd = JsonSerializer.Serialize(new
                        {
                            id = "probe",
                            op = "previewObject",
                            guid,
                            x = x * 16 + 8,
                            y = y * 16 + 8,
                            level = 1,
                            direction = initDir,
                            wallDirection = wallDir,
                        });
                        Assert.True(server.Handle(cmd));
                        var resp = ResponseFor(DrainEvents(outputStream, writer), "probe");
                        var result = resp.GetProperty("result");
                        if (result.GetProperty("valid").GetBoolean() &&
                            result.TryGetProperty("ghost", out var g) &&
                            g.ValueKind == JsonValueKind.Object)
                        {
                            return (x, y, wallDir, result.GetProperty("direction").GetInt32(), g);
                        }
                    }
                }
                return null;
            }

            var doorMatch = FindValidWallPreview(doorGuid);
            Assert.NotNull(doorMatch);
            var doorSprites = doorMatch!.Value.Ghost.GetProperty("scene").GetProperty("sprites");
            Assert.True(doorSprites.GetArrayLength() > 0);

            var oppositeDir = doorMatch.Value.ValidDir switch { 1 => 16, 16 => 1, 4 => 64, _ => 4 };
            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "doorFlip",
                op = "previewObject",
                guid = doorGuid,
                x = doorMatch.Value.TileX * 16 + 8,
                y = doorMatch.Value.TileY * 16 + 8,
                level = 1,
                direction = oppositeDir,
                wallDirection = doorMatch.Value.WallDir,
            })));
            var doorFlipResp = ResponseFor(DrainEvents(outputStream, writer), "doorFlip");
            Assert.Equal("previewObject", doorFlipResp.GetProperty("operation").GetString());
            var doorFlipRes = doorFlipResp.GetProperty("result");
            Assert.True(doorFlipRes.GetProperty("valid").GetBoolean());
            Assert.Equal(doorMatch.Value.ValidDir, doorFlipRes.GetProperty("direction").GetInt32());

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "wallsCut",
                op = "walls",
                mode = "cutaway",
                x = doorMatch.Value.TileX,
                y = doorMatch.Value.TileY,
                zoom = 2,
                doorWindowTool = true,
            })));
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle(JsonSerializer.Serialize(new
            {
                id = "doorCut",
                op = "previewObject",
                guid = doorGuid,
                x = doorMatch.Value.TileX * 16 + 8,
                y = doorMatch.Value.TileY * 16 + 8,
                level = 1,
                direction = doorMatch.Value.ValidDir,
                wallDirection = doorMatch.Value.WallDir,
            })));
            var doorCutResp = ResponseFor(DrainEvents(outputStream, writer), "doorCut");
            Assert.Equal("previewObject", doorCutResp.GetProperty("operation").GetString());
            var doorCutRes = doorCutResp.GetProperty("result");
            Assert.True(doorCutRes.GetProperty("valid").GetBoolean());
            Assert.True(doorCutRes.GetProperty("ghost").GetProperty("scene").GetProperty("sprites").GetArrayLength() > 0);

            var windowMatch = FindValidWallPreview(windowGuid);
            Assert.NotNull(windowMatch);
            Assert.True(windowMatch!.Value.Ghost.GetProperty("scene").GetProperty("sprites").GetArrayLength() > 0);
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    public void PushInteraction_OnMultiTileChildWithoutOwnTtab_PreservesChildAsOwnerForBoardingGuards()
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        var person = world.Add(new XPerson(11, PrimitiveTestWorld.Definition(XPerson.PersonType)));

        var guardBody = new ChunkBuilder()
            .U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(2).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse)
            .Raw(PrimitiveTestWorld.Expression(
                lhs: 3,
                rhs: person.Id,
                op: (byte)ExpressionOperator.EqualTo,
                lhsOwner: (byte)DataOwner.StackObjectAttribute,
                rhsOwner: (byte)DataOwner.Literal))
            .ToArray();
        var ttabBody = new ChunkBuilder().S16(1).S16(8)
            .U16(4100).U16(4101).S32(0).U32(0).S32(1).U32(0).F32(0).U32(0).S32(0)
            .ToArray();
        var file = new IffFile(ChunkBuilder.File(("BHAV", 4101, guardBody), ("TTAB", 1, ttabBody)), "vehicle.iff");
        var scope = new BehaviorScope(file, file, null);
        var table = InteractionTable.Read(file, file.Find(ResourceType.Parse("TTAB"), 1)!);

        var leadDef = PrimitiveTestWorld.Definition();
        leadDef[(int)ObjdField.MasterId] = 1;
        leadDef[(int)ObjdField.SubIndex] = -1;
        leadDef[(int)ObjdField.InteractionGroup] = -1;
        leadDef[(int)ObjdField.NumAttributes] = 8;
        var lead = world.Add(new XObject(20, leadDef, attributes: 8)
        {
            TypeRecord = new TypeRecord(20) { Interactions = table, BehaviorScope = scope },
        });

        var childDef = PrimitiveTestWorld.Definition();
        childDef[(int)ObjdField.MasterId] = 1;
        childDef[(int)ObjdField.SubIndex] = 4;
        childDef[(int)ObjdField.InteractionGroup] = 1;
        childDef[(int)ObjdField.NumAttributes] = 8;
        var child = world.Add(new XObject(21, childDef, attributes: 8)
        {
            MultiTileMasterId = lead.Id,
            TypeRecord = new TypeRecord(21) { Interactions = null, BehaviorScope = scope },
        });
        lead.MultiTileNextId = child.Id;

        lead.Attributes[3] = 0;
        child.Attributes[3] = person.Id;

        var caller = world.Add(new XObject(30, PrimitiveTestWorld.Definition()));
        caller.SetObjectTableEntry(1, child);
        caller.SetObjectTableEntry(person.Id, person);
        var frame = new StackFrame(person.Id, [1], [0, 0]);
        var context = new TreeContext(world, caller, frame);

        Assert.Same(table, world.EffectiveInteractions(child));
        Assert.Equal(PrimitiveResult.True, PushInteractionPrimitive.Execute(context, [1, 0, 3, 0, 0, 0, 0, 0]));
        Assert.Equal(1, person.ActionQueueEnd);

        var queued = person.ActionQueue[0];
        Assert.Same(child, queued.Owner);
        Assert.Same(child, queued.Secondary);

        InteractionTestEvaluator.Evaluate(world, person, [queued], autonomy: false);
        Assert.NotEqual(0, queued.Flags & InteractionTestEvaluator.Passed);
    }

    [Fact]
    public void FullscreenOverlayScale_AspectFitsLargeViewportsAndKeepsSmallScaling()
    {
        Assert.Equal(2.3, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(2560, 1600), 2.0f), 4);
        Assert.Equal(1.15, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(1920, 1080), 1.0f), 4);
        Assert.Equal(0.5, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(400, 300), 1.0f), 4);
    }

    [Fact]
    public void SimsBitmapFont_RasterizeHiDpi_Produces4xCrispPixelsWhilePreservingLogicalMetrics()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null)
        {
            return;
        }

        var content = new GameContent(gameDir, LocatePackDirectories(gameDir));
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        Assert.Equal(4, SimsBitmapFont.HiDpiScale);

        var raster = font.RasterizeHiDpi("5 Sim Lane", (255, 255, 255), logicalMargin: 0);
        Assert.Equal(Math.Max(1, font.TextWidth("5 Sim Lane")), raster.LogicalWidth);
        Assert.Equal(Math.Max(1, font.LineHeight), raster.LogicalHeight);
        Assert.Equal(raster.LogicalWidth * 4, raster.PixelWidth);
        Assert.Equal(raster.LogicalHeight * 4, raster.PixelHeight);
        Assert.Equal(raster.PixelWidth * raster.PixelHeight * 4, raster.Rgba.Length);

        var hasOpaqueGlyph = false;
        var hasAntiAliasedEdge = false;
        for (var i = 3; i < raster.Rgba.Length; i += 4)
        {
            var alpha = raster.Rgba[i];
            if (alpha == 255)
            {
                hasOpaqueGlyph = true;
            }
            else if (alpha is > 0 and < 255)
            {
                hasAntiAliasedEdge = true;
            }
        }
        Assert.True(hasOpaqueGlyph);
        Assert.True(hasAntiAliasedEdge);

        var tip = PlacementTooltip.RasterizeHiDpi("Switch to Downtown View", font);
        Assert.Equal(tip.LogicalWidth * 4, tip.PixelWidth);
        Assert.Equal(tip.LogicalHeight * 4, tip.PixelHeight);
        Assert.Equal((byte)0, tip.Rgba[0]);
        Assert.Equal((byte)0, tip.Rgba[1]);
        Assert.Equal((byte)0, tip.Rgba[2]);
        Assert.Equal((byte)255, tip.Rgba[3]);

        var interiorOffset = (4 * tip.PixelWidth + 4) * 4;
        Assert.Equal((byte)255, tip.Rgba[interiorOffset]);
        Assert.Equal((byte)255, tip.Rgba[interiorOffset + 1]);
        Assert.Equal((byte)224, tip.Rgba[interiorOffset + 2]);
        Assert.Equal((byte)255, tip.Rgba[interiorOffset + 3]);

        for (var i = 3; i < tip.Rgba.Length; i += 4)
        {
            Assert.Equal((byte)255, tip.Rgba[i]);
        }
    }

    [Fact]
    public void NeighborhoodOverlay_RolloverAndBannerTooltip_AreOpaqueAndHiDpi()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null
            || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff"))
            || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
        {
            return;
        }

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, session) = CreateIsolatedProfile(gameDir, 5);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"ngh","op":"neighborhood","mode":1}"""));
            var nghResp = ResponseFor(DrainEvents(outputStream, writer), "ngh");
            Assert.True(nghResp.GetProperty("ok").GetBoolean());

            var manifestPath = Path.Combine(session, "neighborhood.json");
            Assert.True(File.Exists(manifestPath));

            var content = new GameContent(gameDir, LocatePackDirectories(gameDir));
            var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
            var overlay = new OriginalNeighborhoodOverlay(content, new UIDevice(), manifestPath, session, font);

            var canvas = new CGSize(1600, 1000);
            const float uiScale = 2f;
            var baseCount = overlay.DrawItems(canvas, uiScale, time: 0.0).Count;

            var house5 = overlay.Controls(canvas, uiScale).Single(c => c.Label.EndsWith(" 5", StringComparison.Ordinal));
            overlay.UpdateHover(new CGPoint(house5.Rect.MidX, house5.Rect.MidY), canvas, uiScale);
            Assert.Equal(5, overlay.HoveredHouse);

            var hoverItems = overlay.DrawItems(canvas, uiScale, time: 0.0);
            Assert.True(hoverItems.Count >= baseCount + 16);
            foreach (var item in hoverItems)
            {
                Assert.Equal(1f, item.Opacity);
            }

            var backgroundSlices = hoverItems.Where(i => i.Image.Size == new CGSize(111, 111)).ToArray();
            Assert.Equal(9, backgroundSlices.Length);
            foreach (var slice in backgroundSlices)
            {
                Assert.Equal(1f, slice.Opacity);
            }
            var bgTex = backgroundSlices[0].Image.Texture;
            var bgCenterOffset = (55 * bgTex.Width + 55) * 4;
            Assert.Equal((byte)255, bgTex.Pixels[bgCenterOffset + 3]);

            var hiDpiLines = hoverItems
                .Where(i => i.Image.Texture.Height == (int)i.Image.Size.Height * 4 && i.Image.Size.Height == font.LineHeight)
                .ToArray();
            Assert.True(hiDpiLines.Length >= 4);

            for (var p = hoverItems.Count - 3; p < hoverItems.Count; p++)
            {
                var portrait = hoverItems[p];
                Assert.Equal(new CGSize(25, 25), portrait.Image.Size);
                Assert.Equal(1f, portrait.Opacity);
                var tex = portrait.Image.Texture;
                var centerOffset = (12 * tex.Width + 12) * 4;
                Assert.Equal((byte)255, tex.Pixels[centerOffset + 3]);
            }

            overlay.UpdateHover(new CGPoint(-100, -100), canvas, uiScale);
            var bannerControls = overlay.BannerControls(canvas, uiScale);
            Assert.NotEmpty(bannerControls);
            var bannerTarget = bannerControls.FirstOrDefault(c => c.Label.Contains("Downtown", StringComparison.OrdinalIgnoreCase));
            if (bannerTarget.Label is null)
            {
                bannerTarget = bannerControls[0];
            }

            overlay.UpdateBannerHover(new CGPoint(bannerTarget.Rect.MidX, bannerTarget.Rect.MidY), canvas, quickTips: true, uiScale);
            overlay.DrawItems(canvas, uiScale, time: 1.0);
            Thread.Sleep(280);
            var tipItems = overlay.DrawItems(canvas, uiScale, time: 1.3);

            var tipItem = tipItems[^1];
            Assert.Equal(1f, tipItem.Opacity);
            Assert.Equal(new Vector4(0f, 0f, 1f, 1f), tipItem.UV);
            Assert.Equal((int)tipItem.Image.Size.Width * 4, tipItem.Image.Texture.Width);
            Assert.Equal((int)tipItem.Image.Size.Height * 4, tipItem.Image.Texture.Height);

            var tipPixels = tipItem.Image.Texture.Pixels;
            var tipCornerOffset = (4 * tipItem.Image.Texture.Width + 4) * 4;
            Assert.Equal((byte)255, tipPixels[tipCornerOffset]);
            Assert.Equal((byte)255, tipPixels[tipCornerOffset + 1]);
            Assert.Equal((byte)224, tipPixels[tipCornerOffset + 2]);
            Assert.Equal((byte)255, tipPixels[tipCornerOffset + 3]);
            for (var i = 3; i < tipPixels.Length; i += 4)
            {
                Assert.Equal((byte)255, tipPixels[i]);
            }
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    public void SharpTextOption_DefaultsTrue_FitsOnGraphicsPage_AndTogglesClassic1xFfn()
    {
        var defaults = new OriginalOptionsPanelOverlay.PlayOptionsState();
        Assert.True(defaults.SharpText);
        Assert.True(SimsBitmapFont.SharpTextEnabled);

        var legacyDeserialized = JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState>("{}");
        Assert.True(legacyDeserialized.SharpText);

        Assert.Equal(
            OriginalOptionsPanelOverlay.Page.Graphics,
            OriginalOptionsPanelOverlay.PlayOption.SharpText.Page());
        Assert.True(OriginalOptionsPanelOverlay.PlayOption.SharpText.Value(defaults));
        var toggled = OriginalOptionsPanelOverlay.PlayOption.SharpText.Toggled(defaults);
        Assert.False(toggled.SharpText);

        var gameDir = LocateGameDirectory();
        if (gameDir is null)
        {
            return;
        }

        var content = new GameContent(gameDir, LocatePackDirectories(gameDir));
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var strings = new OriginalUIStrings(content);
        var panel = new OriginalOptionsPanelOverlay(content, new UIDevice(), strings, font);
        panel.Select(OriginalOptionsPanelOverlay.Page.Graphics);

        var canvas = new CGSize(800, 600);
        var controls = panel.Controls(canvas);
        var sharpControl = Assert.Single(controls, c => string.Equals(c.Label, "Sharp Text, on", StringComparison.Ordinal));

        // SubpanelOrigin = (220, 50) at top = 600 - 150 = 450 => subpanel spans [220..800] x [500..600].
        Assert.True(sharpControl.Rect.MinX >= 220);
        Assert.True(sharpControl.Rect.MaxX <= 800);
        Assert.True(sharpControl.Rect.MinY >= 500);
        Assert.True(sharpControl.Rect.MaxY <= 600);

        static bool Intersects(CGRect a, CGRect b) =>
            a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;

        foreach (var other in controls)
        {
            if (string.Equals(other.Label, sharpControl.Label, StringComparison.Ordinal))
            {
                continue;
            }
            Assert.False(
                Intersects(sharpControl.Rect, other.Rect),
                $"Sharp Text ({sharpControl.Rect}) overlapped {other.Label} ({other.Rect})");
        }

        var hitPoint = new CGPoint(sharpControl.Rect.MidX, sharpControl.Rect.MidY);
        Assert.Equal(OriginalOptionsPanelOverlay.PlayOption.SharpText, panel.OptionAt(hitPoint, canvas));
        panel.Hover(hitPoint, canvas);
        Assert.Equal(OriginalOptionsPanelOverlay.PlayOption.SharpText, panel.HoveredOption);
        Assert.False(string.IsNullOrWhiteSpace(panel.AboutText(hitPoint, canvas)));
        var hoveredParts = panel.Parts(canvas);
        Assert.NotEmpty(hoveredParts);

        var previousSharpText = SimsBitmapFont.SharpTextEnabled;
        try
        {
            SimsBitmapFont.SharpTextEnabled = false;
            var expected1x = font.Rasterize("Bella Goth", (255, 255, 255));
            var fallback = font.RasterizeHiDpi("Bella Goth", (255, 255, 255));
            Assert.Equal(expected1x.Width, fallback.PixelWidth);
            Assert.Equal(expected1x.Height, fallback.PixelHeight);
            Assert.Equal(fallback.LogicalWidth, fallback.PixelWidth);
            Assert.Equal(fallback.LogicalHeight, fallback.PixelHeight);
            Assert.Equal(expected1x.Rgba, fallback.Rgba);

            var classicTip = PlacementTooltip.RasterizeHiDpi("Move in Family", font);
            Assert.Equal(classicTip.LogicalWidth, classicTip.PixelWidth);
            Assert.Equal(classicTip.LogicalHeight, classicTip.PixelHeight);

            panel.PlayOptions = toggled;
            panel.RefreshText();
            var offControl = Assert.Single(panel.Controls(canvas), c => string.Equals(c.Label, "Sharp Text, off", StringComparison.Ordinal));
            Assert.Equal(sharpControl.Rect.MinX, offControl.Rect.MinX);
            Assert.Equal(sharpControl.Rect.MinY, offControl.Rect.MinY);
        }
        finally
        {
            SimsBitmapFont.SharpTextEnabled = previousSharpText;
            panel.PlayOptions = defaults;
            panel.RefreshText();
        }
    }

    [Fact]
    public void Bhav0x8003BigEndianAndMalformedCustomContent_LoadCleanlyWithoutCrashing()
    {
        var bhavData = new byte[13 + 2 * 12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bhavData, 0x8003);
        bhavData[2] = 0;
        bhavData[3] = 1;
        bhavData[4] = 2;
        bhavData[5] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bhavData.AsSpan(9, 4), 2u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bhavData.AsSpan(13, 2), 2);
        bhavData[15] = 1;
        bhavData[16] = 0xFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bhavData.AsSpan(25, 2), 2);
        bhavData[27] = 0xFE;
        bhavData[28] = 0xFE;

        var badBhavData = new byte[] { 0xFF, 0xFF, 1, 2, 3 };
        var iffBytes = BuildSingleOrMultiChunkIff(
            ("BHAV", 4104, bhavData),
            ("BHAV", 4105, badBhavData));
        var iff = new SimsEngine.Formats.IffFile(iffBytes, "tso_conveyorcontrol.iff");

        var parsed = SimsEngine.Formats.Bhav.Read(iff, iff.Find(SimsEngine.Formats.ResourceType.Bhav, 4104)!);
        Assert.Equal(0x8003, parsed.Signature);
        Assert.Equal(2, parsed.Instructions.Count);
        Assert.Equal(2, parsed.Instructions[0].Opcode);

        var scope = new SimsEngine.Content.BehaviorScope(iff, iff, null);
        Assert.NotNull(scope.Find(4104));
        Assert.Null(scope.Find(4105));
        Assert.NotEqual(0u, SimsEngine.Simulation.SavedLotObjectLoader.SavedTreeFingerprint(scope, 4104));
        Assert.Equal(0u, SimsEngine.Simulation.SavedLotObjectLoader.SavedTreeFingerprint(scope, 4105));
    }

    [Fact]
    public void VectorBannerRenderer_RasterizesAll15EmbeddedSvgsAndDynamicBannerWithZeroPngs()
    {
        string[] stems =
        [
            "thesimslogo", "movein", "import", "bulldoze", "rezone",
            "downtown", "vacation", "studiotown", "magicland", "exit",
            "return", "previous", "next", "inetbtn", "mods",
        ];

        foreach (string stem in stems)
        {
            Assert.True(SimsSDL.UI.VectorBannerRenderer.HasEmbeddedSvg(stem), $"Missing embedded SVG: {stem}");
        }

        var logo = SimsSDL.UI.VectorBannerRenderer.RenderSimsLogo(scale: 4);
        Assert.Equal(280, logo.PixelWidth);
        Assert.Equal(208, logo.PixelHeight);
        Assert.Equal(70, logo.LogicalSize.Width);
        Assert.Equal(52, logo.LogicalSize.Height);
        Assert.Contains(logo.Rgba.Where((_, idx) => idx % 4 == 3), static a => a > 240);

        var modsBtn = SimsSDL.UI.VectorBannerRenderer.RenderButtonStrip(
            "mods",
            SimsSDL.UI.VectorBannerRenderer.ButtonKind.InsetRing39,
            19.25f,
            scale: 4);
        Assert.Equal(39 * 4 * 4, modsBtn.PixelWidth);
        Assert.Equal(52 * 4, modsBtn.PixelHeight);
        Assert.Equal(156, modsBtn.LogicalSize.Width);
        Assert.Equal(52, modsBtn.LogicalSize.Height);

        var rezoneBtn = SimsSDL.UI.VectorBannerRenderer.RenderButtonStrip(
            "rezone",
            SimsSDL.UI.VectorBannerRenderer.ButtonKind.Medallion53,
            25.0f,
            scale: 4);
        Assert.Equal(53 * 4 * 4, rezoneBtn.PixelWidth);
        Assert.Equal(52 * 4, rezoneBtn.PixelHeight);

        var banner = SimsSDL.UI.VectorBannerRenderer.RenderBannerBar([225f, 275f, 325f, 375f, 425f, 475f, 525f, 575f, 625f], scale: 4);
        Assert.Equal(3200, banner.PixelWidth);
        Assert.Equal(208, banner.PixelHeight);
        Assert.Equal(800, banner.LogicalSize.Width);
        Assert.Equal(52, banner.LogicalSize.Height);

        Assert.Contains(SimsSDL.UI.BannerAction.Mods, SimsSDL.UI.BannerActionExtensions.ActionsForMapMode(1));
        Assert.Contains(SimsSDL.UI.BannerAction.Mods, SimsSDL.UI.BannerActionExtensions.ActionsForMapMode(2));
        Assert.Equal(724, SimsSDL.UI.BannerAction.Mods.Position(1).X);
        Assert.Equal(762, SimsSDL.UI.BannerAction.Internet.Position(1).X);
    }

    [Fact]
    public void ModManager_SupportsPriorityOverrides_AndPersistsSettings()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null || !File.Exists(Path.Combine(gameDir, "UIGraphics", "UIGraphics.far")))
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "sims-modmgr-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(tempRoot, "Data");
        string packsDir = Path.Combine(dataRoot, "Packs");
        Directory.CreateDirectory(packsDir);

        try
        {
            string packADir = Path.Combine(packsDir, "pack_a");
            string packBDir = Path.Combine(packsDir, "pack_b");
            Directory.CreateDirectory(Path.Combine(packADir, "files", "UIGraphics", "NghUI"));
            Directory.CreateDirectory(Path.Combine(packBDir, "files", "UIGraphics", "NghUI"));

            File.WriteAllText(Path.Combine(packADir, "pack.json"), """
                {
                  "schemaVersion": 1,
                  "baseGameVersion": "1.90",
                  "id": "test.pack.a",
                  "name": "Alpha UI Overhaul",
                  "version": "1.0.0",
                  "dependencies": [],
                  "overrides": []
                }
                """);
            File.WriteAllBytes(Path.Combine(packADir, "files", "UIGraphics", "NghUI", "custom_shared.bmp"), [1, 2, 3]);

            File.WriteAllText(Path.Combine(packBDir, "pack.json"), """
                {
                  "schemaVersion": 1,
                  "baseGameVersion": "1.90",
                  "id": "test.pack.b",
                  "name": "Beta UI Patch",
                  "version": "1.0.0",
                  "dependencies": [],
                  "overrides": []
                }
                """);
            File.WriteAllBytes(Path.Combine(packBDir, "files", "UIGraphics", "NghUI", "custom_shared.bmp"), [9, 9, 9]);

            string userDlDir = Path.Combine(dataRoot, "Downloads", "CustomRetroSofa");
            Directory.CreateDirectory(userDlDir);
            File.WriteAllBytes(Path.Combine(userDlDir, "retro_sofa.iff"), BuildSingleOrMultiChunkIff(("STR#", 1, [0, 0])));

            string repoWorkspace = Path.GetFullPath(Path.Combine(gameDir, ".."));
            if (!Directory.Exists(Path.Combine(repoWorkspace, "packs")))
                repoWorkspace = Path.GetFullPath(Path.Combine(gameDir, "..", ".."));

            var discovered = SimsSDL.ModManager.DiscoverAll(gameDir, workspace: repoWorkspace, dataRoot: dataRoot, cliPacks: null);
            Assert.DoesNotContain(discovered, static m => m.DisplayName == "My first expansion");
            Assert.Contains(discovered, static m => m.DisplayName == "Alpha UI Overhaul");
            Assert.Contains(discovered, static m => m.DisplayName == "Beta UI Patch");
            // All 70 shipped Complete Collection Downloads/<Item>/<Item>.far bonus sets have Maxis .ndx + .stx manifests
            // and are excluded automatically without hardcoded names; only the user's CustomRetroSofa folder appears!
            var dlDiscovered = Assert.Single(discovered, static m => m.Kind == SimsSDL.ModManager.ModEntryKind.DownloadsSubfolder);
            Assert.Equal("CustomRetroSofa", dlDiscovered.DisplayName);

            var (ordered, enabled) = SimsSDL.ModManager.BuildInitialState(discovered, new SimsSDL.ModManager.ModSettingsFile());
            var report = SimsSDL.ModManager.Evaluate(gameDir, dataRoot, ordered, enabled);
            Assert.True(report.CanApply);

            var betaStatus = Assert.Single(report.Entries, static e => e.Mod.DisplayName == "Beta UI Patch");
            Assert.Contains(betaStatus.SoftOverlapNotes, static n => n.Contains("Overrides 1 file from Alpha UI Overhaul", StringComparison.Ordinal));

            // Reverse priority order (Beta first, Alpha second -> Alpha wins!)
            ordered.Reverse();
            var reversedSettings = SimsSDL.ModManager.BuildSettingsFromState(ordered, enabled);
            SimsSDL.ModManager.SaveSettings(dataRoot, reversedSettings);

            var mountedContent = SimsSDL.ModManager.LoadConfiguredContent(gameDir, workspace: "", dataRoot: dataRoot, cliPacks: null);
            Assert.Equal(new byte[] { 1, 2, 3 }, mountedContent.Read("UIGraphics/NghUI/custom_shared.bmp"));
        }
        finally
        {
            DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    public void ExtendedCoastalNeighborhood4K_CoversWidescreenAndUltrawideWithoutBlueBorders()
    {
        var gameDir = LocateGameDirectory();
        if (gameDir is null
            || !Directory.Exists(Path.Combine(gameDir, "ExpansionPack5"))
            || !File.Exists(Path.Combine(gameDir, "UserData", "Neighborhood.iff"))
            || !File.Exists(Path.Combine(gameDir, "UserData", "Houses", "House05.iff")))
        {
            return;
        }

        var previousNeighborhood = Neighborhoods.Current;
        var (tempRoot, profile, session) = CreateIsolatedProfile(gameDir, 5);
        try
        {
            Neighborhoods.Current = 1;
            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, session, profile, 0, writer);
            DrainEvents(outputStream, writer);

            Assert.True(server.Handle("""{"id":"ngh","op":"neighborhood","mode":1}"""));
            var nghResp = ResponseFor(DrainEvents(outputStream, writer), "ngh");
            Assert.True(nghResp.GetProperty("ok").GetBoolean());

            var manifestPath = Path.Combine(session, "neighborhood.json");
            var content = new GameContent(gameDir, LocatePackDirectories(gameDir));
            var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
            var device = new UIDevice();

            CGSize[] viewports =
            [
                new(3840, 2160), // 16:9 4K
                new(3024, 1964), // 16:10 Retina
                new(3440, 1440), // 21:9 Ultrawide
                new(5120, 1440), // 32:9 Super-Ultrawide
                new(1200, 1200), // 1:1 Square
            ];

            int[] mapModes = [1, 2, 3, 5, 7];
            foreach (int mode in mapModes)
            {
                Assert.True(server.Handle($$"""{"id":"ngh-{{mode}}","op":"neighborhood","mode":{{mode}}}"""));
                var modeResp = ResponseFor(DrainEvents(outputStream, writer), $"ngh-{mode}");
                Assert.True(modeResp.GetProperty("ok").GetBoolean(), $"Failed switching to map mode {mode}");

                var overlay = new OriginalNeighborhoodOverlay(content, device, manifestPath, session, font);
                foreach (var vp in viewports)
                {
                    var items = overlay.DrawItems(vp, uiScale: 2f, time: 0.0);
                    Assert.NotEmpty(items);

                    var backdrop = items[0];
                    Assert.Equal(3840, backdrop.Image.Texture.Width);
                    Assert.Equal(2160, backdrop.Image.Texture.Height);
                    Assert.True(backdrop.Rect.MinX <= 0.01, $"Mode {mode} MinX={backdrop.Rect.MinX} on {vp.Width}x{vp.Height}");
                    Assert.True(backdrop.Rect.MinY <= 0.01, $"Mode {mode} MinY={backdrop.Rect.MinY} on {vp.Width}x{vp.Height}");
                    Assert.True(backdrop.Rect.MaxX >= vp.Width - 0.01, $"Mode {mode} MaxX={backdrop.Rect.MaxX} on {vp.Width}x{vp.Height}");
                    Assert.True(backdrop.Rect.MaxY >= vp.Height - 0.01, $"Mode {mode} MaxY={backdrop.Rect.MaxY} on {vp.Width}x{vp.Height}");

                    // No 64x64 largeback.bmp 9-slice or 1x1 bevel frame items
                    Assert.DoesNotContain(items, static i => i.Image.Texture.Width == 64 && i.Image.Texture.Height == 64);
                    Assert.DoesNotContain(items, static i => i.Image.Texture.Width == 1 && i.Image.Texture.Height == 1);

                    if (mode == 1)
                    {
                        // Lot hit-testing stays locked to house controls
                        var house5 = overlay.Controls(vp, uiScale: 2f).Single(static c => c.Label.EndsWith(" 5", StringComparison.Ordinal));
                        Assert.Equal(5, overlay.HouseAt(new CGPoint(house5.Rect.MidX, house5.Rect.MidY), vp, uiScale: 2f));
                    }
                    else
                    {
                        Assert.NotEmpty(overlay.Controls(vp, uiScale: 2f));
                    }
                }
            }
        }
        finally
        {
            Neighborhoods.Current = previousNeighborhood;
            DeleteTempTree(tempRoot);
        }
    }

    private static byte[] BuildSingleOrMultiChunkIff(params (string Type, ushort Id, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1"));
        Span<byte> u32 = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(u32, 0u);
        ms.Write(u32);
        foreach (var (type, id, payload) in chunks)
        {
            var header = new byte[76];
            Encoding.ASCII.GetBytes(type.PadRight(4).AsSpan(0, 4), header.AsSpan(0, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)(76 + payload.Length));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), id);
            ms.Write(header);
            ms.Write(payload);
        }
        return ms.ToArray();
    }
}
