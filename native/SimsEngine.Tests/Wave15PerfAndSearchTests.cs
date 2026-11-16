using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class Wave15PerfAndSearchTests
{
    private static byte[] Operand(byte mode, bool explicitTarget = false, byte owner = 0, byte data = 0, int guid = 0, byte local = 0) =>
        [(byte)guid, (byte)(guid >> 8), (byte)(guid >> 16), (byte)(guid >> 24),
            (byte)(mode | (explicitTarget ? 0x80 : 0)), owner, local, data];

    private static byte[] ObjdBytes(uint guid, bool disabled = false, string? semiGlobal = null)
    {
        var body = new ChunkBuilder();
        for (var field = 0; field <= (int)ObjdField.BuildModeType; field++)
            body.U16(field switch
            {
                14 => (ushort)guid,
                15 => (ushort)(guid >> 16),
                16 => (ushort)(disabled ? 1 : 0),
                _ => 0,
            });
        if (semiGlobal is null)
            return ChunkBuilder.File(("OBJD", 4096, body.ToArray()));

        var glob = new ChunkBuilder().Tag(semiGlobal).U8(0).ToArray();
        return ChunkBuilder.File(("GLOB", 128, glob), ("OBJD", 4096, body.ToArray()));
    }

    private static byte[] MultiEntryArchive(params (string Name, byte[] Payload)[] entries)
    {
        var manifestOffset = 16;
        foreach (var (_, payload) in entries)
            manifestOffset += payload.Length;

        var manifestSize = 4;
        foreach (var (name, _) in entries)
            manifestSize += 16 + Encoding.ASCII.GetByteCount(name);

        var bytes = new byte[manifestOffset + manifestSize];
        "FAR!byAZ"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)manifestOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestOffset), (uint)entries.Length);

        var dataCursor = 16;
        var manifestCursor = manifestOffset + 4;
        foreach (var (name, payload) in entries)
        {
            var encoded = Encoding.ASCII.GetBytes(name);
            payload.CopyTo(bytes, dataCursor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestCursor), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestCursor + 4), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestCursor + 8), (uint)dataCursor);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(manifestCursor + 12), (uint)encoded.Length);
            encoded.CopyTo(bytes, manifestCursor + 16);
            dataCursor += payload.Length;
            manifestCursor += 16 + encoded.Length;
        }
        return bytes;
    }

    [Fact]
    public void SetToNextMode9_ResolvesAdjacentSameRoomObjectViaWorldObjectTableWhenActorTableEmpty()
    {
        var world = new SimulationWorld { Host = null };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var anchor = world.Add(new XObject(5, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(0x20, 0x30, 1),
            RoomWord84 = 4,
        });
        var east = world.Add(new XObject(7, PrimitiveTestWorld.Definition())
        {
            Position = new ObjectPosition(0x30, 0x30, 1),
            RoomWord84 = 4,
        });
        world.SetObjectPosition(anchor, anchor.Position);
        world.SetObjectPosition(east, east.Position);

        var context = new TreeContext(world, me, new StackFrame(0, [], [anchor.StackObjectKey]));
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(9)));
        Assert.Equal(east.StackObjectKey, context.Frame.StackObject);
    }

    [Fact]
    public void SetToNextMode11_WalksOccupiedNeighborSlotsAndPreservesTargetOnHoleOrEndWithoutHost()
    {
        var world = new SimulationWorld { Host = null };
        var me = world.Add(new XPerson(100, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        world.NeighborWalkOrder.Add(new NeighborState(1, []));
        world.NeighborWalkOrder.Add(null);
        world.NeighborWalkOrder.Add(new NeighborState(3, []));

        var context = new TreeContext(world, me, new StackFrame(0, [], [0]));
        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal((short)1, context.Frame.StackObject);

        Assert.Equal(PrimitiveResult.True, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal((short)3, context.Frame.StackObject);

        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(context, Operand(11)));
        Assert.Equal((short)3, context.Frame.StackObject);

        var holeContext = new TreeContext(world, me, new StackFrame(2, [], [0]));
        Assert.Equal(PrimitiveResult.False, SetToNextPrimitive.Execute(holeContext, Operand(11)));
        Assert.Equal((short)2, holeContext.Frame.StackObject);
    }

    [Fact]
    public void ObjectRegistryScan_WarmArchiveCacheMatchesColdScanAndHonorsSkipDisabled()
    {
        var temp = Directory.CreateTempSubdirectory("wave15-registry");
        try
        {
            Directory.CreateDirectory(Path.Combine(temp.FullName, "GameData"));
            var farBytes = MultiEntryArchive(
                ("ActiveA.iff", ObjdBytes(0x1111, disabled: false, semiGlobal: "ChairGlobals")),
                ("DisabledB.iff", ObjdBytes(0x2222, disabled: true)),
                ("ActiveC.iff", ObjdBytes(0x3333, disabled: false)));
            File.WriteAllBytes(Path.Combine(temp.FullName, "Objects.far"), farBytes);

            var coldAll = ObjectRegistry.Scan(temp.FullName, skipDisabled: false);
            var warmAll = ObjectRegistry.Scan(temp.FullName, skipDisabled: false);
            var warmSkip = ObjectRegistry.Scan(temp.FullName, skipDisabled: true);

            Assert.Equal(coldAll.Entries.Select(e => (e.Guid, e.Source, e.SemiGlobal)),
                warmAll.Entries.Select(e => (e.Guid, e.Source, e.SemiGlobal)));
            Assert.NotNull(warmAll.Find(0x2222));

            Assert.Null(warmSkip.Find(0x2222));
            Assert.Equal([0x1111u, 0x3333u], warmSkip.Entries.Select(e => e.Guid));
        }
        finally
        {
            Directory.Delete(temp.FullName, true);
        }
    }

    [Fact]
    public void SimulationServerAudioEvent_ReturnsPlayedFalseWithoutThrowingWhenAudioDisabled()
    {
        var temp = Directory.CreateTempSubdirectory("wave15-audio-rpc");
        try
        {
            var gameDir = Path.Combine(temp.FullName, "game");
            var sessionDir = Path.Combine(temp.FullName, "session");
            var profileDir = Path.Combine(temp.FullName, "profile");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(sessionDir);
            Directory.CreateDirectory(profileDir);

            using var outputStream = new MemoryStream();
            using var writer = new StreamWriter(outputStream, new UTF8Encoding(false)) { AutoFlush = true };
            var server = SimulationServer.Load(gameDir, sessionDir, profileDir, 0, writer);
            outputStream.SetLength(0);
            outputStream.Position = 0;

            Assert.True(server.Handle("""{"id":"evt1","op":"audioEvent","name":"ui_click"}"""));
            writer.Flush();
            var responseText = Encoding.UTF8.GetString(outputStream.ToArray());
            using var doc = JsonDocument.Parse(responseText.Trim());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("result").GetProperty("played").GetBoolean());
        }
        finally
        {
            Directory.Delete(temp.FullName, true);
        }
    }
}
