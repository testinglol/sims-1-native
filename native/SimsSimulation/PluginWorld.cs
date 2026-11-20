using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsNative.Plugins;

namespace SimsSimulation;

internal sealed class PluginWorld(
    Func<SimulationWorld?> world,
    Func<short> house,
    Func<ushort?> family,
    Func<ObjectRegistry?> registry,
    Func<bool> paused,
    Func<IffFile?>? lot = null,
    Func<int>? lotSize = null,
    Func<XPerson, bool>? wearingOutdoorSuit = null,
    Action<short, int?>? setSuitOverride = null) : IPluginSimulation, PluginHost.IPluginWorld
{
    private static readonly HashSet<int> ChangeableMotives = [5, 6, 7, 8, 9, 14, 15];
    private Func<short, PluginObject, PluginInteraction, bool> interactionAllowed = (_, _, _) => true;

    private SimulationWorld World => world() ?? throw new InvalidOperationException("No native lot is loaded");

    public short House => house();
    public int Hours => World.Clock.Hour;
    public int Minutes => World.Clock.Minute;
    public bool Paused => paused();
    public int HouseholdFunds => world()?.FamilyBudget?.ReadFamilyBudget() ?? 0;

    private IEnumerable<XPerson> Persons() => World.Objects.Values.OfType<XPerson>();

    private IEnumerable<XPerson> Household() => family() is { } chunk
        ? Persons().Where(person => person[PersonData.IsGhost] == 0 && (ushort)person[PersonData.FamilyNumber] == chunk)
        : [];

    private XPerson HouseholdSim(short objectId) => Household().FirstOrDefault(person => person.Id == objectId)
        ?? throw new ArgumentException("Only household Sims on this lot can be read or changed");

    internal void SetInteractionFilter(Func<short, PluginObject, PluginInteraction, bool> filter) => interactionAllowed = filter;

    private string Name(XPerson person) =>
        !string.IsNullOrWhiteSpace(person.Name) ? person.Name
        : person.TypeRecord?.CatalogName is { } catalogName && !string.IsNullOrWhiteSpace(catalogName) ? catalogName
        : World.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor) && !string.IsNullOrWhiteSpace(neighbor.Name) ? neighbor.Name!
        : person.TypeRecord?.Name ?? "";

    public IReadOnlyList<PluginSim> HouseholdSims => Household()
        .Select(person => new PluginSim(person.Id, person[PersonData.NeighborId], Name(person), PersonAgeCategory.IsChild(person[PersonData.PersonAge]))
        {
            Animation = person.AnimationPlayback?.Clip.Name,
            WearingOutdoorSuit = wearingOutdoorSuit?.Invoke(person) ?? false,
        })
        .ToArray();

    public IReadOnlyList<string> GetQueuedActions(short simId)
    {
        var person = HouseholdSim(simId);
        var actions = new List<ActionQueueSlot>();
        if (person.CurrentActionRecord is { } current) actions.Add(current);
        actions.AddRange(person.ActionQueue.Where(action => action.Owner is not null));
        return actions.Select(action => action.ActionText ?? action.Owner?.TypeRecord?.Interactions?.Interactions
                .FirstOrDefault(entry => entry.TtaIndex == action.RawDword10)?.Name ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    public short GetMotive(short objectId, int motive)
    {
        if (motive is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(motive));
        return (short)HouseholdSim(objectId).Motives[motive];
    }

    public short ChangeMotive(short simId, int motive, int delta)
    {
        if (!ChangeableMotives.Contains(motive)) throw new ArgumentOutOfRangeException(nameof(motive), "Only the needs can be changed");
        if (delta is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(delta));
        var sim = HouseholdSim(simId);
        var value = (short)Math.Clamp((short)sim.Motives[motive] + delta, -100, 100);
        sim.Motives[motive] = value;
        return value;
    }

    private bool Outside(XObject item) => World.RoomIsOutside(World.RoomAtTile(item.Position.X >> 4, item.Position.Y >> 4, item.Position.Height));

    private bool Sheltered(XObject item) =>
        lot?.Invoke() is { } currentLot && world() is { } currentWorld &&
        SceneExporter.IsShelteredAt(currentLot, currentWorld, lotSize?.Invoke() ?? 64, item.Position);

    internal PluginObject Describe(XObject item) => new(item.Id,
        unchecked((uint)(item.TypeRecord?.Key ?? 0)), item is XPerson person ? Name(person) : item.TypeRecord?.Name ?? "",
        item.Position.X >> 4, item.Position.Y >> 4, item.Position.Height, item.MultiTileMasterId != 0 ? item.MultiTileMasterId : item.Id)
        {
            Outside = Outside(item),
            Sheltered = Sheltered(item),
        };

    public IReadOnlyList<PluginObject> Objects => World.Objects.Values.Select(Describe).ToArray();

    public IReadOnlyList<PluginInteraction> GetInteractions(short simId, short objectId)
    {
        var sim = HouseholdSim(simId);
        var clicked = World.ObjectById(objectId) ?? throw new ArgumentException($"No object {objectId} on this lot");
        var target = InteractionMenus.Target(World, clicked);
        if (target.Position.X < 0 || target.Position.Y < 0) return [];
        var described = Objects.First(item => item.ObjectId == target.Id);
        return InteractionMenus.Read(World, sim, target)
            .Select(action => new PluginInteraction(action.RawDword10, action.ActionText ?? "", action.Parameters[0], false))
            .Where(entry => interactionAllowed(simId, described, entry)).ToArray();
    }

    public bool TryQueueInteraction(short simId, short objectId, int interactionId, short param0 = 0, bool global = false)
    {
        if (global || interactionId is < 0 or > byte.MaxValue || World.HasVisibleBlockingDialog) return false;
        var sim = HouseholdSim(simId);
        var clicked = World.ObjectById(objectId);
        if (clicked is null) return false;
        var target = InteractionMenus.Target(World, clicked);
        var described = Objects.First(item => item.ObjectId == target.Id);
        var choice = InteractionMenus.Read(World, sim, target).FirstOrDefault(action =>
            action.RawDword10 == interactionId && action.Parameters[0] == param0);
        if (choice is null) return false;
        var offered = new PluginInteraction(choice.RawDword10, choice.ActionText ?? "", choice.Parameters[0], false);
        return interactionAllowed(simId, described, offered) && World.EnqueueInteractionAction(sim, choice);
    }

    public bool TryCharge(int amount)
    {
        if (world()?.FamilyBudget is not { } budget || amount <= 0 || amount > budget.ReadFamilyBudget()) return false;
        budget.AlterFamilyBudget(0, amount);
        return true;
    }

    public bool TryPay(int amount)
    {
        if (world()?.FamilyBudget is not { } budget || amount <= 0 || amount > 100_000 || budget.ReadFamilyBudget() > int.MaxValue - amount) return false;
        budget.AlterFamilyBudget(0, -amount);
        return true;
    }

    public PluginEnvironmentClock Clock(int house, int mapMode)
    {
        if (world() is not { } currentWorld) return new PluginEnvironmentClock(house, mapMode, 0, 0, 720, []);
        var currentLot = lot?.Invoke();
        var size = lotSize?.Invoke() ?? 64;
        var actors = Persons().Select(person => new PluginActor(person.Id, person.Position.X / 16f, person.Position.Y / 16f,
            person.Position.Height, false, PersonAgeCategory.IsChild(person[PersonData.PersonAge]), false)
        {
            Outside = Outside(person),
            Sheltered = currentLot is not null && SceneExporter.IsShelteredAt(currentLot, currentWorld, size, person.Position),
            WearingOutdoorSuit = wearingOutdoorSuit?.Invoke(person) ?? false,
        }).ToArray();
        return new PluginEnvironmentClock(house, mapMode, currentWorld.CurrentDate?.Month ?? 0, currentWorld.CurrentDate?.Day ?? 0,
            currentWorld.Clock.Hour * 60 + currentWorld.Clock.Minute, actors);
    }

    private XObject OwnedObject(string packId, short objectId, int? index)
    {
        var item = World.ObjectById(objectId) ?? throw new ArgumentException("Object no longer exists");
        var source = item.TypeRecord is { } type ? registry()?.Find(unchecked((uint)type.Key))?.Source : null;
        if (source is null || !source.Replace('\\', '/').Contains("/Downloads/" + packId + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Object attributes may only be changed on this pack's own objects");
        if (index is { } attribute && attribute >= item.Attributes.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return item;
    }

    public short? TryPlaceOwnedObject(string packId, uint guid, int tileX, int tileY, int level, short direction, short? clearForSimId)
    {
        if (world() is not { } currentWorld || level < 1) return null;
        var source = registry()?.Find(guid)?.Source;
        if (source is null || !source.Replace('\\', '/').Contains("/Downloads/" + packId + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only this pack's own objects may be placed");
        using var savedTypes = currentWorld.BeginSavedObjectTypeRegistrationScope();
        var group = currentWorld.ObjectGroupFactory?.Invoke(unchecked((int)guid));
        if (group is { Count: 0 }) return null;
        XObject root;
        if (group is { Count: > 0 })
        {
            root = group[0];
        }
        else
        {
            if (currentWorld.ObjectFactory?.Invoke(unchecked((int)guid)) is not { } single) return null;
            currentWorld.Add(single);
            currentWorld.InitializeCreatedObject(single);
            group = [single];
            root = single;
        }
        var actor = clearForSimId is { } simId ? currentWorld.ObjectById(simId) as XPerson : null;
        var maxCoord = (lotSize?.Invoke() ?? 64) - 2;
        var placed = false;
        try
        {
            var baseFacing = (short)(direction & 6);
            var facings = actor is not null
                ? new short[] { 0, 2, 4, 6 }.OrderBy(d =>
                {
                    var spotPxX = tileX * 16 + 8;
                    var spotPxY = tileY * 16 + 8;
                    var (fx, fy) = d switch { 0 => (0, -1), 2 => (1, 0), 4 => (0, 1), _ => (-1, 0) };
                    return -((fx * (actor.Position.X - spotPxX)) + (fy * (actor.Position.Y - spotPxY)));
                }).ToArray()
                : [baseFacing, (short)((baseFacing + 2) & 6), (short)((baseFacing + 4) & 6), (short)((baseFacing + 6) & 6)];

            for (var radius = 0; radius <= 3 && !placed; radius++)
            for (var dy = -radius; dy <= radius && !placed; dy++)
            for (var dx = -radius; dx <= radius && !placed; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                var cx = tileX + dx;
                var cy = tileY + dy;
                if (cx < 1 || cy < 1 || cx > maxCoord || cy > maxCoord) continue;
                var spot = new ObjectPosition((short)(cx * 16 + 8), (short)(cy * 16 + 8), (sbyte)level);
                if (!currentWorld.RoomIsOutside(currentWorld.RoomAtTile(cx, cy, level)) && level == 1)
                {
                    // Allow indoor or outdoor placement depending on caller, respecting CanPlaceCreatedObject.
                }
                foreach (var facing in facings)
                {
                    currentWorld.FaceCreatedObject(root, group, facing);
                    if (!currentWorld.CanPlaceCreatedObject(root, spot, actor)) continue;
                    currentWorld.PlaceCreatedObject(root, spot);
                    placed = true;
                    break;
                }
            }
            return placed ? root.Id : null;
        }
        finally
        {
            if (!placed) currentWorld.RemoveCreatedObjectGroup(root);
        }
    }

    public bool TryRemoveOwnedObject(string packId, short objectId)
    {
        if (world() is not { } currentWorld) return false;
        if (currentWorld.ObjectById(objectId) is not { } existing) return false;
        var source = existing.TypeRecord is { } type ? registry()?.Find(unchecked((uint)type.Key))?.Source : null;
        if (source is null || !source.Replace('\\', '/').Contains("/Downloads/" + packId + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only this pack's own objects may be removed");
        if (existing[ObjectData.UseCount] > 0) return false;
        var root = existing.MultiTileMasterId != 0 ? (currentWorld.ObjectById(existing.MultiTileMasterId) ?? existing) : existing;
        if (root[ObjectData.UseCount] > 0) return false;
        var seen = new HashSet<short> { root.Id };
        for (var nextId = root.MultiTileNextId; nextId != 0 && seen.Add(nextId); )
        {
            if (currentWorld.ObjectById(nextId) is not { } part) break;
            if (part[ObjectData.UseCount] > 0) return false;
            nextId = part.MultiTileNextId;
        }
        currentWorld.RemoveCreatedObjectGroup(root);
        return true;
    }

    public void SetSimSuitOverride(short simId, int? suitCategory) => setSuitOverride?.Invoke(simId, suitCategory);

    public int WaterOutdoorPlants()
    {
        if (world() is not { } currentWorld) return 0;
        int watered = 0;
        foreach (var item in currentWorld.Objects.Values)
        {
            if (item is XPerson || item.MultiTileMasterId != 0) continue;
            if (!Outside(item) || Sheltered(item)) continue;
            var interactions = item.TypeRecord?.Interactions?.Interactions;
            if (interactions is null || !interactions.Any(entry =>
                entry.Name is not null && entry.Name.Contains("Water", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (item.Attributes.Length > 0 && item.Attributes[0] < 100)
            {
                item.Attributes[0] = 100;
                watered++;
            }
        }
        return watered;
    }

    public short GetObjectAttribute(string packId, short objectId, int index) => OwnedObject(packId, objectId, index).Attributes[index];
    public void SetObjectAttribute(string packId, short objectId, int index, short value)
    {
        var item = OwnedObject(packId, objectId, index);
        item.Attributes[index] = value;
        if (item.MultiTileMasterId == 0 && item.MultiTileNextId != 0 &&
            unchecked((ushort)item.Definition[(int)SimsEngine.Formats.ObjdField.SubIndex]) == ushort.MaxValue)
        {
            var seen = new HashSet<short> { item.Id };
            for (var nextId = item.MultiTileNextId; nextId != 0 && seen.Add(nextId); )
            {
                if (World.ObjectById(nextId) is not { } part) break;
                if (index >= 0 && index < part.Attributes.Length)
                    part.Attributes[index] = value;
                nextId = part.MultiTileNextId;
            }
        }
    }
    public void SetObjectGraphic(string packId, short objectId, short graphic)
    {
        var item = OwnedObject(packId, objectId, null);
        var leadNumGraphics = item.Definition[(int)SimsEngine.Formats.ObjdField.NumGraphics];
        var isCatalogLead = item.MultiTileMasterId == 0 && item.MultiTileNextId != 0 &&
            unchecked((ushort)item.Definition[(int)SimsEngine.Formats.ObjdField.SubIndex]) == ushort.MaxValue;
        if (graphic < 0 || (!isCatalogLead && graphic >= leadNumGraphics))
            throw new ArgumentOutOfRangeException(nameof(graphic));
        if (graphic < leadNumGraphics || leadNumGraphics == 0)
            item[ObjectData.Graphic] = graphic;
        if (isCatalogLead)
        {
            var seen = new HashSet<short> { item.Id };
            for (var nextId = item.MultiTileNextId; nextId != 0 && seen.Add(nextId); )
            {
                if (World.ObjectById(nextId) is not { } part) break;
                if (graphic < part.Definition[(int)SimsEngine.Formats.ObjdField.NumGraphics])
                    part[ObjectData.Graphic] = graphic;
                nextId = part.MultiTileNextId;
            }
        }
    }
}
