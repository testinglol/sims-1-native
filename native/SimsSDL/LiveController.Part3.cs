namespace SimsSDL;

internal sealed partial class LiveController
{
    private void CentreRelationshipIfPending()
    {
        if (_relationshipCenter is not { } position)
        {
            return;
        }

        if (position.Level != EffectiveViewLevel
            && position.Level >= 1
            && position.Level <= Renderer.StoryCount)
        {
            _pendingViewLevel = position.Level;
            _pendingRoofsVisible = false;
            _pendingViewSceneRevision = null;
            _pendingViewRequestID = Send(
                "level",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = position.Level,
                    ["roofs"] = false,
                });
            return;
        }

        if (position.Level != Renderer.ViewLevel)
        {
            return;
        }

        if (_relationshipCenterChangesLevel)
        {
            if (Renderer.CentreGroundTile((int)position.X, (int)position.Y, SimdCanvas) is null)
            {
                return;
            }

            _relationshipCenter = null;
            if (EffectiveRoofsVisible)
            {
                _pendingRoofsVisible = false;
                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send(
                    "roofs",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["visible"] = false });
            }

            return;
        }

        if (Renderer.SceneViewport(SimdCanvas) is not { } viewport
            || Renderer.ScreenPoint(position.X, position.Y, SimdCanvas) is not { } point)
        {
            return;
        }

        _relationshipCenter = null;
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

    public bool RightScrollActive => _rightScrolling;
    public bool RightButtonHeld => _rightOrigin is not null;

    /// <summary>The anchored scroll's velocity in pixels per second, 5 * (press - pointer).</summary>
    private Vector2? RightScrollVelocity =>
        _rightScrolling && _rightOrigin is { } origin && _rightCurrent is { } current
            ? new Vector2((float)(origin.X - current.X), (float)(origin.Y - current.Y)) * 5f
            : (Vector2?)null;

    /// <summary>
    /// <c>EdgeDetectScroller::Classify</c>: the arrow for the scroll direction, the anchor cursor under a speed of 100, and 15 degrees
    /// either side of an axis counting as that axis.
    /// </summary>
    public string RightScrollCursor
    {
        get
        {
            if (RightScrollVelocity is not { } velocity)
            {
                return "Pan";
            }

            float x = velocity.X;
            float y = velocity.Y;
            float limit = (float)Math.Tan(15.0 * Math.PI / 180.0);
            if (MathF.Sqrt(x * x + y * y) < 100f)
            {
                return "Pan";
            }

            if (MathF.Abs(y / x) < limit || (x == 0f && y == 0f))
            {
                return x < 0f ? "RIGHT" : "LEFT";
            }

            if (MathF.Abs(x / y) < limit)
            {
                return y < 0f ? "DOWN" : "UP";
            }

            return x < 0f
                ? (y < 0f ? "DOWNRIGHT" : "UPRIGHT")
                : (y < 0f ? "DOWNLEFT" : "UPLEFT");
        }
    }

    /// <summary>A mouse wheel turn: zooms a whole level per notch, or scrolls an open dialog.</summary>
    public void Wheel(float notches)
    {
        if (Ui.HelpVisible)
        {
            Ui.HelpOverlay.Scroll(notches > 0f ? -3 : notches < 0f ? 3 : 0);
            return;
        }

        if (!HasBlockingDialog && Ui.FamilyPicker is { } familyPicker)
        {
            familyPicker.Scroll(notches > 0f ? -1 : notches < 0f ? 1 : 0);
            return;
        }

        if (!HasBlockingDialog && Ui.GenericSimsCallPicker is { } callPicker)
        {
            callPicker.Scroll(notches > 0f ? -1 : notches < 0f ? 1 : 0, LogicalCanvas);
            return;
        }

        if (Ui.GameDialog is { } dialog
            && (dialog.Dialog.Blocking || dialog.Contains(Pointer ?? CGPoint.Zero, OverlayScale)))
        {
            if (Ui.ShowsLot)
            {
                DismissTransientMenu();
            }

            if (dialog.Dialog.Kind == 10)
            {
                dialog.ScrollPhoneBook(-notches * 20f, Pointer ?? CGPoint.Zero, OverlayScale);
            }
            else
            {
                dialog.ScrollMessage(-notches * 20f);
            }

            return;
        }

        if (!Ui.ShowsLot)
        {
            return;
        }

        DismissTransientMenu();
        int steps = notches > 0f ? 1 : notches < 0f ? -1 : 0;
        Renderer.SetZoom(Renderer.Zoom.Stepped(steps));
    }

    private void UpdateCutaway(CGPoint point)
    {
        if (!string.Equals(Ui.WallMode, "cutaway", StringComparison.Ordinal))
        {
            _lastCutawayTile = null;
            _desiredCutaway = null;
            return;
        }

        if (HasBlockingDialog || Ui.PieMenu is not null)
        {
            ClearCutawayTracking();
            return;
        }

        // Skip wall-cutaway recalculation while actively dragging or edge-scrolling the camera.
        if (_rightOrigin is not null
            || (Ui.OptionsPanel.PlayOptions.EdgeScrolling && Ui.ShowsLot && EdgeScrollRegion(point) is not null))
        {
            return;
        }

        if (Renderer.GroundTileAt(Simd(point), SimdCanvas) is not { } tile)
        {
            ClearCutawayTracking();
            return;
        }

        string key = $"{Renderer.ViewLevel}:{(int)Renderer.Zoom}:{tile.X}:{tile.Y}";
        if (string.Equals(_lastCutawayTile, key, StringComparison.Ordinal))
        {
            _desiredCutaway = null;
            return;
        }

        if (_lastCutawayTile is null)
        {
            _desiredCutaway = null;
            if (Send(
                "cutaway",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = tile.X,
                    ["y"] = tile.Y,
                    ["zoom"] = (int)Renderer.Zoom,
                }) is not null)
            {
                _lastCutawayTile = key;
            }
        }
        else
        {
            _desiredCutaway = (key, tile.X, tile.Y, (int)Renderer.Zoom, CurrentUptime() * 1000.0);
        }
    }

    private void ClearCutawayTracking()
    {
        _desiredCutaway = null;
        if (_lastCutawayTile is not null)
        {
            _ = Send(
                "cutaway",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = -1,
                    ["y"] = -1,
                });
        }

        _lastCutawayTile = null;
    }

    // Keys

    public abstract record Key
    {
        public sealed record Escape : Key;
        public sealed record Enter : Key;
        public sealed record Backspace : Key;
        public sealed record Left : Key;
        public sealed record Right : Key;
        public sealed record Up : Key;
        public sealed record Down : Key;
        public sealed record PageUp : Key;
        public sealed record PageDown : Key;
        public sealed record Home : Key;
        public sealed record End : Key;
        public sealed record Delete : Key;
        public sealed record Tab : Key;
        public sealed record Function(int Value) : Key;
        public sealed record Character(string Value) : Key;
    }

    /// <summary>A key press; returns false for keys the live game does not use (the window handles those).</summary>
    public bool HandleKeyDown(Key key)
    {
        if (Ui.BudgetReportVisible)
        {
            if (key is Key.Escape or Key.Enter)
            {
                PlayUISound("ui_click");
                Ui.BudgetReportVisible = false;
                Ui.BudgetReport.ClearHover();
            }

            return true;
        }

        if (Ui.HelpVisible)
        {
            switch (key)
            {
                case Key.Escape or Key.Enter:
                    SetHelpVisible(false);
                    break;
                case Key.Up:
                    Ui.HelpOverlay.MoveSelection(-1);
                    break;
                case Key.Down:
                    Ui.HelpOverlay.MoveSelection(1);
                    break;
                case Key.PageUp:
                    Ui.HelpOverlay.MovePage(-1);
                    break;
                case Key.PageDown:
                    Ui.HelpOverlay.MovePage(1);
                    break;
                case Key.Home:
                    Ui.HelpOverlay.SelectFirst();
                    break;
                case Key.End:
                    Ui.HelpOverlay.SelectLast();
                    break;
            }

            return true;
        }

        if (DialogKey(key))
        {
            return true;
        }

        if (Ui.Album.Visible)
        {
            if (Ui.Album.EditingCaption)
            {
                switch (key)
                {
                    case Key.Escape or Key.Enter:
                        _ = Ui.Album.ClickCaption(CGPoint.Zero, LogicalCanvas);
                        break;
                    case Key.Backspace:
                        try
                        {
                            Ui.Album.UpdateCaption(DropLastCharacter(Ui.Album.CurrentCaption));
                        }
                        catch
                        {
                            // Ignore caption edit errors.
                        }
                        break;
                }

                return true;
            }

            switch (key)
            {
                case Key.Escape or Key.Enter:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.Done);
                    break;
                case Key.Left:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.Previous);
                    break;
                case Key.Right:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.Next);
                    break;
                case Key.Home:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.First);
                    break;
                case Key.End:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.Last);
                    break;
                case Key.Delete or Key.Backspace:
                    AlbumAction(OriginalPhotoAlbumOverlay.Action.Delete);
                    break;
            }

            return true;
        }

        if (Ui.Camera.Visible)
        {
            if (key is Key.Escape)
            {
                LeaveCameraMode();
                return true;
            }

            if (DeleteModifier || ScriptDeleteModifier)
            {
                switch (key)
                {
                    case Key.Left:
                        Ui.Camera.ResizeCustomFrame(-1, 0);
                        return true;
                    case Key.Right:
                        Ui.Camera.ResizeCustomFrame(1, 0);
                        return true;
                    case Key.Up:
                        Ui.Camera.ResizeCustomFrame(0, -1);
                        return true;
                    case Key.Down:
                        Ui.Camera.ResizeCustomFrame(0, 1);
                        return true;
                }
            }
        }

        if (Ui.GenericSimsCallPicker is { } picker)
        {
            switch (key)
            {
                case Key.Escape:
                    break;
                case Key.Up:
                    picker.MoveSelection(-1);
                    break;
                case Key.Down:
                    picker.MoveSelection(1);
                    break;
                case Key.Enter:
                    if (picker.SelectedPersonRecordIndex is { } row)
                    {
                        ChooseGenericSimsCallPerson(row);
                    }
                    break;
            }

            return true;
        }

        if (ScreenKey(key) is { } screen)
        {
            return screen;
        }

        if (Ui.PluginPanel is not null && !Ui.SoundOptions.IsVisible)
        {
            switch (key)
            {
                case Key.Escape or Key.Enter:
                    Ui.PluginPanel = null;
                    PlayUISound("ui_click");
                    break;
                case Key.Character { Value: "e" }:
                    OpenNextPluginPanel();
                    break;
            }

            return true;
        }

        switch (key)
        {
            case Key.Escape:
            {
                bool hasMenuOrSelection = Ui.PieMenu is not null
                    || Ui.Buy.Selection.Target is not null
                    || Ui.Build.Selected is not null
                    || Ui.Build.SelectedObjectGUID is not null;
                if (!hasMenuOrSelection && _pictureInPicture is not null)
                {
                    // cWinPictureInPicture::OnKeyDown (0x004c3b20): Escape closes PiP without panning.
                    ClosePictureInPicture();
                }
                else
                {
                    DismissMenu();
                }
                break;
            }

            case Key.Enter:
                if (HasBlockingDialog || !Ui.ShowsLot || Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode || Ui.Camera.Visible || _selectedSim == 0)
                {
                    break;
                }

                _centresOnSelectedSim = true;
                CentreOnSelectedSimIfPending();
                break;

            case Key.Left when (SDL_GetModState() & SDL_KMOD_SHIFT) != 0:
                if (HasBlockingDialog || Ui.Album.Visible || !Ui.ShowsLot)
                {
                    return false;
                }

                RotateCamera(-1);
                break;

            case Key.Right when (SDL_GetModState() & SDL_KMOD_SHIFT) != 0:
                if (HasBlockingDialog || Ui.Album.Visible || !Ui.ShowsLot)
                {
                    return false;
                }

                RotateCamera(1);
                break;

            case Key.Character { Value: "`" or "~" or "0" }:
                SendSpeed(0);
                break;

            case Key.Character { Value: "p" }:
            {
                bool paused = IsInLiveMode ? Renderer.Speed == 0 : _liveModePaused;
                SendSpeed(paused ? _lastRunningSpeed : 0);
                break;
            }

            // cWinPeople's key switch (WinPeople.s 0x10283FB8) sends Space to ObjectModule::AdvanceSelectedPerson.
            case Key.Character { Value: " " }:
                NextSim();
                break;

            case Key.Character { Value: "1" }:
                SendSpeed(1);
                break;

            case Key.Character { Value: "2" }:
                SendSpeed(3);
                break;

            case Key.Character { Value: "3" }:
                SendSpeed(10);
                break;

            case Key.Character { Value: "z" or "x" } ch:
            {
                if (HasBlockingDialog)
                {
                    break;
                }

                bool next = string.Equals(ch.Value, "x", StringComparison.Ordinal);
                if (Ui.BuyMode && Ui.Buy.Page is { } buyPage)
                {
                    int capacity = OriginalBuyLayout.Capacity(LogicalCanvas);
                    int? offset = next
                        ? buyPage.NextOffset(capacity)
                        : (buyPage.Offset > 0 ? Math.Max(0, buyPage.Offset - capacity) : (int?)null);
                    if (offset is { } nextBuyOffset)
                    {
                        PlayUISound("ui_click");
                        RequestCatalog(
                            category: Ui.Buy.Category,
                            room: Ui.Buy.RoomMode ? Ui.Buy.Room : (int?)null,
                            subsort: Ui.Buy.FunctionSubSorting ? Ui.Buy.FunctionSubsort : (int?)null,
                            offset: nextBuyOffset);
                    }
                }
                else if (Ui.BuildMode && Ui.Build.TerrainMode is null)
                {
                    int capacity = Ui.Build.VisibleCapacity(LogicalCanvas);
                    int? offset;
                    if (Ui.Build.ObjectPage is { } objectPage)
                    {
                        offset = next
                            ? objectPage.NextOffset(capacity)
                            : (objectPage.Offset > 0 ? Math.Max(0, objectPage.Offset - capacity) : (int?)null);
                    }
                    else if (Ui.Build.Page is { } buildPage)
                    {
                        offset = next
                            ? buildPage.NextOffset(capacity)
                            : (buildPage.Offset > 0 ? Math.Max(0, buildPage.Offset - capacity) : (int?)null);
                    }
                    else
                    {
                        offset = null;
                    }

                    if (offset is { } nextBuildOffset)
                    {
                        PlayUISound("ui_click");
                        RequestBuildCatalog(offset: nextBuildOffset);
                    }
                }
                break;
            }

            case Key.Tab:
                // cLiveModePanel::Message (0051b86b.c lines 519-531) + cSimsPieMenu::Track (0048d6b6.c lines 323-326):
                // While a pie menu is open, Tab advances its 12-item page instead of switching the selected Sim.
                if (Ui.PieMenu is { } menu)
                {
                    bool advanced;
                    try
                    {
                        advanced = menu.NextPage();
                    }
                    catch
                    {
                        advanced = false;
                    }

                    if (advanced)
                    {
                        menu.Prepare(_canvas, Scale);
                        if (Pointer is { } pointer)
                        {
                            menu.Hover(pointer, Scale);
                        }

                        if (menu.Highlighted is { } highlighted && highlighted != _lastPieHighlight)
                        {
                            PlayUISound("ui_piemenu_highlight");
                        }

                        _lastPieHighlight = menu.Highlighted;
                        RequestPieHeadPreview();
                    }
                }
                else
                {
                    NextSim();
                }
                break;

            case Key.Character { Value: "w" }:
                ToggleWalls();
                break;

            case Key.PageUp:
                SelectStory(EffectiveViewLevel + 1);
                break;

            case Key.PageDown:
                SelectStory(EffectiveViewLevel - 1);
                break;

            case Key.Character { Value: "b" }:
                BrowseBuyCatalog();
                break;

            case Key.Character { Value: "f" }:
                BrowseBuildCatalog();
                break;

            case Key.Character { Value: "r" }:
                if (!Ui.ObjectEditing || HasBlockingDialog)
                {
                    break;
                }

                Ui.Buy.Rotate();
                PlayUISound("ui_object_rotate");
                if (Pointer is { } rotatePointer)
                {
                    UpdatePlacement(rotatePointer);
                }
                break;

            // With an object in hand, ,/< and ./> turn it (00486cd1.c lines 12-24); otherwise ,/< and ./> turn the camera.
            case Key.Character { Value: "," or "<" or "." or ">" } mark:
            {
                int steps = mark.Value is "," or "<" ? -1 : 1;
                if (!Ui.ObjectEditing || Ui.Buy.Selection.Target is null || HasBlockingDialog)
                {
                    RotateCamera(steps);
                    break;
                }

                Ui.Buy.Rotate(steps);
                PlayUISound("ui_object_rotate");
                if (Pointer is { } stepRotatePointer)
                {
                    UpdatePlacement(stepRotatePointer);
                }
                break;
            }

            case Key.Character { Value: "[" }:
                RotateCamera(-1);
                break;

            case Key.Character { Value: "]" }:
                RotateCamera(1);
                break;

            case Key.Character { Value: "=" or "+" }:
                UniversalControl(OriginalUniversalControl.Action.ZoomIn);
                break;

            case Key.Character { Value: "-" }:
                UniversalControl(OriginalUniversalControl.Action.ZoomOut);
                break;

            case Key.Home:
                StepWallView(1);
                break;

            case Key.End:
                StepWallView(-1);
                break;

            case Key.Function { Value: 1 }:
                UniversalControl(OriginalUniversalControl.Action.Live);
                break;

            case Key.Function { Value: 2 }:
                UniversalControl(OriginalUniversalControl.Action.Buy);
                break;

            case Key.Function { Value: 3 }:
                UniversalControl(OriginalUniversalControl.Action.Build);
                break;

            case Key.Function { Value: 4 }:
                EnterCameraMode();
                break;

            case Key.Function { Value: 5 }:
                UniversalControl(OriginalUniversalControl.Action.Options);
                break;

            case Key.Character { Value: "s" }:
                SaveGame();
                break;

            case Key.Character { Value: "l" }:
                LoadGame();
                break;

            case Key.Character { Value: "e" }:
                OpenNextPluginPanel();
                break;

            case Key.Delete or Key.Backspace:
                SellHeldObject();
                break;

            default:
                return false;
        }

        return true;
    }

    private static GameDialog.Button? FirstButtonWithCode(IReadOnlyList<GameDialog.Button> buttons, byte code)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Code == code)
            {
                return buttons[i];
            }
        }

        return null;
    }

    private static string DropLastCharacter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= 1 ? string.Empty : text[..starts[^1]];
    }

    private bool DialogKey(Key key)
    {
        if (Ui.GameDialog is not { } dialog || dialog.Submitting)
        {
            return false;
        }

        // Mac ObjectDialog::SetupDialog gives a buttonless dialog its close box (button 2) and binds Enter, Esc and
        // Space to it; non-blocking tutorial banners let all other keys reach the live game.
        if (dialog.TutorialCloseBox is not null && dialog.Dialog.Buttons.Count == 0)
        {
            switch (key)
            {
                case Key.Enter or Key.Escape or Key.Character { Value: " " }:
                    RespondDialog(dialog, code: 0);
                    return true;
                default:
                    return dialog.Dialog.Blocking;
            }
        }

        if (string.Equals(_localDialogToken, dialog.Dialog.Token, StringComparison.Ordinal))
        {
            if (dialog.Dialog.IsBookScreen)
            {
                switch (key)
                {
                    case Key.Enter:
                        RespondDialog(dialog, code: 0);
                        break;
                    case Key.Escape:
                        RespondDialog(dialog, code: 0);
                        break;
                    case Key.Up:
                        dialog.MoveBookSelection(-1);
                        break;
                    case Key.Down:
                        dialog.MoveBookSelection(1);
                        break;
                    case Key.Left:
                        dialog.MoveBookPage(-1);
                        break;
                    case Key.Right:
                        dialog.MoveBookPage(1);
                        break;
                }
            }
            else if (key is Key.Enter && FirstButtonWithCode(dialog.Dialog.Buttons, 0) is { } firstZero)
            {
                RespondDialog(dialog, code: firstZero.Code);
            }
            else if (key is Key.Character { Value: " " }
                     && dialog.Dialog.Buttons.Count == 1
                     && dialog.Dialog.Kind != 3
                     && !dialog.Dialog.IsBookScreen)
            {
                RespondDialog(dialog, code: dialog.Dialog.Buttons[0].Code);
            }
            else if (key is Key.Escape
                     && (FirstButtonWithCode(dialog.Dialog.Buttons, 2)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 1)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 0)) is { } dismiss)
            {
                RespondDialog(dialog, code: dismiss.Code);
            }

            return true;
        }

        if (!dialog.Dialog.Blocking)
        {
            switch (key)
            {
                case Key.Enter:
                    if (dialog.Dialog.Buttons.Count > 0)
                    {
                        RespondDialog(dialog, code: dialog.Dialog.Buttons[0].Code);
                    }
                    return true;

                case Key.Character { Value: " " }
                    when dialog.Dialog.Buttons.Count == 1 && dialog.Dialog.Kind != 3 && !dialog.Dialog.IsBookScreen:
                    RespondDialog(dialog, code: dialog.Dialog.Buttons[0].Code);
                    return true;

                case Key.Escape:
                    if ((FirstButtonWithCode(dialog.Dialog.Buttons, 2)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 1)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 0)) is { } dismiss)
                    {
                        RespondDialog(dialog, code: dismiss.Code);
                    }
                    return true;

                case Key.Backspace when dialog.AcceptsTextInput:
                    try
                    {
                        dialog.UpdateText(DropLastCharacter(dialog.Text));
                    }
                    catch (Exception error)
                    {
                        Log($"Game dialog: {error}");
                    }
                    return true;

                default:
                    return false;
            }
        }

        try
        {
            if (dialog.Dialog.Kind == 10)
            {
                switch (key)
                {
                    case Key.Enter:
                        if (dialog.SelectedPhoneBookContact() is { } neighbor)
                        {
                            RespondDialog(dialog, code: 1, responseText: neighbor.ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    case Key.Escape:
                        RespondDialog(dialog, code: 0, responseText: "-1");
                        break;
                    case Key.Up:
                        dialog.MovePhoneBookSelection(-1);
                        break;
                    case Key.Down:
                        dialog.MovePhoneBookSelection(1);
                        break;
                    case Key.Left:
                        dialog.MovePhoneBookFamily(-1);
                        break;
                    case Key.Right:
                        dialog.MovePhoneBookFamily(1);
                        break;
                }

                return true;
            }

            switch (key)
            {
                case Key.Enter:
                    if (dialog.Dialog.Buttons.Count > 0)
                    {
                        RespondDialog(dialog, code: dialog.Dialog.Buttons[0].Code);
                    }
                    break;

                case Key.Character { Value: " " }
                    when dialog.Dialog.Buttons.Count == 1 && dialog.Dialog.Kind != 3 && !dialog.Dialog.IsBookScreen:
                    RespondDialog(dialog, code: dialog.Dialog.Buttons[0].Code);
                    break;

                case Key.Escape when dialog.Dialog.IsBookScreen:
                    RespondDialog(dialog, code: 0);
                    break;
                case Key.Up when dialog.Dialog.IsBookScreen:
                    dialog.MoveBookSelection(-1);
                    break;
                case Key.Down when dialog.Dialog.IsBookScreen:
                    dialog.MoveBookSelection(1);
                    break;
                case Key.Left when dialog.Dialog.IsBookScreen:
                    dialog.MoveBookPage(-1);
                    break;
                case Key.Right when dialog.Dialog.IsBookScreen:
                    dialog.MoveBookPage(1);
                    break;

                case Key.Escape:
                    if ((FirstButtonWithCode(dialog.Dialog.Buttons, 2)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 1)
                         ?? FirstButtonWithCode(dialog.Dialog.Buttons, 0)) is { } dismiss)
                    {
                        RespondDialog(dialog, code: dismiss.Code);
                    }
                    break;

                case Key.Backspace when dialog.Dialog.Kind == 3 && !dialog.Submitting:
                    dialog.UpdateText(DropLastCharacter(dialog.Text));
                    break;
            }
        }
        catch (Exception error)
        {
            Log($"Game dialog: {error}");
        }

        return true;
    }

    /// <summary>Typed text, for the name-entry dialog.</summary>
    public void Text(string text)
    {
        if (Ui.Album.Visible && Ui.Album.EditingCaption)
        {
            try
            {
                Ui.Album.UpdateCaption(Ui.Album.CurrentCaption + text);
            }
            catch
            {
                // Ignore caption edit errors.
            }

            return;
        }

        if (Ui.GameDialog is null && Ui.HouseholdCreation is { } creation)
        {
            var weakSelf = new WeakReference<LiveController>(this);
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                string character = (string)enumerator.Current;
                _ = creation.Key(character, () =>
                {
                    if (weakSelf.TryGetTarget(out var self))
                    {
                        self.ConfirmCancelHouseholdCreation();
                    }
                });
            }

            return;
        }

        if (Ui.GameDialog is not { } dialog
            || !dialog.AcceptsTextInput
            || string.Equals(_localDialogToken, dialog.Dialog.Token, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            dialog.UpdateText(dialog.Text + text);
        }
        catch (Exception error)
        {
            Log($"Game dialog: {error}");
        }
    }

    public bool AcceptsText =>
        (Ui.Album.Visible && Ui.Album.EditingCaption)
        || Ui.HouseholdCreation is not null
        || (Ui.GameDialog?.AcceptsTextInput == true
            && !string.Equals(_localDialogToken, Ui.GameDialog?.Dialog.Token, StringComparison.Ordinal));

    // Pack plugins

    /// <summary>
    /// Asks once per launch, in a system message box outside the original-game UI, before any untrusted plugin may run.
    /// "Keep Disabled" is the default; trust applies only to the exact files that were reviewed.
    /// </summary>
    private void ReviewPluginTrust()
    {
        string dataRoot = Options.DataRoot ?? Workspace;
        foreach (var (plugin, name) in PackTool.UntrustedPlugins(Content, dataRoot))
        {
            string title = $"Allow “{name}” to run code?";
            string message = $"The pack {plugin.PackID} {plugin.Version} includes a gameplay plugin that runs with your user account's permissions. "
                + $"Only allow packs from authors you trust.\n\nFiles digest: {plugin.Sha256}";
            bool allowed = ShowPluginTrustMessageBox(title, message);
            if (!allowed)
            {
                Log($"plugin {plugin.PackID} left disabled");
                continue;
            }

            try
            {
                PackTool.Trust(plugin, dataRoot);
            }
            catch (Exception error)
            {
                Log($"Plugin trust: {error}");
            }
        }
    }

    private static unsafe bool ShowPluginTrustMessageBox(string title, string message)
    {
        nint titlePtr = Marshal.StringToCoTaskMemUTF8(title);
        nint messagePtr = Marshal.StringToCoTaskMemUTF8(message);
        nint keepPtr = Marshal.StringToCoTaskMemUTF8("Keep Disabled");
        nint allowPtr = Marshal.StringToCoTaskMemUTF8("Allow");
        try
        {
            var buttons = new SDL_MessageBoxButtonData[2];
            buttons[0] = new SDL_MessageBoxButtonData
            {
                flags = SDL_MESSAGEBOX_BUTTON_RETURNKEY_DEFAULT | SDL_MESSAGEBOX_BUTTON_ESCAPEKEY_DEFAULT,
                buttonID = 0,
                text = keepPtr,
            };
            buttons[1] = new SDL_MessageBoxButtonData
            {
                flags = 0,
                buttonID = 1,
                text = allowPtr,
            };

            fixed (SDL_MessageBoxButtonData* buttonsPtr = buttons)
            {
                var data = new SDL_MessageBoxData
                {
                    flags = SDL_MESSAGEBOX_WARNING,
                    window = nint.Zero,
                    title = titlePtr,
                    message = messagePtr,
                    numbuttons = buttons.Length,
                    buttons = (nint)buttonsPtr,
                    colorScheme = nint.Zero,
                };
                return SDL_ShowMessageBox(in data, out int choice) && choice == 1;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(titlePtr);
            Marshal.FreeCoTaskMem(messagePtr);
            Marshal.FreeCoTaskMem(keepPtr);
            Marshal.FreeCoTaskMem(allowPtr);
        }
    }

    /// <summary>Every pack-declared panel, in pack then panel ID order; the macOS client lists them in its Simulation menu.</summary>
    private List<(string PackID, PanelDescription Description)> PluginPanels =>
        Content.Packs.PanelDescriptions()
            .OrderBy(static pack => pack.Key, StringComparer.Ordinal)
            .SelectMany(static pack => pack.Value
                .OrderBy(static panel => panel.Key, StringComparer.Ordinal)
                .Select(panel => (pack.Key, panel.Value)))
            .ToList();

    /// <summary>Opens the panel after the open one (the first when none is open); past the last panel it closes.</summary>
    public void OpenNextPluginPanel()
    {
        if (!HasActiveLot || !Ui.ShowsLot || HasBlockingDialog || Ui.OutfitPicker is not null)
        {
            return;
        }

        List<(string PackID, PanelDescription Description)> panels = PluginPanels;
        if (panels.Count == 0)
        {
            Log("no pack declares a plugin panel");
            return;
        }

        int? current = Ui.PluginPanel is { } open
            ? FindPanelIndex(panels, open.PackId, open.Description.Id)
            : null;
        int next = current is { } currentIndex ? currentIndex + 1 : 0;
        DismissMenu();
        Ui.SoundOptions.Dismiss();
        PlayUISound("ui_click");
        if (next >= panels.Count)
        {
            Ui.PluginPanel = null;
            return;
        }

        OpenPluginPanel(panels[next].PackID, panels[next].Description);
    }

    private static int? FindPanelIndex(
        List<(string PackID, PanelDescription Description)> panels,
        string packID,
        string descriptionID)
    {
        for (int i = 0; i < panels.Count; i++)
        {
            if (string.Equals(panels[i].PackID, packID, StringComparison.Ordinal)
                && string.Equals(panels[i].Description.Id, descriptionID, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Opens one declared panel, from the E key or when a plugin answers a menu choice with it.</summary>
    private void OpenPluginPanel(string packID, PanelDescription description, int? anchorID = null)
    {
        try
        {
            var panel = new OriginalPluginPanelOverlay(
                packId: packID,
                description: description,
                content: Content,
                font: Ui.Font,
                device: Ui.Device,
                closeLabel: Strings.OkButton(),
                anchorId: anchorID);
            Dictionary<string, string>? values =
                _pluginPanelValues.TryGetValue(packID, out var packPanels)
                && packPanels.TryGetValue(description.Id, out var panelValues)
                    ? panelValues
                    : null;
            panel.Update(values ?? new Dictionary<string, string>(StringComparer.Ordinal));
            Ui.PluginPanel = panel;
            FollowPanelAnchor();
            _ = Send("pluginPanels");
        }
        catch (Exception error)
        {
            Log($"Plugin panel: {error}");
        }
    }

    /// <summary>
    /// Keeps an anchored plugin panel beside its object as the view pans, zooms and the object moves; closes it once the
    /// object is gone from the lot.
    /// </summary>
    private void FollowPanelAnchor()
    {
        if (Ui.PluginPanel is not { } panel || panel.AnchorId is not int id)
        {
            return;
        }

        if (!Renderer.ContainsObject(id))
        {
            Ui.PluginPanel = null;
            return;
        }

        panel.LotHeight = LogicalCanvas.Height - 150.0;
        if (Renderer.ScreenBounds(id, SimdCanvas) is not { } bounds)
        {
            return;
        }

        panel.Anchor = new CGRect(
            bounds.MinX / Scale,
            bounds.MinY / Scale,
            bounds.Width / Scale,
            bounds.Height / Scale);
    }

    private void ApplyPluginPanelValues(List<Dictionary<string, JsonElement>> panels)
    {
        var updated = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var panel in panels)
        {
            if (panel.JsonString("packId") is not { } packID
                || panel.JsonString("panelId") is not { } panelID
                || panel.JsonDict("values") is not { } rawValues)
            {
                continue;
            }

            var values = new Dictionary<string, string>(rawValues.Count, StringComparer.Ordinal);
            bool allStrings = true;
            foreach (var (key, element) in rawValues)
            {
                if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } stringValue)
                {
                    allStrings = false;
                    break;
                }

                values[key] = stringValue;
            }

            if (!allStrings)
            {
                continue;
            }

            if (!updated.TryGetValue(packID, out var packPanels))
            {
                packPanels = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                updated[packID] = packPanels;
            }

            packPanels[panelID] = values;
        }

        _pluginPanelValues = updated;
        if (Ui.PluginPanel is { } openPanel)
        {
            Dictionary<string, string>? values =
                updated.TryGetValue(openPanel.PackId, out var packPanels)
                && packPanels.TryGetValue(openPanel.Description.Id, out var panelValues)
                    ? panelValues
                    : null;
            openPanel.Update(values ?? new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    // Buy and build editing

    private bool CanEditBuildObject(EditableLotObject obj)
    {
        if (!Ui.BuildMode)
        {
            return true;
        }

        if (Ui.Build.ObjectTool is { } tool)
        {
            return tool == OriginalBuildObjectTool.Hand || obj.CatalogCategory == tool.Category();
        }

        if (Ui.Build.TerrainMode is not null)
        {
            return false;
        }

        return Ui.Build.Kind switch
        {
            BuildCatalogPage.CatalogKind.WallStyle => obj.CatalogCategory == 13,
            BuildCatalogPage.CatalogKind.Water => obj.CatalogCategory == 14,
            _ => false,
        };
    }

    private void EditObject(CGPoint point)
    {
        if (!Ui.ObjectEditing || Renderer.Speed != 0 || HasBlockingDialog)
        {
            return;
        }

        if (Ui.Buy.Selection.Target is { } target
            && Renderer.GroundTileAt(Simd(point), SimdCanvas) is { } tile)
        {
            if (!target.IsPlaced)
            {
                if (Ui.SelectedCatalogPrice is { } price && price <= (Ui.Gameplay.State?.Budget ?? 0))
                {
                    string? request = Send(
                        "buyObject",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["guid"] = target.CatalogGuid,
                            ["x"] = tile.PlacementX,
                            ["y"] = tile.PlacementY,
                            ["level"] = Renderer.ViewLevel,
                            ["direction"] = Ui.Buy.Direction,
                        });
                    PlayUISoundOnResponse(request, "ui_buy_place");
                }
                else
                {
                    try
                    {
                        Ui.CatalogPopup.ShowPlacementError(
                            Strings.InsufficientFunds(),
                            new CGPoint(point.X / Scale, point.Y / Scale));
                    }
                    catch
                    {
                        // Ignore placement error tooltip failures.
                    }

                    PlayUISound("ui_error");
                }
            }
            else if (target.PlacedObject is { } obj && obj.Movable)
            {
                string? request = Send(
                    "moveObject",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["objectID"] = obj.Id,
                        ["x"] = tile.PlacementX,
                        ["y"] = tile.PlacementY,
                        ["level"] = Renderer.ViewLevel,
                        ["direction"] = Ui.Buy.Direction,
                    });
                PlayUISoundOnResponse(request, "ui_object_move_place");
            }
        }
        else if (Ui.Buy.Selection.Target is null
                 && Renderer.ObjectAt(Simd(point), SimdCanvas) is { } id
                 && _editableObjects.FirstOrDefault(candidate =>
                     candidate.Id == id
                     && (candidate.Level == Renderer.ViewLevel
                         || ((candidate.MultiTileMasterID ?? 0) != 0
                             && (candidate.CatalogCategory == 10 || candidate.CatalogCategory == 12)))
                     && CanEditBuildObject(candidate)) is { } obj)
        {
            Ui.Buy.Selection.ChooseObject(obj);
            PlayUISound("ui_object_move");
            SyncDisposePanel(markSaveDirtyOnPickup: obj.Movable);
        }
    }

    private void SyncDisposePanel(bool? markSaveDirtyOnPickup = null)
    {
        if (Ui.Buy.Selection.Target is not { IsPlaced: true, PlacedObject: { } obj })
        {
            Ui.DisposePanel = null;
            _pendingDisposePreviewRequest = null;
            return;
        }

        bool hasMeta = _placedObjectMeta.TryGetValue(obj.Id, out var meta);
        OriginalGameplayOverlay.Image? existingPreview =
            Ui.DisposePanel?.ObjectID == obj.Id ? Ui.DisposePanel.Preview : null;
        var nextState = new LiveUI.DisposePanelState(
            objectID: obj.Id,
            catalogName: hasMeta ? meta.CatalogName : string.Empty,
            resaleValue: hasMeta ? meta.ResaleValue : 0,
            sellable: obj.Sellable,
            buildModeType: hasMeta ? meta.BuildModeType : (obj.CatalogCategory is { } category ? Math.Max(0, category - 7) : 0),
            preview: existingPreview);

        if (Ui.DisposePanel?.ObjectID != nextState.ObjectID
            || !string.Equals(Ui.DisposePanel?.CatalogName, nextState.CatalogName, StringComparison.Ordinal)
            || Ui.DisposePanel?.ResaleValue != nextState.ResaleValue
            || Ui.DisposePanel?.Sellable != nextState.Sellable
            || Ui.DisposePanel?.BuildModeType != nextState.BuildModeType)
        {
            Ui.DisposePanel = nextState;
        }

        if (markSaveDirtyOnPickup is { } markDirty)
        {
            if (Send(
                "cursorObjectCreated",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["objectID"] = obj.Id,
                    ["dirty"] = markDirty,
                }) is { } reqID)
            {
                _pendingDisposePreviewRequest = (reqID, obj.Id);
            }
        }
        else if (existingPreview is null && _pendingDisposePreviewRequest?.ObjectID != obj.Id)
        {
            if (Send(
                "cursorObjectCreated",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["objectID"] = obj.Id,
                    ["dirty"] = false,
                }) is { } reqID)
            {
                _pendingDisposePreviewRequest = (reqID, obj.Id);
            }
        }
    }

    private void SellHeldObject()
    {
        if (!Ui.ObjectEditing
            || Renderer.Speed != 0
            || HasBlockingDialog
            || Ui.Buy.Selection.Target is not { IsPlaced: true, PlacedObject: { } obj })
        {
            return;
        }

        if (!obj.Sellable)
        {
            PlayUISound("ui_error");
            return;
        }

        if (Send(
            "sellObject",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["objectID"] = obj.Id }) is { } request)
        {
            _pendingSaleRequests.Add(request);
        }

        Ui.Buy.CancelSelection();
        SyncDisposePanel();
        _desiredPlacement = null;
        _checkedPlacement = null;
        try
        {
            Renderer.UpdatePlacement(null);
        }
        catch
        {
            // Ignore transient placement clear errors.
        }
    }

    /// <summary>
    /// "To delete doors, select the Door Tool and hold down the CTRL key... click to remove it" (build tips 21 and 93, also
    /// windows and placed build objects in their corresponding build subpanels): sells the build object under the pointer.
    /// </summary>
    private bool DeleteBuildObject(CGPoint point)
    {
        if (!Ui.BuildMode
            || !(Ui.Build.ObjectTool is not null
                 || (Ui.Build.TerrainMode is null
                     && (Ui.Build.Kind == BuildCatalogPage.CatalogKind.WallStyle
                         || Ui.Build.Kind == BuildCatalogPage.CatalogKind.Water)))
            || Renderer.Speed != 0
            || HasBlockingDialog
            || OverPanelControls(point)
            || OriginalGameplayOverlay.ContainsPanel(Local(point), LogicalCanvas, panelMinimized: IsLivePanelMinimized)
            || Renderer.ObjectAt(Simd(point), SimdCanvas) is not { } id
            || _editableObjects.FirstOrDefault(candidate =>
                candidate.Id == id
                && (candidate.Level == Renderer.ViewLevel
                    || ((candidate.MultiTileMasterID ?? 0) != 0
                        && (candidate.CatalogCategory == 10 || candidate.CatalogCategory == 12)))) is not { } obj
            || !CanEditBuildObject(obj))
        {
            return false;
        }

        if (!obj.Sellable)
        {
            PlayUISound("ui_error");
            return true;
        }

        if (Send(
            "sellObject",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["objectID"] = obj.Id }) is { } request)
        {
            _pendingSaleRequests.Add(request);
        }

        return true;
    }

    private bool OverPanelControls(CGPoint point)
    {
        CGPoint lp = Local(point);
        return OriginalGameplayOverlay.ContainsPanel(lp, LogicalCanvas, panelMinimized: IsLivePanelMinimized)
            || ((Ui.BuyMode || Ui.BuildMode || Ui.OptionsMode) && lp.Y >= LogicalCanvas.Height - 150.0)
            || (!Ui.Album.Visible
                && !Ui.Camera.Visible
                && Ui.PictureInPictureRect(LogicalCanvas, Scale)?.Contains(lp) == true)
            || OriginalUniversalControl.Hit(point, _canvas, Scale, Ui.HelpButtonSize) is not null
            || OriginalSpeedControl.Hit(point, _canvas, Scale) is not null
            || Ui.Camera.ModeRect(LogicalCanvas).Contains(lp)
            || (Ui.BuildMode && (!Ui.ShowsDisposePanel || lp.X < 220.0) && Ui.Build.ActionAt(lp, LogicalCanvas) is not null)
            || (Ui.BuyMode && (!Ui.ShowsDisposePanel || lp.X < 220.0) && Ui.Buy.ActionAt(lp, LogicalCanvas) is not null);
    }

    private FloorTileHit? FloorHit(CGPoint point) =>
        OverPanelControls(point) ? null : Renderer.FloorHitAt(Simd(point), SimdCanvas);

    private bool BuildToolsReady =>
        Ui.BuildMode
        && Ui.Build.ObjectTool is null
        && Ui.Build.SelectedObjectGUID is null
        && Ui.Buy.Selection.Target is null
        && Renderer.Speed == 0
        && !HasBlockingDialog;

    private bool FillRoom(CGPoint point, bool removing)
    {
        if (!BuildToolsReady
            || Ui.Build.TerrainMode is not null
            || !(removing || Ui.Build.SelectedItem is not null)
            || OverPanelControls(point))
        {
            return false;
        }

        FloorTileHit? hit = Renderer.FloorHitAt(Simd(point), SimdCanvas);
        if (BuildRoomFill.Create(
                kind: Ui.Build.Kind,
                pattern: Ui.Build.SelectedItem?.Id ?? 0,
                tile: hit?.Tile,
                wall: Renderer.WallAt(Simd(point), SimdCanvas),
                half: hit?.Half ?? 0,
                removing: removing,
                selectedLevel: Renderer.ViewLevel) is not { } fill)
        {
            return false;
        }

        _floorStroke = null;
        _floorBrush = null;
        try
        {
            Renderer.UpdateFloorBrush(null);
        }
        catch
        {
            // Ignore transient brush clear errors.
        }

        _ = SendWithBuildSound(
            "architecture",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = fill.Command,
                ["x"] = fill.X,
                ["y"] = fill.Y,
                ["level"] = fill.Level,
                ["x2"] = 0,
                ["y2"] = 0,
                ["pattern"] = (int)fill.Pattern,
                ["style"] = 0,
                ["half"] = fill.Half,
            });
        return true;
    }

    private void UpdateBuildPreview(CGPoint point)
    {
        if (Ui.ObjectEditing)
        {
            UpdatePlacement(point);
            return;
        }

        if (Ui.Build.TerrainMode is not null)
        {
            if (_terrainStrokeMode is null)
            {
                UpdateBuildCursor(point);
            }

            return;
        }

        switch (Ui.Build.Kind)
        {
            case BuildCatalogPage.CatalogKind.WallStyle:
                UpdateWallPreview(point);
                break;

            case BuildCatalogPage.CatalogKind.Wall:
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
                        // Ignore transient brush clear errors.
                    }
                }

                UpdateBuildCursor(point);
                break;

            default:
                ClearBuildCursor();
                UpdateFloorPreview(point);
                break;
        }
    }

    /// <summary>
    /// Asks the host for the build cursor at the vertex or tile under <paramref name="point"/>: the terrain tool's, the wallpaper tool's
    /// (cNewWallpaperTool graphic 2 at tile center in 0048a06a.c / 0048a1d1.c), or before a wall stroke, the wall tool's
    /// (its graphic follows the modifiers: 1 deletes with Ctrl or Alt, 3 draws a rectangle with Shift).
    /// </summary>
    private void UpdateBuildCursor(CGPoint point)
    {
        TerrainBrushMode? terrain = Ui.Build.TerrainMode;
        ushort mods = SDL_GetModState();
        bool deleting = (mods & (SDL_KMOD_ALT | SDL_KMOD_CTRL)) != 0;
        if (BuildToolsReady
            && !OverPanelControls(point)
            && terrain is null
            && Ui.Build.Kind == BuildCatalogPage.CatalogKind.Wall
            && (deleting || Ui.Build.SelectedItem is not null))
        {
            LotScene.WallSide? wall = Renderer.WallAt(Simd(point), SimdCanvas);
            SimsSDL.Core.GroundTile? hitTile = FloorHit(point)?.Tile;
            int tileX;
            int tileY;
            int level;
            if (wall is not null && wall.EdgeBit is not null)
            {
                tileX = wall.X;
                tileY = wall.Y;
                level = wall.Level;
            }
            else if (hitTile is { } hit)
            {
                tileX = hit.X;
                tileY = hit.Y;
                level = Renderer.ViewLevel;
            }
            else
            {
                ClearBuildCursor();
                return;
            }

            ushort? pattern = wall is not null && wall.EdgeBit is not null
                ? (deleting ? (ushort)0 : Ui.Build.SelectedItem?.Id)
                : (ushort?)null;
            bool fill = (mods & SDL_KMOD_SHIFT) != 0;
            _desiredBuildCursor = new BuildCursorKey
            {
                Guid = 0x439u,
                Graphic = 2,
                Vertex = new SimsSDL.Core.LotVertex(tileX, tileY),
                Level = level,
                Orientation = Renderer.CameraOrientation,
                Centered = true,
                Pattern = pattern,
                WallDirection = wall?.Direction ?? 0,
                WallEdge = wall?.EdgeBit,
                WallSide = wall?.Side,
                Fill = fill,
                Revision = Renderer.SceneRevisionNumber,
            };
            RequestBuildCursor();
            return;
        }

        if (!BuildToolsReady
            || OverPanelControls(point)
            || !(terrain is not null
                 || (Ui.Build.Kind == BuildCatalogPage.CatalogKind.WallStyle
                     && (Ui.Build.SelectedItem is not null || deleting)))
            || FloorHit(point) is null
            || Renderer.LotVertexAt(Simd(point), SimdCanvas) is not { } vertex)
        {
            ClearBuildCursor();
            return;
        }

        int graphic = terrain is { } mode
            ? mode switch
            {
                TerrainBrushMode.Raise => 0,
                TerrainBrushMode.Lower => 1,
                TerrainBrushMode.Grass => 2,
                _ => 3,
            }
            : (deleting ? 1 : (mods & SDL_KMOD_SHIFT) != 0 ? 3 : 0);

        _desiredBuildCursor = new BuildCursorKey
        {
            Guid = terrain is not null ? 0x2F39B7A6u : 0x439u,
            Graphic = graphic,
            Vertex = vertex,
            Level = Renderer.ViewLevel,
            Orientation = Renderer.CameraOrientation,
            Revision = Renderer.SceneRevisionNumber,
        };
        RequestBuildCursor();
    }

    private void RequestBuildCursor()
    {
        if (_pendingBuildCursor is not null
            || _desiredBuildCursor is not { } key
            || key == _checkedBuildCursor)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["guid"] = key.Guid,
            ["graphic"] = key.Graphic,
            ["x"] = key.Vertex.X,
            ["y"] = key.Vertex.Y,
            ["level"] = key.Level,
            ["direction"] = 1,
        };
        if (key.Centered)
        {
            parameters["centered"] = true;
        }

        if (key.Pattern is { } pattern)
        {
            parameters["pattern"] = (int)pattern;
            parameters["wallDirection"] = key.WallDirection;
            if (key.WallEdge is { } wallEdge)
            {
                parameters["wallEdge"] = wallEdge;
            }

            if (key.WallSide is { } wallSide)
            {
                parameters["wallSide"] = wallSide;
            }

            parameters["fill"] = key.Fill;
        }

        if (Send("buildCursor", parameters) is { } id)
        {
            _pendingBuildCursor = (id, key);
        }
    }

    private void ClearBuildCursor()
    {
        _desiredBuildCursor = null;
        _checkedBuildCursor = null;
        if (Renderer.BuildCursor is not null)
        {
            try
            {
                Renderer.UpdateBuildCursor(null);
            }
            catch
            {
                // Ignore transient cursor clear errors.
            }
        }

        if (Renderer.WallPaintPreview is not null)
        {
            try
            {
                Renderer.UpdateWallPaintPreview(null);
            }
            catch
            {
                // Ignore transient preview clear errors.
            }
        }
    }

    private (SimsSDL.Core.GroundTile Point, bool Buildable)? TerrainStrokePoint(CGPoint point, TerrainBrushMode mode)
    {
        if (!BuildToolsReady || OverPanelControls(point))
        {
            return null;
        }

        switch (mode)
        {
            case TerrainBrushMode.Raise:
            case TerrainBrushMode.Lower:
            {
                int w = Renderer.Scene?.LotWidth ?? 0;
                int h = Renderer.Scene?.LotHeight ?? 0;
                if (FloorHit(point) is null
                    || Renderer.LotVertexAt(Simd(point), SimdCanvas) is not { } vertex
                    || vertex.X < 0 || vertex.X >= w
                    || vertex.Y < 0 || vertex.Y >= h)
                {
                    return null;
                }

                return (new SimsSDL.Core.GroundTile(vertex), _buildable?.Contains(vertex) != false);
            }

            case TerrainBrushMode.Level:
            case TerrainBrushMode.Grass:
            {
                if (Renderer.GroundTileAt(Simd(point), SimdCanvas) is not { } tile)
                {
                    return null;
                }

                return (tile, _buildable?.Contains(tile) != false);
            }

            default:
                return null;
        }
    }

    /// <summary>The original drag tools' answer to a press or drag outside the buildable area (<c>FUN_00492c37</c>).</summary>
    private void RefuseBuild()
    {
        ulong now = SDL_GetTicks();
        if (now < _lastBuildRefusal + 400UL)
        {
            return;
        }

        _lastBuildRefusal = now;
        PlayUISound("ui_error");
    }

    private bool StartTerrainStroke(CGPoint point)
    {
        if (Ui.Build.TerrainMode is not { } mode
            || TerrainStrokePoint(point, mode) is not { } sampled)
        {
            return false;
        }

        if (!sampled.Buildable)
        {
            RefuseBuild();
            return true;
        }

        string strokeID = System.Guid.NewGuid().ToString("D").ToUpperInvariant();
        _terrainStrokeMode = mode;
        _terrainStrokeStartPoint = point;
        _terrainStrokeID = strokeID;
        _lastStreamedTerrainDelta = null;
        _terrainStrokePlayedSound = false;
        _terrainStrokeBaseHeight = null;
        if ((mode == TerrainBrushMode.Raise || mode == TerrainBrushMode.Lower)
            && Renderer.Scene is { } scene
            && scene.LotWidth is { } width
            && scene.LotHeight is { } height
            && scene.TerrainHeights is { } heights
            && sampled.Point.X >= 0 && sampled.Point.X < width
            && sampled.Point.Y >= 0 && sampled.Point.Y < height
            && heights.Count == width * height)
        {
            _terrainStrokeBaseHeight = (byte)Math.Clamp((int)heights[sampled.Point.Y * width + sampled.Point.X], 0, 255);
        }

        _terrainGesture = new TerrainBrushGesture();
        _terrainGesture.Begin(sampled.Point);
        if (mode == TerrainBrushMode.Grass)
        {
            FlushPendingGrassStroke(strokeID);
        }
        else if (mode == TerrainBrushMode.Level)
        {
            UpdateTerrainLevelPreview();
        }

        return true;
    }

    private void UpdateTerrainStroke(CGPoint point)
    {
        if (_terrainStrokeMode is not { } mode || _terrainStrokeID is not { } strokeID)
        {
            return;
        }

        if (mode == TerrainBrushMode.Raise || mode == TerrainBrushMode.Lower)
        {
            if (_terrainGesture.Start is not { } vertex
                || _terrainStrokeBaseHeight is not { } baseHeight
                || _terrainStrokeStartPoint is not { } startPoint
                || Renderer.Scene is not { } scene
                || scene.LotWidth is not { } width
                || scene.LotHeight is not { } height
                || Renderer.SceneViewport(SimdCanvas) is not { } viewport)
            {
                return;
            }

            try
            {
                short rawDelta = TerrainHeightDrag.Delta(
                    startPoint.Y,
                    point.Y,
                    viewport.Scale,
                    mode);
                if (rawDelta == 0 || rawDelta == _lastStreamedTerrainDelta)
                {
                    return;
                }

                ushort mods = SDL_GetModState();
                bool ctrlHeld = (mods & SDL_KMOD_CTRL) != 0 || ScriptDeleteModifier;
                ArchitectureCommandRequest request = TerrainBrushRequest.MakeLiveRaise(
                    mode: mode,
                    vertex: vertex,
                    baseHeight: baseHeight,
                    level: Renderer.ViewLevel,
                    lotWidth: width,
                    lotHeight: height,
                    stories: scene.Stories ?? 5,
                    heightDelta: rawDelta,
                    smooth: ctrlHeld);
                _lastStreamedTerrainDelta = rawDelta;
                SendTerrainBatch([request], strokeID);
            }
            catch (Exception error)
            {
                Log($"Terrain editing: {error}");
            }

            return;
        }

        if (TerrainStrokePoint(point, mode) is not { } sampled)
        {
            return;
        }

        if (!sampled.Buildable)
        {
            RefuseBuild();
            return;
        }

        _terrainGesture.Move(sampled.Point);
        if (mode == TerrainBrushMode.Grass)
        {
            FlushPendingGrassStroke(strokeID);
        }
        else if (mode == TerrainBrushMode.Level)
        {
            UpdateTerrainLevelPreview();
        }
    }

    private void FlushPendingGrassStroke(string strokeID)
    {
        if (Renderer.Scene is not { } scene)
        {
            return;
        }

        List<SimsSDL.Core.GroundTile> pending = _terrainGesture.DrainPendingGrassSamples();
        if (pending.Count == 0)
        {
            return;
        }

        ushort mods = SDL_GetModState();
        bool shiftHeld = (mods & SDL_KMOD_SHIFT) != 0 || ScriptShiftModifier;
        bool ctrlHeld = (mods & SDL_KMOD_CTRL) != 0 || ScriptDeleteModifier;
        short grassStep = (shiftHeld || ctrlHeld) ? (short)32 : (short)-32;
        try
        {
            List<ArchitectureCommandRequest> requests = TerrainBrushRequest.MakeStroke(
                mode: TerrainBrushMode.Grass,
                start: pending[0],
                end: null,
                samples: pending,
                level: Renderer.ViewLevel,
                scene: scene,
                grassStep: grassStep);
            if (requests.Count == 0)
            {
                return;
            }

            SendTerrainBatch(requests, strokeID);
        }
        catch (Exception error)
        {
            Log($"Terrain editing: {error}");
        }
    }

    private void SendTerrainBatch(IReadOnlyList<ArchitectureCommandRequest> requests, string strokeID)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["commands"] = requests.Select(static request => request.Payload).ToList(),
            ["strokeId"] = strokeID,
        };
        if (!_terrainStrokePlayedSound)
        {
            _terrainStrokePlayedSound = true;
            _ = SendWithBuildSound("architectureBatch", payload);
        }
        else
        {
            _ = Send("architectureBatch", payload);
        }
    }

    private void UpdateTerrainLevelPreview()
    {
        if (_terrainGesture.Start is not { } start
            || FloorBrushRectangle.Create(start, _terrainGesture.End ?? start) is not { } rectangle
            || FloorBrushSelection.Create(rectangle, half: 0) is not { } selection)
        {
            ClearTerrainLevelPreview();
            return;
        }

        if (selection == _floorBrush && !_floorBrushDeleting && _floorBrushTexture is null)
        {
            return;
        }

        try
        {
            Renderer.UpdateFloorBrush(selection, deleting: false, textureFile: null);
            _floorBrush = selection;
            _floorBrushDeleting = false;
            _floorBrushTexture = null;
        }
        catch (Exception error)
        {
            Log($"Terrain level brush: {error}");
        }
    }

    private void ClearTerrainLevelPreview()
    {
        if (_floorBrush is null)
        {
            return;
        }

        _floorBrush = null;
        _floorBrushDeleting = false;
        _floorBrushTexture = null;
        try
        {
            Renderer.UpdateFloorBrush(null);
        }
        catch
        {
            // Ignore transient brush clear errors.
        }
    }

    private void FinishTerrainStroke(CGPoint point)
    {
        if (_terrainStrokeMode is not { } mode)
        {
            return;
        }

        string strokeID = _terrainStrokeID ?? System.Guid.NewGuid().ToString("D").ToUpperInvariant();
        byte? baseHeight = _terrainStrokeBaseHeight;
        short? previousDelta = _lastStreamedTerrainDelta;
        bool playedSound = _terrainStrokePlayedSound;
        _terrainStrokeMode = null;
        CGPoint? startPoint = _terrainStrokeStartPoint;
        _terrainStrokeStartPoint = null;
        _terrainStrokeID = null;
        _terrainStrokeBaseHeight = null;
        _lastStreamedTerrainDelta = null;
        _terrainStrokePlayedSound = false;
        try
        {
            if (Renderer.Scene is not { } scene
                || Renderer.SceneViewport(SimdCanvas) is not { } viewport
                || _terrainGesture.Start is not { } startTile)
            {
                _terrainGesture = new TerrainBrushGesture();
                return;
            }

            if (mode != TerrainBrushMode.Raise
                && mode != TerrainBrushMode.Lower
                && TerrainStrokePoint(point, mode) is { Buildable: true } sampled)
            {
                _terrainGesture.Move(sampled.Point);
            }

            try
            {
                ushort mods = SDL_GetModState();
                bool shiftHeld = (mods & SDL_KMOD_SHIFT) != 0 || ScriptShiftModifier;
                bool ctrlHeld = (mods & SDL_KMOD_CTRL) != 0 || ScriptDeleteModifier;
                short grassStep = (shiftHeld || ctrlHeld) ? (short)32 : (short)-32;
                // "Hold down the CTRL key when raising terrain to make some nice curvy hills" (build tips); the host's smooth raise.
                bool smooth = (mode == TerrainBrushMode.Raise || mode == TerrainBrushMode.Lower) && ctrlHeld;
                List<ArchitectureCommandRequest> requests;
                if ((mode == TerrainBrushMode.Raise || mode == TerrainBrushMode.Lower)
                    && previousDelta is { } streamedDelta
                    && baseHeight is { } initialHeight
                    && scene.LotWidth is { } width
                    && scene.LotHeight is { } height)
                {
                    _terrainGesture.Cancel();
                    short rawDelta = TerrainHeightDrag.Delta(
                        startPoint?.Y ?? point.Y,
                        point.Y,
                        viewport.Scale,
                        mode);
                    if (rawDelta == streamedDelta)
                    {
                        return;
                    }

                    requests =
                    [
                        TerrainBrushRequest.MakeLiveRaise(
                            mode: mode,
                            vertex: startTile,
                            baseHeight: initialHeight,
                            level: Renderer.ViewLevel,
                            lotWidth: width,
                            lotHeight: height,
                            stories: scene.Stories ?? 5,
                            heightDelta: rawDelta,
                            smooth: smooth),
                    ];
                }
                else
                {
                    short? heightDelta;
                    if (mode == TerrainBrushMode.Raise || mode == TerrainBrushMode.Lower)
                    {
                        short rawDelta = TerrainHeightDrag.Delta(
                            startPoint?.Y ?? point.Y,
                            point.Y,
                            viewport.Scale,
                            mode);
                        int shiftInvert = shiftHeld ? -1 : 1;
                        short clickDelta = (short)((mode == TerrainBrushMode.Raise ? 1 : -1) * shiftInvert);
                        heightDelta = rawDelta == 0 ? clickDelta : rawDelta;
                    }
                    else
                    {
                        heightDelta = null;
                    }

                    requests = _terrainGesture.FinishStroke(
                        mode: mode,
                        level: Renderer.ViewLevel,
                        scene: scene,
                        heightDelta: heightDelta,
                        grassStep: grassStep,
                        smooth: smooth);
                }

                if (requests.Count == 0)
                {
                    return;
                }

                var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["commands"] = requests.Select(static request => request.Payload).ToList(),
                    ["strokeId"] = strokeID,
                };
                if (!playedSound)
                {
                    _ = SendWithBuildSound("architectureBatch", payload);
                }
                else
                {
                    _ = Send("architectureBatch", payload);
                }
            }
            catch (Exception error)
            {
                _terrainGesture = new TerrainBrushGesture();
                Log($"Terrain editing: {error}");
            }
        }
        finally
        {
            if (mode == TerrainBrushMode.Level)
            {
                ClearTerrainLevelPreview();
            }
        }
    }

    private bool StartWallStroke(CGPoint point, bool rectangle, bool deleting)
    {
        ushort? style = deleting ? (Ui.Build.SelectedItem?.Id ?? (ushort)1) : Ui.Build.SelectedItem?.Id;
        ushort? pattern = deleting ? (Ui.Build.SelectedItem?.Pattern ?? (ushort)0) : Ui.Build.SelectedItem?.Pattern;
        if (!BuildToolsReady
            || Ui.Build.Kind != BuildCatalogPage.CatalogKind.WallStyle
            || style is not { } styleValue
            || pattern is not { } patternValue
            || FloorHit(point) is null
            || Renderer.LotVertexAt(Simd(point), SimdCanvas) is not { } vertex)
        {
            return false;
        }

        if (_buildable?.Contains(vertex) == false)
        {
            RefuseBuild();
            return true;
        }

        _wallStroke = new WallStroke
        {
            Start = vertex,
            Style = styleValue,
            Pattern = patternValue,
            Mode = new WallStrokeMode(Rectangle: rectangle, Deleting: deleting),
        };
        PlayUISound("ui_bld_dragtool_mousedown");
        return true;
    }

    /// <summary>
    /// The stroke's wall from its start to the vertex under <paramref name="point"/>; past the buildable area the wall stops where it
    /// last was, with the error sound, as the original drag tools do.
    /// </summary>
    private WallBrushSelection? WallLine(CGPoint point)
    {
        if (!BuildToolsReady
            || Ui.Build.Kind != BuildCatalogPage.CatalogKind.WallStyle
            || _wallStroke is not { } stroke
            || (!stroke.Mode.Deleting && Ui.Build.SelectedItem?.Id != stroke.Style)
            || FloorHit(point) is null
            || Renderer.LotVertexAt(Simd(point), SimdCanvas) is not { } vertex)
        {
            return null;
        }

        if (_buildable?.Contains(vertex) == false)
        {
            RefuseBuild();
            return _wallBrush;
        }

        ushort modifiers = SDL_GetModState();
        var mode = new WallStrokeMode(
            Rectangle: (modifiers & SDL_KMOD_SHIFT) != 0,
            Deleting: (modifiers & SDL_KMOD_CTRL) != 0 || (modifiers & SDL_KMOD_ALT) != 0);
        if (mode != stroke.Mode)
        {
            if (WallBrushSelection.Create(
                    stroke.Start,
                    vertex,
                    stroke.Mode.Rectangle,
                    stroke.Mode.Deleting) is { } previous)
            {
                stroke.Completed.Add(new WallStrokeSegment(previous, stroke.Style, stroke.Pattern));
            }

            // cNewWallTool resets its wall undo planner and establishes the current cursor as a new origin when the
            // Control/Shift mode changes mid-drag (Drag, 10177854..1017789C).
            stroke.Start = vertex;
            stroke.Mode = mode;
        }

        return WallBrushSelection.Create(
            stroke.Start,
            vertex,
            stroke.Mode.Rectangle,
            stroke.Mode.Deleting);
    }
}
