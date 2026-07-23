using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class InvocationExecutionCache
{
    public const int DefaultMaxCount = 1000;
    public static readonly TimeSpan DefaultCompletedTtl = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, InvocationExecutionEntry> _entries;
    private readonly object _gate = new();
    private readonly int _maxCount;
    private readonly TimeSpan _completedTtl;
    private readonly Func<DateTimeOffset> _utcNow;

    public InvocationExecutionCache(int maxCount = DefaultMaxCount, TimeSpan? completedTtl = null, Func<DateTimeOffset>? utcNow = null)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "maxCount must be positive.");
        }

        _maxCount = maxCount;
        _completedTtl = completedTtl ?? DefaultCompletedTtl;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _entries = new Dictionary<string, InvocationExecutionEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Task<Sdk.CommandExecutionResult> GetOrAdd(string invocationId, Func<Task<Sdk.CommandExecutionResult>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(factory);

        Cleanup();

        InvocationExecutionEntry entry;
        Task<Sdk.CommandExecutionResult> waiter;
        lock (_gate)
        {
            if (_entries.TryGetValue(invocationId, out entry!))
            {
                return entry.Task;
            }

            entry = new InvocationExecutionEntry(_utcNow());
            _entries[invocationId] = entry;
            waiter = entry.Task;
        }

        StartExecution(invocationId, entry, factory);
        return waiter;
    }

    public bool IsCompleted(string invocationId)
    {
        if (string.IsNullOrWhiteSpace(invocationId))
        {
            return false;
        }

        Cleanup();
        lock (_gate)
        {
            return _entries.TryGetValue(invocationId, out var entry) && entry.IsCompleted;
        }
    }

    public void Cleanup()
    {
        var now = _utcNow();
        lock (_gate)
        {
            CleanupCore(now);
        }
    }

    private void StartExecution(
        string invocationId,
        InvocationExecutionEntry entry,
        Func<Task<Sdk.CommandExecutionResult>> factory)
    {
        Task<Sdk.CommandExecutionResult> execution;
        try
        {
            execution = factory() ?? throw new InvalidOperationException("Invocation execution factory returned null.");
        }
        catch (Exception failure)
        {
            InvocationExecutionOutcome.Failed(this, invocationId, entry, DetachFailure(failure)).Publish();
            return;
        }

        var bridge = new InvocationExecutionBridge(this, invocationId, entry);
        var detached = execution.ContinueWith(
            static (task, state) => ((InvocationExecutionBridge)state!).Detach(task),
            bridge,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = detached.ContinueWith(
            static task => task.GetAwaiter().GetResult().Publish(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Sdk.CommandExecutionResult DetachResult(Sdk.CommandExecutionResult result)
    {
        var error = result.Error is null
            ? null
            : new Sdk.MptRuntimeError(
                result.Error.Code,
                result.Error.Message,
                result.Error.Retryable,
                result.Error.Details?.DeepClone() as System.Text.Json.Nodes.JsonObject);
        return new Sdk.CommandExecutionResult(
            result.InvocationId,
            result.CommandId,
            result.State,
            result.Success,
            result.Output,
            error);
    }

    private static Exception DetachFailure(Exception failure)
    {
        var message = LogRouter.Redact(failure.Message);
        return failure switch
        {
            OperationCanceledException cancelled => new OperationCanceledException(message, cancelled.CancellationToken),
            ArgumentException argument => new ArgumentException(message, argument.ParamName),
            InvalidOperationException => new InvalidOperationException(message),
            TimeoutException => new TimeoutException(message),
            _ => new InvalidOperationException($"Invocation execution failed: {message}")
        };
    }

    private void CleanupCore(DateTimeOffset now)
    {
        foreach (var stale in _entries
                     .Where(pair => pair.Value.IsCompleted && pair.Value.CompletedAt + _completedTtl <= now)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(stale);
        }

        if (_entries.Count <= _maxCount)
        {
            return;
        }

        foreach (var overflow in _entries
                     .Where(pair => pair.Value.IsCompleted)
                     .OrderBy(pair => pair.Value.CompletedAt)
                     .ThenBy(pair => pair.Value.CreatedAt)
                     .Take(Math.Max(0, _entries.Count - _maxCount))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(overflow);
        }
    }

    private sealed class InvocationExecutionEntry
    {
        public InvocationExecutionEntry(DateTimeOffset createdAt)
        {
            CreatedAt = createdAt;
            CompletedAt = DateTimeOffset.MaxValue;
            Completion = new TaskCompletionSource<Sdk.CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task = Completion.Task;
        }

        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset CompletedAt { get; private set; }
        public bool IsCompleted { get; private set; }
        public TaskCompletionSource<Sdk.CommandExecutionResult>? Completion { get; private set; }
        public Task<Sdk.CommandExecutionResult> Task { get; }

        public void MarkCompleted(DateTimeOffset completedAt)
        {
            CompletedAt = completedAt;
            IsCompleted = true;
            // Task stays Completion.Task: every waiter (including callers that
            // arrived before completion) observes the same Task instance, and
            // Publish completes the completion source with the detached result.
            // Swapping Task here made concurrent callers receive different Task
            // instances for the same invocation, breaking dedup identity.
        }

        public TaskCompletionSource<Sdk.CommandExecutionResult>? DetachCompletion()
        {
            var completion = Completion;
            Completion = null;
            return completion;
        }
    }

    private sealed class InvocationExecutionBridge
    {
        private readonly InvocationExecutionCache _owner;
        private readonly string _invocationId;
        private readonly InvocationExecutionEntry _entry;

        public InvocationExecutionBridge(InvocationExecutionCache owner, string invocationId, InvocationExecutionEntry entry)
        {
            _owner = owner;
            _invocationId = invocationId;
            _entry = entry;
        }

        public InvocationExecutionOutcome Detach(Task<Sdk.CommandExecutionResult> execution)
        {
            try
            {
                return InvocationExecutionOutcome.Succeeded(
                    _owner,
                    _invocationId,
                    _entry,
                    DetachResult(execution.GetAwaiter().GetResult()));
            }
            catch (OperationCanceledException cancelled)
            {
                return InvocationExecutionOutcome.Cancelled(
                    _owner,
                    _invocationId,
                    _entry,
                    cancelled.CancellationToken);
            }
            catch (Exception failure)
            {
                return InvocationExecutionOutcome.Failed(
                    _owner,
                    _invocationId,
                    _entry,
                    DetachFailure(failure));
            }
            finally
            {
                _ = execution.Exception;
            }
        }
    }

    private sealed class InvocationExecutionOutcome
    {
        private readonly InvocationExecutionCache _owner;
        private readonly string _invocationId;
        private readonly InvocationExecutionEntry _entry;
        private readonly Sdk.CommandExecutionResult? _result;
        private readonly Exception? _failure;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _cancelled;

        private InvocationExecutionOutcome(
            InvocationExecutionCache owner,
            string invocationId,
            InvocationExecutionEntry entry,
            Sdk.CommandExecutionResult? result,
            Exception? failure,
            CancellationToken cancellationToken,
            bool cancelled)
        {
            _owner = owner;
            _invocationId = invocationId;
            _entry = entry;
            _result = result;
            _failure = failure;
            _cancellationToken = cancellationToken;
            _cancelled = cancelled;
        }

        public static InvocationExecutionOutcome Succeeded(
            InvocationExecutionCache owner,
            string invocationId,
            InvocationExecutionEntry entry,
            Sdk.CommandExecutionResult result) =>
            new(owner, invocationId, entry, result, null, CancellationToken.None, false);

        public static InvocationExecutionOutcome Failed(
            InvocationExecutionCache owner,
            string invocationId,
            InvocationExecutionEntry entry,
            Exception failure) =>
            new(owner, invocationId, entry, null, failure, CancellationToken.None, false);

        public static InvocationExecutionOutcome Cancelled(
            InvocationExecutionCache owner,
            string invocationId,
            InvocationExecutionEntry entry,
            CancellationToken cancellationToken) =>
            new(owner, invocationId, entry, null, null, cancellationToken, true);

        public void Publish()
        {
            if (_result is not null)
            {
                TaskCompletionSource<Sdk.CommandExecutionResult>? completion;
                lock (_owner._gate)
                {
                    if (ReferenceEquals(_owner._entries.GetValueOrDefault(_invocationId), _entry))
                    {
                        _entry.MarkCompleted(_owner._utcNow());
                        _owner.CleanupCore(_owner._utcNow());
                    }

                    completion = _entry.DetachCompletion();
                }

                completion?.TrySetResult(_result);
                return;
            }

            TaskCompletionSource<Sdk.CommandExecutionResult>? failedCompletion;
            lock (_owner._gate)
            {
                if (ReferenceEquals(_owner._entries.GetValueOrDefault(_invocationId), _entry))
                {
                    _owner._entries.Remove(_invocationId);
                }

                failedCompletion = _entry.DetachCompletion();
            }

            if (_cancelled)
            {
                failedCompletion?.TrySetCanceled(_cancellationToken);
            }
            else
            {
                failedCompletion?.TrySetException(_failure ?? new InvalidOperationException("Invocation execution failed."));
            }
        }
    }
}
