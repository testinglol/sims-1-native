using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using SimsEngine.Simulation.Neighborhood;
using SimsSDL.Core;
using SimsSDL.UI;

namespace SimsSDL;

internal sealed partial class LiveController
{
    private const string ConnectedNeighborhoodPluginId = "connected-neighborhood";

    private readonly record struct PendingConnectedObjectMenu(short House, int ObjectId, CGPoint AnchorLocal, double CreatedAt);

    private bool _connectedNeighborhoodEnabled;
    private bool _connectedAdjacentRendering;
    private short _connectedFocusedHouse;
    private short _connectedHomeHouse;
    private readonly HashSet<short> _connectedInvitedHouses = [];
    private PendingConnectedObjectMenu? _pendingConnectedObjectMenu;
    private string? _pendingAdjacentScenesId;
    private string? _lastAdjacentSyncSignature;
    private string? _pendingAdjacentSyncSignature;
    private double _lastAdjacentPollAt;
    private (int TileX, int TileY)? _lastStreamedCameraTile;
    private double? _holdingInitialLoadForAdjacentSince;
    private short _initialAdjacentSettledHouse;

    public bool ConnectedNeighborhoodEnabled => _connectedNeighborhoodEnabled;
    public short ConnectedFocusedHouse => _connectedFocusedHouse;
    public short ConnectedHomeHouse => _connectedHomeHouse;

    private void ResetConnectedAdjacentHoldState()
    {
        _holdingInitialLoadForAdjacentSince = null;
        _initialAdjacentSettledHouse = 0;
        _pendingAdjacentSyncSignature = null;
    }

    private void FinishInitialAdjacentHold(bool allowTravelDismiss = false)
    {
        _holdingInitialLoadForAdjacentSince = null;
        if (_openingNeighborhoodLot)
        {
            _openingNeighborhoodLot = false;
            if (_pendingHouseholdCreation is null && Renderer.Scene is not null)
                Ui.Neighborhood = null;
        }
        if (Ui.Loading && Bridge is not null && Renderer.Scene is not null &&
            (allowTravelDismiss || Ui.TravelLoadScreen is null))
        {
            Ui.Loading = false;
            _audio?.StopLoadLoop();
        }
    }

    private bool ShouldHoldLoadingForInitialAdjacent()
    {
        if (!_connectedAdjacentRendering ||
            _connectedFocusedHouse <= 0 ||
            !NeighborhoodWorldBlueprint.IsUnifiedMapLot(_connectedFocusedHouse) ||
            _initialAdjacentSettledHouse == _connectedFocusedHouse ||
            Renderer.HasAdjacentNeighborhood ||
            Ui.TravelLoadScreen is not null)
        {
            _holdingInitialLoadForAdjacentSince = null;
            return false;
        }
        double now = CurrentUptime();
        _holdingInitialLoadForAdjacentSince ??= now;
        if (now - _holdingInitialLoadForAdjacentSince.Value >= 6.0)
        {
            if (_connectedFocusedHouse > 0)
                _initialAdjacentSettledHouse = _connectedFocusedHouse;
            FinishInitialAdjacentHold();
            return false;
        }
        if (_pendingAdjacentScenesId is null && _connectedFocusedHouse > 0 && Renderer.Scene is not null)
        {
            _lastAdjacentPollAt = now;
            _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
        }
        return true;
    }

    private void SyncConnectedAdjacentOnSceneReady()
    {
        if (!_connectedAdjacentRendering || _connectedFocusedHouse <= 0 || Renderer.Scene is not { } activeScene)
            return;
        if (activeScene.House > 0 && activeScene.House != _connectedFocusedHouse)
            return;
        TryDispatchPendingConnectedObjectMenu(activeScene.House);
        if (!NeighborhoodWorldBlueprint.IsUnifiedMapLot(_connectedFocusedHouse))
            return;
        if (_pendingAdjacentScenesId is null)
        {
            _lastAdjacentPollAt = CurrentUptime();
            _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
        }
    }

    private void TryDispatchPendingConnectedObjectMenu(int activeHouse)
    {
        if (_pendingConnectedObjectMenu is not { } pending || activeHouse != pending.House)
            return;

        _pendingConnectedObjectMenu = null;
        if (_selectedSim == 0 || CurrentUptime() - pending.CreatedAt >= 3.0)
            return;

        if (Send("menu", new Dictionary<string, object?> { ["objectID"] = pending.ObjectId }) is { } menuId)
        {
            _pendingMenu = new MenuRequest
            {
                Id = menuId,
                Object = pending.ObjectId,
                Caller = _selectedSim,
                Anchor = pending.AnchorLocal,
            };
        }
    }

    private string? RequestAdjacentScenes(bool force = false)
    {
        var args = new Dictionary<string, object?> { ["force"] = force };
        if (Renderer.TryGetCameraCenterLotTile(SimdCanvas, out int camTx, out int camTy))
        {
            args["cameraTileX"] = camTx;
            args["cameraTileY"] = camTy;
            _lastStreamedCameraTile = (camTx, camTy);
        }
        else
        {
            _lastStreamedCameraTile = (32, 32);
        }
        return Send("adjacentScenes", args);
    }

    private void SyncConnectedNeighborhoodState(IReadOnlyDictionary<string, JsonElement> state)
    {
        if (!state.TryGetValue("connectedNeighborhood", out var cnEl) || cnEl.ValueKind != JsonValueKind.Object)
        {
            if (_connectedNeighborhoodEnabled || Renderer.HasAdjacentNeighborhood)
            {
                _connectedNeighborhoodEnabled = false;
                _connectedAdjacentRendering = false;
                _connectedFocusedHouse = 0;
                _connectedHomeHouse = 0;
                _connectedInvitedHouses.Clear();
                _pendingConnectedObjectMenu = null;
                _lastAdjacentSyncSignature = null;
                _lastStreamedCameraTile = null;
                ResetConnectedAdjacentHoldState();
                Renderer.ClearAdjacentNeighborhood();
            }
            return;
        }

        bool enabled = cnEl.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
        bool adjacent = cnEl.TryGetProperty("adjacentRendering", out var adjEl) && adjEl.ValueKind == JsonValueKind.True;
        short focused = cnEl.TryGetProperty("focusedHouse", out var fhEl) && fhEl.TryGetInt16(out var parsedFh) ? parsedFh : (short)0;
        short home = cnEl.TryGetProperty("homeHouse", out var hhEl) && hhEl.TryGetInt16(out var parsedHh) ? parsedHh : (short)0;
        long adjVersion = cnEl.TryGetProperty("adjacentVersion", out var avEl) && avEl.TryGetInt64(out var parsedAv) ? parsedAv : 0L;

        _connectedInvitedHouses.Clear();
        if (cnEl.TryGetProperty("invitedHouses", out var invEl) && invEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var hItem in invEl.EnumerateArray())
            {
                if (hItem.TryGetInt16(out var invH) && invH > 0)
                    _connectedInvitedHouses.Add(invH);
            }
        }

        if (focused != _connectedFocusedHouse || !(enabled && adjacent))
        {
            _initialAdjacentSettledHouse = 0;
            _pendingAdjacentSyncSignature = null;
        }

        _connectedNeighborhoodEnabled = enabled;
        _connectedAdjacentRendering = enabled && adjacent;
        _connectedFocusedHouse = focused;
        _connectedHomeHouse = home;

        bool sceneMatchesFocused = Renderer.Scene is { } currentScene
            && (currentScene.House <= 0 || currentScene.House == focused);

        // Clear any lingering lot-switch loading cursor only once the target lot's scene (and initial neighborhood) is live.
        if (enabled && focused > 0 && sceneMatchesFocused && !_awaitingLotSwitchChange)
        {
            _switchingLot = false;
            if (!ShouldHoldLoadingForInitialAdjacent())
            {
                FinishInitialAdjacentHold();
            }
        }

        if (!_connectedAdjacentRendering || focused <= 0 || Renderer.Scene is not { } activeScene)
        {
            if (Renderer.HasAdjacentNeighborhood)
            {
                _lastAdjacentSyncSignature = null;
                _pendingAdjacentSyncSignature = null;
                _lastStreamedCameraTile = null;
                Renderer.ClearAdjacentNeighborhood();
            }
            return;
        }

        // Wait until LoadScene has installed the new focused house before polling adjacent lots,
        // so ApplySeamlessConnectedLotTransition matches the exact pre-switch adjacent table.
        if (activeScene.House > 0 && activeScene.House != focused)
            return;
        TryDispatchPendingConnectedObjectMenu(activeScene.House);

        if (!NeighborhoodWorldBlueprint.IsUnifiedMapLot(focused))
        {
            _pendingAdjacentSyncSignature = null;
            if (Renderer.HasAdjacentNeighborhood)
            {
                _lastAdjacentSyncSignature = null;
                _lastStreamedCameraTile = null;
                Renderer.ClearAdjacentNeighborhood();
            }
            return;
        }

        string sig = $"{focused}:{(int)activeScene.Camera}:{activeScene.WallMode}:{activeScene.ViewLevel}:{Ui.RoofsVisible}:v{adjVersion}";
        if (!string.Equals(_lastAdjacentSyncSignature, sig, StringComparison.Ordinal))
        {
            if (_pendingAdjacentScenesId is null)
            {
                _lastAdjacentSyncSignature = sig;
                _pendingAdjacentSyncSignature = null;
                _lastAdjacentPollAt = CurrentUptime();
                _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
            }
            else
            {
                _pendingAdjacentSyncSignature = sig;
            }
        }
    }

    private void TickConnectedNeighborhoodRefresh()
    {
        if (_holdingInitialLoadForAdjacentSince is not null)
            _ = ShouldHoldLoadingForInitialAdjacent();

        if (!_connectedAdjacentRendering || _connectedFocusedHouse <= 0 ||
            !NeighborhoodWorldBlueprint.IsUnifiedMapLot(_connectedFocusedHouse) ||
            !Ui.ShowsLot || Renderer.Scene is not { } activeScene ||
            (activeScene.House > 0 && activeScene.House != _connectedFocusedHouse) ||
            _pendingAdjacentScenesId is not null)
        {
            return;
        }

        double now = CurrentUptime();
        bool cameraMovedFar = false;
        if (Renderer.TryGetCameraCenterLotTile(SimdCanvas, out int camTx, out int camTy))
        {
            if (_lastStreamedCameraTile is not { } prev)
            {
                cameraMovedFar = true;
            }
            else
            {
                int dx = camTx - prev.TileX;
                int dy = camTy - prev.TileY;
                cameraMovedFar = dx * dx + dy * dy >= 36 * 36 && now - _lastAdjacentPollAt >= 0.4;
            }
        }

        if (!cameraMovedFar && (Renderer.Speed <= 0 || now - _lastAdjacentPollAt < 8.0))
            return;

        _lastAdjacentPollAt = now;
        _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
    }

    private bool TryHandleAdjacentScenesResponse(string? id, bool ok, IReadOnlyDictionary<string, JsonElement> evt)
    {
        if (id is null || id != _pendingAdjacentScenesId)
            return false;

        _pendingAdjacentScenesId = null;
        if (!ok || !_connectedAdjacentRendering ||
            !evt.TryGetValue("result", out var resultEl) ||
            resultEl.ValueKind != JsonValueKind.Object)
        {
            Log($"adjacentScenes ignored: ok={ok}, adj={_connectedAdjacentRendering}");
            _pendingAdjacentSyncSignature = null;
            if (_connectedFocusedHouse > 0)
                _initialAdjacentSettledHouse = _connectedFocusedHouse;
            if (_holdingInitialLoadForAdjacentSince is not null && Renderer.Scene is not null && Ui.TravelLoadScreen is null)
                FinishInitialAdjacentHold();
            return true;
        }

        short responseHouse = resultEl.TryGetProperty("focusedHouse", out var fhResp) && fhResp.TryGetInt16(out var pfhResp) ? pfhResp : (short)0;
        if (responseHouse > 0 &&
            ((Renderer.Scene is { House: > 0 } curScene && curScene.House != responseHouse) ||
             (_connectedFocusedHouse > 0 && _connectedFocusedHouse != responseHouse)))
        {
            _pendingAdjacentSyncSignature = null;
            if (_connectedFocusedHouse > 0 &&
                NeighborhoodWorldBlueprint.IsUnifiedMapLot(_connectedFocusedHouse) &&
                _pendingAdjacentScenesId is null)
            {
                _lastAdjacentPollAt = CurrentUptime();
                _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
            }
            return true;
        }

        var lots = new List<LotRenderer.AdjacentLotDescriptor>();
        if (resultEl.TryGetProperty("adjacentLots", out var adjArray) && adjArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in adjArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                short h = item.TryGetProperty("house", out var hEl) && hEl.TryGetInt16(out var ph) ? ph : (short)0;
                int ox = item.TryGetProperty("offsetX", out var oxEl) && oxEl.TryGetInt32(out var pox) ? pox : 0;
                int oy = item.TryGetProperty("offsetY", out var oyEl) && oyEl.TryGetInt32(out var poy) ? poy : 0;
                string lotName = item.TryGetProperty("lotName", out var lnEl) && lnEl.ValueKind == JsonValueKind.String
                    ? lnEl.GetString() ?? $"Lot {h}"
                    : $"Lot {h}";
                string? familyName = item.TryGetProperty("familyName", out var fnEl) && fnEl.ValueKind == JsonValueKind.String
                    ? fnEl.GetString()
                    : null;
                bool occupied = item.TryGetProperty("occupied", out var occEl) && occEl.ValueKind == JsonValueKind.True;
                bool isCommunity = item.TryGetProperty("isCommunity", out var commEl) && commEl.ValueKind == JsonValueKind.True;
                bool hasDoor = item.TryGetProperty("hasDoor", out var doorEl) && doorEl.ValueKind == JsonValueKind.True;
                bool isLive = item.TryGetProperty("isLive", out var liveEl) && liveEl.ValueKind == JsonValueKind.True;
                string scenePath = item.TryGetProperty("scenePath", out var spEl) && spEl.ValueKind == JsonValueKind.String
                    ? spEl.GetString() ?? string.Empty
                    : string.Empty;
                long rev = item.TryGetProperty("revision", out var revEl) && revEl.TryGetInt64(out var prev) ? prev : 0L;
                if (h > 0 && !string.IsNullOrWhiteSpace(scenePath))
                {
                    lots.Add(new LotRenderer.AdjacentLotDescriptor(h, ox, oy, lotName, familyName, occupied, hasDoor, isLive, scenePath, rev, isCommunity));
                }
            }
        }

        var strips = new List<LotRenderer.RoadStripDescriptor>();
        if (resultEl.TryGetProperty("roadStrips", out var stripArray) && stripArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stripArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                int minX = item.TryGetProperty("minTileX", out var x0) && x0.TryGetInt32(out var px0) ? px0 : 0;
                int minY = item.TryGetProperty("minTileY", out var y0) && y0.TryGetInt32(out var py0) ? py0 : 0;
                int maxX = item.TryGetProperty("maxTileX", out var x1) && x1.TryGetInt32(out var px1) ? px1 : 0;
                int maxY = item.TryGetProperty("maxTileY", out var y1) && y1.TryGetInt32(out var py1) ? py1 : 0;
                bool horiz = item.TryGetProperty("horizontal", out var hEl) && hEl.ValueKind == JsonValueKind.True;
                bool inter = item.TryGetProperty("isIntersection", out var iEl) && iEl.ValueKind == JsonValueKind.True;
                strips.Add(new LotRenderer.RoadStripDescriptor(minX, minY, maxX, maxY, horiz, inter));
            }
        }

        Vector4? panBounds = null;
        if (resultEl.TryGetProperty("minTileX", out var bMinX) && bMinX.TryGetInt32(out var pMinX) &&
            resultEl.TryGetProperty("minTileY", out var bMinY) && bMinY.TryGetInt32(out var pMinY) &&
            resultEl.TryGetProperty("maxTileX", out var bMaxX) && bMaxX.TryGetInt32(out var pMaxX) &&
            resultEl.TryGetProperty("maxTileY", out var bMaxY) && bMaxY.TryGetInt32(out var pMaxY))
        {
            panBounds = new Vector4(pMinX, pMinY, pMaxX, pMaxY);
        }

        int worldOriginX = resultEl.TryGetProperty("worldOriginX", out var woxEl) && woxEl.TryGetInt32(out var pWox) ? pWox : 0;
        int worldOriginY = resultEl.TryGetProperty("worldOriginY", out var woyEl) && woyEl.TryGetInt32(out var pWoy) ? pWoy : 0;
        int focusedRoadEdge = resultEl.TryGetProperty("focusedRoadEdge", out var freEl) && freEl.TryGetInt32(out var pFre) ? pFre : 2;
        float focusedRoadCenter = resultEl.TryGetProperty("focusedRoadCenter", out var frcEl) && frcEl.TryGetSingle(out var pFrc) ? pFrc : 46.5f;
        int focusedSecondaryRoadEdge = resultEl.TryGetProperty("focusedSecondaryRoadEdge", out var fsreEl) && fsreEl.TryGetInt32(out var pFsre) ? pFsre : -1;
        float focusedSecondaryRoadCenter = resultEl.TryGetProperty("focusedSecondaryRoadCenter", out var fsrcEl) && fsrcEl.TryGetSingle(out var pFsrc) ? pFsrc : 0f;

        var trafficSprites = new List<LotRenderer.WorldVehicleAssetDescriptor>();
        if (resultEl.TryGetProperty("trafficSprites", out var trafficArray) && trafficArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in trafficArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string vid = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                int dir = item.TryGetProperty("dir", out var dEl) && dEl.TryGetInt32(out var pDir) ? pDir : 0;
                string file = item.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String ? fEl.GetString() ?? "" : "";
                int width = item.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var pW) ? pW : 0;
                int height = item.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var pH) ? pH : 0;
                float anchorX = item.TryGetProperty("anchorX", out var axEl) && axEl.TryGetSingle(out var pAx) ? pAx : 0f;
                float anchorY = item.TryGetProperty("anchorY", out var ayEl) && ayEl.TryGetSingle(out var pAy) ? pAy : 0f;
                if (!string.IsNullOrEmpty(vid) && !string.IsNullOrEmpty(file) && width > 0 && height > 0)
                    trafficSprites.Add(new LotRenderer.WorldVehicleAssetDescriptor(vid, dir, file, width, height, anchorX, anchorY));
            }
        }

        var treeSprites = new List<LotRenderer.WorldTreeAssetDescriptor>();
        if (resultEl.TryGetProperty("treeSprites", out var treeArray) && treeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in treeArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string tid = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                string file = item.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String ? fEl.GetString() ?? "" : "";
                int width = item.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var pW) ? pW : 0;
                int height = item.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var pH) ? pH : 0;
                float anchorX = item.TryGetProperty("anchorX", out var axEl) && axEl.TryGetSingle(out var pAx) ? pAx : 0f;
                float anchorY = item.TryGetProperty("anchorY", out var ayEl) && ayEl.TryGetSingle(out var pAy) ? pAy : 0f;
                if (!string.IsNullOrEmpty(tid) && !string.IsNullOrEmpty(file) && width > 0 && height > 0)
                    treeSprites.Add(new LotRenderer.WorldTreeAssetDescriptor(tid, file, width, height, anchorX, anchorY));
            }
        }

        short targetRespHouse = responseHouse > 0 ? responseHouse : _connectedFocusedHouse;
        Renderer.SetAdjacentNeighborhood(lots, strips, panBounds, focusedRoadEdge, focusedRoadCenter, focusedSecondaryRoadEdge, focusedSecondaryRoadCenter, worldOriginX, worldOriginY, trafficSprites, treeSprites, focusedHouse: targetRespHouse);
        if (targetRespHouse > 0)
            _initialAdjacentSettledHouse = targetRespHouse;
        if (_holdingInitialLoadForAdjacentSince is not null || _openingNeighborhoodLot)
            FinishInitialAdjacentHold();
        if (lots.Count == 0)
            Log($"adjacentScenes returned 0 adjacentLots for house={targetRespHouse}");
        else
            Log($"adjacentScenes loaded {lots.Count} adjacent lots, {trafficSprites.Count} traffic sprites, {treeSprites.Count} tree sprites for house={targetRespHouse}");

        if (_pendingAdjacentSyncSignature is { } pendingSig)
        {
            _pendingAdjacentSyncSignature = null;
            if (!string.Equals(pendingSig, _lastAdjacentSyncSignature, StringComparison.Ordinal) &&
                _connectedAdjacentRendering &&
                _connectedFocusedHouse > 0 &&
                NeighborhoodWorldBlueprint.IsUnifiedMapLot(_connectedFocusedHouse) &&
                Renderer.Scene is { } activeScene &&
                (activeScene.House <= 0 || activeScene.House == _connectedFocusedHouse) &&
                _pendingAdjacentScenesId is null)
            {
                _lastAdjacentSyncSignature = pendingSig;
                _lastAdjacentPollAt = CurrentUptime();
                _pendingAdjacentScenesId = RequestAdjacentScenes(force: false);
            }
        }
        return true;
    }

    private void SendConnectedSelectSim(int simObjectId)
    {
        var parameters = new Dictionary<string, object?> { ["objectID"] = simObjectId };
        if (_connectedNeighborhoodEnabled && Ui.Gameplay.State?.Sims is { } sims)
        {
            var matched = sims.FirstOrDefault(s => s.Id == simObjectId);
            if (matched is not null)
            {
                if (matched.NeighborID is { } nid && nid > 0)
                    parameters["neighborID"] = nid;
                if (matched.LotId is { } lotId && lotId > 0)
                    parameters["lotId"] = lotId;
            }
        }

        _ = Send("selectSim", parameters);
    }

    private GroundTile? _pendingConnectedTargetTile;

    private bool TryHandleAdjacentLotClick(CGPoint point, CGPoint local)
    {
        if (!_connectedNeighborhoodEnabled || !Renderer.HasAdjacentNeighborhood)
            return false;

        if (Renderer.PickAdjacentLot(Simd(point), SimdCanvas) is not { } pick)
            return false;

        _pendingConnectedTargetTile = pick.Tile;

        bool isHome = _connectedHomeHouse > 0 && pick.House == _connectedHomeHouse;
        bool isOpenLot = isHome || pick.IsCommunity || !pick.Occupied || _connectedInvitedHouses.Contains(pick.House);

        // True Sims 3-style direct click on any open/outdoor/community object or Sim:
        // silently focus that lot at 0px camera jump and open its authentic native pie menu.
        if (isOpenLot && pick.ObjectID is int adjObjId && adjObjId > 0)
        {
            _pendingConnectedObjectMenu = new PendingConnectedObjectMenu(pick.House, adjObjId, local, CurrentUptime());
            _lastAdjacentSyncSignature = null;
            _ = Send("focusLot", new Dictionary<string, object?> { ["house"] = (int)pick.House });
            return true;
        }

        var entries = new List<SimInteraction>();
        if (!isOpenLot)
        {
            if (pick.HasDoor)
            {
                entries.Add(new SimInteraction
                {
                    Name = "Ring Doorbell",
                    Interaction = 2,
                    Param0 = pick.House,
                    Global = false,
                    Plugin = ConnectedNeighborhoodPluginId,
                    Entry = "knock",
                });
            }
            entries.Add(new SimInteraction
            {
                Name = "Go Here",
                Interaction = 1,
                Param0 = pick.House,
                Global = false,
                Plugin = ConnectedNeighborhoodPluginId,
                Entry = "visit",
            });
        }
        else if (isHome && _connectedFocusedHouse != _connectedHomeHouse)
        {
            entries.Add(new SimInteraction
            {
                Name = "Go Home",
                Interaction = 4,
                Param0 = _connectedHomeHouse,
                Global = false,
                Plugin = ConnectedNeighborhoodPluginId,
                Entry = "returnHome",
            });
            entries.Add(new SimInteraction
            {
                Name = "Go Here",
                Interaction = 1,
                Param0 = pick.House,
                Global = false,
                Plugin = ConnectedNeighborhoodPluginId,
                Entry = "visit",
            });
        }
        else
        {
            entries.Add(new SimInteraction
            {
                Name = "Go Here",
                Interaction = 1,
                Param0 = pick.House,
                Global = false,
                Plugin = ConnectedNeighborhoodPluginId,
                Entry = "visit",
            });
        }

        int syntheticObjectId = -(1000 + pick.House);
        _pieGround = null;
        PlayUISound("ui_piemenu_appear");
        var pieMenu = new OriginalPieOverlay(
            entries,
            syntheticObjectId,
            _selectedSim,
            local,
            Ui.Font,
            Ui.Device);
        Ui.PieMenu = pieMenu;
        pieMenu.Prepare(_canvas, Scale);
        pieMenu.Hover(point, Scale);
        return true;
    }

    private bool TryHandleConnectedPieAction(OriginalPieOverlay menu, SimInteraction action)
    {
        if (!string.Equals(action.Plugin, ConnectedNeighborhoodPluginId, StringComparison.Ordinal))
            return false;

        short targetHouse = action.Param0;
        var targetTile = _pendingConnectedTargetTile;
        _pendingConnectedTargetTile = null;
        string? requestId = action.Entry switch
        {
            "visit" => Send("visitLot", new Dictionary<string, object?>
            {
                ["house"] = (int)targetHouse,
                ["knock"] = false,
                ["walk"] = true,
                ["targetTileX"] = targetTile?.X,
                ["targetTileY"] = targetTile?.Y,
            }),
            "knock" => Send("visitLot", new Dictionary<string, object?>
            {
                ["house"] = (int)targetHouse,
                ["knock"] = true,
                ["walk"] = true,
                ["targetTileX"] = targetTile?.X,
                ["targetTileY"] = targetTile?.Y,
            }),
            "focus" => Send("focusLot", new Dictionary<string, object?>
            {
                ["house"] = (int)targetHouse,
            }),
            "returnHome" => Send("returnHome", new Dictionary<string, object?>
            {
                ["walk"] = true,
            }),
            _ => null,
        };

        if (requestId is not null)
        {
            _lastAdjacentSyncSignature = null;
            PlayUISound("ui_piemenu_select");
            PlayUISoundOnResponse(requestId, success: "ui_queued", failure: "ui_error");
        }
        return true;
    }

    public string? ScriptSetConnectedNeighborhood(bool enabled, bool adjacentRendering = true, bool storyProgression = true)
    {
        _lastAdjacentSyncSignature = null;
        return Send("setConnectedNeighborhood", new Dictionary<string, object?>
        {
            ["enabled"] = enabled,
            ["adjacentRendering"] = adjacentRendering,
            ["storyProgression"] = storyProgression,
        });
    }

    public string? ScriptVisitLot(int house, bool knock = false, bool walk = false)
    {
        _lastAdjacentSyncSignature = null;
        return Send("visitLot", new Dictionary<string, object?>
        {
            ["house"] = house,
            ["knock"] = knock,
            ["walk"] = walk,
        });
    }

    public string? ScriptFocusLot(int house)
    {
        _lastAdjacentSyncSignature = null;
        return Send("focusLot", new Dictionary<string, object?>
        {
            ["house"] = house,
        });
    }

    public string? ScriptReturnHome(bool walk = false)
    {
        _lastAdjacentSyncSignature = null;
        return Send("returnHome", new Dictionary<string, object?>
        {
            ["walk"] = walk,
        });
    }
}
