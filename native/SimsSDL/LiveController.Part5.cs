using StbImageWriteSharp;

namespace SimsSDL;

internal sealed partial class LiveController
{
    private void ReloadNeighborhood(
        string? edit,
        int mode = 1,
        int? targetNeighborhood = null,
        (string Title, string Message)? errorMessage = null,
        Action? completion = null,
        Action? failure = null)
    {
        if (edit is not null)
        {
            if (_loadingNeighborhood || _editingNeighborhood)
            {
                return;
            }

            _editingNeighborhood = true;
        }
        else
        {
            if (_editingNeighborhood)
            {
                return;
            }

            if (targetNeighborhood is null)
            {
                _pendingNeighborhoodTarget = null;
            }
        }

        _evictMode = false;
        _neighborhoodLoadGeneration = unchecked(_neighborhoodLoadGeneration + 1);
        ulong requestGen = _neighborhoodLoadGeneration;
        int requestedNeighborhood = targetNeighborhood ?? _currentNeighborhood;
        _loadingNeighborhood = true;
        // Retains the current neighborhood view under an hourglass cursor while switching sub-neighborhood
        // maps instead of showing the startup splash screen.
        _ = ReloadNeighborhoodCoreAsync(
            edit,
            mode,
            requestGen,
            requestedNeighborhood,
            errorMessage,
            completion,
            failure);
    }

    private async Task ReloadNeighborhoodCoreAsync(
        string? edit,
        int mode,
        ulong requestGen,
        int requestedNeighborhood,
        (string Title, string Message)? errorMessage,
        Action? completion,
        Action? failure)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        try
        {
            var exported = await FetchNeighborhoodManifest(
                mode: mode,
                edit: edit,
                neighborhood: requestedNeighborhood).ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                try
                {
                    if (self._neighborhoodLoadGeneration != requestGen)
                    {
                        return;
                    }

                    int? previousMode = self.Ui.Neighborhood?.Mode;
                    var neighborhood = new OriginalNeighborhoodOverlay(
                        self.Content,
                        self.Ui.Device,
                        exported.Manifest,
                        exported.Session,
                        self.Ui.Font);
                    self.Ui.Neighborhood = neighborhood;
                    self._neighborhoodHouseSelected = house =>
                    {
                        if (weakSelf.TryGetTarget(out var target))
                        {
                            target.SelectNeighborhoodHouse(house);
                        }
                    };
                    if (neighborhood.Mode != previousMode)
                    {
                        self.StopNeighborhoodAmbientAudio();
                        self.StartNeighborhoodAudio(neighborhood);
                    }

                    if (edit is not null)
                    {
                        self._editingNeighborhood = false;
                    }

                    self._loadingNeighborhood = false;
                    self.Ui.Loading = false;
                    completion?.Invoke();
                }
                catch (Exception error)
                {
                    FailNeighborhoodReload(self, requestGen, errorMessage, failure, error);
                }
                finally
                {
                    FinishNeighborhoodReload(self, edit, requestGen);
                }
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                try
                {
                    FailNeighborhoodReload(self, requestGen, errorMessage, failure, error);
                }
                finally
                {
                    FinishNeighborhoodReload(self, edit, requestGen);
                }
            });
        }
    }

    private static void FailNeighborhoodReload(
        LiveController self,
        ulong requestGen,
        (string Title, string Message)? errorMessage,
        Action? failure,
        Exception error)
    {
        if (self._neighborhoodLoadGeneration != requestGen)
        {
            return;
        }

        self._loadingNeighborhood = false;
        self.Ui.Loading = false;
        failure?.Invoke();
        self.PlayUISound("ui_nhood_error");
        self.PresentLocalFeedback(
            errorMessage?.Title ?? self.Strings.MoveInErrorTitle(),
            errorMessage?.Message ?? self.Strings.EvictFailedMessage());
        Log($"Neighborhood edit: {error}");
    }

    private static void FinishNeighborhoodReload(LiveController self, string? edit, ulong requestGen)
    {
        if (edit is not null)
        {
            self._editingNeighborhood = false;
        }

        if (self._neighborhoodLoadGeneration == requestGen)
        {
            self._loadingNeighborhood = false;
            self.Ui.Loading = false;
        }
    }

    private void ExportNeighborhoodWebPages(bool open = true, int? fallbackMode = null)
    {
        if (_exportingWebPages)
        {
            return;
        }

        int mode = Ui.Neighborhood?.Mode ?? fallbackMode ?? 1;
        if (mode is not (1 or 2 or 3 or 5))
        {
            return;
        }

        if (Ui.Neighborhood is { } neighborhood && neighborhood.WebHouseRenderRequests.Count > 0)
        {
            WriteNeighborhoodWebPages(neighborhood, open);
            return;
        }

        _exportingWebPages = true;
        int requestedNeighborhood = _currentNeighborhood;
        _ = ExportNeighborhoodWebPagesCoreAsync(mode, requestedNeighborhood, open);
    }

    private async Task ExportNeighborhoodWebPagesCoreAsync(int mode, int requestedNeighborhood, bool open)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        try
        {
            var exported = await FetchNeighborhoodManifest(
                mode: mode,
                edit: null,
                neighborhood: requestedNeighborhood,
                includeWebScenes: true).ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                try
                {
                    var webOverlay = new OriginalNeighborhoodOverlay(
                        self.Content,
                        self.Ui.Device,
                        exported.Manifest,
                        exported.Session,
                        self.Ui.Font);
                    self.WriteNeighborhoodWebPages(webOverlay, open);
                }
                catch (Exception error)
                {
                    ReportWebExportFailure(self, error);
                }
                finally
                {
                    self._exportingWebPages = false;
                }
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                try
                {
                    ReportWebExportFailure(self, error);
                }
                finally
                {
                    self._exportingWebPages = false;
                }
            });
        }
    }

    private static void ReportWebExportFailure(LiveController self, Exception error)
    {
        self.PlayUISound("ui_nhood_error");
        self.PresentLocalFeedback(
            self.Strings.SorryTitle(),
            $"The web pages could not be written: {error.Message}");
    }

    private void WriteNeighborhoodWebPages(OriginalNeighborhoodOverlay neighborhood, bool open)
    {
        string destination = Path.Combine(
            Options.DataRoot ?? Workspace,
            "runtime",
            "profiles",
            Options.Profile,
            "Web Pages");
        try
        {
            var houseViews = RenderWebHouseViews(neighborhood.WebHouseRenderRequests);
            string page = WebExport.Export(
                Content.Root,
                neighborhood.WebFamilies(houseViews),
                destination,
                Content.Language);
            Log($"Web pages written to {destination}");
            if (!open || Options.Script is not null)
            {
                return;
            }

            _ = SDL_OpenURL(new Uri(Path.GetFullPath(page)).AbsoluteUri);
        }
        catch (Exception error)
        {
            ReportWebExportFailure(this, error);
        }
    }

    /// <summary>Renders thumbnail, exterior, and floor snapshots for occupied residential lots using the
    /// lot renderer to populate neighborhood web export templates.</summary>
    private Dictionary<int, NeighborhoodWebExport.HouseViews> RenderWebHouseViews(
        IReadOnlyList<(int House, string Thumbnail, string Exterior, string Floor1, string? Floor2)> requests)
    {
        if (requests.Count == 0)
        {
            return new Dictionary<int, NeighborhoodWebExport.HouseViews>();
        }

        byte[] Jpeg(string scenePath, int house)
        {
            var snapshot = new LotRenderer(Renderer.GPU);
            snapshot.LoadScene(scenePath);
            snapshot.Zoom = LotZoom.Far;
            if (snapshot.Scene is { } scene)
            {
                float fit = MathF.Min(432f / MathF.Max(1f, scene.Width), 288f / MathF.Max(1f, scene.Height));
                snapshot.UIScale = fit / snapshot.Zoom.Factor();
            }

            using var targets = new RenderTargets(Renderer.GPU, 432, 288);
            nint command = SDL_AcquireGPUCommandBuffer(Renderer.GPU.Device);
            if (command == nint.Zero)
            {
                throw new SDLError("SDL_AcquireGPUCommandBuffer");
            }

            snapshot.Encode(command, targets.Color, targets.Depth);
            if (!SDL_SubmitGPUCommandBuffer(command))
            {
                throw new SDLError("SDL_SubmitGPUCommandBuffer");
            }

            byte[] rgba = Renderer.GPU.ReadPixels(targets.Color);
            using var stream = new MemoryStream();
            var writer = new ImageWriter();
            writer.WriteJpg(rgba, 432, 288, ColorComponents.RedGreenBlueAlpha, stream, 90);
            byte[] output = stream.ToArray();
            if (output.Length == 0)
            {
                throw new ContentError($"Could not encode the lot snapshot for house {house}.");
            }

            return output;
        }

        var result = new Dictionary<int, NeighborhoodWebExport.HouseViews>(requests.Count);
        foreach (var request in requests)
        {
            result[request.House] = new NeighborhoodWebExport.HouseViews(
                Thumbnail: Jpeg(request.Thumbnail, request.House),
                Exterior: Jpeg(request.Exterior, request.House),
                Floor1: Jpeg(request.Floor1, request.House),
                Floor2: request.Floor2 is { } floor2 ? Jpeg(floor2, request.House) : null);
        }

        return result;
    }

    private void OpenCredits()
    {
        try
        {
            Ui.Credits = new OriginalCreditsOverlay(Content, Ui.Font, Ui.Device, Strings);
        }
        catch (Exception error)
        {
            ShowNeighborhoodError(error);
            return;
        }

        if (Bridge is null || _daemonMode)
        {
            PlayFrontendMusic(OriginalCreditsOverlay.MusicForNeighborhoodMode(Ui.Neighborhood?.Mode ?? 1));
        }
    }

    /// <summary>Handles quit requests by dismissing open credits, prompting to save on active lots with
    /// save capabilities, or displaying a standard confirmation dialog elsewhere.</summary>
    public void ConfirmQuit()
    {
        if (Ui.GameDialog is not null)
        {
            return;
        }

        if (Ui.Credits is not null)
        {
            CloseCredits();
            return;
        }

        var weakSelf = new WeakReference<LiveController>(this);
        if (HasActiveLot && Ui.ShowsLot && !LotSaveDisabled)
        {
            PresentLocalConfirmation(
                Strings.QuitSaveTitle(),
                Strings.SaveBeforeQuitMessage(),
                Strings.YesButton(),
                Strings.NoButton(),
                Strings.CancelButton(),
                response: code =>
                {
                    if (!weakSelf.TryGetTarget(out var self))
                    {
                        return;
                    }

                    switch (code)
                    {
                        case 0:
                            self.SendSaveRequest(succeeded =>
                            {
                                if (succeeded && weakSelf.TryGetTarget(out var target))
                                {
                                    target.QuitRequested = true;
                                }
                            });
                            break;
                        case 1:
                            self.QuitRequested = true;
                            break;
                    }
                });
        }
        else
        {
            PresentLocalConfirmation(
                Strings.QuitTitle(),
                Strings.QuitMessage(),
                Strings.YesButton(),
                Strings.NoButton(),
                response: code =>
                {
                    if (code == 0 && weakSelf.TryGetTarget(out var self))
                    {
                        self.QuitRequested = true;
                    }
                });
        }
    }

    private void CloseCredits()
    {
        Ui.Credits = null;
        if ((Bridge is null || _daemonMode) && Ui.Neighborhood?.Music is { Length: > 0 } music)
        {
            PlayFrontendMusic(music);
        }
    }

    private void OpenFamilyPicker(int house, IReadOnlyList<OriginalFamilyPickerOverlay.Family> families)
    {
        try
        {
            Ui.FamilyPicker = new OriginalFamilyPickerOverlay(
                house: house,
                families: families,
                content: Content,
                font: Ui.Font,
                device: Ui.Device,
                strings: Strings,
                isMagicTown: Ui.Neighborhood?.Mode == 7,
                quickTips: Ui.OptionsPanel.PlayOptions.QuickTips);
        }
        catch (Exception error)
        {
            ShowNeighborhoodError(error);
            return;
        }

        if (Bridge is null || _daemonMode)
        {
            PlayFrontendMusic("bkground_family");
        }
    }

    private void CloseFamilyPicker(bool returnsToMap = true)
    {
        Ui.FamilyPicker = null;
        if (returnsToMap && (Bridge is null || _daemonMode) && Ui.Neighborhood?.Music is { Length: > 0 } music)
        {
            PlayFrontendMusic(music);
        }
    }

    private void HandleFamilyPicker(OriginalFamilyPickerOverlay.Action action, OriginalFamilyPickerOverlay picker)
    {
        if (Ui.Neighborhood is not { } neighborhood)
        {
            return;
        }

        PlayUISound("ui_click");
        switch (action)
        {
            case OriginalFamilyPickerOverlay.Action.Cancel:
                CloseFamilyPicker();
                break;

            case OriginalFamilyPickerOverlay.Action.CreateFamily when picker.House == 0:
            {
                if (_daemonMode && _bridgeConnected)
                {
                    CloseFamilyPicker(returnsToMap: false);
                    OpenHouseholdCreationInDaemon(lot: 0, neighborhood, unhoused: true, reopenPickerOnCancel: true);
                    return;
                }

                // Create a Family executes on a lot, preferring an unoccupied residential lot and falling
                // back to any existing residential lot.
                static int? FirstMatchingLot(IEnumerable<int> range, Func<int, bool> predicate)
                {
                    foreach (int candidate in range)
                    {
                        if (predicate(candidate))
                        {
                            return candidate;
                        }
                    }

                    return null;
                }

                int? host = (neighborhood.VacantResidentialLots.Count > 0 ? (int?)neighborhood.VacantResidentialLots[0] : null)
                    ?? FirstMatchingLot(Enumerable.Range(1, 10), h => neighborhood.HasLot(h) && neighborhood.ZoningIndex(h) == 0)
                    ?? FirstMatchingLot(Enumerable.Range(1, 10), neighborhood.HasLot)
                    ?? FirstMatchingLot(Enumerable.Range(90, 3), neighborhood.HasLot)
                    ?? (neighborhood.HasLot(1) ? (int?)1 : null);
                if (host is not int hostLot)
                {
                    PlayUISound("ui_nhood_error");
                    PresentLocalFeedback(Strings.SorryTitle(), Strings.MoveInErrorMessage());
                    return;
                }

                CloseFamilyPicker(returnsToMap: false);
                OpenNeighborhoodHouse(hostLot, createHousehold: true);
                _householdCreationUnhoused = true;
                _returnToFamilyPickerAfterHouseholdCreation = true;
                break;
            }

            case OriginalFamilyPickerOverlay.Action.CreateFamily:
                if (!neighborhood.CanCreateHousehold(picker.House))
                {
                    PlayUISound("ui_nhood_error");
                    PresentLocalFeedback(Strings.SorryTitle(), Strings.CannotAffordMessage());
                    return;
                }

                CloseFamilyPicker(returnsToMap: false);
                if (_daemonMode && _bridgeConnected)
                {
                    OpenHouseholdCreationInDaemon(lot: picker.House, neighborhood, unhoused: false, reopenPickerOnCancel: false);
                }
                else
                {
                    OpenNeighborhoodHouse(picker.House, createHousehold: true);
                }

                break;

            case OriginalFamilyPickerOverlay.Action.Delete(var family):
            {
                int mode = neighborhood.Mode;
                int house = picker.House;
                string message = OriginalUIStrings.Format(
                    Strings.PickFamily(7, fallback: "Are you sure you want to delete the \"%s\" family?"),
                    [family.Name]);
                var weakSelf = new WeakReference<LiveController>(this);
                PresentLocalConfirmation(
                    Strings.PickFamily(6, fallback: "Delete Family?"),
                    message,
                    Strings.YesButton(),
                    Strings.NoButton(),
                    response: code =>
                    {
                        if (code != 0 || !weakSelf.TryGetTarget(out var self))
                        {
                            return;
                        }

                        self.CloseFamilyPicker(returnsToMap: false);
                        self.ReloadNeighborhood(
                            edit: $"delete:{family.Id}",
                            mode: mode,
                            completion: () =>
                            {
                                if (!weakSelf.TryGetTarget(out var inner) || inner.Ui.Neighborhood is not { } updated)
                                {
                                    return;
                                }

                                inner.OpenFamilyPicker(house: house, families: updated.Families);
                            });
                    });
                break;
            }

            case OriginalFamilyPickerOverlay.Action.MoveIn(var family) when picker.House == 0:
                CloseFamilyPicker();
                FamilyPlacement = family;
                Log($"choose a lot for the {family.Name} family");
                break;

            case OriginalFamilyPickerOverlay.Action.MoveIn(var family):
                OfferLot(picker.House, family, neighborhood);
                break;
        }
    }

    /// <summary>Validates family funds (and MagiCoins for Magic Town lots 90–92) against the lot cost, then
    /// displays the purchase confirmation prompt from string table 132.</summary>
    private void OfferLot(int house, OriginalFamilyPickerOverlay.Family family, OriginalNeighborhoodOverlay neighborhood)
    {
        int price = neighborhood.Price(house) ?? int.MaxValue;
        int requiredMagiCoins = house is >= 90 and <= 92 ? (house == 91 ? 150 : 100) : 0;
        if (family.Budget < price || (family.MagiCoins ?? 0) < requiredMagiCoins)
        {
            PlayUISound("ui_nhood_error");
            PresentLocalFeedback(Strings.SorryTitle(), Strings.CannotAffordMessage());
            return;
        }

        var weakSelf = new WeakReference<LiveController>(this);
        PresentLocalConfirmation(
            Strings.PurchaseHouseTitle(),
            Strings.PurchaseHouseMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0 || !weakSelf.TryGetTarget(out var self))
                {
                    return;
                }

                self.CloseFamilyPicker(returnsToMap: false);
                self._pendingFamilyMoveIn = (house, family.Id, null);
                self.OpenNeighborhoodHouse(house, createHousehold: false, retainNeighborhood: true);
            });
    }

    private void OpenHouseholdCreationInDaemon(
        int lot,
        OriginalNeighborhoodOverlay neighborhood,
        bool unhoused,
        bool reopenPickerOnCancel)
    {
        StopNeighborhoodAmbientAudio();
        if (lot > 0)
        {
            _lastNeighborhoodMode = neighborhood.Mode;
            LotSaveDisabled = neighborhood.Mode == 1 && (neighborhood.ZoningIndex(lot) != 0 || (lot >= 93 && lot <= 99));
        }

        _neighborhoodBeforeHouseholdCreation = neighborhood;
        Ui.Neighborhood = null;
        _neighborhoodHouseSelected = null;
        Ui.FamilyPicker = null;
        Ui.HouseholdCreation = null;
        _rezoneMode = false;
        _evictMode = false;
        _pendingHouseholdCreation = lot;
        _householdCreationUnhoused = unhoused;
        _returnToFamilyPickerAfterHouseholdCreation = reopenPickerOnCancel;
        _deferredAppearanceCatalogRequest = null;
        _queuedAppearanceCatalogRequest = null;
        _pendingAppearanceCatalogKey = null;
        _prewarmingAppearanceCatalog = false;
        _queuedAppearancePreviewRequest = null;
        if (!unhoused)
        {
            _highestReceivedSceneRevision = 0;
            _minimumSceneRevisionToFinishLoading = 1;
            _latestQueuedSceneRevision = 0;
            ClearPendingViewState();
        }

        Ui.Loading = false;
        _openingNeighborhoodLot = false;
        _hasCompletedInitialLoad = true;
        PlayFrontendMusic("bkground_family");
        try
        {
            OpenHouseholdCreationOverlay(lot);
            PrewarmHouseholdAppearanceCatalog();
        }
        catch (Exception error)
        {
            RestoreNeighborhoodAfterHouseholdCreation();
            ShowNeighborhoodError(error);
        }
    }

    private void PrewarmHouseholdAppearanceCatalog()
    {
        _ = Send("audioEvent", new Dictionary<string, object?> { ["name"] = "bkground_family" });
        if (_deferredAppearanceCatalogRequest is { } req && Bridge is { } bridge)
        {
            _deferredAppearanceCatalogRequest = null;
            _pendingAppearanceCatalogKey = $"{req.Gender}|{req.Age}|{req.Skin}";
            _prewarmingAppearanceCatalog = false;
            try
            {
                _pendingHouseholdAppearanceRequest = bridge.Send(
                    "appearanceCatalog",
                    new Dictionary<string, object?>
                    {
                        ["gender"] = req.Gender,
                        ["age"] = req.Age,
                        ["skin"] = req.Skin,
                    });
            }
            catch
            {
                _pendingHouseholdAppearanceRequest = null;
            }
        }
        else if (_pendingHouseholdAppearanceRequest is null && Bridge is { } fallbackBridge)
        {
            _pendingAppearanceCatalogKey = "m|adult|med";
            _prewarmingAppearanceCatalog = true;
            try
            {
                _pendingHouseholdAppearanceRequest = fallbackBridge.Send(
                    "appearanceCatalog",
                    new Dictionary<string, object?>
                    {
                        ["gender"] = "m",
                        ["age"] = "adult",
                        ["skin"] = "med",
                    });
            }
            catch
            {
                _pendingHouseholdAppearanceRequest = null;
            }
        }
    }

    private void OpenNeighborhoodHouse(int house, bool createHousehold, bool retainNeighborhood = false)
    {
        if (Ui.Neighborhood is { } neighborhood)
        {
            _lastNeighborhoodMode = neighborhood.Mode;
            LotSaveDisabled = neighborhood.Mode == 1 && (neighborhood.ZoningIndex(house) != 0 || (house >= 93 && house <= 99));
        }

        _neighborhoodBeforeHouseholdCreation = createHousehold || retainNeighborhood ? Ui.Neighborhood : null;
        if (createHousehold)
        {
            Ui.Neighborhood = null;
            _neighborhoodHouseSelected = null;
        }

        Ui.FamilyPicker = null;
        Ui.HouseholdCreation = null;
        _rezoneMode = false;
        _evictMode = false;
        _pendingHouseholdCreation = createHousehold ? house : null;
        _householdCreationUnhoused = false;
        _returnToFamilyPickerAfterHouseholdCreation = false;
        if (house == 7)
        {
            Renderer.SetZoom(LotZoom.Far, instant: true);
        }

        if (!createHousehold && _daemonMode && _bridgeConnected && Bridge is not null)
        {
            OpenDaemonLot(house);
        }
        else
        {
            StartSimulation(house);
        }
    }

    private void OpenDaemonLot(int house)
    {
        if (Bridge is not { } bridge)
        {
            return;
        }

        StopNeighborhoodAmbientAudio();
        _frontendMusic = null;
        _currentHouse = house;
        HomeLotHouse = _pendingHouseholdCreation is null ? house : null;
        AwayBuyBuildLocked = false;
        _simlessBuildMode = false;
        Ui.PanelMinimized = false;
        _daemonWarmedUp = true;
        _deferredAppearanceCatalogRequest = null;
        _queuedAppearanceCatalogRequest = null;
        _pendingAppearanceCatalogKey = null;
        _prewarmingAppearanceCatalog = false;
        _cachedAppearanceCatalogs.Clear();
        _queuedAppearancePreviewRequest = null;
        _simulatorZoom = null;
        _simulatorMode = null;
        _simulatorViewScrolling = null;
        _lastSimulatorPan = Renderer.Pan;
        _tutorialLiveModeClicked = false;
        _lastRunningSpeed = Math.Max(1, Options.Speed ?? 1);
        _liveModePaused = Options.Speed == 0;
        _highestReceivedSceneRevision = 0;
        _minimumSceneRevisionToFinishLoading = 1;
        _awaitingLotSwitchChange = false;
        _switchingLot = false;
        _pendingTravelSelection = null;
        _lastHouseStateClass = null;
        _lastLotTransitInfo = 0;
        ClearPendingViewState();
        _latestQueuedSceneRevision = 0;
        _pendingPlacement = null;
        _pendingBuildCursor = null;
        _editableObjects = [];
        _placedObjectMeta.Clear();
        _pendingSaleRequests.Clear();
        _pendingDisposePreviewRequest = null;
        Ui.DisposePanel = null;
        ClosePictureInPicture();
        Renderer.LiveMode = true;
        Renderer.DrawsLot = Ui.ShowsLot;
        Renderer.ApplyEnvironment(null);
        _lastAudioSceneRevision = -1;
        _lastAudioAnimationSequence = -1;
        Ui.Loading = false;
        _openingNeighborhoodLot = true;
        _centresOnSelectedSim = Ui.OptionsPanel.PlayOptions.AutoCenter;
        _pendingPieHead = null;
        _desiredPieHead = null;
        _checkedPieHead = null;

        _ = OpenDaemonLotCoreAsync(house, bridge);
    }

    private async Task OpenDaemonLotCoreAsync(int house, SimulationBridge bridge)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakBridge = new WeakReference<SimulationBridge>(bridge);
        try
        {
            var tcs = new TaskCompletionSource<DaemonOpenLotResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            string requestId = bridge.Send("openLot", new Dictionary<string, object?>(StringComparer.Ordinal) { ["house"] = house });
            _pendingDaemonOpenLotRequests[requestId] = tcs;
            DaemonOpenLotResult result = await tcs.Task.ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target) || self._currentHouse != house)
                {
                    return;
                }

                Log($"live session {target.Session}");
                bool simless = result.Simless;
                self._simlessBuildMode = simless;
                if (result.HomeHouse is int hh)
                {
                    self.HomeLotHouse = hh;
                }

                if (simless)
                {
                    self.AwayBuyBuildLocked = false;
                }
                else
                {
                    self.UpdateAwayLotLocks(house, result.Away);
                }

                _ = self.Send("audio", new Dictionary<string, object?> { ["enabled"] = self._audio is not null });
                _ = self.Send("stream", new Dictionary<string, object?> { ["enabled"] = true });
                self.ApplyPlayOptions();
                if (self.Options.Speed is int speed && self._pendingHouseholdCreation is null && !simless)
                {
                    self._liveModePaused = speed == 0;
                    if (speed > 0)
                    {
                        self._lastRunningSpeed = speed;
                    }

                    _ = self.Send("speed", new Dictionary<string, object?> { ["value"] = speed });
                }
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakBridge.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self.Bridge, target))
                {
                    return;
                }

                Log($"Open lot {house}: {error}");
                self._currentHouse = 0;
                self._openingNeighborhoodLot = false;
                self._switchingLot = false;
                self._awaitingLotSwitchChange = false;
                self._pendingFamilyMoveIn = null;
                self.RestoreNeighborhoodAfterHouseholdCreation();
                self.ShowNeighborhoodError(error);
            });
        }
    }

    private void ConfirmCancelHouseholdCreation()
    {
        if (Ui.HouseholdCreation is null)
        {
            return;
        }

        var weakSelf = new WeakReference<LiveController>(this);
        PresentLocalConfirmation(
            Strings.FamilyCancelConfirmationTitle(),
            Strings.FamilyCancelConfirmationMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code == 0 && weakSelf.TryGetTarget(out var self))
                {
                    self.CancelHouseholdCreation();
                }
            });
    }

    private void CancelHouseholdCreation(int? reloadMode = null, bool reopenFamilyPicker = false)
    {
        _lastAudioSceneRevision = -1;
        _lastAudioAnimationSequence = -1;
        if (_daemonMode)
        {
            if ((_currentHouse ?? 0) > 0)
            {
                _ = Send("closeLot", new Dictionary<string, object?> { ["save"] = false });
                _currentHouse = 0;
            }

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
        DismissMenu();
        Ui.GameDialog = null;
        RestoreNeighborhoodAfterHouseholdCreation(reloadMode, reopenFamilyPicker);
    }

    private void RestoreNeighborhoodAfterHouseholdCreation(int? reloadMode = null, bool reopenFamilyPicker = false)
    {
        bool reopenOnCancel = _returnToFamilyPickerAfterHouseholdCreation;
        _returnToFamilyPickerAfterHouseholdCreation = false;
        _householdCreationUnhoused = false;
        Ui.HouseholdCreation = null;
        Renderer.LiveMode = false;
        Renderer.DrawsLot = false;
        Renderer.ClearAppearancePreview();
        _familyPortraits.Clear();
        _pendingHouseholdCreation = null;
        _pendingHouseholdCreationRequest = null;
        _pendingHouseholdAppearanceRequest = null;
        _deferredAppearanceCatalogRequest = null;
        _queuedAppearanceCatalogRequest = null;
        _pendingAppearanceCatalogKey = null;
        _prewarmingAppearanceCatalog = false;
        _cachedAppearanceCatalogs.Clear();
        _queuedAppearancePreviewRequest = null;
        if (!_daemonMode)
        {
            _bridgeConnected = false;
        }

        _pendingHouseholdPreviewRequest = null;
        if (_neighborhoodBeforeHouseholdCreation is not { } previous)
        {
            LoadBaseNeighborhood(
                mode: reloadMode ?? NeighborhoodModeForCurrentLot(),
                reopenFamilyPicker: reopenFamilyPicker || reopenOnCancel);
            return;
        }

        _neighborhoodBeforeHouseholdCreation = null;
        Ui.Neighborhood = previous;
        var weakSelf = new WeakReference<LiveController>(this);
        _neighborhoodHouseSelected = house =>
        {
            if (weakSelf.TryGetTarget(out var self))
            {
                self.SelectNeighborhoodHouse(house);
            }
        };
        StartFrontendAudio();
        StartNeighborhoodAudio(previous);
        if (reopenFamilyPicker || reopenOnCancel)
        {
            OpenFamilyPicker(house: 0, families: previous.Families);
        }
    }

    private void RequestHouseholdAppearanceCatalog(
        string gender,
        string age,
        string skin,
        OriginalHouseholdCreationOverlay creation)
    {
        string key = $"{gender}|{age}|{skin}";
        if (_cachedAppearanceCatalogs.TryGetValue(key, out SimsSDL.Core.HouseholdAppearanceCatalog? cached))
        {
            _queuedAppearanceCatalogRequest = null;
            creation.SetAppearanceCatalog(cached);
            if (creation.AppearancePreviewSelection() is { } selection)
            {
                RequestHouseholdAppearancePreview(
                    selection.Gender,
                    selection.Age,
                    selection.Skin,
                    selection.HeadChoice,
                    selection.BodyChoice,
                    creation);
            }

            return;
        }

        if (Bridge is not { } bridge || !_bridgeConnected)
        {
            _deferredAppearanceCatalogRequest = (gender, age, skin);
            return;
        }

        if (_pendingHouseholdAppearanceRequest is not null)
        {
            _queuedAppearanceCatalogRequest = (gender, age, skin);
            return;
        }

        try
        {
            _pendingAppearanceCatalogKey = key;
            _prewarmingAppearanceCatalog = false;
            _pendingHouseholdAppearanceRequest = bridge.Send(
                "appearanceCatalog",
                new Dictionary<string, object?>
                {
                    ["gender"] = gender,
                    ["age"] = age,
                    ["skin"] = skin,
                });
        }
        catch (Exception error)
        {
            _pendingAppearanceCatalogKey = null;
            creation.Failed($"Appearance choices could not be loaded: {error.Message}");
        }
    }

    private void RequestHouseholdAppearancePreview(
        string gender,
        string age,
        string skin,
        string head,
        string body,
        OriginalHouseholdCreationOverlay creation)
    {
        if (creation.ShowsFamilyPage)
        {
            _queuedAppearancePreviewRequest = null;
            return;
        }

        if (Bridge is not { } bridge || !_bridgeConnected)
        {
            creation.PreviewFailed("The simulation is not connected.");
            return;
        }

        if (_pendingHouseholdPreviewRequest is not null)
        {
            _queuedAppearancePreviewRequest = (gender, age, skin, head, body);
            return;
        }

        try
        {
            _pendingHouseholdPreviewRequest = bridge.Send(
                "appearancePreview",
                new Dictionary<string, object?>
                {
                    ["gender"] = gender,
                    ["age"] = age,
                    ["skin"] = skin,
                    ["headChoice"] = head,
                    ["bodyChoice"] = body,
                });
            _pendingHouseholdPreviewKey = 0;
        }
        catch (Exception error)
        {
            creation.PreviewFailed($"The Sim preview could not be loaded: {error.Message}");
        }
    }

    private void SubmitHouseholdCreation(
        HouseholdCreationRequest request,
        OriginalHouseholdCreationOverlay? creation)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakCreation = creation is null ? null : new WeakReference<OriginalHouseholdCreationOverlay>(creation);
        PresentLocalConfirmation(
            Strings.FamilyDoneConfirmationTitle(),
            Strings.FamilyDoneConfirmationMessage(),
            Strings.YesButton(),
            Strings.NoButton(),
            response: code =>
            {
                if (code != 0
                    || !weakSelf.TryGetTarget(out var self)
                    || weakCreation is null
                    || !weakCreation.TryGetTarget(out var targetCreation))
                {
                    return;
                }

                targetCreation.BeginSubmission();
                if (self.Bridge is not { } bridge)
                {
                    targetCreation.Failed(self.Strings.MoveInErrorMessage());
                    return;
                }

                try
                {
                    if ((self._currentHouse ?? 0) > 0)
                    {
                        _ = bridge.Send("speed", new Dictionary<string, object?> { ["value"] = 0 });
                    }

                    var members = new List<Dictionary<string, object?>>(request.Members.Count);
                    foreach (var member in request.Members)
                    {
                        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = member.Name,
                            ["bio"] = member.Bio,
                            ["code"] = member.Code,
                            ["skin"] = member.Skin,
                            ["personality"] = member.Personality,
                        };
                        if (member.HeadChoice is { } headChoice)
                        {
                            fields["headChoice"] = headChoice;
                        }

                        if (member.BodyChoice is { } bodyChoice)
                        {
                            fields["bodyChoice"] = bodyChoice;
                        }

                        if (member.Zodiac is int zodiac)
                        {
                            fields["zodiac"] = zodiac;
                        }

                        members.Add(fields);
                    }

                    var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["familyName"] = request.FamilyName,
                        ["members"] = members,
                        ["moveIn"] = !self._householdCreationUnhoused,
                    };
                    if (!self._householdCreationUnhoused && self._pendingHouseholdCreation is int lot && lot > 0)
                    {
                        payload["house"] = lot;
                    }

                    if (self.Renderer.SceneViewport(self.SimdCanvas) is { } viewport
                        && self.Renderer.GroundTileAt(viewport.Size / 2f, self.SimdCanvas) is { } center)
                    {
                        payload["centerX"] = center.X;
                        payload["centerY"] = center.Y;
                    }

                    self._pendingHouseholdCreationRequest = bridge.Send("createHousehold", payload);
                }
                catch (Exception error)
                {
                    targetCreation.Failed(error.Message);
                }
            });
    }

    private void ClickHouseholdCreation(OriginalHouseholdCreationOverlay creation, CGPoint point)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakCreation = new WeakReference<OriginalHouseholdCreationOverlay>(creation);
        creation.Click(
            point,
            _canvas,
            Renderer.UIScale,
            submit: request =>
            {
                if (weakSelf.TryGetTarget(out var self) && weakCreation.TryGetTarget(out var target))
                {
                    self.SubmitHouseholdCreation(request, target);
                }
            },
            cancel: () =>
            {
                if (weakSelf.TryGetTarget(out var self))
                {
                    self.ConfirmCancelHouseholdCreation();
                }
            },
            delete: index =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakCreation.TryGetTarget(out _))
                {
                    return;
                }

                self.PresentLocalConfirmation(
                    self.Strings.FamilyDeleteConfirmationTitle(),
                    self.Strings.FamilyDeleteConfirmationMessage(),
                    self.Strings.YesButton(),
                    self.Strings.NoButton(),
                    response: code =>
                    {
                        if (code == 0 && weakCreation.TryGetTarget(out var innerCreation))
                        {
                            innerCreation.DeleteMember(index);
                        }
                    });
            },
            appearanceCatalog: (gender, age, skin) =>
            {
                if (weakSelf.TryGetTarget(out var self) && weakCreation.TryGetTarget(out var target))
                {
                    self.RequestHouseholdAppearanceCatalog(gender, age, skin, target);
                }
            },
            previewAppearance: (gender, age, skin, head, body) =>
            {
                if (weakSelf.TryGetTarget(out var self) && weakCreation.TryGetTarget(out var target))
                {
                    self.RequestHouseholdAppearancePreview(gender, age, skin, head, body, target);
                }
            });
    }

    private void OpenHouseholdCreationOverlay(int lot)
    {
        if (Ui.HouseholdCreation is not null)
        {
            return;
        }

        var creation = new OriginalHouseholdCreationOverlay(Content, Ui.Device);
        var weakSelf = new WeakReference<LiveController>(this);
        creation.PlaySound = name =>
        {
            if (weakSelf.TryGetTarget(out var self))
            {
                self.PlayUISound(name);
            }
        };
        creation.ConfirmUnspent = finish =>
        {
            if (!weakSelf.TryGetTarget(out var self))
            {
                return;
            }

            self.PresentLocalConfirmation(
                self.Strings.Sim(13, fallback: "More Personality!"),
                self.Strings.Sim(14, fallback: "You haven't used all the personality points. Are you really done?"),
                self.Strings.YesButton(),
                self.Strings.NoButton(),
                response: code =>
                {
                    if (code == 0)
                    {
                        finish();
                    }
                });
        };
        creation.ClearPreview = () =>
        {
            if (weakSelf.TryGetTarget(out var self))
            {
                self.Renderer.ClearAppearancePreview(key: 0);
            }
        };
        Ui.HouseholdCreation = creation;
        DismissMenu();
        Log($"create a family on lot {lot}");
    }

    private void ApplyHouseholdState(IReadOnlyDictionary<string, JsonElement> state)
    {
        int? house = state.JsonInt("house");
        if (house is int current)
        {
            _currentHouse = current;
        }

        bool emptyLot = state.JsonArray("sims") is { } sims
            && !sims.Any(static s => s.JsonBool("household") == true);
        if (_pendingHouseholdCreation is int lot && house == lot && emptyLot && Ui.HouseholdCreation is null)
        {
            OpenHouseholdCreationOverlay(lot);
        }

        if (_pendingFamilyMoveIn is { } moveIn
            && moveIn.Request is null
            && house == moveIn.House
            && emptyLot
            && Bridge is { } bridge)
        {
            _ = bridge.Send("speed", new Dictionary<string, object?> { ["value"] = 0 });
            string request = bridge.Send("moveInFamily", new Dictionary<string, object?> { ["family"] = moveIn.Family });
            _pendingFamilyMoveIn = (moveIn.House, moveIn.Family, request);
        }
    }

    private void RespondToNeighborhood(int house)
    {
        if (house != -1 && Ui.Neighborhood?.Mode == 7 && house is >= 90 and <= 92)
        {
            PlayUISound("ui_nhood_click");
            var (title, message) = MagicTownStrings(
                table: 261,
                titleIndex: 0,
                messageIndex: 1,
                fallbackTitle: "Unable To Visit Lot",
                fallbackMessage: "This is a private Creepy Hollow lot. Please select one of the Calamity Forest lots as a destination.");
            PresentLocalFeedback(title, message);
            return;
        }

        if (_activeNeighborhoodDialog is not { } dialog
            || Bridge is not { } bridge
            || _pendingNeighborhoodResponse is not null
            || NeighborhoodTravel.ResponseForDestination(house) is not { } response)
        {
            return;
        }

        try
        {
            PlayUISound("ui_nhood_click");
            StopNeighborhoodAmbientAudio();
            _lotSims.TryGetValue(_selectedSim, out LotSimInfo? selectedTraveller);
            OriginalTravelLoadScreen? plannedScreen = house > 0
                ? OriginalTravelLoadScreenExtensions.Select(
                    sourceHouse: _currentHouse,
                    destination: house,
                    sourceStateClass: _lastHouseStateClass,
                    destinationStateClass: Ui.Neighborhood?.StateClass(house),
                    travellerFameScore: selectedTraveller?.FameScore ?? 0,
                    travellerJobType: selectedTraveller?.JobType ?? 0,
                    lotTransitInfo: _lastLotTransitInfo,
                    isReturnHome: false)
                : null;
            _pendingTravelSelection = house > 0 ? (house, plannedScreen) : null;
            if (UsesNativeEngine && Ui.Neighborhood?.TravelFilterMode is int filter)
            {
                _ = Send("travelFilter", new Dictionary<string, object?> { ["filter"] = filter });
            }

            _pendingNeighborhoodResponse = bridge.Send(
                "dialogResponse",
                new Dictionary<string, object?>
                {
                    ["token"] = dialog.Token,
                    ["code"] = (int)response.Code,
                    ["text"] = response.Text,
                });
            _ = Send("audioEvent", new Dictionary<string, object?> { ["name"] = "bkground_fade" });
            _neighborhoodHouseSelected = null;
        }
        catch (Exception error)
        {
            _pendingTravelSelection = null;
            Log($"Neighborhood selection: {error}");
        }
    }

    private void StartNeighborhoodAudio(OriginalNeighborhoodOverlay neighborhood)
    {
        if (Bridge is null || (_daemonMode && (_currentHouse ?? 0) == 0))
        {
            if (neighborhood.Music is { Length: > 0 } frontendTrack)
            {
                PlayFrontendMusic(frontendTrack);
            }

            if (neighborhood.AmbientSound is { Length: > 0 } frontendAmbient)
            {
                SendFrontendAudio(
                    "audioEvent",
                    new Dictionary<string, object?>
                    {
                        ["name"] = frontendAmbient,
                        ["ownerID"] = -25,
                    });
            }

            return;
        }

        if (_audio is null)
        {
            return;
        }

        if (neighborhood.Music is { Length: > 0 } track)
        {
            _ = Send("audioEvent", new Dictionary<string, object?> { ["name"] = track });
        }

        if (neighborhood.AmbientSound is { Length: > 0 } ambient)
        {
            _ = Send(
                "audioEvent",
                new Dictionary<string, object?>
                {
                    ["name"] = ambient,
                    ["ownerID"] = -25,
                });
        }
    }

    private void StopNeighborhoodAmbientAudio()
    {
        if (Bridge is null)
        {
            SendFrontendAudio("audioStopOwner", new Dictionary<string, object?> { ["ownerID"] = -25 });
            return;
        }

        if (_audio is not null)
        {
            _ = Send("audioStopOwner", new Dictionary<string, object?> { ["ownerID"] = -25 });
        }
    }

    private void LoadTravelNeighborhood(GameDialog dialog, int mode)
    {
        if (Bridge is not { } bridge)
        {
            return;
        }

        try
        {
            string id = bridge.Send("neighborhood", new Dictionary<string, object?> { ["mode"] = mode });
            _pendingNeighborhood = new NeighborhoodRequest(id, dialog, mode);
            DismissMenu();
            Ui.GameDialog = null;
            Ui.Neighborhood = null;
            _neighborhoodHouseSelected = null;
        }
        catch (Exception error)
        {
            _failedNeighborhoodTokens.Add(dialog.Token);
            _activeNeighborhoodDialog = dialog;
            Log($"Neighborhood selection: {error}");
            ShowNeighborhoodError(error);
        }
    }

    private void PresentTravelNeighborhood(NeighborhoodRequest request, string session, string manifest)
    {
        var neighborhood = new OriginalNeighborhoodOverlay(Content, Ui.Device, manifest, session, Ui.Font);
        if (neighborhood.Mode != request.Mode)
        {
            throw new ContentError("Neighborhood export returned the wrong mode");
        }

        neighborhood.EnableTravelFilter();
        if (_localDialogToken is null)
        {
            Ui.GameDialog = null;
        }

        Ui.Neighborhood = neighborhood;
        StartNeighborhoodAudio(neighborhood);
        var weakSelf = new WeakReference<LiveController>(this);
        _neighborhoodHouseSelected = house =>
        {
            if (weakSelf.TryGetTarget(out var self))
            {
                self.RespondToNeighborhood(house);
            }
        };
    }

    private void OpenOutfitPicker(GameDialog dialog)
    {
        DismissMenu();
        Ui.SoundOptions.Dismiss();
        if (Send("outfitChoices") is { } id)
        {
            _pendingOutfitChoices = (id, dialog);
        }
    }

    private void FinishOpeningOutfitPicker(IReadOnlyDictionary<string, JsonElement> evt, GameDialog dialog)
    {
        Dictionary<string, JsonElement>? result = evt.JsonDict("result");
        var names = new List<string>();
        if (result?.JsonArray("choices") is { } choices)
        {
            foreach (var choice in choices)
            {
                if (choice.JsonString("name") is { } name)
                {
                    names.Add(name);
                }
            }
        }

        if (evt.JsonBool("ok") != true || names.Count == 0)
        {
            // Returns a cancellation result when no wardrobe choices are available so the Sim does not
            // remain waiting.
            if (evt.JsonString("error") is { } reason)
            {
                Log($"Outfit choices: {reason}");
            }

            _ = Send(
                "dialogResponse",
                new Dictionary<string, object?>
                {
                    ["token"] = dialog.Token,
                    ["code"] = 0,
                    ["text"] = "-1",
                });
            return;
        }

        try
        {
            Ui.OutfitPicker = new OriginalOutfitPickerOverlay(
                dialog.Token,
                result?.JsonBool("pet") == true,
                names,
                Content,
                Ui.Font,
                Ui.Device,
                Strings);
            PlayUISound("ui_piemenu_appear");
            RequestOutfitPreview();
            if (Options.AnswerDialogs && Ui.OutfitPicker is { } picker)
            {
                HandleOutfitPicker(new OriginalOutfitPickerOverlay.Action.Cancel(), picker);
            }
        }
        catch (Exception error)
        {
            Log($"Outfit picker: {error}");
            _ = Send(
                "dialogResponse",
                new Dictionary<string, object?>
                {
                    ["token"] = dialog.Token,
                    ["code"] = 0,
                    ["text"] = "-1",
                });
        }
    }

    private void RequestOutfitPreview()
    {
        if (Ui.OutfitPicker is not { } picker)
        {
            return;
        }

        _pendingOutfitPreview = Send("outfitPreview", new Dictionary<string, object?> { ["index"] = picker.Index });
    }

    private static string ResolveRealPath(string path) => LotScene.ResolveRealPath(path);

    private void LoadAppearancePreview(IReadOnlyDictionary<string, JsonElement> evt, int key = 0)
    {
        if (evt.JsonBool("ok") != true
            || evt.JsonDict("result") is not { } result
            || !string.Equals(result.JsonString("file"), "cas-preview/preview.json", StringComparison.Ordinal)
            || Bridge is not { } bridge)
        {
            throw new ContentError(evt.JsonString("error") ?? "The preview response is invalid");
        }

        string session = ResolveRealPath(bridge.Session);
        string url = ResolveRealPath(Path.Combine(session, "cas-preview", "preview.json"));
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!url.StartsWith(session + Path.DirectorySeparatorChar, cmp))
        {
            throw new ContentError("The preview file escaped its session directory");
        }

        var loaded = SimsSDL.Core.HouseholdAppearancePreview.Load(url);
        Renderer.UpdateAppearancePreview(loaded.Preview, loaded.Pixels, key);
    }

    private void HandleOutfitPicker(OriginalOutfitPickerOverlay.Action action, OriginalOutfitPickerOverlay picker)
    {
        switch (action)
        {
            case OriginalOutfitPickerOverlay.Action.Previous:
                picker.Move(-1);
                PlayUISound("ui_click");
                RequestOutfitPreview();
                break;

            case OriginalOutfitPickerOverlay.Action.Next:
                picker.Move(1);
                PlayUISound("ui_click");
                RequestOutfitPreview();
                break;

            case OriginalOutfitPickerOverlay.Action.Buy(var index):
                _answeredDialogs.Add(picker.Token);
                PlayUISound("ui_buy_place");
                _ = Send(
                    "dialogResponse",
                    new Dictionary<string, object?>
                    {
                        ["token"] = picker.Token,
                        ["code"] = 1,
                        ["text"] = index.ToString(CultureInfo.InvariantCulture),
                    });
                CloseOutfitPicker();
                break;

            case OriginalOutfitPickerOverlay.Action.Cancel:
                _answeredDialogs.Add(picker.Token);
                PlayUISound("ui_click");
                _ = Send(
                    "dialogResponse",
                    new Dictionary<string, object?>
                    {
                        ["token"] = picker.Token,
                        ["code"] = 0,
                        ["text"] = "-1",
                    });
                CloseOutfitPicker();
                break;
        }
    }

    private void CloseOutfitPicker()
    {
        Ui.OutfitPicker = null;
        _pendingOutfitPreview = null;
        Renderer.ClearAppearancePreview();
    }

    /// <summary>Starts the audio host responsible for playing neighborhood music and ambient sound while no
    /// lot is loaded.</summary>
    private void StartFrontendAudio()
    {
        if (_daemonMode)
        {
            return;
        }

        if (!Options.Audio || _frontendAudio is not null || Bridge is not null)
        {
            return;
        }

        var host = new SimulationBridge(Workspace, Options.DataRoot, Options.Profile, _currentNeighborhood);
        host.DiscardsContentOnExit = true;
        SDLAudioPlayer player;
        try
        {
            player = new SDLAudioPlayer(host.Session);
        }
        catch (Exception error)
        {
            Log($"Neighborhood audio: {error}");
            return;
        }

        var weakSelf = new WeakReference<LiveController>(this);
        var weakHost = new WeakReference<SimulationBridge>(host);
        var weakPlayer = new WeakReference<SDLAudioPlayer>(player);
        player.Ended = voice => RunOnMainThread(() =>
        {
            if (!weakHost.TryGetTarget(out var target))
            {
                return;
            }

            try
            {
                _ = target.Send("audioEnded", new Dictionary<string, object?> { ["voice"] = voice });
            }
            catch
            {
            }
        });
        host.Event = evt => RunOnMainThread(() =>
        {
            if (!weakSelf.TryGetTarget(out var self)
                || !weakHost.TryGetTarget(out var target)
                || !ReferenceEquals(self._frontendAudio, target))
            {
                return;
            }

            switch (evt.JsonString("type"))
            {
                case "hello":
                    try
                    {
                        _ = target.Send("audio", new Dictionary<string, object?> { ["enabled"] = true });
                    }
                    catch
                    {
                    }

                    self._frontendAudioReady = true;
                    List<(string Operation, Dictionary<string, object?> Parameters)> queued = [.. self._pendingFrontendAudio];
                    self._pendingFrontendAudio.Clear();
                    foreach (var request in queued)
                    {
                        try
                        {
                            _ = target.Send(request.Operation, request.Parameters);
                        }
                        catch
                        {
                        }
                    }

                    break;

                case "audio":
                    try
                    {
                        if (weakPlayer.TryGetTarget(out var audioPlayer))
                        {
                            var audioEvent = JsonSerializer.Deserialize<AudioEvent>(
                                JsonSerializer.SerializeToUtf8Bytes(evt),
                                PackContentPaths.JsonReadOptions);
                            if (audioEvent is not null)
                            {
                                audioPlayer.Receive(audioEvent);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        Log($"Neighborhood audio: {error}");
                    }

                    break;

                case "audioError":
                {
                    string audioErr = evt.JsonString("error")
                        ?? (evt.TryGetValue("error", out JsonElement errEl) && errEl.ValueKind != JsonValueKind.Null
                            ? errEl.GetRawText()
                            : "unknown sound error");
                    Log($"Neighborhood audio: {audioErr}");
                    break;
                }
            }
        });
        host.Failed = error => Log($"Neighborhood audio: {error}");
        _frontendAudio = host;
        _frontendAudioPlayer = player;
        _frontendAudioReady = false;
        _ = StartFrontendAudioCoreAsync(host);
    }

    private async Task StartFrontendAudioCoreAsync(SimulationBridge host)
    {
        var weakSelf = new WeakReference<LiveController>(this);
        var weakHost = new WeakReference<SimulationBridge>(host);
        try
        {
            await host.StartFrontendAudioAsync(Content, UsesNativeEngine).ConfigureAwait(false);
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self) || !weakHost.TryGetTarget(out var target))
                {
                    return;
                }

                if (!ReferenceEquals(self._frontendAudio, target))
                {
                    target.Stop(save: false);
                    _ = target.WaitForExit(10);
                }
            });
        }
        catch (Exception error)
        {
            MainThreadDispatcher.Post(() =>
            {
                if (!weakSelf.TryGetTarget(out var self)
                    || !weakHost.TryGetTarget(out var target)
                    || !ReferenceEquals(self._frontendAudio, target))
                {
                    return;
                }

                Log($"Neighborhood audio: {error}");
                self.StopFrontendAudio();
            });
        }
    }

    private void PlayFrontendMusic(string name)
    {
        if (_daemonMode)
        {
            if (Bridge is null || _audio is null || string.Equals(_frontendMusic, name, StringComparison.Ordinal))
            {
                return;
            }

            _frontendMusic = name;
            _ = Send("audioEvent", new Dictionary<string, object?> { ["name"] = name });
            return;
        }

        if (_frontendAudio is null || string.Equals(_frontendMusic, name, StringComparison.Ordinal))
        {
            return;
        }

        _frontendMusic = name;
        SendFrontendAudio("audioEvent", new Dictionary<string, object?> { ["name"] = name });
    }

    private void SendFrontendAudio(string operation, IReadOnlyDictionary<string, object?> parameters)
    {
        if (_daemonMode)
        {
            if (Bridge is not null && _bridgeConnected && _audio is not null)
            {
                _ = Send(operation, parameters);
            }

            return;
        }

        if (_frontendAudio is not { } frontendAudio)
        {
            return;
        }

        if (_frontendAudioReady)
        {
            try
            {
                _ = frontendAudio.Send(operation, parameters);
            }
            catch
            {
            }
        }
        else if (_pendingFrontendAudio.Count < 32)
        {
            _pendingFrontendAudio.Add((operation, new Dictionary<string, object?>(parameters, StringComparer.Ordinal)));
        }
    }

    private void StopFrontendAudio()
    {
        _startupLoadAudio?.Close();
        _startupLoadAudio = null;
        _frontendAudio?.Stop(save: false);
        _frontendAudioPlayer?.Close();
        _frontendAudio = null;
        _frontendAudioPlayer = null;
        _frontendAudioReady = false;
        _pendingFrontendAudio.Clear();
        _frontendMusic = null;
    }

    private bool ClickNeighborhoodScreens(CGPoint point)
    {
        if (Ui.Neighborhood is not null && Ui.Credits is { } credits)
        {
            if (credits.Click(point, _canvas, Renderer.UIScale))
            {
                CloseCredits();
            }

            return true;
        }

        if (Ui.Neighborhood is not null && Ui.FamilyPicker is { } picker)
        {
            if (picker.Click(point, _canvas, Renderer.UIScale) is { } action)
            {
                HandleFamilyPicker(action, picker);
            }

            return true;
        }

        if (Ui.Neighborhood is { } neighborhood)
        {
            if (neighborhood.ClickTravelFilter(point, _canvas, Renderer.UIScale))
            {
                return true;
            }

            if (neighborhood.BannerActionAt(point, _canvas, Renderer.UIScale) is { } action)
            {
                if (neighborhood.IsEnabled(action))
                {
                    ChooseBannerAction(action);
                }
                else
                {
                    PlayUISound("ui_nhood_error");
                }

                return true;
            }

            if (neighborhood.HouseAt(point, _canvas, Renderer.UIScale) is int house)
            {
                _neighborhoodHouseSelected?.Invoke(house);
            }

            return true;
        }

        if (Ui.HouseholdCreation is { } creation)
        {
            ClickHouseholdCreation(creation, point);
            return true;
        }

        return !HasActiveLot;
    }

    private bool? ScreenKey(Key key)
    {
        if (Ui.OutfitPicker is { } outfitPicker)
        {
            switch (key)
            {
                case Key.Left:
                    HandleOutfitPicker(new OriginalOutfitPickerOverlay.Action.Previous(), outfitPicker);
                    break;
                case Key.Right:
                    HandleOutfitPicker(new OriginalOutfitPickerOverlay.Action.Next(), outfitPicker);
                    break;
                case Key.Enter:
                    HandleOutfitPicker(new OriginalOutfitPickerOverlay.Action.Buy(outfitPicker.Index), outfitPicker);
                    break;
                case Key.Escape:
                    HandleOutfitPicker(new OriginalOutfitPickerOverlay.Action.Cancel(), outfitPicker);
                    break;
            }

            return true;
        }

        if (Ui.HouseholdCreation is { } creation)
        {
            var weakSelf = new WeakReference<LiveController>(this);
            var weakCreation = new WeakReference<OriginalHouseholdCreationOverlay>(creation);
            if (key is Key.Enter
                && creation.ShowsFamilyPage
                && creation.SubmitFamilyIfReady(request =>
                {
                    if (weakSelf.TryGetTarget(out var self) && weakCreation.TryGetTarget(out var target))
                    {
                        self.SubmitHouseholdCreation(request, target);
                    }
                }))
            {
                return true;
            }

            string code;
            switch (key)
            {
                case Key.Escape: code = "\u001b"; break;
                case Key.Enter: code = "\r"; break;
                case Key.Backspace: code = "\u007f"; break;
                case Key.Delete: code = "\uf728"; break;
                case Key.Left: code = "\uf702"; break;
                case Key.Right: code = "\uf703"; break;
                case Key.Up: code = "\uf700"; break;
                case Key.Down: code = "\uf701"; break;
                case Key.Tab: code = "\t"; break;
                case Key.PageUp: code = "\uf72c"; break;
                case Key.PageDown: code = "\uf72d"; break;
                case Key.Home: code = "\uf729"; break;
                case Key.End: code = "\uf72b"; break;
                case Key.Character or Key.Function:
                    return true;
                default:
                    return true;
            }

            _ = creation.Key(code, () =>
            {
                if (weakSelf.TryGetTarget(out var self))
                {
                    self.ConfirmCancelHouseholdCreation();
                }
            });
            return true;
        }

        if (Ui.Credits is not null)
        {
            if (key is Key.Escape)
            {
                CloseCredits();
            }

            return true;
        }

        if (Ui.FamilyPicker is { } familyPicker)
        {
            switch (key)
            {
                case Key.Up:
                    familyPicker.MoveSelection(-1);
                    break;
                case Key.Down:
                    familyPicker.MoveSelection(1);
                    break;
                case Key.Enter:
                    if (familyPicker.SelectedFamily is { } family
                        && familyPicker.IsEnabled(OriginalFamilyPickerOverlay.Control.MoveIn))
                    {
                        HandleFamilyPicker(new OriginalFamilyPickerOverlay.Action.MoveIn(family), familyPicker);
                    }

                    break;
                case Key.Escape:
                    HandleFamilyPicker(new OriginalFamilyPickerOverlay.Action.Cancel(), familyPicker);
                    break;
            }

            return true;
        }

        if (_activeNeighborhoodDialog is not null)
        {
            if (key is Key.Escape)
            {
                RespondToNeighborhood(-1);
            }

            return true;
        }

        if (Ui.Neighborhood is not null || !HasActiveLot)
        {
            if (key is Key.Escape && (_rezoneMode || _evictMode))
            {
                _rezoneMode = false;
                _evictMode = false;
                Ui.Neighborhood?.SetRezoneMode(false);
                Ui.Neighborhood?.SetEvictMode(false);
            }

            return true;
        }

        return null;
    }

    private void NeighborhoodResponse(string id, bool ok, IReadOnlyDictionary<string, JsonElement> evt)
    {
        if (string.Equals(_pendingHouseholdAppearanceRequest, id, StringComparison.Ordinal))
        {
            _pendingHouseholdAppearanceRequest = null;
            string? catalogKey = _pendingAppearanceCatalogKey;
            _pendingAppearanceCatalogKey = null;
            bool wasPrewarm = _prewarmingAppearanceCatalog;
            _prewarmingAppearanceCatalog = false;

            SimsSDL.Core.HouseholdAppearanceCatalog? catalog = null;
            if (ok && evt.TryGetValue("result", out JsonElement resultElement) && resultElement.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    catalog = JsonSerializer.Deserialize<SimsSDL.Core.HouseholdAppearanceCatalog>(
                        resultElement.GetRawText(),
                        PackContentPaths.JsonReadOptions);
                }
                catch
                {
                    catalog = null;
                }
            }

            if (catalog is not null)
            {
                if (catalogKey is not null)
                {
                    _cachedAppearanceCatalogs[catalogKey] = catalog;
                }

                if (_queuedAppearanceCatalogRequest is { } queued)
                {
                    _queuedAppearanceCatalogRequest = null;
                    if (Ui.HouseholdCreation is { } creation)
                    {
                        RequestHouseholdAppearanceCatalog(queued.Gender, queued.Age, queued.Skin, creation);
                    }
                }
                else if (!wasPrewarm)
                {
                    Ui.HouseholdCreation?.SetAppearanceCatalog(catalog);
                    if (Ui.HouseholdCreation is { } creation && creation.AppearancePreviewSelection() is { } selection)
                    {
                        RequestHouseholdAppearancePreview(
                            selection.Gender,
                            selection.Age,
                            selection.Skin,
                            selection.HeadChoice,
                            selection.BodyChoice,
                            creation);
                    }
                }
            }
            else if (!wasPrewarm)
            {
                _queuedAppearanceCatalogRequest = null;
                Ui.HouseholdCreation?.Failed(evt.JsonString("error") ?? "Appearance choices could not be loaded.");
            }
            else if (_queuedAppearanceCatalogRequest is { } queued)
            {
                _queuedAppearanceCatalogRequest = null;
                if (Ui.HouseholdCreation is { } creation)
                {
                    RequestHouseholdAppearanceCatalog(queued.Gender, queued.Age, queued.Skin, creation);
                }
            }
        }

        if (string.Equals(_pendingHouseholdPreviewRequest, id, StringComparison.Ordinal))
        {
            _pendingHouseholdPreviewRequest = null;
            int key = _pendingHouseholdPreviewKey;
            if (key == 0)
            {
                if (Ui.HouseholdCreation is { ShowsFamilyPage: false } creation)
                {
                    try
                    {
                        LoadAppearancePreview(evt);
                    }
                    catch (Exception error)
                    {
                        creation.PreviewFailed($"The Sim preview could not be loaded: {error.Message}");
                    }
                }
                else
                {
                    Renderer.ClearAppearancePreview(key: 0);
                }
            }
            else if (Ui.HouseholdCreation?.MemberPreviewSelections() is { } selections
                && selections.TryGetValue(key, out var memberSelection)
                && string.Equals(Signature(memberSelection), _pendingHouseholdPreviewSignature, StringComparison.Ordinal))
            {
                // Failed portrait generation requests are not retried, retaining the slot name without an
                // image.
                _familyPortraits[key] = _pendingHouseholdPreviewSignature;
                try
                {
                    LoadAppearancePreview(evt, key);
                }
                catch (Exception error)
                {
                    Log($"Family member preview: {error}");
                }
            }

            if (_queuedAppearancePreviewRequest is { } queuedPreview && Ui.HouseholdCreation is { } activeCreation)
            {
                _queuedAppearancePreviewRequest = null;
                if (!activeCreation.ShowsFamilyPage)
                {
                    RequestHouseholdAppearancePreview(
                        queuedPreview.Gender,
                        queuedPreview.Age,
                        queuedPreview.Skin,
                        queuedPreview.Head,
                        queuedPreview.Body,
                        activeCreation);
                }
            }
            else
            {
                _queuedAppearancePreviewRequest = null;
            }
        }

        if (string.Equals(_pendingHouseholdCreationRequest, id, StringComparison.Ordinal))
        {
            _pendingHouseholdCreationRequest = null;
            if (ok && _householdCreationUnhoused)
            {
                // Leaves the temporary creation lot unsaved and reloads the neighborhood map so the family
                // bin displays the newly created family.
                bool reopenPicker = _returnToFamilyPickerAfterHouseholdCreation;
                OriginalNeighborhoodOverlay? previous = _neighborhoodBeforeHouseholdCreation;
                int targetMode = previous?.Mode ?? NeighborhoodModeForCurrentLot();
                _householdCreationUnhoused = false;
                _returnToFamilyPickerAfterHouseholdCreation = false;
                _neighborhoodBeforeHouseholdCreation = null;
                if (previous is not null)
                {
                    Ui.Neighborhood = previous;
                }

                Log("household created in the family bin");
                CancelHouseholdCreation(reloadMode: targetMode, reopenFamilyPicker: reopenPicker);
            }
            else if (ok)
            {
                int? targetLot = _pendingHouseholdCreation ?? _currentHouse;
                if (evt.JsonDict("result")?.JsonString("memberInstantiation") is { } memberInstantiation)
                {
                    Log($"household member instance: {memberInstantiation}");
                }

                Ui.HouseholdCreation = null;
                Renderer.ClearAppearancePreview();
                _familyPortraits.Clear();
                _pendingHouseholdPreviewRequest = null;
                _pendingHouseholdCreation = null;
                _returnToFamilyPickerAfterHouseholdCreation = false;
                _neighborhoodBeforeHouseholdCreation = null;
                if (_daemonMode && targetLot is int lot && lot > 0 && (_currentHouse ?? 0) == 0)
                {
                    if (lot == 7)
                    {
                        Renderer.SetZoom(LotZoom.Far, instant: true);
                    }

                    if (Bridge is { } activeBridge)
                    {
                        Log($"live session {activeBridge.Session}");
                    }

                    _currentHouse = lot;
                    _frontendMusic = null;
                    Ui.PanelMinimized = false;
                    _simlessBuildMode = false;
                    AwayBuyBuildLocked = false;
                    _openingNeighborhoodLot = false;
                    _hasCompletedInitialLoad = true;
                    Renderer.LiveMode = true;
                    Renderer.DrawsLot = true;
                    _ = Send("audio", new Dictionary<string, object?> { ["enabled"] = _audio is not null });
                    _ = Send("stream", new Dictionary<string, object?> { ["enabled"] = true });
                    ApplyPlayOptions();
                }

                HomeLotHouse = _currentHouse;
                Log("household moved in");
                _ = Send("modeMusic", new Dictionary<string, object?> { ["mode"] = "live" });
                RequestAutoCenterOnSelection();
                if (Options.Speed is int speed)
                {
                    _liveModePaused = speed == 0;
                    if (speed > 0)
                    {
                        _lastRunningSpeed = speed;
                    }

                    _ = Send("speed", new Dictionary<string, object?> { ["value"] = speed });
                }
            }
            else
            {
                if (evt.JsonString("error") is { } reason)
                {
                    Log($"Household creation: {reason}");
                }

                Ui.HouseholdCreation?.Failed(Strings.MoveInErrorMessage());
            }
        }

        if (_pendingNeighborhood is { } pending && string.Equals(pending.Id, id, StringComparison.Ordinal))
        {
            _pendingNeighborhood = null;
            _activeNeighborhoodDialog = pending.Dialog;
            try
            {
                if (!ok || Bridge is not { } bridge)
                {
                    throw new ContentError(evt.JsonString("error") ?? "The neighborhood could not be loaded.");
                }

                PresentTravelNeighborhood(
                    pending,
                    session: bridge.Session,
                    manifest: Path.Combine(bridge.Session, "neighborhood.json"));
            }
            catch (Exception error)
            {
                _failedNeighborhoodTokens.Add(pending.Dialog.Token);
                Ui.Neighborhood = null;
                _neighborhoodHouseSelected = null;
                ShowNeighborhoodError(error);
            }
        }

        if (string.Equals(_pendingNeighborhoodResponse, id, StringComparison.Ordinal))
        {
            _pendingNeighborhoodResponse = null;
            var selection = _pendingTravelSelection;
            _pendingTravelSelection = null;
            if (ok)
            {
                _activeNeighborhoodDialog = null;
                Ui.Neighborhood = null;
                _neighborhoodHouseSelected = null;
                if (selection?.Screen is { } screen)
                {
                    Ui.TravelLoadScreen = screen;
                    Ui.Loading = true;
                    _switchingLot = true;
                    _awaitingLotSwitchChange = true;
                }
            }
            else
            {
                ShowNeighborhoodError(new ContentError(evt.JsonString("error") ?? "The travel selection was rejected."));
                if (Ui.Neighborhood is { } neighborhood)
                {
                    StartNeighborhoodAudio(neighborhood);
                }

                var weakSelf = new WeakReference<LiveController>(this);
                _neighborhoodHouseSelected = house =>
                {
                    if (weakSelf.TryGetTarget(out var self))
                    {
                        self.RespondToNeighborhood(house);
                    }
                };
            }
        }

        if (_pendingOutfitChoices is { } pendingOutfit && string.Equals(pendingOutfit.Id, id, StringComparison.Ordinal))
        {
            _pendingOutfitChoices = null;
            FinishOpeningOutfitPicker(evt, pendingOutfit.Dialog);
        }
        else if (string.Equals(_pendingOutfitPreview, id, StringComparison.Ordinal))
        {
            _pendingOutfitPreview = null;
            if (Ui.OutfitPicker is not null)
            {
                try
                {
                    LoadAppearancePreview(evt);
                }
                catch (Exception error)
                {
                    Renderer.ClearAppearancePreview();
                    Log($"Outfit preview: {error}");
                }
            }
        }

        if (_pendingFamilyMoveIn is { } moveIn && string.Equals(moveIn.Request, id, StringComparison.Ordinal))
        {
            _pendingFamilyMoveIn = null;
            if (ok)
            {
                _neighborhoodBeforeHouseholdCreation = null;
                HomeLotHouse = moveIn.House;
                Log("family moved in");
                _ = Send("modeMusic", new Dictionary<string, object?> { ["mode"] = "live" });
                RequestAutoCenterOnSelection();
                if (Options.Speed is int speed)
                {
                    _liveModePaused = speed == 0;
                    if (speed > 0)
                    {
                        _lastRunningSpeed = speed;
                    }

                    _ = Send("speed", new Dictionary<string, object?> { ["value"] = speed });
                }
            }
            else
            {
                if (evt.JsonString("error") is { } reason)
                {
                    Log($"Move in: {reason}");
                }

                CancelHouseholdCreation();
                PresentLocalFeedback(Strings.MoveInErrorTitle(), Strings.MoveInErrorMessage());
            }
        }
    }

    /// <summary>Initializes Special Effect events against baseline simulation state so that pre-existing
    /// events are not replayed on connect.</summary>
    private void ApplyPictureInPicture(IReadOnlyDictionary<string, JsonElement> state)
    {
        List<Dictionary<string, JsonElement>> events = state.JsonArray("pictureInPicture") ?? [];
        if (_lastPictureInPictureSequence is not long last)
        {
            long maxSeq = 0;
            foreach (var evt in events)
            {
                if (evt.JsonInt64("sequence") is long seq && seq > maxSeq)
                {
                    maxSeq = seq;
                }
            }

            _lastPictureInPictureSequence = maxSeq;
            return;
        }

        foreach (var evt in events)
        {
            if (evt.JsonInt64("sequence") is not long sequence || sequence <= last)
            {
                continue;
            }

            _lastPictureInPictureSequence = sequence;
            HandlePictureInPicture(evt);
        }
    }

    private (float X, float Y, int Level, bool IsSim)? TargetPosition(int id)
    {
        if (_lotSims.TryGetValue(id, out LotSimInfo? sim))
        {
            return (sim.X, sim.Y, sim.Level, true);
        }

        if (_placedObjectMeta.TryGetValue(id, out PlacedObjectMeta meta))
        {
            return (meta.X, meta.Y, meta.Level, false);
        }

        return null;
    }

    /// <summary>Processes picture-in-picture events by centering on the target object or opening the
    /// picture-in-picture window, and triggers auto-snapshots if enabled.</summary>
    private void HandlePictureInPicture(IReadOnlyDictionary<string, JsonElement> evt)
    {
        int objectID = evt.JsonInt("objectId") ?? 0;
        var playOptions = Ui.OptionsPanel.PlayOptions;
        int sizeIndex = evt.JsonInt("size") ?? evt.JsonInt("byte2") ?? 0;
        int zoom = evt.JsonInt("zoom") ?? evt.JsonInt("byte3") ?? 0;
        string? text = evt.JsonString("text") is { Length: > 0 } rawText ? rawText : null;
        bool insideLotEdge = evt.JsonBool("insideLotEdge") ?? true;
        bool snapshotRequested = evt.JsonBool("snapshot") == true && playOptions.AutoSnapshot && insideLotEdge;
        if (evt.JsonBool("force") == true && objectID != 0)
        {
            bool snapshot = snapshotRequested;
            if (playOptions.AutoCenter)
            {
                CentreView(objectID);
            }
            else
            {
                PlayUISound("ui_error");
                snapshot = false;
            }

            if (snapshot && !Ui.Camera.Visible && PendingPhoto is null && EventRect(objectID) is { } rect)
            {
                PendingPhoto = rect;
                _pendingPhotoIsAutomatic = true;
                _pendingPhotoCaption = text;
            }
        }
        else if (evt.JsonBool("open") == true && objectID != 0)
        {
            OpenPictureInPicture(
                objectID,
                milliseconds: evt.JsonInt("durationMs") ?? 0,
                zoom: zoom,
                sizeIndex: sizeIndex,
                checkOffScreen: evt.JsonBool("checkOffScreen") ?? true,
                insideLotEdge: insideLotEdge,
                snapshot: snapshotRequested,
                text: text);
        }
        else if (objectID == 0 || _pictureInPicture?.ObjectID == objectID)
        {
            ClosePictureInPicture();
        }
    }

    /// <summary>Displays off-screen events in the picture-in-picture window positioned at the bottom-right
    /// above the control panel, remaining visible indefinitely if duration is zero.</summary>
    private void OpenPictureInPicture(
        int objectID,
        int milliseconds,
        int zoom,
        int sizeIndex,
        bool checkOffScreen,
        bool insideLotEdge,
        bool snapshot,
        string? text)
    {
        if (_pictureInPicture is { } open && open.ObjectID != objectID)
        {
            return;
        }

        if (!insideLotEdge)
        {
            return;
        }

        if (checkOffScreen
            && (TargetPosition(objectID)?.Level ?? EffectiveViewLevel) == EffectiveViewLevel
            && Renderer.ScreenBounds(objectID, SimdCanvas) is { } bounds
            && Renderer.SceneViewport(SimdCanvas) is { } viewport)
        {
            double panelTop = Math.Max(0.0, viewport.Size.Y - (Ui.PanelMinimized ? 0.0 : 100.0 * Scale));
            if (viewport.Size.X > 0.0
                && panelTop > 0.0
                && !bounds.IsEmpty
                && bounds.MinX >= 0.0
                && bounds.MinY >= 0.0
                && bounds.MaxX <= viewport.Size.X
                && bounds.MaxY <= panelTop)
            {
                return;
            }
        }

        Ui.PictureInPictureLogicalSize = LiveUI.PictureInPictureSize(sizeIndex);
        Ui.PictureInPictureCaption = text;
        _pictureInPicture = new PictureInPictureWindow
        {
            ObjectID = objectID,
            Expires = milliseconds > 0 ? DateTime.UtcNow.AddSeconds(milliseconds / 1000.0) : null,
            SizeIndex = sizeIndex,
            Zoom = zoom,
            Text = text,
            SnapshotPending = snapshot,
            Built = false,
        };
    }

    private void ClosePictureInPicture()
    {
        _pictureInPicture = null;
        _pipNeedsCapture = false;
        _pipCloseTracking = false;
        Ui.PictureInPicture = null;
        Ui.PictureInPictureCaption = null;
        Ui.PictureInPictureCloseHovered = false;
        Ui.PictureInPictureClosePressed = false;
    }

    /// <summary>Updates the capture state for picture-in-picture, rebuilding every frame when Live PIP is
    /// active or capturing only an initial static frame when disabled.</summary>
    private void UpdatePictureInPictureCaptureState()
    {
        _pipNeedsCapture = false;
        if (_pictureInPicture is not { } window)
        {
            return;
        }

        if (window.Expires is { } expires && DateTime.UtcNow >= expires)
        {
            ClosePictureInPicture();
            return;
        }

        bool objectExists = _placedObjectMeta.ContainsKey(window.ObjectID)
            || _lotSims.ContainsKey(window.ObjectID)
            || Renderer.ContainsObject(window.ObjectID);
        if (!objectExists)
        {
            ClosePictureInPicture();
            return;
        }

        if (Renderer.Scene is null)
        {
            return;
        }

        if (!Ui.OptionsPanel.PlayOptions.LivePIP && window.Built)
        {
            return;
        }

        _pipNeedsCapture = true;
    }

    /// <summary>Renders an off-screen view centered on the target object at requested zoom levels into the
    /// picture-in-picture frame, capturing an initial photo album snapshot if enabled.</summary>
    private void CapturePictureInPictureFrame()
    {
        if (_pictureInPicture is not { } window || Renderer.Scene is null)
        {
            return;
        }

        CGSize logicalSize = LiveUI.PictureInPictureSize(window.SizeIndex);
        int pipW = Math.Max(1, (int)Math.Round(logicalSize.Width * Scale, MidpointRounding.AwayFromZero));
        int pipH = Math.Max(1, (int)Math.Round(logicalSize.Height * Scale, MidpointRounding.AwayFromZero));
        var pipCanvas = new Vector2(pipW, pipH);
        LotZoom targetZoom = Enum.IsDefined(typeof(LotZoom), window.Zoom)
            ? (LotZoom)window.Zoom
            : Renderer.Zoom;

        (LotZoom Zoom, float VisualFactor) savedZoomState = Renderer.SaveVisualZoom();
        Vector2 savedPan = Renderer.Pan;
        bool savedLiveMode = Renderer.LiveMode;
        bool savedSuppressInset = Renderer.SuppressControlPanelInset;
        List<UIDraw> savedOverlay = Renderer.Overlay;
        (int Index, Dictionary<int, Vector4> Rects)? savedPreviewPlacement = Renderer.PreviewPlacement;
        bool savedShowsPlacement = Renderer.ShowsPlacement;
        bool savedShowsGhost = Renderer.ShowsGhost;
        bool savedShowsBuildCursor = Renderer.ShowsBuildCursor;
        bool savedDrawsLot = Renderer.DrawsLot;
        int panBefore = Renderer.PanAssignments;

        byte[]? renderedPixels;
        try
        {
            Renderer.SnapVisualZoom(targetZoom);
            Renderer.Pan = Vector2.Zero;
            Renderer.LiveMode = savedLiveMode;
            Renderer.SuppressControlPanelInset = true;
            Renderer.Overlay = [];
            Renderer.PreviewPlacement = null;
            Renderer.ShowsPlacement = false;
            Renderer.ShowsGhost = false;
            Renderer.ShowsBuildCursor = false;
            Renderer.DrawsLot = true;

            if (Renderer.ScreenBounds(window.ObjectID, pipCanvas) is { } bounds)
            {
                Renderer.Pan += pipCanvas / 2f - new Vector2((float)bounds.MidX, (float)bounds.MidY);
            }
            else if (TargetPosition(window.ObjectID) is { } pos
                && Renderer.ScreenPoint(pos.X, pos.Y, pipCanvas) is { } point)
            {
                // Lifts the target tile position by half a wall height (8 * sqrt(6) pixels scaled by zoom
                // and interface scale) to center objects in picture-in-picture.
                float lift = 8f * MathF.Sqrt(6f) * targetZoom.Factor() * Renderer.UIScale;
                Renderer.Pan += pipCanvas / 2f - (point - new Vector2(0f, lift));
            }

            if (_pipRenderTargets?.Color.Width != pipW || _pipRenderTargets?.Color.Height != pipH)
            {
                _pipRenderTargets?.Dispose();
                _pipRenderTargets = new RenderTargets(Renderer.GPU, pipW, pipH);
            }

            RenderTargets? targets = _pipRenderTargets;
            nint command = targets is not null ? SDL_AcquireGPUCommandBuffer(Renderer.GPU.Device) : nint.Zero;
            if (targets is null || command == nint.Zero)
            {
                return;
            }

            Renderer.Encode(command, targets.Color, targets.Depth);
            if (!SDL_SubmitGPUCommandBuffer(command))
            {
                return;
            }

            renderedPixels = Renderer.GPU.ReadPixels(targets.Color);
        }
        catch (Exception error)
        {
            Log($"Picture-in-picture: {error}");
            return;
        }
        finally
        {
            Renderer.RestoreVisualZoom(savedZoomState);
            Renderer.Pan = savedPan;
            Renderer.LiveMode = savedLiveMode;
            Renderer.SuppressControlPanelInset = savedSuppressInset;
            Renderer.Overlay = savedOverlay;
            Renderer.PreviewPlacement = savedPreviewPlacement;
            Renderer.ShowsPlacement = savedShowsPlacement;
            Renderer.ShowsGhost = savedShowsGhost;
            Renderer.ShowsBuildCursor = savedShowsBuildCursor;
            Renderer.DrawsLot = savedDrawsLot;
            unchecked
            {
                _pipPanAssignmentsBias += Renderer.PanAssignments - panBefore;
            }
        }

        if (renderedPixels is not { } pipPixels)
        {
            return;
        }

        Ui.PictureInPictureLogicalSize = logicalSize;
        Ui.PictureInPictureCaption = window.Text;
        Ui.PictureInPicture = new OriginalGameplayOverlay.Image(
            new UITexture(pipW, pipH, pipPixels),
            new CGSize(pipW, pipH));
        if (_pictureInPicture is not null)
        {
            _pictureInPicture.Built = true;
        }

        if (window.SnapshotPending)
        {
            if (_pictureInPicture is not null)
            {
                _pictureInPicture.SnapshotPending = false;
            }

            try
            {
                int snapW = Math.Max(1, (int)Math.Round(logicalSize.Width, MidpointRounding.AwayFromZero));
                int snapH = Math.Max(1, (int)Math.Round(logicalSize.Height, MidpointRounding.AwayFromZero));
                byte[] snapPixels = ScaleRGBA(pipPixels, pipW, pipH, snapW, snapH);
                var album = new PhotoAlbum(Path.Combine(NeighborhoodUserData, "PhotoAlbum"));
                float[] qualities = [0.2f, 0.5f, 0.9f];
                float quality = qualities[Math.Clamp(Ui.Camera.Quality, 0, qualities.Length - 1)];
                _ = album.Capture(
                    snapPixels,
                    snapW,
                    snapH,
                    quality,
                    window.Text ?? Strings.AlbumDescription());
            }
            catch (Exception error)
            {
                Log($"Picture-in-picture snapshot: {error}");
            }
        }
    }

    /// <summary>Calculates the picture-in-picture capture area for forced events, spanning 200 by 300 base
    /// pixels at near zoom (halved per zoom step) centered on the target object.</summary>
    private CGRect? EventRect(int id)
    {
        if (Renderer.ScreenBounds(id, SimdCanvas) is not { } bounds)
        {
            return null;
        }

        double divisor = 1 << (3 - (int)Renderer.Zoom);
        var size = new CGSize(200.0 / divisor * Scale, 300.0 / divisor * Scale);
        double left = Math.Max(0.0, bounds.MidX - size.Width / 2.0);
        double top = Math.Max(0.0, bounds.MidY - size.Height / 2.0);
        double right = Math.Min(_canvas.Width, bounds.MidX + size.Width / 2.0);
        double bottom = Math.Min(_canvas.Height, bounds.MidY + size.Height / 2.0);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        double minX = Math.Floor(left);
        double minY = Math.Floor(top);
        double maxX = Math.Ceiling(right);
        double maxY = Math.Ceiling(bottom);
        return new CGRect(minX, minY, maxX - minX, maxY - minY);
    }

    private void CentreView(int id)
    {
        if (TargetPosition(id) is { } pos
            && pos.Level != EffectiveViewLevel
            && pos.Level >= 1
            && pos.Level <= Renderer.StoryCount)
        {
            _pendingPipCenterObjectID = id;
            _pendingPipCenterChangesLevel = true;
            _pendingViewLevel = pos.Level;
            _pendingRoofsVisible = false;
            _pendingViewSceneRevision = null;
            _pendingViewRequestID = Send(
                "level",
                new Dictionary<string, object?> { ["value"] = pos.Level, ["roofs"] = false });
            return;
        }

        _pendingPipCenterObjectID = null;
        _pendingPipCenterChangesLevel = false;
        if (Renderer.ScreenBounds(id, SimdCanvas) is { } bounds
            && Renderer.SceneViewport(SimdCanvas) is { } viewport)
        {
            Renderer.Pan += viewport.Size / 2f - new Vector2((float)bounds.MidX, (float)bounds.MidY);
        }
        else if (TargetPosition(id) is { } targetPos
            && Renderer.SceneViewport(SimdCanvas) is { } fallbackViewport
            && Renderer.ScreenPoint(targetPos.X, targetPos.Y, SimdCanvas) is { } point)
        {
            float lift = targetPos.IsSim ? 8f * MathF.Sqrt(6f) * Renderer.Zoom.Factor() * Renderer.UIScale : 0f;
            Renderer.Pan += fallbackViewport.Size / 2f - (point - new Vector2(0f, lift));
        }

        if (EffectiveRoofsVisible)
        {
            _pendingRoofsVisible = false;
            _pendingViewSceneRevision = null;
            _pendingViewRequestID = Send("roofs", new Dictionary<string, object?> { ["visible"] = false });
        }
    }

    private void CentrePipObjectIfPending()
    {
        if (_pendingPipCenterObjectID is not int id)
        {
            return;
        }

        if (TargetPosition(id) is { } pos)
        {
            if (pos.Level != EffectiveViewLevel && pos.Level >= 1 && pos.Level <= Renderer.StoryCount)
            {
                _pendingViewLevel = pos.Level;
                _pendingRoofsVisible = false;
                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send(
                    "level",
                    new Dictionary<string, object?> { ["value"] = pos.Level, ["roofs"] = false });
                return;
            }

            if (pos.Level != Renderer.ViewLevel)
            {
                return;
            }
        }

        if (Renderer.ScreenBounds(id, SimdCanvas) is { } bounds
            && Renderer.SceneViewport(SimdCanvas) is { } viewport)
        {
            _pendingPipCenterObjectID = null;
            _pendingPipCenterChangesLevel = false;
            Renderer.Pan += viewport.Size / 2f - new Vector2((float)bounds.MidX, (float)bounds.MidY);
            if (EffectiveRoofsVisible)
            {
                _pendingRoofsVisible = false;
                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send("roofs", new Dictionary<string, object?> { ["visible"] = false });
            }
        }
        else if (TargetPosition(id) is { } fallbackPos
            && _pendingPipCenterChangesLevel
            && Renderer.CentreGroundTile((int)fallbackPos.X, (int)fallbackPos.Y, SimdCanvas) is not null)
        {
            _pendingPipCenterObjectID = null;
            _pendingPipCenterChangesLevel = false;
            if (EffectiveRoofsVisible)
            {
                _pendingRoofsVisible = false;
                _pendingViewSceneRevision = null;
                _pendingViewRequestID = Send("roofs", new Dictionary<string, object?> { ["visible"] = false });
            }
        }
    }
}
