using System.Buffers.Binary;
using System.Text;
using SimsEngine.Formats;
using Xunit;

namespace SimsEngine.Tests;

public class HouseFileTests
{
    private static ChunkBuilder Persist(int version, string tag, bool swap = false) =>
        new ChunkBuilder().U8(swap ? (byte)1 : (byte)0).U8(0).U8(0).U8(0).S32(version).Tag(new string(tag.Reverse().ToArray()));

    [Fact]
    public void FamilyVersion7ReadsBudgetFlagsAndMembers()
    {
        var (file, chunk) = Persist(7, "FAMI")
            .S32(3).S32(12).S32(20000).S32(45000).S32(0).U32(1)
            .U32(2).U32(0x1234).U32(0x5678)
            .InFile("FAMI", 5);
        var family = Family.Read(file, chunk);
        Assert.Equal(7, family.Version);
        Assert.Equal(3, family.HouseNumber);
        Assert.Null(family.Name);
        Assert.Equal(12, family.FamilyNumber);
        Assert.Equal(20000, family.Budget);
        Assert.Equal(45000, family.Value);
        Assert.Equal(1u, family.Flags);
        Assert.Equal([0x1234u, 0x5678u], family.Members);
    }

    [Fact]
    public void FamilyChunkZeroHasNoMemberList()
    {
        var (file, chunk) = Persist(7, "FAMI").S32(0).S32(0).S32(0).S32(0).S32(0).U32(0).U32(0).InFile("FAMI", 0);
        Assert.Empty(Family.Read(file, chunk).Members);
    }

    [Fact]
    public void FamilyBeforeVersion7ReadsFlagBytesAndName()
    {
        var (file, chunk) = Persist(4, "FAMI")
            .S32(1).EvenString("Newbie").S32(2).S32(100).S32(200).S32(0).U8(0).U8(1)
            .U32(0)
            .InFile("FAMI", 1);
        var family = Family.Read(file, chunk);
        Assert.Equal("Newbie", family.Name);
        Assert.Equal(2u, family.Flags);
    }

    [Fact]
    public void FamilyVersion9WriterRoundTripsTenRecordsAndTheEmptySentinel()
    {
        var (sourceFile, sourceChunk) = Persist(7, "FAMI")
            .S32(3).S32(12).S32(20000).S32(45000).S32(0).U32(1)
            .U32(2).U32(0x1234).U32(0x5678)
            .InFile("FAMI", 5);
        var source = Family.Read(sourceFile, sourceChunk);
        var sentinel = source.WithMembership(0, []);
        var expected = Enumerable.Range(1, 10)
            .Select(index => source.WithMembership(100 + index, [unchecked((uint)(0xA0000000 + index))]))
            .ToArray();
        var chunks = new List<(string Type, ushort Id, byte[] Body)> { ("FAMI", 0, sentinel.Write(0)) };
        chunks.AddRange(expected.Select((family, index) => ("FAMI", (ushort)(index + 1), family.Write((ushort)(index + 1)))));

        var file = new IffFile(ChunkBuilder.File(chunks.ToArray()), "ten-families.iff");
        var decoded = file.OfType(ResourceType.Parse("FAMI"))
            .Select(chunk => (chunk.Id, Family: Family.Read(file, chunk)))
            .ToArray();

        Assert.Equal(11, decoded.Length);
        Assert.Empty(decoded[0].Family.Members);
        Assert.Equal((ushort)0, decoded[0].Id);
        for (var index = 0; index < expected.Length; index++)
        {
            var (id, actual) = decoded[index + 1];
            var wanted = expected[index];
            Assert.Equal((ushort)(index + 1), id);
            Assert.Equal(9, actual.Version);
            Assert.Null(actual.Name);
            Assert.Equal(wanted.HouseNumber, actual.HouseNumber);
            Assert.Equal(wanted.FamilyNumber, actual.FamilyNumber);
            Assert.Equal(wanted.Budget, actual.Budget);
            Assert.Equal(wanted.Value, actual.Value);
            Assert.Equal(wanted.Field0x120, actual.Field0x120);
            Assert.Equal(wanted.Flags, actual.Flags);
            Assert.Equal(wanted.Members, actual.Members);
            Assert.Equal(wanted.Field0x13C, actual.Field0x13C);
            Assert.Equal(wanted.Field0x14E, actual.Field0x14E);
        }
    }

    [Fact]
    public void FamilyVersion9WriterRejectsMembersOnTheChunkZeroSentinel()
    {
        var (file, chunk) = Persist(7, "FAMI")
            .S32(0).S32(0).S32(0).S32(0).S32(0).U32(0).U32(0)
            .InFile("FAMI", 0);
        var family = Family.Read(file, chunk).WithMembership(0, [0x1234]);

        Assert.Throws<InvalidDataException>(() => family.Write(0));
    }

    [Fact]
    public void FamilyBudgetUpdatePreservesTheSourceRecordFields()
    {
        var (file, chunk) = Persist(7, "FAMI")
            .S32(3).S32(12).S32(20000).S32(45000).S32(9).U32(1)
            .U32(2).U32(0x1234).U32(0x5678)
            .InFile("FAMI", 5);
        var source = Family.Read(file, chunk);

        var changed = source.WithBudget(12500);

        Assert.Equal(12500, changed.Budget);
        Assert.Equal(source.HouseNumber, changed.HouseNumber);
        Assert.Equal(source.FamilyNumber, changed.FamilyNumber);
        Assert.Equal(source.Value, changed.Value);
        Assert.Equal(source.Field0x120, changed.Field0x120);
        Assert.Equal(source.Flags, changed.Flags);
        Assert.Equal(source.Members, changed.Members);
        Assert.Equal(source.Field0x13C, changed.Field0x13C);
        Assert.Equal(source.Field0x14E.Count == 6 ? source.Field0x14E : new short[6], changed.Field0x14E);
        var written = new IffFile(ChunkBuilder.File(("FAMI", (ushort)5, changed.Write(5))), "family-budget.iff");
        Assert.Equal(12500, Family.Read(written, written.Find(ResourceType.Parse("FAMI"), 5)!).Budget);
    }

    [Fact]
    public void FamilyVersion9WriterSupportsSwappedResourceStreams()
    {
        var (file, chunk) = Persist(7, "FAMI", swap: true)
            .Raw([0, 0, 0, 3]).Raw([0, 0, 0, 12]).Raw([0, 0, 0x4E, 0x20])
            .Raw([0, 0, 0xAF, 0xC8]).Raw([0, 0, 0, 7]).Raw([0, 0, 0, 1])
            .Raw([0, 0, 0, 1]).Raw([0x12, 0x34, 0x56, 0x78])
            .InFile("FAMI", 5);
        var source = Family.Read(file, chunk);

        var written = source.Write(5, swap: true);
        var (roundTripFile, roundTripChunk) = new ChunkBuilder().Raw(written).InFile("FAMI", 5);
        var roundTrip = Family.Read(roundTripFile, roundTripChunk);

        Assert.Equal(9, roundTrip.Version);
        Assert.Equal(source.FamilyNumber, roundTrip.FamilyNumber);
        Assert.Equal(source.Budget, roundTrip.Budget);
        Assert.Equal(source.Value, roundTrip.Value);
        Assert.Equal(source.Flags, roundTrip.Flags);
        Assert.Equal(source.Members, roundTrip.Members);
    }

    [Fact]
    public void SwapFlagMakesValuesBigEndian()
    {
        var (file, chunk) = Persist(7, "FAMI", swap: true)
            .Raw([0, 0, 0, 9]).Raw([0, 0, 0, 1]).Raw([0, 0, 0x4E, 0x20]).Raw(new byte[12]).Raw(new byte[4])
            .InFile("FAMI", 0);
        var family = Family.Read(file, chunk);
        Assert.Equal(9, family.HouseNumber);
        Assert.Equal(20000, family.Budget);
    }

    [Fact]
    public void NeighborsSkipEmptySlotsAndReadSlottedRelationships()
    {
        var builder = Persist(0x45, "NBRS").U32(2)
            .U32(0)
            .U32(1).S32(4).EvenString("Bob").S32(0).S32(1);
        for (var index = 0; index < 0x50; index++) builder.S16((short)index);
        builder.S16(7).U32(0xABCD)
            .S32(-1).U32(2).U32(0).U32(1).S32(9).U32(2).S32(50).S32(-20);
        var (file, chunk) = builder.InFile("NBRS");
        var neighbors = Neighbors.Read(file, chunk);
        Assert.Equal(2, neighbors.SlotCount);
        var bob = Assert.Single(neighbors.Entries);
        Assert.Equal(1, bob.Slot);
        Assert.Equal("Bob", bob.Name);
        Assert.Equal(0x50, bob.PersonData!.Count);
        Assert.Equal(7, bob.NeighborId);
        Assert.Equal(0xABCDu, bob.Guid);
        Assert.Equal([50, -20], bob.Relationships[9]);
    }

    [Fact]
    public void Version73NeighborsReadMixedRecordFormatsWithoutLosingSlots()
    {
        var builder = Persist(0x49, "NBRS").U32(3)
            .U32(1).S32(10).S32(9).EvenString("New format").S32(17).S32(0).S16(21).U32(0x12345678).S32(0)
            .U32(0)
            .U32(1).S32(4).EvenString("Legacy").S32(18).S32(0).S16(22).U32(0x87654321).S32(0);

        var (file, chunk) = builder.InFile("NBRS");
        var neighbors = Neighbors.Read(file, chunk);

        Assert.Equal(3, neighbors.SlotCount);
        Assert.Equal(new[] { 0, 2 }, neighbors.Entries.Select(entry => entry.Slot));
        Assert.Equal(new[] { 9, 4 }, neighbors.Entries.Select(entry => entry.RecordVersion));
        Assert.Equal("New format", neighbors.Entries[0].Name);
        Assert.Equal(17, neighbors.Entries[0].Field0x1C);
        Assert.Equal((short)22, neighbors.Entries[1].NeighborId);
        Assert.Equal(0x87654321u, neighbors.Entries[1].Guid);
    }

    [Fact]
    public void NeighborhoodDataReadsSixteenValues()
    {
        var builder = Persist(0x3e, "NGBH");
        for (short index = 0; index < 16; index++) builder.S16(index);
        var (file, chunk) = builder.InFile("NGBH");
        var data = NeighborhoodData.Read(file, chunk);
        Assert.Equal(16, data.Data.Count);
        Assert.Equal(15, data.Data[15]);
        Assert.Equal(Enumerable.Range(0, 16).Select(index => (short)index), data.ToVariables());
    }

    [Fact]
    public void NeighborhoodDataOldVersionLeavesRemainingScriptVariablesAtZero()
    {
        var (file, chunk) = Persist(0x13, "NGBH").S16(173).InFile("NGBH");
        var data = NeighborhoodData.Read(file, chunk);
        Assert.Equal((short)173, data.ToVariables()[0]);
        Assert.All(data.ToVariables().Skip(1), value => Assert.Equal((short)0, value));
    }

    [Fact]
    public void NeighborhoodDataReadsScriptValuesAndReportsVersion73Extension()
    {
        var builder = Persist(0x49, "NGBH");
        for (short index = 0; index < 16; index++) builder.S16((short)(index + 100));
        builder.Raw(new byte[16]);
        var (file, chunk) = builder.InFile("NGBH");
        var data = NeighborhoodData.Read(file, chunk);
        Assert.True(data.HasUnparsedExtendedObjectList);
        Assert.Equal(Enumerable.Range(100, 16).Select(index => (short)index), data.ToVariables());
    }

    [Fact]
    public void NeighborhoodDataPreservesRepeatedInventoryGuidsAndRecordOrder()
    {
        var builder = Persist(0x49, "NGBH");
        for (short index = 0; index < 16; index++) builder.S16(index);
        builder.S32(2)
            .S32(1).S16(34).S32(3)
            .S32(0).U32(0x80000001).U16(3)
            .S32(0).U32(0x80000001).U16(9)
            .S32(8).U32(7).U16(0)
            .S32(1).S16(35).S32(1)
            .S32(7).U32(0x12345678).U16(4);
        var (file, chunk) = builder.InFile("NGBH");

        var data = NeighborhoodData.Read(file, chunk);

        Assert.Equal(new short[] { 34, 35 }, data.Inventories.Keys.Order().ToArray());
        Assert.Equal(new NeighborhoodData.InventoryItem[]
        {
            new(0, 0x80000001, 3), new(0, 0x80000001, 9), new(8, 7, 0),
        }, data.Inventories[34]);
        Assert.Equal(new NeighborhoodData.InventoryItem(7, 0x12345678, 4), Assert.Single(data.Inventories[35]));
    }

    [Fact]
    public void HouseReadsRotationAndRoof()
    {
        var (file, chunk) = Persist(0x45, "HOUS")
            .U32(0x100).S32(1).S32(2).S32(-3).S16(2).S16(1).S16(1).F32(0.5f).EvenString("roof")
            .InFile("HOUS", 128);
        var house = House.Read(file, chunk);
        Assert.True(house.Flag0x48);
        Assert.False(house.Flag0x49);
        Assert.Equal(-3, house.Global6F0084);
        Assert.Equal((short)2, house.CameraRotation);
        Assert.True(house.Flag0x51);
        Assert.Equal(0.5f, house.RoofPitch);
        Assert.Equal("roof", house.RoofName);
    }

    [Fact]
    public void SimulationStateReadsGlobalsValuesAndBudgetDays()
    {
        var builder = Persist(0x45, "SIMI");
        for (short index = 0; index < 0x40; index++) builder.S16(index);
        builder.S32(101232).S16(0).S32(5).S32(6).S32(7).S32(8).S32(4500).S32(1000).S32(2000);
        builder.U32(1);
        for (var index = 0; index < 8; index++) builder.S32(index);
        for (var day = 0; day < 5; day++)
        {
            builder.U32(0);
            for (var index = 0; index < 7; index++) builder.S32(0);
        }
        var (file, chunk) = builder.InFile("SIMI", 1);
        var state = SimulationState.Read(file, chunk);
        Assert.Equal(0x40, state.Globals.Count);
        Assert.Equal(101232, state.Field0x98);
        Assert.Equal(6, state.Field0xAC);
        Assert.Equal(4500, state.Field0x9C);
        Assert.Equal(2000, state.Field0xA4);
        Assert.Equal(6, state.BudgetDays.Count);
        Assert.Equal(8, state.BudgetDays[0].Values.Count);
        Assert.Equal(7, state.BudgetDays[1].Values.Count);
        Assert.Equal(builder.ToArray(), state.WriteChunkData());

        var withNewSeed = new IffFile(ChunkBuilder.File(("SIMI", 1, state.WriteChunkData(0xdeadbeef))), "roundtrip.iff");
        var reread = SimulationState.Read(withNewSeed, withNewSeed.Chunks.Single());
        Assert.Equal(unchecked((int)0xdeadbeefu), reread.Field0xAC);
        Assert.Equal(state.Globals, reread.Globals);
        Assert.Equal(state.BudgetDays.Count, reread.BudgetDays.Count);
        for (var day = 0; day < state.BudgetDays.Count; day++)
        {
            Assert.Equal(state.BudgetDays[day].Valid, reread.BudgetDays[day].Valid);
            Assert.Equal(state.BudgetDays[day].Values, reread.BudgetDays[day].Values);
        }
    }

    [Fact]
    public void PreVersion14SimulationStateHasNoStoredRandomSeed()
    {
        var builder = Persist(0x0d, "SIMI");
        for (short index = 0; index < 0x10; index++) builder.S16(index);
        builder.U32(0).S32(0).S16(0).S32(0);
        var (file, chunk) = builder.InFile("SIMI", 1);

        var state = SimulationState.Read(file, chunk);

        Assert.Equal(0x0d, state.Version);
        Assert.Equal(0, state.Field0xAC);
    }

    [Fact]
    public void SimulationStateWriterPreservesSwappedStreamAndUpdatesTheSeed()
    {
        const int version = 0x0e;
        var body = new byte[12 + 0x10 * 2 + 4 + 4 + 2 + 4 + 4 + 4];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), version);
        Encoding.ASCII.GetBytes("IMIS").CopyTo(body, 8);
        var offset = 12;
        void Write16(short value)
        {
            BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(offset), value);
            offset += 2;
        }
        void Write32(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(offset), value);
            offset += 4;
        }
        for (short index = 0; index < 0x10; index++) Write16(index);
        Write32(99);
        Write32(100);
        Write16(101);
        Write32(102);
        Write32(103);
        Write32(104);
        var (file, chunk) = new ChunkBuilder().Raw(body).InFile("SIMI");
        var state = SimulationState.Read(file, chunk);

        Assert.True(state.Swap);
        Assert.Equal(body, state.WriteChunkData());

        var updated = new IffFile(ChunkBuilder.File(("SIMI", 1, state.WriteChunkData(0x12345678))), "swapped-roundtrip.iff");
        var reread = SimulationState.Read(updated, updated.Chunks.Single());
        Assert.Equal(unchecked((int)0x12345678u), reread.Field0xAC);
        Assert.Equal(104, reread.StoredTime);
    }

    [Fact]
    public void LotArrayDecodesRunsWithEvenPadding()
    {
        // Array chunk run-length encoding pads repeat runs to 16-bit boundaries, while literal runs require
        // no padding.
        var (file, chunk) = new ChunkBuilder().S16(0).S16(0).S32(2).S32(3).S32(1).U32(1)
            .U16(0x8004).U8(7).U8(0xEE).U16(2).U8(1).U8(2)
            .InFile("Arry", 8);
        var array = LotArray.Read(file, chunk);
        Assert.False(array.OldLayout);
        Assert.Equal([7, 7, 7, 7, 1, 2], array.Data);
        Assert.Equal(2, array.Element(1, 2)[0]);
    }

    [Fact]
    public void LotArrayReadsRawAndOldLayouts()
    {
        var (raw, rawChunk) = new ChunkBuilder().S16(0).S16(0).S32(1).S32(2).S32(2).U32(0).Raw([1, 2, 3, 4]).InFile("Arry", 0);
        Assert.Equal([1, 2, 3, 4], LotArray.Read(raw, rawChunk).Data);

        // Legacy array chunk headers specify element size, row count, and column width, with element data
        // starting after the header and row offset table.
        var (old, oldChunk) = new ChunkBuilder().S16(1).S16(2).S16(2).Raw(new byte[10]).Raw([5, 6, 7, 8]).InFile("Arry", 0);
        var array = LotArray.Read(old, oldChunk);
        Assert.True(array.OldLayout);
        Assert.Equal([5, 6, 7, 8], array.Data);
    }

    [Fact]
    public void LotArrayWritesItsShapeBackWithoutRunCoding()
    {
        var (file, chunk) = new ChunkBuilder().S16(0).S16(0).S32(2).S32(3).S32(1).U32(1)
            .U16(0x8004).U8(7).U8(0xEE).U16(2).U8(1).U8(2)
            .InFile("Arry", 8);
        var array = LotArray.Read(file, chunk);
        var rewritten = array.ToChunkData([9, 8, 7, 6, 5, 4]);
        var (again, againChunk) = new ChunkBuilder().Raw(rewritten).InFile("Arry", 8);
        var reread = LotArray.Read(again, againChunk);
        Assert.False(reread.RunLengthCoded);
        Assert.Equal((2, 3, 1), (reread.Rows, reread.Width, reread.ElementSize));
        Assert.Equal([9, 8, 7, 6, 5, 4], reread.Data);
        Assert.Throws<ArgumentException>(() => array.ToChunkData(new byte[5]));
    }

    [Fact]
    public void LotArrayRejectsARunPastTheEnd()
    {
        var (file, chunk) = new ChunkBuilder().S16(0).S16(0).S32(1).S32(2).S32(1).U32(1).U16(0x8003).U8(1).U8(0).InFile("Arry", 0);
        Assert.Throws<ResourceFormatException>(() => LotArray.Read(file, chunk));
    }

    /// <summary>Packs fields most significant bit first: a 0 bit for zero, or a 1 bit followed by a two-bit
    /// width index and the value in that many bits.</summary>
    private sealed class FieldWriter
    {
        private readonly List<bool> _bits = [];

        public FieldWriter Field(int value, int widthIndex, int width)
        {
            if (value == 0) return Bits(0, 1);
            return Bits(1, 1).Bits((uint)widthIndex, 2).Bits((uint)value, width);
        }

        public FieldWriter Bits(uint value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--) _bits.Add(((value >> bit) & 1) != 0);
            return this;
        }

        public byte[] Bytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var index = 0; index < _bits.Count; index++)
                if (_bits[index]) bytes[index / 8] |= (byte)(0x80 >> (index % 8));
            return bytes;
        }
    }

    [Fact]
    public void TypeAttributesReadCodedFieldsWithSignExtension()
    {
        var fields = new FieldWriter()
            .Field(1, 0, 6)
            .Field(0x12345678, 3, 32)
            .Field(2, 0, 6)
            .Field(-1, 0, 5).Field(300, 2, 13);
        var (file, chunk) = Persist(0, "TATT").U8(1).Raw(fields.Bytes()).InFile("TATT", 0);
        var attributes = TypeAttributes.Read(file, chunk);
        Assert.Equal([(short)-1, (short)300], attributes.AttributesByGuid[0x12345678]);
    }

    [Fact]
    public void TypeAttributesReadUncodedWhenTheFlagIsClear()
    {
        var (file, chunk) = Persist(0, "TATT").U8(0).U32(1).U32(7).U32(1).S16(-5).InFile("TATT", 0);
        Assert.Equal([(short)-5], TypeAttributes.Read(file, chunk).AttributesByGuid[7]);
    }

    [Fact]
    public void CodedStreamReadsAStringFromTheNextWholeByte()
    {
        // Bit-packed field streams align to the next byte boundary before writing strings, which are then
        // padded to 16-bit alignment.
        var fields = new FieldWriter().Field(1, 0, 2);
        var (file, chunk) = Persist(0, "TEST").Raw(fields.Bytes()).EvenString("ab").U8(0x84).InFile("TEST", 0);
        var stream = new PersistReader(file, chunk);
        stream.BeginFieldCoding();
        Assert.Equal(1, stream.Byte());
        Assert.Equal("ab", stream.String());
        Assert.Equal(4, stream.Int16());
    }

    [Fact]
    public void FamilyHistoryWidensVersion0Values()
    {
        var builder = Persist(0, "FAMh").U8(0).U32(1);
        for (short index = 0; index < 8; index++) builder.S16(index);
        builder.S16(-2).S16(3);
        for (short index = 0; index < 7; index++) builder.S16(index);
        for (short index = 0; index < 6; index++) builder.S16((short)(index - 3));
        builder.S32(100000).S32(-100000);
        for (short index = 0; index < 5; index++) builder.S16(index);
        var (file, chunk) = builder.InFile("FAMh", 0);
        var entry = Assert.Single(FamilyHistory.Read(file, chunk).Entries);
        Assert.Equal([-2, 3], entry.Values0x10);
        Assert.Equal([-3, -2, -1, 0, 1, 2], entry.Values0x28);
        Assert.Equal([100000, -100000], entry.Values0x40);
        Assert.Equal(4, entry.Values0x48[4]);
    }

    [Fact]
    public void ObjectTypeTableStopsAtGuidZeroAndReadsVersionedFields()
    {
        var (file, chunk) = Persist(3, "objt")
            .U32(0xABCD).U32(1).U32(2).S16(1).S16(2).EvenString("*Person").S32(9)
            .U32(0).InFile("objt", 0);
        var entry = Assert.Single(ObjectTypeTable.Read(file, chunk).Entries);
        Assert.Equal(new ObjectTypeEntry(0xABCD, 1, 2, 1, 2, "*Person", 9), entry);

        var (old, oldChunk) = Persist(0, "objt").U32(7).S16(4).EvenString("x").U32(0).InFile("objt", 0);
        Assert.Equal(new ObjectTypeEntry(7, 0, 0, 4, -1, "x", 0), Assert.Single(ObjectTypeTable.Read(old, oldChunk).Entries));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ObjectTypeTableWriterPreservesVersionedFieldsAndAddsEntries(int version, bool swap)
    {
        var builder = Persist(version, "objt").U32(0x12345678);
        if (version > 0) builder.U32(4).U32(5);
        builder.S16(2);
        if (version >= 2) builder.S16(4);
        builder.EvenString("chair");
        if (version >= 3) builder.S32(42);
        var (file, chunk) = builder.U32(0).InFile("objt");
        var table = ObjectTypeTable.Read(file, chunk);
        Assert.Equal(chunk.Data.ToArray(), table.Write(false, "tjbo"));
        var entries = table.Entries.Append(table.Entries[0] with { Guid = 0x23456789, TypeIndex = 7 }).ToArray();
        var rewritten = new IffFile(ChunkBuilder.File(("objt", 0, table.Write(swap, "tjbo", entries))), "types-save.iff");
        Assert.Equal(entries, ObjectTypeTable.Read(rewritten, rewritten.OfType(ResourceType.Parse("objt")).Single()).Entries);
    }

    [Fact]
    public void LotObjectsReadTheListThenOneBlockPerObject()
    {
        // ObjM chunks consist of a header, flags, an object list, and chained state data blocks linked by
        // relative offsets.
        var (file, chunk) = Persist(0x45, "ObjM").U8(0).S16(5).S16(2).S16(0)
            .U32(26 - PersistReader.HeaderSize).Raw([0xAA, 0xBB, 0xCC]).U32(0)
            .S16(7).U32(1).S16(9).EvenString("n")
            .InFile("ObjM", 1);
        var objects = LotObjects.Read(file, chunk);
        var item = Assert.Single(objects.Objects);
        Assert.Equal((short)5, item.ObjectId);
        Assert.Equal((short)2, item.TypeIndex);
        Assert.Equal([0xAA, 0xBB, 0xCC], item.State);
        Assert.Null(item.Head);
        Assert.Equal((short)7, objects.Object0xA8);
        Assert.Equal([(short)9], objects.ObjectList0x38);
        Assert.Equal("n", objects.Name);
        Assert.Equal(0, objects.UnreadBytes);
    }

    [Fact]
    public void LotObjectsRejectABlockOutsideTheChunk()
    {
        var (file, chunk) = Persist(0x45, "ObjM").U8(0).S16(5).S16(2).S16(0).U32(500).InFile("ObjM", 1);
        Assert.Throws<ResourceFormatException>(() => LotObjects.Read(file, chunk));
    }
    [Fact]
    public void LotObjectsDecodeAnObjectStateToTheBlockEnd()
    {
        var data = new short[0x48];
        data[1] = 2;
        var state = new ChunkBuilder().S32(1).S32(2).S32(3).S32(4).S32(48).S32(64).S32(5).S16(1)
            .S16(2).S16(3).S16(4);
        for (var index = 0; index < 8; index++) state.S16((short)index);
        foreach (var value in data) state.S16(value);
        state.S16(0)
            .U32(1).S32(64).S16(5).S16(4096).S16(3).U8(1).U8(2).S16(10).S16(11).S16(12).S32(7).S16(1)
            .S32(-1).U32(2).U32(0).U32(1).S32(9).U32(2).S32(20).S32(30)
            .S16(1).S16(0).S16(6)
            .S16(1).S16(8);
        var objectState = new ChunkBuilder().Raw(state.ToArray()).S32(99);
        var head = Persist(0x45, "ObjM").U8(0).S16(5).S16(1).S16(0);
        var end = head.Length + 4 + objectState.Length;
        var (file, chunk) = head.U32((uint)(end - PersistReader.HeaderSize)).Raw(objectState.ToArray()).U32(0)
            .S16(0).U32(0).EvenString("").InFile("ObjM", 1);
        var (typeFile, typeChunk) = Persist(3, "objt").U32(0xABCD).U32(0).U32(0).S16(1).S16(4).EvenString("chair").S32(0)
            .U32(0).InFile("objt", 0);
        var types = ObjectTypeTable.Read(typeFile, typeChunk);
        var chair = Definition(type: 4, masterId: 0);

        Assert.Null(Assert.Single(LotObjects.Read(file, chunk, types).Objects).Decoded);
        var decoded = Assert.Single(LotObjects.Read(file, chunk, types, Find(chair)).Objects).Decoded;
        Assert.NotNull(decoded);
        Assert.Equal([1, 2, 3, 4, 48, 64], decoded.Values0x108);
        Assert.Equal(5, decoded.Value0x110);
        Assert.Null(decoded.Value0x114);
        Assert.Equal([(short)3, 4], decoded.Attributes);
        Assert.Equal((short)7, decoded.Temps[7]);
        Assert.Equal((short)2, decoded.Data[1]);
        Assert.Equal(64, decoded.StackSize);
        var frame = Assert.Single(decoded.Stack);
        Assert.Equal((5, (ushort)4096, (short)3), (frame.StackObject, frame.Tree, frame.Node));
        Assert.Equal([(short)10, 11], frame.Parameters);
        Assert.Equal([(short)12], frame.Locals);
        Assert.Equal((7, (short)1), (frame.Value0x08, frame.CodeOwner));
        Assert.Equal(2, decoded.Relationships.Count);
        Assert.Null(decoded.Relationships[0]);
        var relationship = decoded.Relationships[1]!;
        Assert.Equal(9, relationship.Value0x0C);
        Assert.Equal([20, 30], relationship.Values);
        Assert.Equal([(short)6], decoded.Slots);
        Assert.Equal([(short)8], decoded.Values0x34);
        Assert.Equal(99, decoded.ManagerFlags);
        Assert.Null(decoded.Person);
        Assert.Null(decoded.MultiTile);

        var (shortFile, shortChunk) = Persist(0x45, "ObjM").U8(0).S16(5).S16(1).S16(0)
            .U32((uint)(end + 2 - PersistReader.HeaderSize)).Raw(objectState.ToArray()).S16(0).U32(0)
            .S16(0).U32(0).EvenString("").InFile("ObjM", 1);
        Assert.Throws<ResourceFormatException>(() => LotObjects.Read(shortFile, shortChunk, types, Find(chair)));

        // Portal parts (OBJD type 8 with a master ID) serialize multi-tile offset fields and floating-point
        // positions before trailing block integers.
        var portalState = new ChunkBuilder().Raw(state.ToArray()).S32(1).S32(2).S32(3).S32(4).S32(5).S32(6).S16(5)
            .U32(1).F32(2.5f).S32(99);
        var portalHead = Persist(0x45, "ObjM").U8(0).S16(5).S16(1).S16(0);
        var portalEnd = portalHead.Length + 4 + portalState.Length;
        var (portalFile, portalChunk) = portalHead.U32((uint)(portalEnd - PersistReader.HeaderSize))
            .Raw(portalState.ToArray()).U32(0).S16(0).U32(0).EvenString("").InFile("ObjM", 1);
        var portal = Assert.Single(LotObjects.Read(portalFile, portalChunk, types, Find(Definition(type: 8, masterId: 3)))
            .Objects).Decoded!;
        Assert.Equal(new MultiTileState(1, 2, 3, 4, 5, 6, 5, null), portal.MultiTile! with { PortalValues = null });
        Assert.Equal([2.5f], portal.MultiTile!.PortalValues!);
        Assert.Equal(99, portal.ManagerFlags);
    }

    [Theory]
    [InlineData(2, 0, ObjectClass.Person)]
    [InlineData(4, 0, ObjectClass.Object)]
    [InlineData(8, 0, null)]
    [InlineData(9, 0, null)]
    [InlineData(8, 1, ObjectClass.Portal)]
    [InlineData(2, 1, ObjectClass.MultiTileObject)]
    public void ObjectClassFollowsTheDefinitionTypeAndMasterId(int type, int masterId, ObjectClass? expected) =>
        Assert.Equal(expected, LotObjects.ClassOf(Definition((ushort)type, (ushort)masterId)));

    [Fact]
    public void PlaceholderDefinitionsAreTheFourSimsExeSubstitutes()
    {
        Assert.True(LotObjects.IsPlaceholder(0x3C08965F));
        Assert.True(LotObjects.IsPlaceholder(0x7BEA0977));
        Assert.False(LotObjects.IsPlaceholder(0xABCD));
    }

    /// <summary>Creates a 16-field OBJD definition with the specified object type in field 9 and master ID
    /// in field 10.</summary>
    private static Objd Definition(ushort type, ushort masterId)
    {
        var fields = new ushort[16];
        fields[(int)ObjdField.Type] = type;
        fields[(int)ObjdField.MasterId] = masterId;
        fields[(int)ObjdField.Guid1] = 0xABCD;
        var builder = new ChunkBuilder();
        foreach (var field in fields) builder.U16(field);
        var (file, chunk) = builder.InFile("OBJD");
        return Objd.Read(file, chunk);
    }

    private static Func<uint, Objd?> Find(Objd definition) => guid => guid == definition.Guid ? definition : null;
}
