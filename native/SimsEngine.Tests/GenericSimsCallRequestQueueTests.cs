using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public sealed class GenericSimsCallRequestQueueTests
{
    [Theory]
    [InlineData(GenericSimsCallRequestKind.First)]
    [InlineData(GenericSimsCallRequestKind.Second)]
    public void StartKeepsTheFiveRecordFieldsInListOrder(GenericSimsCallRequestKind kind)
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 0x1234 };

        var id = queue.Start(person, temporary0: -7, kind: kind);

        Assert.Equal(1, queue.Count);
        Assert.Equal(new GenericSimsCallPendingRequest(id, 0x1234, -7, 0, kind, 0, 0),
            Assert.Single(queue.Snapshot()));
    }

    [Fact]
    public void PollLeavesPendingRequestInPlaceThenConsumesCompletedResult()
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 42 };
        var id = queue.Start(person, temporary0: 8, GenericSimsCallRequestKind.Second);

        Assert.Null(queue.Poll(person));
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryComplete(id, temporary0: -2, temporary1: 19, GenericSimsCallRequestStatus.Succeeded));

        Assert.Equal(new GenericSimsCallRequestResult(-2, 19, 2), queue.Poll(person));
        Assert.Null(queue.Poll(person));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void FailedRequestIsReturnedAndConsumedWithItsStatus()
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 9 };
        var id = queue.Start(person, temporary0: 3, GenericSimsCallRequestKind.First);
        Assert.True(queue.TryComplete(id, temporary0: -1, temporary1: 88, GenericSimsCallRequestStatus.Failed));

        Assert.Equal(new GenericSimsCallRequestResult(-1, 88, 3), queue.Poll(person));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CompletionRejectsStatusesTheOriginalManagerNeverWrites()
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 9 };
        var id = queue.Start(person, temporary0: 3, GenericSimsCallRequestKind.First);

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.TryComplete(
            id, temporary0: -1, temporary1: 88, (GenericSimsCallRequestStatus)1));

        Assert.Equal(new GenericSimsCallPendingRequest(id, 9, 3, 0, GenericSimsCallRequestKind.First, 0, 0),
            Assert.Single(queue.Snapshot()));
    }

    [Fact]
    public void PollStopsAtFirstMatchingPendingDuplicateKey()
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 5 };
        var first = queue.Start(person, temporary0: 1, GenericSimsCallRequestKind.First);
        var second = queue.Start(person, temporary0: 2, GenericSimsCallRequestKind.Second);
        Assert.True(queue.TryComplete(second, temporary0: 20, temporary1: 21, GenericSimsCallRequestStatus.Succeeded));

        Assert.Null(queue.Poll(person));
        Assert.True(queue.TryComplete(first, temporary0: 10, temporary1: 11, GenericSimsCallRequestStatus.Failed));

        Assert.Equal(new GenericSimsCallRequestResult(10, 11, 3), queue.Poll(person));
        Assert.Equal(new GenericSimsCallRequestResult(20, 21, 2), queue.Poll(person));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TickExpiresRequestOnItsHundredthAgeIncrement()
    {
        var queue = new GenericSimsCallRequestQueue();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 7 };
        var id = queue.Start(person, temporary0: 0, GenericSimsCallRequestKind.First);

        for (var i = 0; i < 99; i++) queue.AdvanceTick();
        Assert.Equal((short)99, Assert.Single(queue.Snapshot()).Age);
        Assert.Equal(1, queue.Count);

        queue.AdvanceTick();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryComplete(id, temporary0: 1, temporary1: 2, GenericSimsCallRequestStatus.Succeeded));
    }

    [Fact]
    public void TickAgesAndExpiresEveryNodeWithoutChangingInsertionOrder()
    {
        var queue = new GenericSimsCallRequestQueue();
        var firstPerson = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 1 };
        var secondPerson = new XObject(101, PrimitiveTestWorld.Definition()) { RequestKey = 2 };
        var first = queue.Start(firstPerson, temporary0: 11, GenericSimsCallRequestKind.First);
        var second = queue.Start(secondPerson, temporary0: 22, GenericSimsCallRequestKind.Second);
        Assert.True(queue.TryComplete(second, temporary0: 22, temporary1: 0, GenericSimsCallRequestStatus.Succeeded));

        queue.AdvanceTick();

        Assert.Equal([first, second], queue.Snapshot().Select(request => request.Id));
        Assert.All(queue.Snapshot(), request => Assert.Equal((short)1, request.Age));
    }

    [Fact]
    public void WorldUpdateDispatchesBothKindsStoresOutputsAndRaisesSuccessFlagInSourceOrder()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.Me.TypeRecord = new TypeRecord(10);
        var firstPerson = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 11 };
        var secondPerson = new XObject(101, PrimitiveTestWorld.Definition()) { RequestKey = 22 };
        var firstTarget = context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition())
        {
            RequestKey = 4,
            TypeRecord = new TypeRecord(20),
        });
        var secondTarget = context.World.Add(new XPerson(202, PrimitiveTestWorld.Definition())
        {
            RequestKey = 5,
            TypeRecord = new TypeRecord(21),
        });
        var laterDuplicate = context.World.Add(new XPerson(203, PrimitiveTestWorld.Definition())
        {
            RequestKey = 4,
            TypeRecord = new TypeRecord(22),
        });
        var resultPerson = context.World.Add(new XPerson(204, PrimitiveTestWorld.Definition())
        {
            RequestKey = 49,
            TypeRecord = new TypeRecord(77),
        });
        context.World.Add(new XPerson(205, PrimitiveTestWorld.Definition())
        {
            RequestKey = 50,
            TypeRecord = new TypeRecord(77),
        });
        var first = context.World.GenericSimsCallRequests.Start(firstPerson, 4, GenericSimsCallRequestKind.First);
        var second = context.World.GenericSimsCallRequests.Start(secondPerson, 5, GenericSimsCallRequestKind.Second);
        host.GenericSimsCallRequestProcessor = (request, _) => request.Kind == GenericSimsCallRequestKind.First
            ? new GenericSimsCallRequestCompletion(-4, 40, Succeeded: false)
            : new GenericSimsCallRequestCompletion(123, 0, Succeeded: true, TargetTypeRecordKey: 77);

        context.World.UpdateGenericSimsCallRequests();

        Assert.Equal([
            new GenericSimsCallPendingRequest(first, 11, -4, 40, GenericSimsCallRequestKind.First, 3, 1),
            new GenericSimsCallPendingRequest(second, 22, 0, 49, GenericSimsCallRequestKind.Second, 2, 1),
        ], context.World.GenericSimsCallRequests.Snapshot());
        Assert.Equal([
            "GenericSimsCallRequestManagerAvailable",
            "ProcessGenericSimsCallRequest(1,11,4,0,201)",
            "GenericSimsCallRequestManagerAvailable",
            "ProcessGenericSimsCallRequest(2,22,5,1,202)",
        ], host.Calls);
        Assert.Equal((byte)1, context.World.GlobalPerson.ByteE0);
        Assert.Same(firstTarget, context.World.WalkOrder[1]);
        Assert.Same(secondTarget, context.World.WalkOrder[2]);
        Assert.Same(laterDuplicate, context.World.WalkOrder[3]);
        Assert.Equal((short)49, resultPerson.RequestKey);
        Assert.Equal(new GenericSimsCallRequestResult(-4, 40, 3), context.World.GenericSimsCallRequests.Poll(firstPerson));
        Assert.Equal(new GenericSimsCallRequestResult(0, 49, 2), context.World.GenericSimsCallRequests.Poll(secondPerson));
    }

    [Fact]
    public void WorldUpdateLeavesRequestsPendingWhenHostCannotProcessAndStillAgesThem()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 8 };
        var id = context.World.GenericSimsCallRequests.Start(person, 12, GenericSimsCallRequestKind.First);
        host.GenericSimsCallRequestManagerAvailableAnswer = false;

        context.World.UpdateGenericSimsCallRequests();

        Assert.Equal(new GenericSimsCallPendingRequest(id, 8, 12, 0, GenericSimsCallRequestKind.First, 0, 1),
            Assert.Single(context.World.GenericSimsCallRequests.Snapshot()));
        Assert.Equal(["GenericSimsCallRequestManagerAvailable"], host.Calls);
    }

    [Fact]
    public void WorldUpdateAgesAndExpiresRequestsWithoutAHost()
    {
        var world = new SimulationWorld();
        world.Add(new XPerson(201, PrimitiveTestWorld.Definition()) { RequestKey = 12 });
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 8 };
        var id = world.GenericSimsCallRequests.Start(person, 12, GenericSimsCallRequestKind.First);

        for (var tick = 0; tick < 99; tick++) world.UpdateGenericSimsCallRequests();

        Assert.Equal((short)99, Assert.Single(world.GenericSimsCallRequests.Snapshot()).Age);
        Assert.Null(world.GenericSimsCallRequests.Poll(person));

        world.UpdateGenericSimsCallRequests();

        Assert.Empty(world.GenericSimsCallRequests.Snapshot());
        Assert.False(world.GenericSimsCallRequests.TryComplete(
            id, 1, 2, GenericSimsCallRequestStatus.Succeeded));
    }

    [Fact]
    public void WorldUpdateFailsUnmatchedRequestWithoutChangingItsPayload()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 8 };
        var id = context.World.GenericSimsCallRequests.Start(person, 12, GenericSimsCallRequestKind.Second);
        host.GenericSimsCallRequestProcessor = (_, _) => throw new InvalidOperationException("No object should be dispatched.");

        context.World.UpdateGenericSimsCallRequests();

        Assert.Equal(new GenericSimsCallPendingRequest(id, 8, 12, 0, GenericSimsCallRequestKind.Second, 3, 1),
            Assert.Single(context.World.GenericSimsCallRequests.Snapshot()));
        Assert.Equal(["GenericSimsCallRequestManagerAvailable"], host.Calls);
    }

    [Fact]
    public void KindTwoFailureWithMatchedPersonClearsOnlyTemporaryOne()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition()) { RequestKey = 14 });
        var person = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 8 };
        var id = context.World.GenericSimsCallRequests.Start(person, 14, GenericSimsCallRequestKind.Second);
        host.GenericSimsCallRequestProcessor = (_, _) =>
            new GenericSimsCallRequestCompletion(90, 91, Succeeded: false);

        context.World.UpdateGenericSimsCallRequests();

        Assert.Equal(new GenericSimsCallPendingRequest(id, 8, 14, 0, GenericSimsCallRequestKind.Second, 3, 1),
            Assert.Single(context.World.GenericSimsCallRequests.Snapshot()));
        Assert.Equal(["GenericSimsCallRequestManagerAvailable", "ProcessGenericSimsCallRequest(1,8,14,1,201)"],
            host.Calls);
        Assert.Equal((byte)0, context.World.GlobalPerson.ByteE0);
    }

    [Fact]
    public void WorldUpdateReportsTheOriginalUngatedMissingTypeRecordDereference()
    {
        var (context, _, host) = PrimitiveTestWorld.Create();
        context.Me.TypeRecord = new TypeRecord(10);
        context.World.Add(new XPerson(201, PrimitiveTestWorld.Definition())
        {
            RequestKey = 12,
            TypeRecord = new TypeRecord(20),
        });
        var requestOwner = new XObject(100, PrimitiveTestWorld.Definition()) { RequestKey = 8 };
        context.World.GenericSimsCallRequests.Start(requestOwner, 12, GenericSimsCallRequestKind.Second);
        host.GenericSimsCallRequestProcessor = (_, _) =>
            new GenericSimsCallRequestCompletion(0, 0, Succeeded: true, TargetTypeRecordKey: 77);

        var exception = Assert.Throws<InvalidOperationException>(context.World.UpdateGenericSimsCallRequests);
        Assert.Contains("could not resolve type-record key 77", exception.Message);
    }

    [Fact]
    public void TypeRecordLookupReportsTheOriginalUngatedDereferenceBeforeLaterMatches()
    {
        var world = new SimulationWorld();
        world.Add(new XObject(201, PrimitiveTestWorld.Definition()));
        world.Add(new XObject(202, PrimitiveTestWorld.Definition())
        {
            TypeRecord = new TypeRecord(77),
        });

        var exception = Assert.Throws<InvalidOperationException>(() => world.ObjectByTypeRecordKey(77));

        Assert.Contains("would dereference a missing type record on object 201", exception.Message);
    }
}
