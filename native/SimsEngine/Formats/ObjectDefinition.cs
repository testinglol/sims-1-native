namespace SimsEngine.Formats;

/// <summary>Object definition (OBJD) field numbers. The chunk is an array of u16 fields; the names are the game's
/// own, from Behavior.iff STR# 204 ("definition labels"), which lists one label per field in order.</summary>
public enum ObjdField
{
    Version1 = 0,
    Version2 = 1,
    InitialStackSize = 2,
    BaseGraphic = 3,
    NumGraphics = 4,
    TreeTableId = 7,
    InteractionGroup = 8,
    Type = 9,
    UsesFnTable = 22,
    MasterId = 10,
    SubIndex = 11,
    AnimTableId = 13,
    Guid1 = 14,
    Guid2 = 15,
    Disabled = 16,
    Price = 18,
    BodyStringsId = 19,
    SlotsId = 20,
    SalePrice = 34,
    InitialDepreciation = 35,
    DailyDepreciation = 36,
    SelfDepreciating = 37,
    DepreciationLimit = 38,
    RoomSort = 39,
    FunctionSort = 40,
    /// <summary>The catalog category bitmask in OBJD field 40, called FunctionFlags in the original resource format.</summary>
    FunctionFlags = FunctionSort,
    CatalogStringsId = 41,
    IsGlobalSimObject = 42,
    WallStyle = 46,
    ObjectVersion = 49,
    MotiveEffectsId = 51,
    CatalogPopupId = 54,
    LevelOffset = 56,
    Shadow = 57,
    /// <summary>Signed per-object adjustment to the generated floor-shadow palette.</summary>
    ShadowBrightness = 79,
    NumAttributes = 58,
    FrontDirection = 61,
    MtLeadObject = 63,
    DynamicSpritesBaseId = 64,
    NumDynamicSprites = 65,
    ChairEntryFlags = 66,
    TileWidth = 67,
    InhibitSuitCopying = 68,
    BuildModeType = 69,
    OriginalGuid1 = 70,
    OriginalGuid2 = 71,
    SuitGuid1 = 72,
    SuitGuid2 = 73,
    /// <summary>The packed four-nibble footprint extent mask (type-record +0x9a, read by FUN_00433dbb).</summary>
    FootprintMask = 77,
    /// <summary>The residential lot catalog subsort bit mask at OBJD field 94 (installed seating objects carry 1, 2, 4, 8 and 128).</summary>
    FunctionSubsort = 94,
    /// <summary>The VitaBoy flags word read by Mac SAnimator::UpdateBackgroundPractice at OBJD byte offset 0xCC.</summary>
    VitaBoyFlags = 102,
    /// <summary>Signed draw-group selector returned by Mac ObjSelector::GetThumbnailGraphicIndex (OBJD byte offset 0x96).</summary>
    ThumbnailGraphic = 75,
}

/// <summary>An object definition (OBJD): the u16 fields of one object, read by number as the game does.</summary>
public sealed class Objd
{
    private readonly ushort[] _fields;

    public IReadOnlyList<ushort> Fields => _fields;

    private Objd(ushort[] fields) => _fields = fields;

    public static Objd Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var fields = new ushort[reader.Length / 2];
        for (var index = 0; index < fields.Length; index++) fields[index] = reader.U16();
        if (fields.Length < 16) throw reader.Error($"{fields.Length} fields cannot hold the GUID (fields 14 and 15)");
        return new Objd(fields);
    }

    /// <summary>A field by number; fields past the end of an older, shorter definition read as 0.</summary>
    public ushort this[ObjdField field] => (int)field < _fields.Length ? _fields[(int)field] : (ushort)0;

    public uint Version => (uint)(this[ObjdField.Version1] | this[ObjdField.Version2] << 16);
    public uint Guid => (uint)(this[ObjdField.Guid1] | this[ObjdField.Guid2] << 16);
    /// <summary>Mac GetThumbnailGraphicIndex reads this OBJD word as a signed short.</summary>
    public short ThumbnailGraphicIndex => (int)ObjdField.ThumbnailGraphic < _fields.Length
        ? unchecked((short)_fields[(int)ObjdField.ThumbnailGraphic])
        : (short)0;
    /// <summary>Mac DrawIconic renders the thumbnail from front direction + 4, wrapped to the eight directions.</summary>
    public int ThumbnailDirectionIndex => (unchecked((short)this[ObjdField.FrontDirection]) + 4) & 7;
}

/// <summary>An object function table (OBJf): for each object function (the OBJD "old tree id" slots in order), a
/// guard tree and an action tree, 0 when absent. Layout from the files: two u32 zeros, the tag "fJBO", a u32
/// count, then count pairs of u16 guard and u16 action.</summary>
public sealed record Objf(IReadOnlyList<(ushort Guard, ushort Action)> Functions)
{
    private static readonly int[] DefaultActionFunctionFields =
    [
        43, 5, 47, 52, 60, 21, 62, 50, 78, 44, 74, 48, 45, 53, 55,
        17, 6, 12, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 59, 80,
    ];

    /// <summary>FUN_00456209: the 30 legacy entry points copied from these OBJD word offsets when no OBJf table is
    /// used or available. The offsets are the original in-memory OBJD layout (two bytes per definition field).</summary>
    public static IReadOnlyList<ushort> DefaultActionFunctions(Objd definition) =>
        DefaultActionFunctionFields.Select(field => definition.Fields.Count > field ? definition.Fields[field] : (ushort)0).ToArray();

    public static IReadOnlyList<(ushort Guard, ushort Action)> FunctionEntries(IffFile file, IffChunk objdChunk, Objd definition)
    {
        if (definition[ObjdField.UsesFnTable] != 0 && file.Find(ResourceType.Objf, objdChunk.Id) is { } chunk)
            return Read(file, chunk).Functions;
        return DefaultActionFunctions(definition).Select(action => ((ushort)0, action)).ToArray();
    }

    public static IReadOnlyList<ushort> ActionFunctions(IffFile file, IffChunk objdChunk, Objd definition) =>
        FunctionEntries(file, objdChunk, definition).Select(entry => entry.Action).ToArray();

    public static Objf Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        reader.U32();
        reader.U32();
        if (!reader.Bytes(4).SequenceEqual("fJBO"u8)) throw reader.Error("missing the fJBO tag");
        var count = reader.U32();
        if (count > (uint)reader.Remaining / 4) throw reader.Error($"{count} functions do not fit the chunk");
        var functions = new (ushort, ushort)[count];
        for (var index = 0; index < count; index++) functions[index] = (reader.U16(), reader.U16());
        return new Objf(functions);
    }
}

/// <summary>The semi-global file an object file uses (GLOB). Sims.exe FUN_0043bb37 reads a zero-terminated name
/// when the chunk's last byte is 0, and a length-prefixed name otherwise.</summary>
public sealed record Glob(string Name)
{
    public static Glob Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var data = chunk.Data.Span;
        return new Glob(data.Length > 0 && data[^1] == 0 ? reader.CString() : reader.PascalString());
    }
}
