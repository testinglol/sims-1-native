namespace SimsSDL;

internal sealed class InputScript
{
    public abstract record Action
    {
        private Action() { }
        public sealed record None : Action;
        public sealed record Shot(string Path) : Action;
    }

    private sealed record ConstructionSnapshot(
        int Budget,
        List<string> Walls,
        List<string> Floors,
        IReadOnlyList<short> Heights,
        byte[] Grass) : IEquatable<ConstructionSnapshot>
    {
        public static ConstructionSnapshot? TryCreate(LiveController live)
        {
            if (live.Renderer.Scene is not { } scene || live.Ui.Gameplay.State?.Budget is not { } budget)
            {
                return null;
            }

            List<string> walls = scene.Sprites
                .Where(static s => s.ArchitectureDepth is not null || s.Wall is not null || s.Junction is not null)
                .Select(s => FormattableString.Invariant(
                    $"{scene.Textures[s.Texture].File}|{s.X}|{s.Y}|{s.Width}|{s.Height}|{(s.Flip ? "true" : "false")}|{s.Depth ?? 0f}|{s.Level ?? 1}"))
                .ToList();
            walls.Sort(StringComparer.Ordinal);

            List<string> floors = [];
            if (scene.Meshes is { } meshes)
            {
                foreach (LotScene.Mesh mesh in meshes)
                {
                    if (mesh.Floor is not { } floor)
                    {
                        continue;
                    }

                    string vertices = string.Join(
                        ";",
                        mesh.Vertices.Select(static v => FormattableString.Invariant($"{v.X},{v.Y},{v.Z}")));
                    floors.Add(FormattableString.Invariant(
                        $"{floor.X}|{floor.Y}|{floor.Pattern}|{floor.Half}|{floor.Level ?? 1}|{vertices}"));
                }
            }
            floors.Sort(StringComparer.Ordinal);

            IReadOnlyList<short> heights = scene.TerrainHeights ?? [];
            byte[] grass = (scene.TerrainVertices ?? [])
                .Select(static v => v.Grass ?? (byte)0)
                .ToArray();

            return new ConstructionSnapshot(budget, walls, floors, heights, grass);
        }

        public bool Equals(ConstructionSnapshot? other) =>
            other is not null &&
            Budget == other.Budget &&
            Walls.SequenceEqual(other.Walls, StringComparer.Ordinal) &&
            Floors.SequenceEqual(other.Floors, StringComparer.Ordinal) &&
            Heights.SequenceEqual(other.Heights) &&
            Grass.SequenceEqual(other.Grass);

        public override int GetHashCode() =>
            HashCode.Combine(Budget, Walls.Count, Floors.Count, Heights.Count, Grass.Length);
    }

    private static readonly HashSet<string> SimpleWaitforConditions = new(StringComparer.Ordinal)
    {
        "lot", "album", "illustrated", "scene", "menu", "head", "panel", "dialog", "nodialog", "quiet",
        "neighborhood", "picker", "creation", "preview", "popup", "catalog", "placement", "cursor",
        "built", "canundo", "canredo", "idle", "freewill"
    };

    private static readonly HashSet<string> AllowedHoldKeys = new(StringComparer.Ordinal)
    {
        "ctrl", "shift", "alt", "none"
    };

    private readonly List<(int Number, string[] Words)> _lines;
    private int _index;
    private int _waitFrames;
    private (string Name, int Deadline)? _condition;
    private int _frame;
    private int _lastDialogFrame;

    private int? _lastDialogClock;

    public string? Failed { get; private set; }

    private HashSet<int> _placedBefore = [];
    private HashSet<int> _objectsBefore = [];

    private HashSet<string> _held = [];
    private string? _keepSpeed;

    private long _revisionBefore;

    private int _soundsBefore;

    private readonly Dictionary<string, ConstructionSnapshot> _constructionSnapshots = new(StringComparer.Ordinal);

    public InputScript(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text = File.ReadAllText(path, Encoding.UTF8);
        string[] rawLines = text.Split('\n');
        _lines = new List<(int Number, string[] Words)>(rawLines.Length);
        for (int offset = 0; offset < rawLines.Length; offset++)
        {
            string line = rawLines[offset];
            int commentIndex = line.IndexOf('#');
            string code = commentIndex >= 0 ? line[..commentIndex] : line;
            string[] words = code.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                _lines.Add((offset + 1, words));
            }
        }
    }

    public Action Step(LiveController live)
    {
        _frame += 1;
        if (live.Ui.GameDialog is not null)
        {
            _lastDialogFrame = _frame;
            _lastDialogClock = null;
        }
        else if (_lastDialogClock is null)
        {
            _lastDialogClock = live.Ui.ClockMinutes;
        }

        live.ScriptAnswerLocalDialog();
        if (_keepSpeed is not null &&
            _frame % 30 == 0 &&
            !live.HasBlockingDialog &&
            live.Ui.PieMenu is null &&
            live.Renderer.Speed != (_keepSpeed == "3" ? 10 : _keepSpeed == "2" ? 3 : 1))
        {
            _ = live.HandleKeyDown(new LiveController.Key.Character(_keepSpeed));
        }

        if (_waitFrames > 0)
        {
            _waitFrames -= 1;
            return new Action.None();
        }

        if (_condition is { } pending)
        {
            if (Holds(pending.Name, live))
            {
                _condition = null;
            }
            else if (_frame >= pending.Deadline)
            {
                string placed = pending.Name.StartsWith("placed ", StringComparison.Ordinal)
                    ? "; new objects face " + string.Join(
                        ", ",
                        live.ScriptEditableObjects
                            .Where(kv => !_objectsBefore.Contains(kv.Key))
                            .Select(static kv => kv.Value.ToString(CultureInfo.InvariantCulture)))
                    : "";
                string anims = pending.Name.StartsWith("anim ", StringComparison.Ordinal)
                    ? "; Sims play " + string.Join(", ", live.ScriptSimAnimations)
                    : "";
                string position = live.ScriptSimPosition is { } pos
                    ? FormattableString.Invariant(
                        $"; Sim at {pos.X},{pos.Y}, idle {(live.ScriptSimIdle ? "true" : "false")} [{string.Join(", ", live.ScriptSimActions)}], speed {live.Renderer.Speed}")
                    : "";
                Fail($"waitfor {pending.Name} timed out{placed}{anims}{position}", live);
                return new Action.None();
            }
            else
            {
                return new Action.None();
            }
        }

        while (_index < _lines.Count && !live.QuitRequested)
        {
            (int number, string[] words) = _lines[_index];
            _index += 1;
            string argument = words.Length > 1 ? string.Join(' ', words, 1, words.Length - 1) : "";
            Log($"script {number}: {string.Join(' ', words)}");

            switch (words[0])
            {
                case "wait":
                    _waitFrames = TryParseInt(argument, out int waitCount) ? waitCount : 1;
                    return new Action.None();

                case "autodismiss-until":
                    live.ScriptAnswersDialogs = true;
                    live.ScriptDialogStop = argument;
                    break;

                case "autodismiss":
                    live.ScriptAnswersDialogs = true;
                    break;

                case "keep-speed":
                    if (argument is not ("1" or "2" or "3" or "off"))
                    {
                        Fail($"line {number}: keep-speed needs 1, 2, 3 or off", live);
                        return new Action.None();
                    }
                    _keepSpeed = argument == "off" ? null : argument;
                    break;

                case "cancelactions":
                    foreach ((string label, CGRect rect) in live.ScriptControls)
                    {
                        if (label.StartsWith("Cancel ", StringComparison.Ordinal))
                        {
                            var point = new CGPoint(rect.MidX, rect.MidY);
                            live.Press(point, live.ScriptCanvas, false, false);
                            live.Release(point);
                        }
                    }
                    break;

                case "centresim":
                {
                    IReadOnlyList<int> simIds = live.ScriptSimIDs;
                    if (!TryParseInt(argument, out int simIndex) ||
                        (uint)simIndex >= (uint)simIds.Count ||
                        live.ScriptCentreOnObject(simIds[simIndex]) is null)
                    {
                        Fail($"line {number}: household Sim is not visible", live);
                        return new Action.None();
                    }
                    break;
                }

                case "snapshot":
                {
                    if (words.Length != 2 || ConstructionSnapshot.TryCreate(live) is not { } snapshot)
                    {
                        Fail($"line {number}: snapshot needs a name and a loaded household lot", live);
                        return new Action.None();
                    }
                    _constructionSnapshots[argument] = snapshot;
                    Log(FormattableString.Invariant(
                        $"snapshot {argument}: budget {snapshot.Budget}, walls {snapshot.Walls.Count}, floors {snapshot.Floors.Count}"));
                    break;
                }

                case "waitfor":
                    HandleWaitfor(number, words, live);
                    return new Action.None();

                case "key":
                    HandleKey(argument, live);
                    return new Action.None();

                case "click":
                    if (words.Length != 3 || !TryParseDouble(words[1], out double clickX) || !TryParseDouble(words[2], out double clickY))
                    {
                        Fail($"line {number}: click needs x y", live);
                        return new Action.None();
                    }
                    Click(live.ScriptWindowPoint(clickX, clickY), live);
                    return new Action.None();

                case "pan":
                    if (words.Length != 3 ||
                        !TryParseFloat(words[1], out float panX) ||
                        !TryParseFloat(words[2], out float panY) ||
                        !float.IsFinite(panX) ||
                        !float.IsFinite(panY))
                    {
                        Fail($"line {number}: pan needs finite dx dy", live);
                        return new Action.None();
                    }
                    live.Pan(new Vector2(panX, panY));
                    Log(FormattableString.Invariant($"pan: {live.Renderer.Pan.X},{live.Renderer.Pan.Y}"));
                    return new Action.None();

                case "drag":
                {
                    if (words.Length != 5 ||
                        !TryParseDouble(words[1], out double x1) ||
                        !TryParseDouble(words[2], out double y1) ||
                        !TryParseDouble(words[3], out double x2) ||
                        !TryParseDouble(words[4], out double y2))
                    {
                        Fail($"line {number}: drag needs x y x2 y2", live);
                        return new Action.None();
                    }
                    _placedBefore = [.. live.ScriptEditableObjects.Keys];
                    _revisionBefore = live.Renderer.SceneRevisionNumber;
                    _soundsBefore = live.ScriptUISounds?.Count ?? 0;
                    CGPoint start = live.ScriptWindowPoint(x1, y1);
                    CGPoint end = live.ScriptWindowPoint(x2, y2);
                    live.Hover(start, live.ScriptCanvas);
                    Press(start, live);
                    live.Drag(end);
                    live.Release(end);
                    return new Action.None();
                }

                case "hold":
                {
                    var keys = new HashSet<string>(words.Skip(1).Select(static w => w.ToLowerInvariant()), StringComparer.Ordinal);
                    if (keys.Count == 0 || !keys.IsSubsetOf(AllowedHoldKeys))
                    {
                        Fail($"line {number}: hold needs ctrl, shift, alt or none", live);
                        return new Action.None();
                    }
                    keys.Remove("none");
                    _held = keys;
                    live.ScriptDeleteModifier = _held.Contains("ctrl") || _held.Contains("alt");
                    live.ScriptShiftModifier = _held.Contains("shift");
                    return new Action.None();
                }

                case "move":
                    if (words.Length != 3 || !TryParseDouble(words[1], out double moveX) || !TryParseDouble(words[2], out double moveY))
                    {
                        Fail($"line {number}: move needs x y", live);
                        return new Action.None();
                    }
                    live.Hover(live.ScriptWindowPoint(moveX, moveY), live.ScriptCanvas);
                    return new Action.None();

                case "moveground":
                {
                    if (words.Length != 3 ||
                        !TryParseInt(words[1], out int gx) ||
                        !TryParseInt(words[2], out int gy) ||
                        live.ScriptPointOnGroundCenter(gx, gy) is not { } point)
                    {
                        Fail($"line {number}: ground tile center is outside the visible lot ({live.ScriptGroundProjectionInfo})", live);
                        return new Action.None();
                    }
                    SimsSDL.Core.GroundTile? hit = live.ScriptGroundTileAt(point);
                    Log(FormattableString.Invariant(
                        $"script: ground tile {gx},{gy} center at {(int)point.X},{(int)point.Y}; picker hit {(hit is { } h ? FormattableString.Invariant($"{h.X},{h.Y}") : "none")}"));
                    if (hit is not { } hitTile || hitTile.X != gx || hitTile.Y != gy)
                    {
                        Fail($"line {number}: projected center picked another tile", live);
                        return new Action.None();
                    }
                    live.Hover(point, live.ScriptCanvas);
                    return new Action.None();
                }

                case "centerground":
                {
                    if (words.Length != 3 ||
                        !TryParseInt(words[1], out int gx) ||
                        !TryParseInt(words[2], out int gy) ||
                        live.ScriptCentreOnGround(gx, gy) is not { } point)
                    {
                        Fail($"line {number}: ground tile cannot be centered", live);
                        return new Action.None();
                    }
                    Log(FormattableString.Invariant($"script: centered ground tile {gx},{gy} at {(int)point.X},{(int)point.Y}"));
                    return new Action.None();
                }

                case "hoverentry":
                    if (live.ScriptPointOnMenuEntry(argument) is not { } hoverEntryPoint)
                    {
                        Fail($"line {number}: no pie entry {argument}; available: {string.Join(", ", live.ScriptMenuLabels)}", live);
                        return new Action.None();
                    }
                    live.Hover(hoverEntryPoint, live.ScriptCanvas);
                    return new Action.None();

                case "clickground":
                {
                    if (words.Length != 3 ||
                        !TryParseInt(words[1], out int gx) ||
                        !TryParseInt(words[2], out int gy) ||
                        live.ScriptPointOnGround(gx, gy) is not { } point)
                    {
                        Fail($"line {number}: ground tile is not visible", live);
                        return new Action.None();
                    }
                    Click(point, live);
                    return new Action.None();
                }

                case "clickgroundcenter":
                {
                    if (words.Length != 3 ||
                        !TryParseInt(words[1], out int gx) ||
                        !TryParseInt(words[2], out int gy) ||
                        live.ScriptPointOnGroundCenter(gx, gy) is not { } point)
                    {
                        Fail($"line {number}: ground tile center is outside the visible lot ({live.ScriptGroundProjectionInfo})", live);
                        return new Action.None();
                    }
                    SimsSDL.Core.GroundTile? hit = live.ScriptGroundTileAt(point);
                    Log(FormattableString.Invariant(
                        $"script: ground tile {gx},{gy} center click at {(int)point.X},{(int)point.Y}; picker hit {(hit is { } h ? FormattableString.Invariant($"{h.X},{h.Y}") : "none")}"));
                    if (hit is not { } hitTile || hitTile.X != gx || hitTile.Y != gy)
                    {
                        Fail($"line {number}: projected center picked another tile", live);
                        return new Action.None();
                    }
                    Click(point, live);
                    return new Action.None();
                }

                case "clicksim":
                {
                    IReadOnlyList<int> simIds = live.ScriptSimIDs;
                    if (!TryParseInt(argument, out int simIdx) || (uint)simIdx >= (uint)simIds.Count)
                    {
                        Fail($"line {number}: no Sim {argument}", live);
                        return new Action.None();
                    }
                    if (live.ScriptPointOnObject(simIds[simIdx]) is not { } point)
                    {
                        Fail($"line {number}: Sim {argument} is not on screen", live);
                        return new Action.None();
                    }
                    Log(FormattableString.Invariant($"script: Sim {simIdx} at {(int)point.X},{(int)point.Y}"));
                    Click(point, live);
                    return new Action.None();
                }

                case "rightclicksim":
                {
                    IReadOnlyList<int> simIds = live.ScriptSimIDs;
                    if (!TryParseInt(argument, out int simIdx) || (uint)simIdx >= (uint)simIds.Count)
                    {
                        Fail($"line {number}: no Sim {argument}", live);
                        return new Action.None();
                    }
                    if (live.ScriptPointOnObject(simIds[simIdx]) is not { } point)
                    {
                        Fail($"line {number}: Sim {argument} is not on screen", live);
                        return new Action.None();
                    }
                    Log(FormattableString.Invariant($"script: right-click Sim {simIdx} at {(int)point.X},{(int)point.Y}"));
                    live.RightDown(point);
                    live.RightUp(point);
                    return new Action.None();
                }

                case "cursorshots":
                {
                    if (argument.Length == 0)
                    {
                        Fail($"line {number}: cursorshots needs a folder", live);
                        return new Action.None();
                    }
                    try { Directory.CreateDirectory(argument); } catch { }
                    var cursors = new OriginalCursors(live.Content);
                    foreach (string name in OriginalCursors.Names)
                    {
                        if (!cursors.Dump(name, Path.Combine(argument, $"{name}.bmp")))
                        {
                            Fail($"line {number}: cursor {name} did not decode", live);
                        }
                    }
                    return new Action.None();
                }

                case "hoversim":
                {
                    IReadOnlyList<int> simIds = live.ScriptSimIDs;
                    if (!TryParseInt(argument, out int simIdx) ||
                        (uint)simIdx >= (uint)simIds.Count ||
                        live.ScriptPointOnObject(simIds[simIdx]) is not { } point)
                    {
                        Fail($"line {number}: Sim {argument} is not on screen", live);
                        return new Action.None();
                    }
                    live.Hover(point, live.ScriptCanvas);
                    return new Action.None();
                }

                case "actions":
                {
                    string position = live.ScriptSimPosition is { } pos
                        ? FormattableString.Invariant(
                            $"Sim at {pos.X},{pos.Y}, idle {(live.ScriptSimIdle ? "true" : "false")} [{string.Join(", ", live.ScriptSimActions)}], speed {live.Renderer.Speed}")
                        : "no Sim";
                    Log($"actions: {position}");
                    break;
                }

                case "objects":
                {
                    string needle = argument.ToLowerInvariant();
                    foreach ((int id, string name) in live.ScriptObjects)
                    {
                        if (argument.Length == 0 || name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                        {
                            Log(FormattableString.Invariant($"object: {id} {name}"));
                        }
                    }
                    break;
                }

                case "asserttile":
                    if (words.Length != 6 ||
                        !TryParseInt(words[1], out int tileX) ||
                        !TryParseInt(words[2], out int tileY) ||
                        !TryParseInt(words[3], out int level) ||
                        words[4] != "contains" ||
                        !TryParseInt(words[5], out int expectedId) ||
                        live.ScriptRequestTileTraversal(tileX, tileY, level) is not { } requestId)
                    {
                        Fail($"line {number}: asserttile needs x y level contains id and a fixture-enabled native host", live);
                        return new Action.None();
                    }
                    _condition = (FormattableString.Invariant($"tilecontents {requestId} {expectedId}"), _frame + 600);
                    return new Action.None();

                case "clickobject":
                {
                    IReadOnlyList<int> matches = TryParseInt(argument, out int exactId)
                        ? [exactId]
                        : ScriptObjectMatches(words, live);
                    CGPoint? foundPoint = null;
                    foreach (int id in matches)
                    {
                        if (live.ScriptPointOnObject(id) is { } pt)
                        {
                            foundPoint = pt;
                            break;
                        }
                    }
                    if (foundPoint is not { } point)
                    {
                        Fail($"line {number}: object {argument} is not on screen", live);
                        return new Action.None();
                    }
                    Log(FormattableString.Invariant(
                        $"script: object {(matches.Count > 0 ? matches[0] : 0)} at {(int)point.X},{(int)point.Y}"));
                    Click(point, live);
                    return new Action.None();
                }

                case "centre":
                case "center":
                {
                    IReadOnlyList<int> matches = TryParseInt(argument, out int exactId)
                        ? [exactId]
                        : ScriptObjectMatches(words, live);
                    bool centered = false;
                    foreach (int id in matches)
                    {
                        if (live.ScriptCentreOnObject(id) is not null)
                        {
                            centered = true;
                            break;
                        }
                    }
                    if (!centered)
                    {
                        Fail($"line {number}: object {argument} is not on the lot", live);
                        return new Action.None();
                    }
                    return new Action.None();
                }

                case "choose":
                    if (live.ScriptPointOnMenuEntry(argument) is not { } choosePoint)
                    {
                        Fail($"line {number}: no pie entry {argument}; available: {string.Join(", ", live.ScriptMenuLabels)}", live);
                        return new Action.None();
                    }
                    Click(choosePoint, live);
                    return new Action.None();

                case "control":
                {
                    string needle = argument.ToLowerInvariant();
                    if (needle.Length == 0 || FindControlContains(live.ScriptControls, needle) is not { } control)
                    {
                        Fail($"line {number}: no control {argument}", live);
                        return new Action.None();
                    }
                    Click(new CGPoint(control.Rect.MidX, control.Rect.MidY), live);
                    return new Action.None();
                }

                case "rightrelationship":
                    if (!TryParseInt(argument, out int relId) || live.ScriptRelationshipPoint(relId) is not { } relPoint)
                    {
                        Fail($"line {number}: no visible relationship {argument}", live);
                        return new Action.None();
                    }
                    live.RightDown(relPoint);
                    live.RightUp(relPoint);
                    Log(FormattableString.Invariant($"rightrelationship pan: {live.Renderer.Pan.X},{live.Renderer.Pan.Y}"));
                    return new Action.None();

                case "relationshippopup":
                    if (!TryParseInt(argument, out int popupId) || live.ScriptRelationshipPoint(popupId) is not { } popupPoint)
                    {
                        Fail($"line {number}: no visible relationship {argument}", live);
                        return new Action.None();
                    }
                    Click(popupPoint, live);
                    if (live.ScriptRelationshipPopupID != popupId)
                    {
                        Fail($"line {number}: relationship popup did not open", live);
                        return new Action.None();
                    }
                    return new Action.None();

                case "hover":
                {
                    string needle = argument.ToLowerInvariant();
                    if (needle.Length == 0 || FindControlForHover(live.ScriptControls, needle) is not { } control)
                    {
                        Fail($"line {number}: no control {argument}", live);
                        return new Action.None();
                    }
                    live.Hover(new CGPoint(control.Rect.MidX, control.Rect.MidY), live.ScriptCanvas);
                    return new Action.None();
                }

                case "controls":
                    foreach ((string label, CGRect rect) in live.ScriptControls)
                    {
                        Log(FormattableString.Invariant($"control: {label} at {(int)rect.MidX},{(int)rect.MidY}"));
                    }
                    break;

                case "sweep":
                {
                    List<string> wrong = [];
                    foreach ((string name, CGRect rect, string key, bool affordable) in live.ScriptCatalogItems)
                    {
                        Click(new CGPoint(rect.MidX, rect.MidY), live);
                        bool held = string.Equals(live.ScriptCatalogSelection, key, StringComparison.Ordinal);
                        Log($"sweep: {name} {(affordable ? "affordable" : "unaffordable")}, {(held ? "held" : "not held")}");
                        if (!held)
                        {
                            wrong.Add(name);
                        }
                    }
                    if (wrong.Count > 0)
                    {
                        Fail($"line {number}: catalog selection wrong for {string.Join(", ", wrong)}", live);
                    }
                    return new Action.None();
                }

                case "catalogitem":
                {
                    string needle = argument.ToLowerInvariant();
                    (string Name, CGRect Rect, string Key, bool Affordable)? foundItem = null;
                    if (needle.Length > 0)
                    {
                        foreach (var item in live.ScriptCatalogItems)
                        {
                            if (item.Name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                            {
                                foundItem = item;
                                break;
                            }
                        }
                    }
                    if (foundItem is not { } selectedItem)
                    {
                        Fail($"line {number}: no catalog item {argument}", live);
                        return new Action.None();
                    }
                    Click(new CGPoint(selectedItem.Rect.MidX, selectedItem.Rect.MidY), live);
                    if (!string.Equals(live.ScriptCatalogSelection, selectedItem.Key, StringComparison.Ordinal))
                    {
                        Fail($"line {number}: catalog item {selectedItem.Name} not held", live);
                    }
                    return new Action.None();
                }

                case "type":
                    live.Text(argument);
                    return new Action.None();

                case "shot":
                    if (live.Ui.OptionsMode && (live.Ui.OptionsPanel.IsTransitioning || live.Ui.SoundOptions.IsTransitioning))
                    {
                        _index -= 1;
                        return new Action.None();
                    }
                    return new Action.Shot(argument);

                case "quit":
                    live.QuitRequested = true;
                    return new Action.None();

                default:
                    Fail($"line {number}: unknown command {words[0]}", live);
                    return new Action.None();
            }
        }

        return new Action.None();
    }

    private void HandleWaitfor(int number, string[] words, LiveController live)
    {
        if (words.Length > 2 && words[1] is ("matches" or "spent" or "greener"))
        {
            if (!_constructionSnapshots.ContainsKey(words[2]))
            {
                Fail($"line {number}: unknown snapshot {words[2]}", live);
                return;
            }
            _condition = ($"{words[1]} {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 2 && words[1] == "pointer")
        {
            _condition = ($"pointer {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 2 && words[1] == "placement" && words[2] == "valid")
        {
            _condition = ("placement-valid", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 2 && words[1] is ("map" or "camera" or "absent"))
        {
            if (!TryParseInt(words[2], out _))
            {
                Fail($"line {number}: {words[1]} needs a number", live);
                return;
            }
            _condition = ($"{words[1]} {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 3 && words[1] == "walked")
        {
            if (!TryParseDouble(words[2], out _) || !TryParseDouble(words[3], out _))
            {
                Fail($"line {number}: walked needs x y", live);
                return;
            }
            _condition = ($"walked {words[2]} {words[3]}", _frame + ParseTimeoutFrames(words, 4, 900));
            return;
        }

        if (words.Length > 2 && words[1] == "placed")
        {
            if (!TryParseInt(words[2], out _))
            {
                Fail($"line {number}: placed needs a direction", live);
                return;
            }
            _objectsBefore = [.. _placedBefore];
            _condition = ($"placed {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 2 && words[1] == "sound")
        {
            _condition = ($"sound {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        if (words.Length > 2 && words[1] == "anim")
        {
            _condition = ($"anim {words[2]}", _frame + ParseTimeoutFrames(words, 3, 1800));
            return;
        }

        int frames = ParseTimeoutFrames(words, 2, 1800);
        string conditionName = words.Length > 1 ? words[1] : "";
        if (!SimpleWaitforConditions.Contains(conditionName))
        {
            Fail($"line {number}: unknown condition", live);
            return;
        }
        _condition = (words[1], _frame + frames);
    }

    private void HandleKey(string argument, LiveController live)
    {
        _revisionBefore = live.Renderer.SceneRevisionNumber;
        _soundsBefore = live.ScriptUISounds?.Count ?? 0;

        switch (argument)
        {
            case "ctrl+left":
            case "ctrl-left":
                SendDeleteModifiedKey(new LiveController.Key.Left(), live);
                return;
            case "ctrl+right":
            case "ctrl-right":
                SendDeleteModifiedKey(new LiveController.Key.Right(), live);
                return;
            case "ctrl+up":
            case "ctrl-up":
                SendDeleteModifiedKey(new LiveController.Key.Up(), live);
                return;
            case "ctrl+down":
            case "ctrl-down":
                SendDeleteModifiedKey(new LiveController.Key.Down(), live);
                return;
        }

        LiveController.Key key = argument switch
        {
            "escape" => new LiveController.Key.Escape(),
            "enter" => new LiveController.Key.Enter(),
            "tab" => new LiveController.Key.Tab(),
            "backspace" => new LiveController.Key.Backspace(),
            "space" => new LiveController.Key.Character(" "),
            "home" => new LiveController.Key.Home(),
            "end" => new LiveController.Key.End(),
            "left" => new LiveController.Key.Left(),
            "right" => new LiveController.Key.Right(),
            "up" => new LiveController.Key.Up(),
            "down" => new LiveController.Key.Down(),
            "f1" or "f2" or "f3" or "f4" or "f5" =>
                new LiveController.Key.Function(int.Parse(argument.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture)),
            _ => new LiveController.Key.Character(argument.ToLowerInvariant())
        };
        _ = live.HandleKeyDown(key);
    }

    private void SendDeleteModifiedKey(LiveController.Key key, LiveController live)
    {
        live.ScriptDeleteModifier = true;
        _ = live.HandleKeyDown(key);
        live.ScriptDeleteModifier = _held.Contains("ctrl") || _held.Contains("alt");
    }

    private static List<int> ScriptObjectMatches(string[] words, LiveController live)
    {
        int? part = words.Length > 2 && TryParseInt(words[^1], out int parsedPart) ? parsedPart : null;
        int needleCount = words.Length - 1 - (part is null ? 0 : 1);
        string needle = needleCount > 0
            ? string.Join(' ', words, 1, needleCount).ToLowerInvariant()
            : "";

        // Placed objects on the active floor take precedence over sub-tiles.
        IReadOnlyDictionary<int, int> placed = live.ScriptEditableObjects;
        List<(int Id, string Name)> named = [];
        if (needle.Length > 0)
        {
            foreach ((int id, string name) in live.ScriptObjects)
            {
                if (name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                {
                    named.Add((id, name));
                }
            }
        }

        List<int> matches = new(named.Count);
        foreach ((int id, _) in named)
        {
            if (placed.ContainsKey(id) && live.ScriptSceneContains(id)) matches.Add(id);
        }
        foreach ((int id, _) in named)
        {
            if (!placed.ContainsKey(id) && live.ScriptSceneContains(id)) matches.Add(id);
        }
        foreach ((int id, _) in named)
        {
            if (placed.ContainsKey(id) && !live.ScriptSceneContains(id)) matches.Add(id);
        }
        foreach ((int id, _) in named)
        {
            if (!placed.ContainsKey(id) && !live.ScriptSceneContains(id)) matches.Add(id);
        }

        if (part is { } partNumber)
        {
            int idx = partNumber - 1;
            return (uint)idx < (uint)matches.Count ? [matches[idx]] : [];
        }
        return matches;
    }

    private void Click(CGPoint point, LiveController live)
    {
        _placedBefore = [.. live.ScriptEditableObjects.Keys];
        _revisionBefore = live.Renderer.SceneRevisionNumber;
        _soundsBefore = live.ScriptUISounds?.Count ?? 0;
        live.Hover(point, live.ScriptCanvas);
        Press(point, live);
        live.Release(point);
    }

    private void Press(CGPoint point, LiveController live)
    {
        live.Press(point, live.ScriptCanvas, _held.Contains("shift"), _held.Contains("alt"), _held.Contains("ctrl"));
    }

    private bool Holds(string name, LiveController live)
    {
        switch (name)
        {
            case "illustrated": return live.Ui.GameDialog?.Illustration is not null;
            case "album": return live.ScriptScreen == "album";
            case "lot":
            {
                IReadOnlyList<int> simIds = live.ScriptSimIDs;
                return live.Ui.ShowsLot && live.ScriptSceneReady && simIds.Count > 0 && simIds.All(live.ScriptSceneContains);
            }
            case "scene": return live.Ui.ShowsLot && live.ScriptSceneReady;
            case "menu": return live.Ui.PieMenu is not null;
            case "head": return live.ScriptPieHeadSettled;
            case "panel": return live.Ui.PluginPanel is not null;
            case "dialog": return live.Ui.GameDialog is { Submitting: false };
            case "preview": return live.ScriptPreviewsReady;
            case "popup": return live.Ui.CatalogPopup.IsFullyOpen;
            case "catalog": return live.ScriptCatalogReady;
            case "placement": return live.ScriptPlacementChecked;
            case "placement-valid": return live.ScriptPlacementValid;
            case "cursor": return live.ScriptBuildCursorShown;
            case "built": return live.Renderer.SceneRevisionNumber > _revisionBefore;
            case "canundo": return live.Ui.Gameplay.State?.CanUndo == true;
            case "canredo": return live.Ui.Gameplay.State?.CanRedo == true;
            case "idle": return live.ScriptSimIdle;
            case "freewill": return live.ScriptFreeWill == true;
            case "quiet":
                // Startup letters queue minutes apart; game clock must advance between them.
                if (live.Ui.GameDialog is not null || _frame - _lastDialogFrame < 30) return false;
                if (!live.Ui.ShowsLot) return true;
                if (_lastDialogClock is not { } since || live.Ui.ClockMinutes is not { } now) return false;
                return (now - since + 1440) % 1440 >= 3;
            case "neighborhood":
            case "picker":
            case "creation":
                return live.Ui.GameDialog is null && live.ScriptScreen == name;
        }

        if (name.StartsWith("matches ", StringComparison.Ordinal) ||
            name.StartsWith("spent ", StringComparison.Ordinal) ||
            name.StartsWith("greener ", StringComparison.Ordinal))
        {
            string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (live.Renderer.SceneRevisionNumber <= _revisionBefore ||
                words.Length < 2 ||
                !_constructionSnapshots.TryGetValue(words[1], out ConstructionSnapshot? saved) ||
                ConstructionSnapshot.TryCreate(live) is not { } current)
            {
                return false;
            }
            if (words[0] == "matches") return current == saved;
            if (words[0] == "greener")
            {
                return current.Grass.Length == saved.Grass.Length &&
                       current.Grass.Sum(static b => (int)b) < saved.Grass.Sum(static b => (int)b) &&
                       current.Heights.SequenceEqual(saved.Heights);
            }
            return current.Budget < saved.Budget &&
                   (!current.Walls.SequenceEqual(saved.Walls, StringComparer.Ordinal) ||
                    !current.Floors.SequenceEqual(saved.Floors, StringComparer.Ordinal) ||
                    !current.Heights.SequenceEqual(saved.Heights) ||
                    !current.Grass.SequenceEqual(saved.Grass));
        }

        if (name.StartsWith("pointer ", StringComparison.Ordinal))
        {
            return string.Equals(live.CursorName, name[8..], StringComparison.Ordinal);
        }

        if (name.StartsWith("placed ", StringComparison.Ordinal))
        {
            // Simulation state can lead the rendered scene, so verify presence in both.
            string expectedDirection = name[7..];
            foreach ((int key, int direction) in live.ScriptEditableObjects)
            {
                if (!_objectsBefore.Contains(key) &&
                    direction.ToString(CultureInfo.InvariantCulture) == expectedDirection &&
                    live.ScriptSceneContains(key))
                {
                    return true;
                }
            }
            return false;
        }

        if (name.StartsWith("walked ", StringComparison.Ordinal))
        {
            string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 ||
                !TryParseDouble(parts[1], out double targetX) ||
                !TryParseDouble(parts[2], out double targetY) ||
                live.ScriptSimPosition is not { } position)
            {
                return false;
            }
            return Math.Abs(position.X - targetX) < 0.25 && Math.Abs(position.Y - targetY) < 0.25;
        }

        if (name.StartsWith("tilecontents ", StringComparison.Ordinal))
        {
            string[] values = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 3 || !TryParseInt(values[2], out int expectedId))
            {
                return false;
            }
            return live.ScriptTileTraversalPassed(values[1], expectedId);
        }

        if (name.StartsWith("absent ", StringComparison.Ordinal))
        {
            if (!TryParseInt(name[7..], out int id) || !live.ScriptSceneReady)
            {
                return false;
            }
            return !live.ScriptObjects.Any(o => o.Id == id) && !live.ScriptSceneContains(id);
        }

        if (name.StartsWith("map ", StringComparison.Ordinal))
        {
            return !live.IsLoadingNeighborhood &&
                   live.Ui.GameDialog is null &&
                   live.Ui.Neighborhood is { } neighborhood &&
                   neighborhood.Mode.ToString(CultureInfo.InvariantCulture) == name[4..];
        }

        if (name.StartsWith("camera ", StringComparison.Ordinal))
        {
            return live.Renderer.Scene is not null &&
                   ((int)live.Renderer.CameraOrientation).ToString(CultureInfo.InvariantCulture) == name[7..];
        }

        if (name.StartsWith("sound ", StringComparison.Ordinal))
        {
            if (live.ScriptUISounds is not { } sounds)
            {
                return false;
            }
            string expectedSound = name[6..];
            for (int i = _soundsBefore; i < sounds.Count; i++)
            {
                if (string.Equals(sounds[i], expectedSound, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        if (name.StartsWith("anim ", StringComparison.Ordinal))
        {
            string expectedAnim = name[5..];
            return live.ScriptSimAnimations.Any(a => a.Contains(expectedAnim, StringComparison.Ordinal));
        }

        return live.Ui.GameDialog is not { Submitting: false };
    }

    private void Fail(string message, LiveController live)
    {
        Failed = message;
        Log($"script failed: {message}");
        live.QuitRequested = true;
    }

    private static (string Label, CGRect Rect)? FindControlContains(IReadOnlyList<(string Label, CGRect Rect)> controls, string needle)
    {
        foreach (var control in controls)
        {
            if (control.Label.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
            {
                return control;
            }
        }
        return null;
    }

    private static (string Label, CGRect Rect)? FindControlForHover(IReadOnlyList<(string Label, CGRect Rect)> controls, string needle)
    {
        foreach (var control in controls)
        {
            if (control.Label.ToLowerInvariant().EndsWith(needle, StringComparison.Ordinal))
            {
                return control;
            }
        }
        return FindControlContains(controls, needle);
    }

    private static int ParseTimeoutFrames(string[] words, int index, int fallback) =>
        words.Length > index && TryParseInt(words[index], out int frames) ? frames : fallback;

    private const NumberStyles StrictFloatStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    private static bool TryParseInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);

    private static bool TryParseDouble(string value, out double result) =>
        double.TryParse(value, StrictFloatStyles, CultureInfo.InvariantCulture, out result);

    private static bool TryParseFloat(string value, out float result) =>
        float.TryParse(value, StrictFloatStyles, CultureInfo.InvariantCulture, out result);

    private static void Log(string message) =>
        Console.Error.WriteLine($"SimsSDL: {message}");
}
