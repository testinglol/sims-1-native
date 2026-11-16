namespace SimsSDL;

internal sealed partial class LiveController
{
    public void ScriptAnswerLocalDialog()
    {
        if (!ScriptAnswersDialogs || Ui.GameDialog is not { } dialog)
        {
            return;
        }

        if (string.Equals(_localDialogToken, dialog.Dialog.Token, StringComparison.Ordinal))
        {
            RespondDialog(dialog, code: 0);
            return;
        }

        GameDialog first = dialog.Dialog;
        if (ScriptDialogStop is { } stop
            && (first.Message.Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                || first.Title.Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                || (first.Kind == 10 && "Phone Book".Contains(stop, StringComparison.CurrentCultureIgnoreCase))
                || (first.Kind == 12 && "Spellbook".Contains(stop, StringComparison.CurrentCultureIgnoreCase))
                || (first.Kind == 15
                    && ("Cookbook".Contains(stop, StringComparison.CurrentCultureIgnoreCase)
                        || "Recipes".Contains(stop, StringComparison.CurrentCultureIgnoreCase)))))
        {
            ScriptAnswersDialogs = false;
            ScriptDialogStop = null;
            return;
        }

        if (!_answeredDialogs.Contains(first.Token))
        {
            _answeredDialogs.Add(first.Token);
            byte firstCode = first.Buttons.Count > 0 ? first.Buttons[0].Code : (byte)0;
            RespondDialog(
                dialog,
                code: first.Kind == 10 ? (byte)0 : firstCode,
                responseText: first.Kind == 10 ? "-1" : null);
        }
    }

    private void RespondDialog(OriginalDialogOverlay dialog, byte code, string? responseText = null)
    {
        if (string.Equals(_localDialogToken, dialog.Dialog.Token, StringComparison.Ordinal))
        {
            Action<byte>? response = _localDialogResponse;
            _localDialogToken = null;
            _localDialogResponse = null;
            Ui.GameDialog = null;
            response?.Invoke(code);
            return;
        }

        if (dialog.Submitting)
        {
            return;
        }

        if (Send("dialogResponse", new Dictionary<string, object?>
            {
                ["token"] = dialog.Dialog.Token,
                ["code"] = (int)code,
                ["text"] = responseText ?? dialog.Text,
            }) is not null)
        {
            _answeredDialogs.Add(dialog.Dialog.Token);
            dialog.Submitting = true;
        }
    }

    public IReadOnlyList<int> ScriptSimIDs => _simIDs;

    public bool ScriptSceneReady =>
        Renderer.Scene is not null
        && !Renderer.IsLoadingScene
        && _pendingViewRequestID is null
        && _pendingViewSceneRevision is null;

    public IReadOnlyList<string> ScriptSimAnimations => _simAnimations;

    public CGSize ScriptCanvas => _canvas;

    public CGPoint ScriptWindowPoint(double x, double y) =>
        new(x * ScriptPixelDensity, y * ScriptPixelDensity);

    public string? ScriptRequestTileTraversal(int tileX, int tileY, int level)
    {
        if (Send("testTileContents", new Dictionary<string, object?>
            {
                ["tileX"] = tileX,
                ["tileY"] = tileY,
                ["level"] = level,
            }) is not { } id)
        {
            return null;
        }

        _pendingScriptTileTraversals.Add(id);
        return id;
    }

    public bool ScriptTileTraversalPassed(string requestID, int expectedObjectID)
    {
        if (!_scriptTileTraversalResults.TryGetValue(requestID, out var result)
            || result.ObjectIDs is not { } objectIDs)
        {
            return false;
        }

        bool passed = result.Error is null
            && objectIDs.Contains(expectedObjectID)
            && new HashSet<int>(objectIDs).Count == objectIDs.Count;
        if (passed)
        {
            _scriptTileTraversalResults.Remove(requestID);
        }

        return passed;
    }

    private CGRect ScriptScreenRect(CGRect rect) =>
        new(rect.MinX * Scale, rect.MinY * Scale, rect.Width * Scale, rect.Height * Scale);

    public CGPoint? ScriptPointOnObject(int id)
    {
        if (ScriptPixel(id) is { } point)
        {
            return new CGPoint(point.X, point.Y);
        }

        return ScriptCentreOnObject(id);
    }

    public CGPoint? ScriptPointOnGround(int x, int y)
    {
        int maxY = (int)(_canvas.Height - 100.0 * Scale);
        int maxX = (int)_canvas.Width;
        for (int py = 2; py < maxY; py += 4)
        {
            for (int px = 2; px < maxX; px += 4)
            {
                var point = new Vector2(px, py);
                if (Renderer.GroundTileAt(point, SimdCanvas) is { } tile
                    && tile.X == x
                    && tile.Y == y
                    && Renderer.ObjectAt(point, SimdCanvas) is null)
                {
                    return new CGPoint(px, py);
                }
            }
        }

        return null;
    }

    public CGPoint? ScriptPointOnGroundCenter(int x, int y)
    {
        if (Renderer.ScreenPoint(x, y, SimdCanvas) is not { } point)
        {
            return null;
        }

        return new CGPoint(point.X, point.Y);
    }

    public CGPoint? ScriptCentreOnGround(int x, int y)
    {
        if (Renderer.CentreGroundTile(x, y, SimdCanvas) is not { } point)
        {
            return null;
        }

        return new CGPoint(point.X, point.Y);
    }

    public SimsSDL.Core.GroundTile? ScriptGroundTileAt(CGPoint point) =>
        Renderer.GroundTileAt(new Vector2((float)point.X, (float)point.Y), SimdCanvas);

    public string ScriptGroundProjectionInfo => Renderer.GroundProjectionInfo(SimdCanvas);

    private Vector2? ScriptPixel(int id)
    {
        int maxY = (int)_canvas.Height;
        int maxX = (int)_canvas.Width;
        for (int y = 2; y < maxY; y += 4)
        {
            for (int x = 2; x < maxX; x += 4)
            {
                var candidate = new Vector2(x, y);
                if (Renderer.ObjectAt(candidate, SimdCanvas) == id)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public CGPoint? ScriptCentreOnObject(int id)
    {
        LotZoom zoom = Renderer.Zoom;
        Vector2 pan = Renderer.Pan;
        Renderer.SetZoom(LotZoom.Far, instant: true);
        Renderer.Pan = Vector2.Zero;
        Vector2? found = ScriptPixel(id) is { } farPixel && Renderer.SceneViewport(SimdCanvas) is { } farViewport
            ? farViewport.ScenePoint(farPixel)
            : null;
        Renderer.SetZoom(zoom, instant: true);
        Renderer.Pan = pan;
        if (found is not { } scenePoint || Renderer.SceneViewport(SimdCanvas) is not { } viewport)
        {
            return null;
        }

        // Adjusts camera pan offset to center the target scene point in viewport.
        Renderer.Pan += viewport.Size / 2f - (viewport.Origin + scenePoint * viewport.Scale);
        if (Renderer.SceneViewport(SimdCanvas) is not { } centred)
        {
            return null;
        }

        Vector2 point = centred.Origin + scenePoint * centred.Scale;
        if (Renderer.ObjectAt(point, SimdCanvas) == id)
        {
            return new CGPoint(point.X, point.Y);
        }

        return ScriptPixel(id) is { } fallback ? new CGPoint(fallback.X, fallback.Y) : null;
    }

    public bool ScriptCatalogReady
    {
        get
        {
            if (_pendingCatalog is not null
                || _pendingBuildCatalog is not null
                || _pendingViewRequestID is not null
                || _pendingViewSceneRevision is not null
                || Renderer.IsLoadingScene)
            {
                return false;
            }

            return ScriptCatalogItems.Count > 0;
        }
    }

    /// <summary>Lists visible catalog items with affordability status; wall and floor patterns are charged
    /// per tile and are always considered selectable.</summary>
    public IReadOnlyList<(string Name, CGRect Rect, string Key, bool Affordable)> ScriptCatalogItems
    {
        get
        {
            int budget = Ui.Gameplay.State?.Budget ?? 0;
            if (Ui.BuyMode && Ui.Buy.Page is { } buyPage)
            {
                int buyCapacity = OriginalBuyLayout.Capacity(LogicalCanvas);
                int buyCount = Math.Min(buyPage.Items.Count, Math.Max(0, buyCapacity));
                var buyItems = new List<(string Name, CGRect Rect, string Key, bool Affordable)>(buyCount);
                for (int i = 0; i < buyCount; i++)
                {
                    BuyCatalogPage.Item item = buyPage.Items[i];
                    buyItems.Add((
                        item.Name,
                        ScriptScreenRect(OriginalBuyLayout.Thumbnail(i, LogicalCanvas)),
                        $"g{item.Guid}",
                        item.Price <= budget));
                }

                return buyItems;
            }

            if (!Ui.BuildMode || Ui.Build.TerrainMode is not null)
            {
                return [];
            }

            int capacity = Ui.Build.VisibleCapacity(LogicalCanvas);
            if (Ui.Build.ObjectPage is { } objectPage)
            {
                int objCount = Math.Min(objectPage.Items.Count, Math.Max(0, capacity));
                var objItems = new List<(string Name, CGRect Rect, string Key, bool Affordable)>(objCount);
                for (int i = 0; i < objCount; i++)
                {
                    BuyCatalogPage.Item item = objectPage.Items[i];
                    objItems.Add((
                        item.Name,
                        ScriptScreenRect(OriginalBuildLayout.Thumbnail(i, LogicalCanvas)),
                        $"g{item.Guid}",
                        item.Price <= budget));
                }

                return objItems;
            }

            IReadOnlyList<BuildCatalogPage.Item> buildPageItems = Ui.Build.Page?.Items ?? [];
            int count = Math.Min(buildPageItems.Count, Math.Max(0, capacity));
            var result = new List<(string Name, CGRect Rect, string Key, bool Affordable)>(count);
            for (int i = 0; i < count; i++)
            {
                BuildCatalogPage.Item item = buildPageItems[i];
                result.Add((
                    item.Name,
                    ScriptScreenRect(Ui.Build.ItemThumbnail(i, LogicalCanvas)),
                    item.Guid is uint guid ? $"g{guid}" : $"p{item.Id}",
                    item.Guid is null || item.Price <= budget));
            }

            return result;
        }
    }

    public string? ScriptCatalogSelection
    {
        get
        {
            if (Ui.BuildMode && Ui.Build.SelectedObjectGUID is { } guid)
            {
                return $"g{guid}";
            }

            if (Ui.BuildMode && Ui.Build.Selected is { } id)
            {
                return $"p{id}";
            }

            return Ui.Buy.Selected is { } buyGuid ? $"g{buyGuid}" : null;
        }
    }

    public IReadOnlyDictionary<int, int> ScriptEditableObjects
    {
        get
        {
            var dict = new Dictionary<int, int>();
            foreach (EditableLotObject obj in _editableObjects)
            {
                if (obj.Movable || obj.Sellable)
                {
                    dict.TryAdd(obj.Id, obj.Direction);
                }
            }

            return dict;
        }
    }

    public bool ScriptSceneContains(int id) => Renderer.ContainsObject(id);

    public bool ScriptBuildCursorShown =>
        Renderer.ShowsBuildCursor
        && Renderer.BuildCursor is not null
        && _desiredBuildCursor is not null
        && _pendingBuildCursor is null
        && _checkedBuildCursor == _desiredBuildCursor;

    public bool ScriptPieHeadSettled =>
        Ui.PieHeadImage is not null
        && _desiredPieHead is not null
        && _pendingPieHead is null
        && _checkedPieHead == _desiredPieHead;

    public bool ScriptPlacementChecked =>
        _desiredPlacement is not null
        && _pendingPlacement is null
        && _checkedPlacement == _desiredPlacement;

    public bool ScriptPlacementValid => ScriptPlacementChecked && Renderer.PlacementIsValid == true;

    public IReadOnlyList<(string Label, CGRect Rect)> ScriptControls
    {
        get
        {
            if (Ui.HelpVisible)
            {
                return Ui.HelpOverlay.Controls(OverlayLogicalCanvas)
                    .Select(c => (c.Label, OverlayScriptScreenRect(c.Rect)))
                    .ToList();
            }

            if (Ui.BudgetReportVisible)
            {
                return Ui.BudgetReport.Controls()
                    .Select(c => (c.Label, ScriptScreenRect(c.Rect)))
                    .ToList();
            }

            if (Ui.GameDialog is { Submitting: false, Dialog.Blocking: true } blockingDialog)
            {
                return blockingDialog.Controls()
                    .Select(c => (c.Label, OverlayScriptScreenRect(c.Rect)))
                    .ToList();
            }

            List<(string Label, CGRect Rect)> dialogControls = Ui.GameDialog is { Submitting: false } openDialog
                ? openDialog.Controls().Select(c => (c.Label, OverlayScriptScreenRect(c.Rect))).ToList()
                : [];

            if (Ui.Album.Visible)
            {
                return Ui.Album.Controls(LogicalCanvas)
                    .Select(c => (c.Label, ScriptScreenRect(c.Rect)))
                    .ToList();
            }

            if (Ui.Credits is { } credits)
            {
                return credits.Controls(_canvas, Renderer.UIScale);
            }

            if (Ui.FamilyPicker is { } familyPicker)
            {
                return familyPicker.Controls(_canvas, Renderer.UIScale);
            }

            if (Ui.GenericSimsCallPicker is { } callPicker)
            {
                return callPicker.Controls(LogicalCanvas)
                    .Select(c => (c.Label, ScriptScreenRect(c.Rect)))
                    .ToList();
            }

            if (Ui.HouseholdCreation is { } creation)
            {
                return creation.Controls(_canvas, Renderer.UIScale);
            }

            if (Ui.Neighborhood is { } neighborhood)
            {
                return
                [
                    .. neighborhood.BannerControls(_canvas, Renderer.UIScale),
                    .. neighborhood.Controls(_canvas, Renderer.UIScale),
                ];
            }

            if (Ui.OutfitPicker is { } outfit)
            {
                return outfit.Controls(LogicalCanvas)
                    .Select(c => (c.Label, ScriptScreenRect(c.Rect)))
                    .ToList();
            }

            if (Ui.PluginPanel is { } pluginPanel)
            {
                return pluginPanel.Controls(LogicalCanvas)
                    .Select(c => (c.Label, ScriptScreenRect(c.Rect)))
                    .ToList();
            }

            IReadOnlyList<(string Label, CGRect Rect)>? rawPanel;
            if (Ui.Camera.Visible)
            {
                rawPanel = Ui.Camera.Controls(LogicalCanvas);
            }
            else if (Ui.BuyMode)
            {
                rawPanel = Ui.Buy.Controls(LogicalCanvas);
            }
            else if (Ui.BuildMode)
            {
                rawPanel = Ui.Build.Controls(LogicalCanvas);
            }
            else if (Ui.OptionsMode)
            {
                var optionControls = new List<(string Label, CGRect Rect)>(Ui.OptionsPanel.Controls(LogicalCanvas));
                if (Ui.OptionsPanel.CurrentPage == OriginalOptionsPanelOverlay.Page.Sound)
                {
                    optionControls.AddRange(Ui.SoundOptions.Controls(LogicalCanvas));
                }

                rawPanel = optionControls;
            }
            else
            {
                rawPanel = null;
            }

            IReadOnlyList<(string Label, CGRect Rect)>? panel;
            if (Ui.ShowsDisposePanel && rawPanel is not null)
            {
                var disposePanel = new List<(string Label, CGRect Rect)>();
                foreach ((string label, CGRect rect) in rawPanel)
                {
                    if (rect.MinX < 220)
                    {
                        disposePanel.Add((label, rect));
                    }
                }

                disposePanel.Add(("Dispose Object", Ui.DisposePanelRect(LogicalCanvas)));
                panel = disposePanel;
            }
            else
            {
                panel = rawPanel;
            }

            var pipControls = new List<(string Label, CGRect Rect)>();
            if (!Ui.Album.Visible && !Ui.Camera.Visible
                && Ui.PictureInPictureRect(LogicalCanvas, Scale) is { } pipRect)
            {
                if (Ui.PictureInPictureCloseRect(LogicalCanvas, Scale) is { } closeRect)
                {
                    pipControls.Add(("Close Picture-in-Picture", ScriptScreenRect(closeRect)));
                }

                pipControls.Add(("Picture-in-Picture", ScriptScreenRect(pipRect)));
            }

            // Universal controls sort after panels so panel controls match first.
            CGRect reportFundsRect = OriginalGameplayOverlay.FundsRect.OffsetBy(0, LogicalCanvas.Height - 150);
            var combinedPanelAndUniversal = new List<(string Label, CGRect Rect)>();
            foreach ((string label, CGRect rect) in panel ?? Ui.Gameplay.Controls(LogicalCanvas, panelMinimized: Ui.PanelMinimized))
            {
                combinedPanelAndUniversal.Add((label, ScriptScreenRect(rect)));
            }

            foreach (OriginalUniversalControl control in OriginalUniversalControl.All)
            {
                combinedPanelAndUniversal.Add((
                    control.Label,
                    ScriptScreenRect(control.Rect(LogicalCanvas, Ui.HelpButtonSize))));
            }

            combinedPanelAndUniversal.Add(("Camera Mode", ScriptScreenRect(Ui.Camera.ModeRect(LogicalCanvas))));
            combinedPanelAndUniversal.Add(("Budget", ScriptScreenRect(reportFundsRect)));

            return [.. dialogControls, .. pipControls, .. combinedPanelAndUniversal];
        }
    }

    public CGPoint? ScriptRelationshipPoint(int neighborID)
    {
        if (Ui.Gameplay.TutorialButtonForRelationship(neighborID, LogicalCanvas) is not { } rect)
        {
            return null;
        }

        CGRect screen = ScriptScreenRect(rect);
        return new CGPoint(screen.MidX, screen.MidY);
    }

    public int? ScriptRelationshipPopupID => Ui.Gameplay.RelationshipPopupId;

    public string ScriptScreen
    {
        get
        {
            if (Ui.HelpVisible) return "help";
            if (Ui.Album.Visible) return "album";
            if (Ui.Credits is not null) return "credits";
            if (Ui.FamilyPicker is not null) return "picker";
            if (Ui.GenericSimsCallPicker is not null) return "picker";
            if (Ui.OutfitPicker is not null) return "picker";
            if (Ui.HouseholdCreation is not null) return _bridgeConnected ? "creation" : "loading";
            if (Ui.Loading || _loadingNeighborhood || _openingNeighborhoodLot || _switchingLot) return "loading";
            if (Ui.Neighborhood is not null) return "neighborhood";
            return "lot";
        }
    }

    public bool ScriptPreviewsReady
    {
        get
        {
            if (Ui.HouseholdCreation is not { } creation
                || _pendingHouseholdPreviewRequest is not null
                || _pendingHouseholdAppearanceRequest is not null)
            {
                return false;
            }

            if (!creation.ShowsFamilyPage)
            {
                return creation.AppearancePreviewSelection() is not null;
            }

            foreach (var (key, selection) in creation.MemberPreviewSelections())
            {
                if (!_familyPortraits.TryGetValue(key, out string? sig)
                    || !string.Equals(sig, Signature(selection), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public CGPoint? ScriptPointOnMenuEntry(string label)
    {
        if (Ui.PieMenu is not { } menu)
        {
            return null;
        }

        menu.Prepare(_canvas, Scale);
        return menu.Point(label, Scale);
    }

    public IReadOnlyList<string> ScriptMenuLabels =>
        Ui.PieMenu is { } menu ? menu.Nodes.Select(n => n.Label).ToList() : [];

    /// <summary>Handles a primary press, initiating a construction gesture via <see cref="PrimaryGesture"/>
    /// or treating it as a click. <paramref name="ctrl"/> and <paramref name="alt"/> activate deletion mode
    /// for architectural tools like walls, floors, and paint.</summary>
    public void Press(CGPoint point, CGSize canvas, bool shift, bool alt, bool ctrl = false)
    {
        _canvas = canvas;
        Ui.Gameplay.DismissTooltips();
        try
        {
            if (Ui.BudgetReportVisible)
            {
                Click(point);
                PrimaryGesture = false;
                return;
            }

            if (Ui.HelpVisible)
            {
                Click(point);
                PrimaryGesture = false;
                return;
            }

            if ((Ui.Loading || _openingNeighborhoodLot || _switchingLot || _exportingWebPages
                    || (_loadingNeighborhood && (Ui.Neighborhood is null || _editingNeighborhood)))
                && !HasBlockingDialog)
            {
                PrimaryGesture = false;
                return;
            }

            if (Ui.GameDialog is { Submitting: false } dialog
                && (dialog.Dialog.Blocking
                    || (Ui.PieMenu is null
                        && (dialog.Dialog.Buttons.Count > 0 || dialog.Dialog.Kind == 10 || dialog.TutorialCloseBox is not null)
                        && dialog.Contains(point, OverlayScale))))
            {
                try
                {
                    dialog.Prepare(canvas, OverlayScale);
                }
                catch
                {
                }

                if (dialog.BeginButtonPress(point, OverlayScale))
                {
                    _dialogButtonTracking = true;
                    PrimaryGesture = true;
                    return;
                }

                if (!dialog.Dialog.Blocking)
                {
                    Click(point);
                    PrimaryGesture = false;
                    return;
                }
            }

            if (!HasBlockingDialog && Ui.OutfitPicker is null
                && Ui.GenericSimsCallPicker is { } callPicker
                && callPicker.BeginOKPress(Local(point), LogicalCanvas))
            {
                _chooserOKTracking = true;
                PrimaryGesture = true;
                return;
            }

            if (Ui.GameDialog is { Dialog.Blocking: false } nonBlockingDialog)
            {
                nonBlockingDialog.BlurInput();
            }

            bool deleting = alt || ctrl;
            if (!HasBlockingDialog && Ui.OutfitPicker is null && Ui.SoundOptions.IsVisible
                && (!Ui.SoundOptions.IsEmbedded || Ui.SoundOptions.Contains(point, _canvas, Scale)))
            {
                if (Ui.SoundOptions.PointerDown(point, _canvas, Scale) is { } change)
                {
                    SendAudioVolume(change);
                }

                PrimaryGesture = true;
                _soundGesture = true;
                return;
            }

            if (!HasBlockingDialog && (Ui.Album.Visible || Ui.Camera.Visible))
            {
                Click(point);
                PrimaryGesture = false;
                return;
            }

            if (!HasBlockingDialog && _pictureInPicture is not null
                && Ui.PictureInPictureCloseRect(LogicalCanvas, Scale) is { } pipCloseRect
                && pipCloseRect.Contains(Local(point)))
            {
                _pipCloseTracking = true;
                Ui.PictureInPictureClosePressed = true;
                Ui.PictureInPictureCloseHovered = true;
                PrimaryGesture = true;
                return;
            }

            if (!HasBlockingDialog && _pictureInPicture is not null
                && Ui.PictureInPictureRect(LogicalCanvas, Scale) is { } pipRect
                && pipRect.Contains(Local(point)))
            {
                Click(point);
                PrimaryGesture = false;
                return;
            }

            if (deleting && DeleteBuildObject(point))
            {
                PrimaryGesture = false;
                return;
            }

            if (StartPlacementDrag(point))
            {
                PrimaryGesture = true;
                return;
            }

            if (shift && FillRoom(point, removing: deleting))
            {
                PrimaryGesture = false;
                return;
            }

            PrimaryGesture = StartTerrainStroke(point)
                || StartWallStroke(point, rectangle: shift, deleting: deleting)
                || StartWallpaperStroke(point, deleting: deleting)
                || StartFloorStroke(point, deleting: deleting);
            if (!PrimaryGesture)
            {
                Click(point);
            }
        }
        finally
        {
            Ui.Gameplay.DismissTooltips();
        }
    }

    public bool ConsumesTutorialRightClick(CGPoint point)
    {
        if (Ui.TutorialIconRect(LogicalCanvas) is not { } bounds)
        {
            return false;
        }

        return bounds.Contains(Local(point));
    }

    public void Drag(CGPoint point)
    {
        Pointer = point;
        if (_dialogButtonTracking)
        {
            Ui.GameDialog?.Hover(point, OverlayScale);
            return;
        }

        if (_chooserOKTracking)
        {
            Ui.GenericSimsCallPicker?.Hover(Local(point), LogicalCanvas);
            return;
        }

        if (_pipCloseTracking)
        {
            bool inside = Ui.PictureInPictureCloseRect(LogicalCanvas, Scale)?.Contains(Local(point)) == true;
            Ui.PictureInPictureClosePressed = inside;
            Ui.PictureInPictureCloseHovered = inside;
            return;
        }

        if (_soundGesture)
        {
            if (Ui.SoundOptions.PointerDragged(point, _canvas, Scale) is { } change)
            {
                SendAudioVolume(change);
            }

            return;
        }

        if (_placementAnchor is { } anchor)
        {
            // Orients the placed object toward the drag direction while keeping it anchored at its
            // placement tile.
            if (Renderer.GroundTileAt(Simd(point), SimdCanvas) is { } tile
                && Ui.Buy.Selection.Face(tile.X - anchor.Tile.X, tile.Y - anchor.Tile.Y))
            {
                PlayUISound("ui_object_rotate");
            }

            UpdatePlacement(anchor.Point);
            return;
        }

        UpdateTerrainStroke(point);
        UpdateWallpaperStroke(point);
        UpdateBuildPreview(point);
    }

    private bool StartPlacementDrag(CGPoint point)
    {
        if (!Ui.ObjectEditing || Renderer.Speed != 0 || HasBlockingDialog || Ui.PieMenu is not null || Ui.PluginPanel is not null
            || Ui.Buy.Selection.Target is null || OverPanelControls(point)
            || OriginalGameplayOverlay.ContainsPanel(Local(point), LogicalCanvas, panelMinimized: IsLivePanelMinimized)
            || (!Ui.ShowsDisposePanel && Ui.Buy.ActionAt(Local(point), LogicalCanvas) is not null)
            || Renderer.GroundTileAt(Simd(point), SimdCanvas) is not { } tile)
        {
            return false;
        }

        _placementAnchor = (point, tile);
        return true;
    }

    public void Release(CGPoint point)
    {
        if (_pendingPieMenuWarp is { } warp)
        {
            _pendingPieMenuWarp = null;
            if (Ui.PieMenu is not null)
            {
                WarpPointer?.Invoke(warp);
                Hover(warp, _canvas);
            }
        }

        if (_dialogButtonTracking)
        {
            _dialogButtonTracking = false;
            PrimaryGesture = false;
            bool wasPressed = Ui.GameDialog?.EndButtonPress() == true;
            Ui.GameDialog?.Hover(point, OverlayScale);
            if (wasPressed)
            {
                Click(point);
            }

            return;
        }

        if (_chooserOKTracking)
        {
            _chooserOKTracking = false;
            PrimaryGesture = false;
            bool wasPressed = Ui.GenericSimsCallPicker?.EndOKPress() == true;
            Ui.GenericSimsCallPicker?.Hover(Local(point), LogicalCanvas);
            if (wasPressed)
            {
                Click(point);
            }

            return;
        }

        if (_pipCloseTracking)
        {
            _pipCloseTracking = false;
            PrimaryGesture = false;
            Ui.PictureInPictureClosePressed = false;
            bool inside = Ui.PictureInPictureCloseRect(LogicalCanvas, Scale)?.Contains(Local(point)) == true;
            Ui.PictureInPictureCloseHovered = inside;
            if (inside)
            {
                PlayUISound("ui_click");
                ClosePictureInPicture();
            }

            return;
        }

        if (!PrimaryGesture)
        {
            return;
        }

        PrimaryGesture = false;
        if (_soundGesture)
        {
            _soundGesture = false;
            bool wasVisible = Ui.SoundOptions.IsVisible;
            if (Ui.SoundOptions.PointerUp(point, _canvas, Scale) is { } change)
            {
                SendAudioVolume(change);
            }

            if (wasVisible && !Ui.SoundOptions.IsVisible)
            {
                PlayUISound("ui_click");
            }

            return;
        }

        if (_placementAnchor is { } anchor)
        {
            _placementAnchor = null;
            EditObject(anchor.Point);
            UpdatePlacement(point);
            return;
        }

        FinishTerrainStroke(point);
        FinishWallStroke(point);
        FinishWallpaperStroke();
        FinishFloorStroke(point);
    }

    private void Click(CGPoint point)
    {
        // Allows sub-neighborhood switches while a neighborhood map is visible, unless an edit mutation or
        // lot load is actively running.
        if ((Ui.Loading || _openingNeighborhoodLot || _switchingLot || _exportingWebPages
                || (_loadingNeighborhood && (Ui.Neighborhood is null || _editingNeighborhood)))
            && !HasBlockingDialog)
        {
            return;
        }

        if (Ui.HelpVisible)
        {
            if (Ui.HelpOverlay.Click(OverlayLocal(point), OverlayLogicalCanvas))
            {
                SetHelpVisible(false);
            }

            return;
        }

        if (Ui.BudgetReportVisible)
        {
            if (Ui.BudgetReport.IsOKButton(Local(point)))
            {
                PlayUISound("ui_click");
                Ui.BudgetReportVisible = false;
                Ui.BudgetReport.ClearHover();
            }

            return;
        }

        CGRect reportFundsRect = OriginalGameplayOverlay.FundsRect.OffsetBy(0, LogicalCanvas.Height - 150);
        if (!HasBlockingDialog && Ui.ShowsLot && !Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && !Ui.Camera.Visible
            && Ui.BudgetReportValues is not null && Ui.Gameplay.State?.Budget is not null && reportFundsRect.Contains(Local(point)))
        {
            PlayUISound("ui_whoosh");
            Ui.BudgetReportVisible = true;
            Ui.Gameplay.PanelTip = null;
            Ui.BudgetReport.Hover(Local(point));
            return;
        }

        if (Ui.PieMenu is null && Ui.GameDialog is { Submitting: false, Dialog.Blocking: false } nonBlockingDialog
            && (nonBlockingDialog.Dialog.Buttons.Count > 0 || nonBlockingDialog.Dialog.Kind == 10 || nonBlockingDialog.TutorialCloseBox is not null)
            && nonBlockingDialog.Contains(point, OverlayScale))
        {
            try
            {
                nonBlockingDialog.Prepare(_canvas, OverlayScale);
                if (nonBlockingDialog.Dialog.Kind == 10)
                {
                    switch (nonBlockingDialog.PhoneBookActionAt(point, OverlayScale))
                    {
                        case OriginalDialogOverlay.PhoneBookAction.ChangedSelection:
                        case null:
                            break;
                        case OriginalDialogOverlay.PhoneBookAction.Call call:
                            RespondDialog(nonBlockingDialog, code: 1, responseText: call.NeighborId.ToString(CultureInfo.InvariantCulture));
                            break;
                        case OriginalDialogOverlay.PhoneBookAction.Cancel:
                            RespondDialog(nonBlockingDialog, code: 0, responseText: "-1");
                            break;
                    }
                }
                else if (nonBlockingDialog.Choice(point, OverlayScale) is { } code)
                {
                    RespondDialog(nonBlockingDialog, code: code);
                }
            }
            catch (Exception error)
            {
                Log($"Dialog: {error}");
            }

            return;
        }

        if (!HasBlockingDialog && !Ui.Album.Visible && !Ui.Camera.Visible
            && _pictureInPicture is { } window
            && Ui.PictureInPictureRect(LogicalCanvas, Scale) is { } pipRect
            && pipRect.Contains(Local(point)))
        {
            if (Ui.PictureInPictureCloseRect(LogicalCanvas, Scale)?.Contains(Local(point)) == true)
            {
                PlayUISound("ui_click");
                ClosePictureInPicture();
                return;
            }

            CentreView(window.ObjectID);
            ClosePictureInPicture();
            return;
        }

        if (!HasBlockingDialog && Ui.TutorialIconRect(LogicalCanvas) is { } iconRect && iconRect.Contains(Local(point)))
        {
            _ = Send("tutorialInfo");
            return;
        }

        if (!HasBlockingDialog && Ui.Album.Visible)
        {
            if (Ui.Album.Hit(Local(point), LogicalCanvas) is { } albumAction)
            {
                AlbumAction(albumAction);
            }
            else
            {
                _ = Ui.Album.ClickCaption(Local(point), LogicalCanvas);
            }

            return;
        }

        if (!HasBlockingDialog && Ui.ShowsLot && Ui.Camera.ModeRect(LogicalCanvas).Contains(Local(point)))
        {
            EnterCameraMode();
            return;
        }

        if (!HasBlockingDialog && Ui.Camera.Visible)
        {
            if (OriginalUniversalControl.Hit(point, _canvas, Scale, Ui.HelpButtonSize) is { } control)
            {
                if (!Ui.IsControlEnabled(control.Kind))
                {
                    PlayUISound("ui_error");
                    return;
                }

                if (control.Kind != OriginalUniversalControl.Action.Options)
                {
                    PlayUISound("ui_click");
                }

                RecordButton(image: control.ImageId);
                UniversalControl(control.Kind);
                return;
            }

            if (OriginalSpeedControl.Hit(point, _canvas, Scale) is not null)
            {
                return;
            }

            if (Ui.Camera.Hit(Local(point), LogicalCanvas) is { } cameraAction)
            {
                switch (cameraAction)
                {
                    case OriginalCameraOverlay.Action.Size size:
                        Ui.Camera.SelectPresetSize(size.Value);
                        break;
                    case OriginalCameraOverlay.Action.CustomSize:
                        Ui.Camera.Size = 3;
                        break;
                    case OriginalCameraOverlay.Action.Quality quality:
                        Ui.Camera.Quality = quality.Value;
                        break;
                    case OriginalCameraOverlay.Action.Album:
                        try
                        {
                            Ui.Album.Open(Path.Combine(NeighborhoodUserData, "PhotoAlbum"), newest: true);
                        }
                        catch
                        {
                            PresentLocalFeedback("Photo Album", "Could not open the photo album.");
                        }

                        break;
                }

                PlayUISound("ui_click");
            }
            else if (Ui.Camera.CaptureRect is { } rect && rect.Contains(Local(point)))
            {
                PendingPhoto = ScriptScreenRect(rect);
            }

            return;
        }

        if (Ui.OutfitPicker is { } outfitPicker)
        {
            if (outfitPicker.Click(Local(point), LogicalCanvas) is { } action)
            {
                HandleOutfitPicker(action, outfitPicker);
            }

            return;
        }

        if (Ui.GenericSimsCallPicker is { } callPicker)
        {
            if (callPicker.Click(Local(point), LogicalCanvas) is { } row)
            {
                ChooseGenericSimsCallPerson(row);
            }

            return;
        }

        if (Ui.PluginPanel is { } pluginPanel && !HasBlockingDialog && !Ui.SoundOptions.IsVisible)
        {
            // Plugin panels act modally, preventing clicks from falling through to the underlying lot.
            switch (pluginPanel.HitTest(point, _canvas, Scale))
            {
                case OriginalPluginPanelOverlay.Hit.Close:
                    Ui.PluginPanel = null;
                    PlayUISound("ui_click");
                    break;
                case OriginalPluginPanelOverlay.Hit.Action hitAction:
                    PlayUISound("ui_click");
                    _ = Send("pluginAction", new Dictionary<string, object?>
                    {
                        ["packId"] = pluginPanel.PackId,
                        ["panelId"] = pluginPanel.Description.Id,
                        ["action"] = hitAction.Value,
                    });
                    break;
                case OriginalPluginPanelOverlay.Hit.Inside:
                case null:
                    break;
            }

            return;
        }

        if (Ui.GameDialog is { } localDialog && string.Equals(_localDialogToken, localDialog.Dialog.Token, StringComparison.Ordinal))
        {
            try
            {
                localDialog.Prepare(_canvas, OverlayScale);
                if (localDialog.Choice(point, OverlayScale) is { } code)
                {
                    RespondDialog(localDialog, code: code);
                }
            }
            catch (Exception error)
            {
                Log($"Dialog: {error}");
            }

            return;
        }

        if (!HasBlockingDialog && Ui.GameDialog?.Contains(point, OverlayScale) != true && ClickNeighborhoodScreens(point))
        {
            return;
        }

        if (!HasActiveLot)
        {
            return;
        }

        try
        {
            if (Ui.GameDialog is { Submitting: false } dialog
                && (dialog.Dialog.Blocking
                    || (Ui.PieMenu is null && (dialog.Dialog.Buttons.Count > 0 || dialog.Dialog.Kind == 10 || dialog.TutorialCloseBox is not null))))
            {
                dialog.Prepare(_canvas, OverlayScale);
                if (dialog.Dialog.Kind == 10)
                {
                    switch (dialog.PhoneBookActionAt(point, OverlayScale))
                    {
                        case OriginalDialogOverlay.PhoneBookAction.ChangedSelection:
                        case null:
                            break;
                        case OriginalDialogOverlay.PhoneBookAction.Call call:
                            RespondDialog(dialog, code: 1, responseText: call.NeighborId.ToString(CultureInfo.InvariantCulture));
                            break;
                        case OriginalDialogOverlay.PhoneBookAction.Cancel:
                            RespondDialog(dialog, code: 0, responseText: "-1");
                            break;
                    }
                }
                else if (dialog.Choice(point, OverlayScale) is { } code)
                {
                    RespondDialog(dialog, code: code);
                }

                if (dialog.Dialog.Blocking || dialog.Contains(point, OverlayScale))
                {
                    return;
                }
            }

            if (OriginalUniversalControl.Hit(point, _canvas, Scale, Ui.HelpButtonSize) is { } universal)
            {
                if (!Ui.IsControlEnabled(universal.Kind))
                {
                    PlayUISound("ui_error");
                    return;
                }

                if (universal.Kind != OriginalUniversalControl.Action.Options)
                {
                    PlayUISound("ui_click");
                }

                RecordButton(image: universal.ImageId);
                UniversalControl(universal.Kind);
                return;
            }

            if (OriginalSpeedControl.Hit(point, _canvas, Scale) is { } speed)
            {
                if (Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode || Ui.Camera.Visible)
                {
                    return;
                }

                DismissMenu();
                if (OriginalSpeedControl.All.FirstOrDefault(c => c.Speed == speed) is { } speedControl)
                {
                    RecordButton(image: speedControl.ImageId);
                }

                bool paused = IsInLiveMode ? Renderer.Speed == 0 : _liveModePaused;
                SendSpeed(speed == 0 && paused ? _lastRunningSpeed : speed);
                return;
            }

            CGPoint local = Local(point);
            if (Ui.OptionsMode && Ui.OptionsPanel.ButtonAt(local, LogicalCanvas) is { } optionButton)
            {
                if (Ui.OptionsPanel.IsEnabled(optionButton))
                {
                    ChooseOption(optionButton);
                }

                return;
            }

            if (Ui.OptionsMode && Ui.OptionsPanel.OptionAt(local, LogicalCanvas) is { } playOption)
            {
                if (playOption.IsImplemented())
                {
                    TogglePlayOption(playOption);
                }

                return;
            }

            if (Ui.OptionsMode && Ui.OptionsPanel.RadioAt(local, LogicalCanvas) is { } radioHit)
            {
                if (radioHit.Setting.IsImplemented())
                {
                    SetDetail(radioHit.Setting, radioHit.Level);
                }

                return;
            }

            if (Ui.BuildMode)
            {
                if (Ui.ShowsDisposePanel && Ui.DisposePanelRect(LogicalCanvas).Contains(local))
                {
                    SellHeldObject();
                    UpdatePlacement(point);
                    return;
                }

                if ((!Ui.ShowsDisposePanel || local.X < 220) && Ui.Build.ActionAt(local, LogicalCanvas) is { } buildAction)
                {
                    switch (buildAction)
                    {
                        case OriginalBuildOverlay.Action.Page pageAction:
                            PlayUISound("ui_click");
                            RequestBuildCatalog(offset: pageAction.Offset);
                            break;
                        case OriginalBuildOverlay.Action.Kind kindAction:
                            BuildCatalogPage.CatalogKind kind = kindAction.Value;
                            PlayUISound("ui_click");
                            DismissMenu();
                            ClearBuildCursor();
                            Ui.Buy.CancelSelection();
                            _desiredPlacement = null;
                            _checkedPlacement = null;
                            Renderer.UpdatePlacement(null);
                            if (kind == BuildCatalogPage.CatalogKind.Roof)
                            {
                                _pendingRoofsVisible = true;
                                _pendingWallMode = "up";
                                var roofPayload = new Dictionary<string, object?>
                                {
                                    ["visible"] = true,
                                    ["doorWindowTool"] = false,
                                };
                                if (Renderer.StoryCount > 1)
                                {
                                    _pendingViewLevel = Renderer.StoryCount;
                                    roofPayload["level"] = Renderer.StoryCount;
                                }

                                _pendingViewSceneRevision = null;
                                _pendingViewRequestID = Send("roofs", roofPayload);
                            }
                            else if (kind == BuildCatalogPage.CatalogKind.Water && EffectiveViewLevel != 1)
                            {
                                SelectStory(1, doorWindowTool: false);
                            }
                            else if (EffectiveRoofsVisible)
                            {
                                _pendingRoofsVisible = false;
                                _pendingViewSceneRevision = null;
                                _pendingViewRequestID = Send("roofs", new Dictionary<string, object?>
                                {
                                    ["visible"] = false,
                                    ["doorWindowTool"] = false,
                                });
                            }

                            Ui.Build.ChangeKind(kind);
                            RequestBuildCatalog(offset: 0);
                            break;
                        case OriginalBuildOverlay.Action.Select selectPattern:
                            Ui.Buy.CancelSelection();
                            _desiredPlacement = null;
                            _checkedPlacement = null;
                            Renderer.UpdatePlacement(null);
                            Ui.Build.Choose(selectPattern.Id);
                            if (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Roof)
                            {
                                _ = Send("roof", new Dictionary<string, object?> { ["style"] = selectPattern.Id });
                            }

                            break;
                        case OriginalBuildOverlay.Action.RoofPitch pitchAction:
                            PlayUISound("ui_click");
                            Ui.RoofPitch = pitchAction.Pitch;
                            _ = Send("roof", new Dictionary<string, object?> { ["pitch"] = pitchAction.Pitch });
                            break;
                        case OriginalBuildOverlay.Action.ObjectTool toolAction:
                            OriginalBuildObjectTool tool = toolAction.Tool;
                            PlayUISound("ui_click");
                            DismissMenu();
                            bool isDoorWindow = tool is OriginalBuildObjectTool.Door or OriginalBuildObjectTool.Window;
                            if (isDoorWindow && EffectiveRoofsVisible)
                            {
                                _pendingRoofsVisible = false;
                                _pendingWallMode = "up";
                                _pendingViewSceneRevision = null;
                                _pendingViewRequestID = Send("walls", new Dictionary<string, object?>
                                {
                                    ["mode"] = "up",
                                    ["doorWindowTool"] = true,
                                });
                            }
                            else if (isDoorWindow)
                            {
                                _ = Send("view", new Dictionary<string, object?>
                                {
                                    ["doorWindowTool"] = true,
                                });
                            }
                            else if (tool == OriginalBuildObjectTool.Plants && EffectiveViewLevel != 1)
                            {
                                SelectStory(1, doorWindowTool: false);
                            }
                            else if (tool == OriginalBuildObjectTool.Stairs && Renderer.StoryCount > 1 && EffectiveViewLevel != 2)
                            {
                                SelectStory(2, doorWindowTool: false);
                            }
                            else if (tool.Category() is null)
                            {
                                if (EffectiveRoofsVisible)
                                {
                                    _pendingRoofsVisible = false;
                                    _pendingViewSceneRevision = null;
                                    _pendingViewRequestID = Send("roofs", new Dictionary<string, object?>
                                    {
                                        ["visible"] = false,
                                        ["doorWindowTool"] = false,
                                    });
                                }
                                else
                                {
                                    _ = Send("view", new Dictionary<string, object?>
                                    {
                                        ["editing"] = true,
                                        ["doorWindowTool"] = false,
                                    });
                                }
                            }

                            Ui.Build.ChangeObjectTool(tool);
                            Ui.Buy.ClearCategory(tool.Category() ?? 0);
                            _pendingBuildCatalog = null;
                            if (tool.Category() is not null)
                            {
                                RequestBuildCatalog(offset: 0);
                            }

                            break;
                        case OriginalBuildOverlay.Action.SelectObject selectObj:
                            ClearBuildCursor();
                            if (_floorBrush is not null)
                            {
                                _floorBrush = null;
                                Renderer.UpdateFloorBrush(null);
                            }

                            if (Ui.ChooseCatalogObject(selectObj.Guid))
                            {
                                _ = Send("cursorObjectCreated");
                            }
                            else
                            {
                                PlayUISound("ui_error");
                            }

                            break;
                        case OriginalBuildOverlay.Action.Terrain:
                            PlayUISound("ui_click");
                            DismissMenu();
                            Ui.Buy.CancelSelection();
                            Ui.Build.ChangeTerrainMode(TerrainBrushMode.Raise);
                            if (EffectiveViewLevel != 1 || EffectiveRoofsVisible)
                            {
                                SelectStory(1, doorWindowTool: false);
                            }
                            else
                            {
                                _ = Send("view", new Dictionary<string, object?>
                                {
                                    ["editing"] = true,
                                    ["doorWindowTool"] = false,
                                });
                            }

                            break;
                        case OriginalBuildOverlay.Action.TerrainMode terrainModeAction:
                            PlayUISound("ui_click");
                            DismissMenu();
                            Ui.Build.ChangeTerrainMode(terrainModeAction.Mode);
                            if (EffectiveViewLevel != 1 || EffectiveRoofsVisible)
                            {
                                SelectStory(1, doorWindowTool: false);
                            }
                            else
                            {
                                _ = Send("view", new Dictionary<string, object?>
                                {
                                    ["editing"] = true,
                                    ["doorWindowTool"] = false,
                                });
                            }

                            break;
                        case OriginalBuildOverlay.Action.History historyAction:
                            ConstructionHistoryAction history = historyAction.Value;
                            bool canAct = history == ConstructionHistoryAction.Undo
                                ? Ui.Gameplay.State?.CanUndo == true
                                : Ui.Gameplay.State?.CanRedo == true;
                            if (!canAct)
                            {
                                break;
                            }

                            PlayUISound("ui_click");
                            ushort? selectedPattern = Ui.Build.Selected;
                            DismissMenu();
                            if (selectedPattern is { } keptPattern)
                            {
                                Ui.Build.Choose(keptPattern);
                            }

                            _ = Send(history.RawValue());
                            break;
                    }
                }
                else if (Ui.ObjectEditing && Renderer.Speed == 0)
                {
                    EditObject(point);
                }
                else if (Renderer.Speed == 0 && Ui.Build.Kind == BuildCatalogPage.CatalogKind.Wall
                    && Ui.Build.SelectedItem is { } wallItem
                    && Renderer.WallAt(Simd(point), SimdCanvas) is { } wall
                    && wall.EdgeBit is { } edge)
                {
                    SendWithBuildSound("architecture", new Dictionary<string, object?>
                    {
                        ["command"] = "PATTERN_DOT",
                        ["x"] = wall.X,
                        ["y"] = wall.Y,
                        ["level"] = wall.Level,
                        ["edge"] = edge,
                        ["side"] = wall.Side ?? 0,
                        ["x2"] = wall.Direction,
                        ["y2"] = wall.Direction,
                        ["pattern"] = wallItem.Id,
                        ["style"] = 0,
                    });
                }
                else if (Renderer.Speed == 0
                    && (Ui.Build.Kind == BuildCatalogPage.CatalogKind.Floor || Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water)
                    && Ui.Build.SelectedItem is { } floorItem
                    && Renderer.FloorHitAt(Simd(point), SimdCanvas) is { } hit)
                {
                    string command = Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water && floorItem.Id == 65535
                        ? "POOL_RECT"
                        : "FLOOR_RECT";
                    SendWithBuildSound("architecture", new Dictionary<string, object?>
                    {
                        ["command"] = command,
                        ["x"] = hit.Tile.X,
                        ["y"] = hit.Tile.Y,
                        ["level"] = Renderer.ViewLevel,
                        ["x2"] = 0,
                        ["y2"] = 0,
                        ["pattern"] = floorItem.Id,
                        ["style"] = hit.Style,
                        ["half"] = hit.Half,
                    });
                }

                UpdatePlacement(point);
                return;
            }

            if (Ui.BuyMode)
            {
                if (Ui.ShowsDisposePanel && Ui.DisposePanelRect(LogicalCanvas).Contains(local))
                {
                    SellHeldObject();
                    UpdatePlacement(point);
                    return;
                }

                if ((!Ui.ShowsDisposePanel || local.X < 220) && Ui.Buy.ActionAt(local, LogicalCanvas) is { } buyAction)
                {
                    switch (buyAction)
                    {
                        case OriginalBuyOverlay.Action.Category cat:
                            PlayUISound("ui_click");
                            Ui.Buy.ClearCategory(cat.Value);
                            RequestCatalog(cat.Value, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.FunctionCategory fnCat:
                            PlayUISound("ui_click");
                            Ui.Buy.ClearFunctionCategory(fnCat.Index);
                            RequestCatalog(Ui.Buy.Category, subsort: Ui.Buy.FunctionSubsort, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.FunctionSubsort fnSub:
                            PlayUISound("ui_click");
                            Ui.Buy.ClearFunctionSubsort(fnSub.Index);
                            RequestCatalog(Ui.Buy.Category, subsort: fnSub.Index, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.FunctionBack:
                            PlayUISound("ui_click");
                            Ui.Buy.LeaveFunctionSubsort();
                            RequestCatalog(Ui.Buy.Category, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.Room room:
                            PlayUISound("ui_click");
                            Ui.Buy.ClearRoom(room.Index);
                            RequestCatalog(Ui.Buy.Category, room: room.Index, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.SubSort sub:
                            PlayUISound("ui_click");
                            Ui.Buy.ClearSubSort(sub.Index);
                            RequestCatalog(Ui.Buy.Category, room: Ui.Buy.Room, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.Back:
                            PlayUISound("ui_click");
                            Ui.Buy.LeaveSubSort();
                            RequestCatalog(-1, room: Ui.Buy.Room, offset: 0);
                            break;
                        case OriginalBuyOverlay.Action.Page page:
                            PlayUISound("ui_click");
                            RequestCatalog(
                                Ui.Buy.Category,
                                room: Ui.Buy.RoomMode ? Ui.Buy.Room : null,
                                subsort: Ui.Buy.FunctionSubSorting ? Ui.Buy.FunctionSubsort : null,
                                offset: page.Offset);
                            break;
                        case OriginalBuyOverlay.Action.Select sel:
                            if (!Ui.Buy.Choose(sel.Guid, Ui.Gameplay.State?.Budget))
                            {
                                PlayUISound("ui_error");
                            }

                            break;
                    }
                }
                else if (Renderer.Speed == 0)
                {
                    EditObject(point);
                }

                UpdatePlacement(point);
                return;
            }

            if (Ui.PieMenu is { } menu)
            {
                menu.Prepare(_canvas, Scale);
                (SimInteraction? Action, bool KeepOpen) result = menu.Choose(point, Scale);
                if (result.Action is { } action
                    && menu.SelectedSim == _selectedSim
                    && ((menu.ObjectID == 0 && _pieGround is not null) || Renderer.ContainsObject(menu.ObjectID)))
                {
                    Dictionary<string, object> rawParams = action.InteractParameters(menu.ObjectID);
                    var parameters = new Dictionary<string, object?>(rawParams.Count, StringComparer.Ordinal);
                    foreach (var kv in rawParams)
                    {
                        parameters[kv.Key] = kv.Value;
                    }

                    string operation;
                    if (_pieGround is { } ground && menu.ObjectID == 0)
                    {
                        operation = "groundInteract";
                        parameters["x"] = ground.Tile.PlacementX;
                        parameters["y"] = ground.Tile.PlacementY;
                        parameters["level"] = ground.Level;
                    }
                    else
                    {
                        operation = "interact";
                    }

                    string? interaction = Send(operation, parameters);
                    Ui.Gameplay.RecordPieChoice(local);
                    PlayUISound("ui_piemenu_select");
                    PlayUISoundOnResponse(interaction, success: "ui_queued", failure: "ui_error");
                }
                else if (result.KeepOpen)
                {
                    CGPoint clickAnchor = menu.Anchor;
                    menu.Prepare(_canvas, Scale);
                    if (menu.Layout is { } layout && double.Hypot(layout.Center.X - clickAnchor.X, layout.Center.Y - clickAnchor.Y) > 0.5)
                    {
                        var target = new CGPoint(layout.Center.X * Scale, layout.Center.Y * Scale);
                        WarpPointer?.Invoke(target);
                        Hover(target, _canvas);
                    }
                    else
                    {
                        menu.Hover(point, Scale);
                    }
                }

                if (!result.KeepOpen)
                {
                    DismissMenu();
                }

                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.Selection(local, LogicalCanvas) is { } sim)
            {
                DismissMenu();
                PlayUISound("ui_click");
                _selectedSim = sim;
                RecordButton(person: sim);
                RequestAutoCenterOnSelection();
                _ = Send("selectSim", new Dictionary<string, object?> { ["objectID"] = sim });
                return;
            }

            if (!Ui.OptionsMode && Ui.Gameplay.Cancellation(local, LogicalCanvas) is { } token)
            {
                DismissMenu();
                Ui.Gameplay.MarkActionCancelRequested(token);
                _ = Send("cancelAction", new Dictionary<string, object?>
                {
                    ["objectID"] = _selectedSim,
                    ["token"] = token,
                });
                PlayUISound("ui_queue_delete");
                return;
            }

            if (!Ui.OptionsMode && Ui.Gameplay.HasUncancellableAction(local, LogicalCanvas))
            {
                DismissMenu();
                PlayUISound("ui_error");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.PageInterests(local, LogicalCanvas))
            {
                DismissMenu();
                PlayUISound("ui_click");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.RelationshipControl(local, LogicalCanvas))
            {
                DismissMenu();
                PlayUISound("ui_click");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickRelationship(local, LogicalCanvas))
            {
                if (UsesNativeEngine && Ui.Gameplay.RelationshipPopupId is { } relId)
                {
                    _ = Send("relationshipPopup", new Dictionary<string, object?> { ["neighborID"] = relId });
                }

                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickCareerSummary(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.InventoryControl(local, LogicalCanvas))
            {
                DismissMenu();
                PlayUISound("ui_click");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickSkill(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickFameSkill(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickPetSkill(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickHouseHint(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.ClickPersonality(local, LogicalCanvas))
            {
                PlayUISound("ui_click");
                PlayUISound("ui_whoosh");
                return;
            }

            if (!Ui.OptionsMode && !Ui.PanelMinimized && Ui.Gameplay.SubpanelAt(local, LogicalCanvas) is { } panel)
            {
                DismissMenu();
                PlayUISound("ui_click");
                RecordButton(image: panel.ImageId());
                Ui.Gameplay.Choose(panel);
                return;
            }

            if (OriginalGameplayOverlay.ContainsPanel(local, LogicalCanvas, panelMinimized: IsLivePanelMinimized))
            {
                DismissMenu();
                return;
            }

            DismissMenu();
            if (_selectedSim == 0)
            {
                PlayUISound("ui_error");
                return;
            }

            if (Renderer.ObjectAt(Simd(point), SimdCanvas) is { } targetObject
                && Send("menu", new Dictionary<string, object?> { ["objectID"] = targetObject }) is { } menuId)
            {
                _pendingMenu = new MenuRequest
                {
                    Id = menuId,
                    Object = targetObject,
                    Caller = _selectedSim,
                    Anchor = local,
                };
            }
            else if (Renderer.GroundTileAt(Simd(point), SimdCanvas) is { } tile
                && Send("groundMenu", new Dictionary<string, object?>
                {
                    ["x"] = tile.PlacementX,
                    ["y"] = tile.PlacementY,
                    ["level"] = Renderer.ViewLevel,
                }) is { } groundMenuId)
            {
                _pendingMenu = new MenuRequest
                {
                    Id = groundMenuId,
                    Object = 0,
                    Caller = _selectedSim,
                    Anchor = local,
                    Ground = (tile, Renderer.ViewLevel),
                };
            }
            else
            {
                PlayUISound("ui_error");
            }
        }
        catch (Exception error)
        {
            DismissMenu();
            Log($"Interaction menu: {error}");
        }
    }

    public void Hover(CGPoint point, CGSize canvas)
    {
        _canvas = canvas;
        try
        {
            Pointer = point;
            if (Ui.HelpVisible)
            {
                Ui.HelpOverlay.Hover(OverlayLocal(point), OverlayLogicalCanvas);
                Ui.Gameplay.ClearInventoryHover();
                return;
            }

            Ui.HelpOverlay.Hover(null);
            Ui.Camera.Pointer = Local(point);
            if (Ui.Album.Visible)
            {
                Ui.Album.Hover(Local(point), LogicalCanvas);
                Ui.Camera.ClearHover();
                Ui.Gameplay.PanelTip = null;
                Ui.HoveredUniversalControl = null;
                Ui.HoveredSpeedControl = null;
                Ui.Gameplay.ClearInventoryHover();
                return;
            }

            Ui.Album.ClearHover();
            if (Ui.BudgetReportVisible)
            {
                HoverCursor = "Arrow";
                Ui.BudgetReport.Hover(Local(point));
                Ui.Gameplay.PanelTip = null;
                Ui.HoveredUniversalControl = null;
                Ui.HoveredSpeedControl = null;
                Ui.Gameplay.ClearInventoryHover();
                return;
            }

            Ui.BudgetReport.ClearHover();
            if (Ui.Camera.Visible && Ui.GameDialog is null && Ui.PieMenu is null)
            {
                Ui.Camera.Hover(Local(point), LogicalCanvas);
                if (Ui.Camera.ConsumeWhoosh())
                {
                    PlayUISound("ui_whoosh");
                }
            }
            else
            {
                Ui.Camera.ClearHover();
            }

            if (Ui.GameDialog is { Submitting: false } dialog
                && (dialog.Dialog.Blocking
                    || (Ui.PieMenu is null
                        && (dialog.Dialog.Buttons.Count > 0 || dialog.Dialog.Kind == 10 || dialog.TutorialCloseBox is not null)
                        && dialog.Contains(point, OverlayScale))))
            {
                Ui.Gameplay.ClearInventoryHover();
                Ui.HoveredUniversalControl = null;
                Ui.HoveredSpeedControl = null;
                Ui.Buy.ClearHover();
                Ui.Build.ClearHover();
                ShowCatalogPopup(null);
                dialog.Hover(point, OverlayScale);
                return;
            }

            if (Ui.OutfitPicker is { } outfitPicker)
            {
                Ui.Gameplay.ClearInventoryHover();
                outfitPicker.Hover(Local(point), LogicalCanvas);
                Ui.Gameplay.HoveredPeopleTip = outfitPicker.Tooltip(LogicalCanvas);
                return;
            }

            if (Ui.GenericSimsCallPicker is { } callPicker)
            {
                callPicker.Hover(Local(point), LogicalCanvas);
                return;
            }

            if (Ui.PluginPanel is { } pluginPanel && !Ui.SoundOptions.IsVisible)
            {
                Ui.Gameplay.ClearInventoryHover();
                pluginPanel.Hover(point, _canvas, Scale);
                return;
            }

            if (Ui.Neighborhood is { } neighborhood)
            {
                Ui.Gameplay.ClearInventoryHover();
                if (Ui.Credits is { } credits)
                {
                    credits.Hover(point, _canvas, Renderer.UIScale);
                }
                else if (Ui.FamilyPicker is { } familyPicker)
                {
                    familyPicker.Hover(point, _canvas, Renderer.UIScale);
                }
                else
                {
                    neighborhood.UpdateHover(point, _canvas, Renderer.UIScale);
                    neighborhood.UpdateBannerHover(
                        point,
                        _canvas,
                        quickTips: Ui.OptionsPanel.PlayOptions.QuickTips,
                        uiScale: Renderer.UIScale);
                }

                return;
            }

            if (Ui.HouseholdCreation is { } creation)
            {
                creation.Hover(
                    point,
                    _canvas,
                    quickTips: Ui.OptionsPanel.PlayOptions.QuickTips,
                    uiScale: Renderer.UIScale);
                Ui.Gameplay.ClearInventoryHover();
                return;
            }

            UpdatePlacement(point);
            UpdateCutaway(point);
            Ui.SoundOptions.Hover(point, _canvas, Scale);
            bool suppressHover = Ui.GameDialog is not null || Ui.PieMenu is not null;
            if (Ui.OptionsMode && !suppressHover)
            {
                Ui.OptionsPanel.Hover(Local(point), LogicalCanvas, hoveredSoundIndex: Ui.SoundOptions.HoveredIndex);
            }

            Ui.PieMenu?.Hover(point, Scale);
            RequestPieHeadPreview();
            int? highlighted = Ui.PieMenu?.Highlighted;
            if (highlighted is { } hi && hi != _lastPieHighlight)
            {
                PlayUISound("ui_piemenu_highlight");
            }

            _lastPieHighlight = highlighted;
            CGPoint local = Local(point);
            if (suppressHover || !IsInLiveMode)
            {
                Ui.Gameplay.HoveredPerson = null;
                Ui.Gameplay.HoveredAction = null;
            }
            else
            {
                if (Ui.PanelMinimized)
                {
                    Ui.Gameplay.HoveredPerson = null;
                }
                else
                {
                    Ui.Gameplay.UpdatePersonHover(local, LogicalCanvas);
                }

                Ui.Gameplay.HoveredAction = Ui.Gameplay.HoveredActionIndex(local, LogicalCanvas);
            }

            var panel = new CGPoint(local.X, local.Y - (LogicalCanvas.Height - 150));
            Ui.Gameplay.HoveredPanelPoint = suppressHover || Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode || Ui.Camera.Visible || Ui.PanelMinimized
                ? null
                : panel;
            Ui.Gameplay.IndicatorHovered = suppressHover
                ? null
                : CycleIndicator.GlyphRect.Contains(panel)
                    ? CycleIndicator.Hover.Weather
                    : CycleIndicator.ReelRect.Contains(panel)
                        ? CycleIndicator.Hover.Season
                        : null;

            (int Index, CGRect Rect)? panelTip = null;
            Ui.HoveredUniversalControl = null;
            Ui.HoveredSpeedControl = null;
            if (!suppressHover)
            {
                if (OriginalUniversalControl.Hit(point, _canvas, Scale, Ui.HelpButtonSize) is { } control)
                {
                    if (Ui.IsControlEnabled(control.Kind))
                    {
                        Ui.HoveredUniversalControl = control.Kind;
                        panelTip = (control.TooltipIndex, control.Rect(LogicalCanvas, Ui.HelpButtonSize));
                    }
                }
                else if (!Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && !Ui.Camera.Visible
                    && OriginalSpeedControl.Hit(point, _canvas, Scale) is { } speed
                    && OriginalSpeedControl.All.FirstOrDefault(c => c.Speed == speed) is { } speedControl)
                {
                    Ui.HoveredSpeedControl = speed;
                    CGRect rect = new CGRect(speedControl.Position, speedControl.Size)
                        .OffsetBy(0, LogicalCanvas.Height - OriginalSpeedControl.PanelHeight);
                    panelTip = (speedControl.TooltipIndex, rect);
                }
                else if (Ui.Camera.ModeRect(LogicalCanvas).Contains(local))
                {
                    panelTip = (19, Ui.Camera.ModeRect(LogicalCanvas));
                }
                else if (Ui.Gameplay.State?.FamilyFriends is { } count
                    && Ui.Gameplay.FamilyFriendsRect(count).OffsetBy(0, LogicalCanvas.Height - 150).Contains(local))
                {
                    CGRect rect = Ui.Gameplay.FamilyFriendsRect(count).OffsetBy(0, LogicalCanvas.Height - 150);
                    panelTip = (20, rect);
                }
                else if (Ui.Gameplay.State?.Budget is not null)
                {
                    CGRect rect = OriginalGameplayOverlay.FundsRect.OffsetBy(0, LogicalCanvas.Height - 150);
                    if (rect.Contains(local))
                    {
                        panelTip = (14, rect);
                    }
                }
            }

            Ui.Gameplay.PanelTip = panelTip;
            Ui.Gameplay.HoveredNeed = suppressHover || Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode || Ui.Camera.Visible || Ui.PanelMinimized
                ? null
                : Ui.Gameplay.NeedIndexAtPanelPoint(panel, LogicalCanvas);
            Ui.Gameplay.HoveredPeopleTip = suppressHover || Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode || Ui.Camera.Visible
                ? null
                : Ui.Gameplay.PeopleTooltip(local, LogicalCanvas, panelMinimized: Ui.PanelMinimized);

            bool overDispose = Ui.ShowsDisposePanel && Ui.DisposePanelRect(LogicalCanvas).Contains(local);
            Ui.PictureInPictureCloseHovered = !suppressHover && !Ui.Album.Visible && !Ui.Camera.Visible
                && Ui.PictureInPictureCloseRect(LogicalCanvas, Scale)?.Contains(local) == true;
            Ui.Buy.Hover(!suppressHover && Ui.BuyMode && !overDispose ? local : null);
            Ui.Build.Hover(!suppressHover && Ui.BuildMode && !overDispose ? local : null);
            if (!suppressHover && !overDispose && (Ui.BuyMode || Ui.BuildMode))
            {
                if (Ui.BuildMode && Ui.Build.ToolTipAt(
                        local,
                        LogicalCanvas,
                        canUndo: Ui.Gameplay.State?.CanUndo == true,
                        canRedo: Ui.Gameplay.State?.CanRedo == true) is { } buildTip)
                {
                    Ui.Gameplay.HoveredPeopleTip = (buildTip.Text, buildTip.Rect);
                }
                else if (Ui.BuyMode && Ui.Buy.ToolTipAt(local, LogicalCanvas) is { } buyTip)
                {
                    Ui.Gameplay.HoveredPeopleTip = (buyTip.Text, buyTip.Rect);
                }
            }

            if (!suppressHover && !Ui.Album.Visible && !Ui.Camera.Visible
                && Ui.PictureInPictureRect(LogicalCanvas, Scale) is { } pipRect
                && pipRect.Contains(local))
            {
                Ui.Gameplay.HoveredPerson = null;
                // Uses custom string table 305 text if provided, falling back to UI text string table 156
                // entry 0.
                string? customText = _pictureInPicture?.Text;
                string tip = !string.IsNullOrEmpty(customText) ? customText : Ui.Strings.PictureInPictureTip();
                Ui.Gameplay.HoveredPeopleTip = (tip, pipRect);
            }
            else if (!suppressHover && !Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode
                && !Ui.HelpVisible && !Ui.Album.Visible && !Ui.Camera.Visible
                && Ui.OutfitPicker is null && Ui.PluginPanel is null
                && Ui.Gameplay.HoveredPeopleTip is null
                && (Ui.PanelMinimized || local.Y < LogicalCanvas.Height - 150)
                && !OverPanelControls(point)
                && Renderer.ObjectAt(Simd(point), SimdCanvas) is { } target
                && _lotSims.TryGetValue(target, out LotSimInfo? sim)
                && !sim.HideHover
                && sim.TooltipName.Length > 0)
            {
                Ui.Gameplay.HoveredPeopleTip = (sim.TooltipName, new CGRect(local.X + 17, local.Y - 4, 0, 0));
            }

            if (suppressHover || IsLivePanelMinimized)
            {
                Ui.Gameplay.HoverInventory(new CGPoint(-1000, -1000), LogicalCanvas);
            }
            else
            {
                Ui.Gameplay.HoverInventory(local, LogicalCanvas);
            }

            (OriginalCatalogPopup.Item Item, OriginalGameplayOverlay.Part Icon)? popup = suppressHover || overDispose
                ? null
                : Ui.BuyMode
                    ? Ui.Buy.PopupAt(local, LogicalCanvas)
                    : Ui.BuildMode
                        ? Ui.Build.PopupAt(local, LogicalCanvas)
                        : null;
            ShowCatalogPopup(popup);
        }
        finally
        {
            HoverCursor = CursorStem(point);
        }
    }

    private void ShowCatalogPopup((OriginalCatalogPopup.Item Item, OriginalGameplayOverlay.Part Icon)? item)
    {
        bool wasVisible = Ui.CatalogPopup.IsVisible;
        if (item is { } popup)
        {
            Ui.CatalogPopup.Hover(popup.Item, thumbnail: popup.Icon);
        }
        else
        {
            Ui.CatalogPopup.Hover(null);
        }

        bool nowVisible = Ui.CatalogPopup.IsVisible;
        if (!wasVisible && nowVisible)
        {
            PlayUISound("ui_whoosh");
        }

        _catalogPopupWasVisible = nowVisible;
    }

    public bool IsBusyCursor =>
        Ui.Loading
        || _loadingNeighborhood
        || _openingNeighborhoodLot
        || _switchingLot
        || _exportingWebPages
        || _pendingNeighborhood is not null
        || (Ui.ShowsLot && Renderer.Scene is null);

    public string CursorName
    {
        get
        {
            if (IsBusyCursor)
            {
                return "HOURGLASS";
            }

            if (EdgeScrollCursor() is { } scroll)
            {
                return scroll;
            }

            if (Ui.Neighborhood is not null && !Ui.Loading && Ui.GameDialog is null)
            {
                if (FamilyPlacement is not null) return "MOVEIN";
                if (_evictMode) return "BULLDOZE";
                if (_rezoneMode) return "Rezone2";
            }

            if ((DeleteModifier || ScriptDeleteModifier)
                && (string.Equals(HoverCursor, "Arrow", StringComparison.Ordinal) || string.Equals(HoverCursor, "SimsMove", StringComparison.Ordinal))
                && Ui.BuildMode && Ui.Build.TerrainMode is null
                && Pointer is { } point
                && Ui.PieMenu is null && Ui.GameDialog is null
                && Local(point).Y < LogicalCanvas.Height - 150)
            {
                return "SimsDelete";
            }

            return HoverCursor;
        }
    }

    private string? EdgeScrollCursor()
    {
        if (!Ui.OptionsPanel.PlayOptions.EdgeScrolling || HasBlockingDialog || !Ui.ShowsLot
            || Ui.OutfitPicker is not null || Ui.PluginPanel is not null
            || Pointer is not { } pointer
            || EdgeScrollRegion(pointer) is not { } region)
        {
            return null;
        }

        return (region.X, region.Y) switch
        {
            (-1, 0) => "LEFT",
            (1, 0) => "RIGHT",
            (0, -1) => "UP",
            (0, 1) => "DOWN",
            (1, -1) => "UPRIGHT",
            (-1, -1) => "UPLEFT",
            (1, 1) => "DOWNRIGHT",
            _ => "DOWNLEFT",
        };
    }

    /// <summary>Determines the edge-scrolling direction from a 32-pixel margin (5 pixels in fullscreen)
    /// around the viewport area above the control panel.</summary>
    private (int X, int Y)? EdgeScrollRegion(CGPoint pointer)
    {
        double margin = 32.0 * Scale;
        double bottom = _canvas.Height - 150.0 * Scale;
        if (pointer.Y >= bottom)
        {
            return null;
        }

        int x = pointer.X < margin ? -1 : pointer.X >= _canvas.Width - margin ? 1 : 0;
        int y = pointer.Y < margin ? -1 : pointer.Y >= bottom - margin ? 1 : 0;
        return x == 0 && y == 0 ? null : (x, y);
    }

    /// <summary>Computes edge-scrolling velocity, ramping linearly over the first 1500 ms to a base speed
    /// of 562.5 pixels per second (doubled horizontally in corners) and capped to 562.5.</summary>
    private (double X, double Y) EdgeScrollVelocity((int X, int Y) region, double elapsed)
    {
        double vx = -region.X * 562.5 * (region.Y == 0 ? 1.0 : 2.0);
        double vy = -region.Y * 562.5;
        if (elapsed < 1500.0)
        {
            double ramp = elapsed / 1500.0;
            vx *= ramp;
            vy *= ramp;
        }

        double length = Math.Sqrt(vx * vx + vy * vy);
        if (length > 562.5)
        {
            double clamp = 562.5 / length;
            vx *= clamp;
            vy *= clamp;
        }

        return (vx, vy);
    }

    /// <summary>Updates edge scrolling per frame by multiplying velocity by elapsed time (capped at 100
    /// ms), rounding by adding 0.5 before truncating, and scaling by the interface scale.</summary>
    public void Tick(CGSize canvas)
    {
        _canvas = canvas;
        if (Ui.Loading && Ui.TravelLoadScreen is null && _splashProgressQueue.Count > 0)
        {
            double uptime = CurrentUptime();
            if (uptime - _lastSplashProgressStep >= 1.0)
            {
                _lastSplashProgressStep = uptime;
                Ui.LoadingText = _splashProgressQueue[0];
                _splashProgressQueue.RemoveAt(0);
            }
        }

        bool catalogPopupVisible = Ui.CatalogPopup.IsVisible;
        if (!_catalogPopupWasVisible && catalogPopupVisible)
        {
            PlayUISound("ui_whoosh");
        }

        _catalogPopupWasVisible = catalogPopupVisible;
        if (Ui.Credits is { IsFinished: true })
        {
            CloseCredits();
        }

        FollowPanelAnchor();
        if (Ui.PieMenu is { } menu && _pendingPieHead is null && Ui.PieHeadImage is not null && menu.TickDeadzoneRotation())
        {
            RequestPieHeadPreview();
        }

        double now = CurrentUptime() * 1000.0;
        if (UsesNativeEngine && HasActiveLot && Ui.ShowsLot && Ui.GenericSimsCallPicker is null
            && _pendingGenericSimsCallChoice is null && _pendingGenericSimsCallPoll is null
            && now - _lastGenericSimsCallPoll >= 500.0)
        {
            _lastGenericSimsCallPoll = now;
            var parameters = new Dictionary<string, object?>();
            if (Renderer.SceneViewport(SimdCanvas) is { } viewport
                && Renderer.GroundTileAt(viewport.Size / 2f, SimdCanvas) is { } center)
            {
                parameters["centerX"] = center.PlacementX;
                parameters["centerY"] = center.PlacementY;
            }

            _pendingGenericSimsCallPoll = Send("genericSimsCallPersonPicker", parameters);
        }

        if (UsesNativeEngine && HasActiveLot && Ui.ShowsLot && Renderer.Zoom != _simulatorZoom
            && Send("zoom", new Dictionary<string, object?> { ["scale"] = (int)Renderer.Zoom }) is not null)
        {
            _simulatorZoom = Renderer.Zoom;
        }

        string interfaceMode = Ui.OptionsMode ? "options" : Ui.BuyMode ? "buy" : Ui.BuildMode ? "build" : "live";
        if (UsesNativeEngine && HasActiveLot && Ui.ShowsLot && !string.Equals(interfaceMode, _simulatorMode, StringComparison.Ordinal)
            && Send("mode", new Dictionary<string, object?> { ["mode"] = interfaceMode }) is not null)
        {
            _simulatorMode = interfaceMode;
        }

        if (UsesNativeEngine && HasActiveLot && Ui.ShowsLot && Ui.TutorialIcon is not null && now - _lastTutorialPanelCheck >= 1000.0 / 30.0)
        {
            _lastTutorialPanelCheck = now;
            bool live = !Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && (Ui.TutorialIcon is null || _tutorialLiveModeClicked);
            var payload = new Dictionary<string, object?>
            {
                ["live"] = live,
                ["scrolls"] = Math.Max(0, unchecked(Renderer.PanAssignments - _pipPanAssignmentsBias)),
                ["setButtons"] = Ui.SetButtonImages(Renderer.Speed),
            };
            if (live && Ui.Gameplay.TutorialPopupName is { } popup)
            {
                payload["popup"] = popup;
            }

            _ = Send("tutorialPanelCheck", payload);
        }

        if (_desiredCutaway is { } desired && _rightOrigin is null && _edgeScrollRegionKey is null && !Renderer.IsZoomInterpolating && now - desired.Time >= 40.0)
        {
            _desiredCutaway = null;
            if (string.Equals(Ui.WallMode, "cutaway", StringComparison.Ordinal)
                && !HasBlockingDialog && Ui.PieMenu is null
                && !string.Equals(_lastCutawayTile, desired.Key, StringComparison.Ordinal)
                && Send("cutaway", new Dictionary<string, object?>
                {
                    ["x"] = desired.X,
                    ["y"] = desired.Y,
                    ["zoom"] = desired.Zoom,
                }) is not null)
            {
                _lastCutawayTile = desired.Key;
            }
        }

        double frame = Math.Min(now - _edgeScrollClock, 100.0);
        _edgeScrollClock = now;
        if (RightScrollVelocity is { } velocity)
        {
            _edgeScrollRegionKey = null;
            Vector2 step = velocity * (float)(frame / 1000.0);
            if (frame > 0.0 && step != Vector2.Zero)
            {
                Renderer.Pan += step;
            }

            SyncSimulatorViewScrolling(active: true);
            return;
        }

        if (!Ui.OptionsPanel.PlayOptions.EdgeScrolling || HasBlockingDialog || !Ui.ShowsLot
            || Ui.OutfitPicker is not null || Ui.PluginPanel is not null
            || Pointer is not { } ptr || EdgeScrollRegion(ptr) is not { } region)
        {
            _edgeScrollRegionKey = null;
            SyncSimulatorViewScrolling(active: false);
            return;
        }

        int key = region.X * 3 + region.Y;
        if (_edgeScrollRegionKey != key)
        {
            _edgeScrollRegionKey = key;
            _edgeScrollStart = now;
        }

        if (frame <= 0.0)
        {
            SyncSimulatorViewScrolling(active: true);
            return;
        }

        (double vx, double vy) = EdgeScrollVelocity(region, elapsed: now - _edgeScrollStart);
        double scaleTime = Scale * frame / 1000.0;
        var delta = new Vector2((float)(vx * scaleTime), (float)(vy * scaleTime));
        if (delta != Vector2.Zero)
        {
            Renderer.Pan += delta;
        }

        SyncSimulatorViewScrolling(active: true);
    }

    internal bool AllowsContinuousArrowPan =>
        Ui.ShowsLot
        && !HasBlockingDialog
        && !Ui.IsModalDialogBlocking
        && Ui.OutfitPicker is null
        && Ui.PluginPanel is null
        && !Ui.Album.Visible
        && Ui.GenericSimsCallPicker is null
        && !(Ui.Camera.Visible && DeleteModifier)
        && !Ui.SoundOptions.IsVisible;

    private void SyncSimulatorViewScrolling(bool active)
    {
        if (UsesNativeEngine)
        {
            _lastSimulatorPan = Renderer.Pan;
            return;
        }

        if (!HasActiveLot)
        {
            _simulatorViewScrolling = null;
            _lastSimulatorPan = Renderer.Pan;
            return;
        }

        if (!Ui.ShowsLot)
        {
            if (_simulatorViewScrolling != false
                && Send("viewScrolling", new Dictionary<string, object?> { ["value"] = false }) is not null)
            {
                _simulatorViewScrolling = false;
            }

            _lastSimulatorPan = Renderer.Pan;
            return;
        }

        bool panChanged = _lastSimulatorPan is { } prevPan && prevPan != Renderer.Pan;
        _lastSimulatorPan = Renderer.Pan;
        bool scrolling = active || panChanged;
        if (_simulatorViewScrolling == scrolling
            || Send("viewScrolling", new Dictionary<string, object?> { ["value"] = scrolling }) is null)
        {
            return;
        }

        _simulatorViewScrolling = scrolling;
    }

    private string CursorStem(CGPoint point)
    {
        if (!Ui.ShowsLot || Ui.OptionsMode || Ui.HelpVisible || Ui.Album.Visible || Ui.Camera.Visible
            || Ui.PieMenu is not null || Ui.GameDialog is not null || Ui.OutfitPicker is not null || Ui.PluginPanel is not null
            || (!IsLivePanelMinimized && Local(point).Y >= LogicalCanvas.Height - 150)
            || OverPanelControls(point))
        {
            return "Arrow";
        }

        if (Ui.BuildMode)
        {
            return Ui.Build.TerrainMode switch
            {
                TerrainBrushMode.Raise => "SimsRaise",
                TerrainBrushMode.Lower => "SimsLower",
                TerrainBrushMode.Level => "SimsLevel",
                TerrainBrushMode.Grass => "SimsPlace",
                null => "Arrow",
                _ => "Arrow",
            };
        }

        if (Ui.BuyMode)
        {
            if (Ui.Buy.Selection.Target is not null)
            {
                return "SimsPlace";
            }

            return Renderer.ObjectAt(Simd(point), SimdCanvas) is not null ? "SimsMove" : "Arrow";
        }

        if (Renderer.ObjectAt(Simd(point), SimdCanvas) is not { } target)
        {
            int selectedGender = _lotSims.TryGetValue(_selectedSim, out LotSimInfo? selectedSimInfo)
                ? selectedSimInfo.Gender
                : (Ui.Gameplay.State?.Selected?.Gender ?? 0);
            return selectedGender > 1 ? "LiveNothingPet" : "LiveNothing";
        }

        if (_lotSims.TryGetValue(target, out LotSimInfo? sim))
        {
            if (sim.HideHover)
            {
                return "LiveObjectUnavail";
            }

            return (sim.Gender < 2 || sim.Guid == 0xFC697613u) ? "LivePerson" : "LivePet";
        }

        return _simIDs.Contains(target) ? "LivePerson" : "LiveObjectAvail";
    }

    public void Leave()
    {
        Pointer = null;
        ClearBuildCursor();
        if (_floorBrush is not null)
        {
            _floorBrush = null;
            _floorBrushDeleting = false;
            _floorBrushTexture = null;
            Renderer.UpdateFloorBrush(null);
        }

        Ui.PictureInPictureCloseHovered = false;
        if (!_pipCloseTracking)
        {
            Ui.PictureInPictureClosePressed = false;
        }

        if (!_dialogButtonTracking)
        {
            Ui.GameDialog?.ClearHover();
        }

        if (!_chooserOKTracking)
        {
            Ui.GenericSimsCallPicker?.ClearHover();
        }

        Ui.HoveredUniversalControl = null;
        Ui.HoveredSpeedControl = null;
        Ui.Buy.ClearHover();
        Ui.Build.ClearHover();
        Ui.CatalogPopup.Hover(null);
        _catalogPopupWasVisible = false;
        Ui.OptionsPanel.ClearHover();
        Ui.HouseholdCreation?.ClearHover();
        Ui.Gameplay.IndicatorHovered = null;
        Ui.Gameplay.PanelTip = null;
        Ui.Gameplay.HoveredNeed = null;
        Ui.Gameplay.HoveredPeopleTip = null;
        Ui.Gameplay.ClearInventoryHover();
    }

    public void Pan(Vector2 delta)
    {
        if (HasBlockingDialog || Ui.Album.Visible || _switchingLot || _openingNeighborhoodLot
            || (_loadingNeighborhood && Ui.Neighborhood is null) || !Ui.ShowsLot)
        {
            return;
        }

        if (Ui.Camera.Visible && (DeleteModifier || ScriptDeleteModifier))
        {
            double dx = delta.X > 0 ? -4 : delta.X < 0 ? 4 : 0;
            double dy = delta.Y > 0 ? 4 : delta.Y < 0 ? -4 : 0;
            Ui.Camera.ResizeCustomFrame(dx, dy);
            return;
        }

        DismissTransientMenu();
        Renderer.Pan += delta;
    }

    public void RightDown(CGPoint point)
    {
        Ui.Gameplay.DismissTooltips();
        if (HasBlockingDialog || Ui.Album.Visible || _switchingLot || _openingNeighborhoodLot
            || (_loadingNeighborhood && Ui.Neighborhood is null) || !Ui.ShowsLot)
        {
            return;
        }

        DismissTransientMenu();
        if (!Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && !Ui.PanelMinimized
            && Ui.Gameplay.RelationshipAt(Local(point), LogicalCanvas) is { } relationship)
        {
            _rightOrigin = null;
            if (relationship.Position is { } position)
            {
                _relationshipCenter = position;
                _relationshipCenterChangesLevel = position.Level != Renderer.ViewLevel;
                CentreRelationshipIfPending();
            }

            return;
        }

        if (!Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode && !Ui.PanelMinimized
            && Ui.Gameplay.Selection(Local(point), LogicalCanvas) is { } simID)
        {
            _rightOrigin = null;
            if (_lotSims.TryGetValue(simID, out LotSimInfo? sim))
            {
                _relationshipCenter = new GameplayPanelState.Relationship.LivePosition(sim.X, sim.Y, sim.Level);
                _relationshipCenterChangesLevel = sim.Level != Renderer.ViewLevel;
                CentreRelationshipIfPending();
            }

            return;
        }

        if (OverPanelControls(point))
        {
            _rightOrigin = null;
            return;
        }

        _rightOrigin = point;
        _rightCurrent = point;
        _rightScrolling = false;
        _rightSelectedSim = false;
        // In Live Mode, right-clicking a household member selects them immediately, preventing the camera
        // from centering on release.
        if (!Ui.BuyMode && !Ui.BuildMode && !Ui.OptionsMode
            && Renderer.ObjectAt(Simd(point), SimdCanvas) is { } target
            && _simIDs.Contains(target))
        {
            if (target != _selectedSim)
            {
                _selectedSim = target;
                RequestAutoCenterOnSelection();
                _ = Send("selectSim", new Dictionary<string, object?> { ["objectID"] = target });
            }

            _rightSelectedSim = true;
        }
    }

    /// <summary>Begins anchored scrolling once the pointer moves more than 8 pixels from the press
    /// location, scaling velocity to five times the displacement from the anchor.</summary>
    public void RightMove(CGPoint point)
    {
        if (_rightOrigin is not { } origin)
        {
            return;
        }

        _rightCurrent = point;
        if (!_rightScrolling && (Math.Abs(point.X - origin.X) > 8.0 * Scale || Math.Abs(point.Y - origin.Y) > 8.0 * Scale))
        {
            _rightScrolling = true;
            _edgeScrollClock = CurrentUptime() * 1000.0;
        }
    }

    /// <summary>Centers the camera on the clicked tile if the right mouse button is released without
    /// engaging anchored scrolling.</summary>
    public void RightUp(CGPoint point)
    {
        try
        {
            if (_rightOrigin is null || _rightScrolling || _rightSelectedSim || HasBlockingDialog || !Ui.ShowsLot
                || OverPanelControls(point)
                || Renderer.GroundTileAt(Simd(point), SimdCanvas) is not { } tile)
            {
                return;
            }

            _ = Renderer.CentreGroundTile(tile.X, tile.Y, SimdCanvas);
        }
        finally
        {
            _rightOrigin = null;
            _rightCurrent = null;
            _rightScrolling = false;
            _rightSelectedSim = false;
        }
    }
}
