using System.Text.Json.Nodes;

namespace RemoteToolGateway.Core;

/// <summary>
/// One remote invocation. Records live in memory only: a restarted module must not present a
/// stale "running" invocation as if a phone could still poll it. <see cref="Args"/> is kept so the
/// desktop confirmation surface can hand the original arguments to the existing execution entry
/// point; it is never persisted, logged or returned in a list.
///
/// Every state change goes through this type's own lock, so the desktop claim, the phone cancel and
/// the resolution of a pending confirmation cannot interleave into a torn state. Callers must never
/// hold this lock across an await.
/// </summary>
public sealed class InvocationRecord
{
    private readonly object _sync = new();

    public required string InvocationId { get; init; }
    public required string GrantId { get; init; }
    public required string DeviceName { get; init; }
    public required string CommandId { get; init; }
    public required string CommandTitle { get; init; }
    public required JsonObject Args { get; init; }
    public required string ArgsSummary { get; init; }
    public string State { get; private set; } = ControlStates.Accepted;
    public string Message { get; private set; } = "";
    public bool Terminal { get; private set; }
    public bool RequiresElevation { get; set; }
    public string ConfirmationKind { get; set; } = "";
    public string ConfirmationReason { get; set; } = "";
    public bool ClaimedByDesktop { get; private set; }
    public bool ExecutionStarted { get; set; }
    /// <summary>True only after the runtime accepted a cancel request; it never means "cancelled".</summary>
    public bool CancelRequested { get; private set; }
    /// <summary>Per-invocation lifetime; cancelling it releases the local stream after the cancel grace.</summary>
    public CancellationTokenSource? Cancellation { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public ControlInvocationResult Result { get; private set; } = null!;

    public ControlInvocation ToWire()
    {
        lock (_sync)
        {
            return new ControlInvocation(InvocationId, CommandId, State, Message, Terminal, Result);
        }
    }

    /// <summary>A snapshot of the mutable state; never used to make decisions on its own.</summary>
    public (string State, bool Terminal, bool Claimed, bool CancelRequested) Snapshot()
    {
        lock (_sync) return (State, Terminal, ClaimedByDesktop, CancelRequested);
    }

    /// <summary>
    /// Applies a state change under the record's own lock. A terminal result is final: the first one
    /// wins, so two concurrent resolves (or a late runtime event after a cancellation) can never
    /// flip an invocation that already ended. A "cancelling" record is not terminal, so a real
    /// success reported after a cancel request still lands.
    /// </summary>
    /// <returns><see langword="false"/> when the record had already reached a terminal state.</returns>
    public bool Apply(string state, string message, bool terminal, ControlInvocationResult? result = null)
    {
        lock (_sync)
        {
            if (Terminal) return false;
            State = ControlText.Bound(state, ControlWire.MaxTextLength);
            Message = ControlText.Bound(message, ControlWire.MaxMessageLength);
            Terminal = terminal;
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = result ?? new ControlInvocationResult(
                InvocationId,
                State,
                Message,
                Result?.LogCursor ?? "",
                Result?.ErrorCode ?? "",
                Result?.ErrorMessage ?? "",
                Result?.Retryable ?? false,
                Result?.ErrorDetails);
            return true;
        }
    }

    /// <summary>
    /// The single atomic claim for a pending confirmation. Exactly one caller can win; a second
    /// claim returns false and must never receive the executable arguments again.
    /// </summary>
    public bool TryClaim()
    {
        lock (_sync)
        {
            if (Terminal || ClaimedByDesktop) return false;
            if (!string.Equals(State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal)) return false;
            ClaimedByDesktop = true;
            State = ControlStates.Claimed;
            Message = "电脑端已受理，正在本机执行。";
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = new ControlInvocationResult(InvocationId, State, Message, "", "", "", false, null);
            return true;
        }
    }

    /// <summary>Cancels a pending confirmation that no desktop page has claimed yet.</summary>
    public bool TryCancelPending()
    {
        lock (_sync)
        {
            if (Terminal || ClaimedByDesktop) return false;
            if (!string.Equals(State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal)) return false;
            State = ControlStates.Cancelled;
            Message = "已取消等待电脑确认的调用。";
            Terminal = true;
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = new ControlInvocationResult(InvocationId, State, Message, "", "cancelled",
                "手机取消了尚未确认的调用。", true, null);
            return true;
        }
    }

    /// <summary>Rejects a pending confirmation that no desktop page has claimed yet.</summary>
    public bool TryRejectPending(string message)
    {
        lock (_sync)
        {
            if (Terminal || ClaimedByDesktop) return false;
            if (!string.Equals(State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal)) return false;
            State = ControlStates.Rejected;
            Message = ControlText.Bound(message, ControlWire.MaxMessageLength);
            Terminal = true;
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = new ControlInvocationResult(InvocationId, State, Message, "", "rejected", Message, false, null);
            return true;
        }
    }

    /// <summary>
    /// Records that the runtime accepted a cancel request. This is a non-terminal state: the real
    /// outcome still has to come back from the runtime before anything is called cancelled.
    /// </summary>
    public void MarkCancelAccepted(string message)
    {
        lock (_sync)
        {
            if (Terminal) return;
            CancelRequested = true;
            State = ControlStates.Cancelling;
            Message = ControlText.Bound(message, ControlWire.MaxMessageLength);
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = new ControlInvocationResult(InvocationId, State, Message, Result?.LogCursor ?? "",
                Result?.ErrorCode ?? "", Result?.ErrorMessage ?? "", Result?.Retryable ?? false, Result?.ErrorDetails);
        }
    }

    /// <summary>Fails an invocation that cannot proceed (for example because its grant was revoked).</summary>
    public void FailClosed(string message, string errorCode, bool retryable = false)
    {
        lock (_sync)
        {
            if (Terminal) return;
            State = ControlStates.Failed;
            Message = ControlText.Bound(message, ControlWire.MaxMessageLength);
            Terminal = true;
            UpdatedAt = DateTimeOffset.UtcNow;
            Result = new ControlInvocationResult(InvocationId, State, "", "", errorCode, Message, retryable, null);
        }
    }
}

public sealed class InvocationStore
{
    public const int MaxRecords = 200;

    private readonly object _lock = new();
    private readonly Dictionary<string, InvocationRecord> _byId = new(StringComparer.Ordinal);
    private readonly List<InvocationRecord> _order = [];

    /// <summary>False when the id is already taken; a duplicate id must never execute twice.</summary>
    public bool TryAdd(InvocationRecord record)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(record.InvocationId)) return false;
            _byId[record.InvocationId] = record;
            _order.Add(record);
            Evict();
            return true;
        }
    }

    public bool TryGet(string invocationId, out InvocationRecord record)
    {
        lock (_lock) return _byId.TryGetValue(invocationId, out record!);
    }

    public InvocationRecord? Find(string invocationId)
    {
        lock (_lock) return _byId.GetValueOrDefault(invocationId);
    }

    public IReadOnlyList<InvocationRecord> Recent(int max)
    {
        lock (_lock) return _order.AsEnumerable().Reverse().Take(Math.Max(0, max)).ToArray();
    }

    /// <summary>
    /// Everything the desktop page still has to act on: requests waiting for a confirmation and
    /// requests a page already claimed (they stay visible while the runtime executes them).
    /// </summary>
    public IReadOnlyList<InvocationRecord> PendingConfirmations()
    {
        lock (_lock) return _order
            .Where(item =>
            {
                var snapshot = item.Snapshot();
                return !snapshot.Terminal &&
                       (string.Equals(snapshot.State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal) ||
                        string.Equals(snapshot.State, ControlStates.Claimed, StringComparison.Ordinal));
            })
            .ToArray();
    }

    public IReadOnlyList<InvocationRecord> ActiveForGrant(string grantId)
    {
        lock (_lock) return _order
            .Where(item => !item.Terminal && string.Equals(item.GrantId, grantId, StringComparison.Ordinal))
            .ToArray();
    }

    /// <summary>Oldest terminal records are dropped first; an in-flight invocation is never evicted.</summary>
    private void Evict()
    {
        while (_order.Count > MaxRecords)
        {
            var index = _order.FindIndex(item => item.Snapshot().Terminal);
            if (index < 0) return;
            _byId.Remove(_order[index].InvocationId);
            _order.RemoveAt(index);
        }
    }
}
