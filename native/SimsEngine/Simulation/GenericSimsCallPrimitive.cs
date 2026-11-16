using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>Dispatches global simulation calls by call index (0 through 43), providing engine services such
/// as family management, active Sim resolution, interaction queue manipulation, and neighbor updates.</summary>
public static class GenericSimsCallPrimitive
{
    private const short LastCall = 0x2b;
    private const uint InvalidCallAddress = 0x45145f;

    public static readonly IReadOnlyList<uint> CaseAddresses =
    [
        0x44f81a, 0x44f844, 0x44fb91, 0x44fb81, 0x44fc1d, 0x44fd26, 0x44fc93, InvalidCallAddress,
        0x44f81e, 0x44f821, 0x44fd69, 0x44fd7a, 0x44fd8b, 0x44fe16, 0x44fe62, 0x44fe7c,
        0x44fee8, 0x4506a0, 0x45073b, 0x4507c7, 0x4508bd, 0x4509ca, InvalidCallAddress, 0x450b6b,
        0x44ffae, 0x450c42, 0x450772, 0x450c85, 0x450ce2, 0x4501fd, 0x450568, 0x450432,
        0x450d32, 0x450fca, 0x451054, 0x451182, 0x4510e8, 0x4511e2, 0x451244, 0x45133f,
        0x45133f, 0x44f830, 0x4513e0, 0x451443,
    ];

    public static PrimitiveResult Execute(in TreeContext context, ReadOnlySpan<byte> operand)
    {
        var me = context.Me;
        var call = (short)(operand[0] | operand[1] << 8);
        var address = call is >= 0 and <= LastCall ? CaseAddresses[call] : InvalidCallAddress;
        if (address == InvalidCallAddress) throw new SimulationException(SimulationError.InvalidGlobalSimCall);
        switch (call)
        {
            case 0x00:
                return ExecuteCallInitialization(context, 0);
            case 0x01:
                return ExecuteCallOne(context, me);
            case 0x08:
                return ExecuteCallInitialization(context, 1);
            case 0x09:
                return ExecuteCallInitialization(context, 2);
            case 0x26:
                return ExecuteCallTwentySix(context, me);
            case 0x27:
                return ExecuteCallTwentySevenOrTwentyEight(context, me, GenericSimsCallRequestKind.First);
            case 0x28:
                return ExecuteCallTwentySevenOrTwentyEight(context, me, GenericSimsCallRequestKind.Second);
            case 0x02:
            {
                var slot = me.ObjectTableEntry(context.Frame.StackObject)
                    ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
                if (me is not XPerson placed)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                if (placed.GenericSimsCall02GateWordA84 != 0) placed.SetCurrentActionSecondaryReference(slot);
                return PrimitiveResult.True;
            }
            case 0x0c:
            {
                var item = me.ObjectTableEntry(context.Frame.StackObject)
                    ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
                var x = unchecked((sbyte)(item.Position.X >> 4));
                var y = unchecked((sbyte)(item.Position.Y >> 4));
                var parameter = context.World.LotTransformParameter;
                if (parameter != 0 && !LotGeometry.TryTransformTile(x, y, parameter, out x, out y))
                {
                    if (context.World.Host is { } tileHost)
                    {
                        var index = unchecked(((parameter * 0x40 - 0x40 + x) * 0x40 + y) * 2);
                        x = tileHost.TileTransformByte(index);
                        y = tileHost.TileTransformByte(index + 1);
                    }
                    else
                    {
                        var norm = parameter & 3;
                        (x, y) = norm switch
                        {
                            1 => (unchecked((sbyte)(63 - y)), x),
                            2 => (unchecked((sbyte)(63 - x)), unchecked((sbyte)(63 - y))),
                            3 => (y, unchecked((sbyte)(63 - x))),
                            _ => (x, y),
                        };
                    }
                }
                me.Temporaries[0] = unchecked((short)(-(x + y)));
                return PrimitiveResult.True;
            }
            case 0x0d:
            {
                var subject = me.ObjectTableEntry(context.Frame.StackObject)
                    ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
                context.World.NotifyPersonObjectReferenceRecipients(subject);
                return PrimitiveResult.True;
            }
            case 0x04:
                return ExecuteAddToFamily(context, context.World.NeighborhoodFamilies?.ActiveFamilyNumber ?? 0);
            case 0x05:
            {
                var item = context.World.FindInteractionObjectByTableWord(me.Temporaries[0]);
                if (item is null) return PrimitiveResult.False;
                var amount = context.World.GenericSimsCall05Amount(item);
                context.World.RequireFamilyBudget().AlterFamilyBudget(2, unchecked(-amount));
                return PrimitiveResult.True;
            }
            case 0x21:
                return ExecuteAddToFamily(context, me.Temporaries[0]);
            case 0x22:
                return ExecuteCallTwentyTwo(context, me);
            case 0x24:
                return ExecuteCallTwentyFour(context, me);
            case 0x15:
                return ExecuteCallFifteen(context, me);
            case 0x13:
                return ExecuteCallThirteenOrFourteen(context, me, 0);
            case 0x14:
                return ExecuteCallThirteenOrFourteen(context, me, 3);
            case 0x11:
                return ExecuteCallEleven(context, me);
            case 0x10:
                return ExecuteCallTen(context, me);
            case 0x1b:
            {
                var available = 9;
                for (var fieldValue = 40; fieldValue <= 48; fieldValue++)
                {
                    foreach (var item in context.World.InteractionLookupOrder)
                    {
                        if (item.GenericSimsCall1BRawWord13C != fieldValue) continue;
                        available--;
                        break;
                    }
                }
                me.Temporaries[0] = (short)available;
                return PrimitiveResult.True;
            }
            case 0x20:
                return ExecuteCallTwenty(context);
            case 0x06:
                return ExecuteCallSix(context);
            case 0x03:
            {
                var state = context.World.GenericSimsCall03State;
                var host = context.World.Host;
                if (state.PrimaryHandle != 0)
                {
                    host?.ReleaseGenericSimsCall03PrimaryHandle(state.PrimaryHandle);
                    state.PrimaryHandle = 0;
                    if (state.LinkedGlobalWord60 is not null) state.LinkedGlobalWord60 = 0;
                }
                if (state.SecondaryHandle != 0)
                {
                    host?.ReleaseGenericSimsCall03SecondaryHandle(state.SecondaryHandle);
                    state.SecondaryHandle = 0;
                }
                return PrimitiveResult.True;
            }
            case 0x17:
                return ExecuteCallSeventeen(context, me);
            case 0x18:
                return ExecuteCallActionGroups(context, me, me as XPerson, InteractionBehaviorScope.ActionArrayCount);
            case 0x1d:
                return ExecuteCallActionGroups(context, me, ResolveActionGroupPerson(context, me), InteractionBehaviorScope.ActionArrayCount - 1);
            case 0x1e:
                return ExecuteCallActionGroupLookup(context, me, ResolveActionGroupPerson(context, me), returnCount: false);
            case 0x1f:
                return ExecuteCallActionGroupLookup(context, me, ResolveActionGroupPerson(context, me), returnCount: true);
            case 0x23:
            {
                if (me.Definition[(int)ObjdField.Type] != XPerson.PersonType)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                if (me is XPerson self && context.World.GenericSimsCall23PersonSlotBase is { } slotBase)
                    slotBase.StoredPersonPointer10C = self;
                return PrimitiveResult.True;
            }
            case 0x25:
            {
                var entry = me.ObjectTableEntry(context.Frame.StackObject);
                if (entry is not XPerson)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                return PrimitiveResult.True;
            }
            case 0x12:
            {
                var slot = context.World.ActivePersonIndex(0);
                if (slot != -1)
                    me.Temporaries[0] = context.World.ActivePersonRecordId(slot) ?? (short)-1;
                return PrimitiveResult.True;
            }
            case 0x1a:
            {
                var slot = context.World.ActivePersonIndex(0);
                if (slot == -1) return PrimitiveResult.True;
                var stored = context.World.StoredSlotPersonId;
                if (stored is null)
                {
                    me.Temporaries[0] = context.World.ActivePersonRecordId(slot) ?? (short)-1;
                    me.Temporaries[1] = 0;
                }
                else
                {
                    me.Temporaries[0] = stored.Value;
                    me.Temporaries[1] = 1;
                }
                return PrimitiveResult.True;
            }
            case 0x0a:
                context.World.MaintainLotList(me);
                return PrimitiveResult.True;
            case 0x0b:
                context.World.RemoveFromLotList(me);
                return PrimitiveResult.True;
            case 0x0e:
                me.SetObjectTableEffectWord(31, me.Temporaries[0]);
                context.World[SimulationGlobal.HouseRadioStation] = me.Temporaries[0];
                return PrimitiveResult.True;
            case 0x0f:
                if (me is not XPerson footprintOwner)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                footprintOwner[PersonData.RouteFootprintMask] = me.Temporaries[0];
                footprintOwner[PersonData.FootprintExtension] = 0;
                if (me.Temporaries[0] == 0) footprintOwner.RefreshFootprintBounds();
                return PrimitiveResult.True;
            case 0x19:
                if (me is not XPerson calledPerson)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                context.World.SavePersonRecord(calledPerson);
                return PrimitiveResult.True;
            case 0x1c:
            {
                var value = me.Temporaries[0];
                if (value >= 0x5a && value <= 0x5c) me.Temporaries[0] = 0;
                else if (value >= 0x5d && value <= 0x62) me.Temporaries[0] = 1;
                else me.Temporaries[0] = context.World.StateClass(value) switch
                {
                    1 => 0,
                    2 => 1,
                    _ => -1,
                };
                return PrimitiveResult.True;
            }
            case 0x29:
                return PrimitiveResult.True;
            case 0x2b:
            {
                context.World.RunLotUpkeep(me);
                return PrimitiveResult.True;
            }
            case 0x2a:
            {
                if (me.Temporaries[0] == 0) return PrimitiveResult.True;
                var entry = me.ObjectTableEntry(me.Temporaries[0]);
                var resolved = entry as XPerson;
                if (resolved is not null)
                {
                    var selection = context.World.EvaluatePersonRecordSelection(context.World.PersonRecordIndex(resolved));
                    if (selection is { } selected)
                        context.World.ApplyPersonRecordSelection(selected.Person, context.World.Host);
                }
                return PrimitiveResult.True;
            }
        }
        throw new SimulationException(SimulationError.InvalidGlobalSimCall);
    }

    private static PrimitiveResult ExecuteCallFifteen(in TreeContext context, XObject me)
    {
        var entry = me.ObjectTableEntry(me.Temporaries[0]);
        var item = entry as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var scope = context.World.InteractionExecutorScope(item);
        var remaining = scope.CountValue(2);
        if (remaining <= 0) return PrimitiveResult.True;

        var current = new InteractionScopeEntry(0, 0, 1);
        short cursor = -1;
        while (remaining-- > 0)
        {
            if (scope.TryFindNext(2, ref cursor, 0, out _) && scope.TryGet(cursor, out var fetched))
                current = fetched;

            var kind = current.Mode;
            if ((uint)kind <= 6)
            {
                var selector = kind switch
                {
                    0 => 9,
                    1 => 6,
                    2 => 5,
                    3 => 15,
                    4 => 7,
                    5 => 8,
                    _ => 14,
                };
                item.SetInteractionExecutorFloat(selector, unchecked((short)current.Result));
                scope.TryAdvance(cursor, -1);
            }
            else if (kind is >= 9 and <= 12)
            {
                var offset = kind switch
                {
                    9 => 0x59c,
                    10 => 0x62c,
                    11 => 0x630,
                    _ => 0x62e,
                };
                item.SetInteractionExecutorWord(offset, unchecked((short)current.Result));
                scope.TryAdvance(cursor, -1);
            }
        }

        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallSeventeen(in TreeContext context, XObject me)
    {
        if (me is not XPerson item)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var scope = context.World.InteractionExecutorScope(item);

        short cursor = -1;
        if (!scope.TryFindNext(2, ref cursor, 7, out _) || !scope.TryGet(cursor, out var first))
            return PrimitiveResult.True;

        me.Temporaries[0] = unchecked((short)first.Result);
        scope.TryAdvance(cursor, -1);

        cursor = 0;
        if (scope.TryFindNext(2, ref cursor, 8, out _) && scope.TryGet(cursor, out var second))
        {
            me.Temporaries[1] = unchecked((short)second.Result);
            scope.TryAdvance(cursor, -1);
        }

        return PrimitiveResult.True;
    }

    private static XPerson? ResolveActionGroupPerson(in TreeContext context, XObject me) =>
        context.Frame.StackObject != 0
            ? (me.ObjectTableEntry(context.Frame.StackObject) ?? context.StackObject) as XPerson
            : me as XPerson;

    private static PrimitiveResult ExecuteCallActionGroups(in TreeContext context, XObject me, XPerson? resolvedPerson, int groupCount)
    {
        var resolved = resolvedPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var host = context.World.Host;
        var scope = InteractionBehaviorScope.Initialize();
        var savedWord = resolved.InteractionBehaviorSavedWord;
        scope.TypeRecord = resolved.TypeRecord;
        scope.Mode = 0;
        scope.QueryContext = host is not null
            ? host.BeginInteractionBehaviorScope(scope)
            : WardrobeSuitService.BeginPersonScope(context.World, resolved, scope);

        var selectedIndex = me.Temporaries[1];
        var inputIndex = me.Temporaries[1];
        var mode = me.Temporaries[0];
        if (inputIndex >= 0 && mode >= 0 && mode < groupCount &&
            InteractionBehaviorActionGroup.TryGet(mode, out var group))
        {
            var list = host is not null
                ? host.InteractionBehaviorActions(scope, group.ListIndex)
                : WardrobeSuitService.GetActionList(context.World, resolved, scope.QueryContext, group.ListIndex);
            scope.SetActionList(group.ListIndex, list);
            if (inputIndex < (list?.Entries.Count ?? 0))
            {
                var text = scope.QueryContext?.StringAt(group.QueryId);
                selectedIndex = unchecked((short)InteractionBehaviorActionQueryBuilder.FindMatchingIndex(list!, text));
                scope.QueryContext?.SetString(group.QueryId,
                    InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(list!.Entries[inputIndex]));
            }
        }

        if (host is not null)
        {
            host.EndInteractionBehaviorScope(scope, commit: true);
            host.FinalizeInteractionBehaviorTypeRecord(resolved.TypeRecord);
            host.NotifyInteractionBehaviorGlobal(0x20000000);
        }
        else
        {
            WardrobeSuitService.EndPersonScope(context.World, resolved, scope, commit: true);
            context.World.DispatchNotice(0x20000000, 0);
        }
        scope.Close();
        resolved.InteractionBehaviorSavedWord = savedWord;
        me.Temporaries[1] = selectedIndex;
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallActionGroupLookup(in TreeContext context, XObject me, XPerson? resolvedPerson, bool returnCount)
    {
        var resolved = resolvedPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var host = context.World.Host;
        var scope = InteractionBehaviorScope.Initialize();
        var savedWord = resolved.InteractionBehaviorSavedWord;
        scope.TypeRecord = resolved.TypeRecord;
        scope.Mode = 0;
        scope.QueryContext = host is not null
            ? host.BeginInteractionBehaviorScope(scope)
            : WardrobeSuitService.BeginPersonScope(context.World, resolved, scope);

        var mode = me.Temporaries[0];
        short result = -1;
        if (mode >= 0 && mode < InteractionBehaviorScope.ActionArrayCount - 1 &&
            InteractionBehaviorActionGroup.TryGet(mode, out var group))
        {
            var list = host is not null
                ? host.InteractionBehaviorActions(scope, group.ListIndex)
                : WardrobeSuitService.GetActionList(context.World, resolved, scope.QueryContext, group.ListIndex);
            scope.SetActionList(group.ListIndex, list);
            if (returnCount)
            {
                result = unchecked((short)(list?.Entries.Count ?? 0));
            }
            else if (list is not null)
            {
                result = unchecked((short)InteractionBehaviorActionQueryBuilder.FindMatchingIndex(
                    list, scope.QueryContext?.StringAt(group.QueryId)));
            }
        }

        if (host is not null)
        {
            host.EndInteractionBehaviorScope(scope, commit: !returnCount);
            host.RefreshInteractionBehaviorTypeRecord(resolved.TypeRecord);
            host.NotifyInteractionBehaviorGlobal(0x20000000);
        }
        else
        {
            WardrobeSuitService.EndPersonScope(context.World, resolved, scope, commit: !returnCount);
            context.World.DispatchNotice(0x20000000, 0);
        }
        scope.Close();
        resolved.InteractionBehaviorSavedWord = savedWord;
        me.Temporaries[1] = result;
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTen(in TreeContext context, XObject me)
    {
        if (me is not XPerson resolved)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var host = context.World.Host;
        var scope = InteractionBehaviorScope.Initialize();
        var savedWord = resolved.InteractionBehaviorSavedWord;
        scope.TypeRecord = resolved.TypeRecord;
        scope.Mode = 0;
        scope.QueryContext = host is not null
            ? host.BeginInteractionBehaviorScope(scope)
            : WardrobeSuitService.BeginPersonScope(context.World, resolved, scope);

        var actions = host is not null
            ? host.InteractionBehaviorActions(scope, 0)
            : WardrobeSuitService.GetActionList(context.World, resolved, scope.QueryContext, 0);
        scope.SetActionList(0, actions);
        var count = actions?.Entries.Count ?? 0;
        if (count != 0)
        {
            var matchIndex = InteractionBehaviorActionQueryBuilder.FindMatchingIndex(
                actions!, scope.QueryContext?.StringAt(0x02));
            var updateIndex = (matchIndex + 1) % count;
            scope.QueryContext?.SetString(0x02,
                InteractionBehaviorActionQueryBuilder.BuildSelectedActionUpdate(actions!.Entries[updateIndex]));
        }

        if (host is not null)
        {
            host.EndInteractionBehaviorScope(scope, commit: count != 0);
            host.RefreshInteractionBehaviorTypeRecord(resolved.TypeRecord);
            host.NotifyInteractionBehaviorGlobal(0x20000000);
        }
        else
        {
            WardrobeSuitService.EndPersonScope(context.World, resolved, scope, commit: count != 0);
            context.World.DispatchNotice(0x20000000, 0);
        }
        scope.Close();
        resolved.InteractionBehaviorSavedWord = savedWord;
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTwenty(in TreeContext context)
    {
        var personCount = context.World.ActivePersonCount;
        for (var position = 0; position < personCount; position++)
        {
            var person = context.World.ActivePersonAt(position);
            if (person is not null) continue;

            var host = context.World.Host;
            person = host is null
                ? context.World.ResolveOrCreateActivePerson(position)
                : host.GenericSimsCall20FallbackPerson(position);
            person = person
                ?? throw new InvalidOperationException($"GenericSimsCall 0x20 could not resolve person at position {position}.");
            var scope = context.World.InteractionExecutorScope(person);
            var remaining = scope.CountValue(2);
            for (var iteration = 0; iteration < remaining; iteration++)
            {
                short cursor = -1;
                if (!scope.TryFindNext(2, ref cursor, 0, out _) || !scope.TryGet(cursor, out var entry))
                    continue;

                var payload = unchecked((short)entry.Result);
                var removeEntry = true;
                switch (entry.Mode)
                {
                    case 0: person.SetInteractionExecutorFloat(9, payload); break;
                    case 1: person.SetInteractionExecutorFloat(6, payload); break;
                    case 2: person.SetInteractionExecutorFloat(5, payload); break;
                    case 3: person.SetInteractionExecutorFloat(15, payload); break;
                    case 4: person.SetInteractionExecutorFloat(7, payload); break;
                    case 5: person.SetInteractionExecutorFloat(8, payload); break;
                    case 6: person.SetInteractionExecutorFloat(14, payload); break;
                    case 9: person.InteractionBehaviorSavedWord = payload; break;
                    case 10: person.SetInteractionExecutorWord(0x62c, payload); break;
                    case 11: person.SetInteractionExecutorWord(0x630, payload); break;
                    case 12: person.SetInteractionExecutorWord(0x62e, payload); break;
                    case 7:
                    case 8:
                    default:
                        removeEntry = false;
                        break;
                }
                if (removeEntry) scope.TryAdvance(cursor, -1);
            }

            if (host is null)
                PlaceCallTwentyFallbackPerson(context, person);
            else
                host.GenericSimsCall20FinalizePerson(person);
        }
        return PrimitiveResult.True;
    }

    /// <summary>Searches for an open placement location around the caller's position using room and water
    /// filters, retrying without room matching if needed before placing the Sim.</summary>
    private static void PlaceCallTwentyFallbackPerson(in TreeContext context, XPerson person)
    {
        var center = context.Me.Position;
        var parameters = new FindLocationParams
        {
            UseCenter = true,
            CenterX = center.X,
            CenterY = center.Y,
            Height = center.Height,
            Direction = -1,
            ContinueFlag = 1,
            FlagB = 1,
            FlagC = 1,
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (context.World.FindLocation(person, parameters, out var found, context.Stack))
            {
                var destination = new ObjectPosition(found.X, found.Y, center.Height);
                if (context.World.CanPlaceObject(person, destination, context.Stack))
                {
                    context.World.PlaceObject(person, destination, context.Stack);
                    context.World.RefreshPersonPlacementCallbacksAfterMovement(person);
                }
                return;
            }
            parameters.ContinueFlag = 0;
        }
    }

    private static PrimitiveResult ExecuteCallThirteenOrFourteen(in TreeContext context, XObject me, int mode)
    {
        var world = context.World;
        var host = world.Host;
        var entry = me.ObjectTableEntry(me.Temporaries[0]);
        var item = entry as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var scope = context.World.InteractionExecutorScope(item);

        short cursor = -1;
        if (!scope.TryFindNext(1, ref cursor, mode, out _) || !scope.TryGet(cursor, out var fetched))
        {
            me.Temporaries[0] = -1;
            return PrimitiveResult.True;
        }

        // The entry mode stores the person record key, while count tracks scope lifetime.
        var personRecordKey = fetched.Mode;
        me.Temporaries[0] = (host is null
            ? world.ScopedNeighborPersonRecordId(personRecordKey)
            : host.ScopedNeighborPersonRecordId(personRecordKey)) ?? (short)-1;
        scope.TryAdvance(cursor, 1);
        world.PersistNeighborhoodState();
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTwentyTwo(in TreeContext context, XObject me)
    {
        var entry = me.ObjectTableEntry(context.Frame.StackObject);
        var resolved = entry as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var target = context.World.GenericSimsCallRecordCallbackTarget;
        if (target is null) return PrimitiveResult.False;

        var host = context.World.Host;
        if (resolved.TypeRecord is null)
        {
            if (host is not null) host.GenericSimsCall22Finalize(resolved);
            return PrimitiveResult.False;
        }

        var personRecordIndex = context.World.PersonRecordIndex(resolved);
        if (personRecordIndex < 0)
        {
            if (host is not null) host.GenericSimsCall22Finalize(resolved);
            return PrimitiveResult.False;
        }
        var candidate = context.World.EvaluateFamePromotion(personRecordIndex, target);
        var result = candidate is not null && context.World.MeetsFameFriendPowerGate(personRecordIndex, candidate.FriendPowerThreshold);
        if (result)
        {
            var oldStars = candidate!.CurrentStarPower;
            SimulationWorld.ApplyFamePromotion(candidate!);
            var newStars = candidate!.PersonData[(int)PersonData.FameStarPower09];
            context.World.OnStarPowerChange(resolved.Id, oldStars, newStars);
        }
        if (host is not null) host.GenericSimsCall22Finalize(resolved);
        else context.World.DispatchNotice(0x110, resolved.Id);
        return result ? PrimitiveResult.True : PrimitiveResult.False;
    }

    private static PrimitiveResult ExecuteCallOne(in TreeContext context, XObject me)
    {
        var host = context.World.Host;
        if (me is not XPerson runner)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var stackEntry = me.ObjectTableEntry(context.Frame.StackObject)
            ?? throw new SimulationException(SimulationError.ReferenceToStackObjectOutOfRange);
        if (stackEntry is not XPerson stackObject)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);

        var runnerContainer = me.ObjectTableEntry(me[ObjectData.ContainerId])
            ?? throw new SimulationException(SimulationError.ReferenceToStackObjectOutOfRange);
        var stackContainer = me.ObjectTableEntry(stackObject[ObjectData.ContainerId]);

        var runnerWord4C = runner.Data[1];
        var stackWord4C = stackObject.Data[1];
        if (host is not null)
        {
            host.RunGenericSimsCall01InteractionPhase(runner, stackObject, runnerContainer, stackContainer);
        }
        else
        {
            if (stackContainer is not null && context.World.CanPlaceRelativeObject(runner, runner.Position, stackContainer, 0))
            {
                context.World.PlaceObject(runner, runner.Position, context.Stack, stackContainer, 0);
            }
            if (runnerContainer is not null && context.World.CanPlaceRelativeObject(stackObject, stackObject.Position, runnerContainer, 0))
            {
                context.World.PlaceObject(stackObject, stackObject.Position, context.Stack, runnerContainer, 0);
            }
        }

        runner.Data[1] = stackWord4C;
        stackObject.Data[1] = runnerWord4C;
        runner[ObjectData.Direction] = stackWord4C;
        stackObject[ObjectData.Direction] = runnerWord4C;

        if (host is not null)
        {
            host.RunGenericSimsCall01RoutePhase(runner, stackObject, runnerContainer!, stackContainer);
        }
        else
        {
            if (runner is XPerson runnerPerson && stackContainer is not null && stackContainer.RoutingSlots.Count > 0)
            {
                var slot = stackContainer.RoutingSlots[0];
                runnerPerson.Position = LotGeometry.SlotPosition(stackContainer.Position, stackContainer.Data[1], slot.X, slot.Y);
            }
            if (stackObject is XPerson stackPerson && runnerContainer is not null && runnerContainer.RoutingSlots.Count > 0)
            {
                var slot = runnerContainer.RoutingSlots[0];
                stackPerson.Position = LotGeometry.SlotPosition(runnerContainer.Position, runnerContainer.Data[1], slot.X, slot.Y);
            }
        }
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallInitialization(in TreeContext context, int mode)
    {
        context.World.Host?.RunGenericSimsCallInitialization(mode);
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTwentySix(in TreeContext context, XObject me)
    {
        var selector = me.Temporaries[0];
        var index = unchecked((short)(me.Temporaries[1] + 1));
        me.Temporaries[0] = 0;
        if (selector == 0) return PrimitiveResult.True;

        byte[]? bytes = null;
        if (context.Frame.Table is BehaviorScope scope)
        {
            bytes = scope.StringTableBytes(context.Frame.TreeId, unchecked((ushort)selector), index, context.World.Language);
        }
        else if (context.World.Host is { } host)
        {
            bytes = host.GenericSimsCall26StringBytes(context.Frame, selector, index);
        }
        else
        {
            var fallbackScope = me.TypeRecord?.BehaviorScope as BehaviorScope
                ?? context.StackObject?.TypeRecord?.BehaviorScope as BehaviorScope;
            bytes = fallbackScope?.StringTableBytes(context.Frame.TreeId, unchecked((ushort)selector), index, context.World.Language);
        }
        if (bytes is null)
        {
            me.Temporaries[0] = -1;
            return PrimitiveResult.False;
        }

        var position = 0;
        var count = 0;
        while (position < bytes.Length && bytes[position] != 0 && count < 7)
        {
            var value = 0;
            while (position < bytes.Length && bytes[position] is >= (byte)'0' and <= (byte)'9')
            {
                value = unchecked(value * 10 + bytes[position] - (byte)'0');
                position++;
            }

            count++;
            me.Temporaries[count] = unchecked((short)value);
            // Advances by one byte between fields even when no delimiter character is present.
            if (position < bytes.Length && bytes[position] != 0) position++;
        }

        me.Temporaries[0] = (short)count;
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTwentySevenOrTwentyEight(
        in TreeContext context,
        XObject me,
        GenericSimsCallRequestKind requestKind)
    {
        if (context.Frame.WaitState == 0 || !context.World.GenericSimsCallRequests.HasPending(me))
        {
            context.World.GenericSimsCallRequests.Start(me, me.Temporaries[0], requestKind);
            context.Frame.WaitState = 1;
            return PrimitiveResult.Stop;
        }

        var result = context.World.GenericSimsCallRequests.Poll(me);
        if (result is null) return PrimitiveResult.Stop;

        me.Temporaries[0] = result.Value.Temporary0;
        me.Temporaries[1] = result.Value.Status == 2 ? result.Value.Temporary1 : (short)0;
        return PrimitiveResult.True;
    }

    private static PrimitiveResult ExecuteCallTwentyFour(in TreeContext context, XObject me)
    {
        var entry = me.ObjectTableEntry(context.Frame.StackObject);
        var resolved = entry as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var target = context.World.GenericSimsCallRecordCallbackTarget;
        if (target is null) return PrimitiveResult.False;

        var host = context.World.Host;
        if (resolved.TypeRecord is null)
        {
            if (host is not null) host.GenericSimsCall22Finalize(resolved);
            return PrimitiveResult.False;
        }

        var personRecordIndex = context.World.PersonRecordIndex(resolved);
        if (personRecordIndex < 0)
        {
            if (host is not null) host.GenericSimsCall22Finalize(resolved);
            return PrimitiveResult.False;
        }
        var personData = context.World.PersonRecordData(personRecordIndex);
        var oldStars = personData is not null ? personData[(int)PersonData.FameStarPower09] : (short)0;
        var result = context.World.TryDecrementFameStarPower(
            personRecordIndex, target.FameStarThresholds);
        if (result && personData is not null)
        {
            var newStars = personData[(int)PersonData.FameStarPower09];
            context.World.OnStarPowerChange(resolved.Id, oldStars, newStars);
        }
        if (host is not null) host.GenericSimsCall22Finalize(resolved);
        else context.World.DispatchNotice(0x110, resolved.Id);
        return result ? PrimitiveResult.True : PrimitiveResult.False;
    }

    private static PrimitiveResult ExecuteCallEleven(in TreeContext context, XObject me)
    {
        if (me is not XPerson resolved)
            throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var world = context.World;
        var host = world.Host;
        if (host is null)
        {
            world.RunGenericSimsCall11Native(
                resolved,
                me.Temporaries[0],
                me.Temporaries[1],
                me.Temporaries[2],
                me.Temporaries[3]);
        }
        else
        {
            var target = host.GenericSimsCall11CallbackTarget();
            if (target is not null && host.PassesAutonomyCallbackGate(target))
                host.InvokeGatedObjectCallback(target, new GatedObjectCallbackRequest(3, 0x10, 0, 0));

            host.RunGenericSimsCall11Finalize(
                resolved,
                me.Temporaries[0],
                me.Temporaries[1],
                me.Temporaries[2],
                me.Temporaries[3]);
        }
        context.World.GlobalPerson.ByteE1 = 1;
        return PrimitiveResult.True;
    }

    /// <summary>Removes the neighbor identified by the stack object from their family, returning false if
    /// the neighbor cannot be found or removed.</summary>
    private static PrimitiveResult ExecuteCallSix(in TreeContext context)
    {
        if (!context.World.Neighbors.TryGetValue(context.Frame.StackObject, out var neighbor))
            throw new SimulationException(SimulationError.NeighborNotFound);
        var host = context.World.Host;
        var target = host?.GenericSimsCall06CallbackTarget();
        if (host is not null && target is not null && host.PassesAutonomyCallbackGate(target))
            host.InvokeGatedObjectCallback(target, new GatedObjectCallbackRequest(3, 0x10, 0x1b, 0));

        return RemoveFromFamily(context.World, neighbor) ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>Transfers the neighbor identified by the stack object into the specified family, removing
    /// them from any existing family first.</summary>
    private static PrimitiveResult ExecuteAddToFamily(in TreeContext context, short familyNumber)
    {
        var world = context.World;
        if (!world.Neighbors.TryGetValue(context.Frame.StackObject, out var neighbor))
            throw new SimulationException(SimulationError.NeighborNotFound);
        var families = world.NeighborhoodFamilies;
        if (families is null || familyNumber == 0 || !families.Exists(familyNumber)) return PrimitiveResult.False;
        if (neighbor.PersonData[(int)PersonData.FamilyNumber] != 0 && !RemoveFromFamily(world, neighbor)) return PrimitiveResult.False;
        return AddToFamily(world, neighbor, families, familyNumber) ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>Adds a neighbor to a family, updating their family number, resetting visitor status, and
    /// clearing the family modified flag.</summary>
    private static bool AddToFamily(SimulationWorld world, NeighborState neighbor, INeighborhoodFamilies families, short familyNumber)
    {
        var guid = neighbor.TypeRecordKey;
        if (families.HasMember(familyNumber, guid)) return false;
        families.AddMember(familyNumber, guid);
        world.RefreshNeighborInteractionManager();
        if (world.WalkOrder.OfType<XPerson>().FirstOrDefault(item => item.TypeRecord?.Key == guid) is { } person)
        {
            person[PersonData.FamilyNumber] = familyNumber;
            person[PersonData.LingeringHouseNumber] = 0;
        }
        neighbor.PersonData[(int)PersonData.FamilyNumber] = familyNumber;
        neighbor.PersonData[(int)PersonData.LingeringHouseNumber] = 0;
        world.DirtyRelationshipFamilies.Add(familyNumber);
        world.DispatchNotice(0xe8, 0);
        return true;
    }

    /// <summary>Removes a neighbor from their family, clearing their family number, updating visitor
    /// instance types, and setting the family dirty state.</summary>
    private static bool RemoveFromFamily(SimulationWorld world, NeighborState neighbor)
    {
        var familyNumber = neighbor.PersonData[(int)PersonData.FamilyNumber];
        if (familyNumber == 0) return false;
        var families = world.NeighborhoodFamilies;
        var guid = neighbor.TypeRecordKey;
        if (families is null || !families.HasMember(familyNumber, guid)) return false;

        if (world.WalkOrder.OfType<XPerson>().FirstOrDefault(item => item.TypeRecord?.Key == guid) is { } person)
            person[PersonData.FamilyNumber] = 0;
        neighbor.PersonData[(int)PersonData.FamilyNumber] = 0;
        families.RemoveMember(familyNumber, guid);
        world.RefreshNeighborInteractionManager();
        world.DirtyRelationshipFamilies.Add(familyNumber);
        world.DispatchNotice(0xe8, 0);
        return true;
    }
}
