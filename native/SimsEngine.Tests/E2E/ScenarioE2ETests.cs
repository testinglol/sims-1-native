using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using SimLotCamera = global::LotCamera;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Tier", "Tier4")]
[Trait("Feature", "Multi")]
[Trait("Area", "Scenarios")]
public sealed class ScenarioE2ETests
{
    private sealed class DummyTreeStack : TreeStack
    {
        protected override PrimitiveResult RunPrimitive(StackFrame frame, BhavInstruction instruction) =>
            PrimitiveResult.False;
    }

    private static bool IsHousehold(XPerson person) =>
        person[PersonData.FamilyNumber] > 0 && person.TypeRecord is not null;

    private static bool IsChild(XPerson person) =>
        person[PersonData.PersonAge] < 18;

    private static short[] DefaultPersonality() => [500, 500, 500, 500, 500];

    private static int ReadFamilyVacationField(string userData, ushort familyChunkId)
    {
        var ngh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
        var chunk = ngh.Find(ResourceType.Parse("FAMI"), familyChunkId)!;
        return Family.Read(ngh, chunk).Field0x13C;
    }

    private static string CreateIsolatedGameDirWithDownloads(
        string sourceGameDir,
        Action<string> populateDownloads,
        out string tempRoot)
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-t4-game-" + Guid.NewGuid().ToString("N"));
        var gameCopy = Path.Combine(tempRoot, "game");
        Directory.CreateDirectory(gameCopy);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceGameDir))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "Downloads", StringComparison.OrdinalIgnoreCase))
                continue;

            var dst = Path.Combine(gameCopy, name);
            if (Directory.Exists(entry))
            {
                try
                {
                    Directory.CreateSymbolicLink(dst, entry);
                }
                catch
                {
                    CopyDirRecursive(entry, dst);
                }
            }
            else if (File.Exists(entry))
            {
                File.Copy(entry, dst, overwrite: true);
            }
        }

        var downloads = Path.Combine(gameCopy, "Downloads");
        Directory.CreateDirectory(downloads);
        var sourceDownloads = Path.Combine(sourceGameDir, "Downloads");
        if (Directory.Exists(sourceDownloads))
        {
            foreach (var dlEntry in Directory.EnumerateFileSystemEntries(sourceDownloads))
            {
                var dlDst = Path.Combine(downloads, Path.GetFileName(dlEntry));
                if (Directory.Exists(dlEntry))
                {
                    try { Directory.CreateSymbolicLink(dlDst, dlEntry); }
                    catch { CopyDirRecursive(dlEntry, dlDst); }
                }
                else if (File.Exists(dlEntry))
                {
                    try { File.CreateSymbolicLink(dlDst, dlEntry); }
                    catch { File.Copy(dlEntry, dlDst, overwrite: true); }
                }
            }
        }
        populateDownloads(downloads);
        return gameCopy;
    }

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirRecursive(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    private static void WriteSyntheticSkinPack(
        string folder,
        string bmpFileName,
        string cmxFileName,
        string sknFileName,
        string suitName,
        string meshName,
        string textureName)
    {
        Directory.CreateDirectory(folder);

        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 180;
            pixels[i + 1] = 120;
            pixels[i + 2] = 90;
            pixels[i + 3] = 255;
        }
        File.WriteAllBytes(Path.Combine(folder, bmpFileName), BitmapCodec.Write(4, 4, pixels));

        var skn = new StringBuilder();
        skn.AppendLine(meshName);
        skn.AppendLine(textureName);
        skn.AppendLine("1");
        skn.AppendLine("PELVIS");
        skn.AppendLine("1");
        skn.AppendLine("0 1 2");
        skn.AppendLine("1");
        skn.AppendLine("0 0 3 0 0");
        skn.AppendLine("3");
        skn.AppendLine("0 0");
        skn.AppendLine("1 0");
        skn.AppendLine("0 1");
        skn.AppendLine("0");
        skn.AppendLine("3");
        skn.AppendLine("0 0 0 0 0 1");
        skn.AppendLine("1 0 0 0 0 1");
        skn.AppendLine("0 1 0 0 0 1");
        File.WriteAllText(Path.Combine(folder, sknFileName), skn.ToString(), Encoding.Latin1);

        var cmx = new StringBuilder();
        cmx.AppendLine("// Character File");
        cmx.AppendLine("version 300");
        cmx.AppendLine("0");
        cmx.AppendLine("1");
        cmx.AppendLine(suitName);
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        cmx.AppendLine("1");
        cmx.AppendLine("BODY");
        cmx.AppendLine(meshName);
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        cmx.AppendLine("0");
        File.WriteAllText(Path.Combine(folder, cmxFileName), cmx.ToString(), Encoding.Latin1);
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

    private static (int X, int Y) FindFlatEmptyRegion(string userData, short house, SimulationWorld world, int span)
    {
        var lotIff = IffFile.Open(Path.Combine(userData, "Houses", $"House{house:D2}.iff"));
        var terrain = LotTerrain.Read(lotIff);
        var walls = LotWalls.Read(lotIff, 1);
        var floors = LotFloors.Read(lotIff, 1);
        var tiles = LotTiles.Read(lotIff);
        var pool = LotPool.Read(lotIff);
        var water = LotWater.Read(lotIff);
        var roomMap = world.LotRoomMaps.GetValueOrDefault(1);

        for (int y = 8; y <= 56 - span; y++)
        {
            for (int x = 8; x <= 56 - span; x++)
            {
                var refH = terrain.ReferenceHeight(x, y);
                bool ok = true;
                for (int dy = 0; dy < span && ok; dy++)
                {
                    for (int dx = 0; dx < span; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (!tiles.InsideLot(nx, ny) ||
                            world.TileValueAt(nx, ny, 1) != 0 ||
                            pool.IsPool(nx, ny, 1) ||
                            water.IsWater(nx, ny, 1) ||
                            (roomMap is not null && !roomMap.IsOutside(roomMap.RoomAt(nx, ny))) ||
                            walls[nx, ny][0] != 0 ||
                            floors[nx, ny] != 0 ||
                            terrain.ReferenceHeight(nx, ny) != refH)
                        {
                            ok = false;
                            break;
                        }
                    }
                }
                if (ok)
                    return (x, y);
            }
        }

        throw new InvalidOperationException($"Unable to find {span}x{span} flat empty region on House{house:D2}.");
    }

    private static bool TryBuyObjectInRegion(
        ServerSession server,
        uint guid,
        int startX,
        int startY,
        int width,
        int height,
        int level,
        out short objectId)
    {
        byte[] dirs = [1, 4, 16, 64];
        for (int dy = 0; dy < height; dy++)
        {
            for (int dx = 0; dx < width; dx++)
            {
                int wx = (startX + dx) * 16 + 8;
                int wy = (startY + dy) * 16 + 8;
                foreach (byte dir in dirs)
                {
                    DismissBlockingDialogs(server);
                    var resp = server.HandleJson("buyObject", new
                    {
                        guid,
                        x = wx,
                        y = wy,
                        level,
                        direction = dir
                    });
                    if (resp.GetProperty("ok").GetBoolean())
                    {
                        DismissBlockingDialogs(server);
                        objectId = resp.GetProperty("result").GetProperty("objectID").GetInt16();
                        return true;
                    }
                }
            }
        }

        objectId = 0;
        return false;
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
    // T4-01: New Family Life Cycle (CAS -> Move-In -> Furnish -> Skill/Career -> Downtown Date -> Save/Reload)
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_01_NewFamilyLifeCycle_CasMoveInFurnishSkillCareerDowntownDateSaveReload()
    {
        if (!E2EHarness.TryLocateGameDir(out var baseGameDir) || !E2EHarness.HasHouses(baseGameDir, 9, 21))
            return;

        string? tempGameRoot = null;
        try
        {
            var customGameDir = CreateIsolatedGameDirWithDownloads(baseGameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "LifeCycleSkin"),
                    "b901fafitlgt_t401.bmp",
                    "b901fafit_t401.cmx",
                    "xskin-b901fafit_t401-PELVIS-BODY.skn",
                    "b901fafit_t401",
                    "xskin-b901fafit_t401-PELVIS-BODY",
                    "b901fafitlgt_t401");
            }, out tempGameRoot);

            var skinSearch = CharacterSearchPath.Skins(customGameDir);
            Assert.NotNull(skinSearch.Find("b901fafit_t401.cmx"));

            var catalog = HouseholdAppearance.Catalog(customGameDir, new ObjectRegistry(), "f", "adult", "lgt");
            var customBody = catalog.Bodies.FirstOrDefault(b => b.Id.Contains("b901fafit_t401", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(customBody);
            var head = catalog.Heads[0];

            using var profile = E2EHarness.CreateIsolatedProfile(customGameDir, 9, 21);
            using var server = E2EHarness.StartServer(profile, house: 0);

            Assert.True(server.HandleJson("warmup").GetProperty("ok").GetBoolean());

            int[] sim1Personality = [700, 300, 200, 300, 1000]; // sums to 2500 (25 points)
            int[] sim2Personality = [400, 700, 600, 500, 300];
            var createResp = server.HandleJson("createHousehold", new
            {
                familyName = "LifeCycle",
                moveIn = true,
                house = (short)9,
                members = new[]
                {
                    new
                    {
                        name = "Clara",
                        bio = "Starter Sim",
                        code = "fafit",
                        skin = "lgt",
                        headChoice = head.Id,
                        bodyChoice = customBody!.Id,
                        personality = sim1Personality
                    },
                    new
                    {
                        name = "Arthur",
                        bio = "Partner Sim",
                        code = "mafit",
                        skin = "med",
                        headChoice = "",
                        bodyChoice = "",
                        personality = sim2Personality
                    }
                }
            });
            Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
            var createResult = createResp.GetProperty("result");
            Assert.Equal(9, createResult.GetProperty("house").GetInt32());
            Assert.Equal(16500, createResult.GetProperty("budget").GetInt32()); // $20,000 - $3,500 House09

            var state0 = server.State();
            var simStates = state0.GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
            Assert.Equal(2, simStates.Length);
            Assert.InRange(simStates[0].GetProperty("zodiac").GetInt32(), 1, 12);

            // Enable audio and verify Build/Buy playlists advance sequentially (IsLoop == false)
            Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
            var buildMusic = server.HandleJson("modeMusic", new { mode = "build" });
            Assert.Equal("bkground_build", buildMusic.GetProperty("result").GetProperty("name").GetString());
            Assert.True(buildMusic.GetProperty("result").GetProperty("played").GetBoolean());

            var buyMusic = server.HandleJson("modeMusic", new { mode = "buy" });
            Assert.Equal("bkground_buy1", buyMusic.GetProperty("result").GetProperty("name").GetString());
            var buyPlay = server.LastEvents.First(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.GetProperty("group").GetString() == "MUSIC");
            Assert.False(buyPlay.GetProperty("loop").GetBoolean());
            var buyFile1 = buyPlay.GetProperty("file").GetString();
            Assert.True(server.HandleJson("audioEnded", new { voice = buyPlay.GetProperty("voice").GetInt64() }).GetProperty("ok").GetBoolean());
            var buyPlay2 = server.LastEvents.First(e =>
                e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
                e.TryGetProperty("command", out var c) && c.GetString() == "play" &&
                e.GetProperty("group").GetString() == "MUSIC");
            Assert.NotEqual(buyFile1, buyPlay2.GetProperty("file").GetString());

            // Enter Build/Buy editing, build starter room, and buy real furniture GUIDs
            Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("view", new { editing = true }).GetProperty("ok").GetBoolean());
            DismissBlockingDialogs(server);

            var (rx, ry) = FindFlatEmptyRegion(profile.UserData, 9, server.World!, span: 10);
            Assert.True(server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = rx, y = ry, x2 = 6, y2 = 6, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = rx, y = ry, x2 = 6, y2 = 6, pattern = 1 }).GetProperty("ok").GetBoolean());

            Assert.True(TryBuyObjectInRegion(server, 0x675C18AFu, rx, ry, 6, 6, 1, out _)); // Fridge - Cheap
            Assert.True(TryBuyObjectInRegion(server, 0xEB686709u, rx, ry, 6, 6, 1, out _)); // Stove - Cheap
            Assert.True(TryBuyObjectInRegion(server, 0x9B3A5CDFu, rx, ry, 6, 6, 1, out var bookshelfId)); // Bookshelf - Cheap
            Assert.True(TryBuyObjectInRegion(server, 0x3089D8D2u, rx, ry, 6, 6, 1, out _)); // Bed - Double - Cheap
            Assert.True(TryBuyObjectInRegion(server, 0x6B264238u, rx, ry, 6, 6, 1, out _)); // Phone - Wall
            var newspaperObj = server.World!.ObjectFactory?.Invoke(unchecked((int)0xE6B26801u));
            Assert.NotNull(newspaperObj);
            server.World!.Add(newspaperObj!);
            server.World!.InitializeCreatedObject(newspaperObj);
            server.World!.PlaceObject(newspaperObj, new ObjectPosition((rx + 7) * 16 + 8, (ry + 1) * 16 + 8, 1));
            var newspaperId = newspaperObj.Id;
            Assert.True(bookshelfId > 0 && newspaperId > 0);

            // Switch back to Live mode, verify Clara's skin, skill up + golden overhead + sting_char_winner, wages +$, GenericSimsCall 0x00/0x08/0x09
            Assert.True(server.HandleJson("view", new { editing = false }).GetProperty("ok").GetBoolean());
            var world = server.World!;
            var householdSims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
            Assert.Equal(2, householdSims.Length);
            var clara = householdSims[0];
            var arthur = householdSims[1];
            var claraGuid = clara.TypeRecord!.Key;
            var arthurGuid = arthur.TypeRecord!.Key;
            var familyNum = clara[PersonData.FamilyNumber];

            var playedSounds = new List<string>();
            var prevSound = world.PlaySoundEvent;
            world.PlaySoundEvent = (cue, id, loop, auto) =>
            {
                playedSounds.Add(cue);
                return prevSound?.Invoke(cue, id, loop, auto) ?? true;
            };

            // Skill up + golden overhead + sting_char_winner
            clara[PersonData.CookingHouseBrokenSkill] = 100;
            clara[PersonData.MoneyAmountOverHead] = -10001;
            server.Step(1);
            Assert.Contains("sting_char_winner", playedSounds, StringComparer.OrdinalIgnoreCase);

            // Wage gain +$250
            world.FamilyBudget!.AlterFamilyBudget(0, -250);
            clara[PersonData.MoneyAmountOverHead] = 250;
            server.Step(1);

            var (skillScene, _) = LotScene.Load(server.LatestScenePath());
            skillScene.Validate();
            Assert.Contains(skillScene.OverheadFeedback ?? [], f => f.Kind == "skillUp");
            Assert.Contains(skillScene.OverheadFeedback ?? [], f => f.Kind == "currency" && f.Text == "+$250");

            // Production GenericSimsCall 0x00, 0x08, 0x09 on live lot
            var gscCtx = new TreeContext(world, clara, new StackFrame(clara.Id, new short[8], [0, 1]));
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(gscCtx, [0x00, 0, 0, 0, 0, 0, 0, 0]));
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(gscCtx, [0x08, 0, 0, 0, 0, 0, 0, 0]));
            Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(gscCtx, [0x09, 0, 0, 0, 0, 0, 0, 0]));

            // Clara travels solo Downtown (House21); Arthur remains restricted/home
            world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, claraGuid, familyNum);
            server.BatchRun(1);
            server.DrainEvents();

            var awayWorld = server.World!;
            Assert.Equal(21, server.State().GetProperty("house").GetInt32());
            Assert.Equal([claraGuid], awayWorld.ActivePersonRecordKeys);
            Assert.DoesNotContain(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == arthurGuid);

            var awayClara = awayWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == claraGuid);
            if (awayClara.Position.X < 0 || awayClara.Position.Y < 0)
            {
                var (dx, dy) = FindFlatEmptyRegion(profile.UserData, 21, awayWorld, span: 2);
                awayWorld.PlaceObject(awayClara, new ObjectPosition(dx * 16 + 8, dy * 16 + 8, 1));
            }
            awayWorld.FamilyBudget!.AlterFamilyBudget(0, 50); // Spend $50 on date
            awayClara[PersonData.MoneyAmountOverHead] = -50;
            awayWorld.NeighborInventory(awayClara[PersonData.NeighborId]).Add(7, unchecked((int)0xBEEF0001), 1);

            // Build positive ++ relationship with a Downtown visitor/neighbor if present
            var datePartner = awayWorld.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.Id != awayClara.Id && p[PersonData.NeighborId] > 0);
            if (datePartner is not null)
            {
                var relFrame = new StackFrame(awayClara.Id, new short[8], [0, 1]) { StackObject = datePartner.Id };
                awayClara.Temporaries[0] = 45;
                Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(
                    new TreeContext(awayWorld, awayClara, relFrame),
                    [0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0]));
            }
            server.Step(1);
            var (awayScene, _) = LotScene.Load(server.LatestScenePath());
            awayScene.Validate();
            Assert.Contains(awayScene.OverheadFeedback ?? [], f => f.Kind == "currency" && f.Text == "-$50");

            // Save while Downtown -> close -> reload -> verify Arthur still excluded
            Assert.True(server.HandleJson("save", new { slot = "t4-01-dt", lot = false }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("load", new { slot = "t4-01-dt" }).GetProperty("ok").GetBoolean());
            Assert.Equal(21, server.State().GetProperty("house").GetInt32());
            Assert.Equal([claraGuid], server.World!.ActivePersonRecordKeys);
            Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == arthurGuid);

            // Return home -> verify SuspendedHome, budget, inventory, both Sims restored -> save home -> reload
            var expectedBudgetAfterDate = server.World.FamilyBudget!.ReadFamilyBudget();
            server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, claraGuid, familyNum);
            server.BatchRun(1);
            server.DrainEvents();

            Assert.Equal(9, server.State().GetProperty("house").GetInt32());
            Assert.False(server.State().GetProperty("away").GetBoolean());
            var returnedClara = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == claraGuid);
            var returnedArthur = server.World.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == arthurGuid);
            Assert.NotNull(returnedArthur);
            Assert.Equal((short)100, returnedClara[PersonData.CookingHouseBrokenSkill]);
            Assert.Equal(expectedBudgetAfterDate, server.World.FamilyBudget!.ReadFamilyBudget());
            Assert.Equal(1, server.World.NeighborInventory(returnedClara[PersonData.NeighborId]).Entries
                .Where(e => e.Key1 == 7 && e.Key2 == unchecked((int)0xBEEF0001)).Sum(e => e.Count));

            Assert.True(server.HandleJson("save", new { slot = "t4-01-home", lot = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("load", new { slot = "t4-01-home" }).GetProperty("ok").GetBoolean());
            Assert.Equal(2, server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean()));
        }
        finally
        {
            if (tempGameRoot is not null) E2EHarness.DeleteTempTree(tempGameRoot);
        }
    }

    // -------------------------------------------------------------------------
    // T4-02: Vacation Island Hotel Stay & Unsaved Exit Recovery (FAMI.Field0x13C)
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_02_VacationIslandHotelStayAndUnsavedExitRecovery()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 42))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 42);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        var homeWorld = server.World!;
        var homeBudget = server.State().GetProperty("budget").GetInt32();
        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNumber = traveller[PersonData.FamilyNumber];
        var familyChunkId = ((FamilyBudget)homeWorld.FamilyBudget!).FamilyChunkId!.Value;

        // First test unsaved trip exit: depart to House42 without saving -> Field0x13C on disk must stay 0
        homeWorld.RunGenericSimsCall11Native(traveller, 42, 14, 0, 1);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(42, server.State().GetProperty("house").GetInt32());
        Assert.Equal(5, server.State().GetProperty("homeHouse").GetInt32());
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));

        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));
        Assert.True(server.HandleJson("openLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        // Now send family to snowy Vacation resort House42, verify station_vacation, snow & Front Desk - Lodge (0x1600367F)
        var reopenedTraveller = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        server.World.RunGenericSimsCall11Native(reopenedTraveller, 42, 14, 0, 1);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(42, server.State().GetProperty("house").GetInt32());
        Assert.True(server.State().GetProperty("away").GetBoolean());
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0x1600367Fu); // Front Desk - Lodge

        var vacMusic = server.HandleJson("modeMusic", new { mode = "buy" });
        Assert.Equal("station_vacation", vacMusic.GetProperty("result").GetProperty("name").GetString());
        Assert.True(vacMusic.GetProperty("result").GetProperty("played").GetBoolean());

        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (snowScene, _) = LotScene.Load(server.LatestScenePath());
        snowScene.Validate();
        Assert.Contains(snowScene.TerrainVertices!, v => v.R >= 200 && v.B >= 230);

        // Verify footstep_snow cue resolves on House42 snow tile
        var vacWorld = server.World!;
        var vacActor = vacWorld.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        var emittedCues = new List<string>();
        var prevPlay = vacWorld.PlaySoundEvent;
        vacWorld.PlaySoundEvent = (cue, owner, pan, zoom) => { emittedCues.Add(cue); return prevPlay?.Invoke(cue, owner, pan, zoom) ?? true; };
        var house42Iff = IffFile.Open(Path.Combine(profile.UserData, "Houses", "House42.iff"));
        var floors42 = LotFloors.Read(house42Iff, 1);
        var pool42 = LotPool.Read(house42Iff);
        var water42 = LotWater.Read(house42Iff);
        var snowTile = Enumerable.Range(4, 52).SelectMany(y => Enumerable.Range(4, 52).Select(x => (x, y)))
            .First(t => floors42[t.x, t.y] == 0 && !pool42.IsPool(t.x, t.y, 1) && !water42.IsWater(t.x, t.y, 1));
        vacActor[PersonData.CurrentOutfit] = 0;
        vacWorld.SetObjectPosition(vacActor, new ObjectPosition((short)(snowTile.x * 16 + 8), (short)(snowTile.y * 16 + 8), 1));
        var stepClip = new AvatarAnimationClip("t402-snow-step", 10, [], [new AvatarAnimationSoundEvent(0, "", "footstep", "1", false, false)], DurationMilliseconds: 333f);
        vacActor.AnimationPlayback = new AvatarAnimationPlayback(stepClip, false, false, vacActor.AnimatorMilliseconds);
        vacWorld.TickAvatarAnimations(vacActor);
        Assert.Contains("footstep_snow", emittedCues);

        // Deduct hotel stay fee, advance past midnight, commit save (sets FAMI.Field0x13C = 42)
        vacWorld.FamilyBudget!.AlterFamilyBudget(0, 180);
        vacWorld.Clock.Restore(23, 59, 0);
        server.Step(60);

        var saveResp = server.HandleJson("save", new { slot = "t4-02-vac", lot = true });
        Assert.True(saveResp.GetProperty("ok").GetBoolean(), saveResp.ToString());
        Assert.Equal(42, ReadFamilyVacationField(profile.UserData, familyChunkId));
        var envPath = saveResp.GetProperty("result").GetProperty("path").GetString()!;
        using (var doc = JsonDocument.Parse(File.ReadAllText(envPath)))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("suspendedHome").GetProperty("homeHouse").GetInt32());
        }

        // Close without saving and reopen House05 -> redirects cleanly to House42 with homeHouse == 5
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        var reopenResp = server.HandleJson("openLot", new { house = 5 });
        Assert.True(reopenResp.GetProperty("ok").GetBoolean());
        Assert.Equal(42, reopenResp.GetProperty("result").GetProperty("house").GetInt32());
        Assert.Equal(5, reopenResp.GetProperty("result").GetProperty("homeHouse").GetInt32());

        // Check out & return home -> save home -> FAMI.Field0x13C clears to 0
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNumber);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
        Assert.False(server.State().GetProperty("away").GetBoolean());
        Assert.Equal(homeBudget - 180, server.State().GetProperty("budget").GetInt32());

        Assert.True(server.HandleJson("save", new { slot = "t4-02-home", lot = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));
    }

    // -------------------------------------------------------------------------
    // T4-03: Studio Town Rise to Stardom (Tabloid, Makeover, Fame Stars, Limo, Award)
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_03_StudioTownRiseToStardom_TabloidMakeoverFameLimoAndAward()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 82, 84, 86))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 82, 84, 86);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);

        var world = server.World!;
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var bellaGuid = bella.TypeRecord!.Key;
        var famNum = bella[PersonData.FamilyNumber];

        // 1. Place Studio Town Tabloid (0x7AFDD7E3, delivered object, not in Buy Mode catalog) and inspect menu interactions
        var (fx, fy) = FindFlatEmptyRegion(profile.UserData, 5, world, span: 5);
        var tabloidObj = world.ObjectFactory?.Invoke(unchecked((int)0x7AFDD7E3u));
        Assert.NotNull(tabloidObj);
        world.Add(tabloidObj!);
        world.InitializeCreatedObject(tabloidObj);
        world.PlaceObject(tabloidObj, new ObjectPosition(fx * 16 + 8, fy * 16 + 8, 1));
        var tabloidId = tabloidObj.Id;
        var tabloidMenu = server.HandleJson("menu", new { objectID = tabloidId });
        Assert.True(tabloidMenu.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(tabloidMenu.GetProperty("result").EnumerateArray().ToArray());

        // 2. Makeover / High-Fashion wardrobe change refreshes SimPortrait
        var entry = world.ObjectRegistry!.Find(unchecked((uint)bellaGuid))!;
        var beforePortrait = entry.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
        var scope = InteractionBehaviorScope.Initialize();
        var ctx = WardrobeSuitService.BeginPersonScope(world, bella, scope);
        var highFashionList = WardrobeSuitService.GetActionList(world, bella, ctx, 5);
        if (highFashionList is { Entries.Count: > 0 })
        {
            ctx.SetString(0x23, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(highFashionList.Entries[0]));
        }
        var headsList = WardrobeSuitService.GetActionList(world, bella, ctx, 0, headsList: true);
        if (headsList is { Entries.Count: > 1 })
        {
            var currentHead = bella.SavedCharacter?.Parts[4].Item1 ?? string.Empty;
            var altHead = headsList.Entries.FirstOrDefault(e =>
                !e.First.Equals(currentHead, StringComparison.OrdinalIgnoreCase));
            var chosenHead = altHead ?? headsList.Entries[1];
            ctx.SetString(0x03, $"{chosenHead.First},HEAD={chosenHead.First}");
        }
        WardrobeSuitService.CommitContext(world.ObjectRegistry, entry, world.Language, ctx, bella);
        Assert.True(server.HandleJson("save", new { slot = "t4-03-makeover", lot = true }).GetProperty("ok").GetBoolean());
        var afterPortrait = world.ObjectRegistry.Find(unchecked((uint)bellaGuid))!.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
        Assert.NotEmpty(beforePortrait);
        Assert.NotEmpty(afterPortrait);
        Assert.False(beforePortrait.SequenceEqual(afterPortrait), "Makeover must regenerate SimPortrait BMP_ 2002.");

        // 3. Enroll Superstar career & earn initial fame stars -> travel to Studio Town lots (84, 82, 86)
        bella[PersonData.JobType] = 0;
        bella[PersonData.FameScore] = 100;
        bella[PersonData.CharismaObedienceSkill] = 700;
        bella[PersonData.BodyFurnitureSkill] = 700;
        bella[PersonData.CreativitySkill] = 700;
        world.SavePersonRecord(bella);
        Assert.True(world.EvaluateFamePromotion(bella.Id));

        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(84, bellaGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(84, server.State().GetProperty("house").GetInt32());
        Assert.True(server.State().GetProperty("away").GetBoolean());
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0x68DBDE65u); // KaraokeStage

        var studioMusic = server.HandleJson("modeMusic", new { mode = "buy" });
        Assert.Equal("station_superstar", studioMusic.GetProperty("result").GetProperty("name").GetString());
        Assert.True(studioMusic.GetProperty("result").GetProperty("played").GetBoolean());

        // Hop to House82 (MovieSet 0x6D5C1770) & perform LookTowards + fame gain
        var studioBella = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == bellaGuid);
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(82, bellaGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(82, server.State().GetProperty("house").GetInt32());
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0x6D5C1770u); // MovieSet

        studioBella = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == bellaGuid);
        studioBella[PersonData.FameScore] = 350;
        server.World.EvaluateFamePromotion(studioBella.Id);
        studioBella[PersonData.MoneyAmountOverHead] = 50;
        server.Step(1);

        // Return home, place Star Award (0x71E7DB5C), save & reload -> verify Fame + Award persist
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, bellaGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var (ax, ay) = FindFlatEmptyRegion(profile.UserData, 5, server.World!, span: 4);
        var awardWorld = server.World!;
        var awardObj = awardWorld.ObjectFactory?.Invoke(unchecked((int)0x71E7DB5Cu));
        Assert.NotNull(awardObj);
        awardWorld.Add(awardObj!);
        awardWorld.InitializeCreatedObject(awardObj);
        var awardPos = new ObjectPosition((short)(ax * 16 + 8), (short)(ay * 16 + 8), 1);
        Assert.True(awardWorld.CanPlaceObject(awardObj, awardPos));
        awardWorld.PlaceObject(awardObj, awardPos);

        Assert.True(server.HandleJson("save", new { slot = "t4-03-stardom", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t4-03-stardom" }).GetProperty("ok").GetBoolean());

        var reloadedBella = server.State().GetProperty("sims").EnumerateArray()
            .First(s => unchecked((int)s.GetProperty("guid").GetUInt32()) == bellaGuid);
        Assert.True(reloadedBella.GetProperty("fameScore").GetInt32() >= 350);
        Assert.True(reloadedBella.GetProperty("fame").GetProperty("level").GetInt32() >= 1);
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0x71E7DB5Cu);
    }

    // -------------------------------------------------------------------------
    // T4-04: Magic Town Spellcraft, MagiCoins & Lot 90–92 Move-In
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_04_MagicTownSpellcraftMagiCoinsAndLot90To92MoveIn()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 90, 91, 92, 93))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 90, 91, 92, 93);

        // 1. Verify MagiCoin move-in gate on Magic Town residential lot 91 (< 150 MagiCoins blocked)
        var unhoused = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1);
        Assert.NotEmpty(unhoused);
        var nghGatePath = Path.Combine(profile.UserData, "Neighborhood.iff");
        var nghGateIff = IffFile.Open(nghGatePath);
        var ngbhGateChunk = nghGateIff.Find(ResourceType.Parse("NGBH"), 1)!;
        var ngbhGateData = NeighborhoodData.Read(nghGateIff, ngbhGateChunk);
        var nbrsGate = Neighbors.ReadRecoveringSupportedPrefix(nghGateIff, nghGateIff.OfType(ResourceType.Parse("NBRS")).First()).Entries;
        var famiGate = Family.Read(nghGateIff, nghGateIff.Find(ResourceType.Parse("FAMI"), (ushort)unhoused[0].Id)!);
        var firstNbrGate = nbrsGate.First(n => famiGate.Members.Contains(n.Guid));
        var gateInv = ngbhGateData.Inventories.ToDictionary(kv => kv.Key, kv => kv.Value);
        gateInv[firstNbrGate.NeighborId] = [new NeighborhoodData.InventoryItem(7, NeighborhoodExport.MagiCoinTokenGuid, 50)];
        var gateSwap = ngbhGateChunk.Data.Length > 0 && ngbhGateChunk.Data.Span[0] != 0;
        var gateBytes = NeighborhoodData.Write(ngbhGateData.Data, gateInv, Math.Max(ngbhGateData.Version, 0x49), gateSwap, ngbhGateData.TrailingBytes.Span);
        nghGateIff = nghGateIff.WithChunkDataResized(ngbhGateChunk.Type, ngbhGateChunk.Id, gateBytes);
        File.WriteAllBytes(nghGatePath, nghGateIff.Bytes.ToArray());
        var blockedEx = Assert.Throws<InvalidOperationException>(() =>
            NeighborhoodEdits.MoveIn(profile.UserData, gameDir, 91, unhoused[0].Id));
        Assert.Contains("MagiCoins", blockedEx.Message, StringComparison.OrdinalIgnoreCase);

        using var server = E2EHarness.StartServer(profile, house: 0);
        Assert.True(server.HandleJson("neighborhood", new { mode = 7 }).GetProperty("ok").GetBoolean());
        var overlay7 = new OriginalNeighborhoodOverlay(new GameContent(gameDir), new UIDevice(), Path.Combine(profile.Session, "neighborhood.json"), profile.Session);
        Assert.Contains("MagiCoins", overlay7.RolloverText(90), StringComparison.OrdinalIgnoreCase);

        // 2. Open House05, place Magic Starter Kit (0x197CDB3C) & Spellbook (0x8EC5750D)
        Assert.True(server.HandleJson("openLot", new { house = (short)5 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);

        var world = server.World!;
        var (mx, my) = FindFlatEmptyRegion(profile.UserData, 5, world, span: 5);
        var starterKitObj = world.ObjectFactory?.Invoke(unchecked((int)0x197CDB3Cu));
        Assert.NotNull(starterKitObj);
        world.Add(starterKitObj!);
        world.InitializeCreatedObject(starterKitObj);
        world.PlaceObject(starterKitObj, new ObjectPosition(mx * 16 + 8, my * 16 + 8, 1));
        Assert.True(TryBuyObjectInRegion(server, 0x8EC5750Du, mx, my, 4, 4, 1, out var spellbookId));

        // 3. Test magic_tokens cheat IPC & MagiCoin token persistence
        var caster = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var casterGuid = caster.TypeRecord!.Key;
        var famNum = caster[PersonData.FamilyNumber];

        var magicCheatResp = server.HandleJson("cheat", new { code = "magic_tokens" });
        Assert.True(magicCheatResp.GetProperty("ok").GetBoolean(), magicCheatResp.ToString());

        world.NeighborInventory(caster[PersonData.NeighborId]).Add(7, unchecked((int)NeighborhoodExport.MagiCoinTokenGuid), 175);

        // Cast SpecialEffect (0x23) & trigger magenta overhead (-20005)
        var sfxFrame = new StackFrame(caster.Id, new short[8], [0, 1]) { StackObject = spellbookId };
        var sfxCtx = new TreeContext(world, caster, sfxFrame) { Stack = new DummyTreeStack() };
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(sfxCtx, [2, 0, 1, 0, 0, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.Stop, SpecialEffectPrimitive.Execute(sfxCtx, [2, 0, 1, 0, 0, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.True, SpecialEffectPrimitive.Execute(sfxCtx, [2, 0, 1, 0, 0, 0, 0, 0]));
        Assert.NotEmpty(world.HeadlessSpecialEffects.Dispatches);

        caster[PersonData.MoneyAmountOverHead] = -20005;
        server.Step(1);
        var (magicScene, _) = LotScene.Load(server.LatestScenePath());
        magicScene.Validate();
        Assert.Contains(magicScene.OverheadFeedback ?? [], f => f.Kind == "magenta");

        // 4. Travel to Magic Town community lot 93 (MagicArena 0xC96515A5) and check music_magictown
        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(93, casterGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(93, server.State().GetProperty("house").GetInt32());
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0xC96515A5u);

        var mtMusic = server.HandleJson("modeMusic", new { mode = "buy" });
        Assert.Equal("music_magictown", mtMusic.GetProperty("result").GetProperty("name").GetString());
        Assert.True(mtMusic.GetProperty("result").GetProperty("played").GetBoolean());

        // Return home, save & reload -> verify MagiCoins & spellbook persist
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, casterGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "t4-04-magic", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t4-04-magic" }).GetProperty("ok").GetBoolean());

        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("guid").GetUInt32() == 0x8EC5750Du);
        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famChunkId = ((FamilyBudget)server.World!.FamilyBudget!).FamilyChunkId!.Value;
        var fam = Family.Read(ngh, ngh.Find(ResourceType.Parse("FAMI"), famChunkId)!);
        var nbrs = Neighbors.Read(ngh, ngh.Find(ResourceType.Parse("NBRS"), 1)!);
        var ngbhChunk = ngh.Find(ResourceType.Parse("NGBH"), 1);
        var ngbhData = ngbhChunk is null ? null : NeighborhoodData.Read(ngh, ngbhChunk);
        Assert.True(NeighborhoodExport.GetMagicoinsForFamily(fam, nbrs.Entries, ngbhData) >= 175);
    }

    // -------------------------------------------------------------------------
    // T4-05: Emergency Gauntlet: Fire, Burglar & Repo Man
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_05_EmergencyGauntlet_FireBurglarAndRepoMan()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);

        var world = server.World!;
        var registry = world.ObjectRegistry!;
        Assert.NotNull(registry.Find(0x14C180BAu)); // Firefighter
        Assert.NotNull(registry.Find(0x7557C98Eu)); // Officer
        Assert.NotNull(registry.Find(0xA82892C3u)); // RepoMan

        var (ex, ey) = FindFlatEmptyRegion(profile.UserData, 5, world, span: 6);
        Assert.True(server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = ex, y = ey, x2 = 4, y2 = 4, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = ex, y = ey, x2 = 4, y2 = 4, pattern = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(TryBuyObjectInRegion(server, 0xEB686709u, ex, ey, 4, 4, 1, out var stoveId));
        Assert.True(TryBuyObjectInRegion(server, 0xEEA7D425u, ex, ey, 4, 4, 1, out var smokeAlarmId));
        Assert.True(TryBuyObjectInRegion(server, 0xAB647747u, ex, ey, 4, 4, 1, out var burglarAlarmId));

        // 1. Fire & smoke alarm + spatial pan/muffle
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        bella[PersonData.CookingHouseBrokenSkill] = 0;
        var stove = world.ObjectById(stoveId)!;
        var burnCtx = new TreeContext(world, stove, new StackFrame(stove.Id, new short[8], [0, 1]) { StackObject = stove.Id });
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(burnCtx, [0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Contains(world.WalkOrder, o => o.TypeRecord?.Key == BurnPrimitive.FireType);

        Assert.True(server.HandleJson("audioEvent", new { name = "fire_burning_small", ownerID = (int)smokeAlarmId }).GetProperty("ok").GetBoolean());
        Assert.Equal(0.25f, PositionalSound.FloorGain(2, 1), 3);

        // 2. Nighttime burglar alarm & critical motive pulsing
        world.Clock.Restore(1, 0, 0);
        Assert.True(server.HandleJson("setMotive", new { objectID = bella.Id, motive = "Energy", value = -85f }).GetProperty("ok").GetBoolean());
        var bar = new OriginalGameplayLayout.MotiveBarState(-85);
        bar.Reset(-85, nowMs: 1000);
        Assert.True(bar.Evaluate(1150).IsAnimating);
        Assert.True(server.HandleJson("audioEvent", new { name = "alarm_burglar", ownerID = (int)burglarAlarmId }).GetProperty("ok").GetBoolean());

        // 3. Upkeep / GenericSimsCall 0x05 + bill deduction + Repo Man object removal & save/reload persistence
        var initialBudget = world.FamilyBudget!.ReadFamilyBudget();
        bella.Temporaries[0] = bella[PersonData.FamilyNumber];
        var billCtx = new TreeContext(world, bella, new StackFrame(bella.Id, new short[8], [0, 1]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(billCtx, [0x05, 0, 0, 0, 0, 0, 0, 0]));
        var budgetAfterValuation = world.FamilyBudget.ReadFamilyBudget();
        Assert.True(budgetAfterValuation > initialBudget);
        Assert.Equal(PrimitiveResult.True, AlterBudgetPrimitive.Execute(billCtx, [0, 0, 150, 0, 0, 0, 2, 0]));
        Assert.Equal(budgetAfterValuation - 150, world.FamilyBudget.ReadFamilyBudget());

        // Extinguish active fire, dismiss blocking dialogs, and repossess target object across save/load
        DismissBlockingDialogs(server);
        foreach (var fire in world.WalkOrder.Where(o => o.TypeRecord?.Key == BurnPrimitive.FireType).ToArray())
            Assert.True(world.Remove(fire.Id));
        Assert.True(world.Remove(stoveId));
        Assert.True(server.HandleJson("save", new { slot = "t4-05-emerg", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t4-05-emerg" }).GetProperty("ok").GetBoolean());
        Assert.Null(server.World!.ObjectById(stoveId));
        Assert.DoesNotContain(server.State().GetProperty("objects").EnumerateArray(),
            o => o.GetProperty("id").GetInt16() == stoveId);
    }

    // -------------------------------------------------------------------------
    // T4-06: Audiovisual Immersion: HIT VM, Playlists, TV & Spatial Mixing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task T4_06_AudiovisualImmersion_HitVmPlaylistsTvAndSpatialMixing()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);

        // 1. 4-channel volume persistence across FrontendAudioServer restart
        await using (var fa1 = await E2EHarness.StartFrontendAudioServerAsync(profile))
        {
            Assert.True((await fa1.SendAsync("audioVolume", new { group = "FX", volume = 0.30f })).GetProperty("ok").GetBoolean());
            Assert.True((await fa1.SendAsync("audioVolume", new { group = "MUSIC", volume = 0.65f })).GetProperty("ok").GetBoolean());
            Assert.True((await fa1.SendAsync("audioVolume", new { group = "VOX", volume = 0.85f })).GetProperty("ok").GetBoolean());
            Assert.True((await fa1.SendAsync("audioVolume", new { group = "AMBIENCE", volume = 0.20f })).GetProperty("ok").GetBoolean());
        }

        await using (var fa2 = await E2EHarness.StartFrontendAudioServerAsync(profile))
        {
            var stateResp = await fa2.SendAsync("audioState");
            Assert.True(stateResp.GetProperty("ok").GetBoolean());
            var vols = stateResp.GetProperty("result").GetProperty("volumes");
            Assert.Equal(0.30f, vols.GetProperty("FX").GetSingle(), 2);
            Assert.Equal(0.65f, vols.GetProperty("MUSIC").GetSingle(), 2);
            Assert.Equal(0.85f, vols.GetProperty("VOX").GetSingle(), 2);
            Assert.Equal(0.20f, vols.GetProperty("AMBIENCE").GetSingle(), 2);
        }

        // 2. Stereo RadioStationAutoPatch, TVStations, bkground_fade & playlist advancement
        using var server = E2EHarness.StartServer(profile, house: 5);
        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);

        var (ax, ay) = FindFlatEmptyRegion(profile.UserData, 5, server.World!, span: 4);
        short stereoId = 0;
        if (!TryBuyObjectInRegion(server, 0x46F4E80Bu, ax, ay, 3, 3, 1, out stereoId))
        {
            var tableEntry = server.World!.ObjectRegistry!.Entries
                .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                            e.Definition[ObjdField.BuildModeType] == 0 &&
                            e.Definition[ObjdField.MasterId] == 0 &&
                            ObjectCatalog.TryGetEntry(e, 1, out var cat) &&
                            cat is { Price: > 0 and <= 300 } &&
                            cat.Name.Contains("End Table", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Definition[ObjdField.Price])
                .FirstOrDefault();
            if (tableEntry is not null && TryBuyObjectInRegion(server, tableEntry.Guid, ax, ay, 2, 2, 1, out _))
                TryBuyObjectInRegion(server, 0x46F4E80Bu, ax, ay, 2, 2, 1, out stereoId);
            if (stereoId == 0)
            {
                foreach (var stEntry in server.World.ObjectRegistry.Entries
                             .Where(e => e.Definition[ObjdField.Disabled] == 0 &&
                                         e.Definition[ObjdField.BuildModeType] == 0 &&
                                         e.Definition[ObjdField.MasterId] == 0 &&
                                         ObjectCatalog.TryGetEntry(e, 1, out var cat) &&
                                         cat is { Price: > 0 and <= 2500 } &&
                                         cat.Name.Contains("Stereo", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.Definition[ObjdField.Price]))
                {
                    if (TryBuyObjectInRegion(server, stEntry.Guid, ax, ay, 3, 3, 1, out stereoId))
                        break;
                }
            }
        }
        Assert.True(stereoId > 0, "Failed to place a Stereo on House05.");
        Assert.True(TryBuyObjectInRegion(server, 0x5FA381C1u, ax + 1, ay + 1, 3, 3, 1, out var tvId));     // Television - Moderate

        Assert.True(server.HandleJson("audioEvent", new { name = "stereo_classical", ownerID = (int)stereoId }).GetProperty("result").GetProperty("played").GetBoolean());
        Assert.True(server.HandleJson("audioEvent", new { name = "stereo_rock", ownerID = (int)stereoId }).GetProperty("result").GetProperty("played").GetBoolean());
        Assert.True(server.HandleJson("audioEvent", new { name = "radio_next_song", ownerID = (int)stereoId }).GetProperty("result").GetProperty("played").GetBoolean());
        Assert.True(server.HandleJson("audioEvent", new { name = "stereo_turnoff", ownerID = (int)stereoId }).GetProperty("result").GetProperty("played").GetBoolean());

        Assert.True(server.HandleJson("audioEvent", new { name = "tv_action_norm", ownerID = (int)tvId }).GetProperty("result").GetProperty("played").GetBoolean());
        Assert.True(server.HandleJson("audioEvent", new { name = "tv_turn_off", ownerID = (int)tvId }).GetProperty("result").GetProperty("played").GetBoolean());

        Assert.True(server.HandleJson("modeMusic", new { mode = "buy" }).GetProperty("result").GetProperty("played").GetBoolean());
        var buyVoice = server.LastEvents.First(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "audio" &&
            e.TryGetProperty("command", out var c) && c.GetString() == "play").GetProperty("voice").GetInt64();
        Assert.True(server.HandleJson("audioEnded", new { voice = buyVoice }).GetProperty("ok").GetBoolean());

        // 3. HIT VM VOX demographics, Freshness_Update rotation, Ducking, PitchScale resampling & Spatial Pan/Muffle
        using var audio = E2EHarness.CreateAudioSession(gameDir);
        Assert.Equal(9, audio.Bank.GetMusicMode("bkground_fade"));
        Assert.True(audio.Runtime.Play("bkground_buy1", -1));
        var bgPlay = Assert.Single(audio.PlayEvents());
        float initialBgVol = bgPlay.GetProperty("volume").GetSingle();
        long bgVoice = bgPlay.GetProperty("voice").GetInt64();

        audio.DrainAudioEvents();
        Assert.True(audio.Runtime.Play("sting_kiss_polite", -1));
        Assert.Contains(audio.UpdateEvents(), u => u.GetProperty("voice").GetInt64() == bgVoice && u.GetProperty("volume").GetSingle() < initialBgVol);

        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 10, true, true, 'f'));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 11, true, true, 'm'));
        Assert.True(audio.Runtime.PlayForSimulationObject("vox_laugh_giggle", 12, true, true, 'k'));

        var footstepFiles = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            audio.DrainAudioEvents();
            Assert.True(audio.Runtime.PlayForSimulationObject("footstep_hard", (short)(30 + i), false, false));
            footstepFiles.Add(Assert.Single(audio.PlayEvents()).GetProperty("file").GetString()!);
        }
        for (int i = 1; i < footstepFiles.Count; i++)
            Assert.NotEqual(footstepFiles[i - 1], footstepFiles[i]);

        // Pitch resampling & spatial muffle
        var mixer = new AudioMixer();
        mixer.SetMuffle(0.5f, 0.2f);
        var pcm = new float[2048 * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = i % 4 == 0 ? 0.5f : -0.5f;
        mixer.Set(1, new AudioMixer.Voice(pcm) { PitchScale = 0.75f, Outdoors = true, Loop = true });
        mixer.Set(2, new AudioMixer.Voice(pcm) { PitchScale = 1.35f, Loop = true });
        mixer.MixAndPut(nint.Zero, 256);

        double posSlow = 0, posFast = 0;
        mixer.Update(1, (ref AudioMixer.Voice v) => posSlow = v.FramePosition);
        mixer.Update(2, (ref AudioMixer.Voice v) => posFast = v.FramePosition);
        Assert.InRange(posSlow, 191.5, 192.5);
        Assert.InRange(posFast, 345.0, 346.0);
        Assert.True(LotZoom.Near.SoundGain() > LotZoom.Medium.SoundGain() && LotZoom.Medium.SoundGain() > LotZoom.Far.SoundGain());
        Assert.Equal(-1f, PositionalSound.Mix(new Vector2(0f, 360f), new Vector2(1280f, 720f), panned: true).Pan, 3);
        Assert.Equal(1f, PositionalSound.Mix(new Vector2(1280f, 360f), new Vector2(1280f, 720f), panned: true).Pan, 3);
    }

    // -------------------------------------------------------------------------
    // T4-07: Cheat Console Power-User Session & World-Space Diagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_07_CheatConsolePowerUserSessionAndWorldSpaceDiagnostics()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var console = new OriginalCheatConsoleOverlay(font, new UIDevice());

        // Empty history '!' check
        console.Open(nowSeconds: 1.0);
        console.TextInput("!");
        Assert.False(console.TryPrepareSegments(out _, out var errTitle, out var errMsg));
        Assert.Equal("Sorry", errTitle);
        Assert.Equal("No previous cheat.", errMsg);

        // Chained repeat 'rosebud;!;!'
        console.Open(nowSeconds: 2.0);
        console.TextInput("rosebud;!;!");
        Assert.True(console.TryPrepareSegments(out var expanded, out _, out _));
        Assert.Equal(["rosebud", "rosebud", "rosebud"], expanded);

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);
        var initialBudget = server.State().GetProperty("budget").GetInt32();

        // Cheat IPC: help, invalid 'foobar', rosebud;!;!, set_hour 22, move_objects on, water_tool
        var helpResp = server.HandleJson("cheat", new { code = "help" });
        Assert.True(helpResp.GetProperty("ok").GetBoolean(), helpResp.ToString());
        Assert.Contains("rosebud", helpResp.ToString() + server.State().GetProperty("dialogs").ToString(), StringComparison.OrdinalIgnoreCase);

        var badResp = server.HandleJson("cheat", new { code = "foobar" });
        var badCombined = badResp.ToString() + server.State().GetProperty("dialogs").ToString();
        Assert.Contains("Sorry", badCombined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such cheat.", badCombined, StringComparison.OrdinalIgnoreCase);

        var rosebudChain = server.HandleJson("cheat", new { code = "rosebud;!;!" });
        Assert.True(rosebudChain.GetProperty("ok").GetBoolean(), rosebudChain.ToString());
        Assert.Equal(initialBudget + 3000, server.State().GetProperty("budget").GetInt32());

        Assert.True(server.HandleJson("cheat", new { code = "set_hour 22" }).GetProperty("ok").GetBoolean());
        Assert.Equal(22, server.State().GetProperty("hours").GetInt32());

        Assert.True(server.HandleJson("cheat", new { code = "move_objects on" }).GetProperty("result").GetProperty("moveObjects").GetBoolean());
        Assert.True(server.HandleJson("cheat", new { code = "water_tool" }).GetProperty("ok").GetBoolean());

        // Move an object while move_objects is active
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var (fx, fy) = FindFlatEmptyRegion(profile.UserData, 5, server.World!, span: 4);
        Assert.True(TryBuyObjectInRegion(server, 0x5FA381C1u, fx, fy, 2, 2, 1, out var propId));
        Assert.True(server.HandleJson("moveObject", new { objectID = propId, x = (fx + 2) * 16 + 8, y = (fy + 2) * 16 + 8, level = 1, direction = (byte)1 }).GetProperty("ok").GetBoolean());

        // Trigger world-space diagnostics: skill-up, currency, ++/--, thought balloon, critical motive red pulse
        var world = server.World!;
        var sims = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).Take(2).ToArray();
        Assert.Equal(2, sims.Length);

        sims[0][PersonData.MoneyAmountOverHead] = -10001;
        server.Step(1);
        sims[0][PersonData.MoneyAmountOverHead] = 500;
        server.Step(1);

        var relFrame = new StackFrame(sims[0].Id, new short[8], [0, 1]) { StackObject = sims[1].Id };
        sims[0].Temporaries[0] = 35;
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(new TreeContext(world, sims[0], relFrame), [0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.True, SetBalloonHeadlinePrimitive.Execute(new TreeContext(world, sims[0], new StackFrame(sims[0].Id, new short[8], [0, 1])), [0, 0, 1, 1, 60, 0, 2, 1]));
        Assert.True(server.HandleJson("setMotive", new { objectID = sims[0].Id, motive = "Energy", value = -85f }).GetProperty("ok").GetBoolean());

        var motiveBar = new OriginalGameplayLayout.MotiveBarState(-85);
        motiveBar.Reset(-85, nowMs: 1000);
        Assert.True(motiveBar.Evaluate(1200).IsAnimating);

        server.Step(1);
        foreach (int rot in new[] { 0, 1, 2, 3 })
        {
            Assert.True(server.HandleJson("cameraOrientation", new { value = rot }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("zoom", new { scale = (rot % 3) + 1 }).GetProperty("ok").GetBoolean());
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            Assert.Contains(scene.OverheadFeedback ?? [], f => f.Kind == "skillUp");
            Assert.Contains(scene.OverheadFeedback ?? [], f => f.Kind == "currency" && f.Text == "+$500");
            Assert.Contains(scene.Sprites, s => s.ObjectID == sims[0].Id && scene.Textures[s.Texture].File.StartsWith("runtime-slot-", StringComparison.Ordinal));
        }
    }

    // -------------------------------------------------------------------------
    // T4-08: Partial Household Solo Travel + Stay-at-Home Integrity
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_08_PartialHouseholdSoloTravelAndStayAtHomeIntegrity()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9, 21);
        using var server = E2EHarness.StartServer(profile, house: 0);

        // Create 3 human Sims (2 Adults + 1 Child) on House09, then adopt 1 Dog -> 4-member household
        Assert.True(server.HandleJson("createHousehold", new
        {
            familyName = "SoloKeep",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Alice", code = "fafit", skin = "lgt", personality = DefaultPersonality() },
                new { name = "Bob", code = "mafit", skin = "med", personality = DefaultPersonality() },
                new { name = "Timmy", code = "mcchd", skin = "lgt", personality = DefaultPersonality() }
            }
        }).GetProperty("ok").GetBoolean());

        var homeWorld = server.World!;
        var adult1 = homeWorld.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var makeFrame = new StackFrame(adult1.Id, new short[8], [0, 1, 0]);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(new TreeContext(homeWorld, adult1, makeFrame), [0, 1, 2, 1, 0, 0, 0, 0]));
        var petNeighborId = makeFrame.StackObject;
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(homeWorld, adult1, new StackFrame(adult1.Id, new short[8], [0, 1]) { StackObject = petNeighborId }),
            [0x04, 0, 0, 0, 0, 0, 0, 0]));
        server.BatchRun(1);
        server.DrainEvents();

        // Switch away and back (like T1_F17_05) so EnsureSelectedHouseholdMember spawns all 4 members including the adopted pet
        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, adult1.TypeRecord!.Key, adult1[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, adult1.TypeRecord!.Key, adult1[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        var allFour = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.Equal(4, allFour.Length);

        var traveller = allFour[0];
        var staySim2 = allFour[1];
        var staySim3 = allFour[2];
        var stayPet = allFour[3];

        staySim2[PersonData.CreativitySkill] = 515;
        staySim2[Motive.Hunger] = 64f;
        staySim3[PersonData.LogicSkill] = 310;
        staySim3[Motive.Energy] = 72f;
        stayPet[PersonData.CharismaObedienceSkill] = 420;
        stayPet[Motive.Social] = 58f;

        var travellerGuid = traveller.TypeRecord!.Key;
        var stayKeys = new[] { staySim2.TypeRecord!.Key, staySim3.TypeRecord!.Key, stayPet.TypeRecord!.Key };
        var famNum = traveller[PersonData.FamilyNumber];

        // Adult 1 travels solo to Downtown 21
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(21, server.State().GetProperty("house").GetInt32());
        Assert.Equal([travellerGuid], server.World!.ActivePersonRecordKeys);
        foreach (var sk in stayKeys)
            Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == sk);

        // Save while away, close, reload -> ONLY Adult 1 is active/selectable
        Assert.True(server.HandleJson("save", new { slot = "t4-08-away", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t4-08-away" }).GetProperty("ok").GetBoolean());

        Assert.Equal([travellerGuid], server.World!.ActivePersonRecordKeys);
        var awaySims = server.State().GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
        Assert.Single(awaySims);
        Assert.Equal(travellerGuid, unchecked((int)awaySims[0].GetProperty("guid").GetUInt32()));

        // Modify traveller state while away & return home -> all 4 members active and stay-at-home states intact
        var awayTraveller = server.World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        awayTraveller[PersonData.CharismaObedienceSkill] = 640;
        server.World.NeighborInventory(awayTraveller[PersonData.NeighborId]).Add(7, unchecked((int)0xCAFE1234), 2);
        server.World.FamilyBudget!.AlterFamilyBudget(0, 75);
        var expectedBudget = server.World.FamilyBudget.ReadFamilyBudget();

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(9, server.State().GetProperty("house").GetInt32());
        Assert.Equal(expectedBudget, server.World!.FamilyBudget!.ReadFamilyBudget());
        var returnedAll = server.World.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.Equal(4, returnedAll.Length);

        var r1 = returnedAll.First(p => p.TypeRecord?.Key == travellerGuid);
        var r2 = returnedAll.First(p => p.TypeRecord?.Key == stayKeys[0]);
        var r3 = returnedAll.First(p => p.TypeRecord?.Key == stayKeys[1]);
        var r4 = returnedAll.First(p => p.TypeRecord?.Key == stayKeys[2]);
        Assert.Equal((short)640, r1[PersonData.CharismaObedienceSkill]);
        Assert.Equal((short)515, r2[PersonData.CreativitySkill]);
        Assert.Equal(64f, r2[Motive.Hunger]);
        Assert.Equal((short)310, r3[PersonData.LogicSkill]);
        Assert.Equal(72f, r3[Motive.Energy]);
        Assert.Equal((short)420, r4[PersonData.CharismaObedienceSkill]);
        Assert.Equal(58f, r4[Motive.Social]);
    }

    // -------------------------------------------------------------------------
    // T4-09: Animation Fidelity: Blending (Last), XEVTs, Carry & Head Tracking
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_09_AnimationFidelity_BlendingLastXevtsCarryAndHeadTracking()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var bella = Assert.IsType<XPerson>(world.ObjectById(18));
        var mortimer = Assert.IsType<XPerson>(world.ObjectById(21));
        var geometry = AvatarSceneGeometry.For(gameDir, world.ObjectRegistry!);
        var camera = new SimLotCamera(0, 64, 64);

        var cookClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-stove-cook");
        var eatClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-eatloop-stand");
        var carryClip = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-rarm-carry-loop");
        var swimAdjust = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-pool-swim-adjust-n");
        var getFood = ResolveRealClip(gameDir, world.ObjectRegistry, "a2o-fridge-getfood");

        // 1. Primary -> StopCurrentAnimation -> Last fade-out slot blending + Carry layering
        bella.AnimationPlayback = new AvatarAnimationPlayback(cookClip, false, false, bella.AnimatorMilliseconds);
        for (int i = 0; i < 12; i++) world.TickAvatarAnimations(bella);
        var cookParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);

        world.StopCurrentAnimation(bella);
        bella.AnimationPlayback = new AvatarAnimationPlayback(eatClip, false, false, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);

        var order = bella.GetAnimationPracticeApplicationOrder();
        var fadingCook = order.FirstOrDefault(p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fadingCook);
        Assert.InRange(fadingCook!.PracticeWeight, 0.001f, 0.999f);

        bella.CarryAnimationPlayback = new AvatarAnimationPlayback(carryClip, false, true, bella.AnimatorMilliseconds);
        world.TickAvatarAnimations(bella);
        var carryParts = geometry.Build(bella, 1, 0, (_, _) => 0f, camera);
        AssertFiniteParts(carryParts);
        Assert.NotNull(geometry.HandPosition(bella));
        Assert.True(MaxVertexDelta(cookParts, carryParts) > 0.001f);

        // 2. LookTowards head tracking + Pose.ApplyHeadAdjustment clamp
        world.SetObjectPosition(mortimer, new ObjectPosition(bella.Position.X, (short)(bella.Position.Y + 32), bella.Position.Height));
        bella.SetObjectTableEntry(2, mortimer);
        world.HeadSeekGeometryResolver = (actor, target) => new HeadSeekGeometry(
            new Vector2(actor.Position.X, actor.Position.Y),
            new Vector2(actor.Position.X + 16, actor.Position.Y),
            new Vector2(target.Position.X, target.Position.Y));
        Assert.Equal(PrimitiveResult.True, LookTowardsPrimitive.Execute(new TreeContext(world, bella, new StackFrame(2, [], [])), [0, 0]));
        for (int i = 0; i < 10; i++) world.TickAvatarAnimations(bella);
        Assert.True(MathF.Abs(bella.AnimatorHeadAdjustment) > 0.01f);

        var (adultSkel, idleAnim, idleCurves) = LoadRealSkeletonAndAnim(gameDir, world.ObjectRegistry, "adult", "a2o-idle-armsdown-breathe");
        var basePose = Pose.Sample(adultSkel, idleAnim, idleCurves, 5.0, loop: true);
        var clamped1 = Pose.ApplyHeadAdjustment(adultSkel, basePose, 5.0f);
        var clamped2 = Pose.ApplyHeadAdjustment(adultSkel, basePose, 10.0f);
        int headIdx = Pose.BoneIndices(adultSkel)["HEAD"];
        Assert.Equal(clamped1[headIdx].LocalRotation, clamped2[headIdx].LocalRotation);

        // 3. XEVT anchor, discontinuity, censor & footstep cues + deficient-event substitute completion
        var swimPb = new AvatarAnimationPlayback(swimAdjust, false, false, bella.AnimatorMilliseconds);
        bella.AnimationPlayback = swimPb;
        bella.AnimatorAnchorInitialized = true;
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, swimPb, new AvatarAnimationSoundEvent(0, "", "anchor", "1", false, false, "R_TOE01")));
        Assert.Equal("R_TOE01", bella.AnimatorAnchorBone);

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, swimPb, new AvatarAnimationSoundEvent(0, "", "discontinuity", "1", false, false)));
        Assert.DoesNotContain(bella.GetAnimationPracticeApplicationOrder(), p => p.Clip.Name.Equals("a2o-stove-cook", StringComparison.OrdinalIgnoreCase));

        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, swimPb, new AvatarAnimationSoundEvent(0, "", "censor", "127", false, false)));
        Assert.True(server.HandleJson("scene").GetProperty("ok").GetBoolean());
        var (censorScene, _) = LotScene.Load(server.LatestScenePath());
        Assert.NotEmpty(censorScene.CensorRects ?? []);
        Assert.True(SimulationWorld.ApplyAnimationTimeProperty(bella, swimPb, new AvatarAnimationSoundEvent(0, "", "censor", "0", false, false)));

        world.AvatarAnimationTable[(1, false, 42)] = getFood;
        var animFrame = new StackFrame(0, [0], [0, 0]);
        var animCtx = new TreeContext(world, bella, animFrame) { Stack = new DummyTreeStack() };
        var operand = AnimateSimOperand(42, source: 1, flags: 0, expectedEvents: 3);
        var fired = new List<short>();
        var res = AnimateSimPrimitive.Execute(animCtx, operand);
        for (int tick = 0; tick < 300 && res != PrimitiveResult.True; tick++)
        {
            world.TickAvatarAnimations(bella);
            res = AnimateSimPrimitive.Execute(animCtx, operand);
            if (res == PrimitiveResult.False)
                fired.Add(animFrame.Parameters[0]);
        }
        Assert.Equal(PrimitiveResult.True, res);
        Assert.Equal(new short[] { 0, 1, 2 }, fired);
    }

    // -------------------------------------------------------------------------
    // T4-10: Old Town Pet Ownership, Pet Shows & Neighborhood Life
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_10_OldTownPetOwnershipPetShowsAndNeighborhoodLife()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 70))
            return;

        var straysPath = Path.Combine(gameDir, "TemplateUserData", "Patch", "Strays_4000.FAM");
        Assert.True(File.Exists(straysPath), $"Expected Strays_4000.FAM at '{straysPath}'.");
        Assert.NotEmpty(IffFile.Open(straysPath).OfType(ResourceType.Parse("FAMI")));

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 70);
        using var server = E2EHarness.StartServer(profile, house: 5);

        Assert.True(server.HandleJson("audio", new { enabled = true }).GetProperty("ok").GetBoolean());
        var homeWorld = server.World!;
        var actor = homeWorld.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var actorGuid = actor.TypeRecord!.Key;
        var famNum = actor[PersonData.FamilyNumber];

        // 1. Travel to Old Town Pet Paradise (House70) and verify real PetPenDog / PetPenCat displays
        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(70, actorGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(70, server.State().GetProperty("house").GetInt32());
        var objs70 = server.State().GetProperty("objects").EnumerateArray().ToArray();
        Assert.Contains(objs70, o => o.GetProperty("guid").GetUInt32() == 0x3D3E7889u); // Display - Floor - PetPenDog
        Assert.Contains(objs70, o => o.GetProperty("guid").GetUInt32() == 0x6350E96Fu); // Display - Floor - PetPenCat

        // Adopt both a Dog (mode 1) and a Cat (mode 2) on/from trip within 8-member household cap
        var awayActor = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == actorGuid);
        var dogFrame = new StackFrame(awayActor.Id, new short[8], [0, 1, 0]);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(new TreeContext(server.World, awayActor, dogFrame), [0, 1, 2, 1, 0, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(server.World, awayActor, new StackFrame(awayActor.Id, new short[8], [0, 1]) { StackObject = dogFrame.StackObject }),
            [0x04, 0, 0, 0, 0, 0, 0, 0]));
        var dogGuid = server.World.Neighbors[dogFrame.StackObject].TypeRecordKey;

        var catFrame = new StackFrame(awayActor.Id, new short[8], [0, 1, 0]);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(new TreeContext(server.World, awayActor, catFrame), [0, 1, 2, 2, 0, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(
            new TreeContext(server.World, awayActor, new StackFrame(awayActor.Id, new short[8], [0, 1]) { StackObject = catFrame.StackObject }),
            [0x04, 0, 0, 0, 0, 0, 0, 0]));
        var catGuid = server.World.Neighbors[catFrame.StackObject].TypeRecordKey;

        // Return home -> verify both pets spawn on home lot, buy Pet Bowl & Pet Bed, train 3 pet skills
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, actorGuid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        var dog = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == dogGuid);
        var cat = server.World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == catGuid);
        Assert.NotNull(server.World.ObjectRegistry!.Find(unchecked((uint)dogGuid))!.File.Find(ResourceType.Parse("BMP_"), 2002));
        Assert.NotNull(server.World.ObjectRegistry!.Find(unchecked((uint)catGuid))!.File.Find(ResourceType.Parse("BMP_"), 2002));

        // Train 3 pet skills (Housebreaking, Obedience, Tricks/Hunting)
        dog[PersonData.CookingHouseBrokenSkill] = 400;
        dog[PersonData.CharismaObedienceSkill] = 550;
        dog[PersonData.MechanicalTricksSkill] = 700;
        cat[PersonData.CookingHouseBrokenSkill] = 350;
        cat[PersonData.CharismaObedienceSkill] = 450;
        cat[PersonData.MechanicalTricksSkill] = 650;

        // Verify pet VOX audio cues & 3D skeleton rendering
        Assert.True(server.HandleJson("audioEvent", new { name = "voxdog_barkb", ownerID = (int)dog.Id }).GetProperty("result").GetProperty("played").GetBoolean());
        Assert.True(server.HandleJson("audioEvent", new { name = "voxcat_mew", ownerID = (int)cat.Id }).GetProperty("result").GetProperty("played").GetBoolean());

        var geometry = AvatarSceneGeometry.For(gameDir, server.World.ObjectRegistry);
        var camera = new SimLotCamera(0, 64, 64);
        AssertFiniteParts(geometry.Build(dog, 1, 0, (_, _) => 0f, camera));
        AssertFiniteParts(geometry.Build(cat, 1, 0, (_, _) => 0f, camera));

        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);
        var (px, py) = FindFlatEmptyRegion(profile.UserData, 5, server.World, span: 4);
        Assert.True(TryBuyObjectInRegion(server, 0x8EEC19F7u, px, py, 3, 3, 1, out _)); // Pet - Food Bowl - Cheap
        Assert.True(TryBuyObjectInRegion(server, 0xE23F9020u, px, py, 3, 3, 1, out _)); // Pet - Bed - Cheap

        // Save & reload -> verify pets, pet skills, and pet objects persist
        Assert.True(server.HandleJson("save", new { slot = "t4-10-pets", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "t4-10-pets" }).GetProperty("ok").GetBoolean());

        var reloadedDog = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == dogGuid);
        var reloadedCat = server.World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == catGuid);
        Assert.Equal((short)700, reloadedDog[PersonData.MechanicalTricksSkill]);
        Assert.Equal((short)650, reloadedCat[PersonData.MechanicalTricksSkill]);
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(), o => o.GetProperty("guid").GetUInt32() == 0x8EEC19F7u);
        Assert.Contains(server.State().GetProperty("objects").EnumerateArray(), o => o.GetProperty("guid").GetUInt32() == 0xE23F9020u);
    }

    // -------------------------------------------------------------------------
    // T4-11: Architectural Mansion Build, Room Score & Classic Save Import
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_11_ArchitecturalMansionBuildRoomScoreAndClassicSaveImport()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 4, 5))
            return;

        var famSrc = Path.Combine(gameDir, "TemplateFamilyUnleashed", "Kat_6.FAM");
        Assert.True(File.Exists(famSrc), $"Expected family file at '{famSrc}'.");

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 4, 5);
        var importDir = Path.Combine(profile.UserData, "Import");
        Directory.CreateDirectory(importDir);

        var famIff = IffFile.Open(famSrc);
        var famiType = ResourceType.Parse("FAMI");
        var famiChunk = famIff.OfType(famiType).First(c => c.Id != 0);
        var famRecord = Family.Read(famIff, famiChunk);
        var patchedFam = famIff.WithAddedOrReplacedChunk(
            famiType,
            famiChunk.Id,
            famRecord.WithHouseNumber(0).WithBudget(80_000).WithValue(0).Write(famiChunk.Id));
        File.WriteAllBytes(Path.Combine(importDir, "Kat_6.FAM"), patchedFam.Bytes.ToArray());

        var candidates = NeighborhoodEdits.EnumerateImportCandidates(profile.UserData, profile.Profile, gameDir, 1);
        var katIdx = Array.FindIndex(candidates.Candidates, c => c.FamilyName.Contains("Kat", StringComparison.OrdinalIgnoreCase));
        Assert.True(katIdx >= 0);

        using var server = E2EHarness.StartServer(profile, house: 0);
        Assert.True(server.HandleJson("neighborhood", new { mode = 1, edit = $"import:{katIdx}" }).GetProperty("ok").GetBoolean());
        var importedKat = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1)
            .First(f => f.Name.Contains("Kat", StringComparison.OrdinalIgnoreCase));

        Assert.True(server.HandleJson("openLot", new { house = 4 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("moveInFamily", new { family = importedKat.Id }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("speed", new { value = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("view", new { editing = true }).GetProperty("ok").GetBoolean());
        DismissBlockingDialogs(server);

        var world = server.World!;
        var (mx, my) = FindFlatEmptyRegion(profile.UserData, 4, world, span: 10);

        // 1. Build 2-story mansion with diagonal wall, floor, wallpaper, pool, and gable roof
        Assert.True(server.HandleJson("architecture", new { command = "WALL_RECT", level = 1, x = mx, y = my, x2 = 5, y2 = 5, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "WALL_LINE", level = 1, x = mx + 1, y = my + 1, x2 = mx + 2, y2 = my + 2, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 1, x = mx, y = my, x2 = 5, y2 = 5, pattern = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "WALL_RECT", level = 2, x = mx, y = my, x2 = 5, y2 = 5, style = 1, pattern = 0 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "FLOOR_RECT", level = 2, x = mx, y = my, x2 = 5, y2 = 5, pattern = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("architecture", new { command = "POOL_RECT", level = 1, x = mx + 6, y = my + 1, x2 = 3, y2 = 3 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("roof", new { style = 0, pitch = 1.0f }).GetProperty("ok").GetBoolean());

        // 2. Toggle walls (down/cutaway/up), roofs (true/false), and 4 camera rotations
        foreach (var mode in new[] { "down", "cutaway", "up" })
        {
            Assert.True(server.HandleJson("walls", new { mode, x = mx + 2, y = my + 2 }).GetProperty("ok").GetBoolean());
        }
        Assert.True(server.HandleJson("roofs", new { visible = true, level = 2 }).GetProperty("ok").GetBoolean());
        foreach (int rot in new[] { 0, 1, 2, 3 })
        {
            Assert.True(server.HandleJson("cameraOrientation", new { value = rot }).GetProperty("ok").GetBoolean());
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            Assert.NotEmpty(scene.Meshes ?? []);
        }

        // Verify Room motive & GenericSimsCall lot valuation
        var actor = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        world.UpdatePersonRoomMotive(actor);
        Assert.NotEmpty(world.LotRoomMaps);
        var valCtx = new TreeContext(world, actor, new StackFrame(actor.Id, new short[8], [0, 1]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(valCtx, [0x00, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(valCtx, [0x2b, 0, 0, 0, 0, 0, 0, 0]));

        // Save House04, then load authentic original House05.iff and export Neighborhood HTML web album
        Assert.True(server.HandleJson("save", new { slot = "t4-11-mansion", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("openLot", new { house = 5 }).GetProperty("ok").GetBoolean());
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());

        var webDest = Path.Combine(profile.TempRoot, "WebExport");
        var webFamilies = new List<NeighborhoodWebExport.Family>
        {
            new(
                name: "Goth",
                lot: 5,
                street: 1,
                members: 3,
                friends: 2,
                cashBalance: 2542,
                daysExist: 4,
                memberNames: ["Mortimer", "Bella", "Cassandra"],
                houseSquareFeet: 1200,
                houseBathrooms: 2,
                houseBedrooms: 2,
                houseFloors: 2,
                houseValue: 33116)
        };
        NeighborhoodWebExport.Export(gameDir, webFamilies, webDest, InstalledGameLanguage.EnglishUS);
        Assert.True(File.Exists(Path.Combine(webDest, "family1.html")));
        Assert.True(File.Exists(Path.Combine(webDest, "1_Goth_Sim_Lane", "familyhome.html")));
    }

    // -------------------------------------------------------------------------
    // T4-12: Neighborhood Easter Eggs, Multi-Panel Classic UI & Options Persistence
    // -------------------------------------------------------------------------

    [Fact]
    public void T4_12_NeighborhoodEasterEggsMultiPanelClassicUiAndOptionsPersistence()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        var manifestPath = Path.Combine(profile.Session, "neighborhood.json");
        var content = new GameContent(gameDir);
        var font = new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        var strings = new OriginalUIStrings(content);
        var device = new UIDevice();

        // 1. Browse Neighborhood modes (1, 2, 3, 5, 7), TravelFilter persistence, Nessie + duplicate rejection, Cars
        using (var server = E2EHarness.StartServer(profile, house: 0))
        {
            Assert.True(server.HandleJson("travelFilter", new { mode = 2, filter = 16 }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("travelFilter", new { mode = 5, filter = 0x200 }).GetProperty("ok").GetBoolean());
            TravelFilter.Save(profile.UserData, 1, 0, 1); // 0 must NOT overwrite non-zero mask or crash
            Assert.Equal(16, TravelFilter.Load(profile.UserData, 1, 1, 2));

            foreach (int mode in new[] { 1, 2, 3, 5, 7 })
            {
                Assert.True(server.HandleJson("neighborhood", new { mode }).GetProperty("ok").GetBoolean());
            }

            Assert.True(server.HandleJson("neighborhood", new { mode = 1 }).GetProperty("ok").GetBoolean());
            var overlay1 = new OriginalNeighborhoodOverlay(content, device, manifestPath, profile.Session, font);
            var played = new List<string>();
            overlay1.PlaySound = played.Add;
            Assert.True(overlay1.TriggerNessie(time: 1.0));
            Assert.Contains("nessie_sfx", played);
            Assert.False(overlay1.TriggerNessie(time: 1.5)); // Reject duplicate Nessie
            Assert.True(server.HandleJson("cheat", new { code = "nessie" }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("cheat", new { code = "cars 2 3" }).GetProperty("ok").GetBoolean());

            // 2. Open House05: Pie Head Preview, Live PIP, Action Queue cancel, Photo Album, Options Persistence
            Assert.True(server.HandleJson("openLot", new { house = 5 }).GetProperty("ok").GetBoolean());

            var headA = server.HandleJson("pieHeadPreview", new { yaw = 0.4f, pitch = -0.1f });
            var headB = server.HandleJson("pieHeadPreview", new { yaw = -0.4f, pitch = 0.1f });
            Assert.True(headA.GetProperty("ok").GetBoolean());
            Assert.True(headB.GetProperty("ok").GetBoolean());
            var pxA = Convert.FromBase64String(headA.GetProperty("result").GetProperty("pixels").GetString()!);
            var pxB = Convert.FromBase64String(headB.GetProperty("result").GetProperty("pixels").GetString()!);
            Assert.False(pxA.SequenceEqual(pxB));

            Assert.True(server.HandleJson("pictureInPicture", new { live = true }).GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Array, server.State().GetProperty("pictureInPicture").ValueKind);

            // Queue actions & cancel #2
            var state = server.State();
            short objId = 0;
            JsonElement action = default;
            foreach (var obj in state.GetProperty("objects").EnumerateArray())
            {
                var id = obj.GetProperty("id").GetInt16();
                var menuResp = server.HandleJson("menu", new { objectID = id });
                if (menuResp.GetProperty("ok").GetBoolean() && menuResp.GetProperty("result").GetArrayLength() > 0)
                {
                    objId = id;
                    action = menuResp.GetProperty("result")[0].Clone();
                    break;
                }
            }
            Assert.True(objId > 0);
            for (int i = 0; i < 3; i++)
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
            var actions = server.State().GetProperty("sims").EnumerateArray()
                .First(s => s.GetProperty("id").GetInt16() == selectedId)
                .GetProperty("actions").EnumerateArray().ToArray();
            if (actions.Length >= 2)
            {
                var cancelToken = actions[1].GetProperty("token").GetString()!;
                Assert.True(server.HandleJson("cancelAction", new { objectID = selectedId, token = cancelToken }).GetProperty("ok").GetBoolean());
            }

            // Photo Album capture & reload
            var albumDir = Path.Combine(profile.TempRoot, "PhotoAlbum");
            var album = new PhotoAlbum(albumDir);
            var rgba = new byte[4 * 4 * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 190;
                rgba[i + 1] = 140;
                rgba[i + 2] = 90;
                rgba[i + 3] = 255;
            }
            album.Capture(rgba, 4, 4, 0.8f, "T4-12 Snapshot");
            album.Reload();
            Assert.Single(album.Photos);

            // Options panel toggles & persistence (options.json + audioVolume)
            var optPanel = new OriginalOptionsPanelOverlay(content, device, strings, font);
            var opts = new OriginalOptionsPanelOverlay.PlayOptionsState();
            opts = OriginalOptionsPanelOverlay.PlayOption.EdgeScrolling.Toggled(opts);
            opts = OriginalOptionsPanelOverlay.PlayOption.AutoCenter.Toggled(opts);
            opts = OriginalOptionsPanelOverlay.PlayOption.FreeWill.Toggled(opts);
            opts = OriginalOptionsPanelOverlay.PlayOption.LivePIP.Toggled(opts);
            opts = OriginalOptionsPanelOverlay.PlayOption.SharpText.Toggled(opts);
            optPanel.PlayOptions = opts;

            var settingsDir = Path.Combine(profile.Profile, "Settings");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllBytes(Path.Combine(settingsDir, "options.json"), JsonSerializer.SerializeToUtf8Bytes(opts));
            Assert.True(server.HandleJson("audioVolume", new { group = "MUSIC", volume = 0.42f }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("save", new { slot = "t4-12-ui", lot = true }).GetProperty("ok").GetBoolean());
        }

        // 3. Restart SimulationServer & verify persisted options, TravelFilter, audio volume & saved lot
        using (var restarted = E2EHarness.StartServer(profile, house: 0))
        {
            var reloadedOpts = JsonSerializer.Deserialize<OriginalOptionsPanelOverlay.PlayOptionsState>(
                File.ReadAllBytes(Path.Combine(profile.Profile, "Settings", "options.json")));
            Assert.False(reloadedOpts.EdgeScrolling);
            Assert.False(reloadedOpts.AutoCenter);
            Assert.False(reloadedOpts.FreeWill);
            Assert.False(reloadedOpts.LivePIP);
            Assert.False(reloadedOpts.SharpText);

            Assert.Equal(16, TravelFilter.Load(profile.UserData, 1, 1, 2));
            Assert.Equal(0x200, TravelFilter.Load(profile.UserData, 4, 1, 0x400));
            Assert.Equal(0.42f, restarted.HandleJson("audioState").GetProperty("result").GetProperty("volumes").GetProperty("MUSIC").GetSingle(), 2);

            Assert.True(restarted.HandleJson("load", new { slot = "t4-12-ui" }).GetProperty("ok").GetBoolean());
            Assert.Equal(5, restarted.State().GetProperty("house").GetInt32());
        }
    }
}
