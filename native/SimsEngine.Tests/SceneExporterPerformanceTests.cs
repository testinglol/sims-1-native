using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class SceneExporterPerformanceTests
{
    [Fact]
    public void BindPose_And_BoneIndices_ReuseCachedSkeletonLayout()
    {
        var skeleton = new Skeleton("adult",
        [
            new Bone("ROOT", "", [], new Vector3(1f, 2f, 3f), Quaternion.Identity, [0, 0, 0], [0f, 0f]),
            new Bone("PELVIS", "ROOT", [], new Vector3(0f, 1f, 0f), Quaternion.Identity, [0, 0, 1], [0f, 0f]),
            new Bone("SPINE", "PELVIS", [], new Vector3(0f, 2f, 0f), Quaternion.Identity, [0, 0, 1], [0f, 0f]),
            new Bone("HEAD", "SPINE", [], new Vector3(0f, 1.5f, 0f), Quaternion.Identity, [0, 0, 1], [0f, 0f]),
        ]);

        var firstBind = Pose.BindPose(skeleton);
        var secondBind = Pose.BindPose(skeleton);
        Assert.Same(firstBind, secondBind);

        var firstIndices = Pose.BoneIndices(skeleton);
        var secondIndices = Pose.BoneIndices(skeleton);
        Assert.Same(firstIndices, secondIndices);
        Assert.Equal(0, firstIndices["ROOT"]);
        Assert.Equal(3, firstIndices["HEAD"]);

        var emptyAnimation = new Animation("idle", "idle", [], 0, 0, 0, []);
        var emptyCurves = new AnimationCurves([], []);
        var idleSample = Pose.Sample(skeleton, emptyAnimation, emptyCurves, 0.0);
        Assert.Same(firstBind, idleSample);

        var activeMotion = new Motion("SPINE", 2, 30f, [0, 0, 0, -1], [], []);
        var activeAnimation = new Animation("nod", "nod", [], 0, 2, 0, [activeMotion]);
        var activeCurves = new AnimationCurves([0f, 2f, 0f, 0f, 3f, 0f], []);
        var activeSample = Pose.Sample(skeleton, activeAnimation, activeCurves, 0.5);
        Assert.True(activeSample[2].Animated);
        Assert.Equal(new Vector3(1f, 5.5f, 3f), activeSample[2].WorldTranslation);
        Assert.Equal(new Vector3(1f, 7f, 3f), activeSample[3].WorldTranslation);
    }

    [Fact]
    public void Sample_OnCyclicSkeleton_ThrowsInvalidDataException()
    {
        var cyclic = new Skeleton("cyclic",
        [
            new Bone("A", "B", [], Vector3.Zero, Quaternion.Identity, [0, 0, 1], [0f, 0f]),
            new Bone("B", "A", [], Vector3.Zero, Quaternion.Identity, [0, 0, 1], [0f, 0f]),
        ]);
        var emptyAnimation = new Animation("idle", "idle", [], 0, 0, 0, []);
        var emptyCurves = new AnimationCurves([], []);

        Assert.Throws<InvalidDataException>(() => Pose.BindPose(cyclic));
        Assert.Throws<InvalidDataException>(() => Pose.Sample(cyclic, emptyAnimation, emptyCurves, 0.0));
    }

    [Fact]
    public void ExportAndAnimation_SkipStaticRebuild_And_MatchCutawayMask()
    {
        var lot = BuildSyntheticLotWithEnclosedRoom();
        var world = new SimulationWorld();
        var roomMap = RoomMap.Build(lot, 1, 64, 64);
        world.LotRoomMaps[1] = roomMap;
        foreach (var roomId in roomMap.RoomIds)
            world.PersonRoomMotiveEntries[roomId] = new PersonRoomMotiveEntry(100f, IsOutside: roomMap.IsOutside(roomId));
        var registry = new ObjectRegistry();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sims-perf-test-{Guid.NewGuid():N}");
        var gameRoot = Path.Combine(tempRoot, "game");
        var sceneDir = Path.Combine(tempRoot, "scene");
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(sceneDir);

        try
        {
            var animState = new SceneExporter.AnimationState();
            const long revision = 7;
            var exportedPath = SceneExporter.Export(
                lot, world, registry, gameRoot, sceneDir,
                house: 1, revision: revision, viewLevel: 1, wallMode: "cutaway",
                cameraOrientation: 0, cutawayTile: (11, 11), roofsVisible: false,
                state: animState, cutawayRooms: null, cutawaySim: (11, 11), cutawayZoom: 2);

            Assert.True(File.Exists(exportedPath));

            // Moving within the same enclosed indoor room keeps the cutaway mask identical (no full Export needed).
            Assert.True(SceneExporter.CutawayMaskMatches(
                world, animState, revision, viewLevel: 1, wallMode: "cutaway",
                cameraOrientation: 0, cutawayTile: (12, 12), cutawayRooms: null,
                cutawaySim: (12, 12), cutawayZoom: 2));

            // Moving out of the enclosed room to outdoors changes the cutaway mask (full Export required).
            Assert.False(SceneExporter.CutawayMaskMatches(
                world, animState, revision, viewLevel: 1, wallMode: "cutaway",
                cameraOrientation: 0, cutawayTile: (2, 2), cutawayRooms: null,
                cutawaySim: (2, 2), cutawayZoom: 2));

            // Stale revision never matches or produces an animation frame.
            Assert.False(SceneExporter.CutawayMaskMatches(
                world, animState, revision + 1, viewLevel: 1, wallMode: "cutaway",
                cameraOrientation: 0, cutawayTile: (11, 11), cutawayRooms: null,
                cutawaySim: (11, 11), cutawayZoom: 2));
            Assert.Null(SceneExporter.Animation(
                lot, world, registry, gameRoot, sceneDir,
                house: 1, revision: revision + 1, state: animState, sequence: 1, updates: 1,
                viewLevel: 1, wallMode: "cutaway", cameraOrientation: 0,
                cutawayTile: (12, 12), roofsVisible: false, cutawayRooms: null,
                cutawaySim: (12, 12), cutawayZoom: 2));

            // Matching revision produces a valid 30 FPS SceneAnimation delta without touching static architecture.
            var frame = SceneExporter.Animation(
                lot, world, registry, gameRoot, sceneDir,
                house: 1, revision: revision, state: animState, sequence: 1, updates: 2,
                viewLevel: 1, wallMode: "cutaway", cameraOrientation: 0,
                cutawayTile: (12, 12), roofsVisible: false, cutawayRooms: null,
                cutawaySim: (12, 12), cutawayZoom: 2);

            Assert.NotNull(frame);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(frame));
            Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(revision, doc.RootElement.GetProperty("baseRevision").GetInt64());
            Assert.Equal(1L, doc.RootElement.GetProperty("sequence").GetInt64());
            Assert.Equal(2L, doc.RootElement.GetProperty("updates").GetInt64());

            var secondFrame = SceneExporter.Animation(
                lot, world, registry, gameRoot, sceneDir,
                house: 1, revision: revision, state: animState, sequence: 2, updates: 3,
                viewLevel: 1, wallMode: "cutaway", cameraOrientation: 0,
                cutawayTile: (12, 12), roofsVisible: false, cutawayRooms: null,
                cutawaySim: (12, 12), cutawayZoom: 2, subTickAlpha: 0.5f);
            Assert.NotNull(secondFrame);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IffFile BuildSyntheticLotWithEnclosedRoom()
    {
        var header = new byte[IffFile.HeaderSize];
        Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1").CopyTo(header, 0);
        var iff = new IffFile(header, "synthetic-lot.iff");

        // Empty version-3 objt table.
        var objtStream = PersistWriter.Start(false, 3, "tjbo");
        objtStream.Int32(0);
        iff = iff.WithAddedChunk(ResourceType.Parse("objt"), 0, objtStream.ToArray());

        // Arry 0: 64x64x2 flat terrain heights; Arry 8: 64x64x1 tile flags (0x10 indoor, 0x14 outdoor).
        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 0, BuildLotArrayChunk(64, 64, 2, new byte[64 * 64 * 2]));
        var flagBytes = new byte[64 * 64];
        for (var x = 0; x < 64; x++)
        for (var y = 0; y < 64; y++)
            flagBytes[x * 64 + y] = x is >= 10 and <= 13 && y is >= 10 and <= 13 ? (byte)0x10 : (byte)0x14;
        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 8, BuildLotArrayChunk(64, 64, 1, flagBytes));

        // Arry 2: 64x64x8 wall layer enclosing an indoor room at x=10..13, y=10..13.
        // Segment 1 is on the x-min edge of tile (x, y) (towards x - 1), style at byte 2.
        // Segment 2 is on the y-min edge of tile (x, y) (towards y - 1), style at byte 3.
        var wallBytes = new byte[64 * 64 * 8];
        void SetWall(int row, int col, byte segmentBit, int _)
        {
            var offset = (row * 64 + col) * 8;
            wallBytes[offset] |= segmentBit;
            wallBytes[offset + 1] = 0x0f;
            wallBytes[offset + 2] = 1;
            wallBytes[offset + 3] = 1;
        }

        for (var y = 10; y <= 13; y++)
        {
            SetWall(10, y, 1, 2); // West wall on x=10
            SetWall(9, y, 4, 0);  // Matching east side on x=9
            SetWall(14, y, 1, 2); // East wall on x=14 (towards x=13)
            SetWall(13, y, 4, 0); // Matching east side on x=13
        }
        for (var x = 10; x <= 13; x++)
        {
            SetWall(x, 10, 2, 3); // North wall on y=10
            SetWall(x, 9, 8, 0);  // Matching south side on y=9
            SetWall(x, 14, 2, 3); // South wall on y=14 (towards y=13)
            SetWall(x, 13, 8, 0); // Matching south side on y=13
        }

        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 2, BuildLotArrayChunk(64, 64, 8, wallBytes));
        return iff;
    }

    private static byte[] BuildLotArrayChunk(int rows, int width, int elementSize, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[20 + data.Length];
        BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, 4), rows);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(12, 4), elementSize);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(16, 4), 0u);
        data.CopyTo(chunk.AsSpan(20));
        return chunk;
    }
}
