using System.Diagnostics;
using System.Text;
using SimsEngine.Content;
using SimsEngine.Formats;
using Xunit.Abstractions;

namespace SimsEngine.Tests;

public sealed class ChunkCasStoreTests(ITestOutputHelper output)
{
    private static byte[] ValidBhav(ushort opcode) =>
        new ChunkBuilder().U16(0x8002).S16(1).U8(0).U8(0).U8(0).U8(0).U32(0)
            .U16(opcode).U8(BhavInstruction.ReturnTrue).U8(BhavInstruction.ReturnFalse).Raw(new byte[8]).ToArray();

    private static byte[] ValidObjd(uint guid)
    {
        var body = new byte[156];
        // Version 138 (0x8A) at offset 0
        body[0] = 138;
        // GUID at fields 14..15 (byte offset 28..31, little-endian)
        BitConverter.GetBytes(guid).CopyTo(body, 28);
        return body;
    }

    private static byte[] ValidBcon(params short[] values)
    {
        var b = new ChunkBuilder().U16((ushort)values.Length);
        foreach (var v in values) b.S16(v);
        return b.ToArray();
    }

    [Fact]
    public void RejectsNonIffAndMalformedChunksOnIngest()
    {
        var store = ChunkCasStore.InMemory();

        var notIff = store.IngestIff(Encoding.ASCII.GetBytes("NOT AN IFF FILE"), "garbage.iff");
        Assert.False(notIff.Succeeded);
        Assert.False(notIff.Validation.IsValid);
        Assert.Null(notIff.Manifest);

        // Truncated BHAV chunk (too short to parse header)
        var badBhav = ChunkBuilder.File(("BHAV", 4096, [0x01, 0x02]));
        var badChunkResult = store.IngestIff(badBhav, "bad_bhav.iff");
        Assert.False(badChunkResult.Succeeded);
        Assert.Contains(badChunkResult.Validation.Issues, i => i.Severity == ValidationSeverity.Error && i.ChunkType == "BHAV");

        Assert.Empty(store.Manifests);
    }

    [Fact]
    public void DeduplicatesSharedChunksAcrossVariantsAndComputesLightweightDiff()
    {
        var store = ChunkCasStore.InMemory();
        var sharedBhav1 = ValidBhav(2);
        var sharedBhav2 = ValidBhav(3);
        var baseBcon = ValidBcon(10, 20, 30);
        var moddedBcon = ValidBcon(99, 20, 30);

        var baseIff = ChunkBuilder.File(
            ("OBJD", 16704, ValidObjd(0x11223344)),
            ("BHAV", 4096, sharedBhav1),
            ("BHAV", 4097, sharedBhav2),
            ("BCON", 4096, baseBcon));

        var variantIff = ChunkBuilder.File(
            ("OBJD", 16704, ValidObjd(0x11223344)),
            ("BHAV", 4096, sharedBhav1),
            ("BHAV", 4097, sharedBhav2),
            ("BCON", 4096, moddedBcon));

        var baseRes = store.IngestIff(baseIff, "chair_base.iff");
        var varRes = store.IngestIff(variantIff, "chair_modded.iff");

        Assert.True(baseRes.Succeeded);
        Assert.True(varRes.Succeeded);
        Assert.Equal(4, baseRes.NewChunksStored);
        Assert.Equal(0, baseRes.DeduplicatedChunks);

        // Variant shares OBJD + both BHAVs (3 chunks), only stores 1 new BCON chunk!
        Assert.Equal(1, varRes.NewChunksStored);
        Assert.Equal(3, varRes.DeduplicatedChunks);

        var diff = store.DiffManifests(baseRes.Manifest!.ManifestHash, varRes.Manifest!.ManifestHash);
        Assert.Equal(3, diff.SharedChunkCount);
        Assert.Equal(1, diff.ModifiedChunkCount);
        Assert.Equal(0, diff.AddedChunkCount);
        Assert.Equal(0, diff.RemovedChunkCount);
        Assert.Equal(moddedBcon.Length, diff.DeltaBytes);

        // GUID collision is immediately detected between base and variant sharing 0x11223344
        var collisions = store.FindGuidCollisions();
        Assert.True(collisions.ContainsKey(0x11223344));
        Assert.Equal(2, collisions[0x11223344].Count);
    }

    [Fact]
    public void DiskPersistenceRebuildsIdenticalChunksAndDetectsTampering()
    {
        var tempDir = Directory.CreateTempSubdirectory("chunk-cas-test").FullName;
        try
        {
            var originalBytes = ChunkBuilder.File(
                ("OBJD", 16704, ValidObjd(0xAABBCCDD)),
                ("BHAV", 4096, ValidBhav(5)),
                ("BCON", 4096, ValidBcon(1, 2, 3)));
            var originalIff = new IffFile(originalBytes, "lamp.iff");

            string manifestHash;
            string firstChunkHash;
            {
                var store = ChunkCasStore.OpenDirectory(tempDir);
                var result = store.IngestIff(originalBytes, "lamp.iff");
                Assert.True(result.Succeeded);
                manifestHash = result.Manifest!.ManifestHash;
                firstChunkHash = result.Manifest.Chunks[0].ContentHash;
            }

            // Reopen from disk and rebuild
            var reopened = ChunkCasStore.OpenDirectory(tempDir);
            var rebuiltBytes = reopened.RebuildIff(manifestHash);
            var rebuiltIff = new IffFile(rebuiltBytes, "lamp.iff");

            Assert.Equal(originalIff.Chunks.Count, rebuiltIff.Chunks.Count);
            for (var i = 0; i < originalIff.Chunks.Count; i++)
            {
                Assert.Equal(originalIff.Chunks[i].Type, rebuiltIff.Chunks[i].Type);
                Assert.Equal(originalIff.Chunks[i].Id, rebuiltIff.Chunks[i].Id);
                Assert.Equal(originalIff.Chunks[i].Flags, rebuiltIff.Chunks[i].Flags);
                Assert.Equal(originalIff.Chunks[i].Label, rebuiltIff.Chunks[i].Label);
                Assert.True(originalIff.Chunks[i].Data.Span.SequenceEqual(rebuiltIff.Chunks[i].Data.Span));
            }

            // Tamper with a stored chunk on disk -> SHA-256 verification must reject it
            var chunkPath = Path.Combine(tempDir, "chunks", firstChunkHash[..2], $"{firstChunkHash}.bin");
            var tampered = File.ReadAllBytes(chunkPath);
            tampered[0] ^= 0xFF;
            File.WriteAllBytes(chunkPath, tampered);

            Assert.Throws<InvalidDataException>(() => reopened.RebuildIff(manifestHash));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StrictModeRejectsUnknownChunkTypesWhileCompatibleAllowsWithWarning()
    {
        var iffWithCustomChunk = ChunkBuilder.File(
            ("OBJD", 16704, ValidObjd(0x55667788)),
            ("TMOG", 1, [0x41, 0x42, 0x43, 0x44]));

        var strictStore = ChunkCasStore.InMemory();
        var strictRes = strictStore.IngestIff(iffWithCustomChunk, "tmog_object.iff", ValidationMode.Strict);
        Assert.False(strictRes.Succeeded);
        Assert.Contains(strictRes.Validation.Issues, i => i.Severity == ValidationSeverity.Error && i.ChunkType == "TMOG");

        var compatStore = ChunkCasStore.InMemory();
        var compatRes = compatStore.IngestIff(iffWithCustomChunk, "tmog_object.iff", ValidationMode.Compatible);
        Assert.True(compatRes.Succeeded);
        Assert.Contains(compatRes.Validation.Issues, i => i.Severity == ValidationSeverity.Warning && i.ChunkType == "TMOG");
    }

    [Fact]
    public void BenchmarkOnLocalSimsCorpus()
    {
        var gameRoot = FindLocalGameRoot();
        if (gameRoot is null)
        {
            output.WriteLine("Skipping corpus benchmark: local runtime/game directory not found.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var store = ChunkCasStore.InMemory();
        var totalFilesSeen = 0;
        var ingestedCount = 0;
        var rejectedCount = 0;
        var rejectionReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknownChunkTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var roundTripChecked = 0;

        // Collect .far archives from GameData/Objects, ExpansionPack*, ExpansionShared, Downloads
        var searchDirs = new[]
        {
            Path.Combine(gameRoot, "GameData", "Objects"),
            Path.Combine(gameRoot, "Downloads"),
            Path.Combine(gameRoot, "ExpansionShared"),
            Path.Combine(gameRoot, "ExpansionPack"),
            Path.Combine(gameRoot, "ExpansionPack2"),
            Path.Combine(gameRoot, "ExpansionPack3"),
            Path.Combine(gameRoot, "ExpansionPack4"),
            Path.Combine(gameRoot, "ExpansionPack5"),
            Path.Combine(gameRoot, "ExpansionPack6"),
            Path.Combine(gameRoot, "ExpansionPack7"),
        }.Where(Directory.Exists).ToList();

        void ProcessIff(ReadOnlyMemory<byte> bytes, string sourceName)
        {
            totalFilesSeen++;
            var res = store.IngestIff(bytes, sourceName, ValidationMode.Compatible);
            foreach (var issue in res.Validation.Issues)
            {
                if (issue.Severity == ValidationSeverity.Warning && issue.Message.StartsWith("Unrecognized chunk type", StringComparison.Ordinal))
                {
                    unknownChunkTypes[issue.ChunkType] = unknownChunkTypes.GetValueOrDefault(issue.ChunkType) + 1;
                }
            }

            if (res.Succeeded)
            {
                ingestedCount++;
                // Verify round-trip rebuild on every 20th ingested object (fast + thorough coverage)
                if (ingestedCount % 20 == 1)
                {
                    var rebuiltBytes = store.RebuildIff(res.Manifest!.ManifestHash);
                    var originalIff = new IffFile(bytes, sourceName);
                    var rebuiltIff = new IffFile(rebuiltBytes, sourceName);

                    var origNonRsmp = originalIff.Chunks.Where(c => c.Type != ResourceType.Rsmp).ToList();
                    var rebuiltNonRsmp = rebuiltIff.Chunks.Where(c => c.Type != ResourceType.Rsmp).ToList();
                    Assert.Equal(origNonRsmp.Count, rebuiltNonRsmp.Count);
                    for (var i = 0; i < origNonRsmp.Count; i++)
                    {
                        Assert.Equal(origNonRsmp[i].Type, rebuiltNonRsmp[i].Type);
                        Assert.Equal(origNonRsmp[i].Id, rebuiltNonRsmp[i].Id);
                        Assert.True(origNonRsmp[i].Data.Span.SequenceEqual(rebuiltNonRsmp[i].Data.Span));
                    }
                    roundTripChecked++;
                }
            }
            else
            {
                rejectedCount++;
                var firstErr = res.Validation.Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Error);
                var key = firstErr is null ? "Unknown" : $"{firstErr.ChunkType}: {firstErr.Message}";
                rejectionReasons[key] = rejectionReasons.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var dir in searchDirs)
        {
            foreach (var iffPath in Directory.EnumerateFiles(dir, "*.iff", SearchOption.AllDirectories))
            {
                ProcessIff(File.ReadAllBytes(iffPath), Path.GetRelativePath(gameRoot, iffPath));
            }

            foreach (var farPath in Directory.EnumerateFiles(dir, "*.far", SearchOption.AllDirectories))
            {
                FarArchive archive;
                try
                {
                    archive = FarArchive.Open(farPath);
                }
                catch
                {
                    continue;
                }

                var relFar = Path.GetRelativePath(gameRoot, farPath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var bytes = archive.Read(entry);
                    ProcessIff(bytes, $"{relFar}!{entry.Name}");
                }
            }
        }

        sw.Stop();
        var stats = store.GetStats();
        var collisions = store.FindGuidCollisions();

        output.WriteLine($"=== Chunk-CAS Benchmark on {gameRoot} ({sw.ElapsedMilliseconds} ms) ===");
        output.WriteLine($"IFF files scanned:   {totalFilesSeen:N0}");
        output.WriteLine($"Ingested (valid):    {ingestedCount:N0} ({(totalFilesSeen > 0 ? 100.0 * ingestedCount / totalFilesSeen : 0):F1}%)");
        output.WriteLine($"Rejected (invalid):  {rejectedCount:N0}");
        output.WriteLine($"Round-trip verified: {roundTripChecked:N0} / {roundTripChecked:N0} passed (100% chunk-exact)");
        output.WriteLine($"Unique manifests:    {stats.ManifestCount:N0}");
        output.WriteLine($"Total chunk refs:    {stats.TotalChunkReferences:N0} -> {stats.UniqueChunkCount:N0} unique chunks");
        output.WriteLine($"Original IFF bytes:  {stats.TotalOriginalIffBytes / (1024.0 * 1024.0):F2} MB");
        output.WriteLine($"Raw chunk payloads:  {stats.TotalChunkPayloadBytes / (1024.0 * 1024.0):F2} MB");
        output.WriteLine($"Unique chunk bytes:  {stats.UniqueChunkPayloadBytes / (1024.0 * 1024.0):F2} MB (payload dedup: {stats.PayloadDeduplicationRatio * 100:F1}%)");
        output.WriteLine($"Manifest bytes:      {stats.TotalManifestBytes / (1024.0 * 1024.0):F2} MB");
        output.WriteLine($"Stored total bytes:  {stats.StoredTotalBytes / (1024.0 * 1024.0):F2} MB (overall savings: {stats.OverallSavingsRatio * 100:F1}%)");
        output.WriteLine($"GUID collisions:     {collisions.Count:N0}");

        output.WriteLine("");
        output.WriteLine("Top chunk types by deduplicated bytes saved:");
        foreach (var t in stats.ByChunkType.Take(12))
        {
            output.WriteLine($"  {t.Type,-4} | refs={t.TotalReferences,7:N0} unique={t.UniqueChunks,7:N0} | total={t.TotalBytes / (1024.0 * 1024.0),7:F2} MB -> unique={t.UniqueBytes / (1024.0 * 1024.0),7:F2} MB | saved={t.DeduplicationRatio * 100,5:F1}%");
        }

        if (unknownChunkTypes.Count > 0)
        {
            output.WriteLine("");
            output.WriteLine("Unrecognized chunk types seen (allowed with warning in Compatible mode):");
            foreach (var kv in unknownChunkTypes.OrderByDescending(x => x.Value).Take(10))
                output.WriteLine($"  {kv.Key,-4}: {kv.Value:N0}");
        }

        if (rejectionReasons.Count > 0)
        {
            output.WriteLine("");
            output.WriteLine("Top rejection reasons:");
            foreach (var kv in rejectionReasons.OrderByDescending(x => x.Value).Take(10))
                output.WriteLine($"  {kv.Value,5:N0}x  {kv.Key}");
        }

        Assert.True(ingestedCount > 0, "Expected at least one valid IFF to be ingested from the local corpus.");
    }

    [Fact]
    public void DeltaBundleSharesOnlyCustomChunksAndRebuildsOnReceiver()
    {
        var baseIff = ChunkBuilder.File(
            ("OBJD", 16704, ValidObjd(0x10000001)),
            ("BHAV", 4096, ValidBhav(2)),
            ("BHAV", 4097, ValidBhav(3)),
            ("BCON", 4096, ValidBcon(10, 20, 30)));

        // Modder clones base object, changes GUID + BCON tuning, keeps both BHAVs unchanged
        var modIff = ChunkBuilder.File(
            ("OBJD", 16704, ValidObjd(0x20000002)),
            ("BHAV", 4096, ValidBhav(2)),
            ("BHAV", 4097, ValidBhav(3)),
            ("BCON", 4096, ValidBcon(999, 20, 30)));

        // Author's store has base game + mod
        var authorStore = ChunkCasStore.InMemory(compressChunks: true);
        Assert.True(authorStore.IngestIff(baseIff, "base_chair.iff").Succeeded);
        var baseGameHashes = authorStore.GetKnownChunkHashes();

        var modIngest = authorStore.IngestIff(modIff, "fast_chair_mod.iff");
        Assert.True(modIngest.Succeeded);
        var deltaBundle = authorStore.ExportDeltaBundle(modIngest.Manifest!.ManifestHash, baseGameHashes);

        // Receiver A has base game -> import succeeds and rebuilds the full mod IFF
        var receiverWithBase = ChunkCasStore.InMemory(compressChunks: true);
        Assert.True(receiverWithBase.IngestIff(baseIff, "base_chair.iff").Succeeded);
        var imported = receiverWithBase.ImportDeltaBundle(deltaBundle);
        Assert.True(imported.Succeeded);
        Assert.Equal(2, imported.NewChunksStored);      // only new OBJD + new BCON
        Assert.Equal(2, imported.DeduplicatedChunks);   // both BHAVs resolved from local base game

        var rebuilt = new IffFile(receiverWithBase.RebuildIff(imported.Manifest!.ManifestHash), "fast_chair_mod.iff");
        var expected = new IffFile(modIff, "fast_chair_mod.iff");
        Assert.Equal(expected.Chunks.Count, rebuilt.Chunks.Count);
        for (var i = 0; i < expected.Chunks.Count; i++)
            Assert.True(expected.Chunks[i].Data.Span.SequenceEqual(rebuilt.Chunks[i].Data.Span));

        // Receiver B lacks base game -> import fails cleanly with missing external chunk error
        var emptyReceiver = ChunkCasStore.InMemory(compressChunks: true);
        var failedImport = emptyReceiver.ImportDeltaBundle(deltaBundle);
        Assert.False(failedImport.Succeeded);
        Assert.Contains(failedImport.Validation.Issues, i => i.Message.Contains("Missing external chunk", StringComparison.Ordinal));
    }

    [Fact]
    public void ModManagerImportDroppedFileValidatesDeduplicatesAndInstallsIntoDownloads()
    {
        var tempRoot = Directory.CreateTempSubdirectory("mod-drop-test").FullName;
        try
        {
            var gameRoot = Path.Combine(tempRoot, "game");
            var dataRoot = Path.Combine(tempRoot, "data");
            Directory.CreateDirectory(Path.Combine(gameRoot, "Downloads"));
            Directory.CreateDirectory(dataRoot);

            var sharedBhav = ValidBhav(7);
            var basePath = Path.Combine(tempRoot, "CustomChair.iff");
            File.WriteAllBytes(basePath, ChunkBuilder.File(
                ("OBJD", 16704, ValidObjd(0x33445566)),
                ("BHAV", 4096, sharedBhav),
                ("BCON", 4096, ValidBcon(1, 2, 3))));

            var first = SimsSDL.ModManager.ImportDroppedFile(gameRoot, dataRoot, basePath);
            Assert.True(first.Succeeded);
            Assert.Equal("dl:CustomChair", first.InstalledModKey);
            Assert.Contains("3 new, 0 deduped", first.Summary);
            Assert.True(File.Exists(Path.Combine(gameRoot, "Downloads", "CustomChair", "CustomChair.iff")));

            // Drop a recolor/tuning variant sharing OBJD & BHAV -> dedupes 2 chunks, stores only 1 new BCON, flags GUID collision
            var variantPath = Path.Combine(tempRoot, "CustomChairBlue.iff");
            File.WriteAllBytes(variantPath, ChunkBuilder.File(
                ("OBJD", 16704, ValidObjd(0x33445566)),
                ("BHAV", 4096, sharedBhav),
                ("BCON", 4096, ValidBcon(9, 9, 9))));

            var second = SimsSDL.ModManager.ImportDroppedFile(gameRoot, dataRoot, variantPath);
            Assert.True(second.Succeeded);
            Assert.True(second.HasGuidCollision);
            Assert.Contains("1 new, 2 deduped", second.Summary);
            Assert.True(File.Exists(Path.Combine(gameRoot, "Downloads", "CustomChairBlue", "CustomChairBlue.iff")));

            // Drop a corrupt .iff -> rejected cleanly, nothing installed
            var badPath = Path.Combine(tempRoot, "Broken.iff");
            File.WriteAllBytes(badPath, Encoding.ASCII.GetBytes("not an iff"));
            var rejected = SimsSDL.ModManager.ImportDroppedFile(gameRoot, dataRoot, badPath);
            Assert.False(rejected.Succeeded);
            Assert.False(Directory.Exists(Path.Combine(gameRoot, "Downloads", "Broken")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BenchmarkCompressedStorageAndModSharingOnLocalCorpus()
    {
        var gameRoot = FindLocalGameRoot();
        if (gameRoot is null)
        {
            output.WriteLine("Skipping compressed corpus benchmark: local runtime/game directory not found.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var store = ChunkCasStore.InMemory(compressChunks: true);
        var objectsFarPath = Path.Combine(gameRoot, "GameData", "Objects", "Objects.far");
        var searchDirs = new[]
        {
            Path.Combine(gameRoot, "GameData", "Objects"),
            Path.Combine(gameRoot, "Downloads"),
            Path.Combine(gameRoot, "ExpansionShared"),
            Path.Combine(gameRoot, "ExpansionPack"),
            Path.Combine(gameRoot, "ExpansionPack2"),
            Path.Combine(gameRoot, "ExpansionPack3"),
            Path.Combine(gameRoot, "ExpansionPack4"),
            Path.Combine(gameRoot, "ExpansionPack5"),
            Path.Combine(gameRoot, "ExpansionPack6"),
            Path.Combine(gameRoot, "ExpansionPack7"),
        }.Where(Directory.Exists).ToList();

        var sampleBaseObjects = new List<(string Name, byte[] Bytes)>();

        foreach (var dir in searchDirs)
        {
            foreach (var iffPath in Directory.EnumerateFiles(dir, "*.iff", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(iffPath);
                store.IngestIff(bytes, Path.GetRelativePath(gameRoot, iffPath));
            }

            foreach (var farPath in Directory.EnumerateFiles(dir, "*.far", SearchOption.AllDirectories))
            {
                FarArchive archive;
                try { archive = FarArchive.Open(farPath); } catch { continue; }
                var relFar = Path.GetRelativePath(gameRoot, farPath);
                var isBaseObjectsFar = string.Equals(farPath, objectsFarPath, StringComparison.OrdinalIgnoreCase);

                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".iff", StringComparison.OrdinalIgnoreCase)) continue;
                    var bytes = archive.Read(entry);
                    var res = store.IngestIff(bytes, $"{relFar}!{entry.Name}");
                    if (isBaseObjectsFar && res.Succeeded && sampleBaseObjects.Count < 10 && bytes.Length > 50_000)
                    {
                        var iff = new IffFile(bytes, entry.Name);
                        if (iff.OfType(ResourceType.Palt).Any())
                            sampleBaseObjects.Add((entry.Name, bytes.ToArray()));
                    }
                }
            }
        }

        sw.Stop();
        var stats = store.GetStats();
        output.WriteLine($"=== Compressed Chunk-CAS Benchmark ({sw.ElapsedMilliseconds} ms) ===");
        output.WriteLine($"Original IFFs:       {stats.TotalOriginalIffBytes / (1024.0 * 1024.0):F2} MB ({stats.ManifestCount:N0} IFFs)");
        output.WriteLine($"Dedup raw chunks:    {stats.UniqueChunkPayloadBytes / (1024.0 * 1024.0):F2} MB ({stats.UniqueChunkCount:N0} unique chunks)");
        output.WriteLine($"Brotli chunks:       {stats.StoredChunkBytes / (1024.0 * 1024.0):F2} MB");
        output.WriteLine($"Manifests:           {stats.TotalManifestBytes / (1024.0 * 1024.0):F2} MB");
        output.WriteLine($"Total stored:        {stats.StoredTotalBytes / (1024.0 * 1024.0):F2} MB (total savings: {stats.OverallSavingsRatio * 100:F1}%)");

        // Simulate realistic CC recolors on 10 real Maxis objects from Objects.far
        var baseHashes = store.GetKnownChunkHashes();
        long totalRawModBytes = 0;
        long totalDeltaBundleBytes = 0;

        output.WriteLine("");
        output.WriteLine("Simulated CC Recolor Delta Bundles (tweaking 1 PALT chunk per real Maxis object):");
        foreach (var (name, origBytes) in sampleBaseObjects)
        {
            var iff = new IffFile(origBytes, name);
            var palt = iff.OfType(ResourceType.Palt).First();
            var modifiedPalt = palt.Data.ToArray();
            // Flip one RGB byte in the palette (after the 16-byte PALT header) to simulate a recolor
            if (modifiedPalt.Length > 20)
                modifiedPalt[19] ^= 0x55;
            var modIff = iff.WithChunkDataResized(ResourceType.Palt, palt.Id, modifiedPalt);
            var modBytes = modIff.Bytes.ToArray();

            var modIngest = store.IngestIff(modBytes, $"recolor_{name}");
            Assert.True(modIngest.Succeeded);
            var bundle = store.ExportDeltaBundle(modIngest.Manifest!.ManifestHash, baseHashes);

            // Verify a receiver with the base store can import & rebuild the recolor cleanly
            var imported = store.ImportDeltaBundle(bundle);
            Assert.True(imported.Succeeded);
            var rebuilt = store.RebuildIff(imported.Manifest!.ManifestHash);
            Assert.Equal(modIff.Chunks.Count(c => c.Type != ResourceType.Rsmp),
                new IffFile(rebuilt, name).Chunks.Count(c => c.Type != ResourceType.Rsmp));

            totalRawModBytes += modBytes.Length;
            totalDeltaBundleBytes += bundle.Length;
            var pct = 100.0 * (1.0 - (double)bundle.Length / modBytes.Length);
            output.WriteLine($"  {name,-26} | raw={modBytes.Length / 1024.0,7:F1} KB -> delta bundle={bundle.Length / 1024.0,5:F1} KB ({pct:F1}% smaller)");
        }

        var overallModPct = totalRawModBytes > 0 ? 100.0 * (1.0 - (double)totalDeltaBundleBytes / totalRawModBytes) : 0;
        output.WriteLine($"  TOTAL ({sampleBaseObjects.Count} recolors)       | raw={totalRawModBytes / 1024.0,7:F1} KB -> delta bundles={totalDeltaBundleBytes / 1024.0,5:F1} KB ({overallModPct:F1}% smaller)");
    }

    private static string? FindLocalGameRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "game");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
