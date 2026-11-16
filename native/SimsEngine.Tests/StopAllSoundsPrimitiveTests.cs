using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class StopAllSoundsPrimitiveTests
{
    private static byte[] Operand(short word0) => [(byte)word0, (byte)(word0 >> 8), 0, 0];

    [Fact]
    public void AHostlessWorldStopsNothingAndStillAnswersTrue()
    {
        var (context, _, _) = PrimitiveTestWorld.Create(stackObject: 5);
        context.World.Host = null;
        Assert.Equal(PrimitiveResult.True, StopAllSoundsPrimitive.Execute(context, Operand(0)));
    }

    [Fact]
    public void ZeroOwnerStopsTheRunningObjectsSounds()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 5);
        Assert.Equal(PrimitiveResult.True, StopAllSoundsPrimitive.Execute(context, Operand(0)));
        Assert.Equal(["StopOwnerSounds(100)"], host.Calls);
    }

    [Fact]
    public void OtherOwnerStopsTheStackObjectsSoundsByItsIdWhetherOrNotItIsOnTheLot()
    {
        var (context, _, host) = PrimitiveTestWorld.Create(stackObject: 9);
        Assert.Equal(PrimitiveResult.True, StopAllSoundsPrimitive.Execute(context, Operand(1)));
        Assert.Equal(["StopOwnerSounds(9)"], host.Calls);
    }
}
