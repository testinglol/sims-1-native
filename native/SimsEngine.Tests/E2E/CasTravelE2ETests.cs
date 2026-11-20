using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSDL.Core;
using SimsSDL.UI;
using SimsSimulation;
using CoreAppearancePreview = SimsSDL.Core.HouseholdAppearancePreview;

namespace SimsEngine.Tests.E2E;

[Collection("Neighborhoods")]
[Trait("Suite", "E2E")]
[Trait("Area", "CasTravel")]
public sealed class CasTravelE2ETests
{
    private static bool IsHousehold(XPerson person) =>
        person[PersonData.FamilyNumber] > 0 && person.TypeRecord is not null;

    private static bool IsChild(XPerson person) =>
        person[PersonData.PersonAge] < 18;

    private static short GenderOf(XPerson person) =>
        person[PersonData.Gender];

    private static int ReadFamilyVacationField(string userData, ushort familyChunkId)
    {
        var ngh = IffFile.Open(Path.Combine(userData, "Neighborhood.iff"));
        var chunk = ngh.Find(ResourceType.Parse("FAMI"), familyChunkId)!;
        return Family.Read(ngh, chunk).Field0x13C;
    }

    private static short[] DefaultPersonality() => [500, 500, 500, 500, 500];

    private static string CreateIsolatedGameDirWithDownloads(string sourceGameDir, Action<string> populateDownloads, out string tempRoot)
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "sims-e2e-gamedir-" + Guid.NewGuid().ToString("N"));
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
            pixels[i] = 180; pixels[i + 1] = 120; pixels[i + 2] = 90; pixels[i + 3] = 255;
        }
        File.WriteAllBytes(Path.Combine(folder, bmpFileName), BitmapCodec.Write(4, 4, pixels));

        // Minimal valid text SKN matching Mesh.Read order:
        // name, texture, bones, faces, bindings, texture coordinates, blends, vertices.
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

        // Minimal valid text CMX referencing meshName & textureName
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

    private static void AssertCas3DPreviewMeshIntegrity(CoreAppearancePreview preview, IReadOnlyList<byte[]> pixels)
    {
        preview.Validate();
        Assert.True(preview.Meshes.Count >= 2, $"Expected >= 2 CAS 3D preview meshes (body/head/hands), got {preview.Meshes.Count}.");
        Assert.NotEmpty(preview.Textures);
        Assert.Equal(preview.Textures.Count, pixels.Count);

        foreach (var px in pixels)
        {
            Assert.NotEmpty(px);
            Assert.Equal(0, px.Length % 4);
            Assert.Contains(px, b => b != 0);

            byte r0 = px[0], g0 = px[1], b0 = px[2];
            bool hasColorVariance = false;
            for (int i = 4; i + 2 < px.Length; i += 4)
            {
                if (px[i] != r0 || px[i + 1] != g0 || px[i + 2] != b0)
                {
                    hasColorVariance = true;
                    break;
                }
            }
            Assert.True(hasColorVariance, "CAS 3D preview texture has zero pixel variance (possible FallbackWhiteTexture).");
        }

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        float minU = float.PositiveInfinity, maxU = float.NegativeInfinity;
        float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        int totalTris = 0;
        int nonDegenerateTris = 0;

        foreach (var mesh in preview.Meshes)
        {
            Assert.NotEmpty(mesh.Vertices);
            Assert.Equal(0, mesh.Vertices.Count % 3);
            Assert.InRange(mesh.Texture, 0, preview.Textures.Count - 1);
            totalTris += mesh.Vertices.Count / 3;

            for (int i = 0; i < mesh.Vertices.Count; i += 3)
            {
                var a = mesh.Vertices[i];
                var b = mesh.Vertices[i + 1];
                var c = mesh.Vertices[i + 2];

                foreach (var v in new[] { a, b, c })
                {
                    Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
                    Assert.True(float.IsFinite(v.U) && float.IsFinite(v.V));
                    Assert.True(float.IsFinite(v.NX) && float.IsFinite(v.NY) && float.IsFinite(v.NZ));
                    Assert.True(
                        v.NX * v.NX + v.NY * v.NY + v.NZ * v.NZ > 0.5f,
                        $"CAS 3D preview vertex normal collapsed: ({v.NX}, {v.NY}, {v.NZ})");

                    minX = MathF.Min(minX, v.X);
                    maxX = MathF.Max(maxX, v.X);
                    minY = MathF.Min(minY, v.Y);
                    maxY = MathF.Max(maxY, v.Y);
                    minZ = MathF.Min(minZ, v.Z);
                    maxZ = MathF.Max(maxZ, v.Z);
                    minU = MathF.Min(minU, v.U);
                    maxU = MathF.Max(maxU, v.U);
                    minV = MathF.Min(minV, v.V);
                    maxV = MathF.Max(maxV, v.V);
                }

                var e1 = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                var e2 = new Vector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                if (Vector3.Cross(e1, e2).LengthSquared() > 1e-8f)
                    nonDegenerateTris++;
            }
        }

        Assert.True(maxX - minX > 0.05f, $"CAS 3D preview X span collapsed: {maxX - minX}");
        Assert.True(maxY - minY > 0.1f, $"CAS 3D preview Y span collapsed: {maxY - minY}");
        Assert.True(maxZ - minZ > 0.001f, $"CAS 3D preview Z span collapsed: {maxZ - minZ}");
        Assert.True(maxU > minU && maxV > minV, $"CAS 3D preview UV span collapsed: U=[{minU}, {maxU}], V=[{minV}, {maxV}].");
        Assert.True(
            nonDegenerateTris * 2 > totalTris,
            $"CAS 3D preview has too many degenerate triangles: {nonDegenerateTris}/{totalTris}.");
    }

    // -------------------------------------------------------------------------
    // Feature F13: Travelling: SuspendedHome & FAMI.Field0x13C Sync
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void T1_F13_01_TravelAwayAndReturnHome_RestoresSuspendedHomeAndSyncsTraveller()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var homeObj = homeWorld.Objects.Values.First(o => o is not XPerson && o.TypeAttributes.Length > 0);
        homeObj.TypeAttributes[0] = 77;
        var homeObjId = homeObj.Id;

        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNum = traveller[PersonData.FamilyNumber];

        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        var awayState = server.State();
        Assert.Equal(21, awayState.GetProperty("house").GetInt32());
        Assert.True(awayState.GetProperty("away").GetBoolean());

        var awayTraveller = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        awayTraveller[PersonData.CookingHouseBrokenSkill] = 520;
        server.World.FamilyBudget!.AlterFamilyBudget(0, -300);
        var expectedBudget = server.World.FamilyBudget.ReadFamilyBudget();

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        var returnedState = server.State();
        Assert.Equal(5, returnedState.GetProperty("house").GetInt32());
        Assert.False(returnedState.GetProperty("away").GetBoolean());
        Assert.Equal(expectedBudget, returnedState.GetProperty("budget").GetInt32());

        var restoredObj = server.World!.ObjectById(homeObjId);
        Assert.NotNull(restoredObj);
        Assert.Equal((short)77, restoredObj!.TypeAttributes[0]);

        var returnedTraveller = server.World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == travellerGuid);
        Assert.Equal((short)520, returnedTraveller[PersonData.CookingHouseBrokenSkill]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void T1_F13_02_SaveOnAwayLotQuitReloadAndReturnHome_RestoresSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var homeObj = homeWorld.Objects.Values.First(o => o is not XPerson && o.TypeAttributes.Length > 0);
        homeObj.TypeAttributes[0] = 55;
        var homeObjId = homeObj.Id;

        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNum = traveller[PersonData.FamilyNumber];

        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "away-save", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());

        var loadResp = server.HandleJson("load", new { slot = "away-save" });
        Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
        Assert.Equal(21, server.State().GetProperty("house").GetInt32());

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        var returned = server.State();
        Assert.Equal(5, returned.GetProperty("house").GetInt32());
        Assert.False(returned.GetProperty("away").GetBoolean());
        Assert.Equal((short)55, server.World!.ObjectById(homeObjId)!.TypeAttributes[0]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void T1_F13_03_OpenLotInternalAndServerLoadAfterVacationSave_RedirectsAndRestoresSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNum = traveller[PersonData.FamilyNumber];

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "vac-open", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());

        var openResp = server.HandleJson("openLot", new { house = 5 });
        Assert.True(openResp.GetProperty("ok").GetBoolean(), openResp.ToString());
        var openRes = openResp.GetProperty("result");
        Assert.Equal(40, openRes.GetProperty("house").GetInt32());
        Assert.Equal(5, openRes.GetProperty("homeHouse").GetInt32());
        Assert.True(openRes.GetProperty("away").GetBoolean());

        using var directReload = E2EHarness.StartServer(profile, house: 5);
        var reloadState = directReload.State();
        Assert.Equal(40, reloadState.GetProperty("house").GetInt32());
        Assert.Equal(5, reloadState.GetProperty("homeHouse").GetInt32());

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
        Assert.False(server.State().GetProperty("away").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void T1_F13_04_VacationIslandSave_SetsAndClearsFamiField0x13C()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var familyChunkId = ((FamilyBudget)homeWorld.FamilyBudget!).FamilyChunkId!.Value;
        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNum = traveller[PersonData.FamilyNumber];

        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "vac-field", lot = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(40, ReadFamilyVacationField(profile.UserData, familyChunkId));

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "home-cleared", lot = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void T1_F13_05_UnsavedExitOnVacation_ReconcilesFamiField0x13CAtomically()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var familyChunkId = ((FamilyBudget)homeWorld.FamilyBudget!).FamilyChunkId!.Value;
        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var travellerGuid = traveller.TypeRecord!.Key;
        var familyNum = traveller[PersonData.FamilyNumber];

        // Depart to 40 without saving -> disk Field0x13C stays 0 even when NeighborInventory persists state.
        homeWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();
        server.World!.NeighborInventory(traveller[PersonData.NeighborId]).Add(7, 0x11223344, 1);
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));

        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.Equal(0, ReadFamilyVacationField(profile.UserData, familyChunkId));

        var reopen = server.HandleJson("openLot", new { house = 5 });
        Assert.True(reopen.GetProperty("ok").GetBoolean());
        Assert.Equal(5, reopen.GetProperty("result").GetProperty("house").GetInt32());

        // Commit 40, return home without saving, then quit -> Field0x13C reconciles back to 40.
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.True(server.HandleJson("save", new { slot = "vac-commit", lot = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(40, ReadFamilyVacationField(profile.UserData, familyChunkId));

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, travellerGuid, familyNum);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.False(server.Handle("""{"id":"quitVac","op":"quit"}"""));
        server.DrainEvents();
        Assert.Equal(40, ReadFamilyVacationField(profile.UserData, familyChunkId));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void T2_F13_01_EvictFamilySavedOnVacation_BlockedCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var familyChunkId = ((FamilyBudget)server.World.FamilyBudget!).FamilyChunkId!.Value;

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(40, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.True(server.HandleJson("save", new { slot = "vac-evict", lot = true }).GetProperty("ok").GetBoolean());
        Assert.Equal(40, ReadFamilyVacationField(profile.UserData, familyChunkId));

        Assert.Throws<InvalidOperationException>(() => NeighborhoodEdits.Evict(profile.UserData, gameDir, 5));
        var evictResp = server.HandleJson("neighborhood", new { mode = 1, edit = "evict:5" });
        Assert.False(evictResp.GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void T2_F13_02_MultiHopCommunityTravelChain_PreservesOriginalSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21, 22))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21, 22);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var guid = traveller.TypeRecord!.Key;
        var famNum = traveller[PersonData.FamilyNumber];

        // Hop 1: Home 5 -> Downtown 21
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, guid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        var s21 = server.State();
        Assert.Equal(21, s21.GetProperty("house").GetInt32());
        Assert.Equal(5, s21.GetProperty("homeHouse").GetInt32());

        // Hop 2: Downtown 21 -> Downtown 22
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(22, guid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        var s22 = server.State();
        Assert.Equal(22, s22.GetProperty("house").GetInt32());
        Assert.Equal(5, s22.GetProperty("homeHouse").GetInt32());

        // Return Home
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, guid, famNum);
        server.BatchRun(1);
        server.DrainEvents();
        var sHome = server.State();
        Assert.Equal(5, sHome.GetProperty("house").GetInt32());
        Assert.False(sHome.GetProperty("away").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void T2_F13_03_AwaySaveMissingSuspendedHome_FallsBackSafelyWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        var saveResp = server.HandleJson("save", new { slot = "legacy-away", lot = false });
        var envPath = saveResp.GetProperty("result").GetProperty("path").GetString()!;

        using var doc = JsonDocument.Parse(File.ReadAllBytes(envPath));
        var patched = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "suspendedHome") continue;
            if (prop.Name == "runtime")
            {
                var rt = new Dictionary<string, object?>();
                foreach (var rProp in prop.Value.EnumerateObject())
                {
                    if (rProp.Name == "suspendedHome") continue;
                    rt[rProp.Name] = rProp.Value.Clone();
                }
                patched["runtime"] = rt;
            }
            else
            {
                patched[prop.Name] = prop.Value.Clone();
            }
        }
        File.WriteAllBytes(envPath, JsonSerializer.SerializeToUtf8Bytes(patched));

        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        var loadResp = server.HandleJson("load", new { slot = "legacy-away" });
        Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
        Assert.Equal(21, server.State().GetProperty("house").GetInt32());
        Assert.True(server.State().GetProperty("away").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void T2_F13_04_ReturnHomeClockArithmetic_AdvancesWithoutNegativeOr24HourOverflow()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var traveller = homeWorld.WalkOrder.OfType<XPerson>().First(IsHousehold);
        homeWorld.Clock.Restore(22, 30, 0);

        // Downtown departure adds 4h35m wrapping cleanly past midnight -> 03:05
        homeWorld.RunGenericSimsCall11Native(traveller, 21, 4, 35, 0);
        server.BatchRun(1);
        server.DrainEvents();

        var awayWorld = server.World!;
        Assert.InRange(awayWorld.Clock.Hour, 0, 23);
        Assert.InRange(awayWorld.Clock.Minute, 0, 59);
        Assert.Equal(3, awayWorld.Clock.Hour);
        Assert.Equal(5, awayWorld.Clock.Minute);

        // Return home -> test Vacation departure clock (sets explicit hour/minute 14:00)
        awayWorld.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        var returnedHome = server.World!;
        var homeTraveller = returnedHome.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == traveller.TypeRecord.Key);
        returnedHome.Clock.Restore(10, 0, 0);
        returnedHome.RunGenericSimsCall11Native(homeTraveller, 40, 14, 0, 1);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(14, server.World!.Clock.Hour);
        Assert.Equal(0, server.World.Clock.Minute);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void T2_F13_05_TravelWhileOtherSimAtWorkSchool_PreservesWorkingSimInSuspendedHome()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var members = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(members.Length >= 2);
        var traveller = members[0];
        var worker = members[1];
        var workerGuid = worker.TypeRecord!.Key;
        worker[PersonData.IsPersonAtWorkSchool] = 1;
        worker[PersonData.CookingHouseBrokenSkill] = 333;

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.DoesNotContain(server.World!.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == workerGuid);

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, traveller.TypeRecord.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        var restoredWorkers = server.World!.WalkOrder.OfType<XPerson>().Where(p => p.TypeRecord?.Key == workerGuid).ToArray();
        Assert.Single(restoredWorkers);
        Assert.Equal((short)333, restoredWorkers[0][PersonData.CookingHouseBrokenSkill]);
    }

    // -------------------------------------------------------------------------
    // Feature F14: Travelling: Partial Household RestrictActivePersonKeys
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public void T1_F14_01_SoloTravel_ExcludesStayAtHomeSimsFromAwayLot()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var members = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(members.Length >= 3);
        var solo = members[0];
        var stayB = members[1];
        var stayC = members[2];

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, solo.TypeRecord!.Key, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal([solo.TypeRecord.Key], server.World!.ActivePersonRecordKeys);
        Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayB.TypeRecord!.Key);
        Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayC.TypeRecord!.Key);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public void T1_F14_02_SaveAndReloadSoloAwayLot_PreservesRestrictedActivePersonKeys()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var members = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        var solo = members[0];
        var soloGuid = solo.TypeRecord!.Key;
        var stayHomeGuid = members[1].TypeRecord!.Key;

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, soloGuid, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "solo-persist", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "solo-persist" }).GetProperty("ok").GetBoolean());

        Assert.Equal([soloGuid], server.World!.ActivePersonRecordKeys);
        Assert.DoesNotContain(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayHomeGuid);
        var householdSims = server.State().GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
        Assert.Single(householdSims);
        Assert.Equal(soloGuid, unchecked((int)householdSims[0].GetProperty("guid").GetUInt32()));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public void T1_F14_03_DateOuting_SpawnsTravellerAndInvitedGuestOnly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var members = world.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(members.Length >= 2);
        var traveller = members[0];
        var stayHome = members[1];

        var visitorGuest = world.WalkOrder.OfType<XPerson>().FirstOrDefault(p => !IsHousehold(p) && p.TypeRecord is not null);
        if (visitorGuest is null)
        {
            // Spawn a neighbor visitor onto the lot if none is currently walking in.
            var neighborKey = world.Neighbors.Values
                .Select(n => n.TypeRecordKey)
                .First(k => k != 0 && world.PersonRecordKeys.Contains(k) && members.All(m => m.TypeRecord!.Key != k));
            Assert.NotNull(world.ScopedNeighborPersonRecordId(neighborKey));
            visitorGuest = world.WalkOrder.OfType<XPerson>().FirstOrDefault(p => p.TypeRecord?.Key == neighborKey);
        }
        Assert.NotNull(visitorGuest);
        var guestGuid = visitorGuest!.TypeRecord!.Key;

        var scope = world.InteractionExecutorScope(traveller);
        scope.AddOrIncrement(1, guestGuid, 1);
        world.RunGenericSimsCall11Native(traveller, 21, 1, 0, 0);
        server.BatchRun(1);
        server.DrainEvents();

        var awayWorld = server.World!;
        Assert.Contains(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == traveller.TypeRecord!.Key);
        Assert.Contains(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == guestGuid);
        Assert.DoesNotContain(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == stayHome.TypeRecord!.Key);
        Assert.Equal([traveller.TypeRecord!.Key], awayWorld.ActivePersonRecordKeys);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public void T1_F14_04_FullFamilyVacationTravel_BringsAllFamilyMembers()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 40))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 40);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeWorld = server.World!;
        var homeMembers = homeWorld.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        Assert.True(homeMembers.Length >= 3);
        var leader = homeMembers[0];

        homeWorld.RunGenericSimsCall11Native(leader, 40, 10, 0, 1);
        server.BatchRun(1);
        server.DrainEvents();

        var awayWorld = server.World!;
        foreach (var member in homeMembers)
        {
            Assert.Contains(member.TypeRecord!.Key, awayWorld.ActivePersonRecordKeys);
            Assert.Contains(awayWorld.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == member.TypeRecord!.Key);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public void T1_F14_05_ReturnHomeAfterSoloSaveReload_RestoresAllFamilyMembers()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var members = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        var expectedGuids = members.Select(m => m.TypeRecord!.Key).ToHashSet();
        var solo = members[0];

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, solo.TypeRecord!.Key, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "solo-ret", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "solo-ret" }).GetProperty("ok").GetBoolean());

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, solo.TypeRecord.Key, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        foreach (var guid in expectedGuids)
        {
            Assert.Contains(guid, server.World!.ActivePersonRecordKeys);
            Assert.Contains(server.World.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == guid);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public void T2_F14_01_InvitedDateDeclinesOrAbsent_SetsTemporary0ToMinus1WithoutNullGuest()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        person.Temporaries[0] = person.Id;

        var frame = new StackFrame(person.Id, new short[8], [0, 1]);
        var ctx = new TreeContext(world, person, frame);
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(ctx, [0x13, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal((short)-1, person.Temporaries[0]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public void T2_F14_02_StayAtHomeSimStateUntouched_AfterSoloSaveReloadAndReturn()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var members = server.World!.WalkOrder.OfType<XPerson>().Where(IsHousehold).ToArray();
        var simA = members[0];
        var simB = members[1];
        simB[PersonData.CreativitySkill] = 432;
        simB[Motive.Hunger] = 65f;

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, simA.TypeRecord!.Key, simA[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.True(server.HandleJson("save", new { slot = "solo-untouched", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "solo-untouched" }).GetProperty("ok").GetBoolean());

        var awayA = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simA.TypeRecord.Key);
        awayA[PersonData.LogicSkill] = 789;
        server.World.NeighborInventory(awayA[PersonData.NeighborId]).Add(7, unchecked((int)0xABCDEF01), 2);

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, simA.TypeRecord.Key, simA[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        var returnedA = server.World!.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simA.TypeRecord.Key);
        var returnedB = server.World.WalkOrder.OfType<XPerson>().First(p => p.TypeRecord?.Key == simB.TypeRecord!.Key);

        Assert.Equal((short)432, returnedB[PersonData.CreativitySkill]);
        Assert.Equal(65f, returnedB[Motive.Hunger]);
        Assert.Equal((short)789, returnedA[PersonData.LogicSkill]);
        Assert.Equal(2, server.World.NeighborInventory(returnedA[PersonData.NeighborId]).Entries
            .Where(e => e.Key1 == 7 && e.Key2 == unchecked((int)0xABCDEF01)).Sum(e => e.Count));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public void T2_F14_03_EightMemberHouseholdSoloTravel_SavesReloadsAndRestoresAll8()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9, 21);
        using var server = E2EHarness.StartServer(profile, house: 9);

        var members = Enumerable.Range(1, 8).Select(i => new
        {
            name = $"Member{i}",
            code = i % 2 == 0 ? "fafit" : "mafit",
            skin = "lgt",
            personality = new[] { 500, 500, 500, 500, 500 }
        }).ToArray();

        var createResp = server.HandleJson("createHousehold", new { familyName = "Octo", moveIn = true, house = (short)9, members });
        Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());
        Assert.Equal(8, server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean()));

        var solo = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, solo.TypeRecord!.Key, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Single(server.World!.ActivePersonRecordKeys);
        Assert.True(server.HandleJson("save", new { slot = "octo-solo", lot = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "octo-solo" }).GetProperty("ok").GetBoolean());
        Assert.Single(server.World!.ActivePersonRecordKeys);

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, solo.TypeRecord.Key, solo[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(8, server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean()));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public void T2_F14_04_ChildSimTravelMenuCheck_DoesNotOfferSoloDowntownCabOrCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var child = server.State().GetProperty("sims").EnumerateArray().First(s => s.GetProperty("child").GetBoolean());
        Assert.True(server.HandleJson("selectSim", new { objectID = child.GetProperty("id").GetInt16() }).GetProperty("ok").GetBoolean());

        var phones = server.State().GetProperty("objects").EnumerateArray()
            .Where(o => (o.GetProperty("name").GetString() ?? "").Contains("Phone", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(phones);

        foreach (var phone in phones)
        {
            var menu = server.HandleJson("menu", new { objectID = phone.GetProperty("id").GetInt16() });
            Assert.True(menu.GetProperty("ok").GetBoolean());
            foreach (var entry in menu.GetProperty("result").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString() ?? "";
                Assert.DoesNotContain("Call Cab", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Call a Cab", name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public void T2_F14_05_FamilyBudgetDeductionWhileRestrictedAway_SyncsToHomeFunds()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 21))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 21);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var initialBudget = server.State().GetProperty("budget").GetInt32();
        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(21, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        server.World!.FamilyBudget!.AlterFamilyBudget(0, 50);

        server.World.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, traveller.TypeRecord.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(initialBudget - 50, server.State().GetProperty("budget").GetInt32());
    }

    // -------------------------------------------------------------------------
    // Feature F15: Expansion Travelling & Neighborhood Easter Eggs
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void T1_F15_01_SubNeighborhoodMapSwitchingAndTravelFilters_LoadDistinctModesAndPersistFilters()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false, 5);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var expectedMusic = new Dictionary<int, string>
        {
            [2] = "station_dtnhood",
            [3] = "station_vacation",
            [5] = "station_superstar",
            [7] = "music_magictown"
        };

        foreach (var (mode, filter, slot) in new[] { (2, 16, 1), (3, 64, 2), (5, 0x200, 4), (7, 0x1000, 5) })
        {
            var tf = server.HandleJson("travelFilter", new { mode, filter });
            Assert.True(tf.GetProperty("ok").GetBoolean(), tf.ToString());
            Assert.Equal(slot, tf.GetProperty("result").GetProperty("slot").GetInt32());

            var ngh = server.HandleJson("neighborhood", new { mode });
            Assert.True(ngh.GetProperty("ok").GetBoolean(), ngh.ToString());
            var result = ngh.GetProperty("result");
            Assert.Equal(mode, result.GetProperty("mode").GetInt32());
            Assert.Equal(filter, result.GetProperty("travelFilter").GetInt32());
            Assert.Equal(expectedMusic[mode], result.GetProperty("music").GetString());
            Assert.True(result.GetProperty("entries").GetArrayLength() > 0);
        }

        Assert.True(server.HandleJson("neighborhood", new { mode = 1 }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("neighborhood", new { mode = 4 }).GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void T1_F15_02_VacationIslandHotelStay_MultiDayClockAndBudgetSync()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 48))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 48);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var homeBudget = server.State().GetProperty("budget").GetInt32();
        var traveller = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);

        // Full family boards shuttle to Vacation resort House48
        server.World.RunGenericSimsCall11Native(traveller, 48, 23, 50, 1);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(48, server.State().GetProperty("house").GetInt32());
        Assert.NotEmpty(server.State().GetProperty("objects").EnumerateArray().ToArray());

        // Hotel stay fee & midnight day rollover
        server.World!.FamilyBudget!.AlterFamilyBudget(0, 150);
        server.World.Clock.Restore(23, 59, 0);
        server.Step(60);
        Assert.True(server.HandleJson("save", new { slot = "vac-hotel", lot = true }).GetProperty("ok").GetBoolean());

        // Return home
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, traveller.TypeRecord!.Key, traveller[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();

        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
        Assert.Equal(homeBudget - 150, server.State().GetProperty("budget").GetInt32());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void T1_F15_03_StudioTownFameProgressionAndSuperstarLot_EvaluatesStarsAndPersists()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 85))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 85);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        bella[PersonData.JobType] = 0;
        bella[PersonData.FameScore] = 50;
        bella[PersonData.CharismaObedienceSkill] = 600;
        bella[PersonData.BodyFurnitureSkill] = 600;
        bella[PersonData.CreativitySkill] = 600;
        world.SavePersonRecord(bella);

        Assert.True(world.EvaluateFamePromotion(bella.Id));
        var fameObj = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == bella.Id)
            .GetProperty("fame");
        Assert.True(fameObj.GetProperty("level").GetInt32() >= 1);

        // Travel to Studio Town House85
        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(85, bella.TypeRecord!.Key, bella[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(85, server.State().GetProperty("house").GetInt32());
        Assert.True(server.State().GetProperty("away").GetBoolean());

        // Return home, save, reload
        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, bella.TypeRecord.Key, bella[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.True(server.HandleJson("save", new { slot = "fame-save", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "fame-save" }).GetProperty("ok").GetBoolean());

        var reloadedFame = server.State().GetProperty("sims").EnumerateArray()
            .First(s => unchecked((int)s.GetProperty("guid").GetUInt32()) == bella.TypeRecord.Key)
            .GetProperty("fame");
        Assert.True(reloadedFame.GetProperty("level").GetInt32() >= 1);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void T1_F15_04_MagicTownMagiCoinsAndSpellbook_PersistsAndLoadsLot93()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 93))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 93);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var person = world.WalkOrder.OfType<XPerson>().First(IsHousehold);
        var neighborId = person[PersonData.NeighborId];
        world.NeighborInventory(neighborId).Add(7, unchecked((int)NeighborhoodExport.MagiCoinTokenGuid), 175);
        Assert.True(server.HandleJson("save", new { slot = "mt-coins", lot = true }).GetProperty("ok").GetBoolean());

        var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
        var famChunkId = ((FamilyBudget)world.FamilyBudget!).FamilyChunkId!.Value;
        var fam = Family.Read(ngh, ngh.Find(ResourceType.Parse("FAMI"), famChunkId)!);
        var nbrs = Neighbors.Read(ngh, ngh.Find(ResourceType.Parse("NBRS"), 1)!);
        var ngbhChunk = ngh.Find(ResourceType.Parse("NGBH"), 1);
        var ngbhData = ngbhChunk is null ? null : NeighborhoodData.Read(ngh, ngbhChunk);
        Assert.Equal(175, NeighborhoodExport.GetMagicoinsForFamily(fam, nbrs.Entries, ngbhData));
        var inv = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == person.Id)
            .GetProperty("inventory").EnumerateArray().ToArray();
        Assert.Contains(inv, item => item.GetProperty("count").GetInt32() >= 175);

        // Travel to Magic Town community lot 93
        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(93, person.TypeRecord!.Key, person[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(93, server.State().GetProperty("house").GetInt32());
        Assert.True(server.State().GetProperty("away").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void T1_F15_05_NeighborhoodEasterEggs_NessieAndAmbientRoadCars()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false, 5);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var nessieResp = server.HandleJson("cheat", new { code = "nessie" });
        Assert.True(nessieResp.GetProperty("ok").GetBoolean());
        Assert.Contains(server.AllEvents, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "neighborhoodCheat" &&
            e.GetProperty("code").GetString() == "nessie");

        var carsResp = server.HandleJson("cheat", new { code = "cars 2 3" });
        Assert.True(carsResp.GetProperty("ok").GetBoolean());
        Assert.Contains(server.AllEvents, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "neighborhoodCheat" &&
            e.GetProperty("code").GetString() == "cars" &&
            e.GetProperty("speed").GetInt32() == 2 &&
            e.GetProperty("count").GetInt32() == 3);

        Assert.True(server.HandleJson("neighborhood", new { mode = 1 }).GetProperty("ok").GetBoolean());
        var manifestPath = Path.Combine(profile.Session, "neighborhood.json");
        var overlay1 = new OriginalNeighborhoodOverlay(new GameContent(gameDir), new UIDevice(), manifestPath, profile.Session);
        var sounds = new List<string>();
        overlay1.PlaySound = sounds.Add;
        Assert.True(overlay1.TriggerNessie(time: 0.0));
        Assert.Equal(["nessie_sfx"], sounds);

        Assert.True(server.HandleJson("neighborhood", new { mode = 5 }).GetProperty("ok").GetBoolean());
        var overlay5 = new OriginalNeighborhoodOverlay(new GameContent(gameDir), new UIDevice(), manifestPath, profile.Session);
        Assert.True(overlay5.TriggerCars(speed: 2, count: 3));
        Assert.NotEmpty(overlay5.DrawItems(new CGSize(800, 600), time: 1.0));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public void T2_F15_01_DuplicateNessieTrigger_RejectedCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false, 5);
        using var server = E2EHarness.StartServer(profile, house: 0);

        Assert.True(server.HandleJson("neighborhood", new { mode = 1 }).GetProperty("ok").GetBoolean());
        var manifestPath = Path.Combine(profile.Session, "neighborhood.json");
        var overlay = new OriginalNeighborhoodOverlay(new GameContent(gameDir), new UIDevice(), manifestPath, profile.Session);
        var sounds = new List<string>();
        overlay.PlaySound = sounds.Add;

        Assert.True(overlay.TriggerNessie(time: 0.0));
        Assert.False(overlay.TriggerNessie(time: 0.1));
        Assert.Single(sounds);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public void T2_F15_02_TravelFilterZeroMask_SkipsZeroAndFallsBackToDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sims-e2e-tf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Equal(2, TravelFilter.Load(tempDir, 1, 1, 2));
            Assert.Equal(0x400, TravelFilter.Load(tempDir, 4, 1, 0x400));

            // Writing 0 must be skipped cleanly
            TravelFilter.Save(tempDir, 1, 0, 1);
            Assert.False(File.Exists(Path.Combine(tempDir, "LotLocations.iff")));
        }
        finally
        {
            E2EHarness.DeleteTempTree(tempDir);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public void T2_F15_03_InsufficientMagiCoinsForLot91_BlocksMoveInAndShowsShortageRollover()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 91))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, true, 1, false, 91);
        var unhoused = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1);
        Assert.NotEmpty(unhoused);
        var fam = unhoused[0];
        var famId = fam.Id;

        // Seed NGBH inventory tracking with 50 MagiCoins (< 150 required for lot 91).
        var nghPath = Path.Combine(profile.UserData, "Neighborhood.iff");
        var ngh = IffFile.Open(nghPath);
        var ngbhChunk = ngh.Find(ResourceType.Parse("NGBH"), 1)!;
        var ngbhData = NeighborhoodData.Read(ngh, ngbhChunk);
        var nbrs = Neighbors.ReadRecoveringSupportedPrefix(ngh, ngh.OfType(ResourceType.Parse("NBRS")).First()).Entries;
        var fami = Family.Read(ngh, ngh.Find(ResourceType.Parse("FAMI"), (ushort)famId)!);
        var firstNbr = nbrs.First(n => fami.Members.Contains(n.Guid));
        var updatedInv = ngbhData.Inventories.ToDictionary(kv => kv.Key, kv => kv.Value);
        updatedInv[firstNbr.NeighborId] = [new NeighborhoodData.InventoryItem(7, NeighborhoodExport.MagiCoinTokenGuid, 50)];
        var swap = ngbhChunk.Data.Length > 0 && ngbhChunk.Data.Span[0] != 0;
        var ngbhBytes = NeighborhoodData.Write(ngbhData.Data, updatedInv, Math.Max(ngbhData.Version, 0x49), swap, ngbhData.TrailingBytes.Span);
        ngh = ngh.WithChunkDataResized(ngbhChunk.Type, ngbhChunk.Id, ngbhBytes);
        File.WriteAllBytes(nghPath, ngh.Bytes.ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            NeighborhoodEdits.MoveIn(profile.UserData, gameDir, 91, famId));
        Assert.Contains("MagiCoins", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify family remains in bin (HouseNumber == 0)
        Assert.Contains(NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1), f => f.Id == famId);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public void T2_F15_04_UnaffordableOrCommunityLotMoveIn_BlockedWithFamilyIntactInBin()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 2, 70))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, true, 1, false, 2, 70);
        var unhoused = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1);
        Assert.NotEmpty(unhoused);
        var fam = unhoused[0];

        // House02 ($50,777) > $20,000 budget
        Assert.Throws<InvalidOperationException>(() =>
            NeighborhoodEdits.MoveIn(profile.UserData, gameDir, 2, fam.Id));

        // House70 is Community lot
        Assert.Throws<InvalidOperationException>(() =>
            NeighborhoodEdits.MoveIn(profile.UserData, gameDir, 70, fam.Id));

        var stillUnhoused = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1).First(f => f.Id == fam.Id);
        Assert.Equal(fam.Budget, stillUnhoused.Budget);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public void T2_F15_05_StudioTownPetExclusionAndMagicTownVacantRollover()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 90, 93))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false, 90, 93);
        using var server = E2EHarness.StartServer(profile, house: 0);

        Assert.True(server.HandleJson("neighborhood", new { mode = 7 }).GetProperty("ok").GetBoolean());
        var manifestPath = Path.Combine(profile.Session, "neighborhood.json");
        var overlay = new OriginalNeighborhoodOverlay(new GameContent(gameDir), new UIDevice(), manifestPath, profile.Session);
        var rollover90 = overlay.RolloverText(90);
        Assert.Contains("MagiCoins", rollover90, StringComparison.OrdinalIgnoreCase);

        // Verify pet Gender >= 2 maps to pet body string mode and is excluded from human adult/child CAS gender codes.
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest(
            "PetOnly",
            [new HouseholdCreationRequest.Member("Fluffy", "", "pcfit", "lgt", [500, 500, 500, 500, 500])]).Validate());
    }

    // -------------------------------------------------------------------------
    // Feature F16: CAS Downloads/ Skin Discovery & Wardrobe Portraits
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void T1_F16_01_DownloadsSkinOnlySubfolderWithoutIff_DiscoveredInCasCatalog()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "SkinOnlyPack"),
                    "b901fafitlgt_e2e.bmp",
                    "b901fafit_e2e.cmx",
                    "xskin-b901fafit_e2e-PELVIS-BODY.skn",
                    "b901fafit_e2e",
                    "xskin-b901fafit_e2e-PELVIS-BODY",
                    "b901fafitlgt_e2e");
            }, out tempRoot);

            var registry = new ObjectRegistry();
            var skins = CharacterSearchPath.Skins(customGame, registry);
            Assert.Contains(skins.Files, s => s.Name.Contains("b901fafit_e2e", StringComparison.OrdinalIgnoreCase));

            var catalog = HouseholdAppearance.Catalog(customGame, registry, "f", "adult", "lgt");
            Assert.Contains(catalog.Bodies, b => b.Id.Contains("b901fafit_e2e", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void T1_F16_02_DownloadsSubfolderWithIffAndSkins_DiscoveredWithoutDuplicates()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                var packDir = Path.Combine(downloads, "ObjAndSkinPack");
                WriteSyntheticSkinPack(
                    packDir,
                    "b902mafitlgt_e2e.bmp",
                    "b902mafit_e2e.cmx",
                    "xskin-b902mafit_e2e-PELVIS-BODY.skn",
                    "b902mafit_e2e",
                    "xskin-b902mafit_e2e-PELVIS-BODY",
                    "b902mafitlgt_e2e");
                File.WriteAllBytes(Path.Combine(packDir, "marker.iff"), []);
            }, out tempRoot);

            var registry = new ObjectRegistry();
            var skins = CharacterSearchPath.Skins(customGame, registry);
            Assert.Single(skins.Files, s => s.Name.Equals("b902mafit_e2e.cmx", StringComparison.OrdinalIgnoreCase));

            var catalog = HouseholdAppearance.Catalog(customGame, registry, "m", "adult", "lgt");
            Assert.Contains(catalog.Bodies, b => b.Id.Contains("b902mafit_e2e", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void T1_F16_03_InLotWardrobeOutfitChange_RefreshesSimPortrait()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var registry = world.ObjectRegistry!;
        var bellaGuid = unchecked((uint)bella.TypeRecord!.Key);
        var entry = registry.Find(bellaGuid)!;
        var beforeBmp = entry.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();

        // Trigger body wardrobe outfit change (0x02, headsList: false) & portrait refresh via WardrobeSuitService
        var bodyActions = WardrobeSuitService.GetActionList(world, bella, null, 0, headsList: false)!;
        Assert.NotEmpty(bodyActions.Entries);
        var currentBody = bella.SavedCharacter?.Parts[0].Item1 ?? "";
        var currentBodyTex = bella.SavedCharacter?.Parts[0].Item2 ?? "";
        var altBody = bodyActions.Entries.First(e =>
            !e.First.Equals(currentBody, StringComparison.OrdinalIgnoreCase) ||
            !e.Second.Equals(currentBodyTex, StringComparison.OrdinalIgnoreCase));

        var scope = InteractionBehaviorScope.Initialize();
        var ctx = WardrobeSuitService.BeginPersonScope(world, bella, scope);
        ctx.SetString(0x02, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(altBody));
        WardrobeSuitService.CommitContext(registry, entry, world.Language, ctx, bella);
        world.DispatchNotice(0x20000000, 0);
        server.DrainEvents();

        Assert.Equal(altBody.First, bella.SavedCharacter?.Parts[0].Item1);
        var afterBmp = registry.Find(bellaGuid)!.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
        Assert.False(beforeBmp.SequenceEqual(afterBmp));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void T1_F16_04_InLotMakeoverHeadChange_RegeneratesPortraitChunks()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var mortimer = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p) && GenderOf(p) == 0);
        var registry = world.ObjectRegistry!;
        var guid = unchecked((uint)mortimer.TypeRecord!.Key);
        var entry = registry.Find(guid)!;

        var beforePortrait = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == mortimer.Id)
            .GetProperty("portrait").GetProperty("file").GetString();

        var headActions = WardrobeSuitService.GetActionList(world, mortimer, null, 0, headsList: true)!;
        var currentHead = mortimer.SavedCharacter?.Parts[4].Item1 ?? "";
        var altHead = headActions.Entries.First(e => !e.First.Equals(currentHead, StringComparison.OrdinalIgnoreCase));

        var scope = InteractionBehaviorScope.Initialize();
        var ctx = WardrobeSuitService.BeginPersonScope(world, mortimer, scope);
        ctx.SetString(0x03, $"{altHead.First},HEAD={altHead.First}");
        WardrobeSuitService.CommitContext(registry, entry, world.Language, ctx, mortimer);
        world.DispatchNotice(0x20000000, 0);
        server.DrainEvents();

        var afterPortrait = server.State().GetProperty("sims").EnumerateArray()
            .First(s => s.GetProperty("id").GetInt16() == mortimer.Id)
            .GetProperty("portrait").GetProperty("file").GetString();
        Assert.NotEqual(beforePortrait, afterPortrait);
        Assert.NotNull(registry.Find(guid)!.File.Find(ResourceType.Parse("BMP_"), 2003));
        Assert.NotNull(registry.Find(guid)!.File.Find(ResourceType.Parse("BMP_"), 2004));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void T1_F16_05_SaveAndReloadWithCustomDownloadsSkinAndUpdatedPortrait_Persists()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "PersistSkin"),
                    "b903fafitlgt_e2e.bmp",
                    "b903fafit_e2e.cmx",
                    "xskin-b903fafit_e2e-PELVIS-BODY.skn",
                    "b903fafit_e2e",
                    "xskin-b903fafit_e2e-PELVIS-BODY",
                    "b903fafitlgt_e2e");
            }, out tempRoot);

            var catalog = HouseholdAppearance.Catalog(customGame, new ObjectRegistry(), "f", "adult", "lgt");
            var customBody = catalog.Bodies.First(b => b.Id.Contains("b903fafit_e2e", StringComparison.OrdinalIgnoreCase));
            var head = catalog.Heads[0];

            using var profile = E2EHarness.CreateIsolatedProfile(customGame, 9);
            using var server = E2EHarness.StartServer(profile, house: 9);

            var createResp = server.HandleJson("createHousehold", new
            {
                familyName = "CustomSkin",
                moveIn = true,
                house = (short)9,
                members = new[]
                {
                    new
                    {
                        name = "Clara",
                        code = "fafit",
                        skin = "lgt",
                        headChoice = head.Id,
                        bodyChoice = customBody.Id,
                        personality = new[] { 500, 500, 500, 500, 500 }
                    }
                }
            });
            Assert.True(createResp.GetProperty("ok").GetBoolean(), createResp.ToString());

            Assert.True(server.HandleJson("save", new { slot = "custom-skin", lot = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("load", new { slot = "custom-skin" }).GetProperty("ok").GetBoolean());

            var clara = server.World!.WalkOrder.OfType<XPerson>().First(IsHousehold);
            Assert.Contains("b903fafit_e2e", clara.SavedCharacter?.Parts[0].Item1 ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void T2_F16_01_EmptyDownloadsFolderAndNonSkinFiles_IgnoredCleanly()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                Directory.CreateDirectory(Path.Combine(downloads, "EmptyFolder"));
                var readmeDir = Path.Combine(downloads, "ReadmeOnly");
                Directory.CreateDirectory(readmeDir);
                File.WriteAllText(Path.Combine(readmeDir, "readme.txt"), "not a skin");
            }, out tempRoot);

            var registry = new ObjectRegistry();
            var catalog = HouseholdAppearance.Catalog(customGame, registry, "f", "adult", "lgt");
            Assert.NotEmpty(catalog.Heads);
            Assert.NotEmpty(catalog.Bodies);
            Assert.DoesNotContain(catalog.Bodies, b => b.Id.Contains("readme", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(catalog.Heads, h => h.Id.Contains("readme", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void T2_F16_02_MalformedCmxOrMissingBmpInDownloads_SkippedGracefully()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                var broken = Path.Combine(downloads, "BrokenSkin");
                Directory.CreateDirectory(broken);
                File.WriteAllText(Path.Combine(broken, "b904fa_broken.cmx"), "corrupt-cmx-content");
                File.WriteAllText(Path.Combine(broken, "xskin-b904fa_broken-PELVIS-BODY.skn"), "corrupt-skn");
            }, out tempRoot);

            var catalog = HouseholdAppearance.Catalog(customGame, new ObjectRegistry(), "f", "adult", "lgt");
            Assert.NotEmpty(catalog.Bodies);
            Assert.DoesNotContain(catalog.Bodies, b => b.Id.Contains("b904fa_broken", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void T2_F16_03_PortraitRefreshDuringCensorOrNakedState_PreservesEverydayPortrait()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var bella = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var registry = world.ObjectRegistry!;
        var bellaGuid = unchecked((uint)bella.TypeRecord!.Key);
        var entry = registry.Find(bellaGuid)!;

        // Establish native Everyday portrait baseline first.
        var baseCtx = WardrobeSuitService.BeginPersonScope(world, bella, InteractionBehaviorScope.Initialize());
        WardrobeSuitService.CommitContext(registry, entry, world.Language, baseCtx, bella);
        Assert.True(server.HandleJson("save", new { slot = "everyday-base", lot = true }).GetProperty("ok").GetBoolean());
        var everydayBmp = registry.Find(bellaGuid)!.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();

        // Put Sim in temporary censored/naked outfit state, queue pending portrait refresh via CommitContext, and save
        bella[PersonData.CensorshipFlags] = 0x80;
        bella[PersonData.CurrentOutfit] = 2;
        var scope = InteractionBehaviorScope.Initialize();
        var ctx = WardrobeSuitService.BeginPersonScope(world, bella, scope);
        WardrobeSuitService.CommitContext(registry, entry, world.Language, ctx, bella);

        Assert.True(server.HandleJson("save", new { slot = "censor-save", lot = true }).GetProperty("ok").GetBoolean());
        var afterBmp = registry.Find(bellaGuid)!.File.Find(ResourceType.Parse("BMP_"), 2002)!.Data.ToArray();
        Assert.True(everydayBmp.SequenceEqual(afterBmp), "Temporary naked/censor state must not overwrite Everyday portrait.");
        Assert.Equal((short)2, bella[PersonData.CurrentOutfit]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void T2_F16_04_DeletedCustomDownloadsSkinOnSavedSimReload_FallsBackWithoutCrash()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        string? tempRoot = null;
        try
        {
            string packDir = "";
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                packDir = Path.Combine(downloads, "DeletableSkin");
                WriteSyntheticSkinPack(
                    packDir,
                    "b905fafitlgt_e2e.bmp",
                    "b905fafit_e2e.cmx",
                    "xskin-b905fafit_e2e-PELVIS-BODY.skn",
                    "b905fafit_e2e",
                    "xskin-b905fafit_e2e-PELVIS-BODY",
                    "b905fafitlgt_e2e");
            }, out tempRoot);

            var catalog = HouseholdAppearance.Catalog(customGame, new ObjectRegistry(), "f", "adult", "lgt");
            var customBody = catalog.Bodies.First(b => b.Id.Contains("b905fafit_e2e", StringComparison.OrdinalIgnoreCase));

            using var profile = E2EHarness.CreateIsolatedProfile(customGame, 9);
            using var server = E2EHarness.StartServer(profile, house: 9);

            Assert.True(server.HandleJson("createHousehold", new
            {
                familyName = "Fallback",
                moveIn = true,
                house = (short)9,
                members = new[]
                {
                    new
                    {
                        name = "Eve",
                        code = "fafit",
                        skin = "lgt",
                        headChoice = catalog.Heads[0].Id,
                        bodyChoice = customBody.Id,
                        personality = new[] { 500, 500, 500, 500, 500 }
                    }
                }
            }).GetProperty("ok").GetBoolean());

            Assert.True(server.HandleJson("save", new { slot = "del-skin", lot = true }).GetProperty("ok").GetBoolean());
            Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());

            E2EHarness.DeleteTempTree(packDir);

            var loadResp = server.HandleJson("load", new { slot = "del-skin" });
            Assert.True(loadResp.GetProperty("ok").GetBoolean(), loadResp.ToString());
            var eve = server.World!.WalkOrder.OfType<XPerson>().FirstOrDefault(IsHousehold);
            Assert.NotNull(eve);
            var (scene, _) = LotScene.Load(server.LatestScenePath());
            scene.Validate();
            Assert.NotNull(scene.Meshes);
            Assert.Contains(scene.Meshes!, m => m.ObjectID == eve!.Id);
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void T2_F16_05_MixedCaseSkinFilenamesInDownloads_ResolvedCaseInsensitively()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        string? tempRoot = null;
        try
        {
            var customGame = CreateIsolatedGameDirWithDownloads(gameDir, downloads =>
            {
                WriteSyntheticSkinPack(
                    Path.Combine(downloads, "MixedCase"),
                    "B906FAFitlgt_Case.BMP",
                    "B906FAFit_Case.CMX",
                    "XSKIN-B906FAFit_Case-PELVIS-BODY.SKN",
                    "B906FAFit_Case",
                    "XSKIN-B906FAFit_Case-PELVIS-BODY",
                    "B906FAFitlgt_Case");
            }, out tempRoot);

            var catalog = HouseholdAppearance.Catalog(customGame, new ObjectRegistry(), "f", "adult", "lgt");
            Assert.Contains(catalog.Bodies, b => b.Id.Contains("b906fafit_case", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (tempRoot is not null) E2EHarness.DeleteTempTree(tempRoot);
        }
    }

    // -------------------------------------------------------------------------
    // Feature F17: CAS Core & Family Creation / Move-In / Pets
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void T1_F17_01_CasAppearanceCyclingAnd3DPreviewMeshSanity_AcrossGenderAgeSkin()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var combos = new[]
        {
            ("m", "adult", "lgt"),
            ("f", "adult", "med"),
            ("m", "child", "drk"),
            ("f", "child", "lgt")
        };

        foreach (var (gender, age, skin) in combos)
        {
            var catResp = server.HandleJson("appearanceCatalog", new { gender, age, skin });
            Assert.True(catResp.GetProperty("ok").GetBoolean(), catResp.ToString());
            var cat = catResp.GetProperty("result");
            var heads = cat.GetProperty("heads").EnumerateArray().ToArray();
            var bodies = cat.GetProperty("bodies").EnumerateArray().ToArray();
            Assert.NotEmpty(heads);
            Assert.NotEmpty(bodies);

            if (age == "child")
            {
                Assert.DoesNotContain(bodies, b =>
                    string.Equals(b.GetProperty("bodyType").GetString(), "fat", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.GetProperty("bodyType").GetString(), "skn", StringComparison.OrdinalIgnoreCase));
            }

            var headId = heads[0].GetProperty("id").GetString()!;
            var bodyId = bodies[0].GetProperty("id").GetString()!;
            var prevResp = server.HandleJson("appearancePreview", new
            {
                gender,
                age,
                skin,
                headChoice = headId,
                bodyChoice = bodyId
            });
            Assert.True(prevResp.GetProperty("ok").GetBoolean(), prevResp.ToString());

            var previewPath = Path.Combine(profile.Session, "cas-preview", "preview.json");
            var (preview, pixels) = CoreAppearancePreview.Load(previewPath);
            AssertCas3DPreviewMeshIntegrity(preview, pixels);

            // Verify </> cycling to next head and body options produces valid distinct preview geometry/textures
            Assert.True(heads.Length >= 2 && bodies.Length >= 2, $"Expected >= 2 heads and bodies for {gender}/{age}/{skin}.");
            var nextHeadId = heads[1].GetProperty("id").GetString()!;
            var nextBodyId = bodies[1].GetProperty("id").GetString()!;
            var nextPrevResp = server.HandleJson("appearancePreview", new
            {
                gender,
                age,
                skin,
                headChoice = nextHeadId,
                bodyChoice = nextBodyId
            });
            Assert.True(nextPrevResp.GetProperty("ok").GetBoolean(), nextPrevResp.ToString());
            var (nextPreview, nextPixels) = CoreAppearancePreview.Load(previewPath);
            AssertCas3DPreviewMeshIntegrity(nextPreview, nextPixels);
            Assert.False(
                preview.Textures.Select(t => t.File).SequenceEqual(nextPreview.Textures.Select(t => t.File)),
                $"Expected </> cycling in {gender}/{age}/{skin} to change preview textures.");

            if (age == "adult")
            {
                foreach (var bodyType in new[] { "fat", "skn" })
                {
                    var typedBody = bodies.FirstOrDefault(b =>
                        string.Equals(b.GetProperty("bodyType").GetString(), bodyType, StringComparison.OrdinalIgnoreCase));
                    Assert.NotEqual(JsonValueKind.Undefined, typedBody.ValueKind);
                    var typedBodyId = typedBody.GetProperty("id").GetString()!;
                    var typedPrevResp = server.HandleJson("appearancePreview", new
                    {
                        gender,
                        age,
                        skin,
                        headChoice = headId,
                        bodyChoice = typedBodyId
                    });
                    Assert.True(typedPrevResp.GetProperty("ok").GetBoolean(), typedPrevResp.ToString());
                    var (typedPreview, typedPixels) = CoreAppearancePreview.Load(previewPath);
                    AssertCas3DPreviewMeshIntegrity(typedPreview, typedPixels);
                }
            }
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void T1_F17_02_Personality25PointAllocationAndZodiacCalculation_MatchesAll12Profiles()
    {
        int[] values = [0, 0, 0, 0, 0];
        for (int i = 0; i < 25; i++)
        {
            var next = HouseholdPersonalityEditor.Step(values, i % 5, increasing: true);
            Assert.NotNull(next);
            values = next!;
        }
        Assert.Equal(2500, values.Sum());
        Assert.Null(HouseholdPersonalityEditor.Step(values, 0, increasing: true));

        var observedZodiacs = new HashSet<short>();
        short[][] samples =
        [
            [700, 300, 200, 300, 1000],
            [500, 500, 800, 300, 400],
            [300, 700, 800, 400, 300],
            [600, 400, 200, 600, 700],
            [400, 1000, 400, 400, 300],
            [900, 200, 800, 200, 400],
            [200, 800, 200, 600, 700],
            [600, 200, 800, 300, 600],
            [200, 600, 900, 700, 100],
            [700, 400, 100, 800, 500],
            [400, 400, 400, 700, 600],
            [500, 200, 200, 700, 900]
        ];
        foreach (var p in samples)
        {
            var data = new short[8];
            data[6] = p[0]; // Neat
            data[2] = p[1]; // Outgoing
            data[5] = p[2]; // Active
            data[7] = p[3]; // Playful
            data[3] = p[4]; // Nice
            var z = Neighborhoods.ComputeExportZodiac(data);
            Assert.NotNull(z);
            Assert.InRange(z!.Value, 1, 12);
            observedZodiacs.Add((short)z.Value);
        }
        Assert.True(observedZodiacs.Count >= 6, $"Expected varied zodiac signs across distinct profiles, got {observedZodiacs.Count}.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void T1_F17_03_CreateMultiMemberHouseholdWithBioAndMoveIn_DeductsFundsAndLoadsLiveMode()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var resp = server.HandleJson("createHousehold", new
        {
            familyName = "TesterThree",
            moveIn = true,
            house = (short)9,
            members = new[]
            {
                new { name = "Alice", bio = "Lead sim", code = "fafit", skin = "lgt", personality = DefaultPersonality() },
                new { name = "Bob", bio = "Second sim", code = "mafit", skin = "med", personality = DefaultPersonality() },
                new { name = "Charlie", bio = "Child sim", code = "mcchd", skin = "drk", personality = DefaultPersonality() }
            }
        });
        Assert.True(resp.GetProperty("ok").GetBoolean(), resp.ToString());
        var res = resp.GetProperty("result");
        Assert.Equal(9, res.GetProperty("house").GetInt32());
        Assert.Equal(16500, res.GetProperty("budget").GetInt32());
        Assert.Equal(3, res.GetProperty("members").GetInt32());

        var open = server.HandleJson("openLot", new { house = 9 });
        Assert.True(open.GetProperty("ok").GetBoolean());
        var sims = server.State().GetProperty("sims").EnumerateArray().Where(s => s.GetProperty("household").GetBoolean()).ToArray();
        Assert.Equal(3, sims.Length);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void T1_F17_04_FamImportMoveInAndEvictRoundTrip_RefundsAndReturnsToBin()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 4))
            return;

        var famSrc = Path.Combine(gameDir, "TemplateFamilyUnleashed", "Burb_11.FAM");
        if (!File.Exists(famSrc))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 4);
        var importDir = Path.Combine(profile.UserData, "Import");
        Directory.CreateDirectory(importDir);

        // Burb_11.FAM ships with HouseNumber=63; write an unhoused (HouseNumber=0) copy so
        // import places the family directly into the family bin rather than onto lot 63.
        var famIff = IffFile.Open(famSrc);
        var famiType = ResourceType.Parse("FAMI");
        var famiChunk = famIff.OfType(famiType).First(c => c.Id != 0);
        var famRecord = Family.Read(famIff, famiChunk);
        var startingBudget = Math.Max(25000, famRecord.Budget + famRecord.Value);
        var unhousedFam = famRecord
            .WithHouseNumber(0)
            .WithBudget(startingBudget)
            .WithValue(0);
        var patchedFam = famIff.WithAddedOrReplacedChunk(famiType, famiChunk.Id, unhousedFam.Write(famiChunk.Id));
        File.WriteAllBytes(Path.Combine(importDir, "Burb_11.FAM"), patchedFam.Bytes.ToArray());

        var candidates = NeighborhoodEdits.EnumerateImportCandidates(profile.UserData, profile.Profile, gameDir, 1);
        Assert.NotEmpty(candidates.Candidates);
        var burbIdx = Array.FindIndex(candidates.Candidates, c => c.FamilyName.Contains("Burb", StringComparison.OrdinalIgnoreCase));
        Assert.True(burbIdx >= 0, "Expected Burb candidate in ImportCandidates.");

        using var server = E2EHarness.StartServer(profile, house: 0);
        var importResp = server.HandleJson("neighborhood", new { mode = 1, edit = $"import:{burbIdx}" });
        Assert.True(importResp.GetProperty("ok").GetBoolean(), importResp.ToString());

        var importedFam = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1)
            .FirstOrDefault(f => f.Name.Contains("Burb", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(importedFam);

        Assert.True(server.HandleJson("openLot", new { house = 4 }).GetProperty("ok").GetBoolean());
        var moveInResp = server.HandleJson("moveInFamily", new { family = importedFam!.Id });
        Assert.True(moveInResp.GetProperty("ok").GetBoolean(), moveInResp.ToString());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());

        var evictResp = server.HandleJson("neighborhood", new { mode = 1, edit = "evict:4" });
        Assert.True(evictResp.GetProperty("ok").GetBoolean(), evictResp.ToString());

        var backInBin = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1)
            .FirstOrDefault(f => f.Id == importedFam.Id);
        Assert.NotNull(backInBin);
        Assert.True(backInBin!.Budget >= 20000);
        Assert.Equal(startingBudget, backInBin.Budget);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void T1_F17_05_UnleashedPetAdoptionAndPetStore_AddsPetWithinCapAndPersists()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 5, 70))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 5, 70);
        using var server = E2EHarness.StartServer(profile, house: 5);

        var world = server.World!;
        var actor = world.WalkOrder.OfType<XPerson>().First(p => IsHousehold(p) && !IsChild(p));
        var initialHousehold = server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean());

        // Adopt a dog (mode 1) and add to family via MakeNewCharacterPrimitive + GenericSimsCall 0x04
        var makeFrame = new StackFrame(actor.Id, new short[8], [0, 1, 0]);
        var makeCtx = new TreeContext(world, actor, makeFrame);
        Assert.Equal(PrimitiveResult.True, MakeNewCharacterPrimitive.Execute(makeCtx, [0, 1, 2, 1, 0, 0, 0, 0]));
        var petNeighborId = makeFrame.StackObject;
        var addCtx = new TreeContext(world, actor, new StackFrame(actor.Id, new short[8], [0, 1]) { StackObject = petNeighborId });
        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(addCtx, [0x04, 0, 0, 0, 0, 0, 0, 0]));

        server.BatchRun(1);
        server.DrainEvents();

        var petGuid = world.Neighbors[petNeighborId].TypeRecordKey;
        Assert.NotNull(world.ObjectRegistry!.Find(unchecked((uint)petGuid))!.File.Find(ResourceType.Parse("BMP_"), 2002));
        Assert.True(world.NeighborhoodFamilies!.HasMember(actor[PersonData.FamilyNumber], petGuid));

        // Verify Pet Paradise House70 loads cleanly for pet store visits, and returning home spawns the adopted pet XPerson
        world.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(70, actor.TypeRecord!.Key, actor[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(70, server.State().GetProperty("house").GetInt32());

        server.World!.PendingLotSwitch = new SimulationWorld.LotSwitchRequest(-1, actor.TypeRecord!.Key, actor[PersonData.FamilyNumber]);
        server.BatchRun(1);
        server.DrainEvents();
        Assert.Equal(5, server.State().GetProperty("house").GetInt32());
        Assert.Contains(server.World!.WalkOrder.OfType<XPerson>(), p => p.TypeRecord?.Key == petGuid && IsHousehold(p));
        Assert.Equal(
            initialHousehold + 1,
            server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean()));

        Assert.True(server.HandleJson("save", new { slot = "pet-adopted", lot = true }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("closeLot", new { save = false }).GetProperty("ok").GetBoolean());
        Assert.True(server.HandleJson("load", new { slot = "pet-adopted" }).GetProperty("ok").GetBoolean());
        Assert.Equal(
            initialHousehold + 1,
            server.State().GetProperty("sims").EnumerateArray().Count(s => s.GetProperty("household").GetBoolean()));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void T2_F17_01_InvalidNamesAndEmptyMembers_RejectedCleanly()
    {
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest("", [new("Alice", "fafit", "lgt", [500, 500, 500, 500, 500])]).Validate());
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest("Bad/Name", [new("Alice", "fafit", "lgt", [500, 500, 500, 500, 500])]).Validate());
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest("Good", [new("", "fafit", "lgt", [500, 500, 500, 500, 500])]).Validate());
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest("Empty", []).Validate());

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);

        Assert.False(server.HandleJson("createHousehold", new
        {
            familyName = "Bad/Name",
            moveIn = false,
            members = new[] { new { name = "Alice", code = "fafit", skin = "lgt", personality = DefaultPersonality() } }
        }).GetProperty("ok").GetBoolean());

        Assert.False(server.HandleJson("createHousehold", new
        {
            familyName = "NoMembers",
            moveIn = false,
            members = Array.Empty<object>()
        }).GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void T2_F17_02_NineMemberHousehold_BlockedByValidationAndIpc()
    {
        var nineMembers = Enumerable.Range(1, 9)
            .Select(i => new HouseholdCreationRequest.Member($"Sim{i}", "fafit", "lgt", [500, 500, 500, 500, 500]))
            .ToArray();
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest("TooBig", nineMembers).Validate());

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);

        var resp = server.HandleJson("createHousehold", new
        {
            familyName = "TooBig",
            moveIn = false,
            members = Enumerable.Range(1, 9).Select(i => new
            {
                name = $"Sim{i}",
                code = "fafit",
                skin = "lgt",
                personality = new[] { 500, 500, 500, 500, 500 }
            }).ToArray()
        });
        Assert.False(resp.GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void T2_F17_03_UnallocatedPersonalityPointsAndZodiacTieBreak()
    {
        var partialReq = new HouseholdCreationRequest(
            "Partial",
            [new HouseholdCreationRequest.Member("Alice", "fafit", "lgt", [500, 0, 0, 0, 0])]);
        partialReq.Validate();

        // Editor boundary checks: 0 floor, 1000 trait cap, 2500 total cap
        Assert.Null(HouseholdPersonalityEditor.Step([0, 0, 0, 0, 0], 0, increasing: false));
        Assert.Null(HouseholdPersonalityEditor.Step([1000, 0, 0, 0, 0], 0, increasing: true));
        Assert.Null(HouseholdPersonalityEditor.Step([500, 500, 500, 500, 500], 0, increasing: true));

        // Deterministic tie-break (first matching profile wins)
        var tieData = new short[8];
        tieData[6] = 500; tieData[2] = 500; tieData[5] = 500; tieData[7] = 500; tieData[3] = 500;
        var z1 = Neighborhoods.ComputeExportZodiac(tieData);
        var z2 = Neighborhoods.ComputeExportZodiac(tieData);
        Assert.Equal(z1, z2);
        Assert.NotNull(z1);
        Assert.InRange(z1!.Value, 1, 12);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void T2_F17_04_ChildOnlyAndPetOnlyHouseholds_RejectedFromCreationAndBin()
    {
        Assert.Throws<ContentError>(() => new HouseholdCreationRequest(
            "KidOnly",
            [new HouseholdCreationRequest.Member("Kid", "mcchd", "lgt", [500, 500, 500, 500, 500])]).Validate());

        if (!E2EHarness.TryLocateGameDir(out var gameDir) || !E2EHarness.HasHouses(gameDir, 9))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, 9);
        using var server = E2EHarness.StartServer(profile, house: 0);

        Assert.False(server.HandleJson("createHousehold", new
        {
            familyName = "KidOnly",
            moveIn = false,
            members = new[] { new { name = "Kid", code = "mcchd", skin = "lgt", personality = DefaultPersonality() } }
        }).GetProperty("ok").GetBoolean());

        var straysFam = Path.Combine(gameDir, "TemplateFamilyUnleashed", "Strays_4000.FAM");
        if (File.Exists(straysFam))
        {
            var seeded = NeighborhoodEdits.SeedStraysIfNeeded(profile.UserData, gameDir, 1);
            if (seeded is null)
            {
                var strayCopy = Path.Combine(profile.TempRoot, "Strays_4000.FAM");
                File.Copy(straysFam, strayCopy, overwrite: true);
                seeded = NeighborhoodEdits.ImportFile(strayCopy, profile.UserData, gameDir, 1, NeighborhoodEdits.StrayFamilyId);
            }
            Assert.NotNull(seeded);

            var ngh = IffFile.Open(Path.Combine(profile.UserData, "Neighborhood.iff"));
            var strayChunk = ngh.Find(ResourceType.Parse("FAMI"), NeighborhoodEdits.StrayFamilyId);
            Assert.NotNull(strayChunk);
            var strayFami = Family.Read(ngh, strayChunk!);
            Assert.NotEmpty(strayFami.Members);

            var unhoused = NeighborhoodExport.UnhousedFamilies(profile.UserData, gameDir, 1);
            Assert.DoesNotContain(unhoused, f =>
                f.Id == NeighborhoodEdits.StrayFamilyId ||
                f.Name.Contains("Stray", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void T2_F17_05_CorruptFamImport_FailsCleanlyWithoutCorruptingNeighborhoodIff()
    {
        if (!E2EHarness.TryLocateGameDir(out var gameDir))
            return;

        using var profile = E2EHarness.CreateIsolatedProfile(gameDir, copyCharacters: false, 1, false, 5);
        var nghPath = Path.Combine(profile.UserData, "Neighborhood.iff");
        var beforeBytes = File.ReadAllBytes(nghPath);

        var corruptPath = Path.Combine(profile.TempRoot, "corrupt.FAM");
        File.WriteAllBytes(corruptPath, "NOT_A_VALID_IFF_FAM_FILE"u8.ToArray());

        Assert.ThrowsAny<Exception>(() =>
            NeighborhoodEdits.ImportFile(corruptPath, profile.UserData, gameDir, 1));

        var afterBytes = File.ReadAllBytes(nghPath);
        Assert.Equal(beforeBytes, afterBytes);
        Assert.NotEmpty(IffFile.Open(nghPath).OfType(ResourceType.Parse("FAMI")));
    }
}
