namespace SimsSDL;

internal sealed partial class LiveController
{
    private void UpdateWallPreview(CGPoint point)
    {
        if (WallLine(point) is not { } line)
        {
            if (_wallBrush is not null)
            {
                _wallBrush = null;
                try
                {
                    Renderer.UpdateWallBrush(null);
                }
                catch
                {
                }
            }

            if (_wallStroke is null)
            {
                UpdateBuildCursor(point);
            }

            return;
        }

        if (line == _wallBrush)
        {
            return;
        }

        try
        {
            Renderer.UpdateWallBrush(line);
            _wallBrush = line;
        }
        catch (Exception error)
        {
            Log($"Wall brush: {error}");
        }
    }

    private void FinishWallStroke(CGPoint point)
    {
        if (_wallStroke is null)
        {
            return;
        }

        WallBrushSelection? line = WallLine(point);
        if (_wallStroke is not { } stroke)
        {
            return;
        }

        PlayUISound("ui_bld_dragtool_mouseup");
        _wallStroke = null;
        _wallBrush = null;
        try
        {
            Renderer.UpdateWallBrush(null);
        }
        catch
        {
        }

        List<WallStrokeSegment> segments = [.. stroke.Completed];
        if (line is not null)
        {
            segments.Add(new WallStrokeSegment(line, stroke.Style, stroke.Pattern));
        }

        if (segments.Count == 0)
        {
            return;
        }

        var requests = segments.Select(segment =>
        {
            var arguments = segment.Selection.Arguments;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = segment.Selection.Command,
                ["x"] = arguments.X,
                ["y"] = arguments.Y,
                ["x2"] = arguments.X2,
                ["y2"] = arguments.Y2,
                ["pattern"] = (int)segment.Pattern,
                ["style"] = (int)segment.Style,
            };
        }).ToList();

        _ = SendWithBuildSound(
            "architecture",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = "WALL_STROKE",
                ["level"] = Renderer.ViewLevel,
                ["segments"] = requests,
            });
    }

    private bool StartWallpaperStroke(CGPoint point, bool deleting)
    {
        if (!BuildToolsReady
            || Ui.Build.Kind != BuildCatalogPage.CatalogKind.Wall
            || OverPanelControls(point)
            || (deleting ? (ushort)0 : Ui.Build.SelectedItem?.Id) is not ushort pattern)
        {
            return false;
        }

        LotScene.WallSide? wall = Renderer.WallAt(Simd(point), SimdCanvas);
        GroundTile? hitTile = FloorHit(point)?.Tile;
        if ((wall is null || wall.EdgeBit is null) && hitTile is null)
        {
            return false;
        }

        _wallpaperStroke = (deleting, pattern, new HashSet<LotScene.WallSide>());
        UpdateBuildCursor(point);
        if (wall is { EdgeBit: not null })
        {
            ApplyWallpaperAtWall(wall, deleting, pattern);
        }

        return true;
    }

    private void UpdateWallpaperStroke(CGPoint point)
    {
        if (_wallpaperStroke is not { } stroke || OverPanelControls(point))
        {
            return;
        }

        UpdateBuildCursor(point);
        if (Renderer.WallAt(Simd(point), SimdCanvas) is { EdgeBit: not null } wall)
        {
            ApplyWallpaperAtWall(wall, stroke.Deleting, stroke.Pattern);
        }
    }

    private void FinishWallpaperStroke()
    {
        _wallpaperStroke = null;
    }

    private void ApplyWallpaperAtWall(LotScene.WallSide wall, bool deleting, ushort pattern)
    {
        if (wall.EdgeBit is not int edge)
        {
            return;
        }

        if (_wallpaperStroke is { } activeStroke && !activeStroke.Visited.Add(wall))
        {
            return;
        }

        if (deleting)
        {
            _ = SendWithBuildSound(
                "architecture",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["command"] = "PATTERN_ERASE",
                    ["x"] = wall.X,
                    ["y"] = wall.Y,
                    ["level"] = wall.Level,
                    ["edge"] = edge,
                    ["side"] = wall.Side ?? 0,
                    ["pattern"] = 0,
                },
                successSound: "ui_archtool_delete");
        }
        else
        {
            _ = SendWithBuildSound(
                "architecture",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["command"] = "PATTERN_DOT",
                    ["x"] = wall.X,
                    ["y"] = wall.Y,
                    ["level"] = wall.Level,
                    ["edge"] = edge,
                    ["side"] = wall.Side ?? 0,
                    ["x2"] = wall.Direction,
                    ["y2"] = wall.Direction,
                    ["pattern"] = (int)pattern,
                    ["style"] = 0,
                });
        }
    }

    private bool FloorToolReady =>
        BuildToolsReady
        && Ui.Build.SelectedObjectGUID is null
        && (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Floor || Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water);

    private bool StartFloorStroke(CGPoint point, bool deleting)
    {
        if (!FloorToolReady
            || (deleting ? (ushort)0 : Ui.Build.SelectedItem?.Id) is not ushort pattern
            || FloorHit(point)?.Tile is not { } tile)
        {
            return false;
        }

        if (_buildable is { } area && !area.Contains(tile))
        {
            RefuseBuild();
            return true;
        }

        _floorStroke = (tile, pattern, Ui.Build.SelectedItem?.Id, deleting);
        if (!deleting)
        {
            PlayUISound("ui_bld_dragtool_mousedown");
        }

        _floorBrush = null;
        UpdateFloorPreview(point);
        return true;
    }

    private (FloorBrushRectangle Rectangle, FloorBrushSelection Selection)? FloorSelection(CGPoint point)
    {
        ushort mods = SDL_GetModState();
        bool shiftRoomFill = _floorStroke is null
            && Ui.Build.Kind == BuildCatalogPage.CatalogKind.Floor
            && (mods & SDL_KMOD_SHIFT) != 0;
        bool modDeleting = _floorStroke?.Deleting ?? ((mods & (SDL_KMOD_ALT | SDL_KMOD_CTRL)) != 0);
        if (!FloorToolReady
            || (Ui.Build.SelectedItem is null && !modDeleting)
            || FloorHit(point) is not { } hit
            || FloorBrushRectangle.Create(_floorStroke?.Start ?? hit.Tile, hit.Tile) is not { } rectangle
            || FloorBrushSelection.Create(
                rectangle,
                hit.Half,
                water: Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water && !modDeleting,
                roomFill: shiftRoomFill) is not { } selection)
        {
            return null;
        }

        return (rectangle, selection);
    }

    private void UpdateFloorPreview(CGPoint point)
    {
        if (FloorSelection(point) is not { } selected)
        {
            if (_floorBrush is not null)
            {
                _floorBrush = null;
                _floorBrushDeleting = false;
                _floorBrushTexture = null;
                try
                {
                    Renderer.UpdateFloorBrush(null);
                }
                catch
                {
                }
            }

            return;
        }

        FloorBrushSelection selection = selected.Selection;
        bool deleting = _floorStroke?.Deleting ?? ((SDL_GetModState() & (SDL_KMOD_ALT | SDL_KMOD_CTRL)) != 0);
        string? textureFile = !deleting && Ui.Build.Kind == BuildCatalogPage.CatalogKind.Floor
            ? (Ui.Build.SelectedItem?.Preview?.File ?? Ui.Build.SelectedItem?.Icon?.File)
            : null;
        if (Equals(selection, _floorBrush)
            && deleting == _floorBrushDeleting
            && string.Equals(textureFile, _floorBrushTexture, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Renderer.UpdateFloorBrush(selection, deleting: deleting, textureFile: textureFile);
            _floorBrush = selection;
            _floorBrushDeleting = deleting;
            _floorBrushTexture = textureFile;
        }
        catch (Exception error)
        {
            Log($"Floor brush: {error}");
        }
    }

    private void FinishFloorStroke(CGPoint point)
    {
        if (_floorStroke is not { } stroke)
        {
            return;
        }

        var selected = FloorSelection(point);
        PlayUISound("ui_bld_dragtool_mouseup");
        _floorStroke = null;
        _floorBrush = null;
        try
        {
            Renderer.UpdateFloorBrush(null);
        }
        catch
        {
        }

        if ((!stroke.Deleting && Ui.Build.SelectedItem?.Id != stroke.Selection)
            || selected is not { } hit)
        {
            return;
        }

        var (rectangle, selection) = hit;
        string command;
        if (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water && stroke.Selection == 65535)
        {
            command = stroke.Deleting ? "POOL_ERASE" : "POOL_RECT";
        }
        else if (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water && stroke.Selection == 65534)
        {
            command = stroke.Deleting ? "WATER_ERASE" : "WATER_RECT";
        }
        else
        {
            command = "FLOOR_RECT";
        }

        _ = SendWithBuildSound(
            "architecture",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = command,
                ["x"] = rectangle.X,
                ["y"] = rectangle.Y,
                ["level"] = Renderer.ViewLevel,
                ["x2"] = rectangle.Width,
                ["y2"] = rectangle.Height,
                ["pattern"] = (int)stroke.Pattern,
                ["style"] = (int)selection.Style,
                ["half"] = selection.Half,
            });
        UpdateFloorPreview(point);
    }

    private bool IsWallPortalPlacementTarget(BuySelection.SelectionTarget target) =>
        (Ui.BuildMode && Ui.Build.ObjectTool is OriginalBuildObjectTool.Door or OriginalBuildObjectTool.Window or OriginalBuildObjectTool.Fireplace)
        || (target.IsPlaced && target.PlacedObject?.CatalogCategory is 8 or 9 or 12);

    private (GroundTile Tile, int? WallDirection)? ResolveObjectPlacementTarget(CGPoint point, BuySelection.SelectionTarget target)
    {
        if (IsWallPortalPlacementTarget(target)
            && Renderer.WallAt(Simd(point), SimdCanvas) is { } wall
            && wall.Level == Renderer.ViewLevel
            && wall.Edge is null
            && wall.Direction is >= 0 and <= 3)
        {
            return (new GroundTile(wall.X, wall.Y), wall.Direction);
        }

        return Renderer.GroundTileAt(Simd(point), SimdCanvas) is { } ground
            ? (ground, null)
            : null;
    }

    private void UpdatePlacement(CGPoint point)
    {
        var currentTarget = Ui.Buy.Selection.Target;
        var resolvedTarget = currentTarget is { } activeTarget
            ? ResolveObjectPlacementTarget(point, activeTarget)
            : (Renderer.GroundTileAt(Simd(point), SimdCanvas) is { } fallbackTile ? (fallbackTile, (int?)null) : null);

        bool insufficientFunds = Ui.Buy.Selected is not null
            && (Ui.SelectedCatalogPrice ?? 0) > (Ui.Gameplay.State?.Budget ?? 0);
        if (!insufficientFunds || resolvedTarget is null)
        {
            Ui.CatalogPopup.ShowPlacementError(null);
        }

        if (Ui.BuildMode && !Ui.ObjectEditing)
        {
            UpdateBuildPreview(point);
            return;
        }

        if (!Ui.ObjectEditing
            || Renderer.Speed != 0
            || HasBlockingDialog
            || OverPanelControls(point)
            || currentTarget is not { } target
            || resolvedTarget is not { } resolved)
        {
            if (_desiredPlacement is not null)
            {
                try
                {
                    Renderer.UpdatePlacement(null);
                }
                catch
                {
                }
            }

            _desiredPlacement = null;
            _checkedPlacement = null;
            _snappedPlacementDirection = null;
            return;
        }

        bool unavailable = target.IsPlaced && !(target.PlacedObject?.Movable ?? false);
        if (unavailable)
        {
            _desiredPlacement = null;
            _checkedPlacement = null;
            _snappedPlacementDirection = null;
            try
            {
                Renderer.UpdatePlacement(null);
            }
            catch
            {
            }

            return;
        }

        if (resolved.WallDirection is int wallDir)
        {
            int primaryDir = wallDir switch { 1 => 1, 2 => 4, 3 => 16, _ => 64 };
            int oppositeDir = wallDir switch { 1 => 16, 2 => 64, 3 => 1, _ => 4 };
            if (Ui.Buy.Direction != primaryDir && Ui.Buy.Direction != oppositeDir)
            {
                int snappedDir = _desiredPlacement is { WallDirection: { } prevWall }
                    && prevWall == wallDir
                    && _desiredPlacement.Value.Direction == primaryDir
                    ? oppositeDir
                    : primaryDir;
                Ui.Buy.Selection.SetDirection(snappedDir);
            }
        }

        var key = new PlacementKey(
            target,
            resolved.Tile,
            Ui.Buy.Direction,
            Renderer.SceneRevisionNumber,
            Renderer.ViewLevel,
            resolved.WallDirection);
        if (_desiredPlacement != key)
        {
            _snappedPlacementDirection = null;
            try
            {
                Renderer.UpdatePlacement(null);
            }
            catch
            {
            }
        }

        _desiredPlacement = key;
        RequestPlacement();
    }

    private void RequestPlacement()
    {
        if (_pendingPlacement is not null
            || _desiredPlacement is not { } key
            || _desiredPlacement == _checkedPlacement)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x"] = key.Tile.PlacementX,
            ["y"] = key.Tile.PlacementY,
            ["level"] = key.Level,
            ["direction"] = key.Direction,
        };
        if (key.WallDirection is int wallDir)
        {
            parameters["wallDirection"] = wallDir;
        }
        if (key.Target.IsPlaced && key.Target.PlacedObject is { } obj)
        {
            parameters["objectID"] = obj.Id;
        }
        else
        {
            parameters["guid"] = key.Target.CatalogGuid;
        }

        if (Send("previewObject", parameters) is { } id)
        {
            _pendingPlacement = (id, key);
        }
    }

    public void PrepareFrame()
    {
        SyncDisposePanel();
        if (Ui.BuildMode && !Ui.ObjectEditing)
        {
            ushort mods = SDL_GetModState();
            if (mods != _lastBuildModifiers)
            {
                _lastBuildModifiers = mods;
                if (Pointer is { } pointer)
                {
                    UpdateBuildPreview(pointer);
                }
            }
        }

        Renderer.ShowsPlacement = Ui.BuyMode || Ui.BuildMode;
        Renderer.ShowsGhost = Ui.ObjectEditing;
        Renderer.ShowsBuildCursor = Ui.BuildMode
            && !Ui.ObjectEditing
            && (Ui.Build.TerrainMode is not null
                || Ui.Build.Kind == BuildCatalogPage.CatalogKind.WallStyle
                || Ui.Build.Kind == BuildCatalogPage.CatalogKind.Wall);
        Renderer.DrawsLot = Ui.ShowsLot;
        SyncFamilyPortraits();
    }

    /// <summary>Requests member portraits sequentially for Create a Family slots because preview generation
    /// shares a single output file and cannot overlap.</summary>
    private void SyncFamilyPortraits()
    {
        if (Ui.HouseholdCreation is not { ShowsFamilyPage: true } creation || Bridge is not { } bridge)
        {
            return;
        }

        var wanted = creation.MemberPreviewSelections();
        foreach (int key in _familyPortraits.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _familyPortraits.Remove(key);
            Renderer.ClearAppearancePreview(key);
        }

        if (_pendingHouseholdPreviewRequest is not null || _pendingHouseholdAppearanceRequest is not null)
        {
            return;
        }

        foreach (var (key, selection) in wanted.OrderBy(static kv => kv.Key))
        {
            string sig = Signature(selection);
            if (_familyPortraits.TryGetValue(key, out string? existing)
                && string.Equals(existing, sig, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _pendingHouseholdPreviewRequest = bridge.Send(
                    "appearancePreview",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["gender"] = selection.Gender,
                        ["age"] = selection.Age,
                        ["skin"] = selection.Skin,
                        ["headChoice"] = selection.HeadChoice,
                        ["bodyChoice"] = selection.BodyChoice,
                        ["portrait"] = true,
                    });
                _pendingHouseholdPreviewKey = key;
                _pendingHouseholdPreviewSignature = sig;
            }
            catch (Exception error)
            {
                _familyPortraits[key] = sig;
                Log($"Family member preview: {error}");
            }

            break;
        }
    }

    private static string Signature(
        (string Gender, string Age, string Skin, string HeadChoice, string BodyChoice) selection) =>
        $"{selection.Gender}|{selection.Age}|{selection.Skin}|{selection.HeadChoice}|{selection.BodyChoice}";

    private void ChooseGenericSimsCallPerson(int personRecordIndex)
    {
        if (_pendingGenericSimsCallChoice is not null
            || _activeGenericSimsCallRequest is not long requestID)
        {
            return;
        }

        if (Send(
                "genericSimsCallPersonPicker",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestID"] = requestID,
                    ["personRecordIndex"] = personRecordIndex,
                }) is { } id)
        {
            _pendingGenericSimsCallChoice = id;
        }
    }

    private void SimulationEvent(IReadOnlyDictionary<string, JsonElement> evt)
    {
        try
        {
            string? type = evt.JsonString("type");
            string? id = evt.JsonString("id");
            bool ok = evt.JsonBool("ok") == true;

            if (type == "response" && id is not null && HandleCheatResponse(id, ok, evt))
            {
                return;
            }

            if (type == "neighborhoodCheat")
            {
                HandleNeighborhoodCheatEvent(evt);
                return;
            }

            if (type == "response" && id is not null && id == _pendingGenericSimsCallPoll)
            {
                _pendingGenericSimsCallPoll = null;
                if (ok
                    && evt.JsonDict("result") is { } result
                    && result.JsonInt64("requestID") is long requestID
                    && result.TryGetValue("candidates", out JsonElement candidatesElement)
                    && candidatesElement.ValueKind == JsonValueKind.Array)
                {
                    List<GenericSimsCallChooserOverlay.Candidate>? candidates = null;
                    try
                    {
                        candidates = JsonSerializer.Deserialize<List<GenericSimsCallChooserOverlay.Candidate>>(
                            candidatesElement.GetRawText(),
                            PackContentPaths.JsonReadOptions);
                    }
                    catch
                    {
                        candidates = null;
                    }

                    if (candidates is { Count: > 0 })
                    {
                        _activeGenericSimsCallRequest = requestID;
                        string? sceneDirectory = Renderer.SceneRoot
                            ?? (Bridge is { } b ? Path.Combine(b.Session, "scene") : null);
                        Ui.GenericSimsCallPicker = new GenericSimsCallChooserOverlay(
                            candidates,
                            Ui.Font,
                            Ui.Device,
                            Ui.Content,
                            Ui.Strings,
                            Ui.Gameplay.State?.Sims ?? [],
                            sceneDirectory);
                    }
                }
            }

            if (type == "response" && id is not null && id == _pendingGenericSimsCallChoice)
            {
                _pendingGenericSimsCallChoice = null;
                bool accepted = ok && evt.JsonDict("result")?.JsonBool("accepted") == true;
                if (accepted)
                {
                    Ui.GenericSimsCallPicker = null;
                    _activeGenericSimsCallRequest = null;
                }
                else if (evt.JsonString("error") is { } reason)
                {
                    Log($"GenericSimsCall chooser: {reason}");
                }
            }

            if (type == "response" && id is not null && _pendingScriptTileTraversals.Remove(id))
            {
                if (ok
                    && evt.JsonDict("result") is { } result
                    && result.JsonIntArray("objectIDs") is { } objectIDs)
                {
                    _scriptTileTraversalResults[id] = (objectIDs, null);
                    string tileX = result.JsonInt("tileX")?.ToString(CultureInfo.InvariantCulture) ?? "?";
                    string tileY = result.JsonInt("tileY")?.ToString(CultureInfo.InvariantCulture) ?? "?";
                    string level = result.JsonInt("level")?.ToString(CultureInfo.InvariantCulture) ?? "?";
                    Log($"tile traversal {tileX} {tileY} level {level}: [{string.Join(", ", objectIDs)}]");
                }
                else
                {
                    string error = evt.JsonString("error") ?? "tile traversal returned no object IDs";
                    _scriptTileTraversalResults[id] = (null, error);
                    Log($"tile traversal failed: {error}");
                }
            }

            if (type == "response"
                && id is not null
                && _pendingDaemonNeighborhoodRequests.Remove(id, out var daemonNeighborhoodEntry))
            {
                if (ok)
                {
                    string manifest = Path.Combine(daemonNeighborhoodEntry.Session, "neighborhood.json");
                    daemonNeighborhoodEntry.Continuation.TrySetResult((daemonNeighborhoodEntry.Session, manifest));
                }
                else
                {
                    daemonNeighborhoodEntry.Continuation.TrySetException(
                        new ContentError(evt.JsonString("error") ?? "The neighborhood could not be loaded."));
                }

                return;
            }

            if (type == "response"
                && id is not null
                && _pendingDaemonOpenLotRequests.Remove(id, out var openLotContinuation))
            {
                if (ok)
                {
                    var result = evt.JsonDict("result");
                    bool simless = result?.JsonBool("simless") ?? false;
                    bool away = result?.JsonBool("away") ?? false;
                    int? homeHouse = result?.JsonInt("homeHouse") is int hh && hh > 0 ? hh : null;
                    openLotContinuation.TrySetResult(new DaemonOpenLotResult(simless, away, homeHouse));
                }
                else
                {
                    openLotContinuation.TrySetException(
                        new ContentError(evt.JsonString("error") ?? "The lot could not be opened."));
                }

                return;
            }

            if (type == "response" && id is not null && _pendingSaveRequests.Remove(id))
            {
                _pendingSaveCompletions.Remove(id, out Action<bool>? completion);
                if (!ok)
                {
                    if (evt.JsonString("error") is { } reason)
                    {
                        Log($"Save: {reason}");
                    }

                    PresentLocalFeedback(Strings.SaveHouseTitle(), Strings.SaveFailedMessage());
                }
                else if (Ui.OptionsPanel.PlayOptions.ExportHTML
                    && !_simlessBuildMode
                    && !AwayBuyBuildLocked
                    && _currentHouse is int house
                    && ((house >= 1 && house <= 19) || (house >= 90 && house <= 92)))
                {
                    ExportNeighborhoodWebPages(open: false, fallbackMode: NeighborhoodModeForCurrentLot());
                }

                completion?.Invoke(ok);
            }

            if (type == "response" && id is not null && id == _pendingResetTutorialState)
            {
                _pendingResetTutorialState = null;
                if (!ok)
                {
                    Log("Reset Tutorial: could not read save state");
                    return;
                }

                bool requiresSave = evt.JsonDict("result")?.JsonBool("requiresSave") == true;
                if (requiresSave)
                {
                    OfferResetTutorialSave();
                }
                else
                {
                    FinishResetTutorial();
                }
            }

            if (type == "response" && id is not null && _pendingLoadRequests.Remove(id) && !ok)
            {
                if (evt.JsonString("error") is { } reason)
                {
                    Log($"Load: {reason}");
                }

                if (evt.JsonStringArray("missingPacks") is { Count: > 0 } missing)
                {
                    ConfirmPackRemoval(missing);
                }
                else
                {
                    PresentLocalFeedback(Strings.LoadHouseTitle(), Strings.LoadFailedMessage());
                }
            }

            if (type == "response" && id is not null)
            {
                NeighborhoodResponse(id, ok, evt);
            }

            if (type == "pluginPanels" && evt.JsonArray("panels") is { } directPanels)
            {
                ApplyPluginPanelValues(directPanels);
            }

            if (type == "response"
                && evt.JsonDict("result")?.JsonArray("panels") is { } resultPanels
                && resultPanels.All(static p => p.TryGetValue("values", out JsonElement v) && v.ValueKind != JsonValueKind.Null))
            {
                ApplyPluginPanelValues(resultPanels);
            }

            if (type == "pluginPanelShow"
                && evt.JsonString("packId") is { } packID
                && evt.JsonString("panelId") is { } panelID
                && Ui.ShowsLot
                && !HasBlockingDialog
                && Ui.OutfitPicker is null)
            {
                PanelDescription? description = null;
                foreach (var (candidatePackID, candidateDescription) in PluginPanels)
                {
                    if (string.Equals(candidatePackID, packID, StringComparison.Ordinal)
                        && string.Equals(candidateDescription.Id, panelID, StringComparison.Ordinal))
                    {
                        description = candidateDescription;
                        break;
                    }
                }

                if (description is not null)
                {
                    DismissMenu();
                    Ui.SoundOptions.Dismiss();
                    OpenPluginPanel(packID, description, evt.JsonInt("anchorId"));
                }
            }

            if (type == "plugin"
                && evt.JsonString("packId") is { } pluginPackID
                && evt.JsonString("state") is { } pluginState)
            {
                string reasonSuffix = string.Empty;
                if (evt.TryGetValue("reason", out JsonElement reasonElement) && reasonElement.ValueKind != JsonValueKind.Null)
                {
                    reasonSuffix = $" — {(reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : reasonElement.GetRawText())}";
                }

                Log($"Plugin {pluginPackID}: {pluginState}{reasonSuffix}");
                if (string.Equals(pluginState, "failed", StringComparison.Ordinal)
                    && string.Equals(Ui.PluginPanel?.PackId, pluginPackID, StringComparison.Ordinal))
                {
                    Ui.PluginPanel = null;
                }
            }

            if (type == "saveMigrated")
            {
                var removedObjects = evt.JsonArray("removedObjects") ?? [];
                var upgraded = evt.JsonStringArray("upgraded") ?? [];
                var removed = evt.JsonStringArray("removed") ?? [];
                string backup = evt.JsonString("backup") ?? "none";
                Log($"Save migrated: {string.Join(", ", upgraded)}; removed {string.Join(", ", removed)}; {removedObjects.Count} objects; backup {backup}");
                if (removed.Count > 0)
                {
                    int refund = evt.JsonInt("refund") ?? 0;
                    string plural = removedObjects.Count == 1 ? string.Empty : "s";
                    string refundText = refund > 0 ? $" and refunded {refund} Simoleons" : string.Empty;
                    PresentLocalFeedback(
                        Strings.LoadHouseTitle(),
                        $"Removed {removedObjects.Count} object{plural} from content that is no longer installed{refundText}. A backup of the save was kept.");
                }
            }

            if (type == "tutorialFlash"
                && evt.JsonString("target") is { } flashTarget
                && evt.JsonInt("value") is int flashValue
                && evt.JsonBool("on") is bool flashOn)
            {
                Ui.TutorialFlash(flashTarget, flashValue, flashOn);
                string highlightText = Ui.TutorialHighlight is { } highlight ? $"{highlight.Button}" : "hidden";
                Log($"tutorial flash {flashTarget} {flashValue} {flashOn}: {highlightText}");
            }

            if (type == "lotSwitching")
            {
                int? fromHouse = evt.JsonInt("fromHouse") ?? _currentHouse;
                int? destination = evt.JsonInt("house");
                bool isReturnHome = evt.JsonBool("returnHome") ?? (evt.JsonBool("away") == false);
                int? sourceStateClass = evt.JsonInt("sourceStateClass") ?? _lastHouseStateClass;
                int? destinationStateClass = evt.JsonInt("destinationStateClass");
                int transitInfo = evt.JsonInt("lotTransitInfo") ?? _lastLotTransitInfo;
                _lotSims.TryGetValue(_selectedSim, out LotSimInfo? selectedLotSim);
                int fameScore = evt.JsonInt("fameScore") ?? (selectedLotSim?.FameScore ?? 0);
                int jobType = evt.JsonInt("jobType") ?? (selectedLotSim?.JobType ?? 0);
                _awaitingLotSwitchChange = true;
                _switchingLot = true;
                if (destination is int dest
                    && OriginalTravelLoadScreenExtensions.Select(
                        fromHouse,
                        dest,
                        sourceStateClass,
                        destinationStateClass,
                        fameScore,
                        jobType,
                        transitInfo,
                        isReturnHome) is { } screen)
                {
                    Ui.TravelLoadScreen = screen;
                    Ui.Loading = true;
                }
                else
                {
                    Ui.TravelLoadScreen = null;
                    Ui.Loading = false;
                }
            }

            if (type == "lotChanged")
            {
                int? previousHouse = _currentHouse;
                bool seamlessConnectedSwitch = false;
                Vector2 seamlessPanDelta = Vector2.Zero;
                if (evt.JsonInt("house") is int destination)
                {
                    bool? awayFlag = evt.JsonBool("away");
                    bool isReturnHome = awayFlag == false;
                    if (awayFlag == true)
                    {
                        _simlessBuildMode = false;
                    }

                    bool isNearbyOrVisible = Renderer.Scene?.House == destination
                        || Renderer.IsAdjacentHouseNearCamera((short)destination, SimdCanvas);
                    bool suppressTravelLoadScreen = _connectedNeighborhoodEnabled
                        && (Renderer.Scene?.House == destination || Renderer.HasAdjacentHouse((short)destination));
                    seamlessConnectedSwitch = _connectedNeighborhoodEnabled && isNearbyOrVisible;

                    _lotSims.TryGetValue(_selectedSim, out LotSimInfo? selectedLotSim);
                    if (!suppressTravelLoadScreen
                        && !_awaitingLotSwitchChange
                        && !_switchingLot
                        && OriginalTravelLoadScreenExtensions.Select(
                            previousHouse,
                            destination,
                            _lastHouseStateClass,
                            destinationStateClass: null,
                            travellerFameScore: selectedLotSim?.FameScore ?? 0,
                            travellerJobType: selectedLotSim?.JobType ?? 0,
                            lotTransitInfo: _lastLotTransitInfo,
                            isReturnHome: isReturnHome) is { } fallbackScreen)
                    {
                        Ui.TravelLoadScreen = fallbackScreen;
                        Ui.Loading = true;
                    }

                    _switchingLot = !seamlessConnectedSwitch;
                    _currentHouse = destination;
                    UpdateAwayLotLocks(destination, awayFlag);
                }

                _awaitingLotSwitchChange = false;
                _minimumSceneRevisionToFinishLoading = seamlessConnectedSwitch ? 1 : _highestReceivedSceneRevision + 1;
                _lastHouseStateClass = null;
                _lastLotTransitInfo = 0;
                DismissMenu();
                Ui.PluginPanel = null;
                Ui.SoundOptions.Dismiss();
                Ui.CheatConsole.Close();
                Ui.GameDialog = null;
                _answeredDialogs.Clear();
                Ui.BuyMode = false;
                Ui.BuildMode = false;
                Ui.PanelMinimized = false;
                _tutorialLiveModeClicked = false;
                if (seamlessConnectedSwitch)
                {
                    // Seamless connected switch: Install() atomically preserves the world camera position at 0px jump.
                    _ = seamlessPanDelta;
                    _centresOnSelectedSim = false;
                }
                else
                {
                    if (evt.JsonInt("house") is int resetDestination)
                        Renderer.RequestCenterOnHouse((short)resetDestination);
                    Renderer.Pan = Vector2.Zero;
                    _centresOnSelectedSim = Ui.OptionsPanel.PlayOptions.AutoCenter;
                }
                _pendingAdjacentScenesId = null;
                _lastAdjacentSyncSignature = null;
                _lastStreamedCameraTile = null;
                ResetConnectedAdjacentHoldState();
                _pendingCatalog = null;
                _pendingBuildCatalog = null;
                ClearPendingViewState();
                _latestQueuedSceneRevision = 0;
                _pendingPlacement = null;
                _pendingBuildCursor = null;
                _editableObjects = [];
                _placedObjectMeta.Clear();
                _pendingSaleRequests.Clear();
                _pendingDisposePreviewRequest = null;
                Ui.DisposePanel = null;
                _pendingPipCenterObjectID = null;
                ClosePictureInPicture();
            }

            if (type == "response" && _pendingBuildCursor is { } pendingCursor && id == pendingCursor.Id)
            {
                _pendingBuildCursor = null;
                _checkedBuildCursor = pendingCursor.Key;
                if (_desiredBuildCursor == pendingCursor.Key && Ui.BuildMode)
                {
                    Dictionary<string, JsonElement>? result = ok ? evt.JsonDict("result") : null;
                    PlacementPreview.GhostData? decodedGhost = null;
                    if (result is not null
                        && result.TryGetValue("ghost", out JsonElement ghostEl)
                        && ghostEl.ValueKind == JsonValueKind.Object)
                    {
                        decodedGhost = JsonSerializer.Deserialize<PlacementPreview.GhostData>(
                            ghostEl.GetRawText(),
                            PackContentPaths.JsonReadOptions);
                    }

                    PlacementPreview.GhostData? decodedWallPreview = null;
                    if (result is not null
                        && result.TryGetValue("wallPreview", out JsonElement wallPreviewEl)
                        && wallPreviewEl.ValueKind == JsonValueKind.Object)
                    {
                        decodedWallPreview = JsonSerializer.Deserialize<PlacementPreview.GhostData>(
                            wallPreviewEl.GetRawText(),
                            PackContentPaths.JsonReadOptions);
                    }

                    Renderer.UpdateBuildCursor(decodedGhost);
                    Renderer.UpdateWallPaintPreview(decodedWallPreview);
                }

                RequestBuildCursor();
            }

            if (type == "response" && _pendingPlacement is { } pendingPlacement && id == pendingPlacement.Id)
            {
                _pendingPlacement = null;
                _checkedPlacement = pendingPlacement.Key;
                if (_desiredPlacement == pendingPlacement.Key
                    && Ui.ObjectEditing
                    && ok
                    && evt.TryGetValue("result", out JsonElement placeResultEl)
                    && placeResultEl.ValueKind == JsonValueKind.Object)
                {
                    var preview = JsonSerializer.Deserialize<PlacementPreview>(
                        placeResultEl.GetRawText(),
                        PackContentPaths.JsonReadOptions);
                    if (preview is not null)
                    {
                        if (pendingPlacement.Key.WallDirection is not null
                            && preview.Direction is int snappedDir and (1 or 4 or 16 or 64))
                        {
                            _snappedPlacementDirection = (pendingPlacement.Key, snappedDir);
                        }
                        else
                        {
                            _snappedPlacementDirection = null;
                        }

                        Renderer.UpdatePlacement(preview);
                    }
                }

                RequestPlacement();
            }

            if (type == "response" && id is not null && id == _pendingBuildCatalog)
            {
                _pendingBuildCatalog = null;
                if (ok
                    && Ui.BuildMode
                    && evt.TryGetValue("result", out JsonElement buildCatEl)
                    && buildCatEl.ValueKind == JsonValueKind.Object
                    && Renderer.SceneRoot is { } buildRoot)
                {
                    string rawJson = buildCatEl.GetRawText();
                    if (Ui.Build.ObjectTool is { } tool && tool.Category() is int category)
                    {
                        var page = JsonSerializer.Deserialize<BuyCatalogPage>(rawJson, PackContentPaths.JsonReadOptions)
                            ?? throw new ContentError("Buy catalog page decoded to null");
                        if (page.Category == category)
                        {
                            Ui.Build.UpdateObjects(page, buildRoot);
                            Ui.Buy.Update(page, buildRoot, buildObjects: true);
                        }
                    }
                    else
                    {
                        var page = JsonSerializer.Deserialize<BuildCatalogPage>(rawJson, PackContentPaths.JsonReadOptions)
                            ?? throw new ContentError("Build catalog page decoded to null");
                        Ui.Build.Update(page, buildRoot);
                        if (page.Kind == BuildCatalogPage.CatalogKind.Roof)
                        {
                            Ui.Build.Choose(Ui.RoofStyle);
                        }

                        if (page.Kind == BuildCatalogPage.CatalogKind.Water && Ui.Build.SelectedObjectGUID is null)
                        {
                            Ui.Buy.CancelSelection();
                        }
                    }

                    if (Pointer is { } pointer)
                    {
                        Hover(pointer, _canvas);
                    }
                }
            }

            if (type == "response" && TryHandleAdjacentScenesResponse(id, ok, evt))
            {
            }

            if (type == "response" && id is not null && id == _pendingCatalog)
            {
                _pendingCatalog = null;
                if (ok
                    && Ui.BuyMode
                    && evt.TryGetValue("result", out JsonElement buyCatEl)
                    && buyCatEl.ValueKind == JsonValueKind.Object
                    && Renderer.SceneRoot is { } buyRoot)
                {
                    var page = JsonSerializer.Deserialize<BuyCatalogPage>(
                        buyCatEl.GetRawText(),
                        PackContentPaths.JsonReadOptions)
                        ?? throw new ContentError("Buy catalog page decoded to null");
                    Ui.Buy.Update(page, buyRoot);
                    if (Pointer is { } pointer)
                    {
                        Hover(pointer, _canvas);
                    }
                }
            }

            if (type == "response" && _pendingMenu is { } pendingMenu && id == pendingMenu.Id)
            {
                _pendingMenu = null;
                if (ok
                    && _selectedSim == pendingMenu.Caller
                    && (pendingMenu.Ground is not null || Renderer.ContainsObject(pendingMenu.Object))
                    && evt.TryGetValue("result", out JsonElement menuResultEl)
                    && menuResultEl.ValueKind == JsonValueKind.Array)
                {
                    var entries = JsonSerializer.Deserialize<List<SimInteraction>>(
                        menuResultEl.GetRawText(),
                        PackContentPaths.JsonReadOptions) ?? [];
                    if (entries.Count > 0)
                    {
                        _pieGround = pendingMenu.Ground;
                        PlayUISound("ui_piemenu_appear");
                        var pieMenu = new OriginalPieOverlay(
                            entries,
                            pendingMenu.Object,
                            pendingMenu.Caller,
                            pendingMenu.Anchor,
                            Ui.Font,
                            Ui.Device);
                        Ui.PieMenu = pieMenu;
                        // Centers newly opened root pie menus on their anchor, warping the cursor to match
                        // if clamping keeps the menu within viewport bounds.
                        pieMenu.Prepare(_canvas, Scale);
                        if (pieMenu.Layout?.Center is { } center)
                        {
                            var target = new CGPoint(center.X * Scale, center.Y * Scale);
                            double dx = center.X - pendingMenu.Anchor.X;
                            double dy = center.Y - pendingMenu.Anchor.Y;
                            if (Math.Sqrt((dx * dx) + (dy * dy)) > 0.5)
                            {
                                _pendingPieMenuWarp = target;
                                WarpPointer?.Invoke(target);
                                Hover(target, _canvas);
                            }
                            else
                            {
                                pieMenu.Hover(target, Scale);
                            }
                        }

                        RequestPieHeadPreview();
                    }
                    else
                    {
                        PlayUISound("ui_error");
                    }
                }
                else if (_selectedSim == pendingMenu.Caller)
                {
                    PlayUISound("ui_error");
                }
            }

            if (type == "response" && _pendingPieHead is { } pendingPieHead && id == pendingPieHead.Id)
            {
                _pendingPieHead = null;
                _checkedPieHead = pendingPieHead.Key;
                if (Ui.PieMenu is { } menu
                    && ReferenceEquals(menu, pendingPieHead.Key.Menu)
                    && menu.SelectedSim == _selectedSim
                    && ok
                    && evt.JsonDict("result") is { } pieResult)
                {
                    Ui.UpdatePieHeadPreview(pieResult);
                }

                RequestPieHeadPreview();
            }

            if (type == "response" && id is not null && id == _pendingAudioState)
            {
                _pendingAudioState = null;
                if (ok && evt.JsonDict("result")?.JsonDict("volumes") is { } volumes)
                {
                    var converted = new Dictionary<string, object?>(volumes.Count, StringComparer.Ordinal);
                    foreach (var (key, value) in volumes)
                    {
                        converted[key] = value;
                    }

                    Ui.SoundOptions.UpdateVolumes(converted);
                    if (Ui.SoundOptions.Volumes.TryGetValue("AMBIENCE", out float ambienceVolume))
                    {
                        _audio?.SetAmbientMasterVolume(ambienceVolume);
                    }
                }
            }

            if (type == "response" && id is not null && _pendingAudioVolumes.Remove(id, out var change))
            {
                if (_latestAudioVolumeRequest.TryGetValue(change.Group, out string? latestRequestId)
                    && latestRequestId == id)
                {
                    _latestAudioVolumeRequest.Remove(change.Group);
                    if (!ok)
                    {
                        Ui.SoundOptions.Restore(change);
                        if (string.Equals(change.Group, "AMBIENCE", StringComparison.OrdinalIgnoreCase))
                        {
                            _audio?.SetAmbientMasterVolume(change.PreviousVolume);
                        }
                    }
                }
            }

            if (type == "hello")
            {
                _bridgeConnected = true;
                var waitingHello = _pendingHelloContinuations.ToList();
                _pendingHelloContinuations.Clear();
                foreach (var continuation in waitingHello)
                {
                    continuation.TrySetResult();
                }

                _simlessBuildMode = evt.JsonBool("simless") ?? false;
                if (evt.JsonInt("homeHouse") is int hh && hh > 0)
                {
                    HomeLotHouse = hh;
                }

                if (_simlessBuildMode)
                {
                    AwayBuyBuildLocked = false;
                }
                else if (evt.JsonBool("away") is bool away
                    && (evt.JsonInt("house") ?? _currentHouse) is int activeHouse
                    && activeHouse > 0)
                {
                    UpdateAwayLotLocks(activeHouse, away);
                }

                _ = Send("audio", new Dictionary<string, object?>(StringComparer.Ordinal) { ["enabled"] = _audio is not null });
                _ = Send("stream", new Dictionary<string, object?>(StringComparer.Ordinal) { ["enabled"] = true });
                ApplyPlayOptions();
                if (_pendingHouseholdCreation is not null)
                {
                    PrewarmHouseholdAppearanceCatalog();
                }

                // Empty or community lots begin paused in build mode without Sims, while occupied lots
                // resume at the configured simulation speed.
                if ((_currentHouse ?? 0) > 0
                    && Options.Speed is int speed
                    && _pendingHouseholdCreation is null
                    && evt.JsonBool("simless") != true)
                {
                    _liveModePaused = speed == 0;
                    if (speed > 0)
                    {
                        _lastRunningSpeed = speed;
                    }

                    _ = Send("speed", new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = speed });
                }
            }

            if (type == "response" && id is not null && _pendingResponseSounds.Remove(id, out var sounds))
            {
                if (ok)
                {
                    PlayUISound(sounds.Success);
                }
                else if (sounds.Failure is { } failure)
                {
                    PlayUISound(failure);
                }
            }

            if (type == "response" && id is not null && _pendingSaleRequests.Remove(id))
            {
                // Plays a deletion sound if the refunded amount is zero, or a money refund sound when value
                // is returned.
                if (ok)
                {
                    int refund = evt.JsonDict("result")?.JsonInt("refund") ?? 0;
                    PlayUISound(refund == 0 ? "ui_archtool_delete" : "ui_object_moneyback");
                }
                else
                {
                    PlayUISound("ui_error");
                }
            }

            if (type == "response" && _pendingDisposePreviewRequest is { } pendingDispose && id == pendingDispose.Id)
            {
                _pendingDisposePreviewRequest = null;
                string? disposeRoot = Renderer.SceneRoot
                    ?? (Bridge is { } b ? Path.Combine(b.Session, "scene") : null);
                if (ok
                    && evt.JsonDict("result")?.JsonDict("preview") is { } previewRef
                    && disposeRoot is not null
                    && Ui.DisposePanel is { } disposePanel
                    && disposePanel.ObjectID == pendingDispose.ObjectID)
                {
                    disposePanel.Preview = Ui.LoadDisposePreview(previewRef, disposeRoot);
                }
            }

            if (type == "response" && evt.JsonString("operation") == "moveObject" && ok)
            {
                Ui.Buy.CancelSelection();
                SyncDisposePanel();
                _desiredPlacement = null;
                _checkedPlacement = null;
                _snappedPlacementDirection = null;
                try
                {
                    Renderer.UpdatePlacement(null);
                }
                catch
                {
                }
            }

            if (type == "response" && evt.JsonString("operation") == "dialogResponse" && ok)
            {
                _checkedPlacement = null;
            }

            if (!UsesNativeEngine
                && type == "response"
                && evt.JsonString("operation") == "dialogResponse"
                && ok
                && Options.Speed is int dialogSpeed
                && !Ui.BuyMode
                && !Ui.BuildMode)
            {
                _ = Send("speed", new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = dialogSpeed });
            }

            if (type == "response" && id is not null && id == _pendingViewRequestID)
            {
                _pendingViewRequestID = null;
                if (!ok || Renderer.SceneRevisionNumber >= _latestQueuedSceneRevision)
                {
                    ClearPendingViewState();
                }
                else
                {
                    _pendingViewSceneRevision = _latestQueuedSceneRevision;
                }
            }

            if (evt.JsonString("scene") is { } scene)
            {
                long revision = evt.JsonInt64("revision") ?? 0;
                _latestQueuedSceneRevision = Math.Max(_latestQueuedSceneRevision, revision);
                _highestReceivedSceneRevision = Math.Max(_highestReceivedSceneRevision, revision);
                var weakSelf = new WeakReference<LiveController>(this);
                Renderer.LoadSceneInBackground(scene, revision, error => RunOnMainThread(() =>
                {
                    if (!weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    if (error is not null)
                    {
                        Log($"scene {revision}: {error}");
                        return;
                    }

                    if (self._pendingViewRequestID is null
                        && self._pendingViewSceneRevision is long targetRevision
                        && revision >= targetRevision)
                    {
                        self.ClearPendingViewState();
                    }

                    if (self._stateAwaitingScene is { } waitingState && self.Renderer.SceneRoot is { } sceneRoot)
                    {
                        self._stateAwaitingScene = null;
                        try
                        {
                            self.Ui.Update(waitingState, sceneRoot);
                            self.SyncConnectedNeighborhoodState(waitingState);
                        }
                        catch (Exception stateError)
                        {
                            Log($"state: {stateError}");
                        }
                    }

                    if (!self._awaitingLotSwitchChange && revision >= self._minimumSceneRevisionToFinishLoading)
                    {
                        bool wasOpeningOrLoading = self._openingNeighborhoodLot || self._switchingLot || self.Ui.Loading;
                        self._hasCompletedInitialLoad = true;
                        self._switchingLot = false;
                        bool holdAdjacent = self.ShouldHoldLoadingForInitialAdjacent();
                        if (!holdAdjacent)
                        {
                            self.FinishInitialAdjacentHold(allowTravelDismiss: true);
                        }
                        self.SyncConnectedAdjacentOnSceneReady();

                        if (wasOpeningOrLoading && !holdAdjacent)
                        {
                            AppLog.SetStage(
                                FormattableString.Invariant($"Ready: Lot {self._currentHouse ?? 0} (scene revision {revision})"),
                                isSteadyState: true);
                        }
                    }

                    if (self.Pointer is { } pointer)
                    {
                        self.UpdatePlacement(pointer);
                        if (self._lastCutawayTile is null)
                        {
                            self.UpdateCutaway(pointer);
                        }
                    }

                    self.CentreOnSelectedSimIfPending();
                    self.CentreRelationshipIfPending();
                    self.CentrePipObjectIfPending();
                    if (revision > 0)
                    {
                        _ = self.Send("sceneAck", new Dictionary<string, object?>(StringComparer.Ordinal) { ["revision"] = revision });
                    }
                }));
            }

            Dictionary<string, JsonElement>? state = evt.JsonDict("state") ?? evt.JsonDict("result");
            if (state is not null && state.JsonInt("speed") is int stateSpeed)
            {
                ApplyState(state, stateSpeed);
            }

            if (type == "audio" && _audio is { } audio)
            {
                try
                {
                    var audioEvent = JsonSerializer.Deserialize<AudioEvent>(
                        JsonSerializer.SerializeToUtf8Bytes(evt),
                        PackContentPaths.JsonReadOptions);
                    if (audioEvent is not null)
                    {
                        audio.Receive(audioEvent);
                    }
                }
                catch (Exception audioError)
                {
                    Log($"audio: {audioError}");
                }
            }

            if (type == "environment")
            {
                PluginEnvironment? environment = null;
                if (evt.TryGetValue("environment", out JsonElement envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    environment = JsonSerializer.Deserialize<PluginEnvironment>(
                        envEl.GetRawText(),
                        PackContentPaths.JsonReadOptions);
                }

                Renderer.ApplyEnvironment(environment);
                Ui.Gameplay.Environment = environment;
            }

            if (type == "audioError")
            {
                string audioErr = evt.JsonString("error")
                    ?? (evt.TryGetValue("error", out JsonElement errEl) && errEl.ValueKind != JsonValueKind.Null
                        ? errEl.GetRawText()
                        : "unknown sound error");
                Log($"audio: {audioErr}");
            }

            if (type == "error")
            {
                Log($"host error {JsonSerializer.Serialize(evt)}");
                Ui.Loading = false;
                _switchingLot = false;
                _openingNeighborhoodLot = false;
                _awaitingLotSwitchChange = false;
                _audio?.StopLoadLoop();
            }

            if (evt.JsonBool("ok") == false && evt.JsonString("operation") != "audioEvent")
            {
                if (Ui.GameDialog is not null)
                {
                    Ui.GameDialog.Submitting = false;
                }

                string opName = evt.JsonString("operation") ?? "request";
                string errText = evt.JsonString("error") ?? string.Empty;
                Log($"{opName} failed: {errText}");
            }
        }
        catch (Exception error)
        {
            Log($"{error}");
        }
    }

    private void UpdateAwayLotLocks(int house, bool? awayFlag = null)
    {
        var locks = AwayLotLocks.Compute(
            house: house,
            homeHouse: HomeLotHouse,
            simlessBuildMode: _simlessBuildMode,
            awayFlag: awayFlag);
        AwayBuyBuildLocked = locks.BuyBuildLocked;
        if (locks.BuyBuildLocked)
        {
            Ui.BuyMode = false;
            Ui.BuildMode = false;
        }

        if (locks.LotSaveDisabled is bool saveDisabled)
        {
            LotSaveDisabled = saveDisabled;
        }
    }

    private void ApplyState(IReadOnlyDictionary<string, JsonElement> state, int speed)
    {
        _lastHouseStateClass = state.JsonInt("houseStateClass");
        _lastLotTransitInfo = state.JsonInt("lotTransitInfo") ?? 0;
        if (state.JsonInt("homeHouse") is int hh && hh > 0)
        {
            HomeLotHouse = hh;
        }

        if (state.JsonBool("simlessBuildMode") is bool simless)
        {
            _simlessBuildMode = simless;
        }

        int stateHouse = state.JsonInt("house") ?? _currentHouse ?? 0;
        if (_simlessBuildMode)
        {
            AwayBuyBuildLocked = false;
        }
        else if (state.JsonBool("away") is bool away && stateHouse > 0)
        {
            UpdateAwayLotLocks(stateHouse, away);
        }

        if (state.TryGetValue("simulationError", out JsonElement simErrEl) && simErrEl.ValueKind == JsonValueKind.String)
        {
            Ui.Loading = false;
            _switchingLot = false;
            _openingNeighborhoodLot = false;
            _awaitingLotSwitchChange = false;
            _audio?.StopLoadLoop();
        }

        ScriptFreeWill = state.JsonBool("freeWill");
        PluginEnvironment? environment = null;
        if (state.TryGetValue("environment", out JsonElement stateEnvEl) && stateEnvEl.ValueKind == JsonValueKind.Object)
        {
            environment = JsonSerializer.Deserialize<PluginEnvironment>(
                stateEnvEl.GetRawText(),
                PackContentPaths.JsonReadOptions);
        }

        Renderer.ApplyEnvironment(environment);
        Ui.Gameplay.Environment = environment;
        ApplyHouseholdState(state);

        if (state.TryGetValue("dialogs", out JsonElement dialogsEl) && dialogsEl.ValueKind == JsonValueKind.Array)
        {
            var pending = JsonSerializer.Deserialize<List<GameDialog>>(
                dialogsEl.GetRawText(),
                PackContentPaths.JsonReadOptions) ?? [];
            GameDialog? travel = pending.FirstOrDefault(static d => OriginalNeighborhoodOverlay.ModeForDialogKind(d.Kind) is not null);
            if (travel is not null && OriginalNeighborhoodOverlay.ModeForDialogKind(travel.Kind) is int mode)
            {
                if (_localDialogToken is null)
                {
                    Ui.GameDialog = null;
                }

                if (_activeNeighborhoodDialog?.Token != travel.Token
                    && _pendingNeighborhood?.Dialog.Token != travel.Token
                    && !_failedNeighborhoodTokens.Contains(travel.Token))
                {
                    LoadTravelNeighborhood(travel, mode);
                }
            }
            else if (pending.FirstOrDefault() is { } firstOutfit && (firstOutfit.Kind == 6 || firstOutfit.Kind == 9))
            {
                if (_localDialogToken is null)
                {
                    Ui.GameDialog = null;
                }

                if (!_answeredDialogs.Contains(firstOutfit.Token)
                    && Ui.OutfitPicker?.Token != firstOutfit.Token
                    && _pendingOutfitChoices?.Dialog.Token != firstOutfit.Token)
                {
                    OpenOutfitPicker(firstOutfit);
                }
            }
            else if (Ui.HouseholdCreation is not null)
            {
                // Help prompts cannot be resolved before a family exists, deferring them until the lot is
                // loaded.
                if (_localDialogToken is null)
                {
                    Ui.GameDialog = null;
                }
            }
            else if ((pending.FirstOrDefault(static d => d.Blocking) ?? pending.FirstOrDefault()) is { } first)
            {
                if (Ui.GameDialog?.Dialog.Token != first.Token && _localDialogToken is null)
                {
                    // A blocking dialog closes active pie menus while preserving any object currently held
                    // by the Buy cursor.
                    if (first.Blocking)
                    {
                        if (_awaitingLotSwitchChange)
                        {
                            Ui.Loading = false;
                            _switchingLot = false;
                            _awaitingLotSwitchChange = false;
                        }

                        BuySelection.SelectionTarget? heldTarget = Ui.Buy.Selection.Target;
                        int heldDirection = Ui.Buy.Selection.Direction;
                        DismissMenu();
                        if (heldTarget is { } savedTarget)
                        {
                            if (savedTarget.IsPlaced && savedTarget.PlacedObject is { } savedObject)
                            {
                                Ui.Buy.Selection.ChooseObject(savedObject);
                            }
                            else if (savedTarget.CatalogGuid != 0)
                            {
                                Ui.Buy.Selection.ChooseCatalog(savedTarget.CatalogGuid);
                            }

                            int[] directions = [1, 4, 16, 64];
                            int currentDirectionIndex = Array.IndexOf(directions, Ui.Buy.Selection.Direction);
                            int heldDirectionIndex = Array.IndexOf(directions, heldDirection);
                            if (currentDirectionIndex >= 0
                                && heldDirectionIndex >= 0
                                && currentDirectionIndex != heldDirectionIndex)
                            {
                                Ui.Buy.Selection.Rotate(heldDirectionIndex - currentDirectionIndex);
                            }
                        }
                    }

                    string? sceneDirectory = Renderer.SceneRoot
                        ?? (Bridge is { } b ? Path.Combine(b.Session, "scene") : null);
                    Ui.GameDialog = new OriginalDialogOverlay(first, Ui.Font, Ui.Device, Ui.Content, sceneDirectory);
                    string choices = string.Join(", ", first.Buttons.Select(static button => button.Label));
                    Log($"dialog \"{first.Title}\": {first.Message} [{choices}]");
                }

                if (ScriptDialogStop is { } stop
                    && (first.Message.Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                        || first.Title.Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                        || (first.Kind == 10 && "Phone Book".Contains(stop, StringComparison.CurrentCultureIgnoreCase))
                        || (first.IsBookScreen && first.Kind == 12 && "Spellbook".Contains(stop, StringComparison.CurrentCultureIgnoreCase))
                        || (first.IsBookScreen
                            && first.Kind == 15
                            && ("Cookbook".Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                                || "Recipes".Contains(stop, StringComparison.CurrentCultureIgnoreCase)))))
                {
                    ScriptAnswersDialogs = false;
                    ScriptDialogStop = null;
                }

                if ((Options.AnswerDialogs || ScriptAnswersDialogs)
                    && !_answeredDialogs.Contains(first.Token)
                    && Ui.GameDialog is { } dialog
                    && dialog.Dialog.Token == first.Token)
                {
                    _answeredDialogs.Add(first.Token);
                    byte code = first.Kind == 10
                        ? (byte)0
                        : (first.Buttons.Count > 0 ? first.Buttons[0].Code : (byte)0);
                    RespondDialog(dialog, code, responseText: first.Kind == 10 ? "-1" : null);
                }
            }
            else
            {
                if (_localDialogToken is null)
                {
                    Ui.GameDialog = null;
                }

                if (Ui.OutfitPicker is not null)
                {
                    CloseOutfitPicker();
                }
            }
        }

        bool buildableChanged = false;
        if (BuildableArea.Create(state) is { } area)
        {
            if (!Equals(_buildable, area))
            {
                _buildable = area;
                buildableChanged = true;
            }
        }

        if (BuildableArea.Create(state, "buildableLevel2") is { } area2)
        {
            if (!Equals(_buildableLevel2, area2))
            {
                _buildableLevel2 = area2;
                buildableChanged = true;
            }
        }

        if (buildableChanged)
        {
            Renderer.SetBuildableAreas(_buildable, _buildableLevel2);
        }

        var sims = state.JsonArray("sims") ?? [];
        if (state.JsonArray("objects") is { } objects)
        {
            var simEntries = sims
                .Where(static sim => sim.JsonInt("id").HasValue)
                .Select(static sim => (Id: sim.JsonInt("id")!.Value, Name: sim.JsonString("name") ?? string.Empty));
            var nonAvatars = objects.Where(static obj => obj.JsonBool("avatar") != true).ToList();
            ScriptObjects = nonAvatars
                .Where(static obj => obj.JsonInt("id").HasValue)
                .Select(static obj => (Id: obj.JsonInt("id")!.Value, Name: obj.JsonString("name") ?? string.Empty))
                .Concat(simEntries)
                .OrderBy(static entry => entry.Id)
                .ToList();
            var editableObjects = new List<EditableLotObject>(nonAvatars.Count);
            var nextMeta = new Dictionary<int, PlacedObjectMeta>(nonAvatars.Count);
            foreach (var obj in nonAvatars)
            {
                if (obj.JsonInt("id") is not int objId)
                {
                    continue;
                }

                float x = (float)(obj.JsonDouble("x") ?? 0.0);
                float y = (float)(obj.JsonDouble("y") ?? 0.0);
                int level = obj.JsonInt("level") ?? 1;
                int? catalogCategory = obj.JsonInt("catalogCategory");
                int buildModeType = obj.JsonInt("buildModeType")
                    ?? (catalogCategory is int category ? Math.Max(0, category - 7) : 0);
                int resaleValue = obj.JsonInt("resaleValue") ?? 0;
                string catalogName = obj.JsonString("catalogName") ?? string.Empty;
                nextMeta[objId] = new PlacedObjectMeta(x, y, level, buildModeType, resaleValue, catalogName);

                uint guid = (uint)(obj.JsonInt64("guid") ?? 0);
                int direction = obj.JsonInt("direction") ?? 0;
                bool movable = obj.JsonBool("movable") ?? false;
                bool sellable = obj.JsonBool("sellable") ?? false;
                int? multiTileMasterId = obj.JsonInt("multiTileMasterID");
                editableObjects.Add(new EditableLotObject(
                    objId, guid, level, direction, movable, sellable, catalogCategory, multiTileMasterId));
            }

            _editableObjects = editableObjects;
            _placedObjectMeta = nextMeta;
            Ui.Buy.Selection.Reconcile(_editableObjects);
            SyncDisposePanel();
        }

        Renderer.Speed = speed;
        if (speed > 0)
        {
            _lastRunningSpeed = speed;
            _liveModePaused = false;
        }

        float pitch = (float)(state.JsonDouble("roofPitch") ?? 0.66);
        Ui.RoofPitch = pitch;
        Ui.Build.SelectedRoofPitch = pitch;
        if ((state.JsonInt("roofStyleIndex") ?? state.JsonInt("roofStyle")) is int roofStyle
            && roofStyle is >= 0 and <= ushort.MaxValue)
        {
            ushort style = (ushort)roofStyle;
            Ui.RoofStyle = style;
            if (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Roof)
            {
                Ui.Build.Choose(style);
            }
        }

        // State updates arriving before the target lot's scene has loaded must wait for scene completion,
        // both for paused initial loads and during non-adjacent lot switches while the prior SceneRoot is still mounted.
        int effectiveStateHouse = state.JsonInt("house")
            ?? (state.TryGetValue("connectedNeighborhood", out var cnHouseEl)
                && cnHouseEl.ValueKind == JsonValueKind.Object
                && cnHouseEl.TryGetProperty("focusedHouse", out var fhHouseEl)
                && fhHouseEl.TryGetInt32(out int fhHouse) ? fhHouse : 0);
        bool awaitingTargetScene = _switchingLot
            || _awaitingLotSwitchChange
            || Renderer.IsLoadingScene
            || (effectiveStateHouse > 0 && Renderer.Scene is { House: > 0 } currentScene && currentScene.House != effectiveStateHouse);
        if (!awaitingTargetScene && Renderer.SceneRoot is { } root)
        {
            Ui.Update(state, root);
            SyncConnectedNeighborhoodState(state);
            _stateAwaitingScene = null;
        }
        else
        {
            _stateAwaitingScene = new Dictionary<string, JsonElement>(state, StringComparer.Ordinal);
        }

        if (Pointer is { } pointer)
        {
            UpdatePlacement(pointer);
            if (_lastCutawayTile is null)
            {
                UpdateCutaway(pointer);
            }
        }

        _selectedSim = state.JsonInt("selectedSim") ?? 0;
        if (Ui.PieMenu is { } menu
            && (menu.SelectedSim != _selectedSim || (menu.ObjectID > 0 && !Renderer.ContainsObject(menu.ObjectID))))
        {
            DismissMenu();
        }

        _simIDs = sims
            .Where(static s => s.JsonBool("household") == true && s.JsonInt("id").HasValue)
            .Select(static s => s.JsonInt("id")!.Value)
            .ToList();
        var nextLotSims = new Dictionary<int, LotSimInfo>();
        foreach (var entry in sims)
        {
            if (entry.JsonInt("id") is not int simId)
            {
                continue;
            }

            string? rawTooltipName = entry.JsonString("tooltipName");
            string tooltipName = !string.IsNullOrEmpty(rawTooltipName)
                ? rawTooltipName
                : (entry.JsonString("name") ?? string.Empty);
            int gender = entry.JsonInt("gender") ?? 0;
            uint guid = (uint)(entry.JsonInt64("guid") ?? 0);
            bool hideHover = entry.JsonBool("hideHover") ?? false;
            int fameScore = entry.JsonInt("fameScore") ?? 0;
            int jobType = entry.JsonInt("jobType") ?? 0;
            float x = (float)(entry.JsonDouble("x") ?? 0.0);
            float y = (float)(entry.JsonDouble("y") ?? 0.0);
            int level = entry.JsonInt("level") ?? 1;
            bool winterClothes = entry.JsonBool("winterClothes") ?? false;
            bool outfitChanging = entry.JsonBool("outfitChanging") ?? false;
            nextLotSims[simId] = new LotSimInfo
            {
                TooltipName = tooltipName,
                Gender = gender,
                Guid = guid,
                HideHover = hideHover,
                FameScore = fameScore,
                JobType = jobType,
                X = x,
                Y = y,
                Level = level,
                WinterClothes = winterClothes,
                OutfitChanging = outfitChanging,
            };
        }

        _lotSims = nextLotSims;
        _simAnimations = sims
            .Select(static s => s.JsonString("animation"))
            .OfType<string>()
            .ToList();

        Dictionary<string, JsonElement>? selected = null;
        foreach (var entry in sims)
        {
            if (entry.JsonInt("id") == _selectedSim)
            {
                selected = entry;
                break;
            }
        }

        if (selected is not null
            && selected.JsonDouble("x") is double simX
            && selected.JsonDouble("y") is double simY)
        {
            var actions = selected.JsonArray("actions") ?? [];
            ScriptSimPosition = new CGPoint(simX, simY);
            ScriptSimIdle = !actions.Any(static a => a.JsonBool("visible") == true);
            ScriptSimActions = actions
                .Select(static a => a.JsonString("name"))
                .OfType<string>()
                .ToList();
            _scriptSimLevel = selected.JsonInt("level");
        }
        else
        {
            ScriptSimPosition = null;
            _scriptSimLevel = null;
            ScriptSimIdle = false;
        }

        ApplyPictureInPicture(state);
        CentreOnSelectedSimIfPending();
        CentreRelationshipIfPending();
        CentrePipObjectIfPending();
    }

    private void CentreOnSelectedSimIfPending()
    {
        if (!_centresOnSelectedSim
            || _selectedSim == 0
            || ScriptSimPosition is not { } position
            || _scriptSimLevel is not int level)
        {
            return;
        }

        if (level != EffectiveViewLevel && level >= 1 && level <= Renderer.StoryCount)
        {
            _pendingViewLevel = level;
            _pendingRoofsVisible = false;
            _pendingViewSceneRevision = null;
            _pendingViewRequestID = Send(
                "level",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = level, ["roofs"] = false });
            return;
        }

        if (level != Renderer.ViewLevel)
        {
            return;
        }

        if (Renderer.SceneViewport(SimdCanvas) is not { } viewport
            || Renderer.ScreenPoint((float)position.X, (float)position.Y, SimdCanvas) is not { } point)
        {
            return;
        }

        _centresOnSelectedSim = false;
        // Centers the camera on an object by offsetting the tile position upward by half a Sim's height (8
        // * sqrt(6) pixels scaled by zoom factor and interface scale).
        float lift = 8f * MathF.Sqrt(6f) * Renderer.Zoom.Factor() * Renderer.UIScale;
        Renderer.Pan += viewport.Size / 2f - (point - new Vector2(0f, lift));
        if (EffectiveRoofsVisible)
        {
            _pendingRoofsVisible = false;
            _pendingViewSceneRevision = null;
            _pendingViewRequestID = Send(
                "roofs",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["visible"] = false });
        }
    }

    private string NeighborhoodSettingPath =>
        Path.Combine(
            Options.DataRoot ?? Workspace,
            "runtime",
            "profiles",
            Options.Profile,
            "Settings",
            "current-neighborhood.txt");

    private void StoreNeighborhood(int number)
    {
        string path = NeighborhoodSettingPath;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, number.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch (Exception error)
        {
            Log($"Neighborhood setting: {error}");
        }
    }

    private int NeighborhoodModeForCurrentLot() => (_currentHouse ?? Options.House ?? 0) switch
    {
        >= 21 and <= 30 => 2,
        >= 40 and <= 49 => 3,
        >= 81 and <= 89 => 5,
        >= 90 and <= 99 => 7,
        _ => (Ui.Neighborhood?.Mode ?? _lastNeighborhoodMode) is int mode && (mode == 1 || mode == 4) ? mode : 1,
    };

    private void BeginStartupSplash(int startingAt)
    {
        Ui.TravelLoadScreen = null;
        Ui.Loading = true;
        _splashProgressQueue = Strings.SplashProgressSequence(startingAt).ToList();
        if (_splashProgressQueue.Count == 0)
        {
            Ui.LoadingText = Strings.SplashProgress(startingAt);
        }
        else
        {
            Ui.LoadingText = _splashProgressQueue[0];
            _splashProgressQueue.RemoveAt(0);
        }

        _lastSplashProgressStep = CurrentUptime();
    }

    private async Task<(string Session, string Manifest)> FetchNeighborhoodManifest(
        int mode,
        string? edit = null,
        int neighborhood = 1,
        bool includeWebScenes = false)
    {
        if (_daemonMode && _bridgeConnected && Bridge is { } bridge)
        {
            if (edit is null
                && SimsSDL.Client.NeighborhoodExport.LookupCached(
                    Content,
                    Workspace,
                    Options.DataRoot,
                    mode,
                    Options.Profile,
                    neighborhood,
                    includeWebScenes) is { } hit)
            {
                return hit;
            }

            string storageRoot = Options.DataRoot ?? Workspace;
            string exportSession = Path.Combine(
                storageRoot,
                "runtime",
                "sessions",
                "neighborhood-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
            Directory.CreateDirectory(exportSession);
            SessionSweeper.Claim(exportSession);
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mode"] = mode,
                ["neighborhood"] = neighborhood,
                ["outputDirectory"] = exportSession,
            };
            if (!string.IsNullOrEmpty(edit))
            {
                parameters["edit"] = edit;
            }

            if (includeWebScenes)
            {
                parameters["webScenes"] = true;
            }

            var continuation = new TaskCompletionSource<(string Session, string Manifest)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string requestId = bridge.Send("neighborhood", parameters);
            _pendingDaemonNeighborhoodRequests[requestId] = (exportSession, continuation);
            var exported = await continuation.Task.ConfigureAwait(false);
            if (edit is null)
            {
                SimsSDL.Client.NeighborhoodExport.StoreCached(
                    Content,
                    Workspace,
                    Options.DataRoot,
                    mode,
                    Options.Profile,
                    neighborhood,
                    includeWebScenes,
                    session: exported.Session,
                    manifest: exported.Manifest);
            }

            return exported;
        }

        return await SimsSDL.Client.NeighborhoodExport.Create(
            Content,
            Workspace,
            Options.DataRoot,
            mode,
            Options.Profile,
            edit,
            neighborhood,
            discardsContent: true,
            nativeEngine: UsesNativeEngine,
            includeWebScenes: includeWebScenes).ConfigureAwait(false);
    }

    public void LoadBaseNeighborhood(int mode = 1, bool reopenFamilyPicker = false)
    {
        if (_loadingNeighborhood || _editingNeighborhood)
        {
            return;
        }

        _neighborhoodLoadGeneration = unchecked(_neighborhoodLoadGeneration + 1);
        ulong requestGen = _neighborhoodLoadGeneration;
        int targetNeighborhood = _currentNeighborhood;
        _loadingNeighborhood = true;
        Renderer.LiveMode = false;
        // Shows the startup splash only on initial launch; returning from a lot retains the frozen lot view
        // under an hourglass cursor until the neighborhood map is loaded.
        if (!_hasCompletedInitialLoad && Ui.Neighborhood is null && Ui.HouseholdCreation is null)
        {
            Renderer.DrawsLot = false;
            BeginStartupSplash(startingAt: 3);
            if (Options.Audio && !_daemonMode && _startupLoadAudio is null && LoadLoopURL(Content) is { } loop)
            {
                try
                {
                    var player = new SDLAudioPlayer(Content.Root);
                    player.PlayLoadLoop(loop, LoadLoopVolume());
                    _startupLoadAudio = player;
                }
                catch
                {
                }
            }
        }
        else
        {
            Ui.Loading = false;
            if (Ui.Neighborhood is not null || Ui.HouseholdCreation is not null)
            {
                Renderer.DrawsLot = false;
            }
        }

        _ = LoadBaseNeighborhoodCoreAsync(mode, reopenFamilyPicker, requestGen, targetNeighborhood);
    }

    private async Task LoadBaseNeighborhoodCoreAsync(
        int mode,
        bool reopenFamilyPicker,
        ulong requestGen,
        int targetNeighborhood)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        try
        {
            AppLog.SetStage(FormattableString.Invariant($"Neighborhood: loading N{targetNeighborhood} (mode {mode})"));
            var exported = await FetchNeighborhoodManifest(
                mode: mode,
                edit: null,
                neighborhood: targetNeighborhood).ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                bool loaded = false;
                try
                {
                    if (self._neighborhoodLoadGeneration != requestGen
                        || (self.Bridge is not null && !(self._daemonMode && (self._currentHouse ?? 0) == 0)))
                    {
                        return;
                    }

                    var neighborhood = new OriginalNeighborhoodOverlay(
                        self.Content,
                        self.Ui.Device,
                        exported.Manifest,
                        exported.Session,
                        self.Ui.Font);
                    self.WireNeighborhoodOverlay(neighborhood);
                    self.Ui.Neighborhood = neighborhood;
                    self.Ui.Loading = false;
                    self._hasCompletedInitialLoad = true;
                    loaded = true;
                    self.Renderer.DrawsLot = false;
                    self._neighborhoodHouseSelected = house =>
                    {
                        if (weakSelf.TryGetTarget(out var target))
                        {
                            target.SelectNeighborhoodHouse(house);
                        }
                    };
                    Log($"neighborhood {targetNeighborhood} ({exported.Session})");
                    AppLog.SetStage(
                        FormattableString.Invariant($"Ready: Neighborhood N{targetNeighborhood} (mode {mode})"),
                        isSteadyState: true);
                    self._audio?.StopLoadLoop();
                    self._startupLoadAudio?.Close();
                    self._startupLoadAudio = null;
                    self.StartFrontendAudio();
                    self.StartNeighborhoodAudio(neighborhood);
                    if (self._daemonMode && self._bridgeConnected && !self._daemonWarmedUp)
                    {
                        self._daemonWarmedUp = true;
                        _ = self.Send("warmup", new Dictionary<string, object?>(StringComparer.Ordinal));
                    }

                    if (reopenFamilyPicker)
                    {
                        self.OpenFamilyPicker(house: 0, families: neighborhood.Families);
                    }
                    else if (self._safeModeBootError is { } bootErr)
                    {
                        self._safeModeBootError = null;
                        self.OpenModsOverlay(bootErr);
                    }
                }
                catch (Exception error)
                {
                    if (self._neighborhoodLoadGeneration == requestGen)
                    {
                        self.Renderer.DrawsLot = false;
                        Log($"Neighborhood: {error}");
                        self.HandleInitialOrNeighborhoodError(error);
                    }
                }
                finally
                {
                    if (self._neighborhoodLoadGeneration == requestGen)
                    {
                        self._loadingNeighborhood = false;
                        if (loaded || self.Ui.Neighborhood is not null || self._hasCompletedInitialLoad)
                        {
                            self.Ui.Loading = false;
                        }
                        self._audio?.StopLoadLoop();
                    }

                    self._startupLoadAudio?.Close();
                    self._startupLoadAudio = null;
                }
            });
        }
        catch (Exception error)
        {
            AppLog.Error($"Neighborhood load failed (N{targetNeighborhood}, mode {mode}): {error}");
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                if (self._neighborhoodLoadGeneration == requestGen)
                {
                    self.Renderer.DrawsLot = false;
                    Log($"Neighborhood: {error}");
                    self._loadingNeighborhood = false;
                    self._audio?.StopLoadLoop();
                    self.HandleInitialOrNeighborhoodError(error);
                }

                self._startupLoadAudio?.Close();
                self._startupLoadAudio = null;
            });
        }
    }

    public void ReturnToNeighborhood(bool saving, int? mode = null)
    {
        try
        {
            int targetMode = mode ?? NeighborhoodModeForCurrentLot();
            if (saving)
            {
                var weakSelf = new WeakReference<LiveController>(this);
                SendSaveRequest(succeeded =>
                {
                    if (!succeeded || !weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    self.ReturnToNeighborhood(saving: false, mode: targetMode);
                });
                return;
            }

            CloseCameraOverlay();
            CloseOptionsOverlay();
            DismissMenu();
            Ui.PanelMinimized = false;
            _lastAudioSceneRevision = -1;
            _lastAudioAnimationSequence = -1;
            if (_daemonMode)
            {
                if ((_currentHouse ?? 0) > 0)
                {
                    _ = Send("closeLot", new Dictionary<string, object?>(StringComparer.Ordinal) { ["save"] = false });
                }

                _currentHouse = 0;
                _frontendMusic = null;
                _simulatorZoom = null;
                _simulatorMode = null;
                _simulatorViewScrolling = null;
            }
            else
            {
                _audio?.Close();
                _audio = null;
                Bridge?.Stop(save: false);
                Bridge = null;
            }

            HomeLotHouse = null;
            AwayBuyBuildLocked = false;
            _simlessBuildMode = false;
            _simIDs = [];
            _lotSims.Clear();
            _placedObjectMeta.Clear();
            _pendingSaleRequests.Clear();
            _pendingDisposePreviewRequest = null;
            Ui.DisposePanel = null;
            ClosePictureInPicture();
            Ui.GameDialog = null;
            _localDialogToken = null;
            _localDialogResponse = null;
            Ui.BuyMode = false;
            Ui.BuildMode = false;
            Ui.SoundOptions.Dismiss();
            Ui.PluginPanel = null;
            CloseOutfitPicker();
            _activeNeighborhoodDialog = null;
            _pendingNeighborhood = null;
            _pendingNeighborhoodResponse = null;
            _pendingTravelSelection = null;
            _openingNeighborhoodLot = false;
            _switchingLot = false;
            _awaitingLotSwitchChange = false;
            ResetConnectedAdjacentHoldState();
            Ui.Loading = false;
            Renderer.Pan = Vector2.Zero;
            LoadBaseNeighborhood(mode: targetMode);
        }
        finally
        {
            if (!saving)
            {
                LotSaveDisabled = false;
            }
        }
    }

    private void ShowNeighborhoodError(Exception error)
    {
        AppLog.Error($"Neighborhood/session error: {error}");
        string summary = $"{error.GetType().Name}: {error.Message}";
        if (AppLog.CurrentLogPath is { Length: > 0 } logPath)
        {
            summary += $"\n\nLog saved to:\n{logPath}";
        }

        PresentLocalFeedback(Strings.MoveInErrorTitle(), summary);
    }

    private void HandleInitialOrNeighborhoodError(Exception error)
    {
        AppLog.Error($"Startup/neighborhood failure: {error}");
        AppLog.SetStage($"Error: {error.GetType().Name}", isSteadyState: true);
        if (!_hasCompletedInitialLoad && Ui.Neighborhood is null && (_currentHouse ?? 0) == 0)
        {
            // Keep Loading = true so LiveUI renders the dialog over the splash screen rather than
            // flipping ShowsLot to true over an empty black void with an hourglass cursor.
            Ui.Loading = true;
            string summary = $"{error.GetType().Name}: {error.Message}";
            string? logPath = AppLog.CurrentLogPath;
            string? logsDir = AppLog.CurrentLogsDirectory;
            if (!string.IsNullOrEmpty(logPath))
            {
                summary += $"\n\nLog saved to:\n{logPath}";
            }

            if (!Options.AnswerDialogs && !string.IsNullOrEmpty(logsDir) && Directory.Exists(logsDir))
            {
                var weakSelf = new WeakReference<LiveController>(this);
                PresentLocalConfirmation(
                    Strings.MoveInErrorTitle(),
                    summary,
                    "Open Logs",
                    "Quit",
                    response: choice =>
                    {
                        if (choice == 0)
                        {
                            AppLog.Flush();
                            AppLog.RevealLogInFileManager(logPath, logsDir);
                        }

                        if (weakSelf.TryGetTarget(out var self))
                        {
                            self.QuitRequested = true;
                        }
                    });
                return;
            }

            var weakFallback = new WeakReference<LiveController>(this);
            PresentLocalConfirmation(
                Strings.MoveInErrorTitle(),
                summary,
                Strings.OkButton(),
                "Quit",
                response: _ =>
                {
                    if (weakFallback.TryGetTarget(out var self))
                    {
                        self.QuitRequested = true;
                    }
                });
            return;
        }

        Ui.Loading = false;
        ShowNeighborhoodError(error);
    }

    private (string Title, string Message) MagicTownStrings(
        ushort table,
        int titleIndex,
        int messageIndex,
        string fallbackTitle,
        string fallbackMessage)
    {
        byte[]? data = null;
        try
        {
            data = Content.Read("GameData/UIText.iff");
        }
        catch
        {
            try
            {
                data = Content.Read("UIText.iff");
            }
            catch
            {
                data = null;
            }
        }

        if (data is null
            || OriginalStringResource.TryParse(data, Content.Language.Code, new HashSet<ushort> { table }) is not { } resource
            || !resource.Tables.TryGetValue(table, out IReadOnlyList<string>? entries))
        {
            return (fallbackTitle, fallbackMessage);
        }

        string title = titleIndex >= 0 && titleIndex < entries.Count && entries[titleIndex].Length > 0
            ? entries[titleIndex]
            : fallbackTitle;
        string message = messageIndex >= 0 && messageIndex < entries.Count && entries[messageIndex].Length > 0
            ? entries[messageIndex]
            : fallbackMessage;
        return (title, message);
    }

    /// <summary>Opens the household for occupied lots, or opens the family picker when clicking vacant
    /// residential lots.</summary>
    private void SelectNeighborhoodHouse(int house)
    {
        if (Ui.Neighborhood is not { } neighborhood || Ui.FamilyPicker is not null || Ui.ModsOverlay is not null || Ui.Credits is not null)
        {
            return;
        }

        PlayUISound("ui_nhood_click");
        if (_rezoneMode)
        {
            _rezoneMode = false;
            neighborhood.SetRezoneMode(false);
            BeginRezone(house);
            return;
        }

        if (_evictMode)
        {
            _evictMode = false;
            neighborhood.SetEvictMode(false);
            ConfirmEvict(house, neighborhood);
            return;
        }

        if (FamilyPlacement is { } family)
        {
            if (!neighborhood.IsVacantResidential(house))
            {
                PlayUISound("ui_nhood_error");
                return;
            }

            FamilyPlacement = null;
            OfferLot(house, family, neighborhood);
            return;
        }

        if (neighborhood.IsVacantResidential(house))
        {
            if (neighborhood.Mode == 7 && house is >= 90 and <= 92)
            {
                // Vacant Magic Town residential lots display string table 133 prompts unless a family is
                // actively being moved in.
                var (title, message) = MagicTownStrings(
                    table: 133,
                    titleIndex: 0,
                    messageIndex: 1,
                    fallbackTitle: "No one lives here!",
                    fallbackMessage: "You must pick a family and move them in first.");
                PresentLocalFeedback(title, message);
                return;
            }

            OpenFamilyPicker(house: house, families: neighborhood.Families);
            return;
        }

        OpenNeighborhoodHouse(house, createHousehold: false);
    }

    /// <summary>Rezones a lot by evicting the family, bulldozing the house, and prompting to select
    /// residential or community zoning using string tables 131, 250, and 251.</summary>
    private void BeginRezone(int house)
    {
        if (Ui.Neighborhood is not { } neighborhood || !neighborhood.HasLot(house))
        {
            return;
        }

        int mode = neighborhood.Mode;
        string yes = Strings.YesButton();
        string no = Strings.NoButton();
        var weakSelf = new WeakReference<LiveController>(this);
        if (neighborhood.IsOnVacation(house))
        {
            PresentLocalFeedback(Strings.VacationEvictTitle(), Strings.VacationEvictMessage());
        }
        else if (neighborhood.Occupant(house) is { } family)
        {
            string message = OriginalUIStrings.Format(
                Strings.RezoneEvictMessage(),
                [family.Name, OriginalUIStrings.Simoleons(family.NetWorth)]);
            PresentLocalConfirmation(
                Strings.RezoneEvictTitle(),
                message,
                yes,
                no,
                response: code =>
                {
                    if (code != 0 || !weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    self.PlayUISound("ui_nhood_bdoze_evict");
                    self.ReloadNeighborhood(
                        edit: $"evict:{house}",
                        mode: mode,
                        completion: () => self.BeginRezone(house));
                });
        }
        else if (!neighborhood.IsBulldozed(house))
        {
            PresentLocalConfirmation(
                Strings.RezoneBulldozeTitle(),
                Strings.RezoneBulldozeMessage(),
                yes,
                no,
                response: code =>
                {
                    if (code != 0 || !weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    self.PlayUISound("ui_nhood_bdoze_demolish");
                    self.ReloadNeighborhood(
                        edit: $"bulldoze:{house}",
                        mode: mode,
                        completion: () => self.BeginRezone(house));
                });
        }
        else
        {
            string current = Strings.ZoningType(neighborhood.ZoningIndex(house));
            string message = OriginalUIStrings.Format(Strings.RezoneMessage(), [current]);
            // Confirms lot rezoning where selecting Yes sets Residential, No sets Community, and Cancel
            // aborts.
            PresentLocalConfirmation(
                Strings.RezoneTitle(),
                message,
                Strings.ZoningType(0),
                Strings.ZoningType(1),
                Strings.CancelButton(),
                response: code =>
                {
                    if ((code != 0 && code != 1)
                        || code == neighborhood.ZoningIndex(house)
                        || !weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    self.ReloadNeighborhood(edit: $"rezone:{house}:{code}", mode: mode);
                });
        }
    }

    private void ConfirmEvict(int house, OriginalNeighborhoodOverlay neighborhood)
    {
        int mode = neighborhood.Mode;
        // Prevents lot eviction or bulldozing when the residing family is away on vacation.
        if (neighborhood.IsOnVacation(house))
        {
            PresentLocalFeedback(Strings.VacationEvictTitle(), Strings.VacationEvictMessage());
            return;
        }

        bool hasHouse = !neighborhood.IsBulldozed(house);
        if (neighborhood.Occupant(house) is not { } family)
        {
            if (hasHouse)
            {
                // Bulldozes an unoccupied lot's house directly using string table 131 confirmation entries.
                ConfirmBulldoze(house, mode, evictFirst: false);
                return;
            }

            PlayUISound("ui_nhood_error");
            PresentLocalFeedback(Strings.NothingToBulldozeTitle(), Strings.NothingToBulldozeMessage());
            return;
        }

        string message = OriginalUIStrings.Format(
            Strings.EvictMessage(),
            [family.Name, OriginalUIStrings.Simoleons(family.NetWorth)]);
        var weakSelf = new WeakReference<LiveController>(this);
        PresentLocalConfirmation(
            Strings.EvictTitle(),
            message,
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0 || !weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                self.PlayUISound("ui_nhood_bdoze_evict");
                if (hasHouse)
                {
                    // Evicting an occupied lot with a house immediately prompts whether to bulldoze the
                    // house, preserving the structure if the second prompt is cancelled.
                    self.ConfirmBulldoze(house, mode, evictFirst: true);
                }
                else
                {
                    self.ReloadNeighborhood(edit: $"evict:{house}", mode: mode);
                }
            });
    }

    private void ConfirmBulldoze(int house, int mode, bool evictFirst = false)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        PresentLocalConfirmation(
            Strings.BulldozeTitle(),
            Strings.BulldozeMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                if (code == 0)
                {
                    self.PlayUISound("ui_nhood_bdoze_demolish");
                    if (evictFirst)
                    {
                        if (self._daemonMode)
                        {
                            self.ReloadNeighborhood(edit: $"evict:{house};bulldoze:{house}", mode: mode);
                        }
                        else
                        {
                            self.ReloadNeighborhood(
                                edit: $"evict:{house}",
                                mode: mode,
                                completion: () =>
                                {
                                    if (weakSelf.TryGetTarget(out var target))
                                    {
                                        target.ReloadNeighborhood(edit: $"bulldoze:{house}", mode: mode);
                                    }
                                });
                        }
                    }
                    else
                    {
                        self.ReloadNeighborhood(edit: $"bulldoze:{house}", mode: mode);
                    }
                }
                else if (evictFirst)
                {
                    self.ReloadNeighborhood(edit: $"evict:{house}", mode: mode);
                }
            });
    }

    private void ChooseBannerAction(BannerAction action)
    {
        // While a travel dialog is active, only the return action is permitted to cancel and stay home.
        if (_activeNeighborhoodDialog is not null && action != BannerAction.ReturnHome)
        {
            PlayUISound("ui_nhood_error");
            return;
        }

        PlayUISound("ui_nhood_click");
        switch (action)
        {
            case BannerAction.ReturnHome:
                if (_activeNeighborhoodDialog is not null)
                {
                    RespondToNeighborhood(-1);
                }
                else
                {
                    ReloadNeighborhood(edit: null, mode: 1);
                }

                break;

            case BannerAction.Downtown:
            case BannerAction.Vacation:
            case BannerAction.StudioTown:
            case BannerAction.MagicTown:
                if (action.MapMode() is int mode)
                {
                    ReloadNeighborhood(edit: null, mode: mode);
                }

                break;

            case BannerAction.Rezone:
                FamilyPlacement = null;
                _rezoneMode = !_rezoneMode;
                if (_rezoneMode)
                {
                    _evictMode = false;
                    Ui.Neighborhood?.SetEvictMode(false);
                }

                Ui.Neighborhood?.SetRezoneMode(_rezoneMode);
                break;

            case BannerAction.Bulldoze:
                FamilyPlacement = null;
                _evictMode = !_evictMode;
                if (_evictMode)
                {
                    _rezoneMode = false;
                    Ui.Neighborhood?.SetRezoneMode(false);
                }

                Ui.Neighborhood?.SetEvictMode(_evictMode);
                break;

            case BannerAction.Quit:
                ConfirmQuit();
                break;

            case BannerAction.ImportFamily:
                OfferImport(0);
                break;

            case BannerAction.Previous:
            case BannerAction.Next:
                SwitchNeighborhood(forward: action == BannerAction.Next);
                break;

            case BannerAction.Mods:
                OpenModsOverlay();
                break;

            case BannerAction.Internet:
                ExportNeighborhoodWebPages();
                break;

            case BannerAction.Credits:
            case BannerAction.SimsLogo:
                OpenCredits();
                break;

            case BannerAction.SelectFamily:
            {
                // Opening the family picker directly allows creating families into the family bin or
                // selecting a family before choosing a lot.
                if (Ui.Neighborhood is not { } neighborhood || (neighborhood.Mode != 1 && neighborhood.Mode != 7))
                {
                    PlayUISound("ui_nhood_error");
                    return;
                }

                FamilyPlacement = null;
                _rezoneMode = false;
                _evictMode = false;
                neighborhood.SetRezoneMode(false);
                neighborhood.SetEvictMode(false);
                OpenFamilyPicker(house: 0, families: neighborhood.Families);
                break;
            }

            default:
                break;
        }
    }

    /// <summary>Iterates through <c>.FAM</c> files in the Import folder, prompting to import each family
    /// onto its lot (sending any existing family to the bin) or advance to the next file.</summary>
    private void OfferImport(int index)
    {
        if (Ui.Neighborhood is not { } neighborhood || index < 0 || index >= neighborhood.Imports.Count)
        {
            if (index == 0)
            {
                PlayUISound("ui_nhood_error");
            }

            return;
        }

        var family = neighborhood.Imports[index];
        static string Truncate32(string s) => s.Length <= 32 ? s : s[..32];
        string members = string.Join(", ", family.Members.Take(8).Select(Truncate32));
        string message = Truncate32(family.FamilyName) + (members.Length == 0 ? string.Empty : "\n" + members);
        var weakSelf = new WeakReference<LiveController>(this);
        PresentLocalConfirmation(
            Strings.ImportFamilyTitle(),
            message,
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                if (code == 0)
                {
                    self.ReloadNeighborhood(
                        edit: $"import:{index}",
                        mode: neighborhood.Mode,
                        errorMessage: ("Error", "Couldn't import the family"));
                }
                else
                {
                    self.OfferImport(index + 1);
                }
            });
    }

    private void SwitchNeighborhood(bool forward)
    {
        if (Ui.Neighborhood is not { } neighborhood || neighborhood.Mode != 1)
        {
            PlayUISound("ui_nhood_error");
            return;
        }

        int @base = _pendingNeighborhoodTarget ?? _currentNeighborhood;
        if (OriginalNeighborhoodOverlay.NeighborhoodAfter(@base, neighborhood.Neighborhoods, forward) is not int target
            || target == @base)
        {
            PlayUISound("ui_nhood_error");
            return;
        }

        _pendingNeighborhoodTarget = target;
        _rezoneMode = false;
        var weakSelf = new WeakReference<LiveController>(this);
        ReloadNeighborhood(
            edit: null,
            mode: 1,
            targetNeighborhood: target,
            completion: () =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                self._currentNeighborhood = target;
                if (self._pendingNeighborhoodTarget == target)
                {
                    self._pendingNeighborhoodTarget = null;
                }

                self.StoreNeighborhood(target);
            },
            failure: () =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                if (self._pendingNeighborhoodTarget == target)
                {
                    self._pendingNeighborhoodTarget = null;
                }
            });
    }
}
