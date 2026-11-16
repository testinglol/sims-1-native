using SimsEngine.Content;
using SimsEngine.Simulation;
using SimsNative.Plugins;

namespace SimsSimulation;

internal sealed class PluginWorld(Func<SimulationWorld?> world, Func<short> house, Func<ushort?> family,
    Func<ObjectRegistry?> registry, Func<bool> paused) : IPluginSimulation, PluginHost.IPluginWorld
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
            { Animation = person.AnimationPlayback?.Clip.Name })
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

    internal PluginObject Describe(XObject item) => new(item.Id,
        unchecked((uint)(item.TypeRecord?.Key ?? 0)), item is XPerson person ? Name(person) : item.TypeRecord?.Name ?? "",
        item.Position.X >> 4, item.Position.Y >> 4, item.Position.Height, item.MultiTileMasterId != 0 ? item.MultiTileMasterId : item.Id)
        { Outside = Outside(item) };

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
        if (world() is not { } lot) return new PluginEnvironmentClock(house, mapMode, 0, 0, 720, []);
        var actors = Persons().Select(person => new PluginActor(person.Id, person.Position.X / 16f, person.Position.Y / 16f,
            person.Position.Height, false, PersonAgeCategory.IsChild(person[PersonData.PersonAge]), false) { Outside = Outside(person) }).ToArray();
        return new PluginEnvironmentClock(house, mapMode, lot.CurrentDate?.Month ?? 0, lot.CurrentDate?.Day ?? 0,
            lot.Clock.Hour * 60 + lot.Clock.Minute, actors);
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
