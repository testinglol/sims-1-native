using System.Globalization;
using System.Text.RegularExpressions;
using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The DAT_006e50e0/50ac/50a8 manual-menu context; FUN_004588f5 appends named candidate copies.</summary>
public sealed class InteractionMenuContext
{
    private readonly SimulationWorld world;
    public InteractionMenuContext(SimulationWorld world) => this.world = world;
    public ActionQueueSlot? Current { get; internal set; }
    public List<ActionQueueSlot> Choices { get; } = [];
    internal void AddBase(ActionQueueSlot action)
    {
        var copy = action.Copy();
        copy.ActionText ??= action.Owner?.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10)?.Name
            ?? throw new NotSupportedException("Native interaction name is unavailable in TTAs or the action BHAV label.");
        Choices.Add(copy);
    }
    public void Add(string text, short stackObject, StackFrame frame)
    {
        var copy = Current?.Copy() ?? throw new InvalidOperationException("No native menu candidate is being checked.");
        copy.Flags = (copy.Flags & ~InteractionTestEvaluator.Hidden) | InteractionTestEvaluator.Evaluated | InteractionTestEvaluator.Passed;
        copy.ActionText = SubstituteMenuText(text, copy.Person, copy.Owner, frame, 0);
        copy.Parameters[0] = NeighborOutput != 0 ? NeighborOutput : stackObject;
        if (FamilyOutput != 0) copy.Parameters[1] = FamilyOutput;
        Choices.Add(copy);
    }

    /// <summary>Expands the dynamic string commands of a dialog string the way FUN_004360c1 does through FUN_0044bf5a, with
    /// <paramref name="actor"/> as the running object. Unlike a menu label, a command that cannot be resolved stays in the
    /// text, as in Sims.exe.</summary>
    internal string Expand(string text, XObject actor, StackFrame frame)
        => SubstituteMenuText(text, actor, actor, frame, 0, strict: false);

    /// <summary>
    /// FUN_0044bf5a parses dynamic string commands while Sims.exe builds candidate labels and dialog text; each command
    /// takes up to two digit-only ":number" arguments (FUN_0044cb36 collects bytes accepted by
    /// FUN_006368c3's numeric-character class before conversion). REA found 16 Windows literal-table entries at
    /// 0x6c4044–0x6c4100. The Mac reference parser's 18-entry table also includes $Me and $Job (ObjectSim.s and
    /// data.s), so the Windows literal list alone is not the complete cross-version spelling inventory. This parser
    /// implements those 18 reference spellings. There is no "$$" escape in Sims.exe: "$$Local:2" keeps the first "$" and
    /// expands "$Local:2". Commands that cannot be resolved stay in the text, matching the original parser behavior.
    /// </summary>
    private string SubstituteMenuText(string text, XObject? actor, XObject? owner, StackFrame frame, int depth, bool strict = true)
    {
        if (depth > 10) return text;
        if (depth == 0) { FamilyOutput = 0; NeighborOutput = 0; }
        var person = actor as XPerson;
        // Mac ObjectSim.s 0x100EF9AC–0x100EF9D8 expands $Me using custom instance Name when set,
        // otherwise the actor selector's GetCatalogName. __sinit_:ObjectSim_cpp binds that token at 0x100F2E18–0x100F2E30.
        var actorName = (!string.IsNullOrEmpty(actor?.Name) ? actor.Name : null) ?? actor?.TypeRecord?.CatalogName ??
            (person is not null && world.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor)
                ? neighbor.Name : null) ?? actor?.TypeRecord?.Name;
        var stackObject = person is not null
            ? world.ObjectByStackObject(person, frame.StackObject) ?? owner
            : world.ObjectById(frame.StackObject) ?? owner;
        var objectName = ObjectName(stackObject);
        var neighborLocalAborted = false;
        var expanded = Regex.Replace(text,
            @"\$(?<name>TokenNameLocal|NameLocal|NameAttrib|CatalogLocal|FameTitleLocal|TimeLocal|JobOffer|JobDesc|NeighborLocal|Neighbor|Attrib|Local|Job|Grade|FamilyAssets|Family|Object|Me)(?=[:]|[^A-Za-z0-9]|$)(?::(?<first>[0-9]+)(?::(?<second>[0-9]+))?)?",
            match =>
            {
                var name = match.Groups["name"].Value;
                var first = match.Groups["first"].Success ? int.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture) : -1;
                var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture) : -1;
                try
                {
                    return name switch
                    {
                        "Me" => actorName ?? string.Empty,
                        "Object" => objectName ?? string.Empty,
                        "NameLocal" => ObjectName(world.ObjectById(Read(frame.Locals, first, "$NameLocal"))) ?? string.Empty,
                        "NameAttrib" => ObjectName(world.ObjectById(Read(stackObject?.Data, first, "$NameAttrib"))) ?? match.Value,
                        "TokenNameLocal" => TokenNameLocal(person, Read(frame.Locals, first, "$TokenNameLocal")),
                        "CatalogLocal" => world.ObjectById(Read(frame.Locals, first, "$CatalogLocal"))?.TypeRecord?.CatalogDescription ?? string.Empty,
                        "Local" => Read(frame.Locals, first, "$Local").ToString(CultureInfo.CurrentCulture),
                        "Attrib" => stackObject is null ? string.Empty : Read(stackObject.Data, first, "$Attrib").ToString(CultureInfo.CurrentCulture),
                        "TimeLocal" => FormatTime(frame.Locals, first, second),
                        "JobOffer" => JobOffer(person, frame.Locals, first, second),
                        "Job" => JobString(person, frame.Locals, first, second, false),
                        "JobDesc" => JobString(person, frame.Locals, first, second, true),
                        "Neighbor" => NeighborName(frame.StackObject),
                        "NeighborLocal" => neighborLocalAborted ? match.Value
                            : NeighborLocal(Read(frame.Locals, first, "$NeighborLocal"), ref neighborLocalAborted) ?? match.Value,
                        "Family" => FamilyOfNeighbor(frame.StackObject) ?? match.Value,
                        "FamilyAssets" => world.MenuTextSources?.FamilyAssets(FamilyNumberOfLocal(Read(frame.Locals, first, "$FamilyAssets")))
                            ?.ToString(CultureInfo.InvariantCulture) ?? match.Value,
                        "Grade" => Grade(frame.Locals, first),
                        "FameTitleLocal" => FameTitle(Read(frame.Locals, first, "$FameTitleLocal")) ?? match.Value,
                        _ => match.Value,
                    };
                }
                catch (UnresolvedMenuToken) { return match.Value; }
                catch (NotSupportedException) when (!strict) { return match.Value; }
            }, RegexOptions.CultureInvariant);
        return expanded;
    }

    /// <summary>Sims.exe FUN_0044cb36 finds a command by name and each handler skips an argument that is not a local of the
    /// frame, so a recognized command whose value cannot be read stays in the text instead of failing the menu.</summary>
    private sealed class UnresolvedMenuToken(string message) : NotSupportedException(message);

    /// <summary>FUN_004034e6: work.iff STR# 0x1001 entry <paramref name="index"/> + 1 (one-based), the empty string when
    /// the table is shorter. The command is skipped when the index is not a local of the frame (FUN_0044bf5a).</summary>
    private string Grade(short[] locals, int index) => (uint)index >= locals.Length ? string.Empty
        : world.CareerHost is CareerCatalog careers ? careers.GradeString(index, world.Language) ?? string.Empty : string.Empty;

    private string? FameTitle(int level) => (uint)level > 10 ? string.Empty : world.MenuTextSources?.FameTitle(level);

    /// <summary>$Family: the name of the family of the neighbor the stack object names (FUN_0042573e, neighbor +0xee).
    /// It also stores the family into the second word of the output record (<see cref="FamilyOutput"/>).</summary>
    private string? FamilyOfNeighbor(short oneBasedNeighborId)
    {
        var neighbor = world.NeighborManagerEntry(oneBasedNeighborId);
        if (neighbor is null) return null;
        var number = neighbor.PersonData[(int)PersonData.FamilyNumber];
        var name = world.MenuTextSources?.FamilyName(number);
        if (name is not null) FamilyOutput = checked((short)number);
        return name;
    }

    /// <summary>The family number a <c>$FamilyAssets</c> local holds (FUN_00425db3 searches the family list by it).</summary>
    private static int FamilyNumberOfLocal(short value) => value;

    /// <summary>The second word of the output record FUN_0044bf5a fills for its caller: the family <c>$Family</c> named
    /// (family +0x10c), or 0 when no <c>$Family</c> was resolved. FUN_0044cc47 passes it on with the message.</summary>
    public short FamilyOutput { get; private set; }

    /// <summary>The first word of that record: the one-based neighbor index <c>$Neighbor</c> named, or 0.</summary>
    public short NeighborOutput { get; private set; }

    /// <summary>$NeighborLocal: FUN_0042573e finds the manager entry, FUN_0045741b takes its type-record key and FUN_004e988f
    /// finds that key in the neighbor table; a key that is missing there stops every later $NeighborLocal command
    /// (<paramref name="aborted"/>), while a missing manager entry only skips this one. FUN_004e9d82 then writes the
    /// neighbor's name and its family's name, family first when the interface asks for it (vtable +0x84, taken to be the
    /// East Asian languages).</summary>
    private string? NeighborLocal(int index, ref bool aborted)
    {
        var entry = world.NeighborTableEntry(unchecked((short)index));
        if (entry is null) return null;
        var key = entry.TypeRecord?.Key ?? 0;
        var neighbor = world.NeighborWalkOrder.FirstOrDefault(candidate => candidate is not null && candidate.TypeRecordKey == key);
        if (neighbor is null) { aborted = true; return null; }
        var name = neighbor.Name ?? string.Empty;
        var family = world.MenuTextSources?.FamilyName(neighbor.PersonData[(int)PersonData.FamilyNumber]);
        if (string.IsNullOrEmpty(family)) return name;
        if (string.IsNullOrEmpty(name)) return null;
        return world.Language is 15 or 17 or 18 or 20 ? family + " " + name : name + " " + family;
    }

    // Sims.exe cXObject::ParseUIString gets the actor's inventory by neighbor id, treats the
    // command's local as a zero-based token index, then resolves that token GUID through the
    // object folder and returns selector catalog string 0 (Mac ParseUIString, 0x100EFE24+).
    private string TokenNameLocal(XPerson? person, int index)
    {
        if (person is null || index < 0 || world.ObjectRegistry is null) return string.Empty;
        var neighborId = unchecked((short)person[PersonData.NeighborId]);
        if (!world.NeighborInventories.TryGetValue(neighborId, out var inventory) || index >= inventory.Count)
            return string.Empty;
        var entry = world.ObjectRegistry.Find(inventory[index].GUID);
        return entry?.CatalogString(0, world.Language) ?? string.Empty;
    }

    /// <summary>FUN_0044bf5a reads the career and level from the command's two locals; when either argument is missing
    /// it uses the running Sim's own job type and promotion level (person +0x5fc and +0x5fe).</summary>
    private static (short Career, int Level) JobArguments(XPerson? person, short[] locals, int careerIndex, int levelIndex, string token)
    {
        if (careerIndex < 0 || levelIndex < 0)
            return person is null ? throw new NotSupportedException($"Native dynamic interaction token {token} needs a Sim.")
                : (person[PersonData.JobType], person[PersonData.JobPromotionLevel]);
        return (Read(locals, careerIndex, $"{token}:career"), Read(locals, levelIndex, $"{token}:level"));
    }

    private string JobOffer(XPerson? person, short[] locals, int careerIndex, int levelIndex)
    {
        var (career, level) = JobArguments(person, locals, careerIndex, levelIndex, "$JobOffer");
        if (world.CareerHost is not CareerCatalog careers) return string.Empty;
        return careers.JobText(career, level, JobTextKind.Offer, IsFemale(person), world.Language) is { Length: > 0 } text
            ? text : careers.JobOffer(career, level) ?? string.Empty;
    }

    private string JobString(XPerson? person, short[] locals, int careerIndex, int levelIndex, bool description)
    {
        if (world.CareerHost is not CareerCatalog careers) return string.Empty;
        var (career, level) = JobArguments(person, locals, careerIndex, levelIndex, description ? "$JobDesc" : "$Job");
        return careers.JobText(career, level, description ? JobTextKind.Description : JobTextKind.Title, IsFemale(person), world.Language) ?? string.Empty;
    }

    /// <summary>Person +0x60e bit 0 (FUN_0044bf5a), the Gender field.</summary>
    private static bool IsFemale(XPerson? person) => person is not null && (person[PersonData.Gender] & 1) != 0;

    private string NeighborName(short oneBasedNeighborId)
    {
        var neighbor = world.NeighborManagerEntry(oneBasedNeighborId)
            ?? (world.Neighbors.TryGetValue(oneBasedNeighborId, out var byId) ? byId : null);
        if (neighbor is null) return "Unknown";
        if (neighbor.TypeRecordKey != 0) NeighborOutput = oneBasedNeighborId;
        var objectName = neighbor.TypeRecordKey == 0 ? null : ObjectName(world.ObjectByTypeRecordKey(neighbor.TypeRecordKey));
        return objectName ?? neighbor.Name ?? "Unknown";
    }

    private static string? ObjectName(XObject? item)
    {
        if (item is null) return null;
        return !string.IsNullOrEmpty(item.Name) ? item.Name :
            !string.IsNullOrEmpty(item.TypeRecord?.CatalogName) ? item.TypeRecord.CatalogName :
            !string.IsNullOrEmpty(item.TypeRecord?.Name) ? item.TypeRecord.Name :
            item.TypeRecord?.Key.ToString("X8");
    }

    private static short Read(short[]? values, int index, string token)
    {
        if (values is null || (uint)index >= values.Length) throw new UnresolvedMenuToken($"Native dynamic interaction token {token}:{index} has no value.");
        return values[index];
    }

    private static string FormatTime(short[] values, int hourIndex, int minuteIndex)
    {
        var hours = Read(values, hourIndex, "$TimeLocal");
        if (hours is < 0 or >= 24)
            throw new UnresolvedMenuToken($"Native dynamic interaction token $TimeLocal hour {hours} is out of range.");
        var minutes = minuteIndex >= 0 && (uint)minuteIndex < values.Length && values[minuteIndex] is >= 0 and < 60
            ? values[minuteIndex]
            : (short)0;
        var suffix = hours >= 12 ? "pm" : "am";
        var displayHour = hours % 12 == 0 ? 12 : hours % 12;
        return $"{displayHour}:{minutes.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')}{suffix}";
    }

}

public static class InteractionMenus
{
    /// <summary>FUN_0045846c / FUN_0043532b resolves the clicked object's interaction-group lead (+0x10 == -group)
    /// when MasterId != 0 and InteractionGroup &gt; 0, otherwise preserving the clicked object itself.</summary>
    public static XObject Target(SimulationWorld world, XObject clicked)
    {
        if (clicked.Definition is { Length: > (int)ObjdField.MasterId } def &&
            def[(int)ObjdField.MasterId] != 0 &&
            def[(int)ObjdField.InteractionGroup] is > 0 and var group)
        {
            var lead = clicked.MultiTileMasterId == 0
                ? clicked : world.ObjectById(clicked.MultiTileMasterId);
            var visited = new HashSet<short>();
            for (var part = lead; part is not null && visited.Add(part.Id);
                 part = part.MultiTileNextId == 0 ? null : world.ObjectById(part.MultiTileNextId))
            {
                if (part.Definition is { Length: > (int)ObjdField.InteractionGroup } partDef &&
                    partDef[(int)ObjdField.InteractionGroup] == -group)
                    return part;
            }
        }
        return clicked;
    }

    public static IReadOnlyList<ActionQueueSlot> Read(SimulationWorld world, XPerson person, XObject clicked)
    {
        var target = Target(world, clicked);
        var context = new InteractionMenuContext(world);
        var savedContext = world.InteractionMenu;
        world.InteractionMenu = context;
        try
        {
            AppendForTarget(world, person, target, context);

            // cObjPickerTool::DoMenu (0048d4dc.c:27-39; Mac ObjPickerTool.s, 0x1017C108–0x1017C174) walks the
            // house ObjectIterator at the clicked object's tile when the clicked object yielded no menu entries,
            // including peers whose OBJD field 91 bit 0 is set and whose selector type is not 8.
            if (context.Choices.Count == 0)
            {
                var tileX = clicked.Position.X >> 4;
                var tileY = clicked.Position.Y >> 4;
                foreach (var peer in world.Objects.Values
                             .OrderBy(item => item.ClockManagerIndex)
                             .Where(item => item.Id != clicked.Id && item.Position.Height == clicked.Position.Height &&
                                            (item.Position.X >> 4) == tileX && (item.Position.Y >> 4) == tileY &&
                                            (item.TypeRecord?.ObjectType ?? (item.Definition.Length > (int)ObjdField.Type ? item.Definition[(int)ObjdField.Type] : 0)) != 8 &&
                                            item.Definition.Length > 91 && (item.Definition[91] & 1) != 0))
                {
                    var peerTarget = Target(world, peer);
                    if (peerTarget.Id != target.Id) AppendForTarget(world, person, peerTarget, context);
                }
            }

            return context.Choices;
        }
        finally { world.InteractionMenu = savedContext; }
    }

    private static void AppendForTarget(SimulationWorld world, XPerson person, XObject target,
        InteractionMenuContext context)
    {
        // FUN_00458708 (00458708.c lines 24-38) & cObjPickerTool::DoMenu (0048d6b6.c lines 32-138):
        // iterate TTAB interactions in index order (0..count-1) without alphabetical sorting.
        var entries = target.TypeRecord?.Interactions?.Interactions;
        if (entries is null) return;

        var candidates = entries.Select(entry => new ActionQueueSlot
            {
                Person = person, Owner = target, Secondary = target, RawDword10 = entry.TtaIndex,
                Threshold = 50, ActionTree = entry.ActionTree, Flags = (entry.Flags & 0x100) != 0 ? 1 : 0,
            }).ToArray();
        InteractionTestEvaluator.Evaluate(world, person, candidates, false);

        // ObjTestSim::AppendInteractionsForMenu also offers this target Sim's
        // person-to-person TTAB entries whose JoiningIndex points at the actor's
        // currently running joinable interaction (Mac ObjTestSim.s, 0x100FD214–0x100FD2E8).
        if (target is XPerson && person.CurrentActionRecord is { Owner: not null } active)
        {
            var activeEntry = active.Owner.TypeRecord?.Interactions?.Interactions
                .FirstOrDefault(entry => entry.TtaIndex == active.RawDword10 && (entry.Flags & 2) != 0);
            if (activeEntry is not null)
            {
                var joinCandidates = entries
                    .Where(entry => entry.JoiningIndex == activeEntry.TtaIndex)
                    .Select(entry => new ActionQueueSlot
                        {
                            Person = person, Owner = target, Secondary = target,
                            // ObjTestSim::AppendInteractionsForMenu uses the person-to-person Interaction
                            // constructor here. That constructor writes record kind 1 and SetStackVars puts
                            // the active Sim's manager index in parameter 0 before the guard runs.
                            RecordKind = 1,
                            RawDword10 = entry.TtaIndex, Threshold = 50,
                            ActionTree = entry.ActionTree,
                            Parameters = [person.ClockManagerIndex, 0, 0, 0],
                            Flags = (entry.Flags & 0x100) != 0 ? 1 : 0,
                        }).ToArray();
                InteractionTestEvaluator.Evaluate(world, person, joinCandidates, false);
            }
        }
    }

    internal static string? String(StackFrame frame, short table, int index, byte language)
    {
        if (frame.Table is not BehaviorScope scope) return null;
        // FUN_00401f6f returns the current code scope's private resource file (+8).
        return String(scope.Private, unchecked((ushort)table), unchecked((ushort)index), language);
    }

    internal static string? String(IffFile file, ushort table, ushort index, byte language)
    {
        var chunk = file.Find(ResourceType.Str, table);
        if (chunk is null) return null;
        var strings = StringTable.Read(file, chunk).ForLanguage(language);
        return index < strings.Count ? strings[index] : null;
    }
}
