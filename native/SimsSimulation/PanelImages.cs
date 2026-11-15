using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsSimulation;

/// <summary>
/// The bitmaps the gameplay panel draws for Sims and queued actions, taken from the original resource files: a Sim's
/// BMP_ 2002 "faces" from its character file (three expressions), and an object's catalog bitmap (OBJD field 41) for an
/// action on it. The client reads them from the session directory under a name derived from their content.
/// </summary>
internal sealed class PanelImages(string directory, ObjectRegistry registry)
{
    private static readonly ResourceType Bmp = ResourceType.Parse("BMP_");
    private static readonly ResourceType Spr2 = ResourceType.Spr2;
    private static readonly ResourceType Spr = ResourceType.Spr1;
    private static readonly ResourceType Palt = ResourceType.Palt;

    private static readonly Dictionary<ushort, string> DefaultUiGraphicsPaths = new()
    {
        [107] = "CPanel/Buttons/UnknownFace.bmp",
        [598] = "CPanel/CatalogUnknown.bmp",
        [1103] = "CPanel/Buttons/CamFrameLarge.bmp",
        [1104] = "CPanel/Buttons/CamFrameCust.bmp",
        [2008] = "CPanel/Buttons/Objects.bmp",
        [2009] = "CPanel/Buttons/Options.bmp",
        [2010] = "CPanel/Buttons/People.bmp",
        [2011] = "CPanel/Buttons/RotRight.bmp",
        [2026] = "CPanel/Buttons/pause.bmp",
        [4301] = "cpanel/Catalog/ThumbTemplate1Frame.BMP",
        [4302] = "CPanel/People/PeopleTemplate.bmp",
        [4500] = "CPanel/Buttons/Mood.bmp",
        [9005] = "Other/TragedyMask.bmp",
    };

    private readonly Dictionary<(IffFile File, ushort Id), object?> stored = new();
    private readonly Dictionary<string, object?> storedCustom = new(StringComparer.Ordinal);
    private bool uiGraphicsInitialized;
    private string? uiGraphicsRoot;
    private FarArchive? uiGraphicsFar;
    private Dictionary<ushort, string>? uiGraphicsTable;

    public object? Portrait(SimulationWorld world, XPerson person)
    {
        var key = person.TypeRecord?.Key ?? (world.Neighbors.TryGetValue(person[PersonData.NeighborId], out var neighbor) ? neighbor.TypeRecordKey : 0);
        if (key != 0 && Portrait((uint)key) is { } portrait) return portrait;
        if ((person.TypeRecord?.BehaviorScope as BehaviorScope)?.Private is { } scopeFile)
        {
            if (Store(scopeFile, 2002, 3) is { } scopePortrait) return scopePortrait;
            if (Store(scopeFile, 2006, 1) is { } alt2006) return alt2006;
            if (Store(scopeFile, 2005, 1) is { } alt2005) return alt2005;
        }
        // FUN_004e8d99 (004e8d99.c:216-226): falls back to UI resource 107 (0x6b, UnknownFace.bmp, 4x3) when no face bitmap is available.
        return StoreUiBitmapById(107, 4, 3);
    }

    public object? Portrait(uint guid)
    {
        if (registry.Find(guid) is not { } entry) return null;
        return Store(entry.File, 2002, 3)
            ?? Store(entry.File, 2006, 1)
            ?? Store(entry.File, 2005, 1);
    }

    /// <summary>The Relationships panel's five-expression portrait strip (character BMP_ 2003).</summary>
    public object? NeighborPortrait(uint guid) => registry.Find(guid) is { } entry ? Store(entry.File, 2003, 5) : null;

    /// <summary>The queue icon for an action on <paramref name="owner"/>: a Sim's face, or the object's catalog bitmap.</summary>
    public object? Icon(SimulationWorld world, XObject? owner)
    {
        if (owner is null) return null;
        if (owner is XPerson person) return Portrait(world, person);

        var guid = (uint)(owner.TypeRecord?.Key ?? 0);
        if (guid == 0 && owner.Definition.Length > (int)ObjdField.Guid2)
        {
            var g1 = (uint)unchecked((ushort)owner.Definition[(int)ObjdField.Guid1]);
            var g2 = (uint)unchecked((ushort)owner.Definition[(int)ObjdField.Guid2]);
            guid = g1 | (g2 << 16);
        }
        if (owner.Definition.Length > (int)ObjdField.Type && owner.Definition[(int)ObjdField.Type] == XPerson.PersonType)
        {
            return (guid != 0 ? Portrait(guid) : null) ?? StoreUiBitmapById(107, 4, 3);
        }

        if (guid != 0)
        {
            // FUN_004ea33f (004ea33f.c:83) -> FUN_004573f9 / FUN_0043aeef: resolves multi-tile sub-objects to the
            // master cXObjType (SubIndex == -1) before querying cProduct::GetQueueIcon (FUN_004ccc2f).
            if (registry.Find(guid) is { } entry)
            {
                var masterEntry = registry.FindAlternativeType(entry) ?? entry;
                if (ItemIcon(masterEntry.Guid) is { } masterBmp) return masterBmp;
                if (masterEntry.Guid != guid && ItemIcon(guid) is { } subBmp) return subBmp;
                if (ObjectCatalog.CatalogIcon(masterEntry, directory) is { } masterCatalog) return masterCatalog;
                if (masterEntry.Guid != guid && ObjectCatalog.CatalogIcon(entry, directory) is { } subCatalog) return subCatalog;
                if (Store(entry.File, 2002, 3) is { } entryFace) return entryFace;
            }
        }

        if ((owner.TypeRecord?.BehaviorScope as BehaviorScope)?.Private is { } scopeFile)
        {
            var id = owner.Definition.Length > (int)ObjdField.CatalogStringsId
                ? MasterCatalogStringsId(scopeFile, owner.Definition)
                : (ushort)0;
            if (id != 0 && Store(scopeFile, id, 0) is { } scopeBmp) return scopeBmp;
            if (Store(scopeFile, 2000, 0) is { } scope2000) return scope2000;
            if (Store(scopeFile, 2002, 3) is { } scopeFace) return scopeFace;
        }

        // FUN_004cbf52 (004cbf52.c:297-300) & FUN_004cd130 (004cd130.c:13-25): when an object's product has neither a
        // BMP_ icon nor a renderable DGRP (e.g. non-graphic social/controller helper objects), Sims.exe assigns static UI
        // resource 0x256 (598, CPanel/CatalogUnknown.bmp, 90x45, 2 columns x 1 row).
        return StoreUiBitmapById(598, 2, 1);
    }

    /// <summary>The tutorial icon bitmap is resource 300 in the current tutorial owner's private IFF, as cTutorialIcon::ObjectChanged does.</summary>
    public object? TutorialIcon(SimulationWorld world)
    {
        var scope = world.TutorialOwner?.TypeRecord?.BehaviorScope as BehaviorScope;
        if (scope is null || scope.Private.Find(Bmp, 300) is not { } chunk) return null;
        var data = chunk.Data.Span;
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        if (width <= 0 || width > 512 || height == 0 || height < -512 || height > 512) return null;
        // cTutorialIcon starts with one column and six rows; SetIcon switches the
        // row count to one only for a 45-pixel-high bitmap.
        return Store(scope.Private, 300, 1, Math.Abs(height) == 45 ? 1 : 6);
    }

    /// <summary>Dialog icon art (FUN_004360c1 / FUN_00524715): resolves mode 0 (Auto), mode 2 (Neighbor),
    /// mode 3 (Indexed 5000 + operand[1]), and mode 4 (Dynamic STR# 301 specs: my, gz, gzi, guid, rel, job).</summary>
    public object? DialogImage(SimulationDialogRecord record)
    {
        if (record.ImageFile is { } imageFile)
        {
            if (imageFile.Source.StartsWith("dialog-spec:", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDynamicSpecImage(record, imageFile.Source["dialog-spec:".Length..].Trim());
            }
            if (imageFile.Source.StartsWith("dialog-neighbor:", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNeighborSpecImage(imageFile.Source["dialog-neighbor:".Length..]);
            }
            if (record.ImageBitmapId != 0)
            {
                return Store(imageFile, record.ImageBitmapId, 1, 1)
                    ?? StoreSpriteFrame(imageFile, record.ImageBitmapId);
            }
        }
        return record.Icon is { } iconObj ? ResolveObjectOrPersonDialogThumbnail(iconObj) : null;
    }

    /// <summary>An inventory tile's bitmap (FUN_0050e555): the object's catalog icon, resolved by GUID.</summary>
    public object? ItemIcon(uint guid)
    {
        if (registry.Find(guid) is not { } entry) return null;
        var masterEntry = registry.FindAlternativeType(entry) ?? entry;
        var id = MasterCatalogStringsId(masterEntry.File, masterEntry.Definition);
        if (id == 0) id = MasterCatalogStringsId(entry.File, entry.Definition);
        return Store(masterEntry.File, id, 0) ?? Store(entry.File, id, 0);
    }

    /// <summary>An inventory tile's name: the first catalog string, or the original's "Unknown Item (%d)" fallback.</summary>
    public string ItemName(uint guid, byte language) =>
        registry.Find(guid)?.CatalogString(0, language) is { Length: > 0 } name ? name : $"Unknown Item ({(int)guid})";

    /// <summary>The second catalog string carried to the original inventory hover tooltip.</summary>
    public string ItemDescription(uint guid, byte language) =>
        registry.Find(guid)?.CatalogString(1, language) ?? "";

    private static ushort MasterCatalogStringsId(IffFile file, Objd definition)
    {
        var id = definition[ObjdField.CatalogStringsId];
        if (definition[ObjdField.MasterId] != 0 && definition[ObjdField.SubIndex] != 0xFFFF)
        {
            foreach (var chunk in file.OfType(ResourceType.Objd))
            {
                try
                {
                    var master = Objd.Read(file, chunk);
                    if (master[ObjdField.MasterId] == definition[ObjdField.MasterId] && master[ObjdField.SubIndex] == 0xFFFF)
                        return master[ObjdField.CatalogStringsId];
                }
                catch (ResourceFormatException) { }
            }
        }
        return id;
    }

    private static ushort MasterCatalogStringsId(IffFile file, IReadOnlyList<short> definition)
    {
        var id = definition.Count > (int)ObjdField.CatalogStringsId
            ? unchecked((ushort)definition[(int)ObjdField.CatalogStringsId])
            : (ushort)0;
        var masterId = definition.Count > (int)ObjdField.MasterId
            ? unchecked((ushort)definition[(int)ObjdField.MasterId])
            : (ushort)0;
        var subIndex = definition.Count > (int)ObjdField.SubIndex
            ? unchecked((ushort)definition[(int)ObjdField.SubIndex])
            : (ushort)0;
        if (masterId != 0 && subIndex != 0xFFFF)
        {
            foreach (var chunk in file.OfType(ResourceType.Objd))
            {
                try
                {
                    var master = Objd.Read(file, chunk);
                    if (master[ObjdField.MasterId] == masterId && master[ObjdField.SubIndex] == 0xFFFF)
                        return master[ObjdField.CatalogStringsId];
                }
                catch (ResourceFormatException) { }
            }
        }
        return id;
    }

    private object? ResolveDynamicSpecImage(SimulationDialogRecord record, string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        var cacheKey = $"spec:{spec}";
        if (storedCustom.TryGetValue(cacheKey, out var cached)) return cached;

        var spaceIndex = spec.IndexOf(' ');
        var command = (spaceIndex < 0 ? spec : spec[..spaceIndex]).Trim();
        var arg = spaceIndex < 0 ? "" : spec[(spaceIndex + 1)..].Trim();

        object? result = null;
        if (command.Equals("gz", StringComparison.OrdinalIgnoreCase) && TryParseUshort(arg, out var gzId))
        {
            // FUN_004360c1 (004360c1.c:340-377): loads UI bitmap by resource ID and crops 1st of 3 horizontal frames.
            if (ReadUiGraphicsBitmapById(gzId) is { } raw)
                result = StoreCroppedHorizontalFrame(raw, 0, 3, compositeOnThumbTemplate: false);
        }
        else if (command.Equals("gzi", StringComparison.OrdinalIgnoreCase) && TryParseUshort(arg, out var gziId))
        {
            // FUN_004360c1 (004360c1.c:378-380): loads UI bitmap by resource ID as a single-frame image.
            if (ReadUiGraphicsBitmapById(gziId) is { } raw)
                result = StoreRawBmpBytes(raw, 1, 1);
        }
        else if (command.Equals("guid", StringComparison.OrdinalIgnoreCase) && TryParseUint(arg, out var guid))
        {
            // FUN_004360c1 (004360c1.c:381-391) -> selector thumbnail (004360c1.c:195-303 + FUN_00524715).
            result = ResolveEntryDialogThumbnail(registry.Find(guid));
        }
        else if (command.Equals("rel", StringComparison.OrdinalIgnoreCase))
        {
            // FUN_004360c1 (004360c1.c:422-477): loads character BMP_ 2003 (0x7d3) and crops the 3rd of 5 frames (index 2).
            IffFile? charFile = null;
            if (TryParseUint(arg, out var relGuid) && relGuid != 0 && registry.Find(relGuid) is { } relEntry)
                charFile = relEntry.File;
            charFile ??= (record.Icon?.TypeRecord?.BehaviorScope as BehaviorScope)?.Private;
            if (charFile?.Find(Bmp, 2003) is { } chunk)
                result = StoreCroppedHorizontalFrame(chunk.Data.ToArray(), 2, 5, compositeOnThumbTemplate: false);
        }
        else if (command.Equals("job", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jobIndex) && jobIndex >= 1)
        {
            // FUN_004360c1 (004360c1.c:479-500): loads CPanel\Backgrounds\JobIconMultiPopup.bmp and crops frame (jobIndex - 1) of 11.
            if (ReadUiGraphicsFile("CPanel/Backgrounds/JobIconMultiPopup.bmp") is { } raw)
                result = StoreCroppedHorizontalFrame(raw, jobIndex - 1, 11, compositeOnThumbTemplate: false);
        }

        return storedCustom[cacheKey] = result;
    }

    private object? ResolveNeighborSpecImage(string neighborPayload)
    {
        var cacheKey = $"neighbor:{neighborPayload}";
        if (storedCustom.TryGetValue(cacheKey, out var cached)) return cached;

        var colon = neighborPayload.IndexOf(':');
        var hexGuid = colon < 0 ? neighborPayload : neighborPayload[..colon];
        var name = colon < 0 ? "" : neighborPayload[(colon + 1)..];
        ObjectEntry? entry = null;
        if (uint.TryParse(hexGuid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var guid) && guid != 0)
            entry = registry.Find(guid);
        if (entry is null && name.Length > 0)
        {
            entry = registry.Entries.FirstOrDefault(candidate =>
                candidate.Scan == ObjectScan.People &&
                Path.GetFileNameWithoutExtension(candidate.Source.Split('#')[0]).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        return storedCustom[cacheKey] = ResolveEntryDialogThumbnail(entry);
    }

    private object? ResolveObjectOrPersonDialogThumbnail(XObject iconObj)
    {
        var guid = (uint)(iconObj.TypeRecord?.Key ?? 0);
        var scopeFile = (iconObj.TypeRecord?.BehaviorScope as BehaviorScope)?.Private;
        if (iconObj is XPerson)
        {
            var entryFile = guid != 0 ? registry.Find(guid)?.File : null;
            return ResolvePersonDialogThumbnail(scopeFile, entryFile);
        }

        if (guid != 0 && registry.Find(guid) is { } objEntry)
            return ResolveEntryDialogThumbnail(objEntry);

        if (scopeFile is not null)
        {
            var bmpId = unchecked((ushort)iconObj.Definition[(int)ObjdField.CatalogStringsId]);
            if (iconObj.Definition[(int)ObjdField.MasterId] != 0 && unchecked((ushort)iconObj.Definition[(int)ObjdField.SubIndex]) != 0xFFFF)
            {
                foreach (var chunk in scopeFile.OfType(ResourceType.Objd))
                {
                    try
                    {
                        var master = Objd.Read(scopeFile, chunk);
                        if (master[ObjdField.MasterId] == unchecked((ushort)iconObj.Definition[(int)ObjdField.MasterId]) && master[ObjdField.SubIndex] == 0xFFFF)
                        {
                            bmpId = master[ObjdField.CatalogStringsId];
                            break;
                        }
                    }
                    catch (ResourceFormatException) { }
                }
            }
            if (bmpId != 0 && scopeFile.Find(Bmp, bmpId) is { } bmpChunk)
            {
                var data = bmpChunk.Data.ToArray();
                var width = data.Length >= 22 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)) : 0;
                return StoreObjectDialogBox(data, 0, width >= 46 ? 2 : 1);
            }
        }
        return null;
    }

    private object? ResolveEntryDialogThumbnail(ObjectEntry? entry)
    {
        if (entry is null) return null;
        var cacheKey = $"thumb:{entry.Guid:X8}:{entry.Source}";
        if (storedCustom.TryGetValue(cacheKey, out var cached)) return cached;

        object? result = null;
        if (entry.Scan == ObjectScan.People || entry.File.Find(Bmp, 2002) is not null)
        {
            result = ResolvePersonDialogThumbnail(entry.File, null);
        }
        else
        {
            if (ObjectCatalog.RenderedDialogPreview(entry) is { } rendered)
            {
                result = StoreObjectDialogBox(rendered, 0, 1);
            }
            else
            {
                var bmpId = MasterCatalogStringsId(entry.File, entry.Definition);
                if (bmpId != 0 && entry.File.Find(Bmp, bmpId) is { } bmpChunk)
                {
                    var data = bmpChunk.Data.ToArray();
                    var width = data.Length >= 22 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)) : 0;
                    result = StoreObjectDialogBox(data, 0, width >= 46 ? 2 : 1);
                }
            }
        }

        return storedCustom[cacheKey] = result;
    }

    private object? ResolvePersonDialogThumbnail(IffFile? primaryFile, IffFile? secondaryFile)
    {
        IffChunk? FindPersonBmp(ushort id) => primaryFile?.Find(Bmp, id) ?? secondaryFile?.Find(Bmp, id);

        if (FindPersonBmp(2002) is { } faceChunk)
            return StorePersonDialogThumbnail(faceChunk.Data.ToArray(), keyCornerColor: false);
        if (FindPersonBmp(2006) is { } largeSpeechChunk)
            return StorePersonDialogThumbnail(largeSpeechChunk.Data.ToArray(), keyCornerColor: true);
        if (FindPersonBmp(2005) is { } mediumSpeechChunk)
            return StorePersonDialogThumbnail(mediumSpeechChunk.Data.ToArray(), keyCornerColor: true);
        if (ReadUiGraphicsBitmapById(107) is { } unknownFaceBytes)
            return StorePersonDialogThumbnail(unknownFaceBytes, keyCornerColor: false);
        return null;
    }

    /// <summary>004e8d99.c + 004e739b.c + 004e93e4.c: detects face sheet grid dimensions so 105x41 / 108x41 3x1
    /// BMP_ #2002 strips, 3x3 face sheets, 4x3 UnknownFace.bmp (180x135 / 140x123), and single-frame BMP_ #2006/#2005
    /// all crop their neutral (col = 0, row = 0) cell cleanly.</summary>
    internal static (int Columns, int Rows) PersonFaceSheetGrid(int width, int height)
    {
        if ((width is 140 or 180) && height >= 120) return (4, 3);
        if (width >= 90 && height >= 90) return (3, 3);
        if (width >= 90) return (3, 1);
        return (1, 1);
    }

    internal static (int Width, int Height, byte[] Rgba) CompositePersonDialogThumbnail(
        int width, int height, byte[] rgba, byte[]? peopleTemplateBmp, bool keyCornerColor = false)
    {
        var (cols, rows) = PersonFaceSheetGrid(width, height);
        var frameW = Math.Max(1, width / cols);
        var frameH = Math.Max(1, height / rows);
        var keyR5 = rgba.Length >= 4 ? rgba[0] >> 3 : -1;
        var keyG6 = rgba.Length >= 4 ? rgba[1] >> 2 : -1;
        var keyB5 = rgba.Length >= 4 ? rgba[2] >> 3 : -1;

        if (peopleTemplateBmp is not null)
        {
            var (tplFullW, tplH, tplFullRgba) = BitmapCodec.Read(peopleTemplateBmp);
            var tplW = tplFullW >= 180 ? tplFullW / 4 : tplFullW;
            var outRgba = new byte[tplW * tplH * 4];
            for (var y = 0; y < tplH; y++)
                Buffer.BlockCopy(tplFullRgba, y * tplFullW * 4, outRgba, y * tplW * 4, tplW * 4);

            // PeopleTemplate.bmp (45x45) has a 2px outer bevel on all four sides (inner area [2..42]); clamp
            // destination inside that inner area so wider cells (e.g. UnknownFace.bmp) never overwrite the bevel.
            var bevelX = tplW > 4 ? 2 : 0;
            var bevelY = tplH > 4 ? 2 : 0;
            var minDstX = bevelX;
            var maxDstX = tplW - bevelX;
            var minDstY = bevelY;
            var maxDstY = tplH - bevelY;
            var offsetX = (tplW - frameW) / 2;
            var offsetY = (tplH - frameH) / 2;

            for (var y = 0; y < frameH; y++)
            {
                var dstY = offsetY + y;
                if (dstY < minDstY || dstY >= maxDstY) continue;
                for (var x = 0; x < frameW; x++)
                {
                    var dstX = offsetX + x;
                    if (dstX < minDstX || dstX >= maxDstX) continue;
                    var srcIdx = (y * width + x) * 4;
                    if (rgba[srcIdx + 3] == 0) continue;
                    var r = rgba[srcIdx];
                    var g = rgba[srcIdx + 1];
                    var b = rgba[srcIdx + 2];
                    if (r == 255 && g == 0 && b == 255) continue;
                    if (keyCornerColor && (r >> 3) == keyR5 && (g >> 2) == keyG6 && (b >> 3) == keyB5) continue;
                    var dstIdx = (dstY * tplW + dstX) * 4;
                    outRgba[dstIdx] = r;
                    outRgba[dstIdx + 1] = g;
                    outRgba[dstIdx + 2] = b;
                    outRgba[dstIdx + 3] = 255;
                }
            }
            return (tplW, tplH, outRgba);
        }

        var cropped = new byte[frameW * frameH * 4];
        for (var y = 0; y < frameH; y++)
            Buffer.BlockCopy(rgba, y * width * 4, cropped, y * frameW * 4, frameW * 4);
        return (frameW, frameH, cropped);
    }

    /// <summary>004360c1.c + FUN_004e93e4 + FUN_004e71be: composites neutral frame (0, 0) of a Sim's face sheet
    /// onto frame 0 (45x45) of <c>CPanel/People/PeopleTemplate.bmp</c>.</summary>
    private object? StorePersonDialogThumbnail(byte[] faceSheetData, bool keyCornerColor = false)
    {
        try
        {
            var (width, height, rgba) = BitmapCodec.Read(faceSheetData);
            var (outW, outH, outRgba) = CompositePersonDialogThumbnail(
                width, height, rgba, ReadUiGraphicsBitmapById(4302), keyCornerColor);
            return StoreRawBmpBytes(BitmapCodec.Write(outW, outH, outRgba), 1, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>004360c1.c:230-285 + FUN_004cbd1c: draws an object preview centered inside a 120x120 white box with a 1px dark border.</summary>
    private object? StoreObjectDialogBox(byte[] bmpData, int frameIndex, int frameCount)
    {
        try
        {
            var (width, height, rgba) = BitmapCodec.Read(bmpData);
            var safeCount = Math.Max(1, frameCount);
            var frameWidth = Math.Max(1, width / safeCount);
            var clampedIndex = Math.Clamp(frameIndex, 0, safeCount - 1);
            var startX = clampedIndex * frameWidth;
            if (startX + frameWidth > width) frameWidth = width - startX;

            const int BoxW = 120;
            const int BoxH = 120;
            var boxRgba = new byte[BoxW * BoxH * 4];
            for (var y = 0; y < BoxH; y++)
            {
                for (var x = 0; x < BoxW; x++)
                {
                    var border = x == 0 || y == 0 || x == BoxW - 1 || y == BoxH - 1;
                    var idx = (y * BoxW + x) * 4;
                    boxRgba[idx] = border ? (byte)0 : (byte)255;
                    boxRgba[idx + 1] = border ? (byte)0 : (byte)255;
                    boxRgba[idx + 2] = border ? (byte)52 : (byte)255;
                    boxRgba[idx + 3] = 255;
                }
            }

            var offsetX = (BoxW - frameWidth) / 2;
            var offsetY = (BoxH - height) / 2;
            for (var y = 0; y < height; y++)
            {
                var dstY = offsetY + y;
                if (dstY <= 0 || dstY >= BoxH - 1) continue;
                for (var x = 0; x < frameWidth; x++)
                {
                    var dstX = offsetX + x;
                    if (dstX <= 0 || dstX >= BoxW - 1) continue;
                    var srcIdx = (y * width + startX + x) * 4;
                    var r = rgba[srcIdx];
                    var g = rgba[srcIdx + 1];
                    var b = rgba[srcIdx + 2];
                    if (r == 255 && g == 0 && b == 255) continue;
                    var dstIdx = (dstY * BoxW + dstX) * 4;
                    boxRgba[dstIdx] = r;
                    boxRgba[dstIdx + 1] = g;
                    boxRgba[dstIdx + 2] = b;
                    boxRgba[dstIdx + 3] = 255;
                }
            }
            return StoreRawBmpBytes(BitmapCodec.Write(BoxW, BoxH, boxRgba), 1, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Crops horizontal frame <paramref name="frameIndex"/> of <paramref name="frameCount"/> and optionally
    /// composites it centered onto <c>cpanel\Catalog\ThumbTemplate1Frame.BMP</c> (resource 4301) per FUN_00524715.</summary>
    private object? StoreCroppedHorizontalFrame(byte[] bmpData, int frameIndex, int frameCount, bool compositeOnThumbTemplate)
    {
        try
        {
            var (width, height, rgba) = BitmapCodec.Read(bmpData);
            var safeCount = Math.Max(1, frameCount);
            var frameWidth = Math.Max(1, width / safeCount);
            var clampedIndex = Math.Clamp(frameIndex, 0, safeCount - 1);
            var startX = clampedIndex * frameWidth;
            if (startX + frameWidth > width) frameWidth = width - startX;

            var cropped = new byte[frameWidth * height * 4];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgba, (y * width + startX) * 4, cropped, y * frameWidth * 4, frameWidth * 4);
            }

            if (compositeOnThumbTemplate && ReadUiGraphicsBitmapById(4301) is { } templateBytes)
            {
                var (tplW, tplH, tplRgba) = BitmapCodec.Read(templateBytes);
                var offsetX = (tplW - frameWidth) / 2;
                var offsetY = (tplH - height) / 2;
                for (var y = 0; y < height; y++)
                {
                    var dstY = offsetY + y;
                    if ((uint)dstY >= (uint)tplH) continue;
                    for (var x = 0; x < frameWidth; x++)
                    {
                        var dstX = offsetX + x;
                        if ((uint)dstX >= (uint)tplW) continue;
                        var srcIdx = (y * frameWidth + x) * 4;
                        var r = cropped[srcIdx];
                        var g = cropped[srcIdx + 1];
                        var b = cropped[srcIdx + 2];
                        if (r == 255 && g == 0 && b == 255) continue;
                        var dstIdx = (dstY * tplW + dstX) * 4;
                        tplRgba[dstIdx] = r;
                        tplRgba[dstIdx + 1] = g;
                        tplRgba[dstIdx + 2] = b;
                        tplRgba[dstIdx + 3] = 255;
                    }
                }
                return StoreRawBmpBytes(BitmapCodec.Write(tplW, tplH, tplRgba), 1, 1);
            }

            if (safeCount == 1)
                return StoreRawBmpBytes(bmpData, 1, 1);
            return StoreRawBmpBytes(BitmapCodec.Write(frameWidth, height, cropped), 1, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private object? StoreSpriteFrame(IffFile file, ushort id)
    {
        if (stored.TryGetValue((file, id), out var cached) && cached is not null) return cached;
        var chunk = file.Find(Spr2, id) ?? file.Find(Spr, id);
        if (chunk is null) return null;
        try
        {
            int width, height, paletteId;
            Func<int, bool> isOpaque;
            Func<int, byte> indexAt;
            if (chunk.Type == Spr2)
            {
                var art = DepthSprite.Read(file, chunk);
                if (art.Frames.Count == 0) return null;
                var frame = art.Frames[0];
                width = frame.Width; height = frame.Height; paletteId = art.PaletteId;
                isOpaque = pixel => frame.Alpha[pixel] != 0;
                indexAt = pixel => frame.Indices[pixel];
            }
            else
            {
                var art = Sprite.Read(file, chunk);
                if (art.Frames.Count == 0) return null;
                var frame = art.Frames[0];
                width = frame.Width; height = frame.Height; paletteId = art.PaletteId;
                isOpaque = pixel => frame.Opaque[pixel];
                indexAt = pixel => frame.Indices[pixel];
            }
            if (width <= 0 || height <= 0) return null;
            var palId = paletteId is > 0 and < 1000 ? (ushort)paletteId : (ushort)0;
            var paletteChunk = file.Find(Palt, palId) ?? file.OfType(Palt).FirstOrDefault();
            if (paletteChunk is null) return null;
            var colors = Palette.Read(file, paletteChunk).Colors;
            var rgba = new byte[width * height * 4];
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var at = pixel * 4;
                var index = indexAt(pixel);
                if (!isOpaque(pixel) || index >= colors.Count)
                {
                    rgba[at] = 255;
                    rgba[at + 1] = 0;
                    rgba[at + 2] = 255;
                    rgba[at + 3] = 255;
                }
                else
                {
                    var color = colors[index];
                    rgba[at] = color.Red;
                    rgba[at + 1] = color.Green;
                    rgba[at + 2] = color.Blue;
                    rgba[at + 3] = 255;
                }
            }
            return stored[(file, id)] = StoreRawBmpBytes(BitmapCodec.Write(width, height, rgba), 1, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private byte[]? ReadUiGraphicsBitmapById(ushort id)
    {
        EnsureUiGraphicsLoaded();
        if (uiGraphicsTable is not null && uiGraphicsTable.TryGetValue(id, out var mappedPath))
        {
            if (ReadUiGraphicsFile(mappedPath) is { } bytes) return bytes;
        }
        if (DefaultUiGraphicsPaths.TryGetValue(id, out var fallbackPath))
        {
            if (ReadUiGraphicsFile(fallbackPath) is { } bytes) return bytes;
        }
        return null;
    }

    private byte[]? ReadUiGraphicsFile(string relativePath)
    {
        EnsureUiGraphicsLoaded();
        var clean = relativePath.TrimStart('/', '\\').Replace('\\', '/');
        if (uiGraphicsRoot is not null)
        {
            var direct = Path.Combine(uiGraphicsRoot, clean.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(direct)) return File.ReadAllBytes(direct);
        }
        if (uiGraphicsFar?.Find(clean) is { } entry)
        {
            return uiGraphicsFar.Read(entry).ToArray();
        }
        return null;
    }

    private void EnsureUiGraphicsLoaded()
    {
        if (uiGraphicsInitialized) return;
        uiGraphicsInitialized = true;

        var candidatePaths = new List<string>();
        foreach (var (_, folder) in registry.Folders)
            if (!string.IsNullOrEmpty(folder)) candidatePaths.Add(folder);
        foreach (var (_, archive) in registry.Archives)
            if (!string.IsNullOrEmpty(archive)) candidatePaths.Add(archive);
        if (candidatePaths.Count == 0 && registry.Entries.Count > 0)
            candidatePaths.Add(registry.Entries[0].Source.Split('#')[0]);

        foreach (var start in candidatePaths)
        {
            var current = Directory.Exists(start) ? new DirectoryInfo(start) : new FileInfo(start).Directory;
            while (current is not null)
            {
                var candidateUi = Path.Combine(current.FullName, "UIGraphics");
                if (Directory.Exists(candidateUi))
                {
                    uiGraphicsRoot = candidateUi;
                    var farPath = Path.Combine(candidateUi, "UIGraphics.far");
                    if (File.Exists(farPath))
                    {
                        try { uiGraphicsFar = FarArchive.Open(farPath); }
                        catch (Exception) { }
                    }
                    break;
                }
                current = current.Parent;
            }
            if (uiGraphicsRoot is not null) break;
        }

        uiGraphicsTable = new Dictionary<ushort, string>(DefaultUiGraphicsPaths);
        if (uiGraphicsFar is null) return;

        // FUN_005a5631 / FUN_005a47ae: pair Res_*.h (#define NAME ID) with Res_*.RT (NAME = path).
        var symbolIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in uiGraphicsFar.Entries.Where(e => e.Name.EndsWith(".h", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Encoding.Latin1.GetString(uiGraphicsFar.Read(entry).Span);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("#define ", StringComparison.Ordinal)) continue;
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && TryParseUshort(parts[2], out var id))
                    symbolIds[parts[1]] = id;
            }
        }
        foreach (var entry in uiGraphicsFar.Entries.Where(e => e.Name.EndsWith(".RT", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Encoding.Latin1.GetString(uiGraphicsFar.Read(entry).Span);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var name = line[..eq].Trim();
                var path = line[(eq + 1)..].Trim();
                if (symbolIds.TryGetValue(name, out var id) && path.Length > 0)
                    uiGraphicsTable[id] = path;
            }
        }
    }

    private static bool TryParseUshort(string text, out ushort value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseUint(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            value = unchecked((uint)signed);
            return true;
        }
        value = 0;
        return false;
    }

    private object? StoreUiBitmapById(ushort id, int columns, int rows)
    {
        var cacheKey = $"ui:{id}:{columns}:{rows}";
        if (storedCustom.TryGetValue(cacheKey, out var cached)) return cached;
        object? result = null;
        if (ReadUiGraphicsBitmapById(id) is { } raw)
            result = StoreRawBmpBytes(raw, columns, rows);
        return storedCustom[cacheKey] = result;
    }

    // columns 0 means "one frame, or two when width >= 46 (0x2e)" as in FUN_004ccc2f (004ccc2f.c:118-124).
    private object? Store(IffFile file, ushort id, int columns)
        => Store(file, id, columns, 1);

    private object? Store(IffFile file, ushort id, int columns, int rows)
    {
        if (id == 0) return null;
        if (stored.TryGetValue((file, id), out var cached)) return cached;
        object? result = null;
        if (file.Find(Bmp, id) is { } chunk)
        {
            result = StoreRawBmpBytes(chunk.Data.ToArray(), columns, rows);
        }
        return stored[(file, id)] = result;
    }

    private object? StoreRawBmpBytes(byte[] data, int columns, int rows)
    {
        if (data.Length is >= 54 and <= 4 * 1024 * 1024 && data[0] == 'B' && data[1] == 'M')
        {
            var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
            var height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
            var frames = columns != 0 ? columns : width >= 46 ? 2 : 1;
            if (width >= frames && width <= 512 && height != 0 && Math.Abs(height) <= 512
                && width % frames == 0 && Math.Abs(height) % rows == 0)
            {
                var name = "ui-" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ".bmp";
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) File.WriteAllBytes(path, data);
                return new { file = name, columns = frames, rows };
            }
        }
        return null;
    }
}
