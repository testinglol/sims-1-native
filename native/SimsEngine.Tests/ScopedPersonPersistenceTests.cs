using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class ScopedPersonPersistenceTests
{
    private static byte[] Operand(short call) => [(byte)call, 0, 0, 0, 0, 0, 0, 0];

    [Theory]
    [InlineData(0x13, 27, true)]
    [InlineData(0x13, 27, false)]
    [InlineData(0x14, 3, true)]
    [InlineData(0x14, 3, false)]
    public void ScopedPersonCallsWithoutHostPersistAfterConsumptionEvenWhenLookupMisses(int call, int key, bool personExists)
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.Temporaries[0] = 1;
        var item = new XPerson(300, PrimitiveTestWorld.Definition(XPerson.PersonType));
        me.SetObjectTableEntry(1, item);
        var scopeValue = call == 0x13 ? 1 : 3;
        var scope = new InteractionScopeTable([new InteractionScopeEntry(scopeValue, key, 2)]);
        context.World.InteractionScopes[item.InteractionScopeKey5CA] = scope;
        context.World.Host = null;
        short expected = -1;
        if (personExists)
        {
            context.World.PersonRecordKeys.Add(key);
            var person = context.World.Add(new XPerson(501, PrimitiveTestWorld.Definition(XPerson.PersonType))
            {
                TypeRecord = new TypeRecord(key),
            });
            expected = person.ClockManagerIndex;
        }
        var persisted = 0;
        context.World.PersistNeighborhoodStateImmediately = () =>
        {
            Assert.Equal(expected, me.Temporaries[0]);
            Assert.True(scope.TryGet(0, out var remaining));
            Assert.Equal(new InteractionScopeEntry(scopeValue, key, 1), remaining);
            persisted++;
        };

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand((short)call)));
        Assert.Equal(1, persisted);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0x14)]
    public void MissingScopeEntryWithoutHostSkipsPersistence(int call)
    {
        var (context, me, _) = PrimitiveTestWorld.Create();
        context.World.Host = null;
        me.Temporaries[0] = 1;
        me.SetObjectTableEntry(1, new XPerson(300, PrimitiveTestWorld.Definition(XPerson.PersonType)));
        var persisted = 0;
        context.World.PersistNeighborhoodStateImmediately = () => persisted++;

        Assert.Equal(PrimitiveResult.True, GenericSimsCallPrimitive.Execute(context, Operand((short)call)));
        Assert.Equal((short)-1, me.Temporaries[0]);
        Assert.Equal(0, persisted);
    }
}
