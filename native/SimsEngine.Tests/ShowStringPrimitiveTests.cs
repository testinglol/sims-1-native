using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;

namespace SimsEngine.Tests;

/// <summary>Show String (0x15), after FUN_00451af7: the string lookup, the "name : text" message to the UI and the
/// result, which is always true.</summary>
public class ShowStringPrimitiveTests
{
    private const short TableWord = 0x0102;

    /// <summary>The operand: the string table in word 0 and the signed string index in word 1.</summary>
    private static byte[] Operand(short table = TableWord, short index = 1) =>
        [(byte)table, (byte)(table >> 8), (byte)index, (byte)(index >> 8), 0, 0, 0, 0];

    private static (TreeContext Context, PrimitiveTestHost Host, XPerson Me) Lot(string? name = "Bob")
    {
        var (context, me, host) = PrimitiveTestWorld.Create();
        me.TypeRecord = new TypeRecord(1) { Name = name };
        return (context, host, me);
    }

    [Fact]
    public void TheMessageIsTheNameThenTheTextToTheUi()
    {
        var (context, host, _) = Lot();
        host.Strings[(TableWord, 0)] = "Hi";
        Assert.Equal(PrimitiveResult.True, ShowStringPrimitive.Execute(context, Operand()));
        Assert.Equal(new[] { (TableWord, 0) }, host.StringRequests);
        Assert.Equal(new[] { (0xEB, "Bob : Hi") }, host.Broadcasts);
        Assert.Equal(new[] { (0xEB, "Bob : Hi") }, context.World.UiMessages.Select(message => (message.Message, message.Text)));
    }

    [Fact]
    public void AnUnnamedRecordPrintsTheTextAfterTheSeparator()
    {
        var (context, host, _) = Lot(name: null);
        host.Strings[(TableWord, 0)] = "Hi";
        ShowStringPrimitive.Execute(context, Operand());
        Assert.Equal(new[] { (0xEB, " : Hi") }, host.Broadcasts);
    }

    [Fact]
    public void EmptyTextSendsNothing()
    {
        var (context, host, _) = Lot();
        host.Strings[(TableWord, 0)] = "";
        Assert.Equal(PrimitiveResult.True, ShowStringPrimitive.Execute(context, Operand()));
        Assert.Empty(host.Broadcasts);
    }

    [Fact]
    public void MissingTextSendsNothing()
    {
        var (context, host, _) = Lot();
        Assert.Equal(PrimitiveResult.True, ShowStringPrimitive.Execute(context, Operand()));
        Assert.Empty(host.Broadcasts);
    }

    [Fact]
    public void MissingTypeRecordIsOnlyAnErrorWhenThereIsTextToShow()
    {
        var (context, host, me) = Lot();
        me.TypeRecord = null;
        // With no text there is nothing to name, so the missing record is not read.
        Assert.Equal(PrimitiveResult.True, ShowStringPrimitive.Execute(context, Operand()));
        host.Strings[(TableWord, 0)] = "Hi";
        Assert.Throws<InvalidOperationException>(() => ShowStringPrimitive.Execute(context, Operand()));
    }

    [Fact]
    public void AHostlessWorldRetainsTheMessageForItsFrontend()
    {
        var (context, host, _) = Lot();
        context.World.Host = null;
        var strings = new ChunkBuilder().S16(-1).S16(1).CString("The fridge is bare.");
        var file = new IffFile(ChunkBuilder.File(("STR#", unchecked((ushort)TableWord), strings.ToArray())), "fridge.iff");
        var scope = new BehaviorScope(file, file, null);
        context.Me.TypeRecord = new TypeRecord(1) { Name = "Bob", BehaviorScope = scope };
        var frame = new StackFrame(0, [], []) { TreeId = 4109, Table = scope };
        var scopedContext = new TreeContext(context.World, context.Me, frame);
        Assert.Equal(PrimitiveResult.True, ShowStringPrimitive.Execute(scopedContext, Operand()));
        Assert.Equal(new[] { (0xEB, "Bob : The fridge is bare.") }, context.World.UiMessages.Select(message => (message.Message, message.Text)));
    }

    [Fact]
    public void NonpositiveOneBasedIndexesDoNotReadAString()
    {
        var (context, host, _) = Lot();
        host.Strings[(TableWord, -2)] = "Hi";
        ShowStringPrimitive.Execute(context, Operand(index: -2));
        Assert.Empty(host.StringRequests);
        Assert.Empty(host.Broadcasts);
    }

    [Fact]
    public void ShowStringInATreeBranchesTrue()
    {
        var (context, host, _) = Lot();
        host.Strings[(TableWord, 0)] = "Hi";
        var tree = PrimitiveTestWorld.Tree(0, (0x15, 0xFE, 0xFF, Operand()));
        Assert.True(TreeRunner.Run(context, tree));
        Assert.Single(host.Broadcasts);
    }
}
