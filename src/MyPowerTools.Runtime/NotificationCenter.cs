namespace MyPowerTools.Runtime;

public sealed class NotificationCenter
{
    private readonly List<NotificationRecord> _notifications = [];
    private readonly object _gate = new();

    public IReadOnlyList<NotificationRecord> List()
    {
        lock (_gate)
        {
            return _notifications.OrderByDescending(item => item.Time).ToArray();
        }
    }

    public NotificationRecord Publish(string moduleId, string level, string title, string body)
    {
        var record = new NotificationRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, level, title, LogRouter.Redact(body), false);
        lock (_gate)
        {
            _notifications.Add(record);
        }

        return record;
    }

    public void MarkRead(string id)
    {
        SetReadState(id, true);
    }

    public NotificationReadStateUpdate? SetReadState(string id, bool isRead)
    {
        lock (_gate)
        {
            var index = _notifications.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return null;
            }

            var current = _notifications[index];
            if (current.IsRead == isRead)
            {
                return new NotificationReadStateUpdate(current, false);
            }

            var updated = current with { IsRead = isRead };
            _notifications[index] = updated;
            return new NotificationReadStateUpdate(updated, true);
        }
    }
}

public sealed record NotificationRecord(string Id, DateTimeOffset Time, string ModuleId, string Level, string Title, string Body, bool IsRead);

public sealed record NotificationReadStateUpdate(NotificationRecord Notification, bool Changed);
