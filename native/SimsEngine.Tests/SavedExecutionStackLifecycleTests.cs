using SimsEngine.Simulation;

namespace SimsEngine.Tests;

public sealed class SavedExecutionStackLifecycleTests
{
    [Fact]
    public void RemovingOrReplacingAnObjectCannotLeaveItsSavedStackRunnable()
    {
        var world = new SimulationWorld();
        var original = world.Add(new XObject(7, new short[XObject.DefinitionCount]));
        var originalStack = TreeRunner.RestoreSavedStack(world, original, _ => null);
        world.SavedExecutionStacks.Add(original.Id, originalStack);

        world.Add(original);
        Assert.Same(originalStack, world.SavedExecutionStacks[original.Id]);

        var replacement = new XObject(7, new short[XObject.DefinitionCount]);
        world.Add(replacement);
        Assert.Same(replacement, world.ObjectById(7));
        Assert.False(world.SavedExecutionStacks.ContainsKey(7));

        var replacementStack = TreeRunner.RestoreSavedStack(world, replacement, _ => null);
        world.SavedExecutionStacks.Add(replacement.Id, replacementStack);
        Assert.True(world.Remove(replacement.Id));
        Assert.Null(world.ObjectById(7));
        Assert.False(world.SavedExecutionStacks.ContainsKey(7));
    }
}
