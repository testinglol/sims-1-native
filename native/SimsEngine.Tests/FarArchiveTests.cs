using System.Buffers.Binary;
using System.Text;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

/// <summary>The FAR archive format begins with a header containing a signature, version, and manifest
/// offset, followed by entry records storing compressed size, uncompressed size, file offset, filename
/// length, and filename.</summary>
public class FarArchiveTests
{
    private static byte[] Archive(params (string Name, byte[] Data)[] entries)
    {
        var names = entries.Select(entry => Encoding.ASCII.GetBytes(entry.Name)).ToArray();
        var header = 16;
        var manifest = header + entries.Sum(entry => entry.Data.Length);
        var table = 4 + entries.Select((entry, index) => 16 + names[index].Length).Sum();
        var bytes = new byte[manifest + table];
        "FAR!byAZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)manifest);
        var dataOffset = header;
        for (var index = 0; index < entries.Length; index++)
        {
            entries[index].Data.CopyTo(bytes, dataOffset);
            dataOffset += entries[index].Data.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifest), (uint)entries.Length);
        var entryOffset = manifest + 4;
        dataOffset = header;
        for (var index = 0; index < entries.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset), (uint)entries[index].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 4), (uint)entries[index].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 8), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 12), (uint)names[index].Length);
            names[index].CopyTo(bytes, entryOffset + 16);
            entryOffset += 16 + names[index].Length;
            dataOffset += entries[index].Data.Length;
        }
        return bytes;
    }

    [Fact]
    public void EntriesReadBackTheirBytes()
    {
        var archive = new FarArchive(Archive(("Outdoors\\sim_amb.fsc", [1, 2, 3]), ("Park\\hotdatepark_amb.fsc", [4, 5])), "test.far");
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal([1, 2, 3], archive.Read(archive.Entries[0]).ToArray());
        Assert.Equal([4, 5], archive.Read(archive.Entries[1]).ToArray());
    }

    [Fact]
    public void FindIgnoresCaseAndSeparators()
    {
        var archive = new FarArchive(Archive(("Outdoors\\sim_amb.fsc", [9])), "test.far");
        Assert.Equal("Outdoors\\sim_amb.fsc", archive.Find("outdoors/sim_amb.FSC")?.Name);
        Assert.Null(archive.Find("missing.fsc"));
    }

    [Fact]
    public void BadSignatureThrows()
    {
        var bytes = Archive(("a", [1]));
        "XXXXbyAZ"u8.CopyTo(bytes);
        Assert.Throws<ResourceFormatException>(() => new FarArchive(bytes, "test.far"));
    }

    [Fact]
    public void BadVersionThrows()
    {
        var bytes = Archive(("a", [1]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        Assert.Throws<ResourceFormatException>(() => new FarArchive(bytes, "test.far"));
    }

    [Fact]
    public void ManifestOutsideTheFileThrows()
    {
        var bytes = Archive(("a", [1]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)bytes.Length);
        Assert.Throws<ResourceFormatException>(() => new FarArchive(bytes, "test.far"));
    }

    [Fact]
    public void EntryOutsideTheFileThrows()
    {
        var bytes = Archive(("a", [1]));
        var manifest = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifest + 4 + 8), (uint)(bytes.Length + 1));
        Assert.Throws<ResourceFormatException>(() => new FarArchive(bytes, "test.far"));
    }

    [Fact]
    public void OpenReadsEntriesLazilyFromDiskAndAllowsDelete()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sims-far-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "lazy.far");
        try
        {
            byte[] payloadA = [10, 20, 30, 40];
            byte[] payloadB = [1, 2, 3];
            File.WriteAllBytes(path, Archive(("Alpha\\one.iff", payloadA), ("Beta\\two.iff", payloadB), ("Empty\\zero.iff", [])));

            var archive = FarArchive.Open(path);
            var entryA = archive.Find("alpha/one.iff");
            var entryB = archive.Find("Beta\\two.iff");
            var entryZero = archive.Find("empty/zero.iff");

            Assert.NotNull(entryA);
            Assert.NotNull(entryB);
            Assert.NotNull(entryZero);
            Assert.Null(archive.Find("missing.iff"));
            Assert.Equal(payloadA, archive.Read(entryA!).ToArray());
            Assert.Equal(payloadB, archive.Read(entryB!).ToArray());
            Assert.Empty(archive.Read(entryZero!).ToArray());

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ReadIff_KeepsMetadataAndLazilyReadsLargeAndArtChunksEquivalently()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "sims-far-iff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "objects.far");
        try
        {
            var objdBody = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var spr2Body = new byte[8192];
            for (var i = 0; i < spr2Body.Length; i++)
                spr2Body[i] = unchecked((byte)(i * 31 + 7));
            var iffBytes = ChunkBuilder.File(("OBJD", 16384, objdBody), ("SPR2", 100, spr2Body));
            File.WriteAllBytes(path, Archive(("sofa.iff", iffBytes)));

            var archive = FarArchive.Open(path);
            var entry = archive.Find("sofa.iff")!;
            var eager = new IffFile(archive.Read(entry), "eager");
            var lazy = archive.ReadIff(entry, "lazy");

            Assert.Equal(eager.Chunks.Count, lazy.Chunks.Count);
            Assert.Equal(objdBody, lazy.Find(ResourceType.Objd, 16384)!.Data.ToArray());
            Assert.Equal(spr2Body, lazy.Find(ResourceType.Spr2, 100)!.Data.ToArray());
            Assert.Equal(eager.Bytes.ToArray(), lazy.Bytes.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
