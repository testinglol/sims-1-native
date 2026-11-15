using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using SimsEngine.Formats;

/// <summary>
/// Compiles a JSON object description into a pack holding new IFF objects. Each object starts from an installed original
/// object (its sprites, draw groups, slots and behaviors) and gets its own GUIDs, catalog text, price, definition fields,
/// SimAntics behaviors and interactions. Template chunks the document does not touch are copied byte for byte; changed
/// definition fields are patched in place so every other OBJD field keeps its original value.
/// </summary>
internal static class ObjectAuthor
{
    internal sealed record Document
    {
        public int SchemaVersion { get; init; } = 1;
        public required string PackID { get; init; }
        public string? Name { get; init; }
        public string Version { get; init; } = "0.1.0";
        public required ObjectSpec[] Objects { get; init; }
    }

    internal sealed record ObjectSpec
    {
        /// <summary>The IFF file name inside the pack, without extension.</summary>
        public required string File { get; init; }
        /// <summary>An original object file, such as "flamingo.iff", looked up in GameData/Objects.</summary>
        public required string Template { get; init; }
        /// <summary>The GUID of the original object to start from when the template file holds several.</summary>
        public JsonElement? TemplateObject { get; init; }
        /// <summary>The GUID of the lead definition; allocate it with the pack tool's --reserve-object-guid.</summary>
        public required JsonElement Guid { get; init; }
        /// <summary>GUIDs for the template's other definitions (multi-tile parts), in chunk order.</summary>
        public JsonElement[] TileGuids { get; init; } = [];
        /// <summary>OBJD fields for non-lead tiles in tileGuids order.</summary>
        public Dictionary<string, int>[] TileFields { get; init; } = [];
        /// <summary>Explicit signed tile offsets for a variable-size multi-tile object. Each entry clones one template tile part.</summary>
        public TilePart[]? TileLayout { get; init; }
        public string? CatalogName { get; init; }
        public string? Description { get; init; }
        /// <summary>Transparent PNG for the catalog BMP_, relative to the document.</summary>
        public string? CatalogIcon { get; init; }
        public int? Price { get; init; }
        /// <summary>Definition fields by their OBJD name, such as RatingFun or LotCategories.</summary>
        public Dictionary<string, int> Fields { get; init; } = [];
        public bool ReplaceInteractions { get; init; }
        /// <summary>Shifts the colors of the template's sprite palettes and catalog icon.</summary>
        public Recolor? Recolor { get; init; }
        /// <summary>Catalog and interaction text for other languages, keyed by language name such as "French".</summary>
        public Dictionary<string, Translation> Translations { get; init; } = [];
        /// <summary>Repainted sprite frames, as exported by --export-object-sprites.</summary>
        public SpriteImage[] Sprites { get; init; } = [];
        /// <summary>Complete four-direction, three-zoom graphics, independent of a template's mirrored frames.</summary>
        public GraphicSet? Graphics { get; init; }
        /// <summary>Clips added to or explicitly replacing entries in the object's adult and child animation tables.</summary>
        public AnimationEntry[] Animations { get; init; } = [];
        /// <summary>Renames existing pie-menu entries without replacing their source action/test routines.</summary>
        public InteractionRename[] RenameInteractions { get; init; } = [];
        /// <summary>Optional replacement routing points from the group's (0,0) tile, in SLOT subtiles (16 per tile).</summary>
        public RoutingSlot[]? RoutingSlots { get; init; }
        public Behavior[] Behaviors { get; init; } = [];
        /// <summary>Play an added object animation on one successful branch of an existing template routine.</summary>
        public AnimationHook[] AnimationHooks { get; init; } = [];
        /// <summary>Dispatch Animate Sim XEVTs stored in a local, then return to the Animate Sim node.</summary>
        public AnimationEventHook[] AnimationEventHooks { get; init; } = [];
        public Interaction[] Interactions { get; init; } = [];
        /// <summary>Versioned native BHAV extension capabilities emitted in the private NLCF resource.</summary>
        public string[] NativeCapabilities { get; init; } = [];
        /// <summary>Routine IDs whose calls in the template's routines are skipped, as if each call returned true.</summary>
        public int[] DropCalls { get; init; } = [];
        /// <summary>A behavior from <see cref="Behaviors"/> that every definition runs on creation, before its own init.</summary>
        public string? InitBehavior { get; init; }
    }

    internal sealed record RoutingSlot
    {
        public required float[] Offset { get; init; }
        /// <summary>Optional source SLOT route index. Sparse indices are preserved with unreachable filler entries.</summary>
        public int? Index { get; init; }
        /// <summary>Explicitly permits this stand point inside a multi-tile footprint, for source-backed walkable surfaces.</summary>
        public bool AllowInsideFootprint { get; init; }
        public int Facing { get; init; } = -2;
        public int Resolution { get; init; } = 16;
        internal bool IsGeneratedPlaceholder { get; init; }
    }

    internal sealed record TilePart
    {
        public required JsonElement Guid { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public Dictionary<string, int> Fields { get; init; } = [];
    }

    internal sealed record AnimationEntry
    {
        /// <summary>The name behaviors use for it in an animate operand's animationID.</summary>
        public required string Name { get; init; }
        /// <summary>The clip an adult plays, without ".anim", such as "a2o-flamingo-kick".</summary>
        public required string Adult { get; init; }
        /// <summary>The clip a child plays; defaults to the adult's.</summary>
        public string? Child { get; init; }
        /// <summary>Replace this existing animation-table index instead of appending.</summary>
        public int? Index { get; init; }
    }

    internal sealed record AnimationHook
    {
        /// <summary>Existing private BHAV ID to extend.</summary>
        public int Routine { get; init; }
        /// <summary>Instruction whose successful branch reaches the appended animation.</summary>
        public int Instruction { get; init; }
        public string Branch { get; init; } = "true";
        public required string Animation { get; init; }
    }

    internal sealed record AnimationEventHook
    {
        /// <summary>Existing private BHAV that contains the Animate Sim node.</summary>
        public int Routine { get; init; }
        /// <summary>Alternative to Routine for a BHAV authored in this document.</summary>
        public string? RoutineName { get; init; }
        /// <summary>Animate Sim instruction whose false edge receives stored XEVTs.</summary>
        public int Instruction { get; init; }
        /// <summary>Local index selected by the Animate Sim operand's StoreEventInLocal flag.</summary>
        public int Local { get; init; }
        /// <summary>Event value and an authored private behavior to call for that event.</summary>
        public required AnimationEventCase[] Events { get; init; }
    }

    internal sealed record AnimationEventCase
    {
        public int Value { get; init; }
        public required string Behavior { get; init; }
    }

    internal sealed record InteractionRename
    {
        public required string From { get; init; }
        public required string To { get; init; }
    }

    internal sealed record Behavior
    {
        public required string Name { get; init; }
        /// <summary>Existing private routine to replace. Preserve its argument/local capacity and instruction count for saved frames.</summary>
        public int? ReplaceRoutine { get; init; }
        public int Args { get; init; }
        public int Locals { get; init; }
        public required Instruction[] Instructions { get; init; }
    }

    internal sealed record Instruction
    {
        public string? Label { get; init; }
        /// <summary>A primitive by its engine name, such as "expression" or "animate sim".</summary>
        public string? Primitive { get; init; }
        /// <summary>A validated native interaction-lifecycle operation (requires the matching private NLCF capability).</summary>
        public string? NativeOperation { get; init; }
        /// <summary>A behavior to call: one of this object's behaviors by name, or a BHAV ID.</summary>
        public JsonElement? Call { get; init; }
        public short[]? Args { get; init; }
        /// <summary>A raw opcode with <see cref="OperandHex"/>, for primitives without an operand model.</summary>
        public int? Opcode { get; init; }
        public string? OperandHex { get; init; }
        public Dictionary<string, JsonElement>? Operand { get; init; }
        public JsonElement? True { get; init; }
        public JsonElement? False { get; init; }
    }

    internal sealed record Interaction
    {
        public required string Name { get; init; }
        public required JsonElement Action { get; init; }
        public JsonElement? Test { get; init; }
        public int AutonomyThreshold { get; init; }
        public Dictionary<string, Advertisement> Advertisements { get; init; } = [];
        public bool AllowVisitors { get; init; } = true;
        public bool AllowChildren { get; init; } = true;
        public bool AllowAdults { get; init; } = true;
        public bool Joinable { get; init; }
        public bool RunImmediately { get; init; }
        public bool Debug { get; init; }
    }

    internal sealed record Recolor
    {
        /// <summary>Degrees added to the hue.</summary>
        public double Hue { get; init; }
        public double Saturation { get; init; } = 1;
        public double Lightness { get; init; } = 1;
        /// <summary>Only colors whose hue lies in this range, in degrees; the range may wrap through 0.</summary>
        public double[]? HueRange { get; init; }
        /// <summary>Colors with less saturation, such as grays and wood, keep their color.</summary>
        public double MinimumSaturation { get; init; }
    }

    internal sealed record Translation
    {
        public string? CatalogName { get; init; }
        public string? Description { get; init; }
        /// <summary>Pie-menu text keyed by the interaction's name in the document.</summary>
        public Dictionary<string, string> Interactions { get; init; } = [];
    }

    internal sealed record SpriteImage
    {
        /// <summary>The SPR2 chunk ID.</summary>
        public int Sprite { get; init; }
        public int Frame { get; init; }
        /// <summary>A PNG relative to the document; resizing requires spriteOffset and depth.</summary>
        public required string Image { get; init; }
        /// <summary>An optional grayscale PNG of depth values (0 nearest); without it, painted pixels take the nearest original depth.</summary>
        public string? Depth { get; init; }
        /// <summary>Explicit DGRP screen offset for resized frames, [x,y]. Requires depth.</summary>
        public int[]? SpriteOffset { get; init; }
    }

    internal sealed record GraphicSet
    {
        public required int[] Groups { get; init; }
        public required GraphicView[] Views { get; init; }
        /// <summary>Additional complete twelve-view poses, selected through ObjectData.Graphic.</summary>
        public GraphicVariant[] Variants { get; init; } = [];
        /// <summary>Draw groups written with all twelve views and no sprites, for the tiles of a multi-tile object
        /// whose art one tile draws whole.</summary>
        public int[] EmptyGroups { get; init; } = [];
    }

    internal sealed record GraphicVariant
    {
        public required int Group { get; init; }
        public required GraphicView[] Views { get; init; }
    }

    internal sealed record GraphicView
    {
        public required int Direction { get; init; }
        public required int Zoom { get; init; }
        public required string Image { get; init; }
        public required string Depth { get; init; }
        public required int[] SpriteOffset { get; init; }
        /// <summary>Optional DGRP object offset [x,y,z] in 1/16 tile; moves the sprite's depth interval with its anchor.</summary>
        public float[]? ObjectOffset { get; init; }
        /// <summary>Additional depth-tested images drawn with this pose, used for baked object effects.</summary>
        public GraphicLayer[]? Layers { get; init; }
    }

    internal sealed record GraphicLayer
    {
        public required string Image { get; init; }
        public required string Depth { get; init; }
        public required int[] SpriteOffset { get; init; }
        public float[]? ObjectOffset { get; init; }
    }

    internal sealed record Advertisement
    {
        public int Min { get; init; }
        public int Max { get; init; }
        public int Personality { get; init; }
    }

    internal sealed class Chunk
    {
        public required string Type;
        public ushort Id;
        public ushort Flags;
        public required string Label;
        public required byte[] Data;
    }

    private enum STRLangCode : byte
    {
        Default = 1,
        EnglishUS = 1,
        EnglishUK = 2,
        French = 3,
        German = 4,
        Italian = 5,
        Spanish = 6,
        Dutch = 7,
        Danish = 8,
        Swedish = 9,
        Norwegian = 10,
        Finnish = 11,
        Hebrew = 12,
        Russian = 13,
        Portuguese = 14,
        Japanese = 15,
        Polish = 16,
        SimplifiedChinese = 17,
        TraditionalChinese = 18,
        Thai = 19,
        Korean = 20
    }

    private enum VMMotive
    {
        HappyLife = 0,
        HappyWeek = 1,
        HappyDay = 2,
        Mood = 3,
        Physical = 4,
        Energy = 5,
        Comfort = 6,
        Hunger = 7,
        Hygiene = 8,
        Bladder = 9,
        Mental = 10,
        SleepState = 11,
        Stress = 12,
        Room = 13,
        Social = 14,
        Fun = 15
    }

    [Flags]
    private enum TTABFlags : uint
    {
        AllowVisitors = 1,
        Joinable = 1 << 1,
        RunImmediately = 1 << 2,
        Consecutive = 1 << 3,
        TS1NoChild = 1 << 4,
        TS1NoDemoChild = 1 << 5,
        TS1NoAdults = 1 << 6,
        Debug = 1 << 7,
        AutoFirst = 1 << 8,
        TS1AllowCats = 1 << 9,
        TS1AllowDogs = 1 << 10,
        Carry = 1 << 11,
        Repair = 1 << 12,
        Check = 1 << 13,
        AllowGhost = 1 << 16,
        IgnoreObjectLock = 1 << 17,
        MustRun = 1 << 18,
        Leapfrog = 1 << 19
    }

    private sealed class STRItem
    {
        public byte LanguageCode = 1;
        public string Value = "";
        public string Comment = "";
    }

    private sealed class STRLanguageSet
    {
        public STRItem[] Strings = [];
    }

    private sealed class STR
    {
        public STRLanguageSet?[] LanguageSets { get; } = new STRLanguageSet?[20];
        public int Length => LanguageSets[0]?.Strings.Length ?? 0;
        public string? GetString(int index) => index >= 0 && index < (LanguageSets[0]?.Strings.Length ?? 0) ? LanguageSets[0]!.Strings[index].Value : null;
    }

    private sealed class TTABMotiveEntry
    {
        public int MotiveIndex;
        public short EffectRangeMinimum;
        public short EffectRangeDelta;
        public ushort PersonalityModifier;
    }

    private sealed class TTABInteraction
    {
        public ushort ActionFunction;
        public ushort TestFunction;
        public TTABFlags Flags;
        public uint TTAIndex;
        public uint AttenuationCode;
        public float AttenuationValue;
        public uint AutonomyThreshold;
        public int JoiningIndex = -1;
        public TTABMotiveEntry[] MotiveEntries = Enumerable.Range(0, 16).Select(motive => new TTABMotiveEntry { MotiveIndex = motive }).ToArray();

        public bool TS1AllowChild
        {
            get => (Flags & TTABFlags.TS1NoChild) == 0;
            set
            {
                if (!value) Flags |= TTABFlags.TS1NoChild;
                else Flags &= ~TTABFlags.TS1NoChild;
            }
        }

        public bool TS1AllowDemoChild
        {
            get => (Flags & TTABFlags.TS1NoDemoChild) == 0;
            set
            {
                if (!value) Flags |= TTABFlags.TS1NoDemoChild;
                else Flags &= ~TTABFlags.TS1NoDemoChild;
            }
        }

        public bool TS1AllowAdults
        {
            get => (Flags & TTABFlags.TS1NoAdults) == 0;
            set
            {
                if (!value) Flags |= TTABFlags.TS1NoAdults;
                else Flags &= ~TTABFlags.TS1NoAdults;
            }
        }
    }

    private sealed class TTAB
    {
        public ushort Version = 10;
        public TTABInteraction[] Interactions = [];
    }

    private sealed class BHAVInstruction
    {
        public ushort Opcode;
        public bool Breakpoint;
        public byte TruePointer;
        public byte FalsePointer;
        public byte[] Operand = new byte[8];
    }

    private sealed class BHAV
    {
        public ushort Version;
        public byte Type;
        public byte Args;
        public ushort Locals;
        public ushort Unknown;
        public BHAVInstruction[] Instructions = [];
    }

    private sealed class SLOTItem
    {
        public ushort Type = 3;
        public float OffsetX;
        public float OffsetY;
        public float OffsetZ;
        public int Standing = 1;
        public int Sitting = 0;
        public int Ground = 0;
        public uint Rsflags = 0;
        public int SnapTargetSlot = -1;
        public int MinProximity = 0;
        public int MaxProximity = 0;
        public int OptimalProximity = 0;
        public int MaxSize = 100;
        public int I10 = 0;
        public float Gradient = 0f;
        public int Height = 5;
        public int Facing = -2;
        public int Resolution = 16;
    }

    private sealed class SLOT
    {
        public List<SLOTItem> Chronological { get; } = [];
    }

    private sealed class DGRPSprite
    {
        public uint SpriteID;
        public uint SpriteFrameIndex;
        public uint Flags;
        public Vector2 SpriteOffset;
        public Vector3 ObjectOffset;
        public bool Flip => (Flags & 1) != 0;
    }

    private sealed class DGRPImage
    {
        public uint Direction;
        public uint Zoom;
        public DGRPSprite[] Sprites = [];
    }

    private sealed class DGRP
    {
        public DGRPImage[] Images = [];
    }

    private sealed class ParsedObjd
    {
        public uint GUID;
        public ushort ObjectType;
        public ushort MasterID;
        public short SubIndex;
        public ushort TreeTableID;
        public ushort AnimationTableID;
        public ushort Price;
        public ushort BaseGraphicID;
        public ushort NumGraphics;
        public ushort Disabled;
        public ushort FunctionFlags;
        public ushort BuildModeType;
        public ushort RoomFlags;
        public ushort FunctionSubsort;
        public ushort DTSubsort;
        public ushort VacationSubsort;
        public ushort CommunitySubsort;
        public ushort STSubsort;
        public ushort MTSubsort;
    }

    private abstract class VMPrimitiveOperand
    {
        public abstract void Read(byte[] bytes);
        public abstract void Write(byte[] bytes);
    }

    private enum VMVariableScope : sbyte
    {
        MyObjectAttributes = 0,
        MyAttribute = 0,
        StackObjectAttributes = 1,
        StackObjectAttribute = 1,
        TargetObjectAttributes = 2,
        TargetObjectAttribute = 2,
        MyObject = 3,
        My = 3,
        StackObject = 4,
        TargetObject = 5,
        Global = 6,
        Literal = 7,
        Temps = 8,
        Temporary = 8,
        Parameters = 9,
        Parameter = 9,
        StackObjectID = 10,
        StackObjectId = 10,
        TempByTemp = 11,
        TemporaryByTemporary = 11,
        TreeAdRange = 12,
        CheckTreeAdRange = 12,
        StackObjectTemp = 13,
        StackObjectTemporary = 13,
        MyMotives = 14,
        MyMotive = 14,
        StackObjectMotives = 15,
        StackObjectMotive = 15,
        StackObjectSlot = 16,
        StackObjectMotiveByTemp = 17,
        StackObjectMotiveByTemporary = 17,
        MyPersonData = 18,
        StackObjectPersonData = 19,
        MySlot = 20,
        StackObjectDefinition = 21,
        StackObjectAttributeByParameter = 22,
        RoomByTemp0 = 23,
        RoomByTemporary0 = 23,
        NeighborInStackObject = 24,
        Local = 25,
        Tuning = 26,
        Constant = 26,
        DynSpriteFlagForTempOfStackObject = 27,
        StackObjectDynamicSpriteFlagByTemporary = 27,
        TreeAdPersonalityVar = 28,
        CheckTreeAdPersonalityVariable = 28,
        TreeAdMin = 29,
        CheckTreeAdMin = 29,
        MyPersonDataByTemp = 30,
        MyPersonDataByTemporary = 30,
        StackObjectPersonDataByTemp = 31,
        StackObjectPersonDataByTemporary = 31,
        NeighborPersonData = 32,
        JobData = 33,
        JobDataByTemporary0And1 = 33,
        NeighborhoodData = 34,
        StackObjectFunction = 35,
        MyTypeAttr = 36,
        MyTypeAttribute = 36,
        StackObjectTypeAttr = 37,
        StackObjectTypeAttribute = 37,
        NeighborObjectDefinition = 38,
        Invalid = -1
    }

    private enum VMExpressionOperator : byte
    {
        GreaterThan = 0,
        LessThan = 1,
        Equals = 2,
        EqualTo = 2,
        PlusEquals = 3,
        AddTo = 3,
        MinusEquals = 4,
        SubtractFrom = 4,
        Assign = 5,
        AssignTo = 5,
        MulEquals = 6,
        MultiplyBy = 6,
        DivEquals = 7,
        DivideBy = 7,
        IsFlagSet = 8,
        SetFlag = 9,
        ClearFlag = 10,
        IncAndLessThan = 11,
        IncrementAndLessThan = 11,
        ModEquals = 12,
        ModuloBy = 12,
        AndEquals = 13,
        AndWith = 13,
        GreaterThanOrEqualTo = 14,
        LessThanOrEqualTo = 15,
        NotEqualTo = 16,
        DecAndGreaterThan = 17,
        DecrementAndGreaterThan = 17,
        OrEquals = 18,
        OrWith = 18,
        XorEquals = 19,
        XorWith = 19,
        AssignSquareRootRhs = 20,
        AssignSquareRoot = 20
    }

    private sealed class VMSleepOperand : VMPrimitiveOperand
    {
        public ushort StackVarToDec { get; set; }
        public override void Read(byte[] bytes) => StackVarToDec = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        public override void Write(byte[] bytes) => BinaryPrimitives.WriteUInt16LittleEndian(bytes, StackVarToDec);
    }

    private sealed class VMExpressionOperand : VMPrimitiveOperand
    {
        public short LhsData { get; set; }
        public short RhsData { get; set; }
        public byte IsSigned { get; set; }
        public VMExpressionOperator Operator { get; set; }
        public VMVariableScope LhsOwner { get; set; }
        public VMVariableScope RhsOwner { get; set; }

        public override void Read(byte[] bytes)
        {
            LhsData = BinaryPrimitives.ReadInt16LittleEndian(bytes);
            RhsData = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2));
            IsSigned = bytes[4];
            Operator = (VMExpressionOperator)bytes[5];
            LhsOwner = (VMVariableScope)unchecked((sbyte)bytes[6]);
            RhsOwner = (VMVariableScope)unchecked((sbyte)bytes[7]);
        }

        public override void Write(byte[] bytes)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes, LhsData);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), RhsData);
            bytes[4] = IsSigned;
            bytes[5] = (byte)Operator;
            bytes[6] = unchecked((byte)(sbyte)LhsOwner);
            bytes[7] = unchecked((byte)(sbyte)RhsOwner);
        }
    }

    private enum VMRemoveObjectTarget : byte
    {
        Me = 0,
        StackObject = 1,
        TargetObject = 2
    }

    private sealed class VMRemoveObjectInstanceOperand : VMPrimitiveOperand
    {
        public VMRemoveObjectTarget Target { get; set; }
        public byte Flags { get; set; }
        public bool ReturnImmediately
        {
            get => (Flags & 1) == 1;
            set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
        }
        public bool CleanupAll
        {
            get => (Flags & 2) == 2;
            set => Flags = (byte)((Flags & ~2) | (value ? 2 : 0));
        }

        public override void Read(byte[] bytes)
        {
            Target = (VMRemoveObjectTarget)bytes[0];
            Flags = bytes[2];
        }

        public override void Write(byte[] bytes)
        {
            bytes[0] = (byte)Target;
            bytes[2] = Flags;
        }
    }

    private enum VMTransferFundsExpenseType : byte
    {
        None = 0,
        MiscIncome = 1,
        JobIncome = 2,
        ServiceExpense = 3,
        FoodExpense = 4,
        BillsExpense = 5,
        MiscExpense = 6,
        HouseholdExpense = 7,
        ArchitectureExpense = 8
    }

    private sealed class VMTransferFundsOperand : VMPrimitiveOperand
    {
        public byte OldAmountOwner { get; set; } = 255;
        public VMVariableScope AmountOwner { get; set; }
        public ushort AmountData { get; set; }
        public byte Flags { get; set; }
        public VMTransferFundsExpenseType ExpenseType { get; set; }

        public bool JustTest
        {
            get => (Flags & 1) > 0;
            set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
        }

        public bool Subtract
        {
            get => (Flags & 2) > 0;
            set => Flags = (byte)((Flags & ~2) | (value ? 2 : 0));
        }

        public override void Read(byte[] bytes)
        {
            OldAmountOwner = bytes[0];
            AmountOwner = OldAmountOwner switch
            {
                0 => VMVariableScope.Literal,
                1 => VMVariableScope.Parameters,
                2 => VMVariableScope.Local,
                _ => (VMVariableScope)unchecked((sbyte)bytes[1])
            };
            AmountData = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
            Flags = bytes[4];
            ExpenseType = (VMTransferFundsExpenseType)bytes[6];
        }

        public override void Write(byte[] bytes)
        {
            bytes[0] = OldAmountOwner;
            bytes[1] = unchecked((byte)(sbyte)AmountOwner);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), AmountData);
            bytes[4] = Flags;
            bytes[6] = (byte)ExpenseType;
        }
    }

    private enum VMGotoRelativeLocation : sbyte
    {
        OnTopOf = -2,
        AnywhereNear = -1,
        InFrontOf = 0,
        InFrontAndToRightOf = 1,
        ToTheRightOf = 2,
        BehindAndToRightOf = 3,
        Behind = 4,
        BehindAndToTheLeftOf = 5,
        ToTheLeftOf = 6,
        InFrontAndToTheLeftOf = 7
    }

    private enum VMGotoRelativeDirection : sbyte
    {
        AnyDirection = -2,
        Facing = -1,
        SameDirection = 0,
        FortyFiveDegreesRightOfSameDirection = 1,
        NinetyDegreesRightOfSameDirection = 2,
        OneThirtyFiveDegreesRightOfSameDirection = 3,
        OppositeDirection = 4,
        OneThirtyFiveDegreesLeftOfSameDirection = 5,
        NinetyDegreesLeftOfSameDirection = 6,
        FortyFiveDegreesLeftOfSameDirection = 7
    }

    [Flags]
    private enum VMGotoRelativeFlags : byte
    {
        None = 0,
        RequireEmpty = 1,
        NoFailureTrees = 2,
        AllowDifferentAltitude = 4
    }

    private sealed class VMGotoRelativePositionOperand : VMPrimitiveOperand
    {
        public ushort OldTrapCount { get; set; }
        public VMGotoRelativeLocation Location { get; set; }
        public VMGotoRelativeDirection Direction { get; set; }
        public ushort RouteCount { get; set; }
        public VMGotoRelativeFlags Flags { get; set; }

        public override void Read(byte[] bytes)
        {
            OldTrapCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            Location = (VMGotoRelativeLocation)unchecked((sbyte)bytes[2]);
            Direction = (VMGotoRelativeDirection)unchecked((sbyte)bytes[3]);
            RouteCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
            Flags = (VMGotoRelativeFlags)bytes[6];
        }

        public override void Write(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, OldTrapCount);
            bytes[2] = unchecked((byte)(sbyte)Location);
            bytes[3] = unchecked((byte)(sbyte)Direction);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), RouteCount);
            bytes[6] = (byte)Flags;
        }
    }

    private sealed class VMSetMotiveDeltasOperand : VMPrimitiveOperand
    {
        public VMVariableScope RateOwner { get; set; }
        public VMVariableScope MaxOwner { get; set; }
        public VMMotive Motive { get; set; }
        public byte Flags { get; set; }
        public short RateData { get; set; }
        public short MaxData { get; set; }

        public bool ClearAll
        {
            get => (Flags & 1) > 0;
            set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
        }

        public override void Read(byte[] bytes)
        {
            RateOwner = (VMVariableScope)unchecked((sbyte)bytes[0]);
            MaxOwner = (VMVariableScope)unchecked((sbyte)bytes[1]);
            Motive = (VMMotive)bytes[2];
            Flags = bytes[3];
            RateData = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(4));
            MaxData = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(6));
        }

        public override void Write(byte[] bytes)
        {
            bytes[0] = unchecked((byte)(sbyte)RateOwner);
            bytes[1] = unchecked((byte)(sbyte)MaxOwner);
            bytes[2] = (byte)Motive;
            bytes[3] = Flags;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), RateData);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), MaxData);
        }
    }

    private enum VMCreateObjectPosition : byte
    {
        InFrontOfMe = 0,
        OnTopOfMe = 1,
        InMyHand = 2,
        InFrontOfStackObject = 3,
        InSlot0OfStackObject = 4,
        UnderneathMe = 5,
        OutOfWorld = 6,
        BelowObjectInStackParam0 = 7,
        BelowObjectInLocal = 8,
        NextToMeInDirectionOfLocal = 9
    }

    [Flags]
    private enum VMCreateObjectFlags : byte
    {
        None = 0,
        FailIfTileFull = 1,
        PassObjectIds = 2,
        CreateNeighborInStackObject = 4,
        FailIfFloorOccupied = 8,
        PassTemp0 = 16,
        CopyDirFromStackObject = 32,
        IgnoreRooms = 64
    }

    private sealed class VMCreateObjectInstanceOperand : VMPrimitiveOperand
    {
        public uint GUID { get; set; }
        public VMCreateObjectPosition Position { get; set; }
        public VMCreateObjectFlags Flags { get; set; }
        public byte LocalToUse { get; set; }
        public byte InteractionCallback { get; set; }

        public override void Read(byte[] bytes)
        {
            GUID = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            Position = (VMCreateObjectPosition)bytes[4];
            Flags = (VMCreateObjectFlags)bytes[5];
            LocalToUse = bytes[6];
            InteractionCallback = bytes[7];
        }

        public override void Write(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, GUID);
            bytes[4] = (byte)Position;
            bytes[5] = (byte)Flags;
            bytes[6] = LocalToUse;
            bytes[7] = InteractionCallback;
        }
    }

    private enum VMAnimationScope : byte
    {
        Object = 0,
        Misc = 1,
        PersonStock = 2,
        Global = 3,
        StackObject = 4
    }

    private sealed class VMAnimateSimOperand : VMPrimitiveOperand
    {
        public ushort AnimationID { get; set; }
        public byte LocalEventNumber { get; set; }
        public byte Pad { get; set; }
        public VMAnimationScope Source { get; set; }
        public byte Flags { get; set; }
        public byte ExpectedEventCount { get; set; }

        public byte Mode
        {
            get => (byte)((Flags & 1) | ((Flags >> 3) & 2));
            set => Flags = (byte)((Flags & ~0x11) | (value & 1) | ((value & 2) << 3));
        }

        public bool PlayBackwards
        {
            get => (Flags & 2) > 0;
            set => Flags = (byte)((Flags & ~2) | (value ? 2 : 0));
        }

        public bool AnimSpeed
        {
            get => (Flags & 4) > 0;
            set => Flags = (byte)((Flags & ~4) | (value ? 4 : 0));
        }

        public bool Hurry
        {
            get => (Flags & 8) > 0;
            set => Flags = (byte)((Flags & ~8) | (value ? 8 : 0));
        }

        public bool StoreEventInLocal
        {
            get => (Flags & 32) > 0;
            set => Flags = (byte)((Flags & ~32) | (value ? 32 : 0));
        }

        public bool BlendOutDisabled
        {
            get => (Flags & 64) > 0;
            set => Flags = (byte)((Flags & ~64) | (value ? 64 : 0));
        }

        public bool BlendInDisabled
        {
            get => (Flags & 128) > 0;
            set => Flags = (byte)((Flags & ~128) | (value ? 128 : 0));
        }

        public override void Read(byte[] bytes)
        {
            AnimationID = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            LocalEventNumber = bytes[2];
            Pad = bytes[3];
            Source = (VMAnimationScope)bytes[4];
            Flags = bytes[5];
            ExpectedEventCount = bytes[6];
        }

        public override void Write(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, AnimationID);
            bytes[2] = LocalEventNumber;
            bytes[3] = Pad;
            bytes[4] = (byte)Source;
            bytes[5] = Flags;
            bytes[6] = ExpectedEventCount;
        }
    }

    private enum VMGotoRoutingSlotType : ushort
    {
        Param = 0,
        Literal = 1,
        Global = 2
    }

    private sealed class VMGotoRoutingSlotOperand : VMPrimitiveOperand
    {
        public ushort Data { get; set; }
        public VMGotoRoutingSlotType Type { get; set; }
        public byte Flags { get; set; }

        public bool NoFailureTrees
        {
            get => (Flags & 1) > 0;
            set => Flags = (byte)((Flags & ~1) | (value ? 1 : 0));
        }

        public override void Read(byte[] bytes)
        {
            Data = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            Type = (VMGotoRoutingSlotType)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
            Flags = bytes[4];
        }

        public override void Write(byte[] bytes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, Data);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)Type);
            bytes[4] = Flags;
        }
    }

    private sealed record PrimitiveRegistration(ushort Opcode, string Name, Type? OperandModel = null);

    private static readonly PrimitiveRegistration?[] Primitives = BuildPrimitives();

    private static PrimitiveRegistration?[] BuildPrimitives()
    {
        var table = new PrimitiveRegistration?[256];
        void Register(ushort opcode, string name, Type? model = null) => table[opcode] = new PrimitiveRegistration(opcode, name, model);
        Register(0, "sleep", typeof(VMSleepOperand));
        Register(1, "generic_sims_call");
        Register(2, "expression", typeof(VMExpressionOperand));
        Register(3, "find_best_interaction");
        Register(4, "grab");
        Register(5, "drop");
        Register(6, "change_suit_or_accessory");
        Register(7, "refresh");
        Register(8, "random_number");
        Register(9, "burn");
        Register(10, "tutorial");
        Register(11, "get_distance_to");
        Register(12, "get_direction_to");
        Register(13, "push_interaction");
        Register(14, "find_best_object_for_function");
        Register(15, "breakpoint");
        Register(16, "find_location_for");
        Register(17, "idle_for_input");
        Register(18, "remove_object_instance", typeof(VMRemoveObjectInstanceOperand));
        Register(19, "make_new_character");
        Register(20, "run_functional_tree");
        Register(21, "show_string");
        Register(22, "look_towards");
        Register(23, "play_sound");
        Register(24, "old_relationship");
        Register(25, "budget", typeof(VMTransferFundsOperand));
        Register(26, "relationship");
        Register(27, "goto_relative", typeof(VMGotoRelativePositionOperand));
        Register(28, "run_tree_by_name");
        Register(29, "set_motive_deltas", typeof(VMSetMotiveDeltasOperand));
        Register(30, "gosub_found_action");
        Register(31, "set_to_next");
        Register(32, "test_object_type");
        Register(33, "find_5_worst_motives");
        Register(34, "ui_effect");
        Register(35, "special_effect");
        Register(36, "dialog");
        Register(37, "test_sim_interacting_with");
        Register(41, "set_balloon_headline");
        Register(42, "create_object_instance", typeof(VMCreateObjectInstanceOperand));
        Register(43, "drop_onto");
        Register(44, "animate", typeof(VMAnimateSimOperand));
        Register(45, "goto_routing_slot", typeof(VMGotoRoutingSlotOperand));
        Register(46, "snap");
        Register(47, "reach");
        Register(48, "stop_all_sounds");
        Register(49, "stack_obj_out_of_idle");
        Register(50, "change_action_string");
        Register(51, "manage_inventory");
        return table;
    }

    private static readonly string[] ObjdFieldNames =
    [
        "StackSize",
        "BaseGraphicID",
        "NumGraphics",
        "BHAV_MainID",
        "BHAV_GardeningID",
        "TreeTableID",
        "InteractionGroupID",
        "ObjectType",
        "MasterID",
        "SubIndex",
        "BHAV_WashHandsID",
        "AnimationTableID",
        "GUID1",
        "GUID2",
        "Disabled",
        "BHAV_Portal",
        "Price",
        "BodyStringID",
        "SlotID",
        "BHAV_AllowIntersectionID",
        "UsesFnTable",
        "Bitfield1",
        "BHAV_PrepareFoodID",
        "BHAV_CookFoodID",
        "BHAV_PlaceSurfaceID",
        "BHAV_DisposeID",
        "BHAV_EatID",
        "BHAV_PickupFromSlotID",
        "BHAV_WashDishID",
        "BHAV_EatingSurfaceID",
        "BHAV_SitID",
        "BHAV_StandID",
        "SalePrice",
        "InitialDepreciation",
        "DailyDepreciation",
        "SelfDepreciating",
        "DepreciationLimit",
        "RoomFlags",
        "FunctionFlags",
        "CatalogStringsID",
        "Global",
        "BHAV_Init",
        "BHAV_Place",
        "BHAV_UserPickup",
        "WallStyle",
        "BHAV_Load",
        "BHAV_UserPlace",
        "ObjectVersion",
        "BHAV_RoomChange",
        "MotiveEffectsID",
        "BHAV_Cleanup",
        "BHAV_LevelInfo",
        "CatalogID",
        "BHAV_ServingSurface",
        "LevelOffset",
        "Shadow",
        "NumAttributes",
        "BHAV_Clean",
        "BHAV_QueueSkipped",
        "FrontDirection",
        "BHAV_WallAdjacencyChanged",
        "MyLeadObject",
        "DynamicSpriteBaseId",
        "NumDynamicSprites",
        "ChairEntryFlags",
        "TileWidth",
        "LotCategories",
        "BuildModeType",
        "OriginalGUID1",
        "OriginalGUID2",
        "SuitGUID1",
        "SuitGUID2",
        "BHAV_Pickup",
        "ThumbnailGraphic",
        "ShadowFlags",
        "FootprintMask",
        "BHAV_DynamicMultiTileUpdate",
        "ShadowBrightness",
        "BHAV_Repair",
        "WallStyleSpriteID",
        "RatingHunger",
        "RatingComfort",
        "RatingHygiene",
        "RatingBladder",
        "RatingEnergy",
        "RatingFun",
        "RatingRoom",
        "RatingSkillFlags",
        "NumTypeAttributes",
        "MiscFlags",
        "TypeAttrGUID1",
        "TypeAttrGUID2",
        "FunctionSubsort",
        "DTSubsort",
        "KeepBuying",
        "VacationSubsort",
        "ResetLotAction",
        "CommunitySubsort",
        "DreamFlags",
        "RenderFlags",
        "VitaboyFlags",
        "STSubsort",
        "MTSubsort"
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private const byte PointerError = 253, PointerTrue = 254, PointerFalse = 255;

    internal static object Compile(string source, string game, string destination)
    {
        var document = ReadDocument(source);
        return Compile(document, game, destination, Path.GetDirectoryName(Path.GetFullPath(source))!);
    }

    internal static Document ReadDocument(string source)
    {
        if (new FileInfo(source).Length > 8 * 1024 * 1024) throw new InvalidDataException("Object source exceeds size limit");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(source), Json) ?? throw new InvalidDataException("Invalid object document");
    }

    /// <summary>Read-only summary of object definitions, catalog interactions and SimAntics instructions in an IFF.</summary>
    internal static object Inspect(string path)
    {
        return InspectChunks(Path.GetFileName(path), ReadIff(File.ReadAllBytes(path)));
    }

    internal static object InspectTemplate(string name, string game)
    {
        return InspectChunks(name, ReadIff(new Templates(Path.GetFullPath(game)).Read(name)));
    }

    private static object InspectChunks(string name, List<Chunk> chunks)
    {
        var definitions = chunks.Where(chunk => chunk.Type == "OBJD").Select(chunk =>
        {
            var definition = ParseObjd(chunk);
            var table = chunks.FirstOrDefault(item => item.Type == "TTAB" && item.Id == definition.TreeTableID);
            var names = chunks.FirstOrDefault(item => item.Type == "TTAs" && item.Id == definition.TreeTableID);
            object AnimationTable(ushort id)
            {
                var animationChunk = chunks.FirstOrDefault(item => item.Type == "STR#" && item.Id == id);
                if (animationChunk == null) return new { id, entries = Array.Empty<object>() };
                var strings = ParseStrings(animationChunk);
                var entries = Enumerable.Range(0, strings.Length)
                    .Select(index => (object)new { index, name = strings.GetString(index) ?? "" }).ToArray();
                return new { id, entries };
            }
            var parsedNames = names == null ? null : ParseStrings(names);
            var interactions = table == null ? Array.Empty<object>() : ParseTtab(table).Interactions.Select(interaction => (object)new
            {
                index = interaction.TTAIndex,
                name = parsedNames == null ? "" : parsedNames.GetString((int)interaction.TTAIndex),
                action = interaction.ActionFunction,
                test = interaction.TestFunction,
                autonomy = interaction.AutonomyThreshold,
                flags = interaction.Flags.ToString()
            }).ToArray();
            return new
            {
                guid = $"0x{definition.GUID:X8}", chunk = chunk.Id, label = chunk.Label,
                definition.ObjectType, definition.MasterID, definition.SubIndex, definition.TreeTableID,
                definition.AnimationTableID,
                animationTables = new
                {
                    adult = AnimationTable(definition.AnimationTableID),
                    child = AnimationTable((ushort)(definition.AnimationTableID + 1))
                },
                definition.Price, definition.BaseGraphicID, definition.NumGraphics,
                definition.Disabled, definition.FunctionFlags, definition.BuildModeType, definition.RoomFlags,
                definition.FunctionSubsort, definition.DTSubsort, definition.VacationSubsort,
                definition.CommunitySubsort, definition.STSubsort, definition.MTSubsort, interactions
            };
        }).ToArray();
        var behaviors = chunks.Where(chunk => chunk.Type == "BHAV").Select(chunk =>
        {
            var behavior = ParseBhav(chunk);
            return new
            {
                id = chunk.Id, label = chunk.Label, behavior.Type, behavior.Args, behavior.Locals,
                instructions = behavior.Instructions.Select((instruction, index) => new
                {
                    index, opcode = instruction.Opcode,
                    primitive = instruction.Opcode < 256 ? Primitives[instruction.Opcode]?.Name : null,
                    instruction.TruePointer, instruction.FalsePointer,
                    operand = Convert.ToHexString(instruction.Operand)
                }).ToArray()
            };
        }).ToArray();
        return new { file = name, definitions, behaviors };
    }

    internal static object Compile(Document document, string game, string destination, string? baseDirectory = null)
    {
        var output = Path.GetFullPath(destination);
        if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Object pack output already exists");
        if (document.SchemaVersion != 1 || !Identifier(document.PackID) || string.IsNullOrWhiteSpace(document.Version) ||
            (document.Name != null && string.IsNullOrWhiteSpace(document.Name)) || document.Objects == null || document.Objects.Length is < 1 or > 64)
            throw new InvalidDataException("Invalid object pack specification");
        if (document.Objects.Select(item => item.File?.ToLowerInvariant()).Distinct().Count() != document.Objects.Length)
            throw new InvalidDataException("Object file names must be unique");
        var templates = new Templates(Path.GetFullPath(game));
        var guids = new HashSet<uint>();
        var compiled = new List<(string File, byte[] Data, object Summary)>();
        foreach (var spec in document.Objects)
        {
            if (!Identifier(spec.File)) throw new InvalidDataException($"Invalid object file name {spec.File}");
            var (data, summary) = CompileObject(spec, templates.Read(spec.Template), guids, baseDirectory);
            compiled.Add((spec.File.ToLowerInvariant() + ".iff", data, summary));
        }

        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".object-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(staging, "files", "Downloads", document.PackID.ToLowerInvariant());
            Directory.CreateDirectory(folder);
            foreach (var item in compiled) File.WriteAllBytes(Path.Combine(folder, item.File), item.Data);
            var manifest = new
            {
                schemaVersion = 1, id = document.PackID, name = document.Name ?? document.PackID, version = document.Version,
                baseGameVersion = "1.90", dependencies = Array.Empty<object>(), overrides = Array.Empty<string>()
            };
            File.WriteAllText(Path.Combine(staging, "pack.json"), JsonSerializer.Serialize(manifest, Json));
            Directory.Move(staging, output);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        return new
        {
            pack = output,
            objects = compiled.Select(item => item.Summary).ToArray()
        };
    }

    private static (byte[] Data, object Summary) CompileObject(ObjectSpec spec, byte[] template, HashSet<uint> guids, string? baseDirectory)
    {
        var chunks = ReadIff(template);
        var (lead, definitions) = SelectObject(spec, chunks);
        if (spec.TileLayout is { } layoutParts)
        {
            if (spec.TileGuids is { Length: > 0 } || spec.TileFields.Length > 0)
                throw new InvalidDataException("tileLayout cannot be combined with tileGuids or tileFields");
            definitions = ExpandTileLayout(lead, definitions, layoutParts, chunks);
        }
        var tileGuids = spec.TileLayout is { } tileLayout
            ? tileLayout.Select(part => ParseGuid(part.Guid, "tile GUID")).ToArray()
            : (spec.TileGuids ?? []).Select(value => ParseGuid(value, "tile GUID")).ToArray();
        if (tileGuids.Length != definitions.Count - 1)
            throw new InvalidDataException($"Template {spec.Template} has {definitions.Count} definitions; give tileGuids for the {definitions.Count - 1} besides the lead");
        if (spec.TileFields.Length > 0 && spec.TileFields.Length != tileGuids.Length)
            throw new InvalidDataException("tileFields must have one entry per tileGuid");
        var assigned = new Dictionary<Chunk, uint> { [lead] = ParseGuid(spec.Guid, "GUID") };
        foreach (var (chunk, guid) in definitions.Where(chunk => chunk != lead).Zip(tileGuids)) assigned[chunk] = guid;
        foreach (var guid in assigned.Values)
            if (guid == 0 || !guids.Add(guid)) throw new InvalidDataException($"Object GUID 0x{guid:X8} is zero or used twice");

        var price = spec.Price;
        if (price is < 0 or > ushort.MaxValue) throw new InvalidDataException("Object price must be between 0 and 65535");
        foreach (var name in spec.Fields.Keys.Concat(spec.TileFields.SelectMany(fields => fields.Keys))
                     .Concat(spec.TileLayout?.SelectMany(part => part.Fields.Keys) ?? []))
            if (name is "GUID1" or "GUID2" or "Price" or "TreeTableID" or "CatalogStringsID" or "MasterID" or "SubIndex" || FieldIndex(name) < 0)
                throw new InvalidDataException($"Definition field {name} cannot be set");
        var dropped = DropCalls(chunks, spec.DropCalls);
        // A synthesized tile layout may clone one template part many times. Calls in the retained template's BHAVs
        // still target its original tile GUID, so map that old GUID to the first authored instance deterministically.
        var behaviorGuidMap = definitions.GroupBy(chunk => ReadField(chunk, "GUID1") | (uint)ReadField(chunk, "GUID2") << 16)
            .ToDictionary(group => group.Key, group => assigned[group.First()]);
        var retargeted = RetargetBehaviors(chunks, behaviorGuidMap);
        foreach (var definition in definitions)
        {
            var guid = assigned[definition];
            WriteField(definition, "GUID1", (ushort)(guid & 0xFFFF));
            WriteField(definition, "GUID2", (ushort)(guid >> 16));
            if (definition == lead && price.HasValue) WriteField(definition, "Price", (ushort)price.Value);
            if (definition == lead)
                foreach (var (name, value) in spec.Fields)
                {
                    if (value is < short.MinValue or > ushort.MaxValue) throw new InvalidDataException($"Definition field {name} is out of range");
                    WriteField(definition, name, (ushort)value);
                }
            else if (spec.TileLayout is { } layout)
            {
                var index = Array.IndexOf(tileGuids, guid);
                foreach (var (name, value) in layout[index].Fields)
                {
                    if (value is < short.MinValue or > ushort.MaxValue) throw new InvalidDataException($"Definition field {name} is out of range");
                    WriteField(definition, name, (ushort)value);
                }
            }
            else if (spec.TileFields.Length > 0)
            {
                var index = Array.IndexOf(tileGuids, guid);
                foreach (var (name, value) in spec.TileFields[index])
                {
                    if (value is < short.MinValue or > ushort.MaxValue) throw new InvalidDataException($"Definition field {name} is out of range");
                    WriteField(definition, name, (ushort)value);
                }
            }
        }
        if (spec.CatalogName != null) lead.Label = Label(spec.CatalogName);
        var translations = new List<(STRLangCode Language, Translation Text)>();
        foreach (var (name, translation) in spec.Translations ?? [])
        {
            if (name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse<STRLangCode>(name, true, out var language) || (int)language is < 1 or > 20)
                throw new InvalidDataException($"Unknown language {name}; use a name such as French or German");
            if (translations.Any(item => item.Language == language)) throw new InvalidDataException($"Language {name} is translated twice");
            foreach (var interaction in translation.Interactions.Keys)
                if (!spec.Interactions.Any(item => item.Name == interaction))
                    throw new InvalidDataException($"The {name} translation names an unknown interaction {interaction}");
            translations.Add((language, translation));
        }

        EnsureCatalogResources(spec, chunks, lead, translations);

        // Catalog text: string 0 is the name and 1 the description, in every language the template carries.
        if (spec.CatalogName != null || spec.Description != null || translations.Any(item => item.Text.CatalogName != null || item.Text.Description != null))
        {
            var catalogID = ReadField(lead, "CatalogStringsID");
            var catalog = chunks.FirstOrDefault(chunk => chunk.Type == "CTSS" && chunk.Id == catalogID)
                ?? throw new InvalidDataException($"Template {spec.Template} has no catalog strings");
            var strings = ParseStrings(catalog);
            if (spec.CatalogName != null) Set(strings, 0, Text(spec.CatalogName, "catalog name"));
            if (spec.Description != null) Set(strings, 1, Text(spec.Description, "description"));
            foreach (var (language, text) in translations)
            {
                if (text.CatalogName != null) Set(strings, language, 0, Text(text.CatalogName, $"{language} catalog name"));
                if (text.Description != null) Set(strings, language, 1, Text(text.Description, $"{language} description"));
            }
            catalog.Data = SerializeStrings(strings);
        }

        // Repainted frames come first so a recolor also applies to them.
        if (spec.CatalogIcon is { } iconPath) ApplyCatalogIcon(iconPath, chunks, ReadField(lead, "CatalogStringsID"), baseDirectory);
        var painted = (spec.Sprites ?? []).Length > 0 ? ApplySprites(spec.Sprites!, chunks, baseDirectory) : null;
        var graphics = spec.Graphics is { } graphicSet ? ApplyGraphicSet(graphicSet, chunks, baseDirectory) : null;
        if (spec.TileLayout != null && spec.Graphics is { Groups.Length: > 0 } authoredGraphics)
        {
            var authoredArtGroups = authoredGraphics.Groups.Concat(authoredGraphics.Variants.Select(variant => variant.Group)).ToArray();
            var physicalGraphics = definitions.Where(definition => definition != lead)
                .Select(definition => (BaseGraphicID: (int)ReadField(definition, "BaseGraphicID"), NumGraphics: (int)ReadField(definition, "NumGraphics")));
            ValidateTileLayoutGraphics(
                explicitTileLayout: true,
                explicitGraphics: true,
                authoredArtGroups,
                ((int)ReadField(lead, "BaseGraphicID"), (int)ReadField(lead, "NumGraphics")),
                physicalGraphics);
        }
        var recolored = spec.Recolor is { } recolor ? ApplyRecolor(recolor, chunks, ReadField(lead, "CatalogStringsID")) : null;

        if (spec.RoutingSlots is { } routingSlots)
        {
            if (routingSlots.Length is 0 or > 255)
                throw new InvalidDataException("Give between 1 and 255 routing slots");
            var indexed = routingSlots.Any(route => route.Index.HasValue);
            if (indexed && routingSlots.Any(route => !route.Index.HasValue))
                throw new InvalidDataException("Either give every routing slot an Index or leave all indices implicit");
            var routes = routingSlots;
            if (indexed)
            {
                if (routingSlots.Any(route => route.Index is < 0 or > 254) ||
                    routingSlots.Select(route => route.Index!.Value).Distinct().Count() != routingSlots.Length)
                    throw new InvalidDataException("Routing slot indices must be unique values from 0 through 254");
                var lastIndex = routingSlots.Max(route => route.Index!.Value);
                routes = Enumerable.Range(0, lastIndex + 1).Select(index => routingSlots.FirstOrDefault(route => route.Index == index) ??
                    new RoutingSlot { Index = index, Offset = [-32768, -32768, 0], IsGeneratedPlaceholder = true }).ToArray();
            }
            foreach (var route in routingSlots)
            {
                if (route.Offset.Length != 3 || route.Offset.Any(value => !float.IsFinite(value)) ||
                    route.Facing is < -3 or > 7 || route.Resolution is < 1 or > 16)
                    throw new InvalidDataException("Invalid routing slot offset, facing or resolution");
            }
            if (definitions.Count > 1)
            {
                // SLOT coordinates and SubIndex values are both expressed in
                // sixteenths of a tile here. A multi-tile interaction target
                // must leave the footprint plus a small actor-clearance margin;
                // otherwise its BHAV has to make the object non-solid to Sims.
                const double tileHalfExtent = 8;
                const double actorClearance = 3.2;
                var occupiedTiles = definitions.Select(definition =>
                {
                    var sub = ReadField(definition, "SubIndex");
                    return sub == ushort.MaxValue
                        ? (X: 0d, Y: 0d)
                        : (X: (double)(sbyte)(sub >> 8), Y: (double)(sbyte)(sub & 255));
                }).Distinct().ToArray();
                foreach (var (route, index) in routingSlots.Select((route, index) => (route, index)))
                {
                    if (route.IsGeneratedPlaceholder || route.AllowInsideFootprint) continue;
                    var x = route.Offset[0];
                    var y = route.Offset[1];
                    if (occupiedTiles.Any(tile =>
                            Math.Abs(x - tile.X * 16) < tileHalfExtent + actorClearance &&
                            Math.Abs(y - tile.Y * 16) < tileHalfExtent + actorClearance))
                        throw new InvalidDataException(
                            $"Routing slot {index} lies within a multi-tile footprint or its actor-clearance margin; move it outside instead of disabling object collision");
                }
            }
            // Each physical part can receive the interaction. SLOT offsets are
            // relative to that part; subtract its original signed SubIndex so
            // every part resolves the same point in the object's footprint.
            foreach (var definition in definitions)
            {
                var slotID = (ushort)Enumerable.Range(1, ushort.MaxValue).First(id => !chunks.Any(chunk => chunk.Type == "SLOT" && chunk.Id == id));
                var sub = ReadField(definition, "SubIndex");
                var x = sub == ushort.MaxValue ? 0 : (sbyte)(sub >> 8);
                var y = sub == ushort.MaxValue ? 0 : (sbyte)(sub & 255);
                var slots = new SLOT();
                foreach (var route in routes)
                    slots.Chronological.Add(new SLOTItem
                    {
                        Type = 3, OffsetX = route.Offset[0] - 16 * x, OffsetY = route.Offset[1] - 16 * y, OffsetZ = route.Offset[2],
                        Facing = route.Facing, Resolution = route.Resolution, Height = 5
                    });
                chunks.Add(new Chunk { Type = "SLOT", Id = slotID, Flags = 0, Label = "Authored routing points", Data = SerializeSlot(slots) });
                WriteField(definition, "SlotID", slotID);
            }
        }
        var animations = AddAnimations(spec, lead, chunks);

        // Behaviors take free private IDs; calls between them resolve by name.
        var behaviorIDs = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var nextID = (ushort)Math.Max(0x1000, chunks.Where(chunk => chunk.Type == "BHAV" && chunk.Id is >= 0x1000 and < 0x2000)
            .Select(chunk => chunk.Id + 1).DefaultIfEmpty(0x1000).Max());
        var existing = chunks.Where(chunk => chunk.Type == "BHAV").Select(chunk => chunk.Id).ToHashSet();
        var assignedBehaviorIDs = new HashSet<ushort>();
        foreach (var behavior in spec.Behaviors)
        {
            ushort id;
            if (behavior.ReplaceRoutine is int replacement)
            {
                if (replacement is < 0x1000 or >= 0x2000 || !existing.Contains((ushort)replacement))
                    throw new InvalidDataException("replaceRoutine must name an existing private routine");
                id = (ushort)replacement;
                var original = ParseBhav(chunks.Single(chunk => chunk.Type == "BHAV" && chunk.Id == id));
                if (behavior.Args != original.Args || behavior.Locals < original.Locals || behavior.Instructions.Length < original.Instructions.Length)
                    throw new InvalidDataException("replaceRoutine must preserve argument count, local capacity and saved instruction positions");
            }
            else
            {
                if (nextID >= 0x2000) throw new InvalidDataException("No private behavior IDs are left in the template");
                id = nextID++;
            }
            if (string.IsNullOrWhiteSpace(behavior.Name) || behavior.Name.Length > 60 || !behaviorIDs.TryAdd(behavior.Name, id) || !assignedBehaviorIDs.Add(id))
                throw new InvalidDataException("Behavior names and target IDs must be unique");
        }
        var behaviors = new List<object>();
        foreach (var behavior in spec.Behaviors)
        {
            var bhav = Assemble(behavior, behaviorIDs, existing, animations, spec.NativeCapabilities);
            if (behavior.ReplaceRoutine != null) chunks.RemoveAll(chunk => chunk.Type == "BHAV" && chunk.Id == behaviorIDs[behavior.Name]);
            chunks.Add(new Chunk { Type = "BHAV", Id = behaviorIDs[behavior.Name], Flags = 0x10, Label = Label(behavior.Name), Data = SerializeBhav(bhav) });
            behaviors.Add(new { name = behavior.Name, id = behaviorIDs[behavior.Name], instructions = bhav.Instructions.Length });
        }
        var animationHooks = ApplyAnimationHooks(spec.AnimationHooks, chunks, animations);
        var animationEventHooks = ApplyAnimationEventHooks(spec.AnimationEventHooks, chunks, behaviorIDs);
        AddNativeCapabilities(spec.NativeCapabilities, chunks);
        var initWrapped = spec.InitBehavior is { } initBehavior ? WrapInit(chunks, definitions, initBehavior, behaviorIDs, existing, ref nextID) : 0;

        var interactions = new List<object>();
        if (spec.Interactions.Length > 0 || spec.RenameInteractions.Length > 0 || spec.ReplaceInteractions)
        {
            var tableID = ReadField(lead, "TreeTableID");
            var tableChunk = chunks.FirstOrDefault(chunk => chunk.Type == "TTAB" && chunk.Id == tableID);
            var namesChunk = chunks.FirstOrDefault(chunk => chunk.Type == "TTAs" && chunk.Id == tableID);
            if (tableChunk == null)
            {
                // Some decorative templates have no tree table; give every definition a new one.
                tableID = (ushort)Math.Max(0x80, chunks.Where(chunk => chunk.Type is "TTAB" or "TTAs").Select(chunk => chunk.Id + 1).DefaultIfEmpty(0x80).Max());
                foreach (var definition in definitions) WriteField(definition, "TreeTableID", tableID);
                tableChunk = new Chunk { Type = "TTAB", Id = tableID, Flags = 0x10, Label = lead.Label, Data = [] };
                chunks.Add(tableChunk);
            }
            if (namesChunk == null)
            {
                namesChunk = new Chunk { Type = "TTAs", Id = tableID, Flags = 0x10, Label = lead.Label, Data = [] };
                chunks.Add(namesChunk);
            }
            var table = tableChunk.Data.Length == 0 ? new TTAB() : ParseTtab(tableChunk);
            var names = namesChunk.Data.Length == 0 ? new STR() : ParseStrings(namesChunk);
            var entries = spec.ReplaceInteractions ? new List<TTABInteraction>() : table.Interactions.ToList();
            if (spec.RenameInteractions.Length > 0 && spec.ReplaceInteractions)
                throw new InvalidDataException("Cannot rename source interactions while replacing the interaction table");
            foreach (var rename in spec.RenameInteractions)
            {
                if (string.IsNullOrWhiteSpace(rename.From) || string.IsNullOrWhiteSpace(rename.To))
                    throw new InvalidDataException("Interaction rename needs nonempty source and destination names");
                var matches = entries.Where(entry => names.GetString((int)entry.TTAIndex) == rename.From).ToArray();
                if (matches.Length != 1) throw new InvalidDataException($"Expected one source interaction named {rename.From}, found {matches.Length}");
                Set(names, (int)matches[0].TTAIndex, Text(rename.To, "renamed interaction"));
            }
            foreach (var interaction in spec.Interactions)
            {
                var index = (uint)Math.Max(names.Length, entries.Select(entry => (int)entry.TTAIndex + 1).DefaultIfEmpty(0).Max());
                var entry = new TTABInteraction
                {
                    ActionFunction = Resolve(interaction.Action, behaviorIDs, existing, "action"),
                    TestFunction = interaction.Test is { } test ? Resolve(test, behaviorIDs, existing, "test") : (ushort)0,
                    TTAIndex = index,
                    AutonomyThreshold = (uint)Math.Clamp(interaction.AutonomyThreshold, 0, 100),
                    AttenuationCode = 0,
                    MotiveEntries = Enumerable.Range(0, 16).Select(motive => new TTABMotiveEntry { MotiveIndex = motive }).ToArray(),
                };
                entry.TS1AllowAdults = interaction.AllowAdults;
                entry.TS1AllowChild = interaction.AllowChildren;
                entry.TS1AllowDemoChild = interaction.AllowChildren;
                if (interaction.AllowVisitors) entry.Flags |= TTABFlags.AllowVisitors;
                if (interaction.Joinable) entry.Flags |= TTABFlags.Joinable;
                if (interaction.RunImmediately) entry.Flags |= TTABFlags.RunImmediately;
                if (interaction.Debug) entry.Flags |= TTABFlags.Debug;
                foreach (var (motive, advertisement) in interaction.Advertisements)
                {
                    if (!Enum.TryParse<VMMotive>(motive, true, out var index16) || (int)index16 is < 0 or > 15)
                        throw new InvalidDataException($"Unknown advertised motive {motive}");
                    if (advertisement.Min is < -100 or > 100 || advertisement.Max is < -1000 or > 1000 || advertisement.Personality is < 0 or > 255)
                        throw new InvalidDataException($"Advertisement for {motive} is out of range");
                    entry.MotiveEntries[(int)index16] = new TTABMotiveEntry
                    {
                        MotiveIndex = (int)index16, EffectRangeMinimum = (short)advertisement.Min,
                        EffectRangeDelta = (short)advertisement.Max, PersonalityModifier = (ushort)advertisement.Personality
                    };
                }
                Set(names, (int)index, Text(interaction.Name, "interaction name"));
                foreach (var (language, text) in translations)
                    if (text.Interactions.TryGetValue(interaction.Name, out var translated))
                        Set(names, language, (int)index, Text(translated, $"{language} interaction name"));
                entries.Add(entry);
                interactions.Add(new { name = interaction.Name, index, action = entry.ActionFunction, test = entry.TestFunction });
            }
            if (entries.Count > 255) throw new InvalidDataException("An object can have at most 255 interactions");
            table.Interactions = entries.ToArray();
            tableChunk.Data = SerializeTtab(table);
            namesChunk.Data = SerializeStrings(names);
        }

        var summary = new
        {
            file = spec.File.ToLowerInvariant() + ".iff",
            template = spec.Template,
            guid = assigned[lead],
            tileGuids,
            retargeted,
            dropped,
            initWrapped,
            catalogName = spec.CatalogName,
            price = ReadField(lead, "Price"),
            painted,
            graphics,
            recolored,
            animations = animations.Select(item => new { name = item.Key, index = item.Value }).ToArray(),
            behaviors,
            animationHooks,
            animationEventHooks,
            interactions
        };
        return (WriteIff(chunks), summary);
    }

    /// <summary>
    /// A custom tile layout's lead OBJD is the logical master; only its children make up the placed footprint.
    /// When the author explicitly supplies visible draw groups, at least one child must reference one if the master
    /// references that art. Catalog BMP_ resources remain attached to the lead and are unaffected by this world-art check.
    /// </summary>
    internal static void ValidateTileLayoutGraphics(bool explicitTileLayout, bool explicitGraphics, IEnumerable<int> authoredArtGroups,
        (int BaseGraphicID, int NumGraphics) master, IEnumerable<(int BaseGraphicID, int NumGraphics)> physicalParts)
    {
        if (!explicitTileLayout || !explicitGraphics) return;
        var artGroups = authoredArtGroups.ToHashSet();
        if (artGroups.Count == 0 || !ReferencesAuthoredArt(master, artGroups)) return;
        if (physicalParts.Any(part => ReferencesAuthoredArt(part, artGroups))) return;
        throw new InvalidDataException("The tileLayout master is catalog-only; assign artwork to a physical tile.");
    }

    private static bool ReferencesAuthoredArt((int BaseGraphicID, int NumGraphics) definition, HashSet<int> authoredArtGroups)
    {
        // ObjectData.Graphic selects BaseGraphicID + offset; NumGraphics bounds the authored selection range.
        // The unchecked ushort addition matches the resource-ID lookup used by the scene renderer.
        var count = Math.Max(1, definition.NumGraphics);
        for (var offset = 0; offset < count; offset++)
            if (authoredArtGroups.Contains(unchecked((ushort)(definition.BaseGraphicID + offset)))) return true;
        return false;
    }

    /// <summary>
    /// Expands a template's linked tile group to the requested offsets. Sims 1 stores each part's offset as signed
    /// bytes in OBJD.SubIndex (X in the high byte, Y in the low byte); the VM creates every non-lead definition in
    /// that group's IFF when the lead is placed. Repeatedly clone one template tile so large authored objects are not
    /// limited to the template's original part count.
    /// </summary>
    private static List<Chunk> ExpandTileLayout(Chunk lead, List<Chunk> definitions, TilePart[] layout, List<Chunk> chunks)
    {
        if (layout.Length is 0 or > 4095) throw new InvalidDataException("tileLayout must contain between 1 and 4095 parts");
        var donor = definitions.FirstOrDefault(chunk => chunk != lead)
            ?? throw new InvalidDataException("tileLayout needs a multi-tile template with at least one part to clone");
        var offsets = new HashSet<(int X, int Y)>();
        foreach (var part in layout)
        {
            if (part.X is < sbyte.MinValue or > sbyte.MaxValue || part.Y is < sbyte.MinValue or > sbyte.MaxValue)
                throw new InvalidDataException($"tileLayout offset ({part.X}, {part.Y}) is outside the signed SubIndex range -128 through 127");
            if (part.X == -1 && part.Y == -1)
                throw new InvalidDataException("tileLayout offset (-1, -1) is reserved for the lead object");
            if (!offsets.Add((part.X, part.Y)))
                throw new InvalidDataException($"tileLayout repeats offset ({part.X}, {part.Y})");
        }
        var usedIds = chunks.Where(chunk => chunk.Type == "OBJD").Select(chunk => chunk.Id).ToHashSet();
        var donorFunctionTable = chunks.FirstOrDefault(chunk => chunk.Type == "OBJf" && chunk.Id == donor.Id);
        var nextId = 1;
        ushort AllocateId()
        {
            while (nextId <= ushort.MaxValue && usedIds.Contains((ushort)nextId)) nextId++;
            if (nextId > ushort.MaxValue) throw new InvalidDataException("No OBJD chunk IDs remain for tileLayout");
            var id = (ushort)nextId++;
            usedIds.Add(id);
            return id;
        }
        chunks.RemoveAll(chunk => chunk.Type == "OBJD" && chunk != lead);
        var expanded = new List<Chunk>(layout.Length + 1) { lead };
        foreach (var part in layout)
        {
            var clone = new Chunk
            {
                Type = donor.Type,
                Id = AllocateId(),
                Flags = donor.Flags,
                Label = donor.Label,
                Data = donor.Data.ToArray()
            };
            WriteField(clone, "SubIndex", (ushort)(((byte)part.X << 8) | (byte)part.Y));
            chunks.Add(clone);
            // A cloned tile may use OBJf entry points. OBJf is keyed by the
            // OBJD chunk ID, so cloning only OBJD leaves UsesFnTable tiles
            // without an entry table and crashes on Init/placement preview.
            if (donorFunctionTable != null)
            {
                chunks.Add(new Chunk
                {
                    Type = donorFunctionTable.Type,
                    Id = clone.Id,
                    Flags = donorFunctionTable.Flags,
                    Label = donorFunctionTable.Label,
                    Data = donorFunctionTable.Data.ToArray()
                });
            }
            expanded.Add(clone);
        }
        return expanded;
    }

    /// <summary>
    /// Inserts Animate Sim on a known successful return branch, without replacing the source routine or changing its
    /// other paths. Hooks deliberately require a ReturnTrue pointer: a source-specific recipe must identify the exact
    /// stage-completion instruction rather than broadly intercepting every successful exit.
    /// </summary>
    private static object[] ApplyAnimationHooks(AnimationHook[] hooks, List<Chunk> chunks, Dictionary<string, ushort> animations)
    {
        if (hooks.Length == 0) return [];
        var animate = Primitives.FirstOrDefault(item => item?.Name == "animate")
            ?? throw new InvalidDataException("Animate primitive is not registered");
        if (animate.OperandModel != typeof(VMAnimateSimOperand))
            throw new InvalidDataException("Animate primitive has an unexpected operand model");
        var used = new HashSet<(int Routine, int Instruction, string Branch)>();
        var results = new List<object>();
        foreach (var hook in hooks)
        {
            if (hook.Routine is < 0x1000 or >= 0x2000 || hook.Instruction < 0 ||
                (hook.Branch != "true" && hook.Branch != "false") || !used.Add((hook.Routine, hook.Instruction, hook.Branch)))
                throw new InvalidDataException("Invalid or duplicate animation hook target");
            if (!animations.TryGetValue(hook.Animation, out var animationID))
                throw new InvalidDataException($"Animation hook names unknown animation {hook.Animation}");
            var chunk = chunks.SingleOrDefault(item => item.Type == "BHAV" && item.Id == hook.Routine)
                ?? throw new InvalidDataException($"Animation hook routine 0x{hook.Routine:X4} does not exist");
            var bhav = ParseBhav(chunk);
            if (hook.Instruction >= bhav.Instructions.Length || bhav.Instructions.Length >= 253)
                throw new InvalidDataException($"Animation hook instruction {hook.Instruction} is invalid for routine 0x{hook.Routine:X4}");
            var source = bhav.Instructions[hook.Instruction];
            var pointer = hook.Branch == "true" ? source.TruePointer : source.FalsePointer;
            if (pointer != 254)
                throw new InvalidDataException($"Animation hook 0x{hook.Routine:X4}:{hook.Instruction} {hook.Branch} branch must originally return true");
            var operandValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(VMAnimateSimOperand.AnimationID)] = JsonSerializer.SerializeToElement(animationID),
                [nameof(VMAnimateSimOperand.Source)] = JsonSerializer.SerializeToElement("Object")
            };
            var node = new BHAVInstruction
            {
                Opcode = animate.Opcode,
                TruePointer = 254,
                FalsePointer = 254,
                Operand = Operand(animate.OperandModel, AnimationNames(operandValues, animate.OperandModel, animations, "animation hook"), "animation hook")
            };
            var target = bhav.Instructions.Length;
            if (hook.Branch == "true") source.TruePointer = (byte)target;
            else source.FalsePointer = (byte)target;
            bhav.Instructions = [.. bhav.Instructions, node];
            chunk.Data = SerializeBhav(bhav);
            results.Add(new { routine = hook.Routine, instruction = hook.Instruction, branch = hook.Branch, animation = hook.Animation, appendedInstruction = target });
        }
        return results.ToArray();
    }

    /// <summary>
    /// Dispatches Animate Sim XEVTs through event-specific private BHAVs. On an XEVT, Animate Sim returns false after
    /// storing the event in its selected local. Each appended comparison routes matching event values to a callback;
    /// both callback returns and unknown event values resume the same Animate Sim node, which drains any queued events
    /// before eventually taking its original true completion edge.
    /// </summary>
    private static object[] ApplyAnimationEventHooks(AnimationEventHook[] hooks, List<Chunk> chunks,
        Dictionary<string, ushort> behaviorIDs)
    {
        if (hooks.Length == 0) return [];
        var used = new HashSet<(int Routine, int Instruction)>();
        var results = new List<object>();
        foreach (var hook in hooks)
        {
            var routineId = hook.RoutineName is { } name
                ? behaviorIDs.GetValueOrDefault(name)
                : (ushort)hook.Routine;
            if ((hook.RoutineName is null && hook.Routine is < 0x1000 or >= 0x2000) ||
                (hook.RoutineName is not null && routineId == 0) || hook.Instruction < 0 || hook.Local is < 0 or > 255 ||
                hook.Events is not { Length: > 0 } || !used.Add((routineId, hook.Instruction)))
                throw new InvalidDataException("Invalid or duplicate animation event hook target");
            var chunk = chunks.SingleOrDefault(item => item.Type == "BHAV" && item.Id == routineId)
                ?? throw new InvalidDataException($"Animation event hook routine 0x{routineId:X4} does not exist");
            var bhav = ParseBhav(chunk);
            if (hook.Instruction >= bhav.Instructions.Length)
                throw new InvalidDataException($"Animation event hook instruction {hook.Instruction} is invalid for routine 0x{routineId:X4}");
            var animate = Primitives.FirstOrDefault(item => item?.Name == "animate")
                ?? throw new InvalidDataException("Animate primitive is not registered");
            var source = bhav.Instructions[hook.Instruction];
            if (source.Opcode != animate.Opcode || (source.Operand[5] & 0x20) == 0 || source.Operand[2] != hook.Local)
                throw new InvalidDataException($"Animation event hook 0x{routineId:X4}:{hook.Instruction} must be Animate Sim with StoreEventInLocal to local {hook.Local}");
            if (hook.Local >= bhav.Locals)
                throw new InvalidDataException($"Animation event hook local {hook.Local} is outside routine 0x{routineId:X4}'s {bhav.Locals} locals");

            var values = new HashSet<int>();
            foreach (var item in hook.Events)
            {
                if (item.Value is < 0 or > short.MaxValue || !values.Add(item.Value))
                    throw new InvalidDataException($"Animation event hook 0x{routineId:X4}:{hook.Instruction} has an invalid or duplicate XEVT value");
                if (!behaviorIDs.ContainsKey(item.Behavior))
                    throw new InvalidDataException($"Animation event hook names unknown behavior {item.Behavior}");
            }

            // Instructions 253–255 are BHAV return/failure sentinels, leaving 253 authored nodes at most.
            var required = checked(hook.Events.Length * 2);
            if (bhav.Instructions.Length + required > 253)
                throw new InvalidDataException($"Animation event hook 0x{routineId:X4}:{hook.Instruction} exceeds the BHAV instruction limit");
            var first = bhav.Instructions.Length;
            var resume = hook.Instruction;
            var dispatch = new BHAVInstruction[required];
            for (var index = 0; index < hook.Events.Length; index++)
            {
                var compareIndex = first + index * 2;
                var callIndex = compareIndex + 1;
                var nextCompare = index + 1 < hook.Events.Length ? callIndex + 1 : resume;
                var eventValue = hook.Events[index].Value;
                // Expression: Local[hook.Local] == Literal[eventValue]. The XEVT is a signed 16-bit value.
                byte[] operand = [
                    (byte)hook.Local, 0,
                    (byte)eventValue, (byte)(eventValue >> 8),
                    0, 2,
                    25, 7 // DataOwner.Local, DataOwner.Literal
                ];
                dispatch[index * 2] = new BHAVInstruction
                {
                    Opcode = 2,
                    TruePointer = (byte)callIndex,
                    FalsePointer = (byte)nextCompare,
                    Operand = operand
                };
                var callback = behaviorIDs[hook.Events[index].Behavior];
                dispatch[index * 2 + 1] = new BHAVInstruction
                {
                    Opcode = callback,
                    TruePointer = (byte)resume,
                    FalsePointer = (byte)resume,
                    Operand = new byte[8]
                };
            }
            source.FalsePointer = (byte)first;
            bhav.Instructions = [.. bhav.Instructions, .. dispatch];
            chunk.Data = SerializeBhav(bhav);
            results.Add(new
            {
                routine = routineId,
                routineName = hook.RoutineName,
                instruction = hook.Instruction,
                local = hook.Local,
                resumeInstruction = resume,
                events = hook.Events.Select(item => new { value = item.Value, behavior = item.Behavior, compareInstruction = first + Array.IndexOf(hook.Events, item) * 2 }).ToArray()
            });
        }
        return results.ToArray();
    }

    /// <summary>
    /// Skips the template's calls to the listed routines: every branch to such a call goes to the call's true branch instead,
    /// as if the call had returned true. Vacation's Frost Fortress, for one, calls global 423 "Allow on Vacation Only" from its
    /// main and removes itself from any other lot. The calls stay in place, unreachable, so the routines keep their layout.
    /// A call that starts a routine becomes an always-true expression instead. Returns the number of calls skipped. A listed
    /// ID that is never called is rejected.
    /// </summary>
    private static int DropCalls(List<Chunk> chunks, int[] routines)
    {
        foreach (var routine in routines)
            if (routine is < 0x100 or > 0xFFFF) throw new InvalidDataException($"dropCalls entry {routine} is not a routine ID (0x100 to 0xFFFF)");
        var dropped = 0;
        var found = new HashSet<int>();
        foreach (var chunk in chunks.Where(chunk => chunk.Type == "BHAV" && chunk.Data.Length >= 12 && routines.Length > 0))
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data);
            if (version is < 0x8000 or > 0x8003) continue;
            var start = version == 0x8003 ? 13 : 12;
            var count = Math.Min(version == 0x8003 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.Data.AsSpan(9)) : BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data.AsSpan(2)),
                (chunk.Data.Length - start) / 12);
            int Opcode(int index) => BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data.AsSpan(start + index * 12));
            bool Skipped(int index) => index < count && routines.Contains(Opcode(index));
            int Target(int pointer)
            {
                // Follow a chain of skipped calls; a loop of them is left to the original call.
                for (var steps = 0; steps < count && Skipped(pointer); steps++) pointer = chunk.Data[start + pointer * 12 + 2];
                return pointer;
            }
            if (count > 0 && Skipped(0))
            {
                // Nothing can jump past a routine's first instruction, so a call there becomes an expression that is always
                // true (Literal 0 == Literal 0) and continues where the call's true branch went (the igloo's load tree).
                found.Add(Opcode(0));
                dropped++;
                var first = chunk.Data.AsSpan(start, 12);
                BinaryPrimitives.WriteUInt16LittleEndian(first, 2);
                first[3] = first[2];
                ReadOnlySpan<byte> always = [0, 0, 0, 0, 0, 2, 7, 7];
                always.CopyTo(first[4..]);
            }
            for (var index = 0; index < count; index++)
            {
                if (Skipped(index)) { found.Add(Opcode(index)); dropped++; }
                for (var offset = 2; offset <= 3; offset++)
                    chunk.Data[start + index * 12 + offset] = (byte)Target(chunk.Data[start + index * 12 + offset]);
            }
        }
        foreach (var routine in routines.Where(routine => !found.Contains(routine)))
            throw new InvalidDataException($"dropCalls entry {routine} is never called by the template's routines");
        return dropped;
    }

    /// <summary>
    /// Makes every definition run the named behavior when it is created, then its own init (function table entry 0, or the
    /// definition's BHAV_Init field when it has no table). One wrapper routine is added per distinct init. Vacation's igloo
    /// and Frost Fortress, for one, never set "cannot be stolen" (flag 8 of FlagField2), since no burglar visits a vacation
    /// lot; on a home lot the thief's "can item be stolen" (NPC Controller 4115) would carry them off. Returns the number of
    /// definitions changed.
    /// </summary>
    private static int WrapInit(List<Chunk> chunks, List<Chunk> definitions, string name, Dictionary<string, ushort> behaviorIDs, HashSet<ushort> existing, ref ushort nextID)
    {
        if (!behaviorIDs.TryGetValue(name, out var behavior)) throw new InvalidDataException($"initBehavior {name} is not one of the object's behaviors");
        var wrappers = new Dictionary<ushort, ushort>();
        var changed = 0;
        foreach (var definition in definitions)
        {
            var table = ReadField(definition, "UsesFnTable") != 0 ? chunks.FirstOrDefault(chunk => chunk.Type == "OBJf" && chunk.Id == definition.Id) : null;
            // OBJf: pad, version, magic, count (4 bytes each), then (condition, action) pairs; entry 0 is init.
            if (table != null && (table.Data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(table.Data.AsSpan(12)) == 0)) table = null;
            var init = table != null ? BinaryPrimitives.ReadUInt16LittleEndian(table.Data.AsSpan(18)) : ReadField(definition, "BHAV_Init");
            if (!wrappers.TryGetValue(init, out var wrapper))
            {
                wrapper = behavior;
                if (init >= 0x100)
                {
                    if (nextID >= 0x2000) throw new InvalidDataException("No private behavior IDs are left in the template");
                    wrapper = nextID++;
                    var instructions = new[]
                    {
                        new Instruction { Call = JsonSerializer.SerializeToElement(behavior), True = JsonSerializer.SerializeToElement(1), False = JsonSerializer.SerializeToElement(1) },
                        new Instruction { Call = JsonSerializer.SerializeToElement(init), True = JsonSerializer.SerializeToElement("true"), False = JsonSerializer.SerializeToElement("false") }
                    };
                    var label = $"{name} then init {init}";
                    var bhav = Assemble(new Behavior { Name = label, Instructions = instructions }, behaviorIDs, [.. existing, behavior], [], []);
                    chunks.Add(new Chunk { Type = "BHAV", Id = wrapper, Flags = 0x10, Label = Label(label), Data = SerializeBhav(bhav) });
                }
                wrappers[init] = wrapper;
            }
            if (table != null) BinaryPrimitives.WriteUInt16LittleEndian(table.Data.AsSpan(18), wrapper);
            else WriteField(definition, "BHAV_Init", wrapper);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Points the template's own routines at the new GUIDs. An object that finds itself or its parts by type (Test Object
    /// Type, Set to Next of a type, Create Object Instance; the Frost Fortress's "Get Control Piece", for one) would
    /// otherwise look for the original object and fail. Operands are patched in place so the routines keep their version.
    /// Returns the number of instructions changed.
    /// </summary>
    private static int RetargetBehaviors(List<Chunk> chunks, Dictionary<uint, uint> guids)
    {
        var changed = 0;
        foreach (var chunk in chunks.Where(chunk => chunk.Type == "BHAV" && chunk.Data.Length >= 12))
        {
            var version = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data);
            if (version is < 0x8000 or > 0x8003) continue;
            var start = version == 0x8003 ? 13 : 12;
            var count = version == 0x8003 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.Data.AsSpan(9)) : BinaryPrimitives.ReadUInt16LittleEndian(chunk.Data.AsSpan(2));
            for (var index = 0; index < count && start + index * 12 + 12 <= chunk.Data.Length; index++)
            {
                var instruction = chunk.Data.AsSpan(start + index * 12, 12);
                var opcode = BinaryPrimitives.ReadUInt16LittleEndian(instruction);
                // 31 Set to Next (only its Object of Type search reads the GUID), 32 Test Object Type, 42 Create Object Instance.
                if (opcode is not (31 or 32 or 42) || (opcode == 31 && (instruction[8] & 0x7F) != 4)) continue;
                if (!guids.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(instruction[4..]), out var guid)) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(instruction[4..], guid);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Picks the template object to copy: its lead definition and, for a multi-tile object, every part sharing its master
    /// ID. Definitions and catalog strings of the file's other objects are dropped so their original GUIDs never load twice.
    /// </summary>
    private static (Chunk Lead, List<Chunk> Definitions) SelectObject(ObjectSpec spec, List<Chunk> chunks)
    {
        var all = chunks.Where(chunk => chunk.Type == "OBJD").ToList();
        if (all.Count == 0) throw new InvalidDataException($"Template {spec.Template} holds no object definition");
        uint Guid(Chunk chunk) => ReadField(chunk, "GUID1") | (uint)ReadField(chunk, "GUID2") << 16;
        bool Master(Chunk chunk) => ReadField(chunk, "SubIndex") == 0xFFFF;
        List<Chunk> Group(Chunk lead) => Master(lead) ? all.Where(chunk => ReadField(chunk, "MasterID") == ReadField(lead, "MasterID")).ToList() : [lead];
        var leads = all.Where(chunk => Master(chunk) || ReadField(chunk, "MasterID") == 0).ToList();
        Chunk selected;
        if (spec.TemplateObject is { } wanted)
        {
            var guid = ParseGuid(wanted, "template object");
            selected = leads.FirstOrDefault(chunk => Guid(chunk) == guid)
                ?? throw new InvalidDataException($"Template {spec.Template} has no object 0x{guid:X8}; it has {string.Join(", ", leads.Select(chunk => $"0x{Guid(chunk):X8} ({chunk.Label})"))}");
        }
        else if (leads.Count == 1) selected = leads[0];
        else throw new InvalidDataException($"Template {spec.Template} holds several objects; choose one with templateObject: {string.Join(", ", leads.Select(chunk => $"0x{Guid(chunk):X8} ({chunk.Label})"))}");
        var definitions = Group(selected);
        // Parts are listed in their original chunk order, lead first.
        definitions.Remove(selected);
        definitions.Insert(0, selected);
        var catalogs = definitions.Select(chunk => ReadField(chunk, "CatalogStringsID")).ToHashSet();
        chunks.RemoveAll(chunk => (chunk.Type == "OBJD" && !definitions.Contains(chunk)) || (chunk.Type == "CTSS" && !catalogs.Contains(chunk.Id)));
        return (selected, definitions);
    }

    /// <summary>
    /// Appends the document's clips to the object's animation tables: STR# AnimationTableID for adults and the next ID for
    /// children (129 and 130 when the definition names none), the tables Animate Sim reads with its Object source. Both
    /// tables get the same index, so one animate instruction plays the right clip for either age. Returns each name's index.
    /// </summary>
    private static Dictionary<string, ushort> AddAnimations(ObjectSpec spec, Chunk lead, List<Chunk> chunks)
    {
        var indices = new Dictionary<string, ushort>(StringComparer.Ordinal);
        if ((spec.Animations ?? []).Length == 0) return indices;
        if (spec.Animations!.Length > 200) throw new InvalidDataException("An object can add at most 200 animations");
        static string Clip(string? value, string what)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                value.Any(character => character is < '!' or > '~'))
                throw new InvalidDataException($"Invalid {what}: clip names are 1 to 64 printable ASCII characters without spaces or .anim");
            return value;
        }
        var adultID = ReadField(lead, "AnimationTableID") is var id and not 0 ? id : (ushort)129;
        STR Table(ushort tableID, out Chunk chunk)
        {
            chunk = chunks.FirstOrDefault(item => item.Type == "STR#" && item.Id == tableID)!;
            if (chunk == null)
            {
                chunk = new Chunk { Type = "STR#", Id = tableID, Flags = 0x10, Label = tableID == adultID ? "a2o" : "c2o", Data = [] };
                chunks.Add(chunk);
                return new STR();
            }
            return ParseStrings(chunk);
        }
        var adult = Table(adultID, out var adultChunk);
        var child = Table((ushort)(adultID + 1), out var childChunk);
        int Length(STR table) => table.LanguageSets[0]?.Strings.Length ?? 0;
        // Index 0 of an animation table is never played; keep the tables aligned from the first free index of either.
        var next = Math.Max(1, Math.Max(Length(adult), Length(child)));
        var used = new HashSet<int>();
        foreach (var animation in spec.Animations)
        {
            if (string.IsNullOrWhiteSpace(animation.Name) || animation.Name.Length > 60 || indices.ContainsKey(animation.Name))
                throw new InvalidDataException($"Animation names must be unique, nonempty and at most 60 characters: {animation.Name}");
            if (next > short.MaxValue) throw new InvalidDataException("The animation tables are full");
            var adultClip = Clip(animation.Adult, $"adult clip for {animation.Name}");
            var index = animation.Index ?? next;
            if (index < 1 || index > short.MaxValue || !used.Add(index))
                throw new InvalidDataException($"Invalid or duplicate animation-table index {index} for {animation.Name}");
            if (animation.Index.HasValue && (index >= Length(adult) || index >= Length(child)))
                throw new InvalidDataException($"Animation-table override {index} must replace an existing adult and child entry");
            Set(adult, index, adultClip);
            Set(child, index, Clip(animation.Child ?? adultClip, $"child clip for {animation.Name}"));
            indices[animation.Name] = (ushort)index;
            if (!animation.Index.HasValue) next++;
        }
        if (Length(adult) == 0 || Length(child) == 0) throw new InvalidDataException("Animation tables could not be written");
        adultChunk.Data = SerializeStrings(adult);
        childChunk.Data = SerializeStrings(child);
        return indices;
    }

    private static BHAV Assemble(Behavior behavior, Dictionary<string, ushort> behaviorIDs, HashSet<ushort> existing,
        Dictionary<string, ushort> animations, string[] nativeCapabilities)
    {
        if (behavior.Instructions == null || behavior.Instructions.Length is < 1 or > 253)
            throw new InvalidDataException($"Behavior {behavior.Name} needs 1 to 253 instructions");
        if (behavior.Args is < 0 or > 4 || behavior.Locals is < 0 or > 255)
            throw new InvalidDataException($"Behavior {behavior.Name} takes 0 to 4 arguments and 0 to 255 locals");
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < behavior.Instructions.Length; index++)
            if (behavior.Instructions[index].Label is { } label && !labels.TryAdd(label, index))
                throw new InvalidDataException($"Behavior {behavior.Name} repeats label {label}");
        var instructions = new BHAVInstruction[behavior.Instructions.Length];
        for (var index = 0; index < instructions.Length; index++)
        {
            var source = behavior.Instructions[index];
            var where = $"{behavior.Name} instruction {index}";
            var forms = (source.Primitive != null ? 1 : 0) + (source.Call != null ? 1 : 0) +
                        (source.Opcode != null ? 1 : 0) + (source.NativeOperation != null ? 1 : 0);
            if (forms != 1) throw new InvalidDataException($"{where} needs exactly one of primitive, nativeOperation, call or opcode");
            ushort opcode;
            var operand = new byte[8];
            if (source.Call is { } call)
            {
                opcode = Resolve(call, behaviorIDs, existing, where);
                if (source.Operand != null || source.OperandHex != null) throw new InvalidDataException($"{where}: a call takes args, not an operand");
                var args = source.Args ?? [];
                if (args.Length > 4) throw new InvalidDataException($"{where}: a call takes at most four arguments");
                for (var argument = 0; argument < args.Length; argument++) BinaryPrimitives.WriteInt16LittleEndian(operand.AsSpan(argument * 2), args[argument]);
            }
            else if (source.NativeOperation is { } nativeOperation)
            {
                if (!nativeCapabilities.Contains("rink.v1", StringComparer.Ordinal))
                    throw new InvalidDataException($"{where}: native operation {nativeOperation} requires nativeCapabilities [\"rink.v1\"]");
                if (source.Args != null) throw new InvalidDataException($"{where}: native operations do not take call args");
                opcode = nativeOperation switch
                {
                    "acquire_interaction_lane" => 0xF0,
                    "release_interaction_lane" => 0xF1,
                    "set_interaction_attachment" => 0xF2,
                    "interaction_interrupt_requested" => 0xF3,
                    _ => throw new InvalidDataException($"{where}: unsupported native operation {nativeOperation}")
                };
                operand = NativeOperationOperand(nativeOperation, source.Operand, source.OperandHex, behavior, where);
            }
            else if (source.Primitive is { } name)
            {
                var registration = Primitives.FirstOrDefault(item => item != null && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{where}: unknown primitive {name}");
                if (source.Args != null) throw new InvalidDataException($"{where}: only calls take args");
                opcode = registration.Opcode;
                if (source.OperandHex != null) operand = Hex(source.OperandHex, where);
                else if (registration.OperandModel != null) operand = Operand(registration.OperandModel, AnimationNames(source.Operand ?? [], registration.OperandModel, animations, where), where);
                else if (source.Operand is { Count: > 0 }) throw new InvalidDataException($"{where}: {name} has no operand model; use operandHex");
            }
            else
            {
                if (source.Opcode is not (>= 0 and < 256) || Primitives[source.Opcode.Value] == null)
                    throw new InvalidDataException($"{where}: opcode must name an installed primitive");
                if (source.Operand != null || source.Args != null) throw new InvalidDataException($"{where}: a raw opcode takes operandHex");
                opcode = (ushort)source.Opcode.Value;
                if (source.OperandHex != null) operand = Hex(source.OperandHex, where);
            }
            instructions[index] = new BHAVInstruction
            {
                Opcode = opcode,
                TruePointer = Pointer(source.True, index, instructions.Length, labels, true, where),
                FalsePointer = Pointer(source.False, index, instructions.Length, labels, false, where),
                Operand = operand
            };
        }
        return new BHAV { Instructions = instructions, Type = 0, Args = (byte)behavior.Args, Locals = (ushort)behavior.Locals, Version = 0 };
    }

    private static byte[] NativeOperationOperand(string name, Dictionary<string, JsonElement>? values, string? hex,
        Behavior behavior, string where)
    {
        if (hex != null || values == null) throw new InvalidDataException($"{where}: {name} requires its explicit operand object");
        var operand = new byte[8];
        switch (name)
        {
            case "acquire_interaction_lane":
                if (values.Count != 2 || !values.TryGetValue("laneCount", out var lanes) || !lanes.TryGetInt32(out var count) || count is < 1 or > 8 ||
                    !values.TryGetValue("local", out var local) || !local.TryGetInt32(out var localIndex) || localIndex < 0 || localIndex >= behavior.Locals)
                    throw new InvalidDataException($"{where}: acquire_interaction_lane expects laneCount 1..8 and a valid local index");
                operand[0] = (byte)count;
                operand[1] = (byte)localIndex;
                break;
            case "release_interaction_lane":
                if (values.Count != 0) throw new InvalidDataException($"{where}: release_interaction_lane takes no operands");
                break;
            case "set_interaction_attachment":
                if (values.Count != 2 || !values.TryGetValue("attachment", out var attachment) || attachment.GetString() != "skates" ||
                    !values.TryGetValue("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"{where}: set_interaction_attachment expects attachment=skates and a boolean visible value");
                operand[0] = 1;
                operand[1] = visible.GetBoolean() ? (byte)1 : (byte)0;
                break;
            case "interaction_interrupt_requested":
                if (values.Count != 0) throw new InvalidDataException($"{where}: interaction_interrupt_requested takes no operands");
                break;
        }
        return operand;
    }

    private static void AddNativeCapabilities(string[] capabilities, List<Chunk> chunks)
    {
        if (capabilities.Length == 0) return;
        if (capabilities.Length != 1 || capabilities[0] != "rink.v1")
            throw new InvalidDataException($"Unsupported native capability set: {string.Join(",", capabilities)}");
        if (chunks.Any(chunk => chunk.Type == "NLCF")) throw new InvalidDataException("Template already contains reserved NLCF capability resource");
        // NLCF is a repository-private extension resource, distinct from all Sims IFF resource types.
        // Body format: ASCII S1LC, schema byte 1, supported-operation bit mask 0b1111, two reserved zero bytes.
        chunks.Add(new Chunk { Type = "NLCF", Id = 0, Flags = 0x10, Label = "Native lifecycle rink.v1",
            Data = [0x53, 0x31, 0x4c, 0x43, 1, 15, 0, 0] });
    }

    /// <summary>Compiles an authored catalog PNG to the original BMP_ format.</summary>
    private static void ApplyCatalogIcon(string relative, List<Chunk> chunks, ushort id, string? baseDirectory)
    {
        if (baseDirectory == null || Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("Catalog icon must be relative to the document");
        var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Catalog icon is missing, outside the document directory or too large");
        var (width, height, rgba) = SpriteArt.ReadPng(File.ReadAllBytes(path), relative);
        if (width is < 1 or > 256 || height is < 1 or > 256) throw new InvalidDataException("Catalog icon must be at most 256x256");
        // Original BMP_ resources contain ordinary BMP files. Preserve the original
        // magenta transparency key; 24-bit rows are padded and stored bottom first.
        var stride = (width * 3 + 3) & ~3;
        var data = new byte[54 + stride * height];
        data[0] = (byte)'B'; data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(34), stride * height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var source = (y * width + x) * 4;
            var target = 54 + (height - 1 - y) * stride + x * 3;
            var visible = rgba[source + 3] >= 64;
            data[target] = visible ? rgba[source + 2] : (byte)255;
            data[target + 1] = visible ? rgba[source + 1] : (byte)0;
            data[target + 2] = visible ? rgba[source] : (byte)255;
        }
        var chunk = chunks.FirstOrDefault(chunk => chunk.Type == "BMP_" && chunk.Id == id)
            ?? throw new InvalidDataException("Template has no catalog icon");
        chunk.Data = data;
    }

    /// <summary>
    /// Some original runtime-only objects (for example TrashPile.iff) have no catalog resources. When a port gives such
    /// an object catalog text or art, allocate the paired CTSS/BMP_ resources instead of requiring a catalog-ready
    /// template. The caller must provide a catalog icon so the new BMP_ is valid.
    /// </summary>
    private static void EnsureCatalogResources(ObjectSpec spec, List<Chunk> chunks, Chunk lead, List<(STRLangCode Language, Translation Text)> translations)
    {
        var wantsCatalog = spec.CatalogName != null || spec.Description != null || spec.CatalogIcon != null ||
            translations.Any(item => item.Text.CatalogName != null || item.Text.Description != null);
        if (!wantsCatalog) return;
        var id = ReadField(lead, "CatalogStringsID");
        var strings = chunks.FirstOrDefault(chunk => chunk.Type == "CTSS" && chunk.Id == id);
        if (strings == null)
        {
            if (spec.CatalogIcon == null)
                throw new InvalidDataException($"Template {spec.Template} has no catalog strings; give catalogIcon when adding catalog text");
            id = (ushort)Enumerable.Range(1, ushort.MaxValue).FirstOrDefault(candidate => !chunks.Any(chunk => chunk.Id == candidate));
            if (id == 0) throw new InvalidDataException("No IFF chunk IDs are available for catalog resources");
            WriteField(lead, "CatalogStringsID", id);
            strings = new Chunk { Type = "CTSS", Id = id, Flags = 0x10, Label = "Authored catalog strings", Data = SerializeStrings(new STR()) };
            chunks.Add(strings);
        }
        if (spec.CatalogIcon != null && !chunks.Any(chunk => chunk.Type == "BMP_" && chunk.Id == id))
            chunks.Add(new Chunk { Type = "BMP_", Id = id, Flags = 0x10, Label = "Authored catalog icon", Data = [] });
    }

    /// <summary>Writes complete DGRP v20004 views using the original SPR2/PALT containers.</summary>
    private static object ApplyGraphicSet(GraphicSet set, List<Chunk> chunks, string? baseDirectory)
    {
        if (baseDirectory == null) throw new InvalidDataException("Graphics need a document directory");
        if (set.Groups == null || set.Groups.Length is < 1 or > 64 || set.Groups.Any(id => id is < 1 or > 65535)
            || set.Groups.Distinct().Count() != set.Groups.Length)
            throw new InvalidDataException("Graphics groups must be unique valid chunk IDs");
        if (set.Variants.Length > 768 || set.Variants.Any(v => v.Group is < 1 or > 65535)
            || set.EmptyGroups.Length > 64 || set.EmptyGroups.Any(id => id is < 1 or > 65535)
            || set.EmptyGroups.Concat(set.Groups).Concat(set.Variants.Select(v => v.Group)).Distinct().Count()
                != set.EmptyGroups.Length + set.Groups.Length + set.Variants.Length)
            throw new InvalidDataException("Graphics groups and variants must have unique valid chunk IDs");
        static void CheckViews(GraphicView[] views)
        {
            if (views == null || views.Length != 12 || views.Any(v => v.Direction is not (1 or 4 or 16 or 64) || v.Zoom is < 1 or > 3)
                || views.Select(v => (v.Direction, v.Zoom)).Distinct().Count() != 12)
                throw new InvalidDataException("Graphics require all four directions at all three zooms exactly once");
            foreach (var view in views)
            foreach (var layer in view.Layers ?? [])
            {
                if (view.Layers!.Length > 4) throw new InvalidDataException("Graphics views support at most four baked layers");
                if (layer.SpriteOffset == null || layer.SpriteOffset.Length != 2 || layer.SpriteOffset.Any(v => v is < -4096 or > 4096))
                    throw new InvalidDataException("Baked graphics layers require a bounded two-coordinate spriteOffset");
                if (layer.ObjectOffset != null && (layer.ObjectOffset.Length != 3 || layer.ObjectOffset.Any(v => !float.IsFinite(v) || v is < -256 or > 256)))
                    throw new InvalidDataException("Baked graphics layer objectOffset must be three bounded coordinates");
            }
        }
        CheckViews(set.Views);
        foreach (var variant in set.Variants) CheckViews(variant.Views);
        var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        byte[] Load(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Graphics paths must be relative");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Graphics image is missing, oversized or outside the document directory");
            return File.ReadAllBytes(path);
        }
        var frames = new List<SpriteArt.Frame>();
        var frameIndices = new Dictionary<(string Image, string Depth), int>();
        var viewSets = new[] { set.Views }.Concat(set.Variants.Select(v => v.Views)).ToArray();
        foreach (var view in viewSets.SelectMany(v => v))
        {
            _ = AddFrame(view.Image, view.Depth);
            foreach (var layer in view.Layers ?? []) _ = AddFrame(layer.Image, layer.Depth);
        }

        int AddFrame(string imagePath, string depthPath)
        {
            if (frameIndices.TryGetValue((imagePath, depthPath), out var existing)) return existing;
            var (width, height, rgba) = SpriteArt.ReadPng(Load(imagePath), imagePath);
            var (dw, dh, depth) = SpriteArt.ReadPng(Load(depthPath), depthPath);
            if (dw != width || dh != height) throw new InvalidDataException("Graphics depth must match color dimensions");
            if (!Enumerable.Range(0, width * height).Any(p => rgba[p * 4 + 3] > 0))
                throw new InvalidDataException("Graphics view is empty");
            var z = new byte[width * height];
            for (var p = 0; p < z.Length; p++) z[p] = rgba[p * 4 + 3] == 0 ? (byte)255 : Math.Min(depth[p * 4], (byte)254);
            var index = frames.Count;
            frames.Add(new SpriteArt.Frame { Width = width, Height = height, Rgba = rgba, Depth = z });
            frameIndices[(imagePath, depthPath)] = index;
            return index;
        }
        var spriteID = chunks.Where(c => c.Type == "SPR2").Select(c => (int)c.Id + 1).DefaultIfEmpty(1).Max();
        var paletteID = chunks.Where(c => c.Type == "PALT").Select(c => (int)c.Id + 1).DefaultIfEmpty(1).Max();
        if (spriteID > ushort.MaxValue || paletteID > ushort.MaxValue) throw new InvalidDataException("No free graphics chunk IDs");
        var colors = SpriteArt.Quantize(frames);
        var palette = new byte[16 + 768];
        BinaryPrimitives.WriteUInt32LittleEndian(palette, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(palette.AsSpan(4), 256);
        colors.CopyTo(palette, 16);
        chunks.Add(new Chunk { Type = "PALT", Id = (ushort)paletteID, Flags = 0x10, Label = "Authored graphics", Data = palette });
        chunks.Add(new Chunk { Type = "SPR2", Id = (ushort)spriteID, Flags = 0x10, Label = "Authored graphics",
            Data = SpriteArt.EncodeSprite(frames, (ushort)paletteID, colors) });
        foreach (var id in set.Groups.Concat(set.EmptyGroups).Concat(set.Variants.Select(v => v.Group)))
        {
            var group = new DGRP();
            var variantIndex = Array.FindIndex(set.Variants, v => v.Group == id);
            var drawn = set.Groups.Contains(id) || variantIndex >= 0;
            var selectedViews = variantIndex >= 0 ? set.Variants[variantIndex].Views : set.Views;
            group.Images = selectedViews.Select(view =>
            {
                var image = new DGRPImage { Direction = (uint)view.Direction, Zoom = (uint)view.Zoom };
                if (!drawn) image.Sprites = [];
                else
                {
                    var sprites = new List<DGRPSprite>
                    {
                        MakeSprite(view.SpriteOffset, view.ObjectOffset, frameIndices[(view.Image, view.Depth)])
                    };
                    foreach (var layer in view.Layers ?? [])
                        sprites.Add(MakeSprite(layer.SpriteOffset, layer.ObjectOffset, frameIndices[(layer.Image, layer.Depth)]));
                    image.Sprites = sprites.ToArray();
                }
                return image;
            }).ToArray();

            DGRPSprite MakeSprite(int[] spriteOffset, float[]? objectOffset, int frame) => new()
            {
                SpriteID = (uint)spriteID,
                SpriteFrameIndex = (uint)frame,
                SpriteOffset = new Vector2(spriteOffset[0], spriteOffset[1]),
                ObjectOffset = objectOffset is { } o ? new Vector3(o[0], o[1], o[2]) : default
            };
            var chunk = chunks.FirstOrDefault(c => c.Type == "DGRP" && c.Id == id);
            if (chunk == null) chunks.Add(new Chunk { Type = "DGRP", Id = (ushort)id, Flags = 0x10, Label = "Authored graphics", Data = SerializeDgrp(group) });
            else chunk.Data = SerializeDgrp(group);
        }
        return new { groups = set.Groups, emptyGroups = set.EmptyGroups, variants = set.Variants.Length,
            sprite = spriteID, palette = paletteID, views = frames.Count };
    }

    /// <summary>Replaces SPR2 frames, sharing one new palette per edited chunk.</summary>
    private static object ApplySprites(SpriteImage[] images, List<Chunk> chunks, string? baseDirectory)
    {
        if (images.Length > 4096) throw new InvalidDataException("Too many sprite images");
        if (baseDirectory == null) throw new InvalidDataException("Sprite images need a document directory");
        var root = Path.GetFullPath(baseDirectory);
        byte[] Load(string relative, string what)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException($"{what} must be a path relative to the document");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"{what} {relative} is outside the document directory");
            if (!File.Exists(path)) throw new InvalidDataException($"{what} {relative} does not exist");
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException($"{what} {relative} exceeds size limit");
            return File.ReadAllBytes(path);
        }
        var palettes = new List<object>();
        var nextPalette = chunks.Where(chunk => chunk.Type == "PALT").Select(chunk => chunk.Id + 1).DefaultIfEmpty(1).Max();
        foreach (var group in images.GroupBy(image => image.Sprite))
        {
            var chunk = chunks.FirstOrDefault(chunk => chunk.Type == "SPR2" && chunk.Id == group.Key)
                ?? throw new InvalidDataException($"The template has no sprite {group.Key}");
            var (_, frames) = SpriteArt.DecodeSprite(chunk.Data, Palette(chunks));
            var replaced = new HashSet<int>();
            foreach (var image in group)
            {
                var where = $"sprite {image.Sprite} frame {image.Frame}";
                if (image.Frame < 0 || image.Frame >= frames.Count) throw new InvalidDataException($"Sprite {image.Sprite} has no frame {image.Frame}");
                if (!replaced.Add(image.Frame)) throw new InvalidDataException($"The document paints {where} twice");
                var frame = frames[image.Frame];
                var (width, height, rgba) = SpriteArt.ReadPng(Load(image.Image, "Image"), image.Image);
                if ((width != frame.Width || height != frame.Height) && image.SpriteOffset == null)
                    throw new InvalidDataException($"{image.Image} is {width}x{height}; {where} is {frame.Width}x{frame.Height}");
                if (image.SpriteOffset is { } offset)
                {
                    if (offset.Length != 2 || offset.Any(value => value is < -4096 or > 4096) || image.Depth == null)
                        throw new InvalidDataException("Resized sprites require a two-coordinate offset and depth map");
                    foreach (var drawChunk in chunks.Where(c => c.Type == "DGRP"))
                    {
                        var draw = ParseDgrp(drawChunk);
                        var changed = false;
                        foreach (var use in draw.Images.SelectMany(i => i.Sprites))
                            if (use.SpriteID == image.Sprite && use.SpriteFrameIndex == image.Frame)
                            {
                                // Mirrored uses anchor the flipped frame, as in the shipped graphics.
                                use.SpriteOffset = new Vector2(use.Flip ? -width - offset[0] - 1 : offset[0], offset[1]);
                                changed = true;
                            }
                        if (changed) drawChunk.Data = SerializeDgrp(draw);
                    }
                }
                var (originalRgba, originalDepth) = (frame.Rgba, frame.Depth);
                frame.Width = width; frame.Height = height;
                frame.Rgba = rgba;
                frame.Depth = new byte[width * height];
                if (image.Depth is { } depthPath)
                {
                    var (depthWidth, depthHeight, depth) = SpriteArt.ReadPng(Load(depthPath, "Depth"), depthPath);
                    if (depthWidth != width || depthHeight != height) throw new InvalidDataException($"{depthPath} does not match the size of {image.Image}");
                    for (var pixel = 0; pixel < width * height; pixel++) frame.Depth[pixel] = rgba[pixel * 4 + 3] == 0 ? (byte)255 : Math.Min(depth[pixel * 4], (byte)254);
                }
                else SpriteArt.FillDepth(frame, originalDepth, originalRgba);
            }
            if (nextPalette > ushort.MaxValue) throw new InvalidDataException("No palette IDs are left in the template");
            var paletteID = (ushort)nextPalette++;
            var colors = SpriteArt.Quantize(frames);
            var palette = new byte[16 + 768];
            BinaryPrimitives.WriteUInt32LittleEndian(palette, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(palette.AsSpan(4), 256);
            colors.CopyTo(palette, 16);
            chunks.Add(new Chunk { Type = "PALT", Id = paletteID, Flags = 0x10, Label = chunk.Label, Data = palette });
            chunk.Data = SpriteArt.EncodeSprite(frames, paletteID, colors);
            palettes.Add(new { sprite = group.Key, frames = replaced.Order().ToArray(), palette = paletteID });
        }
        return palettes;
    }

    private static Func<ushort, byte[]?> Palette(List<Chunk> chunks) => id =>
    {
        var chunk = chunks.FirstOrDefault(chunk => chunk.Type == "PALT" && chunk.Id == id);
        if (chunk == null || chunk.Data.Length < 16) return null;
        var colors = new byte[768];
        var count = (int)Math.Min(Math.Min(BitConverter.ToUInt32(chunk.Data, 4), 256u), (uint)(chunk.Data.Length - 16) / 3);
        Array.Copy(chunk.Data, 16, colors, 0, count * 3);
        return colors;
    };

    /// <summary>
    /// Writes every SPR2 frame of an original object file as a color PNG and a grayscale depth PNG, with a sprites.json
    /// manifest naming the draw groups and objects that use each frame. Paint the color PNGs and list them in an object's
    /// "sprites" to compile them back.
    /// </summary>
    internal static object ExportSprites(string template, string game, string destination)
    {
        var output = Path.GetFullPath(destination);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidDataException("Sprite export directory is not empty");
        var chunks = ReadIff(new Templates(Path.GetFullPath(game)).Read(template));
        Directory.CreateDirectory(output);
        var usage = new Dictionary<(uint, uint), List<object>>();
        foreach (var chunk in chunks.Where(chunk => chunk.Type == "DGRP"))
        {
            DGRP group;
            try { group = ParseDgrp(chunk); } catch (Exception) { continue; }
            foreach (var image in group.Images ?? [])
            foreach (var sprite in image.Sprites ?? [])
            {
                var key = (sprite.SpriteID, sprite.SpriteFrameIndex);
                if (!usage.TryGetValue(key, out var list)) usage[key] = list = [];
                list.Add(new
                {
                    group = chunk.Id, direction = image.Direction, zoom = image.Zoom, flip = sprite.Flip,
                    spriteOffset = new[] { sprite.SpriteOffset.X, sprite.SpriteOffset.Y },
                    objectOffset = new[] { sprite.ObjectOffset.X, sprite.ObjectOffset.Y, sprite.ObjectOffset.Z }
                });
            }
        }
        var sprites = new List<object>();
        var stem = Path.GetFileNameWithoutExtension(template).ToLowerInvariant();
        foreach (var chunk in chunks.Where(chunk => chunk.Type == "SPR2"))
        {
            var (_, frames) = SpriteArt.DecodeSprite(chunk.Data, Palette(chunks));
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.Width == 0 || frame.Height == 0) continue;
                var name = $"{stem}-{chunk.Id}-{index}";
                File.WriteAllBytes(Path.Combine(output, name + ".png"), SpriteArt.WritePng(frame.Width, frame.Height, frame.Rgba, false));
                File.WriteAllBytes(Path.Combine(output, name + "-depth.png"), SpriteArt.WritePng(frame.Width, frame.Height, frame.Depth, true));
                sprites.Add(new
                {
                    sprite = chunk.Id, frame = index, label = chunk.Label, width = frame.Width, height = frame.Height,
                    image = name + ".png", depth = name + "-depth.png",
                    usedBy = usage.GetValueOrDefault(((uint)chunk.Id, (uint)index)) ?? []
                });
            }
        }
        var objects = chunks.Where(chunk => chunk.Type == "OBJD").Select(chunk => new
        {
            guid = $"0x{ReadField(chunk, "GUID1") | (uint)ReadField(chunk, "GUID2") << 16:X8}", label = chunk.Label,
            groups = new[] { ReadField(chunk, "BaseGraphicID"), ReadField(chunk, "NumGraphics") }
        }).ToArray();
        var manifest = new { template, objects, sprites };
        File.WriteAllText(Path.Combine(output, "sprites.json"), JsonSerializer.Serialize(manifest, Json));
        return new { directory = output, objects = objects.Length, frames = sprites.Count };
    }

    /// <summary>Recolors every sprite palette (PALT) in the file and the lead's catalog icon. Sprites index into these
    /// palettes, so their shading, transparency and depth are unchanged. Pure magenta, the icon's transparent key, is kept.</summary>
    private static object ApplyRecolor(Recolor recolor, List<Chunk> chunks, ushort iconID)
    {
        ValidateRecolor(recolor);
        var palettes = 0;
        var changed = 0;
        foreach (var chunk in chunks.Where(chunk => chunk.Type == "PALT"))
        {
            if (chunk.Data.Length < 16) throw new InvalidDataException($"Palette {chunk.Id} is truncated");
            var count = (int)Math.Min(BitConverter.ToUInt32(chunk.Data, 4), (uint)(chunk.Data.Length - 16) / 3);
            for (var index = 0; index < count; index++) changed += Shift(recolor, chunk.Data, 16 + index * 3, 0, 1, 2, false) ? 1 : 0;
            palettes++;
        }
        var icon = chunks.FirstOrDefault(chunk => chunk.Type == "BMP_" && chunk.Id == iconID);
        var iconColors = icon == null ? 0 : RecolorBitmap(recolor, icon.Data, true);
        return new { palettes, paletteColors = changed, iconColors };
    }

    /// <summary>Recolors a BMP in place: the palette of an indexed bitmap (so RLE and indices are untouched) or every pixel
    /// of an uncompressed 24- or 32-bit one. With <paramref name="keepKey"/>, pure magenta stays transparent. Returns the
    /// number of changed colors; anything that is not such a BMP is left alone and counts none.</summary>
    internal static int RecolorBitmap(Recolor recolor, byte[] data, bool keepKey)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') return 0;
        var header = BitConverter.ToInt32(data, 14);
        var width = BitConverter.ToInt32(data, 18);
        var height = Math.Abs(BitConverter.ToInt32(data, 22));
        var bits = BitConverter.ToUInt16(data, 28);
        var compression = BitConverter.ToUInt32(data, 30);
        var pixels = BitConverter.ToInt32(data, 10);
        var changed = 0;
        if (bits <= 8)
        {
            var used = BitConverter.ToInt32(data, 46);
            var entries = used > 0 ? used : 1 << bits;
            for (var index = 0; index < entries && 14 + header + index * 4 + 3 <= data.Length; index++)
                changed += Shift(recolor, data, 14 + header + index * 4, 2, 1, 0, keepKey) ? 1 : 0;
        }
        else if ((bits == 24 || bits == 32) && compression is 0 or 3 && width is > 0 and <= 4096)
        {
            var stride = (width * bits / 8 + 3) & ~3;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = pixels + y * stride + x * bits / 8;
                if (offset + 3 > data.Length) break;
                changed += Shift(recolor, data, offset, 2, 1, 0, keepKey) ? 1 : 0;
            }
        }
        return changed;
    }

    internal static void ValidateRecolor(Recolor recolor)
    {
        if (!double.IsFinite(recolor.Hue) || recolor.Hue is < -360 or > 360 || !double.IsFinite(recolor.Saturation) || recolor.Saturation is < 0 or > 4 ||
            !double.IsFinite(recolor.Lightness) || recolor.Lightness is < 0 or > 4 || recolor.MinimumSaturation is < 0 or > 1 ||
            recolor.HueRange is { } range && (range.Length != 2 || range.Any(value => !double.IsFinite(value) || value is < 0 or > 360)))
            throw new InvalidDataException("Recolor takes hue -360..360, saturation and lightness 0..4, minimumSaturation 0..1 and a hueRange of two angles 0..360");
    }

    private static bool Shift(Recolor recolor, byte[] data, int offset, int red, int green, int blue, bool keepKey)
    {
        double r = data[offset + red] / 255.0, g = data[offset + green] / 255.0, b = data[offset + blue] / 255.0;
        if (keepKey && data[offset + red] == 255 && data[offset + green] == 0 && data[offset + blue] == 255) return false;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        var saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        if (saturation < recolor.MinimumSaturation) return false;
        if (recolor.HueRange is [var from, var to] && !(from <= to ? hue >= from && hue <= to : hue >= from || hue <= to)) return false;
        hue = ((hue + recolor.Hue) % 360 + 360) % 360;
        saturation = Math.Clamp(saturation * recolor.Saturation, 0, 1);
        lightness = Math.Clamp(lightness * recolor.Lightness, 0, 1);
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var second = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var (rr, gg, bb) = (int)(hue / 60) switch
        {
            0 => (chroma, second, 0.0), 1 => (second, chroma, 0.0), 2 => (0.0, chroma, second),
            3 => (0.0, second, chroma), 4 => (second, 0.0, chroma), _ => (chroma, 0.0, second)
        };
        var match = lightness - chroma / 2;
        var result = new[] { rr + match, gg + match, bb + match }.Select(value => (byte)Math.Clamp(Math.Round(value * 255), 0, 255)).ToArray();
        // Never produce the icon's transparent key by accident.
        if (keepKey && result[0] == 255 && result[1] == 0 && result[2] == 255) result[1] = 1;
        var before = (data[offset + red], data[offset + green], data[offset + blue]);
        (data[offset + red], data[offset + green], data[offset + blue]) = (result[0], result[1], result[2]);
        return before != (result[0], result[1], result[2]);
    }

    /// <summary>"next", "true", "false", "error", a label or an instruction index. A missing true branch goes to the
    /// next instruction (or returns true after the last); a missing false branch is an error.</summary>
    private static byte Pointer(JsonElement? value, int index, int count, Dictionary<string, int> labels, bool truePath, string where)
    {
        var last = index == count - 1;
        if (value is not { } element) return truePath ? (last ? PointerTrue : (byte)(index + 1)) : PointerError;
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt32(out var target) || target < 0 || target >= count)
                throw new InvalidDataException($"{where}: branch target is out of range");
            return (byte)target;
        }
        var name = element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new InvalidDataException($"{where}: invalid branch target");
        return name switch
        {
            "next" when !last => (byte)(index + 1),
            "next" => throw new InvalidDataException($"{where}: the last instruction cannot branch to next"),
            "true" => PointerTrue,
            "false" => PointerFalse,
            "error" => PointerError,
            _ => labels.TryGetValue(name, out var target) ? (byte)target : throw new InvalidDataException($"{where}: unknown label {name}")
        };
    }

    private static ushort Resolve(JsonElement value, Dictionary<string, ushort> behaviorIDs, HashSet<ushort> existing, string where)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var name = value.GetString()!;
            if (behaviorIDs.TryGetValue(name, out var id)) return id;
            if (!name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !ushort.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id))
                throw new InvalidDataException($"{where}: unknown behavior {name}");
            value = JsonSerializer.SerializeToElement((int)id);
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 0x100 or > 0x2FFF)
            throw new InvalidDataException($"{where}: behavior IDs are global (0x100), private (0x1000) or semi-global (0x2000)");
        if (number is >= 0x1000 and < 0x2000 && !existing.Contains((ushort)number) && !behaviorIDs.ContainsValue((ushort)number))
            throw new InvalidDataException($"{where}: private behavior 0x{number:X4} is not in the template");
        return (ushort)number;
    }

    /// <summary>Fills an engine operand model from JSON, then checks that encoding it keeps every value.</summary>
    private static byte[] Operand(Type model, Dictionary<string, JsonElement> values, string where)
    {
        var operand = (VMPrimitiveOperand)Activator.CreateInstance(model)!;
        var members = Members(model);
        foreach (var (name, value) in values)
        {
            var member = members.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{where}: {model.Name} has no field {name}; it has {string.Join(", ", members.Select(item => item.Name))}");
            Assign(operand, member, value, where);
        }
        var bytes = new byte[8];
        operand.Write(bytes);
        var check = (VMPrimitiveOperand)Activator.CreateInstance(model)!;
        check.Read(bytes);
        foreach (var name in values.Keys)
        {
            var member = members.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!Equivalent(Value(operand, member), Value(check, member)))
                throw new InvalidDataException($"{where}: {member.Name} does not fit in the {model.Name} encoding");
        }
        return bytes;
    }

    /// <summary>An animate operand may name one of the document's animations in animationID instead of giving its index.</summary>
    private static Dictionary<string, JsonElement> AnimationNames(Dictionary<string, JsonElement> values, Type model, Dictionary<string, ushort> animations, string where)
    {
        if (model != typeof(VMAnimateSimOperand)) return values;
        var key = values.Keys.FirstOrDefault(name => string.Equals(name, nameof(VMAnimateSimOperand.AnimationID), StringComparison.OrdinalIgnoreCase));
        if (key == null || values[key].ValueKind != JsonValueKind.String) return values;
        var name = values[key].GetString()!;
        if (!animations.TryGetValue(name, out var index))
            throw new InvalidDataException($"{where}: unknown animation {name}; the document adds {(animations.Count == 0 ? "none" : string.Join(", ", animations.Keys))}");
        if (values.Keys.Any(item => string.Equals(item, nameof(VMAnimateSimOperand.Source), StringComparison.OrdinalIgnoreCase) &&
            !(values[item].ValueKind == JsonValueKind.String ? string.Equals(values[item].GetString(), "Object", StringComparison.OrdinalIgnoreCase) : values[item].ValueKind == JsonValueKind.Number && values[item].GetInt32() == 0)))
            throw new InvalidDataException($"{where}: animation {name} is in the object's tables, so its source must be Object");
        return new Dictionary<string, JsonElement>(values) { [key] = JsonSerializer.SerializeToElement(index) };
    }

    private static MemberInfo[] Members(Type model) =>
        model.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.CanRead && property.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>().Concat(model.GetFields(BindingFlags.Public | BindingFlags.Instance)).ToArray();

    private static Type MemberType(MemberInfo member) => member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    private static object? Value(object target, MemberInfo member) => member is PropertyInfo property ? property.GetValue(target) : ((FieldInfo)member).GetValue(target);

    private static bool Equivalent(object? left, object? right) =>
        left is Array a && right is Array b ? a.Length == b.Length && a.Cast<object>().SequenceEqual(b.Cast<object>()) : Equals(left, right);

    private static void Assign(object target, MemberInfo member, JsonElement value, string where)
    {
        var type = MemberType(member);
        object converted;
        try
        {
            if (type.IsEnum)
                converted = value.ValueKind == JsonValueKind.String
                    ? Enum.Parse(type, value.GetString()!, true)
                    : Enum.ToObject(type, value.GetInt64());
            else if (type == typeof(bool)) converted = value.ValueKind == JsonValueKind.Number ? value.GetInt64() != 0 : value.GetBoolean();
            else if (type.IsArray && type.GetElementType() is { IsPrimitive: true } element)
            {
                var items = value.EnumerateArray().Select(item => Convert.ChangeType(item.GetInt64(), element)).ToArray();
                var array = Array.CreateInstance(element, items.Length);
                for (var index = 0; index < items.Length; index++) array.SetValue(items[index], index);
                converted = array;
            }
            else if (type.IsPrimitive) converted = Convert.ChangeType(value.GetInt64(), type);
            else throw new InvalidDataException($"{where}: {member.Name} cannot be set from JSON");
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"{where}: invalid value for {member.Name}: {error.Message}");
        }
        if (type.IsEnum && value.ValueKind == JsonValueKind.String && !Enum.IsDefined(type, converted) && !type.IsDefined(typeof(FlagsAttribute), false))
            throw new InvalidDataException($"{where}: {value.GetString()} is not a {type.Name}");
        if (member is PropertyInfo property) property.SetValue(target, converted);
        else ((FieldInfo)member).SetValue(target, converted);
    }

    private static byte[] Hex(string value, string where)
    {
        try
        {
            var bytes = Convert.FromHexString(value.Replace(" ", ""));
            if (bytes.Length > 8) throw new FormatException();
            return bytes.Concat(new byte[8 - bytes.Length]).ToArray();
        }
        catch (FormatException) { throw new InvalidDataException($"{where}: operandHex must be at most eight hexadecimal bytes"); }
    }

    private static uint ParseGuid(JsonElement value, string what)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out number)) return number;
        throw new InvalidDataException($"Invalid {what}; use a number or a 0x-prefixed hexadecimal string");
    }

    private static string Text(string value, string what)
    {
        if (value.Length is < 1 or > 1000 || value.Contains('\0')) throw new InvalidDataException($"Invalid {what}");
        if (value.Any(character => character > '\u00FF')) throw new InvalidDataException($"The {what} has characters outside Latin-1, which object strings cannot hold");
        return value;
    }

    private static string Label(string value) => new string(value.Where(character => character is >= ' ' and < '\u007f').Take(63).ToArray());

    /// <summary>Sets one language's string. A language the template lacks starts as a copy of the default strings,
    /// since the engine falls back to the default only when a language has no strings at all.</summary>
    private static void Set(STR strings, STRLangCode language, int index, string value)
    {
        var code = (int)language;
        var set = strings.LanguageSets[code - 1] ??= new STRLanguageSet { Strings = [] };
        if (code > 1 && set.Strings.Length == 0)
            set.Strings = (strings.LanguageSets[0]?.Strings ?? []).Select(item => new STRItem { LanguageCode = (byte)code, Value = item.Value, Comment = item.Comment ?? "" }).ToArray();
        if (set.Strings.Length <= index)
            set.Strings = set.Strings.Concat(Enumerable.Range(0, index + 1 - set.Strings.Length)
                .Select(_ => new STRItem { LanguageCode = (byte)code, Value = "", Comment = "" })).ToArray();
        set.Strings[index].Value = value;
        set.Strings[index].Comment ??= "";
    }

    /// <summary>Sets a string in English and in every other language the table carries, growing the table as needed.</summary>
    private static void Set(STR strings, int index, string value)
    {
        for (var language = 0; language < strings.LanguageSets.Length; language++)
        {
            var set = strings.LanguageSets[language] ??= new STRLanguageSet();
            if (language > 0 && set.Strings.Length == 0) continue;
            if (set.Strings.Length <= index)
                set.Strings = set.Strings.Concat(Enumerable.Range(0, index + 1 - set.Strings.Length)
                    .Select(_ => new STRItem { LanguageCode = (byte)(language + 1), Value = "", Comment = "" })).ToArray();
            set.Strings[index].Value = value;
            set.Strings[index].Comment ??= "";
        }
    }

    private static int FieldIndex(string name) => Array.IndexOf(ObjdFieldNames, name);

    private static ushort ReadField(Chunk definition, string name)
    {
        var offset = 4 + FieldIndex(name) * 2;
        return offset >= 4 && offset + 2 <= definition.Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(definition.Data.AsSpan(offset)) : (ushort)0;
    }

    private static void WriteField(Chunk definition, string name, ushort value)
    {
        var index = FieldIndex(name);
        if (index < 0) throw new InvalidDataException($"Unknown definition field {name}");
        var offset = 4 + index * 2;
        if (offset + 2 > definition.Data.Length) throw new InvalidDataException($"The template's definition version has no {name} field");
        BinaryPrimitives.WriteUInt16LittleEndian(definition.Data.AsSpan(offset), value);
    }

    private static ParsedObjd ParseObjd(Chunk chunk) => new()
    {
        GUID = ReadField(chunk, "GUID1") | ((uint)ReadField(chunk, "GUID2") << 16),
        ObjectType = ReadField(chunk, "ObjectType"),
        MasterID = ReadField(chunk, "MasterID"),
        SubIndex = unchecked((short)ReadField(chunk, "SubIndex")),
        TreeTableID = ReadField(chunk, "TreeTableID"),
        AnimationTableID = ReadField(chunk, "AnimationTableID"),
        Price = ReadField(chunk, "Price"),
        BaseGraphicID = ReadField(chunk, "BaseGraphicID"),
        NumGraphics = ReadField(chunk, "NumGraphics"),
        Disabled = ReadField(chunk, "Disabled"),
        FunctionFlags = ReadField(chunk, "FunctionFlags"),
        BuildModeType = ReadField(chunk, "BuildModeType"),
        RoomFlags = ReadField(chunk, "RoomFlags"),
        FunctionSubsort = ReadField(chunk, "FunctionSubsort"),
        DTSubsort = ReadField(chunk, "DTSubsort"),
        VacationSubsort = ReadField(chunk, "VacationSubsort"),
        CommunitySubsort = ReadField(chunk, "CommunitySubsort"),
        STSubsort = ReadField(chunk, "STSubsort"),
        MTSubsort = ReadField(chunk, "MTSubsort")
    };

    private static STR ParseStrings(Chunk chunk)
    {
        var table = new STR();
        for (var i = 0; i < table.LanguageSets.Length; i++)
            table.LanguageSets[i] = new STRLanguageSet { Strings = [] };
        var data = chunk.Data;
        if (data.Length < 2) return table;
        var format = BinaryPrimitives.ReadInt16LittleEndian(data);
        var lists = Enumerable.Range(0, 20).Select(_ => new List<STRItem>()).ToArray();
        var cursor = 2;

        string ReadCString(byte language = 1)
        {
            var start = cursor;
            while (cursor < data.Length && data[cursor] != 0) cursor++;
            var text = ResourceReader.EncodingForLanguage(language).GetString(data, start, cursor - start);
            if (cursor < data.Length) cursor++;
            return text;
        }

        string ReadLengthString(byte language = 1)
        {
            if (cursor >= data.Length) return "";
            var length = (int)data[cursor++];
            if (length >= 0x80 && cursor < data.Length)
                length = (length & 0x7F) | (data[cursor++] << 7);
            var available = Math.Min(length, data.Length - cursor);
            var text = ResourceReader.EncodingForLanguage(language).GetString(data, cursor, available);
            cursor += available;
            return text;
        }

        if (format == 0)
        {
            if (data.Length < 4) return table;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            cursor = 4;
            for (var i = 0; i < count && cursor < data.Length; i++)
            {
                int len = data[cursor++];
                var available = Math.Min(len, data.Length - cursor);
                lists[0].Add(new STRItem { LanguageCode = 1, Value = ResourceReader.EncodingForLanguage(1).GetString(data, cursor, available), Comment = "" });
                cursor += available;
            }
        }
        else if (format == -1)
        {
            if (data.Length < 4) return table;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            cursor = 4;
            for (var i = 0; i < count && cursor < data.Length; i++)
                lists[0].Add(new STRItem { LanguageCode = 1, Value = ReadCString(1), Comment = "" });
        }
        else if (format == -2)
        {
            if (data.Length < 4) return table;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            cursor = 4;
            for (var i = 0; i < count && cursor < data.Length; i++)
                lists[0].Add(new STRItem { LanguageCode = 1, Value = ReadCString(1), Comment = ReadCString(1) });
        }
        else if (format == -3)
        {
            if (data.Length < 4) return table;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            cursor = 4;
            for (var i = 0; i < count && cursor < data.Length; i++)
            {
                var code = data[cursor++];
                var decodeLanguage = code is >= 1 and <= 20 ? code : (byte)1;
                var value = ReadCString(decodeLanguage);
                var comment = ReadCString(decodeLanguage);
                if (code is >= 1 and <= 20)
                    lists[code - 1].Add(new STRItem { LanguageCode = code, Value = value, Comment = comment });
            }
        }
        else if (format == -4)
        {
            if (data.Length < 3) return table;
            int numSets = data[2];
            cursor = 3;
            for (var s = 0; s < numSets && cursor + 2 <= data.Length; s++)
            {
                int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor));
                cursor += 2;
                var language = (byte)Math.Clamp(s + 1, 1, 20);
                for (var i = 0; i < count && cursor < data.Length; i++)
                {
                    var code = (byte)(data[cursor++] + 1);
                    var value = ReadLengthString(language);
                    var comment = ReadLengthString(language);
                    if (s < 20)
                        lists[s].Add(new STRItem { LanguageCode = (byte)(s + 1), Value = value, Comment = comment });
                }
            }
        }

        for (var i = 0; i < 20; i++)
            table.LanguageSets[i] = new STRLanguageSet { Strings = lists[i].ToArray() };
        return table;
    }

    private static byte[] SerializeStrings(STR table)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)-3);
        var total = (ushort)table.LanguageSets.Sum(set => set?.Strings.Length ?? 0);
        writer.Write(total);
        for (var i = 0; i < table.LanguageSets.Length; i++)
        {
            var set = table.LanguageSets[i];
            if (set == null) continue;
            var language = (byte)(i + 1);
            var encoding = ResourceReader.EncodingForLanguage(language);
            foreach (var item in set.Strings)
            {
                writer.Write(language);
                writer.Write(encoding.GetBytes(item.Value ?? ""));
                writer.Write((byte)0);
                writer.Write(encoding.GetBytes(item.Comment ?? ""));
                writer.Write((byte)0);
            }
        }
        return stream.ToArray();
    }

    private static BHAV ParseBhav(Chunk chunk)
    {
        var data = chunk.Data;
        if (data.Length < 12) return new BHAV();
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data);
        byte type = 0, args = 0;
        ushort locals = 0, unknown = 0;
        int count, start = 12;
        if (version == 0x8000)
        {
            count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            unknown = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        }
        else if (version == 0x8001)
        {
            count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            type = data[4];
            args = data[5];
            locals = data[6];
            unknown = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        }
        else if (version == 0x8002)
        {
            count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            type = data[4];
            args = data[5];
            locals = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
            unknown = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        }
        else if (version == 0x8003 && data.Length >= 13)
        {
            type = data[2];
            args = data[3];
            locals = data[4];
            unknown = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5));
            count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9));
            start = 13;
        }
        else
        {
            return new BHAV { Version = version };
        }
        count = Math.Max(0, Math.Min(count, (data.Length - start) / 12));
        var instructions = new BHAVInstruction[count];
        for (var i = 0; i < count; i++)
        {
            var span = data.AsSpan(start + i * 12, 12);
            var rawOpcode = BinaryPrimitives.ReadUInt16LittleEndian(span);
            instructions[i] = new BHAVInstruction
            {
                Opcode = (ushort)(rawOpcode & 0x7FFF),
                Breakpoint = (rawOpcode & 0x8000) != 0,
                TruePointer = span[2],
                FalsePointer = span[3],
                Operand = span.Slice(4, 8).ToArray()
            };
        }
        return new BHAV
        {
            Version = version,
            Type = type,
            Args = args,
            Locals = locals,
            Unknown = unknown,
            Instructions = instructions
        };
    }

    private static byte[] SerializeBhav(BHAV bhav)
    {
        var version = bhav.Version == 0 ? (ushort)0x8002 : bhav.Version;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (version == 0x8000)
        {
            writer.Write(version);
            writer.Write((ushort)bhav.Instructions.Length);
            writer.Write(new byte[8]);
        }
        else if (version == 0x8001)
        {
            writer.Write(version);
            writer.Write((ushort)bhav.Instructions.Length);
            writer.Write(bhav.Type);
            writer.Write(bhav.Args);
            writer.Write((byte)bhav.Locals);
            writer.Write(new byte[5]);
        }
        else if (version == 0x8002)
        {
            writer.Write(version);
            writer.Write((ushort)bhav.Instructions.Length);
            writer.Write(bhav.Type);
            writer.Write(bhav.Args);
            writer.Write(bhav.Locals);
            writer.Write(bhav.Unknown);
            writer.Write((ushort)0);
        }
        else if (version == 0x8003)
        {
            writer.Write(version);
            writer.Write(bhav.Type);
            writer.Write(bhav.Args);
            writer.Write((byte)bhav.Locals);
            writer.Write(bhav.Unknown);
            writer.Write((ushort)0);
            writer.Write((uint)bhav.Instructions.Length);
        }
        else
        {
            throw new InvalidDataException($"Unsupported BHAV version 0x{version:X4}");
        }
        foreach (var instruction in bhav.Instructions)
        {
            var rawOpcode = (ushort)(instruction.Opcode | (instruction.Breakpoint ? 0x8000 : 0));
            writer.Write(rawOpcode);
            writer.Write(instruction.TruePointer);
            writer.Write(instruction.FalsePointer);
            writer.Write(instruction.Operand, 0, 8);
        }
        return stream.ToArray();
    }

    private static IffFile SingleChunkFile(Chunk chunk, out IffChunk iffChunk)
    {
        var file = new IffFile(WriteIff([chunk]), chunk.Label);
        iffChunk = file.Chunks[0];
        return file;
    }

    private static TTAB ParseTtab(Chunk chunk)
    {
        if (chunk.Data.Length < 4) return new TTAB();
        var file = SingleChunkFile(chunk, out var iffChunk);
        var parsed = InteractionTable.Read(file, iffChunk);
        return new TTAB
        {
            Version = parsed.Version >= 9 ? (ushort)parsed.Version : (ushort)10,
            Interactions = parsed.Interactions.Select(entry => new TTABInteraction
            {
                ActionFunction = entry.ActionTree,
                TestFunction = entry.GuardTree,
                Flags = (TTABFlags)entry.Flags,
                TTAIndex = (uint)entry.TtaIndex,
                AttenuationCode = entry.AttenuationCode,
                AttenuationValue = entry.AttenuationValue,
                AutonomyThreshold = entry.AutonomyThreshold,
                JoiningIndex = entry.JoiningIndex,
                MotiveEntries = Enumerable.Range(0, Math.Max(16, entry.Motives.Count)).Select(index =>
                    index < entry.Motives.Count
                        ? new TTABMotiveEntry
                        {
                            MotiveIndex = index,
                            EffectRangeMinimum = entry.Motives[index].Minimum,
                            EffectRangeDelta = entry.Motives[index].Delta,
                            PersonalityModifier = (ushort)entry.Motives[index].Personality
                        }
                        : new TTABMotiveEntry { MotiveIndex = index }).ToArray()
            }).ToArray()
        };
    }

    private static byte[] SerializeTtab(TTAB table)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)table.Interactions.Length);
        if (table.Interactions.Length == 0) return stream.ToArray();
        var version = table.Version is >= 9 and <= 10 ? table.Version : (ushort)10;
        writer.Write(version);
        writer.Write((byte)0);
        foreach (var entry in table.Interactions)
        {
            writer.Write(entry.ActionFunction);
            writer.Write(entry.TestFunction);
            writer.Write((uint)entry.MotiveEntries.Length);
            writer.Write((uint)entry.Flags);
            writer.Write(entry.TTAIndex);
            writer.Write(entry.AttenuationCode);
            writer.Write(entry.AttenuationValue);
            writer.Write(entry.AutonomyThreshold);
            writer.Write(entry.JoiningIndex);
            foreach (var motive in entry.MotiveEntries)
            {
                writer.Write(motive.EffectRangeMinimum);
                writer.Write(motive.EffectRangeDelta);
                writer.Write(motive.PersonalityModifier);
            }
        }
        return stream.ToArray();
    }

    private static byte[] SerializeSlot(SLOT slots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(10u);
        writer.Write("TOLS"u8);
        writer.Write((uint)slots.Chronological.Count);
        foreach (var item in slots.Chronological)
        {
            writer.Write(item.Type);
            writer.Write(item.OffsetX);
            writer.Write(item.OffsetY);
            writer.Write(item.OffsetZ);
            writer.Write(item.Standing);
            writer.Write(item.Sitting);
            writer.Write(item.Ground);
            writer.Write(item.Rsflags);
            writer.Write(item.SnapTargetSlot);
            writer.Write(item.MinProximity);
            writer.Write(item.MaxProximity);
            writer.Write(item.OptimalProximity);
            writer.Write(item.MaxSize);
            writer.Write(item.I10);
            writer.Write(item.Gradient);
            writer.Write(item.Height);
            writer.Write(item.Facing);
            writer.Write(item.Resolution);
        }
        return stream.ToArray();
    }

    private static DGRP ParseDgrp(Chunk chunk)
    {
        var file = SingleChunkFile(chunk, out var iffChunk);
        var parsed = DrawGroup.Read(file, iffChunk);
        return new DGRP
        {
            Images = parsed.Images.Select(image => new DGRPImage
            {
                Direction = (uint)image.Direction,
                Zoom = (uint)image.Zoom,
                Sprites = image.Sprites.Select(sprite => new DGRPSprite
                {
                    SpriteID = (uint)sprite.SpriteId,
                    SpriteFrameIndex = (uint)sprite.Frame,
                    Flags = sprite.Flags,
                    SpriteOffset = new Vector2(sprite.X, sprite.Y),
                    ObjectOffset = new Vector3(sprite.Offset2, sprite.Offset3, sprite.Offset1)
                }).ToArray()
            }).ToArray()
        };
    }

    private static byte[] SerializeDgrp(DGRP group)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)20004);
        writer.Write((uint)group.Images.Length);
        foreach (var image in group.Images)
        {
            writer.Write(image.Direction);
            writer.Write(image.Zoom);
            writer.Write((uint)image.Sprites.Length);
            foreach (var sprite in image.Sprites)
            {
                writer.Write(sprite.SpriteID);
                writer.Write(sprite.SpriteFrameIndex);
                writer.Write((int)sprite.SpriteOffset.X);
                writer.Write((int)sprite.SpriteOffset.Y);
                writer.Write(sprite.ObjectOffset.Z);
                writer.Write(sprite.Flags);
                writer.Write(sprite.ObjectOffset.X);
                writer.Write(sprite.ObjectOffset.Y);
            }
        }
        return stream.ToArray();
    }

    internal static List<Chunk> ReadIff(byte[] data)
    {
        if (data.Length < 64 || !Encoding.ASCII.GetString(data, 0, 8).StartsWith("IFF FILE", StringComparison.Ordinal))
            throw new InvalidDataException("Template is not an IFF file");
        var chunks = new List<Chunk>();
        var cursor = 64;
        while (cursor + 76 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, cursor, 4);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4));
            if (size < 76 || cursor + size > data.Length) throw new InvalidDataException("Template IFF chunk is truncated");
            var label = Encoding.Latin1.GetString(data, cursor + 12, 64);
            chunks.Add(new Chunk
            {
                Type = type,
                Id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor + 8)),
                Flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cursor + 10)),
                Label = label[..(label.IndexOf('\0') is var end and >= 0 ? end : label.Length)],
                Data = data.AsSpan(cursor + 76, size - 76).ToArray()
            });
            cursor += size;
        }
        // The resource map indexes the original layout; the engine does not need it, so it is not carried over.
        chunks.RemoveAll(chunk => chunk.Type == "rsmp");
        return chunks;
    }

    internal static byte[] WriteIff(List<Chunk> chunks)
    {
        using var stream = new MemoryStream();
        var header = new byte[64];
        Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1").CopyTo(header, 0);
        stream.Write(header);
        foreach (var chunk in chunks)
        {
            var head = new byte[76];
            Encoding.ASCII.GetBytes(chunk.Type).CopyTo(head, 0);
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)(chunk.Data.Length + 76));
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8), chunk.Id);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(10), chunk.Flags);
            Encoding.Latin1.GetBytes(chunk.Label).CopyTo(head, 12);
            stream.Write(head);
            stream.Write(chunk.Data);
        }
        return stream.ToArray();
    }

    private static bool Identifier(string value) => value != null && value.Length is >= 1 and <= 120 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_') && value is not ("." or "..");

    /// <summary>Original object files, loose or inside the FAR archives of GameData/Objects, then of the installed
    /// ExpansionPack folders (Vacation's snow forts, for example). Base objects win a shared name.</summary>
    private sealed class Templates(string game)
    {
        private readonly string objects = Path.Combine(game, "GameData", "Objects");

        public byte[] Read(string name)
        {
            // "tso:NAME.iff" takes a The Sims Online object, joined with its sprite and tuning files, from the
            // TSOClient/objectdata/objects folder named by SIMS_TSO_OBJECTS (see TsoInventory.Merged).
            if (name.StartsWith("tso:", StringComparison.OrdinalIgnoreCase))
            {
                var tso = Environment.GetEnvironmentVariable("SIMS_TSO_OBJECTS");
                var file = name[4..];
                if (string.IsNullOrEmpty(tso) || !Directory.Exists(tso)) throw new InvalidDataException($"Template {name} needs SIMS_TSO_OBJECTS set to The Sims Online's TSOClient/objectdata/objects folder");
                if (file.IndexOfAny(['/', '\\']) >= 0 || !file.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Template {name} must name a TSO object file such as tso:christmastree.iff");
                try { return TsoInventory.Merged(tso, Path.GetFileNameWithoutExtension(file)).Bytes; }
                catch (FileNotFoundException error) { throw new InvalidDataException(error.Message); }
            }
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\']) >= 0 || !name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Template {name} must be an original object file name such as flamingo.iff");
            if (!Directory.Exists(objects)) throw new InvalidDataException($"No original objects at {objects}");
            var roots = new[] { objects }.Concat(Directory.EnumerateDirectories(game, "ExpansionPack*").Order(StringComparer.Ordinal));
            foreach (var root in roots)
            {
                var loose = Directory.EnumerateFiles(root, "*.iff", SearchOption.AllDirectories)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
                if (loose != null) return File.ReadAllBytes(loose);
                foreach (var path in Directory.EnumerateFiles(root, "*.far", SearchOption.AllDirectories))
                {
                    var archive = FarArchive.Open(path);
                    var entry = archive.Entries.FirstOrDefault(item => string.Equals(Path.GetFileName(item.Name.Replace('\\', '/')), name, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) return archive.Read(entry).ToArray();
                }
            }
            throw new InvalidDataException($"Template {name} is not an installed original object");
        }
    }
}
