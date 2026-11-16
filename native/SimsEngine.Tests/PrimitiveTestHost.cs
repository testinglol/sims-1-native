using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

internal sealed class PrimitiveTestHost : ISimulationHost
{
    public Queue<uint> Randoms { get; } = [];
    public int RandomCalls { get; private set; }
    public Dictionary<TypeRecord, TypeRecord> Alternatives { get; } = [];

    public uint NextRandom()
    {
        RandomCalls++;
        return Randoms.Count > 0 ? Randoms.Dequeue() : 0;
    }

    public TypeRecord? AlternativeObjectType(TypeRecord entry)
    {
        Calls.Add($"AlternativeObjectType({entry.Key})");
        return Alternatives.GetValueOrDefault(entry);
    }

    public Dictionary<(short Table, int Index), string> Strings { get; } = [];
    public List<(short Table, int Index)> StringRequests { get; } = [];
    public List<(int Message, string Text)> Broadcasts { get; } = [];
    public HashSet<int> AcceptingBanks { get; } = [];
    public List<(int Bank, short Event)> SoundRequests { get; } = [];
    public List<short> PositionalSounds { get; } = [];
    public bool PositionalPlays { get; set; } = true;

    public IReadOnlyList<short>? Constants(StackFrame frame, ushort id) => null;
    public Dictionary<(short Room, short Field), short> RoomFields { get; } = [];
    public short? Room(short room, short field) => RoomFields.TryGetValue((room, field), out var value) ? value : null;
    public bool CareerExists(short career) => false;
    public short? JobData(short career, short level, short field) => null;
    public string? StringTableEntry(short table, int index)
    {
        StringRequests.Add((table, index));
        return Strings.GetValueOrDefault((table, index));
    }
    public void BroadcastString(int message, string text) => Broadcasts.Add((message, text));
    public bool PlaySoundEvent(int bank, short eventId)
    {
        SoundRequests.Add((bank, eventId));
        return AcceptingBanks.Contains(bank);
    }
    public bool PlayPositionalSound(short ownerId)
    {
        PositionalSounds.Add(ownerId);
        return PositionalPlays;
    }

    public List<string> Calls { get; } = [];
    public bool BurnEnabledAnswer { get; set; } = true;
    public TypeRecord? FireType { get; set; }
    public bool FootprintClearAnswer { get; set; } = true;
    public bool SpreadAnswer { get; set; } = true;
    public bool HasBalloons { get; set; } = true;
    public bool ClaimAnswer { get; set; } = true;
    public bool BalloonTextAnswer { get; set; } = true;
    public Dictionary<short, XObject> ObjectTable { get; } = [];
    public Dictionary<short, int> ListValues { get; } = [];

    public void RefreshDisplay() => Calls.Add("RefreshDisplay");
    public void RefreshRoom(ushort room) => Calls.Add($"RefreshRoom({room})");

    public bool BurnEnabled()
    {
        Calls.Add("BurnEnabled");
        return BurnEnabledAnswer;
    }
    public TypeRecord? FindObjectType(int key)
    {
        Calls.Add($"FindObjectType({key:X})");
        return FireType;
    }
    public bool BurnFootprintClear(int tileX, int tileY, int height, short direction)
    {
        Calls.Add($"BurnFootprintClear({tileX},{tileY},{height},{direction})");
        return FootprintClearAnswer;
    }
    public bool BurnSpread(XObject me, ObjectPosition position, TypeRecord? fire, bool strictFlags)
    {
        Calls.Add($"BurnSpread({me.Id},{position.X},{position.Y},{position.Height},{fire?.Key.ToString() ?? "null"},{strictFlags})");
        return SpreadAnswer;
    }

    public void SetObjectUiEffect(short id, bool flag) => Calls.Add($"SetObjectUiEffect({id},{flag})");
    public void ShowObjectUiEffect(short id, bool flag) => Calls.Add($"ShowObjectUiEffect({id},{flag})");
    public void ShowEntryUiEffect(short id, bool flag) => Calls.Add($"ShowEntryUiEffect({id},{flag})");

    public bool HasBalloonEntries(XObject owner)
    {
        Calls.Add($"HasBalloonEntries({owner.Id})");
        return HasBalloons;
    }
    public bool ClaimBalloon(XObject owner, int priority)
    {
        Calls.Add($"ClaimBalloon({owner.Id},{priority})");
        return ClaimAnswer;
    }
    public void SetBalloonState(XObject owner, int value) => Calls.Add($"SetBalloonState({owner.Id},{value})");
    public void SetBalloonStateTimed(XObject owner, int count) => Calls.Add($"SetBalloonStateTimed({owner.Id},{count})");
    public Action<XPerson, XObject>? OnPersonObjectReferenceNotification { get; set; }
    public void NotifyPersonObjectReference(XPerson recipient, XObject subject)
    {
        Calls.Add($"NotifyPersonObjectReference({recipient.Id},{subject.Id})");
        OnPersonObjectReferenceNotification?.Invoke(recipient, subject);
    }
    public void ShowBalloonForType(XObject owner, TypeRecord? alternative) =>
        Calls.Add($"ShowBalloonForType({owner.Id},{alternative?.Key.ToString() ?? "null"})");
    public int ListEntryValue(short index)
    {
        Calls.Add($"ListEntryValue({index})");
        return ListValues.GetValueOrDefault(index);
    }
    public void ShowBalloonForValue(XObject owner, int value) => Calls.Add($"ShowBalloonForValue({owner.Id},{value})");
    public bool ShowBalloonText(byte table, int textId, int flags)
    {
        Calls.Add($"ShowBalloonText({table},{textId},{flags})");
        return BalloonTextAnswer;
    }
    public short BalloonString(byte table, short index)
    {
        Calls.Add($"BalloonString({table},{index})");
        return 7;
    }
    public int BalloonDuration(byte kind)
    {
        Calls.Add($"BalloonDuration({kind})");
        return 100 + kind;
    }
    public void SetBalloonFields(XObject owner, int duration, int label, bool flag) =>
        Calls.Add($"SetBalloonFields({owner.Id},{duration},{label},{flag})");
    public void NotifyObjectTable(XObject owner) => Calls.Add($"NotifyObjectTable({owner.Id})");

    public bool PlacementAnswer { get; set; } = true;
    public int? DialogState { get; set; }
    public byte DialogGlobalFlagF8Answer { get; set; }

    private static string Who(XObject? who) => who?.Id.ToString() ?? "null";

    public bool CanGrab(XObject item, ObjectPosition at, XObject? who, int flag)
    {
        Calls.Add($"CanGrab({item.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
        return PlacementAnswer;
    }
    public void Grab(XObject item, ObjectPosition at, XObject? who, int flag) =>
        Calls.Add($"Grab({item.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
    public bool CanDrop(XObject item, ObjectPosition at, XObject? who, int flag)
    {
        Calls.Add($"CanDrop({item.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
        return PlacementAnswer;
    }
    public void Drop(XObject item, ObjectPosition at, XObject? who, int flag) =>
        Calls.Add($"Drop({item.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
    public bool CanDropOnto(XObject target, ObjectPosition at, XObject? who, int flag)
    {
        Calls.Add($"CanDropOnto({target.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
        return PlacementAnswer;
    }
    public void DropOnto(XObject target, ObjectPosition at, XObject? who, int flag) =>
        Calls.Add($"DropOnto({target.Id},{at.X},{at.Y},{at.Height},{Who(who)},{flag})");
    public int? DialogRecordState(XObject owner)
    {
        Calls.Add($"DialogRecordState({owner.Id})");
        return DialogState;
    }
    public byte DialogGlobalFlagF8()
    {
        Calls.Add("DialogGlobalFlagF8");
        return DialogGlobalFlagF8Answer;
    }
    public void DismissDialogRecord(XObject owner)
    {
        Calls.Add($"DismissDialogRecord({owner.Id})");
        DialogState = 5;
    }
    public void DismissDialogRecords(XObject owner)
    {
        Calls.Add($"DismissDialogRecords({owner.Id})");
        DialogState = null;
    }
    public void ShowDialog(XPerson owner, byte style) => Calls.Add($"ShowDialog({owner.Id},{style})");

    public int TurnStatusAnswer { get; set; }
    public bool TryStartAnswer { get; set; } = true;

    public void SetLookTarget(XObject me, short target) => Calls.Add($"SetLookTarget({me.Id},{target})");
    public void TurnTowards(XObject me, ObjectPosition? point)
    {
        var at = point is { } p ? $"{p.X},{p.Y}" : "null";
        Calls.Add($"TurnTowards({me.Id},{at})");
    }
    public int TurnStatus(XObject me)
    {
        Calls.Add($"TurnStatus({me.Id})");
        return TurnStatusAnswer;
    }
    public bool TryStartTurn(XObject me)
    {
        Calls.Add($"TryStartTurn({me.Id})");
        return TryStartAnswer;
    }

    public bool RoutingEnabledAnswer { get; set; } = true;
    public PrimitiveResult RouteAnswer { get; set; } = PrimitiveResult.True;
    public PrimitiveResult AnimateAnswer { get; set; } = PrimitiveResult.True;
    public Dictionary<short, List<RoutingSlotRequest>> StackRoutingSlots { get; } = [];
    public List<RoutingSlotRequest> LotRoutingSlots { get; } = [];
    public List<RoutingSlotRequest?> Routes { get; } = [];

    public bool RoutingEnabled()
    {
        Calls.Add("RoutingEnabled");
        return RoutingEnabledAnswer;
    }
    public int RoutingSlotCount(XObject stackObject)
    {
        Calls.Add($"RoutingSlotCount({stackObject.Id})");
        return StackRoutingSlots.GetValueOrDefault(stackObject.Id)?.Count ?? 0;
    }
    public RoutingSlotRequest RoutingSlotAt(XObject stackObject, int index)
    {
        Calls.Add($"RoutingSlotAt({stackObject.Id},{index})");
        return StackRoutingSlots[stackObject.Id][index];
    }
    public int LotRoutingSlotCount(XObject me)
    {
        Calls.Add($"LotRoutingSlotCount({me.Id})");
        return LotRoutingSlots.Count;
    }
    public RoutingSlotRequest LotRoutingSlotAt(XObject me, int index)
    {
        Calls.Add($"LotRoutingSlotAt({me.Id},{index})");
        return LotRoutingSlots[index];
    }
    public PrimitiveResult Route(XObject me, StackFrame frame, RoutingSlotRequest? target)
    {
        Calls.Add($"Route({me.Id},{(target is null ? "null" : "slot")})");
        Routes.Add(target);
        return RouteAnswer;
    }
    public PrimitiveResult AnimateAvatar(XPerson me, StackFrame frame, ReadOnlySpan<byte> operand)
    {
        Calls.Add($"AnimateAvatar({me.Id},{Convert.ToHexString(operand)})");
        return AnimateAnswer;
    }

    public bool EffectStarts { get; set; } = true;

    public void StopOwnerSounds(short ownerId) => Calls.Add($"StopOwnerSounds({ownerId})");
    public void ChangeActionString(XObject me, short stackObject, string? text) =>
        Calls.Add($"ChangeActionString({me.Id},{stackObject},{text ?? "null"})");
    public PrimitiveResult SnapshotAnswer { get; set; } = PrimitiveResult.True;
    public PrimitiveResult RunSnapshot(XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, bool snapTargetFlag,
        int directionMask, bool slotFlag, (int Dy, int Dx, int Ddir)? relative)
    {
        var offset = relative is { } r ? $"{r.Dy},{r.Dx},{r.Ddir}" : "null";
        Calls.Add($"RunSnapshot({me.Id},{target?.Id.ToString() ?? "null"},{position.X},{position.Y},{position.Height},{snapTargetSlot},{snapTargetFlag},{directionMask},{slotFlag},{offset})");
        return SnapshotAnswer;
    }
    public void SetEffectWord(short index, short value) => Calls.Add($"SetEffectWord({index},{value})");
    public bool PlaySpecialEffect(XObject me, XObject stackObject, SpecialEffectRequest request)
    {
        Calls.Add($"PlaySpecialEffect({me.Id},{stackObject.Id},{request.DurationMs},{request.Byte2},{request.Byte3},{request.Byte4},{request.Byte5})");
        return EffectStarts;
    }

    public AutonomyLot Lot { get; set; } = new(true, false, false, 0, 0, 0, null);
    public Dictionary<short, List<InteractionRecord>> Records { get; } = [];
    public AutonomyLot AutonomyLotState() => Lot;
    public bool PushAnswer { get; set; } = true;
    public bool PushInteraction(XPerson person, XObject owner, byte interactionId, int priority, XObject? secondary, int flags)
    {
        Calls.Add($"PushInteraction({person.Id},{owner.Id},{interactionId},{priority},{Who(secondary)},{flags:X})");
        return PushAnswer;
    }

    public XObject? NewCharacter { get; set; }

    public XObject? CreateCharacter(int mode)
    {
        Calls.Add($"CreateCharacter({mode})");
        return NewCharacter;
    }
    public Func<int, sbyte> TileTransformByteAnswer { get; set; } = _ => 0;

    public sbyte TileTransformByte(int index)
    {
        Calls.Add($"TileTransformByte({index})");
        return TileTransformByteAnswer(index);
    }
    public Func<int, int> ActivePersonIndexAnswer { get; set; } = _ => -1;

    public int ActivePersonIndex(int position)
    {
        Calls.Add($"ActivePersonIndex({position})");
        return ActivePersonIndexAnswer(position);
    }

    public Func<int, short?> ActivePersonRecordIdAnswer { get; set; } = _ => null;

    public short? ActivePersonRecordId(int index)
    {
        Calls.Add($"ActivePersonRecordId({index})");
        return ActivePersonRecordIdAnswer(index);
    }

    public short? StoredSlotPersonIdAnswer { get; set; }

    public short? StoredSlotPersonId()
    {
        Calls.Add("StoredSlotPersonId");
        return StoredSlotPersonIdAnswer;
    }

    public InteractionBehaviorRuntimeContext? InteractionBehaviorQueryContextAnswer { get; set; } = new();
    public Dictionary<int, InteractionBehaviorActionList?> InteractionBehaviorLists { get; } = [];
    public InteractionBehaviorScope? LastInteractionBehaviorScope { get; private set; }
    public bool? LastInteractionBehaviorScopeCommit { get; private set; }
    public InteractionBehaviorRuntimeContext? BeginInteractionBehaviorScope(InteractionBehaviorScope scope)
    {
        Calls.Add($"BeginInteractionBehaviorScope({scope.TypeRecord?.Key.ToString() ?? "null"},{scope.Mode})");
        LastInteractionBehaviorScope = scope;
        return InteractionBehaviorQueryContextAnswer;
    }
    public InteractionBehaviorActionList? InteractionBehaviorActions(InteractionBehaviorScope scope, int listIndex)
    {
        Calls.Add($"InteractionBehaviorActions({listIndex})");
        return InteractionBehaviorLists.GetValueOrDefault(listIndex);
    }
    public void EndInteractionBehaviorScope(InteractionBehaviorScope scope, bool commit)
    {
        LastInteractionBehaviorScopeCommit = commit;
        Calls.Add("EndInteractionBehaviorScope");
    }
    public TypeRecord? FinalizedInteractionBehaviorTypeRecord { get; private set; }
    public void FinalizeInteractionBehaviorTypeRecord(TypeRecord? typeRecord)
    {
        Calls.Add($"FinalizeInteractionBehaviorTypeRecord({typeRecord?.Key.ToString() ?? "null"})");
        FinalizedInteractionBehaviorTypeRecord = typeRecord;
    }
    public void RefreshInteractionBehaviorTypeRecord(TypeRecord? typeRecord) =>
        Calls.Add($"RefreshInteractionBehaviorTypeRecord({typeRecord?.Key.ToString() ?? "null"})");
    public void NotifyInteractionBehaviorGlobal(uint mask) => Calls.Add($"NotifyInteractionBehaviorGlobal({mask:X8})");
    public Dictionary<int, XPerson?> GenericSimsCall20FallbackPersons { get; } = [];
    public XPerson? GenericSimsCall20FallbackPerson(int position)
    {
        Calls.Add($"GenericSimsCall20FallbackPerson({position})");
        return GenericSimsCall20FallbackPersons.GetValueOrDefault(position);
    }
    public void GenericSimsCall20FinalizePerson(XPerson person) => Calls.Add($"GenericSimsCall20FinalizePerson({person.Id})");
    public Action<XObject, XObject>? GenericSimsCall01InteractionPhaseAction { get; set; }
    public void RunGenericSimsCall01InteractionPhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer)
    {
        Calls.Add($"RunGenericSimsCall01InteractionPhase({runner.Id},{stackObject.Id},{runnerContainer.Id},{stackContainer?.Id.ToString() ?? "null"})");
        GenericSimsCall01InteractionPhaseAction?.Invoke(runner, stackObject);
    }
    public void RunGenericSimsCall01RoutePhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer) =>
        Calls.Add($"RunGenericSimsCall01RoutePhase({runner.Id},{stackObject.Id},{runnerContainer.Id},{stackContainer?.Id.ToString() ?? "null"};{runner.Data[1]},{stackObject.Data[1]})");
    public void GenericSimsCall22Finalize(XObject resolved) => Calls.Add($"GenericSimsCall22Finalize({resolved.Id})");

    public short? ScopedNeighborPersonRecordIdAnswer { get; set; }
    public short? ScopedNeighborPersonRecordId(int personRecordKey)
    {
        Calls.Add($"ScopedNeighborPersonRecordId({personRecordKey})");
        return ScopedNeighborPersonRecordIdAnswer;
    }
    public void PersistNeighborhoodState() => Calls.Add("PersistNeighborhoodState");

    public XObject? GenericSimsCall11CallbackTargetAnswer { get; set; }
    public bool GenericSimsCall11CallbackGateAnswer { get; set; }
    public XObject? GenericSimsCall11CallbackTarget()
    {
        Calls.Add("GenericSimsCall11CallbackTarget");
        return GenericSimsCall11CallbackTargetAnswer;
    }
    public bool PassesAutonomyCallbackGate(XObject target)
    {
        Calls.Add($"PassesAutonomyCallbackGate({target.Id})");
        return GenericSimsCall11CallbackGateAnswer;
    }
    public void InvokeGatedObjectCallback(XObject target, GatedObjectCallbackRequest request) =>
        Calls.Add($"InvokeGatedObjectCallback({target.Id},{request.First},{request.Second},{request.Third},{request.Fourth})");
    public XObject? GenericSimsCall06CallbackTargetAnswer { get; set; }
    public XObject? GenericSimsCall06CallbackTarget()
    {
        Calls.Add("GenericSimsCall06CallbackTarget");
        return GenericSimsCall06CallbackTargetAnswer;
    }
    public void RunGenericSimsCall11Finalize(XObject resolved, short temporary0, short temporary1, short temporary2, short temporary3) =>
        Calls.Add($"RunGenericSimsCall11Finalize({resolved.Id},{temporary0},{temporary1},{temporary2},{temporary3})");
    public bool GenericSimsCallRequestManagerAvailableAnswer { get; set; } = true;
    public bool GenericSimsCallRequestManagerAvailable()
    {
        Calls.Add("GenericSimsCallRequestManagerAvailable");
        return GenericSimsCallRequestManagerAvailableAnswer;
    }
    public Func<GenericSimsCallPendingRequest, XObject, GenericSimsCallRequestCompletion>? GenericSimsCallRequestProcessor { get; set; }
    public GenericSimsCallRequestCompletion ProcessGenericSimsCallRequest(GenericSimsCallPendingRequest request, XObject target)
    {
        Calls.Add($"ProcessGenericSimsCallRequest({request.Id},{request.PersonKey},{request.Temporary0},{(int)request.Kind},{target.Id})");
        return GenericSimsCallRequestProcessor?.Invoke(request, target)
            ?? throw new InvalidOperationException("No generic call request processor was configured.");
    }
    public void ReleaseGenericSimsCall03PrimaryHandle(int handle) => Calls.Add($"ReleaseGenericSimsCall03PrimaryHandle({handle})");
    public void ReleaseGenericSimsCall03SecondaryHandle(int handle) => Calls.Add($"ReleaseGenericSimsCall03SecondaryHandle({handle})");
    public void RunGenericSimsCallInitialization(int mode) => Calls.Add($"RunGenericSimsCallInitialization({mode})");
    public byte[]? GenericSimsCall26StringBytesAnswer { get; set; }
    public byte[]? GenericSimsCall26StringBytes(StackFrame frame, short selector, short oneBasedIndex)
    {
        Calls.Add($"GenericSimsCall26StringBytes({selector},{oneBasedIndex})");
        return GenericSimsCall26StringBytesAnswer;
    }

    public void SelectedPersonChanged(XObject person) => Calls.Add($"SelectedPersonChanged({person.Id})");
    public List<int> IdTableEntries { get; set; } = [];

    public IReadOnlyList<int> GlobalIdTable()
    {
        Calls.Add("GlobalIdTable");
        return IdTableEntries;
    }
    public List<int> CareerEntries { get; set; } = [];

    public IReadOnlyList<int> CareerTable()
    {
        Calls.Add("CareerTable");
        return CareerEntries;
    }
    public Func<int, XObject?> CreateObjectAnswer { get; set; } = _ => null;
    public Func<int, int, int, int?, bool> TileHasObjectAnswer { get; set; } = (_, _, _, _) => false;
    public Func<int, int, int, short> RoomAtTileAnswer { get; set; } = (_, _, _) => 0;
    public Func<int, int, int, int, bool> FacingClearAnswer { get; set; } = (_, _, _, _) => true;

    public XObject? CreateObjectInstance(int guidKey)
    {
        Calls.Add($"CreateObjectInstance({guidKey})");
        return CreateObjectAnswer(guidKey);
    }
    public bool TileHasObject(int tileX, int tileY, int level, int? guidKey)
    {
        Calls.Add($"TileHasObject({tileX},{tileY},{level},{guidKey?.ToString() ?? "null"})");
        return TileHasObjectAnswer(tileX, tileY, level, guidKey);
    }
    public short RoomAtTile(int tileX, int tileY, int level)
    {
        Calls.Add($"RoomAtTile({tileX},{tileY},{level})");
        return RoomAtTileAnswer(tileX, tileY, level);
    }
    public bool FacingClear(int tileX, int tileY, int level, int facing)
    {
        Calls.Add($"FacingClear({tileX},{tileY},{level},{facing})");
        return FacingClearAnswer(tileX, tileY, level, facing);
    }
    public bool CanPlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method)
    {
        Calls.Add($"CanPlaceCreated({created.Id},{at.X},{at.Y},{at.Height},{Who(relativeTo)},{method})");
        return PlacementAnswer;
    }
    public void PlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method) =>
        Calls.Add($"PlaceCreated({created.Id},{at.X},{at.Y},{at.Height},{Who(relativeTo)},{method})");
    public void DiscardObject(XObject created) => Calls.Add($"DiscardObject({created.Id})");
    public void RotateCreatedObject(XObject created) => Calls.Add($"RotateCreatedObject({created.Id})");
    public void InitCreatedPerson(XPerson created) => Calls.Add($"InitCreatedPerson({created.Id})");
    public void ArriveCreatedPerson(XPerson created) => Calls.Add($"ArriveCreatedPerson({created.Id})");

    public Func<XObject, int> SoundBankAnswer { get; set; } = _ => 0;

    public int SoundBankOf(XObject obj)
    {
        Calls.Add($"SoundBankOf({obj.Id})");
        return SoundBankAnswer(obj);
    }

    public bool FindLocationAnswer { get; set; } = true;
    public ObjectPosition FoundTile { get; set; }

    public bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found)
    {
        Calls.Add($"FindLocation({stackObject.Id},{parameters.CenterX},{parameters.CenterY},{parameters.Height},{parameters.Direction},{parameters.ContinueFlag},{parameters.FlagB},{parameters.FlagC})");
        found = FoundTile;
        return FindLocationAnswer;
    }
    public bool CanPlaceAt(XObject stackObject, ObjectPosition at)
    {
        Calls.Add($"CanPlaceAt({stackObject.Id},{at.X},{at.Y},{at.Height})");
        return PlacementAnswer;
    }
    public void PlaceAt(XObject stackObject, ObjectPosition at) =>
        Calls.Add($"PlaceAt({stackObject.Id},{at.X},{at.Y},{at.Height})");
    public void AlignMultitileParts(XObject stackObject) => Calls.Add($"AlignMultitileParts({stackObject.Id})");
    public void RotateForPlacement(XObject stackObject, int steps) =>
        Calls.Add($"RotateForPlacement({stackObject.Id},{steps})");
    public void FinishLocationSearch(XObject stackObject) => Calls.Add($"FinishLocationSearch({stackObject.Id})");
    public void FinalizePlacement(XObject stackObject) => Calls.Add($"FinalizePlacement({stackObject.Id})");
    public XObject? QueuedActionTarget { get; set; }
    public bool PlaceAnswer { get; set; } = true;
    public (XObject? Holder, int Value) HolderAnswer { get; set; }
    public float SlotPositionAnswer { get; set; } = 1f;
    public Queue<int> AvatarEvents { get; } = [];
    public bool ReachDoneAnswer { get; set; }
    public bool StartReachAnswer { get; set; } = true;
    public ActionQueueSlot? StartedAction { get; private set; }
    public bool SetNonInterruptibleDuringQueuedActionStart { get; set; }

    public XObject? StartQueuedAction(XPerson me, ActionQueueSlot action)
    {
        Calls.Add($"StartQueuedAction({me.Id})");
        StartedAction = action;
        if (SetNonInterruptibleDuringQueuedActionStart) me[PersonData.NonInterruptible] = 1;
        return QueuedActionTarget;
    }
    public (XObject? Holder, int Value) GrabHolder(XObject target)
    {
        Calls.Add($"GrabHolder({target.Id})");
        return HolderAnswer;
    }
    public float SlotPosition(int slot)
    {
        Calls.Add($"SlotPosition({slot})");
        return SlotPositionAnswer;
    }
    public bool NextAvatarEvent(XPerson me, out int code)
    {
        Calls.Add($"NextAvatarEvent({me.Id})");
        if (AvatarEvents.Count > 0)
        {
            code = AvatarEvents.Dequeue();
            return true;
        }
        code = 0;
        return false;
    }
    public bool ReachAnimationDone(XPerson me)
    {
        Calls.Add($"ReachAnimationDone({me.Id})");
        return ReachDoneAnswer;
    }
    public void FinishReach(XPerson me) => Calls.Add($"FinishReach({me.Id})");
    public bool StartReachAnimation(XPerson me, float position)
    {
        Calls.Add($"StartReachAnimation({me.Id},{position})");
        return StartReachAnswer;
    }
    public bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag)
    {
        Calls.Add($"CanPlace({target.Id},{at.X},{at.Y},{at.Height},{who.Id},{flag})");
        return PlaceAnswer;
    }
    public void Place(XObject target, ObjectPosition at, XObject who, int flag) =>
        Calls.Add($"Place({target.Id},{at.X},{at.Y},{at.Height},{who.Id},{flag})");
}

internal sealed class TreeTable(params (short Id, Bhav Tree)[] trees) : ITreeTable
{
    private readonly Dictionary<short, Bhav> _trees = trees.ToDictionary(entry => entry.Id, entry => entry.Tree);

    public Bhav? Find(short id) => _trees.GetValueOrDefault(id);
}

internal static class PrimitiveTestWorld
{
    public static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    public static Bhav Tree(byte type, params (ushort Opcode, byte True, byte False, byte[] Operand)[] code)
    {
        var chunk = new ChunkBuilder().U16(0x8002).S16((short)code.Length).U8(type).U8(0).U8(0).U8(0).U32(0);
        foreach (var (opcode, t, f, operand) in code) chunk.U16(opcode).U8(t).U8(f).Raw(operand);
        var (file, iff) = chunk.InFile("BHAV");
        return Bhav.Read(file, iff);
    }

    public static byte[] Expression(short lhs, short rhs, byte op, byte lhsOwner = 8, byte rhsOwner = 7) =>
        [(byte)lhs, (byte)(lhs >> 8), (byte)rhs, (byte)(rhs >> 8), 0, op, lhsOwner, rhsOwner];

    public static (TreeContext Context, XPerson Me, PrimitiveTestHost Host) Create(short[]? parameters = null, short stackObject = 0)
    {
        var host = new PrimitiveTestHost();
        var world = new SimulationWorld { Host = host };
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            if (message == 0xe8 && value == 0) host.Calls.Add("NotifyNeighborInteractionChanged");
            else host.Calls.Add($"SendNotice({message},{value})");
            return false;
        }));
        var me = world.Add(new XPerson(100, Definition(XPerson.PersonType)));
        var frame = new StackFrame(stackObject, parameters ?? [], [0, 0]);
        return (new TreeContext(world, me, frame), me, host);
    }
}
