using SimsEngine.Content;
using SimsEngine.Formats;

namespace SimsEngine.Simulation;

/// <summary>The Generic Sims Call primitive (opcode 1), after Sims.exe 0x44f7e6, the global sim call. Its operand word 0 is
/// the call number, a signed short from 0 to 0x2b; any other number, including 7 and 0x16, raises "Invalid global sim
/// call." (42), as Sims.exe's jump table sends those to 0x45145f.
/// <para>Port status, by call number (the code address Sims.exe's jump table at 0x45147a gives, in
/// <see cref="CaseAddresses"/>): 0x02 takes the frame stack object's 1-based entry from the runner's +0xdc table
/// (a zero, out-of-range or missing entry is error 10), requires the runner to be a person (error 28), and when the
/// person's raw +0xa84 dword is nonzero stores the entry at +0xa54, always answering
/// true. 0x0c takes the frame stack object's 1-based entry the same way (error 10), gets its orientation from
/// the native lot manager's +0x84 orientation, applies the native in-lot table mappings from
/// FUN_00434FFD/FUN_00407761 (using <see cref="ISimulationHost.TileTransformByte"/> only outside the verified
/// table domain), and writes the negated sum to temporary 0, always answering true. 0x0a appends the runner to its lot-list
/// (<see cref="SimulationWorld.MaintainLotList"/>, FUN_00443bad) and 0x0b removes its first matching entry
/// (<see cref="SimulationWorld.RemoveFromLotList"/>, FUN_00443c37); both send notice 0x108 only when emptiness changes.
/// Both answer true. 0x0e writes
/// the running object's temporary 0 to effect word 31
/// (<see cref="XObject.SetObjectTableEffectWord"/>, FUN_0046dae0 with the table at +0x7c of its +0xdc lot table)
/// and answers true. 0x0f requires the runner to be a person (error 28), writes
/// temporary 0 and 0 to its route footprint fields (<see cref="PersonData.RouteFootprintMask"/> and
/// <see cref="PersonData.FootprintExtension"/>, the words at +0x61c and +0x61e) and answers true, refreshing the
/// expanded footprint box at +0x100 when temporary 0 is zero
/// (<see cref="XPerson.RefreshFootprintBounds"/>, FUN_00433dbb). 0x19 saves the running person's record
/// (<see cref="SimulationWorld.SavePersonRecord"/>, FUN_00425ab3; not a person is error 28) and answers true.
/// 0x1c classifies temporary 0 and answers true: 0x5a-0x5c becomes 0, 0x5d-0x62 becomes 1 (the shared tail at
/// 0x450769 writes di, which the prologue set to 1), otherwise the state class <see cref="SimulationWorld.StateClass"/>
/// (FUN_0042ba25) decides — 1 becomes 0, 2 becomes 1, anything else becomes -1.
/// 0x23 requires the runner's definition type to be a person (error 28), and when the runner is one stores it
/// at +0x10C of the object at DAT_006E4F10 (<see cref="SimulationWorld.GenericSimsCall23PersonSlotBase"/>; a
/// missing base answers
/// true with no store), always answering true. 0x25 takes the frame stack object's 1-based entry from the lot
/// object table (<see cref="ISimulationHost.ObjectTableEntry"/>; a zero, out-of-range or missing entry counts
/// as no person) and requires it to be a person (error 28), always answering true.
/// 0x12 takes the active-person record id (<see cref="SimulationWorld.ActivePersonIndex"/>, FUN_004E9918,
/// then <see cref="SimulationWorld.ActivePersonRecordId"/>, FUN_004E9744; verified 0x45073b-0x450770: no active
/// person answers true with temporary 0 untouched, a missing record writes -1, otherwise temporary 0 takes
/// [result+0xF0]), always answering true.
/// 0x1a resolves the active person the same way, then writes the stored slot (<see cref="SimulationWorld.StoredSlotPersonId"/>)
/// to temporary 0 with 1 to temporary 1 — or, when no slot is stored, the active-person record id to temporary 0
/// with 0 to temporary 1 (verified 0x450772-0x4507c5; the null-record fallback is inferred, see the case), always
/// answering true.
/// 0x15 resolves temporary 0 through the object's table and FUN_006359F2 (null is error 28), gets the looked-up
/// object's interaction scope, then runs the original count/search/fetch/dispatch/update loop. Executor float and
/// word writes update the native object; scope identity and its production table source remain host operations.
/// It always answers true (verified 0x4509ca-0x450b66,
/// jump table 0x45152a, and FUN_0041f748/F69e/F6fd/F7a9/F56d/F957/F42a8e3/F45d29f).
/// 0x17 runs the same null gate and per-object scope lookup, copies the first value-2/mode-7 entry's low word to
/// temporary 0, removes that entry, resets the cursor to zero, then copies the first value-2/mode-8 entry's low word
/// to temporary 1 and removes it if found. It always answers true (verified 0x450b6b-0x450c41).
/// 0x13/0x14 resolve temporary 0 through the running object's object table and FUN_006359F2, search its executor
/// scope for value 1 with mode 0/3 respectively, map the fetched entry's second dword to a person-record type key,
/// write that record's +0xf0 word (or -1) to temporary 0, decrement the scope entry, then persist neighborhood state
/// via FUN_00425316. A scope miss writes -1 and returns before persistence (verified 0x4507c7-0x4509c5).
/// 0x11 resolves the running object, conditionally dispatches the DAT_006F3ADC callback through the FUN_005DA398
/// gate, always runs FUN_0045A399 with temporaries 0-3, and sets the global +0xe1 byte (verified 0x4506a0-0x450736).
/// 0x06 resolves the frame's neighbor entry, conditionally calls the global +0x48/+0x18 object callback with
/// (3,0x10,0x1b,0) through the same FUN_005DA398 gate, then answers whether FUN_00426068 completed successfully
/// (verified 0x44fc93-0x44fd21 and 0x451040-0x45104f).
/// 0x03 calls FUN_004FC469 on DAT_006F2A08 to release and clear its manager handles and reset linked global state;
/// the stateful helper is a host operation (verified 0x44fb81-0x44fb8c and 004fc469.c).
/// 0x0d validates the frame stack object's lot-table entry (error 10), then calls FUN_00444346 to walk the live
/// person-manager vector in order and invoke vtable +0x38 with that entry except on the entry itself (verified
/// 0x44fe16-0x44fe5d and 0x444346-0x444399). Person recipients use cXPerson::FUN_0045e8fd: the native callback applies
/// its reentrancy guard and action/queue/runtime-reference invalidation, and dispatches a subject matching +0xb00 or
/// +0xa54 to the changed-code reset path. Cleanup callbacks for each released reference remain incomplete.
/// 0x04 resolves a one-based neighbor table entry (error 55 on a miss), requires the lot's interaction subject,
/// and answers true only when its optional +0xee-gated FUN_00426068 membership gate and FUN_00425fb0 duplicate-link gate pass
/// (verified 0x44fc1d-0x44fc92 and 0x45103b-0x45104f). Their remaining lot/person mutations are host effects only;
/// their results are fixed by the native gates.
/// 0x21 resolves the same neighbor table entry (error 55), looks up the runner's temporary-0 id in its interaction
/// vector by +0x10c (a miss answers false), then applies the same optional native-gated teardown and final check as 0x04.
/// Verified 0x450fca-0x45104f; receiver mutations remain host effects only and cannot veto the native result.
/// 0x05 finds the +0x10c table entry keyed by temporary 0; a miss answers false. A hit computes its original
/// removal value and charges the family budget as expense type 2 (verified 0x44fd26-0x44fd61).
/// 0x29 answers true (its code is the common return, 0x44f830). 0x2b runs the upkeep when the native lot-manager
/// pointer is present; the source function is FUN_00410B89. Its remaining resource and manager side effects are host-backed.
/// 0x2a answers true when the running object's temporary 0 is zero; otherwise it looks the temporary-0-th
/// lot-table entry up (no index error; a null entry answers true via FUN_006359F2),
/// then maps the object's type-record key to the first person-record index through
/// <see cref="SimulationWorld.PersonRecordIndex"/> (FUN_004E98C0; a miss is -1, not an error), then applies the
/// manager and state-class gates of FUN_004E99D7 in the world before delegating only FUN_00445489's selection effect
/// to <see cref="SimulationWorld.ApplyPersonRecordSelection"/>; selection flags and notice dispatch are native,
/// while object-module view effects still require a host callback.
/// Verified 0x4513E0-0x451442; always answers true.
/// 0x1d uses the same scope and action-array search/update path as 0x18 but dispatches only groups 0 through 4
/// (verified 0x4501fd-0x45042d and FUN_0040d0d9/0040d106/0040d16d, 0040d561/0040d591/0040d5fb,
/// 0040d879/0040d8a9/0040d913, 0040db91/0040dbc1/0040dc2b and 0040dea9/0040ded9/0040df43).
/// 0x1e searches one of those five groups and returns the match index; 0x1f returns the selected group's count.
/// Both call FUN_00458278/FUN_004EB227 after restoring +0x59c, then send the same global notice (verified
/// 0x450432-0x450563 and 0x450568-0x450696).
/// 0x10 searches group 0, then updates its query string at (match index + 1) modulo the list count; it commits
/// the scope only when that list is nonempty, then runs the FUN_00458278/global-notice tail (0x44fee8-0x44ffa9).
/// 0x1b returns 9 minus the number of distinct raw +0x13c values from the global manager vector that equal 40..48
/// (verified 0x450c85-0x450cdd); the field meaning and concrete vector owner remain host-side/unknown.
/// 0x20 walks active FAMI member keys and resolves each key to a live lot object. A found object skips the remaining
/// work; only a missing live object reaches record-based creation, mode-0 interaction-scope updates and route/
/// placement. The no-host path uses the source-mapped person factory and native location search/check/place methods;
/// the final +0x74 person callback remains unverified (verified 0x450d32-0x450fc9).
/// 0x22 indexes the running object's +0xdc object table with the frame stack-object index and calls FUN_006359F2
/// (null is error 28), then calls FUN_0040542D, which returns shared optional target DAT_006DE804. When present,
/// native code applies FUN_00404CEF's fame-score and six-skill gates, delegates only its mutual-friend gate to the
/// host, mutates Fame Star Power/high-watermark on success, then runs FUN_00446F29 (verified 0x451054-0x4510e3).
/// 0x24 uses the same object-table lookup, null gate, FUN_0040542D target and FUN_004E98C0 record-index mapping;
/// the world applies FUN_00404DF6's Fame Star Power gate/decrement to native person data, then the host runs the
/// shared FUN_00446F29 finalizer (0x4510e8-0x451177 and 00404df6.c).
/// 0x01 resolves the running object and the frame stack object, then resolves their ContainerId entries through
/// the runner's +0xdc table. The runner's entry is required (error 15), the stack object's container is optional,
/// the host runs vtable +0x50/+0x54/+0x88 interaction work, native code exchanges the pre-callback +0x4c words,
/// then the host runs animation +0x20/+0x2c routing (verified 0x44f844-0x44fb80, word exchange at 0x44f9e2-0x44f9f2).
/// 0, 8 and 9 call FUN_00429CC7 on the global object's +0x24 subobject with arguments 0, 1 and 2 respectively;
/// argument 0 additionally processes the IMISSUOH block. The shared call always answers true (0x44f81a-0x44f841).
/// 0x26 uses temporary 0 as a table selector, fetches the selected frame-table string at temporary 1 plus one,
/// parses up to seven decimal groups into temporaries 1-7, and returns their count in temporary 0. A failed lookup
/// writes -1 to temporary 0 and answers false (0x451244-0x45133a).
/// 0x27/0x28 start a person-keyed asynchronous request on the first invocation and return Stop; later invocations
/// poll until the manager yields status 2 or 3, then write temporary 0 and conditionally temporary 1 (0x45133f-0x4513df).
/// Every call from 0 to 0x2b has a case above; numbers outside that range raise "Invalid global sim call." Those calls
/// are inline blocks of this one function (0x44f81a to 0x451443, except the shared exits), not separate functions; the calls 0, 8 and 9
/// share the block at 0x44f823, which calls FUN_00429cc7 (system init) with the dword at +0x24 of the global at DAT_006e486c as its
/// receiver and 0, 1 or 2 as its argument (0x44f81e pushes edi, which the prologue set to 1 with push 1 / pop edi).</para>
/// <para>Inferred: the primitive's name, from the error text "Invalid global sim call.", which Sims.exe raises from the
/// same function.</para></summary>
public static class GenericSimsCallPrimitive
{
    private const short LastCall = 0x2b;
    private const uint InvalidCallAddress = 0x45145f;

    /// <summary>The code each call number runs in Sims.exe (the jump table at 0x45147a, entry n at +4n). Calls 7 and 0x16
    /// name <see cref="InvalidCallAddress"/>, the error exit.</summary>
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
                // Sims.exe reads [me + 0xdc] before the person gate (0x44fb91-0x44fc12);
                // FUN_00407498 defines the one-based bounds and null-slot behavior.
                var slot = me.ObjectTableEntry(context.Frame.StackObject)
                    ?? throw new SimulationException(SimulationError.ReferenceToTreeParameterWhenNoParameterExists);
                if (me is not XPerson placed)
                    throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
                if (placed.GenericSimsCall02GateWordA84 != 0) placed.SetCurrentActionSecondaryReference(slot);
                return PrimitiveResult.True;
            }
            case 0x0c:
            {
                // Sims.exe reads the running object's +0xdc table directly at 0x44fd8b-0x44fe11;
                // FUN_00407498 supplies the one-based bounds/null behavior.
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
                // ECX remains me+0xdc at 0x44fe58. FUN_00444346 broadcasts through the live person-manager
                // vector; the same native owner is used by changed-code reset.
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
                // Sims.exe reads [me + 0xdc] and applies its person/null gate at 0x4511e2-0x451243;
                // FUN_00407498 defines the one-based bounds and null-slot behavior.
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
                    // Inferred: the original dereferences the record chain without a null guard (verified
                    // 0x450772-0x4507c5); unreachable when the gate above passes, so a missing record writes -1
                    // like call 0x12's explicit null branch.
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
                // Sims.exe reads the running object's +0xdc table directly at 0x4513ef-0x451420;
                // FUN_00407498 supplies the one-based bounds/null behavior before the person cast.
                var entry = me.ObjectTableEntry(me.Temporaries[0]);
                // Sims.exe uses FUN_006359f2(entry, XObject, XPerson) here. The lot/object table stores
                // arbitrary XObjects, while this record walk requires the runtime person subtype.
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
        throw new NotImplementedException($"Generic Sims Call {call:x} (code at 0x{address:x6}) is not ported yet.");
    }

    private static PrimitiveResult ExecuteCallFifteen(in TreeContext context, XObject me)
    {
        // Sims.exe reads me+0x3a, then FUN_00407498 indexes me's one-based +0xdc table.
        var entry = me.ObjectTableEntry(me.Temporaries[0]);
        var item = entry as XPerson
            ?? throw new SimulationException(SimulationError.ObjectReferredToMustBeAPerson);
        var scope = context.World.InteractionExecutorScope(item);
        var remaining = scope.CountValue(2);
        if (remaining <= 0) return PrimitiveResult.True;

        // F6FD leaves this stack-local triple untouched on a failed fetch. Its initial words are
        // 0, 0, and 1 (0x450A57-0x450A63), so preserve it when the search/fetch misses.
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
            // Cases 7 and 8, and unsigned values above 12, jump straight to the loop decrement
            // in Sims.exe. They neither dispatch an executor nor update the scope table.
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

        // Sims.exe explicitly resets this dword to zero after removing the first entry. The next
        // F69E search therefore starts at index 1, even if compaction moved a mode-8 entry to index 0.
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
            // FUN_0044f7e6 first resolves the key through FUN_004e9744. A live lot person takes the branch that
            // advances the active-person cursor without touching its scope; only a missing lot object reaches the
            // effectful FUN_004e9855 creation and later route/placement callbacks.
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

    /// <summary>Port the no-host part of FUN_0044f7e6 case 0x20 after its scope updates: search around the running
    /// object's position with FUN_00434895's room/water filters, retry once without the center-room match, then run
    /// the placement check/action represented by the person's +0x54/+0x88 callbacks.</summary>
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
                    // FUN_0044f7e6 invokes the person's no-argument +0x74 callback after +0x88 placement.
                    // Mac cXPerson::Place performs UpdateCurrentRoom, and Windows person +0x74 refreshes
                    // cXPerson +0x60c from the current tile after +0x88 placement.
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
        // Sims.exe reads temporary 0 and resolves it through the running object's +0xdc table.
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

        // The original passes the second dword (the person-record type key) to the neighbor-record chain;
        // the third dword is the one-count scope lifetime consumed below.
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
        // The shared block always answers true; the manager initialization is the host's, and a hostless world has none.
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
            // Sims.exe advances one byte between fields, even when it is not a conventional delimiter.
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
            // The data snapshot and neighborhood save in FUN_0045A399 are native lot state. Only the gated
            // cWinPeople active-tool callback remains frontend-owned when the full simulation host is absent.
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

    /// <summary>Mac ObjectSim.s 0x100E9654: <c>Neighborhood::FindNeighborByID</c> on the stack object (error 0x37 when
    /// it is missing), the main window callback (3, 0x10, 0x1b, 0), then <c>Neighborhood::RemoveFromFamily</c>, which
    /// answers false when it returns -1.</summary>
    private static PrimitiveResult ExecuteCallSix(in TreeContext context)
    {
        if (!context.World.Neighbors.TryGetValue(context.Frame.StackObject, out var neighbor))
            throw new SimulationException(SimulationError.NeighborNotFound);
        // The gated callback object is a host effect; a hostless world has none, so only the removal below runs.
        var host = context.World.Host;
        var target = host?.GenericSimsCall06CallbackTarget();
        if (host is not null && target is not null && host.PassesAutonomyCallbackGate(target))
            host.InvokeGatedObjectCallback(target, new GatedObjectCallbackRequest(3, 0x10, 0x1b, 0));

        return RemoveFromFamily(context.World, neighbor) ? PrimitiveResult.True : PrimitiveResult.False;
    }

    /// <summary>Mac ObjectSim.s 0x100E95C8 (call 4, the household on the lot, ObjectModule +0x10) and 0x100EACFC (call
    /// 0x21, <c>Neighborhood::GetFamily</c> of temp 0): <c>FindNeighborByID</c> on the stack object (error 0x37), false
    /// when there is no such family, then a neighbor that has a family leaves it (<c>RemoveFromFamily</c>, false on -1)
    /// and <c>AddToFamily</c> answers true unless it returns -1.</summary>
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

    /// <summary>Mac <c>Neighborhood::AddToFamily</c> (Neighborhood.s): -1 (false) when the family already lists the
    /// neighbor's GUID. Otherwise the family appends it, <c>UpdateInstanceVisitorTypes</c> runs, the lot person with that
    /// GUID gets person data 61 (+0x606) = the family number and 67 (+0x612) = 0, the neighbor the same (+0xee, +0xfa),
    /// the family's +0x124 is set and +0x138 bit 3 cleared, and <c>GlobalDispatch(0xe8, 0)</c> follows.</summary>
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

    /// <summary>Mac <c>Neighborhood::RemoveFromFamily</c> (Neighborhood.s): false (-1) when the neighbor's family number
    /// (+0xee, person data 61) is 0 or its family does not list the neighbor's GUID. Otherwise the lot person with that
    /// GUID gets person data 61 (+0x606) = 0, the neighbor's family number is cleared, the family drops the member,
    /// <c>UpdateInstanceVisitorTypes</c> runs, the family's +0x124 is set and +0x138 bit 3 cleared, and
    /// <c>GlobalDispatch(0xe8, 0)</c> follows.</summary>
    private static bool RemoveFromFamily(SimulationWorld world, NeighborState neighbor)
    {
        var familyNumber = neighbor.PersonData[(int)PersonData.FamilyNumber];
        if (familyNumber == 0) return false;
        var families = world.NeighborhoodFamilies;
        var guid = neighbor.TypeRecordKey;
        if (families is null || !families.HasMember(familyNumber, guid)) return false;

        // ObjectModule::GetPersonByGUID: the lot person whose object GUID is the neighbor's.
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
