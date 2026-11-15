using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SimsEngine.Formats;

/// <summary>
/// Lists objects in a Sims 1 or The Sims Online object tree as TSV for candidate selection, and merges TSO object packs (.iff + .spf + .otf) into a single Sims 1-compatible .iff.
/// </summary>
static class TsoInventory
{
    public static int Run(string root, string output)
    {
        var rows = new StringBuilder("file\tguid\tobject_name\tcatalog_name\tprice\tbhavs\tsprites\tdgrps\tmax_opcode\n");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".far", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in ReadEntries(path))
            {
                var name = Path.GetFileName(entry.Key);
                if (!name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                IffFile iff;
                try
                {
                    iff = new IffFile(entry.Value, name);
                }
                catch
                {
                    continue;
                }
                var bhavs = new List<Bhav>();
                foreach (var chunk in iff.OfType(ResourceType.Bhav))
                {
                    try { bhavs.Add(Bhav.Read(iff, chunk)); }
                    catch { }
                }
                var sprites = iff.OfType(ResourceType.Spr2).Count() + iff.OfType(ResourceType.Spr1).Count();
                var dgrps = iff.OfType(ResourceType.Dgrp).Count();
                var maxOpcode = bhavs.SelectMany(b => b.Instructions).Where(i => i.Opcode < 256).Select(i => (int)i.Opcode).DefaultIfEmpty(-1).Max();
                foreach (var objdChunk in iff.OfType(ResourceType.Objd))
                {
                    Objd objd;
                    try { objd = Objd.Read(iff, objdChunk); }
                    catch { continue; }
                    var catalog = "";
                    if (iff.Find(ResourceType.Ctss, objd[ObjdField.CatalogStringsId]) is { } ctssChunk)
                    {
                        try { catalog = StringTable.Read(iff, ctssChunk).GetString(0, 1); }
                        catch { }
                    }
                    rows.Append($"{name}\t{objd.Guid:X8}\t{Clean(objdChunk.Label)}\t{Clean(catalog)}\t{objd[ObjdField.Price]}\t{bhavs.Count}\t{sprites}\t{dgrps}\t{maxOpcode}\n");
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, rows.ToString());
        Console.WriteLine($"Wrote {output}");
        return 0;
    }

    public static int Merge(string root, string objectName, string output)
    {
        var (bytes, summary) = Merged(root, objectName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"Wrote {output} ({summary})");
        return 0;
    }

    /// <summary>
    /// Reads <paramref name="objectName"/> from a TSO objects directory, merging its .spf sprites and .otf tuning tables into the .iff chunk list when present.
    /// </summary>
    public static (byte[] Bytes, string Summary) Merged(string root, string objectName)
    {
        var name = Path.GetFileNameWithoutExtension(objectName);
        var iff = Find(root, name + ".iff") ?? throw new FileNotFoundException($"No {name}.iff under {root}");
        var spf = Find(root, name + ".spf");
        var otf = Find(root, name + ".otf");
        var chunks = ObjectAuthor.ReadIff(iff);
        var spriteChunks = 0;
        if (spf != null)
        {
            foreach (var chunk in ObjectAuthor.ReadIff(spf).Where(c => !chunks.Any(o => o.Type == c.Type && o.Id == c.Id)))
            {
                chunks.Add(chunk);
                spriteChunks++;
            }
        }
        var tuningTables = 0;
        if (otf != null)
        {
            foreach (var table in ReadOtfTables(otf))
            {
                if (chunks.Any(o => o.Type == "BCON" && o.Id == table.Id)) continue;
                var count = table.Keys.Select(k => k.Id + 1).DefaultIfEmpty(0).Max();
                var data = new byte[2 + count * 2];
                data[0] = (byte)count;
                foreach (var key in table.Keys.Where(k => k.Id >= 0))
                    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2 + key.Id * 2), (short)key.Value);
                chunks.Add(new ObjectAuthor.Chunk { Type = "BCON", Id = (ushort)table.Id, Label = table.Name, Data = data });
                tuningTables++;
            }
        }
        return (ObjectAuthor.WriteIff(chunks), $"{name}: {chunks.Count} chunks, {spriteChunks} from the sprite file, {tuningTables} tuning tables");
    }

    static byte[]? Find(string root, string fileName)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                return File.ReadAllBytes(path);
            if (!path.EndsWith(".far", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in ReadEntries(path))
                if (string.Equals(Path.GetFileName(entry.Key), fileName, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
        }
        return null;
    }

    static IEnumerable<KeyValuePair<string, byte[]>> ReadEntries(string path)
    {
        if (path.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
            return [new(Path.GetFileName(path), File.ReadAllBytes(path))];
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return []; }
        if (TryReadFar1b(bytes) is { } far1b)
            return far1b;
        try
        {
            var archive = FarArchive.Open(path);
            return archive.Entries
                .Select(entry => new KeyValuePair<string, byte[]>(entry.Name, archive.Read(entry).ToArray()))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    static List<KeyValuePair<string, byte[]>>? TryReadFar1b(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan(0, 8).SequenceEqual("FAR!byAZ"u8))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != 1)
            return null;
        var manifest = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (manifest < 16 || manifest > bytes.Length - 4)
            return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(manifest));
        if (count < 0 || count > (bytes.Length - manifest - 4) / 14)
            return null;
        var entries = new List<KeyValuePair<string, byte[]>>(count);
        var position = manifest + 4;
        for (var i = 0; i < count; i++)
        {
            if (position > bytes.Length - 14) return null;
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 8));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 12));
            position += 14;
            if (nameLength == 0 || position > bytes.Length - nameLength || size < 0 || offset < 16 || (long)offset + size > bytes.Length)
                return null;
            var name = Encoding.Latin1.GetString(bytes, position, nameLength);
            if (name.Contains('\0'))
                return null;
            position += nameLength;
            entries.Add(new KeyValuePair<string, byte[]>(name, bytes.AsSpan(offset, size).ToArray()));
        }
        return entries;
    }

    sealed record OtfKey(int Id, int Value);
    sealed record OtfTable(int Id, string Name, IReadOnlyList<OtfKey> Keys);

    static IReadOnlyList<OtfTable> ReadOtfTables(byte[] otf)
    {
        var length = otf.Length;
        while (length > 0 && otf[length - 1] == 0) length--;
        if (length == 0) return [];
        using var stream = new MemoryStream(otf, 0, length, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
        });
        var document = XDocument.Load(reader);
        var tables = new List<OtfTable>();
        foreach (var element in document.Descendants().Where(e => string.Equals(e.Name.LocalName, "table", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryParseInt((string?)element.Attribute("id"), out var tableId)) continue;
            var name = (string?)element.Attribute("name") ?? "";
            var keys = new List<OtfKey>();
            foreach (var keyElement in element.Elements().Where(e => string.Equals(e.Name.LocalName, "key", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryParseInt((string?)keyElement.Attribute("id"), out var keyId) &&
                    TryParseInt((string?)keyElement.Attribute("value"), out var keyValue))
                {
                    keys.Add(new OtfKey(keyId, keyValue));
                }
            }
            tables.Add(new OtfTable(tableId, name, keys));
        }
        return tables;
    }

    static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    static string Clean(string? value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
