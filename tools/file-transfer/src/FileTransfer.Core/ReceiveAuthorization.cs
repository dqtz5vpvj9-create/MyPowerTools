namespace FileTransfer.Core;

/// <summary>
/// One device waiting for the user to decide about a first contact. It carries no credential: the
/// pending list is exactly what the surface may show, and nothing is trusted until the user answers.
/// </summary>
public sealed record ReceiveRequest(string RequestId, string DeviceId, string Name, IReadOnlyList<string> ItemNames,
    string Address, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// The token the caller proposed. It is kept so an accepted, remembered contact can be stored as a
    /// permanent credential, and the surface projection deliberately never contains it.
    /// </summary>
    public string? OfferedToken { get; init; }
}

/// <summary>
/// First-contact approval queue.
///
/// An approval is never keyed on the caller's self-reported device id alone: it is bound to the
/// remote endpoint that asked, the device id it claimed and the exact item ids it wanted to send.
/// A second client that claims the same device id from another endpoint, or that tries to send a
/// different file first, is not covered by the earlier approval and has to ask again.
/// </summary>
public sealed class ReceiveAuthorization(TimeSpan? requestLifetime = null, TimeSpan? grantLifetime = null,
    TimeSpan? denialLifetime = null, Func<DateTimeOffset>? clock = null)
{
    public const int MaxPending = 8;
    private readonly TimeSpan _requestLifetime = requestLifetime ?? TimeSpan.FromMinutes(2);
    private readonly TimeSpan _grantLifetime = grantLifetime ?? TimeSpan.FromMinutes(10);
    private readonly TimeSpan _denialLifetime = denialLifetime ?? TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly List<Waiting> _pending = [];
    private readonly Dictionary<string, Approval> _decisions = new(StringComparer.Ordinal);

    private sealed record Waiting(ReceiveRequest Request, string Key, string Endpoint, TaskCompletionSource<bool> Answer);
    private sealed record Approval(string DeviceId, string Endpoint, DateTimeOffset ExpiresAt, bool Accepted);

    /// <summary>Requests that are still waiting for the user, oldest first, expired ones removed.</summary>
    public IReadOnlyList<ReceiveRequest> Pending()
    {
        lock (_gate)
        {
            Prune();
            return _pending.Select(item => item.Request).ToArray();
        }
    }

    /// <summary>
    /// Records a first contact and returns the request the user has to answer. Repeating the same
    /// request (same endpoint, device and item set) returns the same request id, so a network retry
    /// never invalidates the prompt the user is looking at and never stacks a second one.
    /// </summary>
    public ReceiveRequest Request(string deviceId, string endpoint, string name, IReadOnlyList<string> itemNames,
        string address, string? offeredToken = null, IEnumerable<string>? itemIds = null)
    {
        var key = Key(deviceId, endpoint, itemIds);
        lock (_gate)
        {
            Prune();
            var existing = _pending.FirstOrDefault(item => item.Key == key);
            if (existing is not null) return existing.Request;
            var request = new ReceiveRequest(Guid.NewGuid().ToString("N"), deviceId, name, itemNames,
                address, _clock(), _clock() + _requestLifetime) { OfferedToken = offeredToken };
            _pending.Add(new Waiting(request, key, endpoint,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)));
            while (_pending.Count > MaxPending) _pending.RemoveAt(0);
            return request;
        }
    }

    /// <summary>
    /// Waits for the user's answer on a held connection. The caller keeps the transfer open and starts
    /// the payload as soon as this returns true, so an accepted first contact is delivered immediately
    /// instead of being retried on a later schedule.
    /// </summary>
    public async Task<bool> WaitAsync(string requestId, CancellationToken token)
    {
        Task<bool> decision;
        lock (_gate)
        {
            Prune();
            var pending = _pending.FirstOrDefault(item => item.Request.RequestId == requestId);
            if (pending is null) return false;
            decision = pending.Answer.Task;
        }
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(_requestLifetime);
        try { return await decision.WaitAsync(window.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Applies the user's answer. A rejection leaves no permanent trust; an acceptance becomes a
    /// decision bound to the same endpoint, device and item ids, which is what makes the sender's
    /// retry after a lost answer succeed without asking the user twice.
    /// </summary>
    public bool Respond(string requestId, bool accept, bool remember, out ReceiveRequest? request)
    {
        request = null;
        lock (_gate)
        {
            Prune();
            var pending = _pending.FirstOrDefault(item => item.Request.RequestId == requestId);
            if (pending is null) return false;
            _pending.Remove(pending);
            request = pending.Request;
            _decisions[pending.Key] = new Approval(pending.Request.DeviceId, pending.Endpoint,
                _clock() + (accept ? _grantLifetime : _denialLifetime), accept);
            pending.Answer.TrySetResult(accept);
            return true;
        }
    }

    /// <summary>
    /// True only when the user's earlier acceptance covers this exact endpoint, device and item.
    /// A missing entry, a denial, an expiry, another endpoint or another item all return false.
    /// </summary>
    public bool IsApproved(string deviceId, string endpoint, string itemId)
    {
        lock (_gate)
        {
            Prune();
            return _decisions.TryGetValue(Key(deviceId, endpoint, [itemId]), out var decision) && decision.Accepted;
        }
    }

    /// <summary>The recorded answer for one item, or null when the user was never asked about it.</summary>
    public bool? Decision(string deviceId, string endpoint, string itemId)
    {
        lock (_gate)
        {
            Prune();
            return _decisions.TryGetValue(Key(deviceId, endpoint, [itemId]), out var decision) ? decision.Accepted : null;
        }
    }

    public void ForgetItemApproval(string itemId)
    {
        lock (_gate)
        {
            foreach (var key in _decisions.Keys.Where(key => key.EndsWith("|" + itemId, StringComparison.Ordinal)).ToArray())
                _decisions.Remove(key);
        }
    }

    public void Forget(string deviceId)
    {
        lock (_gate)
        {
            _pending.RemoveAll(item => item.Request.DeviceId == deviceId);
            foreach (var key in _decisions.Where(pair => pair.Value.DeviceId == deviceId).Select(pair => pair.Key).ToArray())
                _decisions.Remove(key);
        }
    }

    private static string Key(string deviceId, string endpoint, IEnumerable<string>? itemIds) =>
        endpoint + "|" + deviceId + "|" + string.Join(",", (itemIds ?? []).OrderBy(id => id, StringComparer.Ordinal));

    private void Prune()
    {
        var now = _clock();
        foreach (var expired in _pending.Where(item => item.Request.ExpiresAt <= now).ToArray())
        {
            _pending.Remove(expired);
            expired.Answer.TrySetResult(false);
        }
        foreach (var expired in _decisions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _decisions.Remove(expired);
    }
}
