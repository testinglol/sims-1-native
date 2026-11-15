using SimsEngine.Formats;
using SimsEngine.Content;
using SimsEngine.Character;
using System.Numerics;

namespace SimsEngine.Simulation;

/// <summary>An object's position in the native lot axes: X is Sims.exe cXObject +0xf8, Y is +0xf4, and Height is
/// +0x110 (ObjectHead's saved order). FUN_004341ed scales the horizontal squared difference by 1/256. Height is the
/// level: FUN_004341ed scales its difference by 20 (the field's name is inferred from that use, and the units per
/// tile are not traced).</summary>
public readonly record struct ObjectPosition(int X, int Y, int Height);
public readonly record struct EditorPlacementTile(int X, int Y, int Level);
public sealed record EditorPlacementResult(bool Valid, string Status, IReadOnlyList<EditorPlacementTile> Tiles, XObject? ReferenceObject = null, int ReferenceSlot = -1);

/// <summary>The placement-only projection of a temporary cXCursorObject chain created by FUN_00421619. Each record
/// points back to its source lot object; cursor flags and association links belong to the temporary copy, not that
/// source object. This keeps cursor state from leaking into later SimAntics placements.</summary>
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

    /// <summary>FUN_00421619 copies the source's linked multi-tile chain, and FUN_004219b9 marks each cXMT copy.</summary>
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

/// <summary>The transformed three-byte coordinate passed by FUN_00463648 to each FUN_00476b8f listener.
/// First and Second retain the source buffer's byte order because their consumer semantics are not established.</summary>
public readonly record struct PortalWallRefreshCoordinate(byte First, byte Second, byte Level);

/// <summary>A pending source Dialog-primitive message record. State 0 waits for the UI; states 2–4 are original
/// completion states written by the dialog manager after a response; state 5 is dismissed (FUN_004370fd).</summary>
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
    /// <summary>Original spellbook detail state from Global BHAV 496, keyed by the resource id stored on each
    /// MagicMasterSpells list row. Null for other dialog kinds.</summary>
    public IReadOnlyList<SimulationBookAvailability>? BookAvailability { get; internal set; }
    /// <summary>FUN_005109fe's book mode: 0 child spellbook, 1 adult spellbook, 2 cookbook.</summary>
    public byte? BookMode { get; set; }
    /// <summary>FUN_004ec793's mode-2 row exclusions, initialized by the two FUN_005117ea probes.</summary>
    public bool? HideBookItem9 { get; internal set; }
    public bool? HideBookItem4 { get; internal set; }
    public Formats.IffFile? ImageFile { get; set; }
    public ushort ImageBitmapId { get; set; }
}

public readonly record struct SimulationBookAvailability(short ContextItemId, short ItemId, bool Made);

/// <summary>The optional 0x138-byte object extension created by FUN_0043779b. Only the fields changed by its
/// vtable +0x24 callback (FUN_00585039/FUN_00584ff1) are represented here.</summary>
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

/// <summary>The optional 0x6c-byte helper object allocated for DAT_006DE804 by FUN_00405376/FUN_004053C4.
/// Its internal callback/list layout is not yet fully decoded, so keep it distinct from cXObject.</summary>
public sealed class GenericSimsCallRecordTarget
{
    public IReadOnlyList<int> FameStarThresholds { get; init; } = [];
    /// <summary>Six skill-threshold rows, in FUN_00404cef order: cooking, mechanical, charisma, body, logic, creativity.</summary>
    public IReadOnlyList<IReadOnlyList<int>> FameSkillThresholds { get; init; } = [];
    /// <summary>Friend-star-power thresholds read by FUN_004051e0 at the current Fame Star Power index.</summary>
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

/// <summary>A candidate that passed the source-defined Fame score and skill gates in FUN_00404cef.</summary>
public sealed record FamePromotionCandidate(short[] PersonData, short CurrentStarPower, int FriendPowerThreshold);

/// <summary>A diagnostic record for a successful Create New Object Instance primitive (Sims.exe FUN_0044e108).</summary>
public sealed record ObjectCreationDiagnostic(short SourceObjectId, short CreatedObjectId, int GuidKey, byte Mode, byte Flags, string? Location,
    ObjectPosition? RequestedPosition = null, short RelativeObjectId = 0, bool? PlacementAccepted = null);
/// <summary>A diagnostic record for a Remove Object Instance primitive (Sims.exe FUN_00451baa).</summary>
public sealed record ObjectRemovalDiagnostic(short SourceObjectId, short SelectedObjectId, short EffectiveTargetId, string? Location);

/// <summary>The four values at cXObject +0x100 in Sims.exe order: max X, max Y, min X, min Y.</summary>
public readonly record struct FootprintBounds(int MaxX, int MaxY, int MinX, int MinY);

/// <summary>The lot manager's +0x70/+0x74/+0x78/+0x7c coordinate bounds in X-min, X-max, Y-min, Y-max order.</summary>
public readonly record struct LotCoordinateBounds(int MinX, int MaxX, int MinY, int MaxY);

/// <summary>Source-backed type-0 SLOT data used to build one entry of cXObject +0x130. Fields are inferred from
/// FUN_0046dfd7's copies; FUN_0046e014 normalizes Height from HeightCode (the default comparison value is 0.0 in Sims.exe).</summary>
public sealed class ObjectFootprintSlot
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Height { get; init; }
    /// <summary>Sims.exe cXObject +0x130 record +0x10: the one-based occupant manager key written by FUN_004327d9.</summary>
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

/// <summary>An object instance on the lot, the part of Sims.exe's cXObject the tree variables reach: its
/// attributes (+0x2c, count at +0x30), eight temporaries (+0x3a), object data (+0x4a, 0x48 fields, named by
/// <see cref="ObjectData"/>), its definition (+0x118, 0x6c fields; field 9 "type" is 2 for a person), its slots
/// (+0x130, 0x24-byte entries; entry 0 is not a slot), its dynamic sprite flags (+0x34, count at +0x38) and its type
/// attributes.</summary>
public class XObject(short id, short[] definition, int attributes = 0)
{
    public const int DataCount = 0x48;
    public const int DefinitionCount = 0x6c;

    /// <summary>The lot object's stable identity; used as the managed counterpart of cXObject +4 where direct REA
    /// analysis observes that raw word being stored in Manage Inventory token keys.</summary>
    public short Id { get; } = id;
    /// <summary>The Sims.exe runtime class selected by FUN_004409db; routing's portal manager contains live cXPortal instances.</summary>
    public Formats.ObjectClass? RuntimeClass { get; set; }
    /// <summary>The one-based runtime manager slot at cXObject +0xf0, assigned by Sims.exe FUN_0044350d.</summary>
    public short ClockManagerIndex { get; internal set; }
    public short[] Definition { get; } = definition.Length == DefinitionCount ? definition : throw new ArgumentException($"A definition has {DefinitionCount} fields.", nameof(definition));
    public short[] Attributes { get; set; } = new short[attributes];
    public short[] Temporaries { get; } = new short[8];
    public short[] Data { get; } = CreateData();
    /// <summary>Instance-assigned name entered through a type-3 Dialog primitive; null uses the type record's catalog name.</summary>
    public string? Name { get; set; }
    /// <summary>Character words corresponding to cXObject's MyList; a dynamic-string token consumer is not recovered.</summary>
    public List<short> MyList { get; } = [];
    /// <summary>Sims.exe cXObject +0x44: FUN_004403c5 sets this after a placement changes the saved position.</summary>
    public bool PositionRefreshPending { get; internal set; }
    /// <summary>Sims.exe FUN_00463da6 returns _DAT_0066657c (0.0 in analysis/Sims.original.exe) for an
    /// unallocated route index.</summary>
    public const float UnallocatedPortalRouteCost = 0f;

    /// <summary>FUN_004639d2 fills cXPortal's allocated +0x1ac route-cost vector with 10001.0f; route
    /// initialization and vector growth use this same uninitialized-cost sentinel.</summary>
    public const float UninitializedPortalRouteCost = 10001f;
    public Dictionary<short, float> PortalRouteCosts { get; } = [];

    /// <summary>Sims.exe FUN_00463da6 returns _DAT_0066657c for a nonpositive or unallocated route index.</summary>
    public float PortalRouteCostAt(short routeId) =>
        routeId > 0 && PortalRouteCosts.TryGetValue(routeId, out var cost) ? cost : UnallocatedPortalRouteCost;

    /// <summary>Sims.exe FUN_00463155 grows the route-cost vector through the requested 1-based index, filling new
    /// entries with the uninitialized sentinel before writing the requested value.</summary>
    public void SetPortalRouteCost(short routeId, float cost)
    {
        if (routeId <= 0) throw new ArgumentOutOfRangeException(nameof(routeId));
        for (short index = 1; index <= routeId; index++)
            if (!PortalRouteCosts.ContainsKey(index)) PortalRouteCosts[index] = UninitializedPortalRouteCost;
        PortalRouteCosts[routeId] = cost;
    }

    /// <summary>FUN_004639d2 resets every allocated route-cost entry to the source's uninitialized sentinel.</summary>
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

    /// <summary>The Sleep gate's raw word at cXObject +0x7c, aliased to object Data[25] (Lockout Count), as are all
    /// writes and reads to that object-data slot.</summary>
    public short SleepGateWord7C
    {
        get => Data[(int)ObjectData.LockoutCount];
        set => Data[(int)ObjectData.LockoutCount] = value;
    }
    /// <summary>The short at cXObject +0x24, used by Sims.exe's state-class predicates. Its gameplay name is not recovered.</summary>
    public short StateValue { get; set; }
    /// <summary>The short at cXObject +0x5cc, tested by FUN_004E99D7.</summary>
    public virtual short SelectionWord5CC { get; set; }
    /// <summary>The short at cXObject +0x606, compared with the current stored slot by FUN_004E99D7.</summary>
    public short SelectionWord606 { get; set; }
    /// <summary>The raw dword at cXObject +0x10c, matched by FUN_00425DB3 in the interaction lookup vector.</summary>
    public int TableWord10C { get; set; }
    /// <summary>The source byte at cXObject +0x14c gates Sims.exe FUN_00410b89.</summary>
    public bool LotUpkeepGateByte14C { get; set; }
    /// <summary>The pointer-presence gate at cXObject +0x128 for Sims.exe FUN_00410b89.</summary>
    public bool LotUpkeepPointer128Present { get; set; }
    /// <summary>The six signed words at cXObject +0x14e..+0x158 copied as lower bounds into the matching
    /// Magic Master Spells object's attribute vector by FUN_00410b89.</summary>
    public short[] LotUpkeepMinimumResources { get; } = new short[6];
    /// <summary>The raw dword read at cXObject +0x13c by GenericSimsCall 0x1b. Its meaning/overlap with the routing-slot
    /// vector's native layout is unresolved, so this source-specific view is kept separate.</summary>
    public int GenericSimsCall1BRawWord13C { get; set; }
    /// <summary>The pointer interpretation of cXObject +0x10c when this object is DAT_006E4F10, written by
    /// GenericSimsCall 0x23. Kept separate from the integer lookup view because the same offset has typed uses.</summary>
    public XPerson? StoredPersonPointer10C { get; set; }
    /// <summary>The room word at cXObject +0x84, which is also object-data field 29 (the data array starts at +0x4a).
    /// FUN_00431b5e initializes it to -5; FUN_00434353 restores all 0x48 data words on saved lots from version 0x2f,
    /// including this word; FUN_00433ef5 recalculates it after a position change.</summary>
    public short RoomWord84 { get => Data[(int)ObjectData.Room]; set => Data[(int)ObjectData.Room] = value; }
    /// <summary>The short at cXObject +0x58 that gates FUN_00433ef5's pair of 0xf3 room-change notices.</summary>
    public short RoomChangeNoticeGateWord58 { get; set; }
    /// <summary>The raw short at cXObject +0x14, set to -1 by Sims.exe FUN_004331d4 before an object is queued for removal.</summary>
    public short RemovalCallbackWord14 { get; set; }
    /// <summary>The raw dwords at cXObject +0x118/+0x11c used by FUN_0042730F when the special-item gate is false.</summary>
    public int ObjectValueWord118 { get; set; }
    public int ObjectValueWord11C { get; set; }
    /// <summary>The raw dword at +0x8 of a lot-manager neighbor-table entry, returned by FUN_00425b48.</summary>
    public int NeighborTableWord08 { get; set; }
    /// <summary>The signed word at cXObject +0xee, read by GenericSimsCall 0x04/0x21 before FUN_00426068.</summary>
    public short NeighborInteractionTargetWordEE { get; set; }
    /// <summary>The signed word at cXObject +0xf6, tested against 2 by Sims.exe FUN_00427c21.</summary>
    public short NeighborInteractionWordF6 { get; set; }
    /// <summary>The cached count dword at cXObject +0x120, returned by FUN_00427263.</summary>
    public int NeighborInteractionCountWord120 { get; set; }
    /// <summary>The short at cXObject +0xfa, cleared by FUN_00425fb0 after a successful neighbor link.</summary>
    public short NeighborInteractionWordFA { get; set; }
    /// <summary>The short at cXObject +0x612, cleared by FUN_00425fb0 on the resolved person record.</summary>
    public short NeighborInteractionWord612 { get; set; }
    /// <summary>The dword at cXObject +0x138, whose bit 3 is cleared by FUN_00426068/FUN_00425fb0.</summary>
    public uint NeighborInteractionFlagsWord138 { get; set; }
    /// <summary>The byte at cXObject +0x124 set by FUN_00426068/FUN_00425fb0.</summary>
    public byte NeighborInteractionStateByte124 { get; set; }
    /// <summary>The ordered pointer vector at cXObject +0x12c/+0x130 used by FUN_00410285/FUN_00410263.
    /// Entries point to another object's +4 subobject; this model keeps the owning object for identity comparisons.</summary>
    private readonly List<XObject> _neighborSubobjectReferences = [];
    public IReadOnlyList<XObject> NeighborSubobjectReferences => _neighborSubobjectReferences;

    public bool HasNeighborSubobjectReference(XObject neighbor) =>
        _neighborSubobjectReferences.Any(candidate => ReferenceEquals(candidate, neighbor));

    /// <summary>FUN_004108DC appends the neighbor's +4 pointer only when the ordered vector does not already contain it.</summary>
    public bool AddNeighborSubobjectReference(XObject neighbor)
    {
        if (HasNeighborSubobjectReference(neighbor)) return false;
        _neighborSubobjectReferences.Add(neighbor);
        return true;
    }

    /// <summary>FUN_00410917 removes the first matching +4 subobject pointer while preserving the other entries' order.</summary>
    public bool RemoveNeighborSubobjectReference(XObject neighbor)
    {
        var index = _neighborSubobjectReferences.FindIndex(candidate => ReferenceEquals(candidate, neighbor));
        if (index < 0) return false;
        _neighborSubobjectReferences.RemoveAt(index);
        return true;
    }
    /// <summary>The one-based object-reference vector at cXObject +0xdc (FUN_00407498). Index 0 is not an entry.</summary>
    private List<XObject?> _objectTableEntries = [];
    /// <summary>The running object's +0xdc is the lot object manager, shared by every instance; the manager's
    /// +0x18/+0x1c pointer vector is the one-based table read by FUN_00407498.</summary>
    public List<XObject?> ObjectTableEntries => _objectTableEntries;
    /// <summary>Words in the effect table at +0x7c of this object's +0xdc lot table, addressed by FUN_0046dae0.</summary>
    public Dictionary<short, short> ObjectTableEffectWords { get; } = [];
    /// <summary>The four signed coordinates at cXObject +0x100, recomputed by FUN_00433dbb.</summary>
    public FootprintBounds FootprintBounds { get; set; }
    /// <summary>The direction for which the currently retained bounds were saved or last recomputed. This cache
    /// lets movement translate a known footprint only while its orientation is unchanged.</summary>
    public short? FootprintBoundsDirection { get; internal set; }
    /// <summary>The ordered list at +0x38/+0x3c of this object's lot-table context, searched and compacted by
    /// FUN_00443c37 for GenericSimsCall 0x0b. Entries are object references; removal matches pointer identity.</summary>
    public List<XObject> LotListEntries { get; } = [];
    /// <summary>The live object flag word at cXObject +0x114, including bit 0x1000 that Remove Object Instance updates.</summary>
    public uint Flags { get; set; }
    /// <summary>FUN_00439b54's source-backed object-error prelude: if +0x114 bit 16 is set, clear bit 8
    /// (the low bit of byte +0x115) before the error UI/callback path. Returns whether that precondition held.</summary>
    public bool PrepareClockSpecialUpdate()
    {
        if ((Flags & 0x00010000u) == 0) return false;
        Flags &= ~0x00000100u;
        return true;
    }
    /// <summary>The decoded stack frames preserved from this object's ObjM block. Save writes serialize this list,
    /// so contained scheduler errors must clear it when dropping the corresponding live execution stack.</summary>
    public List<ObjectStackFrame> SavedStack { get; } = [];
    /// <summary>The allocation size stored beside the saved object stack (FUN_00475130); preserved for parity and diagnostics.</summary>
    public int SavedStackSize { get; set; }
    /// <summary>The decoded multi-tile/portal tail of this object's ObjM state, when it has one.</summary>
    public MultiTileState? SavedMultiTileState { get; set; }
    /// <summary>cXMTObject +0x1b4/+0x1b8/+0x1bc, cached after the last group move by FUN_00421ea5.</summary>
    public ObjectPosition? MultiTileMovePositionState { get; internal set; }
    /// <summary>cXMTObject +0x1b0, copied from the lead after movement; the no-associated-object path sets it to 1.</summary>
    public short MultiTileMoveFlag1B0 { get; internal set; }
    /// <summary>Raw cXMTObject words at +0x1c4/+0x1c6, cleared when FUN_00421ea5 receives a changed position.
    /// FUN_004333ef forwards the original +0x58 request slot argument, which FUN_00421ea5 stores at +0x1c6 when
    /// the request position is unchanged.</summary>
    public short MultiTileMoveValue1C4 { get; internal set; }
    public short MultiTileMoveValue1C6 { get; internal set; }
    /// <summary>The optional object extension pointer at cXObject +0x120, created by FUN_0043779b when
    /// TypeRecord.ExtensionFlagsCC bit 0 is enabled.</summary>
    public AssociatedPlacementHelper? AssociatedPlacementHelper { get; internal set; }
    /// <summary>The byte at cXObject +0x180 set when FUN_0043779b creates the +0x120 extension.</summary>
    public bool AssociatedPlacementExtensionFlag180 { get; internal set; }
    /// <summary>The runtime association pointer at cXMTObject +0x1ac, consulted through FUN_00422083 before
    /// optional associated-object callbacks. Its presence alone does not replace the ordinary +0x190 group move.</summary>
    public XObject? MultiTileMoveAssociatedObject { get; set; }
    /// <summary>cXMTObject +0x1c0, reset to -1.0 after FUN_00421ea5 moves its chain.</summary>
    public float MultiTileMoveValue1C0 { get; internal set; }
    /// <summary>The lead object's id stored at cXMTObject +0x190; zero means this multi-tile object has no linked lead.</summary>
    public short MultiTileMasterId { get; set; }
    /// <summary>The interaction-executor scope key at cXObject +0x5ca, passed to Sims.exe FUN_0042a8e3.</summary>
    public short InteractionScopeKey5CA { get; set; }
    /// <summary>The next multi-tile object id at cXMTObject +0x18c; zero terminates the group's ordered chain.</summary>
    public short MultiTileNextId { get; set; }
    private short _requestKey;
    /// <summary>The raw signed word at cXObject +0xf0. Sims.exe uses it as the stack-object key written by the
    /// vtable +0x34 functional-tree push (FUN_00452bd8) and as the person key in GenericSimsCall 0x27/0x28.</summary>
    public short RequestKey
    {
        get => _requestKey;
        set
        {
            _requestKey = value;
            HasRequestKey = true;
        }
    }
    /// <summary>Whether the source object's +0xf0 key was loaded. This distinguishes a real zero from an omitted field
    /// in hand-built fixtures.</summary>
    public bool HasRequestKey { get; private set; }
    /// <summary>The value written into a frame when this object is pushed as its stack object.</summary>
    public short StackObjectKey => HasRequestKey ? RequestKey : Id;
    /// <summary>Reads cXObject +0xdc by its one-based index (FUN_00407498); nonpositive and out-of-range indexes miss.</summary>
    public XObject? ObjectTableEntry(short index) =>
        index > 0 && index <= ObjectTableEntries.Count ? ObjectTableEntries[index - 1] : null;

    /// <summary>Sets one-based cXObject +0xdc entry <paramref name="index"/>, growing intervening entries as null.</summary>
    public void SetObjectTableEntry(short index, XObject? item)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        while (ObjectTableEntries.Count < index) ObjectTableEntries.Add(null);
        ObjectTableEntries[index - 1] = item;
    }

    internal void AttachObjectTable(List<XObject?> table) => _objectTableEntries = table;

    /// <summary>FUN_0046dae0: write one short to the +0x7c effect table of this object's lot table.</summary>
    public void SetObjectTableEffectWord(short index, short value) => ObjectTableEffectWords[index] = value;

    /// <summary>FUN_0045D29F: store a float at cXObject +0x78c + 4 * <paramref name="selector"/>.</summary>
    public void SetInteractionExecutorFloat(int selector, float value) => InteractionExecutorFloats[selector] = value;

    /// <summary>Store a Generic Sims Call executor word at its original cXObject byte offset.</summary>
    public void SetInteractionExecutorWord(int offset, short value)
    {
        if (offset == 0x59c) InteractionBehaviorSavedWord = value;
        else InteractionExecutorWords[offset] = value;
    }

    /// <summary>The value each +0x130 slot entry holds at +0x10. Entry 0 is the reserved overlap-chain slot;
    /// actual attachment slots begin at entry 1.</summary>
    public List<short> Slots { get; } = [];
    /// <summary>The parsed SLOT resource entries in original order. Sims.exe's FUN_0046e3ed dispatches type 0 to the
    /// cXObject +0x130 footprint-slot vector, type 1 to the separate 0x3c-byte runtime vector, and type 3 to routing.</summary>
    public List<Formats.Slot> DefinitionSlots { get; } = [];
    /// <summary>The type-0 entries built at cXObject +0x130 in source order (FUN_0046e3ed/FUN_0046dfd7); entry index 0
    /// here maps to the first non-sentinel vector entry used by Snapshot and saved slot restoration.</summary>
    public List<ObjectFootprintSlot> FootprintSlots { get; } = [];
    /// <summary>Type-3 SLOT records copied by FUN_0046dea8 into the object's route-slot vector in
    /// FUN_0046e3ed. These supply native Go To Routing Slot requests.</summary>
    public List<RoutingSlotRequest> RoutingSlots { get; } = [];
    /// <summary>The object's 0x3c-byte runtime records consumed by FUN_00452237/FUN_0046e120. These are distinct from
    /// SLOT resources and from the stack-built <see cref="RoutingSlotRequest"/> passed to the route host.</summary>
    public List<ObjectRuntimeSlot> RuntimeSlots { get; } = [];
    public short[] DynamicSpriteFlags { get; set; } = [];
    public short[] TypeAttributes { get; set; } = [];
    /// <summary>The transient word at Sims.exe cXObject +0x59c saved and restored by GenericSimsCall 0x18.</summary>
    public short InteractionBehaviorSavedWord { get; set; }
    /// <summary>Values at cXObject +0x78c + 4 * selector, written by FUN_0045D29F for GenericSimsCall 0x15 and 0x20.</summary>
    public Dictionary<int, float> InteractionExecutorFloats { get; } = [];
    /// <summary>Raw words written by GenericSimsCall 0x15 and 0x20 at +0x62c, +0x630 and +0x62e.</summary>
    public Dictionary<int, short> InteractionExecutorWords { get; } = [];
    /// <summary>The type record at +0x11c, or null (inferred name; read by Test Object Type).</summary>
    public TypeRecord? TypeRecord { get; set; }
    /// <summary>Where the object is on the lot: X (+0xf8), Y (+0xf4) and Height (+0x110). Read by the distance and
    /// direction primitives.</summary>
    public ObjectPosition Position { get; set; }
    /// <summary>The object's relationship list (+0xe4): the values it keeps toward other objects, read by the
    /// Relationship and Old Relationship primitives.</summary>
    public RelationshipTable Relationships { get; } = new();

    public short this[ObjectData field]
    {
        get => Data[(int)field];
        set => Data[(int)field] = value;
    }
}

/// <summary>The type record a cXObject holds at +0x11c (inferred name). Sims.exe reads the type key, a dword, at +0x1c of
/// the record at the record's +0x60; the port keeps that key on this record (inferred to be the object type's id). The
/// record's cached alternative (+0x8c) is loaded into <see cref="AlternativeTypeKey"/> from the ordered native object registry.</summary>
public sealed class TypeRecord(int key)
{
    public int Key { get; } = key;
    /// <summary>Raw type-record byte/dword at +0xcc. Bit 0 gates FUN_0043779b; the original constructor zeros it and
    /// no later producer was found, so native file loaders retain zero.</summary>
    public byte ExtensionFlagsCC { get; init; }
    /// <summary>The alternative type's OBJD Price at +0x24, used when the associated OBJD has a MasterId.</summary>
    public short? AlternativeObjectPrice { get; init; }
    /// <summary>The lazily allocated type-record extension at +0x88 ensured by FUN_00458058(1) for non-person types.</summary>
    public bool RuntimeExtension88Initialized { get; internal set; }
    /// <summary>OBJD object type word read by Sims.exe FUN_004341ed at type-record +0x12.</summary>
    public short ObjectType { get; init; } = -1;
    /// <summary>The signed type-record word at +0x10, used by FUN_00463804 and FUN_0043532b to match
    /// multi-tile members; loaded from the OBJD SubIndex.</summary>
    public short SubIndex { get; init; }
    /// <summary>The type-record word at +0x62 read by the base cXObject initializer in FUN_00431c90. Its offset
    /// matches OBJD ObjectVersion (word 49); native hydrates it from that field.</summary>
    public short ObjectVersion { get; init; }
    /// <summary>Person animator body-string selector derived from OBJD BodyStrings entry 13 at load time.</summary>
    public short? AnimatorBodyStringMode { get; init; }
    /// <summary>Action-function ids resolved from the object's OBJf chunk or legacy OBJD entry points. A null table
    /// means the type record has not been hydrated from an object definition.</summary>
    public IReadOnlyList<ushort>? FunctionTable { get; init; }
    /// <summary>Check-tree ids paired with <see cref="FunctionTable"/>. Legacy OBJD function entries have no check trees.</summary>
    public IReadOnlyList<ushort>? FunctionCheckTable { get; init; }
    /// <summary>The cached alternative type-record key from +0x8c, resolved by FUN_004573f9/FUN_0043aeef.
    /// Null means the alternative has not been loaded or no registry was supplied.</summary>
    public int? AlternativeTypeKey { get; init; }
    /// <summary>The packed four-nibble footprint extent mask at type-record +0x9a, rotated by FUN_00433dbb.</summary>
    public ushort FootprintMask { get; init; }
    /// <summary>The animation key the record keeps at +0x1c (inferred name and meaning): Manage Inventory reads it
    /// as the animation key when its operand names none, matches avatar objects by it (FUN_00425769), and ensures
    /// the avatar animation through it (FUN_0042a8e3). Zero when the port has not loaded one.</summary>
    public int AnimationKey { get; init; }
    /// <summary>The name the record keeps at +0x18 (inferred: the object's name, which Show String prints before the
    /// text). Null when the port has not loaded one.</summary>
    public string? Name { get; init; }
    /// <summary>CTSS catalog entries 0 and 1 for object-name and catalog-description text substitutions.</summary>
    public string? CatalogName { get; init; }
    public string? CatalogDescription { get; init; }
    /// <summary>The tree table the record keeps at +0xc (inferred name: its behavior scope). Run Tree By Name's sync
    /// path and the functional-tree push read the stack object's table from here (inferred: the same table both
    /// times). Null when the port has not loaded one.</summary>
    public ITreeTable? BehaviorScope { get; init; }
    /// <summary>The object's resolved TTAB, used by autonomy and Push Interaction.</summary>
    public InteractionTable? Interactions { get; init; }
    /// <summary>The BuildModeType (+0x8a) of the first same-class, same-MasterId, SubIndex -1 entry resolved by
    /// FUN_0043aeef. Zero when the type has no alternate record.</summary>
    public short AutonomyAlternativeTypeFlag { get; init; }
}

/// <summary>The four dwords passed by pointer to a gated Sims.exe object callback.</summary>
public readonly record struct GatedObjectCallbackRequest(int First, int Second, int Third, int Fourth);

/// <summary>A timed XEVT embedded in an original Vitaboy animation. Playback time is in milliseconds.</summary>
public readonly record struct AvatarAnimationEvent(int TimeMilliseconds, short Value)
{
    public static int MotionFrameToMilliseconds(int frame, int motionFrameCount, float durationMilliseconds) =>
        motionFrameCount > 0 && durationMilliseconds > 0f
            ? (int)(frame * (durationMilliseconds / motionFrameCount))
            : frame;

    /// <summary>Converts a motion's authored frame key to Practice time. Practice compares each motion's event key
    /// against trunc(phase × that motion's frame count), where phase is elapsed milliseconds / skill duration.</summary>
    public static AvatarAnimationEvent FromMotionFrame(int frame, short value, int motionFrameCount,
        float durationMilliseconds) => new(MotionFrameToMilliseconds(frame, motionFrameCount, durationMilliseconds), value);
}

/// <summary>A pack-owned sound cue embedded in an imported animation's timed properties.</summary>
public readonly record struct AvatarAnimationSoundEvent(int TimeMilliseconds, string PackId, string Resource, string Cue, bool Loop, bool Stop);

/// <summary>SAnimator::FindTagTime returns the matched motion's bone name and event time for the authored empty-name
/// property value "1" or "2". The normalized time is the value StartVitaBoy stores at +0x168/+0x16c.</summary>
public readonly record struct AvatarAnimationMarker(float NormalizedTime, string Bone);

/// <summary>The source-space points consumed by SAnimator::UpdateHeadDirection after target resolution.</summary>
public readonly record struct HeadSeekGeometry(Vector2 Actor, Vector2 HeadFake, Vector2 Target);

/// <summary>The playback facts needed by Sims.exe's Animate Sim state machine. Scene rendering can use the same clip
/// separately; simulation timing does not depend on avatar mesh construction.</summary>
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

/// <summary>JSON-safe saved state for an animator channel. Vector values are stored as explicit components because
/// System.Numerics.Vector3 is field-backed and the native save metadata does not enable field serialization.</summary>
public sealed record AvatarAnimationPlaybackState(string Name, int FrameCount, AvatarAnimationEvent[] Events,
    AvatarAnimationSoundEvent[]? SoundEvents, float StandardDistance, float[] StandardOffset,
    float[][]? StandardRootMotionSamples, float DurationMilliseconds, bool Backwards, bool Loop, float Frame,
    bool EndReached, bool RouteDriven, byte EventsRun, int NextEventIndex, int NextSoundEventIndex,
    short[] EventQueue, AvatarAnimationSoundEvent[] SoundEventQueue)
{
    public bool InterpolatePose { get; init; } = true;
    public bool Interruptible { get; init; }
    /// <summary>Marks the one-shot standard skill 0x13 started by deferred pool EndFollow, whose practice may
    /// continue after SAnimator's state-7 tail has completed the follow.</summary>
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
}

/// <summary>One person's current Animate Sim playback. The original advances the avatar once per person update and
/// delivers XEVT values to the waiting BHAV on a later invocation of opcode 0x2c.</summary>
public sealed class AvatarAnimationPlayback(AvatarAnimationClip clip, bool backwards, bool loop, int? startedAtMils = null)
{
    private static long nextPracticeStartOrder;
    private int nextEventIndex = backwards ? clip.Events.Count - 1 : 0;
    private int nextSoundEventIndex = backwards ? (clip.SoundEvents?.Count ?? 0) - 1 : 0;
    public long PracticeStartOrder { get; private set; } = Interlocked.Increment(ref nextPracticeStartOrder);
    public AvatarAnimationClip Clip { get; } = clip;
    public bool Backwards { get; } = backwards;
    public bool Loop { get; } = loop;
    /// <summary>Practice +0x18 records the last Animator::GetMils value applied. Root-only VitaBoy ticks skip
    /// Practice::Apply, so the next full application advances across the elapsed interval (skeleton.s
    /// 0x10361960–0x103619BC; Animator.s 0x10476E90–0x10476F28).</summary>
    public int? LastPracticeApplyMils { get; private set; } = startedAtMils;
    /// <summary>Practice +0x24. Practice::SetScale changes its signed rate and the duration used by Apply.</summary>
    public float PracticeScale { get; private set; } = backwards ? -1f : 1f;
    public float Frame { get; private set; } = backwards ? clip.FrameCount : 0;
    public bool EndReached { get; private set; } = !loop && clip.FrameCount <= 0;
    /// <summary>Windows FUN_00597c54 only interpolates adjacent pose frames when the elapsed update is less than two
    /// frame intervals; a larger jump samples the current frame directly.</summary>
    public bool InterpolatePose { get; private set; } = true;
    /// <summary>True while a route owns the frame: the walk skill advances with the route's root motion.</summary>
    public bool RouteDriven { get; init; }
    /// <summary>Primary Practice +0x68, set from AnimateNewParam flags bit 3 or updated by interruptable/interruptible
    /// time properties (00591301.c:37-40). Sims.exe may stop an unfinished primary practice early when the person's
    /// source ShouldInterrupt predicate becomes true.</summary>
    public bool Interruptible { get; set; }
    public bool PoolSwimExit { get; init; }
    /// <summary>Practice +0x2c, the weight Skeleton::ApplyPractices uses to blend this practice into the current pose.</summary>
    public float PracticeWeight { get; set; } = 1f;
    private bool practiceWeightFadeActive;
    private int practiceWeightFadeStartMils;
    private int practiceWeightFadeEndMils;
    private float practiceWeightFadeStart;
    private float practiceWeightFadeTarget;

    /// <summary>Practice::Fade stores its millisecond interval and endpoints at +0x48/+0x4c/+0x50/+0x54.</summary>
    public void FadePracticeWeight(int startMils, int endMils, float startWeight, float targetWeight)
    {
        practiceWeightFadeActive = true;
        practiceWeightFadeStartMils = startMils;
        practiceWeightFadeEndMils = endMils;
        practiceWeightFadeStart = startWeight;
        practiceWeightFadeTarget = targetWeight;
    }

    /// <summary>Practice::Apply evaluates the fade against Animator::GetMils, clamps interpolation to [0, 1], and
    /// commits the target weight after the end time.</summary>
    private void ApplyPracticeWeightFade(int animatorMils)
    {
        if (!practiceWeightFadeActive) return;
        var elapsed = unchecked(animatorMils - practiceWeightFadeStartMils);
        var duration = unchecked(practiceWeightFadeEndMils - practiceWeightFadeStartMils);
        if (elapsed > duration)
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

    /// <summary>Advances Practice by the elapsed Animator milliseconds. The normal Animator tick is 42 ms, while
    /// root-only VitaBoy ticks skip Practice::Apply and the next full application includes that elapsed interval.
    /// Timed properties crossed in this interval are queued in chronological order (reverse chronological order while
    /// playing backwards).</summary>
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
            // FUN_00597c54 sets its pose interpolation flag unless two frame intervals fit in this update.
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
            // Practice::Apply cycle mode 0 clamps its phase before timed properties are
            // evaluated (skeleton.s 0x10361AFC–0x10361B38).
            Frame = Math.Clamp(Frame, 0f, Clip.FrameCount);
            EndReached = backwards ? Frame <= 0f : Frame >= Clip.FrameCount;
        }
        var time = duration > 0f ? Frame * (duration / Clip.FrameCount) : Frame * (1000f / 30f);
        var processedLoopBoundary = false;
        // Practice::Apply skips the timed property associations when blend weight +0x2c
        // is not positive (skeleton.s 0x10361CE8–0x10361CF4). Leave both cursors in
        // place so they are considered again if the practice becomes visible later.
        if (PracticeWeight > 0f && Loop && duration > 0f && frameStep > 0f &&
            (backwards ? Frame < 0f : Frame > Clip.FrameCount))
        {
            // Practice::Apply's TimePropsIterator crosses the end/start seam in the same application
            // (skeleton.s 0x10361EEC–0x103621A0). A loop tick can therefore deliver the tail, reset the
            // iterator, and deliver the head before committing the wrapped phase.
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
                // Practice::Apply cycle mode 1 retains the fractional part of its phase, even when one
                // elapsed tick crosses more than one cycle (skeleton.s 0x10361B3C–0x10361B78).
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

    /// <summary>FUN_0058950f's +0xa4 counts numbered XEVTs only as the BHAV consumes them, then supplies one
    /// missing expected number at a time after the animation has no more authored XEVTs.</summary>
    public short? TakeMissingExpectedEvent(sbyte expectedCount)
    {
        if (EventsRun >= expectedCount) return null;
        var expected = Backwards ? expectedCount - EventsRun - 1 : EventsRun;
        EventsRun++;
        return (short)expected;
    }

    /// <summary>Validates an authored numbered XEVT against the next expected index before exposing it to the BHAV.
    /// VitaBoy events 100 and above are independent substitutes and do not advance this counter.</summary>
    public bool TryConsumeExpectedEvent(short value, sbyte expectedCount, out short expectedValue)
    {
        expectedValue = value;
        if (value >= 100) return true;
        var expected = Backwards ? expectedCount - EventsRun - 1 : EventsRun;
        if (expected < 0 || expected >= expectedCount || value != expected) return false;
        expectedValue = (short)expected;
        EventsRun++;
        return true;
    }
}

/// <summary>The values the GenericSimsCall 0x27/0x28 primitive reads from FUN_0046DC72's 20-byte output record:
/// updated temporary 0, temporary 1, and request status. The native record interleaves those values with its key,
/// kind and age fields.</summary>
public readonly record struct GenericSimsCallRequestResult(short Temporary0, short Temporary1, int Status);

/// <summary>The update result of FUN_0042DB93/FUN_0042DF00 for one pending manager request. Kind 1 success returns
/// the type-record key that FUN_0044097D resolves to the final person key.</summary>
public readonly record struct GenericSimsCallRequestCompletion(
    short Temporary0,
    short Temporary1,
    bool Succeeded,
    int? TargetTypeRecordKey = null);

/// <summary>A person, Sims.exe's cXPerson: an object that also has person data (+0x58c, 256 fields, named by
/// <see cref="PersonData"/>) and sixteen motives, kept as floats (<see cref="Motive"/>).</summary>
public sealed class XPerson(short id, short[] definition, int attributes = 0) : XObject(id, definition, attributes)
{
    public const short PersonType = 2;

    /// <summary>The ordered subset of the global object type registry materialized in cXPerson +0x148 by
    /// FUN_00424850. Sims.exe includes type words 2, 0x200, 0x400, 0x800 and 0x1000; the vector's source slot numbers
    /// are one-based and are separate from lot object ids.</summary>
    public List<GenericSimsCallPersonTypeRecord> GenericSimsCallPersonTypeRecords { get; } = [];

    /// <summary>cXPerson +0x5ca: the one-based +0x148 slot matching this person's +0x11c type record, initialized by
    /// FUN_004258ed.</summary>
    public short GenericSimsCallPersonTypeRecordIndex { get; set; }

    /// <summary>The +0x5cc word is PersonData[32] (Person Type), read by FUN_00458485's visitor gate.</summary>
    public override short SelectionWord5CC
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.PersonType];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.PersonType] = value;
    }


    /// <summary>FUN_0045e8fd's reentrancy guard in cXPerson +0x114 bit 11. It remains set while the
    /// object-reference callback is processing this person.</summary>
    public bool TryBeginObjectReferenceNotification()
    {
        if ((Flags & 0x00000800u) != 0) return false;
        Flags |= 0x00000800u;
        return true;
    }

    /// <summary>Clears the FUN_0045e8fd reentrancy guard (the low bit 3 of byte +0x115).</summary>
    public void EndObjectReferenceNotification() => Flags &= ~0x00000800u;

    /// <summary>FUN_0045e8fd copies an initialized empty action record over +0xa8c when its previous record owner
    /// matches the notified subject.</summary>
    public void ClearPreviousActionRecord() => PreviousActionRecord = ActionQueueSlot.EmptyRecord();

    /// <summary>FUN_0045e8fd: when the invalidated subject is OBJD type 2, clear matching object pointers in the
    /// recipient's ordered +0xaf0..+0xaf4 runtime-reference vector (0x9c-byte entries).</summary>
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

    /// <summary>The short at cXPerson +0x72 written by Sims.exe FUN_0045dea5 when autonomy chooses an interaction. It is object data 20
    /// (Tree Table Entry), which Gosub Found Action (FUN_0045a9c5) reads back.</summary>
    public short InteractionActionWord72
    {
        get => this[ObjectData.TreeTableEntry];
        set => this[ObjectData.TreeTableEntry] = value;
    }
    /// <summary>The last point requested by native Look Towards when no presentation host is attached. Rotation animation
    /// and the source-facing queue are not modeled.</summary>
    public ObjectPosition? LastLookTowardsPoint { get; set; }

    /// <summary>The low bits at cXPerson +0x614 and +0x634 (person data Is Ghost and Is Living Ghost), read by FUN_00435244.</summary>
    public short FootprintFlags614 { get => this[global::SimsEngine.Simulation.PersonData.IsGhost]; set => this[global::SimsEngine.Simulation.PersonData.IsGhost] = value; }
    public short FootprintFlags634 { get => this[global::SimsEngine.Simulation.PersonData.IsLivingGhost]; set => this[global::SimsEngine.Simulation.PersonData.IsLivingGhost] = value; }

    public short[] PersonData { get; } = new short[256];
    public float[] Motives { get; } = new float[16];
    /// <summary>The avatar's primary animation channel used by Animate Sim (Sims.exe FUN_0058950f).</summary>
    public AvatarAnimationPlayback? AnimationPlayback { get; set; }
    /// <summary>SAnimator's shared +0x2c event vector. VitaBoy animation callbacks enqueue XEVTs from every active
    /// practice here; Animate Sim dequeues from this animator-level queue, not from one practice's local properties.</summary>
    public List<short> PendingAnimationEvents { get; } = [];
    /// <summary>SAnimator +0x220: current locomotion practice scale, retained across route callback teardown.</summary>
    public float AnimatorWalkingScale { get; set; } = 1f;
    /// <summary>Whether SetupRegistrations found both SAnimator head-seek registrations (+0x13c and +0x144).
    /// Unknown until the selected native avatar pose is prepared; anything other than true makes UpdateHeadDirection return before it
    /// resolves or clears the target (Mac SAnimator.s 0x1034E0DC–0x1034E0F0).</summary>
    public bool? AnimatorHeadSeekRegistrationsAvailable { get; set; }
    /// <summary>SAnimator +0x210, the transient AdjustHead posture angle. The original does not stream this value.</summary>
    public float AnimatorHeadAdjustment { get; set; }
    /// <summary>SAnimator +0x208/+0x20c: cached actor baseline and target direction, in eighth-turn units.</summary>
    public float AnimatorHeadBaselineDirection { get; set; }
    public float AnimatorHeadTargetDirection { get; set; }
    /// <summary>SAnimator +0x21c/+0x224: cached horizontal target coordinates refreshed when HeadSeekObject is nonzero.</summary>
    public Vector2 AnimatorHeadTargetPoint { get; set; }
    /// <summary>VitaBoy scale set by cXPerson::Simulate from PersonData[ScalingSim]. It starts at unit scale.</summary>
    public float AnimatorBodyScale { get; set; } = 1f;
    /// <summary>The transient cXPerson +0x8e guard suppressing periodic scale updates.</summary>
    public bool AnimatorScaleUpdateSuppressed { get; set; }
    /// <summary>The floating animator root coordinates seeded and advanced by SAnimator::BeginFollow/FollowOneStep.
    /// EndFollow copies these into its interpolation origin, so keep them after the route callback is removed; the
    /// source ForceLocation/SnapToGrid path invalidates them before the next route reseeds the root.</summary>
    public float? AnimatorRootX { get; set; }
    public float? AnimatorRootY { get; set; }
    /// <summary>SAnimator +0x88: the current FTile direction copied at BeginFollow and retained through EndFollow.</summary>
    public float? AnimatorRootDirection { get; set; }
    /// <summary>SAnimator +0x8c/+0x90/+0x94: root x/y and direction displacements set by SetAnimDisplacements.</summary>
    public float AnimatorDisplacementX { get; private set; }
    public float AnimatorDisplacementY { get; private set; }
    public float AnimatorDisplacementDirection { get; private set; }
    /// <summary>SAnimator +0x28c/+0x290: projected root step used by the gait-entry distance gate.</summary>
    public float AnimatorProjectedRootStepX { get; set; }
    public float AnimatorProjectedRootStepY { get; set; }
    public void SetAnimatorDisplacements(float x, float y, float direction)
    {
        AnimatorDisplacementX = x;
        AnimatorDisplacementY = y;
        AnimatorDisplacementDirection = direction;
    }
    /// <summary>SAnimator +0x188 control state. The Mac constructor and Reset initialize it to 3; route following
    /// also enters state 3. Other animator transitions are modeled separately as their source gates are completed.</summary>
    public int AnimatorControlState { get; set; } = 3;
    /// <summary>SAnimator +0x294, loaded from Animate Sim flags bit 6 and consumed by control state 0.</summary>
    public bool AnimatorPracticeScaleEnabled { get; set; }
    /// <summary>SAnimator +0x98 current standard-skill index. It is independent of the route's selected motion and
    /// starts at -1; SetCurAnim, StopCurAnim and the swimming transition update it.</summary>
    public int AnimatorCurrentSkill { get; set; } = -1;
    /// <summary>Animator::GetMils timebase retained per Sim; its base tick interval is 42 milliseconds.</summary>
    public int AnimatorMilliseconds { get; set; }
    /// <summary>SAnimator +0x284: blocks locomotion entry while a near-waypoint Practice::Fade is active.</summary>
    public int AnimatorLocomotionFadeGate { get; set; }
    /// <summary>SAnimator +0x288 transition state read by the deferred EndFollow branch. SAnimator::Reset initializes it to 0.</summary>
    public int AnimatorPoolTransitionState { get; set; }
    /// <summary>SAnimator +0x280 routing transition state; distinct from the pool transition at +0x288.</summary>
    public int AnimatorRouteTransitionState { get; set; }
    /// <summary>Raw SAnimator +0x198 word, set to 1 by completed state-5/7 dispatches. Its consumers are outside
    /// SAnimator; retain the observed source write without assigning it a higher-level meaning.</summary>
    public int AnimatorStateTailField198 { get; set; }
    /// <summary>cXPerson +0x60c, refreshed only by ForceLocation from the current tile's HasPool result.</summary>
    public bool AnimatorPoolTile { get; set; }
    /// <summary>SAnimator +0x194: gait-transition gate written when a start skill enters control state 9/10.</summary>
    public int AnimatorGaitEntryState { get; set; }
    /// <summary>SAnimator +0x60e, selected by ResetSuits from the owning ObjSelector's Body Strings entry 13.</summary>
    public short AnimatorBodyStringMode { get; set; }
    /// <summary>The independent background animation channel used by Animate Sim mode 1 (Sims.exe FUN_0058950f).</summary>
    public AvatarAnimationPlayback? BackgroundAnimationPlayback { get; set; }
    /// <summary>SAnimator +0x190: last cXPerson PersonData[0] value processed by UpdateBackgroundPractice.</summary>
    public short BackgroundPracticePersonState { get; set; } = -1;
    /// <summary>The avatar's independent carry animation channel, used by Animate Sim mode 2 (Sims.exe FUN_0058950f).</summary>
    public AvatarAnimationPlayback? CarryAnimationPlayback { get; set; }
    /// <summary>SAnimator +0x1a0: 6 suppresses automatic hand-slot carry practice while an explicit mode-2 animation owns it.</summary>
    public int CarryPracticeMode { get; set; }
    /// <summary>Returns active animation practices in Sims.exe Skeleton::ApplyPractices order. Background practices
    /// have priority -10, primary practices have priority 0, and both automatic and explicit mode-2 carry practices
    /// have priority +10 (005903a7.c:27, 0058950f.c:122).</summary>
    public IReadOnlyList<AvatarAnimationPlayback> GetAnimationPracticeApplicationOrder()
    {
        var result = new List<(AvatarAnimationPlayback Playback, int Priority, long StartOrder)>(3);
        if (BackgroundAnimationPlayback is { } background) result.Add((background, -10, background.PracticeStartOrder));
        if (AnimationPlayback is { } primary) result.Add((primary, 0, primary.PracticeStartOrder));
        if (CarryAnimationPlayback is { } carry)
            result.Add((carry, 10, carry.PracticeStartOrder));
        result.Sort(static (left, right) =>
        {
            var priority = left.Priority.CompareTo(right.Priority);
            return priority != 0 ? priority : left.StartOrder.CompareTo(right.StartOrder);
        });
        return result.ConvertAll(static entry => entry.Playback);
    }

    /// <summary>Consumes the shared SAnimator event FIFO. The primary playback fallback supports callers and
    /// snapshots created before the per-person queue was introduced.</summary>
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
    /// <summary>SAnimator +0x1a4: cached automatic carry-practice state; -1 forces the next animator tick to refresh it.</summary>
    public int CarryPracticeCachedState { get; set; } = -1;
    /// <summary>SAnimator +0x19c: a reach animation is active, so hand-slot changes wait until reach completion.</summary>
    public bool ReachAnimationActive { get; set; }
    /// <summary>The currently active native route operation. Sims.exe keeps this in its asynchronous route record.</summary>
    public RouteState? NativeRoute { get; set; }
    /// <summary>Routes suspended while a portal function runs a nested route on the same person stack.</summary>
    public List<RouteState> NativeRouteParents { get; } = [];
    /// <summary>Applies the ordered, per-tick motive changes from cXPerson::FUN_0045cdf8 after its generic object update
    /// succeeds. Each entry moves toward its target by its stored rate and clamps only when it crosses that target.</summary>
    public void ApplyMotiveChanges()
    {
        foreach (var change in MotiveChanges)
        {
            var index = (int)change.Motive;
            var current = Motives[index];
            // cXPerson::Simulate branches on the signed rate (MotiveInc +4),
            // not on whether the target is positive or negative.
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
    /// <summary>The previous motive values at +0x7cc; FUN_00420fe9 reads prior Hunger and refreshes all 16 values.</summary>
    public float[] SavedPersonValues7CC { get; } = new float[16];
    /// <summary>The PERS wrapper's last 60-tick update counter at +0x810.</summary>
    public int MotiveDecayLastTickCounter { get; set; }
    /// <summary>The raw short at cXPerson +0x8e, which gates FUN_0045d2b1; its other semantics remain unresolved.</summary>
    public short PersonTickGateWord8E { get; set; }
    /// <summary>The cached room key at cXPerson +0xafc, synchronized from RoomWord84 by FUN_0045e5a9.</summary>
    public ushort LastObservedRoomIdAFC { get; set; }
    /// <summary>The character object state restored by the person's virtual +0x84 loader (FUN_0059187a).</summary>
    public PersonCharacter? SavedCharacter { get; set; }
    /// <summary>Transient rendered character state (for example the winter body suit). Save serialization always reads
    /// <see cref="SavedCharacter"/> so runtime clothing changes cannot rewrite a Sim's persistent outfit baseline.</summary>
    public PersonCharacter? PresentationCharacter { get; set; }
    /// <summary>Runtime extra-part suits attached via SAnimator +0xd8/+0xdc ("dress"/"undress", 00591301.c / 005859df.c /
    /// 0058d91d.c). Mirrors additions even when <see cref="SavedCharacter"/> is absent or <see cref="PresentationCharacter"/>
    /// is refreshed.</summary>
    public List<(string Appearance, string Texture)> TransientExtraParts { get; } = [];

    /// <summary>Implements SAnimator vtable +0xd8 (FUN_005859df): removes any matching extra-part suit via +0xdc, then
    /// appends <paramref name="appearance"/> and <paramref name="texture"/> without instantiating a non-null
    /// <see cref="PresentationCharacter"/> when none is active.</summary>
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

    /// <summary>Implements SAnimator vtable +0xdc (FUN_0058d91d): removes all extra-part entries matching
    /// <paramref name="appearance"/> case-insensitively, ignoring any optional <c>.apr</c> suffix.</summary>
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

    /// <summary>Implements SAnimator vtable +0x10 (FUN_00588d45, called by ResetSuits FUN_00588907): clears all dynamic
    /// extra-part suits at +0x274.</summary>
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
    /// <summary>True while the native clothes-change animation temporarily owns this person's primary action update.</summary>
    public bool NativeOutfitTransitionActive { get; set; }
    /// <summary>The preallocated count at +0xaf0 restored by FUN_0046058f; its entry serializer currently has no state.</summary>
    public int SavedPersonActionStorageCount { get; set; }
    /// <summary>The 0x9c-byte records in the cXPerson +0xaf0/+0xaf4 vector. FUN_0045e8fd compares each entry's
    /// object pointer at +0x80 with a person subject and, on a match, sets +0x84, clears +0x80 and zeroes +0x90;
    /// its full-reset branch removes this entire vector via FUN_0045fc43.</summary>
    public List<PersonRuntimeObjectReference> RuntimeObjectReferences { get; } = [];
    /// <summary>The distinct 0x3c-byte records in cXPerson +0x148/+0x14c. FUN_0045e8fd passes each record to
    /// FUN_0046e15e(0), clearing its +0x10 pointer and +0x28 dword.</summary>
    public List<PersonRuntimeStateRecord> RuntimeStateRecords { get; } = [];
    /// <summary>FUN_0045fa7d's pending action requests: target, execution-stack depth and the source flag byte.</summary>
    public List<PersonActionRequest> PendingActionRequests { get; } = [];
    /// <summary>Retained source snapshots from FUN_0041f1c7; runtime execution and saves use the live records below.
    /// SavedQueuedAction is the legacy name for the second fixed (previous-action) record.</summary>
    public PersonAction? SavedCurrentAction { get; set; }
    public PersonAction? SavedQueuedAction { get; set; }
    public List<PersonAction> SavedActions { get; } = [];
    /// <summary>Lossless +0xb00 entries after resolving each saved +0xf0 key through the lot object manager.</summary>
    public List<PersonObjectReference> SavedPersonObjectReferences { get; } = [];
    /// <summary>Raw +0xad0 entries. Their first dword's meaning is not fully traced.</summary>
    public List<PersonValueEntry> SavedPersonValues { get; } = [];
    /// <summary>FUN_0045e839 writes +0x5ce, which is PersonData[33] (Priority), after accepting an action.</summary>
    public short ActivePriority
    {
        get => PersonData[(int)global::SimsEngine.Simulation.PersonData.Priority];
        set => PersonData[(int)global::SimsEngine.Simulation.PersonData.Priority] = value;
    }
    /// <summary>The active action sequence at cXPerson +0xa84, the +0x3c field within the current record at +0xa48.</summary>
    public int GenericSimsCall02GateWordA84
    {
        get => CurrentActionRecord?.Sequence ?? 0;
        set
        {
            if (CurrentActionRecord is { } record) record.Sequence = value;
            else CurrentActionRecord = new ActionQueueSlot { Sequence = value };
        }
    }
    /// <summary>Bit 0 at cXPerson +0xa89, set by Sims.exe FUN_0045d764 when the active interaction is cancelled.</summary>
    public bool ActiveActionCancelRequested { get; set; }
    /// <summary>Observable native attachment state for source-timed interaction accessories not yet rendered as body props.</summary>
    public HashSet<string> InteractionAttachments { get; } = new(StringComparer.Ordinal);
    /// <summary>The raw object pointer at cXPerson +0xa54, written by GenericSimsCall 0x02 when +0xa84 is nonzero.</summary>
    public XObject? GenericSimsCall02ObjectA54 { get; set; }
    /// <summary>The modeled action-record projection at cXPerson +0xa48. Sims.exe replaces it after an accepted start.</summary>
    public ActionQueueSlot? CurrentActionRecord { get; set; }
    /// <summary>The previous current-action projection at cXPerson +0xa8c.</summary>
    public ActionQueueSlot? PreviousActionRecord { get; private set; }
    /// <summary>Hydrates the two fixed action records and the eight-slot ring after all object ids are registered.
    /// FUN_0045d099 writes current (+0xa48), previous (+0xa8c), then queue entries in logical order.</summary>
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
            // FUN_0045f8b0 refuses an append when the eight-entry ring is full.
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

    /// <summary>The raw byte at cXPerson +0x20, which controls bit 5 when FUN_0045e839 backs up the current action.</summary>
    public byte ActionFlagByte20 { get; set; }
    /// <summary>The two words at cXPerson +0x5d8 (PersonData[38], MultipleRoutes) and +0x5da (PersonData[39], RoutingComplete),
    /// cleared when an existing action is replaced in FUN_0045e839.</summary>
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

    /// <summary>FUN_00433dbb recomputes the four footprint bounds from position, ghost span, the type-record extent
    /// nibbles and (for even facings) the temporary route-footprint extension.</summary>
    public void RefreshFootprintBounds()
    {
        FootprintBounds = FootprintBoundsAt(Position);
        FootprintBoundsDirection = this[ObjectData.Direction];
    }

    /// <summary>FUN_00433dbb's person bounds at a proposed location, used by placement before the person moves.</summary>
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

    /// <summary>The modeled state changes made when an action passes the startup checks in FUN_0045e839. Action records
    /// retain the native queue projection; fields the projection does not yet represent remain outside this transfer.</summary>
    public void ApplyStartedActionState(ActionQueueSlot action, int? replacementSequence = null)
    {
        if (CurrentActionRecord?.Owner is not null)
        {
            PreviousActionRecord = CurrentActionRecord.Copy();
            PreviousActionRecord.Flags = (PreviousActionRecord.Flags & ~0x20) | (ActionFlagByte20 != 0 ? 0x20 : 0);
        }
        CurrentActionRecord = action.Copy();
        // FUN_0045e839 tests +0xa50 after copying the incoming record (0x0045e880–0x0045e8a5).
        if (CurrentActionRecord.Owner is not null)
        {
            CurrentActionWord5D8 = 0;
            CurrentActionWord5DA = 0;
            if (GenericSimsCall02GateWordA84 == 0 && replacementSequence is { } sequence)
                CurrentActionRecord.Sequence = sequence;
        }
        GenericSimsCall02ObjectA54 = CurrentActionRecord.Secondary;
        ActivePriority = unchecked((short)action.Threshold);
        // FUN_0045e839 erases the full +0xad0 vector with FUN_004609a6. Set Motive Change
        // (FUN_0045a2db) stores its ordered three-dword entries there via FUN_0045f995.
        MotiveChanges.Clear();
        if (this[global::SimsEngine.Simulation.PersonData.FootprintExtension] != 0)
        {
            this[global::SimsEngine.Simulation.PersonData.FootprintExtension] = 0;
            RefreshFootprintBounds();
        }
        this[global::SimsEngine.Simulation.PersonData.RouteFootprintMask] = 0;
        this[global::SimsEngine.Simulation.PersonData.NonInterruptible] = 0;
    }

    /// <summary>Writes the live +0xa54 secondary-object reference, also projected by the current action record.</summary>
    public void SetCurrentActionSecondaryReference(XObject? value)
    {
        GenericSimsCall02ObjectA54 = value;
        if (CurrentActionRecord is { } current) current.Secondary = value;
    }

    /// <summary>The motive changes the person runs each tick (the vector at +0xad0; Set Motive Change adds to it and clears
    /// it, see <see cref="MotiveChange"/>).</summary>
    public List<MotiveChange> MotiveChanges { get; } = [];
    /// <summary>The objects this person is interacting with (the vector at +0xb00, 12-byte entries whose first dword is the
    /// object; the other two dwords are not traced). Test Sim Interacting With reads it (the name is inferred).</summary>
    public List<XObject> InteractingObjects { get; } = [];

    /// <summary>The flag byte at +0x5a (inferred name: input flags). Idle For Input sets <see cref="IdleForInputFlag"/> while
    /// the person is idle for input and clears it when its countdown ends. Other writers are not traced.</summary>
    public byte InputFlags
    {
        get => unchecked((byte)Data[(int)ObjectData.Flags]);
        set => Data[(int)ObjectData.Flags] = unchecked((short)(((ushort)Data[(int)ObjectData.Flags] & 0xff00) | value));
    }
    /// <summary>The bit of <see cref="InputFlags"/> that Idle For Input (FUN_0045dd00) sets and clears (inferred name).</summary>
    public const byte IdleForInputFlag = 0x40;
    /// <summary>The action queue's current index at +0xa40 (inferred name). The queue's slot is this value and 7.</summary>
    public int ActionQueueCurrent { get; set; }
    /// <summary>The action queue's end index at +0xa44 (inferred name). The queue is non-empty when it differs from
    /// <see cref="ActionQueueCurrent"/>.</summary>
    public int ActionQueueEnd { get; set; }
    /// <summary>The eight queue slots, from +0x820 at 0x44 bytes each; the ring indices are at +0xa40 and +0xa44
    /// (FUN_0045d377 / FUN_0045f887). Each <see cref="ActionQueueSlot"/> exposes the priority at record +0x1c and flags at +0x40.</summary>
    public ActionQueueSlot[] ActionQueue { get; } = [new(), new(), new(), new(), new(), new(), new(), new()];
    /// <summary>The float at +0x7a0 that Idle For Input's predicate reads. The predicate compares it with -99
    /// (DAT_00667428). It aliases motive slot 5 (+0x78c + 4 * 5, FUN_0045d283), as +0x7a8 and +0x7b0 alias slots 7 and 9.</summary>
    public float IdleFloat7A0 { get => Motives[5]; set => Motives[5] = value; }
    /// <summary>The float at +0x7a8 that Idle For Input's predicate reads (same comparison; see <see cref="IdleFloat7A0"/>).</summary>
    public float IdleFloat7A8 { get => Motives[7]; set => Motives[7] = value; }
    /// <summary>The float at +0x7b0 that Idle For Input's predicate reads (same comparison; see <see cref="IdleFloat7A0"/>).</summary>
    public float IdleFloat7B0 { get => Motives[9]; set => Motives[9] = value; }
}

/// <summary>The 12-byte request appended by cXPerson::FUN_0045e707 and consumed by its stack-pop callback.</summary>
public sealed record PersonActionRequest(XObject Target, int Priority, bool Flag);

/// <summary>A native Gosub Found Action whose target action tree could not be pushed by the person's +0x34 virtual.</summary>
public sealed record FoundActionPushFailure(short PersonId, short TargetId, short Key, short ActionTree, string Reason);

/// <summary>Fields from one 0x9c-byte record in cXPerson's +0xaf0 vector, used by subject invalidation.</summary>
public sealed class PersonRuntimeObjectReference(XObject? target, byte state84 = 0, int rawWord90 = 0, int rawWord28 = 0)
{
    public XObject? Target { get; set; } = target;
    public byte State84 { get; set; } = state84;
    public int RawWord90 { get; set; } = rawWord90;
    public int RawWord28 { get; set; } = rawWord28;
}

/// <summary>The +0x10 pointer and +0x28 dword in one 0x3c-byte person-owned record at +0x148; reset clears both.</summary>
public sealed class PersonRuntimeStateRecord(XObject? target = null, int rawWord28 = 0)
{
    public XObject? Target { get; set; } = target;
    public int RawWord28 { get; set; } = rawWord28;
}

/// <summary>A restored +0xb00 person-list entry: its saved object-manager key, resolved target (if still present), and
/// the two remaining words that FUN_00459461 restores verbatim.</summary>
public sealed record PersonObjectReference(short SavedObjectKey, XObject? Target, int Value, bool Flag);

/// <summary>A native projection of one 0x44-byte action record in the person's queue. <see cref="Threshold"/> is the
/// priority at record +0x1c (absolute person offset +0x83c), and <see cref="Flags"/> is the flags dword at record +0x40;
/// Idle For Input reads these through its +0x83c view.</summary>
public sealed class ActionQueueSlot
{
    /// <summary>Record dword +0x10. FUN_0041ed45 initializes an empty 0x44-byte action record with this set to -1.</summary>
    public int RawDword10 { get; set; }
    /// <summary>The priority at record +0x1c. Idle For Input is busy when the person's Priority is below it.</summary>
    public int Threshold { get; set; }
    /// <summary>The flags dword at record +0x40. Bit 1 (value 2) makes Idle For Input busy and allows a queued action
    /// to start while the frame is waiting.</summary>
    public int Flags { get; set; }
    public XPerson? Person { get; init; }
    public XObject? Owner { get; init; }
    public XObject? Secondary { get; set; }
    public int RecordKind { get; init; }
    public short[] Parameters { get; init; } = new short[4];
    public byte InteractionId { get => unchecked((byte)RawDword10); init => RawDword10 = value; }
    /// <summary>The action tree word at record +0x20 (TTAB descriptor +2); the guard comes from the descriptor itself.</summary>
    public ushort ActionTree { get; init; }
    public float Attenuation { get; init; }
    /// <summary>The sequence dword at record +0x3c, assigned by FUN_0041f15a.</summary>
    public int Sequence { get; set; }
    /// <summary>The owned string pointer at record +0x28, written by Change Action String's FUN_0041f1b0.</summary>
    public string? ActionText { get; set; }
    /// <summary>The sixteen motive advertisements evaluated by FUN_00458485 (including CheckTreeAds modifications and
    /// cross-room Room motive zeroing).</summary>
    public MotiveAdvertisement[]? EvaluatedAdvertisements { get; set; }

    /// <summary>FUN_0041f1c7 resolves object ids through the lot manager; the first reference must be a person.
    /// The action text is not serialized by the original function.</summary>
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

    /// <summary>Creates the initialized empty record used when Sims.exe resets a person's previous or current action.</summary>
    public static ActionQueueSlot EmptyRecord() => new() { RawDword10 = -1 };

    /// <summary>FUN_0041edc1 copies the current 0x44-byte slot to a temporary record before advancing the ring cursor.</summary>
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

/// <summary>The message FUN_00422d79 sends when priority insertion removes a queued action.</summary>
public sealed record ActionQueueRemovalNotice(short PersonKey, int QueuePosition);
public sealed record QueuedInteractionStartTrace(short PersonId, short? TargetId, short ActionTree,
    bool TargetScopeFound, bool InteractionPassed, bool ActionTreePushed, ushort GuardTree,
    short PersonSelectionWord, short PersonAge, short PersonGender, int StateClass, short TargetFlags);
/// <summary>A type-record pointer slot created in a person's +0x148 vector by FUN_00424850/FUN_004257ef.
/// RegistryIndex preserves the ordered global type-record identity; Index is that record's one-based position in the
/// person's vector, and TypeRecordKey is the key at the referenced type record's +0x1c. CatalogName and TypeName
/// retain localized catalog and definition labels. Payload is the record's 256-word block at +0x74, zero-initialized
/// by FUN_004237d4; other record fields and the editor's scoped-field map are not represented here.</summary>
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
/// <summary>One candidate produced by FUN_0042e158: a type-2 per-person record mapped to the first shared person row
/// with the same type-record key. LivePersonId and PersonData follow FUN_004e9744/FUN_004ea79e lookup order.</summary>
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
/// <summary>State changed by Sims.exe FUN_0042a2fe while refreshing the neighbor interaction manager; retained to
/// distinguish source-driven person-type changes from ObjM restore differences during native acceptance checks.</summary>

/// <summary>A neighbor in the neighborhood: the person data the neighbor keeps between lots (+0x74) and the
/// definition of the neighbor's object (+8, then +0x60).</summary>
public sealed class NeighborState(short id, short[] definition)
{
    public short Id { get; } = id;
    /// <summary>The name stored in the neighborhood record and restored with the ordered NBRS list.</summary>
    public string? Name { get; set; }
    /// <summary>The key returned by FUN_0045741b for the neighbor's type record, used by FUN_004ea2ce when
    /// a person record has no cached neighbor id.</summary>
    public int TypeRecordKey { get; set; }
    /// <summary>Null represents neighbor +8 being null; otherwise these are the linked type record's raw +0xb4 flags
    /// tested by FUN_00423f63. The same predicate contributes to the source tree-step budget.</summary>
    public uint? FriendEligibilityObjectFlagsB4 { get; set; }
    /// <summary>The dword at neighbor +0x18, set to the person-data layout version by FUN_00425ab3.</summary>
    public int PersonRecordVersion { get; set; }
    public short[] PersonData { get; } = new short[256];
    public short[] Definition { get; } = definition;
    /// <summary>The relationship list the neighbor record keeps (+0xc): the values it holds toward other neighbors.</summary>
    public RelationshipTable Relationships { get; } = new();
    /// <summary>Neighborhood +0x24: set by the original relationship-change notification.</summary>
    public bool RelationshipsChangedFlag24 { get; set; }
    /// <summary>The cached friend count at neighbor +0x20, rewritten by FUN_00427192 while +0x24 is set.</summary>
    public int FriendCountWord20 { get; set; }
}

/// <summary>One frame of a tree stack: the stack object (+4), the parameters (count in byte +7) and the locals
/// (count in byte +6).</summary>
public sealed class StackFrame(short stackObject, short[] parameters, short[] locals)
{
    internal short[] StackObjectCell { get; } = [stackObject];
    public short StackObject { get => StackObjectCell[0]; set => StackObjectCell[0] = value; }
    public short[] Parameters { get; } = parameters;
    public short[] Locals { get; } = locals;
    /// <summary>The tree this frame runs, for the host's constant lookup.</summary>
    public object? Tree { get; init; }
    /// <summary>The tree id (+0), or -1 (0xFFFF) for none.</summary>
    public short TreeId { get; init; } = -1;
    /// <summary>The flag bit 15 of the stored tree id carries: the debugger's breakpoint on the whole tree.</summary>
    public bool TreeBreakpoint { get; init; }
    /// <summary>The current instruction (+2): an index, or 0xFE/0xFF once the tree has branched to a return.</summary>
    public int Instruction { get; set; }
    /// <summary>The dword at +8, cleared whenever the frame branches. Some primitives keep a phase in it (Reach/Put uses 0 to
    /// 3; Idle For Input sets 1 while its queued action runs), so the port stores the whole value.</summary>
    public int WaitState { get; set; }
    /// <summary>Whether <see cref="WaitState"/> is set. While it is set, a returning callee does not branch this frame.</summary>
    public bool Waiting { get => WaitState != 0; set => WaitState = value ? 1 : 0; }
    /// <summary>The serialized signed word passed through the code-owner frame callback. Its runtime meaning is not yet traced.</summary>
    public short CodeOwner { get; init; }
    /// <summary>Where this frame finds the trees it calls (+0xc). A frame without one is popped by the next step.</summary>
    public ITreeTable? Table { get; init; }
}

/// <summary>The 0x3c-byte route request passed to Sims.exe's routing host. Go To Relative Position constructs it on the
/// stack with FUN_0046de64/FUN_0046dfa0; it is not the per-object 0x3c-byte runtime vector iterated by FUN_00452237 and
/// rendered by FUN_00437961. Some fields are copied from a 0x48-byte SLOT resource by FUN_0046dea8. Defaults come from
/// FUN_0046de64; names for its type and Z fields (+0x00 and +0x0c) remain unresolved.</summary>
public sealed class RoutingSlotRequest
{
    /// <summary>+0x00: local slot X offset copied by FUN_0046dea8. Its units are inferred from FUN_00454f0c's 3/16 scale.</summary>
    public float X { get; set; }
    /// <summary>+0x04: local slot Y offset; FUN_00454f0c maps it to the other horizontal axis.</summary>
    public float Y { get; set; }
    /// <summary>+0x08: the third stored slot coordinate; FUN_004549b6's free-slot projection does not read it.</summary>
    public float Z { get; set; }
    /// <summary>The word at +0x10, set to 1 by Go To Relative Position's FUN_0046dfa0 call. Its semantic name is inferred.</summary>
    public int Standing { get; set; }
    /// <summary>+0x14 (inferred name, as <see cref="Standing"/>).</summary>
    public int Sitting { get; set; }
    /// <summary>+0x18 (inferred name, as <see cref="Standing"/>).</summary>
    public int Ground { get; set; }
    /// <summary>+0x1c. Bits 0 to 7 are the directions the slot can be reached from (inferred: one bit per direction,
    /// numbered 0 to 7 as the operands number them). Bit 14 and bit 15 are flags the two routing primitives set; their
    /// meanings are not traced.</summary>
    public uint Flags { get; set; }
    /// <summary>Go To Relative Position operand byte 6 bit 2; FUN_004549b6 passes it separately to FUN_004550e0.</summary>
    public bool CheckCandidateFootprintAgainstWall { get; set; }
    /// <summary>+0x20: the slot the snap goes to, -1 for none (inferred name).</summary>
    public int SnapTargetSlot { get; set; } = -1;
    /// <summary>+0x24: the minimum proximity (inferred name and order: the three proximities follow the table's order,
    /// minimum, maximum, optimal).</summary>
    public int MinProximity { get; set; } = 16;
    /// <summary>+0x28: the maximum proximity (inferred, as <see cref="MinProximity"/>).</summary>
    public int MaxProximity { get; set; } = 16;
    /// <summary>+0x2c: the optimal proximity (inferred, as <see cref="MinProximity"/>).</summary>
    public int OptimalProximity { get; set; } = 16;
    /// <summary>+0x30: the gradient the route allows (the table's default, read as a float).</summary>
    public float Gradient { get; set; } = 0.1875f;
    /// <summary>+0x34: facing from FUN_00483fc1: -1 faces opposite the anchor, -2 faces back toward the anchor
    /// from the chosen approach side (the default), -3 preserves current facing, and 0 to 7 are relative to the anchor.</summary>
    public int Facing { get; set; } = -2;
    /// <summary>+0x38: the resolution (the table's default).</summary>
    public int Resolution { get; set; } = 16;

    /// <summary>FUN_0046dea8 copies the type-3 SLOT coordinates and routing fields into the 0x3c-byte route vector,
    /// inserting -1 in the unmodeled dword at +0x0c.</summary>
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

/// <summary>Raw dwords from one 0x3c-byte per-object runtime record. Only offsets touched by FUN_0046e120 are named;
/// other fields remain available by byte offset until their meanings are traced.</summary>
public sealed class ObjectRuntimeSlot
{
    private readonly int[] _words = new int[15];

    /// <summary>The master ObjSelector key represented by +0x18 after SetSprite(ObjSelector*). The native pointer is
    /// process-specific and is retained as a type-record key in the headless model.</summary>
    public int? SelectorTypeKey { get; private set; }

    /// <summary>Reads or writes one aligned dword in the original 0x3c-byte record.</summary>
    public int ReadWord(int byteOffset) => _words[WordIndex(byteOffset)];
    public void WriteWord(int byteOffset, int value) => _words[WordIndex(byteOffset)] = value;

    /// <summary>SpriteSlot::SetSprite(cRenderer*, spriteID, frameCount, backwards), followed by the balloon,
    /// optional label and flag stores in cXObject::TrySetBalloon. The renderer pointer is not meaningful in the
    /// headless world, so +0x18 remains zero; ordinary sprite draws select by +0x14 and never dereference it.</summary>
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

    /// <summary>SpriteSlot::SetSprite(ObjSelector*): select the object's master type icon rather than an SPR# id.</summary>
    public void SetSelectorSprite(int typeKey)
    {
        SelectorTypeKey = typeKey;
        WriteWord(0x14, -1);
        WriteWord(0x18, 0);
        WriteWord(0x1c, 1);
        WriteWord(0x20, 0);
        WriteWord(0x24, 1);
    }

    /// <summary>SpriteSlot::ActivateForTicks: reset the animation phase and set its remaining tick count.</summary>
    public void ActivateForTicks(int ticks)
    {
        WriteWord(0x28, 0);
        WriteWord(0x10, ticks);
    }

    /// <summary>SpriteSlot::ActivateForLoops: reset the animation phase and set the remaining tick count to the
    /// requested number of loops times the record's frame count at +0x1c, with twelve ticks per frame.</summary>
    public void ActivateForLoops(int loops)
    {
        WriteWord(0x28, 0);
        WriteWord(0x10, unchecked(loops * ReadWord(0x1c) * 12));
    }

    /// <summary>SlotLoader::Load initializes the runtime record for a type-1 SLOT entry from its X/Y/Z fields and
    /// writes its one-based position in the complete SLOT table at +0x0c.</summary>
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

    /// <summary>FUN_0046e120: decrements a positive +0x10 counter, then advances the periodic +0x20 value every
    /// twelve calls while that counter remains nonzero. A rollover requests a display refresh.</summary>
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
/// <summary>The Special Effect primitive's request to the host: the duration in milliseconds (the variable's value times
/// 1000) and operand bytes 2 to 5, which the playback reads (their meanings are not traced in the port).</summary>
public readonly record struct SpecialEffectRequest(int DurationMs, byte Byte2, byte Byte3, byte Byte4, byte Byte5);

/// <summary>Native Find Best Interaction inputs that remain external. Hydrated TTAB candidates and guards are evaluated by the
/// engine; the legacy interaction-record source is used only when a candidate type record has no interaction table.</summary>
public interface ISimulationAutonomyHost
{
    AutonomyLot AutonomyLotState();
    float ScoreAutonomy(XPerson person, XObject target, short actionIndex, string actionName, float score) => score;
}

/// <summary>What the variables reach outside the objects and that this stage does not model yet. Each method names
/// the Sims.exe function it stands for.</summary>
/// <summary>Original career table and CARR fields consumed by FUN_00404ccb/FUN_00403647.</summary>
public interface ISimulationCareerHost
{
    /// <summary>FUN_00404CC5's ordered career ids, read by Set to Next mode 10.</summary>
    IReadOnlyList<int> CareerTable();
    /// <summary>FUN_00404ccb: whether an id is present in DAT_006DE7F8..DAT_006DE7FC.</summary>
    bool CareerExists(short career);
    /// <summary>FUN_00403647: a CARR field at a career level, or null when source lookup fails/needs an unmodelled field.</summary>
    short? JobData(short career, short level, short field);
}

public interface ISimulationHost : ISimulationAutonomyHost, ISimulationCareerHost
{
    /// <summary>FUN_0040235e: the constants of BCON <paramref name="id"/> in the scope of the running tree, or null.</summary>
    IReadOnlyList<short>? Constants(StackFrame frame, ushort id);
    /// <summary>FUN_0044aa97: whether Sleep also writes its counter to the object's wait cell on this tick (the other
    /// case only counts the counter down). Only the person case is traced: a person (definition type 2) answers false.
    /// The other fields it reads are not modelled, so for other objects the host answers (inferred).</summary>
    /// <summary>FUN_004573f9: the alternative type entry cached at +0x8c of <paramref name="entry"/>. FUN_0043aeef
    /// finds it when none is cached yet; null when there is none.</summary>
    TypeRecord? AlternativeObjectType(TypeRecord entry);
    /// <summary>FUN_004670a4 and the room queries after it: <paramref name="field"/> 0 to 3 of room
    /// <paramref name="room"/>, or null when there is no such room. Only the score (0) and the half area (3) reach
    /// this method from a world with native room records; the outside flag (1) and group (2) are
    /// <see cref="Variables"/>' native FUN_00468851/FUN_00468838 projections.</summary>
    short? Room(short room, short field);
    /// <summary>FUN_00470383 then FUN_004705f2: entry <paramref name="index"/> of string table <paramref name="table"/>,
    /// or null when there is none. Show String and Run Tree By Name read their text through it.</summary>
    string? StringTableEntry(short table, int index);
    /// <summary>FUN_0040766a(<paramref name="message"/>, <paramref name="text"/>): the message goes to the registered
    /// listeners (the game's UI). Show String sends 0xeb (inferred: a line of text for the UI).</summary>
    void BroadcastString(int message, string text);
    /// <summary>FUN_00582e2a(bank, <paramref name="eventId"/>): whether sound bank <paramref name="bank"/> accepted the
    /// event. Bank 0 is FUN_00401f6f() and bank 1 is FUN_00401f73() (inferred from the order Play Sound Event tries them).</summary>
    bool PlaySoundEvent(int bank, short eventId);
    /// <summary>FUN_0057a7f7: plays the positional part of a sound event whose owner has id <paramref name="ownerId"/>.
    /// Its name argument (a local of FUN_00452862) is not traced. Returns whether it played.</summary>
    bool PlayPositionalSound(short ownerId);

    // Refresh (0x451d3f): the display and notice side effects.
    /// <summary>FUN_004341a6 with argument 0: the display refresh that Refresh (selector 0) and Set Balloon/Headline end
    /// with.</summary>
    void RefreshDisplay();
    /// <summary>FUN_0040766a: sends notice <paramref name="message"/> with <paramref name="value"/> to the notice
    /// listeners. Answers whether any listener answered; Refresh ignores it.</summary>
    /// <summary>FUN_00466bdf: refreshes <paramref name="room"/> (Refresh selector 2 passes the subject's Room field).
    /// The table it works in is not traced (inferred: the object list's +0x24).</summary>
    void RefreshRoom(ushort room);

    // Burn (0x44d9af): the lot side of the fire.
    /// <summary>FUN_00432644: whether Burn runs at all (the flag comes from FUN_00432664; not traced).</summary>
    bool BurnEnabled();
    /// <summary>FUN_0043acf5: the object type with <paramref name="key"/>, or null. Burn passes the Fire key 0x24C95F99.</summary>
    TypeRecord? FindObjectType(int key);
    /// <summary>The lot's size in tiles (DAT_006e54dc+0x10; the name is inferred from its use as the bound of the tile
    /// test, which Burn compares with size-1).</summary>
    /// <summary>The tile check of Burn's mode 1: the tile mask FUN_00479737 gives for the tile, tested against the
    /// direction mask at DAT_006f4640 and FUN_00435221 (not traced). True when the tile passes.</summary>
    bool BurnFootprintClear(int tileX, int tileY, int height, short direction);
    /// <summary>The lot side of Burn after its own checks. Sims.exe: the tile query FUN_0043f371 (a fire on the tile, or
    /// an object with flag 0x800, answers false), the burnable object of the tile (flag 0x20), the closure over the
    /// neighbouring tiles (FUN_0043f371 again, with the eight offsets at DAT_006c2924 and DAT_006c2948), the person
    /// burn, and the Fire placement through FUN_00440b6d and the type's placement entries. When there is no burnable
    /// object, a Fire is placed at <paramref name="position"/>. True when a Fire was placed.
    /// <para><paramref name="strictFlags"/> is operand byte 1 bit 0 clear; then the closure also answers false for an
    /// object with flag 0x20 or a positive Data[62] (inferred field name).</para></summary>
    bool BurnSpread(XObject me, ObjectPosition position, TypeRecord? fire, bool strictFlags);

    // UI Effect (0x44ad3f).
    /// <summary>FUN_004765df: the effect on the object with <paramref name="id"/> (flag is the effect's on/off).</summary>
    void SetObjectUiEffect(short id, bool flag);
    /// <summary>FUN_00476a13: the effect on the object with <paramref name="id"/>, for the object list (inferred: its
    /// FUN_0042573e lookup of the id is not traced).</summary>
    void ShowObjectUiEffect(short id, bool flag);
    /// <summary>FUN_00476afb: the effect on the entry with <paramref name="id"/> (inferred: the FUN_0042573e lookup of the
    /// id is not traced).</summary>
    void ShowEntryUiEffect(short id, bool flag);

    // Set Balloon/Headline (0x44b764). The balloon records of an object are its balloon list (+0x148, 0x3c bytes each);
    // the primitive works on the first one, the entry.
    /// <summary>Whether <paramref name="owner"/> has any balloon record (the list at +0x148 is not empty).</summary>
    bool HasBalloonEntries(XObject owner);
    /// <summary>Claims the first balloon record of <paramref name="owner"/> for a balloon of <paramref name="priority"/>.
    /// Answers false, and changes nothing, when the record is active (+0x10 is not 0) and the new priority is lower than
    /// its own (+0x34). Otherwise it sets +0x34 to the priority and answers true.</summary>
    bool ClaimBalloon(XObject owner, int priority);
    /// <summary>FUN_0046e15e on the first balloon record: clears +0x28 and sets +0x10 to <paramref name="value"/>.</summary>
    void SetBalloonState(XObject owner, int value);
    /// <summary>FUN_0046e16c on the first balloon record: clears +0x28 and sets +0x10 to the record's count (+0x1c) times
    /// <paramref name="count"/> times 12.</summary>
    void SetBalloonStateTimed(XObject owner, int count);
    /// <summary>Runs the remaining +0xb00 reference-list and interaction cleanup from cXPerson::FUN_0045e8fd
    /// (vtable +0x38) for a broadcast subject. The native caller already manages the bit-11 guard, queued-action
    /// compaction and current/previous action-record changes.</summary>
    void NotifyPersonObjectReference(XPerson recipient, XObject subject);
    /// <summary>FUN_0046e105 on the first balloon record, with <paramref name="alternative"/> as the value. The value is
    /// FUN_004573f9 of the object's type record (null when there is none).</summary>
    void ShowBalloonForType(XObject owner, TypeRecord? alternative);
    /// <summary>FUN_0042573e on the object list's +0x24 (inferred: the list is not modelled), then the entry's +8
    /// field. Zero when the index is out of range or the entry is absent.</summary>
    int ListEntryValue(short index);
    /// <summary>FUN_0046e105 on the first balloon record, with <paramref name="value"/> as the value (Set Balloon/Headline
    /// selector 1, the +8 field of <see cref="ListEntryValue"/>).</summary>
    void ShowBalloonForValue(XObject owner, int value);
    /// <summary>The text of a balloon: FUN_004169e6 selects the string table, FUN_00416a49 looks up
    /// <paramref name="textId"/> there, FUN_004c8f58 gives its length, and FUN_0046e0d1 shows the text, one entry per three
    /// characters (the flags word is passed through as Sims.exe builds it). Answers false, and shows nothing, when the
    /// id has no text or the text is shorter than three characters. The strings are released afterwards (FUN_00416a38).</summary>
    bool ShowBalloonText(byte table, int textId, int flags);
    /// <summary>FUN_004169e6 selects the string table, FUN_00416a49 looks up <paramref name="index"/>, and the strings are
    /// released (FUN_00416a38). The label of the balloon (flag 0x200 of the operand uses table 1, index 3).</summary>
    short BalloonString(byte table, short index);
    /// <summary>FUN_00416b9f: the duration a balloon of <paramref name="kind"/> gets (operand byte 6).</summary>
    int BalloonDuration(byte kind);
    /// <summary>Writes the balloon fields of the first record of <paramref name="owner"/>: +0x2c duration, +0x30 label and
    /// +0x38 flag (the flag is operand byte 7 bit 0).</summary>
    void SetBalloonFields(XObject owner, int duration, int label, bool flag);
    /// <summary>FUN_0044578b with the owner's object table (+0xdc) and the owner's id.</summary>
    void NotifyObjectTable(XObject owner);

    // Grab (0x451e1a), Drop (0x454470) and Drop Onto (0x44e9aa). Each calls two virtuals on the object it acts on: slot
    // +0x54 is the check and slot +0x58 the action. Both get the position (X, Y and height), the other object (null for
    // none) and a flag; the action also gets a trailing 1, which the port does not pass on (inferred: a "do it" flag).
    // The holding, slot and display bookkeeping is the host's.
    /// <summary>Grab's check (vtable +0x54 of the stack object) with the grabber's position and height, the grabber and flag 0.</summary>
    bool CanGrab(XObject item, ObjectPosition at, XObject? who, int flag);
    /// <summary>Grab's action (vtable +0x58 of the stack object), with the same arguments as <see cref="CanGrab"/>.</summary>
    void Grab(XObject item, ObjectPosition at, XObject? who, int flag);
    /// <summary>Drop's check (vtable +0x54 of the held object) at the footprint in front of the running object, with its
    /// height, no other object and flag 0.</summary>
    bool CanDrop(XObject item, ObjectPosition at, XObject? who, int flag);
    /// <summary>Drop's action (vtable +0x58 of the held object), with the same arguments as <see cref="CanDrop"/>.</summary>
    void Drop(XObject item, ObjectPosition at, XObject? who, int flag);
    /// <summary>Drop Onto's check (vtable +0x54 of the object in the slot) at (-16, -16) with height 1, the stack object
    /// and the operand's flag value.</summary>
    bool CanDropOnto(XObject target, ObjectPosition at, XObject? who, int flag);
    /// <summary>Drop Onto's action (vtable +0x58 of the object in the slot), with the same arguments as <see cref="CanDropOnto"/>.</summary>
    void DropOnto(XObject target, ObjectPosition at, XObject? who, int flag);

    // Dialog (0x451592).
    /// <summary>FUN_0044439c on the lot table's dialog list (+0x64): the state (+0x74) of the record whose first field is
    /// <paramref name="owner"/>, or null when there is none. Inferred: the record is the one the lookup returns.</summary>
    int? DialogRecordState(XObject owner);
    /// <summary>FUN_004370fd: transitions a specific owner dialog record to state 5 and releases its UI manager state.</summary>
    void DismissDialogRecord(XObject owner);
    /// <summary>FUN_00444c08 scans the lot dialog list and runs FUN_004370fd for each record owned by this object.</summary>
    void DismissDialogRecords(XObject owner);
    /// <summary>Creates and presents the 0x468-byte dialog selected by FUN_00510867, then runs FUN_004F6C41 and FUN_005DA4C7.</summary>
    void ShowDialog(XPerson owner, byte style);
    /// <summary>The byte at DAT_006f2a08 + 0xf8 used by Dialog case 0xd (FUN_00451592).</summary>
    byte DialogGlobalFlagF8();
    /// <summary>The dialog record FUN_0044439c finds for <paramref name="owner"/>, with the fields FUN_00451592 reads when it
    /// resumes (kind, response text). The default is the world's own record list, which a host that keeps no records of its
    /// own shares with the native client.</summary>
    SimulationDialogRecord? DialogRecordFor(SimulationWorld world, XObject owner) => world.DialogRecord(owner);
    /// <summary>FUN_004443eb: creates a generic message record (FUN_004360c1) for a Dialog instruction and registers it with
    /// the lot's dialog list. The default registers it in the world's record list.</summary>
    SimulationDialogRecord? CreateDialogRecord(SimulationWorld world, XObject owner, XObject? icon, string title, string message,
        string? yes, string? no, string? cancel, byte kind, bool blocking, bool awaitsResponse)
        => world.CreateMessageDialog(owner, icon, title, message, yes, no, cancel, kind, blocking, awaitsResponse);
    /// <summary>Retires a record whose response the instruction has consumed.</summary>
    void CompleteDialogRecord(SimulationWorld world, SimulationDialogRecord record) => world.CompleteDialogRecord(record);
    /// <summary>Whether a dialog is on screen (lot +0x7c): the instruction creates no record while one is.</summary>
    bool HasVisibleDialog(SimulationWorld world) => world.HasVisibleDialog;
    // Look Towards (FUN_0045aafa).
    /// <summary>FUN_0058a039: the head look target of <paramref name="me"/> becomes object <paramref name="target"/>, or none
    /// when it is 0 (the look record at +0x138 is not modelled).</summary>
    void SetLookTarget(XObject me, short target);
    /// <summary>The facing a Look Towards tick starts: the point to turn to, or null for mode 1 (the front of the lot, inferred).
    /// The rotation and facing calls (FUN_0047bd91, FUN_00422055, FUN_0045f7c5) are not traced.</summary>
    void TurnTowards(XObject me, ObjectPosition? point);
    /// <summary>FUN_00589d4b: the state of the turn of <paramref name="me"/> as the primitive reads it: 0 for nothing to turn,
    /// 1 for a turn in progress, 2 for busy (the primitive then waits). The meanings are inferred from how the answer is used.</summary>
    int TurnStatus(XObject me);
    /// <summary>FUN_00589e31: starts a turn on <paramref name="me"/> and answers true, or answers false while a turn is
    /// running (the busy flag at +0x284 is not modelled).</summary>
    bool TryStartTurn(XObject me);

    // Go To Relative Position (FUN_0045c7a0), Go To Routing Slot (FUN_0045ad3a) and the route they both run (FUN_0045aecf).
    /// <summary>FUN_00432644: whether routing runs. Go To Routing Slot answers false, and changes nothing, when it does not.
    /// This is the function <see cref="BurnEnabled"/> stands for, so both read the same flag (inferred from the use; the
    /// flag comes from FUN_00432664, not traced).</summary>
    bool RoutingEnabled();
    /// <summary>The number of routing slots of <paramref name="stackObject"/>: (+0x140 - +0x13c) / 0x3c.</summary>
    int RoutingSlotCount(XObject stackObject);
    /// <summary>The routing slot at <paramref name="index"/> of <paramref name="stackObject"/> (+0x13c + index * 0x3c). The
    /// host keeps the slot, so a change made to it stays for later runs.</summary>
    RoutingSlotRequest RoutingSlotAt(XObject stackObject, int index);
    /// <summary>The number of routing slots in the table at +0xdc of <paramref name="me"/>: (+0xa0 - +0x9c) / 0x3c, as Go To
    /// Routing Slot mode 2 reads it. Inferred: the table is the lot's object table (see <see cref="ObjectTableEntry"/>).</summary>
    int LotRoutingSlotCount(XObject me);
    /// <summary>The routing slot at <paramref name="index"/> of the table at +0xdc of <paramref name="me"/> (+0x9c + index * 0x3c).</summary>
    RoutingSlotRequest LotRoutingSlotAt(XObject me, int index);
    /// <summary>FUN_0045aecf: runs the route of <paramref name="me"/> for <paramref name="frame"/>. A null target continues the
    /// route the frame is waiting on (its state is in the frame's +8 dword, kept as <see cref="StackFrame.Waiting"/>);
    /// otherwise the route starts toward <paramref name="target"/>. Answers the handler's result: 0 False, 1 True,
    /// 2 Stop or 3 Again (the route's own state machine is not traced).</summary>
    PrimitiveResult Route(XObject me, StackFrame frame, RoutingSlotRequest? target);

    // Animate Sim (0x2c): the avatar's slot 0x28.
    /// <summary>FUN_0058950f, slot 0x28 of the avatar at +0xadc of <paramref name="me"/>: the avatar's Animate, which the person
    /// dispatcher FUN_0045cd1f calls with the frame and the operand. The avatar is built by FUN_00587d5c (vtable 0x671ee4).
    /// Answers the avatar's result, which the primitive passes on unchanged (the animation state machine is not traced).</summary>
    PrimitiveResult AnimateAvatar(XPerson me, StackFrame frame, ReadOnlySpan<byte> operand);
    // Stop All Sounds (0x45257a).
    /// <summary>FUN_0057a9a6 with the owner's id: stops the sounds that owner plays on the sound manager (DAT_006e486c+0x10).
    /// Only the owner is named (inferred: the sound manager keeps which sounds each owner plays).</summary>
    void StopOwnerSounds(short ownerId);

    // Notify Stack Object Out Of Room (0x452199).

    // Change Action String (0x44cc47).
    /// <summary>The action update, after the text is looked up (FUN_00470383 and FUN_004705f2, the same lookup as
    /// <see cref="StringTableEntry"/>). FUN_005b870f sets the action text; FUN_004588d7 tells whether the action display is up,
    /// and FUN_004588f5 sends the text and the target to it when it is. FUN_0044bf5a builds the frame's action record (through
    /// FUN_0044cb36 and FUN_00455800, not traced), and FUN_0041f1b0 marks a person when the display is down and DAT_006c4d5c is
    /// set. <paramref name="text"/> is null when the table has no entry (Sims.exe does not check; inferred: nothing shows).
    /// Inferred: the target is <paramref name="stackObject"/> when the record names none.</summary>
    void ChangeActionString(XObject me, short stackObject, string? text);

    // Snapshot (0x4549b6).
    /// <summary>The search and the result of Snapshot modes 1 and 2, given the position the primitive worked out. For mode 1
    /// <paramref name="target"/> is the stack object, and for mode 2 it is null. <paramref name="directionMask"/> is the running
    /// object's direction and 7. <paramref name="slotFlag"/> is operand byte 4 bit 2. <paramref name="relative"/> is the
    /// difference from the running object's position (Y, then X) and the direction difference, which operand byte 4 bit 0 asks
    /// for; null without that bit.
    /// <para>Sims.exe: for mode 1 the stack object's first slot entry, turned by its direction, adjusts the relative values
    /// (FUN_005c3840 and FUN_00454ff9 are not traced), then FUN_004550e0 runs the search with the position, the target, the
    /// mask and the slot flag. Its answer is the primitive's. A person (definition type 2) whose answer is 1 has its avatar
    /// (+0xadc, vtable +0x2c) given the relative values, and the running object's flags at +0xb2 and +0xb6 are set from the
    /// search's globals DAT_006e4a7c and DAT_006e4a80. All of this is inferred to be the host's; none of it is traced here.</para></summary>
    /// <summary>FUN_004550e0 search and its arguments. <paramref name="snapTargetSlot"/> and the raw slot flag bit 0x800
    /// are passed separately from the operand's <paramref name="slotFlag"/> (bit 2).</summary>
    PrimitiveResult RunSnapshot(XObject me, XObject? target, ObjectPosition position, int snapTargetSlot, bool snapTargetFlag,
        int directionMask, bool slotFlag, (int Dy, int Dx, int Ddir)? relative);

    // Special Effect (0x44aae6).
    /// <summary>FUN_0046dae0(index, value): writes the word at the table's +0x10 + 2 * index (inferred: the effect's word of the
    /// lot's table; the primitive writes index 11 and value 1 when it starts).</summary>
    void SetEffectWord(short index, short value);
    /// <summary>The effect's gate and playback, after the value is read. Sims.exe: FUN_004cffb7 (a flag byte at +4) and
    /// FUN_004074bc (FUN_0042ba25 on the object's id at +0x24) decide the gate, which operand byte 4 bit 3 skips. Then FUN_005b8680,
    /// FUN_0040766a(0x10c, 0) when byte 4 bit 6 is clear, the string of byte 5 (FUN_005d4bbf), FUN_0044bf5a, and FUN_004a6b68 plays
    /// the effect. Inferred: all of this is the host's. Answers whether the gate passed and the effect started (false makes the
    /// primitive answer true at once, as Sims.exe does).</summary>
    bool PlaySpecialEffect(XObject me, XObject stackObject, SpecialEffectRequest request);
    // Push Interaction (0x44cd6b). Only the immediate-run and explicit queue-replacement branches remain host-backed.
    /// <summary>FUN_0041f010 builds an action record for <paramref name="interactionId"/> of
    /// <paramref name="owner"/> and FUN_0045d377 inserts ordinary queued actions by priority at record +0x1c.
    /// The native world emits pending displacement notices; the host must deliver them through FUN_00422d79.
    /// <para><paramref name="secondary"/> is the record's +0xc object, null when none. <paramref name="flags"/> is the
    /// record's +0x40 word, with 0x2 (operand byte 3 bit 2) and 0x40 (bit 3) only (inferred: FUN_0045d377 reads those
    /// bits and the port does not trace the rest of the word).</para></summary>
    bool PushInteraction(XPerson person, XObject owner, byte interactionId, int priority, XObject? secondary, int flags);

    // Find Best Object for Function (0x44f2ff).
    // Make New Character (0x44ae48).
    /// <summary>FUN_0042612b with <paramref name="mode"/> (operand byte 3): finds a free character file, creates the
    /// character and answers it; null when it fails (Sims.exe answers -1 or no record). Inferred: the mode picks the
    /// character folder (1 and 2 pick two of them, any other mode the first).</summary>
    XObject? CreateCharacter(int mode);

    // Generic Sims Call (0x44f7e6).
    /// <summary>FUN_004E9918 with argument <paramref name="position"/> (GenericSimsCall calls 0x12/0x1a/0x20:
    /// the active-person table index at that position, or -1 when there is none. What the table holds is the
    /// host's).</summary>
    int ActivePersonIndex(int position);
    /// <summary>FUN_004E9744 of the <paramref name="index"/>-th active-person entry (GenericSimsCall call 0x12:
    /// the +0xF0 word of its record, or null when it has no record. What the record is is the host's).</summary>
    short? ActivePersonRecordId(int index);
    /// <summary>The +0x10C word of the +0x10 object at DAT_006E486C (GenericSimsCall call 0x1a: the stored slot;
    /// null when that object is missing, and the call resolves the active-person record instead. What the slot
    /// is is the host's).</summary>
    short? StoredSlotPersonId();
    /// <summary>FUN_00409377 for GenericSimsCall 0x18: performs the context/global setup side effects and returns
    /// a managed representation of the FUN_004580CC(0) runtime-context pointer stored at scope +8. The native caller
    /// initializes the scope's type record and mode.</summary>
    InteractionBehaviorRuntimeContext? BeginInteractionBehaviorScope(InteractionBehaviorScope scope);
    /// <summary>The lazy action-array getter for one of the six lists at scope +0x7c..+0x90 (GenericSimsCall 0x18).
    /// Null corresponds to the original getter's zero count.</summary>
    InteractionBehaviorActionList? InteractionBehaviorActions(InteractionBehaviorScope scope, int listIndex);
    /// <summary>FUN_004094F4(scope, commit): true runs the interaction completion effects; false frees only the context.</summary>
    void EndInteractionBehaviorScope(InteractionBehaviorScope scope, bool commit);
    /// <summary>The type-record callbacks FUN_0045741B/FUN_004E9205/FUN_004EB208 at the common 0x18 tail.</summary>
    void FinalizeInteractionBehaviorTypeRecord(TypeRecord? typeRecord);
    /// <summary>FUN_00458278/FUN_004EB227 type-record refresh at the GenericSimsCall 0x1e/0x1f tail.</summary>
    void RefreshInteractionBehaviorTypeRecord(TypeRecord? typeRecord);
    /// <summary>Invokes DAT_006F2A08 +0xb0 with 0x20000000 at the common 0x18 tail.</summary>
    void NotifyInteractionBehaviorGlobal(uint mask);
    /// <summary>Resolves one position through FUN_004102AB, FUN_004E988F, FUN_004E9855 and FUN_004E9744 after the active-person path misses.</summary>
    XPerson? GenericSimsCall20FallbackPerson(int position);
    /// <summary>Runs GenericSimsCall 0x20's route search, vtable +0x54 placement check, +0x88 placement action and +0x74 completion for <paramref name="person"/>. The lot topology and object virtuals remain host effects.</summary>
    void GenericSimsCall20FinalizePerson(XPerson person);
    /// <summary>Runs GenericSimsCall 0x01's paired route/placement operation: two objects receive the original
    /// temporary route and footprint virtual calls, using their associated table objects. This includes the
    /// vtable +0x50/+0x54/+0x88 calls and the route-position writes at +0x2c, all of which depend on game objects.</summary>
    /// <summary>Runs GenericSimsCall 0x01's vtable +0x50/+0x54/+0x88 checks and actions before its native +0x4c
    /// word exchange (Sims.exe 0x44f96d-0x44f9dc).</summary>
    void RunGenericSimsCall01InteractionPhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer);
    /// <summary>Runs GenericSimsCall 0x01's animation +0x20/+0x2c calls after its native +0x4c word exchange
    /// (Sims.exe 0x44f9f4-0x44fb72).</summary>
    void RunGenericSimsCall01RoutePhase(XObject runner, XObject stackObject, XObject runnerContainer, XObject? stackContainer);
    /// <summary>Runs FUN_00446F29 on [DAT_006E486C]+0x14 with the resolved object.</summary>
    void GenericSimsCall22Finalize(XObject resolved);
    /// <summary>Resolves the scope entry's second dword through FUN_004e988f/FUN_004e9744, then the
    /// FUN_004e9855 fallback and a second FUN_004e9744 lookup; returns the resolved record's +0xf0 word,
    /// or null when both lookups fail (GenericSimsCall 0x13/0x14). The argument is the scope entry's second dword.</summary>
    short? ScopedNeighborPersonRecordId(int personRecordKey);
    /// <summary>Persists neighborhood and neighbor records through FUN_00425316 for the current neighborhood.</summary>
    void PersistNeighborhoodState();
    /// <summary>The optional callback target stored in DAT_006F3ADC (GenericSimsCall 0x11).</summary>
    XObject? GenericSimsCall11CallbackTarget();
    /// <summary>FUN_005DA398 with DAT_006FE8F4 as receiver and the callback target as argument.</summary>
    bool PassesAutonomyCallbackGate(XObject target);
    /// <summary>Invokes the callback target's vtable slot 0 with the four-word request.</summary>
    void InvokeGatedObjectCallback(XObject target, GatedObjectCallbackRequest request);
    /// <summary>Calls vtable +0x18 on [DAT_006E486C+0x48], then FUN_004B46A0 on its result (the +0x2c callback target), or null (GenericSimsCall 0x06).</summary>
    XObject? GenericSimsCall06CallbackTarget();
    /// <summary>Runs FUN_0045A399 on the resolved running object with its four temporaries; its record and lot effects
    /// remain host-owned.</summary>
    void RunGenericSimsCall11Finalize(XObject resolved, short temporary0, short temporary1, short temporary2, short temporary3);
    /// <summary>Whether the lot table at [DAT_006E486C]+0x24 exists, the gate before either async request helper.</summary>
    bool GenericSimsCallRequestManagerAvailable();
    /// <summary>Runs the part of FUN_0042DB93 (kind 0) or FUN_0042DF00 (kind 1) after the lot-vector key lookup has
    /// resolved <paramref name="target"/>. Kind 0 returns both updated words; kind 1 success returns its target's
    /// type-record key, which the native world resolves through FUN_0044097D. The native world also applies the
    /// kind-1 success write that clears temporary 0.</summary>
    GenericSimsCallRequestCompletion ProcessGenericSimsCallRequest(GenericSimsCallPendingRequest request, XObject target);
    /// <summary>Runs FUN_005DA4C7 on the opaque +0x7c handle released by FUN_004FC469.</summary>
    void ReleaseGenericSimsCall03PrimaryHandle(int handle);
    /// <summary>Runs FUN_005E3581 on the opaque +0x78 handle released by FUN_004FC469.</summary>
    void ReleaseGenericSimsCall03SecondaryHandle(int handle);
    /// <summary>Runs FUN_00429CC7 on the global object's +0x24 subobject with argument 0, 1 or 2
    /// (GenericSimsCall 0, 8, 9). The helper's initialization, manager and block-file effects remain host-owned;
    /// argument 0 additionally runs the IMISSUOH block pass.</summary>
    void RunGenericSimsCallInitialization(int mode);
    /// <summary>Fallback for GenericSimsCall 0x26 when a frame does not use the native <see cref="Content.BehaviorScope"/>:
    /// resolves the frame-table selector through FUN_004754D8 and table vtable slot 0, then returns the requested
    /// one-based string's original bytes through FUN_00470383/FUN_004705F2. Null means the string lookup missed.</summary>
    byte[]? GenericSimsCall26StringBytes(StackFrame frame, short selector, short oneBasedIndex);
    /// <summary>FUN_0044668C: updates or tears down the selected object's UI representation.</summary>
    void SelectedPersonChanged(XObject person);
    /// <summary>The byte at DAT_006DE81C + <paramref name="index"/> (GenericSimsCall call 0x0c: FUN_00434FFD reads
    /// the transformed tile bytes as a pair from here. What the table means is not traced).</summary>
    sbyte TileTransformByte(int index);
    /// <summary>The dword table at +0x11c of the global object manager for Set to Next mode 11, in order. The
    /// primitive walks it past the current target and takes the next entry's low word. What the table lists is
    /// not traced.</summary>
    IReadOnlyList<int> GlobalIdTable();
    // Create New Object Instance (FUN_0044e108).
    /// <summary>FUN_00440b6d on the type with <paramref name="guidKey"/>: creates the object and answers it; null
    /// when the type is missing or the creation fails (Sims.exe answers 0). The primitive registers the answer in
    /// the world itself.</summary>
    XObject? CreateObjectInstance(int guidKey);
    /// <summary>The tile scan of Create Object's duplicate and empty checks (FUN_0043f371 over the tile, then
    /// FUN_0043f3b9): whether the tile (<paramref name="tileX"/>, <paramref name="tileY"/>, <paramref name="level"/>)
    /// holds an object of type <paramref name="guidKey"/>, or any object when <paramref name="guidKey"/> is null.
    /// Byte 5 bit 1 answers false on the first, bit 3 on the second.</summary>
    bool TileHasObject(int tileX, int tileY, int level, int? guidKey);
    /// <summary>The room of the tile (<paramref name="tileX"/>, <paramref name="tileY"/>) on
    /// <paramref name="level"/>, as Create Object's room check reads it (0 when the tile has no room).</summary>
    short RoomAtTile(int tileX, int tileY, int level);
    /// <summary>FUN_00435a8f on the tile with <paramref name="facing"/>: whether the direction may be placed there.
    /// Create Object answers false when its bit 0 is set. What the bits test is not traced; inferred to be the
    /// host's placement facing rule.</summary>
    bool FacingClear(int tileX, int tileY, int level, int facing);
    /// <summary>The placement check of Create Object (vtable +0x54 of the created object) at <paramref name="at"/>,
    /// with <paramref name="relativeTo"/> (the placement relative, null when the mode names none) and
    /// <paramref name="method"/> (operand modes 1 and 2 pass 0, 10 to 14 pass 2 to 4). Same shape as the Grab and
    /// Put checks; the receiver here is the created object.</summary>
    bool CanPlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method);
    /// <summary>The placement action of Create Object (vtable +0x58 of the created object), with the same arguments
    /// as <see cref="CanPlaceCreated"/> (the trailing 1 is not passed, as with Grab and Put).</summary>
    void PlaceCreated(XObject created, ObjectPosition at, XObject? relativeTo, int method);
    /// <summary>FUN_0044127f on the created object: removes it at once when its placement is refused.</summary>
    void DiscardObject(XObject created);
    /// <summary>The direction copy of Create Object (operand byte 5 bit 5) for a created object that is not a
    /// person: the rotation through vtable +0x4c that turns it toward the stack object's masked direction. The
    /// primitive sets Data[Direction] itself; the host owns the visual side.</summary>
    void RotateCreatedObject(XObject created);
    /// <summary>The person bind of Create Object (FUN_0042597a) with the avatar-state notices (FUN_00458278(1) and
    /// FUN_004cee86(0x20000000)): the flag bytes at +0x614, +0x620 and +0x634 it tests are not modelled, so the
    /// host applies the whole step.</summary>
    void InitCreatedPerson(XPerson created);
    /// <summary>The arrival scheduling of Create Object for a created person with a nonzero PersonType when operand
    /// byte 5 bit 2 is set (FUN_0046f824, FUN_0046f847, FUN_00402246 and the tree run FUN_00474c68): the host owns
    /// the visitor tables and the tree, so it performs the whole step.</summary>
    void ArriveCreatedPerson(XPerson created);

    // Find Location For (FUN_0044b408).
    /// <summary>The lot search of Find Location For (FUN_00434895) for <paramref name="stackObject"/>: searches
    /// around <paramref name="parameters"/> and answers the found tile. False when there is none (the primitive
    /// then answers false). The search needs the lot topology (walkability, rooms, slopes), which the port does
    /// not model, so the host performs it.</summary>
    bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found);
    /// <summary>The placement check of Find Location For (vtable +0x54 of the stack object) at <paramref name="at"/>,
    /// with zeros for the other two words. False answers false.</summary>
    bool CanPlaceAt(XObject stackObject, ObjectPosition at);
    /// <summary>The placement action of Find Location For (vtable +0x58 of the stack object) at
    /// <paramref name="at"/>, with zeros and a trailing 1 (the trailing 1 is not passed, as with Grab and Put).</summary>
    void PlaceAt(XObject stackObject, ObjectPosition at);
    /// <summary>Mode 1's multitile alignment (FUN_0042349d on the stack object): walks its parts through the
    /// placement helpers. The result is ignored by the primitive. Inferred from the calls it makes; the host owns it.</summary>
    void AlignMultitileParts(XObject stackObject);
    /// <summary>Mode 2's turn (vtable +0x4c of the stack object) by <paramref name="steps"/> quarter steps toward
    /// the target, from the quadrant math on both directions. Inferred to be the same rotate virtual Create Object
    /// turns through; the host owns the visual side.</summary>
    void RotateForPlacement(XObject stackObject, int steps);
    /// <summary>The +0x74 step of the retried search paths, after the action. Its purpose is not traced; the host
    /// performs it.</summary>
    void FinishLocationSearch(XObject stackObject);
    /// <summary>The +0x50 step with argument 1, after every successful placement (and alone in mode 1). Its purpose
    /// is not traced; the host performs it.</summary>
    void FinalizePlacement(XObject stackObject);
    // Idle For Input (FUN_0045dd00).
    /// <summary>The queued-action guard, run bookkeeping, and person's acceptance test for Idle For Input after native
    /// FUN_0045f8e7 has copied and removed <paramref name="action"/> from the ring. Answers the object the action runs on when it is accepted, and
    /// null when there is none or it is refused. A refusal with an object shows the message FUN_00422d79 gives
    /// (inferred: the host does it, since the caller has no use for it).</summary>
    XObject? StartQueuedAction(XPerson me, ActionQueueSlot action);

    // Reach/Put (FUN_0045cb26 and FUN_0045c9b4).
    /// <summary>FUN_0043270d then FUN_00432664: the holder of <paramref name="target"/> and the value that goes with it, as
    /// Reach's mode 0 reads them. The holder is the object the mode 0 check and slot count apply to (inferred). Not traced.</summary>
    (XObject? Holder, int Value) GrabHolder(XObject target);
    /// <summary>FUN_004340f2 then FUN_00454f69: the position a slot number gives, before Reach scales it by 3 (DAT_00667344).
    /// The meaning of the position is not traced.</summary>
    float SlotPosition(int slot);
    /// <summary>The avatar of <paramref name="me"/> (its +0xadc) pops one event through vtable +0x80: answers true and the
    /// event's code when there was one, false when the queue is empty (the code is the dword the call writes).</summary>
    bool NextAvatarEvent(XPerson me, out int code);
    /// <summary>FUN_0058a004 on the avatar: whether its reach animation has finished (inferred from how the primitive
    /// reads the answer).</summary>
    bool ReachAnimationDone(XPerson me);
    /// <summary>FUN_0058a011 on the avatar: clears the reach state after the animation is done (its effects are not traced).</summary>
    void FinishReach(XPerson me);
    /// <summary>FUN_00589ef7 on the avatar with the position and flag 1: starts the reach animation. Answers whether it started
    /// (the flag is not traced: the port passes it on as Sims.exe does, as the vtable's "no flag" argument).</summary>
    bool StartReachAnimation(XPerson me, float position);
    /// <summary>The check of Put (vtable +0x54 of <paramref name="target"/>), with the held object's position and height, the
    /// held object as who, and the operand's value as flag.</summary>
    bool CanPlace(XObject target, ObjectPosition at, XObject who, int flag);
    /// <summary>The action of Put (vtable +0x58 of <paramref name="target"/>), with the same arguments as <see cref="CanPlace"/>
    /// (the trailing 1 is not passed, as with Grab).</summary>
    void Place(XObject target, ObjectPosition at, XObject who, int flag);
}

/// <summary>A found action: the table entry's key (a dword at +0x18, compared with the person's +0x72 word) and the
/// action at +2. FUN_004760ae reads TtaIndex into +0x18 and ActionTree into +2.</summary>
public readonly record struct FoundAction(int Key, short Action)
{
    public static FoundAction FromTtab(Interaction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return new FoundAction(interaction.TtaIndex, unchecked((short)interaction.ActionTree));
    }
}

/// <summary>An ordered state-to-class entry from the manager's +0x174 table (FUN_0042ba25).</summary>
public readonly record struct StateClassEntry(int State, int Class);

/// <summary>The room-owned values read by cXPerson's post-update room-motive calculation: +0x68 for an interior
/// room, or the lot manager's +0x3c value when the room is outside.</summary>
/// <summary>The runtime room-manager record found by Sims.exe FUN_004670a4. Refresh uses the room key stored at +0x00
/// and the dirty byte at +0x70; person-motive lookup uses the score at +0x68 and outside cache.</summary>
public record struct PersonRoomMotiveEntry(float InteriorValue, bool IsOutside)
{
    /// <summary>cXRoom +0x70, set by FUN_00466bdf and consumed by the room update pass.</summary>
    public bool NeedsRefresh { get; set; }
    /// <summary>cXRoom +0x6c: current room light, read before reset by FUN_00466d14/FUN_004688e4.</summary>
    public float LightLevel6c { get; set; }
    /// <summary>cXRoom +0x73: household light-enable state toggled by FUN_004688e4.</summary>
    public byte LightsEnabledFlag73 { get; set; }
    /// <summary>cXRoom +0x75: manual light override suppresses automatic person-driven toggles.</summary>
    public byte LightOverrideFlag75 { get; set; }
    /// <summary>cXRoom +0x280: count of lit lighting objects, used by the room light control.</summary>
    public int LightingObjectCount140 { get; set; }
    /// <summary>cXRoom +0x284: count of person objects accumulated during room refresh.</summary>
    public int PersonObjectCount142 { get; set; }
    /// <summary>cXRoom +0x58: eligible category-4 objects used by the original House-panel bedroom count.</summary>
    public int HouseCategory4ObjectCount58 { get; set; }
    /// <summary>cXRoom +0x5c: eligible category-5-through-7 objects used by the original House-panel bathroom count.</summary>
    public int HouseCategory5To7ObjectCount5c { get; set; }
}

public enum RoomRefreshResult
{
    /// <summary>The runtime room-manager tree has not been reconstructed for this lot.</summary>
    ManagerUnavailable,
    /// <summary>The reconstructed tree has no record with this key; Sims.exe's lookup is a no-op.</summary>
    RoomNotFound,
    /// <summary>The record dirty byte and applicable manager flag were updated.</summary>
    Refreshed,
}

/// <summary>The two global person-state bytes written by GenericSimsCall 0x11 and successful asynchronous requests
/// (Sims.exe global person state at [DAT_006E486C]+0x1c, offsets +0xe1 and +0xe0).</summary>
public sealed class GlobalPersonState
{
    public byte ByteE0 { get; set; }
    public byte ByteE1 { get; set; }
    /// <summary>The signed word at global person state +0x22 compared with the running object's +0x10c by
    /// Sims.exe FUN_00410b89.</summary>
    public short LotUpkeepMatchWord22 { get; set; }
}

/// <summary>Opaque manager fields read by FUN_004FC469 when GenericSimsCall 0x03 resets global state. Zero handles
/// mean absent. Null <see cref="LinkedGlobalWord60"/> means DAT_006E486C or its +0x2c object is absent.</summary>
public sealed class GenericSimsCall03ManagerState
{
    public int PrimaryHandle { get; set; }
    public int SecondaryHandle { get; set; }
    public int? LinkedGlobalWord60 { get; set; }
}

/// <summary>The selection effect FUN_00445489 applies after GenericSimsCall 0x2a's native record and state checks.</summary>
public readonly record struct PersonRecordSelection(XObject? Person);

/// <summary>A node in Sims.exe's synchronous FUN_0040766a listener chain. The original reads the next pointer before
/// invoking the current listener, so a callback may detach itself without skipping its former successor.</summary>
public sealed class SimulationNoticeListener(Func<int, int, bool> callback)
{
    public Func<int, int, bool> Callback { get; } = callback ?? throw new ArgumentNullException(nameof(callback));
    public SimulationNoticeListener? Next { get; set; }
}

/// <summary>The simulation the trees run in: the objects and people on the lot by id, the neighbors, the simulation
/// globals (Sims.exe DAT_006e486c+0x1c, from +0x10; named by <see cref="SimulationGlobal"/>), the neighborhood data
/// (DAT_006e486c+0x24, from +0x128) and the check tree ad table (PTR_DAT_006c436c, three shorts an entry: personality
/// variable, min, range).</summary>
/// <summary>A Sims.exe UI-listener message produced by a simulation primitive.</summary>
public sealed record SimulationUiMessage(long Sequence, int Message, string Text);

public sealed partial class SimulationWorld
{
    /// <summary>Resolves the selected avatar skeleton's source StartVitaBoy stride markers for runtime locomotion.</summary>
    public Func<XPerson, string, (float MarkerOneStride, float MarkerTwoStride)?>? LocomotionStrideResolver { get; set; }
    /// <summary>Resolves source-space actor, HeadFake, and target points for SAnimator::UpdateHeadDirection.</summary>
    public Func<XPerson, XObject, HeadSeekGeometry?>? HeadSeekGeometryResolver { get; set; }
    private static long s_uiMessageSequence;
    private readonly Dictionary<short, (StackFrame Frame, TreeStack? Stack)> _directPositionRoutes = [];
    private int _visitorAvatarAnimationKey;

    /// <summary>The active lot key used by Neighborhood::GetZoningType during TTAB interaction tests.</summary>
    public int ActiveLotNumber { get; set; }

    /// <summary>Headless Special Effect requests that reach Sims.exe's renderer dispatch boundary.</summary>
    public HeadlessSpecialEffectService HeadlessSpecialEffects { get; } = new();

    /// <summary>The most recent string messages emitted by simulation primitives. Sims.exe sends these to UI listeners
    /// (FUN_0040766a); a bounded history lets a hostless native session expose the event to its frontend.</summary>
    public List<SimulationUiMessage> UiMessages { get; } = [];


    public void RecordUiMessage(int message, string text)
    {
        UiMessages.Add(new SimulationUiMessage(Interlocked.Increment(ref s_uiMessageSequence), message, text));
        if (UiMessages.Count > 16) UiMessages.RemoveAt(0);
    }

    /// <summary>The process-local DAT_006e545c random state. Sims.exe FUN_0046f556 advances it with the Microsoft
    /// 32-bit LCG. Session startup must provide the game's seed. Sims.exe saves and restores this state around the
    /// object-manager pass; it is distinct from the lot-local random state stored in SIMI +0xac.</summary>
    public uint RandomState { get; set; } = 1;

    /// <summary>The random state stored at the lot manager's +0xac and in SIMI +0xac. FUN_0046d461 swaps this into
    /// DAT_006e545c for the object-manager pass, saves its advancement here, then restores <see cref="RandomState"/>.</summary>
    public uint ObjectManagerRandomState { get; set; } = 1;

    /// <summary>The decoded SIMI record retained so a native save can write the updated lot-local random state back
    /// without losing the other SIMI globals. Set by <see cref="SavedLotObjectLoader"/> when the lot has a SIMI chunk.</summary>
    public SimulationState? SavedSimulationState { get; internal set; }

    /// <summary>The original GameData/Sprites.iff list used by IconGroup and SpriteSlot Set Balloon.</summary>
    public IffFile? GlobalSprites { get; set; }

    /// <summary>The installed object selectors used by Sims.exe's ParseUIString token-name lookup.</summary>
    internal ObjectRegistry? ObjectRegistry { get; set; }

    /// <summary>Replaces the retained SIMI daily budget rows after a native budget update.</summary>
    public void ReplaceSavedBudgetDays(IReadOnlyList<BudgetDay> days)
    {
        if (SavedSimulationState is { } saved) SavedSimulationState = saved.WithBudgetDays(days);
    }

    /// <summary>Replaces the retained SIMI furnishings (+0xA0) and architecture (+0xA4) valuation fields.</summary>
    public void ReplaceSavedValuation(int fieldA0, int fieldA4)
    {
        if (SavedSimulationState is { } saved) SavedSimulationState = saved.WithValuation(fieldA0, fieldA4);
    }

    /// <summary>Installs the versioned NGBH script-variable prefix into the native world as Sims.exe
    /// FUN_00425540 does for the neighborhood simulation context.</summary>
    public void LoadNeighborhoodData(NeighborhoodData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.ToVariables().CopyTo(NeighborhoodData, 0);
        NeighborInventories = data.Inventories;
        _neighborInventories.Clear();
    }

    private readonly Dictionary<short, AvatarInventory> _neighborInventories = [];

    /// <summary>Raised after Manage Inventory changes a neighbor's inventory, so the host can write it back to the
    /// neighborhood file's SimInventory vector.</summary>
    public event Action<short, AvatarInventory>? NeighborInventoryChanged;

    internal void RaiseNeighborInventoryChanged(short neighborId, AvatarInventory inventory) =>
        NeighborInventoryChanged?.Invoke(neighborId, inventory);

    /// <summary>The neighborhood's SimInventory for <paramref name="neighborId"/>, created empty on first use (FUN_0042a8e3
    /// searches the neighborhood's vector at +0x194 by neighbor id and appends a new record when there is none). Manage
    /// Inventory reaches it through the person's type key (FUN_00425769 finds the neighbor with that GUID).</summary>
    public AvatarInventory NeighborInventory(short neighborId)
    {
        if (_neighborInventories.TryGetValue(neighborId, out var inventory)) return inventory;
        inventory = new AvatarInventory();
        if (NeighborInventories.TryGetValue(neighborId, out var items))
            foreach (var item in items) inventory.Add(item.Type, unchecked((int)item.GUID), unchecked((short)item.Count));
        return _neighborInventories[neighborId] = inventory;
    }

    /// <summary>The neighborhood file's per-neighbor inventory records, as loaded with <see cref="LoadNeighborhoodData"/>.</summary>
    public IReadOnlyDictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>> NeighborInventories { get; private set; } =
        new Dictionary<short, IReadOnlyList<NeighborhoodData.InventoryItem>>();

    /// <summary>The shared per-GUID type-attribute arrays (`cXObjectManager` `+0x1250`, `0043e17b.c`, `0043e1d5.c`).</summary>
    public Dictionary<uint, short[]> SharedTypeAttributes { get; } = [];

    /// <summary>Returns the shared type-attribute array for <paramref name="guid"/> (`0043e1d5.c:31-53`), creating or
    /// expanding it to at least <paramref name="minimumLength"/> entries so all instances of the same GUID share the
    /// same backing array.</summary>
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

    /// <summary>The ObjM read from the lot, retained so a native save can re-encode its blocks.</summary>
    public LotObjects? SavedLotObjects { get; internal set; }

    /// <summary>Each saved type index's object class and placeholder flag, for re-encoding its state.</summary>
    public IReadOnlyDictionary<short, (ObjectClass Class, bool Placeholder)> SavedObjectClasses { get; internal set; } =
        new Dictionary<short, (ObjectClass, bool)>();

    /// <summary>The ObjM version the lot was restored from; its manager-flag tail differs by version
    /// (FUN_0043fe4a). Zero for worlds built without a saved lot.</summary>
    public int SavedObjectManagerVersion { get; internal set; }

    /// <summary>Writes each restored object's current clock-manager flags back into its ObjM block. The whole body is
    /// re-encoded with the byte-exact writer and the lot is grown or shrunk through
    /// <see cref="IffFile.WithChunkDataResized"/>, so a flag whose field coding changes width is saved too. Answers the
    /// lot and the ids written. This narrow writer starts from the restored ObjM snapshot; when composing it with
    /// <see cref="WriteSavedLotObjectsTo"/>, call it first so the full live-state writer runs last.</summary>
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

    /// <summary>Writes the current live state of every restored object back into the ObjM: position, footprint bounds,
    /// attributes, temporaries, data, dynamic sprites, slot occupants, saved stack frames and the clock-manager flags.
    /// Includes live person fields, actions and relationships, and adds created objects/removes deleted objects. The body is
    /// re-encoded with the byte-exact writer and written through <see cref="IffFile.WithChunkDataResized"/>. Answers the
    /// lot and how many objects were written.</summary>
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
            values[0] = bounds.MinX;
            values[1] = bounds.MinY;
            values[2] = bounds.MaxX;
            values[3] = bounds.MaxY;
            // The u32s at +0xf8 and +0xf4 are the x and y positions in sixteenths of a tile (ObjectHead.X and
            // ObjectHead.Y), in that order.
            values[4] = live.Position.X;
            values[5] = live.Position.Y;
            var flags = state.ManagerFlags is null || live.ClockManagerIndex < 1 ||
                        live.ClockManagerIndex > ClockManager.Entries.Count
                ? state.ManagerFlags
                : unchecked((int)ClockManager.Entries[live.ClockManagerIndex - 1].Flags);
            var personState = state.Person;
            if (live is XPerson person && personState is not null)
            {
                // Person data is version-sized (0x50 before 0x47, 0x100 from it); the motives and the +0x7cc floats
                // are the live values the panel and the motive decay read.
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
            overrides[saved.ObjectId] = state with
            {
                Person = personState,
                MultiTile = (live.SavedMultiTileState ?? state.MultiTile) is { } multiTile
                    ? multiTile with { LeadObject = live.MultiTileMasterId } : null,
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

    /// <summary>Writes the retained SIMI stream with the current native random state and global table, or null when
    /// the source lot had no SIMI chunk. This returns chunk data, not a complete house IFF file.</summary>
    public byte[]? WriteSavedSimulationStateChunk() => SavedSimulationState?.WriteChunkData(ObjectManagerRandomState, Globals);

    /// <summary>Returns <paramref name="lot"/> with its existing SIMI chunk updated to the current native random
    /// state. Same-length replacements preserve all chunk and resource-map offsets; resized SIMI streams (such as
    /// legacy 7-category to version-1 8-category cBudgetDay upgrades per FUN_0046d38c) update chunk and rsmp offsets.</summary>
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

    /// <summary>Serializes <see cref="SharedTypeAttributes"/> as a `TATT` #1 chunk payload (`0043e1a3.c`, `0043e1bc.c`, `0043e1d5.c`).</summary>
    public byte[] WriteSavedTypeAttributesChunk(int version = 0x49, bool coded = true, bool swap = false) =>
        TypeAttributes.Write(SharedTypeAttributes, version, coded, swap);

    /// <summary>Writes <see cref="SharedTypeAttributes"/> into <paramref name="file"/>'s `TATT` #1 chunk.</summary>
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

    /// <summary>Builds a snapshot of the live neighborhood inventories (merging loaded <see cref="NeighborInventories"/>
    /// with any mutated <see cref="AvatarInventory"/> instances) and serializes `NGBH` #1 (`00425540.c`).</summary>
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

    /// <summary>Advances the Sims.exe FUN_0046f556 random source and returns the upper 32 bits of its 64-bit multiply-add shifted right 16.</summary>
    public uint NextRandom()
    {
        var previous = RandomState;
        RandomState = unchecked(previous * 0x41c64e6d + 0x3039);
        // FUN_0046f556 returns the upper 32 bits of the 64-bit multiply-add shifted right 16,
        // not merely the high 16 bits of the wrapped state.
        return (uint)(((ulong)previous * 0x41c64e6d + 0x3039) >> 16);
    }
    private readonly List<XObject?> _objectTableEntries = [];
    private readonly HashSet<short> _activeAssociatedPlacementCallbacks = [];
    private int _actionQueueSequence;

    /// <summary>FUN_0041f1c7 raises the sequence high-water mark for every restored action.</summary>
    public void RestoreActionSequence(int sequence) => _actionQueueSequence = Math.Max(_actionQueueSequence, sequence);
    private static readonly byte[] PersonRecordFieldsVersion9 =
    [
        2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 16, 20, 15, 26, 17, 18, 46, 47, 48, 49, 50, 51, 52, 53,
        13, 14, 58, 60, 61, 56, 57, 63, 65, 67, 68, 69, 74, 55, 75, 76, 80, 81, 82,
    ];

    public const int GlobalCount = 0x40;

    /// <summary>The ordered listener chain at Sims.exe DAT_006DE808. FUN_0040766a invokes each listener synchronously.</summary>
    public SimulationNoticeListener? NoticeListeners { get; private set; }

    /// <summary>Append a listener to the ordered DAT_006DE808 projection, preserving the original registration order.</summary>
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

    /// <summary>FUN_0040766a: call every listener with the message and value, returning true if any returns nonzero.
    /// The next link is captured before each callback, matching the original linked-list traversal.</summary>
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
        // FUN_00416375 handles room-change notification 0xf3 after dispatching upstream listeners;
        // the off-lot key is the unsigned 0xfffb sentinel and has no room record to dirty.
        if (message == 0xf3 && value is not (-5 or 0xfffb))
            RefreshRoomRecord(unchecked((ushort)value));
        return handled;
    }

    /// <summary>The native clock fields advanced by FUN_0046d461. The caller supplies the result of its
    /// manager-dependent FUN_0046d66f gate until that manager loop is ported.</summary>
    public SimulationClock Clock { get; } = new();
    /// <summary>The date-manager words consumed by FUN_00432396. Saved worlds source these from the SIMI globals
    /// that FUN_0046d0cf restores into the manager's Hour/DayOfMonth/Month/Year fields. Hand-built worlds may supply
    /// an explicit date for focused simulation.</summary>
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
    /// <summary>The resumable object-manager vectors and cursor from Sims.exe FUN_0046d66f.</summary>
    public SimulationClockManager ClockManager { get; } = new();
    /// <summary>DAT_006c4d60 from Sims.exe `FUN_0044350d`: 10,000 plus fifteen times the object-manager vector
    /// length, plus fifteen times the square of `FUN_00427162`'s count. The helper scans the neighborhood manager's
    /// +0x148/+0x14c vector (the same `[DAT_006e486c]+0x24` manager used by `FUN_004EA800`/`FUN_00427281`) and counts
    /// non-null neighbor records whose linked object's +0xb4 flags satisfy `(flags & 0xC) == 4` (`FUN_00423f63`).
    /// `ClockManager.Entries` models the one-based object-manager vector; `NeighborWalkOrder` models that ordered
    /// neighborhood vector.</summary>
    public int TreeStepLimit
    {
        get
        {
            var eligibleNeighbors = NeighborWalkOrder.Count(neighbor =>
                neighbor?.FriendEligibilityObjectFlagsB4 is { } flags && (flags & 0xC) == 4);
            return checked(10_000 + ClockManager.Entries.Count * 15 + eligibleNeighbors * eligibleNeighbors * 15);
        }
    }
    /// <summary>Ordered numeric row ids from the registered MagicMasterSpells.iff STR# 15, excluding its reserved first entry.</summary>
    public IReadOnlyList<short> MagicMasterSpellItemIds { get; set; } = [];
    /// <summary>Global.iff FCNS 1/5/6 tuning loaded by Sims.exe FUN_00420c69.</summary>
    public PersonMotiveDecayTuning? PersonMotiveDecayTuning { get; set; }
    /// <summary>Pending message-4 events from priority-based action-queue displacement; a concrete game host must deliver
    /// these through FUN_00422d79.</summary>
    public List<ActionQueueRemovalNotice> ActionQueueRemovalNotices { get; } = [];
    /// <summary>Records the source admission path for actions dequeued by FUN_0045dd00.</summary>
    public List<QueuedInteractionStartTrace> QueuedInteractionStartTrace { get; } = [];
    /// <summary>Models the three nonzero runtime globals checked by FUN_004588d7 before updating the visible action display.
    /// Production runtime population is not yet ported.</summary>
    public bool ActionStringDisplayActive { get; set; }
    public InteractionMenuContext? InteractionMenu { get; internal set; }
    /// <summary>Sims.exe DAT_006e5244 prevents nested Run Immediately action execution until FUN_00474c68 returns.</summary>
    internal bool ImmediateInteractionRunning { get; set; }
    internal ActionQueueSlot? ActiveImmediateAction { get; set; }
    internal XPerson? ActiveImmediatePerson { get; set; }
    /// <summary>True only while a TTAB guard is being evaluated for availability. Movement primitives must not start live routing here.</summary>
    public bool EvaluatingInteractionTest { get; set; }
    /// <summary>Models DAT_006c4d5c != 0, which permits Change Action String to store text on a person's +0xa48 record.</summary>
    /// <summary>Sims.exe DAT_006c4d5c is initialized to 1; FUN_00474b22 saves and clears it only for a nested tree,
    /// then FUN_00474c34 restores the prior value. TreeRunner mirrors that scoped suppression.</summary>
    public bool ActionStringRecordWriteEnabled { get; set; } = true;

    public Dictionary<short, XObject> Objects { get; } = [];
    /// <summary>Room records looked up by cXPerson's FUN_004670a4 room-tree search. Values are the room's +0x68
    /// interior motive score and the cached outside predicate from FUN_00468851.</summary>
    public Dictionary<ushort, PersonRoomMotiveEntry> PersonRoomMotiveEntries { get; } = [];
    /// <summary>The per-level wall-derived room partitions created by Sims.exe FUN_00479f0c when a lot is loaded.</summary>
    public Dictionary<int, SimsEngine.World.RoomMap> LotRoomMaps { get; } = [];
    /// <summary>The lot manager +0x34 tile flag grid (Arry 8) that FUN_00434895's flag-C check reads: a tile flagged 0x20
    /// is outside the playable lot. Null when the lot has no such grid.</summary>
    public SimsEngine.World.LotTiles? LotTileFlags { get; set; }
    /// <summary>Mutable per-level room-manager bytes updated by FUN_00433186 for BuildModeType 6 objects. Bit 3 is
    /// set on placement and cleared on removal; this is distinct from imported Arry 9 pool bytes.</summary>
    public Dictionary<int, byte[]> BuildModeType6PlacementGrids { get; } = [];
    /// <summary>Manager +0x64's placement-eligibility bytes. Sims.exe initializes these cells to 0xff; the writer/load
    /// mapping for non-0xff values remains unresolved. This table is distinct from the manager +0x30 grid.</summary>
    public Dictionary<int, byte[]> PlacementEligibilityGrids { get; } = [];
    /// <summary>Mutable per-level byte grids written by Sims.exe FUN_00467805. Source coordinates address x*64+y.</summary>
    public Dictionary<int, byte[]> LotRoomLightMaps { get; } = [];
    /// <summary>The manager's two-candidate room-light grid maintained by FUN_004b52ba/FUN_004b5090.</summary>
    public SimsEngine.World.RoomLightCoordinateGrid RoomLightCandidates { get; } = new();
    /// <summary>Source global DAT_006e4a84. When nonzero, FUN_00434895 bypasses the FlagC water-grid bit test.
    /// Sims.exe case 10 in FUN_004f8584 writes this switch; its user-facing setting name is not recovered.</summary>
    public bool FindLocationWaterOverride { get; set; }
    /// <summary>Alias for <see cref="FindLocationWaterOverride"/> (Sims.exe DAT_006e4a84 move_objects cheat flag),
    /// checked by FUN_0043538d (CanPickUpObject) and FUN_004354e5 (CanSellObject).</summary>
    public bool MoveObjects
    {
        get => FindLocationWaterOverride;
        set => FindLocationWaterOverride = value;
    }
    /// <summary>True only after the runtime cXRoom records, distinct from tile room partitions, have been reconstructed.</summary>
    public bool RoomManagerRecordsLoaded { get; set; }
    /// <summary>The lot manager's +0x3c outside-room value read by FUN_0045d060.</summary>
    public float OutsidePersonRoomMotiveValue { get; set; }
    /// <summary>Runtime override for Sims.exe DAT_006f011c used by room-light grid traversal. Its executable-image
    /// initial value is zero; no direct writer was found in Sims.exe.</summary>
    public float RoomLightRadiusScalar { get; set; } = SimsEngine.World.RoomLightCoordinateGrid.SimsExecutableInitialLightingScalar;
    /// <summary>Runtime override for Sims.exe DAT_006f010c used by FUN_004674e7. Its executable-image initial value
    /// is zero; no direct writer was found in Sims.exe.</summary>
    public float RoomDaylightScalar { get; set; } = SimsEngine.World.RoomRuntimeScalarDefaults.SimsExecutableInitialDaylightScalar;
    /// <summary>FUN_004674e7's current time-phase multiplier applied to the room daylight scalar.</summary>
    public float RoomDaylightAtCurrentClockPhase =>
        SimsEngine.World.RoomRuntimeScalarDefaults.DaylightForTimePhase(Clock.TimeOfDay, RoomDaylightScalar);
    /// <summary>The low dirty bits of the lot manager's +0x34, set by FUN_00466bdf when a room in group 0 or 1 changes.</summary>
    public uint RoomManagerRefreshFlags { get; private set; }
    private readonly HashSet<int> _portalWallRefreshRequestedLevels = [];
    private readonly HashSet<int> _portalGeometryRefreshRequestedLevels = [];
    /// <summary>Manager channels marked by the separate cXPortal FUN_00463648/FUN_00540eb1 path.</summary>
    public IReadOnlySet<int> PortalWallRefreshRequestedChannels => _portalWallRefreshRequestedLevels;
    /// <summary>Manager channels marked by portal placement's FUN_004632af/FUN_00540ecf after wall/room updates.</summary>
    public IReadOnlySet<int> PortalGeometryRefreshRequestedChannels => _portalGeometryRefreshRequestedLevels;

    /// <summary>Consumes pending manager-channel requests like FUN_00540eed after its geometry updates. Native
    /// RoomMap wall cells are updated in place before this call, so the returned heights tell the host which levels
    /// to repaint; this does not claim to implement Sims.exe's cached geometry-group rebuild.</summary>
    public IReadOnlyList<int> ConsumePortalWallRefreshRequests()
    {
        if (_portalWallRefreshRequestedLevels.Count == 0) return Array.Empty<int>();
        var levels = _portalWallRefreshRequestedLevels.Order().ToArray();
        _portalWallRefreshRequestedLevels.Clear();
        return levels;
    }

    /// <summary>Drains portal placement's FUN_00540ecf geometry-channel requests, the +0x40/+0x44/+0x48
    /// manager words consumed by the source refresh lifecycle.</summary>
    public IReadOnlyList<int> ConsumePortalGeometryRefreshRequests()
    {
        if (_portalGeometryRefreshRequestedLevels.Count == 0) return Array.Empty<int>();
        var levels = _portalGeometryRefreshRequestedLevels.Order().ToArray();
        _portalGeometryRefreshRequestedLevels.Clear();
        return levels;
    }

    /// <summary>Synchronous listeners for cXPortal FUN_00476b8f's vtable +0x40 callback, passed the coordinate
    /// transformed by FUN_00434ffd. Listener behavior is not yet traced.</summary>
    public event Action<PortalWallRefreshCoordinate>? PortalWallRefreshRequested;

    private void MarkPortalGeometryRefreshRequested(int height)
    {
        // FUN_00540ecf sets +0x40 and +0x44 for height 0; otherwise it sets +0x3c + height*4.
        if (height == 0)
        {
            _portalGeometryRefreshRequestedLevels.Add(1);
            _portalGeometryRefreshRequestedLevels.Add(2);
        }
        else _portalGeometryRefreshRequestedLevels.Add(height);
    }

    private void MarkPortalWallRefreshRequested(int height, int tileX, int tileY)
    {
        // FUN_00540eb1(param=0) sets both manager words at +0x38 and +0x3c; nonzero levels set
        // the corresponding +0x34 + height*4 word.
        if (height == 0)
        {
            _portalWallRefreshRequestedLevels.Add(1);
            _portalWallRefreshRequestedLevels.Add(2);
        }
        else _portalWallRefreshRequestedLevels.Add(height);
        // FUN_00463648 calls FUN_00434ffd on [y, x, level], then synchronously fans that buffer out through FUN_00476b8f.
        var coordinate = MapPortalWallRefreshCoordinate(tileX, tileY, height);
        PortalWallRefreshRequested?.Invoke(coordinate);
    }

    private static PortalWallRefreshCoordinate MapPortalWallRefreshCoordinate(int tileX, int tileY, int height)
    {
        // FUN_00407761 initializes DAT_006de81c's 64x64 maps for levels 1..3; FUN_00434ffd indexes it
        // with [level - 1, y, x]. Preserve the source byte order in the listener record.
        var (first, second) = height switch
        {
            1 => (63 - tileX, tileY),
            2 => (63 - tileY, 63 - tileX),
            3 => (tileX, 63 - tileY),
            // The source table has no defined level-0 or level-4+ region. Keep raw coordinates for those inputs.
            _ => (tileY, tileX),
        };
        return new PortalWallRefreshCoordinate(unchecked((byte)first), unchecked((byte)second), unchecked((byte)height));
    }

    /// <summary>Completes Sims.exe's room-load sequence: FUN_00479f0c fills each level's partition, then calls
    /// FUN_004670ce/FUN_00467873 for valid room keys; FUN_00467999 initializes each cXRoom score to zero, outside
    /// cache to false, and dirty byte +0x70 to one. Supports in-place refreshes when build tools modify walls/floors/pools.</summary>
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
        // FUN_00479f0c ends by sending message 0xf0. FUN_00416375 handles it by calling
        // FUN_00442699 before FUN_00466d14: repair level-zero objects, refresh every object's room key,
        // then calculate every initially dirty room in the same source order.
        ReconcileObjectRoomsAfterMapLoad();
        RefreshDirtyRoomRecords();
    }

    /// <summary>Reloads room maps in-place, rebuilding runtime room records and refreshing object room memberships.</summary>
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
                person.LastObservedRoomIdAFC = unchecked((ushort)roomId); // FUN_0045e5a9's cached room id write.
        }
    }

    /// <summary>FUN_004670a4's room-tree lookup succeeds when this runtime record key is present.</summary>
    public bool HasRoomRecord(short roomId) =>
        RoomManagerRecordsLoaded && PersonRoomMotiveEntries.ContainsKey(unchecked((ushort)roomId));

    /// <summary>The cached cXRoom +0x72 outside flag FUN_00468851 fills, <see cref="RoomMap.IsOutside"/>'s inferred
    /// rule in <see cref="LoadRoomMaps"/>. False for a room without a native record.</summary>
    public bool RoomIsOutside(short roomId) =>
        PersonRoomMotiveEntries.TryGetValue(unchecked((ushort)roomId), out var room) && room.IsOutside;

    /// <summary>FUN_00466bdf: find a native room-manager record, set its +0x70 dirty byte, and mark the appropriate
    /// lot-manager refresh bit from room-key bits 10–11. Returns its lookup result.</summary>
    public RoomRefreshResult RefreshRoomRecord(ushort roomId)
    {
        if (!RoomManagerRecordsLoaded) return RoomRefreshResult.ManagerUnavailable;
        if (!PersonRoomMotiveEntries.TryGetValue(roomId, out var room)) return RoomRefreshResult.RoomNotFound;
        room.NeedsRefresh = true;
        PersonRoomMotiveEntries[roomId] = room;
        // FUN_00467873 stores roomId in the first word of cXRoom; FUN_00466bdf tests that word's 0xc00 bits.
        switch ((roomId & 0x0c00) >> 10)
        {
            case 0: RoomManagerRefreshFlags |= 1; break;
            case 1: RoomManagerRefreshFlags |= 2; break;
        }
        return RoomRefreshResult.Refreshed;
    }

    /// <summary>Runs the source message-0xf0 dirty-room pass (Sims.exe FUN_00466d14). Group 0 refreshes all groups;
    /// groups 1 and 2 select the matching cXRoom key bits. Returns the number of records recalculated.</summary>
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

    internal static float DisplayedRoomLight(PersonRoomMotiveEntry room)
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

    /// <summary>Moves an object through the native position path and refreshes cXObject +0x84 from the retained
    /// room grid. Sims.exe FUN_00433ef5 calls FUN_00433c6a after writing position; saved-state restore deliberately
    /// bypasses this method because FUN_0043426c does not restore the room word.</summary>
    public void SetObjectPosition(XObject item, ObjectPosition position)
        => SetObjectPosition(item, position, new HashSet<XObject>(ReferenceEqualityComparer.Instance));

    private void SetObjectPosition(XObject item, ObjectPosition position, HashSet<XObject> visited)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!visited.Add(item)) return;
        item.Position = position;
        if (item is XPerson movingPerson && movingPerson.NativeRoute is null)
        {
            // External placement changes the animator's world anchor. An active FollowOneStep owns
            // its floating root coordinates until EndFollow consumes that callback.
            movingPerson.AnimatorRootX = position.X;
            movingPerson.AnimatorRootY = position.Y;
            movingPerson.AnimatorRootDirection = movingPerson[ObjectData.Direction];
        }
        // Sims.exe FUN_00433ef5 refreshes cXPerson and ordinary-object bounds through FUN_00433dbb on every
        // position change. Keeping the old rectangle leaves an object collidable at its previous tile.
        if (item is XPerson person && person.TypeRecord is not null)
            person.RefreshFootprintBounds();
        else if (item.TypeRecord is not null)
        {
            // FUN_00433ef5 calls FUN_00433dbb on every move. Recompute from type extents so changed direction
            // and changed position both update the retained rectangle.
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

        // FUN_00433ef5 skips its reserved entry zero and moves every related object found through +0x130's table.
        foreach (var related in EnumerateContainedSlotChildren(item))
            SetObjectPosition(related, position, visited);
    }
    /// <summary>Saved TypeRecord +0xa4 code-owner slots to their behavior scopes. Sims.exe loads the slot values
    /// from OBJT TypeIndex and builds the parallel code-owner array in FUN_0043a5fb/FUN_0043de5b.</summary>
    public Dictionary<short, ITreeTable?> CodeOwnerScopes { get; } = [];
    /// <summary>The saved object type GUID assigned to each TypeRecord +0xa4 code-owner slot.</summary>
    public Dictionary<short, uint> CodeOwnerTypeGuids { get; } = [];
    /// <summary>Interpreter stacks reconstructed from each object's saved stack frames, keyed by saved object id.</summary>
    public Dictionary<short, TreeStack> SavedExecutionStacks { get; } = [];
    /// <summary>Action-scoped queue/lane and attachment state for explicitly capability-enabled native interactions.</summary>
    public InteractionLifecycle InteractionLifecycle { get; } = new();
    /// <summary>Diagnostic failures from the source-defined +0x34 found-action tree push.</summary>
    public List<FoundActionPushFailure> FoundActionPushFailures { get; } = [];
    /// <summary>Successful source primitive object creations, retained only when execution tracing is enabled.</summary>
    public List<ObjectCreationDiagnostic> ObjectCreationTrace { get; } = [];
    public List<ObjectRemovalDiagnostic> ObjectRemovalTrace { get; } = [];
    /// <summary>The lot object's ordered vector at +0x84/+0x88, used by Sims.exe object-list walks including
    /// FUN_0042DB93/FUN_0042DF00 and Set to Next. It holds the same objects as <see cref="Objects"/>.</summary>
    public List<XObject> WalkOrder { get; } = [];
    /// <summary>FUN_00445897's per-type cache, projected from the manager's ordered lot-object vector (+0x18).
    /// The original compares shared type-record pointers; TypeRecord.Key is the native model's GUID identity.</summary>
    public IReadOnlyList<XObject> ObjectsOfType(int guidKey) =>
        WalkOrder.Where(item => item.TypeRecord?.Key == guidKey).ToArray();
    /// <summary>The ordered object pointer vector at the lot manager's +0x154/+0x158, searched by
    /// FUN_00425DB3 for a matching cXObject +0x10c dword (GenericSimsCall 0x05/0x21).</summary>
    public List<XObject> InteractionLookupOrder { get; } = [];
    /// <summary>The lot-manager byte at +0x11 read by FUN_00447d6b. A live SimulationWorld represents the initialized
    /// lot manager; callers constructing an in-progress manager can clear this gate.</summary>
    public bool InteractionRefreshManagerEnabled { get; set; } = true;
    /// <summary>The active global type-record key returned by FUN_0045741b for FUN_0044822b's dispatch.</summary>
    public int InteractionRefreshTypeKey { get; set; }
    /// <summary>DAT_006e579c, the active interaction target compared by FUN_00447d6b.</summary>
    public XObject? ActiveInteractionRefreshTarget { get; set; }
    /// <summary>Whether FUN_004d40b6 invalidated both active interaction views for the matching target.</summary>
    public bool InteractionContextsInvalidated { get; private set; }
    /// <summary>The manager's +0xd0/+0xd4 object vector. FUN_0044822b transfers a matching target out of this vector
    /// for active type key 0xe9337020.</summary>
    public List<XObject> InteractionRefreshPendingObjects { get; } = [];
    /// <summary>The manager's +0xdc/+0xe0 object vector, which receives the transferred target.</summary>
    public List<XObject> InteractionRefreshDeferredObjects { get; } = [];
    /// <summary>The lot manager's +0x84 orientation value consumed by FUN_00434FFD. FUN_00415C5B creates this manager
    /// through FUN_00479161, which initializes +0x84 to zero; FUN_00479318 updates it as orientation &amp; 3.</summary>
    public int LotTransformParameter { get; private set; }
    /// <summary>The lot manager's +0x70/+0x74/+0x78/+0x7c bounds; the game constructs a 64 × 64 tile lot.</summary>
    public LotCoordinateBounds LotBounds { get; private set; } = LotGeometry.TransformLotBounds(64, 0);
    /// <summary>FUN_00479318: store the masked orientation and recompute the transformed lot bounds.</summary>
    public void SetLotOrientation(int orientation)
    {
        LotTransformParameter = LotGeometry.NormalizeOrientation(orientation);
        LotBounds = LotGeometry.TransformLotBounds(64, LotTransformParameter);
    }
    public Dictionary<short, NeighborState> Neighbors { get; } = [];
    internal Neighbors? SavedNeighbors { get; set; }
    /// <summary>False when an invalid NBRS tail leaves later ids unknown; creation must not reuse those ids.</summary>
    internal bool NeighborRosterComplete { get; set; } = true;
    internal int CompleteNeighborSlotPrefix { get; set; }
    /// <summary>ObjectModule +0x12, set when a person-neighbor relationship changes.</summary>
    public bool RelationshipModuleDirtyFlag12 { get; private set; }
    /// <summary>Set when live Sims copy persistent PersonData fields into their neighborhood neighbor records.</summary>
    public bool PersistentPersonDataDirty { get; private set; }

    /// <summary>Writes a field on an uninstantiated neighbor and includes it in the next neighborhood save.</summary>
    public void SetPersistentNeighborPersonData(NeighborState neighbor, PersonData field, short value)
    {
        var index = (int)field;
        if (neighbor.PersonData[index] == value) return;
        neighbor.PersonData[index] = value;
        if (neighbor.PersonRecordVersion == 0) neighbor.PersonRecordVersion = 9;
        PersistentPersonDataDirty = true;
    }

    /// <summary>PersonFinder::GetData prefers the instantiated person's data over the neighbor's persistent data.</summary>
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
    /// <summary>Family +0x124 dirty markers produced by Neighborhood::RelationshipsChanged and Neighborhood::RemoveFromFamily.</summary>
    public HashSet<short> DirtyRelationshipFamilies { get; } = [];

    public void MarkRelationshipsChanged(NeighborState source, NeighborState target)
    {
        source.RelationshipsChangedFlag24 = true;
        target.RelationshipsChangedFlag24 = true;
        var sourceFamily = source.PersonData[(int)PersonData.FamilyNumber];
        var targetFamily = target.PersonData[(int)PersonData.FamilyNumber];
        if (sourceFamily != 0) DirtyRelationshipFamilies.Add(sourceFamily);
        if (targetFamily != 0) DirtyRelationshipFamilies.Add(targetFamily);
        RelationshipModuleDirtyFlag12 = true;
    }

    /// <summary>Projects live relationship records back into the saved neighborhood NBRS chunk.</summary>
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
            if (_removedNeighbors.Contains(saved.NeighborId)) continue;
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
            if (live is null || _removedNeighbors.Contains(live.Id) || entriesById.ContainsKey(live.Id))
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
                RecordVersion: versioned ? live.PersonRecordVersion : (SavedNeighbors.Version > 0x48 ? 9 : 4),
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

    /// <summary>Mac Neighbor::IsCharacter (Sims.exe FUN_00423f63): the neighbor's type record was loaded as a character
    /// (bits 2 to 3 of its +0xb4 flags are 1).</summary>
    public static bool IsCharacter(NeighborState neighbor) =>
        neighbor.FriendEligibilityObjectFlagsB4 is { } flags && (flags & 0xC) == 4;

    /// <summary>The character sweep Mac Neighborhood::SaveHouse (0x100a6d00) runs after House::SaveFile: each character
    /// whose person data names no family (61) and no lingering house (67) goes through <see cref="DeleteCharacter"/>,
    /// and the scan restarts after every deletion. The original restarts after a refusal too, which would find the
    /// same neighbor again; here a refusal ends the sweep.</summary>
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

    /// <summary>Mac Neighborhood::DeleteCharacter (0x100a9680) up to its file removal: false for a neighbor that is not a
    /// character. Otherwise every lot object of the neighbor's GUID is killed (ObjectModule::KillObject), and the call
    /// answers false when a family still lists the GUID (Family::TestMember). Then Neighborhood::RemoveNeighbor
    /// (0x100aba70) erases the neighbor's inventory tokens of types 0 to 9, empties its slot in the neighbor vector,
    /// drops every other neighbor's relationship array toward it (RelMatrix::RemoveArray) and destroys it. Its
    /// GlobalDispatch(0xfb, id) notifies the interface only. The host removes the character file.</summary>
    public bool DeleteCharacter(NeighborState neighbor, Func<int, bool> isFamilyMember)
    {
        ArgumentNullException.ThrowIfNull(neighbor);
        ArgumentNullException.ThrowIfNull(isFamilyMember);
        if (!IsCharacter(neighbor)) return false;
        while (WalkOrder.FirstOrDefault(item => item.TypeRecord?.Key == neighbor.TypeRecordKey) is { } item)
            KillObject(item.Id);
        if (isFamilyMember(neighbor.TypeRecordKey)) return false;

        // Neighborhood::RemoveNeighbor dispatches this interface notice before it clears the neighbor's inventory
        // and removes the neighbor/relationship records (Mac Neighborhood.s 0x100ABA70–0x100ABAA0; Windows
        // FUN_004259ec).
        DispatchNotice(0xfb, neighbor.Id);
        var inventory = NeighborInventory(neighbor.Id);
        for (var type = 0; type <= 9; type++) inventory.RemoveAllOfType(type);
        RaiseNeighborInventoryChanged(neighbor.Id, inventory);
        // RemoveNeighbor clears vector element id - 1; native keeps each record at its loaded NBRS slot.
        var slot = NeighborWalkOrder.IndexOf(neighbor);
        if (slot >= 0) NeighborWalkOrder[slot] = null;
        foreach (var other in NeighborWalkOrder)
            other?.Relationships.RemoveTarget(neighbor.Id);
        Neighbors.Remove(neighbor.Id);
        _removedNeighbors.Add(neighbor.Id);
        RelationshipModuleDirtyFlag12 = true;
        return true;
    }

    /// <summary>The ordered nullable cXNeighbor pointers at `[DAT_006e486c]+0x24`, the neighborhood manager's
    /// +0x148/+0x14c vector traversed by FUN_00427162 and FUN_00427281. Null slots and order are preserved.</summary>
    public List<NeighborState?> NeighborWalkOrder { get; } = [];
    /// <summary>FUN_0042573e: resolves the one-based index used by tree variable owners 0x20 and 0x26 through the
    /// ordered neighborhood-manager vector. This index is independent of a record's saved NeighborId.</summary>
    public NeighborState? NeighborManagerEntry(short oneBasedIndex) =>
        oneBasedIndex >= 1 && oneBasedIndex <= NeighborWalkOrder.Count ? NeighborWalkOrder[oneBasedIndex - 1] : null;
    /// <summary>FUN_004750ba/FUN_004754d8/FUN_004020a6: the opcode (low 15 bits) of the instruction the object's thread
    /// is waiting on, or null when it has no active frame. Native projection: the restored stack's top frame names the
    /// instruction in its current tree.</summary>
    public short? WaitInstructionOpcode(XObject stackObject)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        if (!SavedExecutionStacks.TryGetValue(stackObject.Id, out var stack) || stack.Top is not { } frame) return null;
        if (frame.Tree is not Bhav tree) return null;
        if (frame.Instruction < 0 || frame.Instruction >= tree.Instructions.Count) return null;
        return (short)(tree.Instructions[frame.Instruction].Opcode & 0x7fff);
    }

    /// <summary>FUN_004752f8: clears the waiting instruction's counter. Native projection: the parameter its operand
    /// word 0 names on the top frame (Sleep and Idle For Input keep their counters there).</summary>
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

    /// <summary>FUN_00440b6d's object factory: a content-backed creator for Create New Object Instance, or null when
    /// the world has no registry. Set by <see cref="SavedLotObjectLoader.Load"/>; returns null for an unknown type.</summary>
    public Func<int, XObject?>? ObjectFactory { get; set; }

    /// <summary>Creates and registers a catalog multi-tile root and its definition-ordered parts. Sims.exe
    /// FUN_00440b6d constructs every matching member before placement, linking +0x18c and storing the root id on
    /// each part. A null result means the requested definition is not a group root.</summary>
    public Func<int, IReadOnlyList<XObject>?>? ObjectGroupFactory { get; set; }

    /// <summary>Creates an unregistered candidate for read-only placement checks. Unlike <see cref="ObjectFactory"/>,
    /// this does not add a new saved-object type to the live lot.</summary>
    public Func<int, XObject?>? PlacementPreviewFactory { get; set; }
    /// <summary>Builds a build-tool cursor object (OBJD type 8 or 9 without a master id, which the lot never creates) as a plain object so it can be previewed.</summary>
    public Func<int, XObject?>? BuildCursorFactory { get; set; }

    /// <summary>Creates and registers an unregistered multi-tile preview group. Its members are removed together after
    /// scene export; the preview creator must not add new saved-object type records.</summary>
    public Func<int, IReadOnlyList<XObject>?>? PlacementPreviewGroupFactory { get; set; }

    /// <summary>Removes a newly created multi-tile group as one failed-placement unit.</summary>
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

    /// <summary>FUN_0044350d's first free one-based object id for a newly created object.</summary>
    public short NextObjectId()
    {
        for (short id = 1; id < short.MaxValue; id++)
            if (!Objects.ContainsKey(id)) return id;
        throw new InvalidOperationException("No free object id.");
    }

    /// <summary>The tile scan of Create Object's duplicate and empty checks (FUN_0043f371): whether the tile holds
    /// an object of <paramref name="guidKey"/>, or any object when it is null. Inferred: the walk order and the
    /// object's current fixed-point position.</summary>
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

    /// <summary>The room of a tile on <paramref name="level"/> from the retained wall partitions, or 0 when the map
    /// or room is absent (Create Object's FUN_00435a8f room test).</summary>
    public short RoomAtTile(int tileX, int tileY, int level)
    {
        return TryGetRoomAtTile(tileX, tileY, level, out var room) ? room : (short)0;
    }

    /// <summary>Distinguishes a valid room key 0 from a tile with no room, which the short compatibility accessor
    /// above intentionally collapses.</summary>
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

    /// <summary>Whether the retained wall layer marks a tile as a room-dividing diagonal (FUN_00434895).</summary>
    public bool TileHasRoomDividingDiagonal(int tileX, int tileY, int level) =>
        LotRoomMaps.TryGetValue(level, out var map) && map.HasRoomDividingDiagonal(tileX, tileY);

    public bool TileHasDiagonalWall(int tileX, int tileY, int level) =>
        LotRoomMaps.TryGetValue(level, out var map) && map.HasDiagonalWall(tileX, tileY);

    /// <summary>The placement check of Create Object's vtable +0x54 (FUN_0044e108's check/action pair). The source
    /// relative object skips the wall and allowed-height gates, but FUN_00432a70 still checks upper-floor presence.</summary>
    public bool CanPlaceCreatedObject(XObject created, ObjectPosition at, XObject? relativeTo = null, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(created);
        return PassesCommonPlacementChecks(created, at, relativeTo is not null, checkStack,
            relativeTo?.ClockManagerIndex ?? 0) && PassesUpperFloorPlacementCheck(created, at);
    }

    /// <summary>FUN_00434895's lot search over native lot state. Candidate offsets, walk limit, room flag and dividing
    /// diagonal rejection and retained Arry 9 pool gates follow the decompilation. Inferred: outside/slope checks,
    /// complete wall tests and placement virtual are not fully represented by the native lot model.</summary>
    public bool FindLocation(XObject stackObject, FindLocationParams parameters, out ObjectPosition found, TreeStack? checkStack = null)
    {
        ArgumentNullException.ThrowIfNull(stackObject);
        var size = LotGeometry.LotDimension;
        var height = parameters.Height is >= 1 and <= 2 ? parameters.Height : 1;
        var transformedCenter = LotGeometry.TransformSearchCenter(
            new ObjectPosition(parameters.CenterX, parameters.CenterY, height), LotTransformParameter);
        var centerX = Math.Clamp(transformedCenter.X >> 4, 1, size - 2);
        var centerY = Math.Clamp(transformedCenter.Y >> 4, 1, size - 2);
        var hasCenterRoom = TryGetRoomAtTile(centerX, centerY, height, out var centerRoom);
        var offsetX = 0;
        var offsetY = 0;
        var consecutiveOffLot = 0;
        // FUN_00434895 bounds the walk using lotDimension * 8 consecutive off-lot positions. Its direction -1 path moves around a
        // square spiral; cardinal directions search a source-defined alternating line along one axis. The
        // 8*dimension limit is consecutive off-lot positions; the independent absolute cap is 0x1000.
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
                // In Sims.exe, FUN_00434d3d writes +0xf4 (Y) to word 0 and +0xf8 (X) to word 1 of cWorldPt;
                // ObjectPosition stores X (+0xf8) first and Y (+0xf4) second.
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

    /// <summary>PersonFinder::CreateInstance calls cXObject::FindGoodLocation with an unset center and direction -1.
    /// The Mac routine searches from GViewer::GetCentermostTile in its square spiral, tests the object's placement
    /// callback at tile centers, and returns the first accepted tile. The caller supplies that viewer center here;
    /// native placement uses the same ordinary placement callback used by Find Location For.</summary>
    public bool FindGoodLocation(XObject item, int centerX, int centerY, out ObjectPosition found)
    {
        ArgumentNullException.ThrowIfNull(item);
        var size = LotGeometry.LotDimension;
        // FUN_100BBCC0 takes GViewer's center tile in viewer coordinates, then calls RotateCoords
        // (the inverse of the lot orientation) before walking the canonical lot grid.
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

            // FUN_100BBCC0's direction -1 branch: compare dy against -dx and dx, then advance one
            // edge of the square spiral. This preserves its initial order (0,0), (-1,0), (-1,1), (0,1).
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
        // FUN_004790e8 returns true for a level-1 byte below 0x10, and FUN_00434895 requires it to be false.
        if (LotRoomMaps.TryGetValue(height, out var map) && map.TryGetWaterGridByte(x, y, out var value) && value < 0x10)
            waterOk = false;
        if (!waterOk) return false;
        // FUN_00434895 requires bit 0x20 of the lot manager +0x34 tile flag grid (not the water grid) to be clear when
        // +0x16 is set and DAT_006e4a84 is zero.
        return flagC == 0 || FindLocationWaterOverride || LotTileFlags?.InsideLot(x, y) != false;
    }

    /// <summary>Matches cXPerson::ForceLocation and cFixedWorld::HasPool: a person is on a pool tile when their
    /// ground tile is inside the one-tile lot border and the Arry 9 byte is below 0x10.</summary>
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
            // Decompiled FUN_00434895's -1 branch. The label shared by its two comparisons is kept as a single
            // continuation here so tie behavior at the spiral corners matches the original.
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

        // FUN_00434895 alternates 0,+1,-1,+2,-2... for the axis selected by its direction branch.
        if (direction is 0 or 4) offsetY = offsetY < 1 ? 1 - offsetY : -offsetY;
        else if (direction is 2 or 6) offsetX = offsetX < 1 ? 1 - offsetX : -offsetX;
    }

    /// <summary>Find Location For's placement check (vtable +0x54), inferred like <see cref="CanPlaceCreatedObject"/>:
    /// the tile must be inside the buildable area and its tile key empty or the item itself.</summary>
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

    /// <summary>cXMTObject `FUN_00422aee` validates this object's linked parts through the ordinary placement callback
    /// (`FUN_004333c0`) at translated ObjM positions before portal-specific gates run. The distinct +0x1ac association
    /// is used by FUN_00422083 during apply and does not replace this object's placement-validation chain.</summary>
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

    /// <summary>cXPortal `FUN_0046346a` writes global placement error 0x16 for a styled-wall mismatch or 3 for
    /// a non-center requested position. Reset by each <see cref="CanPlaceObject"/> evaluation.</summary>
    public int LastPortalPlacementFailureCode { get; private set; }

    /// <summary>The placement error written by source callbacks where it is mapped. Currently includes the
    /// `0x0b` conflicts that gate cXMTObject's associated-slot retry in FUN_00422083.</summary>
    public int LastPlacementFailureCode { get; internal set; }

    /// <summary>FUN_004549b6 clears DAT_006e4a7c and DAT_006e4a80 before it asks for the placement.</summary>
    internal void ResetPlacementFailure()
    {
        LastPlacementFailureCode = 0;
        LastPlacementBlockingObjectId = 0;
    }

    /// <summary>Neighbor retained for the FUN_004338a5 overlap failure path, which also stores its blocking object.</summary>
    internal short LastPlacementBlockingObjectId { get; set; }

    /// <summary>cXPortal vtable +0x54 (FUN_0046346a) requires the final requested X/Y to be tile centers.</summary>
    private static bool PassesPortalPlacementAlignmentCheck(XObject item, ObjectPosition requested) =>
        item.RuntimeClass != SimsEngine.Formats.ObjectClass.Portal ||
        ((requested.X & 0x0f) == 8 && (requested.Y & 0x0f) == 8);

    /// <summary>cXPortal vtable +0x54 (FUN_0046346a): linked members with a nonzero OBJD WallStyle
    /// require their selected wall segment to have runtime category 1 after the shared placement gate.</summary>
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

    /// <summary>cXObject/cXPerson's route-slot vtable +0x54 request (FUN_004333c0) carries the referenced slot object
    /// in request +0x0c, selecting FUN_00432a70's relative-object branch. That branch keeps the shared bounds, terrain,
    /// root and upper-floor gates but bypasses ordinary floor permission, wall-placement and footprint collision checks.</summary>
    internal bool CanPlaceRouteCandidate(XObject item, ObjectPosition at, XObject? referenceObject,
        int referenceSlotIndex = -1)
    {
        LastPlacementFailureCode = 0;
        if (referenceObject is null) return CanPlaceObject(item, at);
        return CanPlaceReferencedSlot(item, at, referenceObject, referenceSlotIndex);
    }

    /// <summary>cXObject vtable +0x60 (FUN_004355c7, invoked by FUN_00422083 for editor/associated placement):
    /// delegates to vtable +0x54 and additionally reports error 0x0b for centered placement into a selected slot
    /// whose normalized ground code is 5, 6, or 7.</summary>
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

    /// <summary>Runs the moving object's +0x54 relative-placement callback (FUN_004333c0 -> FUN_00432a70)
    /// used by Grab, Drop Onto, Create Object Instance, and Reach checks.</summary>
    internal bool CanPlaceRelativeObject(XObject item, ObjectPosition at, XObject referenceObject,
        int referenceSlotIndex)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(referenceObject);
        return CanPlaceReferencedSlot(item, at, referenceObject, referenceSlotIndex);
    }

    // FUN_00432a70 is the common relative-placement validator used by associated non-person
    // callbacks as well as the person route callback. Keep person-only center-height handling above.
    private bool CanPlaceReferencedSlot(XObject item, ObjectPosition at, XObject referenceObject,
        int referenceSlotIndex)
    {
        LastPlacementFailureCode = 0;
        // FUN_00432a70 (00432a70.c:175-178, 276): the relative branch rejects runtime object Flags bit 2
        // on either object with error 0x0c (PlacementFlags bit 2 is NOT checked here).
        if (((unchecked((ushort)item[ObjectData.Flags]) |
              unchecked((ushort)referenceObject[ObjectData.Flags])) & 0x0004) != 0)
        {
            LastPlacementFailureCode = 0x0c;
            return false;
        }
        // FUN_004333c0/FUN_00432728 forward the slot index when a reference object is present;
        // FUN_00432a70 handles an existing slot occupant after validation by displacing it to the mover's slot 0.
        if (referenceSlotIndex >= 0)
        {
            // FUN_00432a70 indexes the reference's +0x130 vector by request slot + 1; in native,
            // FootprintSlots contains those definition entries without the vector's leading sentinel.
            if (referenceSlotIndex >= referenceObject.FootprintSlots.Count)
            {
                LastPlacementFailureCode = 0x0d;
                return false;
            }
            // FUN_00432a70 compares the selected runtime slot's +0x10 occupant key with the mover's
            // +0xf0 manager key: zero or this mover passes; another occupant returns error 0x0b.
            // FUN_004327d9 writes that same one-based manager key into +0x10. Apply-mode displacement
            // is separate and runs only after this validation succeeds.
            var slot = referenceObject.FootprintSlots[referenceSlotIndex];
            var occupant = slot.OccupantObjectId;
            if (occupant != 0 && occupant != item.ClockManagerIndex)
            {
                LastPlacementFailureCode = 0x0b;
                return false;
            }
            // FUN_00432a70 tests the selected record's normalized Ground code against the moving
            // object's AllowedHeightFlags bit at code - 1 (error 0x18 when disallowed).
            var heightCode = slot.HeightCode;
            var allowedHeightFlags = unchecked((ushort)item[ObjectData.AllowedHeightFlags]);
            if (heightCode == 0 ||
                (allowedHeightFlags & (1 << ((heightCode - 1) & 0x1f))) == 0)
            {
                LastPlacementFailureCode = 0x18;
                return false;
            }
            // The same branch requires moving Weight (+0x80) <= reference SupportStrength (+0x82).
            if (item[ObjectData.Weight] > referenceObject[ObjectData.SupportStrength])
            {
                LastPlacementFailureCode = 0x0e;
                return false;
            }
            // FUN_00432664 walks the reference's container ancestry; accepting the move when
            // that chain reaches the mover would create a containment cycle (error 0x0b).
            if (HasRelativePlacementCycle(item, referenceObject))
            {
                LastPlacementFailureCode = 0x0b;
                return false;
            }
        }
        // Drop Onto passes (-16, -16, 1) as its relative-placement sentinel. The Windows
        // placement callback FUN_004333ef resolves relative requests against the referenced
        // object's position before applying them; validate the same resolved position here.
        var relativePosition = referenceObject.Position;
        if (!PassesCommonPlacementChecks(item, relativePosition, hasRelativeObject: true,
                relativeObjectClockManagerIndex: referenceObject.ClockManagerIndex) ||
            !PassesUpperFloorPlacementCheck(item, relativePosition)) return false;
        return true;
    }

    private bool HasRelativePlacementCycle(XObject item, XObject referenceObject)
    {
        // Sims.exe FUN_00432a70 (00432a70.c:258-264) walks enclosing containers via FUN_00432664,
        // skipping SlotHierNumber == 0 tile-overlap links (Slots[0]) before stepping to real slot parents.
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
        return (flags & 0x0200) != 0 || floorWord != 0;
    }

    /// <summary>Sims.exe FUN_00463a0f assigns a Route ID if needed, then initializes that route's cost entry on
    /// every live portal. FUN_00463970 chooses the first unused positive short across object and portal vectors.</summary>
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

    /// <summary>The shared bounds, cardinal/diagonal wall flags, terrain flags and root-cell collision portion of Sims.exe
    /// FUN_00432a70. Elevation, footprint, relative-slot and remaining class-specific
    /// checks are not represented here yet.</summary>
    private bool PassesCommonPlacementChecks(XObject item, ObjectPosition at, bool hasRelativeObject = false,
        TreeStack? checkStack = null, short relativeObjectClockManagerIndex = 0)
    {
        var tileX = at.X >> 4;
        var tileY = at.Y >> 4;
        var size = LotGeometry.LotDimension;
        if (tileX <= 0 || tileY <= 0 || tileX >= size - 1 || tileY >= size - 1)
        {
            LastPlacementFailureCode = 1;
            return false;
        }
        // FUN_00432a70 rejects placement levels outside 1..3 before inspecting the level tile grid.
        if (at.Height is < 1 or > 3) return false;

        var placementFlags = unchecked((ushort)item[ObjectData.PlacementFlags]);
        // FUN_00432a70 accepts its placement-validation branch only below level 2 or when
        // PlacementFlags 0x1000 is clear; on upper floors that bit produces error 0x1f.
        if (at.Height >= 2 && (placementFlags & 0x1000) != 0) return false;

        // FUN_004790bc reports eligible only for level-1 manager +0x64 cells whose byte is not 0xff.
        // FUN_00432a70 (00432a70.c:79-100) rejects non-water cells when RequireWater (0x2000; 0x0020 in
        // synthetic tests) is set (error 0x2f), and rejects water cells when AllowOnWater (0x0004) is clear
        // unless another object on the tile sets runtime Flags 0x1000 (error 0x1d).
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
                     !ObjectsAtTile(tileX, tileY, at.Height).Any(obj => !ReferenceEquals(obj, item) && (obj.Flags & 0x1000) != 0))
            {
                LastPlacementFailureCode = 0x1d;
                return false;
            }
        }

        // FUN_00432a70 calls FUN_004770d1 for non-person objects except relative placement.
        // PlacementFlags 0x1000 is allowed on level 1 but rejected above it by the outer gate above.
        // Preserve the source-defined diagonal-wall requirements using FUN_00479737's runtime record.
        if (!hasRelativeObject && item.Definition[(int)ObjdField.Type] != XPerson.PersonType &&
            (at.Height == 1 || (placementFlags & 0x1000) == 0) &&
            !PassesWallPlacementCheck(item, tileX, tileY, at.Height)) return false;

        // FUN_00432a70 reads PlacementFlags (+0x9e, data field 42) and the room manager's level-1 Arry 9 pool byte.
        // FUN_00479f0c independently uses the same pool plane and <0x10 threshold when classifying level-1 room tiles.
        if (at.Height == 1 && LotRoomMaps.TryGetValue(1, out var roomMap))
        {
            var groundClass = roomMap.WaterGridByteAt(tileX, tileY);
            var flags = unchecked((ushort)item[ObjectData.PlacementFlags]);
            if ((flags & 0x0800) == 0 && groundClass < 0x10)
            {
                LastPlacementFailureCode = 0x1e;
                return false;
            }
            if ((flags & 0x1000) != 0 && groundClass >= 0x10)
            {
                LastPlacementFailureCode = 0x1f;
                return false;
            }
        }

        // FUN_00432a70 (00432a70.c:134-151) checks AllowOnFloor (0x0001, error 0x13) against floor tiles and
        // level-1 pool deck (Arry 9 == 0x10), and AllowOnTerrain (0x0002, error 0x14) against bare level-1 terrain.
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

        // FUN_00432a70 reads RoomPlacement (+0x86) against bit 2 of the per-level
        // manager byte grid at +0x30. FUN_00433186 writes bit 3 of this same grid
        // while preserving the other bits. A relative-object request skips this branch.
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
        // FUN_00432a70 skips the ordinary root-cell comparison when PlacementFlags bit 0 is set;
        // relative-object requests use their separate slot/occupancy branch.
        if (!hasRelativeObject && (placementFlags & 1) == 0 &&
            key != 0 && key != item.ClockManagerIndex && key != relativeObjectClockManagerIndex &&
            (key > short.MaxValue || ObjectTableEntry((short)key) is not { } rootObj || !PlacementNeighborCallbackFilters(item, rootObj)))
        {
            LastPlacementFailureCode = 0x0b;
            return false;
        }
        if (!hasRelativeObject && !PassesFootprintCollisionCheck(item, at, checkStack)) return false;

        // FUN_00432a70 checks object-data Allowed Height Flags (data field 4, +0x52) after the ordinary
        // placement/footprint validation (lines 160-171 of 00432a70.c). Bit 0 must permit floor placement;
        // relative-object creation takes the source's separate slot/alignment branch and skips this gate.
        if (!hasRelativeObject && (item[ObjectData.AllowedHeightFlags] & 1) == 0)
        {
            LastPlacementFailureCode = 0x18;
            return false;
        }
        return true;
    }

    /// <summary>The ordinary-object and ordinary-Sim AABB branches in Sims.exe FUN_004338a5.</summary>
    private bool PassesFootprintCollisionCheck(XObject candidate, ObjectPosition at, TreeStack? checkStack)
    {
        // FUN_004338a5 still reaches its final footprint-wall gate at level 3, but its
        // FUN_004325af neighboring-root lookup returns no root for that level.
        var candidatePerson = candidate.Definition[(int)ObjdField.Type] == XPerson.PersonType
            ? candidate as XPerson ?? throw new InvalidOperationException("Person placement requires an XPerson instance.")
            : null;
        // FUN_004338a5 sends ghosts and living ghosts through a short-circuit branch before the general scan.
        // A live second slot object or a resolved parent context sends them back to that scan; otherwise placement
        // takes the source's short circuit.
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
                // FUN_004338a5's candidate-person branch reads the NEIGHBOR's +0x5a flags,
                // regardless of its class. Doors set bit 4 to allow people through their footprint.
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
                // FUN_004338a5's person-neighbor branch ignores ghosts. With candidate Flags bit 2 clear,
                // bit 4 suppresses the check; with bit 2 set, bit 1 selects the collision check instead.
                // Person candidates use the same function-5 path here when they are in the general (non-ghost) scan.
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
                // FUN_004338a5's ninth root offset is (0, 0), as confirmed by the two nine-entry
                // tables at 0x006c2924/0x006c2948 in analysis/Sims.original.exe. At this center
                // root, shared Exclusive Placement Flags bit 0 rejects placement before the
                // ordinary Flags-bit-2 overlap bypass.
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
        // FUN_004338a5's final FUN_00476f33 gate is skipped when PlacementFlags 0x0400 (+0x9f byte bit 2) is set.
        if ((unchecked((ushort)candidate[ObjectData.PlacementFlags]) & 0x0400) == 0 &&
            FootprintIntersectsWall(bounds, at.Height))
        {
            return false;
        }
        return true;
    }

    /// <summary>FUN_00423181, installed at vtable +0x5c for cXMTObject and cXPortal, returns true when the
    /// proposed object's linked chain (master +0x190, then next +0x18c) contains the neighbor.</summary>
    private bool PlacementNeighborCallbackFilters(XObject candidate, XObject other)
    {
        // The cXPerson vtable's +0x5c slot is FUN_00433c4e, a pointer-equality/self check.
        // The base and multi-tile vtables use FUN_00423181, which follows +0x190/+0x18c.
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

    /// <summary>FUN_00435968 maps wall-placement bits 0/2 (+0x64) to directional manager states 2/8, then uses the
    /// rotation table initialized by FUN_0053bc14. Its four-way rotation advances one cardinal quarter-turn
    /// per pair of direction values.</summary>
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

    /// <summary>FUN_00432664 climbs slot-hierarchy-zero parents, then resolves the final object's parent key.</summary>
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

    /// <summary>FUN_004338a5 at 00433ac7 binds ECX to the candidate for function 5 with the neighbor's key.
    /// If that returns false, 00433ae2 binds ECX to the neighbor for its function 5 with the candidate's key.
    /// Each callback uses its receiver's behavior scope and synchronous runner.</summary>
    private bool PlacementFunctionFiveAllowsOverlap(XObject candidate, XObject other, TreeStack? checkStack)
    {
        // The decompiled path always runs the check tree in the active VM. A caller without that VM cannot claim
        // the overlap was approved, and a missing behavior scope cannot provide a runnable function tree.
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

    /// <summary>FUN_00433dbb's non-person form: base square from OBJD +0x86 (or 16), then the four type-record
    /// +0x9a extent nibbles rotated by the object's direction. Person route extensions and ghost span use separate
    /// source branches and are handled elsewhere.</summary>
    internal static FootprintBounds PlacementFootprintBounds(XObject item, ObjectPosition at)
    {
        // FUN_00433dbb calls FUN_00435244, which reads OBJD +0x86 (field 67 / TileWidth). ObjectData.RoomPlacement
        // is a separate mutable field at the same byte offset and is used by FUN_00432a70's room-grid branch.
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

    /// <summary>FUN_00476f33 rejects a proposed footprint that strictly crosses a blocking wall segment.</summary>
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
        // FUN_0053bdc6 rotates the queried wall record by -Direction into object-local coordinates.
        // Rotating each required local mask by +Direction is the equivalent world-space check.
        var rotation = direction >> 1;

        // FUN_004770d1 examines local placement bits in mask order 2,1,4,8. These correspond to
        // WallPlacementFlags bits 0,3,1,2. FUN_0053bc14 supplies the four cardinal segment rotations.
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

    /// <summary>FUN_0053bc14's cardinal mask table rotates bits 1,2,4,8 by one position per quarter-turn.</summary>
    public static uint RotatePlacementWallBit(uint bit, int turns)
    {
        for (var turn = 0; turn < turns; turn++) bit = bit == 8 ? 1u : bit << 1;
        return bit;
    }

    /// <summary>Find Location For's placement action (vtable +0x58): writes the position, like
    /// <see cref="PlaceCreatedObject"/>.</summary>
    public void PlaceObject(XObject item, ObjectPosition at, TreeStack? checkStack = null,
        XObject? placementContainer = null, short placementSlotNumber = 0,
        MultiTilePlacementCursorGroup? cursorGroup = null) =>
        PlaceObjectCore(item, at, checkStack, placementContainer, placementSlotNumber, directClassCallback: false, cursorGroup);

    /// <summary>Constructs the temporary cursor-chain state used by the native editor before it invokes the regular
    /// placement callback. The returned group is short-lived and does not enter the world's object manager.</summary>
    public MultiTilePlacementCursorGroup CreatePlacementCursorGroup(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return MultiTilePlacementCursorGroup.Create(item, MultiTileMembers(item));
    }

    /// <summary>Evaluates an existing-object editor drag using a temporary cursor-chain projection. Direction flags are
    /// the four values sent by the native SDL editor; the lot objects' orientation and ObjM offsets are restored after
    /// the source placement validator has checked the proposed facing.</summary>
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

    /// <summary>Commits the native editor's existing-object drag through the same +0x58 placement path used by
    /// SimAntics. The temporary cursor group supplies +0x1b2 to the source error gate without storing it on lot items.</summary>
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

    /// <summary>Validates Buy/Build editor placement (FUN_00422083): first checks ordinary floor/multi-tile placement,
    /// then on error 0x0b for a single-tile object (MasterId == 0) scans objects rooted at the target tile for a
    /// footprint slot whose HeightCode is permitted by AllowedHeightFlags and passes CanPlaceReferencedSlot.</summary>
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

    /// <summary>Turns a created, not yet placed object group to face <paramref name="direction"/>, rebuilding the
    /// multi-tile offsets as ApplyEditorFacing does.</summary>
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

    /// <summary>Sims.exe cXCursorObject::Rotate (00421c17.c:38-41, 73-76) rotates contained slot children across
    /// Slots.Skip(1) and FootprintSlots when MasterId == 0 and RotationNotches is 2 (or 0 when uninitialized).</summary>
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

    /// <summary>Sims.exe FUN_00432664: walks past tile-overlap hierarchy links (SlotHierNumber == 0) and resolves the
    /// enclosing slot container (SlotHierNumber &gt; 0 or ContainerId &gt; 0) via ObjectTableEntry.</summary>
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
        // A route SLOT uses a zero-based definition index, while FUN_00432771 stores its
        // relationship at index + 1. The source object's +0x130 vector already owns that
        // definition-backed entry even when the saved relationship array has not been loaded.
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
        // FUN_004333ef substitutes the referenced object's position for relative placement requests.
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
                // FUN_004333ef passes param_4 through FUN_00432728 as the placement container. Its function-9 callback
                // reads the container's own data (including My Slot); running it on the mover's saved stack gives it
                // the mover's slot vector instead. Keep the same tree and stack-object key, but bind `me` to the container.
                TreeRunner.RunWithParameters(this, placementContainer, containerScope, containerTree, 0, []);
            }
            // FUN_004333ef calls FUN_004403c5 after its function-9 callbacks when either coordinate changed.
            if (originalPositions.TryGetValue(member, out var original) &&
                (original.X != member.Position.X || original.Y != member.Position.Y))
                member.PositionRefreshPending = true;
        }

        var placedMembers = PlaceObjectOrMultiTileGroup(item, placementPosition,
            groupCallback ? ApplyMemberCallbacks : null, placementSlotNumber, checkStack, directClassCallback,
            cursorGroup, memberContainer, memberSlot, RecordPriorContainer);
        if (groupCallback)
        {
            // FUN_00422c62 runs function 9 once per linked member after the member placement callbacks.
            foreach (var member in placedMembers)
            {
                var tree = FunctionTreeId(member, 9);
                if (tree != 0) checkStack!.RunToReturn(member.TypeRecord?.BehaviorScope, [], false, tree, 0);
            }
        }
        else ApplyMemberCallbacks(item);
        foreach (var oldContainer in priorContainers)
            RunFunctionToCompletion(oldContainer, 9);
        // cXPortal vtable +0x58 reaches FUN_004632af after linked-member placement callbacks.
        if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal)
        {
            foreach (var member in placedMembers) RefreshPortalWallStyleOverlay(member);
            // FUN_0046322f reinitializes the live portal manager's allocated route costs after the refresh.
            foreach (var portal in WalkOrder.Where(candidate => candidate.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal))
                portal.ResetAllocatedPortalRouteCosts();
        }
    }

    /// <summary>A restored lot reaches cXPortal's +0x58 wall-style refresh (FUN_004632af) for each saved portal part.</summary>
    public void RefreshSavedPortalWallStyles()
    {
        foreach (var portal in WalkOrder.Where(item => item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal).ToArray())
        {
            RefreshPortalWallStyleOverlay(portal);
        }
    }

    /// <summary>The source-backed wall-style write performed by cXPortal FUN_004632af through FUN_004633e3.
    /// FUN_00479954's adjacent runtime-cell updates and room refresh remain separate.</summary>
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
            // Keep the separate FUN_00463648 wall-notification path distinct from ordinary portal placement.
            MarkPortalWallRefreshRequested(member.Position.Height, tileX, tileY);
        }
        else
        {
            // Find Location calls the cXPortal +0x58 callback (FUN_004632af) with its final argument set;
            // after FUN_00479954 it requests geometry refresh through FUN_00540ecf, not FUN_00540eb1.
            MarkPortalGeometryRefreshRequested(member.Position.Height);
        }
    }

    /// <summary>FUN_00435051 refreshes the placed object's wall-adjacency byte and runs function 6 only when it changes.</summary>
    private void RefreshPlacementWallAdjacency(XObject item, TreeStack? checkStack)
    {
        var tree = FunctionTreeId(item, 6);
        // FUN_00435051 starts with FUN_004351c8, which returns false when function 6 is absent.
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

    /// <summary>Create Object's placement action (vtable +0x58): applies the source tile-grid remove/add around the
    /// accepted position.</summary>
    public void PlaceCreatedObject(XObject created, ObjectPosition at)
    {
        ArgumentNullException.ThrowIfNull(created);
        PlaceObjectOrMultiTileGroup(created, at);
    }

    /// <summary>Commits the user's buy-catalog placement. Mac cXCursorObject::DropObject dispatches UserPlace
    /// (vtable +0x6c); cXObject::UserPlace (004356d4.c) and cXMTObject::UserPlace (00422ecc.c) run Place (+0x58)
    /// first and then invoke function entry 11 on each member and its contained slot children.</summary>
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

    /// <summary>Runs the function-11 tree that Sims.exe UserPlace (004356d4.c:10-51, 00422ecc.c:11-53) invokes via
    /// FUN_00422d79(0xb, 0, 0) for each placed member and each contained child in Slots.Skip(1) after Place succeeds.</summary>
    private void RunEditorUserPlacementCallbacks(IReadOnlyList<XObject> members)
    {
        var callerLocation = CurrentExecutionLocation;
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
            CurrentExecutionLocation = callerLocation;
        }

        void RunUserPlacementCallbackOnObject(XObject target)
        {
            var tree = FunctionTreeId(target, 11);
            var scope = target.TypeRecord?.BehaviorScope;
            if (tree == 0 || scope is null) return;
            TreeRunner.RunWithParameters(this, target, scope, tree, 0, []);
        }
    }

    /// <summary>Sims.exe FUN_00451c87: wakes placed objects whose clock-manager record has SimulateOnPause (bit 1, 0x00020000)
    /// when they are waiting on Sleep (opcode 0) or Idle For Input (opcode 0x11).</summary>
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

    /// <summary>Places a newly created object into the first real slot (Create Object mode 2). The source placement
    /// request uses slot 0, then FUN_00432771 increments it to hierarchy slot 1; hierarchy slot 0 remains the separate
    /// tile-overlap ancestry link.</summary>
    public bool PlaceCreatedObjectInHand(XObject holder, XObject created)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(created);
        if (ReferenceEquals(holder, created) || created[ObjectData.ContainerId] != 0 ||
            holder[ObjectData.ContainerId] == created.ClockManagerIndex) return false;
        return TryPlaceInFirstRealSlot(holder, created);
    }

    /// <summary>Moves an existing object into a holder's first real slot. The slot-array entry and hierarchy number
    /// are 1 (source hierarchy slot 1); ObjectData.SlotNumber remains the zero-based slot argument 0.</summary>
    public bool TryPlaceInFirstRealSlot(XObject holder, XObject item)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(item);
        if (ReferenceEquals(holder, item) || IsInSlotSubtree(holder, item)) return false;
        // The source validates the holder's authored first-real-slot record before placement. Do not
        // synthesize hand capacity for objects whose definition/runtime slots do not contain entry 1.
        if (holder.Slots.Count <= 1) return false;

        var heldKey = item.ClockManagerIndex;
        if (holder.Slots.Count > 1 && holder.Slots[1] == heldKey &&
            item[ObjectData.ContainerId] == holder.ClockManagerIndex &&
            item[ObjectData.ParentId] == holder.ClockManagerIndex &&
            item[ObjectData.SlotHierNumber] == 1) return true;
        if (holder.Slots.Count > 1 && holder.Slots[1] != 0) return false;

        // Reject malformed ParentId chains or cyclic real containment (FUN_00432664 skips SlotHierNumber == 0
        // tile-overlap links, which UnplaceObjectFromGridOrSlot detaches when item enters holder.Slots[1]).
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

        // FUN_00421ea5 delegates non-leads to the lead; FUN_00422c62 moves each +0x18c member using its ObjM offsets.
        // FUN_00421ea5's +0x190 branch delegates only non-master members to their master,
        // translating by the requesting member's offsets. +0x1ac is a separate, gated
        // association callback and must never replace this ordinary group move by pointer alone.
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

        // FUN_00421ea5's association gate belongs to the root branch only. A non-lead request delegates
        // straight to the master's +0x58 and does not re-enter the master wrapper.
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
                    ? 0 // TypeRecord +0x24 is initialized and reset to zero; no writer was found.
                    : associated.TypeRecord?.AlternativeObjectPrice ?? 0;
                // FamilyBudget supplies FUN_0046d9e5's +0x50 SimlessBuildMode override and +0x90 funds.
                // A constructed world without a family budget retains the source manager's zero-initialized +0x90.
                var placementContextValue = FamilyBudget?.ReadFamilyBudget() ?? 0;
                if (placementContextValue < associatedValue && associatedValue != 0 &&
                    (cursorGroup?.HasError15Gate(lead) ?? false))
                    LastPlacementFailureCode = 0x15;
                else
                    lead.MultiTileMoveFlag1B0 = 1;
                // Track the delegating root, not the callback receiver. The receiver's +0x58 may
                // itself enter FUN_00421ea5 and evaluate its own association; only a cycle back to
                // this root must be suppressed.
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
                // The source +0x74 sidecar callback is applied after the associated +0x58 move.
                RefreshAssociatedPlacementHelper(associated);
            }
        }

        var requestedOffset = MultiTileOffset(item);
        // A non-lead request has already entered FUN_00421ea5's +0x190 branch, which subtracts
        // that requesting member's own offsets before calling the lead's +0x58. A lead request
        // enters FUN_00421ea5's root branch directly, so it must not subtract its own offset here.
        var delegatedTarget = ReferenceEquals(item, lead)
            ? at
            : new ObjectPosition(at.X - requestedOffset.X, at.Y - requestedOffset.Y,
                at.Height - requestedOffset.Height);
        var leadOffset = MultiTileOffset(lead);
        // FUN_00421ea5's root branch initialized +0x1b0 before the association gate. Preserve a
        // successful gate's update while FUN_00422c62 moves the ordinary linked group.
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
                BeginObjectRemoval(part, 1); // FUN_00422c62 (00422c62.c:43) tests param_2 (delegatedTarget) via FUN_00422665.
            movedParts.Add(part);
            part = nextId == 0 ? null : ObjectById(nextId);
        }

        // FUN_00421ea5 caches each member's settled position and resets its movement scalar after the group pass.
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

        // FUN_00422083's alternate manager scan is entered only for error 0x0b and an associated
        // definition with no MasterId. The scan is the preorder of objects rooted in the request tile.
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
        // FUN_00422083 restores the source placement error after exhausting the iterator. Its
        // special 0x24 result is selected only for the corresponding AllowedHeightFlags mask.
        var allowedMask = unchecked((ushort)associated[ObjectData.AllowedHeightFlags]);
        LastPlacementFailureCode = allowedMask != 0 && (allowedMask & 0xfff7) == 0 ? 0x24 : 0x0b;
        return false;
    }

    /// <summary>FUN_00422083's candidate-member branch advances the candidate direction in quarter turns while
    /// the corresponding adjacent tile remains clear according to FUN_004324fc; four clear checks restore its
    /// original direction. Root candidates skip this offset search.</summary>
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

    /// <summary>cXMT/cXPortal vtable +0x4c (FUN_0042296a), reached by an accepted fallback candidate whose
    /// OBJD MasterId is zero. Its +0x50 callback removes the linked group, then it applies the rotation-notch delta;
    /// a nonzero rotation is revalidated at the saved anchor and reapplied through the direct +0x58 group callback.</summary>
    private void ApplyAssociatedPlacementAdjustment(XObject mover, int directionDelta, TreeStack? checkStack)
    {
        var group = MultiTileMembers(mover);
        var savedPosition = mover.Position;

        // FUN_00422db9 first runs function 10 once for every member; FUN_004331d4 then repeats it
        // for OBJD masters before each member's removal callback.
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
        // FUN_00422db9 performs one additional manager refresh on the group root after its per-member +0x50(1) calls.
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

    /// <summary>cXMT/cXPortal +0x88 (FUN_00422a6e) rebuilds each member's ObjM offsets from its saved +0x194/+0x198/+0x19c
    /// values, relative to the lead, then rotates the horizontal pair by the lead's current direction.</summary>
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
        // The receiver's +0x120 helper is type-record gated and remains optional in the native model.
        // When present, FUN_00433c58 -> helper +0x24 -> helper +0x20(1) clears its three float values
        // and marks the helper's +0xd9 byte active.
        associated.AssociatedPlacementHelper?.RefreshAfterMove();
    }

    private static void UpdateMultiTileMoveRequest(XObject item, ObjectPosition requested, short sourceParam5)
    {
        // FUN_00421ea5 compares the requested coordinates with cached +0x1b4/+0x1b8/+0x1bc.
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
        // FUN_0042339b derives +0x1a0 from +0xf8 and +0x1a4 from +0xf4. ObjectHead.X/Position.X already
        // represent +0xf8, and Y represents +0xf4. FUN_00422055's swap is for the executable's raw two-word
        // position structure, whose first word is +0xf4; repeating it on this typed position transposes parts.
        return new ObjectPosition((state?.Value0x1A0 ?? 0) << 4, (state?.Value0x1A4 ?? 0) << 4, state?.Value0x1A8 ?? 0);
    }

    /// <summary>SimAntics errors contained by the clock-manager walk (inferred continuation policy): the object id and
    /// the error text, in occurrence order. The failing object's saved thread is dropped.</summary>
    public List<ThreadError> ThreadErrors { get; } = [];
    /// <summary>Check-tree Break Point errors contained while evaluating interaction candidates. Sims.exe's
    /// FUN_00458485 runs these in the check-tree runner; a rejected candidate must not terminate its owner's saved
    /// person thread. The failed check remains visible here for the native host diagnostics.</summary>
    public List<InteractionGuardError> InteractionGuardErrors { get; } = [];
    /// <summary>Opt-in interpreter trace used by saved-lot diagnostics; production worlds leave this disabled.</summary>
    public bool CaptureExecutionTrace { get; set; }
    public List<SimulationTraceEntry> ExecutionTrace { get; } = [];
    public List<AutonomyAttemptDiagnostic> AutonomyAttemptTrace { get; } = [];
    public List<SimulationPrimitiveResultTrace> PrimitiveResultTrace { get; } = [];
    public List<SimulationRouteCandidateTrace> RouteCandidateTrace { get; } = [];
    internal string LastRouteCandidateFailureReason { get; set; } = string.Empty;
    public List<SimulationNativeRouteTrace> NativeRouteTrace { get; } = [];
    public List<SimulationRouteSearchTrace> RouteSearchTrace { get; } = [];
    public List<SimulationTreeCallTrace> TreeCallTrace { get; } = [];
    /// <summary>Opt-in named-tree lookup evidence for source-backed interaction diagnostics.</summary>
    public List<SimulationNamedTreeLookupTrace> NamedTreeLookupTrace { get; } = [];
    public List<SimulationInteractionTestTrace> InteractionTestTrace { get; } = [];

    /// <summary>The game's string language for STR# lookups (1 is English); the installed Complete Collection reports 1.</summary>
    public byte Language { get; set; } = 1;

    /// <summary>FUN_00470383/FUN_004705f2: entry <paramref name="index"/> of string table <paramref name="table"/> from
    /// the stack object's behavior scope (private, semi-global, global), or the running object's when it has none.
    /// Inferred: the operand's holder flag is ignored and the table is read in <see cref="Language"/>.</summary>
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

    /// <summary>FUN_00425ba7 case 0: the lot object whose type record (+0x11c) is the neighbor's +8 record, read as
    /// its +0xf0 key. Zero when the neighbor or its record is missing, or no object shares the type.</summary>
    public short NeighborObjectKey(short oneBasedIndex)
    {
        var neighbor = NeighborManagerEntry(oneBasedIndex);
        if (neighbor is null || neighbor.TypeRecordKey == 0) return 0;
        foreach (var item in WalkOrder)
            if (item.TypeRecord?.Key == neighbor.TypeRecordKey) return item.ClockManagerIndex;
        return 0;
    }

    /// <summary>FUN_00425ba7 case 1 ("belongs in house"): with the lot's family object present, FUN_00410285 searches its
    /// +0x12c member-key vector for the neighbor's type key (FUN_0045741b). Inferred: an empty vector stands for the
    /// missing family object, since only a loaded household fills it.</summary>
    public short NeighborBelongsToHouse(short oneBasedIndex)
    {
        var neighbor = NeighborManagerEntry(oneBasedIndex);
        if (neighbor is null || NeighborInteractionManagerTypeKeys.Count == 0) return 0;
        return NeighborInteractionManagerTypeKeys.Contains(neighbor.TypeRecordKey) ? (short)1 : (short)0;
    }

    /// <summary>FUN_00425ba7 case 2: the neighbor's +0xe8 word, person-data field 58.</summary>
    public short NeighborWordE8(short oneBasedIndex) =>
        NeighborManagerEntry(oneBasedIndex) is { } neighbor ? neighbor.PersonData[58] : (short)0;

    /// <summary>FUN_00425ba7 case 5 and FUN_00427192: while the neighbor's +0x24 flag is set, count the other
    /// friend-eligible neighbors (FUN_00423f63) with a nonzero +0xee word (person-data field 61) whose relationship
    /// lists with this neighbor both hold an entry at or above the threshold (FUN_0042720f); the count is cached at +0x20.</summary>
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

    /// <summary>FUN_00425ba7 case 6: for the stack object's one-based neighbor-table entry, walk the neighborhood's
    /// family vector (+0x154/+0x158), find the first family whose +0x12c member list contains the neighbor's GUID
    /// (FUN_00401f73/FUN_00410285), and return its +0x110 house number (falling back to InteractionLookupOrder when
    /// NeighborhoodFamilies is absent). A missing entry or no match returns 0.</summary>
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

    /// <summary>FUN_00425ba7 cases 7 and 8: resolve the stack neighbor through FUN_0042573e, then find the first
    /// family (or interaction-manager object fallback) whose +0x10c family number matches the neighbor's +0xee word.
    /// If its +0x110 house number is nonzero, return bit 0 or bit 1 of its +0x138 flags word respectively.</summary>
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

    /// <summary>FUN_00425ba7 case 9 and FUN_00427263/FUN_00427c21: resolve the stack neighbor's +0xee family (or
    /// interaction-object fallback) and count eligible mutual-friend entries in the ordered neighbor table.</summary>
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

    /// <summary>Counts non-family neighbors who are friends (`>= friendshipThreshold`, default 50) with at least one
    /// member of <paramref name="family"/> (`00427c21.c:1-48`, `0042720f.c`).</summary>
    public static int CountFamilyFriends(Family family, Neighbors neighbors, int friendshipThreshold = 50)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(neighbors);
        return CountFamilyFriends(family.Members, neighbors.Entries, friendshipThreshold);
    }

    /// <summary>Counts non-family neighbors who are friends (`>= friendshipThreshold`, default 50) with at least one
    /// member in <paramref name="familyMemberGuids"/> (`00427c21.c:1-48`, `0042720f.c`).</summary>
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

    /// <summary>Counts non-family neighbors in this simulation world who are friends with at least one member of
    /// <paramref name="family"/> (`00427c21.c:1-48`).</summary>
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

    /// <summary>Computes and updates the lot save house-info globals (`SimulationGlobal.LotSize` = 23,
    /// `SimulationGlobal.LotHasHouse` = 22, `SimulationGlobal.FilterFlagsForTransitScreen` = 37) and returns the
    /// `FAMI.Flags` baby/phone mask (`0x1` if a baby is on the lot, `0x2` if a phone is on the lot) matching
    /// `0041607b.c:16-40`, `004789d4.c`, and `00444cf9.c:10-211`.</summary>
    public int ComputeSaveHouseInfo(out bool lotHasHouse, out int lotSize, out int transitFilterFlags, out int familyFlagsMask)
    {
        lotSize = LotRoomMaps.Values.FirstOrDefault()?.Width is { } width && width > 0
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

    /// <summary>Convenience overload of <see cref="ComputeSaveHouseInfo(out bool, out int, out int, out int)"/>.</summary>
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

    /// <summary>DAT_006c4388, loaded from FCNS variable "friendship threshold" by FUN_0045928c (default 25).</summary>
    public int FriendshipThreshold { get; set; } = 25;
    /// <summary>FCNS 2 "yard score multiplier", read by Sims.exe FUN_00424260 into DAT_006e4a20 and applied to the sum of outside room scores by FUN_00416890. The executable's 0x00666cc0 fallback is 0.33f.</summary>
    public float HouseYardScoreMultiplier { get; private set; } = 0.33f;
    /// <summary>FCNS 2 "upkeep penalty per dirty object", loaded by FUN_00424260 into DAT_006e4a1c; Sims.exe fallback is 6.</summary>
    public int HouseUpkeepPenaltyPerDirtyObject { get; private set; } = 6;
    /// <summary>FCNS 2 "upkeep penalty per broken object", loaded by FUN_00424260 into DAT_006e4a18; Sims.exe fallback is 15.</summary>
    public int HouseUpkeepPenaltyPerBrokenObject { get; private set; } = 15;
    /// <summary>FCNS 2 "fill value for layout history", loaded by FUN_00424260 into DAT_006e4a28; Sims.exe fallback is 200.</summary>
    public int HouseLayoutHistoryFillValue { get; private set; } = 200;
    /// <summary>The native autonomy tuning loaded by FUN_0045928c from FCNS 2, with Sims.exe defaults for missing values.</summary>
    public FindBestInteractionTuning SimulatorTuning { get; set; } = new();
    /// <summary>The FCNS room, floor, wall and lighting score factors loaded by FUN_004666a9 before FUN_00466d14.</summary>
    public SimsEngine.World.RoomScoreTuning RoomScoreTuning { get; set; } = new();
    /// <summary>Sims.exe DAT_006c2970, set by FUN_004322cf from the player's Free Will option. Null means the
    /// caller has not supplied this global and legacy host-backed autonomy state remains authoritative. The same
    /// setter writes effect-table word 0x1e through FUN_0046dae0 when the lot manager is present.</summary>
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
                // Mac cXObject::SetFreeWill writes cSimulator global 30 as well as
                // the option state. The effect-table word above is the Windows
                // setter's additional path (FUN_004322cf / FUN_0046dae0).
                this[SimulationGlobal.FreeWill] = sourceValue;
            }
        }
    }
    /// <summary>Global lot-table effect words written by Sims.exe FUN_004322cf/FUN_0046dae0. Kept at world scope
    /// because this table belongs to the lot manager rather than any individual cXObject instance.</summary>
    public Dictionary<short, short> GlobalObjectTableEffectWords { get; } = [];
    /// <summary>The type-3 route vector built by Sims.exe FUN_004405c7 from SLOT resource 100 through FUN_0046e3ed.</summary>
    public List<RoutingSlotRequest> LotRoutingSlots { get; } = [];
    /// <summary>FUN_00432644's routing-enabled gate. Native lot startup has routing enabled unless the caller overrides it.</summary>
    public bool RoutingEnabled { get; set; } = true;
    /// <summary>The global person-state object at [DAT_006E486C]+0x1c.</summary>
    public GlobalPersonState GlobalPerson { get; } = new();
    /// <summary>Whether DAT_006E486C's lot-manager pointer exists, the source gate for GenericSimsCall 0x2b.</summary>
    public bool LotUpkeepManagerPresent { get; set; }
    /// <summary>The pointer at [[DAT_006E486C]]+0x10 read by GenericSimsCall 0x04; null preserves its false short-circuit.</summary>
    public XObject? NeighborInteractionSubject { get; set; }
    /// <summary>The +0x12c/+0x130 raw key vector on [DAT_006E486C]+0x10 searched by FUN_0041022e.</summary>
    public List<int> NeighborInteractionManagerTypeKeys { get; } = [];
    /// <summary>The short at [[DAT_006E486C]+0x1c]+0x24 read by FUN_004074bc for FUN_0042a2fe's state-class gate.</summary>
    public short NeighborInteractionGlobalStateValue { get; set; }
    /// <summary>Global reset fields passed through FUN_004FC469 for GenericSimsCall 0x03.</summary>
    public GenericSimsCall03ManagerState GenericSimsCall03State { get; } = new();
    /// <summary>The global object at DAT_006E4F10, used by GenericSimsCall 0x23 to store a person pointer.</summary>
    public XObject? GenericSimsCall23PersonSlotBase { get; set; }
    /// <summary>The lot manager's ordered +0x148/+0x14c object-pointer vector, indexed from one by FUN_0042573E.
    /// This is distinct from neighbor person records in <see cref="Neighbors"/>.</summary>
    public List<XObject?> NeighborTableEntries { get; } = [];
    /// <summary>The selected family's ordered FAMI member keys, loaded by FUN_00410561 into family +0x12c and indexed
    /// by FUN_004102E1/FUN_004102AB. FUN_004E9918 maps each key through the person-record table; vector order matters.</summary>
    public List<int> ActivePersonRecordKeys { get; } = [];
    /// <summary>The ordered person-record keys from DAT_006F2648, one per 0x44-byte record. FUN_004E988F searches
    /// this list linearly; FUN_004E8D71 reads the selected record's first dword.</summary>
    public List<int> PersonRecordKeys { get; } = [];
    /// <summary>The stored neighbor id (+4 in a 0x44-byte record) used by FUN_004ea2ce when the person is not on-lot.</summary>
    public List<short?> PersonRecordNeighborIds { get; } = [];
    /// <summary>Whether a selected FAMI object backs the active-person vector. FUN_004E9918 returns -1 when absent.</summary>
    public bool ActivePersonManagerPresent { get; set; }
    /// <summary>The +0x10C value on the object at lot-manager +0x10, read by GenericSimsCall 0x1A.</summary>
    public short? StoredSlotPersonId { get; set; }
    /// <summary>Whether the lot manager's +0x14 object exists, the early return in FUN_004E99D7.</summary>
    public bool PersonRecordManagerPresent { get; set; }
    /// <summary>The optional opaque 0x6c-byte helper object at DAT_006DE804, allocated by FUN_00405376/FUN_004053C4
    /// and returned by FUN_0040542D for GenericSimsCall 0x22/0x24. It is not a cXObject.</summary>
    public GenericSimsCallRecordTarget? GenericSimsCallRecordCallbackTarget { get; set; }
    /// <summary>The raw DAT_006E4F14 override flag used by FUN_004E99D7's selected-person gates.</summary>
    public bool PersonSelectionOverride { get; set; }
    public short[] Globals { get; } = new short[GlobalCount];
    public short[] NeighborhoodData { get; } = new short[16];
    /// <summary>Lot-manager dword at +0x164 used by FUN_0042730F's special object-value gate.</summary>
    public int? GenericSimsCall05ManagerWord164 { get; set; }
    /// <summary>The configurable architecture-value multiplier loaded by FUN_00424260 into DAT_006E4A38.</summary>
    public float? ArchitectureValueMultiplier { get; set; }
    /// <summary>Raw words at the active family/global object offsets +0x50, +0x90, +0x9c, +0xa0 and +0xa4.
    /// FUN_0042730F/FUN_0046D9E5 consume these only when the +0x164/+0x110 gate matches.</summary>
    public int? ObjectValueGlobalWord50 { get; set; }
    public int? ObjectValueGlobalWord90 { get; set; }
    public int? ObjectValueGlobalWord9C { get; set; }
    public int? ObjectValueGlobalWordA0 { get; set; }
    public int? ObjectValueGlobalWordA4 { get; set; }
    public short[] CheckTreeAds { get; set; } = [];
    /// <summary>The ordered key/class pairs at the state manager's +0x174..+0x178, searched linearly by FUN_0042ba25.
    /// Entry order matters when duplicate state keys exist.</summary>
    public List<StateClassEntry> StateClasses { get; } = [];
    /// <summary>Native clip metadata indexed by (Animate Sim source scope, animation-table entry). Populated from the
    /// installed Global.iff and avatar animation resources by the simulation server.</summary>
    public Dictionary<(byte Source, bool Child, int Index), AvatarAnimationClip> AvatarAnimationTable { get; } = [];
    /// <summary>ObjSelector-private STR# 150 overrides, keyed by the selector's object GUID and standard animation index.</summary>
    public Dictionary<(uint ObjectGuid, int Index), AvatarAnimationClip> CustomWalkAvatarAnimationTable { get; } = [];
    /// <summary>Global age/gender fallback tables used by Mac GetStdSkill when the ObjSelector STR# 150 entry is empty.
    /// Keys are the original Global STR# resource ID and zero-based StdAnimIdx.</summary>
    public Dictionary<(ushort TableId, int Index), AvatarAnimationClip> StandardWalkFallbackAnimationTable { get; } = [];
    /// <summary>Object-scope animation clips keyed by the behavior code owner's type GUID, child/adult table and table index.</summary>
    /// <summary>Object-selector tables 129-132, loaded from each object's private IFF. Sims.exe
    /// FUN_0059dbf8 selects the table by actor/secondary-Sim age; the table id is therefore retained here.</summary>
    public Dictionary<(int ObjectKey, ushort TableId, int Index), AvatarAnimationClip> ObjectAvatarAnimationTable { get; } = [];
    /// <summary>Dispatches authored pack sound cues when a native animation crosses their source timestamp.</summary>
    public Action<short, AvatarAnimationSoundEvent>? AnimationSound { get; set; }
    /// <summary>Resolves SAnimator's marker-derived stride distances against the Sim's selected original avatar skeleton.</summary>
    /// <summary>Mac HouseViewer scrolling makes cXPerson::Simulate pass false to SAnimator::Tick, selecting
    /// VitaBoy::AnimationTickRootOnly and leaving animation practices at their current phase.</summary>
    public bool AvatarAnimationRootOnly { get; set; }
    /// <summary>The +0x194/+0x198 scope vector owned by the interaction-scope manager. FUN_0042A8E3 keys entries by
    /// the object's +0x5ca word; multiple objects with the same key share one scope.</summary>
    public Dictionary<short, InteractionScopeTable> InteractionScopes { get; } = [];
    /// <summary>FUN_004257BA/FUN_004257EF cache slots for the type records whose keys initialize person +0x5ca.</summary>
    public Dictionary<int, short> PersonTypeRecordSlots { get; } = [];
    public ISimulationHost? Host { get; set; }
    /// <summary>Native presentation hooks for the Play Sound Event and Stop All Sounds primitives. They are separate
    /// from the full simulator host because audio does not participate in behavior decisions or saved game state.</summary>
    public Func<string, short, bool, bool, bool>? PlaySoundEvent { get; set; }
    public Action<short>? StopOwnerSounds { get; set; }
    /// <summary>Raised when an object's active sounds should be stopped due to destruction (FUN_004323e0) or reset (FUN_0043206d / FUN_0045e8fd).</summary>
    public event Action<short>? ObjectSoundsStopped;
    /// <summary>Native presentation hook for the UI Effect primitive's tutorial flashes (kind, value, on).</summary>
    public Action<TutorialFlashKind, short, bool>? TutorialFlash { get; set; }
    /// <summary>Object ids currently owning a positional HIT sound, exposed to scene export for owners without art.</summary>
    public HashSet<short> PositionalSoundOwners { get; } = [];

    /// <summary>Stops active HIT/UTK/animation sounds owned by object <paramref name="id"/> during object
    /// destruction (FUN_004323e0:33-35) or reset (FUN_0043206d:47-49 / FUN_0045e8fd:50).</summary>
    public void StopObjectSounds(short id)
    {
        PositionalSoundOwners.Remove(id);
        StopOwnerSounds?.Invoke(id);
        ObjectSoundsStopped?.Invoke(id);
    }
    /// <summary>Source-backed work.iff career table, separate from the larger optional runtime host.</summary>
    public ISimulationCareerHost? CareerHost { get; set; }
    /// <summary>Native implementation of FUN_0045dea5's lot and interaction data inputs; the remaining host services stay separate.</summary>
    public ISimulationAutonomyHost? AutonomyHost { get; set; }
    /// <summary>The object/tree/instruction currently dispatching a primitive, used to identify unimplemented runtime
    /// dependencies without changing the behavior of the primitive dispatcher.</summary>
    internal string? CurrentExecutionLocation { get; set; }
    /// <summary>FUN_00452237 requests FUN_004341a6(0) when an object's runtime slot reports a periodic update.</summary>
    public bool DisplayRefreshRequested { get; set; }
    /// <summary>The family budget the Alter Budget primitive charges (see <see cref="IFamilyBudget"/>).</summary>
    public IFamilyBudget? FamilyBudget { get; set; }
    /// <summary>The neighborhood's families, read and changed by Generic Sims Call 6 (see <see cref="INeighborhoodFamilies"/>).</summary>
    public INeighborhoodFamilies? NeighborhoodFamilies { get; set; }
    /// <summary>Pending native lot switch queued by GenericSimsCall 0x11 (FUN_0045A399).</summary>
    public sealed record LotSwitchRequest(short RawTargetHouse, int PersonGuid, short FamilyNumber, int CompanionGuid = 0);

    /// <summary>Pending native lot switch waiting to be applied by the host at a tick boundary.</summary>
    public LotSwitchRequest? PendingLotSwitch { get; set; }

    /// <summary>Production persistence callback used by FUN_00425316 after a neighborhood-transfer snapshot is written.</summary>
    public Action? PersistNeighborhoodStateImmediately { get; set; }

    /// <summary>Runs the host-owned FUN_00425316 persistence callback when present. The production native server
    /// installs the same immediate NBRS/FAMI projection directly on the world, so hostless GenericSimsCall paths
    /// can still persist the recovered neighborhood state subset.</summary>
    public void PersistNeighborhoodState()
    {
        if (Host is { } host) host.PersistNeighborhoodState();
        else PersistNeighborhoodStateImmediately?.Invoke();
    }
    /// <summary>The family and fame lookups the dynamic string parser makes (see <see cref="ISimulationMenuTextSources"/>).</summary>
    public ISimulationMenuTextSources? MenuTextSources { get; set; }
    /// <summary>The removal queue Sims.exe FUN_00442db0 appends to (the list at the manager's +0x58). Inferred: the lot
    /// removes the queued objects later; nothing in the port reads this list yet.</summary>
    public List<short> PendingRemovals { get; } = [];
    /// <summary>The linked request list at the global person manager's +0x1a4, used by GenericSimsCall 0x27/0x28.</summary>
    public GenericSimsCallRequestQueue GenericSimsCallRequests { get; } = new();
    public bool AutoProcessGenericSimsCallRequests { get; set; }
    /// <summary>DAT_006c4d5c (initial value 1 in Sims.exe's data). While it is set, Remove Object Instance ends the run
    /// when it removes the running object (FUN_00451baa returns 2). FUN_00474b22 clears it for a nested run; that
    /// clearing is not modelled in TreeStack, so the caller of a nested run must clear it.</summary>
    public bool SelfRemovalEndsRun { get; set; } = true;

    public short this[SimulationGlobal global] { get => Globals[(int)global]; set => Globals[(int)global] = value; }

    /// <summary>The shared one-based lot object-manager vector reached through every cXObject +0xdc pointer.</summary>
    public IReadOnlyList<XObject?> ObjectTableEntries => _objectTableEntries;
    private readonly Dictionary<(int X, int Y, int Level), short> _tileRoots = [];

    /// <summary>
    /// FUN_004325af reads the object-manager key stored in the lot's level-specific 64 × 64 tile map.
    /// FUN_004327d9/FUN_00432888 write an object's +0xf0 key at its tile when it is not attached through a
    /// footprint slot. Object restoration reaches FUN_004327d9 through FUN_00431c90; FUN_004231a8 walks the
    /// cXMTObject +0x18c chain for that path. The native model stores the manager key at registration, moves it through
    /// placement callbacks, and clears it through object-removal callbacks.
    /// </summary>
    public int TileValueAt(int tileX, int tileY, int level)
    {
        // FUN_004325af accepts 0 < level < 3; FUN_00432888 uses the same gate when writing roots.
        if (tileX is < 0 or >= 64 || tileY is < 0 or >= 64 || level is < 1 or > 2) return 0;
        return _tileRoots.GetValueOrDefault((tileX, tileY, level));
    }

    /// <summary>Reads the source-maintained per-level byte grid at manager +0x30. FUN_00433186 writes bit 3 for
    /// BuildModeType 6, while FUN_00432a70 reads bit 2 for RoomPlacement. It is separate from imported Arry 9.</summary>
    public byte BuildModeType6PlacementFlagsAt(int tileX, int tileY, int level)
    {
        if (tileX is < 0 or >= 64 || tileY is < 0 or >= 64 || level is < 1 or > 3) return 0;
        // FUN_00432a70 and FUN_00433186 both address this manager buffer as grid[x][y].
        return BuildModeType6PlacementGrids.TryGetValue(level, out var grid) ? grid[tileX * 64 + tileY] : (byte)0;
    }

    /// <summary>FUN_004790bc: only level 1 consults manager +0x64, where grid[x][y] != 0xff is eligible.</summary>
    public bool HasPlacementEligibilityAt(int tileX, int tileY, int level)
    {
        if (level != 1 || tileX is < 0 or >= 64 || tileY is < 0 or >= 64) return false;
        return PlacementEligibilityGrids.TryGetValue(1, out var grid) && grid[tileX * 64 + tileY] != 0xff;
    }

    /// <summary>Set one runtime manager +0x64 cell. Its source producer/load path is not yet mapped.</summary>
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

    /// <summary>Sims.exe FUN_00435a8f combines saved wall segments with each object rooted at the tile;
    /// FUN_00435a73 rotates its Wall Block Flags (+0xb4) by the object's direction.</summary>
    internal bool BlocksRouteDirectionAt(SimsEngine.World.RoomMap map, int tileX, int tileY, int direction)
    {
        // FUN_00435a8f returns zero before reading walls or roots unless both signed tile coordinates are
        // strictly inside the lot border (FUN_00433136).
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

    /// <summary>Sims.exe FUN_00432984 (00432984.c:1-32): clears the direct tile-root or parent-slot link and resets
    /// +0x66/+0x7e without touching item.Slots[0] (so a displaced object retains its existing overlap chain).</summary>
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

    /// <summary>Sims.exe FUN_00432a70 remove mode (00432a70.c:32-45): detaches item from its current tile root or
    /// parent slot via FUN_00432984, then splices item.Slots[0] back into that prior tile root or parent slot.</summary>
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
        // FUN_004333ef (004333ef.c:41-46) unplaces `item` via FUN_00432a70 BEFORE resolving destination occupancy,
        // even when moving within the same tile cell with no container.
        UnplaceObjectFromGridOrSlot(item, previous);
        if (wasFloorPlaced && isChair && (TileCell(previous) != TileCell(destination) || placementContainer is not null))
        {
            var oldOffset = LotGeometry.FootprintOffset(previousDirection);
            UpdateAdjacentChairFlag((previous.X >> 4) + (oldOffset.X >> 4),
                (previous.Y >> 4) + (oldOffset.Y >> 4), previous.Height);
        }

        // FUN_00432a70 (00432a70.c:315-334) resolves the destination occupant via FUN_004329f3, detaches it with
        // FUN_00432984 (preserving its Slots[0] subtree), attaches `item` via FUN_004327d9, and links the displaced
        // occupant into `item.Slots[0]`.
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
        // FUN_004333ef sends the same notice only after the accepted move has updated position and occupancy.
        if ((unchecked((ushort)item[ObjectData.FlagField2]) & 0x0400) != 0)
            DispatchNotice(0xf2, item.ClockManagerIndex);
    }

    /// <summary>FUN_004336c1: refreshes bit 0x0100 of ObjectData.Flags on all objects rooted at (tileX, tileY, level)
    /// according to whether any orthogonally adjacent chair (OBJf 0x1a) faces this tile, running Placement (OBJf 9)
    /// when bit 0x0100 flips on an object that defines OBJf 0x19.</summary>
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

    /// <summary>FUN_00433186 updates bit 3 of the level byte grid for objects whose type record BuildModeType is 6.</summary>
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
        // FUN_00433186 selects the per-x row pointer, then indexes that byte row by y.
        var index = x * size + y;
        grid[index] = present ? (byte)(grid[index] | 0x08) : (byte)(grid[index] & ~0x08);
    }

    /// <summary>Enumerates the object at a tile, then its contained slot objects in the preorder used by
    /// Sims.exe FUN_0043f371/FUN_0043f3b9. FUN_004325af obtains the tile's one-based manager key;
    /// FUN_004328d2 resolves each slot key, and the child +0x7e parent key/+0x66 slot index drive ascent.
    /// Tile-root selection reads the level-specific object grid and contained objects follow saved slot links.</summary>
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

    /// <summary>Source-equivalent first gate for Burn (FUN_00432644/FUN_00432664): the running object's
    /// effective tile must be strictly inside the lot border. FUN_00422665 returns false for that interior
    /// case; FUN_00432664 inverts it into its output byte, which FUN_00432644 returns as the gate result.</summary>
    internal static bool BurnSourceOnLot(XObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tileX = (sbyte)(source.Position.X >> 4);
        var tileY = (sbyte)(source.Position.Y >> 4);
        return tileX > 0 && tileY > 0 && tileX < LotGeometry.LotDimension - 1 && tileY < LotGeometry.LotDimension - 1;
    }

    /// <summary>Native projection of Sims.exe FUN_0044d9af / Mac cXObject::TryBurn. The source first scans
    /// the addressed tile for FlagField2 (+0x9a) bit 0x20 (Burns), then repeatedly closes its burnable set over
    /// linked multi-tile parts, objects sharing a tile with FlagField2 bit 0x20, and objects whose footprint
    /// intersects one of the eight neighboring tiles. The final scan rejects Flags (+0x5a) bit 0x800, and in
    /// strict mode rejects Flags bit 0x20 (Burned) or positive Data[62] (UseCount). Runtime class 2 (XPerson)
    /// objects in the closure run their +0x38/+0x58/+0x74 reset and tile-snap sequence before the Fire pass.
    /// Fire creation uses the source FUN_00440b6d / placement callback pair (+0x54 check, +0x58 action).</summary>
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

        // FUN_0044d9af's two DAT_006c2924/2948 tables walk the eight surrounding cells. The closure is
        // order-independent after duplicate suppression; preserve the source's object iterator order within
        // each tile and append newly discovered objects to the same growing work list.
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
            // FUN_0044d9af expands class-2 bounds by two fixed-point units on each side before
            // its strict rectangle-overlap comparisons.
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


    /// <summary>Source-backed state changes from Sims.exe vtable +0x50 callback FUN_004331d4. Both arguments 0 and 1
    /// relocate the object to (-16,-16,1), recompute its room, reset +0x14/+0x4e, and clear bit 0 of byte +0x5b.
    /// Nonzero arguments additionally dispatch the manager interaction refresh in FUN_00447d6b.</summary>
    public void BeginObjectRemoval(XObject item, int argument)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (argument is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(argument), "The source +0x50 callback paths use arguments 0 or 1.");

        // cXPortal's +0x50 callback runs FUN_00463648 before the base cXObject removal state changes.
        if (item.RuntimeClass == SimsEngine.Formats.ObjectClass.Portal)
        {
            foreach (var member in MultiTileMembers(item)) RefreshPortalWallStyleOverlay(member, resetToDefault: true);
            // FUN_00463648 ends with FUN_004639d2, which clears every allocated route-cost vector in the manager.
            foreach (var routeOwner in WalkOrder) routeOwner.ResetAllocatedPortalRouteCosts();
        }

        // FUN_004331d4 (cXObject::Pickup, 004331d4.c:13-15) invokes OBJf[10] (Pickup) when MasterId == 0 before moving off-lot.
        if (item.Definition is { Length: > (int)ObjdField.MasterId } &&
            item.Definition[(int)ObjdField.MasterId] == 0)
        {
            RunFunctionToCompletion(item, 10);
        }

        // FUN_004331d4 sends notice 0xf2 with the runtime +0xf0 manager index before changing the object's position.
        if ((unchecked((ushort)item[ObjectData.FlagField2]) & 0x0400) != 0)
            DispatchNotice(0xf2, item.ClockManagerIndex);
        SetBuildModeType6PlacementFlag(item, item.Position, false);
        // FUN_004331d4 (004331d4.c:47, 62-64) resolves the prior container via FUN_00432664 before FUN_00432a70
        // detaches the slot link, then runs OBJf[9] (Placement) on that prior container after moving off-lot.
        var oldPosition = item.Position;
        var oldDirection = item[ObjectData.Direction];
        var oldContainer = ResolveParentContainer(item);
        // FUN_004331d4 calls FUN_00432a70 with the remove flag, which runs FUN_00432984 on the held object: the
        // holder's hierarchy slot entry is cleared, so a Sim that finishes eating no longer carries the meal.
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
        item.Data[2] = 0; // cXObject +0x4e
        item.Data[8] = (short)(item.Data[8] & 0xfeff); // clear bit 0 of the byte at cXObject +0x5b
        if (oldContainer is not null)
            RunFunctionToCompletion(oldContainer, 9);
        DisplayRefreshRequested = true; // FUN_004341a6(0)
        if (argument != 0) RefreshInteractionAfterObjectRemoval(item);
    }

    /// <summary>FUN_00447d6b/FUN_0044822b after a nonzero cXObject +0x50 callback. The selected related object
    /// receives +0x50(0), whose ordinary removal state changes do not recursively dispatch this refresh.</summary>
    private void RefreshInteractionAfterObjectRemoval(XObject removed)
    {
        if (!InteractionRefreshManagerEnabled) return;
        if (ReferenceEquals(ActiveInteractionRefreshTarget, removed)) InteractionContextsInvalidated = true;

        XObject? target = removed;
        if (removed.TypeRecord?.ObjectType == XPerson.PersonType)
        {
            // FUN_0044822b selects vector record +0x48: the second usable definition slot after the leading sentinel.
            var occupantKey = removed.FootprintSlots.Count >= 2 ? removed.FootprintSlots[1].OccupantObjectId : (short)0;
            target = occupantKey > 0 ? ObjectTableEntry(occupantKey) ?? ObjectById(occupantKey) : null;
        }
        if (target is null) return;

        if (InteractionRefreshTypeKey == unchecked((int)0xe9337020u))
        {
            var index = InteractionRefreshPendingObjects.FindIndex(candidate => ReferenceEquals(candidate, target));
            if (index >= 0)
            {
                // FUN_0044845a appends the matched pointer to +0xdc; FUN_004486a3 erases its +0xd0 entry.
                InteractionRefreshDeferredObjects.Add(InteractionRefreshPendingObjects[index]);
                InteractionRefreshPendingObjects.RemoveAt(index);
            }
        }
        else if (InteractionRefreshTypeKey is not (0x0baa71c5 or 0x2f726fff))
            return;

        BeginObjectRemoval(target, 0);
    }

    /// <summary>FUN_00422909 unlinks the selected cXMTObject from its +0x18c chain before Find Location For
    /// invokes the removal callback. Removing a lead promotes its first part to lead and retains the remaining chain.</summary>
    internal void DetachMultiTilePart(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var firstPart = item.MultiTileNextId == 0 ? null : ObjectById(item.MultiTileNextId);
        if (item.MultiTileMasterId == 0)
        {
            // FUN_00422909 re-roots the chain at the first part, then the common tail clears this former lead.
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
            // The master field points back to the lead; walk that lead's next chain and splice out this part.
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

    /// <summary>Applies the recoverable instance-state writes from Sims.exe FUN_0043206d's ordinary bit-clear
    /// branch: vtable +0x50 removes the object, +0x40 clears its data/attribute/temp storage, and +0x58 restores
    /// its prior root or parent-slot placement when that location is still accepted. The +0x40 initializer restores
    /// ObjectId and ObjectVersion, normalizes and copies back the source-selected data fields, and queues function-table
    /// entry 1 on the object's resumable stack, then runs the initializer tail. Full +0x58 virtual validation remains incomplete.</summary>
    private void ResetObjectAfterScriptError(XObject item, TreeStack? activeStack)
    {
        if (item is XPerson resettingPerson)
        {
            InteractionLifecycle.FinalizeActor(resettingPerson);
            ResetPersonAnimatorForChangedCode(resettingPerson);
        }
        else InteractionLifecycle.FinalizeRink(item);
        var previousPosition = item.Position;
        var preservedData = PreservedInitializerData(item);
        // FUN_00432664 climbs from a hierarchy slot 0 link (+0x66 == 0) through each +0x7e parent until it reaches an
        // object whose hierarchy slot is not 0, and takes that object's parent and slot as the restore target.
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

        // The removal virtual (+0x50) drops the link from the object's own parent; the climbed anchor above only
        // chooses where the +0x58 re-place goes.
        var directParent = ObjectTableEntry(item[ObjectData.ParentId]);
        var directSlot = item[ObjectData.SlotHierNumber];

        BeginObjectRemoval(item, 1);

        if (directParent is not null && directSlot >= 0 && directSlot < directParent.Slots.Count)
        {
            if (directParent.Slots[directSlot] == item.ClockManagerIndex) directParent.Slots[directSlot] = 0;
            if (directSlot > 0 && directSlot - 1 < directParent.FootprintSlots.Count &&
                directParent.FootprintSlots[directSlot - 1].OccupantObjectId == item.ClockManagerIndex)
                directParent.FootprintSlots[directSlot - 1].OccupantObjectId = 0;
        }

        var restoreParentSlot = parent is not null && parentSlot >= 0 && parentSlot < parent.Slots.Count;
        InitializeObjectData(item, preservedData);

        if (restoreParentSlot && parent is not null)
        {
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
        // FUN_004327d9 restores the container slot inside FUN_00431c90, before the initializer tail.
        // FUN_00431c90 (00431c90.c:70) gates Function 0 (init) and the immediate tick on OBJD MasterId == 0.
        FinishObjectInitializer(item, runTick: true, finishMultiTileMember: false);

        // FUN_0043206d calls the +0x58 virtual only when FUN_00432664 reports a valid prior position.
        // FUN_004333ef performs its own placement checks but still runs the placement state transition;
        // do not gate this reset callback on the separate incomplete native CanPlaceObject contract.
        if (restoreParentSlot || validPriorTile)
            PlaceObject(item, previousPosition, activeStack,
                restoreParentSlot ? parent : null,
                restoreParentSlot ? checked((short)parentSlot) : (short)0);
        StopObjectSounds(item.Id);
    }

    /// <summary>FUN_00431c90 snapshots the complete data block, clears it, then writes these fields back from the
    /// snapshot.</summary>
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

    /// <summary>The first part of the cXObject +0x40 initializer, Sims.exe FUN_00431c90: the data clear and the
    /// function 1 push. <see cref="FinishObjectInitializer"/> holds the rest. Both callers (the FUN_00440b6d creation
    /// path and the FUN_0043206d error reset) pass 1, the tick argument. After the container-slot restore, the source sets the manager wait word to -1 (FUN_00431e78) and, for a
    /// single-tile or lead object (OBJD MasterId 0), runs function 0 to its return (FUN_00422d79/FUN_00474b22), runs
    /// one object tick (vtable +0x14, FUN_00452237), then writes manager bit 1 from SimulateOnPause and the autonomy
    /// bit 3 (FUN_0044582f with masks 2 and 8).</summary>
    private void InitializeObjectData(XObject item, Dictionary<ObjectData, short> preservedData)
    {
        // FUN_00431c90 clears the complete +0x4a data block, the per-instance attribute vector, and eight temps.
        // TypeAttributes and dynamic sprite flags are separate arrays and are not cleared by that routine.
        Array.Clear(item.Data);
        // FUN_00431c90 writes the object-manager index back to cXObject+0x60 after clearing +0x4a..+0xd9.
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

        // FUN_00431c90 queues function-table entry 1 via FUN_00474979/FUN_004749b7, with four zero parameter words.
        RunObjectResetInitializer(item);
    }

    /// <summary>The tail of FUN_00431c90 after its container-slot restore; see <see cref="InitializeObjectData"/>.</summary>
    private void FinishObjectInitializer(XObject item, bool runTick, bool finishMultiTileMember = false,
        XObject? autonomyRoot = null)
    {
        if (item.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
        if (item.Definition is not { Length: > (int)ObjdField.MasterId } definition ||
            (definition[(int)ObjdField.MasterId] != 0 && !finishMultiTileMember)) return;
        var callerLocation = CurrentExecutionLocation;
        try
        {
            if (FunctionTreeId(item, 0) is var initTree && initTree != 0 && item.TypeRecord?.BehaviorScope is { } scope)
                TreeRunner.RunWithParameters(this, item, scope, initTree, 0, []);
            if (runTick && SavedExecutionStacks.ContainsKey(item.Id))
                TreeRunner.RunRestoredObjectStack(this, item);
        }
        finally
        {
            CurrentExecutionLocation = callerLocation;
        }
        if (item.ClockManagerIndex <= 0 || !ReferenceEquals(item, ObjectById(item.Id))) return;
        ClockManager.SetFlag(item.ClockManagerIndex, 1, item[ObjectData.SimulateOnPause] != 0);
        var root = autonomyRoot ?? InteractionMenus.Target(this, item);
        InitializeAutonomyCandidate(item, root, root.TypeRecord?.Interactions);
    }

    /// <summary>Sims.exe FUN_00440b6d's creation of a single-tile object after <see cref="Add{T}"/> has assigned its
    /// manager index (FUN_0044350d). Applies FUN_00431b5e's held cursor state (position -16,-16, no slot, slot count,
    /// zeroed attributes), the FUN_00431ea2 birth stamp from the lot date and clock, the +0x40 initializer, the OBJD
    /// price as CurrentValue, flag 0x80 and the manager tick bit.</summary>
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

    /// <summary>FUN_004258ed binds a cXPerson (during construction in FUN_00459797 or PostLoad in FUN_00459d62)
    /// to the neighborhood person record selected by its type record (+0x11c). It assigns PersonData[NeighborId] (+0x5ca),
    /// copies all version-gated persistent fields permitted by FUN_0042385a (+0x58c), and copies Neighbor +0xee
    /// into PersonData[FamilyNumber] (+0x606).</summary>
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
            return neighbor;
        }
        if (neighbor is not null)
        {
            person[PersonData.NeighborId] = neighbor.Id;
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
                    (0x45, 5),
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
            // Neighborhood::LoadPersistentData copies Neighbor +0xee into person data 61 after restoring
            // the persistent fields; +0xee is derived from FAMI membership by UpdateFamilyNumbers.
            // Preserve the lot-saved family number on unserialized catalog neighbors (such as the Tutorial lot).
            if (version > 0 || neighbor.PersonData[(int)PersonData.FamilyNumber] != 0)
                person[PersonData.FamilyNumber] = neighbor.PersonData[(int)PersonData.FamilyNumber];
        }
        return neighbor;
    }

    /// <summary>FUN_004258ed and FUN_0042597a bind a created cXPerson to the neighborhood person record selected by its type record.
    /// It assigns PersonData[NeighborId], copies all version-gated persistent fields permitted by FUN_0042385a/FUN_00423cc8,
    /// and copies the neighbor's relationship table into the created person.</summary>
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

        // cXPerson initialization randomizes all interest fields only when all eight base interests
        // are at or below the uninitialized sentinel (FUN_10108ca4). Existing saved interests pass
        // through unchanged; the same method also initializes the five Hot Date interests.
        if (Enumerable.Range(0x2e, 8).All(field => person.PersonData[field] <= 10))
            RandomizeCreatedPersonInterests(person);
    }

    /// <summary>FUN_0044e108's Create Object arrival path: when bit 2 is set for a non-household person,
    /// Sims.exe looks up the named "init visitor" BHAV in the person's private/semi-global/global scope and
    /// runs it synchronously with the person's clock-manager slot as stack object and start instruction 0.</summary>
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
        // Neighborhood::AddNewNeighbor uses the first empty ordered slot, or appends a slot, and
        // assigns the one-based slot number as the neighbor id when called with id -1.
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
            // A newly created person neighbor is backed by a character object (ObjectScan.People),
            // whose OBJD category flags are hydrated as 4 by the native saved-lot loader.
            FriendEligibilityObjectFlagsB4 = 4u,
        };
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

    /// <summary>Creates a cXMT group through the source's post-link initialization phases. FUN_00440b6d first creates
    /// and links every member; cXMT +0x40 then initializes member data, +0x88 derives rotated ObjM offsets, and its
    /// second loop runs function 0 and the object tick for each member before placement validation.</summary>
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

        // FUN_004231a8 first calls FUN_00431c90 on each member. Its MasterId==0 branch is retained by the generic
        // finisher (and applies only to definitions authored with MasterId 0); the rink's lead and children all have
        // MasterId 1, so function 0 is intentionally deferred until after +0x88 below.
        foreach (var member in group) FinishObjectInitializer(member, runTick: true);
        RefreshMultiTileOffsetsAfterRotation(root);

        // FUN_004231a8's second pass runs function 0, then the reset-stack tick and manager/autonomy state per member.
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

    /// <summary>Sims.exe FUN_0043538d (the Hand tool's "can pick this up" test, reached through vtable +0x70 after
    /// FUN_004354e5 also requires MovementFlags bit 8 for a sale). An object must be movable (MovementFlags bit 2), unused
    /// (UseCount 0), inside the buildable lot boundary (Arry 8 bit 0x20 clear) and without Flags bit 0x20; every object
    /// in its slots must be removable as well. MoveObjects (DAT_006e4a84) bypasses all checks.</summary>
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

    /// <summary>The Hand tool's move: the object passes <see cref="CanPickUpObject"/>, is taken to the cursor state with
    /// the +0x50(1) removal callback, and is placed at <paramref name="destination"/> with the new direction when
    /// FUN_00432a70's placement validation accepts it. A refused placement puts it back exactly where it was.
    /// Multi-tile groups need the group placement cursor and are not handled here.</summary>
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

    /// <summary>Sims.exe FUN_004354e5: an object is sellable when it can be picked up, its own MovementFlags bit 8 is set,
    /// and none of its container slots (slot >= 1) is occupied. MoveObjects (DAT_006e4a84) bypasses all checks.</summary>
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

    /// <summary>Sims.exe FUN_00432249: the object's CurrentValue, halved (C division) when it or any part of its
    /// multi-tile group is broken (RepairState nonzero). For a group, read the lead object's value even when the
    /// lead OBJD itself has a nonzero MasterId.</summary>
    public short ObjectSaleValue(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var members = MultiTileMembers(item);
        var master = members.FirstOrDefault() ?? item;
        var broken = members.Any(member => member[ObjectData.RepairState] != 0) || item[ObjectData.RepairState] != 0;
        var value = master[ObjectData.CurrentValue];
        return broken ? (short)(value / 2) : value;
    }

    /// <summary>The Hand tool's sale (FUN_00487f4b, FUN_004357f6): runs each part's function 12 and its slot
    /// occupants', takes the group out with the +0x50(1) removal callback and deletes it. Returns the buy-mode
    /// refund, the unscaled <see cref="ObjectSaleValue"/> (the 0.8 factor belongs to the build tools).</summary>
    public int SellObject(XObject item)
    {
        if (!CanSellObject(item)) throw new InvalidOperationException("Object cannot be sold");
        var refund = ObjectSaleValue(item);
        DemolishObject(item, runCleanup: true);
        return refund;
    }

    /// <summary>Sims.exe FUN_00487c19 / FUN_00487f4b / cWinDisposePanel::Draw (0x004820c2): plants/trees/shrubs
    /// (BuildModeType 4) refund 0; doors, windows, stairs, fireplaces, and columns (1, 2, 3, 5, 6) refund
    /// <c>(int)(baseSaleValue * 0.8f + 0.5f)</c>; buy objects (0) and pool ladders/diving boards (7) refund full value.</summary>
    public static int HandResaleValue(int baseSaleValue, int buildModeType) => buildModeType switch
    {
        4 => 0,
        1 or 2 or 3 or 5 or 6 => (int)(baseSaleValue * 0.8f + 0.5f),
        _ => baseSaleValue,
    };

    /// <summary>Sims.exe FUN_00433136 / FUN_00525256: returns whether <paramref name="target"/> sits strictly inside the
    /// lot's outer edge ring (<c>1 &lt;= tile &lt; LotDimension - 1</c>).</summary>
    public static bool IsInsideLotEdge(XObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return TileInsideLotEdge(target);
    }

    /// <summary>The removal half of <see cref="SellObject"/>: the +0x50(1) callback and deletion of the group and its slot
    /// occupants, preceded by function 12 when <paramref name="runCleanup"/> is set (the Hand tool does, the wall tool's
    /// sale of a door or window on an erased wall, FUN_00497140, calls only +0x50(1)).</summary>
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
        var callerLocation = CurrentExecutionLocation;
        if (runCleanup)
        foreach (var member in doomed)
            if (FunctionTreeId(member, 12) is var tree && tree != 0 && member.TypeRecord?.BehaviorScope is { } scope)
                TreeRunner.RunWithParameters(this, member, scope, tree, 0, []);
        CurrentExecutionLocation = callerLocation;
        foreach (var member in doomed) BeginObjectRemoval(member, 1);
        foreach (var member in doomed) KillObject(member.Id);
        WakeSimulateOnPauseObjects();
    }

    /// <summary>Ports Sims.exe FUN_00432396's source-order year/month/day subtraction for ObjectData.SimAge and
    /// ObjectData.AgeInMonths. The date input comes from the lot date manager and remains unavailable in the current loader.</summary>
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

    /// <summary>Ports Sims.exe FUN_004322f2's daily depreciation invoked by FUN_00442668 on midnight rollover.</summary>
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

    /// <summary>Type GUIDs whose saved OBJT TreeValue0/1 differ from the current function 0/1 fingerprints.
    /// Sims.exe FUN_0043a5fb sets TypeRecord +0xb4 bit 2 for these types while it loads the OBJT table.</summary>
    public HashSet<uint> ChangedCodeTypeGuids { get; } = [];
    internal Dictionary<short, ObjectTypeEntry> CurrentObjectTypes { get; } = [];

    /// <summary>Sims.exe FUN_00431edd's post-load pass over every restored object. An object resets when its type's
    /// code changed since the save (TypeRecord +0xb4 bit 2 with DAT_006c2972, which defaults to 1) or when its type is
    /// GUID 0x3C08965F (FUN_0043e07d). The reset broadcasts the subject to the live person-manager vector through
    /// FUN_00444346, runs the subject's vtable +0x38(0) behavior, and for changed code also runs function 0 to
    /// completion (FUN_00422d79(0,0,0)). The +0x38 mapping to the native per-object reset below remains source-backed
    /// by the Windows routine and inferred from the Mac cXObject::Cleanup argument shape.</summary>
    public void ResetObjectsWithChangedCode()
    {
        foreach (var item in WalkOrder.ToArray())
        {
            if (!ReferenceEquals(item, ObjectById(item.Id))) continue;
            var guid = unchecked((uint)(item.TypeRecord?.Key ?? 0));
            var changed = ChangedCodeTypeGuids.Contains(guid);
            if (!changed && guid != 0x3C08965F) continue;
            NotifyPersonObjectReferenceRecipients(item);
            SavedExecutionStacks.Remove(item.Id);
            if (item is XPerson person) ResetPersonForChangedCode(person);
            else ResetObjectForChangedCode(item);
            // FUN_0043206d and FUN_0045e8fd both end with FUN_00431e78(id, -1).
            if (item.ClockManagerIndex > 0) ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
            if (changed) RunFunctionToCompletion(item, 0);
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

    /// <summary>FUN_00444346 walks the live person-manager vector at +0x84/+0x88 in order and invokes each
    /// recipient's vtable +0x38 with the changed subject, excluding the subject itself. Walk WalkOrder live so
    /// recipients appended during a callback are visited, and re-anchor the loop index if ResetPersonForChangedCode
    /// removes earlier helper entries from WalkOrder.</summary>
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
                // FUN_0045e8fd(subject): queued +8/+0xc records, action references and person-owned
                // runtime references are invalidated before the next recipient is visited.
                if (person.SavedPersonObjectReferences.Any(reference => ReferenceEquals(reference.Target, subject)) ||
                    ReferenceEquals(person.GenericSimsCall02ObjectA54, subject))
                {
                    // FUN_0045e8fd takes its full reset branch when the subject matches the ordered +0xb00
                    // interaction references or the current +0xa54 object pointer.
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

    /// <summary>
    /// Mac HierSever__8cXObjectFv (0x100c0050): severs hierarchy links between the object and its parent slot,
    /// and between the object and any child objects attached to its slots.
    /// </summary>
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

    /// <summary>The cXObject vtable +0x38 null branch, Sims.exe FUN_0043206d (0043206d.c:18-53). Without a cleanup
    /// function (function 3) it takes the same remove/initialize/re-place branch as a script-error reset. Otherwise it
    /// runs cleanup to completion (FUN_00422d79(3,0,0)), clears Flags bit 0x20 (byte +0x5a bit 5) and UseCount (+0xc6),
    /// clears the stack and pushes function 1 (FUN_00474979). That branch never severs the slot hierarchy and never
    /// clears attributes or graphics flags, so saved state such as a gravestone's hours-since-death survives the reset.
    /// Both branches then stop the object's sounds (FUN_00431e78/FUN_0057a9a6).</summary>
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
            // FUN_0043206d (0043206d.c:29-35): when KillObject has already set Flags bit 6 (0x40), run only
            // BeginObjectRemoval(item, 1) and stop sounds — never re-initialize or re-place a dying object.
            if ((item.Flags & 0x40u) != 0)
            {
                BeginObjectRemoval(item, 1);
                item.SavedStack.Clear();
                item.SavedStackSize = 0;
                StopObjectSounds(item.Id);
                return;
            }
            // No saved thread is active during the post-load pass; the original runs the re-place callbacks to
            // completion through FUN_00422d79's own stack, which the transient runner stands in for.
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

    /// <summary>The part of cXPerson's vtable +0x38 null branch, Sims.exe FUN_0045e8fd, that the native person model
    /// holds: FUN_00457da4 clears the stack, FUN_00474979 pushes function 1, FUN_00422d79(3,0,0) runs cleanup,
    /// FUN_0045e839 replaces the current action with an empty record, and FUN_0045ee5d pops the whole queue ring.
    /// The released-reference callbacks, separate +0xaf0/+0x148 vector teardown and ordinary off-lot recovery are
    /// ported. The holder +0x50/+0x54 validation/search sequence and animator +0x0c/+0x14/+0x20 field effects are
    /// mapped; detailed visual outfit/hand refresh and runtime parity remain open.</summary>
    private void ResetPersonForChangedCode(XPerson person, bool notificationGuardHeld = false, XObject? subject = null)
    {
        if (!notificationGuardHeld && !person.TryBeginObjectReferenceNotification()) return;
        try
        {
            SavedExecutionStacks.Remove(person.Id);
            InteractionLifecycle.FinalizeActor(person);
            // FUN_0045e8fd walks the ordered cXPerson +0xb00 reference vector before it clears the
            // saved stack. PersonInteractionStack.Release mirrors the source pop path: decrement the
            // referenced object's +0xc6 UseCount and remove its ordered interaction reference.
            // Release from each saved depth so stale nested references are drained in LIFO order.
            var flagSource = subject ?? person;
            while (person.SavedPersonObjectReferences.Count > 0)
            {
                var lastReference = person.SavedPersonObjectReferences[^1];
                PersonInteractionStack.Release(person, lastReference.Value - 1);
                if ((flagSource.Flags & 0x00001000u) != 0 || lastReference.Target is not { } target) continue;

                // FUN_0045e8fd broadcasts each released +0xb00 target, then calls that target's
                // vtable +0x38 with null. The person's (or subject's, when non-null) bit-12 gate suppresses both callbacks.
                NotifyPersonObjectReferenceRecipients(target);
                if (target is XPerson targetPerson) ResetPersonForChangedCode(targetPerson);
                else ResetObjectForChangedCode(target);
            }
            // FUN_0059294c(0) writes cXPerson +0x5c8, PersonData[30] (Censorship Flags).
            person[global::SimsEngine.Simulation.PersonData.CensorshipFlags] = 0;
            ResetPersonAnimatorForChangedCode(person);
            StopObjectSounds(person.Id);
            // FUN_0045fc43 removes the +0xaf0 vector of 0x9c-byte subject-reference records.
            person.RuntimeObjectReferences.Clear();
            foreach (var reference in person.RuntimeStateRecords)
            {
                // FUN_0045e8fd calls FUN_0046e15e(0) for every 0x3c-byte record: clear +0x10 and +0x28.
                reference.Target = null;
                reference.RawWord28 = 0;
            }
            ResetPersonPlacementAfterChangedCode(person);
            person.SavedStack.Clear();
            person.SavedStackSize = 0;
            RunObjectResetInitializer(person);
            RunFunctionToCompletion(person, 3);
            // FUN_0045e8fd calls FUN_0045e839 with an empty record, preserving its backup/alias writes.
            // A non-null subject then reaches the ordinary reference cleanup below the full-reset branch.
            ApplyStartedInteractionAction(person, ActionQueueSlot.EmptyRecord());
            if (!notificationGuardHeld) person.ActionQueueCurrent = person.ActionQueueEnd = 0;
        }
        finally
        {
            if (!notificationGuardHeld) person.EndObjectReferenceNotification();
        }
    }

    /// <summary>Ports the changed-code reset placement sequence from FUN_00432664/FUN_0045e8fd, including the
    /// holder-context validation/search probe and ordinary off-lot recovery. FUN_00432644 admits positions whose
    /// X/Y tiles are strictly inside the one-tile lot border. If that test fails and +0x114 bit 6 is clear, Sims.exe
    /// searches first at the lot center, then through FUN_00434895, and finally dispatches person +0x88/+0x74
    /// (placement and room-state refresh).</summary>
    private void ResetPersonPlacementAfterChangedCode(XPerson person)
    {
        // FUN_0043270d reconstructs this record before any removal. A non-null holder takes the first
        // +0x50(1)/+0x54 validation path, independently of the later off-lot check.
        var (holder, _) = PlacementSpec(person);
        var stack = TreeRunner.CreateTransientStack(this, person);
        if (holder is not null)
        {
            // FUN_004331d4 clears the old parent slot and moves the Sim to (-16,-16,1). The holder
            // branch reads that post-removal sentinel position for +0x54 and its FindLocation center.
            // If blocked, FUN_00434895 probes nearby candidates; the source then forwards the resulting
            // in/out coordinates to its +0x88 placement callback before the independent off-lot check.
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

            // FUN_0045e8fd invokes person +0x88 with the holder probe's in/out position after +0x54,
            // whether validation passed or both searches failed, then invokes +0x74. The independent off-lot
            // check reads the resulting position.
            PlaceObject(person, holderCandidate, stack);
            RefreshPersonPlacementCallbacksAfterMovement(person);
        }

        var position = person.Position;
        var size = LotGeometry.LotDimension;
        var tileX = position.X >> 4;
        var tileY = position.Y >> 4;
        var positionInsideLot = tileX > 0 && tileY > 0 && tileX < size - 1 && tileY < size - 1;
        if ((person.Flags & 0x00000040u) != 0 || positionInsideLot) return;

        // Read height after +0x50: FUN_004331d4 resets it to level 1 before this center candidate is built.
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
        // GenericSimsCall 0x20 has the same +0x88/+0x74 call pair. The +0x74 target is 0x0045e685,
        // whose pool-tile state write and animator +0x24 grid-root invalidation are mapped below.
        RefreshPersonPlacementCallbacksAfterMovement(person);
    }

    /// <summary>
    /// Clears the animator-owned route and animation channels during cXPerson's changed-code reset. Windows
    /// FUN_0045e8fd calls animator +0x0c (FUN_00589254) and +0x14 (FUN_00588907, ResetSuits), then removes the
    /// person's route records. The state/practice mapping follows Mac SAnimator::Reset (0x10357A40–0x10357C2C);
    /// FUN_00589254 also calls animator +0x20(1), whose target invalidates +0x80/+0x84/+0x88; normalize those
    /// sentinels to absent roots and clear the +0x8c/+0x90/+0x94 displacements. ResetSuits re-reads Body Strings,
    /// so refresh its source-backed +0x60e body mode from the person's loaded selector cache. The native renderer's
    /// transient presentation override is dropped so it falls back to the saved character baseline; detailed
    /// Body Strings outfit/hand selection and dressing-record redress remain unmodeled.
    /// </summary>
    private void ResetPersonAnimatorForChangedCode(XPerson person)
    {
        StopObjectSounds(person.Id);
        person.NativeRoute = null;
        person.NativeRouteParents.Clear();
        InvalidateAnimatorRootGrid(person);
        person.SetAnimatorDisplacements(0f, 0f, 0f);
        person.AnimationPlayback = null;
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
        // SAnimator::ResetSuits (00588907.c:16-17) zeroes HandGestureLeft (+0x5b8) and HandGestureRight (+0x5ba).
        person[global::SimsEngine.Simulation.PersonData.HandGestureLeft] = 0;
        person[global::SimsEngine.Simulation.PersonData.HandGestureRight] = 0;
        person.NativeOutfitTransitionActive = false;
        if (person.TypeRecord?.AnimatorBodyStringMode is { } bodyStringMode)
            person.AnimatorBodyStringMode = bodyStringMode;
    }

    /// <summary>Sims.exe FUN_00422d79(n, stackObject, 0): runs function-table entry n to its return through FUN_00474b22.</summary>
    public void RunFunctionToCompletion(XObject item, short function, short stackObject = 0)
    {
        if (FunctionTreeId(item, function) is not (var tree and not 0) || item.TypeRecord?.BehaviorScope is not { } scope)
            return;
        var callerLocation = CurrentExecutionLocation;
        TreeRunner.RunWithParameters(this, item, scope, tree, stackObject, []);
        CurrentExecutionLocation = callerLocation;
    }

    private void RunObjectResetInitializer(XObject item)
    {
        var type = item.TypeRecord;
        if (type?.FunctionTable is not { Count: > 1 } functions || type.BehaviorScope is not { } scope) return;
        var tree = functions[1];
        if (tree == 0) return;

        // The original stack push has four zero parameter words. Resolve the same saved code-owner slot that will
        // be written on the new frame so a later ObjM save/reload keeps this tree in its type's behavior scope.
        var codeOwner = CodeOwnerTypeGuids.FirstOrDefault(entry => entry.Value == unchecked((uint)type.Key));
        if (!CodeOwnerTypeGuids.ContainsKey(codeOwner.Key) || codeOwner.Value != unchecked((uint)type.Key)) return;
        var stack = TreeRunner.StartObjectTree(this, item, scope, unchecked((short)tree), item.Id, codeOwner.Key,
            [0, 0, 0, 0]);
        if (stack is null) return; // FUN_004749b7 answers false when it cannot resolve or enter the tree.
        SavedExecutionStacks[item.Id] = stack;
        TreeRunner.SaveFrames(item, stack);
    }

    /// <summary>Queues Sims.exe FUN_004341a6(0) for the runtime output. The host hook preserves the existing
    /// renderer integration while the pending flag lets the native runtime consume the refresh.</summary>
    public void RequestDisplayRefresh()
    {
        DisplayRefreshRequested = true;
        Host?.RefreshDisplay();
    }

    /// <summary>FUN_00407498: resolve a one-based manager slot; nonpositive, out-of-range and null entries miss.</summary>
    public XObject? ObjectTableEntry(short index) =>
        index > 0 && index <= _objectTableEntries.Count ? _objectTableEntries[index - 1] : null;

    /// <summary>FUN_00432664's holder and slot (cXObject::GetPlacementSpec): follows the +0x7e parent while the slot hierarchy
    /// number (+0x66) is zero, then answers the parent as the holder and that number less one (-1 stays -1). A zero number with
    /// no parent is written back as -1 (0xffff).</summary>
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

    /// <summary>FUN_004340f2 / cXObject::GetSlotHeight: the height of slot <paramref name="slot"/> of <paramref name="holder"/>
    /// plus the heights of the slots that carry it, up the holder chain. A slot outside the holder's table answers zero.</summary>
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

    /// <summary>Global.iff STR# 154 (adult) and 155 (child) reach clips (InitSkillLookup tables 0x9a and 0x9b), keyed by
    /// the child flag and the reach animation index.</summary>
    public Dictionary<(bool Child, int Index), AvatarAnimationClip> ReachAvatarAnimationTable { get; } = [];

    /// <summary>FUN_00432664/FUN_0043270d's removal chain. FUN_004327c8 resolves the current object's +0x7e
    /// parent key through its manager table; FUN_00432664 follows that key while +0x66 (SlotHierNumber) is zero.</summary>
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

    /// <summary>FUN_004E9918: map one selected-family member position to the first matching person-record index,
    /// preserving the FAMI and person-record table orders. The source returns -1 when no active family is selected.</summary>
    public int ActivePersonIndex(int position)
    {
        if (!ActivePersonManagerPresent || position < 0 || position >= ActivePersonRecordKeys.Count) return -1;
        var key = ActivePersonRecordKeys[position];
        return PersonRecordKeys.IndexOf(key);
    }

    /// <summary>Resolves an object's action tree from the cached source function table. Sims.exe
    /// FUN_00457da4 creates the type record and FUN_004563fd resolves its OBJf/default table; FUN_004561a0
    /// returns the low word at the requested function index, or zero for a negative/out-of-range index.
    /// SavedLotObjectLoader populates <see cref="TypeRecord.FunctionTable"/> from OBJf or the 30 legacy OBJD slots.</summary>
    public static short FunctionTreeId(XObject item, short function)
    {
        ArgumentNullException.ThrowIfNull(item);
        var functions = item.TypeRecord?.FunctionTable;
        if (function < 0 || functions is null || function >= functions.Count) return 0;
        return unchecked((short)functions[function]);
    }

    /// <summary>FUN_004561c2 resolves a four-byte function entry: action tree in the low short and check tree in the high
    /// short. Sims.exe FUN_004561a0 returns that action id and treats a missing index as zero.</summary>
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

    /// <summary>FUN_004561a0's callers treat a zero action-tree id as an absent function.</summary>
    public static bool HasFunction(XObject item, short function) => FunctionTreeId(item, function) != 0;

    /// <summary>FUN_004102E1's active-person count and the FUN_004E9918/FUN_004E9744 lookup used by
    /// GenericSimsCall 0x20. It searches the existing ordered native vectors and resolves the record key to the first
    /// matching lot object, preserving the same order as the source helper chain.</summary>
    public int ActivePersonCount => ActivePersonManagerPresent ? ActivePersonRecordKeys.Count : 0;

    public XPerson? ActivePersonAt(int position)
    {
        var index = ActivePersonIndex(position);
        if (index < 0 || index >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[index];
        if (key == 0) return null;
        return WalkOrder.FirstOrDefault(item => item.TypeRecord?.Key == key) as XPerson;
    }

    /// <summary>FUN_004e9918/FUN_004e9744 followed by FUN_004e9855: resolve the selected family's member key to its
    /// first lot person, creating it through FUN_00440b6d only when no live lot object is present.</summary>
    public XPerson? ResolveOrCreateActivePerson(int position)
    {
        if (!ActivePersonManagerPresent || position < 0 || position >= ActivePersonRecordKeys.Count) return null;
        if (ActivePersonAt(position) is { } existing) return existing;
        var key = ActivePersonRecordKeys[position];
        if (key == 0 || ActivePersonIndex(position) < 0 || ScopedNeighborPersonRecordId(key) is null) return null;
        return WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.TypeRecord?.Key == key);
    }

    /// <summary>FUN_0042A8E3: find the scope keyed by cXObject +0x5ca, or register a zeroed 16-byte scope when absent.</summary>
    public InteractionScopeTable InteractionExecutorScope(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!InteractionScopes.TryGetValue(item.InteractionScopeKey5CA, out var scope))
            InteractionScopes[item.InteractionScopeKey5CA] = scope = new InteractionScopeTable([]);
        return scope;
    }

    /// <summary>Ports the neighborhood-transfer portion of Sims.exe FUN_0045A399. The source copies the seven
    /// x87-backed fields and four raw words into interaction-scope type 2 entries; FUN_0044F7E6 call 0x20 later
    /// consumes those entries when an active-family person is instantiated.</summary>
    public void RunGenericSimsCall11Native(XObject me, short temporary0, short temporary1, short temporary2, short temporary3)
    {
        ArgumentNullException.ThrowIfNull(me);
        if (me.TypeRecord is null) return;

        InteractionScopeTable? timeScope = null;
        XObject? companion = null;
        var wroteState = false;
        if (temporary3 == 1)
        {
            // FUN_0045A399 enumerates the selected family manager's ordered person vector. FamilyBudget's
            // active-person vector is the corresponding ordered source for live members on this lot.
            for (var index = 0; index < ActivePersonCount; index++)
            {
                if (ActivePersonAt(index) is not { } person) continue;
                SavePersonRecord(person); // FUN_00425AB3 copies the versioned persistent fields to its neighbor.
                var scope = InteractionExecutorScope(person);
                StoreGenericSimsCall11State(scope, person);
                timeScope ??= scope;
                wroteState = true;
            }
            if (!wroteState && ActivePersonCount == 0)
            {
                // The original null-member-list path falls through to the running object's own scope.
                timeScope = InteractionExecutorScope(me);
                StoreGenericSimsCall11State(timeScope, me);
                wroteState = true;
            }
        }
        else if (temporary3 == 0)
        {
            // The current scope's type-3 entry is used for temp0 == -1; otherwise the type-1 entry supplies
            // the selected person-record key. PersonRecordKeys mirrors DAT_006F2648's ordered type-2 rows
            // (FUN_004E8C79/FUN_004E8A46), so Contains plus the first live-object lookup reproduces the
            // FUN_004E988F/FUN_004E9744 chain without creating a missing person.
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
            // FUN_0045A399 (0045a399.c:106-118 -> LAB_0045a673 at :164-184) always falls through to
            // snapshot me into sourceScope and restores local_c (me's scope) for the arrival clock (:167, 205-206).
            StoreGenericSimsCall11State(sourceScope, me);
            timeScope = sourceScope;
        }
        else
        {
            // FUN_0045A399's default branch snapshots the running object into its own type-record scope.
            timeScope = InteractionExecutorScope(me);
            StoreGenericSimsCall11State(timeScope, me);
        }

        if (timeScope is not null)
        {
            int hour;
            int minute;
            if (StateClass(unchecked((short)ActiveLotNumber)) == 1 && temporary0 is >= 0x28 and < 0x32)
            {
                // Residential calendar-managed visits store the requested time directly (FUN_004074BC and
                // FUN_0045A910–FUN_0045A95D), instead of adding it to the current clock.
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
        // FUN_0041F4E7(2) retains only type-2 entries; the following FUN_0041F38F calls add their signed
        // 16-bit amounts. Float-to-integer conversion follows __ftol's truncation toward zero.
        scope.RemoveEntriesWithDifferentKey(2);
        ReadOnlySpan<(int Selector, int Mode)> floatFields = [(9, 0), (6, 1), (5, 2), (15, 3), (7, 4), (8, 5), (14, 6)];
        foreach (var (selector, mode) in floatFields)
        {
            var value = source.InteractionExecutorFloats.GetValueOrDefault(selector);
            scope.AddOrIncrement(2, mode, unchecked((int)MathF.Truncate(value)));
        }
        scope.AddOrIncrement(2, 9, source.InteractionBehaviorSavedWord);
        scope.AddOrIncrement(2, 10, source.InteractionExecutorWords.GetValueOrDefault(0x62c));
        scope.AddOrIncrement(2, 11, source.InteractionExecutorWords.GetValueOrDefault(0x630));
        scope.AddOrIncrement(2, 12, source.InteractionExecutorWords.GetValueOrDefault(0x62e));
    }

    /// <summary>FUN_00458485/FUN_0043e2ff visitor-inventory gate. DAT_006e4f0c caches the last nonzero
    /// animation key found while scanning type-13 avatar objects; that key is then looked up as (0, key) in
    /// the current person's +0x5ca scope. A zero cache follows the source's zero-key lookup path.</summary>
    internal bool HasVisitorInventoryEntry(XPerson person)
    {
        var scope = InteractionExecutorScope(person);
        if (_visitorAvatarAnimationKey == 0)
            foreach (var avatar in scope.AvatarObjects)
                if (avatar.ObjectType == 13) _visitorAvatarAnimationKey = avatar.AnimationKey;
        return scope.HasVisitorInventoryEntry(_visitorAvatarAnimationKey);
    }

    /// <summary>FUN_0042597A initializes a person's +0x5ca from the first cached type-record entry matching
    /// its +0x11c key. On a miss FUN_004257EF creates the entry in the first empty one-based manager slot;
    /// repeated persons of the same type reuse that slot.</summary>
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

    /// <summary>FUN_004E9744: take the selected person record's first dword, then return the first person-manager
    /// entry whose type-record key matches it (FUN_0045741B). A zero key or no match returns zero in Sims.exe,
    /// represented here as null so the primitive can apply its decoded missing-record result.</summary>
    public short? ActivePersonRecordId(int index)
    {
        if (index < 0 || index >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[index];
        if (key == 0) return null;
        foreach (var item in WalkOrder.OfType<XPerson>())
            if (item.TypeRecord?.Key == key) return item.Id;
        return null;
    }

    /// <summary>FUN_0042e158's candidate enumeration. It visits the request owner's +0x148 entries in registry order,
    /// keeps OBJD.Type 2 records, resolves each key to the first shared person row (FUN_004e988f), then reads the
    /// first live person and live-person-or-neighbor data (FUN_004e9744/FUN_004ea79e). Selector and mode affect the
    /// row's presentation and callbacks, not this candidate pass.</summary>
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

    /// <summary>FUN_004e978e's missing-row path: construct the type-2 person with FUN_00440b6d, initialize it,
    /// find a valid room-zero lot position around the viewer center using FUN_00434895 defaults, then invoke the
    /// person placement and room/pool callbacks. The viewer center is supplied by the native frontend because it
    /// owns the scene viewport corresponding to Windows FUN_004a6994.</summary>
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
            KillObject(person.Id); // FUN_004e978e calls FUN_0044127f when FUN_00434895 finds no place.
            return null;
        }

        PlaceObject(person, found, checkStack: checkStack);
        RefreshPersonPlacementCallbacksAfterMovement(person);
        return person.Id;
    }

    /// <summary>Completes the kind-1 chooser after its UI callback has selected a materialized person row.
    /// FUN_0042df00 returns that live person's +0xf0 key, clears temporary 0, and FUN_0046dd08 publishes success
    /// through global-person byte +0xe0. The request must be the first queued node for this person because the BHAV
    /// poll consumes the first matching person key.</summary>
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

        // FUN_0042df00 writes the selected object's +0xf0 key to temporary 1. The wrapper clears temporary 0.
        if (!GenericSimsCallRequests.TryComplete(
                requestId, 0, selected.StackObjectKey, GenericSimsCallRequestStatus.Succeeded))
            return false;
        GlobalPerson.ByteE0 = 1;
        return true;
    }

    /// <summary>GenericSimsCall 0x13/0x14's FUN_004e988f/FUN_004e9744 lookup and FUN_004e9855 fallback. The scope
    /// entry's second dword is used directly as a person-record type key; the first matching lot object contributes
    /// its +0xf0 runtime manager index. On a key-table hit with no lot object, the content-backed factories perform
    /// FUN_00440b6d's single-object or multi-tile construction and initialization.</summary>
    public short? ScopedNeighborPersonRecordId(int personRecordKey)
    {
        if (personRecordKey == 0 || !PersonRecordKeys.Contains(personRecordKey)) return null;

        foreach (var item in WalkOrder)
            if (item.TypeRecord?.Key == personRecordKey)
                return item.ClockManagerIndex;

        // FUN_004e9855 resolves the type record and FUN_00440b6d creates it only after the first ordered
        // lot-object lookup misses. The content-backed group factory owns the multi-tile variant and returns
        // its already registered/initialized root; the single-object path mirrors CreateObjectInstancePrimitive.
        if (ObjectGroupFactory?.Invoke(personRecordKey) is { } group)
            return group.Count == 0 ? null : group[0].ClockManagerIndex;

        if (ObjectFactory?.Invoke(personRecordKey) is not { } created) return null;
        Add(created);
        if (created is XPerson person) InitializeCreatedPerson(person);
        InitializeCreatedObject(created);
        if (created is XPerson postInitPerson) InitializeCreatedPerson(postInitPerson);
        return created.ClockManagerIndex;
    }

    /// <summary>FUN_004E98C0: find the first 0x44-byte person record whose key matches the object's type-record key.
    /// A missing person record returns -1; the following FUN_004E99D7 path still runs with that result.</summary>
    public int PersonRecordIndex(XObject item)
    {
        var key = item.TypeRecord?.Key
            ?? throw new InvalidOperationException($"Object {item.Id} has no type record for person-record lookup.");
        return PersonRecordKeys.IndexOf(key);
    }

    /// <summary>FUN_004ea79e selects on-lot person data first (+0x58c), then the associated neighbor's person data
    /// (+0x74). The ordered record key selects the first matching live person, as FUN_004e9744 does.</summary>
    public short[]? PersonRecordData(int personRecordIndex)
    {
        if (personRecordIndex < 0 || personRecordIndex >= PersonRecordKeys.Count) return null;
        var key = PersonRecordKeys[personRecordIndex];
        short[]? personData = key == 0 ? null : WalkOrder.OfType<XPerson>()
            .FirstOrDefault(item => item.TypeRecord?.Key == key) is XPerson person ? person.PersonData : null;
        if (personData is null) personData = PersonRecordNeighbor(personRecordIndex)?.PersonData;
        return personData;
    }

    /// <summary>Applies FUN_0045928c's FCNS 2 values to native autonomy and mutual-friend gates.</summary>
    public void ApplySimulatorConstants(SimulatorConstants constants)
    {
        SimulatorTuning = FindBestInteractionTuning.FromSimulatorConstants(constants);
        RoomScoreTuning = SimsEngine.World.RoomScoreTuning.FromSimulatorConstants(constants);
        HouseYardScoreMultiplier = constants.Get("yard score multiplier", 0.33f);
        HouseUpkeepPenaltyPerDirtyObject = (int)Math.Round(constants.Get("upkeep penalty per dirty object", 6f), MidpointRounding.ToEven);
        HouseUpkeepPenaltyPerBrokenObject = (int)Math.Round(constants.Get("upkeep penalty per broken object", 15f), MidpointRounding.ToEven);
        HouseLayoutHistoryFillValue = (int)Math.Round(constants.Get("fill value for layout history", 200f), MidpointRounding.ToEven);
        FriendshipThreshold = SimulatorTuning.FriendshipThreshold;
    }

    /// <summary>Initializes the autonomy candidate bit for one object after its multitile root and TTAB resource are
    /// known. This is the native equivalent of the bit-3 write in FUN_00431c90/FUN_00431edd.</summary>
    public bool InitializeAutonomyCandidate(XObject item, XObject root, InteractionTable? interactions)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item, ObjectById(item.Id)))
            throw new InvalidOperationException($"Object {item.Id} is not registered in this simulation world.");
        var eligible = AutonomyCandidateEligibility.IsEligible(item, root, interactions);
        ClockManager.SetFlag(item.ClockManagerIndex, 3, eligible);
        return eligible;
    }

    /// <summary>FUN_004ea2ce: use the person record's cached neighbor id, or lazily resolve its type-record key
    /// against the ordered neighbor manager vector and cache the found id.</summary>
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
        // FUN_004EA2CE writes the zero result from its ordered scan back to record +4, so later lookups
        // do not repeat the manager walk until the person-record association is reset.
        PersonRecordNeighborIds[personRecordIndex] = 0;
        return null;
    }

    /// <summary>FUN_00427281 sums the star power of eligible mutual friends in neighborhood-vector order.</summary>
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

    /// <summary>FUN_00405333 compares the configured friend requirement to half the mutual-friends' star power.</summary>
    public bool MeetsFameFriendPowerGate(int personRecordIndex, int requiredFriendPower) =>
        requiredFriendPower <= FameFriendStarPower(personRecordIndex) * 0.5f;

    /// <summary>FUN_00404cef: apply the promotion's Fame-score and six skill gates, returning the native record view
    /// plus the per-star friend-power threshold for the remaining FUN_00405333 gate.</summary>
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

    /// <summary>FUN_00404cef's successful tail increments Fame Star Power and raises its high watermark only when needed.</summary>
    public static void ApplyFamePromotion(FamePromotionCandidate candidate)
    {
        var promoted = (short)(candidate.CurrentStarPower + 1);
        candidate.PersonData[(int)PersonData.FameStarPower09] = promoted;
        if (candidate.PersonData[(int)PersonData.FameStarHighWatermark] < promoted)
            candidate.PersonData[(int)PersonData.FameStarHighWatermark] = promoted;
    }

    /// <summary>FUN_00404df6: lower Fame Star Power when Fame Score is below the corresponding manager threshold.</summary>
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

    /// <summary>FUN_004E99D7: resolve the indexed record's type key to the first matching lot object, then apply its
    /// state-class and override tests. Null means the source returns without calling FUN_00445489; a selection with
    /// <see cref="PersonRecordSelection.Person"/> null means FUN_00445489(0) clears the selected person.</summary>
    public PersonRecordSelection? EvaluatePersonRecordSelection(int index)
    {
        if (!PersonRecordManagerPresent) return null;
        var key = index >= 0 && index < PersonRecordKeys.Count ? PersonRecordKeys[index] : 0;
        var person = key == 0 ? null : ObjectByTypeRecordKey(key);
        if (person is null) return new PersonRecordSelection(null);

        var state = person.StateValue;
        var stateClass = StateClass(state);
        if (stateClass == 1)
            return new PersonRecordSelection(PersonSelectionOverride || person.SelectionWord5CC == 0 ? person : null);

        if (stateClass != 2 && !(state > 0x14 && state < 0x1f) && !(state > 0x27 && state < 0x32))
            return null;

        if (PersonSelectionOverride) return new PersonRecordSelection(person);
        if (state <= 0x14 || state >= 0x1f)
        {
            if (StoredSlotPersonId is null)
                throw new InvalidOperationException("FUN_004E99D7 reads the stored slot word, but the manager slot object is missing.");
            if (person.SelectionWord606 == StoredSlotPersonId.Value) return new PersonRecordSelection(person);
        }

        return new PersonRecordSelection(person.SelectionWord5CC != 2 ? person : null);
    }

    /// <summary>FUN_00445489: compare against FUN_00445724's first object with selected bit 1 and run the old-object
    /// deselection sequence before the new-object selection sequence. Selection flags are native, including the
    /// multi-tile chain. Host callbacks remain optional view effects; FUN_0044668C's object-module behavior is not
    /// represented by these flag and notice updates.</summary>
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

    /// <summary>FUN_0042ba25: state zero has class 1; otherwise return the first matching manager entry, or zero.</summary>
    public int StateClass(short state)
    {
        if (state == 0) return 1;
        foreach (var entry in StateClasses)
            if (entry.State == state) return entry.Class;
        return 0;
    }

    /// <summary>FUN_00410b89, invoked by GenericSimsCall 0x2b when the native lot manager exists. It raises the six
    /// attributes of the first live Magic Master Spells object to the running object's six resource minima.</summary>
    public void RunLotUpkeep(XObject runningObject)
    {
        ArgumentNullException.ThrowIfNull(runningObject);
        if (!LotUpkeepManagerPresent || !runningObject.LotUpkeepGateByte14C ||
            runningObject.TableWord10C == 0 || runningObject.TableWord10C != GlobalPerson.LotUpkeepMatchWord22 ||
            StateClass(runningObject.StateValue) != 1 || !runningObject.LotUpkeepPointer128Present)
            return;

        var resources = ObjectByTypeRecordKey(unchecked((int)0xB6C90029))?.Attributes;
        if (resources is null) return;
        if (resources.Length < 6)
            throw new InvalidOperationException("Sims.exe FUN_00410b89 requires six Magic Master Spells resource attributes.");
        for (var i = 0; i < 6; i++)
            if (resources[i] < runningObject.LotUpkeepMinimumResources[i])
                resources[i] = runningObject.LotUpkeepMinimumResources[i];
    }

    /// <summary>FUN_0045e5a9's cached room-key write, household light transitions through FUN_004688e4, and
    /// FUN_0045cdf8/FUN_0045d060's Room-motive refresh. Room scores remain explicit world inputs.</summary>
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
        if (currentRoom == 0xfffb) return; // Sims.exe skips the -5 off-lot sentinel.

        var motive = 0f;
        if (PersonRoomMotiveEntries.TryGetValue(currentRoom, out var room))
            motive = room.IsOutside ? OutsidePersonRoomMotiveValue : room.InteriorValue;
        var stateValue = person.StateValue != 0 ? person.StateValue : NeighborInteractionGlobalStateValue;
        if (currentRoom == 0 && StateClass(stateValue) != 1 && motive < 0f)
            motive = 0f;
        person[Motive.Room] = motive;
    }

    /// <summary>Applies FUN_0045e5a9's room-transition tail immediately after the person position callback updates
    /// cXObject +0x84. Route callbacks can yield for several clock passes, so waiting for the next completed person
    /// BHAV update leaves the cached +0xafc room, room lights, and Room motive stale while the Sim is crossing a room.</summary>
    internal void RefreshPersonRoomStateAfterMovement(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (person.LastObservedRoomIdAFC == unchecked((ushort)person.RoomWord84)) return;
        UpdatePersonRoomMotive(person);
    }

    /// <summary>Models the source-ordered person +0x88/+0x74 placement callbacks: +0x88 updates the cached
    /// room/light state through FUN_0045e5a9, then +0x74 refreshes cXPerson +0x60c from the current tile's
    /// HasPool result. The field behavior matches Windows 0x0045e685 and Mac cXPerson::ForceLocation at
    /// person.s 0x101003E0–0x101004C0. The Windows animator +0x24 callback invalidates the root grid fields;
    /// native scene geometry represents the source -999 sentinels as absent roots and falls back to object state.</summary>
    internal void RefreshPersonPlacementCallbacksAfterMovement(XPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        RefreshPersonRoomStateAfterMovement(person);
        InvalidateAnimatorRootGrid(person);
        person.AnimatorPoolTile = IsPoolTile(person.Position);
    }

    /// <summary>Normalizes the source animator's -999 grid-coordinate sentinel to absent native roots. Windows
    /// animator +0x20 writes -999 to +0x80/+0x84/+0x88 and raises +0xd9; Mac SnapToGrid writes the same sentinel
    /// through SetGridX/SetGridY/SetDirection. Scene geometry then falls back to the current object position and
    /// direction until a route seeds fresh floating roots.</summary>
    private static void InvalidateAnimatorRootGrid(XPerson person)
    {
        person.AnimatorRootX = null;
        person.AnimatorRootY = null;
        person.AnimatorRootDirection = null;
    }

    /// <summary>Ports FUN_004688e4's indoor room light-toggle state and FUN_00467805's per-room tile writes.</summary>
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

    /// <summary>FUN_0044aa97, called by Sleep after its wait-cell and positive-counter checks. The source returns false
    /// for OBJD type 2. Otherwise it returns true only when the first runtime SpriteSlot at cXObject +0x148 has a zero
    /// +0x10 dword (or the vector is empty) and cXObject +0x7c is zero. The raw +0x7c field's meaning is not fully
    /// traced.</summary>
    public bool SleepWritesWaitCell(XObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Definition[(int)ObjdField.Type] == 2) return false;
        if (item.RuntimeSlots.Count > 0 && item.RuntimeSlots[0].ReadWord(0x10) != 0) return false;
        return item.SleepGateWord7C == 0;
    }

    /// <summary>FUN_0042a2fe, Mac Neighborhood::UpdateInstanceVisitorTypes: refresh selection word +0x5cc over
    /// ObjectModule's person list (+0x90). Family::TestMember (FUN_0041022e) checks the person's GUID against the active
    /// family's members (+0x12c); FUN_004074bc classifies the global manager's +0x24 state value. Both source inputs
    /// are explicit native world state.</summary>
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

    /// <summary>The modeled object with native identity <paramref name="id"/>, or null. Frame stack-object keys use
    /// <see cref="ObjectByStackObject"/> instead.</summary>
    public XObject? ObjectById(short id) => Objects.GetValueOrDefault(id);

    /// <summary>FUN_00443bad on cXObject +0xdc: append the running object to its ordered +0x38/+0x3c list, then
    /// notify only when the list changes from empty to non-empty. Duplicate pointers are retained.</summary>
    public void MaintainLotList(XObject owner)
    {
        var wasEmpty = owner.LotListEntries.Count == 0;
        owner.LotListEntries.Add(owner);
        if (wasEmpty) DispatchNotice(0x108, 0);
    }

    /// <summary>FUN_00443c37: remove the first matching object pointer from the ordered lot list. Its notice is sent
    /// only when the list changes between empty and non-empty, as the before/after count test in the caller requires.</summary>
    public void RemoveFromLotList(XObject owner)
    {
        var wasEmpty = owner.LotListEntries.Count == 0;
        var index = owner.LotListEntries.FindIndex(item => ReferenceEquals(item, owner));
        if (index >= 0) owner.LotListEntries.RemoveAt(index);
        var isEmpty = owner.LotListEntries.Count == 0;
        if (wasEmpty != isEmpty) DispatchNotice(0x108, 0);
    }

    /// <summary>FUN_00425DB3: returns the first object in the manager's +0x154/+0x158 vector whose +0x10c
    /// dword equals <paramref name="id"/>. Entry order matters when keys are duplicated.</summary>
    public XObject? FindInteractionObjectByTableWord(short id)
    {
        foreach (var item in InteractionLookupOrder)
            if (item.TableWord10C == id) return item;
        return null;
    }

    /// <summary>FUN_0042573E: reads the one-based +0x148/+0x14c lot-manager vector; zero, negative,
    /// out-of-range and null entries all return null.</summary>
    public XObject? NeighborTableEntry(short index) =>
        index > 0 && index <= NeighborTableEntries.Count ? NeighborTableEntries[index - 1] : null;

    /// <summary>Sets one-based manager-vector entry, filling skipped slots with null pointers.</summary>
    public void SetNeighborTableEntry(short index, XObject? item)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        while (NeighborTableEntries.Count < index) NeighborTableEntries.Add(null);
        NeighborTableEntries[index - 1] = item;
    }

    /// <summary>FUN_00425b48: returns the neighbor's +0x8 selector pointer from the one-based manager vector.
    /// Native factories use the selector's GUID; a null selector must still answer zero.</summary>
    public int StackObjectCreateKey(short index) => NeighborManagerEntry(index) is { FriendEligibilityObjectFlagsB4: not null } neighbor
        ? neighbor.TypeRecordKey : 0;

    /// <summary>FUN_00410304/FUN_0042730F: computes the amount charged by GenericSimsCall 0x05. The ordinary path
    /// adds object words +0x118 and +0x11c. When manager +0x164 matches object +0x110, Sims.exe instead uses the
    /// active global object's budget and value fields, with +0xa4 scaled by DAT_006E4A38 and converted toward zero
    /// by __ftol/FUN_0063604C. Missing special-path inputs are reported rather than guessed.</summary>
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

        static int MultiplyAndTruncateX87(int value, float factor)
        {
            // The original fild/fmul/fistp sequence keeps the exact int32 × float32 product in
            // x87 extended precision, then truncates toward zero. The product needs at most 56
            // significant bits, so an integer rational preserves it exactly here.
            var bits = BitConverter.SingleToInt32Bits(factor);
            var exponentBits = (bits >> 23) & 0xff;
            if (exponentBits == 0xff) return 0; // x87 FISTP qword's invalid-operation result has low dword zero.
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
    }

    /// <summary>Resolves a frame's stack-object word through the running object's +0xdc table, as FUN_0044578b does
    /// after FUN_00452bd8 stores the pushed object's +0xf0 key. Id fallback is retained only for synthetic running
    /// objects without a raw request key; a native object with no manager table treats the lookup as a miss.</summary>
    public XObject? ObjectByStackObject(XObject me, short key)
    {
        if (me.ObjectTableEntries.Count > 0) return me.ObjectTableEntry(key);
        // Compact synthetic worlds predate raw +0xf0 keys and use ids directly. In a native lot, FUN_00407498
        // returns zero for a missing/out-of-range manager entry; an external host cannot fill that source table.
        return me.HasRequestKey ? null : ObjectById(key);
    }

    /// <summary>FUN_00425ab3: look up the person's neighbor record by PersonData.NeighborId, write layout version 9
    /// (+0x18), and copy the version-9 fields selected by FUN_0042385a into the record at +0x74. No record is a no-op.</summary>
    public void SavePersonRecord(XPerson person)
    {
        var neighborId = person[PersonData.NeighborId];
        if (neighborId < 1 || !Neighbors.TryGetValue(neighborId, out var neighbor)) return;
        var changed = neighbor.PersonRecordVersion != 9; // FUN_00423e37 returns 9.
        neighbor.PersonRecordVersion = 9;
        // The NBRS record stores the Sim's name separately from its persistent PersonData.
        // Native text-entry dialogs update NeighborState.Name directly, so compare it with
        // the loaded record to ensure a name-only change also reaches the neighborhood file.
        if (SavedNeighbors?.Entries.FirstOrDefault(saved => saved.NeighborId == neighborId) is { } savedNeighbor &&
            !string.Equals(savedNeighbor.Name, neighbor.Name, StringComparison.Ordinal))
            changed = true;
        if (person.Name is { } personName && !string.Equals(neighbor.Name, personName, StringComparison.Ordinal))
        {
            neighbor.Name = personName;
            changed = true;
        }
        foreach (var field in PersonRecordFieldsVersion9)
        {
            if (neighbor.PersonData[field] == person.PersonData[field]) continue;
            neighbor.PersonData[field] = person.PersonData[field];
            changed = true;
        }
        PersistentPersonDataDirty |= changed;
    }

    /// <summary>Mac Neighborhood::SaveAllPersistentDataForMatterTransferance copies the latest persistent
    /// fields from each instantiated person to its neighbor record before saving the neighborhood.</summary>
    public void SaveAllPersistentPersonData()
    {
        foreach (var person in WalkOrder.OfType<XPerson>()) SavePersonRecord(person);
    }

    /// <summary>FUN_0044097D: the first object in the lot's +0x84/+0x88 vector whose type record key matches
    /// <paramref name="key"/>. FUN_0045741B reads the dword at +0x1c in the definition reached through +0x60. The
    /// original dereferences each object's type record without a null check; a missing model record is reported.</summary>
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

    /// <summary>FUN_0046DD08's pending-request pass followed by FUN_0046DCC7's age/expiry pass. Without a host, work
    /// remains pending but the manager's native queue still advances its age.</summary>
    public void UpdateGenericSimsCallRequests()
    {
        var host = Host;
        foreach (var request in GenericSimsCallRequests.Snapshot())
        {
            if (request.Status != 0) continue;

            // FUN_0046DD08 tests DAT_006e486c+0x24 before calling either worker, so an absent manager leaves the
            // node pending. Without a host the native server is the live-lot manager owner.
            if (host is not null && !host.GenericSimsCallRequestManagerAvailable()) continue;

            // FUN_0042DB93/FUN_0042DF00 scan the person manager vector (+0x84..+0x88), not the full lot object walk.
            var target = WalkOrder.OfType<XPerson>().FirstOrDefault(person => person.StackObjectKey == request.Temporary0);
            // The worker consumes a missing-person lookup as failure without the interactive picker. A live target
            // still requires the UI-backed picker owner.
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
                    // FUN_0042DF00 leaves its input word alone and writes zero to the output word when the person
                    // is found but no action matches.
                    result = result with { Temporary0 = request.Temporary0, Temporary1 = 0 };
                }
                else if (result.Succeeded)
                {
                    var key = result.TargetTypeRecordKey
                        ?? throw new InvalidOperationException("GenericSimsCall request kind 1 succeeded without a type-record key.");
                    var resultPerson = ObjectByTypeRecordKey(key)
                        ?? throw new InvalidOperationException($"GenericSimsCall request kind 1 could not resolve type-record key {key}.");
                    // FUN_0042DF00 clears request temporary 0 itself on the successful-action path, before
                    // resolving the returned type-record key to the selected object's +0xf0 key.
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

    /// <summary>Runs one restored behavior stack for every live object in lot walk order. This order and the snapshot
    /// boundary are an explicit scheduler inference: Sims.exe's main loop calls FUN_0046d461 before the asynchronous
    /// request pass in FUN_0046daf2, but the original global object-thread iteration has not yet been located. Objects
    /// removed or replaced earlier in this pass are skipped; objects added during the pass begin on the next tick.</summary>
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

    /// <summary>Mirrors cXPerson::Simulate's periodic VitaBoy::SetScale gate (Mac person.s 0x10103190–0x10103238;
    /// Windows FUN_0045cdf8): +0x8e must be clear, the simulator pass counter divisible by ten, and ScalingSim / 100
    /// strictly between 0.4 and 10. A rejected value leaves VitaBoy's previous scale untouched.</summary>
    internal static void UpdateAnimatorBodyScale(XPerson person, int simulationTick)
    {
        if (person.PersonTickGateWord8E != 0 || person.AnimatorScaleUpdateSuppressed || simulationTick % 10 != 0) return;
        var scale = person[PersonData.ScalingSim] / 100f;
        if (scale > 0.4f && scale < 10f) person.AnimatorBodyScale = scale;
    }

    /// <summary>Completes the object-simulation tail in Mac cXObject::Simulate and Windows FUN_00452237.
    /// This runs after the object tree reports success, including objects with no restored tree; yielded trees skip it.</summary>
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
        var moneyOverHead = person[PersonData.MoneyAmountOverHead];
        if (moneyOverHead != 0)
        {
            if (moneyOverHead is >= -20000 and <= -10001)
                PlaySoundEvent?.Invoke("sting_char_winner", person.Id, false, false);
            person[PersonData.MoneyAmountOverHead] = 0;
        }
    }

    /// <summary>Advances the currently modeled part of one native simulation tick: restored object stacks first, then
    /// the Generic Sims Call manager pass and request aging. Sims.exe `FUN_0046daf2` invokes `FUN_0046d461` before
    /// `FUN_0046dd08`; object-stack iteration within that clock/update call remains inferred (see
    /// <see cref="TickSavedExecutionStacks"/>). This does not yet implement the rest of `FUN_0046d461`'s clock,
    /// neighborhood, room, or object-manager updates. <paramref name="advanceGameTime"/> must be the resolved
    /// result of the original FUN_0046d66f gate; omitting it leaves the clock paused.</summary>
    public int AdvanceSimulationTick(bool advanceGameTime = false)
    {
        AdvanceClock(advanceGameTime, simulationTickCompleted: advanceGameTime);
        var scheduled = TickSavedExecutionStacks(Clock.Ticks);
        UpdateGenericSimsCallRequests();
        RefreshPendingRoomGroups();
        return scheduled;
    }

    /// <summary>Runs the modeled FUN_0046d66f object-manager pass, then advances the clock only after a complete
    /// pass with no global hold, followed by the FUN_0046dd08 request pass. <paramref name="tickClockObject"/> owns
    /// per-object execution for this overload; it is not followed by the inferred saved-stack sweep. The
    /// FUN_00439b54 special path remains explicitly incomplete and therefore does not advance time.</summary>
    public int AdvanceSimulationTick(
        SimulationClockControl control,
        short clockState28,
        int clockTickCounter,
        Func<XObject, int, bool> tickClockObject)
    {
        // FUN_0046d461 swaps the lot manager's saved +0xac seed into the process RNG for the entire
        // FUN_0046d66f pass, then persists its advancement and restores the ambient process stream.
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
        // FUN_00442db0 queues removal after the object's +0x50 callback; commit only after the
        // object-manager walk so its current interaction can finish this pass.
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

        // The native BHAV global table is what Sims.exe's Get/Set Global primitives expose. Keep it in step
        // with cSimulator's hour/minute/second and derived day/dusk/night/dawn value as time advances.
        if (advanceGameTime)
        {
            this[SimulationGlobal.Hour] = checked((short)Clock.Hour);
            this[SimulationGlobal.Minute] = checked((short)Clock.Minute);
            this[SimulationGlobal.Second] = checked((short)Clock.Second);
            this[SimulationGlobal.TimeOfDay] = checked((short)Clock.TimeOfDay);
        }

        if (!result.DayChanged) return;

        // GameTime::CountDaysInMonth (TimeOfDay.s, 0x101460c0) returns 30 for every month.
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
        // FUN_0046d461 increments Days Running (+0x48) on every day rollover and, when the month advances,
        // stores FUN_0047365d's 30 in Maximum Day Number (+0x4a).
        this[SimulationGlobal.DaysRunning] = unchecked((short)(this[SimulationGlobal.DaysRunning] + 1));
        if (nextDate.Month != date.Month) this[SimulationGlobal.MaximumDayNumber] = 30;
        this[SimulationGlobal.DayOfMonth] = nextDate.Day;
        this[SimulationGlobal.Month] = nextDate.Month;
        this[SimulationGlobal.Year] = nextDate.Year;
        // ObjectModule::DayChanged walks the live objects and calls cXObject::DayPassed; its first date-dependent
        // writes are the object's SimAge and AgeInMonths, followed by FUN_004322f2's daily depreciation.
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

    /// <summary>Consumes the removal vector populated by Sims.exe FUN_00442db0/FUN_004029df after the
    /// current object-manager pass. The source callback has already relocated each queued object out of the lot;
    /// this removes its manager/object-table entry and tears down its live execution state.</summary>
    private void CommitPendingObjectRemovals()
    {
        if (PendingRemovals.Count == 0) return;
        var removals = PendingRemovals.Distinct().ToArray();
        PendingRemovals.Clear();
        foreach (var id in removals) KillObject(id);
    }

    /// <summary>Runs the clock-manager pass using the native restored-stack runner as the object callback. This
    /// connects Sims.exe FUN_0046d66f's manager walk to FUN_00452237/FUN_00474ff5 without a caller-supplied shim.
    /// Objects without a restored active stack have no modeled tree work and return success; a yielded stack returns
    /// false and suspends the manager pass, while a stopped stack returns true and applies the source UseCount/slot tail.
    /// </summary>
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
                // Sims.exe FUN_00439b54 routes this failure into the cXObject reset virtual. Record the source
                // error and apply the modeled FUN_0043206d state path. Mac TickAllObjects calls HandleError and
                // returns false on either pre-existing or newly raised object error, so the manager must retain its
                // cursor and stop this pass after the reset. Its initializer
                // frame is queued for later clock-manager execution; other derived defaults and full placement
                // validation remain outside this model.
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
                // FUN_00439b54 (00439b54.c:28-29, 185-186) broadcasts the errored subject via FUN_00444346
                // and invokes vtable +0x38(0): FUN_0045e8fd for cXPerson, FUN_0043206d for cXObject.
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
                // Reset may have pushed the source initializer as a new resumable root frame. The ObjM writer
                // serializes this decoded snapshot, not SavedExecutionStacks, so retain that frame for saves.
                // The normal Sims.exe object-error reset (FUN_0043206d) ends by setting this manager
                // entry's wait word to -1 through FUN_00431e78. Keep the object eligible for the next
                // manager pass after the failed saved execution stack has been cleared.
                if (item.ClockManagerIndex > 0)
                    ClockManager.SetWaitTicksAt(item.ClockManagerIndex, -1);
                return false;
            }
            catch (Exception error)
            {
                // Diagnostics only: the manager walk cannot say which object's stack threw.
                error.Data["tickingObject"] = item.Id;
                error.Data["executionLocation"] = CurrentExecutionLocation;
                throw;
            }
        }
    }

    internal void TickAvatarAnimations(XPerson person)
    {
        person.AnimatorMilliseconds = unchecked(person.AnimatorMilliseconds + 42);
        if (person.AnimationPlayback is { RouteDriven: true } && person.NativeRoute is null)
            person.AnimationPlayback = null;
        UpdateCarryPractice(person);
        UpdateBackgroundPractice(person);
        UpdateAnimatorWalkingScale(person);
        // Mac SAnimator::Tick performs OutOfBounds before its control-state dispatch. The out-of-bounds
        // branch skips the dispatch, but still reaches the full avatar practice tick below.
        // Also pause route locomotion dispatch while NativeOutfitTransitionActive is playing a2o/c2o-clotheschange
        // so the route entry gate and root movement do not advance or overwrite the outfit change animation.
        if (!AnimatorOutOfBounds(person) && !person.NativeOutfitTransitionActive)
        {
            // BeginFollow sets its transition target to the current root and heading. Its first
            // state-3 Tick therefore matches the current pose and skips the turn/root-step pass,
            // selecting state 9/10 through ApplyAnimatorRouteEntryGate; on the next tick state 9
            // evaluates lookahead heading error and drops to state 3 -> state 4 body turn if
            // |delta| >= 2.0 octants and the body-turn clip is available (0058dc76.c:262-268, 892-907).
            var firstFollowTick = person.NativeRoute is { BeginFollowPendingTick: true };
            if (firstFollowTick)
                person.NativeRoute = person.NativeRoute! with { BeginFollowPendingTick = false };
            var routeHeadingClamped = !firstFollowTick && UpdateAnimatorRootDirection(this, person);
            if (!firstFollowTick)
                RouteExecutor.TryBeginRouteTurnRoot(this, person);
            ApplyAnimatorRouteEntryGate(person, routeHeadingClamped);
            RouteExecutor.TickRouteTurnRootInterpolation(this, person);
            if (person.AnimatorPoolTile && person.AnimatorRouteTransitionState == 0)
                person.AnimatorRouteTransitionState = 1; // SAnimator::Tick writes +0x280=1 after its pool route dispatch.
            switch (person.AnimatorControlState)
            {
                case 0 when person.AnimatorPracticeScaleEnabled && person.AnimationPlayback is { } primaryPractice:
                    // Mac state 0 reapplies GetWalkingScale to the primary practice while preserving its rate sign
                    // (SAnimator.s 0x1034F24C–0x1034F290). Practice::SetScale divides skill duration by this value.
                    var practiceScale = person.AnimatorWalkingScale;
                    primaryPractice.SetPracticeScale(primaryPractice.PracticeScale < 0f ? -practiceScale : practiceScale);
                    break;
                case 1:
                    // SAnimator.s 0x1034F294–0x1034F2F4: with a primary practice and Freeze for Idle clear,
                    // state 1 calls StopCurAnim(+0x104), resets the background cache +0x190, then dispatches
                    // UpdateBackgroundPractice(+0x60). All state-1/2 branches clear +0x194/+0x288.
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
                    // SAnimator.s 0x1034F2F8–0x1034F348: the same StopCurAnim/background refresh without
                    // the Freeze for Idle gate.
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
                    // These dispatch cases clear +0x194/+0x288 (SAnimator.s 0x1034FB04–0x1034FB10,
                    // and 0x10350430–0x10350438).
                    person.AnimatorGaitEntryState = 0;
                    person.AnimatorPoolTransitionState = 0;
                    break;
                case 8:
                    // The state-8 case marks both transition flags before returning (0x10350474–0x1035047C).
                    person.AnimatorGaitEntryState = 1;
                    person.AnimatorPoolTransitionState = 1;
                    break;
            }
            if (person.AnimatorControlState is 6 or 7 or 11)
            {
                // SAnimator::Tick's gait-state cases clear +0x194/+0x288: state 6 at 0x1034FF44–0x1034FF50,
                // state 7 at 0x10350044–0x1035004C, and state 11 at 0x10350430–0x10350438.
                person.AnimatorGaitEntryState = 0;
                person.AnimatorPoolTransitionState = 0;
            }
            if (person.AnimatorControlState != 5 && person.NativeRoute?.MotionAnimationIndex is >= 11 and <= 18)
            {
                // SAnimator::Tick's direction-sector dispatch writes +0x288 after selecting standing-adjust skill 11–18.
                person.AnimatorPoolTransitionState = 1;
            }
            TickRouteLocomotionFade(person);
            TickRouteFollowTransition(person);
            TickPoolSwimExit(person);
            TickDeferredEndFollow(person);
            RouteExecutor.TickStandingStartRootInterpolation(this, person);
            RouteExecutor.TickRouteRootMovement(this, person,
                SavedExecutionStacks.GetValueOrDefault(person.Id));
        }
        if (AvatarAnimationRootOnly) return;
        foreach (var playback in person.GetAnimationPracticeApplicationOrder())
            Tick(playback);

        void Tick(AvatarAnimationPlayback playback)
        {
            playback.Tick(deliverAnimationEvents: true, person.AnimatorMilliseconds);
            person.PendingAnimationEvents.AddRange(playback.EventQueue);
            playback.EventQueue.Clear();
            foreach (var sound in playback.SoundEventQueue)
            {
                if (ApplyAnimationTimeProperty(person, playback, sound)) continue;
                AnimationSound?.Invoke(person.Id, sound);
            }
            playback.SoundEventQueue.Clear();
        }
        // SAnimator::Tick updates HEAD only after the full practice tick; root-only ticks return through the separate
        // AnimationTickRootOnly path and never reach AdjustHead/UpdateHeadDirection (Mac SAnimator.s
        // 0x1035150C–0x10351538; root-only dispatch 0x10351294–0x103512A8).
        TickHeadPosture(person);
    }

    /// <summary>Applies SAnimator::HandleTimeProps state mutations (00591301.c:37-95) for non-audio time properties.</summary>
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
                // SAnimator::HandleTimeProps (00591301.c:65-76) -> vtable +0xd8 (005859df.c).
                person.DressAccessory(prop.Cue);
                return true;
            case "undress":
                // SAnimator::HandleTimeProps (00591301.c:78, 257-263) -> vtable +0xdc (0058d91d.c).
                person.UndressAccessory(prop.Cue);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Runs the full-tick AdjustHead then UpdateHeadDirection sequence. AdjustHead consumes the cached
    /// directions first; UpdateHeadDirection then resolves the current target, refreshes those directions, and applies
    /// the source floor-level check (Mac SAnimator.s 0x1035150C–0x10351538, Windows FUN_0059076c / FUN_005904d6).</summary>
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

        // null means the rendering-derived registration cache has not been built yet; only a known-missing
        // registration set should suppress the resolver. Mac checks the SetupRegistrations pointers directly,
        // and Windows FUN_005904d6 likewise gates on its actor registration pointers.
        if (person.AnimatorHeadSeekRegistrationsAvailable == false ||
            (person.AnimatorHeadSeekRegistrationsAvailable is null && HeadSeekGeometryResolver is null) ||
            (person[PersonData.HeadSeekState] & 1) == 0) return;

        XObject? target = null;
        if (person[PersonData.HeadSeekObject] != 0)
        {
            // FUN_005904d6 resolves the saved head-seek key with FUN_00407498, the object's manager table.
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

    /// <summary>Mac SAnimator::StopCurAnim (+0x104): stops the primary practice, then writes -1 to the current
    /// standard-skill index +0x98 (SAnimator.s 0x1034AF60–0x1034AF8C). Its StopAnimation fade path is unreachable:
    /// the fade-enable byte at lbl_106504DC is zero-initialized and has no writer in the image.</summary>
    internal void StopCurrentAnimation(XPerson person)
    {
        person.AnimationPlayback = null;
        person.AnimatorCurrentSkill = -1;
    }

    /// <summary>Mac SAnimator::StopReachAnimation (0x10356470–0x10356514): StopCurAnim, clear the reach/carry
    /// gate +0x19c, erase the shared XEVT queue, then select the standing control state from PersonData[Sitting].
    /// Sitting 0 enters state 3, 1 enters state 1, 2 enters state 2; -1 and other values leave it unchanged.</summary>
    internal void StopReachAnimation(XPerson person)
    {
        StopCurrentAnimation(person);
        person.ReachAnimationActive = false;
        person.PendingAnimationEvents.Clear();
        person.AnimatorControlState = person[PersonData.Sitting] switch
        {
            0 => 3,
            1 => 1,
            2 => 2,
            _ => person.AnimatorControlState,
        };
    }

    /// <summary>Applies SAnimator::Tick's bounded +0x88 update toward the active lookahead target (0058dc76.c:868-925).
    /// When the wrapped heading error in state 9/10 reaches fRam006d078c (2.0 octants = 90 deg), Tick writes
    /// +0x1bc/+0x1c0 = current root, +0x188 = 3, +0x194 = 0 and redispatches state 3 -> state 4 body turn in place
    /// (0058dc76.c:892-907). Otherwise the default step is 0.2 (0.056 for body-string mode > 1), divided by
    /// AnimatorWalkingScale when below 1 (SAnimator.s 0x1034F58C–0x1034F638, 0x10350D60–0x10350E28).</summary>
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

    /// <summary>Matches SAnimator::Tick's route-entry branch (Mac SAnimator.s 0x10350E38–0x10350EB0; Windows
    /// FUN_0058dc76). +0x284/+0x194 and current skills 0x14/0x15 force gait entry. Otherwise pool Sims take the
    /// standing state; non-pool Sims enter gait when the turn was clamped or the projected root step is no longer
    /// than the cached walk/run threshold.</summary>
    internal void ApplyAnimatorRouteEntryGate(XPerson person, bool routeHeadingClamped)
    {
        if (person.AnimatorControlState is not (3 or 9) || person.NativeRoute is not
            { MotionTargetX: not int.MinValue, MotionTargetY: not int.MinValue } route) return;

        var walkStyle = person[ObjectData.WalkStyle];
        var running = walkStyle == 1;
        var currentSkill = person.AnimatorCurrentSkill;
        // SAnimator::Tick's state-3 path selects +0x188 = 9/10 before the gait-entry gate
        // when FollowOneStep's current point is far enough away (SAnimator.s 0x10350694–0x10350708).
        // BeginFollow clears +0x280; the Mac threshold is +0x68 of the cached walk/run distance
        // multiplier, which is 3 FTile for walking and 4.5 FTile for running.
        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        // The route-preference arrival branches straight to the step at 0x10350F4C, past this gate and its
        // +0x28c/+0x290 reset, so the stored step survives for that arrival (SAnimator.s 0x10350798–0x103507AC).
        // Terminal follow states (-1/2) only run the state-3 arrival heading match/turn gate, never re-entering gait.
        if (route.TransitionTargetX is not null || route.AnimatorFollowState is -1 or 2 ||
            RouteExecutor.IsRoutePreferenceArrival(person, route, rootX, rootY)) return;
        var remainingDistance = float.IsFinite(rootX) && float.IsFinite(rootY)
            ? MathF.Sqrt(MathF.Pow(route.MotionTargetX - rootX, 2) + MathF.Pow(route.MotionTargetY - rootY, 2))
            : 0f;
        var finalSegment = route.MotionPath is not { Count: > 1 } || route.MotionTargetIsDestination;
        if (finalSegment)
        {
            // At cursor count-2 Mac selects 9/10 by distance only for non-zero person data 9 (0x10350694–
            // 0x10350708). With data 9 = 0 it reaches state 9 through the state-3 match gate instead: follow
            // state 1 with the root and heading on +0x1bc/+0x1c4 stores +0x188=9 (0x1034F4BC–0x1034F4F4).
            // Native tracks +0x1bc only for turns, so this distance test also stands in for that redispatch.
            if (person.AnimatorRouteTransitionState == 0 && remainingDistance >= (running ? 4.5f : 3f))
                person.AnimatorControlState = running ? 10 : 9;
        }
        else if (person.AnimationPlayback is not { Interruptible: false })
        {
            // Earlier segments select 9/10 without a distance test unless the primary practice's +0x68
            // interruptible flag is clear (0x10350A00–0x10350A30).
            person.AnimatorControlState = running ? 10 : 9;
        }

        var entersGait = person.AnimatorLocomotionFadeGate != 0 || person.AnimatorGaitEntryState != 0 ||
                         currentSkill is 20 or 21;
        if (!entersGait && !person.AnimatorPoolTile)
        {
            // StartVitaBoy caches +0x1f0 = StdAnimDist(0x13) * 16/3 and +0x1fc = StdAnimDist(0x16) * 16/3;
            // the entry gate compares the squared step against them inclusively (SAnimator.s
            // 0x10358BF8–0x10358C94, 0x10350E28–0x10350E7C).
            var thresholdSkill = RouteExecutor.ResolveWalkAnimation(this, person, running ? 22 : 19);
            var threshold = (thresholdSkill?.StandardDistance ?? 0f) * (16f / 3f);
            // f25 is the squared offset from the root to this Tick's lookahead sample (SAnimator.s 0x10350B98),
            // not the stored +0x28c/+0x290 step; that step only feeds f25 on the branch that skips this gate
            // (0x10350798–0x103507AC).
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
                RouteExecutor.ShowWalkSkill(person, startClip, 0f, startClip.StandardRootMotionSamples.Count);
            }
            return;
        }

        person.AnimatorGaitEntryState = 1;
        // SAnimator does not choose control state 9/10 here. The earlier state-3 path selects it only
        // when +0x280 is clear and the remaining route distance reaches its source threshold
        // (0x103506D0–0x10350708). This branch derives StartSkill's ID from the state already selected.
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
        RouteExecutor.ShowWalkSkill(person, clip, 0f, samples.Count);
    }

    private bool AnimatorOutOfBounds(XPerson person)
    {
        if (!LotRoomMaps.TryGetValue(person.Position.Height, out var map)) return false;
        // Mac CTilePt conversion uses arithmetic FTile-to-tile shifts; OutOfBounds reads signed tile bytes
        // and rejects coordinates below 1 or at/above dimension - 1.
        var tileX = person.Position.X >> 4;
        var tileY = person.Position.Y >> 4;
        return tileX < 1 || tileY < 1 || tileX >= map.Width - 1 || tileY >= map.Height - 1;
    }

    /// <summary>Models SAnimator::Tick's near-waypoint Practice::Fade branch at +0x1b8. Its strides come from
    /// StartVitaBoy against the person's selected skeleton; a missing source skeleton/marker leaves the branch inactive.</summary>
    private void TickRouteLocomotionFade(XPerson person)
    {
        if (person.NativeRoute is not { } route || route.MotionTargetX == int.MinValue ||
            route.MotionTargetY == int.MinValue || person.AnimationPlayback is not { RouteDriven: true } playback)
        {
            person.AnimatorLocomotionFadeGate = 0;
            return;
        }

        // Tick reaches the terminal fade branch only at cursor TileList.count - 2. The final
        // TileList entry supplies facing, rather than another walking segment (0x10350588–0x10350630).
        // Native MotionPath stores only positional points, whose final point has IsDestination set.
        if (route.MotionPath is { Count: > 1 } && !route.MotionTargetIsDestination) return;
        if (person.AnimatorControlState is not (3 or 9 or 10)) return;
        // Non-zero person data 9 takes the route-preference arrival instead of this fade
        // (SAnimator.s 0x10350694–0x10350698; RouteExecutor.IsRoutePreferenceArrival).
        if (person[ObjectData.RoutePreference] != 0) return;

        if (route.LocomotionFadeEndMils > 0)
        {
            var expiryRootX = person.AnimatorRootX ?? route.MotionX;
            var expiryRootY = person.AnimatorRootY ?? route.MotionY;
            var distanceToTarget = float.IsFinite(expiryRootX) && float.IsFinite(expiryRootY)
                ? MathF.Sqrt(MathF.Pow(route.MotionTargetX - expiryRootX, 2) + MathF.Pow(route.MotionTargetY - expiryRootY, 2))
                : float.PositiveInfinity;
            // Mac SAnimator::Tick keeps the active fade while the Sim is within two units of its target
            // and the deadline has not arrived. WalkStyle 1 and reaching two units both bypass that wait.
            if (person[ObjectData.WalkStyle] != 1 && distanceToTarget < 2f &&
                person.AnimatorMilliseconds < route.LocomotionFadeEndMils) return;

            route = route with { LocomotionFadeEndMils = -1 };
            person.NativeRoute = route;
            person.AnimatorLocomotionFadeGate = 0;
            person.AnimatorPoolTransitionState = 0;
        }

        if (route.LocomotionFadeEndMils < 0)
        {
            // Tick consumes the negative +0x1b8 sentinel in the same pass: it starts the follow
            // transition (+0x188=3, +0x180=-1), then clears +0x1b8/+0x284/+0x288. FollowOneStep
            // reports its source-defined busy result (2) while +0x180 is -1.
            // The source commits the terminal position (+0x80/+0x84 and +0x1bc/+0x1c0) before redispatching
            // state 3 (0x103509A0–0x103509CC), rather than treating the current root as its own target.
            person.AnimatorRootX = route.MotionTargetX;
            person.AnimatorRootY = route.MotionTargetY;
            // Mac writes the terminal heading f3 (atan2 toward the look point FUN_0045c4fa appends after the
            // path end, 0045c4fa.c:70-132 / FUN_0045f7c5) only to the transition-target heading +0x1c4
            // (0x103509C4); the root heading +0x88 is left alone. The redispatched state-3 gate then turns the
            // Sim toward it with an animated state-4 body turn (or its small-error snap) before follow state
            // -1 becomes 2 (0x1034F438–0x1034F69C). Native models +0x1c4 as RouteExecutor.ArrivalHeading for a
            // destination target in TryBeginRouteTurnRoot and TickRouteFollowTransition. Writing +0x88 here made Sims snap
            // to the slot facing on arrival. Look Towards keeps its previous handling.
            if (route.LookTowardsFollow)
                person.AnimatorRootDirection = route.ArrivalFacing;
            person.AnimatorControlState = 3;
            // The +0x104 virtual call is StopCurAnim. It invokes StopAnimation on the primary
            // practice, clears the current-skill index (+0x98), and the process-global fade switch
            // is zero-initialized and has no writers in the Mac image, so StopAnimation stops it.
            person.AnimationPlayback = null;
            person.AnimatorCurrentSkill = -1;
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
        // StartVitaBoy caches marker times and strides from standard skill 0x14, not from the
        // current practice (Windows FUN_00588351; Mac SAnimator.s 0x10358D84–0x10359068).
        var markerClip = RouteExecutor.ResolveWalkAnimation(this, person, 0x14);
        if (markerClip is null || clip.FrameCount <= 0 || clip.DurationMilliseconds <= 0f ||
            markerClip.LocomotionMarkerOne is not { } markerOne || markerOne.Bone.Length == 0 ||
            markerClip.LocomotionMarkerTwo is not { } markerTwo || markerTwo.Bone.Length == 0 ||
            person.AnimatorWalkingScale <= 0f || LocomotionStrideResolver?.Invoke(person, markerClip.Name) is not { } strides)
            return;

        var phase = playback.Frame / clip.FrameCount;
        var stride = Pose.LocomotionStrideAtPhase(phase, markerOne.NormalizedTime, markerTwo.NormalizedTime,
            strides.MarkerOneStride, strides.MarkerTwoStride);
        if (!person.AnimatorPoolTile && person[ObjectData.WalkStyle] == 1)
            stride *= person.AnimatorWalkingScale;

        var rootX = person.AnimatorRootX ?? route.MotionX;
        var rootY = person.AnimatorRootY ?? route.MotionY;
        if (!float.IsFinite(rootX) || !float.IsFinite(rootY)) return;
        var distance = MathF.Sqrt(MathF.Pow(route.MotionTargetX - rootX, 2) + MathF.Pow(route.MotionTargetY - rootY, 2));
        var fade = Pose.LocomotionFade(distance, stride, phase, markerOne.NormalizedTime, markerTwo.NormalizedTime,
            clip.DurationMilliseconds, person.AnimatorWalkingScale);
        if (fade is not { } schedule) return;

        // Mac's near-waypoint branch has entered the +0x280=1 route-transition state before it
        // schedules +0x1b8 and sets +0x288=1 (SAnimator.s 0x10350824–0x10350878, 0x10350F4C–0x10350F54).
        person.AnimatorRouteTransitionState = 1;
        person.AnimatorPoolTransitionState = 1;
        var endMils = unchecked(person.AnimatorMilliseconds + schedule.DurationMilliseconds);
        playback.FadePracticeWeight(person.AnimatorMilliseconds, endMils, playback.PracticeWeight, schedule.TargetWeight);
        person.NativeRoute = route with { LocomotionFadeEndMils = endMils };
        person.AnimatorLocomotionFadeGate = 1;
    }

    /// <summary>Completes the transient follow state -1 produced when the route locomotion fade ends.
    /// Mac Tick commits the root to its transition target, stops the primary practice, then redispatches
    /// state 3; its root/heading match gate advances follow state -1 to 2 (SAnimator.s 0x1034F4BC–0x1034F4E8,
    /// 0x103509A0–0x103509F0). The native route snapshot is the modeled transition target.</summary>
    private static void TickRouteFollowTransition(XPerson person)
    {
        if (person.NativeRoute is { AnimatorFollowState: -1, TransitionTargetX: { } transitionX,
                TransitionTargetY: { } transitionY } arrivalRoute)
        {
            // Route-preference arrival keeps its practice; the state-3 gate matches the root against
            // +0x1bc/+0x1c0 and advances follow state -1 to 2 (0x1034F438–0x1034F4F8).
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

        // The Mac match gate also compares +0x88 against +0x1c4 within the same 1e-6 constant
        // (0x1034F438–0x1034F4A4). For both routed destinations and Look Towards the fade end stores
        // the terminal look-point heading there (RouteExecutor.ArrivalHeading); wait for the state-3 turn to reach it.
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

    /// <summary>Consumes Mac SAnimator::EndFollow's deferred +0x27c request from Tick. Tick only checks it
    /// while +0x180 is 1, and waits when the active practice's +0x68 interruptible flag is clear.</summary>
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
            // Mac SAnimator::Tick sets +0x280=3 and then branches on +0x288. The zero branch sets +0x188=3
            // and +0x180=3. The nonzero branch sets +0x188=7, selects standard skill 0x13 and starts it on the
            // primary practice; Windows FUN_0058dc76 corroborates the same state/skill selection.
            person.AnimatorRouteTransitionState = 3; // SAnimator::Tick writes +0x280=3 for deferred pool EndFollow.
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

        // The nonzero pool-transition branch leaves +0x180 at 1 while control state 7 runs skill 0x13.
        // State 7's completion path advances it to 3; FollowOneStep must keep reporting busy meanwhile.
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

    /// <summary>Tick's nonzero +0x288 pool branch (SAnimator.s 0x103511EC–0x10351214): +0x188=7, current skill
    /// 0x13, then StartSkill(primary, 0x13, 1). Deferred pool EndFollow and the pool route-preference arrival share it.</summary>
    internal void StartPoolSwimExitPractice(XPerson person)
    {
        person.AnimatorControlState = 7;
        var swimExit = RouteExecutor.ResolveWalkAnimation(this, person, 0x13);
        person.AnimatorCurrentSkill = 0x13;
        if (swimExit is not null)
            // StartSkill negates the practice scale for a nonzero direction argument, so this skill plays backward.
            person.AnimationPlayback = new AvatarAnimationPlayback(swimExit, backwards: true, loop: false,
                startedAtMils: person.AnimatorMilliseconds)
            {
                Interruptible = true,
                PoolSwimExit = true,
            };
    }

    /// <summary>Completes deferred pool EndFollow on state 7's next Tick, while the skill-0x13 practice continues
    /// until its own one-shot phase ends (Mac SAnimator.s 0x1034FFFC–0x103502A8, 0x103510A0–0x10351240).</summary>
    private static void TickPoolSwimExit(XPerson person)
    {
        if (person.NativeRoute is not { PoolSwimExitPending: true } route) return;
        if (person.AnimatorControlState == 7 &&
            person.AnimationPlayback is null or { EndReached: true })
        {
            // With no active primary Practice, state 7 substitutes phase 1 and takes its terminal branch.
            // Restore the root snapshot and direction that Mac copied to +0x1d4/+0x1d8/+0x1dc when it
            // entered this deferred pool-exit path (SAnimator.s 0x10350000–0x103502A8).
            var rootX = route.PoolSwimExitRootX ?? person.AnimatorRootX ?? route.MotionX;
            var rootY = route.PoolSwimExitRootY ?? person.AnimatorRootY ?? route.MotionY;
            var direction = route.PoolSwimExitDirection ?? person.AnimatorRootDirection ?? person[ObjectData.Direction];
            person.AnimatorRootX = rootX;
            person.AnimatorRootY = rootY;
            person.AnimatorRootDirection = direction;
            person[ObjectData.Direction] = SimsEngine.Character.AvatarDirection.ToObjectDirection(direction);
            person.AnimatorStateTailField198 = 1;
            person.AnimationPlayback = null;
            person.AnimatorCurrentSkill = -1;
            person.AnimatorControlState = 3;
            person.NativeRoute = route with
            {
                MotionX = rootX,
                MotionY = rootY,
                // State 7's terminal branch maps +0x280 to +0x180: 1 → −1, 2 → 2, 3 → 3 (0x1035025C–0x103502A4).
                // Deferred EndFollow entered with +0x280=3; the pool route-preference arrival enters with 1.
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
        // The remaining shortcuts consume the still-pending EndFollow (+0x27c), which the arrival does not set.
        if (person.AnimatorRouteTransitionState != 3) return;
        if (person.AnimationPlayback is { PoolSwimExit: true, EndReached: true })
        {
            person.AnimationPlayback = null;
            person.AnimatorCurrentSkill = -1;
            person.AnimatorControlState = 3;
            person.NativeRoute = route with
            {
                AnimatorFollowState = 3,
                PoolSwimExitPending = false,
            };
            return;
        }
        if (person.AnimatorControlState != 7) return;

        // State 7 clears +0x288 before the Tick tail reads it. The still-pending +0x27c therefore takes
        // the zero-transition branch, which restores control state 3 and writes +0x180=3 immediately.
        person.AnimatorControlState = 3;
        person.NativeRoute = route with
        {
            AnimatorFollowState = 3,
            PoolSwimExitPending = person.AnimationPlayback is { PoolSwimExit: true },
        };
    }

    /// <summary>Matches SAnimator::GetWalkingScale and Tick's +0x220 easing. FollowOneStep runs before this tick,
    /// so route movement consumes the previous scale.</summary>
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
        const float step = 0.2f; // lbl_105C1F94.
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

    /// <summary>Matches Mac SAnimator::UpdateCarryPractice: infer automatic carry practice from the first
    /// cXPerson object-slot record, unless a reach is active or an explicit mode-2 carry animation owns the channel.</summary>
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
            // Mac UpdateCarryPractice obtains standard skill 0x26 and starts it on the carry practice channel.
            var clip = RouteExecutor.ResolveWalkAnimation(this, person, 0x26);
            if (clip is not null) person.CarryAnimationPlayback = new AvatarAnimationPlayback(clip, backwards: false,
                loop: true, startedAtMils: person.AnimatorMilliseconds);
        }
        person.CarryPracticeCachedState = desiredState;
    }

    /// <summary>Implements the background-practice portion of cXPerson::Simulate's SAnimator::Tick call.
    /// SAnimator::Tick dispatches UpdateBackgroundPractice after its carry-practice update. PersonData[0] selects
    /// standard animation 1, 2, or 3 for values 1, 2, or -1. SAnimator::GetSkill delegates to GetStdSkill, so use
    /// the same selector, pool, age, and gender lookup as route locomotion. SAnimator's constructor initializes its
    /// cached person state to -1.</summary>
    internal void UpdateBackgroundPractice(XPerson person)
    {
        var state = person[PersonData.Sitting];
        var changed = state != person.BackgroundPracticePersonState;
        if (changed)
        {
            // UpdateBackgroundPractice stops the background practice when +0x58c changes.
            person.BackgroundAnimationPlayback = null;
        }
        else if (person.BackgroundAnimationPlayback is not null && !UpdateBackgroundPracticeForPersonState(person))
        {
            return;
        }

        // Mac reads the person selector's OBJD word at byte offset 0xCC here and skips practice updates when
        // VitaBoy Flags contains bit 2. The cache write follows this gate in the original routine.
        if ((unchecked((ushort)person.Definition[(int)ObjdField.VitaBoyFlags]) & 0x0004) != 0) return;

        person.BackgroundPracticePersonState = state;

        // SAnimator::UpdateBackgroundPractice (00590434.c:16-27) maps Sitting 0 => 3 (standing idle),
        // 1 => 1 (chair sitting idle), and 2 => 2 (floor sitting idle).
        var standardIndex = state switch { 0 => 3, 1 => 1, 2 => 2, _ => -1 };
        if (standardIndex < 0) return;
        var clip = RouteExecutor.ResolveWalkAnimation(this, person, standardIndex);
        if (clip is null) return;
        person.BackgroundAnimationPlayback = new AvatarAnimationPlayback(clip, backwards: false, loop: true,
            startedAtMils: person.AnimatorMilliseconds);

        static bool UpdateBackgroundPracticeForPersonState(XPerson sim)
        {
            // Mac UpdateBackgroundPractice applies this expression to person +0x60e, set from Body Strings 13 by
            // ResetSuits. The authored human values 0 and 1 preserve an active practice; pet values 2–5 enter the
            // source's refresh path.
            var bodyStringMode = sim.AnimatorBodyStringMode;
            var value = bodyStringMode ^ 1;
            var difference = unchecked((value >> 1) - (value & bodyStringMode));
            return (unchecked((uint)difference) & 0x4000_0000u) != 0;
        }
    }

    /// <summary>Starts an explicit-position helper route alongside the ordinary clock callbacks.
    /// User Go Here commands enqueue Destination.iff's action on the person's normal stack.</summary>
    public bool StartDirectPositionRoute(XPerson person, ObjectPosition destination)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (!ReferenceEquals(person, ObjectById(person.Id)))
            throw new ArgumentException("The Sim is not in this world.", nameof(person));
        // Replace a prior helper request; BHAV-owned and portal-nested routes retain their own
        // cancellation lifecycle and must not be detached from their execution stack here.
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

    /// <summary>Resumes explicit-position helper routes. User commands run on the ordinary BHAV stack.</summary>
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

    /// <summary>The ordinary queued path of Sims.exe FUN_0045d377. Assign the source sequence number, insert in
    /// descending priority order, and remove the lower-priority suffix when the new action belongs ahead of it. The
    /// explicit replace-queue and immediate-run branches are handled separately by the caller.</summary>
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

    public bool IsImmediateMenuInteraction(ActionQueueSlot choice) =>
        choice.Owner?.TypeRecord?.Interactions?.Interactions
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

    /// <summary>FUN_0045d377's ring insertion for an already validated action. Its record flag 0x2 branch puts the new
    /// action first and copies the existing queue after it; the ordinary branch inserts by descending priority.</summary>
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

        // FUN_0045d377 (0045d377.c:68-86): in the non-continuation branch, record flag 0x40 only inherits
        // the action string when an immediate action is currently active on this person.
        if ((action.Flags & 0x40) != 0 && ActiveImmediateAction is not null && ReferenceEquals(ActiveImmediatePerson, person))
        {
            InheritActionRecordText(action, ActiveImmediateAction);
        }

        person.ActionQueue[person.ActionQueueEnd & 7] = action;
        person.ActionQueueEnd = unchecked(person.ActionQueueEnd + 1);
        return true;
    }

    private static void InheritActionRecordText(ActionQueueSlot destination, ActionQueueSlot? source)
    {
        if (source is null) return;
        var text = source.ActionText;
        if (string.IsNullOrEmpty(text))
            text = source.Owner?.TypeRecord?.Interactions?.Interactions
                .FirstOrDefault(entry => entry.TtaIndex == source.RawDword10)?.Name;
        source.Flags &= ~0x80;
        if (!string.IsNullOrEmpty(text))
        {
            destination.ActionText = text;
            destination.Flags |= 0x80;
        }
    }

    /// <summary>Constructs the found-action record used by Sims.exe FUN_0041f010(person, target, key, priority).
    /// Gosub Found Action (FUN_0045a9c5) does not queue it: it passes the stack record to FUN_0045e839, the accepted-start
    /// transition, so the person's Priority becomes the record's priority and the queue is untouched. The target's TTAB row is resolved by TtaIndex;
    /// FUN_0041f101 leaves the action payload empty when that lookup misses, while the target and key remain in
    /// the record.</summary>
    public void StartFoundAction(XPerson person, XObject target, int key, int priority)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(target);
        var interaction = target.TypeRecord?.Interactions?.Interactions.FirstOrDefault(entry => entry.TtaIndex == key);
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

    /// <summary>FUN_0045d377's record +0x40 bit 1 path: prepend the new interaction and retain the first seven queued
    /// actions in their existing order. The eight-entry ring cannot retain an eighth prior action.</summary>
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

    /// <summary>Removes queued records whose +8 or +0xc object pointer matches <paramref name="subject"/>, as
    /// cXPerson::FUN_0045e8fd does when an object reference becomes invalid. The original rebuilds the ring through a
    /// temporary eight-entry queue, preserving survivor order and notifying for each removed record with a non-null +8.</summary>
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

    /// <summary>FUN_0045d764 removes queued actions by rebuilding the eight-slot ring. A matching active sequence
    /// instead sets the +0xa89 interrupt bit and clears active Priority; when FUN_0045d92c returns false, it also
    /// dispatches TTAB entry 0x21 on the person at priority 50.</summary>
    public bool CancelInteractionAction(XPerson person, int sequence)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (sequence == person.GenericSimsCall02GateWordA84)
        {
            if (person.CurrentActionRecord?.Owner is null) return false;
            // Source interactions with an owned native lifecycle may opt into an authored exit check. In that case
            // Cancel Interaction requests the next source track-boundary exit and leaves the active route/animation
            // interruptible; lane waiters and all ordinary interactions retain the immediate engine cancel below.
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

    /// <summary>FUN_0045e839's accepted-start transition. The backup's +0x20 action bit is assigned from the person's
    /// byte at +0x20; when the incoming action has an owner and person +0xa84 is zero, FUN_0041f15a assigns a new sequence.</summary>
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

    /// <summary>Requests a source Exit Check Interrupt at the next authored track boundary, tied to one active action UID.</summary>
    public bool RequestInteractionExit(XPerson person, int actionSequence)
    {
        if (person.CurrentActionRecord is not { Owner: not null } current || current.Sequence != actionSequence) return false;
        return InteractionLifecycle.TryRequestGracefulExit(person, actionSequence);
    }

    /// <summary>Ports the current/previous action-record branches of cXPerson::FUN_0045e8fd. Owner matches take
    /// precedence; a current-owner match runs the same empty-record transition as FUN_0045e839.</summary>
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

        // +0xa54 is the same live pointer GenericSimsCall 0x02 writes. The action record may not be hydrated yet.
        if (ReferenceEquals(person.GenericSimsCall02ObjectA54, subject))
            person.SetCurrentActionSecondaryReference(null);
    }

    /// <summary>FUN_0045f8e7: copy the current eight-slot ring entry, then advance its current index.</summary>
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

    /// <summary>Adds <paramref name="item"/> to the lot. An object with the same id replaces the one already there, in
    /// its walk position.</summary>
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
            // FUN_0043779b lazily prepares the owner's type-record extension, allocates cXObject +0x120,
            // initializes that helper with the owner, then sets the owner byte at +0x180.
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
            // Preserve explicit entries on hand-built test objects as aliases into the shared manager table.
            // Production saved-lot instances have no prepopulated +0xdc vector.
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

    /// <summary>Removes the object with <paramref name="id"/> from the lot, from both the lookup and the walk order
    /// (Create Object's failed placement takes the object back out). False when no such object is on the lot.</summary>
    public bool Remove(short id)
    {
        if (!Objects.Remove(id, out var item)) return false;
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
        ClockManager.Remove(item);
        if (tableSlot > 0 && tableSlot <= _objectTableEntries.Count && ReferenceEquals(_objectTableEntries[tableSlot - 1], item))
            _objectTableEntries[tableSlot - 1] = null;
        SavedExecutionStacks.Remove(id);
        return true;
    }

    /// <summary>Models ObjectModule::KillObject followed by KillOutOfWorldObject, including its post-removal 0x88 dispatch.</summary>
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

    /// <summary>Optional UIText.iff override for localized dialog strings (FUN_00435ff3); falls back to <c>ObjectRegistry.UiText</c>.</summary>
    public SimsEngine.Formats.IffFile? UiTextFile { get; set; }

    /// <summary>FUN_00435ff3: reads one zero-based entry from UIText.iff STR# <paramref name="tableId"/> in <see cref="Language"/>.</summary>
    public string? UiTextString(ushort tableId, int index)
    {
        if (UiTextFile?.Find(SimsEngine.Formats.ResourceType.Str, tableId) is { } chunk)
        {
            var strings = SimsEngine.Formats.StringTable.Read(UiTextFile, chunk).ForLanguage(Language);
            if (index >= 0 && index < strings.Count && strings[index].Length > 0) return strings[index];
        }
        return ObjectRegistry?.UiTextString(tableId, index, Language);
    }

    /// <summary>The lot table words the Dialog primitive reads and writes (+0xea to +0xfc; see <see cref="DialogWords"/>).</summary>
    public DialogWords Dialog { get; } = new();

    /// <summary>The lot-manager dialog records at +0x64, in insertion order.</summary>
    public List<SimulationDialogRecord> DialogRecords { get; } = [];
    private long _dialogSequence;

    public SimulationDialogRecord? DialogRecord(XObject owner) =>
        DialogRecords.FirstOrDefault(record => ReferenceEquals(record.Owner, owner));

    public int? DialogRecordState(XObject owner) => DialogRecord(owner)?.State;

    public bool HasVisibleBlockingDialog => DialogRecords.Any(record => record.Visible && record.Blocking && record.State < 2);
    public bool HasVisibleDialog => DialogRecords.Any(record => record.Visible);

    public SimulationDialogRecord? CreateMessageDialog(XObject owner, XObject? icon, string title, string message,
        string? yes, string? no, string? cancel, byte kind = 0, bool blocking = true, bool awaitsResponse = true)
    {
        // Sims.exe's dialog manager has specialized records beyond the five generic
        // STR# 301 dialogs. The native client consumes those records as well (travel,
        // wardrobe/pet choice, phone book, and transformation prompts).
        var record = new SimulationDialogRecord($"native-dialog-{++_dialogSequence}", owner, icon, kind, title, message,
            yes, no, cancel, blocking, awaitsResponse)
        {
            Visible = blocking ? !HasVisibleBlockingDialog : !HasVisibleDialog,
            State = !awaitsResponse && !blocking ? 1 : 0,
        };
        DialogRecords.Add(record);
        return record;
    }

    /// <summary>Creates the source's style-0 Spellbook or style-1 Cookbook record (FUN_00451592/FUN_00510867) for the
    /// headless native runtime. The SDL consumer resolves the matching localized title format and book contents.</summary>
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
            // FUN_005109fe selects mode 0 for children and mode 1 for age 0 / ages above 17.
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
        return record;
    }

    /// <summary>Runs Sims.exe's Global.iff BHAV 496 (`Magic - Has Made Spell/Recipe?`) for the Makin' Magic
    /// resource ids listed in MagicMasterSpells.iff STR# 15. BHAV 496 finds the lot's 0xB6C90029 Magic Master Spells
    /// object and calls its named BHAV 4101, whose result is the made/unknown detail state. The UI passes [current
    /// selector, row id]: adults keep +0x140 at zero, while children store the previously selected row id there.
    /// The wrapper writes its arguments into the actor's first two temporaries, so preserve the live actor state.</summary>
    private IReadOnlyList<SimulationBookAvailability> QuerySpellBookAvailability(XPerson owner, byte mode)
    {
        if (owner.TypeRecord?.BehaviorScope is not Content.BehaviorScope scope) return [];
        var savedTemporaries = owner.Temporaries.ToArray();
        try
        {
            // Use the registry's exact STR# 15 numeric ids, preserving its order, gaps and placeholder entries.
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
                // FUN_00451592 case 6 returns the response through lot words +0xf4/+0xf6.
                Dialog[0xf4] = code == 1 ? (short)-2 : (short)0;
                Dialog[0xf6] = (short)selection;
            }
            else
            {
                // Case 9 returns the selected index through +0x100 and uses +0x108 == -2
                // to distinguish a choice from its cancel path.
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
            // FUN_00451592 consumes the selected lot from these lot-table words for
            // vacation, downtown, studio town, unleashed, and magic town respectively.
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
    }

    public void CompleteDialogRecord(SimulationDialogRecord record)
    {
        DialogRecords.Remove(record);
        ShowNextDialogRecord();
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

    /// <summary>The lot table's tutorial sim (+0xa8, an object pointer that FUN_00444bf6 reads and FUN_00444c08 writes; inferred
    /// name). Null when no sim holds the tutorial.</summary>
    public XObject? TutorialOwner { get; set; }

    /// <summary>The state of each frame whose primitive waits across ticks (the dword at the frame's +8 that Special Effect
    /// keeps; inferred: the dword is this state for that primitive). A primitive removes its entry when it finishes, as
    /// Sims.exe clears the dword when the frame branches.</summary>
    public Dictionary<StackFrame, int> PrimitiveTicks { get; } = [];
}


/// <summary>One SimAntics error contained during a clock-manager tick (see <see cref="SimulationWorld.ThreadErrors"/>).</summary>
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

/// <summary>A TreeBreakEncountered contained at an interaction-check boundary.</summary>
public readonly record struct InteractionGuardError(short PersonId, short ObjectId, int InteractionId,
    int GuardTree, SimulationError Error, string Message);
