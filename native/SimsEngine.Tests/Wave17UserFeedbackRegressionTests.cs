using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL.Client;
using SimsSDL.Core;
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
        Assert.Equal(1600.0 / 600.0, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(2560, 1600), 2.0f), 4);
        Assert.Equal(1.8, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(1920, 1080), 1.0f), 4);
        Assert.Equal(0.5, OriginalUIScalingLayout.FullscreenOverlayScale(new CGSize(400, 300), 1.0f), 4);
    }
}
