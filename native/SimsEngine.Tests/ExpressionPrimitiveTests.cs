using SimsEngine.Simulation;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.World;
using Xunit;

namespace SimsEngine.Tests;

public sealed class ExpressionPrimitiveTests
{
    private sealed class Host : ISimulationHost
    {
        public Dictionary<ushort, short[]> Bcons { get; } = [];
        public IReadOnlyList<short>? Constants(StackFrame frame, ushort id) => Bcons.GetValueOrDefault(id);
        public short? Room(short room, short field) => room == 3 ? (short)(10 + field) : null;
        public bool CareerExists(short career) => career is -1 or 4;
        public short? JobData(short career, short level, short field) => career == 4 ? (short)(level * 100 + field) : null;
        public uint NextRandom() => 0;
        public TypeRecord? AlternativeObjectType(TypeRecord entry) => null;
        public string? StringTableEntry(short table, int index) => null;
        public void BroadcastString(int message, string text) { }
        public bool PlaySoundEvent(int bank, short eventId) => false;
        public bool PlayPositionalSound(short ownerId) => false;
        public AutonomyLot AutonomyLotState() => throw new NotImplementedException();
        public void RefreshDisplay() => throw new NotImplementedException();
        public void RefreshRoom(ushort room) => throw new NotImplementedException();
        public bool BurnEnabled() => throw new NotImplementedException();
        public TypeRecord? FindObjectType(int key) => throw new NotImplementedException();
        public bool BurnFootprintClear(int tileX, int tileY, int height, short direction) => throw new NotImplementedException();
        public bool BurnSpread(XObject me, ObjectPosition position, TypeRecord? fire, bool strictFlags) => throw new NotImplementedException();
        public void SetObjectUiEffect(short id, bool flag) => throw new NotImplementedException();
        public void ShowObjectUiEffect(short id, bool flag) => throw new NotImplementedException();
        public void ShowEntryUiEffect(short id, bool flag) => throw new NotImplementedException();
        public bool HasBalloonEntries(XObject owner) => throw new NotImplementedException();
        public bool ClaimBalloon(XObject owner, int priority) => throw new NotImplementedException();
        public void SetBalloonState(XObject owner, int value) => throw new NotImplementedException();
        public void SetBalloonStateTimed(XObject owner, int count) => throw new NotImplementedException();
        public void NotifyPersonObjectReference(XPerson recipient, XObject subject) => throw new NotImplementedException();
        public void ShowBalloonForType(XObject owner, TypeRecord? alternative) => throw new NotImplementedException();
        public int ListEntryValue(short index) => throw new NotImplementedException();
        public void ShowBalloonForValue(XObject owner, int value) => throw new NotImplementedException();
        public bool ShowBalloonText(byte table, int textId, int flags) => throw new NotImplementedException();
        public short BalloonString(byte table, short index) => throw new NotImplementedException();
        public int BalloonDuration(byte kind) => throw new NotImplementedException();
        public void SetBalloonFields(XObject owner, int duration, int label, bool flag) => throw new NotImplementedException();
        public void NotifyObjectTable(XObject owner) => throw new NotImplementedException();
        public bool CanGrab(XObject item, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public void Grab(XObject item, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public bool CanDrop(XObject item, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public void Drop(XObject item, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public bool CanDropOnto(XObject target, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public void DropOnto(XObject target, ObjectPosition at, XObject? who, int flag) => throw new NotImplementedException();
        public int? DialogRecordState(XObject owner) => throw new NotImplementedException();
        public byte DialogGlobalFlagF8() => 0;
        public void DismissDialogRecord(XObject owner) { }
        public void DismissDialogRecords(XObject owner) { }
        public void ShowDialog(XPerson owner, byte style) { }
        public void SetLookTarget(XObject me, short target) => throw new NotImplementedException();
        public void TurnTowards(XObject me, ObjectPosition? point) => throw new NotImplementedException();
        public int TurnStatus(XObject me) => throw new NotImplementedException();
        public bool TryStartTurn(XObject me) => throw new NotImplementedException();
        public bool RoutingEnabled() => throw new NotImplementedException();
        public int RoutingSlotCount(XObject stackObject) => throw new NotImplementedException();
        public RoutingSlotRequest RoutingSlotAt(XObject stackObject, int index) => throw new NotImplementedException();
        public int LotRoutingSlotCount(XObject me) => throw new NotImplementedException();
        public RoutingSlotRequest LotRoutingSlotAt(XObject me, int index) => throw new NotImplementedException();
        public PrimitiveResult Route(XObject me, StackFrame frame, RoutingSlotRequest? target) => throw new NotImplementedException();
        public PrimitiveResult AnimateAvatar(XPerson me, StackFrame frame, ReadOnlySpan<byte> operand) => throw new NotImplementedException();
        public void StopOwnerSounds(short ownerId) => throw new NotImplementedException();
        public void ChangeActionString(XObject me, short stackObject, string? text) => throw new NotImplementedException();
        public PrimitiveResult RunSnapshot(XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, bool snapTargetFlag,
            int directionMask, bool slotFlag, (int Dy, int Dx, int Ddir)? relative) => throw new NotImplementedException();
        public void SetEffectWord(short index, short value) => throw new NotImplementedException();
        public bool PlaySpecialEffect(XObject me, XObject stackObject, SpecialEffectRequest request) => throw new NotImplementedException();
        public bool PushInteraction(XPerson person, XObject owner, byte interactionId, int priority, XObject? secondary, int flags) => throw new NotImplementedException();
        public bool IsSearchable(XObject candidate) => throw new NotImplementedException();
        public XObject? CreateCharacter(int mode) => throw new NotImplementedException();
        public sbyte TileTransformByte(int index) => throw new NotImplementedException();
        public int ActivePersonIndex(int position) => throw new NotImplementedException();
        public short? ActivePersonRecordId(int index) => throw new NotImplementedException();
        public short? StoredSlotPersonId() => throw new NotImplementedException();
        public InteractionBehaviorRuntimeContext? BeginInteractionBehaviorScope(InteractionBehaviorScope scope) => throw new NotImplementedException();
        public InteractionBehaviorActionList? InteractionBehaviorActions(InteractionBehaviorScope scope, int listIndex) => throw new NotImplementedException();
        public void EndInteractionBehaviorScope(InteractionBehaviorScope scope, bool commit) => throw new NotImplementedException();
        public void FinalizeInteractionBehaviorTypeRecord(TypeRecord? typeRecord) => throw new NotImplementedException();
        public void RefreshInteractionBehaviorTypeRecord(TypeRecord? typeRecord) => throw new NotImplementedException();
        public void NotifyInteractionBehaviorGlobal(uint mask) => throw new NotImplementedException();
        public int GenericSimsCall20PersonCount() => throw new NotImplementedException();
        public XPerson? GenericSimsCall20ActivePerson(int position) => throw new NotImplementedException();
        public XPerson? GenericSimsCall20FallbackPerson(int position) => throw new NotImplementedException();
        public void GenericSimsCall20FinalizePerson(XPerson person) => throw new NotImplementedException();
        public void RunGenericSimsCall01InteractionPhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer) => throw new NotImplementedException();
        public void RunGenericSimsCall01RoutePhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer) => throw new NotImplementedException();
        public void GenericSimsCall22Finalize(XObject resolved) => throw new NotImplementedException();
        public short? ScopedNeighborPersonRecordId(int personRecordKey) => throw new NotImplementedException();
        public void PersistNeighborhoodState() => throw new NotImplementedException();
        public XObject? GenericSimsCall11CallbackTarget() => throw new NotImplementedException();
        public bool PassesAutonomyCallbackGate(XObject target) => throw new NotImplementedException();
        public void InvokeGatedObjectCallback(XObject target, GatedObjectCallbackRequest request) => throw new NotImplementedException();
        public XObject? GenericSimsCall06CallbackTarget() => throw new NotImplementedException();
        public void RunGenericSimsCall11Finalize(XObject resolved, short temporary0, short temporary1, short temporary2, short temporary3) => throw new NotImplementedException();
        public bool GenericSimsCallRequestManagerAvailable() => throw new NotImplementedException();
        public GenericSimsCallRequestCompletion ProcessGenericSimsCallRequest(GenericSimsCallPendingRequest request, XObject target) => throw new NotImplementedException();
        public void ReleaseGenericSimsCall03PrimaryHandle(int handle) => throw new NotImplementedException();
        public void ReleaseGenericSimsCall03SecondaryHandle(int handle) => throw new NotImplementedException();
        public void RunGenericSimsCallInitialization(int mode) => throw new NotImplementedException();
        public byte[]? GenericSimsCall26StringBytes(StackFrame frame, short selector, short oneBasedIndex) => throw new NotImplementedException();
        public void SelectedPersonChanged(XObject person) => throw new NotImplementedException();
        public void MaintainLotList(XObject me) => throw new NotImplementedException();
        public void RemoveFromLotList(XObject me) => throw new NotImplementedException();
        public IReadOnlyList<int> GlobalIdTable() => throw new NotImplementedException();
        public IReadOnlyList<int> CareerTable() => throw new NotImplementedException();
        public XObject? CreateObjectInstance(int guidKey) => throw new NotImplementedException();
        public bool TileHasObject(int tileX, int tileY, int level, int? guidKey) => throw new NotImplementedException();
        public short RoomAtTile(int tileX, int tileY, int level) => throw new NotImplementedException();
        public bool FacingClear(int tileX, int tileY, int level, int facing) => throw new NotImplementedException();
        public bool CanPlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method) => throw new NotImplementedException();
        public void PlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method) => throw new NotImplementedException();
        public void DiscardObject(XObject created) => throw new NotImplementedException();
        public void RotateCreatedObject(XObject created) => throw new NotImplementedException();
        public void InitCreatedPerson(XPerson created) => throw new NotImplementedException();
        public void ArriveCreatedPerson(XPerson created) => throw new NotImplementedException();
        public int SoundBankOf(XObject obj) => throw new NotImplementedException();
        public bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found) => throw new NotImplementedException();
        public bool CanPlaceAt(XObject stackObject, ObjectPosition at) => throw new NotImplementedException();
        public void PlaceAt(XObject stackObject, ObjectPosition at) => throw new NotImplementedException();
        public void AlignMultitileParts(XObject stackObject) => throw new NotImplementedException();
        public void RotateForPlacement(XObject stackObject, int steps) => throw new NotImplementedException();
        public void FinishLocationSearch(XObject stackObject) => throw new NotImplementedException();
        public void FinalizePlacement(XObject stackObject) => throw new NotImplementedException();
        public XObject? StartQueuedAction(XPerson me, ActionQueueSlot action) => throw new NotImplementedException();
        public void QueueAction(XPerson me, XObject target, int key, int kind) => throw new NotImplementedException();
        public (XObject? Holder, int Value) GrabHolder(XObject target) => throw new NotImplementedException();
        public float SlotPosition(int slot) => throw new NotImplementedException();
        public bool NextAvatarEvent(XPerson me, out int code) => throw new NotImplementedException();
        public bool ReachAnimationDone(XPerson me) => throw new NotImplementedException();
        public void FinishReach(XPerson me) => throw new NotImplementedException();
        public bool StartReachAnimation(XPerson me, float position) => throw new NotImplementedException();
        public bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag) => throw new NotImplementedException();
        public void Place(XObject target, ObjectPosition at, XObject who, int flag) => throw new NotImplementedException();
    }

    private static short[] Definition(short type = 0)
    {
        var definition = new short[XObject.DefinitionCount];
        definition[9] = type;
        return definition;
    }

    private static (TreeContext Context, XPerson Me, XObject Chair) World(short stackObject = 2)
    {
        var world = new SimulationWorld { Host = new Host(), CheckTreeAds = [7, 20, 30, 8, 40, 50] };
        var me = world.Add(new XPerson(1, Definition(XPerson.PersonType), attributes: 4));
        var chair = world.Add(new XObject(2, Definition(), attributes: 2));
        var frame = new StackFrame(stackObject, [5, 1, -3], [0, 0]);
        return (new TreeContext(world, me, frame), me, chair);
    }

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    private static bool Run(TreeContext context, DataOwner lhsOwner, short lhs, ExpressionOperator op, DataOwner rhsOwner, short rhs) =>
        ExpressionPrimitive.Execute(context, lhsOwner, lhs, op, rhsOwner, rhs);

    [Fact]
    public void ParsesTheOperand()
    {
        var (context, me, _) = World();
        Assert.True(ExpressionPrimitive.Execute(context, [3, 0, 7, 0, 0, 3, 8, 7]));
        Assert.Equal(7, me.Temporaries[3]);
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => ExpressionPrimitive.Execute(context, [3, 0, 7, 0, 0, 2, 0xff, 7])));
    }

    [Fact]
    public void ComparesWithoutWriting()
    {
        var (context, me, _) = World();
        me.Temporaries[0] = 5;
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.GreaterThan, DataOwner.Literal, 4));
        Assert.False(Run(context, DataOwner.Temporary, 0, ExpressionOperator.LessThan, DataOwner.Literal, 5));
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.LessThanOrEqualTo, DataOwner.Literal, 5));
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.NotEqualTo, DataOwner.Literal, 6));
        Assert.True(Run(context, DataOwner.Literal, 9, ExpressionOperator.EqualTo, DataOwner.Literal, 9));
    }

    [Fact]
    public void ArithmeticWrapsToSixteenBits()
    {
        var (context, me, _) = World();
        me.Temporaries[0] = short.MaxValue;
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.AddTo, DataOwner.Literal, 1);
        Assert.Equal(short.MinValue, me.Temporaries[0]);
        me.Temporaries[1] = 300;
        Run(context, DataOwner.Temporary, 1, ExpressionOperator.MultiplyBy, DataOwner.Literal, 300);
        Assert.Equal(unchecked((short)90000), me.Temporaries[1]);
        me.Temporaries[2] = -7;
        Run(context, DataOwner.Temporary, 2, ExpressionOperator.DivideBy, DataOwner.Literal, 2);
        Assert.Equal(-3, me.Temporaries[2]);
        Assert.Equal(SimulationError.DivideByZero, Fails(() => Run(context, DataOwner.Temporary, 2, ExpressionOperator.DivideBy, DataOwner.Literal, 0)));
    }

    [Fact]
    public void ModuloDropsBothSigns()
    {
        var (context, me, _) = World();
        me.Temporaries[0] = -7;
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.ModuloBy, DataOwner.Literal, -3);
        Assert.Equal(1, me.Temporaries[0]);
        Assert.Equal(SimulationError.DivideByZero, Fails(() => Run(context, DataOwner.Temporary, 0, ExpressionOperator.ModuloBy, DataOwner.Literal, 0)));
    }

    [Fact]
    public void FlagsCountFromOne()
    {
        var (context, me, _) = World();
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.SetFlag, DataOwner.Literal, 1);
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.SetFlag, DataOwner.Literal, 16);
        Assert.Equal(unchecked((short)0x8001), me.Temporaries[0]);
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.IsFlagSet, DataOwner.Literal, 16));
        Assert.False(Run(context, DataOwner.Temporary, 0, ExpressionOperator.IsFlagSet, DataOwner.Literal, 2));
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.ClearFlag, DataOwner.Literal, 1);
        Assert.Equal(unchecked((short)0x8000), me.Temporaries[0]);
        // Testing flag 0 checks the sign bit via a 31-bit shift, whereas setting or clearing flag 0 has no
        // effect.
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.IsFlagSet, DataOwner.Literal, 0));
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.ClearFlag, DataOwner.Literal, 0);
        Assert.Equal(unchecked((short)0x8000), me.Temporaries[0]);
        Assert.Equal(SimulationError.FlagReferenceIsOutOfRange, Fails(() => Run(context, DataOwner.Temporary, 0, ExpressionOperator.SetFlag, DataOwner.Literal, 17)));
        Assert.Equal(SimulationError.FlagReferenceIsOutOfRange, Fails(() => Run(context, DataOwner.Temporary, 0, ExpressionOperator.IsFlagSet, DataOwner.Literal, -1)));
    }

    [Fact]
    public void StepsThenCompares()
    {
        var (context, me, _) = World();
        me.Temporaries[0] = 2;
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.IncrementAndLessThan, DataOwner.Literal, 4));
        Assert.False(Run(context, DataOwner.Temporary, 0, ExpressionOperator.IncrementAndLessThan, DataOwner.Literal, 4));
        Assert.Equal(4, me.Temporaries[0]);
        Assert.True(Run(context, DataOwner.Temporary, 0, ExpressionOperator.DecrementAndGreaterThan, DataOwner.Literal, 2));
        Assert.False(Run(context, DataOwner.Temporary, 0, ExpressionOperator.DecrementAndGreaterThan, DataOwner.Literal, 2));
        Assert.Equal(2, me.Temporaries[0]);
    }

    [Fact]
    public void SquareRootTruncates()
    {
        var (context, me, _) = World();
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.AssignSquareRoot, DataOwner.Literal, 10);
        Assert.Equal(3, me.Temporaries[0]);
        Run(context, DataOwner.Temporary, 0, ExpressionOperator.AssignSquareRoot, DataOwner.Literal, -4);
        Assert.Equal(0, me.Temporaries[0]);
    }

    [Fact]
    public void MotivesAreFloatsClampedAfterAWrite()
    {
        var (context, me, _) = World();
        me[Motive.Hunger] = 95.5f;
        Run(context, DataOwner.MyMotive, (short)Motive.Hunger, ExpressionOperator.AddTo, DataOwner.Literal, 10);
        Assert.Equal(100f, me[Motive.Hunger]);
        Run(context, DataOwner.MyMotive, (short)Motive.Hunger, ExpressionOperator.AssignTo, DataOwner.Literal, -250);
        Assert.Equal(-100f, me[Motive.Hunger]);
        me[Motive.Fun] = -3.75f;
        Assert.Equal(-3, Variables.Get(context, DataOwner.MyMotive, (short)Motive.Fun));
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => Run(context, DataOwner.MyMotive, (short)Motive.Fun, ExpressionOperator.OrWith, DataOwner.Literal, 1)));
        Assert.Equal(SimulationError.MotiveNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.MyMotive, 16)));
    }

    [Fact]
    public void ReadOnlyOwnersRejectWrites()
    {
        var (context, _, _) = World();
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => Run(context, DataOwner.Literal, 1, ExpressionOperator.AssignTo, DataOwner.Literal, 2)));
        Assert.Equal(SimulationError.ExpressionAttemptingToWriteInAReadOnlyArea, Fails(() => Run(context, DataOwner.Global, (short)SimulationGlobal.Hour, ExpressionOperator.AssignTo, DataOwner.Literal, 2)));
        Run(context, DataOwner.Global, (short)SimulationGlobal.LotTransitInfo, ExpressionOperator.AssignTo, DataOwner.Literal, 2);
        Assert.Equal(2, context.World[SimulationGlobal.LotTransitInfo]);
        Assert.Equal(SimulationError.GlobalNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.Global, 0x40)));
    }

    [Fact]
    public void UnknownOperatorFailsAfterReadingBothSides()
    {
        var (context, _, _) = World();
        Assert.Equal(SimulationError.BadOperator, Fails(() => Run(context, DataOwner.Temporary, 0, (ExpressionOperator)21, DataOwner.Literal, 0)));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => Run(context, DataOwner.Temporary, 9, (ExpressionOperator)21, DataOwner.Parameter, 3)));
    }

    [Fact]
    public void OwnerRangeErrors()
    {
        var (context, _, _) = World();
        Assert.Equal(SimulationError.TemporaryReferenceOutOfRange, Fails(() => Variables.Get(context, DataOwner.Temporary, 8)));
        Assert.Equal(SimulationError.AttributeNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.MyAttribute, 4)));
        Assert.Equal(SimulationError.DataNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.My, XObject.DataCount)));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.Local, 2)));
        Assert.Equal(SimulationError.PersonDataFieldIsOutOfRange, Fails(() => Variables.Get(context, DataOwner.MyPersonData, 256)));
        Assert.Equal(SimulationError.NeighborhoodDataIndexOutOfRange, Fails(() => Variables.Get(context, DataOwner.NeighborhoodData, 0)));
        Assert.Equal(SimulationError.MotiveNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.CheckTreeAdMin, 2)));
        Assert.Equal(40, Variables.Get(context, DataOwner.CheckTreeAdMin, 1));
        Assert.Equal(SimulationError.IllegalOwnerFieldInDataReference, Fails(() => Variables.Get(context, DataOwner.TargetObject, 0)));
    }

    [Fact]
    public void StackObjectOwners()
    {
        var (context, me, chair) = World();
        chair.Attributes[1] = 42;
        Assert.Equal(42, Variables.Get(context, DataOwner.StackObjectAttribute, 1));
        Assert.Equal(42, Variables.Get(context, DataOwner.StackObjectAttributeByParameter, 1));
        Assert.Equal(SimulationError.ObjectReferredToMustBeAPerson, Fails(() => Variables.Get(context, DataOwner.StackObjectMotive, 0)));
        Variables.Set(context, DataOwner.StackObjectId, 0, 1);
        Assert.Equal(1, context.Frame.StackObject);
        Assert.Equal(SimulationError.ReferenceToStackObjectOutOfRange, Fails(() => Variables.Get(context, DataOwner.StackObjectId, 1)));
        me.Temporaries[0] = 16;
        Assert.Equal(SimulationError.MotiveNumberOutOfRange, Fails(() => Variables.Get(context, DataOwner.StackObjectMotiveByTemporary, 0)));

        var (empty, _, _) = World(stackObject: 99);
        Assert.Equal(SimulationError.ReferenceToTreeParameterWhenNoParameterExists, Fails(() => Variables.Get(empty, DataOwner.StackObject, 0)));
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => Variables.Get(empty, DataOwner.StackObjectPersonData, 0)));
    }

    [Fact]
    public void ReadingObjectLockoutCountSetsThatObjectsClockWaitCellToMinusTwo()
    {
        var (context, me, chair) = World();
        me[ObjectData.LockoutCount] = 3;
        chair[ObjectData.LockoutCount] = 4;
        context.World.ClockManager.SetWaitTicksAt(me.ClockManagerIndex, 6);
        context.World.ClockManager.SetWaitTicksAt(chair.ClockManagerIndex, 8);

        Assert.Equal((short)3, Variables.Get(context, DataOwner.My, (short)ObjectData.LockoutCount));
        Assert.Equal((short)-2, context.World.ClockManager.WaitTicksAt(me.ClockManagerIndex));

        Assert.Equal((short)4, Variables.Get(context, DataOwner.StackObject, (short)ObjectData.LockoutCount));
        Assert.Equal((short)-2, context.World.ClockManager.WaitTicksAt(chair.ClockManagerIndex));
    }

    [Fact]
    public void ExpressionLockoutReadCopiesNonnegativeWaitCellToCurrentFrameParameterZero()
    {
        var (context, me, _) = World();
        me[ObjectData.LockoutCount] = 4;
        context.World.ClockManager.SetWaitTicksAt(me.ClockManagerIndex, 8);

        Assert.True(Run(context, DataOwner.My, (short)ObjectData.LockoutCount,
            ExpressionOperator.EqualTo, DataOwner.Literal, 4));

        Assert.Equal((short)8, context.Frame.Parameters[0]);
        Assert.Equal((short)-2, context.World.ClockManager.WaitTicksAt(me.ClockManagerIndex));
    }

    [Fact]
    public void NonExpressionLockoutReadDoesNotCopyWaitCellToFrameParameter()
    {
        var (context, me, _) = World();
        me[ObjectData.LockoutCount] = 4;
        context.World.ClockManager.SetWaitTicksAt(me.ClockManagerIndex, 8);

        Assert.Equal((short)4, Variables.Get(context, DataOwner.My, (short)ObjectData.LockoutCount));

        Assert.Equal((short)5, context.Frame.Parameters[0]);
        Assert.Equal((short)-2, context.World.ClockManager.WaitTicksAt(me.ClockManagerIndex));
    }

    [Fact]
    public void RoomGroupAndMissingRecordComeFromTheNativeRoomRecords()
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        var wallBody = new ChunkBuilder().S16(0).S16(0).S32(3).S32(3).S32(8).U32(0);
        for (var tile = 0; tile < 9; tile++) wallBody.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var levelOne = RoomMap.Build(new IffFile(ChunkBuilder.File(("Arry", 2, wallBody.ToArray())), "room-level-1.iff"), 1, 3, 3);

        var upperWalls = new ChunkBuilder().S16(0).S16(0).S32(4).S32(1).S32(8).U32(0);
        for (var tile = 0; tile < 4; tile++) upperWalls.Raw([0, 0x0F, 0, 0, 0, 0, 0, 0]);
        var upperFloors = new ChunkBuilder().S16(0).S16(0).S32(4).S32(1).S32(1).U32(0)
            .U8(0).U8(255).U8(13).U8(13).ToArray();
        var levelTwo = RoomMap.Build(new IffFile(ChunkBuilder.File(
            ("Arry", 102, upperWalls.ToArray()), ("Arry", 101, upperFloors)), "room-level-2.iff"), 2, 4, 1);
        context.World.LoadRoomMaps(new Dictionary<int, RoomMap> { [1] = levelOne, [2] = levelTwo });

        me.Temporaries[0] = unchecked((short)levelOne.RoomAt(1, 1));
        Assert.Equal(1, Variables.Get(context, DataOwner.RoomByTemporary0, 2));
        me.Temporaries[0] = 0x400;
        Assert.Equal(2, Variables.Get(context, DataOwner.RoomByTemporary0, 2));

        host.RoomFields[(unchecked((short)levelOne.RoomAt(1, 1)), 0)] = 77;
        host.RoomFields[(0x400, 2)] = 99;
        me.Temporaries[0] = unchecked((short)levelOne.RoomAt(1, 1));
        Assert.Equal(77, Variables.Get(context, DataOwner.RoomByTemporary0, 0));
        me.Temporaries[0] = 0x400;
        Assert.Equal(2, Variables.Get(context, DataOwner.RoomByTemporary0, 2));

        me.Temporaries[0] = 7;
        Assert.Equal(SimulationError.BadRoomIdSpecified, Fails(() => Variables.Get(context, DataOwner.RoomByTemporary0, 2)));
        Assert.Equal(SimulationError.BadRoomIdSpecified, Fails(() => Variables.Get(context, DataOwner.RoomByTemporary0, 0)));
    }

    [Fact]
    public void NeighborInStackObjectFieldsTwoAndFiveAreNative()
    {
        var (context, _, _) = PrimitiveTestWorld.Create();
        var definition = PrimitiveTestWorld.Definition();
        var subject = new NeighborState(1, definition);
        subject.PersonData[58] = 12;
        var friend = new NeighborState(2, definition) { FriendEligibilityObjectFlagsB4 = 4u };
        friend.PersonData[61] = 5;
        subject.Relationships.Resize(2, 1);
        friend.Relationships.Resize(1, 1);
        subject.Relationships.Set(2, 0, 50);
        friend.Relationships.Set(1, 0, 50);
        context.World.NeighborWalkOrder.Add(subject);
        context.World.NeighborWalkOrder.Add(friend);
        context.Frame.StackObject = 1;

        Assert.Equal(12, Variables.Get(context, DataOwner.NeighborInStackObject, 2));
        subject.RelationshipsChangedFlag24 = true;
        Assert.Equal(1, Variables.Get(context, DataOwner.NeighborInStackObject, 5));
        subject.RelationshipsChangedFlag24 = false;
        friend.PersonData[61] = 0;
        Assert.Equal(1, Variables.Get(context, DataOwner.NeighborInStackObject, 5));
        subject.RelationshipsChangedFlag24 = true;
        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 5));
    }

    [Fact]
    public void NeighborInStackObjectFieldZeroUsesTheNeighborsTypeRecordNatively()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var neighborDefinition = PrimitiveTestWorld.Definition();
        context.World.NeighborWalkOrder.Add(new NeighborState(1, neighborDefinition) { TypeRecordKey = 0x0BADF00D });
        var body = context.World.Add(new XPerson(7, neighborDefinition) { TypeRecord = new TypeRecord(0x0BADF00D) });
        context.Frame.StackObject = 1;

        Assert.Equal(body.ClockManagerIndex, Variables.Get(context, DataOwner.NeighborInStackObject, 0));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));

        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 3));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));

        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 1));
        context.World.NeighborInteractionManagerTypeKeys.Add(0x0BADF00D);
        Assert.Equal(1, Variables.Get(context, DataOwner.NeighborInStackObject, 1));

        context.Frame.StackObject = 9;
        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 0));
        context.World.NeighborWalkOrder[0]!.TypeRecordKey = 0x12345678;
        context.Frame.StackObject = 1;
        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 0));
    }

    [Fact]
    public void NeighborInStackObjectFieldSixReturnsFirstMatchingInteractionObjectHeight()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var neighbor = context.World.Add(new XObject(30, PrimitiveTestWorld.Definition()));
        var first = new XObject(31, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(10, 10, 2) };
        var second = new XObject(32, PrimitiveTestWorld.Definition()) { Position = new ObjectPosition(20, 20, 3) };
        first.AddNeighborSubobjectReference(neighbor);
        second.AddNeighborSubobjectReference(neighbor);
        context.World.SetNeighborTableEntry(1, neighbor);
        context.World.InteractionLookupOrder.Add(first);
        context.World.InteractionLookupOrder.Add(second);
        context.Frame.StackObject = 1;

        Assert.Equal(2, Variables.Get(context, DataOwner.NeighborInStackObject, 6));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));

        context.Frame.StackObject = 2;
        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 6));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));
    }

    [Fact]
    public void NeighborInStackObjectFieldNineCalculatesAndCachesMutualFriendCount()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var definition = PrimitiveTestWorld.Definition();
        var stackNeighbor = new XObject(30, definition)
        {
            TypeRecord = new TypeRecord(300),
            NeighborInteractionTargetWordEE = 90,
        };
        var candidate = new XObject(31, definition)
        {
            TypeRecord = new TypeRecord(301),
            NeighborInteractionTargetWordEE = 91,
            NeighborInteractionWordF6 = 1,
        };
        var activeNeighbor = new XPerson(32, definition) { TypeRecord = new TypeRecord(302) };
        var interaction = new XObject(33, definition)
        {
            TableWord10C = 90,
            NeighborInteractionStateByte124 = 1,
        };
        candidate.Relationships.Resize(activeNeighbor.Id, 1);
        candidate.Relationships.Set(activeNeighbor.Id, 0, 25);
        activeNeighbor.Relationships.Resize(candidate.Id, 1);
        activeNeighbor.Relationships.Set(candidate.Id, 0, 25);
        context.World.SetNeighborTableEntry(1, stackNeighbor);
        context.World.SetNeighborTableEntry(2, candidate);
        context.World.SetNeighborTableEntry(3, activeNeighbor);
        context.World.InteractionLookupOrder.Add(interaction);
        context.World.PersonRecordKeys.Add(activeNeighbor.TypeRecord.Key);
        context.World.ActivePersonRecordKeys.Add(activeNeighbor.TypeRecord.Key);
        context.World.ActivePersonManagerPresent = true;
        context.Frame.StackObject = 1;

        Assert.Equal(1, Variables.Get(context, DataOwner.NeighborInStackObject, 9));
        Assert.Equal(1, interaction.NeighborInteractionCountWord120);
        Assert.Equal(0, interaction.NeighborInteractionStateByte124);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));

        candidate.Relationships.Set(activeNeighbor.Id, 0, 24);
        Assert.Equal(1, Variables.Get(context, DataOwner.NeighborInStackObject, 9));
        interaction.NeighborInteractionStateByte124 = 1;
        Assert.Equal(0, Variables.Get(context, DataOwner.NeighborInStackObject, 9));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("NeighborInStackObject", StringComparison.Ordinal));
    }

    [Fact]
    public void RoomAndJobDataUseTheHostWhileFunctionIdsComeFromTheNativeTypeTable()
    {
        var (context, me, chair) = World();
        chair.TypeRecord = new TypeRecord(1) { FunctionTable = [0, 0, 1] };
        me.Temporaries[0] = -5;
        Assert.Equal(1, Variables.Get(context, DataOwner.RoomByTemporary0, 1));
        Assert.Equal(0, Variables.Get(context, DataOwner.RoomByTemporary0, 2));
        Assert.Equal(SimulationError.BadRoomValueRequested, Fails(() => Variables.Get(context, DataOwner.RoomByTemporary0, 4)));
        me.Temporaries[0] = 3;
        Assert.Equal(12, Variables.Get(context, DataOwner.RoomByTemporary0, 2));
        me.Temporaries[0] = 2;
        Assert.Equal(SimulationError.BadRoomIdSpecified, Fails(() => Variables.Get(context, DataOwner.RoomByTemporary0, 0)));

        me.Temporaries[0] = 4;
        me.Temporaries[1] = 3;
        Assert.Equal(305, Variables.Get(context, DataOwner.JobDataByTemporary0And1, 5));
        me.Temporaries[0] = 5;
        Assert.Equal(SimulationError.CareerIdNotFound, Fails(() => Variables.Get(context, DataOwner.JobDataByTemporary0And1, 5)));
        me.Temporaries[0] = -1;
        Assert.Equal(SimulationError.JobDataNotFound, Fails(() => Variables.Get(context, DataOwner.JobDataByTemporary0And1, 5)));

        Assert.Equal(1, Variables.Get(context, DataOwner.StackObjectFunction, 2));
        Assert.Equal(0, Variables.Get(context, DataOwner.StackObjectFunction, 3));
    }

    [Fact]
    public void StackObjectFunctionReturnsTheNativeActionFunctionId()
    {
        var (context, _, stackObject) = World();
        stackObject.TypeRecord = new TypeRecord(0x1234) { FunctionTable = [0, 0x2345, 0x4567] };

        Assert.Equal((short)0x4567, Variables.Get(context, DataOwner.StackObjectFunction, 2));
        Assert.Equal((short)0, Variables.Get(context, DataOwner.StackObjectFunction, 3));
    }

    [Fact]
    public void NeighborVariablesResolveTheOneBasedManagerSlotNotTheSavedNeighborId()
    {
        var (context, _, _) = World();
        var vectorEntry = new NeighborState(19, Definition()) { PersonRecordVersion = 9 };
        vectorEntry.PersonData[61] = 73;
        vectorEntry.Definition[4] = 91;
        var wrongIdMatch = new NeighborState(1, Definition());
        wrongIdMatch.PersonData[61] = 12;
        wrongIdMatch.Definition[4] = 34;
        context.World.NeighborWalkOrder.AddRange([wrongIdMatch, null, vectorEntry]);
        context.World.Neighbors[19] = vectorEntry;
        context.World.Neighbors[3] = wrongIdMatch;
        context.Frame.StackObject = 3;

        Assert.Equal((short)73, Variables.Get(context, DataOwner.NeighborPersonData, 61));
        Assert.Equal((short)91, Variables.Get(context, DataOwner.NeighborObjectDefinition, 4));
    }

    [Fact]
    public void NeighborVariablesReportMissingManagerSlot()
    {
        var (context, _, _) = World(stackObject: 1);
        Assert.Equal(SimulationError.NeighborNotFound, Fails(() => Variables.Get(context, DataOwner.NeighborPersonData, 0)));
        Assert.Equal(SimulationError.NeighborNotFound, Fails(() => Variables.Get(context, DataOwner.NeighborObjectDefinition, 0)));
    }

    [Fact]
    public void ConstantsDecodeScopeBconAndIndex()
    {
        Assert.Equal(((ushort)0x1000, 5), Variables.DecodeConstant(5));
        Assert.Equal(((ushort)0x2003, 1), Variables.DecodeConstant((short)(0x2000 | 3 << 7 | 1)));
        Assert.Equal(((ushort)0x100, 0), Variables.DecodeConstant(0x4000));
        Assert.Null(Variables.DecodeConstant(0x6000));

        var (context, _, _) = World();
        ((Host)context.World.Host!).Bcons[0x1001] = [11, 22];
        Assert.Equal(22, Variables.Get(context, DataOwner.Constant, 1 << 7 | 1));
        Assert.Equal(SimulationError.InvalidConstant, Fails(() => Variables.Get(context, DataOwner.Constant, 1 << 7 | 2)));
        Assert.Equal(SimulationError.InvalidConstant, Fails(() => Variables.Get(context, DataOwner.Constant, 0x6000)));
    }

    [Fact]
    public void SavedBehaviorScopeSuppliesPrivateConstantsWithoutAHost()
    {
        var (context, _, _) = World();
        context.World.Host = null;
        var privateBytes = ChunkBuilder.File(("BCON", 0x1001, new ChunkBuilder().U8(2).U8(0).S16(13).S16(29).ToArray()));
        var globalBytes = ChunkBuilder.File();
        var scope = new BehaviorScope(new IffFile(globalBytes, "Global.iff"), new IffFile(privateBytes, "VisitGen.iff"), null);
        var frame = new StackFrame(0, [], []) { Table = scope };
        context = context with { Frame = frame };

        Assert.Equal(29, Variables.Get(context, DataOwner.Constant, (short)(1 << 7 | 1)));
    }
}
