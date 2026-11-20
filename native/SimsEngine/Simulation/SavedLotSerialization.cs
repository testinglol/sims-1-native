using SimsEngine.Formats;

namespace SimsEngine.Simulation;

public sealed partial class SimulationWorld
{
    internal Dictionary<short, ObjectTypeEntry> SavedObjectTypes { get; } = [];
    internal Dictionary<short, XObject> LoadedObjectInstances { get; } = [];

    public SavedObjectTypeRegistrationScope BeginSavedObjectTypeRegistrationScope() =>
        new(this, SavedObjectTypes.ToDictionary(pair => pair.Key, pair => pair.Value),
            SavedObjectClasses.ToDictionary(pair => pair.Key, pair => pair.Value),
            CodeOwnerTypeGuids.ToDictionary(pair => pair.Key, pair => pair.Value),
            CodeOwnerScopes.ToDictionary(pair => pair.Key, pair => pair.Value));

    internal void RollbackSavedObjectTypeRegistrations(SavedObjectTypeRegistrationScope scope)
    {
        var additions = SavedObjectTypes.Where(pair => !scope.Types.ContainsKey(pair.Key)).ToArray();
        var classes = SavedObjectClasses.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (index, entry) in additions)
        {
            // Retains an object type registration only if another active or saved object references it.
            if (WalkOrder.Any(item => item.TypeRecord?.Key == unchecked((int)entry.Guid)) ||
                LoadedObjectInstances.Values.Any(item => item.TypeRecord?.Key == unchecked((int)entry.Guid)) ||
                (SavedLotObjects?.Objects.Any(item => item.TypeIndex == index) ?? false))
                continue;
            SavedObjectTypes.Remove(index);
            Restore(classes, scope.Classes, index);
            Restore(CodeOwnerTypeGuids, scope.TypeGuids, index);
            Restore(CodeOwnerScopes, scope.Scopes, index);
        }
        SavedObjectClasses = classes;

        static void Restore<T>(Dictionary<short, T> current, Dictionary<short, T> original, short index)
        {
            if (original.TryGetValue(index, out var value)) current[index] = value;
            else current.Remove(index);
        }
    }

    internal short RegisterSavedObjectType(ObjectTypeEntry definition, ObjectClass objectClass, ITreeTable? scope)
    {
        foreach (var entry in SavedObjectTypes.Values)
            if (entry.Guid == definition.Guid) return entry.TypeIndex;
        var next = 1;
        while (next <= short.MaxValue && SavedObjectTypes.ContainsKey((short)next)) next++;
        if (next > short.MaxValue) throw new InvalidDataException("The saved object type table is full.");
        var index = (short)next;
        SavedObjectTypes.Add(index, definition with { TypeIndex = index });
        var classes = SavedObjectClasses.ToDictionary(pair => pair.Key, pair => pair.Value);
        classes[index] = (objectClass, LotObjects.IsPlaceholder(definition.Guid));
        SavedObjectClasses = classes;
        CodeOwnerTypeGuids[index] = definition.Guid;
        CodeOwnerScopes[index] = scope;
        return index;
    }

    private IReadOnlyList<LotObject> BuildLiveSavedObjectList(LotObjects source)
    {
        var originals = source.Objects.ToDictionary(item => item.ObjectId);
        var result = new List<LotObject>();
        foreach (var live in WalkOrder)
        {
            if (SavedExecutionStacks.TryGetValue(live.Id, out var activeStack))
            {
                if (activeStack.Frames.Count > 0)
                {
                    TreeRunner.SaveFrames(live, activeStack);
                }
                else
                {
                    SavedExecutionStacks.Remove(live.Id);
                    live.SavedStack.Clear();
                    live.SavedStackSize = 0;
                }
            }
            else
            {
                live.SavedStack.Clear();
                live.SavedStackSize = 0;
            }
            var guid = unchecked((uint)(live.TypeRecord?.Key ?? 0));
            LotObject? original = null;
            short typeIndex;
            if (originals.TryGetValue(live.Id, out original) &&
                ReferenceEquals(LoadedObjectInstances.GetValueOrDefault(live.Id), live) &&
                SavedObjectTypes[original.TypeIndex].Guid == guid)
            {
                typeIndex = original.TypeIndex;
            }
            else
            {
                var type = SavedObjectTypes.Values.FirstOrDefault(entry => entry.Guid == guid)
                    ?? throw new InvalidDataException($"Object {live.Id}: no registered saved type.");
                typeIndex = type.TypeIndex;
            }
            var state = BuildLiveObjectState(live, typeIndex, source.Version, original?.Decoded);
            result.Add(new LotObject(live.Id, typeIndex, original?.StateBytes ?? [], original?.StateOffset ?? 0, state));
        }
        // Preserves the relative walk order of active objects while keeping opaque records near their
        // original positions.
        for (var index = 0; index < source.Objects.Count; index++)
        {
            var saved = source.Objects[index];
            if (ObjectById(saved.ObjectId) is null && !SavedObjectClasses.ContainsKey(saved.TypeIndex))
                result.Insert(Math.Min(index, result.Count), saved);
        }
        return result;
    }

    private ObjectState BuildLiveObjectState(XObject live, short typeIndex, int version, ObjectState? baseState)
    {
        var (objectClass, placeholder) = SavedObjectClasses[typeIndex];
        PersonState? personState = null;
        if (live is XPerson person)
        {
            var character = person.SavedCharacter
                ?? baseState?.Person?.Character
                ?? throw new InvalidDataException($"Created person {live.Id} has no initialized character to save.");
            var personDataLength = version < 0x47 ? 0x50 : 0x100;
            var personData = new short[personDataLength];
            Array.Copy(person.PersonData, personData, Math.Min(person.PersonData.Length, personDataLength));
            var first = (person.CurrentActionRecord ?? ActionQueueSlot.EmptyRecord()).ToSaved();
            var second = (person.PreviousActionRecord ?? ActionQueueSlot.EmptyRecord()).ToSaved();
            var actions = person.WriteActionQueue();
            var objects = person.SavedPersonObjectReferences.Select(r => new PersonObjectEntry(r.Target?.Id ?? r.SavedObjectKey, r.Value, r.Flag)).ToArray();
            var values = person.SavedPersonValues.ToArray();
            personState = new PersonState(character, person.Motives.ToArray(), person.SavedPersonValues7CC.ToArray(),
                personData, person.SavedPersonActionStorageCount, first, second, actions, objects, values);
        }

        var multiTile = objectClass is ObjectClass.MultiTileObject or ObjectClass.Portal
            ? (live.SavedMultiTileState ?? baseState?.MultiTile ?? new MultiTileState(0, 0, version > 0x39 ? 0 : null, 0, 0, 0,
                live.MultiTileMasterId, objectClass == ObjectClass.Portal ? [] : null))
            : null;
        if (multiTile is not null)
        {
            multiTile = multiTile with
            {
                LeadObject = live.MultiTileMasterId,
                PortalValues = objectClass == ObjectClass.Portal ? multiTile.PortalValues ?? [] : multiTile.PortalValues,
            };
        }

        var values0x108 = new int[6];
        values0x108[0] = live.FootprintBounds.MinY;
        values0x108[1] = live.FootprintBounds.MinX;
        values0x108[2] = live.FootprintBounds.MaxY;
        values0x108[3] = live.FootprintBounds.MaxX;
        values0x108[4] = live.Position.X;
        values0x108[5] = live.Position.Y;

        int? managerFlags = placeholder || version <= 0x35
            ? null
            : live.ClockManagerIndex >= 1 && live.ClockManagerIndex <= ClockManager.Entries.Count
                ? unchecked((int)ClockManager.Entries[live.ClockManagerIndex - 1].Flags)
                : (baseState?.ManagerFlags ?? 0);

        return new ObjectState(
            values0x108,
            live.Position.Height,
            version == 0x37 ? unchecked((int)live.Flags) : null,
            live.Attributes.ToArray(),
            live.Temporaries.ToArray(),
            live.Data.ToArray(),
            live.SavedStackSize,
            live.SavedStack.ToArray(),
            live.Relationships.WriteSavedEntries(baseState?.Relationships ?? []),
            live.Slots.ToArray(),
            live.DynamicSpriteFlags.ToArray(),
            managerFlags,
            personState,
            multiTile);
    }

    private LotObject FreshSavedObject(XObject live, ObjectTypeEntry type, int version)
    {
        if (SavedExecutionStacks.TryGetValue(live.Id, out var activeStack))
        {
            if (activeStack.Frames.Count > 0)
            {
                TreeRunner.SaveFrames(live, activeStack);
            }
            else
            {
                SavedExecutionStacks.Remove(live.Id);
                live.SavedStack.Clear();
                live.SavedStackSize = 0;
            }
        }
        else
        {
            live.SavedStack.Clear();
            live.SavedStackSize = 0;
        }
        var state = BuildLiveObjectState(live, type.TypeIndex, version, null);
        return new LotObject(live.Id, type.TypeIndex, [], 0, state);
    }
}

public sealed class SavedObjectTypeRegistrationScope : IDisposable
{
    private readonly SimulationWorld world;
    private bool committed;
    private bool disposed;

    internal Dictionary<short, ObjectTypeEntry> Types { get; }
    internal Dictionary<short, (ObjectClass Class, bool Placeholder)> Classes { get; }
    internal Dictionary<short, uint> TypeGuids { get; }
    internal Dictionary<short, ITreeTable?> Scopes { get; }

    internal SavedObjectTypeRegistrationScope(SimulationWorld world, Dictionary<short, ObjectTypeEntry> types,
        Dictionary<short, (ObjectClass Class, bool Placeholder)> classes, Dictionary<short, uint> typeGuids,
        Dictionary<short, ITreeTable?> scopes)
    {
        this.world = world;
        Types = types;
        Classes = classes;
        TypeGuids = typeGuids;
        Scopes = scopes;
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        committed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!committed) world.RollbackSavedObjectTypeRegistrations(this);
    }
}
