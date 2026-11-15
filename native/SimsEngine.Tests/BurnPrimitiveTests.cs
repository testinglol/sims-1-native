using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class BurnPrimitiveTests
{
    // Operand: byte 0 the mode (1 adds the origin's footprint), byte 1 the flags (bit 0 clear is the strict check).
    private static byte[] Operand(byte mode, byte flags = 0) => [mode, flags, 0, 0, 0, 0, 0, 0];

    private static SimulationError Fails(Action action) => Assert.Throws<SimulationException>(action).Error;

    // The host records the key in hex (0x24C95F99), the spread in decimal.
    private const string FireCall = "FindObjectType(24C95F99)";

    /// <summary>A 64-tile lot with the origin object (stack object 5) at <paramref name="position"/>, the running
    /// person (100) and the Fire type.</summary>
    private static (TreeContext Context, PrimitiveTestHost Host, XObject Origin) Lot(ObjectPosition position, short direction = 2)
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        var origin = context.World.Add(new XObject(5, PrimitiveTestWorld.Definition()));
        origin.Position = position;
        origin[ObjectData.Direction] = direction;
        host.FireType = new TypeRecord(BurnPrimitive.FireType);
                return (context, host, origin);
    }

    // Tile (10, 14) at height 7: X 160 is tile 10, Y 224 is tile 14.
    private static readonly ObjectPosition Centre = new(160, 224, 7);

    [Fact]
    public void MissingStackObjectRaisesNoStackObjectBeforeAnyHostCall()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 77);
        Assert.Equal(SimulationError.NoStackObjectPresentWhenRequired, Fails(() => BurnPrimitive.Execute(context, Operand(0))));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void ClosedGateAnswersFalseBeforeTheFireTypeIsLookedUp()
    {
        var (context, host, _) = Lot(Centre);
        host.BurnEnabledAnswer = false;
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.Equal(new[] { "BurnEnabled" }, host.Calls);
    }

    [Fact]
    public void OpenGateLooksUpTheFireTypeThenSpreadsAtTheOriginPosition()
    {
        var (context, host, _) = Lot(Centre);
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(context, Operand(0)));
        Assert.Equal(
            new[] { "BurnEnabled", FireCall, $"BurnSpread(100,160,224,7,{BurnPrimitive.FireType},True)" },
            host.Calls);
    }

    [Fact]
    public void SpreadAnswerIsThePrimitiveResult()
    {
        var (context, host, _) = Lot(Centre);
        host.SpreadAnswer = false;
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void MissingFireTypeStillSpreadsWithNoType()
    {
        var (context, host, _) = Lot(Centre);
        host.FireType = null;
        BurnPrimitive.Execute(context, Operand(0));
        Assert.Equal($"BurnSpread(100,160,224,7,null,True)", host.Calls[^1]);
    }

    [Fact]
    public void ModeOneMovesThePositionByTheFootprintOfTheDirection()
    {
        var (context, host, _) = Lot(Centre, direction: 6);
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(context, Operand(1)));
        Assert.Equal(
            new[]
            {
                "BurnEnabled", FireCall, "BurnFootprintClear(9,14,7,6)",
                $"BurnSpread(100,144,224,7,{BurnPrimitive.FireType},True)",
            },
            host.Calls);
    }

    [Fact]
    public void OnlyModeOneAsksTheFootprint()
    {
        var (context, host, _) = Lot(Centre);
        BurnPrimitive.Execute(context, Operand(2));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("FootprintOffset") || call.StartsWith("BurnFootprintClear"));
    }

    [Fact]
    public void MissingFootprintClearFailsModeOneBeforeTheSpread()
    {
        var (context, host, _) = Lot(Centre);
        host.FootprintClearAnswer = false;
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(1)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));
    }

    [Fact]
    public void ModeOneTileOutOfTheLotFailsBeforeTheFootprintClearIsAsked()
    {
        var (context, host, _) = Lot(new ObjectPosition(0, 224, 7), direction: 6);
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(1)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnFootprintClear"));
    }

    [Fact]
    public void TileZeroAndTheLastTileAreOutsideTheLot()
    {
        // Sims.exe's lot dimension is 64: tiles 1 to 62 are inside (0 and 63 are outside).
        var (context, host, _) = Lot(new ObjectPosition(0, 224, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));

        (context, host, _) = Lot(new ObjectPosition(63 * 16, 224, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));

        (context, host, _) = Lot(new ObjectPosition(62 * 16, 224, 7));
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(context, Operand(0)));

        (context, host, _) = Lot(new ObjectPosition(16, 224, 7));
        Assert.Equal(PrimitiveResult.True, BurnPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void TheYAxisHasTheSameBounds()
    {
        var (context, host, _) = Lot(new ObjectPosition(160, 63 * 16, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));

        (context, host, _) = Lot(new ObjectPosition(160, 0, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void NegativeTilesAreOutsideTheLot()
    {
        var (context, host, _) = Lot(new ObjectPosition(-16, 224, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));
    }

    [Fact]
    public void TheTileIsAByteSoALargeTileWrapsToANegativeOne()
    {
        // 128 tiles is 2048 units; Sims.exe keeps the tile as a C char, which reads it as -128, outside the lot.
        var (context, host, _) = Lot(new ObjectPosition(2048, 224, 7));
        Assert.Equal(PrimitiveResult.False, BurnPrimitive.Execute(context, Operand(0)));
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("BurnSpread"));
    }

    [Fact]
    public void StrictFlagIsClearedByOperandByteOneBitZero()
    {
        var (context, host, _) = Lot(Centre);
        BurnPrimitive.Execute(context, Operand(0, flags: 1));
        Assert.EndsWith(",False)", host.Calls[^1]);
        BurnPrimitive.Execute(context, Operand(0, flags: 0xFE));
        Assert.EndsWith(",True)", host.Calls[^1]);
    }

    [Fact]
    public void NativeLotDimensionIsUsedWithoutAHostQuery()
    {
        // Bounds use the source manager's fixed +0x10 dimension (64).
        var (context, host, _) = Lot(new ObjectPosition(0, 224, 7));
        BurnPrimitive.Execute(context, Operand(0));
        Assert.DoesNotContain(host.Calls, call => call == "LotSize");
    }
}
