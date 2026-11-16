namespace SimsEngine.Simulation;

public enum GenericSimsCallRequestKind
{
    First = 0,
    Second = 1,
}

public enum GenericSimsCallRequestStatus
{
    Succeeded = 2,
    Failed = 3,
}

public readonly record struct GenericSimsCallPendingRequest(
    long Id,
    short PersonKey,
    short Temporary0,
    short Temporary1,
    GenericSimsCallRequestKind Kind,
    int Status,
    short Age);

/// <summary>Manages asynchronous simulation requests queued by global sim calls 27 and 28. Requests become
/// readable upon reaching completed status (2 or 3) and expire automatically after 100 ticks if unconsumed.
/// </summary>
public sealed class GenericSimsCallRequestQueue
{
    private const short ExpirationTicks = 100;
    private readonly List<Request> _requests = [];
    private long _nextId;

    public int Count => _requests.Count;

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

    public long Start(XObject me, short temporary0, GenericSimsCallRequestKind kind)
    {
        var id = checked(++_nextId);
        _requests.Add(new Request(id, me.StackObjectKey, temporary0, kind));
        return id;
    }

    /// <summary>Records an asynchronous completion result on a pending request, ignoring requests that have
    /// already transitioned to a terminal status.</summary>
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

    /// <summary>Polls for the first completed request matching the caller, consuming and returning the
    /// result only when in a terminal status.</summary>
    public GenericSimsCallRequestResult? Poll(XObject me)
    {
        var index = _requests.FindIndex(request => request.PersonKey == me.StackObjectKey);
        if (index < 0) return null;

        var request = _requests[index];
        if (request.Status is not (2 or 3)) return null;

        _requests.RemoveAt(index);
        return new GenericSimsCallRequestResult(request.Temporary0, request.Temporary1, request.Status);
    }

    public bool HasPending(XObject me) => _requests.Any(request => request.PersonKey == me.StackObjectKey);

    /// <summary>Advances pending requests by one simulation tick, removing any unconsumed requests that
    /// reach 100 ticks of age.</summary>
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
