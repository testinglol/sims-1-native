using SimsEngine.Formats;
using SimsEngine.Content;
using SimsEngine.Character;
using System.Numerics;

namespace SimsEngine.Simulation;

public readonly record struct ObjectPosition(int X, int Y, int Height);
public readonly record struct EditorPlacementTile(int X, int Y, int Level);
public sealed record EditorPlacementResult(bool Valid, string Status, IReadOnlyList<EditorPlacementTile> Tiles, XObject? ReferenceObject = null, int ReferenceSlot = -1);

/// <summary>Represents a temporary multi-tile placement group linked to source lot objects. Cursor flags
/// and association links belong to the temporary copies to prevent cursor state from affecting subsequent
/// simulation placements.</summary>
public sealed class MultiTilePlacementCursorGroup
{
    internal sealed record CursorMember(XObject Source, short MasterId, XObject? AssociatedObject, bool Error15Gate);

    private readonly Dictionary<short, CursorMember> _members;
    internal short LeadSourceId { get; }

    private MultiTilePlacementCursorGroup(short leadSourceId, IEnumerable<CursorMember> members)
    {
        LeadSourceId = leadSourceId;
        _members = members.ToDictionary(member => member.Source.Id);
    }

    internal bool HasError15Gate(XObject member) => _members.TryGetValue(member.Id, out var cursor) && cursor.Error15Gate;
    internal XObject? AssociatedObjectFor(XObject member) =>
        _members.TryGetValue(member.Id, out var cursor) ? cursor.AssociatedObject : null;

    internal static MultiTilePlacementCursorGroup Create(XObject requested, IReadOnlyList<XObject> sourceMembers)
    {
        var lead = requested.MultiTileMasterId == 0
            ? requested
            : sourceMembers.FirstOrDefault(member => member.Id == requested.MultiTileMasterId) ?? requested;
        return new MultiTilePlacementCursorGroup(lead.Id, sourceMembers.Select(member => new CursorMember(
            member,
            member.MultiTileMasterId,
            member.MultiTileMoveAssociatedObject,
            member.RuntimeClass == Formats.ObjectClass.MultiTileObject)));
    }
}

public readonly record struct PortalWallRefreshCoordinate(byte First, byte Second, byte Level);

/// <summary>Tracks the state of a dialog primitive message: state 0 waits for user interaction, states 2
/// through 4 represent completion responses, and state 5 indicates dismissal.</summary>
public sealed class SimulationDialogRecord(string token, XObject owner, XObject? icon, byte kind, string title, string message,
    string? yes, string? no, string? cancel, bool blocking = true, bool awaitsResponse = true)
{
    public string Token { get; } = token;
    public XObject Owner { get; } = owner;
    public XObject? Icon { get; } = icon;
    public byte Kind { get; } = kind;
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string? Yes { get; } = yes;
    public string? No { get; } = no;
    public string? Cancel { get; } = cancel;
    public string? ResponseText { get; internal set; }
    public bool Blocking { get; } = blocking;
    public bool AwaitsResponse { get; } = awaitsResponse;
    public int State { get; set; }
    public bool Visible { get; set; } = true;
    /// <summary>Spellbook detail availability state keyed by the resource ID stored on each master spell
    /// row. Null for non-book dialog types.</summary>
    public IReadOnlyList<SimulationBookAvailability>? BookAvailability { get; internal set; }
    /// <summary>Book dialog display mode: 0 for child spellbooks, 1 for adult spellbooks, and 2 for
    /// cookbooks.</summary>
    public byte? BookMode { get; set; }
    public bool? HideBookItem9 { get; internal set; }
    public bool? HideBookItem4 { get; internal set; }
    public Formats.IffFile? ImageFile { get; set; }
    public ushort ImageBitmapId { get; set; }
}

public readonly record struct SimulationBookAvailability(short ContextItemId, short ItemId, bool Made);

public sealed class AssociatedPlacementHelper
{
    public XObject? Owner { get; internal set; }
    public float Value80 { get; private set; }
    public float Value84 { get; private set; }
    public float Value88 { get; private set; }
    public bool FlagD9 { get; private set; }

    internal void Initialize(XObject owner) => Owner = owner;

    internal void RefreshAfterMove()
    {
        Value80 = 0;
        Value84 = 0;
        Value88 = 0;
        FlagD9 = true;
    }
}

public sealed class GenericSimsCallRecordTarget
{
    public IReadOnlyList<int> FameStarThresholds { get; init; } = [];
    /// <summary>Skill threshold requirements ordered by skill type: cooking, mechanical, charisma, body,
    /// logic, and creativity.</summary>
    public IReadOnlyList<IReadOnlyList<int>> FameSkillThresholds { get; init; } = [];
    public IReadOnlyList<int> FameFriendThresholds { get; init; } = [];

    public static GenericSimsCallRecordTarget FromFameTuning(FameTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        return new GenericSimsCallRecordTarget
        {
            FameStarThresholds = tuning.FameStarThresholds,
            FameSkillThresholds = tuning.FameSkillThresholds,
            FameFriendThresholds = tuning.FameFriendThresholds,
        };
    }
}

public sealed record FamePromotionCandidate(short[] PersonData, short CurrentStarPower, int FriendPowerThreshold);

public sealed record ObjectCreationDiagnostic(short SourceObjectId, short CreatedObjectId, int GuidKey, byte Mode, byte Flags, string? Location,
    ObjectPosition? RequestedPosition = null, short RelativeObjectId = 0, bool? PlacementAccepted = null);
public sealed record ObjectRemovalDiagnostic(short SourceObjectId, short SelectedObjectId, short EffectiveTargetId, string? Location);

public readonly record struct FootprintBounds(int MaxX, int MaxY, int MinX, int MinY);

public readonly record struct LotCoordinateBounds(int MinX, int MaxX, int MinY, int MaxY);

public sealed class ObjectFootprintSlot
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Height { get; init; }
    public short OccupantObjectId { get; set; }
    public int HeightCode { get; init; }
    public int Standing { get; init; }
    public int Sitting { get; init; }

    public static ObjectFootprintSlot FromDefinition(Formats.Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Type != 0) throw new ArgumentException("Only type-0 SLOT entries build cXObject footprint slots.", nameof(slot));
        var height = slot.Z;
        var heightCode = slot.Height != 0 ? slot.Height : slot.Ground;
        switch (heightCode)
        {
            case 0:
                heightCode = height == 0f ? 1 : 5;
                break;
            case 1:
                height = 0f;
                break;
            case 2:
                height = 2.5f;
                break;
            case 3:
            case 8:
                height = 4f;
                break;
            case 4:
                height = 4.05f;
                break;
        }

        return new ObjectFootprintSlot
        {
            X = slot.X,
            Y = slot.Y,
            Height = height,
            HeightCode = heightCode,
            Standing = slot.MaxSize != 0 ? slot.MaxSize : slot.Standing,
            Sitting = slot.Reserved14 != 0 ? slot.Reserved14 : slot.Sitting,
        };
    }
}

public class XObject(short id, short[] definition, int attributes = 0)
{
    public const int DataCount = 0x48;
    public const int DefinitionCount = 0x6c;

    public short Id { get; } = id;
    public Formats.ObjectClass? RuntimeClass { get; set; }
    public short ClockManagerIndex { get; internal set; }
    public short[] Definition { get; } = definition.Length == DefinitionCount ? definition : throw new ArgumentException($"A definition has {DefinitionCount} fields.", nameof(definition));
    public short[] Attributes { get; set; } = new short[attributes];
    public short[] Temporaries { get; } = new short[8];
    public short[] Data { get; } = CreateData();
    /// <summary>The instance-specific name assigned via a text-entry dialog primitive. When null, the
    /// object falls back to its catalog name.</summary>
    public string? Name { get; set; }
    public List<short> MyList { get; } = [];
    public bool PositionRefreshPending { get; internal set; }
    public const float UnallocatedPortalRouteCost = 0f;

    /// <summary>Sentinel value representing an uninitialized portal route cost during route allocation and
    /// expansion.</summary>
    public const float UninitializedPortalRouteCost = 10001f;
    public Dictionary<short, float> PortalRouteCosts { get; } = [];

    public float PortalRouteCostAt(short routeId) =>
        routeId > 0 && PortalRouteCosts.TryGetValue(routeId, out var cost) ? cost : UnallocatedPortalRouteCost;

    public void SetPortalRouteCost(short routeId, float cost)
    {
        if (routeId <= 0) throw new ArgumentOutOfRangeException(nameof(routeId));
        for (short index = 1; index <= routeId; index++)
            if (!PortalRouteCosts.ContainsKey(index)) PortalRouteCosts[index] = UninitializedPortalRouteCost;
        PortalRouteCosts[routeId] = cost;
    }

    internal void ResetAllocatedPortalRouteCosts()
    {
        foreach (var routeId in PortalRouteCosts.Keys.ToArray())
            PortalRouteCosts[routeId] = UninitializedPortalRouteCost;
    }

    private static short[] CreateData()
    {
        var data = new short[DataCount];
        data[(int)ObjectData.Room] = -5;
        data[(int)ObjectData.RotationNotches] = 2;
        return data;
    }

    /// <summary>The sleep gate flag, aliased to object data slot 25 (Lockout Count).</summary>
    public short SleepGateWord7C
    {
        get => Data[(int)ObjectData.LockoutCount];
        set => Data[(int)ObjectData.LockoutCount] = value;
    }
    public short StateValue { get; set; }
    public virtual short SelectionWord5CC { get; set; }
    public virtual short SelectionWord606 { get; set; }
    public int TableWord10C { get; set; }
    public bool LotUpkeepGateByte14C { get; set; }
    public bool LotUpkeepPointer128Present { get; set; }
    /// <summary>Six minimum resource thresholds copied as lower bounds into the matching master spell
    /// object's attribute array for lot upkeep.</summary>
    public short[] LotUpkeepMinimumResources { get; } = new short[6];
    public int GenericSimsCall1BRawWord13C { get; set; }
    public XPerson? StoredPersonPointer10C { get; set; }
    /// <summary>The room identifier for this object, stored in object data field 29. Defaults to -5 and is
    /// recalculated whenever the object changes position.</summary>
    public short RoomWord84 { get => Data[(int)ObjectData.Room]; set => Data[(int)ObjectData.Room] = value; }
    public short RoomChangeNoticeGateWord58 { get; set; }
    public short RemovalCallbackWord14 { get; set; }
    public int ObjectValueWord118 { get; set; }
    public int ObjectValueWord11C { get; set; }
    public int NeighborTableWord08 { get; set; }
    public short NeighborInteractionTargetWordEE { get; set; }
    public short NeighborInteractionWordF6 { get; set; }
    public int NeighborInteractionCountWord120 { get; set; }
    public short NeighborInteractionWordFA { get; set; }
    public short NeighborInteractionWord612 { get; set; }
    public uint NeighborInteractionFlagsWord138 { get; set; }
    public byte NeighborInteractionStateByte124 { get; set; }
    private readonly List<XObject> _neighborSubobjectReferences = [];
    public IReadOnlyList<XObject> NeighborSubobjectReferences => _neighborSubobjectReferences;

    public bool HasNeighborSubobjectReference(XObject neighbor) =>
        _neighborSubobjectReferences.Any(candidate => ReferenceEquals(candidate, neighbor));

    public bool AddNeighborSubobjectReference(XObject neighbor)
    {
        if (HasNeighborSubobjectReference(neighbor)) return false;
        _neighborSubobjectReferences.Add(neighbor);
        return true;
    }

    public bool RemoveNeighborSubobjectReference(XObject neighbor)
    {
        var index = _neighborSubobjectReferences.FindIndex(candidate => ReferenceEquals(candidate, neighbor));
        if (index < 0) return false;
        _neighborSubobjectReferences.RemoveAt(index);
        return true;
    }
    /// <summary>The one-based table of object references in the lot object manager, where index 0 is
    /// unused.</summary>
    private List<XObject?> _objectTableEntries = [];
    public List<XObject?> ObjectTableEntries => _objectTableEntries;
    public Dictionary<short, short> ObjectTableEffectWords { get; } = [];
    public FootprintBounds FootprintBounds { get; set; }
    /// <summary>The orientation for which footprint bounds were last calculated, allowing translational
    /// movement without recalculating bounds while orientation remains unchanged.</summary>
    public short? FootprintBoundsDirection { get; internal set; }
    public List<XObject> LotListEntries { get; } = [];
    public uint Flags { get; set; }
    public bool PrepareClockSpecialUpdate()
    {
        if ((Flags & 0x00010000u) == 0) return false;
        Flags &= ~0x00000100u;
        return true;
    }
    /// <summary>The execution stack frames saved in the object's ObjM chunk. Scheduler errors must clear
    /// this list when dropping a live stack to prevent persisting stale execution state.</summary>
    public List<ObjectStackFrame> SavedStack { get; } = [];
    public int SavedStackSize { get; set; }
    /// <summary>The multi-tile and portal state stored in the trailing section of the object's ObjM chunk.</summary>
    public MultiTileState? SavedMultiTileState { get; set; }
    public ObjectPosition? MultiTileMovePositionState { get; internal set; }
    public short MultiTileMoveFlag1B0 { get; internal set; }
    public short MultiTileMoveValue1C4 { get; internal set; }
    public short MultiTileMoveValue1C6 { get; internal set; }
    public AssociatedPlacementHelper? AssociatedPlacementHelper { get; internal set; }
    public bool AssociatedPlacementExtensionFlag180 { get; internal set; }
    public XObject? MultiTileMoveAssociatedObject { get; set; }
    public float MultiTileMoveValue1C0 { get; internal set; }
    /// <summary>The lead object identifier for this multi-tile group. A value of zero indicates no linked
    /// lead object.</summary>
    public short MultiTileMasterId { get; set; }
    public short InteractionScopeKey5CA { get; set; }
    /// <summary>The identifier of the next object in the multi-tile group chain. A value of zero terminates
    /// the chain.</summary>
    public short MultiTileNextId { get; set; }
    private short _requestKey;
    public short RequestKey
    {
        get => _requestKey;
        set
        {
            _requestKey = value;
            HasRequestKey = true;
        }
    }
    public bool HasRequestKey { get; private set; }
    public short StackObjectKey => HasRequestKey ? RequestKey : Id;
    public XObject? ObjectTableEntry(short index) =>
        index > 0 && index <= ObjectTableEntries.Count ? ObjectTableEntries[index - 1] : null;

    public void SetObjectTableEntry(short index, XObject? item)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        while (ObjectTableEntries.Count < index) ObjectTableEntries.Add(null);
        ObjectTableEntries[index - 1] = item;
    }

    internal void AttachObjectTable(List<XObject?> table) => _objectTableEntries = table;

    public void SetObjectTableEffectWord(short index, short value) => ObjectTableEffectWords[index] = value;

    public void SetInteractionExecutorFloat(int selector, float value)
    {
        InteractionExecutorFloats[selector] = value;
        if (this is XPerson person && (uint)selector < (uint)person.Motives.Length)
            person.Motives[selector] = value;
    }

    public void SetInteractionExecutorWord(int offset, short value)
    {
        if (offset == 0x59c) InteractionBehaviorSavedWord = value;
        else InteractionExecutorWords[offset] = value;
        if (this is XPerson person)
        {
            switch (offset)
            {
                case 0x62c: person.PersonData[80] = value; break;
                case 0x62e: person.PersonData[81] = value; break;
                case 0x630: person.PersonData[82] = value; break;
            }
        }
    }

    /// <summary>Stores slot occupant object identifiers. Entry 0 is reserved for the overlap chain, while
    /// container attachment slots begin at index 1.</summary>
    public List<short> Slots { get; } = [];
    /// <summary>Parsed SLOT chunk entries in resource order: type 0 designates footprint slots, type 1
    /// designates runtime attachment slots, and type 3 designates routing slots.</summary>
    public List<Formats.Slot> DefinitionSlots { get; } = [];
    public List<ObjectFootprintSlot> FootprintSlots { get; } = [];
    /// <summary>Type-3 SLOT entries specifying target locations and orientations for routing slot requests.</summary>
    public List<RoutingSlotRequest> RoutingSlots { get; } = [];
    public List<ObjectRuntimeSlot> RuntimeSlots { get; } = [];
    public short[] DynamicSpriteFlags { get; set; } = [];
    public short[] TypeAttributes { get; set; } = [];
    public short InteractionBehaviorSavedWord { get; set; }
    public Dictionary<int, float> InteractionExecutorFloats { get; } = [];
    public Dictionary<int, short> InteractionExecutorWords { get; } = [];
    public TypeRecord? TypeRecord { get; set; }
    public ObjectPosition Position { get; set; }
    public RelationshipTable Relationships { get; } = new();

    public short this[ObjectData field]
    {
        get => Data[(int)field];
        set => Data[(int)field] = value;
    }
}

public sealed class TypeRecord(int key)
{
    public int Key { get; } = key;
    public byte ExtensionFlagsCC { get; init; }
    /// <summary>The alternative object type's OBJD price, used when the associated object definition
    /// specifies a master ID.</summary>
    public short? AlternativeObjectPrice { get; init; }
    public bool RuntimeExtension88Initialized { get; internal set; }
    public short ObjectType { get; init; } = -1;
    /// <summary>The multi-tile member sub-index loaded from the OBJD chunk, used to associate constituent
    /// multi-tile parts.</summary>
    public short SubIndex { get; init; }
    public short ObjectVersion { get; init; }
    /// <summary>The character animator body-string selector derived from OBJD BodyStrings entry 13.</summary>
    public short? AnimatorBodyStringMode { get; init; }
    /// <summary>Action function IDs resolved from the object's OBJf chunk or legacy OBJD entry points. Null
    /// indicates the type has not been initialized from an object definition.</summary>
    public IReadOnlyList<ushort>? FunctionTable { get; init; }
    /// <summary>Check-tree IDs paired with <see cref="FunctionTable"/>. Legacy OBJD function entries do not
    /// define check trees.</summary>
    public IReadOnlyList<ushort>? FunctionCheckTable { get; init; }
    public int? AlternativeTypeKey { get; init; }
    /// <summary>A packed four-nibble footprint extent mask rotated according to the object's facing
    /// direction.</summary>
    public ushort FootprintMask { get; init; }
    public int AnimationKey { get; init; }
    public string? Name { get; init; }
    /// <summary>Object name and catalog description text substitutions loaded from CTSS chunk entries 0 and
    /// 1.</summary>
    public string? CatalogName { get; init; }
    public string? CatalogDescription { get; init; }
    public ITreeTable? BehaviorScope { get; init; }
    /// <summary>The object's resolved TTAB interaction table, evaluated during autonomy scoring and
    /// interaction queuing.</summary>
    public InteractionTable? Interactions { get; init; }
    public short AutonomyAlternativeTypeFlag { get; init; }
}

public readonly record struct GatedObjectCallbackRequest(int First, int Second, int Third, int Fourth);

/// <summary>A timed animation event (XEVT) embedded in a character animation clip, measured in
/// milliseconds.</summary>
public readonly record struct AvatarAnimationEvent(int TimeMilliseconds, short Value)
{
    public static int MotionFrameToMilliseconds(int frame, int motionFrameCount, float durationMilliseconds) =>
        motionFrameCount > 0 && durationMilliseconds > 0f
            ? (int)(frame * (durationMilliseconds / motionFrameCount))
            : frame;

    /// <summary>Converts an authored motion frame index to playback milliseconds based on the clip's frame
    /// count and duration.</summary>
    public static AvatarAnimationEvent FromMotionFrame(int frame, short value, int motionFrameCount,
        float durationMilliseconds) => new(MotionFrameToMilliseconds(frame, motionFrameCount, durationMilliseconds), value);
}

public readonly record struct AvatarAnimationSoundEvent(int TimeMilliseconds, string PackId, string Resource, string Cue, bool Loop, bool Stop, string BoneName = "");

/// <summary>An animation marker identifying a bone and normalized event time, resolved from authored
/// animation event tags.</summary>
public readonly record struct AvatarAnimationMarker(float NormalizedTime, string Bone);

public readonly record struct HeadSeekGeometry(Vector2 Actor, Vector2 HeadFake, Vector2 Target);

public sealed record AvatarAnimationClip
{
    private readonly Lazy<(System.Numerics.Vector3 Offset, IReadOnlyList<System.Numerics.Vector3>? Samples)>? _lazyRootMotion;
    private readonly System.Numerics.Vector3 _standardOffset;
    private readonly IReadOnlyList<System.Numerics.Vector3>? _standardRootMotionSamples;

    public string Name { get; init; }
    public int FrameCount { get; init; }
    public IReadOnlyList<AvatarAnimationEvent> Events { get; init; }
    public IReadOnlyList<AvatarAnimationSoundEvent>? SoundEvents { get; init; }
    public float StandardDistance { get; init; }
    public System.Numerics.Vector3 StandardOffset
    {
        get => _lazyRootMotion is { } lazy ? lazy.Value.Offset : _standardOffset;
        init => _standardOffset = value;
    }
    public IReadOnlyList<System.Numerics.Vector3>? StandardRootMotionSamples
    {
        get => _lazyRootMotion is { } lazy ? lazy.Value.Samples : _standardRootMotionSamples;
        init => _standardRootMotionSamples = value;
    }
    public float DurationMilliseconds { get; init; }
    public AvatarAnimationMarker? LocomotionMarkerOne { get; init; }
    public AvatarAnimationMarker? LocomotionMarkerTwo { get; init; }

    public AvatarAnimationClip(string Name, int FrameCount, IReadOnlyList<AvatarAnimationEvent> Events,
        IReadOnlyList<AvatarAnimationSoundEvent>? SoundEvents = null, float StandardDistance = 0f,
        System.Numerics.Vector3 StandardOffset = default,
        IReadOnlyList<System.Numerics.Vector3>? StandardRootMotionSamples = null,
        float DurationMilliseconds = 0f,
        AvatarAnimationMarker? LocomotionMarkerOne = null,
        AvatarAnimationMarker? LocomotionMarkerTwo = null,
        Lazy<(System.Numerics.Vector3 Offset, IReadOnlyList<System.Numerics.Vector3>? Samples)>? LazyRootMotion = null)
    {
        this.Name = Name;
        this.FrameCount = FrameCount;
        this.Events = Events;
        this.SoundEvents = SoundEvents;
        this.StandardDistance = StandardDistance;
        _standardOffset = StandardOffset;
        _standardRootMotionSamples = StandardRootMotionSamples;
        this.DurationMilliseconds = DurationMilliseconds;
        this.LocomotionMarkerOne = LocomotionMarkerOne;
        this.LocomotionMarkerTwo = LocomotionMarkerTwo;
        _lazyRootMotion = LazyRootMotion;
    }
}

/// <summary>Serialized state for an animation playback channel, storing vector components as individual
/// primitives for save compatibility.</summary>
public sealed record AvatarAnimationPlaybackState(string Name, int FrameCount, AvatarAnimationEvent[] Events,
    AvatarAnimationSoundEvent[]? SoundEvents, float StandardDistance, float[] StandardOffset,
    float[][]? StandardRootMotionSamples, float DurationMilliseconds, bool Backwards, bool Loop, float Frame,
    bool EndReached, bool RouteDriven, byte EventsRun, int NextEventIndex, int NextSoundEventIndex,
    short[] EventQueue, AvatarAnimationSoundEvent[] SoundEventQueue)
{
    public bool InterpolatePose { get; init; } = true;
    public bool Interruptible { get; init; }
    /// <summary>Indicates a one-shot pool exit animation whose playback continues after completing the pool
    /// ladder path.</summary>
    public bool PoolSwimExit { get; init; }
    public long PracticeStartOrder { get; init; }
    public float PracticeWeight { get; init; } = 1f;
    public bool PracticeWeightFadeActive { get; init; }
    public int? PracticeWeightFadeStartMils { get; init; }
    public int? PracticeWeightFadeEndMils { get; init; }
    public float? PracticeWeightFadeStart { get; init; }
    public float? PracticeWeightFadeTarget { get; init; }
    public AvatarAnimationMarker? LocomotionMarkerOne { get; init; }
    public AvatarAnimationMarker? LocomotionMarkerTwo { get; init; }
    public int? LastPracticeApplyMils { get; init; }
    public float? PracticeScale { get; init; }
    public int PracticePriority { get; init; }
    public bool AllowUnblendedOverride { get; init; } = true;
    public string? AnchorBone { get; init; }
}

/// <summary>Manages character animation playback, advancing once per update and delivering queued XEVT
/// events to waiting threads during animation primitives.</summary>
public sealed class AvatarAnimationPlayback(AvatarAnimationClip clip, bool backwards, bool loop, int? startedAtMils = null)
{
    private static long nextPracticeStartOrder;
    private int nextEventIndex = backwards ? clip.Events.Count - 1 : 0;
    private int nextSoundEventIndex = backwards ? (clip.SoundEvents?.Count ?? 0) - 1 : 0;
    public long PracticeStartOrder { get; private set; } = Interlocked.Increment(ref nextPracticeStartOrder);
    public AvatarAnimationClip Clip { get; } = clip;
    public bool Backwards { get; } = backwards;
    public bool Loop { get; } = loop;
    /// <summary>Tracks the timestamp of the last applied animation update so that intervals skipped during
    /// root-only updates advance cumulatively on the next full pose update.</summary>
    public int? LastPracticeApplyMils { get; private set; } = startedAtMils;
    public float PracticeScale { get; private set; } = backwards ? -1f : 1f;
    public float Frame { get; private set; } = backwards ? clip.FrameCount : 0;
    public bool EndReached { get; private set; } = !loop && clip.FrameCount <= 0;
    /// <summary>Indicates whether to interpolate between adjacent pose frames, enabled only when the
    /// elapsed interval is less than two frames to avoid interpolation artifacts during large time jumps.
    /// </summary>
    public bool InterpolatePose { get; private set; } = true;
    /// <summary>Indicates whether animation progress is driven by path traversal, advancing locomotion
    /// frames in synchronization with root motion.</summary>
    public bool RouteDriven { get; init; }
    /// <summary>Indicates whether the active animation can be interrupted before completion, configured via
    /// animation parameters and dynamically toggled by timed animation properties.</summary>
    public bool Interruptible { get; set; }
    public bool PoolSwimExit { get; init; }
    public float PracticeWeight { get; set; } = 1f;
    public int PracticePriority { get; set; }
    public bool AllowUnblendedOverride { get; set; } = true;
    public string? AnchorBone { get; set; }
    private bool practiceWeightFadeActive;
    private int practiceWeightFadeStartMils;
    private int practiceWeightFadeEndMils;
    private float practiceWeightFadeStart;
    private float practiceWeightFadeTarget;
    public bool IsPracticeWeightFadeActive => practiceWeightFadeActive;

    public void FadePracticeWeight(int startMils, int endMils, float startWeight, float targetWeight)
    {
        practiceWeightFadeActive = true;
        practiceWeightFadeStartMils = startMils;
        practiceWeightFadeEndMils = endMils;
        practiceWeightFadeStart = startWeight;
        practiceWeightFadeTarget = targetWeight;
    }

    public float EvaluatePracticeWeight(int animatorMils, float subTickAlpha = 0f)
    {
        if (!practiceWeightFadeActive || subTickAlpha <= 0f)
            return PracticeWeight;
        var duration = unchecked(practiceWeightFadeEndMils - practiceWeightFadeStartMils);
        if (duration <= 0)
            return practiceWeightFadeTarget;
        var elapsed = unchecked(animatorMils - practiceWeightFadeStartMils) + subTickAlpha * 42f;
        var amount = Math.Clamp(elapsed / duration, 0f, 1f);
        return practiceWeightFadeStart + amount * (practiceWeightFadeTarget - practiceWeightFadeStart);
    }

    private void ApplyPracticeWeightFade(int animatorMils)
    {
        if (!practiceWeightFadeActive) return;
        var elapsed = unchecked(animatorMils - practiceWeightFadeStartMils);
        var duration = unchecked(practiceWeightFadeEndMils - practiceWeightFadeStartMils);
        if (duration <= 0 || elapsed > duration)
        {
            practiceWeightFadeActive = false;
            PracticeWeight = practiceWeightFadeTarget;
            return;
        }
        var amount = (float)elapsed / duration;
        amount = Math.Clamp(amount, 0f, 1f);
        PracticeWeight = practiceWeightFadeStart + amount * (practiceWeightFadeTarget - practiceWeightFadeStart);
    }
    public void SetFrame(float frame) => Frame = frame;
    public void SetPracticeScale(float scale) => PracticeScale = scale;
    public byte EventsRun { get; private set; }
    public List<short> EventQueue { get; } = [];
    public List<AvatarAnimationSoundEvent> SoundEventQueue { get; } = [];

    public AvatarAnimationPlaybackState SaveState() => new(Clip.Name, Clip.FrameCount, Clip.Events.ToArray(),
        Clip.SoundEvents?.ToArray(), Clip.StandardDistance, [Clip.StandardOffset.X, Clip.StandardOffset.Y, Clip.StandardOffset.Z],
        Clip.StandardRootMotionSamples?.Select(sample => new[] { sample.X, sample.Y, sample.Z }).ToArray(),
        Clip.DurationMilliseconds, Backwards, Loop, Frame, EndReached, RouteDriven, EventsRun, nextEventIndex,
        nextSoundEventIndex, EventQueue.ToArray(), SoundEventQueue.ToArray())
    {
        Interruptible = Interruptible,
        PoolSwimExit = PoolSwimExit,
        PracticeStartOrder = PracticeStartOrder,
        PracticeWeight = PracticeWeight,
        PracticeWeightFadeActive = practiceWeightFadeActive,
        PracticeWeightFadeStartMils = practiceWeightFadeActive ? practiceWeightFadeStartMils : null,
        PracticeWeightFadeEndMils = practiceWeightFadeActive ? practiceWeightFadeEndMils : null,
        PracticeWeightFadeStart = practiceWeightFadeActive ? practiceWeightFadeStart : null,
        PracticeWeightFadeTarget = practiceWeightFadeActive ? practiceWeightFadeTarget : null,
        LocomotionMarkerOne = Clip.LocomotionMarkerOne,
        LocomotionMarkerTwo = Clip.LocomotionMarkerTwo,
        LastPracticeApplyMils = LastPracticeApplyMils,
        PracticeScale = PracticeScale,
        InterpolatePose = InterpolatePose,
        PracticePriority = PracticePriority,
        AllowUnblendedOverride = AllowUnblendedOverride,
        AnchorBone = AnchorBone,
    };

    public static AvatarAnimationPlayback RestoreState(AvatarAnimationPlaybackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FrameCount < 0 || state.Events is null || state.EventQueue is null || state.SoundEventQueue is null ||
            state.StandardOffset is not { Length: 3 } || state.StandardRootMotionSamples?.Any(sample => sample is not { Length: 3 }) == true ||
            state.NextEventIndex < -1 || state.NextEventIndex > state.Events.Length ||
            state.NextSoundEventIndex < -1 || state.NextSoundEventIndex > (state.SoundEvents?.Length ?? 0) ||
            state.PracticeScale is { } savedScale && !float.IsFinite(savedScale) ||
            (state.PracticeWeightFadeActive && (state.PracticeWeightFadeStartMils is null ||
                state.PracticeWeightFadeEndMils is null || state.PracticeWeightFadeStart is null ||
                state.PracticeWeightFadeTarget is null)))
            throw new InvalidDataException("Invalid saved avatar animation state.");
        var clip = new AvatarAnimationClip(state.Name, state.FrameCount, state.Events, state.SoundEvents,
            state.StandardDistance, new System.Numerics.Vector3(state.StandardOffset[0], state.StandardOffset[1], state.StandardOffset[2]),
            state.StandardRootMotionSamples?.Select(sample => new System.Numerics.Vector3(sample[0], sample[1], sample[2])).ToArray(),
            state.DurationMilliseconds, state.LocomotionMarkerOne, state.LocomotionMarkerTwo);
        var result = new AvatarAnimationPlayback(clip, state.Backwards, state.Loop, state.LastPracticeApplyMils)
        {
            Frame = state.Frame,
            InterpolatePose = state.InterpolatePose,
            PracticeScale = state.PracticeScale ?? (state.Backwards ? -1f : 1f),
            EndReached = state.EndReached || (!state.Loop && state.FrameCount <= 0),
            RouteDriven = state.RouteDriven,
            Interruptible = state.Interruptible,
            PoolSwimExit = state.PoolSwimExit,
            PracticeWeight = state.PracticeWeight,
            practiceWeightFadeActive = state.PracticeWeightFadeActive,
            practiceWeightFadeStartMils = state.PracticeWeightFadeStartMils ?? 0,
            practiceWeightFadeEndMils = state.PracticeWeightFadeEndMils ?? 0,
            practiceWeightFadeStart = state.PracticeWeightFadeStart ?? state.PracticeWeight,
            practiceWeightFadeTarget = state.PracticeWeightFadeTarget ?? state.PracticeWeight,
            PracticePriority = state.PracticePriority,
            AllowUnblendedOverride = state.AllowUnblendedOverride,
            AnchorBone = state.AnchorBone,
            EventsRun = state.EventsRun,
            nextEventIndex = state.NextEventIndex,
            nextSoundEventIndex = state.NextSoundEventIndex,
        };
        if (state.PracticeStartOrder > 0)
        {
            result.PracticeStartOrder = state.PracticeStartOrder;
            AdvancePracticeOrder(state.PracticeStartOrder);
        }
        result.EventQueue.AddRange(state.EventQueue);
        result.SoundEventQueue.AddRange(state.SoundEventQueue);
        return result;
    }

    private static void AdvancePracticeOrder(long value)
    {
        var current = Volatile.Read(ref nextPracticeStartOrder);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref nextPracticeStartOrder, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    /// <summary>Advances animation playback by elapsed milliseconds (typically 42 ms ticks). Timed events
    /// crossed during the interval are dispatched in chronological order, or reverse chronological order
    /// when playing backwards.</summary>
    public void Tick(bool deliverAnimationEvents = true, int? animatorMils = null)
    {
        if (animatorMils is { } now) ApplyPracticeWeightFade(now);
        var elapsedMils = animatorMils is { } currentMils && LastPracticeApplyMils is { } previousMils
            ? unchecked(currentMils - previousMils)
            : 42;
        if (Clip.FrameCount > 0)
        {
            var poseDuration = Clip.DurationMilliseconds > 0f
                ? Clip.DurationMilliseconds
                : Clip.FrameCount * (1000f / 30f);
            var scale = MathF.Abs(PracticeScale);
            var frameInterval = scale > 0f ? poseDuration / (Clip.FrameCount * scale) : float.PositiveInfinity;
            // Disable pose interpolation when elapsed time spans two or more frame intervals.
            InterpolatePose = frameInterval + frameInterval > elapsedMils;
        }
        else InterpolatePose = false;
        if (animatorMils is { } appliedMils) LastPracticeApplyMils = appliedMils;
        if (Clip.FrameCount <= 0)
        {
            if (!Loop) EndReached = true;
            return;
        }
        if (RouteDriven || EndReached) return;
        var duration = Clip.DurationMilliseconds;
        var backwards = PracticeScale < 0f;
        var practiceScale = MathF.Abs(PracticeScale);
        var frameStep = duration > 0f
            ? Clip.FrameCount * (elapsedMils / duration) * practiceScale
            : 1.2f * (elapsedMils / 42f) * practiceScale;
        var previousFrame = Frame;
        Frame += backwards ? -frameStep : frameStep;
        if (!Loop)
        {
            Frame = Math.Clamp(Frame, 0f, Clip.FrameCount);
            EndReached = backwards ? Frame <= 0f : Frame >= Clip.FrameCount;
        }
        var time = duration > 0f ? Frame * (duration / Clip.FrameCount) : Frame * (1000f / 30f);
        var processedLoopBoundary = false;
        // Skip timed events when blend weight is non-positive, preserving event cursors.
        if (PracticeWeight > 0f && Loop && duration > 0f && frameStep > 0f &&
            (backwards ? Frame < 0f : Frame > Clip.FrameCount))
        {
            // Deliver remaining tail events and wrapped head events across the loop boundary in one tick.
            var remainingMilliseconds = frameStep * (duration / Clip.FrameCount);
            var currentTime = previousFrame * (duration / Clip.FrameCount);
            if (!backwards)
            {
                while (remainingMilliseconds > 0f)
                {
                    var endTime = Math.Min(duration, currentTime + remainingMilliseconds);
                    QueueForwardEventsThrough(endTime);
                    remainingMilliseconds -= endTime - currentTime;
                    if (endTime < duration || remainingMilliseconds <= 0f) break;
                    nextEventIndex = 0;
                    nextSoundEventIndex = 0;
                    currentTime = 0f;
                }
            }
            else
            {
                while (remainingMilliseconds > 0f)
                {
                    var startTime = Math.Max(0f, currentTime - remainingMilliseconds);
                    QueueBackwardEventsThrough(startTime);
                    remainingMilliseconds -= currentTime - startTime;
                    if (startTime > 0f || remainingMilliseconds <= 0f) break;
                    nextEventIndex = Clip.Events.Count - 1;
                    nextSoundEventIndex = (Clip.SoundEvents?.Count ?? 0) - 1;
                    currentTime = duration;
                }
            }
            processedLoopBoundary = true;
        }
        else if (PracticeWeight > 0f && backwards)
        {
            QueueBackwardEventsThrough(time);
        }
        else if (PracticeWeight > 0f)
        {
            QueueForwardEventsThrough(time);
        }

        if (Frame > Clip.FrameCount || Frame < 0)
        {
            if (Loop)
            {
                Frame %= Clip.FrameCount;
                if (Frame < 0) Frame += Clip.FrameCount;
                if (!processedLoopBoundary)
                {
                    nextEventIndex = backwards ? Clip.Events.Count - 1 : 0;
                    nextSoundEventIndex = backwards ? (Clip.SoundEvents?.Count ?? 0) - 1 : 0;
                }
            }
            else EndReached = true;
        }

        void QueueForwardEventsThrough(float throughTime)
        {
            var sounds = Clip.SoundEvents ?? [];
            while (nextSoundEventIndex < sounds.Count)
            {
                var item = sounds[nextSoundEventIndex];
                if (item.TimeMilliseconds > throughTime) break;
                nextSoundEventIndex++;
                SoundEventQueue.Add(item);
            }
            while (nextEventIndex < Clip.Events.Count)
            {
                var item = Clip.Events[nextEventIndex];
                if (item.TimeMilliseconds > throughTime) break;
                nextEventIndex++;
                if (deliverAnimationEvents) EventQueue.Add(item.Value);
            }
        }

        void QueueBackwardEventsThrough(float throughTime)
        {
            var sounds = Clip.SoundEvents ?? [];
            while (nextSoundEventIndex >= 0)
            {
                var item = sounds[nextSoundEventIndex];
                if (item.TimeMilliseconds < throughTime) break;
                nextSoundEventIndex--;
                SoundEventQueue.Add(item);
            }
            while (nextEventIndex >= 0)
            {
                var item = Clip.Events[nextEventIndex];
                if (item.TimeMilliseconds < throughTime) break;
                nextEventIndex--;
                if (deliverAnimationEvents) EventQueue.Add(item.Value);
            }
        }
    }

    /// <summary>Supplies missing expected numbered animation events one at a time after all authored events
    /// have been exhausted, preventing waiting scripts from stalling.</summary>
    public short? TakeMissingExpectedEvent(sbyte expectedCount, bool? backwardsOverride = null)
    {
        if (EventsRun >= expectedCount) return null;
        var backwards = backwardsOverride ?? Backwards;
        var expected = backwards ? expectedCount - EventsRun - 1 : EventsRun;
        EventsRun++;
        return (short)expected;
    }

    /// <summary>Validates an authored numbered animation event against the next expected index before
    /// exposing it to the script. Animation events 100 and above are independent substitutes and do not
    /// advance this counter.</summary>
    public bool TryConsumeExpectedEvent(short value, sbyte expectedCount, out short expectedValue, bool? backwardsOverride = null)
    {
        expectedValue = value;
        if (value >= 100) return true;
        var backwards = backwardsOverride ?? Backwards;
        var expected = backwards ? expectedCount - EventsRun - 1 : EventsRun;
        if (expected < 0 || expected >= expectedCount || value != expected) return false;
        expectedValue = (short)expected;
        EventsRun++;
        return true;
    }
}

public readonly record struct GenericSimsCallRequestResult(short Temporary0, short Temporary1, int Status);

public readonly record struct GenericSimsCallRequestCompletion(
    short Temporary0,
    short Temporary1,
    bool Succeeded,
    int? TargetTypeRecordKey = null);

public sealed class XPerson(short id, short[] definition, int attributes = 0) : XObject(id, definition, attributes)
{
    public const short PersonType = 2;

    public List<GenericSimsCallPersonTypeRecord> GenericSimsCallPersonTypeRecords { get; } = [];

    public short GenericSimsCallPersonTypeRecordIndex { get; set; }

    public override short SelectionWord5CC
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.PersonType];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.PersonType] = value;
    }

    public override short SelectionWord606
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.FamilyNumber];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.FamilyNumber] = value;
    }

    public bool TryBeginObjectReferenceNotification()
    {
        if ((Flags & 0x00000800u) != 0) return false;
        Flags |= 0x00000800u;
        return true;
    }

    public void EndObjectReferenceNotification() => Flags &= ~0x00000800u;

    public void ClearPreviousActionRecord() => PreviousActionRecord = ActionQueueSlot.EmptyRecord();

    public void InvalidateRuntimeObjectReferences(XObject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Definition[(int)ObjdField.Type] != PersonType) return;
        foreach (var entry in RuntimeObjectReferences)
        {
            if (!ReferenceEquals(entry.Target, subject)) continue;
            entry.State84 = 1;
            entry.Target = null;
            entry.RawWord90 = 0;
        }
    }

    public short InteractionActionWord72
    {
        get => this[ObjectData.TreeTableEntry];
        set => this[ObjectData.TreeTableEntry] = value;
    }
    public ObjectPosition? LastLookTowardsPoint { get; set; }

    public short FootprintFlags614 { get => this[global::SimsEngine.Simulation.PersonData.IsGhost]; set => this[global::SimsEngine.Simulation.PersonData.IsGhost] = value; }
    public short FootprintFlags634 { get => this[global::SimsEngine.Simulation.PersonData.IsLivingGhost]; set => this[global::SimsEngine.Simulation.PersonData.IsLivingGhost] = value; }

    public short[] PersonData { get; } = new short[256];
    public float[] Motives { get; } = new float[16];
    public AvatarAnimationPlayback? AnimationPlayback { get; set; }
    /// <summary>Receives animation events dispatched from all active animation practices. Animation
    /// playback consumes events from this shared queue rather than from individual practices.</summary>
    public List<short> PendingAnimationEvents { get; } = [];
    public float AnimatorWalkingScale { get; set; } = 1f;
    public bool? AnimatorHeadSeekRegistrationsAvailable { get; set; }
    public float AnimatorHeadAdjustment { get; set; }
    public float AnimatorHeadBaselineDirection { get; set; }
    public float AnimatorHeadTargetDirection { get; set; }
    public Vector2 AnimatorHeadTargetPoint { get; set; }
    public float AnimatorBodyScale { get; set; } = 1f;
    public bool AnimatorScaleUpdateSuppressed { get; set; }
    public float? AnimatorRootX { get; set; }
    public float? AnimatorRootY { get; set; }
    public float? AnimatorRootDirection { get; set; }
    public float AnimatorDisplacementX { get; private set; }
    public float AnimatorDisplacementY { get; private set; }
    public float AnimatorDisplacementDirection { get; private set; }
    public float AnimatorProjectedRootStepX { get; set; }
    public float AnimatorProjectedRootStepY { get; set; }
    public void SetAnimatorDisplacements(float x, float y, float direction)
    {
        AnimatorDisplacementX = x;
        AnimatorDisplacementY = y;
        AnimatorDisplacementDirection = direction;
    }
    public int AnimatorControlState { get; set; } = 3;
    public bool AnimatorPracticeScaleEnabled { get; set; }
    public int AnimatorCurrentSkill { get; set; } = -1;
    public int AnimatorMilliseconds { get; set; }
    public int AnimatorLocomotionFadeGate { get; set; }
    public int AnimatorPoolTransitionState { get; set; }
    public int AnimatorRouteTransitionState { get; set; }
    public int AnimatorStateTailField198 { get; set; }
    public bool AnimatorPoolTile { get; set; }
    public int AnimatorGaitEntryState { get; set; }
    public short AnimatorBodyStringMode { get; set; }
    public AvatarAnimationPlayback? BackgroundAnimationPlayback { get; set; }
    public short BackgroundPracticePersonState { get; set; } = -1;
    public AvatarAnimationPlayback? LastAnimationPlayback { get; set; }
    public bool AnimatorDiscontinuityPending { get; set; }
    public string? AnimatorAnchorBone { get; set; }
    public bool AnimatorAnchorInitialized { get; set; }
    public Vector3 AnimatorAnchorWorldPosition { get; set; }
    public bool PrimaryAnimationWaiting { get; set; }
    public byte PrimaryAnimationEventsRun { get; set; }
    public bool PrimaryAnimationBackwards { get; set; }
    public AvatarAnimationPlayback? CarryAnimationPlayback { get; set; }
    public int CarryPracticeMode { get; set; }
    /// <summary>Returns active animation practices in skeletal layering order: background practices at
    /// priority -10, primary practices at PracticePriority (default 0, +5 on discontinuity), fading Last
    /// practices at oldPrimary.PracticePriority + 1, and carry practices at priority +10.</summary>
    public IReadOnlyList<AvatarAnimationPlayback> GetAnimationPracticeApplicationOrder()
    {
        var result = new List<(AvatarAnimationPlayback Playback, int Priority, long StartOrder)>(4);
        if (BackgroundAnimationPlayback is { } background) result.Add((background, -10, background.PracticeStartOrder));
        if (AnimationPlayback is { } primary) result.Add((primary, primary.PracticePriority, primary.PracticeStartOrder));
        if (LastAnimationPlayback is { } last) result.Add((last, last.PracticePriority, last.PracticeStartOrder));
        if (CarryAnimationPlayback is { } carry)
            result.Add((carry, 10, carry.PracticeStartOrder));
        result.Sort(static (left, right) =>
        {
            var priority = left.Priority.CompareTo(right.Priority);
            return priority != 0 ? priority : left.StartOrder.CompareTo(right.StartOrder);
        });
        return result.ConvertAll(static entry => entry.Playback);
    }

    public bool TryDequeueAnimationEvent(out short value)
    {
        if (PendingAnimationEvents.Count > 0)
        {
            value = PendingAnimationEvents[0];
            PendingAnimationEvents.RemoveAt(0);
            return true;
        }
        if (AnimationPlayback is { EventQueue.Count: > 0 } playback)
        {
            value = playback.EventQueue[0];
            playback.EventQueue.RemoveAt(0);
            return true;
        }
        value = default;
        return false;
    }
    public int CarryPracticeCachedState { get; set; } = -1;
    /// <summary>Indicates whether a reach animation is active, deferring hand-slot changes until the reach
    /// animation completes.</summary>
    public bool ReachAnimationActive { get; set; }
    public RouteState? NativeRoute { get; set; }
    /// <summary>Stores routes suspended while a portal function executes a nested route on the person
    /// stack.</summary>
    public List<RouteState> NativeRouteParents { get; } = [];
    public void ApplyMotiveChanges()
    {
        foreach (var change in MotiveChanges)
        {
            var index = (int)change.Motive;
            var current = Motives[index];
            // Branches on the signed rate, not on whether target is positive or negative.
            if (change.Rate >= 0f)
            {
                if (current < change.Target)
                {
                    current += change.Rate;
                    if (change.Target < current) current = change.Target;
                }
            }
            else if (change.Target < current)
            {
                current += change.Rate;
                if (current < change.Target) current = change.Target;
            }
            Motives[index] = current;
        }
    }
    public float[] SavedPersonValues7CC { get; } = new float[16];
    public int MotiveDecayLastTickCounter { get; set; }
    public short PersonTickGateWord8E { get; set; }
    public ushort LastObservedRoomIdAFC { get; set; }
    public PersonCharacter? SavedCharacter { get; set; }
    /// <summary>Transient rendered character appearance, such as temporary winter clothing. Save
    /// serialization always reads <see cref="SavedCharacter"/> to prevent runtime clothing changes from
    /// overwriting the persistent outfit baseline.</summary>
    public PersonCharacter? PresentationCharacter { get; set; }
    public List<(string Appearance, string Texture)> TransientExtraParts { get; } = [];

    public void DressAccessory(string appearance, string texture = "")
    {
        var suit = appearance.Trim();
        var suitTexture = texture.Trim();
        if (suit.Length == 0 || suit.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;
        UndressAccessory(suit);
        TransientExtraParts.Add((suit, suitTexture));
        if (SavedCharacter is { } character)
        {
            var appearances = character.ExtraParts.ToList();
            appearances.Add((suit, suitTexture));
            SavedCharacter = character with { ExtraParts = appearances };
        }
        if (PresentationCharacter is { } presentation)
        {
            var appearances = presentation.ExtraParts.ToList();
            appearances.Add((suit, suitTexture));
            PresentationCharacter = presentation with { ExtraParts = appearances };
        }
    }

    /// <summary>Removes all accessory entries matching <paramref name="appearance"/> case-insensitively,
    /// ignoring any optional <c>.apr</c> extension.</summary>
    public void UndressAccessory(string appearance)
    {
        var key = NormalizeAccessorySuitName(appearance);
        if (key.Length == 0) return;
        TransientExtraParts.RemoveAll(part =>
            NormalizeAccessorySuitName(part.Appearance).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (SavedCharacter is { } character)
        {
            var appearances = character.ExtraParts
                .Where(part => !NormalizeAccessorySuitName(part.Item1).Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (appearances.Count != character.ExtraParts.Count)
                SavedCharacter = character with { ExtraParts = appearances };
        }
        if (PresentationCharacter is { } presentation)
        {
            var appearances = presentation.ExtraParts
                .Where(part => !NormalizeAccessorySuitName(part.Item1).Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (appearances.Count != presentation.ExtraParts.Count)
                PresentationCharacter = presentation with { ExtraParts = appearances };
        }
    }

    public void ClearExtraParts()
    {
        TransientExtraParts.Clear();
        if (SavedCharacter is { ExtraParts.Count: > 0 } character)
            SavedCharacter = character with { ExtraParts = [] };
        if (PresentationCharacter is { ExtraParts.Count: > 0 } presentation)
            PresentationCharacter = presentation with { ExtraParts = [] };
    }

    private static string NormalizeAccessorySuitName(string name)
    {
        var trimmed = name.Trim();
        foreach (var suffix in new[] { ".cmx.bcf", ".apr", ".bcf", ".cmx" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }
        return trimmed;
    }
    public bool NativeOutfitTransitionActive { get; set; }
    public int SavedPersonActionStorageCount { get; set; }
    public List<PersonRuntimeObjectReference> RuntimeObjectReferences { get; } = [];
    public List<PersonRuntimeStateRecord> RuntimeStateRecords { get; } = [];
    public List<PersonActionRequest> PendingActionRequests { get; } = [];
    public PersonAction? SavedCurrentAction { get; set; }
    public PersonAction? SavedQueuedAction { get; set; }
    public List<PersonAction> SavedActions { get; } = [];
    public List<PersonObjectReference> SavedPersonObjectReferences { get; } = [];
    public List<PersonValueEntry> SavedPersonValues { get; } = [];
    public short ActivePriority
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.Priority];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.Priority] = value;
    }
    public int GenericSimsCall02GateWordA84
    {
        get => CurrentActionRecord?.Sequence ?? 0;
        set
        {
            if (CurrentActionRecord is { } record) record.Sequence = value;
            else CurrentActionRecord = new ActionQueueSlot { Sequence = value };
        }
    }
    public bool ActiveActionCancelRequested { get; set; }
    public HashSet<string> InteractionAttachments { get; } = new(StringComparer.Ordinal);
    public XObject? GenericSimsCall02ObjectA54 { get; set; }
    public ActionQueueSlot? CurrentActionRecord { get; set; }
    public ActionQueueSlot? PreviousActionRecord { get; private set; }
    public void RestoreActionState(PersonState state, SimulationWorld world)
    {
        CurrentActionRecord = ActionQueueSlot.FromSaved(state.First, world);
        PreviousActionRecord = ActionQueueSlot.FromSaved(state.Second, world);
        world.RestoreActionSequence(state.First.Id);
        world.RestoreActionSequence(state.Second.Id);
        ActionQueueCurrent = ActionQueueEnd = 0;
        foreach (var action in state.Actions)
        {
            world.RestoreActionSequence(action.Id);
            if (ActionQueueEnd < ActionQueue.Length)
                ActionQueue[ActionQueueEnd++] = ActionQueueSlot.FromSaved(action, world);
        }
        GenericSimsCall02ObjectA54 = CurrentActionRecord.Secondary;
        GenericSimsCall02GateWordA84 = CurrentActionRecord.Sequence;
    }

    public IReadOnlyList<PersonAction> WriteActionQueue()
    {
        var count = unchecked(ActionQueueEnd - ActionQueueCurrent);
        if (count is < 0 or > 8) throw new InvalidDataException($"Person {Id}: invalid action queue count {count}.");
        return Enumerable.Range(0, count).Select(index => ActionQueue[(ActionQueueCurrent + index) & 7].ToSaved()).ToArray();
    }

    public byte ActionFlagByte20 { get; set; }
    public short CurrentActionWord5D8
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.MultipleRoutes];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.MultipleRoutes] = value;
    }
    public short CurrentActionWord5DA
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.RoutingComplete];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.RoutingComplete] = value;
    }

    public short this[PersonData field] { get => PersonData[(int)field]; set => PersonData[(int)field] = value; }
    public float this[Motive motive] { get => Motives[(int)motive]; set => Motives[(int)motive] = value; }

    /// <summary>Recomputes footprint bounds from position, ghost dimensions, type-record extent nibbles,
    /// and route extensions applied on even facings.</summary>
    public void RefreshFootprintBounds()
    {
        FootprintBounds = FootprintBoundsAt(Position);
        FootprintBoundsDirection = this[ObjectData.Direction];
    }

    public FootprintBounds FootprintBoundsAt(ObjectPosition position)
    {
        var span = (FootprintFlags614 & 1) != 0 || (FootprintFlags634 & 1) != 0 ? 0 : 6;
        var half = span / 2;
        var mask = TypeRecord?.FootprintMask
            ?? unchecked((ushort)Definition[(int)ObjdField.FootprintMask]);
        static int Extent(ushort value, int index) => (value >> (index * 4)) & 0xf;
        var extents = Enumerable.Range(0, 4).Select(index => Extent(mask, index)).ToArray();
        var extension = this[global::SimsEngine.Simulation.PersonData.FootprintExtension];
        if (extension != 0 && (this[ObjectData.Direction] & 1) == 0)
            for (var index = 0; index < extents.Length; index++)
                extents[index] -= Extent(unchecked((ushort)extension), index);
        var rotation = (8 - this[ObjectData.Direction]) / 2;
        return new FootprintBounds(
            position.X + half - extents[(rotation + 1) % 4],
            position.Y + half - extents[(rotation + 2) % 4],
            position.X - half + extents[(rotation + 3) % 4],
            position.Y - half + extents[rotation % 4]);
    }

    public void ApplyStartedActionState(ActionQueueSlot action, int? replacementSequence = null)
    {
        if (CurrentActionRecord?.Owner is not null)
        {
            PreviousActionRecord = CurrentActionRecord.Copy();
            PreviousActionRecord.Flags = (PreviousActionRecord.Flags & ~0x20) | (ActionFlagByte20 != 0 ? 0x20 : 0);
        }
        CurrentActionRecord = action.Copy();
        if (CurrentActionRecord.Owner is not null)
        {
            CurrentActionWord5D8 = 0;
            CurrentActionWord5DA = 0;
            if (GenericSimsCall02GateWordA84 == 0 && replacementSequence is { } sequence)
                CurrentActionRecord.Sequence = sequence;
        }
        GenericSimsCall02ObjectA54 = CurrentActionRecord.Secondary;
        ActivePriority = unchecked((short)action.Threshold);
        MotiveChanges.Clear();
        if (this[global::SimsEngine.Simulation.PersonData.FootprintExtension] != 0)
        {
            this[global::SimsEngine.Simulation.PersonData.FootprintExtension] = 0;
            RefreshFootprintBounds();
        }
        this[global::SimsEngine.Simulation.PersonData.RouteFootprintMask] = 0;
        this[global::SimsEngine.Simulation.PersonData.NonInterruptible] = 0;
    }

    public void SetCurrentActionSecondaryReference(XObject? value)
    {
        GenericSimsCall02ObjectA54 = value;
        if (CurrentActionRecord is { } current) current.Secondary = value;
    }

    public List<MotiveChange> MotiveChanges { get; } = [];
    public List<XObject> InteractingObjects { get; } = [];

    public byte InputFlags
    {
        get => unchecked((byte)Data[(int)ObjectData.Flags]);
        set => Data[(int)ObjectData.Flags] = unchecked((short)(((ushort)Data[(int)ObjectData.Flags] & 0xff00) | value));
    }
    public const byte IdleForInputFlag = 0x40;
    public int ActionQueueCurrent { get; set; }
    public int ActionQueueEnd { get; set; }
    public ActionQueueSlot[] ActionQueue { get; } = [new(), new(), new(), new(), new(), new(), new(), new()];
    public float IdleFloat7A0 { get => Motives[5]; set => Motives[5] = value; }
    public float IdleFloat7A8 { get => Motives[7]; set => Motives[7] = value; }
    public float IdleFloat7B0 { get => Motives[9]; set => Motives[9] = value; }
}

public sealed record PersonActionRequest(XObject Target, int Priority, bool Flag);

public sealed record FoundActionPushFailure(short PersonId, short TargetId, short Key, short ActionTree, string Reason);

public sealed class PersonRuntimeObjectReference(XObject? target, byte state84 = 0, int rawWord90 = 0, int rawWord28 = 0)
{
    public XObject? Target { get; set; } = target;
    public byte State84 { get; set; } = state84;
    public int RawWord90 { get; set; } = rawWord90;
    public int RawWord28 { get; set; } = rawWord28;
}

public sealed class PersonRuntimeStateRecord(XObject? target = null, int rawWord28 = 0)
{
    public XObject? Target { get; set; } = target;
    public int RawWord28 { get; set; } = rawWord28;
}

public sealed record PersonObjectReference(short SavedObjectKey, XObject? Target, int Value, bool Flag);

public sealed class ActionQueueSlot
{
    public int RawDword10 { get; set; }
    public int Threshold { get; set; }
    public int Flags { get; set; }
    public XPerson? Person { get; init; }
    public XObject? Owner { get; init; }
    public XObject? Secondary { get; set; }
    public int RecordKind { get; init; }
    public short[] Parameters { get; init; } = new short[4];
    public byte InteractionId { get => unchecked((byte)RawDword10); init => RawDword10 = value; }
    public ushort ActionTree { get; init; }
    public float Attenuation { get; init; }
    public int Sequence { get; set; }
    public string? ActionText { get; set; }
    /// <summary>Evaluated motive advertisements for the interaction, including dynamic tree adjustments and
    /// zeroing the Room motive across room boundaries.</summary>
    public MotiveAdvertisement[]? EvaluatedAdvertisements { get; set; }

    public static ActionQueueSlot FromSaved(PersonAction action, SimulationWorld world) => new()
    {
        RecordKind = action.Value0x00,
        Sequence = action.Id,
        Person = world.ObjectById(action.Person) as XPerson,
        Owner = world.ObjectById(action.Object0x08),
        Secondary = world.ObjectById(action.Object0x0C),
        RawDword10 = action.Value0x10,
        Parameters = action.Values0x14.ToArray(),
        Threshold = action.Value0x1C,
        ActionTree = unchecked((ushort)action.Value0x20),
        Attenuation = action.Value0x24,
        Flags = action.Flags,
    };

    public PersonAction ToSaved() => new(RecordKind, Sequence, Person?.RequestKey ?? 0,
        Owner?.RequestKey ?? 0, Secondary?.RequestKey ?? 0, RawDword10, Parameters.ToArray(), Threshold,
        unchecked((short)ActionTree), Attenuation, Flags);

    public static ActionQueueSlot EmptyRecord() => new() { RawDword10 = -1 };

    public ActionQueueSlot Copy() => new()
    {
        RawDword10 = RawDword10,
        RecordKind = RecordKind,
        Parameters = Parameters.ToArray(),
        Threshold = Threshold,
        Flags = Flags,
        Person = Person,
        Owner = Owner,
        Secondary = Secondary,
        ActionTree = ActionTree,
        Attenuation = Attenuation,
        Sequence = Sequence,
        ActionText = ActionText,
        EvaluatedAdvertisements = EvaluatedAdvertisements?.ToArray(),
    };
}

public sealed record ActionQueueRemovalNotice(short PersonKey, int QueuePosition);
public sealed record QueuedInteractionStartTrace(short PersonId, short? TargetId, short ActionTree,
    bool TargetScopeFound, bool InteractionPassed, bool ActionTreePushed, ushort GuardTree,
    short PersonSelectionWord, short PersonAge, short PersonGender, int StateClass, short TargetFlags);
public sealed class GenericSimsCallPersonTypeRecord(
    int registryIndex, short index, int typeRecordKey, short objectType, string? catalogName, string typeName)
{
    public int RegistryIndex { get; } = registryIndex;
    public short Index { get; } = index;
    public int TypeRecordKey { get; } = typeRecordKey;
    public short ObjectType { get; } = objectType;
    public string? CatalogName { get; } = catalogName;
    public string TypeName { get; } = typeName;
    public short[] Payload { get; } = new short[256];
}
public sealed record GenericSimsCallChooserCandidate(
    int RegistryIndex,
    short PersonTypeRecordIndex,
    int TypeRecordKey,
    int PersonRecordIndex,
    short? LivePersonId,
    string? NeighborName,
    short[]? PersonData,
    string? CatalogName,
    string TypeName);

public sealed class NeighborState(short id, short[] definition)
{
    public short Id { get; } = id;
    public string? Name { get; set; }
    public int TypeRecordKey { get; set; }
    public uint? FriendEligibilityObjectFlagsB4 { get; set; }
    public int PersonRecordVersion { get; set; }
    public short[] PersonData { get; } = new short[256];
    public short[] Definition { get; } = definition;
    public RelationshipTable Relationships { get; } = new();
    public bool RelationshipsChangedFlag24 { get; set; }
    public int FriendCountWord20 { get; set; }
}

public sealed class StackFrame(short stackObject, short[] parameters, short[] locals)
{
    internal short[] StackObjectCell { get; } = [stackObject];
    public short StackObject { get => StackObjectCell[0]; set => StackObjectCell[0] = value; }
    public short[] Parameters { get; } = parameters;
    public short[] Locals { get; } = locals;
    public object? Tree { get; init; }
    public short TreeId { get; init; } = -1;
    /// <summary>Indicates a debugger breakpoint on the entire tree, stored in bit 15 of the tree ID.</summary>
    public bool TreeBreakpoint { get; init; }
    /// <summary>The current instruction node index, where 254 and 255 represent terminal false and true
    /// return branches.</summary>
    public int Instruction { get; set; }
    public int WaitState { get; set; }
    /// <summary>Indicates whether the frame is waiting. While set, a returning callee does not branch this
    /// frame.</summary>
    public bool Waiting { get => WaitState != 0; set => WaitState = value ? 1 : 0; }
    public short CodeOwner { get; init; }
    public ITreeTable? Table { get; init; }
}

public sealed class RoutingSlotRequest
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int Standing { get; set; }
    public int Sitting { get; set; }
    public int Ground { get; set; }
    public uint Flags { get; set; }
    public bool CheckCandidateFootprintAgainstWall { get; set; }
    public int SnapTargetSlot { get; set; } = -1;
    public int MinProximity { get; set; } = 16;
    public int MaxProximity { get; set; } = 16;
    public int OptimalProximity { get; set; } = 16;
    public float Gradient { get; set; } = 0.1875f;
    /// <summary>Specifies target facing direction: -1 faces opposite the anchor, -2 faces back toward the
    /// anchor from the chosen approach side, -3 preserves current facing, and 0 through 7 are relative to
    /// the anchor.</summary>
    public int Facing { get; set; } = -2;
    public int Resolution { get; set; } = 16;

    public static RoutingSlotRequest FromDefinition(Formats.Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Type != 3) throw new ArgumentException("Only type-3 SLOT entries build routing slots.", nameof(slot));
        return new RoutingSlotRequest
        {
            X = slot.X, Y = slot.Y, Z = slot.Z,
            Standing = slot.Standing, Sitting = slot.Sitting, Ground = slot.Ground,
            Flags = slot.Flags, SnapTargetSlot = slot.SnapTargetSlot,
            MinProximity = slot.MinProximity, MaxProximity = slot.MaxProximity,
            OptimalProximity = slot.OptimalProximity, Gradient = slot.Gradient,
            Facing = slot.Facing, Resolution = slot.Resolution,
        };
    }
}

public sealed class ObjectRuntimeSlot
{
    private readonly int[] _words = new int[15];

    public int? SelectorTypeKey { get; private set; }

    public int ReadWord(int byteOffset) => _words[WordIndex(byteOffset)];
    public void WriteWord(int byteOffset, int value) => _words[WordIndex(byteOffset)] = value;

    public void SetRendererSprite(short spriteId, int frameCount, bool backwards, short balloonSpriteId,
        short labelSpriteId, bool flag)
    {
        SelectorTypeKey = null;
        WriteWord(0x14, spriteId);
        WriteWord(0x18, 0);
        WriteWord(0x1c, frameCount);
        WriteWord(0x20, backwards ? frameCount - 1 : 0);
        WriteWord(0x24, backwards ? -1 : 1);
        WriteWord(0x2c, balloonSpriteId);
        WriteWord(0x30, labelSpriteId);
        WriteWord(0x38, flag ? 1 : 0);
    }

    public void SetSelectorSprite(int typeKey)
    {
        SelectorTypeKey = typeKey;
        WriteWord(0x14, -1);
        WriteWord(0x18, 0);
        WriteWord(0x1c, 1);
        WriteWord(0x20, 0);
        WriteWord(0x24, 1);
    }

    public void ActivateForTicks(int ticks)
    {
        WriteWord(0x28, 0);
        WriteWord(0x10, ticks);
    }

    public void ActivateForLoops(int loops)
    {
        WriteWord(0x28, 0);
        WriteWord(0x10, unchecked(loops * ReadWord(0x1c) * 12));
    }

    public static ObjectRuntimeSlot FromObjectSlot(Formats.Slot slot, int slotTableIndex)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Type != 1) throw new ArgumentException("Only type-1 SLOT entries initialize this runtime vector.", nameof(slot));
        if (slotTableIndex < 0) throw new ArgumentOutOfRangeException(nameof(slotTableIndex));
        var runtime = new ObjectRuntimeSlot();
        runtime.WriteWord(0x00, BitConverter.SingleToInt32Bits(slot.X));
        runtime.WriteWord(0x04, BitConverter.SingleToInt32Bits(slot.Y));
        runtime.WriteWord(0x08, BitConverter.SingleToInt32Bits(slot.Z));
        runtime.WriteWord(0x0c, slotTableIndex + 1);
        return runtime;
    }

    public bool Tick()
    {
        if (ReadWord(0x10) > 0) WriteWord(0x10, ReadWord(0x10) - 1);
        if (ReadWord(0x10) == 0) return false;

        var phase = ReadWord(0x28) + 1;
        WriteWord(0x28, phase);
        if (phase != 12) return false;

        var interval = ReadWord(0x1c);
        var progress = ReadWord(0x20) + ReadWord(0x24);
        while (progress < 0) progress += interval;
        WriteWord(0x20, progress % interval);
        WriteWord(0x28, 0);
        return true;
    }

    private static int WordIndex(int byteOffset)
    {
        if ((byteOffset & 3) != 0 || (uint)byteOffset >= 0x3c)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Runtime-slot offsets must be aligned dwords within 0x00..0x38.");
        return byteOffset / 4;
    }
}
public readonly record struct SpecialEffectRequest(int DurationMs, byte Byte2, byte Byte3, byte Byte4, byte Byte5);

public interface ISimulationAutonomyHost
{
    AutonomyLot AutonomyLotState();
    float ScoreAutonomy(XPerson person, XObject target, short actionIndex, string actionName, float score) => score;
}

public interface ISimulationCareerHost
{
    IReadOnlyList<int> CareerTable();
    bool CareerExists(short career);
    short? JobData(short career, short level, short field);
    bool TryGetCareerUniform(short career, short level, bool female, out string meshTemplate, out string skinTemplate)
    {
        meshTemplate = string.Empty;
        skinTemplate = string.Empty;
        return false;
    }
}

public interface ISimulationHost : ISimulationAutonomyHost, ISimulationCareerHost
{
    IReadOnlyList<short>? Constants(StackFrame frame, ushort id);
    TypeRecord? AlternativeObjectType(TypeRecord entry);
    short? Room(short room, short field);
    string? StringTableEntry(short table, int index);
    void BroadcastString(int message, string text);
    bool PlaySoundEvent(int bank, short eventId);
    bool PlayPositionalSound(short ownerId);

    void RefreshDisplay();
    void RefreshRoom(ushort room);

    bool BurnEnabled();
    TypeRecord? FindObjectType(int key);
    bool BurnFootprintClear(int tileX, int tileY, int height, short direction);
    bool BurnSpread(XObject me, ObjectPosition position, TypeRecord? fire, bool strictFlags);

    void SetObjectUiEffect(short id, bool flag);
    void ShowObjectUiEffect(short id, bool flag);
    void ShowEntryUiEffect(short id, bool flag);

    bool HasBalloonEntries(XObject owner);
    /// <summary>Claims the balloon slot for an object if inactive or if the requested priority is greater
    /// than or equal to the active balloon's priority.</summary>
    bool ClaimBalloon(XObject owner, int priority);
    void SetBalloonState(XObject owner, int value);
    void SetBalloonStateTimed(XObject owner, int count);
    void NotifyPersonObjectReference(XPerson recipient, XObject subject);
    void ShowBalloonForType(XObject owner, TypeRecord? alternative);
    int ListEntryValue(short index);
    void ShowBalloonForValue(XObject owner, int value);
    /// <summary>Displays balloon text looked up from the specified string table. Returns
    /// <see langword="false"/> and displays nothing if the text entry does not exist or is shorter than
    /// three characters.</summary>
    bool ShowBalloonText(byte table, int textId, int flags);
    short BalloonString(byte table, short index);
    int BalloonDuration(byte kind);
    void SetBalloonFields(XObject owner, int duration, int label, bool flag);
    void NotifyObjectTable(XObject owner);

    bool CanGrab(XObject item, ObjectPosition at, XObject? who, int flag);
    void Grab(XObject item, ObjectPosition at, XObject? who, int flag);
    bool CanDrop(XObject item, ObjectPosition at, XObject? who, int flag);
    void Drop(XObject item, ObjectPosition at, XObject? who, int flag);
    bool CanDropOnto(XObject target, ObjectPosition at, XObject? who, int flag);
    void DropOnto(XObject target, ObjectPosition at, XObject? who, int flag);

    int? DialogRecordState(XObject owner);
    void DismissDialogRecord(XObject owner);
    void DismissDialogRecords(XObject owner);
    void ShowDialog(XPerson owner, byte style);
    byte DialogGlobalFlagF8();
    SimulationDialogRecord? DialogRecordFor(SimulationWorld world, XObject owner) => world.DialogRecord(owner);
    SimulationDialogRecord? CreateDialogRecord(SimulationWorld world, XObject owner, XObject? icon, string title, string message,
        string? yes, string? no, string? cancel, byte kind, bool blocking, bool awaitsResponse)
        => world.CreateMessageDialog(owner, icon, title, message, yes, no, cancel, kind, blocking, awaitsResponse);
    void CompleteDialogRecord(SimulationWorld world, SimulationDialogRecord record) => world.CompleteDialogRecord(record);
    bool HasVisibleDialog(SimulationWorld world) => world.HasVisibleDialog;
    void SetLookTarget(XObject me, short target);
    void TurnTowards(XObject me, ObjectPosition? point);
    int TurnStatus(XObject me);
    bool TryStartTurn(XObject me);

    bool RoutingEnabled();
    int RoutingSlotCount(XObject stackObject);
    RoutingSlotRequest RoutingSlotAt(XObject stackObject, int index);
    int LotRoutingSlotCount(XObject me);
    RoutingSlotRequest LotRoutingSlotAt(XObject me, int index);
    /// <summary>Runs routing for an object. Passing a <see langword="null"/> target resumes the active
    /// route that the frame is waiting on, while a non-null target initiates a new route.</summary>
    PrimitiveResult Route(XObject me, StackFrame frame, RoutingSlotRequest? target);

    PrimitiveResult AnimateAvatar(XPerson me, StackFrame frame, ReadOnlySpan<byte> operand);
    void StopOwnerSounds(short ownerId);

    void ChangeActionString(XObject me, short stackObject, string? text);

    PrimitiveResult RunSnapshot(XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, bool snapTargetFlag,
        int directionMask, bool slotFlag, (int Dy, int Dx, int Ddir)? relative);

    void SetEffectWord(short index, short value);
    bool PlaySpecialEffect(XObject me, XObject stackObject, SpecialEffectRequest request);
    bool PushInteraction(XPerson person, XObject owner, byte interactionId, int priority, XObject? secondary, int flags);

    XObject? CreateCharacter(int mode);

    int ActivePersonIndex(int position);
    short? ActivePersonRecordId(int index);
    short? StoredSlotPersonId();
    InteractionBehaviorRuntimeContext? BeginInteractionBehaviorScope(InteractionBehaviorScope scope);
    InteractionBehaviorActionList? InteractionBehaviorActions(InteractionBehaviorScope scope, int listIndex);
    void EndInteractionBehaviorScope(InteractionBehaviorScope scope, bool commit);
    void FinalizeInteractionBehaviorTypeRecord(TypeRecord? typeRecord);
    void RefreshInteractionBehaviorTypeRecord(TypeRecord? typeRecord);
    void NotifyInteractionBehaviorGlobal(uint mask);
    XPerson? GenericSimsCall20FallbackPerson(int position);
    void GenericSimsCall20FinalizePerson(XPerson person);
    void RunGenericSimsCall01InteractionPhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer);
    void RunGenericSimsCall01RoutePhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer);
    void GenericSimsCall22Finalize(XObject resolved);
    short? ScopedNeighborPersonRecordId(int personRecordKey);
    void PersistNeighborhoodState();
    XObject? GenericSimsCall11CallbackTarget();
    bool PassesAutonomyCallbackGate(XObject target);
    void InvokeGatedObjectCallback(XObject target, GatedObjectCallbackRequest request);
    XObject? GenericSimsCall06CallbackTarget();
    void RunGenericSimsCall11Finalize(XObject resolved, short temporary0, short temporary1, short temporary2, short temporary3);
    bool GenericSimsCallRequestManagerAvailable();
    GenericSimsCallRequestCompletion ProcessGenericSimsCallRequest(GenericSimsCallPendingRequest request, XObject target);
    void ReleaseGenericSimsCall03PrimaryHandle(int handle);
    void ReleaseGenericSimsCall03SecondaryHandle(int handle);
    void RunGenericSimsCallInitialization(int mode);
    byte[]? GenericSimsCall26StringBytes(StackFrame frame, short selector, short oneBasedIndex);
    void SelectedPersonChanged(XObject person);
    sbyte TileTransformByte(int index);
    IReadOnlyList<int> GlobalIdTable();
    XObject? CreateObjectInstance(int guidKey);
    bool TileHasObject(int tileX, int tileY, int level, int? guidKey);
    short RoomAtTile(int tileX, int tileY, int level);
    bool FacingClear(int tileX, int tileY, int level, int facing);
    bool CanPlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method);
    void PlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method);
    void DiscardObject(XObject created);
    void RotateCreatedObject(XObject created);
    void InitCreatedPerson(XPerson created);
    void ArriveCreatedPerson(XPerson created);

    bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found);
    bool CanPlaceAt(XObject stackObject, ObjectPosition at);
    void PlaceAt(XObject stackObject, ObjectPosition at);
    void AlignMultitileParts(XObject stackObject);
    void RotateForPlacement(XObject stackObject, int steps);
    void FinishLocationSearch(XObject stackObject);
    void FinalizePlacement(XObject stackObject);
    XObject? StartQueuedAction(XPerson me, ActionQueueSlot action);

    (XObject? Holder, int Value) GrabHolder(XObject target);
    float SlotPosition(int slot);
    bool NextAvatarEvent(XPerson me, out int code);
    bool ReachAnimationDone(XPerson me);
    void FinishReach(XPerson me);
    bool StartReachAnimation(XPerson me, float position);
    bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag);
    void Place(XObject target, ObjectPosition at, XObject who, int flag);
}

public readonly record struct FoundAction(int Key, short Action)
{
    public static FoundAction FromTtab(Interaction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return new FoundAction(interaction.TtaIndex, unchecked((short)interaction.ActionTree));
    }
}

public readonly record struct StateClassEntry(int State, int Class);

public record struct PersonRoomMotiveEntry(float InteriorValue, bool IsOutside)
{
    public bool NeedsRefresh { get; set; }
    public float LightLevel6c { get; set; }
    public byte LightsEnabledFlag73 { get; set; }
    /// <summary>Indicates whether manual room lighting override is active, suppressing automatic Sim-driven
    /// light toggles.</summary>
    public byte LightOverrideFlag75 { get; set; }
    public int LightingObjectCount140 { get; set; }
    public int PersonObjectCount142 { get; set; }
    /// <summary>Count of eligible category 4 objects in the room, used to calculate the house panel bedroom
    /// count.</summary>
    public int HouseCategory4ObjectCount58 { get; set; }
    /// <summary>Count of eligible category 5 through 7 objects in the room, used to calculate the house
    /// panel bathroom count.</summary>
    public int HouseCategory5To7ObjectCount5c { get; set; }
}

public enum RoomRefreshResult
{
    ManagerUnavailable,
    RoomNotFound,
    Refreshed,
}

public sealed class GlobalPersonState
{
    public byte ByteE0 { get; set; }
    public byte ByteE1 { get; set; }
    public short LotUpkeepMatchWord22 { get; set; }
}

public sealed class GenericSimsCall03ManagerState
{
    public int PrimaryHandle { get; set; }
    public int SecondaryHandle { get; set; }
    public int? LinkedGlobalWord60 { get; set; }
}

public readonly record struct PersonRecordSelection(XObject? Person);

public sealed class SimulationNoticeListener(Func<int, int, bool> callback)
{
    public Func<int, int, bool> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));
    public SimulationNoticeListener? Next { get; set; }
}

public sealed record SimulationUiMessage(long Sequence, int Message, string Text);

public sealed partial class SimulationWorld
{
    public Func<XPerson, string, (float MarkerOneStride, float MarkerTwoStride)?>? LocomotionStrideResolver { get; set; }
    public Func<XPerson, XObject, HeadSeekGeometry?>? HeadSeekGeometryResolver { get; set; }
    public Func<XPerson, string, Vector3?>? AnchorBoneWorldResolver { get; set; }
    private static long s_uiMessageSequence;
    private readonly Dictionary<short, (StackFrame Frame, TreeStack? Stack)> _directPositionRoutes = [];
    private int _visitorAvatarAnimationKey;

    public int ActiveLotNumber { get; set; }

    public HeadlessSpecialEffectService HeadlessSpecialEffects { get; } = new();

    public List<SimulationUiMessage> UiMessages { get; } = [];

    public void RecordUiMessage(int message, string text)
    {
        UiMessages.Add(new SimulationUiMessage(Interlocked.Increment(ref s_uiMessageSequence), message, text));
        if (UiMessages.Count > 16) UiMessages.RemoveAt(0);
    }

    /// <summary>The global simulation random state advanced by a 32-bit linear congruential generator,
    /// maintained separately from the lot-local random state in the SIMI chunk.</summary>
    public uint RandomState { get; set; } = 1;

    /// <summary>The lot-local random state stored in the SIMI chunk, swapped in during the object manager
    /// update pass and saved back on completion.</summary>
    public uint ObjectManagerRandomState { get; set; } = 1;

    /// <summary>Retains the decoded SIMI chunk so saving can write back the updated lot-local random state
    /// while preserving other simulation globals.</summary>
    public SimulationState? SavedSimulationState { get; internal set; }

    /// <summary>The global sprite archive loaded from GameData/Sprites.iff, used for interaction dialog
    /// icons and thought balloons.</summary>
    public IffFile? GlobalSprites { get; set; }

    internal ObjectRegistry? ObjectRegistry { get; set; }

    public void ReplaceSavedBudgetDays(IReadOnlyList<BudgetDay> days)
    {
        if (SavedSimulationState is { } saved) SavedSimulationState = saved.WithBudgetDays(days);
    }

    /// <summary>Updates the furnishings and architecture valuation fields stored in the SIMI chunk.</summary>
    public void ReplaceSavedValuation(int fieldA0, int fieldA4)
    {
        if (SavedSimulationState is { } saved) SavedSimulationState = saved.WithValuation(fieldA0, fieldA4);
    }

    public void LoadNeighborhoodData(NeighborhoodData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.ToVariables().CopyTo(NeighborhoodData, 0);
        NeighborInventories = data.Inventories;
        foreach (var (neighborId, inventory) in _neighborInventories)
        {
            while (inventory.Entries.Count > 0) inventory.Adjust(0, -1);
            if (NeighborInventories.TryGetValue(neighborId, out var items))
                foreach (var item in items)
                    inventory.Add(item.Type, unchecked((int)item.GUID), unchecked((short)item.Count));
        }
    }

    private readonly Dictionary<short, AvatarInventory> _neighborInventories = [];

    public event Action<short, AvatarInventory>? NeighborInventoryChanged;

    internal void RaiseNeighborInventoryChanged(short neighborId, AvatarInventory inventory) =>
        NeighborInventoryChanged?.Invoke(neighborId, inventory);

    public AvatarInventory NeighborInventory(short neighborId)
    {
        if (_neighborInventories.TryGetValue(neighborId, out var inventory)) return inventory;
        inventory = new AvatarInventory();
        if (NeighborInventories.TryGetValue(neighborId, out var items))
            foreach (var item in items) inventory.Add(item.Type, unchecked((int)item.GUID), unchecked((short)item.Count));
        return _neighborInventories[neighborId] = inventory;
    }

    public IReadOnlyDictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>> NeighborInventories { get; private set; } =
        new Dictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>>();

    public Dictionary<uint, short[]> SharedTypeAttributes { get; } = [];

    /// <summary>Returns the shared type-attribute array for <paramref name="guid"/>, ensuring all instances
    /// of the same GUID share the same backing array.</summary>
    public short[] GetOrCreateTypeAttributes(uint guid, int minimumLength = 8)
    {
        var requiredLength = Math.Max(0, minimumLength);
        if (SharedTypeAttributes.TryGetValue(guid, out var existing))
        {
            if (existing.Length >= requiredLength) return existing;
            var expanded = new short[requiredLength];
            existing.CopyTo(expanded, 0);
            SharedTypeAttributes[guid] = expanded;
            foreach (var obj in WalkOrder)
                if (ObjectGuid(obj) == guid) obj.TypeAttributes = expanded;
            return expanded;
        }

        var created = new short[requiredLength];
        SharedTypeAttributes[guid] = created;
        return created;
    }

    private static uint ObjectGuid(XObject item) =>
        item.TypeRecord is { Key: not 0 } record
            ? unchecked((uint)record.Key)
            : unchecked((uint)(ushort)item.Definition[(int)ObjdField.Guid1] |
                        ((uint)(ushort)item.Definition[(int)ObjdField.Guid2] << 16));

    public LotObjects? SavedLotObjects { get; internal set; }

    public IReadOnlyDictionary<short, (ObjectClass Class, bool Placeholder)> SavedObjectClasses { get; internal set; } =
        new Dictionary<short, (ObjectClass, bool)>();

    /// <summary>The ObjM chunk version restored from the lot, which dictates the layout of the trailing
    /// manager flags.</summary>
    public int SavedObjectManagerVersion { get; internal set; }

    /// <summary>Writes each restored object's clock-manager flags into its ObjM chunk block. When saving,
    /// this must run before <see cref="WriteSavedLotObjectsTo"/> so full live state is preserved.</summary>
    public (IffFile Lot, IReadOnlyList<short> Written) WriteSavedObjectManagerStateTo(IffFile lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var source = SavedLotObjects ?? throw new InvalidDataException($"{lot.Source}: the lot has no parsed ObjM state.");
        var chunk = lot.OfType(ResourceType.Parse("ObjM")).FirstOrDefault()
            ?? throw new InvalidDataException($"{lot.Source}: no ObjM chunk was found.");
        var body = chunk.Data.Span;
        var swap = body.Length > 0 && body[0] != 0;
        var tag = System.Text.Encoding.ASCII.GetString(body[8..12]);
        var overrides = new Dictionary<short, ObjectState>();
        var written = new List<short>();
        foreach (var saved in source.Objects)
        {
            if (saved.Decoded is not { ManagerFlags: not null } state) continue;
            var instance = ObjectById(saved.ObjectId);
            if (instance is null || instance.ClockManagerIndex < 1 || instance.ClockManagerIndex > ClockManager.Entries.Count) continue;
            var flags = ClockManager.Entries[instance.ClockManagerIndex - 1].Flags;
            if (flags == state.ManagerFlags) continue;
            overrides[saved.ObjectId] = state with { ManagerFlags = unchecked((int)flags) };
            written.Add(saved.ObjectId);
        }
        if (written.Count == 0) return (lot, written);
        var reencoded = LotObjects.Write(source, swap, tag, typeIndex => SavedObjectClasses[typeIndex], overrides);
        return (lot.WithChunkDataResized(chunk.Type, chunk.Id, reencoded), written);
    }

    /// <summary>Serializes the live state of all lot objects into the ObjM chunk, including transforms,
    /// attributes, slots, stack frames, and relationships.</summary>
    public (IffFile Lot, int Objects) WriteSavedLotObjectsTo(IffFile lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var source = SavedLotObjects ?? throw new InvalidDataException($"{lot.Source}: the lot has no parsed ObjM state.");
        var chunk = lot.OfType(ResourceType.Parse("ObjM")).FirstOrDefault()
            ?? throw new InvalidDataException($"{lot.Source}: no ObjM chunk was found.");
        var body = chunk.Data.Span;
        var swap = body.Length > 0 && body[0] != 0;
        var tag = System.Text.Encoding.ASCII.GetString(body[8..12]);
        var overrides = new Dictionary<short, ObjectState>();
        var records = BuildLiveSavedObjectList(source);
        foreach (var saved in records)
        {
            if (saved.Decoded is not { } state) continue;
            var live = ObjectById(saved.ObjectId);
            if (live is null) continue;
            var values = state.Values0x108.ToArray();
            var bounds = live.FootprintBounds;
            values[0] = bounds.MinY;
            values[1] = bounds.MinX;
            values[2] = bounds.MaxY;
            values[3] = bounds.MaxX;
            // Object positions are stored in sixteenths of a tile (X followed by Y).
            values[4] = live.Position.X;
            values[5] = live.Position.Y;
            var flags = state.ManagerFlags is null || live.ClockManagerIndex < 1 ||
                        live.ClockManagerIndex > ClockManager.Entries.Count
                ? state.ManagerFlags
                : unchecked((int)ClockManager.Entries[live.ClockManagerIndex - 1].Flags);
            var personState = state.Person;
            if (live is XPerson person && personState is not null)
            {
                var personDataLength = source.Version < 0x47 ? 0x50 : 0x100;
                personState = personState with
                {
                    Character = person.SavedCharacter ?? personState.Character,
                    Values0x78C = person.Motives.ToArray(),
                    Values0x7CC = person.SavedPersonValues7CC.ToArray(),
                    PersonData = person.PersonData.Take(personDataLength).ToArray(),
                    Count0xAF0 = person.SavedPersonActionStorageCount,
                    First = (person.CurrentActionRecord ?? ActionQueueSlot.EmptyRecord()).ToSaved(),
                    Second = (person.PreviousActionRecord ?? ActionQueueSlot.EmptyRecord()).ToSaved(),
                    Actions = person.WriteActionQueue(),
                };
            }
            var isPortal = SavedObjectClasses.TryGetValue(saved.TypeIndex, out var savedClass) &&
                           savedClass.Class == Formats.ObjectClass.Portal;
            overrides[saved.ObjectId] = state with
            {
                Person = personState,
                MultiTile = (live.SavedMultiTileState ?? state.MultiTile) is { } multiTile
                    ? multiTile with
                    {
                        LeadObject = live.MultiTileMasterId,
                        PortalValues = isPortal ? multiTile.PortalValues ?? [] : multiTile.PortalValues,
                    }
                    : null,
                Relationships = live.Relationships.WriteSavedEntries(state.Relationships),
                Values0x108 = values,
                Value0x110 = live.Position.Height,
                Attributes = live.Attributes.ToArray(),
                Temps = live.Temporaries.ToArray(),
                Data = live.Data.ToArray(),
                Values0x34 = live.DynamicSpriteFlags.ToArray(),
                Slots = live.Slots.ToArray(),
                Stack = live.SavedStack.ToArray(),
                StackSize = live.SavedStackSize,
                ManagerFlags = flags,
            };
        }
        var reencoded = LotObjects.Write(source, swap, tag, typeIndex => SavedObjectClasses[typeIndex], overrides, records);
        var updated = lot.WithChunkDataResized(chunk.Type, chunk.Id, reencoded);
        var typesChunk = updated.OfType(ResourceType.Parse("objt")).FirstOrDefault()
            ?? throw new InvalidDataException($"{lot.Source}: no objt chunk was found.");
        var types = ObjectTypeTable.Read(updated, typesChunk);
        if (!types.Entries.SequenceEqual(SavedObjectTypes.Values))
        {
            var typesBody = typesChunk.Data.Span;
            var bytes = types.Write(typesBody[0] != 0, System.Text.Encoding.ASCII.GetString(typesBody[8..12]), SavedObjectTypes.Values.ToArray());
            updated = updated.WithChunkDataResized(typesChunk.Type, typesChunk.Id, bytes);
        }
        return (updated, overrides.Count);
    }

    public byte[]? WriteSavedSimulationStateChunk() => SavedSimulationState?.WriteChunkData(ObjectManagerRandomState, Globals);

    /// <summary>Updates the existing SIMI chunk with current simulation state. Same-length replacements
    /// preserve resource map offsets, while resized streams update chunk and resource map offsets.</summary>
    public IffFile WriteSavedSimulationStateTo(IffFile lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var chunk = lot.OfType(ResourceType.Parse("SIMI")).FirstOrDefault()
            ?? throw new InvalidDataException($"{lot.Source}: no SIMI chunk to save.");
        var data = WriteSavedSimulationStateChunk()
            ?? throw new InvalidOperationException("This simulation world has no retained SIMI state.");
        return data.Length == chunk.Data.Length
            ? lot.WithChunkData(chunk.Type, chunk.Id, data)
            : lot.WithChunkDataResized(chunk.Type, chunk.Id, data);
    }

    public byte[] WriteSavedTypeAttributesChunk(int version = 0x49, bool coded = true, bool swap = false) =>
        TypeAttributes.Write(SharedTypeAttributes, version, coded, swap);

    public IffFile WriteSavedTypeAttributesTo(IffFile file, int version = 0x49)
    {
        ArgumentNullException.ThrowIfNull(file);
        var type = ResourceType.Parse("TATT");
        var existing = file.Find(type, 1) ?? file.OfType(type).FirstOrDefault();
        var swap = existing is { Data.Length: > 0 } && existing.Data.Span[0] != 0;
        var coded = existing is not { Data.Length: > 12 } || existing.Data.Span[12] != 0;
        var payload = WriteSavedTypeAttributesChunk(version, coded, swap);
        return existing is not null
            ? file.WithChunkDataResized(existing.Type, existing.Id, payload)
            : file;
    }

    /// <summary>Serializes the neighborhood NGBH chunk, merging unmodified loaded records with any mutated
    /// neighbor inventories.</summary>
    public byte[] WriteSavedNeighborhoodDataChunk(int version = 0x49, bool swap = false)
    {
        var mergedInventories = new Dictionary<short, IReadOnlyList<Formats.NeighborhoodData.InventoryItem>>();
        foreach (var (neighborId, items) in NeighborInventories)
            mergedInventories[neighborId] = items;
        foreach (var (neighborId, liveInventory) in _neighborInventories)
        {
            mergedInventories[neighborId] = liveInventory.Entries
                .Select(entry => new Formats.NeighborhoodData.InventoryItem(entry.Key1, unchecked((uint)entry.Key2), unchecked((ushort)entry.Count)))
                .ToArray();
        }
        return Formats.NeighborhoodData.Write(NeighborhoodData, mergedInventories, version, swap);
    }

    /// <summary>Advances the simulation random state and returns the upper 32 bits of the 64-bit linear
    /// congruential step shifted right 16.</summary>
    public uint NextRandom()
    {
        var previous = RandomState;
        RandomState = unchecked(previous * 0x41c64e6d + 0x3039);
        return (uint)(((ulong)previous * 0x41c64e6d + 0x3039) >> 16);
    }
    private readonly List<XObject?> _objectTableEntries = [];
    private readonly HashSet<short> _activeAssociatedPlacementCallbacks = [];
    private int _actionQueueSequence;

    public void RestoreActionSequence(int sequence) => _actionQueueSequence = Math.Max(_actionQueueSequence, sequence);
    private static readonly byte[] PersonRecordFieldsVersion10 =
    [
        2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 16, 20, 15, 26, 17, 18, 46, 47, 48, 49, 50, 51, 52, 53,
        13, 14, 58, 60, 61, 56, 57, 63, 65, 67, 68, 69, 74, 54, 55, 75, 76, 80, 81, 82, 83, 84, 85, 86,
    ];

    public const int GlobalCount = 0x40;

    public SimulationNoticeListener? NoticeListeners { get; private set; }

    public void AddNoticeListener(SimulationNoticeListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (NoticeListeners is null)
        {
            NoticeListeners = listener;
            return;
        }

        var tail = NoticeListeners;
        var visited = new HashSet<SimulationNoticeListener>();
        while (tail.Next is not null)
        {
            if (!visited.Add(tail)) throw new InvalidOperationException("The simulation notice-listener chain contains a cycle.");
            tail = tail.Next;
        }
        if (!visited.Add(tail)) throw new InvalidOperationException("The simulation notice-listener chain contains a cycle.");
        tail.Next = listener;
    }

    /// <summary>Invokes listeners in sequence until one handles the notice, capturing the next link before
    /// invocation so callbacks can safely detach.</summary>
    public bool DispatchNotice(int message, int value)
    {
        var handled = false;
        var listener = NoticeListeners;
        while (listener is not null)
        {
            var next = listener.Next;
            if (listener.Callback(message, value)) handled = true;
            listener = next;
        }
        // Room key 0xFFFB (-5) is the off-lot sentinel and has no room record.
        if (message == 0xf3 && value is not (-5 or 0xfffb))
            RefreshRoomRecord(unchecked((ushort)value));
        return handled;
    }

    public SimulationClock Clock { get; } = new();
    private SimulationDate? _explicitCurrentDate;
    private bool _currentDateUsesGlobals;
    public SimulationDate? CurrentDate
    {
        get => _currentDateUsesGlobals
            ? new SimulationDate(this[SimulationGlobal.Year], this[SimulationGlobal.Month], this[SimulationGlobal.DayOfMonth])
            : _explicitCurrentDate;
        set
        {
            _explicitCurrentDate = value;
            _currentDateUsesGlobals = false;
        }
    }

    internal void UseSimulationGlobalsForCurrentDate() => _currentDateUsesGlobals = true;
    public SimulationClockManager ClockManager { get; } = new();
    /// <summary>The maximum instruction step limit for simulation trees: 10,000 base steps plus 15 times
    /// the object count, plus 15 times the square of eligible character neighbors.</summary>
    public int TreeStepLimit
    {
        get
        {
            var eligibleNeighbors = NeighborWalkOrder.Count(neighbor =>
                neighbor?.FriendEligibilityObjectFlagsB4 is { } flags && (flags & 0xC) == 4);
            return checked(10_000 + ClockManager.Entries.Count * 15 + eligibleNeighbors * eligibleNeighbors * 15);
        }
    }
    /// <summary>The ordered spell item IDs from MagicMasterSpells.iff STR# 15, excluding the reserved first
    /// entry.</summary>
    public IReadOnlyList<short> MagicMasterSpellItemIds { get; set; } = [];
    /// <summary>Motive decay tuning parameters loaded from Global.iff FCNS resources 1, 5, and 6.</summary>
    public PersonMotiveDecayTuning? PersonMotiveDecayTuning { get; set; }
    /// <summary>Pending notices generated when higher-priority interactions displace existing actions from
    /// an action queue.</summary>
    public List<ActionQueueRemovalNotice> ActionQueueRemovalNotices { get; } = [];
    public List<QueuedInteractionStartTrace> QueuedInteractionStartTrace { get; } = [];
    public bool ActionStringDisplayActive { get; set; }
    public InteractionMenuContext? InteractionMenu { get; internal set; }
    /// <summary>Indicates whether an immediate interaction is executing, preventing nested Run Immediately
    /// actions.</summary>
    internal bool ImmediateInteractionRunning { get; set; }
    internal ActionQueueSlot? ActiveImmediateAction { get; set; }
    internal XPerson? ActiveImmediatePerson { get; set; }
    /// <summary>Indicates that an interaction availability test is running, during which movement
    /// primitives must not initiate routing.</summary>
    public bool EvaluatingInteractionTest { get; set; }
    /// <summary>Determines whether the Change Action String primitive can update the displayed action
    /// string, which is suppressed during nested tree execution.</summary>
    public bool ActionStringRecordWriteEnabled { get; set; } = true;

    public Dictionary<short, XObject> Objects { get; } = [];
    public Dictionary<ushort, PersonRoomMotiveEntry> PersonRoomMotiveEntries { get; } = [];
    public Dictionary<int, SimsEngine.World.RoomMap> LotRoomMaps { get; } = [];
    /// <summary>Tile flags loaded from chunk Arry 8, where bit 0x20 marks tiles that lie outside the
    /// playable lot boundary.</summary>
    public SimsEngine.World.LotTiles? LotTileFlags { get; set; }
    /// <summary>The terrain elevation grid loaded from chunk Arry 0, used to validate slope restrictions
    /// for object placement.</summary>
    public SimsEngine.World.LotTerrain? LotTerrain { get; set; }
    /// <summary>Per-level placement grids for build mode type 6 objects, tracking placement footprints via
    /// bit 3 independently of terrain pool bytes.</summary>
    public Dictionary<int, byte[]> BuildModeType6PlacementGrids { get; } = [];
    public Dictionary<int, byte[]> PlacementEligibilityGrids { get; } = [];
    public Dictionary<int, byte[]> LotRoomLightMaps { get; } = [];
    public SimsEngine.World.RoomLightCoordinateGrid RoomLightCandidates { get; } = new();
    public bool FindLocationWaterOverride { get; set; }
    /// <summary>Enables move_objects mode, bypassing placement water checks and allowing picking up or
    /// selling restricted objects.</summary>
    public bool MoveObjects
    {
        get => FindLocationWaterOverride;
        set => FindLocationWaterOverride = value;
    }
    public bool RoomManagerRecordsLoaded { get; set; }
    public float OutsidePersonRoomMotiveValue { get; set; }
    public float RoomLightRadiusScalar { get; set; } = SimsEngine.World.RoomLightCoordinateGrid.SimsExecutableInitialLightingScalar;
    public float RoomDaylightScalar { get; set; } = SimsEngine.World.RoomRuntimeScalarDefaults.SimsExecutableInitialDaylightScalar;
    public float RoomDaylightAtCurrentClockPhase =>
        SimsEngine.World.RoomRuntimeScalarDefaults.DaylightForTimePhase(Clock.TimeOfDay, RoomDaylightScalar);
    public uint RoomManagerRefreshFlags { get; private set; }
    private readonly HashSet<int> _portalWallRefreshRequestedLevels = [];
    private readonly HashSet<int> _portalGeometryRefreshRequestedLevels = [];
    public IReadOnlySet<int> PortalWallRefreshRequestedChannels => _portalWallRefreshRequestedLevels;
    public IReadOnlySet<int> PortalGeometryRefreshRequestedChannels => _portalGeometryRefreshRequestedLevels;

    /// <summary>Consumes pending wall refresh requests, returning the floor levels that require repainting
    /// after portal or wall geometry changes.</summary>
    public IReadOnlyList<int> ConsumePortalWallRefreshRequests()
    {
        if (_portalWallRefreshRequestedLevels.Count == 0) return Array.Empty<int>();
        var levels = _portalWallRefreshRequestedLevels.Order().ToArray();
        _portalWallRefreshRequestedLevels.Clear();
        return levels;
    }

    public IReadOnlyList<int> ConsumePortalGeometryRefreshRequests()
    {
        if (_portalGeometryRefreshRequestedLevels.Count == 0) return Array.Empty<int>();
        var levels = _portalGeometryRefreshRequestedLevels.Order().ToArray();
        _portalGeometryRefreshRequestedLevels.Clear();
        return levels;
    }

    public event Action<PortalWallRefreshCoordinate>? PortalWallRefreshRequested;

    private void MarkPortalGeometryRefreshRequested(int height)
    {
        if (height == 0)
        {
            _portalGeometryRefreshRequestedLevels.Add(1);
            _portalGeometryRefreshRequestedLevels.Add(2);
        }
        else _portalGeometryRefreshRequestedLevels.Add(height);
    }

    private void MarkPortalWallRefreshRequested(int height, int tileX, int tileY)
    {
        if (height == 0)
        {
            _portalWallRefreshRequestedLevels.Add(1);
            _portalWallRefreshRequestedLevels.Add(2);
        }
        else _portalWallRefreshRequestedLevels.Add(height);
        var coordinate = MapPortalWallRefreshCoordinate(tileX, tileY, height);
        PortalWallRefreshRequested?.Invoke(coordinate);
    }

    private static PortalWallRefreshCoordinate MapPortalWallRefreshCoordinate(int tileX, int tileY, int height)
    {
        var (first, second) = height switch
        {
            1 => (63 - tileX, tileY),
            2 => (63 - tileY, 63 - tileX),
            3 => (tileX, 63 - tileY),
            _ => (tileY, tileX),
        };
        return new PortalWallRefreshCoordinate(unchecked((byte)first), unchecked((byte)second), unchecked((byte)height));
    }

    public void LoadRoomMaps(IReadOnlyDictionary<int, SimsEngine.World.RoomMap> roomMaps)
    {
        ArgumentNullException.ThrowIfNull(roomMaps);
        LotRoomMaps.Clear();
        LotRoomLightMaps.Clear();
        PersonRoomMotiveEntries.Clear();

        foreach (var (level, roomMap) in roomMaps.OrderBy(pair => pair.Key))
        {
            LotRoomMaps.Add(level, roomMap);
            LotRoomLightMaps.Add(level, new byte[SimsEngine.World.RoomLightCoordinateGrid.GridSize * SimsEngine.World.RoomLightCoordinateGrid.GridSize]);
            foreach (var roomId in roomMap.RoomIds)
                PersonRoomMotiveEntries.Add(roomId,
                    new PersonRoomMotiveEntry(0f, IsOutside: roomMap.IsOutside(roomId))
                    {
                        NeedsRefresh = true,
                        LightOverrideFlag75 = roomMap.LightControlFlag75(roomId) ? (byte)1 : (byte)0,
                    });
        }

        RoomManagerRecordsLoaded = true;
        ReconcileObjectRoomsAfterMapLoad();
        RefreshDirtyRoomRecords();
    }

    public void ReloadRoomMaps(IReadOnlyDictionary<int, SimsEngine.World.RoomMap> roomMaps) =>
        LoadRoomMaps(roomMaps);

    private void ReconcileObjectRoomsAfterMapLoad()
    {
        foreach (var item in WalkOrder)
        {
            var position = item.Position;
            if (position.Height == 0 && (position.X > 0 || position.Y > 0))
            {
                position = position with { Height = 1 };
                item.Position = position;
            }

            short roomId = -5;
            var x = position.X >> 4;
            var y = position.Y >> 4;
            if (x > 0 && y > 0 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1 &&
                LotRoomMaps.TryGetValue(position.Height, out var roomMap) && x < roomMap.Width && y < roomMap.Height)
            {
                var room = roomMap.RoomAtPosition(position.X, position.Y);
                if (room != SimsEngine.World.RoomMap.NoRoom) roomId = unchecked((short)room);
            }

            item.RoomWord84 = roomId;
            if (item is XPerson person)
                person.LastObservedRoomIdAFC = unchecked((ushort)roomId);
        }
    }

    public bool HasRoomRecord(short roomId) =>
        RoomManagerRecordsLoaded && PersonRoomMotiveEntries.ContainsKey(unchecked((ushort)roomId));

    public bool RoomIsOutside(short roomId) =>
        PersonRoomMotiveEntries.TryGetValue(unchecked((ushort)roomId), out var room) && room.IsOutside;

    /// <summary>Marks a room record dirty and updates the manager refresh flags using the room group
    /// encoded in bits 10–11 of the room key.</summary>
    public RoomRefreshResult RefreshRoomRecord(ushort roomId)
    {
        if (!RoomManagerRecordsLoaded) return RoomRefreshResult.ManagerUnavailable;
        if (!PersonRoomMotiveEntries.TryGetValue(roomId, out var room)) return RoomRefreshResult.RoomNotFound;
        room.NeedsRefresh = true;
        PersonRoomMotiveEntries[roomId] = room;
        // Room key bits 10–11 (mask 0x0C00) encode the room group.
        switch ((roomId & 0x0c00) >> 10)
        {
            case 0: RoomManagerRefreshFlags |= 1; break;
            case 1: RoomManagerRefreshFlags |= 2; break;
        }
        return RoomRefreshResult.Refreshed;
    }

    /// <summary>Recalculates dirty room records, where group 0 refreshes all rooms and groups 1 and 2
    /// filter by room group.</summary>
    public int RefreshDirtyRoomRecords(int roomGroup = 0)
    {
        if (!RoomManagerRecordsLoaded) return 0;
        if (roomGroup is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(roomGroup));
        var refreshed = 0;
        foreach (var (level, roomMap) in LotRoomMaps.OrderBy(pair => pair.Key))
        foreach (var roomId in roomMap.RoomIds)
        {
            if (!PersonRoomMotiveEntries.TryGetValue(roomId, out var room) || !room.NeedsRefresh) continue;
            var roomKeyGroup = (roomId & 0x0c00) >> 10;
            if (roomGroup != 0 && roomKeyGroup + 1 != roomGroup) continue;

            var previousDisplayedLight = DisplayedRoomLight(room);
            var statistics = new SimsEngine.World.RoomRuntimeStatistics();
            var lightGridContext = new SimsEngine.World.RoomLightGridCallbackContext(
                RoomLightCandidates,
                RoomLightRadiusScalar,
                LevelAt: item => item[ObjectData.LightSource],
                RoomAt: (first, second, candidateLevel) =>
                    LotRoomMaps.TryGetValue(candidateLevel, out var candidateMap) &&
                    second >= 0 && first >= 0 && second < candidateMap.Width && first < candidateMap.Height &&
                    candidateMap.RoomAt(second, first) != SimsEngine.World.RoomMap.NoRoom
                        ? unchecked((short)candidateMap.RoomAt(second, first))
                        : (short)-5,
                AdjacentRooms: (first, second, candidateLevel) =>
                {
                    if (!LotRoomMaps.TryGetValue(candidateLevel, out var candidateMap)) return (null, null);
                    var adjacent = candidateMap.AdjacentRoomsAtTile(second, first);
                    return adjacent is { } rooms
                        ? (rooms.First == SimsEngine.World.RoomMap.NoRoom ? null : unchecked((short?)rooms.First),
                            rooms.Second == SimsEngine.World.RoomMap.NoRoom ? null : unchecked((short?)rooms.Second))
                        : (null, null);
                },
                BridgeUnassignedRoomCells: true);

            foreach (var (tileX, tileY) in roomMap.TilesInRoom(roomId))
            {
                var tileInput = roomMap.StatisticsInputAt(tileX, tileY);
                if (tileInput is { } input)
                    statistics.Add(SimsEngine.World.RoomTileStatistics.AccumulateKnown(input, room.IsOutside));
                statistics.Add(SimsEngine.World.RoomObjectStatistics.AccumulateTile(
                    this, tileX, tileY, level, roomId, room.IsOutside,
                    item => IsInsideLotForRoomLightCallback(item), lightGridContext));
            }

            room.LightingObjectCount140 = statistics.LightingObjects140;
            room.PersonObjectCount142 = statistics.PersonObjects142;
            room.HouseCategory4ObjectCount58 = statistics.Category4Objects2C;
            room.HouseCategory5To7ObjectCount5c = statistics.Category5To7Objects2E;
            var scoreInput = statistics.ToScoreInput(
                RoomDaylightAtCurrentClockPhase,
                room.IsOutside,
                room.LightOverrideFlag75,
                room.LightsEnabledFlag73,
                SimsEngine.World.RoomScoreTuning.DaylightCeiling);
            var result = RoomScoreTuning.Calculate(scoreInput);
            room.InteriorValue = result.RoomScore;
            room.LightLevel6c = result.Light;
            room.NeedsRefresh = false;
            PersonRoomMotiveEntries[roomId] = room;
            WriteRoomLightMap(roomMap, roomId, result.TileLight);
            if (previousDisplayedLight != DisplayedRoomLight(room)) DispatchNotice(0xf1, roomId);
            refreshed++;
        }

        var outsideScoreSum = PersonRoomMotiveEntries.Values
            .Where(room => room.IsOutside)
            .Sum(room => room.InteriorValue);
        var phase = Clock.TimeOfDay;
        var phaseBonus = phase switch
        {
            1 or 3 => RoomScoreTuning.OutdoorDawnDuskBonus,
            2 => RoomScoreTuning.OutdoorNighttimeBonus,
            _ => RoomScoreTuning.OutdoorDaylightBonus,
        };
        OutsidePersonRoomMotiveValue = Math.Clamp(
            outsideScoreSum / RoomScoreTuning.OutdoorObjectDivisor + phaseBonus, -100f, 100f);

        var totalIndoorTiles = 0;
        foreach (var (_, roomMap) in LotRoomMaps)
        foreach (var roomId in roomMap.RoomIds)
            if (PersonRoomMotiveEntries.TryGetValue(roomId, out var entry) && !entry.IsOutside)
                totalIndoorTiles += roomMap.RoomHalfArea(roomId, entry.IsOutside);
        var indoorTilesWord = unchecked((short)Math.Clamp(totalIndoorTiles, short.MinValue, short.MaxValue));
        this[SimulationGlobal.IndoorTiles] = indoorTilesWord;
        GlobalObjectTableEffectWords[0x1b] = indoorTilesWord;

        RoomManagerRefreshFlags = roomGroup switch
        {
            0 => 0,
            1 => RoomManagerRefreshFlags & ~1u,
            2 => RoomManagerRefreshFlags & ~2u,
            _ => RoomManagerRefreshFlags,
        };
        return refreshed;
    }

    public static float DisplayedRoomLight(PersonRoomMotiveEntry room)
    {
        var ceiling = SimsEngine.World.RoomScoreTuning.DaylightCeiling;
        return room.LightOverrideFlag75 == 0 &&
               (room.LightsEnabledFlag73 == 0 || room.LightingObjectCount140 != 0 ||
                room.PersonObjectCount142 < 1 || ceiling <= room.LightLevel6c)
            ? room.LightLevel6c
            : ceiling;
    }

    private static bool IsInsideLotForRoomLightCallback(XObject item)
    {
        var x = item.Position.X >> 4;
        var y = item.Position.Y >> 4;
        return x > 0 && y > 0 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1;
    }

    private void WriteRoomLightMap(SimsEngine.World.RoomMap roomMap, ushort roomId, float light)
    {
        if (!LotRoomLightMaps.TryGetValue(roomMap.Level, out var grid)) return;
        var lightByte = unchecked((byte)MathF.Round(light, MidpointRounding.ToEven));
        foreach (var (x, y) in roomMap.TilesInRoom(roomId))
            grid[x * SimsEngine.World.RoomLightCoordinateGrid.GridSize + y] = lightByte;
    }

    public void SetObjectPosition(XObject item, ObjectPosition position)
        => SetObjectPosition(item, position, new HashSet<XObject>(ReferenceEqualityComparer.Instance));

    private void SetObjectPosition(XObject item, ObjectPosition position, HashSet<XObject> visited)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!visited.Add(item)) return;
        item.Position = position;
        if (item is XPerson movingPerson && movingPerson.NativeRoute is null)
        {
            movingPerson.AnimatorRootX = position.X;
            movingPerson.AnimatorRootY = position.Y;
            movingPerson.AnimatorRootDirection = movingPerson[ObjectData.Direction];
        }
        // Refreshes footprint bounds so the object no longer collides at its old tile.
        if (item is XPerson person && person.TypeRecord is not null)
            person.RefreshFootprintBounds();
        else if (item.TypeRecord is not null)
        {
            item.FootprintBounds = PlacementFootprintBounds(item, position);
            item.FootprintBoundsDirection = item[ObjectData.Direction];
        }
        var previousRoom = item.RoomWord84;
        short currentRoom = -5;
        var x = position.X >> 4;
        var y = position.Y >> 4;
        if (x > 0 && y > 0 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1 &&
            LotRoomMaps.TryGetValue(position.Height, out var roomMap) && x < roomMap.Width && y < roomMap.Height)
        {
            var room = roomMap.RoomAtPosition(position.X, position.Y);
            if (room != SimsEngine.World.RoomMap.NoRoom) currentRoom = unchecked((short)room);
        }

        item.RoomWord84 = currentRoom;
        if (previousRoom != currentRoom && item.RoomChangeNoticeGateWord58 != 0)
        {
            DispatchNotice(0xf3, previousRoom);
            DispatchNotice(0xf3, currentRoom);
        }

        foreach (var related in EnumerateContainedSlotChildren(item))
            SetObjectPosition(related, position, visited);
    }
    /// <summary>Maps code-owner slots loaded from OBJT TypeIndex entries to their behavior tree scopes.</summary>
    public Dictionary<short, ITreeTable?> CodeOwnerScopes { get; } = [];
    public Dictionary<short, uint> CodeOwnerTypeGuids { get; } = [];
    public Dictionary<short, TreeStack> SavedExecutionStacks { get; } = [];
    public InteractionLifecycle InteractionLifecycle { get; } = new();
    public List<FoundActionPushFailure> FoundActionPushFailures { get; } = [];
    public List<ObjectCreationDiagnostic> ObjectCreationTrace { get; } = [];
    public List<ObjectRemovalDiagnostic> ObjectRemovalTrace { get; } = [];
    /// <summary>The ordered list of lot objects traversed by object-iteration primitives such as Set to
    /// Next.</summary>
    public List<XObject> WalkOrder { get; } = [];
    public IReadOnlyList<XObject> ObjectsOfType(int guidKey) =>
        WalkOrder.Where(item => item.TypeRecord?.Key == guidKey).ToArray();
    public List<XObject> InteractionLookupOrder { get; } = [];
    public bool InteractionRefreshManagerEnabled { get; set; } = true;
    public int InteractionRefreshTypeKey { get; set; }
    public XObject? ActiveInteractionRefreshTarget { get; set; }
    public bool InteractionContextsInvalidated { get; private set; }
    public List<XObject> InteractionRefreshPendingObjects { get; } = [];
    public List<XObject> InteractionRefreshDeferredObjects { get; } = [];
    public int LotTransformParameter { get; private set; }
    public LotCoordinateBounds LotBounds { get; private set; } = LotGeometry.TransformLotBounds(64, 0);
    /// <summary>Sets the lot rotation orientation masked to four 90-degree steps and recomputes the
    /// transformed lot bounds.</summary>
    public void SetLotOrientation(int orientation)
    {
        LotTransformParameter = LotGeometry.NormalizeOrientation(orientation);
        LotBounds = LotGeometry.TransformLotBounds(64, LotTransformParameter);
    }
    public Dictionary<short, NeighborState> Neighbors { get; } = [];
    internal Neighbors? SavedNeighbors { get; set; }
    /// <summary>Indicates whether the NBRS chunk neighbor roster is intact; when false, ID allocation must
    /// not reuse subsequent slots to avoid collisions.</summary>
    internal bool NeighborRosterComplete { get; set; } = true;
    internal int CompleteNeighborSlotPrefix { get; set; }
    public bool RelationshipModuleDirtyFlag12 { get; private set; }
    public bool PersistentPersonDataDirty { get; private set; }

    public void SetPersistentNeighborPersonData(NeighborState neighbor, PersonData field, short value)
    {
        var index = (int)field;
        if (neighbor.PersonData[index] == value) return;
        neighbor.PersonData[index] = value;
        if (neighbor.PersonRecordVersion == 0) neighbor.PersonRecordVersion = 10;
        PersistentPersonDataDirty = true;
        DirtyPersistentPersonNeighborIds.Add(neighbor.Id);
    }

    public short InitializeRelationshipZodiac(NeighborState neighbor, bool hasName, Func<short[], short> compute)
    {
        var person = WalkOrder.OfType<XPerson>().FirstOrDefault(item => item[PersonData.NeighborId] == neighbor.Id);
        var data = person?.PersonData ?? neighbor.PersonData;
        var zodiac = data[(int)PersonData.Zodiac];
        if (hasName && zodiac == 0)
        {
            zodiac = compute(data);
            if (person is null) SetPersistentNeighborPersonData(neighbor, PersonData.Zodiac, zodiac);
            else data[(int)PersonData.Zodiac] = zodiac;
        }
        return zodiac;
    }
    public HashSet<short> DirtyRelationshipFamilies { get; } = [];
    public HashSet<short> DirtyRelationshipNeighbors { get; } = [];
    public HashSet<(short SourceNeighborId, short TargetNeighborId)> DirtyRelationshipPairs { get; } = [];
    public HashSet<short> DirtyPersistentPersonNeighborIds { get; } = [];
    public IReadOnlySet<short> RemovedNeighbors => _removedNeighbors;
    public IReadOnlyDictionary<short, uint> RemovedNeighborGuids => _removedNeighborGuids;
    public Func<XPerson, bool>? TryInterceptConnectedVisitorRemoval { get; set; }
    public Action<short>? NotifyRemovedNeighborCleared { get; set; }

    public void ClearRemovedNeighbor(short neighborId)
    {
        _removedNeighbors.Remove(neighborId);
        _removedNeighborGuids.Remove(neighborId);
        NotifyRemovedNeighborCleared?.Invoke(neighborId);
    }

    public void ClearAllRemovedNeighbors()
    {
        _removedNeighbors.Clear();
        _removedNeighborGuids.Clear();
    }

    private bool IsRemovedNeighborEntry(short neighborId, uint guid)
    {
        if (!_removedNeighbors.Contains(neighborId))
            return false;
        if (!_removedNeighborGuids.TryGetValue(neighborId, out var deletedGuid) || deletedGuid == 0 || guid == 0)
            return true;
        return guid == deletedGuid;
    }

    public void MarkRelationshipsChanged(NeighborState source, NeighborState target)
    {
        source.RelationshipsChangedFlag24 = true;
        target.RelationshipsChangedFlag24 = true;
        var sourceFamily = source.PersonData[(int)PersonData.FamilyNumber];
        var targetFamily = target.PersonData[(int)PersonData.FamilyNumber];
        if (sourceFamily != 0) DirtyRelationshipFamilies.Add(sourceFamily);
        if (targetFamily != 0) DirtyRelationshipFamilies.Add(targetFamily);
        DirtyRelationshipNeighbors.Add(source.Id);
        DirtyRelationshipPairs.Add((source.Id, target.Id));
        RelationshipModuleDirtyFlag12 = true;
    }

    public IffFile WriteSavedNeighborRelationshipsTo(IffFile neighborhood)
    {
        if ((!RelationshipModuleDirtyFlag12 && !PersistentPersonDataDirty) || SavedNeighbors is null || !SavedNeighbors.IsComplete)
            return neighborhood;
        var chunk = neighborhood.OfType(ResourceType.Parse("NBRS")).FirstOrDefault();
        if (chunk is null) return neighborhood;

        var entriesById = new Dictionary<short, Neighbor>();
        var entries = new List<Neighbor>(SavedNeighbors.Entries.Count);
        foreach (var saved in SavedNeighbors.Entries)
        {
            if (IsRemovedNeighborEntry(saved.NeighborId, saved.Guid)) continue;
            var updated = Neighbors.TryGetValue(saved.NeighborId, out var live)
                ? saved with
                {
                    Name = PersistentPersonDataDirty ? live.Name : saved.Name,
                    Relationships = RelationshipModuleDirtyFlag12 ? live.Relationships.Snapshot() : saved.Relationships,
                    RecordVersion = PersistentPersonDataDirty && live.PersonRecordVersion > 0
                        ? live.PersonRecordVersion
                        : saved.RecordVersion,
                    PersonData = PersistentPersonDataDirty && live.PersonRecordVersion > 0
                        ? live.PersonData.ToArray()
                        : saved.PersonData,
                }
                : saved;
            entries.Add(updated);
            entriesById[updated.NeighborId] = updated;
        }

        var updatedSlotCount = SavedNeighbors.SlotCount;
        var existingSlots = new HashSet<int>(entries.Select(e => e.Slot));
        for (var slotIndex = 0; slotIndex < NeighborWalkOrder.Count; slotIndex++)
        {
            var live = NeighborWalkOrder[slotIndex];
            if (live is null || IsRemovedNeighborEntry(live.Id, unchecked((uint)live.TypeRecordKey)) || entriesById.ContainsKey(live.Id))
                continue;
            if (live.PersonRecordVersion <= 0 && live.Relationships.Snapshot().Count == 0)
                continue;

            var targetSlot = slotIndex;
            while (existingSlots.Contains(targetSlot)) targetSlot++;
            existingSlots.Add(targetSlot);
            if (targetSlot + 1 > updatedSlotCount) updatedSlotCount = targetSlot + 1;

            var versioned = live.PersonRecordVersion > 0;
            var added = new Neighbor(
                Slot: targetSlot,
                RecordVersion: versioned ? live.PersonRecordVersion : (SavedNeighbors.Version > 0x48 ? 10 : 4),
                Name: live.Name,
                Field0x1C: 0,
                PersonData: versioned ? live.PersonData.ToArray() : null,
                NeighborId: live.Id,
                Guid: unchecked((uint)live.TypeRecordKey),
                Relationships: RelationshipModuleDirtyFlag12 ? live.Relationships.Snapshot() : new Dictionary<int, IReadOnlyList<int>>(),
                StoredFormat: SavedNeighbors.Version > 0x48 ? 10 : 0,
                PersonDataTrailingWord: 0);
            entries.Add(added);
            entriesById[added.NeighborId] = added;
        }

        var rewritten = new Neighbors(
            SavedNeighbors.Version,
            updatedSlotCount,
            entries,
            SavedNeighbors.IsComplete,
            SavedNeighbors.CompleteSlotPrefix);
        return neighborhood.WithChunkDataResized(chunk.Type, chunk.Id,
            rewritten.WriteChunkData(chunk.Data.Span[0] != 0));
    }
    private readonly HashSet<short> _removedNeighbors = [];
    private readonly Dictionary<short, uint> _removedNeighborGuids = [];

    /// <summary>Determines whether a neighbor is a character Sim, checked by testing if bits 2–3 of the
    /// object flags equal 1.</summary>
    public static bool IsCharacter(NeighborState neighbor) =>
        neighbor.FriendEligibilityObjectFlagsB4 is { } flags && (flags & 0xC) == 4;

    /// <summary>Purges unhoused characters whose person data specifies no family (field 61) and no house
    /// (field 67), restarting the scan after each deletion.</summary>
    public IReadOnlyList<short> DeleteUnhousedCharacters(Func<int, bool> isFamilyMember)
    {
        var deleted = new List<short>();
        while (NeighborWalkOrder.FirstOrDefault(neighbor => neighbor is not null && IsCharacter(neighbor) &&
                   neighbor.PersonData[(int)PersonData.FamilyNumber] == 0 &&
                   neighbor.PersonData[(int)PersonData.LingeringHouseNumber] == 0) is { } found)
        {
            if (!DeleteCharacter(found, isFamilyMember)) break;
            deleted.Add(found.Id);
        }
        return deleted;
    }

    /// <summary>Deletes a character Sim: removes their lot objects, inventory tokens, and relationships
    /// across all neighbors, provided the Sim does not belong to an existing family.</summary>
    public bool DeleteCharacter(NeighborState neighbor, Func<int, bool> isFamilyMember)
    {
        ArgumentNullException.ThrowIfNull(neighbor);
        ArgumentNullException.ThrowIfNull(isFamilyMember);
        if (!IsCharacter(neighbor)) return false;
        while (WalkOrder.FirstOrDefault(item => item.TypeRecord?.Key == neighbor.TypeRecordKey) is { } item)
            KillObject(item.Id);
        if (isFamilyMember(neighbor.TypeRecordKey)) return false;

        DispatchNotice(0xfb, neighbor.Id);
        var inventory = NeighborInventory(neighbor.Id);
        for (var type = 0; type <= 9; type++) inventory.RemoveAllOfType(type);
        RaiseNeighborInventoryChanged(neighbor.Id, inventory);
        var slot = NeighborWalkOrder.IndexOf(neighbor);
        if (slot >= 0) NeighborWalkOrder[slot] = null;
        foreach (var other in NeighborWalkOrder)
            other?.Relationships.RemoveTarget(neighbor.Id);
        Neighbors.Remove(neighbor.Id);
        _removedNeighbors.Add(neighbor.Id);
        _removedNeighborGuids[neighbor.Id] = unchecked((uint)neighbor.TypeRecordKey);
        RelationshipModuleDirtyFlag12 = true;
        return true;
    }

    /// <summary>The ordered neighborhood roster, preserving slot positions and null entries for 1-based
    /// tree variable indexing.</summary>
    public List<NeighborState?> NeighborWalkOrder { get; } = [];
    /// <summary>Resolves the one-based neighbor index used by tree variable scopes 0x20 and 0x26, which
    /// indexes the neighborhood roster independently of NeighborId.</summary>
    public NeighborState? NeighborManagerEntry(short oneBasedIndex) =>
        oneBasedIndex >= 1 && oneBasedIndex <= NeighborWalkOrder.Count ? NeighborWalkOrder[oneBasedIndex - 1] : null;
    /// <summary>Returns the opcode (low 15 bits) of the instruction the object thread is waiting on, or
    /// null if no stack frame is active.</summary>
    public short? WaitInstructionOpcode(XObject stackObject)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        if (!SavedExecutionStacks.TryGetValue(stackObject.Id, out var stack) || stack.Top is not { } frame) return null;
        if (frame.Tree is not Bhav tree) return null;
        if (frame.Instruction < 0 || frame.Instruction >= tree.Instructions.Count) return null;
        return (short)(tree.Instructions[frame.Instruction].Opcode & 0x7fff);
    }

    /// <summary>Clears the wait counter referenced by operand word 0 of the waiting instruction, used by
    /// primitives such as Sleep and Idle for Input.</summary>
    public void ClearWaitCounter(XObject stackObject)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        if (!SavedExecutionStacks.TryGetValue(stackObject.Id, out var stack) || stack.Top is not { } frame) return;
        if (frame.Tree is not Bhav tree) return;
        if (frame.Instruction < 0 || frame.Instruction >= tree.Instructions.Count) return;
        var operand = tree.Instructions[frame.Instruction].Operand;
        var index = (short)(operand[0] | operand[1] << 8);
        if (index >= 0 && index < frame.Parameters.Length) frame.Parameters[index] = 0;
    }

    /// <summary>Factory callback used by the Create New Object Instance primitive to instantiate objects by
    /// type GUID.</summary>
    public Func<int, XObject?>? ObjectFactory { get; set; }

    /// <summary>Creates and registers a catalog multi-tile object group, instantiating all parts and
    /// linking each part to the master root object.</summary>
    public Func<int, IReadOnlyList<XObject>?>? ObjectGroupFactory { get; set; }

    /// <summary>Creates an unregistered object candidate for read-only placement checks without adding it
    /// to the lot object registry.</summary>
    public Func<int, XObject?>? PlacementPreviewFactory { get; set; }
    /// <summary>Builds a build-tool cursor object (OBJD type 8 or 9 without a master ID) as a plain object
    /// for placement preview.</summary>
    public Func<int, XObject?>? BuildCursorFactory { get; set; }

    /// <summary>Creates and registers an unregistered multi-tile preview group whose members are removed
    /// together after scene export without adding saved-object type records.</summary>
    public Func<int, IReadOnlyList<XObject>?>? PlacementPreviewGroupFactory { get; set; }

    public void RemoveCreatedObjectGroup(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var root = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        var members = new List<XObject>();
        var seen = new HashSet<short>();
        for (var member = root; member is not null && seen.Add(member.Id);)
        {
            members.Add(member);
            member = member.MultiTileNextId == 0 ? null : ObjectById(member.MultiTileNextId);
        }
        foreach (var member in members.AsEnumerable().Reverse()) Remove(member.Id);
    }

    public short NextObjectId()
    {
        for (short id = 1; id < short.MaxValue; id++)
            if (!Objects.ContainsKey(id)) return id;
        throw new InvalidOperationException("No free object id.");
    }

    public bool TileHasObject(int tileX, int tileY, int level, int? guidKey)
    {
        foreach (var item in WalkOrder)
        {
            if (item.Position.Height != level) continue;
            if ((item.Position.X >> 4) != tileX || (item.Position.Y >> 4) != tileY) continue;
            if (guidKey is null || item.TypeRecord?.Key == guidKey) return true;
        }
        return false;
    }

    public short RoomAtTile(int tileX, int tileY, int level)
    {
        return TryGetRoomAtTile(tileX, tileY, level, out var room) ? room : (short)0;
    }

    /// <summary>Distinguishes a valid room ID of 0 from a tile with no room assigned.</summary>
    public bool TryGetRoomAtTile(int tileX, int tileY, int level, out short room)
    {
        room = 0;
        if (!LotRoomMaps.TryGetValue(level, out var map) || tileX < 0 || tileY < 0 || tileX >= map.Width || tileY >= map.Height)
            return false;
        var key = map.RoomAt(tileX, tileY);
        if (key == SimsEngine.World.RoomMap.NoRoom) return false;
        room = unchecked((short)key);
        return true;
    }

    public bool TileHasRoomDividingDiagonal(int tileX, int tileY, int level) =>
        LotRoomMaps.TryGetValue(level, out var map) && map.HasRoomDividingDiagonal(tileX, tileY);

    public bool TileHasDiagonalWall(int tileX, int tileY, int level) =>
        LotRoomMaps.TryGetValue(level, out var map) && map.HasDiagonalWall(tileX, tileY);

    /// <summary>Validates whether a newly created object can be placed. Relative placement bypasses wall
    /// and allowed-height checks, but still validates upper-floor presence.</summary>
    public bool CanPlaceCreatedObject(XObject created, ObjectPosition at, XObject? relativeTo = null, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(created);
        return PassesCommonPlacementChecks(created, at, relativeTo is not null, checkStack,
            relativeTo?.ClockManagerIndex ?? 0) && PassesUpperFloorPlacementCheck(created, at);
    }

    public bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        var size = LotGeometry.LotDimension;
        var height = parameters.Height is >= 1 and <= 2 ? parameters.Height : 1;
        var centerPosition = new ObjectPosition(parameters.CenterX, parameters.CenterY, height);
        if (parameters.RandomizeCenter)
        {
            // Mode 5 scatters across the lot or within a [-5..+4] offset around the target.
            if (!parameters.HasTarget || ((centerPosition.X >> 4) == 1 && (centerPosition.Y >> 4) == 1))
            {
                var randY = (int)(NextRandom() % (uint)size);
                var randX = (int)(NextRandom() % (uint)size);
                centerPosition = new ObjectPosition(randX * 16 + 8, randY * 16 + 8, height);
            }
            else
            {
                var baseTileX = centerPosition.X >> 4;
                var baseTileY = centerPosition.Y >> 4;
                var scatteredX = baseTileX;
                var scatteredY = baseTileY;
                for (var scatterTry = 0; scatterTry < 32; scatterTry++)
                {
                    var dy = (int)(NextRandom() % 10) - 5;
                    var dx = (int)(NextRandom() % 10) - 5;
                    var candX = baseTileX + dx;
                    var candY = baseTileY + dy;
                    if (candX >= 1 && candX < size - 1 && candY >= 1 && candY < size - 1)
                    {
                        scatteredX = candX;
                        scatteredY = candY;
                        break;
                    }
                }
                centerPosition = new ObjectPosition(scatteredX * 16 + 8, scatteredY * 16 + 8, height);
            }
        }
        var transformedCenter = LotGeometry.TransformSearchCenter(centerPosition, LotTransformParameter);
        var centerX = Math.Clamp(transformedCenter.X >> 4, 1, size - 2);
        var centerY = Math.Clamp(transformedCenter.Y >> 4, 1, size - 2);
        var hasCenterRoom = TryGetRoomAtTile(centerX, centerY, height, out var centerRoom);
        var offsetX = 0;
        var offsetY = 0;
        var consecutiveOffLot = 0;
        // Walk stops after lotDimension * 8 consecutive off-lot steps or 4096 attempts.
        for (var attempt = 0; attempt < 0x1000; attempt++)
        {
            var x = centerX + offsetX;
            var y = centerY + offsetY;
            var insideLot = x >= 1 && y >= 1 && x < size - 1 && y < size - 1;
            consecutiveOffLot = insideLot ? 0 : consecutiveOffLot + 1;
            var hasCandidateRoom = insideLot && TryGetRoomAtTile(x, y, height, out var candidateRoom);
            var candidateRoomKey = hasCandidateRoom ? RoomAtTile(x, y, height) : (short)0;
            if (insideLot &&
                !TileHasDiagonalWall(x, y, height) &&
                (parameters.ContinueFlag == 0 || !hasCenterRoom || !hasCandidateRoom || candidateRoomKey == centerRoom) &&
                (parameters.FlagB == 0 || TileValueAt(x, y, height) == 0) &&
                PassesFindLocationWaterChecks(x, y, height, parameters.FlagC))
            {
                var candidate = new ObjectPosition(x * 16 + 8, y * 16 + 8, height);
                if (CanPlaceObject(stackObject, candidate, checkStack))
                {
                    found = candidate;
                    return true;
                }
            }

            if (consecutiveOffLot >= size * 8) break;

            AdvanceFindLocationOffset(parameters.Direction, ref offsetX, ref offsetY);
        }
        found = default;
        return false;
    }

    /// <summary>Searches outward in a square spiral from the given center tile, testing placement at tile
    /// centers and returning the first accepted position.</summary>
    public bool FindGoodLocation(XObject item, int centerX, int centerY, out ObjectPosition found)
    {
        ArgumentNullException.ThrowIfNull(item);
        var size = LotGeometry.LotDimension;
        // Transforms search center from viewer coordinates to canonical lot coordinates.
        var transformedCenter = LotGeometry.TransformSearchCenter(
            new ObjectPosition(centerX * 16 + 8, centerY * 16 + 8, 1), LotTransformParameter);
        centerX = transformedCenter.X >> 4;
        centerY = transformedCenter.Y >> 4;
        var dx = 0;
        var dy = 0;
        var consecutiveOffLot = 0;
        for (var attempt = 0; attempt < 0x1000; attempt++)
        {
            var x = centerX + dx;
            var y = centerY + dy;
            var insideLot = x >= 1 && y >= 1 && x < size - 1 && y < size - 1;
            consecutiveOffLot = insideLot ? 0 : consecutiveOffLot + 1;
            if (insideLot)
            {
                var candidate = new ObjectPosition(x * 16 + 8, y * 16 + 8, 1);
                if (CanPlaceObject(item, candidate))
                {
                    found = candidate;
                    return true;
                }
            }

            if (consecutiveOffLot >= size * 8) break;

            // Advances the square spiral in order (0,0), (-1,0), (-1,1), (0,1).
            var negativeDx = -dx;
            if (dy > negativeDx || dy > dx)
            {
                if (dy > dx)
                {
                    if (dy < negativeDx) dy++;
                    else dx++;
                }
                else if (dy <= negativeDx)
                {
                    if (dy < negativeDx) dy++;
                }
                else dy--;
            }
            else dx--;
        }

        found = default;
        return false;
    }

    private bool PassesFindLocationWaterChecks(int x, int y, int height, byte flagC)
    {
        var waterOk = true;
        if (LotRoomMaps.TryGetValue(height, out var map) && map.TryGetWaterGridByte(x, y, out var value) && value < 0x10)
            waterOk = false;
        if (!waterOk) return false;
        return flagC == 0 || FindLocationWaterOverride || LotTileFlags?.InsideLot(x, y) != false;
    }

    /// <summary>Determines if a position is on a pool tile, defined as being inside the one-tile lot border
    /// with an Arry 9 grid value below 0x10.</summary>
    internal bool IsPoolTile(ObjectPosition position)
    {
        if (position.Height != 1 || !LotRoomMaps.TryGetValue(1, out var map)) return false;
        var x = position.X >> 4;
        var y = position.Y >> 4;
        return x > 0 && y > 0 && x < map.Width - 1 && y < map.Height - 1 &&
            map.TryGetWaterGridByte(x, y, out var value) && value < 0x10;
    }

    private static void AdvanceFindLocationOffset(short direction, ref int offsetX, ref int offsetY)
    {
        if (direction == -1)
        {
            if (-offsetX < offsetY)
            {
                if (offsetX >= offsetY)
                {
                    offsetY--;
                    return;
                }
            }
            else if (offsetX >= offsetY)
            {
                offsetX--;
                return;
            }

            if (offsetY < -offsetX)
            {
                if (offsetX < offsetY) offsetY++;
            }
            else
            {
                offsetX++;
            }
            return;
        }

        // Alternates offsets along the search axis: 0, +1, -1, +2, -2...
        if (direction is 0 or 4) offsetY = offsetY < 1 ? 1 - offsetY : -offsetY;
        else if (direction is 2 or 6) offsetX = offsetX < 1 ? 1 - offsetX : -offsetX;
    }

    public bool CanPlaceObject(XObject item, ObjectPosition at, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastPortalPlacementFailureCode = 0;
        LastPlacementFailureCode = 0;
        if (item.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal)
        {
            if (!PassesMultiTileMemberPlacementChecks(item, at, checkStack)) return false;
        }
        else if (!PassesCommonPlacementChecks(item, at, checkStack: checkStack) ||
                 !PassesUpperFloorPlacementCheck(item, at)) return false;
        if (!PassesPortalWallStyleCheck(item, at))
        {
            LastPortalPlacementFailureCode = 0x16;
            return false;
        }
        if (!PassesPortalPlacementAlignmentCheck(item, at))
        {
            LastPortalPlacementFailureCode = 3;
            return false;
        }
        return true;
    }

    /// <summary>Validates an object's linked parts at their translated ObjM positions before
    /// portal-specific checks run.</summary>
    private bool PassesMultiTileMemberPlacementChecks(XObject item, ObjectPosition requested, TreeStack? checkStack,
        bool hasRelativeObject = false, short relativeObjectClockManagerIndex = 0)
    {
        var itemOffset = MultiTileOffset(item);
        var rootTarget = new ObjectPosition(requested.X - itemOffset.X, requested.Y - itemOffset.Y,
            requested.Height - itemOffset.Height);
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        var visited = new HashSet<short>();
        for (var member = lead; member is not null && visited.Add(member.Id);
             member = member.MultiTileNextId == 0 ? null : ObjectById(member.MultiTileNextId))
        {
            var offset = MultiTileOffset(member);
            var memberTarget = new ObjectPosition(rootTarget.X + offset.X, rootTarget.Y + offset.Y,
                rootTarget.Height + offset.Height);
            if (!PassesCommonPlacementChecks(member, memberTarget, hasRelativeObject, checkStack, relativeObjectClockManagerIndex) ||
                !PassesUpperFloorPlacementCheck(member, memberTarget)) return false;
        }
        return true;
    }

    /// <summary>Placement failure code for portals: 0x16 indicates a styled-wall mismatch and 3 indicates
    /// an uncentered position.</summary>
    public int LastPortalPlacementFailureCode { get; private set; }

    public int LastPlacementFailureCode { get; internal set; }

    internal void ResetPlacementFailure()
    {
        LastPlacementFailureCode = 0;
        LastPlacementBlockingObjectId = 0;
    }

    internal short LastPlacementBlockingObjectId { get; set; }

    /// <summary>Requires portals to be placed exactly at tile centers.</summary>
    private static bool PassesPortalPlacementAlignmentCheck(XObject item, ObjectPosition requested) =>
        item.RuntimeClass != SimsEngine.Formats.ObjectClass.Portal ||
        ((requested.X & 0x0f) == 8 && (requested.Y & 0x0f) == 8);

    /// <summary>Verifies that linked portal members with a non-zero OBJD WallStyle are placed on a wall
    /// segment of category 1.</summary>
    private bool PassesPortalWallStyleCheck(XObject item, ObjectPosition requested)
    {
        if (item.RuntimeClass != SimsEngine.Formats.ObjectClass.Portal) return true;
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId);
        if (lead is null) return true;
        var requestedOffset = MultiTileOffset(item);
        var leadOffset = MultiTileOffset(lead);
        var leadTargetX = requested.X - requestedOffset.X + leadOffset.X;
        var leadTargetY = requested.Y - requestedOffset.Y + leadOffset.Y;
        var leadTargetLevel = requested.Height - requestedOffset.Height + leadOffset.Height;
        foreach (var member in MultiTileMembers(lead))
        {
            if (member.Definition[(int)ObjdField.WallStyle] == 0) continue;
            var localMask = member[ObjectData.WallPlacementFlags] & 5;
            var wallBit = localMask switch { 1 => 2u, 4 => 8u, _ => 0u };
            if (wallBit == 0) continue;
            wallBit = RotatePlacementWallBit(wallBit, (member[ObjectData.Direction] & 7) / 2);

            var memberOffset = MultiTileOffset(member);
            var tileX = (leadTargetX - leadOffset.X + memberOffset.X) >> 4;
            var tileY = (leadTargetY - leadOffset.Y + memberOffset.Y) >> 4;
            var level = leadTargetLevel - leadOffset.Height + memberOffset.Height;
            var size = LotGeometry.LotDimension;
            if (tileX <= 0 || tileY <= 0 || tileX >= size - 1 || tileY >= size - 1) continue;
            if (!LotRoomMaps.TryGetValue(level, out var map) || map.ExpandedWallAt(tileX, tileY) is not { } walls)
                return false;
            if (walls.WallCategory(wallBit) != 1) return false;
        }
        return true;
    }

    /// <summary>Validates candidate placement in a route slot, enforcing bounds, terrain, and upper-floor
    /// constraints while bypassing ordinary floor permission, wall-placement, and footprint collision
    /// checks.</summary>
    internal bool CanPlaceRouteCandidate(XObject item, ObjectPosition at, XObject? referenceObject,
        int referenceSlotIndex = -1, TreeStack? checkStack = null)
    {
        LastPlacementFailureCode = 0;
        if (referenceObject is null) return CanPlaceObject(item, at, checkStack);
        return CanPlaceReferencedSlot(item, at, referenceObject, referenceSlotIndex);
    }

    /// <summary>Validates editor placement into a referenced slot, rejecting centered placement into slots
    /// with normalized height codes 5, 6, or 7 with error 0x0B.</summary>
    internal bool CanUserPlaceReferencedSlot(XObject item, ObjectPosition at, XObject referenceObject,
        int referenceSlotIndex = -1)
    {
        if (!CanPlaceReferencedSlot(item, at, referenceObject, referenceSlotIndex)) return false;
        if (referenceSlotIndex >= 0 && (at.X & 0x0f) == 8 && (at.Y & 0x0f) == 8 &&
            referenceObject.FootprintSlots[referenceSlotIndex].HeightCode is > 4 and < 8)
        {
            LastPlacementFailureCode = 0x0b;
            return false;
        }
        return true;
    }

    internal bool CanPlaceRelativeObject(XObject item, ObjectPosition at, XObject referenceObject,
        int referenceSlotIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(referenceObject);
        return CanPlaceReferencedSlot(item, at, referenceObject, referenceSlotIndex);
    }

    private bool CanPlaceReferencedSlot(XObject item, ObjectPosition at, XObject referenceObject,
        int referenceSlotIndex)
    {
        LastPlacementFailureCode = 0;
        // Rejects relative placement with error 0x0C if either object sets Flags bit 2.
        if (((unchecked((ushort)item[ObjectData.Flags]) |
              unchecked((ushort)referenceObject[ObjectData.Flags])) & 0x0004) != 0)
        {
            LastPlacementFailureCode = 0x0c;
            return false;
        }
        if (referenceSlotIndex >= 0)
        {
            if (referenceSlotIndex >= referenceObject.FootprintSlots.Count)
            {
                LastPlacementFailureCode = 0x0d;
                return false;
            }
            // A slot must be empty or held by the moving object; other occupants yield 0x0B.
            var slot = referenceObject.FootprintSlots[referenceSlotIndex];
            var occupant = slot.OccupantObjectId;
            if (occupant != 0 && occupant != item.ClockManagerIndex)
            {
                LastPlacementFailureCode = 0x0b;
                return false;
            }
            // Enforces same-HeightCode centerpiece mutual exclusion unless Flags has 0x2000.
            if (item is not XPerson && item.Definition[(int)ObjdField.Type] != XPerson.PersonType)
            {
                var itemCenteredAndBlocking = slot.X == 0f && slot.Y == 0f &&
                    (unchecked((ushort)item[ObjectData.Flags]) & 0x2000) == 0;
                foreach (var otherSlot in referenceObject.FootprintSlots)
                {
                    if (otherSlot.HeightCode != slot.HeightCode || otherSlot.OccupantObjectId == 0 ||
                        otherSlot.OccupantObjectId == item.ClockManagerIndex)
                        continue;
                    if (otherSlot.X == 0f && otherSlot.Y == 0f)
                    {
                        var otherObj = item.ObjectTableEntry(otherSlot.OccupantObjectId) ??
                            ObjectTableEntry(otherSlot.OccupantObjectId);
                        if (otherObj is null || (unchecked((ushort)otherObj[ObjectData.Flags]) & 0x2000) == 0)
                        {
                            LastPlacementFailureCode = 0x0b;
                            return false;
                        }
                    }
                    if (itemCenteredAndBlocking)
                    {
                        LastPlacementFailureCode = 0x0b;
                        return false;
                    }
                }
            }
            // Tests slot HeightCode against AllowedHeightFlags bit (HeightCode - 1).
            var heightCode = slot.HeightCode;
            var allowedHeightFlags = unchecked((ushort)item[ObjectData.AllowedHeightFlags]);
            if (heightCode == 0 ||
                (allowedHeightFlags & (1 << ((heightCode - 1) & 0x1f))) == 0)
            {
                LastPlacementFailureCode = 0x18;
                return false;
            }
            if (item[ObjectData.Weight] > referenceObject[ObjectData.SupportStrength])
            {
                LastPlacementFailureCode = 0x0e;
                return false;
            }
            // Rejects relative placement with error 0x0B if it creates a containment cycle.
            if (HasRelativePlacementCycle(item, referenceObject))
            {
                LastPlacementFailureCode = 0x0b;
                return false;
            }
        }
        var relativePosition = referenceObject.Position;
        if (!PassesCommonPlacementChecks(item, relativePosition, hasRelativeObject: true,
                relativeObjectClockManagerIndex: referenceObject.ClockManagerIndex) ||
            !PassesUpperFloorPlacementCheck(item, relativePosition)) return false;
        return true;
    }

    private bool HasRelativePlacementCycle(XObject item, XObject referenceObject)
    {
        // Walks enclosing containers, skipping SlotHierNumber 0 tile-overlap links.
        var current = referenceObject;
        var visited = new HashSet<short>();
        while (visited.Add(current.ClockManagerIndex))
        {
            if (ReferenceEquals(current, item)) return true;
            var overlap = current;
            var overlapVisited = new HashSet<short> { overlap.ClockManagerIndex };
            while (overlap[ObjectData.SlotHierNumber] == 0 && overlap[ObjectData.ParentId] > 0)
            {
                var next = overlap.ObjectTableEntry(overlap[ObjectData.ParentId]) ?? ObjectTableEntry(overlap[ObjectData.ParentId]);
                if (next is null) break;
                if (next.Slots.Count == 0 || next.Slots[0] != overlap.ClockManagerIndex) break;
                if (!overlapVisited.Add(next.ClockManagerIndex)) return true;
                overlap = next;
            }
            var parentKey = overlap[ObjectData.ParentId];
            if (parentKey <= 0 || (overlap.ObjectTableEntry(parentKey) ?? ObjectTableEntry(parentKey)) is not { } parent)
                return false;
            current = parent;
        }
        return true;
    }

    private bool PassesUpperFloorPlacementCheck(XObject item, ObjectPosition at)
    {
        if (at.Height <= 1) return true;
        var flags = unchecked((ushort)item[ObjectData.PlacementFlags]);
        var floorWord = LotRoomMaps.TryGetValue(at.Height, out var roomMap)
            ? roomMap.FloorGridWordOrZeroAt(at.X >> 4, at.Y >> 4)
            : (ushort)0;
        if ((flags & 0x0200) != 0 || floorWord != 0) return true;
        LastPlacementFailureCode = 0x12;
        return false;
    }

    /// <summary>Assigns a Route ID if needed and initializes that route's cost entry on every active
    /// portal.</summary>
    internal short InitializePortalRouteCosts(XObject routeOwner)
    {
        var current = routeOwner[ObjectData.RouteId];
        if (current == 0)
        {
            var used = new HashSet<short>();
            foreach (var entry in ClockManager.Entries)
                if (entry.Object is { } item && item[ObjectData.RouteId] > 0)
                    used.Add(item[ObjectData.RouteId]);
            foreach (var portal in WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal))
                if (portal[ObjectData.RouteId] > 0)
                    used.Add(portal[ObjectData.RouteId]);

            for (short candidate = 1; candidate <= 9999; candidate++)
            {
                if (used.Contains(candidate)) continue;
                routeOwner[ObjectData.RouteId] = candidate;
                current = candidate;
                break;
            }
        }

        if (current > 0)
            foreach (var portal in WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal))
                portal.SetPortalRouteCost(current, XObject.UninitializedPortalRouteCost);
        return current;
    }

    /// <summary>Active lot dimension in tiles, matching classic Sims.exe's house lot size (Global 23 when
    /// populated in the valid range 4..64, defaulting to <see cref="LotGeometry.LotDimension"/>).</summary>
    public int ActiveLotDimension
    {
        get
        {
            var configured = this[SimulationGlobal.LotSize];
            return configured is > 3 and <= LotGeometry.LotDimension
                ? configured
                : LotGeometry.LotDimension;
        }
    }

    private bool PassesCommonPlacementChecks(XObject item, ObjectPosition at, bool hasRelativeObject = false,
        TreeStack? checkStack = null, short relativeObjectClockManagerIndex = 0)
    {
        var tileX = at.X >> 4;
        var tileY = at.Y >> 4;
        var size = ActiveLotDimension;
        if (tileX <= 0 || tileY <= 0 || tileX >= size - 1 || tileY >= size - 1)
        {
            LastPlacementFailureCode = 1;
            return false;
        }
        if (at.Height is < 1 or > 3)
        {
            LastPlacementFailureCode = 2;
            return false;
        }

        var placementFlags = unchecked((ushort)item[ObjectData.PlacementFlags]);
        // PlacementFlags 0x1000 forbids placement on upper floors (error 0x1F).
        if (at.Height >= 2 && (placementFlags & 0x1000) != 0)
        {
            LastPlacementFailureCode = 0x1f;
            return false;
        }

        // RequireWater rejects dry tiles; AllowOnWater is needed on water (error 0x1D).
        if (!hasRelativeObject)
        {
            var hasWaterAtTile = HasPlacementEligibilityAt(tileX, tileY, at.Height);
            if (!hasWaterAtTile)
            {
                if ((placementFlags & 0x2020) != 0)
                {
                    LastPlacementFailureCode = 0x2f;
                    return false;
                }
            }
            else if ((placementFlags & 0x2007) != 0 && (placementFlags & 0x0004) == 0 &&
                     !ObjectsAtTile(tileX, tileY, at.Height).Any(obj => !ReferenceEquals(obj, item) &&
                         (unchecked((ushort)obj[ObjectData.Flags]) & 0x1000) != 0))
            {
                LastPlacementFailureCode = 0x1d;
                return false;
            }
        }

        if (!hasRelativeObject && item.Definition[(int)ObjdField.Type] != XPerson.PersonType &&
            (at.Height == 1 || (placementFlags & 0x1000) == 0) &&
            !PassesWallPlacementCheck(item, tileX, tileY, at.Height)) return false;

        // Level 1 pool tiles are identified by an Arry 9 grid value below 0x10.
        if (at.Height == 1 && LotRoomMaps.TryGetValue(1, out var roomMap))
        {
            var groundClass = roomMap.WaterGridByteAt(tileX, tileY);
            var flags = unchecked((ushort)item[ObjectData.PlacementFlags]);
            if ((flags & 0x0800) == 0 && groundClass < 0x10)
            {
                LastPlacementFailureCode = 0x1d;
                return false;
            }
            if ((flags & 0x1000) != 0 && groundClass >= 0x10)
            {
                LastPlacementFailureCode = 0x1e;
                return false;
            }
        }

        // Sloped tiles are rejected unless PlacementFlags AllowOnSlope (0x0100) is set.
        if (!hasRelativeObject && item is not XPerson && item.Definition[(int)ObjdField.Type] != XPerson.PersonType &&
            (placementFlags & 0x0103) != 0 && (placementFlags & 0x0100) == 0 && LotTerrain is { } terrain)
        {
            var corners = terrain.Corners(tileX, tileY);
            if (!(corners[0] == corners[1] && corners[0] == corners[2] && corners[0] == corners[3]))
            {
                LastPlacementFailureCode = 0x11;
                return false;
            }
        }

        // AllowOnFloor (0x01) covers floors and pool deck (Arry 9 == 0x10).
        if (!hasRelativeObject && item is not XPerson && item.Definition[(int)ObjdField.Type] != XPerson.PersonType &&
            (placementFlags & 0x0003) != 0 &&
            LotRoomMaps.TryGetValue(at.Height, out var floorRoomMap) && floorRoomMap.HasAuthoritativeOutsideRooms &&
            floorRoomMap.TryGetFloorGridWord(tileX, tileY, out var rawFloorWord))
        {
            var floorWord = rawFloorWord >= 65534 ? (ushort)0 : rawFloorWord;
            var groundByte = at.Height == 1 ? floorRoomMap.WaterGridByteAt(tileX, tileY) : (byte)0xff;
            var isPoolOrWater = (at.Height == 1 && groundByte < 0x10) || HasPlacementEligibilityAt(tileX, tileY, at.Height);
            if (!isPoolOrWater)
            {
                if ((placementFlags & 0x0001) == 0 && (floorWord != 0 || (at.Height == 1 && groundByte == 0x10)))
                {
                    LastPlacementFailureCode = 0x13;
                    return false;
                }
                if ((placementFlags & 0x0002) == 0 && at.Height == 1 && floorWord == 0)
                {
                    LastPlacementFailureCode = 0x14;
                    return false;
                }
            }
        }

        if (!hasRelativeObject)
        {
            var roomPlacement = item[ObjectData.RoomPlacement];
            var managerBit = (BuildModeType6PlacementFlagsAt(tileX, tileY, at.Height) & 0x04) != 0 ||
                (LotRoomMaps.TryGetValue(at.Height, out var rpMap) &&
                 rpMap.HasAuthoritativeOutsideRooms &&
                 RoomIsOutside(RoomAtTile(tileX, tileY, at.Height)));
            if (roomPlacement == 1 && !managerBit)
            {
                LastPlacementFailureCode = 9;
                return false;
            }
            if (roomPlacement == 2 && managerBit)
            {
                LastPlacementFailureCode = 10;
                return false;
            }
        }

        var key = TileValueAt(tileX, tileY, at.Height);
        if (!hasRelativeObject && (placementFlags & 1) == 0 &&
            key != 0 && key != item.ClockManagerIndex && key != relativeObjectClockManagerIndex &&
            (key > short.MaxValue || ObjectTableEntry((short)key) is not { } rootObj || !PlacementNeighborCallbackFilters(item, rootObj)))
        {
            LastPlacementFailureCode = 0x0b;
            return false;
        }
        if (!hasRelativeObject && !PassesFootprintCollisionCheck(item, at, checkStack)) return false;

        // Bit 0 of AllowedHeightFlags must be set to permit floor placement.
        if (!hasRelativeObject && (item[ObjectData.AllowedHeightFlags] & 1) == 0)
        {
            LastPlacementFailureCode = 0x18;
            return false;
        }
        return true;
    }

    private bool PassesFootprintCollisionCheck(XObject candidate, ObjectPosition at, TreeStack? checkStack)
    {
        var candidatePerson = candidate.Definition[(int)ObjdField.Type] == XPerson.PersonType
            ? candidate as XPerson ?? throw new InvalidOperationException("Person placement requires an XPerson instance.")
            : null;
        // Ghosts bypass footprint collisions unless slotted or attached to a parent.
        if (candidatePerson is not null &&
            ((candidatePerson[PersonData.IsGhost] & 1) != 0 || (candidatePerson[PersonData.IsLivingGhost] & 1) != 0))
        {
            var hasSecondSlotObject = candidatePerson.Slots.Count > 1 &&
                candidatePerson.ObjectTableEntry(candidatePerson.Slots[1]) is not null;
            var hasParentObject = HasGhostPlacementParentContext(candidatePerson);
            if (!hasSecondSlotObject && !hasParentObject) return true;
        }

        var bounds = candidatePerson?.FootprintBoundsAt(at) ?? PlacementFootprintBounds(candidate, at);
        if (at.Height <= 2)
        {
        foreach (var other in WalkOrder)
        {
            if (ReferenceEquals(candidate, other) || other.Position.Height != at.Height) continue;
            if (other[ObjectData.ParentId] > 0 && other[ObjectData.SlotHierNumber] > 0) continue;
            if (PlacementNeighborCallbackFilters(candidate, other)) continue;

            FootprintBounds otherBounds;
            if (candidatePerson is not null)
            {
                // Objects setting Flags bit 4 (such as doors) allow people through footprints.
                var otherFlags = other[ObjectData.Flags];
                var checksOverlap = (otherFlags & 4) == 0
                    ? (otherFlags & 0x10) == 0
                    : (otherFlags & 2) != 0;
                if (!checksOverlap) continue;
                if (other is XPerson neighborPerson)
                {
                    neighborPerson.RefreshFootprintBounds();
                    otherBounds = neighborPerson.FootprintBounds;
                }
                else otherBounds = PlacementFootprintBounds(other, other.Position);
            }
            else if (other.Definition[(int)ObjdField.Type] == XPerson.PersonType)
            {
                var person = (XPerson)other;
                var candidateFlags = candidate[ObjectData.Flags];
                var checksPersonOverlap = (candidatePerson is not null &&
                                           (candidatePerson[PersonData.IsLivingGhost] & 1) != 0) ||
                                          ((candidateFlags & 4) == 0
                    ? (candidateFlags & 0x10) == 0
                    : (candidateFlags & 2) != 0);
                if ((person[PersonData.IsGhost] & 1) != 0 || !checksPersonOverlap) continue;
                person.RefreshFootprintBounds();
                otherBounds = person.FootprintBounds;
            }
            else
            {
                var candidateFlags = candidate[ObjectData.Flags];
                var otherFlags = other[ObjectData.Flags];
                // At the center tile, Exclusive Placement Flags bit 0 rejects placement.
                var sharesRootTile = (other.Position.X >> 4) == (at.X >> 4) &&
                                     (other.Position.Y >> 4) == (at.Y >> 4) &&
                                     (((candidateFlags | otherFlags) & 4) != 0);
                if (sharesRootTile &&
                    (candidate[ObjectData.ExclusivePlacementFlags] & other[ObjectData.ExclusivePlacementFlags] & 1) != 0)
                {
                    LastPlacementFailureCode = 0x0b;
                    return false;
                }
                var candidateBuildModeType = candidate.TypeRecord?.AutonomyAlternativeTypeFlag is > 0 and var candidateAlt
                    ? candidateAlt
                    : candidate.Definition[(int)ObjdField.BuildModeType];
                var otherBuildModeType = other.TypeRecord?.AutonomyAlternativeTypeFlag is > 0 and var otherAlt
                    ? otherAlt
                    : other.Definition[(int)ObjdField.BuildModeType];
                if (sharesRootTile && (candidateFlags & 4) != 0 && (otherFlags & 4) != 0 &&
                    ((candidateBuildModeType == 2) == (otherBuildModeType == 2)))
                {
                    var useLegacyObjectIdField = candidate[ObjectData.WallPlacementFlags] == 0 &&
                        other[ObjectData.WallPlacementFlags] == 0 &&
                        (candidate[ObjectData.ObjectId] != candidate.ClockManagerIndex ||
                         other[ObjectData.ObjectId] != other.ClockManagerIndex);
                    var candidateManagerState = PlacementManagerState(candidate, useLegacyObjectIdField);
                    var otherManagerState = PlacementManagerState(other, useLegacyObjectIdField);
                    if (candidateManagerState != 0 && candidateManagerState == otherManagerState)
                    {
                        LastPlacementFailureCode = 0x16;
                        return false;
                    }
                }
                if ((candidateFlags & 4) != 0 || (otherFlags & 4) != 0) continue;
                otherBounds = PlacementFootprintBounds(other, other.Position);
            }

            if (otherBounds.MinX < bounds.MaxX && bounds.MinX < otherBounds.MaxX &&
                otherBounds.MinY < bounds.MaxY && bounds.MinY < otherBounds.MaxY &&
                !PlacementFunctionFiveAllowsOverlap(candidate, other, checkStack))
            {
                LastPlacementFailureCode = 0x0b;
                LastPlacementBlockingObjectId = other.ClockManagerIndex;
                return false;
            }
        }
        }
        if ((unchecked((ushort)candidate[ObjectData.PlacementFlags]) & 0x0400) == 0 &&
            FootprintIntersectsWall(bounds, at.Height))
        {
            LastPlacementFailureCode = 7;
            return false;
        }
        return true;
    }

    /// <summary>Checks whether the candidate object's linked multi-tile chain contains the specified
    /// neighbor.</summary>
    private bool PlacementNeighborCallbackFilters(XObject candidate, XObject other)
    {
        if (candidate is XPerson) return ReferenceEquals(candidate, other);

        var nextId = candidate.MultiTileMasterId == 0 ? candidate.Id : candidate.MultiTileMasterId;
        var visited = new HashSet<short>();
        while (nextId != 0 && visited.Add(nextId))
        {
            if (nextId == other.Id) return true;
            nextId = ObjectById(nextId)?.MultiTileNextId ?? 0;
        }
        return false;
    }

    /// <summary>Maps wall-placement flags to directional manager states, advancing one cardinal
    /// quarter-turn per two direction units.</summary>
    private static int PlacementManagerState(XObject item, bool useLegacyObjectIdField = false)
    {
        var flags = useLegacyObjectIdField ? item[ObjectData.ObjectId] : item[ObjectData.WallPlacementFlags];
        var state = (flags & 5) switch
        {
            1 => 2,
            4 => 8,
            _ => 0,
        };
        if (state == 0) return 0;

        var rotation = (item[ObjectData.Direction] & 7) >> 1;
        return ((state << rotation) & 0xf) | (state >> (4 - rotation));
    }

    /// <summary>Determines whether a ghost has an enclosing parent container context by traversing
    /// tile-overlap parent links.</summary>
    private static bool HasGhostPlacementParentContext(XPerson person)
    {
        XObject current = person;
        var visited = new HashSet<short>();
        while (current[ObjectData.SlotHierNumber] == 0 && visited.Add(current.ClockManagerIndex))
        {
            var parent = person.ObjectTableEntry(current[ObjectData.ParentId]);
            if (parent is null) break;
            current = parent;
        }

        return person.ObjectTableEntry(current[ObjectData.ParentId]) is not null;
    }

    /// <summary>Evaluates function 5 (Can Object Overlap) for the candidate against the neighbor, falling
    /// back to the neighbor against the candidate.</summary>
    private bool PlacementFunctionFiveAllowsOverlap(XObject candidate, XObject other, TreeStack? checkStack)
    {
        // Overlap cannot be approved without an active virtual machine execution stack.
        if (checkStack is null) return false;

        var previous = SelfRemovalEndsRun;
        SelfRemovalEndsRun = false;
        try
        {
            var candidateTree = FunctionTreeId(candidate, 5);
            if (candidateTree != 0 && candidate.TypeRecord?.BehaviorScope is { } candidateScope &&
                checkStack.RunToReturn(candidateScope, [], true, candidateTree, other.StackObjectKey)) return true;
            var otherTree = FunctionTreeId(other, 5);
            return otherTree != 0 && other.TypeRecord?.BehaviorScope is { } otherScope &&
                TreeRunner.RunWithParameters(this, other, otherScope, otherTree, candidate.StackObjectKey, other.Temporaries);
        }
        finally
        {
            SelfRemovalEndsRun = previous;
        }
    }

    /// <summary>Calculates footprint bounds from OBJD TileWidth (defaulting to 16) and the four extent
    /// nibbles rotated by object direction.</summary>
    internal static FootprintBounds PlacementFootprintBounds(XObject item, ObjectPosition at)
    {
        var span = item.Definition[(int)ObjdField.TileWidth];
        if (span == 0) span = 16;
        var half = span / 2;
        var mask = item.TypeRecord?.FootprintMask ?? 0;
        Span<int> extents = stackalloc int[4];
        for (var index = 0; index < extents.Length; index++) extents[index] = (mask >> (index * 4)) & 0xf;
        var direction = item[ObjectData.Direction] & 7;
        var rotation = (8 - direction) / 2;
        return new FootprintBounds(
            at.X + half - extents[(rotation + 1) % 4],
            at.Y + half - extents[(rotation + 2) % 4],
            at.X - half + extents[(rotation + 3) % 4],
            at.Y - half + extents[rotation % 4]);
    }

    internal bool FootprintIntersectsWall(FootprintBounds bounds, int level)
    {
        if (!LotRoomMaps.TryGetValue(level, out var roomMap)) return false;
        for (var tileY = Math.Max(0, bounds.MinY >> 4); tileY <= (bounds.MaxY - 1) >> 4; tileY++)
        for (var tileX = Math.Max(0, bounds.MinX >> 4); tileX <= (bounds.MaxX - 1) >> 4; tileX++)
        {
            var walls = roomMap.ExpandedWallAt(tileX, tileY);
            if (walls is null) continue;
            var mask = walls.Value.UInt32At(0);
            ReadOnlySpan<uint> segments = [1, 2, 0x10, 0x20];
            foreach (var segment in segments)
            {
                if ((mask & segment) == 0) continue;
                var category = walls.Value.WallCategory(segment);
                if (category is 3 or 5 or 6 or 15 or 23) continue;
                var crosses = segment switch
                {
                    1 => StrictlyBetween(tileX * 16, bounds.MinX, bounds.MaxX),
                    2 => StrictlyBetween(tileY * 16, bounds.MinY, bounds.MaxY),
                    0x10 => StrictlyBetween((tileX + tileY + 1) * 16,
                        bounds.MinX + bounds.MinY, bounds.MaxX + bounds.MaxY),
                    _ => StrictlyBetween((tileY - tileX) * 16,
                        bounds.MinY - bounds.MaxX, bounds.MaxY - bounds.MinX),
                };
                if (crosses) return true;
            }
        }
        return false;

        static bool StrictlyBetween(int value, int first, int second) =>
            first < value && value < second || second < value && value < first;
    }

    private bool PassesWallPlacementCheck(XObject item, int tileX, int tileY, int level)
    {
        if (!LotRoomMaps.TryGetValue(level, out var roomMap)) return true;
        var walls = roomMap.ExpandedWallAt(tileX, tileY);
        if (walls is null) return true;
        var wallSegments = walls.Value.UInt32At(0);
        var flags = unchecked((ushort)item[ObjectData.WallPlacementFlags]);
        var direction = item[ObjectData.Direction] & 7;
        // Rotating local masks by +Direction provides equivalent world-space wall checks.
        var rotation = direction >> 1;

        // Local masks 2, 1, 4, 8 correspond to WallPlacementFlags bits 0, 3, 1, 2.
        ReadOnlySpan<uint> localMasks = [2, 1, 4, 8];
        ReadOnlySpan<ushort> requiredFlags = [0x0001, 0x0008, 0x0002, 0x0004];
        ReadOnlySpan<ushort> clearFlags = [0x0100, 0x0800, 0x0200, 0x0400];
        for (var index = 0; index < localMasks.Length; index++)
        {
            var wallBit = localMasks[index];
            wallBit = RotatePlacementWallBit(wallBit, rotation);
            if ((flags & clearFlags[index]) != 0 && (wallSegments & wallBit) != 0) return false;
            if ((flags & requiredFlags[index]) == 0) continue;
            if ((wallSegments & wallBit) == 0) return false;
            var category = walls.Value.WallCategory(wallBit);
            if (category is 2 or 12 or 13 or 14) return false;
        }

        var hasDiagonal = (wallSegments & 0x30) != 0;
        if ((flags & 0x40) != 0 && !hasDiagonal) return false;
        if ((flags & 0x80) == 0 && hasDiagonal) return false;
        return true;
    }

    /// <summary>Rotates cardinal wall-placement mask bits (1, 2, 4, 8) by one position per quarter-turn.</summary>
    public static uint RotatePlacementWallBit(uint bit, int turns)
    {
        for (var turn = 0; turn < turns; turn++) bit = bit == 8 ? 1u : bit << 1;
        return bit;
    }

    public void PlaceObject(XObject item, ObjectPosition at, TreeStack? checkStack = null,
        XObject? placementContainer = null, short placementSlotNumber = 0,
        MultiTilePlacementCursorGroup? cursorGroup = null) =>
        PlaceObjectCore(item, at, checkStack, placementContainer, placementSlotNumber, directClassCallback: false, cursorGroup);

    /// <summary>Constructs a temporary multi-tile cursor group for editor placement validation without
    /// adding it to the world object manager.</summary>
    public MultiTilePlacementCursorGroup CreatePlacementCursorGroup(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return MultiTilePlacementCursorGroup.Create(item, MultiTileMembers(item));
    }

    /// <summary>Previews an existing object move in the editor, restoring object orientations and ObjM
    /// offsets after validating the proposed placement.</summary>
    public EditorPlacementResult PreviewEditorMove(XObject item, ObjectPosition at, byte directionFlag,
        TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var members = MultiTileMembers(item);
        var original = members.ToDictionary(member => member,
            member => (member[ObjectData.Direction], member.SavedMultiTileState));
        try
        {
            ApplyEditorFacing(item, members, directionFlag);
            var tiles = EditorTiles(item, at, members);
            var valid = CanPlaceObjectFromEditor(item, at, out var referenceObject, out var referenceSlot, checkStack);
            return new EditorPlacementResult(valid, valid ? "Success" : "Blocked", tiles, referenceObject, referenceSlot);
        }
        finally
        {
            RestoreEditorFacing(original);
        }
    }

    public bool MoveObjectFromEditor(XObject item, ObjectPosition at, byte directionFlag, TreeStack checkStack)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(checkStack);
        if (!CanPickUpObject(item)) return false;
        var members = MultiTileMembers(item);
        var original = members.ToDictionary(member => member,
            member => (member[ObjectData.Direction], member.SavedMultiTileState));
        var placed = false;
        try
        {
            ApplyEditorFacing(item, members, directionFlag);
            if (!CanPlaceObjectFromEditor(item, at, out var referenceObject, out var referenceSlot, checkStack)) return false;
            var cursorGroup = MultiTilePlacementCursorGroup.Create(item, members);
            var slotNumber = referenceObject is not null && referenceSlot >= 0 ? checked((short)(referenceSlot + 1)) : (short)0;
            var detachedContainers = new List<XObject>();
            foreach (var member in members)
            {
                var oldContainer = ResolveParentContainer(member) ??
                    (member[ObjectData.ContainerId] > 0
                        ? member.ObjectTableEntry(member[ObjectData.ContainerId]) ?? ObjectTableEntry(member[ObjectData.ContainerId]) ?? ObjectById(member[ObjectData.ContainerId])
                        : member[ObjectData.ParentId] > 0 && member[ObjectData.SlotHierNumber] != 0
                            ? member.ObjectTableEntry(member[ObjectData.ParentId]) ?? ObjectTableEntry(member[ObjectData.ParentId]) ?? ObjectById(member[ObjectData.ParentId])
                            : null);
                RunFunctionToCompletion(member, 10);
                if (oldContainer is not null && !ReferenceEquals(oldContainer, member))
                {
                    UnplaceObjectFromGridOrSlot(member);
                    member[ObjectData.ContainerId] = 0;
                    member[ObjectData.SlotNumber] = -1;
                    member[ObjectData.ParentId] = 0;
                    member[ObjectData.SlotHierNumber] = -1;
                    if (!detachedContainers.Contains(oldContainer))
                        detachedContainers.Add(oldContainer);
                }
            }
            foreach (var oldContainer in detachedContainers)
                RunFunctionToCompletion(oldContainer, 9);
            if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal)
            {
                RestoreEditorFacing(original);
                foreach (var member in members) RefreshPortalWallStyleOverlay(member, resetToDefault: true);
                ApplyEditorFacing(item, members, directionFlag);
            }
            PlaceObject(item, at, checkStack, placementContainer: referenceObject, placementSlotNumber: slotNumber, cursorGroup: cursorGroup);
            RunEditorUserPlacementCallbacks(members);
            WakeSimulateOnPauseObjects();
            placed = true;
            return true;
        }
        finally
        {
            if (!placed) RestoreEditorFacing(original);
        }
    }

    /// <summary>Validates editor placement, falling back on error 0x0B for single-tile objects to scanning
    /// target tile objects for an available slot permitted by AllowedHeightFlags.</summary>
    public bool CanPlaceObjectFromEditor(XObject item, ObjectPosition at, out XObject? referenceObject, out int referenceSlot,
        TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        referenceObject = null;
        referenceSlot = -1;
        checkStack ??= TreeRunner.CreateTransientStack(this, item);
        if (CanPlaceObject(item, at, checkStack)) return true;
        if (LastPlacementFailureCode != 0x0b || item.Definition[(int)ObjdField.MasterId] != 0) return false;

        var tileX = at.X >> 4;
        var tileY = at.Y >> 4;
        foreach (var candidate in ObjectsAtTile(tileX, tileY, at.Height))
        {
            if (ReferenceEquals(candidate, item)) continue;
            for (var slotIndex = 0; slotIndex < candidate.FootprintSlots.Count; slotIndex++)
            {
                var heightCode = candidate.FootprintSlots[slotIndex].HeightCode;
                var allowedHeightFlags = unchecked((ushort)item[ObjectData.AllowedHeightFlags]);
                if (heightCode == 0 ||
                    (allowedHeightFlags & (1 << ((heightCode - 1) & 0x1f))) == 0) continue;

                var slotAccepted = CanUserPlaceReferencedSlot(item, at, candidate, slotIndex);
                if (!slotAccepted) continue;

                referenceObject = candidate;
                referenceSlot = slotIndex;
                return true;
            }
        }
        var allowedMask = unchecked((ushort)item[ObjectData.AllowedHeightFlags]);
        LastPlacementFailureCode = allowedMask != 0 && (allowedMask & 0xfff7) == 0 ? 0x24 : 0x0b;
        return false;
    }

    private void ApplyEditorFacing(XObject item, IReadOnlyList<XObject> members, byte directionFlag)
    {
        var targetDirection = directionFlag switch
        {
            1 => 0,
            4 => 2,
            16 => 4,
            64 => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(directionFlag), "Direction must be 1, 4, 16 or 64."),
        };
        var delta = (targetDirection - item[ObjectData.Direction] + 8) & 7;
        foreach (var member in members)
        {
            member[ObjectData.Direction] = (short)((member[ObjectData.Direction] + delta) & 7);
            RotateContainedSlotChildren(member, delta);
        }
        if (delta != 0 && item.RuntimeClass is Formats.ObjectClass.MultiTileObject or Formats.ObjectClass.Portal)
            RefreshMultiTileOffsetsAfterRotation(item);
    }

    public void FaceCreatedObject(XObject root, IReadOnlyList<XObject> members, short direction)
    {
        var delta = (direction - root[ObjectData.Direction] + 8) & 7;
        if (delta == 0) return;
        foreach (var member in members)
        {
            member[ObjectData.Direction] = (short)((member[ObjectData.Direction] + delta) & 7);
            RotateContainedSlotChildren(member, delta);
        }
        if (root.RuntimeClass is Formats.ObjectClass.MultiTileObject or Formats.ObjectClass.Portal)
            RefreshMultiTileOffsetsAfterRotation(root);
    }

    public void RotateObjectForPlacement(XObject item, int quarterSteps)
    {
        var notches = item[ObjectData.RotationNotches];
        var delta = ((quarterSteps * notches) % 8 + 8) & 7;
        if (delta == 0) return;
        var members = MultiTileMembers(item);
        foreach (var member in members)
        {
            member[ObjectData.Direction] = (short)((member[ObjectData.Direction] + delta) & 7);
            RotateContainedSlotChildren(member, delta);
        }
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        if (lead.RuntimeClass is Formats.ObjectClass.MultiTileObject or Formats.ObjectClass.Portal)
            RefreshMultiTileOffsetsAfterRotation(lead);
    }

    private void RestoreEditorFacing(
        IReadOnlyDictionary<XObject, (short Direction, MultiTileState? State)> original)
    {
        foreach (var (member, saved) in original)
        {
            var delta = (saved.Direction - member[ObjectData.Direction] + 8) & 7;
            member[ObjectData.Direction] = saved.Direction;
            member.SavedMultiTileState = saved.State;
            if (delta != 0) RotateContainedSlotChildren(member, delta);
        }
    }

    /// <summary>Rotates contained slot children and footprint slot objects when MasterId is 0 and
    /// RotationNotches is 2 or uninitialized.</summary>
    private void RotateContainedSlotChildren(XObject member, int delta)
    {
        if (delta == 0) return;
        foreach (var child in EnumerateContainedSlotChildren(member))
        {
            if (child.Definition is not { Length: > (int)ObjdField.MasterId } ||
                child.Definition[(int)ObjdField.MasterId] == 0)
            {
                if (child[ObjectData.RotationNotches] is 0 or 2)
                    child[ObjectData.Direction] = (short)((child[ObjectData.Direction] + delta) & 7);
            }
            RotateContainedSlotChildren(child, delta);
        }
    }

    public IEnumerable<XObject> EnumerateContainedSlotChildren(XObject member)
    {
        var yielded = new HashSet<XObject>(ReferenceEqualityComparer.Instance);
        for (var slotIndex = 1; slotIndex < member.Slots.Count; slotIndex++)
        {
            var slotKey = member.Slots[slotIndex];
            if (slotKey <= 0) continue;
            var child = member.ObjectTableEntry(slotKey) ?? ObjectTableEntry(slotKey) ?? ObjectById(slotKey);
            if (child is not null && !ReferenceEquals(child, member) && yielded.Add(child))
                yield return child;
        }
        for (var i = 0; i < member.FootprintSlots.Count; i++)
        {
            var slotKey = member.FootprintSlots[i].OccupantObjectId;
            if (slotKey <= 0) continue;
            var child = member.ObjectTableEntry(slotKey) ?? ObjectTableEntry(slotKey) ?? ObjectById(slotKey);
            if (child is not null && !ReferenceEquals(child, member) && yielded.Add(child))
                yield return child;
        }
    }

    /// <summary>Resolves the enclosing slot container by skipping tile-overlap links (<c>SlotHierNumber ==
    /// 0</c>).</summary>
    internal XObject? ResolveParentContainer(XObject item)
    {
        var current = item;
        var visited = new HashSet<XObject>(ReferenceEqualityComparer.Instance) { current };
        while (current[ObjectData.SlotHierNumber] == 0 && current[ObjectData.ParentId] > 0)
        {
            var next = current.ObjectTableEntry(current[ObjectData.ParentId]) ?? ObjectTableEntry(current[ObjectData.ParentId]);
            if (next is null || !visited.Add(next)) break;
            current = next;
        }
        var containerKey = current[ObjectData.SlotHierNumber] > 0 && current[ObjectData.ParentId] > 0
            ? current[ObjectData.ParentId]
            : item[ObjectData.ContainerId];
        if (containerKey <= 0) return null;
        var container = current.ObjectTableEntry(containerKey) ?? ObjectTableEntry(containerKey) ?? ObjectById(containerKey);
        return container is not null && !ReferenceEquals(container, item) ? container : null;
    }

    private List<EditorPlacementTile> EditorTiles(XObject item, ObjectPosition requested, IReadOnlyList<XObject> members)
    {
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        var requestedOffset = MultiTileOffset(item);
        var leadOffset = MultiTileOffset(lead);
        var delegated = ReferenceEquals(item, lead)
            ? requested
            : new ObjectPosition(requested.X - requestedOffset.X, requested.Y - requestedOffset.Y,
                requested.Height - requestedOffset.Height);
        var root = new ObjectPosition(delegated.X - leadOffset.X, delegated.Y - leadOffset.Y,
            delegated.Height - leadOffset.Height);
        return members.Select(member =>
        {
            var offset = MultiTileOffset(member);
            return new EditorPlacementTile((root.X + offset.X) >> 4, (root.Y + offset.Y) >> 4,
                root.Height + offset.Height);
        }).ToList();
    }

    private void PlaceObjectCore(XObject item, ObjectPosition at, TreeStack? checkStack,
        XObject? placementContainer, short placementSlotNumber, bool directClassCallback,
        MultiTilePlacementCursorGroup? cursorGroup = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var multiTileCallback = item.RuntimeClass == SimsEngine.Formats.ObjectClass.MultiTileObject;
        var groupCallback = item.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal;
        // Route slots use 0-based indexes, whereas hierarchy storage offsets by 1.
        if (!multiTileCallback && placementContainer is not null && placementSlotNumber > 0 &&
            placementSlotNumber <= placementContainer.FootprintSlots.Count)
        {
            while (placementContainer.Slots.Count <= placementSlotNumber)
                placementContainer.Slots.Add(0);
        }
        if (!multiTileCallback && placementContainer is not null &&
            (placementSlotNumber < 0 || placementSlotNumber >= placementContainer.Slots.Count))
            throw new InvalidOperationException("The accepted placement request names no allocated parent slot.");
        var members = groupCallback ? MultiTileMembers(item) : [item];
        var originalPositions = members.ToDictionary(member => member, member => member.Position);
        // Relative placement uses the referenced container's position.
        var placementPosition = !multiTileCallback && placementContainer is not null
            ? placementContainer.Position
            : at;
        var containerTree = multiTileCallback || placementContainer is null ? (short)0 : FunctionTreeId(placementContainer, 9);
        var containerScope = placementContainer?.TypeRecord?.BehaviorScope;
        foreach (var member in members)
        {
            var tree = FunctionTreeId(member, 9);
            var scope = member.TypeRecord?.BehaviorScope;
            var runsMemberFunctionNine = tree != 0 && member.Definition[(int)ObjdField.MasterId] == 0;
            var runsTrailingFunctionNine = groupCallback && tree != 0;
            var runsFunctionSix = FunctionTreeId(member, 6) != 0;
            if (((runsMemberFunctionNine || runsTrailingFunctionNine || runsFunctionSix) && scope is null) ||
                (placementContainer is not null && containerTree != 0 && containerScope is null) ||
                ((runsMemberFunctionNine || runsTrailingFunctionNine || runsFunctionSix ||
                  (placementContainer is not null && containerTree != 0)) && checkStack is null))
                throw new NotSupportedException("Placing this object requires its placement BHAV and active stack.");
        }

        var priorContainers = new List<XObject>();
        var memberContainer = multiTileCallback ? null : placementContainer;
        var memberSlot = multiTileCallback ? (short)0 : placementSlotNumber;
        void RecordPriorContainer(XObject oldContainer)
        {
            if (!priorContainers.Contains(oldContainer))
                priorContainers.Add(oldContainer);
        }

        void ApplyMemberCallbacks(XObject member)
        {
            RefreshPlacementWallAdjacency(member, checkStack);
            var tree = FunctionTreeId(member, 9);
            if (tree != 0 && member.Definition[(int)ObjdField.MasterId] == 0)
            {
                if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal && placementContainer is not null)
                    TreeRunner.RunWithParameters(this, placementContainer, member.TypeRecord?.BehaviorScope, tree, 0, []);
                else if (checkStack is not null && checkStack.GetType().DeclaringType != typeof(TreeRunner))
                    checkStack.RunToReturn(member.TypeRecord?.BehaviorScope, [], false, tree, 0);
                else
                    TreeRunner.RunWithParameters(this, member, member.TypeRecord?.BehaviorScope, tree, 0, []);
            }
            if (!multiTileCallback && placementContainer is not null && containerTree != 0)
            {
                // Runs function 9 with 'me' bound to the container so slot queries target it.
                TreeRunner.RunWithParameters(this, placementContainer, containerScope, containerTree, 0, []);
            }
            if (originalPositions.TryGetValue(member, out var original) &&
                (original.X != member.Position.X || original.Y != member.Position.Y))
                member.PositionRefreshPending = true;
        }

        var placedMembers = PlaceObjectOrMultiTileGroup(item, placementPosition,
            groupCallback ? ApplyMemberCallbacks : null, placementSlotNumber, checkStack, directClassCallback,
            cursorGroup, memberContainer, memberSlot, RecordPriorContainer);
        if (groupCallback)
        {
            foreach (var member in placedMembers)
            {
                var tree = FunctionTreeId(member, 9);
                if (tree != 0)
                {
                    if (checkStack is not null && checkStack.GetType().DeclaringType != typeof(TreeRunner))
                        checkStack.RunToReturn(member.TypeRecord?.BehaviorScope, [], false, tree, 0);
                    else
                        TreeRunner.RunWithParameters(this, member, member.TypeRecord?.BehaviorScope, tree, 0, []);
                }
            }
        }
        else ApplyMemberCallbacks(item);
        foreach (var oldContainer in priorContainers)
            RunFunctionToCompletion(oldContainer, 9);
        if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal)
        {
            foreach (var member in placedMembers) RefreshPortalWallStyleOverlay(member);
            foreach (var portal in WalkOrder.Where(candidate => candidate.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal))
                portal.ResetAllocatedPortalRouteCosts();
        }
    }

    public void RefreshSavedPortalWallStyles()
    {
        foreach (var portal in WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal).ToArray())
        {
            RefreshPortalWallStyleOverlay(portal);
        }
    }

    private void RefreshPortalWallStyleOverlay(XObject member, bool resetToDefault = false)
    {
        var sourceWallStyle = member.Definition[(int)ObjdField.WallStyle];
        if (sourceWallStyle == 0) return;
        var wallStyle = resetToDefault ? (ushort)1 : unchecked((ushort)sourceWallStyle);
        var localMask = member[ObjectData.WallPlacementFlags] & 5;
        var wallBit = localMask switch { 1 => 2u, 4 => 8u, _ => 0u };
        if (wallBit == 0) return;
        wallBit = RotatePlacementWallBit(wallBit, (member[ObjectData.Direction] & 7) / 2);
        var tileX = member.Position.X >> 4;
        var tileY = member.Position.Y >> 4;
        if (tileX <= 0 || tileY <= 0 || !LotRoomMaps.TryGetValue(member.Position.Height, out var roomMap) ||
            tileX >= roomMap.Width - 1 || tileY >= roomMap.Height - 1) return;
        var walls = roomMap.ExpandedWallAt(tileX, tileY);
        if (walls is not { } expanded) return;
        var refreshedWall = expanded.WithWallStyleOverlay(wallStyle, wallBit);
        roomMap.ApplyWallStyleOverlay(tileX, tileY, wallStyle, wallBit, refreshedWall);
        if (resetToDefault)
        {
            MarkPortalWallRefreshRequested(member.Position.Height, tileX, tileY);
        }
        else
        {
            MarkPortalGeometryRefreshRequested(member.Position.Height);
        }
    }

    /// <summary>Refreshes the object's wall-adjacency flags and runs function 6 only when the adjacency
    /// changes.</summary>
    private void RefreshPlacementWallAdjacency(XObject item, TreeStack? checkStack)
    {
        var tree = FunctionTreeId(item, 6);
        if (tree == 0) return;
        var flags = unchecked((ushort)item[ObjectData.WallAdjacencyFlags]) & 0xffc0;
        var position = item.Position;
        if (LotRoomMaps.TryGetValue(position.Height, out var current) &&
            (position.X >> 4) > 0 && (position.Y >> 4) > 0 &&
            (position.X >> 4) < current.Width - 1 && (position.Y >> 4) < current.Height - 1)
        {
            var tileX = position.X >> 4;
            var tileY = position.Y >> 4;
            var direction = item[ObjectData.Direction] & 7;
            var rotation = ((-direction / 2) & 3);
            var walls = current.ExpandedWallAt(tileX, tileY);
            if (walls is not null)
            {
                var localWalls = walls.Value.WithRotatedSides(rotation);
                foreach (var (localBit, resultFlag) in new (uint Bit, ushort Flag)[]
                         { (1, 1), (2, 4), (4, 2), (8, 8) })
                {
                    if ((localWalls.UInt32At(0) & localBit) == 0) continue;
                    var category = localWalls.WallCategory(localBit);
                    if (category is not (2 or 12 or 13 or 14)) flags |= resultFlag;
                }

                if (position.Height < 1 && LotRoomMaps.TryGetValue(position.Height + 1, out var above))
                {
                    var upperWalls = above.ExpandedWallAt(tileX, tileY);
                    if (upperWalls is not null)
                    {
                        var localUpperWalls = upperWalls.Value.WithRotatedSides(rotation);
                        foreach (var (localBit, resultFlag) in new (uint Bit, ushort Flag)[] { (1, 0x10), (4, 0x20) })
                        {
                            if ((localUpperWalls.UInt32At(0) & localBit) == 0) continue;
                            var category = localUpperWalls.WallCategory(localBit);
                            if (category is not (2 or 12 or 13 or 14)) flags |= resultFlag;
                        }
                    }
                }
            }
        }

        if (flags == unchecked((ushort)item[ObjectData.WallAdjacencyFlags])) return;
        if (checkStack is null) throw new NotSupportedException("Placement wall-adjacency refresh requires the active tree stack.");
        item[ObjectData.WallAdjacencyFlags] = unchecked((short)flags);
        checkStack.RunToReturn(item.TypeRecord?.BehaviorScope, [], false, tree, 0);
    }

    public void PlaceCreatedObject(XObject created, ObjectPosition at)
    {
        ArgumentNullException.ThrowIfNull(created);
        PlaceObjectOrMultiTileGroup(created, at);
    }

    /// <summary>Commits editor object placement, placing all members and invoking function 11 on each
    /// member and its contained slot children.</summary>
    public void PlaceCreatedObjectFromEditor(XObject created, ObjectPosition at, XObject? referenceObject = null, int referenceSlot = -1)
    {
        ArgumentNullException.ThrowIfNull(created);
        var members = MultiTileMembers(created);
        var checkStack = TreeRunner.CreateTransientStack(this, created);
        var cursorGroup = MultiTilePlacementCursorGroup.Create(created, members);
        var slotNumber = referenceObject is not null && referenceSlot >= 0 ? checked((short)(referenceSlot + 1)) : (short)0;
        PlaceObject(created, at, checkStack, placementContainer: referenceObject, placementSlotNumber: slotNumber, cursorGroup: cursorGroup);
        RunEditorUserPlacementCallbacks(members);
        WakeSimulateOnPauseObjects();
    }

    /// <summary>Runs function 11 for each placed member and each contained slot child after placement
    /// succeeds.</summary>
    private void RunEditorUserPlacementCallbacks(IReadOnlyList<XObject> members)
    {
        var callerLocation = CaptureExecutionLocation();
        try
        {
            foreach (var member in members)
            {
                RunUserPlacementCallbackOnObject(member);
                foreach (var child in EnumerateContainedSlotChildren(member))
                    RunUserPlacementCallbackOnObject(child);
            }
        }
        finally
        {
            RestoreExecutionLocation(callerLocation);
        }

        void RunUserPlacementCallbackOnObject(XObject target)
        {
            var tree = FunctionTreeId(target, 11);
            var scope = target.TypeRecord?.BehaviorScope;
            if (tree == 0 || scope is null) return;
            TreeRunner.RunWithParameters(this, target, scope, tree, 0, []);
        }
    }

    /// <summary>Wakes objects with SimulateOnPause enabled when they are waiting on Sleep (opcode 0) or
    /// Idle For Input (opcode 0x11).</summary>
    public void WakeSimulateOnPauseObjects()
    {
        foreach (var item in WalkOrder)
        {
            if (item.ClockManagerIndex <= 0) continue;
            var simulateOnPause = item[ObjectData.SimulateOnPause] != 0 ||
                                  (ClockManager.FlagsAt(item.ClockManagerIndex) & 0x00020000u) != 0;
            if (!simulateOnPause) continue;
            switch (WaitInstructionOpcode(item))
            {
                case 0:
                    ClearWaitCounter(item);
                    ClockManager.SetWaitTicksAt(item.ClockManagerIndex, 0);
                    break;
                case 0x11:
                    ClearWaitCounter(item);
                    break;
            }
        }
    }

    private const uint WeatherControlGuid = 0xE4B85D21;

    /// <summary>Places a newly created object into the holder's first real slot (hierarchy slot 1),
    /// reserving hierarchy slot 0 for tile-overlap links.</summary>
    public bool PlaceCreatedObjectInHand(XObject holder, XObject created)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(created);
        if (ReferenceEquals(holder, created) || created[ObjectData.ContainerId] != 0 ||
            holder[ObjectData.ContainerId] == created.ClockManagerIndex) return false;
        return TryPlaceInFirstRealSlot(holder, created);
    }

    /// <summary>Moves an object into a holder's first real slot (hierarchy slot 1), while
    /// <c>ObjectData.SlotNumber</c> remains 0.</summary>
    public bool TryPlaceInFirstRealSlot(XObject holder, XObject item)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(item);
        if (ReferenceEquals(holder, item) || IsInSlotSubtree(holder, item)) return false;
        // Objects must have an authored slot entry at index 1 to hold items in hand.
        if (holder.Slots.Count <= 1) return false;

        var heldKey = item.ClockManagerIndex;
        if (holder.Slots.Count > 1 && holder.Slots[1] == heldKey &&
            item[ObjectData.ContainerId] == holder.ClockManagerIndex &&
            item[ObjectData.ParentId] == holder.ClockManagerIndex &&
            item[ObjectData.SlotHierNumber] == 1) return true;
        if (holder.Slots.Count > 1 && holder.Slots[1] != 0) return false;

        var visited = new HashSet<short> { holder.ClockManagerIndex };
        XObject? ancestor = holder;
        while (ancestor is { } current && current[ObjectData.ParentId] != 0)
        {
            var slotHier = current[ObjectData.SlotHierNumber];
            ancestor = ObjectByStackObject(current, current[ObjectData.ParentId]);
            var isTileOverlap = slotHier == 0 && ancestor is not null &&
                                ancestor.Slots.Count > 0 && ancestor.Slots[0] == current.ClockManagerIndex;
            if (ancestor is null || !visited.Add(ancestor.ClockManagerIndex) ||
                (!isTileOverlap && ReferenceEquals(ancestor, item)))
                return false;
        }

        if (item[ObjectData.ParentId] != 0)
        {
            var priorContainer = ObjectByStackObject(item, item[ObjectData.ParentId]);
            var priorSlot = item[ObjectData.SlotHierNumber];
            if (priorContainer is null || priorSlot < 0 || priorSlot >= priorContainer.Slots.Count ||
                priorContainer.Slots[priorSlot] != heldKey) return false;
        }

        UnplaceObjectFromGridOrSlot(item);
        holder.Slots[1] = heldKey;
        item[ObjectData.ContainerId] = holder.ClockManagerIndex;
        item[ObjectData.ParentId] = holder.ClockManagerIndex;
        item[ObjectData.SlotNumber] = 0;
        item[ObjectData.SlotHierNumber] = 1;
        SetObjectPosition(item, holder.Position);
        return true;
    }

    private List<XObject> PlaceObjectOrMultiTileGroup(XObject item, ObjectPosition at,
        Action<XObject>? placementCallback = null, short sourceParam5 = 0,
        TreeStack? checkStack = null, bool directClassCallback = false,
        MultiTilePlacementCursorGroup? cursorGroup = null,
        XObject? placementContainer = null, short placementSlotNumber = 0,
        Action<XObject>? onPriorContainerDetached = null)
    {
        if (item.RuntimeClass is not (SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal))
        {
            if (!directClassCallback) UpdateMultiTileMoveRequest(item, at, sourceParam5);
            MoveTileRoot(item, at, placementContainer, placementSlotNumber, onPriorContainerDetached);
            placementCallback?.Invoke(item);
            return [item];
        }

        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId);
        if (lead is null)
        {
            UpdateMultiTileMoveRequest(item, at, sourceParam5);
            MoveTileRoot(item, at, placementContainer, placementSlotNumber, onPriorContainerDetached);
            placementCallback?.Invoke(item);
            return [item];
        }

        var wrapperRoot = !directClassCallback && ReferenceEquals(item, lead);
        if (wrapperRoot) UpdateMultiTileMoveRequest(lead, at, sourceParam5);

        if (wrapperRoot)
            lead.MultiTileMoveFlag1B0 = lead.MultiTileMoveAssociatedObject is null ? (short)1 : (short)0;
        var associatedMove = cursorGroup?.AssociatedObjectFor(lead) ?? lead.MultiTileMoveAssociatedObject;
        if (wrapperRoot && !_activeAssociatedPlacementCallbacks.Contains(lead.Id) &&
            associatedMove is { } associated)
        {
            if (TryApplyAssociatedPlacement(lead, associated, at, checkStack, out var referenceObject,
                    out var referenceSlot))
            {
                var associatedValue = associated.Definition[(int)ObjdField.MasterId] == 0
                    ? 0
                    : associated.TypeRecord?.AlternativeObjectPrice ?? 0;
                var placementContextValue = FamilyBudget?.ReadFamilyBudget() ?? 0;
                if (placementContextValue < associatedValue && associatedValue != 0 &&
                    (cursorGroup?.HasError15Gate(lead) ?? false))
                    LastPlacementFailureCode = 0x15;
                else
                    lead.MultiTileMoveFlag1B0 = 1;
                _activeAssociatedPlacementCallbacks.Add(lead.Id);
                try
                {
                    if (associated is XPerson)
                        PlaceObjectCore(associated, at, checkStack, referenceObject,
                            referenceObject is not null && referenceSlot >= 0
                                ? checked((short)(referenceSlot + 1))
                                : (short)0,
                            directClassCallback: true, cursorGroup);
                    else
                        PlaceObjectCore(associated, at, checkStack,
                            referenceObject is not null && referenceSlot >= 0 ? referenceObject : null,
                            referenceObject is not null && referenceSlot >= 0
                                ? checked((short)(referenceSlot + 1))
                                : (short)0,
                            directClassCallback:
                            associated.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal,
                            cursorGroup);
                }
                finally
                {
                    _activeAssociatedPlacementCallbacks.Remove(lead.Id);
                }
                RefreshAssociatedPlacementHelper(associated);
            }
        }

        var requestedOffset = MultiTileOffset(item);
        var delegatedTarget = ReferenceEquals(item, lead)
            ? at
            : new ObjectPosition(at.X - requestedOffset.X, at.Y - requestedOffset.Y,
                at.Height - requestedOffset.Height);
        var leadOffset = MultiTileOffset(lead);
        var visited = new HashSet<short>();
        var movedParts = new List<XObject>();
        var delegatedTileX = delegatedTarget.X >> 4;
        var delegatedTileY = delegatedTarget.Y >> 4;
        var targetInLot = delegatedTileX is > 0 and < LotGeometry.LotDimension - 1 &&
                          delegatedTileY is > 0 and < LotGeometry.LotDimension - 1;
        for (var part = lead; part is not null && visited.Add(part.Id);)
        {
            var nextId = part.MultiTileNextId;
            var partOffset = MultiTileOffset(part);
            var destination = new ObjectPosition(
                delegatedTarget.X - leadOffset.X + partOffset.X,
                delegatedTarget.Y - leadOffset.Y + partOffset.Y,
                delegatedTarget.Height - leadOffset.Height + partOffset.Height);
            if (targetInLot)
            {
                MoveTileRoot(part, destination, placementContainer, placementSlotNumber, onPriorContainerDetached);
                placementCallback?.Invoke(part);
            }
            else
                BeginObjectRemoval(part, 1);
            movedParts.Add(part);
            part = nextId == 0 ? null : ObjectById(nextId);
        }

        if (wrapperRoot)
        foreach (var part in movedParts)
        {
            part.MultiTileMovePositionState = part.Position;
            part.MultiTileMoveFlag1B0 = lead.MultiTileMoveFlag1B0;
            part.MultiTileMoveValue1C0 = -1.0f;
        }
        return movedParts;
    }

    private bool TryApplyAssociatedPlacement(XObject mover, XObject associated, ObjectPosition requested,
        TreeStack? checkStack, out XObject? referenceObject, out int referenceSlot)
    {
        referenceObject = ObjectTableEntry(mover.MultiTileMoveValue1C4);
        referenceSlot = mover.MultiTileMoveValue1C6;
        var accepted = referenceObject is null
            ? CanPlaceObject(associated, requested, checkStack)
            : CanUserPlaceReferencedSlot(associated, requested, referenceObject, referenceSlot);
        if (accepted) return true;

        if (LastPlacementFailureCode != 0x0b || associated.Definition[(int)ObjdField.MasterId] != 0)
            return false;

        var tileX = requested.X >> 4;
        var tileY = requested.Y >> 4;
        foreach (var candidate in ObjectsAtTile(tileX, tileY, requested.Height))
        {
            for (var slotIndex = 0; slotIndex < candidate.FootprintSlots.Count; slotIndex++)
            {
                var heightCode = candidate.FootprintSlots[slotIndex].HeightCode;
                var allowedHeightFlags = unchecked((ushort)associated[ObjectData.AllowedHeightFlags]);
                if (heightCode == 0 ||
                    (allowedHeightFlags & (1 << ((heightCode - 1) & 0x1f))) == 0) continue;

                var slotAccepted = CanUserPlaceReferencedSlot(associated, requested, candidate, slotIndex);
                if (!slotAccepted) continue;

                mover.MultiTileMoveValue1C4 = candidate.ClockManagerIndex;
                mover.MultiTileMoveValue1C6 = checked((short)slotIndex);
                referenceObject = candidate;
                referenceSlot = slotIndex;
                if (associated[ObjectData.RotationNotches] != 0)
                {
                    var candidateDirection = AssociatedPlacementDirection(candidate, requested);
                    var directionDelta = candidateDirection - associated[ObjectData.Direction];
                    var adjustment = directionDelta / associated[ObjectData.RotationNotches];
                    ApplyAssociatedPlacementAdjustment(mover, adjustment, checkStack);
                }
                return true;
            }
        }
        // Reports error 0x24 if AllowedHeightFlags matches slot height, otherwise 0x0B.
        var allowedMask = unchecked((ushort)associated[ObjectData.AllowedHeightFlags]);
        LastPlacementFailureCode = allowedMask != 0 && (allowedMask & 0xfff7) == 0 ? 0x24 : 0x0b;
        return false;
    }

    /// <summary>Advances candidate direction in quarter-turns while adjacent tiles remain clear, restoring
    /// the original direction if all four checks pass.</summary>
    private int AssociatedPlacementDirection(XObject candidate, ObjectPosition requested)
    {
        var originalDirection = candidate[ObjectData.Direction] & 7;
        if (candidate.Definition[(int)ObjdField.MasterId] == 0) return originalDirection;
        var direction = originalDirection;
        var clearSteps = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (x, y) = direction switch
            {
                0 => (-1, 0), 1 => (-1, 1), 2 => (0, 1), 3 => (1, 1),
                4 => (1, 0), 5 => (1, -1), 6 => (0, -1), _ => (-1, -1),
            };
            var adjacent = new ObjectPosition(candidate.Position.X + (x << 4),
                candidate.Position.Y + (y << 4), requested.Height);
            if (!IsAssociatedOffsetTileClear(adjacent)) break;
            direction = (direction + 2) & 7;
            clearSteps++;
        }
        return clearSteps == 4 ? originalDirection : direction;
    }

    private bool IsAssociatedOffsetTileClear(ObjectPosition position)
    {
        foreach (var item in ObjectsAtTile(position.X >> 4, position.Y >> 4, position.Height))
        {
            var flags = unchecked((ushort)item[ObjectData.Flags]);
            var clear = (flags & 0x0004) == 0 ? (flags & 0x0010) != 0 : (flags & 0x0002) == 0;
            if (!clear) return false;
        }
        return true;
    }

    /// <summary>Removes a linked object group, applies rotation notches, revalidates placement at the
    /// anchor, and reapplies placement.</summary>
    private void ApplyAssociatedPlacementAdjustment(XObject mover, int directionDelta, TreeStack? checkStack)
    {
        var group = MultiTileMembers(mover);
        var savedPosition = mover.Position;

        foreach (var member in group)
        {
            var tree = FunctionTreeId(member, 10);
            if (tree == 0) continue;
            if (checkStack is null || member.TypeRecord?.BehaviorScope is not { } scope)
                throw new NotSupportedException("Associated multi-tile adjustment requires function 10's active tree stack.");
            checkStack.RunToReturn(scope, [], false, tree, 0);
        }
        foreach (var member in group)
        {
            if (member.Definition[(int)ObjdField.MasterId] == 0 && FunctionTreeId(member, 10) is var tree && tree != 0)
            {
                if (checkStack is null || member.TypeRecord?.BehaviorScope is not { } scope)
                    throw new NotSupportedException("Associated multi-tile adjustment requires function 10's active tree stack.");
            }
            BeginObjectRemoval(member, 1);
        }
        RefreshInteractionAfterObjectRemoval(mover);

        var rotation = (unchecked((ushort)mover[ObjectData.RotationNotches]) * directionDelta) & 7;
        if (rotation == 0) return;
        foreach (var member in group)
        {
            member[ObjectData.Direction] = (short)((member[ObjectData.Direction] + rotation) & 7);
            SetObjectPosition(member, member.Position);
        }
        RefreshMultiTileOffsetsAfterRotation(mover);

        if (!CanPlaceObject(mover, savedPosition, checkStack) &&
            !IsInsidePlacementLot(savedPosition))
        {
            foreach (var member in group)
            {
                member[ObjectData.Direction] = (short)((member[ObjectData.Direction] - rotation) & 7);
                SetObjectPosition(member, member.Position);
            }
            RefreshMultiTileOffsetsAfterRotation(mover);
        }
        if (CanPlaceObject(mover, savedPosition, checkStack))
            PlaceObjectCore(mover, savedPosition, checkStack, placementContainer: null,
                placementSlotNumber: 0, directClassCallback: true);
    }

    /// <summary>Rebuilds linked member ObjM offsets relative to the lead object, rotating the horizontal
    /// offset pair by the lead's current direction.</summary>
    private void RefreshMultiTileOffsetsAfterRotation(XObject item)
    {
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        var leadState = lead.SavedMultiTileState;
        if (leadState is null) return;
        var direction = lead[ObjectData.Direction] & 7;
        foreach (var member in MultiTileMembers(lead))
        {
            if (member.SavedMultiTileState is not { } state) continue;
            var x = state.Value0x194 - leadState.Value0x194;
            var y = state.Value0x198 - leadState.Value0x198;
            (x, y) = direction switch
            {
                0 => (x, y),
                2 => (-y, x),
                4 => (-x, -y),
                6 => (y, -x),
                _ => (0, 0),
            };
            member.SavedMultiTileState = state with
            {
                Value0x1A0 = x,
                Value0x1A4 = y,
                Value0x1A8 = state.Value0x19C - leadState.Value0x19C,
            };
        }
    }

    private static bool IsInsidePlacementLot(ObjectPosition position)
    {
        var x = position.X >> 4;
        var y = position.Y >> 4;
        return x > 0 && y > 0 && x < LotGeometry.LotDimension - 1 && y < LotGeometry.LotDimension - 1;
    }

    private static void RefreshAssociatedPlacementHelper(XObject associated)
    {
        associated.AssociatedPlacementHelper?.RefreshAfterMove();
    }

    private static void UpdateMultiTileMoveRequest(XObject item, ObjectPosition requested, short sourceParam5)
    {
        if (item.MultiTileMovePositionState is { } previous && previous == requested)
            item.MultiTileMoveValue1C6 = sourceParam5;
        else
        {
            item.MultiTileMoveValue1C4 = 0;
            item.MultiTileMoveValue1C6 = 0;
        }
    }

    internal List<XObject> MultiTileMembers(XObject item)
    {
        var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
        var members = new List<XObject>();
        var visited = new HashSet<short>();
        for (var part = lead; part is not null && visited.Add(part.Id);)
        {
            members.Add(part);
            part = part.MultiTileNextId == 0 ? null : ObjectById(part.MultiTileNextId);
        }
        return members;
    }

    private static ObjectPosition MultiTileOffset(XObject part)
    {
        var state = part.SavedMultiTileState;
        return new ObjectPosition((state?.Value0x1A0 ?? 0) << 4, (state?.Value0x1A4 ?? 0) << 4, state?.Value0x1A8 ?? 0);
    }

    /// <summary>Records SimAntics thread errors in occurrence order; failing object threads are dropped.</summary>
    public List<ThreadError> ThreadErrors { get; } = [];
    /// <summary>Check-tree errors encountered while evaluating interaction candidates; rejected candidates
    /// do not terminate the person's thread.</summary>
    public List<InteractionGuardError> InteractionGuardErrors { get; } = [];
    public bool CaptureExecutionTrace { get; set; }
    public List<SimulationTraceEntry> ExecutionTrace { get; } = [];
    public List<AutonomyAttemptDiagnostic> AutonomyAttemptTrace { get; } = [];
    public List<SimulationPrimitiveResultTrace> PrimitiveResultTrace { get; } = [];
    public List<SimulationRouteCandidateTrace> RouteCandidateTrace { get; } = [];
    internal string LastRouteCandidateFailureReason { get; set; } = string.Empty;
    public List<SimulationNativeRouteTrace> NativeRouteTrace { get; } = [];
    public List<SimulationRouteSearchTrace> RouteSearchTrace { get; } = [];
    public List<SimulationTreeCallTrace> TreeCallTrace { get; } = [];
    public List<SimulationNamedTreeLookupTrace> NamedTreeLookupTrace { get; } = [];
    public List<SimulationInteractionTestTrace> InteractionTestTrace { get; } = [];

    /// <summary>The language ID used for STR# resource lookups (1 is English).</summary>
    public byte Language { get; set; } = 1;

    /// <summary>Resolves a string table entry from the stack object's behavior scope, falling back to the
    /// current object when absent.</summary>
    public string? StringTableEntry(XObject? stackObject, XObject me, short table, int index)
    {
        foreach (var candidate in new[] { stackObject?.TypeRecord?.BehaviorScope, me.TypeRecord?.BehaviorScope })
        {
            if (candidate is not Content.BehaviorScope scope) continue;
            foreach (var file in new[] { scope.Private, scope.SemiGlobal, scope.Global })
            {
                if (file is null) continue;
                var chunk = file.Find(ResourceType.Str, unchecked((ushort)table));
                if (chunk is null) continue;
                var strings = StringTable.Read(file, chunk).ForLanguage(Language);
                return index >= 0 && index < strings.Count ? strings[index] : null;
            }
        }
        return null;
    }

    /// <summary>Resolves the lot object ID matching the neighbor's object type, or 0 if missing.</summary>
    public short NeighborObjectKey(short oneBasedIndex)
    {
        var neighbor = NeighborManagerEntry(oneBasedIndex);
        if (neighbor is null || neighbor.TypeRecordKey == 0) return 0;
        foreach (var item in WalkOrder)
            if (item.TypeRecord?.Key == neighbor.TypeRecordKey) return item.ClockManagerIndex;
        return 0;
    }

    /// <summary>Determines whether the neighbor belongs to the family currently residing on the lot.</summary>
    public short NeighborBelongsToHouse(short oneBasedIndex)
    {
        var neighbor = NeighborManagerEntry(oneBasedIndex);
        if (neighbor is null || NeighborInteractionManagerTypeKeys.Count == 0) return 0;
        return NeighborInteractionManagerTypeKeys.Contains(neighbor.TypeRecordKey) ? (short)1 : (short)0;
    }

    public short NeighborWordE8(short oneBasedIndex) =>
        NeighborManagerEntry(oneBasedIndex) is { } neighbor ? neighbor.PersonData[58] : (short)0;

    /// <summary>Counts friend-eligible neighbors whose mutual relationship scores both meet or exceed the
    /// friendship threshold.</summary>
    public short NeighborFriendCount(short oneBasedIndex)
    {
        var subject = NeighborManagerEntry(oneBasedIndex);
        if (subject is null) return 0;
        if (subject.RelationshipsChangedFlag24)
        {
            var count = 0;
            foreach (var candidate in NeighborWalkOrder)
            {
                if (candidate is null || ReferenceEquals(candidate, subject)) continue;
                if (candidate.FriendEligibilityObjectFlagsB4 is not { } flags || (flags & 0xC) != 4) continue;
                if (candidate.PersonData[61] == 0) continue;
                if (subject.Relationships.Count(candidate.Id) == 0 || candidate.Relationships.Count(subject.Id) == 0) continue;
                if (subject.Relationships.Get(candidate.Id, 0) < FriendshipThreshold ||
                    candidate.Relationships.Get(subject.Id, 0) < FriendshipThreshold) continue;
                count++;
            }
            subject.FriendCountWord20 = count;
        }
        return unchecked((short)subject.FriendCountWord20);
    }

    /// <summary>Returns the house number of the family that contains the neighbor's GUID, or 0 if unhoused.</summary>
    public short NeighborInteractionValue(short oneBasedIndex)
    {
        if (NeighborhoodFamilies is { } families && NeighborManagerEntry(oneBasedIndex) is { } neighborState)
            return families.HouseNumberForMember(neighborState.TypeRecordKey);
        var neighbor = NeighborTableEntry(oneBasedIndex);
        if (neighbor is null) return 0;
        foreach (var item in InteractionLookupOrder)
            if (item.HasNeighborSubobjectReference(neighbor))
                return unchecked((short)item.Position.Height);
        return 0;
    }

    /// <summary>Returns the specified flag bit (0 or 1) from the family flags of the neighbor's household
    /// if they reside in a house.</summary>
    public short NeighborInteractionFlag(short oneBasedIndex, int flagBit)
    {
        if (flagBit is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(flagBit));
        var neighbor = NeighborManagerEntry(oneBasedIndex);
        if (neighbor is null || neighbor.PersonData[61] == 0) return 0;
        var familyNumber = neighbor.PersonData[61];
        if (NeighborhoodFamilies is { } families && families.Exists(familyNumber))
            return families.HouseNumber(familyNumber) != 0 ? (short)((families.Flags(familyNumber) >> flagBit) & 1) : (short)0;
        var interactionObject = FindInteractionObjectByTableWord(familyNumber);
        if (interactionObject is null || interactionObject.Position.Height == 0) return 0;
        return (short)((interactionObject.NeighborInteractionFlagsWord138 >> flagBit) & 1);
    }

    /// <summary>Counts eligible mutual friends for the neighbor's family.</summary>
    public short NeighborInteractionCount(short oneBasedIndex)
    {
        if (NeighborhoodFamilies is { } families && NeighborManagerEntry(oneBasedIndex) is { } neighborState)
        {
            var familyNumber = neighborState.PersonData[61];
            if (familyNumber == 0 || !families.Exists(familyNumber)) return 0;
            return unchecked((short)CountFamilyFriends(families, familyNumber));
        }
        var neighbor = NeighborTableEntry(oneBasedIndex);
        if (neighbor is null || neighbor.NeighborInteractionTargetWordEE == 0) return 0;
        var interactionObject = FindInteractionObjectByTableWord(neighbor.NeighborInteractionTargetWordEE);
        if (interactionObject is null) return 0;
        if (interactionObject.NeighborInteractionStateByte124 != 0)
        {
            interactionObject.NeighborInteractionCountWord120 = CountNeighborInteractions(interactionObject);
            interactionObject.NeighborInteractionStateByte124 = 0;
        }
        return unchecked((short)interactionObject.NeighborInteractionCountWord120);
    }

    public int CountFamilyFriends(INeighborhoodFamilies families, short familyNumber)
    {
        var memberGuids = families.MemberGuids(familyNumber);
        if (memberGuids.Count == 0) return 0;
        var count = 0;
        foreach (var candidate in NeighborWalkOrder)
        {
            if (candidate is null || families.HasMember(familyNumber, candidate.TypeRecordKey) ||
                candidate.PersonData[61] == 0) continue;

            foreach (var memberGuid in memberGuids)
            {
                var memberNeighbor = NeighborWalkOrder.FirstOrDefault(item => item is not null && item.TypeRecordKey == memberGuid);
                if (memberNeighbor is null) continue;
                if (candidate.Relationships.Count(memberNeighbor.Id) <= 0 ||
                    memberNeighbor.Relationships.Count(candidate.Id) <= 0) continue;
                if (candidate.Relationships.Get(memberNeighbor.Id, 0) < FriendshipThreshold ||
                    memberNeighbor.Relationships.Get(candidate.Id, 0) < FriendshipThreshold ||
                    candidate.PersonData[65] >= 2) continue;

                count++;
                break;
            }
        }
        return count;
    }

    /// <summary>Counts non-family neighbors who maintain a relationship score at or above the friendship
    /// threshold with at least one family member.</summary>
    public static int CountFamilyFriends(Family family, Neighbors neighbors, int friendshipThreshold = 50)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(neighbors);
        return CountFamilyFriends(family.Members, neighbors.Entries, friendshipThreshold);
    }

    /// <summary>Counts non-family neighbors who maintain a relationship score at or above the friendship
    /// threshold with at least one family member GUID.</summary>
    public static int CountFamilyFriends(IReadOnlyList<uint> familyMemberGuids, IReadOnlyList<Neighbor> neighbors,
        int friendshipThreshold = 50)
    {
        ArgumentNullException.ThrowIfNull(familyMemberGuids);
        ArgumentNullException.ThrowIfNull(neighbors);
        if (familyMemberGuids.Count == 0 || neighbors.Count == 0) return 0;

        var memberSet = new HashSet<uint>(familyMemberGuids);
        var membersByGuid = new Dictionary<uint, Neighbor>();
        foreach (var entry in neighbors)
        {
            if (memberSet.Contains(entry.Guid) && !membersByGuid.ContainsKey(entry.Guid))
                membersByGuid[entry.Guid] = entry;
        }

        var enforceFamilyNumber = neighbors.Any(entry => entry.PersonData is { Count: > 61 } pd && pd[61] != 0);
        var hasMemberSideRelationships = neighbors.Any(entry => memberSet.Contains(entry.Guid) && entry.Relationships.Count > 0);
        var hasCandidateSideRelationships = neighbors.Any(entry => !memberSet.Contains(entry.Guid) && entry.Relationships.Count > 0);
        var requireMutual = hasMemberSideRelationships && hasCandidateSideRelationships;

        var count = 0;
        foreach (var candidate in neighbors)
        {
            if (memberSet.Contains(candidate.Guid)) continue;
            if (enforceFamilyNumber && (candidate.PersonData is not { Count: > 61 } familyPd || familyPd[61] == 0))
                continue;
            if (candidate.PersonData is { Count: > 65 } speciesPd && speciesPd[65] >= 2)
                continue;

            foreach (var memberGuid in familyMemberGuids)
            {
                if (!membersByGuid.TryGetValue(memberGuid, out var member)) continue;
                var hasCandidateToMember = candidate.Relationships.TryGetValue(member.NeighborId, out var cToM) &&
                                           cToM.Count > 0 && cToM[0] >= friendshipThreshold;
                var hasMemberToCandidate = member.Relationships.TryGetValue(candidate.NeighborId, out var mToC) &&
                                           mToC.Count > 0 && mToC[0] >= friendshipThreshold;
                var isFriend = requireMutual
                    ? hasCandidateToMember && hasMemberToCandidate
                    : hasCandidateToMember || hasMemberToCandidate;
                if (!isFriend) continue;

                count++;
                break;
            }
        }

        return count;
    }

    public int CountFamilyFriends(Family family)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (family.Members.Count == 0) return 0;
        var memberSet = new HashSet<int>(family.Members.Select(guid => unchecked((int)guid)));
        var roster = NeighborWalkOrder.Count > 0
            ? NeighborWalkOrder.Where(n => n is not null).Select(n => n!).ToArray()
            : Neighbors.Values.ToArray();
        if (roster.Length == 0) return 0;

        var enforceFamilyNumber = roster.Any(n => n.PersonData[61] != 0);
        var hasMemberSide = roster.Any(n => memberSet.Contains(n.TypeRecordKey) && n.Relationships.Snapshot().Count > 0);
        var hasCandidateSide = roster.Any(n => !memberSet.Contains(n.TypeRecordKey) && n.Relationships.Snapshot().Count > 0);
        var requireMutual = hasMemberSide && hasCandidateSide;
        var threshold = FriendshipThreshold > 0 ? FriendshipThreshold : 50;

        var count = 0;
        foreach (var candidate in roster)
        {
            if (memberSet.Contains(candidate.TypeRecordKey)) continue;
            if (enforceFamilyNumber && candidate.PersonData[61] == 0) continue;
            if (candidate.PersonData[65] >= 2) continue;

            foreach (var memberKey in memberSet)
            {
                var member = roster.FirstOrDefault(n => n.TypeRecordKey == memberKey);
                if (member is null) continue;
                var cToM = candidate.Relationships.Count(member.Id) > 0 &&
                           candidate.Relationships.Get(member.Id, 0) >= threshold;
                var mToC = member.Relationships.Count(candidate.Id) > 0 &&
                           member.Relationships.Get(candidate.Id, 0) >= threshold;
                if (requireMutual ? (cToM && mToC) : (cToM || mToC))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>Updates lot-save globals and returns the FAMI family flags mask (0x1 if a baby is present,
    /// 0x2 if a phone is present).</summary>
    public int ComputeSaveHouseInfo(out bool lotHasHouse, out int lotSize, out int transitFilterFlags, out int familyFlagsMask)
    {
        lotSize = this[SimulationGlobal.LotSize] is > 3 and <= LotGeometry.LotDimension
            ? this[SimulationGlobal.LotSize]
            : LotRoomMaps.Values.FirstOrDefault()?.Width is { } width && width > 0
                ? width
                : LotGeometry.LotDimension;
        this[SimulationGlobal.LotSize] = unchecked((short)lotSize);

        lotHasHouse = ComputeLotHasHouse(lotSize);
        this[SimulationGlobal.LotHasHouse] = lotHasHouse ? (short)1 : (short)0;

        familyFlagsMask = 0;
        transitFilterFlags = 0;
        var hasCashRegister = false;
        var hasCategoryBVar6 = false;
        var hasCategoryBVar3 = false;
        var hasCategoryBVar5 = false;
        var hasCategoryBVar7 = false;

        foreach (var item in WalkOrder)
        {
            var category = item[ObjectData.Category];
            if (category == 0 && item.TypeRecord?.Name is { Length: > 0 } typeName)
            {
                if (typeName.Equals("Baby", StringComparison.OrdinalIgnoreCase)) category = 1;
                else if (typeName.Contains("Phone", StringComparison.OrdinalIgnoreCase)) category = 9;
            }

            switch (category)
            {
                case 1:
                    if (item.Position.X != -16 || item.Position.Y != -16)
                        familyFlagsMask |= 0x1;
                    break;
                case 4:
                case 0x2e:
                case 0x2f:
                case 0x30:
                case 0x47:
                    transitFilterFlags |= 0x0001;
                    break;
                case 9:
                    familyFlagsMask |= 0x2;
                    break;
                case 10:
                    hasCashRegister = true;
                    break;
                case 0x11:
                case 0x15:
                case 0x16:
                case 0x17:
                case 0x18:
                case 0x1e:
                case 0x27:
                case 0x28:
                case 0x3a:
                case 0x3b:
                case 0x4a:
                    hasCategoryBVar6 = true;
                    break;
                case 0x19:
                case 0x1a:
                case 0x32:
                case 0x48:
                    transitFilterFlags |= 0x0002;
                    break;
                case 0x2b:
                case 0x31:
                case 0x33:
                case 0x4d:
                    transitFilterFlags |= 0x0040;
                    break;
                case 0x49:
                    transitFilterFlags |= 0x0004;
                    break;
                case 0x4b:
                    hasCategoryBVar3 = true;
                    break;
                case 0x4c:
                    hasCategoryBVar5 = true;
                    break;
                case 0x50:
                    transitFilterFlags |= 0x0080;
                    break;
                case 0x53:
                    transitFilterFlags |= 0x0400;
                    break;
                case 0x54:
                    transitFilterFlags |= 0x0100;
                    break;
                case 0x55:
                    transitFilterFlags |= 0x0200;
                    break;
                case 0x5d:
                case 0x5e:
                    transitFilterFlags |= 0x1000;
                    break;
                case 0x5f:
                    transitFilterFlags |= 0x0800;
                    break;
                case 0x61:
                    hasCategoryBVar7 = true;
                    break;
            }
        }

        if (hasCategoryBVar6 && hasCashRegister) transitFilterFlags |= 0x0008;
        if (hasCategoryBVar3 && hasCashRegister) transitFilterFlags |= 0x0010;
        if ((hasCategoryBVar5 && hasCashRegister) || hasCategoryBVar7) transitFilterFlags |= 0x0020;

        this[SimulationGlobal.FilterFlagsForTransitScreen] = unchecked((short)transitFilterFlags);
        return familyFlagsMask;
    }

    public int ComputeSaveHouseInfo() => ComputeSaveHouseInfo(out _, out _, out _, out _);

    private bool ComputeLotHasHouse(int lotSize)
    {
        var anyObjectHasCatalogSort = WalkOrder.Any(item =>
            item.Definition[(int)ObjdField.RoomSort] != 0 ||
            item.Definition[(int)ObjdField.FunctionSort] != 0 ||
            item.Definition[(int)ObjdField.BuildModeType] != 0);

        foreach (var item in WalkOrder)
        {
            if (item.MultiTileMasterId != 0 && item.MultiTileMasterId != item.Id) continue;
            var objectType = item.TypeRecord?.ObjectType ?? item.Definition[(int)ObjdField.Type];
            if (objectType == 7) continue;
            var x = item.Position.X >> 4;
            var y = item.Position.Y >> 4;
            if (x <= 0 || y <= 0 || x >= lotSize - 1 || y >= lotSize - 1) continue;
            if (LotTileFlags is not null && !LotTileFlags.InsideLot(x, y)) continue;

            if (item.Definition[(int)ObjdField.RoomSort] != 0 ||
                item.Definition[(int)ObjdField.FunctionSort] != 0 ||
                item.Definition[(int)ObjdField.BuildModeType] != 0)
            {
                return true;
            }

            if (!anyObjectHasCatalogSort && SavedLotObjects is null && item is not XPerson &&
                objectType is not (2 or 7 or 8 or 9 or 11))
            {
                return true;
            }
        }

        foreach (var (level, roomMap) in LotRoomMaps.OrderBy(pair => pair.Key))
        {
            var maxX = Math.Min(lotSize - 1, roomMap.Width - 1);
            var maxY = Math.Min(lotSize - 1, roomMap.Height - 1);
            for (var x = 1; x < maxX; x++)
            for (var y = 1; y < maxY; y++)
            {
                if (LotTileFlags is not null && !LotTileFlags.InsideLot(x, y)) continue;
                if (roomMap.StatisticsInputAt(x, y) is { } stats)
                {
                    if (stats.Walls.UInt32At(0) != 0) return true;
                    if (stats.FloorGridWord is { } floor && floor != 0) return true;
                    if (level == 1 && stats.RawGroundWaterGridByte is { } pool && pool != 0 && pool != 0xff)
                        return true;
                }
                else if (roomMap.RuntimeWallAt(x, y) is { Segments: not 0 })
                {
                    return true;
                }
            }
        }

        return false;
    }

    private int CountNeighborInteractions(XObject interactionObject)
    {
        var count = 0;
        foreach (var candidate in NeighborTableEntries)
        {
            if (candidate is null || interactionObject.HasNeighborSubobjectReference(candidate) ||
                candidate.NeighborInteractionTargetWordEE == 0) continue;

            for (var position = 0; position < ActivePersonCount; position++)
            {
                var personRecordPosition = ActivePersonIndex(position);
                if (personRecordPosition < 0 || personRecordPosition >= PersonRecordKeys.Count) continue;
                var personRecordKey = PersonRecordKeys[personRecordPosition];
                var activeNeighbor = NeighborTableEntries.FirstOrDefault(item => item?.TypeRecord?.Key == personRecordKey);
                if (activeNeighbor is null) continue;

                if (candidate.Relationships.Count(activeNeighbor.Id) == 0 ||
                    activeNeighbor.Relationships.Count(candidate.Id) == 0) continue;
                if (candidate.Relationships.Get(activeNeighbor.Id, 0) < FriendshipThreshold ||
                    activeNeighbor.Relationships.Get(candidate.Id, 0) < FriendshipThreshold ||
                    candidate.NeighborInteractionWordF6 >= 2) continue;

                count++;
                break;
            }
        }
        return count;
    }

    /// <summary>Autonomy friendship threshold loaded from FCNS tuning (default 25).</summary>
    public int FriendshipThreshold { get; set; } = 25;
    /// <summary>Multiplier loaded from FCNS tuning applied to the sum of outside room scores (default
    /// 0.33).</summary>
    public float HouseYardScoreMultiplier { get; private set; } = 0.33f;
    /// <summary>Penalty per dirty object loaded from FCNS tuning used in house upkeep score calculations
    /// (default 6).</summary>
    public int HouseUpkeepPenaltyPerDirtyObject { get; private set; } = 6;
    /// <summary>Penalty per broken object loaded from FCNS tuning used in house upkeep score calculations
    /// (default 15).</summary>
    public int HouseUpkeepPenaltyPerBrokenObject { get; private set; } = 15;
    /// <summary>Fill value for room layout history loaded from FCNS tuning (default 200).</summary>
    public int HouseLayoutHistoryFillValue { get; private set; } = 200;
    /// <summary>Autonomy tuning parameters loaded from the FCNS resource chunk.</summary>
    public FindBestInteractionTuning SimulatorTuning { get; set; } = new();
    /// <summary>Room, floor, wall, and lighting score factors loaded from the FCNS resource chunk.</summary>
    public SimsEngine.World.RoomScoreTuning RoomScoreTuning { get; set; } = new();
    /// <summary>Overrides the player's Free Will setting. When null, host-backed autonomy state remains
    /// authoritative.</summary>
    private bool? _autonomyForced;
    public bool? AutonomyForced
    {
        get => _autonomyForced;
        set
        {
            _autonomyForced = value;
            if (value is { } enabled)
            {
                var sourceValue = (short)(enabled ? 1 : 0);
                GlobalObjectTableEffectWords[0x1e] = sourceValue;
                this[SimulationGlobal.FreeWill] = sourceValue;
            }
        }
    }
    public Dictionary<short, short> GlobalObjectTableEffectWords { get; } = [];
    /// <summary>Contains the lot routing slots populated from SLOT resource 100.</summary>
    public List<RoutingSlotRequest> LotRoutingSlots { get; } = [];
    public bool RoutingEnabled { get; set; } = true;
    public GlobalPersonState GlobalPerson { get; } = new();
    public bool LotUpkeepManagerPresent { get; set; }
    public XObject? NeighborInteractionSubject { get; set; }
    public List<int> NeighborInteractionManagerTypeKeys { get; } = [];
    public short NeighborInteractionGlobalStateValue { get; set; }
    public GenericSimsCall03ManagerState GenericSimsCall03State { get; } = new();
    public XObject? GenericSimsCall23PersonSlotBase { get; set; }
    /// <summary>The lot's one-based table of active neighbor object instances, distinct from persistent
    /// person records in <see cref="Neighbors"/>.</summary>
    public List<XObject?> NeighborTableEntries { get; } = [];
    /// <summary>The selected family's ordered member keys loaded from the FAMI resource, preserving file
    /// order for person record lookups.</summary>
    public List<int> ActivePersonRecordKeys { get; } = [];
    public List<int> PersonRecordKeys { get; } = [];
    public List<short?> PersonRecordNeighborIds { get; } = [];
    public bool ActivePersonManagerPresent { get; set; }
    public short? StoredSlotPersonId { get; set; }
    public bool PersonRecordManagerPresent { get; set; }
    public GenericSimsCallRecordTarget? GenericSimsCallRecordCallbackTarget { get; set; }
    public bool PersonSelectionOverride { get; set; }
    public short[] Globals { get; } = new short[GlobalCount];
    public short[] NeighborhoodData { get; } = new short[16];
    public int? GenericSimsCall05ManagerWord164 { get; set; }
    public float? ArchitectureValueMultiplier { get; set; }
    public int? ObjectValueGlobalWord50 { get; set; }
    public int? ObjectValueGlobalWord90 { get; set; }
    public int? ObjectValueGlobalWord9C { get; set; }
    public int? ObjectValueGlobalWordA0 { get; set; }
    public int? ObjectValueGlobalWordA4 { get; set; }
    public short[] CheckTreeAds { get; set; } = [];
    /// <summary>Ordered state class entries where list order determines precedence when duplicate state
    /// keys are present.</summary>
    public List<StateClassEntry> StateClasses { get; } = [];
    /// <summary>Avatar animation clips indexed by animation scope, age group, and table index, populated
    /// from Global.iff and avatar animation resources.</summary>
    public Dictionary<(byte Source, bool Child, int Index), AvatarAnimationClip> AvatarAnimationTable { get; } = [];
    /// <summary>Overrides for standard walk animations loaded from an object's STR# 150 resource, keyed by
    /// object GUID and animation index.</summary>
    public Dictionary<(uint ObjectGuid, int Index), AvatarAnimationClip> CustomWalkAvatarAnimationTable { get; } = [];
    /// <summary>Fallback walk animations keyed by global STR# resource ID and zero-based animation index,
    /// used when an object lacks custom overrides.</summary>
    public Dictionary<(ushort TableId, int Index), AvatarAnimationClip> StandardWalkFallbackAnimationTable { get; } = [];
    /// <summary>Object-scoped animation clips from tables 129–132 loaded from the object's IFF file,
    /// selected by actor and secondary character age.</summary>
    public Dictionary<(int ObjectKey, ushort TableId, int Index), AvatarAnimationClip> ObjectAvatarAnimationTable { get; } = [];
    /// <summary>Dispatches sound cues authored in animation packages when playback crosses their event
    /// timestamp.</summary>
    public Action<short, AvatarAnimationSoundEvent>? AnimationSound { get; set; }
    /// <summary>Indicates whether avatar animations should advance only the root transform while keeping
    /// animation poses and blend channels fixed.</summary>
    public bool AvatarAnimationRootOnly { get; set; }
    /// <summary>Interaction scope tables keyed by object group ID, allowing multiple objects with the same
    /// ID to share an interaction scope.</summary>
    public Dictionary<short, InteractionScopeTable> InteractionScopes { get; } = [];
    public Dictionary<int, short> PersonTypeRecordSlots { get; } = [];
    public ISimulationHost? Host { get; set; }
    /// <summary>Hooks for sound primitives, kept separate from the simulation host because audio does not
    /// affect behavior or saved game state.</summary>
    public Func<string, short, bool, bool, bool>? PlaySoundEvent { get; set; }
    public Action<short>? StopOwnerSounds { get; set; }
    /// <summary>Raised when an object's active sounds should be stopped due to object destruction or reset.</summary>
    public event Action<short>? ObjectSoundsStopped;
    public Action<TutorialFlashKind, short, bool>? TutorialFlash { get; set; }
    /// <summary>Tracks object identifiers that own active positional sound events, ensuring audio emitter
    /// locations are exported even for objects without visual geometry.</summary>
    public HashSet<short> PositionalSoundOwners { get; } = [];

    /// <summary>Stops active positional and animation-driven sounds owned by the specified object during
    /// destruction or reset.</summary>
    public void StopObjectSounds(short id)
    {
        PositionalSoundOwners.Remove(id);
        StopOwnerSounds?.Invoke(id);
        ObjectSoundsStopped?.Invoke(id);
    }
    /// <summary>Provides career track data loaded from work.iff independently of the simulation host.</summary>
    public ISimulationCareerHost? CareerHost { get; set; }
    public ISimulationAutonomyHost? AutonomyHost { get; set; }
    internal readonly record struct ExecutionLocationSnapshot(
        XObject Me,
        short TreeId,
        int Instruction,
        ushort Opcode,
        byte[] Operand,
        short StackObjectKey,
        XObject? StackObject);

    private ExecutionLocationSnapshot? _executionLocationSnapshot;
    private string? _executionLocationOverride;

    internal void SetExecutionLocation(
        XObject me,
        short treeId,
        int instruction,
        ushort opcode,
        byte[] operand,
        short stackObjectKey,
        XObject? stackObject)
    {
        _executionLocationOverride = null;
        _executionLocationSnapshot = new ExecutionLocationSnapshot(
            me, treeId, instruction, opcode, operand, stackObjectKey, stackObject);
    }

    internal (ExecutionLocationSnapshot? Snapshot, string? Override) CaptureExecutionLocation() =>
        (_executionLocationSnapshot, _executionLocationOverride);

    internal void RestoreExecutionLocation((ExecutionLocationSnapshot? Snapshot, string? Override) state)
    {
        _executionLocationSnapshot = state.Snapshot;
        _executionLocationOverride = state.Override;
    }

    internal string? CurrentExecutionLocation
    {
        get
        {
            if (_executionLocationOverride is { } cached)
                return cached;
            if (_executionLocationSnapshot is not { } snap)
                return null;
            var objectType = snap.Me.Definition[(int)ObjdField.Type];
            int? objectGuid = snap.Me.TypeRecord is { Key: not 0 } typeRecord ? typeRecord.Key : null;
            var tileX = snap.Me.Position.X >> 4;
            var tileY = snap.Me.Position.Y >> 4;
            var tileValue = TileValueAt(tileX, tileY, snap.Me.Position.Height);
            var stackObject = snap.StackObject;
            var stackType = stackObject?.TypeRecord is { } stackRecord ? $", GUID 0x{stackRecord.Key:X8}" : string.Empty;
            var formatted = $"object {snap.Me.Id} (OBJD type {objectType}, GUID {(objectGuid is { } guid ? $"0x{guid:X8}" : "unknown")}), BHAV {snap.TreeId}, instruction {snap.Instruction}, opcode 0x{snap.Opcode:X}, operand {Convert.ToHexString(snap.Operand)}, stack key {snap.StackObjectKey}, stack object {(stackObject is null ? "none" : $"{stackObject.Id} (OBJD type {stackObject.Definition[(int)ObjdField.Type]}{stackType})")}, position {snap.Me.Position}, tile key {tileValue}";
            _executionLocationOverride = formatted;
            return formatted;
        }
        set
        {
            _executionLocationOverride = value;
            _executionLocationSnapshot = null;
        }
    }
    public bool DisplayRefreshRequested { get; set; }
    public IFamilyBudget? FamilyBudget { get; set; }
    public INeighborhoodFamilies? NeighborhoodFamilies { get; set; }
    public sealed record LotSwitchRequest(short RawTargetHouse, int PersonGuid, short FamilyNumber, int CompanionGuid = 0);

    public LotSwitchRequest? PendingLotSwitch { get; set; }

    public Action? PersistNeighborhoodStateImmediately { get; set; }
    public Action? PersistLotSimulationStateImmediately { get; set; }
    public Action<int>? ReleaseGenericSimsCall03PrimaryHandle { get; set; }
    public Action<int>? ReleaseGenericSimsCall03SecondaryHandle { get; set; }

    public delegate bool FamilyValuationLookup(short familyNumber, out int houseNumber, out int budget, out int value);
    public FamilyValuationLookup? TryLookupFamilyValuation { get; set; }
    public Func<(int Furnishings, int Architecture)>? LiveLotValuationProvider { get; set; }
    public Func<IReadOnlyList<int>>? OccupiedVacationHousesProvider { get; set; }

    public delegate bool ActiveFamilyUpkeepLookup(out short familyNumber, out bool hasMembers, out IReadOnlyList<short> minResources);
    public ActiveFamilyUpkeepLookup? TryLookupActiveFamilyUpkeep { get; set; }

    public List<XObject> WorldLotListEntries { get; } = [];

    /// <summary>Executes GenericSimsCall 0x00, 0x08, and 0x09 initialization natively, clearing tutorial
    /// state on mode 0 and persisting NGBH neighborhood mode flags.</summary>
    public void RunGenericSimsCallInitialization(int mode)
    {
        if (mode == 0)
        {
            this[SimulationGlobal.IsTutorialHouseNoYes] = 0;
            PersistLotSimulationStateImmediately?.Invoke();
        }
        NeighborhoodData[1] = checked((short)(mode + 1));
        NeighborhoodData[2] = 0;
        PersistNeighborhoodState();
    }

    /// <summary>Flushes immediate neighborhood state projections to disk, updating NBRS and FAMI resources.</summary>
    public void PersistNeighborhoodState()
    {
        if (Host is { } host) host.PersistNeighborhoodState();
        else PersistNeighborhoodStateImmediately?.Invoke();
    }
    public ISimulationMenuTextSources? MenuTextSources { get; set; }
    public List<short> PendingRemovals { get; } = [];
    public GenericSimsCallRequestQueue GenericSimsCallRequests { get; } = new();
    public bool AutoProcessGenericSimsCallRequests { get; set; }
    /// <summary>Controls whether removing the currently executing object terminates the active tree
    /// execution immediately.</summary>
    public bool SelfRemovalEndsRun { get; set; } = true;

    public short this[SimulationGlobal global] { get => Globals[(int)global]; set => Globals[(int)global] = value; }

    /// <summary>Provides one-based indexing for all active object instances on the lot.</summary>
    public IReadOnlyList<XObject?> ObjectTableEntries => _objectTableEntries;
    private readonly Dictionary<(int X, int Y, int Level), short> _tileRoots = [];

    public int TileValueAt(int tileX, int tileY, int level)
    {
        if (tileX is < 0 or >= 64 || tileY is < 0 or >= 64 || level is < 1 or > 2) return 0;
        return _tileRoots.GetValueOrDefault((tileX, tileY, level));
    }

    /// <summary>Reads placement flags from the runtime grid for columns and structural objects, distinct
    /// from room boundary arrays.</summary>
    public byte BuildModeType6PlacementFlagsAt(int tileX, int tileY, int level)
    {
        if (tileX is < 0 or >= 64 || tileY is < 0 or >= 64 || level is < 1 or > 3) return 0;
        return BuildModeType6PlacementGrids.TryGetValue(level, out var grid) ? grid[tileX * 64 + tileY] : (byte)0;
    }

    /// <summary>Determines whether the specified tile is eligible for placement, checked only on the ground
    /// floor (level 1).</summary>
    public bool HasPlacementEligibilityAt(int tileX, int tileY, int level)
    {
        if (level != 1 || tileX is < 0 or >= 64 || tileY is < 0 or >= 64) return false;
        return PlacementEligibilityGrids.TryGetValue(1, out var grid) && grid[tileX * 64 + tileY] != 0xff;
    }

    public void SetPlacementEligibilityAt(int tileX, int tileY, byte value)
    {
        if (tileX is < 0 or >= 64 || tileY is < 0 or >= 64)
            throw new ArgumentOutOfRangeException(nameof(tileX), "Placement eligibility cells must be inside the lot grid.");
        if (!PlacementEligibilityGrids.TryGetValue(1, out var grid))
        {
            grid = new byte[64 * 64];
            Array.Fill(grid, (byte)0xff);
            PlacementEligibilityGrids.Add(1, grid);
        }
        grid[tileX * 64 + tileY] = value;
    }

    /// <summary>Determines whether routing in a given direction is blocked by combining tile wall segments
    /// with object wall block flags rotated by object orientation.</summary>
    internal bool BlocksRouteDirectionAt(SimsEngine.World.RoomMap map, int tileX, int tileY, int direction)
    {
        if (tileX <= 0 || tileY <= 0 || tileX >= LotGeometry.LotDimension - 1 ||
            tileY >= LotGeometry.LotDimension - 1) return false;
        var mask = map.RouteBlockingMaskAt(tileX, tileY);
        foreach (var item in ObjectsAtTile(tileX, tileY, map.Level))
        {
            var wallBlockFlags = item[ObjectData.WallBlockFlags];
            if (wallBlockFlags == 0) continue;
            var shift = unchecked((byte)item[ObjectData.Direction]) & 0x1f;
            var objectMask = unchecked((uint)((int)wallBlockFlags << shift));
            objectMask = (objectMask & 0xff) | ((objectMask >> 8) & 0xff);
            mask |= objectMask;
        }
        return SimsEngine.World.RoomMap.BlocksRouteDirection(mask, direction);
    }

    private static (int X, int Y, int Level)? TileCell(ObjectPosition position)
    {
        var x = position.X >> 4;
        var y = position.Y >> 4;
        return x is >= 0 and < 64 && y is >= 0 and < 64 && position.Height is >= 1 and <= 2
            ? (x, y, position.Height)
            : null;
    }

    private bool IsInSlotSubtree(XObject target, XObject root)
    {
        var visited = new HashSet<XObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<XObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (ReferenceEquals(current, target)) return true;
            for (var i = 0; i < current.Slots.Count; i++)
            {
                var key = current.Slots[i];
                if (key <= 0) continue;
                var child = current.ObjectTableEntry(key) ?? ObjectTableEntry(key);
                if (child is not null) stack.Push(child);
            }
        }
        return false;
    }

    private void InsertTileRoot(XObject item, ObjectPosition? at = null)
    {
        if (item[ObjectData.ParentId] != 0 || item.ClockManagerIndex <= 0 || TileCell(at ?? item.Position) is not { } cell) return;
        _tileRoots[cell] = item.ClockManagerIndex;
    }

    private void RemoveTileRoot(XObject item, ObjectPosition? at = null)
    {
        if (item[ObjectData.ParentId] == 0)
            UnplaceObjectFromGridOrSlot(item, at);
    }

    /// <summary>Detaches an object from its tile root or parent slot without clearing slot 0, preserving
    /// any displaced overlap chain.</summary>
    private void DetachDirectPlacementLink(XObject item, ObjectPosition? at = null)
    {
        if (TileCell(at ?? item.Position) is { } cell &&
            _tileRoots.TryGetValue(cell, out var rootKey) && rootKey == item.ClockManagerIndex)
        {
            _tileRoots.Remove(cell);
        }

        var parentIndex = item[ObjectData.ParentId] > 0 ? item[ObjectData.ParentId] : item[ObjectData.ContainerId];
        var slotIndex = item[ObjectData.SlotHierNumber];
        if (slotIndex < 0 && item[ObjectData.ContainerId] > 0 && item[ObjectData.SlotNumber] > 0)
            slotIndex = item[ObjectData.SlotNumber];
        if (parentIndex > 0 && (item.ObjectTableEntry(parentIndex) ?? ObjectTableEntry(parentIndex) ?? ObjectById(parentIndex)) is { } parent)
        {
            if (slotIndex >= 0)
            {
                if (slotIndex < parent.Slots.Count && parent.Slots[slotIndex] == item.ClockManagerIndex)
                    parent.Slots[slotIndex] = 0;
                if (slotIndex > 0 && slotIndex - 1 < parent.FootprintSlots.Count &&
                    parent.FootprintSlots[slotIndex - 1].OccupantObjectId == item.ClockManagerIndex)
                {
                    parent.FootprintSlots[slotIndex - 1].OccupantObjectId = 0;
                }
            }
            else
            {
                for (var i = 1; i < parent.Slots.Count; i++)
                {
                    if (parent.Slots[i] == item.ClockManagerIndex) parent.Slots[i] = 0;
                }
                for (var i = 0; i < parent.FootprintSlots.Count; i++)
                {
                    if (parent.FootprintSlots[i].OccupantObjectId == item.ClockManagerIndex)
                        parent.FootprintSlots[i].OccupantObjectId = 0;
                }
            }
        }
        item[ObjectData.ParentId] = 0;
        item[ObjectData.SlotHierNumber] = -1;
    }

    /// <summary>Unplaces an object from the tile grid or parent slot, splicing any occupant in slot 0 back
    /// into its place.</summary>
    private void UnplaceObjectFromGridOrSlot(XObject item, ObjectPosition? at = null)
    {
        var pos = at ?? item.Position;
        var oldCell = TileCell(pos);
        var oldHeight = pos.Height;
        var oldParentKey = item[ObjectData.ParentId];
        var oldSlotIndex = item[ObjectData.SlotHierNumber];
        var oldParent = oldParentKey > 0
            ? item.ObjectTableEntry(oldParentKey) ?? ObjectTableEntry(oldParentKey) ?? ObjectById(oldParentKey)
            : null;
        var wasTileRoot = oldParentKey == 0 && oldCell is { } c &&
                          _tileRoots.TryGetValue(c, out var currentKey) && currentKey == item.ClockManagerIndex;
        var wasParentSlotOccupant = oldParent is not null && oldSlotIndex >= 0 &&
                                    ((oldSlotIndex < oldParent.Slots.Count && oldParent.Slots[oldSlotIndex] == item.ClockManagerIndex) ||
                                     (oldSlotIndex > 0 && oldSlotIndex - 1 < oldParent.FootprintSlots.Count &&
                                      oldParent.FootprintSlots[oldSlotIndex - 1].OccupantObjectId == item.ClockManagerIndex));

        DetachDirectPlacementLink(item, pos);

        if (item.Slots.Count > 0 && item.Slots[0] > 0)
        {
            var displacedKey = item.Slots[0];
            item.Slots[0] = 0;
            var displaced = item.ObjectTableEntry(displacedKey) ?? ObjectTableEntry(displacedKey);
            if (displaced is not null && !ReferenceEquals(displaced, item))
            {
                DetachDirectPlacementLink(displaced);
                if (wasParentSlotOccupant && oldParent is not null && !ReferenceEquals(oldParent, displaced) &&
                    !IsInSlotSubtree(oldParent, displaced) && !IsInSlotSubtree(displaced, oldParent))
                {
                    if (oldSlotIndex < oldParent.Slots.Count)
                        oldParent.Slots[oldSlotIndex] = displaced.ClockManagerIndex;
                    if (oldSlotIndex > 0 && oldSlotIndex - 1 < oldParent.FootprintSlots.Count)
                        oldParent.FootprintSlots[oldSlotIndex - 1].OccupantObjectId = displaced.ClockManagerIndex;
                    displaced[ObjectData.ParentId] = oldParent.ClockManagerIndex;
                    displaced[ObjectData.SlotHierNumber] = oldSlotIndex;
                    if (oldSlotIndex > 0)
                    {
                        displaced[ObjectData.ContainerId] = oldParent.ClockManagerIndex;
                        displaced[ObjectData.SlotNumber] = oldSlotIndex;
                    }
                    SetObjectPosition(displaced, displaced.Position with { Height = oldParent.Position.Height });
                }
                else if (wasTileRoot && oldCell is { } rootCell && displaced.ClockManagerIndex > 0)
                {
                    _tileRoots[rootCell] = displaced.ClockManagerIndex;
                    SetObjectPosition(displaced, displaced.Position with { Height = oldHeight });
                }
            }
        }
    }

    private void DetachFromFootprintSlot(XObject item)
    {
        if (item[ObjectData.ParentId] > 0 || item[ObjectData.ContainerId] > 0)
            UnplaceObjectFromGridOrSlot(item);
        else
            DetachDirectPlacementLink(item);
    }

    private void MoveTileRoot(XObject item, ObjectPosition destination, XObject? placementContainer = null,
        short placementSlotNumber = 0, Action<XObject>? onPriorContainerDetached = null)
    {
        var previous = item.Position;
        var previousDirection = item.FootprintBoundsDirection ?? item[ObjectData.Direction];
        var isChair = HasFunction(item, 0x1a);
        var oldContainer = ResolveParentContainer(item);
        var wasFloorPlaced = oldContainer is null && TileCell(previous) is not null;
        if (oldContainer is not null && !ReferenceEquals(oldContainer, placementContainer))
            onPriorContainerDetached?.Invoke(oldContainer);

        SetBuildModeType6PlacementFlag(item, previous, false);
        UnplaceObjectFromGridOrSlot(item, previous);
        if (wasFloorPlaced && isChair && (TileCell(previous) != TileCell(destination) || placementContainer is not null))
        {
            var oldOffset = LotGeometry.FootprintOffset(previousDirection);
            UpdateAdjacentChairFlag((previous.X >> 4) + (oldOffset.X >> 4),
                (previous.Y >> 4) + (oldOffset.Y >> 4), previous.Height);
        }

        short destinationOccupantKey = 0;
        if (placementContainer is not null)
        {
            if (placementSlotNumber > 0 && placementSlotNumber - 1 < placementContainer.FootprintSlots.Count)
                destinationOccupantKey = placementContainer.FootprintSlots[placementSlotNumber - 1].OccupantObjectId;
            else if (placementSlotNumber >= 0 && placementSlotNumber < placementContainer.Slots.Count)
                destinationOccupantKey = placementContainer.Slots[placementSlotNumber];
        }
        else if (TileCell(destination) is { } targetCell)
        {
            destinationOccupantKey = checked((short)TileValueAt(targetCell.X, targetCell.Y, targetCell.Level));
        }

        var displacedObject = destinationOccupantKey > 0
            ? item.ObjectTableEntry(destinationOccupantKey) ?? ObjectTableEntry(destinationOccupantKey)
            : null;
        if (displacedObject is not null &&
            (ReferenceEquals(displacedObject, item) ||
             IsInSlotSubtree(item, displacedObject) ||
             IsInSlotSubtree(displacedObject, item)))
        {
            displacedObject = null;
        }
        if (displacedObject is not null)
            DetachDirectPlacementLink(displacedObject);

        item[ObjectData.ContainerId] = placementContainer?.ClockManagerIndex ?? 0;
        item[ObjectData.SlotNumber] = placementContainer is null ? (short)0 : placementSlotNumber;
        item[ObjectData.ParentId] = placementContainer?.ClockManagerIndex ?? 0;
        item[ObjectData.SlotHierNumber] = placementContainer is null ? (short)-1 : placementSlotNumber;
        if (placementContainer is not null)
        {
            if (placementSlotNumber >= 0 && placementSlotNumber < placementContainer.Slots.Count)
                placementContainer.Slots[placementSlotNumber] = item.ClockManagerIndex;
            if (placementSlotNumber > 0 && placementSlotNumber - 1 < placementContainer.FootprintSlots.Count)
                placementContainer.FootprintSlots[placementSlotNumber - 1].OccupantObjectId = item.ClockManagerIndex;
        }
        else if (item.ClockManagerIndex > 0 && TileCell(destination) is { } destinationCell)
        {
            _tileRoots[destinationCell] = item.ClockManagerIndex;
        }

        var currentDirection = item[ObjectData.Direction];
        SetObjectPosition(item, destination);
        if (displacedObject is not null && item.ClockManagerIndex > 0)
        {
            if (item.Slots.Count == 0) item.Slots.Add(0);
            item.Slots[0] = displacedObject.ClockManagerIndex;
            displacedObject[ObjectData.ParentId] = item.ClockManagerIndex;
            displacedObject[ObjectData.SlotHierNumber] = 0;
            SetObjectPosition(displacedObject, displacedObject.Position with { Height = destination.Height });
        }

        SetBuildModeType6PlacementFlag(item, destination, true);
        UpdateAdjacentChairFlag(destination.X >> 4, destination.Y >> 4, destination.Height);
        if (isChair)
        {
            if (TileCell(previous) == TileCell(destination) && placementContainer is null && wasFloorPlaced &&
                previousDirection != currentDirection)
            {
                var oldOffset = LotGeometry.FootprintOffset(previousDirection);
                UpdateAdjacentChairFlag((previous.X >> 4) + (oldOffset.X >> 4),
                    (previous.Y >> 4) + (oldOffset.Y >> 4), previous.Height);
            }
            if (placementContainer is null)
            {
                var newOffset = LotGeometry.FootprintOffset(currentDirection);
                UpdateAdjacentChairFlag((destination.X >> 4) + (newOffset.X >> 4),
                    (destination.Y >> 4) + (newOffset.Y >> 4), destination.Height);
            }
        }
        if ((unchecked((ushort)item[ObjectData.FlagField2]) & 0x0400) != 0)
            DispatchNotice(0xf2, item.ClockManagerIndex);
    }

    /// <summary>Updates the adjacent-chair flag on all objects at the tile based on whether an orthogonally
    /// adjacent chair faces it, triggering placement logic when the flag state changes.</summary>
    internal void UpdateAdjacentChairFlag(int tileX, int tileY, int level)
    {
        var size = LotGeometry.LotDimension;
        if (tileX <= 0 || tileY <= 0 || tileX >= size - 1 || tileY >= size - 1) return;
        var tileObjects = ObjectsAtTile(tileX, tileY, level).ToList();
        if (tileObjects.Count == 0) return;

        var hasAdjacentChair = false;
        for (short dir = 0; dir < 8; dir += 2)
        {
            var offset = LotGeometry.FootprintOffset(dir);
            var nx = tileX + (offset.X >> 4);
            var ny = tileY + (offset.Y >> 4);
            if (nx <= 0 || ny <= 0 || nx >= size - 1 || ny >= size - 1) continue;
            var neighbor = ObjectsAtTile(nx, ny, level).FirstOrDefault(candidate =>
                candidate is not XPerson &&
                (candidate[ObjectData.ParentId] == 0 || candidate[ObjectData.SlotHierNumber] == 0) &&
                (unchecked((ushort)candidate[ObjectData.Flags]) & 4) == 0);
            if (neighbor is not null && HasFunction(neighbor, 0x1a) &&
                (neighbor[ObjectData.Direction] & 7) == ((dir + 4) & 7))
            {
                hasAdjacentChair = true;
                break;
            }
        }

        foreach (var obj in tileObjects)
        {
            var oldFlags = unchecked((ushort)obj[ObjectData.Flags]);
            var newFlags = hasAdjacentChair ? (ushort)(oldFlags | 0x0100) : (ushort)(oldFlags & ~0x0100);
            obj[ObjectData.Flags] = unchecked((short)newFlags);
            if (((oldFlags ^ newFlags) & 0x0100) != 0 && HasFunction(obj, 0x19))
                RunFunctionToCompletion(obj, 9);
        }
    }

    private void SetBuildModeType6PlacementFlag(XObject item, ObjectPosition position, bool present)
    {
        if (item.Definition[(int)ObjdField.BuildModeType] != 6) return;
        var x = position.X >> 4;
        var y = position.Y >> 4;
        var size = LotGeometry.LotDimension;
        if (x <= 0 || y <= 0 || x >= size - 1 || y >= size - 1 || position.Height is < 1 or > 3) return;
        if (!BuildModeType6PlacementGrids.TryGetValue(position.Height, out var grid))
        {
            if (!present) return;
            grid = new byte[size * size];
            BuildModeType6PlacementGrids.Add(position.Height, grid);
        }
        var index = x * size + y;
        grid[index] = present ? (byte)(grid[index] | 0x08) : (byte)(grid[index] & ~0x08);
    }

    /// <summary>Enumerates the root object at the specified tile followed by all contained slot objects in
    /// preorder traversal.</summary>
    public IEnumerable<XObject> ObjectsAtTile(int tileX, int tileY, int level)
    {
        var rootKey = TileValueAt(tileX, tileY, level);
        if (rootKey <= 0 || rootKey > short.MaxValue || ObjectTableEntry((short)rootKey) is not { } root) yield break;

        var activePath = new HashSet<short>();
        foreach (var item in EnumerateSlotSubtree(root, activePath))
            yield return item;
    }

    private IEnumerable<XObject> EnumerateSlotSubtree(XObject root, HashSet<short> activePath)
    {
        if (!activePath.Add(root.Id)) yield break;
        var visited = new HashSet<short> { root.Id };
        var stack = new Stack<(XObject Node, int NextSlot)>();
        stack.Push((root, 0));
        try
        {
            yield return root;
            while (stack.Count > 0)
            {
                var (node, nextSlot) = stack.Pop();
                var descended = false;
                while (nextSlot < node.Slots.Count)
                {
                    var child = ObjectTableEntry(node.Slots[nextSlot++]);
                    if (child is null) continue;
                    if (!visited.Add(child.Id) || !activePath.Add(child.Id)) continue;
                    stack.Push((node, nextSlot));
                    stack.Push((child, 0));
                    descended = true;
                    yield return child;
                    break;
                }
                if (!descended)
                    activePath.Remove(node.Id);
            }
        }
        finally
        {
            while (stack.Count > 0)
                activePath.Remove(stack.Pop().Node.Id);
            activePath.Remove(root.Id);
        }
    }

    /// <summary>Determines whether an object's position is strictly inside the lot boundary and eligible to
    /// burn.</summary>
    internal static bool BurnSourceOnLot(XObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tileX = (sbyte)(source.Position.X >> 4);
        var tileY = (sbyte)(source.Position.Y >> 4);
        return tileX > 0 && tileY > 0 && tileX < LotGeometry.LotDimension - 1 && tileY < LotGeometry.LotDimension - 1;
    }

    /// <summary>Attempts to ignite a tile by expanding a burnable set across multi-tile parts, co-located
    /// objects, and neighboring footprints. Sims in the affected area are reset and snapped before fire
    /// instances are created, while in-use or already burned objects are excluded.</summary>
    internal bool TryBurnTile(int tileX, int tileY, short height, bool strictFlags, TreeStack? checkStack = null) =>
        TryBurnTile(new ObjectPosition(tileX * 16 + 8, tileY * 16 + 8, height), strictFlags, checkStack);

    internal bool TryBurnTile(ObjectPosition position, bool strictFlags, TreeStack? checkStack = null)
    {
        var tileX = (sbyte)(position.X >> 4);
        var tileY = (sbyte)(position.Y >> 4);
        if (tileX <= 0 || tileY <= 0 || tileX >= LotGeometry.LotDimension - 1 ||
            tileY >= LotGeometry.LotDimension - 1 || position.Height is < 1 or > 2) return false;

        XObject? seed = null;
        foreach (var item in ObjectsAtTile(tileX, tileY, position.Height))
        {
            if ((unchecked((ushort)item[ObjectData.FlagField2]) & 0x0020) != 0)
                seed = item;
            if ((unchecked((ushort)item[ObjectData.Flags]) & 0x0800) != 0 ||
                item.TypeRecord?.Key == BurnPrimitive.FireType) return false;
        }
        if (seed is null) return TryCreateBurnFire(position, checkStack);

        var burnable = new List<XObject> { seed };
        var burnableIds = new HashSet<short> { seed.Id };

        ReadOnlySpan<(int X, int Y)> neighbors = [
            (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];
        for (var cursor = 0; cursor < burnable.Count; cursor++)
        {
            var candidate = burnable[cursor];
            if ((candidate.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal ||
                 candidate.MultiTileMasterId != 0 || candidate.MultiTileNextId != 0) &&
                (unchecked((ushort)candidate[ObjectData.FlagField2]) & 0x0020) != 0 &&
                !HasFunction(candidate, 8))
            {
                foreach (var member in MultiTileMembers(candidate))
                    AddUnique(member);
            }

            var candidateX = (sbyte)(candidate.Position.X >> 4);
            var candidateY = (sbyte)(candidate.Position.Y >> 4);
            foreach (var sameTile in ObjectsAtTile(candidateX, candidateY, candidate.Position.Height))
            {
                if ((unchecked((ushort)sameTile[ObjectData.FlagField2]) & 0x0020) != 0)
                    AddUnique(sameTile);
            }

            foreach (var (dx, dy) in neighbors)
            {
                var nx = candidateX + dx;
                var ny = candidateY + dy;
                foreach (var adjacent in ObjectsAtTile(nx, ny, candidate.Position.Height))
                {
                    var adjacentBounds = adjacent.FootprintBoundsDirection == adjacent[ObjectData.Direction]
                        ? adjacent.FootprintBounds
                        : PlacementFootprintBounds(adjacent, adjacent.Position);
                    if (FootprintsOverlapCell(adjacent, adjacentBounds, candidateX, candidateY))
                        AddUnique(adjacent);
                }
            }

            void AddUnique(XObject item)
            {
                if (burnableIds.Add(item.Id))
                    burnable.Add(item);
            }
        }

        foreach (var item in burnable)
        {
            var flags = unchecked((ushort)item[ObjectData.Flags]);
            if ((flags & 0x0800) != 0 || (strictFlags && ((flags & 0x0020) != 0 || item.Data[62] > 0))) return false;
        }

        foreach (var item in burnable)
        {
            if (item is not XPerson person) continue;
            var pos = person.Position;
            ResetPersonForChangedCode(person);
            var snappedPosition = new ObjectPosition((pos.X & ~0x7) | 8, (pos.Y & ~0xf) | 8, pos.Height);
            PlaceObject(person, snappedPosition, checkStack ?? TreeRunner.CreateTransientStack(this, person));
            RefreshPersonPlacementCallbacksAfterMovement(person);
        }

        var placed = false;
        foreach (var item in burnable)
        {
            var objectTileX = (sbyte)(item.Position.X >> 4);
            var objectTileY = (sbyte)(item.Position.Y >> 4);
            if (ObjectsAtTile(objectTileX, objectTileY, item.Position.Height)
                .Any(occupant => occupant.TypeRecord?.Key == BurnPrimitive.FireType)) continue;
            var firePosition = new ObjectPosition((item.Position.X & ~0x7) | 8, (item.Position.Y & ~0xf) | 8, item.Position.Height);
            placed |= TryCreateBurnFire(firePosition, checkStack);
        }
        return placed;

        static bool FootprintsOverlapCell(XObject item, FootprintBounds bounds, int tileX, int tileY)
        {
            // Expands person bounds by two sub-tile units for overlap testing.
            var expansion = item is XPerson ? 2 : 0;
            var minX = bounds.MinX - expansion;
            var maxX = bounds.MaxX + expansion;
            var minY = bounds.MinY - expansion;
            var maxY = bounds.MaxY + expansion;
            var cellMinX = tileX * 16;
            var cellMaxX = cellMinX + 16;
            var cellMinY = tileY * 16;
            var cellMaxY = cellMinY + 16;
            return minX < cellMaxX && cellMinX < maxX && minY < cellMaxY && cellMinY < maxY;
        }
    }

    private bool TryCreateBurnFire(ObjectPosition position, TreeStack? checkStack = null)
    {
        if (ObjectFactory?.Invoke(BurnPrimitive.FireType) is not { } fire) return false;
        Add(fire);
        InitializeCreatedObject(fire);
        var effectiveStack = checkStack ?? TreeRunner.CreateTransientStack(this, fire);
        if (!CanPlaceCreatedObject(fire, position, checkStack: effectiveStack))
        {
            Remove(fire.Id);
            return false;
        }
        PlaceObject(fire, position, effectiveStack);
        return true;
    }

    /// <summary>Relocates an object to the off-lot sentinel position (-16, -16, 1), recomputes its room,
    /// and optionally triggers an interaction refresh.</summary>
    public void BeginObjectRemoval(XObject item, int argument)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (argument is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(argument), "The source +0x50 callback paths use arguments 0 or 1.");

        if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal)
        {
            foreach (var member in MultiTileMembers(item)) RefreshPortalWallStyleOverlay(member, resetToDefault: true);
            // Clears allocated portal routing costs across all actors upon portal removal.
            foreach (var routeOwner in WalkOrder) routeOwner.ResetAllocatedPortalRouteCosts();
        }

        // Invokes Pickup (OBJf 10) on lead objects before off-lot relocation.
        if (item.Definition is { Length: > (int)ObjdField.MasterId } &&
            item.Definition[(int)ObjdField.MasterId] == 0)
        {
            RunFunctionToCompletion(item, 10);
        }

        if ((unchecked((ushort)item[ObjectData.FlagField2]) & 0x0400) != 0)
            DispatchNotice(0xf2, item.ClockManagerIndex);
        SetBuildModeType6PlacementFlag(item, item.Position, false);
        // Runs Placement (OBJf 9) on the container object after slot item removal.
        var oldPosition = item.Position;
        var oldDirection = item[ObjectData.Direction];
        var oldContainer = ResolveParentContainer(item);
        DetachFromFootprintSlot(item);
        RemoveTileRoot(item);
        SetObjectPosition(item, new ObjectPosition(-16, -16, 1));
        if (HasFunction(item, 0x1a))
        {
            var oldOffset = LotGeometry.FootprintOffset(oldDirection);
            UpdateAdjacentChairFlag((oldPosition.X >> 4) + (oldOffset.X >> 4),
                (oldPosition.Y >> 4) + (oldOffset.Y >> 4), oldPosition.Height);
        }
        item.RemovalCallbackWord14 = -1;
        item.Data[2] = 0;
        item.Data[8] = (short)(item.Data[8] & 0xfeff);
        if (oldContainer is not null)
            RunFunctionToCompletion(oldContainer, 9);
        DisplayRefreshRequested = true;
        if (argument != 0) RefreshInteractionAfterObjectRemoval(item);
    }

    private void RefreshInteractionAfterObjectRemoval(XObject removed)
    {
        if (!InteractionRefreshManagerEnabled) return;
        if (ReferenceEquals(ActiveInteractionRefreshTarget, removed)) InteractionContextsInvalidated = true;

        XObject? target = removed;
        if (removed.TypeRecord?.ObjectType == XPerson.PersonType)
        {
            var occupantKey = removed.FootprintSlots.Count >= 2 ? removed.FootprintSlots[1].OccupantObjectId : (short)0;
            target = occupantKey > 0 ? ObjectTableEntry(occupantKey) ?? ObjectById(occupantKey) : null;
        }
        if (target is null) return;

        if (InteractionRefreshTypeKey == unchecked((int)0xe9337020u))
        {
            var index = InteractionRefreshPendingObjects.FindIndex(candidate => ReferenceEquals(candidate, target));
            if (index >= 0)
            {
                InteractionRefreshDeferredObjects.Add(InteractionRefreshPendingObjects[index]);
                InteractionRefreshPendingObjects.RemoveAt(index);
            }
        }
        else if (InteractionRefreshTypeKey is not (0x0baa71c5 or 0x2f726fff))
            return;

        BeginObjectRemoval(target, 0);
    }

    /// <summary>Unlinks a multi-tile part from its master chain. If the lead object is removed, its first
    /// child part is promoted to lead while preserving the remaining chain.</summary>
    internal void DetachMultiTilePart(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var firstPart = item.MultiTileNextId == 0 ? null : ObjectById(item.MultiTileNextId);
        if (item.MultiTileMasterId == 0)
        {
            var current = item;
            var visited = new HashSet<short>();
            while (current is not null && visited.Add(current.Id))
            {
                var nextId = current.MultiTileNextId;
                current.MultiTileMasterId = firstPart?.Id ?? (short)0;
                current = nextId == 0 ? null : ObjectById(nextId);
            }
            if (firstPart is not null) firstPart.MultiTileMasterId = 0;
        }
        else
        {
            var previous = ObjectById(item.MultiTileMasterId);
            var visited = new HashSet<short>();
            while (previous is not null && visited.Add(previous.Id))
            {
                var next = previous.MultiTileNextId == 0 ? null : ObjectById(previous.MultiTileNextId);
                if (ReferenceEquals(next, item))
                {
                    previous.MultiTileNextId = item.MultiTileNextId;
                    break;
                }
                previous = next;
            }
        }

        item.MultiTileMasterId = 0;
        item.MultiTileNextId = 0;
    }

    private sealed record ResetObjectSnapshot(
        XObject Item,
        ObjectPosition PreviousPosition,
        Dictionary<ObjectData, short> PreservedData,
        XObject? Parent,
        short ParentSlot,
        bool RestoreParentSlot,
        bool ValidPriorTile,
        XObject? DirectParent,
        short DirectSlot);

    private ResetObjectSnapshot CaptureResetObjectSnapshot(XObject item)
    {
        var previousPosition = item.Position;
        var preservedData = PreservedInitializerData(item);
        // Climbs slot-0 parent links to find the first non-zero container slot.
        var anchor = item;
        for (var visited = 0; visited < 64 && anchor[ObjectData.SlotHierNumber] == 0 &&
                              ObjectTableEntry(anchor[ObjectData.ParentId]) is { } above && !ReferenceEquals(above, anchor); visited++)
            anchor = above;
        var parentKey = anchor[ObjectData.ParentId];
        var parentSlot = anchor[ObjectData.SlotHierNumber];
        var parent = ObjectTableEntry(parentKey);
        var validPriorTile = previousPosition.Height is >= 1 and <= 3 &&
            previousPosition.X >> 4 is > 0 and < LotGeometry.LotDimension - 1 &&
            previousPosition.Y >> 4 is > 0 and < LotGeometry.LotDimension - 1;
        var directParent = ObjectTableEntry(item[ObjectData.ParentId]);
        var directSlot = item[ObjectData.SlotHierNumber];
        var restoreParentSlot = parent is not null && parentSlot >= 0 && parentSlot < parent.Slots.Count;
        return new ResetObjectSnapshot(item, previousPosition, preservedData, parent, parentSlot,
            restoreParentSlot, validPriorTile, directParent, directSlot);
    }

    private void DetachAndInitializeResetObject(ResetObjectSnapshot snapshot)
    {
        var item = snapshot.Item;
        BeginObjectRemoval(item, 1);

        if (snapshot.DirectParent is not null && snapshot.DirectSlot >= 0 && snapshot.DirectSlot < snapshot.DirectParent.Slots.Count)
        {
            if (snapshot.DirectParent.Slots[snapshot.DirectSlot] == item.ClockManagerIndex) snapshot.DirectParent.Slots[snapshot.DirectSlot] = 0;
            if (snapshot.DirectSlot > 0 && snapshot.DirectSlot - 1 < snapshot.DirectParent.FootprintSlots.Count &&
                snapshot.DirectParent.FootprintSlots[snapshot.DirectSlot - 1].OccupantObjectId == item.ClockManagerIndex)
                snapshot.DirectParent.FootprintSlots[snapshot.DirectSlot - 1].OccupantObjectId = 0;
        }

        InitializeObjectData(item, snapshot.PreservedData);

        if (snapshot.RestoreParentSlot && snapshot.Parent is not null)
        {
            var parent = snapshot.Parent;
            var parentSlot = snapshot.ParentSlot;
            var existingSlotKey = parentSlot > 0 && parentSlot - 1 < parent.FootprintSlots.Count
                ? parent.FootprintSlots[parentSlot - 1].OccupantObjectId
                : parent.Slots[parentSlot];
            var displacedPeer = existingSlotKey > 0 && existingSlotKey != item.ClockManagerIndex
                ? ObjectTableEntry(existingSlotKey)
                : null;
            if (displacedPeer is not null &&
                (ReferenceEquals(displacedPeer, item) ||
                 IsInSlotSubtree(item, displacedPeer) ||
                 IsInSlotSubtree(displacedPeer, item)))
            {
                displacedPeer = null;
            }
            if (displacedPeer is not null) DetachDirectPlacementLink(displacedPeer);
            parent.Slots[parentSlot] = item.ClockManagerIndex;
            if (parentSlot > 0 && parentSlot - 1 < parent.FootprintSlots.Count)
                parent.FootprintSlots[parentSlot - 1].OccupantObjectId = item.ClockManagerIndex;
            item[ObjectData.ParentId] = parent.ClockManagerIndex;
            item[ObjectData.ContainerId] = parent.ClockManagerIndex;
            item[ObjectData.SlotNumber] = parentSlot;
            item[ObjectData.SlotHierNumber] = parentSlot;
            if (displacedPeer is not null && item.ClockManagerIndex > 0)
            {
                if (item.Slots.Count == 0) item.Slots.Add(0);
                item.Slots[0] = displacedPeer.ClockManagerIndex;
                displacedPeer[ObjectData.ParentId] = item.ClockManagerIndex;
                displacedPeer[ObjectData.SlotHierNumber] = 0;
            }
        }
    }

    /// <summary>Resets an object after a script error by temporarily unplacing it, clearing instance data
    /// and temps, queuing its reset tree (function 1), and attempting to restore its prior placement.</summary>
    private void ResetObjectAfterScriptError(XObject item, TreeStack? activeStack)
    {
        if (item.MultiTileMasterId != 0 || item.MultiTileNextId != 0)
        {
            var lead = item.MultiTileMasterId != 0 ? ObjectById(item.MultiTileMasterId) ?? item : item;
            var group = MultiTileMembers(lead);
            if (group.Count > 1)
            {
                // Matches Sims.exe cXMTObject::Reset (004231a8.c): unplace/initialize all parts,
                // run init (function 0) on every part, and re-place the group so cross-part
                // placement callbacks (e.g. stair BHAV 4122) see initialized sibling Attributes.
                var snapshots = new List<ResetObjectSnapshot>(group.Count);
                foreach (var member in group)
                {
                    if (member is XPerson resettingMember)
                    {
                        InteractionLifecycle.FinalizeActor(resettingMember);
                        ResetPersonAnimatorForChangedCode(resettingMember);
                    }
                    else InteractionLifecycle.FinalizeRink(member);
                    snapshots.Add(CaptureResetObjectSnapshot(member));
                }
                foreach (var snapshot in snapshots)
                {
                    SavedExecutionStacks.Remove(snapshot.Item.Id);
                    DetachAndInitializeResetObject(snapshot);
                }
                foreach (var snapshot in snapshots)
                    FinishObjectInitializer(snapshot.Item, runTick: true, finishMultiTileMember: true);

                var leadSnapshot = snapshots[0];
                if (lead.RuntimeClass is SimsEngine.Formats.ObjectClass.MultiTileObject or SimsEngine.Formats.ObjectClass.Portal)
                {
                    if (leadSnapshot.RestoreParentSlot || leadSnapshot.ValidPriorTile)
                        PlaceObject(lead, leadSnapshot.PreviousPosition, activeStack ?? TreeRunner.CreateTransientStack(this, lead),
                            leadSnapshot.RestoreParentSlot ? leadSnapshot.Parent : null,
                            leadSnapshot.RestoreParentSlot ? checked((short)leadSnapshot.ParentSlot) : (short)0);
                }
                else
                {
                    foreach (var snapshot in snapshots)
                    {
                        if (snapshot.RestoreParentSlot || snapshot.ValidPriorTile)
                            PlaceObject(snapshot.Item, snapshot.PreviousPosition, activeStack ?? TreeRunner.CreateTransientStack(this, snapshot.Item),
                                snapshot.RestoreParentSlot ? snapshot.Parent : null,
                                snapshot.RestoreParentSlot ? checked((short)snapshot.ParentSlot) : (short)0);
                    }
                }
                foreach (var member in group) StopObjectSounds(member.Id);
                return;
            }
        }

        if (item is XPerson resettingPerson)
        {
            InteractionLifecycle.FinalizeActor(resettingPerson);
            ResetPersonAnimatorForChangedCode(resettingPerson);
        }
        else InteractionLifecycle.FinalizeRink(item);
        var singleSnapshot = CaptureResetObjectSnapshot(item);
        DetachAndInitializeResetObject(singleSnapshot);
        // Runs init (function 0) and the initial tick only for lead/single objects (cXObject::Reset 00431c90.c:70).
        FinishObjectInitializer(item, runTick: true, finishMultiTileMember: false);

        if (singleSnapshot.RestoreParentSlot || singleSnapshot.ValidPriorTile)
            PlaceObject(item, singleSnapshot.PreviousPosition, activeStack,
                singleSnapshot.RestoreParentSlot ? singleSnapshot.Parent : null,
                singleSnapshot.RestoreParentSlot ? checked((short)singleSnapshot.ParentSlot) : (short)0);
        StopObjectSounds(item.Id);
    }

    /// <summary>Snapshots object data fields that must be preserved across a reset before instance storage
    /// is cleared.</summary>
    private static Dictionary<ObjectData, short> PreservedInitializerData(XObject item) => new()
    {
        [ObjectData.Direction] = item[ObjectData.Direction],
        [ObjectData.BirthMinute] = item[ObjectData.BirthMinute],
        [ObjectData.BirthHour] = item[ObjectData.BirthHour],
        [ObjectData.Room] = item[ObjectData.Room],
        [ObjectData.CurrentValue] = item[ObjectData.CurrentValue],
        [ObjectData.BirthYear] = item[ObjectData.BirthYear],
        [ObjectData.BirthMonth] = item[ObjectData.BirthMonth],
        [ObjectData.BirthDay] = item[ObjectData.BirthDay],
        [ObjectData.SlotCount] = item[ObjectData.SlotCount],
    };

    /// <summary>Clears instance data, attributes, and temp registers, restores preserved identity fields,
    /// and queues the reset tree (function 1).</summary>
    private void InitializeObjectData(XObject item, Dictionary<ObjectData, short> preservedData)
    {
        // Clears instance data, attributes, and temps without wiping type attributes.
        Array.Clear(item.Data);
        item[ObjectData.ObjectId] = item.ClockManagerIndex;
        item[ObjectData.ObjectVersion] = item.TypeRecord?.ObjectVersion ?? 0;
        foreach (var (field, value) in preservedData)
            item[field] = field == ObjectData.Direction ? (short)(value & 7) : value;
        if (CurrentDate is { } currentDate)
            RecomputeObjectAge(item, currentDate);
        item[ObjectData.SlotHierNumber] = -1;
        Array.Clear(item.Attributes);
        Array.Clear(item.Temporaries);
        item.SavedStack.Clear();
        item.SavedStackSize = 0;

        RunObjectResetInitializer(item);
    }

    private void FinishObjectInitializer(XObject item, bool runTick, bool finishMultiTileMember = false,
        XObject? autonomyRoot = null)
    {
        if (item.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
        if (item.Definition is not { Length: > (int)ObjdField.MasterId } definition ||
            (definition[(int)ObjdField.MasterId] != 0 && !finishMultiTileMember)) return;
        var callerLocation = CaptureExecutionLocation();
        try
        {
            if (FunctionTreeId(item, 0) is var initTree && initTree != 0 && item.TypeRecord?.BehaviorScope is { } scope)
                TreeRunner.RunWithParameters(this, item, scope, initTree, 0, []);
            if (runTick && SavedExecutionStacks.ContainsKey(item.Id))
                TreeRunner.RunRestoredObjectStack(this, item);
        }
        finally
        {
            RestoreExecutionLocation(callerLocation);
        }
        if (item.ClockManagerIndex <= 0 || !ReferenceEquals(item, ObjectById(item.Id))) return;
        ClockManager.SetFlag(item.ClockManagerIndex, 1, item[ObjectData.SimulateOnPause] != 0);
        var root = autonomyRoot ?? InteractionMenus.Target(this, item);
        InitializeAutonomyCandidate(item, root, root.TypeRecord?.Interactions);
    }

    /// <summary>Initializes a newly created object in the held cursor state (-16, -16), applies its
    /// creation timestamp, sets initial value from OBJD price, and queues reset execution.</summary>
    public void InitializeCreatedObject(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item, ObjectById(item.Id)))
            throw new InvalidOperationException($"Object {item.Id} is not registered in this simulation world.");
        PrepareCreatedObject(item);
        InitializeObjectData(item, PreservedInitializerData(item));
        FinishObjectInitializer(item, runTick: true);
        if (item.Definition is { Length: > (int)ObjdField.Price } definition)
            item[ObjectData.CurrentValue] = definition[(int)ObjdField.Price];
        item.Flags |= 0x80u;
        ClockManager.SetTickEnabled(item, true);
        DispatchNotice(0xe2, item.ClockManagerIndex);
    }

    /// <summary>Binds a person instance to their neighborhood person record, assigning their neighbor ID,
    /// copying persistent data fields, and synchronizing family membership.</summary>
    public NeighborState? BindPersonToNeighborRecord(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (!ReferenceEquals(person, ObjectById(person.Id)))
            throw new InvalidOperationException($"Person {person.Id} is not registered in this simulation world.");
        var typeKey = person.TypeRecord?.Key;
        var neighbor = typeKey is null ? null : Neighbors.Values.FirstOrDefault(item => item.TypeRecordKey == typeKey.Value);
        if (neighbor is null && typeKey is not null)
        {
            neighbor = AddCreatedPersonNeighbor(person, typeKey.Value);
            person[PersonData.NeighborId] = neighbor.Id;
            SyncPersonScopeWithNeighborInventory(person, neighbor.Id);
            if (Neighborhood.TrackingEnabled) Neighborhood.OnPersonInstantiated(this, person);
            return neighbor;
        }
        if (neighbor is not null)
        {
            person[PersonData.NeighborId] = neighbor.Id;
            SyncPersonScopeWithNeighborInventory(person, neighbor.Id);
            var version = neighbor.PersonRecordVersion;
            if (version > 0)
            {
                ReadOnlySpan<(int Field, int MinVersion)> versionedFields =
                [
                    (2, 1), (3, 1), (4, 1), (5, 1), (6, 1), (7, 1), (9, 1), (10, 1), (0x0b, 1), (0x0c, 1),
                    (0x10, 1), (0x14, 1), (0x0f, 1), (0x1a, 1), (0x11, 1), (0x12, 1),
                    (0x2e, 1), (0x2f, 1), (0x30, 1), (0x31, 1), (0x32, 1), (0x33, 1), (0x34, 1), (0x35, 1),
                    (0x0d, 1), (0x0e, 1), (0x3a, 1), (0x3c, 1), (0x3d, 1),
                    (0x38, 2), (0x39, 2), (0x3f, 2),
                    (0x41, 3),
                    (0x43, 4), (0x44, 4),
                    (0x45, 5), (0x46, 1),
                    (0x4a, 6),
                    (0x36, 7), (0x37, 7), (0x4b, 7), (0x4c, 7),
                    (0x50, 8), (0x51, 8),
                    (0x52, 9),
                    (0x53, 10), (0x54, 10), (0x55, 10), (0x56, 10),
                ];
                foreach (var (field, minVersion) in versionedFields)
                {
                    if (version >= minVersion && field != (int)PersonData.FamilyNumber)
                        person.PersonData[field] = neighbor.PersonData[field];
                }
            }
            // Preserves the saved family number on unpersisted catalog or tutorial Sims.
            if (version > 0 || neighbor.PersonData[(int)PersonData.FamilyNumber] != 0)
                person[PersonData.FamilyNumber] = neighbor.PersonData[(int)PersonData.FamilyNumber];
            if (Neighborhood.TrackingEnabled) Neighborhood.OnPersonInstantiated(this, person);
        }
        return neighbor;
    }

    private void SyncPersonScopeWithNeighborInventory(XPerson person, short neighborId)
    {
        if (neighborId <= 0) return;
        var inventory = NeighborInventory(neighborId);
        if (InteractionScopes.TryGetValue(person.InteractionScopeKey5CA, out var existing))
        {
            if (existing.IsBackedBy(inventory)) return;
            for (short i = 0; i < existing.Count; i++)
            {
                if (existing.TryGet(i, out var entry))
                    inventory.Add(entry.Value, entry.Mode, unchecked((short)entry.Result));
            }
            if (existing.Count > 0) inventory.MarkChanged();
            if (!existing.TryBindResolver(() => NeighborInventory(neighborId)))
            {
                var backed = new InteractionScopeTable(() => NeighborInventory(neighborId));
                backed.AvatarObjects.AddRange(existing.AvatarObjects);
                InteractionScopes[person.InteractionScopeKey5CA] = backed;
            }
        }
        else
        {
            InteractionScopes[person.InteractionScopeKey5CA] = new InteractionScopeTable(() => NeighborInventory(neighborId));
        }
    }

    /// <summary>Initializes a created person from their neighborhood record, copying persistent fields and
    /// relationship tables.</summary>
    public void InitializeCreatedPerson(XPerson person)
    {
        var neighbor = BindPersonToNeighborRecord(person);
        if (neighbor is not null)
        {
            foreach (var (otherId, values) in neighbor.Relationships.Snapshot())
            {
                var targetId = checked((short)otherId);
                person.Relationships.Resize(targetId, values.Count);
                for (var index = 0; index < values.Count; index++)
                    person.Relationships.Set(targetId, index, values[index]);
            }
        }

        // Randomizes interests only when all eight base personality values are uninitialized at ten or
        // below.
        if (Enumerable.Range(0x2e, 8).All(field => person.PersonData[field] <= 10))
            RandomizeCreatedPersonInterests(person);
    }

    /// <summary>Runs the 'init visitor' action synchronously for arriving non-household characters.</summary>
    internal void ArriveCreatedPerson(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (!ReferenceEquals(person, ObjectById(person.Id)) || person[PersonData.PersonType] == 0 ||
            person.TypeRecord?.BehaviorScope is not Content.BehaviorScope scope) return;
        var tree = scope.FindNamedTree("init visitor");
        if (tree == 0) return;
        TreeRunner.RunWithParameters(this, person, scope, tree, person.ClockManagerIndex, []);
    }

    private NeighborState AddCreatedPersonNeighbor(XPerson person, int typeKey)
    {
        // Allocates the first empty slot or appends to assign a one-based neighbor ID.
        var slot = NeighborWalkOrder.FindIndex(neighbor => neighbor is null);
        if (!NeighborRosterComplete && (slot < 0 || slot >= CompleteNeighborSlotPrefix))
            throw new InvalidOperationException("Cannot allocate a neighbor id because the loaded neighborhood roster has an unknown tail.");
        if (slot < 0)
        {
            slot = NeighborWalkOrder.Count;
            NeighborWalkOrder.Add(null);
        }
        var id = checked((short)(slot + 1));
        while (Neighbors.ContainsKey(id))
        {
            if (!NeighborRosterComplete)
                throw new InvalidOperationException("Cannot allocate a neighbor id because the loaded neighborhood roster has an unknown tail.");
            slot = NeighborWalkOrder.Count;
            NeighborWalkOrder.Add(null);
            id = checked((short)(slot + 1));
        }
        var neighbor = new NeighborState(id, person.Definition.ToArray())
        {
            TypeRecordKey = typeKey,
            // Character objects use OBJD category flag 4 for friend eligibility.
            FriendEligibilityObjectFlagsB4 = 4u,
        };
        ClearRemovedNeighbor(id);
        Neighbors.Add(id, neighbor);
        NeighborWalkOrder[slot] = neighbor;
        return neighbor;
    }

    private void RandomizeCreatedPersonInterests(XPerson person)
    {
        Randomize([0x2e, 0x2f, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37], [4, 3, 3]);
        Randomize([0x0d, 0x0e, 0x10, 0x14, 0x1a], [1, 2, 2]);

        void Randomize(ReadOnlySpan<int> fields, int[] remaining)
        {
            foreach (var field in fields)
            {
                int category;
                do category = (int)(NextRandom() % 3); while (remaining[category] == 0);
                remaining[category]--;
                var value = category switch
                {
                    0 => (int)(NextRandom() & 3),
                    1 => (int)(NextRandom() % 3) + 4,
                    _ => (int)(NextRandom() & 3) + 7,
                };
                person.PersonData[field] = unchecked((short)(value * 100));
            }
        }
    }

    /// <summary>Initializes a multi-tile object group by creating member links, computing rotated tile
    /// offsets, and running initialization and first ticks across all members.</summary>
    public void InitializeCreatedObjectGroup(IReadOnlyList<XObject> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count < 1) throw new ArgumentException("A created multi-tile group must include at least one member.", nameof(group));
        var root = group[0];
        for (var index = 0; index < group.Count; index++)
        {
            var member = group[index];
            if (!ReferenceEquals(member, ObjectById(member.Id)))
                throw new InvalidOperationException($"Object {member.Id} is not registered in this simulation world.");
            if (index == 0)
            {
                if (member.MultiTileMasterId != 0)
                    throw new InvalidOperationException("A created multi-tile group root cannot name another master.");
            }
            else if (member.MultiTileMasterId != root.Id)
            {
                throw new InvalidOperationException($"Multi-tile member {member.Id} does not name root {root.Id}.");
            }
            var expectedNext = index + 1 < group.Count ? group[index + 1].Id : (short)0;
            if (member.MultiTileNextId != expectedNext)
                throw new InvalidOperationException($"Multi-tile member {member.Id} has an incomplete group link.");
        }

        foreach (var member in group)
        {
            PrepareCreatedObject(member);
            if (member[ObjectData.CurrentValue] == 0 && member.Definition is { Length: > (int)ObjdField.Price } definition)
                member[ObjectData.CurrentValue] = definition[(int)ObjdField.Price];
        }
        foreach (var member in group) InitializeObjectData(member, PreservedInitializerData(member));

        foreach (var member in group) FinishObjectInitializer(member, runTick: true);
        RefreshMultiTileOffsetsAfterRotation(root);

        foreach (var member in group)
        {
            FinishObjectInitializer(member, runTick: true, finishMultiTileMember: true);
            member.Flags |= 0x80u;
            ClockManager.SetTickEnabled(member, true);
        }
        DispatchNotice(0xe2, root.ClockManagerIndex);
    }

    private void PrepareCreatedObject(XObject item)
    {
        SetObjectPosition(item, new ObjectPosition(-16, -16, item.Position.Height));
        item[ObjectData.SlotNumber] = -1;
        item[ObjectData.SlotHierNumber] = -1;
        item[ObjectData.SlotCount] = checked((short)(item.FootprintSlots.Count == 0 &&
            item.Definition is { Length: > (int)ObjdField.SlotsId } slots && slots[(int)ObjdField.SlotsId] == 0
                ? -1 : item.FootprintSlots.Count));
        Array.Clear(item.Attributes);
        if (CurrentDate is { } date)
        {
            item[ObjectData.BirthYear] = date.Year;
            item[ObjectData.BirthMonth] = date.Month;
            item[ObjectData.BirthDay] = date.Day;
        }
        item[ObjectData.BirthHour] = checked((short)Clock.Hour);
        item[ObjectData.BirthMinute] = checked((short)Clock.Minute);
    }

    /// <summary>Determines whether an object can be picked up by checking that it is movable, not in use,
    /// within buildable boundaries, not burned, and that all slotted objects can also be picked up, unless
    /// the move-objects cheat is active.</summary>
    public bool CanPickUpObject(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        LastPlacementFailureCode = 0;
        LastPlacementBlockingObjectId = 0;
        if (MoveObjects) return true;
        var visited = new HashSet<short>();
        bool CanPickUpTree(XObject root)
        {
            var members = MultiTileMembers(root);
            var lead = members[0];
            foreach (var member in members)
            {
                if (!visited.Add(member.Id)) continue;
                var isPerson = member is XPerson ||
                               member.TypeRecord?.ObjectType == XPerson.PersonType ||
                               (member.Definition is { Length: > (int)ObjdField.Type } &&
                                member.Definition[(int)ObjdField.Type] == XPerson.PersonType);
                if (isPerson || !ReferenceEquals(member, ObjectById(member.Id))) return false;
                if ((member[ObjectData.MovementFlags] & 2) == 0)
                {
                    LastPlacementFailureCode = 0x25;
                    return false;
                }
                if (member[ObjectData.UseCount] >= 1 || (member[ObjectData.Flags] & 0x20) != 0)
                {
                    LastPlacementFailureCode = 0x26;
                    return false;
                }
                if (ReferenceEquals(member, lead))
                {
                    var tileX = member.Position.X >> 4;
                    var tileY = member.Position.Y >> 4;
                    if (tileX >= 0 && tileY >= 0 && tileX < LotGeometry.LotDimension && tileY < LotGeometry.LotDimension &&
                        (LotTileFlags?.InsideLot(tileX, tileY) == false ||
                         (BuildModeType6PlacementFlagsAt(tileX, tileY, 1) & 0x20) != 0))
                    {
                        LastPlacementFailureCode = 0x27;
                        return false;
                    }
                }
                foreach (var child in EnumerateContainedSlotChildren(member))
                    if (!CanPickUpTree(child)) return false;
            }
            return true;
        }
        return CanPickUpTree(item);
    }

    /// <summary>Moves an object using the hand tool, temporarily picking it up and placing it at the new
    /// destination, restoring its original position if placement fails.</summary>
    public bool MoveObject(XObject item, ObjectPosition destination, short direction)
    {
        if (!CanPickUpObject(item)) throw new InvalidOperationException("Object cannot be moved");
        var members = MultiTileMembers(item);
        if (members.Count > 1)
        {
            var original = members.ToDictionary(member => member,
                member => (member[ObjectData.Direction], member.SavedMultiTileState));
            var directionFlag = direction switch
            {
                0 => (byte)1,
                2 => (byte)4,
                4 => (byte)16,
                6 => (byte)64,
                _ => throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be 0, 2, 4 or 6."),
            };
            var placed = false;
            try
            {
                var checkStack = TreeRunner.CreateTransientStack(this, item);
                ApplyEditorFacing(item, members, directionFlag);
                if (!CanPlaceObject(item, destination, checkStack: checkStack)) return false;
                foreach (var member in members)
                    RunFunctionToCompletion(member, 10);
                PlaceObject(item, destination, checkStack: checkStack, cursorGroup: MultiTilePlacementCursorGroup.Create(item, members));
                RunEditorUserPlacementCallbacks(members);
                WakeSimulateOnPauseObjects();
                placed = true;
                return true;
            }
            finally
            {
                if (!placed) RestoreEditorFacing(original);
            }
        }
        var origin = item.Position;
        var originalDirection = item[ObjectData.Direction];
        var singleDelta = (direction - originalDirection + 8) & 7;
        BeginObjectRemoval(item, 1);
        item[ObjectData.Direction] = direction;
        RotateContainedSlotChildren(item, singleDelta);
        if (CanPlaceObject(item, destination))
        {
            PlaceObject(item, destination);
            RunEditorUserPlacementCallbacks(members);
            WakeSimulateOnPauseObjects();
            return true;
        }
        item[ObjectData.Direction] = originalDirection;
        if (singleDelta != 0) RotateContainedSlotChildren(item, (8 - singleDelta) & 7);
        PlaceObject(item, origin);
        return false;
    }

    /// <summary>Determines whether an object can be sold, requiring it to be pickable, marked sellable in
    /// its movement flags, and having all container slots empty unless the move-objects cheat is active.
    /// </summary>
    public bool CanSellObject(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (MoveObjects) return true;
        if (!CanPickUpObject(item)) return false;
        foreach (var member in MultiTileMembers(item))
        {
            if ((member[ObjectData.MovementFlags] & 8) == 0) return false;
            if (EnumerateContainedSlotChildren(member).Any()) return false;
        }
        return true;
    }

    /// <summary>Calculates an object's sale value from its current value, halved if any part of its
    /// multi-tile group is broken, using the lead object's value for groups.</summary>
    public short ObjectSaleValue(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var members = MultiTileMembers(item);
        var master = members.FirstOrDefault() ?? item;
        var broken = members.Any(member => member[ObjectData.RepairState] != 0) || item[ObjectData.RepairState] != 0;
        var value = master[ObjectData.CurrentValue];
        return broken ? (short)(value / 2) : value;
    }

    /// <summary>Sells an object using the hand tool, running cleanup (function 12) across all parts and
    /// slotted occupants before deleting them and returning the refund value.</summary>
    public int SellObject(XObject item)
    {
        if (!CanSellObject(item)) throw new InvalidOperationException("Object cannot be sold");
        var refund = ObjectSaleValue(item);
        DemolishObject(item, runCleanup: true);
        return refund;
    }

    /// <summary>Calculates resale value by build mode type: plants refund nothing; architectural elements
    /// (doors, windows, stairs, fireplaces, columns) refund 80%; buy-mode objects and pool accessories
    /// refund full value.</summary>
    public static int HandResaleValue(int baseSaleValue, int buildModeType) => buildModeType switch
    {
        4 => 0,
        1 or 2 or 3 or 5 or 6 => (int)(baseSaleValue * 0.8f + 0.5f),
        _ => baseSaleValue,
    };

    /// <summary>Determines whether an object is placed strictly inside the lot's outer edge boundary.</summary>
    public static bool IsInsideLotEdge(XObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return TileInsideLotEdge(target);
    }

    /// <summary>Demolishes an object and its slotted occupants, optionally running cleanup (function 12)
    /// before removal.</summary>
    public void DemolishObject(XObject item, bool runCleanup = false)
    {
        var doomed = new List<XObject>();
        void Collect(XObject root)
        {
            foreach (var member in MultiTileMembers(root))
            {
                if (doomed.Contains(member)) continue;
                doomed.Add(member);
                foreach (var occupant in EnumerateContainedSlotChildren(member))
                {
                    var isPerson = occupant is XPerson ||
                                   occupant.TypeRecord?.ObjectType == XPerson.PersonType ||
                                   (occupant.Definition is { Length: > (int)ObjdField.Type } &&
                                    occupant.Definition[(int)ObjdField.Type] == XPerson.PersonType);
                    if (!isPerson) Collect(occupant);
                }
            }
        }
        Collect(item);
        var callerLocation = CaptureExecutionLocation();
        if (runCleanup)
        foreach (var member in doomed)
            if (FunctionTreeId(member, 12) is var tree && tree != 0 && member.TypeRecord?.BehaviorScope is { } scope)
                TreeRunner.RunWithParameters(this, member, scope, tree, 0, []);
        RestoreExecutionLocation(callerLocation);
        foreach (var member in doomed) BeginObjectRemoval(member, 1);
        foreach (var member in doomed) KillObject(member.Id);
        WakeSimulateOnPauseObjects();
    }

    /// <summary>Updates an object's age in days and months by subtracting its creation date from the
    /// current simulation date.</summary>
    private static void RecomputeObjectAge(XObject item, SimulationDate currentDate)
    {
        var years = unchecked((short)(currentDate.Year - item[ObjectData.BirthYear]));
        var months = unchecked((short)(currentDate.Month - item[ObjectData.BirthMonth]));
        if (currentDate.Day < item[ObjectData.BirthDay]) months = unchecked((short)(months - 1));
        if (months < 0)
        {
            months = unchecked((short)(months + 12));
            years = unchecked((short)(years - 1));
        }
        item[ObjectData.SimAge] = years;
        item[ObjectData.AgeInMonths] = unchecked((short)(months + years * 12));
    }

    /// <summary>Applies daily object depreciation invoked during midnight rollover.</summary>
    private void DepreciateObjectForDay(XObject item)
    {
        if (item is XPerson || item.Definition is not { Length: > (int)ObjdField.SelfDepreciating }) return;
        if (item.Definition[(int)ObjdField.Type] == XPerson.PersonType) return;

        short selfDepreciating;
        short price;
        short initial;
        short daily;
        short limit;

        if (item.Definition[(int)ObjdField.MasterId] != 0)
        {
            if (item.RuntimeClass is not null and not SimsEngine.Formats.ObjectClass.MultiTileObject) return;
            var lead = item.MultiTileMasterId == 0 ? item : ObjectById(item.MultiTileMasterId) ?? item;
            if (!ReferenceEquals(item, lead)) return;

            var masterEntry = item.TypeRecord?.AlternativeTypeKey is { } altKey
                ? ObjectRegistry?.Find(unchecked((uint)altKey))
                : item.TypeRecord is { Key: var key } && ObjectRegistry?.Find(unchecked((uint)key)) is { } memberEntry
                    ? ObjectRegistry.FindAlternativeType(memberEntry)
                    : null;
            if (masterEntry is not null)
            {
                selfDepreciating = unchecked((short)masterEntry.Definition[ObjdField.SelfDepreciating]);
                price = unchecked((short)masterEntry.Definition[ObjdField.Price]);
                initial = unchecked((short)masterEntry.Definition[ObjdField.InitialDepreciation]);
                daily = unchecked((short)masterEntry.Definition[ObjdField.DailyDepreciation]);
                limit = unchecked((short)masterEntry.Definition[ObjdField.DepreciationLimit]);
            }
            else if (ObjectRegistry is not null)
            {
                return;
            }
            else
            {
                selfDepreciating = item.Definition[(int)ObjdField.SelfDepreciating];
                price = item.TypeRecord?.AlternativeObjectPrice ?? item.Definition[(int)ObjdField.Price];
                initial = item.Definition[(int)ObjdField.InitialDepreciation];
                daily = item.Definition[(int)ObjdField.DailyDepreciation];
                limit = item.Definition[(int)ObjdField.DepreciationLimit];
            }
        }
        else
        {
            selfDepreciating = item.Definition[(int)ObjdField.SelfDepreciating];
            price = item.Definition[(int)ObjdField.Price];
            initial = item.Definition[(int)ObjdField.InitialDepreciation];
            daily = item.Definition[(int)ObjdField.DailyDepreciation];
            limit = item.Definition[(int)ObjdField.DepreciationLimit];
        }

        if (selfDepreciating != 0) return;
        if (price <= 0 && initial == 0 && daily == 0 && limit == 0) return;

        var current = item[ObjectData.CurrentValue];
        var delta = current == price && initial != 0 ? initial : daily;
        current = unchecked((short)(current - delta));
        if (current < limit) current = limit;
        item[ObjectData.CurrentValue] = current;
    }

    /// <summary>Tracks type GUIDs whose saved OBJT tree checksums differ from current function 0 and 1
    /// fingerprints, indicating modified behavior code.</summary>
    public HashSet<uint> ChangedCodeTypeGuids { get; } = [];
    internal Dictionary<short, ObjectTypeEntry> CurrentObjectTypes { get; } = [];

    /// <summary>Resets objects whose behavior code changed since saving, notifying active Sims, running
    /// cleanup, and executing function 0 to completion.</summary>
    public void ResetObjectsWithChangedCode()
    {
        var resetIds = new HashSet<short>();
        foreach (var item in WalkOrder.ToArray())
        {
            if (!ReferenceEquals(item, ObjectById(item.Id)) || resetIds.Contains(item.Id)) continue;
            if (item is not XPerson && (item.MultiTileMasterId != 0 || item.MultiTileNextId != 0))
            {
                var lead = item.MultiTileMasterId != 0 ? ObjectById(item.MultiTileMasterId) ?? item : item;
                var group = MultiTileMembers(lead);
                if (group.Count > 1 && group.Any(m => FunctionTreeId(m, 3) == 0))
                {
                    var anyResetTarget = false;
                    foreach (var member in group)
                    {
                        var memberGuid = unchecked((uint)(member.TypeRecord?.Key ?? 0));
                        if (ChangedCodeTypeGuids.Contains(memberGuid) || memberGuid == 0x3C08965F)
                        {
                            anyResetTarget = true;
                            break;
                        }
                    }
                    if (!anyResetTarget) continue;
                    foreach (var member in group)
                    {
                        NotifyPersonObjectReferenceRecipients(member);
                        SavedExecutionStacks.Remove(member.Id);
                    }
                    if (!group.Any(m => (m.Flags & 0x40u) != 0))
                        ResetObjectAfterScriptError(lead, TreeRunner.CreateTransientStack(this, lead));
                    else
                    {
                        foreach (var member in group)
                        {
                            BeginObjectRemoval(member, 1);
                            member.SavedStack.Clear();
                            member.SavedStackSize = 0;
                            StopObjectSounds(member.Id);
                        }
                    }
                    foreach (var member in group)
                    {
                        resetIds.Add(member.Id);
                        if (member.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(member.ClockManagerIndex, -1);
                    }
                    continue;
                }
            }

            var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
            var changed = ChangedCodeTypeGuids.Contains(guid);
            if (!changed && guid != 0x3C08965F) continue;
            NotifyPersonObjectReferenceRecipients(item);
            SavedExecutionStacks.Remove(item.Id);
            if (item is XPerson person) ResetPersonForChangedCode(person);
            else ResetObjectForChangedCode(item);
            resetIds.Add(item.Id);
            if (item.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
            if (changed && (item is XPerson || FunctionTreeId(item, 3) != 0)) RunFunctionToCompletion(item, 0);
        }
        foreach (var (typeIndex, updatedType) in CurrentObjectTypes)
            SavedObjectTypes[typeIndex] = updatedType;
        CurrentObjectTypes.Clear();
        ChangedCodeTypeGuids.Clear();
        RefreshSavedPortalWallStyles();
    }

    public (IReadOnlyList<(string Guid, short ObjectId, string Name, int Refund)> RemovedObjects,
            IReadOnlyList<(string From, string To, short ObjectId)> MigratedObjects,
            int Refund,
            IReadOnlySet<short> ResetEntityIds) ApplyPackObjectMigration(
        Content.ObjectRegistry registry,
        IReadOnlyDictionary<short, (uint From, uint To)> remappedByTypeIndex,
        bool stripMissingObjects)
    {
        var removedObjects = new List<(string Guid, short ObjectId, string Name, int Refund)>();
        var migratedObjects = new List<(string From, string To, short ObjectId)>();
        var resetEntityIds = new HashSet<short>();
        var totalRefund = 0;

        var typeIndexByObjectId = SavedLotObjects?.Objects.ToDictionary(o => o.ObjectId, o => o.TypeIndex) ?? [];

        bool FrameReferencesSubject(short stackObject, short codeOwner, XObject subject)
        {
            if (stackObject == subject.Id || (subject.ClockManagerIndex > 0 && stackObject == subject.ClockManagerIndex))
                return true;
            return typeIndexByObjectId.TryGetValue(subject.Id, out var subjTi) && subjTi != 0 && codeOwner == subjTi;
        }

        bool PersonReferencesObject(XPerson person, XObject subject)
        {
            if (person.SavedPersonObjectReferences.Any(r => ReferenceEquals(r.Target, subject)) ||
                ReferenceEquals(person.GenericSimsCall02ObjectA54, subject) ||
                ReferenceEquals(person.CurrentActionRecord?.Owner, subject) ||
                ReferenceEquals(person.CurrentActionRecord?.Secondary, subject) ||
                person.NativeRoute?.AnchorObjectId == subject.Id ||
                person.NativeRouteParents.Any(r => r.AnchorObjectId == subject.Id) ||
                person.SavedStack.Any(f => FrameReferencesSubject(f.StackObject, f.CodeOwner, subject)))
                return true;
            var count = unchecked(person.ActionQueueEnd - person.ActionQueueCurrent);
            if (count is > 0 and <= 8)
            {
                for (var i = 0; i < count; i++)
                {
                    var slot = person.ActionQueue[(person.ActionQueueCurrent + i) & 7];
                    if (ReferenceEquals(slot.Owner, subject) || ReferenceEquals(slot.Secondary, subject))
                        return true;
                }
            }
            return SavedExecutionStacks.TryGetValue(person.Id, out var stack) &&
                   stack.Frames.Any(f => FrameReferencesSubject(f.StackObject, f.CodeOwner, subject));
        }

        void ResetReferencingEntities(XObject subject, HashSet<short>? excludedIds = null)
        {
            foreach (var other in WalkOrder.ToArray())
            {
                if (ReferenceEquals(other, subject) || (excludedIds is not null && excludedIds.Contains(other.Id))) continue;
                if (other is XPerson person)
                {
                    if (person[ObjectData.ParentId] == subject.Id ||
                        (subject.ClockManagerIndex > 0 && person[ObjectData.ParentId] == subject.ClockManagerIndex))
                    {
                        person[ObjectData.ParentId] = 0;
                        person[ObjectData.SlotHierNumber] = -1;
                        resetEntityIds.Add(person.Id);
                    }
                    if (PersonReferencesObject(person, subject))
                    {
                        ResetPersonForChangedCode(person);
                        RemoveQueuedActionsReferencing(person, subject);
                        person.CurrentActionRecord = ActionQueueSlot.EmptyRecord();
                        person.ActionQueueCurrent = 0;
                        person.ActionQueueEnd = 0;
                        Array.Clear(person.ActionQueue);
                        if (person.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(person.ClockManagerIndex, -1);
                        resetEntityIds.Add(person.Id);
                    }
                }
                else if (other[ObjectData.ParentId] == subject.Id ||
                         (subject.ClockManagerIndex > 0 && other[ObjectData.ParentId] == subject.ClockManagerIndex) ||
                         other.SavedStack.Any(f => FrameReferencesSubject(f.StackObject, f.CodeOwner, subject)) ||
                         (SavedExecutionStacks.TryGetValue(other.Id, out var oStack) &&
                          oStack.Frames.Any(f => FrameReferencesSubject(f.StackObject, f.CodeOwner, subject))))
                {
                    ResetObjectForChangedCode(other);
                    if (other.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(other.ClockManagerIndex, -1);
                    resetEntityIds.Add(other.Id);
                }
            }
        }

        if (remappedByTypeIndex.Count > 0)
        {
            foreach (var item in WalkOrder.ToArray())
            {
                if (!typeIndexByObjectId.TryGetValue(item.Id, out var typeIndex) ||
                    !remappedByTypeIndex.TryGetValue(typeIndex, out var remap)) continue;
                ChangedCodeTypeGuids.Add(remap.To);
                if (item.MultiTileMasterId == 0)
                    migratedObjects.Add(($"0x{remap.From:X8}", $"0x{remap.To:X8}", item.Id));
                resetEntityIds.Add(item.Id);
                ResetReferencingEntities(item);
            }
        }

        if (stripMissingObjects)
        {
            var handledIds = new HashSet<short>();
            foreach (var item in WalkOrder.ToArray())
            {
                if (item is XPerson || handledIds.Contains(item.Id)) continue;
                var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
                if (guid == 0 || registry.Find(guid) is not null) continue;
                var lead = item.MultiTileMasterId != 0 && ObjectById(item.MultiTileMasterId) is { } master ? master : item;
                if (handledIds.Contains(lead.Id)) continue;
                var group = WalkOrder
                    .Where(o => o is not XPerson && (o.Id == lead.Id || o.MultiTileMasterId == lead.Id))
                    .ToList();
                var groupIds = group.Select(o => o.Id).ToHashSet();
                var addedChild = true;
                while (addedChild)
                {
                    addedChild = false;
                    foreach (var o in WalkOrder)
                    {
                        if (o is not XPerson && !groupIds.Contains(o.Id) && groupIds.Contains(o[ObjectData.ParentId]))
                        {
                            group.Add(o);
                            groupIds.Add(o.Id);
                            addedChild = true;
                        }
                    }
                }
                foreach (var id in groupIds) handledIds.Add(id);
                var refund = group.Sum(part => Math.Max(0, (int)part[ObjectData.CurrentValue]));
                var leadGuid = unchecked((uint)(lead.TypeRecord?.Key ?? 0));
                var leadName = typeIndexByObjectId.TryGetValue(lead.Id, out var ti) &&
                               SavedObjectTypes.TryGetValue(ti, out var savedType) &&
                               !string.IsNullOrWhiteSpace(savedType.Name)
                    ? savedType.Name
                    : lead.TypeRecord?.Name ?? $"0x{leadGuid:X8}";
                removedObjects.Add(($"0x{leadGuid:X8}", lead.Id, leadName, refund));
                totalRefund += refund;

                foreach (var part in group)
                    ResetReferencingEntities(part, groupIds);
                foreach (var part in group)
                {
                    NotifyPersonObjectReferenceRecipients(part);
                    HierSever(part);
                    Remove(part.Id);
                }
            }

            foreach (var pair in SavedObjectTypes.ToArray())
            {
                var ti = pair.Key;
                var savedType = pair.Value;
                if (registry.Find(savedType.Guid) is null &&
                    !WalkOrder.Any(o => unchecked((uint)(o.TypeRecord?.Key ?? 0)) == savedType.Guid))
                {
                    SavedObjectTypes.Remove(ti);
                    CurrentObjectTypes.Remove(ti);
                    CodeOwnerTypeGuids.Remove(ti);
                    CodeOwnerScopes.Remove(ti);
                }
            }
        }

        return (removedObjects, migratedObjects, totalRefund, resetEntityIds);
    }

    /// <summary>Notifies all active Sims that a referenced object has been reset or removed, allowing them
    /// to clear interaction queues targeting it.</summary>
    public void NotifyPersonObjectReferenceRecipients(XObject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        for (var index = 0; index < WalkOrder.Count; index++)
        {
            if (WalkOrder[index] is not XPerson person || ReferenceEquals(person, subject)) continue;
            if (!person.TryBeginObjectReferenceNotification()) continue;
            var prefix = index > 0 ? WalkOrder.Take(index).ToArray() : [];
            try
            {
                if (person.SavedPersonObjectReferences.Any(reference => ReferenceEquals(reference.Target, subject)) ||
                    ReferenceEquals(person.GenericSimsCall02ObjectA54, subject))
                {
                    ResetPersonForChangedCode(person, notificationGuardHeld: true, subject: subject);
                }
                RemoveQueuedActionsReferencing(person, subject);
                ApplyPersonObjectReferenceActionChanges(person, subject);
                person.InvalidateRuntimeObjectReferences(subject);
                Host?.NotifyPersonObjectReference(person, subject);
            }
            finally
            {
                person.EndObjectReferenceNotification();
                if (index >= WalkOrder.Count || !ReferenceEquals(WalkOrder[index], person))
                {
                    var moved = -1;
                    for (var probe = Math.Min(index, WalkOrder.Count - 1); probe >= 0; probe--)
                    {
                        if (ReferenceEquals(WalkOrder[probe], person))
                        {
                            moved = probe;
                            break;
                        }
                    }
                    index = moved >= 0 ? moved : prefix.Count(item => WalkOrder.Contains(item)) - 1;
                }
            }
        }
    }

    /// <summary>Severs hierarchy links between an object and its parent slot, as well as any child objects
    /// slotted into it.</summary>
    public void HierSever(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var directParent = ObjectTableEntry(item[ObjectData.ParentId]) ?? ObjectById(item[ObjectData.ParentId]);
        var directSlot = item[ObjectData.SlotHierNumber];
        if (directParent is not null)
        {
            if (directSlot >= 0 && directSlot < directParent.Slots.Count)
            {
                if (directParent.Slots[directSlot] == item.ClockManagerIndex || directParent.Slots[directSlot] == item.Id)
                    directParent.Slots[directSlot] = 0;
                if (directSlot > 0 && directSlot - 1 < directParent.FootprintSlots.Count &&
                    (directParent.FootprintSlots[directSlot - 1].OccupantObjectId == item.ClockManagerIndex ||
                     directParent.FootprintSlots[directSlot - 1].OccupantObjectId == item.Id))
                    directParent.FootprintSlots[directSlot - 1].OccupantObjectId = 0;
            }
            for (var i = 0; i < directParent.Slots.Count; i++)
            {
                if (directParent.Slots[i] == item.ClockManagerIndex || directParent.Slots[i] == item.Id)
                    directParent.Slots[i] = 0;
            }
        }
        item[ObjectData.ParentId] = 0;
        item[ObjectData.ContainerId] = 0;
        item[ObjectData.SlotNumber] = 0;
        item[ObjectData.SlotHierNumber] = -1;

        foreach (var child in WalkOrder)
        {
            if (child[ObjectData.ParentId] == item.Id || child[ObjectData.ParentId] == item.ClockManagerIndex)
            {
                child[ObjectData.ParentId] = 0;
                child[ObjectData.ContainerId] = 0;
                child[ObjectData.SlotNumber] = 0;
                child[ObjectData.SlotHierNumber] = -1;
            }
        }
        for (var i = 0; i < item.Slots.Count; i++) item.Slots[i] = 0;
        for (var i = 0; i < item.FootprintSlots.Count; i++) item.FootprintSlots[i].OccupantObjectId = 0;
    }

    /// <summary>Resets an object after its code changes. If a cleanup tree (function 3) exists, runs it,
    /// clears in-use flags and the execution stack, and pushes the reset tree (function 1) while preserving
    /// attributes and slot hierarchy.</summary>
    public void ResetObjectForChangedCode(XObject item)
    {
        if (item is XPerson person)
        {
            ResetPersonForChangedCode(person);
            return;
        }
        SavedExecutionStacks.Remove(item.Id);
        if (FunctionTreeId(item, 3) == 0)
        {
            // Dying objects marked for deletion must not be re-initialized or re-placed.
            if ((item.Flags & 0x40u) != 0)
            {
                BeginObjectRemoval(item, 1);
                item.SavedStack.Clear();
                item.SavedStackSize = 0;
                StopObjectSounds(item.Id);
                return;
            }
            // Uses a transient execution stack to run placement callbacks during load.
            ResetObjectAfterScriptError(item, TreeRunner.CreateTransientStack(this, item));
            StopObjectSounds(item.Id);
            return;
        }
        RunFunctionToCompletion(item, 3);
        item[ObjectData.Flags] = unchecked((short)(item[ObjectData.Flags] & ~0x20));
        item[ObjectData.UseCount] = 0;
        item.SavedStack.Clear();
        item.SavedStackSize = 0;
        RunObjectResetInitializer(item);
        StopObjectSounds(item.Id);
    }

    /// <summary>Resets a Sim after code changes by clearing their execution stack and action queue, running
    /// cleanup (function 3), pushing reset (function 1), and invalidating interaction references.</summary>
    private void ResetPersonForChangedCode(XPerson person, bool notificationGuardHeld = false, XObject? subject = null)
    {
        if (!notificationGuardHeld && !person.TryBeginObjectReferenceNotification()) return;
        try
        {
            SavedExecutionStacks.Remove(person.Id);
            InteractionLifecycle.FinalizeActor(person);
            // Drains saved interaction references in LIFO order to decrement use counts.
            var flagSource = subject ?? person;
            while (person.SavedPersonObjectReferences.Count > 0)
            {
                var lastReference = person.SavedPersonObjectReferences[^1];
                PersonInteractionStack.Release(person, lastReference.Value - 1);
                if ((flagSource.Flags & 0x00001000u) != 0 || lastReference.Target is not { } target) continue;

                NotifyPersonObjectReferenceRecipients(target);
                if (target is XPerson targetPerson) ResetPersonForChangedCode(targetPerson);
                else ResetObjectForChangedCode(target);
            }
            person[global::SimsEngine.Simulation.PersonData.CensorshipFlags] = 0;
            ResetPersonAnimatorForChangedCode(person);
            StopObjectSounds(person.Id);
            person.RuntimeObjectReferences.Clear();
            foreach (var reference in person.RuntimeStateRecords)
            {
                reference.Target = null;
                reference.RawWord28 = 0;
            }
            ResetPersonPlacementAfterChangedCode(person);
            person.SavedStack.Clear();
            person.SavedStackSize = 0;
            RunObjectResetInitializer(person);
            RunFunctionToCompletion(person, 3);
            // Zeroes posture flags so a detached Sim never resumes idle while sitting.
            person[global::SimsEngine.Simulation.PersonData.Sitting] = 0;
            person[global::SimsEngine.Simulation.PersonData.WalkingUpStairs] = 0;
            person[global::SimsEngine.Simulation.PersonData.ObjectSocialObjectID] = 0;
            person[ObjectData.Hidden] = 0;
            person[ObjectData.RoutePreference] = 0;
            ApplyStartedInteractionAction(person, ActionQueueSlot.EmptyRecord());
            if (!notificationGuardHeld) person.ActionQueueCurrent = person.ActionQueueEnd = 0;
        }
        finally
        {
            if (!notificationGuardHeld) person.EndObjectReferenceNotification();
        }
    }

    /// <summary>Validates a Sim's position within lot boundaries during a reset, falling back to a search
    /// around the lot center if the original position is invalid or outside the border.</summary>
    private void ResetPersonPlacementAfterChangedCode(XPerson person)
    {
        var (holder, _) = PlacementSpec(person);
        var stack = TreeRunner.CreateTransientStack(this, person);
        if (holder is not null)
        {
            BeginObjectRemoval(person, 1);
            var detachedPosition = person.Position;
            var holderCandidate = detachedPosition;
            if (!CanPlaceObject(person, detachedPosition, stack))
            {
                var holderParams = new FindLocationParams
                {
                    UseCenter = true,
                    CenterX = detachedPosition.X,
                    CenterY = detachedPosition.Y,
                    Height = detachedPosition.Height,
                    Direction = -1,
                    ContinueFlag = 1,
                    FlagB = 1,
                };
                if (FindLocation(person, holderParams, out var found, stack))
                {
                    holderCandidate = new ObjectPosition(found.X, found.Y, detachedPosition.Height);
                }
                else
                {
                    holderParams.ContinueFlag = 0;
                    if (FindLocation(person, holderParams, out found, stack))
                        holderCandidate = new ObjectPosition(found.X, found.Y, detachedPosition.Height);
                }

            }

            PlaceObject(person, holderCandidate, stack);
            RefreshPersonPlacementCallbacksAfterMovement(person);
        }

        var position = person.Position;
        var size = LotGeometry.LotDimension;
        var tileX = position.X >> 4;
        var tileY = position.Y >> 4;
        var positionInsideLot = tileX > 0 && tileY > 0 && tileX < size - 1 && tileY < size - 1;
        if ((person.Flags & 0x00000040u) != 0 || positionInsideLot) return;

        BeginObjectRemoval(person, 1);
        var center = size / 2 * 16 + 8;
        var candidate = new ObjectPosition(center, center, person.Position.Height);
        if (!CanPlaceObject(person, candidate, stack))
        {
            var parameters = new FindLocationParams
            {
                UseCenter = true,
                CenterX = candidate.X,
                CenterY = candidate.Y,
                Height = person.Position.Height,
                Direction = -1,
                ContinueFlag = 1,
                FlagB = 1,
            };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (FindLocation(person, parameters, out var found, stack))
                {
                    candidate = new ObjectPosition(found.X, found.Y, person.Position.Height);
                    break;
                }
                parameters.ContinueFlag = 0;
            }
        }

        PlaceObject(person, candidate, stack);
        RefreshPersonPlacementCallbacksAfterMovement(person);
    }

    /// <summary>Clears active animation channels, route records, and bone displacements during a Sim reset,
    /// restoring the base body mesh and outfits from character definitions.</summary>
    private void ResetPersonAnimatorForChangedCode(XPerson person)
    {
        StopObjectSounds(person.Id);
        person.NativeRoute = null;
        person.NativeRouteParents.Clear();
        InvalidateAnimatorRootGrid(person);
        person.SetAnimatorDisplacements(0f, 0f, 0f);
        person.AnimationPlayback = null;
        person.LastAnimationPlayback = null;
        person.AnimatorDiscontinuityPending = true;
        person.AnimatorAnchorBone = null;
        person.AnimatorAnchorInitialized = false;
        person.AnimatorAnchorWorldPosition = Vector3.Zero;
        person.PrimaryAnimationWaiting = false;
        person.PrimaryAnimationEventsRun = 0;
        person.PrimaryAnimationBackwards = false;
        person.BackgroundAnimationPlayback = null;
        person.CarryAnimationPlayback = null;
        person.PendingAnimationEvents.Clear();
        person.BackgroundPracticePersonState = -1;
        person.CarryPracticeMode = 0;
        person.CarryPracticeCachedState = -1;
        person.AnimatorControlState = 3;
        person.AnimatorRouteTransitionState = 0;
        person.AnimatorPoolTransitionState = 0;
        person.AnimatorGaitEntryState = 0;
        person.AnimatorLocomotionFadeGate = 0;
        person.AnimatorStateTailField198 = 1;
        person.PresentationCharacter = null;
        person.ClearExtraParts();
        person[global::SimsEngine.Simulation.PersonData.HandGestureLeft] = 0;
        person[global::SimsEngine.Simulation.PersonData.HandGestureRight] = 0;
        person.NativeOutfitTransitionActive = false;
        if (person.TypeRecord?.AnimatorBodyStringMode is { } bodyStringMode)
            person.AnimatorBodyStringMode = bodyStringMode;
    }

    /// <summary>Runs an object's function table entry synchronously to completion on a new stack frame.</summary>
    public void RunFunctionToCompletion(XObject item, short function, short stackObject = 0)
    {
        if (FunctionTreeId(item, function) is not (var tree and not 0) || item.TypeRecord?.BehaviorScope is not { } scope)
            return;
        var callerLocation = CaptureExecutionLocation();
        TreeRunner.RunWithParameters(this, item, scope, tree, stackObject, []);
        RestoreExecutionLocation(callerLocation);
    }

    private void RunObjectResetInitializer(XObject item)
    {
        var type = item.TypeRecord;
        if (type?.FunctionTable is not { Count: > 1 } functions || type.BehaviorScope is not { } scope) return;
        var tree = functions[1];
        if (tree == 0) return;

        // Resolves code-owner slot so saved ObjM trees remain in their type scope.
        var codeOwner = CodeOwnerTypeGuids.FirstOrDefault(entry => entry.Value == unchecked((uint)type.Key));
        if (!CodeOwnerTypeGuids.ContainsKey(codeOwner.Key) || codeOwner.Value != unchecked((uint)type.Key)) return;
        var stack = TreeRunner.StartObjectTree(this, item, scope, unchecked((short)tree), 0, codeOwner.Key,
            [0, 0, 0, 0]);
        if (stack is null) return;
        SavedExecutionStacks[item.Id] = stack;
        TreeRunner.SaveFrames(item, stack);
    }

    public void RequestDisplayRefresh()
    {
        DisplayRefreshRequested = true;
        Host?.RefreshDisplay();
    }

    public XObject? ObjectTableEntry(short index) =>
        index > 0 && index <= _objectTableEntries.Count ? _objectTableEntries[index - 1] : null;

    /// <summary>Resolves an object's container and placement slot by traversing parent links while the slot
    /// hierarchy index is zero. Returns the parent container with its 0-based slot index, or -1 if
    /// unparented.</summary>
    public (XObject? Holder, int Value) PlacementSpec(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var visited = new HashSet<short> { item.Id };
        while (item[ObjectData.SlotHierNumber] == 0)
        {
            var parent = ObjectTableEntry(item[ObjectData.ParentId]);
            if (parent is null || !visited.Add(parent.Id)) break;
            item = parent;
        }
        var holder = ObjectTableEntry(item[ObjectData.ParentId]);
        int value = item[ObjectData.SlotHierNumber];
        if (value != -1)
        {
            value--;
            if (value == -1) item[ObjectData.SlotHierNumber] = -1;
        }
        return (holder, value);
    }

    /// <summary>Calculates the cumulative height of a slot by summing slot heights up the container chain.
    /// Returns zero if the slot index is outside the container's slot table.</summary>
    public float SlotHeight(XObject holder, int slot)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (slot < 0 || slot >= holder.FootprintSlots.Count) return 0f;
        var total = 0f;
        XObject? current = holder;
        var visited = new HashSet<short>();
        while (current is not null && visited.Add(current.Id))
        {
            if (slot >= 0 && slot < current.FootprintSlots.Count) total += current.FootprintSlots[slot].Height;
            var (next, nextSlot) = PlacementSpec(current);
            current = next;
            slot = (short)nextSlot;
        }
        return total;
    }

    /// <summary>Caches reach animation clips from global STR# 154 (adult) and 155 (child), keyed by age
    /// group and reach animation index.</summary>
    public Dictionary<(bool Child, int Index), AvatarAnimationClip> ReachAvatarAnimationTable { get; } = [];

    /// <summary>Traverses an object's parent hierarchy while the slot hierarchy index is zero to collect
    /// all parent objects in the removal group.</summary>
    public IReadOnlyList<XObject> RemovalGroupParents(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parents = new List<XObject>();
        var current = item;
        var visited = new HashSet<short> { item.Id };
        while (current[ObjectData.SlotHierNumber] == 0)
        {
            var parent = ObjectTableEntry(current[ObjectData.ParentId]);
            if (parent is null || !visited.Add(parent.Id)) break;
            parents.Add(parent);
            current = parent;
        }
        return parents;
    }

    /// <summary>Maps a selected family member position to its corresponding person record index, preserving
    /// FAMI and person table order. Returns -1 when no active family is selected.</summary>
    public int ActivePersonIndex(int position)
    {
        if (!ActivePersonManagerPresent || position < 0 || position >= ActivePersonRecordKeys.Count) return -1;
        var key = ActivePersonRecordKeys[position];
        return PersonRecordKeys.IndexOf(key);
    }

    /// <summary>Resolves an object's action tree ID for a function index from its function table, returning
    /// zero if out of range. Function tables are loaded from OBJf chunks or the 30 legacy OBJD function
    /// slots.</summary>
    public static short FunctionTreeId(XObject item, short function)
    {
        ArgumentNullException.ThrowIfNull(item);
        var functions = item.TypeRecord?.FunctionTable;
        if (function < 0 || functions is null || function >= functions.Count) return 0;
        return unchecked((short)functions[function]);
    }

    /// <summary>Resolves a function entry comprising an action tree ID in the low 16 bits and a check tree
    /// ID in the high 16 bits, returning zero for missing indices.</summary>
    public static (short Tree, short Check) FunctionEntry(XObject item, short function)
    {
        ArgumentNullException.ThrowIfNull(item);
        var tree = FunctionTreeId(item, function);
        var checks = item.TypeRecord?.FunctionCheckTable;
        var check = function >= 0 && checks is not null && function < checks.Count
            ? unchecked((short)checks[function])
            : (short)0;
        return (tree, check);
    }

    public static bool HasFunction(XObject item, short function) => FunctionTreeId(item, function) != 0;

    public int ActivePersonCount => ActivePersonManagerPresent ? ActivePersonRecordKeys.Count : 0;

    public XPerson? ActivePersonAt(int position)
    {
        var index = ActivePersonIndex(position);
        if (index < 0 || index >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[index];
        if (key == 0) return null;
        return WalkOrder.FirstOrDefault(item => item.TypeRecord?.Key == key) as XPerson;
    }

    public XPerson? ResolveOrCreateActivePerson(int position)
    {
        if (!ActivePersonManagerPresent || position < 0 || position >= ActivePersonRecordKeys.Count) return null;
        if (ActivePersonAt(position) is { } existing) return existing;
        var key = ActivePersonRecordKeys[position];
        if (key == 0 || ActivePersonIndex(position) < 0 || ScopedNeighborPersonRecordId(key) is null) return null;
        return WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.TypeRecord?.Key == key);
    }

    public InteractionScopeTable InteractionExecutorScope(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (InteractionScopes.TryGetValue(item.InteractionScopeKey5CA, out var scope))
            return scope;
        scope = item is XPerson person && person[PersonData.NeighborId] is > 0 and var neighborId
            ? new InteractionScopeTable(() => NeighborInventory(neighborId))
            : new InteractionScopeTable([]);
        InteractionScopes[item.InteractionScopeKey5CA] = scope;
        return scope;
    }

    public void RunGenericSimsCall11Native(XObject me, short temporary0, short temporary1, short temporary2, short temporary3)
    {
        ArgumentNullException.ThrowIfNull(me);
        if (me.TypeRecord is null) return;

        InteractionScopeTable? timeScope = null;
        XObject? companion = null;
        var wroteState = false;
        if (temporary3 == 1)
        {
            for (var index = 0; index < ActivePersonCount; index++)
            {
                if (ActivePersonAt(index) is not { } person) continue;
                SavePersonRecord(person);
                var scope = InteractionExecutorScope(person);
                StoreGenericSimsCall11State(scope, person);
                timeScope ??= scope;
                wroteState = true;
            }
            if (!wroteState && ActivePersonCount == 0)
            {
                timeScope = InteractionExecutorScope(me);
                StoreGenericSimsCall11State(timeScope, me);
                wroteState = true;
            }
        }
        else if (temporary3 == 0)
        {
            var sourceScope = InteractionExecutorScope(me);
            var selector = temporary0 == -1 ? 3 : 1;
            short cursor = -1;
            if (sourceScope.TryFindNext(selector, ref cursor, 0, out _) && sourceScope.TryGet(cursor, out var entry))
            {
                var selected = entry.Mode == 0 || !PersonRecordKeys.Contains(entry.Mode)
                    ? null
                    : WalkOrder.FirstOrDefault(item => item.TypeRecord?.Key == entry.Mode);
                if (selected is not null)
                {
                    companion = selected;
                    var selectedScope = InteractionExecutorScope(selected);
                    if (!ReferenceEquals(selectedScope, sourceScope))
                        StoreGenericSimsCall11State(selectedScope, selected);
                }
            }
            StoreGenericSimsCall11State(sourceScope, me);
            timeScope = sourceScope;
        }
        else
        {
            timeScope = InteractionExecutorScope(me);
            StoreGenericSimsCall11State(timeScope, me);
        }

        if (timeScope is not null)
        {
            int hour;
            int minute;
            if (StateClass(unchecked((short)ActiveLotNumber)) == 1 && temporary0 is >= 0x28 and < 0x32)
            {
                hour = temporary1;
                minute = temporary2;
            }
            else
            {
                hour = Clock.Hour + temporary1;
                minute = Clock.Minute + temporary2;
                if (minute > 59) { hour++; minute %= 60; }
                if (hour > 23) hour %= 24;
            }
            timeScope.AddOrIncrement(2, 7, hour);
            timeScope.AddOrIncrement(2, 8, minute);
        }

        PersistNeighborhoodState();
        if (temporary3 is 0 or 1)
        {
            var selfFamily = (me as XPerson)?[PersonData.FamilyNumber] ?? (short)0;
            var firstActive = ActivePersonCount > 0 ? ActivePersonAt(0) as XPerson : null;
            var familyNumber = temporary3 == 1
                ? firstActive?[PersonData.FamilyNumber] ?? selfFamily
                : selfFamily;
            var personGuid = temporary3 == 1 ? 0 : me.TypeRecord?.Key ?? 0;
            var companionGuid = temporary3 == 0 && companion is not null && !ReferenceEquals(companion, me)
                ? companion.TypeRecord?.Key ?? 0
                : 0;
            PendingLotSwitch = new LotSwitchRequest(temporary0, personGuid, familyNumber, companionGuid);
        }
    }

    private static void StoreGenericSimsCall11State(InteractionScopeTable scope, XObject source)
    {
        scope.RemoveEntriesWithKey(2);
        ReadOnlySpan<(int Selector, int Mode)> floatFields = [(9, 0), (6, 1), (5, 2), (15, 3), (7, 4), (8, 5), (14, 6)];
        var person = source as XPerson;
        foreach (var (selector, mode) in floatFields)
        {
            var value = person is not null && (uint)selector < (uint)person.Motives.Length
                ? person.Motives[selector]
                : source.InteractionExecutorFloats.GetValueOrDefault(selector);
            scope.AddOrIncrement(2, mode, unchecked((int)MathF.Truncate(value)));
        }
        scope.AddOrIncrement(2, 9, source.InteractionBehaviorSavedWord);
        scope.AddOrIncrement(2, 10, person is not null
            ? person.PersonData[80]
            : source.InteractionExecutorWords.GetValueOrDefault(0x62c));
        scope.AddOrIncrement(2, 11, person is not null
            ? person.PersonData[82]
            : source.InteractionExecutorWords.GetValueOrDefault(0x630));
        scope.AddOrIncrement(2, 12, person is not null
            ? person.PersonData[81]
            : source.InteractionExecutorWords.GetValueOrDefault(0x62e));
    }

    /// <summary>Checks whether a visitor has an active inventory entry by querying the person's interaction
    /// scope using the animation key cached from avatar objects.</summary>
    internal bool HasVisitorInventoryEntry(XPerson person)
    {
        var scope = InteractionExecutorScope(person);
        if (_visitorAvatarAnimationKey == 0)
            foreach (var avatar in scope.AvatarObjects)
                if (avatar.ObjectType == 13) _visitorAvatarAnimationKey = avatar.AnimationKey;
        return scope.HasVisitorInventoryEntry(_visitorAvatarAnimationKey);
    }

    public void InitializePersonTypeRecord(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.TypeRecord is not { } typeRecord) return;
        if (!PersonTypeRecordSlots.TryGetValue(typeRecord.Key, out var slot))
        {
            short candidate = 1;
            while (PersonTypeRecordSlots.ContainsValue(candidate))
            {
                if (candidate == short.MaxValue)
                    throw new InvalidOperationException("The person type-record manager exhausted its one-based short slots.");
                candidate++;
            }
            slot = candidate;
            PersonTypeRecordSlots.Add(typeRecord.Key, slot);
        }
        person.InteractionScopeKey5CA = slot;
    }

    public short? ActivePersonRecordId(int index)
    {
        if (index < 0 || index >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[index];
        if (key == 0) return null;
        foreach (var item in WalkOrder.OfType<XPerson>())
            if (item.TypeRecord?.Key == key) return item.Id;
        return null;
    }

    /// <summary>Enumerates candidate person records for chooser dialogs by filtering for person objects
    /// (OBJD type 2) and resolving their live person or neighbor data.</summary>
    public IReadOnlyList<GenericSimsCallChooserCandidate> GenericSimsCallChooserCandidates(
        XPerson requestOwner, int? viewerCenterX = null, int? viewerCenterY = null)
    {
        ArgumentNullException.ThrowIfNull(requestOwner);
        if (viewerCenterX.HasValue != viewerCenterY.HasValue)
            throw new ArgumentException("GenericSimsCall chooser viewer center requires both coordinates.");
        var candidates = new List<GenericSimsCallChooserCandidate>();
        foreach (var typeRecord in requestOwner.GenericSimsCallPersonTypeRecords)
        {
            if (typeRecord.ObjectType != XPerson.PersonType) continue;
            var rowIndex = PersonRecordKeys.IndexOf(typeRecord.TypeRecordKey);
            if (rowIndex < 0) continue;

            var livePersonId = ActivePersonRecordId(rowIndex);
            if (livePersonId is null && viewerCenterX is { } centerX && viewerCenterY is { } centerY)
                livePersonId = TryMaterializeGenericSimsCallPerson(typeRecord.TypeRecordKey, centerX, centerY);
            var neighbor = PersonRecordNeighbor(rowIndex);
            var personData = PersonRecordData(rowIndex)?.ToArray();
            candidates.Add(new GenericSimsCallChooserCandidate(
                typeRecord.RegistryIndex,
                typeRecord.Index,
                typeRecord.TypeRecordKey,
                rowIndex,
                livePersonId,
                neighbor?.Name,
                personData,
                typeRecord.CatalogName,
                typeRecord.TypeName));
        }

        return candidates;
    }

    /// <summary>Materializes an off-lot person object by instantiating it and finding an available outdoor
    /// (room zero) placement near the viewport center.</summary>
    private short? TryMaterializeGenericSimsCallPerson(int typeRecordKey, int centerX, int centerY)
    {
        if (!PersonRecordKeys.Contains(typeRecordKey) || ScopedNeighborPersonRecordId(typeRecordKey) is null)
            return null;
        var person = WalkOrder.OfType<XPerson>().FirstOrDefault(candidate => candidate.TypeRecord?.Key == typeRecordKey);
        if (person is null) return null;

        var parameters = new FindLocationParams
        {
            UseCenter = false,
            CenterX = centerX,
            CenterY = centerY,
            Height = 1,
            Direction = -1,
            ContinueFlag = 1,
            FlagB = 1,
        };
        var checkStack = TreeRunner.CreateTransientStack(this, person);
        if (!FindLocation(person, parameters, out var found, checkStack: checkStack))
        {
            KillObject(person.Id);
            return null;
        }

        PlaceObject(person, found, checkStack: checkStack);
        RefreshPersonPlacementCallbacksAfterMovement(person);
        return person.Id;
    }

    /// <summary>Completes person selection for an interactive chooser request, returning the selected
    /// person's object ID and publishing success. The request must be the head queued node for the
    /// requesting Sim.</summary>
    public bool TryChooseGenericSimsCallPerson(long requestId, int personRecordIndex)
    {
        var requests = GenericSimsCallRequests.Snapshot();
        var request = requests.FirstOrDefault(candidate => candidate.Id == requestId);
        if (request.Id != requestId || request.Status != 0 || request.Kind != GenericSimsCallRequestKind.Second)
            return false;
        if (requests.FirstOrDefault(candidate => candidate.PersonKey == request.PersonKey).Id != requestId)
            return false;

        var owner = WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.StackObjectKey == request.Temporary0);
        if (owner is null) return false;
        var candidate = GenericSimsCallChooserCandidates(owner)
            .FirstOrDefault(row => row.PersonRecordIndex == personRecordIndex && row.LivePersonId is not null);
        if (candidate?.LivePersonId is not { } livePersonId) return false;
        if (ObjectById(livePersonId) is not XPerson selected || selected.TypeRecord?.Key != candidate.TypeRecordKey)
            return false;

        if (!GenericSimsCallRequests.TryComplete(
                requestId, 0, selected.StackObjectKey, GenericSimsCallRequestStatus.Succeeded))
            return false;
        GlobalPerson.ByteE0 = 1;
        return true;
    }

    /// <summary>Resolves a person record key to an existing on-lot Sim object ID, or instantiates the
    /// person if not currently present on the lot.</summary>
    public short? ScopedNeighborPersonRecordId(int personRecordKey)
    {
        if (personRecordKey == 0 || !PersonRecordKeys.Contains(personRecordKey)) return null;

        foreach (var item in WalkOrder)
            if (item.TypeRecord?.Key == personRecordKey)
                return item.ClockManagerIndex;

        if (ObjectGroupFactory?.Invoke(personRecordKey) is { } group)
            return group.Count == 0 ? null : group[0].ClockManagerIndex;

        if (ObjectFactory?.Invoke(personRecordKey) is not { } created) return null;
        Add(created);
        if (created is XPerson person) InitializeCreatedPerson(person);
        InitializeCreatedObject(created);
        if (created is XPerson postInitPerson) InitializeCreatedPerson(postInitPerson);
        return created.ClockManagerIndex;
    }

    public int PersonRecordIndex(XObject item)
    {
        var key = item.TypeRecord?.Key
            ?? throw new InvalidOperationException($"Object {item.Id} has no type record for person-record lookup.");
        return PersonRecordKeys.IndexOf(key);
    }

    /// <summary>Returns live on-lot person data if the Sim is currently instantiated; otherwise falls back
    /// to the neighbor record's persistent person data.</summary>
    public short[]? PersonRecordData(int personRecordIndex)
    {
        if (personRecordIndex < 0 || personRecordIndex >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[personRecordIndex];
        short[]? personData = key == 0 ? null : WalkOrder.OfType<XPerson>()
            .FirstOrDefault(item => item.TypeRecord?.Key == key) is XPerson person ? person.PersonData : null;
        if (personData is null) personData = PersonRecordNeighbor(personRecordIndex)?.PersonData;
        return personData;
    }

    /// <summary>Applies tuning constants from FCNS chunk 2 to autonomy and mutual-friend qualification
    /// gates.</summary>
    public void ApplySimulatorConstants(SimulatorConstants constants)
    {
        SimulatorTuning = FindBestInteractionTuning.FromSimulatorConstants(constants);
        RoomScoreTuning = SimsEngine.World.RoomScoreTuning.FromSimulatorConstants(constants);
        HouseYardScoreMultiplier = constants.Get("yard score multiplier", 0.33f);
        HouseUpkeepPenaltyPerDirtyObject = (int)Math.Round(constants.Get("upkeep penalty per dirty object", 6f), MidpointRounding.ToEven);
        HouseUpkeepPenaltyPerBrokenObject = (int)Math.Round(constants.Get("upkeep penalty per broken object", 15f), MidpointRounding.ToEven);
        HouseLayoutHistoryFillValue = (int)Math.Round(constants.Get("fill value for layout history", 200f), MidpointRounding.ToEven);
        FriendshipThreshold = SimulatorTuning.FriendshipThreshold;
        ArchitectureValueMultiplier = constants.Get("architecture value multiplier", 0.7f);
    }

    /// <summary>Initializes the autonomy candidate flag for an object after its multi-tile root and
    /// interaction table (TTAB) are resolved.</summary>
    public bool InitializeAutonomyCandidate(XObject item, XObject root, InteractionTable? interactions)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item, ObjectById(item.Id)))
            throw new InvalidOperationException($"Object {item.Id} is not registered in this simulation world.");
        var eligible = AutonomyCandidateEligibility.IsEligible(item, root, interactions);
        ClockManager.SetFlag(item.ClockManagerIndex, 3, eligible);
        return eligible;
    }

    private NeighborState? PersonRecordNeighbor(int personRecordIndex)
    {
        if (personRecordIndex < 0 || personRecordIndex >= PersonRecordKeys.Count) return null;
        while (PersonRecordNeighborIds.Count <= personRecordIndex) PersonRecordNeighborIds.Add(null);
        if (PersonRecordNeighborIds[personRecordIndex] is { } neighborId)
            return Neighbors.GetValueOrDefault(neighborId);

        var key = PersonRecordKeys[personRecordIndex];
        foreach (var neighbor in NeighborWalkOrder)
        {
            if (neighbor?.TypeRecordKey != key) continue;
            PersonRecordNeighborIds[personRecordIndex] = neighbor.Id;
            return neighbor;
        }
        PersonRecordNeighborIds[personRecordIndex] = 0;
        return null;
    }

    public int FameFriendStarPower(int personRecordIndex)
    {
        var subject = PersonRecordNeighbor(personRecordIndex);
        if (subject is null) return 0;

        var total = 0;
        foreach (var candidate in NeighborWalkOrder)
        {
            if (candidate is null || ReferenceEquals(candidate, subject)) continue;
            if (candidate.FriendEligibilityObjectFlagsB4 is not { } flags || (flags & 0xC) != 4) continue;
            var candidateFamily = candidate.PersonData[61];
            var subjectFamily = subject.PersonData[61];
            if (candidateFamily == 0 || candidateFamily == subjectFamily) continue;

            var candidateToSubject = candidate.Relationships;
            var subjectToCandidate = subject.Relationships;
            if (candidateToSubject.Count(subject.Id) == 0 || subjectToCandidate.Count(candidate.Id) == 0) continue;
            if (candidateToSubject.Get(subject.Id, 0) < FriendshipThreshold ||
                subjectToCandidate.Get(candidate.Id, 0) < FriendshipThreshold) continue;
            total = unchecked(total + candidate.PersonData[81]);
        }
        return total;
    }

    public bool MeetsFameFriendPowerGate(int personRecordIndex, int requiredFriendPower) =>
        requiredFriendPower <= FameFriendStarPower(personRecordIndex) * 0.5f;

    /// <summary>Evaluates whether a Sim qualifies for a Fame promotion by testing Fame score and skill
    /// gates, returning the per-star friend-power requirement.</summary>
    public FamePromotionCandidate? EvaluateFamePromotion(int personRecordIndex, GenericSimsCallRecordTarget target)
    {
        var data = PersonRecordData(personRecordIndex);
        if (data is null) return null;
        var stars = data[(int)PersonData.FameStarPower09];
        if (stars < 0 || stars >= 10 || stars >= target.FameStarThresholds.Count || stars >= target.FameFriendThresholds.Count)
            return null;
        if (data[(int)PersonData.FameScore] < target.FameStarThresholds[stars]) return null;

        ReadOnlySpan<int> skillFields = [10, 12, 11, 17, 18, 15];
        if (target.FameSkillThresholds.Count < skillFields.Length) return null;
        for (var skill = 0; skill < skillFields.Length; skill++)
        {
            var rows = target.FameSkillThresholds[skill];
            if (stars >= rows.Count) return null;
            var threshold = rows[stars];
            var raw = data[skillFields[skill]];
            var required = unchecked(threshold * 100) <= raw ? 0 : threshold;
            var level = Math.Clamp(raw / 100, 0, 10);
            if (level < required) return null;
        }
        return new FamePromotionCandidate(data, stars, target.FameFriendThresholds[stars]);
    }

    public static void ApplyFamePromotion(FamePromotionCandidate candidate)
    {
        var promoted = (short)(candidate.CurrentStarPower + 1);
        candidate.PersonData[(int)PersonData.FameStarPower09] = promoted;
        if (candidate.PersonData[(int)PersonData.FameStarHighWatermark] < promoted)
            candidate.PersonData[(int)PersonData.FameStarHighWatermark] = promoted;
    }

    public bool TryDecrementFameStarPower(int personRecordIndex, IReadOnlyList<int> fameStarThresholds)
    {
        var personData = PersonRecordData(personRecordIndex);
        if (personData is null) return false;

        var starPower = personData[(int)PersonData.FameStarPower09];
        if (starPower <= 0 || starPower > fameStarThresholds.Count) return false;
        if (personData[(int)PersonData.FameScore] >= fameStarThresholds[starPower - 1]) return false;
        personData[(int)PersonData.FameStarPower09] = (short)(starPower - 1);
        return true;
    }

    /// <summary>Evaluates person selection for a record index by testing state-class and selection
    /// overrides. A selection with a null person clears the active selection.</summary>
    public PersonRecordSelection? EvaluatePersonRecordSelection(int index)
    {
        if (!PersonRecordManagerPresent) return null;
        var key = index >= 0 && index < PersonRecordKeys.Count ? PersonRecordKeys[index] : 0;
        var person = key == 0 ? null : ObjectByTypeRecordKey(key);
        if (person is null) return new PersonRecordSelection(null);

        var state = person.StateValue != 0 ? person.StateValue : NeighborInteractionGlobalStateValue;
        var stateClass = StateClass(state);
        if (stateClass == 1)
            return new PersonRecordSelection(PersonSelectionOverride || person.SelectionWord5CC == 0 ? person : null);

        if (stateClass != 2 && !(state > 0x14 && state < 0x1f) && !(state > 0x27 && state < 0x32))
            return null;

        if (PersonSelectionOverride) return new PersonRecordSelection(person);
        if (state <= 0x14 || state >= 0x1f)
        {
            if (StoredSlotPersonId is { } stored && person.SelectionWord606 == stored)
                return new PersonRecordSelection(person);
        }

        return new PersonRecordSelection(person.SelectionWord5CC != 2 ? person : null);
    }

    /// <summary>Applies person selection changes by running the deselection sequence on the previously
    /// active object before selecting the new object across its multi-tile hierarchy.</summary>
    public void ApplyPersonRecordSelection(XObject? selected, ISimulationHost? host = null)
    {
        var previous = WalkOrder.FirstOrDefault(item => (item.Flags & 2) != 0);
        if (ReferenceEquals(previous, selected)) return;

        if (previous is not null)
        {
            SetPersonSelectionFlags(previous, (byte)(previous.Flags & 0x1d), host);
            host?.RefreshDisplay();
            host?.SelectedPersonChanged(previous);
        }

        if (selected is null)
        {
            DispatchNotice(0xe4, 0);
            return;
        }

        SetPersonSelectionFlags(selected, (byte)((selected.Flags & 0x1f) | 2), host);
        host?.RefreshDisplay();
        host?.SelectedPersonChanged(selected);
        DispatchNotice(0xe4, selected.RequestKey);
    }

    private void SetPersonSelectionFlags(XObject person, byte lowFiveBits, ISimulationHost? host)
    {
        lowFiveBits &= 0x1f;
        if (person.Definition[(int)ObjdField.MasterId] != 0)
        {
            if (HasFunction(person, 8))
            {
                if ((person.Flags & 0x1f) != lowFiveBits)
                {
                    person.Flags = person.Flags & ~0x1fu | lowFiveBits;
                    host?.RefreshDisplay();
                }
                return;
            }

            SetMultiTilePersonSelectionFlags(person, lowFiveBits, host);
            return;
        }

        if ((person.Flags & 0x1f) == lowFiveBits) return;
        person.Flags = person.Flags & ~0x1fu | lowFiveBits;
        host?.RefreshDisplay();
    }

    private void SetMultiTilePersonSelectionFlags(XObject person, byte lowFiveBits, ISimulationHost? host)
    {
        var current = person.MultiTileMasterId == 0
            ? person
            : ObjectById(person.MultiTileMasterId)
                ?? throw new InvalidOperationException($"Multi-tile object {person.Id} names missing master {person.MultiTileMasterId}.");
        var visited = new HashSet<short>();
        while (true)
        {
            if (!visited.Add(current.Id))
                throw new InvalidOperationException($"Multi-tile selection chain loops at object {current.Id}.");

            if ((current.Flags & 0x1f) != lowFiveBits)
            {
                current.Flags = current.Flags & ~0x1fu | lowFiveBits;
                host?.RefreshDisplay();
            }

            if (current.MultiTileNextId == 0) return;
            current = ObjectById(current.MultiTileNextId)
                ?? throw new InvalidOperationException($"Multi-tile object {current.Id} names missing next object {current.MultiTileNextId}.");
        }
    }

    /// <summary>Resolves the classification for an object state, mapping state zero to class 1 by default.</summary>
    public int StateClass(short state)
    {
        if (state == 0) return 1;
        foreach (var entry in StateClasses)
            if (entry.State == state) return entry.Class;
        return 0;
    }

    /// <summary>Performs lot upkeep by raising the six attributes of the active Magic Master Spells
    /// controller to match the caller's or active family's minimum resource requirements.</summary>
    public void RunLotUpkeep(XObject runningObject)
    {
        ArgumentNullException.ThrowIfNull(runningObject);
        if (!LotUpkeepManagerPresent) return;

        IReadOnlyList<short> minResources;
        if (runningObject.LotUpkeepGateByte14C)
        {
            if (runningObject.TableWord10C == 0 || runningObject.TableWord10C != GlobalPerson.LotUpkeepMatchWord22 ||
                StateClass(runningObject.StateValue) != 1 || !runningObject.LotUpkeepPointer128Present)
                return;
            minResources = runningObject.LotUpkeepMinimumResources;
        }
        else if (TryLookupActiveFamilyUpkeep is { } lookup &&
                 lookup(out var familyNumber, out var hasMembers, out var familyResources) &&
                 familyNumber != 0 &&
                 familyNumber == GlobalPerson.LotUpkeepMatchWord22 &&
                 StateClass(unchecked((short)ActiveLotNumber)) == 1 &&
                 hasMembers &&
                 familyResources.Count >= 6)
        {
            minResources = familyResources;
        }
        else
        {
            return;
        }

        var resources = ObjectByTypeRecordKey(unchecked((int)0xB6C90029))?.Attributes;
        if (resources is null) return;
        if (resources.Length < 6)
            throw new InvalidOperationException("Sims.exe FUN_00410b89 requires six Magic Master Spells resource attributes.");
        for (var i = 0; i < 6; i++)
            if (resources[i] < minResources[i])
                resources[i] = minResources[i];
    }

    /// <summary>Updates a Sim's cached room location, toggles room lighting transitions, and recalculates
    /// the Room motive.</summary>
    public void UpdatePersonRoomMotive(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var currentRoom = unchecked((ushort)person.RoomWord84);
        var previousRoom = person.LastObservedRoomIdAFC;
        person.LastObservedRoomIdAFC = currentRoom;
        if (previousRoom != currentRoom && person[PersonData.Gender] < 2)
        {
            if (PersonRoomMotiveEntries.ContainsKey(previousRoom) &&
                !Objects.Values.OfType<XPerson>().Any(other => !ReferenceEquals(other, person) &&
                    (other.FootprintFlags614 & 1) == 0 && (other.FootprintFlags634 & 1) == 0 &&
                    other.LastObservedRoomIdAFC == previousRoom))
                SetRoomLightsEnabled(previousRoom, enabled: false);
            if (PersonRoomMotiveEntries.ContainsKey(currentRoom))
                SetRoomLightsEnabled(currentRoom, enabled: true);
        }
        if (previousRoom != currentRoom &&
            ((person.Flags & 2) != 0 || this[SimulationGlobal.SelectedPersonID] == person.Id))
            DispatchNotice(0xf7, 0);
        if (currentRoom == 0xfffb) return; // Room ID 0xfffb (-5) represents the off-lot sentinel value.

        var motive = 0f;
        if (PersonRoomMotiveEntries.TryGetValue(currentRoom, out var room))
            motive = room.IsOutside ? OutsidePersonRoomMotiveValue : room.InteriorValue;
        var stateValue = person.StateValue != 0 ? person.StateValue : NeighborInteractionGlobalStateValue;
        if (currentRoom == 0 && StateClass(stateValue) != 1 && motive < 0f)
            motive = 0f;
        person[Motive.Room] = motive;
    }

    /// <summary>Refreshes room location, lighting, and motive immediately after movement so room state does
    /// not remain stale while routing across ticks.</summary>
    internal void RefreshPersonRoomStateAfterMovement(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.LastObservedRoomIdAFC == unchecked((ushort)person.RoomWord84)) return;
        UpdatePersonRoomMotive(person);
    }

    /// <summary>Executes placement callbacks after movement to update room lighting, refresh swimming pool
    /// status, and invalidate root grid coordinates.</summary>
    internal void RefreshPersonPlacementCallbacksAfterMovement(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        RefreshPersonRoomStateAfterMovement(person);
        InvalidateAnimatorRootGrid(person);
        person.AnimatorPoolTile = IsPoolTile(person.Position);
    }

    /// <summary>Invalidates animator root coordinates, allowing scene geometry to fall back to the object's
    /// current position and direction until routing establishes new roots.</summary>
    private static void InvalidateAnimatorRootGrid(XPerson person)
    {
        person.AnimatorRootX = null;
        person.AnimatorRootY = null;
        person.AnimatorRootDirection = null;
    }

    private void SetRoomLightsEnabled(ushort roomId, bool enabled)
    {
        if (!PersonRoomMotiveEntries.TryGetValue(roomId, out var room) || room.IsOutside ||
            room.LightsEnabledFlag73 == (enabled ? 1 : 0) || room.LightOverrideFlag75 != 0)
            return;

        room.LightsEnabledFlag73 = enabled ? (byte)1 : (byte)0;
        PersonRoomMotiveEntries[roomId] = room;
        if (room.LightingObjectCount140 != 0 || room.LightLevel6c >= SimsEngine.World.RoomScoreTuning.DaylightCeiling)
            return;

        DispatchNotice(0xf1, roomId);
        var light = enabled ? SimsEngine.World.RoomScoreTuning.DaylightCeiling : room.LightLevel6c;
        foreach (var (level, roomMap) in LotRoomMaps)
        {
            if (!LotRoomLightMaps.TryGetValue(level, out var grid) || !roomMap.RoomIds.Contains(roomId)) continue;
            WriteRoomLightMap(roomMap, roomId, light);
            break;
        }
    }

    /// <summary>Determines whether Sleep writes to the wait cell, returning false for person objects (OBJD
    /// type 2) and requiring inactive sprite slots.</summary>
    public bool SleepWritesWaitCell(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Definition[(int)ObjdField.Type] == 2) return false;
        if (item.RuntimeSlots.Count > 0 && item.RuntimeSlots[0].ReadWord(0x10) != 0) return false;
        return item.SleepGateWord7C == 0;
    }

    /// <summary>Refreshes visitor type classifications for all instantiated persons by comparing their
    /// GUIDs against active family members.</summary>
    public void RefreshNeighborInteractionManager()
    {
        var managerAllowsSelection = StateClass(NeighborInteractionGlobalStateValue) == 1;
        foreach (var item in WalkOrder.OfType<XPerson>())
        {
            var selection = item.SelectionWord5CC == 0 ? (short)1 : item.SelectionWord5CC;
            var typeRecord = item.TypeRecord
                ?? throw new InvalidOperationException($"Sims.exe FUN_0045741b would dereference a missing type record on object {item.Id}.");
            if (NeighborInteractionManagerTypeKeys.Contains(typeRecord.Key)) selection = 0;
            if (!managerAllowsSelection && selection == 0) selection = 1;
            item.SelectionWord5CC = selection;
        }
    }

    /// <summary>Resolves an object by its simulation ID. Frame stack-object keys must use
    /// <see cref="ObjectByStackObject"/> instead.</summary>
    public XObject? ObjectById(short id) => Objects.GetValueOrDefault(id);

    /// <summary>Appends an object to the lot list, retaining duplicates and notifying listeners only when
    /// the list transitions from empty to non-empty.</summary>
    public void MaintainLotList(XObject owner)
    {
        var wasEmpty = WorldLotListEntries.Count == 0 && owner.LotListEntries.Count == 0;
        owner.LotListEntries.Add(owner);
        WorldLotListEntries.Add(owner);
        if (wasEmpty) DispatchNotice(0x108, 0);
    }

    /// <summary>Removes the first matching object from the lot list, notifying listeners only when the list
    /// transitions between empty and non-empty.</summary>
    public void RemoveFromLotList(XObject owner)
    {
        var wasEmpty = WorldLotListEntries.Count == 0 && owner.LotListEntries.Count == 0;
        var index = owner.LotListEntries.FindIndex(item => ReferenceEquals(item, owner));
        if (index >= 0) owner.LotListEntries.RemoveAt(index);
        var worldIndex = WorldLotListEntries.FindIndex(item => ReferenceEquals(item, owner));
        if (worldIndex >= 0) WorldLotListEntries.RemoveAt(worldIndex);
        var isEmpty = WorldLotListEntries.Count == 0 && owner.LotListEntries.Count == 0;
        if (wasEmpty != isEmpty) DispatchNotice(0x108, 0);
    }

    /// <summary>Finds the first interaction object matching the specified ID, preserving registration order
    /// when keys are duplicated.</summary>
    public XObject? FindInteractionObjectByTableWord(short id)
    {
        foreach (var item in InteractionLookupOrder)
            if (item.TableWord10C == id) return item;
        return null;
    }

    public XObject? NeighborTableEntry(short index) =>
        index > 0 && index <= NeighborTableEntries.Count ? NeighborTableEntries[index - 1] : null;

    public void SetNeighborTableEntry(short index, XObject? item)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        while (NeighborTableEntries.Count < index) NeighborTableEntries.Add(null);
        NeighborTableEntries[index - 1] = item;
    }

    public int StackObjectCreateKey(short index) => NeighborManagerEntry(index) is { FriendEligibilityObjectFlagsB4: not null } neighbor
        ? neighbor.TypeRecordKey : 0;

    /// <summary>Calculates the charge amount for GenericSimsCall 0x05, using the service manager budget and
    /// scaled value when matching, or summing standard cost words.</summary>
    public int GenericSimsCall05Amount(XObject item)
    {
        var managerWord = GenericSimsCall05ManagerWord164;
        if (managerWord is null || managerWord == 0 || item.Position.Height != managerWord.Value)
            return unchecked(item.ObjectValueWord118 + item.ObjectValueWord11C);

        var mode = ObjectValueGlobalWord50 ?? throw MissingObjectValueState(nameof(ObjectValueGlobalWord50));
        var funds = ObjectValueGlobalWord90 ?? throw MissingObjectValueState(nameof(ObjectValueGlobalWord90));
        var word9C = ObjectValueGlobalWord9C ?? throw MissingObjectValueState(nameof(ObjectValueGlobalWord9C));
        var wordA0 = ObjectValueGlobalWordA0 ?? throw MissingObjectValueState(nameof(ObjectValueGlobalWordA0));
        var wordA4 = ObjectValueGlobalWordA4 ?? throw MissingObjectValueState(nameof(ObjectValueGlobalWordA4));
        var multiplier = ArchitectureValueMultiplier ?? throw MissingObjectValueState(nameof(ArchitectureValueMultiplier));
        var scaled = MultiplyAndTruncateX87(wordA4, multiplier);
        var availableFunds = mode == 1 ? 999_999 : funds;
        return unchecked(availableFunds + wordA0 + word9C + scaled);

        static InvalidOperationException MissingObjectValueState(string name) =>
            new($"GenericSimsCall 0x05 special value path needs {name} from the game state.");
    }

    /// <summary>Computes family net worth for GenericSimsCall 0x05 natively from FAMI and live lot
    /// valuation state when no test interaction object entry is registered.</summary>
    public bool TryGetGenericSimsCall05FamilyAmount(short familyNumber, out int amount)
    {
        amount = 0;
        if (TryLookupFamilyValuation is not { } lookup ||
            !lookup(familyNumber, out var houseNumber, out var budget, out var value))
            return false;

        if (ActiveLotNumber != 0 && houseNumber == ActiveLotNumber)
        {
            var availableFunds = this[SimulationGlobal.SimlessBuildMode] == 1
                ? 999_999
                : (FamilyBudget?.ReadFamilyBudget() ?? budget);
            var simi = SavedSimulationState;
            var lotValue = simi?.Field0x9C ?? 0;
            var (furnishings, arch) = LiveLotValuationProvider?.Invoke()
                ?? (simi?.Field0xA0 ?? 0, simi?.Field0xA4 ?? 0);
            var scaled = MultiplyAndTruncateX87(arch, ArchitectureValueMultiplier ?? 0.7f);
            amount = unchecked(availableFunds + lotValue + furnishings + scaled);
        }
        else
        {
            amount = unchecked(budget + value);
        }
        return true;
    }

    private static int MultiplyAndTruncateX87(int value, float factor)
    {
        var bits = BitConverter.SingleToInt32Bits(factor);
        var exponentBits = (bits >> 23) & 0xff;
        if (exponentBits == 0xff) return 0;
        var significand = bits & 0x7fffff;
        var exponent = exponentBits == 0 ? -149 : exponentBits - 127 - 23;
        if (exponentBits != 0) significand |= 1 << 23;

        var product = new BigInteger(value) * significand;
        if ((bits & int.MinValue) != 0) product = -product;
        var negative = product.Sign < 0;
        var magnitude = BigInteger.Abs(product);
        magnitude = exponent >= 0 ? magnitude << exponent : magnitude >> -exponent;
        var truncated = negative ? -magnitude : magnitude;
        if (truncated < long.MinValue || truncated > long.MaxValue) return 0;
        return unchecked((int)(long)truncated);
    }

    /// <summary>Resolves a stack-object reference to a live simulation object through the caller's object
    /// table, treating missing entries as lookup failures.</summary>
    public XObject? ObjectByStackObject(XObject me, short key)
    {
        if (me.ObjectTableEntries.Count > 0) return me.ObjectTableEntry(key);
        return me.HasRequestKey ? null : ObjectById(key);
    }

    /// <summary>Saves a person's persistent data into their neighbor record using layout version 10 format.</summary>
    public void SavePersonRecord(XPerson person)
    {
        var neighborId = person[PersonData.NeighborId];
        if (neighborId < 1 || !Neighbors.TryGetValue(neighborId, out var neighbor)) return;
        var changed = neighbor.PersonRecordVersion != 10;
        neighbor.PersonRecordVersion = 10;
        // NBRS chunks store Sim names separately from persistent person data.
        if (SavedNeighbors?.Entries.FirstOrDefault(saved => saved.NeighborId == neighborId) is { } savedNeighbor &&
            !string.Equals(savedNeighbor.Name, neighbor.Name, StringComparison.Ordinal))
            changed = true;
        if (person.Name is { } personName && !string.Equals(neighbor.Name, personName, StringComparison.Ordinal))
        {
            neighbor.Name = personName;
            changed = true;
        }
        foreach (var field in PersonRecordFieldsVersion10)
        {
            if (field >= neighbor.PersonData.Length || field >= person.PersonData.Length) continue;
            if (neighbor.PersonData[field] == person.PersonData[field]) continue;
            neighbor.PersonData[field] = person.PersonData[field];
            changed = true;
        }
        PersistentPersonDataDirty |= changed;
        if (changed) DirtyPersistentPersonNeighborIds.Add(neighborId);
    }

    /// <summary>Flushes persistent data from all instantiated persons to their neighbor records prior to
    /// saving the neighborhood.</summary>
    public void SaveAllPersistentPersonData()
    {
        foreach (var person in WalkOrder.OfType<XPerson>()) SavePersonRecord(person);
    }

    public XObject? ObjectByTypeRecordKey(int key)
    {
        foreach (var item in WalkOrder)
        {
            var record = item.TypeRecord
                ?? throw new InvalidOperationException($"Sims.exe FUN_0045741B would dereference a missing type record on object {item.Id}.");
            if (record.Key == key) return item;
        }

        return null;
    }

    /// <summary>Processes pending GenericSimsCall requests and advances request expiration timers.</summary>
    public void UpdateGenericSimsCallRequests()
    {
        var host = Host;
        foreach (var request in GenericSimsCallRequests.Snapshot())
        {
            if (request.Status != 0) continue;

            if (host is not null && !host.GenericSimsCallRequestManagerAvailable()) continue;

            var target = WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.StackObjectKey == request.Temporary0);
            if (target is null)
            {
                GenericSimsCallRequests.TryComplete(
                    request.Id,
                    request.Temporary0,
                    request.Temporary1,
                    GenericSimsCallRequestStatus.Failed);
                continue;
            }

            if (host is null && !AutoProcessGenericSimsCallRequests) continue;

            GenericSimsCallRequestCompletion result;
            if (host is not null)
            {
                result = host.ProcessGenericSimsCallRequest(request, target);
            }
            else
            {
                if (request.Kind == GenericSimsCallRequestKind.Second)
                {
                    result = target.TypeRecord is not null
                        ? new GenericSimsCallRequestCompletion(0, target.StackObjectKey, Succeeded: true, TargetTypeRecordKey: target.TypeRecord.Key)
                        : new GenericSimsCallRequestCompletion(request.Temporary0, 0, Succeeded: false);
                }
                else
                {
                    result = new GenericSimsCallRequestCompletion(request.Temporary0, request.Temporary1, Succeeded: true);
                }
            }

            if (request.Kind == GenericSimsCallRequestKind.Second)
            {
                if (target is not null && !result.Succeeded)
                {
                    result = result with { Temporary0 = request.Temporary0, Temporary1 = 0 };
                }
                else if (result.Succeeded)
                {
                    var key = result.TargetTypeRecordKey
                        ?? throw new InvalidOperationException("GenericSimsCall request kind 1 succeeded without a type-record key.");
                    var resultPerson = ObjectByTypeRecordKey(key)
                        ?? throw new InvalidOperationException($"GenericSimsCall request kind 1 could not resolve type-record key {key}.");
                    result = result with { Temporary0 = 0, Temporary1 = resultPerson.StackObjectKey };
                }
            }

            if (result.Succeeded) GlobalPerson.ByteE0 = 1;
            GenericSimsCallRequests.TryComplete(
                request.Id,
                result.Temporary0,
                result.Temporary1,
                result.Succeeded ? GenericSimsCallRequestStatus.Succeeded : GenericSimsCallRequestStatus.Failed);
        }

        GenericSimsCallRequests.AdvanceTick();
    }

    /// <summary>Executes one behavior stack tick for each live object in lot walk order. Objects removed
    /// during the pass are skipped, and newly added objects begin on the next tick.</summary>
    public int TickSavedExecutionStacks(int? tickCounter = null)
    {
        var currentTick = tickCounter ?? Clock.Ticks;
        var scheduled = 0;
        foreach (var owner in WalkOrder.ToArray())
        {
            if (!Objects.TryGetValue(owner.Id, out var current) || !ReferenceEquals(current, owner) ||
                !SavedExecutionStacks.ContainsKey(owner.Id))
                continue;

            bool completed;
            try
            {
                completed = TreeRunner.RunRestoredObjectStack(this, owner);
            }
            catch (Exception error) when (error is SimulationException or InvalidOperationException or TreeRunException)
            {
                throw new InvalidOperationException($"Saved object {owner.Id}: {error.Message} ({CurrentExecutionLocation ?? "no current instruction"})", error);
            }
            if (owner is XPerson scalingPerson) UpdateAnimatorBodyScale(scalingPerson, currentTick);
            if (completed) FinishObjectSimulation(owner);
            if (completed && owner is XPerson animatingPerson)
            {
                TickAvatarAnimations(animatingPerson);
            }
            if (completed && owner is XPerson person) ApplyPersonPostUpdate(person, currentTick);
            scheduled++;
        }
        CommitPendingObjectRemovals();
        return scheduled;
    }

    /// <summary>Updates avatar body scale every ten simulation ticks if scaling is enabled and the scale
    /// factor is strictly between 0.4 and 10.0.</summary>
    internal static void UpdateAnimatorBodyScale(XPerson person, int simulationTick)
    {
        if (person.PersonTickGateWord8E != 0 || person.AnimatorScaleUpdateSuppressed || simulationTick % 10 != 0) return;
        var scale = person[PersonData.ScalingSim] / 100f;
        if (scale > 0.4f && scale < 10f) person.AnimatorBodyScale = scale;
    }

    /// <summary>Completes object simulation after an action tree finishes successfully or for objects with
    /// no active tree; skipped when a tree yields.</summary>
    private void FinishObjectSimulation(XObject item)
    {
        if (item[ObjectData.LockoutCount] != 0)
            item[ObjectData.LockoutCount]--;
        foreach (var slot in item.RuntimeSlots)
            if (slot.Tick()) DisplayRefreshRequested = true;
    }

    private void ApplyPersonPostUpdate(XPerson person, int tickCounter)
    {
        if (unchecked(person.MotiveDecayLastTickCounter + 60) <= tickCounter)
        {
            person.MotiveDecayLastTickCounter = tickCounter;
            if (person[PersonData.MotivesStatic] == 0 && person.PersonTickGateWord8E == 0)
                PersonMotiveDecayTuning?.Apply(person, Clock.Hour);
        }
        UpdatePersonRoomMotive(person);
        person[PersonData.TickCounter] = unchecked((short)(person[PersonData.TickCounter] + 1));
        person.ApplyMotiveChanges();
        ProcessPersonMoneyOverhead(person);
    }

    /// <summary>Advances one simulation tick, running active object behavior stacks followed by request
    /// processing and aging. Simulation time advances only when <paramref name="advanceGameTime"/> is set.
    /// </summary>
    public int AdvanceSimulationTick(bool advanceGameTime = false)
    {
        AdvanceClock(advanceGameTime, simulationTickCompleted: advanceGameTime);
        var scheduled = TickSavedExecutionStacks(Clock.Ticks);
        UpdateGenericSimsCallRequests();
        RefreshPendingRoomGroups();
        return scheduled;
    }

    /// <summary>Executes the clock manager pass and advances simulation time only after a complete pass
    /// with no global hold, followed by request processing.</summary>
    public int AdvanceSimulationTick(
        SimulationClockControl control,
        short clockState28,
        int clockTickCounter,
        Func<XObject, int, bool> tickClockObject)
    {
        // Swaps the lot manager RNG state in for the duration of the object tick pass.
        var ambientRandomState = RandomState;
        RandomState = ObjectManagerRandomState;
        SimulationClockManagerResult result;
        try
        {
            result = ClockManager.Evaluate(control, clockState28, clockTickCounter, tickClockObject);
        }
        finally
        {
            ObjectManagerRandomState = RandomState;
            RandomState = ambientRandomState;
        }
        // Commit pending removals after the pass so current interactions can finish.
        CommitPendingObjectRemovals();
        var advanceGameTime = result == SimulationClockManagerResult.PassComplete && !control.GlobalTimeHold;
        AdvanceClock(advanceGameTime, simulationTickCompleted: result == SimulationClockManagerResult.PassComplete);
        UpdateGenericSimsCallRequests();
        RefreshPendingRoomGroups();
        return ClockManager.ObjectCallbacksLastEvaluation;
    }

    internal void AdvanceClock(bool advanceGameTime = true, bool simulationTickCompleted = true)
    {
        var result = Clock.Tick(advanceGameTime, simulationTickCompleted);
        if (!simulationTickCompleted) return;

        if (advanceGameTime)
        {
            this[SimulationGlobal.Hour] = checked((short)Clock.Hour);
            this[SimulationGlobal.Minute] = checked((short)Clock.Minute);
            this[SimulationGlobal.Second] = checked((short)Clock.Second);
            this[SimulationGlobal.TimeOfDay] = checked((short)Clock.TimeOfDay);
        }

        if (!result.DayChanged) return;

        // Every simulation calendar month contains exactly 30 days.
        var date = CurrentDate ?? SimulationDate.Initial;
        var day = date.Day + 1;
        var month = date.Month;
        var year = date.Year;
        if (day > 30)
        {
            day = 1;
            month++;
            if (month > 12)
            {
                month = 1;
                year++;
            }
        }
        var nextDate = new SimulationDate(checked((short)year), checked((short)month), checked((short)day));
        CurrentDate = nextDate;
        this[SimulationGlobal.DaysRunning] = unchecked((short)(this[SimulationGlobal.DaysRunning] + 1));
        if (nextDate.Month != date.Month) this[SimulationGlobal.MaximumDayNumber] = 30;
        this[SimulationGlobal.DayOfMonth] = nextDate.Day;
        this[SimulationGlobal.Month] = nextDate.Month;
        this[SimulationGlobal.Year] = nextDate.Year;
        // Midnight rollover updates each object's age and applies daily depreciation.
        foreach (var item in (WalkOrder.Count > 0 ? (IEnumerable<XObject>)WalkOrder : Objects.Values).ToArray())
        {
            RecomputeObjectAge(item, nextDate);
            DepreciateObjectForDay(item);
        }
        FamilyBudget?.AdvanceBudgetDay();
    }

    private void RefreshPendingRoomGroups()
    {
        if ((RoomManagerRefreshFlags & 1) != 0) RefreshDirtyRoomRecords(1);
        if ((RoomManagerRefreshFlags & 2) != 0) RefreshDirtyRoomRecords(2);
    }

    /// <summary>Removes queued objects from the lot and object tables and tears down their execution state.</summary>
    private void CommitPendingObjectRemovals()
    {
        if (PendingRemovals.Count == 0) return;
        var removals = PendingRemovals.Distinct().ToArray();
        PendingRemovals.Clear();
        foreach (var id in removals)
        {
            if (Neighborhood.TrackingEnabled &&
                ObjectById(id) is XPerson person &&
                TryInterceptConnectedVisitorRemoval?.Invoke(person) == true)
            {
                continue;
            }
            KillObject(id);
        }
    }

    /// <summary>Executes the clock manager pass across objects. Yielded stacks suspend the pass, while
    /// completed stacks finalize object slot and usage counts.</summary>
    public int AdvanceSimulationTick(SimulationClockControl control, short clockState28, int clockTickCounter)
    {
        var callbacks = AdvanceSimulationTick(control, clockState28, clockTickCounter, TickClockObject);
        ContinueDirectPositionRoutes();
        return callbacks;

        bool TickClockObject(XObject item, int tickCounter)
        {
            try
            {
                var outfitTransition = item is XPerson { NativeOutfitTransitionActive: true };
                var completed = outfitTransition || !SavedExecutionStacks.ContainsKey(item.Id) || TreeRunner.RunRestoredObjectStack(this, item);
                if (item is XPerson scalingPerson) UpdateAnimatorBodyScale(scalingPerson, tickCounter);
                if (completed) FinishObjectSimulation(item);
                if (completed && item is XPerson animatingPerson)
                {
                    TickAvatarAnimations(animatingPerson);
                }
                if (completed && item is XPerson person) ApplyPersonPostUpdate(person, tickCounter);
                return completed;
            }
            catch (Exception error) when (error is SimulationException or TreeRunException)
            {
                // Object errors stop the manager pass and reset the object to its initializer.
                var failing = SavedExecutionStacks.GetValueOrDefault(item.Id)?.Top;
                var failingTree = failing?.Tree as Bhav;
                var operand = failingTree is not null && failing!.Instruction >= 0 &&
                              failing.Instruction < failingTree.Instructions.Count
                    ? Convert.ToHexString(failingTree.Instructions[failing.Instruction].Operand)
                    : string.Empty;
                var frames = SavedExecutionStacks.GetValueOrDefault(item.Id)?.Frames.Select(frame =>
                {
                    var tree = frame.Tree as Bhav;
                    var instruction = tree is not null && frame.Instruction >= 0 && frame.Instruction < tree.Instructions.Count
                        ? tree.Instructions[frame.Instruction]
                        : null;
                    var behaviorScope = frame.Table as BehaviorScope;
                    var treeLabel = behaviorScope?.FileFor(frame.TreeId)?.Find(ResourceType.Bhav,
                        unchecked((ushort)frame.TreeId))?.Label;
                    var stackObject = ObjectByStackObject(item, frame.StackObject);
                    return new ThreadFrameError(frame.TreeId, frame.Instruction, instruction?.Opcode, frame.StackObject,
                        frame.Parameters.ToArray(), frame.Locals.ToArray(), frame.CodeOwner,
                        treeLabel, behaviorScope?.ObjectKey,
                        behaviorScope is null ? null : BehaviorScope.ScopeOf(frame.TreeId).ToString(),
                        instruction?.TrueTarget, instruction?.FalseTarget,
                        instruction is null ? null : Convert.ToHexString(instruction.Operand),
                        stackObject?.Id, stackObject?.TypeRecord?.Key, stackObject is XPerson);
                }).ToArray() ?? [];
                ThreadErrors.Add(new ThreadError(item.Id,
                    $"{error.Message} (tree {failing?.TreeId}, instruction {failing?.Instruction}, stack {failing?.StackObject}, operand {operand})",
                    (item as XPerson)?.Temporaries.ToArray(), frames));
                var activeStack = SavedExecutionStacks.GetValueOrDefault(item.Id);
                NotifyPersonObjectReferenceRecipients(item);
                SavedExecutionStacks.Remove(item.Id);
                if (item is XPerson erroredPerson)
                {
                    ResetPersonForChangedCode(erroredPerson);
                }
                else if (FunctionTreeId(item, 3) != 0)
                {
                    ResetObjectForChangedCode(item);
                }
                else if ((item.Flags & 0x40u) != 0)
                {
                    BeginObjectRemoval(item, 1);
                    item.SavedStack.Clear();
                    item.SavedStackSize = 0;
                }
                else ResetObjectAfterScriptError(item, activeStack);
                // Sets wait ticks to -1 so the object is eligible on the next manager pass.
                if (item.ClockManagerIndex > 0)
                    ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
                return false;
            }
            catch (Exception error)
            {
                error.Data["tickingObject"] = item.Id;
                error.Data["executionLocation"] = CurrentExecutionLocation;
                throw;
            }
        }
    }

    internal void TickAvatarAnimations(XPerson person)
    {
        person.AnimatorMilliseconds = unchecked(person.AnimatorMilliseconds + 42);
        if (person.LastAnimationPlayback is { } expiredLast &&
            MathF.Abs(expiredLast.PracticeWeight) <= LastPracticePruneWeightThreshold)
            person.LastAnimationPlayback = null;
        if (person.AnimationPlayback is { RouteDriven: true } && person.NativeRoute is null)
            StopCurrentAnimation(person);
        UpdateCarryPractice(person);
        UpdateBackgroundPractice(person);
        UpdateAnimatorWalkingScale(person);
        // Pauses route locomotion during outfit changes to prevent animation conflicts.
        if (!AnimatorOutOfBounds(person) && !person.NativeOutfitTransitionActive)
        {
            var firstFollowTick = person.NativeRoute is { BeginFollowPendingTick: true };
            if (firstFollowTick)
                person.NativeRoute = person.NativeRoute! with { BeginFollowPendingTick = false };
            var routeHeadingClamped = !firstFollowTick && UpdateAnimatorRootDirection(this, person);
            if (!firstFollowTick)
                RouteExecutor.TryBeginRouteTurnRoot(this, person);
            ApplyAnimatorRouteEntryGate(person, routeHeadingClamped);
            RouteExecutor.TickRouteTurnRootInterpolation(this, person);
            if (person.AnimatorPoolTile && person.AnimatorRouteTransitionState == 0)
                person.AnimatorRouteTransitionState = 1;
            switch (person.AnimatorControlState)
            {
                case 0 when person.AnimatorPracticeScaleEnabled && person.AnimationPlayback is { } primaryPractice:
                    var practiceScale = person.AnimatorWalkingScale;
                    primaryPractice.SetPracticeScale(primaryPractice.PracticeScale < 0f ? -practiceScale : practiceScale);
                    break;
                case 1:
                    if (person.AnimationPlayback is not null && person[PersonData.FreezeForIdleHotDate] == 0)
                    {
                        StopCurrentAnimation(person);
                        person.BackgroundPracticePersonState = -1;
                        UpdateBackgroundPractice(person);
                    }
                    person.AnimatorGaitEntryState = 0;
                    person.AnimatorPoolTransitionState = 0;
                    break;
                case 2:
                    if (person.AnimationPlayback is not null)
                    {
                        StopCurrentAnimation(person);
                        person.BackgroundPracticePersonState = -1;
                        UpdateBackgroundPractice(person);
                    }
                    person.AnimatorGaitEntryState = 0;
                    person.AnimatorPoolTransitionState = 0;
                    break;
                case 4 or 12:
                    person.AnimatorGaitEntryState = 0;
                    person.AnimatorPoolTransitionState = 0;
                    break;
                case 8:
                    person.AnimatorGaitEntryState = 1;
                    person.AnimatorPoolTransitionState = 1;
                    break;
            }
            if (person.AnimatorControlState is 6 or 7 or 11)
            {
                person.AnimatorGaitEntryState = 0;
                person.AnimatorPoolTransitionState = 0;
            }
            if (person.AnimatorControlState != 5 && person.NativeRoute?.MotionAnimationIndex is >= 11 and <= 18)
            {
                person.AnimatorPoolTransitionState = 1;
            }
            TickRouteLocomotionFade(person);
            TickRouteFollowTransition(person);
            TickPoolSwimExit(person);
            TickDeferredEndFollow(person);
            RouteExecutor.TickStandingStartRootInterpolation(this, person);
            RouteExecutor.TickRouteRootMovement(this, person,
                SavedExecutionStacks.GetValueOrDefault(person.Id));
            TickAnchorRootPinning(person);
        }
        if (AvatarAnimationRootOnly) return;
        foreach (var playback in person.GetAnimationPracticeApplicationOrder())
            Tick(playback);
        if (person.LastAnimationPlayback is { } fadedLast &&
            !fadedLast.IsPracticeWeightFadeActive &&
            MathF.Abs(fadedLast.PracticeWeight) <= LastPracticePruneWeightThreshold)
        {
            person.LastAnimationPlayback = null;
        }

        void Tick(AvatarAnimationPlayback playback)
        {
            var isLast = ReferenceEquals(playback, person.LastAnimationPlayback);
            playback.Tick(deliverAnimationEvents: !isLast, person.AnimatorMilliseconds);
            if (isLast)
            {
                playback.EventQueue.Clear();
                playback.SoundEventQueue.Clear();
                return;
            }
            person.PendingAnimationEvents.AddRange(playback.EventQueue);
            playback.EventQueue.Clear();
            foreach (var sound in playback.SoundEventQueue)
            {
                if (ApplyAnimationTimeProperty(person, playback, sound)) continue;
                if (string.IsNullOrEmpty(sound.PackId) &&
                    sound.Resource is "anchor" or "discontinuity")
                    continue;
                AnimationSound?.Invoke(person.Id, sound);
            }
            playback.SoundEventQueue.Clear();
        }
        // Updates head posture only during full practice ticks, not root-only ticks.
        TickHeadPosture(person);
    }

    private void TickAnchorRootPinning(XPerson person)
    {
        if (AnchorBoneWorldResolver is null || person.AnimationPlayback is null ||
            person.NativeRoute is not { AnimatorFollowState: 1 } ||
            person.AnimatorControlState is not (6 or 8 or 9 or 10))
        {
            person.AnimatorAnchorInitialized = false;
            return;
        }
        if (string.IsNullOrEmpty(person.AnimatorAnchorBone) ||
            person.AnimatorRootX is not { } rx || person.AnimatorRootY is not { } ry ||
            !float.IsFinite(rx) || !float.IsFinite(ry))
        {
            person.AnimatorAnchorInitialized = false;
            return;
        }
        if (AnchorBoneWorldResolver.Invoke(person, person.AnimatorAnchorBone) is not { } boneAvatarPos)
            return;
        // 0058dc76.c:101-103 zeroes avatar-local X/Y and transforms (0, 0, bz) by the avatar world transform
        // (LotFacingAngle rotation + body scale + root translation in VitaBoy units).
        const float scale = 16f / 3f;
        var rootDirection = AvatarDirection.RootDirection(person.AnimatorRootDirection,
            person[ObjectData.Direction], person.AnimatorDisplacementDirection);
        var facingMatrix = Matrix4x4.CreateRotationY(AvatarDirection.LotFacingAngle(rootDirection));
        var bodyScale = float.IsFinite(person.AnimatorBodyScale) && person.AnimatorBodyScale > 0f
            ? person.AnimatorBodyScale
            : 1f;
        var curPos = Vector3.Transform(new Vector3(0f, 0f, boneAvatarPos.Z), facingMatrix) * bodyScale +
                     new Vector3(ry / scale, 0f, rx / scale);
        if (!person.AnimatorAnchorInitialized)
        {
            person.AnimatorAnchorWorldPosition = curPos;
            person.AnimatorAnchorInitialized = true;
            return;
        }
        var delta = person.AnimatorAnchorWorldPosition - curPos;
        var dx = scale * delta.Z;
        var dy = scale * delta.X;
        var distSq = dx * dx + dy * dy;
        if (distSq > 0.01f && distSq <= 10000.0f)
        {
            person.AnimatorRootX = rx + dx;
            person.AnimatorRootY = ry + dy;
        }
    }

    /// <summary>Applies animation event time properties for non-audio visual cues such as dressing and
    /// undressing accessories.</summary>
    public static bool ApplyAnimationTimeProperty(XPerson person, AvatarAnimationPlayback playback, AvatarAnimationSoundEvent prop)
    {
        if (!string.IsNullOrEmpty(prop.PackId)) return false;
        _ = int.TryParse(prop.Cue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue);
        switch (prop.Resource)
        {
            case "interruptable":
            case "interruptible":
                playback.Interruptible = intValue != 0;
                return true;
            case "lefthand":
                person[PersonData.HandGestureLeft] = unchecked((short)intValue);
                return true;
            case "righthand":
                person[PersonData.HandGestureRight] = unchecked((short)intValue);
                return true;
            case "censor":
                person[PersonData.CensorshipFlags] = unchecked((short)intValue);
                return true;
            case "dress":
                person.DressAccessory(prop.Cue);
                return true;
            case "undress":
                person.UndressAccessory(prop.Cue);
                return true;
            case "discontinuity":
                if (person.LastAnimationPlayback is not null)
                {
                    person.LastAnimationPlayback = null;
                    person.AnimatorDiscontinuityPending = true;
                }
                return true;
            case "anchor":
            {
                if (!ReferenceEquals(playback, person.AnimationPlayback))
                    return false;
                var boneName = prop.BoneName ?? "";
                if (intValue == 0)
                {
                    playback.AnchorBone = null;
                    if (string.Equals(person.AnimatorAnchorBone, boneName, StringComparison.Ordinal))
                    {
                        person.AnimatorAnchorBone = null;
                        person.AnimatorAnchorInitialized = false;
                    }
                }
                else
                {
                    playback.AnchorBone = boneName;
                    if (!string.Equals(person.AnimatorAnchorBone, boneName, StringComparison.Ordinal))
                    {
                        person.AnimatorAnchorBone = boneName;
                        person.AnimatorAnchorInitialized = false;
                    }
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Applies cached head directions before resolving the current target and refreshing
    /// directions for the subsequent tick.</summary>
    private void TickHeadPosture(XPerson person)
    {
        if (person[PersonData.HeadSeekTimeout] > 0)
        {
            person[PersonData.HeadSeekTimeout]--;
            if (person[PersonData.HeadSeekTimeout] == 0)
                person[PersonData.HeadSeekState] = 8;
        }

        var seeking = (person[PersonData.HeadSeekState] & 1) != 0;
        person.AnimatorHeadAdjustment = Pose.AdvanceHeadAdjustment(person.AnimatorHeadAdjustment,
            person.AnimatorHeadBaselineDirection, person.AnimatorHeadTargetDirection, seeking, out var hitLimit);
        if (hitLimit)
        {
            switch (person[PersonData.HeadSeekLimitAction])
            {
                case 0: person[PersonData.HeadSeekState] = 5; break;
                case 1: person[PersonData.HeadSeekState] = 4; break;
            }
        }

        if (person.AnimatorHeadSeekRegistrationsAvailable == false ||
            (person.AnimatorHeadSeekRegistrationsAvailable is null && HeadSeekGeometryResolver is null) ||
            (person[PersonData.HeadSeekState] & 1) == 0) return;

        XObject? target = null;
        if (person[PersonData.HeadSeekObject] != 0)
        {
            target = ObjectByStackObject(person, person[PersonData.HeadSeekObject]);
            if (target is null)
            {
                person[PersonData.HeadSeekObject] = 0;
                person[PersonData.HeadSeekState] = 0x10;
            }
            else if (target.Position.Height != person.Position.Height)
            {
                person[PersonData.HeadSeekObject] = 0;
                person[PersonData.HeadSeekState] = 0x10;
            }
        }

        if (HeadSeekGeometryResolver?.Invoke(person, target ?? person) is { } geometry)
        {
            if (target is not null)
                person.AnimatorHeadTargetPoint = geometry.Target;
            person.AnimatorHeadBaselineDirection = Pose.HeadDirection(geometry.Actor, geometry.HeadFake,
                person.AnimatorHeadBaselineDirection);
            person.AnimatorHeadTargetDirection = Pose.HeadDirection(geometry.Actor, person.AnimatorHeadTargetPoint,
                person.AnimatorHeadTargetDirection);
        }
    }

    internal const int StopPracticeFadeDurationMilliseconds = 200;
    private const float LastPracticePruneWeightThreshold = 1e-6f;

    /// <summary>Stops the primary animation playback, fading it out in the Last slot per FUN_00586ed8,
    /// and resets the active skill index to -1.</summary>
    internal void StopCurrentAnimation(XPerson person)
    {
        if (person.AnimationPlayback is { } current)
        {
            current.EventQueue.Clear();
            current.SoundEventQueue.Clear();
            if (current.PracticeWeight > LastPracticePruneWeightThreshold)
            {
                current.AllowUnblendedOverride = false;
                current.PracticePriority = current.PracticePriority + 1;
                current.FadePracticeWeight(person.AnimatorMilliseconds,
                    unchecked(person.AnimatorMilliseconds + StopPracticeFadeDurationMilliseconds),
                    current.PracticeWeight, 0f);
                person.LastAnimationPlayback = current;
            }
            else
            {
                person.LastAnimationPlayback = null;
            }
        }
        person.AnimationPlayback = null;
        person.AnimatorAnchorBone = null;
        person.AnimatorAnchorInitialized = false;
        person.AnimatorAnchorWorldPosition = Vector3.Zero;
        person.AnimatorCurrentSkill = -1;
    }

    /// <summary>Transitions out any active primary animation and installs a new primary playback,
    /// consuming any pending discontinuity flag per FUN_005926e6.</summary>
    internal void BeginPrimaryAnimation(XPerson person, AvatarAnimationPlayback next)
    {
        StopCurrentAnimation(person);
        if (person.AnimatorDiscontinuityPending)
        {
            person.AnimatorDiscontinuityPending = false;
            person.LastAnimationPlayback = null;
            next.PracticePriority += 5;
        }
        person.PendingAnimationEvents.Clear();
        person.AnimatorCurrentSkill = -1;
        person.AnimationPlayback = next;
    }

    /// <summary>Stops reach playback, clears event queues, and transitions to the appropriate posture
    /// control state based on the Sitting attribute.</summary>
    internal void StopReachAnimation(XPerson person)
    {
        StopCurrentAnimation(person);
        person.ReachAnimationActive = false;
        person.PrimaryAnimationWaiting = false;
        person.PrimaryAnimationEventsRun = 0;
        person.PrimaryAnimationBackwards = false;
        person.PendingAnimationEvents.Clear();
        person.AnimatorControlState = person[PersonData.Sitting] switch
        {
            0 => 3,
            1 => 1,
            2 => 2,
            _ => person.AnimatorControlState,
        };
    }

    /// <summary>Steers root heading toward the lookahead target by up to 0.2 octants per tick, scaled by
    /// walking scale, or triggers an in-place turn if heading error reaches 90 degrees.</summary>
    private static bool UpdateAnimatorRootDirection(SimulationWorld world, XPerson person)
    {
        if (person.AnimatorControlState is not (3 or 9 or 10)) return false;
        if (person.NativeRoute is not { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route)
            return false;
        if (route.LocomotionFadeEndMils != 0) return false;

        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY)) return false;
        var lookahead = RouteExecutor.ResolveRouteLookahead(world, person, route, rootX, rootY);
        if (lookahead.AdvancedCursor)
        {
            route = lookahead.Route;
            person.NativeRoute = route;
        }
        var deltaX = lookahead.TargetX - rootX;
        var deltaY = lookahead.TargetY - rootY;
        var distanceSquared = deltaX * deltaX + deltaY * deltaY;
        if (distanceSquared <= 1e-6f && !route.MotionTargetIsDestination) return false;

        var current = person.AnimatorRootDirection ?? person[ObjectData.Direction];
        var targetDirection = distanceSquared <= 1e-6f && route.MotionTargetIsDestination
            ? RouteExecutor.ArrivalHeading(route) : AvatarDirection.Toward(deltaX, deltaY);
        if (person.AnimatorControlState is 9 or 10 && route.AnimatorFollowState == 1 &&
            person.AnimatorRouteTransitionState == 0 && distanceSquared > 0.25f)
        {
            var headingError = targetDirection - current;
            while (headingError >= 4f) headingError -= 8f;
            while (headingError < -4f) headingError += 8f;
            var largeTurnThreshold = person.AnimatorBodyStringMode > 1 ? 0.57f : 2.0f;
            if (person.AnimatorWalkingScale is < 1f and > 0f)
                largeTurnThreshold /= person.AnimatorWalkingScale;
            if (MathF.Abs(headingError) >= largeTurnThreshold)
            {
                var (turnSkill, _) = RouteExecutor.BodyTurnSkillForHeadingError(headingError);
                if (RouteExecutor.ResolveWalkAnimation(world, person, turnSkill)?.StandardRootMotionSamples is { Count: > 1 })
                {
                    person.AnimatorControlState = 3;
                    person.AnimatorGaitEntryState = 0;
                    return false;
                }
            }
        }

        var step = person.AnimatorBodyStringMode > 1 ? 0.056f : 0.2f;
        if (person.AnimatorWalkingScale < 1f && person.AnimatorWalkingScale > 0f)
            step /= person.AnimatorWalkingScale;
        person.AnimatorRootDirection = AvatarDirection.Approach(current, targetDirection, step,
            out var clamped);
        person[ObjectData.Direction] = AvatarDirection.ToObjectDirection(person.AnimatorRootDirection.Value);
        return clamped;
    }

    /// <summary>Evaluates route entry conditions, placing swimming Sims into the standing state and routing
    /// Sims into gait when the turn is clamped or within threshold step distance.</summary>
    internal void ApplyAnimatorRouteEntryGate(XPerson person, bool routeHeadingClamped)
    {
        if (person.AnimatorControlState is not (3 or 9) || person.NativeRoute is not
            { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route) return;

        var walkStyle = person[ObjectData.WalkStyle];
        var running = walkStyle == 1;
        var currentSkill = person.AnimatorCurrentSkill;
        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        // Terminal follow states only align arrival heading and never re-enter gait.
        if (route.TransitionTargetX is not null || route.AnimatorFollowState is -1 or 2 ||
            RouteExecutor.IsRoutePreferenceArrival(person, route, rootX, rootY)) return;
        var remainingDistance = float.IsFinite(rootX) && float.IsFinite(rootY)
            ? MathF.Sqrt(MathF.Pow(route.MotionTargetX - rootX, 2) + MathF.Pow(route.MotionTargetY - rootY, 2))
            : 0f;
        var finalSegment = route.MotionPath is not { Count: > 1 } || route.MotionTargetIsDestination;
        if (finalSegment)
        {
            if (person.AnimatorRouteTransitionState == 0 && remainingDistance >= (running ? 4.5f : 3f))
                person.AnimatorControlState = running ? 10 : 9;
        }
        else if (person.AnimationPlayback is not { Interruptible: false })
        {
            person.AnimatorControlState = running ? 10 : 9;
        }

        var entersGait = person.AnimatorLocomotionFadeGate != 0 || person.AnimatorGaitEntryState != 0 ||
                         currentSkill is 20 or 21;
        if (!entersGait && !person.AnimatorPoolTile)
        {
            // Scales standard animation distance by 16/3 to determine step threshold.
            var thresholdSkill = RouteExecutor.ResolveWalkAnimation(this, person, running ? 22 : 19);
            var threshold = (thresholdSkill?.StandardDistance ?? 0f) * (16f / 3f);
            var lookahead = RouteExecutor.ResolveRouteLookahead(this, person, route, rootX, rootY);
            var offsetSquared = MathF.Pow(lookahead.TargetX - rootX, 2) + MathF.Pow(lookahead.TargetY - rootY, 2);
            entersGait = routeHeadingClamped || offsetSquared <= threshold * threshold;
        }

        person.AnimatorProjectedRootStepX = 0f;
        person.AnimatorProjectedRootStepY = 0f;
        if (!entersGait)
        {
            var startSkill = running ? (short)22 : (short)19;
            person.AnimatorControlState = running ? 11 : 6;
            if (route.MotionAnimationIndex < 0 &&
                RouteExecutor.ResolveWalkAnimation(this, person, startSkill) is { StandardRootMotionSamples.Count: > 1 } startClip)
            {
                person.AnimatorGaitEntryState = 0;
                person.AnimatorPoolTransitionState = 0;
                var originX = float.IsFinite(rootX) ? rootX : person.Position.X;
                var originY = float.IsFinite(rootY) ? rootY : person.Position.Y;
                if (startClip.StandardDistance > 0f)
                {
                    var angle = ((person.AnimatorRootDirection ?? person[ObjectData.Direction]) + 6f) * (MathF.PI / 4f);
                    var startDistance = startClip.StandardDistance * (16f / 3f);
                    route = route with
                    {
                        MotionAnimationIndex = startSkill,
                        MotionFrame = 0f,
                        MotionPracticeFrame = 0f,
                        StartRootOriginX = originX,
                        StartRootOriginY = originY,
                        StartRootTargetX = originX + MathF.Cos(angle) * startDistance,
                        StartRootTargetY = originY + MathF.Sin(angle) * startDistance,
                    };
                }
                else
                {
                    route = route with { MotionAnimationIndex = startSkill, MotionFrame = 0f, MotionPracticeFrame = 0f };
                }
                person.NativeRoute = route;
                RouteExecutor.ShowWalkSkill(this, person, startClip, 0f, startClip.StandardRootMotionSamples.Count);
            }
            return;
        }

        person.AnimatorGaitEntryState = 1;
        if (person.AnimatorControlState == 10 && !running)
            person.AnimatorControlState = 9;
        var gaitSkill = person.AnimatorControlState switch
        {
            9 => 20,
            10 => 21,
            _ => 0,
        };
        if (gaitSkill == 0 || currentSkill == gaitSkill) return;

        var clip = RouteExecutor.ResolveWalkAnimation(this, person, gaitSkill);
        if (clip?.StandardRootMotionSamples is not { Count: > 1 } samples) return;
        route = route with { MotionAnimationIndex = (short)gaitSkill, MotionFrame = 0f, MotionPracticeFrame = 0f };
        person.NativeRoute = route;
        RouteExecutor.ShowWalkSkill(this, person, clip, 0f, samples.Count);
    }

    private bool AnimatorOutOfBounds(XPerson person)
    {
        if (!LotRoomMaps.TryGetValue(person.Position.Height, out var map)) return false;
        // Tile coordinates shift FTiles right by 4; outer border tiles are out of bounds.
        var tileX = person.Position.X >> 4;
        var tileY = person.Position.Y >> 4;
        return tileX < 1 || tileY < 1 || tileX >= map.Width - 1 || tileY >= map.Height - 1;
    }

    /// <summary>Fades out route locomotion animation as the Sim nears the final waypoint based on skeleton
    /// stride markers.</summary>
    private void TickRouteLocomotionFade(XPerson person)
    {
        if (person.NativeRoute is not { } route || route.MotionTargetX == int.MinValue ||
            route.MotionTargetY == int.MinValue || person.AnimationPlayback is not { RouteDriven: true } playback)
        {
            person.AnimatorLocomotionFadeGate = 0;
            return;
        }

        // The final waypoint in a path specifies arrival facing rather than movement.
        if (route.MotionPath is { Count: > 1 } && !route.MotionTargetIsDestination) return;
        if (person.AnimatorControlState is not (3 or 9 or 10)) return;
        // Route preferences bypass locomotion fade on arrival.
        if (person[ObjectData.RoutePreference] != 0) return;

        if (route.LocomotionFadeEndMils > 0)
        {
            var expiryRootX = person.AnimatorRootX ?? route.MotionX;
            var expiryRootY = person.AnimatorRootY ?? route.MotionY;
            var distanceToTarget = float.IsFinite(expiryRootX) && float.IsFinite(expiryRootY)
                ? MathF.Sqrt(MathF.Pow(route.MotionTargetX - expiryRootX, 2) + MathF.Pow(route.MotionTargetY - expiryRootY, 2))
                : float.PositiveInfinity;
            // Maintains fade within 2 units of target until the deadline or WalkStyle 1.
            if (person[ObjectData.WalkStyle] != 1 && distanceToTarget < 2f &&
                person.AnimatorMilliseconds < route.LocomotionFadeEndMils) return;

            route = route with { LocomotionFadeEndMils = -1 };
            person.NativeRoute = route;
            person.AnimatorLocomotionFadeGate = 0;
            person.AnimatorPoolTransitionState = 0;
        }

        if (route.LocomotionFadeEndMils < 0)
        {
            person.AnimatorRootX = route.MotionTargetX;
            person.AnimatorRootY = route.MotionTargetY;
            if (route.LookTowardsFollow)
                person.AnimatorRootDirection = route.ArrivalFacing;
            person.AnimatorControlState = 3;
            person.AnimatorDiscontinuityPending = true;
            StopCurrentAnimation(person);
            person.LastAnimationPlayback = null;
            person.NativeRoute = route with
            {
                AnimatorFollowState = -1,
                MotionAnimationIndex = -1,
                LocomotionFadeEndMils = 0,
                MotionX = route.MotionTargetX,
                MotionY = route.MotionTargetY,
            };
            person.AnimatorLocomotionFadeGate = 0;
            person.AnimatorPoolTransitionState = 0;
            return;
        }

        var clip = playback.Clip;
        // Marker times and strides derive from standard walk skill 0x14.
        if (LocomotionStrideResolver is null || clip.FrameCount <= 0 || clip.DurationMilliseconds <= 0f ||
            person.AnimatorWalkingScale <= 0f)
            return;

        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY)) return;
        var distance = MathF.Sqrt(MathF.Pow(route.MotionTargetX - rootX, 2) + MathF.Pow(route.MotionTargetY - rootY, 2));

        (float TargetWeight, int DurationMilliseconds)? fade = null;
        var markerClip = RouteExecutor.ResolveWalkAnimation(this, person, 0x14);
        if (markerClip is not null &&
            markerClip.LocomotionMarkerOne is { } markerOne && markerOne.Bone.Length > 0 &&
            markerClip.LocomotionMarkerTwo is { } markerTwo && markerTwo.Bone.Length > 0 &&
            LocomotionStrideResolver.Invoke(person, markerClip.Name) is { } strides)
        {
            var phase = playback.Frame / clip.FrameCount;
            var stride = Pose.LocomotionStrideAtPhase(phase, markerOne.NormalizedTime, markerTwo.NormalizedTime,
                strides.MarkerOneStride, strides.MarkerTwoStride);
            if (!person.AnimatorPoolTile && person[ObjectData.WalkStyle] == 1)
                stride *= person.AnimatorWalkingScale;

            fade = Pose.LocomotionFade(distance, stride, phase, markerOne.NormalizedTime, markerTwo.NormalizedTime,
                clip.DurationMilliseconds, person.AnimatorWalkingScale);
            if (fade is null && stride <= 0f && route.MotionTargetIsDestination && distance <= 1e-3f)
                fade = (0.2f, 100);
        }
        else if (route.MotionTargetIsDestination && distance <= 1e-3f)
        {
            // Fallback to a 100ms terminal fade when animations lack foot anchor markers.
            fade = (0.2f, 100);
        }

        if (fade is not { } schedule) return;

        person.AnimatorRouteTransitionState = 1;
        person.AnimatorPoolTransitionState = 1;
        var endMils = unchecked(person.AnimatorMilliseconds + schedule.DurationMilliseconds);
        playback.FadePracticeWeight(person.AnimatorMilliseconds, endMils, playback.PracticeWeight, schedule.TargetWeight);
        person.NativeRoute = route with { LocomotionFadeEndMils = endMils };
        person.AnimatorLocomotionFadeGate = 1;
    }

    /// <summary>Finalizes the route follow transition when locomotion fade ends, committing the target
    /// position and aligning arrival heading.</summary>
    private static void TickRouteFollowTransition(XPerson person)
    {
        if (person.NativeRoute is { AnimatorFollowState: -1, TransitionTargetX: { } transitionX,
                TransitionTargetY: { } transitionY } arrivalRoute)
        {
            var arrivalRootX = person.AnimatorRootX ?? arrivalRoute.MotionX;
            var arrivalRootY = person.AnimatorRootY ?? arrivalRoute.MotionY;
            if (MathF.Abs(arrivalRootX - transitionX) > 0.000001f || MathF.Abs(arrivalRootY - transitionY) > 0.000001f)
                return;
            person.NativeRoute = arrivalRoute with
            {
                AnimatorFollowState = 2,
                TransitionTargetX = null,
                TransitionTargetY = null,
            };
            return;
        }
        if (person.NativeRoute is not { AnimatorFollowState: -1, MotionAnimationIndex: -1 } route) return;
        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY) ||
            MathF.Abs(rootX - route.MotionX) > 0.000001f || MathF.Abs(rootY - route.MotionY) > 0.000001f)
            return;

        // Waits for the arrival turn to align with the destination heading.
        if ((route.LookTowardsHeading is not null || !route.LookTowardsFollow) && route.MotionTargetIsDestination)
        {
            var headingError = RouteExecutor.ArrivalHeading(route) -
                               (person.AnimatorRootDirection ?? person[ObjectData.Direction]);
            while (headingError >= 4f) headingError -= 8f;
            while (headingError < -4f) headingError += 8f;
            if (MathF.Abs(headingError) > 0.000001f) return;
        }
        person.NativeRoute = route with { AnimatorFollowState = 2 };
    }

    /// <summary>Processes deferred route completion requests, waiting if the active animation practice is
    /// non-interruptible.</summary>
    private void TickDeferredEndFollow(XPerson person)
    {
        if (person.NativeRoute is not { EndFollowPending: true, AnimatorFollowState: 1 } route ||
            person.AnimationPlayback is { Interruptible: false }) return;

        var poolSwimExitStarted = false;
        float? poolSwimExitRootX = null;
        float? poolSwimExitRootY = null;
        float? poolSwimExitDirection = null;
        if (person.AnimatorPoolTile)
        {
            person.AnimatorRouteTransitionState = 3;
            if (person.AnimatorPoolTransitionState == 0)
            {
                person.AnimatorControlState = 3;
            }
            else
            {
                poolSwimExitStarted = true;
                poolSwimExitRootX = person.AnimatorRootX ?? route.MotionX;
                poolSwimExitRootY = person.AnimatorRootY ?? route.MotionY;
                poolSwimExitDirection = person.AnimatorRootDirection ?? person[ObjectData.Direction];
                StartPoolSwimExitPractice(person);
            }
        }

        // Route execution reports busy while the pool swim exit animation plays.
        person.NativeRoute = route with
        {
            AnimatorFollowState = poolSwimExitStarted ? 1 : 3,
            EndFollowPending = !poolSwimExitStarted,
            PoolSwimExitPending = poolSwimExitStarted,
            PoolSwimExitRootX = poolSwimExitRootX,
            PoolSwimExitRootY = poolSwimExitRootY,
            PoolSwimExitDirection = poolSwimExitDirection,
        };
    }

    /// <summary>Starts the swimming exit animation practice (skill 0x13) when concluding a route at a pool
    /// edge.</summary>
    internal void StartPoolSwimExitPractice(XPerson person)
    {
        person.AnimatorControlState = 7;
        var swimExit = RouteExecutor.ResolveWalkAnimation(this, person, 0x13);
        if (swimExit is not null)
        {
            // Swimming exit plays the swim transition skill in reverse.
            BeginPrimaryAnimation(person, new AvatarAnimationPlayback(swimExit, backwards: true, loop: false,
                startedAtMils: person.AnimatorMilliseconds)
            {
                Interruptible = true,
                PoolSwimExit = true,
            });
        }
        person.AnimatorCurrentSkill = 0x13;
    }

    /// <summary>Finalizes swimming route completion while allowing the one-shot exit animation to finish
    /// playing.</summary>
    private void TickPoolSwimExit(XPerson person)
    {
        if (person.NativeRoute is not { PoolSwimExitPending: true } route) return;
        if (person.AnimatorControlState == 7 &&
            person.AnimationPlayback is null or { EndReached: true })
        {
            // Restores root position snapshot upon entering the pool exit terminal branch.
            var rootX = route.PoolSwimExitRootX ?? person.AnimatorRootX ?? route.MotionX;
            var rootY = route.PoolSwimExitRootY ?? person.AnimatorRootY ?? route.MotionY;
            var direction = route.PoolSwimExitDirection ?? person.AnimatorRootDirection ?? person[ObjectData.Direction];
            person.AnimatorRootX = rootX;
            person.AnimatorRootY = rootY;
            person.AnimatorRootDirection = direction;
            person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(direction);
            person.AnimatorStateTailField198 = 1;
            StopCurrentAnimation(person);
            person.AnimatorControlState = 3;
            person.NativeRoute = route with
            {
                MotionX = rootX,
                MotionY = rootY,
                AnimatorFollowState = person.AnimatorRouteTransitionState switch
                {
                    1 => -1,
                    2 => 2,
                    _ => 3,
                },
                PoolSwimExitPending = false,
            };
            return;
        }
        if (person.AnimatorRouteTransitionState != 3) return;
        if (person.AnimationPlayback is { PoolSwimExit: true, EndReached: true })
        {
            StopCurrentAnimation(person);
            person.AnimatorControlState = 3;
            person.NativeRoute = route with
            {
                AnimatorFollowState = 3,
                PoolSwimExitPending = false,
            };
            return;
        }
        if (person.AnimatorControlState != 7) return;

        person.AnimatorControlState = 3;
        person.NativeRoute = route with
        {
            AnimatorFollowState = 3,
            PoolSwimExitPending = person.AnimationPlayback is { PoolSwimExit: true },
        };
    }

    /// <summary>Updates and eases walking scale. Route movement steps consume the previous tick's scale
    /// before easing occurs.</summary>
    private static void UpdateAnimatorWalkingScale(XPerson person)
    {
        var scaleSelector = 1f;
        if (person[ObjectData.WalkStyle] == 1)
        {
            if (person.AnimatorControlState == 0)
                scaleSelector = 8f;
            else if (person.AnimatorControlState is >= 4 and <= 9)
                scaleSelector = person.AnimatorBodyStringMode <= 1 ? 8f : 0f;
        }
        var target = 1.2f + scaleSelector * (scaleSelector < 0.5f ? 0.2f : 0.4f);
        const float step = 0.2f;
        var current = person.AnimatorWalkingScale;
        if (current >= target - step && current <= target + step)
            current = target;
        else if (target > current)
            current += step;
        else
            current = target;
        person.AnimatorWalkingScale = current;
        if (person.NativeRoute is { } route)
            person.NativeRoute = route with { MotionPlaybackScale = current };
    }

    /// <summary>Infers automatic carry animations from the person's first object slot unless a reach
    /// animation or explicit carry animation is active.</summary>
    internal void UpdateCarryPractice(XPerson person)
    {
        if (person.CarryPracticeMode == 6) return;

        var desiredState = -1;
        if (!person.ReachAnimationActive)
        {
            person.CarryPracticeMode = person.FootprintSlots.Count > 0 &&
                person.FootprintSlots[0].OccupantObjectId != 0 ? 1 : 0;
            if (person.CarryPracticeMode is > 0 and < 6)
                desiredState = 0;
        }

        if (desiredState == person.CarryPracticeCachedState) return;

        person.CarryAnimationPlayback = null;
        if (desiredState == 0)
        {
            var clip = RouteExecutor.ResolveWalkAnimation(this, person, 0x26);
            if (clip is not null) person.CarryAnimationPlayback = new AvatarAnimationPlayback(clip, backwards: false,
                loop: true, startedAtMils: person.AnimatorMilliseconds);
        }
        person.CarryPracticeCachedState = desiredState;
    }

    internal void UpdateBackgroundPractice(XPerson person)
    {
        var state = person[PersonData.Sitting];
        var changed = state != person.BackgroundPracticePersonState;
        if (changed)
        {
            person.BackgroundAnimationPlayback = null;
        }
        else if (person.BackgroundAnimationPlayback is not null && !UpdateBackgroundPracticeForPersonState(person))
        {
            return;
        }

        // Skips background practice updates when VitaBoy flags bit 2 (0x0004) is set.
        if ((unchecked((ushort)person.Definition[(int)ObjdField.VitaBoyFlags]) & 0x0004) != 0) return;

        person.BackgroundPracticePersonState = state;

        var standardIndex = state switch { 0 => 3, 1 => 1, 2 => 2, _ => -1 };
        if (standardIndex < 0) return;
        var clip = RouteExecutor.ResolveWalkAnimation(this, person, standardIndex);
        if (clip is null) return;
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(clip, backwards: false, loop: true,
            startedAtMils: person.AnimatorMilliseconds);

        static bool UpdateBackgroundPracticeForPersonState(XPerson sim)
        {
            // Human animator body string modes (0 and 1) preserve active practice, while pet modes (2
            // through 5) trigger a refresh.
            var bodyStringMode = sim.AnimatorBodyStringMode;
            var value = bodyStringMode ^ 1;
            var difference = unchecked((value >> 1) - (value & bodyStringMode));
            return (unchecked((uint)difference) & 0x4000_0000u) != 0;
        }
    }

    /// <summary>Starts an explicit-position helper route. Player movement commands enqueue Destination.iff
    /// actions on the normal interaction stack instead.</summary>
    public bool StartDirectPositionRoute(XPerson person, ObjectPosition destination)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (!ReferenceEquals(person, ObjectById(person.Id)))
            throw new ArgumentException("The Sim is not in this world.", nameof(person));
        // Replaces any prior helper route; script-owned and portal-nested routes retain their own
        // cancellation lifecycles.
        if (_directPositionRoutes.TryGetValue(person.Id, out var previous) &&
            RouteExecutor.CancelDirectPosition(this, person, previous.Frame))
            _directPositionRoutes.Remove(person.Id);
        if (_directPositionRoutes.ContainsKey(person.Id) || person.NativeRoute is not null)
            throw new InvalidOperationException("The Sim is already routing.");

        var frame = new StackFrame(person.Id, [], []);
        var stack = SavedExecutionStacks.GetValueOrDefault(person.Id);
        var result = RouteExecutor.StartDirectPosition(this, person, frame, destination, stack);
        if (result == PrimitiveResult.False) return false;
        if (frame.Waiting) _directPositionRoutes.Add(person.Id, (frame, stack));
        return true;
    }

    private void ContinueDirectPositionRoutes()
    {
        foreach (var (personId, route) in _directPositionRoutes.ToArray())
        {
            if (Objects.GetValueOrDefault(personId) is XPerson { NativeOutfitTransitionActive: true }) continue;
            if (ObjectById(personId) is not XPerson person || !ReferenceEquals(person.NativeRoute?.OwnerFrame, route.Frame))
            {
                _directPositionRoutes.Remove(personId);
                continue;
            }

            RouteExecutor.Continue(this, person, route.Frame, SavedExecutionStacks.GetValueOrDefault(personId));
            if (!route.Frame.Waiting) _directPositionRoutes.Remove(personId);
        }
    }

    /// <summary>Enqueues an interaction in descending priority order and removes any lower-priority actions
    /// that follow the insertion point.</summary>
    public bool EnqueueInteractionAction(XPerson person, XObject owner, Interaction interaction, byte interactionId,
        int priority, XObject? secondary, bool secondarySpecified, int recordFlags)
    {
        var action = new ActionQueueSlot
        {
            Threshold = priority,
            Person = person,
            Owner = owner,
            Secondary = secondarySpecified ? secondary : owner,
            InteractionId = interactionId,
            ActionTree = interaction.ActionTree,
            Attenuation = InteractionAttenuation(person, interaction),
            Flags = (interaction.Flags & 0x100) != 0 ? 1 : 0,
        };
        action.Flags |= recordFlags;
        return EnqueueInteractionAction(person, action, (recordFlags & 2) != 0);
    }

    public InteractionTable? EffectiveInteractions(XObject? obj)
    {
        if (obj is null) return null;
        if (obj.TypeRecord?.Interactions is { } direct) return direct;
        if (obj.Definition is { Length: > (int)SimsEngine.Formats.ObjdField.MasterId } def &&
            def[(int)SimsEngine.Formats.ObjdField.MasterId] != 0)
        {
            var resolved = InteractionMenus.Target(this, obj);
            if (resolved.TypeRecord?.Interactions is { } targetTable) return targetTable;
            foreach (var member in MultiTileMembers(obj))
            {
                if (member.TypeRecord?.Interactions is { } memberTable) return memberTable;
            }
        }
        return null;
    }

    public bool IsImmediateMenuInteraction(ActionQueueSlot choice) =>
        EffectiveInteractions(choice.Owner)?.Interactions
            .FirstOrDefault(entry => entry.TtaIndex == choice.RawDword10) is { } interaction &&
        (interaction.Flags & 4) != 0;

    public bool RunImmediateMenuInteraction(XPerson person, ActionQueueSlot choice)
    {
        if (ImmediateInteractionRunning || choice.Owner?.TypeRecord?.BehaviorScope is not { } scope) return false;
        var previousPriority = person.ActivePriority;
        var previousImmediate = ActiveImmediateAction;
        var previousImmediatePerson = ActiveImmediatePerson;
        ImmediateInteractionRunning = true;
        ActiveImmediateAction = choice;
        ActiveImmediatePerson = person;
        person.ActivePriority = unchecked((short)choice.Threshold);
        try
        {
            TreeRunner.RunWithParameters(this, person, scope, unchecked((short)choice.ActionTree),
                choice.Owner.StackObjectKey, choice.Parameters);
            return true;
        }
        finally
        {
            person.ActivePriority = previousPriority;
            ImmediateInteractionRunning = false;
            ActiveImmediateAction = previousImmediate;
            ActiveImmediatePerson = previousImmediatePerson;
        }
    }

    /// <summary>Inserts a validated action into the queue, either prepending it when flagged as a
    /// continuation or inserting it in descending priority order.</summary>
    public bool EnqueueInteractionAction(XPerson person, ActionQueueSlot candidate, bool headContinuation = false)
    {
        var action = candidate.Copy();
        action.Sequence = unchecked(++_actionQueueSequence);
        if (headContinuation)
        {
            if ((action.Flags & 0x40) != 0)
            {
                var sourceRecord = ActiveImmediateAction is not null && ReferenceEquals(ActiveImmediatePerson, person)
                    ? ActiveImmediateAction
                    : person.CurrentActionRecord;
                InheritActionRecordText(action, sourceRecord);
            }
            return EnqueueHeadContinuation(person, action);
        }

        var priority = action.Threshold;
        var count = person.ActionQueueEnd - person.ActionQueueCurrent;
        if (count is < 0 or > 8) return false;

        var insertion = count;
        while (insertion > 0)
        {
            var previous = person.ActionQueue[(person.ActionQueueCurrent + insertion - 1) & 7];
            if (previous.Threshold >= priority) break;
            insertion--;
        }

        if (insertion != count)
        {
            var retained = new ActionQueueSlot[insertion];
            for (var index = 0; index < insertion; index++)
                retained[index] = person.ActionQueue[(person.ActionQueueCurrent + index) & 7];
            for (var index = insertion; index < count; index++)
                if (person.ActionQueue[(person.ActionQueueCurrent + index) & 7].Owner is not null)
                    ActionQueueRemovalNotices.Add(new ActionQueueRemovalNotice(person.RequestKey, index));

            person.ActionQueueCurrent = 0;
            person.ActionQueueEnd = 0;
            foreach (var queued in retained)
                person.ActionQueue[person.ActionQueueEnd++] = queued;
        }
        else if (count == 8)
        {
            return false;
        }

        // Action flag 0x40 only inherits the action string when an immediate action is active on the
        // person.
        if ((action.Flags & 0x40) != 0 && ActiveImmediateAction is not null && ReferenceEquals(ActiveImmediatePerson, person))
        {
            InheritActionRecordText(action, ActiveImmediateAction);
        }

        person.ActionQueue[person.ActionQueueEnd & 7] = action;
        person.ActionQueueEnd = unchecked(person.ActionQueueEnd + 1);
        return true;
    }

    private void InheritActionRecordText(ActionQueueSlot destination, ActionQueueSlot? source)
    {
        if (source is null) return;
        var text = source.ActionText;
        if (string.IsNullOrEmpty(text))
            text = EffectiveInteractions(source.Owner)?.Interactions
                .FirstOrDefault(entry => entry.TtaIndex == source.RawDword10)?.Name;
        source.Flags &= ~0x80;
        if (!string.IsNullOrEmpty(text))
        {
            destination.ActionText = text;
            destination.Flags |= 0x80;
        }
    }

    /// <summary>Starts an interaction found by a search primitive directly without queuing it, setting the
    /// person's active priority while leaving the interaction queue untouched. Resolves the target
    /// interaction table row by index.</summary>
    public void StartFoundAction(XPerson person, XObject target, int key, int priority)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(target);
        var interaction = EffectiveInteractions(target)?.Interactions.FirstOrDefault(entry => entry.TtaIndex == key);
        var action = new ActionQueueSlot
        {
            RecordKind = 0,
            Person = person,
            Owner = target,
            Secondary = target,
            RawDword10 = key,
            Threshold = priority,
            ActionTree = interaction?.ActionTree ?? 0,
            Attenuation = interaction is null ? 0 : InteractionAttenuation(person, interaction),
            Flags = interaction is not null && (interaction.Flags & 0x100) != 0 ? 1 : 0,
        };
        action.Sequence = unchecked(++_actionQueueSequence);
        ApplyStartedInteractionAction(person, action);
    }

    /// <summary>Prepends an interaction to the head of the action queue and retains up to seven existing
    /// queued actions, discarding any that exceed the eight-slot queue capacity.</summary>
    private static bool EnqueueHeadContinuation(XPerson person, ActionQueueSlot action)
    {
        var count = unchecked(person.ActionQueueEnd - person.ActionQueueCurrent);
        if (count is < 0 or > 8) return false;
        var retainedCount = Math.Min(count, 7);
        var retained = new ActionQueueSlot[retainedCount];
        for (var index = 0; index < retainedCount; index++)
            retained[index] = person.ActionQueue[(person.ActionQueueCurrent + index) & 7].Copy();

        for (var index = 0; index < person.ActionQueue.Length; index++)
            person.ActionQueue[index] = ActionQueueSlot.EmptyRecord();
        person.ActionQueueCurrent = 0;
        person.ActionQueueEnd = 0;
        person.ActionQueue[person.ActionQueueEnd++] = action;
        foreach (var queued in retained)
            person.ActionQueue[person.ActionQueueEnd++] = queued;
        return true;
    }

    public bool RemoveQueuedActionsReferencing(XPerson person, XObject subject)
    {
        var count = unchecked(person.ActionQueueEnd - person.ActionQueueCurrent);
        if (count is < 0 or > 8) return false;

        var survivors = new List<ActionQueueSlot>(count);
        var removedAny = false;
        for (var index = 0; index < count; index++)
        {
            var action = person.ActionQueue[(person.ActionQueueCurrent + index) & 7];
            if (ReferenceEquals(action.Owner, subject) || ReferenceEquals(action.Secondary, subject))
            {
                removedAny = true;
                if (action.Owner is not null)
                    ActionQueueRemovalNotices.Add(new ActionQueueRemovalNotice(person.RequestKey, index));
            }
            else
            {
                survivors.Add(action.Copy());
            }
        }

        if (!removedAny) return false;
        for (var index = 0; index < person.ActionQueue.Length; index++)
            person.ActionQueue[index] = new ActionQueueSlot();
        for (var index = 0; index < survivors.Count; index++)
            person.ActionQueue[index] = survivors[index];
        person.ActionQueueCurrent = 0;
        person.ActionQueueEnd = survivors.Count;
        return true;
    }

    /// <summary>Cancels a queued action by sequence number, or interrupts the active interaction by
    /// clearing its priority and triggering interaction table entry 0x21 at priority 50 if immediate
    /// termination fails.</summary>
    public bool CancelInteractionAction(XPerson person, int sequence)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (sequence == person.GenericSimsCall02GateWordA84)
        {
            if (person.CurrentActionRecord?.Owner is null) return false;
            // Interactions opting into exit checks request termination at the next track boundary, while
            // ordinary actions cancel immediately.
            if (InteractionLifecycle.TryRequestGracefulExit(person, sequence)) return true;
            person.ActivePriority = 0;
            person.ActiveActionCancelRequested = true;
            if (!IdleForInputPrimitive.ShouldInterrupt(this, person) &&
                person.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == 0x21) is { } cancelEntry)
            {
                if ((cancelEntry.Flags & 4) == 0)
                {
                    EnqueueInteractionAction(person, person, cancelEntry, 0x21, 50, null, secondarySpecified: true, recordFlags: 0);
                }
                else if (!ImmediateInteractionRunning && person.TypeRecord.BehaviorScope is { } scope)
                {
                    var immediate = new ActionQueueSlot
                    {
                        Person = person,
                        Owner = person,
                        Secondary = null,
                        RawDword10 = 0x21,
                        ActionTree = cancelEntry.ActionTree,
                        Threshold = 50,
                        Flags = (cancelEntry.Flags & 0x100) != 0 ? 1 : 0,
                    };
                    InteractionTestEvaluator.Evaluate(this, person, [immediate], autonomy: false);
                    if ((immediate.Flags & InteractionTestEvaluator.Passed) != 0)
                    {
                        var previousPriority = person.ActivePriority;
                        var previousImmediate = ActiveImmediateAction;
                        var previousImmediatePerson = ActiveImmediatePerson;
                        ImmediateInteractionRunning = true;
                        ActiveImmediateAction = immediate;
                        ActiveImmediatePerson = person;
                        person.ActivePriority = 50;
                        try
                        {
                            TreeRunner.RunWithParameters(this, person, scope, unchecked((short)cancelEntry.ActionTree),
                                person.StackObjectKey, immediate.Parameters);
                        }
                        finally
                        {
                            person.ActivePriority = previousPriority;
                            ImmediateInteractionRunning = false;
                            ActiveImmediateAction = previousImmediate;
                            ActiveImmediatePerson = previousImmediatePerson;
                        }
                    }
                }
            }
            return true;
        }

        var count = unchecked(person.ActionQueueEnd - person.ActionQueueCurrent);
        if (count is < 0 or > 8) return false;

        var survivors = new List<ActionQueueSlot>(count);
        var removed = false;
        for (var index = 0; index < count; index++)
        {
            var action = person.ActionQueue[(person.ActionQueueCurrent + index) & 7];
            if (action.Sequence == sequence)
            {
                removed = true;
                if (action.Owner is not null)
                    ActionQueueRemovalNotices.Add(new ActionQueueRemovalNotice(person.RequestKey, 0));
            }
            else survivors.Add(action.Copy());
        }

        if (!removed) return false;
        for (var index = 0; index < person.ActionQueue.Length; index++)
            person.ActionQueue[index] = ActionQueueSlot.EmptyRecord();
        for (var index = 0; index < survivors.Count; index++)
            person.ActionQueue[index] = survivors[index];
        person.ActionQueueCurrent = 0;
        person.ActionQueueEnd = survivors.Count;
        return true;
    }

    public void ApplyStartedInteractionAction(XPerson person, ActionQueueSlot action)
    {
        if (person.CurrentActionRecord is { Owner: not null } current)
            InteractionLifecycle.FinalizeAction(person, current.Sequence);
        var sequence = action.Owner is not null && action.Sequence == 0
            ? unchecked(++_actionQueueSequence)
            : (int?)null;
        person.ApplyStartedActionState(action, sequence);
        person.ActiveActionCancelRequested = false;
    }

    /// <summary>Requests an exit check interrupt at the next animation track boundary for the specified
    /// active action.</summary>
    public bool RequestInteractionExit(XPerson person, int actionSequence)
    {
        if (person.CurrentActionRecord is not { Owner: not null } current || current.Sequence != actionSequence) return false;
        return InteractionLifecycle.TryRequestGracefulExit(person, actionSequence);
    }

    public void ApplyPersonObjectReferenceActionChanges(XPerson person, XObject subject)
    {
        if (person.PreviousActionRecord is { } previous)
        {
            if (ReferenceEquals(previous.Owner, subject)) person.ClearPreviousActionRecord();
            else if (ReferenceEquals(previous.Secondary, subject)) previous.Secondary = null;
        }

        if (person.CurrentActionRecord is { } current)
        {
            if (ReferenceEquals(current.Owner, subject))
                ApplyStartedInteractionAction(person, ActionQueueSlot.EmptyRecord());
            else if (ReferenceEquals(current.Secondary, subject))
                current.Secondary = null;
        }

        if (ReferenceEquals(person.GenericSimsCall02ObjectA54, subject))
            person.SetCurrentActionSecondaryReference(null);
    }

    public static bool TryDequeueInteractionAction(XPerson person, out ActionQueueSlot? action)
    {
        var current = person.ActionQueueCurrent;
        if (unchecked((uint)current) >= unchecked((uint)person.ActionQueueEnd))
        {
            action = null;
            return false;
        }

        action = person.ActionQueue[current & 7].Copy();
        person.ActionQueueCurrent = unchecked(current + 1);
        return true;
    }

    internal float InteractionAttenuation(XPerson person, Interaction interaction)
    {
        var tuning = SimulatorTuning;
        var visitor = person.SelectionWord5CC != 0;
        return interaction.AttenuationCode switch
        {
            0 => interaction.AttenuationValue,
            1 => 0f,
            2 => visitor ? tuning.VisitorLowAttenuation : tuning.FamilyLowAttenuation,
            3 => visitor ? tuning.VisitorModerateAttenuation : tuning.FamilyModerateAttenuation,
            4 => visitor ? tuning.VisitorHighAttenuation : tuning.FamilyHighAttenuation,
            _ => tuning.FallbackAttenuation,
        };
    }

    public T Add<T>(T item) where T : XObject
    {
        var initialTable = item.ObjectTableEntries;
        if (Objects.TryGetValue(item.Id, out var previous))
        {
            if (!ReferenceEquals(previous, item))
                UnplaceObjectFromGridOrSlot(previous);
            WalkOrder[WalkOrder.IndexOf(previous)] = item;
            if (!ReferenceEquals(previous, item))
            {
                ClockManager.Remove(previous);
                SavedExecutionStacks.Remove(item.Id);
            }
        }
        else WalkOrder.Add(item);
        Objects[item.Id] = item;
        var itemGuid = ObjectGuid(item);
        if (itemGuid != 0)
        {
            var definitionAttributeCount = (int)(ushort)item.Definition[(int)ObjdField.NumAttributes];
            if (SharedTypeAttributes.TryGetValue(itemGuid, out var shared))
            {
                if (!ReferenceEquals(shared, item.TypeAttributes))
                {
                    var minLength = Math.Max(definitionAttributeCount, item.TypeAttributes.Length);
                    if (shared.Length < minLength)
                        shared = GetOrCreateTypeAttributes(itemGuid, minLength);
                    if (item.TypeAttributes.Any(value => value != 0) && shared.All(value => value == 0))
                        item.TypeAttributes.CopyTo(shared, 0);
                    item.TypeAttributes = shared;
                }
            }
            else if (item.TypeAttributes.Length > 0)
            {
                SharedTypeAttributes[itemGuid] = item.TypeAttributes;
            }
            else
            {
                item.TypeAttributes = GetOrCreateTypeAttributes(itemGuid, definitionAttributeCount > 0 ? definitionAttributeCount : 8);
            }
        }
        if (item.AssociatedPlacementHelper is null && (item.TypeRecord?.ExtensionFlagsCC & 1) != 0)
        {
            if (item.TypeRecord is { ObjectType: not XPerson.PersonType } typeRecord)
                typeRecord.RuntimeExtension88Initialized = true;
            item.AssociatedPlacementHelper = new AssociatedPlacementHelper();
            item.AssociatedPlacementHelper.Initialize(item);
            item.AssociatedPlacementExtensionFlag180 = true;
        }
        if (item is XPerson person) InitializePersonTypeRecord(person);
        if (!ReferenceEquals(previous, item))
        {
            var slot = ClockManager.Register(item);
            while (_objectTableEntries.Count < slot) _objectTableEntries.Add(null);
            _objectTableEntries[slot - 1] = item;
            foreach (var existing in WalkOrder) existing.AttachObjectTable(_objectTableEntries);
            InsertTileRoot(item);
            for (var index = 0; index < initialTable.Count; index++)
                if (initialTable[index] is { } entry && (index >= _objectTableEntries.Count || _objectTableEntries[index] is null))
                {
                    while (_objectTableEntries.Count <= index) _objectTableEntries.Add(null);
                    _objectTableEntries[index] = entry;
                }
            foreach (var existing in WalkOrder) existing.AttachObjectTable(_objectTableEntries);
        }
        return item;
    }

    public bool Remove(short id)
    {
        if (!Objects.Remove(id, out var item)) return false;
        if (Neighborhood.TrackingEnabled && item is XPerson removedPerson) Neighborhood.OnPersonRemoved(this, removedPerson);
        if (item is XPerson person) InteractionLifecycle.FinalizeActor(person);
        else InteractionLifecycle.FinalizeRink(item);
        StopObjectSounds(id);
        UnplaceObjectFromGridOrSlot(item);
        var tableSlot = item.ClockManagerIndex;
        if (tableSlot > 0)
        {
            for (var i = 1; i < item.Slots.Count; i++)
            {
                var childKey = item.Slots[i];
                if (childKey <= 0 || (item.ObjectTableEntry(childKey) ?? ObjectTableEntry(childKey)) is not { } child)
                    continue;
                if (child[ObjectData.ParentId] == tableSlot)
                {
                    child[ObjectData.ParentId] = 0;
                    child[ObjectData.SlotHierNumber] = -1;
                }
            }
        }
        WalkOrder.Remove(item);
        WorldLotListEntries.RemoveAll(entry => ReferenceEquals(entry, item));
        ClockManager.Remove(item);
        if (tableSlot > 0 && tableSlot <= _objectTableEntries.Count && ReferenceEquals(_objectTableEntries[tableSlot - 1], item))
            _objectTableEntries[tableSlot - 1] = null;
        SavedExecutionStacks.Remove(id);
        return true;
    }

    public bool KillObject(short id)
    {
        if (ObjectById(id) is not { } target) return false;
        if ((target.MultiTileMasterId != 0 || target.MultiTileNextId != 0) && !HasFunction(target, 8))
        {
            var members = MultiTileMembers(target).ToArray();
            foreach (var member in members)
                member.Flags = (member.Flags & ~0x40u) | 0x40u;
            var removedAny = false;
            foreach (var member in members)
            {
                DispatchNotice(0x87, member.Id);
                NotifyPersonObjectReferenceRecipients(member);
                if (member is XPerson killedMemberPerson) ResetPersonForChangedCode(killedMemberPerson);
                DetachFromFootprintSlot(member);
                if (!Remove(member.Id)) continue;
                DispatchNotice(0x88, member.Id);
                removedAny = true;
            }
            return removedAny;
        }
        DispatchNotice(0x87, id);
        target.Flags = (target.Flags & ~0x40u) | 0x40u;
        NotifyPersonObjectReferenceRecipients(target);
        if (target is XPerson killedPerson) ResetPersonForChangedCode(killedPerson);
        DetachFromFootprintSlot(target);
        StopObjectSounds(id);
        if (!Remove(id)) return false;
        DispatchNotice(0x88, id);
        return true;
    }

    public int RoomArea(short roomId)
    {
        var uRoom = unchecked((ushort)roomId);
        var outside = RoomIsOutside(roomId);
        foreach (var map in LotRoomMaps.Values)
        {
            var area = map.RoomHalfArea(uRoom, outside);
            if (area > 0) return area;
        }
        return 0;
    }

    public bool FacingClear(int tileX, int tileY, int level, int facing) =>
        !LotRoomMaps.TryGetValue(level, out var roomMap) ||
        !BlocksRouteDirectionAt(roomMap, tileX, tileY, facing);

    public InteractionBehaviorActionList? GetInteractionBehaviorActions(XObject me, int listIndex) =>
        me is XPerson person ? WardrobeSuitService.GetActionList(this, person, null, listIndex) : null;

    internal ISimulationHost RequireHost() => Host ?? throw new InvalidOperationException(
        CurrentExecutionLocation is { } location
            ? $"This variable needs a simulation host ({location})."
            : "This variable needs a simulation host.");

    internal ISimulationHost RequireHost(string operation) => Host ?? throw new InvalidOperationException(
        CurrentExecutionLocation is { } location
            ? $"This operation needs a simulation host for {operation} ({location})."
            : $"This operation needs a simulation host for {operation}.");

    internal ISimulationCareerHost RequireCareerHost(string operation) =>
        CareerHost ?? (ISimulationCareerHost?)Host ?? throw new InvalidOperationException(
            CurrentExecutionLocation is { } location
                ? $"This operation needs career data for {operation} ({location})."
                : $"This operation needs career data for {operation}.");

    internal ISimulationAutonomyHost RequireAutonomyHost() => AutonomyHost ?? Host ?? throw new InvalidOperationException(
        CurrentExecutionLocation is { } location
            ? $"This autonomy service needs a simulation host ({location})."
            : "This autonomy service needs a simulation host.");

    internal IFamilyBudget RequireFamilyBudget() => FamilyBudget ?? throw new InvalidOperationException("This primitive needs the family budget.");

    /// <summary>Optional UIText.iff override for localized dialog strings, falling back to
    /// <c>ObjectRegistry.UiText</c>.</summary>
    public SimsEngine.Formats.IffFile? UiTextFile { get; set; }

    /// <summary>Reads a localized string entry from the specified UIText.iff string table for the active
    /// <see cref="Language"/>.</summary>
    public string? UiTextString(ushort tableId, int index)
    {
        if (UiTextFile?.Find(SimsEngine.Formats.ResourceType.Str, tableId) is { } chunk)
        {
            var strings = SimsEngine.Formats.StringTable.Read(UiTextFile, chunk).ForLanguage(Language);
            if (index >= 0 && index < strings.Count && strings[index].Length > 0) return strings[index];
        }
        return ObjectRegistry?.UiTextString(tableId, index, Language);
    }

    /// <summary>The lot variable words read and written by the Dialog primitive (see
    /// <see cref="DialogWords"/>).</summary>
    public DialogWords Dialog { get; } = new();

    public List<SimulationDialogRecord> DialogRecords { get; } = [];
    private long _dialogSequence;
    private int? _dialogOwnedGenericSimsCall03PrimaryHandle;

    public void SyncGenericSimsCall03DialogHandle()
    {
        if (Host is not null) return;
        if (HasVisibleDialog)
        {
            if (GenericSimsCall03State.PrimaryHandle == 0)
            {
                var handle = unchecked((int)Math.Max(1L, _dialogSequence));
                GenericSimsCall03State.PrimaryHandle = handle;
                if (GenericSimsCall03State.LinkedGlobalWord60 is null or 0)
                    GenericSimsCall03State.LinkedGlobalWord60 = 0xf;
                _dialogOwnedGenericSimsCall03PrimaryHandle = handle;
            }
            return;
        }
        if (_dialogOwnedGenericSimsCall03PrimaryHandle is { } owned &&
            GenericSimsCall03State.PrimaryHandle == owned)
        {
            GenericSimsCall03State.PrimaryHandle = 0;
            if (GenericSimsCall03State.LinkedGlobalWord60 == 0xf)
                GenericSimsCall03State.LinkedGlobalWord60 = 0;
            _dialogOwnedGenericSimsCall03PrimaryHandle = null;
        }
    }

    public void DismissVisibleDialogRecords()
    {
        for (var i = DialogRecords.Count - 1; i >= 0; i--)
        {
            var record = DialogRecords[i];
            if (!record.Visible) continue;
            record.State = record.AwaitsResponse ? 2 : 5;
            record.Visible = false;
            if (!record.AwaitsResponse)
                DialogRecords.RemoveAt(i);
        }
        if (_dialogOwnedGenericSimsCall03PrimaryHandle is { } owned &&
            GenericSimsCall03State.PrimaryHandle == owned)
        {
            GenericSimsCall03State.PrimaryHandle = 0;
            if (GenericSimsCall03State.LinkedGlobalWord60 == 0xf)
                GenericSimsCall03State.LinkedGlobalWord60 = 0;
        }
        _dialogOwnedGenericSimsCall03PrimaryHandle = null;
        ShowNextDialogRecord();
        SyncGenericSimsCall03DialogHandle();
    }

    public SimulationDialogRecord? DialogRecord(XObject owner) =>
        DialogRecords.FirstOrDefault(record => ReferenceEquals(record.Owner, owner));

    public int? DialogRecordState(XObject owner) => DialogRecord(owner)?.State;

    public bool HasVisibleBlockingDialog => DialogRecords.Any(record => record.Visible && record.Blocking && record.State < 2);
    public bool HasVisibleDialog => DialogRecords.Any(record => record.Visible);

    public SimulationDialogRecord? CreateMessageDialog(XObject owner, XObject? icon, string title, string message,
        string? yes, string? no, string? cancel, byte kind = 0, bool blocking = true, bool awaitsResponse = true)
    {
        // Specialized dialog records handle non-generic prompts such as travel, wardrobe or pet selection,
        // phone books, and transformations beyond standard STR# 301 dialogs.
        var record = new SimulationDialogRecord($"native-dialog-{++_dialogSequence}", owner, icon, kind, title, message,
            yes, no, cancel, blocking, awaitsResponse)
        {
            Visible = blocking ? !HasVisibleBlockingDialog : !HasVisibleDialog,
            State = !awaitsResponse && !blocking ? 1 : 0,
        };
        DialogRecords.Add(record);
        SyncGenericSimsCall03DialogHandle();
        return record;
    }

    /// <summary>Creates a dialog record for a Spellbook (style 0) or Cookbook (style 1).</summary>
    public SimulationDialogRecord? CreateBookDialog(XPerson owner, byte style)
    {
        if (style > 1) throw new ArgumentOutOfRangeException(nameof(style));
        if (HasVisibleDialog) return null;
        var familyNumber = owner[PersonData.FamilyNumber];
        if (familyNumber == 0 && Neighbors.TryGetValue(owner[PersonData.NeighborId], out var neighbor))
            familyNumber = neighbor.PersonData[(int)PersonData.FamilyNumber];
        var familyName = familyNumber != 0 ? MenuTextSources?.FamilyName(familyNumber) : null;
        if (string.IsNullOrWhiteSpace(familyName))
            familyName = !string.IsNullOrWhiteSpace(owner.Name) ? owner.Name : owner.TypeRecord?.CatalogName ?? owner.TypeRecord?.Name;
        var record = new SimulationDialogRecord(
            $"native-dialog-{++_dialogSequence}", owner, null, style == 0 ? (byte)12 : (byte)15,
            string.IsNullOrWhiteSpace(familyName) ? "Family" : familyName, "", UiTextString(142, 0) ?? "OK", null, null, awaitsResponse: false);
        if (style == 0)
        {
            // Selects book mode 0 for children and mode 1 for adults (age 0 or greater than 17).
            var age = owner[PersonData.PersonAge];
            record.BookMode = (byte)(age == 0 || age > 17 ? 1 : 0);
            record.BookAvailability = QuerySpellBookAvailability(owner, record.BookMode.Value);
        }
        else
        {
            record.BookMode = 2;
            if (owner.TypeRecord?.BehaviorScope is Content.BehaviorScope scope)
            {
                record.HideBookItem9 = !RunMagicMadeQuery(owner, scope, 3, 5);
                record.HideBookItem4 = !RunMagicMadeQuery(owner, scope, 4, 4);
            }
        }
        DialogRecords.Add(record);
        SyncGenericSimsCall03DialogHandle();
        return record;
    }

    /// <summary>Queries spell or recipe availability using Global.iff BHAV 496 for the resource IDs in
    /// MagicMasterSpells.iff STR# 15. Calls the Magic Master Spells controller object (GUID 0xB6C90029) to
    /// determine unlocked state.</summary>
    private IReadOnlyList<SimulationBookAvailability> QuerySpellBookAvailability(XPerson owner, byte mode)
    {
        if (owner.TypeRecord?.BehaviorScope is not Content.BehaviorScope scope) return [];
        var savedTemporaries = owner.Temporaries.ToArray();
        try
        {
            // Preserves the exact numeric IDs, ordering, and placeholder entries from STR# 15.
            var itemIds = MagicMasterSpellItemIds;
            short[] contexts = mode == 1 ? [0] : [0, .. itemIds];
            var result = new SimulationBookAvailability[contexts.Length * itemIds.Count];
            var index = 0;
            foreach (var contextItemId in contexts)
            foreach (var itemId in itemIds)
            {
                var made = TreeRunner.RunWithParameters(this, owner, scope, 496, owner.StackObjectKey,
                    [contextItemId, itemId]);
                result[index++] = new SimulationBookAvailability(contextItemId, itemId, made);
            }
            return result;
        }
        finally
        {
            savedTemporaries.CopyTo(owner.Temporaries, 0);
        }
    }

    private bool RunMagicMadeQuery(XPerson owner, Content.BehaviorScope scope, short contextItemId, short itemId)
    {
        var savedTemporaries = owner.Temporaries.ToArray();
        try
        {
            return TreeRunner.RunWithParameters(this, owner, scope, 496, owner.StackObjectKey,
                [contextItemId, itemId]);
        }
        finally
        {
            savedTemporaries.CopyTo(owner.Temporaries, 0);
        }
    }

    public void DismissDialogRecord(XObject owner)
    {
        var record = DialogRecord(owner);
        if (record is null) return;
        record.State = 5;
        record.Visible = false;
        ShowNextDialogRecord();
        SyncGenericSimsCall03DialogHandle();
    }

    public void RespondMessageDialog(string token, byte code, string? responseText = null)
    {
        var record = DialogRecords.FirstOrDefault(item => item.Token == token && item.Visible)
            ?? throw new ArgumentException("Dialog is no longer active.");
        if (record.Kind is 12 or 15 && record.BookMode is not null)
        {
            if (code != 0) throw new ArgumentException("Book dialog response code must be 0.");
            record.State = 2;
            record.Visible = false;
            if (!record.AwaitsResponse) DialogRecords.Remove(record);
            ShowNextDialogRecord();
            SyncGenericSimsCall03DialogHandle();
            return;
        }
        if (record.Kind == 2 || record.Kind == 14)
        {
            var max = record.Kind == 2 ? 2 : 1;
            if (code > max) throw new ArgumentException("Invalid native dialog response.");
            record.State = code switch { 0 => 2, 1 => 3, _ => 4 };
        }
        else if (record.Kind == 1)
        {
            if (code > 1) throw new ArgumentException("Invalid native dialog response.");
            record.State = code == 0 ? 2 : 3;
        }
        else if (record.Kind is 6 or 9)
        {
            if (code > 1) throw new ArgumentException("Invalid native selection response.");
            short selection = -1;
            if (code == 1 && (responseText is null || !short.TryParse(responseText.AsSpan(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out selection) || selection < 0))
                throw new ArgumentException("A selected wardrobe or pet choice requires a nonnegative index.");
            if (record.Kind == 6)
            {
                Dialog[0xf4] = code == 1 ? (short)-2 : (short)0;
                Dialog[0xf6] = (short)selection;
            }
            else
            {
                Dialog[0x100] = (short)selection;
                Dialog[0x108] = code == 1 ? (short)-2 : (short)0;
            }
            record.State = code == 1 ? 2 : 4;
        }
        else if (record.Kind == 10)
        {
            if (code > 1 || record.Owner is not XPerson caller ||
                !short.TryParse(responseText.AsSpan(), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var neighborId))
                throw new ArgumentException("Invalid native phone-book response.");
            if (code == 1)
            {
                if (neighborId <= 0 || !Neighbors.TryGetValue(neighborId, out var neighbor) ||
                    !Neighbors.TryGetValue(caller[PersonData.NeighborId], out var callerNeighbor) ||
                    callerNeighbor.Relationships.Count(neighborId) == 0)
                    throw new ArgumentException("The selected phone-book contact is no longer available.");
                Dialog[0xe8] = neighborId;
            }
            else
            {
                if (neighborId != -1) throw new ArgumentException("Phone-book cancellation must use -1.");
                Dialog[0xe8] = -9999;
            }
            record.State = code == 1 ? 2 : 3;
        }
        else if (record.Kind is 5 or 7 or 8 or 11 or 13)
        {
            if (code > 1 || !short.TryParse(responseText.AsSpan(), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var destination) ||
                (code == 0 && destination != -1) || (code == 1 && destination is < 1 or > 100))
                throw new ArgumentException("Invalid native travel destination response.");
            // Maps destination dialog kinds to lot variable words for vacation, downtown, studio town,
            // community, and magic town destinations.
            var offset = record.Kind switch { 5 => 0xea, 7 => 0xec, 8 => 0xf2, 11 => 0xee, _ => 0xf0 };
            Dialog[offset] = destination;
            record.State = code == 1 ? 2 : 3;
        }
        else
        {
            if ((record.Kind is 0 or 3 or 4 or 12 or 15) && code != 0)
                throw new ArgumentException("This native Dialog type accepts only its first button.");
            if (record.Kind == 3 && responseText is { Length: > 64 })
                throw new ArgumentException("Text-entry response exceeds 64 characters.");
            if (record.Kind == 3 && responseText is not null && responseText.Any(character => character == '\0' || character is '\r' or '\n'))
                throw new ArgumentException("Text-entry response contains an unsupported character.");
            record.State = code == 0 ? 2 : code == 1 ? 3 : 4;
        }
        record.ResponseText = responseText;
        record.Visible = false;
        if (!record.AwaitsResponse || record.Kind is 5 or 6 or 7 or 8 or 9 or 10 or 11 or 13)
            DialogRecords.Remove(record);
        ShowNextDialogRecord();
        SyncGenericSimsCall03DialogHandle();
    }

    public void CompleteDialogRecord(SimulationDialogRecord record)
    {
        DialogRecords.Remove(record);
        ShowNextDialogRecord();
        SyncGenericSimsCall03DialogHandle();
    }

    private void ShowNextDialogRecord()
    {
        if (!HasVisibleBlockingDialog)
        {
            var nextBlocking = DialogRecords.FirstOrDefault(record => !record.Visible && record.Blocking && record.State < 2);
            if (nextBlocking is not null)
            {
                nextBlocking.Visible = true;
                return;
            }
        }
        if (HasVisibleDialog) return;
        var next = DialogRecords.FirstOrDefault(record => !record.Visible && record.State < 2);
        if (next is not null) next.Visible = true;
    }

    public XObject? TutorialOwner { get; set; }

    /// <summary>Tracks execution state across ticks for primitives that suspend execution, clearing the
    /// entry when the primitive completes or branches.</summary>
    public Dictionary<StackFrame, int> PrimitiveTicks { get; } = [];
}

public readonly record struct ThreadFrameError(short Tree, int Instruction, ushort? Opcode, short StackObject,
    short[] Parameters, short[] Locals, short CodeOwner, string? TreeLabel = null, int? TableObjectKey = null,
    string? ResourceScope = null, byte? TrueTarget = null, byte? FalseTarget = null, string? Operand = null,
    short? ResolvedStackObjectId = null, int? ResolvedStackObjectTypeKey = null, bool? ResolvedStackObjectIsPerson = null);

public readonly record struct AutonomyAttemptDiagnostic(short PersonId, bool Forced, bool GatePassed,
    int CandidatesVisited, int InteractionRecords, int PassedRecords, int ZeroAdvertisementRecords,
    int BelowThresholdRecords, int ScoredRecords, float? MaximumScore, float? MaximumScoreBaseline,
    float? MaximumScoreAdvertisement, short? MaximumScoreObjectId, short? MaximumScoreAction,
    MotiveAdvertisement[]? MaximumScoreMotives, float? MaximumAbsoluteMotiveDelta, short? MaximumDeltaObjectId,
    short? MaximumDeltaAction, MotiveAdvertisement[]? MaximumDeltaMotives, int? MaximumRawAdvertisement,
    short? MaximumRawAdvertisementObjectId, short? MaximumRawAdvertisementAction, short? ChosenObjectId, short? Action);

public readonly record struct SimulationTraceEntry(short ObjectId, short Tree, int Instruction, ushort Opcode,
    string Operand, short StackObject, short[] ActorTemporaries, short[] Parameters, short[] Locals);

public readonly record struct SimulationPrimitiveResultTrace(short ObjectId, short Tree, int Instruction, ushort Opcode,
    PrimitiveResult Result, short[] Parameters, short CodeOwner = 0);

public readonly record struct SimulationRouteCandidateTrace(short ActorId, short AnchorId, ObjectPosition Position,
    ushort Room, int Direction, bool PlacementAccepted, int PlacementFailureCode, short BlockingObjectId, bool Selected,
    string FailureReason);

public readonly record struct SimulationNativeRouteTrace(short ActorId, short AnchorId, ObjectPosition Destination,
    string Phase, string? Failure);

public readonly record struct SimulationRouteSearchTrace(short ActorId, ObjectPosition Destination,
    (int X, int Y) StartTile, (int X, int Y) EndTile, ushort StartRoom, ushort EndRoom, int VisitedTiles,
    int RejectedPlacements, int RejectedEdges, string VisitedRooms);

public readonly record struct SimulationTreeCallTrace(short ObjectId, short CallerTree, int Instruction, short CalleeTree,
    short[]? Parameters);

public readonly record struct SimulationNamedTreeLookupTrace(short ObjectId, short StackObjectId, short CallerTree,
    int Instruction, short StringTable, byte StringNumber, string? Name, int? ScopeKey, short ResolvedTree);

public readonly record struct SimulationInteractionTestTrace(short PersonId, short Tree, int Instruction, byte Mode,
    short StackObjectKey, short[] InteractingObjectIds, short[] InteractingStackKeys, bool Result);

public readonly record struct ThreadError(short ObjectId, string Message, short[]? ActorTemporaries = null,
    ThreadFrameError[]? Frames = null);

public readonly record struct InteractionGuardError(short PersonId, short ObjectId, int InteractionId,
    int GuardTree, SimulationError Error, string Message);
