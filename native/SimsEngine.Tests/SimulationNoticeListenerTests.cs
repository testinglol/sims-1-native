using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class SimulationNoticeListenerTests
{
    [Fact]
    public void MissingListenerListReturnsFalse()
    {
        var world = new SimulationWorld();

        Assert.False(world.DispatchNotice(0xe8, 0));
    }

    [Fact]
    public void DispatchCallsEveryListenerInRegistrationOrderAndAggregatesResults()
    {
        var world = new SimulationWorld();
        var calls = new List<(int Message, int Value)>();
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            calls.Add((message, value));
            return false;
        }));
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            calls.Add((message, value));
            return true;
        }));
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            calls.Add((message, value));
            return false;
        }));

        Assert.True(world.DispatchNotice(0xe8, 0));
        Assert.Equal([(0xe8, 0), (0xe8, 0), (0xe8, 0)], calls);
    }

    [Fact]
    public void DispatchCapturesNextBeforeCallingListener()
    {
        var world = new SimulationWorld();
        var calls = new List<string>();
        var first = new SimulationNoticeListener((_, _) =>
        {
            calls.Add("first");
            world.NoticeListeners!.Next = null;
            return false;
        });
        var second = new SimulationNoticeListener((_, _) =>
        {
            calls.Add("second");
            return false;
        });
        first.Next = second;
        world.AddNoticeListener(first);

        Assert.False(world.DispatchNotice(0xe8, 0));
        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public void KillObjectRemovesBeforeDispatchingTheRemovalNotice()
    {
        var world = new SimulationWorld();
        var item = world.Add(new XObject(5, new short[XObject.DefinitionCount]));
        var notices = new List<(int Message, int Value)>();
        var removedBeforePostNotice = false;
        world.AddNoticeListener(new SimulationNoticeListener((message, value) =>
        {
            notices.Add((message, value));
            if (message == 0x87)
                Assert.NotNull(world.ObjectById(item.Id));
            else if (message == 0x88)
                removedBeforePostNotice = world.ObjectById(item.Id) is null;
            return false;
        }));

        Assert.True(world.KillObject(item.Id));
        Assert.Equal([(0x87, (int)item.Id), (0x88, (int)item.Id)], notices);
        Assert.True(removedBeforePostNotice);
        Assert.False(world.KillObject(item.Id));
    }
}
