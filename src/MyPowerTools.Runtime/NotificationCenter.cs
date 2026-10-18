namespace MyPowerTools.Runtime;

public sealed class NotificationCenter
{
    private readonly HistoryBufferOptions _options;
    private readonly BoundedHistoryBuffer<NotificationRecord> _notifications;
    private readonly object _gate = new();

    public NotificationCenter() : this(null) { }

    public NotificationCenter(HistoryBufferOptions? options)
    {
        _options = options ?? HistoryBufferOptions.NotificationDefaults;
        _options.Validate();
        _notifications = new(_options, preferReadEviction: true);
    }

    public int Count
    {
        get { lock (_gate) return _notifications.Count; }
    }

    public HistoryBufferStats GetStats()
    {
        lock (_gate) return _notifications.GetStats();
    }

    public IReadOnlyList<NotificationRecord> List() => Query();

    public IReadOnlyList<NotificationRecord> Query(string? moduleId = null, string? level = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, bool? isRead = null, int? limit = null)
    {
        lock (_gate)
        {
            return _notifications.Values.Where(item => (string.IsNullOrWhiteSpace(moduleId) || item.ModuleId == moduleId)
                    && (string.IsNullOrWhiteSpace(level) || string.Equals(item.Level, level, StringComparison.OrdinalIgnoreCase))
                    && (!since.HasValue || item.Time >= since.Value)
                    && (!until.HasValue || item.Time < until.Value)
                    && (!isRead.HasValue || item.IsRead == isRead.Value))
                .OrderByDescending(item => item.Time)
                .Take(limit.HasValue ? Math.Max(0, limit.Value) : int.MaxValue).ToArray();
        }
    }

    public NotificationRecord Publish(string moduleId, string level, string title, string body)
    {
        var record = Fit(new NotificationRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            moduleId, level, LogRouter.Redact(title), LogRouter.Redact(body), false));
        lock (_gate)
        {
            if (!record.IsRetained) _notifications.Reject();
            else _notifications.Add(record.Id, record, record.RetainedBytes, record.OriginalBytes, record.Time, unread: true);
        }
        return record;
    }

    public void MarkRead(string id) => SetReadState(id, true);

    public NotificationReadStateUpdate? SetReadState(string id, bool isRead)
    {
        lock (_gate)
        {
            if (!_notifications.TryGet(id, out var current)) return null;
            if (current.IsRead == isRead) return new NotificationReadStateUpdate(current, false);
            var updated = current with { IsRead = isRead };
            _notifications.Update(id, updated, updated.RetainedBytes, updated.OriginalBytes, !isRead, countTruncation: false);
            return new NotificationReadStateUpdate(updated, true);
        }
    }

    private NotificationRecord Fit(NotificationRecord record)
    {
        var identityBytes = HistoryRecordText.RecordOverheadBytes + HistoryRecordText.Bytes(record.Id)
            + HistoryRecordText.Bytes(record.ModuleId);
        var originalBytes = identityBytes + HistoryRecordText.Bytes(record.Level) + HistoryRecordText.Bytes(record.Title)
            + HistoryRecordText.Bytes(record.Body);
        if (identityBytes > _options.MaxRecordBytes)
        {
            return record with { Level = "", Title = "", Body = "", IsRetained = false,
                IsTruncated = originalBytes != identityBytes, OriginalBytes = originalBytes, RetainedBytes = 0 };
        }
        var level = HistoryRecordText.Fit(record.Level, Math.Min(128, _options.MaxRecordBytes - identityBytes));
        var remaining = _options.MaxRecordBytes - identityBytes - HistoryRecordText.Bytes(level);
        var title = HistoryRecordText.Fit(record.Title, Math.Min(2048, remaining));
        var body = HistoryRecordText.Fit(record.Body, remaining - HistoryRecordText.Bytes(title));
        var retainedBytes = identityBytes + HistoryRecordText.Bytes(level) + HistoryRecordText.Bytes(title) + HistoryRecordText.Bytes(body);
        return record with { Level = level, Title = title, Body = body, IsRetained = true,
            IsTruncated = retainedBytes < originalBytes, OriginalBytes = originalBytes, RetainedBytes = retainedBytes };
    }
}

public sealed record NotificationRecord(string Id, DateTimeOffset Time, string ModuleId, string Level, string Title, string Body, bool IsRead)
{
    public bool IsTruncated { get; init; }
    public bool IsRetained { get; init; } = true;
    public long OriginalBytes { get; init; }
    public long RetainedBytes { get; init; }
}

public sealed record NotificationReadStateUpdate(NotificationRecord Notification, bool Changed);
