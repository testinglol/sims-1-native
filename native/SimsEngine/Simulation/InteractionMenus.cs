using System.Globalization;
using System.Text.RegularExpressions;
using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

public sealed partial class InteractionMenuContext
{
    [GeneratedRegex(
        @"\$(?<name>TokenNameLocal|NameLocal|NameAttrib|CatalogLocal|FameTitleLocal|TimeLocal|JobOffer|JobDesc|NeighborLocal|Neighbor|Attrib|Local|Job|Grade|FamilyAssets|Family|Object|Me)(?=[:]|[^A-Za-z0-9]|$)(?::(?<first>[0-9]+)(?::(?<second>[0-9]+))?)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex MenuTokenRegex();

    private readonly SimulationWorld world;
    public InteractionMenuContext(SimulationWorld world) => this.world = world;
    public ActionQueueSlot? Current { get; internal set; }
    public List<ActionQueueSlot> Choices { get; } = [];
    internal void AddBase(ActionQueueSlot action)
    {
        var copy = action.Copy();
        copy.ActionText ??= world.EffectiveInteractions(action.Owner)?.Interactions.FirstOrDefault(entry => entry.TtaIndex == action.RawDword10)?.Name
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

    /// <summary>Expands dynamic string tokens in dialog text, leaving unresolved tokens intact in the
    /// output.</summary>
    internal string Expand(string text, XObject actor, StackFrame frame)
        => SubstituteMenuText(text, actor, actor, frame, 0, strict: false);

    /// <summary>Expands dynamic menu tokens with up to two numeric arguments, supporting 18 reference token
    /// types without dollar-escape sequences while preserving unresolved tokens.</summary>
    private string SubstituteMenuText(string text, XObject? actor, XObject? owner, StackFrame frame, int depth, bool strict = true)
    {
        if (depth > 10) return text;
        if (depth == 0) { FamilyOutput = 0; NeighborOutput = 0; }
        var person = actor as XPerson;
        // Expands $Me using custom instance name if present, otherwise catalog name.
        var actorName = (!string.IsNullOrEmpty(actor?.Name) ? actor.Name : null) ?? actor?.TypeRecord?.CatalogName ??
            (person is not null && world.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor)
                ? neighbor.Name : null) ?? actor?.TypeRecord?.Name;
        var stackObject = person is not null
            ? world.ObjectByStackObject(person, frame.StackObject) ?? owner
            : world.ObjectById(frame.StackObject) ?? owner;
        var objectName = ObjectName(stackObject);
        var neighborLocalAborted = false;
        var expanded = MenuTokenRegex().Replace(text,
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
            });
        return expanded;
    }

    /// <summary>Preserves recognized menu tokens whose arguments reference invalid local variables instead
    /// of failing menu creation.</summary>
    private sealed class UnresolvedMenuToken(string message) : NotSupportedException(message);

    /// <summary>Retrieves the school grade string from career resources by 1-based index, returning empty
    /// text if the index is out of bounds.</summary>
    private string Grade(short[] locals, int index) => (uint)index >= locals.Length ? string.Empty
        : world.CareerHost is CareerCatalog careers ? careers.GradeString(index, world.Language) ?? string.Empty : string.Empty;

    private string? FameTitle(int level) => (uint)level > 10 ? string.Empty : world.MenuTextSources?.FameTitle(level);

    /// <summary>Resolves the family name for the specified neighbor and records the family ID in the output
    /// record.</summary>
    private string? FamilyOfNeighbor(short oneBasedNeighborId)
    {
        var neighbor = world.NeighborManagerEntry(oneBasedNeighborId);
        if (neighbor is null) return null;
        var number = neighbor.PersonData[(int)PersonData.FamilyNumber];
        var name = world.MenuTextSources?.FamilyName(number);
        if (name is not null) FamilyOutput = checked((short)number);
        return name;
    }

    private static int FamilyNumberOfLocal(short value) => value;

    /// <summary>Contains the family ID resolved during string token substitution, or zero if unreferenced.</summary>
    public short FamilyOutput { get; private set; }

    /// <summary>Contains the 1-based neighbor ID resolved during string token substitution, or zero if
    /// unreferenced.</summary>
    public short NeighborOutput { get; private set; }

    /// <summary>Expands the $NeighborLocal token with the neighbor and family name, formatting family first
    /// for East Asian languages. A missing neighbor table entry aborts subsequent evaluations.</summary>
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

    // Resolves an actor's inventory token by zero-based index to an object GUID, returning catalog string
    // 0.
    private string TokenNameLocal(XPerson? person, int index)
    {
        if (person is null || index < 0 || world.ObjectRegistry is null) return string.Empty;
        var neighborId = unchecked((short)person[PersonData.NeighborId]);
        if (!world.NeighborInventories.TryGetValue(neighborId, out var inventory) || index >= inventory.Count)
            return string.Empty;
        var entry = world.ObjectRegistry.Find(inventory[index].GUID);
        return entry?.CatalogString(0, world.Language) ?? string.Empty;
    }

    /// <summary>Reads the career and level from the command's local variables, falling back to the Sim's
    /// current job type and promotion level if either argument is omitted.</summary>
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
    /// <summary>Resolves the clicked object's interaction-group lead when it belongs to a multi-tile group,
    /// otherwise preserving the clicked object itself.</summary>
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
            if (clicked.MultiTileMasterId == 0)
            {
                // Saved lots occasionally omit MultiTileMasterId on a sub-part while the head still links
                // via MultiTileNextId.
                foreach (var obj in world.WalkOrder)
                {
                    if (obj.MultiTileNextId == 0) continue;
                    visited.Clear();
                    XObject? groupLead = null;
                    var containsClicked = false;
                    for (var part = obj; part is not null && visited.Add(part.Id);
                         part = part.MultiTileNextId == 0 ? null : world.ObjectById(part.MultiTileNextId))
                    {
                        if (groupLead is null &&
                            part.Definition is { Length: > (int)ObjdField.InteractionGroup } partDef &&
                            partDef[(int)ObjdField.InteractionGroup] == -group)
                            groupLead = part;
                        if (part.Id == clicked.Id) containsClicked = true;
                        if (containsClicked && groupLead is not null) return groupLead;
                    }
                }
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

            // When a clicked object yields no menu entries, walks other objects on the same tile whose OBJD
            // field 91 bit 0 is set and selector type is not 8.
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
        // Pet actors can only inspect interaction targets matching type GUID 0x7c4.
        if ((person.AnimatorBodyStringMode & 6) != 0)
        {
            var guid = (ushort)target.Definition[(int)ObjdField.Guid1] | ((uint)(ushort)target.Definition[(int)ObjdField.Guid2] << 16);
            if (target.TypeRecord?.Key != 0x7c4 && guid != 0x7c4u)
                return;
        }

        // Iterates TTAB interactions in index order without alphabetical sorting.
        var entries = target.TypeRecord?.Interactions?.Interactions;
        if (entries is null) return;

        var candidates = entries.Select(entry => new ActionQueueSlot
            {
                Person = person, Owner = target, Secondary = target, RawDword10 = entry.TtaIndex,
                Threshold = 50, ActionTree = entry.ActionTree, Flags = (entry.Flags & 0x100) != 0 ? 1 : 0,
            }).ToArray();
        InteractionTestEvaluator.Evaluate(world, person, candidates, false);

        // Offers the target Sim's person-to-person interactions whose joining index matches the actor's
        // active joinable interaction.
        if (target is XPerson && person.CurrentActionRecord is { Owner: not null } active)
        {
            var activeEntry = world.EffectiveInteractions(active.Owner)?.Interactions
                .FirstOrDefault(entry => entry.TtaIndex == active.RawDword10 && (entry.Flags & 2) != 0);
            if (activeEntry is not null)
            {
                var joinCandidates = entries
                    .Where(entry => entry.JoiningIndex == activeEntry.TtaIndex)
                    .Select(entry => new ActionQueueSlot
                        {
                            Person = person, Owner = target, Secondary = target,
                            // For person-to-person interactions, sets record kind 1 and places the active
                            // Sim's manager index in parameter 0 before running the guard.
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
