using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsEngine.World;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class FloorShadowTests
{
    [Fact]
    public void RotateShadowId_RotatesCardinalAndDiagonalNibblesIndependently_And_Preserves16()
    {
        // Single-tile shadow identifiers 0 and 16 remain invariant under all rotations.
        for (var turns = 0; turns < 4; turns++)
        {
            Assert.Equal(0, SceneExporter.RotateShadowId(0, turns));
            Assert.Equal(16, SceneExporter.RotateShadowId(16, turns));
        }

        // Cardinal shadow bits 0 through 3 rotate cyclically through values 1, 8, 4, and 2 without
        // affecting diagonal bits 4 through 7.
        Assert.Equal(8, SceneExporter.RotateShadowId(1, 1));
        Assert.Equal(4, SceneExporter.RotateShadowId(1, 2));
        Assert.Equal(2, SceneExporter.RotateShadowId(1, 3));
        Assert.Equal(1, SceneExporter.RotateShadowId(1, 4));

        // Diagonal shadow corner bits 4 through 7 rotate cyclically through values 128, 64, 32, and 16
        // independently of cardinal bits.
        Assert.Equal(64, SceneExporter.RotateShadowId(128, 1));
        Assert.Equal(32, SceneExporter.RotateShadowId(128, 2));
        Assert.Equal(16, SceneExporter.RotateShadowId(128, 3));
        Assert.Equal(128, SceneExporter.RotateShadowId(128, 4));

        var validCount = 0;
        for (var mask = 0; mask < 256; mask++)
        {
            if (!SceneExporter.ShadowMaskIsValid(mask)) continue;
            validCount++;
            if (mask == 16) continue;
            for (var turns = 1; turns < 4; turns++)
                Assert.True(SceneExporter.ShadowMaskIsValid(SceneExporter.RotateShadowId(mask, turns)));
            Assert.Equal(mask, SceneExporter.RotateShadowId(mask, 4));
        }
        Assert.Equal(47, validCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MultiTile2x3Table_HasSeamlessEdgeContinuityAcrossAllQuarterTurns(int turns)
    {
        var rawGrid = new Dictionary<(int X, int Y), int>
        {
            [(0, 0)] = 76,
            [(0, 1)] = 205,
            [(0, 2)] = 137,
            [(1, 0)] = 38,
            [(1, 1)] = 55,
            [(1, 2)] = 19,
        };

        // Object direction steps and camera orientation quarter-turns rotate tile coordinate offsets by
        // mapping (x, y) to (-y, x).
        static (int X, int Y) RotateOffset(int x, int y, int quarterTurns)
        {
            for (var step = 0; step < quarterTurns; step++)
                (x, y) = (-y, x);
            return (x, y);
        }

        var rotatedGrid = rawGrid.ToDictionary(
            pair => RotateOffset(pair.Key.X, pair.Key.Y, turns),
            pair => SceneExporter.RotateShadowId(pair.Value, turns));

        foreach (var ((cx, cy), mask) in rotatedGrid)
        {
            var lookups = SceneExporter.ShadowLookupValues(mask);
            Assert.DoesNotContain(lookups, value => value is 2 or 4 or 6);

            for (var step = 0; step <= 8; step++)
            {
                var t = step / 8f;

                var topShade = SceneExporter.EvaluateFloorShadowShade(lookups, t, 0f);
                if (rotatedGrid.TryGetValue((cx, cy - 1), out var northMask))
                {
                    var northShade = SceneExporter.EvaluateFloorShadowShade(
                        SceneExporter.ShadowLookupValues(northMask), t, 1f);
                    Assert.Equal(northShade, topShade, 5);
                }
                else Assert.Equal(0f, topShade, 5);

                var leftShade = SceneExporter.EvaluateFloorShadowShade(lookups, 0f, t);
                if (rotatedGrid.TryGetValue((cx - 1, cy), out var westMask))
                {
                    var westShade = SceneExporter.EvaluateFloorShadowShade(
                        SceneExporter.ShadowLookupValues(westMask), 1f, t);
                    Assert.Equal(westShade, leftShade, 5);
                }
                else Assert.Equal(0f, leftShade, 5);

                if (!rotatedGrid.ContainsKey((cx, cy + 1)))
                    Assert.Equal(0f, SceneExporter.EvaluateFloorShadowShade(lookups, t, 1f), 5);

                if (!rotatedGrid.ContainsKey((cx + 1, cy)))
                    Assert.Equal(0f, SceneExporter.EvaluateFloorShadowShade(lookups, 1f, t), 5);
            }
        }
    }

    [Fact]
    public void Export_MultiTile2x3TableWithPositiveShadowBrightness_EmitsConnectedRotatedMasksAndAttenuatesAlpha()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sims-floor-shadow-{Guid.NewGuid():N}");
        var gameRoot = Path.Combine(tempRoot, "game");
        var objectsDir = Path.Combine(gameRoot, "GameData", "Objects");
        var sceneDir0 = Path.Combine(tempRoot, "scene-rot0");
        var sceneDir1 = Path.Combine(tempRoot, "scene-rot1");
        Directory.CreateDirectory(objectsDir);
        Directory.CreateDirectory(sceneDir0);
        Directory.CreateDirectory(sceneDir1);

        try
        {
            File.WriteAllBytes(Path.Combine(gameRoot, "GameData", "floors.iff"), BuildSyntheticFloorsIff());
            File.WriteAllBytes(Path.Combine(objectsDir, "DiningTable2x3.iff"), BuildSynthetic2x3TableIff(shadowBrightness: 100));

            var registry = ObjectRegistry.Scan(gameRoot);
            var lot = BuildEmptyFlatLot();
            var world = new SimulationWorld { ObjectRegistry = registry };
            world.Clock.Restore(12, 0, 0);
            var roomMap = RoomMap.Build(lot, 1, 64, 64);
            world.LotRoomMaps[1] = roomMap;
            foreach (var roomId in roomMap.RoomIds)
                world.PersonRoomMotiveEntries[roomId] = new PersonRoomMotiveEntry(100f, IsOutside: roomMap.IsOutside(roomId));

            for (var ox = 0; ox < 2; ox++)
            for (var oy = 0; oy < 3; oy++)
            {
                var guid = 0x70000100u + (uint)(ox * 3 + oy + 1);
                var entry = registry.Find(guid)!;
                Assert.NotNull(entry);
                var id = (short)(ox * 3 + oy + 1);
                var part = new XObject(id, entry.Definition.Fields.Select(v => (short)v).ToArray())
                {
                    TypeRecord = new TypeRecord(unchecked((int)guid)),
                    Position = new ObjectPosition((10 + ox) * 16 + 8, (10 + oy) * 16 + 8, 1),
                };
                part[ObjectData.Direction] = 0;
                world.Add(part);
            }

            var path0 = SceneExporter.Export(
                lot, world, registry, gameRoot, sceneDir0,
                house: 5, revision: 1, viewLevel: 1, wallMode: "down", cameraOrientation: 0);
            var path1 = SceneExporter.Export(
                lot, world, registry, gameRoot, sceneDir1,
                house: 5, revision: 2, viewLevel: 1, wallMode: "down", cameraOrientation: 1);

            var shadows0 = ReadExportedSpriteShadows(path0);
            var shadows1 = ReadExportedSpriteShadows(path1);
            Assert.Equal(6, shadows0.Count);
            Assert.Equal(6, shadows1.Count);

            Assert.Equal([19, 38, 55, 76, 137, 205], shadows0.Select(s => s.Shadow).Order().ToArray());
            var expectedRot1 = new[] { 19, 38, 55, 76, 137, 205 }
                .Select(id => SceneExporter.RotateShadowId(id, 1))
                .Order()
                .ToArray();
            Assert.Equal(expectedRot1, shadows1.Select(s => s.Shadow).Order().ToArray());

            // Floor shadow textures attenuate outdoor daylight brightness, limiting the maximum shadow
            // alpha to 155 at full room lighting.
            foreach (var item in shadows0)
            {
                Assert.Equal($"floor-shadow-{item.Shadow}-100-2.rgba", item.TextureFile);
                var rgba = File.ReadAllBytes(Path.Combine(sceneDir0, item.TextureFile));
                var maxAlpha = 0;
                for (var i = 3; i < rgba.Length; i += 4)
                    if (rgba[i] > maxAlpha) maxAlpha = rgba[i];
                Assert.InRange(maxAlpha, 80, 155);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static List<(int Shadow, string TextureFile)> ReadExportedSpriteShadows(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var textures = doc.RootElement.GetProperty("textures")
            .EnumerateArray()
            .Select(t => t.GetProperty("file").GetString()!)
            .ToArray();
        var result = new List<(int Shadow, string TextureFile)>();
        foreach (var sprite in doc.RootElement.GetProperty("sprites").EnumerateArray())
        {
            var shadow = sprite.GetProperty("shadow").GetInt32();
            var floorShadow = sprite.GetProperty("floorShadow");
            Assert.NotEqual(JsonValueKind.Null, floorShadow.ValueKind);
            var texIdx = floorShadow.GetProperty("texture").GetInt32();
            result.Add((shadow, textures[texIdx]));
        }
        return result;
    }

    private static byte[] BuildSyntheticFloorsIff()
    {
        // SPR2 base shadow masks are defined by zoom level at IDs 0x000 (31x16), 0x100 (63x32), and 0x200
        // (127x64), followed by prebaked masks from 0x300 to 0x350.
        static byte[] BuildDiamondSpr2(int width, int height, byte fillIndex)
        {
            var frame = new ChunkBuilder()
                .U16((ushort)width)
                .U16((ushort)height)
                .U32(3)
                .U16(0)
                .U16(2)
                .S16(0)
                .S16(0);
            var halfH = height / 2f;
            var centerX = width / 2f;
            for (var row = 0; row < height; row++)
            {
                var dy = MathF.Abs(row + 0.5f - halfH) / halfH;
                var spanHalf = Math.Max(1, (int)MathF.Round((1f - dy) * centerX));
                var startCol = Math.Clamp((int)centerX - spanHalf, 0, width - 1);
                var count = Math.Clamp(spanHalf * 2, 1, width - startCol);
                frame.U16(0x0000);
                if (startCol > 0) frame.U16((ushort)(0x6000 | startCol));
                frame.U16((ushort)(0xC000 | count));
                for (var c = 0; c < count; c++) frame.U8(fillIndex);
                if ((count & 1) != 0) frame.U8(0);
            }
            frame.U16(0xA000);
            var frameBytes = frame.ToArray();
            return new ChunkBuilder()
                .U32(1000)
                .U32(1)
                .S32(-1)
                .S32(16)
                .Raw(frameBytes)
                .ToArray();
        }

        return ChunkBuilder.File(
            ("SPR2", 0x000, BuildDiamondSpr2(31, 16, 0)),
            ("SPR2", 0x100, BuildDiamondSpr2(63, 32, 0)),
            ("SPR2", 0x200, BuildDiamondSpr2(127, 64, 0)),
            ("SPR2", 0x300, BuildDiamondSpr2(31, 16, 64)),
            ("SPR2", 0x310, BuildDiamondSpr2(31, 16, 64)),
            ("SPR2", 0x320, BuildDiamondSpr2(63, 32, 64)),
            ("SPR2", 0x330, BuildDiamondSpr2(63, 32, 64)),
            ("SPR2", 0x340, BuildDiamondSpr2(127, 64, 64)),
            ("SPR2", 0x350, BuildDiamondSpr2(127, 64, 64)));
    }

    private static byte[] BuildSynthetic2x3TableIff(short shadowBrightness)
    {
        static byte[] BuildObjd(uint guid, ushort masterId, ushort subIndex, short shadow, short brightness, ushort baseGraphic)
        {
            var fields = new ushort[XObject.DefinitionCount];
            fields[(int)ObjdField.Version1] = 138;
            fields[(int)ObjdField.BaseGraphic] = baseGraphic;
            fields[(int)ObjdField.NumGraphics] = 1;
            fields[(int)ObjdField.Type] = 4;
            fields[(int)ObjdField.MasterId] = masterId;
            fields[(int)ObjdField.SubIndex] = subIndex;
            fields[(int)ObjdField.Guid1] = (ushort)(guid & 0xFFFF);
            fields[(int)ObjdField.Guid2] = (ushort)(guid >> 16);
            fields[(int)ObjdField.Shadow] = unchecked((ushort)shadow);
            fields[(int)ObjdField.ShadowBrightness] = unchecked((ushort)brightness);
            var builder = new ChunkBuilder();
            foreach (var field in fields) builder.U16(field);
            return builder.ToArray();
        }

        var palt = new ChunkBuilder().U32(1).U32(256).Raw(new byte[8]).Raw(new byte[256 * 3]).ToArray();
        var sprFrame = new ChunkBuilder()
            .U16(4).U16(4).U32(7).U16(1).U16(0).S16(0).S16(0)
            .U16(0x0000).U16(0x4000 | 1).U8(128).U8(1).U8(31).U8(0)
            .U16(0xA000)
            .ToArray();
        var spr2 = new ChunkBuilder().U32(1000).U32(1).S32(1).S32(16).Raw(sprFrame).ToArray();
        var dgrp = new ChunkBuilder().S16(20004).U32(12);
        foreach (var dirFlag in new uint[] { 1, 4, 16, 64 })
        foreach (var zoom in new uint[] { 1, 2, 3 })
        {
            dgrp.U32(dirFlag).U32(zoom).U32(1)
                .U32(100).U32(0).S32(0).S32(0).S32(0).U32(0).S32(0).S32(0);
        }

        var chunks = new List<(string Type, ushort Id, byte[] Data)>
        {
            ("PALT", 1, palt),
            ("SPR2", 100, spr2),
            ("DGRP", 200, dgrp.ToArray()),
            ("OBJD", 16800, BuildObjd(0x70000100u, masterId: 10, subIndex: 0xFFFF, shadow: -1, brightness: 0, baseGraphic: 0)),
        };
        ushort nextChunkId = 16801;
        for (var ox = 0; ox < 2; ox++)
        for (var oy = 0; oy < 3; oy++)
        {
            var subIndex = (ushort)(((byte)ox << 8) | (byte)oy);
            var guid = 0x70000100u + (uint)(ox * 3 + oy + 1);
            chunks.Add(("OBJD", nextChunkId++, BuildObjd(guid, masterId: 10, subIndex: subIndex, shadow: 0, brightness: shadowBrightness, baseGraphic: 200)));
        }
        return ChunkBuilder.File(chunks.ToArray());
    }

    private static IffFile BuildEmptyFlatLot(byte[]? wallData = null)
    {
        var header = new byte[IffFile.HeaderSize];
        Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1").CopyTo(header, 0);
        var iff = new IffFile(header, "flat-lot.iff");
        var objtStream = PersistWriter.Start(false, 3, "tjbo");
        objtStream.Int32(0);
        iff = iff.WithAddedChunk(ResourceType.Parse("objt"), 0, objtStream.ToArray());
        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 0, BuildLotArrayChunk(64, 64, 2, new byte[64 * 64 * 2]));
        var outdoorFlags = Enumerable.Repeat((byte)0x14, 64 * 64).ToArray();
        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 8, BuildLotArrayChunk(64, 64, 1, outdoorFlags));
        iff = iff.WithAddedChunk(ResourceType.Parse("Arry"), 2, BuildLotArrayChunk(64, 64, 8, wallData ?? new byte[64 * 64 * 8]));
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

    private static byte[] BuildSpr1Chunk(int frameCount)
    {
        var headerSize = 12 + frameCount * 4;
        var builder = new ChunkBuilder().U32(504).U32((uint)frameCount).U32(1);
        for (var i = 0; i < frameCount; i++) builder.U32((uint)headerSize);
        return builder
            .U16(0).U16(0).U16(2).U16(2)
            .U8(4).U8(0).U8(2).U8(2).U8(1).U8(1).U8(5).U8(0)
            .ToArray();
    }

    [Fact]
    public void Export_WallJunctionUsesTileRoom_AndRuntimeSlotSpritesHideAtFarZoom()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sims-wave3-scene-{Guid.NewGuid():N}");
        var gameRoot = Path.Combine(tempRoot, "game");
        var objectsDir = Path.Combine(gameRoot, "GameData", "Objects");
        var sceneDir = Path.Combine(tempRoot, "scene");
        Directory.CreateDirectory(objectsDir);
        Directory.CreateDirectory(sceneDir);

        try
        {
            var palt = new ChunkBuilder().U32(1).U32(2).U32(0).U32(0).Raw([10, 20, 30, 240, 240, 240]).ToArray();
            var wallsIff = ChunkBuilder.File(
                ("PALT", 1, palt),
                ("SPR#", 4096, BuildSpr1Chunk(16)),
                ("SPR#", 4097, BuildSpr1Chunk(16)),
                ("SPR#", 4098, BuildSpr1Chunk(16)));
            File.WriteAllBytes(Path.Combine(gameRoot, "GameData", "walls.iff"), wallsIff);
            File.WriteAllBytes(Path.Combine(gameRoot, "GameData", "floors.iff"), BuildSyntheticFloorsIff());
            File.WriteAllBytes(Path.Combine(objectsDir, "DiningTable2x3.iff"), BuildSynthetic2x3TableIff(shadowBrightness: 100));

            var registry = ObjectRegistry.Scan(gameRoot);
            var wallData = new byte[64 * 64 * 8];
            for (var x = 10; x <= 14; x++)
            {
                wallData[(10 * 64 + x) * 8] |= 0x01;
                wallData[(15 * 64 + x) * 8] |= 0x01;
            }
            for (var y = 10; y <= 14; y++)
            {
                wallData[(y * 64 + 10) * 8] |= 0x02;
                wallData[(y * 64 + 15) * 8] |= 0x02;
            }
            wallData[(12 * 64 + 12) * 8] |= 0x01;

            var lot = BuildEmptyFlatLot(wallData);
            var spritesBytes = ChunkBuilder.File(("PALT", 1, palt), ("SPR#", 104, BuildSpr1Chunk(3)));
            var spritesIff = new IffFile(spritesBytes, "Sprites.iff");
            var world = new SimulationWorld { ObjectRegistry = registry, GlobalSprites = spritesIff };
            world.Clock.Restore(12, 0, 0);
            var roomMap = RoomMap.Build(lot, 1, 64, 64);
            world.LotRoomMaps[1] = roomMap;
            foreach (var roomId in roomMap.RoomIds)
                world.PersonRoomMotiveEntries[roomId] = new PersonRoomMotiveEntry(100f, IsOutside: roomMap.IsOutside(roomId));

            var guid = 0x70000101u;
            var entry = registry.Find(guid)!;
            var obj = new XObject(1, entry.Definition.Fields.Select(v => (short)v).ToArray())
            {
                TypeRecord = new TypeRecord(unchecked((int)guid)),
                Position = new ObjectPosition(12 * 16 + 8, 12 * 16 + 8, 1),
            };
            var runtimeSlot = new ObjectRuntimeSlot();
            runtimeSlot.WriteWord(0x10, 1);
            runtimeSlot.WriteWord(0x14, 104);
            obj.RuntimeSlots.Add(runtimeSlot);
            world.Add(obj);

            var scenePath = SceneExporter.Export(
                lot, world, registry, gameRoot, sceneDir,
                house: 5, revision: 1, viewLevel: 1, wallMode: "up", cameraOrientation: 0);

            using var doc = JsonDocument.Parse(File.ReadAllText(scenePath));
            var sprites = doc.RootElement.GetProperty("sprites").EnumerateArray().ToArray();

            var junction = sprites.First(s =>
                s.TryGetProperty("junction", out var j) &&
                j.GetProperty("x").GetInt32() == 12 &&
                j.GetProperty("y").GetInt32() == 12);
            Assert.Equal((int)roomMap.RoomAt(12, 12), junction.GetProperty("room").GetInt32());

            var slotSprite = sprites.First(s =>
                s.TryGetProperty("objectID", out var idEl) && idEl.GetInt32() == 1 &&
                (!s.TryGetProperty("shadow", out var sh) || sh.ValueKind == JsonValueKind.Null));
            // Runtime slot sprites are not rendered at far zoom.
            Assert.True(slotSprite.GetProperty("hideAtFarZoom").GetBoolean());
            Assert.True(!slotSprite.TryGetProperty("far", out var farEl) || farEl.ValueKind == JsonValueKind.Null);
            Assert.True(slotSprite.TryGetProperty("near", out var nearEl) && nearEl.ValueKind == JsonValueKind.Object);
            Assert.True(nearEl.GetProperty("width").GetSingle() > 0f);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
