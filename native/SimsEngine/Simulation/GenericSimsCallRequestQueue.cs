namespace SimsEngine.Simulation;

/// <summary>The two manager request modes started by GenericSimsCall 0x27 and 0x28.</summary>
public enum GenericSimsCallRequestKind
{
    First = 0,
    Second = 1,
}

/// <summary>The terminal states FUN_0046DC72 accepts when a request is polled.</summary>
public enum GenericSimsCallRequestStatus
{
    Succeeded = 2,
    Failed = 3,
}

/// <summary>The manager state needed by the host's asynchronous update pass.</summary>
public readonly record struct GenericSimsCallPendingRequest(
    long Id,
    short PersonKey,
    short Temporary0,
    short Temporary1,
    GenericSimsCallRequestKind Kind,
    int Status,
    short Age);

/// <summary>
/// The request list used by GenericSimsCall 0x27/0x28. Sims.exe stores 20 bytes per node at +0x8: the person key,
/// temporary 0, temporary 1, request kind, completion status, and age. A request is visible to the primitive only
/// when its status is 2 or 3; polling then removes it. FUN_0046DCC7 ages every node and removes it at 100 ticks.
///
/// The host still owns the asynchronous work that produces completion values (FUN_0046DD08); this class owns the
/// queue's insertion, poll/consume, and expiration rules so those rules are shared and testable.
/// </summary>
public sealed class GenericSimsCallRequestQueue
{
    private const short ExpirationTicks = 100;
    private readonly List<Request> _requests = [];
    private long _nextId;

    /// <summary>Number of requests currently retained by the manager.</summary>
    public int Count => _requests.Count;

    /// <summary>A stable snapshot in original linked-list order for the host's FUN_0046DD08 update pass.</summary>
    public IReadOnlyList<GenericSimsCallPendingRequest> Snapshot() => _requests
        .Select(request => new GenericSimsCallPendingRequest(
            request.Id,
            request.PersonKey,
            request.Temporary0,
            request.Temporary1,
            request.Kind,
            request.Status,
            request.Age))
        .ToArray();

    /// <summary>
    /// Appends a request in list order, as FUN_0046DD96/FUN_0046DDDE do. The person key is cXObject +0xF0;
    /// temporary 1, status, and age begin at zero in the 20-byte record initialized by FUN_0046DC56.
    /// The returned handle lets the asynchronous host complete this particular node even if keys are duplicated.
    /// </summary>
    public long Start(XObject me, short temporary0, GenericSimsCallRequestKind kind)
    {
        var id = checked(++_nextId);
        _requests.Add(new Request(id, me.StackObjectKey, temporary0, kind));
        return id;
    }

    /// <summary>
    /// Stores an asynchronous result on the matching pending node. FUN_0046DD08 writes only terminal status 2 or 3;
    /// it ignores requests whose status is already nonzero.
    /// </summary>
    public bool TryComplete(long requestId, short temporary0, short temporary1, GenericSimsCallRequestStatus status)
    {
        if (status is not (GenericSimsCallRequestStatus.Succeeded or GenericSimsCallRequestStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A request can complete only as succeeded (2) or failed (3).");

        var request = _requests.FirstOrDefault(candidate => candidate.Id == requestId);
        if (request is null || request.Status != 0) return false;

        request.Temporary0 = temporary0;
        request.Temporary1 = temporary1;
        request.Status = (int)status;
        return true;
    }

    /// <summary>
    /// Finds the first matching person key, matching FUN_0046DC72's forward list scan. Pending and absent requests
    /// return null without removing anything. Status 2 or 3 returns the result and consumes that node.
    /// </summary>
    public GenericSimsCallRequestResult? Poll(XObject me)
    {
        var index = _requests.FindIndex(request => request.PersonKey == me.StackObjectKey);
        if (index < 0) return null;

        var request = _requests[index];
        if (request.Status is not (2 or 3)) return null;

        _requests.RemoveAt(index);
        return new GenericSimsCallRequestResult(request.Temporary0, request.Temporary1, request.Status);
    }

    /// <summary>Checks whether there is an active request queued for this person.</summary>
    public bool HasPending(XObject me) => _requests.Any(request => request.PersonKey == me.StackObjectKey);

    /// <summary>
    /// Advances the request manager by one simulation tick. FUN_0046DCC7 increments the signed 16-bit age field on
    /// every node and removes it once the incremented value is no longer below 100.
    /// </summary>
    public void AdvanceTick()
    {
        for (var i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            request.Age = unchecked((short)(request.Age + 1));
            if (request.Age >= ExpirationTicks) _requests.RemoveAt(i);
        }
    }

    private sealed class Request(long id, short personKey, short temporary0, GenericSimsCallRequestKind kind)
    {
        public long Id { get; } = id;
        public short PersonKey { get; } = personKey;
        public short Temporary0 { get; set; } = temporary0;
        public short Temporary1 { get; set; }
        public GenericSimsCallRequestKind Kind { get; } = kind;
        public int Status { get; set; }
        public short Age { get; set; }
    }
}
