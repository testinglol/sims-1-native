using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public class TreeRunnerTests
{
    private sealed class TreeTable(Bhav root, Bhav child) : ITreeTable
    {
        public Bhav? Find(short id) => id switch { 256 => root, 257 => child, _ => null };
    }

    private sealed class Host : ISimulationHost
    {
        public IReadOnlyList<short>? Constants(StackFrame frame, ushort id) => null;
        public short? Room(short room, short field) => null;
        public bool CareerExists(short career) => false;
        public short? JobData(short career, short level, short field) => null;
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

    private static TreeContext World(out XPerson me)
    {
        var world = new SimulationWorld { Host = new Host() };
        me = world.Add(new XPerson(1, new short[XObject.DefinitionCount]));
        return new TreeContext(world, me, new StackFrame(0, [], []));
    }

    private static byte[] Expr(short lhs, short rhs, byte op) =>
        [(byte)lhs, (byte)(lhs >> 8), (byte)rhs, (byte)(rhs >> 8), 0, op, 8, 7];

    private static Bhav Tree(byte type, params (ushort Opcode, byte True, byte False, byte[] Operand)[] code)
    {
        var chunk = new ChunkBuilder().U16(0x8002).S16((short)code.Length).U8(type).U8(0).U8(0).U8(0).U32(0);
        foreach (var (opcode, t, f, operand) in code) chunk.U16(opcode).U8(t).U8(f).Raw(operand);
        var (file, iff) = chunk.InFile("BHAV");
        return Bhav.Read(file, iff);
    }

    private static SimulationError Fails(Action run) => Assert.Throws<SimulationException>(run).Error;

    [Fact]
    public void AssignThenBranchReturnsTrue()
    {
        var context = World(out var me);
        var tree = Tree(0,
            (2, 1, 0xFF, Expr(3, 7, 5)),
            (2, 0xFE, 0xFF, Expr(3, 7, 2)));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal(7, me.Temporaries[3]);
    }

    [Fact]
    public void RemovingMeInsideARunToReturnDoesNotEndItAndTheFlagIsRestored()
    {
        var context = World(out _);
        var tree = Tree(0, (0x12, 0xFE, 0xFF, [0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.True(context.World.SelfRemovalEndsRun);
        Assert.Equal(new short[] { 1 }, context.World.PendingRemovals);
    }

    [Fact]
    public void FalseBranchReturnsFalse()
    {
        var context = World(out _);
        var tree = Tree(0, (2, 0xFE, 0xFF, Expr(0, 6, 2)));
        Assert.False(TreeRunner.Run(context, tree));
    }

    [Fact]
    public void CapturedTraceIncludesTreeCallSiteAndArguments()
    {
        var context = World(out var me);
        context.World.CaptureExecutionTrace = true;
        var child = Tree(0, (2, 0xFE, 0xFF, Expr(0, 0, 2)));
        var root = Tree(0, (257, 0xFE, 0xFF, new byte[8]));

        Assert.True(TreeRunner.RunWithParameters(context.World, me, new TreeTable(root, child), 256, me.Id, []));

        var call = Assert.Single(context.World.TreeCallTrace);
        Assert.Equal((short)1, call.ObjectId);
        Assert.Equal((short)256, call.CallerTree);
        Assert.Equal(0, call.Instruction);
        Assert.Equal((short)257, call.CalleeTree);
        Assert.Equal(new short[] { 0, 0, 0, 0 }, call.Parameters);
    }

    [Fact]
    public void BreakpointBitCallsTheBreakpointHandler()
    {
        var context = World(out var me);
        var tree = Tree(0, (0x8002, 0xFE, 0xFF, Expr(3, 7, 5)));
        Assert.Equal(SimulationError.TreeBreakEncountered, Fails(() => { TreeRunner.Run(context, tree); }));
        Assert.Equal(0, me.Temporaries[3]);
    }

    [Fact]
    public void ErrorExitIsABadTarget()
    {
        var context = World(out _);
        var tree = Tree(0, (2, 0xFD, 0xFF, Expr(3, 7, 5)));
        Assert.Equal(TreeRunError.BadTarget, Assert.Throws<TreeRunException>(() => TreeRunner.Run(context, tree)).Error);
    }

    [Fact]
    public void TargetPastTheEndIsAMissingInstruction()
    {
        var context = World(out _);
        var tree = Tree(0, (2, 9, 0xFF, Expr(3, 7, 5)));
        Assert.Equal(TreeRunError.TreeOrInstructionMissing, Assert.Throws<TreeRunException>(() => TreeRunner.Run(context, tree)).Error);
    }

    [Fact]
    public void UnknownOpcodeIsAnIllegalPrimitive()
    {
        var context = World(out _);
        var tree = Tree(0, (0x34, 0xFE, 0xFF, new byte[8]));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => { TreeRunner.Run(context, tree); }));
    }

    [Fact]
    public void LastUnportedOpcodeNowDispatches()
    {
        var context = World(out _);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => { TreeRunner.Run(context, Tree(0, (0x10, 0xFE, 0xFF, new byte[8]))); }));
    }

    [Fact]
    public void PersonOnlyOpcodesAreIllegalForOtherObjects()
    {
        var world = new SimulationWorld { Host = new Host() };
        var item = world.Add(new XObject(2, new short[XObject.DefinitionCount]));
        var context = new TreeContext(world, item, new StackFrame(0, [], []));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => { TreeRunner.Run(context, Tree(0, (3, 0xFE, 0xFF, new byte[8]))); }));
        Assert.Equal(SimulationError.IllegalPrimitiveCode, Fails(() => { TreeRunner.Run(context, Tree(0, (0x11, 0xFE, 0xFF, new byte[8]))); }));
    }

    [Fact]
    public void Type4StartsAtTheCallerInstruction()
    {
        var context = World(out var me);
        var tree = Tree(4,
            (2, 0xFE, 0xFF, Expr(0, 99, 5)),
            (2, 0xFE, 0xFF, Expr(0, 7, 5)));
        Assert.True(TreeRunner.Run(context, tree, 1));
        Assert.Equal(7, me.Temporaries[0]);
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal(99, me.Temporaries[0]);
    }

    private static TreeContext PrimitiveWorld(out XPerson me, out PrimitiveTestHost host)
    {
        var (context, person, testHost) = PrimitiveTestWorld.Create();
        me = person;
        host = testHost;
        return context;
    }

    [Fact]
    public void RandomNumberIsDispatchedAndBranches()
    {
        var context = PrimitiveWorld(out var me, out var host);
        var tree = Tree(0, (8, 0xFE, 0xFF, new byte[] { 1, 0, 8, 0, 4, 0, 7, 0 }));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal(2, me.Temporaries[1]);
    }

    [Fact]
    public void TestObjectTypeIsDispatchedAndBranches()
    {
        var context = PrimitiveWorld(out var me, out _);
        me.Temporaries[0] = 2;
        context.World.Add(new XObject(2, PrimitiveTestWorld.Definition())).TypeRecord = new TypeRecord(0x1234);
        var match = Tree(0, (0x20, 0xFE, 0xFF, new byte[] { 0x34, 0x12, 0, 0, 0, 0, 8, 0 }));
        Assert.True(TreeRunner.Run(context, match));
        var other = Tree(0, (0x20, 0xFE, 0xFF, new byte[] { 0x35, 0x12, 0, 0, 0, 0, 8, 0 }));
        Assert.False(TreeRunner.Run(context, other));
    }

    [Fact]
    public void SetToNextWalksTheObjectsThroughTheTree()
    {
        var context = PrimitiveWorld(out var me, out _);
        context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        context.World.Add(new XObject(6, PrimitiveTestWorld.Definition()));
        var tree = Tree(0, (0x1f, 0xFE, 0xFF, new byte[] { 0, 0, 0, 0, 0x81, 8, 0, 0 }));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Equal(me.Id, me.Temporaries[0]);
    }

    [Fact]
    public void SleepWithoutAParameterIsStackNumberError()
    {
        var context = PrimitiveWorld(out _, out _);
        var tree = Tree(0, (0, 0xFE, 0xFF, new byte[8]));
        Assert.Equal(SimulationError.StackNumberOutOfRange, Fails(() => { TreeRunner.Run(context, tree); }));
    }

    [Fact]
    public void SetToNextModeFourUsesNativeTypeOrderWithoutAHost()
    {
        var context = World(out var me);
        context.World.Host = null;
        const int typeGuid = 0x12345678;
        me.TypeRecord = new TypeRecord(typeGuid);
        context.World.Add(new XObject(2, new short[XObject.DefinitionCount])
        {
            TypeRecord = new TypeRecord(typeGuid),
        });
        // Find Object mode 4 matches objects by the 32-bit GUID in operand bytes 0–3 and stores the first
        // matching object ID.
        var operand = new byte[] { 0x78, 0x56, 0x34, 0x12, 0x84, 8, 0, 0 };
        var tree = Tree(0, (0x1f, 0xFE, 0xFF, operand));

        Assert.True(TreeRunner.Run(context, tree));

        Assert.Equal(me.Id, me.Temporaries[0]);
    }
}
