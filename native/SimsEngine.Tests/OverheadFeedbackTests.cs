using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class OverheadFeedbackTests
{
    private static (SimulationWorld World, XPerson PersonA, XPerson PersonB) CreateTwoSimWorld()
    {
        var (context, personA, _) = PrimitiveTestWorld.Create(stackObject: 200);
        var world = context.World;
        world.Host = null;

        var personB = world.Add(new XPerson(200, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        personA[PersonData.NeighborId] = 1;
        personB[PersonData.NeighborId] = 2;

        world.Neighbors[1] = new NeighborState(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        world.Neighbors[2] = new NeighborState(2, PrimitiveTestWorld.Definition(XPerson.PersonType));

        return (world, personA, personB);
    }

    [Fact]
    public void ProcessPersonMoneyOverhead_DispatchesAllThreeDecompiledBranchesAndExpiresAt100Ticks()
    {
        var (world, personA, _) = CreateTwoSimWorld();
        var playedSounds = new List<string>();
        world.PlaySoundEvent = (sound, _, _, _) => { playedSounds.Add(sound); return true; };

        // Branch A: positive & negative currency (> -10001)
        personA[PersonData.MoneyAmountOverHead] = 250;
        world.ProcessPersonMoneyOverhead(personA);
        Assert.Equal((short)0, personA[PersonData.MoneyAmountOverHead]);

        personA[PersonData.MoneyAmountOverHead] = -10000;
        world.ProcessPersonMoneyOverhead(personA);
        Assert.DoesNotContain("sting_char_winner", playedSounds);

        // Branch B: golden skill-up (-20000 .. -10001) -> plays sting_char_winner
        personA[PersonData.MoneyAmountOverHead] = -10001;
        world.ProcessPersonMoneyOverhead(personA);
        Assert.Single(playedSounds, "sting_char_winner");

        // Branch C: magenta (<= -20001) -> no sound
        playedSounds.Clear();
        personA[PersonData.MoneyAmountOverHead] = -20005;
        world.ProcessPersonMoneyOverhead(personA);
        Assert.Empty(playedSounds);

        var active = world.ActiveOverheadFeedback();
        Assert.Equal(4, active.Count);

        Assert.Equal(OverheadFeedbackKind.Currency, active[0].Kind);
        Assert.Equal("+$250", active[0].Text);
        Assert.Equal(0x000052u, active[0].BackgroundRgb);
        Assert.Equal(0x00CA39u, active[0].ForegroundRgb);
        Assert.Equal(0, active[0].StackIndex);

        Assert.Equal(OverheadFeedbackKind.Currency, active[1].Kind);
        Assert.Equal("-$10000", active[1].Text);
        Assert.Equal(1, active[1].StackIndex);

        Assert.Equal(OverheadFeedbackKind.SkillUp, active[2].Kind);
        Assert.Equal("1", active[2].Text);
        Assert.Equal(0xFFCB17u, active[2].BackgroundRgb);
        Assert.Equal(0x000000u, active[2].ForegroundRgb);
        Assert.Equal(2, active[2].StackIndex);

        Assert.Equal(OverheadFeedbackKind.Magenta, active[3].Kind);
        Assert.Equal("5", active[3].Text);
        Assert.Equal(0xFF00FFu, active[3].BackgroundRgb);
        Assert.Equal(0x000000u, active[3].ForegroundRgb);
        Assert.Equal(3, active[3].StackIndex);

        // Expires after 100 simulation ticks
        Assert.Equal(4, world.ActiveOverheadFeedback(99).Count);
        Assert.Empty(world.ActiveOverheadFeedback(100));
    }

    [Theory]
    [InlineData(11, "++")]
    [InlineData(10, "+")]
    [InlineData(1, "+")]
    [InlineData(-1, "-")]
    [InlineData(-8, "-")]
    [InlineData(-9, "--")]
    [InlineData(-40, "--")]
    public void RelationshipPrimitive_DailyStrChange_EmitsThresholdIndicators(short delta, string expectedText)
    {
        var (world, personA, personB) = CreateTwoSimWorld();
        var frame = new StackFrame(personA.Id, new short[8], [0, 1]) { StackObject = personB.Id };
        var ctx = new TreeContext(world, personA, frame);
        personA.Temporaries[0] = delta;

        var op = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, op));

        var active = world.ActiveOverheadFeedback();
        var evt = Assert.Single(active);
        Assert.Equal(personA.Id, evt.PersonId);
        Assert.Equal(OverheadFeedbackKind.Relationship, evt.Kind);
        Assert.Equal(expectedText, evt.Text);
        Assert.Equal(delta, evt.Delta);
    }

    [Fact]
    public void RelationshipPrimitive_ZeroDelta_DoesNotEmitOverhead()
    {
        var (world, personA, personB) = CreateTwoSimWorld();
        var frame = new StackFrame(personA.Id, new short[8], [0, 1]) { StackObject = personB.Id };
        var ctx = new TreeContext(world, personA, frame);
        personA.Temporaries[0] = 0;

        var op = new byte[] { 0, 0, 4, 0, (byte)DataOwner.Temporary, 0, 0, 0 };
        Assert.Equal(PrimitiveResult.True, RelationshipPrimitive.Execute(ctx, op));

        Assert.Empty(world.ActiveOverheadFeedback());
    }
}
