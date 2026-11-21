using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimsEngine.Formats;

namespace SimsEngine.Content;

public sealed record ManifestChunkEntry(
    string Type,
    ushort Id,
    ushort Flags,
    string Label,
    string ContentHash,
    int Size);

public sealed record IffManifest(
    string ManifestHash,
    string SourceName,
    string HeaderBase64,
    bool HasRsmp,
    int OriginalSizeBytes,
    IReadOnlyList<ManifestChunkEntry> Chunks,
    IReadOnlyList<uint> ObjectGuids,
    string? SemiGlobalName);

public sealed record IngestResult(
    bool Succeeded,
    IffValidationReport Validation,
    IffManifest? Manifest,
    int NewChunksStored,
    int DeduplicatedChunks);

public sealed record ChunkTypeStats(
    string Type,
    int TotalReferences,
    int UniqueChunks,
    long TotalBytes,
    long UniqueBytes)
{
    public long SavedBytes => TotalBytes - UniqueBytes;
    public double DeduplicationRatio => TotalBytes == 0 ? 0.0 : (double)SavedBytes / TotalBytes;
}

public sealed record ChunkStoreStats(
    int ManifestCount,
    long TotalOriginalIffBytes,
    int TotalChunkReferences,
    int UniqueChunkCount,
    long TotalChunkPayloadBytes,
    long UniqueChunkPayloadBytes,
    long StoredChunkBytes,
    long TotalManifestBytes,
    IReadOnlyList<ChunkTypeStats> ByChunkType)
{
    public long StoredTotalBytes => StoredChunkBytes + TotalManifestBytes;
    public double PayloadDeduplicationRatio => TotalChunkPayloadBytes == 0
        ? 0.0
        : (double)(TotalChunkPayloadBytes - UniqueChunkPayloadBytes) / TotalChunkPayloadBytes;
    public double OverallSavingsRatio => TotalOriginalIffBytes == 0
        ? 0.0
        : (double)(TotalOriginalIffBytes - StoredTotalBytes) / TotalOriginalIffBytes;
}

public sealed record ManifestDiff(
    string BaseManifestHash,
    string VariantManifestHash,
    int SharedChunkCount,
    int ModifiedChunkCount,
    int AddedChunkCount,
    int RemovedChunkCount,
    long SharedBytes,
    long DeltaBytes);

/// <summary>Shareable mod/CC delta bundle containing an IFF manifest plus only the custom chunks not already
/// present in the receiver's local base-game chunk store.</summary>
public sealed record ModDeltaBundle(
    IffManifest Manifest,
    IReadOnlyDictionary<string, string> IncludedChunksBase64,
    IReadOnlyList<string> ExternalChunkHashes);

/// <summary>Content-addressed chunk store for Sims 1 IFF objects. Validates chunks on ingestion, deduplicates
/// identical chunk payloads by SHA-256, optionally compresses chunks with Brotli, stores lightweight manifests,
/// exports/imports delta mod bundles, and rebuilds valid IFFs on demand.</summary>
public sealed class ChunkCasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string? _rootDirectory;
    private readonly bool _compressChunks;
    private readonly Dictionary<string, byte[]> _chunks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _storedChunkSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IffManifest> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _manifestSerializedSizes = new(StringComparer.Ordinal);

    private ChunkCasStore(string? rootDirectory, bool compressChunks)
    {
        _rootDirectory = rootDirectory;
        _compressChunks = compressChunks;
    }

    public static ChunkCasStore InMemory(bool compressChunks = false) => new(null, compressChunks);

    public static ChunkCasStore OpenDirectory(string rootDirectory, bool compressChunks = false)
    {
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(Path.Combine(rootDirectory, "chunks"));
        Directory.CreateDirectory(Path.Combine(rootDirectory, "manifests"));

        var store = new ChunkCasStore(rootDirectory, compressChunks);
        var manifestsDir = Path.Combine(rootDirectory, "manifests");
        foreach (var manifestPath in Directory.EnumerateFiles(manifestsDir, "*.json"))
        {
            var json = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<IffManifest>(json, JsonOptions);
            if (manifest is not null)
            {
                store._manifests[manifest.ManifestHash] = manifest;
                store._manifestSerializedSizes[manifest.ManifestHash] = json.Length;
                foreach (var entry in manifest.Chunks)
                {
                    if (!store._storedChunkSizes.ContainsKey(entry.ContentHash))
                    {
                        var path = store.ChunkPath(entry.ContentHash);
                        if (File.Exists(path))
                            store._storedChunkSizes[entry.ContentHash] = (int)new FileInfo(path).Length;
                    }
                }
            }
        }
        return store;
    }

    public IReadOnlyCollection<IffManifest> Manifests => _manifests.Values;

    public bool HasChunk(string contentHash) =>
        _rootDirectory is not null
            ? File.Exists(ChunkPath(contentHash))
            : _chunks.ContainsKey(contentHash);

    public HashSet<string> GetKnownChunkHashes()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in _manifests.Values)
        {
            foreach (var entry in manifest.Chunks)
                hashes.Add(entry.ContentHash);
        }
        return hashes;
    }

    public IngestResult IngestIff(ReadOnlyMemory<byte> iffBytes, string sourceName, ValidationMode mode = ValidationMode.Compatible)
    {
        IffFile file;
        try
        {
            file = new IffFile(iffBytes, sourceName);
        }
        catch (Exception ex) when (ex is ResourceFormatException or ArgumentException or IndexOutOfRangeException)
        {
            var failed = new IffValidationReport(
                sourceName,
                IsValid: false,
                Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "IFF ", 0, ex.Message)],
                ObjectGuids: [],
                SemiGlobalName: null);
            return new IngestResult(false, failed, null, 0, 0);
        }

        var report = IffChunkValidator.Validate(file, mode);
        if (!report.IsValid)
            return new IngestResult(false, report, null, 0, 0);

        var hasRsmp = false;
        var entries = new List<ManifestChunkEntry>(file.Chunks.Count);
        var newChunks = 0;
        var dedupChunks = 0;

        foreach (var chunk in file.Chunks)
        {
            if (chunk.Type == ResourceType.Rsmp)
            {
                hasRsmp = true;
                continue;
            }

            var payload = chunk.Data.Span;
            var hash = ComputeSha256Hex(payload);
            if (StoreChunkIfMissing(hash, payload))
                newChunks++;
            else
                dedupChunks++;

            entries.Add(new ManifestChunkEntry(
                chunk.Type.ToString(),
                chunk.Id,
                chunk.Flags,
                chunk.Label,
                hash,
                payload.Length));
        }

        var header = iffBytes.Span[..IffFile.HeaderSize].ToArray();
        if (hasRsmp)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60, 4), 0);

        var headerBase64 = Convert.ToBase64String(header);
        var provisional = new IffManifest(
            ManifestHash: "",
            SourceName: sourceName,
            HeaderBase64: headerBase64,
            HasRsmp: hasRsmp,
            OriginalSizeBytes: iffBytes.Length,
            Chunks: entries,
            ObjectGuids: report.ObjectGuids,
            SemiGlobalName: report.SemiGlobalName);

        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(provisional, JsonOptions);
        var manifestHash = ComputeSha256Hex(canonicalBytes);
        var manifest = provisional with { ManifestHash = manifestHash };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        _manifests[manifestHash] = manifest;
        _manifestSerializedSizes[manifestHash] = serialized.Length;

        if (_rootDirectory is not null)
        {
            var manifestPath = Path.Combine(_rootDirectory, "manifests", $"{manifestHash}.json");
            if (!File.Exists(manifestPath))
                File.WriteAllBytes(manifestPath, serialized);
        }

        return new IngestResult(true, report, manifest, newChunks, dedupChunks);
    }

    public byte[] RebuildIff(string manifestHash)
    {
        if (!_manifests.TryGetValue(manifestHash, out var manifest))
            throw new KeyNotFoundException($"Manifest '{manifestHash}' not found in store.");

        return RebuildIffFromManifest(manifest, LoadChunkVerified);
    }

    /// <summary>Exports a compressed delta bundle for <paramref name="manifestHash"/> containing only the chunks
    /// not present in <paramref name="receiverKnownChunks"/> (e.g., excluding stock Maxis base-game chunks).</summary>
    public byte[] ExportDeltaBundle(string manifestHash, IReadOnlySet<string>? receiverKnownChunks = null)
    {
        if (!_manifests.TryGetValue(manifestHash, out var manifest))
            throw new KeyNotFoundException($"Manifest '{manifestHash}' not found in store.");

        var included = new Dictionary<string, string>(StringComparer.Ordinal);
        var external = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in manifest.Chunks)
        {
            if (receiverKnownChunks is not null && receiverKnownChunks.Contains(entry.ContentHash))
            {
                external.Add(entry.ContentHash);
            }
            else if (!included.ContainsKey(entry.ContentHash))
            {
                var raw = LoadChunkVerified(entry.ContentHash);
                included[entry.ContentHash] = Convert.ToBase64String(raw);
            }
        }

        var bundle = new ModDeltaBundle(manifest, included, external.OrderBy(static h => h, StringComparer.Ordinal).ToList());
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions);
        return CompressBrotli(json);
    }

    /// <summary>Imports a delta bundle, verifying SHA-256 hashes of all embedded chunks, resolving external chunks
    /// from the local store, and validating the reconstructed IFF before committing anything.</summary>
    public IngestResult ImportDeltaBundle(ReadOnlyMemory<byte> bundleBytes, ValidationMode mode = ValidationMode.Compatible)
    {
        ModDeltaBundle? bundle;
        try
        {
            var json = DecompressBrotli(bundleBytes.Span);
            bundle = JsonSerializer.Deserialize<ModDeltaBundle>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            var failed = new IffValidationReport(
                "bundle",
                IsValid: false,
                Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "BNDL", 0, $"Invalid bundle: {ex.Message}")],
                ObjectGuids: [],
                SemiGlobalName: null);
            return new IngestResult(false, failed, null, 0, 0);
        }

        if (bundle is null)
        {
            var failed = new IffValidationReport(
                "bundle",
                IsValid: false,
                Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "BNDL", 0, "Bundle deserialized to null.")],
                ObjectGuids: [],
                SemiGlobalName: null);
            return new IngestResult(false, failed, null, 0, 0);
        }

        var stagedChunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (expectedHash, b64) in bundle.IncludedChunksBase64)
        {
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(b64);
            }
            catch (FormatException ex)
            {
                var failed = new IffValidationReport(
                    bundle.Manifest.SourceName,
                    IsValid: false,
                    Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "BNDL", 0, $"Invalid base64 for chunk '{expectedHash}': {ex.Message}")],
                    ObjectGuids: [],
                    SemiGlobalName: null);
                return new IngestResult(false, failed, null, 0, 0);
            }

            var actualHash = ComputeSha256Hex(raw);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                var failed = new IffValidationReport(
                    bundle.Manifest.SourceName,
                    IsValid: false,
                    Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "BNDL", 0, $"Chunk hash mismatch: expected '{expectedHash}', got '{actualHash}'.")],
                    ObjectGuids: [],
                    SemiGlobalName: null);
                return new IngestResult(false, failed, null, 0, 0);
            }
            stagedChunks[expectedHash] = raw;
        }

        foreach (var neededHash in bundle.Manifest.Chunks.Select(static c => c.ContentHash).Distinct(StringComparer.Ordinal))
        {
            if (!stagedChunks.ContainsKey(neededHash) && !HasChunk(neededHash))
            {
                var failed = new IffValidationReport(
                    bundle.Manifest.SourceName,
                    IsValid: false,
                    Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "BNDL", 0, $"Missing external chunk '{neededHash}' in local store.")],
                    ObjectGuids: [],
                    SemiGlobalName: null);
                return new IngestResult(false, failed, null, 0, 0);
            }
        }

        byte[] rebuilt;
        try
        {
            rebuilt = RebuildIffFromManifest(
                bundle.Manifest,
                hash => stagedChunks.TryGetValue(hash, out var bytes) ? bytes : LoadChunkVerified(hash));
        }
        catch (Exception ex) when (ex is InvalidDataException or ResourceFormatException or ArgumentException)
        {
            var failed = new IffValidationReport(
                bundle.Manifest.SourceName,
                IsValid: false,
                Issues: [new ChunkValidationIssue(ValidationSeverity.Error, "IFF ", 0, ex.Message)],
                ObjectGuids: [],
                SemiGlobalName: null);
            return new IngestResult(false, failed, null, 0, 0);
        }

        // Run the full ingestion + validation pipeline on the rebuilt IFF ("only valid game data in").
        return IngestIff(rebuilt, bundle.Manifest.SourceName, mode);
    }

    public ManifestDiff DiffManifests(string baseManifestHash, string variantManifestHash)
    {
        if (!_manifests.TryGetValue(baseManifestHash, out var baseManifest))
            throw new KeyNotFoundException($"Base manifest '{baseManifestHash}' not found.");
        if (!_manifests.TryGetValue(variantManifestHash, out var variantManifest))
            throw new KeyNotFoundException($"Variant manifest '{variantManifestHash}' not found.");

        var baseByKey = new Dictionary<(string Type, ushort Id), ManifestChunkEntry>();
        foreach (var entry in baseManifest.Chunks)
            baseByKey.TryAdd((entry.Type, entry.Id), entry);

        var variantKeys = new HashSet<(string Type, ushort Id)>();
        var sharedCount = 0;
        var modifiedCount = 0;
        var addedCount = 0;
        long sharedBytes = 0;
        long deltaBytes = 0;

        foreach (var entry in variantManifest.Chunks)
        {
            var key = (entry.Type, entry.Id);
            variantKeys.Add(key);
            if (baseByKey.TryGetValue(key, out var existing))
            {
                if (string.Equals(existing.ContentHash, entry.ContentHash, StringComparison.Ordinal))
                {
                    sharedCount++;
                    sharedBytes += entry.Size;
                }
                else
                {
                    modifiedCount++;
                    deltaBytes += entry.Size;
                }
            }
            else
            {
                addedCount++;
                deltaBytes += entry.Size;
            }
        }

        var removedCount = baseByKey.Keys.Count(key => !variantKeys.Contains(key));
        return new ManifestDiff(
            baseManifestHash,
            variantManifestHash,
            sharedCount,
            modifiedCount,
            addedCount,
            removedCount,
            sharedBytes,
            deltaBytes);
    }

    public IReadOnlyDictionary<uint, IReadOnlyList<IffManifest>> FindGuidCollisions()
    {
        var byGuid = new Dictionary<uint, List<IffManifest>>();
        foreach (var manifest in _manifests.Values)
        {
            foreach (var guid in manifest.ObjectGuids.Distinct())
            {
                if (!byGuid.TryGetValue(guid, out var list))
                {
                    list = [];
                    byGuid[guid] = list;
                }
                list.Add(manifest);
            }
        }

        var collisions = new Dictionary<uint, IReadOnlyList<IffManifest>>();
        foreach (var (guid, list) in byGuid)
        {
            if (list.Count > 1)
                collisions[guid] = list;
        }
        return collisions;
    }

    public ChunkStoreStats GetStats()
    {
        long totalOriginalBytes = 0;
        var totalRefs = 0;
        long totalPayloadBytes = 0;
        var globalUnique = new Dictionary<string, int>(StringComparer.Ordinal);
        var byType = new Dictionary<string, (int Refs, long TotalBytes, Dictionary<string, int> Unique)>(StringComparer.Ordinal);

        foreach (var manifest in _manifests.Values)
        {
            totalOriginalBytes += manifest.OriginalSizeBytes;
            foreach (var chunk in manifest.Chunks)
            {
                totalRefs++;
                totalPayloadBytes += chunk.Size;
                globalUnique.TryAdd(chunk.ContentHash, chunk.Size);

                if (!byType.TryGetValue(chunk.Type, out var acc))
                {
                    acc = (0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
                }
                acc.Refs++;
                acc.TotalBytes += chunk.Size;
                acc.Unique.TryAdd(chunk.ContentHash, chunk.Size);
                byType[chunk.Type] = acc;
            }
        }

        var uniquePayloadBytes = globalUnique.Values.Sum(static size => (long)size);
        var storedChunkBytes = globalUnique.Keys.Sum(hash => (long)_storedChunkSizes.GetValueOrDefault(hash, globalUnique[hash]));
        var totalManifestBytes = _manifestSerializedSizes.Values.Sum(static size => (long)size);

        var typeStats = byType
            .Select(kvp => new ChunkTypeStats(
                kvp.Key,
                kvp.Value.Refs,
                kvp.Value.Unique.Count,
                kvp.Value.TotalBytes,
                kvp.Value.Unique.Values.Sum(static size => (long)size)))
            .OrderByDescending(stat => stat.SavedBytes)
            .ThenByDescending(stat => stat.TotalBytes)
            .ToList();

        return new ChunkStoreStats(
            _manifests.Count,
            totalOriginalBytes,
            totalRefs,
            globalUnique.Count,
            totalPayloadBytes,
            uniquePayloadBytes,
            storedChunkBytes,
            totalManifestBytes,
            typeStats);
    }

    private static byte[] RebuildIffFromManifest(IffManifest manifest, Func<string, byte[]> loadChunk)
    {
        var header = Convert.FromBase64String(manifest.HeaderBase64);
        if (header.Length != IffFile.HeaderSize)
            throw new InvalidDataException($"Manifest '{manifest.ManifestHash}' has invalid IFF header length {header.Length}.");

        var chunkCount = manifest.Chunks.Count;
        var payloads = new byte[chunkCount][];
        var offsets = new int[chunkCount];
        var total = IffFile.HeaderSize;

        for (var i = 0; i < chunkCount; i++)
        {
            var entry = manifest.Chunks[i];
            var payload = loadChunk(entry.ContentHash);
            if (payload.Length != entry.Size)
                throw new InvalidDataException($"Chunk '{entry.ContentHash}' size mismatch: expected {entry.Size}, got {payload.Length}.");
            payloads[i] = payload;
            offsets[i] = total;
            total += IffFile.ChunkHeaderSize + payload.Length;
        }

        byte[]? rsmpPayload = null;
        var rsmpOffset = 0;
        if (manifest.HasRsmp)
        {
            var rsmpEntries = new List<(ResourceType Type, ushort Id, ushort Flags, string Label, int Offset)>(chunkCount);
            for (var i = 0; i < chunkCount; i++)
            {
                var entry = manifest.Chunks[i];
                rsmpEntries.Add((ResourceType.Parse(entry.Type), entry.Id, entry.Flags, entry.Label, offsets[i]));
            }
            rsmpPayload = ResourceMap.Write(rsmpEntries);
            rsmpOffset = total;
            total += IffFile.ChunkHeaderSize + rsmpPayload.Length;
        }

        var output = new byte[total];
        header.CopyTo(output, 0);
        if (manifest.HasRsmp)
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(60, 4), (uint)rsmpOffset);

        var cursor = IffFile.HeaderSize;
        for (var i = 0; i < chunkCount; i++)
        {
            var entry = manifest.Chunks[i];
            WriteChunk(output.AsSpan(cursor), ResourceType.Parse(entry.Type), entry.Id, entry.Flags, entry.Label, payloads[i]);
            cursor += IffFile.ChunkHeaderSize + payloads[i].Length;
        }

        if (rsmpPayload is not null)
        {
            WriteChunk(output.AsSpan(cursor), ResourceType.Rsmp, 0, 0, "", rsmpPayload);
        }

        // Verify the rebuilt bytes parse cleanly before returning ("only valid game data out").
        _ = new IffFile(output, manifest.SourceName);
        return output;
    }

    private bool StoreChunkIfMissing(string hash, ReadOnlySpan<byte> payload)
    {
        if (_rootDirectory is not null)
        {
            var path = ChunkPath(hash);
            if (File.Exists(path))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var toWrite = _compressChunks ? CompressBrotli(payload) : payload.ToArray();
            File.WriteAllBytes(path, toWrite);
            _storedChunkSizes[hash] = toWrite.Length;
            return true;
        }

        if (_chunks.ContainsKey(hash))
            return false;

        var stored = _compressChunks ? CompressBrotli(payload) : payload.ToArray();
        _chunks[hash] = stored;
        _storedChunkSizes[hash] = stored.Length;
        return true;
    }

    private byte[] LoadChunkVerified(string hash)
    {
        byte[] storedBytes;
        if (_rootDirectory is not null)
        {
            var path = ChunkPath(hash);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Chunk '{hash}' is missing from store.", path);
            storedBytes = File.ReadAllBytes(path);
        }
        else if (!_chunks.TryGetValue(hash, out var stored))
        {
            throw new KeyNotFoundException($"Chunk '{hash}' is missing from in-memory store.");
        }
        else
        {
            storedBytes = stored;
        }

        var rawBytes = _compressChunks ? DecompressBrotli(storedBytes) : storedBytes;
        var actualHash = ComputeSha256Hex(rawBytes);
        if (!string.Equals(actualHash, hash, StringComparison.Ordinal))
            throw new InvalidDataException($"Chunk integrity failure for '{hash}': actual hash was '{actualHash}'.");

        return rawBytes;
    }

    private string ChunkPath(string hash) =>
        Path.Combine(_rootDirectory!, "chunks", hash[..2], _compressChunks ? $"{hash}.br" : $"{hash}.bin");

    private static byte[] CompressBrotli(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(data);
        return output.ToArray();
    }

    private static byte[] DecompressBrotli(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteChunk(Span<byte> destination, ResourceType type, ushort id, ushort flags, string label, ReadOnlySpan<byte> payload)
    {
        var header = destination[..IffFile.ChunkHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, type.Code);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(IffFile.ChunkHeaderSize + payload.Length));
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], id);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], flags);
        var labelBytes = Encoding.Latin1.GetBytes(label);
        labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 64)).CopyTo(header[12..]);
        payload.CopyTo(destination[IffFile.ChunkHeaderSize..]);
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));
}
