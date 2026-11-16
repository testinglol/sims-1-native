namespace SimsEngine.Formats;

/// <summary>Defines 16-bit field indices of an object definition (OBJD) resource, corresponding to the
/// definition labels in string table 204.</summary>
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
    FunctionFlags = FunctionSort,
    CatalogStringsId = 41,
    IsGlobalSimObject = 42,
    WallStyle = 46,
    ObjectVersion = 49,
    MotiveEffectsId = 51,
    CatalogPopupId = 54,
    LevelOffset = 56,
    Shadow = 57,
    /// <summary>Signed per-object adjustment applied to the generated floor shadow palette.</summary>
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
    /// <summary>A packed four-nibble bitmask defining the object's footprint extents.</summary>
    FootprintMask = 77,
    /// <summary>Catalog subcategory bitmask used to filter objects within a primary function category.</summary>
    FunctionSubsort = 94,
    VitaBoyFlags = 102,
    /// <summary>Specifies the signed draw group index used for catalog thumbnails.</summary>
    ThumbnailGraphic = 75,
}

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

    /// <summary>Accesses a definition field by index, returning zero for fields beyond the end of truncated
    /// legacy definitions.</summary>
    public ushort this[ObjdField field] => (int)field < _fields.Length ? _fields[(int)field] : (ushort)0;

    public uint Version => (uint)(this[ObjdField.Version1] | this[ObjdField.Version2] << 16);
    public uint Guid => (uint)(this[ObjdField.Guid1] | this[ObjdField.Guid2] << 16);
    /// <summary>Gets the catalog thumbnail draw group index interpreted as a signed 16-bit integer.</summary>
    public short ThumbnailGraphicIndex => (int)ObjdField.ThumbnailGraphic < _fields.Length
        ? unchecked((short)_fields[(int)ObjdField.ThumbnailGraphic])
        : (short)0;
    /// <summary>Calculates the thumbnail viewing direction by offsetting the front direction by 180
    /// degrees, evaluated as <c>(FrontDirection + 4) &amp; 7</c>.</summary>
    public int ThumbnailDirectionIndex => (unchecked((short)this[ObjdField.FrontDirection]) + 4) & 7;
}

/// <summary>Represents an object function table (OBJf) resource. The binary chunk consists of two zero
/// integers, the magic tag "fJBO", a count, and pairs of 16-bit guard and action behavior tree IDs.</summary>
public sealed record Objf(IReadOnlyList<(ushort Guard, ushort Action)> Functions)
{
    private static readonly int[] DefaultActionFunctionFields =
    [
        43, 5, 47, 52, 60, 21, 62, 50, 78, 44, 74, 48, 45, 53, 55,
        17, 6, 12, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 59, 80,
    ];

    /// <summary>Extracts the 30 legacy action entry point tree IDs directly from definition fields when an
    /// OBJf resource is absent.</summary>
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

/// <summary>Represents a semi-global reference (GLOB) chunk, storing the filename as a null-terminated
/// string if the trailing byte is zero or as a length-prefixed string otherwise.</summary>
public sealed record Glob(string Name)
{
    public static Glob Read(IffFile file, IffChunk chunk)
    {
        var reader = file.Reader(chunk);
        var data = chunk.Data.Span;
        return new Glob(data.Length > 0 && data[^1] == 0 ? reader.CString() : reader.PascalString());
    }
}
