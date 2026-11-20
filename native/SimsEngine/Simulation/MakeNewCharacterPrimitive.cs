using System.Buffers.Binary;
using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Executes the Make New Character primitive (opcode 0x13) to create and register a new Sim or pet
/// from skin, gender, and mode locals.</summary>
public static class MakeNewCharacterPrimitive
{
    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var (world, me, frame) = context;
        CheckLocal(frame, operand[0]);
        CheckLocal(frame, operand[2]);
        CheckLocal(frame, operand[1]);

        if (world.Host is { } host)
        {
            var character = host.CreateCharacter(operand[3]);
            if (character is null) return PrimitiveResult.False;
            frame.StackObject = character.Id;
            return PrimitiveResult.True;
        }

        if (world.ObjectRegistry is { } registry)
        {
            var neighborId = CreateNeighborFromTemplate(context, world, registry, me, frame,
                operand[3], frame.Locals[operand[0]], frame.Locals[operand[1]], frame.Locals[operand[2]]);
            if (neighborId == 0) return PrimitiveResult.False;
            // Stores the newly registered character's neighbor ID in the frame stack object.
            frame.StackObject = neighborId;
            return PrimitiveResult.True;
        }

        var fallback = CreateFromTemplate(world, operand[3]);
        if (fallback is null) return PrimitiveResult.False;
        frame.StackObject = fallback.Id;
        return PrimitiveResult.True;
    }

    private static short CreateNeighborFromTemplate(
        in TreeContext context,
        SimulationWorld world,
        ObjectRegistry registry,
        XObject me,
        StackFrame frame,
        int mode,
        short colorLocal,
        short age,
        short gender)
    {
        var templateGuid = mode switch { 1 => 0x4a70df92u, 2 => 0x7bea0977u, _ => 0x7fd96b54u };
        var templateEntry = registry.Find(templateGuid);
        if (templateEntry is null) return 0;

        // Validates the zero-based skin color index against semi-global string table 0x2000 (0=unknown,
        // 1=lgt, 2=drk, 3=med) when age is non-negative.
        var skin = "lgt";
        if (age >= 0)
        {
            var semiGlobal = (frame.Table as BehaviorScope)?.SemiGlobal
                ?? (me.TypeRecord?.BehaviorScope as BehaviorScope)?.SemiGlobal;
            if (semiGlobal?.Find(ResourceType.Str, 0x2000) is { } skinChunk)
            {
                var skinStrings = StringTable.Read(semiGlobal, skinChunk).ForLanguage(world.Language);
                if (colorLocal < 0 || colorLocal >= skinStrings.Count || string.IsNullOrEmpty(skinStrings[colorLocal]))
                    throw new SimulationException(SimulationError.InvalidSkinColor);
                skin = skinStrings[colorLocal];
            }
            else
            {
                skin = colorLocal switch
                {
                    0 => "unknown",
                    1 => "lgt",
                    2 => "drk",
                    3 => "med",
                    _ => throw new SimulationException(SimulationError.InvalidSkinColor),
                };
            }
        }

        uint newGuid;
        do
        {
            newGuid = (world.NextRandom() << 16) | (world.NextRandom() & 0xffffu);
        } while (newGuid == 0 || registry.Find(newGuid) is not null ||
                 world.Neighbors.Values.Any(existing => existing.TypeRecordKey == unchecked((int)newGuid)));

        var objdChunk = templateEntry.Chunk;
        var objdBytes = objdChunk.Data.ToArray();
        if (objdBytes.Length >= 0x20)
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x1c, 4), newGuid);
        if (objdBytes.Length >= 0x90)
            BinaryPrimitives.WriteUInt32LittleEndian(objdBytes.AsSpan(0x8c, 4), templateGuid);
        var clonedFile = templateEntry.File.WithChunkData(ResourceType.Objd, objdChunk.Id, objdBytes);
        var bodyStringsId = templateEntry.Definition[ObjdField.BodyStringsId];
        var characterName = world.UiTextString(142, 4) ?? "No childname";
        var resolvedGenderMode = mode switch
        {
            1 => gender != 0 ? 3 : 2,
            2 => gender != 0 ? 5 : 4,
            _ => gender != 0 ? 1 : 0,
        };

        var targetContext = new InteractionBehaviorRuntimeContext();
        WardrobeSuitService.PopulateContextFromEntry(registry, templateEntry, world.Language, targetContext, persistInitializedDefaults: false);

        if (age < 0)
        {
            // A negative age argument copies age, skin, gender, everyday body, and head from the caller.
            if (me is XPerson sourcePerson)
            {
                var sourceScope = InteractionBehaviorScope.Initialize();
                var sourceContext = WardrobeSuitService.BeginPersonScope(world, sourcePerson, sourceScope);
                targetContext.SetString(0x01, sourceContext.StringAt(0x01));
                targetContext.SetString(0x0d, sourceContext.StringAt(0x0d));
                targetContext.SetString(0x0e, sourceContext.StringAt(0x0e));
                targetContext.SetString(0x0f, sourceContext.StringAt(0x0f));
                targetContext.SetString(0x02, sourceContext.StringAt(0x02));
                targetContext.SetString(0x03, sourceContext.StringAt(0x03));
                sourceScope.Close();
                if (!string.IsNullOrWhiteSpace(sourcePerson.Name))
                    characterName = sourcePerson.Name;
                else if (!string.IsNullOrWhiteSpace(sourcePerson.TypeRecord?.CatalogName))
                    characterName = sourcePerson.TypeRecord!.CatalogName!;
            }
            clonedFile = WardrobeSuitService.CommitContextToFile(registry, clonedFile, bodyStringsId, world.Language, targetContext);
        }
        else
        {
            var isChild = mode == 0 && age < 18;
            targetContext.SetString(0x0e, isChild ? "9" : "27");
            targetContext.SetString(0x01, mode switch
            {
                1 => "dog",
                2 => "cat",
                _ => isChild ? "child" : "adult",
            });
            targetContext.SetString(0x0f, skin);
            targetContext.SetString(0x0d, resolvedGenderMode switch
            {
                0 => "male",
                1 => "female",
                2 => "dogmale",
                3 => "dogfemale",
                4 => "catmale",
                _ => "catfemale",
            });

            if (WardrobeSuitService.HasCatalog(registry))
            {
                var bodies = WardrobeSuitService.GetActionList(world, null, targetContext, 0, headsList: false);
                var bodyCount = bodies?.Entries.Count ?? 0;
                if (resolvedGenderMode is 0 or 1)
                {
                    var heads = WardrobeSuitService.GetActionList(world, null, targetContext, 0, headsList: true);
                    var headCount = heads?.Entries.Count ?? 0;
                    if (bodyCount == 0 || headCount == 0) return 0;

                    var headIdx = (int)(world.NextRandom() % (uint)headCount);
                    targetContext.SetString(0x03, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(heads!.Entries[headIdx]));
                    var bodyIdx = (int)(world.NextRandom() % (uint)bodyCount);
                    targetContext.SetString(0x02, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(bodies!.Entries[bodyIdx]));
                }
                else
                {
                    if (bodyCount == 0) return 0;
                    // Consumes the selected pet breed from dialog register 0x100 and resets it to -1.
                    var selectedBreed = world.Dialog[0x100];
                    world.Dialog[0x100] = -1;
                    var breedIdx = selectedBreed >= 0 && selectedBreed < bodyCount
                        ? (int)selectedBreed
                        : (int)(world.NextRandom() % (uint)bodyCount);
                    targetContext.SetString(0x02, InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(bodies!.Entries[breedIdx]));
                }
            }

            clonedFile = WardrobeSuitService.CommitContextToFile(registry, clonedFile, bodyStringsId, world.Language, targetContext);
        }

        // Writes the character name to catalog strings (CTSS) entry 0 when present.
        var ctssId = templateEntry.Definition[ObjdField.CatalogStringsId];
        if (ctssId != 0 && clonedFile.Find(ResourceType.Ctss, ctssId) is { } ctssChunk)
        {
            var ctssTable = StringTable.Read(clonedFile, ctssChunk);
            var existingCtss = ctssTable.ForLanguage(world.Language).ToList();
            if (existingCtss.Count == 0) existingCtss.Add(characterName);
            else existingCtss[0] = characterName;
            clonedFile = clonedFile.WithChunkDataResized(ResourceType.Ctss, ctssId,
                ctssTable.WriteLanguageValues(existingCtss, world.Language));
        }

        var slot = -1;
        var limit = world.NeighborRosterComplete ? world.NeighborWalkOrder.Count : world.CompleteNeighborSlotPrefix;
        for (var i = 0; i < limit; i++)
        {
            if (world.NeighborWalkOrder[i] is null && !world.Neighbors.ContainsKey(checked((short)(i + 1))))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            slot = world.NeighborWalkOrder.Count;
            world.NeighborWalkOrder.Add(null);
        }
        while (world.Neighbors.ContainsKey(checked((short)(slot + 1))))
        {
            slot = world.NeighborWalkOrder.Count;
            world.NeighborWalkOrder.Add(null);
        }

        var neighborId = checked((short)(slot + 1));
        registry.RegisterFile(clonedFile, $"User{neighborId:D5}.iff", ObjectScan.People);
        registry.DirtyPeopleGuids.Add(newGuid);
        var clonedEntry = registry.Find(newGuid) ?? templateEntry;
        var definition = clonedEntry.Definition.Fields.Select(value => unchecked((short)value)).ToArray();
        var neighbor = new NeighborState(neighborId, definition)
        {
            Name = characterName,
            TypeRecordKey = unchecked((int)newGuid),
            PersonRecordVersion = 9,
            FriendEligibilityObjectFlagsB4 = 4u,
        };
        neighbor.PersonData[(int)PersonData.PersonAge] = age < 0 && me is XPerson src ? src[PersonData.PersonAge] : age;
        neighbor.PersonData[(int)PersonData.Gender] = age < 0 && me is XPerson srcG ? srcG[PersonData.Gender] : gender;
        neighbor.PersonData[65] = (short)resolvedGenderMode;
        world.ClearRemovedNeighbor(neighborId);
        world.Neighbors[neighborId] = neighbor;
        world.NeighborWalkOrder[slot] = neighbor;
        return neighborId;
    }

    /// <summary>Builds a character from template GUID 0x4a70df92 for mode 1, 0x7bea0977 for mode 2, or
    /// 0x7fd96b54 for default Sims.</summary>
    private static XObject? CreateFromTemplate(SimulationWorld world, int mode) =>
        world.ObjectFactory?.Invoke(mode switch { 1 => 0x4a70df92, 2 => 0x7bea0977, _ => 0x7fd96b54 });

    private static void CheckLocal(StackFrame frame, byte index)
    {
        if (index >= frame.Locals.Length) throw new SimulationException(SimulationError.StackNumberOutOfRange);
    }
}
