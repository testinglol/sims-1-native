using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using SimsSDL.Core;

namespace SimsSDL.UI;

/// <summary>
/// Clean, end-user Neighborhood Mods &amp; Custom Content screen modeled on The Sims 1's Family Selection
/// screen and built on SimsVectorUiKit (raised cobalt frame, sunken [0,0,59] wells, framed Pack Preview
/// Photo, slanted WinBtn buttons, FamilyPickItemBkg list rows, OptCheckBox toggles, and domed ScrollBar).
/// </summary>
public sealed class OriginalModsOverlay
{
    public abstract record Action
    {
        private protected Action() { }

        public sealed record Cancel : Action;
        public sealed record ApplyAndReload(ModManager.ModSettingsFile Settings, bool HasChanges) : Action;
        public sealed record OpenModsFolder : Action;
    }

    private enum FilterTab
    {
        All = 0,
        Packs = 1,
        Downloads = 2,
    }

    private const int DialogW = 684;
    private const int DialogH = 456;
    private const int FramePad = 8;

    private const double TabsY = 48;
    private const double ListX = 20;
    private const double ListY = 80;
    private const int ListW = 382;
    private const int ListH = 314;
    private const double RowPitch = 33;
    private const int RowItemH = 30;
    private const int MaxVisibleRows = 9;

    private const int ScrollBarW = 20;
    private const int ScrollArrowH = 22;

    private const double DetailX = 414;
    private const double DetailY = 48;
    private const int DetailW = 250;
    private const int DetailH = 346;

    private const int PhotoFrameW = 222;
    private const int PhotoFrameH = 132;
    private const int PhotoInnerPad = 4;

    private readonly UIDevice _device;
    private readonly string _gameRoot;
    private readonly string _workspace;
    private readonly string _dataRoot;
    private readonly string? _cliPacks;

    private readonly SimsBitmapFont _titleFont;
    private readonly SimsBitmapFont _font;
    private readonly SimsBitmapFont _smallFont;

    private readonly OriginalGameplayOverlay.Image _dimBackdrop;
    private readonly OriginalGameplayOverlay.Image _dropLineFill;

    private OriginalGameplayOverlay.Image _panelFrame;
    private OriginalGameplayOverlay.Image _listWell;
    private OriginalGameplayOverlay.Image _detailWell;
    private OriginalGameplayOverlay.Image _photoFrameWell;
    private OriginalGameplayOverlay.Image _defaultPhotoPlaceholder;
    private OriginalGameplayOverlay.Image _checkboxStrip;
    private OriginalGameplayOverlay.Image _scrollTrackImg;
    private OriginalGameplayOverlay.Image _scrollUpNormal;
    private OriginalGameplayOverlay.Image _scrollUpHover;
    private OriginalGameplayOverlay.Image _scrollDownNormal;
    private OriginalGameplayOverlay.Image _scrollDownHover;

    private readonly Dictionary<int, OriginalGameplayOverlay.Image> _rowStripCache = new();
    private readonly Dictionary<(int H, bool Hover), OriginalGameplayOverlay.Image> _thumbCache = new();
    private readonly Dictionary<(int W, int H, bool Highlighted), OriginalGameplayOverlay.Image> _slantedBtnCache = new();
    private readonly Dictionary<(string Text, byte R, byte G, byte B, int Tier), OriginalGameplayOverlay.Image> _textCache = new();
    private readonly Dictionary<string, OriginalGameplayOverlay.Image?> _previewPhotoCache = new(StringComparer.OrdinalIgnoreCase);

    private List<ModManager.DiscoveredMod> _orderedMods = [];
    private HashSet<string> _enabledKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _initialEnabledKeys = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _initialOrderKeys = [];
    private ModManager.EvaluationReport _report;

    private FilterTab _activeTab = FilterTab.All;
    private string? _selectedKey;
    private int _scrollOffset;
    private string? _hoveredId;
    private string? _pressedButtonId;

    private int? _draggingPackGlobalIdx;
    private double _dragStartLocalY;
    private bool _dragMoved;
    private int? _dragDropTargetPackIdx;
    private bool _draggingScrollbar;
    private double _scrollbarGrabOffsetY;
    private string? _dropNotice;
    private bool _dropNoticeIsError;
    private bool _importedFilesChanged;

    public string? SafeModeNotice { get; }

    public bool EditingOrderNumber => false;

    public OriginalModsOverlay(
        GameContent content,
        UIDevice device,
        string gameRoot,
        string workspace,
        string dataRoot,
        string? cliPacks,
        string? safeModeNotice = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _device = device;
        _gameRoot = gameRoot;
        _workspace = workspace;
        _dataRoot = dataRoot;
        _cliPacks = cliPacks;
        SafeModeNotice = safeModeNotice;

        _smallFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_09.ffn")
            ?? new SimsBitmapFont(content.Read("UIGraphics/Fonts/variablesans_10.ffn"));
        _font = TryLoadFont(content, "UIGraphics/Fonts/variablesans_10.ffn") ?? _smallFont;
        _titleFont = TryLoadFont(content, "UIGraphics/Fonts/variablesans_16.ffn")
            ?? TryLoadFont(content, "UIGraphics/Fonts/variablesans_14.ffn")
            ?? _font;

        _dimBackdrop = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 3, 24, 152]);
        _dropLineFill = OriginalGameplayOverlay.CreateImage(device, 1, 1, [0, 250, 255, 255]);

        RebuildVectorChrome();
        ReloadDiscovery(preserveStagedChanges: false);
        _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
    }

    public bool HasStagedChanges =>
        _importedFilesChanged
        || !_enabledKeys.SetEquals(_initialEnabledKeys)
        || !_orderedMods.Select(static m => m.Key).SequenceEqual(_initialOrderKeys, StringComparer.OrdinalIgnoreCase);

    public ModManager.EvaluationReport Report => _report;

    public string? DropNotice => _dropNotice;

    public void HandleDroppedFile(string filePath)
    {
        var result = ModManager.ImportDroppedFile(_gameRoot, _dataRoot, filePath);
        _dropNotice = result.Summary;
        _dropNoticeIsError = !result.Succeeded || result.HasGuidCollision;

        if (!result.Succeeded)
            return;

        _importedFilesChanged = true;
        ReloadDiscovery(preserveStagedChanges: true);
        if (!string.IsNullOrWhiteSpace(result.InstalledModKey))
        {
            _selectedKey = result.InstalledModKey;
            _enabledKeys.Add(result.InstalledModKey);
            _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
        }
    }

    private void RebuildVectorChrome()
    {
        int s = SimsVectorUiKit.CurrentScale();

        var frame = SimsVectorUiKit.RenderPanelFrame(DialogW, DialogH, s);
        _panelFrame = OriginalGameplayOverlay.CreateImage(_device, frame.PixelWidth, frame.PixelHeight, frame.Rgba, frame.LogicalSize);

        var lWell = SimsVectorUiKit.RenderRecessedWell(ListW, ListH, cornerR: 7.5f, scale: s);
        _listWell = OriginalGameplayOverlay.CreateImage(_device, lWell.PixelWidth, lWell.PixelHeight, lWell.Rgba, lWell.LogicalSize);

        var dWell = SimsVectorUiKit.RenderRecessedWell(DetailW, DetailH, cornerR: 7.5f, scale: s);
        _detailWell = OriginalGameplayOverlay.CreateImage(_device, dWell.PixelWidth, dWell.PixelHeight, dWell.Rgba, dWell.LogicalSize);

        var pWell = SimsVectorUiKit.RenderRecessedWell(PhotoFrameW, PhotoFrameH, cornerR: 6f, scale: s);
        _photoFrameWell = OriginalGameplayOverlay.CreateImage(_device, pWell.PixelWidth, pWell.PixelHeight, pWell.Rgba, pWell.LogicalSize);

        _defaultPhotoPlaceholder = BuildDefaultPhotoPlaceholder(PhotoFrameW - PhotoInnerPad * 2, PhotoFrameH - PhotoInnerPad * 2, s);

        var chk = SimsVectorUiKit.RenderCheckboxStrip(20, s);
        _checkboxStrip = OriginalGameplayOverlay.CreateImage(_device, chk.PixelWidth, chk.PixelHeight, chk.Rgba, chk.LogicalSize);

        var trk = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, 12, kind: 3, hovered: false, scale: s);
        _scrollTrackImg = OriginalGameplayOverlay.CreateImage(_device, trk.PixelWidth, trk.PixelHeight, trk.Rgba, trk.LogicalSize);

        var upN = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, ScrollArrowH, kind: 1, hovered: false, scale: s);
        _scrollUpNormal = OriginalGameplayOverlay.CreateImage(_device, upN.PixelWidth, upN.PixelHeight, upN.Rgba, upN.LogicalSize);
        var upH = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, ScrollArrowH, kind: 1, hovered: true, scale: s);
        _scrollUpHover = OriginalGameplayOverlay.CreateImage(_device, upH.PixelWidth, upH.PixelHeight, upH.Rgba, upH.LogicalSize);

        var dnN = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, ScrollArrowH, kind: 2, hovered: false, scale: s);
        _scrollDownNormal = OriginalGameplayOverlay.CreateImage(_device, dnN.PixelWidth, dnN.PixelHeight, dnN.Rgba, dnN.LogicalSize);
        var dnH = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, ScrollArrowH, kind: 2, hovered: true, scale: s);
        _scrollDownHover = OriginalGameplayOverlay.CreateImage(_device, dnH.PixelWidth, dnH.PixelHeight, dnH.Rgba, dnH.LogicalSize);

        _rowStripCache.Clear();
        _thumbCache.Clear();
        _slantedBtnCache.Clear();
        _previewPhotoCache.Clear();
    }

    public void ClearTextCaches()
    {
        _textCache.Clear();
        RebuildVectorChrome();
    }

    public void ReloadDiscovery(bool preserveStagedChanges)
    {
        _previewPhotoCache.Clear();
        var saved = ModManager.LoadSettings(_dataRoot);
        var discovered = ModManager.DiscoverAll(_gameRoot, _workspace, _dataRoot, _cliPacks);
        var (ordered, enabled) = ModManager.BuildInitialState(discovered, saved);

        if (!preserveStagedChanges)
        {
            _orderedMods = ordered;
            _enabledKeys = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            _initialEnabledKeys = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            _initialOrderKeys = ordered.Select(static m => m.Key).ToList();
        }
        else
        {
            var byKey = ordered.ToDictionary(static m => m.Key, StringComparer.OrdinalIgnoreCase);
            var merged = new List<ModManager.DiscoveredMod>();
            foreach (var prev in _orderedMods)
            {
                if (byKey.Remove(prev.Key, out var updated))
                    merged.Add(updated);
            }
            foreach (var added in ordered)
            {
                if (byKey.ContainsKey(added.Key))
                {
                    merged.Add(added);
                    if (added.DefaultEnabled && added.LoadError is null)
                        _enabledKeys.Add(added.Key);
                }
            }
            _orderedMods = merged;
        }

        if (_selectedKey is null || _orderedMods.All(m => !string.Equals(m.Key, _selectedKey, StringComparison.OrdinalIgnoreCase)))
        {
            // Prefer selecting the first enabled or photo-bearing pack so the screen opens on real content
            _selectedKey = _orderedMods.FirstOrDefault(static m => m.PreviewImagePath is not null)?.Key
                ?? _orderedMods.FirstOrDefault()?.Key;
        }

        ClampScroll();
        _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
    }

    private List<int> FilteredIndices()
    {
        var list = new List<int>(_orderedMods.Count);
        for (int i = 0; i < _orderedMods.Count; i++)
        {
            var kind = _orderedMods[i].Kind;
            if (_activeTab == FilterTab.Packs && kind != ModManager.ModEntryKind.Pack)
                continue;
            if (_activeTab == FilterTab.Downloads && kind != ModManager.ModEntryKind.DownloadsSubfolder)
                continue;
            list.Add(i);
        }
        return list;
    }

    private (double Left, double Top, double Scale) Transform(CGSize canvas, float uiScale)
    {
        double scale = LiveUI.FullscreenOverlayScale(canvas, uiScale);
        double maxFit = Math.Max(0.25, Math.Min((canvas.Width - 20) / DialogW, (canvas.Height - 20) / DialogH));
        scale = Math.Min(scale, maxFit);
        double left = (canvas.Width - DialogW * scale) * 0.5;
        double top = (canvas.Height - DialogH * scale) * 0.5;
        return (left, top, scale);
    }

    private CGPoint ToLocal(CGPoint point, CGSize canvas, float uiScale)
    {
        var (left, top, scale) = Transform(canvas, uiScale);
        return new CGPoint((point.X - left) / scale, (point.Y - top) / scale);
    }

    private CGRect ToCanvas(CGRect local, double left, double top, double scale) =>
        new(left + local.X * scale, top + local.Y * scale, local.Width * scale, local.Height * scale);

    private ModManager.EvaluatedModStatus? SelectedStatus()
    {
        if (_selectedKey is null)
            return null;
        foreach (var entry in _report.Entries)
        {
            if (string.Equals(entry.Mod.Key, _selectedKey, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private IEnumerable<(string Id, string Label, CGRect LocalRect, bool Enabled, bool Highlighted)> InteractiveButtons()
    {
        yield return ("tab_all", "All", new CGRect(ListX, TabsY, 110, 26), true, _activeTab == FilterTab.All);
        yield return ("tab_packs", "Packs", new CGRect(ListX + 116, TabsY, 122, 26), true, _activeTab == FilterTab.Packs);
        yield return ("tab_dl", "Downloads", new CGRect(ListX + 244, TabsY, 138, 26), true, _activeTab == FilterTab.Downloads);

        if (SelectedStatus() is { } sel)
        {
            bool canToggle = sel.Mod.LoadError is null || sel.Enabled;
            string toggleLabel = sel.Enabled ? "Disable" : "Enable";
            bool hasPluginBtn = sel.Mod.PluginReport is not null;
            double toggleY = hasPluginBtn ? (DetailY + DetailH - 78) : (DetailY + DetailH - 44);
            yield return ("toggle_selected", toggleLabel, new CGRect(DetailX + 14, toggleY, DetailW - 28, 30), canToggle, false);

            if (hasPluginBtn)
            {
                string trustLabel = sel.PluginTrusted == true ? "Block Script" : "Allow Script";
                yield return ("toggle_trust", trustLabel, new CGRect(DetailX + 14, DetailY + DetailH - 44, DetailW - 28, 30), true, sel.PluginTrusted != true);
            }
        }

        bool dirty = HasStagedChanges || SafeModeNotice is not null;
        string applyLabel = dirty ? "Apply" : "Done";
        yield return ("apply", applyLabel, new CGRect(20, DialogH - 48, 166, 33), _report.CanApply, dirty);
        yield return ("open_folder", "Open Mods Folder", new CGRect(194, DialogH - 48, 174, 33), true, false);
        yield return ("refresh", "Refresh", new CGRect(376, DialogH - 48, 132, 33), true, false);
        yield return ("cancel", "Cancel", new CGRect(516, DialogH - 48, 148, 33), true, false);
    }

    public void Hover(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var local = ToLocal(point, canvas, uiScale);
        _hoveredId = HitIdAt(local);
    }

    public void ScrollWheel(double notches)
    {
        var visible = FilteredIndices();
        if (visible.Count <= MaxVisibleRows || Math.Abs(notches) < 0.01)
            return;
        int step = notches > 0 ? -2 : 2;
        _scrollOffset = Math.Clamp(_scrollOffset + step, 0, Math.Max(0, visible.Count - MaxVisibleRows));
    }

    public void TextInput(string text)
    {
        _ = text;
    }

    public Action? KeyPress(bool isUp, bool isDown, bool isEnter, bool isEscape, bool isBackspace = false)
    {
        _ = isBackspace;
        if (isEscape)
            return new Action.Cancel();

        var visible = FilteredIndices();
        if ((isUp || isDown) && visible.Count > 0)
        {
            int curPos = _selectedKey is null
                ? -1
                : visible.FindIndex(idx => string.Equals(_orderedMods[idx].Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
            int nextPos = curPos < 0 ? 0 : Math.Clamp(curPos + (isUp ? -1 : 1), 0, visible.Count - 1);
            _selectedKey = _orderedMods[visible[nextPos]].Key;
            if (nextPos < _scrollOffset)
                _scrollOffset = nextPos;
            else if (nextPos >= _scrollOffset + MaxVisibleRows)
                _scrollOffset = nextPos - MaxVisibleRows + 1;
            ClampScroll();
            return null;
        }

        if (isEnter && _report.CanApply)
        {
            var settings = ModManager.BuildSettingsFromState(_orderedMods, _enabledKeys);
            return new Action.ApplyAndReload(settings, HasStagedChanges || SafeModeNotice is not null);
        }

        return null;
    }

    public bool PointerDown(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var local = ToLocal(point, canvas, uiScale);
        _pressedButtonId = null;
        foreach (var (id, _, bRect, enabled, _) in InteractiveButtons())
        {
            if (enabled && bRect.Contains(local))
            {
                _pressedButtonId = id;
                break;
            }
        }

        var visible = FilteredIndices();
        if (visible.Count > MaxVisibleRows)
        {
            if (ScrollUpRect().Contains(local))
            {
                _scrollOffset = Math.Max(0, _scrollOffset - 1);
                return true;
            }
            if (ScrollDownRect().Contains(local))
            {
                _scrollOffset = Math.Min(Math.Max(0, visible.Count - MaxVisibleRows), _scrollOffset + 1);
                return true;
            }
            var track = ScrollThumbTrackRect();
            if (track.Contains(local))
            {
                var thumb = ScrollThumbRect(visible.Count);
                _draggingScrollbar = true;
                _scrollbarGrabOffsetY = thumb.Contains(local) ? (local.Y - thumb.Y) : (thumb.Height * 0.5);
                UpdateScrollbarFromLocalY(local.Y, visible.Count);
                return true;
            }
        }

        int rowW = RowWidth(visible.Count > MaxVisibleRows);
        var listInner = new CGRect(ListX + 7, ListY + 8, rowW, MaxVisibleRows * RowPitch);
        if (listInner.Contains(local))
        {
            int row = (int)Math.Floor((local.Y - listInner.MinY) / RowPitch);
            int visIdx = _scrollOffset + row;
            if (row >= 0 && row < MaxVisibleRows && visIdx >= 0 && visIdx < visible.Count)
            {
                int globalIdx = visible[visIdx];
                var mod = _orderedMods[globalIdx];
                _selectedKey = mod.Key;

                double relX = local.X - listInner.MinX;
                if (relX is >= 10 and <= 35)
                    return false;

                if (mod.Kind == ModManager.ModEntryKind.Pack && _orderedMods.Count(static m => m.Kind == ModManager.ModEntryKind.Pack) > 1)
                {
                    _draggingPackGlobalIdx = globalIdx;
                    _dragStartLocalY = local.Y;
                    _dragMoved = false;
                    _dragDropTargetPackIdx = globalIdx;
                    return true;
                }
            }
        }

        return false;
    }

    public void PointerDragged(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        var local = ToLocal(point, canvas, uiScale);
        var visible = FilteredIndices();

        if (_draggingScrollbar)
        {
            UpdateScrollbarFromLocalY(local.Y, visible.Count);
            return;
        }

        if (_draggingPackGlobalIdx is int srcGlobalIdx)
        {
            if (Math.Abs(local.Y - _dragStartLocalY) > 4.0)
                _dragMoved = true;

            int packCount = _orderedMods.Count(static m => m.Kind == ModManager.ModEntryKind.Pack);
            double listTop = ListY + 8;
            int rawRow = (int)Math.Clamp(Math.Floor((local.Y - listTop + RowPitch * 0.5) / RowPitch), 0, MaxVisibleRows);
            int targetVis = Math.Clamp(_scrollOffset + rawRow, 0, Math.Max(0, visible.Count));

            int targetPackIdx = targetVis >= visible.Count
                ? packCount - 1
                : Math.Clamp(visible[targetVis], 0, packCount - 1);
            _dragDropTargetPackIdx = Math.Clamp(targetPackIdx, 0, packCount - 1);
            _ = srcGlobalIdx;
        }
    }

    public void PointerReleased(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        _pressedButtonId = null;
        _draggingScrollbar = false;
        if (_draggingPackGlobalIdx is int srcIdx)
        {
            if (_dragMoved && _dragDropTargetPackIdx is int dstIdx && dstIdx != srcIdx)
            {
                MovePackToExactIndex(srcIdx, dstIdx);
            }
            _draggingPackGlobalIdx = null;
            _dragDropTargetPackIdx = null;
            _dragMoved = false;
        }
        Hover(point, canvas, uiScale);
    }

    public Action? Click(CGPoint point, CGSize canvas, float uiScale = 1f)
    {
        _pressedButtonId = null;
        var local = ToLocal(point, canvas, uiScale);

        var visible = FilteredIndices();
        int rowW = RowWidth(visible.Count > MaxVisibleRows);
        var listInner = new CGRect(ListX + 7, ListY + 8, rowW, MaxVisibleRows * RowPitch);
        if (listInner.Contains(local))
        {
            int row = (int)Math.Floor((local.Y - listInner.MinY) / RowPitch);
            int visIdx = _scrollOffset + row;
            if (row >= 0 && row < MaxVisibleRows && visIdx >= 0 && visIdx < visible.Count)
            {
                int globalIdx = visible[visIdx];
                var mod = _orderedMods[globalIdx];
                _selectedKey = mod.Key;

                double relX = local.X - listInner.MinX;
                if (relX is >= 10 and <= 35)
                    ToggleModAtIndex(globalIdx);

                return null;
            }
        }

        foreach (var (id, _, rect, enabled, _) in InteractiveButtons())
        {
            if (!rect.Contains(local) || !enabled)
                continue;

            switch (id)
            {
                case "tab_all":
                    _activeTab = FilterTab.All;
                    _scrollOffset = 0;
                    return null;
                case "tab_packs":
                    _activeTab = FilterTab.Packs;
                    _scrollOffset = 0;
                    return null;
                case "tab_dl":
                    _activeTab = FilterTab.Downloads;
                    _scrollOffset = 0;
                    return null;
                case "toggle_selected":
                    if (_selectedKey is not null)
                    {
                        int gIdx = _orderedMods.FindIndex(m => string.Equals(m.Key, _selectedKey, StringComparison.OrdinalIgnoreCase));
                        ToggleModAtIndex(gIdx);
                    }
                    return null;
                case "toggle_trust":
                    ToggleSelectedPluginTrust();
                    return null;
                case "refresh":
                    ReloadDiscovery(preserveStagedChanges: true);
                    return null;
                case "open_folder":
                    ModManager.OpenUserModsDirectory(_dataRoot);
                    return new Action.OpenModsFolder();
                case "apply":
                {
                    var settings = ModManager.BuildSettingsFromState(_orderedMods, _enabledKeys);
                    return new Action.ApplyAndReload(settings, HasStagedChanges || SafeModeNotice is not null);
                }
                case "cancel":
                    return new Action.Cancel();
            }
        }

        return null;
    }

    public IReadOnlyList<(string Label, CGRect Rect)> Controls(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, scale) = Transform(canvas, uiScale);
        var controls = new List<(string Label, CGRect Rect)>();
        var visible = FilteredIndices();
        int rowW = RowWidth(visible.Count > MaxVisibleRows);

        for (int r = 0; r < MaxVisibleRows; r++)
        {
            int visIdx = _scrollOffset + r;
            if (visIdx >= visible.Count)
                break;
            var mod = _orderedMods[visible[visIdx]];
            var rowRect = new CGRect(ListX + 7, ListY + 8 + r * RowPitch, rowW, RowItemH);
            controls.Add(($"Mod: {mod.DisplayName}", ToCanvas(rowRect, left, top, scale)));
        }

        foreach (var (_, label, localRect, enabled, _) in InteractiveButtons())
        {
            if (enabled)
                controls.Add((label, ToCanvas(localRect, left, top, scale)));
        }

        return controls;
    }

    public IReadOnlyList<OriginalNeighborhoodOverlay.DrawItem> DrawItems(CGSize canvas, float uiScale = 1f)
    {
        var (left, top, scale) = Transform(canvas, uiScale);
        var items = new List<OriginalNeighborhoodOverlay.DrawItem>(128)
        {
            new(_dimBackdrop, new CGRect(0, 0, canvas.Width, canvas.Height)),
            new(_panelFrame, ToCanvas(new CGRect(-FramePad, -FramePad, DialogW + FramePad * 2, DialogH + FramePad * 2), left, top, scale)),
            new(_listWell, ToCanvas(new CGRect(ListX, ListY, ListW, ListH), left, top, scale)),
            new(_detailWell, ToCanvas(new CGRect(DetailX, DetailY, DetailW, DetailH), left, top, scale)),
        };

        DrawText(items, "MODS & CUSTOM CONTENT", new CGPoint(22, 14), SimsVectorUiKit.TextTitle, tier: 2, left, top, scale);

        if (SafeModeNotice is not null)
        {
            var safeImg = GetTextImage("Safe Mode", SimsVectorUiKit.TextWarnAmber, tier: 0);
            items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                safeImg,
                ToCanvas(new CGRect(DialogW - 22 - safeImg.Size.Width, 18, safeImg.Size.Width, safeImg.Size.Height), left, top, scale)));
        }

        var visible = FilteredIndices();
        bool hasScrollbar = visible.Count > MaxVisibleRows;
        int rowW = RowWidth(hasScrollbar);
        var rowStrip = GetRowStrip(rowW);

        if (visible.Count == 0)
        {
            string emptyMsg = _orderedMods.Count == 0
                ? "No mods installed yet.\n\nDrag & drop .iff or .hod files here, or click 'Open Mods Folder'."
                : _activeTab == FilterTab.Downloads
                    ? "No custom downloads installed.\n\nDrag & drop .iff or .hod files here to import."
                    : "No packs installed.";
            DrawWrappedText(
                items,
                emptyMsg,
                new CGRect(ListX + 18, ListY + 22, ListW - 36, ListH - 44),
                SimsVectorUiKit.TextMuted,
                tier: 1,
                left,
                top,
                scale);
        }
        else
        {
            for (int r = 0; r < MaxVisibleRows; r++)
            {
                int visIdx = _scrollOffset + r;
                if (visIdx >= visible.Count)
                    break;

                int globalIdx = visible[visIdx];
                var status = _report.Entries[globalIdx];
                bool isSelected = string.Equals(status.Mod.Key, _selectedKey, StringComparison.OrdinalIgnoreCase);
                bool isHovered = _hoveredId == $"row:{globalIdx}";
                bool isBeingDragged = _draggingPackGlobalIdx == globalIdx && _dragMoved;

                int rowState = isSelected ? 2 : isHovered ? 1 : 0;
                var rowLocal = new CGRect(ListX + 7, ListY + 8 + r * RowPitch, rowW, RowItemH);
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                    rowStrip,
                    ToCanvas(rowLocal, left, top, scale),
                    opacity: isBeingDragged ? 0.55f : 1f,
                    uv: new Vector4(0f, rowState / 3f, 1f, 1f / 3f)));

                int chkState = status.Enabled ? (isHovered ? 3 : 1) : (isHovered ? 2 : 0);
                var chkLocal = new CGRect(rowLocal.X + 12, rowLocal.Y + 5.5, 19, 19);
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                    _checkboxStrip,
                    ToCanvas(chkLocal, left, top, scale),
                    uv: new Vector4(chkState / 6f, 0f, 1f / 6f, 1f)));

                bool hasConflict = status.HardConflictErrors.Count > 0;
                bool needsTrust = status.Enabled && status.PluginTrusted == false;
                int maxNameW = rowW - (hasConflict || needsTrust ? 104 : 52);
                string displayName = FitSingleLine(status.Mod.DisplayName, _font, maxNameW);
                var nameCol = hasConflict
                    ? SimsVectorUiKit.TextErrorRed
                    : !status.Enabled
                        ? (isSelected ? SimsVectorUiKit.TextHover : SimsVectorUiKit.TextMuted)
                        : isSelected
                            ? SimsVectorUiKit.TextSelected
                            : isHovered
                                ? SimsVectorUiKit.TextHover
                                : SimsVectorUiKit.TextNormal;
                DrawText(items, displayName, new CGPoint(rowLocal.X + 40, rowLocal.Y + 6.5), nameCol, tier: 1, left, top, scale);

                if (hasConflict)
                {
                    DrawText(items, "Conflict", new CGPoint(rowLocal.MaxX - 54, rowLocal.Y + 7.5), SimsVectorUiKit.TextErrorRed, tier: 0, left, top, scale);
                }
                else if (needsTrust)
                {
                    DrawText(items, "Script", new CGPoint(rowLocal.MaxX - 44, rowLocal.Y + 7.5), SimsVectorUiKit.TextWarnAmber, tier: 0, left, top, scale);
                }
            }

            if (_draggingPackGlobalIdx is int srcG && _dragMoved && _dragDropTargetPackIdx is int dstG && dstG != srcG)
            {
                int visTarget = visible.IndexOf(dstG);
                if (visTarget >= _scrollOffset && visTarget <= _scrollOffset + MaxVisibleRows)
                {
                    double lineY = ListY + 8 + (visTarget - _scrollOffset + (dstG > srcG ? 1 : 0)) * RowPitch - 2;
                    items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                        _dropLineFill,
                        ToCanvas(new CGRect(ListX + 8, lineY, rowW - 2, 2.2), left, top, scale)));
                }
            }

            if (hasScrollbar)
            {
                var fullBar = ScrollBarFullRect();
                var upRect = ScrollUpRect();
                var dnRect = ScrollDownRect();
                var thumbRect = ScrollThumbRect(visible.Count);
                var midTrack = new CGRect(fullBar.X, upRect.MaxY, fullBar.Width, dnRect.Y - upRect.MaxY);

                items.Add(new OriginalNeighborhoodOverlay.DrawItem(_scrollTrackImg, ToCanvas(midTrack, left, top, scale)));
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                    _hoveredId == "scroll_up" ? _scrollUpHover : _scrollUpNormal,
                    ToCanvas(upRect, left, top, scale)));
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                    _hoveredId == "scroll_down" ? _scrollDownHover : _scrollDownNormal,
                    ToCanvas(dnRect, left, top, scale)));

                bool thumbHover = _draggingScrollbar || _hoveredId == "scroll_thumb";
                var thumbImg = GetThumbImage((int)Math.Round(thumbRect.Height), thumbHover);
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(thumbImg, ToCanvas(thumbRect, left, top, scale)));
            }
        }

        // Right-hand Preview Card (like The Sims 1 Family Selection card: Photo Frame + Title + Summary + Toggle)
        if (SelectedStatus() is { } sel)
        {
            var photoOuter = new CGRect(DetailX + 14, DetailY + 14, PhotoFrameW, PhotoFrameH);
            var photoInner = new CGRect(
                photoOuter.X + PhotoInnerPad,
                photoOuter.Y + PhotoInnerPad,
                PhotoFrameW - PhotoInnerPad * 2,
                PhotoFrameH - PhotoInnerPad * 2);

            items.Add(new OriginalNeighborhoodOverlay.DrawItem(_photoFrameWell, ToCanvas(photoOuter, left, top, scale)));
            var photoImg = GetPreviewPhotoImage(sel.Mod) ?? _defaultPhotoPlaceholder;
            items.Add(new OriginalNeighborhoodOverlay.DrawItem(photoImg, ToCanvas(photoInner, left, top, scale)));

            string verText = !string.IsNullOrWhiteSpace(sel.Mod.Version) && sel.Mod.Version != "Invalid"
                ? $"v{sel.Mod.Version}"
                : string.Empty;
            int verW = verText.Length > 0 ? _smallFont.TextWidth(verText) + 8 : 0;
            string title = FitSingleLine(sel.Mod.DisplayName, _font, DetailW - 28 - verW);
            DrawText(items, title, new CGPoint(DetailX + 14, DetailY + 156), SimsVectorUiKit.TextHover, tier: 1, left, top, scale);
            if (verText.Length > 0)
            {
                var verImg = GetTextImage(verText, SimsVectorUiKit.TextMuted, tier: 0);
                items.Add(new OriginalNeighborhoodOverlay.DrawItem(
                    verImg,
                    ToCanvas(new CGRect(DetailX + DetailW - 14 - verImg.Size.Width, DetailY + 159, verImg.Size.Width, verImg.Size.Height), left, top, scale)));
            }

            bool hasPluginBtn = sel.Mod.PluginReport is not null;
            double bottomBtnsTop = hasPluginBtn ? (DetailY + DetailH - 84) : (DetailY + DetailH - 50);
            var descRect = new CGRect(DetailX + 14, DetailY + 180, DetailW - 28, Math.Max(28, bottomBtnsTop - (DetailY + 180)));

            if (sel.HardConflictErrors.Count > 0)
            {
                DrawWrappedText(items, string.Join("\n", sel.HardConflictErrors), descRect, SimsVectorUiKit.TextErrorRed, tier: 0, left, top, scale);
            }
            else if (_report.GlobalHardErrors.Count > 0 && sel.Enabled)
            {
                DrawWrappedText(items, string.Join("\n", _report.GlobalHardErrors), descRect, SimsVectorUiKit.TextErrorRed, tier: 0, left, top, scale);
            }
            else if (_dropNotice is not null)
            {
                var dropCol = _dropNoticeIsError ? SimsVectorUiKit.TextErrorRed : SimsVectorUiKit.TextSelected;
                DrawWrappedText(items, _dropNotice, descRect, dropCol, tier: 0, left, top, scale);
            }
            else if (SafeModeNotice is not null)
            {
                DrawWrappedText(items, SafeModeNotice, descRect, SimsVectorUiKit.TextWarnAmber, tier: 0, left, top, scale);
            }
            else
            {
                string summary = !string.IsNullOrWhiteSpace(sel.Mod.Description)
                    ? sel.Mod.Description!
                    : string.Join(" · ", sel.Mod.CategoryTags);
                if (sel.Enabled && sel.PluginTrusted == false)
                    summary += "\n\nClick 'Allow Script' below to enable gameplay scripts.";
                var summaryCol = sel.Enabled && sel.PluginTrusted == false
                    ? SimsVectorUiKit.TextWarnAmber
                    : SimsVectorUiKit.TextNormal;
                DrawWrappedText(items, summary, descRect, summaryCol, tier: 0, left, top, scale);
            }
        }
        else if (_dropNotice is not null)
        {
            var dropRect = new CGRect(DetailX + 14, DetailY + 18, DetailW - 28, DetailH - 36);
            var dropCol = _dropNoticeIsError ? SimsVectorUiKit.TextErrorRed : SimsVectorUiKit.TextSelected;
            DrawWrappedText(items, _dropNotice, dropRect, dropCol, tier: 0, left, top, scale);
        }

        foreach (var (id, label, localRect, enabled, highlighted) in InteractiveButtons())
        {
            var state = !enabled
                ? SimsVectorUiKit.WidgetState.Disabled
                : _pressedButtonId == id
                    ? SimsVectorUiKit.WidgetState.Pressed
                    : _hoveredId == id
                        ? SimsVectorUiKit.WidgetState.Hover
                        : SimsVectorUiKit.WidgetState.Normal;
            DrawSlantedButton(items, label, localRect, state, highlighted, left, top, scale);
        }

        return items;
    }

    private OriginalGameplayOverlay.Image? GetPreviewPhotoImage(ModManager.DiscoveredMod mod)
    {
        if (string.IsNullOrWhiteSpace(mod.PreviewImagePath))
            return null;
        if (_previewPhotoCache.TryGetValue(mod.PreviewImagePath, out var cached))
            return cached;

        OriginalGameplayOverlay.Image? loaded = null;
        try
        {
            if (TryDecodeRawImage(mod.PreviewImagePath, out int srcW, out int srcH, out byte[] srcRgba))
            {
                int s = SimsVectorUiKit.CurrentScale();
                int logW = PhotoFrameW - PhotoInnerPad * 2;
                int logH = PhotoFrameH - PhotoInnerPad * 2;
                int dstW = logW * s;
                int dstH = logH * s;
                byte[] cropped = CoverCropWithRoundedMask(srcRgba, srcW, srcH, dstW, dstH, cornerR: 4f * s);
                loaded = OriginalGameplayOverlay.CreateImage(_device, dstW, dstH, cropped, new CGSize(logW, logH));
            }
        }
        catch
        {
        }

        _previewPhotoCache[mod.PreviewImagePath] = loaded;
        return loaded;
    }

    private static bool TryDecodeRawImage(string path, out int w, out int h, out byte[] rgba)
    {
        w = 0;
        h = 0;
        rgba = [];
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24)
            return false;

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            int pw = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            int ph = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            var png = new PNGImage(bytes, pw, ph);
            w = png.Width;
            h = png.Height;
            rgba = png.Rgba;
            return true;
        }

        if (path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            var bmp = new SimsBitmap(bytes);
            w = bmp.Width;
            h = bmp.Height;
            rgba = bmp.Rgba;
            return true;
        }

        if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            var tga = new SimsTarga(bytes);
            w = tga.Width;
            h = tga.Height;
            rgba = tga.Rgba;
            return true;
        }

        return false;
    }

    private static byte[] CoverCropWithRoundedMask(byte[] src, int sw, int sh, int dw, int dh, float cornerR)
    {
        byte[] dst = new byte[dw * dh * 4];
        float scale = MathF.Max((float)dw / Math.Max(1, sw), (float)dh / Math.Max(1, sh));
        float visW = dw / scale;
        float visH = dh / scale;
        float offX = (sw - visW) * 0.5f;
        float offY = (sh - visH) * 0.5f;
        float hw = dw * 0.5f;
        float hh = dh * 0.5f;

        for (int dy = 0; dy < dh; dy++)
        {
            float fy = offY + (dy + 0.5f) / scale - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, sw > 0 ? sh - 1 : 0);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float ty = Math.Clamp(fy - y0, 0f, 1f);

            for (int dx = 0; dx < dw; dx++)
            {
                float qx = MathF.Abs(dx + 0.5f - hw) - (hw - cornerR);
                float qy = MathF.Abs(dy + 0.5f - hh) - (hh - cornerR);
                float dRound = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f))
                    + MathF.Min(MathF.Max(qx, qy), 0f) - cornerR;
                float maskA = Math.Clamp(0.5f - dRound, 0f, 1f);
                if (maskA <= 0.001f)
                    continue;

                float fx = offX + (dx + 0.5f) / scale - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float tx = Math.Clamp(fx - x0, 0f, 1f);

                int i00 = (y0 * sw + x0) * 4;
                int i10 = (y0 * sw + x1) * 4;
                int i01 = (y1 * sw + x0) * 4;
                int i11 = (y1 * sw + x1) * 4;

                int o = (dy * dw + dx) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float top = src[i00 + c] + (src[i10 + c] - src[i00 + c]) * tx;
                    float bot = src[i01 + c] + (src[i11 + c] - src[i01 + c]) * tx;
                    dst[o + c] = (byte)Math.Clamp((int)MathF.Round(top + (bot - top) * ty), 0, 255);
                }
                dst[o + 3] = (byte)Math.Clamp((int)MathF.Round(maskA * 255f), 0, 255);
            }
        }
        return dst;
    }

    private OriginalGameplayOverlay.Image BuildDefaultPhotoPlaceholder(int logW, int logH, int scale)
    {
        int pw = logW * scale;
        int ph = logH * scale;
        byte[] rgba = new byte[pw * ph * 4];
        float hw = pw * 0.5f;
        float hh = ph * 0.5f;
        float cornerR = 4f * scale;
        float plumbobH = ph * 0.30f;
        float plumbobW = plumbobH * 0.48f;

        for (int py = 0; py < ph; py++)
        {
            float v = (float)py / Math.Max(1, ph - 1);
            for (int px = 0; px < pw; px++)
            {
                float u = (float)px / Math.Max(1, pw - 1);
                float qx = MathF.Abs(px + 0.5f - hw) - (hw - cornerR);
                float qy = MathF.Abs(py + 0.5f - hh) - (hh - cornerR);
                float dRound = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f))
                    + MathF.Min(MathF.Max(qx, qy), 0f) - cornerR;
                float maskA = Math.Clamp(0.5f - dRound, 0f, 1f);
                if (maskA <= 0.001f)
                    continue;

                float r = 4f + 8f * u;
                float g = 18f + 34f * (1f - v);
                float b = 74f + 46f * (0.5f * u + 0.5f * (1f - v));

                // Subtle centered Sims plumbob diamond emblem
                float dx = px + 0.5f - hw;
                float dy = py + 0.5f - hh;
                float dDiamond = (MathF.Abs(dx) / plumbobW + MathF.Abs(dy) / plumbobH - 1f) * plumbobW * 0.7f;
                float gemCov = Math.Clamp(0.5f - dDiamond, 0f, 1f);
                if (gemCov > 0.001f)
                {
                    bool leftFacet = dx < 0f;
                    bool topFacet = dy < 0f;
                    float gr = leftFacet ? (topFacet ? 88f : 24f) : (topFacet ? 36f : 12f);
                    float gg = leftFacet ? (topFacet ? 242f : 184f) : (topFacet ? 206f : 142f);
                    float gb = leftFacet ? (topFacet ? 196f : 148f) : (topFacet ? 168f : 118f);
                    r += (gr - r) * gemCov * 0.85f;
                    g += (gg - g) * gemCov * 0.85f;
                    b += (gb - b) * gemCov * 0.85f;
                }

                int o = (py * pw + px) * 4;
                rgba[o] = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);
                rgba[o + 3] = (byte)Math.Clamp((int)MathF.Round(maskA * 255f), 0, 255);
            }
        }

        return OriginalGameplayOverlay.CreateImage(_device, pw, ph, rgba, new CGSize(logW, logH));
    }

    private static int RowWidth(bool hasScrollbar) =>
        ListW - 14 - (hasScrollbar ? ScrollBarW + 6 : 0);

    private static CGRect ScrollBarFullRect() =>
        new(ListX + ListW - ScrollBarW - 5, ListY + 6, ScrollBarW, ListH - 12);

    private static CGRect ScrollUpRect()
    {
        var bar = ScrollBarFullRect();
        return new CGRect(bar.X, bar.Y, bar.Width, ScrollArrowH);
    }

    private static CGRect ScrollDownRect()
    {
        var bar = ScrollBarFullRect();
        return new CGRect(bar.X, bar.MaxY - ScrollArrowH, bar.Width, ScrollArrowH);
    }

    private static CGRect ScrollThumbTrackRect()
    {
        var bar = ScrollBarFullRect();
        return new CGRect(bar.X, bar.Y + ScrollArrowH + 1, bar.Width, bar.Height - ScrollArrowH * 2 - 2);
    }

    private CGRect ScrollThumbRect(int totalVisible)
    {
        var track = ScrollThumbTrackRect();
        double ratio = Math.Clamp((double)MaxVisibleRows / Math.Max(1, totalVisible), 0.12, 1.0);
        double thumbH = Math.Max(26.0, Math.Round(track.Height * ratio));
        int maxScroll = Math.Max(1, totalVisible - MaxVisibleRows);
        double t = Math.Clamp((double)_scrollOffset / maxScroll, 0.0, 1.0);
        double thumbY = track.Y + (track.Height - thumbH) * t;
        return new CGRect(track.X, thumbY, track.Width, thumbH);
    }

    private void UpdateScrollbarFromLocalY(double localY, int totalVisible)
    {
        var track = ScrollThumbTrackRect();
        double ratio = Math.Clamp((double)MaxVisibleRows / Math.Max(1, totalVisible), 0.12, 1.0);
        double thumbH = Math.Max(26.0, Math.Round(track.Height * ratio));
        double avail = Math.Max(1.0, track.Height - thumbH);
        double t = Math.Clamp((localY - _scrollbarGrabOffsetY - track.Y) / avail, 0.0, 1.0);
        int maxScroll = Math.Max(0, totalVisible - MaxVisibleRows);
        _scrollOffset = (int)Math.Round(t * maxScroll);
    }

    private OriginalGameplayOverlay.Image GetRowStrip(int rowW)
    {
        if (_rowStripCache.TryGetValue(rowW, out var cached))
            return cached;
        var raster = SimsVectorUiKit.RenderListRowStrip(rowW, RowItemH, SimsVectorUiKit.CurrentScale());
        var created = OriginalGameplayOverlay.CreateImage(_device, raster.PixelWidth, raster.PixelHeight, raster.Rgba, raster.LogicalSize);
        _rowStripCache[rowW] = created;
        return created;
    }

    private OriginalGameplayOverlay.Image GetThumbImage(int thumbH, bool hover)
    {
        thumbH = Math.Max(24, thumbH);
        var key = (thumbH, hover);
        if (_thumbCache.TryGetValue(key, out var cached))
            return cached;
        var raster = SimsVectorUiKit.RenderScrollbarCap(ScrollBarW, thumbH, kind: 0, hovered: hover, scale: SimsVectorUiKit.CurrentScale());
        var created = OriginalGameplayOverlay.CreateImage(_device, raster.PixelWidth, raster.PixelHeight, raster.Rgba, raster.LogicalSize);
        _thumbCache[key] = created;
        return created;
    }

    private void DrawSlantedButton(
        List<OriginalNeighborhoodOverlay.DrawItem> items,
        string label,
        CGRect localRect,
        SimsVectorUiKit.WidgetState state,
        bool highlighted,
        double left,
        double top,
        double scale)
    {
        int w = Math.Max(32, (int)Math.Round(localRect.Width));
        int h = Math.Max(22, (int)Math.Round(localRect.Height));
        var cacheKey = (w, h, highlighted);
        if (!_slantedBtnCache.TryGetValue(cacheKey, out var stripImg))
        {
            var raster = SimsVectorUiKit.RenderSlantedButtonStrip(w, h, highlighted, SimsVectorUiKit.CurrentScale());
            stripImg = OriginalGameplayOverlay.CreateImage(_device, raster.PixelWidth, raster.PixelHeight, raster.Rgba, raster.LogicalSize);
            _slantedBtnCache[cacheKey] = stripImg;
        }

        int frame = (int)state;
        items.Add(new OriginalNeighborhoodOverlay.DrawItem(
            stripImg,
            ToCanvas(localRect, left, top, scale),
            uv: new Vector4(frame * 0.25f, 0f, 0.25f, 1f)));

        var textCol = state == SimsVectorUiKit.WidgetState.Disabled
            ? SimsVectorUiKit.TextDisabled
            : highlighted
                ? SimsVectorUiKit.TextSelected
                : state is SimsVectorUiKit.WidgetState.Hover or SimsVectorUiKit.WidgetState.Pressed
                    ? SimsVectorUiKit.TextHover
                    : SimsVectorUiKit.TextNormal;
        double pressShift = state == SimsVectorUiKit.WidgetState.Pressed ? 1.0 : 0.0;
        DrawCenteredText(items, label, localRect.OffsetBy(pressShift, pressShift), textCol, tier: 0, left, top, scale);
    }

    private void MovePackToExactIndex(int srcIdx, int dstIdx)
    {
        int packCount = _orderedMods.Count(static m => m.Kind == ModManager.ModEntryKind.Pack);
        if (srcIdx < 0 || srcIdx >= packCount || dstIdx < 0 || dstIdx >= packCount || srcIdx == dstIdx)
            return;

        var item = _orderedMods[srcIdx];
        _orderedMods.RemoveAt(srcIdx);
        _orderedMods.Insert(dstIdx, item);
        _selectedKey = item.Key;
        _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
    }

    private void ToggleModAtIndex(int index)
    {
        if (index < 0 || index >= _orderedMods.Count)
            return;
        var mod = _orderedMods[index];
        if (_enabledKeys.Contains(mod.Key))
            _enabledKeys.Remove(mod.Key);
        else if (mod.LoadError is null)
            _enabledKeys.Add(mod.Key);

        _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
    }

    private void ToggleSelectedPluginTrust()
    {
        if (SelectedStatus()?.Mod.PluginReport is not { } plugin)
            return;
        try
        {
            var store = new PluginTrustStore(_dataRoot);
            if (store.IsTrusted(plugin))
                store.Revoke(plugin.PackID);
            else
                store.Trust(plugin);
        }
        catch
        {
        }

        _report = ModManager.Evaluate(_gameRoot, _dataRoot, _orderedMods, _enabledKeys);
    }

    private void ClampScroll()
    {
        int maxScroll = Math.Max(0, FilteredIndices().Count - MaxVisibleRows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }

    private string? HitIdAt(CGPoint local)
    {
        var visible = FilteredIndices();
        if (visible.Count > MaxVisibleRows)
        {
            if (ScrollUpRect().Contains(local))
                return "scroll_up";
            if (ScrollDownRect().Contains(local))
                return "scroll_down";
            if (ScrollThumbRect(visible.Count).Contains(local))
                return "scroll_thumb";
        }

        int rowW = RowWidth(visible.Count > MaxVisibleRows);
        var listInner = new CGRect(ListX + 7, ListY + 8, rowW, MaxVisibleRows * RowPitch);
        if (listInner.Contains(local))
        {
            int row = (int)Math.Floor((local.Y - listInner.MinY) / RowPitch);
            int visIdx = _scrollOffset + row;
            if (row >= 0 && row < MaxVisibleRows && visIdx >= 0 && visIdx < visible.Count)
                return $"row:{visible[visIdx]}";
        }

        foreach (var (id, _, rect, enabled, _) in InteractiveButtons())
        {
            if (enabled && rect.Contains(local))
                return id;
        }
        return null;
    }

    private void DrawText(
        List<OriginalNeighborhoodOverlay.DrawItem> items,
        string text,
        CGPoint pos,
        (byte R, byte G, byte B) color,
        int tier,
        double left,
        double top,
        double scale)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var img = GetTextImage(text, color, tier);
        items.Add(new OriginalNeighborhoodOverlay.DrawItem(
            img,
            ToCanvas(new CGRect(pos.X, pos.Y, img.Size.Width, img.Size.Height), left, top, scale)));
    }

    private void DrawCenteredText(
        List<OriginalNeighborhoodOverlay.DrawItem> items,
        string text,
        CGRect box,
        (byte R, byte G, byte B) color,
        int tier,
        double left,
        double top,
        double scale)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var font = FontForTier(tier);
        var img = GetTextImage(text, color, tier);
        double x = box.MidX - img.Size.Width * 0.5;
        double y = box.MidY - font.CapMidYPx;
        items.Add(new OriginalNeighborhoodOverlay.DrawItem(
            img,
            ToCanvas(new CGRect(x, y, img.Size.Width, img.Size.Height), left, top, scale)));
    }

    private void DrawWrappedText(
        List<OriginalNeighborhoodOverlay.DrawItem> items,
        string text,
        CGRect rect,
        (byte R, byte G, byte B) color,
        int tier,
        double left,
        double top,
        double scale)
    {
        var font = FontForTier(tier);
        int maxW = Math.Max(40, (int)Math.Floor(rect.Width));
        double curY = rect.Y;
        foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(para))
            {
                curY += font.LineHeight * 0.55;
                continue;
            }
            IReadOnlyList<string> lines;
            try
            {
                lines = font.WrappedLines(para, maxW);
            }
            catch
            {
                lines = [para];
            }

            foreach (string line in lines)
            {
                if (curY + font.LineHeight > rect.MaxY + 2)
                    return;
                DrawText(items, line, new CGPoint(rect.X, curY), color, tier, left, top, scale);
                curY += font.LineHeight + 1;
            }
        }
    }

    private static string FitSingleLine(string text, SimsBitmapFont font, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.TextWidth(text) <= maxWidth)
            return text;
        const string ellipsis = "...";
        int target = Math.Max(12, maxWidth - font.TextWidth(ellipsis));
        int len = text.Length;
        while (len > 1 && font.TextWidth(text[..len]) > target)
            len--;
        return text[..len].TrimEnd() + ellipsis;
    }

    private SimsBitmapFont FontForTier(int tier) => tier switch
    {
        2 => _titleFont,
        1 => _font,
        _ => _smallFont,
    };

    private OriginalGameplayOverlay.Image GetTextImage(string text, (byte R, byte G, byte B) color, int tier)
    {
        var key = (text, color.R, color.G, color.B, tier);
        if (_textCache.TryGetValue(key, out var cached))
            return cached;
        var created = OriginalGameplayOverlay.Text(text, FontForTier(tier), _device, color);
        _textCache[key] = created;
        return created;
    }

    private static SimsBitmapFont? TryLoadFont(GameContent content, string path)
    {
        try
        {
            return new SimsBitmapFont(content.Read(path));
        }
        catch
        {
            return null;
        }
    }
}
